using Microsoft.Extensions.DependencyInjection;
using MMLib.Alvo.Data;
using MMLib.Alvo.Data.Internal;
using MMLib.Alvo.Extensions;
using MMLib.Alvo.Extensions.Internal;

namespace MMLib.Alvo.Hooks.Internal;

/// <summary>A hook's options, frozen at registration, with the stage's default timeout filled in.</summary>
internal sealed record HookSettings(string? Description, int Order, bool OrderStated, TimeSpan Timeout, int MaxOperations)
{
    internal static HookSettings From(AlvoHookOptions options, WriteHookStage stage) => new(
        options.Description,
        options.Order,
        options.OrderStated,
        options.Timeout ?? (stage == WriteHookStage.Validate ? AlvoHookOptions.DefaultValidateTimeout : AlvoHookOptions.DefaultTimeout),
        options.MaxOperations);
}

/// <summary>A host write hook: one id, one stage, bound to the named entities or to every entity (<c>"*"</c>).</summary>
internal sealed record WriteHookRegistration(
    string Id, int Sequence, IReadOnlyList<string> Entities, WriteHookStage Stage, string? When, HookSettings Settings, HookInvoker Invoker, RecordShape? Typed)
    : ExtensionRegistration(Id, Sequence)
{
    internal bool IsEveryEntity => Entities is [EntityEvents.AllEntities];

    internal bool InTransaction => Stage != WriteHookStage.Validate;

    internal bool Covers(string entity) => IsEveryEntity || Entities.Contains(entity, StringComparer.Ordinal);

    internal bool RunsOn(AlvoWriteOperation operation) => Stage is WriteHookStage.BeforeDelete or WriteHookStage.AfterDelete
        ? operation == AlvoWriteOperation.Delete
        : operation != AlvoWriteOperation.Delete;

    internal static void Add(
        IServiceCollection services, string id, IReadOnlyList<string> entities, WriteHookStage stage, string? when,
        Action<AlvoHookOptions>? configure, HookInvoker invoker, RecordShape? typed = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        if (when is not null)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(when);
        }

        var options = new AlvoHookOptions();
        configure?.Invoke(options);
        ExtensionRegistrations.Add(services, sequence => new WriteHookRegistration(id, sequence, entities, stage, when, HookSettings.From(options, stage), invoker, typed));
    }
}
