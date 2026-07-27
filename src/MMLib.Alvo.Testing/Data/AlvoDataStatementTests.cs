using MMLib.Alvo.Data;
using MMLib.Alvo.Descriptor;
using MMLib.Alvo.Schema;
using Shouldly;
using Xunit;
using DescField = MMLib.Alvo.Descriptor.FieldType;
using SchemaField = MMLib.Alvo.Schema.FieldType;

namespace MMLib.Alvo.Testing.Data;

/// <summary>
/// §2.4's *"application-side rules compile to SQL predicates — <b>never</b> an in-memory post-filter"*, as a
/// per-engine fact rather than an argument about shared code.
/// </summary>
/// <remarks>
/// <para>
/// This is the one criterion no outcome can carry. An implementation that fetched every candidate row and
/// filtered them with <see cref="MMLib.Alvo.Expressions.IPredicateEvaluator"/> returns exactly the same rows,
/// throws exactly the same exceptions, and pages exactly the same way — it passes the whole adversarial suite,
/// the differential matrix and the ordering suite. What it cannot do is put the resolved predicate in the
/// statement it sends. So the assertion is on the SQL, and it has to run on every engine: a criterion proved
/// once on one engine is a property of that engine's test project, not of the port.
/// </para>
/// <para>
/// The predicate's <em>text</em> is engine-specific (SQLite folds three-valued logic with
/// <c>COALESCE(…, 0)</c>, PostgreSQL with <c>…, FALSE</c>), so these facts assert on the one part that is
/// not: the reserved parameter prefixes a resolved predicate binds its values under. A post-filtering
/// implementation binds none of them, because it never renders the predicate at all.
/// </para>
/// </remarks>
public abstract class AlvoDataStatementTests
{
    /// <summary>The prefix a resolved <c>USING</c> predicate binds its values under.</summary>
    private const string UsingPrefix = "alvo_u";

    /// <summary>The prefix the synthesized tenant scope binds its values under.</summary>
    private const string TenantPrefix = "alvo_t";

    private const string Entity = "notes";

    /// <summary>
    /// Builds a probe over <paramref name="descriptor"/>/<paramref name="schema"/>, seeded with
    /// <paramref name="seed"/>'s rows out of band, that also records the SQL its engine executes.
    /// </summary>
    /// <param name="schema">The schema every entity in <paramref name="descriptor"/> maps to.</param>
    /// <param name="descriptor">The project descriptor whose rules and tenancy apply.</param>
    /// <param name="seed">The initial rows to insert, keyed by entity name.</param>
    protected abstract Task<IStatementProbe> CreateAsync(
        SchemaModel schema, AlvoDescriptor descriptor, IReadOnlyDictionary<string, IReadOnlyList<AlvoRecord>> seed);

    /// <summary>
    /// The row-scoping rule reaches the engine as a bound predicate inside the statement's own <c>WHERE</c>,
    /// and the read is <b>one</b> statement — so there is no second query, and no fetched-then-discarded row.
    /// </summary>
    [Fact]
    public async Task The_policy_predicate_is_bound_inside_the_where_clause_of_one_statement()
    {
        var world = await OwnedNotesAsync();
        world.Probe.ClearStatements();

        await world.Probe.Data.QueryAsync(new AlvoQuery { Entity = Entity }, world.Alice, Token);

        var statement = world.Probe.Statements.ShouldHaveSingleItem();
        statement.ShouldContain(UsingPrefix);
        WhereClauseOf(statement).ShouldContain(UsingPrefix);
    }

    /// <summary>
    /// The synthesized tenant scope is in the same <c>WHERE</c>. It is a separate fact because it is a separate
    /// predicate with its own reserved prefix, and an implementation could compose one and post-filter the
    /// other.
    /// </summary>
    [Fact]
    public async Task The_tenant_scope_is_bound_inside_the_same_where_clause()
    {
        var world = await OwnedNotesAsync();
        world.Probe.ClearStatements();

        await world.Probe.Data.QueryAsync(new AlvoQuery { Entity = Entity }, world.Alice, Token);

        WhereClauseOf(world.Probe.Statements.ShouldHaveSingleItem()).ShouldContain(TenantPrefix);
    }

    /// <summary>
    /// A single-row read carries the predicate too. Named separately because <c>get</c> has the id available and
    /// is the most tempting place to fetch by key and check afterwards — which is exactly the post-filter this
    /// criterion forbids, and which would be invisible in the returned <see langword="null"/>.
    /// </summary>
    [Fact]
    public async Task A_single_row_read_carries_the_predicate_rather_than_checking_after_the_fetch()
    {
        var world = await OwnedNotesAsync();
        world.Probe.ClearStatements();

        await world.Probe.Data.GetAsync(Entity, world.BobRowId, world.Alice, Token);

        var statement = world.Probe.Statements.ShouldHaveSingleItem();
        WhereClauseOf(statement).ShouldContain(UsingPrefix);
    }

    /// <summary>
    /// The non-vacuity control: with a bare <c>"true"</c> rule there is nothing to bind, so the prefix is
    /// <b>absent</b>. Without this, the facts above would also pass for an implementation that happened to put
    /// the string <c>alvo_u</c> in every statement it ever sent.
    /// </summary>
    [Fact]
    public async Task A_rule_with_no_operands_binds_no_policy_parameter()
    {
        var world = await PublicNotesAsync();
        world.Probe.ClearStatements();

        await world.Probe.Data.QueryAsync(new AlvoQuery { Entity = Entity }, world.Alice, Token);

        world.Probe.Statements.ShouldHaveSingleItem().ShouldNotContain(UsingPrefix);
    }

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    /// <summary>
    /// Everything after the statement's <b>first</b> <c>WHERE</c>. Deliberately crude: the point is that the
    /// predicate is part of the filtering clause rather than merely present somewhere in the text (a
    /// post-filtering implementation could still name a parameter in a projection), and taking the first
    /// occurrence means a predicate pushed into a subquery still counts — which is correct, because a
    /// subquery's <c>WHERE</c> filters in the engine just as well.
    /// </summary>
    private static string WhereClauseOf(string statement)
    {
        var where = statement.IndexOf("WHERE", StringComparison.OrdinalIgnoreCase);
        where.ShouldBeGreaterThanOrEqualTo(0, $"The statement has no WHERE clause at all:{Environment.NewLine}{statement}");

        return statement[where..];
    }

    private Task<StatementWorld> OwnedNotesAsync() => NotesAsync("owner_id == @user.id");

    private Task<StatementWorld> PublicNotesAsync() => NotesAsync("true");

    private async Task<StatementWorld> NotesAsync(string rule)
    {
        var tenant = TenantId.New();
        var alice = Caller(tenant);
        var bob = Caller(tenant);
        var bobRow = Guid.NewGuid();

        var (descriptor, schema) = Fixture(rule);
        var seed = new Dictionary<string, IReadOnlyList<AlvoRecord>>(StringComparer.Ordinal)
        {
            [Entity] =
            [
                Row(Guid.NewGuid(), alice.User.Value, tenant.Value),
                Row(bobRow, bob.User.Value, tenant.Value),
            ],
        };

        return new StatementWorld(await CreateAsync(schema, descriptor, seed), alice, bobRow);
    }

    private static AlvoRecord Row(Guid id, Guid owner, Guid tenant) =>
        new(new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["id"] = id,
            ["owner_id"] = owner,
            ["tenant_id"] = tenant,
        });

    private static AlvoContext Caller(TenantId tenant) => new()
    {
        User = UserId.New(),
        Roles = new HashSet<Role> { Role.Authenticated },
        Tenant = tenant,
    };

    /// <summary>A tenant-scoped entity, so both a <c>USING</c> predicate and a tenant scope are in play.</summary>
    private static (AlvoDescriptor Descriptor, SchemaModel Schema) Fixture(string rule)
    {
        var descriptor = new AlvoDescriptor
        {
            ApiVersion = "alvo.dev/v1",
            Name = "statement-fixture",
            Entities = new Dictionary<string, EntityDescriptor>(StringComparer.Ordinal)
            {
                [Entity] = new EntityDescriptor
                {
                    Tenancy = EntityTenancy.Scoped,
                    Fields = new Dictionary<string, FieldDescriptor>(StringComparer.Ordinal)
                    {
                        ["owner_id"] = new() { Type = DescField.Uuid, Required = true },
                    },
                    Rules = new AccessRules { List = rule, Get = rule },
                },
            },
        };

        var schema = new SchemaModel([
            new EntitySchema
            {
                Name = Entity,
                Tenancy = TenancyMode.Scoped,
                Fields =
                [
                    new FieldSchema { Name = "id", Type = SchemaField.Uuid, Required = true },
                    new FieldSchema { Name = "owner_id", Type = SchemaField.Uuid, Required = true },
                    new FieldSchema { Name = "tenant_id", Type = SchemaField.Uuid, Required = true, Indexed = true },
                ],
            },
        ]);

        return (descriptor, schema);
    }

    private sealed record StatementWorld(IStatementProbe Probe, AlvoContext Alice, Guid BobRowId);
}
