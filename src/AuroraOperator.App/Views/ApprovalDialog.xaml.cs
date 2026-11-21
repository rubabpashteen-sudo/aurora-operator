using System.Windows;
using System.Windows.Threading;
using AuroraOperator.App.Services;
using AuroraOperator.App.ViewModels;
using AuroraOperator.Core;
using AuroraOperator.Core.Security;

namespace AuroraOperator.App.Views;

/// <summary>
/// Modal approval dialog. Shows the exact command or change, never a
/// paraphrase, so the user approves precisely what will run. It returns a
/// distinct <see cref="ApprovalOutcome"/> (not just a bool): Approve is
/// Approved, Deny or closing the window is Rejected, the countdown reaching
/// zero is Expired, and a forced close (task cancelled / panel resolve) is
/// the forced outcome. <see cref="DialogResult"/> is kept for bool
/// compatibility (true only on Approve).
/// </summary>
public partial class ApprovalDialog : Window
{
    private readonly DispatcherTimer _countdownTimer;
    private readonly DateTimeOffset _expiresAtUtc;
    private bool _outcomeSet;

    /// <summary>The outcome decided in this dialog. Pending until the dialog closes with a decision.</summary>
    public ApprovalOutcome Outcome { get; private set; } = ApprovalOutcome.Pending;

    public ApprovalDialog(ApprovalRequest request)
        : this(request, request.ExpiresAtUtc is DateTimeOffset expires ? expires - DateTimeOffset.UtcNow : TimeSpan.FromMinutes(5))
    {
    }

    public ApprovalDialog(ApprovalRequest request, TimeSpan timeout)
    {
        ArgumentNullException.ThrowIfNull(request);
        InitializeComponent();

        TitleText.Text = request.Title;
        DescriptionText.Text = request.Description;
        ExactText.Text = request.ExactCommandOrChange;
        CategoryText.Text = request.Category.ToString();
        RiskText.Text = request.RiskLevel.ToString();
        TaskIdText.Text = request.TaskId;
        ActionIdText.Text = string.IsNullOrWhiteSpace(request.ActionId) ? request.StepId : request.ActionId;

        _expiresAtUtc = request.ExpiresAtUtc ?? DateTimeOffset.UtcNow + (timeout <= TimeSpan.Zero ? TimeSpan.FromMinutes(5) : timeout);
        UpdateExpiryText();

        _countdownTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _countdownTimer.Tick += (_, _) =>
        {
            UpdateExpiryText();
            if (DateTimeOffset.UtcNow >= _expiresAtUtc)
            {
                _countdownTimer.Stop();
                SetOutcome(ApprovalOutcome.Expired);
                DialogResult = false;
                Close();
            }
        };
        _countdownTimer.Start();

        Closing += (_, _) =>
        {
            _countdownTimer.Stop();
            if (!_outcomeSet)
            {
                // Closed without a decision (X button, Alt+F4): a denial.
                SetOutcome(ApprovalOutcome.Rejected);
            }
        };

        if (Application.Current?.MainWindow is not null && Application.Current.MainWindow != this)
        {
            Owner = Application.Current.MainWindow;
        }

        SourceInitialized += (_, _) => WindowBackdrop.Apply(this, Owner?.DataContext is not MainViewModel model || model.IsDarkTheme);
        Loaded += (_, _) => DenyButton.Focus();
    }

    /// <summary>Forces the dialog closed with the given outcome (panel resolve / task cancel / expiry). Safe to call from any thread.</summary>
    public void ForceOutcome(ApprovalOutcome outcome)
    {
        if (Dispatcher.CheckAccess())
        {
            SetOutcome(outcome);
            try { DialogResult = outcome == ApprovalOutcome.Approved; } catch (InvalidOperationException) { }
            Close();
        }
        else
        {
            _ = Dispatcher.InvokeAsync(() => ForceOutcome(outcome));
        }
    }

    private void SetOutcome(ApprovalOutcome outcome)
    {
        Outcome = outcome;
        _outcomeSet = true;
    }

    private void UpdateExpiryText()
    {
        var remaining = _expiresAtUtc - DateTimeOffset.UtcNow;
        ExpiryText.Text = remaining <= TimeSpan.Zero
            ? "expired"
            : $"{(int)remaining.TotalMinutes}:{remaining.Seconds:00} (mm:ss)";
    }

    private void ApproveButton_Click(object sender, RoutedEventArgs e)
    {
        SetOutcome(ApprovalOutcome.Approved);
        DialogResult = true;
        Close();
    }

    private void DenyButton_Click(object sender, RoutedEventArgs e)
    {
        SetOutcome(ApprovalOutcome.Rejected);
        DialogResult = false;
        Close();
    }
}
