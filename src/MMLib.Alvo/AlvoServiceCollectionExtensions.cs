using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using MMLib.Alvo;
using MMLib.Alvo.Api;
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
    /// <see cref="MMLib.Alvo.Schema.ISchemaRegistry"/> arrives with the policy catalog provider, which
    /// implements it: the applied schema a data port validates a caller's field names against is then
    /// always the one the rules judging the same request were compiled against. It reads an empty model
    /// until a descriptor is applied — no entity declared, so every entity and field name is refused —
    /// and a host with its own schema source registers its own and takes it over.
    /// </remarks>
    /// <param name="services">The service collection.</param>
    /// <param name="configure">Optional callback to attach providers and features to the builder.</param>
    /// <returns>The <see cref="IAlvoBuilder"/>, for further chaining outside <paramref name="configure"/>.</returns>
    public static IAlvoBuilder AddAlvo(this IServiceCollection services, Action<IAlvoBuilder>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(services);

        var builder = new AlvoBuilder(services);

        // Alvo writes at least one warning of its own (the declared-but-unhonoured descriptor blocks), so it
        // resolves ILogger<T> — and it must not require the host to have arranged that. AddLogging is
        // idempotent (TryAdd throughout), so a host that already called it, or any ASP.NET host, is unaffected;
        // a plain console host embedding Alvo would otherwise fail to activate SchemaMigrationRunner at all.
        services.AddLogging();

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
        services.AddAlvoApi();

        configure?.Invoke(builder);

        return builder;
    }
}
