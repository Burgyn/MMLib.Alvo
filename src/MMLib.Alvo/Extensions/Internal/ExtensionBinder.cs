using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

using MMLib.Alvo.Expressions;
using MMLib.Alvo.Rules;

using System.Runtime.CompilerServices;

namespace MMLib.Alvo.Extensions.Internal;

/// <summary>
/// Checks the registry once at boot (<see cref="EnsureRegistrable"/>, every boot shape), binds it to the boot's catalog
/// strictly (<see cref="EnsureBindable"/>), and to every later catalog leniently (<see cref="For"/> binds what fits
/// and logs the rest once per catalog). Each catalog is bound once.
/// </summary>
internal sealed class ExtensionBinder(
    ExtensionRegistry registry, ICelCompiler compiler, IServiceProviderIsService services, ILogger<ExtensionBinder> logger)
{
    private readonly ConditionalWeakTable<PolicyCatalog, CatalogBinding> _bindings = new();

    /// <summary>Refuses every problem no descriptor can cure, at once, and logs the order ties.</summary>
    /// <exception cref="Migrations.AlvoStartupRefusedException">A registration is wrong whatever the descriptor says.</exception>
    internal void EnsureRegistrable()
    {
        var result = ExtensionBindingCompiler.Check(registry, compiler, services);
        foreach (var tie in result.Ties)
        {
            ExtensionLog.ExtensionOrderTie(logger, tie.Target, tie.Order, string.Join(", ", tie.Ids));
        }

        if (result.Problems.Count > 0)
        {
            throw ExtensionRefusal.Of(result.Problems);
        }
    }

    /// <summary>Refuses every registration that does not fit <paramref name="catalog"/>, at once.</summary>
    /// <param name="catalog">The catalog the boot is about to prime.</param>
    /// <exception cref="Migrations.AlvoStartupRefusedException">A registration does not fit the catalog.</exception>
    internal void EnsureBindable(PolicyCatalog catalog)
    {
        var binding = Binding(catalog);
        if (binding.Result.Problems.Count > 0)
        {
            throw ExtensionRefusal.Of(binding.Result.Problems);
        }

        binding.MarkReported();
    }

    /// <summary>The handlers bound to <paramref name="catalog"/>; the first call per catalog logs each unbound one.</summary>
    /// <param name="catalog">The catalog now serving.</param>
    internal ExtensionBindings For(PolicyCatalog catalog)
    {
        var binding = Binding(catalog);
        if (binding.MarkReported())
        {
            foreach (var problems in binding.Result.Problems.GroupBy(problem => problem.Id, StringComparer.Ordinal))
            {
                ExtensionLog.ExtensionUnbound(logger, problems.Key, Described(problems));
            }
        }

        return binding.Result.Bindings;
    }

    private static string Described(IEnumerable<ExtensionProblem> problems) =>
        string.Join("; ", problems.Select(problem => $"{problem.Locator}: {problem.Message}"));

    private CatalogBinding Binding(PolicyCatalog catalog) =>
        _bindings.GetValue(catalog, primed => new(ExtensionBindingCompiler.Compile(registry, primed, compiler, services)));

    /// <summary>One catalog's binding, and whether its unbound handlers were reported.</summary>
    private sealed class CatalogBinding(ExtensionBindingResult result)
    {
        private int _reported;

        internal ExtensionBindingResult Result { get; } = result;

        internal bool MarkReported() => Interlocked.Exchange(ref _reported, 1) == 0;
    }
}
