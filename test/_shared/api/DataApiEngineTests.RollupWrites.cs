using System.Net;
using System.Text.Json.Nodes;

namespace MMLib.Alvo.Api.Tests;

/// <summary>
/// A rollup is maintained by Alvo and by nothing else, on every write route and on every engine (#342): a payload
/// naming one is refused with the same <c>403</c> <c>forbidden</c> a payload naming a computed field gets.
/// </summary>
/// <remarks>
/// <para>
/// <b>Engine-sensitive by construction, which is why it lives here and not in a SQLite-only suite.</b> The defect
/// this pins was invisible to everything but a stored value: <c>PATCH {"net_total": 1}</c> answered <c>200</c> and
/// the engine kept the <c>1</c>, and a computed field reading the rollup followed it. Each fact therefore reads the
/// parent back after the refusal, and the computed field with it, because a refusal that still let the value land
/// would be the same bug with a different status.
/// </para>
/// <para>
/// The counterweight is <see cref="The_rollup_still_follows_its_children_after_every_refused_write"/>: refusing the
/// caller must not have refused the maintainer, which writes the same column from inside the child's transaction.
/// </para>
/// </remarks>
public abstract partial class DataApiEngineTests
{
    private static readonly TestApiKey _invoiceWriter = new(
        "invoice-writer",
        ["writer"],
        ["invoices:read", "invoices:write", "invoice_items:read", "invoice_items:write"]);

    /// <summary>A <c>POST</c> naming the rollup is refused, and no invoice is stored.</summary>
    [Fact]
    public async Task A_create_naming_a_rollup_is_403_forbidden_and_stores_nothing()
    {
        await using var world = await StartRollupInvoicesAsync();

        using var response = await world.SendAsync(
            HttpMethod.Post, "/api/invoices", _invoiceWriter, body: ForgedInvoice());

        await ShouldBeRollupRefusalAsync(response);
        (await world.CountRowsAsync("invoices")).ShouldBe(0, "a refused create stores no row");
    }

    /// <summary>
    /// The issue's own repro: a <c>PATCH</c> naming the rollup is refused, and neither the rollup nor the computed
    /// field reading it moves.
    /// </summary>
    [Fact]
    public async Task A_patch_naming_a_rollup_is_403_forbidden_and_the_total_does_not_move()
    {
        await using var world = await StartRollupInvoicesAsync();
        var invoice = await CreateInvoiceWithItemAsync(world, amount: 7);

        using var response = await world.SendAsync(
            HttpMethod.Patch, $"/api/invoices/{invoice}", _invoiceWriter, body: new JsonObject { ["net_total"] = 1 });

        await ShouldBeRollupRefusalAsync(response);
        await ShouldHoldTotalsAsync(world, invoice, net: 7, gross: 9);
    }

    /// <summary>
    /// A <c>PUT</c> naming the rollup is refused on both of its branches: replacing a row that exists, and creating
    /// one at a free id.
    /// </summary>
    [Fact]
    public async Task A_put_naming_a_rollup_is_403_forbidden_on_both_branches()
    {
        await using var world = await StartRollupInvoicesAsync();
        var invoice = await CreateInvoiceWithItemAsync(world, amount: 7);
        using var replaced = await world.SendAsync(
            HttpMethod.Put, $"/api/invoices/{invoice}", _invoiceWriter, body: ForgedInvoice());
        using var created = await world.SendAsync(
            HttpMethod.Put, $"/api/invoices/{Guid.NewGuid()}", _invoiceWriter, body: ForgedInvoice());

        await ShouldBeRollupRefusalAsync(replaced);
        await ShouldBeRollupRefusalAsync(created);
        await ShouldHoldTotalsAsync(world, invoice, net: 7, gross: 9);
        (await world.CountRowsAsync("invoices")).ShouldBe(1, "the create branch stored no second invoice");
    }

    /// <summary>
    /// A batch row naming the rollup is refused as a row — <c>403</c> <c>forbidden</c>, the violation pointing at
    /// that row — and the batch writes nothing, on the create and the update route alike.
    /// </summary>
    [Fact]
    public async Task A_batch_row_naming_a_rollup_is_403_forbidden_and_named_by_its_pointer()
    {
        await using var world = await StartRollupInvoicesAsync();
        var invoice = await CreateInvoiceWithItemAsync(world, amount: 7);

        using var created = await world.SendAsync(
            HttpMethod.Post, "/api/invoices/batch", _invoiceWriter,
            body: BatchOf(new JsonObject { ["vat"] = 1 }, new JsonObject { ["vat"] = 1, ["net_total"] = 1 }));
        using var updated = await world.SendAsync(
            HttpMethod.Patch, "/api/invoices/batch", _invoiceWriter,
            body: BatchOf(new JsonObject { ["id"] = invoice, ["net_total"] = 1 }));

        await ShouldBeRollupRowRefusalAsync(created, "/rows/1");
        await ShouldBeRollupRowRefusalAsync(updated, "/rows/0");
        (await world.CountRowsAsync("invoices")).ShouldBe(1, "a refused batch commits no row");
        await ShouldHoldTotalsAsync(world, invoice, net: 7, gross: 9);
    }

    /// <summary>
    /// The maintainer is not the caller: after every refused write the rollup still follows its children, because
    /// the recompute writes the column from inside the child write's own transaction and never through the guard.
    /// </summary>
    [Fact]
    public async Task The_rollup_still_follows_its_children_after_every_refused_write()
    {
        await using var world = await StartRollupInvoicesAsync();
        var invoice = await CreateInvoiceWithItemAsync(world, amount: 7);
        using var refused = await world.SendAsync(
            HttpMethod.Patch, $"/api/invoices/{invoice}", _invoiceWriter, body: new JsonObject { ["net_total"] = 1 });

        await CreateItemAsync(world, invoice, amount: 5);

        refused.StatusCode.ShouldBe(HttpStatusCode.Forbidden, await refused.ReadTextAsync());
        await ShouldHoldTotalsAsync(world, invoice, net: 12, gross: 14);
    }

    private Task<AlvoApiWorld> StartRollupInvoicesAsync() =>
        AlvoApiWorld.FromDescriptorAsync(
            "rollup-invoices.alvo.json", [_invoiceWriter], new AlvoApiWorldSetup(MapAlvoProblemDetails: true), Engine);

    private static async Task<Guid> CreateInvoiceWithItemAsync(AlvoApiWorld world, int amount)
    {
        using var created = await world.SendAsync(
            HttpMethod.Post, "/api/invoices", _invoiceWriter, body: new JsonObject { ["vat"] = 2 });
        created.StatusCode.ShouldBe(HttpStatusCode.Created, await created.ReadTextAsync());
        var invoice = (await created.ReadJsonObjectAsync())["id"]!.GetValue<Guid>();
        await CreateItemAsync(world, invoice, amount);

        return invoice;
    }

    private static async Task CreateItemAsync(AlvoApiWorld world, Guid invoice, int amount)
    {
        using var item = await world.SendAsync(
            HttpMethod.Post, "/api/invoice_items", _invoiceWriter,
            body: new JsonObject { ["invoice_id"] = invoice, ["amount"] = amount });
        item.StatusCode.ShouldBe(HttpStatusCode.Created, await item.ReadTextAsync());
    }

    private static JsonObject ForgedInvoice() => new() { ["vat"] = 2, ["net_total"] = 1 };

    private static JsonObject BatchOf(params JsonObject[] rows) => new() { ["rows"] = new JsonArray(rows) };

    private static async Task ShouldBeRollupRefusalAsync(HttpResponseMessage response)
    {
        response.StatusCode.ShouldBe(HttpStatusCode.Forbidden, await response.ReadTextAsync());
        (await response.ReadProblemTypeAsync()).ShouldBe(AlvoProblemTypes.Forbidden);
        var detail = await response.ReadProblemDetailAsync();
        detail.ShouldContain("net_total");
        detail.ShouldContain("rollup");
    }

    private static async Task ShouldBeRollupRowRefusalAsync(HttpResponseMessage response, string pointer)
    {
        response.StatusCode.ShouldBe(HttpStatusCode.Forbidden, await response.ReadTextAsync());
        (await response.ReadProblemTypeAsync()).ShouldBe(AlvoProblemTypes.Forbidden);
        (await response.ReadViolationsAsync()).ShouldContain((pointer, "forbidden"));
        (await response.ReadTextAsync()).ShouldContain("net_total");
    }

    private static async Task ShouldHoldTotalsAsync(AlvoApiWorld world, Guid invoice, long net, long gross)
    {
        using var read = await world.SendAsync(HttpMethod.Get, $"/api/invoices/{invoice}", _invoiceWriter);
        read.StatusCode.ShouldBe(HttpStatusCode.OK, await read.ReadTextAsync());
        var row = await read.ReadJsonObjectAsync();
        row["net_total"]!.GetValue<long>().ShouldBe(net, "the rollup is the sum of the children, not a caller's number");
        row["gross_total"]!.GetValue<long>().ShouldBe(gross, "and the computed field reading it follows the real total");
    }
}
