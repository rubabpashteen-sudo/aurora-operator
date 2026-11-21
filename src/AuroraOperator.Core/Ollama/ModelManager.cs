namespace AuroraOperator.Core.Ollama;

/// <summary>
/// Manages local Ollama models: hardware-based selection, download with
/// verification, switching and unloading. Only one model is kept loaded at a
/// time; the 8 GB baseline uses a conservative 8K context window.
/// </summary>
public sealed class ModelManager
{
    private readonly OllamaClient _client;
    public string ModelsDirectory { get; }
    public string CurrentModel { get; private set; } = string.Empty;
    public int ContextLength { get; set; } = 8192;

    public ModelManager(OllamaClient client, string modelsDir)
    {
        _client = client ?? throw new ArgumentNullException(nameof(client));
        ModelsDirectory = string.IsNullOrWhiteSpace(modelsDir)
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".ollama", "models")
            : modelsDir;
    }

    /// <summary>Selects the best model for this machine, downloads it if
    /// missing, verifies it answers, and makes it current.</summary>
    public async Task<ModelInfo> EnsureBestModelAsync(IProgress<string>? progress, CancellationToken ct)
    {
        var hw = HardwareInfo.Detect();
        var name = ModelCatalog.SelectBestModel(hw.TotalRamBytes, hw.AvailableRamBytes);
        // The profile's context window is the one for this tier (Phase 8:
        // Minimal tier uses 4K, the rest 8K). Previously the 8192 default
        // was used on every machine, including sub-8 GB ones.
        ContextLength = ModelCatalog.SelectProfile(hw.TotalRamBytes).ContextTokens;
        var info = ModelCatalog.All.First(m => m.Name == name);
        progress?.Report($"Hardware: {hw.TotalRamBytes / 1024 / 1024 / 1024} GB RAM total, " +
                         $"{hw.AvailableRamBytes / 1024 / 1024 / 1024} GB free. Selected model: {name}.");
        if (!await IsModelPresentAsync(name, ct).ConfigureAwait(false))
        {
            progress?.Report($"Downloading {name} (~{info.ApproxSizeBytes / 1_000_000} MB)…");
            var downloaded = await DownloadModelAsync(name, null, ct).ConfigureAwait(false);
            if (!downloaded) throw new InvalidOperationException($"Model download failed: {name}");
        }
        await SwitchModelAsync(name, ct).ConfigureAwait(false);
        progress?.Report($"Model ready: {name} (context {ContextLength} tokens).");
        return info;
    }

    public async Task<bool> IsModelPresentAsync(string name, CancellationToken ct)
    {
        var models = await _client.ListModelsAsync(ct).ConfigureAwait(false);
        return models.Any(m => string.Equals(m, name, StringComparison.OrdinalIgnoreCase) ||
                               m.StartsWith(name + ":", StringComparison.OrdinalIgnoreCase));
    }

    public async Task<bool> DownloadModelAsync(string name, IProgress<double>? progress, CancellationToken ct)
    {
        var ok = await _client.PullModelAsync(name, progress, ct).ConfigureAwait(false);
        if (!ok) return false;
        return await IsModelPresentAsync(name, ct).ConfigureAwait(false);
    }

    /// <summary>Switches the current model, verifying it generates a reply.</summary>
    public async Task<bool> SwitchModelAsync(string name, CancellationToken ct)
    {
        try
        {
            var reply = await _client.GenerateAsync(name, "Reply with the single word OK.", ct).ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(reply)) return false;
            CurrentModel = name;
            LocalLogger.Info($"Model switched to {name} (verified response).");
            return true;
        }
        catch (Exception ex)
        {
            LocalLogger.Warn($"Model switch to {name} failed: {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// Unloads a model from Ollama's memory (keep_alive=0). Delegates to
    /// <see cref="OllamaClient.UnloadModelAsync"/> so no second
    /// <see cref="HttpClient"/> is created here (a per-call client was the
    /// previous implementation; it was disposed, but it bypassed the
    /// injected client and its configuration).
    /// </summary>
    public async Task UnloadModelAsync(string name, CancellationToken ct)
    {
        try
        {
            // The unload request itself is best-effort: Ollama answers or
            // it does not, and the model is considered unloaded either way,
            // matching the previous behaviour (which ignored the response).
            _ = await _client.UnloadModelAsync(name, ct).ConfigureAwait(false);
            if (CurrentModel == name) CurrentModel = string.Empty;
        }
        catch (Exception ex)
        {
            LocalLogger.Warn($"Model unload for {name} failed: {ex.Message}");
        }
    }

    public async Task<bool> HealthCheckAsync(CancellationToken ct)
    {
        if (!await _client.IsAvailableAsync(ct).ConfigureAwait(false)) return false;
        if (string.IsNullOrEmpty(CurrentModel)) return true;
        try
        {
            var reply = await _client.GenerateAsync(CurrentModel, "Reply with the single word OK.", ct).ConfigureAwait(false);
            return !string.IsNullOrWhiteSpace(reply);
        }
        catch { return false; }
    }
}
