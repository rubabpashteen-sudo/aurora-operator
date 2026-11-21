namespace AuroraOperator.Core.Ollama;

/// <summary>Outcome of ensuring Ollama is installed.</summary>
public sealed record OllamaInstallResult(bool Success, string Status, string Message);

/// <summary>
/// Detects an existing Ollama installation and, on Windows, describes how to
/// install it from the official source. This class never silently installs
/// untrusted software and never claims success when installation failed.
/// On non-Windows machines EnsureInstalledAsync honestly reports that the
/// Windows installer cannot run here.
/// </summary>
public static class OllamaInstaller
{
    public const string OllamaDownloadUrl = "https://ollama.com/download/OllamaSetup.exe";
    public const string OllamaHomePage = "https://ollama.com/download";

    public static bool IsOllamaInstalled()
    {
        if (FindOllamaExecutable() is not null) return true;
        return false;
    }

    public static string? FindOllamaExecutable()
    {
        var candidates = new List<string>();
        if (OperatingSystem.IsWindows())
        {
            var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            candidates.Add(Path.Combine(localAppData, "Programs", "Ollama", "ollama.exe"));
            candidates.Add(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Ollama", "ollama.exe"));
        }
        foreach (var c in candidates)
            if (File.Exists(c)) return c;

        var pathDirs = (Environment.GetEnvironmentVariable("PATH") ?? string.Empty)
            .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries);
        foreach (var dir in pathDirs)
        {
            var exe = Path.Combine(dir, OperatingSystem.IsWindows() ? "ollama.exe" : "ollama");
            if (File.Exists(exe)) return exe;
        }
        return null;
    }

    /// <summary>Checks for Ollama and verifies its API. On Windows without
    /// Ollama, returns Failed with the official download URL — the installer
    /// (NSIS/Inno) performs the actual download and install with progress UI.
    /// Never reports success unless the API actually answers.</summary>
    public static async Task<OllamaInstallResult> EnsureInstalledAsync(
        OllamaClient client, IProgress<string>? progress, CancellationToken ct)
    {
        var exe = FindOllamaExecutable();
        if (exe is null)
        {
            if (!OperatingSystem.IsWindows())
                return new OllamaInstallResult(false, "NotInstalled",
                    "Ollama is not installed on this machine, and the Windows installer (OllamaSetup.exe) cannot run on this OS. " +
                    "On Windows, download it from " + OllamaDownloadUrl);
            progress?.Report("Ollama not found. Download it from " + OllamaDownloadUrl);
            return new OllamaInstallResult(false, "NotInstalled",
                "Ollama is not installed. Download the official Windows installer from " + OllamaDownloadUrl +
                " and run it, then restart Aurora Operator.");
        }

        progress?.Report("Ollama found at " + exe + ". Checking its local API…");
        if (await client.IsAvailableAsync(ct).ConfigureAwait(false))
            return new OllamaInstallResult(true, "AlreadyInstalled",
                "Ollama is installed and its local API is responding at " + client.BaseUrl + ".");

        return new OllamaInstallResult(false, "ServiceNotRunning",
            "Ollama is installed at " + exe + " but its local API is not responding. " +
            "Start Ollama (it normally starts automatically on Windows) and try again.");
    }
}
