namespace MMLib.Alvo.Data;

/// <summary>
/// Thrown when an <see cref="AlvoIdempotency"/> key has already been used for a <em>different</em> request:
/// same key, different <see cref="AlvoIdempotency.Fingerprint"/>.
/// </summary>
/// <remarks>
/// <para>
/// <b>Not a replay, and answering it as one would lose data.</b> Returning the first request's row would
/// report success for a create that never happened — the caller's second, genuinely different payload would
/// be silently discarded, and they would hold an id for a row that does not contain what they sent.
/// Creating a second row instead would break the promise the key exists to make. So the only safe answer is
/// to refuse and say why.
/// </para>
/// <para>
/// <b>Its own family, not <see cref="ArgumentException"/>.</b> The request is well-formed; what is wrong is
/// its relationship to a request the store already accepted, which nothing about this payload alone could
/// have revealed. It renders <c>409</c> — the fix is a fresh key, not a corrected body, and that is
/// advice a <c>422</c> would not carry.
/// </para>
/// <para>
/// The message names neither the key nor the entity: the key is caller-supplied text this port does not echo
/// (it is a log-injection vector like every other such string here), and a message confirming which key is
/// already in use would let one caller probe another's keys.
/// </para>
/// </remarks>
public sealed class AlvoIdempotencyConflictException : Exception
{
    private const string DefaultMessage =
        "This idempotency key was already used for a different request. Reusing one key for two requests "
        + "would silently discard the second, so send a fresh key.";

    /// <summary>Initializes a new instance of the <see cref="AlvoIdempotencyConflictException"/> class.</summary>
    public AlvoIdempotencyConflictException()
        : base(DefaultMessage)
    {
    }

    /// <summary>Initializes a new instance of the <see cref="AlvoIdempotencyConflictException"/> class.</summary>
    /// <param name="message">The exception message.</param>
    public AlvoIdempotencyConflictException(string message)
        : base(message)
    {
    }

    /// <summary>Initializes a new instance of the <see cref="AlvoIdempotencyConflictException"/> class.</summary>
    /// <param name="message">The exception message.</param>
    /// <param name="innerException">The inner exception.</param>
    public AlvoIdempotencyConflictException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
