param([string]$Installer='artifacts/SaruCast-Setup-win-x64.exe')
$ErrorActionPreference='Stop'
$env:PYTHONUTF8='1'; $env:PYTHONIOENCODING='utf-8'; $env:CI='true'
Set-Location (Split-Path $PSScriptRoot -Parent)
$repo=(Get-Location).Path
$installerPath=[IO.Path]::GetFullPath((Join-Path $repo $Installer))
$testRoot=Join-Path $repo 'artifacts/installer-verification'
$installDir=Join-Path $testRoot 'インストール 試験 #100% &'
$menu=Join-Path ([Environment]::GetFolderPath('Programs')) 'SaruCast'
$desktop=Join-Path ([Environment]::GetFolderPath('Desktop')) 'SaruCast.lnk'
$reg='Software\Microsoft\Windows\CurrentVersion\Uninstall\SaruCast'
function Assert([bool]$ok,[string]$message){if(!$ok){throw $message}}
function RegistryValue([string]$path,[string]$name){
    $key=[Microsoft.Win32.Registry]::CurrentUser.OpenSubKey($path)
    try{if($key){return $key.GetValue($name)}}finally{if($key){$key.Dispose()}}
}
function RunProcess([string]$file,[string]$arguments,[bool]$isolated=$false){
    $info=New-Object Diagnostics.ProcessStartInfo
    $info.FileName=$file; $info.Arguments=$arguments; $info.UseShellExecute=$false; $info.CreateNoWindow=$true
    if($isolated){
        $info.EnvironmentVariables['SARUCAST_DATA_ROOT']=Join-Path $testRoot 'isolated-user-data'
        $info.EnvironmentVariables['DOTNET_ROOT']=Join-Path $testRoot 'no-runtime'
        $info.EnvironmentVariables['DOTNET_ROOT_X64']=Join-Path $testRoot 'no-runtime'
        $info.EnvironmentVariables['PATH']="$env:WINDIR\System32;$env:WINDIR"
    }
    $p=[Diagnostics.Process]::Start($info)
    if(!$p.WaitForExit(60000)){throw 'Installer verification process timed out.'}
    $code=$p.ExitCode; $p.Dispose(); return $code
}
function Setup([string]$dir){return RunProcess $installerPath ('/S /D='+$dir)}
function Uninstall{
    $copy=Join-Path $testRoot 'uninstall-test.exe'
    Copy-Item -LiteralPath (Join-Path $installDir 'Uninstall.exe') -Destination $copy
    $code=RunProcess $copy ('/S _?='+$installDir)
    Assert ($code -eq 0) "Uninstall failed: $code"
    Remove-Item -LiteralPath $copy
}
function CheckPayload{
    foreach($line in Get-Content -LiteralPath (Join-Path $installDir 'SHA256SUMS.txt')){
        Assert ($line -match '^([0-9a-f]{64})  (.+)$') 'Invalid installed hash manifest.'
        $hash=$Matches[1]; $file=Join-Path $installDir $Matches[2]
        Assert ((Get-FileHash -LiteralPath $file -Algorithm SHA256).Hash.ToLowerInvariant() -eq $hash) "Installed file differs: $file"
    }
    foreach($line in Get-Content -LiteralPath (Join-Path $installDir 'ZIP-SHA256SUMS.txt')){
        Assert ($line -match '^([0-9a-f]{64})  (.+)$') 'Invalid original ZIP manifest.'
        $hash=$Matches[1]; $relative=$Matches[2]
        if($relative -eq 'README.md' -or $relative -eq 'dependencies.json' -or $relative.StartsWith('docs/')){continue}
        Assert ((Get-FileHash -LiteralPath (Join-Path $installDir $relative) -Algorithm SHA256).Hash.ToLowerInvariant() -eq $hash) "App/runtime differs from main ZIP: $relative"
    }
}
Assert (Test-Path -LiteralPath $installerPath) 'Build the installer first.'
Assert (!(Test-Path -LiteralPath $installDir)) 'Prior verification install exists; inspect it before retrying.'
Assert (!(Test-Path -LiteralPath $menu)) 'Existing Start menu folder will not be overwritten.'
Assert (!(Test-Path -LiteralPath $desktop)) 'Existing desktop shortcut will not be overwritten.'
Assert ($null -eq (RegistryValue $reg 'DisplayName')) 'Existing SaruCast installation will not be overwritten.'
Assert ($null -eq (RegistryValue 'Software\SaruCast' 'InstallDir')) 'Existing installation path will not be overwritten.'
$zipHash=(Get-FileHash artifacts/SaruCast-win-x64.zip -Algorithm SHA256).Hash
$settings=Join-Path $env:LOCALAPPDATA 'SaruCast/settings.json'
$settingsHash=if(Test-Path -LiteralPath $settings){(Get-FileHash -LiteralPath $settings -Algorithm SHA256).Hash}else{$null}
New-Item -ItemType Directory -Force $testRoot | Out-Null
Assert ((Setup $installDir) -eq 0) 'Initial installation failed.'
CheckPayload
$exe=Join-Path $installDir 'SaruCast.exe'
Assert ((RegistryValue $reg 'InstallLocation') -eq $installDir) 'Install location registration differs.'
Assert ((RegistryValue $reg 'UninstallString') -eq ('"'+$installDir+'\Uninstall.exe"')) 'Uninstall command quoting is incorrect.'
$shell=New-Object -ComObject WScript.Shell
foreach($shortcut in @($desktop,(Join-Path $menu 'SaruCast.lnk'))){
    Assert (Test-Path -LiteralPath $shortcut) "Shortcut missing: $shortcut"
    $link=$shell.CreateShortcut($shortcut)
    Assert ($link.TargetPath -eq $exe) 'Shortcut target is incorrect.'
    Assert ($link.WorkingDirectory -eq $installDir) 'Shortcut working directory is incorrect.'
}
Write-Output 'PASS install, all original ZIP files, Start menu, desktop shortcut and uninstall registration'
$output=Join-Path $testRoot 'startup.json'
Assert ((RunProcess $exe ('--diagnose "'+$output+'"') $true) -eq 0) 'Installed app failed without external runtime.'
$startup=Get-Content -LiteralPath $output -Raw | ConvertFrom-Json
Assert ($startup.ffmpegBundled -and $startup.defaultFrameRate -eq 15 -and !$startup.captureStarted) 'Installed startup state is incorrect.'
Write-Output 'PASS installed app startup with bundled runtime and no automatic capture'
$sentinel=Join-Path $installDir 'user-created.txt'
[IO.File]::WriteAllText($sentinel,'Preserve this user-created file.')
$locked=[IO.File]::Open($exe,[IO.FileMode]::Open,[IO.FileAccess]::Read,[IO.FileShare]::Read)
try{Assert ((Setup $installDir) -eq 2) 'Locked application update was not rejected.'}finally{$locked.Dispose()}
Assert ((Setup $installDir) -eq 0) 'Same-directory update failed.'
CheckPayload
Assert (Test-Path -LiteralPath $sentinel) 'Update removed a user-created file.'
Write-Output 'PASS locked-file protection and same-directory update'
Uninstall
Assert (!(Test-Path -LiteralPath $exe)) 'Uninstall left the executable.'
Assert (!(Test-Path -LiteralPath $menu)) 'Uninstall left Start menu entries.'
Assert (!(Test-Path -LiteralPath $desktop)) 'Uninstall left the desktop shortcut.'
Assert ($null -eq (RegistryValue $reg 'DisplayName')) 'Uninstall registration remains.'
Assert ($null -eq (RegistryValue 'Software\SaruCast' 'InstallDir')) 'Installation path registration remains.'
Assert (Test-Path -LiteralPath $sentinel) 'Uninstall deleted a user-created file.'
Remove-Item -LiteralPath $sentinel
Remove-Item -LiteralPath $installDir
Write-Output 'PASS uninstall removes app, shortcuts and registration, preserving user-created files'
$unowned=Join-Path $testRoot 'unowned'
New-Item -ItemType Directory -Path $unowned | Out-Null
[IO.File]::WriteAllText((Join-Path $unowned 'existing.txt'),'Existing user data')
Assert ((Setup $unowned) -eq 2) 'Nonempty unrelated folder was accepted.'
Assert (!(Test-Path -LiteralPath (Join-Path $unowned 'SaruCast.exe'))) 'Unrelated folder was modified.'
Remove-Item -LiteralPath (Join-Path $unowned 'existing.txt')
Remove-Item -LiteralPath $unowned
Assert ((Get-FileHash artifacts/SaruCast-win-x64.zip -Algorithm SHA256).Hash -eq $zipHash) 'Main ZIP changed.'
$afterSettings=if(Test-Path -LiteralPath $settings){(Get-FileHash -LiteralPath $settings -Algorithm SHA256).Hash}else{$null}
Assert ($afterSettings -eq $settingsHash) 'Existing app settings changed.'
@{success=$true;installedUserDataPreserved=$true;portableZipUnchanged=$true;startMenuShortcutVerified=$true;desktopShortcutVerified=$true;installedStartupVerified=$true;updateVerified=$true;lockedFileRejected=$true;unrelatedFolderRejected=$true;uninstallVerified=$true;userSettingsUnchanged=$true;installerSha256=(Get-FileHash -LiteralPath $installerPath -Algorithm SHA256).Hash.ToLowerInvariant()} | ConvertTo-Json | Set-Content (Join-Path $testRoot 'result.json') -Encoding utf8
Write-Output 'Installer verification passed. Test installation and shortcuts were removed; main ZIP and user settings were preserved.'
