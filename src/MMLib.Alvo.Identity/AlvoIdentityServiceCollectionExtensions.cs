using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection.Extensions;
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

        services.AddDbContext<AlvoIdentityDbContext>(configureStore);
        AddIdentityCore(services);

        var options = services.AddOptions<AlvoIdentityOptions>();
        if (configure is not null)
        {
            options.Configure(configure);
        }

        services.TryAddScoped<IAlvoUserStore, AlvoIdentityUserStore>();
        services.TryAddSingleton<AlvoBootstrapAdmin>();
        services.Replace(ServiceDescriptor.Singleton<IAlvoBootstrapAdmin>(
            provider => provider.GetRequiredService<AlvoBootstrapAdmin>()));
        services.AddKeyedScoped<IAlvoContextResolver, AlvoIdentityContextResolver>(AlvoIdentity.ResolverKey);
        services.AddHostedService<AlvoIdentityBootstrap>();

        return services;
    }

    /// <summary>Adds ASP.NET Core Identity's user and role managers over the Alvo identity store.</summary>
    /// <param name="services">The service collection to register into.</param>
    private static void AddIdentityCore(IServiceCollection services) =>
        services.AddIdentityCore<AlvoIdentityUser>()
            .AddRoles<AlvoIdentityRole>()
            .AddEntityFrameworkStores<AlvoIdentityDbContext>();
}
