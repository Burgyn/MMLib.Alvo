using System.Net;
using System.Text.Json.Nodes;

namespace MMLib.Alvo.Host.Tests;

/// <summary>
/// The other two forwarded-headers flags the host enables — <c>X-Forwarded-Proto</c> and
/// <c>X-Forwarded-Host</c>, the pair that decides the request's <em>origin</em> rather than its path.
/// <see cref="AlvoHostPathBaseTests"/> measures the third (<c>X-Forwarded-Prefix</c>) and the switch that
/// guards all of them.
/// </summary>
/// <remarks>
/// <para>
/// <b>They were enabled and measured by nothing:</b> deleting either flag from the host's configuration left
/// the whole suite green, while behind a TLS-terminating ingress <c>Request.Scheme</c> silently reverts to
/// <c>http</c> and <c>Request.Host</c> to the container's own name.
/// </para>
/// <para>
/// The observable is the OpenAPI document's <c>servers[0].url</c>, and it is the right one rather than a
/// convenient one: ASP.NET Core builds it from <c>Request.Scheme</c>, <c>Request.Host</c> and
/// <c>Request.PathBase</c>, and it is the base URL every generated client and every agent reading the
/// document sends its next request to. A document advertising <c>http://localhost</c> from behind an ingress
/// is not a cosmetic defect: a client that follows it leaves TLS, or fails to resolve the name at all.
/// </para>
/// <para>
/// Each fact moves <b>one</b> header and pins the whole origin, so the half that did not move is the
/// non-vacuity control inside the fact: a host that ignored the flag under test answers
/// <c>http://localhost/</c>, and a host that honoured headers it was never sent would fail the other half.
/// </para>
/// </remarks>
public class AlvoHostForwardedOriginTests
{
    /// <summary>A TLS-terminating ingress announces the scheme it served, and the host advertises it.</summary>
    [Fact]
    public async Task A_trusted_proxys_forwarded_proto_becomes_the_advertised_scheme()
    {
        await using var world = await AlvoHostWorld.StartAsync(overrides: ForwardedHeadersEnabled());

        var origin = await DocumentOriginAsync(world, Forwarded("X-Forwarded-Proto", "https"));

        origin.ShouldBe(
            "https://localhost/",
            "a client that arrived over TLS must not be handed an http:// base URL by the document");
    }

    /// <summary>A proxy announces the host it was asked for, and the host advertises that rather than its own.</summary>
    [Fact]
    public async Task A_trusted_proxys_forwarded_host_becomes_the_advertised_host()
    {
        await using var world = await AlvoHostWorld.StartAsync(overrides: ForwardedHeadersEnabled());

        var origin = await DocumentOriginAsync(world, Forwarded("X-Forwarded-Host", "api.example.com"));

        origin.ShouldBe(
            "http://api.example.com/",
            "the document must name the origin the client reached, not the container's own hostname");
    }

    /// <summary>
    /// The control, and the security half: with the switch off — the default — neither header moves the
    /// origin, so a caller cannot talk the host into advertising an origin of their choosing.
    /// </summary>
    [Fact]
    public async Task An_untrusted_forwarded_origin_is_ignored()
    {
        await using var world = await AlvoHostWorld.StartAsync();

        var headers = Forwarded("X-Forwarded-Proto", "https");
        headers["X-Forwarded-Host"] = "attacker.example";

        var origin = await DocumentOriginAsync(world, headers);

        origin.ShouldBe(
            "http://localhost/",
            "an untrusted caller must not choose the base URL the document hands the next client");
    }

    /// <summary>
    /// The origin the served document advertises, read the way a generated client reads it.
    /// </summary>
    /// <param name="world">The running host.</param>
    /// <param name="headers">The headers a proxy in front of it would set.</param>
    private static async Task<string> DocumentOriginAsync(
        AlvoHostWorld world, IReadOnlyDictionary<string, string> headers)
    {
        using var response = await world.SendAsync(
            HttpMethod.Get, AlvoHost.OpenApiDocumentPath, body: null, headers);

        response.StatusCode.ShouldBe(HttpStatusCode.OK, await response.ReadTextAsync());

        var servers = (await response.ReadJsonObjectAsync())["servers"]!.AsArray();

        servers.Count.ShouldBe(
            1, $"the document must advertise exactly one server; it advertised {servers.Count}");

        return servers[0]!["url"]!.GetValue<string>();
    }

    private static Dictionary<string, string> Forwarded(string name, string value) =>
        new(StringComparer.Ordinal) { [name] = value };

    private static Dictionary<string, string?> ForwardedHeadersEnabled() =>
        new(StringComparer.Ordinal) { ["Alvo:ForwardedHeaders:Enabled"] = "true" };
}
