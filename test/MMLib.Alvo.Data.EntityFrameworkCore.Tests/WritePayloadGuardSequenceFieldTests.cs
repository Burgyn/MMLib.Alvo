using MMLib.Alvo.Schema;
using MMLib.Alvo.Testing.Data;

namespace MMLib.Alvo.Data.EntityFrameworkCore.Tests;

/// <summary>The guard's <c>sequence</c> arm: the framework assigns the number, so a caller's value for one is refused.</summary>
public class WritePayloadGuardSequenceFieldTests
{
    private const string Sequence = "service_number";

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void A_payload_naming_a_sequence_field_is_refused_like_a_computed_one(bool isUpdate)
    {
        var message = Should.Throw<AlvoAuthorizationException>(() => WritePayloadGuard.EnsureWritable(
            new Dictionary<string, object?>(StringComparer.Ordinal) { [Sequence] = 7L },
            SequencedVehicle(), SnapshotFixture.UpdateDecision(null), isUpdate)).Message;

        message.ShouldBe(
            $"Field '{Sequence}' is a sequence number assigned by the framework and cannot be written: Alvo allocates it "
            + "inside the create's own transaction, so a value sent here would be a number the count never issued. "
            + "Remove it from the payload.");
    }

    [Fact]
    public void An_ordinary_field_on_the_same_entity_is_still_writable() =>
        WritePayloadGuard.EnsureWritable(
            new Dictionary<string, object?>(StringComparer.Ordinal) { ["plate"] = "ACME-001" },
            SequencedVehicle(), SnapshotFixture.UpdateDecision(null), isUpdate: true);

    private static EntitySchema SequencedVehicle() => AlvoDataFixtures.Vehicle with
    {
        Fields = [
            .. AlvoDataFixtures.Vehicle.Fields,
            new FieldSchema { Name = Sequence, Type = FieldType.Integer, Nullable = true, Sequence = new SequenceSchema() },
        ],
    };
}
