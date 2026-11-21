using AuroraOperator.Core.Ollama;
using AuroraOperator.Core.Tools;

namespace AuroraOperator.Tests;

/// <summary>
/// Phase 7 integration tests (coordinator): the three fixes made while
/// integrating the Phase 7 workers — the 8 GB model-selection alignment,
/// the corrected qwen3.5:2b size, and the browser download containment
/// fix (security finding F8). No browser is launched: the download tests
/// assert the validation decision only, and the "inside profile" case
/// fails honestly on the missing Chromium, never with a fake success.
/// </summary>
public sealed class Phase7IntegrationTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "aurora-phase7int-" + Guid.NewGuid().ToString("N"));
    public Phase7IntegrationTests() => Directory.CreateDirectory(_root);
    public void Dispose() { try { Directory.Delete(_root, true); } catch { } }

    private static long Gb(long n) => n * 1024L * 1024 * 1024;

    // ---- 8 GB model-selection alignment (ModelCatalog) -------------------

    [Fact]
    public void SelectBestModel_8GbTotal_SelectsFallback_NotPrimary()
    {
        // Phase 7 decision: profiles and auto-selection agree — an 8 GB
        // machine gets qwen3:1.7b; qwen3:4b needs 16 GB total + 6 GB free.
        Assert.Equal("qwen3:1.7b", ModelCatalog.SelectBestModel(Gb(8), Gb(4)));
        Assert.Equal("qwen3:1.7b", ModelCatalog.SelectBestModel(Gb(8), Gb(6)));
    }

    [Fact]
    public void SelectBestModel_16GbTotal_SelectsPrimary()
    {
        Assert.Equal("qwen3:4b", ModelCatalog.SelectBestModel(Gb(16), Gb(8)));
        Assert.Equal("qwen3:1.7b", ModelCatalog.SelectBestModel(Gb(16), Gb(2)));
    }

    [Fact]
    public void SelectProfile_And_SelectBestModel_Agree_On8And16Gb()
    {
        Assert.Equal(ModelCatalog.SelectProfile(Gb(8)).Model, ModelCatalog.SelectBestModel(Gb(8), Gb(4)));
        Assert.Equal(ModelCatalog.SelectProfile(Gb(16)).Model, ModelCatalog.SelectBestModel(Gb(16), Gb(8)));
    }

    [Fact]
    public void Catalog_Qwen35_2b_SizeMatchesOllamaLibrary()
    {
        // Verified against ollama.com/library 2026-11-21: qwen3.5:2b is
        // 2.7–3.1 GB (multimodal), not the 1.6 GB the catalog used to say.
        var vision = ModelCatalog.All.Single(m => m.Name == "qwen3.5:2b");
        Assert.InRange(vision.ApproxSizeBytes, 2_700_000_000L, 3_100_000_000L);
        Assert.True(vision.MinRamGb >= 8);
    }

    // ---- Browser download containment (security finding F8) --------------

    [Fact]
    public async Task ExecuteAsync_Download_OutsideDownloads_IsRefused()
    {
        // F8: the old code derived the "allowed" directory from the
        // requested path itself, so any path passed. Now downloads are
        // contained to the user's Downloads folder.
        var tool = new BrowserTools();
        var result = await tool.ExecuteAsync(new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["action"] = "download",
            ["url"] = "https://example.com/file.zip",
            ["outputPath"] = Path.Combine(_root, "file.zip"),
        }, CancellationToken.None);
        Assert.False(result.Success);
        Assert.Contains("refused", result.Summary, StringComparison.OrdinalIgnoreCase);
        Assert.False(File.Exists(Path.Combine(_root, "file.zip")));
    }

    [Fact]
    public async Task ExecuteAsync_Download_InsideProfileDirectory_PassesValidation()
    {
        // An explicit downloadDirectory inside the user profile is
        // honoured: validation passes, so the attempt reaches the
        // (missing) browser and fails honestly about Chromium instead
        // of being refused at the path check.
        string baseDir = BrowserTools.IsInsideUserProfile(Path.GetTempPath(), out _)
            ? Path.GetTempPath() : Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var dir = Path.Combine(baseDir, "aurora-phase7int-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var tool = new BrowserTools();
            var result = await tool.ExecuteAsync(new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["action"] = "download",
                ["url"] = "https://example.com/file.zip",
                ["outputPath"] = Path.Combine(dir, "file.zip"),
                ["downloadDirectory"] = dir,
            }, CancellationToken.None);
            Assert.False(result.Success);
            Assert.DoesNotContain("refused", result.Summary, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            try { Directory.Delete(dir, true); } catch { }
        }
    }

    [Fact]
    public async Task ExecuteAsync_Download_DirectoryOutsideProfile_IsRefused()
    {
        var tool = new BrowserTools();
        string outside = OutsideProfile();
        var result = await tool.ExecuteAsync(new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["action"] = "download",
            ["url"] = "https://example.com/file.zip",
            ["outputPath"] = Path.Combine(outside, "file.zip"),
            ["downloadDirectory"] = outside,
        }, CancellationToken.None);
        Assert.False(result.Success);
        Assert.Contains("refused", result.Summary, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void IsInsideUserProfile_Profile_Itself_And_Subdirectory_Accepted()
    {
        var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        Assert.True(BrowserTools.IsInsideUserProfile(profile, out _));
        Assert.True(BrowserTools.IsInsideUserProfile(Path.Combine(profile, "Downloads"), out _));
        Assert.True(BrowserTools.IsInsideUserProfile(BrowserTools.DefaultDownloadDirectory, out _));
    }

    [Fact]
    public void IsInsideUserProfile_OutsideProfile_And_Nul_Rejected()
    {
        Assert.False(BrowserTools.IsInsideUserProfile("/etc", out _));
        Assert.False(BrowserTools.IsInsideUserProfile(OutsideProfile(), out _));
        Assert.False(BrowserTools.IsInsideUserProfile("bad\0dir", out _));
        Assert.False(BrowserTools.IsInsideUserProfile(null, out _));
    }

    private static string OutsideProfile() => OperatingSystem.IsWindows() ? Environment.SystemDirectory : "/etc";
}
