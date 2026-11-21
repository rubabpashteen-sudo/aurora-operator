using System.Collections.Concurrent;
using System.Windows;
using AuroraOperator.App.Views;
using AuroraOperator.Core;
using AuroraOperator.Core.Security;

namespace AuroraOperator.App.Services;

/// <summary>
/// The WPF implementation of <see cref="IApprovalService"/>: shows the
/// modal <see cref="ApprovalDialog"/> on the UI thread for each request and
/// returns <see cref="ApprovalOutcome.Approved"/> only when the user presses
/// Approve. Deny or closing the window is Rejected, the expiry countdown
/// reaching zero is Expired, task cancellation is Cancelled (and throws
/// through the awaiting caller when the task token fires), and any failure
/// to show the dialog is a denial — approvals never fail open.
/// <para>
/// Pending requests are tracked so the Approvals panel can show them and
/// resolve them (<see cref="TryResolve"/>) and so Stop can cancel them
/// (<see cref="CancelPending"/>).
/// </para>
/// </summary>
public sealed class WpfApprovalService : IApprovalService
{
    private sealed class PendingEntry
    {
        public required ApprovalRequest Request { get; init; }
        public ApprovalDialog? Dialog { get; set; }
        public ApprovalOutcome? ForcedOutcome { get; set; }
    }

    private readonly AuditLog? _auditLog;
    private readonly TimeSpan _defaultTimeout;
    private readonly ConcurrentDictionary<string, PendingEntry> _pending = new();

    public WpfApprovalService(AuditLog? auditLog = null, TimeSpan? defaultTimeout = null)
    {
        _auditLog = auditLog;
        _defaultTimeout = defaultTimeout ?? TimeSpan.FromMinutes(5);
    }

    /// <summary>Kept for the legacy Func-based prompt: adapts this service to the old delegate shape.</summary>
    public Func<ApprovalRequest, Task<bool>> AsFunc() => async request =>
        await RequestApprovalAsync(request, CancellationToken.None).ConfigureAwait(false) == ApprovalOutcome.Approved;

    public event EventHandler? PendingChanged;

    public IReadOnlyList<ApprovalRequest> PendingRequests =>
        _pending.Values.Select(e => e.Request).OrderBy(r => r.RequestedAtUtc).ToList();

    public async Task<ApprovalOutcome> RequestApprovalAsync(ApprovalRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        ct.ThrowIfCancellationRequested();

        var timeout = request.ExpiresAtUtc is DateTimeOffset expires
            ? expires - DateTimeOffset.UtcNow
            : _defaultTimeout;
        if (timeout <= TimeSpan.Zero)
        {
            Log(request, ApprovalOutcome.Expired);
            return ApprovalOutcome.Expired;
        }

        var entry = new PendingEntry { Request = request };
        _pending[request.Id] = entry;
        Log(request, ApprovalOutcome.Pending);
        PendingChanged?.Invoke(this, EventArgs.Empty);

        try
        {
            var dispatcher = Application.Current?.Dispatcher;
            if (dispatcher is null)
            {
                // No UI to ask: fail closed.
                Log(request, ApprovalOutcome.Rejected);
                return ApprovalOutcome.Rejected;
            }

            // Task cancellation closes the dialog (if shown) with Cancelled.
            using var registration = ct.Register(() =>
            {
                entry.ForcedOutcome = ApprovalOutcome.Cancelled;
                CloseDialog(entry);
            });

            ApprovalOutcome outcome;
            if (dispatcher.CheckAccess())
            {
                outcome = ShowDialogCore(request, entry, timeout);
            }
            else
            {
                outcome = await dispatcher.InvokeAsync(() => ShowDialogCore(request, entry, timeout)).Task.ConfigureAwait(false);
            }

            // A service that was cancelled must surface it as cancellation,
            // not as a quiet denial the engine would continue past.
            ct.ThrowIfCancellationRequested();
            Log(request, outcome);
            return outcome;
        }
        finally
        {
            _pending.TryRemove(request.Id, out _);
            PendingChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    /// <summary>Resolves a pending request from the Approvals panel. Closes its dialog (if shown) with the given outcome.</summary>
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
        entry.ForcedOutcome = outcome;
        CloseDialog(entry);
        return true;
    }

    public void CancelPending(string taskId)
    {
        if (string.IsNullOrWhiteSpace(taskId))
        {
            return;
        }
        foreach (var entry in _pending.Values.Where(e => e.Request.TaskId == taskId).ToList())
        {
            entry.ForcedOutcome = ApprovalOutcome.Cancelled;
            CloseDialog(entry);
        }
    }

    private ApprovalOutcome ShowDialogCore(ApprovalRequest request, PendingEntry entry, TimeSpan timeout)
    {
        // Resolved/cancelled before the dialog was shown: never show it.
        if (entry.ForcedOutcome is ApprovalOutcome forced)
        {
            return forced;
        }
        try
        {
            var dialog = new ApprovalDialog(request, timeout);
            entry.Dialog = dialog;
            // A resolve/cancel that arrived after the dialog opened.
            if (entry.ForcedOutcome is ApprovalOutcome forcedAfterOpen)
            {
                dialog.ForceOutcome(forcedAfterOpen);
                return forcedAfterOpen;
            }
            dialog.ShowDialog();
            return entry.ForcedOutcome ?? dialog.Outcome;
        }
        catch (Exception)
        {
            // Never fail open: if the dialog cannot be shown, deny — but
            // keep a forced outcome (cancel / panel resolve) if one was set.
            return entry.ForcedOutcome ?? ApprovalOutcome.Rejected;
        }
        finally
        {
            entry.Dialog = null;
        }
    }

    private static void CloseDialog(PendingEntry entry)
    {
        var dialog = entry.Dialog;
        if (dialog is null)
        {
            return;
        }
        try
        {
            var dispatcher = dialog.Dispatcher;
            if (dispatcher.CheckAccess())
            {
                dialog.ForceOutcome(entry.ForcedOutcome ?? ApprovalOutcome.Cancelled);
            }
            else
            {
                _ = dispatcher.InvokeAsync(() => dialog.ForceOutcome(entry.ForcedOutcome ?? ApprovalOutcome.Cancelled));
            }
        }
        catch (Exception)
        {
            // Closing a dialog that is already gone is not an error.
        }
    }

    private void Log(ApprovalRequest request, ApprovalOutcome outcome)
    {
        try
        {
            _auditLog?.RecordApproval(request, outcome, "ui");
        }
        catch (Exception ex)
        {
            LocalLogger.Warn("Approval audit logging failed: " + ex.Message);
        }
    }
}
