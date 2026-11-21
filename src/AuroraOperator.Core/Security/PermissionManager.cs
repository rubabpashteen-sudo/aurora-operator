using AuroraOperator.Core;

namespace AuroraOperator.Core.Security;

/// <summary>
/// Granular permission grants for the agent. Safe defaults: read-only-ish
/// capabilities (file access, browser access, launching applications) are
/// granted; anything that can change the system or reach the network
/// (terminal execution, system settings, network access, screen observation,
/// keyboard/mouse input) is NOT granted until the user explicitly grants it.
/// <para>
/// IMPORTANT (Phase 1): a category grant NEVER auto-approves a Sensitive or
/// higher risk action. <see cref="RequestApprovalAsync"/> returns true from
/// a grant only for low-risk requests (ReadOnly / NormalReversible).
/// SensitiveFileModification, Destructive, Administrator and
/// ExternalCommunication always need a per-request decision through an
/// <see cref="IApprovalService"/> (or the supplied UI prompt). Use
/// <see cref="RequiresApproval"/> / <see cref="IsRiskAutoAllowed"/> to
/// decide. <see cref="Grant"/> is for explicit user settings only — no
/// approval flow may call it to grant a whole category after one approval.
/// </para>
/// </summary>
public sealed class PermissionManager
{
    private static readonly PermissionCategory[] DefaultGranted =
    {
        PermissionCategory.FileAccess,
        PermissionCategory.BrowserAccess,
        PermissionCategory.ApplicationLaunch,
    };

    private readonly HashSet<PermissionCategory> _granted = new(DefaultGranted);
    private readonly object _gate = new();

    public bool IsGranted(PermissionCategory category)
    {
        lock (_gate)
        {
            return _granted.Contains(category);
        }
    }

    /// <summary>
    /// True when this risk level always needs a per-request approval,
    /// regardless of any category grant: SensitiveFileModification and above.
    /// </summary>
    public static bool RequiresApproval(RiskLevel risk) => risk >= RiskLevel.SensitiveFileModification;

    /// <summary>
    /// True when this risk level may run without a per-request approval:
    /// ReadOnly always; NormalReversible only when
    /// <paramref name="autoApproveLowRisk"/> is true. Sensitive and above
    /// are never auto-allowed.
    /// </summary>
    public static bool IsRiskAutoAllowed(RiskLevel risk, bool autoApproveLowRisk = true) =>
        risk == RiskLevel.ReadOnly || (risk == RiskLevel.NormalReversible && autoApproveLowRisk);

    /// <summary>
    /// Explicit user-settings grant of a whole category. This is for the
    /// user changing settings only. It does NOT authorize Sensitive or
    /// higher risk actions (see <see cref="RequestApprovalAsync"/>), and no
    /// approval flow may call it as a side effect of one approval.
    /// </summary>
    public void Grant(PermissionCategory category)
    {
        lock (_gate)
        {
            _granted.Add(category);
        }
    }

    public void Revoke(PermissionCategory category)
    {
        lock (_gate)
        {
            _granted.Remove(category);
        }
    }

    /// <summary>
    /// Asks for approval for a request. Returns true only when the request
    /// is low-risk and its category is already granted, or when a supplied
    /// UI prompt approves it. Sensitive+ requests are NEVER auto-approved
    /// by a category grant: with no UI prompt the safe default is false
    /// (deny), and with a prompt the prompt's answer decides.
    /// </summary>
    public async Task<bool> RequestApprovalAsync(
        ApprovalRequest request,
        Func<ApprovalRequest, Task<bool>>? uiPrompt,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (ct.IsCancellationRequested)
        {
            return false;
        }

        // A category grant must not auto-approve Sensitive+ risks.
        if (!RequiresApproval(request.RiskLevel) && IsGranted(request.Category))
        {
            return true;
        }

        if (uiPrompt is null)
        {
            return false;
        }

        try
        {
            return await uiPrompt(request).WaitAsync(ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return false;
        }
    }

    /// <summary>Alias used by the agent engine: same safe-default semantics as RequestApprovalAsync.</summary>
    public Task<bool> RequireApproval(ApprovalRequest request, CancellationToken ct = default) =>
        RequestApprovalAsync(request, uiPrompt: null, ct);

    /// <summary>Alias used by the agent engine when a UI prompt is available.</summary>
    public Task<bool> RequireApproval(
        ApprovalRequest request,
        Func<ApprovalRequest, Task<bool>>? uiPrompt,
        CancellationToken ct = default) =>
        RequestApprovalAsync(request, uiPrompt, ct);

    /// <summary>
    /// True when the text asks for a destructive, unbounded operation:
    /// deleting everything, formatting, wiping, or recursive forced removal.
    /// </summary>
    public bool IsDestructiveRequest(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        var lower = text.ToLowerInvariant();
        string[] patterns =
        {
            "delete all",
            "delete everything",
            "delete every",
            "delete all files",
            "remove all",
            "remove everything",
            "erase all",
            "erase everything",
            "clear everything",
            "wipe",
            "format",
            "factory reset",
            "rm -rf",
            "rm -r /",
            "rm -rf /",
            "del /s",
            "rd /s",
            "remove-item -recurse -force",
        };
        return patterns.Any(pattern => lower.Contains(pattern, StringComparison.Ordinal));
    }

    /// <summary>
    /// Guard for delete-all style requests. Destructive requests always get
    /// a failure ToolResult refusing the unbounded operation and asking the
    /// user to name a specific, bounded scope. Non-destructive requests pass
    /// through with success.
    /// </summary>
    public Task<ToolResult> GuardDeleteAllAsync(string request, CancellationToken ct = default)
    {
        if (!IsDestructiveRequest(request))
        {
            return Task.FromResult(new ToolResult(
                true,
                "Request is not a destructive delete-all request; no guard was triggered."));
        }

        const string details = "Aurora Operator refuses unbounded delete/wipe/format requests. " +
            "It will not delete everything, format a drive, or wipe a system. " +
            "To proceed, specify an exact, bounded scope — for example the full folder path, " +
            "a named file, or a file pattern — and confirm that scope. " +
            "Once a specific scope is given, the request can be reviewed and approved step by step.";
        return Task.FromResult(new ToolResult(
            false,
            "Refused: destructive delete-all request. Please specify exactly which folder, files, or items to delete.",
            details));
    }
}
