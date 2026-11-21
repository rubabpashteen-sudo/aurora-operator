using System.Diagnostics;
using System.Runtime.InteropServices;
using AuroraOperator.Core.Security;

namespace AuroraOperator.Core.Tools;

/// <summary>Controls desktop applications: launch, close, list, focus, type.</summary>
/// <summary>
/// One running process, identified precisely enough for the agent to
/// close exactly that process and no other. Identity is PID + name +
/// executable path + start time — never a bare process name, because
/// several processes can share a name (e.g. several Notepad windows
/// are several processes, and several python3 processes are common).
/// </summary>
public sealed record ProcessInfo(
    int Id,
    string Name,
    string? ExecutablePath,
    DateTime StartTime,
    string MainWindowTitle,
    int SessionId,
    bool SafeToClose);

/// <summary>
/// PID-scoped process closing (final release, security finding F20).
/// Closing by process NAME alone was the old behaviour: it closed
/// every same-named process and force-killed the whole tree when a
/// graceful close timed out, silently. That is no longer acceptable.
/// The rules now enforced here, in both controls:
/// <list type="bullet">
/// <item>Close by PID only. A name is first resolved to the list of
/// matching processes; the caller must pick one PID.</item>
/// <item>Exactly one safe match may be closed by name; two or more
/// safe matches is an ambiguity and closes NOTHING — the caller must
/// re-run with an explicit pid.</item>
/// <item>Graceful first (<c>CloseMainWindow</c>, 2 s wait). If the
/// process is still running, it is NOT force-killed unless the caller
/// explicitly passed <c>forceKill: true</c> — force termination is a
/// separately approved action (it is part of the approved step's
/// parameters, so the approval hash binds it).</item>
/// <item>Force-kill kills only that one process (<c>Kill()</c>), never
/// the whole tree.</item>
/// <item>Protected processes and session-0 processes are never
/// closed, even if approved (see <see cref="Security.ProcessSafety"/>).</item>
/// </list>
/// </summary>
public static class AppProcessCloser
{
    /// <summary>All processes with this name, with their identity and safety flag.</summary>
    public static IReadOnlyList<ProcessInfo> FindProcesses(string processName)
    {
        var result = new List<ProcessInfo>();
        if (string.IsNullOrWhiteSpace(processName))
        {
            return result;
        }

        var name = Path.GetFileNameWithoutExtension(processName);
        Process[] processes;
        try
        {
            processes = Process.GetProcessesByName(name);
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            return result;
        }

        foreach (var process in processes)
        {
            try
            {
                using (process)
                {
                    string? path = null;
                    try { path = process.MainModule?.FileName; }
                    catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception or UnauthorizedAccessException) { }
                    DateTime start;
                    try { start = process.StartTime; }
                    catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception) { start = DateTime.MinValue; }
                    string title = string.Empty;
                    try { title = process.MainWindowTitle ?? string.Empty; }
                    catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception) { }
                    result.Add(new ProcessInfo(
                        process.Id, process.ProcessName, path, start, title,
                        process.SessionId, ProcessSafety.IsSafeToClose(process)));
                }
            }
            catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
            {
                // A process that exited mid-enumeration is skipped.
            }
        }

        return result;
    }

    /// <summary>
    /// Closes exactly the process with this PID. Returns (true, message)
    /// only when that process actually exited. <paramref name="expectedName"/>
    /// (when given) must match the process's real name — a PID is never
    /// trusted on its own when the caller also named an app.
    /// </summary>
    public static (bool Success, string Message) CloseProcessById(int processId, bool forceKill, string? expectedName = null)
    {
        Process process;
        try
        {
            process = Process.GetProcessById(processId);
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            return (false, $"No running process has PID {processId}.");
        }

        using (process)
        {
            if (expectedName is not null)
            {
                var bare = Path.GetFileNameWithoutExtension(expectedName.Trim());
                if (!string.Equals(process.ProcessName, bare, StringComparison.OrdinalIgnoreCase))
                {
                    return (false, $"PID {processId} is '{process.ProcessName}', not '{bare}': refusing to close the wrong process.");
                }
            }

            if (!ProcessSafety.IsSafeToClose(process))
            {
                return (false, $"Refusing to close PID {processId} ('{process.ProcessName}'): it is a protected system process, runs in the system session, or has already exited.");
            }

            var display = $"'{process.ProcessName}' (PID {processId})";
            try
            {
                if (!process.HasExited)
                {
                    // Graceful first: ask the main window to close.
                    bool asked;
                    try { asked = process.MainWindowHandle != IntPtr.Zero && process.CloseMainWindow(); }
                    catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception) { asked = false; }
                    if (asked)
                    {
                        process.WaitForExit(2000);
                    }
                }

                if (process.HasExited)
                {
                    return (true, $"Closed {display} gracefully.");
                }

                if (!forceKill)
                {
                    return (false, $"{display} did not close gracefully and is still running. It was NOT force-terminated. To force-terminate it, re-run close-app with force=true as a separately approved action.");
                }

                // Separately approved force termination: this one process
                // only, never the whole tree.
                process.Kill();
                process.WaitForExit(2000);
                return process.HasExited
                    ? (true, $"Force-terminated {display} (separately approved).")
                    : (false, $"{display} could not be terminated even with force.");
            }
            catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception or IOException or UnauthorizedAccessException)
            {
                return (false, $"Could not close {display}: {ex.Message}");
            }
        }
    }

    /// <summary>
    /// Name-based close, safe form: closes only when exactly one
    /// safe-to-close process matches. Zero matches, only protected
    /// matches, or two-plus safe matches all close NOTHING and return
    /// a message saying why (the multi-match message lists the PIDs
    /// so the caller can re-run with an explicit pid).
    /// </summary>
    public static (bool Success, string Message) CloseByName(string processName, bool forceKill)
    {
        if (string.IsNullOrWhiteSpace(processName))
        {
            return (false, "close-app needs an 'app' argument.");
        }

        var matches = FindProcesses(processName);
        if (matches.Count == 0)
        {
            return (false, $"No running application named '{processName}' was found to close.");
        }

        var safe = matches.Where(m => m.SafeToClose).ToList();
        if (safe.Count == 0)
        {
            return (false, $"All {matches.Count} matching process(es) named '{processName}' are protected system processes and can never be terminated by the agent.");
        }

        if (safe.Count > 1)
        {
            var list = string.Join(", ", safe.Select(m =>
                $"PID {m.Id}" + (m.ExecutablePath is null ? string.Empty : $" ({m.ExecutablePath})")));
            return (false, $"Multiple processes named '{processName}' are running ({list}). No process was closed: close one by PID — re-run close-app with app='{processName}' and pid=<PID>.");
        }

        return CloseProcessById(safe[0].Id, forceKill, processName);
    }
}

public interface IAppControl
{
    /// <summary>Launches an application by name or path. Returns true when the process started.</summary>
    bool LaunchApp(string appNameOrPath);

    /// <summary>
    /// Closes the application with the given name — but only when
    /// exactly one safe-to-close process matches (see
    /// <see cref="AppProcessCloser.CloseByName"/>). Two or more matches
    /// close NOTHING; use <see cref="CloseProcessById"/> with an
    /// explicit PID instead. Protected system processes are never
    /// closed (see <see cref="Security.ProcessSafety"/>).
    /// Returns true only when a process actually exited.
    /// </summary>
    bool CloseApp(string processName);

    /// <summary>
    /// Lists the processes with this name, each identified by PID, path
    /// and start time, with its safety flag. Default: no matches (only
    /// the real controls enumerate processes).
    /// </summary>
    IReadOnlyList<ProcessInfo> FindProcesses(string processName) => Array.Empty<ProcessInfo>();

    /// <summary>
    /// Closes exactly the process with this PID (graceful first; force
    /// only when <paramref name="forceKill"/> is true, a separately
    /// approved action). Default: refuses.
    /// </summary>
    bool CloseProcessById(int processId, bool forceKill = false, string? expectedName = null) => false;

    /// <summary>Titles of windows currently shown by running applications.</summary>
    IReadOnlyList<string> RunningAppTitles();

    /// <summary>Brings the first window whose title contains the text to the foreground.</summary>
    bool FocusWindow(string titleContains);

    /// <summary>Types text into the currently focused control. Windows-only; throws elsewhere.</summary>
    bool TypeText(string text);

    /// <summary>
    /// Reads the text of the currently focused control (UI Automation
    /// ValuePattern). Default: throws <see cref="PlatformNotSupportedException"/>
    /// — the cross-platform control has no UI Automation, and it must never
    /// return invented text. Overridden by WindowsAppControl.
    /// Returns null when no readable text is available.
    /// </summary>
    string? ReadTextFromFocusedControl() => throw new PlatformNotSupportedException(
        "Reading control text needs Windows UI Automation (ValuePattern), which does not exist on this operating system.");

    /// <summary>
    /// Reads the text of the first editable control in the first window
    /// whose title contains <paramref name="titleContains"/> (UI Automation
    /// ValuePattern). Default: throws <see cref="PlatformNotSupportedException"/>.
    /// Returns null when no window / readable text is found.
    /// </summary>
    string? ReadWindowText(string titleContains) => throw new PlatformNotSupportedException(
        "Reading window text needs Windows UI Automation (ValuePattern), which does not exist on this operating system.");

    /// <summary>
    /// Saves the focused Notepad document to <paramref name="path"/> via the
    /// Notepad Save dialog (Ctrl+S + UI Automation). Default: throws
    /// <see cref="PlatformNotSupportedException"/>. Returns false when the
    /// dialog could not be driven; callers then use the verified direct
    /// file write fallback only when the user explicitly requested a save
    /// path, and verify the file content afterwards.
    /// </summary>
    bool SaveNotepadFile(string path) => throw new PlatformNotSupportedException(
        "Saving via the Notepad Save dialog needs Windows UI Automation, which does not exist on this operating system.");
}

/// <summary>
/// Cross-platform <see cref="IAppControl"/> built on <see cref="Process"/>.
/// Launch/list work on Linux and Windows. Focus is best-effort (Windows only,
/// false elsewhere). Typing needs real window input, so it is Windows-only.
/// </summary>
public sealed class ProcessAppControl : IAppControl
{
    public bool LaunchApp(string appNameOrPath)
    {
        if (string.IsNullOrWhiteSpace(appNameOrPath))
        {
            return false;
        }

        // Defense in depth (F5): even this raw primitive refuses
        // Rejected targets — URLs, shell protocols, scripts,
        // installers and shortcuts are never launched, whatever layer
        // called it. RequiresApproval targets are gated by the engine
        // (RiskClassifier + IApprovalService) before AppTools runs.
        if (AppLaunchPolicy.Evaluate(appNameOrPath) == LaunchDecision.Rejected)
        {
            return false;
        }

        try
        {
            using var process = Process.Start(new ProcessStartInfo
            {
                FileName = appNameOrPath,
                UseShellExecute = true,
            });
            return process is not null;
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception or IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>
    /// Name-based close, safe form (F20): exactly one safe match may
    /// close; two or more matches close NOTHING. See
    /// <see cref="AppProcessCloser.CloseByName"/>.
    /// </summary>
    public bool CloseApp(string processName) =>
        AppProcessCloser.CloseByName(processName, forceKill: false).Success;

    public IReadOnlyList<ProcessInfo> FindProcesses(string processName) =>
        AppProcessCloser.FindProcesses(processName);

    /// <summary>Closes exactly the process with this PID (graceful first; force only when separately approved).</summary>
    public bool CloseProcessById(int processId, bool forceKill = false, string? expectedName = null) =>
        AppProcessCloser.CloseProcessById(processId, forceKill, expectedName).Success;

    public IReadOnlyList<string> RunningAppTitles()
    {
        var titles = new List<string>();
        Process[] processes;
        try
        {
            processes = Process.GetProcesses();
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            return titles;
        }

        foreach (var process in processes)
        {
            try
            {
                using (process)
                {
                    var title = process.MainWindowTitle;
                    if (!string.IsNullOrWhiteSpace(title) && !titles.Contains(title))
                    {
                        titles.Add(title);
                    }
                }
            }
            catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
            {
                // A process that exited mid-enumeration is skipped.
            }
        }

        return titles;
    }

    /// <summary>
    /// Best-effort focus. Returns false on non-Windows; on Windows it
    /// restores and foregrounds the matching window via user32.
    /// </summary>
    public bool FocusWindow(string titleContains)
    {
        if (string.IsNullOrWhiteSpace(titleContains) || !OperatingSystem.IsWindows())
        {
            return false;
        }

        try
        {
            foreach (var process in Process.GetProcesses())
            {
                try
                {
                    using (process)
                    {
                        if (process.MainWindowHandle == IntPtr.Zero)
                        {
                            continue;
                        }

                        if (!process.MainWindowTitle.Contains(titleContains, StringComparison.OrdinalIgnoreCase))
                        {
                            continue;
                        }

                        ShowWindow(process.MainWindowHandle, SwRestore);
                        return SetForegroundWindow(process.MainWindowHandle);
                    }
                }
                catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
                {
                    // Skip processes that exit mid-enumeration.
                }
            }
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            return false;
        }

        return false;
    }

    /// <summary>
    /// Types text with Unicode SendInput keystrokes. Windows-only: on any
    /// other OS this throws <see cref="PlatformNotSupportedException"/>.
    /// </summary>
    public bool TypeText(string text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return false;
        }

        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException(
                "TypeText is only supported on Windows: it sends keystrokes to the focused window " +
                "using the Windows user32 SendInput API, which does not exist on this operating system.");
        }

        var inputs = new List<INPUT>(text.Length * 2);
        foreach (var ch in text)
        {
            inputs.Add(new INPUT
            {
                Type = InputKeyboard,
                U = new InputUnion
                {
                    Ki = new KEYBDINPUT { WScan = ch, DwFlags = KeyeventfUnicode },
                },
            });
            inputs.Add(new INPUT
            {
                Type = InputKeyboard,
                U = new InputUnion
                {
                    Ki = new KEYBDINPUT { WScan = ch, DwFlags = KeyeventfUnicode | KeyeventfKeyUp },
                },
            });
        }

        var sent = SendInput((uint)inputs.Count, inputs.ToArray(), Marshal.SizeOf<INPUT>());
        return sent == inputs.Count;
    }

    // ---- user32 (Windows only, only called after an OperatingSystem.IsWindows() check) ----

    private const uint InputKeyboard = 1;
    private const uint KeyeventfUnicode = 0x0004;
    private const uint KeyeventfKeyUp = 0x0002;
    private const int SwRestore = 9;

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint SendInput(uint nInputs, INPUT[] pInputs, int cbSize);

    [StructLayout(LayoutKind.Sequential)]
    private struct INPUT
    {
        public uint Type;
        public InputUnion U;
    }

    [StructLayout(LayoutKind.Explicit)]
    private struct InputUnion
    {
        [FieldOffset(0)]
        public KEYBDINPUT Ki;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct KEYBDINPUT
    {
        public ushort WVk;
        public ushort WScan;
        public uint DwFlags;
        public uint Time;
        public IntPtr DwExtraInfo;
    }
}
