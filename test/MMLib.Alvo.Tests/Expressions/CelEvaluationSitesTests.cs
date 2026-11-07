namespace MMLib.Alvo.Tests.Expressions;

/// <summary>
/// The interpreter's hook entry points are called from exactly the six places that run inside a host-function budget pass —
/// the state-machine guard's and the timer planner's pass is opened by <c>EntityLifecycleService</c>, their only caller — so a seventh caller — which would run host functions with no limit — is a deliberate, reviewed change.
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
        var callers = CallersOf(_entryPoints).Where(file => file != "Expressions/Internal/CelInterpreter.cs");

        callers.ShouldBe(
            ["Events/Internal/ActionValues.cs", "Expressions/Internal/PredicateEvaluator.cs", "Hooks/Internal/WriteExtensionRunner.cs", "Lifecycle/EntityTimerPlanner.cs", "Lifecycle/StateMachineGuard.cs",
                "Rules/Internal/BeforeHookRunner.cs"]);
    }

    /// <summary>
    /// The guard and the planner open no budget pass of their own: <c>EntityLifecycleService</c> opens it around them. So a
    /// second caller of either would run host functions unbudgeted, and is a deliberate, reviewed change.
    /// </summary>
    [Fact]
    public void Only_the_lifecycle_service_runs_the_state_machine_guard_and_the_timer_planner() =>
        CallersOf(["StateMachineGuard.Ensure(", "EntityTimerPlanner.Plan("]).ShouldBe(["Lifecycle/EntityLifecycleService.cs"]);

    private static IOrderedEnumerable<string> CallersOf(string[] calls)
    {
        var core = Path.Combine(RepositoryRoot.Find(), "src", "MMLib.Alvo");

        return Directory.EnumerateFiles(core, "*.cs", SearchOption.AllDirectories)
            .Where(file => File.ReadLines(file).Any(line => !line.TrimStart().StartsWith("///", StringComparison.Ordinal)
                && calls.Any(call => line.Contains(call, StringComparison.Ordinal))))
            .Select(file => Path.GetRelativePath(core, file).Replace('\\', '/'))
            .Order(StringComparer.Ordinal);
    }
}
