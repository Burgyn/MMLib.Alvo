using MMLib.Alvo.Data;
using MMLib.Alvo.Reads;
using MMLib.Alvo.Reads.Internal;

namespace MMLib.Alvo.Extensions;

/// <summary>The read extensions of the named entities.</summary>
/// <remarks>
/// A narrowing's filter is ANDed into the SQL of every list, get and count a caller other than the system makes, beside
/// the rules and the tenant scope: it can only remove rows, never add one, and writes are never narrowed. An enricher shapes
/// every row a read or a write's echo returns, for every caller, after the rules and masks: it can only hide a field or fill
/// one the descriptor declares virtual. Both run inside the read — possibly inside a unit's transaction — so a type that
/// takes a network or data-port dependency refuses the start; one that throws, or answers what the entity cannot hold, fails
/// the read.
/// </remarks>
public sealed partial class AlvoEntityBuilder
{
    /// <summary>Narrows every non-system read of these entities by a <typeparamref name="TNarrowing"/>, built per read.</summary>
    /// <typeparam name="TNarrowing">The narrowing class.</typeparam>
    /// <param name="id">A stable id, lower-case letters, digits and hyphens.</param>
    /// <returns>This builder.</returns>
    public AlvoEntityBuilder Narrow<TNarrowing>(string id)
        where TNarrowing : class, IQueryNarrowing =>
        AddRead(id, ReadExtensionKind.Narrowing, ReadInvoker.NarrowingClass<TNarrowing>((narrowing, query) => narrowing.Narrow(query)));

    /// <summary>Narrows every non-system read of these entities by the filter <paramref name="narrow"/> answers.</summary>
    /// <param name="id">A stable id, lower-case letters, digits and hyphens.</param>
    /// <param name="narrow">The filter ANDed into the read, or <see langword="null"/> to leave it as the rules made it.</param>
    /// <returns>This builder.</returns>
    public AlvoEntityBuilder Narrow(string id, Func<QueryNarrowing, AlvoFilter?> narrow) =>
        AddRead(id, ReadExtensionKind.Narrowing, ReadInvoker.NarrowingDelegate(narrow));

    /// <summary>Shapes every row a read or a write's echo returns of these entities by a <typeparamref name="TEnricher"/>, built once per read.</summary>
    /// <typeparam name="TEnricher">The enricher class.</typeparam>
    /// <param name="id">A stable id, lower-case letters, digits and hyphens.</param>
    /// <returns>This builder.</returns>
    public AlvoEntityBuilder Enrich<TEnricher>(string id)
        where TEnricher : class, IRecordEnricher<AlvoRecord> =>
        AddRead(id, ReadExtensionKind.Enricher, ReadInvoker.EnricherClass<TEnricher>(
            (enricher, row, caller, operation) => enricher.Enrich(new Enrich<AlvoRecord>(row, caller, operation))));

    /// <summary>Shapes every row a read or a write's echo returns of these entities by <paramref name="enrich"/>.</summary>
    /// <param name="id">A stable id, lower-case letters, digits and hyphens.</param>
    /// <param name="enrich">Hides fields of, or fills virtual fields on, one returned row.</param>
    /// <returns>This builder.</returns>
    public AlvoEntityBuilder Enrich(string id, Action<Enrich<AlvoRecord>> enrich)
    {
        ArgumentNullException.ThrowIfNull(enrich);
        return AddRead(id, ReadExtensionKind.Enricher, ReadInvoker.EnricherDelegate(
            (row, caller, operation) => enrich(new Enrich<AlvoRecord>(row, caller, operation))));
    }

    private AlvoEntityBuilder AddRead(string id, ReadExtensionKind kind, ReadInvoker invoker)
    {
        ReadExtensionRegistration.Add(_services, id, _entities, kind, invoker, typed: null);

        return this;
    }
}
