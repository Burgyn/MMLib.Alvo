using MMLib.Alvo.Data.EntityFrameworkCore.Internal;
using MMLib.Alvo.Schema;
using MMLib.Alvo.Testing.Data;

namespace MMLib.Alvo.Data.EntityFrameworkCore.Tests;

/// <summary>
/// Which image the envelope reads from, and what the factory refuses before it reads anything at all.
/// </summary>
/// <remarks>
/// Separate from <see cref="OutboxEventFactoryTests"/>, which pins the envelope one ordinary write produces.
/// Every fact here is about an edge the three write faces never reach on their own: a missing argument, two
/// images that disagree, and no image at all.
/// </remarks>
public class OutboxEventFactoryImageTests
{
    /// <summary>
    /// The entity is refused as a missing argument rather than dereferenced while the event type is
    /// composed — one line later, and a <see cref="NullReferenceException"/> from inside a string
    /// interpolation names nothing.
    /// </summary>
    [Fact]
    public void A_missing_entity_is_refused_as_a_missing_argument()
        => Should.Throw<ArgumentNullException>(
                () => OutboxEventFactory.For(null!, OutboxOperation.Created, Caller, Now, PostImage, null))
            .ParamName.ShouldBe("entity");

    /// <summary>
    /// The caller is refused <em>before</em> the images are inspected. With that guard gone, a write with no
    /// caller and no image at all is reported as the images' invariant breach instead — the provenance
    /// helpers do guard the caller themselves, but only after the row id has already been demanded.
    /// </summary>
    [Fact]
    public void A_missing_caller_is_refused_before_the_images_are_read()
        => Should.Throw<ArgumentNullException>(
                () => OutboxEventFactory.For(Vehicles, OutboxOperation.Created, null!, Now, null, null))
            .ParamName.ShouldBe("context");

    /// <summary>
    /// An event describes one row, and with no image to take that row's id from it is a breach of this data
    /// path's own invariant rather than a caller's mistake.
    /// </summary>
    [Fact]
    public void An_event_with_neither_image_is_refused_because_it_names_no_row()
        => Should.Throw<InvalidOperationException>(
            () => OutboxEventFactory.For(Vehicles, OutboxOperation.Updated, Caller, Now, null, null));

    /// <summary>
    /// When both images are present the row is taken from the <em>post</em>-image. The two agree on every
    /// write this factory serves, so the precedence is only ever visible here — and it is the right way
    /// round: the subject names the row as it stands after the change.
    /// </summary>
    [Fact]
    public void The_subject_names_the_post_images_row_when_the_two_images_disagree()
    {
        var @event = OutboxEventFactory.For(
            Vehicles, OutboxOperation.Updated, Caller, Now, Row(AfterId), Row(BeforeId));

        @event.Subject.ShouldBe($"vehicles/{AfterId}");
        @event.PartitionKey.ShouldBe(OutboxEventFactory.PartitionKeyFor("vehicles", AfterId));
    }

    /// <summary>
    /// No image, no fields. Reached only through <see cref="OutboxEventFactory.ChangedFields"/> itself — a
    /// full envelope with neither image is refused earlier — and it has to answer with an empty list rather
    /// than by dereferencing the image that is not there.
    /// </summary>
    [Fact]
    public void Changed_is_empty_when_there_is_no_image_to_read_fields_from()
        => OutboxEventFactory.ChangedFields(null, null).ShouldBeEmpty();

    /// <summary>
    /// A create names every field of the new row, ordered ordinally — the same order the update path emits,
    /// so one write produces one payload byte for byte whatever order the engine returned the columns in.
    /// </summary>
    [Fact]
    public void A_creates_changed_fields_are_ordered_ordinally()
        => OutboxEventFactory.ChangedFields(Row(AfterId, ("make", "vw"), ("color", "blue")), null)
            .ShouldBe(["color", "id", "make"]);

    /// <summary>The same order on a delete, which reads its fields from the pre-image.</summary>
    [Fact]
    public void A_deletes_changed_fields_are_ordered_ordinally()
        => OutboxEventFactory.ChangedFields(null, Row(BeforeId, ("make", "vw"), ("color", "red")))
            .ShouldBe(["color", "id", "make"]);

    private static Guid AfterId { get; } = Guid.Parse("3f2504e0-4f89-41d3-9a0c-0305e82c3301");

    private static Guid BeforeId { get; } = Guid.Parse("8f14e45f-ceea-467a-9cfd-98b27a8b2f1a");

    private static DateTimeOffset Now { get; } = new(2026, 8, 3, 9, 30, 0, TimeSpan.Zero);

    private static AlvoContext Caller { get; } = AlvoDataFixtures.Caller;

    private static AlvoRecord PostImage { get; } = Row(AfterId, ("make", "vw"));

    private static EntitySchema Vehicles { get; } = new()
    {
        Name = "vehicles",
        Tenancy = TenancyMode.Global,
        Fields =
        [
            new FieldSchema { Name = AlvoManagedColumns.Id, Type = FieldType.Uuid, Required = true },
            new FieldSchema { Name = "make", Type = FieldType.String, Nullable = true },
        ],
    };

    /// <summary>
    /// A row whose fields arrive in an order no sort would produce, so an ordering claim is a claim about
    /// the factory rather than about the dictionary it was handed.
    /// </summary>
    private static AlvoRecord Row(Guid rowId, params (string Field, object? Value)[] values)
    {
        var fields = new Dictionary<string, object?>(StringComparer.Ordinal);
        foreach (var (field, value) in values)
        {
            fields[field] = value;
        }

        fields[AlvoManagedColumns.Id] = rowId;
        return new AlvoRecord(fields);
    }
}
