using MMLib.Alvo.Data;
using MMLib.Alvo.Hooks.Internal;

namespace MMLib.Alvo.Hooks;

/// <summary>A delete as an after-delete hook sees it: inside the delete's transaction, after the row is removed and its rollups recomputed.</summary>
/// <typeparam name="T">The record type.</typeparam>
public sealed class AfterDelete<T>
    where T : class
{
    private readonly HookRun _run;

    internal AfterDelete(HookRun run)
    {
        _run = run;
        Old = HookImage<T>.Of(run.Invocation.Old, run.Derived)!;
    }

    /// <summary>Gets the row that was deleted.</summary>
    public T Old { get; }

    /// <summary>Gets the delete's transaction, as the caller, one hop deeper in the causation chain: every call counts toward the hook's budget.</summary>
    public IAlvoTransaction Transaction => _run.Transaction!;

    /// <summary>Gets the caller.</summary>
    public AlvoContext Caller => _run.Invocation.Caller;

    /// <summary>Gets the instant the delete is stamped with.</summary>
    public DateTimeOffset Now => _run.Invocation.Now;

    /// <summary>Refuses the delete as invalid data (422); refusing rolls the delete back.</summary>
    /// <param name="code">A stable code a client can branch on.</param>
    /// <param name="message">What is wrong, for a person; never name a hidden field in it.</param>
    /// <param name="field">The field at fault, or <see langword="null"/>; a hidden field is never pointed at.</param>
    public void Invalid(string code, string message, string? field = null) => _run.Rejections.Record(AlvoWriteRejectionKind.Invalid, code, message, field);

    /// <summary>Refuses the delete as a clash with other data (409); refusing rolls the delete back.</summary>
    /// <param name="code">A stable code a client can branch on.</param>
    /// <param name="message">What clashes, for a person; never name a hidden field in it.</param>
    /// <param name="field">The field at fault, or <see langword="null"/>; a hidden field is never pointed at.</param>
    public void Conflict(string code, string message, string? field = null) => _run.Rejections.Record(AlvoWriteRejectionKind.Conflict, code, message, field);

    /// <summary>Refuses the delete as not permitted for this caller (403); refusing rolls the delete back.</summary>
    /// <param name="code">A stable code a client can branch on.</param>
    /// <param name="message">Why, for a person; never name a hidden field in it.</param>
    public void Forbidden(string code, string message) => _run.Rejections.Record(AlvoWriteRejectionKind.Forbidden, code, message, null);

    /// <summary>Publishes a custom event that commits with the delete, or not at all; one call of the budget.</summary>
    /// <param name="type">The event's name.</param>
    /// <param name="subject">What it is about.</param>
    /// <param name="data">Its payload, or <see langword="null"/>.</param>
    /// <param name="cancellationToken">A token to cancel the publish.</param>
    /// <returns>A task that completes when the event is written into the transaction.</returns>
    public Task PublishAsync(string type, string subject, IReadOnlyDictionary<string, object?>? data, CancellationToken cancellationToken = default) =>
        Transaction.PublishAsync(type, subject, data, cancellationToken);
}
