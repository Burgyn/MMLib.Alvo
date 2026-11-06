using MMLib.Alvo.Management;

namespace MMLib.Alvo.Functions.Internal;

/// <summary>A host function as the extension catalog lists it: post-commit, bound to no entity, with its input's schema.</summary>
internal static class FunctionDescription
{
    internal static ManagementExtension Of(FunctionRegistration function, bool exposeTypeNames) => new()
    {
        Id = function.Id,
        Kind = AlvoExtensionKind.Function,
        Stage = AlvoExtensionStage.PostCommit,
        TypeName = exposeTypeNames ? function.Function.FullName : null,
        InputSchema = function.Schema.Document,
    };
}
