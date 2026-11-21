using System.Text;

namespace AuroraOperator.Core.Tools;

/// <summary>
/// The Notepad flow, testable in Core against any <see cref="IAppControl"/>:
/// launch Notepad → wait for its window → focus it → type the real text →
/// verify by reading the text back (Windows UI Automation) or, after a
/// save, by comparing the saved file byte-for-byte with the input.
/// The UI flow is Windows-only: on any other OS <see cref="RunAsync"/>
/// returns a clear NOT SUPPORTED failure and never pretends Notepad ran.
/// Saving is different: when the user explicitly requested a save path,
/// <see cref="SaveAsync"/> falls back to a verified direct file write
/// (labelled as such in the result) when the Save dialog cannot be driven
/// — including on non-Windows, where it is honestly a file write, not a
/// Notepad save — and always verifies the file content afterwards.
/// </summary>
public static class NotepadWorkflow
{
    public static readonly TimeSpan DefaultWindowTimeout = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(200);

    /// <summary>
    /// Full Notepad UI flow. Windows-only; on non-Windows returns failure
    /// ("Windows-only (NOT SUPPORTED)"). Never simulates success.
    /// </summary>
    public static async Task<ToolResult> RunAsync(
        IAppControl control, string? text, string? savePath,
        CancellationToken ct, TimeSpan? windowTimeout = null)
    {
        ArgumentNullException.ThrowIfNull(control);
        if (string.IsNullOrEmpty(text))
            return new ToolResult(false, "no text provided: the Notepad workflow was given no text to type, so nothing was typed.");
        if (!OperatingSystem.IsWindows())
            return new ToolResult(false,
                "Notepad workflow is Windows-only (NOT SUPPORTED on this operating system): " +
                "it launches notepad.exe and types into its window with Windows UI Automation, " +
                "neither of which exists here. Nothing was launched, typed or saved.");

        if (!control.LaunchApp("notepad.exe"))
            return new ToolResult(false, "Failed to launch Notepad (notepad.exe did not start).");

        var found = await WaitForWindowAsync(control, "Notepad", windowTimeout ?? DefaultWindowTimeout, ct).ConfigureAwait(false);
        if (!found)
            return new ToolResult(false, "Notepad was launched but no window with 'Notepad' in its title appeared within the timeout.");

        if (!control.FocusWindow("Notepad"))
            return new ToolResult(false, "Could not bring the Notepad window to the foreground.");

        bool typed;
        try
        {
            typed = control.TypeText(text);
        }
        catch (PlatformNotSupportedException ex)
        {
            return new ToolResult(false, "Typing into Notepad is Windows-only: " + ex.Message);
        }
        if (!typed)
            return new ToolResult(false, "Typing the text into Notepad failed (no keystrokes were accepted).");

        // Verify by reading the text back through UI Automation.
        string? readBack = null;
        try { readBack = control.ReadTextFromFocusedControl() ?? control.ReadWindowText("Notepad"); }
        catch (PlatformNotSupportedException)
        {
            readBack = null; // No UI Automation read-back on this control.
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            readBack = null;
        }

        if (savePath is not null)
        {
            var saved = await SaveAsync(control, savePath, text, ct).ConfigureAwait(false);
            if (!saved.Success) return saved;
            // SaveAsync already verified the file content equals the input.
            return new ToolResult(true,
                $"Notepad text typed and saved to {savePath}; file content verified to match the typed text exactly. " + saved.Summary,
                OutputPath: savePath, Artifacts: new[] { savePath });
        }

        if (readBack is null)
            return new ToolResult(false,
                "The text was typed into Notepad but its content could not be read back to verify it, and no save path was requested to verify against.");
        if (!string.Equals(readBack, text, StringComparison.Ordinal))
            return new ToolResult(false,
                $"Typed text did not match the text read back from Notepad (typed {text.Length} characters, read back {readBack.Length}).");
        return new ToolResult(true, $"Typed {text.Length} characters into Notepad; read-back verified exact match.");
    }

    /// <summary>
    /// Saves Notepad text to <paramref name="path"/>. On Windows it first
    /// tries the real Save dialog via <see cref="IAppControl.SaveNotepadFile"/>;
    /// when that fails or is unavailable it uses the VERIFIED FALLBACK: a
    /// direct UTF-8 file write, clearly labelled in the result summary, and
    /// only because the caller explicitly requested this save path. The file
    /// is always verified afterwards (exists + content equals the input
    /// exactly). Returns failure — never a simulated success — when the
    /// content cannot be verified.
    /// </summary>
    public static Task<ToolResult> SaveAsync(IAppControl control, string path, string? text, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(control);
        if (string.IsNullOrWhiteSpace(path))
            return Task.FromResult(new ToolResult(false, "no save path provided: cannot save the Notepad text without a path."));
        if (path.Contains('\0'))
            return Task.FromResult(new ToolResult(false, "Save rejected: the path contains a null character and is not a valid file path."));

        // Text to save: the caller's text, else what is currently in Notepad.
        if (text is null)
        {
            try { text = control.ReadTextFromFocusedControl() ?? control.ReadWindowText("Notepad"); }
            catch (PlatformNotSupportedException) { text = null; }
            catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception) { text = null; }
        }
        if (text is null)
            return Task.FromResult(new ToolResult(false,
                "no text provided: nothing to save — no text argument was given and no text could be read back from Notepad."));
        ct.ThrowIfCancellationRequested();

        // 1. Try the real Notepad Save dialog (Windows only).
        if (OperatingSystem.IsWindows())
        {
            try
            {
                if (control.SaveNotepadFile(path) && VerifyFileContent(path, text))
                    return Task.FromResult(new ToolResult(true,
                        $"Saved via the Notepad Save dialog to {path}; file content verified to match exactly.",
                        OutputPath: path, Artifacts: new[] { path }));
            }
            catch (PlatformNotSupportedException)
            {
                // Fall through to the verified fallback below.
            }
            catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception or IOException or UnauthorizedAccessException)
            {
                // Fall through to the verified fallback below.
            }
        }

        // 2. VERIFIED FALLBACK — direct file write. Used only because the
        // user explicitly requested this save path, and only when the Save
        // dialog could not be driven (or on non-Windows, where Notepad does
        // not exist). The result says so plainly; the file is verified after.
        try
        {
            var dir = Path.GetDirectoryName(Path.GetFullPath(path));
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            File.WriteAllText(path, text, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return Task.FromResult(new ToolResult(false, $"Verified fallback file write to {path} failed: {ex.Message}"));
        }

        if (!VerifyFileContent(path, text))
            return Task.FromResult(new ToolResult(false,
                $"Saved to {path} but verification failed: the file content does not match the requested text exactly."));
        var via = OperatingSystem.IsWindows()
            ? "Notepad Save dialog automation did not complete; saved by verified direct file write (fallback) to "
            : "Notepad is Windows-only on this OS; saved by verified direct file write (fallback, not a Notepad save) to ";
        return Task.FromResult(new ToolResult(true,
            via + path + "; file content verified to match the requested text exactly.",
            OutputPath: path, Artifacts: new[] { path }));
    }

    /// <summary>Polls until a window title containing <paramref name="titleContains"/> appears, or the timeout elapses.</summary>
    public static async Task<bool> WaitForWindowAsync(
        IAppControl control, string titleContains, TimeSpan timeout, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(control);
        var deadline = DateTimeOffset.UtcNow + timeout;
        while (DateTimeOffset.UtcNow < deadline)
        {
            ct.ThrowIfCancellationRequested();
            IReadOnlyList<string> titles;
            try { titles = control.RunningAppTitles(); }
            catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception) { titles = Array.Empty<string>(); }
            if (titles.Any(t => t.Contains(titleContains, StringComparison.OrdinalIgnoreCase)))
                return true;
            try { await Task.Delay(PollInterval, ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { throw; }
        }
        return false;
    }

    /// <summary>True when the file exists and its UTF-8 content equals <paramref name="expected"/> exactly.</summary>
    public static bool VerifyFileContent(string path, string expected)
    {
        try
        {
            return File.Exists(path) &&
                string.Equals(File.ReadAllText(path, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false)), expected, StringComparison.Ordinal);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }
}
