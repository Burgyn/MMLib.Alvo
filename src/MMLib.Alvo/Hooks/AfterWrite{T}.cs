using MMLib.Alvo.Data;
using MMLib.Alvo.Hooks.Internal;

namespace MMLib.Alvo.Hooks;

/// <summary>A write as an after-write hook sees it: inside the write's transaction, after the row and its rollups are stored.</summary>
/// <typeparam name="T">The record type.</typeparam>
public sealed class AfterWrite<T>
    where T : class
{
    private readonly HookRun _run;

    internal AfterWrite(HookRun run)
    {
        _run = run;
        Old = HookImage<T>.Of(run.Invocation.Old);
        Saved = HookImage<T>.Of(run.Invocation.Saved)!;
    }

    /// <summary>Gets the write: a create, an update or a replace.</summary>
    public AlvoWriteOperation Operation => _run.Invocation.Operation;

    /// <summary>Gets the row before the write; null on a create.</summary>
    public T? Old { get; }

    /// <summary>Gets the row as stored: its id, defaults and rollups.</summary>
    public T Saved { get; }

    /// <summary>Gets the write's transaction, as the caller, one hop deeper in the causation chain: every call counts toward the hook's budget.</summary>
    public IAlvoTransaction Transaction => _run.Transaction!;

    /// <summary>Gets the caller.</summary>
    public AlvoContext Caller => _run.Invocation.Caller;

    /// <summary>Gets the instant the write is stamped with.</summary>
    public DateTimeOffset Now => _run.Invocation.Now;

    /// <summary>Refuses the write as invalid data (422); refusing rolls the write back.</summary>
    /// <param name="code">A stable code a client can branch on.</param>
    /// <param name="message">What is wrong, for a person; never name a hidden field in it.</param>
    /// <param name="field">The field at fault, or <see langword="null"/>; a hidden field is never pointed at.</param>
    public void Invalid(string code, string message, string? field = null) => _run.Rejections.Record(AlvoWriteRejectionKind.Invalid, code, message, field);

    /// <summary>Refuses the write as a clash with other data (409); refusing rolls the write back.</summary>
    /// <param name="code">A stable code a client can branch on.</param>
    /// <param name="message">What clashes, for a person; never name a hidden field in it.</param>
    /// <param name="field">The field at fault, or <see langword="null"/>; a hidden field is never pointed at.</param>
    public void Conflict(string code, string message, string? field = null) => _run.Rejections.Record(AlvoWriteRejectionKind.Conflict, code, message, field);

    /// <summary>Refuses the write as not permitted for this caller (403); refusing rolls the write back.</summary>
    /// <param name="code">A stable code a client can branch on.</param>
    /// <param name="message">Why, for a person; never name a hidden field in it.</param>
    public void Forbidden(string code, string message) => _run.Rejections.Record(AlvoWriteRejectionKind.Forbidden, code, message, null);

    /// <summary>Publishes a custom event that commits with the write, or not at all; one call of the budget.</summary>
    /// <param name="type">The event's name.</param>
    /// <param name="subject">What it is about.</param>
    /// <param name="data">Its payload, or <see langword="null"/>.</param>
    /// <param name="cancellationToken">A token to cancel the publish.</param>
    /// <returns>A task that completes when the event is written into the transaction.</returns>
    public Task PublishAsync(string type, string subject, IReadOnlyDictionary<string, object?>? data, CancellationToken cancellationToken = default) =>
        Transaction.PublishAsync(type, subject, data, cancellationToken);
}
