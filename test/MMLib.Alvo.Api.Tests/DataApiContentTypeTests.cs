using MMLib.Alvo.Api.Internal;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json.Nodes;

namespace MMLib.Alvo.Api.Tests;

/// <summary>
/// The JSON <c>Content-Type</c> guard (#191): which media types a body-taking route accepts, what it
/// answers when it refuses, and where the refusal sits relative to authorization.
/// </summary>
/// <remarks>
/// Over <c>owners</c> rather than <c>vehicles</c>, because only <c>name</c> is required there — a
/// <c>vehicles</c> row needs six fields including a <c>ref</c> to an owner, so a fact about a
/// <em>header</em> would have had to build two rows first and could fail for a reason that is not the
/// header.
/// </remarks>
public class DataApiContentTypeTests
{
    /// <summary>
    /// The default is the whole security value of the feature, and nothing else in this file can hold it:
    /// every other fact either sends JSON or sets the option explicitly, so flipping the initializer to
    /// <see langword="false"/> would leave them all green.
    /// </summary>
    [Fact]
    public void The_guard_is_on_by_default() =>
        new AlvoApiOptions().RequireJsonContentType.ShouldBeTrue(
            "secure-by-default: a host must opt out of the guard, never into it");

    /// <summary>
    /// The slug is a published constant an embedded host branches on, so it is enumerated in
    /// <see cref="AlvoProblemTypes.All"/> and mintable through <see cref="AlvoProblemTypes.UriOf"/> —
    /// which throws for a slug the catalogue does not declare.
    /// </summary>
    [Fact]
    public void The_unsupported_media_type_slug_is_in_the_catalogue()
    {
        AlvoProblemTypes.All.ShouldContain(AlvoProblemTypes.UnsupportedMediaType);
        AlvoProblemTypes.UriOf(AlvoProblemTypes.UnsupportedMediaType)
            .ShouldBe("https://alvo.dev/errors/unsupported-media-type");
    }

    /// <summary>
    /// Every endpoint kind the Data API has, classified and then driven: the seven that read a body refuse
    /// a <c>text/plain</c> declaration, and the three that read none are untouched by the guard.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Driven from <see cref="DataApiEndpointKind"/> itself, which is what makes it exhaustive.</b> The
    /// guard is called from five delegates and there is deliberately no single chokepoint (see
    /// <see cref="JsonContentType"/>), so this fact is the only thing standing behind those five call
    /// sites. A hand-written list of routes could not fail for a kind nobody remembered to add;
    /// <see cref="RouteOf"/>'s switch has no silent arm, so a <em>new</em> kind fails here until someone
    /// classifies it, and an <em>existing</em> kind that acquires a body fails here until its delegate
    /// calls the guard.
    /// </para>
    /// <para>
    /// This is not circular. The enum is the API's vocabulary for "which endpoint is this"; it is not the
    /// route table, and it is not the set of call sites the guard has. Reading the expected set out of the
    /// route builder would have been.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task Every_endpoint_kind_is_guarded_exactly_when_it_reads_a_body()
    {
        await using var world = await AlvoApiWorld.VehicleRegistryAsync([Admin]);

        var kinds = Enum.GetValues<DataApiEndpointKind>();
        var guarded = 0;
        foreach (var kind in kinds)
        {
            var (method, path, body) = RouteOf(kind);
            using var content = Body(body ?? string.Empty, "text/plain");
            using var response = await world.SendRawAsync(method, path, Admin, content: content);

            if (body is null)
            {
                response.StatusCode.ShouldNotBe(
                    HttpStatusCode.UnsupportedMediaType,
                    $"{kind} ({method} {path}) reads no body, so the guard must not reach it");
                continue;
            }

            response.StatusCode.ShouldBe(
                HttpStatusCode.UnsupportedMediaType,
                $"{kind} ({method} {path}) reads a body, so a text/plain declaration must be refused");
            guarded++;
        }

        kinds.Length.ShouldBe(10, "a kind was added or removed; classify it in RouteOf and re-count");
        guarded.ShouldBe(7, "the create, the update, the replace, the query and the three batch verbs");
    }

    /// <summary>Every body-taking kind accepts the media type the generated document declares.</summary>
    /// <remarks>
    /// The non-vacuity control for the fact above: a guard that refused <em>everything</em> would satisfy
    /// it while breaking the API, and every other fact in the suite sends its JSON through the world's own
    /// serializer rather than through this path.
    /// </remarks>
    [Fact]
    public async Task Every_body_taking_kind_accepts_application_json()
    {
        await using var world = await AlvoApiWorld.VehicleRegistryAsync([Admin]);

        foreach (var kind in Enum.GetValues<DataApiEndpointKind>())
        {
            var (method, path, body) = RouteOf(kind);
            if (body is null)
            {
                continue;
            }

            using var content = Body(body, "application/json");
            using var response = await world.SendRawAsync(method, path, Admin, content: content);

            response.StatusCode.ShouldNotBe(
                HttpStatusCode.UnsupportedMediaType,
                $"{kind} ({method} {path}) must accept the media type the generated document declares");
        }
    }

    /// <summary>A charset parameter is parsed and ignored, not read as a different media type.</summary>
    [Fact]
    public async Task A_charset_parameter_is_accepted()
    {
        await using var world = await AlvoApiWorld.VehicleRegistryAsync([Admin]);
        using var content = Body("""{"name":"Acme Ltd"}""", "application/json; charset=utf-8");

        using var response = await world.SendRawAsync(
            HttpMethod.Post, "/api/owners", Admin, content: content);

        response.StatusCode.ShouldBe(HttpStatusCode.Created);
    }

    /// <summary>
    /// The <c>+json</c> structured suffix is accepted, so RFC 7396's <c>application/merge-patch+json</c>
    /// works — refusing a <em>more</em> precise declaration of the same bytes would be a defect.
    /// </summary>
    [Fact]
    public async Task The_json_structured_suffix_is_accepted()
    {
        await using var world = await AlvoApiWorld.VehicleRegistryAsync([Admin]);
        var id = await CreatedOwnerAsync(world);
        using var content = Body("""{"name":"Renamed Ltd"}""", "application/merge-patch+json");

        using var response = await world.SendRawAsync(
            HttpMethod.Patch, $"/api/owners/{id}", Admin, content: content);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    /// <summary>
    /// A subtype that merely ends in the word is not a JSON suffix: <c>application/notjson</c> carries no
    /// <c>+json</c>, and accepting it would mean the check is string surgery rather than the parser's own
    /// structured-suffix rule.
    /// </summary>
    [Fact]
    public async Task A_subtype_that_only_ends_in_the_word_json_is_refused()
    {
        await using var world = await AlvoApiWorld.VehicleRegistryAsync([Admin]);
        using var content = Body("""{"name":"Acme Ltd"}""", "application/notjson");

        using var response = await world.SendRawAsync(
            HttpMethod.Post, "/api/owners", Admin, content: content);

        response.StatusCode.ShouldBe(HttpStatusCode.UnsupportedMediaType);
    }

    /// <summary>
    /// A <c>+json</c> suffix under another top-level type is refused. The suffix rule is scoped to
    /// <c>application</c>, which is where a JSON payload lives; <c>text/plain+json</c> is a declaration no
    /// client has reason to send, and <c>text/plain</c> is one of the three values a cross-site form can.
    /// </summary>
    [Fact]
    public async Task A_json_suffix_under_another_type_is_refused()
    {
        await using var world = await AlvoApiWorld.VehicleRegistryAsync([Admin]);
        using var content = Body("""{"name":"Acme Ltd"}""", "text/plain+json");

        using var response = await world.SendRawAsync(
            HttpMethod.Post, "/api/owners", Admin, content: content);

        response.StatusCode.ShouldBe(HttpStatusCode.UnsupportedMediaType);
    }

    /// <summary>
    /// A form-urlencoded body is refused — one of the three values WHATWG Fetch safelists, and the one a
    /// cross-site HTML form actually sends.
    /// </summary>
    [Fact]
    public async Task A_form_urlencoded_body_is_refused()
    {
        await using var world = await AlvoApiWorld.VehicleRegistryAsync([Admin]);
        using var content = Body("name=Acme+Ltd", "application/x-www-form-urlencoded");

        using var response = await world.SendRawAsync(
            HttpMethod.Post, "/api/owners", Admin, content: content);

        response.StatusCode.ShouldBe(HttpStatusCode.UnsupportedMediaType);
    }

    /// <summary>
    /// A body with no <c>Content-Type</c> at all is refused, which is the half that actually closes the
    /// vector: a request declaring nothing is CORS-safelisted by omission.
    /// </summary>
    [Fact]
    public async Task A_body_with_no_content_type_is_refused()
    {
        await using var world = await AlvoApiWorld.VehicleRegistryAsync([Admin]);
        using var content = Body("""{"name":"Acme Ltd"}""", mediaType: null);

        using var response = await world.SendRawAsync(
            HttpMethod.Post, "/api/owners", Admin, content: content);

        response.StatusCode.ShouldBe(HttpStatusCode.UnsupportedMediaType);
    }

    /// <summary>The refusal carries the slug, names the fix, and has no violations array to carry.</summary>
    [Fact]
    public async Task The_refusal_names_the_fix_and_carries_no_violations()
    {
        await using var world = await AlvoApiWorld.VehicleRegistryAsync([Admin]);
        using var content = Body("{}", "text/plain");

        using var response = await world.SendRawAsync(
            HttpMethod.Post, "/api/owners", Admin, content: content);

        (await response.ReadProblemTypeAsync()).ShouldBe(AlvoProblemTypes.UnsupportedMediaType);
        (await response.ReadProblemDetailAsync()).ShouldContain("application/json");
        (await response.ReadJsonObjectAsync()).ContainsKey("violations").ShouldBeFalse(
            "a 415 is about the declaration, not about anything inside the body, so there is nothing to point at");
    }

    /// <summary>
    /// The registered <c>Accept-*</c> headers, so the fix is machine-readable and not only prose —
    /// <c>Accept-Post</c> (W3C LDP 1.0 §7.1.2) and <c>Accept-Patch</c> (RFC 5789 §3.1). A <c>PUT</c> carries
    /// none, because no registered <c>Accept-Put</c> exists.
    /// </summary>
    [Fact]
    public async Task The_refusal_advertises_what_the_method_accepts()
    {
        await using var world = await AlvoApiWorld.VehicleRegistryAsync([Admin]);
        var id = Guid.NewGuid();

        using var postBody = Body("{}", "text/plain");
        using var post = await world.SendRawAsync(
            HttpMethod.Post, "/api/owners", Admin, content: postBody);
        using var patchBody = Body("{}", "text/plain");
        using var patch = await world.SendRawAsync(
            HttpMethod.Patch, $"/api/owners/{id}", Admin, content: patchBody);
        using var putBody = Body("{}", "text/plain");
        using var put = await world.SendRawAsync(
            HttpMethod.Put, $"/api/owners/{id}", Admin, content: putBody);

        post.Headers.GetValues("Accept-Post").ShouldHaveSingleItem().ShouldBe("application/json");
        patch.Headers.GetValues("Accept-Patch").ShouldHaveSingleItem()
            .ShouldContain("application/merge-patch+json");
        put.Headers.Contains("Accept-Put").ShouldBeFalse(
            "there is no registered Accept-Put, and inventing one to tidy the set is worse than omitting it");
    }

    /// <summary>Turning the guard off restores the previous behaviour on every body-taking kind.</summary>
    [Fact]
    public async Task A_host_that_opted_out_accepts_any_media_type()
    {
        await using var world = await AlvoApiWorld.VehicleRegistryAsync(
            [Admin], new AlvoApiWorldSetup(ConfigureApi: api => api.RequireJsonContentType = false));

        foreach (var kind in Enum.GetValues<DataApiEndpointKind>())
        {
            var (method, path, body) = RouteOf(kind);
            if (body is null)
            {
                continue;
            }

            using var content = Body(body, "text/plain");
            using var response = await world.SendRawAsync(method, path, Admin, content: content);

            response.StatusCode.ShouldNotBe(
                HttpStatusCode.UnsupportedMediaType,
                $"{kind} ({method} {path}) must accept anything once the host has opted out");
        }
    }

    /// <summary>
    /// A caller whose <em>decision</em> is denied is answered 403, not 415 — the ordering rule this layer
    /// states explicitly: an unauthorized caller must be told they are unauthorized, not something about a
    /// request they were never going to get to send.
    /// </summary>
    /// <remarks>
    /// <b>A tenantless caller on a tenant-scoped entity, because that is a decision denial and a role
    /// literal is not.</b> <c>PolicyEngine.ResolveOperation</c> denies for four reasons only — no
    /// descriptor applied, an unconfigured operation, a tenant-scoped entity with no tenant, and a
    /// predicate reading a caller value the caller lacks — and a <em>configured</em> rule always resolves to
    /// an allow carrying a <c>USING</c>/<c>WITH CHECK</c> predicate the port enforces per row. So a caller
    /// that <c>'admin' in @user.roles</c> will ultimately refuse is <em>not</em> denied at this layer, and
    /// for that caller the 415 legitimately comes first — exactly as <c>EnsureUnconditional</c>'s 412
    /// already does. <see cref="JsonContentType"/>'s remarks record that limit.
    /// </remarks>
    [Fact]
    public async Task A_caller_the_decision_denies_is_answered_403_and_not_415()
    {
        var tenantless = new TestApiKey("tenantless-key", ["admin"], ["*:read", "*:write"]);
        await using var world = await AlvoApiWorld.TenantNotesAsync([tenantless]);
        using var content = Body("{}", "text/plain");

        using var response = await world.SendRawAsync(
            HttpMethod.Post, "/api/notes", tenantless, content: content);

        response.StatusCode.ShouldBe(
            HttpStatusCode.Forbidden,
            "the tenant guard denies before the delegate interprets a single header");
    }

    /// <summary>
    /// A caller whose scopes exclude the operation is answered 403 by the authorization filter, which runs
    /// before the delegate — so the guard is not reached at all.
    /// </summary>
    [Fact]
    public async Task A_caller_out_of_scope_is_answered_403_and_not_415()
    {
        var readOnly = new TestApiKey("read-only-key", ["admin"], ["*:read"]);
        await using var world = await AlvoApiWorld.VehicleRegistryAsync([readOnly]);
        using var content = Body("{}", "text/plain");

        using var response = await world.SendRawAsync(
            HttpMethod.Post, "/api/owners", readOnly, content: content);

        response.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
    }

    /// <summary>A credential that was presented and cannot be used is answered 401, one step earlier still.</summary>
    [Fact]
    public async Task An_unusable_credential_is_answered_401_and_not_415()
    {
        await using var world = await AlvoApiWorld.VehicleRegistryAsync([Admin]);
        using var content = Body("{}", "text/plain");

        using var response = await world.SendRawAsync(
            HttpMethod.Post,
            "/api/owners",
            content: content,
            headers: [new KeyValuePair<string, string>("X-Alvo-Api-Key", "unknown-key.not-a-secret-at-all")]);

        response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    /// <summary>The key every fact presents unless it is measuring a different caller.</summary>
    private static TestApiKey Admin { get; } = new("admin-key", ["admin"], ["*:read", "*:write"]);

    /// <summary>
    /// The route one endpoint kind maps to on <c>owners</c>, and the body it would carry — or
    /// <see langword="null"/> for a kind that reads none.
    /// </summary>
    /// <remarks>
    /// <b>No silent arm.</b> A kind added to <see cref="DataApiEndpointKind"/> without a case here throws,
    /// which is what makes
    /// <see cref="Every_endpoint_kind_is_guarded_exactly_when_it_reads_a_body"/> exhaustive rather than
    /// merely thorough.
    /// </remarks>
    /// <param name="kind">The endpoint kind to route.</param>
    /// <exception cref="ArgumentOutOfRangeException">The kind is not classified here.</exception>
    private static (HttpMethod Method, string Path, string? Body) RouteOf(DataApiEndpointKind kind)
    {
        const string collection = "/api/owners";
        var item = $"{collection}/{Guid.NewGuid()}";
        const string row = """{"name":"Acme Ltd"}""";

        return kind switch
        {
            DataApiEndpointKind.List => (HttpMethod.Get, collection, null),
            DataApiEndpointKind.Get => (HttpMethod.Get, item, null),
            DataApiEndpointKind.Delete => (HttpMethod.Delete, item, null),
            DataApiEndpointKind.Query => (HttpMethod.Post, $"{collection}/query", """{"limit":1}"""),
            DataApiEndpointKind.Create => (HttpMethod.Post, collection, row),
            DataApiEndpointKind.Update => (HttpMethod.Patch, item, row),
            DataApiEndpointKind.Replace => (HttpMethod.Put, item, row),
            DataApiEndpointKind.BatchCreate =>
                (HttpMethod.Post, $"{collection}/batch", $$"""{"rows":[{{row}}]}"""),
            DataApiEndpointKind.BatchUpdate => (HttpMethod.Patch, $"{collection}/batch", """{"rows":[]}"""),
            DataApiEndpointKind.BatchDelete => (HttpMethod.Delete, $"{collection}/batch", """{"ids":[]}"""),
            _ => throw new ArgumentOutOfRangeException(
                nameof(kind), kind, "Unclassified endpoint kind: say whether it reads a request body."),
        };
    }

    /// <summary>A body sent verbatim under <paramref name="mediaType"/>, or under none at all.</summary>
    /// <remarks>
    /// <b>Always through <see cref="HttpContent"/>, never through a request header.</b>
    /// <c>Content-Type</c> is a content header, so <c>HttpRequestMessage.Headers</c> refuses it and
    /// <c>AlvoApiWorld.SendRawAsync</c> would fail its own "the world must really present it" assertion.
    /// <see cref="StringContent"/> always sets one, so the "declared nothing" case is expressed by clearing
    /// it afterwards — which is what a hand-rolled client, and a cross-site form, actually produce.
    /// </remarks>
    /// <param name="json">The body text.</param>
    /// <param name="mediaType">The media type to declare, or <see langword="null"/> to declare none.</param>
    private static StringContent Body(string json, string? mediaType)
    {
        var content = new StringContent(json, Encoding.UTF8);
        content.Headers.ContentType = mediaType is null ? null : MediaTypeHeaderValue.Parse(mediaType);

        return content;
    }

    /// <summary>A row to aim a <c>PATCH</c> at, created through the ordinary JSON path.</summary>
    /// <param name="world">The running API.</param>
    private static async Task<Guid> CreatedOwnerAsync(AlvoApiWorld world)
    {
        using var response = await world.SendAsync(
            HttpMethod.Post, "/api/owners", Admin, body: new JsonObject { ["name"] = "Acme Ltd" });
        response.StatusCode.ShouldBe(HttpStatusCode.Created);

        return (await response.ReadJsonObjectAsync())["id"]!.GetValue<Guid>();
    }
}
