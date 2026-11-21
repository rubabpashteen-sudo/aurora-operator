using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Windows;
using AuroraOperator.App.Services;
using AuroraOperator.App.ViewModels;
using AuroraOperator.Core;
using AuroraOperator.Core.Agent;
using AuroraOperator.Core.Memory;
using AuroraOperator.Core.Ollama;
using AuroraOperator.Core.Security;
using AuroraOperator.Core.Tools;

namespace AuroraOperator.App;

/// <summary>
/// Application startup: creates the data folders, wires the Core services
/// into the MainViewModel, installs a global exception handler, and enforces
/// single-instance on a best-effort basis.
/// </summary>
public partial class App : Application
{
    private Mutex? _singleInstanceMutex;
    private OllamaClient? _ollama;
    private MemoryStore? _memory;
    private MainViewModel? _viewModel;
    private AgentEngine? _engine;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // Data locations (all local, nothing leaves the machine).
        string appDataDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "AuroraOperator");
        if (e.Args is ["--data-directory", string isolatedDirectory] && Path.IsPathFullyQualified(isolatedDirectory))
            appDataDir = Path.TrimEndingDirectorySeparator(Path.GetFullPath(isolatedDirectory));
        string logsDir = Path.Combine(appDataDir, "Logs");
        string modelsDir = Path.Combine(appDataDir, "Models");
        Directory.CreateDirectory(appDataDir);
        Directory.CreateDirectory(logsDir);
        Directory.CreateDirectory(modelsDir);

        LocalLogger.Init(logsDir);
        LocalLogger.Info("Aurora Operator starting.");

        // Global exception handling: log everything, keep the app alive for
        // UI-thread errors, and never show a raw stack trace to the user.
        DispatcherUnhandledException += (_, args) =>
        {
            LocalLogger.Error("Unhandled UI exception.", args.Exception);
            MessageBox.Show(
                "Something went wrong. The error was written to the log in " + logsDir + ".",
                "Aurora Operator",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
            args.Handled = true;
        };
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
        {
            if (args.ExceptionObject is Exception ex)
            {
                LocalLogger.Error("Unhandled background exception.", ex);
            }
            else
            {
                LocalLogger.Error("Unhandled background exception: " + args.ExceptionObject);
            }
        };
        // Fire-and-forget work (e.g. the background initialisation in
        // OnStartup) must not die silently: log unobserved task exceptions
        // instead of letting them vanish.
        TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            LocalLogger.Error("Unobserved task exception.", args.Exception);
            args.SetObserved();
        };

        // Single instance, best effort: if the mutex already exists another
        // copy is running; tell the user and exit. If mutex creation itself
        // fails we log and continue rather than refusing to start.
        try
        {
            bool createdNew;
            var key = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(appDataDir.ToUpperInvariant())));
            _singleInstanceMutex = new Mutex(true, "AuroraOperator.SingleInstance." + key, out createdNew);
            if (!createdNew)
            {
                MessageBox.Show(
                    "Aurora Operator is already running.",
                    "Aurora Operator",
                    MessageBoxButton.OK,
                    MessageBoxImage.Information);
                Shutdown();
                return;
            }
        }
        catch (Exception ex)
        {
            LocalLogger.Warn("Single-instance check failed; continuing anyway. " + ex.Message);
        }

        // Core services. Ollama is localhost-only by design.
        // The HttpClient is owned by App (OllamaClient only disposes a
        // client it created itself) and is released in OnExit, as are the
        // Ollama client, the memory store, the engine and the view model.
        var ollama = new OllamaClient();
        _ollama = ollama;
        var modelManager = new ModelManager(ollama, modelsDir);
        var memory = new MemoryStore(Path.Combine(appDataDir, "aurora.db"));
        _memory = memory;
        var permissions = new PermissionManager();
        var auditLog = new AuditLog(Path.Combine(logsDir, "audit.log"));
        // Runtime approval service: shows the ApprovalDialog on the UI
        // thread and audit-logs every request/decision. The engine gets
        // this same service so its dispatcher gate asks the user; the
        // engine is NOT given the audit log here because the service
        // already logs each decision (passing it too would log twice).
        var approvalService = new WpfApprovalService(auditLog);

        var viewModel = new MainViewModel(
            ollama,
            modelManager,
            memory,
            permissions,
            auditLog,
            approvalService,
            appDataDir);

        // AgentEngine needs the ViewModel's progress reporter so step updates
        // arrive on the UI thread; the ViewModel gets the engine back so its
        // Stop / Pause / Resume commands can control a running task.
        IEnumerable<IAgentTool> tools = new IAgentTool[]
        {
            new FilesystemTools(),
            new CodingTools(),
            new BrowserTools(),
            new AppTools(new WindowsAppControl()),
        };
        // The model is the user's saved default when one exists (Model
        // Manager → Switch persists it in preferences); otherwise the
        // ModelManager's current model, otherwise the primary catalog
        // model. A missing saved model is handled at runtime by the
        // model-not-found fallback — startup must not block on Ollama.
        string savedModel = memory.GetPreference("model") ?? string.Empty;
        string model = !string.IsNullOrWhiteSpace(savedModel)
            ? savedModel
            : string.IsNullOrWhiteSpace(modelManager.CurrentModel)
                ? "qwen3:4b"
                : modelManager.CurrentModel;
        var engine = new AgentEngine(ollama, model, tools, permissions, viewModel.AgentProgress,
            approvalService, new ApprovalSettings(AutoApproveLowRisk: true, ApprovalTimeout: TimeSpan.FromMinutes(5)),
            useWorkspacePlanner: true);
        _engine = engine;
        _viewModel = viewModel;
        viewModel.Engine = engine;

        var window = new MainWindow(viewModel);
        MainWindow = window;
        window.Show();

        // Fire-and-forget initialisation: recent tasks, Ollama status and
        // the model list load in the background; failures are reported in
        // the status bar, never as a startup crash.
        _ = viewModel.InitializeAsync();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        try
        {
            _singleInstanceMutex?.ReleaseMutex();
            _singleInstanceMutex?.Dispose();
        }
        catch (Exception ex)
        {
            LocalLogger.Warn("Failed to release single-instance mutex. " + ex.Message);
        }

        // Release the long-lived services (Phase 5 audit): the view model
        // stops its timer and drops its approval subscription, the engine
        // releases its pause gate, the memory store closes its SQLite
        // connection, and the HttpClient/Ollama client are disposed.
        // Each disposal is guarded so one failure cannot block the rest.
        try { _viewModel?.Dispose(); } catch (Exception ex) { LocalLogger.Warn("Failed to dispose the view model. " + ex.Message); }
        try { _engine?.Dispose(); } catch (Exception ex) { LocalLogger.Warn("Failed to dispose the agent engine. " + ex.Message); }
        try { _memory?.Dispose(); } catch (Exception ex) { LocalLogger.Warn("Failed to dispose the memory store. " + ex.Message); }
        try { _ollama?.Dispose(); } catch (Exception ex) { LocalLogger.Warn("Failed to dispose the Ollama client. " + ex.Message); }

        LocalLogger.Info("Aurora Operator exiting.");
        base.OnExit(e);
    }
}
