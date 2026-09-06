using MMLib.Alvo.Data;
using MMLib.Alvo.Descriptor;
using MMLib.Alvo.Schema;
using Shouldly;
using DescField = MMLib.Alvo.Descriptor.FieldType;
using SchemaField = MMLib.Alvo.Schema.FieldType;

namespace MMLib.Alvo.Testing.Data;

/// <summary>
/// The projection as a rule of the <b>port</b>, proved over every <see cref="IAlvoData"/> implementation
/// this suite runs against — the in-memory reference included. Three claims, in rising order of how
/// easily they break: a projection narrows the returned key set; it never narrows it below what
/// <see cref="IAlvoData"/>'s returned-key-set contract promises; and it never changes which rows come
/// back, or in what order, or how they page.
/// </summary>
/// <remarks>
/// <para>
/// The third claim is the reason this is a suite of its own rather than a section of
/// <c>AlvoDataPagingTests</c>. The shipped drivers implement the projection by rendering an unselected
/// column as a typed SQL <c>NULL</c> <em>aliased to the column's own name</em>, and a bare identifier in
/// <c>ORDER BY</c> resolves against the output column names first — measured on SQLite and PostgreSQL
/// alike. So a projection that NULLed a sort key would order the page by the <c>NULL</c> while the keyset
/// boundary in <c>WHERE</c> still described the real sequence: a page that skips or repeats a row, not a
/// mis-sort. Every fact below that pairs a projected read against an unprojected one exists to catch
/// that class of defect, and the paged one is the fact that fails loudly where a single-page order
/// assertion might not.
/// </para>
/// <para>
/// A projection is a caller <em>preference</em> and the field mask is a security <em>control</em>. The
/// two are separate inputs everywhere they travel and are unioned only at render time, so the refusals
/// below are asserted in pairs: what the mask answers, and what a merely-unselected field answers.
/// </para>
/// </remarks>
public abstract class AlvoDataProjectionTests
{
    private const string Entity = "notes";

    /// <inheritdoc cref="AlvoDataPagingTests.CreateAsync"/>
    protected abstract Task<IAlvoData> CreateAsync(
        SchemaModel schema, AlvoDescriptor descriptor, IReadOnlyDictionary<string, IReadOnlyList<AlvoRecord>> seed);

    /// <summary>
    /// Both refusals are the port's own <c>QueryFieldUnavailable</c> message, so a caller cannot tell
    /// "this entity has a field called X, hidden from you" from "this entity has no such field". A
    /// projection is the third way to ask that question, after a filter and a sort key, and it earns the
    /// same non-answer.
    /// </summary>
    [Fact]
    public async Task A_projection_naming_a_hidden_field_is_refused_exactly_as_an_undeclared_one_is()
    {
        var world = await SeededWorldAsync(rowCount: 3);

        var hidden = await Should.ThrowAsync<AlvoAuthorizationException>(() => world.Data.QueryAsync(
            new AlvoQuery { Entity = Entity, Select = ["secret"] }, world.Alice, Token));
        var undeclared = await Should.ThrowAsync<AlvoAuthorizationException>(() => world.Data.QueryAsync(
            new AlvoQuery { Entity = Entity, Select = ["nosuchfield"] }, world.Alice, Token));

        hidden.Message.ShouldBe(
            undeclared.Message, "the refusal must not tell a caller which of the two cases happened");
    }

    /// <summary>
    /// An empty projection could return no field at all. Refused by the port's own guard rather than read
    /// as "every field", on the same ground the <c>after</c>/<c>offset</c> pair is refused.
    /// </summary>
    [Fact]
    public async Task A_projection_naming_no_field_is_refused()
    {
        var world = await SeededWorldAsync(rowCount: 1);

        await Should.ThrowAsync<ArgumentException>(() => world.Data.QueryAsync(
            new AlvoQuery { Entity = Entity, Select = [] }, world.Alice, Token));
    }

    /// <summary>
    /// The plain claim: an unselected field's key is <b>absent</b>, not present and null. That distinction
    /// is the whole observable contract — a driver that returned the key with a null value would have
    /// pushed the projection into the statement and then given the saving back on the wire.
    /// </summary>
    [Fact]
    public async Task A_projection_returns_the_selected_keys_and_no_other_descriptor_declared_field()
    {
        var world = await SeededWorldAsync(rowCount: 3);

        var page = await world.Data.QueryAsync(
            new AlvoQuery { Entity = Entity, Select = ["title"] }, world.Alice, Token);

        page.Items.Count.ShouldBe(3);
        page.Items[0].Values.ShouldContainKey("title");
        page.Items[0].Values.ShouldNotContainKey("body", "an unselected field is absent, not null");
        page.Items[0].Values.ShouldNotContainKey("label");
    }

    /// <summary>
    /// <see cref="IAlvoData"/>'s returned-key-set contract survives the projection: a record carries every
    /// framework-managed column whatever the projection named. Not merely tidy — <c>id</c> is what a keyset
    /// cursor is minted from, so a NULLed row key would not mis-sort a page, it would break paging.
    /// </summary>
    [Fact]
    public async Task A_framework_managed_column_survives_a_projection_that_did_not_name_it()
    {
        var world = await SeededWorldAsync(rowCount: 2);

        var page = await world.Data.QueryAsync(
            new AlvoQuery { Entity = Entity, Select = ["title"] }, world.Alice, Token);

        page.Items[0].Values.ShouldContainKey(AlvoManagedColumns.Id);
    }

    /// <summary>
    /// The identity case: naming every field must be indistinguishable from naming none. It is the fact
    /// that would catch a survivor set that dropped something it should have kept.
    /// </summary>
    [Fact]
    public async Task A_projection_selecting_every_declared_field_returns_the_same_keys_as_no_projection()
    {
        var world = await SeededWorldAsync(rowCount: 2);
        AlvoSort[] sort = [new AlvoSort("title")];

        var unprojected = await world.Data.QueryAsync(
            new AlvoQuery { Entity = Entity, Sort = sort }, world.Alice, Token);
        var projected = await world.Data.QueryAsync(
            new AlvoQuery { Entity = Entity, Sort = sort, Select = ["id", "title", "body", "label"] },
            world.Alice,
            Token);

        Keys(projected).ShouldBe(Keys(unprojected));
    }

    /// <summary>
    /// The defect this suite exists for, in its cheapest form: a sort over a field the projection did not
    /// name must order exactly as the same sort without a projection. The fixture's <c>label</c> order is
    /// deliberately the reverse of its insertion order, so a sort that silently resolved to a projected
    /// <c>NULL</c> would return the scan order and be caught here.
    /// </summary>
    [Fact]
    public async Task A_sort_over_an_unselected_field_orders_exactly_as_the_same_sort_without_a_projection()
    {
        var world = await SeededWorldAsync(rowCount: 5);
        AlvoSort[] sort = [new AlvoSort("label", Descending: true)];

        var unprojected = await world.Data.QueryAsync(
            new AlvoQuery { Entity = Entity, Sort = sort }, world.Alice, Token);
        var projected = await world.Data.QueryAsync(
            new AlvoQuery { Entity = Entity, Sort = sort, Select = ["id"] }, world.Alice, Token);

        Ids(projected).ShouldBe(Ids(unprojected), "a projection must not change the order of a page");
    }

    /// <summary>
    /// The same defect in the form that cannot be missed. Under an alias-shadowed <c>ORDER BY</c> the
    /// page's order and the keyset boundary describe two different sequences, so walking the pages skips
    /// or repeats rows — which this asserts on directly rather than through a single page's order.
    /// </summary>
    [Fact]
    public async Task A_projected_paged_read_over_an_unselected_sort_key_returns_each_row_exactly_once()
    {
        var world = await SeededWorldAsync(rowCount: 7);
        AlvoSort[] sort = [new AlvoSort("label", Descending: true)];

        var walked = new List<object?>();
        string? cursor = null;
        do
        {
            var page = await world.Data.QueryAsync(
                new AlvoQuery { Entity = Entity, Sort = sort, Select = ["id"], Limit = 2, After = cursor },
                world.Alice,
                Token);
            walked.AddRange(page.Items.Select(row => row["id"]));
            cursor = page.NextCursor;
        }
        while (cursor is not null);

        var expected = await world.Data.QueryAsync(
            new AlvoQuery { Entity = Entity, Sort = sort }, world.Alice, Token);
        walked.ShouldBe(Ids(expected), "every row exactly once, in the unprojected order");
    }

    /// <summary>
    /// A filter term is composed into <c>WHERE</c>, where both shipped engines resolve the table column and
    /// ignore an output alias of the same name — measured, and this is what keeps it measured.
    /// </summary>
    [Fact]
    public async Task A_filter_over_an_unselected_field_matches_the_same_rows_as_without_the_projection()
    {
        var world = await SeededWorldAsync(rowCount: 5);
        var filter = new AlvoComparison("label", AlvoFilterOperator.Eq, "label-0003");

        var unprojected = await world.Data.QueryAsync(
            new AlvoQuery { Entity = Entity, Filter = filter }, world.Alice, Token);
        var projected = await world.Data.QueryAsync(
            new AlvoQuery { Entity = Entity, Filter = filter, Select = ["id"] }, world.Alice, Token);

        unprojected.Items.Count.ShouldBe(1, "the fixture seeds one row per label");
        Ids(projected).ShouldBe(Ids(unprojected));
    }

    /// <summary>
    /// The case that would have been a bypass rather than a mis-sort. This world's <c>list</c> rule is
    /// <c>!has(label)</c>, so it admits exactly the rows whose <c>label</c> is null. Had <c>WHERE</c>
    /// resolved the projected alias the way <c>ORDER BY</c> does, the predicate would have rendered
    /// <c>NOT("label" IS NOT NULL)</c> over a constant <c>NULL</c> and admitted <b>every</b> row — and a
    /// compiled predicate's field references are not enumerable from a <c>CompiledExpression</c>, so no
    /// survivor set could have excluded them.
    /// </summary>
    [Fact]
    public async Task A_using_predicate_over_an_unselected_field_admits_exactly_the_rows_it_admits_unprojected()
    {
        var world = await NullLabelScopedWorldAsync();

        var unprojected = await world.Data.QueryAsync(
            new AlvoQuery { Entity = Entity, Sort = [new AlvoSort("title")] }, world.Alice, Token);
        var projected = await world.Data.QueryAsync(
            new AlvoQuery { Entity = Entity, Sort = [new AlvoSort("title")], Select = ["id"] },
            world.Alice,
            Token);

        unprojected.Items.Count.ShouldBe(2, "the fixture seeds two rows with no label out of four");
        Ids(projected).ShouldBe(Ids(unprojected), "a projection must not widen what a policy admits");
    }

    /// <summary>
    /// Every unselected field is now rendered through the dialect's typed-<c>NULL</c> projection. Before
    /// this, only a <c>hidden</c> field was, and a mask over one column of every field type was never
    /// exercised — so a store type the cast could not express would first have surfaced on a caller's read.
    /// </summary>
    /// <remarks>
    /// Covers the eight scalar types plus <c>text</c> and <c>json</c>. <c>enum</c> and <c>ref</c> are
    /// excluded deliberately: each needs its own descriptor configuration (a value list, a target entity),
    /// which is a different fixture rather than another column in this one.
    /// </remarks>
    [Fact]
    public async Task A_projection_over_an_entity_declaring_every_field_type_casts_every_null_it_projects()
    {
        var world = await EveryFieldTypeWorldAsync();

        var page = await world.Data.QueryAsync(
            new AlvoQuery { Entity = Entity, Select = ["title"] }, world.Alice, Token);

        page.Items.ShouldHaveSingleItem();
        page.Items[0].Values.ShouldContainKey("title");
        foreach (var unselected in new[] { "a_text", "an_integer", "a_decimal", "a_boolean", "a_date", "a_datetime", "a_uuid", "a_json" })
        {
            page.Items[0].Values.ShouldNotContainKey(unselected);
        }
    }

    /// <summary>
    /// Two callers in one tenant, different rows: a projected read returns exactly the rows the caller's own
    /// <c>USING</c> predicate admits, and the same rows the unprojected read admits.
    /// </summary>
    /// <remarks>
    /// The security core's checklist asks for this as a test rather than an argument. The argument is that
    /// the predicate is a <c>WHERE</c> term and a projection only rewrites the <c>SELECT</c> list — but the
    /// projection aliases a <c>NULL</c> to the predicate's own column name, so "the predicate still filters"
    /// is exactly the claim that has to be measured rather than reasoned about.
    /// </remarks>
    [Fact]
    public async Task A_projected_read_admits_one_callers_rows_and_not_the_other_callers()
    {
        var world = await ScopedWorldAsync();

        var mine = await world.Data.QueryAsync(
            new AlvoQuery { Entity = Entity, Select = ["title"] }, world.Alice, Token);
        var unprojected = await world.Data.QueryAsync(
            new AlvoQuery { Entity = Entity }, world.Alice, Token);

        mine.Items.Count.ShouldBe(2, "Alice owns two of the four rows in her tenant");
        Ids(mine).Order().ShouldBe(Ids(unprojected).Order());
        mine.Items.ShouldAllBe(row => (string)row["title"]! == "alice-row");
    }

    /// <summary>
    /// Two tenants, otherwise identical: a projected read never crosses the tenant boundary. The
    /// synthesized tenant scope is a <c>WHERE</c> term over a column the projection is free to exclude, so
    /// this is the fact that would fail if an excluded column's <c>NULL</c> ever reached that clause.
    /// </summary>
    [Fact]
    public async Task A_projected_read_never_crosses_the_tenant_boundary()
    {
        var world = await ScopedWorldAsync();

        var ours = await world.Data.QueryAsync(
            new AlvoQuery { Entity = Entity, Select = ["title"] }, world.Alice, Token);
        var theirs = await world.Data.QueryAsync(
            new AlvoQuery { Entity = Entity, Select = ["title"] }, world.OtherTenantAlice!, Token);

        ours.Items.Count.ShouldBe(2);
        theirs.Items.Count.ShouldBe(2, "the other tenant's rows are seeded identically");
        Ids(ours).Intersect(Ids(theirs)).ShouldBeEmpty("no row is visible to both tenants");
    }

    private static IReadOnlyList<object?> Ids(AlvoPage page) => [.. page.Items.Select(row => row["id"])];

    private static IReadOnlyList<string> Keys(AlvoPage page) =>
        [.. page.Items[0].Values.Keys.OrderBy(key => key, StringComparer.Ordinal)];

    /// <summary>
    /// One seeded <c>notes</c> database: a title to select, a body to leave unselected, a nullable label to
    /// sort and filter by, and one field a <c>hidden</c> rule masks.
    /// </summary>
    private async Task<SeededWorld> SeededWorldAsync(int rowCount)
    {
        // Descending by label is ascending by insertion order, so a sort over label that silently resolved
        // to a projected NULL — and therefore returned the scan order — is distinguishable from one that
        // did not.
        var seed = Enumerable.Range(0, rowCount)
            .Select(index => Row(
                title: $"row-{index:D4}",
                label: $"label-{rowCount - index:D4}"))
            .ToList();

        return await WorldAsync(Descriptor(listRule: "true"), Schema(), seed);
    }

    /// <summary>
    /// The same entity under a <c>list</c> rule whose truth depends on a field a projection can exclude:
    /// <c>!has(label)</c>. Two of the four rows carry no label.
    /// </summary>
    private async Task<SeededWorld> NullLabelScopedWorldAsync()
    {
        List<AlvoRecord> seed =
        [
            Row(title: "row-0001", label: null),
            Row(title: "row-0002", label: "label-0002"),
            Row(title: "row-0003", label: null),
            Row(title: "row-0004", label: "label-0004"),
        ];

        return await WorldAsync(Descriptor(listRule: "!has(label)"), Schema(), seed);
    }

    /// <summary>One row of an entity declaring one column of (nearly) every field type — see the fact's remarks.</summary>
    private async Task<SeededWorld> EveryFieldTypeWorldAsync()
    {
        var descriptor = new AlvoDescriptor
        {
            ApiVersion = "alvo.dev/v1",
            Name = "projection-types-fixture",
            Entities = new Dictionary<string, EntityDescriptor>(StringComparer.Ordinal)
            {
                [Entity] = new EntityDescriptor
                {
                    Tenancy = EntityTenancy.Global,
                    Fields = new Dictionary<string, FieldDescriptor>(StringComparer.Ordinal)
                    {
                        ["title"] = new() { Type = DescField.String, Required = true },
                        ["a_text"] = new() { Type = DescField.Text },
                        ["an_integer"] = new() { Type = DescField.Integer },
                        ["a_decimal"] = new() { Type = DescField.Decimal, Precision = 18, Scale = 2 },
                        ["a_boolean"] = new() { Type = DescField.Boolean },
                        ["a_date"] = new() { Type = DescField.Date },
                        ["a_datetime"] = new() { Type = DescField.DateTime },
                        ["a_uuid"] = new() { Type = DescField.Uuid },
                        ["a_json"] = new() { Type = DescField.Json },
                    },
                    Rules = new AccessRules { List = "true", Get = "true" },
                },
            },
        };

        var schema = new SchemaModel([
            new EntitySchema
            {
                Name = Entity,
                Tenancy = TenancyMode.Global,
                Fields =
                [
                    new FieldSchema { Name = "id", Type = SchemaField.Uuid, Required = true },
                    new FieldSchema { Name = "title", Type = SchemaField.String, Required = true, MaxLength = 32 },
                    new FieldSchema { Name = "a_text", Type = SchemaField.Text, Nullable = true },
                    new FieldSchema { Name = "an_integer", Type = SchemaField.Integer, Nullable = true },
                    new FieldSchema { Name = "a_decimal", Type = SchemaField.Decimal, Nullable = true, Precision = 18, Scale = 2 },
                    new FieldSchema { Name = "a_boolean", Type = SchemaField.Boolean, Nullable = true },
                    new FieldSchema { Name = "a_date", Type = SchemaField.Date, Nullable = true },
                    new FieldSchema { Name = "a_datetime", Type = SchemaField.DateTime, Nullable = true },
                    new FieldSchema { Name = "a_uuid", Type = SchemaField.Uuid, Nullable = true },
                    new FieldSchema { Name = "a_json", Type = SchemaField.Json, Nullable = true },
                ],
            },
        ]);

        // Every projectable column is seeded null on purpose: the typed-NULL cast this fact exercises is
        // rendered from the store type, not from the stored value, so a value would test nothing extra and
        // would import each engine's own conversion rules into the fixture.
        var seed = new List<AlvoRecord>
        {
            new(new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["id"] = Guid.NewGuid(),
                ["title"] = "the only row",
            }),
        };

        return await WorldAsync(descriptor, schema, seed);
    }

    /// <summary>
    /// A tenant-scoped <c>notes</c> under <c>owner_id == @user.id</c>: two tenants, two owners each, two
    /// rows per owner. The projection excludes both <c>tenant_id</c> and <c>owner_id</c> from the response,
    /// which is what makes the two facts above adversarial rather than decorative.
    /// </summary>
    private async Task<SeededWorld> ScopedWorldAsync()
    {
        var tenant = Guid.NewGuid();
        var otherTenant = Guid.NewGuid();
        var alice = UserId.New();
        var bob = UserId.New();

        var descriptor = new AlvoDescriptor
        {
            ApiVersion = "alvo.dev/v1",
            Name = "projection-scoped-fixture",
            Entities = new Dictionary<string, EntityDescriptor>(StringComparer.Ordinal)
            {
                [Entity] = new EntityDescriptor
                {
                    Tenancy = EntityTenancy.Scoped,
                    Fields = new Dictionary<string, FieldDescriptor>(StringComparer.Ordinal)
                    {
                        ["owner_id"] = new() { Type = DescField.Uuid, Required = true },
                        ["title"] = new() { Type = DescField.String, Required = true },
                        ["body"] = new() { Type = DescField.String },
                    },
                    Rules = new AccessRules { List = "owner_id == @user.id", Get = "owner_id == @user.id" },
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
                    new FieldSchema { Name = "tenant_id", Type = SchemaField.Uuid, Required = true, Indexed = true },
                    new FieldSchema { Name = "owner_id", Type = SchemaField.Uuid, Required = true },
                    new FieldSchema { Name = "title", Type = SchemaField.String, Required = true, MaxLength = 32 },
                    new FieldSchema { Name = "body", Type = SchemaField.String, Nullable = true },
                ],
            },
        ]);

        List<AlvoRecord> seed =
        [
            .. ScopedRows(tenant, alice.Value, "alice-row"),
            .. ScopedRows(tenant, bob.Value, "bob-row"),
            .. ScopedRows(otherTenant, alice.Value, "alice-row"),
            .. ScopedRows(otherTenant, bob.Value, "bob-row"),
        ];

        var data = await CreateAsync(
            schema,
            descriptor,
            new Dictionary<string, IReadOnlyList<AlvoRecord>>(StringComparer.Ordinal) { [Entity] = seed });

        return new SeededWorld(data, ScopedCaller(alice, tenant), ScopedCaller(alice, otherTenant));
    }

    private static IEnumerable<AlvoRecord> ScopedRows(Guid tenant, Guid owner, string title) =>
        Enumerable.Range(0, 2).Select(_ => new AlvoRecord(new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["id"] = Guid.NewGuid(),
            ["tenant_id"] = tenant,
            ["owner_id"] = owner,
            ["title"] = title,
            ["body"] = new string('x', 128),
        }));

    private static AlvoContext ScopedCaller(UserId user, Guid tenant) => new()
    {
        User = user,
        Roles = new HashSet<Role> { Role.Authenticated },
        Tenant = new TenantId(tenant),
    };

    private static AlvoRecord Row(string title, string? label) =>
        new(new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["id"] = Guid.NewGuid(),
            ["title"] = title,
            ["body"] = new string('x', 128),
            ["label"] = label,
            ["secret"] = "classified",
        });

    private static AlvoDescriptor Descriptor(string listRule) => new()
    {
        ApiVersion = "alvo.dev/v1",
        Name = "projection-fixture",
        Entities = new Dictionary<string, EntityDescriptor>(StringComparer.Ordinal)
        {
            [Entity] = new EntityDescriptor
            {
                Tenancy = EntityTenancy.Global,
                Fields = new Dictionary<string, FieldDescriptor>(StringComparer.Ordinal)
                {
                    ["title"] = new() { Type = DescField.String, Required = true },
                    ["body"] = new() { Type = DescField.String },
                    ["label"] = new() { Type = DescField.String },
                    ["secret"] = new() { Type = DescField.String, Hidden = BoolOrCel.FromBoolean(true) },
                },
                Rules = new AccessRules { List = listRule, Get = "true" },
            },
        },
    };

    private static SchemaModel Schema() => new([
        new EntitySchema
        {
            Name = Entity,
            Tenancy = TenancyMode.Global,
            Fields =
            [
                new FieldSchema { Name = "id", Type = SchemaField.Uuid, Required = true },
                new FieldSchema { Name = "title", Type = SchemaField.String, Required = true, MaxLength = 32 },
                new FieldSchema { Name = "body", Type = SchemaField.String, Nullable = true },
                new FieldSchema { Name = "label", Type = SchemaField.String, Nullable = true },
                new FieldSchema { Name = "secret", Type = SchemaField.String, Nullable = true },
            ],
        },
    ]);

    private async Task<SeededWorld> WorldAsync(
        AlvoDescriptor descriptor, SchemaModel schema, IReadOnlyList<AlvoRecord> seed)
    {
        var data = await CreateAsync(
            schema,
            descriptor,
            new Dictionary<string, IReadOnlyList<AlvoRecord>>(StringComparer.Ordinal) { [Entity] = seed });

        return new SeededWorld(data, Caller);
    }

    /// <summary>One seeded store, plus the callers the facts above query as.</summary>
    /// <param name="Data">The seeded store.</param>
    /// <param name="Alice">The caller every fact queries as.</param>
    /// <param name="OtherTenantAlice">
    /// The same user identity in a different tenant, for the cross-tenant fact. The same <em>user</em> on
    /// purpose: it makes the tenant scope the only thing separating the two reads, so a fact that passes
    /// cannot be passing because the row-level predicate happened to do the work.
    /// </param>
    private sealed record SeededWorld(IAlvoData Data, AlvoContext Alice, AlvoContext? OtherTenantAlice = null);

    private static AlvoContext Caller => new()
    {
        User = UserId.New(),
        Roles = new HashSet<Role> { Role.Authenticated },
        Tenant = null,
    };

    private static CancellationToken Token => TestContext.Current.CancellationToken;
}
