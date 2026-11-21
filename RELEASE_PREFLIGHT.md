# RELEASE PREFLIGHT — Aurora Operator 0.2.0

Date: 2026-11-21. Purpose: one accurate, evidence-backed engineering
baseline before the first real Windows test. No ZIP was packaged in
this preflight. Nothing in this document claims Windows runtime
verification — every Windows-only item is marked **NOT VERIFIED**.

## 1. Verified facts (with evidence)

### 1.1 Version — 0.2.0, consistent everywhere checked

| Location | Evidence |
|---|---|
| `Directory.Build.props:11` | `<Version>0.2.0</Version>` |
| `src/AuroraOperator.App/AuroraOperator.App.csproj:13` | `<Version>0.2.0</Version>` |
| `src/AuroraOperator.Installer/AuroraOperator.nsi:74` | `APP_VERSION "0.2.0"` |
| `src/AuroraOperator.Installer/InnoSetup.iss:74` | `AppVersion "0.2.0"` |
| Publish output | `AuroraOperator.dll` stamped `0.2.0.0` (verified in Phase 7) |

### 1.2 Final build and test counts (re-run in this preflight, Linux VM)

| Check | Command | Timestamp (UTC) | Result |
|---|---|---|---|
| Full clean build | `dotnet build AuroraOperator.sln -c Release -p:EnableWindowsTargeting=true --no-incremental` (SDK 8.0.428, `~/.dotnet/dotnet`) | 2026-11-21T16:02:09Z | **0 errors, 40 warnings** |
| Incremental build | same command without `--no-incremental` | 2026-11-21T16:01:36Z | **0 errors, 32 warnings** |
| TestRunner | `dotnet run --project tests/AuroraOperator.TestRunner -c Release --no-build` | 2026-11-21T16:03:28Z | **184 passed, 0 failed, 184 total** |
| `dotnet test` (vstest) | — | — | **0 tests run** (see §1.4) |
| Ollama presence | `curl -m 3 http://localhost:11434/api/tags` | preflight | exit 7 — no server on this VM |
| Installer compilers | `command -v makensis` / `iscc` | preflight | not found (exit 1) |

Warning composition (full clean rebuild, authoritative): **40 =
32× NU1603** (PdfPig `0.1.9-alpha001-patch1` sub-packages resolved to
`1.7.0-custom-5`; pin deliberately unchanged — the sandbox NuGet feed
offers only that prerelease and `1.7.0-custom-5`, stable 0.1.9 restores
fail with NU1103 here; fix on real nuget.org) **+ 4× CS8600**
(2 sites, `BrowserPhase7Tests.cs:75,110`, each emitted twice)
**+ 2× CS0067** (1 site, `ApprovalTests.cs:25`, emitted twice)
**+ 2× xUnit2013** (1 site, `Phase2Tests.cs:260`, emitted twice). No
warning was suppressed.
Why counts differ: 38 was Worker A's fresh build before the CS8600s
landed; 40 is the full clean rebuild; 32 is an incremental rebuild
where up-to-date projects are skipped and their warnings are not
re-emitted. The 72 raw NU1603 lines in `build.log` are the same 32
warnings re-emitted per project/pass.

### 1.3 Test-count reconciliation (why BUILD_AUDIT said 174)

`BUILD_AUDIT.md` §7.4 recorded Worker A's parallel runs (170 passed /
4 failed, 174 total, then 175/0) — intermediate, before the other
workers' test files landed and before the 4 `BrowserPhase7Tests`
failures were fixed (`build.log:1401` shows the 170/4 run). The
integration and closing runs (`build.log:1480,1518`) and this
preflight's re-run all show **184 passed / 0 failed**. 174 is
superseded; it is preserved as history in `BUILD_AUDIT.md` (tagged
SUPERSEDED), not deleted.

### 1.4 `dotnet test` — 0 tests run, environment defect (not a code failure)

vstest testhost's localhost TCP channel is redirected network-level to
this sandbox's egress proxy (`[fd8b:…]:3128`), proved with `--Diag`
logging in Phases 3–5 and recorded in `TESTS.md`. Nothing about the
environment changed in Phase 7. Run `dotnet test` on Windows.

### 1.5 Model selection and catalog (actual code values)

`src/AuroraOperator.Core/Ollama/ModelCatalog.cs`:

| Model | Size (catalog) | Min RAM | Role |
|---|---|---|---|
| `qwen3:4b` | 2,600,000,000 (~2.5 GB) | 8 GB | primary |
| `qwen3:1.7b` | 1,400,000,000 (~1.4 GB) | 4 GB | fallback |
| `qwen3.5:2b` | 2,900,000,000 (~2.9 GB) | 8 GB | vision |
| `qwen2.5-coder:3b` | 1,900,000,000 (~1.9 GB) | 6 GB | coding |
| `qwen3:0.6b` | 523,000,000 (~523 MB) | 4 GB | ultra-light |
| `qwen3:8b` | 5,200,000,000 (~5.2 GB) | 16 GB | advanced |

All tags were verified against ollama.com/library in Phase 7,
including the suspect `qwen3.5:2b` (exists, multimodal, 2.7–3.1 GB).

**Auto-selection rule (`SelectBestModel`, `ModelCatalog.cs`):**
qwen3:4b only when **total RAM ≥ 16 GB AND free RAM ≥ 6 GB**;
otherwise qwen3:1.7b. Profiles agree: 8 GB → Lightweight / 1.7b,
16 GB → Recommended / 4b, 32 GB → Advanced / 8b. Pinned by
`Phase7IntegrationTests.SelectBestModel_8GbTotal_SelectsFallback_
NotPrimary` and `SelectProfile_And_SelectBestModel_Agree_On8And16Gb`
(both passing in the 184). A user may still pick 4b manually on 8 GB
from Model Manager. Only one model is ever loaded at a time.

**Preflight fix — installer thresholds were wrong and are now
aligned.** Both installer scripts selected qwen3:4b at ≥ 8 GB
(`AuroraOperator.nsi` RAM ≥ "8"; `InnoSetup.iss` `RamGb >= 8`),
contradicting the rule above and `README.md`/`BUILD.md`/`TESTS.md`.
Both now select 4b at ≥ 16 GB, 1.7b at 8–15 GB, 0.6b below 8 GB
(and on RAM-detection failure). New constants:
`MODEL_SIZE_06B_MB 523` (NSIS), `ModelSize06bMB = 523` (Inno).
This is the one behaviour change in this preflight, made because the
implementation contradicted the intended documented profiles. It has
**not** been executed — verify on Windows (8 GB VM pulls 1.7b,
16 GB VM pulls 4b).

### 1.6 Publish outputs (re-verified in this preflight)

| Output | Size | Entries | Contents verified |
|---|---|---|---|
| `publish/win-x64-framework-dependent/` | 224 MB | 31 top-level (358 files recursively) | PE32+ GUI x86-64 `AuroraOperator.exe`; `.dll`, `.deps.json`, `runtimeconfig.json` (net8.0, `Microsoft.NETCore.App` + `Microsoft.WindowsDesktop.App` 8.0.0, no explicit RollForward — default Minor); `e_sqlite3.dll` at root |
| `publish/win-x64-self-contained/` | 384 MB | 281 top-level (816 files recursively) | Same checks; bundles .NET 8.0.34; `e_sqlite3.dll` at root |
| `src/AuroraOperator.App/bin/Release/net8.0-windows/win-x64/publish/` | 384 MB | — | File listing identical to `publish/win-x64-self-contained/`; **this is the path both installer scripts copy from** |

**Reconciled recommendation: self-contained first**
(`publish/win-x64-self-contained/`). It needs no .NET runtime on the
target, so the install cannot fail on a missing runtime, and it is
what the installers ship. Framework-dependent is the alternative
only with the .NET 8 Desktop Runtime installed by the installer.
(`BUILD_AUDIT.md` / `publish/README.md` previously recommended
framework-dependent first — SUPERSEDED.)

Layout facts: there is **no `runtimes/` folder** in either output —
native assets are flattened (`e_sqlite3.dll` at the output root,
Playwright driver under `.playwright/node/win32_x64`). Each output
carries **205 MB of `.playwright`** (`node/linux-x64` ~115 MB +
`node/win32_x64` ~80 MB); the Linux half is dead weight on Windows
in both outputs, not only the framework-dependent one. The app itself
is ~19 MB. A PE32+ exe proves the code compiled and linked for
win-x64 — **it does not prove the app runs on Windows**.

### 1.7 Installer scripts (verified by reading the code, not by running)

- **Version:** both 0.2.0 (§1.1). **Output filenames now distinct
  (preflight fix):** NSIS →
  `AuroraOperator-Setup-NSIS-0.2.0.exe`; Inno →
  `AuroraOperator-Setup-Inno-0.2.0.exe`. Previously both wrote
  `AuroraOperator-Setup-0.2.0.exe` into
  `src/AuroraOperator.Installer/`, so compiling the second silently
  overwrote the first.
- **Source paths:** both copy the default-location self-contained
  publish — NSIS `File /r "..\AuroraOperator.App\bin\Release\
  net8.0-windows\win-x64\publish\*.*"` (`AuroraOperator.nsi:368`);
  Inno `Source: "..\AuroraOperator.App\bin\Release\net8.0-windows\
  win-x64\publish\*"` (`InnoSetup.iss:105`). The publish output
  required by those paths exists (384 MB, §1.6).
- **Admin:** both require admin (NSIS `RequestExecutionLevel admin`,
  Inno `PrivilegesRequired=admin`) — justified in the scripts by
  Program Files install + uninstall keys.
- **Model download / verification / rollback — consistent for the
  pre-install gates, divergent for the post-install check:**
  - Both: poll `ollama list` until the server answers (~30 s cap);
    pull with **3 attempts** (`ollama pull` resumes partial blobs);
    verify with `ollama list` containing the model name; tiny
    `ollama run <model> "Say OK"` smoke test (60 s cap, best-effort
    warning only, never a pass/fail gate).
  - NSIS: any failure calls `Rollback` (`AuroraOperator.nsi:183-190`
    — deletes `$INSTDIR` and temp files, then aborts).
  - Inno: failures in `ssInstall` call `FailInstall` → `Abort()`
    before any app file is copied (`InnoSetup.iss:305-307`), so no
    partial install is left.
  - **Divergence:** the post-install health check aborts/rolls back
    in NSIS (`AuroraOperator.nsi:396-397`) but in Inno only shows a
    `MsgBox` and setup still reports success
    (`InnoSetup.iss:413-418`). First-Windows-test requirement: decide
    whether to accept the advisory check or fix it after verifying
    `Abort()` semantics in `ssPostInstall` on Windows.
  - **"Rollback" removes app files only in both scripts.** Neither
    uninstalls Ollama nor deletes the downloaded model on failure —
    recorded, not hidden; arguably correct (shared, reusable state),
    but it is not a full rollback.
- **Ollama binding — verified present in both scripts:**
  `AuroraOperator.nsi:296-299` (`OLLAMA_HOST=127.0.0.1:11434` via
  `SetEnvironmentVariable` before serve) and
  `InnoSetup.iss:362-364` (`set OLLAMA_HOST=127.0.0.1:11434 &&
  ollama serve`). App-side enforcement also present:
  `OllamaClient.cs:43-60` (`ValidateLoopbackOnly` — rejects 0.0.0.0,
  LAN and remote hosts; default `http://localhost:11434`).
  Ollama is **not** exposed to the local network by this code.
- **Installers NOT compiled or executed** — `makensis`/`iscc` are
  not installed on this Linux VM. Do not claim they work.

### 1.8 Browser download containment — verified present

`BrowserTools.cs:196-205` (`DefaultDownloadDirectory` = the user's
Downloads folder, never derived from the requested path),
`:266-330` (`ValidateDownloadPath` — absolute path, file name
required, no `..` escape, NUL rejected), `:348-379` (download action
enforces URL + path validation). Chromium itself has **not** been run.

### 1.9 Security review status

`SECURITY_REVIEW.md`: **10 PASS, 4 PARTIAL, 6 FIXED, 0 FAIL.**
Fixed in Phase 7 and re-verified present in this preflight where
cited above: F2 (installer Ollama loopback), F8 (download
containment), F18 (OllamaClient loopback enforcement), F19 (stale
panel click). Remaining PARTIALs: **F5** (app launch
`UseShellExecute=true`), **F13** (residual free-text secrets in
logs), **F14** (plaintext SQLite), **F20** (`close-app` can kill any
process by name) — see §3 for classification.

## 2. Remaining Windows-only tests

All items below are **NOT VERIFIED**. The full 15-item guide with
exact steps, expected results and FAIL criteria is
`tests/windows/WINDOWS_TESTING_GUIDE.md`; the tick-off sheet is
`tests/windows/WINDOWS_ACCEPTANCE_CHECKLIST.md`.

1. Application launch (double-click the published exe / installed app).
2. Ollama detection (fresh machine, no Ollama → installer installs it).
3. Model download (8 GB VM → qwen3:1.7b; 16 GB VM → qwen3:4b —
   the preflight installer-threshold fix must be checked here).
4. Real AI inference (chat a question, get a reply from the local model).
5. Notepad launch via the agent.
6. Actual text entry (typed text appears in Notepad).
7. Text verification (read-back matches the input exactly).
8. File saving (Save dialog → file on disk, content identical).
9. Approval and rejection (sensitive action pauses, shows exact
   command, Approve executes it, Reject prevents it, expiry never
   auto-executes).
10. Task cancellation (Stop mid-task → no further actions execute).
11. Browser automation (Chromium install, navigation, extraction,
    download into Downloads only).
12. Memory persistence (restart the app → tasks/history still there).
13. Offline operation (disconnect internet after install → local AI
    still works).
14. Application restart (no orphan processes, state recovers).
15. Uninstallation (with and without keeping models).

Plus: `dotnet test` on Windows; NSIS + Inno compilation and full
install runs (including failure paths: no internet, interrupted
model download, low disk, Windows build < 19045); native DLL loading
from the flattened publish layout; PdfPig stable-0.1.9 pin clearing
the NU1603s on real nuget.org.

## 3. Remaining risks — classified

| # | Risk | Class | Justification |
|---|---|---|---|
| R1 | Ollama HTTP retry/backoff missing (`OllamaClient.cs` has no retry/`Delay`; verified by grep) | **Post-test improvement** | Single-attempt failure is surfaced honestly as an error; agent step-level retry exists. A transient network/Ollama failure fails one call, it does not corrupt state. |
| R2 | No explicit model warm-up call (load happens implicitly on first chat/generate) | **Post-test improvement** | First inference is slower, not wrong. Add a warm-up call only after confirming first-load latency on Windows. |
| R3 | Windows native DLL loading unverified (flattened layout, no `runtimes/` folder) | **First-Windows-test requirement** | App launch is the first test item; if `e_sqlite3.dll` / Playwright driver fail to load, the app fails immediately and visibly. Must be checked in test 1. |
| R4 | Playwright Chromium not run on Windows | **First-Windows-test requirement** | Browser research is a headline feature; test 11 covers it. Missing Chromium must be reported honestly by the app, never faked. |
| R5 | Framework-dependent publish ships ~115 MB of Linux Playwright Node (205 MB `.playwright` in both outputs) | **Post-test improvement** | Dead weight only; the self-contained recommendation is unaffected. Trim before release, not before the first test. |
| R6 | Security PARTIAL F5 (app launch `UseShellExecute=true`) | **Accepted residual risk** (product decision) | Launching arbitrary executables by path is inherent to an operator; mitigated by approval gating. Allowlist is a post-test product decision. |
| R7 | Security PARTIAL F13 (residual free-text secrets in logs) | **First-Windows-test requirement** | `RedactSecrets` covers known patterns; free-text secrets (e.g. pasted in a prompt) can still land in logs. Check the log after the Windows pass; do not ship logs containing secrets. |
| R8 | Security PARTIAL F14 (plaintext SQLite) | **Accepted residual risk** (with remediation planned) | Database is local, per-user, under `%LOCALAPPDATA%`; DPAPI encryption is the documented follow-up. Acceptable for a first test on a trusted machine; not acceptable to call "production ready". |
| R9 | Security PARTIAL F20 (`close-app` can kill any process by name) | **First-Windows-test requirement** | Destructive by design; verify the approval gate actually fires for `close-app` in test 9 before trusting it. |
| R10 | Model switching while the engine is already running unverified | **First-Windows-test requirement** | Known from Phases 1–6: a switch takes effect for new engine runs / after restart. Confirm the behaviour on Windows; do not assume a live switch works. |
| R11 | Installer model-threshold fix (preflight) unexecuted | **First-Windows-test requirement** | The fix changes which model a fresh install downloads. Test 3 on both an 8 GB and a 16 GB VM is the gate. |
| R12 | Inno post-install health check advisory-only (diverges from NSIS rollback) | **First-Windows-test requirement** | A failed post-install check must not be mistaken for success. Decide on Windows whether to accept it or fix it. |
| R13 | PdfPig pin still prerelease (`0.1.9-alpha001-patch1`, 32 NU1603 warnings) | **Post-test improvement** | Build succeeds; PDF extraction works against the resolved `1.7.0-custom-5` sub-packages in tests. Pin stable 0.1.9 on real nuget.org after the Windows pass. |

**Release blockers: none for a first Windows *test* attempt.**
Nothing above blocks running the acceptance pass on a test machine,
provided the GO conditions in §6 are met. There is no GO for
"production ready" — that decision requires the Windows pass first.

## 4. Exact files and folders to transfer to a Windows test machine

Transfer the whole repository directory, or at minimum:

```
aurora-operator/
  AuroraOperator.sln
  Directory.Build.props
  README.md  BUILD.md  TESTS.md  LIMITATIONS.md  DEPENDENCIES.md
  TROUBLESHOOTING.md  BUILD_AUDIT.md  PHASE7_REPORT.md
  PHASES_1-6_REPORT.md  SECURITY_REVIEW.md  RELEASE_PREFLIGHT.md
  build.log
  src/AuroraOperator.Core/
  src/AuroraOperator.App/
  src/AuroraOperator.Installer/     (AuroraOperator.nsi, InnoSetup.iss)
  tests/AuroraOperator.Tests/
  tests/AuroraOperator.TestRunner/
  tests/windows/                    (guide, checklist, diagnostics, .ps1)
  publish/win-x64-self-contained/   (270 MB — FIRST test target; Phase 8 trim)
  publish/win-x64-framework-dependent/  (109 MB — alternative only)
```

Do **not** transfer `bin/`/`obj/` as the source of truth — rebuild on
Windows (see §5). The `publish/` folders are Linux cross-published;
they are the first launch target, but the installer scripts copy from
`src/AuroraOperator.App/bin/Release/net8.0-windows/win-x64/publish/`,
so a Windows `dotnet publish` must populate that path before running
`makensis`/`iscc` (or copy `publish/win-x64-self-contained/` there).

## 5. Exact commands to run on Windows

```powershell
# 0. Preconditions: Windows 10 22H2+ / Windows 11 x64.
#    Install .NET 8 SDK (VS 2022 with .NET desktop workload recommended)
#    and NSIS (makensis) and/or Inno Setup 6 (iscc) as needed.
#    Run from the transferred aurora-operator\ folder.

# 1. Diagnostics first (read-only; writes only its own report file)
powershell -NoProfile -ExecutionPolicy Bypass -File tests\windows\windows-diagnostics.ps1

# 2. Build + unit tests (expect: 0 errors; warnings ~40, all NU1603 PdfPig
#    until the stable pin is applied; TestRunner 248/0 as of Phase 8)
dotnet build AuroraOperator.sln -c Release
dotnet test tests\AuroraOperator.Tests -c Release
dotnet run --project tests\AuroraOperator.TestRunner -c Release --no-build

# 3. Publish the self-contained first test target
dotnet publish src\AuroraOperator.App -c Release -r win-x64 --self-contained true
#    (populates src\AuroraOperator.App\bin\Release\net8.0-windows\win-x64\publish\)
#    Framework-dependent alternative:
#    dotnet publish src\AuroraOperator.App -c Release -r win-x64 --self-contained false

# 4. Launch the published app directly (first Windows launch test)
.\src\AuroraOperator.App\bin\Release\net8.0-windows\win-x64\publish\AuroraOperator.exe

# 5. Ollama + model (do this before judging any AI behaviour)
ollama --version
curl.exe http://localhost:11434/api/tags
ollama list
#    Expect: qwen3:0.6b below 8 GB, qwen3:1.7b on an 8 GB VM, qwen3:4b on a
#    16 GB VM, qwen3:8b on a 32 GB VM (Phase 8 alignment; free-RAM gates
#    also apply — see PHASE8_REPORT.md §3).
#    If the model is missing: ollama pull qwen3:1.7b   (or qwen3:4b)
ollama run qwen3:1.7b "Say OK"     # smoke test; expect a reply

# 6. Follow the 15-item pass in tests\windows\WINDOWS_TESTING_GUIDE.md
#    and tick off tests\windows\WINDOWS_ACCEPTANCE_CHECKLIST.md.

# 7. Build the installers (from src\AuroraOperator.Installer, after step 3)
cd src\AuroraOperator.Installer
makensis AuroraOperator.nsi        # AuroraOperator-Setup-NSIS-0.2.0.exe
iscc InnoSetup.iss                 # AuroraOperator-Setup-Inno-0.2.0.exe
#    Run each installer on a clean VM (one with, one without Ollama
#    pre-installed), including the failure paths in the guide.

# 8. Uninstall test (last item): uninstall from Settings/Apps, then
#    check no AuroraOperator processes remain and the model-keep /
#    model-delete choice behaved as selected.
```

## 6. Expected results and failure conditions — GO / NO-GO

**Expected (GO for the first test attempt):** diagnostics script
passes its environment checks; solution builds with 0 errors;
TestRunner reports 248/0 (or more, if tests were added — never fewer
without an explanation); the published exe launches and shows the
Aurora Operator window; Ollama answers on `127.0.0.1:11434` and the
correct model for the machine's RAM is listed; the 15-item guide can
be worked through item by item.

**Failure conditions (NO-GO for calling the test a pass — stop and
report, do not work around silently):** the app does not launch or
crashes on launch; a native DLL fails to load from the flattened
layout; Ollama cannot be installed / does not answer / the wrong
model is pulled on 8 GB or 16 GB; any approval-gate test fails
(Approve does not execute, Reject still executes, or an expired
approval executes); `dotnet test` failures that are not the sandbox
proxy issue; an installer that reports success after a failed model
download, or an Inno install that reports success after a failed
post-install health check.

**Decision: CONDITIONAL GO for the first Windows test attempt only.**
The code, publish outputs, installer scripts (as reviewed, uncompiled)
and test package are ready to be exercised on a real Windows machine
under the guide in `tests/windows/`. This is **not** a GO for
production release, and it is **not** a claim that the installer works
— the installers have never been compiled or executed, the app has
never run on Windows, and no real Ollama inference has ever been
observed. The Windows pass is the next gate.

## 7. Files changed in this preflight

| File | Change |
|---|---|
| `src/AuroraOperator.Installer/AuroraOperator.nsi` | Model thresholds aligned (4b ≥ 16 GB, 1.7b 8–15 GB, 0.6b < 8 GB); added `MODEL_SIZE_06B_MB 523`; `OutFile` → `AuroraOperator-Setup-NSIS-0.2.0.exe` |
| `src/AuroraOperator.Installer/InnoSetup.iss` | Same threshold alignment; added `ModelSize06bMB = 523`; `OutputBaseFilename` → `AuroraOperator-Setup-Inno-0.2.0.exe` |
| `PHASE7_REPORT.md` | §11 output filenames corrected; new §12 preflight reconciliation (all 7 contradictions + 6 new findings + final counts) |
| `BUILD_AUDIT.md` | Reconciliation preamble; 8 passages tagged [SUPERSEDED — preflight] with reasons and evidence |
| `publish/README.md` | Recommendation corrected to self-contained first; installer-version line corrected to 0.2.0; `.playwright` note corrected (205 MB in both outputs, recursive file counts); dead `WINDOWS_VERIFICATION_CHECKLIST.md` links fixed to `tests/windows/WINDOWS_TESTING_GUIDE.md` |
| `BUILD.md` | Installer output filenames corrected |
| `README.md` | Installer filename corrected (NSIS / Inno variants) |
| `LIMITATIONS.md` | "qwen3:4b default" clarified to primary-on-16 GB / 1.7b-auto-on-8 GB |
| `RELEASE_PREFLIGHT.md` | New — this file |
| `build.log` | Preflight build + TestRunner output appended (history preserved) |

## 8. Phase 8 addendum (2026-11-21) — read before the Windows test

Phase 8 changed the baseline. The preflight decision stands
(**CONDITIONAL GO for the first Windows test attempt only; NO-GO
for production release** — see `PHASE8_REPORT.md` §13), but these
facts supersede the preflight where they differ:

- **Tests:** TestRunner is now **248 passed / 0 failed** (was 184).
  `dotnet test` still runs 0 tests on the Linux sandbox (proxy
  defect re-proved 2026-11-21T17:47Z); it must execute real tests on
  Windows.
- **Model selection:** qwen3:8b **is** auto-selected at 32 GB total
  + ≥ 10 GB free (the preflight's "32 GB → 8b" was a profile claim
  the code did not implement — fixed in Phase 8); below 8 GB the
  app, profiles and installers all select qwen3:0.6b. Installers
  now also select 8b at ≥ 32 GB (5,200 MB). The installers measure
  total RAM only; the app also gates on free RAM.
- **Publish sizes:** self-contained is now **270 MB** (was 384 MB),
  framework-dependent **109 MB** (was 224 MB) — the Linux Playwright
  Node (~115 MB) is excluded at publish time. The empty
  `.playwright/node/linux-x64` directory remains (0 bytes).
- **Inno health check:** the advisory-only MsgBox is gone. On
  failure the script now sets `HealthCheckFailed` and calls
  `WizardForm.Close` to cancel setup (a bare `Abort()` in
  `ssPostInstall` would silently continue — verified against the
  official Inno Setup Abort documentation). **Not executed yet:**
  force a health-check failure on Windows and confirm the app files
  are removed and setup reports failure.
- **Security:** F5 (launch allowlist) and F20 (close-app guard) are
  FIXED in code with Linux tests; F13/F14 are PARTIAL (improved).
  Updated verdict: 10 PASS, 2 PARTIAL, 8 FIXED, 0 FAIL. The DPAPI
  preference protection and DB ACL paths have not executed.
- **Ollama retry:** bounded retry (3 attempts, 250/500 ms, 5xx /
  network / timeout only, never 4xx or user cancellation) is now in
  `OllamaClient`. No warm-up request was added.
- **Warnings:** still 0 errors, 40 warnings on a clean rebuild
  (32× NU1603 PdfPig + 4× CS8600 + 2× CS0067 + 2× xUnit2013).
- **Full detail:** `PHASE8_REPORT.md`. New Windows procedures
  (Inno forced-failure test, 32 GB model check, DPAPI/ACL checks,
  close-app and launch-allowlist bypass attempts) are in
  `tests/windows/WINDOWS_TESTING_GUIDE.md` (Phase 8 additions) and
  `tests/windows/WINDOWS_ACCEPTANCE_CHECKLIST.md` (Phase 8 section).

## Final release addendum (2026-11-21)

The preflight baseline above is superseded by
`release/FINAL_RELEASE_REPORT.md` for everything that changed in the final
release: TestRunner 184 → 248 (Phase 8) → **263 passed / 0 failed**; clean
build 0 errors / 40 warnings; publish re-measured 270 MB / 109 MB from the
final tree; and the NSIS installer is now **actually compiled**
(`release/AuroraOperator-Setup-0.2.0-x64.exe`, 110,168,296 bytes,
SHA-256 `a185abb1e47303a7801f1b45c694b2aef3b86777ef16e54b0ee590523c807f97`).
The Inno installer is still uncompiled (Windows-only compiler). The
decision remains **CONDITIONAL GO for the first Windows test attempt only**;
installer execution, Ollama, Chromium and Notepad on Windows are still
BLOCKED and are the user's test, per `release/WINDOWS_TEST_RESULTS.md`.
