using System.Net;
using System.Text.Json;
using AuroraOperator.Core;
using AuroraOperator.Core.Agent;
using AuroraOperator.Core.Ollama;
using AuroraOperator.Core.Security;
using AuroraOperator.Core.Tools;

namespace AuroraOperator.Tests;

/// <summary>
/// Model-not-found fix (2026-11-22): the user's PC had qwen3.5:4b
/// installed, not qwen3:4b, and /api/chat answered
/// 404 {"error":"model 'qwen3:4b' not found"}. The app silently fell back
/// to a local summary. These tests pin the fix: the 404 is classified as
/// a distinct ModelNotFound outcome (never "Ollama is down"), the engine
/// falls back to an installed model transparently and says so, reports
/// clearly when nothing is installed, and Model Manager merges installed
/// models (including non-catalog ones) with the catalog. All HTTP is
/// mocked; no real server or model is needed.
/// </summary>
public sealed class ModelNotFoundTests
{
    /// <summary>Routes like a minimal Ollama: /api/tags lists the
    /// installed models; /api/chat answers 404 "model not found" for any
    /// model that is not installed and a canned reply for installed ones.</summary>
    private sealed class RoutingHandler : HttpMessageHandler
    {
        private readonly IReadOnlyList<string> _installed;
        public RoutingHandler(IReadOnlyList<string> installed) => _installed = installed;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var path = request.RequestUri?.AbsolutePath ?? string.Empty;
            if (path == "/api/tags")
            {
                var models = string.Join(",", _installed.Select(m => "{\"name\":\"" + m + "\"}"));
                return Task.FromResult(StubHandler.Json("{\"models\":[" + models + "]}"));
            }
            if (path == "/api/chat")
            {
                var body = request.Content?.ReadAsStringAsync(ct).GetAwaiter().GetResult() ?? string.Empty;
                using var doc = JsonDocument.Parse(body);
                var model = doc.RootElement.GetProperty("model").GetString() ?? string.Empty;
                if (_installed.Contains(model, StringComparer.OrdinalIgnoreCase))
                {
                    return Task.FromResult(StubHandler.Json(
                        "{\"message\":{\"role\":\"assistant\",\"content\":\"Hello from " + model + "\"},\"done\":true}"));
                }
                return Task.FromResult(StubHandler.Json(
                    $"{{\"error\":\"model '{model}' not found\"}}", HttpStatusCode.NotFound));
            }
            return Task.FromResult(StubHandler.Json("{}"));
        }
    }

    [Fact]
    public async Task ChatAsync_ThrowsModelNotFoundException_On404ModelNotFound()
    {
        var handler = new StubHandler(_ =>
            StubHandler.Json("""{"error":"model 'qwen3:4b' not found"}""", HttpStatusCode.NotFound));
        using var client = new OllamaClient(handler);
        var ex = await Assert.ThrowsAsync<ModelNotFoundException>(() =>
            client.ChatAsync("qwen3:4b",
                new[] { new ChatMessage("user", "hi") }, CancellationToken.None));
        Assert.Equal("qwen3:4b", ex.Model);
        Assert.Contains("404", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task GenerateAsync_ThrowsModelNotFoundException_On404ModelNotFound()
    {
        var handler = new StubHandler(_ =>
            StubHandler.Json("""{"error":"model 'qwen3:4b' not found"}""", HttpStatusCode.NotFound));
        using var client = new OllamaClient(handler);
        var ex = await Assert.ThrowsAsync<ModelNotFoundException>(() =>
            client.GenerateAsync("qwen3:4b", "Reply OK", CancellationToken.None));
        Assert.Equal("qwen3:4b", ex.Model);
    }

    [Fact]
    public async Task ChatStreamAsync_ThrowsModelNotFoundException_On404ModelNotFound()
    {
        var handler = new StubHandler(_ =>
            StubHandler.Json("""{"error":"model 'qwen3:4b' not found"}""", HttpStatusCode.NotFound));
        using var client = new OllamaClient(handler);
        await Assert.ThrowsAsync<ModelNotFoundException>(async () =>
        {
            await foreach (var _ in client.ChatStreamAsync("qwen3:4b",
                new[] { new ChatMessage("user", "hi") }, CancellationToken.None))
            {
            }
        });
    }

    [Fact]
    public async Task ChatAsync_ThrowsPlainHttpRequestException_On404WithoutModelError()
    {
        // A 404 that does NOT say a model is missing must not be
        // classified as ModelNotFound: it must never trigger a fallback.
        // ThrowsAsync requires the exact type, so a ModelNotFoundException
        // (a subclass) would fail this test.
        var handler = new StubHandler(_ =>
            StubHandler.Json("""{"error":"page not found"}""", HttpStatusCode.NotFound));
        using var client = new OllamaClient(handler);
        var ex = await Assert.ThrowsAsync<HttpRequestException>(() =>
            client.ChatAsync("qwen3:4b",
                new[] { new ChatMessage("user", "hi") }, CancellationToken.None));
        Assert.Contains("404", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void SelectFallback_PrefersCatalogModel_WhenInstalled()
    {
        // qwen3:1.7b (catalog) beats qwen3.5:4b (installed, non-catalog).
        Assert.Equal("qwen3:1.7b",
            ModelFallback.SelectFallbackModel("qwen3:4b", new[] { "qwen3.5:4b", "qwen3:1.7b" }));
    }

    [Fact]
    public void SelectFallback_PicksNonCatalogModel_WhenNoCatalogModelInstalled()
    {
        // The user's real case: only qwen3.5:4b is installed.
        Assert.Equal("qwen3.5:4b",
            ModelFallback.SelectFallbackModel("qwen3:4b", new[] { "qwen3.5:4b" }));
    }

    [Fact]
    public void SelectFallback_ReturnsNull_WhenNothingInstalled()
    {
        Assert.Null(ModelFallback.SelectFallbackModel("qwen3:4b", Array.Empty<string>()));
    }

    [Fact]
    public void SelectFallback_SkipsConfiguredModel()
    {
        Assert.Equal("qwen3:4b",
            ModelFallback.SelectFallbackModel("qwen3:8b", new[] { "qwen3:4b", "qwen3:0.6b" }));
    }

    [Fact]
    public void SelectFallback_PrefersQwenFamily_AmongNonCatalogModels()
    {
        Assert.Equal("qwen2.5:7b",
            ModelFallback.SelectFallbackModel("qwen3:4b", new[] { "llama3:8b", "qwen2.5:7b" }));
    }

    [Fact]
    public void MergeAvailableModels_MarksNonCatalogInstalledModels()
    {
        var merged = ModelFallback.MergeAvailableModels(new[] { "qwen3.5:4b", "qwen3:4b" });
        var nonCatalog = merged.First(m => m.Name == "qwen3.5:4b");
        Assert.True(nonCatalog.IsInstalled);
        Assert.False(nonCatalog.IsInCatalog);
        Assert.Contains("not in catalog", nonCatalog.DisplayLabel, StringComparison.OrdinalIgnoreCase);
        var catalog = merged.First(m => m.Name == "qwen3:4b");
        Assert.True(catalog.IsInstalled);
        Assert.True(catalog.IsInCatalog);
        var missing = merged.First(m => m.Name == "qwen3:1.7b");
        Assert.False(missing.IsInstalled);
        Assert.True(missing.IsInCatalog);
    }

    [Fact]
    public void MergeAvailableModels_EmptyInstalled_ShowsCatalogNotInstalled()
    {
        var merged = ModelFallback.MergeAvailableModels(Array.Empty<string>());
        Assert.Equal(ModelCatalog.All.Count, merged.Count);
        Assert.All(merged, m => Assert.False(m.IsInstalled));
        Assert.All(merged, m => Assert.True(m.IsInCatalog));
    }

    [Fact]
    public async Task ModelManager_GetAvailableModelsAsync_MergesInstalledAndCatalog()
    {
        var handler = new StubHandler(_ =>
            StubHandler.Json("""{"models":[{"name":"qwen3.5:4b"}]}"""));
        using var client = new OllamaClient(handler);
        var manager = new ModelManager(client, Path.GetTempPath());
        var models = await manager.GetAvailableModelsAsync(CancellationToken.None);
        Assert.Contains(models, m => m.Name == "qwen3.5:4b" && m.IsInstalled && !m.IsInCatalog);
        Assert.Contains(models, m => m.Name == "qwen3:4b" && !m.IsInstalled && m.IsInCatalog);
    }

    [Fact]
    public async Task ModelManager_ResolveFallbackModelAsync_PicksInstalledModel()
    {
        var handler = new StubHandler(_ =>
            StubHandler.Json("""{"models":[{"name":"qwen3.5:4b"}]}"""));
        using var client = new OllamaClient(handler);
        var manager = new ModelManager(client, Path.GetTempPath());
        Assert.Equal("qwen3.5:4b",
            await manager.ResolveFallbackModelAsync("qwen3:4b", CancellationToken.None));
    }

    [Fact]
    public async Task ModelManager_ResolveFallbackModelAsync_ReturnsNull_WhenNothingInstalled()
    {
        var handler = new StubHandler(_ => StubHandler.Json("""{"models":[]}"""));
        using var client = new OllamaClient(handler);
        var manager = new ModelManager(client, Path.GetTempPath());
        Assert.Null(await manager.ResolveFallbackModelAsync("qwen3:4b", CancellationToken.None));
    }

    [Fact]
    public async Task Engine_Ask_FallsBackToInstalledModel_WhenConfiguredModelMissing()
    {
        using var client = new OllamaClient(new RoutingHandler(new[] { "qwen3.5:4b" }));
        var engine = new AgentEngine(client, "qwen3:4b",
            new IAgentTool[] { new FilesystemTools() }, new PermissionManager());
        var task = new AgentTask("t-fallback", "hi", Language.English, AgentMode.Ask);
        var result = await engine.RunAsync(task, CancellationToken.None);
        Assert.Equal(TaskState.Completed, result.State);
        Assert.Contains("Hello from qwen3.5:4b", result.FinalReport);
        // The reply says plainly which model was used and why.
        Assert.Contains("qwen3:4b", result.FinalReport);
        Assert.Contains("not installed", result.FinalReport, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("ollama pull qwen3:4b", result.FinalReport);
        Assert.Equal("qwen3.5:4b", engine.SessionFallbackModel);
        Assert.Equal("qwen3:4b", engine.LastMissingModel);
    }

    [Fact]
    public async Task Engine_Ask_ReportsNoModelInstalled_WhenNothingInstalled()
    {
        using var client = new OllamaClient(new RoutingHandler(Array.Empty<string>()));
        var engine = new AgentEngine(client, "qwen3:4b",
            new IAgentTool[] { new FilesystemTools() }, new PermissionManager());
        var task = new AgentTask("t-nomodel", "hi", Language.English, AgentMode.Ask);
        var result = await engine.RunAsync(task, CancellationToken.None);
        Assert.Equal(TaskState.Completed, result.State);
        Assert.Contains("no model is installed", result.FinalReport, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("ollama pull qwen3:4b", result.FinalReport);
        Assert.Null(engine.SessionFallbackModel);
    }

    [Fact]
    public async Task Engine_Ask_UsesConfiguredModel_WhenPresent()
    {
        using var client = new OllamaClient(new RoutingHandler(new[] { "qwen3:4b" }));
        var engine = new AgentEngine(client, "qwen3:4b",
            new IAgentTool[] { new FilesystemTools() }, new PermissionManager());
        var task = new AgentTask("t-present", "hi", Language.English, AgentMode.Ask);
        var result = await engine.RunAsync(task, CancellationToken.None);
        Assert.Equal(TaskState.Completed, result.State);
        Assert.Contains("Hello from qwen3:4b", result.FinalReport);
        Assert.DoesNotContain("not installed", result.FinalReport, StringComparison.OrdinalIgnoreCase);
        Assert.Null(engine.SessionFallbackModel);
    }
}
