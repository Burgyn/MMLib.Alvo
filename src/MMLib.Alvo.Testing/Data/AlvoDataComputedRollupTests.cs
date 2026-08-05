using MMLib.Alvo.Data;
using MMLib.Alvo.Descriptor;
using MMLib.Alvo.Migrations;
using MMLib.Alvo.Schema;
using Shouldly;
using Xunit;
using DescField = MMLib.Alvo.Descriptor.FieldType;
using SchemaField = MMLib.Alvo.Schema.FieldType;

namespace MMLib.Alvo.Testing.Data;

/// <summary>
/// What <c>computed</c> and <c>rollup</c> do over a real engine (#21) — the ladder
/// <c>baas-analyza:1358</c> describes, asked of both shipped drivers so §0 principle 3's "identical
/// behaviour" is measured rather than assumed.
/// </summary>
/// <remarks>
/// <para>
/// <b>Its own suite, for <see cref="AlvoDataConstraintTests"/>' reason.</b> Every fact here needs a real
/// database: a generated column is maintained by the engine, and a rollup is maintained by a statement the
/// engine runs. The in-memory reference implementation inherits neither, because it has no engine to refuse a
/// write and no transaction to recompute inside — a fact placed in the adversarial suite would be vacuous for
/// it or would demand it grow a second implementation of the feature.
/// </para>
/// <para>
/// <b>The two engines disagree in opposite directions here, which is exactly why one suite asks both.</b> For
/// <c>computed</c>, SQLite accepts <c>ALTER TABLE … ADD COLUMN … STORED</c> on an empty table and refuses it on
/// a populated one, while PostgreSQL accepts it and backfills. For <c>rollup</c>, PostgreSQL needs the parent's
/// row lock or loses updates, while SQLite must not read the parent before writing at all. Both differences are
/// absorbed below <see cref="MMLib.Alvo.Data.IAlvoData"/>, and every fact here reads identically for either engine.
/// </para>
/// <para>
/// <b>What is deliberately <em>not</em> here: the lost-update race.</b> SQLite admits one writer at a time, so a
/// lost update is structurally impossible on it and a shared race fact would be green there whatever the
/// implementation does. That fact lives on PostgreSQL alone
/// (<c>PostgreSqlRollupRaceTests</c>), with its window widened, because without both properties it asserts
/// nothing — measured: 40 of 40 writers looked correct until a delay widened the window, and then 31 of 40.
/// </para>
/// <para>
/// <b>Decimal precision on SQLite is the engine's, not this suite's.</b> SQLite has no decimal storage class, so
/// a computed decimal column holds a <c>real</c> (measured: <c>typeof()</c> answers <c>real</c> for the untyped
/// generated column EF emits there) and a decimal rollup is summed as a double. Every value below is therefore
/// chosen to be exact in binary floating point — halves and quarters — so a failure here is a defect rather than
/// the last bit of a mantissa.
/// </para>
/// </remarks>
public abstract class AlvoDataComputedRollupTests
{
    /// <summary>
    /// Builds a fresh <see cref="IAlvoData"/> over <paramref name="descriptor"/>/<paramref name="schema"/> — the
    /// same seam <see cref="AlvoDataConstraintTests.CreateAsync"/> is. Every fact writes its own rows through the
    /// port, because what is measured is what a <em>write</em> does.
    /// </summary>
    /// <param name="schema">The schema every entity in <paramref name="descriptor"/> maps to.</param>
    /// <param name="descriptor">The project descriptor whose rules apply.</param>
    protected abstract Task<IAlvoData> CreateAsync(SchemaModel schema, AlvoDescriptor descriptor);

    /// <summary>
    /// Executes one statement against the store the most recent <see cref="CreateAsync"/> stood up,
    /// <b>outside</b> the data port, and returns the engine's own failure or <see langword="null"/> when it
    /// accepted the statement.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>This exists for exactly one fact, and that fact is the load-bearing one.</b> "The value is a stored
    /// generated column" is a claim about the <em>engine</em>, and the only way to ask an engine is to hand it a
    /// statement it must refuse. Every other route is vacuous: a value read back is equally consistent with an
    /// ordinary column somebody happened to fill in correctly, and a refusal from the port proves only that the
    /// port refused — which is precisely the state this build shipped <em>before</em> #21, when <c>computed</c>
    /// was dropped on the floor.
    /// </para>
    /// <para>
    /// It returns the exception instead of throwing so the fact can assert on the engine's own message. An
    /// implementation opens its own connection: this is not a port operation and must not go through one.
    /// </para>
    /// </remarks>
    /// <param name="sql">The statement to execute. Identifiers are double-quoted, which both engines accept.</param>
    protected abstract Task<Exception?> ExecuteOutOfBandAsync(string sql);

    /// <summary>
    /// Plans and applies the change from <paramref name="current"/> to <paramref name="desired"/> against the store the
    /// most recent <see cref="CreateAsync"/> stood up, and re-primes the port against
    /// <paramref name="desired"/> — a real second migration of a database that already holds rows.
    /// </summary>
    /// <remarks>
    /// It returns the <see cref="MigrationResult"/> rather than <c>void</c> so a fact can assert on the plan the
    /// migrator produced as well as on the database afterwards: whether the change was classified destructive is
    /// a property of the plan, and on the engine that rebuilds its table it is not obvious.
    /// </remarks>
    /// <param name="current">The schema the store currently holds.</param>
    /// <param name="desired">The schema to migrate it to.</param>
    protected abstract Task<MigrationResult> MigrateAsync(SchemaModel current, SchemaModel desired);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>
    /// The whole point of <c>computed</c>: the value is maintained <b>by the database</b>, so the engine itself
    /// refuses a write to the column — no hook, no custom endpoint, no bug and no second application can set it.
    /// </summary>
    /// <remarks>
    /// Asserted by the engine's refusal of an out-of-band <c>UPDATE</c>, never by reading the value back. Reading
    /// it back would be satisfied by an ordinary column that happened to hold the right number, which is the
    /// difference between this feature and the state before it.
    /// </remarks>
    [Fact]
    public async Task The_engine_itself_refuses_a_write_to_a_computed_column()
    {
        var data = await CreateAsync(Schema, Descriptor);
        await CreateItemAsync(data, await CreateInvoiceAsync(data), unitPrice: 2.5m, amount: 4);

        var refusal = await ExecuteOutOfBandAsync($"UPDATE \"{Items}\" SET \"{LineTotal}\" = 999");

        refusal.ShouldNotBeNull("a stored generated column is read-only to every writer, including this one");
        refusal.Message.ShouldNotBeNullOrWhiteSpace();
    }

    /// <summary>
    /// The counterweight: an ordinary column on the same table still takes an out-of-band write. Without it, a
    /// broken <see cref="ExecuteOutOfBandAsync"/> — a typo'd table name, a closed connection — would satisfy the
    /// refusal above and prove nothing at all.
    /// </summary>
    [Fact]
    public async Task The_engine_accepts_the_same_write_to_an_ordinary_column()
    {
        var data = await CreateAsync(Schema, Descriptor);
        await CreateItemAsync(data, await CreateInvoiceAsync(data), unitPrice: 2.5m, amount: 4);

        var refusal = await ExecuteOutOfBandAsync($"UPDATE \"{Items}\" SET \"amount\" = 9");

        refusal.ShouldBeNull("only the generated column is read-only; the probe itself has to work");
    }

    /// <summary>A computed field is the expression over the row the write stored.</summary>
    [Fact]
    public async Task A_computed_field_is_the_expression_over_the_row_the_write_stored()
    {
        var data = await CreateAsync(Schema, Descriptor);

        var stored = await CreateItemAsync(data, await CreateInvoiceAsync(data), unitPrice: 2.5m, amount: 4);

        stored[LineTotal].ShouldBe(10m);
    }

    /// <summary>
    /// A caller who names a computed field in a payload is <b>refused</b>, not silently ignored.
    /// </summary>
    /// <remarks>
    /// The runtime model marks the property store-generated, so EF leaves the column out of the <c>INSERT</c>
    /// altogether — without a guard the caller would get a <c>201</c> whose body reports the engine's value and
    /// nothing anywhere saying their number was discarded. A silently dropped payload is the same
    /// wrong-stored-number failure class the feature exists to remove, arriving from the caller's side.
    /// </remarks>
    [Fact]
    public async Task A_payload_naming_a_computed_field_is_refused_rather_than_ignored()
    {
        var data = await CreateAsync(Schema, Descriptor);
        var invoice = await CreateInvoiceAsync(data);

        var refusal = await Should.ThrowAsync<AlvoAuthorizationException>(() => data.CreateAsync(
            Items,
            new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                [Invoice] = invoice,
                ["unit_price"] = 2.5m,
                ["amount"] = 4,
                [LineTotal] = 999m,
            },
            Caller,
            cancellationToken: Ct));

        refusal.Message.ShouldContain(LineTotal);
        refusal.Message.ShouldContain("computed");
    }

    /// <summary>An update to a source field moves the computed value with it — the engine tracks it.</summary>
    [Fact]
    public async Task An_update_to_a_source_field_moves_the_computed_value_with_it()
    {
        var data = await CreateAsync(Schema, Descriptor);
        var stored = await CreateItemAsync(data, await CreateInvoiceAsync(data), unitPrice: 2.5m, amount: 4);

        var updated = await data.UpdateAsync(
            Items,
            (Guid)stored["id"]!,
            new Dictionary<string, object?>(StringComparer.Ordinal) { ["amount"] = 6 },
            Caller,
            cancellationToken: Ct);

        updated[LineTotal].ShouldBe(15m, "the engine recomputed from the row it now holds");
    }

    /// <summary>
    /// Adding a computed field to an entity that <b>already holds a row</b> succeeds, the existing row gets the
    /// value, and the column is a generated one afterwards.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The row is written BEFORE the migration, and that is the single most load-bearing line in this
    /// suite.</b> On an <em>empty</em> table SQLite accepts <c>ALTER TABLE … ADD COLUMN … STORED</c>; on a table
    /// holding one row it refuses with <c>cannot add a STORED column</c>. The same fact over a fresh fixture is
    /// therefore green on both engines while the only case that matters — a deployed entity that already has
    /// data — is broken on one of them. Moving the write below the migration silently removes the whole fact.
    /// </para>
    /// <para>
    /// It asserts the value <em>and</em> the engine's refusal, because a rebuild that copied the column as an
    /// ordinary one would satisfy the value alone.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task A_computed_field_can_be_added_to_an_entity_that_already_holds_a_row()
    {
        var data = await CreateAsync(PlainSchema, PlainDescriptor);
        var invoice = await CreateInvoiceAsync(data, vatTotal: 5m);
        await CreateItemAsync(data, invoice, unitPrice: 3m, amount: 2);      // FIRST. Not a detail.

        var migration = await MigrateAsync(PlainSchema, Schema);

        migration.Applied.ShouldBeTrue("adding a computed field is not a destructive change on either engine");
        var item = (await data.QueryAsync(new AlvoQuery { Entity = Items }, Caller, Ct)).Items.ShouldHaveSingleItem();
        item[LineTotal].ShouldBe(6m, "the engine computed the value for the row that was already there");
        (await ExecuteOutOfBandAsync($"UPDATE \"{Items}\" SET \"{LineTotal}\" = 999")).ShouldNotBeNull(
            "and it is a GENERATED column afterwards, not an ordinary one the rebuild happened to fill in");
    }

    /// <summary>
    /// The counterweight to the fact above, and the answer to "does the two-hop trip the destructive gate":
    /// adding a computed field plans <b>no destructive step</b>, so it applies without
    /// <see cref="MigrationOptions.AllowDestructive"/>.
    /// </summary>
    /// <remarks>
    /// It is not obvious on the engine that rebuilds: the emitted SQL contains <c>DROP TABLE</c>, and only the
    /// <em>operation</em> EF was handed — an <c>AlterColumnOperation</c> that narrows nothing — decides the
    /// classification. If <c>DestructiveScan</c> ever started reading the SQL instead of the operation, every
    /// computed field would become a change a host has to opt into, and this is the fact that would say so.
    /// </remarks>
    [Fact]
    public async Task Adding_a_computed_field_is_not_classified_destructive()
    {
        var data = await CreateAsync(PlainSchema, PlainDescriptor);
        await CreateItemAsync(data, await CreateInvoiceAsync(data), unitPrice: 3m, amount: 2);

        var migration = await MigrateAsync(PlainSchema, Schema);

        migration.Plan.HasDestructiveChanges.ShouldBeFalse();
        migration.Plan.Steps.ShouldNotBeEmpty("a plan with no steps would pass this vacuously");
    }

    private const string Invoices = "invoices";

    private const string Items = "invoice_items";

    private const string Invoice = "invoice";

    private const string LineTotal = "line_total";

    private const string NetTotal = "net_total";

    private const string VatTotal = "vat_total";

    private const string GrossTotal = "gross_total";

    private const string ItemCount = "item_count";

    private const string LargestLine = "largest_line";

    private const string SmallestLine = "smallest_line";

    private const string AverageLine = "average_line";

    private static readonly AlvoContext _caller = new()
    {
        User = UserId.New(),
        Roles = new HashSet<Role> { Role.Authenticated },
    };

    /// <summary>One caller for the whole suite: none of these facts is about authorization.</summary>
    private static AlvoContext Caller => _caller;

    /// <summary>
    /// Creates one invoice and returns its id. <c>vat_total</c> is written <b>explicitly</b>, which is the
    /// ladder's missing before-hook rung stated rather than hidden — the ladder fact's own remarks say why.
    /// </summary>
    private static async Task<Guid> CreateInvoiceAsync(IAlvoData data, decimal vatTotal = 0m) =>
        (Guid)(await data.CreateAsync(
            Invoices,
            new Dictionary<string, object?>(StringComparer.Ordinal) { [VatTotal] = vatTotal },
            Caller,
            cancellationToken: Ct))["id"]!;

    private static Task<AlvoRecord> CreateItemAsync(IAlvoData data, Guid invoice, decimal unitPrice, int amount) =>
        data.CreateAsync(
            Items,
            new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                [Invoice] = invoice,
                ["unit_price"] = unitPrice,
                ["amount"] = amount,
            },
            Caller,
            cancellationToken: Ct);

    /// <summary>
    /// <c>baas-analyza:1358</c>'s invoice, as one descriptor: <c>invoice_items.line_total</c> is
    /// <em>computed</em>, <c>invoices.net_total</c> is a <em>rollup</em> over it, and
    /// <c>invoices.gross_total</c> is <em>computed again</em> over a column the framework maintains. The four
    /// remaining rollups exist so <c>min</c>/<c>max</c>/<c>avg</c>/<c>count</c> cannot be satisfied by a
    /// sum-shaped implementation.
    /// </summary>
    private static AlvoDescriptor Descriptor => DescriptorFor(computed: true);

    /// <summary>
    /// The descriptor with or without its two <c>computed</c> declarations, so the migration fact starts from a
    /// schema whose columns are ordinary and adds the generation — the production shape, where a computed field
    /// is declared on an entity that has been serving for a while.
    /// </summary>
    /// <param name="computed">Whether the two computed fields declare their expression.</param>
    private static AlvoDescriptor DescriptorFor(bool computed) => new()
    {
        ApiVersion = "alvo.dev/v1",
        Name = "computed-rollup-suite",
        Entities = new Dictionary<string, EntityDescriptor>(StringComparer.Ordinal)
        {
            [Invoices] = new()
            {
                Tenancy = EntityTenancy.Global,
                Fields = new Dictionary<string, FieldDescriptor>(StringComparer.Ordinal)
                {
                    [VatTotal] = new() { Type = DescField.Decimal },
                    [NetTotal] = new() { Type = DescField.Decimal, Rollup = Sum(LineTotal) },
                    [GrossTotal] = new() { Type = DescField.Decimal, Computed = computed ? "net_total + vat_total" : null },
                    [ItemCount] = new() { Type = DescField.Integer, Rollup = new() { From = Items, Op = RollupOp.Count } },
                    [LargestLine] = new() { Type = DescField.Decimal, Rollup = Aggregate(RollupOp.Max, LineTotal) },
                    [SmallestLine] = new() { Type = DescField.Decimal, Rollup = Aggregate(RollupOp.Min, LineTotal) },
                    [AverageLine] = new() { Type = DescField.Decimal, Rollup = Aggregate(RollupOp.Avg, LineTotal) },
                },
                Rules = AllowAll,
            },
            [Items] = new()
            {
                Tenancy = EntityTenancy.Global,
                Fields = new Dictionary<string, FieldDescriptor>(StringComparer.Ordinal)
                {
                    [Invoice] = new() { Type = DescField.Ref, Entity = Invoices, Required = true },
                    ["unit_price"] = new() { Type = DescField.Decimal, Required = true },
                    ["amount"] = new() { Type = DescField.Integer, Required = true },
                    [LineTotal] = new() { Type = DescField.Decimal, Computed = computed ? "unit_price * amount" : null },
                },
                Rules = AllowAll,
            },
        },
    };

    private static Rollup Sum(string field) => Aggregate(RollupOp.Sum, field);

    private static Rollup Aggregate(RollupOp op, string field) => new() { From = Items, Op = op, Field = field };

    private static AccessRules AllowAll =>
        new() { List = "true", Get = "true", Create = "true", Update = "true", Delete = "true" };

    /// <summary>
    /// The applied schema the descriptor above maps to, paired by hand for the reason
    /// <see cref="AlvoDataConstraintTests"/> gives: the core's mapper is <see langword="internal"/> and
    /// unreachable from this project. Nothing here restates a <em>rule</em> — only each field's shape and the
    /// two mechanisms the drivers read off the applied schema.
    /// </summary>
    private static SchemaModel Schema => new([Invoice_(computed: true), Items_(computed: true)]);

    /// <summary>The same schema with both computed fields as ordinary columns — where the migration fact starts.</summary>
    private static SchemaModel PlainSchema => new([Invoice_(computed: false), Items_(computed: false)]);

    /// <summary>The descriptor that matches <see cref="PlainSchema"/>.</summary>
    private static AlvoDescriptor PlainDescriptor => DescriptorFor(computed: false);

    private static EntitySchema Invoice_(bool computed) => new()
    {
        Name = Invoices,
        Tenancy = TenancyMode.Global,
        Fields =
        [
            new FieldSchema { Name = "id", Type = SchemaField.Uuid, Required = true },
            Money(VatTotal),
            Money(NetTotal) with { Rollup = RollupOf(RollupOperation.Sum, LineTotal) },
            Money(GrossTotal) with { ComputedExpression = computed ? "net_total + vat_total" : null },
            new FieldSchema { Name = ItemCount, Type = SchemaField.Integer, Nullable = true, Rollup = RollupOf(RollupOperation.Count, field: null) },
            Money(LargestLine) with { Rollup = RollupOf(RollupOperation.Max, LineTotal) },
            Money(SmallestLine) with { Rollup = RollupOf(RollupOperation.Min, LineTotal) },
            Money(AverageLine) with { Rollup = RollupOf(RollupOperation.Avg, LineTotal) },
        ],
    };

    private static EntitySchema Items_(bool computed) => new()
    {
        Name = Items,
        Tenancy = TenancyMode.Global,
        Fields =
        [
            new FieldSchema { Name = "id", Type = SchemaField.Uuid, Required = true },
            new FieldSchema
            {
                Name = Invoice,
                Type = SchemaField.Ref,
                Required = true,
                Reference = new RefSchema(Invoices, MMLib.Alvo.Schema.OnDelete.Cascade),
            },
            Money("unit_price") with { Required = true, Nullable = false },
            new FieldSchema { Name = "amount", Type = SchemaField.Integer, Required = true },
            Money(LineTotal) with { ComputedExpression = computed ? "unit_price * amount" : null },
        ],
    };

    /// <summary>
    /// A nullable <c>numeric(18,2)</c>, which is what every amount on this ladder is. Nullable deliberately: a
    /// rollup over <em>zero</em> children is the engine's own empty answer, and for four of the five operations
    /// that answer is <c>NULL</c> rather than a zero this layer invented.
    /// </summary>
    private static FieldSchema Money(string name) => new()
    {
        Name = name,
        Type = SchemaField.Decimal,
        Precision = 18,
        Scale = 2,
        Nullable = true,
    };

    private static RollupSchema RollupOf(RollupOperation op, string? field) =>
        new() { From = Items, Op = op, Field = field, Via = Invoice };
}
