using System.Net.Http.Json;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;

namespace AuroraOperator.Core.Ollama;

/// <summary>One chat turn for the Ollama /api/chat endpoint.</summary>
public sealed record ChatMessage(string Role, string Content);

/// <summary>
/// Minimal client for the local Ollama HTTP API (http://localhost:11434 only).
/// The constructor rejects any non-loopback base URL, so the client can
/// never be pointed at a remote server. Supports /api/tags, /api/chat, /api/generate and /api/pull, with streaming
/// (NDJSON) chat, timeouts and clear errors. Never contacts any cloud API.
/// A custom HttpMessageHandler can be injected for tests.
/// </summary>
public sealed class OllamaClient : IDisposable
{
    public const string DefaultBaseUrl = "http://localhost:11434";
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };

    private readonly HttpClient _http;
    private readonly bool _ownsClient;
    public string BaseUrl { get; }

    public OllamaClient(HttpClient? http = null, string baseUrl = DefaultBaseUrl, TimeSpan? timeout = null)
    {
        BaseUrl = (baseUrl ?? DefaultBaseUrl).TrimEnd('/');
        ValidateLoopbackOnly(BaseUrl);
        _ownsClient = http is null;
        _http = http ?? new HttpClient { Timeout = timeout ?? TimeSpan.FromSeconds(120) };
        if (http is not null && timeout.HasValue) _http.Timeout = timeout.Value;
    }

    /// <summary>
    /// Enforces the localhost-only design (Phase 7 security review): the
    /// client talks to a local Ollama server and must never be pointed at
    /// a remote host, where prompts — the user's own text — would leave
    /// the machine. Only loopback hosts (localhost, 127.0.0.1, ::1) over
    /// http/https are accepted.
    /// </summary>
    public static void ValidateLoopbackOnly(string baseUrl)
    {
        if (!Uri.TryCreate(baseUrl, UriKind.Absolute, out var uri) ||
            (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        {
            throw new ArgumentException(
                $"Ollama base URL must be a loopback address (localhost, 127.0.0.1 or ::1); got '{baseUrl}'. " +
                "Aurora Operator only talks to a local Ollama server.", nameof(baseUrl));
        }
        var host = uri.Host.Trim('[', ']');
        var loopback = host.Equals("localhost", StringComparison.OrdinalIgnoreCase) ||
            (System.Net.IPAddress.TryParse(host, out var ip) && System.Net.IPAddress.IsLoopback(ip));
        if (!loopback)
        {
            throw new ArgumentException(
                $"Ollama base URL must be a loopback address (localhost, 127.0.0.1 or ::1); got '{baseUrl}'. " +
                "Aurora Operator only talks to a local Ollama server.", nameof(baseUrl));
        }
    }

    public OllamaClient(HttpMessageHandler handler, string baseUrl = DefaultBaseUrl)
        : this(new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(120) }, baseUrl) { }

    /// <summary>True when the local Ollama service answers GET /api/tags.
    /// Single attempt on purpose: this is a health probe, and retrying it
    /// would only delay reporting that Ollama is down. Retries live in
    /// the request methods below (<see cref="WithRetryAsync{T}"/>).</summary>
    public async Task<bool> IsAvailableAsync(CancellationToken ct)
    {
        try
        {
            using var resp = await _http.GetAsync(BaseUrl + "/api/tags", ct).ConfigureAwait(false);
            return resp.IsSuccessStatusCode;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            return false;
        }
    }

    /// <summary>Marker for a failure worth retrying: a 5xx response.
    /// 4xx responses are permanent (bad model name, bad payload) and are
    /// never retried — they use <see cref="PermanentOllamaException"/>.
    /// The markers are converted back to a plain
    /// <see cref="HttpRequestException"/> at the retry boundary, so
    /// callers always see the exception type they expect.</summary>
    private sealed class TransientOllamaException(string message) : Exception(message);

    /// <summary>Marker for a permanent failure (4xx). Never retried.</summary>
    private sealed class PermanentOllamaException(string message) : Exception(message);

    /// <summary>
    /// Bounded retry (Phase 8) for idempotent Ollama requests: at most 3
    /// attempts total, 250 ms then 500 ms backoff. Retried only on
    /// transient failures — 5xx responses, network errors, and timeouts.
    /// 4xx responses, user cancellation, and <see cref="OperationCanceledException"/>
    /// from the caller's token are never retried. Streaming calls
    /// (<see cref="ChatStreamAsync"/>, <see cref="PullModelAsync"/>) do
    /// not use this: a partially-consumed response stream cannot be
    /// replayed, so a mid-stream failure is reported as-is.
    /// </summary>
    private async Task<T> WithRetryAsync<T>(Func<CancellationToken, Task<T>> operation, CancellationToken ct)
    {
        var delays = new[] { TimeSpan.FromMilliseconds(250), TimeSpan.FromMilliseconds(500) };
        for (var attempt = 0; ; attempt++)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                return await operation(ct).ConfigureAwait(false);
            }
            catch (PermanentOllamaException ex)
            {
                // Permanent (4xx): never retried; surface as the plain
                // HttpRequestException callers expect.
                throw new HttpRequestException(ex.Message, ex);
            }
            catch (TransientOllamaException) when (attempt < delays.Length)
            {
                await Task.Delay(delays[attempt], ct).ConfigureAwait(false);
            }
            catch (TransientOllamaException ex)
            {
                // Retries exhausted: same public exception type as a 4xx.
                throw new HttpRequestException(ex.Message, ex);
            }
            catch (Exception ex) when (attempt < delays.Length && IsTransientNetwork(ex, ct))
            {
                await Task.Delay(delays[attempt], ct).ConfigureAwait(false);
            }
        }
    }

    /// <summary>Network-level transient failures only. A 4xx response is
    /// never a network failure: the operation reports it with
    /// <see cref="PermanentOllamaException"/> instead, so it cannot be
    /// mistaken for a retryable error here.</summary>
    private static bool IsTransientNetwork(Exception ex, CancellationToken ct)
    {
        if (ct.IsCancellationRequested) return false;
        // A timeout surfaces as TaskCanceledException without the caller's
        // token being cancelled; a user cancel surfaces the same way but
        // with the token cancelled (checked above), so it is not retried.
        if (ex is TaskCanceledException) return true;
        return ex is HttpRequestException;
    }

    public async Task<IReadOnlyList<string>> ListModelsAsync(CancellationToken ct)
    {
        return await WithRetryAsync(async token =>
        {
            using var resp = await _http.GetAsync(BaseUrl + "/api/tags", token).ConfigureAwait(false);
            if ((int)resp.StatusCode >= 500)
                throw new TransientOllamaException($"Ollama /api/tags failed ({(int)resp.StatusCode}).");
            if (!resp.IsSuccessStatusCode)
                throw new PermanentOllamaException($"Ollama /api/tags failed ({(int)resp.StatusCode}).");
            var doc = await resp.Content.ReadFromJsonAsync<JsonDocument>(JsonOptions, token).ConfigureAwait(false);
            var names = new List<string>();
            if (doc?.RootElement.TryGetProperty("models", out var models) == true)
                foreach (var m in models.EnumerateArray())
                    if (m.TryGetProperty("name", out var n) && n.GetString() is string s)
                        names.Add(s);
            return (IReadOnlyList<string>)names;
        }, ct).ConfigureAwait(false);
    }

    /// <summary>Non-streaming chat. Returns the assistant's full reply text.
    /// 5xx responses are retried (bounded, see <see cref="WithRetryAsync{T}"/>);
    /// 4xx responses fail immediately.</summary>
    public async Task<string> ChatAsync(string model, IEnumerable<ChatMessage> messages, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(model);
        var payload = new
        {
            model,
            messages = messages.Select(m => new { role = m.Role, content = m.Content }).ToArray(),
            stream = false,
        };
        var body = await WithRetryAsync(async token =>
        {
            using var resp = await _http.PostAsJsonAsync(BaseUrl + "/api/chat", payload, JsonOptions, token).ConfigureAwait(false);
            var b = await resp.Content.ReadAsStringAsync(token).ConfigureAwait(false);
            if ((int)resp.StatusCode >= 500)
                throw new TransientOllamaException($"Ollama /api/chat failed ({(int)resp.StatusCode}): {b}");
            if (!resp.IsSuccessStatusCode)
                throw new PermanentOllamaException($"Ollama /api/chat failed ({(int)resp.StatusCode}): {b}");
            return b;
        }, ct).ConfigureAwait(false);
        using var doc = JsonDocument.Parse(body);
        if (doc.RootElement.TryGetProperty("message", out var msg) &&
            msg.TryGetProperty("content", out var content))
            return content.GetString() ?? string.Empty;
        if (doc.RootElement.TryGetProperty("error", out var err))
            throw new HttpRequestException("Ollama error: " + err.GetString());
        return string.Empty;
    }

    /// <summary>Non-streaming single-prompt generation (/api/generate).
    /// 5xx responses are retried (bounded); 4xx fail immediately.</summary>
    public async Task<string> GenerateAsync(string model, string prompt, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(model);
        var payload = new { model, prompt, stream = false };
        var body = await WithRetryAsync(async token =>
        {
            using var resp = await _http.PostAsJsonAsync(BaseUrl + "/api/generate", payload, JsonOptions, token).ConfigureAwait(false);
            var b = await resp.Content.ReadAsStringAsync(token).ConfigureAwait(false);
            if ((int)resp.StatusCode >= 500)
                throw new TransientOllamaException($"Ollama /api/generate failed ({(int)resp.StatusCode}): {b}");
            if (!resp.IsSuccessStatusCode)
                throw new PermanentOllamaException($"Ollama /api/generate failed ({(int)resp.StatusCode}): {b}");
            return b;
        }, ct).ConfigureAwait(false);
        using var doc = JsonDocument.Parse(body);
        return doc.RootElement.TryGetProperty("response", out var r) ? r.GetString() ?? string.Empty : string.Empty;
    }

    /// <summary>Streaming chat: yields each content delta as it arrives (NDJSON lines).</summary>
    public async IAsyncEnumerable<string> ChatStreamAsync(string model, IEnumerable<ChatMessage> messages, [EnumeratorCancellation] CancellationToken ct)
    {
        var payload = new
        {
            model,
            messages = messages.Select(m => new { role = m.Role, content = m.Content }).ToArray(),
            stream = true,
        };
        using var request = new HttpRequestMessage(HttpMethod.Post, BaseUrl + "/api/chat")
        {
            Content = JsonContent.Create(payload, options: JsonOptions),
        };
        using var resp = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        resp.EnsureSuccessStatusCode();
        await using var stream = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        using var reader = new StreamReader(stream, Encoding.UTF8);
        while (await reader.ReadLineAsync(ct).ConfigureAwait(false) is string line)
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            string? delta = null;
            try
            {
                using var doc = JsonDocument.Parse(line);
                if (doc.RootElement.TryGetProperty("message", out var msg) &&
                    msg.TryGetProperty("content", out var content))
                    delta = content.GetString();
            }
            catch (JsonException) { continue; }
            if (!string.IsNullOrEmpty(delta)) yield return delta;
        }
    }

    /// <summary>Pulls a model, reporting fractional progress (0..1) when Ollama reports totals.</summary>
    public async Task<bool> PullModelAsync(string model, IProgress<double>? progress, CancellationToken ct)
    {
        var payload = new { name = model, stream = true };
        using var request = new HttpRequestMessage(HttpMethod.Post, BaseUrl + "/api/pull")
        {
            Content = JsonContent.Create(payload, options: JsonOptions),
        };
        using var resp = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        if (!resp.IsSuccessStatusCode) return false;
        await using var stream = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        using var reader = new StreamReader(stream, Encoding.UTF8);
        var ok = false;
        while (await reader.ReadLineAsync(ct).ConfigureAwait(false) is string line)
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            try
            {
                using var doc = JsonDocument.Parse(line);
                if (doc.RootElement.TryGetProperty("status", out var st) &&
                    st.GetString() is string s && s.Contains("success", StringComparison.OrdinalIgnoreCase))
                    ok = true;
                if (progress is not null &&
                    doc.RootElement.TryGetProperty("total", out var total) &&
                    doc.RootElement.TryGetProperty("completed", out var completed) &&
                    total.TryGetInt64(out var t) && t > 0 && completed.TryGetInt64(out var c))
                    progress.Report(Math.Clamp((double)c / t, 0, 1));
            }
            catch (JsonException) { /* ignore malformed progress lines */ }
        }
        return ok;
    }

    /// <summary>
    /// Unloads a model from Ollama's memory (POST /api/generate with
    /// keep_alive=0). Uses this client's own <see cref="HttpClient"/> so
    /// callers never need a second one. Returns false (never throws for an
    /// HTTP failure) when Ollama does not answer successfully.
    /// </summary>
    public async Task<bool> UnloadModelAsync(string name, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        try
        {
            var payload = new { model = name, prompt = string.Empty, stream = false, keep_alive = 0 };
            using var resp = await _http.PostAsJsonAsync(BaseUrl + "/api/generate", payload, JsonOptions, ct).ConfigureAwait(false);
            return resp.IsSuccessStatusCode;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            return false;
        }
    }

    public void Dispose()
    {
        if (_ownsClient) _http.Dispose();
    }
}
