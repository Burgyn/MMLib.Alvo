namespace MMLib.Alvo.Hooks.Internal;

/// <summary>When, in a write, a host hook runs.</summary>
internal enum WriteHookStage
{
    Validate,
    BeforeWrite,
    AfterWrite,
    BeforeDelete,
    AfterDelete,
}
