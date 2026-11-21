namespace AuroraOperator.Core.Ollama;

/// <summary>
/// One model entry shown in Model Manager: a catalog entry, an installed
/// model, or both. Installed models that are not in the catalog (for
/// example the user's qwen3.5:4b) are shown as installed so they can be
/// selected, instead of being invisible to the user.
/// </summary>
public sealed record AvailableModel(string Name, bool IsInstalled, bool IsInCatalog, string Role)
{
    /// <summary>Human-readable label for the Model Manager list.</summary>
    public string DisplayLabel => IsInstalled
        ? IsInCatalog ? $"{Name} — Installed" : $"{Name} — Installed (not in catalog)"
        : $"{Name} — Not installed";
}

/// <summary>
/// Pure fallback/selection logic for the "model not found" fix: which
/// installed model to use when the configured model is missing, and how
/// the Model Manager list merges installed models with the catalog.
/// Pure (no I/O) so it is unit-testable without a server.
/// </summary>
public static class ModelFallback
{
    /// <summary>
    /// Fallback priority when the configured model is missing: catalog
    /// models first — primary, fallback, ultra-light, advanced, vision,
    /// coding — then any other installed model. This is the order the
    /// fix was specified in (2026-11-22); it is deliberately not
    /// <see cref="ModelCatalog.All"/> order, which lists the vision and
    /// coding models before the ultra-light and advanced general models.
    /// </summary>
    public static IReadOnlyList<string> CatalogPriority { get; } = new[]
    {
        "qwen3:4b", "qwen3:1.7b", "qwen3:0.6b", "qwen3:8b", "qwen3.5:2b", "qwen2.5-coder:3b",
    };

    /// <summary>The exact shell command that installs a model, shown to
    /// the user whenever a model is missing.</summary>
    public static string PullCommand(string model) => "ollama pull " + model;

    /// <summary>
    /// Best installed model to use instead of <paramref name="configuredModel"/>.
    /// Prefers catalog models in <see cref="CatalogPriority"/> order; if no
    /// catalog model is installed, uses an installed non-catalog model,
    /// preferring the same (qwen) family — for example the user's
    /// qwen3.5:4b — then any installed model, deterministically sorted.
    /// Returns null when nothing is installed at all, or when the only
    /// installed model is the configured one itself (which cannot happen
    /// after a genuine model-not-found, but is guarded anyway).
    /// </summary>
    public static string? SelectFallbackModel(string configuredModel, IReadOnlyList<string> installed)
    {
        var present = new HashSet<string>(installed, StringComparer.OrdinalIgnoreCase);
        foreach (var name in CatalogPriority)
        {
            if (!string.Equals(name, configuredModel, StringComparison.OrdinalIgnoreCase) &&
                present.Contains(name))
            {
                return name;
            }
        }

        var others = installed
            .Where(m => !string.Equals(m, configuredModel, StringComparison.OrdinalIgnoreCase))
            .OrderBy(m => m.StartsWith("qwen", StringComparison.OrdinalIgnoreCase) ? 0 : 1)
            .ThenBy(m => m, StringComparer.OrdinalIgnoreCase)
            .ToList();
        return others.Count == 0 ? null : others[0];
    }

    /// <summary>
    /// Merged Model Manager list: every catalog entry (in catalog order,
    /// flagged installed or not) followed by installed models that are
    /// not in the catalog, sorted by name. Installed non-catalog models
    /// are therefore visible and selectable.
    /// </summary>
    public static IReadOnlyList<AvailableModel> MergeAvailableModels(IReadOnlyList<string> installed)
    {
        var present = new HashSet<string>(installed, StringComparer.OrdinalIgnoreCase);
        var result = new List<AvailableModel>();
        foreach (var info in ModelCatalog.All)
        {
            result.Add(new AvailableModel(info.Name, present.Contains(info.Name), true, info.Role));
        }
        foreach (var name in installed
            .Where(m => !ModelCatalog.All.Any(c => string.Equals(c.Name, m, StringComparison.OrdinalIgnoreCase)))
            .OrderBy(m => m, StringComparer.OrdinalIgnoreCase))
        {
            result.Add(new AvailableModel(name, true, false, "installed"));
        }
        return result;
    }
}
