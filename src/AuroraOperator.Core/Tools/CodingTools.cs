using System.Diagnostics;
using System.Text;
using AuroraOperator.Core.Agent;

namespace AuroraOperator.Core.Tools;

/// <summary>
/// Coding tool ("coding"): scaffolds a small Python project (main.py, a test
/// file that works under both pytest and unittest, README) and runs its tests,
/// capturing the real stdout/stderr and exit code. Nothing is faked: if Python
/// or pytest is missing, the returned ToolResult says so.
/// </summary>
public sealed class CodingTools : IAgentTool
{
    public string Name => "coding";
    public string Description => "Create a Python project and run its tests (pytest, falling back to unittest).";

    public Task<ToolResult> ExecuteAsync(IReadOnlyDictionary<string, string> args, CancellationToken ct)
    {
        var action = (Get(args, "action") ?? string.Empty).Trim().ToLowerInvariant();
        var path = Get(args, "path") ?? Get(args, "projectDir") ?? Get(args, "dir") ?? ".";
        var name = Get(args, "name") ?? "app";

        try
        {
            return action switch
            {
                "create-project" => Task.FromResult(CreatePythonProject(path, name)),
                "run-tests" => RunTestsAsync(path, ct),
                _ => Task.FromResult(new ToolResult(false,
                    $"Unknown coding action '{action}'. Supported: create-project, run-tests.")),
            };
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return Task.FromResult(new ToolResult(false, $"Coding action '{action}' failed: {ex.Message}", ex.ToString()));
        }
    }

    private static string? Get(IReadOnlyDictionary<string, string> args, string key)
    {
        foreach (var kv in args)
            if (string.Equals(kv.Key, key, StringComparison.OrdinalIgnoreCase))
                return kv.Value;
        return null;
    }

    /// <summary>Creates dir/name with main.py, test_main.py and README.md. Returns the project result.</summary>
    public static ToolResult CreatePythonProject(string dir, string name)
    {
        var safeName = string.IsNullOrWhiteSpace(name) ? "app" : name.Trim();
        foreach (var bad in Path.GetInvalidFileNameChars())
            safeName = safeName.Replace(bad, '_');
        var projectDir = Path.GetFullPath(Path.Combine(dir, safeName));
        Directory.CreateDirectory(projectDir);

        var mainPy = """
            # Entry point for the project.


            def greet(name: str = "world") -> str:
                # Return a greeting for *name*.
                return f"Hello, {name}!"


            def main() -> None:
                print(greet())


            if __name__ == "__main__":
                main()
            """;
        var testPy = """
            # Tests for main.py. Runs under both pytest and unittest.

            import unittest

            from main import greet


            class TestMain(unittest.TestCase):
                def test_greet_default(self):
                    self.assertEqual(greet(), "Hello, world!")

                def test_greet_named(self):
                    self.assertEqual(greet("Aurora"), "Hello, Aurora!")

                def test_main_runs(self):
                    import main

                    self.assertTrue(callable(main.main))


            if __name__ == "__main__":
                unittest.main()
            """;
        var readme = $"""
            # {safeName}

            A small Python project created by Aurora Operator.

            ## Run

            ```sh
            python main.py
            ```

            ## Test

            ```sh
            python -m pytest -q
            ```

            If pytest is not installed, the same tests run with the standard library:

            ```sh
            python -m unittest discover -v
            ```
            """;

        var mainPath = Path.Combine(projectDir, "main.py");
        var testPath = Path.Combine(projectDir, "test_main.py");
        var readmePath = Path.Combine(projectDir, "README.md");
        File.WriteAllText(mainPath, mainPy.TrimStart() + "\n");
        File.WriteAllText(testPath, testPy.TrimStart() + "\n");
        File.WriteAllText(readmePath, readme.TrimStart() + "\n");

        return new ToolResult(true, $"Python project created at {projectDir}.",
            OutputPath: projectDir,
            Artifacts: new[] { mainPath, testPath, readmePath });
    }

    /// <summary>Runs the project's tests and returns the real output and exit code.</summary>
    public static Task<ToolResult> RunTests(string projectDir) => RunTestsAsync(projectDir, CancellationToken.None);

    private static async Task<ToolResult> RunTestsAsync(string projectDir, CancellationToken ct)
    {
        if (!Directory.Exists(projectDir))
            return new ToolResult(false, $"Project directory not found: {projectDir}");

        var candidates = OperatingSystem.IsWindows()
            ? new[] { "python", "py" }
            : new[] { "python3", "python" };

        Exception? lastStartError = null;
        foreach (var python in candidates)
        {
            // First try pytest.
            var pytest = await TryRunAsync(python, "-m pytest -q", projectDir, ct).ConfigureAwait(false);
            if (pytest.StartFailed)
            {
                lastStartError = pytest.Error;
                continue; // try the next python executable
            }
            if (pytest.Combined.Contains("No module named pytest", StringComparison.OrdinalIgnoreCase) ||
                pytest.Combined.Contains("No module named 'pytest'", StringComparison.OrdinalIgnoreCase))
            {
                var unittest = await TryRunAsync(python, "-m unittest discover -v", projectDir, ct).ConfigureAwait(false);
                return BuildResult("unittest", unittest);
            }
            return BuildResult("pytest", pytest);
        }
        return new ToolResult(false,
            "Python was not found on PATH (tried: " + string.Join(", ", candidates) + "). " +
            "Install Python from https://www.python.org/downloads/ and try again." +
            (lastStartError is not null ? " Last error: " + lastStartError.Message : string.Empty));
    }

    private static ToolResult BuildResult(string runner, ProcessRun run)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"Test runner: {runner}");
        sb.AppendLine($"Exit code: {run.ExitCode}");
        if (!string.IsNullOrWhiteSpace(run.StdOut)) { sb.AppendLine("--- stdout ---"); sb.AppendLine(run.StdOut.TrimEnd()); }
        if (!string.IsNullOrWhiteSpace(run.StdErr)) { sb.AppendLine("--- stderr ---"); sb.AppendLine(run.StdErr.TrimEnd()); }
        var summary = run.ExitCode == 0
            ? $"Tests passed ({runner}, exit code 0)."
            : $"Tests failed ({runner}, exit code {run.ExitCode}).";
        return new ToolResult(run.ExitCode == 0, summary, sb.ToString());
    }

    private sealed record ProcessRun(bool StartFailed, int ExitCode, string StdOut, string StdErr, Exception? Error)
    {
        public string Combined => StdOut + "\n" + StdErr;
    }

    private static async Task<ProcessRun> TryRunAsync(string fileName, string arguments, string workDir, CancellationToken ct)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = fileName,
            Arguments = arguments,
            WorkingDirectory = workDir,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        Process? process;
        try
        {
            process = Process.Start(startInfo);
        }
        catch (Exception ex)
        {
            return new ProcessRun(true, -1, string.Empty, string.Empty, ex);
        }
        if (process is null)
            return new ProcessRun(true, -1, string.Empty, string.Empty, new InvalidOperationException("Process.Start returned null."));
        using (process)
        {
            try
            {
                var outTask = process.StandardOutput.ReadToEndAsync(ct);
                var errTask = process.StandardError.ReadToEndAsync(ct);
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
                timeout.CancelAfter(TimeSpan.FromMinutes(5));
                await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
                var stdout = await outTask.ConfigureAwait(false);
                var stderr = await errTask.ConfigureAwait(false);
                return new ProcessRun(false, process.ExitCode, stdout, stderr, null);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                try { process.Kill(entireProcessTree: true); } catch (Exception) { /* already exited */ }
                return new ProcessRun(false, -1, string.Empty, "Test run timed out after 5 minutes.", null);
            }
        }
    }
}
