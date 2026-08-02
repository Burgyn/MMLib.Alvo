using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using MMLib.Alvo;
using MMLib.Alvo.Auth;
using MMLib.Alvo.Descriptor;
using MMLib.Alvo.Expressions;
using MMLib.Alvo.Internal;
using MMLib.Alvo.Migrations;
using MMLib.Alvo.Rules;

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
    /// <remarks>
    /// <see cref="MMLib.Alvo.Schema.ISchemaRegistry"/> is deliberately <b>not</b> registered yet: it
    /// would have to be seeded from the applied model that migration itself produces, and nothing
    /// consumes it until the Data API does. Resolve it and you get "no service registered", not an
    /// empty registry — a host that needs one today must register its own.
    /// </remarks>
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

        services.TryAddSingleton<IDescriptorValidator, MMLib.Alvo.Descriptor.Internal.DescriptorValidator>();
        services.TryAddSingleton<SchemaMigrationRunner>();
        services.TryAddSingleton<RuntimeSchemaService>();
        services.AddAlvoAuth();
        services.AddAlvoExpressions();
        services.AddAlvoRules();

        // TODO(#19): register ISchemaRegistry once the Data API needs it (see this method's remarks).

        configure?.Invoke(builder);

        return builder;
    }
}
