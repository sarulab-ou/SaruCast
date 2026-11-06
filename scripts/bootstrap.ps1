$ErrorActionPreference = 'Stop'
$env:PYTHONUTF8 = '1'
$env:PYTHONIOENCODING = 'utf-8'
$env:CI = 'true'
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
$env:DOTNET_NOLOGO = '1'
Set-Location (Split-Path $PSScriptRoot -Parent)
$deps = Get-Content dependencies.json -Raw | ConvertFrom-Json
New-Item -ItemType Directory -Force .tools/sources | Out-Null
function Fetch-Verified($Url, $Destination, $Algorithm, $Expected) {
    if (-not (Test-Path -LiteralPath $Destination)) { Invoke-WebRequest $Url -OutFile $Destination }
    if ((Get-FileHash -LiteralPath $Destination -Algorithm $Algorithm).Hash.ToLowerInvariant() -ne $Expected) {
        throw "Hash mismatch: $Destination. Remove this cached file and retry."
    }
}
if (-not (Test-Path .tools/dotnet/dotnet.exe)) {
    Fetch-Verified $deps.sdk.url .tools/sdk.zip SHA512 $deps.sdk.sha512
    Expand-Archive .tools/sdk.zip .tools/dotnet -Force
}
$version = & ./.tools/dotnet/dotnet.exe --version
if ($LASTEXITCODE -ne 0 -or $version -ne $deps.sdk.version) { throw 'Pinned .NET SDK mismatch' }
Fetch-Verified $deps.ffmpeg.url .tools/sources/ffmpeg-9.0.2.tar.xz SHA256 $deps.ffmpeg.sha256
Write-Output 'Build prerequisites downloaded and verified. No system installation performed.'
