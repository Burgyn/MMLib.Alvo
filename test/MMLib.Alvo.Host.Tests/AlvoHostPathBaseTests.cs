using System.Net;
using System.Text.Json.Nodes;

namespace MMLib.Alvo.Host.Tests;

/// <summary>
/// The standalone half of #121: a container behind a reverse proxy. The core's matrix proves the header honours
/// <c>PathBase</c>; these prove the host is the thing that <em>sets</em> one — from configuration, and from a
/// proxy's <c>X-Forwarded-Prefix</c> when it has been told to trust it.
/// </summary>
/// <remarks>
/// <b>The 404 #121 describes happens at the proxy, not in the host</b>, and that shapes what these can assert.
/// A host mounted under a path base still answers the unprefixed path in-process, because
/// <c>UsePathBase</c> strips a prefix when it is present rather than refusing a request without one — so
/// "follow the header and get 200" cannot, on its own, tell a correct <c>Location</c> from the broken one.
/// Two devices close that: the <c>Location</c> is pinned <em>whole</em> rather than by prefix, and the
/// forwarded-prefix fact follows the header through a model of the proxy that produced it, which is where the
/// 404 really lives.
/// </remarks>
public class AlvoHostPathBaseTests
{
    private const string Prefix = "/gateway";

    /// <summary>
    /// <c>Alvo:PathBase</c> reaches <c>UsePathBase</c>: the create only resolves under the base, and the row it
    /// advertises is reachable at the URL it advertised.
    /// </summary>
    [Fact]
    public async Task A_configured_path_base_is_honoured_end_to_end()
    {
        await using var world = await AlvoHostWorld.StartAsync(overrides: PathBase("/alvo"));

        using var created = await world.SendAsync(
            HttpMethod.Post, "/alvo/api/warehouses", new JsonObject { ["code"] = "W-2" });

        created.StatusCode.ShouldBe(HttpStatusCode.Created, await created.Content.ReadAsStringAsync(Ct));
        var location = created.Headers.Location!.ToString();

        using var followed = await world.SendAsync(HttpMethod.Get, location, body: null);

        followed.StatusCode.ShouldBe(HttpStatusCode.OK, $"following '{location}' must reach the row");
        location.ShouldBe($"/alvo/api/warehouses/{IdIn(location)}");
    }

    /// <summary>
    /// A proxy-set prefix, once the host has been told to trust forwarded headers. The trust is explicit
    /// because honouring a client-supplied <c>X-Forwarded-Prefix</c> unconditionally lets any caller choose the
    /// URL the host advertises.
    /// </summary>
    [Fact]
    public async Task A_trusted_proxys_forwarded_prefix_becomes_the_path_base()
    {
        await using var world = await AlvoHostWorld.StartAsync(overrides: ForwardedHeadersEnabled());

        using var created = await world.SendAsync(
            HttpMethod.Post, "/api/warehouses", new JsonObject { ["code"] = "W-3" }, ForwardedPrefix());

        created.StatusCode.ShouldBe(HttpStatusCode.Created, await created.Content.ReadAsStringAsync(Ct));
        var location = created.Headers.Location!.ToString();

        var followed = await FollowThroughTheProxyAsync(world, location);

        followed.ShouldBe(
            HttpStatusCode.OK,
            $"a client behind the proxy follows '{location}' and must reach the row");
        location.ShouldBe($"{Prefix}/api/warehouses/{IdIn(location)}");
    }

    /// <summary>
    /// The control, and the security half: with forwarded headers off — the default — a caller cannot talk the
    /// host into advertising a prefix of their choosing.
    /// </summary>
    [Fact]
    public async Task An_untrusted_forwarded_prefix_is_ignored()
    {
        await using var world = await AlvoHostWorld.StartAsync();

        using var created = await world.SendAsync(
            HttpMethod.Post,
            "/api/warehouses",
            new JsonObject { ["code"] = "W-4" },
            new Dictionary<string, string>(StringComparer.Ordinal) { ["X-Forwarded-Prefix"] = "/attacker" });

        created.StatusCode.ShouldBe(HttpStatusCode.Created, await created.Content.ReadAsStringAsync(Ct));
        var location = created.Headers.Location!.ToString();

        using var followed = await world.SendAsync(HttpMethod.Get, location, body: null);

        followed.StatusCode.ShouldBe(HttpStatusCode.OK, $"following '{location}' must reach the row");
        location.ShouldBe(
            $"/api/warehouses/{IdIn(location)}",
            "an untrusted caller must not choose the URL the host hands the next client");
    }

    /// <summary>
    /// Follows <paramref name="location"/> the way a client behind the proxy does, because that is where #121's
    /// 404 happens and the host cannot produce it.
    /// </summary>
    /// <remarks>
    /// The client resolves the header against the proxy's origin and asks the proxy for it. The proxy serves
    /// <see cref="Prefix"/> and nothing else: it strips that prefix, forwards the remainder, and re-announces
    /// the prefix it stripped. A <c>Location</c> outside the prefix is not the proxy's to serve, so the client
    /// meets a 404 the host never sees — which is exactly the bug, and why this returns one instead of
    /// asserting the prefix and calling that the fact.
    /// </remarks>
    private static async Task<HttpStatusCode> FollowThroughTheProxyAsync(AlvoHostWorld world, string location)
    {
        if (!location.StartsWith($"{Prefix}/", StringComparison.Ordinal))
        {
            return HttpStatusCode.NotFound;
        }

        using var forwarded = await world.SendAsync(
            HttpMethod.Get, location[Prefix.Length..], body: null, headers: ForwardedPrefix());

        return forwarded.StatusCode;
    }

    private static Dictionary<string, string> ForwardedPrefix() =>
        new(StringComparer.Ordinal) { ["X-Forwarded-Prefix"] = Prefix };

    private static Dictionary<string, string?> PathBase(string value) =>
        new(StringComparer.Ordinal) { ["Alvo:PathBase"] = value };

    private static Dictionary<string, string?> ForwardedHeadersEnabled() =>
        new(StringComparer.Ordinal) { ["Alvo:ForwardedHeaders:Enabled"] = "true" };

    private static string IdIn(string location) => location[(location.LastIndexOf('/') + 1)..];

    private static CancellationToken Ct => TestContext.Current.CancellationToken;
}
