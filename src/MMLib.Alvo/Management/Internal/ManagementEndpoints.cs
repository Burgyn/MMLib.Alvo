using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using MMLib.Alvo.Api.Internal;

namespace MMLib.Alvo.Management.Internal;

/// <summary>
/// One minimal-API delegate per <see cref="IAlvoManagement"/> member, mapped under the configured prefix.
/// </summary>
/// <remarks>
/// <para>
/// <b>Every delegate is a thin adapter over the same service the dashboard calls in-process</b> — it binds,
/// calls one member, and renders. No business rule lives here, which is what keeps the two transports on one
/// path.
/// </para>
/// <para>
/// <b>Every route carries the same two filters, in this order:</b> <see cref="ManagementCallerFilter"/>
/// resolves the presented credential and publishes the caller, and
/// <c>RequireAlvoManagementAccess</c> refuses unless the descriptor's <c>access</c> block admits them to the
/// operation. The order is asserted rather than assumed — the gate reads what the first filter published,
/// so a reversed pair would judge every caller as anonymous.
/// </para>
/// <para>
/// <b>The routes are excluded from the OpenAPI document</b> (<c>ExcludeFromDescription</c>), deliberately
/// and temporarily: the document Alvo publishes is the <em>generated</em> Data API contract, pinned by
/// <c>OpenApiDocumentTests.The_document_is_stable</c>, linted by <c>scripts/lint-api</c> and pinned again by
/// the TeaPie e2e suite as a path-set equality. Mixing a hand-written admin surface into it would move all
/// three for a reason that has nothing to do with the Data API. A document of its own is the follow-on.
/// </para>
/// </remarks>
internal static class ManagementEndpoints
{
    /// <summary>Maps every management route under <paramref name="options"/>' prefix.</summary>
    /// <param name="endpoints">The endpoint route builder to map onto.</param>
    /// <param name="options">The management options the prefix is read from.</param>
    /// <returns>The group the routes were mapped into.</returns>
    internal static RouteGroupBuilder Map(IEndpointRouteBuilder endpoints, AlvoManagementOptions options)
    {
        var group = endpoints.MapGroup(RoutePrefix.Normalize(options.RoutePrefix));
        group.ExcludeFromDescription();

        MapInfo(group);

        return group;
    }

    /// <summary><c>GET {prefix}/info</c> — <see cref="IAlvoManagement.GetInfoAsync"/>.</summary>
    /// <param name="group">The group to map into.</param>
    private static void MapInfo(RouteGroupBuilder group) =>
        Gate(
            group.MapGet("/info", (IAlvoManagement management, CancellationToken ct) => management.GetInfoAsync(ct)),
            new ManagementRoute(nameof(IAlvoManagement.GetInfoAsync), ManagementOperation.GetInfo));

    /// <summary>Attaches the caller filter, the access gate and the metadata the contract test reads.</summary>
    /// <param name="route">The route being built.</param>
    /// <param name="operation">What the route stands for.</param>
    private static RouteHandlerBuilder Gate(RouteHandlerBuilder route, ManagementRoute operation) =>
        route.AddEndpointFilter<RouteHandlerBuilder, ManagementCallerFilter>()
            .RequireAlvoManagementAccess(operation.Operation)
            .WithMetadata(operation)
            .WithName($"Alvo.Management.{operation.Member}");
}
