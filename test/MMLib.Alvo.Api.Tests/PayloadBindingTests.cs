using Microsoft.AspNetCore.Http;
using MMLib.Alvo.Api.Internal;
using MMLib.Alvo.Schema;
using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json.Nodes;

namespace MMLib.Alvo.Api.Tests;

/// <summary>
/// The write path's body reader: the one part of the Data API an <b>unauthenticated</b> request can put
/// to work, since a body is parsed before the port has any say. Its bounds, its refusals, and the one
/// condition it must <em>not</em> treat as a caller error.
/// </summary>
/// <remarks>
/// The bounds are exercised over the live API with the options lowered, rather than by sending a real
/// megabyte: what needs proving is that the endpoint reads
/// <see cref="AlvoApiOptions"/> and refuses, not that a large number is large. The one fact that cannot
/// be reached over HTTP — an applied schema carrying a field type this build cannot map — drives the
/// reader directly, because no descriptor the schema admits can express it.
/// </remarks>
public sealed class PayloadBindingTests
{
    private static readonly TestApiKey _admin = new("admin-key", ["admin", "authenticated"], ["*:read", "*:write"]);

    [Fact]
    public async Task A_body_larger_than_the_configured_maximum_is_refused_and_names_the_bound()
    {
        await using var world = await AlvoApiWorld.VehicleRegistryAsync(
            [_admin], new AlvoApiWorldSetup(api => api.MaxRequestBodyBytes = 64));

        using var response = await world.SendRawAsync(
            HttpMethod.Post, "/api/owners", _admin,
            content: AlvoApiWorld.RawJson($@"{{""name"":""{new string('x', 512)}""}}"));

        response.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);
        (await response.ReadProblemDetailAsync()).ShouldContain("64");
    }

    /// <summary>
    /// The size bound must hold for a body that declares no <c>Content-Length</c> too, or it is a check on
    /// a header rather than on what arrived.
    /// </summary>
    [Fact]
    public async Task A_chunked_body_larger_than_the_configured_maximum_is_refused_as_well()
    {
        await using var world = await AlvoApiWorld.VehicleRegistryAsync(
            [_admin], new AlvoApiWorldSetup(api => api.MaxRequestBodyBytes = 64));

        using var content = Chunked($@"{{""name"":""{new string('x', 512)}""}}");
        using var response = await world.SendRawAsync(HttpMethod.Post, "/api/owners", _admin, content: content);

        response.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);
        content.Headers.ContentLength.ShouldBeNull("or this fact is the Content-Length case again");
    }

    [Fact]
    public async Task A_body_nested_deeper_than_the_configured_maximum_is_refused()
    {
        await using var world = await AlvoApiWorld.VehicleRegistryAsync(
            [_admin], new AlvoApiWorldSetup(api => api.MaxPayloadDepth = 4));

        using var response = await world.SendRawAsync(
            HttpMethod.Post, "/api/owners", _admin, content: AlvoApiWorld.RawJson(Nested(depth: 16)));

        response.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);
    }

    [Fact]
    public async Task A_body_with_more_keys_than_the_configured_maximum_is_refused_and_names_the_bound()
    {
        await using var world = await AlvoApiWorld.VehicleRegistryAsync(
            [_admin], new AlvoApiWorldSetup(api => api.MaxPayloadKeys = 3));

        using var response = await world.SendRawAsync(
            HttpMethod.Post, "/api/owners", _admin, content: AlvoApiWorld.RawJson(WideObject(keys: 10)));

        response.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);
        (await response.ReadProblemDetailAsync()).ShouldContain("3");
    }

    /// <summary>
    /// A wide-but-shallow object is what a depth cap alone misses, so the key bound has to bite where the
    /// depth bound does not. Without this control the fact above could be passing for the wrong reason.
    /// </summary>
    [Fact]
    public async Task A_wide_but_shallow_body_is_within_the_depth_bound_and_still_refused_by_the_key_bound()
    {
        await using var world = await AlvoApiWorld.VehicleRegistryAsync(
            [_admin], new AlvoApiWorldSetup(api =>
            {
                api.MaxPayloadKeys = 3;
                api.MaxPayloadDepth = 32;
            }));

        using var response = await world.SendRawAsync(
            HttpMethod.Post, "/api/owners", _admin, content: AlvoApiWorld.RawJson(WideObject(keys: 10)));

        response.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);
    }

    [Fact]
    public async Task A_body_that_is_not_a_json_object_is_refused_rather_than_bound()
    {
        await using var world = await AlvoApiWorld.VehicleRegistryAsync([_admin]);

        using var array = await world.SendRawAsync(
            HttpMethod.Post, "/api/owners", _admin, content: AlvoApiWorld.RawJson("[1,2,3]"));
        using var scalar = await world.SendRawAsync(
            HttpMethod.Post, "/api/owners", _admin, content: AlvoApiWorld.RawJson("42"));

        array.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);
        scalar.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);
    }

    [Fact]
    public async Task A_malformed_body_is_refused_rather_than_crashing_the_endpoint()
    {
        await using var world = await AlvoApiWorld.VehicleRegistryAsync([_admin]);

        using var response = await world.SendRawAsync(
            HttpMethod.Post, "/api/owners", _admin, content: AlvoApiWorld.RawJson(@"{""name"":"));

        response.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);
    }

    /// <summary>
    /// A value the field's declared type cannot hold is the caller's mistake — 422 — and the refusal must
    /// name the field so the caller can fix it. The control is a valid create of the same field: without
    /// it, "422" could be this endpoint refusing everything.
    /// </summary>
    [Fact]
    public async Task A_value_the_field_type_cannot_hold_is_refused_naming_the_field()
    {
        await using var world = await AlvoApiWorld.VehicleRegistryAsync([_admin]);
        var ownerId = await CreateOwnerAsync(world, "Acme Ltd");

        using var badYear = await world.SendAsync(
            HttpMethod.Post, "/api/vehicles", _admin, body: Vehicle(ownerId, year: "not-a-number"));
        using var goodYear = await world.SendAsync(
            HttpMethod.Post, "/api/vehicles", _admin, body: Vehicle(ownerId, year: 2020));

        badYear.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);
        (await badYear.ReadProblemDetailAsync()).ShouldContain("year");
        goodYear.StatusCode.ShouldBe(
            HttpStatusCode.Created, "the same field accepts a well-typed value, so the refusal is about the value");
    }

    /// <summary>
    /// A key the entity does not declare is refused <em>here</em>, before its value is materialised, and
    /// the refusal names neither the key nor the entity — it must not answer "does this entity have a
    /// field called X?" one request at a time.
    /// </summary>
    [Fact]
    public async Task An_undeclared_key_is_refused_without_naming_it()
    {
        await using var world = await AlvoApiWorld.VehicleRegistryAsync([_admin]);

        using var response = await world.SendRawAsync(
            HttpMethod.Post, "/api/owners", _admin,
            content: AlvoApiWorld.RawJson(@"{""name"":""Acme Ltd"",""smuggled_field"":{""deep"":1}}"));

        response.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);
        var detail = await response.ReadProblemDetailAsync();
        detail.ShouldNotContain("smuggled_field");
        detail.ShouldNotContain("owners");
    }

    /// <summary>
    /// The condition the first round laundered: an applied schema carrying a field type this build cannot
    /// map is a <b>broken invariant of whoever composed the schema</b> — family 3 in <c>IAlvoData</c>'s
    /// table, rendered 500 — not a caller error. Rendering it as a 422 tells the caller to fix a request
    /// that was fine.
    /// </summary>
    /// <remarks>
    /// Driven directly rather than over HTTP because no descriptor the schema admits can declare such a
    /// field: the only way to reach it is to hand the reader the schema a broken composer would have
    /// produced.
    /// </remarks>
    [Fact]
    public async Task An_unmapped_field_type_propagates_as_a_broken_invariant_not_a_client_error()
    {
        var entity = new EntitySchema
        {
            Name = "broken",
            Fields = [new FieldSchema { Name = "mystery", Type = (FieldType)999 }],
        };

        var exception = await Should.ThrowAsync<NotSupportedException>(() => JsonPayloadReader.ReadAsync(
            Request(@"{""mystery"":""anything""}"), entity, new AlvoApiOptions(), TestContext.Current.CancellationToken));

        exception.Message.ShouldContain("999");
    }

    /// <summary>
    /// The counterpart: a declared type this build <em>does</em> map binds without complaint, so the fact
    /// above is about the unmapped arm rather than about the reader refusing everything.
    /// </summary>
    [Fact]
    public async Task A_mapped_field_type_binds_to_the_clr_type_the_port_publishes()
    {
        var entity = new EntitySchema
        {
            Name = "vehicles",
            Fields =
            [
                new FieldSchema { Name = "owner_id", Type = FieldType.Ref },
                new FieldSchema { Name = "year", Type = FieldType.Integer },
                new FieldSchema { Name = "plate", Type = FieldType.String },
            ],
        };
        var owner = Guid.NewGuid();

        var (values, failure) = await JsonPayloadReader.ReadAsync(
            Request($@"{{""owner_id"":""{owner}"",""year"":2020,""plate"":""ACME-1""}}"),
            entity,
            new AlvoApiOptions(),
            TestContext.Current.CancellationToken);

        failure.ShouldBeNull();
        values!["owner_id"].ShouldBe(owner);
        values["year"].ShouldBe(2020L);
        values["plate"].ShouldBe("ACME-1");
    }

    private static async Task<Guid> CreateOwnerAsync(AlvoApiWorld world, string name)
    {
        using var response = await world.SendAsync(
            HttpMethod.Post, "/api/owners", _admin, body: new JsonObject { ["name"] = name });
        response.StatusCode.ShouldBe(HttpStatusCode.Created);
        return (await response.ReadJsonObjectAsync())["id"]!.GetValue<Guid>();
    }

    private static JsonObject Vehicle(Guid ownerId, object year) => new()
    {
        ["vin"] = Guid.NewGuid().ToString("N")[..17],
        ["plate"] = Guid.NewGuid().ToString("N")[..8],
        ["make"] = "Skoda",
        ["model"] = "Octavia",
        ["year"] = year is int number ? JsonValue.Create(number) : JsonValue.Create((string)year),
        ["owner_id"] = ownerId.ToString(),
    };

    /// <summary>An <see cref="HttpRequest"/> carrying <paramref name="json"/>, for the facts that drive the reader directly.</summary>
    private static HttpRequest Request(string json)
    {
        var context = new DefaultHttpContext();
        context.Request.Body = new MemoryStream(Encoding.UTF8.GetBytes(json));
        context.Request.ContentLength = context.Request.Body.Length;
        return context.Request;
    }

    /// <summary>Content that deliberately declares no length, so the size bound cannot be satisfied by a header check.</summary>
    private static StreamContent Chunked(string json)
    {
        var content = new StreamContent(new MemoryStream(Encoding.UTF8.GetBytes(json)));
        content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/json");
        content.Headers.ContentLength = null;
        return content;
    }

    private static string Nested(int depth) =>
        $@"{{""name"":{new string('[', depth)}1{new string(']', depth)}}}";

    private static string WideObject(int keys) =>
        "{" + string.Join(
            ',',
            Enumerable.Range(0, keys).Select(index =>
                string.Create(CultureInfo.InvariantCulture, $@"""field_{index}"":1"))) + "}";
}
