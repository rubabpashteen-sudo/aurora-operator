# LIMITATIONS.md — Honest limits

Last verified: **2026-11-21** (Phase 8). Evidence: full-solution build
`~/.dotnet/dotnet build AuroraOperator.sln -c Release
-p:EnableWindowsTargeting=true` → **0 errors, 40 warnings** (NU1603
PdfPig plus pre-existing test-code warnings, see [BUILD.md](BUILD.md)); in-process TestRunner →
**248 passed, 0 failed, 248 total**. `dotnet test` (vstest) does **not**
run on this Linux VM — see "Why `dotnet test` does not run here" below;
that is an environment limitation, not a test failure. No ZIP was
packaged in this phase.

Status categories used below:

- **(A) Implemented and verified** — code exists and has been executed
  here (build or TestRunner) or on Windows, with the evidence cited.
- **(B) Implemented but not yet verified on Windows** — code exists,
  compiles, and is covered by Linux tests where testable, but the
  Windows-only execution path has never run.
- **(C) Partially implemented** — works in part; the missing piece is
  stated exactly.
- **(D) Not implemented** — absent, not merely untested.
- **(E) Requires manual Windows testing** — cannot be verified on Linux
  at all; the checklist is in [TESTS.md](TESTS.md).

## Corrected stale claims

- **The WPF build does NOT fail on Linux.** Earlier versions of this
  file said the App project fails to build on Linux. That was wrong:
  with `-p:EnableWindowsTargeting=true` the full solution — Core, App
  (WPF), Tests, TestRunner — builds on Linux with 0 errors (final
  build.log entry, 2026-11-21). What the Linux build does **not**
  prove is that the WPF app *runs* on Windows: compiling
  `net8.0-windows` with Windows targeting is a compile check only.
  Do not claim Windows compatibility from a Linux build.
- **The engine DOES take an approval prompt now (Phase 1).** Earlier
  versions said `AgentEngine`'s constructor takes no UI prompt and
  `PermissionManager` has no prompt hook. That is outdated:
  `AgentEngine` now takes an optional `IApprovalService`
  (plus `ApprovalSettings`, `AuditLog`, `planProvider`), enforces
  approval in its dispatcher before Sensitive+ steps, and App wires
  the real `WpfApprovalService` into it. `PermissionManager.
  RequestApprovalAsync` is the legacy low-risk path and is **not**
  what the engine uses.

## (A) Implemented and verified (Linux, 2026-11-21)

- **Full-solution build on Linux** — Core, App (WPF), Tests,
  TestRunner: 0 errors, 32 warnings, all NU1603 (UglyToad.PdfPig
  0.1.9-alpha001-patch1 dependencies resolved to approximate
  1.7.0-custom-5 matches). See [BUILD.md](BUILD.md).
- **Automated suite via TestRunner: 102/102 pass.** Covers:
  approval approve/reject/expire/cancel, wrong-task/wrong-action/
  wrong-parameter approvals, forged `approved=true` flags, duplicate
  ActionId blocking, pause/resume mid-task, cancel-while-paused,
  retry re-execution (max 3 attempts), filesystem search/create/
  organise/rename/copy/move/write, duplicate finding, CSV→Excel,
  PowerPoint/Word creation, Python project scaffolding + test runs,
  Ollama client over mocked HTTP (chat/generate/list/streaming,
  availability, unload, health check), model selection by RAM,
  language detection, planner + delete-all refusal, MemoryStore
  round-trips including Cancelled/Failed/Running tasks and corrupt
  rows (garbage enums, unparseable dates), null-byte path rejection,
  and overwrite-of-existing-file approval gating. Per-item status is
  in [TESTS.md](TESTS.md).
- **Approval service (Phase 1)** — `IApprovalService` /
  `ApprovalService` / `RiskClassifier` / `RiskLevel` /
  `ApprovalOutcome` / `ApprovalSettings`, per-request binding to
  (TaskId, ActionId, ParameterHash), 5-minute default expiry,
  fail-closed on error/timeout/cancel, audit-logged. Verified by
  `ApprovalTests` and `ApprovalServiceTests`.
- **Structured plan args + real text (Phase 2)** — `PlanStep.Args`
  carries exact text (Unicode, newlines); the engine executes from
  Args, legacy description hints only fill gaps. Verified by
  `Phase2Tests`.
- **Real process launch on Linux** — `ProcessAppControl` launches a
  real process, finds it in the process list, and closes it
  (`Phase2Tests.ProcessAppControl_LaunchRealProcess_AppearsInList_
  ThenCloses`). `TypeText` / UI Automation reads / Save dialog
  throw `PlatformNotSupportedException` on non-Windows instead of
  pretending.
- **Notepad save fallback (verified path)** — when the user requested
  a save path and the Save dialog cannot be driven,
  `NotepadWorkflow.SaveAsync` writes the file directly and verifies
  its content byte-for-byte, labelling the result as a fallback
  (`Phase2Tests.AppTools_SaveAs_WithLinuxTestDouble_...`). The
  Windows Save-dialog path itself is (B)/(E), not (A).
- **MemoryStore corrupt-row tolerance (Phase 5)** — a conversation
  row with an unparseable date still loads (date falls back to now);
  a task row with unknown enum strings loads as
  Unknown/Ask/Created; a single bad row never fails a whole search,
  list, or export. Verified by
  `Phase3Tests.CorruptRows_GarbageEnumsAndBadDate_DoNotFailLoads`.
- **Pause/resume/cancel (Phase 5)** — the engine pause gate is now
  a truly async `TaskCompletionSource` gate: pausing no longer
  blocks the calling thread (the previous `ManualResetEventSlim`
  implementation deadlocked any direct caller of `RunAsync` — the
  new `Phase3Tests` caught it, and it is fixed). Cancel-while-paused
  ends Cancelled; it never spins or hangs. Verified by
  `Phase3Tests.PauseMidTask_...`, `PauseBeforeStart_...`,
  `CancelWhilePaused_...`.
- **Ollama client over mocked HTTP** — chat/generate/list/streaming
  (NDJSON, `done:true`), HTTP errors, timeouts, unreachable server
  → `IsAvailableAsync` false, health check false, no crash.
  Verified by `OllamaClientTests` and `Phase3Tests`.
- **Dispose hygiene (Phase 5)** — `OllamaClient` disposes a client
  it created; `ModelManager.UnloadModelAsync` now uses the injected
  `OllamaClient` (no second `HttpClient`); `AgentEngine` and
  `MainViewModel` are `IDisposable`; App releases the view model,
  engine, memory store, Ollama client and `HttpClient` on exit and
  logs `TaskScheduler.UnobservedTaskException`.

## (B) Implemented but not yet verified on Windows

- **WPF runtime and UI** (`AuroraOperator.App`) — builds on Linux
  (see above) but has never been executed: no window has ever been
  shown, no theme toggled, no approval dialog displayed. All XAML,
  data binding, `WpfApprovalService` dialog flow, `WindowsAppControl`
  UI Automation, and the `DispatcherTimer` UI refresh loop are in
  this category.
- **UI Automation / `WindowsAppControl`** — launch/focus/close/list
  via `Process` is verified on Linux; typing via `SendInput`,
  reading control text via `ValuePattern`, and driving the Notepad
  Save dialog are Windows-only and unverified.
- **Notepad UI flow** — launch notepad.exe → wait for window →
  focus → type real text → read back and compare exactly. On Linux
  it fails honestly with "Windows-only (NOT SUPPORTED)" and launches
  nothing (`Phase2Tests`); the real flow has never run.
- **Playwright Chromium / web research** — `BrowserTools.
  ResearchAsync` compiles; no Chromium is installed on this VM
  (`~/.cache/ms-playwright` does not exist) and **no test covers it
  at all**, not even the honest-failure path. Research has never
  produced a real report anywhere.
- **Ollama model download** — `ModelManager.DownloadModelAsync` /
  `EnsureBestModelAsync` and the installers' `ollama pull` step have
  never run. No model was downloaded on this VM (deliberately: the
  VM has 7.7 GB RAM and no Ollama server). `qwen3:8b` is ~5.2 GB,
  `qwen3:4b` is ~2.6 GB, `qwen3:1.7b` ~1.4 GB; verify on a Windows
  machine with real network access.
- **Installers** — NSIS (`AuroraOperator.nsi`) and Inno Setup
  (`InnoSetup.iss`) scripts exist, but neither `makensis` nor `iscc`
  is installed on this VM, so no installer has ever been built or
  run. The installer checklist in [TESTS.md](TESTS.md) is entirely
  unexecuted. Phase 8 changed the Inno post-install health check
  from advisory-only to cancelling setup on failure
  (`HealthCheckFailed` + `WizardForm.Close`); that path has never
  executed and must be forced-failure tested on Windows first.
- **`OllamaInstaller`** — implemented but uncalled (nothing in App
  references it) and untested.
- **Model switch takes effect for new engine runs.** `ModelManager`
  switches the model, but the `AgentEngine` instance is built at
  startup with the model name from that moment. After switching
  models in Model Manager, restart the app (or re-send after an
  engine rebuild) for the new model to be used. The UI says so in
  its status message.
- **`MemoryStore.ListRecentTasks` shape** — verified: it returns
  `AgentTask` records, matching `CONTRACTS.md` and the ViewModel's
  use. No adjustment was needed.

## (C) Partially implemented

- **Screen context.** The toggle records the request with the task;
  the snapshot itself depends on `WindowsAppControl` UI Automation
  work (see (B)). The toggle is UI state only until that runs.
- **Mica.** True Windows 11 Mica needs DWM backdrop APIs; the UI
  approximates it with `WindowChrome` plus semi-transparent
  charcoal surfaces. It looks like frosted glass on dark, but it is
  not the system Mica material.
- **Retry re-execution (documented, by design).** A retry
  re-executes the *same* step (up to 3 attempts total: 1 + 2
  retries). The ActionId dedup blocks a *different* step re-using
  an ActionId; it does not block a retry of the step itself. Tools
  are expected to be idempotent or fail-safe on re-execution
  (write-file rewrites identical content, create-folder is a no-op
  when the folder exists, copy refuses to overwrite, organise moves
  only what is left). A tool that is not safe to run twice must fail
  loudly on its second call rather than silently repeating its side
  effect. See `AgentEngine` docs and `Phase3Tests.
  Retry_FailingThenSucceedingTool_...`.
- **File safety scope.** There is **no sandboxed base directory**:
  the agent operates on the user's own machine at the user's
  direction, so directory-traversal restrictions against a base dir
  do not apply. What is enforced: paths containing a NUL byte are
  rejected with a clear failure (`FilesystemTools`,
  `NotepadWorkflow.SaveAsync`); copy uses `overwrite:false`;
  move/rename throw when the destination exists; writing over an
  *existing* file is classified `SensitiveFileModification` and is
  approval-gated by the engine before the tool runs (verified by
  `Phase3Tests.WriteFile_ExistingFile_BlockedWithoutApproval_...`).
  Direct tool calls outside the engine do not get the approval gate
  — tools are only invoked through the engine.
- **`WpfApprovalPrompt` (legacy).** The `Func<ApprovalRequest,
  Task<bool>>` adapter exists but is **not wired anywhere** (nothing
  calls `AsFunc()`); the live path is `WpfApprovalService`, which
  uses `Dispatcher.InvokeAsync`, not the prompt's synchronous
  `Dispatcher.Invoke`. The prompt is kept for compatibility only.
- **`MainViewModel` background Ollama calls use
  `CancellationToken.None`.** `RefreshOllamaStatusAsync`,
  `RefreshModelsAsync`, `SwitchModelAsync` and `DownloadModelAsync`
  have no task cancellation token to thread — they are independent
  background/UI operations, not part of a running task, so there is
  nothing to cancel them with. There is no UI affordance to cancel a
  model download today. The running task's token *is* threaded
  through the engine, tools and Ollama chat/generate calls.

## (D) Not implemented

- No macOS/Linux client (Windows 10/11 64-bit only, by design).
- No remote Ollama servers, no cloud models, no API keys
  (localhost:11434 only, by design).
- No delete action in `FilesystemTools` (by design — deletion is
  approval-gated elsewhere and never performed silently).
- No CAPTCHA/login bypass in web research (by design — blocked
  pages are reported as blocked).
- No auto-resume of interrupted tasks: a task left `Running` when
  the app was killed loads back as `Running` and is treated as
  interrupted — it is never silently resumed or re-executed. (The
  round-trip itself is verified; see (A).)
- No sandboxing of tool file access beyond the NUL-byte /
  no-overwrite rules in (C).

## (E) Requires manual Windows testing

1. `dotnet build AuroraOperator.sln -c Release` on Windows,
   including the WPF app (compile-only check already done on
   Linux; this is the runtime check).
2. `dotnet test tests/AuroraOperator.Tests -c Release` on Windows —
   the suite should run there (it cannot run on this Linux VM; see
   below). Expect 102 tests.
3. Run the manual pass in [TESTS.md](TESTS.md) (Test 10): launch
   the app with Ollama running, send a task in each mode,
   approve/deny, Stop/Pause/Resume from the UI, theme toggle, model
   switch, attach a file, double-click a recent task, restart and
   confirm recent tasks persist, and the full Notepad flow
   (launch → type → read-back verify → Save dialog).
4. Web research end-to-end with Chromium installed.
5. Build both installers on a clean Windows VM (one with and one
   without Ollama pre-installed) and follow the installer checklist
   in [TESTS.md](TESTS.md).
6. Model download (`qwen3:4b` / `qwen3:1.7b` / `qwen3:0.6b`) via installer and via
   Model Manager, on 16 GB, 8 GB and 4 GB machines.

## Why `dotnet test` does not run here (environment, not code)

`dotnet test` / `dotnet vstest` cannot run on this Linux VM, and the
cause was re-proved on 2026-11-21 with `--Diag` logging. Summary of
what was tried (full per-attempt record in [TESTS.md](TESTS.md)):

- `dotnet test` with the default 90 s timeout: "vstest.console
  process failed to connect to testhost process after 90 seconds"
  (also in the older build.log, 2026-11-21T13:27 and 13:29, the
  second with `VSTEST_CONNECTION_TIMEOUT=600` failing after 600 s).
- 2026-11-21 attempt A: `VSTEST_CONNECTION_TIMEOUT=600`,
  `dotnet test --filter FullyQualifiedName~OllamaClientTests`:
  still no connection after 240 s (wrapper bound).
- Attempt B: `dotnet vstest` on the built test DLL directly with
  `--Diag`: vstest.console listened on `127.0.0.1:37439` and waited
  600 s; testhost reported "connected to server endpoint:
  127.0.0.1:037439" — but its socket's actual remote endpoint was
  `[fd8b:4f84:7d32:99::1]:3128`, the sandbox egress proxy. The
  sandbox transparently redirects the testhost's outbound TCP
  connection to the proxy, so testhost talks to the proxy (which
  never speaks the vstest length-prefixed protocol) while
  vstest.console never sees a connection. When the wrapper killed
  vstest.console, testhost's message loop died with
  `EndOfStreamException` in `LengthPrefixCommunicationChannel.
  NotifyDataAvailable`.
- Attempt C: all proxy env vars unset
  (`http_proxy`/`https_proxy`/`HTTP_PROXY`/`HTTPS_PROXY`/
  `all_proxy`/`ALL_PROXY` removed, `NO_PROXY=localhost,127.0.0.1`):
  still no connection after ~173 s. Expected: the redirect is
  network-level, not env-var driven, and testhost uses a raw
  `TcpClient`, not an HTTP client.
- A plain TCP connect from bash to the vstest listener succeeds, so
  loopback TCP itself works — only the testhost↔console channel is
  diverted.

Consequence: on this VM the 102 xUnit tests are executed by the
in-process `AuroraOperator.TestRunner` instead (same test methods,
same assertions, fresh instance per test, `IDisposable` honoured,
async methods awaited). On a normal Windows machine `dotnet test`
works and should be used instead. The TestRunner is a sandbox
workaround, not part of the shipped product.

## Product limits (by design)

- Windows 10/11 64-bit only; no macOS/Linux client.
- Ollama on `localhost:11434` only — no remote servers, no cloud
  models, no API keys.
- Local models are small (`qwen3:8b` on 32 GB RAM, `qwen3:4b`
  primary on 16 GB+ RAM, `qwen3:1.7b` auto-selected on 8 GB,
  `qwen3:0.6b` below 8 GB — Phase 8 alignment, all three selection
  mechanisms agree): they are good at
  planning, files, and research summaries, and weaker than frontier
  cloud models at long-form reasoning.
- The agent never deletes files without explicit approval, and
  blanket deletions are refused outright.
- Web research cannot bypass CAPTCHAs or logins; blocked pages are
  reported as blocked.

## Phase 8 notes (2026-11-21)

- **Publish sizes changed:** self-contained is now 270 MB (was
  384 MB) and framework-dependent 109 MB (was 224 MB) — the Linux
  Playwright Node runtime is excluded at publish time. The trimmed
  build has never launched on Windows; re-verify the launch and the
  Chromium tests against it.
- **DPAPI preference protection / DB ACL:** implemented in code
  (`SecretProtector`, `MemoryStore`), never executed on Windows.
  Conversations and tasks remain plaintext SQLite by design; a
  secret pasted into a conversation turn stays in plaintext until
  the user clears memory. Log redaction cannot catch a secret
  written as bare free prose with no key marker.
- **Ollama retry:** bounded retry exists in `OllamaClient` (3
  attempts, 5xx/network/timeout only). It has only been tested
  against mocked HTTP, never against a real Ollama server.
