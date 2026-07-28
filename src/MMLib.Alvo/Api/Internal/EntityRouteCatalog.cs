using MMLib.Alvo.Schema;

namespace MMLib.Alvo.Api.Internal;

/// <summary>
/// The single authority on which entities get routes: the applied schema, read from
/// <see cref="ISchemaRegistry"/>, and nothing else.
/// </summary>
/// <remarks>
/// <para>
/// It exists as a named service rather than as a line inside the mapping because two things must
/// answer this identically — the route generation here and the OpenAPI document that has to list
/// exactly the routes that were mapped. A second enumeration is how a document comes to advertise a
/// path that does not exist, or miss one that does.
/// </para>
/// <para>
/// <b>The read is live, and mapping happens once.</b> A host maps routes at startup, so the applied
/// schema must already be in place by then — <see cref="AlvoApiOptions"/>' own seam,
/// <c>MapAlvoDataApi</c>, documents that ordering. A descriptor applied <em>later</em> at runtime
/// (<c>RuntimeSchemaService</c>) changes policy and validation immediately but cannot add a route
/// literal to an already-built endpoint table; F7's dynamic entities will need an endpoint data source
/// that can change, which is deliberately not built here.
/// </para>
/// </remarks>
/// <param name="schema">The applied schema registry — the same instance the policy catalog serves, by construction.</param>
internal sealed class EntityRouteCatalog(ISchemaRegistry schema)
{
    /// <summary>Every entity the applied descriptor declares, in the schema's own order.</summary>
    internal IReadOnlyList<EntitySchema> Entities => schema.GetSchema().Entities;
}
