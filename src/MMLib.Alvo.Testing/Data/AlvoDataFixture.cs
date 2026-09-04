using MMLib.Alvo.Data;
using MMLib.Alvo.Descriptor;
using MMLib.Alvo.Rules;
using MMLib.Alvo.Schema;
using Xunit;
using DescField = MMLib.Alvo.Descriptor.FieldType;
using SchemaField = MMLib.Alvo.Schema.FieldType;

namespace MMLib.Alvo.Testing.Data;

/// <summary>
/// The fixture both port-level write suites are built on: one store over a hand-paired descriptor and
/// schema, the callers and tenants they write as, and the payload helpers they share.
/// </summary>
/// <remarks>
/// <para>
/// Hoisted out of <see cref="AlvoDataConcurrencyTests"/> when <c>AlvoDataBatchTests</c> arrived and
/// needed the same world. The alternative was a second copy of ~250 lines of scaffolding, which is how two
/// fixtures that must agree come to disagree — and these two must agree, because the batch suite's whole
/// claim is that a batch judges a row exactly as its single-row sibling does.
/// </para>
/// <para>
/// <b>There is deliberately no unfiltered row-count seam.</b> A batch's atomicity claim needs "how many rows
/// does this entity hold", and the obvious way to get it — a per-leg abstract that reaches past the port —
/// would be a second read path answering a question the port itself can answer: every fixture here is
/// <see cref="EntityFixture.Permissive"/> and global, so <c>QueryAsync</c> as <see cref="World.Caller"/> has
/// no predicate filtering it and its count <em>is</em> the entity's count. The one fixture where that is
/// untrue is the tenant-scoped one, and there a count is the wrong assertion anyway: what matters is that
/// the other tenant's row is unchanged, which is checked by reading it back <em>as that tenant</em> — a
/// stronger claim than a number, because it compares the value.
/// </para>
/// </remarks>
public abstract class AlvoDataFixture
{
    /// <summary>
    /// Builds a fresh <see cref="IAlvoData"/> over <paramref name="descriptor"/>/<paramref name="schema"/>,
    /// seeded out of band with <paramref name="seed"/>'s rows — the same seam
    /// <see cref="AlvoDataAdversarialTests.CreateAsync"/> defines, so an engine's subclass is the fixture it
    /// already has plus nothing.
    /// </summary>
    /// <remarks>
    /// Every fact in both suites seeds nothing and writes its rows through the port. That is not incidental:
    /// a version is only meaningful if the framework's own audit stamp wrote it, and a row inserted out of
    /// band carries whatever instant the seeding seam chose. Per-fact isolation is still required, exactly as
    /// the adversarial suite requires it — several facts assert an exact row count over an entity with no
    /// row-scoping predicate.
    /// </remarks>
    /// <param name="schema">The schema every entity in <paramref name="descriptor"/> maps to.</param>
    /// <param name="descriptor">The project descriptor whose rules apply.</param>
    /// <param name="seed">The initial rows to insert, keyed by entity name.</param>
    protected abstract Task<IAlvoData> CreateAsync(
        SchemaModel schema, AlvoDescriptor descriptor, IReadOnlyDictionary<string, IReadOnlyList<AlvoRecord>> seed);

    private protected const string Orders = "orders";
    private protected const string Receipts = "receipts";
    private protected const string Tickets = "tickets";
    private protected const string Drafts = "drafts";
    private protected const string Invoices = "invoices";
    private protected const string Vaults = "vaults";
    private protected const string Dropbox = "dropbox";

    private protected static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>A key no other fact can collide with, so facts stay independent even on a shared store.</summary>
    private protected static string NewKey() => $"key-{Guid.NewGuid():N}";

    /// <summary>
    /// A fresh token whose fingerprint covers <paramref name="entity"/>, as
    /// <see cref="AlvoIdempotency.Fingerprint"/> requires of whoever computes one.
    /// </summary>
    /// <param name="entity">The entity the fingerprinted request writes.</param>
    private protected static AlvoIdempotency TokenFor(string entity) => new(NewKey(), $"{entity}:a-request-digest");

    private protected static Dictionary<string, object?> Payload(string title) =>
        new(StringComparer.Ordinal) { ["title"] = title };

    private protected static Dictionary<string, object?> OwnedPayload(string title, AlvoContext owner) =>
        new(StringComparer.Ordinal) { ["title"] = title, ["owner_id"] = owner.User.Value };

    private protected static Dictionary<string, object?> TenantPayload(string title, TenantId tenant) =>
        new(StringComparer.Ordinal) { ["title"] = title, ["tenant_id"] = tenant.Value };

    private protected static Guid IdOf(AlvoRecord record) => (Guid)record[AlvoManagedColumns.Id]!;

    /// <summary>
    /// The row's version as this port returned it, read from the record rather than reconstructed — which is
    /// the point of every round-trip assertion above.
    /// </summary>
    private protected static DateTimeOffset VersionOf(AlvoRecord record) =>
        (DateTimeOffset)record[AlvoManagedColumns.UpdatedAt]!;
    /// <summary>An audited, global <c>orders</c> entity every operation is permitted on.</summary>
    private protected Task<World> AuditedWorldAsync() => WorldAsync(EntityFixture.Permissive(Orders, audit: true));

    /// <summary>
    /// An audited <c>orders</c> entity whose rules admit the anonymous caller, so the token refusal is reached
    /// on a create the policy would otherwise allow — and the tokenless create in the same fact really lands.
    /// </summary>
    private protected Task<World> AnonymousWorldAsync() => WorldAsync(
        EntityFixture.Permissive(Orders, audit: true) with
        {
            Rules = new AccessRules { List = "true", Get = "true", Create = "true" },
        });

    /// <summary>A non-audited <c>drafts</c> entity — the one with no version column at all.</summary>
    private protected Task<World> UnauditedWorldAsync() => WorldAsync(EntityFixture.Permissive(Drafts, audit: false));

    /// <summary>
    /// An audited <c>tickets</c> entity row-scoped by owner, so one caller's row is genuinely invisible to
    /// the other — which is what lets the check-order and cross-user facts tell their answers apart.
    /// </summary>
    private protected Task<World> OwnedWorldAsync() => WorldAsync(
        EntityFixture.Permissive(Tickets, audit: true) with
        {
            Rules = OwnerRules,
            Extra = ("owner_id", DescField.Uuid),
        });

    /// <summary>An audited, tenant-scoped <c>invoices</c> entity, plus a caller in each of two tenants.</summary>
    private protected Task<World> TenantedWorldAsync() => WorldAsync(
        EntityFixture.Permissive(Invoices, audit: true) with { Tenancy = EntityTenancy.Scoped });

    /// <summary>
    /// An audited <c>vaults</c> entity with a field whose <c>hidden</c> expression covers a non-admin caller,
    /// so a replay's projection is comparable against what a <c>get</c> by that caller returns.
    /// </summary>
    private protected Task<World> MaskedWorldAsync() => WorldAsync(
        EntityFixture.Permissive(Vaults, audit: true) with { Hidden = ("secret", "!('admin' in @user.roles)") });

    /// <summary>
    /// An audited <c>dropbox</c> entity a caller may write and not read — no <c>get</c> or <c>list</c> rule at
    /// all, so the replay's own read has nothing to resolve. <c>delete</c> is granted too, deliberately: it is
    /// still a write, and it is what lets the concurrency suite's read-denied replay fact remove the row out
    /// from under a replay, to prove structurally that nothing reads it.
    /// </summary>
    private protected Task<World> WriteOnlyWorldAsync() => WorldAsync(
        EntityFixture.Permissive(Dropbox, audit: true) with
        {
            Rules = new AccessRules { Create = "true", Delete = "true" },
        });

    /// <summary>Two audited, permissive entities in one store, for the one-key-two-entities fact.</summary>
    private protected Task<World> TwoEntityWorldAsync() => WorldAsync(
        EntityFixture.Permissive(Orders, audit: true),
        EntityFixture.Permissive(Receipts, audit: true));

    private protected const string OwnerRule = "owner_id == @user.id";

    private protected static AccessRules OwnerRules => new()
    {
        List = OwnerRule,
        Get = OwnerRule,
        Create = OwnerRule,
        Update = OwnerRule,
        Delete = OwnerRule,
    };

    /// <summary>
    /// One entity of a fixture: the traits the descriptor and the schema have to agree on, in one place so the
    /// pair cannot drift.
    /// </summary>
    /// <param name="Name">The entity name.</param>
    /// <param name="Audit">Whether it declares <c>audit</c>, and therefore has a version column.</param>
    /// <param name="Tenancy">Its tenancy.</param>
    /// <param name="Rules">The access rules to compile.</param>
    /// <param name="Extra">An additional required field, for the owner-scoped fixture.</param>
    /// <param name="Hidden">A field and the <c>hidden</c> expression that masks it, for the masking fixture.</param>
    private protected sealed record EntityFixture(
        string Name,
        bool Audit,
        EntityTenancy Tenancy,
        AccessRules Rules,
        (string Name, DescField Type)? Extra = null,
        (string Field, string Expression)? Hidden = null)
    {
        internal static EntityFixture Permissive(string name, bool audit) => new(
            name,
            audit,
            EntityTenancy.Global,
            new AccessRules { List = "true", Get = "true", Create = "true", Update = "true", Delete = "true" });
    }

    /// <summary>
    /// A store over <paramref name="entities"/>, its descriptor and its schema paired by hand — the schema
    /// mapper that injects the managed columns is <see langword="internal"/> to the core, so this suite pairs
    /// them exactly as the adversarial suite does.
    /// </summary>
    /// <param name="entities">The entities the fixture declares.</param>
    private protected async Task<World> WorldAsync(params EntityFixture[] entities)
    {
        var descriptor = new AlvoDescriptor
        {
            ApiVersion = "alvo.dev/v1",
            Name = "concurrency-fixture",
            Entities = entities.ToDictionary(entity => entity.Name, DescriptorOf, StringComparer.Ordinal),
        };

        var data = await CreateAsync(
            new SchemaModel([.. entities.Select(SchemaOf)]),
            descriptor,
            new Dictionary<string, IReadOnlyList<AlvoRecord>>(StringComparer.Ordinal));

        return new World(data);
    }

    private protected static EntityDescriptor DescriptorOf(EntityFixture entity) => new()
    {
        Tenancy = entity.Tenancy,
        Audit = entity.Audit,
        Fields = DescriptorFieldsOf(entity),
        Rules = entity.Rules,
    };

    private protected static Dictionary<string, FieldDescriptor> DescriptorFieldsOf(EntityFixture entity)
    {
        var fields = new Dictionary<string, FieldDescriptor>(StringComparer.Ordinal)
        {
            ["title"] = new() { Type = DescField.String },
        };
        if (entity.Extra is { } extra)
        {
            fields[extra.Name] = new FieldDescriptor { Type = extra.Type, Required = true };
        }

        if (entity.Hidden is { } hidden)
        {
            fields[hidden.Field] = new FieldDescriptor
            {
                Type = DescField.String,
                Hidden = BoolOrCel.FromExpression(hidden.Expression),
            };
        }

        return fields;
    }

    /// <summary>
    /// The schema half of one fixture entity: the row key, the declared fields, and whichever framework
    /// columns the traits ask for.
    /// </summary>
    private protected static EntitySchema SchemaOf(EntityFixture entity) => new()
    {
        Name = entity.Name,
        Tenancy = entity.Tenancy == EntityTenancy.Scoped ? TenancyMode.Scoped : TenancyMode.Global,
        Audit = entity.Audit,
        Fields = [.. SchemaFieldsOf(entity)],
    };

    /// <summary>The row key and whatever the fixture declares, then whatever its traits inject.</summary>
    private protected static IEnumerable<FieldSchema> SchemaFieldsOf(EntityFixture entity) =>
        [.. DeclaredFieldsOf(entity), .. ManagedFieldsOf(entity)];

    private protected static IEnumerable<FieldSchema> DeclaredFieldsOf(EntityFixture entity)
    {
        yield return new FieldSchema { Name = AlvoManagedColumns.Id, Type = SchemaField.Uuid, Required = true };
        yield return new FieldSchema { Name = "title", Type = SchemaField.String, Nullable = true };

        if (entity.Extra is { } extra)
        {
            yield return new FieldSchema
            {
                Name = extra.Name,
                Type = Enum.Parse<SchemaField>(extra.Type.ToString()),
                Required = true,
            };
        }

        if (entity.Hidden is { } hidden)
        {
            yield return new FieldSchema { Name = hidden.Field, Type = SchemaField.String, Nullable = true };
        }
    }

    /// <summary>The columns the framework injects for these traits, in the mapper's own order.</summary>
    private protected static IEnumerable<FieldSchema> ManagedFieldsOf(EntityFixture entity) =>
    [
        .. entity.Tenancy == EntityTenancy.Scoped ? TenantField : [],
        .. entity.Audit ? AuditFields : [],
    ];

    private protected static IEnumerable<FieldSchema> TenantField =>
    [
        new FieldSchema
        {
            Name = AlvoManagedColumns.TenantId,
            Type = SchemaField.Uuid,
            Required = true,
            Indexed = true,
        },
    ];

    /// <summary>
    /// The audit quartet as the schema mapper injects it. <c>updated_at</c> is <c>required</c> — the version
    /// column a precondition compares can never be absent on a row the framework wrote.
    /// </summary>
    private protected static IEnumerable<FieldSchema> AuditFields =>
    [
        new FieldSchema { Name = AlvoManagedColumns.CreatedAt, Type = SchemaField.DateTime, Required = true },
        new FieldSchema { Name = AlvoManagedColumns.CreatedBy, Type = SchemaField.Uuid, Nullable = true },
        new FieldSchema { Name = AlvoManagedColumns.UpdatedAt, Type = SchemaField.DateTime, Required = true },
        new FieldSchema { Name = AlvoManagedColumns.UpdatedBy, Type = SchemaField.Uuid, Nullable = true },
    ];

    /// <summary>One fixture database plus the callers and tenants the facts above write as.</summary>
    private protected sealed class World(IAlvoData data)
    {
        internal IAlvoData Data { get; } = data;

        /// <summary>The single caller the global fixtures write as.</summary>
        internal AlvoContext Caller { get; } = NewCaller(tenant: null);

        internal TenantId Acme { get; } = TenantId.New();

        internal TenantId Globex { get; } = TenantId.New();

        /// <summary>
        /// Two callers <b>in one tenant</b>, which is what the cross-user replay fact needs: with a record
        /// identity scoped to the tenant alone, these two would share one key space.
        /// </summary>
        internal AlvoContext Alice => _alice ??= NewCaller(Acme);

        /// <inheritdoc cref="Alice"/>
        internal AlvoContext Bob => _bob ??= NewCaller(Acme);

        internal AlvoContext AcmeCaller => _acmeCaller ??= NewCaller(Acme);

        internal AlvoContext GlobexCaller => _globexCaller ??= NewCaller(Globex);

        private AlvoContext? _alice;
        private AlvoContext? _bob;
        private AlvoContext? _acmeCaller;
        private AlvoContext? _globexCaller;

        private static AlvoContext NewCaller(TenantId? tenant) => new()
        {
            User = UserId.New(),
            Roles = new HashSet<Role> { Role.Authenticated },
            Tenant = tenant,
        };
    }
}
