// Sandbox test runner (NOT part of the shipped product).
//
// Why this exists: on this Linux VM, `dotnet test` / vstest cannot run.
// vstest.console and testhost.dll talk over a localhost TCP socket, and this
// sandbox's egress proxy intercepts that traffic (testhost's own diag log
// shows its socket receiving proxy bytes on [fd8b:...]:3128, which then fail
// JSON parsing: "Error parsing undefined value"). The testhost does start
// and does connect, but the channel is corrupted by the environment.
// The tests themselves are ordinary xUnit [Fact]/[Theory] tests in
// AuroraOperator.Tests; this runner executes those exact test methods
// in-process via reflection (fresh instance per test, IDisposable honoured,
// async Task methods awaited) and reports real pass/fail results.
// On a normal Windows machine, `dotnet test` works and should be used
// instead (see TESTS.md).
using System.Reflection;
using AuroraOperator.Tests;

var testAssembly = typeof(PlannerTests).Assembly;
var passed = 0;
var failed = 0;
var failures = new List<string>();

foreach (var type in testAssembly.GetTypes().OrderBy(t => t.FullName))
{
    if (type.IsAbstract || !type.IsClass || type.Namespace != "AuroraOperator.Tests") continue;
    foreach (var method in type.GetMethods(BindingFlags.Public | BindingFlags.Instance).OrderBy(m => m.Name))
    {
        var isFact = method.GetCustomAttributes().Any(a => a.GetType().Name == "FactAttribute");
        var isTheory = method.GetCustomAttributes().Any(a => a.GetType().Name == "TheoryAttribute");
        if (!isFact && !isTheory) continue;

        var cases = new List<(string Name, object?[] Args)>();
        if (isFact) cases.Add((method.Name, Array.Empty<object?>()));
        else
        {
            foreach (var attr in method.GetCustomAttributes())
            {
                if (attr.GetType().Name != "InlineDataAttribute") continue;
                // DataAttribute.GetData(MethodInfo) is the documented xUnit
                // API: one object?[] per case.
                var getData = attr.GetType().GetMethod("GetData", new[] { typeof(MethodInfo) });
                var rows = getData?.Invoke(attr, new object[] { method }) as System.Collections.IEnumerable;
                if (rows is null) continue;
                foreach (var row in rows)
                {
                    var values = (row as object[]) ?? Array.Empty<object?>();
                    cases.Add(($"{method.Name}({string.Join(", ", values.Select(v => v?.ToString()))})", values));
                }
            }
        }

        foreach (var (caseName, caseArgs) in cases)
        {
            var display = $"{type.Name}.{caseName}";
            object? instance = null;
            try
            {
                instance = Activator.CreateInstance(type);
                var result = method.Invoke(instance, caseArgs);
                if (result is Task task) await task.ConfigureAwait(false);
                Console.WriteLine($"PASS {display}");
                passed++;
            }
            catch (Exception ex)
            {
                var inner = ex is TargetInvocationException tie ? tie.InnerException ?? ex : ex;
                Console.WriteLine($"FAIL {display}: {inner.GetType().Name}: {inner.Message.Split('\n')[0]}");
                failed++;
                failures.Add(display + ": " + inner.Message.Split('\n')[0]);
            }
            finally
            {
                if (instance is IDisposable d)
                    try { d.Dispose(); } catch { }
            }
        }
    }
}

Console.WriteLine();
Console.WriteLine($"Test run complete: {passed} passed, {failed} failed, {passed + failed} total.");
if (failures.Count > 0)
{
    Console.WriteLine("Failures:");
    foreach (var f in failures) Console.WriteLine(" - " + f);
}
return failed == 0 ? 0 : 1;
