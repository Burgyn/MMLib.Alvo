using MMLib.Alvo.Events;
using System.Linq.Expressions;

namespace MMLib.Alvo.Hooks;

/// <summary>
/// What a typed write hook's <c>when</c> lambda reads. Alvo translates the lambda into a CEL condition when the hook is
/// registered and never runs it, so every member throws if called.
/// </summary>
/// <typeparam name="T">The record type the hook is registered for.</typeparam>
/// <remarks>
/// <see cref="New"/> is the row the write would store (after-write: the stored row), <see cref="Old"/> the row before it —
/// absent on a create, so a comparison with it is false there — and <see cref="Changed"/> is false on a create. A delete hook
/// reads <see cref="Old"/> only. The constructs a condition may use are an <c>EventCondition</c>'s.
/// </remarks>
public sealed class WriteCondition<T>
    where T : class
{
    private const string TranslationOnly = "A WriteCondition is read by Alvo's translator and never run: pass the lambda to when:.";

    private WriteCondition()
    {
    }

    /// <summary>Gets the row the write would store (<c>new.</c>).</summary>
    public T New => throw new NotSupportedException(TranslationOnly);

    /// <summary>Gets the row before the write (<c>old.</c>).</summary>
    public T Old => throw new NotSupportedException(TranslationOnly);

    /// <summary>Gets who makes the write.</summary>
    public AlvoConditionCaller Caller => throw new NotSupportedException(TranslationOnly);

    /// <summary>Whether any of <paramref name="fields"/> changes in the write (<c>changed(field)</c>); false on a create.</summary>
    /// <param name="fields">Mapped properties of the record, such as <c>x =&gt; x.Status</c>.</param>
    /// <returns>Never returns: the call is translated.</returns>
    public bool Changed(params Expression<Func<T, object?>>[] fields) => throw new NotSupportedException(TranslationOnly);
}
