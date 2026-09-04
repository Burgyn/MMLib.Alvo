using MMLib.Alvo.Rules;

namespace MMLib.Alvo.Api.Internal;

/// <summary>
/// The endpoint metadata every generated Data API route carries: which entity it serves and which
/// <see cref="DataOperation"/> it performs.
/// </summary>
/// <remarks>
/// <para>
/// It exists so that "every generated endpoint is gated" is provable from the endpoint table rather than
/// from a list of literal paths. <c>AddEndpointFilter</c> leaves nothing in <c>Endpoint.Metadata</c>, so
/// without a marker the only evidence available was one fact per known path and verb — and a sixth
/// endpoint added later would have been caught by nothing.
/// </para>
/// <para>
/// The marker is attached by <c>DataApiEndpoints.Protect</c> in the same call that attaches the filter, so
/// an endpoint carrying one without the other is unrepresentable: a marker with no filter cannot be
/// written, and a filter with no marker cannot either. That is the property being relied on — the fact
/// asserts the marker, and the marker's presence is what the filter's presence is inferred from.
/// </para>
/// <para>
/// Internal, and deliberately not on the public surface: a host has no reason to read it, and the one
/// consumer that does — <c>AlvoDocumentTransformer</c>, which uses it to tell Alvo's endpoints from a host's
/// own when enriching the OpenAPI document — lives inside this assembly.
/// </para>
/// </remarks>
/// <param name="Entity">The entity the endpoint serves, as the applied schema names it.</param>
/// <param name="Kind">
/// Which endpoint this route is — finer than the operation it gates, because two kinds are one operation.
/// </param>
internal sealed record DataApiOperationMetadata(string Entity, DataApiEndpointKind Kind)
{
    /// <summary>The operation the endpoint performs, and the one its filter gates.</summary>
    /// <remarks>
    /// Derived rather than stored beside <see cref="Kind"/>, so a marker cannot claim a kind and an
    /// operation that disagree — which would be a route gated as something other than what it is.
    /// </remarks>
    internal DataOperation Operation => Kind.ToDataOperation();
}
