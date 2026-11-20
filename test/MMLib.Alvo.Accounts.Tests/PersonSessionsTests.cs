using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using MMLib.Alvo.Accounts.Internal;
using MMLib.Alvo.Rules;
using NSubstitute;

namespace MMLib.Alvo.Accounts.Tests;

/// <summary>
/// A host that has not applied a descriptor yet identifies no person: neither the Data API's resolver nor
/// <c>/logout</c>, which identify through <see cref="PersonSessions.IdentifyAsync"/>.
/// </summary>
public sealed class PersonSessionsTests : IDisposable
{
    private const string Issuer = "https://alvo.test/auth/v1";

    private const string Audience = "time-tracker";

    private static readonly AccessTokenSubject _subject = new(new UserId(Guid.NewGuid()), Guid.NewGuid(), "google");

    private readonly FakeTimeProvider _clock = new(new DateTimeOffset(2026, 11, 13, 9, 0, 0, TimeSpan.Zero));

    private readonly KeyStoreFake _store = new();

    private readonly AccountKeys _keys;

    /// <summary>Wires the keys over an in-memory store.</summary>
    public PersonSessionsTests() => _keys = new AccountKeys(_store, _clock);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <inheritdoc/>
    public void Dispose() => _keys.Dispose();

    [Theory]
    [InlineData(Audience)]
    [InlineData(null)]
    public async Task Without_an_applied_descriptor_a_genuine_token_identifies_nobody(string? configuredAudience)
    {
        var options = Options.Create(new AlvoAccountsOptions { Issuer = Issuer, Audience = configuredAudience });
        var tokens = new AccessTokens(_keys, options, _clock);
        var token = await tokens.MintAsync(_subject, Issuer, Audience, Ct);
        (await tokens.ValidateAsync(token, Issuer, Audience, Ct)).ShouldBe(_subject, "the token itself is genuine");
        var reads = _store.Reads;

        var session = await Sessions(tokens, options).IdentifyAsync(token, new DefaultHttpContext().Request, Ct);

        session.ShouldBeNull();
        _store.Reads.ShouldBe(reads, "nothing is validated, so no key is looked up");
    }

    private PersonSessions Sessions(AccessTokens tokens, IOptions<AlvoAccountsOptions> options)
    {
        var catalog = Substitute.For<IPolicyCatalogProvider>();
        catalog.Current.Returns((PolicyCatalog?)null);
        return new PersonSessions(tokens, _store, new DescriptorSignIn(catalog, new OidcProviders([])), options, _clock);
    }
}
