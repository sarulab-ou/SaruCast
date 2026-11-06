param([string]$Zip = 'artifacts/SaruCast-win-x64.zip', [string]$PackagingRevision = 'working-tree', [string]$CertificateThumbprint)
$ErrorActionPreference='Stop'
$env:PYTHONUTF8='1'; $env:PYTHONIOENCODING='utf-8'; $env:CI='true'
Set-Location (Split-Path $PSScriptRoot -Parent)
$repo=(Get-Location).Path
if($repo.Contains("'")){throw 'Repository path must not contain apostrophes for WSL build.'}
$linuxRoot='/mnt/'+$repo.Substring(0,1).ToLowerInvariant()+$repo.Substring(2).Replace('\','/')
$deps=Get-Content dependencies.json -Raw | ConvertFrom-Json
New-Item -ItemType Directory -Force .tools/nsis-packages, .tools/nsis-root, artifacts/installer | Out-Null
$buildLock=[IO.File]::Open((Join-Path $repo '.tools/installer-build.lock'),[IO.FileMode]::OpenOrCreate,[IO.FileAccess]::ReadWrite,[IO.FileShare]::None)
try {
foreach($package in $deps.installer.packages){
    $cached=Join-Path $repo ('.tools/nsis-packages/'+$package.file)
    if(!(Test-Path -LiteralPath $cached)){Invoke-WebRequest -Uri $package.url -OutFile $cached}
    if((Get-FileHash -LiteralPath $cached -Algorithm SHA256).Hash.ToLowerInvariant() -ne $package.sha256){throw "NSIS package hash mismatch: $($package.file)"}
    wsl bash -lc "cd '$linuxRoot' && dpkg-deb -x '.tools/nsis-packages/$($package.file)' '.tools/nsis-root'"
    if($LASTEXITCODE -ne 0){throw 'NSIS extraction failed.'}
}
$zipPath=[IO.Path]::GetFullPath((Join-Path $repo $Zip))
$checksum=(Get-Content -LiteralPath ($zipPath+'.sha256') -Raw).Trim()
if($checksum -notmatch '^([0-9a-fA-F]{64})  '){throw 'Invalid ZIP checksum file.'}
$zipHash=(Get-FileHash -LiteralPath $zipPath -Algorithm SHA256).Hash.ToLowerInvariant()
if($zipHash -ne $Matches[1].ToLowerInvariant()){throw 'ZIP hash mismatch.'}
$payload=Join-Path $repo 'artifacts/installer/payload'
# This is the generated payload below this repository's artifacts directory only.
$allowed=[IO.Path]::GetFullPath((Join-Path $repo 'artifacts/installer'))+[IO.Path]::DirectorySeparatorChar
if(!$payload.StartsWith($allowed,[StringComparison]::OrdinalIgnoreCase)){throw 'Unsafe payload path.'}
if(Test-Path -LiteralPath $payload){Remove-Item -LiteralPath $payload -Recurse -Force}
Expand-Archive -LiteralPath $zipPath -DestinationPath $payload
foreach($line in Get-Content -LiteralPath "$payload/SHA256SUMS.txt"){
    if($line -notmatch '^([0-9a-f]{64})  (.+)$'){throw 'Invalid payload hash manifest.'}
    $expected=$Matches[1]; $file=[IO.Path]::GetFullPath((Join-Path $payload $Matches[2]))
    if(!$file.StartsWith($payload+[IO.Path]::DirectorySeparatorChar,[StringComparison]::OrdinalIgnoreCase)){throw 'Unsafe manifest path.'}
    if((Get-FileHash -LiteralPath $file -Algorithm SHA256).Hash.ToLowerInvariant() -ne $expected){throw 'Payload file hash mismatch.'}
}
$version=(Get-Item -LiteralPath "$payload/SaruCast.exe").VersionInfo.FileVersion
if($version -notmatch '^(\d+\.\d+\.\d+)\.0$'){throw 'Unexpected application version.'}
$version=$Matches[1]
Copy-Item "$payload/SHA256SUMS.txt" "$payload/ZIP-SHA256SUMS.txt"
Copy-Item README.md, dependencies.json $payload
Copy-Item docs/build.md, docs/design.md, docs/verification.md "$payload/docs"
Copy-Item .tools/nsis-root/usr/share/doc/nsis-common/copyright "$payload/licenses/NSIS-COPYRIGHT.txt"
"SaruCast installer payload`nApp version: $version`nSource ZIP SHA-256: $zipHash`nPackaging revision: $PackagingRevision`nNSIS Ubuntu package: $($deps.installer.version)`n" | Set-Content "$payload/Installer-Info.txt" -Encoding utf8
$manifest=Get-ChildItem -LiteralPath $payload -Recurse -File | Where-Object {$_.FullName -ne (Join-Path $payload 'SHA256SUMS.txt')} | Sort-Object FullName | ForEach-Object {
    (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant()+'  '+$_.FullName.Substring($payload.Length+1).Replace('\','/')
}
$manifest | Set-Content "$payload/SHA256SUMS.txt" -Encoding utf8
$deletes=@('; Generated from the exact payload; never recursively delete the install directory.')
foreach($file in Get-ChildItem -LiteralPath $payload -Recurse -File){
    $relative=$file.FullName.Substring($payload.Length+1)
    if($relative -match '[\$"\r\n]'){throw 'Unsupported payload filename.'}
    $deletes += 'Delete "$INSTDIR\'+$relative+'"'
}
foreach($dir in Get-ChildItem -LiteralPath $payload -Recurse -Directory | Sort-Object {$_.FullName.Length} -Descending){
    $deletes += 'RMDir "$INSTDIR\'+$dir.FullName.Substring($payload.Length+1)+'"'
}
$deletes | Set-Content artifacts/installer/uninstall-files.nsh -Encoding utf8
wsl bash -lc "cd '$linuxRoot' && NSISDIR='$linuxRoot/.tools/nsis-root/usr/share/nsis' '.tools/nsis-root/usr/bin/makensis' -V2 '-DPRODUCT_VERSION=$version' '-DPAYLOAD_DIR=$linuxRoot/artifacts/installer/payload' '-DOUTPUT_FILE=$linuxRoot/artifacts/SaruCast-Setup-win-x64.exe' '-DUNINSTALL_FILES=$linuxRoot/artifacts/installer/uninstall-files.nsh' installer/SaruCast.nsi"
if($LASTEXITCODE -ne 0){throw 'Installer compilation failed.'}
$output=Join-Path $repo 'artifacts/SaruCast-Setup-win-x64.exe'
if($CertificateThumbprint){
    $certificate=Get-Item -LiteralPath "Cert:/CurrentUser/My/$CertificateThumbprint"
    $signature=Set-AuthenticodeSignature -FilePath $output -Certificate $certificate -HashAlgorithm SHA256 -TimestampServer 'http://timestamp.digicert.com'
    if($signature.Status -ne 'Valid'){throw 'Installer signing failed.'}
}
(Get-FileHash -LiteralPath $output -Algorithm SHA256).Hash.ToLowerInvariant()+'  SaruCast-Setup-win-x64.exe' | Set-Content ($output+'.sha256') -Encoding ascii
Write-Output "Installer: $output"
} finally { $buildLock.Dispose() }
