using System.Text.RegularExpressions;

namespace AuroraOperator.Tests;

/// <summary>
/// Regression tests for the NSIS installer nsExec pop-order bug
/// (2026-11-22, found on the user's real Windows PC).
///
/// Root cause: nsExec::ExecToStack pushes the RETURN VALUE (exit code)
/// on top of the stack and the printed output BELOW it, so the FIRST
/// Pop is the exit code and the SECOND Pop is the output (NSIS wiki,
/// "NsExec plug-in": Pop $0 # return value/error/timeout, Pop $1 #
/// printed text). The shipped 0.2.0 installer popped them backwards
/// at every call site, so the Ollama poll compared the (empty) output
/// to "0" and could never succeed — a real install failed with
/// "Ollama did not start (its local API did not answer within ~30
/// seconds)" even though `ollama list` answered fine. RAM always fell
/// back to 4 GB, the disk check was silently skipped, and model
/// verification / health check could never pass either.
///
/// These tests are static checks over the .nsi source (the installer
/// itself only runs on Windows), but they pin the exact contract:
/// if they fail, the pops are backwards again. They locate the repo
/// root by walking up from the test assembly until AuroraOperator.sln
/// is found, so they work both under `dotnet test` and the in-process
/// TestRunner on Linux and on Windows.
/// </summary>
public sealed class NsisInstallerTests
{
    private static readonly string NsiPath = FindNsiPath();

    private static string FindNsiPath()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        for (var i = 0; i < 10 && dir is not null; i++, dir = dir.Parent)
        {
            var candidate = Path.Combine(dir.FullName, "AuroraOperator.sln");
            if (File.Exists(candidate))
                return Path.Combine(dir.FullName, "src", "AuroraOperator.Installer", "AuroraOperator.nsi");
        }
        throw new FileNotFoundException(
            "Could not locate AuroraOperator.sln by walking up from " + AppContext.BaseDirectory);
    }

    private static string[] NsiLines() => File.ReadAllLines(NsiPath);

    /// <summary>Returns (exitCodeVar, outputVar) popped after each ExecToStack.</summary>
    private static List<(string ExitCodeVar, string OutputVar, int Line)> PopPairs()
    {
        var lines = NsiLines();
        var pairs = new List<(string, string, int)>();
        var popRe = new Regex(@"^\s*Pop\s+(\$\w+)", RegexOptions.Compiled);
        for (var i = 0; i < lines.Length; i++)
        {
            var trimmed = lines[i].TrimStart();
            // Skip comment lines: the header and the RAM comment both
            // mention ExecToStack in prose.
            if (trimmed.StartsWith(";", StringComparison.Ordinal)) continue;
            if (!lines[i].Contains("nsExec::ExecToStack", StringComparison.Ordinal)) continue;
            var pops = new List<string>();
            for (var j = i + 1; j < lines.Length && pops.Count < 2 && j <= i + 4; j++)
            {
                var m = popRe.Match(lines[j]);
                if (m.Success) pops.Add(m.Groups[1].Value);
            }
            Assert.Equal(2, pops.Count);
            pairs.Add((pops[0], pops[1], i + 1));
        }
        return pairs;
    }

    [Fact]
    public void Nsis_PopOrder_ContractCommentPresent()
    {
        var text = File.ReadAllText(NsiPath);
        Assert.Contains("FIRST Pop is the exit code", text);
        Assert.DoesNotContain("so the first Pop is output", text);
        Assert.DoesNotContain("pushes the output on top", text);
    }

    [Fact]
    public void Nsis_EveryExecToStack_PopsExitCodeFirst()
    {
        var pairs = PopPairs();
        // 10 ExecToStack sites: RAM, where-ollama, disk space, .NET
        // runtime, Ollama size, poll loop, final poll, model verify,
        // smoke test, health check. If a site is added, update this
        // count — and make sure it pops $0 (exit code) first.
        Assert.Equal(10, pairs.Count);
        foreach (var (exitCodeVar, outputVar, line) in pairs)
        {
            Assert.Equal("$0", exitCodeVar);
            Assert.NotEqual("$0", outputVar);
        }
    }

    [Fact]
    public void Nsis_NoOutputVariableComparedToZeroAsExitCode()
    {
        var text = File.ReadAllText(NsiPath);
        // These were the buggy comparisons: $1 holds printed output,
        // never the exit code, so comparing it to "0" can never mean
        // "the command succeeded".
        Assert.DoesNotContain("${If} $1 == \"0\"", text);
        Assert.DoesNotContain("${If} $1 != \"0\"", text);
    }

    [Fact]
    public void Nsis_PollLoop_BreaksOnExitCodeZero()
    {
        var lines = NsiLines();
        var pollBreaks = 0;
        for (var i = 0; i < lines.Length; i++)
        {
            if (!lines[i].Contains("list > nul", StringComparison.Ordinal)) continue;
            // The next few lines must contain a break/rollback gated
            // on the EXIT CODE ($0), not on the output ($1).
            var window = string.Join("\n", lines.Skip(i).Take(6));
            if (window.Contains("${If} $0 == \"0\"", StringComparison.Ordinal)) pollBreaks++;
            if (window.Contains("${If} $0 != \"0\"", StringComparison.Ordinal)) pollBreaks++;
        }
        // Poll loop break + final poll check.
        Assert.Equal(2, pollBreaks);
    }

    [Fact]
    public void Nsis_ExecToLog_StillPopsOnlyTheReturnCode()
    {
        // ExecToLog prints output to the log window and pushes ONLY the
        // return code (NSIS wiki). The model-pull site relies on this:
        // a single Pop $0 compared to "0". Guard it against being
        // "fixed" into a two-pop pattern by mistake.
        var text = File.ReadAllText(NsiPath);
        Assert.Contains("nsExec::ExecToLog", text);
        var lines = NsiLines();
        for (var i = 0; i < lines.Length; i++)
        {
            if (lines[i].TrimStart().StartsWith(";", StringComparison.Ordinal)) continue;
            if (!lines[i].Contains("nsExec::ExecToLog", StringComparison.Ordinal)) continue;
            var nextPop = lines.Skip(i + 1).FirstOrDefault(l => l.TrimStart().StartsWith("Pop ", StringComparison.Ordinal));
            Assert.NotNull(nextPop);
            Assert.Contains("Pop $0", nextPop);
        }
    }
}
