using MMLib.Alvo.Data.EntityFrameworkCore.Internal;
using MMLib.Alvo.Events;
using MMLib.Alvo.Schema;
using MMLib.Alvo.Testing.Data;

using System.Text.RegularExpressions;

namespace MMLib.Alvo.Data.EntityFrameworkCore.Tests;

/// <summary>
/// The envelope one write produces, with no database in sight: the event type's grammar, the partition key, the
/// changed-field list, which image each operation carries, and who the event says acted.
/// </summary>
public partial class OutboxEventFactoryTests
{
    /// <summary>
    /// Every type this factory can produce must be a type a rule could subscribe to — the descriptor's own
    /// <c>$defs/eventPattern</c> grammar. A type no pattern can name is a type no hook can ever fire on.
    /// </summary>
    /// <remarks>
    /// One fact over all three faces rather than a <c>[Theory]</c>: <c>OutboxOperation</c> is
    /// <see langword="internal"/> to the driver, so it cannot appear in a public test method's signature.
    /// </remarks>
    [Fact]
    public void The_event_type_matches_the_frozen_event_pattern_grammar()
    {
        foreach (var (operation, expected) in _typePerOperation)
        {
            var @event = Subject(operation);

            @event.Type.ShouldBe(expected);
            EventPatternRegex().IsMatch(@event.Type).ShouldBeTrue(@event.Type);
        }
    }

    private static readonly (OutboxOperation Operation, string Type)[] _typePerOperation =
    [
        (OutboxOperation.Created, "entity.vehicles.created"),
        (OutboxOperation.Updated, "entity.vehicles.updated"),
        (OutboxOperation.Deleted, "entity.vehicles.deleted"),
    ];

    /// <summary>
    /// The partition key carries the entity, so two entities that happen to hold one row id are two partitions.
    /// </summary>
    [Fact]
    public void The_partition_key_carries_the_entity_so_two_entities_cannot_collide_on_one_row_id()
    {
        var rowId = Guid.CreateVersion7();

        OutboxEventFactory.PartitionKeyFor("vehicles", rowId)
            .ShouldNotBe(OutboxEventFactory.PartitionKeyFor("owners", rowId));
    }

    /// <summary>
    /// The subject and the partition key both name the row, and both come from the image rather than from a
    /// second read.
    /// </summary>
    [Fact]
    public void The_subject_and_the_partition_key_name_the_row_the_write_touched()
    {
        var @event = Subject(OutboxOperation.Updated);

        @event.Subject.ShouldBe($"vehicles/{RowId}");
        @event.PartitionKey.ShouldBe(OutboxEventFactory.PartitionKeyFor("vehicles", RowId));
    }

    /// <summary>
    /// <c>changed(field)</c> has to be cheap for the dispatcher, which is why the payload carries the list —
    /// and it has to be true, which is why only the fields that really moved are in it.
    /// </summary>
    [Fact]
    public void Changed_names_only_the_fields_whose_value_really_moved()
    {
        var before = Record(("make", "vw"), ("color", "red"));
        var after = Record(("make", "vw"), ("color", "blue"));

        OutboxEventFactory.ChangedFields(after, before).ShouldBe(["color"]);
    }

    /// <summary>
    /// A field one image has and the other does not counts as moved — a column added by a migration between
    /// the two reads, or a value that became null, is a change and not a match.
    /// </summary>
    [Fact]
    public void Changed_names_a_field_only_one_image_carries()
    {
        var before = Record(("make", "vw"));
        var after = Record(("make", "vw"), ("color", "blue"));

        OutboxEventFactory.ChangedFields(after, before).ShouldBe(["color"]);
    }

    /// <summary>
    /// The list is ordered ordinally, so one write produces one payload byte for byte whatever order the
    /// engine returned the columns in.
    /// </summary>
    [Fact]
    public void Changed_is_ordered_so_one_write_produces_one_payload()
    {
        var before = Record(("make", "vw"), ("color", "red"), ("plate", "A"));
        var after = Record(("make", "audi"), ("color", "blue"), ("plate", "B"));

        OutboxEventFactory.ChangedFields(after, before).ShouldBe(["color", "make", "plate"]);
    }

    /// <summary>A create has no pre-image, and every field of a new row has moved.</summary>
    [Fact]
    public void A_create_carries_no_old_record_and_names_every_field_as_changed()
    {
        var @event = Subject(OutboxOperation.Created);

        @event.Data.OldRecord.ShouldBeNull();
        @event.Data.Changed.ShouldBe(@event.Data.Record!.Values.Keys, ignoreOrder: true);
    }

    /// <summary>A delete carries the pre-image and no record: there is no post-image of a row that is gone.</summary>
    [Fact]
    public void A_delete_carries_the_pre_image_and_no_record()
    {
        var @event = Subject(OutboxOperation.Deleted);

        @event.Data.Record.ShouldBeNull();
        @event.Data.OldRecord.ShouldNotBeNull();
        @event.Data.Changed.ShouldBe(PreImage.Values.Keys, ignoreOrder: true);
    }

    /// <summary>
    /// The auth type distinguishes the framework from the originator, which is what §3.3's "as system / as the
    /// originator" needs off the envelope. It is authentication, never a role.
    /// </summary>
    /// <param name="context">The caller the write ran as.</param>
    /// <param name="expected">The <see cref="AlvoEventAuthType"/> value it reports.</param>
    [Theory]
    [MemberData(nameof(Callers))]
    public void The_auth_type_distinguishes_the_system_from_the_originator(AlvoContext context, string expected)
        => OutboxEventFactory.For(Vehicles, OutboxOperation.Created, context, Now, PostImage, null)
            .AuthType.ShouldBe(expected);

    public static TheoryData<AlvoContext, string> Callers => new()
    {
        { AlvoContext.Anonymous, AlvoEventAuthType.Unauthenticated },
        { AlvoContext.System(tenant: null), AlvoEventAuthType.System },
        { Caller, AlvoEventAuthType.ApiKey },
        { Caller with { ActorKind = AlvoActorKind.User }, AlvoEventAuthType.User },
    };

    /// <summary>
    /// <c>tenantid</c> is the write's tenant: the caller's on a tenant-scoped entity, and absent on a global row or a
    /// tenantless system write, where there is no tenant to report.
    /// </summary>
    [Fact]
    public void A_scoped_write_carries_the_callers_tenant_and_a_global_one_carries_none()
    {
        var tenant = TenantId.New();
        var caller = Caller with { Tenant = tenant };

        OutboxEventFactory.For(Vehicles with { Tenancy = TenancyMode.Scoped }, OutboxOperation.Created, caller, Now, PostImage, null)
            .TenantId.ShouldBe(tenant.ToString());
        OutboxEventFactory.For(Vehicles, OutboxOperation.Created, caller, Now, PostImage, null).TenantId.ShouldBeNull();
        OutboxEventFactory.For(Vehicles with { Tenancy = TenancyMode.Scoped }, OutboxOperation.Created, AlvoContext.System(tenant: null), Now, PostImage, null)
            .TenantId.ShouldBeNull("a tenantless system write has no tenant to report");
    }

    /// <summary>
    /// <c>events.exclude</c> takes a field out of both images, but <c>changed</c> is computed from the full images,
    /// so it still says the field moved.
    /// </summary>
    [Fact]
    public void Excluded_fields_leave_both_images_and_stay_named_in_changed()
    {
        var @event = OutboxEventFactory.For(
            Vehicles with { EventImageExclusions = ["color"] }, OutboxOperation.Updated, Caller, Now, PostImage, PreImage);

        @event.Data.Record!.Values.ShouldNotContainKey("color");
        @event.Data.OldRecord!.Values.ShouldNotContainKey("color");
        @event.Data.Record!["make"].ShouldBe("vw");
        @event.Data.Changed.ShouldContain("color");
    }

    /// <summary>The caller's stated provenance — depth, correlation and causation — is what the envelope reports.</summary>
    [Fact]
    public void The_effective_provenance_is_stamped_on_the_event()
    {
        var caller = Caller with { Provenance = new AlvoProvenance { Depth = 3, CorrelationId = "flow-1", CausationId = "cause-1" } };

        var @event = OutboxEventFactory.For(Vehicles, OutboxOperation.Created, caller, Now, PostImage, null);

        (@event.ChainDepth, @event.CorrelationId, @event.CausationId).ShouldBe((3, "flow-1", "cause-1"));
    }

    /// <summary>A write inside a deeper framework flow reports that flow's depth, never the shallower caller's.</summary>
    [Fact]
    public void An_ambient_flow_deeper_than_the_caller_sets_the_events_depth()
    {
        using var _ = AlvoFlow.Enter(new AlvoProvenance { Depth = 4, CausationId = "event-7" });

        var @event = OutboxEventFactory.For(Vehicles, OutboxOperation.Created, Caller, Now, PostImage, null);

        (@event.ChainDepth, @event.CausationId).ShouldBe((4, "event-7"));
    }

    /// <summary>The caller's roles travel as <c>authclaims</c>, for after-hook conditions.</summary>
    [Fact]
    public void The_callers_roles_travel_as_auth_claims()
        => OutboxEventFactory.For(Vehicles, OutboxOperation.Created, Caller, Now, PostImage, null)
            .AuthClaims.ShouldBe(AlvoEventProvenance.AuthClaimsOf(Caller));

    /// <summary>With no stated trace, the ambient W3C activity is the envelope's trace context and its correlation.</summary>
    [Fact]
    public void The_ambient_w3c_activity_is_the_events_trace_context()
    {
        using var activity = new System.Diagnostics.Activity("write").SetIdFormat(System.Diagnostics.ActivityIdFormat.W3C).Start();
        activity.TraceStateString = "alvo=1";

        var @event = OutboxEventFactory.For(Vehicles, OutboxOperation.Created, Caller, Now, PostImage, null);

        (@event.TraceParent, @event.TraceState).ShouldBe((activity.Id, "alvo=1"));
        @event.CorrelationId.ShouldBe(activity.TraceId.ToString());
    }

    /// <summary>A stated <c>traceparent</c> with no stated correlation makes its trace id the correlation id.</summary>
    [Fact]
    public void A_stated_traceparent_without_a_correlation_names_the_flow()
    {
        var caller = Caller with { Provenance = new AlvoProvenance { TraceParent = StatedTraceParent } };

        var @event = OutboxEventFactory.For(Vehicles, OutboxOperation.Created, caller, Now, PostImage, null);

        (@event.TraceParent, @event.CorrelationId).ShouldBe((StatedTraceParent, StatedTraceId));
    }

    /// <summary>A stated <c>traceparent</c> that is not valid W3C names no flow, so the event's own id does.</summary>
    /// <param name="traceParent">A malformed or all-zero traceparent.</param>
    [Theory]
    [InlineData("not-a-traceparent")]
    [InlineData("01-4bf92f3577b34da6a3ce929d0e0e4736-00f067aa0ba902b7-01")]
    [InlineData("00-4BF92F3577B34DA6A3CE929D0E0E4736-00f067aa0ba902b7-01")]
    [InlineData("00-00000000000000000000000000000000-00f067aa0ba902b7-01")]
    public void An_invalid_stated_traceparent_falls_back_to_the_events_own_id(string traceParent)
    {
        var caller = Caller with { Provenance = new AlvoProvenance { TraceParent = traceParent } };

        var @event = OutboxEventFactory.For(Vehicles, OutboxOperation.Created, caller, Now, PostImage, null);

        @event.CorrelationId.ShouldBe(@event.Id.ToString());
    }

    /// <summary>A stated trace context is the caller's own word and beats whatever W3C activity happens to be ambient.</summary>
    [Fact]
    public void A_stated_traceparent_beats_an_ambient_w3c_activity()
    {
        using var activity = new System.Diagnostics.Activity("write").SetIdFormat(System.Diagnostics.ActivityIdFormat.W3C).Start();
        activity.TraceStateString = "ambient=1";
        var caller = Caller with { Provenance = new AlvoProvenance { TraceParent = StatedTraceParent, TraceState = "stated=1" } };

        var @event = OutboxEventFactory.For(Vehicles, OutboxOperation.Created, caller, Now, PostImage, null);

        (@event.TraceParent, @event.TraceState, @event.CorrelationId).ShouldBe((StatedTraceParent, "stated=1", StatedTraceId));
    }

    /// <summary>
    /// A stated <c>traceparent</c> outside the W3C grammar is dropped, and its <c>tracestate</c> with it — W3C discards
    /// the state of a parent it cannot read — rather than becoming the registered extension's value.
    /// </summary>
    /// <param name="traceParent">A malformed, all-zero, unknown-version or newline-terminated traceparent.</param>
    [Theory]
    [InlineData("not-a-traceparent")]
    [InlineData("01-4bf92f3577b34da6a3ce929d0e0e4736-00f067aa0ba902b7-01")]
    [InlineData("00-00000000000000000000000000000000-00f067aa0ba902b7-01")]
    [InlineData($"{StatedTraceParent}\n")]
    public void An_invalid_stated_traceparent_is_dropped_with_its_tracestate(string traceParent)
    {
        var caller = Caller with { Provenance = new AlvoProvenance { TraceParent = traceParent, TraceState = "stated=1" } };

        var @event = OutboxEventFactory.For(Vehicles, OutboxOperation.Created, caller, Now, PostImage, null);

        (@event.TraceParent, @event.TraceState, @event.CorrelationId).ShouldBe((null, null, @event.Id.ToString()));
    }

    /// <summary>A stated <c>tracestate</c> outside the list grammar, or over the cap, is dropped; its valid parent stays.</summary>
    /// <param name="traceState">A malformed, newline-carrying or oversize tracestate.</param>
    [Theory]
    [InlineData("not a list member")]
    [InlineData("stated=1\n")]
    [InlineData("stated=1\r\nX-Injected: yes")]
    [MemberData(nameof(OversizeTraceState))]
    public void An_invalid_stated_tracestate_is_dropped_and_its_parent_kept(string traceState)
    {
        var caller = Caller with { Provenance = new AlvoProvenance { TraceParent = StatedTraceParent, TraceState = traceState } };

        var @event = OutboxEventFactory.For(Vehicles, OutboxOperation.Created, caller, Now, PostImage, null);

        (@event.TraceParent, @event.TraceState, @event.CorrelationId).ShouldBe((StatedTraceParent, null, StatedTraceId));
    }

    /// <summary>An ambient activity's <c>tracestate</c> — the inbound header — is held to the same grammar and cap.</summary>
    [Fact]
    public void An_oversize_ambient_tracestate_is_dropped_and_the_activity_kept()
    {
        using var activity = new System.Diagnostics.Activity("write").SetIdFormat(System.Diagnostics.ActivityIdFormat.W3C).Start();
        activity.TraceStateString = Oversize;

        var @event = OutboxEventFactory.For(Vehicles, OutboxOperation.Created, Caller, Now, PostImage, null);

        (@event.TraceParent, @event.TraceState).ShouldBe((activity.Id, null));
    }

    /// <summary>The oversize tracestate, as theory data.</summary>
    public static TheoryData<string> OversizeTraceState => [Oversize];

    /// <summary>A well-formed tracestate one character over <see cref="W3CTraceContext.MaxTraceStateLength"/>.</summary>
    private static string Oversize => $"k={new string('v', 255)},j={new string('w', 253)}";

    /// <summary>
    /// A hierarchical activity has no trace id; its all-zero placeholder must never become a correlation id that would
    /// put every such event in one flow.
    /// </summary>
    [Fact]
    public void A_hierarchical_activity_is_no_trace_context()
    {
        using var activity = new System.Diagnostics.Activity("write").SetIdFormat(System.Diagnostics.ActivityIdFormat.Hierarchical).Start();

        var @event = OutboxEventFactory.For(Vehicles, OutboxOperation.Created, Caller, Now, PostImage, null);

        (@event.TraceParent, @event.CorrelationId).ShouldBe((null, @event.Id.ToString()));
    }

    /// <summary>An exclusion holds when the operation has only one image: the post-image of a create, the pre-image of a delete.</summary>
    [Fact]
    public void Excluded_fields_leave_the_single_image_of_a_create_and_a_delete()
    {
        var entity = Vehicles with { EventImageExclusions = ["color"] };

        var created = OutboxEventFactory.For(entity, OutboxOperation.Created, Caller, Now, PostImage, null);
        var deleted = OutboxEventFactory.For(entity, OutboxOperation.Deleted, Caller, Now, null, PreImage);

        created.Data.Record!.Values.ShouldNotContainKey("color");
        created.Data.OldRecord.ShouldBeNull();
        deleted.Data.OldRecord!.Values.ShouldNotContainKey("color");
        deleted.Data.Record.ShouldBeNull();
        created.Data.Changed.ShouldContain("color");
        deleted.Data.Changed.ShouldContain("color");
    }

    private const string StatedTraceId = "4bf92f3577b34da6a3ce929d0e0e4736";

    private const string StatedTraceParent = $"00-{StatedTraceId}-00f067aa0ba902b7-01";

    /// <summary>
    /// The anonymous caller's reserved all-zero id means "no identity", so reporting it would assert that an
    /// identified caller made the change.
    /// </summary>
    [Fact]
    public void An_anonymous_caller_discloses_no_auth_id()
        => OutboxEventFactory.For(Vehicles, OutboxOperation.Created, AlvoContext.Anonymous, Now, PostImage, null)
            .AuthId.ShouldBeNull();

    /// <summary>An identified caller is on the envelope, because §3.3 needs to know who acted.</summary>
    [Fact]
    public void An_identified_caller_is_named_on_the_envelope()
        => Subject(OutboxOperation.Created).AuthId.ShouldBe(Caller.User.Value.ToString());

    /// <summary>
    /// The event's time is the write's own instant, handed in rather than read from a clock here — so the
    /// envelope's <c>time</c>, the row's audit stamp and the id's embedded millisecond are one instant.
    /// </summary>
    [Fact]
    public void The_events_time_is_the_writes_own_instant_never_a_second_clock_read()
        => OutboxEventFactory.For(Vehicles, OutboxOperation.Created, Caller, Now, PostImage, null)
            .Time.ShouldBe(Now);

    /// <summary>
    /// The id is minted by <see cref="AlvoEventId"/> and never by <c>Guid.CreateVersion7()</c>: the outbox
    /// claims in <c>ORDER BY id</c>, and the plain BCL mint sorts 49.9 % of its same-millisecond pairs
    /// backwards (spike Q1). Two events for one entity inside one millisecond are exactly that case.
    /// </summary>
    /// <remarks>
    /// Asserted here as well as on <c>AlvoEventIdTests</c>, because the mistake this guards against is made at
    /// <em>this</em> call site — both spellings compile and both produce a valid v7 id, so nothing but a fact
    /// over the factory's own output would notice.
    /// </remarks>
    [Fact]
    public void Two_events_minted_in_one_millisecond_sort_in_the_order_they_were_emitted()
    {
        var ids = Enumerable.Range(0, SameMillisecondEventCount)
            .Select(_ => Subject(OutboxOperation.Updated).Id.ToString())
            .ToList();

        ids.ShouldBe(
            [.. ids.Order(StringComparer.Ordinal)],
            customMessage: "ORDER BY id is the queue order, so the mint order has to survive an ordinal sort");
        ids.ShouldBeUnique();
    }

    private const int SameMillisecondEventCount = 64;

    /// <summary>
    /// <c>$defs/eventPattern</c>, copied verbatim from <c>schema/project.schema.json</c> — the descriptor's own
    /// grammar for what a rule may subscribe to, wildcards and the coalesced batch shape included.
    /// </summary>
    [GeneratedRegex(@"^(entity|auth|storage)\.([a-z][a-z0-9_]*|\*)\.([a-z]+|\*)(\.batch)?$")]
    private static partial Regex EventPatternRegex();

    private static AlvoEvent Subject(OutboxOperation operation) => OutboxEventFactory.For(
        Vehicles,
        operation,
        Caller,
        Now,
        operation == OutboxOperation.Deleted ? null : PostImage,
        operation == OutboxOperation.Created ? null : PreImage);

    private static EntitySchema Vehicles { get; } = new()
    {
        Name = "vehicles",
        Tenancy = TenancyMode.Global,
        Fields =
        [
            new FieldSchema { Name = AlvoManagedColumns.Id, Type = FieldType.Uuid, Required = true },
            new FieldSchema { Name = "make", Type = FieldType.String, Nullable = true },
            new FieldSchema { Name = "color", Type = FieldType.String, Nullable = true },
        ],
    };

    private static Guid RowId { get; } = Guid.Parse("3f2504e0-4f89-41d3-9a0c-0305e82c3301");

    private static DateTimeOffset Now { get; } = new(2026, 8, 3, 9, 30, 0, TimeSpan.Zero);

    private static AlvoContext Caller { get; } = AlvoDataFixtures.Caller;

    private static AlvoRecord PostImage { get; } = Record(("make", "vw"), ("color", "blue"));

    private static AlvoRecord PreImage { get; } = Record(("make", "vw"), ("color", "red"));

    private static AlvoRecord Record(params (string Field, object? Value)[] values)
    {
        var fields = new Dictionary<string, object?>(StringComparer.Ordinal) { [AlvoManagedColumns.Id] = RowId };
        foreach (var (field, value) in values)
        {
            fields[field] = value;
        }

        return new AlvoRecord(fields);
    }
}
