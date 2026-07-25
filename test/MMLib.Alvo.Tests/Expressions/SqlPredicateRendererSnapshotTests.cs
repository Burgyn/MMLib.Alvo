using MMLib.Alvo.Expressions;
using MMLib.Alvo.Expressions.Internal;

namespace MMLib.Alvo.Tests.Expressions;

/// <summary>
/// A golden snapshot of the SQL <see cref="SqlPredicateRenderer"/> produces for a representative
/// table of rules — every comparison operator, nesting, negation, <c>has(...)</c>, both forms of
/// role membership, tenant scope, and a mixed <c>&amp;&amp;</c>/<c>||</c> tree. This is the artifact
/// a later differential test and PR2's per-engine snapshots compare against; if it moves,
/// <c>alvo-snapshot-judge</c> decides whether the move is justified.
/// </summary>
public class SqlPredicateRendererSnapshotTests
{
    private static readonly IFieldSqlRenderer _fields = new TestFieldSqlRenderer();

#pragma warning disable CA1859
    private static readonly IPredicateRenderer _renderer = new SqlPredicateRenderer();
#pragma warning restore CA1859

    [Fact]
    public Task Cel_to_sql_core()
    {
        var rows = new[]
        {
            ("owner_id == @user.id", "Alice", CelFixtures.Alice),
            ("status != 'draft'", "Alice", CelFixtures.Alice),
            ("total > 100", "Alice", CelFixtures.Alice),
            ("total >= 100", "Alice", CelFixtures.Alice),
            ("total < 100", "Alice", CelFixtures.Alice),
            ("total <= 100", "Alice", CelFixtures.Alice),
            ("created_at == approved_at", "Alice", CelFixtures.Alice),
            ("!(owner_id == @user.id)", "Alice", CelFixtures.Alice),
            ("has(owner_id)", "Alice", CelFixtures.Alice),
            ("'editor' in @user.roles", "Editor", CelFixtures.Editor),
            ("'editor' in @user.roles", "Alice", CelFixtures.Alice),
            ("status in @user.roles", "Editor", CelFixtures.Editor),
            ("tenant_id == @tenant.id", "Alice", CelFixtures.Alice),
            ("tenant_id == @tenant.id", "TenantlessAlice", CelFixtures.TenantlessAlice),
            ("owner_id == @user.id && status == 'approved'", "Alice", CelFixtures.Alice),
            ("owner_id == @user.id || 'editor' in @user.roles", "Editor", CelFixtures.Editor),
        };

        var rendered = rows.Select(row =>
        {
            var (source, contextName, context) = row;
            var predicate = _renderer.Render(CelFixtures.CompileRule(source), context, _fields);
            return new
            {
                source,
                context = contextName,
                predicate.Sql,
                Parameters = predicate.Parameters
                    .OrderBy(pair => pair.Key, StringComparer.Ordinal)
                    .Select(pair => new { pair.Key, Value = Scrub(pair.Value) }),
            };
        }).ToList();

        return Verify(rendered).UseFileName("cel-to-sql-core");
    }

    // Alice/Editor/AcmeTenant carry a fresh random Guid every test run (CelFixtures generates them
    // once per process via UserId.New()/TenantId.New()), so the raw values are not reproducible
    // across runs and cannot be committed into a Verify baseline. Substituting a stable token keeps
    // the snapshot deterministic while still proving each parameter binds a real Guid, not text.
    private static object? Scrub(object? value) => value switch
    {
        Guid guid when guid == CelFixtures.Alice.User.Value => "{alice-user-id}",
        Guid guid when guid == CelFixtures.Editor.User.Value => "{editor-user-id}",
        Guid guid when guid == CelFixtures.AcmeTenant.Value => "{acme-tenant-id}",
        _ => value,
    };
}
