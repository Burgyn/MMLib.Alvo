using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using MMLib.Alvo;
using MMLib.Alvo.Auth;
using MMLib.Alvo.Identity;
using MMLib.Alvo.Identity.Internal;

namespace Microsoft.Extensions.DependencyInjection;

/// <summary>Registers Alvo's human identity subsystem.</summary>
public static class AlvoIdentityServiceCollectionExtensions
{
    /// <summary>
    /// Adds ASP.NET Core Identity's stores, the <see cref="IAlvoUserStore"/> over them, the cookie
    /// <see cref="IAlvoContextResolver"/> (under <see cref="AlvoIdentity.ResolverKey"/>), and the
    /// bootstrap administrator.
    /// </summary>
    /// <remarks>
    /// <b>The cookie resolver is registered keyed, deliberately.</b> The unkeyed
    /// <see cref="IAlvoContextResolver"/> is the one the Data API hands the raw API-key header to, and
    /// this one's "presented key" is a subject ASP.NET Core already authenticated — so replacing the
    /// unkeyed registration would make a user's uuid a working API key. A fact holds that line.
    /// </remarks>
    /// <param name="services">The service collection to register into.</param>
    /// <param name="configureStore">Configures the identity store's database — the provider and its connection.</param>
    /// <param name="configure">Configures the bootstrap administrator, if there is one.</param>
    /// <returns><paramref name="services"/>, for chaining.</returns>
    public static IServiceCollection AddAlvoIdentity(
        this IServiceCollection services,
        Action<DbContextOptionsBuilder> configureStore,
        Action<AlvoIdentityOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configureStore);

        AddStore(services, configureStore);

        /* Data protection, because the credential token is a protected payload and the provider it
           needs is not in a bare container. A web host usually has one already and this call is
           idempotent — it returns a builder over the same registrations — so a deployment that
           configures its own key ring keeps it. Registering it here rather than leaving it to the
           host is what makes `IssueCredentialTokenAsync` work in every composition that has the
           member, instead of in the ones that happen to be web hosts. */
        services.AddDataProtection();

        AddIdentityCore(services);

        AddValidatedOptions(services, configure);

        services.TryAddScoped<IAlvoUserStore, AlvoIdentityUserStore>();
        services.TryAddSingleton<AlvoBootstrapAdmin>();
        services.Replace(ServiceDescriptor.Singleton<IAlvoBootstrapAdmin>(
            provider => provider.GetRequiredService<AlvoBootstrapAdmin>()));
        services.AddKeyedScoped<IAlvoContextResolver, AlvoIdentityContextResolver>(AlvoIdentity.ResolverKey);

        /* Keyed, and the key is the guard. The core registers a guarded decorator under the plain
           IAlvoUserAdministration and resolves this one through the key — so there is no
           registration anywhere that hands an in-process caller the unguarded implementation. A
           guard living inside this adapter would be optional by construction: the next
           implementation simply would not have it. */
        services.AddKeyedScoped<IAlvoUserAdministration, AlvoIdentityUserAdministration>(
            AlvoUserAdministration.UnguardedKey);
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IHostedService, AlvoIdentityBootstrap>());

        return services;
    }

    /// <summary>
    /// Registers the identity store, with the host's own provider configuration and Alvo's model cache key.
    /// </summary>
    /// <remarks>
    /// <b>The cache key is not optional.</b> <see cref="AlvoIdentityDbContext"/> names its tables from
    /// <see cref="AlvoOptions.SchemaPrefix"/>, and EF caches a built model under a key that is the context
    /// type alone — in a cache that lives in EF's process-wide internal service provider. Two containers
    /// under two prefixes would share whichever model was built first;
    /// <see cref="AlvoIdentityModelCacheKeyFactory"/> is what makes the prefix part of that key.
    /// </remarks>
    /// <param name="services">The service collection to register into.</param>
    /// <param name="configureStore">Configures the identity store's database — the provider and its connection.</param>
    private static void AddStore(IServiceCollection services, Action<DbContextOptionsBuilder> configureStore) =>
        services.AddDbContext<AlvoIdentityDbContext>(store =>
        {
            configureStore(store);
            store.ReplaceService<IModelCacheKeyFactory, AlvoIdentityModelCacheKeyFactory>();
        });

    /// <summary>
    /// Binds <see cref="AlvoIdentityOptions"/> and refuses a misconfigured bootstrap administrator at
    /// startup — <c>extensibility.md</c> rule 5.
    /// </summary>
    /// <remarks>
    /// <b>The package validates itself rather than leaving it to the standalone host.</b> An embedded
    /// host calls this method directly and reaches none of the host's validation, so a check that
    /// lived only there would leave half the distributions unchecked — with the misconfiguration
    /// surfacing as a bootstrap that silently seeded nothing.
    /// </remarks>
    /// <param name="services">The service collection to register into.</param>
    /// <param name="configure">Configures the bootstrap administrator, if there is one.</param>
    private static void AddValidatedOptions(IServiceCollection services, Action<AlvoIdentityOptions>? configure)
    {
        var options = services.AddOptions<AlvoIdentityOptions>().ValidateOnStart();
        if (configure is not null)
        {
            options.Configure(configure);
        }

        services.TryAddEnumerable(
            ServiceDescriptor.Singleton<IValidateOptions<AlvoIdentityOptions>, AlvoIdentityOptionsValidation>());
    }

    /// <summary>Adds ASP.NET Core Identity's user and role managers over the Alvo identity store.</summary>
    /// <param name="services">The service collection to register into.</param>
    private static void AddIdentityCore(IServiceCollection services) =>
        services.AddIdentityCore<AlvoIdentityUser>()
            .AddRoles<AlvoIdentityRole>()
            .AddEntityFrameworkStores<AlvoIdentityDbContext>()
            /* One token provider, named, rather than AddDefaultTokenProviders().

               It is what `IAlvoUserAdministration.IssueCredentialTokenAsync` stands on: without it
               Identity throws "no IUserTwoFactorTokenProvider named 'Default' is registered" the
               first time an administrator lets a colleague set a password. AddIdentityCore
               deliberately registers none — it is the minimal composition.

               The default set would also add the email, phone and authenticator providers, which
               are three capabilities this package does not have: no mail transport, no SMS, no
               second factor. Registering them would make `TokenOptions` advertise providers that
               cannot deliver anything. One provider, for the one operation that exists. */
            .AddTokenProvider<DataProtectorTokenProvider<AlvoIdentityUser>>(
                TokenOptions.DefaultProvider);
}
