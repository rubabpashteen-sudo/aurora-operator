Unicode true
!include "MUI2.nsh"
!include "x64.nsh"
!include "WinVer.nsh"
!include "LogicLib.nsh"
!include "FileFunc.nsh"

!ifndef RELEASE_DIR
  !error "RELEASE_DIR must be passed by scripts/Build-WindowsRelease.ps1."
!endif
!ifndef PUBLISH_DIR
  !error "PUBLISH_DIR must be passed by scripts/Build-WindowsRelease.ps1."
!endif
!ifndef MANIFEST
  !error "MANIFEST must be passed by scripts/Build-WindowsRelease.ps1."
!endif

!define PRODUCT "Aurora Operator RC"
!define APP_VERSION "0.3.0-rc.3"
!define UNINSTALL_KEY "Software\Microsoft\Windows\CurrentVersion\Uninstall\AuroraOperatorRC"

Name "${PRODUCT}"
OutFile "${RELEASE_DIR}\AuroraOperator-Setup-${APP_VERSION}-x64.exe"
InstallDir "$LOCALAPPDATA\Programs\Aurora Operator RC"
InstallDirRegKey HKCU "${UNINSTALL_KEY}" "InstallLocation"
RequestExecutionLevel user
SetCompressor /SOLID lzma
ShowInstDetails show
ShowUninstDetails show
VIProductVersion "0.3.0.3"
VIAddVersionKey "ProductName" "Aurora Operator RC"
VIAddVersionKey "FileDescription" "Aurora Operator RC Setup"
VIAddVersionKey "FileVersion" "${APP_VERSION}"
VIAddVersionKey "LegalCopyright" "Aurora Operator contributors"

!define MUI_ICON "..\AuroraOperator.App\Assets\aurora.ico"
!define MUI_ABORTWARNING
!define MUI_FINISHPAGE_RUN "$INSTDIR\AuroraOperator.exe"
!define MUI_FINISHPAGE_RUN_NOTCHECKED
!define MUI_FINISHPAGE_SHOWREADME
!define MUI_FINISHPAGE_SHOWREADME_NOTCHECKED
!define MUI_FINISHPAGE_SHOWREADME_TEXT "Open the official Ollama download page"
!define MUI_FINISHPAGE_SHOWREADME_FUNCTION OpenOllama
!define MUI_FINISHPAGE_TEXT "Setup installed Aurora Operator. Start Ollama before your first chat. If Ollama is already installed, use Aurora's Model Manager to select or download a local model."
!insertmacro MUI_PAGE_WELCOME
!insertmacro MUI_PAGE_DIRECTORY
!insertmacro MUI_PAGE_INSTFILES
!insertmacro MUI_PAGE_FINISH
!insertmacro MUI_UNPAGE_CONFIRM
!insertmacro MUI_UNPAGE_INSTFILES
!insertmacro MUI_LANGUAGE "English"

Function .onInit
  ${IfNot} ${RunningX64}
  ${OrIfNot} ${AtLeastWin10}
    MessageBox MB_ICONSTOP "Aurora Operator requires 64-bit Windows 10 or 11." /SD IDOK
    SetErrorLevel 2
    Abort
  ${EndIf}
FunctionEnd

Function OpenOllama
  ExecShell "open" "https://ollama.com/download/windows"
FunctionEnd

Section "Aurora Operator"
  SetShellVarContext current
  ClearErrors
  SetOutPath "$INSTDIR"
  File /r "${PUBLISH_DIR}\*"
  IfErrors failed
  IfFileExists "$INSTDIR\AuroraOperator.exe" 0 failed
  IfFileExists "$INSTDIR\AuroraOperator.dll" 0 failed
  IfFileExists "$INSTDIR\coreclr.dll" 0 failed
  WriteUninstaller "$INSTDIR\Uninstall.exe"
  IfErrors failed

  IfFileExists "$LOCALAPPDATA\Programs\Ollama\ollama.exe" 0 unknownOllama
    DetailPrint "Ollama found in the current user's installed applications."
    Goto integration
  unknownOllama:
    DetailPrint "Ollama was not found in its standard per-user location. Aurora will check the loopback service at startup."

  integration:
  CreateDirectory "$SMPROGRAMS\Aurora Operator RC"
  CreateShortcut "$SMPROGRAMS\Aurora Operator RC\Aurora Operator RC.lnk" "$INSTDIR\AuroraOperator.exe"
  CreateShortcut "$SMPROGRAMS\Aurora Operator RC\Uninstall Aurora Operator RC.lnk" "$INSTDIR\Uninstall.exe"
  IfErrors failed
  WriteRegStr HKCU "${UNINSTALL_KEY}" "DisplayName" "${PRODUCT}"
  WriteRegStr HKCU "${UNINSTALL_KEY}" "DisplayVersion" "${APP_VERSION}"
  WriteRegStr HKCU "${UNINSTALL_KEY}" "InstallLocation" "$INSTDIR"
  WriteRegStr HKCU "${UNINSTALL_KEY}" "UninstallString" '$\"$INSTDIR\Uninstall.exe$\"'
  WriteRegDWORD HKCU "${UNINSTALL_KEY}" "NoModify" 1
  WriteRegDWORD HKCU "${UNINSTALL_KEY}" "NoRepair" 1
  IfErrors failed
  Goto done

  failed:
    MessageBox MB_ICONSTOP "Setup could not finish writing the application or its Windows shortcuts and registration. Check the installation log and folder permissions." /SD IDOK
    SetErrorLevel 3
    Abort
  done:
SectionEnd

Section "Uninstall"
  SetShellVarContext current
  !include "${MANIFEST}"
  Delete "$INSTDIR\Uninstall.exe"
  Delete "$SMPROGRAMS\Aurora Operator RC\Aurora Operator RC.lnk"
  Delete "$SMPROGRAMS\Aurora Operator RC\Uninstall Aurora Operator RC.lnk"
  RMDir "$SMPROGRAMS\Aurora Operator RC"
  RMDir "$INSTDIR"
  DeleteRegKey HKCU "${UNINSTALL_KEY}"
SectionEnd
