using System.Net;
using System.Text.Json.Nodes;

namespace MMLib.Alvo.Api.Tests;

/// <summary>
/// HX-10 Ruling 44 on every engine: a built-in's refusal is the caller's 422 <c>expression-failed</c> only when every field
/// it read was in this request's body. <c>new.</c> also reaches the stored row an update left alone, and a delete sends no
/// body at all, so those — and a refusal that reads only a literal or <c>@user</c> — are 500 <c>function-failed</c>.
/// </summary>
public abstract partial class DataApiEngineTests
{
    private static readonly TestApiKey _parcelsWriter = new("parcels-writer", ["writer"], ["parcels:read", "parcels:write"]);

    /// <summary>The weight the PATCH sends is the caller's: 422, and the stored weight stays (Ruling 44).</summary>
    [Fact]
    public async Task An_update_refusing_a_field_it_sent_is_422_expression_failed_on_every_engine()
    {
        await using var world = await StartExpressionOriginAsync();
        var id = await CreateParcelAsync(world, weight: "x");

        using var refused = await world.SendAsync(HttpMethod.Patch, $"/api/parcels/{id}", _parcelsWriter, body: new JsonObject { ["weight_text"] = "y" });

        await ShouldBeAsync(refused, HttpStatusCode.UnprocessableEntity, "expression-failed");
        (await ParcelAsync(world, id))["weight_text"]!.GetValue<string>().ShouldBe("x");
    }

    /// <summary>
    /// The PATCH sends only a note; <c>new.weight_text</c> reads the stored row, which the caller cannot fix by sending
    /// other data: 500 <c>function-failed</c>, and nothing written (Ruling 44).
    /// </summary>
    [Fact]
    public async Task An_update_refusing_a_stored_field_it_did_not_send_is_500_function_failed_on_every_engine()
    {
        await using var world = await StartExpressionOriginAsync();
        var id = await CreateParcelAsync(world, weight: "x");

        using var refused = await world.SendAsync(HttpMethod.Patch, $"/api/parcels/{id}", _parcelsWriter, body: new JsonObject { ["note"] = "fragile" });

        await ShouldBeAsync(refused, HttpStatusCode.InternalServerError, "function-failed");
        (await ParcelAsync(world, id))["note"].ShouldBeNull();
    }

    /// <summary>
    /// The note is sent, but the refusing call reads only a literal or <c>@user</c> — the descriptor author's or the identity
    /// provider's, never the caller's: 500 (Ruling 44, Task 3 review m2).
    /// </summary>
    [Theory]
    [InlineData("literal")]
    [InlineData("identity")]
    public async Task An_update_whose_refusal_reads_no_field_it_sent_is_500_function_failed_on_every_engine(string note)
    {
        await using var world = await StartExpressionOriginAsync();
        var id = await CreateParcelAsync(world, weight: "12");

        using var refused = await world.SendAsync(HttpMethod.Patch, $"/api/parcels/{id}", _parcelsWriter, body: new JsonObject { ["note"] = note });

        await ShouldBeAsync(refused, HttpStatusCode.InternalServerError, "function-failed");
    }

    /// <summary>A delete sends no body, so a before-delete refusal reads only the stored row: 500, and the row stays (Ruling 44).</summary>
    [Fact]
    public async Task A_delete_whose_condition_a_built_in_refuses_is_500_function_failed_and_keeps_the_row_on_every_engine()
    {
        await using var world = await StartExpressionOriginAsync();
        var id = await CreateParcelAsync(world, weight: "x");

        using var refused = await world.SendAsync(HttpMethod.Delete, $"/api/parcels/{id}", _parcelsWriter);

        await ShouldBeAsync(refused, HttpStatusCode.InternalServerError, "function-failed");
        (await world.CountRowsAsync("parcels")).ShouldBe(1);
    }

    /// <summary>A batch row is judged by the fields every row of the batch carried (Ruling 44).</summary>
    [Fact]
    public async Task A_batch_update_answers_422_only_for_a_field_its_rows_sent_on_every_engine()
    {
        await using var world = await StartExpressionOriginAsync();
        var id = await CreateParcelAsync(world, weight: "x");

        using var sent = await world.SendAsync(HttpMethod.Patch, "/api/parcels/batch", _parcelsWriter, body: Batch(new JsonObject { ["id"] = id, ["weight_text"] = "y" }));
        using var unsent = await world.SendAsync(HttpMethod.Patch, "/api/parcels/batch", _parcelsWriter, body: Batch(new JsonObject { ["id"] = id, ["note"] = "n" }));

        await ShouldBeAsync(sent, HttpStatusCode.UnprocessableEntity, "expression-failed");
        await ShouldBeAsync(unsent, HttpStatusCode.InternalServerError, "function-failed");
    }

    /// <summary>
    /// Two rows that each send one field: the refusal of a weight is the caller's only if every row sent the weight, so
    /// the batch is 500 in either order and writes neither row — even when the weight row comes first and the refused
    /// value is that row's own sent one, not the stored weight. A union of the rows' fields, the first row's or the last
    /// row's would each answer 422 in at least one of the orders (Ruling 44, Task 4 review I1).
    /// </summary>
    /// <param name="weightFirst">Whether the row that sends the weight comes first.</param>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task A_batch_whose_rows_sent_different_fields_is_judged_by_the_fields_every_row_sent_on_every_engine(bool weightFirst)
    {
        await using var world = await StartExpressionOriginAsync();
        var weighed = await CreateParcelAsync(world, weight: "x");
        var noted = await CreateParcelAsync(world, weight: "x");
        var weightRow = new JsonObject { ["id"] = weighed, ["weight_text"] = "y" };
        var noteRow = new JsonObject { ["id"] = noted, ["note"] = "n" };
        var rows = weightFirst ? new JsonArray(weightRow, noteRow) : new JsonArray(noteRow, weightRow);

        using var refused = await world.SendAsync(HttpMethod.Patch, "/api/parcels/batch", _parcelsWriter, body: new JsonObject { ["rows"] = rows });

        await ShouldBeAsync(refused, HttpStatusCode.InternalServerError, "function-failed");
        foreach (var id in new[] { weighed, noted })
        {
            var parcel = await ParcelAsync(world, id);
            parcel["weight_text"]!.GetValue<string>().ShouldBe("x");
            parcel["note"].ShouldBeNull();
        }
    }

    /// <summary>
    /// The create omits the label, so <c>new.label_text</c> reads the descriptor's default — the author's value, not the
    /// caller's: 500 <c>function-failed</c>, and no row; the same label sent is 422 (Ruling 44, Task 4 review m1).
    /// </summary>
    [Fact]
    public async Task A_create_refusing_a_default_it_did_not_send_is_500_function_failed_on_every_engine()
    {
        await using var world = await StartExpressionOriginAsync();

        using var refused = await world.SendAsync(HttpMethod.Post, "/api/parcels", _parcelsWriter, body: new JsonObject { ["code"] = "defaulted", ["weight_text"] = "12" });

        using var sent = await world.SendAsync(
            HttpMethod.Post, "/api/parcels", _parcelsWriter, body: new JsonObject { ["code"] = "defaulted", ["weight_text"] = "12", ["label_text"] = "x" });

        await ShouldBeAsync(refused, HttpStatusCode.InternalServerError, "function-failed");
        await ShouldBeAsync(sent, HttpStatusCode.UnprocessableEntity, "expression-failed");
        (await world.CountRowsAsync("parcels")).ShouldBe(0);
    }

    private Task<AlvoApiWorld> StartExpressionOriginAsync() =>
        AlvoApiWorld.FromDescriptorAsync("expression-origin.alvo.json", [_parcelsWriter], new AlvoApiWorldSetup(MapAlvoProblemDetails: true), Engine);

    private static async Task<Guid> CreateParcelAsync(AlvoApiWorld world, string weight)
    {
        using var created = await world.SendAsync(HttpMethod.Post, "/api/parcels", _parcelsWriter, body: new JsonObject { ["code"] = "P1", ["weight_text"] = weight });
        created.StatusCode.ShouldBe(HttpStatusCode.Created, await created.ReadTextAsync());
        return (await created.ReadJsonObjectAsync())["id"]!.GetValue<Guid>();
    }

    private static async Task<JsonObject> ParcelAsync(AlvoApiWorld world, Guid id)
    {
        using var read = await world.SendAsync(HttpMethod.Get, $"/api/parcels/{id}", _parcelsWriter);
        return await read.ReadJsonObjectAsync();
    }

    private static JsonObject Batch(JsonObject row) => new() { ["rows"] = new JsonArray(row) };

    private static async Task ShouldBeAsync(HttpResponseMessage response, HttpStatusCode status, string slug)
    {
        response.StatusCode.ShouldBe(status, await response.ReadTextAsync());
        (await response.ReadJsonObjectAsync())["type"]!.GetValue<string>().ShouldBe($"https://alvo.dev/errors/{slug}");
    }
}
