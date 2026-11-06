param([string]$Zip = 'artifacts/SaruCast-win-x64.zip', [string]$DestinationDirectory = 'artifacts/package-verification')
$ErrorActionPreference = 'Stop'
$env:PYTHONUTF8='1'; $env:PYTHONIOENCODING='utf-8'; $env:CI='true'
Set-Location (Split-Path $PSScriptRoot -Parent)
$repo = (Get-Location).Path
$target = [IO.Path]::GetFullPath((Join-Path $repo $DestinationDirectory))
$artifacts = [IO.Path]::GetFullPath((Join-Path $repo 'artifacts'))
if (-not $target.StartsWith($artifacts + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) { throw 'Verification output must be below artifacts.' }
if (Test-Path -LiteralPath $target) { throw 'Choose a new, empty verification directory.' }
Expand-Archive -LiteralPath $Zip -DestinationPath $target
$checked = 0
foreach ($line in Get-Content -LiteralPath (Join-Path $target 'SHA256SUMS.txt')) {
    if ($line -notmatch '^([0-9a-f]{64})  (.+)$') { throw 'Invalid package manifest.' }
    $expected = $Matches[1]; $relative = $Matches[2]
    $file = [IO.Path]::GetFullPath((Join-Path $target $relative))
    if (-not $file.StartsWith($target + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) { throw 'Invalid manifest path.' }
    if ((Get-FileHash -LiteralPath $file -Algorithm SHA256).Hash.ToLowerInvariant() -ne $expected) { throw "Package hash mismatch: $relative" }
    $checked++
}
$names = @('PATH','DOTNET_ROOT','DOTNET_ROOT_X64','DOTNET_MULTILEVEL_LOOKUP','SARUCAST_DATA_ROOT')
$saved = @{}; foreach ($name in $names) { $saved[$name] = [Environment]::GetEnvironmentVariable($name, 'Process') }
try {
    $env:PATH = "$env:WINDIR\System32;$env:WINDIR"
    $env:DOTNET_ROOT = Join-Path $target 'no-external-runtime'
    $env:DOTNET_ROOT_X64 = $env:DOTNET_ROOT
    $env:DOTNET_MULTILEVEL_LOOKUP = '0'
    $env:SARUCAST_DATA_ROOT = Join-Path $target 'isolated-user-data'
    $exe = Join-Path $target 'SaruCast.exe'
    $output = Join-Path $target 'startup-verification.json'
    $process = Start-Process -FilePath $exe -ArgumentList @('--diagnose', ('"' + $output + '"')) -WindowStyle Hidden -Wait -PassThru
    if ($process.ExitCode -ne 0) { throw 'Packaged startup failed.' }
    $startup = Get-Content -LiteralPath $output -Raw | ConvertFrom-Json
    if (-not $startup.ffmpegBundled -or $startup.captureStarted -or $startup.defaultFrameRate -ne 15 -or $startup.transport -ne 'Cast Streaming') { throw 'Unexpected packaged startup state.' }
    $results = @()
    foreach ($hardware in @(1,0)) {
        $video = Join-Path $target "encoder-$hardware.h264"
        & (Join-Path $target 'tools/ffmpeg.exe') -hide_banner -loglevel error -re -f lavfi -i 'testsrc2=size=640x360:rate=15' -an -vf format=nv12 -c:v h264_mf -hw_encoding $hardware -profile:v 66 -scenario display_remoting -flags low_delay -b:v 1000k -g 15 -bf 0 -r 15 -t 4 -bsf:v h264_metadata=aud=insert -f h264 -y $video
        if ($LASTEXITCODE -ne 0) { throw "Packaged H.264 encoder failed: hardware=$hardware" }
        $probe = & (Join-Path $target 'tools/ffprobe.exe') -v error -f h264 -count_frames -show_entries stream=codec_name,profile,level,nb_read_frames -of json $video | Out-String | ConvertFrom-Json
        if ($LASTEXITCODE -ne 0 -or $probe.streams[0].codec_name -ne 'h264' -or [int]$probe.streams[0].nb_read_frames -lt 55) { throw 'Packaged H.264 decode/count failed.' }
        $lumaFile = Join-Path $target "luma-$hardware.txt"
        # FFmpeg filter options use ':' as a separator, so use a relative, forward-slash path.
        $lumaRelative = $lumaFile.Substring($repo.Length + 1).Replace('\','/')
        & (Join-Path $target 'tools/ffmpeg.exe') -hide_banner -loglevel error -f h264 -i $video -vf "signalstats,metadata=print:key=lavfi.signalstats.YAVG:file='$lumaRelative'" -f null NUL
        if ($LASTEXITCODE -ne 0) { throw 'Packaged decoded image inspection failed.' }
        $luma = @(Get-Content -LiteralPath $lumaFile | Where-Object { $_ -like 'lavfi.signalstats.YAVG=*' } | ForEach-Object { [double]::Parse(($_ -split '=')[1], [Globalization.CultureInfo]::InvariantCulture) })
        $minimum = ($luma | Measure-Object -Minimum).Minimum
        if ($luma.Count -lt 55 -or $minimum -le 20) { throw 'Test pattern decoded as black or incomplete frames.' }
        $results += @{ hardware=$hardware; decodedFrames=[int]$probe.streams[0].nb_read_frames; profile=$probe.streams[0].profile; level=$probe.streams[0].level; minimumMeanLuma=$minimum }
    }
    $result = @{ checkedFiles=$checked; version=(Get-Item -LiteralPath $exe).VersionInfo.ProductVersion; startup=$startup; encoderChecks=$results; cleanMachineVerified=$false }
    $result | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath (Join-Path $target 'package-verification.json') -Encoding utf8
    $result | ConvertTo-Json -Depth 8
}
finally { foreach ($name in $names) { [Environment]::SetEnvironmentVariable($name, $saved[$name], 'Process') } }
