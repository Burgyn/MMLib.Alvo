namespace MMLib.Alvo.Migrations;

/// <summary>
/// Thrown when an apply waited longer than its lock timeout for a table other transactions are using — or the database
/// failed it to break a deadlock with one of them — and so changed nothing: a retryable refusal, never a broken invariant
/// (HX-11 Ruling 20; issue 443).
/// </summary>
/// <remarks>
/// <para>
/// <b>Why the wait is bounded at all.</b> Adding a <c>CHECK</c> on PostgreSQL takes <c>ACCESS EXCLUSIVE</c>, and a lock
/// request queued behind a long reader also blocks every reader that arrives after it. A driver that can bound the wait
/// does so as the first statement of every apply transaction, and its executors raise this when the bound is hit.
/// </para>
/// <para>
/// <b>A deadlock is the same refusal with its own sentence</b> (<see cref="Deadlocked"/>, issue 443). The backfill's child
/// locks are taken before the DDL so writers and applies agree on an order (HX-11 Ruling 74), but a parent delete that
/// cascades to its children runs parent then child, and the engine may break that cycle by failing the apply. Either way
/// the apply lost a wait for a lock and changed nothing, and either way the answer is to retry it — so every surface that
/// already answers a lock timeout answers this one, and only the message tells them apart. One type rather than a sibling
/// keeps the boot's convergence on a lost version-row race (<see cref="AtTheVersionRow"/>) for both.
/// </para>
/// <para>
/// <b>Internal.</b> It is a fact about one apply, answered by the Management API (409 <c>conflict</c> with
/// <c>Retry-After</c>) and by the boot (a refused start). The dashboard catches it by type through the D45 grant (Ruling
/// 78). The cost is deliberate and recorded (Ruling 82): an embedded host calling <c>IAlvoManagement</c> in-process
/// cannot catch it by type and must treat it as a failed apply carrying <see cref="Refusal"/>; a public, catchable
/// refusal type for the Management port is a follow-up, designed once for every refusal rather than for this one.
/// </para>
/// </remarks>
internal sealed class AlvoApplyLockTimeoutException : Exception
{
    /// <summary>The one sentence every answer to this refusal carries.</summary>
    internal const string Refusal =
        "The apply waited longer than its lock timeout for a table other transactions are using, and changed nothing. Retry it.";

    /// <summary>The one sentence every answer to a deadlock the apply lost carries.</summary>
    internal const string DeadlockRefusal =
        "The database failed the apply to break a deadlock with another transaction, and nothing changed. Retry it.";

    /// <summary>The seconds a caller should wait before retrying — the bound the apply itself waits.</summary>
    internal const int RetryAfterSeconds = 5;

    /// <summary>
    /// Whether the wait that timed out was the version-row insert — the optimistic-lock gate — rather than the plan's DDL.
    /// </summary>
    /// <remarks>
    /// That insert waits only on another writer's uncommitted row for the same revision, so a timeout there is a race
    /// lost to a slow winner, and the boot converges on it instead of refusing the start (HX-11 Ruling 81). Set by the
    /// runtime writer, which alone knows which statement was running.
    /// </remarks>
    internal bool AtTheVersionRow { get; init; }

    /// <summary>Initializes a new instance of the <see cref="AlvoApplyLockTimeoutException"/> class.</summary>
    public AlvoApplyLockTimeoutException()
        : base(Refusal)
    {
    }

    /// <summary>Initializes a new instance of the <see cref="AlvoApplyLockTimeoutException"/> class.</summary>
    /// <param name="message">The exception message.</param>
    public AlvoApplyLockTimeoutException(string message)
        : base(message)
    {
    }

    /// <summary>Initializes a new instance of the <see cref="AlvoApplyLockTimeoutException"/> class.</summary>
    /// <param name="message">The exception message.</param>
    /// <param name="innerException">The engine's own failure.</param>
    public AlvoApplyLockTimeoutException(string message, Exception innerException)
        : base(message, innerException)
    {
    }

    /// <summary>Initializes a new instance from the engine's own lock-timeout failure, kept as the inner exception.</summary>
    /// <param name="innerException">The engine's own failure.</param>
    public AlvoApplyLockTimeoutException(Exception innerException)
        : base(Refusal, innerException)
    {
    }

    /// <summary>The refusal for an apply the engine failed to break a deadlock, its own failure kept as the inner exception.</summary>
    /// <param name="engine">The engine's own deadlock failure.</param>
    internal static AlvoApplyLockTimeoutException Deadlocked(Exception engine) => new(DeadlockRefusal, engine);
}
