using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using MMLib.Alvo.Api.Internal;

namespace MMLib.Alvo.Tenants.SelfService;

/// <summary>
/// Maps the self-service routes (spec §6.3) under a sign-in group: <c>{prefix}/tenants…</c>, tagged <see cref="Tag"/>, and
/// <c>{prefix}/invitations/…</c>, tagged <see cref="InvitationsTag"/>.
/// </summary>
/// <remarks>
/// <para>
/// <b>In the core, mapped by the sign-in package</b> (S3 plan D1): every route needs a person from a session, which exists
/// only where sign-in is registered, and the auth prefix keeps <c>tenants</c> from colliding with an entity of that name
/// under the Data API's prefix. A host that federates its own identity later maps the same routes.
/// </para>
/// <para>
/// <b>Always mapped</b>, whatever the descriptor says: with the feature off a route answers 404 <c>feature-disabled</c>
/// naming the descriptor key that enables it, so an agent that found it in the OpenAPI document learns what to declare.
/// </para>
/// <para>
/// <b>Every route carries the core's CORS filter first, then <c>Cache-Control: no-store</c></b> — each answer is about a
/// person — so a refusal is readable from an allowed origin and never cached; creating a tenant is charged the
/// <see cref="AlvoRateLimit.Create"/> budget after both, and every invitation route the one
/// <see cref="AlvoRateLimit.Invitations"/> budget (S4 plan D13).
/// </para>
/// </remarks>
internal static class SelfServiceEndpoints
{
    /// <summary>The OpenAPI tag every self-service route carries.</summary>
    internal const string Tag = "tenants";

    /// <summary>The operation id of <c>GET {prefix}/tenants</c>.</summary>
    internal const string ListOperationId = "tenants.list";

    /// <summary>The operation id of <c>POST {prefix}/tenants</c>.</summary>
    internal const string CreateOperationId = "tenants.create";

    /// <summary>The operation id of <c>PATCH {prefix}/tenants/{tenantId}</c>.</summary>
    internal const string RenameOperationId = "tenants.rename";

    /// <summary>The operation id of <c>GET {prefix}/tenants/{tenantId}/members</c>.</summary>
    internal const string ListMembersOperationId = "tenants.listMembers";

    /// <summary>The operation id of <c>PATCH {prefix}/tenants/{tenantId}/members/{userId}</c>.</summary>
    internal const string UpdateMemberOperationId = "tenants.updateMember";

    /// <summary>The operation id of <c>DELETE {prefix}/tenants/{tenantId}/members/{userId}</c>.</summary>
    internal const string RemoveMemberOperationId = "tenants.removeMember";

    /// <summary>The OpenAPI tag the two routes that take an invitation code carry.</summary>
    internal const string InvitationsTag = "invitations";

    /// <summary>The operation id of <c>POST {prefix}/invitations/preview</c>.</summary>
    internal const string PreviewInvitationOperationId = "invitations.preview";

    /// <summary>The operation id of <c>POST {prefix}/invitations/accept</c>.</summary>
    internal const string AcceptInvitationOperationId = "invitations.accept";

    /// <summary>The operation id of <c>POST {prefix}/tenants/{tenantId}/invitations</c>.</summary>
    internal const string CreateInvitationOperationId = "tenants.createInvitation";

    /// <summary>The operation id of <c>GET {prefix}/tenants/{tenantId}/invitations</c>.</summary>
    internal const string ListInvitationsOperationId = "tenants.listInvitations";

    /// <summary>The operation id of <c>DELETE {prefix}/tenants/{tenantId}/invitations/{invitationId}</c>.</summary>
    internal const string RevokeInvitationOperationId = "tenants.revokeInvitation";

    private const string Collection = "/tenants";

    private const string Tenant = Collection + "/{tenantId:guid}";

    private const string Members = Tenant + "/members";

    private const string Member = Members + "/{userId:guid}";

    private const string Invitations = Tenant + "/invitations";

    private const string Invitation = Invitations + "/{invitationId:guid}";

    private const string Preview = "/invitations/preview";

    private const string Accept = "/invitations/accept";

    /// <summary>Maps the self-service routes into <paramref name="group"/>.</summary>
    /// <param name="group">The sign-in group, mapped at the auth prefix.</param>
    /// <param name="cors">The CORS filter each route carries first.</param>
    /// <param name="limits">
    /// The per-client budgets: creating a tenant is charged <see cref="AlvoRateLimit.Create"/>, every invitation route
    /// <see cref="AlvoRateLimit.Invitations"/>.
    /// </param>
    internal static void Map(RouteGroupBuilder group, AlvoCors cors, AlvoRateLimits limits)
    {
        ArgumentNullException.ThrowIfNull(group);
        ArgumentNullException.ThrowIfNull(cors);
        ArgumentNullException.ThrowIfNull(limits);
        Protect(group.MapGet(Collection, (Func<HttpContext, Task<IResult>>)TenantRoutes.ListAsync), cors, ListOperationId);
        Protect(group.MapPost(Collection, (Func<HttpContext, Task<IResult>>)TenantRoutes.CreateAsync), cors, CreateOperationId)
            .AddEndpointFilter(limits.For(AlvoRateLimit.Create));
        Protect(
            group.MapPatch(Tenant, static (HttpContext http, Guid tenantId) => TenantRoutes.RenameAsync(http, tenantId)),
            cors,
            RenameOperationId);
        MapMembers(group, cors);
        MapInvitations(group, cors, limits.For(AlvoRateLimit.Invitations));
    }

    private static void MapMembers(RouteGroupBuilder group, AlvoCors cors)
    {
        Protect(
            group.MapGet(Members, static (HttpContext http, Guid tenantId) => MemberRoutes.ListAsync(http, tenantId)),
            cors,
            ListMembersOperationId);
        Protect(
            group.MapPatch(Member, static (HttpContext http, Guid tenantId, Guid userId) => MemberRoutes.UpdateAsync(http, tenantId, userId)),
            cors,
            UpdateMemberOperationId);
        Protect(
            group.MapDelete(Member, static (HttpContext http, Guid tenantId, Guid userId) => MemberRoutes.RemoveAsync(http, tenantId, userId)),
            cors,
            RemoveMemberOperationId);
    }

    /// <summary>The five invitation routes, each charged the one <see cref="AlvoRateLimit.Invitations"/> budget (S4 plan D13).</summary>
    private static void MapInvitations(RouteGroupBuilder group, AlvoCors cors, IEndpointFilter budget)
    {
        Protect(
            group.MapPost(Invitations, static (HttpContext http, Guid tenantId) => InvitationRoutes.CreateAsync(http, tenantId)),
            cors,
            CreateInvitationOperationId).AddEndpointFilter(budget);
        Protect(
            group.MapGet(Invitations, static (HttpContext http, Guid tenantId) => InvitationRoutes.ListAsync(http, tenantId)),
            cors,
            ListInvitationsOperationId).AddEndpointFilter(budget);
        Protect(
            group.MapDelete(
                Invitation,
                static (HttpContext http, Guid tenantId, Guid invitationId) => InvitationRoutes.RevokeAsync(http, tenantId, invitationId)),
            cors,
            RevokeInvitationOperationId).AddEndpointFilter(budget);
        Protect(group.MapPost(Preview, (Func<HttpContext, Task<IResult>>)InvitationRoutes.PreviewAsync), cors, PreviewInvitationOperationId, InvitationsTag)
            .AddEndpointFilter(budget);
        Protect(group.MapPost(Accept, (Func<HttpContext, Task<IResult>>)InvitationRoutes.AcceptAsync), cors, AcceptInvitationOperationId, InvitationsTag)
            .AddEndpointFilter(budget);
    }

    /// <summary>CORS first, then <c>no-store</c>; then the tag and the operation id.</summary>
    private static RouteHandlerBuilder Protect(RouteHandlerBuilder route, AlvoCors cors, string operationId, string tag = Tag) =>
        route.AddEndpointFilter(cors.Filter).AddEndpointFilter(NoStoreResponseFilter.Instance).WithTags(tag).WithName(operationId);
}
