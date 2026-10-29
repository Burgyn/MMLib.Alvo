using MMLib.Alvo.Schema;

namespace MMLib.Alvo.Data;

/// <summary>
/// A session that names the applied schema of the entities it writes — how a typed create or replace leaves out the fields
/// the store maintains itself, <c>computed</c> and <c>rollup</c> (Ruling 52).
/// </summary>
/// <remarks>
/// Internal and in Abstractions because the core's typed writes ask it of a session the EF driver may have made (a unit's
/// <see cref="IAlvoTransaction"/>), and neither may reference the other. Implemented by Alvo's own sessions; a session that
/// does not implement it — a host's fake — gets every mapped field sent, and the port refuses a computed or rollup one.
/// </remarks>
internal interface IAlvoSessionSchema
{
    /// <summary>The applied schema's entity named <paramref name="entity"/>, or <see langword="null"/> when none is applied.</summary>
    /// <param name="entity">The entity name.</param>
    /// <returns>The entity, or <see langword="null"/>.</returns>
    EntitySchema? EntityOf(string entity);
}
