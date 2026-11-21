using AuroraOperator.Core;
using AuroraOperator.Core.Agent;
using AuroraOperator.Core.Ollama;
using AuroraOperator.Core.Security;

namespace AuroraOperator.Tests;

public sealed class LiveOllamaFactAttribute : FactAttribute
{
    public LiveOllamaFactAttribute()
    {
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("AURORA_LIVE_MODEL")))
            Skip = "Set AURORA_LIVE_MODEL to an installed Ollama model for this live test.";
    }
}

public sealed class LiveOllamaTests
{
    [LiveOllamaFact]
    public async Task LocalModelProposesARealVerifiedFileCreation()
    {
        var path = Directory.CreateTempSubdirectory("aurora-live-").FullName;
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(3));
            using var ollama = new OllamaClient(baseUrl: "http://127.0.0.1:11434");
            string model = Environment.GetEnvironmentVariable("AURORA_LIVE_MODEL")!;
            Assert.Contains(model, await ollama.ListModelsAsync(timeout.Token));
            var approval = new FixtureApproval(path);
            using var engine = new AgentEngine(ollama, model, [], new PermissionManager(),
                approvalService: approval, useWorkspacePlanner: true);
            var task = new AgentTask(Guid.NewGuid().ToString("N"),
                "Create a new UTF-8 text file named rc-smoke.txt in the workspace root. Put exactly AURORA_RC_TEST in it.",
                Language.English, AgentMode.Plan) { WorkspacePath = path };
            await engine.RunAsync(task, timeout.Token);
            Assert.True(task.State == TaskState.WaitingApproval, task.FinalReport);
            Assert.Empty(Directory.GetFiles(path));
            await engine.RunReviewedAsync(task, timeout.Token);
            Assert.True(task.State == TaskState.Completed, task.FinalReport);
            Assert.Equal("AURORA_RC_TEST", File.ReadAllText(Assert.Single(task.Artifacts)).TrimEnd());
        }
        finally { Directory.Delete(path, true); }
    }

    [LiveOllamaFact]
    public async Task LocalChatStreamsContentAndSupportsCancellation()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(3));
        using var ollama = new OllamaClient(baseUrl: "http://127.0.0.1:11434");
        string model = Environment.GetEnvironmentVariable("AURORA_LIVE_MODEL")!;
        var answer = new System.Text.StringBuilder();
        await foreach (string chunk in ollama.ChatStreamAsync(model,
            [new("user", "Respond with the word hello.")], timeout.Token))
            answer.Append(chunk);
        Assert.Contains("hello", answer.ToString(), StringComparison.OrdinalIgnoreCase);

        using var stopped = CancellationTokenSource.CreateLinkedTokenSource(timeout.Token);
        await using var stream = ollama.ChatStreamAsync(model,
            [new("user", "Count slowly from one to ten thousand in words.")], stopped.Token).GetAsyncEnumerator();
        Assert.True(await stream.MoveNextAsync());
        stopped.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => { await stream.MoveNextAsync(); });
    }

    private sealed class FixtureApproval(string root) : IApprovalService
    {
        public event EventHandler? PendingChanged { add { } remove { } }
        public IReadOnlyList<ApprovalRequest> Pending => [];
        public void CancelPending(string taskId) { }
        public Task<ApprovalOutcome> RequestApprovalAsync(ApprovalRequest request, CancellationToken ct) =>
            Task.FromResult(request.Title == "write-new-text" &&
                request.Description.Contains(Path.Combine(root, "rc-smoke.txt"), StringComparison.Ordinal) &&
                request.Description.Contains("AURORA_RC_TEST", StringComparison.Ordinal)
                ? ApprovalOutcome.Approved : ApprovalOutcome.Rejected);
    }
}
