using Microsoft.Extensions.DependencyInjection;
using MMLib.Alvo.Data.EntityFrameworkCore;
using MMLib.Alvo.Descriptor;
using MMLib.Alvo.Schema;
using MMLib.Alvo.Testing.Data;
using DescriptorFieldType = MMLib.Alvo.Descriptor.FieldType;
using SchemaFieldType = MMLib.Alvo.Schema.FieldType;

namespace MMLib.Alvo.Data.Sqlite.Tests;

/// <summary>
/// The three seeded databases the SQLite data-path tests read and write: <c>notes</c> (row-scoped by
/// owner, all five operations), <c>accounts</c> (a masked field, a read-only field, no row scoping) and
/// <c>vehicle</c> (one column of every CLR type the port maps).
/// </summary>
/// <remarks>
/// Deliberately the same descriptor shapes <c>AlvoDataAdversarialTests</c> builds, so these
/// statement-level facts and the inherited outcome-level suite are talking about the same policies. They
/// are rebuilt here rather than shared because that suite's own builders are private to it — it is a
/// shipped contract suite, not a fixture library.
/// </remarks>
internal static class AlvoDataWorlds
{
    internal static async Task<DataWorld> NotesAsync(SqliteAlvoDataFixture fixture, bool includeNullTitleRow = false)
    {
        var tenant = TenantId.New();
        var alice = Caller(tenant);
        var bob = Caller(tenant);
        var fields = new Dictionary<string, FieldDescriptor>(StringComparer.Ordinal)
        {
            ["owner_id"] = new() { Type = DescriptorFieldType.Uuid, Required = true },
            ["title"] = new() { Type = DescriptorFieldType.String },
            ["label"] = new() { Type = DescriptorFieldType.String, Required = true },
        };
        var rules = new AccessRules
        {
            List = OwnerRule,
            Get = OwnerRule,
            Create = OwnerRule,
            Update = OwnerRule,
            Delete = OwnerRule,
        };

        var aliceRow = Guid.NewGuid();
        var aliceSecondRow = Guid.NewGuid();
        var bobRow = Guid.NewGuid();
        var rows = new List<AlvoRecord>
        {
            Row(aliceRow, ("owner_id", alice.User.Value), ("tenant_id", tenant.Value), ("title", "Alice-1"), ("label", "a1")),
            Row(aliceSecondRow, ("owner_id", alice.User.Value), ("tenant_id", tenant.Value), ("title", "Alice-2"), ("label", "a2")),
            Row(bobRow, ("owner_id", bob.User.Value), ("tenant_id", tenant.Value), ("title", "Bob-1"), ("label", "b1")),
        };
        if (includeNullTitleRow)
        {
            rows.Add(Row(
                Guid.NewGuid(), ("owner_id", alice.User.Value), ("tenant_id", tenant.Value), ("title", null), ("label", "a3")));
        }

        var host = await StartAsync(fixture, "notes", fields, EntityTenancy.Scoped, rules, Seed("notes", rows));
        return new DataWorld(host)
        {
            Alice = alice,
            Bob = bob,
            Tenant = tenant,
            AliceRowId = aliceRow,
            AliceSecondRowId = aliceSecondRow,
            BobRowId = bobRow,
        };
    }

    internal static async Task<DataWorld> AccountsAsync(SqliteAlvoDataFixture fixture)
    {
        var fields = new Dictionary<string, FieldDescriptor>(StringComparer.Ordinal)
        {
            ["title"] = new() { Type = DescriptorFieldType.String },
            ["secret"] = new() { Type = DescriptorFieldType.String, Hidden = BoolOrCel.FromBoolean(true) },
            ["note"] = new()
            {
                Type = DescriptorFieldType.String,
                Hidden = BoolOrCel.FromExpression("!('admin' in @user.roles)"),
            },
            ["status"] = new() { Type = DescriptorFieldType.String, ReadOnly = BoolOrCel.FromBoolean(true) },
        };
        var rules = new AccessRules { List = "true", Get = "true", Create = "true", Update = "true", Delete = "true" };

        var rowId = Guid.NewGuid();
        var secondRowId = Guid.NewGuid();
        var seed = Seed(
            "accounts",
            [
                Row(rowId, ("title", "Acct"), ("secret", "shh"), ("note", "internal"), ("status", "active")),
                Row(secondRowId, ("title", "Acct-2"), ("secret", "shh-2"), ("note", "internal-2"), ("status", "active")),
            ]);

        var host = await StartAsync(fixture, "accounts", fields, EntityTenancy.Global, rules, seed);
        return new DataWorld(host)
        {
            Member = Caller(tenant: null),
            Admin = Caller(tenant: null, Role.Admin),
            RowId = rowId,
            SecondRowId = secondRowId,
        };
    }

    /// <summary>
    /// The canonical <c>vehicle</c> entity with one fully populated row, so a read and a patch exercise
    /// every CLR type the port maps.
    /// </summary>
    /// <param name="fixture">The fixture standing the database up.</param>
    /// <param name="extraRows">
    /// Further rows carrying only a plate and a timestamp, for the facts that page over a
    /// <c>created_at</c> key.
    /// </param>
    internal static async Task<DataWorld> VehicleAsync(
        SqliteAlvoDataFixture fixture, params (string Plate, DateTimeOffset CreatedAt)[] extraRows)
    {
        ArgumentNullException.ThrowIfNull(extraRows);
        var tenant = TenantId.New();
        var alice = Caller(tenant);
        var rowId = Guid.NewGuid();
        var row = Row(
            rowId,
            ("tenant_id", tenant.Value),
            ("owner_id", alice.User.Value),
            ("plate", "ACME-001"),
            ("status", "open"),
            ("secret_note", "shh"),
            ("mileage", 10L),
            ("price", 9.99m),
            ("is_public", true),
            ("due_on", new DateOnly(2026, 1, 2)),
            ("created_at", DateTimeOffset.UnixEpoch.AddDays(1)));

        var extras = extraRows.Select(extra => Row(
            Guid.NewGuid(),
            ("tenant_id", tenant.Value),
            ("owner_id", alice.User.Value),
            ("plate", extra.Plate),
            ("created_at", extra.CreatedAt)));

        var host = await StartVehicleAsync(fixture, [row, .. extras]);
        return new DataWorld(host) { Alice = alice, Tenant = tenant, RowId = rowId };
    }

    /// <summary>
    /// An entity whose sort keys are all <b>required</b>, for the facts that page: a keyset cursor cannot
    /// express where a nullable key's nulls sort, so a paged read over one is refused. Its <c>amount</c> is a
    /// <c>decimal</c> — the type whose SQLite storage is <c>TEXT</c>, and therefore the ordering this data path
    /// has to repair — and its <c>occurred_at</c> is a timestamp, whose values must bind through the column.
    /// </summary>
    internal static async Task<DataWorld> LedgerAsync(
        SqliteAlvoDataFixture fixture, IReadOnlyList<decimal> amounts)
    {
        ArgumentNullException.ThrowIfNull(amounts);
        var fields = new Dictionary<string, FieldDescriptor>(StringComparer.Ordinal)
        {
            ["owner_id"] = new() { Type = DescriptorFieldType.Uuid, Required = true },
            ["amount"] = new() { Type = DescriptorFieldType.Decimal, Required = true },
            ["occurred_at"] = new() { Type = DescriptorFieldType.DateTime, Required = true },
        };
        var tenant = TenantId.New();
        var alice = Caller(tenant);
        var rows = amounts.Select((amount, index) => Row(
            Guid.NewGuid(),
            ("tenant_id", tenant.Value),
            ("owner_id", alice.User.Value),
            ("amount", amount),
            ("occurred_at", DateTimeOffset.UnixEpoch.AddDays(index + 1))));

        var host = await StartAsync(
            fixture, "ledger", fields, EntityTenancy.Scoped, OwnerRules(), Seed("ledger", [.. rows]));
        return new DataWorld(host) { Alice = alice, Tenant = tenant };
    }

    private static async Task<AlvoDataHost> StartVehicleAsync(
        SqliteAlvoDataFixture fixture, IReadOnlyList<AlvoRecord> rows)
    {
        var host = await fixture.StartAsync(new SchemaModel([AlvoDataFixtures.Vehicle]), VehicleDescriptor());
        await SeedAsync(host, Seed(AlvoDataFixtures.Vehicle.Name, rows));
        return host;
    }

    /// <summary>
    /// An entity whose <c>update</c> rule references a <c>hidden</c> field, so the <c>WITH CHECK</c> verdict
    /// can only be reached over an <b>unmasked</b> pre-image: read through the mask, <c>secret</c> arrives as
    /// the projected <c>NULL</c> and the rule denies an update that policy allows.
    /// </summary>
    internal static async Task<DataWorld> GuardedSecretAsync(SqliteAlvoDataFixture fixture)
    {
        var fields = new Dictionary<string, FieldDescriptor>(StringComparer.Ordinal)
        {
            ["title"] = new() { Type = DescriptorFieldType.String },
            ["secret"] = new() { Type = DescriptorFieldType.String, Hidden = BoolOrCel.FromBoolean(true) },
        };
        var rules = new AccessRules { List = "true", Get = "true", Update = "secret == 'shh'" };

        var rowId = Guid.NewGuid();
        var seed = Seed("vaults", [Row(rowId, ("title", "Vault"), ("secret", "shh"))]);

        var host = await StartAsync(fixture, "vaults", fields, EntityTenancy.Global, rules, seed);
        return new DataWorld(host) { Member = Caller(tenant: null), RowId = rowId };
    }

    private const string OwnerRule = "owner_id == @user.id";

    private static AccessRules OwnerRules() => new()
    {
        List = OwnerRule,
        Get = OwnerRule,
        Create = OwnerRule,
        Update = OwnerRule,
        Delete = OwnerRule,
    };

    private static AlvoDescriptor VehicleDescriptor() => new()
    {
        ApiVersion = "alvo.dev/v1",
        Name = "vehicle-world",
        Entities = new Dictionary<string, EntityDescriptor>(StringComparer.Ordinal)
        {
            [AlvoDataFixtures.Vehicle.Name] = new EntityDescriptor
            {
                Fields = new Dictionary<string, FieldDescriptor>(StringComparer.Ordinal),
                Tenancy = EntityTenancy.Scoped,
                Rules = new AccessRules
                {
                    List = OwnerRule,
                    Get = OwnerRule,
                    Create = OwnerRule,
                    Update = OwnerRule,
                    Delete = OwnerRule,
                },
            },
        },
    };

    private static async Task<AlvoDataHost> StartAsync(
        SqliteAlvoDataFixture fixture,
        string entity,
        Dictionary<string, FieldDescriptor> fields,
        EntityTenancy tenancy,
        AccessRules rules,
        Dictionary<string, IReadOnlyList<AlvoRecord>> seed)
    {
        var (descriptor, schema) = Build(entity, fields, tenancy, rules);
        var host = await fixture.StartAsync(schema, descriptor);
        await SeedAsync(host, seed);
        return host;
    }

    /// <summary>
    /// Seeds out of band and then forgets the statements it took, so a test's own assertions are about its
    /// own act rather than about the fixture's inserts.
    /// </summary>
    private static async Task SeedAsync(AlvoDataHost host, Dictionary<string, IReadOnlyList<AlvoRecord>> seed)
    {
        await AlvoDataSeed.SeedAsync(
            host.Services.GetRequiredService<AlvoDataContextFactory>(), seed, TestContext.Current.CancellationToken);
        host.ClearStatements();
    }

    private static Dictionary<string, IReadOnlyList<AlvoRecord>> Seed(string entity, IReadOnlyList<AlvoRecord> rows) =>
        new(StringComparer.Ordinal) { [entity] = rows };

    private static AlvoRecord Row(Guid id, params (string Field, object? Value)[] fields)
    {
        var values = new Dictionary<string, object?>(StringComparer.Ordinal) { ["id"] = id };
        foreach (var (field, value) in fields)
        {
            values[field] = value;
        }

        return new AlvoRecord(values);
    }

    private static AlvoContext Caller(TenantId? tenant, params Role[] extraRoles)
    {
        var roles = new HashSet<Role> { Role.Authenticated };
        foreach (var role in extraRoles)
        {
            roles.Add(role);
        }

        return new AlvoContext { User = UserId.New(), Roles = roles, Tenant = tenant };
    }

    /// <summary>
    /// Mirrors the <c>id</c>/<c>tenant_id</c> injection <c>DescriptorToSchemaMapper</c> performs in the
    /// core — that mapper is <see langword="internal"/> there, so a descriptor and its schema are paired by
    /// hand, exactly as the shipped adversarial suite pairs them.
    /// </summary>
    private static (AlvoDescriptor Descriptor, SchemaModel Schema) Build(
        string entity, Dictionary<string, FieldDescriptor> fields, EntityTenancy tenancy, AccessRules rules)
    {
        var descriptor = new AlvoDescriptor
        {
            ApiVersion = "alvo.dev/v1",
            Name = "sqlite-data-world",
            Entities = new Dictionary<string, EntityDescriptor>(StringComparer.Ordinal)
            {
                [entity] = new EntityDescriptor { Fields = fields, Tenancy = tenancy, Rules = rules },
            },
        };

        var tenancyMode = tenancy == EntityTenancy.Scoped ? TenancyMode.Scoped : TenancyMode.Global;
        List<FieldSchema> schemaFields =
        [
            new FieldSchema { Name = "id", Type = SchemaFieldType.Uuid, Required = true },
            .. fields.Select(pair => ToFieldSchema(pair.Key, pair.Value)),
        ];
        if (tenancyMode == TenancyMode.Scoped)
        {
            schemaFields.Add(new FieldSchema { Name = "tenant_id", Type = SchemaFieldType.Uuid, Required = true, Indexed = true });
        }

        return (descriptor, new SchemaModel([new EntitySchema { Name = entity, Tenancy = tenancyMode, Fields = schemaFields }]));
    }

    private static FieldSchema ToFieldSchema(string name, FieldDescriptor field) => new()
    {
        Name = name,
        Type = Enum.Parse<SchemaFieldType>(field.Type.ToString()),
        Required = field.Required == true,
        Nullable = field.Nullable ?? field.Required != true,
    };
}

/// <summary>One seeded database plus the callers and row ids a test asserts against.</summary>
internal sealed class DataWorld(AlvoDataHost host)
{
    /// <summary>
    /// The port itself, for the few facts that are about the call rather than about the data — a
    /// <see langword="null"/> context, an unimplemented member. Everything else goes through the wrappers
    /// below, which supply the ambient test cancellation token in one place.
    /// </summary>
    internal IAlvoData Data => host.Data;

    internal Task<IReadOnlyList<AlvoRecord>> QueryAsync(AlvoQuery query, AlvoContext caller) =>
        Data.QueryAsync(query, caller, TestContext.Current.CancellationToken);

    internal Task<AlvoRecord?> GetAsync(string entity, Guid id, AlvoContext caller) =>
        Data.GetAsync(entity, id, caller, TestContext.Current.CancellationToken);

    internal Task<AlvoRecord> CreateAsync(string entity, IReadOnlyDictionary<string, object?> values, AlvoContext caller) =>
        Data.CreateAsync(entity, values, caller, TestContext.Current.CancellationToken);

    internal Task<AlvoRecord> UpdateAsync(
        string entity, Guid id, IReadOnlyDictionary<string, object?> values, AlvoContext caller) =>
        Data.UpdateAsync(entity, id, values, caller, TestContext.Current.CancellationToken);

    internal Task DeleteAsync(string entity, Guid id, AlvoContext caller) =>
        Data.DeleteAsync(entity, id, caller, TestContext.Current.CancellationToken);

    internal IReadOnlyList<string> Statements => host.Statements;

    internal string LastStatement => host.LastStatement;

    internal void ClearStatements() => host.ClearStatements();

    internal IReadOnlyList<PreImageMutation> RequestedLocks => host.RequestedLocks;

    internal AlvoContext Alice { get; init; } = AlvoContext.Anonymous;

    internal AlvoContext Bob { get; init; } = AlvoContext.Anonymous;

    internal AlvoContext Member { get; init; } = AlvoContext.Anonymous;

    internal AlvoContext Admin { get; init; } = AlvoContext.Anonymous;

    internal TenantId Tenant { get; init; }

    internal Guid RowId { get; init; }

    internal Guid SecondRowId { get; init; }

    internal Guid AliceRowId { get; init; }

    internal Guid AliceSecondRowId { get; init; }

    internal Guid BobRowId { get; init; }

    /// <summary>The cursor this provider would issue for a returned row — the internal encoding, not a guess at it.</summary>
    internal static string CursorOf(AlvoRecord row)
    {
        ArgumentNullException.ThrowIfNull(row);
        return KeysetCursor.Encode((Guid)row["id"]!);
    }
}
