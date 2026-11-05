namespace MMLib.Alvo.Tests.Expressions;

/// <summary>
/// The interpreter's hook entry points are called from exactly the three places that open a host-function budget pass, so
/// a fourth caller — which would run host functions with no limit — is a deliberate, reviewed change.
/// </summary>
public sealed class CelEvaluationSitesTests
{
    private static readonly string[] _entryPoints =
        ["CelInterpreter.EvaluatePredicate(", "CelInterpreter.EvaluateCondition(", "CelInterpreter.EvaluateMutation("];

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

        callers.ShouldBe(["Expressions/Internal/PredicateEvaluator.cs", "Hooks/Internal/WriteExtensionRunner.cs", "Rules/Internal/BeforeHookRunner.cs"]);
    }
}
