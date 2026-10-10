using MMLib.Alvo.Rules;
using MMLib.Alvo.Schema;
using MMLib.Alvo.Testing.Data;

namespace MMLib.Alvo.Data.EntityFrameworkCore.Tests;

/// <summary>
/// The guard's <c>rollup</c> arm (#342): a rollup is maintained by Alvo from the child rows, so a caller's value for
/// one is refused exactly like a value for a <c>computed</c> field.
/// </summary>
/// <remarks>
/// Unlike a computed column, nothing below the port refuses this write: a rollup is an ordinary column the framework
/// keeps by a statement of its own, so without this arm the caller's number is <em>stored</em> — and every computed
/// field reading it follows the forged value.
/// </remarks>
public class WritePayloadGuardRollupFieldTests
{
    private const string Rollup = "service_count";

    /// <summary>A payload naming a rollup is refused on both write paths.</summary>
    /// <param name="isUpdate">Whether this is an update rather than a create.</param>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void A_rollup_field_is_refused_on_both_write_paths(bool isUpdate)
        => Should.Throw<AlvoAuthorizationException>(() => EnsureWritable(Payload((Rollup, 1L)), isUpdate));

    /// <summary>An explicit <see langword="null"/> is a write too, and is refused the same way.</summary>
    [Fact]
    public void A_null_written_to_a_rollup_field_is_refused()
        => Should.Throw<AlvoAuthorizationException>(() => EnsureWritable(Payload((Rollup, null)), isUpdate: true));

    /// <summary>
    /// The refusal names the field and says it is a rollup, so the caller knows which key to drop — and it does
    /// not name the child entity, which this caller may have no rule to read.
    /// </summary>
    [Fact]
    public void The_refusal_names_the_rollup_field_and_not_its_child_entity()
    {
        var message = Should.Throw<AlvoAuthorizationException>(
            () => EnsureWritable(Payload((Rollup, 1L)), isUpdate: false)).Message;

        message.ShouldContain(Rollup);
        message.ShouldContain("rollup");
        message.ShouldNotContain("services");
    }

    /// <summary>The collecting form a batch reads answers the same refusal the throwing form raises.</summary>
    [Fact]
    public void The_batch_verdict_is_the_same_refusal()
        => WritePayloadGuard.PayloadRefusal(
                Payload((Rollup, 1L)), RolledUpVehicle(), SnapshotFixture.UpdateDecision(null), isUpdate: true)
            .ShouldBe(Should.Throw<AlvoAuthorizationException>(
                () => EnsureWritable(Payload((Rollup, 1L)), isUpdate: true)).Message);

    /// <summary>Every other declared field on the entity carrying the rollup stays writable.</summary>
    [Fact]
    public void An_ordinary_field_on_the_same_entity_is_still_writable()
        => EnsureWritable(Payload(("plate", "ACME-001")), isUpdate: true);

    private static Dictionary<string, object?> Payload(params (string Field, object? Value)[] fields)
        => fields.ToDictionary(pair => pair.Field, pair => pair.Value, StringComparer.Ordinal);

    private static void EnsureWritable(Dictionary<string, object?> payload, bool isUpdate)
        => WritePayloadGuard.EnsureWritable(
            payload, RolledUpVehicle(), SnapshotFixture.UpdateDecision(null), isUpdate);

    /// <summary>The canonical fixture entity with one <c>rollup</c> field added.</summary>
    private static EntitySchema RolledUpVehicle() => AlvoDataFixtures.Vehicle with
    {
        Fields = [
            .. AlvoDataFixtures.Vehicle.Fields,
            new FieldSchema
            {
                Name = Rollup,
                Type = FieldType.Integer,
                Nullable = true,
                Rollup = new RollupSchema { From = "services", Via = "vehicle_id", Op = RollupOperation.Count },
            },
        ],
    };
}
