using Microsoft.AspNetCore.Builder;

namespace MMLib.Alvo.Api.Internal;

/// <summary>The conventions a <c>ForEntity</c> call collected, replayed on the endpoints of its one entity.</summary>
/// <param name="entity">The entity whose generated routes the conventions apply to.</param>
internal sealed class EntityConventions(string entity) : IEndpointConventionBuilder
{
    private readonly List<Action<EndpointBuilder>> _conventions = [];
    private readonly List<Action<EndpointBuilder>> _finally = [];

    /// <summary>Gets a value indicating whether a finally convention was collected.</summary>
    internal bool HasFinally => _finally.Count > 0;

    /// <inheritdoc/>
    public void Add(Action<EndpointBuilder> convention)
    {
        ArgumentNullException.ThrowIfNull(convention);
        _conventions.Add(convention);
    }

    /// <inheritdoc/>
    public void Finally(Action<EndpointBuilder> finallyConvention)
    {
        ArgumentNullException.ThrowIfNull(finallyConvention);
        _finally.Add(finallyConvention);
    }

    /// <summary>Runs the ordinary conventions on <paramref name="endpoint"/> when it is one of the entity's routes.</summary>
    internal void Apply(EndpointBuilder endpoint) => Replay(_conventions, endpoint);

    /// <summary>Runs the finally conventions on <paramref name="endpoint"/> when it is one of the entity's routes.</summary>
    internal void ApplyFinally(EndpointBuilder endpoint) => Replay(_finally, endpoint);

    private void Replay(List<Action<EndpointBuilder>> conventions, EndpointBuilder endpoint)
    {
        if (!Serves(endpoint))
        {
            return;
        }

        foreach (var convention in conventions)
        {
            convention(endpoint);
        }
    }

    private bool Serves(EndpointBuilder endpoint) =>
        endpoint.Metadata.OfType<AlvoEndpointMetadata>().Any(metadata => string.Equals(metadata.Entity, entity, StringComparison.Ordinal));
}
