using MMLib.Alvo.Expressions;
using MMLib.Alvo.Expressions.Internal;

namespace MMLib.Alvo.Tests.Expressions;

/// <summary>
/// A golden snapshot of the SQL <see cref="SqlPredicateRenderer"/> produces for a representative
/// table of rules — every comparison operator, nesting, negation, <c>has(...)</c>/<c>!has(...)</c>,
/// both forms of role membership, tenant scope, a boolean field, a nested mixed
/// <c>&amp;&amp;</c>/<c>||</c> tree, and the Computed (scalar) entry point. This is the artifact a
/// later differential test and PR2's per-engine snapshots compare against; if it moves,
/// <c>alvo-snapshot-judge</c> decides whether the move is justified.
/// </summary>
public class SqlPredicateRendererSnapshotTests
{
    private static readonly IFieldSqlRenderer _fields = new TestFieldSqlRenderer();
    private static readonly SqlPredicateRenderer _renderer = new();

    private static readonly (string Source, string Context, AlvoContext AlvoContext)[] _predicateRows =
    [
        ("owner_id == @user.id", "Alice", CelFixtures.Alice),
        ("status != 'draft'", "Alice", CelFixtures.Alice),
        ("total > 100", "Alice", CelFixtures.Alice),
        ("total >= 100", "Alice", CelFixtures.Alice),
        ("total < 100", "Alice", CelFixtures.Alice),
        ("total <= 100", "Alice", CelFixtures.Alice),
        ("created_at == approved_at", "Alice", CelFixtures.Alice),
        ("!(owner_id == @user.id)", "Alice", CelFixtures.Alice),
        ("has(owner_id)", "Alice", CelFixtures.Alice),
        ("!has(owner_id)", "Alice", CelFixtures.Alice),
        ("is_public", "Alice", CelFixtures.Alice),
        ("!is_public", "Alice", CelFixtures.Alice),
        ("'editor' in @user.roles", "Editor", CelFixtures.Editor),
        ("'editor' in @user.roles", "Alice", CelFixtures.Alice),
        ("status in @user.roles", "Editor", CelFixtures.Editor),
        ("tenant_id == @tenant.id", "Alice", CelFixtures.Alice),
        ("tenant_id == @tenant.id", "TenantlessAlice", CelFixtures.TenantlessAlice),
        ("owner_id == @user.id && status == 'approved'", "Alice", CelFixtures.Alice),
        ("owner_id == @user.id || 'editor' in @user.roles", "Editor", CelFixtures.Editor),
        ("(owner_id == @user.id || status == 'approved') && !has(owner_id)", "Alice", CelFixtures.Alice),
    ];

    private static readonly string[] _computedRows =
    [
        "total + 1",
        "total * 2 + 1",
        "(total + 1) * (total - 1)",
        "total > 5 ? 1 : 2",
        "!(total > 5) ? 1 : 2",
        "(total > 5 && total < 10) ? 1 : 2",
    ];

    [Fact]
    public Task Cel_to_sql_core()
    {
        var predicateRows = _predicateRows.Select(row =>
        {
            var predicate = _renderer.Render(CelFixtures.CompileRule(row.Source), row.AlvoContext, _fields);
            return new
            {
                source = row.Source,
                context = row.Context,
                predicate.Sql,
                Parameters = predicate.Parameters
                    .OrderBy(pair => pair.Key, StringComparer.Ordinal)
                    .Select(pair => new { pair.Key, Value = Scrub(pair.Value) }),
            };
        });

        var computedRows = _computedRows.Select(source =>
        {
            var scalar = _renderer.Render(CelFixtures.CompileComputed(source), _fields);
            return new
            {
                source,
                context = "(scalar)",
                scalar.Sql,
                Parameters = scalar.Parameters
                    .OrderBy(pair => pair.Key, StringComparer.Ordinal)
                    .Select(pair => new { pair.Key, Value = Scrub(pair.Value) }),
            };
        });

        return Verify(predicateRows.Concat(computedRows).ToList()).UseFileName("cel-to-sql-core");
    }

    /// <summary>
    /// Alice/Editor/AcmeTenant carry a fresh random <see cref="Guid"/> every test run (
    /// <see cref="CelFixtures"/> generates them once per process via <c>UserId.New()</c>/
    /// <c>TenantId.New()</c>), so the raw values are not reproducible across runs and cannot be
    /// committed into a Verify baseline. Substituting a stable token keeps the snapshot
    /// deterministic while still proving each parameter binds a real <see cref="Guid"/>, not text.
    /// </summary>
    private static object? Scrub(object? value) => value switch
    {
        Guid guid when guid == CelFixtures.Alice.User.Value => "{alice-user-id}",
        Guid guid when guid == CelFixtures.Editor.User.Value => "{editor-user-id}",
        Guid guid when guid == CelFixtures.AcmeTenant.Value => "{acme-tenant-id}",
        _ => value,
    };
}
