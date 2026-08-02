using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Routing;

namespace MMLib.Alvo.Host.Internal;

/// <summary>The endpoints the host itself owns, as opposed to the ones the descriptor generates.</summary>
internal static class AlvoHostEndpoints
{
    /// <summary>
    /// Maps liveness. Unauthenticated by construction — a container probe presents no credential, and only
    /// <c>MapAlvoDataApi</c>'s endpoints carry the API-key filter.
    /// </summary>
    /// <remarks>
    /// <b>Answering at all proves the descriptor applied</b>, because <see cref="AlvoHost.BuildAsync"/>
    /// applies before the server ever listens: a host whose apply failed never reaches this route. That is
    /// what lets <c>docker compose up --wait</c> mean "the backend is up", not "a process is running".
    /// Readiness with database / cache / bus reachability (§2.12) is F4's — see <c>docs/architecture/host.md</c>.
    /// </remarks>
    internal static void MapAlvoLiveness(this IEndpointRouteBuilder endpoints) =>
        endpoints.MapHealthChecks(AlvoHost.LivenessPath);
}
