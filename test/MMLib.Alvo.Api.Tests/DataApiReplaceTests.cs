using System.Net;
using System.Text.Json.Nodes;

namespace MMLib.Alvo.Api.Tests;

/// <summary>
/// The create-or-replace route over HTTP: one verb on the item path, two branches, two success codes.
/// </summary>
/// <remarks>
/// The port's own suite proves what a replacement <em>is</em> — the branch, the write check on both sides of
/// it, the whole-row semantics. This file proves the things only the transport can get wrong: which status
/// each branch answers, that the caller needs both operations before a byte of body is read, and that the
/// published document actually describes the route — including the two parameter switches whose miss is
/// silent rather than an exception.
/// </remarks>
public class DataApiReplaceTests
{
    /// <summary>
    /// The writing caller. <c>authenticated</c> as well as <c>admin</c>, because the registry's <c>get</c>
    /// rule names the first while its write rules name the second.
    /// </summary>
    private static readonly TestApiKey _admin =
        new("admin-key", ["admin", "authenticated"], ["*:read", "*:write"]);

    /// <summary>A key that may read and not write, so the request is refused before the body is read.</summary>
    private static readonly TestApiKey _reader =
        new("reader-key", ["admin", "authenticated"], ["*:read"]);

    /// <summary>A replace on a free id answers 201 and a Location naming the id the caller chose.</summary>
    [Fact]
    public async Task A_put_on_a_free_id_answers_201_with_a_location_naming_that_id()
    {
        await using var world = await AlvoApiWorld.VehicleRegistryAsync([_admin]);
        var id = Guid.NewGuid();

        using var response = await world.SendAsync(
            HttpMethod.Put, $"/api/owners/{id}", _admin, body: Owner("New"));

        response.StatusCode.ShouldBe(HttpStatusCode.Created);

        // The WHOLE value, not just its tail. `EndsWith(id)` is satisfied by
        // "/api/owners/{id:guid}/<guid>" too — which is what this header was before the route template's
        // trailing parameter was dropped, and a path that matches nothing.
        response.Headers.Location!.ToString().ShouldBe($"/api/owners/{id}");
    }

    /// <summary>The Location a create-or-replace returns is a path a client can actually follow.</summary>
    /// <remarks>
    /// <b>The header is built from the matched endpoint's own route, which for this verb is the item
    /// template.</b> Appending the id there yields <c>/api/owners/{id:guid}/&lt;guid&gt;</c> — well-formed,
    /// containing the id, ending with it, and matching no route at all. So the fact follows it rather than
    /// inspecting it: a GET on the returned path must answer the row that was just created.
    /// </remarks>
    [Fact]
    public async Task The_location_a_put_returns_leads_to_the_row_it_created()
    {
        await using var world = await AlvoApiWorld.VehicleRegistryAsync([_admin]);
        var id = Guid.NewGuid();

        using var created = await world.SendAsync(
            HttpMethod.Put, $"/api/owners/{id}", _admin, body: Owner("Followed"));
        using var followed = await world.SendAsync(
            HttpMethod.Get, created.Headers.Location!.ToString(), _admin);

        followed.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await followed.ReadJsonObjectAsync())["name"]!.GetValue<string>().ShouldBe("Followed");
    }

    /// <summary>A replace on an existing row answers 200 and no Location.</summary>
    /// <remarks>
    /// The <c>Location</c> half is the point: a header that appeared on both branches would tell a client a
    /// row was created every time it was merely replaced.
    /// </remarks>
    [Fact]
    public async Task A_put_on_an_existing_row_answers_200_and_no_location()
    {
        await using var world = await AlvoApiWorld.VehicleRegistryAsync([_admin]);
        var id = await CreatedIdAsync(world, "First");

        using var response = await world.SendAsync(
            HttpMethod.Put, $"/api/owners/{id}", _admin, body: Owner("Second"));

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        response.Headers.Location.ShouldBeNull();
        (await response.ReadJsonObjectAsync())["name"]!.GetValue<string>().ShouldBe("Second");
    }

    /// <summary>A caller who may not write is refused, on the branch that would have created.</summary>
    /// <remarks>
    /// Asserted against a free id on purpose: the route needs <c>create</c> as well as <c>update</c>, and a
    /// refusal read off an existing row would not tell the two apart.
    /// </remarks>
    [Fact]
    public async Task A_caller_who_may_not_write_is_refused()
    {
        await using var world = await AlvoApiWorld.VehicleRegistryAsync([_admin, _reader]);

        using var response = await world.SendAsync(
            HttpMethod.Put, $"/api/owners/{Guid.NewGuid()}", _reader, body: Owner("Nope"));

        response.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
    }

    /// <summary>The published PUT declares its id parameter and the headers it accepts.</summary>
    /// <remarks>
    /// <b>The only fact that catches the two silent switches.</b> <c>DataApiParameters.AddressesOneRow</c> is
    /// an <c>is</c> pattern and <c>HeaderNames</c> is a switch whose default arm is empty — a kind missing
    /// from either is <see langword="false"/> and <c>[]</c>, not an exception. So a missing arm publishes a
    /// <c>PUT</c> with no <c>{id}</c> parameter and neither <c>If-Match</c> nor <c>Idempotency-Key</c>, the
    /// route-count facts stay green, and the document quietly disagrees with the endpoint. PR-H shipped
    /// exactly this bug for the batch kinds.
    /// </remarks>
    [Fact]
    public async Task The_published_put_declares_its_id_parameter_and_its_headers()
    {
        await using var world = await AlvoApiWorld.VehicleRegistryAsync(
            [_admin], new AlvoApiWorldSetup(MapOpenApiDocument: true));

        var document = await world.OpenApiDocumentAsync();
        var names = ParameterNames(document, "/api/owners/{id}", "put");

        names.ShouldContain("id", "the path template names it, so the operation must declare it");
        names.ShouldContain("If-Match");
        names.ShouldContain("Idempotency-Key");
    }

    /// <summary>The control for the fact above: PATCH on the same path declares the same three.</summary>
    /// <remarks>
    /// Without it, a resolver that returned every parameter for every operation would satisfy the assertion
    /// above while proving nothing about the switches at all.
    /// </remarks>
    [Fact]
    public async Task The_published_patch_declares_the_same_three_so_the_comparison_means_something()
    {
        await using var world = await AlvoApiWorld.VehicleRegistryAsync(
            [_admin], new AlvoApiWorldSetup(MapOpenApiDocument: true));

        var document = await world.OpenApiDocumentAsync();
        var put = ParameterNames(document, "/api/owners/{id}", "put");
        var patch = ParameterNames(document, "/api/owners/{id}", "patch");

        put.ShouldBe(patch, ignoreOrder: true, "both address one row and both accept the same two headers");
    }

    /// <summary>Every operation the document publishes carries an id of its own.</summary>
    /// <remarks>
    /// <c>ToWireName</c>'s default arm falls through to the operation's spelling, so a kind without its own
    /// arm would spell <c>replace</c> as <c>update</c> — two routes minting one <c>operationId</c>, and one
    /// route's prose published for the other.
    /// </remarks>
    [Fact]
    public async Task Every_published_operation_id_is_distinct()
    {
        await using var world = await AlvoApiWorld.VehicleRegistryAsync(
            [_admin], new AlvoApiWorldSetup(MapOpenApiDocument: true));

        var document = await world.OpenApiDocumentAsync();
        var ids = OperationIds(document);

        ids.ShouldBeUnique();
        ids.ShouldContain("owners.replace");
    }

    private static IReadOnlyList<string> ParameterNames(JsonObject document, string path, string verb)
    {
        var operation = document["paths"]![path]![verb]!;
        return
        [
            .. operation["parameters"]!.AsArray()
                .Select(parameter => Resolved(document, parameter!)["name"]!.GetValue<string>()),
        ];
    }

    /// <summary>
    /// A parameter as declared, following the one <c>$ref</c> the document uses for the shared ones.
    /// </summary>
    private static JsonNode Resolved(JsonObject document, JsonNode parameter)
    {
        if (parameter["$ref"]?.GetValue<string>() is not { } reference)
        {
            return parameter;
        }

        var name = reference[(reference.LastIndexOf('/') + 1)..];
        return document["components"]!["parameters"]![name]!;
    }

    private static IReadOnlyList<string> OperationIds(JsonObject document) =>
    [
        .. document["paths"]!.AsObject()
            .SelectMany(path => path.Value!.AsObject())
            .Select(operation => operation.Value!["operationId"]!.GetValue<string>()),
    ];

    private static JsonObject Owner(string name) => new() { ["name"] = name };

    private static async Task<Guid> CreatedIdAsync(AlvoApiWorld world, string name)
    {
        using var response = await world.SendAsync(HttpMethod.Post, "/api/owners", _admin, body: Owner(name));

        return (await response.ReadJsonObjectAsync())["id"]!.GetValue<Guid>();
    }
}
