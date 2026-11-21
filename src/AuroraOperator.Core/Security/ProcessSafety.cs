using System.Diagnostics;

namespace AuroraOperator.Core.Security;

/// <summary>
/// Guard for the "app" tool's close-app action (Phase 8, security
/// finding F20). Previously <c>CloseApp</c> found processes by name and
/// killed the whole tree after 2 seconds, classified NormalReversible
/// (auto-allowed) — so a step naming e.g. <c>explorer</c> or a database
/// process could terminate it, and killing after a failed graceful
/// close can lose unsaved work. Two layers now protect it:
/// <list type="bullet">
/// <item><see cref="RiskClassifier"/> classifies close-app as
/// SensitiveFileModification, so the engine asks the user (showing the
/// exact process name) before the tool runs.</item>
/// <item>This class refuses protected processes outright, in both the
/// tool layer (<see cref="Tools.AppTools"/>) and the process controls,
/// even if approved. Approval authorises closing an ordinary app; it
/// never authorises killing a system process.</item>
/// </list>
/// Identity: a close is by PID (see <see cref="Tools.AppProcessCloser"/>),
/// never by bare process name — several processes can share a name, and
/// closing by name used to close all of them. The approval dialog names
/// the exact target (PID, path, start time) because the approval request
/// carries the exact parameters. Ownership by another user is a
/// Windows-test item: the name-based API cannot prove ownership
/// cross-platform, and the tool reports exactly which PID it closed.</summary>
public static class ProcessSafety
{
    /// <summary>Process names that must never be closed by the agent.
    /// Compared case-insensitively, without the .exe extension. This
    /// list covers Windows system processes, the shell, the window
    /// manager, the login/session infrastructure, the service host,
    /// the antimalware service and the print spooler. It is a
    /// denylist, not a proof of safety for everything else — closing
    /// any other process still needs approval (see above).</summary>
    public static readonly string[] ProtectedProcessNames =
    {
        "system", "registry", "smss", "csrss", "lsass", "winlogon", "wininit",
        "services", "svchost", "dwm", "fontdrvhost", "conhost", "explorer",
        "searchindexer", "spoolsv", "msmpeng", "securityhealthservice",
        "audiodg", "sihost", "taskhostw", "lsaiso", "memory compression",
        "secure system", "idle",
    };

    /// <summary>True when the name (with or without .exe) is protected.</summary>
    public static bool IsProtectedProcessName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return false;
        }
        var bare = Path.GetFileNameWithoutExtension(name.Trim());
        return ProtectedProcessNames.Contains(bare, StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>
    /// True when this specific process may be closed: not a protected
    /// name and (on Windows) not running in session 0, the system
    /// session, where user apps never run. A process that has already
    /// exited is not closable either. Ownership by another user cannot
    /// be proven from this API on every platform — see the class docs.
    /// </summary>
    public static bool IsSafeToClose(Process process)
    {
        ArgumentNullException.ThrowIfNull(process);
        try
        {
            if (process.HasExited)
            {
                return false;
            }
            if (IsProtectedProcessName(process.ProcessName))
            {
                return false;
            }
            if (OperatingSystem.IsWindows() && process.SessionId == 0)
            {
                return false;
            }
            return true;
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            // A process we cannot inspect is not one we close.
            return false;
        }
    }
}
