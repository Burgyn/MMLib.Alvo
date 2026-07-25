using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using MMLib.Alvo.Descriptor;
using MMLib.Alvo.Expressions;
using MMLib.Alvo.Migrations;
using MMLib.Alvo.Rules.Internal;

namespace MMLib.Alvo.Rules;

/// <summary>Registers <see cref="IPolicyEngine"/>: the default-deny authorization checkpoint every data port consults.</summary>
internal static class RulesSetup
{
    /// <summary>
    /// Adds <see cref="IPolicyEngine"/> as a singleton whose <see cref="PolicyCatalog"/> is built
    /// lazily, on the first <see cref="IPolicyEngine.Resolve"/> call, from the registered
    /// <see cref="IDescriptorSource"/> — never at registration time. A descriptor is not available
    /// when <c>AddAlvo</c> runs (the <c>FromDescriptor</c> chicken/egg <c>AlvoServiceCollectionExtensions</c>
    /// already names), so registering an eagerly empty catalog here would deny every operation with a
    /// confusing "no rules" message; deferring the build instead means resolving <see cref="IPolicyEngine"/>
    /// itself never fails, only an <see cref="IPolicyEngine.Resolve"/> call made before a descriptor
    /// source is configured does, with a message that says exactly that.
    /// </summary>
    /// <param name="services">The service collection to add the rules services to.</param>
    /// <returns><paramref name="services"/>, for chaining.</returns>
    internal static IServiceCollection AddAlvoRules(this IServiceCollection services)
    {
        services.TryAddSingleton<IPolicyEngine>(provider => new PolicyEngine(() => BuildCatalog(provider)));
        return services;
    }

    private static PolicyCatalog BuildCatalog(IServiceProvider services)
    {
        var source = services.GetService<IDescriptorSource>() ?? throw new InvalidOperationException(
            "IPolicyEngine has no descriptor to compile rules from: no IDescriptorSource is configured. " +
            "Call FromDescriptor(...) on the Alvo builder before resolving a policy for any entity.");
        var compiler = services.GetRequiredService<ICelCompiler>();

        var descriptorJson = source.LoadAsync().GetAwaiter().GetResult();
        var descriptor = AlvoDescriptor.Parse(descriptorJson);
        var schema = DescriptorToSchemaMapper.Map(descriptor);
        return PolicyCatalog.Build(descriptor, schema, compiler);
    }
}
