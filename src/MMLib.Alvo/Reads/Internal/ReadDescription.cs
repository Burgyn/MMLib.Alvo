using MMLib.Alvo.Management;

namespace MMLib.Alvo.Reads.Internal;

/// <summary>
/// One read extension as the catalog describes it: no condition, since a narrowing's filter is per caller; a narrowing runs on
/// list and get, an enricher on those and on a write's echo.
/// </summary>
internal static class ReadDescription
{
    private static readonly string[] _narrowed = ["list", "get"];

    private static readonly string[] _enriched = ["list", "get", "create", "update"];

    internal static ManagementExtension Of(ReadExtensionRegistration read, bool exposeTypeNames) => new()
    {
        Id = read.Id,
        Kind = read.Kind == ReadExtensionKind.Narrowing ? AlvoExtensionKind.Narrowing : AlvoExtensionKind.Enricher,
        Stage = AlvoExtensionStage.Read,
        Entities = read.Entities,
        Operations = read.Kind == ReadExtensionKind.Narrowing ? _narrowed : _enriched,
        TypeName = exposeTypeNames ? read.Invoker.TypeName : null,
    };
}
