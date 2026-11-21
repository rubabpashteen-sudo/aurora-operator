using AuroraOperator.Core.Agent;
using AuroraOperator.Core.Security;

namespace AuroraOperator.Core.Tools;

/// <summary>
/// The "app" tool: launches, focuses, closes and lists desktop
/// applications and types / reads text in them, backed by an
/// <see cref="IAppControl"/> (WindowsAppControl in the App, or
/// ProcessAppControl cross-platform). Every action reports what really
/// happened: a launch that did not start, a type that was not accepted,
/// or a read-back that did not match is a failure, never a success.
/// Typing and UI Automation read-back are Windows-only in the real
/// controls; on other operating systems those actions fail honestly.
/// </summary>
public sealed class AppTools : IAgentTool
{
    private readonly IAppControl _control;

    public AppTools(IAppControl? control = null) => _control = control ?? new ProcessAppControl();

    public string Name => "app";
    public string Description => "Launch, focus, close and list desktop applications; type and read back text (e.g. Notepad); save Notepad text to a file.";

    public async Task<ToolResult> ExecuteAsync(IReadOnlyDictionary<string, string> args, CancellationToken ct)
    {
        var action = (Get(args, "action") ?? string.Empty).Trim().ToLowerInvariant();
        try
        {
            ct.ThrowIfCancellationRequested();
            switch (action)
            {
                case "launch-app":
                case "launch":
                case "launch-notepad":
                {
                    var app = Get(args, "app") ?? Get(args, "name") ?? Get(args, "path");
                    if (string.IsNullOrWhiteSpace(app))
                        return new ToolResult(false, "launch-app needs an 'app' argument (e.g. app=notepad).");
                    if (action == "launch-notepad") app = "notepad";
                    // Phase 8 (F5): dangerous targets are refused outright,
                    // even if approved — a URL, script or installer is never
                    // launched through the agent. Unknown targets were gated
                    // behind approval by the RiskClassifier; the tool only
                    // hard-blocks the Rejected class here.
                    if (AppLaunchPolicy.Evaluate(app) == LaunchDecision.Rejected)
                        return new ToolResult(false, "Launch refused: " + AppLaunchPolicy.RejectionReason(app));
                    var resolved = ResolveAppName(app);
                    if (!_control.LaunchApp(resolved))
                        return new ToolResult(false, $"Failed to launch '{app}' (process did not start).");
                    return new ToolResult(true, $"Launched {app}.");
                }
                case "close-app":
                case "close":
                {
                    var name = Get(args, "app") ?? Get(args, "name") ?? Get(args, "path");
                    if (string.IsNullOrWhiteSpace(name))
                        return new ToolResult(false, "close-app needs an 'app' argument.");
                    // Phase 8 (F20): protected processes are refused
                    // outright, even if approved — approval authorises
                    // closing an ordinary app, never killing a system
                    // process. The classifier also gates close-app behind
                    // per-request approval, and the approval dialog must
                    // name the exact target (see below).
                    if (ProcessSafety.IsProtectedProcessName(name))
                        return new ToolResult(false, $"Refusing to close '{name}': it is a protected system process and can never be terminated by the agent.");
                    // Final release (F20): close by PID, never by bare
                    // name when several processes share it. An explicit
                    // pid= closes exactly that process (its real name
                    // must match the named app). Without a pid, the
                    // control lists the matches: exactly one safe match
                    // may close; two or more close NOTHING and the
                    // failure lists the PIDs to pick from.
                    // Force termination is a separately approved action:
                    // it is only taken when the approved step's args
                    // contain force=true (bound by the approval's
                    // parameter hash); a graceful close that fails
                    // otherwise reports "still running" and stops.
                    var force = string.Equals(Get(args, "force"), "true", StringComparison.OrdinalIgnoreCase)
                        || string.Equals(Get(args, "force"), "1", StringComparison.Ordinal)
                        || string.Equals(Get(args, "force"), "yes", StringComparison.OrdinalIgnoreCase);
                    var pidText = Get(args, "pid");
                    if (pidText is not null && int.TryParse(pidText, out var pid))
                    {
                        var (ok, message) = AppProcessCloser.CloseProcessById(pid, force, name);
                        return ok
                            ? new ToolResult(true, message)
                            : new ToolResult(false, message);
                    }
                    if (pidText is not null)
                        return new ToolResult(false, $"close-app: pid='{pidText}' is not a valid process ID.");
                    var matches = _control.FindProcesses(name);
                    var safe = matches.Where(m => m.SafeToClose).ToList();
                    if (safe.Count == 0)
                        return matches.Count == 0
                            ? new ToolResult(false, $"No running application named '{name}' was found to close.")
                            : new ToolResult(false, $"All {matches.Count} matching process(es) named '{name}' are protected system processes and can never be terminated by the agent.");
                    if (safe.Count > 1)
                    {
                        var list = string.Join(", ", safe.Select(m =>
                            $"PID {m.Id}" + (m.ExecutablePath is null ? string.Empty : $" ({m.ExecutablePath})")));
                        return new ToolResult(false,
                            $"Multiple processes named '{name}' are running ({list}). No process was closed: close one by PID — re-run close-app with app='{name}' and pid=<PID>.");
                    }
                    var target = safe[0];
                    var (closed, closeMessage) = AppProcessCloser.CloseProcessById(target.Id, force, name);
                    return closed
                        ? new ToolResult(true, closeMessage)
                        : new ToolResult(false, closeMessage);
                }
                case "focus":
                {
                    var title = Get(args, "title") ?? Get(args, "app") ?? Get(args, "text");
                    if (string.IsNullOrWhiteSpace(title))
                        return new ToolResult(false, "focus needs a 'title' argument.");
                    if (!_control.FocusWindow(title))
                        return new ToolResult(false, $"No window with '{title}' in its title could be brought to the foreground.");
                    return new ToolResult(true, $"Focused the window containing '{title}'.");
                }
                case "list":
                {
                    var titles = _control.RunningAppTitles();
                    return new ToolResult(true, $"Found {titles.Count} window(s).",
                        Details: string.Join("\n", titles));
                }
                case "type-text":
                case "type":
                {
                    var text = Get(args, "text");
                    if (string.IsNullOrEmpty(text))
                        return new ToolResult(false, "no text provided: type-text needs a 'text' argument and none was supplied, so nothing was typed.");
                    bool typed;
                    try { typed = _control.TypeText(text); }
                    catch (PlatformNotSupportedException ex)
                    {
                        return new ToolResult(false, "TypeText is Windows-only: " + ex.Message);
                    }
                    if (!typed)
                        return new ToolResult(false, "Typing the text failed (no keystrokes were accepted).");
                    // Read-back verification where the platform allows it.
                    string? readBack = null;
                    try { readBack = _control.ReadTextFromFocusedControl(); }
                    catch (PlatformNotSupportedException) { readBack = null; }
                    catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception) { readBack = null; }
                    if (readBack is not null && !string.Equals(readBack, text, StringComparison.Ordinal))
                        return new ToolResult(false,
                            $"Typed text did not match the text read back (typed {text.Length} characters, read back {readBack.Length}).");
                    return new ToolResult(true, readBack is not null
                        ? $"Typed {text.Length} characters; read-back verified exact match."
                        : $"Typed {text.Length} characters. (Read-back verification is unavailable on this platform/control.)");
                }
                case "read-text":
                {
                    string? read;
                    try { read = _control.ReadTextFromFocusedControl(); }
                    catch (PlatformNotSupportedException ex)
                    {
                        return new ToolResult(false, "Reading control text is Windows-only: " + ex.Message);
                    }
                    if (read is null)
                        return new ToolResult(false, "Could not read any text from the focused control.");
                    return new ToolResult(true, $"Read {read.Length} characters from the focused control.", Details: read);
                }
                case "verify-text":
                {
                    var expected = Get(args, "text") ?? Get(args, "expected");
                    if (expected is null)
                        return new ToolResult(false, "verify-text needs a 'text' argument with the expected text.");
                    string? read;
                    try { read = _control.ReadTextFromFocusedControl() ?? _control.ReadWindowText(Get(args, "title") ?? "Notepad"); }
                    catch (PlatformNotSupportedException ex)
                    {
                        return new ToolResult(false, "Reading control text is Windows-only: " + ex.Message);
                    }
                    if (read is null)
                        return new ToolResult(false, "Could not read any text to verify against.");
                    if (!string.Equals(read, expected, StringComparison.Ordinal))
                        return new ToolResult(false, $"Text in the window does not match the expected text (read {read.Length} characters, expected {expected.Length}).");
                    return new ToolResult(true, "Text verified: the window content matches the expected text exactly.", Details: read);
                }
                case "save-as":
                case "save":
                {
                    var path = Get(args, "path") ?? Get(args, "destination");
                    if (string.IsNullOrWhiteSpace(path))
                        return new ToolResult(false, "save-as needs a 'path' argument.");
                    return await NotepadWorkflow.SaveAsync(_control, path, Get(args, "text"), ct).ConfigureAwait(false);
                }
                case "notepad-write":
                {
                    var text = Get(args, "text");
                    if (string.IsNullOrEmpty(text))
                        return new ToolResult(false, "no text provided: notepad-write needs a 'text' argument and none was supplied, so nothing was typed.");
                    return await NotepadWorkflow.RunAsync(_control, text, Get(args, "path") ?? Get(args, "destination"), ct).ConfigureAwait(false);
                }
                default:
                    return new ToolResult(false,
                        $"Unknown app action '{action}'. Supported: launch-app, close-app, focus, list, type-text, read-text, verify-text, save-as, notepad-write.");
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return new ToolResult(false, $"App action '{action}' failed: {ex.Message}", ex.ToString());
        }
    }

    private static string? Get(IReadOnlyDictionary<string, string> args, string key)
    {
        foreach (var kv in args)
            if (string.Equals(kv.Key, key, StringComparison.OrdinalIgnoreCase))
                return kv.Value;
        return null;
    }

    /// <summary>Resolves friendly app names to the executable Windows starts (notepad → notepad.exe). Unknown names pass through unchanged.</summary>
    internal static string ResolveAppName(string app)
    {
        if (!OperatingSystem.IsWindows()) return app;
        return app.Trim().ToLowerInvariant() switch
        {
            "notepad" => "notepad.exe",
            "calculator" => "calc.exe",
            "paint" => "mspaint.exe",
            "wordpad" => "wordpad.exe",
            _ => app,
        };
    }
}
