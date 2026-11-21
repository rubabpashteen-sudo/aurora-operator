using AuroraOperator.Core;
using AuroraOperator.Core.Agent;
using AuroraOperator.Core.Ollama;
using AuroraOperator.Core.Security;
using AuroraOperator.Core.Tools;

namespace AuroraOperator.Tests;

/// <summary>Acceptance mapping (see TESTS.md): planner + permission refusal (Test 8),
/// cancellation (Test 10), filesystem (Tests 2/3/5), documents (Test 4), coding (Test 7).</summary>
public sealed class PlannerTests
{
    [Fact]
    public void DeleteAll_Plan_RefusesBroadDelete_AndRequiresApproval()
    {
        var task = new AgentTask("t1", "Delete all files from my computer", Language.English, AgentMode.Autonomous);
        var plan = Planner.Plan(task);
        Assert.True(plan.Steps.Count >= 3);
        Assert.True(plan.Steps[0].RequiresApproval);
        Assert.Contains("Confirm scope", plan.Steps[0].Title);
        Assert.DoesNotContain(plan.Steps, s => s.Description.Contains("delete", StringComparison.OrdinalIgnoreCase)
            && s.ToolName == "filesystem" && s.Description.Contains("action=delete"));
    }

    [Fact]
    public void CreateFolder_Plan_HasAtLeastThreeSteps_WithCompletionConditions()
    {
        var task = new AgentTask("t2", "Create a folder named Business Reports on my Desktop", Language.English, AgentMode.Autonomous);
        var plan = Planner.Plan(task);
        Assert.True(plan.Steps.Count >= 3 && plan.Steps.Count <= 10);
        Assert.All(plan.Steps, s => Assert.False(string.IsNullOrWhiteSpace(s.Title)));
        Assert.NotNull(plan.Steps[^1].CompletionCondition);
    }

    [Fact]
    public void CsvToExcel_Plan_MentionsDocumentStep()
    {
        var task = new AgentTask("t3", "Create an Excel report from this CSV file", Language.English, AgentMode.Autonomous);
        var plan = Planner.Plan(task);
        Assert.Contains(plan.Steps, s => s.ToolName == "document");
    }
}

public sealed class LanguageAndModelTests
{
    [Theory]
    [InlineData("打开记事本写你好世界", Language.Chinese)]
    [InlineData("میرا کمپیوٹر سست کیوں ہے", Language.Urdu)]
    [InlineData("Open Notepad and write Hello World", Language.English)]
    public void Detect_ReturnsExpectedLanguage(string text, Language expected) =>
        Assert.Equal(expected, LanguageDetector.Detect(text));

    [Fact]
    public void SelectBestModel_16Gb_SelectsQwen3_4b() =>
        Assert.Equal("qwen3:4b", ModelCatalog.SelectBestModel(16L * 1024 * 1024 * 1024, 8L * 1024 * 1024 * 1024));

    [Fact]
    public void SelectBestModel_4Gb_SelectsUltraLight() =>
        // Phase 8 alignment: below 8 GB the app, the profiles and the
        // installers all select qwen3:0.6b (previously the app selected
        // 1.7b here while the installers installed 0.6b).
        Assert.Equal("qwen3:0.6b", ModelCatalog.SelectBestModel(4L * 1024 * 1024 * 1024, 2L * 1024 * 1024 * 1024));

    [Fact]
    public void Hardware_Detect_ReturnsPositiveRam() =>
        Assert.True(HardwareInfo.Detect().TotalRamBytes > 0);
}

public sealed class PermissionTests
{
    [Fact]
    public void IsDestructiveRequest_DeleteAll_IsTrue()
    {
        var pm = new PermissionManager();
        Assert.True(pm.IsDestructiveRequest("Delete all files from my computer"));
        Assert.False(pm.IsDestructiveRequest("List the PDF files in Downloads"));
    }

    [Fact]
    public async Task GuardDeleteAll_Refuses_WithFailureResult()
    {
        var pm = new PermissionManager();
        var result = await pm.GuardDeleteAllAsync("Delete all files from my computer");
        Assert.False(result.Success);
        Assert.Contains("scope", result.Details ?? result.Summary, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task RequestApproval_NoPrompt_DeniesByDefault()
    {
        var pm = new PermissionManager();
        var req = new ApprovalRequest("a1", "t1", "Delete", "Delete a file", "del file.txt", PermissionCategory.FileAccess);
        // FileAccess is granted by default, so use a denied-by-default category.
        var req2 = req with { Category = PermissionCategory.TerminalExecution };
        Assert.False(await pm.RequestApprovalAsync(req2, null, CancellationToken.None));
    }
}

public sealed class FilesystemTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "aurora-tests-" + Guid.NewGuid().ToString("N"));
    public FilesystemTests() => Directory.CreateDirectory(_root);
    public void Dispose() { try { Directory.Delete(_root, true); } catch { } }

    [Fact]
    public void CreateFolder_And_SearchFiles_Work()
    {
        var folder = FilesystemTools.CreateFolder(Path.Combine(_root, "Business Reports"));
        Assert.True(Directory.Exists(folder));
        File.WriteAllText(Path.Combine(_root, "a.pdf"), "x");
        File.WriteAllText(Path.Combine(_root, "b.pdf"), "y");
        var found = FilesystemTools.SearchFiles(_root, "*.pdf");
        Assert.Equal(2, found.Count);
    }

    [Fact]
    public void DuplicateFinder_GroupsIdenticalFiles_Only()
    {
        File.WriteAllText(Path.Combine(_root, "one.txt"), "same-content");
        File.WriteAllText(Path.Combine(_root, "two.txt"), "same-content");
        File.WriteAllText(Path.Combine(_root, "three.txt"), "different-content");
        var groups = DuplicateFinder.Find(_root, CancellationToken.None);
        Assert.Single(groups);
        Assert.Equal(2, groups[0].Paths.Count);
    }

    [Fact]
    public void OrganizeByExtension_MovesFiles_IntoExtensionFolders()
    {
        File.WriteAllText(Path.Combine(_root, "doc1.txt"), "a");
        File.WriteAllText(Path.Combine(_root, "sheet.csv"), "a,b\n1,2\n");
        var moved = FilesystemTools.OrganizeByExtension(_root);
        Assert.Equal(2, moved.Count);
        Assert.True(File.Exists(Path.Combine(_root, "txt", "doc1.txt")));
        Assert.True(File.Exists(Path.Combine(_root, "csv", "sheet.csv")));
    }
}

public sealed class DocumentTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "aurora-doc-" + Guid.NewGuid().ToString("N"));
    public DocumentTests() => Directory.CreateDirectory(_root);
    public void Dispose() { try { Directory.Delete(_root, true); } catch { } }

    [Fact]
    public void CreateExcelFromCsv_ProducesReadableWorkbook()
    {
        var csv = Path.Combine(_root, "data.csv");
        var xlsx = Path.Combine(_root, "report.xlsx");
        File.WriteAllText(csv, "Name,Amount\nAlpha,10\nBeta,20\n");
        var made = DocumentTools.CreateExcelFromCsv(csv, xlsx);
        Assert.True(File.Exists(made));
        using var wb = new ClosedXML.Excel.XLWorkbook(made);
        var ws = wb.Worksheets.First();
        Assert.Equal("Alpha", ws.Cell(2, 1).GetString());
        Assert.Equal(20, ws.Cell(3, 2).GetDouble());
    }

    [Fact]
    public void CreatePowerPoint_And_WordReport_ProduceFiles()
    {
        var pptx = Path.Combine(_root, "deck.pptx");
        var docx = Path.Combine(_root, "report.docx");
        Assert.True(File.Exists(DocumentTools.CreatePowerPoint("Title", new[] { ("Slide 1", "Body") }, pptx)));
        Assert.True(File.Exists(DocumentTools.CreateWordReport("Title", "Hello", docx)));
    }
}

public sealed class CodingTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "aurora-code-" + Guid.NewGuid().ToString("N"));
    public CodingTests() => Directory.CreateDirectory(_root);
    public void Dispose() { try { Directory.Delete(_root, true); } catch { } }

    [Fact]
    public async Task CreatePythonProject_And_RunTests_Passes()
    {
        var created = CodingTools.CreatePythonProject(_root, "demo");
        Assert.True(created.Success, created.Summary);
        var result = await CodingTools.RunTests(Path.Combine(_root, "demo"));
        Assert.True(result.Success, result.Details ?? result.Summary);
    }
}

public sealed class AgentEngineTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "aurora-agent-" + Guid.NewGuid().ToString("N"));
    public AgentEngineTests() => Directory.CreateDirectory(_root);
    public void Dispose() { try { Directory.Delete(_root, true); } catch { } }

    [Fact]
    public async Task AskMode_DoesNotModifyComputer()
    {
        var engine = new AgentEngine(null, "qwen3:4b", new[] { new FilesystemTools() }, new PermissionManager());
        var target = Path.Combine(_root, "ShouldNotExist");
        var task = new AgentTask("t-ask", $"Create a folder named ShouldNotExist in {_root}", Language.English, AgentMode.Ask);
        var done = await engine.RunAsync(task, CancellationToken.None);
        Assert.Equal(TaskState.Completed, done.State);
        Assert.False(Directory.Exists(target));
        Assert.Contains("no changes", done.FinalReport ?? string.Empty, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task PlanMode_StopsAtWaitingApproval_WithoutExecuting()
    {
        var engine = new AgentEngine(null, "qwen3:4b", new[] { new FilesystemTools() }, new PermissionManager());
        var task = new AgentTask("t-plan", $"Create a folder named X in {_root}", Language.English, AgentMode.Plan);
        var done = await engine.RunAsync(task, CancellationToken.None);
        Assert.Equal(TaskState.WaitingApproval, done.State);
    }

    [Fact]
    public async Task CancelledToken_ResultsInCancelledState()
    {
        var engine = new AgentEngine(null, "qwen3:4b", new[] { new FilesystemTools() }, new PermissionManager());
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        var task = new AgentTask("t-cancel", $"Create a folder named Y in {_root}", Language.English, AgentMode.Autonomous);
        var done = await engine.RunAsync(task, cts.Token);
        Assert.Equal(TaskState.Cancelled, done.State);
    }

    [Fact]
    public async Task Autonomous_CreateFolder_ActuallyCreatesIt()
    {
        var engine = new AgentEngine(null, "qwen3:4b", new[] { new FilesystemTools() }, new PermissionManager());
        var task = new AgentTask("t-auto", $"Create a folder named Business Reports in {_root}", Language.English, AgentMode.Autonomous);
        var done = await engine.RunAsync(task, CancellationToken.None);
        Assert.True(Directory.Exists(Path.Combine(_root, "Business Reports")),
            "Final report: " + (done.FinalReport ?? "(none)"));
    }
}
