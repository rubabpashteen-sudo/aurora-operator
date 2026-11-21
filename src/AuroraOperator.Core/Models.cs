using AuroraOperator.Core.Security;

namespace AuroraOperator.Core;

/// <summary>How the agent is allowed to act on a task.</summary>
public enum AgentMode { Ask, Plan, Autonomous }

/// <summary>Lifecycle state of an agent task.</summary>
public enum TaskState { Created, Planning, WaitingApproval, Running, Paused, Completed, Failed, Cancelled, Blocked }

/// <summary>Status of one plan step.</summary>
public enum StepStatus { Pending, Running, Succeeded, Failed, Skipped, NeedsApproval }

/// <summary>Granular permission categories. See Security/PermissionManager.</summary>
public enum PermissionCategory { FileAccess, BrowserAccess, ScreenObservation, KeyboardMouse, ApplicationLaunch, TerminalExecution, SystemSettings, NetworkAccess }

/// <summary>Language detected from the user's instruction.</summary>
public enum Language { English, Chinese, Urdu, Unknown }

/// <summary>Result of one tool execution. Every reported action has one.</summary>
public sealed record ToolResult(bool Success, string Summary, string? Details = null, string? OutputPath = null, IReadOnlyList<string>? Artifacts = null);

/// <summary>One executable step of a task plan.</summary>
public sealed record PlanStep(string Id, string Title, string ToolName, string Description, bool RequiresApproval = false, string? Precondition = null, string? CompletionCondition = null)
{
    public StepStatus Status { get; set; } = StepStatus.Pending;
    public string? ResultSummary { get; set; }
    internal Agent.WorkspaceTools.Tool? WorkspaceTool { get; init; }

    /// <summary>
    /// Structured machine-readable arguments for this step (action, path,
    /// destination, name, text, verify, …). This is where machine data
    /// lives; <see cref="Description"/> stays human-readable prose and is
    /// no longer scanned for parameters except as a legacy fallback for
    /// steps built before Args existed. Empty by default; the Planner
    /// populates it for every step it creates.
    /// </summary>
    public IReadOnlyDictionary<string, string> Args { get; init; } =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
}

/// <summary>A complete executable plan for one user request.</summary>
public sealed record TaskPlan(string TaskId, string UserRequest, Language Language, IReadOnlyList<PlanStep> Steps, string Summary);

/// <summary>One user task, tracked from creation to final report.</summary>
public sealed record AgentTask(string TaskId, string UserRequest, Language Language, AgentMode Mode)
{
    public TaskState State { get; set; } = TaskState.Created;
    public string? WorkspacePath { get; init; }
    public string? ConversationId { get; init; }
    public IReadOnlyList<Ollama.ChatMessage> ChatHistory { get; init; } = [];
    public TaskPlan? Plan { get; set; }
    public List<string> Log { get; } = new();
    public List<string> Artifacts { get; } = new();
    public string? FinalReport { get; set; }
}

/// <summary>
/// A request for the user to approve a sensitive action before it runs.
/// The 6-argument positional form is the historical shape and still works;
/// the extra fields are init-only properties so existing call sites and
/// `with` expressions keep compiling. An approval is bound to a single
/// (TaskId, ActionId, ParameterHash) triple: <see cref="ParameterHash"/>
/// is the SHA256 of <see cref="ExactParameters"/> (see
/// <see cref="ApprovalService"/>), and an approval for any other triple
/// must never authorize this request. <see cref="EffectiveParameterHash"/>
/// computes the hash when <see cref="ParameterHash"/> was not supplied.
/// </summary>
public sealed record ApprovalRequest(string Id, string TaskId, string Title, string Description, string ExactCommandOrChange, PermissionCategory Category)
{
    /// <summary>Id of the plan step this request is for (usually the step's Id).</summary>
    public string StepId { get; init; } = string.Empty;
    /// <summary>Stable action id the approval is bound to (usually the step's Id). Executing the same ActionId twice in one run is blocked.</summary>
    public string ActionId { get; init; } = string.Empty;
    /// <summary>Risk of the requested action, from <see cref="RiskClassifier"/>.</summary>
    public RiskLevel RiskLevel { get; init; } = RiskLevel.NormalReversible;
    /// <summary>Canonical exact parameters (sorted key=value pairs) the approval covers.</summary>
    public string ExactParameters { get; init; } = string.Empty;
    /// <summary>SHA256 (lower-case hex) of <see cref="ExactParameters"/>, or of <see cref="ExactCommandOrChange"/> when no canonical parameters were supplied. Empty until computed; use <see cref="EffectiveParameterHash"/>.</summary>
    public string ParameterHash { get; init; } = string.Empty;
    /// <summary>When the request was created (UTC).</summary>
    public DateTimeOffset RequestedAtUtc { get; init; } = DateTimeOffset.UtcNow;
    /// <summary>When the request stops being answerable (UTC). Null means the service default timeout applies.</summary>
    public DateTimeOffset? ExpiresAtUtc { get; init; }
    /// <summary>Current outcome; Pending until a decision, expiry or cancellation is recorded.</summary>
    public ApprovalOutcome Outcome { get; init; } = ApprovalOutcome.Pending;
    /// <summary>When the outcome was decided (UTC). Null while Pending.</summary>
    public DateTimeOffset? DecidedAtUtc { get; init; }

    /// <summary>The hash this approval is bound to: the supplied <see cref="ParameterHash"/> when present, otherwise the SHA256 of the canonical parameters.</summary>
    public string EffectiveParameterHash =>
        string.IsNullOrWhiteSpace(ParameterHash)
            ? ApprovalService.ComputeParameterHash(string.IsNullOrWhiteSpace(ExactParameters) ? ExactCommandOrChange : ExactParameters)
            : ParameterHash;
}
