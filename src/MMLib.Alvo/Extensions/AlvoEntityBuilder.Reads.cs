using MMLib.Alvo.Data;
using MMLib.Alvo.Reads;
using MMLib.Alvo.Reads.Internal;

namespace MMLib.Alvo.Extensions;

/// <summary>The read extensions of the named entities.</summary>
/// <remarks>
/// A narrowing's filter is ANDed into the SQL of every list, get and count a caller other than the system makes, beside
/// the rules and the tenant scope: it can only remove rows, never add one, and writes are never narrowed. It runs inside the
/// read — possibly inside a unit's transaction — so a narrowing type that takes a network or data-port dependency refuses
/// the start; one that throws, or answers a filter the entity cannot hold, fails the read.
/// </remarks>
public sealed partial class AlvoEntityBuilder
{
    /// <summary>Narrows every non-system read of these entities by a <typeparamref name="TNarrowing"/>, built per read.</summary>
    /// <typeparam name="TNarrowing">The narrowing class.</typeparam>
    /// <param name="id">A stable id, lower-case letters, digits and hyphens.</param>
    /// <returns>This builder.</returns>
    public AlvoEntityBuilder Narrow<TNarrowing>(string id)
        where TNarrowing : class, IQueryNarrowing =>
        AddRead(id, ReadInvoker.NarrowingClass<TNarrowing>((narrowing, query) => narrowing.Narrow(query)));

    /// <summary>Narrows every non-system read of these entities by the filter <paramref name="narrow"/> answers.</summary>
    /// <param name="id">A stable id, lower-case letters, digits and hyphens.</param>
    /// <param name="narrow">The filter ANDed into the read, or <see langword="null"/> to leave it as the rules made it.</param>
    /// <returns>This builder.</returns>
    public AlvoEntityBuilder Narrow(string id, Func<QueryNarrowing, AlvoFilter?> narrow) =>
        AddRead(id, ReadInvoker.NarrowingDelegate(narrow));

    private AlvoEntityBuilder AddRead(string id, ReadInvoker invoker)
    {
        ReadExtensionRegistration.Add(_services, id, _entities, ReadExtensionKind.Narrowing, invoker, typed: null);

        return this;
    }
}
