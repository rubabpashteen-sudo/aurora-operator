; Aurora Operator — NSIS installer (Phase 7, version 0.2.0)
; Builds with NSIS 3.x (makensis AuroraOperator.nsi).
;
; Flow:
;   0. .onInit checks: 64-bit Windows (x64), Windows 10 build 19045
;      (22H2) or newer / Windows 11, total RAM (PowerShell +
;      Get-CimInstance; falls back to the small model if detection
;      fails), and Ollama detection (installed location, then PATH).
;   1. Disk-space check: required space is computed from the model
;      size and the app size SEPARATELY and shown in the error message:
;        model  qwen3:4b ~2,600 MB / qwen3:1.7b ~1,400 MB (approximate,
;               from the Ollama library, ollama.com)
;        app    APP_ALLOWANCE_MB (see below) — 512 MB allowance for the
;               self-contained win-x64 publish, MEASURED at 270 MB on
;               2026-11-21 (Linux cross-publish; re-measure on Windows).
;               The check enforces model + allowance.
;   2. .NET 8 Desktop Runtime: the shipped publish is SELF-CONTAINED,
;      so no runtime is required and DOTNET_REQUIRED is 0. If the
;      publish mode ever changes to framework-dependent, set
;      DOTNET_REQUIRED to 1: the script then detects the runtime
;      (dotnet --list-runtimes) and installs it from Microsoft's
;      official aka.ms link before anything else.
;   3. Ollama: if missing, download OllamaSetup.exe from the official
;      https://ollama.com/download/OllamaSetup.exe (HTTPS only), verify
;      the download exists and is larger than 1 MB, then install it
;      silently. NOTE: Ollama does not publish a checksum for
;      OllamaSetup.exe, so no hash pin is invented here; the size
;      sanity check plus the official HTTPS URL is the verification.
;      Re-check ollama.com before release in case a checksum appears.
;   4. Start Ollama and wait until "ollama list" answers (up to ~30 s),
;      then pull the local model with live progress and up to 3
;      attempts (resume/retry on interrupted download — "ollama pull"
;      resumes partial blob downloads itself). The model is VERIFIED
;      with "ollama list" (the model name must appear); a tiny
;      generation smoke test ("ollama run <model> Say OK", 60 s cap)
;      runs best-effort and a failure there is a WARNING only, never
;      a pass/fail gate.
;      BEHAVIOUR CHANGE (Phase 7): a model pull that still fails after
;      retries, or a failed model verification, now ABORTS the install
;      and rolls back. Previously the install continued with a warning.
;      The installer must NOT report success when the model or a
;      required dependency failed.
;   5. Install the app (self-contained publish output — see BUILD.md),
;      desktop + Start Menu shortcuts, uninstaller.
;   6. Installation health check: app exe present AND "ollama list"
;      shows the model. Any failure aborts and rolls back.
;   7. Finish page offers to launch Aurora Operator.
;   Optional section: Chromium for web research ("playwright install
;   chromium" via the bundled playwright.ps1). Best-effort only: if
;   it fails, research still reports "Chromium missing" honestly
;   instead of fake results.
;
; Admin: RequestExecutionLevel admin. Genuinely needed steps: writing
; to $PROGRAMFILES64, HKLM uninstall keys, and installing Ollama /
; the .NET runtime (both install machine-wide). The model download
; and the app's own data live in the user profile and do not need
; admin by themselves, but the install as a whole does.
;
; The app files must already be published before running makensis:
;   dotnet publish src\AuroraOperator.App -c Release -r win-x64 --self-contained true
;
; VALIDATION STATUS (final release, 2026-11-21): COMPILED on Linux
; with makensis 3.08-2 (Ubuntu nsis package) against the fresh
; self-contained win-x64 publish (270 MB, 815 files). Output:
; AuroraOperator-Setup-0.2.0-x64.exe, 110,168,296 bytes, PE32 GUI,
; SHA-256 a185abb1e47303a7801f1b45c694b2aef3b86777ef16e54b0ee590523c807f97.
; The install-data size makensis reported (280,912,871 bytes
; uncompressed) matches the publish byte count (280,652,815) plus the
; uninstaller stub. The installer was NOT executed and NOT installed
; on Windows — execution, Ollama download, model pull and the
; failure/rollback paths are still BLOCKED until a Windows run.
; Compile and run on Windows before calling this production-ready,
; including the failure paths (no Ollama, interrupted model download,
; low disk space, Windows build < 19045).
;
; RECOMPILED 2026-11-22 after the nsExec pop-order fix below (the
; previous binary could never pass its own Ollama check): makensis
; 3.08-2 exit 0, output AuroraOperator-Setup-0.2.0-x64.exe,
; 110,168,298 bytes, PE32 GUI, SHA-256
; 4b9b361f8ddac147385dc593eb42c5ad39f9240f6400d02cf66b7176e6ffe50a,
; CRC 0xB913284A, install data unchanged (110,037,720 /
; 280,912,871 bytes). Same file re-uploaded to the user's Google
; Drive (same file ID, same link). The FIXED installer is still
; UNEXECUTED on Windows — compile PASS only; the user re-running it
; is the first execution test.
;
; nsExec POP ORDER — CRITICAL FIX (2026-11-22): nsExec::ExecToStack
; pushes the return value on top of the stack and the printed output
; below it, so the FIRST Pop is the exit code and the SECOND Pop is
; the output (NSIS wiki, "NsExec plug-in": Pop $0 # return
; value/error/timeout, Pop $1 # printed text). The shipped 0.2.0
; installer had them backwards at EVERY call site: the Ollama poll
; compared the (empty) output to "0" and could never succeed, so a
; real Windows install failed with "Ollama did not start (its local
; API did not answer within ~30 seconds)" even though `ollama list`
; answered fine; RAM always fell back to 4 GB (0.6b model); the disk
; check was silently skipped; the Ollama size check, model
; verification and health check could never pass either. All call
; sites now pop $0 (exit code) first. Regression-guarded by
; tests/AuroraOperator.Tests/NsisInstallerTests.cs — if that test
; fails, the pops are backwards again. Recompile after any edit:
; makensis AuroraOperator.nsi (from this directory).

!include "MUI2.nsh"
!include "LogicLib.nsh"
!include "x64.nsh"

!define APP_NAME "Aurora Operator"
!define APP_VERSION "0.2.0"
!define APP_PUBLISHER "Aurora Operator"
!define APP_EXE "AuroraOperator.exe"
!define OLLAMA_URL "https://ollama.com/download/OllamaSetup.exe"
!define OLLAMA_SETUP "$TEMP\OllamaSetup.exe"
; Set to 1 only if the publish mode changes to framework-dependent.
!define DOTNET_REQUIRED 0
!define DOTNET_RUNTIME_URL "https://aka.ms/dotnet/8.0/windowsdesktop-runtime-win-x64.exe"
!define DOTNET_SETUP "$TEMP\DotNetDesktopRuntime.exe"
; App publish size: MEASURED 2026-11-21 — the self-contained win-x64
; publish is 270 MB (Linux cross-publish; re-measure on Windows, the
; figure should be close). 512 MB allowance leaves headroom.
!define APP_ALLOWANCE_MB 512
; Model sizes: approximate, from the Ollama library (ollama.com).
!define MODEL_SIZE_4B_MB 2600
!define MODEL_SIZE_17B_MB 1400
!define MODEL_SIZE_06B_MB 523
!define MODEL_SIZE_8B_MB 5200

Name "${APP_NAME} ${APP_VERSION}"
OutFile "AuroraOperator-Setup-${APP_VERSION}-x64.exe"
; NOTE: the NSIS output name is the release's primary installer name
; (AuroraOperator-Setup-0.2.0-x64.exe). The Inno Setup installer keeps
; its own distinct name (AuroraOperator-Setup-Inno-0.2.0.exe), so the
; two installers can never overwrite each other.
InstallDir "$PROGRAMFILES64\${APP_NAME}"
RequestExecutionLevel admin
ShowInstDetails show

!define MUI_FINISHPAGE_RUN "$INSTDIR\${APP_EXE}"
!define MUI_FINISHPAGE_RUN_TEXT "Launch Aurora Operator"

Var RAM_GB
Var MODEL_NAME
Var MODEL_SIZE_MB
Var OLLAMA_EXE
Var OLLAMA_FOUND
Var PULL_OK
Var WIN_BUILD
Var DRIVE_LETTER
Var FREE_MB
Var REQUIRED_MB
Var LOOP_I

!insertmacro MUI_PAGE_WELCOME
!insertmacro MUI_PAGE_DIRECTORY
!insertmacro MUI_PAGE_INSTFILES
!insertmacro MUI_PAGE_FINISH
!insertmacro MUI_UNPAGE_CONFIRM
!insertmacro MUI_UNPAGE_INSTFILES
!insertmacro MUI_LANGUAGE "English"

; ---------------------------------------------------------------------------
; Pre-flight checks: architecture, Windows version, RAM, Ollama detection.
; Runs before any page is shown; aborts before anything is installed.
; ---------------------------------------------------------------------------
Function .onInit
  ; --- 64-bit Windows only.
  ${IfNot} ${RunningX64}
    MessageBox MB_ICONSTOP "Aurora Operator needs 64-bit Windows (x64). This PC is not 64-bit."
    Abort
  ${EndIf}

  ; --- Windows 10 22H2 (build 19045) or newer / Windows 11.
  ReadRegStr $WIN_BUILD HKLM "SOFTWARE\Microsoft\Windows NT\CurrentVersion" "CurrentBuildNumber"
  ${If} $WIN_BUILD == ""
    MessageBox MB_ICONSTOP "Could not determine the Windows version (registry read failed). Installation cannot continue."
    Abort
  ${EndIf}
  IntCmp $WIN_BUILD 19045 BuildOK BuildTooOld BuildOK
  BuildTooOld:
    MessageBox MB_ICONSTOP "Aurora Operator needs Windows 10 version 22H2 (build 19045) or newer, or Windows 11. This PC reports build $WIN_BUILD."
    Abort
  BuildOK:

  ; --- RAM via PowerShell. nsExec::ExecToStack pushes the RETURN VALUE
  ; (exit code) on top of the stack and the printed output BELOW it, so
  ; the FIRST Pop is the exit code and the SECOND Pop is the output
  ; (NSIS wiki, "NsExec plug-in": Pop $0 # return value/error/timeout,
  ; Pop $1 # printed text). A previous version of this script popped
  ; them in the opposite order, so $RAM_GB always received the exit
  ; code "0" and every machine fell back to the 4 GB / qwen3:0.6b model.
  ; If anything looks wrong we fall back to the small model (safe choice).
  StrCpy $RAM_GB "0"
  nsExec::ExecToStack 'powershell -NoProfile -Command "[math]::Round((Get-CimInstance Win32_ComputerSystem).TotalPhysicalMemory / 1GB)"'
  Pop $0 ; exit code
  Pop $RAM_GB ; printed output (total RAM in GB)
  ${If} $0 != "0"
  ${OrIf} $RAM_GB == ""
  ${OrIf} $RAM_GB == "0"
    StrCpy $RAM_GB "4"
  ${EndIf}
  ; Preflight reconciliation 2026-11-21: thresholds aligned with
  ; ModelCatalog.Profiles (8 GB -> qwen3:1.7b; qwen3:4b only at 16 GB;
  ; below 8 GB -> qwen3:0.6b). Previously this selected 4b at >= 8 GB,
  ; contradicting the app-side rule and README/BUILD.md.
  ; Phase 8: qwen3:8b at >= 32 GB (5,200 MB), matching SelectBestModel
  ; (which also requires >= 10 GB free — the installer only measures
  ; total RAM, so a 32 GB machine with little free RAM gets 8b here
  ; while the app may fall back to 4b; both are safe, the app's choice
  ; governs at runtime).
  ${If} $RAM_GB >= "32"
    StrCpy $MODEL_NAME "qwen3:8b"
    StrCpy $MODEL_SIZE_MB "${MODEL_SIZE_8B_MB}"
  ${ElseIf} $RAM_GB >= "16"
    StrCpy $MODEL_NAME "qwen3:4b"
    StrCpy $MODEL_SIZE_MB "${MODEL_SIZE_4B_MB}"
  ${ElseIf} $RAM_GB >= "8"
    StrCpy $MODEL_NAME "qwen3:1.7b"
    StrCpy $MODEL_SIZE_MB "${MODEL_SIZE_17B_MB}"
  ${Else}
    StrCpy $MODEL_NAME "qwen3:0.6b"
    StrCpy $MODEL_SIZE_MB "${MODEL_SIZE_06B_MB}"
  ${EndIf}

  ; --- Ollama: installed location first, then PATH.
  StrCpy $OLLAMA_FOUND "0"
  StrCpy $OLLAMA_EXE "$LOCALAPPDATA\Programs\Ollama\ollama.exe"
  ${If} ${FileExists} "$OLLAMA_EXE"
    StrCpy $OLLAMA_FOUND "1"
  ${Else}
    nsExec::ExecToStack 'cmd /c where ollama'
    Pop $0 ; exit code (0 = found on PATH)
    Pop $1 ; printed output (the resolved path, may carry line breaks)
    ${If} $0 == "0"
    ${AndIf} $1 != ""
      ; Found on PATH. Use the bare name (resolved via PATH at runtime)
      ; rather than the raw `where` output, which can carry line breaks.
      StrCpy $OLLAMA_EXE "ollama"
      StrCpy $OLLAMA_FOUND "1"
    ${EndIf}
  ${EndIf}
FunctionEnd

; ---------------------------------------------------------------------------
; Rollback: remove anything this install created, then abort.
; ---------------------------------------------------------------------------
Function Rollback
  DetailPrint "Rolling back the installation..."
  RMDir /r "$INSTDIR"
  Delete "${OLLAMA_SETUP}"
  Delete "${DOTNET_SETUP}"
  Delete "$TEMP\aurora-smoke.txt"
  Delete "$TEMP\aurora-smoke-err.txt"
  Abort "Aurora Operator installation failed and was rolled back."
FunctionEnd

; ---------------------------------------------------------------------------
; Main install section.
; ---------------------------------------------------------------------------
Section "${APP_NAME} (required)" SecApp
  SectionIn RO
  SetOutPath "$INSTDIR"
  StrCpy $PULL_OK "0"

  DetailPrint "Detected RAM: $RAM_GB GB — model selected: $MODEL_NAME (approx. $MODEL_SIZE_MB MB)"

  ; --- 0. Disk space (app size and model size stated separately) -------
  ; Required = model size + app allowance (512 MB; self-contained
  ; publish measured at 384 MB, 2026-11-21). The model figure is the
  ; approximate Ollama library size. If free-space detection itself
  ; fails we warn and continue — a genuinely full disk still fails
  ; loudly at the file-copy check below, which rolls back.
  IntOp $REQUIRED_MB $MODEL_SIZE_MB + ${APP_ALLOWANCE_MB}
  StrCpy $DRIVE_LETTER $INSTDIR 1
  nsExec::ExecToStack 'powershell -NoProfile -Command "[math]::Floor((Get-PSDrive $DRIVE_LETTER).Free / 1MB)"'
  Pop $0 ; exit code
  Pop $FREE_MB ; printed output (free MB on the drive)
  ${If} $0 == "0"
  ${AndIf} $FREE_MB != ""
    IntCmp $FREE_MB $REQUIRED_MB SpaceOK SpaceTooSmall SpaceOK
    SpaceTooSmall:
      MessageBox MB_ICONSTOP "Not enough free disk space.$\n$\nRequired: $REQUIRED_MB MB total ($MODEL_SIZE_MB MB for the $MODEL_NAME model + ${APP_ALLOWANCE_MB} MB app allowance).$\nFree on $DRIVE_LETTER: $FREE_MB MB.$\n$\nFree up disk space and try again."
      Abort "Not enough free disk space."
    SpaceOK:
      DetailPrint "Disk space OK: $FREE_MB MB free on $DRIVE_LETTER, $REQUIRED_MB MB required."
  ${Else}
    DetailPrint "WARNING: could not measure free disk space (exit code $0); continuing — the file-copy check below will still fail loudly if the disk is full."
  ${EndIf}

  ; --- 0b. .NET 8 Desktop Runtime (framework-dependent only) ----------
  ; Dormant for the self-contained publish (DOTNET_REQUIRED is 0).
  ${If} ${DOTNET_REQUIRED} == "1"
    DetailPrint "Checking for the .NET 8 Desktop Runtime..."
    nsExec::ExecToStack 'powershell -NoProfile -Command "if (dotnet --list-runtimes | Out-String -match \"Microsoft.WindowsDesktop.App 8.\") { exit 0 } else { exit 1 }"'
    Pop $0 ; exit code (0 = runtime found)
    Pop $1 ; printed output (none for this command)
    ${If} $0 != "0"
      DetailPrint ".NET 8 Desktop Runtime not found. Downloading from Microsoft..."
      NSISdl::download "${DOTNET_RUNTIME_URL}" "${DOTNET_SETUP}"
      Pop $0
      ${If} $0 != "success"
        MessageBox MB_ICONSTOP "Could not download the .NET 8 Desktop Runtime ($0). Check your internet connection and try again."
        Call Rollback
      ${EndIf}
      DetailPrint "Installing .NET 8 Desktop Runtime (silent)..."
      ExecWait '"${DOTNET_SETUP}" /install /quiet /norestart' $0
      ${If} $0 != "0"
        MessageBox MB_ICONSTOP ".NET 8 Desktop Runtime installation failed (exit code $0)."
        Call Rollback
      ${EndIf}
      Delete "${DOTNET_SETUP}"
    ${Else}
      DetailPrint ".NET 8 Desktop Runtime found."
    ${EndIf}
  ${EndIf}

  ; --- 1. Ollama ---------------------------------------------------------
  ${If} $OLLAMA_FOUND == "0"
    DetailPrint "Ollama not found. Downloading OllamaSetup.exe..."
    NSISdl::download "${OLLAMA_URL}" "${OLLAMA_SETUP}"
    Pop $0
    ${If} $0 != "success"
      MessageBox MB_ICONSTOP "Could not download Ollama from ollama.com ($0). Check your internet connection and try again."
      Call Rollback
    ${EndIf}
    ; Size sanity check: the real installer is tens of MB. Ollama
    ; publishes no checksum for OllamaSetup.exe, so no hash is pinned.
    nsExec::ExecToStack 'powershell -NoProfile -Command "(Get-Item \"$TEMP\OllamaSetup.exe\").Length"'
    Pop $0 ; exit code
    Pop $1 ; printed output (file size in bytes)
    ${If} $0 != "0"
    ${OrIf} $1 == ""
      MessageBox MB_ICONSTOP "The downloaded Ollama installer could not be verified (size check failed). The download may be incomplete."
      Call Rollback
    ${EndIf}
    IntCmp $1 1000000 SizeOK SizeTooSmall SizeOK
    SizeTooSmall:
      MessageBox MB_ICONSTOP "The downloaded Ollama installer is only $1 bytes — far smaller than the real installer. The download may be incomplete or tampered with."
      Call Rollback
    SizeOK:
    DetailPrint "Installing Ollama (silent)..."
    ExecWait '"${OLLAMA_SETUP}" /VERYSILENT /SUPPRESSMSGBOXES /NORESTART' $0
    ${If} $0 != "0"
      MessageBox MB_ICONSTOP "Ollama installation failed (exit code $0)."
      Call Rollback
    ${EndIf}
    Delete "${OLLAMA_SETUP}"
    StrCpy $OLLAMA_EXE "$LOCALAPPDATA\Programs\Ollama\ollama.exe"
    ${IfNot} ${FileExists} "$OLLAMA_EXE"
      MessageBox MB_ICONSTOP "Ollama installed but ollama.exe was not found where expected."
      Call Rollback
    ${EndIf}
  ${Else}
    DetailPrint "Ollama found at $OLLAMA_EXE"
  ${EndIf}

  ; --- 2. Start Ollama, pull the model (retry), verify it ---------------
  DetailPrint "Starting Ollama..."
  ; SECURITY (Phase 7, finding F2): pin Ollama to loopback. A user-set
  ; OLLAMA_HOST=0.0.0.0 would otherwise expose the unauthenticated local
  ; API to the network. SetEnvironmentVariable applies to this process
  ; and every child it launches, including "ollama serve" below.
  System::Call 'kernel32::SetEnvironmentVariable(t "OLLAMA_HOST", t "127.0.0.1:11434")'
  Exec '"$OLLAMA_EXE" serve'
  ; Poll until "ollama list" answers (up to ~30 s). "ollama list"
  ; contacts the server, so a zero exit code means it is listening.
  StrCpy $LOOP_I "0"
  ${For} $LOOP_I 1 10
    nsExec::ExecToStack 'cmd /c ""$OLLAMA_EXE" list > nul 2>&1"'
    Pop $0 ; exit code (0 = the Ollama API answered)
    Pop $1 ; printed output (empty here — redirected to nul)
    ${If} $0 == "0"
      ${Break}
    ${EndIf}
    ExecWait 'cmd /c ping -n 4 127.0.0.1 > nul'
  ${Next}
  nsExec::ExecToStack 'cmd /c ""$OLLAMA_EXE" list > nul 2>&1"'
  Pop $0 ; exit code
  Pop $1 ; printed output (empty here — redirected to nul)
  ${If} $0 != "0"
    MessageBox MB_ICONSTOP "Ollama did not start (its local API did not answer within ~30 seconds)."
    Call Rollback
  ${EndIf}

  ; Pull with up to 3 attempts: "ollama pull" resumes partial blob
  ; downloads itself, so an interrupted download resumes cleanly.
  DetailPrint "Downloading local model $MODEL_NAME (approx. $MODEL_SIZE_MB MB; this can take a few minutes)..."
  ${For} $LOOP_I 1 3
    DetailPrint "Model download attempt $LOOP_I of 3..."
    nsExec::ExecToLog '"$OLLAMA_EXE" pull $MODEL_NAME'
    Pop $0
    ${If} $0 == "0"
      StrCpy $PULL_OK "1"
      ${Break}
    ${EndIf}
    DetailPrint "Model download attempt $LOOP_I failed (exit code $0); retrying..."
  ${Next}
  ${If} $PULL_OK != "1"
    MessageBox MB_ICONSTOP "The $MODEL_NAME model could not be downloaded after 3 attempts. Aurora Operator needs this model to run, so the installation cannot continue. Check your internet connection and try again."
    Call Rollback
  ${EndIf}

  ; Verify the model is actually present (never trust the pull's exit
  ; code alone): "ollama list" output must contain the model name.
  nsExec::ExecToStack 'cmd /c ""$OLLAMA_EXE" list | findstr /I /C:"$MODEL_NAME""'
  Pop $0 ; exit code (0 = findstr found the model name)
  Pop $1 ; printed output (the matching "ollama list" line)
  ${If} $0 != "0"
    MessageBox MB_ICONSTOP "Model verification failed: '$MODEL_NAME' does not appear in 'ollama list' after the download. The installation cannot continue."
    Call Rollback
  ${EndIf}
  DetailPrint "Model $MODEL_NAME verified in 'ollama list'."

  ; Tiny generation smoke test, best-effort only (60 s cap): a failure
  ; here is a WARNING, never a pass/fail gate — the list check above
  ; is the gate. $$p is NSIS escaping for PowerShell's $p.
  DetailPrint "Running a tiny generation smoke test (best-effort, 60 s cap)..."
  nsExec::ExecToStack 'powershell -NoProfile -Command "$$p = Start-Process -FilePath "$OLLAMA_EXE" -ArgumentList "run","$MODEL_NAME","Say OK" -PassThru -NoNewWindow -RedirectStandardOutput "$TEMP\aurora-smoke.txt" -RedirectStandardError "$TEMP\aurora-smoke-err.txt"; if ($$p.WaitForExit(60000)) { exit $$p.ExitCode } else { $$p.Kill(); exit 2 }"'
  Pop $0 ; exit code
  Pop $1 ; printed output (none for this command)
  ${If} $0 == "0"
    DetailPrint "Smoke test passed."
  ${Else}
    DetailPrint "WARNING: model smoke test did not complete cleanly (exit code $0). The model is still listed by 'ollama list'; if the app cannot chat, re-download the model from Model Manager."
  ${EndIf}
  Delete "$TEMP\aurora-smoke.txt"
  Delete "$TEMP\aurora-smoke-err.txt"

  ; --- 3. App files -------------------------------------------------------
  DetailPrint "Installing Aurora Operator files..."
  SetOverwrite on
  File /r "..\AuroraOperator.App\bin\Release\net8.0-windows\win-x64\publish\*.*"
  ${If} ${FileExists} "$INSTDIR\${APP_EXE}"
  ${Else}
    MessageBox MB_ICONSTOP "App files were not copied. Did you run 'dotnet publish' first? See BUILD.md."
    Call Rollback
  ${EndIf}

  ; --- 4. Shortcuts + uninstaller ----------------------------------------
  CreateDirectory "$SMPROGRAMS\${APP_NAME}"
  CreateShortcut "$SMPROGRAMS\${APP_NAME}\${APP_NAME}.lnk" "$INSTDIR\${APP_EXE}"
  CreateShortcut "$SMPROGRAMS\${APP_NAME}\Uninstall ${APP_NAME}.lnk" "$INSTDIR\uninstall.exe"
  WriteUninstaller "$INSTDIR\uninstall.exe"
  WriteRegStr HKLM "Software\Microsoft\Windows\CurrentVersion\Uninstall\${APP_NAME}" "DisplayName" "${APP_NAME}"
  WriteRegStr HKLM "Software\Microsoft\Windows\CurrentVersion\Uninstall\${APP_NAME}" "DisplayVersion" "${APP_VERSION}"
  WriteRegStr HKLM "Software\Microsoft\Windows\CurrentVersion\Uninstall\${APP_NAME}" "Publisher" "${APP_PUBLISHER}"
  WriteRegStr HKLM "Software\Microsoft\Windows\CurrentVersion\Uninstall\${APP_NAME}" "UninstallString" "$INSTDIR\uninstall.exe"
  WriteRegDWORD HKLM "Software\Microsoft\Windows\CurrentVersion\Uninstall\${APP_NAME}" "NoModify" 1
  WriteRegDWORD HKLM "Software\Microsoft\Windows\CurrentVersion\Uninstall\${APP_NAME}" "NoRepair" 1

  ; --- 5. Installation health check --------------------------------------
  ; App exe present AND the model still listed. Any failure rolls back
  ; instead of reporting a successful install.
  DetailPrint "Running installation health check..."
  nsExec::ExecToStack 'cmd /c ""$OLLAMA_EXE" list | findstr /I /C:"$MODEL_NAME""'
  Pop $0 ; exit code (0 = findstr found the model name)
  Pop $1 ; printed output (the matching "ollama list" line)
  ${If} $0 != "0"
  ${OrIfNot} ${FileExists} "$INSTDIR\${APP_EXE}"
    MessageBox MB_ICONSTOP "Installation health check failed: the app or the $MODEL_NAME model is missing after install. The installation will be rolled back."
    Call Rollback
  ${EndIf}
  DetailPrint "Health check passed: ${APP_EXE} installed, model $MODEL_NAME present."
SectionEnd

Section "Desktop shortcut" SecDesktop
  CreateShortcut "$DESKTOP\${APP_NAME}.lnk" "$INSTDIR\${APP_EXE}"
SectionEnd

; ---------------------------------------------------------------------------
; Optional: Chromium for web research. Best-effort only — if it fails,
; BrowserTools still reports "Chromium missing" honestly.
; Runs "playwright install chromium" via the playwright.ps1 bundled
; next to the app (shipped with the Microsoft.Playwright package).
; ---------------------------------------------------------------------------
Section /o "Chromium browser for web research (optional)" SecChromium
  ${IfNot} ${FileExists} "$INSTDIR\playwright.ps1"
    DetailPrint "WARNING: playwright.ps1 was not found next to the app, so Chromium was not installed. Web research will report that Chromium is missing until you run 'playwright install chromium' yourself."
  ${Else}
    DetailPrint "Installing Chromium for Playwright (this can take a few minutes)..."
    ExecWait 'powershell -NoProfile -ExecutionPolicy Bypass -File "$INSTDIR\playwright.ps1" install chromium' $0
    ${If} $0 != "0"
      DetailPrint "WARNING: Chromium installation finished with exit code $0. Web research will report that Chromium is missing until it is installed."
    ${Else}
      DetailPrint "Chromium installed for web research."
    ${EndIf}
  ${EndIf}
SectionEnd

; ---------------------------------------------------------------------------
; Uninstaller: offers to keep or remove the downloaded Ollama models.
; Ollama itself is left installed (it is a shared tool the user may use
; elsewhere); only its downloaded models are optionally removed.
; ---------------------------------------------------------------------------
Function un.onInit
  MessageBox MB_YESNO|MB_ICONQUESTION "Keep the downloaded Ollama models (qwen3)?$\n$\nYes = keep the models on disk.$\nNo = delete the downloaded models as well." IDYES KeepModels
  RMDir /r "$PROFILE\.ollama\models"
  Goto Done
  KeepModels:
  Done:
FunctionEnd

Section "Uninstall"
  Delete "$DESKTOP\${APP_NAME}.lnk"
  Delete "$SMPROGRAMS\${APP_NAME}\${APP_NAME}.lnk"
  Delete "$SMPROGRAMS\${APP_NAME}\Uninstall ${APP_NAME}.lnk"
  RMDir "$SMPROGRAMS\${APP_NAME}"
  RMDir /r "$INSTDIR"
  DeleteRegKey HKLM "Software\Microsoft\Windows\CurrentVersion\Uninstall\${APP_NAME}"
SectionEnd
