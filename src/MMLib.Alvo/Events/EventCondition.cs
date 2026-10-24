using System.Linq.Expressions;

namespace MMLib.Alvo.Events;

/// <summary>
/// What a typed handler's <c>when</c> lambda reads. Alvo translates the lambda into a CEL condition when the handler is
/// registered and never runs it, so every member throws if called.
/// </summary>
/// <typeparam name="T">The record type the handler is registered for.</typeparam>
/// <remarks>
/// A condition compares mapped properties of <see cref="New"/> and <see cref="Old"/> — as the verb's event carries them —
/// with each other, with constants, <c>static readonly</c> fields and enum members; it may call <see cref="Changed"/> on
/// an update, test <see cref="Caller"/>, use comparisons, <c>&amp;&amp;</c>, <c>||</c>, <c>!</c>, arithmetic,
/// <c>== null</c>, <c>HasValue</c>, the ordinal <c>StartsWith</c>, <c>EndsWith</c> and <c>Contains</c>, and
/// <c>Contains</c> over a constant list. Anything else is refused with an
/// <see cref="Data.AlvoTranslationException"/> when the handler is registered.
/// </remarks>
public sealed class EventCondition<T>
    where T : class
{
    private const string TranslationOnly = "An EventCondition is read by Alvo's translator and never run: pass the lambda to when:.";

    private EventCondition()
    {
    }

    /// <summary>Gets the record after the write (<c>new.</c>); a delete carries none.</summary>
    public T New => throw new NotSupportedException(TranslationOnly);

    /// <summary>Gets the record before the write (<c>old.</c>); an update and a delete carry it.</summary>
    public T Old => throw new NotSupportedException(TranslationOnly);

    /// <summary>Gets who caused the event.</summary>
    public AlvoConditionCaller Caller => throw new NotSupportedException(TranslationOnly);

    /// <summary>Whether any of <paramref name="fields"/> changed in the write (<c>changed(field)</c>); on an update only.</summary>
    /// <param name="fields">Mapped properties of the record, such as <c>x =&gt; x.Status</c>.</param>
    /// <returns>Never returns: the call is translated.</returns>
    public bool Changed(params Expression<Func<T, object?>>[] fields) => throw new NotSupportedException(TranslationOnly);
}
