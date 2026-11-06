using MMLib.Alvo.Data;
using MMLib.Alvo.Descriptor;
using MMLib.Alvo.Schema;
using Shouldly;
using Xunit;

namespace MMLib.Alvo.Testing.Data;

/// <summary>
/// Relation rules over a real engine: a rule that reads a related row matches only through a related row the caller
/// may itself list — its own rule and its own tenant scope ANDed inside the correlated <c>EXISTS</c> — on reads, counts
/// and writes alike, identically on every engine.
/// </summary>
/// <remarks>
/// <para>
/// <b>Its own suite, for <see cref="AlvoDataComputedRollupTests"/>' reason:</b> a relation is a correlated read the
/// engine runs, and the in-memory double refuses one rather than imitate it.
/// </para>
/// <para>
/// Rows are seeded through an <c>admin</c> caller every create rule admits, in the tenant the row belongs to; every
/// fact then reads and writes as the ordinary callers of <see cref="RelationWorld"/>.
/// </para>
/// </remarks>
public abstract partial class AlvoDataRelationRuleTests
{
    /// <summary>Builds a fresh <see cref="IAlvoData"/> over <paramref name="schema"/> with <paramref name="descriptor"/>'s rules.</summary>
    /// <param name="schema">The schema every entity in <paramref name="descriptor"/> maps to.</param>
    /// <param name="descriptor">The project descriptor whose rules apply.</param>
    protected abstract Task<IAlvoData> CreateAsync(SchemaModel schema, AlvoDescriptor descriptor);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>B cannot place an order on A's customer: the create probe finds no customer B may list.</summary>
    [Fact]
    public async Task A_create_whose_relation_the_caller_cannot_satisfy_is_refused()
    {
        var (data, world) = await RelationWorldAsync();
        var aCustomer = await world.SeedCustomerAsync(world.A, active: true);

        await Should.ThrowAsync<AlvoAuthorizationException>(() =>
            data.CreateAsync(RelationWorld.Orders, world.Order(aCustomer), world.B, cancellationToken: Ct));
    }

    /// <summary>A places an order on A's own customer.</summary>
    [Fact]
    public async Task A_create_through_a_relation_the_caller_satisfies_succeeds()
    {
        var (data, world) = await RelationWorldAsync();
        var aCustomer = await world.SeedCustomerAsync(world.A, active: true);

        var created = await data.CreateAsync(RelationWorld.Orders, world.Order(aCustomer), world.A, cancellationToken: Ct);

        created["customer_id"].ShouldBe(aCustomer);
    }

    /// <summary>The <c>WITH CHECK</c> probe reads the post-image: moving an order to a customer A cannot list is refused.</summary>
    [Fact]
    public async Task An_update_moving_a_row_to_a_related_row_the_caller_cannot_list_is_refused()
    {
        var (data, world) = await RelationWorldAsync();
        var aOrder = await world.SeedOrderAsync(world.A, active: true);
        var bCustomer = await world.SeedCustomerAsync(world.B, active: true);

        await data.UpdateAsync(RelationWorld.Orders, aOrder, Patch("note", "still mine"), world.A, cancellationToken: Ct);

        await Should.ThrowAsync<AlvoAuthorizationException>(() =>
            data.UpdateAsync(RelationWorld.Orders, aOrder, Patch("customer_id", bCustomer), world.A, cancellationToken: Ct));
        (await data.GetAsync(RelationWorld.Orders, aOrder, world.A, Ct)).ShouldNotBeNull()["note"].ShouldBe("still mine");
    }

    /// <summary>Each row is probed over its own post-image, and every failing row is reported at its index.</summary>
    [Fact]
    public async Task A_batch_create_reports_each_row_whose_relation_fails_at_its_index()
    {
        var (data, world) = await RelationWorldAsync();
        var mine = await world.SeedCustomerAsync(world.A, active: true);
        var theirs = await world.SeedCustomerAsync(world.B, active: true);

        var result = await data.CreateManyAsync(
            RelationWorld.Orders,
            [world.Order(mine), world.Order(theirs), world.Order(mine), world.Order(null), world.Order(theirs)],
            world.A,
            cancellationToken: Ct);

        result.Succeeded.ShouldBeFalse();
        result.Refusals.Select(refusal => refusal.Index).ShouldBe([1, 3, 4], ignoreOrder: false);
    }

    /// <summary>A null ref reads no related row, so the relation is false.</summary>
    [Fact]
    public async Task A_null_reference_never_satisfies_a_forward_relation()
    {
        var (data, world) = await RelationWorldAsync();

        await Should.ThrowAsync<AlvoAuthorizationException>(() =>
            data.CreateAsync(RelationWorld.Orders, world.Order(null), world.A, cancellationToken: Ct));
    }

    /// <summary>
    /// Post-image values bind by their declared type, so the probe compares a decimal and an instant as the columns do —
    /// on SQLite's text-stored decimals as much as anywhere — exactly at the boundary.
    /// </summary>
    /// <param name="amount">The quote's amount, against a credit limit of 100.50.</param>
    /// <param name="millisecondsAfterSince">How long after the customer's start the quote is placed.</param>
    /// <param name="admitted">Whether the create is admitted.</param>
    [Theory]
    [InlineData("100.50", 0, true)]
    [InlineData("100.51", 0, false)]
    [InlineData("100.50", -1, false)]
    [InlineData("100.49", 1, true)]
    public async Task A_probe_compares_outer_decimal_and_timestamp_values_as_the_columns_do(
        string amount, int millisecondsAfterSince, bool admitted)
    {
        var (data, world) = await RelationWorldAsync();
        var customer = await world.SeedCustomerAsync(world.A, active: true, creditLimit: 100.50m);
        var quote = world.Quote(customer, decimal.Parse(amount, System.Globalization.CultureInfo.InvariantCulture),
            RelationWorld.Since.AddMilliseconds(millisecondsAfterSince));

        var attempt = () => data.CreateAsync(RelationWorld.Quotes, quote, world.A, cancellationToken: Ct);

        if (admitted)
        {
            (await attempt())["customer_id"].ShouldBe(customer);
        }
        else
        {
            await Should.ThrowAsync<AlvoAuthorizationException>(attempt);
        }
    }

    private async Task<(IAlvoData Data, RelationWorld World)> RelationWorldAsync()
    {
        var data = await CreateAsync(RelationWorld.Schema(), RelationWorld.Descriptor());
        return (data, new RelationWorld(data));
    }

    private static Dictionary<string, object?> Patch(string field, object? value) =>
        new(StringComparer.Ordinal) { [field] = value };
}
