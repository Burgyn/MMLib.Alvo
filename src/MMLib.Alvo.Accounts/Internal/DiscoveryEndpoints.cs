using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;
using System.Buffers.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace MMLib.Alvo.Accounts.Internal;

/// <summary>
/// How a client discovers sign-in: RFC 8414 authorization-server metadata at <c>{prefix}/.well-known/oauth-authorization-server</c>
/// (spec §5.4) and at RFC 8414 §3.1's path-inserted <c>/.well-known/oauth-authorization-server{prefix}</c> (S2 D7), and the
/// JWKS (RFC 7517) the access tokens are signed under at <c>{prefix}/.well-known/jwks.json</c>.
/// </summary>
/// <remarks>
/// <para>
/// <b>Every URL comes from the issuer</b> (<see cref="AccountsIssuer.IssuerFor"/>), which is the public URL of the prefix:
/// RFC 8414 §3.3 requires the metadata's <c>issuer</c> to be the one the token's <c>iss</c> carries, and deriving the other
/// two from it keeps them on the same origin. In Development without a configured issuer, that is the URL the request
/// reached — the one place the request may choose it.
/// </para>
/// <para>
/// <b>The JWKS publishes the public half only</b>, built from the curve point, never by converting the key itself: the
/// held key carries its private scalar, and a converter that copies <c>d</c> when it is present would publish it.
/// </para>
/// <para>
/// <b>Both answers are cacheable for five minutes</b> (<c>Cache-Control: public, max-age=300</c>), the interval a replica
/// reloads its keys on an unknown <c>kid</c> — so a client's cached key set and a replica's are equally fresh.
/// </para>
/// </remarks>
internal static class DiscoveryEndpoints
{
    /// <summary>The operation id of the authorization-server metadata.</summary>
    internal const string MetadataOperationId = "auth.metadata";

    /// <summary>The operation id of the key set.</summary>
    internal const string JwksOperationId = "auth.jwks";

    /// <summary>RFC 8414 §3's well-known suffix.</summary>
    internal const string MetadataPath = "/.well-known/oauth-authorization-server";

    /// <summary>Where the key set is published, under the prefix.</summary>
    internal const string JwksPath = "/.well-known/jwks.json";

    /// <summary>The <c>Cache-Control</c> both answers carry.</summary>
    internal const string Cacheable = "public, max-age=300";

    private static readonly JsonSerializerOptions _json = new(JsonSerializerDefaults.General);

    /// <summary>Maps the metadata (at both spellings) and the key set.</summary>
    /// <param name="endpoints">The route builder the sign-in group was created on, for the path-inserted metadata URL.</param>
    /// <param name="group">The sign-in group.</param>
    /// <param name="prefix">The group's prefix.</param>
    internal static void Map(IEndpointRouteBuilder endpoints, RouteGroupBuilder group, string prefix)
    {
        ArgumentNullException.ThrowIfNull(endpoints);
        ArgumentNullException.ThrowIfNull(group);
        group.MapGet(MetadataPath, Metadata).AddEndpointFilter(CacheFor).WithName(MetadataOperationId);
        group.MapGet(JwksPath, JwksAsync).AddEndpointFilter(CacheFor).WithName(JwksOperationId);
        var inserted = prefix.TrimEnd('/');
        if (inserted.Length > 0)
        {
            endpoints.MapGet(MetadataPath + inserted, Metadata).AddEndpointFilter(CacheFor).ExcludeFromDescription();
        }
    }

    /// <summary>The metadata document (RFC 8414 §2) for the request's issuer.</summary>
    /// <param name="request">The request, consulted for the issuer in Development only.</param>
    /// <param name="options">The accounts options.</param>
    internal static IResult Metadata(HttpRequest request, IOptions<AlvoAccountsOptions> options)
    {
        ArgumentNullException.ThrowIfNull(options);
        var issuer = AccountsIssuer.IssuerFor(request, options.Value);
        var origin = issuer.TrimEnd('/');
        return Results.Json(
            new JsonObject
            {
                ["issuer"] = issuer,
                ["token_endpoint"] = origin + "/token",
                ["jwks_uri"] = origin + JwksPath,
                ["grant_types_supported"] = new JsonArray(ExchangeGrant.GrantType, RefreshGrant.GrantType),
                ["token_endpoint_auth_methods_supported"] = new JsonArray("none"),
                ["response_types_supported"] = new JsonArray(),
            },
            _json,
            contentType: "application/json");
    }

    private static async Task<IResult> JwksAsync(AccountKeys keys, HttpContext http)
    {
        var published = await keys.PublishedAsync(http.RequestAborted).ConfigureAwait(false);
        return Results.Json(
            new JsonObject { ["keys"] = new JsonArray([.. published.Select(Jwk)]) },
            _json,
            contentType: "application/json");
    }

    /// <summary>One signing key's public half as a JWK (RFC 7518 §6.2.1): the curve point, and nothing private.</summary>
    /// <param name="key">A signing key.</param>
    internal static JsonObject Jwk(SigningKey key)
    {
        ArgumentNullException.ThrowIfNull(key);
        var point = key.Key.ECDsa.ExportParameters(includePrivateParameters: false).Q;
        return new JsonObject
        {
            ["kty"] = "EC",
            ["crv"] = "P-256",
            ["x"] = Base64Url.EncodeToString(point.X),
            ["y"] = Base64Url.EncodeToString(point.Y),
            ["kid"] = key.Kid,
            ["use"] = "sig",
            ["alg"] = SecurityAlgorithms.EcdsaSha256,
        };
    }

    private static ValueTask<object?> CacheFor(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        context.HttpContext.Response.Headers.CacheControl = Cacheable;
        return next(context);
    }
}
