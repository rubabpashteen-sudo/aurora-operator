using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows.Automation;
using AuroraOperator.Core.Security;
using AuroraOperator.Core.Tools;

namespace AuroraOperator.App.Services;

/// <summary>
/// Windows-only <see cref="IAppControl"/> using UI Automation
/// (<see cref="System.Windows.Automation"/>) plus user32.
/// Compiled only in the App project (net8.0-windows + UseWPF).
/// </summary>
public sealed class WindowsAppControl : IAppControl
{
    public bool LaunchApp(string appNameOrPath)
    {
        if (string.IsNullOrWhiteSpace(appNameOrPath))
        {
            return false;
        }

        // Phase 8 (F5): dangerous targets are refused outright at the
        // Windows control too, even if approved — a URL, script or
        // installer is never launched through the agent.
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
    /// close; two or more matches close NOTHING — use
    /// <see cref="CloseProcessById"/> with an explicit PID. Protected
    /// names are refused before any process is touched, even if
    /// approved. Shared implementation: <see cref="AppProcessCloser"/>.
    /// </summary>
    public bool CloseApp(string processName)
    {
        if (string.IsNullOrWhiteSpace(processName))
        {
            return false;
        }

        if (ProcessSafety.IsProtectedProcessName(processName))
        {
            return false;
        }

        return AppProcessCloser.CloseByName(processName, forceKill: false).Success;
    }

    public IReadOnlyList<ProcessInfo> FindProcesses(string processName) =>
        AppProcessCloser.FindProcesses(processName);

    /// <summary>Closes exactly the process with this PID (graceful first; force only when separately approved).</summary>
    public bool CloseProcessById(int processId, bool forceKill = false, string? expectedName = null) =>
        AppProcessCloser.CloseProcessById(processId, forceKill, expectedName).Success;

    /// <summary>Window titles from UI Automation (falls back to Process titles if UIA is unavailable).</summary>
    public IReadOnlyList<string> RunningAppTitles()
    {
        try
        {
            var titles = new List<string>();
            var windows = AutomationElement.RootElement.FindAll(
                TreeScope.Children,
                new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Window));
            foreach (AutomationElement window in windows)
            {
                try
                {
                    var name = window.Current.Name;
                    if (!string.IsNullOrWhiteSpace(name) && !titles.Contains(name))
                    {
                        titles.Add(name);
                    }
                }
                catch (ElementNotAvailableException)
                {
                    // Window closed mid-enumeration.
                }
            }

            return titles;
        }
        catch (Exception ex) when (ex is ElementNotAvailableException or InvalidOperationException or COMException)
        {
            var titles = new List<string>();
            foreach (var process in Process.GetProcesses())
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
                catch (Exception inner) when (inner is InvalidOperationException or System.ComponentModel.Win32Exception)
                {
                    // Skip processes that exit mid-enumeration.
                }
            }

            return titles;
        }
    }

    /// <summary>
    /// Finds the first window whose title contains the text, restores it if
    /// minimized (via <see cref="WindowPattern"/>), and brings it to the
    /// foreground via user32 <c>SetForegroundWindow</c>.
    /// </summary>
    public bool FocusWindow(string titleContains)
    {
        if (string.IsNullOrWhiteSpace(titleContains))
        {
            return false;
        }

        var window = FindWindowByTitle(titleContains);
        if (window is null)
        {
            return false;
        }

        try
        {
            if (window.TryGetCurrentPattern(WindowPattern.Pattern, out var patternObject)
                && patternObject is WindowPattern windowPattern)
            {
                try
                {
                    if (windowPattern.Current.WindowVisualState == WindowVisualState.Minimized)
                    {
                        windowPattern.SetWindowVisualState(WindowVisualState.Normal);
                    }
                }
                catch (InvalidOperationException)
                {
                    // The window may not allow state changes; foregrounding below still applies.
                }
            }

            var handle = GetWindowHandle(window);
            if (handle == IntPtr.Zero)
            {
                return false;
            }

            ShowWindow(handle, SwRestore);
            return SetForegroundWindow(handle);
        }
        catch (ElementNotAvailableException)
        {
            return false;
        }
    }

    /// <summary>
    /// Types text into the focused control: first via <see cref="ValuePattern"/>
    /// (focused element, else the first editable descendant of the focused
    /// window), falling back to Unicode SendInput keystrokes.
    /// </summary>
    public bool TypeText(string text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return false;
        }

        try
        {
            var focused = AutomationElement.FocusedElement;
            if (focused is not null)
            {
                if (TrySetValue(focused, text))
                {
                    return true;
                }

                // Look for an editable descendant of the focused element/window.
                var edit = focused.FindFirst(
                    TreeScope.Descendants,
                    new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Edit));
                if (edit is not null && TrySetValue(edit, text))
                {
                    return true;
                }

                var anyValue = focused.FindFirst(
                    TreeScope.Descendants,
                    new PropertyCondition(AutomationElement.IsValuePatternAvailableProperty, true));
                if (anyValue is not null && TrySetValue(anyValue, text))
                {
                    return true;
                }
            }
        }
        catch (Exception ex) when (ex is ElementNotAvailableException or InvalidOperationException or COMException)
        {
            // Fall through to SendInput.
        }

        return TypeTextViaSendInput(text);
    }

    /// <summary>
    /// Reads the text of the focused control via <see cref="ValuePattern"/>
    /// (focused element, else the first Edit descendant), or null when no
    /// readable text is available. Real UI Automation — never invented text.
    /// </summary>
    public string? ReadTextFromFocusedControl()
    {
        try
        {
            var focused = AutomationElement.FocusedElement;
            if (focused is null) return null;
            if (TryGetValue(focused, out var value)) return value;
            var edit = focused.FindFirst(
                TreeScope.Descendants,
                new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Edit));
            if (edit is not null && TryGetValue(edit, out value)) return value;
            var anyValue = focused.FindFirst(
                TreeScope.Descendants,
                new PropertyCondition(AutomationElement.IsValuePatternAvailableProperty, true));
            if (anyValue is not null && TryGetValue(anyValue, out value)) return value;
            return null;
        }
        catch (Exception ex) when (ex is ElementNotAvailableException or InvalidOperationException or COMException)
        {
            return null;
        }
    }

    /// <summary>
    /// Reads the text of the first editable control in the first window
    /// whose title contains <paramref name="titleContains"/> (UI Automation
    /// ValuePattern), or null when no such window / readable text exists.
    /// </summary>
    public string? ReadWindowText(string titleContains)
    {
        if (string.IsNullOrWhiteSpace(titleContains)) return null;
        var window = FindWindowByTitle(titleContains);
        if (window is null) return null;
        try
        {
            var edit = window.FindFirst(
                TreeScope.Descendants,
                new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Edit));
            if (edit is not null && TryGetValue(edit, out var value)) return value;
            var anyValue = window.FindFirst(
                TreeScope.Descendants,
                new PropertyCondition(AutomationElement.IsValuePatternAvailableProperty, true));
            if (anyValue is not null && TryGetValue(anyValue, out value)) return value;
            return TryGetValue(window, out value) ? value : null;
        }
        catch (Exception ex) when (ex is ElementNotAvailableException or InvalidOperationException or COMException)
        {
            return null;
        }
    }

    /// <summary>
    /// Saves the focused Notepad document to <paramref name="path"/>: focuses
    /// Notepad, sends Ctrl+S, and drives the Save dialog via UI Automation
    /// (sets the filename Edit to the full path, presses Save, confirms an
    /// overwrite prompt with Yes). Returns true only when the file exists
    /// afterwards. Returns false when no Save dialog appears (e.g. the
    /// document was already saved under a name, or the dialog could not be
    /// driven) — the caller then uses the verified direct file write
    /// fallback, never claims a dialog save happened. Windows only; this
    /// class is compiled only into the Windows App project.
    /// </summary>
    public bool SaveNotepadFile(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return false;
        var fullPath = Path.GetFullPath(path);
        if (!FocusWindow("Notepad")) return false;

        SendCtrlS();

        // Wait for the Save dialog (a top-level window whose name contains
        // "Save"); modern Notepad may instead save silently when the
        // document already has a file name.
        AutomationElement? dialog = null;
        var deadline = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(3);
        while (DateTimeOffset.UtcNow < deadline)
        {
            dialog = FindWindowByTitle("Save");
            if (dialog is not null) break;
            Thread.Sleep(200);
        }
        if (dialog is null) return File.Exists(fullPath);

        try
        {
            var fileNameEdit = dialog.FindFirst(
                TreeScope.Descendants,
                new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Edit));
            if (fileNameEdit is null) return false;
            if (!TrySetValue(fileNameEdit, fullPath)) return false;

            var saveButton = FindButton(dialog, "Save");
            if (saveButton is null) return false;
            if (!TryInvoke(saveButton)) return false;

            // Overwrite confirmation ("Replace"/"Yes") if the file exists.
            deadline = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(3);
            while (DateTimeOffset.UtcNow < deadline)
            {
                if (File.Exists(fullPath)) return true;
                var confirm = FindWindowByTitle("Confirm") ?? FindWindowByTitle("Notepad");
                if (confirm is not null)
                {
                    var yes = FindButton(confirm, "Yes") ?? FindButton(confirm, "Replace");
                    if (yes is not null && TryInvoke(yes))
                    {
                        Thread.Sleep(500);
                        if (File.Exists(fullPath)) return true;
                    }
                }
                Thread.Sleep(200);
            }
            return File.Exists(fullPath);
        }
        catch (Exception ex) when (ex is ElementNotAvailableException or InvalidOperationException or COMException)
        {
            return File.Exists(fullPath);
        }
    }

    private static AutomationElement? FindButton(AutomationElement root, string nameContains)
    {
        try
        {
            var buttons = root.FindAll(
                TreeScope.Descendants,
                new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Button));
            foreach (AutomationElement button in buttons)
            {
                try
                {
                    if (button.Current.Name.Contains(nameContains, StringComparison.OrdinalIgnoreCase))
                        return button;
                }
                catch (ElementNotAvailableException)
                {
                    // Button closed mid-enumeration.
                }
            }
        }
        catch (Exception ex) when (ex is ElementNotAvailableException or InvalidOperationException or COMException)
        {
            // No button found.
        }
        return null;
    }

    private static bool TryInvoke(AutomationElement element)
    {
        try
        {
            if (element.TryGetCurrentPattern(InvokePattern.Pattern, out var patternObject)
                && patternObject is InvokePattern invokePattern)
            {
                invokePattern.Invoke();
                return true;
            }
        }
        catch (Exception ex) when (ex is ElementNotAvailableException or InvalidOperationException)
        {
            // Not invocable.
        }
        return false;
    }

    private static bool TryGetValue(AutomationElement element, out string? value)
    {
        value = null;
        try
        {
            if (element.TryGetCurrentPattern(ValuePattern.Pattern, out var patternObject)
                && patternObject is ValuePattern valuePattern)
            {
                value = valuePattern.Current.Value;
                return value is not null;
            }
        }
        catch (Exception ex) when (ex is ElementNotAvailableException or InvalidOperationException)
        {
            // Not readable.
        }
        return false;
    }

    /// <summary>Sends Ctrl+S via SendInput (Ctrl down, S down/up, Ctrl up).</summary>
    private static void SendCtrlS()
    {
        const ushort vkControl = 0x11;
        const ushort vkS = 0x53;
        var inputs = new[]
        {
            new INPUT { Type = InputKeyboard, U = new InputUnion { Ki = new KEYBDINPUT { WVk = vkControl } } },
            new INPUT { Type = InputKeyboard, U = new InputUnion { Ki = new KEYBDINPUT { WVk = vkS } } },
            new INPUT { Type = InputKeyboard, U = new InputUnion { Ki = new KEYBDINPUT { WVk = vkS, DwFlags = KeyeventfKeyUp } } },
            new INPUT { Type = InputKeyboard, U = new InputUnion { Ki = new KEYBDINPUT { WVk = vkControl, DwFlags = KeyeventfKeyUp } } },
        };
        SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<INPUT>());
    }

    /// <summary>Maximizes the first window whose title contains the text (via <see cref="WindowPattern"/>).</summary>
    public bool MaximizeWindow(string titleContains)
    {
        var window = FindWindowByTitle(titleContains);
        return window is not null && SetWindowVisualState(window, WindowVisualState.Maximized);
    }

    /// <summary>Minimizes the first window whose title contains the text (via <see cref="WindowPattern"/>).</summary>
    public bool MinimizeWindow(string titleContains)
    {
        var window = FindWindowByTitle(titleContains);
        return window is not null && SetWindowVisualState(window, WindowVisualState.Minimized);
    }

    private static bool SetWindowVisualState(AutomationElement window, WindowVisualState state)
    {
        try
        {
            if (window.TryGetCurrentPattern(WindowPattern.Pattern, out var patternObject)
                && patternObject is WindowPattern windowPattern
                && (state == WindowVisualState.Maximized ? windowPattern.Current.CanMaximize : windowPattern.Current.CanMinimize))
            {
                windowPattern.SetWindowVisualState(state);
                return true;
            }

            return false;
        }
        catch (Exception ex) when (ex is ElementNotAvailableException or InvalidOperationException)
        {
            return false;
        }
    }

    private static AutomationElement? FindWindowByTitle(string titleContains)
    {
        try
        {
            var windows = AutomationElement.RootElement.FindAll(
                TreeScope.Children,
                new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Window));
            foreach (AutomationElement window in windows)
            {
                try
                {
                    if (window.Current.Name.Contains(titleContains, StringComparison.OrdinalIgnoreCase))
                    {
                        return window;
                    }
                }
                catch (ElementNotAvailableException)
                {
                    // Window closed mid-enumeration.
                }
            }
        }
        catch (Exception ex) when (ex is ElementNotAvailableException or InvalidOperationException or COMException)
        {
            // No window found via UI Automation.
        }

        return null;
    }

    private static bool TrySetValue(AutomationElement element, string text)
    {
        try
        {
            if (element.TryGetCurrentPattern(ValuePattern.Pattern, out var patternObject)
                && patternObject is ValuePattern valuePattern
                && !valuePattern.Current.IsReadOnly)
            {
                valuePattern.SetValue(text);
                return true;
            }
        }
        catch (Exception ex) when (ex is ElementNotAvailableException or InvalidOperationException)
        {
            // Not settable; caller falls back.
        }

        return false;
    }

    private static IntPtr GetWindowHandle(AutomationElement window)
    {
        try
        {
            var value = window.GetCurrentPropertyValue(AutomationElement.NativeWindowHandleProperty);
            return value is int handle ? new IntPtr(handle) : IntPtr.Zero;
        }
        catch (Exception ex) when (ex is ElementNotAvailableException or InvalidOperationException)
        {
            return IntPtr.Zero;
        }
    }

    private static bool TypeTextViaSendInput(string text)
    {
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

    // ---- user32 P/Invoke ----

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
