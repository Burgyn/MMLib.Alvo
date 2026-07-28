using MMLib.Alvo.Data;
using MMLib.Alvo.Descriptor;
using MMLib.Alvo.Schema;
using Shouldly;
using Xunit;
using DescField = MMLib.Alvo.Descriptor.FieldType;
using SchemaField = MMLib.Alvo.Schema.FieldType;

namespace MMLib.Alvo.Testing.Data;

/// <summary>
/// The write path's two concurrency channels as rules of the <b>port</b>, proved over every
/// <see cref="IAlvoData"/> implementation this suite runs against — the in-memory reference included: an
/// <see cref="AlvoPrecondition"/> that no longer matches the stored row refuses the write, an entity with no
/// version column refuses a precondition rather than ignoring it, and an <see cref="AlvoIdempotency"/> key
/// replayed with the same request returns the first row rather than creating a second one.
/// </summary>
/// <remarks>
/// <para>
/// A suite of its own rather than a section of <see cref="AlvoDataAdversarialTests"/>, on the same reasoning
/// that separated <see cref="AlvoDataPagingTests"/>: these facts are about what happens when <em>two</em>
/// writes meet, so several of them need a second write, a second caller, or a second tenant before they can
/// ask their question at all — and one of them needs two calls genuinely in flight at once. The adversarial
/// suite's shape is "one caller, one act, what may they not do"; nothing here fits it.
/// </para>
/// <para>
/// <b>Every fact is written to be able to fail for the reason its name claims</b>, which for three of them
/// takes deliberate construction:
/// </para>
/// <list type="bullet">
///   <item>
///   Every staleness fact <em>advances</em> the version with a real second write and asserts that it moved,
///   so "refused" cannot pass merely because the version never changed.
///   </item>
///   <item>
///   <see cref="The_version_a_write_returns_is_the_one_a_following_precondition_accepts"/> chains a create
///   into two updates, each precondition minted from the record the <em>previous</em> call returned. An
///   implementation comparing against its own clock instead of the stored pre-image passes on a store that
///   keeps 100-nanosecond ticks and fails on PostgreSQL, which keeps microseconds — which is exactly the
///   engine divergence this suite exists to surface.
///   </item>
///   <item>
///   <see cref="A_stale_precondition_is_refused_before_the_policy_check_reveals_anything"/> uses one stale
///   version against two rows — one visible to the caller, one not — so the two exception types are the only
///   thing that distinguishes a correct check order from an inverted one.
///   </item>
///   <item>
///   <see cref="Two_concurrent_creates_with_one_idempotency_key_produce_exactly_one_row"/> starts both calls
///   before awaiting either, so they are genuinely in flight together on any backend that awaits I/O.
///   </item>
/// </list>
/// </remarks>
public abstract class AlvoDataConcurrencyTests
{
    /// <summary>
    /// Builds a fresh <see cref="IAlvoData"/> over <paramref name="descriptor"/>/<paramref name="schema"/>,
    /// seeded out of band with <paramref name="seed"/>'s rows — the same seam
    /// <see cref="AlvoDataAdversarialTests.CreateAsync"/> defines, so an engine's subclass is the fixture it
    /// already has plus nothing.
    /// </summary>
    /// <remarks>
    /// Every fact here seeds nothing and writes its rows through the port. That is not incidental: a version
    /// is only meaningful if the framework's own audit stamp wrote it, and a row inserted out of band carries
    /// whatever instant the seeding seam chose. Per-fact isolation is still required, exactly as the
    /// adversarial suite requires it — several facts assert an exact row count over an entity with no
    /// row-scoping predicate.
    /// </remarks>
    /// <param name="schema">The schema every entity in <paramref name="descriptor"/> maps to.</param>
    /// <param name="descriptor">The project descriptor whose rules apply.</param>
    /// <param name="seed">The initial rows to insert, keyed by entity name.</param>
    protected abstract Task<IAlvoData> CreateAsync(
        SchemaModel schema, AlvoDescriptor descriptor, IReadOnlyDictionary<string, IReadOnlyList<AlvoRecord>> seed);

    /// <summary>
    /// The happy path, and the counterweight every refusal below needs: a precondition carrying the version
    /// the row actually holds is accepted, so none of the refusals can be satisfied by refusing every
    /// precondition.
    /// </summary>
    [Fact]
    public async Task An_update_whose_precondition_matches_the_stored_version_succeeds()
    {
        var world = await AuditedWorldAsync();
        var created = await world.Data.CreateAsync(Orders, Payload("first"), world.Caller);

        var updated = await world.Data.UpdateAsync(
            Orders, IdOf(created), Payload("second"), world.Caller, new AlvoPrecondition(VersionOf(created)));

        updated["title"].ShouldBe("second");
    }

    /// <summary>
    /// The lost update this channel exists to prevent: a second writer already advanced the row, so the
    /// first writer's version no longer describes it and their write must not land. The stored title is
    /// asserted afterwards because an implementation that threw <em>after</em> writing would satisfy the
    /// exception assertion alone.
    /// </summary>
    [Fact]
    public async Task An_update_whose_precondition_is_stale_is_refused_and_changes_nothing()
    {
        var world = await AuditedWorldAsync();
        var created = await world.Data.CreateAsync(Orders, Payload("first"), world.Caller);
        var stale = VersionOf(created);
        var advanced = await world.Data.UpdateAsync(Orders, IdOf(created), Payload("second"), world.Caller);
        VersionOf(advanced).ShouldNotBe(stale, "a write must advance the version, or nothing below discriminates");

        await Should.ThrowAsync<AlvoPreconditionFailedException>(() => world.Data.UpdateAsync(
            Orders, IdOf(created), Payload("third"), world.Caller, new AlvoPrecondition(stale)));

        var stored = await world.Data.GetAsync(Orders, IdOf(created), world.Caller);
        stored.ShouldNotBeNull();
        stored!["title"].ShouldBe("second", "the refused write must not have landed");
    }

    /// <summary>
    /// The same rule on the delete path, where the cost of getting it wrong is not an overwritten field but
    /// a row that is gone. Carries its own counterweight in the same act — the current version does delete
    /// the row — so this cannot be satisfied by refusing every delete that carries a precondition.
    /// </summary>
    [Fact]
    public async Task A_delete_whose_precondition_is_stale_is_refused_and_the_row_survives()
    {
        var world = await AuditedWorldAsync();
        var created = await world.Data.CreateAsync(Orders, Payload("first"), world.Caller);
        var stale = VersionOf(created);
        var advanced = await world.Data.UpdateAsync(Orders, IdOf(created), Payload("second"), world.Caller);
        VersionOf(advanced).ShouldNotBe(stale, "a write must advance the version, or nothing below discriminates");

        await Should.ThrowAsync<AlvoPreconditionFailedException>(() => world.Data.DeleteAsync(
            Orders, IdOf(created), world.Caller, new AlvoPrecondition(stale)));
        (await world.Data.GetAsync(Orders, IdOf(created), world.Caller)).ShouldNotBeNull();

        await world.Data.DeleteAsync(Orders, IdOf(created), world.Caller, new AlvoPrecondition(VersionOf(advanced)));
        (await world.Data.GetAsync(Orders, IdOf(created), world.Caller)).ShouldBeNull();
    }

    /// <summary>
    /// An entity with no <c>audit</c> has no version source at all, so it cannot answer "has this row
    /// changed since you read it". Refused — a silently ignored precondition is a lost update the caller
    /// believes it prevented, and they would have no way to find out. The message points at <c>audit: true</c>
    /// because that is the fix, and the ordinary update in the same act is the counterweight: this cannot be
    /// implemented as "refuse every update on a non-audited entity".
    /// </summary>
    [Fact]
    public async Task A_precondition_against_an_entity_with_no_version_column_is_refused_not_ignored()
    {
        var world = await UnauditedWorldAsync();
        var created = await world.Data.CreateAsync(Drafts, Payload("first"), world.Caller);

        var refusal = await Should.ThrowAsync<AlvoPreconditionFailedException>(() => world.Data.UpdateAsync(
            Drafts, IdOf(created), Payload("second"), world.Caller, new AlvoPrecondition(DateTimeOffset.UnixEpoch)));
        refusal.Message.ShouldContain("audit");

        var stored = await world.Data.GetAsync(Drafts, IdOf(created), world.Caller);
        stored.ShouldNotBeNull();
        stored!["title"].ShouldBe("first", "the refused write must not have landed");

        var updated = await world.Data.UpdateAsync(Drafts, IdOf(created), Payload("second"), world.Caller);
        updated["title"].ShouldBe("second");
    }

    /// <summary>
    /// The round trip, which is the whole reason a version is a stored value rather than a minted one:
    /// PostgreSQL's <c>timestamptz</c> keeps microseconds, SQLite keeps rendered text, and a .NET clock keeps
    /// 100-nanosecond ticks. Every precondition here is minted from the record the previous call
    /// <em>returned</em>, so an implementation that compares against anything other than the stored value —
    /// or a create that returns its candidate payload instead of re-reading the row — fails on the engine
    /// whose precision is coarsest, with no diagnosis available to the caller.
    /// </summary>
    [Fact]
    public async Task The_version_a_write_returns_is_the_one_a_following_precondition_accepts()
    {
        var world = await AuditedWorldAsync();
        var created = await world.Data.CreateAsync(Orders, Payload("first"), world.Caller);

        var updated = await world.Data.UpdateAsync(
            Orders, IdOf(created), Payload("second"), world.Caller, new AlvoPrecondition(VersionOf(created)));
        var again = await world.Data.UpdateAsync(
            Orders, IdOf(created), Payload("third"), world.Caller, new AlvoPrecondition(VersionOf(updated)));

        again["title"].ShouldBe("third");
    }

    /// <summary>
    /// Invisibility outranks the precondition. One stale version is used against two rows: the caller's own,
    /// which is visible, and another caller's, which their <c>USING</c> predicate excludes. The visible row
    /// answers <see cref="AlvoPreconditionFailedException"/> and the invisible one must still answer
    /// <see cref="AlvoRecordNotFoundException"/> — identically to a row that never existed. Ordered the other
    /// way round, "412 rather than 404" would confirm a row's existence to a caller who cannot read it, one
    /// request at a time; the pair of assertions is what makes the order observable at all.
    /// </summary>
    [Fact]
    public async Task A_stale_precondition_is_refused_before_the_policy_check_reveals_anything()
    {
        var world = await OwnedWorldAsync();
        var hers = await world.Data.CreateAsync(Tickets, OwnedPayload("hers", world.Alice), world.Alice);
        var his = await world.Data.CreateAsync(Tickets, OwnedPayload("his", world.Bob), world.Bob);
        var stale = VersionOf(hers);
        var advanced = await world.Data.UpdateAsync(Tickets, IdOf(hers), Payload("hers-again"), world.Alice);
        VersionOf(advanced).ShouldNotBe(stale, "a write must advance the version, or nothing below discriminates");

        await Should.ThrowAsync<AlvoPreconditionFailedException>(() => world.Data.UpdateAsync(
            Tickets, IdOf(hers), Payload("x"), world.Alice, new AlvoPrecondition(stale)));

        await Should.ThrowAsync<AlvoRecordNotFoundException>(() => world.Data.UpdateAsync(
            Tickets, IdOf(his), Payload("x"), world.Alice, new AlvoPrecondition(stale)));
        await Should.ThrowAsync<AlvoRecordNotFoundException>(() => world.Data.DeleteAsync(
            Tickets, IdOf(his), world.Alice, new AlvoPrecondition(stale)));
    }

    /// <summary>
    /// The replay itself: the same key and the same fingerprint answer with the row the first request
    /// created. The version is compared too, because a replay that quietly re-wrote the row would return the
    /// right id with a new version — and the caller's own <c>If-Match</c> would then be stale for a request
    /// they believe never happened twice.
    /// </summary>
    [Fact]
    public async Task Replaying_an_idempotency_key_with_the_same_fingerprint_returns_the_first_row()
    {
        var world = await AuditedWorldAsync();
        var token = new AlvoIdempotency(NewKey(), "fingerprint-of-the-first-request");

        var first = await world.Data.CreateAsync(Orders, Payload("first"), world.Caller, token);
        var replay = await world.Data.CreateAsync(Orders, Payload("first"), world.Caller, token);

        IdOf(replay).ShouldBe(IdOf(first));
        VersionOf(replay).ShouldBe(VersionOf(first), "a replay returns the stored row, it does not write again");
        replay["title"].ShouldBe("first");
    }

    /// <summary>
    /// The half a returned row cannot prove on its own: nothing new was written. An implementation that
    /// created a second row and happened to return the first one's id would satisfy the fact above and fail
    /// this one.
    /// </summary>
    [Fact]
    public async Task Replaying_an_idempotency_key_returns_the_row_and_creates_no_second_one()
    {
        var world = await AuditedWorldAsync();
        var token = new AlvoIdempotency(NewKey(), "fingerprint-of-the-first-request");

        await world.Data.CreateAsync(Orders, Payload("first"), world.Caller, token);
        await world.Data.CreateAsync(Orders, Payload("first"), world.Caller, token);

        var all = await world.Data.QueryAsync(new AlvoQuery { Entity = Orders }, world.Caller);
        all.Items.Count.ShouldBe(1);
    }

    /// <summary>
    /// A key reused for a <em>different</em> request is not a replay: answering with the first row would
    /// report success for a create that never happened and silently discard the second payload. Refused, and
    /// the second row is not created either — the only two answers that do not lose data.
    /// </summary>
    [Fact]
    public async Task The_same_idempotency_key_with_a_different_fingerprint_is_a_conflict()
    {
        var world = await AuditedWorldAsync();
        var key = NewKey();

        await world.Data.CreateAsync(
            Orders, Payload("first"), world.Caller, new AlvoIdempotency(key, "fingerprint-of-the-first"));

        await Should.ThrowAsync<AlvoIdempotencyConflictException>(() => world.Data.CreateAsync(
            Orders, Payload("second"), world.Caller, new AlvoIdempotency(key, "fingerprint-of-the-second")));

        var all = await world.Data.QueryAsync(new AlvoQuery { Entity = Orders }, world.Caller);
        all.Items.Count.ShouldBe(1);
    }

    /// <summary>
    /// The case the whole mechanism exists for — a client that retried because the first response never
    /// arrived, so both requests are in flight at once. Both calls are started before either is awaited, so
    /// on any backend that awaits its I/O they genuinely overlap; a check-then-insert with no unique
    /// constraint behind it lets both pass the check and creates two rows. Both callers must also come back
    /// with the <em>same</em> row: the loser is translated into a replay, never into a raw provider
    /// exception the caller has no contract for.
    /// </summary>
    [Fact]
    public async Task Two_concurrent_creates_with_one_idempotency_key_produce_exactly_one_row()
    {
        var world = await AuditedWorldAsync();
        var token = new AlvoIdempotency(NewKey(), "fingerprint-of-the-retried-request");

        var first = world.Data.CreateAsync(Orders, Payload("first"), world.Caller, token);
        var second = world.Data.CreateAsync(Orders, Payload("first"), world.Caller, token);
        var both = await Task.WhenAll(first, second);

        IdOf(both[1]).ShouldBe(IdOf(both[0]), "the loser of the race must be answered with the winner's row");
        var all = await world.Data.QueryAsync(new AlvoQuery { Entity = Orders }, world.Caller);
        all.Items.Count.ShouldBe(1);
    }

    /// <summary>
    /// A key is the caller's own string, so two tenants will collide on <c>"1"</c> sooner rather than later.
    /// In a shared key space the second tenant's replay would be answered with the first tenant's row id — a
    /// cross-tenant read through the one channel that is meant to be a safe retry. Each tenant therefore
    /// gets its own row, and each sees exactly one.
    /// </summary>
    [Fact]
    public async Task An_idempotency_key_is_scoped_to_its_tenant_so_one_tenant_cannot_replay_anothers()
    {
        var world = await TenantedWorldAsync();
        var token = new AlvoIdempotency("1", "fingerprint-both-tenants-happen-to-share");

        var acme = await world.Data.CreateAsync(Invoices, TenantPayload("acme", world.Acme), world.AcmeCaller, token);
        var globex = await world.Data.CreateAsync(
            Invoices, TenantPayload("globex", world.Globex), world.GlobexCaller, token);

        IdOf(globex).ShouldNotBe(IdOf(acme), "a shared key space would answer one tenant with another's row");
        globex["title"].ShouldBe("globex");
        (await world.Data.QueryAsync(new AlvoQuery { Entity = Invoices }, world.AcmeCaller)).Items.Count.ShouldBe(1);
        (await world.Data.QueryAsync(new AlvoQuery { Entity = Invoices }, world.GlobexCaller)).Items.Count.ShouldBe(1);
    }

    private const string Orders = "orders";
    private const string Tickets = "tickets";
    private const string Drafts = "drafts";
    private const string Invoices = "invoices";

    /// <summary>A key no other fact can collide with, so facts stay independent even on a shared store.</summary>
    private static string NewKey() => $"key-{Guid.NewGuid():N}";

    private static Dictionary<string, object?> Payload(string title) =>
        new(StringComparer.Ordinal) { ["title"] = title };

    private static Dictionary<string, object?> OwnedPayload(string title, AlvoContext owner) =>
        new(StringComparer.Ordinal) { ["title"] = title, ["owner_id"] = owner.User.Value };

    private static Dictionary<string, object?> TenantPayload(string title, TenantId tenant) =>
        new(StringComparer.Ordinal) { ["title"] = title, ["tenant_id"] = tenant.Value };

    private static Guid IdOf(AlvoRecord record) => (Guid)record[AlvoManagedColumns.Id]!;

    /// <summary>
    /// The row's version as this port returned it, read from the record rather than reconstructed — which is
    /// the point of every round-trip assertion above.
    /// </summary>
    private static DateTimeOffset VersionOf(AlvoRecord record) =>
        (DateTimeOffset)record[AlvoManagedColumns.UpdatedAt]!;

    /// <summary>An audited, global <c>orders</c> entity every operation is permitted on.</summary>
    private Task<World> AuditedWorldAsync() => WorldAsync(Orders, audit: true, EntityTenancy.Global, PermissiveRules);

    /// <summary>A non-audited <c>drafts</c> entity — the one with no version column at all.</summary>
    private Task<World> UnauditedWorldAsync() => WorldAsync(Drafts, audit: false, EntityTenancy.Global, PermissiveRules);

    /// <summary>
    /// An audited <c>tickets</c> entity row-scoped by owner, so one caller's row is genuinely invisible to
    /// the other — which is what lets the check-order fact tell 404 from 412.
    /// </summary>
    private Task<World> OwnedWorldAsync() => WorldAsync(
        Tickets,
        audit: true,
        EntityTenancy.Global,
        new AccessRules
        {
            List = OwnerRule,
            Get = OwnerRule,
            Create = OwnerRule,
            Update = OwnerRule,
            Delete = OwnerRule,
        },
        _ownerField);

    /// <summary>An audited, tenant-scoped <c>invoices</c> entity, plus a caller in each of two tenants.</summary>
    private Task<World> TenantedWorldAsync() =>
        WorldAsync(Invoices, audit: true, EntityTenancy.Scoped, PermissiveRules);

    private const string OwnerRule = "owner_id == @user.id";

    private static AccessRules PermissiveRules => new()
    {
        List = "true",
        Get = "true",
        Create = "true",
        Update = "true",
        Delete = "true",
    };

    private static readonly (string Name, DescField Type) _ownerField = ("owner_id", DescField.Uuid);

    /// <summary>
    /// One entity, its descriptor and its schema paired by hand — the schema mapper that injects the managed
    /// columns is <see langword="internal"/> to the core, so this suite pairs them exactly as the adversarial
    /// suite does, and the audit quartet is listed explicitly because a real backend has to create those
    /// columns before a version can exist.
    /// </summary>
    /// <param name="entity">The entity name.</param>
    /// <param name="audit">Whether the entity declares <c>audit</c>, and therefore has a version column.</param>
    /// <param name="tenancy">The entity's tenancy.</param>
    /// <param name="rules">The access rules to compile.</param>
    /// <param name="extra">An additional required field, for the owner-scoped fixture.</param>
    private async Task<World> WorldAsync(
        string entity,
        bool audit,
        EntityTenancy tenancy,
        AccessRules rules,
        (string Name, DescField Type)? extra = null)
    {
        var fields = new Dictionary<string, FieldDescriptor>(StringComparer.Ordinal)
        {
            ["title"] = new() { Type = DescField.String },
        };
        if (extra is { } field)
        {
            fields[field.Name] = new FieldDescriptor { Type = field.Type, Required = true };
        }

        var descriptor = new AlvoDescriptor
        {
            ApiVersion = "alvo.dev/v1",
            Name = "concurrency-fixture",
            Entities = new Dictionary<string, EntityDescriptor>(StringComparer.Ordinal)
            {
                [entity] = new EntityDescriptor { Tenancy = tenancy, Audit = audit, Fields = fields, Rules = rules },
            },
        };

        var data = await CreateAsync(
            new SchemaModel([SchemaOf(entity, audit, tenancy, extra)]),
            descriptor,
            new Dictionary<string, IReadOnlyList<AlvoRecord>>(StringComparer.Ordinal));

        return new World(data);
    }

    private static EntitySchema SchemaOf(
        string entity, bool audit, EntityTenancy tenancy, (string Name, DescField Type)? extra)
    {
        List<FieldSchema> fields =
        [
            new FieldSchema { Name = AlvoManagedColumns.Id, Type = SchemaField.Uuid, Required = true },
            new FieldSchema { Name = "title", Type = SchemaField.String, Nullable = true },
        ];
        if (extra is { } field)
        {
            fields.Add(new FieldSchema
            {
                Name = field.Name,
                Type = Enum.Parse<SchemaField>(field.Type.ToString()),
                Required = true,
            });
        }

        if (tenancy == EntityTenancy.Scoped)
        {
            fields.Add(new FieldSchema
            {
                Name = AlvoManagedColumns.TenantId,
                Type = SchemaField.Uuid,
                Required = true,
                Indexed = true,
            });
        }

        if (audit)
        {
            fields.AddRange(AuditFields);
        }

        return new EntitySchema
        {
            Name = entity,
            Tenancy = tenancy == EntityTenancy.Scoped ? TenancyMode.Scoped : TenancyMode.Global,
            Audit = audit,
            Fields = fields,
        };
    }

    /// <summary>
    /// The audit quartet as the schema mapper injects it. <c>updated_at</c> is <c>required</c> — the version
    /// column a precondition compares can never be absent on a row the framework wrote.
    /// </summary>
    private static IEnumerable<FieldSchema> AuditFields =>
    [
        new FieldSchema { Name = AlvoManagedColumns.CreatedAt, Type = SchemaField.DateTime, Required = true },
        new FieldSchema { Name = AlvoManagedColumns.CreatedBy, Type = SchemaField.Uuid, Nullable = true },
        new FieldSchema { Name = AlvoManagedColumns.UpdatedAt, Type = SchemaField.DateTime, Required = true },
        new FieldSchema { Name = AlvoManagedColumns.UpdatedBy, Type = SchemaField.Uuid, Nullable = true },
    ];

    /// <summary>One fixture database plus the callers and tenants the facts above write as.</summary>
    private sealed class World(IAlvoData data)
    {
        internal IAlvoData Data { get; } = data;

        /// <summary>The single caller the global fixtures write as.</summary>
        internal AlvoContext Caller { get; } = NewCaller(tenant: null);

        internal AlvoContext Alice { get; } = NewCaller(tenant: null);

        internal AlvoContext Bob { get; } = NewCaller(tenant: null);

        internal TenantId Acme { get; } = TenantId.New();

        internal TenantId Globex { get; } = TenantId.New();

        internal AlvoContext AcmeCaller => _acmeCaller ??= NewCaller(Acme);

        internal AlvoContext GlobexCaller => _globexCaller ??= NewCaller(Globex);

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
