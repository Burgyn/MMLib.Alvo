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
    /// <summary>
    /// The writing caller. <c>authenticated</c> as well as <c>admin</c>, because the registry's <c>get</c> and
    /// <c>list</c> rules name the first while its write rules name the second — a key holding only
    /// <c>admin</c> can create a row and then cannot read it back, which reads as a product bug and is not.
    /// </summary>
    private static readonly TestApiKey _admin =
        new("admin-key", ["admin", "authenticated"], ["*:read", "*:write"]);

    /// <summary>A key that may read and not write, so the batch is refused before a byte of it is read.</summary>
    private static readonly TestApiKey _reader =
        new("reader-key", ["admin", "authenticated"], ["*:read"]);

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
    /// The field bound is spent PER ROW, so a batch of many small rows reaches the row bound rather than the
    /// field bound.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>This was silently false until a reviewer caught it.</b> The shape scan counted property names once
    /// across the whole body, so a batch of N rows with K fields spent <c>1 + N·K</c> of one shared budget:
    /// at the defaults a five-field entity was refused at about a hundred rows with "too many fields" —
    /// advice about the wrong thing by <see cref="AlvoApiOptions.MaxPayloadKeys"/>'s own definition — and
    /// <see cref="AlvoApiOptions.MaxBatchRows"/> was unreachable over HTTP for any entity with more than one
    /// field.
    /// </para>
    /// <para>
    /// The bounds are set deliberately far apart here: three fields per row and a key bound of four, so
    /// twenty rows spend 61 keys against a shared budget and 3 against a per-row one. A batch that is
    /// refused proves the counter is shared; a batch that is answered proves it is not.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task The_field_bound_is_spent_per_row_not_across_the_batch()
    {
        await using var world = await AlvoApiWorld.VehicleRegistryAsync(
            [_admin],
            new AlvoApiWorldSetup(ConfigureApi: options =>
            {
                options.MaxPayloadKeys = 4;
                options.MaxBatchRows = 100;
            }));

        var rows = Enumerable.Range(0, 20)
            .Select(ordinal => new JsonObject
            {
                ["name"] = $"Owner {ordinal}",
                ["email"] = $"owner{ordinal}@example.test",
                ["phone"] = "+421000000000",
            })
            .ToArray();

        using var response = await world.SendAsync(
            HttpMethod.Post, "/api/owners/batch", _admin, body: Rows(rows));

        response.StatusCode.ShouldBe(
            HttpStatusCode.OK,
            $"three fields a row is inside a per-row bound of four: {await response.ReadTextAsync()}");
        (await world.CountRowsAsync("owners")).ShouldBe(20);
    }

    /// <summary>
    /// The row bound counts every element, whatever its type — a body of nulls is refused as too many rows,
    /// not walked into a tree and refused one element at a time.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The first version of the scan counted only the shapes a valid row can take</b> — an object, a
    /// string, a number — so <c>null</c>, <c>true</c>, <c>false</c> and a nested array were never counted at
    /// all. A body of two hundred thousand nulls fitted inside <c>MaxRequestBodyBytes</c>, scanned as zero
    /// rows, and was then parsed in full and refused one element at a time: precisely the work the bound
    /// exists to refuse, reachable by any caller who may write.
    /// </para>
    /// <para>
    /// A bound that counts only well-formed input is not a bound. The refusal for a row of the wrong shape
    /// is the reader's job and happens after this one has already been paid.
    /// </para>
    /// </remarks>
    [Theory]
    [InlineData("null")]
    [InlineData("true")]
    [InlineData("[]")]
    public async Task The_row_bound_counts_an_element_of_any_type(string element)
    {
        await using var world = await AlvoApiWorld.VehicleRegistryAsync(
            [_admin], new AlvoApiWorldSetup(ConfigureApi: options => options.MaxBatchRows = 3));

        using var response = await world.SendRawAsync(
            HttpMethod.Post,
            "/api/owners/batch",
            _admin,
            content: AlvoApiWorld.RawJson($"{{\"rows\":[{string.Join(",", Enumerable.Repeat(element, 10))}]}}"));

        response.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity, await response.ReadTextAsync());
        (await response.ReadViolationsAsync()).Select(violation => violation.Code)
            .ShouldContain("batch-too-many-rows", "ten elements is past a bound of three, whatever they are");
    }

    /// <summary>A row past the per-row field bound is still refused, so the bound is real rather than absent.</summary>
    /// <remarks>
    /// The counterweight to the fact above: an implementation that stopped counting names altogether would
    /// satisfy it, and this is what tells "scoped per row" from "not enforced".
    /// </remarks>
    [Fact]
    public async Task A_row_past_the_field_bound_is_still_refused()
    {
        await using var world = await AlvoApiWorld.VehicleRegistryAsync(
            [_admin], new AlvoApiWorldSetup(ConfigureApi: options => options.MaxPayloadKeys = 2));

        using var response = await world.SendAsync(
            HttpMethod.Post, "/api/owners/batch", _admin,
            body: Rows(new JsonObject
            {
                ["name"] = "Too wide",
                ["email"] = "wide@example.test",
                ["phone"] = "+421000000000",
            }));

        response.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity, await response.ReadTextAsync());
        (await response.ReadViolationsAsync()).Select(violation => violation.Code)
            .ShouldContain("body-too-many-fields");
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

    /// <summary>
    /// A row the <b>port</b> refuses answers 403 with a violation naming it — not 422.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The two refusal channels answer different statuses and this is the one that is easy to get wrong. The
    /// reader refuses what the declared shape refuses and answers 422; the port refuses what policy refuses
    /// and answers 403, because a caller refused by policy on one row of a single-row route gets a 403 and
    /// telling them 422 would send them to fix a shape that is not wrong.
    /// </para>
    /// <para>
    /// A 403 carrying <c>violations</c> is new to this API — the single-row 403 carries only a message — and
    /// it is the whole reason a batch's refusal is usable: the pointer is what a caller repairs.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task A_row_the_port_refuses_is_403_and_names_the_row()
    {
        await using var world = await AlvoApiWorld.VehicleRegistryAsync([_admin]);
        var real = await CreatedIdAsync(world, "Real");

        using var response = await world.SendAsync(
            HttpMethod.Patch, "/api/owners/batch", _admin,
            body: Rows(
                new JsonObject { ["id"] = real, ["name"] = "Renamed" },
                new JsonObject { ["id"] = Guid.NewGuid(), ["name"] = "Renamed" }));

        response.StatusCode.ShouldBe(HttpStatusCode.Forbidden, await response.ReadTextAsync());
        (await response.ReadViolationsAsync()).Select(violation => violation.Pointer).ShouldContain("/rows/1");
        (await response.ReadProblemTypeAsync()).ShouldBe(AlvoProblemTypes.Forbidden);

        using var unchanged = await world.SendAsync(HttpMethod.Get, $"/api/owners/{real}", _admin);
        unchanged.StatusCode.ShouldBe(HttpStatusCode.OK, await unchanged.ReadTextAsync());
        (await unchanged.ReadJsonObjectAsync())["name"]!.GetValue<string>().ShouldBe(
            "Real", "a refused batch writes nothing");
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
