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
/// <b>A re-plan</b> (<see cref="Replan"/>) has one image and the timers already armed in place of the second: it arms an
/// unarmed due that is still ahead, moves an armed one to a different due, cancels an armed one whose due is now null, and
/// leaves everything else alone — so running it again changes nothing. An unarmed due that has already passed is left alone
/// too: the re-plan cannot tell a timer that never was armed from one that fired, and firing it again is the worse mistake.
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

    /// <summary>The changes a system re-plan makes to <paramref name="timers"/> for one stored row, in declaration order.</summary>
    /// <param name="timers">The entity's compiled timers.</param>
    /// <param name="replan">The row, the timers already armed for it, and the system context the re-plan runs as.</param>
    /// <param name="depth">The chain-depth limit an armed timer's context must be within.</param>
    internal static IReadOnlyList<TimerChange> Replan(IReadOnlyList<CompiledTimer> timers, EntityTimerReplan replan, AlvoChainDepthLimit depth)
    {
        ArgumentNullException.ThrowIfNull(timers);
        ArgumentNullException.ThrowIfNull(replan);
        ArgumentNullException.ThrowIfNull(depth);
        var row = new ReplanRow(replan, ScopeOf(replan.Row), IdOf(replan.Row), depth);

        return [.. timers.Select(timer => Replanned(timer, row)).OfType<TimerChange>()];
    }

    /// <summary>The key of each of <paramref name="timers"/> for the row <paramref name="id"/> of <paramref name="entity"/>.</summary>
    /// <param name="timers">The entity's compiled timers.</param>
    /// <param name="entity">The entity.</param>
    /// <param name="id">The row's id.</param>
    internal static IReadOnlyList<string> Keys(IReadOnlyList<CompiledTimer> timers, string entity, Guid id) =>
        [.. timers.Select(timer => KeyOf(entity, timer, id))];

    private static string KeyOf(string entity, CompiledTimer timer, Guid id) => $"{TimerEntries.ReservedKeyPrefix}{entity}:{timer.Name}:{id:D}";

    private static TimerChange? Change(CompiledTimer timer, TimerTarget target)
    {
        var write = target.Write;
        var key = KeyOf(write.Entity, timer, target.Id);
        var next = write.Stored is { } stored ? DueAt(timer, stored, write.Caller, write.Now) : null;
        if (next is not { } due)
        {
            return TimerChange.Cancel(target.Scope, key);
        }

        var before = write.Previous is { } previous ? DueAt(timer, previous, write.Caller, write.Now) : null;
        return before == due ? null : new TimerChange(target.Scope, key, Armed(timer, target, key, due));
    }

    private static TimerChange? Replanned(CompiledTimer timer, ReplanRow row)
    {
        var replan = row.Replan;
        var key = KeyOf(replan.Entity, timer, row.Id);
        DateTimeOffset? armed = replan.Armed.TryGetValue((row.Scope, key), out var at) ? at : null;
        var due = DueAt(timer, replan.Row, replan.Caller, replan.Now);

        return (due, armed) switch
        {
            (null, null) => null,
            (null, _) => TimerChange.Cancel(row.Scope, key),
            ({ } next, { } current) when next == current => null,
            ({ } next, null) when next <= replan.Now => null,
            ({ } next, _) => new TimerChange(row.Scope, key, Rearmed(timer, row, key, next)),
        };
    }

    private static DateTimeOffset? DueAt(CompiledTimer timer, AlvoRecord row, AlvoContext caller, DateTimeOffset now) =>
        timer.When is { } when && !CelInterpreter.EvaluatePredicate(when, row, null, caller)
            ? null
            : CelInterpreter.EvaluateInstant(timer.At, row, caller, now);

    private static TimerEntry Armed(CompiledTimer timer, TimerTarget target, string key, DateTimeOffset due)
    {
        var context = ArmedAs(target);
        target.Depth.EnsureWithin(context);

        return TimerEntries.Of(
            target.Scope, key, due, timer.EventType, Data(target.Write.Entity, timer.Name, target.Id, due), context, target.Write.Now);
    }

    /// <summary>A re-planned timer, armed as the system in the row's tenant, resuming the re-plan's own flow.</summary>
    private static TimerEntry Rearmed(CompiledTimer timer, ReplanRow row, string key, DateTimeOffset due)
    {
        var replan = row.Replan;
        var context = SystemIn(row.Scope) with { Provenance = AlvoEventProvenance.Effective(replan.Caller) };
        row.Depth.EnsureWithin(context);

        return TimerEntries.Of(row.Scope, key, due, timer.EventType, Data(replan.Entity, timer.Name, row.Id, due), context, replan.Now);
    }

    private static AlvoContext SystemIn(string scope) => AlvoContext.System(scope.Length == 0 ? null : TenantId.Parse(scope, null));

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

        return SystemIn(target.Scope) with { Provenance = provenance };
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
            : throw new ArgumentException("A timer's row carries its id.", nameof(row));

    private static Guid GuidOf(object value) => value as Guid? ?? Guid.Parse(Convert.ToString(value, CultureInfo.InvariantCulture)!, CultureInfo.InvariantCulture);

    private sealed record TimerTarget(EntityTimerWrite Write, string Scope, Guid Id, AlvoChainDepthLimit Depth);

    private sealed record ReplanRow(EntityTimerReplan Replan, string Scope, Guid Id, AlvoChainDepthLimit Depth);
}
