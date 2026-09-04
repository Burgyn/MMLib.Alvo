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
    /// An opted-in count is a <b>second</b> statement, and it carries the policy predicate in its own
    /// <c>WHERE</c>. The claim no returned number can carry: a count taken over the bare table returns a
    /// plausible integer and passes every outcome-level fact while telling a caller how many rows exist
    /// outside what they may read.
    /// </summary>
    /// <remarks>
    /// The two-statement shape is asserted alongside, because it is the reason the anchor and the window are
    /// dropped rather than reused: a count composed into the page's own statement would be a window function
    /// over a <c>WHERE</c> that already carries the cursor boundary, and would count the tail instead of the
    /// set on every page but the first.
    /// </remarks>
    [Fact]
    public async Task A_counted_read_composes_its_count_with_the_same_policy_predicate()
    {
        var world = await OwnedNotesAsync();
        world.Probe.ClearStatements();

        await world.Probe.Data.QueryAsync(
            new AlvoQuery { Entity = Entity, IncludeTotalCount = true }, world.Alice, Token);

        world.Probe.Statements.Count.ShouldBe(2);
        var count = world.Probe.Statements.Single(statement => statement.Contains("COUNT(*)", StringComparison.Ordinal));
        WhereClauseOf(count).ShouldContain(UsingPrefix);
        WhereClauseOf(count).ShouldContain(TenantPrefix);
    }

    /// <summary>
    /// The negative that makes the count opt-in observable in the statements rather than only in the answer:
    /// a read that did not ask sends <b>one</b> statement, and no <c>COUNT</c> at all.
    /// </summary>
    [Fact]
    public async Task A_read_that_did_not_ask_for_a_count_sends_no_count_statement()
    {
        var world = await OwnedNotesAsync();
        world.Probe.ClearStatements();

        await world.Probe.Data.QueryAsync(new AlvoQuery { Entity = Entity }, world.Alice, Token);

        world.Probe.Statements.ShouldHaveSingleItem().ShouldNotContain("COUNT(*)");
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

    /// <summary>
    /// The <c>UPDATE</c> the engine executes carries the predicate itself, so the pre-image read is not the only
    /// gate: swap the policy root for the bare table and every outcome-level fact still passes, because the
    /// pre-image has already refused the invisible row — while a concurrent writer that made the row visible
    /// between the two would then be written by an unconstrained statement.
    /// </summary>
    /// <remarks>
    /// Inherited rather than kept per engine, because <c>UPDATE … FROM (subquery)</c> is precisely the
    /// engine-divergent shape the de-risking spike flagged: PostgreSQL and SQLite spell the policy root's
    /// placement differently, and a driver that composed it over the bare table on one engine only would pass
    /// the other engine's copy of this fact. Asserted on the reserved prefix rather than on predicate text,
    /// which is engine-specific.
    /// </remarks>
    [Fact]
    public async Task The_update_statement_itself_carries_the_policy_predicate()
    {
        var world = await OwnedNotesAsync();
        world.Probe.ClearStatements();

        await world.Probe.Data.UpdateAsync(Entity, world.AliceRowId, Patch, world.Alice, cancellationToken: Token);

        StatementStartingWith("UPDATE", world.Probe).ShouldContain(UsingPrefix);
    }

    /// <summary>
    /// And the <c>DELETE</c> too, where the same shape appears as an <c>IN (subquery)</c>. A delete has no
    /// <c>WITH CHECK</c>, so this statement's own predicate is the last gate there is.
    /// </summary>
    [Fact]
    public async Task The_delete_statement_itself_carries_the_policy_predicate()
    {
        var world = await OwnedNotesAsync();
        world.Probe.ClearStatements();

        await world.Probe.Data.DeleteAsync(Entity, world.AliceRowId, world.Alice, cancellationToken: Token);

        StatementStartingWith("DELETE", world.Probe).ShouldContain(UsingPrefix);
    }

    /// <summary>
    /// The non-vacuity control for both: with a bare <c>"true"</c> rule the write statements bind no policy
    /// parameter at all, so the two facts above cannot be satisfied by an implementation that names
    /// <c>alvo_u</c> in every statement it sends.
    /// </summary>
    [Fact]
    public async Task A_rule_with_no_operands_binds_no_policy_parameter_on_the_write_path()
    {
        var world = await PublicNotesAsync();
        world.Probe.ClearStatements();

        await world.Probe.Data.UpdateAsync(Entity, world.AliceRowId, Patch, world.Alice, cancellationToken: Token);

        StatementStartingWith("UPDATE", world.Probe).ShouldNotContain(UsingPrefix);
    }

    /// <summary>The one statement of <paramref name="kind"/> this act emitted.</summary>
    /// <remarks>
    /// A write emits more than one statement — the locked pre-image, then the policy-carrying write — and both
    /// carry the predicate, so a fact naming only "some statement" would be satisfied by the read alone. The
    /// keyword picks the write out; <c>ShouldHaveSingleItem</c> makes a second one of the same kind a failure
    /// rather than a silently ignored candidate.
    /// </remarks>
    private static string StatementStartingWith(string kind, IStatementProbe probe) =>
        probe.Statements
            .Where(statement => statement.StartsWith(kind, StringComparison.OrdinalIgnoreCase))
            .ShouldHaveSingleItem();

    /// <summary>The smallest legal patch over the fixture's one writable field.</summary>
    private static IReadOnlyDictionary<string, object?> Patch =>
        new Dictionary<string, object?>(StringComparer.Ordinal) { ["title"] = "renamed" };

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

    /// <summary>
    /// Everything between the statement's <c>SELECT</c> and its first <c>FROM</c> — the projection alone.
    /// </summary>
    /// <remarks>
    /// Narrower than <see cref="WhereClauseOf"/> is crude, and for the opposite reason: a projection fact
    /// asserts that a column is <em>not</em> fetched, and the whole statement text names every column
    /// somewhere (the <c>NULL</c> projection's own alias, the <c>ORDER BY</c>, the <c>WHERE</c>). Asserting
    /// over the whole text would therefore prove nothing at all.
    /// </remarks>
    private static string SelectListOf(string statement)
    {
        var select = statement.IndexOf("SELECT", StringComparison.OrdinalIgnoreCase);
        select.ShouldBeGreaterThanOrEqualTo(0, $"The statement has no SELECT at all:{Environment.NewLine}{statement}");

        var from = statement.IndexOf(" FROM ", select, StringComparison.OrdinalIgnoreCase);
        from.ShouldBeGreaterThan(select, $"The statement has no FROM clause:{Environment.NewLine}{statement}");

        return statement[(select + "SELECT".Length)..from];
    }

    /// <summary>
    /// The <c>SELECT</c> list as its individual items, trimmed. A projection fact needs this rather than the
    /// list as text: an excluded column's name still <em>appears</em> in the list, as the alias of the
    /// <c>NULL</c> that replaced it, so a substring assertion could never tell "fetched" from "projected".
    /// One item per column, and the item's own shape is the answer.
    /// </summary>
    private static IReadOnlyList<string> SelectItemsOf(string statement) =>
        [.. SelectListOf(statement).Split(',').Select(item => item.Trim())];

    /// <summary>
    /// The push-down, asserted as the only thing a statement can carry: the unselected column is not read.
    /// </summary>
    /// <remarks>
    /// <b>What is deliberately not claimed.</b> <c>NULL AS col</c> stops the engine reading the column; it
    /// does not make the query proportionally cheaper — the win is real for a wide or TOASTed column and
    /// near zero for a narrow int. A throughput claim is not something a statement can prove, so none is
    /// made here.
    /// </remarks>
    [Fact]
    public async Task An_unselected_column_is_not_read_by_the_statement()
    {
        var world = await OwnedNotesAsync();
        world.Probe.ClearStatements();

        await world.Probe.Data.QueryAsync(
            new AlvoQuery { Entity = Entity, Select = ["label"] }, world.Alice, Token);

        var items = SelectItemsOf(world.Probe.Statements.ShouldHaveSingleItem());

        items.ShouldNotContain("\"title\"", "an unselected column must not be fetched");
        items.ShouldContain(
            item => item.EndsWith("AS \"title\"", StringComparison.Ordinal),
            "it is projected as a typed NULL under its own name instead");
    }

    /// <summary>
    /// The row key survives every projection, and this is not merely the returned-key-set contract:
    /// <c>Paginated</c> mints the keyset cursor from the fetched row's <c>id</c>, so a NULLed key would not
    /// mis-sort a page — it would break paging outright.
    /// </summary>
    [Fact]
    public async Task The_row_key_is_read_whatever_the_projection_named()
    {
        var world = await OwnedNotesAsync();
        world.Probe.ClearStatements();

        await world.Probe.Data.QueryAsync(
            new AlvoQuery { Entity = Entity, Select = ["title"] }, world.Alice, Token);

        var items = SelectItemsOf(world.Probe.Statements.ShouldHaveSingleItem());

        items.ShouldContain("\"id\"", "the keyset cursor is minted from the fetched row's id");
    }

    /// <summary>
    /// A sort key is read even when the projection excluded it, and this fact is the statement-level twin of
    /// the behavioural one in <c>AlvoDataProjectionTests</c>. A bare identifier in <c>ORDER BY</c> resolves
    /// against the output column names on both shipped engines, so a NULLed sort key would order the page by
    /// the <c>NULL</c> while the keyset boundary in <c>WHERE</c> still described the real sequence.
    /// </summary>
    [Fact]
    public async Task An_unselected_sort_key_is_read_rather_than_projected_as_a_null()
    {
        var world = await OwnedNotesAsync();
        world.Probe.ClearStatements();

        await world.Probe.Data.QueryAsync(
            new AlvoQuery { Entity = Entity, Select = ["id"], Sort = [new AlvoSort("label")] },
            world.Alice,
            Token);

        var items = SelectItemsOf(world.Probe.Statements.ShouldHaveSingleItem());

        items.ShouldContain(
            "\"label\"", "the ORDER BY would otherwise resolve to the projected NULL of the same name");
    }

    /// <summary>
    /// This change alters cost, not conduct: a caller who sends no projection gets the statement they got
    /// before it, with no null projection anywhere in the <c>SELECT</c> list.
    /// </summary>
    [Fact]
    public async Task A_read_with_no_projection_projects_no_null_at_all()
    {
        var world = await PublicNotesAsync();
        world.Probe.ClearStatements();

        await world.Probe.Data.QueryAsync(new AlvoQuery { Entity = Entity }, world.Alice, Token);

        world.Probe.Statements.ShouldHaveSingleItem().ShouldNotContain(" AS ", Case.Sensitive);
    }

    private Task<StatementWorld> OwnedNotesAsync() => NotesAsync("owner_id == @user.id");

    private Task<StatementWorld> PublicNotesAsync() => NotesAsync("true");

    private async Task<StatementWorld> NotesAsync(string rule)
    {
        var tenant = TenantId.New();
        var alice = Caller(tenant);
        var bob = Caller(tenant);
        var aliceRow = Guid.NewGuid();
        var bobRow = Guid.NewGuid();

        var (descriptor, schema) = Fixture(rule);
        var seed = new Dictionary<string, IReadOnlyList<AlvoRecord>>(StringComparer.Ordinal)
        {
            [Entity] =
            [
                Row(aliceRow, alice.User.Value, tenant.Value),
                Row(bobRow, bob.User.Value, tenant.Value),
            ],
        };

        return new StatementWorld(await CreateAsync(schema, descriptor, seed), alice, aliceRow, bobRow);
    }

    private static AlvoRecord Row(Guid id, Guid owner, Guid tenant) =>
        new(new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["id"] = id,
            ["owner_id"] = owner,
            ["tenant_id"] = tenant,
            ["title"] = "seeded",
            ["label"] = "seeded-label",
        });

    private static AlvoContext Caller(TenantId tenant) => new()
    {
        User = UserId.New(),
        Roles = new HashSet<Role> { Role.Authenticated },
        Tenant = tenant,
    };

    /// <summary>
    /// A tenant-scoped entity, so both a <c>USING</c> predicate and a tenant scope are in play, carrying the
    /// same rule on every operation — the write facts need <c>update</c> and <c>delete</c> to resolve to the
    /// same predicate the read facts assert on, and <c>title</c> is the one field a patch may write.
    /// </summary>
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
                        ["title"] = new() { Type = DescField.String },

                        // A second, nullable field exists only so a projection fact can sort by a column it
                        // did not select — the shape that would have ordered a page by a projected NULL.
                        ["label"] = new() { Type = DescField.String },
                    },
                    Rules = new AccessRules { List = rule, Get = rule, Update = rule, Delete = rule },
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
                    new FieldSchema { Name = "title", Type = SchemaField.String },
                    new FieldSchema { Name = "label", Type = SchemaField.String, Nullable = true },
                ],
            },
        ]);

        return (descriptor, schema);
    }

    private sealed record StatementWorld(
        IStatementProbe Probe, AlvoContext Alice, Guid AliceRowId, Guid BobRowId);
}
