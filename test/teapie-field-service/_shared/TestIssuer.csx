// The field-service stack's TEST-ONLY OpenID issuer: the signing key, the key set the sink publishes for it,
// and the ID tokens 170-SelfService exchanges for an Alvo session.
//
// The stack's Google provider is Google's preset under another authority (docker-compose.field-service.yml):
// its discovery document is a committed WireMock mapping
// (examples/field-service/sink/mappings/test-oidc-issuer.json), and its jwks_uri points at
// http://localhost:9090/oidc/jwks.json — a stub this file registers in the sink, through WireMock's admin
// API, at the start of the run. So no private key is committed anywhere: the key is made on this machine the
// first time a run needs it and kept in the system temp directory, and only its public half ever leaves it.
//
// Why kept, and not made fresh every run: Alvo caches a provider's keys and re-fetches them for a `kid` it does
// not know at most once per five minutes (OidcMetadata.UnknownKidInterval). A fresh key per run would leave the
// third run in five minutes against a live stack refused for a reason that has nothing to do with the product.
// The `kid` is the key's RFC 7638 thumbprint, so a new key is always a new `kid` and never a stale cache hit.
//
// The tokens carry `iss: https://accounts.google.com`, because the preset finds its provider by that issuer;
// only an Alvo whose Google Authority is this sink — which it accepts over http in Development only — trusts
// them. A real deployment reads Google's own keys and refuses every token this key signs.
//
// Do not write the load directive's own spelling into a comment here (see Rows.csx for why). The directory
// holds no `-req.http`, so TeaPie collects no test case from it.

using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

const string GoogleIssuer = "https://accounts.google.com";

/// The path the committed discovery document's `jwks_uri` names, inside the sink.
const string TestIssuerJwksPath = "/oidc/jwks.json";

/// A fixed WireMock stub id, so publishing the key set again replaces the stub instead of adding a second one.
const string TestIssuerJwksStubId = "6f1d1a52-5e0c-4c1e-9a43-0f1e2d3c4b5a";

RSA _testIssuerKey;

/// The issuer's signing key: the one this machine already holds, or a new RSA-2048 key kept for the next run.
RSA TestIssuerKey() => _testIssuerKey ??= LoadOrCreateTestIssuerKey();

RSA LoadOrCreateTestIssuerKey()
{
    var file = Path.Combine(Path.GetTempPath(), "alvo-field-service-e2e", "test-issuer-key.pem");
    var key = RSA.Create();
    try
    {
        key.ImportFromPem(File.ReadAllText(file));
        return key;
    }
    catch (Exception failure) when (failure is IOException or ArgumentException or CryptographicException)
    {
        key.Dispose();
    }

    var created = RSA.Create(2048);
    Directory.CreateDirectory(Path.GetDirectoryName(file));
    File.WriteAllText(file, created.ExportPkcs8PrivateKeyPem());
    if (!OperatingSystem.IsWindows())
    {
        File.SetUnixFileMode(file, UnixFileMode.UserRead | UnixFileMode.UserWrite);
    }

    return created;
}

string Base64Url(byte[] bytes) => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

string Base64Url(string text) => Base64Url(Encoding.UTF8.GetBytes(text));

/// The key's `kid`: its RFC 7638 JWK thumbprint (SHA-256 over the required members, in lexicographic order).
string TestIssuerKid()
{
    var publicHalf = TestIssuerKey().ExportParameters(false);
    var canonical = $"{{\"e\":\"{Base64Url(publicHalf.Exponent)}\",\"kty\":\"RSA\",\"n\":\"{Base64Url(publicHalf.Modulus)}\"}}";
    return Base64Url(SHA256.HashData(Encoding.UTF8.GetBytes(canonical)));
}

/// The key set the sink serves: the public half only (RFC 7517 §6.3.1 — `n` and `e`, never `d`, `p` or `q`).
object TestIssuerKeySet()
{
    var publicHalf = TestIssuerKey().ExportParameters(false);
    return new
    {
        keys = new[]
        {
            new { kty = "RSA", use = "sig", alg = "RS256", kid = TestIssuerKid(), n = Base64Url(publicHalf.Modulus), e = Base64Url(publicHalf.Exponent) },
        },
    };
}

/// The body of WireMock's `POST /__admin/mappings/import` that serves the key set at the discovery document's
/// `jwks_uri`, replacing the stub a previous run registered.
string TestIssuerKeySetImport() => JsonSerializer.Serialize(new
{
    mappings = new[]
    {
        new
        {
            id = TestIssuerJwksStubId,
            name = "TEST-ONLY OpenID issuer key set for the field-service e2e (registered by test/teapie-field-service/_shared/TestIssuer.csx)",
            request = new { method = "GET", urlPath = TestIssuerJwksPath },
            response = new
            {
                status = 200,
                headers = new Dictionary<string, string> { ["Content-Type"] = "application/json" },
                jsonBody = TestIssuerKeySet(),
            },
        },
    },
    importOptions = new { duplicatePolicy = "OVERWRITE", deleteAllNotInImport = false },
});

/// An ID token as Google would sign it for this suite's client id, signed by the issuer's key. Its `azp` names that
/// client too, as Google's names the OAuth client a person signed in through: Alvo refuses an `azp` it does not list.
string IdToken(string subject, string email, string name) => SignedIdToken(TestIssuerKey(), subject, email, name);

/// A properly signed ID token whose `aud` or `azp` names a client this suite's Alvo does not list; `null` keeps
/// the suite's own client id for that claim.
string IdTokenForAnotherClient(string subject, string email, string name, string audience = null, string azp = null)
    => SignedIdToken(TestIssuerKey(), subject, email, name, audience, azp);

/// The same claims and `kid`, signed by a key the issuer never published — a forgery the signature must refuse.
string IdTokenSignedByAnotherKey(string subject, string email, string name)
{
    using var another = RSA.Create(2048);
    return SignedIdToken(another, subject, email, name);
}

string SignedIdToken(RSA key, string subject, string email, string name, string audience = null, string azp = null)
{
    var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
    var clientId = tp.GetVariable<string>("fsGoogleClientId");
    var header = JsonSerializer.Serialize(new { alg = "RS256", kid = TestIssuerKid(), typ = "JWT" });
    var payload = JsonSerializer.Serialize(new
    {
        iss = GoogleIssuer,
        aud = audience ?? clientId,
        azp = azp ?? clientId,
        sub = subject,
        email,
        email_verified = true,
        name,
        iat = now,
        exp = now + 3600,
        jti = Guid.NewGuid().ToString("N"),
    });
    var signingInput = Base64Url(header) + "." + Base64Url(payload);
    var signature = key.SignData(Encoding.UTF8.GetBytes(signingInput), HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
    return signingInput + "." + Base64Url(signature);
}
