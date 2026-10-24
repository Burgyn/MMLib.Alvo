using System.Diagnostics.CodeAnalysis;

namespace MMLib.Alvo.Events;

/// <summary>Reacts to one committed event, after the write that caused it, at least once.</summary>
/// <typeparam name="TMessage">
/// What the handler is handed: <see cref="AlvoCreated{T}"/>, <see cref="AlvoUpdated{T}"/>, <see cref="AlvoDeleted{T}"/> or
/// <see cref="AlvoSaved{T}"/> over <c>AlvoRecord</c> for an entity's events, or <c>AlvoRecord</c> — the event's payload —
/// for an application event.
/// </typeparam>
/// <remarks>
/// <para>
/// <b>At least once.</b> A delivery can run again — after a failure, a timeout, or a process that stopped mid-run — and
/// always with the same <see cref="AlvoEventContext.EventId"/>: deduplicate on it before an effect you cannot repeat.
/// </para>
/// <para>
/// The handler is created per delivery in its own dependency-injection scope. Throwing fails the attempt; it is retried on
/// the handler's schedule and dead after the last attempt. Its exception's message is never stored or logged.
/// </para>
/// </remarks>
[SuppressMessage(
    "Naming",
    "CA1711:Identifiers should not have incorrect suffix",
    Justification = "An Alvo event handler is the product's own concept, named so by the spec; it is not a .NET event delegate.")]
public interface IAlvoEventHandler<in TMessage>
    where TMessage : class
{
    /// <summary>Handles one delivery.</summary>
    /// <param name="message">The event's images, or its payload.</param>
    /// <param name="context">The event, the attempt, and data access as the handler's caller.</param>
    /// <param name="cancellationToken">Cancelled at the handler's timeout or when the host stops.</param>
    /// <returns>A task that completes when the delivery is handled.</returns>
    Task HandleAsync(TMessage message, AlvoEventContext context, CancellationToken cancellationToken);
}
