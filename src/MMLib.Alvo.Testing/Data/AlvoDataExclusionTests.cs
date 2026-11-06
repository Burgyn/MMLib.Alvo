using MMLib.Alvo.Data;
using MMLib.Alvo.Descriptor;
using MMLib.Alvo.Migrations;
using MMLib.Alvo.Schema;
using Shouldly;
using System.Globalization;
using System.Text.RegularExpressions;
using Xunit;
using DescField = MMLib.Alvo.Descriptor.FieldType;
using SchemaField = MMLib.Alvo.Schema.FieldType;

namespace MMLib.Alvo.Testing.Data;

/// <summary>
/// An index's <c>exclude</c> is enforced by the engine itself (HX-12), asked of both shipped drivers: the migrator adds,
/// drops, replaces and keeps an exclusion through renames and table rebuilds, refuses one over rows that already overlap,
/// and the write path refuses an overlapping row as <see cref="AlvoConstraintKind.Exclusion"/> at the range's start, under
/// concurrent writers. Not in the adversarial suite: the in-memory reference implementation has no engine (HX-11 Ruling 28).
/// </summary>
public abstract partial class AlvoDataExclusionTests
{
    private const string Bookings = "bookings";
    private const string Venues = "venues";
    private const string StartsAt = "starts_at";
    private const string EndsAt = "ends_at";
    private const string NotCancelled = "status != 'cancelled'";
    private const string ExcludePointer = "/entities/bookings/indexes/0/exclude";
    private const string NotByPhone = "!(channel == 'phone')";

    private static readonly AlvoContext _acme = InTenant(TenantId.New());
    private static readonly AlvoContext _globex = InTenant(TenantId.New());
    private static readonly DateTimeOffset _nine = new(2026, 6, 1, 9, 0, 0, TimeSpan.Zero);

    /// <summary>Stands up an engine over <paramref name="schema"/>, its policy primed from <paramref name="descriptor"/>.</summary>
    /// <param name="schema">The schema every entity in <paramref name="descriptor"/> maps to.</param>
    /// <param name="descriptor">The project descriptor whose rules apply.</param>
    protected abstract Task<IAlvoData> CreateAsync(SchemaModel schema, AlvoDescriptor descriptor);

    /// <summary>The migrator of the engine <see cref="CreateAsync"/> stood up.</summary>
    protected abstract ISchemaMigrator Migrator { get; }

    /// <summary>Re-primes the policy of the engine <see cref="CreateAsync"/> stood up after an apply.</summary>
    /// <param name="schema">The schema now applied.</param>
    /// <param name="descriptor">The descriptor <paramref name="schema"/> maps from.</param>
    protected abstract void RePrime(SchemaModel schema, AlvoDescriptor descriptor);

    /// <summary>Executes <paramref name="sql"/> on a connection of its own and returns the engine's failure, or <see langword="null"/>.</summary>
    /// <param name="sql">The statement. Identifiers are double-quoted, which both engines accept.</param>
    protected abstract Task<Exception?> ExecuteOutOfBandAsync(string sql);

    /// <summary>The names of the exclusions the engine's catalog holds on <paramref name="table"/>, one per exclusion.</summary>
    /// <param name="table">The table whose exclusions are listed.</param>
    protected abstract Task<IReadOnlyList<string>> ExclusionNamesAsync(string table);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>An exclusion an apply adds is the engine's own — an out-of-band overlapping insert fails — and one it removes is gone.</summary>
    [Fact]
    public async Task Adding_an_exclusion_creates_it_and_removing_it_drops_it()
    {
        await CreateAsync(Schema(exclude: false), Descriptor());

        await ApplyAsync(Schema(exclude: false), Schema());
        (await InsertOutOfBandAsync(Hours(9, 11))).ShouldBeNull();
        (await InsertOutOfBandAsync(Hours(10, 12))).ShouldNotBeNull().Message.ShouldContain("xc_bookings_");

        await ApplyAsync(Schema(), Schema(exclude: false));
        (await InsertOutOfBandAsync(Hours(10, 12))).ShouldBeNull();
    }

    /// <summary>A changed <c>where</c> is a new name (D10): the plan drops the old exclusion and creates the new one.</summary>
    [Fact]
    public async Task Changing_an_exclusion_s_where_replaces_it()
    {
        await CreateAsync(Schema(where: NotCancelled), Descriptor());

        var plan = await PlanAsync(Schema(where: NotCancelled), Schema(where: "status != 'void'"));

        var dropped = NamesIn(plan.Sql.Where(IsDrop));
        var created = NamesIn(plan.Sql.Where(IsCreate));
        dropped.ShouldHaveSingleItem();
        created.ShouldHaveSingleItem();
        created.ShouldNotBe(dropped);
        plan.Steps.Select(step => step.Change.Kind).ShouldBe([SchemaChangeKind.DropIndex, SchemaChangeKind.AddIndex], ignoreOrder: true);
    }

    /// <summary>
    /// Review Focus 1: an apply that rebuilds the table — a validation added to another field, which SQLite answers with a
    /// create-copy-drop-rename — leaves the exclusion enforced.
    /// </summary>
    [Fact]
    public async Task An_exclusion_survives_a_later_apply_that_rebuilds_its_table()
    {
        await CreateAsync(Schema(), Descriptor());

        await ApplyAsync(Schema(), Schema(statusCheck: "status != 'void'"));

        (await InsertOutOfBandAsync(Hours(9, 11))).ShouldBeNull();
        (await InsertOutOfBandAsync(Hours(10, 12))).ShouldNotBeNull().Message.ShouldContain("xc_bookings_");
    }

    /// <summary>The plan — a dry run's too — counts the overlapping pairs a table already holds and refuses at the <c>exclude</c>.</summary>
    [Fact]
    public async Task Adding_an_exclusion_over_overlapping_rows_is_refused_with_their_count()
    {
        var data = await CreateAsync(Schema(exclude: false), Descriptor());
        await BookAsync(data, _acme, "r1", Hours(9, 10));
        await BookAsync(data, _acme, "r1", (_nine.AddMinutes(30), _nine.AddMinutes(90)));
        await BookAsync(data, _acme, "r1", Hours(10, 11));

        var refusal = await Should.ThrowAsync<DescriptorValidationException>(() => PlanAsync(Schema(exclude: false), Schema()));

        var error = refusal.Result.Errors.ShouldHaveSingleItem();
        error.Path.ShouldBe(ExcludePointer);
        error.Message.ShouldBe("2 pair(s) of 'bookings' rows overlap, so this exclusion cannot be added. Nothing was applied.");
        error.FixSuggestion.ShouldBe("Correct or remove the overlapping rows first — or narrow the exclusion with 'where' — then apply again.");
    }

    /// <summary>
    /// A plan made without the count — the runtime writer's — over overlapping rows is refused by the apply itself, at the
    /// <c>exclude</c>, and nothing is applied.
    /// </summary>
    [Fact]
    public async Task Applying_an_uncounted_plan_over_overlapping_rows_is_refused_at_the_exclude_pointer()
    {
        var data = await CreateAsync(Schema(exclude: false), Descriptor());
        await BookAsync(data, _acme, "r1", Hours(9, 11));
        await BookAsync(data, _acme, "r1", Hours(10, 12));
        var plan = await ((IUncountedSchemaPlanner)Migrator).PlanWithoutCountingAsync(
            Schema(exclude: false), Schema(), new MigrationOptions(), Ct);

        var refusal = await Should.ThrowAsync<DescriptorValidationException>(() => Migrator.ApplyAsync(plan, new MigrationOptions(), Ct));

        var error = refusal.Result.Errors.ShouldHaveSingleItem();
        error.Path.ShouldBe(ExcludePointer);
        error.Message.ShouldBe("Rows of 'bookings' overlap, so this exclusion cannot be added. Nothing was applied.");
        (await InsertOutOfBandAsync(Hours(10, 12))).ShouldBeNull();
    }

    /// <summary>
    /// One apply that adds a field and an exclusion whose <c>where</c> reads it applies (review I1): the plan cannot count
    /// over a column that does not exist yet, so it leaves the check to the apply rather than refusing as uncountable.
    /// </summary>
    [Fact]
    public async Task An_exclusion_whose_where_reads_a_field_the_same_apply_adds_applies()
    {
        var data = await CreateAsync(Schema(exclude: false), Descriptor());
        await BookAsync(data, _acme, "r1", Hours(9, 10));
        await BookAsync(data, _acme, "r1", Hours(10, 11));

        await ApplyAsync(Schema(exclude: false), Schema(where: NotByPhone, channel: true));

        (await InsertOutOfBandAsync(Hours(9, 11))).ShouldNotBeNull().Message.ShouldContain("xc_bookings_");
    }

    /// <summary>The same one-shot apply over rows that overlap is refused by the apply itself, at the <c>exclude</c>.</summary>
    [Fact]
    public async Task An_exclusion_whose_where_reads_a_field_the_same_apply_adds_is_refused_over_overlapping_rows()
    {
        var data = await CreateAsync(Schema(exclude: false), Descriptor());
        await BookAsync(data, _acme, "r1", Hours(9, 11));
        await BookAsync(data, _acme, "r1", Hours(10, 12));
        var plan = await PlanAsync(Schema(exclude: false), Schema(where: NotByPhone, channel: true));

        var refusal = await Should.ThrowAsync<DescriptorValidationException>(() => Migrator.ApplyAsync(plan, new MigrationOptions(), Ct));

        var error = refusal.Result.Errors.ShouldHaveSingleItem();
        error.Path.ShouldBe(ExcludePointer);
        error.Message.ShouldBe("Rows of 'bookings' overlap, so this exclusion cannot be added. Nothing was applied.");
    }

    /// <summary>
    /// A range changed between <c>date</c> and <c>datetime</c> keeps its name but not its enforcement, so the plan replaces
    /// the exclusion rather than leaving the old one over the column whose type changes (review M4).
    /// </summary>
    [Fact]
    public async Task Changing_a_range_s_type_replaces_the_exclusion()
    {
        await CreateAsync(Schema(), Descriptor());

        var plan = await PlanAsync(Schema(), Schema(rangeType: SchemaField.Date));

        var dropped = NamesIn(plan.Sql.Where(IsDrop));
        dropped.ShouldHaveSingleItem();
        NamesIn(plan.Sql.Where(IsCreate)).ShouldBe(dropped);
    }

    /// <summary>Renaming a range field re-creates the exclusion under its new name, and it holds.</summary>
    [Fact]
    public async Task Renaming_a_range_field_keeps_the_exclusion_enforced()
    {
        await CreateAsync(Schema(), Descriptor());
        var renamed = Schema(start: "begins_at", startRenamedFrom: StartsAt);

        await ApplyAsync(Schema(), renamed, Descriptor(start: "begins_at"));

        (await InsertOutOfBandAsync(Hours(9, 11), start: "begins_at")).ShouldBeNull();
        (await InsertOutOfBandAsync(Hours(10, 12), start: "begins_at")).ShouldNotBeNull().Message.ShouldContain("xc_bookings_");
        await ShouldHoldOnlyAsync(Bookings, NamesIn((await PlanAsync(new SchemaModel([]), renamed)).Sql.Where(IsCreate)));
    }

    /// <summary>Renaming the entity re-creates the exclusion under the name the new entity gives it, and it holds.</summary>
    [Fact]
    public async Task Renaming_the_entity_keeps_the_exclusion_enforced()
    {
        await CreateAsync(Schema(), Descriptor());

        await ApplyAsync(Schema(), Schema(name: Venues, renamedFrom: Bookings), Descriptor(Venues));

        (await InsertOutOfBandAsync(Hours(9, 11), table: Venues)).ShouldBeNull();
        (await InsertOutOfBandAsync(Hours(10, 12), table: Venues)).ShouldNotBeNull().Message.ShouldContain("xc_venues_");
        (await ExclusionNamesAsync(Venues)).ShouldHaveSingleItem().ShouldStartWith("xc_venues_");
    }

    private async Task ShouldHoldOnlyAsync(string table, IReadOnlyList<string> expected) =>
        (await ExclusionNamesAsync(table)).ShouldBe(expected, ignoreOrder: true);

    /// <summary>
    /// Every equality type the validator admits is one the engine can compare in an exclusion — on PostgreSQL, a type
    /// <c>btree_gist</c> supports (review M8).
    /// </summary>
    [Fact]
    public async Task Every_admitted_equality_type_can_be_excluded_over()
    {
        SchemaField[] types = [SchemaField.String, SchemaField.Integer, SchemaField.Decimal, SchemaField.Boolean, SchemaField.Uuid, SchemaField.Date, SchemaField.Enum];
        var schema = new SchemaModel([EveryEqualityType(types)]);

        await CreateAsync(schema, Descriptor());

        (await ExecuteOutOfBandAsync("SELECT COUNT(*) FROM \"bookings\"")).ShouldBeNull();
    }

    private Task<MigrationPlan> PlanAsync(SchemaModel current, SchemaModel desired) =>
        Migrator.PlanAsync(current, desired, new MigrationOptions(), Ct);

    private async Task ApplyAsync(SchemaModel current, SchemaModel desired, AlvoDescriptor? descriptor = null)
    {
        var plan = await PlanAsync(current, desired);
        (await Migrator.ApplyAsync(plan, new MigrationOptions(), Ct)).Applied.ShouldBeTrue();
        RePrime(desired, descriptor ?? Descriptor());
    }

    private Task<Exception?> InsertOutOfBandAsync(
        (DateTimeOffset From, DateTimeOffset To) range, string table = Bookings, string start = StartsAt, string room = "r1") =>
        ExecuteOutOfBandAsync(
            $"INSERT INTO \"{table}\" (\"id\", \"tenant_id\", \"room\", \"{start}\", \"{EndsAt}\", \"status\") VALUES "
            + $"('{Guid.NewGuid().ToString().ToUpperInvariant()}', '{_acme.Tenant!.Value.Value.ToString().ToUpperInvariant()}', "
            + $"'{room}', '{Stored(range.From)}', '{Stored(range.To)}', 'held')");

    private static string Stored(DateTimeOffset instant) =>
        instant.ToUniversalTime().ToString("yyyy'-'MM'-'dd HH':'mm':'ss'+00:00'", CultureInfo.InvariantCulture);

    private static (DateTimeOffset From, DateTimeOffset To) Hours(int from, int to) => (_nine.AddHours(from - 9), _nine.AddHours(to - 9));

    private static bool IsDrop(string statement) => statement.Contains("DROP ", StringComparison.Ordinal);

    private static bool IsCreate(string statement) =>
        statement.Contains("CREATE TRIGGER", StringComparison.Ordinal) || statement.Contains("ADD CONSTRAINT", StringComparison.Ordinal);

    private static List<string> NamesIn(IEnumerable<string> statements) =>
        [.. statements.SelectMany(statement => ExclusionName().Matches(statement).Select(match => match.Value)).Distinct()];

    [GeneratedRegex("xc_[a-z_]+_[0-9a-f]{8}", RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 1000)]
    private static partial Regex ExclusionName();

    private static Task<AlvoRecord> BookAsync(
        IAlvoData data, AlvoContext caller, string room, (DateTimeOffset From, DateTimeOffset To) range, string status = "held") =>
        data.CreateAsync(Bookings, Booking(caller, room, range, status), caller, cancellationToken: Ct);

    private static Dictionary<string, object?> Booking(
        AlvoContext caller, string room, (DateTimeOffset From, DateTimeOffset To) range, string status = "held") =>
        new(StringComparer.Ordinal)
        {
            ["tenant_id"] = caller.Tenant!.Value.Value,
            ["room"] = room,
            [StartsAt] = range.From,
            [EndsAt] = range.To,
            ["status"] = status,
        };

    private static AlvoContext InTenant(TenantId tenant) => new()
    {
        User = UserId.New(),
        Roles = new HashSet<Role> { Role.Authenticated },
        Tenant = tenant,
    };

    private static SchemaModel Schema(
        string name = Bookings, bool exclude = true, string? where = null, string start = StartsAt, string? startRenamedFrom = null,
        string? renamedFrom = null, string? statusCheck = null, bool channel = false, SchemaField rangeType = SchemaField.DateTime) =>
        new([new EntitySchema
        {
            Name = name,
            RenamedFrom = renamedFrom,
            Tenancy = TenancyMode.Scoped,
            Fields =
            [
                new FieldSchema { Name = "id", Type = SchemaField.Uuid, Required = true },
                new FieldSchema { Name = "room", Type = SchemaField.String, Required = true },
                new FieldSchema { Name = start, RenamedFrom = startRenamedFrom, Type = rangeType, Required = true },
                new FieldSchema { Name = EndsAt, Type = rangeType, Required = true, CheckExpression = $"{EndsAt} >= {start}" },
                new FieldSchema { Name = "status", Type = SchemaField.String, Required = true, CheckExpression = statusCheck },
                new FieldSchema { Name = "tenant_id", Type = SchemaField.Uuid, Required = true, Indexed = true },
                .. channel ? [new FieldSchema { Name = "channel", Type = SchemaField.String, Nullable = true }] : Array.Empty<FieldSchema>(),
            ],
            Indexes = exclude ? [new IndexSchema(["room"], Unique: false) { Exclude = new ExclusionSchema(start, EndsAt), Where = where }] : [],
        }]);

    private static EntitySchema EveryEqualityType(SchemaField[] types) => new()
    {
        Name = Bookings,
        Tenancy = TenancyMode.Scoped,
        Fields =
        [
            new FieldSchema { Name = "id", Type = SchemaField.Uuid, Required = true },
            .. types.Select(type => new FieldSchema
            {
                Name = $"by_{type.ToString().ToLowerInvariant()}",
                Type = type,
                Required = true,
                EnumValues = type == SchemaField.Enum ? ["a", "b"] : null,
            }),
            new FieldSchema { Name = StartsAt, Type = SchemaField.DateTime, Required = true },
            new FieldSchema { Name = EndsAt, Type = SchemaField.DateTime, Required = true },
            new FieldSchema { Name = "tenant_id", Type = SchemaField.Uuid, Required = true, Indexed = true },
        ],
        Indexes =
        [
            new IndexSchema([.. types.Select(type => $"by_{type.ToString().ToLowerInvariant()}")], Unique: false)
            {
                Exclude = new ExclusionSchema(StartsAt, EndsAt),
            },
        ],
    };

    private static AlvoDescriptor Descriptor(string entity = Bookings, string start = StartsAt) => new()
    {
        ApiVersion = "alvo.dev/v1",
        Name = "exclusions",
        Tenancy = new Tenancy { Enabled = true },
        Entities = new Dictionary<string, EntityDescriptor>(StringComparer.Ordinal)
        {
            [entity] = new()
            {
                Tenancy = EntityTenancy.Scoped,
                Fields = new Dictionary<string, FieldDescriptor>(StringComparer.Ordinal)
                {
                    ["room"] = new() { Type = DescField.String, Required = true },
                    [start] = new() { Type = DescField.DateTime, Required = true },
                    [EndsAt] = new() { Type = DescField.DateTime, Required = true },
                    ["status"] = new() { Type = DescField.String, Required = true },
                },
                Rules = new AccessRules { List = "true", Get = "true", Create = "true", Update = "true", Delete = "true" },
            },
        },
    };
}
