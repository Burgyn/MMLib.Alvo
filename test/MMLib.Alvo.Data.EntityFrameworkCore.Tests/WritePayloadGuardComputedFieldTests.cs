using MMLib.Alvo.Rules;
using MMLib.Alvo.Schema;
using MMLib.Alvo.Testing.Data;

namespace MMLib.Alvo.Data.EntityFrameworkCore.Tests;

/// <summary>
/// The guard's <c>computed</c> arm, which is the one refusal whose absence is <em>silent</em>: the runtime
/// model marks a computed property store-generated, so EF leaves the column out of the <c>INSERT</c> and a
/// caller who sent <c>line_total: 999</c> would get a <c>201</c> reporting the engine's own value with
/// nothing anywhere saying their number was discarded.
/// </summary>
public class WritePayloadGuardComputedFieldTests
{
    private const string Computed = "line_total";

    /// <summary>
    /// A payload naming a computed field is refused rather than silently dropped, on both write paths — an
    /// ignored payload is the wrong-stored-number failure class arriving from the caller's side.
    /// </summary>
    /// <param name="isUpdate">Whether this is an update rather than a create.</param>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void A_computed_field_is_refused_on_both_write_paths(bool isUpdate)
        => Should.Throw<AlvoAuthorizationException>(
            () => EnsureWritable(Payload((Computed, 999m)), isUpdate));

    /// <summary>
    /// The refusal names the field, so the answer is actionable: the caller has to remove that key, and the
    /// engine's own refusal — the guarantee behind this one — names neither the field nor the mechanism.
    /// </summary>
    [Fact]
    public void The_refusal_names_the_computed_field()
        => Should.Throw<AlvoAuthorizationException>(() => EnsureWritable(Payload((Computed, 999m)), isUpdate: false))
            .Message.ShouldContain(Computed);

    /// <summary>
    /// The arm is about <c>computed</c> fields and not about the entity that carries one: every other
    /// declared field on the same entity stays writable.
    /// </summary>
    [Fact]
    public void An_ordinary_field_on_the_same_entity_is_still_writable()
        => EnsureWritable(Payload(("plate", "ACME-001")), isUpdate: true);

    /// <summary>
    /// The framework-managed refusal names its field too, for the same reason: a managed column is declared
    /// by whoever controls the backend, so naming it discloses nothing a caller could not already read.
    /// </summary>
    [Fact]
    public void The_managed_column_refusal_names_its_field()
        => Should.Throw<AlvoAuthorizationException>(() => EnsureWritable(Payload(("id", Guid.NewGuid())), isUpdate: true))
            .Message.ShouldContain("id");

    /// <summary>
    /// An empty payload has nothing to refuse, even on an entity the applied schema does not declare. Every
    /// refusal in this type is decided from the payload's own keys, so an entity with no keys against it is
    /// not a refusal — it is nothing at all.
    /// </summary>
    [Fact]
    public void An_empty_payload_on_an_undeclared_entity_has_nothing_to_refuse()
        => WritePayloadGuard.PayloadRefusal(
            Payload(), entity: null, SnapshotFixture.UpdateDecision(null), isUpdate: false).ShouldBeNull();

    private static Dictionary<string, object?> Payload(params (string Field, object? Value)[] fields)
        => fields.ToDictionary(pair => pair.Field, pair => pair.Value, StringComparer.Ordinal);

    private static void EnsureWritable(Dictionary<string, object?> payload, bool isUpdate)
        => WritePayloadGuard.EnsureWritable(
            payload, ComputedVehicle(), SnapshotFixture.UpdateDecision(null), isUpdate);

    /// <summary>The canonical fixture entity with one <c>computed</c> field added.</summary>
    private static EntitySchema ComputedVehicle() => AlvoDataFixtures.Vehicle with
    {
        Fields = [
            .. AlvoDataFixtures.Vehicle.Fields,
            new FieldSchema { Name = Computed, Type = FieldType.Decimal, ComputedExpression = "mileage * price" },
        ],
    };
}
