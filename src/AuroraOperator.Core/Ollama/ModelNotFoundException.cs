namespace AuroraOperator.Core.Ollama;

/// <summary>
/// Ollama answered 404 with a body saying the requested model was not
/// installed (the real-world 2026-11-22 failure: the app asked for
/// qwen3:4b, the PC only had qwen3.5:4b, and /api/chat answered
/// 404 {"error":"model 'qwen3:4b' not found"}).
/// This is a distinct outcome, not "Ollama is down" and not a generic
/// failure: callers can fall back to an installed model or offer the
/// missing model for download. It extends
/// <see cref="HttpRequestException"/> so existing callers that catch the
/// generic type keep working; new code catches this type specifically.
/// </summary>
public sealed class ModelNotFoundException : HttpRequestException
{
    public ModelNotFoundException(string model, string message)
        : base(message)
    {
        Model = model;
    }

    public ModelNotFoundException(string model, string message, Exception? inner)
        : base(message, inner)
    {
        Model = model;
    }

    /// <summary>The model Ollama was asked for (the configured model, not
    /// a name parsed out of the error body — the body is only used to
    /// classify the failure).</summary>
    public string Model { get; }
}
