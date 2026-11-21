using System.Diagnostics;
using AuroraOperator.Core;
using AuroraOperator.Core.Security;
using AuroraOperator.Core.Tools;

namespace AuroraOperator.Tests;

/// <summary>
/// Final-release tests (all Linux-runnable; no real Ollama server, no
/// Chromium, no Windows UI). Covers the final-release fixes:
/// <list type="bullet">
/// <item>F20 PID-scoped close: closing by name with several same-named
/// processes closes NOTHING; closing by PID closes exactly that
/// process; graceful close first, force only when separately approved
/// (force=true in the approved args); wrong-name PID and protected
/// processes are refused.</item>
/// <item>F5 defense in depth: the raw ProcessAppControl primitive now
/// refuses Rejected launch targets (URLs, scripts, installers) even
/// without the engine in front of it.</item>
/// <item>Approval dialog content: the approval request's exact text now
/// carries the canonical parameters, so a close-app approval names
/// the exact PID and whether force termination is included.</item>
/// </list>
/// Windows-only behaviour (WPF runtime, Notepad UI flow, Chromium
/// launch, installer execution, DPAPI on Windows) stays BLOCKED in
/// tests/windows/WINDOWS_ACCEPTANCE_CHECKLIST.md.
/// </summary>
public sealed class Phase9Tests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "aurora-phase9-" + Guid.NewGuid().ToString("N"));
    public Phase9Tests() => Directory.CreateDirectory(_root);
    public void Dispose() { try { Directory.Delete(_root, true); } catch { } }

    // ---- F5: raw primitive refuses Rejected targets ----

    [Theory]
    [InlineData("https://example.com/x.exe")]
    [InlineData("http://example.com/setup.exe")]
    [InlineData("ms-settings:about")]
    [InlineData("evil.bat")]
    [InlineData("setup.msi")]
    [InlineData("script.ps1")]
    [InlineData("shortcut.lnk")]
    public void ProcessAppControl_LaunchApp_RejectsDangerousTargets(string target)
    {
        Assert.False(new ProcessAppControl().LaunchApp(target), target);
    }

    // ---- F20: PID-scoped close (Linux: python sleeper processes) ----

    private string WriteSleeper(string name)
    {
        var script = Path.Combine(_root, name);
        File.WriteAllText(script, "#!/usr/bin/env python3\nimport time\ntime.sleep(120)\n");
        if (!OperatingSystem.IsLinux()) return script;
        File.SetUnixFileMode(script,
            UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute |
            UnixFileMode.GroupRead | UnixFileMode.OtherRead);
        return script;
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
            }
            finally { p.Dispose(); }
        }
        return null;
    }

    private static int LaunchSleeper(ProcessAppControl control, string script)
    {
        Assert.True(control.LaunchApp(script), "Launching the sleeper script must succeed.");
        int? pid = null;
        var deadline = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(5);
        while (DateTimeOffset.UtcNow < deadline && pid is null)
        {
            pid = FindPythonPidForScript(script);
            if (pid is null) Thread.Sleep(200);
        }
        Assert.True(pid.HasValue, "The launched python process did not appear in the process list.");
        return pid.Value;
    }

    private static void KillQuietly(int pid)
    {
        try
        {
            using var p = Process.GetProcessById(pid);
            if (!p.HasExited) { p.Kill(entireProcessTree: true); p.WaitForExit(5000); }
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
        }
    }

    private static bool IsAlive(int pid)
    {
        try
        {
            using var p = Process.GetProcessById(pid);
            return !p.HasExited;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    [Fact]
    public void CloseByName_MultipleSameNamedProcesses_ClosesNothing()
    {
        if (!OperatingSystem.IsLinux()) return;
        var control = new ProcessAppControl();
        var pid1 = LaunchSleeper(control, WriteSleeper("aurora-sleeper-a.py"));
        var pid2 = LaunchSleeper(control, WriteSleeper("aurora-sleeper-b.py"));
        try
        {
            // Two python3 processes exist: closing by name must refuse
            // and leave BOTH alive — the old behaviour closed them all.
            var (ok, message) = AppProcessCloser.CloseByName("python3", forceKill: false);
            Assert.False(ok);
            Assert.Contains(pid1.ToString(), message, StringComparison.Ordinal);
            Assert.Contains(pid2.ToString(), message, StringComparison.Ordinal);
            Assert.True(IsAlive(pid1), "First sleeper must still be alive after the refused close.");
            Assert.True(IsAlive(pid2), "Second sleeper must still be alive after the refused close.");
        }
        finally
        {
            KillQuietly(pid1);
            KillQuietly(pid2);
        }
    }

    [Fact]
    public void CloseProcessById_ClosesOnlyThatPid()
    {
        if (!OperatingSystem.IsLinux()) return;
        var control = new ProcessAppControl();
        var pid1 = LaunchSleeper(control, WriteSleeper("aurora-sleeper-c.py"));
        var pid2 = LaunchSleeper(control, WriteSleeper("aurora-sleeper-d.py"));
        try
        {
            // Sleepers have no window, so a graceful close cannot work;
            // force is a separately approved action here.
            var (ok, _) = AppProcessCloser.CloseProcessById(pid1, forceKill: true, expectedName: "python3");
            Assert.True(ok, "Force-closing the one PID must succeed.");
            Assert.False(IsAlive(pid1), "The closed PID must have exited.");
            Assert.True(IsAlive(pid2), "The other same-named process must still be alive.");
        }
        finally
        {
            KillQuietly(pid1);
            KillQuietly(pid2);
        }
    }

    [Fact]
    public void CloseProcessById_GracefulFailure_DoesNotForceKill()
    {
        if (!OperatingSystem.IsLinux()) return;
        var control = new ProcessAppControl();
        var pid = LaunchSleeper(control, WriteSleeper("aurora-sleeper-e.py"));
        try
        {
            // No window to close gracefully, no force flag: the process
            // must survive and the result must say it was not forced.
            var (ok, message) = AppProcessCloser.CloseProcessById(pid, forceKill: false, expectedName: "python3");
            Assert.False(ok);
            Assert.Contains("NOT force-terminated", message, StringComparison.Ordinal);
            Assert.True(IsAlive(pid), "Without force, the process must still be alive.");
        }
        finally
        {
            KillQuietly(pid);
        }
    }

    [Fact]
    public void CloseProcessById_WrongName_And_UnknownPid_Refused()
    {
        if (!OperatingSystem.IsLinux()) return;
        var control = new ProcessAppControl();
        var pid = LaunchSleeper(control, WriteSleeper("aurora-sleeper-f.py"));
        try
        {
            var (ok, _) = AppProcessCloser.CloseProcessById(pid, forceKill: true, expectedName: "notepad");
            Assert.False(ok, "A PID whose real name does not match the named app must never be closed.");
            Assert.True(IsAlive(pid));

            var (ok2, _) = AppProcessCloser.CloseProcessById(999999, forceKill: true);
            Assert.False(ok2, "A PID with no running process must fail, not succeed.");
        }
        finally
        {
            KillQuietly(pid);
        }
    }

    [Fact]
    public async Task AppTools_CloseApp_Ambiguous_ListsPids_And_ClosesNothing()
    {
        if (!OperatingSystem.IsLinux()) return;
        var control = new ProcessAppControl();
        var tool = new AppTools(control);
        var pid1 = LaunchSleeper(control, WriteSleeper("aurora-sleeper-g.py"));
        var pid2 = LaunchSleeper(control, WriteSleeper("aurora-sleeper-h.py"));
        try
        {
            var result = await tool.ExecuteAsync(new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["action"] = "close-app",
                ["app"] = "python3",
            }, CancellationToken.None);
            Assert.False(result.Success);
            Assert.Contains(pid1.ToString(), result.Summary, StringComparison.Ordinal);
            Assert.Contains(pid2.ToString(), result.Summary, StringComparison.Ordinal);
            Assert.True(IsAlive(pid1));
            Assert.True(IsAlive(pid2));
        }
        finally
        {
            KillQuietly(pid1);
            KillQuietly(pid2);
        }
    }

    [Fact]
    public async Task AppTools_CloseApp_ByPid_ForceSeparatelyApproved()
    {
        if (!OperatingSystem.IsLinux()) return;
        var control = new ProcessAppControl();
        var tool = new AppTools(control);
        var pid = LaunchSleeper(control, WriteSleeper("aurora-sleeper-i.py"));
        try
        {
            // Without force: graceful close fails (no window), process survives.
            var noForce = await tool.ExecuteAsync(new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["action"] = "close-app",
                ["app"] = "python3",
                ["pid"] = pid.ToString(),
            }, CancellationToken.None);
            Assert.False(noForce.Success);
            Assert.True(IsAlive(pid));

            // With force=true in the (approved) args: this one PID closes.
            var forced = await tool.ExecuteAsync(new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["action"] = "close-app",
                ["app"] = "python3",
                ["pid"] = pid.ToString(),
                ["force"] = "true",
            }, CancellationToken.None);
            Assert.True(forced.Success, forced.Summary);
            Assert.False(IsAlive(pid));
        }
        finally
        {
            KillQuietly(pid);
        }
    }

    // ---- Approval dialog content: exact parameters are visible ----

    [Fact]
    public void ApprovalRequest_ExactText_NamesPidAndForce()
    {
        var step = new PlanStep("s1", "Close Notepad", "app", "Close the Notepad process.",
            RequiresApproval: true)
        {
            Args = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["action"] = "close-app",
                ["app"] = "notepad",
                ["pid"] = "1234",
                ["force"] = "true",
            },
        };
        var canonical = ApprovalService.CanonicalParameters(step.ToolName, step.Args, step.Description);
        var request = ApprovalService.CreateRequest("t1", step, RiskLevel.SensitiveFileModification, canonical);
        Assert.Contains("pid=1234", request.ExactCommandOrChange, StringComparison.Ordinal);
        Assert.Contains("force=true", request.ExactCommandOrChange, StringComparison.Ordinal);
        Assert.Contains("pid=1234", request.ExactParameters, StringComparison.Ordinal);
    }

    [Fact]
    public void FindProcesses_ListsPid_Path_And_SafetyFlag()
    {
        if (!OperatingSystem.IsLinux()) return;
        var control = new ProcessAppControl();
        var pid = LaunchSleeper(control, WriteSleeper("aurora-sleeper-j.py"));
        try
        {
            var matches = control.FindProcesses("python3");
            var mine = matches.FirstOrDefault(m => m.Id == pid);
            Assert.NotNull(mine);
            Assert.Equal("python3", mine.Name);
            Assert.True(mine.SafeToClose);
            Assert.True(mine.StartTime > DateTime.MinValue);
        }
        finally
        {
            KillQuietly(pid);
        }
    }
}
