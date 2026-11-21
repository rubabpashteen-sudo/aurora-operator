using System.Text;
using AuroraOperator.Core.Ollama;
using AuroraOperator.Core.Security;
using AuroraOperator.Core.Tools;

namespace AuroraOperator.Core.Agent;

/// <summary>Progress event raised as the engine works through a task.</summary>
public sealed record AgentEvent(string TaskId, string StepId, string Message, StepStatus Status);

/// <summary>A tool the agent can call. Implemented by the Tools in Core/Tools.</summary>
public interface IAgentTool
{
    string Name { get; }
    string Description { get; }
    Task<ToolResult> ExecuteAsync(IReadOnlyDictionary<string, string> args, CancellationToken ct);
}

/// <summary>
/// Runs an AgentTask in Ask, Plan or Autonomous mode.
/// Ask never modifies the computer. Plan stops at WaitingApproval.
/// Autonomous executes steps sequentially (max 50 steps, 5 minutes per step,
/// max 2 retries per step), verifies each step, honours Pause/Resume and
/// cancellation, and enforces runtime approval in the dispatcher (this
/// class, never the prompt): before any step whose risk is
/// SensitiveFileModification or above, whose RequiresApproval flag is set,
/// or which is a destructive request, the engine awaits a decision from
/// the supplied <see cref="IApprovalService"/>. With no service it fails
/// closed (denies). An approval is bound to one (TaskId, ActionId,
/// ParameterHash) triple; any "approved" flag smuggled in plan data is
/// ignored — only <see cref="IApprovalService"/> outcomes count. A step
/// whose exact parameters change between request and execution is
/// re-requested. The same ActionId is never executed twice in one run.
/// <para>
/// RETRY SEMANTICS (Phase 5 audit): a retry re-executes the SAME step
/// (up to 3 attempts total: 1 + 2 retries). The ActionId dedup above
/// prevents a *different* step re-using an ActionId; it does not and
/// cannot prevent a retry of the step itself. Tools are therefore
/// expected to be idempotent or fail-safe on re-execution (write-file
/// rewrites identical content, create-folder is a no-op when the folder
/// exists, copy refuses to overwrite, organise moves only what is left).
/// A tool that is not safe to run twice must fail loudly on its second
/// call rather than silently repeating its side effect.
/// </para>
/// </summary>
public sealed class AgentEngine : IDisposable
{
    public const int MaxSteps = 50;
    public static readonly TimeSpan StepTimeout = TimeSpan.FromMinutes(5);
    public const int MaxRetriesPerStep = 2;

    private readonly OllamaClient? _ollama;
    private readonly string _model;
    private readonly IReadOnlyList<IAgentTool> _tools;
    private readonly PermissionManager _permissions;
    private readonly IProgress<AgentEvent>? _progress;
    private readonly IApprovalService? _approvalService;
    private readonly ApprovalSettings _approvalSettings;
    private readonly AuditLog? _auditLog;
    private readonly Func<AgentTask, TaskPlan>? _planProvider;
    private volatile bool _paused;
    private readonly object _pauseLock = new();
    // Async pause gate: a task that completes when Resume() is called.
    // It starts completed (not paused). Pause() swaps in a fresh
    // uncompleted gate; Resume() completes the current one. Awaiting the
    // gate never blocks a thread — see WaitIfPausedAsync.
    private TaskCompletionSource _resumeGate = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private bool _resumeGateCompleted = true;

    /// <summary>
    /// The first five parameters are the historical constructor. The rest
    /// are optional: <paramref name="approvalService"/> is the runtime
    /// approval service (null = fail closed for Sensitive+ steps),
    /// <paramref name="approvalSettings"/> tunes low-risk auto-approval
    /// and the approval timeout, <paramref name="auditLog"/> records
    /// approval decisions, and <paramref name="planProvider"/> overrides
    /// planning (test seam; null = <see cref="Planner"/>).
    /// </summary>
    public AgentEngine(
        OllamaClient? ollama,
        string model,
        IEnumerable<IAgentTool> tools,
        PermissionManager permissions,
        IProgress<AgentEvent>? progress = null,
        IApprovalService? approvalService = null,
        ApprovalSettings? approvalSettings = null,
        AuditLog? auditLog = null,
        Func<AgentTask, TaskPlan>? planProvider = null)
    {
        _ollama = ollama;
        _model = string.IsNullOrWhiteSpace(model) ? "qwen3:4b" : model;
        _tools = (tools ?? Enumerable.Empty<IAgentTool>()).ToList();
        _permissions = permissions ?? throw new ArgumentNullException(nameof(permissions));
        _progress = progress;
        _approvalService = approvalService;
        _approvalSettings = approvalSettings ?? new ApprovalSettings();
        _auditLog = auditLog;
        _planProvider = planProvider;
        // The pause gate starts completed (not paused); see _resumeGate.
        _resumeGate.TrySetResult();
    }

    public bool IsPaused => _paused;

    /// <summary>
    /// The async pause gate holds no OS handle, so there is nothing to
    /// release; Dispose is kept so App can shut the engine down through
    /// the same lifetime pattern as the other services. It deliberately
    /// does NOT resume a paused run: a paused task stays paused until
    /// Resume() or cancellation, never silently restarted by disposal.
    /// </summary>
    public void Dispose()
    {
    }

    /// <summary>Pauses the engine: the run blocks (asynchronously, without holding a thread) at the next WaitIfPausedAsync until Resume().</summary>
    public void Pause()
    {
        lock (_pauseLock)
        {
            _paused = true;
            if (_resumeGateCompleted)
            {
                _resumeGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                _resumeGateCompleted = false;
            }
        }
    }

    /// <summary>Resumes a paused engine: completes the current pause gate so the waiting run continues.</summary>
    public void Resume()
    {
        lock (_pauseLock)
        {
            _paused = false;
            _resumeGate.TrySetResult();
            _resumeGateCompleted = true;
        }
    }

    public async Task<AgentTask> RunAsync(AgentTask task, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(task);
        task.State = TaskState.Planning;
        task.Log.Add($"[{DateTimeOffset.Now:O}] Task started in {task.Mode} mode.");
        // Phase 8 (F13): never log the full user request — it may contain
        // a pasted secret, and the redactor only catches patterned forms.
        // Mode, language and length are enough for operations.
        LocalLogger.Info($"Task {task.TaskId} started in {task.Mode} mode ({task.Language}, request {task.UserRequest.Length} characters).");

        try
        {
            var plan = _planProvider is not null ? _planProvider(task) : Planner.Plan(task);
            task.Plan = plan;
            task.Log.Add($"Plan built: {plan.Steps.Count} steps. {plan.Summary}");

            if (task.Mode == AgentMode.Ask)
            {
                task.State = TaskState.Completed;
                task.FinalReport = await AnswerAsync(task, plan, ct).ConfigureAwait(false);
                task.Log.Add("Ask mode: answered without modifying the computer.");
                return task;
            }

            if (task.Mode == AgentMode.Plan)
            {
                task.State = TaskState.WaitingApproval;
                task.FinalReport = BuildPlanReport(task, plan);
                task.Log.Add("Plan mode: stopped at WaitingApproval, nothing executed.");
                return task;
            }

            // Autonomous
            task.State = TaskState.Running;
            var accomplished = new List<string>();
            var files = new List<string>();
            var failed = new List<string>();
            var approvals = new List<string>();

            var steps = plan.Steps.Take(MaxSteps).ToList();
            if (plan.Steps.Count > MaxSteps)
                task.Log.Add($"Plan had {plan.Steps.Count} steps; truncated to {MaxSteps}.");

            // ActionIds executed in this run. Executing the same ActionId
            // twice is blocked and logged, even if it was approved before.
            var executedActionIds = new HashSet<string>(StringComparer.Ordinal);

            foreach (var step in steps)
            {
                ct.ThrowIfCancellationRequested();
                await WaitIfPausedAsync(task, ct).ConfigureAwait(false);

                step.Status = StepStatus.Running;
                Report(task, step, $"Starting: {step.Title}", StepStatus.Running);

                // Duplicate ActionId: never execute twice in one run.
                if (!string.IsNullOrWhiteSpace(step.Id) && executedActionIds.Contains(step.Id))
                {
                    step.Status = StepStatus.Failed;
                    step.ResultSummary = $"Blocked: ActionId '{step.Id}' was already executed in this run; duplicate execution is not allowed.";
                    failed.Add($"{step.Title}: {step.ResultSummary}");
                    task.Log.Add($"Step {step.Id} blocked as a duplicate ActionId.");
                    Report(task, step, step.ResultSummary, StepStatus.Failed);
                    continue;
                }

                // Runtime approval gate (dispatcher enforcement). The risk
                // is computed from what the step does, never from any
                // "approved" flag in plan data — such flags are ignored.
                var risk = RiskClassifier.Classify(step);
                var destructive = _permissions.IsDestructiveRequest(step.Title + " " + step.Description);
                if (destructive && risk < RiskLevel.Destructive)
                    risk = RiskLevel.Destructive;
                var needsApproval = step.RequiresApproval || destructive ||
                    !PermissionManager.IsRiskAutoAllowed(risk, _approvalSettings.AutoApproveLowRisk);

                if (needsApproval)
                {
                    ApprovalOutcome outcome;
                    try
                    {
                        outcome = await RequestStepApprovalAsync(task, step, risk, ct).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException)
                    {
                        // Task cancelled while awaiting approval: propagate so
                        // the task ends Cancelled and the action never runs.
                        throw;
                    }

                    // A service that swallows cancellation must still not
                    // let a cancelled task proceed to execution.
                    ct.ThrowIfCancellationRequested();

                    if (outcome != ApprovalOutcome.Approved)
                    {
                        step.Status = StepStatus.NeedsApproval;
                        step.ResultSummary = outcome switch
                        {
                            ApprovalOutcome.Rejected => "Approval denied — step was not executed.",
                            ApprovalOutcome.Expired => "Approval request expired — step was not executed.",
                            ApprovalOutcome.Cancelled => "Approval cancelled — step was not executed.",
                            _ => "Not approved — step was not executed.",
                        };
                        approvals.Add($"{step.Title}: approval {outcome.ToString().ToLowerInvariant()}, not granted; step not executed.");
                        task.Log.Add($"Step {step.Id} needs approval ({outcome}): {step.Title}");
                        Report(task, step, "Needs approval — not executed.", StepStatus.NeedsApproval);
                        continue;
                    }
                }

                executedActionIds.Add(step.Id);
                var result = await ExecuteStepWithRetriesAsync(task, step, ct).ConfigureAwait(false);

                if (result is null)
                {
                    step.Status = StepStatus.Skipped;
                    step.ResultSummary = "Skipped (cancelled or paused shutdown).";
                    continue;
                }

                if (result.Success && VerifyStep(step, result))
                {
                    step.Status = StepStatus.Succeeded;
                    step.ResultSummary = result.Summary;
                    accomplished.Add($"{step.Title}: {result.Summary}");
                    if (result.OutputPath is not null && !files.Contains(result.OutputPath))
                        files.Add(result.OutputPath);
                    if (result.Artifacts is not null)
                        foreach (var a in result.Artifacts)
                            if (!files.Contains(a)) files.Add(a);
                    task.Log.Add($"Step {step.Id} succeeded: {result.Summary}");
                    Report(task, step, result.Summary, StepStatus.Succeeded);
                }
                else
                {
                    step.Status = StepStatus.Failed;
                    step.ResultSummary = result.Success
                        ? "Step reported success but verification failed: " + result.Summary
                        : result.Summary;
                    failed.Add($"{step.Title}: {step.ResultSummary}");
                    task.Log.Add($"Step {step.Id} failed: {step.ResultSummary}");
                    Report(task, step, step.ResultSummary, StepStatus.Failed);
                }
            }

            foreach (var f in files)
                if (!task.Artifacts.Contains(f)) task.Artifacts.Add(f);

            task.FinalReport = BuildFinalReport(task, accomplished, files, failed, approvals);
            task.State = failed.Count == 0 && approvals.Count == 0
                ? TaskState.Completed
                : approvals.Count > 0 && failed.Count == 0
                    ? TaskState.WaitingApproval
                    : TaskState.Failed;
            task.Log.Add($"Task finished with state {task.State}.");
            return task;
        }
        catch (OperationCanceledException)
        {
            task.State = TaskState.Cancelled;
            task.FinalReport = BuildFinalReport(task,
                new List<string>(), task.Artifacts,
                new List<string> { "Task was cancelled." }, new List<string>());
            task.Log.Add("Task cancelled.");
            LocalLogger.Info($"Task {task.TaskId} cancelled.");
            return task;
        }
        catch (Exception ex)
        {
            task.State = TaskState.Failed;
            task.FinalReport = BuildFinalReport(task,
                new List<string>(), task.Artifacts,
                new List<string> { "Engine error: " + ex.Message }, new List<string>());
            task.Log.Add("Engine error: " + ex.Message);
            LocalLogger.Error($"Task {task.TaskId} failed.", ex);
            return task;
        }
    }

    /// <summary>
    /// Waits (asynchronously) while paused. This never blocks a thread:
    /// it awaits the pause gate's task, which <see cref="Resume"/>
    /// completes, or <paramref name="ct"/>, which throws and ends the
    /// task Cancelled. A pause combined with a cancellation therefore
    /// always terminates; it never busy-waits and never deadlocks a
    /// caller that awaits <see cref="RunAsync"/> directly (the previous
    /// implementation blocked the calling thread on a
    /// <see cref="ManualResetEventSlim"/>, so a direct caller could never
    /// reach the code that resumes or cancels the run).
    /// </summary>
    private async Task WaitIfPausedAsync(AgentTask task, CancellationToken ct)
    {
        while (_paused)
        {
            task.State = TaskState.Paused;
            Report(task, new PlanStep("pause", "Paused", "engine", "Task paused by the user."),
                "Paused — waiting for Resume().", StepStatus.Pending);
            Task gate;
            lock (_pauseLock)
            {
                gate = _resumeGate.Task;
            }
            try
            {
                await gate.WaitAsync(ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                ct.ThrowIfCancellationRequested();
                throw;
            }
            task.State = TaskState.Running;
        }
    }

    /// <summary>
    /// Requests a per-request approval for one step from the approval
    /// service. Fail closed: with no service the outcome is Rejected and
    /// nothing runs. The request is bound to (TaskId, ActionId,
    /// ParameterHash); if the step's exact parameters change between the
    /// request and execution the approval is re-requested once with the new
    /// parameters. Throws <see cref="OperationCanceledException"/> when the
    /// task token is cancelled while waiting.
    /// </summary>
    private async Task<ApprovalOutcome> RequestStepApprovalAsync(AgentTask task, PlanStep step, RiskLevel risk, CancellationToken ct)
    {
        if (_approvalService is null)
        {
            task.Log.Add($"Step {step.Id} needs approval ({risk}) but no approval service is configured — fail closed, step not executed.");
            LocalLogger.Warn($"Task {task.TaskId} step {step.Id}: no approval service; denied {risk} step.");
            return ApprovalOutcome.Rejected;
        }

        var (outcome, approvedRequest) = await RequestApprovalOnceAsync(task, step, risk, ct).ConfigureAwait(false);

        if (outcome == ApprovalOutcome.Approved)
        {
            // Re-check the exact parameters just before execution: if they
            // changed after the approval was granted, that approval does
            // not cover the new parameters — ask again, once.
            var current = CurrentCanonicalParameters(step);
            if (!string.Equals(ApprovalService.ComputeParameterHash(current), approvedRequest.EffectiveParameterHash, StringComparison.Ordinal))
            {
                task.Log.Add($"Step {step.Id} parameters changed after approval — re-requesting approval for the new parameters.");
                (outcome, _) = await RequestApprovalOnceAsync(task, step, risk, ct).ConfigureAwait(false);
            }
        }

        return outcome;
    }

    private async Task<(ApprovalOutcome Outcome, ApprovalRequest Request)> RequestApprovalOnceAsync(AgentTask task, PlanStep step, RiskLevel risk, CancellationToken ct)
    {
        var canonical = CurrentCanonicalParameters(step);
        var request = ApprovalService.CreateRequest(task.TaskId, step, risk, canonical, _approvalSettings.EffectiveTimeout);
        try
        {
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeoutCts.CancelAfter(_approvalSettings.EffectiveTimeout);
            ApprovalOutcome outcome;
            try
            {
                outcome = await _approvalService!.RequestApprovalAsync(request, timeoutCts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (OperationCanceledException)
            {
                // The engine-side approval timeout fired (or the service
                // timed out): the request expired, the step does not run.
                outcome = ApprovalOutcome.Expired;
            }

            _auditLog?.RecordApproval(request with { Outcome = outcome, DecidedAtUtc = DateTimeOffset.UtcNow }, outcome, "engine");
            task.Log.Add($"Approval {outcome} for step {step.Id} ({risk}, action {step.Id}, hash {request.EffectiveParameterHash[..8]}…).");
            return (outcome, request);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            // An approval service failure is a denial, never an approval.
            task.Log.Add($"Approval request failed: {ex.Message}");
            _auditLog?.RecordApproval(request, ApprovalOutcome.Rejected, "engine-error");
            return (ApprovalOutcome.Rejected, request);
        }
    }

    private static string CurrentCanonicalParameters(PlanStep step) =>
        ApprovalService.CanonicalParameters(step.ToolName, ResolveArgs(step), step.Description);

    /// <summary>
    /// The step's effective arguments: structured <see cref="PlanStep.Args"/>
    /// first (Args win on any key), with the legacy key=value hints scanned
    /// out of the Description only filling keys Args does not carry. This is
    /// the single place the hint fallback lives; every consumer below uses
    /// this method, so no execution path depends on prose scanning alone.
    /// </summary>
    public static IReadOnlyDictionary<string, string> ResolveArgs(PlanStep step)
    {
        ArgumentNullException.ThrowIfNull(step);
        var merged = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var kv in ParseHints(step.Description)) merged[kv.Key] = kv.Value;
        if (step.Args is not null)
            foreach (var kv in step.Args) merged[kv.Key] = kv.Value;
        return merged;
    }

    private async Task<ToolResult?> ExecuteStepWithRetriesAsync(AgentTask task, PlanStep step, CancellationToken ct)
    {
        ToolResult? last = null;
        for (var attempt = 0; attempt <= MaxRetriesPerStep; attempt++)
        {
            ct.ThrowIfCancellationRequested();
            await WaitIfPausedAsync(task, ct).ConfigureAwait(false);
            try
            {
                using var stepCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                stepCts.CancelAfter(StepTimeout);
                last = await ExecuteStepAsync(task, step, stepCts.Token).ConfigureAwait(false);
                if (last.Success && VerifyStep(step, last))
                    return last;
                if (attempt < MaxRetriesPerStep)
                {
                    task.Log.Add($"Step {step.Id} attempt {attempt + 1} failed ({last.Summary}); retrying.");
                    await Task.Delay(TimeSpan.FromMilliseconds(500 * (attempt + 1)), ct).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                last = new ToolResult(false, $"Step timed out after {StepTimeout.TotalMinutes} minutes: {step.Title}");
                task.Log.Add($"Step {step.Id} timed out.");
                if (attempt < MaxRetriesPerStep)
                    await Task.Delay(TimeSpan.FromMilliseconds(500 * (attempt + 1)), ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                last = new ToolResult(false, $"Step error: {ex.Message}", ex.ToString());
                task.Log.Add($"Step {step.Id} error: {ex.Message}");
                if (attempt < MaxRetriesPerStep)
                    await Task.Delay(TimeSpan.FromMilliseconds(500 * (attempt + 1)), ct).ConfigureAwait(false);
            }
        }
        return last ?? new ToolResult(false, "Step produced no result: " + step.Title);
    }

    private async Task<ToolResult> ExecuteStepAsync(AgentTask task, PlanStep step, CancellationToken ct)
    {
        // Structured Args first; ParseHints only fills legacy gaps (see ResolveArgs).
        var hints = ResolveArgs(step);
        var toolName = (step.ToolName ?? string.Empty).ToLowerInvariant();

        switch (toolName)
        {
            case "diagnostics":
            {
                var report = Diagnostics.Diagnose();
                var sb = new StringBuilder();
                sb.AppendLine($"CPU {report.CpuPercent:F1}%, RAM {report.RamUsedBytes / 1024 / 1024} / {report.RamTotalBytes / 1024 / 1024} MB used, network reachable: {report.NetworkReachable}.");
                foreach (var disk in report.Disks)
                    sb.AppendLine($"Drive {disk.Name}: {disk.FreeBytes / 1024 / 1024} MB free of {disk.TotalBytes / 1024 / 1024} MB.");
                sb.AppendLine("Top processes by memory:");
                foreach (var p in report.TopProcesses)
                    sb.AppendLine($"- {p.Name} (PID {p.ProcessId}): {p.WorkingSetBytes / 1024 / 1024} MB");
                if (report.SlowPcFindings.Count > 0)
                {
                    sb.AppendLine("Slow-PC findings:");
                    foreach (var f in report.SlowPcFindings) sb.AppendLine("- " + f);
                }
                else
                {
                    sb.AppendLine("No slow-PC findings.");
                }
                return new ToolResult(true, "Diagnostics completed.", sb.ToString());
            }
            case "duplicates":
            {
                var root = GetHint(hints, "path") ?? ".";
                var groups = DuplicateFinder.Find(root, ct);
                var sb = new StringBuilder();
                sb.AppendLine($"Found {groups.Count} duplicate group(s) under {root}. Nothing was deleted.");
                foreach (var g in groups)
                {
                    sb.AppendLine($"- {g.SizeBytes} bytes, SHA256 {g.Hash}:");
                    foreach (var p in g.Paths) sb.AppendLine("  " + p);
                }
                return new ToolResult(true, sb.ToString().TrimEnd(),
                    Details: sb.ToString(),
                    Artifacts: groups.SelectMany(g => g.Paths).ToList());
            }
            case "document":
            {
                return ExecuteDocumentStep(task, step, hints, ct);
            }
            case "verify":
                return new ToolResult(true, "Verified: " + step.CompletionCondition);
            default:
            {
                var tool = _tools.FirstOrDefault(t =>
                    string.Equals(t.Name, step.ToolName, StringComparison.OrdinalIgnoreCase));
                if (tool is null)
                    return new ToolResult(false,
                        $"No tool named '{step.ToolName}' is registered, so step '{step.Title}' could not run.");
                var args = new Dictionary<string, string>(hints, StringComparer.OrdinalIgnoreCase);
                if (!args.ContainsKey("path") && IntentParser.FirstPathOrDefault(task.UserRequest) is string p)
                    args["path"] = p;
                return await tool.ExecuteAsync(args, ct).ConfigureAwait(false);
            }
        }
    }

    private ToolResult ExecuteDocumentStep(AgentTask task, PlanStep step,
        IReadOnlyDictionary<string, string> hints, CancellationToken ct)
    {
        var action = (GetHint(hints, "action") ?? string.Empty).ToLowerInvariant();
        var path = GetHint(hints, "path") ?? IntentParser.FirstPathOrDefault(task.UserRequest) ?? string.Empty;
        var destination = GetHint(hints, "destination") ?? string.Empty;

        switch (action)
        {
            case "read-pdf":
            {
                if (!File.Exists(path))
                    return new ToolResult(false, $"PDF not found: {path}");
                var text = DocumentTools.ReadPdfText(path);
                return new ToolResult(true, $"Read {text.Length} characters from {path}.", text);
            }
            case "summarise-pdf":
            case "summarize-pdf":
            {
                if (!File.Exists(path))
                    return new ToolResult(false, $"PDF not found: {path}");
                var text = DocumentTools.ReadPdfText(path);
                var summary = SummariseLocally(text);
                return new ToolResult(true, "PDF summarised.", summary);
            }
            case "csv-to-excel":
            {
                if (!File.Exists(path))
                    return new ToolResult(false, $"CSV not found: {path}");
                if (string.IsNullOrWhiteSpace(destination))
                    destination = Path.ChangeExtension(path, ".xlsx")!;
                var made = DocumentTools.CreateExcelFromCsv(path, destination);
                return new ToolResult(true, $"Excel workbook created: {made}", OutputPath: made,
                    Artifacts: new[] { made });
            }
            case "create-powerpoint":
            {
                var slides = BuildSlidesFromRequest(task.UserRequest);
                var made = DocumentTools.CreatePowerPoint(
                    IntentParser.Parse(task.UserRequest).DesiredOutcome, slides, path);
                return new ToolResult(true, $"Presentation created: {made}", OutputPath: made,
                    Artifacts: new[] { made });
            }
            case "create-word":
            {
                var made = DocumentTools.CreateWordReport(
                    IntentParser.Parse(task.UserRequest).DesiredOutcome, task.UserRequest, path);
                return new ToolResult(true, $"Word report created: {made}", OutputPath: made,
                    Artifacts: new[] { made });
            }
            default:
                return new ToolResult(false, $"Unknown document action '{action}' for step '{step.Title}'.");
        }
    }

    /// <summary>Verifies a step result: success plus any file/folder the step promised (from Args first, hints as legacy fallback).</summary>
    private static bool VerifyStep(PlanStep step, ToolResult result)
    {
        if (result is null || !result.Success) return false;
        var hints = ResolveArgs(step);
        if (hints.TryGetValue("verify", out var verify))
        {
            if (verify.StartsWith("file:", StringComparison.OrdinalIgnoreCase))
                return File.Exists(verify["file:".Length..]);
            if (verify.StartsWith("dir:", StringComparison.OrdinalIgnoreCase))
                return Directory.Exists(verify["dir:".Length..]);
            // "text:…" verification is done inside the tool itself (read-back
            // or file-content comparison); a successful tool result is the
            // verification for this step.
            if (verify.StartsWith("text:", StringComparison.OrdinalIgnoreCase))
                return true;
        }
        if (result.OutputPath is not null &&
            hints.TryGetValue("action", out var action) &&
            (action is "create-folder" or "write-file" or "csv-to-excel" or "create-powerpoint" or "create-word"))
            return File.Exists(result.OutputPath) || Directory.Exists(result.OutputPath);
        return true;
    }

    private async Task<string> AnswerAsync(AgentTask task, TaskPlan plan, CancellationToken ct)
    {
        var intent = IntentParser.Parse(task.UserRequest);
        var local = new StringBuilder();
        local.AppendLine("Answer (no changes were made to the computer):");
        local.AppendLine();
        local.AppendLine("What you asked for: " + intent.DesiredOutcome);
        if (intent.Applications.Count > 0)
            local.AppendLine("Likely applications: " + string.Join(", ", intent.Applications));
        if (intent.Resources.Count > 0)
            local.AppendLine("Files/folders mentioned: " + string.Join(", ", intent.Resources));
        if (intent.Risks.Count > 0)
        {
            local.AppendLine("Things to be careful about:");
            foreach (var r in intent.Risks) local.AppendLine("- " + r);
        }
        local.AppendLine("Completion condition: " + intent.CompletionCondition);
        local.AppendLine();
        local.AppendLine("What I would do (run in Autonomous mode to do it):");
        foreach (var step in plan.Steps)
            local.AppendLine($"- {step.Title}: {step.Description}");

        if (_ollama is null) return local.ToString().TrimEnd();

        try
        {
            if (!await _ollama.IsAvailableAsync(ct).ConfigureAwait(false))
            {
                local.AppendLine();
                local.AppendLine("Note: Ollama was not reachable, so this answer is the local summary above.");
                return local.ToString().TrimEnd();
            }
            var messages = new[]
            {
                new ChatMessage("system",
                    "You are Aurora Operator, a helpful Windows desktop assistant. Answer concisely and accurately. Do not claim to have done anything on the computer."),
                new ChatMessage("user", task.UserRequest),
            };
            var answer = await _ollama.ChatAsync(_model, messages, ct).ConfigureAwait(false);
            return string.IsNullOrWhiteSpace(answer) ? local.ToString().TrimEnd() : answer.Trim();
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            LocalLogger.Warn("Ollama answer failed; falling back to local summary: " + ex.Message);
            local.AppendLine();
            local.AppendLine("Note: Ollama could not answer (" + ex.Message + "), so this is the local summary.");
            return local.ToString().TrimEnd();
        }
    }

    private static string BuildPlanReport(AgentTask task, TaskPlan plan)
    {
        var sb = new StringBuilder();
        sb.AppendLine("Plan (nothing has been executed yet):");
        sb.AppendLine(plan.Summary);
        foreach (var step in plan.Steps)
        {
            sb.AppendLine($"- [{step.Id}] {step.Title} (tool: {step.ToolName})" +
                          (step.RequiresApproval ? " — REQUIRES APPROVAL" : ""));
            sb.AppendLine($"  {step.Description}");
            if (step.Precondition is not null) sb.AppendLine($"  Precondition: {step.Precondition}");
            if (step.CompletionCondition is not null) sb.AppendLine($"  Done when: {step.CompletionCondition}");
        }
        sb.AppendLine("Approve this plan to run it in Autonomous mode.");
        return sb.ToString().TrimEnd();
    }

    private static string BuildFinalReport(AgentTask task,
        IReadOnlyList<string> accomplished, IReadOnlyList<string> files,
        IReadOnlyList<string> failed, IReadOnlyList<string> approvals)
    {
        var sb = new StringBuilder();
        sb.AppendLine("Aurora Operator — Final Report");
        sb.AppendLine($"Task: {task.UserRequest}");
        sb.AppendLine($"Mode: {task.Mode}   State: {task.State}");
        sb.AppendLine();
        sb.AppendLine("Accomplished:");
        sb.AppendLine(accomplished.Count == 0 ? "- (none)" : string.Join("\n", accomplished.Select(a => "- " + a)));
        sb.AppendLine();
        sb.AppendLine("Files created / modified:");
        sb.AppendLine(files.Count == 0 ? "- (none)" : string.Join("\n", files.Select(f => "- " + f)));
        sb.AppendLine();
        sb.AppendLine("Failed:");
        sb.AppendLine(failed.Count == 0 ? "- (none)" : string.Join("\n", failed.Select(f => "- " + f)));
        sb.AppendLine();
        sb.AppendLine("Approvals required:");
        sb.AppendLine(approvals.Count == 0 ? "- (none)" : string.Join("\n", approvals.Select(a => "- " + a)));
        return sb.ToString().TrimEnd();
    }

    private static string SummariseLocally(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return "(The document contained no extractable text.)";
        var sentences = text
            .Replace("\r", " ").Replace("\n", " ")
            .Split(new[] { ". " }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var take = sentences.Take(5);
        var summary = string.Join(". ", take);
        if (!summary.EndsWith('.')) summary += ".";
        return summary + $"\n\n(Source document: {text.Length} characters; first sentences shown.)";
    }

    private static IReadOnlyList<(string Title, string Body)> BuildSlidesFromRequest(string request)
    {
        var intent = IntentParser.Parse(request);
        var slides = new List<(string Title, string Body)>
        {
            ("Overview", intent.DesiredOutcome),
        };
        var parts = request.Split(new[] { ',', ';', '\n' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        foreach (var part in parts.Take(4))
        {
            if (part.Length > 3 && !slides.Any(s => s.Body.Contains(part, StringComparison.OrdinalIgnoreCase)))
                slides.Add((part.Length > 40 ? part[..40] : part, part));
        }
        slides.Add(("Summary", intent.CompletionCondition));
        return slides;
    }

    /// <summary>Parses the machine-readable key=value hints embedded in a step description.</summary>
    public static IReadOnlyDictionary<string, string> ParseHints(string? description)
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrWhiteSpace(description)) return map;
        // Hints are key=value pairs, but the human-readable sentence comes
        // first in the same segment ("Create the folder. action=create-folder;
        // path=..."), so a hint key can appear mid-segment. Scan for each
        // known key followed by '=' and take its value up to the next ';'.
        string[] keys = { "action", "path", "pattern", "destination", "verify", "name", "text" };
        foreach (var key in keys)
        {
            var pattern = @"\b" + key + @"\s*=\s*";
            var match = System.Text.RegularExpressions.Regex.Match(description, pattern, System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            if (!match.Success) continue;
            var start = match.Index + match.Length;
            var end = description.IndexOf(';', start);
            var value = (end < 0 ? description[start..] : description[start..end]).Trim();
            if (value.Length > 0) map[key] = value;
        }
        return map;
    }

    private static string? GetHint(IReadOnlyDictionary<string, string> hints, string key) =>
        hints.TryGetValue(key, out var v) ? v : null;

    private void Report(AgentTask task, PlanStep step, string message, StepStatus status)
    {
        try { _progress?.Report(new AgentEvent(task.TaskId, step.Id, message, status)); }
        catch (Exception ex) { LocalLogger.Warn("Progress reporting failed: " + ex.Message); }
    }
}
