; A test-only silent installer. Never writes registry, shortcuts or real app paths.
Unicode true
Name "SaruCast update integration fixture"
OutFile "${OUTPUT_FILE}"
RequestExecutionLevel user
SilentInstall silent
VIProductVersion "${APP_VERSION}.0"
VIAddVersionKey "ProductName" "SaruCast"
VIAddVersionKey "FileVersion" "${APP_VERSION}"
VIAddVersionKey "FileDescription" "Test-only update fixture"
VIAddVersionKey "LegalCopyright" "SaruCast tests"
Section
  IfFileExists "$INSTDIR\force-failure.flag" 0 apply
  SetErrorLevel 3
  Quit
apply:
  SetOutPath "$INSTDIR"
  File /oname=SaruCast.dll "${APP_DLL}"
  FileOpen $0 "$INSTDIR\fixture-args.txt" w
  FileWrite $0 "$CMDLINE"
  FileClose $0
SectionEnd
