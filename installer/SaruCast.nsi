; -*- coding: utf-8 -*-
Unicode true
!include "MUI2.nsh"
!include "LogicLib.nsh"
!include "FileFunc.nsh"
!include "x64.nsh"
!define PRODUCT_ID "SaruCast-PerUser-Install-v1"
!define UNINSTALL_KEY "Software\Microsoft\Windows\CurrentVersion\Uninstall\SaruCast"
Name "SaruCast ${PRODUCT_VERSION}"
OutFile "${OUTPUT_FILE}"
InstallDir "$LOCALAPPDATA\Programs\SaruCast"
InstallDirRegKey HKCU "Software\SaruCast" "InstallDir"
RequestExecutionLevel user
SetCompressor /SOLID lzma
SetOverwrite on
BrandingText "SaruCast"
!define MUI_ICON "${APP_ICON}"
!define MUI_UNICON "${APP_ICON}"
VIProductVersion "${PRODUCT_VERSION}.0"
VIAddVersionKey /LANG=1041 "ProductName" "SaruCast"
VIAddVersionKey /LANG=1041 "FileDescription" "SaruCast セットアップ（Windows x64）"
VIAddVersionKey /LANG=1041 "FileVersion" "${PRODUCT_VERSION}"
VIAddVersionKey /LANG=1041 "LegalCopyright" "SaruCast"
!define MUI_ABORTWARNING
!define MUI_WELCOMEPAGE_TEXT "SaruCast を現在のユーザー用にインストールします。$\r$\n$\r$\nスタートメニューとデスクトップにショートカットを作成します。$\r$\n$\r$\n更新する場合は、インストール先の SaruCast を終了してから続けてください。"
!insertmacro MUI_PAGE_WELCOME
!insertmacro MUI_PAGE_DIRECTORY
!insertmacro MUI_PAGE_INSTFILES
!define MUI_FINISHPAGE_RUN "$INSTDIR\SaruCast.exe"
!define MUI_FINISHPAGE_RUN_TEXT "SaruCast を起動する"
!define MUI_FINISHPAGE_RUN_NOTCHECKED
!insertmacro MUI_PAGE_FINISH
!insertmacro MUI_UNPAGE_CONFIRM
!insertmacro MUI_UNPAGE_INSTFILES
!insertmacro MUI_UNPAGE_FINISH
!insertmacro MUI_LANGUAGE "Japanese"

Function .onInit
  SetShellVarContext current
  SetRegView 64
  ${IfNot} ${RunningX64}
    MessageBox MB_OK|MB_ICONSTOP "64ビットのWindows 11が必要です。" /SD IDOK
    SetErrorLevel 2
    Abort
  ${EndIf}
  ReadRegStr $0 HKLM "Software\Microsoft\Windows NT\CurrentVersion" "CurrentBuildNumber"
  ${If} $0 < 22000
    MessageBox MB_OK|MB_ICONSTOP "Windows 11が必要です。" /SD IDOK
    SetErrorLevel 2
    Abort
  ${EndIf}
FunctionEnd

Function ValidateDirectory
  System::Call 'kernel32::GetFullPathNameW(w "$INSTDIR", i ${NSIS_MAX_STRLEN}, w .r0, p 0) i.r1'
  ${If} $1 == 0
  ${OrIf} $1 >= ${NSIS_MAX_STRLEN}
    Goto bad_directory
  ${EndIf}
  StrCpy $INSTDIR $0
  ${GetRoot} "$INSTDIR" $0
  ${If} $INSTDIR == $0
  ${OrIf} $INSTDIR == "$0\"
    Goto bad_directory
  ${EndIf}
  ReadRegStr $0 HKCU "Software\SaruCast" "InstallDir"
  ${If} $0 != ""
  ${AndIf} $0 != $INSTDIR
    MessageBox MB_OK|MB_ICONSTOP "別の場所にインストール済みです。先に既存のインストーラー版をアンインストールしてください。" /SD IDOK
    SetErrorLevel 2
    Abort
  ${EndIf}
  IfFileExists "$INSTDIR\*" 0 new_directory
  ReadINIStr $0 "$INSTDIR\SaruCast.install.ini" "Install" "Product"
  ${If} $0 != "${PRODUCT_ID}"
    ${GetSize} "$INSTDIR" "/G=0" $0 $1 $2
    ${If} $1 = 0
    ${AndIf} $2 = 0
      Goto new_directory
    ${EndIf}
    Goto bad_directory
  ${EndIf}
  IfFileExists "$INSTDIR\SaruCast.exe" 0 done_directory
  ClearErrors
  FileOpen $0 "$INSTDIR\SaruCast.exe" a
  ${If} ${Errors}
    MessageBox MB_OK|MB_ICONSTOP "インストール先の SaruCast を終了してから再実行してください。" /SD IDOK
    SetErrorLevel 2
    Abort
  ${EndIf}
  FileClose $0
  Goto done_directory
new_directory:
  IfFileExists "$DESKTOP\SaruCast.lnk" shortcut_collision
  IfFileExists "$SMPROGRAMS\SaruCast\*" shortcut_collision done_directory
shortcut_collision:
  MessageBox MB_OK|MB_ICONSTOP "既存の SaruCast ショートカットがあります。名前を変更してから再実行してください。" /SD IDOK
  SetErrorLevel 2
  Abort
bad_directory:
  MessageBox MB_OK|MB_ICONSTOP "空のフォルダー、またはインストーラー版の SaruCast があるフォルダーを選択してください。ZIP版の展開先へ上書きはできません。" /SD IDOK
  SetErrorLevel 2
  Abort
done_directory:
FunctionEnd

Section "SaruCast" SEC_APP
  Call ValidateDirectory
  SetOutPath "$INSTDIR"
  ClearErrors
  File /r "${PAYLOAD_DIR}/*"
  ${If} ${Errors}
    SetErrorLevel 3
    Abort "ファイルの配置に失敗しました。"
  ${EndIf}
  WriteINIStr "$INSTDIR\SaruCast.install.ini" "Install" "Product" "${PRODUCT_ID}"
  WriteINIStr "$INSTDIR\SaruCast.install.ini" "Install" "Version" "${PRODUCT_VERSION}"
  WriteUninstaller "$INSTDIR\Uninstall.exe"
  WriteRegStr HKCU "Software\SaruCast" "InstallDir" "$INSTDIR"
  WriteRegStr HKCU "${UNINSTALL_KEY}" "DisplayName" "SaruCast"
  WriteRegStr HKCU "${UNINSTALL_KEY}" "DisplayVersion" "${PRODUCT_VERSION}"
  WriteRegStr HKCU "${UNINSTALL_KEY}" "Publisher" "SaruCast"
  WriteRegStr HKCU "${UNINSTALL_KEY}" "DisplayIcon" '"$INSTDIR\SaruCast.exe",0'
  WriteRegStr HKCU "${UNINSTALL_KEY}" "InstallLocation" "$INSTDIR"
  WriteRegStr HKCU "${UNINSTALL_KEY}" "UninstallString" '"$INSTDIR\Uninstall.exe"'
  WriteRegStr HKCU "${UNINSTALL_KEY}" "QuietUninstallString" '"$INSTDIR\Uninstall.exe" /S'
  WriteRegDWORD HKCU "${UNINSTALL_KEY}" "NoModify" 1
  WriteRegDWORD HKCU "${UNINSTALL_KEY}" "NoRepair" 1
  ${GetSize} "$INSTDIR" "/S=0K" $0 $1 $2
  WriteRegDWORD HKCU "${UNINSTALL_KEY}" "EstimatedSize" $0
  SetOutPath "$INSTDIR"
  CreateDirectory "$SMPROGRAMS\SaruCast"
  CreateShortcut "$SMPROGRAMS\SaruCast\SaruCast.lnk" "$INSTDIR\SaruCast.exe" "" "$INSTDIR\SaruCast.exe" 0
  CreateShortcut "$SMPROGRAMS\SaruCast\アンインストール.lnk" "$INSTDIR\Uninstall.exe"
  CreateShortcut "$DESKTOP\SaruCast.lnk" "$INSTDIR\SaruCast.exe" "" "$INSTDIR\SaruCast.exe" 0
  ${If} ${Errors}
    SetErrorLevel 3
    Abort "ショートカットまたはアプリ登録の作成に失敗しました。"
  ${EndIf}
SectionEnd

Function un.onInit
  SetShellVarContext current
  SetRegView 64
  ReadINIStr $0 "$INSTDIR\SaruCast.install.ini" "Install" "Product"
  ReadRegStr $1 HKCU "Software\SaruCast" "InstallDir"
  ${If} $0 != "${PRODUCT_ID}"
  ${OrIf} $1 != $INSTDIR
    MessageBox MB_OK|MB_ICONSTOP "SaruCast のインストール先を確認できません。" /SD IDOK
    SetErrorLevel 2
    Abort
  ${EndIf}
  IfFileExists "$INSTDIR\SaruCast.exe" 0 uninstall_ready
  ClearErrors
  FileOpen $0 "$INSTDIR\SaruCast.exe" a
  ${If} ${Errors}
    MessageBox MB_OK|MB_ICONSTOP "SaruCast を終了してからアンインストールしてください。" /SD IDOK
    SetErrorLevel 2
    Abort
  ${EndIf}
  FileClose $0
uninstall_ready:
FunctionEnd

Section "Uninstall"
  ; Only files present in the build payload are deleted; user-created files survive.
  !include "${UNINSTALL_FILES}"
  Delete "$INSTDIR\SaruCast.install.ini"
  Delete "$INSTDIR\Uninstall.exe"
  RMDir "$INSTDIR"
  Delete "$DESKTOP\SaruCast.lnk"
  Delete "$SMPROGRAMS\SaruCast\SaruCast.lnk"
  Delete "$SMPROGRAMS\SaruCast\アンインストール.lnk"
  RMDir "$SMPROGRAMS\SaruCast"
  DeleteRegKey HKCU "${UNINSTALL_KEY}"
  DeleteRegValue HKCU "Software\SaruCast" "InstallDir"
  DeleteRegKey /ifempty HKCU "Software\SaruCast"
SectionEnd
