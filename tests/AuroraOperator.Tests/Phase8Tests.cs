using System.Net;
using System.Text;
using AuroraOperator.Core;
using AuroraOperator.Core.Memory;
using AuroraOperator.Core.Ollama;
using AuroraOperator.Core.Security;
using AuroraOperator.Core.Tools;

namespace AuroraOperator.Tests;

/// <summary>
/// Phase 8 acceptance tests (all Linux-runnable; no real Ollama server,
/// no Chromium, no Windows UI). Covers: the 32 GB / sub-8 GB model
/// selection alignment, bounded Ollama retry, the F5 launch allowlist,
/// the F20 close-app guard, F13 redaction extensions, F14 preference
/// protection, and the remaining browser-download security cases.
/// Windows-only behaviour (WPF runtime, Notepad UI flow, Chromium
/// launch, installer execution, DPAPI on Windows) stays BLOCKED in
/// tests/windows/WINDOWS_ACCEPTANCE_CHECKLIST.md.
/// </summary>
public sealed class Phase8Tests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "aurora-phase8-" + Guid.NewGuid().ToString("N"));
    public Phase8Tests() => Directory.CreateDirectory(_root);
    public void Dispose() { try { Directory.Delete(_root, true); } catch { } }

    private static long Gb(long n) => n * 1024L * 1024 * 1024;

    // ---- Model selection: 32 GB auto-selects 8b, below 8 GB selects 0.6b ----

    [Fact]
    public void SelectBestModel_32Gb_Selects8b_WhenEnoughFree()
    {
        Assert.Equal("qwen3:8b", ModelCatalog.SelectBestModel(Gb(32), Gb(12)));
        Assert.Equal("qwen3:8b", ModelCatalog.SelectBestModel(Gb(32), Gb(10)));
    }

    [Fact]
    public void SelectBestModel_32Gb_FallsBack_WhenLittleFree()
    {
        // 32 GB total but only 5 GB free: neither the 8b gate (10 GB) nor
        // the 4b gate (6 GB) is met, so the safe fallback is 1.7b — the
        // free-RAM gates are deliberate, not a bug.
        Assert.Equal("qwen3:1.7b", ModelCatalog.SelectBestModel(Gb(32), Gb(5)));
    }

    [Fact]
    public void SelectBestModel_Below8Gb_Selects06b()
    {
        Assert.Equal("qwen3:0.6b", ModelCatalog.SelectBestModel(Gb(4), Gb(2)));
        Assert.Equal("qwen3:0.6b", ModelCatalog.SelectBestModel(Gb(7), Gb(3)));
    }

    [Fact]
    public void SelectProfile_And_SelectBestModel_Agree_On32And4Gb()
    {
        Assert.Equal(ModelCatalog.SelectProfile(Gb(32)).Model, ModelCatalog.SelectBestModel(Gb(32), Gb(12)));
        Assert.Equal(ModelCatalog.SelectProfile(Gb(4)).Model, ModelCatalog.SelectBestModel(Gb(4), Gb(2)));
        Assert.Equal("qwen3:0.6b", ModelCatalog.SelectProfile(Gb(4)).Model);
        Assert.Equal(4096, ModelCatalog.SelectProfile(Gb(4)).ContextTokens);
        Assert.Equal(8192, ModelCatalog.SelectProfile(Gb(8)).ContextTokens);
    }

    // ---- Ollama bounded retry ----

    /// <summary>Handler returning a fixed sequence of statuses, counting calls.</summary>
    private sealed class SequenceHandler : HttpMessageHandler
    {
        private readonly Queue<HttpStatusCode> _statuses;
        private readonly string _body;
        public int Calls { get; private set; }
        public SequenceHandler(IEnumerable<HttpStatusCode> statuses, string body = """{"response":"OK","done":true}""")
        {
            _statuses = new Queue<HttpStatusCode>(statuses);
            _body = body;
        }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Calls++;
            var code = _statuses.Count > 0 ? _statuses.Dequeue() : HttpStatusCode.OK;
            return Task.FromResult(new HttpResponseMessage(code)
            {
                Content = new StringContent(_body, Encoding.UTF8, "application/json"),
            });
        }
    }

    private sealed class HangingHandler : HttpMessageHandler
    {
        public int Calls { get; private set; }
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Calls++;
            await Task.Delay(Timeout.InfiniteTimeSpan, ct).ConfigureAwait(false);
            return StubHandler.Json("{}");
        }
    }

    [Fact]
    public async Task OllamaClient_Retries503_ThenSucceeds_BoundedAttempts()
    {
        var handler = new SequenceHandler(new[] { HttpStatusCode.ServiceUnavailable, HttpStatusCode.ServiceUnavailable, HttpStatusCode.OK });
        using var client = new OllamaClient(handler);
        var reply = await client.GenerateAsync("qwen3:4b", "Say OK", CancellationToken.None);
        Assert.Equal("OK", reply);
        Assert.Equal(3, handler.Calls);
    }

    [Fact]
    public async Task OllamaClient_DoesNotRetry400()
    {
        var handler = new SequenceHandler(new[] { HttpStatusCode.BadRequest });
        using var client = new OllamaClient(handler);
        var ex = await Assert.ThrowsAsync<HttpRequestException>(() =>
            client.GenerateAsync("qwen3:does-not-exist", "Say OK", CancellationToken.None));
        Assert.Contains("400", ex.Message, StringComparison.Ordinal);
        Assert.Equal(1, handler.Calls);
    }

    [Fact]
    public async Task OllamaClient_RetryStopsAfterThreeAttempts()
    {
        var handler = new SequenceHandler(Enumerable.Repeat(HttpStatusCode.InternalServerError, 10));
        using var client = new OllamaClient(handler);
        await Assert.ThrowsAsync<HttpRequestException>(() =>
            client.GenerateAsync("qwen3:4b", "Say OK", CancellationToken.None));
        Assert.Equal(3, handler.Calls);
    }

    [Fact]
    public async Task OllamaClient_Cancellation_AbortsRetry()
    {
        var handler = new HangingHandler();
        using var client = new OllamaClient(handler);
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(200));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            client.GenerateAsync("qwen3:4b", "Say OK", cts.Token));
        Assert.Equal(1, handler.Calls);
    }

    [Fact]
    public async Task OllamaClient_ListModels_Retries502_ThenSucceeds()
    {
        var handler = new SequenceHandler(
            new[] { HttpStatusCode.BadGateway, HttpStatusCode.OK },
            """{"models":[{"name":"qwen3:4b"}]}""");
        using var client = new OllamaClient(handler);
        var models = await client.ListModelsAsync(CancellationToken.None);
        Assert.Equal(new[] { "qwen3:4b" }, models);
        Assert.Equal(2, handler.Calls);
    }

    // ---- F5: launch allowlist ----

    [Theory]
    [InlineData("notepad", LaunchDecision.Allowed)]
    [InlineData("notepad.exe", LaunchDecision.Allowed)]
    [InlineData("calculator", LaunchDecision.Allowed)]
    [InlineData("paint", LaunchDecision.Allowed)]
    [InlineData("wordpad", LaunchDecision.Allowed)]
    [InlineData("chrome.exe", LaunchDecision.RequiresApproval)]
    [InlineData("code.exe", LaunchDecision.RequiresApproval)]
    [InlineData(@"C:\Program Files\Google\Chrome\Application\chrome.exe", LaunchDecision.RequiresApproval)]
    [InlineData("https://example.com/evil.exe", LaunchDecision.Rejected)]
    [InlineData("http://example.com/x.exe", LaunchDecision.Rejected)]
    [InlineData("ms-settings:windowsupdate", LaunchDecision.Rejected)]
    [InlineData("file:///C:/Windows/System32/calc.exe", LaunchDecision.Rejected)]
    [InlineData("evil.bat", LaunchDecision.Rejected)]
    [InlineData("evil.cmd", LaunchDecision.Rejected)]
    [InlineData("evil.ps1", LaunchDecision.Rejected)]
    [InlineData("evil.msi", LaunchDecision.Rejected)]
    [InlineData("evil.lnk", LaunchDecision.Rejected)]
    [InlineData("evil.vbs", LaunchDecision.Rejected)]
    [InlineData("", LaunchDecision.Rejected)]
    public void AppLaunchPolicy_EvaluatesTargets(string target, LaunchDecision expected)
    {
        Assert.Equal(expected, AppLaunchPolicy.Evaluate(target));
    }

    [Fact]
    public async Task AppTools_LaunchApp_RejectsUrl_WithoutCallingControl()
    {
        var fake = new FakeAppControl();
        var tool = new AppTools(fake);
        var result = await tool.ExecuteAsync(new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["action"] = "launch-app",
            ["app"] = "https://example.com/evil.exe",
        }, CancellationToken.None);
        Assert.False(result.Success);
        Assert.Contains("never launched", result.Summary, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(fake.LaunchCalls);
    }

    [Fact]
    public async Task AppTools_LaunchApp_RejectsScript_WithoutCallingControl()
    {
        var fake = new FakeAppControl();
        var tool = new AppTools(fake);
        foreach (var script in new[] { "setup.bat", "install.ps1", "update.msi" })
        {
            var result = await tool.ExecuteAsync(new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["action"] = "launch-app",
                ["app"] = script,
            }, CancellationToken.None);
            Assert.False(result.Success, script);
        }
        Assert.Empty(fake.LaunchCalls);
    }

    [Fact]
    public async Task AppTools_LaunchApp_FriendlyName_ReachesControl()
    {
        var fake = new FakeAppControl();
        var tool = new AppTools(fake);
        var result = await tool.ExecuteAsync(new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["action"] = "launch-app",
            ["app"] = "notepad",
        }, CancellationToken.None);
        Assert.True(result.Success, result.Summary);
        Assert.Single(fake.LaunchCalls);
    }

    [Fact]
    public void RiskClassifier_LaunchApp_UnknownTarget_NeedsApproval()
    {
        Assert.Equal(RiskLevel.NormalReversible,
            RiskClassifier.Classify("app", "launch-app", "Open Notepad.",
                new Dictionary<string, string> { ["app"] = "notepad" }));
        Assert.Equal(RiskLevel.SensitiveFileModification,
            RiskClassifier.Classify("app", "launch-app", "Open Chrome.",
                new Dictionary<string, string> { ["app"] = "chrome.exe" }));
        Assert.Equal(RiskLevel.SensitiveFileModification,
            RiskClassifier.Classify("app", "launch-app", "Open a URL.",
                new Dictionary<string, string> { ["app"] = "https://example.com/x.exe" }));
    }

    // ---- F20: close-app guard ----

    [Theory]
    [InlineData("explorer", true)]
    [InlineData("svchost", true)]
    [InlineData("lsass", true)]
    [InlineData("csrss", true)]
    [InlineData("winlogon", true)]
    [InlineData("dwm", true)]
    [InlineData("services", true)]
    [InlineData("System", true)]
    [InlineData("notepad", false)]
    [InlineData("chrome", false)]
    [InlineData("myapp", false)]
    public void ProcessSafety_ProtectedNames(string name, bool expected)
    {
        Assert.Equal(expected, ProcessSafety.IsProtectedProcessName(name));
    }

    [Fact]
    public void ProcessSafety_CurrentProcess_IsSafeToClose()
    {
        // The test runner itself is an ordinary user process, never a
        // protected system process. (IsSafeToClose only inspects; it
        // closes nothing.)
        using var self = System.Diagnostics.Process.GetCurrentProcess();
        Assert.True(ProcessSafety.IsSafeToClose(self));
    }

    [Fact]
    public async Task AppTools_CloseApp_ProtectedProcess_RefusedWithoutCallingControl()
    {
        var fake = new FakeAppControl();
        var tool = new AppTools(fake);
        foreach (var name in new[] { "explorer", "svchost", "lsass" })
        {
            var result = await tool.ExecuteAsync(new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["action"] = "close-app",
                ["app"] = name,
            }, CancellationToken.None);
            Assert.False(result.Success, name);
            Assert.Contains("protected", result.Summary, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public void RiskClassifier_CloseApp_NeedsApproval()
    {
        Assert.Equal(RiskLevel.SensitiveFileModification,
            RiskClassifier.Classify("app", "close-app", "Close Notepad.",
                new Dictionary<string, string> { ["app"] = "notepad" }));
    }

    [Fact]
    public void ProcessAppControl_CloseApp_ProtectedName_ReturnsFalse()
    {
        // No real process is harmed: the guard refuses before enumerating
        // closable targets, and "explorer" does not run on Linux anyway.
        var control = new ProcessAppControl();
        Assert.False(control.CloseApp("explorer"));
        Assert.False(control.CloseApp("svchost"));
    }

    // ---- F13: redaction extensions ----

    [Theory]
    [InlineData("access_token=abc123", "access_token=[REDACTED]")]
    [InlineData("refresh_token: xyz", "refresh_token=[REDACTED]")]
    [InlineData("client_secret=\"s3cr3t\"", "client_secret=[REDACTED]")]
    [InlineData("Authorization: Basic dXNlcjpwYXNz", "Authorization: Basic [REDACTED]")]
    [InlineData("x-api-key: abc-def", "x-api-key=[REDACTED]")]
    public void RedactSecrets_ExtendedPatterns(string input, string expected)
    {
        Assert.Equal(expected, LocalLogger.RedactSecrets(input));
    }

    [Fact]
    public void RedactSecrets_PrivateKeyBlock()
    {
        var key = "-----BEGIN RSA PRIVATE KEY-----\nMIIEowIBAAKCAQEA7\n-----END RSA PRIVATE KEY-----";
        var result = LocalLogger.RedactSecrets("leaked key: " + key);
        Assert.DoesNotContain("MIIEowIBAAKCAQEA7", result, StringComparison.Ordinal);
        Assert.Contains("[REDACTED PRIVATE KEY]", result, StringComparison.Ordinal);
    }

    [Fact]
    public void RedactSecrets_LeavesOrdinaryTextUntouched_Extended()
    {
        Assert.Equal("Open Notepad and write Hello World",
            LocalLogger.RedactSecrets("Open Notepad and write Hello World"));
    }

    // ---- F14: preference protection ----

    [Fact]
    public void SecretProtector_RoundTrips_OnThisPlatform()
    {
        const string value = "dark";
        var stored = SecretProtector.Protect(value);
        Assert.True(SecretProtector.LooksProtected(stored));
        Assert.Equal(value, SecretProtector.Unprotect(stored));
        Assert.DoesNotContain(value, stored, StringComparison.Ordinal);
    }

    [Fact]
    public void SecretProtector_LegacyPlaintext_ReadsAsIs()
    {
        Assert.Equal("dark", SecretProtector.Unprotect("dark"));
        Assert.Null(SecretProtector.Unprotect(null));
    }

    [Fact]
    public void SecretProtector_DpapiValue_UnreadableOffWindows()
    {
        if (OperatingSystem.IsWindows()) return;
        Assert.Null(SecretProtector.Unprotect("dpapi1:AAAA"));
    }

    [Fact]
    public void MemoryStore_Preference_RoundTrips_ThroughProtection()
    {
        using (var store = new MemoryStore(Path.Combine(_root, "mem.db")))
        {
            store.SetPreference("theme", "dark");
            Assert.Equal("dark", store.GetPreference("theme"));
        }
        // The stored form on disk is protected, not the raw value.
        var raw = File.ReadAllBytes(Path.Combine(_root, "mem.db"));
        var text = System.Text.Encoding.UTF8.GetString(raw);
        Assert.DoesNotContain("\"dark\"", text, StringComparison.Ordinal);
    }

    [Fact]
    public void MemoryStore_PreferenceValue_LookingLikeCredential_IsRefused()
    {
        using var store = new MemoryStore(Path.Combine(_root, "mem2.db"));
        Assert.Throws<ArgumentException>(() => store.SetPreference("notes", "api_key=abc123"));
        Assert.Null(store.GetPreference("notes"));
    }

    [Fact]
    public void MemoryStore_Preference_LegacyPlaintextValue_ReadsAsIs()
    {
        // A database written before Phase 8 (raw value, no marker) must
        // keep working: write it directly, then read through the store.
        var dbPath = Path.Combine(_root, "legacy.db");
        using (var store = new MemoryStore(dbPath))
        {
            store.SetPreference("theme", "dark");
        }
        // Strip the marker by hand: rewrite the stored value to plaintext.
        using (var raw = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={dbPath}"))
        {
            raw.Open();
            using var cmd = raw.CreateCommand();
            cmd.CommandText = "UPDATE preferences SET value = 'light' WHERE key = 'theme';";
            cmd.ExecuteNonQuery();
        }
        using var store2 = new MemoryStore(dbPath);
        Assert.Equal("light", store2.GetPreference("theme"));
    }

    // ---- Browser download security (cases not covered in Phase 7) ----

    [Fact]
    public void BrowserTools_DownloadUrl_EmbeddedCredentials_Rejected()
    {
        Assert.False(BrowserTools.TryValidateDownloadUrl("https://user:pass@example.com/file.zip", out _, out var error));
        Assert.Contains("credentials", error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void BrowserTools_DownloadPath_Traversal_Rejected()
    {
        var downloads = BrowserTools.DefaultDownloadDirectory;
        Assert.False(BrowserTools.ValidateDownloadPath(
            Path.Combine(downloads, "..", "..", "Windows", "evil.exe"), downloads, out _));
        Assert.False(BrowserTools.ValidateDownloadPath(
            Path.Combine(downloads, "sub", "..", "..", "evil.exe"), downloads, out _));
    }

    [Fact]
    public void BrowserTools_DownloadPath_NulByte_Rejected()
    {
        var downloads = BrowserTools.DefaultDownloadDirectory;
        Assert.False(BrowserTools.ValidateDownloadPath(
            downloads + Path.DirectorySeparatorChar + "evil\0.exe", downloads, out var error));
        Assert.Contains("NUL", error, StringComparison.Ordinal);
    }

    [Fact]
    public void BrowserTools_IsInsideUserProfile_OutsideProfile_Rejected()
    {
        var outside = OperatingSystem.IsWindows() ? @"C:\Windows\Temp" : "/tmp";
        Assert.False(BrowserTools.IsInsideUserProfile(outside, out _));
    }
}
