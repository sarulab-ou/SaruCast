param([switch]$RebuildFfmpeg, [string]$CertificateThumbprint, [string]$PublishDirectory = 'artifacts/publish')
$ErrorActionPreference = 'Stop'
$env:PYTHONUTF8 = '1'
$env:PYTHONIOENCODING = 'utf-8'
$env:CI = 'true'
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
$env:DOTNET_NOLOGO = '1'
Set-Location (Split-Path $PSScriptRoot -Parent)
& "$PSScriptRoot/bootstrap.ps1"
$repo = (Get-Location).Path
if ($RebuildFfmpeg -or -not (Test-Path .tools/ffmpeg/ffmpeg.exe)) {
    $linuxPath = '/mnt/' + $repo.Substring(0,1).ToLowerInvariant() + $repo.Substring(2).Replace('\','/')
    # A path containing apostrophes requires additional Bash quoting; fail explicitly.
    if ($linuxPath.Contains("'")) { throw 'Repository path must not contain apostrophes for WSL build.' }
    wsl bash -lc "cd '$linuxPath' && bash scripts/build-ffmpeg.sh"
    if ($LASTEXITCODE -ne 0) { throw 'FFmpeg build failed' }
}
$dotnet = Join-Path $repo '.tools/dotnet/dotnet.exe'
& $dotnet restore src/SaruCast/SaruCast.csproj --locked-mode
if ($LASTEXITCODE -ne 0) { throw 'Restore failed' }
& $dotnet restore tests/SaruCast.Tests/SaruCast.Tests.csproj --locked-mode
if ($LASTEXITCODE -ne 0) { throw 'Test restore failed' }
& $dotnet run --project tests/SaruCast.Tests/SaruCast.Tests.csproj -c Release --no-restore
if ($LASTEXITCODE -ne 0) { throw 'Tests failed' }
New-Item -ItemType Directory -Force artifacts | Out-Null
if ([IO.Path]::IsPathRooted($PublishDirectory)) { throw 'PublishDirectory must be relative to this repository.' }
$output = [IO.Path]::GetFullPath((Join-Path $repo $PublishDirectory))
# Only replace generated output below artifacts; never delete source or the artifacts root.
$resolvedOutput = $output
$artifactsRoot = [IO.Path]::GetFullPath((Join-Path $repo 'artifacts'))
if (-not $resolvedOutput.StartsWith($artifactsRoot + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) { throw 'Unsafe publish path: choose a directory below artifacts.' }
if (Test-Path -LiteralPath $resolvedOutput) { Remove-Item -LiteralPath $resolvedOutput -Recurse -Force }
& $dotnet publish src/SaruCast/SaruCast.csproj -c Release -r win-x64 --self-contained true --no-restore -p:PublishSingleFile=false -p:DebugType=None -p:DebugSymbols=false -o $output
if ($LASTEXITCODE -ne 0) { throw 'Publish failed' }
New-Item -ItemType Directory -Force "$output/tools", "$output/licenses", "$output/sources", "$output/sources/ffmpeg-build", "$output/docs" | Out-Null
Copy-Item .tools/ffmpeg/*.exe "$output/tools"
Copy-Item licenses/*.txt "$output/licenses"
Copy-Item LICENSE "$output/licenses/SaruCast-LICENSE.txt"
Copy-Item .tools/ffmpeg/COPYING.LGPLv2.1 "$output/licenses/FFmpeg-LGPL-2.1.txt"
Copy-Item .tools/ffmpeg/COPYING.LGPLv3 "$output/licenses/FFmpeg-LGPL-3.txt"
Copy-Item .tools/ffmpeg/LICENSE.md "$output/licenses/FFmpeg-LICENSE.txt"
$nuget = & $dotnet nuget locals global-packages --list
$cache = ($nuget -split ': ',2)[1].Trim()
Copy-Item "$cache/microsoft.netcore.app.runtime.win-x64/10.0.14/LICENSE.TXT" "$output/licenses/dotnet-LICENSE.txt"
Copy-Item "$cache/microsoft.netcore.app.runtime.win-x64/10.0.14/THIRD-PARTY-NOTICES.TXT" "$output/licenses/dotnet-THIRD-PARTY-NOTICES.txt"
Copy-Item "$cache/microsoft.windowsdesktop.app.runtime.win-x64/10.0.14/LICENSE" "$output/licenses/WPF-LICENSE.txt"
Copy-Item .tools/dotnet/ThirdPartyNotices.txt "$output/licenses/Microsoft-SDK-NOTICES.txt"
Copy-Item .tools/sources/ffmpeg-9.0.2.tar.xz "$output/sources"
Copy-Item scripts/build-ffmpeg.sh "$output/sources"
Copy-Item artifacts/ffmpeg-build/* "$output/sources/ffmpeg-build"
Copy-Item README.md $output
Copy-Item docs/verification.md, docs/design.md, docs/build.md "$output/docs"
Copy-Item dependencies.json $output
if ($CertificateThumbprint) {
    $certificate = Get-Item -LiteralPath "Cert:/CurrentUser/My/$CertificateThumbprint"
    $signature = Set-AuthenticodeSignature -FilePath "$output/SaruCast.exe" -Certificate $certificate -HashAlgorithm SHA256 -TimestampServer 'http://timestamp.digicert.com'
    if ($signature.Status -ne 'Valid') { throw "Signing failed: $($signature.Status)" }
}
$manifest = Get-ChildItem -LiteralPath $output -Recurse -File | Sort-Object FullName | ForEach-Object {
    $relative = $_.FullName.Substring($resolvedOutput.Length + 1).Replace('\','/')
    (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant() + '  ' + $relative
}
$manifest | Set-Content "$output/SHA256SUMS.txt" -Encoding utf8
Compress-Archive -Path "$output/*" -DestinationPath artifacts/SaruCast-win-x64.zip -Force
(Get-FileHash artifacts/SaruCast-win-x64.zip -Algorithm SHA256).Hash.ToLowerInvariant() + '  SaruCast-win-x64.zip' | Set-Content artifacts/SaruCast-win-x64.zip.sha256 -Encoding ascii
Write-Output "ZIP: $repo/artifacts/SaruCast-win-x64.zip"
