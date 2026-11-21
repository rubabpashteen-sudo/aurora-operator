# Aurora Operator — Phases 1–6 Stabilisation Report

Date: 2026-11-21. Repo: `~/workspace/aurora-operator/`. Scope: stabilise and
complete the existing project. No new project, no ZIP packaged, no visual
features added, no fake implementations.

## 1. Files changed

### Phase 1 — Runtime approval system
| File | Change |
|---|---|
| `src/AuroraOperator.Core/Security/ApprovalService.cs` | **New.** `RiskLevel`, `ApprovalOutcome`, `ApprovalSettings` (5-min default timeout), `IApprovalService` (`RequestApprovalAsync` + `CancelPending`, `TryResolve`, `PendingRequests`, `PendingChanged`), in-memory broker (TaskCompletionSource pending map, expiry via linked CTS + timeout, audit-logs every request/decision), canonical-parameter + SHA256 hash helpers, `CreateRequest` factory binding TaskId + ActionId + ParameterHash. |
| `src/AuroraOperator.Core/Security/RiskClassifier.cs` | **New.** Tool+action → RiskLevel. ReadOnly: list/search/read/diagnose/research. NormalReversible: create-folder, new file. SensitiveFileModification: rename/move/organise/overwrite-existing. Destructive: delete/wipe/format. Administrator: admin/system-settings. ExternalCommunication: browser post/upload/email. Unknown modifying actions default Sensitive. An `approved=true` flag in plan text never lowers risk. |
| `src/AuroraOperator.Core/Models.cs` | `ApprovalRequest` keeps its 6-arg positional shape; adds init-only `StepId`, `ActionId`, `RiskLevel`, `ExactParameters`, `ParameterHash`, `RequestedAtUtc`, `ExpiresAtUtc`, `Outcome`, `DecidedAtUtc`, plus `EffectiveParameterHash` (SHA256 when not supplied). `PlanStep` gains typed init-only `IReadOnlyDictionary<string,string> Args` (Phase 2). |
| `src/AuroraOperator.Core/Security/PermissionManager.cs` | Added `RequiresApproval(RiskLevel)` / `IsRiskAutoAllowed`; `RequestApprovalAsync` no longer auto-approves Sensitive+ risks from a category grant (fail closed, prompt decides); `Grant()` documented as explicit user-settings only. |
| `src/AuroraOperator.Core/Security/AuditLog.cs` | Added `RecordApproval` (timestamp, task, risk, action/step id, hash, outcome, source). |
| `src/AuroraOperator.Core/Agent/AgentEngine.cs` | Optional `IApprovalService` / `ApprovalSettings` / `AuditLog` / plan-provider ctor params (old ctor still works). Dispatcher gate before any step with risk ≥ Sensitive, `RequiresApproval`, or destructive text — no service = fail closed (NeedsApproval). Approval bound to (TaskId, ActionId, ParameterHash); re-request if parameters change. Forged "approved" flags ignored. Per-run `HashSet` blocks duplicate ActionIds. Reject/expire/cancel → step not executed. Task-token cancellation while awaiting approval → task Cancelled, action never runs. Async pause gate (Phase 5 fix, see §4). `ParseHints` kept public for compat. |
| `src/AuroraOperator.App/Services/WpfApprovalService.cs` | **New.** `IApprovalService` showing `ApprovalDialog` on the UI dispatcher; Approved only on Approve, Rejected on Deny/close, Expired on countdown, Cancelled on task cancel/`CancelPending`; `TryResolve` for the panel; never fails open. |
| `src/AuroraOperator.App/Views/ApprovalDialog.xaml` + `.cs` | Shows RiskLevel, TaskId, ActionId, expiry countdown (DispatcherTimer); returns distinct `ApprovalOutcome` (`DialogResult` kept for bool compat). |
| `src/AuroraOperator.App/ViewModels/MainViewModel.cs` | Fake `OnAgentEvent` approval fabrication deleted; Plan-mode flow no longer grants/revokes whole permission categories (per-request prompts via the service, then re-runs the plan copy in Autonomous mode); `PromptForApprovalAsync` uses the service; `ResolveApproval` uses `TryResolve`; `Stop()`/`NewTask()` cancel pending approvals; Approvals panel syncs from the service's real pending requests. Now `IDisposable` (Phase 5). |
| `src/AuroraOperator.App/App.xaml.cs` | Constructs `AgentEngine` **with** the `WpfApprovalService`; services kept in fields and disposed on exit (Phase 5). |
| `tests/AuroraOperator.Tests/ApprovalTests.cs` | **New.** `TestApprovalService` test double (AutoApprove/AutoReject/NeverAnswer/ApproveOnlyFor) and `RecordingTool` in the tests project, not Core. |

### Phase 2 — Actual computer control / structured text passing
| File | Change |
|---|---|
| `src/AuroraOperator.Core/Agent/IntentParser.cs` | New `ExtractTextToWrite(request)`: quoted text, `write … : <text>` colon form, bare `write Hello World`, Chinese, Urdu; preserves Unicode/newlines/punctuation; strips trailing destinations; returns `null` when no real text exists. |
| `src/AuroraOperator.Core/Agent/Planner.cs` | Every step now populates typed `Args` (action/path/destination/name/text/verify); descriptions are human prose only, no `action=…; path=…` hints. `write` carries extracted text in `Args["text"]`; no text → step honestly says "No text was provided". `Open Notepad and write …` plans an `app`-tool flow (launch-app → type-text → save-as when a path was named). |
| `src/AuroraOperator.Core/Tools/AppTools.cs` | **New.** `app` tool (`IAgentTool`, name `"app"`): launch-app (notepad → `notepad.exe` on Windows), close-app, focus, list, type-text (with read-back check), read-text, verify-text, save-as, notepad-write. Every failure reported honestly. |
| `src/AuroraOperator.Core/Tools/NotepadWorkflow.cs` | **New.** Launch → poll for a "Notepad" window title (timeout + `CancellationToken`) → focus → type → read-back verify → save → verify file exists and `File.ReadAllText` equals input exactly (UTF-8). On non-Windows returns a clear `Windows-only (NOT SUPPORTED)` failure and launches nothing. |
| `src/AuroraOperator.Core/Tools/AppControl.cs` | `IAppControl` gains `ReadTextFromFocusedControl()`, `ReadWindowText(titleContains)`, `SaveNotepadFile(path)` as default implementations throwing `PlatformNotSupportedException` (never fake text). `ProcessAppControl` launch/list/close remain real (Process-based); `TypeText` still Windows-only. |
| `src/AuroraOperator.App/Services/WindowsAppControl.cs` | Real implementations: read-back via UI Automation `ValuePattern` (focused element / first Edit descendant); `SaveNotepadFile` (focus → Ctrl+S via SendInput → find Save dialog → set filename Edit → press Save → handle overwrite Yes). Returns false when the dialog cannot be driven, so the caller falls back. |
| `src/AuroraOperator.Core/Tools/FilesystemTools.cs` | `write-file` with no/empty `text` now fails ("no text provided") instead of writing an empty file; writes are explicit UTF-8 (no BOM); NUL-byte paths rejected (Phase 5). |
| `src/AuroraOperator.Core/Security/RiskClassifier.cs` | `Classify(PlanStep)` now uses `ResolveArgs` (Args-first); launch-app/type-text/close-app = NormalReversible, read/verify/focus = ReadOnly, save-as/notepad-write = write-file rule (Sensitive iff target exists). Approval gate untouched. |
| `src/AuroraOperator.App/App.xaml.cs`, `MainViewModel.cs` | Registered `new AppTools(new WindowsAppControl())` in the engine's tool list; plan-review canonical parameters use `ResolveArgs`. |
| `tests/AuroraOperator.Tests/Phase2Tests.cs` | **New** (+ `FakeAppControl`, explicitly labelled a Linux test double, not a Windows pass). |

### Phases 3–5 — Docs, testing, stability
| File | Change |
|---|---|
| `LIMITATIONS.md`, `README.md`, `BUILD.md`, `TESTS.md` | Rewritten to match shipped code, using categories (A) Implemented and verified, (B) Implemented but not yet verified on Windows, (C) Partially implemented, (D) Not implemented, (E) Requires manual Windows testing. Stale "WPF build fails on Linux" claim corrected everywhere. |
| `CONTRACTS.md` | Updated to shipped `IApprovalService`/`RiskLevel`/`PlanStep.Args`/`AppTools`/`NotepadWorkflow` APIs; resolved package versions (Playwright 1.50.0, OpenXml 3.1.1, PdfPig 0.1.9-alpha001-patch1); stale "do not run builds" rule removed. |
| `src/AuroraOperator.Core/Agent/AgentEngine.cs` | Async pause gate (see §4, bug 1). |
| `src/AuroraOperator.Core/Ollama/OllamaClient.cs` | Added `UnloadModelAsync` (`keep_alive=0`). |
| `src/AuroraOperator.Core/Ollama/ModelManager.cs` | Removed per-call `new HttpClient`; `UnloadModelAsync` delegates to `OllamaClient`. |
| `src/AuroraOperator.Core/Memory/MemoryStore.cs` | `TryReadConversation`/`TryReadTask` skip + log corrupt rows instead of failing whole list/search/export; garbage enums fall back to Unknown/Ask/Created. |
| `src/AuroraOperator.Core/Tools/FilesystemTools.cs`, `NotepadWorkflow.cs` | NUL-byte path rejection (see §4, bug 5). |
| `src/AuroraOperator.App/App.xaml.cs`, `MainViewModel.cs` | Disposal (see §4, bug 4). |
| `tests/AuroraOperator.Tests/Phase3Tests.cs` | **New** — 13 tests (pause/resume, cancel-while-paused, duplicate ActionId, interrupted-task recovery, overwrite-without-approval, corrupt memory rows, etc.). |
| `build.log` | Appended only (777 → 1036 lines, plus Phase 6 verification entry); history intact. |

## 2. New components implemented

- **`IApprovalService` + `ApprovalService`** (Core) — runtime approval broker with TaskId/ActionId/parameter-hash binding, expiry, cancellation, audit logging.
- **`RiskClassifier`** (Core) — 6-level risk classification enforced in the execution dispatcher, not in prompts.
- **`WpfApprovalService` + updated `ApprovalDialog`** (App) — real modal approval UI with exact action/command/path, risk, task/action IDs, expiry countdown, Approve/Reject.
- **`PlanStep.Args`** — typed structured step arguments replacing regex-scanned prose hints end-to-end (Args win; `ParseHints` only fills legacy gaps).
- **`AppTools` (`app` tool) + `NotepadWorkflow`** (Core) — launch / focus / type / read-back / save-as / verify-text, with Windows-only failure on other OSes.
- **`WindowsAppControl` read-back + Save-dialog automation** (App) — UI Automation `ValuePattern` read-back; Ctrl+S → Save dialog → filename → Save → overwrite confirm.
- **`OllamaClient.UnloadModelAsync`** — model unload without a fresh `HttpClient` per call.

## 3. Bugs fixed

1. **In-run approval hook missing (critical).** `AgentEngine` had no prompt hook; `PermissionManager` had no prompt hook; in-run approvals fell back to denied and the UI fabricated approvals from `AgentEvent`s. Fixed: engine now takes `IApprovalService`, App wires `WpfApprovalService`, fake `OnAgentEvent` approval fabrication deleted, Plan-mode category-grant workaround removed.
2. **Category grants auto-approved Sensitive+ risks.** A granted `FileAccess` category auto-approved destructive steps. Fixed: `RequestApprovalAsync` no longer auto-approves Sensitive+ risks from a category grant (fail closed).
3. **Engine did not pass text content.** Planner `write` steps carried no text; `ParseHints` regex-scanned prose. Fixed: `ExtractTextToWrite` + typed `Args`; empty-text writes now fail honestly instead of writing empty files.
4. **Pause deadlock (found by new tests, Phase 5).** `WaitIfPausedAsync` blocked the calling thread on `ManualResetEventSlim.Wait(ct)`, so a direct `RunAsync` caller could never reach `Resume()`/`Cancel()`; a TestRunner run hung ~7.5 min. Fixed with a truly async `TaskCompletionSource` pause gate. `Dispose` deliberately does not resume a paused run.
5. **Corrupt memory rows crashed lists.** `DateTimeOffset.Parse` / unguarded `GetString` in `MemoryStore` meant one bad row failed a whole search/list/export. Fixed: bad rows are skipped + logged; garbage enums fall back safely.
6. **Per-call `HttpClient` in `ModelManager`.** Removed; unload goes through the shared `OllamaClient`.
7. **Undisposed services on exit.** `App.xaml.cs` now disposes view model, engine, memory store, Ollama client and `HttpClient`; `MainViewModel` is `IDisposable` (stops `DispatcherTimer`, unsubscribes `PendingChanged`, disposes per-task `CancellationTokenSource`); `TaskScheduler.UnobservedTaskException` logging added.
8. **NUL-byte / unsafe paths.** Rejected with a clear failure in `FilesystemTools` and `NotepadWorkflow.SaveAsync`. Verified: copy is `overwrite:false`, move/rename throw on existing destination, overwriting an existing file is `SensitiveFileModification` and engine-gated (test proves the file stays unchanged without approval).
9. **Classifier hint-parsing bug (found by a Phase 1 test).** The classifier's 3-arg overload wasn't parsing hints, so existing-file writes were misclassified. Fixed; test now passes.
10. **Planner folder bug (pre-existing, fixed in the earlier build).** "Create a folder named X in <path>" created the container path instead of X; now combines name + container. (Recorded here because the final code depends on it.)

## 4. Tests executed — exact counts

| Runner | Passed | Failed | Total |
|---|---|---|---|
| **TestRunner (in-process, this VM)** — final coordinator verification run 2026-11-21T14:52Z | **102** | **0** | **102** |
| `dotnet test` (vstest) | **0 run** | **0** | **0 — could not connect to testhost (environment cause, proved; see below)** |

Test growth: 31 (original build) → 61 after Phase 1 (+30 approval/risk tests) → 89 after Phase 2 (+28 text/control tests) → 102 after Phases 3–5 (+13 stability tests). No test was weakened; no failing test was converted into a fake pass.

TestRunner command (verified by the coordinator independently):
```
~/.dotnet/dotnet run --project tests/AuroraOperator.TestRunner -c Release --no-build
# Test run complete: 102 passed, 0 failed, 102 total.
```

### Phase 4 per-item verification status
| # | Check | Status |
|---|---|---|
| 1 | Approval and rejection | Verified on Linux (engine/service/classifier tests) |
| 2 | Task cancellation (incl. cancel-while-paused) | Verified on Linux |
| 3 | Pause and resume | Verified on Linux |
| 4 | Actual app launching | Verified on Linux (real process launch/list/close); `notepad.exe` NOT verified |
| 5 | Actual text entry | **NOT verified** — Windows-only; Linux tests assert the honest "Windows-only (NOT SUPPORTED)" failure |
| 6 | Filesystem operations | Verified on Linux |
| 7 | File creation and verification | Verified on Linux (exact content comparison) |
| 8 | Ollama connection | Mocked HTTP + real client parsing only; no real Ollama server on this VM (nothing on `localhost:11434`); no 4 GB model downloaded |
| 9 | Model selection | Verified on Linux |
| 10 | Memory persistence (incl. interrupted-task recovery) | Verified on Linux |
| 11 | Agent tool execution | Verified on Linux |
| 12 | Error recovery | Verified on Linux |

### `dotnet test` localhost TCP investigation (Phase 4, per attempt)
- **A (14:30:56Z):** `VSTEST_CONNECTION_TIMEOUT=600`, `dotnet test --filter FullyQualifiedName~OllamaClientTests` → no connection in 240 s (exit 124). Both processes alive; vstest listening on `127.0.0.1:39413`; manual bash TCP connect to it succeeded.
- **B (14:35:00Z):** `dotnet vstest` on the built DLL with `--Diag` → no connection in 240 s (exit 124). **Decisive:** testhost log says "connected to 127.0.0.1:037439" but its socket's real remote endpoint was `[fd8b:4f84:7d32:99::1]:3128` — the sandbox egress proxy. The sandbox transparently redirects the testhost channel to the proxy, which never speaks the vstest protocol; vstest.console's log ends at "waiting for connection with timeout: 600000". On kill, testhost died with `EndOfStreamException` in `LengthPrefixCommunicationChannel.NotifyDataAvailable`.
- **C (14:39:12Z):** all proxy env vars unset (`http_proxy`/`https_proxy`/`HTTP_PROXY`/`HTTPS_PROXY`/`all_proxy`/`ALL_PROXY` removed, `NO_PROXY=localhost,127.0.0.1`) → still no connection in ~173 s; orphaned vstest/testhost killed manually. The redirect is network-level; testhost uses raw `TcpClient`, so env vars cannot fix it. Prior history: 90 s timeout failed 13:27, 600 s timeout failed 13:29.
- **Conclusion:** environment defect, not a code bug. On a normal Windows machine `dotnet test` should be used instead of the in-process runner.

### Stability audit (Phase 5) — verified, no change needed
- Retry path: retries re-execute the *same* step, max 3 attempts (2 retries); dedup blocks *different* steps sharing an ActionId (test pins this).
- `WaitIfPausedAsync` cannot spin when paused+cancelled (async gate).
- No `.Result`/`.Wait()` on the UI thread (the removed gate wait was the only one); `WpfApprovalService` uses `InvokeAsync` (legacy `WpfApprovalPrompt`'s sync `Invoke` is unwired dead code, documented in code/docs).
- `CancellationToken.None` calls in `MainViewModel` have no task token to thread (independent background ops — documented, not changed).
- Ollama failures return false / fall back to local summary, never crash.

## 5. Remaining limitations

- **WPF runtime/UI, `WindowsAppControl` typing/read-back/Save dialog, and the Notepad UI flow** — compile-verified only; never run on Windows. Windows 11 Notepad's tabbed UI may behave differently from the classic Save dialog; if dialog automation proves unreliable, the labelled verified direct-write fallback is the documented behaviour.
- **Playwright/Chromium research** — no test covers `ResearchAsync` at all; no Chromium on this VM.
- **Ollama model download and real inference** — not run (no 4 GB model on this 7.7 GB VM, by design).
- **Both installers** — NSIS/Inno scripts present but never executed (`makensis`/`iscc` not installed here).
- **Model switch for an already-running engine** — takes effect for new engine runs / after app restart (documented in LIMITATIONS.md).
- **Double prompting in Plan mode** — intentional per-request behaviour but a UX wart: plan review prompts per step, then the engine prompts again at execution time for each sensitive step. A later phase could add a scoped plan-approval token; none was invented.
- **PdfPig package resolution** — 32 build warnings, all NU1603 (PdfPig `0.1.9-alpha001-patch1` approximate matches); on real nuget.org, pin stable PdfPig 0.1.9.
- **Manual Windows pass (TESTS.md Test 10) + installer checks** — still to be done on a real Windows machine.

## 6. Windows-specific verification status

| Area | Status |
|---|---|
| Full solution build (incl. WPF App) | **Builds on Linux** with `-p:EnableWindowsTargeting=true`: 0 errors, 32 warnings. This validates C#/XAML compilation, NOT Windows runtime behaviour. |
| WPF runtime / UI | NOT VERIFIED — needs Windows |
| UI Automation / Notepad flow | NOT VERIFIED — needs Windows (manual test in TESTS.md) |
| Approval dialog end-to-end (modal, countdown, panel resolve) | Compile-verified only — needs Windows |
| Playwright Chromium | NOT VERIFIED — needs Windows + `playwright install chromium` |
| Ollama model download / inference | NOT VERIFIED — needs Windows + network |
| NSIS / Inno installer execution | NOT VERIFIED — needs Windows |
| `dotnet test` (vstest) | NOT RUNNABLE on this VM (proxy defect, proved); use on Windows |
| Core logic (102 tests via TestRunner) | VERIFIED on Linux |

## 7. Commands to build and run

On Windows (Visual Studio 2022 with .NET desktop workload, or .NET 8 SDK):
```
dotnet build AuroraOperator.sln -c Release
dotnet test
```
Install Ollama from ollama.com, then:
```
ollama pull qwen3:4b        # 8 GB+ RAM; or qwen3:1.7b on < 8 GB
```
Run the published `AuroraOperator.exe`; build the installer with NSIS or Inno Setup
from `src/AuroraOperator.Installer/`.

On this Linux VM (verification only — the app itself is Windows-only):
```
export PATH="$HOME/.dotnet:$PATH"
dotnet build AuroraOperator.sln -c Release -p:EnableWindowsTargeting=true
dotnet run --project tests/AuroraOperator.TestRunner -c Release
```
