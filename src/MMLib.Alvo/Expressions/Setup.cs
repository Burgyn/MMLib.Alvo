using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using MMLib.Alvo.Expressions.Internal;

namespace MMLib.Alvo.Expressions;

/// <summary>Registers the CEL compilation pipeline: the function catalog (built-ins plus host registrations), the type checker, the profiles, and <see cref="ICelCompiler"/>.</summary>
internal static class ExpressionsSetup
{
    /// <summary>Adds <see cref="ICelCompiler"/>, <see cref="IPredicateRenderer"/>, and <see cref="IPredicateEvaluator"/>.</summary>
    /// <param name="services">The service collection to add the expression services to.</param>
    /// <returns><paramref name="services"/>, for chaining.</returns>
    internal static IServiceCollection AddAlvoExpressions(this IServiceCollection services)
    {
        services.TryAddSingleton(provider => CelFunctionCatalog.BuiltIns.With(
            provider.GetServices<CelFunctionRegistration>().Select(registration => registration.Function)));
        services.TryAddSingleton<ICelCompiler>(provider => new CelCompiler(provider.GetRequiredService<CelFunctionCatalog>()));
        services.TryAddSingleton<IPredicateRenderer, SqlPredicateRenderer>();
        services.TryAddSingleton<IPredicateEvaluator, PredicateEvaluator>();
        return services;
    }
}
