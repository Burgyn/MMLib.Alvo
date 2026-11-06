namespace MMLib.Alvo.Migrations;

/// <summary>
/// Thrown when an apply waited longer than its lock timeout for a table other transactions are using, and so changed
/// nothing — a retryable refusal, never a broken invariant (HX-11 Ruling 20).
/// </summary>
/// <remarks>
/// <para>
/// <b>Why the wait is bounded at all.</b> Adding a <c>CHECK</c> on PostgreSQL takes <c>ACCESS EXCLUSIVE</c>, and a lock
/// request queued behind a long reader also blocks every reader that arrives after it. A driver that can bound the wait
/// does so as the first statement of every apply transaction, and its executors raise this when the bound is hit.
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
}
