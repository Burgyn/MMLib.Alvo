using static MMLib.Alvo.Data.EntityFrameworkCore.Tests.EfAlvoDataRollupWritePathTests;

namespace MMLib.Alvo.Data.EntityFrameworkCore.Tests;

/// <summary>
/// Every write method of the port refuses a caller's payload that names a <c>rollup</c> (#342), and the refused
/// value never reaches the column.
/// </summary>
/// <remarks>
/// <para>
/// <b>One fact per method, because each one is its own call into the guard.</b> The create, the idempotent create,
/// the update, both branches of a replace and both batch routes each reach <c>WritePayloadGuard</c> from a
/// different site; a fix made at one of them and missed at another is exactly the shape of the defect this pins —
/// a total a caller overwrote, with no statement failing.
/// </para>
/// <para>
/// The engine-agnostic half (SQLite and PostgreSQL, over HTTP) is <c>DataApiEngineTests</c>' rollup facts; this is
/// the port itself, over the driver both shipped engines share, where an embedded host's own code reaches it.
/// </para>
/// </remarks>
public class EfAlvoDataRollupPayloadTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>A create naming the rollup is refused.</summary>
    [Fact]
    public async Task A_create_naming_a_rollup_is_refused()
    {
        await using var world = await StartAsync();

        var refusal = await Should.ThrowAsync<AlvoAuthorizationException>(
            () => world.Data.CreateAsync(Invoices, Forged(), world.Caller, cancellationToken: Ct));

        ShouldNameTheRollup(refusal.Message);
    }

    /// <summary>An idempotent create naming the rollup is refused — the token changes recording, not meaning.</summary>
    [Fact]
    public async Task An_idempotent_create_naming_a_rollup_is_refused()
    {
        await using var world = await StartAsync();

        var refusal = await Should.ThrowAsync<AlvoAuthorizationException>(
            () => world.Data.CreateAsync(Invoices, Forged(), world.Caller, Token(), Ct));

        ShouldNameTheRollup(refusal.Message);
    }

    /// <summary>An update naming the rollup is refused, and the stored total is the children's.</summary>
    [Fact]
    public async Task An_update_naming_a_rollup_is_refused_and_the_total_is_kept()
    {
        await using var world = await StartAsync();
        var invoice = await InvoiceWithItemAsync(world);

        var refusal = await Should.ThrowAsync<AlvoAuthorizationException>(
            () => world.Data.UpdateAsync(Invoices, invoice, Forged(), world.Caller, cancellationToken: Ct));

        ShouldNameTheRollup(refusal.Message);
        await ShouldHoldTheChildrensTotalAsync(world, invoice);
    }

    /// <summary>A replace naming the rollup is refused on the branch that replaces an existing row.</summary>
    [Fact]
    public async Task A_replace_of_an_existing_row_naming_a_rollup_is_refused()
    {
        await using var world = await StartAsync();
        var invoice = await InvoiceWithItemAsync(world);

        var refusal = await Should.ThrowAsync<AlvoAuthorizationException>(
            () => world.Data.ReplaceAsync(Invoices, invoice, Forged(), world.Caller, cancellationToken: Ct));

        ShouldNameTheRollup(refusal.Message);
        await ShouldHoldTheChildrensTotalAsync(world, invoice);
    }

    /// <summary>A replace naming the rollup is refused on the branch that creates the row at a free id.</summary>
    [Fact]
    public async Task A_replace_that_would_create_naming_a_rollup_is_refused()
    {
        await using var world = await StartAsync();
        var free = Guid.NewGuid();

        var refusal = await Should.ThrowAsync<AlvoAuthorizationException>(
            () => world.Data.ReplaceAsync(Invoices, free, Forged(), world.Caller, cancellationToken: Ct));

        ShouldNameTheRollup(refusal.Message);
        (await world.Data.GetAsync(Invoices, free, world.Caller, Ct)).ShouldBeNull("the create branch stored nothing");
    }

    /// <summary>A batch create row naming the rollup is refused by its index, and the batch writes nothing.</summary>
    [Fact]
    public async Task A_batch_create_row_naming_a_rollup_is_refused_by_its_index()
    {
        await using var world = await StartAsync();

        var result = await world.Data.CreateManyAsync(
            Invoices, [Reference("INV-OK"), Forged()], world.Caller, cancellationToken: Ct);

        result.Succeeded.ShouldBeFalse();
        var refusal = result.Refusals.ShouldHaveSingleItem();
        refusal.Index.ShouldBe(1);
        refusal.Code.ShouldBe("forbidden");
        ShouldNameTheRollup(refusal.Message);
    }

    /// <summary>A batch update row naming the rollup is refused by its index, and the total is kept.</summary>
    [Fact]
    public async Task A_batch_update_row_naming_a_rollup_is_refused_by_its_index()
    {
        await using var world = await StartAsync();
        var invoice = await InvoiceWithItemAsync(world);

        var result = await world.Data.UpdateManyAsync(
            Invoices, [new AlvoRowPatch(invoice, Forged())], world.Caller, cancellationToken: Ct);

        var refusal = result.Refusals.ShouldHaveSingleItem();
        refusal.Index.ShouldBe(0);
        refusal.Code.ShouldBe("forbidden");
        ShouldNameTheRollup(refusal.Message);
        await ShouldHoldTheChildrensTotalAsync(world, invoice);
    }

    private const int ChildAmount = 7;

    private static Dictionary<string, object?> Forged() =>
        new(StringComparer.Ordinal) { ["reference"] = "INV-FORGED", [TotalAmount] = 1 };

    private static Dictionary<string, object?> Reference(string reference) =>
        new(StringComparer.Ordinal) { ["reference"] = reference };

    private static AlvoIdempotency Token() => new(Guid.NewGuid().ToString(), "fingerprint");

    private static async Task<Guid> InvoiceWithItemAsync(WritePathWorld world)
    {
        var invoice = await InvoiceAsync(world);
        await world.Data.CreateAsync(Items, Item(invoice, ChildAmount), world.Caller, cancellationToken: Ct);

        return invoice;
    }

    private static async Task ShouldHoldTheChildrensTotalAsync(WritePathWorld world, Guid invoice) =>
        (await ReadInvoiceAsync(world, invoice))[TotalAmount].ShouldBe(
            (long)ChildAmount, "the rollup is the sum of the children, never the caller's number");

    private static void ShouldNameTheRollup(string message)
    {
        message.ShouldContain(TotalAmount);
        message.ShouldContain("rollup");
    }
}
