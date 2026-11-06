using MMLib.Alvo.Data;

namespace MMLib.Alvo.Scheduling;

/// <summary>One write's rows, as the descriptor's timers see them.</summary>
/// <param name="Entity">The written entity.</param>
/// <param name="Previous">The row before the write, or <see langword="null"/> on a create.</param>
/// <param name="Stored">The row after the write, or <see langword="null"/> on a delete.</param>
/// <param name="Caller">The writer, whose provenance a fired timer carries.</param>
/// <param name="Now">The write's own instant, which <c>now()</c> resolves to.</param>
/// <param name="CorrelationId">The correlation id of the event the write emitted, which a fired timer resumes.</param>
/// <param name="CausationId">The id of the event the write emitted, which a fired timer is caused by.</param>
internal sealed record EntityTimerWrite(
    string Entity, AlvoRecord? Previous, AlvoRecord? Stored, AlvoContext Caller, DateTimeOffset Now, string? CorrelationId, string? CausationId);

/// <summary>One timer a write arms or moves (<see cref="Arm"/> set), or cancels by key (<see cref="Arm"/> null).</summary>
/// <param name="Scope">The timer's scope: the row's tenant, or empty.</param>
/// <param name="Key">The timer's key, <c>alvo:&lt;entity&gt;:&lt;timer&gt;:&lt;id&gt;</c>.</param>
/// <param name="Arm">The entry to upsert, or <see langword="null"/> to cancel.</param>
internal sealed record TimerChange(string Scope, string Key, TimerEntry? Arm)
{
    /// <summary>The change that cancels <paramref name="key"/> in <paramref name="scope"/>.</summary>
    /// <param name="scope">The timer's scope.</param>
    /// <param name="key">The timer's key.</param>
    internal static TimerChange Cancel(string scope, string key) => new(scope, key, null);
}
