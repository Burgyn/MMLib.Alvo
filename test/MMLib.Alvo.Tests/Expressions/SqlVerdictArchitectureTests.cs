namespace MMLib.Alvo.Tests.Expressions;

/// <summary>
/// Pins the differential test's independence invariant at the source level: <see cref="SqlVerdict"/>
/// must never reference <c>CelInterpreter</c> in code, the very backend it is meant to check
/// <c>SqlPredicateRenderer</c> against. A future edit that made the SQL evaluator delegate to (or
/// reuse code from) the in-memory interpreter would make <c>DifferentialBackendTests</c> compare a
/// backend against itself — passing for a reason that proves nothing.
/// </summary>
public class SqlVerdictArchitectureTests
{
    [Fact]
    public void SqlVerdict_does_not_reference_CelInterpreter_in_code()
    {
        var path = Path.Combine(RepositoryRoot.Find(), "test", "MMLib.Alvo.Tests", "Expressions", "SqlVerdict.cs");

        var code = CodeLines(File.ReadAllText(path));

        code.ShouldNotContain("CelInterpreter");
    }

    /// <summary>
    /// Joins every line that is not an XML doc-comment (<c>///</c>), so a doc comment explaining why
    /// this file is independent of <c>CelInterpreter</c> — which necessarily names it — does not
    /// itself trip the check this test performs.
    /// </summary>
    private static string CodeLines(string source) => string.Join(
        '\n',
        source.Split('\n').Where(line => !line.TrimStart().StartsWith("///", StringComparison.Ordinal)));
}
