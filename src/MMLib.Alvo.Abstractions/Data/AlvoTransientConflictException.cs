namespace MMLib.Alvo.Data;

/// <summary>
/// Thrown when the database broke a lock cycle between this write and a concurrent transaction by failing this one — a
/// PostgreSQL deadlock (<c>40P01</c>) — so it rolled back and changed nothing. Retrying the same operation is safe.
/// </summary>
/// <remarks>
/// <para>
/// <b>Its own family, because no other one fits.</b> The request was well-formed, the caller was allowed to make it, it
/// carried no stale version and it collides with no stored row: it lost a race to a transaction that took the same locks
/// in the other order. A request layer renders <c>409 conflict</c> with a <c>Retry-After</c>, and the right client
/// behaviour is to send the same request again — the advice none of the other families conveys.
/// </para>
/// <para>
/// <b>Never retried by the port itself</b> (spec 2026-10-14 §4.5): a write, and a <c>TransactAsync</c> delegate, runs
/// once, so host code is never re-run behind the caller's back. A write with an <see cref="AlvoIdempotency"/> token keeps
/// the attempt loop it already had, which re-runs any storage failure — a deadlock among them — before it answers. The
/// engine's own failure is kept as the inner exception.
/// </para>
/// <para>
/// <b>Engine-agnostic by construction.</b> Each driver's dialect decides which of its failures is a deadlock; SQLite,
/// which has one writer at a time and no lock cycle to break, raises none.
/// </para>
/// </remarks>
public sealed class AlvoTransientConflictException : Exception
{
    private const string DefaultMessage =
        "The database broke a deadlock between this write and a concurrent one by failing this write, so nothing was "
        + "changed. Retry the same request.";

    /// <summary>Initializes a new instance of the <see cref="AlvoTransientConflictException"/> class.</summary>
    public AlvoTransientConflictException()
        : base(DefaultMessage)
    {
    }

    /// <summary>Initializes a new instance of the <see cref="AlvoTransientConflictException"/> class.</summary>
    /// <param name="message">The exception message.</param>
    public AlvoTransientConflictException(string message)
        : base(message)
    {
    }

    /// <summary>Initializes a new instance of the <see cref="AlvoTransientConflictException"/> class.</summary>
    /// <param name="message">The exception message.</param>
    /// <param name="innerException">The engine's own failure.</param>
    public AlvoTransientConflictException(string message, Exception innerException)
        : base(message, innerException)
    {
    }

    /// <summary>Initializes a new instance from the engine's own deadlock failure, kept as the inner exception.</summary>
    /// <param name="innerException">The engine's own failure.</param>
    public AlvoTransientConflictException(Exception innerException)
        : base(DefaultMessage, innerException)
    {
    }
}
