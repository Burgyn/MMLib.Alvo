using MMLib.Alvo.Rules;

namespace MMLib.Alvo.Api;

/// <summary>
/// Endpoint metadata on every route the Data API generates: the entity it serves, the operation its authorization gates
/// it as, and which route it is.
/// </summary>
/// <remarks>
/// Read it from <c>Endpoint.Metadata</c> in a convention, a middleware or a rate-limiting partition to tell Alvo's routes
/// apart by entity — <c>ForEntity</c> is built on it. It is set by Alvo in the same call that attaches the authorization
/// filter, so a route carrying it is a route Alvo gates.
/// </remarks>
/// <param name="Entity">The entity the route serves, as the applied descriptor names it.</param>
/// <param name="Operation">The operation the route is authorized as.</param>
/// <param name="Kind">Which generated route it is.</param>
public sealed record AlvoEndpointMetadata(string Entity, DataOperation Operation, AlvoEndpointKind Kind);
