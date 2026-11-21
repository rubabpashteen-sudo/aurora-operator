# PHASE 8 — Windows Acceptance Testing & Release Hardening

**Aurora Operator 0.2.0 — 2026-11-21. No ZIP packaged.**

## 0. Environment and repo state (Part 1 + Part 2)

This phase ran on a **Linux VM, not Windows**, and every Windows-only
item below is therefore **BLOCKED** or **NOT TESTED** — nothing in this
report claims a Windows runtime result.

| Item | Actual value (measured 2026-11-21) |
|---|---|
| OS | Ubuntu 24.04.5 LTS, Linux htch-runtime 7.0.0-44-generic, x86_64 |
| CPU | 4 vCPU (shared), 7.7 GB RAM total / ~0.6 GB available at test time, 148 GB free disk |
| .NET SDK | 8.0.428 (`~/.dotnet/dotnet --info`); runtimes 8.0.34 (Microsoft.NETCore.App + AspNetCore) |
| PowerShell | not installed (`pwsh`/`powershell` absent) |
| Ollama | not installed; `curl -m 3 http://localhost:11434/api/tags` → exit 7 (no server) |
| Chromium / Playwright | no Chromium binary on this VM; Playwright 1.50.0 package only |
| NSIS / Inno compilers | `makensis` / `iscc` not found — **installers NOT compiled, NOT executed** |
| Git | **not a git repository** (`git status` → "not a git repository"); no history was initialised — do not treat the absence of git as a clean tree |
| App version | 0.2.0 everywhere checked (`Directory.Build.props`, App csproj, both installer scripts) |

**What this environment can genuinely verify:** repository
inspection, Linux-compatible tests (TestRunner), cross-builds
(`EnableWindowsTargeting=true`), code-level fixes and their tests.
**What it cannot:** WPF runtime, Notepad UI Automation, Chromium
launch, Ollama inference, installer compilation/execution, DPAPI on
Windows, network binding on Windows. Those are BLOCKED in §12.

**Report-vs-code differences found (Part 1):** the Phase 7 reports
were reconciled in the preflight, but this phase still found live
contradictions the preflight missed — see §1. All of them are fixed
or documented below; no superseded statement is presented as current.

## 1. Contradictions found in Phase 8 (and how each was resolved)

### 1.1 The 32 GB profile never auto-selected qwen3:8b — FIXED
`ModelCatalog.Profiles` said "Advanced, 32 GB → qwen3:8b", the
preflight report repeated "32 GB → 8b", and the Phase 8 brief's table
says the same — but `SelectBestModel` capped at qwen3:4b: it had no
8b branch at all. Neither installer selected 8b either (both capped
at 4b). So 8b was a recommended/manual option only, while every
document described it as the 32 GB profile. **Fix:**
`SelectBestModel` now returns qwen3:8b at ≥ 32 GB total **and**
≥ 10 GB free; the installers select 8b at ≥ 32 GB total (5,200 MB).
The free-RAM gate is deliberate and documented in code: a 32 GB
machine with only 5 GB free falls back to 1.7b rather than loading a
5.2 GB model into a nearly-full machine.

### 1.2 Below 8 GB the app and the installers disagreed — FIXED
The installers selected qwen3:0.6b below 8 GB, but
`SelectBestModel` returned qwen3:1.7b there (pinned by an old test),
and `SelectProfile` had no sub-8 GB tier at all (it returned the
1.7b Lightweight profile for a 4 GB machine). A 4 GB user would
therefore get 0.6b from the installer and then have the app's
`EnsureBestModelAsync` download 1.7b over it. **Fix:** a new
`Minimal` profile (0 GB, qwen3:0.6b, 4K context) and
`SelectBestModel` returning 0.6b below 8 GB total. Two old tests
that pinned the contradictory behaviour were updated with the
reason recorded in the test itself
(`AuroraTests.SelectBestModel_4Gb_SelectsUltraLight`,
`OllamaPhase7Tests` profile test). `EnsureBestModelAsync` now also
applies the selected profile's context window (previously the 8192
default was used on every machine, including sub-8 GB ones).

### 1.3 Inno post-install health check — FIXED in code, BLOCKED in verification
Phase 7/preflight recorded it as advisory-only (MsgBox, setup still
reports success). The Phase 8 investigation verified **why** from
the official Inno Setup documentation (Abort reference,
documentation.help/Inno-Setup): `Abort()` only exits Setup when
called from `InitializeSetup`, `InitializeWizard`,
`CurStepChanged(ssInstall)`, `InitializeUninstall`,
`CurUninstallStepChanged(usAppMutexCheck/usUninstall)` or
`PrepareToInstall`. `ssPostInstall` is **not** in that list, so a
bare `Abort()` there would silently continue — it is not a fix.
**Fix implemented:** on health-check failure the script sets a
`HealthCheckFailed` flag, shows the error, and calls
`WizardForm.Close`, which starts Inno's normal cancellation (installed
files removed, setup exits with failure); a new
`CancelButtonClick` handler suppresses the "Exit Setup?"
confirmation for this programmatic cancel only (normal user cancels
still confirm). **This path has never executed** — the first Windows
test must force a health-check failure and confirm the app files are
gone and setup reports failure (checklist item added, §12).

### 1.4 Rollback scope — confirmed intentional and documented
Both installers' rollback removes app files only; Ollama and the
downloaded models persist. This is intentional (Ollama is a shared
install, models are multi-GB user data) and both uninstallers ask
whether to keep or delete models. It is now stated in the installer
headers, the testing guide and LIMITATIONS.md, instead of being
implied.

### 1.5 `.playwright` dead weight — FIXED by publish-time exclusion
Preflight said 205 MB in both outputs. Measured again: linux-x64
Node 115 MB + win32_x64 Node 80 MB + package 11 MB. A publish-time
MSBuild target in `AuroraOperator.App.csproj`
(`RemoveNonWindowsPlaywrightNodeFromPublish`, runs on
`ComputeFilesToPublish` for `win-x64` only) removes the Linux Node
from the publish items — never by hand-deleting files. Results in
§11. The empty `linux-x64` directory remains (0 bytes); that is
harmless.

## 2. Build and test validation (Part 3)

| Check | Command | Time (UTC) | Result |
|---|---|---|---|
| Baseline clean build | `dotnet build AuroraOperator.sln -c Release -p:EnableWindowsTargeting=true --no-incremental` | 2026-11-21T17:46:16Z | **0 errors, 40 warnings** |
| Baseline TestRunner | `dotnet run --project tests/AuroraOperator.TestRunner -c Release --no-build` | 2026-11-21T17:46Z | **184 passed, 0 failed** |
| Final clean build | same build command | 2026-11-21T18:00:32Z | **0 errors, 40 warnings** |
| Final TestRunner | same runner command | 2026-11-21T18:00:32Z | **248 passed, 0 failed, 248 total** |
| `dotnet test` | `timeout 120 dotnet test tests/AuroraOperator.Tests -c Release --no-build --diag /tmp/vstest-diag-p8/diag.log` | 2026-11-21T17:47Z | **0 tests run — Test Run Aborted** (see below) |

**Warning composition (final clean build, 40):** 32× NU1603
(PdfPig — see below) + 4× CS8600 (`BrowserPhase7Tests.cs:75,110`,
each emitted twice) + 2× CS0067 (`ApprovalTests.cs:25`, twice) +
2× xUnit2013 (`Phase2Tests.cs:260`, twice). One new warning
appeared during this phase (CS0168 in the new retry code, an unused
catch variable) and was **fixed by removing the variable, not
suppressed** — the count is back to 40. No warning was suppressed
anywhere.

**`dotnet test` — 0 tests run, environment defect re-proved.**
The test project configuration is correct
(`Microsoft.NET.Test.Sdk` 17.8.0, `xunit` 2.5.3,
`xunit.runner.visualstudio` 2.5.3, `coverlet.collector` 6.0.0,
`IsTestProject=true`). The retry with `--diag` produced the same
signature as Phases 3–5 and 7: testhost starts, then its diag log
shows `TcpClientExtensions.MessageLoopAsync: Polling on
remoteEndPoint: [fd8b:4f84:7d32:99::1]:3128` — the sandbox egress
proxy intercepts testhost's localhost TCP channel — and
`vstest.console` aborts after 90 s ("failed to connect to testhost").
No test was removed or altered to obtain a pass; the same 248 tests
pass in the in-process TestRunner. **On a real Windows machine
`dotnet test` must be run and must execute actual tests** — it is
checklist item 3, BLOCKED here.

**PdfPig NU1603 — re-investigated, pin unchanged.**
Scratch restore in `/tmp/pdfpig-probe` (2026-11-21T17:47Z):
`dotnet add package UglyToad.PdfPig --version 0.1.9` →
`NU1103: Unable to find a stable package UglyToad.PdfPig with
version (>= 0.1.9)` — this sandbox's feed offers only
`1.7.0-custom-5`. Stable 0.1.9 is therefore **not** available from
the configured source here, and upgrading blindly to the custom
1.7.0 build was not attempted. The pin
`0.1.9-alpha001-patch1` stays; moving to stable 0.1.9 remains a
Windows-integrator step on real nuget.org, with a full test run
afterwards. PDF text extraction at runtime is therefore still
unverified on the real feed.

## 3. Model selection and Ollama (Part 5)

Final thresholds — all three mechanisms now agree (code + tests +
installers). `qwen3:8b` **is** auto-selected at 32 GB (with the free
RAM gate), not merely recommended:

| Hardware | `SelectBestModel` (total, free) | `SelectProfile` | NSIS / Inno installer |
|---|---|---|---|
| Below 8 GB | qwen3:0.6b | Minimal → 0.6b (4K ctx) | 0.6b (523 MB) |
| 8 GB | qwen3:1.7b | Lightweight → 1.7b | 1.7b (1,400 MB) |
| 16 GB | qwen3:4b (needs ≥ 6 GB free) | Recommended → 4b | 4b (2,600 MB) |
| 32 GB | qwen3:8b (needs ≥ 10 GB free) | Advanced → 8b | 8b (5,200 MB) |

Known remaining difference, documented in the installer scripts:
the installers measure **total RAM only**; the app also gates on
free RAM. A 32 GB machine with little free RAM therefore gets 8b
from the installer while the app may fall back to 4b at runtime —
both choices are safe, and the app's choice governs at runtime.
RAM-detection failure in both installers falls back to 0.6b
(NSIS defaults `$RAM_GB` to 4 on empty/zero PowerShell output;
Inno catches the API failure and keeps 0.6b). Insufficient disk
space: both installers compute required = model + 512 MB app
allowance and abort with an exact message before downloading.
Only one model is ever loaded at a time (single `CurrentModel`,
`SwitchModelAsync` verifies with a generation call first).

**Ollama code paths verified by mocked-HTTP tests (no server
here):** detection (`IsAvailableAsync`), `/api/tags` list, pull
with progress, load/switch with a verification generation,
`keep_alive=0` unload, chat/generate, streaming NDJSON, cancellation
during pull/stream/inference, Ollama-unavailable behaviour
(`IsAvailableAsync` → false, `SwitchModelAsync` → false, no crash).

**Bounded retry — implemented (was missing).**
`OllamaClient.WithRetryAsync`: at most 3 attempts, 250 ms then
500 ms backoff, only for transient failures — 5xx responses,
network errors, timeouts. **Never** for 4xx, never for user
cancellation. A real bug was found by the new tests while building
this: a 4xx `HttpRequestException` was indistinguishable from a
network `HttpRequestException`, so it was retried. Fixed with
distinct marker exceptions (`TransientOllamaException` /
`PermanentOllamaException`) converted back to a plain
`HttpRequestException` at the retry boundary, so callers see the
exact exception type they expect. Tests prove: 503,503,200 →
success in 3 calls; 400 → 1 call and throws; 500 × always → exactly
3 calls then throws; cancellation mid-request → 1 call and
`OperationCanceledException`. Streaming (`ChatStreamAsync`,
`PullModelAsync`) is deliberately **not** retried — a partially
consumed stream cannot be replayed. **No warm-up request was added:**
a warm-up would load a multi-GB model unexpectedly; the existing
`SwitchModelAsync`/`HealthCheckAsync` verification generation is
the warm-up, and it only runs for the model the user/machine
actually selected.

## 4. Installer compilation and installation (Part 6)

| Check | Result |
|---|---|
| NSIS compiles (`makensis`) | **BLOCKED** — compiler not installed on Linux |
| Inno compiles (`iscc`) | **BLOCKED** — compiler not installed on Linux |
| Output filenames distinct | **PASS (code inspection)** — `AuroraOperator-Setup-NSIS-0.2.0.exe` vs `AuroraOperator-Setup-Inno-0.2.0.exe` |
| Version 0.2.0 in both scripts | **PASS (code inspection)** — NSIS `:74`, Inno `:74` |
| Win version / x64 checks | **PASS (code inspection)** — NSIS checks Win10/11 + x64; Inno `MinVersion=10.0.19045`, `ArchitecturesAllowed=x64`, `PrivilegesRequired=admin` |
| Admin requirement stated | **PASS (code inspection)** — both require admin (Program Files + HKLM uninstall keys); stated in scripts, README and the testing guide |
| Disk-space detection | **PASS (code inspection)** — model + 512 MB allowance, exact failure message, aborts before downloading |
| Ollama download/install, service start, model pull + `ollama list` verification | **NOT TESTED** — never executed; code paths read and consistent between scripts |
| Shortcuts, uninstall, failure messages | **NOT TESTED** — never executed |
| Failed install does not report success | **PASS (code inspection, NSIS) / FIXED-in-code, BLOCKED verification (Inno)** — see §1.3 |
| Rollback behaviour | **PASS (code inspection)** — NSIS `Rollback` removes `$INSTDIR` + temp files then aborts; Inno cancellation removes installed files; both leave Ollama + models installed by design (see §1.4) |

Failure scenarios (insufficient disk, Ollama download failure,
service not starting, model pull failure after 3 attempts, model
verification failure, missing exe, missing model, user cancel) are
all handled in code with explicit failure paths — **none has been
executed**. They are checklist items, BLOCKED.

## 5. Ollama network security (Part 7)

Code-level verification, all present in current source:

- `OLLAMA_HOST=127.0.0.1:11434` is set by both installers before
  starting Ollama (NSIS `SetEnvironmentVariable`, Inno
  `cmd /c set OLLAMA_HOST=… && ollama serve`), so a user-set
  `OLLAMA_HOST=0.0.0.0` cannot leak the unauthenticated API.
- `OllamaClient` defaults to `http://localhost:11434` and its
  constructor rejects any non-loopback base URL
  (`ValidateLoopbackOnly` — `localhost`, `127.0.0.1`, `::1` only).
- No `0.0.0.0` in product code or installer scripts (only in
  rejection tests and diagnostics docs).
- **This is NOT proof of network binding.** The real test —
  `netstat -ano` / `Get-NetTCPConnection` showing port 11434 bound
  to 127.0.0.1 only, plus a connection attempt from another machine
  on the LAN failing — must run on Windows. It is checklist item 7,
  BLOCKED.

## 6. UI Automation and approval (Part 8)

Code audit + Linux-runnable tests. The Notepad workflow code is
sound as written: launch `notepad.exe`, wait for the window, type
via ValuePattern with SendInput fallback, read back via UI
Automation, save via the Save dialog, verify the saved file by
exact content comparison, with a labelled verified direct-write
fallback — and it fails honestly (`PlatformNotSupportedException`)
on non-Windows instead of simulating success. Approval is enforced
in the dispatcher, bound to (TaskId, ActionId, SHA256 of canonical
parameters), 5-minute expiry, reject/expire/cancel never execute,
duplicate ActionIds blocked, forged `approved` flags ignored
(Phase 1 design, re-checked, still in place). **All of the actual
UI execution is BLOCKED:** Windows 11 tabbed-Notepad launch, focus,
typing, read-back, save, approval prompts, rejection, cancellation,
expiry, duplicate-ActionId bypass attempts — none has run. The
testing guide now carries the exact Windows steps for each.

## 7. Browser and Chromium (Part 9)

Code audit + Linux-runnable validation tests. Present and tested:
Chromium readiness detection, launch/navigation/extraction/disposal
code paths, per-query error recovery, timeout and cancellation,
download execution with file existence/non-zero-size checks,
`IsBlockedPage` CAPTCHA markers (recorded as blocked, never
bypassed). Security tests added in this phase (all passing):
`javascript:` / `data:` / `file:` / `ftp:` download URLs rejected,
embedded credentials rejected, path traversal rejected, NUL-byte
paths rejected, downloads contained to the user's Downloads folder
unless an explicit in-profile `downloadDirectory` is supplied,
`IsInsideUserProfile` enforcement. No browser clicking, form
submission or login automation was added. **Chromium runtime is
BLOCKED:** no Chromium binary exists here, no browser has launched,
no real download has run.

## 8. Security hardening (Part 10)

| Finding | Before | After (Phase 8) | Status |
|---|---|---|---|
| F5 app-launch allowlist | PARTIAL — `UseShellExecute=true` on any string | **FIXED** — `AppLaunchPolicy`: friendly four auto-allowed; URLs/shell protocols/scripts/installers/shortcuts hard-rejected at `AppTools` and `WindowsAppControl` even if approved; every other target requires per-request approval via the classifier. The raw `ProcessAppControl` API is deliberately not policy-checked (cross-platform primitive used by tests); the policy lives at the agent-reachable layer — documented in code. Bypass-attempt tests pass (URL/script/.msi never reach the control). | **FIXED** |
| F13 log privacy | PARTIAL — key=value + Bearer redaction; full user request logged | **PARTIAL (improved)** — redaction extended (PEM private-key blocks, Basic auth, `access_token`/`refresh_token`, `client_secret`, `x-api-key`); the engine no longer logs the full user request (mode/language/length only). Residual: a secret as bare free prose with no key marker still cannot be caught by any regex — callers must not log user-supplied secrets. Not claimed as complete protection. | **PARTIAL** |
| F14 plaintext SQLite | PARTIAL — plaintext DB, key-only secret filter | **PARTIAL (improved)** — preference values are now DPAPI-protected on Windows (`SecretProtector`, `dpapi1:` marker, CurrentUser scope) with a documented migration/recovery plan; preference values that look like credentials are refused; the DB file ACL is restricted to the current user on Windows (best-effort, logged on failure). Residual: conversations and tasks remain plaintext **by design** (memory is a feature); a pasted secret in a conversation turn stays in plaintext until the user clears memory. The Windows DPAPI path and the ACL have **not** executed here. | **PARTIAL** |
| F20 close-app safety | PARTIAL — any process by name, auto-allowed, kill-tree after 2 s | **FIXED** — `close-app` is now SensitiveFileModification (per-request approval); `ProcessSafety` refuses protected names (system, lsass, csrss, winlogon, svchost, explorer, dwm, …) and session-0 processes in the tool layer **and** both process controls, even if approved; `CloseApp` now returns true only when a process was actually closed. Residual, documented: closing is by name, so several same-named processes close together (reported as a count on Windows); ownership by another user cannot be proven from this API cross-platform — the approval dialog naming the exact target is the control, and it is a Windows-test item. | **FIXED** |

Updated verdict: **10 PASS, 2 PARTIAL (F13, F14), 8 FIXED, 0 FAIL.**
Full evidence per finding, with file:line references, is in
`SECURITY_REVIEW.md`.

**DPAPI migration/recovery/compatibility plan (F14):** values are
stored per-value with a version marker (`dpapi1:` /
`plain1:`); legacy unmarked values read as-is and are re-protected
on the next save; on a non-Windows machine a `dpapi1:` value reads
as null with a warning; if the Windows user profile is lost, the
values are unrecoverable — acceptable because preferences are
non-critical settings reconfigurable from the UI. Full-database
encryption was **not** introduced: it would need a
decrypt-to-temp-file step at startup that leaks the plaintext, and
is out of scope for this phase.

**Does F14 block external release?** No — with the mitigations in
place (DPAPI preferences, credential-value refusal, user-only ACL,
clear-memory path, documented warning not to paste credentials),
the residual risk is the plaintext conversation/task history of a
single-user local database under the user's own profile ACLs. It
must be disclosed in the product privacy notes, and full-database
encryption remains a post-test improvement, not a silent risk.

## 9. Publish size optimisation (Part 11)

| Output | Before | After | Saving |
|---|---|---|---|
| Self-contained win-x64 | 384 MB | **270 MB** | 114 MB |
| Framework-dependent win-x64 | 224 MB | **109 MB** | 115 MB |

Method: publish-time MSBuild target (see §1.5) — no files were
hand-deleted. Verified after re-publish: `.playwright/node/linux-x64`
is an empty directory (0 bytes); `win32_x64/node.exe` (80 MB),
`.playwright/package` (11 MB, JS driver intact),
`e_sqlite3.dll` at the publish root and the PE32+ GUI x86-64
`AuroraOperator.exe` are all present; the default installer source
location (`src/AuroraOperator.App/bin/Release/net8.0-windows/win-x64/publish/`)
was re-published too and is also 270 MB. **Self-contained remains
the recommended first Windows test target.** Runtime re-verification
(launch the trimmed app, repeat the Chromium tests) is BLOCKED —
it must happen on Windows.

## 10. Files changed in Phase 8

| File | Change |
|---|---|
| `src/AuroraOperator.Core/Ollama/ModelCatalog.cs` | Minimal tier (0.6b < 8 GB, 4K ctx); `SelectBestModel` 8b branch (≥ 32 GB + ≥ 10 GB free) and 0.6b below 8 GB |
| `src/AuroraOperator.Core/Ollama/ModelManager.cs` | Applies the selected profile's context window in `EnsureBestModelAsync` |
| `src/AuroraOperator.Core/Ollama/OllamaClient.cs` | Bounded retry (3 attempts, 250/500 ms, transient-only, never 4xx/cancel); permanent/transient marker exceptions |
| `src/AuroraOperator.Core/Security/AppLaunchPolicy.cs` | **New.** F5 allowlist + rejection reasons |
| `src/AuroraOperator.Core/Security/ProcessSafety.cs` | **New.** F20 protected-process guard + session-0 guard |
| `src/AuroraOperator.Core/Security/SecretProtector.cs` | **New.** F14 DPAPI per-value protection (Windows) + versioned markers |
| `src/AuroraOperator.Core/Security/RiskClassifier.cs` | launch-app: unknown/dangerous → Sensitive; close-app → Sensitive |
| `src/AuroraOperator.Core/Tools/AppTools.cs` | Hard-rejects dangerous launches; refuses protected close-app targets |
| `src/AuroraOperator.Core/Tools/AppControl.cs` | `CloseApp` safety filter; returns true only when a process was closed |
| `src/AuroraOperator.App/Services/WindowsAppControl.cs` | Same F5 hard-reject and F20 guard in the Windows control |
| `src/AuroraOperator.Core/LocalLogger.cs` | F13: PEM/Basic/token redaction; full user request no longer logged (see `AgentEngine.cs:149`) |
| `src/AuroraOperator.Core/Agent/AgentEngine.cs` | Start log: mode/language/length only, no request text |
| `src/AuroraOperator.Core/Memory/MemoryStore.cs` | F14: protected preference values, credential-value refusal, Windows-only DB ACL |
| `src/AuroraOperator.App/AuroraOperator.App.csproj` | Publish target removing non-Windows Playwright Node |
| `src/AuroraOperator.Installer/AuroraOperator.nsi` | 8b model branch (≥ 32 GB, 5,200 MB) |
| `src/AuroraOperator.Installer/InnoSetup.iss` | 8b model branch; ssPostInstall health check now cancels setup on failure (`HealthCheckFailed` + `WizardForm.Close` + `CancelButtonClick`) |
| `tests/AuroraOperator.Tests/Phase8Tests.cs` | **New.** 64 test cases (model selection, retry, F5, F20, F13, F14, browser security) |
| `tests/AuroraOperator.Tests/AuroraTests.cs`, `OllamaPhase7Tests.cs` | Two tests updated to the aligned < 8 GB behaviour, with reasons recorded |
| `publish/win-x64-self-contained/`, `publish/win-x64-framework-dependent/` | Re-published, trimmed (270 MB / 109 MB) |
| `build.log` | Phase 8 evidence appended (now 1,564 lines) |
| `SECURITY_REVIEW.md` | Verdict updated to 10 PASS / 2 PARTIAL / 8 FIXED / 0 FAIL; F5/F20 rows marked FIXED in Phase 8; F13/F14 rows PARTIAL (improved); Phase 8 addendum with per-finding evidence and the DPAPI migration/recovery plan |
| `RELEASE_PREFLIGHT.md` | Phase 8 addendum (§8); TestRunner 184 → 248; model expectations extended (0.6b below 8 GB, 8b at 32 GB) |
| `tests/windows/WINDOWS_ACCEPTANCE_CHECKLIST.md` | Phase 8 section: 10 new items (P8-1 … P8-10) all BLOCKED; status vocabulary defined; stale test counts corrected (102 → 248) |
| `tests/windows/WINDOWS_TESTING_GUIDE.md` | Phase 8 additions: procedures P8-1 … P8-10 |
| `LIMITATIONS.md` | Model table (8b at 32 GB), Inno health-check fix noted as unexecuted, Phase 8 notes section (publish sizes, DPAPI/ACL unexecuted, residual risks) |
| `README.md`, `publish/README.md`, `BUILD.md`, `TESTS.md` | Model selection (8b at 32 GB) and publish sizes (270 MB / 109 MB) updated; TestRunner count 248; file counts re-measured after the trim (357 / 815) |
| `BUILD_AUDIT.md` | Phase 8 addendum (test count, `dotnet test` re-proof, PdfPig re-probe, model-selection fix, publish sizes, installer changes) |

## 11. Acceptance status

Every item is exactly one of PASS / FAIL / BLOCKED / NOT TESTED /
ACCEPTED RISK. **No item is PASS on code inspection alone** — a
code audit is recorded as NOT TESTED (for the Windows evidence
still required) or BLOCKED (where the environment makes it
impossible here). The per-item table lives in
`tests/windows/WINDOWS_ACCEPTANCE_CHECKLIST.md` (updated in this
phase with Phase 8 evidence and the new Windows procedures).

| Area | Status | Evidence / what is still required |
|---|---|---|
| Solution build (Linux cross-build) | **PASS** | 0 errors, 40 warnings, 2026-11-21T18:00:32Z, `build.log` |
| TestRunner (248 tests) | **PASS** | 248 passed, 0 failed, 2026-11-21T18:00:32Z |
| `dotnet test` (vstest) | **BLOCKED** | 0 tests run; sandbox proxy redirects testhost channel; must run on Windows |
| PdfPig stable pin | **NOT TESTED** | NU1103 in this feed; Windows-integrator step on real nuget.org |
| App launch / WPF runtime | **BLOCKED** | Needs a Windows machine |
| Notepad UI flow + approvals | **BLOCKED** | Needs Windows 11 Notepad (tabbed) |
| Ollama install / inference | **BLOCKED** | No server/model here |
| Chromium / browser automation | **BLOCKED** | No Chromium binary here |
| Installer compilation | **BLOCKED** | `makensis`/`iscc` absent |
| Installer execution + rollback | **BLOCKED** | Never executed |
| Ollama network binding | **BLOCKED** | Needs `netstat` on Windows; code checks are not proof |
| DPAPI preference protection | **BLOCKED** | Windows path never executed |
| Model switch on a running engine | **NOT TESTED** | Code path exists; Windows test required |
| F5 / F20 fixes | **PASS** (Linux tests) | 64 new tests pass; Windows bypass-attempt tests still required |
| F13 / F14 improvements | **PARTIAL** | See §8; residual risks documented |
| Publish trim | **PASS** (sizes + contents verified on Linux) | Runtime re-verification BLOCKED (Windows launch + Chromium) |

## 12. Remaining release blockers and risks

**Release blockers for a production release: the Windows gate
itself.** No production GO is possible until a real Windows machine
runs the acceptance checklist. Classified:

- **First-Windows-test requirements:** WPF launch + SQLite native
  load from the flattened layout; Notepad UI flow; Chromium launch
  and downloads; Ollama download + real inference; both installers
  compiled and executed, including the forced-failure paths (Inno
  health-check cancel, model-download failure, disk-full);
  `dotnet test` executing real tests; DPAPI + DB ACL verification;
  Ollama loopback binding via `netstat`; trimmed-publish launch.
- **Post-test improvements:** full-database encryption (F14
  follow-up); the 4 pre-existing non-PdfPig warnings (CS8600/CS0067/
  xUnit2013 in test code); PdfPig stable pin on real nuget.org.
- **Accepted residual risks:** F13 bare-prose secrets in logs;
  F14 plaintext conversations/tasks by design; close-app
  name-ambiguity; installer-only-total-RAM model choice vs the
  app's free-RAM gate.

## 13. Final recommendation

**NO-GO for production release. CONDITIONAL GO for the first
Windows test attempt only.**

The Phase 7 preflight's conditional GO stands, strengthened: the
code is in a better state than at the preflight (model selection
aligned in all three mechanisms, Ollama retry added, F5/F20 fixed,
publish trimmed by 115 MB, Inno health-check failure now cancels
instead of reporting success), and the Linux-verifiable evidence is
complete (248/248 tests, clean build). But the decisive evidence —
does the app launch on Windows, does the installer install, does
Ollama answer, does Chromium run — still does not exist, because
this environment cannot produce it. A production GO while those
tests remain unverified would be fabrication. The next step is a
real Windows machine: run `tests/windows/windows-diagnostics.ps1`,
follow `WINDOWS_TESTING_GUIDE.md`, and tick off
`WINDOWS_ACCEPTANCE_CHECKLIST.md`.

## 14. Files and folders to transfer to the Windows test machine

- The whole `~/workspace/aurora-operator/` tree (or at minimum:
  `src/`, `tests/`, `AuroraOperator.sln`, `*.md`,
  `publish/win-x64-self-contained/` (270 MB) and
  `publish/win-x64-framework-dependent/` (109 MB),
  `tests/windows/`).
- Do **not** transfer `bin/`/`obj/` as the source of truth — rebuild
  on Windows from source; the publish folders are the prebuilt
  test targets.

## 15. Commands to run on Windows (exact)

```powershell
dotnet --info
dotnet build AuroraOperator.sln -c Release
dotnet test tests/AuroraOperator.Tests -c Release
dotnet run --project tests/AuroraOperator.TestRunner -c Release
.\tests\windows\windows-diagnostics.ps1
# Then follow tests/windows/WINDOWS_TESTING_GUIDE.md items 1-15.
```

Expected: build 0 errors; `dotnet test` executes real tests
(it executed 0 here only because of the Linux sandbox proxy);
TestRunner 248 passed / 0 failed. Any failure is a FAIL, not a
note — record it in the checklist with the log path.

## Final release addendum (2026-11-21)

This Phase 8 report is superseded as the current status by
`release/FINAL_RELEASE_REPORT.md`. What changed after Phase 8: PID-scoped
close-app (F20 deepened), raw launch-primitive refusal (F5 deepened),
approval dialog now shows exact parameters, checklist I3 corrected to
`qwen3:0.6b` for 4 GB machines, clean build still 0 errors / 40 warnings,
TestRunner 248 → **263 passed / 0 failed** (15 new Phase 9 cases),
`dotnet test` still 0 run, fresh publish still 270 MB / 109 MB, and a
**genuine NSIS installer was compiled**: `release/AuroraOperator-Setup-0.2.0-x64.exe`
(110,168,296 bytes, SHA-256 `a185abb1…807f97`). Verdict unchanged in kind:
**NO-GO for production; CONDITIONAL GO for the first Windows test attempt
only** — now expressed as CONDITIONAL — USER TESTING REQUIRED.
