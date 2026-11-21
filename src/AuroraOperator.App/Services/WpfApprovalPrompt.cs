using System.Windows;
using AuroraOperator.App.Views;
using AuroraOperator.Core;

namespace AuroraOperator.App.Services;

/// <summary>
/// The WPF implementation of the approval prompt:
/// <see cref="Func{ApprovalRequest, Task{Boolean}}"/>. Shows the modal
/// <see cref="ApprovalDialog"/> on the UI thread and returns true only
/// when the user presses Approve. Any failure to show the dialog, a
/// missing application dispatcher, or a dismissed window is a denial —
/// approvals must never fail open.
/// </summary>
public sealed class WpfApprovalPrompt
{
    /// <summary>Returns this prompt as the delegate PermissionManager expects.</summary>
    public Func<ApprovalRequest, Task<bool>> AsFunc() => ShowAsync;

    public Task<bool> ShowAsync(ApprovalRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is null)
        {
            return Task.FromResult(false);
        }

        if (dispatcher.CheckAccess())
        {
            return Task.FromResult(ShowDialogCore(request));
        }

        return Task.FromResult(dispatcher.Invoke(() => ShowDialogCore(request)));
    }

    private static bool ShowDialogCore(ApprovalRequest request)
    {
        try
        {
            var dialog = new ApprovalDialog(request);
            return dialog.ShowDialog() == true;
        }
        catch (Exception)
        {
            // Never fail open: if the dialog cannot be shown, deny.
            return false;
        }
    }
}
