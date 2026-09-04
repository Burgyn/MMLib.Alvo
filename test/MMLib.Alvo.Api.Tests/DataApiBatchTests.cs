using MMLib.Alvo.Api.Internal;
using System.Net;
using System.Text.Json.Nodes;

namespace MMLib.Alvo.Api.Tests;

/// <summary>
/// The batch route over HTTP: one path, three verbs, and the refusals a caller has to be able to act on.
/// </summary>
/// <remarks>
/// The port's own suite proves what a batch <em>is</em> — one transaction, per-row policy, every offending
/// row reported. This file proves the things only the transport can get wrong: which verb reaches which
/// operation, that the bound is spent before the rows are, that a port refusal becomes a violation pointing
/// at the row the caller sent, and that a <c>DELETE</c> whose body an intermediary stripped is refused
/// rather than answered.
/// </remarks>
public class DataApiBatchTests
{
    private static readonly TestApiKey _admin = new("admin-key", ["admin"], ["*:read", "*:write"]);

    /// <summary>A key that may read and not write, so the batch is refused before a byte of it is read.</summary>
    private static readonly TestApiKey _reader = new("reader-key", ["admin"], ["*:read"]);

    /// <summary>A batch of three creates three rows and answers them in the order they were sent.</summary>
    [Fact]
    public async Task A_batch_of_three_creates_three_rows()
    {
        await using var world = await AlvoApiWorld.VehicleRegistryAsync([_admin]);

        using var response = await world.SendAsync(
            HttpMethod.Post, "/api/owners/batch", _admin,
            body: Rows(Owner("First"), Owner("Second"), Owner("Third")));

        response.StatusCode.ShouldBe(HttpStatusCode.OK, await response.ReadTextAsync());
        var body = await response.ReadJsonObjectAsync();
        body["affected"]!.GetValue<int>().ShouldBe(3);
        body["items"]!.AsArray().Select(row => row!["name"]!.GetValue<string>()).ShouldBe(
            ["First", "Second", "Third"], ignoreOrder: false, customMessage: "in the order they were sent");
        (await world.CountRowsAsync("owners")).ShouldBe(3);
    }

    /// <summary>
    /// A batch whose last row is invalid creates none, and the violation points at the row the caller sent.
    /// </summary>
    /// <remarks>
    /// The pointer is the whole reason this is not just the port's fact repeated: the port reports an index,
    /// and <c>/rows/2/name</c> is what a caller can actually resolve against the body they sent.
    /// </remarks>
    [Fact]
    public async Task A_batch_whose_last_row_is_invalid_creates_none_and_points_at_it()
    {
        await using var world = await AlvoApiWorld.VehicleRegistryAsync([_admin]);

        using var response = await world.SendAsync(
            HttpMethod.Post, "/api/owners/batch", _admin,
            body: Rows(Owner("First"), Owner("Second"), new JsonObject { ["name"] = new string('n', 5000) }));

        response.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity, await response.ReadTextAsync());
        (await response.ReadViolationsAsync()).Select(violation => violation.Pointer).ShouldContain(
            pointer => pointer.StartsWith("/rows/2", StringComparison.Ordinal),
            customMessage: "the violation must name the row the caller sent");
        (await world.CountRowsAsync("owners")).ShouldBe(0, "a batch commits every row or none");
    }

    /// <summary>A batch past the row bound is refused naming the row bound, not the field bound.</summary>
    /// <remarks>
    /// The distinction is the point. <c>MaxPayloadKeys</c> counts property names at every depth, so without a
    /// row bound a batch of a hundred rows would be refused as "too many fields" — advice about the wrong
    /// thing entirely.
    /// </remarks>
    [Fact]
    public async Task A_batch_past_the_row_bound_is_refused_naming_the_row_bound()
    {
        await using var world = await AlvoApiWorld.VehicleRegistryAsync(
            [_admin], new AlvoApiWorldSetup(ConfigureApi: options => options.MaxBatchRows = 2));

        using var response = await world.SendAsync(
            HttpMethod.Post, "/api/owners/batch", _admin,
            body: Rows(Owner("First"), Owner("Second"), Owner("Third")));

        response.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity, await response.ReadTextAsync());
        (await response.ReadViolationsAsync()).Select(violation => violation.Code).ShouldContain("batch-too-many-rows");
        (await world.CountRowsAsync("owners")).ShouldBe(0);
    }

    /// <summary>
    /// An empty batch is refused on every verb — and on the <c>DELETE</c> that is what turns a body an
    /// intermediary stripped into a 422 rather than a silent success.
    /// </summary>
    [Theory]
    [InlineData("POST")]
    [InlineData("PATCH")]
    [InlineData("DELETE")]
    public async Task An_empty_batch_is_refused_on_every_verb(string method)
    {
        await using var world = await AlvoApiWorld.VehicleRegistryAsync([_admin]);

        using var response = await world.SendAsync(
            new HttpMethod(method), "/api/owners/batch", _admin,
            body: new JsonObject { ["rows"] = new JsonArray() });

        response.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity, await response.ReadTextAsync());
        (await response.ReadViolationsAsync()).Select(violation => violation.Code).ShouldContain("empty-batch");
    }

    /// <summary>
    /// A batch on an entity this caller may not write is refused before the store is touched — and the
    /// refusal is the policy's, not a per-row report, so it discloses nothing about the rows they sent.
    /// </summary>
    [Fact]
    public async Task A_batch_the_policy_refuses_is_403_and_names_no_row()
    {
        await using var world = await AlvoApiWorld.VehicleRegistryAsync([_admin, _reader]);

        using var response = await world.SendAsync(
            HttpMethod.Post, "/api/owners/batch", _reader, body: Rows(Owner("First"), Owner("Second")));

        response.StatusCode.ShouldBe(HttpStatusCode.Forbidden, await response.ReadTextAsync());
        (await response.ReadTextAsync()).Contains("/rows/", StringComparison.Ordinal).ShouldBeFalse(
            "a refusal decided before any row was read must not name one");
        (await world.CountRowsAsync("owners")).ShouldBe(0);
    }

    /// <summary>One key replayed writes no second set of rows.</summary>
    [Fact]
    public async Task A_replayed_batch_writes_no_second_set_of_rows()
    {
        await using var world = await AlvoApiWorld.VehicleRegistryAsync([_admin]);
        var headers = new Dictionary<string, string> { ["Idempotency-Key"] = "batch-1" };
        var body = Rows(Owner("First"), Owner("Second"));

        using var first = await world.SendAsync(
            HttpMethod.Post, "/api/owners/batch", _admin, body: body, headers: headers);
        using var replay = await world.SendAsync(
            HttpMethod.Post, "/api/owners/batch", _admin, body: body, headers: headers);

        first.StatusCode.ShouldBe(HttpStatusCode.OK, await first.ReadTextAsync());
        replay.StatusCode.ShouldBe(HttpStatusCode.OK, await replay.ReadTextAsync());
        (await world.CountRowsAsync("owners")).ShouldBe(2, "two rows, not four");
    }

    /// <summary>
    /// The same key against a different set of rows is a 409 rather than a replay of the first — the
    /// fingerprint covers every row, because a batch is one request.
    /// </summary>
    [Fact]
    public async Task One_key_against_a_different_batch_is_a_conflict()
    {
        await using var world = await AlvoApiWorld.VehicleRegistryAsync([_admin]);
        var headers = new Dictionary<string, string> { ["Idempotency-Key"] = "batch-2" };

        using var first = await world.SendAsync(
            HttpMethod.Post, "/api/owners/batch", _admin, body: Rows(Owner("First")), headers: headers);
        using var different = await world.SendAsync(
            HttpMethod.Post, "/api/owners/batch", _admin,
            body: Rows(Owner("First"), Owner("Second")), headers: headers);

        first.StatusCode.ShouldBe(HttpStatusCode.OK, await first.ReadTextAsync());
        different.StatusCode.ShouldBe(HttpStatusCode.Conflict, await different.ReadTextAsync());
        (await world.CountRowsAsync("owners")).ShouldBe(1);
    }

    /// <summary>A batch delete answers 200 with an empty items and a count, not 204.</summary>
    /// <remarks>
    /// It reports on many rows, so a caller correlating the outcome with what they sent needs a body — and
    /// <c>affected</c> is what tells a five-row delete from a refusal, which an empty <c>items</c> could not.
    /// </remarks>
    [Fact]
    public async Task A_batch_delete_answers_a_count_rather_than_no_content()
    {
        await using var world = await AlvoApiWorld.VehicleRegistryAsync([_admin]);
        var first = await CreatedIdAsync(world, "First");
        var second = await CreatedIdAsync(world, "Second");

        using var response = await world.SendAsync(
            HttpMethod.Delete, "/api/owners/batch", _admin,
            body: new JsonObject { ["rows"] = new JsonArray(JsonValue.Create(first), JsonValue.Create(second)) });

        response.StatusCode.ShouldBe(HttpStatusCode.OK, await response.ReadTextAsync());
        var body = await response.ReadJsonObjectAsync();
        body["affected"]!.GetValue<int>().ShouldBe(2);
        body["items"]!.AsArray().Count.ShouldBe(0, "a delete produces no rows");
        (await world.CountRowsAsync("owners")).ShouldBe(0);
    }

    /// <summary>A body that carries no <c>rows</c> array is refused as a shape, not read as an empty batch.</summary>
    [Fact]
    public async Task A_body_with_no_rows_member_is_refused_as_a_shape()
    {
        await using var world = await AlvoApiWorld.VehicleRegistryAsync([_admin]);

        using var response = await world.SendAsync(
            HttpMethod.Post, "/api/owners/batch", _admin, body: Owner("Loose"));

        response.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity, await response.ReadTextAsync());
        (await response.ReadViolationsAsync()).Select(violation => violation.Code).ShouldContain("not-a-batch");
    }

    private static JsonObject Rows(params JsonObject[] rows) =>
        new() { ["rows"] = new JsonArray([.. rows.Select(row => (JsonNode)row)]) };

    private static JsonObject Owner(string name) => new() { ["name"] = name };

    private static async Task<Guid> CreatedIdAsync(AlvoApiWorld world, string name)
    {
        using var response = await world.SendAsync(HttpMethod.Post, "/api/owners", _admin, body: Owner(name));

        return (await response.ReadJsonObjectAsync())["id"]!.GetValue<Guid>();
    }
}
