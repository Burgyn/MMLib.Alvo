namespace MMLib.Alvo.Data;

/// <summary>
/// A data call was made while a transaction unit is open on the current flow, through a port that would run outside the
/// unit — <see cref="IAlvoData"/>, <see cref="IAlvoSession"/>, <c>IAlvoEvents.PublishAsync</c>, a nested
/// <see cref="IAlvoTransactions.TransactAsync{T}"/>, or a write through a framework store such as <c>IOutboxStore</c>.
/// </summary>
/// <remarks>
/// <para>
/// Such a call would run on a second connection: it could not see the unit's writes, would not roll back with them, could
/// carry a different caller into the middle of this one's work, and on an engine with one writer would wait for the
/// unit's own lock. It is refused at once instead. Make the call through the unit's <see cref="IAlvoTransaction"/>, or
/// move it before or after <c>TransactAsync</c>. The refusal follows the .NET execution context: work handed to a queue of
/// the host's own, or run with the flow suppressed, is out of its reach.
/// </para>
/// <para>
/// The refusal also fails the unit: it rolls back however the delegate ends, so a caught refusal cannot commit work its
/// author believed included the refused call. An <see cref="InvalidOperationException"/>, so <see cref="IAlvoData"/>'s
/// invariant family.
/// </para>
/// </remarks>
public sealed class AlvoUnitInProgressException : InvalidOperationException
{
    private const string DefaultMessage =
        "A data call was made while a transaction unit is open on this flow; use the unit's IAlvoTransaction instead.";

    /// <summary>Initializes a new instance with the default message.</summary>
    public AlvoUnitInProgressException()
        : base(DefaultMessage)
    {
    }

    /// <summary>Initializes a new instance with <paramref name="message"/>.</summary>
    /// <param name="message">The message.</param>
    public AlvoUnitInProgressException(string message)
        : base(message)
    {
    }

    /// <summary>Initializes a new instance with <paramref name="message"/> and its cause.</summary>
    /// <param name="message">The message.</param>
    /// <param name="innerException">The cause.</param>
    public AlvoUnitInProgressException(string message, Exception innerException)
        : base(message, innerException)
    {
    }

    /// <summary>The refusal of <paramref name="call"/>.</summary>
    /// <param name="call">The refused member, as <c>IAlvoData.CreateAsync</c>.</param>
    /// <returns>The exception to throw.</returns>
    internal static AlvoUnitInProgressException For(string call) => new(
        $"{call} was called while a transaction unit is open on this flow, so it was refused and the unit will roll back. "
        + "A call through the data port would run outside the unit's transaction, on a second connection. Fix: make the "
        + "call through the unit's IAlvoTransaction (the delegate's parameter), or move it before or after TransactAsync.");
}
