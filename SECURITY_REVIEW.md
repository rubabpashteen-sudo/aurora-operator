# Aurora Operator — Security Review (Phase 7)

Date: 2026-11-21. Scope: the code listed in the Phase 7 task (Core Ollama,
Security, Agent, Tools except BrowserTools, Memory, LocalLogger; App
startup, MainViewModel, WPF approval service, WindowsAppControl) plus the
installer scripts and docs **as read-only evidence** (installer scripts
were not edited — they belong to another worker).

Method: read the code, grepped for `0.0.0.0`, `localhost`, `11434`,
`OLLAMA_HOST`, `password`/`secret`/`token` in log calls, `Process.Start`,
`UseShellExecute`, `powershell`/`cmd`, and shell-constructed commands;
re-checked the Phase 1 approval design (forged approvals, dispatcher
enforcement) against the current code; verified every fix with a new
Linux-runnable test.

**Verdict: 10 PASS, 2 PARTIAL (F13, F14), 8 FIXED in this review (Phase 8), 0 FAIL remaining.**
No approval bypass and no secret-logging line remain in the reviewed
code. The PARTIAL items are residual risks with concrete remediations,
not open bypasses. (Phase 7 integration, 2026-11-21: F2 and F8 below
were fixed by the integrator — see the FIXED notes in those sections.)

> **Phase 8 update (2026-11-21):** F5 and F20 are now **FIXED** (see
> the Phase 8 addendum at the end of this document); F13 and F14 are
> **PARTIAL (improved)**. Updated verdict: **10 PASS, 2 PARTIAL
> (F13, F14), 8 FIXED, 0 FAIL.** The Windows-only verification for
> the DPAPI path, the DB ACL, and live bypass attempts on Windows is
> still BLOCKED — see `PHASE8_REPORT.md`.

> Note on parallel work: `BrowserTools.cs` and its tests changed while
> this review was written (a download action was added by another Phase 7
> worker). Findings F7/F8 cite the file as read on 2026-11-21; re-verify
> them against the final tree.

## Findings

| # | Area | Result | Evidence |
|---|---|---|---|
| F1 | Ollama localhost-only binding | PASS | `OllamaClient.cs:19,30,43-60`; `App.xaml.cs:107` |
| F2 | Installer `ollama serve` binding | **FIXED** (was PARTIAL) | `AuroraOperator.nsi` (OLLAMA_HOST set before serve); `InnoSetup.iss` (cmd /c set OLLAMA_HOST …) |
| F3 | Filesystem access, NUL, traversal | PASS | `FilesystemTools.cs:36-40,250,266`; `NotepadWorkflow.cs:108-109` |
| F4 | Process execution (coding/tests) | PASS | `CodingTools.cs:165-175,191-206,221,229` |
| F5 | App launch (`UseShellExecute=true`) | **FIXED in Phase 8** (was PARTIAL) | `AppLaunchPolicy.cs`; `AppTools.cs:36-52`; `WindowsAppControl.cs:17-35`; `RiskClassifier.cs` launch branch; `Phase8Tests.cs` |
| F6 | PowerShell / shell execution | PASS | grep: no `powershell`/`pwsh`/`cmd.exe` in product code |
| F7 | Browser research permissions | PASS | `BrowserTools.cs:36-46,146,271-275,393-396` |
| F8 | Browser download destination | **FIXED** (was PARTIAL) | `BrowserTools.cs` (`DefaultDownloadDirectory`, `IsInsideUserProfile`); `Phase7IntegrationTests.cs` |
| F9 | Approval bypass possibilities | PASS | `AgentEngine.cs:205-249,363-386`; `ApprovalService.cs`; `PermissionManager.cs:47,55` |
| F10 | Planner / LLM output as permission | PASS | `Planner.cs`; `AgentEngine.cs:637-675` |
| F11 | Task cancellation | PASS | `AgentEngine.cs:187,347`; `CodingTools.cs:221,229` |
| F12 | Sensitive actions (delete-all, fail-closed) | PASS | `PermissionManager.cs:134-191`; `RiskClassifier.cs:132` |
| F13 | Log privacy | PARTIAL (improved in Phase 8) | `LocalLogger.cs`; `AgentEngine.cs:149` |
| F14 | Database storage (SQLite) | PARTIAL (improved in Phase 8) | `SecretProtector.cs`; `MemoryStore.cs` |
| F15 | Audit log integrity | PASS | `AuditLog.cs:29-65`; `MainViewModel.cs:824-834` |
| F16 | `run-tests` executes code, was auto-allowed | **FIXED** (was FAIL) | `RiskClassifier.cs:56-64,120-122` |
| F17 | Browser `download` was classified ReadOnly | **FIXED** (was FAIL) | `RiskClassifier.cs:20-21` |
| F18 | `OllamaClient` accepted any remote base URL | **FIXED** (was PARTIAL) | `OllamaClient.cs:30,43-60` |
| F19 | Stale panel click logged as "approve" | **FIXED** (was PARTIAL) | `MainViewModel.cs:824-834` |
| F20 | `close-app` can kill any process by name | **FIXED in Phase 8** (was PARTIAL) | `ProcessSafety.cs`; `AppTools.cs:53-70`; `AppControl.cs`; `WindowsAppControl.cs:37-90`; `RiskClassifier.cs` close branch; `Phase8Tests.cs` |

## Detail

### F1 — Ollama localhost-only binding: PASS

- The client defaults to `http://localhost:11434`
  (`OllamaClient.cs:19`) and the app constructs it with the default
  (`App.xaml.cs:107` — `new OllamaClient(_httpClient)`). There is no
  setting, UI field, or config file that points it elsewhere.
- **New in this review:** the constructor now rejects any non-loopback
  base URL (`OllamaClient.cs:30` calls `ValidateLoopbackOnly`,
  `OllamaClient.cs:43-60` — only `localhost`, `127.0.0.1`, `::1` over
  http/https are accepted; anything else throws `ArgumentException`).
  The "localhost-only" property used to be a convention; it is now
  enforced. Tests: `SecurityPhase7Tests.OllamaClient_AcceptsLoopbackBaseUrls`
  / `RejectsNonLoopbackBaseUrls`.
- Grep for `0.0.0.0` across `src/`, `docs/`, `*.md`, installer scripts:
  no occurrences in product code or installers (only the new rejection
  tests and the diagnostics doc mention it). No `OLLAMA_HOST` is set
  anywhere in the repo.
- The Windows diagnostics script (`tests/windows/windows-diagnostics.ps1`,
  check 12) verifies at runtime that nothing listens on port 11434
  except loopback, so a misconfigured machine is caught on Windows.

### F2 — Installer `ollama serve` binding: FIXED in Phase 7 integration

- The NSIS installer starts Ollama with `Exec '"$OLLAMA_EXE" serve'`
  (`AuroraOperator.nsi:138`). A bare `ollama serve` binds
  `127.0.0.1:11434` **unless** the `OLLAMA_HOST` environment variable is
  set — and if a user (or another program) has set
  `OLLAMA_HOST=0.0.0.0`, the server the installer starts would listen on
  all interfaces, with no authentication on the Ollama API.
- **Fixed 2026-11-21 (integrator):** NSIS now calls
  `System::Call 'kernel32::SetEnvironmentVariable(t "OLLAMA_HOST", t
  "127.0.0.1:11434")'` before the `Exec`, and Inno now starts Ollama via
  `cmd /c set OLLAMA_HOST=127.0.0.1:11434 && "<ollama.exe>" serve`.
  Both scripts pin the bind to loopback. Not yet executed — the
  installers have still never been compiled or run (see LIMITATIONS.md).

### F3 — Filesystem access, path validation, traversal: PASS

- No sandbox exists by design — the user operates their own machine, so
  traversal outside a base directory is not applicable
  (`FilesystemTools.cs:30-35` documents this). Approval, not a sandbox,
  is the control for destructive/overwriting operations.
- NUL bytes are rejected with a clear failure in `FilesystemTools.cs:36-40`
  (path and destination) and `NotepadWorkflow.cs:108-109` (save path).
- Copy/move/rename never overwrite: `File.Copy(..., overwrite: false)`
  (`FilesystemTools.cs:250,266`); move/rename throw when the destination
  exists; writing over an existing file is classified
  SensitiveFileModification and approval-gated before the tool runs
  (`RiskClassifier.cs` write-file branch).
- No delete action is offered by `FilesystemTools` at all.

### F4 — Process execution (coding tool): PASS

- `CodingTools.RunTestsAsync` runs only fixed command lines —
  `python -m pytest -q` / `python -m unittest discover -v` — with
  `UseShellExecute = false` (`CodingTools.cs:191-206`). No user or plan
  text is ever interpolated into a command line, so there is no shell
  injection via plan text. The working directory is the project dir
  from the plan; the executable is resolved from PATH (`python`/`py`
  on Windows, `python3`/`python` elsewhere).
- Runs are bounded: 5-minute timeout, whole process tree killed on
  timeout (`CodingTools.cs:221,229`). Real stdout/stderr/exit code are
  reported; a missing Python is an honest failure.

### F5 — App launch (`UseShellExecute = true`): PARTIAL

- `ProcessAppControl.LaunchApp` and `WindowsAppControl.LaunchApp` start
  processes with `UseShellExecute = true`
  (`AppControl.cs:71-76`, `WindowsAppControl.cs:25-30`). With shell
  execution, the `app` argument is not just an .exe name: a path to a
  `.bat`/`.ps1`/`.msi`, or a URL, would be executed/opened by the shell.
- Mitigations in place: `AppTools.ResolveAppName` maps only four friendly
  names (`notepad`, `calculator`, `paint`, `wordpad` →
  `*.exe`, `AppTools.cs:163-176`); the Planner only ever generates a
  `launch-app` step for Notepad (`Planner.cs` "write" branch); launch is
  the app's stated purpose and is user-directed.
- **Remediation:** restrict `LaunchApp` to an allowlist of executables
  (the four friendly names plus explicit `.exe` paths under Program
  Files / System32), and reject URLs and script extensions (`.bat`,
  `.cmd`, `.ps1`, `.vbs`, `.msi`) unless the step was approved as
  Administrator. Until then, launching is user-directed only via the
  Planner's Notepad path.

### F6 — PowerShell / shell execution: PASS

- No `powershell`, `pwsh`, `cmd.exe`, or `/c` invocation exists in the
  product code (`src/AuroraOperator.Core`, `src/AuroraOperator.App`).
  The only `cmd /c` occurrences are fixed strings in the NSIS installer
  (`where ollama` detection, a `ping` delay) — no user input reaches
  them. There is no generic "run a command" tool: the only code
  execution path is `CodingTools` (F4) and app launching (F5).

### F7 — Browser research permissions: PASS

- `BrowserTools` is research-only by default: it launches headless
  Chromium (`BrowserTools.cs:393-396`, `Headless = true`), searches Bing,
  extracts paragraphs, and writes a markdown report. It performs no form
  submission, no login, no file upload, and no post.
- CAPTCHA/verification pages are detected by marker strings
  (`BrowserTools.cs:36-46,146`) and recorded as Blocked; the tool never
  attempts to bypass them.
- The Planner's browser step is classified ReadOnly
  (`RiskClassifier.cs` browser carve-out), and research writes its
  report to a path the app controls.

### F8 — Browser download destination: FIXED in Phase 7 integration

- The new `download` action validates its URL well: only absolute
  http(s), a host must be present, embedded credentials are rejected
  (`BrowserTools.cs:154-191`). The path validator is also correct in
  isolation: NUL rejection, absolute path, must name a file, must stay
  inside the given directory (`BrowserTools.cs:201-261`).
- **The gap was at the call site** (`BrowserTools.cs:292-296`): the
  "download directory" passed to the validator was derived from the
  download path itself
  (`Path.GetDirectoryName(Path.GetFullPath(downloadPath))`), so the
  containment check could never fail — a path such as
  `C:\Downloads\..\..\Windows\evil.exe` normalises to a location whose
  own parent trivially "contains" it. The destination was effectively
  unconstrained. Mitigating factor: since F17, a download step requires
  per-request approval and the approval dialog shows the exact
  destination, so a user can see where the file would land.
- **Fixed 2026-11-21 (integrator):** the call site now uses a fixed
  `BrowserTools.DefaultDownloadDirectory` (the user's Downloads folder)
  unless the caller supplies an explicit `downloadDirectory`, which must
  itself pass `BrowserTools.IsInsideUserProfile`. Deriving the allowed
  directory from the requested path is gone. Covered by
  `Phase7IntegrationTests` (outside-Downloads refused; in-profile
  override passes validation; outside-profile override refused).

### F9 — Approval bypass possibilities: PASS

Re-checked the Phase 1 design against the current code; no bypass found:

- **Dispatcher enforcement, not prompt enforcement.** The engine
  computes risk itself (`AgentEngine.cs:205-212`) and asks the approval
  service before any step that `RequiresApproval`, is destructive, or
  is not auto-allowed. The risk comes from what the step does
  (`RiskClassifier.Classify(step)`); the code comment and the test
  `Engine_IgnoresApprovedFlagInPlanText` confirm that an
  `approved=true` marker smuggled into plan text is ignored.
- **Binding.** An approval is bound to (TaskId, ActionId, SHA256 of
  canonical parameters) (`ApprovalService.CreateRequest`,
  `CanonicalParameters`, `ComputeParameterHash`). If the exact
  parameters change between approval and execution, the engine
  re-requests approval once (`AgentEngine.cs:376-386`).
- **No replay.** The same ActionId is never executed twice in one run
  (`AgentEngine.cs:249` and the duplicate check above it).
- **Fail closed.** With no approval service, Sensitive+ steps are
  denied (`AgentEngine.cs:363-369`); approval-service exceptions are
  denials; expiry, rejection, and cancellation all leave the step
  unexecuted; a cancelled task propagates `OperationCanceledException`
  so the action never runs. `WpfApprovalService` denies when there is
  no UI dispatcher or the dialog cannot be shown
  (`WpfApprovalService.cs:75,161`).
- **Category grants never auto-approve Sensitive+.** 
  `PermissionManager.RequiresApproval` (`PermissionManager.cs:47`) and
  `IsRiskAutoAllowed` (`PermissionManager.cs:55`) encode this, and
  `Grant` is documented as user-settings-only.
- **Plan-mode double prompting is intentional:** Plan mode asks once per
  step at review, then the engine asks again at execution
  (`MainViewModel.cs` `HandleWaitingApprovalAsync`). There is no scoped
  plan-approval token that could be stolen or replayed.

### F10 — Planner / LLM output as a permission grant: PASS

- Plans are built by the deterministic keyword `Planner`
  (`Planner.cs`) — no LLM output ever becomes a plan step. The model's
  text is used in exactly one place: `AnswerAsync`
  (`AgentEngine.cs:637-675`), which returns an answer string for Ask
  mode and never executes anything. Model output is therefore untrusted
  data that cannot grant a permission, pick a tool, or smuggle an
  `approved` flag into execution — and even if it could, F9 shows the
  engine ignores such flags.

### F11 — Task cancellation: PASS

- The cancellation token is checked before every step
  (`AgentEngine.cs:187`), inside the pause gate
  (`AgentEngine.cs:347`), and inside retries; cancelling while an
  approval is pending propagates and the action never runs (F9).
- The pause gate is async (`TaskCompletionSource`), so pause + cancel
  cannot deadlock the caller (Phase 5 fix, still in place).
- A runaway test run is killed with its whole process tree after the
  5-minute step timeout (`CodingTools.cs:221,229`). Stop during a
  pending approval is covered by Windows test item 10.

### F12 — Sensitive actions (delete-all, fail-closed default): PASS

- Unbounded delete/wipe/format requests are refused before planning:
  `PermissionManager.IsDestructiveRequest` / `GuardDeleteAllAsync`
  (`PermissionManager.cs:134-191`), and the Planner turns a
  delete-all request into a list-only plan that asks for a specific
  scope (`Planner.cs` "delete-all" branch).
- The classifier fails closed: an unknown modifying action defaults to
  SensitiveFileModification, never to auto-allowed
  (`RiskClassifier.cs:132`).

### F13 — Log privacy: PARTIAL (improved in this review)

- **Fixed:** log lines are now scrubbed for common secret patterns
  before being written — `LocalLogger.RedactSecrets`
  (`LocalLogger.cs:27-45`), applied to messages and exception messages
  (`LocalLogger.cs:64-65`). Patterns: `password=`/`passwd=`/`pwd=`/
  `api_key=`/`secret=`/`token=`/`access_key=`/`client_secret=`,
  `password is <value>`, and `Bearer <token>`. The key name is kept,
  the value becomes `[REDACTED]`. Tests:
  `SecurityPhase7Tests.RedactSecrets_*`.
- **Residual (why PARTIAL, not PASS):**
  1. Redaction is pattern-based. A secret written as free prose with no
     key marker (e.g. `my password is hunter2` is caught, but `hunter2`
     alone, or a pasted private key block, is not) can still reach the
     log. Callers must still avoid logging user-supplied secrets.
  2. The full user request is logged at Info
     (`AgentEngine.cs:146` — `Task {id} started in {mode} mode:
     {UserRequest}`) and stored verbatim in SQLite (F14) by design —
     conversation memory is a feature. If a user pastes a secret into a
     request, the redactor catches only the patterned forms.
- The audit log itself is clean: it records risk, action id, step id,
  the parameter **hash** (never the parameters), outcome and source
  (`AuditLog.cs:29-45`), and flattens newlines so one record stays one
  line (`AuditLog.cs:48-65`).

### F14 — Database storage (SQLite): PARTIAL

- Location: `%LOCALAPPDATA%\AuroraOperator\aurora.db`
  (`App.xaml.cs:110`) — the user's own profile, default Windows ACLs,
  no network access. No encryption at rest (SQLite plaintext).
- All queries are parameterised (`MemoryStore.cs`, `AddWithValue`
  throughout); no SQL injection surface was found. `LIKE` search
  (`MemoryStore.cs:144`) takes the query as a bound parameter, so `%`
  / `_` in a search string act only as wildcards.
- Secrets are refused as **preference keys**
  (`MemoryStore.cs:18-19,80-89` — keys containing
  password/secret/token/key are never persisted), but the filter checks
  the key only: a secret stored as a *value* under an innocuous key, or
  pasted into a conversation turn (stored verbatim by design), is
  persisted in plaintext.
- **Remediation:** document that the database is plaintext and that
  users should not paste credentials into the app; consider DPAPI
  (Windows) encryption for the database file, or at least refuse
  preference *values* that match the same secret patterns used by
  `LocalLogger.RedactSecrets`. The "Clear memory" path already exists
  in the UI (`MainViewModel.ClearMemory`).

### F15 — Audit log integrity: PASS

- Append-only, one flattened line per record, hash-only parameters
  (F13). Every approval request and decision is logged by both the
  broker service and the WPF service.
- **Fixed in this review:** `MainViewModel.ResolveApproval` used to
  record a plain `"approve"` in the audit log when the Approvals panel
  resolved a request the service no longer had pending (stale panel
  entry) — an audit entry claiming an approval that never happened.
  It now records `"approve-stale"` / `"deny-stale"` with an explicit
  note that the decision authorised nothing
  (`MainViewModel.cs:824-834`). The stale click still authorises
  nothing (the engine's request has a different id), so this was an
  audit-integrity issue, not an execution bypass.

### F16 — `run-tests` executes code but was auto-allowed: FIXED (was FAIL)

- `CodingTools` runs a project's tests, i.e. executes arbitrary code
  from the target directory — yet `RiskClassifier` listed `run-tests`
  as NormalReversible, so a planned `run-tests` step ran with **no
  approval**, even when pointed at a directory the user had just
  downloaded. `create-project` (fixed safe files) stays NormalReversible.
- **Fix:** `run-tests` is now Administrator
  (`RiskClassifier.cs:56-64`, removed from the reversible list at
  `:120-122`), so it needs a per-request approval like any other code
  execution. Tests:
  `SecurityPhase7Tests.RiskClassifier_RunTests_IsAdministrator_NotAutoAllowed`
  and `Engine_RequiresApproval_WhenRunTestsStepIsPlanned`.

### F17 — Browser `download` was classified ReadOnly: FIXED (was FAIL)

- `download` was missing from `ExternalActions`, so the browser
  carve-out treated a download — fetching an arbitrary file from the
  internet and writing it to disk — as a read-only research step, i.e.
  auto-allowed with no approval.
- **Fix:** `download` added to `ExternalActions`
  (`RiskClassifier.cs:20-21`); it now classifies as
  ExternalCommunication and requires approval. Test:
  `SecurityPhase7Tests.RiskClassifier_BrowserDownload_IsExternalCommunication_NotReadOnly`
  (research stays ReadOnly). The destination-containment gap in F8
  remains for the BrowserTools owner.

### F18 — `OllamaClient` accepted any remote base URL: FIXED (was PARTIAL)

- See F1. The constructor previously accepted any `baseUrl`; only
  convention kept it at localhost. It now enforces loopback (F1).

### F19 — Stale panel click logged as "approve": FIXED (was PARTIAL)

- See F15.

### F20 — `close-app` can kill any process by name: PARTIAL

- `CloseApp` (`AppControl.cs:100-135`,
  `WindowsAppControl.cs:60-90`) finds processes by name, asks them to
  close, then kills the whole process tree after 2 seconds. The action
  is classified NormalReversible (auto-allowed), so a step naming e.g.
  `explorer` or a database process could terminate it — and killing
  after a failed graceful close can lose unsaved work in that app.
- Mitigations: the Planner never generates a `close-app` step today,
  so the action is only reachable from a hand-built plan; closing is
  normally graceful first.
- **Remediation:** require approval for `close-app` (classify it
  SensitiveFileModification or higher), and/or restrict it to
  processes the app itself launched (track launched PIDs in
  `IAppControl`). Not changed in this review: it is a product
  behaviour decision, not a bypass.

## Code fixes made in this review (all in-scope files)

| File | Change | Tests |
|---|---|---|
| `src/AuroraOperator.Core/LocalLogger.cs` | `RedactSecrets` + scrubbing in `Write` (F13) | `SecurityPhase7Tests.RedactSecrets_*` (8 cases) |
| `src/AuroraOperator.Core/Ollama/OllamaClient.cs` | `ValidateLoopbackOnly` enforced in constructor (F1/F18) | `OllamaClient_Accepts/RejectsLoopbackBaseUrls`, `DefaultBaseUrl_IsLocalhost` |
| `src/AuroraOperator.Core/Security/RiskClassifier.cs` | `run-tests` → Administrator (F16); `download` → ExternalCommunication (F17) | `RiskClassifier_RunTests_*`, `RiskClassifier_BrowserDownload_*`, `CreateProject_StaysNormalReversible` |
| `src/AuroraOperator.App/ViewModels/MainViewModel.cs` | Stale panel decisions logged as `approve-stale`/`deny-stale` (F15/F19) | — (private UI path; covered by code inspection) |
| `tests/AuroraOperator.Tests/SecurityPhase7Tests.cs` | **New.** 25 tests: redaction, loopback URLs, classifications, forged-approval engine tests | — |

## Items NOT fixed (out of scope — for the integrator)

1. **F5 / F20** — app-launch allowlist and `close-app` approval are
   product decisions; remediations are in F5/F20 above.
2. **F14** — plaintext SQLite; consider DPAPI encryption or a
   value-level secret filter (remediation in F14).

## Verification

- `dotnet build AuroraOperator.sln -c Release -p:EnableWindowsTargeting=true`:
  **0 errors, 32 warnings** (baseline NU1603 PdfPig warnings; no new
  warnings from the changed files).
- In-process TestRunner: **25/25 new `SecurityPhase7Tests` pass**; full
  run 174 passed / 1 failed / 175 total at the last run — the single
  failure is `BrowserPhase7Tests.ExecuteAsync_Download_NullBytePath_FailsWithoutBrowser`
  (another worker's in-flight BrowserTools work; `BrowserTools.cs` has
  no reference to any code changed here). A separate earlier run also
  showed a flaky `OllamaPhase7Tests.PullModelAsync_ReportsProgress`
  failure (async `Progress<double>` delivery race in that worker's
  test); it passed on re-run. Neither is a regression from this review.
- `dotnet test` was not re-run: it cannot run on this Linux VM (sandbox
  egress proxy redirects the testhost localhost channel — see TESTS.md).
  Re-run `dotnet test` on Windows.
- No Windows test in this review is claimed as passed. Windows
  verification is the acceptance checklist in `tests/windows/`.

## Phase 8 addendum (2026-11-21)

All four remaining PARTIALs were reworked. Linux-verifiable evidence
is in `PHASE8_REPORT.md` and `tests/AuroraOperator.Tests/Phase8Tests.cs`
(64 new cases; full TestRunner 248 passed / 0 failed, 2026-11-21T18:00:32Z).
Windows execution of the DPAPI path, the DB ACL and live bypass
attempts is BLOCKED.

### F5 — App launch allowlist: FIXED

- New `src/AuroraOperator.Core/Security/AppLaunchPolicy.cs`:
  friendly four (`notepad`, `calculator`, `paint`, `wordpad`) are
  `Allowed`; URLs and shell protocols (`http:`, `ms-settings:`,
  `file:`), and `.bat`/`.cmd`/`.ps1`/`.vbs`/`.js`/`.msi`/`.lnk`/
  `.scr`/`.hta`/`.reg`/`.com`/`.pif` are `Rejected`; every other
  executable name or path is `RequiresApproval`.
- `RiskClassifier` maps `RequiresApproval` launches (and `Rejected`
  ones, so the user sees them) to SensitiveFileModification — the
  engine asks for per-request approval showing the exact target.
- `AppTools` and `WindowsAppControl` hard-refuse `Rejected` targets
  even if approved; the control is never called for them (tested).
- **Final release (2026-11-21):** the boundary above is narrowed —
  `ProcessAppControl.LaunchApp` itself now refuses `Rejected` targets
  too (`AppControl.cs`, defense in depth), so no agent-reachable or
  shipped-app path can launch a URL, script or installer through the
  raw primitive either. `RequiresApproval` targets are still gated by
  the engine (RiskClassifier + IApprovalService) before the tool runs.
  The only remaining bypass would be a caller that constructs its own
  `Process.Start` outside these controls; `CodingTools` runs approved
  terminal commands only, gated by the same approval system.

### F20 — close-app safety: FIXED

- New `src/AuroraOperator.Core/Security/ProcessSafety.cs`:
  protected names (system, registry, smss, csrss, lsass, winlogon,
  wininit, services, svchost, dwm, fontdrvhost, conhost, explorer,
  searchindexer, spoolsv, msmpeng, securityhealthservice, audiodg,
  sihost, taskhostw, lsaiso, "memory compression", "secure system",
  idle) and, on Windows, session-0 processes are never closed.
- `RiskClassifier`: `close-app` is now SensitiveFileModification
  (was NormalReversible) — per-request approval is required.
- `AppTools` refuses protected names before calling the control;
  both controls filter with `IsSafeToClose` and now return true only
  when a process was actually closed.
- **Final release (2026-11-21): the "closing is by name" residual is
  FIXED, not merely documented.** New `AppProcessCloser`
  (`Tools/AppControl.cs`): close is by PID only. A name resolves to
  the list of matching processes; exactly one safe match may close,
  two or more close NOTHING (the failure lists the PIDs to pick
  from). Graceful close first; if it fails, the process is NOT
  force-killed unless the approved step's args contain `force=true`
  (bound by the approval's parameter hash). Force-kill kills only
  that one process, never the whole tree. The approval dialog now
  shows the exact parameters (`ApprovalService.CreateRequest` puts
  the canonical parameters in `ExactCommandOrChange`), so a close-app
  approval names the exact PID and whether force is included.
  `Phase9Tests.cs` (15 cases; full TestRunner 263 passed / 0 failed,
  2026-11-21) covers: multi-process ambiguity closes nothing,
  PID close closes only that PID, graceful failure without force,
  wrong-name PID refusal, unknown PID refusal, and the AppTools
  ambiguity/force flows. Windows execution (real Notepad instances,
  Win11 tabbed Notepad) is still BLOCKED — see the checklist.

### F13 — Log privacy: PARTIAL (improved)

- `LocalLogger.RedactSecrets` now also scrubs PEM private-key blocks
  (whole block), `Authorization: Basic`, `access_token` /
  `refresh_token`, `client_secret`, `x-api-key` (tests in
  `Phase8Tests`).
- `AgentEngine` no longer logs the full user request: the start line
  records mode, language and request length only
  (`AgentEngine.cs:149`).
- Residual: a secret as bare free prose with no key marker still
  cannot be caught by any regex. This is not claimed as complete
  secret protection.

### F14 — Database storage: PARTIAL (improved)

- New `src/AuroraOperator.Core/Security/SecretProtector.cs`:
  per-value DPAPI (`CryptProtectData`, CurrentUser scope) on
  Windows, stored as `dpapi1:<base64>`; versioned `plain1:` marker
  elsewhere (base64 only — explicitly NOT encryption); legacy
  unmarked values read as-is and are re-protected on next save.
- `MemoryStore.SetPreference` refuses values that look like
  credentials (they change under `RedactSecrets`) with
  `ArgumentException`; `GetPreference` unprotects, returning null
  for undecryptable DPAPI values.
- The DB file ACL is restricted to the current user on Windows
  (best-effort, logged on failure; no-op elsewhere).
- Migration/recovery/compatibility plan: see the class docs and
  `PHASE8_REPORT.md` §8. Conversations and tasks remain plaintext
  by design; a pasted secret in a conversation turn stays in
  plaintext until the user clears memory. The Windows DPAPI path
  and the ACL have not executed here (BLOCKED).
