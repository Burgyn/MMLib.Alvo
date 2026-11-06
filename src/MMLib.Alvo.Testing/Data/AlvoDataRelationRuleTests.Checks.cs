using MMLib.Alvo.Data;
using Shouldly;
using Xunit;

namespace MMLib.Alvo.Testing.Data;

/// <summary>
/// Lookup checks: a write whose looked-up row the caller cannot list, or that fails the check's condition, is refused
/// with the author's message and the lookup field; a null lookup skips the check; an update re-runs it only when a field
/// it reads changed; and a batch reports the failing row at its index.
/// </summary>
public abstract partial class AlvoDataRelationRuleTests
{
    /// <summary>An inactive product refuses the line with the check's own message, pointed at the lookup, and writes nothing.</summary>
    [Fact]
    public async Task A_failing_lookup_check_refuses_with_its_message_and_the_lookup_pointer()
    {
        var (data, world) = await RelationWorldAsync();
        var product = await world.SeedProductAsync(world.Tenant1, active: false, stock: 5);

        var refusal = await Should.ThrowAsync<AlvoConstraintViolationException>(() =>
            data.CreateAsync(RelationWorld.OrderLines, world.Line(product, 1), world.A, cancellationToken: Ct));

        refusal.Kind.ShouldBe(AlvoConstraintKind.Check);
        refusal.Fields.ShouldBe(["product_id"]);
        refusal.Message.ShouldBe(RelationWorld.Unavailable);
        (await data.QueryAsync(new AlvoQuery { Entity = RelationWorld.OrderLines }, world.A, Ct)).Items.ShouldBeEmpty();
    }

    /// <summary>An active product with stock enough lets the line through.</summary>
    [Fact]
    public async Task A_passing_lookup_check_lets_the_write_through()
    {
        var (data, world) = await RelationWorldAsync();
        var product = await world.SeedProductAsync(world.Tenant1, active: true, stock: 5);

        var line = await data.CreateAsync(RelationWorld.OrderLines, world.Line(product, 5), world.A, cancellationToken: Ct);

        line["product_id"].ShouldBe(product);
    }

    /// <summary>A null lookup names no row, so the check does not apply — SQL <c>CHECK</c>'s rule for an unknown.</summary>
    [Fact]
    public async Task A_null_lookup_skips_its_check()
    {
        var (data, world) = await RelationWorldAsync();

        var line = await data.CreateAsync(RelationWorld.OrderLines, world.Line(null, 99), world.A, cancellationToken: Ct);

        line["product_id"].ShouldBeNull();
    }

    /// <summary>A product in another tenant satisfies the condition, but the caller cannot list it, so the check fails.</summary>
    [Fact]
    public async Task A_lookup_check_over_a_row_the_caller_cannot_list_refuses()
    {
        var (data, world) = await RelationWorldAsync();
        var elsewhere = await world.SeedProductAsync(world.Tenant2, active: true, stock: 5);

        var refusal = await Should.ThrowAsync<AlvoConstraintViolationException>(() =>
            data.CreateAsync(RelationWorld.OrderLines, world.Line(elsewhere, 1), world.A, cancellationToken: Ct));

        refusal.Message.ShouldBe(RelationWorld.Unavailable);
    }

    /// <summary>A product deactivated after the line was written does not block an edit to a field the check never reads.</summary>
    [Fact]
    public async Task An_update_that_changes_no_field_a_check_reads_skips_it()
    {
        var (data, world) = await RelationWorldAsync();
        var product = await world.SeedProductAsync(world.Tenant1, active: true, stock: 5);
        var line = (Guid)(await data.CreateAsync(RelationWorld.OrderLines, world.Line(product, 1), world.A, cancellationToken: Ct))["id"]!;
        await world.UpdateAsSeederAsync(RelationWorld.Products, product, world.Tenant1, "active", false);

        var updated = await data.UpdateAsync(RelationWorld.OrderLines, line, Patch("note", "gift wrap"), world.A, cancellationToken: Ct);

        updated["note"].ShouldBe("gift wrap");
    }

    /// <summary>Raising the quantity past the stock re-runs the check, which refuses it and leaves the line as it was.</summary>
    [Fact]
    public async Task An_update_changing_quantity_re_runs_the_check()
    {
        var (data, world) = await RelationWorldAsync();
        var product = await world.SeedProductAsync(world.Tenant1, active: true, stock: 5);
        var line = (Guid)(await data.CreateAsync(RelationWorld.OrderLines, world.Line(product, 1), world.A, cancellationToken: Ct))["id"]!;

        var refusal = await Should.ThrowAsync<AlvoConstraintViolationException>(() =>
            data.UpdateAsync(RelationWorld.OrderLines, line, Patch("quantity", 6), world.A, cancellationToken: Ct));

        refusal.Fields.ShouldBe(["product_id"]);
        Convert.ToInt32((await data.GetAsync(RelationWorld.OrderLines, line, world.A, Ct)).ShouldNotBeNull()["quantity"], System.Globalization.CultureInfo.InvariantCulture)
            .ShouldBe(1);
    }

    /// <summary>A lookup the descriptor hides is never named: the refusal carries the message and no field.</summary>
    [Fact]
    public async Task A_hidden_lookup_field_is_not_named_by_the_refusal()
    {
        var (data, world) = await RelationWorldAsync();
        var product = await world.SeedProductAsync(world.Tenant1, active: false, stock: 5);

        var refusal = await Should.ThrowAsync<AlvoConstraintViolationException>(() =>
            data.CreateAsync(RelationWorld.GiftLines, world.Gift(product, 1), world.A, cancellationToken: Ct));

        refusal.Kind.ShouldBe(AlvoConstraintKind.Check);
        refusal.Fields.ShouldBeEmpty();
        refusal.Message.ShouldBe(RelationWorld.Unavailable);
    }

    /// <summary>Each row is checked over its own post-image; the failing one is reported at its index with the check's code and message.</summary>
    [Fact]
    public async Task A_batch_reports_the_failing_row_s_check_at_its_index()
    {
        var (data, world) = await RelationWorldAsync();
        var active = await world.SeedProductAsync(world.Tenant1, active: true, stock: 5);
        var inactive = await world.SeedProductAsync(world.Tenant1, active: false, stock: 5);

        var result = await data.CreateManyAsync(
            RelationWorld.OrderLines,
            [world.Line(active, 1), world.Line(inactive, 1), world.Line(active, 9), world.Line(null, 1)],
            world.A,
            cancellationToken: Ct);

        result.Succeeded.ShouldBeFalse();
        result.Refusals.Select(refusal => (refusal.Index, refusal.Code, refusal.Message))
            .ShouldBe([(1, "check", RelationWorld.Unavailable), (2, "check", RelationWorld.Unavailable)]);
    }
}
