using MMLib.Alvo.Expressions;
using MMLib.Alvo.Expressions.Internal;

namespace MMLib.Alvo.Tests.Expressions;

/// <summary>
/// Proves the renderer's core invariant structurally, across a matrix of rules: every emitted SQL
/// fragment either is already two-valued by its own shape (a bare <c>TRUE</c>/<c>FALSE</c>, an
/// <c>IS NOT NULL</c> test) or is wrapped in <c>COALESCE(..., FALSE)</c> — never left as a bare
/// comparison an engine could evaluate to <c>UNKNOWN</c>. This is a string-structure check, not a
/// SQL parser, but the renderer's grammar is small and fully controlled by
/// <see cref="TestFieldSqlRenderer"/>, so it is exact for every shape the renderer can produce.
/// </summary>
public class SqlPredicateRendererInvariantTests
{
    private static readonly IFieldSqlRenderer _fields = new TestFieldSqlRenderer();

#pragma warning disable CA1859
    private static readonly IPredicateRenderer _renderer = new SqlPredicateRenderer();
#pragma warning restore CA1859

    private static readonly (string Source, AlvoContext Context)[] _rules =
    [
        ("owner_id == @user.id", CelFixtures.Alice),
        ("status != 'draft'", CelFixtures.Alice),
        ("total > 100", CelFixtures.Alice),
        ("total >= 100", CelFixtures.Alice),
        ("total < 100", CelFixtures.Alice),
        ("total <= 100", CelFixtures.Alice),
        ("created_at == approved_at", CelFixtures.Alice),
        ("!(owner_id == @user.id)", CelFixtures.Alice),
        ("!!(owner_id == @user.id)", CelFixtures.Alice),
        ("has(owner_id)", CelFixtures.Alice),
        ("!has(owner_id)", CelFixtures.Alice),
        ("'editor' in @user.roles", CelFixtures.Editor),
        ("'editor' in @user.roles", CelFixtures.Alice),
        ("status in @user.roles", CelFixtures.Editor),
        ("tenant_id == @tenant.id", CelFixtures.Alice),
        ("tenant_id == @tenant.id", CelFixtures.TenantlessAlice),
        ("owner_id == @user.id && status == 'approved'", CelFixtures.Alice),
        ("owner_id == @user.id || 'editor' in @user.roles", CelFixtures.Editor),
        ("(owner_id == @user.id || status == 'approved') && !has(owner_id)", CelFixtures.Alice),
        ("true", CelFixtures.Alice),
        ("false", CelFixtures.Alice),
    ];

    [Fact]
    public void Every_rendered_predicate_is_structurally_two_valued()
    {
        foreach (var (source, context) in _rules)
        {
            var predicate = _renderer.Render(CelFixtures.CompileRule(source), context, _fields);

            IsTwoValued(predicate.Sql).ShouldBeTrue(
                $"'{source}' rendered '{predicate.Sql}', which is not a recognized two-valued shape.");
        }
    }

    private static bool IsTwoValued(string sql)
    {
        var trimmed = sql.Trim();

        if (trimmed is "TRUE" or "FALSE")
        {
            return true;
        }

        if (trimmed.StartsWith("COALESCE(", StringComparison.Ordinal) && trimmed.EndsWith(", FALSE)", StringComparison.Ordinal))
        {
            return true;
        }

        if (trimmed.StartsWith('(') && trimmed.EndsWith(" IS NOT NULL)", StringComparison.Ordinal))
        {
            return true;
        }

        if (trimmed.StartsWith("(NOT ", StringComparison.Ordinal) && trimmed.EndsWith(')'))
        {
            return IsTwoValued(trimmed["(NOT ".Length..^1]);
        }

        var and = SplitTopLevel(trimmed, " AND ");
        if (and is { } andSplit)
        {
            return IsTwoValued(andSplit.Left) && IsTwoValued(andSplit.Right);
        }

        var or = SplitTopLevel(trimmed, " OR ");
        if (or is { } orSplit)
        {
            return IsTwoValued(orSplit.Left) && IsTwoValued(orSplit.Right);
        }

        return false;
    }

    private static (string Left, string Right)? SplitTopLevel(string sql, string op)
    {
        if (!sql.StartsWith('(') || !sql.EndsWith(')'))
        {
            return null;
        }

        var inner = sql[1..^1];
        var depth = 0;
        for (var i = 0; i <= inner.Length - op.Length; i++)
        {
            depth += inner[i] switch { '(' => 1, ')' => -1, _ => 0 };
            if (depth == 0 && string.CompareOrdinal(inner, i, op, 0, op.Length) == 0)
            {
                return (inner[..i], inner[(i + op.Length)..]);
            }
        }

        return null;
    }
}
