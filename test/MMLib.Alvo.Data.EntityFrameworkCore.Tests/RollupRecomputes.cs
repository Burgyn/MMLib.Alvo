using Microsoft.Extensions.DependencyInjection;
using MMLib.Alvo.Expressions;

namespace MMLib.Alvo.Data.EntityFrameworkCore.Tests;

/// <summary>
/// A <see cref="RollupRecompute"/> over a given dialect and field renderer, with the real compiler and renderer
/// <c>AddAlvo()</c> registers — the pair a <c>rollup.where</c> is rendered with.
/// </summary>
internal static class RollupRecomputes
{
    private static readonly ServiceProvider _services = new ServiceCollection().AddAlvo().Services.BuildServiceProvider();

    /// <summary>The recompute over <paramref name="dialect"/> and <paramref name="fields"/>.</summary>
    /// <param name="dialect">The statement seam.</param>
    /// <param name="fields">The expression seam.</param>
    internal static RollupRecompute For(IAlvoSqlDialect dialect, IFieldSqlRenderer fields) => new(
        dialect,
        fields,
        _services.GetRequiredService<ICelCompiler>(),
        _services.GetRequiredService<IPredicateRenderer>());
}
