using System.Diagnostics;
using AuroraOperator.Core;
using AuroraOperator.Core.Agent;
using AuroraOperator.Core.Security;
using AuroraOperator.Core.Tools;

namespace AuroraOperator.Tests;

/// <summary>
/// Phase 2 tests: structured Args, real text extraction, the Notepad flow
/// and the "app" tool. The Windows Notepad UI path (launch notepad.exe,
/// type via UI Automation, Save dialog) is NOT VERIFIED on Linux: tests
/// that need it assert the honest Windows-only failure here, and the
/// Windows manual test is documented in the test names/comments.
/// </summary>
public sealed class Phase2Tests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "aurora-phase2-" + Guid.NewGuid().ToString("N"));
    public Phase2Tests() => Directory.CreateDirectory(_root);
    public void Dispose() { try { Directory.Delete(_root, true); } catch { } }

    // ---- Text extraction (IntentParser.ExtractTextToWrite) ----

    [Theory]
    [InlineData("Write \"Hello, World!\" to /tmp/x.txt", "Hello, World!")]
    [InlineData("Write the following: Hello World", "Hello World")]
    [InlineData("Open Notepad and write Hello World", "Hello World")]
    [InlineData("Write Hello World to /tmp/note.txt", "Hello World")]
    [InlineData("打开记事本写你好世界", "你好世界")]
    [InlineData("Open Notepad and write السلام علیکم", "السلام علیکم")]
    [InlineData("write \"Line one\nLine two\"", "Line one\nLine two")]
    [InlineData("Write 'Hello World' and save it to /tmp/note.txt", "Hello World")]
    [InlineData("Open Notepad and write Hello World and save it to /tmp/note.txt", "Hello World")]
    public void ExtractTextToWrite_ReturnsActualText(string request, string expected) =>
        Assert.Equal(expected, IntentParser.ExtractTextToWrite(request));

    [Theory]
    [InlineData("Write a note to /tmp/x.txt")]
    [InlineData("Write the file to /tmp/x.txt")]
    [InlineData("Write a file")]
    [InlineData("Create a folder named Reports")]
    [InlineData("")]
    public void ExtractTextToWrite_NoText_ReturnsNull(string request) =>
        Assert.Null(IntentParser.ExtractTextToWrite(request));

    // ---- Planner: structured Args ----

    [Fact]
    public void Planner_WriteStep_ArgsCarryExactText_InclUnicodeAndNewlines()
    {
        var path = Path.Combine(_root, "note.txt");
        const string text = "你好世界\nHello\nالسلام علیکم!";
        var task = new AgentTask("p2-1", $"Write \"{text}\" to {path}", Language.English, AgentMode.Autonomous);
        var plan = Planner.Plan(task);
        var write = Assert.Single(plan.Steps, s => s.ToolName == "filesystem" && s.Args.TryGetValue("action", out var a) && a == "write-file");
        Assert.Equal("write-file", write.Args["action"]);
        Assert.Equal(path, write.Args["path"]);
        Assert.Equal(text, write.Args["text"]);
        Assert.Equal("file:" + path, write.Args["verify"]);
        Assert.DoesNotContain("action=", write.Description);
    }

    [Fact]
    public void Planner_AllStepsHaveStructuredArgs_AndNoHintProse()
    {
        var requests = new[]
        {
            "Create a folder named Business Reports in " + _root,
            "Open Notepad and write Hello World",
            "Write \"Hello\" to " + Path.Combine(_root, "a.txt"),
            "Find duplicate files in " + _root,
        };
        foreach (var request in requests)
        {
            var plan = Planner.Plan(new AgentTask("p2-all", request, Language.English, AgentMode.Autonomous));
            Assert.All(plan.Steps, s =>
            {
                Assert.True(s.Args.Count > 0, $"Step '{s.Title}' has no structured Args.");
                Assert.True(s.Args.ContainsKey("action"), $"Step '{s.Title}' has no 'action' in Args.");
                Assert.DoesNotContain("action=", s.Description);
            });
        }
    }

    [Fact]
    public void Planner_NotepadFlow_UsesAppTool_WithRealText()
    {
        var task = new AgentTask("p2-2", "Open Notepad and write Hello World", Language.English, AgentMode.Autonomous);
        var plan = Planner.Plan(task);
        var launch = Assert.Single(plan.Steps, s => s.ToolName == "app" && s.Args.TryGetValue("action", out var a) && a == "launch-app");
        Assert.Equal("notepad", launch.Args["app"]);
        var type = Assert.Single(plan.Steps, s => s.ToolName == "app" && s.Args.TryGetValue("action", out var a) && a == "type-text");
        Assert.Equal("Hello World", type.Args["text"]);
        Assert.DoesNotContain(plan.Steps, s => s.ToolName == "filesystem" && s.Args.TryGetValue("action", out var a) && a == "write-file");
    }

    [Fact]
    public void Planner_NotepadFlow_WithSavePath_AddsSaveAsStep()
    {
        var path = Path.Combine(_root, "saved.txt");
        var task = new AgentTask("p2-3", $"Open Notepad and write Hello World and save it to {path}", Language.English, AgentMode.Autonomous);
        var plan = Planner.Plan(task);
        var save = Assert.Single(plan.Steps, s => s.ToolName == "app" && s.Args.TryGetValue("action", out var a) && a == "save-as");
        Assert.Equal(path, save.Args["path"]);
        Assert.Equal("Hello World", save.Args["text"]);
        Assert.Equal("file:" + path, save.Args["verify"]);
    }

    [Fact]
    public void Planner_Notepad_NoText_StepSaysSoHonestly()
    {
        var task = new AgentTask("p2-4", "Open Notepad and write a note", Language.English, AgentMode.Autonomous);
        var plan = Planner.Plan(task);
        var type = Assert.Single(plan.Steps, s => s.ToolName == "app" && s.Args.TryGetValue("action", out var a) && a == "type-text");
        Assert.False(type.Args.ContainsKey("text"));
        Assert.Contains("No text was provided", type.Description, StringComparison.Ordinal);
    }

    // ---- Engine: Args-only execution, no hint prose ----

    [Fact]
    public async Task Engine_ArgsOnly_WriteFile_WritesExactContent_WithoutAnyHintString()
    {
        const string text = "你好世界\nLine two\nالسلام علیکم!";
        var path = Path.Combine(_root, "exact.txt");
        var step = new PlanStep("s1", "Write the requested text", "filesystem",
            "Write the requested text to a file.")
        {
            Args = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["action"] = "write-file",
                ["path"] = path,
                ["text"] = text,
                ["verify"] = "file:" + path,
            },
        };
        var engine = new AgentEngine(null, "qwen3:4b", new IAgentTool[] { new FilesystemTools() }, new PermissionManager(),
            planProvider: _ => new TaskPlan("t", "write", Language.English, new[] { step }, "test plan"));
        var done = await engine.RunAsync(new AgentTask("t-args", "Write the text", Language.English, AgentMode.Autonomous), CancellationToken.None);

        Assert.Equal(TaskState.Completed, done.State);
        Assert.Equal(StepStatus.Succeeded, done.Plan!.Steps[0].Status);
        Assert.True(File.Exists(path));
        Assert.Equal(text, File.ReadAllText(path));
    }

    [Fact]
    public async Task Engine_WriteFile_NoText_FailsHonestly_AndWritesNothing()
    {
        var path = Path.Combine(_root, "empty.txt");
        var step = new PlanStep("s1", "Write the requested text", "filesystem",
            "No text was provided in the request, so nothing can be written.")
        {
            Args = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["action"] = "write-file",
                ["path"] = path,
                ["verify"] = "file:" + path,
            },
        };
        var engine = new AgentEngine(null, "qwen3:4b", new IAgentTool[] { new FilesystemTools() }, new PermissionManager(),
            planProvider: _ => new TaskPlan("t", "write", Language.English, new[] { step }, "test plan"));
        var done = await engine.RunAsync(new AgentTask("t-notext", "Write a note", Language.English, AgentMode.Autonomous), CancellationToken.None);

        Assert.Equal(StepStatus.Failed, done.Plan!.Steps[0].Status);
        Assert.Contains("no text provided", done.Plan.Steps[0].ResultSummary ?? string.Empty, StringComparison.OrdinalIgnoreCase);
        Assert.False(File.Exists(path));
    }

    // ---- Args resolution / risk classification ----

    [Fact]
    public void ResolveArgs_StructuredArgsWinOverLegacyHintProse()
    {
        var step = new PlanStep("s1", "Do it", "filesystem", "Do it. action=list; path=/tmp/old")
        {
            Args = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["action"] = "write-file",
                ["path"] = "/tmp/new.txt",
                ["text"] = "hi",
            },
        };
        var resolved = AgentEngine.ResolveArgs(step);
        Assert.Equal("write-file", resolved["action"]);
        Assert.Equal("/tmp/new.txt", resolved["path"]);
        Assert.Equal("hi", resolved["text"]);
        // Legacy-only keys still fall back to the hints.
        var legacy = new PlanStep("s2", "List", "filesystem", "List files. action=list; path=/tmp/legacy");
        Assert.Equal("list", AgentEngine.ResolveArgs(legacy)["action"]);
        Assert.Equal("/tmp/legacy", AgentEngine.ResolveArgs(legacy)["path"]);
    }

    [Fact]
    public void RiskClassifier_AppActions_ClassifiedFromArgs()
    {
        Assert.Equal(RiskLevel.NormalReversible,
            RiskClassifier.Classify("app", "launch-app", "Open Notepad."));
        Assert.Equal(RiskLevel.NormalReversible,
            RiskClassifier.Classify("app", "type-text", "Type the text."));
        Assert.Equal(RiskLevel.ReadOnly,
            RiskClassifier.Classify("app", "read-text", "Read the text back."));
        Assert.Equal(RiskLevel.NormalReversible,
            RiskClassifier.Classify("app", "save-as", "Save the text.", new Dictionary<string, string> { ["path"] = Path.Combine(_root, "new.txt") }));

        // Args win over a misleading legacy hint in the description:
        // a write-file to an EXISTING file is Sensitive even when the prose
        // still carries an old "action=list" hint.
        var existing = Path.Combine(_root, "exists.txt");
        File.WriteAllText(existing, "x");
        var step = new PlanStep("s1", "Write", "filesystem", "List files. action=list; path=" + existing)
        {
            Args = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["action"] = "write-file",
                ["path"] = existing,
            },
        };
        Assert.Equal(RiskLevel.SensitiveFileModification, RiskClassifier.Classify(step));
    }

    // ---- App tool + Notepad workflow (Linux test double) ----

    [Fact]
    public async Task AppTools_SaveAs_WithLinuxTestDouble_WritesExactContentViaVerifiedFallback()
    {
        // LINUX TEST DOUBLE: FakeAppControl records calls; it is NOT
        // Windows Notepad. On Linux the Notepad Save dialog does not exist,
        // so SaveAsync uses the VERIFIED FALLBACK (direct file write +
        // content verification) and says so in the result.
        const string text = "你好世界\nLine two\nالسلام علیکم!";
        var path = Path.Combine(_root, "fallback.txt");
        var tool = new AppTools(new FakeAppControl());
        var result = await tool.ExecuteAsync(new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["action"] = "save-as",
            ["app"] = "notepad",
            ["path"] = path,
            ["text"] = text,
        }, CancellationToken.None);

        Assert.True(result.Success, result.Summary);
        Assert.Contains("fallback", result.Summary, StringComparison.OrdinalIgnoreCase);
        Assert.True(File.Exists(path));
        Assert.Equal(text, File.ReadAllText(path));
        Assert.True(NotepadWorkflow.VerifyFileContent(path, text));
    }

    [Fact]
    public async Task NotepadWorkflow_RunAsync_OnLinux_FailsWindowsOnly_AndLaunchesNothing()
    {
        // Windows Notepad path NOT VERIFIED on Linux: this asserts the
        // honest failure instead of running Notepad anywhere.
        if (OperatingSystem.IsWindows()) return;
        var fake = new FakeAppControl();
        var result = await NotepadWorkflow.RunAsync(fake, "Hello World", null, CancellationToken.None);
        Assert.False(result.Success);
        Assert.Contains("Windows-only", result.Summary, StringComparison.Ordinal);
        Assert.Contains("NOT SUPPORTED", result.Summary, StringComparison.Ordinal);
        Assert.Equal(0, fake.LaunchCalls.Count);
    }

    [Fact]
    public async Task Engine_NotepadFlow_OnLinux_FailsHonestly_NeverSimulatedSuccess()
    {
        // Windows Notepad path NOT VERIFIED on Linux: the real Planner +
        // engine + ProcessAppControl must fail honestly, not pretend.
        if (OperatingSystem.IsWindows()) return;
        var engine = new AgentEngine(null, "qwen3:4b",
            new IAgentTool[] { new FilesystemTools(), new AppTools(new ProcessAppControl()) },
            new PermissionManager(),
            approvalService: new TestApprovalService { Mode = TestApprovalMode.AutoApprove });
        var done = await engine.RunAsync(
            new AgentTask("t-np-linux", "Open Notepad and write Hello World", Language.English, AgentMode.Autonomous),
            CancellationToken.None);

        Assert.Equal(TaskState.Failed, done.State);
        Assert.Contains("Windows-only", done.FinalReport ?? string.Empty, StringComparison.Ordinal);
        Assert.Contains(done.Plan!.Steps, s => s.ToolName == "app" && s.Status == StepStatus.Failed);
    }

    // ---- Real process launch / detect / close (Linux) ----

    [Fact]
    public void ProcessAppControl_LaunchRealProcess_AppearsInList_ThenCloses()
    {
        if (!OperatingSystem.IsLinux()) return; // Linux-only: uses /proc cmdline to identify the process.
        var script = Path.Combine(_root, "aurora-sleeper.py");
        File.WriteAllText(script, "#!/usr/bin/env python3\nimport time\ntime.sleep(60)\n");
        File.SetUnixFileMode(script,
            UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute |
            UnixFileMode.GroupRead | UnixFileMode.OtherRead);

        var control = new ProcessAppControl();
        Assert.True(control.LaunchApp(script), "Launching the real sleeper script must succeed.");

        int? pid = null;
        var deadline = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(5);
        while (DateTimeOffset.UtcNow < deadline && pid is null)
        {
            pid = FindPythonPidForScript(script);
            if (pid is null) Thread.Sleep(200);
        }
        Assert.True(pid.HasValue, "The launched python process did not appear in the process list.");

        using var process = Process.GetProcessById(pid.Value);
        Assert.False(process.HasExited);
        process.Kill(entireProcessTree: true);
        Assert.True(process.WaitForExit(5000), "The launched process did not exit after being closed.");

        Assert.False(control.CloseApp("aurora-no-such-process-xyz"));
    }

    private static int? FindPythonPidForScript(string script)
    {
        foreach (var p in Process.GetProcessesByName("python3"))
        {
            try
            {
                var cmdline = File.ReadAllText($"/proc/{p.Id}/cmdline");
                if (cmdline.Contains(script, StringComparison.Ordinal)) return p.Id;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
            {
                // Process exited mid-enumeration.
            }
            finally
            {
                p.Dispose();
            }
        }
        return null;
    }

    [Fact]
    public void ProcessAppControl_TypeText_ThrowsPlatformNotSupported_OnNonWindows()
    {
        // Windows Notepad typing is NOT VERIFIED on Linux: on non-Windows
        // TypeText must throw instead of pretending to type.
        if (OperatingSystem.IsWindows()) return;
        Assert.Throws<PlatformNotSupportedException>(() => new ProcessAppControl().TypeText("Hello"));
        // The cross-platform control has no UI Automation either: reading
        // text or driving the Save dialog must throw, never return fake text.
        // (Default interface methods are only reachable via the interface.)
        IAppControl control = new ProcessAppControl();
        Assert.Throws<PlatformNotSupportedException>(() => control.ReadTextFromFocusedControl());
        Assert.Throws<PlatformNotSupportedException>(() => control.ReadWindowText("Notepad"));
        Assert.Throws<PlatformNotSupportedException>(() => control.SaveNotepadFile("/tmp/x.txt"));
    }
}

/// <summary>
/// LINUX TEST DOUBLE for <see cref="IAppControl"/>: records calls and
/// returns canned answers. It is NOT Windows Notepad and must never be
/// presented as a Windows pass. Used to exercise the file-save + verify
/// path and the Windows-only failure path on Linux.
/// </summary>
public sealed class FakeAppControl : IAppControl
{
    public List<string> LaunchCalls { get; } = new();
    public List<string> TypedTexts { get; } = new();
    public List<string> SaveCalls { get; } = new();
    public string? ReadBackText { get; set; } = "Hello World";
    public bool LaunchResult { get; set; } = true;
    public bool SaveResult { get; set; } = false;
    public List<string> Titles { get; set; } = new() { "Untitled - Notepad" };

    public bool LaunchApp(string appNameOrPath) { LaunchCalls.Add(appNameOrPath); return LaunchResult; }
    public bool CloseApp(string processName) => true;
    public IReadOnlyList<string> RunningAppTitles() => Titles;
    public bool FocusWindow(string titleContains) => true;
    public bool TypeText(string text) { TypedTexts.Add(text); return true; }
    public string? ReadTextFromFocusedControl() => ReadBackText;
    public string? ReadWindowText(string titleContains) => ReadBackText;
    public bool SaveNotepadFile(string path) { SaveCalls.Add(path); return SaveResult; }
}
