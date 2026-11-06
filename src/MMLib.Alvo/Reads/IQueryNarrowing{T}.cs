using MMLib.Alvo.Data;

using System.Linq.Expressions;

namespace MMLib.Alvo.Reads;

/// <summary>
/// Narrows what a non-system caller reads of the entity <typeparamref name="T"/> maps: the predicate is ANDed into the SQL
/// of list, get and count. It can only remove rows from what the rules allow, and writes are never narrowed.
/// </summary>
/// <typeparam name="T">A class marked with <c>[AlvoEntity("…")]</c>.</typeparam>
public interface IQueryNarrowing<T>
    where T : class
{
    /// <summary>The rows this caller may see at most, or <see langword="null"/> to leave the read as the rules made it.</summary>
    /// <param name="query">The read being narrowed.</param>
    /// <returns>A predicate translated to an <see cref="AlvoFilter"/> on every call, or <see langword="null"/>.</returns>
    Expression<Func<T, bool>>? Narrow(QueryNarrowing query);
}
