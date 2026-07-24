using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using MMLib.Alvo;
using MMLib.Alvo.Internal;
using MMLib.Alvo.Migrations;

namespace Microsoft.Extensions.DependencyInjection;

/// <summary>The single Alvo entry point: registers the core services and returns the builder every provider and feature attaches to.</summary>
public static class AlvoServiceCollectionExtensions
{
    /// <summary>
    /// Adds Alvo to <paramref name="services"/>: <see cref="AlvoOptions"/> (validated at startup)
    /// and the code-first migration orchestrator. Attach a database provider (<c>UseSqlite</c>,
    /// <c>UsePostgreSql</c>) and a descriptor source (<c>FromDescriptor</c>) via <paramref name="configure"/>
    /// or by calling the returned builder's extension methods directly.
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <param name="configure">Optional callback to attach providers and features to the builder.</param>
    /// <returns>The <see cref="IAlvoBuilder"/>, for further chaining outside <paramref name="configure"/>.</returns>
    public static IAlvoBuilder AddAlvo(this IServiceCollection services, Action<IAlvoBuilder>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(services);

        var builder = new AlvoBuilder(services);

        services.AddOptions<AlvoOptions>()
            .ValidateDataAnnotations()
            .ValidateOnStart();
        services.TryAddEnumerable(
            ServiceDescriptor.Singleton<IValidateOptions<AlvoOptions>, AlvoProviderValidation>());

        services.TryAddSingleton<SchemaMigrationRunner>();

        // TODO(#19): register ISchemaRegistry once the Data API needs it — deferred to avoid the
        // chicken/egg of seeding it from an applied model that migration itself produces.

        configure?.Invoke(builder);

        return builder;
    }
}
