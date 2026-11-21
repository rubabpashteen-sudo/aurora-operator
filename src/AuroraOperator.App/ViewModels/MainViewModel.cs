using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Windows.Input;
using System.Windows.Threading;
using AuroraOperator.Core;
using AuroraOperator.Core.Agent;
using AuroraOperator.Core.Memory;
using AuroraOperator.Core.Ollama;
using AuroraOperator.Core.Security;
using AuroraOperator.Core.Tools;
using Microsoft.Win32;

namespace AuroraOperator.App.ViewModels;

/// <summary>One line in the centre chat area.</summary>
public sealed class ChatMessageItem : ViewModelBase
{
    private string _content = string.Empty;
    public required string Role { get; init; }
    public required string Content { get => _content; set => SetProperty(ref _content, value); }
    public DateTime Timestamp { get; init; } = DateTime.Now;
    public bool IsUser => Role == "User";
}

/// <summary>
/// Main window view model. Talks only to the public Core APIs in
/// CONTRACTS.md (OllamaClient, ModelManager, MemoryStore,
/// PermissionManager, AuditLog, AgentEngine, LanguageDetector,
/// ModelCatalog, Diagnostics, LocalLogger).
///
/// Approval flow (Phase 1, per-request): the engine enforces approval in
/// its dispatcher through an <see cref="IApprovalService"/> (the WPF
/// service shows the ApprovalDialog). This view model never grants a
/// permission category as a side effect of an approval — approval is
/// per-request, bound to (TaskId, ActionId, ParameterHash), and
/// <see cref="PermissionManager.Grant"/> is only for explicit user
/// settings. In Plan mode the engine stops at WaitingApproval; this view
/// model asks the user to review each step that RequiresApproval (one
/// request per step, through the same service), and on approval re-runs
/// the plan in Autonomous mode, where the engine asks again at execution
/// time for each sensitive step. Approvals the engine raises mid-run
/// arrive as AgentEvent values with NeedsApproval status; the right-hand
/// Approvals panel itself shows the approval service's real pending
/// requests (with expiry), and Approve/Deny there resolves them through
/// the service. Stop cancels pending approvals as well as the task.
/// <para>
/// LIFETIME (Phase 5 audit): the view model owns a
/// <see cref="DispatcherTimer"/> and a subscription to the approval
/// service's <see cref="IApprovalService.PendingChanged"/>; both are
/// released by <see cref="Dispose"/>, which App calls on exit. The
/// timer is a UI-thread timer, so it never freezes the UI by itself, and
/// the engine runs on a background thread (see SendAsync), so a long
/// tool call cannot block the UI thread either.
/// </para>
/// </summary>
public sealed class MainViewModel : ViewModelBase, IDisposable
{
    private readonly OllamaClient _ollama;
    private readonly ModelManager _modelManager;
    private readonly MemoryStore _memory;
    private readonly PermissionManager _permissions;
    private readonly AuditLog _auditLog;
    private readonly IApprovalService _approvalService;
    private readonly DispatcherTimer _timer;
    private readonly string _appDataDir;
    private readonly EventHandler _pendingChangedHandler;
    private bool _disposed;
    private string? _currentTaskId;

    private AgentEngine? _engine;
    private string _conversationId = Guid.NewGuid().ToString("N");
    private string? _workspacePath;
    private string _historyQuery = string.Empty;
    private AgentTask? _reviewTask;
    private ChatMessageItem? _streamMessage;
    private long _historyVersion;
    private CancellationTokenSource? _cts;
    private string _inputText = string.Empty;
    private string _currentModel = "qwen3:4b";
    private string _selectedModel = "qwen3:4b";
    private AgentMode _mode = AgentMode.Ask;
    private string _ollamaStatus = "Checking Ollama…";
    private bool _isOllamaAvailable;
    private long _memoryUsageMB;
    private bool _isRunning;
    private bool _isPaused;
    private string _statusMessage = "Ready.";
    private string _activePanel = "Task";
    private bool _isScreenContextEnabled;
    private bool _isDarkTheme = true;
    private string _verificationStatus = "Not run yet";
    private string _finalReport = string.Empty;
    private string _lastDiagnosticsSummary = string.Empty;
    private int _timerTicks;
    private bool _refreshingOllama;

    public MainViewModel(
        OllamaClient ollama,
        ModelManager modelManager,
        MemoryStore memory,
        PermissionManager permissions,
        AuditLog auditLog,
        IApprovalService approvalService,
        string appDataDir)
    {
        _ollama = ollama ?? throw new ArgumentNullException(nameof(ollama));
        _modelManager = modelManager ?? throw new ArgumentNullException(nameof(modelManager));
        _memory = memory ?? throw new ArgumentNullException(nameof(memory));
        _permissions = permissions ?? throw new ArgumentNullException(nameof(permissions));
        _auditLog = auditLog ?? throw new ArgumentNullException(nameof(auditLog));
        _approvalService = approvalService ?? throw new ArgumentNullException(nameof(approvalService));
        _appDataDir = appDataDir;

        // The Approvals panel shows the approval service's real pending
        // requests (with expiry); keep it in sync as they arrive/resolve.
        // The handler is kept in a field so Dispose can unsubscribe it.
        _pendingChangedHandler = (_, _) => SyncApprovals();
        _approvalService.PendingChanged += _pendingChangedHandler;

        // Progress<AgentEvent> captures the UI synchronization context here
        // (constructed on the UI thread in App.OnStartup), so engine events
        // arrive on the UI thread and can update collections directly.
        AgentProgress = new Progress<AgentEvent>(OnAgentEvent);

        SendCommand = new RelayCommand(async () => await SendAsync(), () => !IsRunning && !string.IsNullOrWhiteSpace(InputText));
        StopCommand = new RelayCommand(Stop, () => IsRunning);
        PauseCommand = new RelayCommand(Pause, () => IsRunning && !IsPaused);
        ResumeCommand = new RelayCommand(Resume, () => IsRunning && IsPaused);
        NewTaskCommand = new RelayCommand(NewTask, () => !IsRunning);
        AttachFileCommand = new RelayCommand(AttachFiles, () => !IsRunning && Mode == AgentMode.Ask);
        ChooseWorkspaceCommand = new RelayCommand(ChooseWorkspace, () => !IsRunning);
        RunPlanCommand = new RelayCommand(async () => await RunPlanAsync(), () => _reviewTask is not null && !IsRunning);
        DiscardPlanCommand = new RelayCommand(DiscardPlan, () => _reviewTask is not null && !IsRunning);
        CopyCommand = new RelayCommand(value =>
        {
            if (value is not string text) return;
            try { System.Windows.Clipboard.SetText(text); StatusMessage = "Copied to clipboard."; }
            catch (System.Runtime.InteropServices.ExternalException) { StatusMessage = "Clipboard is busy. Try again."; }
        });
        SwitchPanelCommand = new RelayCommand(param =>
        {
            ActivePanel = param as string ?? "Task";
            InspectorRequested?.Invoke(this, EventArgs.Empty);
        });
        ToggleThemeCommand = new RelayCommand(ToggleTheme);
        ApproveCommand = new RelayCommand(param => ResolveApproval(param as ApprovalRequest, approved: true));
        DenyCommand = new RelayCommand(param => ResolveApproval(param as ApprovalRequest, approved: false));
        LoadRecentTasksCommand = new RelayCommand(async () => await LoadHistoryAsync());
        ClearMemoryCommand = new RelayCommand(async () => await ClearHistoryAsync(), () => !IsRunning);
        RefreshModelsCommand = new RelayCommand(async () => await RefreshModelsAsync());
        SwitchModelCommand = new RelayCommand(async () => await SwitchModelAsync(), () => !string.IsNullOrWhiteSpace(SelectedModel));
        DownloadModelCommand = new RelayCommand(async () => await DownloadModelAsync(), () => !string.IsNullOrWhiteSpace(SelectedModel));
        RefreshDiagnosticsCommand = new RelayCommand(() => { RefreshDiagnostics(); StatusMessage = "Diagnostics refreshed."; });

        Messages.Add(new ChatMessageItem
        {
            Role = "Aurora",
            Content = "Ask a question, or choose a workspace to inspect files, find duplicates, or draft a text document.",
        });

        _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
        _timer.Tick += (_, _) => OnTimerTick();
        _timer.Start();
    }

    /// <summary>Legacy constructor: adapts a bool prompt delegate to the approval service. Prefer the <see cref="IApprovalService"/> constructor.</summary>
    public MainViewModel(
        OllamaClient ollama,
        ModelManager modelManager,
        MemoryStore memory,
        PermissionManager permissions,
        AuditLog auditLog,
        Func<ApprovalRequest, Task<bool>> approvalPrompt,
        string appDataDir)
        : this(ollama, modelManager, memory, permissions, auditLog,
            new FuncApprovalServiceAdapter(approvalPrompt ?? throw new ArgumentNullException(nameof(approvalPrompt))),
            appDataDir)
    {
    }

    /// <summary>Adapts a legacy <see cref="Func{ApprovalRequest, Task{Boolean}}"/> prompt to <see cref="IApprovalService"/>.</summary>
    private sealed class FuncApprovalServiceAdapter : IApprovalService
    {
        private readonly Func<ApprovalRequest, Task<bool>> _prompt;
        public FuncApprovalServiceAdapter(Func<ApprovalRequest, Task<bool>> prompt) => _prompt = prompt;
        public async Task<ApprovalOutcome> RequestApprovalAsync(ApprovalRequest request, CancellationToken ct)
        {
            try
            {
                return await _prompt(request).WaitAsync(ct).ConfigureAwait(false)
                    ? ApprovalOutcome.Approved
                    : ApprovalOutcome.Rejected;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (OperationCanceledException)
            {
                return ApprovalOutcome.Expired;
            }
        }
        public void CancelPending(string taskId) { }
    }

    /// <summary>
    /// Releases the UI timer, the approval-service subscription and the
    /// current task's <see cref="CancellationTokenSource"/>. Called by App
    /// on exit; the view model lives for the whole app run, so this is
    /// shutdown hygiene, not a per-task operation.
    /// </summary>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        try { _timer.Stop(); } catch (Exception ex) { LocalLogger.Warn("Could not stop the UI timer. " + ex.Message); }
        try { _approvalService.PendingChanged -= _pendingChangedHandler; } catch (Exception ex) { LocalLogger.Warn("Could not unsubscribe from approval changes. " + ex.Message); }
        try { _cts?.Cancel(); } catch (Exception ex) { LocalLogger.Warn("Could not cancel the running task on dispose. " + ex.Message); }
        try { _cts?.Dispose(); } catch (Exception ex) { LocalLogger.Warn("Could not dispose the task cancellation source. " + ex.Message); }
    }

    // Raised when the user toggles the theme; MainWindow swaps the
    // ResourceDictionary in response. The view model owns the state,
    // the view owns the dictionaries.
    public event EventHandler? ThemeToggleRequested;

    public IProgress<AgentEvent> AgentProgress { get; }

    /// <summary>Set by App after the AgentEngine is constructed.</summary>
    public AgentEngine? Engine
    {
        get => _engine;
        set => SetProperty(ref _engine, value);
    }

    public string AppDataPath => _appDataDir;

    public string OllamaBaseUrl => "http://localhost:11434";

    public ObservableCollection<ChatMessageItem> Messages { get; } = new();
    public ObservableCollection<PlanStep> PlanSteps { get; } = new();
    public ObservableCollection<ApprovalRequest> Approvals { get; } = new();
    public ObservableCollection<AgentTask> RecentTasks { get; } = new();
    public ObservableCollection<string> AttachedFiles { get; } = new();
    public ObservableCollection<string> FilesUsed { get; } = new();
    public ObservableCollection<AvailableModel> AvailableModels { get; } = new();

    public ICommand SendCommand { get; }
    public ICommand StopCommand { get; }
    public ICommand PauseCommand { get; }
    public ICommand ResumeCommand { get; }
    public ICommand NewTaskCommand { get; }
    public ICommand AttachFileCommand { get; }
    public ICommand ChooseWorkspaceCommand { get; }
    public ICommand RunPlanCommand { get; }
    public ICommand DiscardPlanCommand { get; }
    public ICommand CopyCommand { get; }

    public event EventHandler? InspectorRequested;
    public bool HasReviewPlan => _reviewTask is not null;
    public string WorkspaceName => _workspacePath ?? "Choose a workspace";
    public string HistoryQuery
    {
        get => _historyQuery;
        set { if (SetProperty(ref _historyQuery, value)) _ = LoadHistoryAsync(); }
    }
    public ICommand SwitchPanelCommand { get; }
    public ICommand ToggleThemeCommand { get; }
    public ICommand ApproveCommand { get; }
    public ICommand DenyCommand { get; }
    public ICommand LoadRecentTasksCommand { get; }
    public ICommand ClearMemoryCommand { get; }
    public ICommand RefreshModelsCommand { get; }
    public ICommand SwitchModelCommand { get; }
    public ICommand DownloadModelCommand { get; }
    public ICommand RefreshDiagnosticsCommand { get; }

    public string InputText
    {
        get => _inputText;
        set
        {
            if (SetProperty(ref _inputText, value))
            {
                CommandManager.InvalidateRequerySuggested();
            }
        }
    }

    public AgentMode Mode
    {
        get => _mode;
        set => SetProperty(ref _mode, value);
    }

    public string CurrentModel
    {
        get => _currentModel;
        set => SetProperty(ref _currentModel, value);
    }

    public string SelectedModel
    {
        get => _selectedModel;
        set
        {
            if (SetProperty(ref _selectedModel, value))
            {
                CommandManager.InvalidateRequerySuggested();
            }
        }
    }

    public int ContextLength
    {
        get => _modelManager.ContextLength;
        set
        {
            _modelManager.ContextLength = value;
            OnPropertyChanged();
        }
    }

    public string OllamaStatus
    {
        get => _ollamaStatus;
        set => SetProperty(ref _ollamaStatus, value);
    }

    public bool IsOllamaAvailable
    {
        get => _isOllamaAvailable;
        set => SetProperty(ref _isOllamaAvailable, value);
    }

    public long MemoryUsageMB
    {
        get => _memoryUsageMB;
        set => SetProperty(ref _memoryUsageMB, value);
    }

    public bool IsRunning
    {
        get => _isRunning;
        set
        {
            if (SetProperty(ref _isRunning, value))
            {
                CommandManager.InvalidateRequerySuggested();
            }
        }
    }

    public bool IsPaused
    {
        get => _isPaused;
        set
        {
            if (SetProperty(ref _isPaused, value))
            {
                CommandManager.InvalidateRequerySuggested();
            }
        }
    }

    public string StatusMessage
    {
        get => _statusMessage;
        set => SetProperty(ref _statusMessage, value);
    }

    /// <summary>Which section the right panel shows: Task, Recent, Projects, Files, Memory, ModelManager, Activity, Settings.</summary>
    public string ActivePanel
    {
        get => _activePanel;
        set => SetProperty(ref _activePanel, value);
    }

    public bool IsScreenContextEnabled
    {
        get => _isScreenContextEnabled;
        set => SetProperty(ref _isScreenContextEnabled, value);
    }

    public bool IsDarkTheme
    {
        get => _isDarkTheme;
        set => SetProperty(ref _isDarkTheme, value);
    }

    public string VerificationStatus
    {
        get => _verificationStatus;
        set => SetProperty(ref _verificationStatus, value);
    }

    public string FinalReport
    {
        get => _finalReport;
        set => SetProperty(ref _finalReport, value);
    }

    public string LastDiagnosticsSummary
    {
        get => _lastDiagnosticsSummary;
        set => SetProperty(ref _lastDiagnosticsSummary, value);
    }

    /// <summary>First load: recent tasks, Ollama status, model list, diagnostics.</summary>
    public async Task InitializeAsync()
    {
        await LoadHistoryAsync();
        await RefreshOllamaStatusAsync();
        await RefreshModelsAsync();
        RefreshDiagnostics();
        StatusMessage = IsOllamaAvailable
            ? "Ready. Local model: " + CurrentModel + "."
            : "Ollama is not running. Start Ollama, or open Model Manager to install it.";
    }

    /// <summary>
    /// Approval prompt through the runtime approval service: one
    /// per-request decision, bound to the request's (TaskId, ActionId,
    /// ParameterHash). Returns true only on an Approved outcome.
    /// </summary>
    public async Task<bool> RequestApprovalAsync(ApprovalRequest request, CancellationToken ct)
    {
        try
        {
            return await _approvalService.RequestApprovalAsync(request, ct).ConfigureAwait(false)
                == ApprovalOutcome.Approved;
        }
        catch (OperationCanceledException)
        {
            return false;
        }
    }

    /// <summary>Loads a previous task back into the centre view (double-click in Recent Tasks).</summary>
    public async void LoadTaskIntoView(AgentTask task)
    {
        if (IsRunning || task is null) return;
        try
        {
            string id = task.ConversationId ?? task.TaskId;
            var turns = await Task.Run(() => _memory.ReadConversationTasks(id));
            if (IsRunning || turns.Count == 0) return;
            SetReview(null);
            _conversationId = id;
            _workspacePath = turns[^1].WorkspacePath;
            OnPropertyChanged(nameof(WorkspaceName));
            Messages.Clear();
            foreach (var turn in turns)
            {
                AddMessage("User", turn.UserRequest);
                if (!string.IsNullOrEmpty(turn.FinalReport)) AddMessage("Aurora", turn.FinalReport);
                if (turn.State is TaskState.Created or TaskState.Planning or TaskState.Running or TaskState.Paused or TaskState.WaitingApproval)
                    AddMessage("System", "This task was interrupted or left pending. Start a new request to continue.");
            }
            InputText = string.Empty;
            Mode = turns[^1].Mode;
            FinalReport = turns[^1].FinalReport ?? string.Empty;
            VerificationStatus = turns[^1].State.ToString();
            PlanSteps.Clear();
            AttachedFiles.Clear();
            FilesUsed.Clear();
            ActivePanel = "Task";
            StatusMessage = "Conversation reopened.";
        }
        catch (Exception ex) { StatusMessage = "Could not reopen the conversation: " + ex.Message; }
    }

    private void OnTimerTick()
    {
        _timerTicks++;
        try
        {
            MemoryUsageMB = Process.GetCurrentProcess().WorkingSet64 / 1024 / 1024;
        }
        catch (Exception ex)
        {
            LocalLogger.Warn("Could not read process memory usage. " + ex.Message);
        }

        // Refresh the heavier checks about every 30 seconds, never overlapping.
        if (_timerTicks % 15 == 0 && !_refreshingOllama)
        {
            _refreshingOllama = true;
            _ = RefreshOllamaStatusAsync()
                .ContinueWith(_ => _refreshingOllama = false, TaskScheduler.Default);
            RefreshDiagnostics();
        }
    }

    private void RefreshDiagnostics()
    {
        try
        {
            SystemReport report = Diagnostics.Diagnose();
            LastDiagnosticsSummary = report.ToString() ?? "Diagnostics returned no summary.";
        }
        catch (Exception ex)
        {
            LastDiagnosticsSummary = "Diagnostics unavailable: " + ex.Message;
            LocalLogger.Warn("Diagnostics failed. " + ex.Message);
        }
    }

    private async Task RefreshOllamaStatusAsync()
    {
        try
        {
            IsOllamaAvailable = await _ollama.IsAvailableAsync(CancellationToken.None);
            OllamaStatus = IsOllamaAvailable
                ? "Connected to Ollama (localhost:11434)"
                : "Ollama offline — start Ollama to use local AI";
        }
        catch (Exception ex)
        {
            IsOllamaAvailable = false;
            OllamaStatus = "Ollama offline — " + ex.Message;
            LocalLogger.Warn("Ollama availability check failed. " + ex.Message);
        }
    }

    private async Task RefreshModelsAsync()
    {
        try
        {
            // Merged list: every catalog entry (flagged installed or
            // not) plus installed models that are not in the catalog
            // (for example qwen3.5:4b) — see ModelFallback.
            IReadOnlyList<AvailableModel> models = await _modelManager.GetAvailableModelsAsync(CancellationToken.None);
            AvailableModels.Clear();
            foreach (AvailableModel model in models)
            {
                AvailableModels.Add(model);
            }
        }
        catch (Exception ex)
        {
            LocalLogger.Warn("Could not list Ollama models. " + ex.Message);
        }

        if (AvailableModels.Count == 0)
        {
            // Ollama is down: show the known catalogue so the user can
            // still pick (and download) a model.
            foreach (ModelInfo info in ModelCatalog.All)
            {
                AvailableModels.Add(new AvailableModel(info.Name, false, true, info.Role));
            }
        }

        if (!string.IsNullOrWhiteSpace(_modelManager.CurrentModel))
        {
            CurrentModel = _modelManager.CurrentModel;
        }
        else if (AvailableModels.Count > 0 &&
                 !AvailableModels.Any(m => string.Equals(m.Name, CurrentModel, StringComparison.OrdinalIgnoreCase)))
        {
            // Prefer an installed model; otherwise the first catalog entry.
            CurrentModel = AvailableModels.FirstOrDefault(m => m.IsInstalled)?.Name ?? AvailableModels[0].Name;
        }

        SelectedModel = CurrentModel;
    }

    private async Task SwitchModelAsync()
    {
        try
        {
            StatusMessage = "Switching model to " + SelectedModel + "…";
            bool ok = await _modelManager.SwitchModelAsync(SelectedModel, CancellationToken.None);
            if (ok)
            {
                CurrentModel = SelectedModel;
                // Persist the choice so the next app start uses it, and
                // tell the running engine now — previously a switch did
                // nothing until the app restarted, because the engine kept
                // its startup model.
                try
                {
                    _memory.SetPreference("model", SelectedModel);
                }
                catch (Exception ex)
                {
                    LocalLogger.Warn("Could not save the default model. " + ex.Message);
                }
                Engine?.UpdateModel(SelectedModel);
                StatusMessage = "Model switched to " + SelectedModel + " and saved as the default. New tasks will use it.";
            }
            else
            {
                StatusMessage = "Could not switch to " + SelectedModel + ". It may not be downloaded yet — use Download first.";
            }
        }
        catch (Exception ex)
        {
            StatusMessage = "Model switch failed: " + ex.Message;
            LocalLogger.Error("Model switch failed.", ex);
        }
    }

    private async Task DownloadModelAsync()
    {
        try
        {
            // A model download is a multi-GB external download: it is an
            // external side effect and must be approved first, through the
            // approval service — never downloaded silently.
            ModelInfo? info = ModelCatalog.All.FirstOrDefault(m =>
                string.Equals(m.Name, SelectedModel, StringComparison.OrdinalIgnoreCase));
            string sizeText = info is null ? string.Empty : $" (~{info.ApproxSizeBytes / 1_000_000} MB)";
            string canonical = ApprovalService.CanonicalParameters("ollama-pull",
                new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["model"] = SelectedModel },
                "Download model " + SelectedModel + " from the Ollama library");
            var request = new ApprovalRequest(Guid.NewGuid().ToString("N"), "model-download",
                "Download model " + SelectedModel,
                "Downloads " + SelectedModel + sizeText + " from the Ollama model library to this PC (a large download).",
                "ollama pull " + SelectedModel,
                PermissionCategory.NetworkAccess)
            {
                ActionId = "download-model:" + SelectedModel,
                RiskLevel = RiskLevel.ExternalCommunication,
                ExactParameters = canonical,
                ParameterHash = ApprovalService.ComputeParameterHash(canonical),
                ExpiresAtUtc = DateTimeOffset.UtcNow + TimeSpan.FromMinutes(5),
            };
            bool approved = await PromptForApprovalAsync(request, CancellationToken.None);
            if (!approved)
            {
                StatusMessage = "Download of " + SelectedModel + " cancelled — approval was not granted.";
                return;
            }

            var progress = new Progress<double>(p =>
                StatusMessage = "Downloading " + SelectedModel + ": " + p.ToString("P0"));
            StatusMessage = "Downloading " + SelectedModel + "…";
            bool ok = await _modelManager.DownloadModelAsync(SelectedModel, progress, CancellationToken.None);
            StatusMessage = ok
                ? "Downloaded " + SelectedModel + "."
                : "Download of " + SelectedModel + " did not complete. Check the Ollama log and try again.";
            await RefreshModelsAsync();
        }
        catch (Exception ex)
        {
            StatusMessage = "Model download failed: " + ex.Message;
            LocalLogger.Error("Model download failed.", ex);
        }
    }

    private async Task SendAsync()
    {
        if (_engine is null)
        {
            StatusMessage = "The agent engine is not ready yet. Please wait a moment and try again.";
            return;
        }

        string text = InputText.Trim();
        if (string.IsNullOrWhiteSpace(text))
        {
            return;
        }

        // Destructive requests are refused up front, with an explanation,
        // and never reach the engine. (PermissionManager.GuardDeleteAllAsync)
        if (_permissions.IsDestructiveRequest(text))
        {
            ToolResult guard = await _permissions.GuardDeleteAllAsync(text);
            AddMessage("Aurora", guard.Summary + (string.IsNullOrWhiteSpace(guard.Details) ? string.Empty : "\n" + guard.Details));
            StatusMessage = "Refused a destructive request. Narrow the scope and try again.";
            return;
        }

        Language language = LanguageDetector.Detect(text);
        SetReview(null);
        var history = Messages.Where(m => m.Role is "User" or "Aurora").TakeLast(6)
            .Select(m => new ChatMessage(m.IsUser ? "user" : "assistant", m.Content.Length <= 1800 ? m.Content : m.Content[..1800])).ToList();
        var task = new AgentTask(Guid.NewGuid().ToString("N"), text, language, Mode)
        {
            WorkspacePath = _workspacePath,
            ConversationId = _conversationId,
            ChatHistory = history,
        };
        _currentTaskId = task.TaskId;
        _streamMessage = null;
        AddMessage("User", text);

        InputText = string.Empty;
        var attachments = AttachedFiles.ToArray();
        AttachedFiles.Clear();
        PlanSteps.Clear();
        FilesUsed.Clear();
        foreach (string file in attachments) FilesUsed.Add(file);

        FinalReport = string.Empty;
        VerificationStatus = "Running…";
        StatusMessage = "Working on your task…";

        _cts?.Dispose();
        _cts = new CancellationTokenSource();
        CancellationToken ct = _cts.Token;
        IsRunning = true;
        IsPaused = false;

        try
        {
            await Task.Run(() => SaveTaskQuietly(task));
            if (task.Mode == AgentMode.Ask)
                await LoadAttachmentsAsync(attachments, history, ct);
            AgentTask result = await Task.Run(() => _engine.RunAsync(task, ct), ct);
            ApplyTaskResult(result);
            if (result.Mode == AgentMode.Plan && result.State == TaskState.WaitingApproval)
                SetReview(result);
            await Task.Run(() => SaveTaskQuietly(result));
            await LoadHistoryAsync();
        }
        catch (OperationCanceledException)
        {
            task.State = TaskState.Cancelled;
            AddMessage("System", "Task cancelled.");
            VerificationStatus = "Cancelled";
            StatusMessage = "Task cancelled.";
            SaveTaskQuietly(task);
        }
        catch (Exception ex)
        {
            task.State = TaskState.Failed;
            LocalLogger.Error("Task " + task.TaskId + " failed.", ex);
            AddMessage("Aurora", "That task failed: " + ex.Message);
            VerificationStatus = "Failed";
            StatusMessage = "Task failed: " + ex.Message;
            SaveTaskQuietly(task);
        }
        finally
        {
            IsRunning = false;
            IsPaused = false;
            _currentTaskId = null;
            // The task is over: release its cancellation source now instead
            // of leaking one per task (the previous code only disposed it
            // when the next task started).
            _cts?.Dispose();
            _cts = null;
        }
    }

    private void ChooseWorkspace()
    {
        var dialog = new OpenFolderDialog { Title = "Choose the folder Aurora may work in" };
        if (dialog.ShowDialog() != true) return;
        _workspacePath = dialog.FolderName;
        SetReview(null);
        OnPropertyChanged(nameof(WorkspaceName));
        StatusMessage = "Workspace selected.";
    }

    private void SetReview(AgentTask? task)
    {
        _reviewTask = task;
        OnPropertyChanged(nameof(HasReviewPlan));
        CommandManager.InvalidateRequerySuggested();
    }

    private async void DiscardPlan()
    {
        if (_reviewTask is not { } task) return;
        SetReview(null);
        task.State = TaskState.Cancelled;
        task.FinalReport = "Plan discarded.";
        ApplyTaskResult(task);
        await Task.Run(() => SaveTaskQuietly(task));
    }

    private async Task RunPlanAsync()
    {
        if (_reviewTask is not { } task || _engine is null) return;
        SetReview(null);
        _currentTaskId = task.TaskId;
        using var cancellation = new CancellationTokenSource();
        _cts = cancellation;
        IsRunning = true;
        try
        {
            AgentTask result = await Task.Run(() => _engine.RunReviewedAsync(task, cancellation.Token));
            ApplyTaskResult(result);
            await Task.Run(() => SaveTaskQuietly(result));
            await LoadHistoryAsync();
        }
        catch (Exception ex) { StatusMessage = "Could not run the plan: " + ex.Message; }
        finally
        {
            IsRunning = false;
            IsPaused = false;
            _cts = null;
            _currentTaskId = null;
        }
    }

    private async Task LoadAttachmentsAsync(IReadOnlyList<string> attachments, List<ChatMessage> context, CancellationToken ct)
    {
        foreach (string path in attachments.Take(3))
        {
            try
            {
                await using var stream = File.OpenRead(path);
                var bytes = new byte[4097];
                int count = await stream.ReadAtLeastAsync(bytes, bytes.Length, false, ct);
                string text = new System.Text.UTF8Encoding(false, true).GetString(bytes, 0, count);
                if (count == bytes.Length || text.Contains('\0'))
                {
                    AddMessage("System", $"Skipped {Path.GetFileName(path)}. Use UTF-8 text attachments up to 4 KiB.");
                    continue;
                }
                context.Add(new ChatMessage("user", System.Text.Json.JsonSerializer.Serialize(
                    new { attachment = Path.GetFileName(path), untrustedText = text })));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Text.DecoderFallbackException)
            {
                AddMessage("System", $"Could not read attachment {Path.GetFileName(path)}.");
            }
        }
    }

    private void ApplyTaskResult(AgentTask result)
    {
        PlanSteps.Clear();
        if (result.Plan is not null)
        {
            foreach (PlanStep step in result.Plan.Steps)
            {
                PlanSteps.Add(step);
            }
        }

        foreach (string artifact in result.Artifacts)
        {
            if (!FilesUsed.Contains(artifact))
            {
                FilesUsed.Add(artifact);
            }
        }

        FinalReport = result.FinalReport ?? string.Empty;
        if (!string.IsNullOrWhiteSpace(result.FinalReport))
        {
            if (_streamMessage is null) AddMessage("Aurora", result.FinalReport);
            else _streamMessage.Content = result.FinalReport;
        }
        else if (result.Log.Count > 0)
        {
            AddMessage("Aurora", result.Log[result.Log.Count - 1]);
        }

        VerificationStatus = result.State switch
        {
            TaskState.Completed => "Completed",
            TaskState.Failed => "Failed — see the chat log",
            TaskState.WaitingApproval => "Waiting for approval",
            TaskState.Paused => "Paused",
            TaskState.Cancelled => "Cancelled",
            TaskState.Blocked => "Blocked — see the chat log",
            _ => result.State.ToString(),
        };
        StatusMessage = "Task " + result.State + ".";

        // A model-not-found fallback happened during this task: say so in
        // the status bar too, and point at the permanent fix. The chat
        // reply already carries the same notice.
        if (_engine?.SessionFallbackModel is string fallback &&
            _engine.LastMissingModel is string missing)
        {
            StatusMessage = $"Using {fallback} this session because {missing} is not installed. " +
                "Switch in Model Manager to make it the default, or download " + missing + " from there.";
        }

        int index = RecentTasks.IndexOf(result);
        if (index >= 0)
        {
            RecentTasks[index] = result;
        }

        _streamMessage = null;
    }

    /// <summary>
    /// Shows a request in the right-hand Approvals panel and asks the
    /// approval service for a per-request decision. Returns true only on
    /// Approved. The service audit-logs the request and the decision.
    /// </summary>
    private async Task<bool> PromptForApprovalAsync(ApprovalRequest request, CancellationToken ct)
    {
        Approvals.Add(request);
        ActivePanel = "Task";
        StatusMessage = "Approval required: " + request.Title + " — see the right panel.";
        try
        {
            var outcome = await _approvalService.RequestApprovalAsync(request, ct).ConfigureAwait(false);
            return outcome == ApprovalOutcome.Approved;
        }
        catch (OperationCanceledException)
        {
            return false;
        }
        finally
        {
            Approvals.Remove(request);
        }
    }

    /// <summary>
    /// Resolves a pending approval from the Approvals panel through the
    /// approval service. Per-request only: it never grants or revokes a
    /// permission category.
    /// </summary>
    private void ResolveApproval(ApprovalRequest? request, bool approved)
    {
        if (request is null)
        {
            return;
        }

        var outcome = approved ? ApprovalOutcome.Approved : ApprovalOutcome.Rejected;
        if (!_approvalService.TryResolve(request.Id, outcome))
        {
            // Not pending in the service (stale panel entry): this click
            // authorised nothing, so the audit record must say so. (Phase 7:
            // recording a plain "approve" here claimed an approval that
            // never happened.)
            _auditLog.Record(request.TaskId, approved ? "approve-stale" : "deny-stale",
                "Stale Approvals-panel entry for '" + request.Title + "': no pending request in the approval service, so this decision authorised nothing.",
                approved);
            Approvals.Remove(request);
        }

        StatusMessage = (approved ? "Approved: " : "Denied: ") + request.Title;
    }

    /// <summary>Rebuilds the Approvals panel from the approval service's real pending requests (with expiry).</summary>
    private void SyncApprovals()
    {
        try
        {
            var pending = _approvalService.PendingRequests;
            var dispatcher = System.Windows.Application.Current?.Dispatcher;
            if (dispatcher is not null && !dispatcher.CheckAccess())
            {
                _ = dispatcher.InvokeAsync(SyncApprovals);
                return;
            }
            Approvals.Clear();
            foreach (var request in pending)
            {
                Approvals.Add(request);
            }
            if (pending.Count > 0)
            {
                ActivePanel = "Task";
                StatusMessage = "Approval requested — see the right panel.";
            }
        }
        catch (Exception ex)
        {
            LocalLogger.Warn("SyncApprovals failed. " + ex.Message);
        }
    }

    private void Stop()
    {
        try
        {
            // Cancel any approval still waiting for this task first, so no
            // pending action can be approved after the stop.
            if (_currentTaskId is not null)
            {
                _approvalService.CancelPending(_currentTaskId);
            }
            _cts?.Cancel();
        }
        catch (Exception ex)
        {
            LocalLogger.Warn("Stop failed. " + ex.Message);
        }

        StatusMessage = "Stopping…";
    }

    private void Pause()
    {
        try
        {
            _engine?.Pause();
            IsPaused = true;
            StatusMessage = "Paused. Press Resume to continue.";
        }
        catch (Exception ex)
        {
            StatusMessage = "Pause failed: " + ex.Message;
            LocalLogger.Error("Pause failed.", ex);
        }
    }

    private void Resume()
    {
        try
        {
            _engine?.Resume();
            IsPaused = false;
            StatusMessage = "Resumed.";
        }
        catch (Exception ex)
        {
            StatusMessage = "Resume failed: " + ex.Message;
            LocalLogger.Error("Resume failed.", ex);
        }
    }

    private void NewTask()
    {
        if (_currentTaskId is not null)
        {
            _approvalService.CancelPending(_currentTaskId);
        }
        _cts?.Cancel();
        _currentTaskId = null;
        SetReview(null);
        _conversationId = Guid.NewGuid().ToString("N");
        InputText = string.Empty;
        Messages.Clear();
        PlanSteps.Clear();
        FilesUsed.Clear();
        AttachedFiles.Clear();
        FinalReport = string.Empty;
        VerificationStatus = "Not run yet";
        ActivePanel = "Task";
        StatusMessage = "New task started.";
        Messages.Add(new ChatMessageItem
        {
            Role = "Aurora",
            Content = "Fresh task. What would you like me to do?",
        });
    }

    private void AttachFiles()
    {
        var dialog = new OpenFileDialog
        {
            Title = "Attach files for Aurora to use",
            Multiselect = true,
            CheckFileExists = true,
        };
        if (dialog.ShowDialog() == true)
        {
            foreach (string file in dialog.FileNames)
            {
                if (!AttachedFiles.Contains(file))
                {
                    AttachedFiles.Add(file);
                    FilesUsed.Add(file);
                }
            }

            StatusMessage = dialog.FileNames.Length + " file(s) attached.";
        }
    }

    private async Task LoadHistoryAsync()
    {
        long version = ++_historyVersion;
        string query = HistoryQuery.Trim();
        try
        {
            var results = await Task.Run(() => _memory.SearchTaskHistory(query));
            if (version != _historyVersion) return;
            RecentTasks.Clear();
            foreach (var task in results) RecentTasks.Add(task);
        }
        catch (Exception ex) { StatusMessage = "Could not load local history: " + ex.Message; }
    }

    private async Task ClearHistoryAsync()
    {
        if (System.Windows.MessageBox.Show("Delete all locally saved conversations and task history?", "Clear local history",
            System.Windows.MessageBoxButton.YesNo, System.Windows.MessageBoxImage.Warning) != System.Windows.MessageBoxResult.Yes)
            return;
        IsRunning = true;
        try
        {
            await Task.Run(_memory.ClearHistory);
            NewTask();
            RecentTasks.Clear();
            await LoadHistoryAsync();
            StatusMessage = "Local conversation and task history cleared.";
        }
        catch (Exception ex) { StatusMessage = "Could not clear local history: " + ex.Message; }
        finally { IsRunning = false; }
    }

    private void ToggleTheme()
    {
        IsDarkTheme = !IsDarkTheme;
        ThemeToggleRequested?.Invoke(this, EventArgs.Empty);
    }

    private void OnAgentEvent(AgentEvent evt)
    {
        if (evt.TaskId != _currentTaskId) return;
        if (evt.IsStreaming)
        {
            if (_streamMessage is null)
            {
                _streamMessage = new ChatMessageItem { Role = "Aurora", Content = evt.Message };
                Messages.Add(_streamMessage);
            }
            else _streamMessage.Content = evt.Message;
            return;
        }
        StatusMessage = evt.Message.Split('\n')[0];
        if (evt.Step is { } snapshot)
        {
            var existing = PlanSteps.FirstOrDefault(step => step.Id == snapshot.Id);
            if (existing is null) PlanSteps.Add(snapshot);
            else PlanSteps[PlanSteps.IndexOf(existing)] = snapshot;
        }
        else if (!string.IsNullOrWhiteSpace(evt.StepId))
        {
            for (int i = 0; i < PlanSteps.Count; i++)
            {
                if (PlanSteps[i].Id == evt.StepId)
                {
                    // PlanStep is a record: replace the item so the UI refreshes.
                    PlanSteps[i] = PlanSteps[i] with { Status = evt.Status };
                    break;
                }
            }
        }

        // A NeedsApproval event means the engine already asked the approval
        // service and the step was not approved — the step was not executed.
        // No approval request is fabricated here: the Approvals panel shows
        // only the service's real pending requests (see SyncApprovals).
        if (evt.Status == StepStatus.NeedsApproval)
        {
            ActivePanel = "Task";
            StatusMessage = "Approval required — " + evt.Message;
        }
    }

    private void SaveTaskQuietly(AgentTask task)
    {
        try
        {
            _memory.SaveTask(task);
        }
        catch (Exception ex)
        {
            LocalLogger.Warn("Could not save task " + task.TaskId + ". " + ex.Message);
        }
    }

    private void AddMessage(string role, string content)
    {
        Messages.Add(new ChatMessageItem { Role = role, Content = content });
    }

}
