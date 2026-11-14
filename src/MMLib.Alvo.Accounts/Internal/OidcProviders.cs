using Microsoft.Extensions.Options;
using System.Diagnostics.CodeAnalysis;

namespace MMLib.Alvo.Accounts.Internal;

/// <summary>The OIDC providers this deployment configured, found by the exact <c>iss</c> a token names (spec §5.2 step 1).</summary>
internal sealed class OidcProviders
{
    private readonly Dictionary<string, OidcProvider> _byIssuer = new(StringComparer.Ordinal);

    /// <summary>The providers <see cref="AlvoAccountsOptions.Providers"/> configure.</summary>
    /// <remarks>Only Google exists in this build; startup validation refuses any other name.</remarks>
    /// <param name="options">The accounts options.</param>
    public OidcProviders(IOptions<AlvoAccountsOptions> options)
        : this(FromOptions(options))
    {
    }

    /// <summary>The given providers.</summary>
    /// <param name="providers">The providers; no two may share an issuer.</param>
    /// <exception cref="InvalidOperationException">Two providers sign as one issuer.</exception>
    internal OidcProviders(IEnumerable<OidcProvider> providers)
    {
        ArgumentNullException.ThrowIfNull(providers);
        All = [.. providers];
        foreach (var provider in All)
        {
            AddIssuers(provider);
        }
    }

    /// <summary>Gets every configured provider.</summary>
    internal IReadOnlyList<OidcProvider> All { get; }

    /// <summary>The provider that signs as exactly <paramref name="issuer"/>.</summary>
    /// <param name="issuer">The token's unvalidated <c>iss</c>.</param>
    /// <param name="provider">The provider, when one signs as that issuer.</param>
    internal bool TryFindByIssuer(string issuer, [NotNullWhen(true)] out OidcProvider? provider)
        => _byIssuer.TryGetValue(issuer, out provider);

    /// <summary>The configured provider named <paramref name="name"/> — the name an identity and a session record.</summary>
    /// <param name="name">The provider's name in the descriptor, e.g. <c>google</c>.</param>
    /// <param name="provider">The provider, when this deployment configures one by that name.</param>
    internal bool TryFindByName(string name, [NotNullWhen(true)] out OidcProvider? provider)
    {
        provider = All.FirstOrDefault(candidate => string.Equals(candidate.Name, name, StringComparison.Ordinal));
        return provider is not null;
    }

    private void AddIssuers(OidcProvider provider)
    {
        foreach (var issuer in provider.Issuers)
        {
            if (!_byIssuer.TryAdd(issuer, provider))
            {
                throw new InvalidOperationException(
                    $"Identity providers '{_byIssuer[issuer].Name}' and '{provider.Name}' both sign as '{issuer}'.");
            }
        }
    }

    private static IEnumerable<OidcProvider> FromOptions(IOptions<AlvoAccountsOptions> options)
    {
        ArgumentNullException.ThrowIfNull(options);
        return options.Value.Providers
            .Where(entry => string.Equals(entry.Key, GooglePreset.Name, StringComparison.OrdinalIgnoreCase))
            .Select(entry => GooglePreset.From(entry.Value));
    }
}
