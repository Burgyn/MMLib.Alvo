using Microsoft.Extensions.DependencyInjection;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;
using System.Buffers.Text;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace MMLib.Alvo.Tests.Accounts;

/// <summary>
/// An OpenID Connect issuer in process: it owns RSA-2048 signing keys, serves its discovery document and JWKS through
/// <see cref="Handler"/>, counts every JWKS fetch, and mints ID tokens shaped like Google's — or deliberately not.
/// </summary>
/// <remarks>
/// <see cref="Handler"/> answers by path alone, so the same issuer serves <c>https://issuer.test</c> and
/// <c>http://issuer.test</c>; its discovery document names the JWKS at the scheme and host it was asked on.
/// </remarks>
public sealed class TestOidcIssuer : IDisposable
{
    /// <summary>The issuer's authority, and the <c>iss</c> its tokens carry unless told otherwise.</summary>
    public const string Authority = "https://issuer.test";

    /// <summary>Passed as <c>jti</c> to <see cref="Mint"/>, mints a token that carries none.</summary>
    public const string NoJti = "";

    /// <summary>The named client the accounts package fetches provider metadata with.</summary>
    public const string HttpClientName = "alvo-accounts-oidc";

    private const string DiscoveryPath = "/.well-known/openid-configuration";

    private const string JwksPath = "/jwks";

    private readonly TimeProvider _clock;

    private readonly List<RsaSecurityKey> _keys = [];

    private int _jwksFetches;

    private int _discoveryFetches;

    /// <summary>Creates an issuer whose first key is <c>test-1</c>.</summary>
    /// <param name="clock">The clock a token's default <c>iat</c> comes from; the system clock when omitted.</param>
    public TestOidcIssuer(TimeProvider? clock = null)
    {
        _clock = clock ?? TimeProvider.System;
        _keys.Add(NewKey("test-1"));
        Handler = new IssuerHandler(this);
    }

    /// <summary>Gets the handler serving the discovery document and the JWKS.</summary>
    public HttpMessageHandler Handler { get; }

    /// <summary>Gets how many times the JWKS was fetched.</summary>
    public int JwksFetches => Volatile.Read(ref _jwksFetches);

    /// <summary>Gets how many times the discovery document was fetched.</summary>
    public int DiscoveryFetches => Volatile.Read(ref _discoveryFetches);

    private RsaSecurityKey Current => _keys[^1];

    /// <inheritdoc/>
    public void Dispose()
    {
        Handler.Dispose();
        foreach (var key in _keys)
        {
            key.Rsa.Dispose();
        }
    }

    /// <summary>Starts signing with a new key, <paramref name="kid"/>; the JWKS publishes it beside the old ones.</summary>
    /// <param name="kid">The new key's id.</param>
    public void RotateKey(string kid) => _keys.Add(NewKey(kid));

    /// <summary>Points the accounts package's metadata client at this issuer.</summary>
    /// <param name="services">The host's services.</param>
    public void UseIn(IServiceCollection services)
        => services.AddHttpClient(HttpClientName).ConfigurePrimaryHttpMessageHandler(() => new IssuerHandler(this));

    /// <summary>Mints an ID token.</summary>
    /// <param name="subject">The <c>sub</c>.</param>
    /// <param name="email">The <c>email</c>, or none.</param>
    /// <param name="emailVerified">The <c>email_verified</c>.</param>
    /// <param name="hd">The Workspace domain (<c>hd</c>), or none.</param>
    /// <param name="audience">The <c>aud</c>.</param>
    /// <param name="issuedAt">The <c>iat</c>; the clock's now when omitted.</param>
    /// <param name="lifetime">From <c>iat</c> to <c>exp</c>; an hour when omitted.</param>
    /// <param name="jti">The <c>jti</c>; a fresh one when omitted, none for <see cref="NoJti"/>.</param>
    /// <param name="alg">The <c>alg</c>: <c>RS256</c> when omitted, <c>none</c> unsigned, <c>HS256</c> MACed with the public key.</param>
    /// <param name="kid">The <c>kid</c> header; the signing key's own when omitted.</param>
    /// <param name="issuer">The <c>iss</c>; <see cref="Authority"/> when omitted.</param>
    /// <param name="notBefore">The <c>nbf</c>, or none.</param>
    public string Mint(
        string subject,
        string? email = "ada@gmail.com",
        bool emailVerified = true,
        string? hd = null,
        string audience = "client-1",
        DateTimeOffset? issuedAt = null,
        TimeSpan? lifetime = null,
        string? jti = null,
        string? alg = null,
        string? kid = null,
        string? issuer = null,
        DateTimeOffset? notBefore = null)
    {
        var iat = issuedAt ?? _clock.GetUtcNow();
        var claims = Claims(subject, email, emailVerified, hd, jti);
        var descriptor = new SecurityTokenDescriptor
        {
            TokenType = "JWT",
            Issuer = issuer ?? Authority,
            Audience = audience,
            IssuedAt = iat.UtcDateTime,
            NotBefore = notBefore?.UtcDateTime,
            Expires = (iat + (lifetime ?? TimeSpan.FromHours(1))).UtcDateTime,
            Claims = claims,
        };
        return alg switch
        {
            "none" => Unsigned(descriptor, kid ?? Current.KeyId),
            "HS256" => Sign(descriptor, new SymmetricSecurityKey(PublicKeyBytes()) { KeyId = kid ?? Current.KeyId }, "HS256"),
            _ => Sign(descriptor, KeyNamed(kid), SecurityAlgorithms.RsaSha256),
        };
    }

    private static Dictionary<string, object> Claims(string subject, string? email, bool emailVerified, string? hd, string? jti)
    {
        var claims = new Dictionary<string, object>
        {
            ["sub"] = subject,
            ["email_verified"] = emailVerified,
            ["name"] = "Ada Lovelace",
            ["picture"] = "https://img.test/ada.png",
        };
        AddUnlessEmpty(claims, "email", email);
        AddUnlessEmpty(claims, "hd", hd);
        AddUnlessEmpty(claims, "jti", jti ?? Guid.NewGuid().ToString("N"));
        return claims;
    }

    private static void AddUnlessEmpty(Dictionary<string, object> claims, string name, string? value)
    {
        if (!string.IsNullOrEmpty(value))
        {
            claims[name] = value;
        }
    }

    private static string Sign(SecurityTokenDescriptor descriptor, SecurityKey key, string algorithm)
    {
        descriptor.SigningCredentials = new SigningCredentials(key, algorithm);
        return new JsonWebTokenHandler { SetDefaultTimesOnTokenCreation = false }.CreateToken(descriptor);
    }

    private string Unsigned(SecurityTokenDescriptor descriptor, string kid)
    {
        var signed = Sign(descriptor, Current, SecurityAlgorithms.RsaSha256);
        var header = $$"""{"alg":"none","kid":"{{kid}}","typ":"JWT"}""";
        return $"{Base64Url.EncodeToString(Encoding.UTF8.GetBytes(header))}.{signed.Split('.')[1]}.";
    }

    private RsaSecurityKey KeyNamed(string? kid)
    {
        if (kid is null)
        {
            return Current;
        }

        return new RsaSecurityKey(Current.Rsa) { KeyId = kid };
    }

    private byte[] PublicKeyBytes() => Current.Rsa.ExportSubjectPublicKeyInfo();

    private static RsaSecurityKey NewKey(string kid) => new(RSA.Create(2048)) { KeyId = kid };

    private HttpResponseMessage Answer(HttpRequestMessage request)
    {
        var origin = request.RequestUri!.GetLeftPart(UriPartial.Authority);
        return request.RequestUri.AbsolutePath switch
        {
            DiscoveryPath => Json(Discovery(origin), ref _discoveryFetches),
            JwksPath => Json(Jwks(), ref _jwksFetches),
            _ => new HttpResponseMessage(HttpStatusCode.NotFound),
        };
    }

    private static Dictionary<string, object> Discovery(string origin) => new Dictionary<string, object>
    {
        ["issuer"] = Authority,
        ["jwks_uri"] = origin + JwksPath,
        ["id_token_signing_alg_values_supported"] = new[] { "RS256" },
        ["response_types_supported"] = new[] { "id_token" },
        ["subject_types_supported"] = new[] { "public" },
    };

    private Dictionary<string, object> Jwks() => new Dictionary<string, object>
    {
        ["keys"] = _keys.Select(PublicJwk).ToArray(),
    };

    private static Dictionary<string, string> PublicJwk(RsaSecurityKey key)
    {
        var parameters = key.Rsa.ExportParameters(includePrivateParameters: false);
        return new Dictionary<string, string>
        {
            ["kty"] = "RSA",
            ["use"] = "sig",
            ["alg"] = "RS256",
            ["kid"] = key.KeyId,
            ["n"] = Base64Url.EncodeToString(parameters.Modulus),
            ["e"] = Base64Url.EncodeToString(parameters.Exponent),
        };
    }

    private static HttpResponseMessage Json(object body, ref int counter)
    {
        Interlocked.Increment(ref counter);
        return new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json"),
        };
    }

    /// <summary>Serves the issuer; owns nothing, so the client factory may dispose it freely.</summary>
    private sealed class IssuerHandler(TestOidcIssuer issuer) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(issuer.Answer(request));
    }
}
