using MMLib.Alvo.Testing;

namespace MMLib.Alvo.Tests.Expressions;

/// <summary>
/// Pins the invariant that makes <c>SqlPredicateRenderer</c> the core's one seam for SQL text: no
/// other file under <c>Expressions</c>/<c>Rules</c> should ever grow its own ad hoc SQL fragment.
/// A future contributor adding a second place that renders SQL (a shortcut around the renderer, or
/// a copy-pasted fragment for a "quick fix") would fragment the two-valued rendering rule and the
/// per-dialect <see cref="MMLib.Alvo.Expressions.IFieldSqlRenderer"/> seam this file exists to protect —
/// this test fails loudly the moment that happens, rather than letting it surface later as a
/// dialect-specific bug. Follows the same source-scanning pattern as
/// <c>SqlVerdictArchitectureTests</c>, since a compile-time reflection check cannot see raw string
/// literals the way a source scan can.
/// </summary>
public class SqlTextConfinedToRendererArchitectureTests
{
    private static readonly string[] _sqlTokens = ["SELECT ", "WHERE ", "ILIKE", "COALESCE(", "IS NOT NULL"];
    private const string AllowedFileName = "SqlPredicateRenderer.cs";

    [Fact]
    public void Only_SqlPredicateRenderer_contains_sql_text()
    {
        var root = RepositoryRoot.Find();
        var scannedDirectories = new[]
        {
            Path.Combine(root, "src", "MMLib.Alvo", "Expressions"),
            Path.Combine(root, "src", "MMLib.Alvo", "Rules"),
        };

        var offenders = scannedDirectories
            .SelectMany(directory => Directory.EnumerateFiles(directory, "*.cs", SearchOption.AllDirectories))
            .Where(ContainsSqlTextOutsideDocComments)
            .Where(path => Path.GetFileName(path) != AllowedFileName)
            .Select(path => Path.GetRelativePath(root, path))
            .ToList();

        offenders.ShouldBeEmpty(
            $"Only '{AllowedFileName}' may contain SQL text ({string.Join(", ", _sqlTokens)}); " +
            $"also found in: {string.Join(", ", offenders)}.");
    }

    /// <summary>
    /// Joins every line that is not an XML doc-comment (<c>///</c>), so a doc comment explaining SQL
    /// semantics in prose (which necessarily names SQL keywords) does not itself trip this check —
    /// only actual SQL-composing code does.
    /// </summary>
    private static bool ContainsSqlTextOutsideDocComments(string path)
    {
        var code = string.Join(
            '\n',
            File.ReadAllLines(path).Where(line => !line.TrimStart().StartsWith("///", StringComparison.Ordinal)));
        return _sqlTokens.Any(token => code.Contains(token, StringComparison.Ordinal));
    }
}
