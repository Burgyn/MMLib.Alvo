using Microsoft.Extensions.DependencyInjection;

using MMLib.Alvo.Data.Internal;
using MMLib.Alvo.Extensions.Internal;

namespace MMLib.Alvo.Reads.Internal;

/// <summary>What a read extension does to a read.</summary>
internal enum ReadExtensionKind
{
    Narrowing,
    Enricher,
}

/// <summary>A host read extension: one id, bound to the named entities — never <c>"*"</c>, which the boot refuses.</summary>
internal sealed record ReadExtensionRegistration(
    string Id, int Sequence, IReadOnlyList<string> Entities, ReadExtensionKind Kind, ReadInvoker Invoker, RecordShape? Typed)
    : ExtensionRegistration(Id, Sequence)
{
    internal bool Covers(string entity) => Entities.Contains(entity, StringComparer.Ordinal);

    internal static void Add(
        IServiceCollection services, string id, IReadOnlyList<string> entities, ReadExtensionKind kind, ReadInvoker invoker, RecordShape? typed)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        ExtensionRegistrations.Add(services, sequence => new ReadExtensionRegistration(id, sequence, entities, kind, invoker, typed));
    }
}

/// <summary>A read extension bound to one catalog.</summary>
internal sealed record BoundReadExtension(ReadExtensionRegistration Registration)
{
    internal string Id => Registration.Id;
}
