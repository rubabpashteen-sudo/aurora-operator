using AuroraOperator.Core.Tools;

namespace AuroraOperator.Tests;

/// <summary>
/// Phase 7 browser-automation readiness tests.
///
/// Only the non-browser parts are tested here: URL validation,
/// download-path validation, blocked-page (CAPTCHA) detection, search-URL
/// building, Chromium readiness detection, and the missing-Chromium
/// error path of ResearchAsync (which must fail honestly, never fake
/// success). No test launches a real browser: no Chromium is installed
/// on this VM (~/.cache/ms-playwright does not exist), and the Chromium
/// runtime itself is NOT VERIFIED — see the manual Windows pass in
/// TESTS.md.
/// </summary>
public sealed class BrowserPhase7Tests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "aurora-browser7-" + Guid.NewGuid().ToString("N"));
    public BrowserPhase7Tests() => Directory.CreateDirectory(_root);
    public void Dispose() { try { Directory.Delete(_root, true); } catch { } }

    // ---- Search URL ------------------------------------------------------

    [Fact]
    public void BuildSearchUrl_EncodesQuery_AndPointsAtBing()
    {
        var url = BrowserTools.BuildSearchUrl("electric bikes under $1000");
        Assert.StartsWith("https://www.bing.com/search?q=", url, StringComparison.Ordinal);
        Assert.Contains("electric%20bikes", url, StringComparison.Ordinal);
        Assert.DoesNotContain(" ", url);
        Assert.DoesNotContain("$1000", url);
    }

    // ---- Blocked-page (CAPTCHA) detection --------------------------------

    [Theory]
    [InlineData("Verify you are human", "")]
    [InlineData("", "please complete the captcha challenge")]
    [InlineData("Are you a robot?", "search results")]
    [InlineData("", "unusual traffic from your computer network")]
    [InlineData("Human verification", "")]
    [InlineData("", "robot check required")]
    public void IsBlockedPage_CaptchaMarkers_Detected(string title, string body)
    {
        Assert.True(BrowserTools.IsBlockedPage(title, body));
    }

    [Theory]
    [InlineData("Electric bikes - search results", "Best electric bikes under $1000 reviewed here.")]
    [InlineData("", "")]
    public void IsBlockedPage_CleanPages_NotBlocked(string title, string body)
    {
        // NOTE: the detector is deliberately conservative — any page
        // containing a marker word (even in prose) is reported Blocked.
        // A false "Blocked" costs one query; a missed CAPTCHA would mean
        // collecting a verification page as if it were content.
        Assert.False(BrowserTools.IsBlockedPage(title, body));
    }

    // ---- Download URL validation -----------------------------------------

    [Theory]
    [InlineData("https://example.com/files/report.pdf")]
    [InlineData("http://example.com/data.csv")]
    [InlineData("https://ollama.com/download/OllamaSetup.exe")]
    public void TryValidateDownloadUrl_HttpHttps_Accepted(string url)
    {
        Assert.True(BrowserTools.TryValidateDownloadUrl(url, out var uri, out var error), error);
        Assert.NotNull(uri);
        Assert.Equal(url, uri!.ToString());
    }

    [Theory]
    [InlineData((object)null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("ftp://example.com/file.zip")]
    [InlineData("file:///C:/Windows/file.exe")]
    [InlineData("javascript:alert(1)")]
    [InlineData("data:text/plain,hello")]
    [InlineData("not a url")]
    [InlineData("https://")]
    [InlineData("https://user:secret@example.com/file.zip")]
    public void TryValidateDownloadUrl_BadUrls_Rejected(string? url)
    {
        Assert.False(BrowserTools.TryValidateDownloadUrl(url, out var uri, out var error));
        Assert.Null(uri);
        Assert.False(string.IsNullOrWhiteSpace(error));
    }

    // ---- Download path validation ----------------------------------------

    [Fact]
    public void ValidateDownloadPath_InsideDirectory_Accepted()
    {
        var path = Path.Combine(_root, "report.pdf");
        Assert.True(BrowserTools.ValidateDownloadPath(path, _root, out var error), error);
    }

    [Fact]
    public void ValidateDownloadPath_NestedInsideDirectory_Accepted()
    {
        var sub = Path.Combine(_root, "sub");
        Directory.CreateDirectory(sub);
        Assert.True(BrowserTools.ValidateDownloadPath(Path.Combine(sub, "a.bin"), _root, out _));
    }

    [Theory]
    [InlineData((object)null)]
    [InlineData("")]
    public void ValidateDownloadPath_EmptyPath_Rejected(string? path)
    {
        Assert.False(BrowserTools.ValidateDownloadPath(path, _root, out var error));
        Assert.False(string.IsNullOrWhiteSpace(error));
    }

    [Fact]
    public void ValidateDownloadPath_TraversalEscapingDirectory_Rejected()
    {
        var escape = Path.Combine(_root, "..", "escape.exe");
        Assert.False(BrowserTools.ValidateDownloadPath(escape, _root, out var error));
        Assert.Contains("escapes", error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ValidateDownloadPath_AbsoluteOutsideDirectory_Rejected()
    {
        var outside = Path.Combine(Path.GetTempPath(), "aurora-outside-" + Guid.NewGuid().ToString("N") + ".bin");
        Assert.False(BrowserTools.ValidateDownloadPath(outside, _root, out _));
    }

    [Fact]
    public void ValidateDownloadPath_NullByte_Rejected()
    {
        var path = Path.Combine(_root, "bad\0file.bin");
        Assert.False(BrowserTools.ValidateDownloadPath(path, _root, out var error));
        Assert.Contains("NUL", error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ValidateDownloadPath_DirectoryOnly_Rejected()
    {
        Assert.False(BrowserTools.ValidateDownloadPath(_root, _root, out var error));
        Assert.False(string.IsNullOrWhiteSpace(error));
    }

    [Fact]
    public void ValidateDownloadPath_RelativePath_Rejected()
    {
        Assert.False(BrowserTools.ValidateDownloadPath("relative/file.bin", _root, out _));
    }

    // ---- Chromium readiness detection (no browser launch) ----------------

    [Fact]
    public void CheckBrowserReadiness_IsConsistent_WithIsChromiumInstalled()
    {
        var (ready, detail) = BrowserTools.CheckBrowserReadiness();
        Assert.Equal(BrowserTools.IsChromiumInstalled(), ready);
        Assert.False(string.IsNullOrWhiteSpace(detail));
        if (!ready)
        {
            // The failure detail must name the real install process.
            Assert.Contains(BrowserTools.ChromiumInstallCommand, detail, StringComparison.Ordinal);
        }
    }

    // ---- Tool dispatch (validation only — no browser launch) -------------

    [Fact]
    public async Task ExecuteAsync_UnknownAction_Fails()
    {
        var tool = new BrowserTools();
        var result = await tool.ExecuteAsync(new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["action"] = "browse",
            ["queries"] = "test",
        }, CancellationToken.None);
        Assert.False(result.Success);
        Assert.Contains("research", result.Details ?? string.Empty, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ExecuteAsync_Research_NoQueries_Fails()
    {
        var tool = new BrowserTools();
        var result = await tool.ExecuteAsync(new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["action"] = "research",
        }, CancellationToken.None);
        Assert.False(result.Success);
        Assert.Contains("No research queries", result.Summary, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ExecuteAsync_Download_InvalidUrl_FailsWithoutBrowser()
    {
        var tool = new BrowserTools();
        var result = await tool.ExecuteAsync(new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["action"] = "download",
            ["url"] = "ftp://example.com/file.zip",
            ["outputPath"] = Path.Combine(_root, "file.zip"),
        }, CancellationToken.None);
        Assert.False(result.Success);
        Assert.Contains("refused", result.Summary, StringComparison.OrdinalIgnoreCase);
        Assert.False(File.Exists(Path.Combine(_root, "file.zip")));
    }

    [Fact]
    public async Task ExecuteAsync_Download_NullBytePath_FailsWithoutBrowser()
    {
        // ExecuteAsync validates the destination against its own
        // directory, so a ".." that resolves inside that directory is
        // not an escape (see ValidateDownloadPath_TraversalEscapingDirectory_Rejected
        // for the fixed-directory case). A NUL byte is refused outright
        // before any browser is launched.
        var tool = new BrowserTools();
        var result = await tool.ExecuteAsync(new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["action"] = "download",
            ["url"] = "https://example.com/file.zip",
            ["outputPath"] = Path.Combine(_root, "bad\0file.zip"),
        }, CancellationToken.None);
        Assert.False(result.Success);
        Assert.Contains("refused", result.Summary, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ExecuteAsync_Download_NoOutputPath_FailsWithoutBrowser()
    {
        var tool = new BrowserTools();
        var result = await tool.ExecuteAsync(new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["action"] = "download",
            ["url"] = "https://example.com/file.zip",
        }, CancellationToken.None);
        Assert.False(result.Success);
    }

    // ---- Missing-Chromium error path (honest failure, no fake success) ---

    [Fact]
    public async Task ResearchAsync_MissingChromium_FailsHonestly_NamesInstallProcess()
    {
        // No Chromium on this VM: ResearchAsync must return failure and
        // name the install process instead of fabricating a report.
        // This test asserts the failure path only; a real-browser run
        // is a manual Windows check (TESTS.md).
        var tool = new BrowserTools();
        var output = Path.Combine(_root, "report.md");
        var result = await tool.ResearchAsync(
            new[] { "electric bikes under $1000" }, output, CancellationToken.None);

        Assert.False(result.Success);
        var text = (result.Summary + " " + (result.Details ?? string.Empty)).ToLowerInvariant();
        Assert.Contains("chromium", text, StringComparison.Ordinal);
        Assert.Contains("playwright install chromium", text, StringComparison.Ordinal);
        Assert.Null(result.OutputPath);
        // No fabricated report file: the launch failure returns before
        // any report is written.
        Assert.False(File.Exists(output));
    }

    [Fact]
    public async Task ResearchAsync_EmptyQueries_Fails()
    {
        var tool = new BrowserTools();
        var result = await tool.ResearchAsync(
            Array.Empty<string>(), Path.Combine(_root, "empty.md"), CancellationToken.None);
        Assert.False(result.Success);
        Assert.False(File.Exists(Path.Combine(_root, "empty.md")));
    }

    [Fact]
    public async Task ResearchAsync_NullByteOutputPath_FailsWithoutThrowing()
    {
        var tool = new BrowserTools();
        var result = await tool.ResearchAsync(
            new[] { "test query" }, Path.Combine(_root, "bad\0.md"), CancellationToken.None);
        Assert.False(result.Success);
        Assert.Contains("NUL", result.Summary, StringComparison.OrdinalIgnoreCase);
    }
}
