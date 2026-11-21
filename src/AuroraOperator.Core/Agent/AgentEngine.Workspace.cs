using System.Net.Http;
using System.Runtime.CompilerServices;
using System.Text.Json;
using AuroraOperator.Core.Ollama;
using AuroraOperator.Core.Security;

namespace AuroraOperator.Core.Agent;

public sealed partial class AgentEngine
{
    private readonly ConditionalWeakTable<AgentTask, TaskPlan> _reviewablePlans = new();
    private const int MaxPlanningPasses = 3;

    public async Task<AgentTask> RunReviewedAsync(AgentTask task, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(task);
        if (!_reviewablePlans.TryGetValue(task, out var plan) || !_reviewablePlans.Remove(task))
            throw new InvalidOperationException("This plan is no longer available to run.");
        return await RunWorkspaceAsync(task, plan, ct).ConfigureAwait(false);
    }

    private async Task<AgentTask> RunWorkspaceAsync(AgentTask task, TaskPlan? reviewed, CancellationToken cancellation)
    {
        using var budget = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        budget.CancelAfter(TimeSpan.FromMinutes(8));
        var ct = budget.Token;
        var completed = new List<string>();
        var failed = new List<string>();
        var steps = new List<PlanStep>();
        var attempted = new HashSet<string>(StringComparer.Ordinal);
        var observations = new List<object>();

        try
        {
            if (string.IsNullOrWhiteSpace(task.WorkspacePath))
            {
                task.State = TaskState.Blocked;
                task.FinalReport = "Choose a workspace folder before using Plan or Agent.";
                return task;
            }
            if (_permissions.IsDestructiveRequest(task.UserRequest))
            {
                task.State = TaskState.Blocked;
                task.FinalReport = "This request includes a destructive action that Aurora cannot run.";
                return task;
            }

            string model = SessionFallbackModel ?? _model;
            string? fallbackNotice = null;
            var planner = new StructuredPlanner(task.WorkspacePath, async (messages, token) =>
            {
                if (_ollama is null)
                    throw new HttpRequestException("No local model connection.");
                try
                {
                    return await _ollama.ChatAsync(model, messages, token, structured: true).ConfigureAwait(false);
                }
                catch (ModelNotFoundException) when (model == _model)
                {
                    string? fallback = await ResolveFallbackModelAsync(_ollama, model, token).ConfigureAwait(false);
                    if (fallback is null) throw;
                    fallbackNotice = $"Using installed model {fallback} because {model} is unavailable.";
                    LastMissingModel = model;
                    SessionFallbackModel = model = fallback;
                    return await _ollama.ChatAsync(model, messages, token, structured: true).ConfigureAwait(false);
                }
            });
            var plan = new TaskPlan(task.TaskId, task.UserRequest, task.Language, steps.AsReadOnly(),
                $"Workspace: {task.WorkspacePath}");
            task.Plan = plan;
            bool requiresMorePlanning = false;
            for (int pass = 0; pass < MaxPlanningPasses; pass++)
            {
                ct.ThrowIfCancellationRequested();
                task.State = TaskState.Planning;
                var batch = reviewed is null
                    ? await planner.PlanAsync(task.UserRequest, observations, steps.Count, task.Mode != AgentMode.Plan, ct).ConfigureAwait(false)
                    : new StructuredPlanner.Batch(reviewed.Steps, false);
                requiresMorePlanning = false;
                if (batch is null || batch.Steps.Count == 0)
                {
                    failed.Add(completed.Count == 0
                        ? "No valid executable plan was available. Try a narrower request or a more capable local model. Offline commands: 'list workspace' and 'find workspace duplicates'."
                        : "Aurora could not validate a follow-up plan.");
                    break;
                }
                steps.AddRange(batch.Steps);
                if (task.Mode == AgentMode.Plan && reviewed is null)
                {
                    task.State = TaskState.WaitingApproval;
                    _reviewablePlans.Add(task, new TaskPlan(plan.TaskId, plan.UserRequest, plan.Language,
                        Array.AsReadOnly(steps.ToArray()), plan.Summary));
                    task.FinalReport = $"{fallbackNotice}\n{plan.Summary}\nReview these steps in the task panel, then choose Run plan. Actions that change files or open Notepad also require individual approval.";
                    return task;
                }

                task.State = TaskState.Running;
                bool stopped = false;
                bool recover = false;
                foreach (var step in batch.Steps)
                {
                    ct.ThrowIfCancellationRequested();
                    await WaitIfPausedAsync(task, ct).ConfigureAwait(false);
                    var tool = step.WorkspaceTool!;
                    var key = CurrentCanonicalParameters(step);
                    if (!attempted.Add(key))
                    {
                        step.Status = StepStatus.Skipped;
                        step.ResultSummary = "This exact action was already attempted.";
                        Report(task, step, step.ResultSummary, step.Status);
                        failed.Add($"{step.Title}: {step.ResultSummary}");
                        stopped = true;
                        break;
                    }
                    if (step.RequiresApproval || !PermissionManager.IsRiskAutoAllowed(tool.Risk, _approvalSettings.AutoApproveLowRisk))
                    {
                        step.Status = StepStatus.NeedsApproval;
                        Report(task, step, "Waiting for approval.", step.Status);
                        var decision = await RequestStepApprovalAsync(task, step, tool.Risk, ct).ConfigureAwait(false);
                        ct.ThrowIfCancellationRequested();
                        if (decision != ApprovalOutcome.Approved)
                        {
                            step.ResultSummary = $"Approval {decision.ToString().ToLowerInvariant()}.";
                            failed.Add($"{step.Title}: {step.ResultSummary}");
                            Report(task, step, step.ResultSummary, step.Status);
                            stopped = true;
                            break;
                        }
                    }

                    step.Status = StepStatus.Running;
                    Report(task, step, $"Running {step.Title}…", step.Status);
                    var result = await ExecuteWorkspaceToolAsync(tool, step.Args, ct).ConfigureAwait(false);
                    step.Status = result.Success ? StepStatus.Succeeded : StepStatus.Failed;
                    var details = result.Details is { Length: > 0 } value ? Limit(value, 1600) : null;
                    step.ResultSummary = details is null ? result.Summary : result.Summary + "\n" + details;
                    Report(task, step, step.ResultSummary, step.Status);
                    observations.Add(new { tool = tool.Name, args = step.Args.Where(a => a.Key != "text"),
                        result.Success, summary = Limit(result.Summary, 400), data = details });
                    if (result.Success)
                    {
                        completed.Add(result.Summary + (tool.Name is "list" or "duplicates" && details is not null ? "\n" + details : ""));
                        if (result.OutputPath is { } artifact) task.Artifacts.Add(artifact);
                        continue;
                    }

                    failed.Add($"{step.Title}: {result.Summary}");
                    recover = reviewed is null && tool.Risk == RiskLevel.ReadOnly;
                    stopped = !recover;
                    break;
                }
                foreach (var step in batch.Steps.Where(s => s.Status == StepStatus.Pending))
                {
                    step.Status = StepStatus.Skipped;
                    step.ResultSummary = "An earlier step stopped this batch.";
                    Report(task, step, step.ResultSummary, step.Status);
                }
                requiresMorePlanning = reviewed is null && !stopped && (batch.Continue || recover);
                if (!requiresMorePlanning || steps.Count >= StructuredPlanner.MaxCalls)
                    break;
            }
            if (requiresMorePlanning)
                failed.Add("The task reached its planning or tool budget before completion.");
            task.State = failed.Count == 0 ? TaskState.Completed : TaskState.Failed;
        }
        catch (OperationCanceledException)
        {
            task.State = cancellation.IsCancellationRequested ? TaskState.Cancelled : TaskState.Failed;
            failed.Add(cancellation.IsCancellationRequested ? "Task cancelled." : "Task time budget expired.");
        }
        catch (Exception ex)
        {
            task.State = TaskState.Failed;
            failed.Add($"Workspace task stopped ({ex.GetType().Name}). Check the selected folder and local model.");
        }

        var lines = new List<string> { $"Task {task.State.ToString().ToLowerInvariant()} in {task.WorkspacePath}." };
        lines.AddRange(completed.Select(message => "- " + message));
        lines.AddRange(failed.Select(message => "- " + message));
        task.FinalReport = string.Join("\n", lines);
        return task;
    }

    private static async Task<ToolResult> ExecuteWorkspaceToolAsync(WorkspaceTools.Tool tool,
        IReadOnlyDictionary<string, string> args, CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(tool.Timeout);
        try
        {
            ct.ThrowIfCancellationRequested();
            return await tool.Execute(args, timeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return new ToolResult(false, "Tool time limit exceeded. Check the workspace before retrying.");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or FormatException or ArgumentException
            or System.ComponentModel.Win32Exception)
        {
            return new ToolResult(false, ex.Message);
        }
    }

    private static string Limit(string value, int max) => value.Length <= max ? value : value[..max] + "…";
}
