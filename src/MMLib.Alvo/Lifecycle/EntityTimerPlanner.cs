using MMLib.Alvo.Data;
using MMLib.Alvo.Events;
using MMLib.Alvo.Expressions.Internal;
using MMLib.Alvo.Scheduling;
using MMLib.Alvo.Schema;

using System.Globalization;

namespace MMLib.Alvo.Lifecycle;

/// <summary>
/// The timers one write arms, moves and cancels, from its two images: <c>due(row) = when(row) ? at(row) : null</c>.
/// </summary>
/// <remarks>
/// <para>
/// A create arms a non-null due; an update upserts only when <c>due(new) ≠ due(old)</c>, so a write that leaves the due alone
/// never resets a timer's generation; a null <c>due(new)</c> and every delete cancel by key, unconditionally — one primary-key
/// delete, which is cheaper than knowing whether a row is there.
/// </para>
/// <para>
/// <b>Fails closed.</b> A <c>when</c> or an <c>at</c> that throws propagates, and the write that planned it rolls back.
/// </para>
/// </remarks>
internal static class EntityTimerPlanner
{
    /// <summary>The changes <paramref name="write"/> makes to <paramref name="timers"/>, in declaration order.</summary>
    /// <param name="timers">The entity's compiled timers.</param>
    /// <param name="write">The write.</param>
    /// <param name="depth">The chain-depth limit an armed timer's context must be within.</param>
    internal static IReadOnlyList<TimerChange> Plan(IReadOnlyList<CompiledTimer> timers, EntityTimerWrite write, AlvoChainDepthLimit depth)
    {
        ArgumentNullException.ThrowIfNull(timers);
        ArgumentNullException.ThrowIfNull(write);
        ArgumentNullException.ThrowIfNull(depth);
        var row = write.Stored ?? write.Previous ?? throw new ArgumentException("A timer write has a row.", nameof(write));
        var target = new TimerTarget(write, ScopeOf(row), IdOf(row), depth);

        return [.. timers.Select(timer => Change(timer, target)).OfType<TimerChange>()];
    }

    private static TimerChange? Change(CompiledTimer timer, TimerTarget target)
    {
        var write = target.Write;
        var key = $"{TimerEntries.ReservedKeyPrefix}{write.Entity}:{timer.Name}:{target.Id:D}";
        var next = write.Stored is { } stored ? DueAt(timer, stored, write) : null;
        if (next is not { } due)
        {
            return TimerChange.Cancel(target.Scope, key);
        }

        var before = write.Previous is { } previous ? DueAt(timer, previous, write) : null;
        return before == due ? null : new TimerChange(target.Scope, key, Armed(timer, target, key, due));
    }

    private static DateTimeOffset? DueAt(CompiledTimer timer, AlvoRecord row, EntityTimerWrite write) =>
        timer.When is { } when && !CelInterpreter.EvaluatePredicate(when, row, null, write.Caller)
            ? null
            : CelInterpreter.EvaluateInstant(timer.At, row, write.Caller, write.Now);

    private static TimerEntry Armed(CompiledTimer timer, TimerTarget target, string key, DateTimeOffset due)
    {
        var context = ArmedAs(target);
        target.Depth.EnsureWithin(context);

        return TimerEntries.Of(
            target.Scope, key, due, timer.EventType, Data(target.Write.Entity, timer.Name, target.Id, due), context, target.Write.Now);
    }

    /// <summary>
    /// The system context the timer is armed as, in the row's tenant, resuming the writer's flow: the write's own event is its
    /// correlation and its cause, so the fired event chains to the write that armed it.
    /// </summary>
    private static AlvoContext ArmedAs(TimerTarget target)
    {
        var write = target.Write;
        var provenance = AlvoEventProvenance.Effective(write.Caller) with
        {
            CorrelationId = write.CorrelationId ?? write.Caller.Provenance.CorrelationId,
            CausationId = write.CausationId ?? write.Caller.Provenance.CausationId,
        };

        return AlvoContext.System(target.Scope.Length == 0 ? null : TenantId.Parse(target.Scope, null)) with { Provenance = provenance };
    }

    private static Dictionary<string, object?> Data(string entity, string timer, Guid id, DateTimeOffset due) => new(StringComparer.Ordinal)
    {
        ["entity"] = entity,
        ["id"] = id.ToString("D"),
        ["timer"] = timer,
        ["at"] = due.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture),
    };

    private static string ScopeOf(AlvoRecord row) => row.TryGetValue(AlvoManagedColumns.TenantId, out var tenant) && tenant is not null
        ? new TenantId(GuidOf(tenant)).ToString()
        : string.Empty;

    private static Guid IdOf(AlvoRecord row) =>
        row.TryGetValue(AlvoManagedColumns.Id, out var id) && id is not null
            ? GuidOf(id)
            : throw new ArgumentException("A timer write's row carries its id.", nameof(row));

    private static Guid GuidOf(object value) => value as Guid? ?? Guid.Parse(Convert.ToString(value, CultureInfo.InvariantCulture)!, CultureInfo.InvariantCulture);

    private sealed record TimerTarget(EntityTimerWrite Write, string Scope, Guid Id, AlvoChainDepthLimit Depth);
}
