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

    /// <summary>
    /// The depth bound refuses, and its message <b>names itself</b> — it was the one bound whose refusal
    /// came back as "not well-formed JSON", because the reader raises the same exception for a too-deep body
    /// as for a broken one. An agent cannot act on that: it would go looking for a syntax error that is not
    /// there.
    /// </summary>
    [Fact]
    public async Task A_body_nested_deeper_than_the_configured_maximum_is_refused_naming_the_bound()
    {
        await using var world = await AlvoApiWorld.VehicleRegistryAsync(
            [_admin], new AlvoApiWorldSetup(api => api.MaxPayloadDepth = 4));

        using var response = await world.SendRawAsync(
            HttpMethod.Post, "/api/owners", _admin, content: AlvoApiWorld.RawJson(Nested(depth: 16)));

        response.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);
        (await response.ReadProblemDetailAsync()).ShouldContain("nests deeper than 4");
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
    /// The key bound counts property names at <b>every</b> depth, so nesting a wide object one level does not
    /// escape it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This is the bound that was not a bound. Counting only depth 1 meant
    /// <c>{"name":{…150 000 keys…}}</c> satisfied the key cap, satisfied the depth cap at depth 2, fitted
    /// inside <see cref="AlvoApiOptions.MaxRequestBodyBytes"/>, and was then materialised in full — roughly
    /// 20–40× memory amplification per request, refused only afterwards.
    /// </para>
    /// <para>
    /// Only the key count can explain this refusal: the body is a few dozen bytes, far under the size bound,
    /// and nests two levels, far under the depth bound — and the message is asserted to name the key bound
    /// rather than any 422 being accepted. Its predecessor set <c>MaxPayloadDepth = 32</c>, which is the
    /// default, and sent the same flat object as the fact above it: one fact written twice.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task A_wide_object_nested_below_the_top_level_is_still_refused_by_the_key_bound()
    {
        await using var world = await AlvoApiWorld.VehicleRegistryAsync(
            [_admin], new AlvoApiWorldSetup(api => api.MaxPayloadKeys = 3));

        using var response = await world.SendRawAsync(
            HttpMethod.Post, "/api/owners", _admin,
            content: AlvoApiWorld.RawJson($@"{{""name"":{WideObject(keys: 10)}}}"));

        response.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);
        (await response.ReadProblemDetailAsync()).ShouldContain(
            "more than 3 fields",
            Case.Sensitive,
            "the key bound must be what refused it, not the size or depth bound");
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
        (await badYear.ReadViolationsAsync()).ShouldBe(
            [("/year", "invalid-value")],
            "the violation's JSON Pointer is what names the field now — the message names the declared type, "
            + "never the caller's value");
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

        var payload = await JsonPayloadReader.ReadAsync(
            Request($@"{{""owner_id"":""{owner}"",""year"":2020,""plate"":""ACME-1""}}"),
            entity,
            new AlvoApiOptions(),
            TestContext.Current.CancellationToken);

        payload.Violations.ShouldBeEmpty();
        payload.Values["owner_id"].ShouldBe(owner);
        payload.Values["year"].ShouldBe(2020L);
        payload.Values["plate"].ShouldBe("ACME-1");
    }

    /// <summary>
    /// An unauthorized caller is refused <b>before</b> the body is read, so a body that would itself be
    /// refused still earns the authorization refusal.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Two reasons the ordering matters, neither of them confidentiality: parsing up to
    /// <see cref="AlvoApiOptions.MaxRequestBodyBytes"/> for a caller who cannot succeed is a
    /// denial-of-service amplifier, and telling an unauthorized caller their body was malformed sends an
    /// agent to fix the wrong thing.
    /// </para>
    /// <para>
    /// Both refusal routes are exercised, because they are decided in different places: the scope gate lives
    /// in the endpoint filter, and the policy decision is resolved by the endpoint itself. The bodies are
    /// deliberately over the configured bound <em>and</em> malformed, so a 422 would be unambiguous evidence
    /// that the read happened first.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task An_unauthorized_write_is_refused_before_the_body_is_read()
    {
        var readOnly = new TestApiKey("reader-key", ["admin", "authenticated"], ["owners:read"]);
        var tenantless = new TestApiKey("no-tenant", ["authenticated"], ["notes:read", "notes:write"]);
        await using var scoped = await AlvoApiWorld.VehicleRegistryAsync(
            [readOnly], new AlvoApiWorldSetup(api => api.MaxRequestBodyBytes = 32));
        await using var tenanted = await AlvoApiWorld.TenantNotesAsync([tenantless]);

        // A fresh content per request: disposing an HttpRequestMessage disposes its content, so a shared
        // instance makes the second send throw ObjectDisposedException rather than assert anything.
        using var scopeRefused = await scoped.SendRawAsync(
            HttpMethod.Post, "/api/owners", readOnly,
            content: AlvoApiWorld.RawJson($@"{{""name"":""{new string('x', 512)}"","));
        using var createRefused = await tenanted.SendRawAsync(
            HttpMethod.Post, "/api/notes", tenantless, content: AlvoApiWorld.RawJson(@"{""title"":"));
        using var patchRefused = await tenanted.SendRawAsync(
            HttpMethod.Patch, $"/api/notes/{Guid.NewGuid()}", tenantless,
            content: AlvoApiWorld.RawJson(@"{""title"":"));

        scopeRefused.StatusCode.ShouldBe(
            HttpStatusCode.Forbidden, "the scope gate must answer before the oversized body is read");
        createRefused.StatusCode.ShouldBe(
            HttpStatusCode.Forbidden, "the create's policy decision must answer before the malformed body is read");
        patchRefused.StatusCode.ShouldBe(
            HttpStatusCode.Forbidden, "and the update's must too — both write endpoints were reordered");
    }

    /// <summary>
    /// The one carve-out in an otherwise public schema shape: a <c>hidden</c> field's name must be
    /// <b>indistinguishable</b> from a name that does not exist — status and body, byte for byte.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The rest of the declared shape is deliberately public: route literals disclose entity existence
    /// before authorization, and Task 8 publishes the declared non-hidden field list. A <c>hidden</c> field
    /// is the exception, because the descriptor author marked it confidential and the document excludes it —
    /// so a filter over it must not be told apart from a filter over nothing.
    /// </para>
    /// <para>
    /// End to end over the live API, whereas <c>QueryStringParserTests</c> asserts the same equality at the
    /// parser with a hand-built mask: the parser can be right while the endpoint passes it the wrong mask,
    /// and only this level notices that. Equality of the whole body, not "both are 422".
    /// </para>
    /// </remarks>
    [Fact]
    public async Task A_filter_over_a_hidden_field_answers_exactly_like_one_over_an_unknown_field()
    {
        var reader = new TestApiKey("reader-key", ["authenticated"], ["notes:read"]);
        await using var world = await AlvoApiWorld.FromDescriptorAsync("masked-notes.alvo.json", [reader]);

        using var hidden = await world.SendAsync(HttpMethod.Get, "/api/notes?secret=eq.x", reader);
        using var unknown = await world.SendAsync(HttpMethod.Get, "/api/notes?nosuchfield=eq.x", reader);
        using var declared = await world.SendAsync(HttpMethod.Get, "/api/notes?title=eq.x", reader);

        hidden.StatusCode.ShouldBe(unknown.StatusCode);
        (await hidden.ReadTextAsync()).ShouldBe(
            await unknown.ReadTextAsync(),
            "any difference answers 'does this entity have a field called secret' one request at a time");
        declared.StatusCode.ShouldBe(
            HttpStatusCode.OK, "a visible field is filterable, so the refusal above is about the mask");
    }

    /// <summary>
    /// The same equality for a caller the <c>list</c> policy <b>denies</b> — the half that did not hold, for
    /// the caller most likely to be asking.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>This was a live oracle.</b> <c>PolicyDecision.Deny</c> carries an <em>empty</em>
    /// <c>HiddenFields</c>, so a denied lister used to reach the query parser with no mask at all: a filter
    /// over the declared-but-hidden <c>secret</c> parsed cleanly and earned the port's 403, while a filter over
    /// a name that does not exist was refused by the parser as a 422. Two responses, one bit, and the answer to
    /// "does this entity have a field called <c>secret</c>" — from a caller who may read none of it. The fix is
    /// that the endpoint resolves the decision <em>before</em> parsing, so a denied lister is answered 403
    /// whatever they asked for.
    /// </para>
    /// <para>
    /// The sibling above proves the invariant for an <em>authorized</em> caller, and it held there all along —
    /// which is exactly why this needed a fact of its own. Byte equality of the whole body, not "both are
    /// refusals": the two used to differ in status, slug, prose and violations, and any one of those is the
    /// oracle.
    /// </para>
    /// <para>
    /// The key carries <c>ledgers:read</c> deliberately. Without the scope the request never reaches a policy
    /// at all — the scope gate answers <c>out-of-scope</c> from the endpoint filter — so both requests would be
    /// equal for a reason that has nothing to do with the mask, and the fact could not fail.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task A_denied_lister_cannot_tell_a_hidden_field_from_an_unknown_one_either()
    {
        var scopedButUnroled = new TestApiKey("no-auditor-key", ["authenticated"], ["ledgers:read"]);
        await using var world = await AlvoApiWorld.FromDescriptorAsync("masked-notes.alvo.json", [scopedButUnroled]);

        using var hidden = await world.SendAsync(HttpMethod.Get, "/api/ledgers?secret=eq.x", scopedButUnroled);
        using var unknown = await world.SendAsync(HttpMethod.Get, "/api/ledgers?nosuchfield=eq.x", scopedButUnroled);

        hidden.StatusCode.ShouldBe(
            HttpStatusCode.Forbidden, "a denied lister must be refused for being denied, not for their filter");
        hidden.StatusCode.ShouldBe(unknown.StatusCode);
        (await hidden.ReadTextAsync()).ShouldBe(
            await unknown.ReadTextAsync(),
            "any difference answers 'does this entity have a field called secret' to a caller who may read none of it");
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
