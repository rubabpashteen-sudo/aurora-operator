using System.Text;
using AuroraOperator.Core.Agent;
using Microsoft.Playwright;

namespace AuroraOperator.Core.Tools;

/// <summary>
/// Playwright-based web research tool (Name="browser").
/// Launches headless Chromium, searches Bing for each query, extracts the
/// page title and first paragraphs, and writes a sourced markdown report.
/// Never bypasses CAPTCHAs or logins: a page that shows a CAPTCHA is
/// recorded honestly as blocked and research continues with the next query.
///
/// Browser automation readiness (Phase 7, 2026-11-21):
///   - Present and wired: Chromium launch (headless, 60 s timeout),
///     navigation (Bing search, DOMContentLoaded, 30 s timeout),
///     text extraction (title + first paragraphs via a "p" locator),
///     per-query error recovery (one failed/blocked query never aborts
///     the report), page close in a finally block, browser disposal via
///     "await using", Playwright disposal via "using", honest failure
///     (never fake success) when Playwright/Chromium is missing, and
///     download handling (action=download, validated URL + path).
///   - Missing / limited: no Chromium-installation bootstrap is run
///     automatically — <see cref="ChromiumInstallCommand"/> is reported
///     in the failure message and <see cref="CheckBrowserReadiness"/>
///     detects the Playwright browser cache; no click/form interaction
///     (research is deliberately read-only); no login handling.
///   - NOT VERIFIED: no Chromium is installed on this Linux VM
///     (~/.cache/ms-playwright does not exist), so ResearchAsync and
///     DownloadAsync have never executed against a real browser.
///     Chromium runtime behaviour must be verified on Windows after
///     "playwright install chromium".
/// </summary>
public sealed class BrowserTools : IAgentTool
{
    private static readonly string[] CaptchaMarkers =
    {
        "captcha",
        "verify you are human",
        "verify you're human",
        "are you a robot",
        "unusual traffic",
        "please verify",
        "human verification",
        "robot check",
    };

    /// <summary>
    /// Command that installs the Chromium browser Playwright needs.
    /// Reported to the user whenever Chromium is missing; Aurora never
    /// pretends research succeeded without it.
    /// </summary>
    public const string ChromiumInstallCommand = "playwright install chromium";

    /// <summary>
    /// Human-readable install instructions, naming the Windows
    /// PowerShell form (playwright.ps1 ships next to the app) as well
    /// as the plain CLI form.
    /// </summary>
    public const string ChromiumInstallHint =
        "Install Chromium for Playwright with 'playwright install chromium' " +
        "(on Windows PowerShell: pwsh playwright.ps1 install chromium, run from the app folder). " +
        "No results are fabricated when Chromium is missing.";

    /// <summary>
    /// Detects whether Playwright's Chromium is installed by looking for
    /// the Playwright browser cache (a "chromium-*" directory holding a
    /// chrome binary). Checks PLAYWRIGHT_BROWSERS_PATH first, then
    /// "%USERPROFILE%/.cache/ms-playwright". A system Chrome installed
    /// outside Playwright is NOT counted: research uses Playwright's
    /// bundled Chromium, so only that cache proves readiness.
    /// </summary>
    public static bool IsChromiumInstalled()
    {
        var roots = new List<string>();
        var envPath = Environment.GetEnvironmentVariable("PLAYWRIGHT_BROWSERS_PATH");
        if (!string.IsNullOrWhiteSpace(envPath))
        {
            roots.Add(envPath);
        }

        var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (!string.IsNullOrWhiteSpace(profile))
        {
            roots.Add(Path.Combine(profile, ".cache", "ms-playwright"));
        }

        foreach (var root in roots)
        {
            try
            {
                if (!Directory.Exists(root))
                {
                    continue;
                }

                foreach (var dir in Directory.EnumerateDirectories(root, "chromium-*"))
                {
                    // Chromium 133 (Playwright 1.50) ships chrome.exe /
                    // chrome (or headless_shell) somewhere under the
                    // versioned directory.
                    if (Directory.EnumerateFiles(dir, "chrome*", SearchOption.AllDirectories).Any()
                        || Directory.EnumerateFiles(dir, "headless_shell*", SearchOption.AllDirectories).Any())
                    {
                        return true;
                    }
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // An unreadable cache is treated as "not installed";
                // the launch attempt below still reports the real error.
            }
        }

        return false;
    }

    /// <summary>
    /// Non-browser readiness check: reports whether Playwright's Chromium
    /// cache is present, with install instructions when it is not.
    /// This never launches a browser and never fabricates a result.
    /// </summary>
    public static (bool Ready, string Detail) CheckBrowserReadiness()
    {
        if (IsChromiumInstalled())
        {
            return (true, "Playwright Chromium is installed (browser cache found).");
        }

        return (false,
            "Playwright Chromium is not installed (no chromium-* browser cache found). " + ChromiumInstallHint);
    }

    /// <summary>Builds the Bing search URL for a query (no browser needed).</summary>
    public static string BuildSearchUrl(string query) =>
        "https://www.bing.com/search?q=" + Uri.EscapeDataString(query);

    /// <summary>
    /// Reports whether a page looks like a CAPTCHA / human-verification
    /// challenge. Blocked pages are recorded as blocked, never bypassed.
    /// </summary>
    public static bool IsBlockedPage(string title, string bodyText)
    {
        var haystack = ((title ?? string.Empty) + "\n" + (bodyText ?? string.Empty)).ToLowerInvariant();
        return CaptchaMarkers.Any(marker => haystack.Contains(marker, StringComparison.Ordinal));
    }

    /// <summary>
    /// Validates a download URL without fetching anything. Only absolute
    /// http(s) URLs with a host are accepted; file://, javascript:,
    /// data:, ftp: and URLs carrying embedded credentials are rejected.
    /// </summary>
    public static bool TryValidateDownloadUrl(string? url, out Uri? uri, out string error)
    {
        uri = null;
        error = string.Empty;
        if (string.IsNullOrWhiteSpace(url))
        {
            error = "No download URL supplied.";
            return false;
        }

        if (!Uri.TryCreate(url.Trim(), UriKind.Absolute, out uri) || uri is null)
        {
            error = $"The download URL '{url}' is not a valid absolute URL.";
            uri = null;
            return false;
        }

        if (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps)
        {
            error = $"Downloads only support http(s) URLs; '{uri.Scheme}' is not allowed.";
            uri = null;
            return false;
        }

        if (string.IsNullOrWhiteSpace(uri.Host))
        {
            error = "The download URL has no host.";
            uri = null;
            return false;
        }

        if (!string.IsNullOrEmpty(uri.UserInfo))
        {
            error = "Download URLs must not carry embedded credentials.";
            uri = null;
            return false;
        }

        return true;
    }

    /// <summary>
    /// Default download destination: the signed-in user's Downloads
    /// folder. Browser downloads are contained here unless the caller
    /// supplies an explicit <c>downloadDirectory</c> inside the user
    /// profile (see <see cref="IsInsideUserProfile"/>). Never derived
    /// from the requested file path — that would make containment vacuous.
    /// </summary>
    public static string DefaultDownloadDirectory =>
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            "Downloads");

    /// <summary>True when <paramref name="path"/> resolves inside the
    /// signed-in user's profile directory (itself, or any sub-path).</summary>
    public static bool IsInsideUserProfile(string? path, out string error)
    {
        error = string.Empty;
        if (string.IsNullOrWhiteSpace(path))
        {
            error = "No directory supplied.";
            return false;
        }

        if (path.Contains('\0'))
        {
            error = "Directory contains a NUL byte and is rejected.";
            return false;
        }

        string fullProfile;
        string fullPath;
        try
        {
            fullProfile = Path.GetFullPath(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));
            fullPath = Path.GetFullPath(path);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException)
        {
            error = $"Directory is not a valid path: {ex.Message}";
            return false;
        }

        if (!Path.IsPathRooted(fullPath))
        {
            error = "Directory must be an absolute path.";
            return false;
        }

        var comparison = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;
        var profileWithSeparator = fullProfile.EndsWith(Path.DirectorySeparatorChar)
            ? fullProfile
            : fullProfile + Path.DirectorySeparatorChar;
        if (!string.Equals(fullPath, fullProfile, comparison) &&
            !fullPath.StartsWith(profileWithSeparator, comparison))
        {
            error = $"Directory '{fullPath}' is outside the user profile '{fullProfile}'.";
            return false;
        }

        return true;
    }

    /// <summary>
    /// Validates a download destination without touching the browser or
    /// the network. The path must be absolute, free of NUL bytes, have a
    /// file name, and stay inside <paramref name="downloadDirectory"/>
    /// (no ".." traversal escaping it).
    /// </summary>
    public static bool ValidateDownloadPath(string? path, string? downloadDirectory, out string error)
    {
        error = string.Empty;
        if (string.IsNullOrWhiteSpace(path))
        {
            error = "No download destination path supplied.";
            return false;
        }

        if (path.Contains('\0'))
        {
            error = "Download path contains a NUL byte and is rejected.";
            return false;
        }

        if (string.IsNullOrWhiteSpace(downloadDirectory))
        {
            error = "No download directory supplied for validation.";
            return false;
        }

        string fullPath;
        string fullDirectory;
        try
        {
            fullPath = Path.GetFullPath(path);
            fullDirectory = Path.GetFullPath(downloadDirectory);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException)
        {
            error = $"Download path is not a valid path: {ex.Message}";
            return false;
        }

        if (!Path.IsPathRooted(fullPath))
        {
            error = "Download path must be absolute.";
            return false;
        }

        if (string.IsNullOrWhiteSpace(Path.GetFileName(fullPath)))
        {
            error = "Download path must name a file, not just a directory.";
            return false;
        }

        var comparison = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;
        if (string.Equals(fullPath, fullDirectory, comparison))
        {
            error = "Download path must name a file, not just a directory.";
            return false;
        }

        var directoryWithSeparator = fullDirectory.EndsWith(Path.DirectorySeparatorChar)
            ? fullDirectory
            : fullDirectory + Path.DirectorySeparatorChar;
        if (!fullPath.StartsWith(directoryWithSeparator, comparison))
        {
            error = $"Download path '{fullPath}' escapes the download directory '{fullDirectory}'.";
            return false;
        }

        return true;
    }

    public string Name => "browser";

    public string Description =>
        "Researches topics on the web with headless Chromium and writes a sourced markdown report. " +
        "Args: action=research, queries (one per line, or separated by ';'), outputPath. " +
        "Also downloads a file: action=download, url, outputPath (http(s) only, destination must stay inside the user's Downloads folder, or an explicit downloadDirectory inside the user profile). " +
        "Chromium runtime NOT VERIFIED on this machine — see class docs.";

    public Task<ToolResult> ExecuteAsync(IReadOnlyDictionary<string, string> args, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(args);
        var action = GetArg(args, "action") ?? "research";
        if (string.Equals(action, "download", StringComparison.OrdinalIgnoreCase))
        {
            var url = GetArg(args, "url");
            if (!TryValidateDownloadUrl(url, out var uri, out var urlError))
            {
                return Task.FromResult(new ToolResult(false, "Download refused: " + urlError, null));
            }

            var downloadPath = GetArg(args, "outputPath") ?? GetArg(args, "output");
            if (string.IsNullOrWhiteSpace(downloadPath))
            {
                return Task.FromResult(new ToolResult(
                    false,
                    "Download refused: no outputPath supplied.",
                    "Pass outputPath naming the file to save (inside its own directory)."));
            }

            // SECURITY (Phase 7, finding F8): downloads are contained to a
            // fixed directory — the user's Downloads folder — unless the
            // caller supplies an explicit downloadDirectory that itself
            // sits inside the user profile. Deriving the "allowed"
            // directory from the requested path (the old code) made the
            // containment check vacuous: any path passed it.
            var downloadDirectory = GetArg(args, "downloadDirectory");
            if (string.IsNullOrWhiteSpace(downloadDirectory))
            {
                downloadDirectory = DefaultDownloadDirectory;
            }
            else if (!IsInsideUserProfile(downloadDirectory, out var profileError))
            {
                return Task.FromResult(new ToolResult(false,
                    "Download refused: " + profileError, null));
            }

            if (!ValidateDownloadPath(downloadPath, downloadDirectory, out var pathError))
            {
                return Task.FromResult(new ToolResult(false, "Download refused: " + pathError, null));
            }

            return DownloadAsync(uri!.ToString(), downloadPath, ct);
        }

        if (!string.Equals(action, "research", StringComparison.OrdinalIgnoreCase))
        {
            return Task.FromResult(new ToolResult(
                false,
                $"Unknown browser action '{action}'.",
                "Supported actions: research, download."));
        }

        var queries = ParseQueries(args);
        if (queries.Count == 0)
        {
            return Task.FromResult(new ToolResult(
                false,
                "No research queries supplied.",
                "Pass queries (one per line or separated by ';') or a single 'query' argument."));
        }

        var outputPath = GetArg(args, "outputPath") ?? GetArg(args, "output")
            ?? Path.Combine(Path.GetTempPath(), "aurora-research-report.md");
        return ResearchAsync(queries, outputPath, ct);
    }

    /// <summary>
    /// Researches each query in headless Chromium and writes a sourced
    /// markdown report to <paramref name="outputPath"/>. Returns failure
    /// (never fake success) when Chromium is missing or every query fails.
    /// </summary>
    public async Task<ToolResult> ResearchAsync(
        IReadOnlyList<string> queries,
        string outputPath,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(queries);
        ArgumentException.ThrowIfNullOrWhiteSpace(outputPath);
        if (outputPath.Contains('\0'))
        {
            return new ToolResult(false, "Web research failed: outputPath contains a NUL byte and is rejected.", null);
        }

        if (queries.Count == 0)
        {
            return new ToolResult(false, "No research queries supplied.", "ResearchAsync received an empty query list.");
        }

        var directory = Path.GetDirectoryName(Path.GetFullPath(outputPath));
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var report = new StringBuilder();
        report.AppendLine("# Research Report");
        report.AppendLine();
        report.AppendLine($"Generated: {DateTimeOffset.UtcNow:yyyy-MM-dd HH:mm:ss} UTC");
        report.AppendLine();

        var succeeded = 0;
        var blocked = 0;
        var failed = 0;

        IPlaywright playwright;
        try
        {
            playwright = await Playwright.CreateAsync().ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is PlaywrightException or InvalidOperationException or IOException)
        {
            return new ToolResult(
                false,
                "Web research failed: Playwright/Chromium is not available on this machine.",
                $"Playwright could not start. {ex.GetType().Name}: {ex.Message} " +
                "Install Playwright browsers with 'playwright install chromium' and retry. No results were fabricated.");
        }

        using (playwright)
        {
            IBrowser browser;
            try
            {
                browser = await playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions
                {
                    Headless = true,
                    Timeout = 60_000,
                }).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is PlaywrightException or TimeoutException)
            {
                var (_, readiness) = CheckBrowserReadiness();
                return new ToolResult(
                    false,
                    "Web research failed: Chromium could not be launched.",
                    $"{ex.GetType().Name}: {ex.Message} {readiness} No results were fabricated.");
            }

            await using (browser)
            {
                foreach (var query in queries)
                {
                    ct.ThrowIfCancellationRequested();
                    var result = await ResearchOneQueryAsync(browser, query, ct).ConfigureAwait(false);
                    report.AppendLine($"## {query}");
                    report.AppendLine();
                    report.AppendLine($"- Source: {result.Url}");
                    report.AppendLine($"- Status: {result.Status}");
                    if (!string.IsNullOrWhiteSpace(result.Title))
                    {
                        report.AppendLine($"- Page title: {result.Title}");
                    }

                    report.AppendLine();
                    if (result.Paragraphs.Count > 0)
                    {
                        foreach (var paragraph in result.Paragraphs)
                        {
                            report.AppendLine(paragraph);
                            report.AppendLine();
                        }
                    }
                    else
                    {
                        report.AppendLine(result.Note);
                        report.AppendLine();
                    }

                    switch (result.Status)
                    {
                        case "OK": succeeded++; break;
                        case "Blocked": blocked++; break;
                        default: failed++; break;
                    }
                }
            }
        }

        await File.WriteAllTextAsync(outputPath, report.ToString(), ct).ConfigureAwait(false);

        if (succeeded == 0)
        {
            var reason = blocked > 0
                ? $"{blocked} quer{(blocked == 1 ? "y was" : "ies were")} blocked (CAPTCHA/verification page) and {failed} failed."
                : $"{failed} quer{(failed == 1 ? "y" : "ies")} failed.";
            return new ToolResult(
                false,
                $"Web research produced no usable results: {reason}",
                $"A partial report was still written to {outputPath} recording the blocked/failed status of each query. No content was fabricated.",
                outputPath);
        }

        return new ToolResult(
            true,
            $"Researched {succeeded} of {queries.Count} quer{(queries.Count == 1 ? "y" : "ies")} successfully" +
            (blocked > 0 ? $"; {blocked} blocked (CAPTCHA/verification) and recorded as such" : string.Empty) +
            (failed > 0 ? $"; {failed} failed" : string.Empty) +
            $". Report written to {outputPath}.",
            null,
            outputPath);
    }

    /// <summary>
    /// Downloads a single file in headless Chromium and saves it to
    /// <paramref name="outputPath"/> (already validated by the caller:
    /// http(s) URL, destination inside its own directory). Returns an
    /// honest failure — never a fake success — when Chromium is missing,
    /// the navigation does not start a download, or the save fails.
    /// NOT VERIFIED: no Chromium on this Linux VM; verify on Windows
    /// after "playwright install chromium".
    /// </summary>
    public async Task<ToolResult> DownloadAsync(
        string url,
        string outputPath,
        CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(url);
        ArgumentException.ThrowIfNullOrWhiteSpace(outputPath);
        if (!TryValidateDownloadUrl(url, out var uri, out var urlError))
        {
            return new ToolResult(false, "Download refused: " + urlError, null);
        }

        // Resolve the containing directory only for paths that can
        // legally be resolved: GetFullPath throws on a NUL byte, so let
        // ValidateDownloadPath report that instead of throwing.
        string? directory = null;
        if (!outputPath.Contains('\0'))
        {
            directory = Path.GetDirectoryName(Path.GetFullPath(outputPath));
        }

        if (!ValidateDownloadPath(outputPath, directory, out var pathError))
        {
            return new ToolResult(false, "Download refused: " + pathError, null);
        }

        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        IPlaywright playwright;
        try
        {
            playwright = await Playwright.CreateAsync().ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is PlaywrightException or InvalidOperationException or IOException)
        {
            return new ToolResult(
                false,
                "Download failed: Playwright/Chromium is not available on this machine.",
                $"Playwright could not start. {ex.GetType().Name}: {ex.Message} {ChromiumInstallHint}");
        }

        using (playwright)
        {
            IBrowser browser;
            try
            {
                browser = await playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions
                {
                    Headless = true,
                    Timeout = 60_000,
                }).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is PlaywrightException or TimeoutException)
            {
                var (_, readiness) = CheckBrowserReadiness();
                return new ToolResult(
                    false,
                    "Download failed: Chromium could not be launched.",
                    $"{ex.GetType().Name}: {ex.Message} {readiness}");
            }

            await using (browser)
            {
                IPage? page = null;
                try
                {
                    page = await browser.NewPageAsync().ConfigureAwait(false);
                    var download = await page.RunAndWaitForDownloadAsync(
                        async () => await page.GotoAsync(uri!.ToString(), new PageGotoOptions
                        {
                            WaitUntil = WaitUntilState.DOMContentLoaded,
                            Timeout = 30_000,
                        }).ConfigureAwait(false),
                        new PageRunAndWaitForDownloadOptions { Timeout = 120_000 }).ConfigureAwait(false);
                    await download.SaveAsAsync(outputPath).ConfigureAwait(false);

                    var saved = new FileInfo(outputPath);
                    if (!saved.Exists || saved.Length == 0)
                    {
                        return new ToolResult(
                            false,
                            "Download failed: the file was not saved or is empty.",
                            $"Chromium reported a download from {uri} but '{outputPath}' is missing or empty after saving.");
                    }

                    return new ToolResult(
                        true,
                        $"Downloaded '{download.SuggestedFilename}' ({saved.Length} bytes) to {outputPath}.",
                        null,
                        outputPath);
                }
                catch (Exception ex) when (ex is PlaywrightException or TimeoutException or IOException)
                {
                    return new ToolResult(
                        false,
                        "Download failed: no download started or the download did not complete.",
                        $"{ex.GetType().Name}: {ex.Message} The page may not offer a file download at that URL.");
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                finally
                {
                    if (page is not null)
                    {
                        try
                        {
                            await page.CloseAsync().ConfigureAwait(false);
                        }
                        catch (PlaywrightException)
                        {
                            // Best-effort cleanup.
                        }
                    }
                }
            }
        }
    }

    private static async Task<QueryOutcome> ResearchOneQueryAsync(IBrowser browser, string query, CancellationToken ct)
    {
        var searchUrl = BuildSearchUrl(query);
        IPage? page = null;
        try
        {
            page = await browser.NewPageAsync().ConfigureAwait(false);
            var response = await page.GotoAsync(searchUrl, new PageGotoOptions
            {
                WaitUntil = WaitUntilState.DOMContentLoaded,
                Timeout = 30_000,
            }).ConfigureAwait(false);
            _ = response;

            var title = string.Empty;
            try
            {
                title = await page.TitleAsync().ConfigureAwait(false) ?? string.Empty;
            }
            catch (PlaywrightException)
            {
                // Title is best-effort; paragraph extraction below still runs.
            }

            string bodyText;
            try
            {
                bodyText = await page.ContentAsync().ConfigureAwait(false) ?? string.Empty;
            }
            catch (PlaywrightException)
            {
                bodyText = string.Empty;
            }

            if (IsBlockedPage(title, bodyText))
            {
                return new QueryOutcome(searchUrl, "Blocked", title,
                    Array.Empty<string>(),
                    "Blocked: the search page presented a CAPTCHA/verification challenge. " +
                    "Aurora Operator does not bypass CAPTCHAs or logins, so no content was collected for this query.");
            }

            var paragraphs = new List<string>();
            try
            {
                var texts = await page.Locator("p").AllInnerTextsAsync().ConfigureAwait(false);
                foreach (var text in texts)
                {
                    var cleaned = (text ?? string.Empty).Trim();
                    if (cleaned.Length >= 40 && paragraphs.Count < 5)
                    {
                        paragraphs.Add(cleaned);
                    }
                }
            }
            catch (PlaywrightException)
            {
                // Paragraph extraction is best-effort.
            }

            if (paragraphs.Count == 0)
            {
                return new QueryOutcome(searchUrl, "Failed", title,
                    Array.Empty<string>(),
                    "No readable paragraphs were found on the search results page.");
            }

            return new QueryOutcome(searchUrl, "OK", title, paragraphs, string.Empty);
        }
        catch (Exception ex) when (ex is PlaywrightException or TimeoutException or IOException)
        {
            return new QueryOutcome(searchUrl, "Failed", string.Empty,
                Array.Empty<string>(),
                $"Navigation or extraction failed: {ex.GetType().Name}: {ex.Message}");
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        finally
        {
            if (page is not null)
            {
                try
                {
                    await page.CloseAsync().ConfigureAwait(false);
                }
                catch (PlaywrightException)
                {
                    // Best-effort cleanup.
                }
            }
        }
    }

    private static bool LooksLikeCaptcha(string title, string bodyText) =>
        IsBlockedPage(title, bodyText);

    private static IReadOnlyList<string> ParseQueries(IReadOnlyDictionary<string, string> args)
    {
        var raw = GetArg(args, "queries") ?? GetArg(args, "query");
        if (string.IsNullOrWhiteSpace(raw))
        {
            return Array.Empty<string>();
        }

        return raw
            .Split(new[] { '\n', '\r', ';' }, StringSplitOptions.RemoveEmptyEntries)
            .Select(q => q.Trim())
            .Where(q => q.Length > 0)
            .ToArray();
    }

    private static string? GetArg(IReadOnlyDictionary<string, string> args, string key) =>
        args.TryGetValue(key, out var value) && !string.IsNullOrWhiteSpace(value) ? value : null;

    private sealed record QueryOutcome(
        string Url,
        string Status,
        string Title,
        IReadOnlyList<string> Paragraphs,
        string Note);
}
