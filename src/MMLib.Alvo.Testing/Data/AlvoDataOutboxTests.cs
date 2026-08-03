using MMLib.Alvo.Data;
using MMLib.Alvo.Descriptor;
using MMLib.Alvo.Events;
using MMLib.Alvo.Schema;
using Shouldly;
using Xunit;
using DescField = MMLib.Alvo.Descriptor.FieldType;
using SchemaField = MMLib.Alvo.Schema.FieldType;

namespace MMLib.Alvo.Testing.Data;

/// <summary>
/// The outbox guarantee at the port's own write faces: <b>no change without an event, and no event without a
/// change</b> — over a real engine, on every implementation that queues events at all.
/// </summary>
/// <remarks>
/// <para>
/// <b>Update and delete come first in this file on purpose.</b> The idiomatic EF place to hang an outbox is a
/// <c>SaveChangesInterceptor</c>, and on this data path it fires for <em>neither</em>: writes run as
/// <c>ExecuteUpdate</c>/<c>ExecuteDelete</c> over the policy-carrying root, which never touches the change
/// tracker (<c>docs/architecture/data-path.md</c>). A create-only suite would pass over exactly that mistake,
/// on the two operations that most need an event.
/// </para>
/// <para>
/// <b>Its own suite, not facts added to <see cref="AlvoDataAdversarialTests"/></b>, for the reason
/// <see cref="AlvoDataConstraintTests"/> gives: that suite is inherited by the in-memory reference too, which
/// has no store to queue anything in, so a fact placed there would be vacuous for it. Both shipped relational
/// drivers inherit this one unchanged.
/// </para>
/// <para>
/// <b>The subclass supplies a store and nothing else.</b> The entity, the rules, the payloads and every
/// assertion live here, so a fact cannot be weakened to make a provider pass — the same arrangement every
/// other inherited data-path suite uses.
/// </para>
/// </remarks>
public abstract class AlvoDataOutboxTests
{
    /// <summary>
    /// Builds a fresh store over <paramref name="descriptor"/>/<paramref name="schema"/>, together with the
    /// read side of its outbox. Nothing is seeded: every fact here writes its rows through the port, because
    /// what is being measured is what a <em>write</em> queues.
    /// </summary>
    /// <param name="schema">The schema every entity in <paramref name="descriptor"/> maps to.</param>
    /// <param name="descriptor">The project descriptor whose rules and field flags apply.</param>
    protected abstract Task<IAlvoDataOutboxWorld> WorldAsync(SchemaModel schema, AlvoDescriptor descriptor);

    /// <summary>
    /// An update queues one event carrying both images and naming only the fields that moved.
    /// </summary>
    /// <remarks>
    /// The first of the two an interceptor never sees: this write is an <c>ExecuteUpdate</c>, so nothing goes
    /// through the change tracker. <c>make</c> is asserted <em>absent</em> from the changed list because a
    /// list that simply named every field would satisfy "contains color" and tell a subscriber nothing.
    /// </remarks>
    [Fact]
    public async Task An_update_emits_exactly_one_event_carrying_both_images()
    {
        var world = await VehiclesWorldAsync();
        var created = await CreateVehicleAsync(world, make: "vw");

        await world.Data.UpdateAsync(
            Vehicles, IdOf(created), Patch(("color", "blue")), Caller, cancellationToken: Ct);

        var events = await world.EventsAsync();
        events.Select(queued => queued.Type).ShouldBe([Created, Updated]);
        var updated = events[^1];
        updated.Data.OldRecord.ShouldNotBeNull();
        updated.Data.OldRecord!["color"].ShouldBe("red");
        updated.Data.Record.ShouldNotBeNull();
        updated.Data.Record!["color"].ShouldBe("blue");
        updated.Data.Changed.ShouldContain("color");
        updated.Data.Changed.ShouldNotContain("make", "a changed list naming every field tells a hook nothing");
        updated.Subject.ShouldBe($"{Vehicles}/{IdOf(created)}");
    }

    /// <summary>
    /// A delete queues one event carrying the pre-image, and no record at all.
    /// </summary>
    /// <remarks>
    /// The second of the two an interceptor never sees: this write is an <c>ExecuteDelete</c>. The row is gone
    /// afterwards, so the event is the only thing that still knows what it held — which is why the delete path
    /// reads an unmasked pre-image it needs for nothing else.
    /// </remarks>
    [Fact]
    public async Task A_delete_emits_exactly_one_event_carrying_the_pre_image()
    {
        var world = await VehiclesWorldAsync();
        var created = await CreateVehicleAsync(world, make: "vw");

        await world.Data.DeleteAsync(Vehicles, IdOf(created), Caller, cancellationToken: Ct);

        var events = await world.EventsAsync();
        events.Select(queued => queued.Type).ShouldBe([Created, Deleted]);
        var deleted = events[^1];
        deleted.Data.OldRecord.ShouldNotBeNull();
        deleted.Data.OldRecord!["make"].ShouldBe("vw");
        deleted.Data.Record.ShouldBeNull("there is no post-image of a row that no longer exists");
        deleted.Data.Changed.ShouldContain("make", "every field of a deleted row moved");
    }

    /// <summary>A create queues one event, carrying the stored row and no old record.</summary>
    [Fact]
    public async Task A_create_emits_exactly_one_event()
    {
        var world = await VehiclesWorldAsync();

        var created = await CreateVehicleAsync(world, make: "vw");

        var queued = (await world.EventsAsync()).ShouldHaveSingleItem();
        queued.Type.ShouldBe(Created);
        queued.Data.OldRecord.ShouldBeNull("changed() is false on a create because there is nothing to compare");
        queued.Data.Record!["make"].ShouldBe("vw");
        queued.PartitionKey.ShouldBe($"{Vehicles}:{IdOf(created)}");
    }

    /// <summary>
    /// A write the engine itself refuses queues nothing — the event and the row it describes are one act.
    /// </summary>
    /// <remarks>
    /// Forced through a duplicate on the entity's own unique index, so the failure is the production path's
    /// rather than a rollback of a transaction the production code never opened. It discriminates against every
    /// implementation that emits <em>before</em> the write it describes has succeeded; the case where the
    /// engine's refusal lands after the outbox insert is
    /// <see cref="Two_concurrent_idempotent_creates_on_one_key_emit_exactly_one_event"/>, because on this path
    /// nothing can fail once the write's re-read has returned.
    /// </remarks>
    [Fact]
    public async Task A_write_the_engine_refuses_leaves_no_outbox_row()
    {
        var world = await VehiclesWorldAsync();
        await CreateVehicleAsync(world, make: "vw", vin: "TAKEN");

        await Should.ThrowAsync<AlvoConstraintViolationException>(
            () => CreateVehicleAsync(world, make: "audi", vin: "TAKEN"));

        (await world.EventsAsync()).ShouldHaveSingleItem().Type.ShouldBe(Created);
    }

    /// <summary>
    /// The atomicity claim itself, on a production path: the row and its event commit together or not at all.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Two clients race one idempotency key. The loser's transaction has already queued its event when the
    /// record's primary key refuses its write, so it rolls the pair back and its retry answers as a replay —
    /// one row, one event. An outbox insert on any connection but the write's own would leave the loser's event
    /// behind and produce two.
    /// </para>
    /// <para>
    /// Both calls are started before either is awaited, so they are genuinely in flight together, exactly as
    /// <c>AlvoDataConcurrencyTests.Two_concurrent_creates_with_one_idempotency_key_produce_exactly_one_row</c>
    /// starts them — this is that fact's outbox half.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task Two_concurrent_idempotent_creates_on_one_key_emit_exactly_one_event()
    {
        var world = await VehiclesWorldAsync();
        var token = new AlvoIdempotency("k-concurrent", $"{Vehicles}:one-request-digest");

        var first = CreateVehicleAsync(world, make: "vw", idempotency: token);
        var second = CreateVehicleAsync(world, make: "vw", idempotency: token);
        var both = await Task.WhenAll(first, second);

        IdOf(both[0]).ShouldBe(IdOf(both[1]), "one key is one row, however the race went");
        (await world.EventsAsync()).ShouldHaveSingleItem().Type.ShouldBe(Created);
    }

    /// <summary>A write policy refuses queues nothing, because nothing was written for it to describe.</summary>
    /// <remarks>
    /// One allowed write goes first, so "nothing" is measured against a queue that exists and is not empty —
    /// an implementation that emitted for a refused write would leave two events here, and one that never
    /// queued anything at all would leave none.
    /// </remarks>
    [Fact]
    public async Task A_denied_write_emits_nothing()
    {
        var world = await VehiclesWorldAsync();
        await CreateVehicleAsync(world, make: "vw");

        await Should.ThrowAsync<AlvoAuthorizationException>(() => world.Data.CreateAsync(
            Vehicles, Payload("audi", vin: null), AlvoContext.Anonymous, cancellationToken: Ct));

        (await world.EventsAsync()).ShouldHaveSingleItem().Data.Record!["make"].ShouldBe("vw");
    }

    /// <summary>
    /// A replayed idempotent create wrote no row, so it queues no second event — or every client retry would
    /// fan out once more through every subscription.
    /// </summary>
    [Fact]
    public async Task A_replayed_idempotent_create_emits_no_second_event()
    {
        var world = await VehiclesWorldAsync();
        var token = new AlvoIdempotency("k-1", $"{Vehicles}:one-request-digest");

        var first = await CreateVehicleAsync(world, make: "vw", idempotency: token);
        var replayed = await CreateVehicleAsync(world, make: "vw", idempotency: token);

        IdOf(replayed).ShouldBe(IdOf(first), "a replay answers with the first row rather than creating a second");
        (await world.EventsAsync()).ShouldHaveSingleItem();
    }

    /// <summary>
    /// The event's <c>time</c> is the instant the row's own audit stamp recorded — one write, one instant
    /// (<c>docs/architecture/data-path.md</c>, <em>Every timestamp is one instant</em>).
    /// </summary>
    /// <remarks>
    /// It is asserted on both write faces that stamp, because each site reads the clock for itself and a site
    /// that read it twice would be off by however long its own I/O took.
    /// </remarks>
    [Fact]
    public async Task The_events_time_equals_the_rows_own_audit_instant()
    {
        var world = await VehiclesWorldAsync();

        var created = await CreateVehicleAsync(world, make: "vw");
        var updated = await world.Data.UpdateAsync(
            Vehicles, IdOf(created), Patch(("color", "blue")), Caller, cancellationToken: Ct);

        var events = await world.EventsAsync();
        events[0].Time.ShouldBe(InstantOf(created, AlvoManagedColumns.CreatedAt));
        events[^1].Time.ShouldBe(InstantOf(updated, AlvoManagedColumns.UpdatedAt));
    }

    /// <summary>
    /// The record on the envelope is <b>unmasked</b>: a <c>hidden</c> field is present in it, with its real
    /// value, while the caller's own response still does not carry the field at all.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This is decision D7, pinned rather than implied. An after-hook condition reading
    /// <c>old.secret_note</c> or <c>changed(secret_note)</c> has to see every field, and <c>hidden</c> is a
    /// per-caller <em>read</em> mask rather than a data classification. The changed list is asserted too,
    /// because a masked post-image fails in a second and worse way: every masked field would compare unequal
    /// to its own stored value and be reported as moved on every update.
    /// </para>
    /// <para>
    /// The disclosure this accepts is real and is on the record: a webhook or an email delivers hidden fields
    /// to the endpoint the descriptor declares. It is accepted because that endpoint is declared by the same
    /// author as the <c>hidden</c> rule and is never caller-supplied. Per-endpoint field projection is tracked
    /// as issue #152.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task An_events_record_carries_a_hidden_field_unmasked_and_that_is_the_documented_disclosure()
    {
        var world = await VehiclesWorldAsync();
        var created = await CreateVehicleAsync(world, make: "vw");
        created.Values.ShouldNotContainKey("secret_note", "the caller's own response still applies the mask");

        var updated = await world.Data.UpdateAsync(
            Vehicles, IdOf(created), Patch(("color", "blue")), Caller, cancellationToken: Ct);

        updated.Values.ShouldNotContainKey("secret_note");
        var events = await world.EventsAsync();
        events[0].Data.Record!["secret_note"].ShouldBe(SecretNote);
        events[^1].Data.Record!["secret_note"].ShouldBe(SecretNote);
        events[^1].Data.OldRecord!["secret_note"].ShouldBe(SecretNote);
        events[^1].Data.Changed.ShouldNotContain(
            "secret_note", "a masked post-image would report every hidden field as moved on every update");
    }

    /// <summary>
    /// Every event a data change produces reads as the framework's own, with the caller behind it identified.
    /// </summary>
    /// <remarks>
    /// <see cref="AlvoEvent.AuthType"/> answers authentication rather than authorization, because §3.3 needs
    /// "as the system" and "as the originator" to be distinguishable off the envelope and a role says neither.
    /// </remarks>
    [Fact]
    public async Task Every_event_carries_the_caller_the_write_was_performed_as()
    {
        var world = await VehiclesWorldAsync();

        await CreateVehicleAsync(world, make: "vw");

        var queued = (await world.EventsAsync()).ShouldHaveSingleItem();
        queued.Source.ShouldBe(AlvoEvent.DefaultSource);
        queued.AuthType.ShouldBe(AlvoEventAuthType.ApiKey);
        queued.AuthId.ShouldBe(Caller.User.Value.ToString());
        queued.CorrelationId.ShouldNotBeNullOrWhiteSpace();
    }

    private const string Vehicles = "vehicles";

    private const string Created = $"entity.{Vehicles}.created";

    private const string Updated = $"entity.{Vehicles}.updated";

    private const string Deleted = $"entity.{Vehicles}.deleted";

    private const string SecretNote = "commission is 12%";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private Task<IAlvoDataOutboxWorld> VehiclesWorldAsync() => WorldAsync(Schema, Descriptor);

    private static Task<AlvoRecord> CreateVehicleAsync(
        IAlvoDataOutboxWorld world, string make, string? vin = null, AlvoIdempotency? idempotency = null) =>
        world.Data.CreateAsync(Vehicles, Payload(make, vin), Caller, idempotency, Ct);

    /// <summary>
    /// The payload every create sends: a colour to move on the update, and a <c>hidden</c> field with a real
    /// value, so the unmasked-record fact has something to find.
    /// </summary>
    private static Dictionary<string, object?> Payload(string make, string? vin) =>
        new(StringComparer.Ordinal)
        {
            ["make"] = make,
            ["color"] = "red",
            ["vin"] = vin,
            ["secret_note"] = SecretNote,
        };

    private static Dictionary<string, object?> Patch(params (string Field, object? Value)[] fields) =>
        fields.ToDictionary(field => field.Field, field => field.Value, StringComparer.Ordinal);

    private static Guid IdOf(AlvoRecord row) => (Guid)row[AlvoManagedColumns.Id]!;

    private static DateTimeOffset InstantOf(AlvoRecord row, string column) => (DateTimeOffset)row[column]!;

    /// <summary>
    /// The caller every write here is performed as: authenticated, so the anonymous refusal above is about
    /// the credential rather than about the rule.
    /// </summary>
    private static AlvoContext Caller { get; } = new()
    {
        User = new UserId(Guid.Parse("bbbbbbbb-0000-0000-0000-000000000001")),
        Roles = new HashSet<Role> { Role.Authenticated },
    };

    /// <summary>
    /// One entity with everything a fact here needs: <c>audit</c> (so an event's <c>time</c> has a stamp to
    /// equal), a <c>unique</c> field (so a write can be refused by the engine after policy has allowed it), a
    /// <c>hidden</c> field (so the unmasked record is observable) and a rule no anonymous caller satisfies.
    /// </summary>
    private static AlvoDescriptor Descriptor => new()
    {
        ApiVersion = "alvo.dev/v1",
        Name = "outbox-suite",
        Entities = new Dictionary<string, EntityDescriptor>(StringComparer.Ordinal)
        {
            [Vehicles] = new()
            {
                Tenancy = EntityTenancy.Global,
                Audit = true,
                Fields = new Dictionary<string, FieldDescriptor>(StringComparer.Ordinal)
                {
                    ["make"] = new() { Type = DescField.String },
                    ["color"] = new() { Type = DescField.String },
                    ["vin"] = new() { Type = DescField.String, Unique = true },
                    ["secret_note"] = new() { Type = DescField.String, Hidden = BoolOrCel.FromBoolean(true) },
                },
                Rules = new AccessRules
                {
                    List = AuthenticatedOnly,
                    Get = AuthenticatedOnly,
                    Create = AuthenticatedOnly,
                    Update = AuthenticatedOnly,
                    Delete = AuthenticatedOnly,
                },
            },
        },
    };

    private const string AuthenticatedOnly = "'authenticated' in @user.roles";

    /// <summary>
    /// The applied schema the descriptor above maps to, paired by hand for the reason
    /// <see cref="AlvoDataConstraintTests"/> gives: the core's mapper is <see langword="internal"/> and
    /// unreachable from this project. <see cref="AlvoManagedColumns"/> stays the authority for <em>which</em>
    /// columns the framework manages; only each column's shape is restated.
    /// </summary>
    private static SchemaModel Schema => new([
        new EntitySchema
        {
            Name = Vehicles,
            Tenancy = TenancyMode.Global,
            Audit = true,
            Fields =
            [
                new FieldSchema { Name = AlvoManagedColumns.Id, Type = SchemaField.Uuid, Required = true },
                new FieldSchema { Name = "make", Type = SchemaField.String, Nullable = true },
                new FieldSchema { Name = "color", Type = SchemaField.String, Nullable = true },
                new FieldSchema
                {
                    Name = "vin", Type = SchemaField.String, Nullable = true, MaxLength = 40, Unique = true,
                },
                new FieldSchema { Name = "secret_note", Type = SchemaField.String, Nullable = true },

                // Last, exactly as the core's mapper appends its managed columns.
                new FieldSchema { Name = AlvoManagedColumns.CreatedAt, Type = SchemaField.DateTime, Required = true },
                new FieldSchema { Name = AlvoManagedColumns.CreatedBy, Type = SchemaField.Uuid, Nullable = true },
                new FieldSchema { Name = AlvoManagedColumns.UpdatedAt, Type = SchemaField.DateTime, Required = true },
                new FieldSchema { Name = AlvoManagedColumns.UpdatedBy, Type = SchemaField.Uuid, Nullable = true },
            ],
        },
    ]);
}
