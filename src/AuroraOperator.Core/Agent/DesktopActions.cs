using System.Diagnostics;

namespace AuroraOperator.Core.Agent;

internal static class DesktopActions
{
    internal const string ToolName = "open-windows-app";

    internal static string? MatchRequest(string request) => request.Trim().TrimEnd('.', '!').ToLowerInvariant() switch
    {
        "open my computer" or "open this pc" or "my computer kholo" or "this pc kholo" or "mera computer kholo" => "this-pc",
        "open file explorer" or "open explorer" or "explorer kholo" => "explorer",
        "open calculator" or "calculator kholo" => "calculator",
        "open notepad" or "notepad kholo" => "notepad",
        "open chrome" or "open google chrome" or "chrome kholo" or "google chrome kholo" or "google chrome open karo" or "chrome open karo" => "chrome",
        _ => null,
    };

    internal static string? DisplayName(string target) => target switch
    {
        "this-pc" => "This PC",
        "explorer" => "File Explorer",
        "calculator" => "Calculator",
        "notepad" => "Notepad",
        "chrome" => "Google Chrome",
        _ => null,
    };

    internal static Task<ToolResult> OpenAsync(IReadOnlyDictionary<string, string> args, CancellationToken ct)
    {
        if (!OperatingSystem.IsWindows())
            return Task.FromResult(new ToolResult(false, "Opening desktop apps requires Windows."));

        string target = args["target"];
        var name = DisplayName(target);
        if (name is null)
            return Task.FromResult(new ToolResult(false, "This desktop target is unavailable."));

        string? executable = target switch
        {
            "chrome" => BrowserWorkflow.FindChrome(),
            "calculator" => Path.Combine(Environment.SystemDirectory, "calc.exe"),
            "notepad" => Path.Combine(Environment.SystemDirectory, "notepad.exe"),
            _ => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "explorer.exe"),
        };
        if (executable is null) return Task.FromResult(new ToolResult(false, "Google Chrome was not found."));
        var launch = new ProcessStartInfo(executable) { UseShellExecute = false };
        if (target == "this-pc") launch.ArgumentList.Add("shell:MyComputerFolder");
        ct.ThrowIfCancellationRequested();
        using var process = Process.Start(launch);
        return Task.FromResult(process is null
            ? new ToolResult(false, $"Windows did not accept the launch for {name}.")
            : new ToolResult(true, $"Sent the request to open {name} to Windows."));
    }
}
