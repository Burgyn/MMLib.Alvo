namespace MMLib.Alvo.Sweeping;

/// <summary>
/// One table's share of the framework sweep (#527): deletes at most <c>limit</c> rows the table no longer needs, in a short
/// transaction of its own, and answers how many it deleted. <see cref="FrameworkSweeper"/> calls it again while a batch
/// comes back full, up to <see cref="SweepLimits.MaxBatches"/> times a tick.
/// </summary>
/// <remarks>
/// <para>
/// <b>Internal, registered by the package that owns the table</b> with <c>TryAddEnumerable</c>: the core registers the two
/// framework ledgers' jobs, <c>MMLib.Alvo.Accounts</c> its sessions, refresh tokens, app tokens and invitations. The
/// Apps line's N6 — a row <c>retention</c> trait, and pruning <c>alvo_outbox</c> (#154) and <c>alvo_idempotency</c> (#115) —
/// adds jobs here, never a second sweeper.
/// </para>
/// <para>
/// <b>A job decides what is dead; the sweeper decides how often and how much.</b> A job computes its thresholds from
/// <c>now</c> in C# and binds them, so its SQL is one text on every engine, and deletes through a bounded sub-select
/// (<c>… WHERE key IN (SELECT key … LIMIT @limit)</c>) — SQLite's own <c>DELETE … LIMIT</c> needs a compile option. Every
/// replica sweeps; the deletes are idempotent.
/// </para>
/// </remarks>
internal interface ISweepJob
{
    /// <summary>Gets the job's name, as the sweep's log lines report it.</summary>
    string Name { get; }

    /// <summary>Deletes at most <paramref name="limit"/> rows that are dead at <paramref name="now"/>; answers how many.</summary>
    /// <param name="now">The instant the sweep runs at.</param>
    /// <param name="limit">The most rows this call may delete.</param>
    /// <param name="ct">A token to cancel the operation.</param>
    Task<int> SweepAsync(DateTimeOffset now, int limit, CancellationToken ct);
}

/// <summary>The framework sweep's bounds.</summary>
internal static class SweepLimits
{
    /// <summary>The most rows one batch of one job deletes.</summary>
    internal const int BatchSize = 500;

    /// <summary>The most batches one job runs a tick, so a tick deletes at most 10 000 rows per table per replica.</summary>
    internal const int MaxBatches = 20;

    /// <summary>How often the framework sweeps.</summary>
    internal static TimeSpan Interval { get; } = TimeSpan.FromHours(1);
}
