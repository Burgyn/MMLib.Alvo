using MMLib.Alvo.Management;

namespace MMLib.Alvo.Reads.Internal;

/// <summary>One read extension as the catalog describes it: no condition, since a narrowing's filter is per caller.</summary>
internal static class ReadDescription
{
    private static readonly string[] _narrowed = ["list", "get"];

    internal static ManagementExtension Of(ReadExtensionRegistration read, bool exposeTypeNames) => new()
    {
        Id = read.Id,
        Kind = AlvoExtensionKind.Narrowing,
        Stage = AlvoExtensionStage.Read,
        Entities = read.Entities,
        Operations = _narrowed,
        TypeName = exposeTypeNames ? read.Invoker.TypeName : null,
    };
}
