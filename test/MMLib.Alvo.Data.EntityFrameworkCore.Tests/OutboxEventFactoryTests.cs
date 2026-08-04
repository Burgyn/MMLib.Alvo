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
        { AlvoContext.Anonymous, AlvoEventAuthType.Anonymous },
        { AlvoContext.System(tenant: null), AlvoEventAuthType.System },
        { Caller, AlvoEventAuthType.ApiKey },
    };

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
