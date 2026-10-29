using MMLib.Alvo.Data.Internal;

using System.Linq.Expressions;

namespace MMLib.Alvo.Data;

/// <summary>
/// Reads typed records through the data port: the entity a type's <see cref="AlvoEntityAttribute"/> names, a predicate
/// lambda translated into an <see cref="AlvoFilter"/>, and the same rules, tenancy and hidden fields as an untyped read.
/// </summary>
/// <remarks>
/// A predicate compares mapped properties with values — captured variables are read on every call and bound as
/// parameters — combined with <c>&amp;&amp;</c>, <c>||</c> and <c>!</c>, tests <c>== null</c> and <c>HasValue</c>, and
/// <c>Contains</c> over a list of values, and a text field's <c>StartsWith</c>, <c>EndsWith</c> and <c>Contains</c> of a value —
/// one-argument or <see cref="StringComparison.Ordinal"/> — which match the value literally, so a <c>%</c> or <c>_</c> in it
/// matches itself, and compare ordinally and case-sensitively, even for the one-argument <c>StartsWith</c>/<c>EndsWith</c>,
/// which C# itself compares by culture. Anything else throws <see cref="AlvoTranslationException"/> when the read is
/// called. A typed read returns one bounded page; page through more with an untyped <see cref="AlvoQuery"/>.
/// </remarks>
public static class AlvoTypedReadExtensions
{
    /// <summary>Reads one record as a <typeparamref name="T"/>.</summary>
    /// <typeparam name="T">A class marked with <see cref="AlvoEntityAttribute"/>.</typeparam>
    /// <param name="session">Data access as the session's caller.</param>
    /// <param name="id">The record's id.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <returns>The record, or <see langword="null"/> when it does not exist or the caller may not see it.</returns>
    /// <exception cref="InvalidOperationException"><typeparamref name="T"/> names no entity, or the record lacks a field it maps.</exception>
    public static async Task<T?> GetAsync<T>(this IAlvoSession session, Guid id, CancellationToken cancellationToken = default)
        where T : class
    {
        ArgumentNullException.ThrowIfNull(session);
        var record = await session.GetAsync(TypedEntity.NameOf<T>(), id, cancellationToken).ConfigureAwait(false);

        return record?.As<T>();
    }

    /// <summary>Reads at most <paramref name="limit"/> records matching <paramref name="where"/>, as <typeparamref name="T"/>.</summary>
    /// <typeparam name="T">A class marked with <see cref="AlvoEntityAttribute"/>.</typeparam>
    /// <param name="session">Data access as the session's caller.</param>
    /// <param name="where">The predicate, translated into a filter on every call.</param>
    /// <param name="limit">The most records to return, at least 1.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <returns>The matching records the caller may see.</returns>
    /// <exception cref="AlvoTranslationException"><paramref name="where"/> holds a construct with no filter equivalent.</exception>
    public static async Task<IReadOnlyList<T>> QueryAsync<T>(this IAlvoSession session, Expression<Func<T, bool>> where, int limit, CancellationToken cancellationToken = default)
        where T : class
    {
        ArgumentNullException.ThrowIfNull(session);
        var page = await session.QueryAsync(TypedEntity.Query(where, limit), cancellationToken).ConfigureAwait(false);

        return TypedEntity.Items<T>(page);
    }

    /// <summary>Reads one record as a <typeparamref name="T"/>, as <paramref name="context"/>.</summary>
    /// <typeparam name="T">A class marked with <see cref="AlvoEntityAttribute"/>.</typeparam>
    /// <param name="data">The data port.</param>
    /// <param name="id">The record's id.</param>
    /// <param name="context">The caller performing the read.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <returns>The record, or <see langword="null"/> when it does not exist or the caller may not see it.</returns>
    /// <exception cref="InvalidOperationException"><typeparamref name="T"/> names no entity, or the record lacks a field it maps.</exception>
    public static async Task<T?> GetAsync<T>(this IAlvoData data, Guid id, AlvoContext context, CancellationToken cancellationToken = default)
        where T : class
    {
        ArgumentNullException.ThrowIfNull(data);
        var record = await data.GetAsync(TypedEntity.NameOf<T>(), id, context, cancellationToken).ConfigureAwait(false);

        return record?.As<T>();
    }

    /// <summary>Reads at most <paramref name="limit"/> records matching <paramref name="where"/>, as <typeparamref name="T"/>, as <paramref name="context"/>.</summary>
    /// <typeparam name="T">A class marked with <see cref="AlvoEntityAttribute"/>.</typeparam>
    /// <param name="data">The data port.</param>
    /// <param name="where">The predicate, translated into a filter on every call.</param>
    /// <param name="limit">The most records to return, at least 1.</param>
    /// <param name="context">The caller performing the read.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <returns>The matching records the caller may see.</returns>
    /// <exception cref="AlvoTranslationException"><paramref name="where"/> holds a construct with no filter equivalent.</exception>
    public static async Task<IReadOnlyList<T>> QueryAsync<T>(this IAlvoData data, Expression<Func<T, bool>> where, int limit, AlvoContext context, CancellationToken cancellationToken = default)
        where T : class
    {
        ArgumentNullException.ThrowIfNull(data);
        var page = await data.QueryAsync(TypedEntity.Query(where, limit), context, cancellationToken).ConfigureAwait(false);

        return TypedEntity.Items<T>(page);
    }
}
