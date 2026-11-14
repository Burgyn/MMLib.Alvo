using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using MMLib.Alvo.Api.Internal;

namespace MMLib.Alvo.Tenants.SelfService;

/// <summary>
/// A tenant's invitations (spec §6.3, S4 plan D8–D11): an owner creates one at <c>POST {prefix}/tenants/{id}/invitations</c>
/// — the answer carries its code, once — lists them at <c>GET</c> on the same path, and revokes one at
/// <c>DELETE …/invitations/{invitationId}</c>; anyone holding a code previews it at <c>POST {prefix}/invitations/preview</c>,
/// and a signed-in person accepts it at <c>POST {prefix}/invitations/accept</c>.
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
/// <para>
/// <b>Every dead code is one answer</b> (D3): unknown, malformed, expired, revoked, used up, bound to another or an untrusted
/// email, into a personal tenant, or from a creator who is no longer an owner — preview and accept answer the S3 routes'
/// <c>not-found</c>, byte for byte. A person who already is a member of the code's tenant is answered their membership
/// instead, whatever the code's state (the <c>AlreadyMember</c> ruling): that tells them nothing they do not know.
/// </para>
/// </remarks>
internal static class InvitationRoutes
{
    private const string InvalidToken = "invalid_token";

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

    /// <summary>
    /// <c>POST {prefix}/invitations/preview</c>: what a live invitation offers, to anyone holding its code — no credential is
    /// read (D8), since under <c>invitationOnly</c> the invitee has no account yet.
    /// </summary>
    /// <param name="http">The request.</param>
    internal static async Task<IResult> PreviewAsync(HttpContext http)
    {
        ArgumentNullException.ThrowIfNull(http);
        var policy = SelfServiceCall.PolicyOf(http);
        if (SelfServiceProblems.InvitationsOff(policy) is { } disabled)
        {
            return disabled;
        }

        var (body, invalid) = await SelfServiceCall.ReadAsync<InvitationCodeBody>(http, InvitationCodeBody.ReadAsync).ConfigureAwait(false);
        if (body is null)
        {
            return invalid!;
        }

        var services = http.RequestServices;
        var now = services.GetRequiredService<TimeProvider>().GetUtcNow();
        var preview = await services.GetRequiredService<ITenantStore>()
            .PreviewInvitationAsync(body.CodeHash, now, http.RequestAborted).ConfigureAwait(false);
        return preview is null ? Dead() : SelfServiceCall.Json(InvitationPreviewResponse.From(preview, policy));
    }

    /// <summary><c>POST {prefix}/invitations/accept</c>: the person joins the invitation's tenant, or already is a member.</summary>
    /// <param name="http">The request.</param>
    /// <exception cref="InvalidOperationException">The registry answered a status this route does not know.</exception>
    internal static async Task<IResult> AcceptAsync(HttpContext http)
    {
        var (call, refusal) = await SelfServiceCall.EnterAsync(http, SelfServiceProblems.InvitationsOff).ConfigureAwait(false);
        if (call is null)
        {
            return refusal!;
        }

        var (body, invalid) = await call.ReadAsync<InvitationCodeBody>(InvitationCodeBody.ReadAsync).ConfigureAwait(false);
        if (body is null)
        {
            return invalid!;
        }

        var outcome = await call.Store.AcceptInvitationAsync(
            new AcceptInvitationRequest(body.CodeHash, call.Person, call.Now), call.Ct).ConfigureAwait(false);
        return outcome.Status switch
        {
            AcceptInvitationStatus.Joined or AcceptInvitationStatus.AlreadyMember => Membership(call, outcome.Membership!),
            AcceptInvitationStatus.Dead => Dead(),
            AcceptInvitationStatus.NoAccount => ProblemResultFactory.BearerUnauthenticated(InvalidToken),
            _ => throw new InvalidOperationException(
                $"The tenant registry answered an acceptance with {outcome.Status}, which this route does not know."),
        };
    }

    private static IResult Membership(SelfServiceCall call, StoredMembership membership) =>
        MembershipRules.Honours(membership, call.Policy)
            ? SelfServiceCall.Json(SelfServiceMemberships.Response(membership, call.Policy))
            : Dead();

    /// <summary>The one answer for every dead code (D3): the S3 routes' <c>not-found</c>, byte for byte.</summary>
    private static IResult Dead() => SelfServiceProblems.NoSuchTenant();

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
