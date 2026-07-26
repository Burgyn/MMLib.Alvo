using MMLib.Alvo.Rules;
using MMLib.Alvo.Testing.Data;

namespace MMLib.Alvo.Data.EntityFrameworkCore.Tests;

public class WritePayloadGuardTests
{
    [Fact]
    public void An_ordinary_field_is_writable_on_both_paths()
    {
        EnsureWritable(Payload(("plate", "ACME-001")), isUpdate: false);
        EnsureWritable(Payload(("plate", "ACME-001")), isUpdate: true);
    }

    [Fact]
    public void The_row_id_is_refused_on_both_paths()
    {
        Should.Throw<AlvoAuthorizationException>(() => EnsureWritable(Payload(("id", Guid.NewGuid())), isUpdate: false));
        Should.Throw<AlvoAuthorizationException>(() => EnsureWritable(Payload(("id", Guid.NewGuid())), isUpdate: true));
    }

    /// <summary>
    /// Deliberately asymmetric: a create legitimately places a row in a tenant, and the synthesized tenant
    /// scope over the candidate row is what decides whether it may. An update can never move a row
    /// between tenants at all, so there the key is refused before any row is looked up.
    /// </summary>
    [Fact]
    public void The_tenant_id_is_writable_on_create_and_refused_on_update()
    {
        EnsureWritable(Payload(("tenant_id", Guid.NewGuid())), isUpdate: false);
        Should.Throw<AlvoAuthorizationException>(() => EnsureWritable(Payload(("tenant_id", Guid.NewGuid())), isUpdate: true));
    }

    /// <summary>
    /// The two framework columns are not descriptor fields, so they can never appear in
    /// <see cref="PolicyDecision.ReadOnlyFields"/> — the read-only check alone would let both through, which
    /// is what makes these two refusals a separate gate rather than a duplicate of it.
    /// </summary>
    [Fact]
    public void The_framework_columns_are_refused_even_though_no_policy_marks_them_read_only()
    {
        SnapshotFixture.UpdateDecision(readOnlyField: null).ReadOnlyFields.ShouldBeEmpty();

        Should.Throw<AlvoAuthorizationException>(() => EnsureWritable(Payload(("id", Guid.NewGuid())), isUpdate: true));
        Should.Throw<AlvoAuthorizationException>(() => EnsureWritable(Payload(("tenant_id", Guid.NewGuid())), isUpdate: true));
    }

    [Fact]
    public void A_read_only_field_is_refused_and_the_message_names_it()
    {
        var refused = Should.Throw<AlvoAuthorizationException>(
            () => EnsureWritable(Payload(("status", "closed")), isUpdate: true, readOnly: "status"));

        refused.Message.ShouldContain("status");
    }

    [Fact]
    public void A_read_only_field_is_refused_on_create_as_well()
        => Should.Throw<AlvoAuthorizationException>(
            () => EnsureWritable(Payload(("status", "closed")), isUpdate: false, readOnly: "status"));

    [Fact]
    public void An_undeclared_key_is_refused_without_being_echoed()
    {
        var refused = Should.Throw<AlvoAuthorizationException>(
            () => EnsureWritable(Payload(("nope\"; DROP TABLE vehicle; --", 1)), isUpdate: false));

        refused.Message.ShouldNotContain("DROP TABLE");
    }

    /// <summary>
    /// An entity the applied schema does not know declares nothing, so every key fails closed: a mismatch
    /// between the policy catalog and this implementation's schema must not be the one path on which an
    /// unvalidated payload reaches storage.
    /// </summary>
    [Fact]
    public void An_entity_the_schema_does_not_know_refuses_every_key()
        => Should.Throw<AlvoAuthorizationException>(() => WritePayloadGuard.EnsureWritable(
            Payload(("plate", "ACME-001")), entity: null, SnapshotFixture.UpdateDecision(null), isUpdate: false));

    /// <summary>
    /// A <c>hidden</c> field stays writable: <c>hidden</c> is a read restriction, and refusing a write to one
    /// would tell the caller the field exists.
    /// </summary>
    [Fact]
    public void A_masked_field_is_still_writable()
        => EnsureWritable(Payload(("secret_note", "shh")), isUpdate: true, hidden: "secret_note");

    [Fact]
    public void Every_argument_is_required()
    {
        Should.Throw<ArgumentNullException>(() => WritePayloadGuard.EnsureWritable(
            null!, AlvoDataFixtures.Vehicle, SnapshotFixture.UpdateDecision(null), isUpdate: false));
        Should.Throw<ArgumentNullException>(() => WritePayloadGuard.EnsureWritable(
            Payload(), AlvoDataFixtures.Vehicle, null!, isUpdate: false));
    }

    private static Dictionary<string, object?> Payload(params (string Field, object? Value)[] fields)
        => fields.ToDictionary(pair => pair.Field, pair => pair.Value, StringComparer.Ordinal);

    private static void EnsureWritable(
        Dictionary<string, object?> payload, bool isUpdate, string? readOnly = null, string? hidden = null)
        => WritePayloadGuard.EnsureWritable(
            payload,
            AlvoDataFixtures.Vehicle,
            SnapshotFixture.UpdateDecision(readOnly, hidden),
            isUpdate);
}
