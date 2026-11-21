using System.Net;
using System.Text;
using AuroraOperator.Core;
using AuroraOperator.Core.Agent;
using AuroraOperator.Core.Memory;
using AuroraOperator.Core.Ollama;
using AuroraOperator.Core.Security;
using AuroraOperator.Core.Tools;
using Microsoft.Data.Sqlite;

namespace AuroraOperator.Tests;

/// <summary>
/// Phase 3/5 regression tests (stability audit): pause/resume mid-task,
/// cancel-while-paused, retry re-execution semantics, interrupted-task
/// recovery via MemoryStore, corrupt-row tolerance, overwrite approval
/// gating, Ollama unload/health-check, and null-byte path rejection.
/// Every test here runs on Linux against Core; the Windows-only paths
/// (real Notepad typing, WPF) are covered by the manual pass in TESTS.md.
/// </summary>
public sealed class Phase3Tests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "aurora-phase3-" + Guid.NewGuid().ToString("N"));
    public Phase3Tests() => Directory.CreateDirectory(_root);
    public void Dispose() { try { Directory.Delete(_root, true); } catch { } }

    private static Func<AgentTask, TaskPlan> FixedPlan(params PlanStep[] steps) =>
        task => new TaskPlan(task.TaskId, task.UserRequest, Language.English, steps, "test plan");

    private static PlanStep ReadOnlyStep(string id) =>
        new(id, "List files", "test-tool", "List files. action=list; path=/tmp")
        {
            Args = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["action"] = "list",
                ["path"] = "/tmp",
            },
        };

    // A tool whose behaviour the test controls: it can pause the engine
    // mid-run, fail a set number of times, or block until released.
    private sealed class ControlledTool : IAgentTool
    {
        public string Name => "test-tool";
        public string Description => "Controlled test tool.";
        public int ExecutionCount;
        public int FailuresBeforeSuccess;
        public Action? OnExecute;

        public async Task<ToolResult> ExecuteAsync(IReadOnlyDictionary<string, string> args, CancellationToken ct)
        {
            await Task.CompletedTask.ConfigureAwait(false);
            ExecutionCount++;
            OnExecute?.Invoke();
            if (ExecutionCount <= FailuresBeforeSuccess)
            {
                return new ToolResult(false, "Controlled failure.");
            }
            return new ToolResult(true, "Controlled success.");
        }
    }

    // ---- Pause / resume mid-task -------------------------------------

    [Fact]
    public async Task PauseMidTask_BlocksProgress_ResumeLetsItComplete()
    {
        var tool = new ControlledTool();
        AgentEngine? engine = null;
        // The first step pauses the engine; the second step must wait for Resume().
        tool.OnExecute = () => engine?.Pause();
        engine = new AgentEngine(null, "qwen3:4b", new IAgentTool[] { tool }, new PermissionManager(),
            planProvider: FixedPlan(ReadOnlyStep("s1"), ReadOnlyStep("s2")));
        var task = new AgentTask("t-pause-mid", "List files", Language.English, AgentMode.Autonomous);
        var run = engine.RunAsync(task, CancellationToken.None);

        await Task.Delay(500, CancellationToken.None);
        Assert.False(run.IsCompleted, "The run finished while paused; Pause() did not block the second step.");
        Assert.Equal(TaskState.Paused, task.State);

        engine.Resume();
        var done = await run;
        Assert.Equal(TaskState.Completed, done.State);
        Assert.Equal(2, tool.ExecutionCount);
        engine.Dispose();
    }

    [Fact]
    public async Task PauseBeforeStart_BlocksUntilResume()
    {
        var tool = new ControlledTool();
        using var engine = new AgentEngine(null, "qwen3:4b", new IAgentTool[] { tool }, new PermissionManager(),
            planProvider: FixedPlan(ReadOnlyStep("s1")));
        engine.Pause();
        var task = new AgentTask("t-pause-pre", "List files", Language.English, AgentMode.Autonomous);
        var run = engine.RunAsync(task, CancellationToken.None);

        await Task.Delay(400, CancellationToken.None);
        Assert.False(run.IsCompleted, "The run started while the engine was paused.");
        Assert.Equal(0, tool.ExecutionCount);

        engine.Resume();
        var done = await run;
        Assert.Equal(TaskState.Completed, done.State);
        Assert.Equal(1, tool.ExecutionCount);
    }

    [Fact]
    public async Task CancelWhilePaused_ResultsInCancelledState_DoesNotHang()
    {
        using var engine = new AgentEngine(null, "qwen3:4b", new IAgentTool[] { new ControlledTool() }, new PermissionManager(),
            planProvider: FixedPlan(ReadOnlyStep("s1")));
        engine.Pause();
        using var cts = new CancellationTokenSource();
        var task = new AgentTask("t-cancel-paused", "List files", Language.English, AgentMode.Autonomous);
        var run = engine.RunAsync(task, cts.Token);

        await Task.Delay(300, CancellationToken.None);
        Assert.False(run.IsCompleted, "The run finished while paused; the test setup is wrong.");
        cts.Cancel();
        var done = await run.WaitAsync(TimeSpan.FromSeconds(10), CancellationToken.None);
        Assert.Equal(TaskState.Cancelled, done.State);
    }

    // ---- Retry semantics ----------------------------------------------

    [Fact]
    public async Task Retry_FailingThenSucceedingTool_ReExecutesSameStep_AndSucceeds()
    {
        // Documented retry semantics (AgentEngine docs): a retry
        // re-executes the SAME step — up to 3 attempts total. The
        // ActionId dedup blocks a *different* step re-using the id; it
        // does not block a retry of the step itself.
        var tool = new ControlledTool { FailuresBeforeSuccess = 1 };
        using var engine = new AgentEngine(null, "qwen3:4b", new IAgentTool[] { tool }, new PermissionManager(),
            planProvider: FixedPlan(ReadOnlyStep("s1")));
        var done = await engine.RunAsync(
            new AgentTask("t-retry", "List files", Language.English, AgentMode.Autonomous), CancellationToken.None);

        Assert.Equal(TaskState.Completed, done.State);
        Assert.Equal(StepStatus.Succeeded, done.Plan!.Steps[0].Status);
        Assert.Equal(2, tool.ExecutionCount);
    }

    [Fact]
    public async Task Retry_PermanentlyFailingTool_StopsAfterMaxRetries()
    {
        var tool = new ControlledTool { FailuresBeforeSuccess = 99 };
        using var engine = new AgentEngine(null, "qwen3:4b", new IAgentTool[] { tool }, new PermissionManager(),
            planProvider: FixedPlan(ReadOnlyStep("s1")));
        var done = await engine.RunAsync(
            new AgentTask("t-retry-max", "List files", Language.English, AgentMode.Autonomous), CancellationToken.None);

        Assert.Equal(TaskState.Failed, done.State);
        Assert.Equal(StepStatus.Failed, done.Plan!.Steps[0].Status);
        // 1 initial attempt + MaxRetriesPerStep (2) retries, no more.
        Assert.Equal(1 + AgentEngine.MaxRetriesPerStep, tool.ExecutionCount);
    }

    // ---- Interrupted-task recovery ------------------------------------

    [Fact]
    public void InterruptedTask_CancelledAndFailedStates_RoundTrip_ThroughMemoryStore()
    {
        var db = Path.Combine(_root, "recovery.db");
        using var store = new MemoryStore(db);
        var cancelled = new AgentTask("task-cancelled", "Organise Downloads", Language.English, AgentMode.Autonomous)
        { State = TaskState.Cancelled, FinalReport = "Task was cancelled." };
        var failed = new AgentTask("task-failed", "Research bikes", Language.English, AgentMode.Autonomous)
        { State = TaskState.Failed, FinalReport = "Engine error: boom." };
        // A task left Running when the app was killed: on restart it loads
        // back as Running, which the app treats as interrupted (it is never
        // silently resumed or re-executed).
        var interrupted = new AgentTask("task-interrupted", "Write a report", Language.English, AgentMode.Autonomous)
        { State = TaskState.Running };
        store.SaveTask(cancelled);
        store.SaveTask(failed);
        store.SaveTask(interrupted);

        Assert.Equal(TaskState.Cancelled, store.GetTask("task-cancelled")!.State);
        Assert.Equal("Task was cancelled.", store.GetTask("task-cancelled")!.FinalReport);
        Assert.Equal(TaskState.Failed, store.GetTask("task-failed")!.State);
        Assert.Equal(TaskState.Running, store.GetTask("task-interrupted")!.State);
        Assert.Contains(store.ListRecentTasks(10), t => t.TaskId == "task-interrupted" && t.State == TaskState.Running);
    }

    [Fact]
    public void CorruptRows_GarbageEnumsAndBadDate_DoNotFailLoads()
    {
        var db = Path.Combine(_root, "corrupt.db");
        using (var store = new MemoryStore(db))
        {
            store.SaveTask(new AgentTask("task-good", "Good task", Language.English, AgentMode.Ask)
            { State = TaskState.Completed });
        }

        // Corrupt the database directly, the way a crashed write or a
        // hand-edited file would: unknown enum strings in a task row and
        // an unparseable date in a conversation row.
        using (var conn = new SqliteConnection($"Data Source={db}"))
        {
            conn.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                INSERT INTO tasks (task_id, user_request, language, mode, state, final_report, created_at, updated_at)
                VALUES ('task-corrupt', 'Corrupt task', 'NotALanguage', 'NotAMode', 'NotAState', NULL, '2026-11-21T00:00:00Z', '2026-11-21T00:00:00Z');
                INSERT INTO conversations (id, role, content, created_at)
                VALUES ('conv-corrupt', 'user', 'hello corrupt date', 'not-a-date-at-all');
                """;
            cmd.ExecuteNonQuery();
        }

        using var reopened = new MemoryStore(db);
        var corrupt = reopened.GetTask("task-corrupt");
        Assert.NotNull(corrupt);
        Assert.Equal(Language.Unknown, corrupt!.Language);
        Assert.Equal(AgentMode.Ask, corrupt.Mode);
        Assert.Equal(TaskState.Created, corrupt.State);
        // The corrupt task must not break the recent-tasks list, and the
        // good task must still be there.
        var recent = reopened.ListRecentTasks(10);
        Assert.Contains(recent, t => t.TaskId == "task-corrupt");
        Assert.Contains(recent, t => t.TaskId == "task-good");
        // The conversation with the bad date still loads instead of
        // throwing; export (which reads every conversation) also works.
        var found = reopened.SearchConversations("corrupt date");
        Assert.Single(found);
        var export = Path.Combine(_root, "export.json");
        reopened.ExportJson(export);
        Assert.Contains("corrupt date", File.ReadAllText(export));
    }

    // ---- Overwrite approval gate --------------------------------------

    [Fact]
    public async Task WriteFile_ExistingFile_BlockedWithoutApproval_FileUnchanged()
    {
        var path = Path.Combine(_root, "existing.txt");
        File.WriteAllText(path, "original");
        var step = new PlanStep("s1", "Write the text", "filesystem", "Write the text to the file.")
        {
            Args = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["action"] = "write-file",
                ["path"] = path,
                ["text"] = "overwrite attempt",
                ["verify"] = "file:" + path,
            },
        };
        // No approval service: the engine must fail closed for a
        // SensitiveFileModification (existing file) step.
        using var engine = new AgentEngine(null, "qwen3:4b", new IAgentTool[] { new FilesystemTools() }, new PermissionManager(),
            planProvider: FixedPlan(step));
        var done = await engine.RunAsync(
            new AgentTask("t-overwrite", "Write to the file", Language.English, AgentMode.Autonomous), CancellationToken.None);

        Assert.Equal(StepStatus.NeedsApproval, done.Plan!.Steps[0].Status);
        Assert.Equal("original", File.ReadAllText(path));
    }

    // ---- Ollama unload / health check (mocked HTTP) --------------------

    [Fact]
    public async Task ModelManager_UnloadModel_UsesOllamaClient_NoSecondHttpClient()
    {
        string? seenPath = null;
        string? seenBody = null;
        var handler = new StubHandler(request =>
        {
            seenPath = request.RequestUri?.AbsolutePath;
            // Synchronous stream read (the content is an in-memory
            // StringContent); avoids blocking on a Task inside the test.
            seenBody = request.Content is null
                ? null
                : new StreamReader(request.Content.ReadAsStream()).ReadToEnd();
            return StubHandler.Json("""{"response":"OK","done":true}""");
        });
        using var client = new OllamaClient(handler);
        var manager = new ModelManager(client, _root);
        // SwitchModelAsync verifies via GenerateAsync, which the stub answers.
        Assert.True(await manager.SwitchModelAsync("qwen3:4b", CancellationToken.None));
        Assert.Equal("qwen3:4b", manager.CurrentModel);

        await manager.UnloadModelAsync("qwen3:4b", CancellationToken.None);
        Assert.Equal(string.Empty, manager.CurrentModel);
        Assert.Equal("/api/generate", seenPath);
        Assert.Contains("keep_alive", seenBody ?? string.Empty, StringComparison.Ordinal);
        Assert.Contains("0", seenBody ?? string.Empty, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ModelManager_HealthCheck_FalseWhenOllamaDown_TrueWhenUp()
    {
        var downHandler = new StubHandler(_ => throw new HttpRequestException("connection refused"));
        using var downClient = new OllamaClient(downHandler);
        var downManager = new ModelManager(downClient, _root);
        Assert.False(await downManager.HealthCheckAsync(CancellationToken.None));

        var upHandler = new StubHandler(request =>
            request.RequestUri?.AbsolutePath == "/api/tags"
                ? StubHandler.Json("""{"models":[{"name":"qwen3:4b"}]}""")
                : StubHandler.Json("""{"response":"OK","done":true}"""));
        using var upClient = new OllamaClient(upHandler);
        var upManager = new ModelManager(upClient, _root);
        Assert.True(await upManager.HealthCheckAsync(CancellationToken.None));
    }

    [Fact]
    public async Task OllamaClient_UnloadModel_ReturnsFalseOnHttpError()
    {
        var handler = new StubHandler(_ => StubHandler.Json("""{"error":"not found"}""", HttpStatusCode.NotFound));
        using var client = new OllamaClient(handler);
        Assert.False(await client.UnloadModelAsync("qwen3:4b", CancellationToken.None));
    }

    // ---- Unsafe paths ---------------------------------------------------

    [Fact]
    public async Task FilesystemTools_NullBytePath_Rejected_WithClearFailure()
    {
        var tool = new FilesystemTools();
        var result = await tool.ExecuteAsync(new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["action"] = "write-file",
            ["path"] = Path.Combine(_root, "bad\0file.txt"),
            ["text"] = "x",
        }, CancellationToken.None);
        Assert.False(result.Success);
        Assert.Contains("null", result.Summary, StringComparison.OrdinalIgnoreCase);
        Assert.False(File.Exists(Path.Combine(_root, "bad file.txt")));
    }

    [Fact]
    public async Task NotepadWorkflow_SaveAsync_NullBytePath_Rejected()
    {
        var result = await NotepadWorkflow.SaveAsync(new FakeAppControl(), Path.Combine(_root, "bad\0.txt"), "hello", CancellationToken.None);
        Assert.False(result.Success);
        Assert.Contains("null", result.Summary, StringComparison.OrdinalIgnoreCase);
    }
}
