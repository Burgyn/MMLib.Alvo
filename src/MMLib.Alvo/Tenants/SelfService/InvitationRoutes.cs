using Microsoft.AspNetCore.Http;

namespace MMLib.Alvo.Tenants.SelfService;

/// <summary>
/// A tenant's invitations (spec §6.3, S4 plan D9–D11): an owner creates one at <c>POST {prefix}/tenants/{id}/invitations</c>
/// — the answer carries its code, once — lists them at <c>GET</c> on the same path, and revokes one at
/// <c>DELETE …/invitations/{invitationId}</c>.
/// </summary>
/// <remarks>
/// <para>
/// <b>The code is minted here and only its hash goes down</b> (D2): the registry stores what it is given and never sees a
/// code, so no statement, log or refusal below this line can carry one.
/// </para>
/// <para>
/// <b>The registry judges ownership in the creation's own transaction</b> (D6): a creation racing its creator's demotion
/// either sees a non-owner or is swept by the demotion. The membership read before it is the S3 routes' stranger gate — a
/// tenant the person is no honoured member of answers as one that does not exist.
/// </para>
/// </remarks>
internal static class InvitationRoutes
{
    /// <summary><c>POST {prefix}/tenants/{tenantId}/invitations</c>: a new invitation, answered with its code.</summary>
    /// <param name="http">The request.</param>
    /// <param name="tenantId">The tenant, from the route.</param>
    internal static async Task<IResult> CreateAsync(HttpContext http, Guid tenantId)
    {
        var (call, refusal) = await SelfServiceCall.EnterAsync(http, SelfServiceProblems.InvitationsOff).ConfigureAwait(false);
        if (call is null)
        {
            return refusal!;
        }

        var (request, invalid) = await call.ReadAsync(
            (body, options, ct) => InvitationRequest.ReadAsync(body, options, call.Policy, call.Now, ct)).ConfigureAwait(false);
        if (request is null)
        {
            return invalid!;
        }

        var tenant = new TenantId(tenantId);
        if (await call.MembershipAsync(tenant).ConfigureAwait(false) is null)
        {
            return SelfServiceProblems.NoSuchTenant();
        }

        return await CreatedAsync(call, tenant, request).ConfigureAwait(false);
    }

    /// <summary><c>GET {prefix}/tenants/{tenantId}/invitations</c>: every invitation of the tenant, newest first, to an owner.</summary>
    /// <param name="http">The request.</param>
    /// <param name="tenantId">The tenant, from the route.</param>
    internal static async Task<IResult> ListAsync(HttpContext http, Guid tenantId)
    {
        var (call, refusal) = await SelfServiceCall.EnterAsync(http, SelfServiceProblems.InvitationsOff).ConfigureAwait(false);
        if (call is null)
        {
            return refusal!;
        }

        var tenant = new TenantId(tenantId);
        var own = await call.MembershipAsync(tenant).ConfigureAwait(false);
        if (OwnerRefusal(own) is { } refused)
        {
            return refused;
        }

        var invitations = await call.Store.ListInvitationsAsync(tenant, call.Ct).ConfigureAwait(false);
        var now = call.Now;
        return SelfServiceCall.Json(invitations.Select(invitation => InvitationResponse.From(invitation, call.Policy, now)).ToList());
    }

    /// <summary><c>DELETE {prefix}/tenants/{tenantId}/invitations/{invitationId}</c>: revokes one, idempotently.</summary>
    /// <param name="http">The request.</param>
    /// <param name="tenantId">The tenant, from the route.</param>
    /// <param name="invitationId">The invitation, from the route.</param>
    internal static async Task<IResult> RevokeAsync(HttpContext http, Guid tenantId, Guid invitationId)
    {
        var (call, refusal) = await SelfServiceCall.EnterAsync(http, SelfServiceProblems.InvitationsOff).ConfigureAwait(false);
        if (call is null)
        {
            return refusal!;
        }

        var tenant = new TenantId(tenantId);
        if (await call.MembershipAsync(tenant).ConfigureAwait(false) is null)
        {
            return SelfServiceProblems.NoSuchTenant();
        }

        var revocation = new InvitationRevocation(tenant, call.Person, invitationId, call.Now);
        var status = await call.Store.RevokeInvitationAsync(revocation, call.Ct).ConfigureAwait(false);
        return status == MemberChangeStatus.Changed ? Results.NoContent() : SelfServiceProblems.Refused(status);
    }

    private static async Task<IResult> CreatedAsync(SelfServiceCall call, TenantId tenant, InvitationRequest request)
    {
        var code = InvitationCode.New();
        var now = call.Now;
        var creation = new CreateInvitationRequest(
            Guid.NewGuid(), tenant, call.Person, code.Hashed, request.Roles, request.IsOwner, request.Email, request.MaxUses,
            request.ExpiresAt, now);
        var outcome = await call.Store.CreateInvitationAsync(creation, call.Ct).ConfigureAwait(false);
        return outcome.Status == MemberChangeStatus.Changed
            ? SelfServiceCall.Json(
                InvitationResponse.From(outcome.Invitation!, call.Policy, now, code.Value), StatusCodes.Status201Created)
            : SelfServiceProblems.Refused(outcome.Status);
    }

    /// <summary>
    /// The refusal for listing a tenant's invitations as <paramref name="own"/>: none for an owner of a shared tenant; the
    /// stranger's 404, the personal tenant's 409 and the non-owner's 403 otherwise — the registry's own order on a write.
    /// </summary>
    private static IResult? OwnerRefusal(StoredMembership? own) => own switch
    {
        null => SelfServiceProblems.NoSuchTenant(),
        { Kind: TenantKind.Personal } => SelfServiceProblems.Refused(MemberChangeStatus.PersonalTenant),
        { IsOwner: false } => SelfServiceProblems.Refused(MemberChangeStatus.NotOwner),
        _ => null,
    };
}
