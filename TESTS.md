# TESTS.md — Test plan and status

Last verified: **2026-11-21** (Phase 8).

- **TestRunner (this Linux VM): 248 passed, 0 failed, 248 total.**
  Run: `~/.dotnet/dotnet tests/AuroraOperator.TestRunner/bin/Release/
  net8.0/AuroraOperator.TestRunner.dll` (after building the solution).
  The suite was 31 tests at the first integrator run, 61 after Phase 1,
  89 after Phase 2, 102 after Phase 3/5, 184 after Phase 7, and **248
  after Phase 8** (64 new Phase 8 cases: model selection, Ollama
  retry, F5/F20/F13/F14 security, browser-download security).
- **`dotnet test`: 0 tests run on this VM — it cannot connect to
  testhost here.** This is an environment limitation, proved with
  `--Diag` logging; see "Why `dotnet test` does not run here" below.
  On a normal Windows machine `dotnet test` works and should be used
  instead; expect the same 248 tests (Phase 8).
- **Build:** `~/.dotnet/dotnet build AuroraOperator.sln -c Release
  -p:EnableWindowsTargeting=true` → **0 errors, 40 warnings** on a
  clean rebuild (32 NU1603 PdfPig + 4 CS8600 + 2 CS0067 + 2
  xUnit2013 — see [BUILD.md](BUILD.md)).
- **No test was converted into a fake pass.** Where a path cannot run
  on Linux, the test asserts the honest failure (e.g. Notepad UI flow
  returns "Windows-only (NOT SUPPORTED)" and launches nothing).
- **Real Ollama server: NOT available here** (Ollama is not installed,
  nothing listens on `localhost:11434`, no model was downloaded —
  deliberately, on this 7.7 GB VM). Ollama is verified over mocked
  HTTP only. A real-server check is a manual Windows item below.

## How the suite runs here

The automated suite lives in `tests/AuroraOperator.Tests/` (xUnit,
.NET 8). `dotnet test` uses vstest, whose testhost talks to
vstest.console over a localhost TCP socket — and this sandbox
transparently redirects that socket to its egress proxy (proved by
the testhost diag log; see below). So on this VM the same xUnit
`[Fact]`/`[Theory]` methods are executed in-process by
`tests/AuroraOperator.TestRunner/` (reflection, fresh instance per
test, `IDisposable` honoured, async `Task` methods awaited). The
TestRunner is a sandbox workaround, NOT part of the shipped product.

```powershell
# Windows (preferred): the real runner
dotnet test tests/AuroraOperator.Tests -c Release

# This Linux VM (workaround): build, then run the TestRunner DLL
~/.dotnet/dotnet build AuroraOperator.sln -c Release -p:EnableWindowsTargeting=true
~/.dotnet/dotnet tests/AuroraOperator.TestRunner/bin/Release/net8.0/AuroraOperator.TestRunner.dll
```

## Phase 4 verification — per-item status (2026-11-21)

| # | Item | Status | Evidence (test names) |
| --- | --- | --- | --- |
| 1 | Approval and rejection | ✅ Verified (Linux) | `ApprovalTests.ApproveThenExecutesExactAction`, `.RejectThenDoesNotExecute`, `.ExpiredThenDoesNotExecute`, `.CancelWhileWaitingThenDoesNotExecute`, `ApprovalServiceTests.TryResolve_ApprovesPendingRequest`, `.CancelPending_CancelsRequestForTask`, `.ExpiresWhenNeverResolved` |
| 2 | Task cancellation | ✅ Verified (Linux) | `AgentEngineTests.CancelledToken_ResultsInCancelledState`, `ApprovalTests.CancelWhileWaitingThenDoesNotExecute`, `Phase3Tests.CancelWhilePaused_ResultsInCancelledState_DoesNotHang` |
| 3 | Pause and resume | ✅ Verified (Linux) | `Phase3Tests.PauseMidTask_BlocksProgress_ResumeLetsItComplete`, `.PauseBeforeStart_BlocksUntilResume` (new in Phase 3/5; these tests caught a real deadlock — see Phase 5 notes) |
| 4 | Actual app launching | ✅ Verified on Linux (real process); ❌ Windows `notepad.exe` launch NOT verified | `Phase2Tests.ProcessAppControl_LaunchRealProcess_AppearsInList_ThenCloses` (launches a real Python sleeper, finds it, kills it). Windows Notepad launch is manual item 10 below |
| 5 | Actual text entry | ❌ NOT verified on Linux (Windows-only) | `Phase2Tests.NotepadWorkflow_RunAsync_OnLinux_FailsWindowsOnly_AndLaunchesNothing`, `.ProcessAppControl_TypeText_ThrowsPlatformNotSupported_OnNonWindows` assert the honest failure; real typing is manual item 10 below |
| 6 | Filesystem ops | ✅ Verified (Linux) | `FilesystemTests.CreateFolder_And_SearchFiles_Work`, `.OrganizeByExtension_MovesFiles_IntoExtensionFolders`, `.DuplicateFinder_GroupsIdenticalFiles_Only`, `Phase3Tests.FilesystemTools_NullBytePath_Rejected_WithClearFailure` |
| 7 | File creation and verification | ✅ Verified (Linux) | `Phase2Tests.Engine_ArgsOnly_WriteFile_WritesExactContent_WithoutAnyHintString`, `.AppTools_SaveAs_WithLinuxTestDouble_WritesExactContentViaVerifiedFallback`, `DocumentTests.CreateExcelFromCsv_ProducesReadableWorkbook`, `.CreatePowerPoint_And_WordReport_ProduceFiles` |
| 8 | Ollama connection | ✅ Verified over mocked HTTP; ❌ real server NOT verified | `OllamaClientTests` (5 tests: list/chat/generate/streaming/unreachable), `Phase3Tests.ModelManager_HealthCheck_FalseWhenOllamaDown_TrueWhenUp`, `.OllamaClient_UnloadModel_ReturnsFalseOnHttpError`. Real Ollama server not installed here; no 4 GB model downloaded |
| 9 | Model selection | ✅ Verified (Linux) | `LanguageAndModelTests.SelectBestModel_16Gb_SelectsQwen3_4b`, `.SelectBestModel_4Gb_SelectsFallback`, `Phase3Tests.ModelManager_UnloadModel_UsesOllamaClient_NoSecondHttpClient` |
| 10 | Memory persistence | ✅ Verified (Linux) | `MemoryStoreTests` (4 tests), `Phase3Tests.InterruptedTask_CancelledAndFailedStates_RoundTrip_ThroughMemoryStore`, `.CorruptRows_GarbageEnumsAndBadDate_DoNotFailLoads` |
| 11 | Agent tool execution | ✅ Verified (Linux) | `AgentEngineTests` (4 tests), `ApprovalTests` (13 tests), `Phase2Tests.Engine_*`, `CodingTests.CreatePythonProject_And_RunTests_Passes` |
| 12 | Error recovery | ✅ Verified (Linux) | `Phase3Tests.Retry_FailingThenSucceedingTool_ReExecutesSameStep_AndSucceeds`, `.Retry_PermanentlyFailingTool_StopsAfterMaxRetries`, `ApprovalTests.ExpiredThenDoesNotExecute`, `Phase3Tests.CorruptRows_...`, `OllamaClientTests.IsAvailable_ReturnsFalse_WhenServerUnreachable` |

## Map (by test class)

| Class | What it covers | Count |
| --- | --- | --- |
| `AgentEngineTests` | Ask/Plan/Autonomous modes, pre-cancelled token, real folder creation | 4 |
| `ApprovalTests` | Per-request approval gate, wrong task/action/parameters, forged flags, duplicate ActionId, organise without approval, low-risk gating, parameter-hash binding | 13 |
| `ApprovalServiceTests` | Broker expiry, resolve, cancel-pending, audit logging with risk + hash | 5 |
| `RiskClassifierTests` | Delete/rename/move/organise/list/search/create-folder/browser-post/research/write-file-existing-vs-new, forged flags | 8 |
| `PermissionRiskTests` | `RequiresApproval` / `IsRiskAutoAllowed` matrix, category grants never auto-approve Sensitive+ | 4 |
| `PermissionTests` | Destructive-request detection, delete-all guard, no-prompt safe default | 3 |
| `PlannerTests` | Delete-all refusal plan, create-folder plan shape, CSV→Excel plan | 3 |
| `LanguageAndModelTests` | Chinese/Urdu/English detection, model selection by RAM, hardware detection | 6 |
| `FilesystemTests` | Create/search, duplicate grouping, organise by extension | 3 |
| `DocumentTests` | CSV→Excel readable workbook, PowerPoint + Word creation | 2 |
| `CodingTests` | Python project scaffold + real test run (pytest/unittest) | 1 |
| `OllamaClientTests` | Mocked-HTTP list/chat/generate/streaming/unreachable | 5 |
| `MemoryStoreTests` | Conversations, secret keys never stored, task round-trip, JSON export | 4 |
| `Phase2Tests` | Text extraction (15 cases), structured Args, Notepad flow + verified save fallback, real process launch, Windows-only failures | 28 |
| `Phase3Tests` | Pause/resume/cancel-while-paused, retry semantics, interrupted-task recovery, corrupt rows, overwrite gating, Ollama unload/health-check, null-byte rejection | 13 |
| **Total** | | **102** |

## Why `dotnet test` does not run here (per-attempt record)

Environment: Linux VM, .NET SDK 8.0.428, Microsoft.NET.Test.Sdk
17.8.0, VSTest 17.11.2. Sandbox egress proxy
`http://hatch-egress-proxy:3128` (IPv6 `[fd8b:4f84:7d32:99::1]:3128`)
is set in `http_proxy`/`https_proxy`/`HTTP_PROXY`/`HTTPS_PROXY`/
`all_proxy`/`ALL_PROXY`; `NO_PROXY` already includes
`localhost,127.0.0.1,::1`. Prior history in `build.log`:
`dotnet test` failed after 90 s (2026-11-21T13:27), then with
`VSTEST_CONNECTION_TIMEOUT=600` failed after 600 s (13:29).

- **Attempt A — 2026-11-21T14:30:56Z.** `VSTEST_CONNECTION_TIMEOUT=600
  dotnet test tests/AuroraOperator.Tests -c Release --filter
  "FullyQualifiedName~OllamaClientTests" --no-build`, bounded by
  `timeout 240`. Outcome: no connection after 240 s; exit 124
  (wrapper bound). While it ran, `ps` showed both processes alive:
  vstest.console listening on `127.0.0.1:39413`, testhost started
  with `--port 39413 --endpoint 127.0.0.1:039413 --role client`.
  A manual `bash /dev/tcp` connect to the listener succeeded, so
  loopback TCP itself works.
- **Attempt B — 2026-11-21T14:35:00Z.** `dotnet vstest
  tests/AuroraOperator.Tests/bin/Release/net8.0/AuroraOperator.Tests.dll
  --Diag:/tmp/vstest-diag-B/diag.log --Tests:OllamaClientTests`,
  `VSTEST_CONNECTION_TIMEOUT=600`, bounded by `timeout 240`.
  Outcome: no connection after 240 s; exit 124. Diag evidence
  (console log): `SocketServer.Start: Listening on endpoint :
  127.0.0.1:37439`, then `TestRequestSender.
  WaitForRequestHandlerConnection: waiting for connection with
  timeout: 600000.` — and nothing after. Diag evidence (host log
  `diag.host.26-11-21_14-35-01_73164_5.log`): testhost
  `SocketClient.Start: connecting to server endpoint:
  127.0.0.1:037439` → `OnServerConnected: connected to server
  endpoint: 127.0.0.1:037439` — but every subsequent
  `MessageLoopAsync: Polling on remoteEndPoint:
  [fd8b:4f84:7d32:99::1]:3128 localEndPoint:
  [fd8b:4f84:7d32:99::2]:45238`. The socket testhost believes is
  vstest.console is actually the **egress proxy**: the sandbox
  transparently redirects testhost's outbound TCP connection to the
  proxy, which never speaks the vstest length-prefixed protocol, so
  testhost polls forever and vstest.console never sees a connection.
  When the wrapper killed vstest.console, testhost's loop died with
  `EndOfStreamException` at `LengthPrefixCommunicationChannel.
  NotifyDataAvailable` (`BinaryReader.ReadString`) — the channel was
  never a vstest channel.
- **Attempt C — 2026-11-21T14:39:12Z.** All proxy env vars unset
  (`env -u http_proxy -u https_proxy -u HTTP_PROXY -u HTTPS_PROXY
  -u all_proxy -u ALL_PROXY`, `NO_PROXY=localhost,127.0.0.1`),
  `VSTEST_CONNECTION_TIMEOUT=600`, `dotnet test --filter
  "FullyQualifiedName~OllamaClientTests.ListModels_ParsesTagsResponse"`,
  bounded by `timeout 180`. Outcome: still no connection after
  ~173 s; vstest.console + testhost were still alive and waiting and
  were killed manually. This matches the diagnosis: the redirect is
  network-level (testhost uses a raw `TcpClient`, not an HTTP
  client), so env vars cannot fix it.

Conclusion: `dotnet test` is unusable on this VM for an environment
reason (transparent proxy redirection of the testhost channel), not
because of any test or code defect. The TestRunner results above are
the authoritative Linux results. Re-check on a machine without the
egress proxy before concluding anything else.

## Manual Windows pass (Test 10) — NOT run here

On a Windows 10/11 machine with Ollama running and `qwen3:4b`
(or `qwen3:1.7b`) pulled:

1. Launch the app; Ollama status dot is green; model list shows the
   pulled model.
2. Send a task in each mode (Ask, Plan, Autonomous); Ask changes
   nothing; Plan stops at WaitingApproval with the plan shown;
   Autonomous executes.
3. Approve and deny a Sensitive step (e.g. organise a folder, write
   over an existing file) from the Approvals panel; denied steps do
   not execute; the audit log records both decisions.
4. Stop / Pause / Resume a running task from the UI; a paused task
   shows Paused and resumes; a stopped task ends Cancelled.
5. **Notepad flow:** "Open Notepad and write Hello World" — Notepad
   launches, the text is typed, read-back verifies the exact match;
   with a save path, the Save dialog saves the file and the content
   is verified byte-for-byte.
6. Theme toggle (dark/light); model switch in Model Manager (takes
   effect for new engine runs — restart the app); attach a file;
   double-click a recent task to reload it; restart the app and
   confirm recent tasks persist.
7. Web research with Chromium installed: a real report is written
   with sources; a CAPTCHA page is recorded as blocked.
8. Kill Ollama mid-task: the app reports Ollama offline, does not
   crash, and Ask mode falls back to the local summary.

## Installer checks (manual, Windows only) — NOT run here

No installer has ever been built on this VM (`makensis`/`iscc` are
not installed). On Windows:

- Fresh VM, no Ollama: installer downloads Ollama, pulls the right
  model for the VM's RAM, app launches, status dot green.
- VM with Ollama already installed: installer skips the download.
- VM with 4 GB RAM: `qwen3:1.7b` is pulled, not `qwen3:4b`.
- VM with 8 GB RAM: `qwen3:1.7b` is pulled, not `qwen3:4b` (Phase 7
  alignment — auto-selection and the documented profiles agree).
- VM with 16 GB RAM: `qwen3:4b` is pulled.
- Kill the network mid-download: install aborts and rolls back the
  app directory (no half-installed app).
- Uninstall with "remove models": `%USERPROFILE%\.ollama\models` is
  gone; with "keep models": it remains.
