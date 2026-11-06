using MMLib.Alvo.Data;
using MMLib.Alvo.Data.Internal;
using MMLib.Alvo.Reads;
using MMLib.Alvo.Reads.Internal;

using System.Linq.Expressions;

namespace MMLib.Alvo.Extensions;

/// <summary>The read extensions of the entity <typeparamref name="T"/> maps.</summary>
/// <remarks>
/// A narrowing's predicate is translated into an <see cref="AlvoFilter"/> on every read — a captured value is read then and
/// bound as a parameter — and ANDed into the SQL of every list, get and count a caller other than the system makes. It can
/// only remove rows, never add one, and writes are never narrowed; one that cannot be translated fails the read.
/// </remarks>
public sealed partial class AlvoEntityBuilder<T>
{
    /// <summary>Narrows every non-system read of the entity by a <typeparamref name="TNarrowing"/>, built per read.</summary>
    /// <typeparam name="TNarrowing">The narrowing class.</typeparam>
    /// <param name="id">A stable id, lower-case letters, digits and hyphens.</param>
    /// <returns>This builder.</returns>
    public AlvoEntityBuilder<T> Narrow<TNarrowing>(string id)
        where TNarrowing : class, IQueryNarrowing<T> =>
        AddRead(id, ReadInvoker.NarrowingClass<TNarrowing>((narrowing, query) => Translated(narrowing.Narrow(query), query.Entity)));

    /// <summary>Narrows every non-system read of the entity by the predicate <paramref name="narrow"/> answers.</summary>
    /// <param name="id">A stable id, lower-case letters, digits and hyphens.</param>
    /// <param name="narrow">The predicate ANDed into the read, or <see langword="null"/> to leave it as the rules made it.</param>
    /// <returns>This builder.</returns>
    public AlvoEntityBuilder<T> Narrow(string id, Func<QueryNarrowing, Expression<Func<T, bool>>?> narrow)
    {
        ArgumentNullException.ThrowIfNull(narrow);
        return AddRead(id, ReadInvoker.NarrowingDelegate(query => Translated(narrow(query), query.Entity)));
    }

    private static AlvoFilter? Translated(Expression<Func<T, bool>>? where, string entity) =>
        where is null ? null : FilterTranslator.Translate(where, entity);

    private AlvoEntityBuilder<T> AddRead(string id, ReadInvoker invoker)
    {
        ReadExtensionRegistration.Add(_services, id, [_entity], ReadExtensionKind.Narrowing, invoker, RecordShape.Of(typeof(T)));

        return this;
    }
}
