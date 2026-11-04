using MMLib.Alvo.Data;
using MMLib.Alvo.Data.Internal;
using MMLib.Alvo.Hooks.Internal;
using System.Linq.Expressions;

namespace MMLib.Alvo.Hooks;

/// <summary>A write as a before-write hook sees it: inside the write's transaction, before the row is written.</summary>
/// <typeparam name="T">The record type.</typeparam>
public sealed class BeforeWrite<T>
    where T : class
{
    private readonly HookRun _run;

    internal BeforeWrite(HookRun run)
    {
        _run = run;
        Old = HookImage<T>.Of(run.Invocation.Old);
        New = HookImage<T>.Of(run.Invocation.New)!;
    }

    /// <summary>Gets the write: a create, an update or a replace.</summary>
    public AlvoWriteOperation Operation => _run.Invocation.Operation;

    /// <summary>Gets the row before the write, read under the write's lock; null on a create.</summary>
    public T? Old { get; }

    /// <summary>Gets the row the write would store, with the earlier hooks' sets; on a create it has no id yet, so a typed row's <c>Id</c> is empty.</summary>
    public T New { get; }

    /// <summary>Gets the write's transaction, as the caller, one hop deeper in the causation chain: every call counts toward the hook's budget.</summary>
    public IAlvoTransaction Transaction => _run.Transaction!;

    /// <summary>Gets the caller.</summary>
    public AlvoContext Caller => _run.Invocation.Caller;

    /// <summary>Gets the instant the write is stamped with.</summary>
    public DateTimeOffset Now => _run.Invocation.Now;

    /// <summary>
    /// Sets a field of the row to be stored; the row is then judged by the entity's rules and the field's facets again. A field
    /// the caller's policy makes read-only is refused 403, as the caller's own payload would be; an undeclared field, one Alvo
    /// manages, or a value the field cannot hold fails the write (500 <c>invalid-set</c>).
    /// </summary>
    /// <param name="field">The field's name.</param>
    /// <param name="value">The value, as the field holds it.</param>
    public void Set(string field, object? value) => _run.Set(field, value);

    /// <summary>
    /// Sets a mapped property's field of the row to be stored; refused at the call for a field Alvo manages. Judged as
    /// <see cref="Set(string, object?)"/> is: a read-only field is refused 403, a value the field cannot hold is <c>invalid-set</c>.
    /// </summary>
    /// <typeparam name="TValue">The property's type.</typeparam>
    /// <param name="field">The property, such as <c>x =&gt; x.Status</c>.</param>
    /// <param name="value">The value.</param>
    public void Set<TValue>(Expression<Func<T, TValue>> field, TValue value)
    {
        foreach (var (name, stored) in new TypedPatch<T>().Set(field, value).Values)
        {
            _run.Set(name, stored);
        }
    }

    /// <summary>Refuses the write as invalid data (422); refusing also discards this hook's sets.</summary>
    /// <param name="code">A stable code a client can branch on.</param>
    /// <param name="message">What is wrong, for a person; never name a hidden field in it.</param>
    /// <param name="field">The field at fault, or <see langword="null"/>; a hidden field is never pointed at.</param>
    public void Invalid(string code, string message, string? field = null) => _run.Rejections.Record(AlvoWriteRejectionKind.Invalid, code, message, field);

    /// <summary>Refuses the write as a clash with other data (409); refusing also discards this hook's sets.</summary>
    /// <param name="code">A stable code a client can branch on.</param>
    /// <param name="message">What clashes, for a person; never name a hidden field in it.</param>
    /// <param name="field">The field at fault, or <see langword="null"/>; a hidden field is never pointed at.</param>
    public void Conflict(string code, string message, string? field = null) => _run.Rejections.Record(AlvoWriteRejectionKind.Conflict, code, message, field);

    /// <summary>Refuses the write as not permitted for this caller (403); refusing also discards this hook's sets.</summary>
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
