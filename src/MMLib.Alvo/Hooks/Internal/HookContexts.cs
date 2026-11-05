namespace MMLib.Alvo.Hooks.Internal;

/// <summary>Builds one run's context, typed or untyped; each factory is captured at registration, where <typeparamref name="T"/> is known.</summary>
internal static class HookContexts<T>
    where T : class
{
    internal static ValidateWrite<T> Validate(HookRun run) => new(run);

    internal static BeforeWrite<T> Before(HookRun run) => new(run);

    internal static AfterWrite<T> After(HookRun run) => new(run);

    internal static BeforeDelete<T> BeforeDelete(HookRun run) => new(run);

    internal static AfterDelete<T> AfterDelete(HookRun run) => new(run);
}
