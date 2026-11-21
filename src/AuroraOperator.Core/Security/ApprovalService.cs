using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using AuroraOperator.Core;
using AuroraOperator.Core.Agent;

namespace AuroraOperator.Core.Security;

/// <summary>How risky an action is. Drives whether approval is required.</summary>
public enum RiskLevel
{
    /// <summary>List / search / read / diagnose only; nothing is changed.</summary>
    ReadOnly = 0,
    /// <summary>Creates something new that can be undone (new folder, new file in an authorized directory).</summary>
    NormalReversible = 1,
    /// <summary>Overwrites, moves or renames an existing file/folder, or writes to an existing file.</summary>
    SensitiveFileModification = 2,
    /// <summary>Deletes something, wipes or formats.</summary>
    Destructive = 3,
    /// <summary>Needs elevated rights or changes system settings.</summary>
    Administrator = 4,
    /// <summary>Reaches outside the machine: browser post, upload, email, message.</summary>
    ExternalCommunication = 5,
}

/// <summary>Outcome of one approval request. Pending is the initial state only.</summary>
public enum ApprovalOutcome { Pending, Approved, Rejected, Expired, Cancelled }

/// <summary>Settings for runtime approval enforcement.</summary>
public sealed record ApprovalSettings(bool AutoApproveLowRisk = true, TimeSpan? ApprovalTimeout = null)
{
    /// <summary>How long an approval request stays answerable. Default 5 minutes.</summary>
    public TimeSpan EffectiveTimeout => ApprovalTimeout ?? TimeSpan.FromMinutes(5);
}

/// <summary>
/// Runtime approval service. An approval is bound to one
/// (TaskId, ActionId, ParameterHash) triple; an <see cref="ApprovalOutcome.Approved"/>
/// result only ever authorizes the exact request it was issued for.
/// Implementations must never approve a request they were not asked about,
/// and must never fail open: errors, timeouts and cancellations are denials.
/// </summary>
public interface IApprovalService
{
    /// <summary>
    /// Asks for approval of <paramref name="request"/> and returns the outcome.
    /// Throws <see cref="OperationCanceledException"/> when <paramref name="ct"/>
    /// is cancelled while waiting, so the caller can cancel the whole task
    /// instead of executing the pending action.
    /// </summary>
    Task<ApprovalOutcome> RequestApprovalAsync(ApprovalRequest request, CancellationToken ct);

    /// <summary>Cancels every pending request belonging to <paramref name="taskId"/> (outcome Cancelled).</summary>
    void CancelPending(string taskId);

    /// <summary>Resolves one pending request. Returns false when the request is not pending. Default: not supported.</summary>
    bool TryResolve(string requestId, ApprovalOutcome outcome) => false;

    /// <summary>Requests currently awaiting a decision. Default: none.</summary>
    IReadOnlyList<ApprovalRequest> PendingRequests => Array.Empty<ApprovalRequest>();

    /// <summary>Raised when the pending set changes. Default: never raised.</summary>
    event EventHandler? PendingChanged { add { } remove { } }
}

/// <summary>
/// Default in-memory approval service: a broker. Callers await
/// <see cref="RequestApprovalAsync"/>; whoever owns the decision (UI panel,
/// dialog, test) resolves it with <see cref="TryResolve"/> or cancels it
/// with <see cref="CancelPending"/>. Requests expire after the timeout
/// (default 5 minutes, or the request's own <see cref="ApprovalRequest.ExpiresAtUtc"/>)
/// and every request and decision is audit-logged. With no resolver, a
/// request simply expires — it is never auto-approved.
/// </summary>
public sealed class ApprovalService : IApprovalService
{
    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromMinutes(5);

    private sealed record PendingEntry(ApprovalRequest Request, TaskCompletionSource<ApprovalOutcome> Completion);

    private readonly AuditLog? _auditLog;
    private readonly TimeSpan _defaultTimeout;
    private readonly string _source;
    private readonly ConcurrentDictionary<string, PendingEntry> _pending = new();

    public ApprovalService(AuditLog? auditLog = null, TimeSpan? defaultTimeout = null, string source = "broker")
    {
        _auditLog = auditLog;
        _defaultTimeout = defaultTimeout ?? DefaultTimeout;
        _source = string.IsNullOrWhiteSpace(source) ? "broker" : source;
    }

    public event EventHandler? PendingChanged;

    public IReadOnlyList<ApprovalRequest> PendingRequests =>
        _pending.Values.Select(e => e.Request).OrderBy(r => r.RequestedAtUtc).ToList();

    /// <summary>Builds a fully-bound approval request for one plan step.</summary>
    public static ApprovalRequest CreateRequest(
        string taskId, PlanStep step, RiskLevel risk, string canonicalParameters,
        TimeSpan? timeout = null, PermissionCategory? category = null)
    {
        ArgumentNullException.ThrowIfNull(step);
        var now = DateTimeOffset.UtcNow;
        var canonical = string.IsNullOrWhiteSpace(canonicalParameters) ? step.Title + " — " + step.Description : canonicalParameters;
        // The approval dialog shows ExactCommandOrChange, so the exact
        // parameters must be visible there — not only in the hash. A
        // close-app approval, for example, must name the exact PID and
        // whether force termination is included, because approving
        // "close Notepad" is not the same as approving "close PID 1234,
        // force=true". The hash still binds the approval to these exact
        // parameters; this text is what the user actually reads.
        var exact = step.Title + " — " + step.Description;
        if (!string.Equals(canonical, exact, StringComparison.Ordinal))
        {
            exact += "\nParameters: " + canonical;
        }
        return new ApprovalRequest(
            Guid.NewGuid().ToString("N"), taskId, step.Title, step.Description,
            ExactCommandOrChange: exact,
            Category: category ?? MapCategory(step))
        {
            StepId = step.Id,
            ActionId = step.Id,
            RiskLevel = risk,
            ExactParameters = canonical,
            ParameterHash = ComputeParameterHash(canonical),
            RequestedAtUtc = now,
            ExpiresAtUtc = now + (timeout ?? DefaultTimeout),
        };
    }

    /// <summary>Canonical form of tool parameters: keys sorted case-insensitively, "key=value" joined with ';'. Two calls with the same parameters produce the same string regardless of order.</summary>
    public static string CanonicalParameters(string toolName, IReadOnlyDictionary<string, string>? args, string? description = null)
    {
        var sb = new StringBuilder();
        sb.Append("tool=").Append((toolName ?? string.Empty).Trim().ToLowerInvariant());
        if (args is not null)
        {
            foreach (var kv in args.OrderBy(kv => kv.Key, StringComparer.OrdinalIgnoreCase))
            {
                sb.Append(';').Append(kv.Key.Trim().ToLowerInvariant()).Append('=').Append(kv.Value ?? string.Empty);
            }
        }
        if (!string.IsNullOrWhiteSpace(description))
        {
            sb.Append(";description=").Append(description);
        }
        return sb.ToString();
    }

    /// <summary>SHA256 of the canonical parameters, lower-case hex.</summary>
    public static string ComputeParameterHash(string canonicalParameters)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(canonicalParameters ?? string.Empty));
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }

    public async Task<ApprovalOutcome> RequestApprovalAsync(ApprovalRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        var timeout = TimeoutFor(request);
        if (timeout <= TimeSpan.Zero)
        {
            Log(request, ApprovalOutcome.Expired);
            return ApprovalOutcome.Expired;
        }

        var tcs = new TaskCompletionSource<ApprovalOutcome>(TaskCreationOptions.RunContinuationsAsynchronously);
        var entry = new PendingEntry(request, tcs);
        if (!_pending.TryAdd(request.Id, entry))
        {
            return ApprovalOutcome.Rejected;
        }
        Log(request, ApprovalOutcome.Pending);
        PendingChanged?.Invoke(this, EventArgs.Empty);

        try
        {
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct);
            linked.CancelAfter(timeout);
            ApprovalOutcome outcome;
            try
            {
                outcome = await tcs.Task.WaitAsync(linked.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                if (ct.IsCancellationRequested)
                {
                    // The task was cancelled while waiting: propagate so the
                    // caller cancels the whole task and never runs the action.
                    Log(request, ApprovalOutcome.Cancelled);
                    throw;
                }
                outcome = ApprovalOutcome.Expired;
            }
            Log(request, outcome);
            return outcome;
        }
        finally
        {
            _pending.TryRemove(request.Id, out _);
            PendingChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    /// <summary>Resolves a pending request to a terminal outcome. Pending is not a valid decision. Returns false when the request is not pending or the outcome is not terminal.</summary>
    public bool TryResolve(string requestId, ApprovalOutcome outcome)
    {
        if (string.IsNullOrWhiteSpace(requestId) || outcome is ApprovalOutcome.Pending)
        {
            return false;
        }
        if (!_pending.TryGetValue(requestId, out var entry))
        {
            return false;
        }
        return entry.Completion.TrySetResult(outcome);
    }

    public void CancelPending(string taskId)
    {
        if (string.IsNullOrWhiteSpace(taskId))
        {
            return;
        }
        foreach (var entry in _pending.Values.Where(e => e.Request.TaskId == taskId).ToList())
        {
            entry.Completion.TrySetResult(ApprovalOutcome.Cancelled);
        }
    }

    private TimeSpan TimeoutFor(ApprovalRequest request)
    {
        if (request.ExpiresAtUtc is DateTimeOffset expires)
        {
            return expires - DateTimeOffset.UtcNow;
        }
        return _defaultTimeout;
    }

    private void Log(ApprovalRequest request, ApprovalOutcome outcome)
    {
        try
        {
            _auditLog?.RecordApproval(request, outcome, _source);
        }
        catch (Exception ex)
        {
            LocalLogger.Warn("Approval audit logging failed: " + ex.Message);
        }
    }

    private static PermissionCategory MapCategory(PlanStep step) =>
        (step.ToolName ?? string.Empty).ToLowerInvariant() switch
        {
            "browser" => PermissionCategory.BrowserAccess,
            "coding" => PermissionCategory.TerminalExecution,
            "terminal" => PermissionCategory.TerminalExecution,
            "document" => PermissionCategory.FileAccess,
            _ => PermissionCategory.FileAccess,
        };
}
