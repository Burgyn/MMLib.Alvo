using MMLib.Alvo.Descriptor;
using MMLib.Alvo.Expressions;
using MMLib.Alvo.Rules;
using MMLib.Alvo.Schema;

namespace MMLib.Alvo.Extensions.Internal;

/// <summary>A proposed catalog no bound handler would lose, and the handlers that bind neither it nor the current one.</summary>
/// <param name="Catalog">The checked catalog — the one an apply publishes, so it is built and compiled once.</param>
/// <param name="Warnings">One warning per problem of a handler that was not bound before the change either.</param>
internal sealed record ExtensionApplyCheck(PolicyCatalog Catalog, IReadOnlyList<DescriptorValidationError> Warnings);

/// <summary>
/// Refuses a change that would unbind a handler: one that binds the catalog the provider holds and would not bind the
/// proposed one — HX-03's own compiler, through the binder's per-catalog cache, so the guard cannot drift from the boot.
/// It keeps a runtime apply, a rollback, a dry run and <c>ApplyAlvoDescriptorAsync</c> from unbinding a handler; the boot
/// refuses on its own, through <see cref="ExtensionBinder.EnsureBindable"/>. A handler that was already unbound (a
/// dashboard-first host's first descriptor, a stored boot that ended <c>Failed</c>) blocks nothing: it is reported as a
/// warning, and its events keep being recorded dead <c>unbound</c> until a descriptor fits it.
/// </summary>
internal sealed class ExtensionApplyGuard(
    ExtensionRegistry registry, ExtensionBinder binder, ICelCompiler compiler, IPolicyCatalogProvider catalogs)
{
    /// <summary>Builds the catalog <paramref name="descriptor"/> proposes and checks it; <see langword="null"/> when no handler is registered.</summary>
    /// <param name="descriptor">The proposed descriptor.</param>
    /// <param name="schema">The schema it maps to — the very instance the apply publishes.</param>
    /// <exception cref="DescriptorValidationException">A handler that binds the current catalog would no longer bind.</exception>
    internal ExtensionApplyCheck? Check(AlvoDescriptor descriptor, SchemaModel schema) =>
        registry.Handlers.Count == 0 ? null : Check(PolicyCatalog.Build(descriptor, schema, compiler));

    /// <summary>Refuses <paramref name="proposed"/> when a handler that binds the current catalog would no longer bind it.</summary>
    /// <param name="proposed">The catalog the change would prime.</param>
    /// <exception cref="DescriptorValidationException">A handler that binds the current catalog would no longer bind.</exception>
    internal ExtensionApplyCheck Check(PolicyCatalog proposed)
    {
        var problems = binder.Result(proposed).Problems;
        if (problems.Count == 0)
        {
            return new(proposed, []);
        }

        var boundNow = BoundNow();
        var regressions = problems.Where(problem => boundNow.Contains(problem.Id)).ToList();
        if (regressions.Count > 0)
        {
            throw new DescriptorValidationException(new DescriptorValidationResult(
                ExtensionApplyRefusals.Refusals(registry, regressions, catalogs.Current!.Schema, proposed.Schema, compiler)));
        }

        return new(proposed, ExtensionApplyRefusals.Warnings(problems));
    }

    private HashSet<string> BoundNow() => catalogs.Current is { } current
        ? binder.Result(current).Bindings.Handlers.Select(handler => handler.Id).ToHashSet(StringComparer.Ordinal)
        : new HashSet<string>(StringComparer.Ordinal);
}
