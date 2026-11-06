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
/// only remove rows, never add one, and writes are never narrowed; one that cannot be translated fails the read. An enricher
/// sees each returned row as <typeparamref name="T"/> and may only hide a field or fill a virtual one.
/// </remarks>
public sealed partial class AlvoEntityBuilder<T>
{
    /// <summary>Narrows every non-system read of the entity by a <typeparamref name="TNarrowing"/>, built per read.</summary>
    /// <typeparam name="TNarrowing">The narrowing class.</typeparam>
    /// <param name="id">A stable id, lower-case letters, digits and hyphens.</param>
    /// <returns>This builder.</returns>
    public AlvoEntityBuilder<T> Narrow<TNarrowing>(string id)
        where TNarrowing : class, IQueryNarrowing<T> =>
        AddRead(id, ReadExtensionKind.Narrowing, ReadInvoker.NarrowingClass<TNarrowing>((narrowing, query) => Translated(narrowing.Narrow(query), query.Entity)));

    /// <summary>Narrows every non-system read of the entity by the predicate <paramref name="narrow"/> answers.</summary>
    /// <param name="id">A stable id, lower-case letters, digits and hyphens.</param>
    /// <param name="narrow">The predicate ANDed into the read, or <see langword="null"/> to leave it as the rules made it.</param>
    /// <returns>This builder.</returns>
    public AlvoEntityBuilder<T> Narrow(string id, Func<QueryNarrowing, Expression<Func<T, bool>>?> narrow)
    {
        ArgumentNullException.ThrowIfNull(narrow);
        return AddRead(id, ReadExtensionKind.Narrowing, ReadInvoker.NarrowingDelegate(query => Translated(narrow(query), query.Entity)));
    }

    /// <summary>Shapes every row a read or a write's echo returns of the entity by a <typeparamref name="TEnricher"/>, built once per read.</summary>
    /// <remarks>
    /// Alvo builds the class once per read and disposes it after the read. When the host registered the type in its services,
    /// the instance is resolved from the read's scope instead and Alvo does not dispose it: a singleton is shared by
    /// concurrent reads of every caller, so its thread safety, and anything it keeps from one caller to the next, are the
    /// host's to get right.
    /// </remarks>
    /// <typeparam name="TEnricher">The enricher class.</typeparam>
    /// <param name="id">A stable id, lower-case letters, digits and hyphens.</param>
    /// <returns>This builder.</returns>
    public AlvoEntityBuilder<T> Enrich<TEnricher>(string id)
        where TEnricher : class, IRecordEnricher<T> =>
        AddRead(id, ReadExtensionKind.Enricher, ReadInvoker.EnricherClass<TEnricher>(
            (enricher, row, caller, operation) => enricher.Enrich(new Enrich<T>(row, caller, operation))));

    /// <summary>Shapes every row a read or a write's echo returns of the entity by <paramref name="enrich"/>.</summary>
    /// <param name="id">A stable id, lower-case letters, digits and hyphens.</param>
    /// <param name="enrich">Hides fields of, or fills virtual fields on, one returned row.</param>
    /// <returns>This builder.</returns>
    public AlvoEntityBuilder<T> Enrich(string id, Action<Enrich<T>> enrich)
    {
        ArgumentNullException.ThrowIfNull(enrich);
        return AddRead(id, ReadExtensionKind.Enricher, ReadInvoker.EnricherDelegate(
            (row, caller, operation) => enrich(new Enrich<T>(row, caller, operation))));
    }

    private static AlvoFilter? Translated(Expression<Func<T, bool>>? where, string entity) =>
        where is null ? null : FilterTranslator.Translate(where, entity);

    private AlvoEntityBuilder<T> AddRead(string id, ReadExtensionKind kind, ReadInvoker invoker)
    {
        ReadExtensionRegistration.Add(_services, id, [_entity], kind, invoker, RecordShape.Of(typeof(T)));

        return this;
    }
}
