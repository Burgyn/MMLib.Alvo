using MMLib.Alvo.Data;
using MMLib.Alvo.Hooks.Internal;

namespace MMLib.Alvo.Hooks;

/// <summary>A write as a validate hook sees it: before any transaction, with a data session as the caller.</summary>
/// <typeparam name="T">The record type.</typeparam>
public sealed class ValidateWrite<T>
    where T : class
{
    private readonly HookRun _run;

    internal ValidateWrite(HookRun run)
    {
        _run = run;
        Old = HookImage<T>.Of(run.Invocation.Old);
        New = HookImage<T>.Of(run.Invocation.New)!;
    }

    /// <summary>Gets the write: a create, an update or a replace.</summary>
    public AlvoWriteOperation Operation => _run.Invocation.Operation;

    /// <summary>Gets the row before the write, read with no lock — another write may move it before this one runs; null on a create.</summary>
    public T? Old { get; }

    /// <summary>Gets the row the write would store; on a create it has no id yet, so a typed row's <c>Id</c> is empty.</summary>
    public T New { get; }

    /// <summary>Gets data access as the caller, one hop deeper in the causation chain; each call commits on its own.</summary>
    public IAlvoSession Data => _run.Data!;

    /// <summary>Gets the caller.</summary>
    public AlvoContext Caller => _run.Invocation.Caller;

    /// <summary>Gets the instant the write is stamped with.</summary>
    public DateTimeOffset Now => _run.Invocation.Now;

    /// <summary>Refuses the write as invalid data (422).</summary>
    /// <param name="code">A stable code a client can branch on.</param>
    /// <param name="message">What is wrong, for a person; never name a hidden field in it.</param>
    /// <param name="field">The field at fault, or <see langword="null"/>; a hidden field is never pointed at.</param>
    public void Invalid(string code, string message, string? field = null) => _run.Rejections.Record(AlvoWriteRejectionKind.Invalid, code, message, field);

    /// <summary>Refuses the write as a clash with other data (409).</summary>
    /// <param name="code">A stable code a client can branch on.</param>
    /// <param name="message">What clashes, for a person; never name a hidden field in it.</param>
    /// <param name="field">The field at fault, or <see langword="null"/>; a hidden field is never pointed at.</param>
    public void Conflict(string code, string message, string? field = null) => _run.Rejections.Record(AlvoWriteRejectionKind.Conflict, code, message, field);

    /// <summary>Refuses the write as not permitted for this caller (403).</summary>
    /// <param name="code">A stable code a client can branch on.</param>
    /// <param name="message">Why, for a person; never name a hidden field in it.</param>
    public void Forbidden(string code, string message) => _run.Rejections.Record(AlvoWriteRejectionKind.Forbidden, code, message, null);
}
