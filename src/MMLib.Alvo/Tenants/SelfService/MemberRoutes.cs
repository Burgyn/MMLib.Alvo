using Microsoft.AspNetCore.Http;

namespace MMLib.Alvo.Tenants.SelfService;

/// <summary>
/// A tenant's members (spec §6.3): <c>GET {prefix}/tenants/{id}/members</c> lists them to any member,
/// <c>PATCH …/members/{userId}</c> changes another member's roles or ownership (an owner's act), and
/// <c>DELETE …/members/{userId}</c> removes a member (an owner's act) or — on oneself — leaves.
/// </summary>
/// <remarks>
/// <para>
/// <b>The registry judges, in one transaction per change</b> (S3 plan D3): nobody changes their own membership (spec §14
/// AC4 — leaving is the one exception, and it is a removal), a personal tenant's membership never changes, and a shared
/// tenant never loses its last owner. Each refusal surfaces as <see cref="SelfServiceProblems.Refused"/> words it.
/// </para>
/// <para>
/// <b>A stranger learns nothing</b>: a tenant the person is no member of answers every member route exactly as an id that
/// names no tenant does.
/// </para>
/// </remarks>
internal static class MemberRoutes
{
    /// <summary><c>GET {prefix}/tenants/{tenantId}/members</c>: the tenant's members, to one of them.</summary>
    /// <param name="http">The request.</param>
    /// <param name="tenantId">The tenant, from the route.</param>
    internal static async Task<IResult> ListAsync(HttpContext http, Guid tenantId)
    {
        var (call, refusal) = await SelfServiceCall.EnterAsync(http, SelfServiceProblems.SelfServiceOff).ConfigureAwait(false);
        if (call is null)
        {
            return refusal!;
        }

        var tenant = new TenantId(tenantId);
        var own = await call.MembershipAsync(tenant).ConfigureAwait(false);
        var members = own is null ? null : await call.Store.ListMembersAsync(tenant, call.Person, call.Ct).ConfigureAwait(false);
        return members is null
            ? SelfServiceProblems.NoSuchTenant()
            : SelfServiceCall.Json(members.Select(member => Response(member, own!.Kind, call.Policy)).ToList());
    }

    /// <summary><c>PATCH {prefix}/tenants/{tenantId}/members/{userId}</c>: another member's roles or ownership.</summary>
    /// <param name="http">The request.</param>
    /// <param name="tenantId">The tenant, from the route.</param>
    /// <param name="userId">The member, from the route.</param>
    internal static async Task<IResult> UpdateAsync(HttpContext http, Guid tenantId, Guid userId)
    {
        var (call, refusal) = await SelfServiceCall.EnterAsync(http, SelfServiceProblems.SelfServiceOff).ConfigureAwait(false);
        if (call is null)
        {
            return refusal!;
        }

        var (patch, invalid) = await call.ReadAsync(
            (request, options, ct) => MemberPatch.ReadAsync(request, options, call.Policy, ct)).ConfigureAwait(false);
        if (patch is null)
        {
            return invalid!;
        }

        var (tenant, member) = (new TenantId(tenantId), new UserId(userId));
        if (await call.MembershipAsync(tenant).ConfigureAwait(false) is null)
        {
            return SelfServiceProblems.NoSuchTenant();
        }

        var update = new MemberUpdate(tenant, call.Person, member, patch.Roles, patch.IsOwner, call.Now);
        var status = await call.Store.UpdateMemberAsync(update, call.Ct).ConfigureAwait(false);
        return status == MemberChangeStatus.Changed
            ? await MemberAsync(call, tenant, member).ConfigureAwait(false)
            : SelfServiceProblems.Refused(status);
    }

    /// <summary><c>DELETE {prefix}/tenants/{tenantId}/members/{userId}</c>: removes a member, or leaves.</summary>
    /// <param name="http">The request.</param>
    /// <param name="tenantId">The tenant, from the route.</param>
    /// <param name="userId">The member, from the route — the person themself to leave.</param>
    internal static async Task<IResult> RemoveAsync(HttpContext http, Guid tenantId, Guid userId)
    {
        var (call, refusal) = await SelfServiceCall.EnterAsync(http, SelfServiceProblems.SelfServiceOff).ConfigureAwait(false);
        if (call is null)
        {
            return refusal!;
        }

        var tenant = new TenantId(tenantId);
        if (await call.MembershipAsync(tenant).ConfigureAwait(false) is null)
        {
            return SelfServiceProblems.NoSuchTenant();
        }

        var removal = new MemberRemoval(tenant, call.Person, new UserId(userId), call.Now);
        var status = await call.Store.RemoveMemberAsync(removal, call.Ct).ConfigureAwait(false);
        return status == MemberChangeStatus.Changed ? Results.NoContent() : SelfServiceProblems.Refused(status);
    }

    private static async Task<IResult> MemberAsync(SelfServiceCall call, TenantId tenant, UserId member)
    {
        var members = await call.Store.ListMembersAsync(tenant, call.Person, call.Ct).ConfigureAwait(false);
        return members?.FirstOrDefault(view => view.User == member) is { } changed
            ? SelfServiceCall.Json(Response(changed, TenantKind.Shared, call.Policy))
            : SelfServiceProblems.NoSuchTenant();
    }

    private static MemberResponse Response(MemberView member, TenantKind kind, SelfServicePolicy policy) =>
        MemberResponse.From(member, SelfServiceMemberships.Roles(kind, member.Roles, policy));
}
