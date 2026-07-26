using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using MMLib.Alvo.Expressions.Internal;

namespace MMLib.Alvo.Expressions;

/// <summary>Registers the CEL compilation pipeline: the type checker, the profiles, and <see cref="ICelCompiler"/>.</summary>
internal static class ExpressionsSetup
{
    /// <summary>Adds <see cref="ICelCompiler"/>, <see cref="IPredicateRenderer"/>, and <see cref="IPredicateEvaluator"/>.</summary>
    /// <param name="services">The service collection to add the expression services to.</param>
    /// <returns><paramref name="services"/>, for chaining.</returns>
    internal static IServiceCollection AddAlvoExpressions(this IServiceCollection services)
    {
        services.TryAddSingleton<ICelCompiler, CelCompiler>();
        services.TryAddSingleton<IPredicateRenderer, SqlPredicateRenderer>();
        services.TryAddSingleton<IPredicateEvaluator, PredicateEvaluator>();
        return services;
    }
}
