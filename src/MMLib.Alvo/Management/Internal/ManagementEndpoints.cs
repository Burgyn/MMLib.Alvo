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
        MapProjects(group);
        MapDescriptorRead(group);
        MapRevisions(group);
        MapRevision(group);

        return group;
    }

    /// <summary><c>GET {prefix}/info</c> — <see cref="IAlvoManagement.GetInfoAsync"/>.</summary>
    /// <param name="group">The group to map into.</param>
    private static void MapInfo(RouteGroupBuilder group) =>
        Gate(
            group.MapGet("/info", (IAlvoManagement management, CancellationToken ct) => management.GetInfoAsync(ct)),
            new ManagementRoute(nameof(IAlvoManagement.GetInfoAsync), ManagementOperation.GetInfo));

    /// <summary><c>GET {prefix}/projects</c> — <see cref="IAlvoManagement.ListProjectsAsync"/>.</summary>
    /// <param name="group">The group to map into.</param>
    private static void MapProjects(RouteGroupBuilder group) =>
        Gate(
            group.MapGet(
                "/projects",
                (IAlvoManagement management, CancellationToken ct) => management.ListProjectsAsync(ct)),
            new ManagementRoute(nameof(IAlvoManagement.ListProjectsAsync), ManagementOperation.ListProjects));

    /// <summary>
    /// <c>GET {prefix}/projects/{project}/descriptor</c> — <see cref="IAlvoManagement.GetDescriptorAsync"/>.
    /// </summary>
    /// <param name="group">The group to map into.</param>
    private static void MapDescriptorRead(RouteGroupBuilder group) =>
        Gate(
            group.MapGet(
                "/projects/{project}/descriptor",
                (string project, IAlvoManagement management, CancellationToken ct) =>
                    Answer(() => management.GetDescriptorAsync(project, ct))),
            new ManagementRoute(nameof(IAlvoManagement.GetDescriptorAsync), ManagementOperation.GetDescriptor));

    /// <summary>
    /// <c>GET {prefix}/projects/{project}/revisions</c> — <see cref="IAlvoManagement.ListRevisionsAsync"/>.
    /// </summary>
    /// <param name="group">The group to map into.</param>
    private static void MapRevisions(RouteGroupBuilder group) =>
        Gate(
            group.MapGet(
                "/projects/{project}/revisions",
                (string project, IAlvoManagement management, CancellationToken ct) =>
                    Answer(() => management.ListRevisionsAsync(project, ct))),
            new ManagementRoute(nameof(IAlvoManagement.ListRevisionsAsync), ManagementOperation.ListRevisions));

    /// <summary>
    /// <c>GET {prefix}/projects/{project}/revisions/{revision}</c> —
    /// <see cref="IAlvoManagement.GetRevisionAsync"/>.
    /// </summary>
    /// <remarks>
    /// The <c>:int</c> constraint is what keeps a revision number out of the delegate's hands: a segment that
    /// is not a number never matches, so nothing here parses one and nothing has to decide what
    /// <c>revisions/latest</c> would mean.
    /// </remarks>
    /// <param name="group">The group to map into.</param>
    private static void MapRevision(RouteGroupBuilder group) =>
        Gate(
            group.MapGet(
                "/projects/{project}/revisions/{revision:int}",
                (string project, int revision, IAlvoManagement management, CancellationToken ct) =>
                    Answer(() => management.GetRevisionAsync(project, revision, ct))),
            new ManagementRoute(nameof(IAlvoManagement.GetRevisionAsync), ManagementOperation.GetRevision));

    /// <summary>
    /// Runs one contract member and turns its refusals into problem documents.
    /// </summary>
    /// <remarks>
    /// Every delegate that can be refused goes through here, so no endpoint decides a status of its own and
    /// every management refusal is minted through <see cref="ProblemResultFactory"/>'s one catalogue.
    /// </remarks>
    /// <typeparam name="T">What the member answers with.</typeparam>
    /// <param name="operation">The member to run.</param>
    private static async Task<IResult> Answer<T>(Func<Task<T>> operation)
    {
        try
        {
            return Results.Ok(await operation().ConfigureAwait(false));
        }
        catch (ManagementProjectNotFoundException refusal)
        {
            return ProblemResultFactory.ManagementNotFound(refusal.Message);
        }
        catch (ManagementRevisionNotFoundException refusal)
        {
            return ProblemResultFactory.ManagementNotFound(refusal.Message);
        }
    }

    /// <summary>Attaches the caller filter, the access gate and the metadata the contract test reads.</summary>
    /// <param name="route">The route being built.</param>
    /// <param name="operation">What the route stands for.</param>
    private static RouteHandlerBuilder Gate(RouteHandlerBuilder route, ManagementRoute operation) =>
        route.AddEndpointFilter<RouteHandlerBuilder, ManagementCallerFilter>()
            .RequireAlvoManagementAccess(operation.Operation)
            .WithMetadata(operation)
            .WithName($"Alvo.Management.{operation.Member}");
}
