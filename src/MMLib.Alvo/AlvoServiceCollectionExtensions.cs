using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MMLib.Alvo;
using MMLib.Alvo.Api;
using MMLib.Alvo.Auth;
using MMLib.Alvo.Descriptor;
using MMLib.Alvo.Events;
using MMLib.Alvo.Expressions;
using MMLib.Alvo.Internal;
using MMLib.Alvo.Migrations;
using MMLib.Alvo.Migrations.Internal;
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
        // a plain console host embedding Alvo would otherwise fail to activate DescriptorBootPlan at all.
        services.AddLogging();

        services.AddOptions<AlvoOptions>()
            .ValidateDataAnnotations()
            .ValidateOnStart();
        services.TryAddEnumerable(
            ServiceDescriptor.Singleton<IValidateOptions<AlvoOptions>, AlvoProviderValidation>());

        AddSchemaOptions(services);

        services.TryAddSingleton<IDescriptorValidator, MMLib.Alvo.Descriptor.Internal.DescriptorValidator>();
        AddBootPlan(services);
        services.TryAddSingleton<SchemaMigrationRunner>();
        services.TryAddSingleton<RuntimeSchemaService>();
        AddBoot(services);
        services.AddAlvoAuth();
        services.AddAlvoExpressions();
        services.AddAlvoRules();
        services.AddAlvoEvents();
        services.AddAlvoApi();

        configure?.Invoke(builder);

        return builder;
    }

    /// <summary>
    /// Registers boot stage 0, with its descriptor source resolved optionally.
    /// </summary>
    /// <remarks>
    /// <b>Through a factory, for the same reason <see cref="AddSchemaOptions"/> uses one.</b> A nullable
    /// constructor parameter is not an optional dependency to the container — only a default value or a factory
    /// is — so taking <c>IDescriptorSource</c> the ordinary way turned a host that forgot <c>FromDescriptor</c>
    /// into <c>Unable to resolve service for type 'IDescriptorSource' while attempting to activate
    /// 'DescriptorBootPlan'</c>, naming two internal types instead of the call to make. The refusal belongs to
    /// <see cref="DescriptorBootPlan.NoDescriptorSourceMessage"/>, where it can be a sentence; and it belongs at
    /// the first load rather than at registration because <c>AddAlvo</c> plus a driver, with the schema applied
    /// by the caller, is a supported composition the data-layer suites depend on.
    /// </remarks>
    /// <param name="services">The service collection.</param>
    private static void AddBootPlan(IServiceCollection services) =>
        services.TryAddSingleton(provider => new DescriptorBootPlan(
            provider.GetService<IDescriptorSource>(),
            provider.GetRequiredService<IDescriptorValidator>(),
            provider.GetRequiredService<ICelCompiler>(),
            provider.GetRequiredService<ILogger<DescriptorBootPlan>>()));

    /// <summary>
    /// Registers the boot: the state a readiness probe reads, the hosted lifecycle service that fills it in
    /// before the server binds, and the health check that reports it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The health check is registered here rather than beside the Data API's services because what it reports on
    /// is the boot, not the API: <c>AddAlvoHealth</c> is the readiness half of what this method publishes, and
    /// <c>MapAlvoHealth</c> is the only thing that makes it reachable.
    /// </para>
    /// <para>
    /// Registered as an <see cref="IHostedService"/> because that is what the host resolves; the
    /// implementation is an <see cref="IHostedLifecycleService"/>, which is how it gets to run before every
    /// <see cref="IHostedService.StartAsync"/> rather than merely before the ones registered after it. See
    /// <see cref="AlvoBootService"/> for why that distinction is a guarantee rather than a preference.
    /// </para>
    /// <para>
    /// <c>TryAddEnumerable</c>, so a host that called <c>AddAlvo</c> twice boots once. The boot needs a
    /// descriptor source and a database provider — the same collaborators <c>ApplyAlvoDescriptorAsync</c>
    /// needs — so a host that registered Alvo without either now fails its <em>start</em> rather than serving
    /// nothing while reporting healthy. A missing provider is refused by <see cref="AlvoProviderValidation"/>;
    /// a missing descriptor source is refused by <c>DescriptorBootPlan</c>, which is where it becomes required
    /// — <c>AddAlvo</c> plus a driver, with the schema applied by the caller, is a supported composition that
    /// the data-layer suites use and must keep working.
    /// </para>
    /// </remarks>
    /// <param name="services">The service collection.</param>
    private static void AddBoot(IServiceCollection services)
    {
        services.TryAddSingleton<AlvoBootState>();
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IHostedService, AlvoBootService>());
        services.AddAlvoHealth();
    }

    /// <summary>
    /// Registers <see cref="AlvoSchemaOptions"/>, bound from its configuration section and validated at
    /// startup: the setting that decides whether a boot may run DDL has to be refused before the boot, not
    /// read for the first time once one is already under way.
    /// </summary>
    /// <remarks>
    /// The section is bound here rather than left to the host so that <c>Alvo__Schema__Startup</c> works in
    /// every distribution with no wiring, exactly as the connection-string convention does
    /// (<c>extensibility.md</c> rule 5). <see cref="AlvoSchemaOptionsConfiguration"/> both binds and validates,
    /// and its <see cref="IConfiguration"/> arrives through a factory so a host that registered none gets the
    /// defaults instead of a DI failure — see that type for why the framework binder cannot do either job here.
    /// </remarks>
    private static void AddSchemaOptions(IServiceCollection services)
    {
        services.AddOptions<AlvoSchemaOptions>()
            .ValidateDataAnnotations()
            .ValidateOnStart();

        services.TryAddEnumerable(
            ServiceDescriptor.Singleton<IConfigureOptions<AlvoSchemaOptions>, AlvoSchemaOptionsConfiguration>(Create));
        services.TryAddEnumerable(
            ServiceDescriptor.Singleton<IValidateOptions<AlvoSchemaOptions>, AlvoSchemaOptionsConfiguration>(Create));

        static AlvoSchemaOptionsConfiguration Create(IServiceProvider provider)
            => new(provider.GetService<IConfiguration>());
    }
}
