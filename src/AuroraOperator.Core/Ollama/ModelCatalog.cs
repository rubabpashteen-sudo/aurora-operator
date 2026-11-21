namespace AuroraOperator.Core.Ollama;

/// <summary>One Ollama model known to Aurora Operator.</summary>
public sealed record ModelInfo(string Name, long ApproxSizeBytes, int MinRamGb, string Role);

/// <summary>
/// One hardware tier and the model Aurora Operator recommends for it.
/// Verified 2026-11-21 against ollama.com/library: qwen3:0.6b (523 MB),
/// qwen3:1.7b (1.4 GB), qwen3:4b (2.5 GB) and qwen3:8b (5.2 GB) all exist.
/// Only one model is ever loaded at a time — Aurora Operator never
/// auto-loads multiple large models (see ModelManager; CurrentModel is a
/// single string and SwitchModelAsync verifies one model before using it).
/// </summary>
public sealed record HardwareProfile(
    string Name,
    int MinTotalRamGb,
    string Model,
    int ContextTokens,
    string Notes);

/// <summary>Catalogue of supported local models and hardware-based selection.
/// Sizes are approximate download sizes; a model needs its size in RAM plus
/// headroom for Windows itself, so selection is deliberately conservative.</summary>
public static class ModelCatalog
{
    public static IReadOnlyList<ModelInfo> All { get; } = new[]
    {
        new ModelInfo("qwen3:4b", 2_600_000_000L, 8, "primary"),
        new ModelInfo("qwen3:1.7b", 1_400_000_000L, 4, "fallback"),
        new ModelInfo("qwen3.5:2b", 2_900_000_000L, 8, "vision"),
        new ModelInfo("qwen2.5-coder:3b", 1_900_000_000L, 6, "coding"),
        new ModelInfo("qwen3:0.6b", 523_000_000L, 4, "ultra-light"),
        new ModelInfo("qwen3:8b", 5_200_000_000L, 16, "advanced"),
    };

    /// <summary>Hardware tiers, smallest first. The profile table and
    /// <see cref="SelectBestModel"/> agree (Phase 7 integration decision,
    /// 2026-11-21, completed in Phase 8): on an 8 GB machine Aurora
    /// auto-selects qwen3:1.7b, because a 2.5 GB model plus Windows itself
    /// leaves too little headroom at 8 GB total. qwen3:4b is auto-selected
    /// only at 16 GB total with at least 6 GB free, and qwen3:8b only at
    /// 32 GB total with at least 10 GB free. Below 8 GB the Minimal tier
    /// selects qwen3:0.6b — the same threshold the NSIS/Inno installers
    /// use. A user may still pick a larger model manually from Model
    /// Manager. Sizes verified against ollama.com/library 2026-11-21;
    /// qwen3.5:2b is ~2.9 GB, not 1.6 GB.</summary>
    public static IReadOnlyList<HardwareProfile> Profiles { get; } = new[]
    {
        new HardwareProfile("Minimal", 0, "qwen3:0.6b", 4096,
            "Below 8 GB RAM. qwen3:0.6b (523 MB), 4K context, CPU-friendly. " +
            "The smallest model Aurora Operator will auto-select."),
        new HardwareProfile("Lightweight", 8, "qwen3:1.7b", 8192,
            "8 GB RAM. qwen3:1.7b (1.4 GB), 4K-8K context, CPU-friendly. " +
            "On machines below 8 GB use qwen3:0.6b (523 MB) instead."),
        new HardwareProfile("Recommended", 16, "qwen3:4b", 8192,
            "16 GB RAM. qwen3:4b (2.5 GB), 8K context — the primary model."),
        new HardwareProfile("Advanced", 32, "qwen3:8b", 8192,
            "32 GB RAM. qwen3:8b (5.2 GB), 8K context. The largest model " +
            "Aurora Operator will recommend; nothing bigger is auto-selected."),
    };

    /// <summary>Best hardware profile for the machine's total RAM.</summary>
    public static HardwareProfile SelectProfile(long totalRamBytes)
    {
        const long gb = 1024L * 1024 * 1024;
        HardwareProfile best = Profiles[0];
        foreach (var p in Profiles)
            if (totalRamBytes >= p.MinTotalRamGb * gb) best = p;
        return best;
    }

    public static ModelInfo Primary => All[0];
    public static ModelInfo Fallback => All[1];

    /// <summary>Best primary model for the machine. Aligned with
    /// <see cref="Profiles"/> and with the NSIS/Inno installer selection
    /// (Phase 8): 32 GB+ total RAM and at least 10 GB free: qwen3:8b;
    /// 16 GB+ total and at least 6 GB free: qwen3:4b; 8 GB+ total:
    /// qwen3:1.7b; below 8 GB total: qwen3:0.6b. Free-RAM gates are
    /// deliberate: a machine with plenty of total RAM but little free
    /// RAM falls back to the smaller model instead of destabilising
    /// Windows. qwen3:8b is therefore genuinely auto-selected at 32 GB,
    /// not merely a recommended/manual option.</summary>
    public static string SelectBestModel(long totalRamBytes, long availableRamBytes)
    {
        const long gb = 1024L * 1024 * 1024;
        if (totalRamBytes >= 32 * gb && availableRamBytes >= 10 * gb) return "qwen3:8b";
        if (totalRamBytes >= 16 * gb && availableRamBytes >= 6 * gb) return "qwen3:4b";
        if (totalRamBytes >= 8 * gb) return "qwen3:1.7b";
        return "qwen3:0.6b";
    }
}
