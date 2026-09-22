using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using MMLib.Alvo.Management.Internal;

namespace MMLib.Alvo.Management;

/// <summary>Registers the Management API's options and its authorization gate.</summary>
/// <remarks>
/// Called from <c>AddAlvo</c> and nowhere else, exactly as <c>AddAlvoApi</c> is: registering a service
/// exposes nothing, and the endpoint seam is <c>MapAlvoManagementApi()</c>
/// (<c>docs/architecture/extensibility.md</c> rule 10).
/// </remarks>
internal static class ManagementSetup
{
    /// <summary>
    /// Adds <see cref="AlvoManagementOptions"/>, the operation surface, and
    /// <see cref="ManagementAccessEvaluator"/>.
    /// </summary>
    /// <param name="services">The service collection to add the management services to.</param>
    /// <returns><paramref name="services"/>, for chaining.</returns>
    internal static IServiceCollection AddAlvoManagement(this IServiceCollection services)
    {
        AddManagementOptions(services);
        AddManagementService(services);
        services.TryAddSingleton<ManagementAccessEvaluator>();
        AddUserAdministration(services);
        return services;
    }

    /// <summary>
    /// Registers the guarded decorator over whatever implements user administration.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Only when an implementation is registered.</b> The package that holds the membership
    /// store registers itself under <see cref="AlvoUserAdministration.UnguardedKey"/>; a
    /// deployment without that package has no keyed service, and this registration's factory
    /// answers <see langword="null"/> — so the routes are simply absent rather than present and
    /// broken.
    /// </para>
    /// <para>
    /// <b>The public interface resolves to the decorator and to nothing else.</b> That is what
    /// makes the guards non-optional: there is no registration anywhere that hands an in-process
    /// caller the raw implementation, which a guard living inside a swappable adapter could never
    /// claim.
    /// </para>
    /// </remarks>
    /// <param name="services">The service collection to register into.</param>
    private static void AddUserAdministration(IServiceCollection services) =>
        services.TryAddScoped<IAlvoUserAdministration>(provider =>
            provider.GetKeyedService<IAlvoUserAdministration>(AlvoUserAdministration.UnguardedKey)
                is { } implementation
                ? new GuardedUserAdministration(
                    implementation,
                    provider.GetRequiredService<Auth.IAlvoContextAccessor>(),
                    provider.GetRequiredService<ManagementAccessEvaluator>(),
                    provider.GetRequiredService<IRoleCatalogProvider>(),
                    provider.GetRequiredService<IAlvoBootstrapAdmin>())
                : throw new InvalidOperationException(
                    "No IAlvoUserAdministration implementation is registered. A package that "
                    + "administers membership registers itself under "
                    + $"'{AlvoUserAdministration.UnguardedKey}'; MMLib.Alvo.Identity does."));

    /// <summary>
    /// Registers the one <see cref="IAlvoManagement"/> implementation, with its data port and its descriptor
    /// history resolved <b>optionally</b>.
    /// </summary>
    /// <remarks>
    /// Through a factory, for <see cref="AddManagementOptions"/>'s reason: a nullable constructor parameter
    /// is not an optional dependency to the container, and <c>AddAlvo</c> without a driver is a supported
    /// composition — so taking <c>IAlvoData</c> the ordinary way would make <c>info</c> unresolvable in it.
    /// <c>IDescriptorVersionStore</c> is registered by a database provider and by nothing else, so it is
    /// resolved the same way for the same reason.
    /// <para>
    /// <c>RuntimeSchemaService</c> is registered unconditionally but <em>activates</em> only where a
    /// database provider registered its writer and its store, so <c>GetService</c> would throw rather than
    /// answer <see langword="null"/>. It is passed as the resolver itself — a <c>Func</c> the service calls
    /// on the first apply — which defers that activation to a call a driver-less container cannot reach.
    /// </para>
    /// </remarks>
    /// <param name="services">The service collection to add the service to.</param>
    private static void AddManagementService(IServiceCollection services) =>
        services.TryAddSingleton<IAlvoManagement>(provider => new AlvoManagementService(
            provider.GetRequiredService<IOptions<AlvoOptions>>(),
            provider.GetRequiredService<IOptions<AlvoManagementOptions>>(),
            provider.GetRequiredService<IOptions<Migrations.AlvoSchemaOptions>>(),
            provider.GetRequiredService<Migrations.AlvoBootState>(),
            provider.GetRequiredService<Schema.ISchemaRegistry>(),
            provider.GetRequiredService<Rules.IPolicyEngine>(),
            provider.GetRequiredService<IRoleCatalogProvider>(),
            provider.GetService<Data.IAlvoData>(),
            provider.GetService<Migrations.IDescriptorVersionStore>(),
            provider.GetService<IManagementIdempotencyStore>(),
            provider.GetRequiredService<Auth.IAlvoContextAccessor>(),
            provider.GetRequiredService<ManagementAccessEvaluator>(),
            provider.GetRequiredService<Microsoft.Extensions.Logging.ILogger<AlvoManagementService>>(),
            provider.GetRequiredService<Ai.IAiConnectionResolver>(),
            provider.GetRequiredService<Secrets.ISecretStore>(),
            provider.GetRequiredService<Migrations.RuntimeSchemaService>));

    /// <summary>
    /// Registers <see cref="AlvoManagementOptions"/>, bound from its configuration section and validated at
    /// startup.
    /// </summary>
    /// <remarks>
    /// The binder's <see cref="IConfiguration"/> arrives through a factory rather than as a constructor
    /// dependency, on <c>AddSchemaOptions</c>'s precedent: a nullable constructor parameter is not an
    /// optional dependency to the container, so a host that registered no configuration would fail to
    /// activate the binder at all.
    /// </remarks>
    /// <param name="services">The service collection to add the options to.</param>
    private static void AddManagementOptions(IServiceCollection services)
    {
        services.AddOptions<AlvoManagementOptions>().ValidateOnStart();

        services.TryAddEnumerable(
            ServiceDescriptor.Singleton<IConfigureOptions<AlvoManagementOptions>, AlvoManagementOptionsConfiguration>(
                Create));
        services.TryAddEnumerable(
            ServiceDescriptor.Singleton<IValidateOptions<AlvoManagementOptions>, AlvoManagementOptionsConfiguration>(
                Create));

        static AlvoManagementOptionsConfiguration Create(IServiceProvider provider) =>
            new(provider.GetService<IConfiguration>(), provider.GetRequiredService<IOptions<Api.AlvoApiOptions>>());
    }
}
