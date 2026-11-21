using System.Net;
using System.Text;
using AuroraOperator.Core;
using AuroraOperator.Core.Memory;
using AuroraOperator.Core.Ollama;

namespace AuroraOperator.Tests;

/// <summary>Mocked HTTP handler so Ollama client tests never need a real server or model.</summary>
internal sealed class StubHandler : HttpMessageHandler
{
    private readonly Func<HttpRequestMessage, HttpResponseMessage> _respond;
    public StubHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) => _respond = respond;
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
        Task.FromResult(_respond(request));

    public static HttpResponseMessage Json(string json, HttpStatusCode code = HttpStatusCode.OK) =>
        new(code) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
}

public sealed class OllamaClientTests
{
    [Fact]
    public async Task ListModels_ParsesTagsResponse()
    {
        var handler = new StubHandler(_ => StubHandler.Json("""{"models":[{"name":"qwen3:4b"},{"name":"qwen3:1.7b"}]}"""));
        using var client = new OllamaClient(handler);
        var models = await client.ListModelsAsync(CancellationToken.None);
        Assert.Equal(new[] { "qwen3:4b", "qwen3:1.7b" }, models);
    }

    [Fact]
    public async Task ChatAsync_ReturnsAssistantContent()
    {
        var handler = new StubHandler(_ => StubHandler.Json("""{"message":{"role":"assistant","content":"Hello World"},"done":true}"""));
        using var client = new OllamaClient(handler);
        var reply = await client.ChatAsync("qwen3:4b",
            new[] { new ChatMessage("user", "Say Hello World") }, CancellationToken.None);
        Assert.Equal("Hello World", reply);
    }

    [Fact]
    public async Task GenerateAsync_ReturnsResponseField()
    {
        var handler = new StubHandler(_ => StubHandler.Json("""{"response":"OK","done":true}"""));
        using var client = new OllamaClient(handler);
        Assert.Equal("OK", await client.GenerateAsync("qwen3:4b", "Reply OK", CancellationToken.None));
    }

    [Fact]
    public async Task IsAvailable_ReturnsFalse_WhenServerUnreachable()
    {
        var handler = new StubHandler(_ => throw new HttpRequestException("connection refused"));
        using var client = new OllamaClient(handler);
        Assert.False(await client.IsAvailableAsync(CancellationToken.None));
    }

    [Fact]
    public async Task ChatStreamAsync_YieldsDeltas()
    {
        var ndjson = "{\"message\":{\"role\":\"assistant\",\"content\":\"Hel\"},\"done\":false}\n" +
                     "{\"message\":{\"role\":\"assistant\",\"content\":\"lo\"},\"done\":true}\n";
        var handler = new StubHandler(_ => StubHandler.Json(ndjson));
        using var client = new OllamaClient(handler);
        var parts = new List<string>();
        await foreach (var part in client.ChatStreamAsync("qwen3:4b",
            new[] { new ChatMessage("user", "hi") }, CancellationToken.None))
            parts.Add(part);
        Assert.Equal("Hello", string.Concat(parts));
    }
}

public sealed class MemoryStoreTests : IDisposable
{
    private readonly string _db = Path.Combine(Path.GetTempPath(), "aurora-mem-" + Guid.NewGuid().ToString("N") + ".db");
    private readonly MemoryStore _store;
    public MemoryStoreTests() => _store = new MemoryStore(_db);
    public void Dispose() { _store.Dispose(); try { File.Delete(_db); } catch { } }

    [Fact]
    public void Conversation_Save_Search_Delete_RoundTrips()
    {
        var id = _store.SaveConversation("user", "Create a folder named Business Reports");
        var found = _store.SearchConversations("Business Reports");
        Assert.Contains(found, e => e.Id == id);
        Assert.True(_store.DeleteConversation(id));
        Assert.DoesNotContain(_store.SearchConversations("Business Reports"), e => e.Id == id);
    }

    [Fact]
    public void Preference_SecretKeys_AreNeverStored()
    {
        _store.SetPreference("api_token", "should-not-persist");
        Assert.Null(_store.GetPreference("api_token"));
        _store.SetPreference("theme", "dark");
        Assert.Equal("dark", _store.GetPreference("theme"));
    }

    [Fact]
    public void Task_Save_Get_List_RoundTrips()
    {
        var task = new AgentTask("task-1", "Find PDF files in Downloads", Language.English, AgentMode.Ask)
        { State = TaskState.Completed, FinalReport = "Found 3 PDFs." };
        _store.SaveTask(task);
        var loaded = _store.GetTask("task-1");
        Assert.NotNull(loaded);
        Assert.Equal(TaskState.Completed, loaded!.State);
        Assert.Equal("Found 3 PDFs.", loaded.FinalReport);
        Assert.Contains(_store.ListRecentTasks(5), t => t.TaskId == "task-1");
    }

    [Fact]
    public void ExportJson_WritesFile()
    {
        _store.SaveConversation("user", "hello");
        var path = Path.Combine(Path.GetTempPath(), "aurora-export-" + Guid.NewGuid().ToString("N") + ".json");
        try
        {
            _store.ExportJson(path);
            Assert.True(File.Exists(path));
            Assert.Contains("hello", File.ReadAllText(path));
        }
        finally { try { File.Delete(path); } catch { } }
    }
}
