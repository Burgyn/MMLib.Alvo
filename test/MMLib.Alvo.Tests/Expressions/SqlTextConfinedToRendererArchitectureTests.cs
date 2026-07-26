using MMLib.Alvo.Testing;

namespace MMLib.Alvo.Tests.Expressions;

/// <summary>
/// Pins the invariant that makes <c>SqlPredicateRenderer</c> the core's one seam for SQL text: no
/// other file in the expression/rules core — in <c>MMLib.Alvo</c> or in <c>Abstractions</c> — should
/// ever grow its own ad hoc SQL fragment. A future contributor adding a second place that renders SQL
/// (a shortcut around the renderer, or a copy-pasted fragment for a "quick fix") would fragment the
/// two-valued rendering rule and the per-dialect <see cref="MMLib.Alvo.Expressions.IFieldSqlRenderer"/>
/// seam this file exists to protect — this test fails loudly the moment that happens, rather than
/// letting it surface later as a dialect-specific bug. Follows the same source-scanning pattern as
/// <c>SqlVerdictArchitectureTests</c>, since a compile-time reflection check cannot see raw string
/// literals the way a source scan can.
/// </summary>
public class SqlTextConfinedToRendererArchitectureTests
{
    /// <summary>
    /// Fragments that only ever appear in composed SQL, chosen for precision as well as reach. The
    /// boolean connective <c>AND</c> is matched bare and case-sensitively, so it catches both the
    /// string-literal form the renderer uses and the interpolated form a copy-paste would take;
    /// <c>OR</c> is matched with its spaces instead, because bare <c>OR</c> is a substring of ordinary
    /// all-caps identifiers (<c>ERROR_CODE</c>) and would fail on innocent code.
    /// </summary>
    private static readonly string[] _sqlTokens =
    [
        "SELECT ", "WHERE ", "ILIKE", "COALESCE(", "IS NOT NULL", "CASE WHEN ", " IN (", "AND", " OR ",
    ];

    /// <summary>
    /// The only two files allowed to spell SQL, and why the second is not a hole in the invariant.
    /// <c>SqlPredicateRenderer</c> is the core's one composer of SQL <i>structure</i>.
    /// <c>IFieldSqlRenderer</c> is the dialect seam itself, and its three two-valued members ship as
    /// <b>default interface members</b> precisely so that adding them broke no existing implementation
    /// — a default implementation is a body, and a body that folds SQL's <c>UNKNOWN</c> into
    /// <c>FALSE</c> has to spell the fold. It composes no structure and reads no field: everything it
    /// contains is one dialect's shape, which a dialect that disagrees overrides. Widening this scan to
    /// <c>Abstractions</c> and naming the file is the honest form of the invariant — leaving
    /// <c>Abstractions</c> unscanned would have kept the test green while the SQL text moved out from
    /// under the name the test claims.
    /// </summary>
    private static readonly string[] _allowedFileNames = ["SqlPredicateRenderer.cs", "IFieldSqlRenderer.cs"];

    private static readonly string[][] _scannedDirectories =
    [
        ["src", "MMLib.Alvo", "Expressions"],
        ["src", "MMLib.Alvo", "Rules"],
        ["src", "MMLib.Alvo.Abstractions", "Expressions"],
        ["src", "MMLib.Alvo.Abstractions", "Rules"],
    ];

    [Fact]
    public void Only_the_renderer_and_the_dialect_seam_contain_sql_text()
    {
        var root = RepositoryRoot.Find();

        var offenders = _scannedDirectories
            .Select(segments => Path.Combine([root, .. segments]))
            .SelectMany(directory => Directory.EnumerateFiles(directory, "*.cs", SearchOption.AllDirectories))
            .Where(ContainsSqlTextOutsideDocComments)
            .Where(path => !_allowedFileNames.Contains(Path.GetFileName(path), StringComparer.Ordinal))
            .Select(path => Path.GetRelativePath(root, path))
            .ToList();

        offenders.ShouldBeEmpty(
            $"Only {string.Join(" and ", _allowedFileNames)} may contain SQL text "
            + $"({string.Join(", ", _sqlTokens)}); also found in: {string.Join(", ", offenders)}.");
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
