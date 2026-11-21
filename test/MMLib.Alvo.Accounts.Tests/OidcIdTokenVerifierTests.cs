using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Microsoft.IdentityModel.JsonWebTokens;
using MMLib.Alvo.Accounts.Internal;
using MMLib.Alvo.Descriptor;
using MMLib.Alvo.Tests.Accounts;
using System.Buffers.Text;
using System.Security.Cryptography;
using System.Text;

namespace MMLib.Alvo.Accounts.Tests;

/// <summary>
/// The ID-token verifier, one fact per step of OIDC Core §3.1.3.7 as spec §5.2 steps 1–3 apply it, and Google's
/// email-trust rule (§5.3), against an in-process issuer and a fixed clock.
/// </summary>
public sealed class OidcIdTokenVerifierTests : IDisposable
{
    private const string ClientId = "client-1";

    private const string MobileClientId = "client-1-ios";

    private static readonly DateTimeOffset _now = new(2026, 11, 13, 9, 0, 0, TimeSpan.Zero);

    private readonly FakeTimeProvider _clock = new(_now);

    private readonly TestOidcIssuer _issuer;

    private readonly ServiceProvider _services;

    /// <summary>Starts the issuer and the client factory pointed at it.</summary>
    public OidcIdTokenVerifierTests()
    {
        _issuer = new TestOidcIssuer(_clock);
        var services = new ServiceCollection();
        _issuer.UseIn(services);
        _services = services.BuildServiceProvider();
    }

    private static Func<string, bool> AnyProvider { get; } = _ => true;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <inheritdoc/>
    public void Dispose()
    {
        _services.Dispose();
        _issuer.Dispose();
    }

    [Fact]
    public async Task A_valid_id_token_yields_the_identity_and_a_subject_token_id_without_the_token()
    {
        using var verifier = Verifier(Google());
        var token = _issuer.Mint("sub-1", jti: "jti-1");

        var verification = await verifier.VerifyAsync(token, AnyProvider, Ct);

        verification.Refusal.ShouldBeNull();
        verification.Identity.ShouldBe(new VerifiedIdentity("google", "sub-1", "ada@gmail.com", true, "Ada Lovelace", "https://img.test/ada.png"));
        verification.SubjectTokenId.ShouldBe(Hash($"{TestOidcIssuer.Authority}\njti-1"));
        verification.ToString().ShouldNotContain(token.Split('.')[1]);
        verification.ToString().ShouldNotContain(verification.SubjectTokenId!);
    }

    /// <summary>
    /// #544: an <c>azp</c> names the OAuth client the person signed in through, so when present it must be one of the
    /// configured client ids — a mobile client's beside the web one the backend verifies against — and its absence changes
    /// nothing. A Google service account can mint an ID token for any <c>aud</c>, ours included, but its <c>azp</c> is its
    /// own numeric id, so it signs nobody in (the refusal cases above).
    /// </summary>
    [Theory]
    [InlineData(null)]
    [InlineData(ClientId)]
    [InlineData(MobileClientId)]
    public async Task An_azp_naming_a_configured_client_or_none_is_accepted(string? azp)
    {
        var options = Options(TestOidcIssuer.Authority);
        options.ClientIds.Add(MobileClientId);
        using var verifier = Verifier(GooglePreset.From(options) with { Issuers = new HashSet<string> { TestOidcIssuer.Authority } });

        var verification = await verifier.VerifyAsync(_issuer.Mint("sub-1", azp: azp), AnyProvider, Ct);

        verification.Refusal.ShouldBeNull();
        verification.Identity!.Subject.ShouldBe("sub-1");
    }

    [Fact]
    public async Task Without_a_jti_the_subject_token_id_is_the_issuer_and_the_token_s_hash()
    {
        using var verifier = Verifier(Google());
        var token = _issuer.Mint("sub-1", jti: TestOidcIssuer.NoJti);

        var verification = await verifier.VerifyAsync(token, AnyProvider, Ct);

        verification.SubjectTokenId.ShouldBe(Hash($"{TestOidcIssuer.Authority}\n{Hash(token)}"));
    }

    [Theory]
    [InlineData("issuer not configured", nameof(IdTokenRefusal.UnknownIssuer))]
    [InlineData("another app's client id", nameof(IdTokenRefusal.Audience))]
    [InlineData("ours beside another app's", nameof(IdTokenRefusal.Audience))]
    [InlineData("ours with a trailing slash", nameof(IdTokenRefusal.Audience))]
    [InlineData("azp a service account's numeric id", nameof(IdTokenRefusal.Audience))]
    [InlineData("azp another app's client id", nameof(IdTokenRefusal.Audience))]
    [InlineData("azp empty", nameof(IdTokenRefusal.Audience))]
    [InlineData("azp a number, unreadable to IdentityModel", nameof(IdTokenRefusal.Malformed))]
    [InlineData("azp an array naming ours, unreadable to IdentityModel", nameof(IdTokenRefusal.Malformed))]
    [InlineData("signed by another key", nameof(IdTokenRefusal.Signature))]
    [InlineData("alg none", nameof(IdTokenRefusal.Algorithm))]
    [InlineData("HS256", nameof(IdTokenRefusal.Algorithm))]
    [InlineData("exp 2 min + 1 s ago", nameof(IdTokenRefusal.Expired))]
    [InlineData("nbf 2 min + 1 s ahead", nameof(IdTokenRefusal.NotYetValid))]
    [InlineData("iat 5 min + 1 s ago", nameof(IdTokenRefusal.Stale))]
    [InlineData("iat 2 min + 1 s ahead", nameof(IdTokenRefusal.Stale))]
    [InlineData("not a JWT", nameof(IdTokenRefusal.Malformed))]
    [InlineData("three parts of garbage", nameof(IdTokenRefusal.Malformed))]
    [InlineData("no sub", nameof(IdTokenRefusal.Malformed))]
    [InlineData("iss null", nameof(IdTokenRefusal.Malformed))]
    [InlineData("iss empty", nameof(IdTokenRefusal.Malformed))]
    [InlineData("no iss", nameof(IdTokenRefusal.Malformed))]
    public async Task An_id_token_is_refused(string defect, string expected)
    {
        using var verifier = Verifier(Google());
        using var stranger = new TestOidcIssuer(_clock);
        var token = defect switch
        {
            "issuer not configured" => _issuer.Mint("sub-1", issuer: "https://elsewhere.test"),
            "another app's client id" => _issuer.Mint("sub-1", audience: "another-app"),
            "ours beside another app's" => _issuer.Mint("sub-1", audiences: [ClientId, "another-app"]),
            "ours with a trailing slash" => _issuer.Mint("sub-1", audience: ClientId + "/"),
            "azp a service account's numeric id" => _issuer.Mint(
                "112233445566778899000", email: "ci@project.iam.gserviceaccount.com", azp: "112233445566778899000"),
            "azp another app's client id" => _issuer.Mint("sub-1", azp: "another-app"),
            "azp empty" => MintWithAzp(string.Empty),
            "azp a number, unreadable to IdentityModel" => MintWithAzp(112233445566778899L),
            "azp an array naming ours, unreadable to IdentityModel" => MintWithAzp(new[] { ClientId }),
            "signed by another key" => stranger.Mint("sub-1"),
            "alg none" => _issuer.Mint("sub-1", alg: "none"),
            "HS256" => _issuer.Mint("sub-1", alg: "HS256"),
            "exp 2 min + 1 s ago" => _issuer.Mint("sub-1", issuedAt: _now.AddMinutes(-3), lifetime: TimeSpan.FromSeconds(59)),
            "nbf 2 min + 1 s ahead" => _issuer.Mint("sub-1", notBefore: _now.AddMinutes(2).AddSeconds(1)),
            "iat 5 min + 1 s ago" => _issuer.Mint("sub-1", issuedAt: _now.AddMinutes(-5).AddSeconds(-1)),
            "iat 2 min + 1 s ahead" => _issuer.Mint("sub-1", issuedAt: _now.AddMinutes(2).AddSeconds(1)),
            "not a JWT" => "not-a-token",
            "three parts of garbage" => "e30.e30.e30",
            "iss null" => IssuerDefects.Null,
            "iss empty" => IssuerDefects.Empty,
            "no iss" => IssuerDefects.Missing,
            _ => _issuer.Mint(string.Empty),
        };

        (await verifier.VerifyAsync(token, AnyProvider, Ct)).ShouldBe(IdentityVerification.Refused(Enum.Parse<IdTokenRefusal>(expected)));
    }

    /// <summary>
    /// OIDC Core §3.1.3.7 item 4: a token for several audiences must say which one it was issued to. Both audiences here
    /// are configured, so only the missing <c>azp</c> refuses it — and the operator's line names <c>azp</c>.
    /// </summary>
    [Fact]
    public async Task A_token_without_azp_and_several_audiences_is_refused()
    {
        var log = new CapturingLogger();
        var options = Options(TestOidcIssuer.Authority);
        options.ClientIds.Add(MobileClientId);
        using var verifier = Verifier(GooglePreset.From(options) with { Issuers = new HashSet<string> { TestOidcIssuer.Authority } }, log);

        var withoutAzp = await verifier.VerifyAsync(_issuer.Mint("sub-1", audiences: [ClientId, MobileClientId]), AnyProvider, Ct);
        var withAzp = await verifier.VerifyAsync(
            _issuer.Mint("sub-1", audiences: [ClientId, MobileClientId], azp: MobileClientId), AnyProvider, Ct);

        withoutAzp.ShouldBe(IdentityVerification.Refused(IdTokenRefusal.Audience));
        log.Entries.ShouldHaveSingleItem().Properties.ShouldContainKeyAndValue("Claim", "azp");
        withAzp.Refusal.ShouldBeNull();
    }

    [Fact]
    public async Task A_json_null_azp_is_refused_never_read_as_absent()
    {
        using var verifier = Verifier(Google());
        var token = MintWithAzp(null);

        var verification = await verifier.VerifyAsync(token, AnyProvider, Ct);

        Encoding.UTF8.GetString(Base64Url.DecodeFromChars(token.Split('.')[1])).ShouldContain("\"azp\":null");
        verification.ShouldBe(IdentityVerification.Refused(IdTokenRefusal.Audience));
    }

    /// <summary>
    /// The caller hears one <see cref="IdTokenRefusal.Audience"/> for a foreign <c>aud</c> and a foreign <c>azp</c> alike;
    /// the operator's log says which claim it was and for which provider, so a mobile client id missing from
    /// <c>ClientIds</c> can be told from a stranger's token — and never what the claim or the token said.
    /// </summary>
    [Theory]
    [InlineData("another app's client id", "aud")]
    [InlineData("ours beside another app's", "aud")]
    [InlineData("azp another app's client id", "azp")]
    public async Task An_audience_refusal_tells_the_operator_which_claim_failed_and_nothing_it_said(string defect, string claim)
    {
        var log = new CapturingLogger();
        using var verifier = Verifier(Google(), log);
        var token = defect switch
        {
            "another app's client id" => _issuer.Mint("sub-1", audience: "another-app"),
            "ours beside another app's" => _issuer.Mint("sub-1", audiences: [ClientId, "another-app"]),
            _ => _issuer.Mint("sub-1", azp: "another-app"),
        };

        var verification = await verifier.VerifyAsync(token, AnyProvider, Ct);

        verification.ShouldBe(IdentityVerification.Refused(IdTokenRefusal.Audience));
        var entry = log.Entries.ShouldHaveSingleItem();
        entry.Level.ShouldBe(LogLevel.Information);
        entry.Properties.ShouldContainKeyAndValue("Provider", "google");
        entry.Properties.ShouldContainKeyAndValue("Claim", claim);
        entry.Line.ShouldNotContain("another-app");
        entry.Line.ShouldNotContain("sub-1");
        token.Split('.').ShouldAllBe(part => !entry.Line.Contains(part));
    }

    [Fact]
    public async Task The_boundaries_are_accepted()
    {
        using var verifier = Verifier(Google());
        var token = _issuer.Mint("sub-1", issuedAt: _now.AddMinutes(-5), lifetime: TimeSpan.FromMinutes(3));

        var verification = await verifier.VerifyAsync(token, AnyProvider, Ct);

        verification.Refusal.ShouldBeNull();
        new JsonWebToken(token).ValidTo.ShouldBe(_now.AddMinutes(-2).UtcDateTime);
    }

    [Fact]
    public async Task Both_google_issuer_spellings_are_google()
    {
        using var verifier = Verifier(Google() with { Issuers = GooglePreset.Issuers });

        var https = await verifier.VerifyAsync(_issuer.Mint("sub-1", issuer: "https://accounts.google.com"), AnyProvider, Ct);
        var bare = await verifier.VerifyAsync(_issuer.Mint("sub-1", issuer: "accounts.google.com"), AnyProvider, Ct);
        var lookalike = await verifier.VerifyAsync(_issuer.Mint("sub-1", issuer: "https://accounts.google.com/"), AnyProvider, Ct);

        https.Identity.ShouldNotBeNull().Provider.ShouldBe("google");
        bare.Identity.ShouldNotBeNull().Provider.ShouldBe("google");
        lookalike.Refusal.ShouldBe(IdTokenRefusal.UnknownIssuer);
        GooglePreset.From(new AlvoOidcProviderOptions()).Authority.ShouldBe("https://accounts.google.com");
    }

    [Fact]
    public async Task An_unknown_kid_refetches_the_jwks_once_within_five_minutes()
    {
        using var verifier = Verifier(Google());
        (await verifier.VerifyAsync(_issuer.Mint("sub-1"), AnyProvider, Ct)).Refusal.ShouldBeNull();
        var fetches = _issuer.JwksFetches;

        _issuer.RotateKey("test-2");
        var rotated = await verifier.VerifyAsync(_issuer.Mint("sub-1"), AnyProvider, Ct);
        var afterRotation = _issuer.JwksFetches;
        _clock.Advance(TimeSpan.FromMinutes(5) - TimeSpan.FromSeconds(1));
        var invented = await verifier.VerifyAsync(_issuer.Mint("sub-1", kid: "test-3"), AnyProvider, Ct);
        var inventedAgain = await verifier.VerifyAsync(_issuer.Mint("sub-1", kid: "test-3"), AnyProvider, Ct);

        rotated.Refusal.ShouldBeNull();
        afterRotation.ShouldBe(fetches + 1);
        invented.Refusal.ShouldBe(IdTokenRefusal.Signature);
        inventedAgain.Refusal.ShouldBe(IdTokenRefusal.Signature);
        _issuer.JwksFetches.ShouldBe(afterRotation);
    }

    [Fact]
    public async Task After_five_minutes_an_unknown_kid_may_refetch_again()
    {
        using var verifier = Verifier(Google());
        await verifier.VerifyAsync(_issuer.Mint("sub-1", kid: "test-3"), AnyProvider, Ct);
        var fetches = _issuer.JwksFetches;

        _clock.Advance(TimeSpan.FromMinutes(5));
        var invented = await verifier.VerifyAsync(_issuer.Mint("sub-1", kid: "test-3"), AnyProvider, Ct);

        invented.Refusal.ShouldBe(IdTokenRefusal.Signature);
        _issuer.JwksFetches.ShouldBe(fetches + 1);
    }

    [Theory]
    [InlineData("ada@gmail.com", true, null, true)]
    [InlineData("ada@kros.sk", true, "kros.sk", true)]
    [InlineData("ada@kros.sk", true, null, false)]
    [InlineData("ada@gmail.com", false, null, false)]
    [InlineData("Ada@GMAIL.com", true, null, true)]
    [InlineData("ada@gmail.com.evil.test", true, null, false)]
    public async Task Google_trusts_an_email_only_when_verified_and_gmail_or_hosted(
        string email, bool verified, string? hd, bool trusted)
    {
        using var verifier = Verifier(Google());

        var verification = await verifier.VerifyAsync(_issuer.Mint("sub-1", email: email, emailVerified: verified, hd: hd), AnyProvider, Ct);

        var identity = verification.Identity.ShouldNotBeNull();
        identity.Email.ShouldBe(email);
        identity.EmailTrusted.ShouldBe(trusted);
    }

    [Theory]
    [InlineData(false, true, false)]
    [InlineData(true, false, false)]
    [InlineData(true, true, true)]
    public async Task A_generic_provider_trusts_email_only_when_configured_and_verified(bool trustEmail, bool verified, bool trusted)
    {
        using var verifier = Verifier(
            OidcProvider.Generic("oidc", AuthProvider.Oidc, TestOidcIssuer.Authority, Options(TestOidcIssuer.Authority), trustEmail));

        var verification = await verifier.VerifyAsync(_issuer.Mint("sub-1", email: "ada@example.com", emailVerified: verified), AnyProvider, Ct);

        verification.Identity.ShouldNotBeNull().EmailTrusted.ShouldBe(trusted);
        verification.Identity.Provider.ShouldBe("oidc");
    }

    [Fact]
    public async Task A_generic_provider_signs_only_with_the_discovered_asymmetric_algorithms()
    {
        using var verifier = Verifier(
            OidcProvider.Generic("oidc", AuthProvider.Oidc, TestOidcIssuer.Authority, Options(TestOidcIssuer.Authority), trustEmail: false));

        (await verifier.VerifyAsync(_issuer.Mint("sub-1", alg: "HS256"), AnyProvider, Ct)).Refusal.ShouldBe(IdTokenRefusal.Algorithm);
        (await verifier.VerifyAsync(_issuer.Mint("sub-1", alg: "none"), AnyProvider, Ct)).Refusal.ShouldBe(IdTokenRefusal.Algorithm);
        (await verifier.VerifyAsync(_issuer.Mint("sub-1"), AnyProvider, Ct)).Refusal.ShouldBeNull();
    }

    [Fact]
    public async Task An_http_authority_is_fetched_only_when_allowed()
    {
        var refused = Google(authority: "http://issuer.test");
        using var refusing = Verifier(refused);
        using var allowing = Verifier(refused with { AllowHttpMetadata = true });

        var withoutPermission = await refusing.VerifyAsync(_issuer.Mint("sub-1"), AnyProvider, Ct);
        var fetchesWithoutPermission = _issuer.DiscoveryFetches + _issuer.JwksFetches;
        var withPermission = await allowing.VerifyAsync(_issuer.Mint("sub-1"), AnyProvider, Ct);

        withoutPermission.Refusal.ShouldBe(IdTokenRefusal.Unreachable);
        fetchesWithoutPermission.ShouldBe(0);
        withPermission.Refusal.ShouldBeNull();
    }

    [Fact]
    public async Task An_unreachable_provider_is_refused_and_logged_without_the_token()
    {
        var log = new CapturingLogger();
        using var verifier = Verifier(Google(authority: TestOidcIssuer.Authority + "/missing"), log);
        var token = _issuer.Mint("sub-1");

        var verification = await verifier.VerifyAsync(token, AnyProvider, Ct);

        verification.ShouldBe(IdentityVerification.Refused(IdTokenRefusal.Unreachable));
        var line = log.Lines.ShouldHaveSingleItem();
        line.ShouldContain("https://issuer.test/missing");
        token.Split('.').ShouldAllBe(part => !line.Contains(part));
    }

    [Fact]
    public async Task Concurrent_attempts_against_a_dead_provider_make_one_fetch_and_the_backoff_makes_none()
    {
        using var verifier = Verifier(Google(authority: TestOidcIssuer.Authority + "/missing"));
        var token = _issuer.Mint("sub-1");

        var attempts = await Task.WhenAll(Enumerable.Range(0, 10).Select(_ => verifier.VerifyAsync(token, AnyProvider, Ct).AsTask()));
        _clock.Advance(OidcMetadata.FirstBackoff - TimeSpan.FromSeconds(1));
        var insideTheBackoff = await verifier.VerifyAsync(token, AnyProvider, Ct);

        attempts.ShouldAllBe(attempt => attempt.Refusal == IdTokenRefusal.Unreachable);
        insideTheBackoff.Refusal.ShouldBe(IdTokenRefusal.Unreachable);
        _issuer.Requests.ShouldBe(1);
    }

    [Fact]
    public async Task The_backoff_doubles_per_failure_up_to_its_cap()
    {
        using var verifier = Verifier(Google(authority: TestOidcIssuer.Authority + "/missing"));
        var token = _issuer.Mint("sub-1");
        var delays = new List<int>();

        await verifier.VerifyAsync(token, AnyProvider, Ct);
        for (var failure = 0; failure < 6; failure++)
        {
            var before = _issuer.Requests;
            var waited = 0;
            while (_issuer.Requests == before)
            {
                _clock.Advance(TimeSpan.FromSeconds(1));
                waited++;
                await verifier.VerifyAsync(_issuer.Mint("sub-1"), AnyProvider, Ct);
            }

            delays.Add(waited);
        }

        delays.ShouldBe([30, 60, 120, 240, 300, 300]);
    }

    [Fact]
    public async Task A_provider_the_caller_does_not_list_is_refused_before_anything_is_fetched()
    {
        using var verifier = Verifier(Google());
        var asked = new List<string>();

        var verification = await verifier.VerifyAsync(
            _issuer.Mint("sub-1"),
            provider =>
            {
                asked.Add(provider);
                return false;
            },
            Ct);

        verification.ShouldBe(IdentityVerification.Refused(IdTokenRefusal.NotListed));
        asked.ShouldBe(["google"]);
        _issuer.Requests.ShouldBe(0);
    }

    [Fact]
    public void Two_providers_signing_as_one_issuer_are_refused()
        => Should.Throw<InvalidOperationException>(() => new OidcProviders([Google(), Google() with { Name = "other" }]))
            .Message.ShouldContain(TestOidcIssuer.Authority);

    private OidcIdTokenVerifier Verifier(OidcProvider provider, ILogger<OidcIdTokenVerifier>? log = null) => new(
        new OidcProviders([provider]),
        _services.GetRequiredService<IHttpClientFactory>(),
        _clock,
        log ?? NullLogger<OidcIdTokenVerifier>.Instance);

    private static OidcProvider Google(string authority = TestOidcIssuer.Authority)
        => GooglePreset.From(Options(authority)) with { Issuers = new HashSet<string> { TestOidcIssuer.Authority } };

    private static AlvoOidcProviderOptions Options(string authority)
    {
        var options = new AlvoOidcProviderOptions { Authority = authority };
        options.ClientIds.Add(ClientId);
        return options;
    }

    private static string Hash(string text) => Base64Url.EncodeToString(SHA256.HashData(Encoding.UTF8.GetBytes(text)));

    private string MintWithAzp(object? azp) => _issuer.Mint("sub-1", rawClaims: new Dictionary<string, object?> { ["azp"] = azp });

    /// <summary>One logged entry: its level, its formatted line, and its structured properties.</summary>
    private sealed record LogEntry(LogLevel Level, string Line, IReadOnlyDictionary<string, object?> Properties);

    /// <summary>Every entry logged.</summary>
    private sealed class CapturingLogger : ILogger<OidcIdTokenVerifier>
    {
        private readonly List<LogEntry> _entries = [];

        public IReadOnlyList<LogEntry> Entries => _entries;

        public IReadOnlyList<string> Lines => [.. _entries.Select(entry => entry.Line)];

        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
            => _entries.Add(new LogEntry(
                logLevel,
                $"{eventId.Id} {formatter(state, exception)} {exception}",
                (state as IEnumerable<KeyValuePair<string, object?>> ?? []).ToDictionary(pair => pair.Key, pair => pair.Value)));
    }
}
