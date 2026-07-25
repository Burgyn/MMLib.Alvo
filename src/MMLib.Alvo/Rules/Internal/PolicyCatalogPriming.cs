using MMLib.Alvo.Descriptor;
using MMLib.Alvo.Expressions;
using MMLib.Alvo.Schema;

namespace MMLib.Alvo.Rules.Internal;

/// <summary>
/// The single call shared by every place that accepts a descriptor as authoritative for a project's
/// schema (the code-first startup path, the runtime dashboard-first apply/rollback path): compile a
/// fresh <see cref="PolicyCatalog"/> from it and make it current. Deliberately a plain, eager
/// <see cref="PolicyCatalog.Build"/> call — it throws <see cref="DescriptorValidationException"/> when
/// a rule fails to compile, which rejects the apply rather than silently keeping the previous
/// (possibly wrong) catalog in effect.
/// </summary>
internal static class PolicyCatalogPriming
{
    /// <summary>Compiles a <see cref="PolicyCatalog"/> from <paramref name="descriptor"/>/<paramref name="schema"/> and makes it current.</summary>
    /// <param name="provider">The provider to prime.</param>
    /// <param name="compiler">The CEL compiler every rule and field flag is compiled through.</param>
    /// <param name="descriptor">The just-accepted project descriptor.</param>
    /// <param name="schema">The schema <paramref name="descriptor"/> maps to.</param>
    /// <exception cref="DescriptorValidationException">Any rule, tenant scope, or field flag failed to compile.</exception>
    internal static void Prime(IPolicyCatalogProvider provider, ICelCompiler compiler, AlvoDescriptor descriptor, SchemaModel schema)
    {
        var catalog = PolicyCatalog.Build(descriptor, schema, compiler);
        provider.SetCurrent(catalog);
    }
}
