using MMLib.Alvo.Management;

namespace MMLib.Alvo.Hooks.Internal;

/// <summary>One write hook as the catalog describes it (Ruling 6).</summary>
internal static class HookDescription
{
    private static readonly string[] _writes = ["create", "update"];
    private static readonly string[] _deletes = ["delete"];

    internal static ManagementExtension Of(WriteHookRegistration hook, bool exposeTypeNames) => new()
    {
        Id = hook.Id,
        Kind = KindOf(hook.Stage),
        Stage = hook.InTransaction ? AlvoExtensionStage.InTransaction : AlvoExtensionStage.PreTransaction,
        Entities = hook.Entities,
        Operations = hook.Stage is WriteHookStage.BeforeDelete or WriteHookStage.AfterDelete ? _deletes : _writes,
        When = hook.When,
        Order = hook.Settings.Order,
        Timeout = hook.Settings.Timeout,
        Description = hook.Settings.Description,
        TypeName = exposeTypeNames ? hook.Invoker.TypeName : null,
        TopLevelOnly = !hook.InTransaction,
    };

    private static AlvoExtensionKind KindOf(WriteHookStage stage) => stage switch
    {
        WriteHookStage.Validate => AlvoExtensionKind.Validate,
        WriteHookStage.BeforeWrite => AlvoExtensionKind.BeforeWrite,
        WriteHookStage.AfterWrite => AlvoExtensionKind.AfterWrite,
        WriteHookStage.BeforeDelete => AlvoExtensionKind.BeforeDelete,
        _ => AlvoExtensionKind.AfterDelete,
    };
}
