# Aurora Operator — Shared API Contracts (READ FIRST)

All builders must follow these exact public type names/signatures so the
solution compiles together. Core targets `net8.0` (cross-platform).
App targets `net8.0-windows` + `UseWPF`. Tests target `net8.0` + xUnit.

Namespaces: `AuroraOperator.Core`, `AuroraOperator.Core.Agent`,
`AuroraOperator.Core.Ollama`, `AuroraOperator.Core.Tools`,
`AuroraOperator.Core.Memory`, `AuroraOperator.Core.Security`.

## Core shared types — file `src/AuroraOperator.Core/Models.cs`
```csharp
namespace AuroraOperator.Core;
public enum AgentMode { Ask, Plan, Autonomous }
public enum TaskState { Created, Planning, WaitingApproval, Running, Paused, Completed, Failed, Cancelled, Blocked }
public enum StepStatus { Pending, Running, Succeeded, Failed, Skipped, NeedsApproval }
public enum PermissionCategory { FileAccess, BrowserAccess, ScreenObservation, KeyboardMouse, ApplicationLaunch, TerminalExecution, SystemSettings, NetworkAccess }
public enum Language { English, Chinese, Urdu, Unknown }
public sealed record ToolResult(bool Success, string Summary, string? Details = null, string? OutputPath = null, IReadOnlyList<string>? Artifacts = null);
public sealed record PlanStep(string Id, string Title, string ToolName, string Description, bool RequiresApproval = false, string? Precondition = null, string? CompletionCondition = null)
{ public StepStatus Status { get; set; } = StepStatus.Pending; public string? ResultSummary { get; set; }
  /// Structured machine-readable arguments (action, path, destination, name, text, verify, …). Args win over any key=value hints scanned out of Description (legacy fallback only). The Planner populates Args for every step it creates.
  public IReadOnlyDictionary<string, string> Args { get; init; } = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase); }
public sealed record TaskPlan(string TaskId, string UserRequest, Language Language, IReadOnlyList<PlanStep> Steps, string Summary);
public sealed record AgentTask(string TaskId, string UserRequest, Language Language, AgentMode Mode)
{ public TaskState State { get; set; } = TaskState.Created; public TaskPlan? Plan { get; set; } public List<string> Log { get; } = new(); public List<string> Artifacts { get; } = new(); public string? FinalReport { get; set; } }
public sealed record ApprovalRequest(string Id, string TaskId, string Title, string Description, string ExactCommandOrChange, PermissionCategory Category)
{
  /// Id of the plan step this request is for. ActionId is the stable id the approval is bound to (usually the step's Id).
  public string StepId { get; init; } = string.Empty;
  public string ActionId { get; init; } = string.Empty;
  /// Risk of the requested action, from RiskClassifier.
  public RiskLevel RiskLevel { get; init; } = RiskLevel.NormalReversible;
  /// Canonical exact parameters (sorted key=value pairs) the approval covers; ParameterHash is their SHA256 (lower-case hex).
  public string ExactParameters { get; init; } = string.Empty;
  public string ParameterHash { get; init; } = string.Empty;
  public DateTimeOffset RequestedAtUtc { get; init; } = DateTimeOffset.UtcNow;
  /// When the request stops being answerable. Null means the service default timeout applies.
  public DateTimeOffset? ExpiresAtUtc { get; init; }
  public ApprovalOutcome Outcome { get; init; } = ApprovalOutcome.Pending;
  public DateTimeOffset? DecidedAtUtc { get; init; }
  /// The hash this approval is bound to: the supplied ParameterHash when present, otherwise the SHA256 of ExactParameters (or ExactCommandOrChange).
  public string EffectiveParameterHash => string.IsNullOrWhiteSpace(ParameterHash)
      ? ApprovalService.ComputeParameterHash(string.IsNullOrWhiteSpace(ExactParameters) ? ExactCommandOrChange : ExactParameters)
      : ParameterHash;
}
```

## Security — approval service (Phase 1, shipped) — `Core/Security/ApprovalService.cs`, `RiskClassifier.cs`
- `RiskLevel`: `ReadOnly = 0`, `NormalReversible = 1`, `SensitiveFileModification = 2`, `Destructive = 3`, `Administrator = 4`, `ExternalCommunication = 5`.
- `ApprovalOutcome`: `Pending, Approved, Rejected, Expired, Cancelled`.
- `ApprovalSettings(bool AutoApproveLowRisk = true, TimeSpan? ApprovalTimeout = null)`: `EffectiveTimeout` defaults to 5 minutes.
- `IApprovalService`: `Task<ApprovalOutcome> RequestApprovalAsync(ApprovalRequest request, CancellationToken ct)` (throws `OperationCanceledException` when `ct` fires while waiting), `void CancelPending(string taskId)`, `bool TryResolve(string requestId, ApprovalOutcome outcome)` (default: false), `IReadOnlyList<ApprovalRequest> PendingRequests` (default: empty), `event EventHandler? PendingChanged` (default: never raised). An approval is bound to one (TaskId, ActionId, ParameterHash) triple; errors, timeouts and cancellations are denials — never fail open.
- `ApprovalService(AuditLog? auditLog = null, TimeSpan? defaultTimeout = null, string source = "broker") : IApprovalService`: in-memory broker; requests expire after the timeout (default 5 min, or the request's own `ExpiresAtUtc`); every request and decision is audit-logged. Static helpers: `CreateRequest(taskId, step, risk, canonicalParameters, timeout, category)`, `CanonicalParameters(toolName, args, description)` (keys sorted case-insensitively), `ComputeParameterHash(canonicalParameters)` (SHA256, lower-case hex).
- `RiskClassifier.Classify(PlanStep step)` / `Classify(toolName, action, description, hints)`: pure function of what the step does. Args win over legacy hints; "approved=true" in plan data never lowers risk; unknown modifying actions default to `SensitiveFileModification`. Write-file / save-as / notepad-write to an *existing* file is `SensitiveFileModification`, to a new file `NormalReversible`; copy to an existing destination is `Sensitive`; launch/type/close-app is `NormalReversible`; browser research is `ReadOnly`, browser post/upload/send is `ExternalCommunication`; delete/wipe/format is `Destructive`; terminal/admin is `Administrator`.

## Ollama — `Core/Ollama/`
- `OllamaClient(HttpClient http, string baseUrl = "http://localhost:11434", TimeSpan? timeout = null)`:
  `Task<bool> IsAvailableAsync(CancellationToken ct)`, `Task<IReadOnlyList<string>> ListModelsAsync(CancellationToken ct)`,
  `Task<string> ChatAsync(string model, IEnumerable<ChatMessage> messages, CancellationToken ct)` (non-streaming, POST /api/chat),
  `Task<string> GenerateAsync(string model, string prompt, CancellationToken ct)` (POST /api/generate),
  `IAsyncEnumerable<string> ChatStreamAsync(...)` (streaming, NDJSON lines).
  `Task<bool> PullModelAsync(string model, IProgress<double>? progress, CancellationToken ct)` (POST /api/pull, streaming progress).
  `Task<bool> UnloadModelAsync(string name, CancellationToken ct)` (POST /api/generate with `keep_alive = 0`; false, never throws, on HTTP failure).
  `public sealed record ChatMessage(string Role, string Content);`
  Must parse Ollama JSON with System.Text.Json, handle `done:true`, errors, timeouts. Constructor for tests must accept a custom `HttpMessageHandler`.
  `OllamaClient` disposes an `HttpClient` it created itself; one created from a caller-supplied `HttpClient` is owned by the caller (App disposes it on exit).
- `ModelCatalog`: `public static IReadOnlyList<ModelInfo> All { get; }` with
  `ModelInfo(string Name, long ApproxSizeBytes, int MinRamGb, string Role)` for
  `qwen3:4b` (primary, 4GB RAM min, ~2.6GB), `qwen3:1.7b` (fallback, 2GB, ~1.4GB),
  `qwen3.5:2b` (vision, optional), `qwen2.5-coder:3b` (coding, optional).
  `public static string SelectBestModel(long totalRamBytes, long availableRamBytes)`.
- `ModelManager(OllamaClient client, string modelsDir)`: `Task<ModelInfo> EnsureBestModelAsync(IProgress<string> progress, CancellationToken ct)`,
  `Task<bool> DownloadModelAsync(string name, IProgress<double> progress, CancellationToken ct)`,
  `Task<bool> SwitchModelAsync(string name, CancellationToken ct)`, `Task UnloadModelAsync(string name, CancellationToken ct)` (delegates to `OllamaClient.UnloadModelAsync` — no second `HttpClient` is created here),
  `Task<bool> HealthCheckAsync(CancellationToken ct)` (false when Ollama is down, never throws),
  `string CurrentModel { get; }`, `int ContextLength { get; set; } = 8192`.
- `HardwareInfo`: `static HardwareReport Detect()` with `TotalRamBytes, AvailableRamBytes, CpuName, CpuCores, GpuName, OsVersion, Is64Bit, FreeDiskBytes(string path), WindowsVersion` — Windows via WMI/registry guarded, Linux via /proc/meminfo fallback (must compile on Linux).
- `OllamaInstaller`: `Task<OllamaInstallResult> EnsureInstalledAsync(IProgress<string> progress, CancellationToken ct)` — detects `ollama.exe` on PATH / `%LOCALAPPDATA%\Programs\Ollama\ollama.exe`, returns `AlreadyInstalled/Installed/Failed + Message`; on non-Windows returns Failed with honest message (never fake). `IsOllamaInstalled()`, `OllamaDownloadUrl` const `https://ollama.com/download/OllamaSetup.exe`.

## Agent — `Core/Agent/`
- `LanguageDetector.Detect(string text)`: Chinese if CJK chars present, Urdu if Arabic-script chars present, else English.
- `IntentParser.Parse(string request)`: returns `UserIntent` record `(string DesiredOutcome, IReadOnlyList<string> Applications, IReadOnlyList<string> Resources, IReadOnlyList<string> Risks, string CompletionCondition)` — keyword-based, real logic, no LLM needed.
- `Planner.Plan(AgentTask task)`: builds `TaskPlan` with 3-10 steps from intent keywords (notepad/write, folder/create, find pdf, csv->excel, duplicates, browser research, python project, diagnostics, organize, powerpoint, delete->Blocked step). A request containing "delete all" / "delete everything" must produce a plan whose first step is `RequiresApproval=true` and title contains "Refuse" or "Confirm scope" — see Security.
- `IAgentTool` interface: `string Name { get; } string Description { get; } Task<ToolResult> ExecuteAsync(IReadOnlyDictionary<string,string> args, CancellationToken ct);`
- `AgentEngine(OllamaClient? ollama, string model, IEnumerable<IAgentTool> tools, PermissionManager permissions, IProgress<AgentEvent>? progress = null, IApprovalService? approvalService = null, ApprovalSettings? approvalSettings = null, AuditLog? auditLog = null, Func<AgentTask, TaskPlan>? planProvider = null)`:
  `Task<AgentTask> RunAsync(AgentTask task, CancellationToken ct)` — plans, executes steps sequentially (max 50 steps, timeout per step 5 min), verifies each step, retries failed steps max 2 times, respects Pause/Resume/Cancel via `CancellationToken` + `Pause()`/`Resume()`, honours `AgentMode` (Ask=answer only via LLM or local summary, Plan=stop at WaitingApproval, Autonomous=execute), produces `FinalReport`. `public sealed record AgentEvent(string TaskId, string StepId, string Message, StepStatus Status);`
  Must never loop forever; approval is enforced in the dispatcher (this class, never the prompt) through the supplied `IApprovalService` before any step whose risk is `SensitiveFileModification` or above, whose `RequiresApproval` flag is set, or which is a destructive request. With no service it fails closed (denies). An approval is bound to one (TaskId, ActionId, ParameterHash) triple; any "approved" flag smuggled in plan data is ignored — only `IApprovalService` outcomes count. A step whose exact parameters change between request and execution is re-requested once. The same ActionId is never executed twice in one run (distinct steps sharing an id are blocked). **Retry semantics:** a retry re-executes the *same* step (up to 3 attempts total); tools must be idempotent or fail-safe on re-execution. Pause uses an async gate (no thread is blocked while paused); cancel-while-paused ends Cancelled. `AgentEngine` is `IDisposable` (releases the pause gate).
  Step arguments come from `PlanStep.Args` first: `AgentEngine.ResolveArgs(step)` merges legacy key=value hints out of the Description with Args winning on any key; `AgentEngine.ParseHints(description)` is the legacy scanner. `PermissionManager.RequestApprovalAsync` is the legacy low-risk path and is NOT what the engine uses.

## Tools — `Core/Tools/`
- `FilesystemTools : IAgentTool` (Name="filesystem"): args `action` = search|create-folder|organize|rename|copy|move|list|write-file; `path`, `pattern`, `destination`, `text`/`content`. Real System.IO. Also static helpers: `SearchFiles(string root, string pattern)`, `CreateFolder(string path)`, `OrganizeByExtension(string folder)`. No delete action is offered. Copy uses `overwrite: false`; move/rename throw when the destination exists; paths containing a NUL byte are rejected with a clear failure. There is no sandboxed base directory (the agent operates on the user's own machine at the user's direction) — see LIMITATIONS.md.
- `DuplicateFinder`: `static IReadOnlyList<DuplicateGroup> Find(string root, CancellationToken ct)` — group by size then SHA256; `record DuplicateGroup(string Hash, long SizeBytes, IReadOnlyList<string> Paths)`; never deletes.
- `DocumentTools`: `static string ReadPdfText(string path)` (PdfPig), `static string ReadCsvAsTable(string path)` (CsvHelper/manual parse), `static string CreateExcelFromCsv(string csvPath, string xlsxPath)` (ClosedXML, returns xlsxPath), `static string CreatePowerPoint(string title, IReadOnlyList<(string Title,string Body)> slides, string pptxPath)` (DocumentFormat.OpenXml), `static string CreateWordReport(string title, string body, string docxPath)` (OpenXml).
- `Diagnostics`: `static SystemReport Diagnose()` — CPU %, RAM used/total, disk free per drive, top processes by memory, network reachable (ping 8.8.8.8 with 2s timeout, failure is a fact not an error), `SlowPcFindings` list.
- `CodingTools : IAgentTool` (Name="coding"): `CreatePythonProject(string dir, string name)`, `RunTests(string projectDir)` — runs `python -m pytest -q` or `python -m unittest` if pytest missing, captures stdout/stderr + exit code, returns ToolResult with real output.
- `BrowserTools : IAgentTool` (Name="browser"): Playwright-based; only action is `research`. `ResearchAsync(IReadOnlyList<string> queries, string outputPath, CancellationToken ct)` — launches Chromium headless, navigates, extracts title + first paragraphs, writes sourced markdown report; on failure returns failure ToolResult honestly. Must NOT bypass CAPTCHA/auth — if a page shows CAPTCHA, record it as blocked and continue.
- `AppTools : IAgentTool` (Name="app"): args `action` = launch-app|launch|launch-notepad|close-app|close|focus|list|type-text|type|read-text|verify-text|save-as|save|notepad-write; `app`/`name`/`path`, `title`, `text`, `destination`. Backed by an `IAppControl`. Every action reports what really happened: a failed launch/type/read-back mismatch is a failure, never a success. Typing and UI Automation read-back are Windows-only in the real controls; on other operating systems those actions fail honestly.
- `NotepadWorkflow` (static, `Core/Tools/NotepadWorkflow.cs`): `RunAsync(control, text, savePath, ct, windowTimeout = 10s)` — full Notepad UI flow (launch → wait for window → focus → type → read back and compare exactly, optionally save). Windows-only: on non-Windows it returns a "Windows-only (NOT SUPPORTED)" failure and launches nothing. `SaveAsync(control, path, text, ct)` — tries the real Notepad Save dialog on Windows; when that cannot be driven it uses a VERIFIED FALLBACK (direct UTF-8 file write, labelled as such, only because the caller explicitly requested that save path) and always verifies the file content afterwards. `VerifyFileContent(path, expected)` (file exists + UTF-8 content equals input exactly), `WaitForWindowAsync(control, titleContains, timeout, ct)` (polls every 200 ms, honours cancellation).
- `IAppControl` interface (Core): `bool LaunchApp(string appNameOrPath)`, `bool CloseApp(string processName)`, `IReadOnlyList<string> RunningAppTitles()`, `bool FocusWindow(string titleContains)`, `bool TypeText(string text)` (Windows-only; throws `PlatformNotSupportedException` elsewhere), `string? ReadTextFromFocusedControl()` (default: throws `PlatformNotSupportedException`), `string? ReadWindowText(string titleContains)` (default: throws), `bool SaveNotepadFile(string path)` (default: throws). Core impl `ProcessAppControl` (Process.Start based, works on Linux for launch/list via Process.GetProcesses). Windows impl `WindowsAppControl` in App project using System.Windows.Automation (UI Automation) + user32 — file `src/AuroraOperator.App/Services/WindowsAppControl.cs`.

## Memory — `Core/Memory/MemoryStore.cs`
- `MemoryStore(string dbPath)` (Microsoft.Data.Sqlite): creates tables conversations, tasks, preferences, templates. Methods: `SaveConversation`, `SearchConversations(string query)`, `SaveTask`, `GetTask`, `ListRecentTasks(int n)`, `SetPreference/GetPreference`, `SaveTemplate/ListTemplates`, `DeleteConversation(id)`, `ClearConversations()`, `ExportJson(string path)`. Never store values whose key contains password/secret/token/key. Corrupt-row tolerance (Phase 5): a conversation row with an unparseable date still loads (date falls back to now); a task row with unknown enum strings loads as Unknown/Ask/Created; one bad row never fails a whole search, list, or export. A task left `Running` when the app was killed loads back as `Running` and is treated as interrupted — never silently resumed or re-executed.

## Security — `Core/Security/`
- `PermissionManager`: `bool IsGranted(PermissionCategory)`, `void Grant/Revoke`, `Task<bool> RequestApprovalAsync(ApprovalRequest req, Func<ApprovalRequest,Task<bool>>? uiPrompt, CancellationToken ct)` — if no UI prompt supplied, returns false (safe default). `bool IsDestructiveRequest(string text)` — true for "delete all/everything", "format", "rm -rf /", "wipe". `Task<ToolResult> GuardDeleteAllAsync(string request)` returns failure ToolResult explaining refusal + asking for specific scope. `static bool RequiresApproval(RiskLevel risk)` — true for `SensitiveFileModification` and above. `static bool IsRiskAutoAllowed(RiskLevel risk, bool autoApproveLowRisk = true)` — ReadOnly always, NormalReversible only when `autoApproveLowRisk` is true, Sensitive+ never. **A category grant NEVER auto-approves a Sensitive or higher risk action** — `RequestApprovalAsync` returns true from a grant only for low-risk requests. `Grant` is for explicit user settings only; no approval flow may call it as a side effect of one approval. `RequestApprovalAsync` is the legacy low-risk path; the engine enforces approval through `IApprovalService` (see Agent above), not through this class.
- `AuditLog(string logPath)`: `void Record(string taskId, string action, string details, bool approved)`, `IReadOnlyList<string> ReadRecent(int n)`.

## App lifetime — `App/App.xaml.cs`, `App/ViewModels/MainViewModel.cs`
- App creates one `HttpClient` (owned by App), one `OllamaClient`, one `ModelManager`, one `MemoryStore`, one `PermissionManager`, one `AuditLog`, one `WpfApprovalService` (`IApprovalService`, wired into the engine) and one `AgentEngine`; the engine runs on a background thread (`Task.Run` in `MainViewModel.SendAsync`), never on the UI thread. On exit App disposes the view model, engine, memory store, Ollama client and `HttpClient` (each guarded so one failure cannot block the rest). `MainViewModel` is `IDisposable`: it stops its `DispatcherTimer`, unsubscribes from `IApprovalService.PendingChanged`, and disposes the running task's `CancellationTokenSource` (also disposed when each task finishes). Global handling: `DispatcherUnhandledException` (log + message, handled), `AppDomain.CurrentDomain.UnhandledException` (logged), `TaskScheduler.UnobservedTaskException` (logged, observed).
- The live approval path is `WpfApprovalService` (modal `ApprovalDialog` via `Dispatcher.InvokeAsync`). The legacy `WpfApprovalPrompt` `Func<ApprovalRequest, Task<bool>>` adapter is not wired anywhere.

## Logging — `Core/LocalLogger.cs`
- `static class LocalLogger`: `Init(string logDir)`, `Info/Warn/Error(string message, Exception? ex=null)` writing timestamped lines to `aurora-operator.log` (rolling by date), thread-safe.

## Packages (Core.csproj) — use these exact versions if restore works, else nearest stable:
Microsoft.Data.Sqlite 8.0.8, ClosedXML 0.105.0, CsvHelper 33.0.1, UglyToad.PdfPig 0.1.10, DocumentFormat.OpenXml 3.0.2, Microsoft.Playwright 1.49.1, Microsoft.Extensions.Logging.Abstractions 8.0.2.
Tests: xunit 2.5.3 (already), Microsoft.NET.Test.Sdk 17.8.0 (already), Moq NOT needed — use a stub HttpMessageHandler.

Resolved versions actually in use (2026-11-21, see BUILD.md): Microsoft.Playwright **1.50.0**, DocumentFormat.OpenXml **3.1.1**, UglyToad.PdfPig **0.1.9-alpha001-patch1** (sub-dependencies resolved to 1.7.0-custom-5 approximate matches, NU1603 warnings only). The rest match the requested versions.

## Rules for every builder
- Real code only. No `TODO`, no `NotImplementedException` in user-facing paths, no fake success.
- No secrets in code. Ollama base URL is localhost only.
- Code must compile with `dotnet build`. The full solution (Core, App/WPF, Tests) builds on Linux with `-p:EnableWindowsTargeting=true` — that is a compile check only and does not prove the WPF app runs on Windows (see LIMITATIONS.md).
- Builds and tests are run one at a time by the verification pass (never concurrently — concurrent builds OOM this 7.7 GB VM) and their output is appended to `build.log` with a timestamped header, never overwriting its history. `dotnet test` does not run on this Linux VM (vstest testhost channel is redirected to the sandbox egress proxy — see TESTS.md); the in-process `AuroraOperator.TestRunner` runs the same xUnit tests there instead. On Windows, use `dotnet test`.
- Write files only inside `~/workspace/aurora-operator/` under your assigned area.
