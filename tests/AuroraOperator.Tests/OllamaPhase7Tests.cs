using System.Net;
using System.Text;
using AuroraOperator.Core.Ollama;

namespace AuroraOperator.Tests;

/// <summary>
/// Phase 7 Ollama tests (mocked HTTP only — no real server or model).
/// Covers the gaps left by OllamaMemoryTests / Phase3Tests: /api/pull with
/// progress, streaming + pull cancellation, /api/chat error handling,
/// unload success, and the ModelCatalog hardware profiles (verified tags
/// qwen3:0.6b / qwen3:1.7b / qwen3:4b / qwen3:8b, qwen3.5:2b, qwen2.5-coder:3b
/// against ollama.com/library, 2026-11-21 — see BUILD_AUDIT.md §7.3).
/// </summary>
public sealed class OllamaPhase7Tests
{
    /// <summary>Handler that honours the caller's CancellationToken, unlike
    /// StubHandler (which answers synchronously). Simulates a server that
    /// never answers until cancelled.</summary>
    private sealed class HangingHandler : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken ct)
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, ct).ConfigureAwait(false);
            return StubHandler.Json("{}");
        }
    }

    /// <summary>Synchronous IProgress: records inline. (Progress&lt;T&gt;
    /// posts callbacks to the captured SynchronizationContext, so in a
    /// context-free test the reports can land after the assertion.)</summary>
    private sealed class SyncProgress : IProgress<double>
    {
        public List<double> Seen { get; } = new();
        public void Report(double value) => Seen.Add(value);
    }

    [Fact]
    public async Task PullModelAsync_ReportsProgress_AndReturnsTrueOnSuccess()
    {
        var ndjson =
            "{\"status\":\"pulling manifest\"}\n" +
            "{\"status\":\"downloading\",\"total\":1000,\"completed\":250}\n" +
            "{\"status\":\"downloading\",\"total\":1000,\"completed\":1000}\n" +
            "{\"status\":\"success\"}\n";
        var handler = new StubHandler(_ => StubHandler.Json(ndjson));
        using var client = new OllamaClient(handler);
        var progress = new SyncProgress();
        Assert.True(await client.PullModelAsync("qwen3:4b", progress, CancellationToken.None));
        Assert.Contains(0.25, progress.Seen);
        Assert.Contains(1.0, progress.Seen);
    }

    [Fact]
    public async Task PullModelAsync_ReturnsFalse_OnHttpError()
    {
        var handler = new StubHandler(_ =>
            StubHandler.Json("""{"error":"model not found"}""", HttpStatusCode.NotFound));
        using var client = new OllamaClient(handler);
        Assert.False(await client.PullModelAsync("qwen3:does-not-exist", null, CancellationToken.None));
    }

    [Fact]
    public async Task PullModelAsync_ReturnsFalse_WhenStreamNeverReportsSuccess()
    {
        var ndjson = "{\"status\":\"pulling manifest\"}\n{\"status\":\"downloading\",\"total\":10,\"completed\":5}\n";
        var handler = new StubHandler(_ => StubHandler.Json(ndjson));
        using var client = new OllamaClient(handler);
        Assert.False(await client.PullModelAsync("qwen3:4b", null, CancellationToken.None));
    }

    [Fact]
    public async Task ChatStreamAsync_StopsWhenCancelled()
    {
        using var client = new OllamaClient(new HangingHandler());
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(200));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
        {
            await foreach (var _ in client.ChatStreamAsync("qwen3:4b",
                new[] { new ChatMessage("user", "hi") }, cts.Token))
            {
            }
        });
    }

    [Fact]
    public async Task PullModelAsync_StopsWhenCancelled()
    {
        using var client = new OllamaClient(new HangingHandler());
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(200));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            client.PullModelAsync("qwen3:4b", null, cts.Token));
    }

    [Fact]
    public async Task ChatAsync_Throws_OnServerError()
    {
        var handler = new StubHandler(_ =>
            StubHandler.Json("""{"error":"model not found"}""", HttpStatusCode.NotFound));
        using var client = new OllamaClient(handler);
        var ex = await Assert.ThrowsAsync<HttpRequestException>(() =>
            client.ChatAsync("qwen3:does-not-exist",
                new[] { new ChatMessage("user", "hi") }, CancellationToken.None));
        Assert.Contains("404", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task UnloadModelAsync_ReturnsTrue_OnSuccess()
    {
        string? seenPath = null;
        var handler = new StubHandler(request =>
        {
            seenPath = request.RequestUri?.AbsolutePath;
            return StubHandler.Json("""{"response":"","done":true}""");
        });
        using var client = new OllamaClient(handler);
        Assert.True(await client.UnloadModelAsync("qwen3:4b", CancellationToken.None));
        Assert.Equal("/api/generate", seenPath);
    }

    [Fact]
    public void ModelCatalog_All_ContainsOnlyVerifiedTags()
    {
        // Every tag below was confirmed to exist on ollama.com/library on
        // 2026-11-21 (BUILD_AUDIT.md §7.3). qwen3.5:2b — the suspect tag —
        // exists (multimodal, 2.7-3.1 GB); no tag needed correcting.
        var names = ModelCatalog.All.Select(m => m.Name).ToArray();
        Assert.Contains("qwen3:4b", names);
        Assert.Contains("qwen3:1.7b", names);
        Assert.Contains("qwen3:0.6b", names);
        Assert.Contains("qwen3:8b", names);
        Assert.Contains("qwen3.5:2b", names);
        Assert.Contains("qwen2.5-coder:3b", names);
        Assert.Equal("vision", ModelCatalog.All.First(m => m.Name == "qwen3.5:2b").Role);
        Assert.Equal("coding", ModelCatalog.All.First(m => m.Name == "qwen2.5-coder:3b").Role);
    }

    [Fact]
    public void ModelCatalog_HardwareProfiles_SelectVerifiedModelPerTier()
    {
        Assert.Equal("qwen3:1.7b", ModelCatalog.SelectProfile(8L * 1024 * 1024 * 1024).Model);
        Assert.Equal("qwen3:4b", ModelCatalog.SelectProfile(16L * 1024 * 1024 * 1024).Model);
        Assert.Equal("qwen3:8b", ModelCatalog.SelectProfile(32L * 1024 * 1024 * 1024).Model);
        // Phase 8: the Minimal tier (below 8 GB) selects qwen3:0.6b.
        Assert.Equal("qwen3:0.6b", ModelCatalog.SelectProfile(4L * 1024 * 1024 * 1024).Model);
        var advanced = ModelCatalog.Profiles.First(p => p.Name == "Advanced");
        Assert.Equal(8192, advanced.ContextTokens);
        // One model at a time: profiles never name two models.
        Assert.All(ModelCatalog.Profiles, p => Assert.Single(new[] { p.Model }));
    }
}
