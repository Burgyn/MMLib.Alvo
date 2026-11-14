using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Microsoft.Net.Http.Headers;
using MMLib.Alvo.Auth;
using MMLib.Alvo.Secrets;

namespace MMLib.Alvo.Accounts.Internal;

/// <summary>
/// Refuses a host that cannot sign anyone in safely, at startup, naming every setting at fault — <c>extensibility.md</c>
/// rule 5 for <see cref="AlvoAccountsOptions"/>.
/// </summary>
/// <remarks>
/// <para>
/// <b>Registered by the package with <c>ValidateOnStart</c>, so both distributions are covered</b> (the
/// <c>AlvoIdentityOptionsValidation</c> precedent): an embedded host calls <c>AddAlvoAccounts</c> directly and reaches
/// none of the standalone host's checks.
/// </para>
/// <para>
/// <b>It also judges three settings this package does not own</b>, because each becomes wrong only once accounts are
/// on: the secrets key file the signing keys are sealed with (spec §5.1), the API-key header (the Bearer channel needs
/// <c>Authorization</c>), and a relational store to keep accounts in. Each would otherwise fail on the first sign-in,
/// or — the header — never fail at all and silently keep the Bearer channel closed.
/// </para>
/// </remarks>
/// <param name="environment">The host's environment, or <see langword="null"/>, which is judged as production.</param>
/// <param name="secrets">The secrets options, for the key file.</param>
/// <param name="auth">The API-key options, for the header the API key is read from.</param>
/// <param name="services">Answers whether an account store is registered, without constructing one.</param>
internal sealed class AlvoAccountsOptionsValidation(
    IHostEnvironment? environment,
    IOptions<AlvoSecretOptions> secrets,
    IOptions<AlvoAuthOptions> auth,
    IServiceProviderIsService? services) : IValidateOptions<AlvoAccountsOptions>
{
    private const string Section = AlvoAccounts.ConfigurationSection;

    private const string ProvidersKey = Section + ":" + nameof(AlvoAccountsOptions.Providers);

    private const string KeyFileKey = AlvoSecretOptions.ConfigurationSection + ":" + nameof(AlvoSecretOptions.EncryptionKeyFile);

    private const string HeaderNameKey = "Alvo:Auth:" + nameof(AlvoAuthOptions.HeaderName);

    private const int MaxAccessTokenMinutes = 60;

    private const int MaxGraceSeconds = 60;

    /// <summary>The providers this build can verify an ID token from; S2 ships the Google preset alone.</summary>
    private static readonly HashSet<string> _availableProviders = new(StringComparer.OrdinalIgnoreCase) { "Google" };

    /// <inheritdoc/>
    public ValidateOptionsResult Validate(string? name, AlvoAccountsOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        string[] failures = [.. IssuerFailures(options), .. PrefixFailures(options), .. LifetimeFailures(options),
            .. ProviderFailures(options), .. HostFailures()];

        return failures.Length is 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(failures);
    }

    private IEnumerable<string> IssuerFailures(AlvoAccountsOptions options)
    {
        const string key = Section + ":" + nameof(AlvoAccountsOptions.Issuer);
        if (string.IsNullOrWhiteSpace(options.Issuer))
        {
            if (!IsDevelopment)
            {
                yield return $"{key} is not set. Outside the Development environment the issuer every access token is "
                    + $"signed as must be configured, not guessed from a request: set {key} to the public https URL of "
                    + $"{Section}:{nameof(AlvoAccountsOptions.Prefix)} (for example https://api.example.com/auth/v1).";
            }

            yield break;
        }

        if (!IsHttpsUri(options.Issuer))
        {
            yield return $"{key} is '{options.Issuer}', which is not an absolute https URI. "
                + "Set it to the public https URL tokens are issued from (for example https://api.example.com/auth/v1)"
                + (IsDevelopment ? ", or — in Development only — leave it unset to issue from the URL each request reached." : ".");
        }
    }

    private static IEnumerable<string> PrefixFailures(AlvoAccountsOptions options)
    {
        if (string.IsNullOrEmpty(options.Prefix) || options.Prefix[0] != '/')
        {
            yield return $"{Section}:{nameof(AlvoAccountsOptions.Prefix)} is '{options.Prefix}'; it must start with '/' "
                + "(the default is '/auth/v1').";
        }
    }

    private static IEnumerable<string> LifetimeFailures(AlvoAccountsOptions options)
    {
        if (options.AccessTokenMinutes is < 1 or > MaxAccessTokenMinutes)
        {
            yield return OutOfRange(nameof(AlvoAccountsOptions.AccessTokenMinutes), options.AccessTokenMinutes, "1 to 60");
        }

        if (options.RefreshIdleDays < 1)
        {
            yield return OutOfRange(nameof(AlvoAccountsOptions.RefreshIdleDays), options.RefreshIdleDays, "at least 1");
        }

        if (options.SessionLifetimeDays < options.RefreshIdleDays)
        {
            yield return OutOfRange(nameof(AlvoAccountsOptions.SessionLifetimeDays), options.SessionLifetimeDays,
                $"at least {Section}:{nameof(AlvoAccountsOptions.RefreshIdleDays)} ({options.RefreshIdleDays})");
        }

        if (options.RefreshReuseGraceSeconds is < 0 or > MaxGraceSeconds)
        {
            yield return OutOfRange(nameof(AlvoAccountsOptions.RefreshReuseGraceSeconds), options.RefreshReuseGraceSeconds,
                "0 to 60");
        }
    }

    private static IEnumerable<string> ProviderFailures(AlvoAccountsOptions options)
    {
        if (options.Providers.Count is 0)
        {
            return [$"{ProvidersKey} names no identity provider, so nobody could sign in. Configure one, for example "
                + $"{ProvidersKey}:Google:ClientIds:0=<your OAuth client id>."];
        }

        return options.Providers.SelectMany(provider => ProviderFailures(provider.Key, provider.Value));
    }

    private static IEnumerable<string> ProviderFailures(string name, AlvoOidcProviderOptions provider)
    {
        var key = $"{ProvidersKey}:{name}";
        if (!_availableProviders.Contains(name))
        {
            return [$"{key}: the provider '{name}' is not available in this build. Available: "
                + $"{string.Join(", ", _availableProviders)}."];
        }

        return [.. ClientIdFailures(key, provider), .. AuthorityFailures(key, provider)];
    }

    private static IEnumerable<string> ClientIdFailures(string key, AlvoOidcProviderOptions provider)
    {
        if (provider.ClientIds.Count is 0 || provider.ClientIds.Any(string.IsNullOrWhiteSpace))
        {
            yield return $"{key}:{nameof(AlvoOidcProviderOptions.ClientIds)} must list at least one OAuth client id, and "
                + "no empty one: an ID token is accepted only when its audience is one of them.";
        }
    }

    private static IEnumerable<string> AuthorityFailures(string key, AlvoOidcProviderOptions provider)
    {
        if (provider.Authority is not { } authority)
        {
            yield break;
        }

        var authorityKey = $"{key}:{nameof(AlvoOidcProviderOptions.Authority)}";
        if (!Uri.TryCreate(authority, UriKind.Absolute, out var uri)
            || (uri.Scheme != Uri.UriSchemeHttps && uri.Scheme != Uri.UriSchemeHttp))
        {
            yield return $"{authorityKey} is '{authority}', which is not an absolute "
                + (provider.AllowHttpMetadata ? "http or https" : "https") + " URI.";
        }
        else if (uri.Scheme == Uri.UriSchemeHttp && !provider.AllowHttpMetadata)
        {
            yield return $"{authorityKey} is '{authority}', an http:// authority: its signing keys could be replaced by "
                + "anyone on the network path. Use https, or — for a test issuer only — set "
                + $"{key}:{nameof(AlvoOidcProviderOptions.AllowHttpMetadata)}=true.";
        }
    }

    private IEnumerable<string> HostFailures()
    {
        if (KeyFileFailure() is { } keyFile)
        {
            yield return keyFile;
        }

        if (string.Equals(auth.Value.HeaderName.Trim(), HeaderNames.Authorization, StringComparison.OrdinalIgnoreCase))
        {
            yield return $"{HeaderNameKey} is '{auth.Value.HeaderName}', but end-user sign-in needs the Authorization "
                + "header for its Bearer channel, and two channels cannot own one header. Read the API key from "
                + "another header (the default is 'X-Alvo-Api-Key').";
        }

        if (services?.IsService(typeof(IAccountStore)) is not true)
        {
            yield return "Accounts need a relational data provider: UseSqlite or UsePostgreSql inside AddAlvo(...).";
        }
    }

    private string? KeyFileFailure()
    {
        var path = secrets.Value.EncryptionKeyFile;
        if (string.IsNullOrWhiteSpace(path))
        {
            return $"{KeyFileKey} is not set. End-user sign-in seals its signing keys with the deployment's secrets "
                + $"key: point {KeyFileKey} at a file holding 32 bytes of base64.";
        }

        return File.Exists(path)
            ? null
            : $"{KeyFileKey} is '{path}', and no file is there. Mount the secrets key file (32 bytes of base64) at "
                + "that path, or point the setting at where it is mounted.";
    }

    private bool IsDevelopment => environment?.IsDevelopment() is true;

    private static bool IsHttpsUri(string value)
        => Uri.TryCreate(value, UriKind.Absolute, out var uri) && uri.Scheme == Uri.UriSchemeHttps;

    private static string OutOfRange(string option, int value, string range)
        => $"{Section}:{option} is {value}; it must be {range}.";
}
