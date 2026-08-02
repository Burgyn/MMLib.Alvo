namespace MMLib.Alvo.Data;

/// <summary>
/// Thrown when a write carried an <see cref="AlvoPrecondition"/> the stored row does not satisfy: the row
/// has been written since the caller read it, or the entity keeps no version of a row at all and therefore
/// cannot answer the question.
/// </summary>
/// <remarks>
/// <para>
/// <b>Its own family, deliberately not folded into <see cref="ArgumentException"/>.</b> A request layer above
/// this port has nothing but the exception type to map a status code from, and a failed precondition is
/// neither a malformed request (the version was well-formed, and was true when the caller read it) nor a
/// denial (the caller may write this row — someone simply got there first). It renders <c>412</c>, and the
/// right client behaviour is to re-read and retry, which is exactly the advice neither <c>422</c> nor
/// <c>403</c> conveys.
/// </para>
/// <para>
/// The default message says only that the record changed. It names no entity, no row id and — critically —
/// <b>not the stored version</b>: handing back the value the caller's guess failed against would turn a
/// refused write into a read of a row they may not be able to read, and would let a caller learn a row's
/// write history by guessing.
/// </para>
/// <para>
/// It is raised only after the operation's <c>USING</c> predicate has admitted the row. A row the caller
/// cannot see raises <see cref="AlvoRecordNotFoundException"/> whichever precondition was supplied, so this
/// exception never answers "does that row exist".
/// </para>
/// </remarks>
public sealed class AlvoPreconditionFailedException : Exception
{
    private const string DefaultMessage =
        "The record was changed since the version this write carries. Re-read it and retry.";

    /// <summary>Initializes a new instance of the <see cref="AlvoPreconditionFailedException"/> class.</summary>
    public AlvoPreconditionFailedException()
        : base(DefaultMessage)
    {
    }

    /// <summary>Initializes a new instance of the <see cref="AlvoPreconditionFailedException"/> class.</summary>
    /// <param name="message">The exception message.</param>
    public AlvoPreconditionFailedException(string message)
        : base(message)
    {
    }

    /// <summary>Initializes a new instance of the <see cref="AlvoPreconditionFailedException"/> class.</summary>
    /// <param name="message">The exception message.</param>
    /// <param name="innerException">The inner exception.</param>
    public AlvoPreconditionFailedException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
