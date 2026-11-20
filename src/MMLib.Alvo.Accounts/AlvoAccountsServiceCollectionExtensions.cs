using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.OpenApi;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using MMLib.Alvo.Accounts;
using MMLib.Alvo.Accounts.Internal;
using MMLib.Alvo.Auth;
using MMLib.Alvo.Auth.Internal;
using MMLib.Alvo.Secrets;
using MMLib.Alvo.Tenants.SelfService;

namespace Microsoft.Extensions.DependencyInjection;

/// <summary>Registers Alvo's end-user sign-in.</summary>
public static class AlvoAccountsServiceCollectionExtensions
{
    /// <summary>The largest discovery document or key set read: Google's are a few KiB.</summary>
    private const long MetadataMaxBytes = 256 * 1024;

    /// <summary>How long a provider's discovery document or keys may take to arrive.</summary>
    private static readonly TimeSpan _metadataTimeout = TimeSpan.FromSeconds(10);

    /// <summary>
    /// Adds end-user sign-in: its options, refused at startup when they cannot sign anyone in safely, and the services
    /// <see cref="Microsoft.AspNetCore.Builder.AlvoAccountsEndpointRouteBuilderExtensions.MapAlvoAccounts"/> maps.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>This call is the switch.</b> A host that never makes it keeps starting without a secrets key file and leaves
    /// the <c>Authorization</c> header to its own authentication. A host that makes it registers the person resolver, so
    /// an Alvo access token presented as <c>Authorization: Bearer</c> on the Data API is the person it names; and it must have a relational store
    /// (<c>UseSqlite</c> or <c>UsePostgreSql</c> inside <c>AddAlvo</c>), a secrets key file
    /// (<c>Alvo:Secrets:EncryptionKeyFile</c>), an API-key header other than <c>Authorization</c>, at least one
    /// provider, and — outside Development — an issuer; the start is refused, naming each, otherwise. It also gives
    /// <c>MapAlvo</c>'s Management API the operator's route that disables an account
    /// (<c>PUT {m}/projects/{project}/accounts/{account}/disabled</c>, at <c>admin</c>).
    /// </para>
    /// <para>
    /// <b>It binds nothing from configuration itself</b>, the <c>AddAlvoIdentity</c> precedent: the host decides where
    /// the settings come from, typically <c>configure: o =&gt; configuration.GetSection(AlvoAccounts.ConfigurationSection).Bind(o)</c>.
    /// </para>
    /// </remarks>
    /// <param name="services">The service collection to register into.</param>
    /// <param name="configure">Configures sign-in: the providers, the issuer and the lifetimes.</param>
    /// <returns><paramref name="services"/>, for chaining.</returns>
    public static IServiceCollection AddAlvoAccounts(
        this IServiceCollection services,
        Action<AlvoAccountsOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(services);

        AddValidatedOptions(services, configure);
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IHostedService, AccountsStartupWarnings>());
        AddVerification(services);
        AddTokenEndpoint(services);
        AddPerson(services);
        services.TryAddSingleton<IAlvoAccountAdministration, AccountAdministration>();
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IConfigureOptions<OpenApiOptions>, AccountsOpenApiSetup>());

        return services;
    }

    /// <summary>
    /// The ID-token verifier and the named client it fetches provider metadata with: bounded in time and size, because a
    /// sign-in waits on it, and without <c>IHttpClientFactory</c>'s logging handlers (only metadata URLs travel on it, but
    /// the webhook client's rule — the library decides what its clients log — holds here too).
    /// </summary>
    private static void AddVerification(IServiceCollection services)
    {
        services.TryAddSingleton<OidcProviders>();
        services.TryAddSingleton<OidcIdTokenVerifier>();
        services.TryAddSingleton<IIdentityVerifier>(provider => provider.GetRequiredService<OidcIdTokenVerifier>());
        services.AddHttpClient(OidcIdTokenVerifier.HttpClientName, client =>
        {
            client.Timeout = _metadataTimeout;
            client.MaxResponseContentBufferSize = MetadataMaxBytes;
        }).RemoveAllLoggers();
    }

    private static void AddTokenEndpoint(IServiceCollection services)
    {
        services.TryAddSingleton(TimeProvider.System);
        services.TryAddSingleton<AccountKeys>();
        services.TryAddSingleton<AccessTokens>();
        services.TryAddSingleton<DescriptorSignIn>();
        services.TryAddSingleton<TokenIssuance>();
        services.TryAddSingleton<ExchangeGrant>();
        services.TryAddSingleton<RefreshGrant>();
        services.TryAddSingleton<TokenEndpoint>();
    }

    /// <summary>
    /// The person resolver, under the key that opens the Bearer channel, and what it reads a session with. Scoped, so it is
    /// resolved from the request's own services; the request reaches it through <see cref="IHttpContextAccessor"/>.
    /// </summary>
    private static void AddPerson(IServiceCollection services)
    {
        services.AddHttpContextAccessor();
        services.TryAddSingleton<PersonSessions>();
        services.TryAddSingleton<SelfServiceMemberships>();
        services.TryAddKeyedScoped<IAlvoContextResolver, PersonResolver>(CallerResolution.PersonResolverKey);
    }

    private static void AddValidatedOptions(IServiceCollection services, Action<AlvoAccountsOptions>? configure)
    {
        var options = services.AddOptions<AlvoAccountsOptions>().ValidateOnStart();
        if (configure is not null)
        {
            options.Configure(configure);
        }

        services.TryAddEnumerable(
            ServiceDescriptor.Singleton<IValidateOptions<AlvoAccountsOptions>, AlvoAccountsOptionsValidation>(CreateValidation));
    }

    private static AlvoAccountsOptionsValidation CreateValidation(IServiceProvider provider) => new(
        provider.GetService<IHostEnvironment>(),
        provider.GetRequiredService<IOptions<AlvoSecretOptions>>(),
        provider.GetRequiredService<IOptions<AlvoAuthOptions>>(),
        provider.GetService<IServiceProviderIsService>());
}
