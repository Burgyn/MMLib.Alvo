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
/// An index's <c>where</c> is a partial index the engine itself keeps (HX-12), asked of both shipped drivers: a partial
/// unique index allows duplicates outside its predicate and stays per tenant, a changed or renamed predicate is a new index
/// under a new name, and a predicate the engine refuses is a refusal at its pointer. Not in the adversarial suite: the
/// in-memory reference implementation has no engine (HX-11 Ruling 28).
/// </summary>
public abstract class AlvoDataPartialIndexTests
{
    private const string Members = "members";
    private const string People = "people";
    private const string ActiveWhere = "active";
    private const string TimestampConstantWhere = "joined_at > timestamp('2026-01-01T00:00:00Z')";

    private static readonly AlvoContext _acme = InTenant(TenantId.New());
    private static readonly AlvoContext _globex = InTenant(TenantId.New());
    private static readonly DateTimeOffset _in2026 = new(2026, 6, 1, 0, 0, 0, TimeSpan.Zero);

    /// <summary>Stands up an engine over <paramref name="schema"/>, its policy primed from <paramref name="descriptor"/>.</summary>
    /// <param name="schema">The schema every entity in <paramref name="descriptor"/> maps to.</param>
    /// <param name="descriptor">The project descriptor whose rules apply.</param>
    protected abstract Task<IAlvoData> CreateAsync(SchemaModel schema, AlvoDescriptor descriptor);

    /// <summary>Plans a migration through this engine's <see cref="ISchemaMigrator"/>.</summary>
    /// <param name="current">The schema the store currently holds.</param>
    /// <param name="desired">The schema to migrate it to.</param>
    protected abstract Task<MigrationPlan> PlanAsync(SchemaModel current, SchemaModel desired);

    /// <summary>
    /// Plans and applies a migration through this engine's migrator, then re-primes the policy from
    /// <paramref name="descriptor"/>.
    /// </summary>
    /// <param name="current">The schema the store currently holds.</param>
    /// <param name="desired">The schema to migrate it to.</param>
    /// <param name="descriptor">The descriptor <paramref name="desired"/> maps from.</param>
    protected abstract Task ApplyAsync(SchemaModel current, SchemaModel desired, AlvoDescriptor descriptor);

    /// <summary>
    /// Whether this engine refuses an index predicate whose functions are not immutable — PostgreSQL's <c>42P17</c> over the
    /// epoch extraction a duration renders through. SQLite's date functions are deterministic without <c>'now'</c>, so the
    /// same predicate is one it keeps.
    /// </summary>
    protected abstract bool RefusesANonImmutablePredicate { get; }

    /// <summary>The names of every ordinary index the engine's catalog holds on <paramref name="table"/>.</summary>
    /// <param name="table">The table whose indexes are listed.</param>
    protected abstract Task<IReadOnlyList<string>> IndexNamesAsync(string table);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>Acceptance (4): a partial unique index allows duplicates outside its <c>where</c> and refuses one inside it.</summary>
    [Fact]
    public async Task A_partial_unique_index_allows_duplicates_outside_its_where()
    {
        var data = await CreateAsync(Schema(Members, ActiveWhere), Descriptor(Members));

        await CreateMemberAsync(data, _acme, "ann@example.com", active: false);
        await CreateMemberAsync(data, _acme, "ann@example.com", active: false);
        await CreateMemberAsync(data, _acme, "ann@example.com", active: true);
        var refused = await Should.ThrowAsync<AlvoConstraintViolationException>(
            () => CreateMemberAsync(data, _acme, "ann@example.com", active: true));

        refused.Kind.ShouldBe(AlvoConstraintKind.Unique);
        refused.Fields.ShouldBe(["email"]);
    }

    /// <summary>A partial unique index on a scoped entity spans the tenant, so two tenants each hold one active email.</summary>
    [Fact]
    public async Task A_partial_unique_index_is_per_tenant()
    {
        var data = await CreateAsync(Schema(Members, ActiveWhere), Descriptor(Members));

        await CreateMemberAsync(data, _acme, "ann@example.com", active: true);
        await CreateMemberAsync(data, _globex, "ann@example.com", active: true);
    }

    /// <summary>A changed predicate is a new name (D10): the plan drops the old index and creates the new one.</summary>
    [Fact]
    public async Task Changing_a_where_recreates_the_index()
    {
        await CreateAsync(Schema(Members, ActiveWhere), Descriptor(Members));

        var sql = string.Join("\n", (await PlanAsync(Schema(Members, ActiveWhere), Schema(Members, "!active"))).Sql);

        var (oldName, newName) = (NameIn(sql, "DROP INDEX"), NameIn(sql, "CREATE UNIQUE INDEX"));
        oldName.ShouldStartWith("ix_members_");
        newName.ShouldStartWith("ix_members_");
        newName.ShouldNotBe(oldName);
    }

    /// <summary>
    /// Review focus 3, narrowed by review R4: a <c>where</c> added by a later apply to a table that already holds rows applies
    /// on both engines and holds — a predicate over the row alone, which PostgreSQL accepts as <c>IMMUTABLE</c>.
    /// </summary>
    [Fact]
    public async Task A_where_added_to_an_existing_table_applies_and_holds()
    {
        var data = await CreateAsync(Schema(Members, where: null), Descriptor(Members));
        await CreateMemberAsync(data, _acme, "bo@example.com", active: false);
        await CreateMemberAsync(data, _acme, "bo@example.com", active: true);

        await ApplyAsync(Schema(Members, where: null), Schema(Members, ActiveWhere), Descriptor(Members));

        await CreateMemberAsync(data, _acme, "bo@example.com", active: false);
        (await Should.ThrowAsync<AlvoConstraintViolationException>(
            () => CreateMemberAsync(data, _acme, "bo@example.com", active: true))).Kind.ShouldBe(AlvoConstraintKind.Unique);
    }

    /// <summary>
    /// Review R4's regression: <c>timestamp('…')</c> is not a constant a stored predicate may hold in HX-12, so a <c>where</c>
    /// comparing with one is refused at its pointer on every engine and nothing is applied.
    /// </summary>
    [Fact]
    public async Task A_where_comparing_with_a_timestamp_constant_is_refused_at_its_pointer()
    {
        await CreateAsync(Schema(Members, where: null), Descriptor(Members));

        var refusal = await Should.ThrowAsync<DescriptorValidationException>(
            () => ApplyAsync(Schema(Members, where: null), Schema(Members, TimestampConstantWhere), Descriptor(Members)));

        refusal.Result.Errors.ShouldAllBe(error => error.Path == "/entities/members/indexes/0/where");
        (await IndexNamesAsync(Members)).ShouldNotContain(name => name.StartsWith("ix_", StringComparison.Ordinal));
    }

    /// <summary>
    /// A predicate the engine itself refuses at apply — here a duration over a field, which the validator refuses (D11) and
    /// which reaches the migrator only from a schema no validator saw — is a refusal at the <c>where</c>'s pointer, never a 500.
    /// </summary>
    [Fact]
    public async Task An_engine_refusal_of_a_where_is_refused_at_its_pointer()
    {
        const string Mutable = "joined_at + duration('1h') > joined_at";
        await CreateAsync(Schema(Members, where: null), Descriptor(Members));

        var apply = () => ApplyAsync(Schema(Members, where: null), Schema(Members, Mutable), Descriptor(Members));

        if (!RefusesANonImmutablePredicate)
        {
            await apply();
            return;
        }

        var refusal = await Should.ThrowAsync<DescriptorValidationException>(apply);
        var error = refusal.Result.Errors.ShouldHaveSingleItem();
        error.Path.ShouldBe("/entities/members/indexes/0/where");
        error.Message.ShouldBe("The database refused this index's 'where' as a partial index predicate. Nothing was applied.");
    }

    /// <summary>
    /// Renaming a field the <c>where</c> reads re-creates the index under the name the new predicate gives it, dropping the
    /// old one before the rename (D10) — and the new one holds.
    /// </summary>
    [Fact]
    public async Task Renaming_a_field_a_where_reads_recreates_the_index_under_its_new_name()
    {
        var data = await CreateAsync(Schema(Members, ActiveWhere), Descriptor(Members));
        var renamed = Schema(Members, "enabled", activeField: "enabled", activeRenamedFrom: "active");

        var sql = string.Join("\n", (await PlanAsync(Schema(Members, ActiveWhere), renamed)).Sql);
        await ApplyAsync(Schema(Members, ActiveWhere), renamed, Descriptor(Members, activeField: "enabled"));

        NameIn(sql, "DROP INDEX").ShouldNotBe(NameIn(sql, "CREATE UNIQUE INDEX"));
        (await IndexNamesAsync(Members)).Where(name => name.StartsWith("ix_", StringComparison.Ordinal))
            .ShouldBe([NameIn(sql, "CREATE UNIQUE INDEX")]);
        await CreateMemberAsync(data, _acme, "cy@example.com", active: true, activeField: "enabled");
        (await Should.ThrowAsync<AlvoConstraintViolationException>(
            () => CreateMemberAsync(data, _acme, "cy@example.com", active: true, activeField: "enabled"))).Kind.ShouldBe(AlvoConstraintKind.Unique);
    }

    /// <summary>Renaming the entity keeps its partial unique index enforced, under the name the new entity gives it.</summary>
    [Fact]
    public async Task Renaming_an_entity_with_a_partial_unique_index_keeps_it_enforced()
    {
        var data = await CreateAsync(Schema(Members, ActiveWhere), Descriptor(Members));

        await ApplyAsync(Schema(Members, ActiveWhere), Schema(People, ActiveWhere, renamedFrom: Members), Descriptor(People));

        await CreateMemberAsync(data, _acme, "di@example.com", active: true, entity: People);
        (await Should.ThrowAsync<AlvoConstraintViolationException>(
            () => CreateMemberAsync(data, _acme, "di@example.com", active: true, entity: People))).Kind.ShouldBe(AlvoConstraintKind.Unique);
    }

    private static string NameIn(string sql, string verb)
    {
        var at = sql.IndexOf(verb, StringComparison.Ordinal);
        at.ShouldBeGreaterThanOrEqualTo(0, $"the plan holds a {verb}:\n{sql}");
        var start = sql.IndexOf("ix_", at, StringComparison.Ordinal);
        var end = sql.IndexOfAny(['"', ' ', ';', '\n'], start);
        return sql[start..end];
    }

    private static Task<AlvoRecord> CreateMemberAsync(
        IAlvoData data, AlvoContext caller, string email, bool active, DateTimeOffset? joinedAt = null,
        string entity = Members, string activeField = "active") =>
        data.CreateAsync(
            entity,
            new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["tenant_id"] = caller.Tenant!.Value.Value,
                ["email"] = email,
                [activeField] = active,
                ["joined_at"] = joinedAt ?? _in2026,
            },
            caller,
            cancellationToken: Ct);

    private static AlvoContext InTenant(TenantId tenant) => new()
    {
        User = UserId.New(),
        Roles = new HashSet<Role> { Role.Authenticated },
        Tenant = tenant,
    };

    private static SchemaModel Schema(
        string name, string? where, string? renamedFrom = null, string activeField = "active", string? activeRenamedFrom = null) =>
        new([new EntitySchema
        {
            Name = name,
            RenamedFrom = renamedFrom,
            Tenancy = TenancyMode.Scoped,
            Fields =
            [
                new FieldSchema { Name = "id", Type = SchemaField.Uuid, Required = true },
                new FieldSchema { Name = "email", Type = SchemaField.String, Required = true },
                new FieldSchema { Name = activeField, RenamedFrom = activeRenamedFrom, Type = SchemaField.Boolean, Required = true },
                new FieldSchema { Name = "joined_at", Type = SchemaField.DateTime, Required = true },
                new FieldSchema { Name = "tenant_id", Type = SchemaField.Uuid, Required = true, Indexed = true },
            ],
            Indexes = where is null ? [] : [new IndexSchema(["email"], Unique: true) { Where = where }],
        }]);

    private static AlvoDescriptor Descriptor(string entity, string activeField = "active") => new()
    {
        ApiVersion = "alvo.dev/v1",
        Name = "partial-indexes",
        Tenancy = new Tenancy { Enabled = true },
        Entities = new Dictionary<string, EntityDescriptor>(StringComparer.Ordinal)
        {
            [entity] = new()
            {
                Tenancy = EntityTenancy.Scoped,
                Fields = new Dictionary<string, FieldDescriptor>(StringComparer.Ordinal)
                {
                    ["email"] = new() { Type = DescField.String, Required = true },
                    [activeField] = new() { Type = DescField.Boolean, Required = true },
                    ["joined_at"] = new() { Type = DescField.DateTime, Required = true },
                },
                Rules = new AccessRules { List = "true", Get = "true", Create = "true", Update = "true", Delete = "true" },
            },
        },
    };
}
