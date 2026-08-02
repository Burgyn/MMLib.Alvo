using System.Net;
using System.Text.Json.Nodes;

namespace MMLib.Alvo.Host.Tests;

/// <summary>
/// #75's last clause: the standalone host serves the OpenAPI document, and Scalar renders <em>that</em>
/// document rather than merely rendering.
/// </summary>
/// <remarks>
/// <b>A 200 carrying HTML is not the fact worth having.</b> Scalar's page is a static shell that fetches its
/// document in the browser, so a page pointed at a route nothing serves still answers 200 with a complete-
/// looking body and fails only in front of the reader — which is exactly the failure a user meets. So the
/// document URL is read <em>out of</em> the page, resolved the way the shipped client resolves it, pinned to
/// the route this host mapped, and then followed to prove the thing on the other end is Alvo's document.
/// </remarks>
public class AlvoHostDocsTests
{
    /// <summary>
    /// The opening of the overview Alvo's own document transformer appends. A fragment of the core's prose
    /// rather than a Host constant on purpose: the fact below is that <em>the core's</em> text landed after the
    /// host's, so restating the host's own words would prove nothing about the two running in order.
    /// </summary>
    private const string AlvoOverview = "Every route here is generated from the applied Alvo descriptor";

    /// <summary>
    /// The document describes the routes this host mapped from the mounted descriptor — so the assertion is on a
    /// path key only that descriptor could have produced, not on the response being 200.
    /// </summary>
    [Fact]
    public async Task The_document_describes_the_mounted_descriptors_routes()
    {
        await using var world = await AlvoHostWorld.StartAsync();

        var document = await DocumentAtAsync(world, AlvoHost.OpenApiDocumentPath);

        document["openapi"]!.GetValue<string>().ShouldStartWith("3.1");
        PathsOf(document).ShouldContain(
            "/api/warehouses", "the document must describe the routes the descriptor generated");
    }

    /// <summary>
    /// Scalar renders <em>the document this host serves</em>: the page names one document URL, that URL
    /// resolves to the route the host mapped, and following it reaches Alvo's document.
    /// </summary>
    [Fact]
    public async Task Scalar_renders_the_document_the_host_serves()
    {
        await using var world = await AlvoHostWorld.StartAsync();

        var page = await ScalarPageAsync(world);
        var referenced = DocumentUrlIn(page);

        referenced.ShouldBe(
            AlvoHost.OpenApiDocumentPath,
            "the page must fetch the document route this host mapped, not Scalar's default");

        var document = await DocumentAtAsync(world, referenced);

        PathsOf(document).ShouldContain(
            "/api/warehouses", "the URL the rendered page fetches must resolve to this host's own document");
    }

    /// <summary>
    /// The control: docs are one setting, and turning them off really removes both routes. Without this the
    /// option could be ignored and every fact above would still pass.
    /// </summary>
    /// <remarks>
    /// Both, not just the UI. <c>Alvo:Docs:Enabled</c> is the switch for publishing the API's shape at all, and
    /// a host that removed the page while still serving the document it renders would have hidden the reader
    /// and kept the contract — which is not what an operator turning docs off is asking for. The page is also
    /// useless without the document, so removing only the UI would be the one combination nobody wants.
    /// </remarks>
    [Fact]
    public async Task Turning_docs_off_removes_both_routes()
    {
        var overrides = new Dictionary<string, string?>(StringComparer.Ordinal)
        {
            ["Alvo:Docs:Enabled"] = "false",
        };

        await using var world = await AlvoHostWorld.StartAsync(overrides: overrides);

        using var document = await world.SendAnonymouslyAsync(HttpMethod.Get, AlvoHost.OpenApiDocumentPath);
        using var entry = await world.SendAnonymouslyAsync(HttpMethod.Get, AlvoHost.ScalarPath);
        using var page = await world.SendAnonymouslyAsync(HttpMethod.Get, $"{AlvoHost.ScalarPath}/");

        document.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        entry.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        page.StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    /// <summary>
    /// Alvo's transformer appends to the host's <c>info.description</c> rather than replacing it — which only
    /// holds if <c>AddOpenApi</c> is registered before <c>AddAlvo</c>. This is the fact that catches a
    /// reordering.
    /// </summary>
    /// <remarks>
    /// The discriminating half is the <em>position</em>, not the presence. Both orders leave a title and a
    /// non-empty description behind, so asserting either would pass on the transformers running the wrong way
    /// round; only "the host wrote something and the core's overview came after it" can fail for that reason.
    /// Registered the other way, the host's transformer runs last and overwrites the overview outright.
    /// </remarks>
    [Fact]
    public async Task The_hosts_own_info_survives_alvos_transformer()
    {
        await using var world = await AlvoHostWorld.StartAsync();

        var document = await DocumentAtAsync(world, AlvoHost.OpenApiDocumentPath);
        var description = document["info"]!["description"]!.GetValue<string>();

        document["info"]!["title"]!.GetValue<string>().ShouldBe("Alvo");
        description.ShouldContain(AlvoOverview, Case.Sensitive);
        description.IndexOf(AlvoOverview, StringComparison.Ordinal).ShouldBeGreaterThan(
            0, "the host's own description must precede the overview Alvo appended to it");
    }

    /// <summary>The docs page as a browser reaches it, and the path it was finally served from.</summary>
    /// <param name="Path">Where the page ended up, which is what its own URL resolution is relative to.</param>
    /// <param name="Html">The page.</param>
    private sealed record ScalarPage(string Path, string Html);

    /// <summary>
    /// Navigates to <see cref="AlvoHost.ScalarPath"/> the way a reader does, following the one redirect Scalar
    /// answers the slash-less entry route with.
    /// </summary>
    /// <remarks>
    /// The redirect is followed rather than asserted, because <em>that</em> the page sits one hop away is
    /// Scalar's business; that the advertised route reaches it is the host's. The test client does not follow
    /// redirects on its own — there is no real network here — so the hop is explicit.
    /// </remarks>
    private static async Task<ScalarPage> ScalarPageAsync(AlvoHostWorld world)
    {
        using var entry = await world.SendAnonymouslyAsync(HttpMethod.Get, AlvoHost.ScalarPath);
        if (entry.Headers.Location is not { } moved)
        {
            return await PageAsync(entry, AlvoHost.ScalarPath);
        }

        var path = Resolve(AlvoHost.ScalarPath, moved.ToString());
        using var redirected = await world.SendAnonymouslyAsync(HttpMethod.Get, path);

        return await PageAsync(redirected, path);
    }

    private static async Task<ScalarPage> PageAsync(HttpResponseMessage response, string path)
    {
        var html = await response.ReadTextAsync();

        response.StatusCode.ShouldBe(HttpStatusCode.OK, html);
        response.Content.Headers.ContentType!.MediaType.ShouldBe("text/html");

        return new ScalarPage(path, html);
    }

    /// <summary>
    /// The absolute path the rendered page will fetch its document from.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Scalar emits the URL relative and resolves it in the browser</b>, so reading the literal out of the
    /// markup would assert a string rather than a destination. <c>scalar.aspnetcore.js</c> resolves it against
    /// the origin plus the base path it derives by stripping the prefix it was initialised with off
    /// <c>window.location.pathname</c> — which is how the page is meant to keep working under a subdirectory.
    /// Reproducing that arithmetic against the path the page was actually served from makes the result the
    /// request a browser would issue.
    /// </para>
    /// <para>
    /// Both the prefix and the source list are read off the page. If a Scalar upgrade changes how either is
    /// emitted this fails loudly and says so, which is the right outcome: the wiring then wants re-checking,
    /// not a green run.
    /// </para>
    /// </remarks>
    private static string DocumentUrlIn(ScalarPage page)
    {
        var sources = SourcesIn(page.Html);

        sources.Count.ShouldBe(1, $"the page must name exactly one OpenAPI document; it named {sources.Count}");

        var basePath = BasePathOf(page, PrefixIn(page.Html));

        return Resolve($"{basePath}/", sources[0]!["url"]!.GetValue<string>());
    }

    /// <summary>The document sources the page's inline configuration carries.</summary>
    private static JsonArray SourcesIn(string html)
    {
        var line = html.Split('\n')
            .Select(candidate => candidate.Trim().TrimEnd(','))
            .SingleOrDefault(candidate => candidate.Contains("\"sources\"", StringComparison.Ordinal));

        line.ShouldNotBeNull("the page must carry Scalar's inline configuration on one line");

        return JsonNode.Parse(line)!["sources"]!.AsArray();
    }

    /// <summary>The prefix Scalar's client is initialised with, percent-decoded as the client decodes it.</summary>
    private static string PrefixIn(string html)
    {
        const string call = "initialize(";
        var opening = html.IndexOf(call, StringComparison.Ordinal);

        opening.ShouldBeGreaterThan(-1, "the page must initialise Scalar's client");

        var quoted = html.IndexOf('\'', opening + call.Length);
        var closing = html.IndexOf('\'', quoted + 1);

        return Uri.UnescapeDataString(html[(quoted + 1)..closing]);
    }

    /// <summary>What the page's own location leaves once the prefix it knows about is stripped off it.</summary>
    private static string BasePathOf(ScalarPage page, string prefix) =>
        page.Path.EndsWith(prefix, StringComparison.Ordinal) ? page.Path[..^prefix.Length] : string.Empty;

    private static string Resolve(string from, string relative) =>
        new Uri(new Uri(new Uri("http://alvo.invalid"), from), relative).AbsolutePath;

    /// <summary>The document served at <paramref name="path"/>, as JSON, after proving it is really there.</summary>
    private static async Task<JsonObject> DocumentAtAsync(AlvoHostWorld world, string path)
    {
        using var response = await world.SendAnonymouslyAsync(HttpMethod.Get, path);

        response.StatusCode.ShouldBe(HttpStatusCode.OK, await response.ReadTextAsync());

        return await response.ReadJsonObjectAsync();
    }

    private static IEnumerable<string> PathsOf(JsonNode document) =>
        document["paths"]!.AsObject().Select(path => path.Key);
}
