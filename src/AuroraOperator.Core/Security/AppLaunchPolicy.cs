namespace AuroraOperator.Core.Security;

/// <summary>What the launch policy says about one launch target.</summary>
public enum LaunchDecision
{
    /// <summary>A known-safe target: launch without an extra prompt.</summary>
    Allowed,

    /// <summary>Not known-safe, but not inherently dangerous: the engine
    /// must get explicit per-request approval (RiskClassifier maps this
    /// to SensitiveFileModification) before the tool launches it.</summary>
    RequiresApproval,

    /// <summary>Never launchable through the agent, even if approved:
    /// URLs, shell protocols, scripts, installers and shortcuts. The
    /// tool refuses these outright.</summary>
    Rejected,
}

/// <summary>
/// Allowlist for the "app" tool's launch action (Phase 8, security
/// finding F5). Previously <c>LaunchApp</c> ran with
/// <c>UseShellExecute = true</c> on whatever string it was given, so a
/// path to a <c>.bat</c>/<c>.ps1</c>/<c>.msi</c> — or a URL — would be
/// executed/opened by the shell. Untrusted LLM output must never
/// independently grant that.
///
/// Enforcement is two layers, and the boundary matters:
/// <list type="bullet">
/// <item><see cref="RiskClassifier"/> maps <see cref="RequiresApproval"/>
/// targets to SensitiveFileModification, so the engine asks the user
/// (showing the exact target) before the tool runs.</item>
/// <item><see cref="Tools.AppTools"/> and the Windows control refuse
/// <see cref="Rejected"/> targets outright, even if approved.</item>
/// </list>
/// The raw <see cref="Tools.ProcessAppControl"/> API is deliberately
/// NOT policy-checked: it is the cross-platform process primitive the
/// tests use to launch real processes, and the shipped app never
/// reaches it except through AppTools. The policy lives at the
/// agent-reachable layer.</summary>
public static class AppLaunchPolicy
{
    /// <summary>Friendly names the agent may launch without a prompt,
    /// resolved to their Windows executables.</summary>
    public static IReadOnlyDictionary<string, string> FriendlyApps { get; } =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["notepad"] = "notepad.exe",
            ["calculator"] = "calc.exe",
            ["paint"] = "mspaint.exe",
            ["wordpad"] = "wordpad.exe",
        };

    /// <summary>Extensions that are never launched through the agent:
    /// scripts, installers, shortcuts and registry files.</summary>
    public static readonly string[] DangerousExtensions =
        { ".bat", ".cmd", ".ps1", ".psm1", ".psd1", ".vbs", ".vbe", ".js", ".jse", ".wsf", ".msi", ".msp", ".lnk", ".scr", ".hta", ".reg", ".com", ".pif" };

    /// <summary>Evaluates one launch target. Friendly names are
    /// <see cref="LaunchDecision.Allowed"/>; URLs, shell protocols and
    /// dangerous extensions are <see cref="LaunchDecision.Rejected"/>;
    /// every other executable name or path is
    /// <see cref="LaunchDecision.RequiresApproval"/>.</summary>
    public static LaunchDecision Evaluate(string? target)
    {
        if (string.IsNullOrWhiteSpace(target))
        {
            return LaunchDecision.Rejected;
        }

        var t = target.Trim();

        // URLs and shell protocols (http:, ms-settings:, file:, ...).
        // A Windows drive path (C:\... or C:/...) is not a protocol.
        if (t.Contains("://", StringComparison.Ordinal))
        {
            return LaunchDecision.Rejected;
        }
        var colon = t.IndexOf(':');
        if (colon > 1 && !IsWindowsDrivePath(t))
        {
            return LaunchDecision.Rejected;
        }

        var ext = Path.GetExtension(t);
        if (!string.IsNullOrEmpty(ext) &&
            DangerousExtensions.Contains(ext, StringComparer.OrdinalIgnoreCase))
        {
            return LaunchDecision.Rejected;
        }

        var bare = Path.GetFileNameWithoutExtension(t);
        if (FriendlyApps.ContainsKey(bare) || FriendlyApps.ContainsKey(t))
        {
            return LaunchDecision.Allowed;
        }
        if (string.Equals(Path.GetFileName(t), "notepad.exe", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(Path.GetFileName(t), "calc.exe", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(Path.GetFileName(t), "mspaint.exe", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(Path.GetFileName(t), "wordpad.exe", StringComparison.OrdinalIgnoreCase))
        {
            return LaunchDecision.Allowed;
        }

        return LaunchDecision.RequiresApproval;
    }

    /// <summary>Human-readable reason for a <see cref="LaunchDecision.Rejected"/> target.</summary>
    public static string RejectionReason(string? target)
    {
        if (string.IsNullOrWhiteSpace(target))
        {
            return "no application was named.";
        }
        var t = target.Trim();
        if (t.Contains("://", StringComparison.Ordinal) || (t.IndexOf(':') > 1 && !IsWindowsDrivePath(t)))
        {
            return $"URLs and shell protocols are never launched by the agent: '{t}'.";
        }
        var ext = Path.GetExtension(t);
        if (!string.IsNullOrEmpty(ext) &&
            DangerousExtensions.Contains(ext, StringComparer.OrdinalIgnoreCase))
        {
            return $"Files ending in '{ext}' are never launched by the agent (scripts/installers/shortcuts). Launch the application itself instead.";
        }
        return $"The target '{t}' is not on the launch allowlist.";
    }

    private static bool IsWindowsDrivePath(string t) =>
        t.Length >= 3 && char.IsLetter(t[0]) && t[1] == ':' && (t[2] == '\\' || t[2] == '/');
}
