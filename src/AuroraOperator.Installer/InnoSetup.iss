; Aurora Operator — Inno Setup installer (Inno Setup 6.x, Phase 7, version 0.2.0)
; Builds with: iscc InnoSetup.iss
;
; Same flow as AuroraOperator.nsi (keep the two scripts in sync):
;   0. InitializeSetup checks: 64-bit Windows (ArchitecturesAllowed=x64),
;      Windows 10 build 19045 (22H2) or newer / Windows 11
;      (MinVersion=10.0.19045), total RAM (kernel32
;      GetPhysicallyInstalledSystemMemory), disk space, and Ollama
;      detection (installed location, then PATH via "where ollama").
;      Disk space is checked at runtime because the required size
;      depends on the selected model; the two parts are stated
;      SEPARATELY in the error message:
;        model  qwen3:4b ~2,600 MB / qwen3:1.7b ~1,400 MB (approximate,
;               from the Ollama library, ollama.com)
;        app    AppAllowanceMB (see below) — 512 MB allowance for the
;               self-contained win-x64 publish, MEASURED at 270 MB on
;               2026-11-21 (Linux cross-publish; re-measure on Windows).
;   1. .NET 8 Desktop Runtime: the shipped publish is SELF-CONTAINED,
;      so no runtime is required and DotNetRequired is False. If the
;      publish mode ever changes to framework-dependent, set it to
;      True: the script then detects the runtime
;      (dotnet --list-runtimes) and installs it from Microsoft's
;      official aka.ms link before anything else.
;   2. Ollama: if missing, download OllamaSetup.exe from the official
;      https://ollama.com/download/OllamaSetup.exe with Inno Setup 6's
;      built-in download page, verify the download exists and is
;      larger than 1 MB, then install it silently. NOTE: Ollama does
;      not publish a checksum for OllamaSetup.exe, so no hash pin is
;      invented here; the size sanity check plus the official HTTPS
;      URL is the verification. Re-check ollama.com before release.
;      (The older Inno Download Plugin, IDP, is not needed.)
;   3. Start Ollama and wait until "ollama list" answers (up to ~30 s),
;      then pull the local model with up to 3 attempts (resume/retry
;      on interrupted download — "ollama pull" resumes partial blob
;      downloads itself). The model is VERIFIED with "ollama list"
;      (the model name must appear); a tiny generation smoke test
;      ("ollama run <model> Say OK", 60 s cap) runs best-effort and a
;      failure there is a WARNING only, never a pass/fail gate.
;      All of this happens in CurStepChanged(ssInstall), BEFORE any app
;      file is copied, so a failure there aborts with no partial
;      install left behind.
;      BEHAVIOUR CHANGE (Phase 7): a model pull that still fails after
;      retries, or a failed model verification, now ABORTS the install.
;      Previously the install continued with a warning. The installer
;      must NOT report success when the model or a required dependency
;      failed.
;   4. Install the app (self-contained publish output — see BUILD.md),
;      desktop + Start Menu shortcuts, uninstaller.
;   5. Installation health check (ssPostInstall): app exe present AND
;      "ollama list" shows the model. A failure is reported with an
;      error dialog instead of a silent success.
;   6. Finish page offers to launch Aurora Operator.
;   Optional task: Chromium for web research ("playwright install
;   chromium" via the bundled playwright.ps1). Best-effort only: if
;   it fails, research still reports "Chromium missing" honestly.
;
; Admin: PrivilegesRequired=admin. Genuinely needed steps: writing to
; {autopf}, HKLM uninstall keys, and installing Ollama / the .NET
; runtime (both install machine-wide). The model download and the
; app's own data live in the user profile and do not need admin by
; themselves, but the install as a whole does.
;
; The app files must already be published before running iscc:
;   dotnet publish src\AuroraOperator.App -c Release -r win-x64 --self-contained true
;
; VALIDATION STATUS (Phase 7, 2026-11-21): this script was NOT
; executed and NOT compiled — iscc is a Windows-only tool and is not
; installed on the Linux VM. Validated by careful review only.
; Compile and run on Windows before release, including the failure
; paths (no Ollama, interrupted model download, low disk space,
; Windows build < 19045).

#define AppName "Aurora Operator"
#define AppVersion "0.2.0"
#define AppPublisher "Aurora Operator"
#define AppExe "AuroraOperator.exe"
#define OllamaUrl "https://ollama.com/download/OllamaSetup.exe"
#define DotNetRuntimeUrl "https://aka.ms/dotnet/8.0/windowsdesktop-runtime-win-x64.exe"

[Setup]
AppId={{7E3F1A2B-4C5D-4E6F-8A9B-0C1D2E3F4A5B}
AppName={#AppName}
AppVersion={#AppVersion}
AppPublisher={#AppPublisher}
DefaultDirName={autopf}\{#AppName}
DefaultGroupName={#AppName}
ArchitecturesAllowed=x64
ArchitecturesInstallIn64BitMode=x64
MinVersion=10.0.19045
PrivilegesRequired=admin
PrivilegesRequiredOverridesAllowed=dialog
OutputDir=.
OutputBaseFilename=AuroraOperator-Setup-Inno-{#AppVersion}
SetupIconFile=..\AuroraOperator.App\Assets\aurora.ico
WizardStyle=modern
Compression=lzma2
SolidCompression=yes
DisableProgramGroupPage=no
UninstallDisplayIcon={app}\{#AppExe}

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Files]
Source: "..\AuroraOperator.App\bin\Release\net8.0-windows\win-x64\publish\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{autoprograms}\{#AppName}\{#AppName}"; Filename: "{app}\{#AppExe}"
Name: "{autoprograms}\{#AppName}\Uninstall {#AppName}"; Filename: "{uninstallexe}"
Name: "{autodesktop}\{#AppName}"; Filename: "{app}\{#AppExe}"; Tasks: desktopicon

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"
Name: "chromium"; Description: "Install Chromium for web research (optional, needs internet)"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked

[Run]
Filename: "{app}\{#AppExe}"; Description: "{cm:LaunchProgram,{#StringChange(AppName, '&', '&&')}}"; Flags: nowait postinstall skipifsilent

[Code]
const
  { Set to True only if the publish mode changes to framework-dependent. }
  DotNetRequired = False;
  { App publish size: MEASURED 2026-11-21 — the self-contained win-x64
    publish is 270 MB (Linux cross-publish; re-measure on Windows, the
    figure should be close). 512 MB allowance leaves headroom. }
  AppAllowanceMB = 512;
  { Model sizes in MB: approximate, from the Ollama library (ollama.com). }
  ModelSize4bMB = 2600;
  ModelSize17bMB = 1400;
  ModelSize06bMB = 523;
  ModelSize8bMB = 5200;

var
  ModelName: String;
  ModelSizeMB: Integer;
  NeedOllama: Boolean;
  NeedDotNet: Boolean;
  DownloadPage: TDownloadWizardPage;
  OllamaExe: String;
  { Phase 8: set when the ssPostInstall health check fails. The
    CancelButtonClick handler below uses it to suppress the "Exit
    Setup?" confirmation for the programmatic cancel that follows. }
  HealthCheckFailed: Boolean;

function GetPhysicallyInstalledSystemMemory(var TotalMemoryInKilobytes: Int64): Boolean;
  external 'GetPhysicallyInstalledSystemMemory@kernel32.dll stdcall';

function OllamaExePath(): String;
begin
  Result := ExpandConstant('{localappdata}\Programs\Ollama\ollama.exe');
end;

{ The ollama binary actually used: the installed location when it
  exists there, otherwise the bare name resolved via PATH (an Ollama
  found only on PATH has no usable default-location path). Set in
  InitializeSetup. }
function OllamaExeResolved(): String;
begin
  if OllamaExe <> '' then
    Result := OllamaExe
  else
    Result := OllamaExePath();
end;

function OllamaOnPath(): Boolean;
var
  WhereFile, Contents: String;
  Code: Integer;
begin
  Result := False;
  WhereFile := ExpandConstant('{tmp}\where-ollama.txt');
  if Exec(ExpandConstant('{cmd}'), '/c where ollama > "' + WhereFile + '" 2>nul', '', SW_HIDE, ewWaitUntilTerminated, Code) then
  begin
    if LoadStringFromFile(WhereFile, Contents) then
      Result := Trim(Contents) <> '';
  end;
  DeleteFile(WhereFile);
end;

function DotNetDesktopRuntimeInstalled(): Boolean;
var
  RuntimesFile, Contents: String;
  Code: Integer;
begin
  Result := False;
  RuntimesFile := ExpandConstant('{tmp}\dotnet-runtimes.txt');
  if Exec(ExpandConstant('{cmd}'), '/c dotnet --list-runtimes > "' + RuntimesFile + '" 2>nul', '', SW_HIDE, ewWaitUntilTerminated, Code) then
  begin
    if LoadStringFromFile(RuntimesFile, Contents) then
      Result := Pos('Microsoft.WindowsDesktop.App 8.', Contents) > 0;
  end;
  DeleteFile(RuntimesFile);
end;

function OllamaListShowsModel(): Boolean;
var
  ListFile, Contents: String;
  Code: Integer;
begin
  Result := False;
  ListFile := ExpandConstant('{tmp}\ollama-list.txt');
  { "ollama list" contacts the server; exit code 0 + the model name in
    the output means the server is up and the model is present. }
  if Exec(ExpandConstant('{cmd}'), '/c ""' + OllamaExeResolved() + '" list > "' + ListFile + '" 2>nul"', '', SW_HIDE, ewWaitUntilTerminated, Code) then
  begin
    if (Code = 0) and LoadStringFromFile(ListFile, Contents) then
      Result := Pos(ModelName, Contents) > 0;
  end;
  DeleteFile(ListFile);
end;

function OllamaServerResponds(): Boolean;
var
  Code: Integer;
begin
  Result := Exec(OllamaExeResolved(), 'list', '', SW_HIDE, ewWaitUntilTerminated, Code) and (Code = 0);
end;

function InitializeSetup(): Boolean;
var
  TotalKb: Int64;
  RamGb: Int64;
  FreeBytes, TotalBytes: Int64;
  RequiredMB: Integer;
  FreeMB: Int64;
  TargetDrive: String;
begin
  { --- RAM detection; on any failure assume the smallest model. --- }
  { Preflight reconciliation 2026-11-21: thresholds aligned with
    ModelCatalog.Profiles (8 GB -> qwen3:1.7b; qwen3:4b only at 16 GB;
    below 8 GB -> qwen3:0.6b). Previously this selected 4b at >= 8 GB,
    contradicting the app-side rule and README/BUILD.md.
    Phase 8: qwen3:8b at >= 32 GB (5,200 MB), matching SelectBestModel
    (which also requires >= 10 GB free — the installer only measures
    total RAM; the app's runtime choice governs if free RAM is low). }
  ModelName := 'qwen3:0.6b';
  ModelSizeMB := ModelSize06bMB;
  try
    if GetPhysicallyInstalledSystemMemory(TotalKb) then
    begin
      RamGb := TotalKb div (1024 * 1024);
      if RamGb >= 32 then
      begin
        ModelName := 'qwen3:8b';
        ModelSizeMB := ModelSize8bMB;
      end
      else if RamGb >= 16 then
      begin
        ModelName := 'qwen3:4b';
        ModelSizeMB := ModelSize4bMB;
      end
      else if RamGb >= 8 then
      begin
        ModelName := 'qwen3:1.7b';
        ModelSizeMB := ModelSize17bMB;
      end;
    end;
  except
    ModelName := 'qwen3:0.6b';
    ModelSizeMB := ModelSize06bMB;
  end;

  { --- Disk space: model size and app size stated separately. --- }
  RequiredMB := ModelSizeMB + AppAllowanceMB;
  TargetDrive := ExtractFileDrive(ExpandConstant('{autopf}')) + '\';
  if GetSpaceOnDisk(TargetDrive, FreeBytes, TotalBytes) then
  begin
    FreeMB := FreeBytes div (1024 * 1024);
    if FreeMB < RequiredMB then
    begin
      MsgBox('Not enough free disk space.' + #13#10 + #13#10 +
        'Required: ' + IntToStr(RequiredMB) + ' MB total (' +
        IntToStr(ModelSizeMB) + ' MB for the ' + ModelName + ' model + ' +
        IntToStr(AppAllowanceMB) + ' MB app allowance).' + #13#10 +
        'Free on ' + TargetDrive + ': ' + IntToStr(FreeMB) + ' MB.' + #13#10 + #13#10 +
        'Free up disk space and try again.',
        mbError, MB_OK);
      Result := False;
      Exit;
    end;
  end
  else
  begin
    { Detection failure must not silently pass: warn in the log via a
      message the user sees, but let the install continue — a genuinely
      full disk still fails loudly at file copy / model download. }
    MsgBox('Warning: free disk space could not be measured on ' + TargetDrive +
      '. The install will continue, but it needs about ' + IntToStr(RequiredMB) +
      ' MB (' + IntToStr(ModelSizeMB) + ' MB for the ' + ModelName +
      ' model + ' + IntToStr(AppAllowanceMB) + ' MB app allowance).',
      mbInformation, MB_OK);
  end;

  { --- Ollama detection: installed location first, then PATH. --- }
  NeedOllama := not FileExists(OllamaExePath());
  if NeedOllama then
    NeedOllama := not OllamaOnPath();
  { Resolve the binary used by every later step. }
  if FileExists(OllamaExePath()) then
    OllamaExe := OllamaExePath()
  else
    OllamaExe := 'ollama';

  { --- .NET 8 Desktop Runtime (framework-dependent only; dormant for
    the self-contained publish). --- }
  NeedDotNet := DotNetRequired and not DotNetDesktopRuntimeInstalled();

  Result := True;
end;

procedure InitializeWizard();
begin
  if NeedOllama or NeedDotNet then
  begin
    { Inno Setup 6 built-in download page; no external plugin required. }
    DownloadPage := CreateDownloadPage('Downloading prerequisites', 'Aurora Operator needs Ollama (and, for framework-dependent builds, the .NET 8 Desktop Runtime). Downloading...', nil);
    if NeedDotNet then
      DownloadPage.Add('{#DotNetRuntimeUrl}', 'DotNetDesktopRuntime.exe', '');
    if NeedOllama then
      DownloadPage.Add('{#OllamaUrl}', 'OllamaSetup.exe', '');
  end;
end;

procedure FailInstall(const Msg: String);
begin
  { Called during ssInstall, before any app file is copied, so aborting
    leaves no partial install behind. Never report success when a
    required dependency or the model failed. }
  MsgBox(Msg, mbError, MB_OK);
  Abort();
end;

procedure CurStepChanged(CurStep: TSetupStep);
var
  Code: Integer;
  Attempt: Integer;
  PullOk: Boolean;
  SetupFile: String;
  SizeOk: Boolean;
  SmokeFile: String;
begin
  if CurStep = ssInstall then
  begin
    if NeedOllama or NeedDotNet then
    begin
      { Downloads before the [Files] section runs; a failed download
        aborts the install, so no partial install is left behind. }
      DownloadPage.Download();
    end;

    { --- .NET 8 Desktop Runtime (dormant for self-contained builds). --- }
    if NeedDotNet then
    begin
      SetupFile := ExpandConstant('{tmp}\DotNetDesktopRuntime.exe');
      if not FileExists(SetupFile) then
        FailInstall('The .NET 8 Desktop Runtime download is missing. The installation cannot continue.');
      if not Exec(SetupFile, '/install /quiet /norestart', '', SW_HIDE, ewWaitUntilTerminated, Code) or (Code <> 0) then
        FailInstall('.NET 8 Desktop Runtime installation failed (exit code ' + IntToStr(Code) + ').');
      DeleteFile(SetupFile);
    end;

    { --- Ollama: install, start, pull + verify the model. --- }
    if NeedOllama then
    begin
      SetupFile := ExpandConstant('{tmp}\OllamaSetup.exe');
      if not FileExists(SetupFile) then
        FailInstall('The Ollama download is missing. The installation cannot continue.');
      { Size sanity check: the real installer is tens of MB. Ollama
        publishes no checksum for OllamaSetup.exe, so no hash is pinned. }
      SizeOk := False;
      if Exec(ExpandConstant('{cmd}'), '/c powershell -NoProfile -Command "if ((Get-Item ''' + SetupFile + ''').Length -gt 1000000) { exit 0 } else { exit 1 }"', '', SW_HIDE, ewWaitUntilTerminated, Code) then
        SizeOk := (Code = 0);
      if not SizeOk then
        FailInstall('The downloaded Ollama installer failed its size check (it is far smaller than the real installer). The download may be incomplete or tampered with.');
      if not Exec(SetupFile, '/VERYSILENT /SUPPRESSMSGBOXES /NORESTART', '', SW_HIDE, ewWaitUntilTerminated, Code) or (Code <> 0) then
        FailInstall('Ollama installation failed (exit code ' + IntToStr(Code) + ').');
      DeleteFile(SetupFile);
      if not FileExists(OllamaExePath()) then
        FailInstall('Ollama installed but ollama.exe was not found where expected.');
    end;

    { Start Ollama (it normally auto-starts on Windows; this is a
      belt-and-braces for a fresh install) and poll until it answers.
      SECURITY (Phase 7, finding F2): pin Ollama to loopback via
      OLLAMA_HOST so a user-set OLLAMA_HOST=0.0.0.0 cannot expose the
      unauthenticated local API to the network. }
    Exec(ExpandConstant('{cmd}'), '/c set OLLAMA_HOST=127.0.0.1:11434 && "' + OllamaExeResolved() + '" serve', '', SW_HIDE, ewNoWait, Code);
    Attempt := 0;
    while (Attempt < 10) and not OllamaServerResponds() do
    begin
      Sleep(3000);
      Attempt := Attempt + 1;
    end;
    if not OllamaServerResponds() then
      FailInstall('Ollama did not start (its local API did not answer within ~30 seconds).');

    { Pull with up to 3 attempts: "ollama pull" resumes partial blob
      downloads itself, so an interrupted download resumes cleanly. }
    PullOk := False;
    for Attempt := 1 to 3 do
    begin
      if Exec(OllamaExeResolved(), 'pull ' + ModelName, '', SW_HIDE, ewWaitUntilTerminated, Code) and (Code = 0) then
      begin
        PullOk := True;
        Break;
      end;
    end;
    if not PullOk then
      FailInstall('The ' + ModelName + ' model could not be downloaded after 3 attempts. Aurora Operator needs this model to run, so the installation cannot continue. Check your internet connection and try again.');

    { Verify the model is actually present (never trust the pull's exit
      code alone): "ollama list" output must contain the model name. }
    if not OllamaListShowsModel() then
      FailInstall('Model verification failed: ''' + ModelName + ''' does not appear in ''ollama list'' after the download. The installation cannot continue.');

    { Tiny generation smoke test, best-effort only (60 s cap): a failure
      here is a WARNING, never a pass/fail gate — the list check above
      is the gate. }
    SmokeFile := ExpandConstant('{tmp}\aurora-smoke.txt');
    if Exec(ExpandConstant('{cmd}'), '/c powershell -NoProfile -Command "$p = Start-Process -FilePath ''' + OllamaExeResolved() + ''' -ArgumentList ''run'',''' + ModelName + ''',''Say OK'' -PassThru -NoNewWindow -RedirectStandardOutput ''' + SmokeFile + '''; if ($p.WaitForExit(60000)) { exit $p.ExitCode } else { $p.Kill(); exit 2 }"', '', SW_HIDE, ewWaitUntilTerminated, Code) then
    begin
      if Code <> 0 then
        MsgBox('Warning: the model smoke test did not complete cleanly (exit code ' + IntToStr(Code) + '). The model is still listed by ''ollama list''; if the app cannot chat, re-download the model from Model Manager.',
          mbInformation, MB_OK);
    end;
    DeleteFile(SmokeFile);
  end;

  if CurStep = ssPostInstall then
  begin
    { Installation health check: app exe present AND the model still
      listed. A failure must NOT report success. Phase 8: the old code
      only showed a MsgBox and setup still reported success. A bare
      Abort() cannot fix this — per the Inno Setup help, Abort() only
      exits Setup from InitializeSetup, InitializeWizard,
      CurStepChanged(ssInstall), InitializeUninstall,
      CurUninstallStepChanged(usAppMutexCheck/usUninstall) and
      PrepareToInstall; ssPostInstall is NOT in that list, so Abort()
      here would silently continue. Instead the failure sets
      HealthCheckFailed and closes the wizard, which starts Inno's
      normal cancellation: installed files are removed and setup exits
      with failure. CancelButtonClick below suppresses the "Exit Setup?"
      confirmation for this programmatic cancel, so the user cannot
      accidentally confirm their way back to a success screen.
      This path has NOT been executed on Windows yet — the first Windows
      test must force a health-check failure and confirm the app files
      are gone and setup reports failure (see tests/windows). }
    if not FileExists(ExpandConstant('{app}\{#AppExe}')) then
    begin
      HealthCheckFailed := True;
      MsgBox('Installation health check failed: the app executable is missing after install. Setup will now cancel and remove the installed files.',
        mbError, MB_OK);
      WizardForm.Close;
    end
    else if not OllamaListShowsModel() then
    begin
      HealthCheckFailed := True;
      MsgBox('Installation health check failed: the ' + ModelName + ' model is no longer listed by ''ollama list''. Setup will now cancel and remove the installed files. Re-download the model from Model Manager after a successful install.',
        mbError, MB_OK);
      WizardForm.Close;
    end;

    { Optional Chromium for web research (task selected by the user).
      Best-effort only: a failure is a warning, and BrowserTools will
      still report "Chromium missing" honestly. }
    if WizardIsTaskSelected('chromium') then
    begin
      if not FileExists(ExpandConstant('{app}\playwright.ps1')) then
      begin
        MsgBox('Warning: playwright.ps1 was not found next to the app, so Chromium was not installed. Web research will report that Chromium is missing until you run ''playwright install chromium'' yourself.',
          mbInformation, MB_OK);
      end
      else if not Exec('powershell.exe', '-NoProfile -ExecutionPolicy Bypass -File "' + ExpandConstant('{app}\playwright.ps1') + '" install chromium', '', SW_HIDE, ewWaitUntilTerminated, Code) or (Code <> 0) then
      begin
        MsgBox('Warning: Chromium installation finished with exit code ' + IntToStr(Code) + '. Web research will report that Chromium is missing until it is installed.',
          mbInformation, MB_OK);
      end;
    end;
  end;
end;

{ Phase 8: when the post-install health check fails, setup cancels
  itself via WizardForm.Close. Without this handler Inno would ask
  "Exit Setup?" and a user answering "No" would land back on a setup
  that then reports success. With HealthCheckFailed set, the cancel
  proceeds without confirmation. Normal user cancels still confirm. }
procedure CancelButtonClick(CurPageID: Integer; var Cancel, Confirm: Boolean);
begin
  if HealthCheckFailed then
    Confirm := False;
end;

procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
begin
  if CurUninstallStep = usUninstall then
  begin
    if MsgBox('Keep the downloaded Ollama models (qwen3)?' + #13#10#13#10 +
      'Yes = keep the models on disk.' + #13#10 +
      'No = delete the downloaded models as well.',
      mbConfirmation, MB_YESNO) = IDNO then
    begin
      DelTree(ExpandConstant('{userprofile}\.ollama\models'), True, True, True);
    end;
    { %LOCALAPPDATA%\AuroraOperator (local memory database, logs) is
      deliberately left in place: it is the user's own data. Delete it
      manually if the machine is being handed on. }
  end;
end;
