using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

using MMLib.Alvo.Expressions;
using MMLib.Alvo.Rules;

using System.Runtime.CompilerServices;

namespace MMLib.Alvo.Extensions.Internal;

/// <summary>
/// Binds the registry to each primed catalog once: strictly at boot (<see cref="EnsureBindable"/> refuses every problem
/// at once), leniently afterwards (<see cref="For"/> binds what fits and logs the rest once per catalog).
/// </summary>
internal sealed class ExtensionBinder(
    ExtensionRegistry registry, ICelCompiler compiler, IServiceProviderIsService services, ILogger<ExtensionBinder> logger)
{
    private static readonly object _reportedMark = new();

    private readonly ConditionalWeakTable<PolicyCatalog, ExtensionBindingResult> _results = new();
    private readonly ConditionalWeakTable<PolicyCatalog, object> _reported = new();
    private readonly Lock _gate = new();

    internal void EnsureBindable(PolicyCatalog catalog)
    {
        var result = Result(catalog);
        foreach (var tie in result.Ties)
        {
            ExtensionLog.ExtensionOrderTie(logger, tie.Target, tie.Order, string.Join(", ", tie.Ids));
        }

        if (result.Problems.Count > 0)
        {
            throw ExtensionRefusal.Of(result.Problems);
        }

        MarkReported(catalog);
    }

    internal ExtensionBindings For(PolicyCatalog catalog)
    {
        var result = Result(catalog);
        if (MarkReported(catalog))
        {
            foreach (var problems in result.Problems.GroupBy(problem => problem.Id, StringComparer.Ordinal))
            {
                ExtensionLog.ExtensionUnbound(logger, problems.Key, Described(problems));
            }
        }

        return result.Bindings;
    }

    private static string Described(IEnumerable<ExtensionProblem> problems) =>
        string.Join("; ", problems.Select(problem => $"{problem.Locator}: {problem.Message}"));

    private ExtensionBindingResult Result(PolicyCatalog catalog)
    {
        lock (_gate)
        {
            if (!_results.TryGetValue(catalog, out var result))
            {
                result = ExtensionBindingCompiler.Compile(registry, catalog, compiler, services);
                _results.Add(catalog, result);
            }

            return result;
        }
    }

    private bool MarkReported(PolicyCatalog catalog)
    {
        lock (_gate)
        {
            if (_reported.TryGetValue(catalog, out _))
            {
                return false;
            }

            _reported.Add(catalog, _reportedMark);

            return true;
        }
    }
}
