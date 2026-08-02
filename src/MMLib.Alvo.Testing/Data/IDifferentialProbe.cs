using MMLib.Alvo.Data;
using MMLib.Alvo.Expressions;

namespace MMLib.Alvo.Testing.Data;

/// <summary>
/// A real database that can be asked one question: does this engine's own <c>WHERE</c> evaluation admit this
/// row under this rendered predicate? Implemented per engine, so the differential suite compares an actual
/// engine's three-valued logic against the in-memory backend rather than a model of it.
/// </summary>
public interface IDifferentialProbe : IAsyncDisposable
{
    /// <summary>
    /// Replaces the table's contents with <paramref name="row"/> alone and answers whether
    /// <paramref name="predicate"/> selects it.
    /// </summary>
    /// <param name="row">The single candidate row. Any previously stored row is removed first.</param>
    /// <param name="predicate">The rendered predicate to use as the whole <c>WHERE</c> clause.</param>
    /// <returns><see langword="true"/> when the engine's own evaluation admits the row.</returns>
    Task<bool> MatchesAsync(AlvoRecord row, SqlPredicate predicate);
}
