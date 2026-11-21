using AuroraOperator.Core;
using AuroraOperator.Core.Agent;

namespace AuroraOperator.Core.Security;

/// <summary>
/// Maps a plan step (tool + action + description) to a <see cref="RiskLevel"/>.
/// ReadOnly and NormalReversible may run autonomously when the settings
/// allow it; SensitiveFileModification and above MUST be approved per
/// request by an <see cref="IApprovalService"/> before they run.
/// Classification is purely a function of what the step does: words like
/// "approved=true" in a plan or description never lower a risk level, and
/// an unknown modifying action defaults to Sensitive rather than to
/// auto-allowed.
/// </summary>
public static class RiskClassifier
{
    private static readonly string[] ReadOnlyActions =
        { "list", "search", "read", "read-pdf", "summarise-pdf", "summarize-pdf", "diagnose", "find-duplicates", "research", "verify", "read-text", "verify-text", "focus" };
    private static readonly string[] ExternalActions =
        { "post", "upload", "send", "email", "share", "publish", "message", "download" };
    private static readonly string[] DestructiveActions =
        { "delete", "delete-file", "remove", "remove-file", "wipe", "format" };
    private static readonly string[] SensitiveActions =
        { "rename", "move", "organize", "overwrite", "write-existing" };

    /// <summary>Classifies one plan step from its tool, structured Args and description. Args win over legacy hints.</summary>
    public static RiskLevel Classify(PlanStep step)
    {
        ArgumentNullException.ThrowIfNull(step);
        var hints = AgentEngine.ResolveArgs(step);
        var action = hints.TryGetValue("action", out var a) ? a : null;
        return Classify(step.ToolName, action, step.Description, hints);
    }

    /// <summary>Classifies a tool call. <paramref name="hints"/> carries the parsed key=value pairs (path, destination, …) when available.</summary>
    public static RiskLevel Classify(string? toolName, string? action, string? description, IReadOnlyDictionary<string, string>? hints = null)
    {
        hints ??= AgentEngine.ParseHints(description);
        var tool = (toolName ?? string.Empty).Trim().ToLowerInvariant();
        var act = (action ?? string.Empty).Trim().ToLowerInvariant();
        var text = ((description ?? string.Empty) + " " + (action ?? string.Empty)).ToLowerInvariant();

        // Destructive first: deleting, wiping, formatting, or a delete-all style request.
        if (DestructiveActions.Contains(act) || ContainsAny(text, "delete all", "delete everything", "delete every", "remove all", "wipe", "format ", "rm -rf", "factory reset"))
        {
            return RiskLevel.Destructive;
        }
        if (act is "delete" || text.Contains("action=delete", StringComparison.Ordinal))
        {
            return RiskLevel.Destructive;
        }

        // Administrator: elevated execution or system settings changes,
        // or running code. Running a project's tests executes arbitrary
        // code from that directory (Phase 7: "run-tests" was
        // NormalReversible, i.e. auto-allowed without approval, even when
        // pointed at a directory the user just downloaded — it now needs
        // a per-request approval like any other code execution).
        if (tool is "terminal" || tool is "coding" && act is "run-tests"
            || act is "run-admin" or "run-as-admin" or "admin"
            || ContainsAny(text, "as administrator", "run as admin", "registry", "system settings", "change system", "elevated"))
        {
            return RiskLevel.Administrator;
        }

        // External communication: posting, uploading, emailing, messaging,
        // downloading. (Phase 7: "download" was missing from ExternalActions,
        // so a browser download — fetching an arbitrary file from the
        // internet and writing it to disk — fell through the research
        // carve-out below and was classified ReadOnly, i.e. auto-allowed
        // with no approval. It is now ExternalCommunication.)
        if (tool is "browser" or "email" or "messenger"
            || ExternalActions.Contains(act)
            || ContainsAny(text, "action=post", "action=upload", "action=send", "action=email", "send an email", "send email", "upload"))
        {
            // A plain web research read is not external communication.
            if (act is "research" || (tool == "browser" && !ExternalActions.Contains(act) && !ContainsAny(text, "post", "upload", "send", "email", "share", "publish")))
            {
                return RiskLevel.ReadOnly;
            }
            return RiskLevel.ExternalCommunication;
        }

        // Sensitive file modifications: rename / move / organize, or writing to an existing file.
        if (SensitiveActions.Contains(act))
        {
            return RiskLevel.SensitiveFileModification;
        }
        if (act is "write-file" or "save-as" or "notepad-write")
        {
            return FileExistsTarget(hints) ? RiskLevel.SensitiveFileModification : RiskLevel.NormalReversible;
        }
        // App control: launching / typing into a new document is reversible;
        // closing an app is likewise reversible (its documents are untouched
        // unless the app itself prompts to save).
        // Phase 8 (F5): an unknown launch target needs approval — only the
        // four friendly apps are auto-allowed. Dangerous targets (URLs,
        // scripts, installers) are also Sensitive here so the user sees
        // them in the approval dialog; the tool refuses them outright
        // even if approved.
        if (act is "launch-app" or "launch")
        {
            return LaunchRisk(hints, description);
        }
        // Phase 8 (F20): closing an app can lose unsaved work and kill a
        // process the user did not mean to stop, so it needs a per-request
        // approval (it was NormalReversible before).
        if (act is "close-app" or "close")
        {
            return RiskLevel.SensitiveFileModification;
        }
        if (act is "type-text" or "type")
        {
            return RiskLevel.NormalReversible;
        }
        if (act is "copy")
        {
            return DestinationExists(hints) ? RiskLevel.SensitiveFileModification : RiskLevel.NormalReversible;
        }
        if (act is "csv-to-excel" or "create-powerpoint" or "create-word")
        {
            return OutputExists(hints) ? RiskLevel.SensitiveFileModification : RiskLevel.NormalReversible;
        }

        // Read-only tools and actions.
        if (tool is "diagnostics" or "duplicates" or "verify")
        {
            return RiskLevel.ReadOnly;
        }
        if (ReadOnlyActions.Contains(act))
        {
            return RiskLevel.ReadOnly;
        }

        // Reversible creation. ("run-tests" is NOT here: it executes code
        // and is classified Administrator above — see the Phase 7 note.)
        if (act is "create-folder" or "create-project" or "create-file")
        {
            return RiskLevel.NormalReversible;
        }
        if (tool is "filesystem" && string.IsNullOrWhiteSpace(act))
        {
            return RiskLevel.ReadOnly;
        }

        // Unknown modifying action: fail closed towards approval, never auto-allow.
        return RiskLevel.SensitiveFileModification;
    }

    /// <summary>Risk for a launch-app step. The target comes from the
    /// step Args ("app"/"name"/"path") when present; otherwise a friendly
    /// app name mentioned in the description (e.g. "Open Notepad.") is
    /// treated as that app, so the planner's Notepad path stays
    /// auto-allowed. An unidentifiable target is Sensitive, never
    /// auto-allowed.</summary>
    private static RiskLevel LaunchRisk(IReadOnlyDictionary<string, string>? hints, string? description)
    {
        string? target = null;
        if (hints is not null)
        {
            foreach (var key in new[] { "app", "name", "path" })
            {
                if (hints.TryGetValue(key, out var v) && !string.IsNullOrWhiteSpace(v))
                {
                    target = v;
                    break;
                }
            }
        }
        if (target is null && !string.IsNullOrWhiteSpace(description))
        {
            var lower = description.ToLowerInvariant();
            foreach (var friendly in AppLaunchPolicy.FriendlyApps.Keys)
            {
                if (lower.Contains(friendly, StringComparison.Ordinal))
                {
                    target = friendly;
                    break;
                }
            }
        }
        if (target is null)
        {
            return RiskLevel.SensitiveFileModification;
        }
        return AppLaunchPolicy.Evaluate(target) == LaunchDecision.Allowed
            ? RiskLevel.NormalReversible
            : RiskLevel.SensitiveFileModification;
    }

    private static bool FileExistsTarget(IReadOnlyDictionary<string, string>? hints) =>
        hints is not null && hints.TryGetValue("path", out var p) && !string.IsNullOrWhiteSpace(p) && File.Exists(p);

    private static bool DestinationExists(IReadOnlyDictionary<string, string>? hints) =>
        hints is not null && hints.TryGetValue("destination", out var d) && !string.IsNullOrWhiteSpace(d) &&
        (File.Exists(d) || Directory.Exists(d));

    private static bool OutputExists(IReadOnlyDictionary<string, string>? hints)
    {
        if (hints is null) return false;
        if (hints.TryGetValue("destination", out var d) && !string.IsNullOrWhiteSpace(d) && File.Exists(d)) return true;
        if (hints.TryGetValue("path", out var p) && !string.IsNullOrWhiteSpace(p) && File.Exists(p)) return true;
        return false;
    }

    private static bool ContainsAny(string text, params string[] needles) =>
        needles.Any(n => text.Contains(n, StringComparison.Ordinal));
}
