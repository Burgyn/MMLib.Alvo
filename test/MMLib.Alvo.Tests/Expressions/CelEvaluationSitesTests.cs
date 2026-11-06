namespace MMLib.Alvo.Tests.Expressions;

/// <summary>
/// The interpreter's hook entry points are called from exactly the five places that run inside a host-function budget pass —
/// the state-machine guard's and the timer planner's pass is opened by <c>EntityLifecycleService</c>, their only caller — so a sixth caller — which would run host functions with no limit — is a deliberate, reviewed change.
/// </summary>
/// <remarks>
/// A textual scan of <c>src/MMLib.Alvo</c>, by file, so it has blind spots it does not try to close: a
/// <c>using static</c> of the interpreter, a method group (<c>CelInterpreter.EvaluatePredicate</c> with no
/// <c>(</c>), a second call outside the pass inside one of the listed files, and any assembly other than the
/// core. None exists today; a reviewer of a change to those files still has to check that every call sits in a pass.
/// </remarks>
public sealed class CelEvaluationSitesTests
{
    private static readonly string[] _entryPoints =
        ["CelInterpreter.EvaluatePredicate(", "CelInterpreter.EvaluateCondition(", "CelInterpreter.EvaluateMutation(", "CelInterpreter.EvaluateInstant("];

    [Fact]
    public void Only_the_budgeted_sites_call_the_interpreter_s_hook_entry_points()
    {
        var core = Path.Combine(RepositoryRoot.Find(), "src", "MMLib.Alvo");

        var callers = Directory.EnumerateFiles(core, "*.cs", SearchOption.AllDirectories)
            .Where(file => File.ReadLines(file).Any(line => !line.TrimStart().StartsWith("///", StringComparison.Ordinal)
                && _entryPoints.Any(entry => line.Contains(entry, StringComparison.Ordinal))))
            .Select(file => Path.GetRelativePath(core, file).Replace('\\', '/'))
            .Where(file => file != "Expressions/Internal/CelInterpreter.cs")
            .Order(StringComparer.Ordinal);

        callers.ShouldBe(
            ["Expressions/Internal/PredicateEvaluator.cs", "Hooks/Internal/WriteExtensionRunner.cs", "Lifecycle/EntityTimerPlanner.cs", "Lifecycle/StateMachineGuard.cs",
                "Rules/Internal/BeforeHookRunner.cs"]);
    }
}
