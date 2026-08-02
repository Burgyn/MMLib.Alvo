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
    /// Fragments that only ever appear in composed SQL. Each already carries punctuation or a space,
    /// so a plain substring match is precise enough.
    /// </summary>
    private static readonly string[] _sqlFragments =
    [
        "SELECT ", "WHERE ", "ILIKE", "COALESCE(", "IS NOT NULL", "CASE WHEN ", " IN (",
    ];

    /// <summary>
    /// The three boolean connectives the renderer composes with. These have to be matched bare to
    /// catch every form the renderer itself uses — the string literals <c>"AND"</c>/<c>"OR"</c> it
    /// picks between, and the interpolated <c>$"(NOT {operand})"</c> — but a bare substring match
    /// would fire on ordinary all-caps identifiers that merely contain one (<c>ERROR_CODE</c>,
    /// <c>OPERAND</c>, <c>COMMAND</c>). They are therefore matched as whole uppercase words: a hit
    /// counts only when neither neighbouring character could be part of the same identifier. That
    /// keeps the token at full strength rather than diluting it with surrounding spaces, which is
    /// what let a copy-paste of the renderer's own <c>NOT</c> and bare <c>"OR"</c> idioms through.
    /// </summary>
    private static readonly string[] _sqlConnectives = ["AND", "OR", "NOT"];

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
            .Where(ContainsSqlTextOutsideComments)
            .Where(path => !_allowedFileNames.Contains(Path.GetFileName(path), StringComparer.Ordinal))
            .Select(path => Path.GetRelativePath(root, path))
            .ToList();

        offenders.ShouldBeEmpty(
            $"Only {string.Join(" and ", _allowedFileNames)} may contain SQL text "
            + $"({string.Join(", ", [.. _sqlFragments, .. _sqlConnectives])}); "
            + $"also found in: {string.Join(", ", offenders)}.");
    }

    /// <summary>
    /// Joins every line that is not a comment, so prose explaining SQL semantics (which necessarily
    /// names SQL keywords) does not itself trip this check — only actual SQL-composing code does.
    /// Both <c>///</c> and plain <c>//</c> are stripped: an ordinary comment mentioning a connective
    /// is a false positive, and one surprising a contributor is worse than the marginal reach of
    /// scanning it.
    /// </summary>
    private static bool ContainsSqlTextOutsideComments(string path)
    {
        var code = string.Join('\n', File.ReadAllLines(path).Where(IsNotAComment));
        return _sqlFragments.Any(fragment => code.Contains(fragment, StringComparison.Ordinal))
            || _sqlConnectives.Any(connective => ContainsAsWholeWord(code, connective));
    }

    private static bool IsNotAComment(string line) => !line.TrimStart().StartsWith("//", StringComparison.Ordinal);

    private static bool ContainsAsWholeWord(string code, string word)
    {
        for (var found = code.IndexOf(word, StringComparison.Ordinal);
             found >= 0;
             found = code.IndexOf(word, found + 1, StringComparison.Ordinal))
        {
            if (!IsIdentifierCharAt(code, found - 1) && !IsIdentifierCharAt(code, found + word.Length))
            {
                return true;
            }
        }

        return false;
    }

    private static bool IsIdentifierCharAt(string code, int index) =>
        index >= 0 && index < code.Length && (char.IsLetterOrDigit(code[index]) || code[index] == '_');
}
