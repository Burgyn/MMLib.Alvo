using MMLib.Alvo.Data;

namespace MMLib.Alvo.Reads;

/// <summary>
/// The untyped query narrowing: an <see cref="AlvoFilter"/> ANDed into the SQL of every non-system list, get and count of
/// the entities it is registered for. It can only remove rows from what the rules allow, and writes are never narrowed.
/// </summary>
public interface IQueryNarrowing
{
    /// <summary>The rows this caller may see at most, or <see langword="null"/> to leave the read as the rules made it.</summary>
    /// <param name="query">The read being narrowed.</param>
    /// <returns>The filter ANDed into the read, or <see langword="null"/>.</returns>
    AlvoFilter? Narrow(QueryNarrowing query);
}
