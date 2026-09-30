using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;

namespace MMLib.Alvo.Ai.Tests;

/// <summary>
/// Every member an assembly's IL references on a type it does not define, as <c>Namespace.Type::Member</c> (nested
/// types joined by <c>/</c>) — read from the metadata, so what is asserted is what was compiled, not what the source
/// looks like.
/// </summary>
/// <remarks>
/// D45 grants <c>MMLib.Alvo.Ai</c> and <c>MMLib.Alvo.Admin</c> every internal of <c>MMLib.Alvo.Abstractions</c>, and
/// among them are the two constructors the security core rests on (<c>PolicyDecision</c>'s and
/// <c>CompiledExpression</c>'s). The grant is for the trace seam only, and this is how the suites hold it there.
/// </remarks>
internal static class InternalMemberReferences
{
    /// <summary>
    /// The members only the core may reach: an allow decision, a compiled, trusted expression, and an application role
    /// minted past <c>RoleCatalog</c>.
    /// </summary>
    internal static IReadOnlyList<string> SecurityCoreOnly { get; } =
    [
        "MMLib.Alvo.Rules.PolicyDecision::.ctor",
        "MMLib.Alvo.Rules.PolicyDecision::Allow",
        "MMLib.Alvo.Expressions.CompiledExpression::.ctor",
        "MMLib.Alvo.Identity.Role::Application",
    ];

    internal static IReadOnlySet<string> Of(Assembly assembly)
    {
        using var stream = File.OpenRead(assembly.Location);
        using var reader = new PEReader(stream);
        var metadata = reader.GetMetadataReader();
        var members = new HashSet<string>(StringComparer.Ordinal);
        foreach (var handle in metadata.MemberReferences)
        {
            var member = metadata.GetMemberReference(handle);
            if (member.Parent.Kind == HandleKind.TypeReference)
            {
                members.Add($"{TypeName(metadata, (TypeReferenceHandle)member.Parent)}::{metadata.GetString(member.Name)}");
            }
        }

        return members;
    }

    private static string TypeName(MetadataReader metadata, TypeReferenceHandle handle)
    {
        var type = metadata.GetTypeReference(handle);
        var name = metadata.GetString(type.Name);
        return type.ResolutionScope.Kind == HandleKind.TypeReference
            ? $"{TypeName(metadata, (TypeReferenceHandle)type.ResolutionScope)}/{name}"
            : $"{metadata.GetString(type.Namespace)}.{name}";
    }
}
