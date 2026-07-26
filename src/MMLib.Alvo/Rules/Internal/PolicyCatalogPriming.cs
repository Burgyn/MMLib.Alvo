using MMLib.Alvo.Descriptor;
using MMLib.Alvo.Expressions;
using MMLib.Alvo.Schema;

namespace MMLib.Alvo.Rules.Internal;

/// <summary>
/// Builds a fresh <see cref="PolicyCatalog"/> from a descriptor and makes it current, for the kind of
/// call site where nothing is durably changed by the call itself: a true no-op run, where the schema
/// in the database and the descriptor already agree — the code-first runner's empty-plan branch, and
/// the runtime service's idempotent re-apply of the descriptor already stored. Because nothing is
/// written either before or after this call, a rule that fails to compile simply rejects the run —
/// <see cref="DescriptorValidationException"/> propagates before
/// <see cref="IPolicyCatalogProvider.SetCurrent"/> ever runs — and the previously primed catalog is
/// untouched.
/// </summary>
/// <remarks>
/// Every call site that accepts a descriptor as authoritative for a change that <em>does</em> write
/// something durable (a genuine runtime apply or idempotent rules-only re-apply, a rollback, a
/// code-first migration that actually runs DDL) does <em>not</em> use this helper: it calls
/// <see cref="PolicyCatalog.Build"/> directly, before the durable write, so an uncompilable rule set
/// rejects the change before anything is committed; only once that write succeeds does it call
/// <see cref="IPolicyCatalogProvider.SetCurrent"/>. Collapsing build-then-write-then-publish into one
/// call — as this helper does — would be wrong there, because it would either publish before the
/// write is known to have succeeded, or (as this type used to do) publish only after the write had
/// already made the schema durable, leaving a rejected apply's still-committed schema paired with a
/// stale catalog.
/// </remarks>
internal static class PolicyCatalogPriming
{
    /// <summary>Compiles a <see cref="PolicyCatalog"/> from <paramref name="descriptor"/>/<paramref name="schema"/> and makes it current.</summary>
    /// <param name="provider">The provider to prime.</param>
    /// <param name="compiler">The CEL compiler every rule and field flag is compiled through.</param>
    /// <param name="project">
    /// The project identity the catalog is published under — see the type remarks; every call site
    /// passes the same string it keys its own durable store by, so the provider's one-project slot and
    /// that store cannot end up naming the project differently.
    /// </param>
    /// <param name="descriptor">The just-accepted project descriptor.</param>
    /// <param name="schema">The schema <paramref name="descriptor"/> maps to.</param>
    /// <exception cref="DescriptorValidationException">Any rule, tenant scope, or field flag failed to compile.</exception>
    internal static void Prime(
        IPolicyCatalogProvider provider, ICelCompiler compiler, string project, AlvoDescriptor descriptor, SchemaModel schema)
    {
        var catalog = PolicyCatalog.Build(descriptor, schema, compiler);
        provider.SetCurrent(project, catalog);
    }
}
