using Microsoft.AspNetCore.Http;
using MMLib.Alvo.Auth;
using MMLib.Alvo.Auth.Internal;
using MMLib.Alvo.Tenants.SelfService;

namespace MMLib.Alvo.Accounts.Internal;

/// <summary>
/// The person resolver (spec §5.7): turns an Alvo access token presented as <c>Authorization: Bearer</c> into the caller
/// it names. Registered under <see cref="CallerResolution.PersonResolverKey"/>, which is what opens the Bearer channel on
/// the Data API and the commands.
/// </summary>
/// <remarks>
/// <para>
/// <b>The order is the spec's, and it is the cost model.</b> The token is validated in memory first; then the requested
/// tenant is read and planned by <see cref="MembershipRules.Plan"/> — a tenant no membership can answer is refused with no
/// I/O (S2 D13); only then is the session read, once, with the membership the plan asks for in the same statement (S3
/// plan D4, spec §14 AC2), and <see cref="MembershipRules.Decide"/> says what it means. Nothing is cached across requests:
/// a removed member, a demoted one, or a role dropped from <c>assignableRoles</c> counts on the next request. Every
/// refusal is the same <see langword="null"/>, which the caller answers with one 401 <c>invalid_token</c> — a tenant the
/// person is not in reads exactly like one that does not exist.
/// </para>
/// <para>
/// <b>A requested tenant that cannot be read is refused, not dropped</b> — a value the resolver cannot evaluate is not one
/// it may ignore, the operator-session resolver's rule. An empty one is no request at all.
/// </para>
/// <para>
/// <b>The principal is S2 D15's:</b> the account as <see cref="AlvoContext.User"/>, <see cref="Role.Authenticated"/>
/// only, the tenant and tenant roles the membership rules answered, <see cref="AlvoActorKind.User"/>, the
/// wildcard scope pair — a session is not scope-limited, the rules decide (S1 D8), as the operator session's is — and
/// <c>session:&lt;sid&gt;</c> as the key id, which is no secret and scopes idempotency keys to the session.
/// </para>
/// </remarks>
/// <param name="sessions">Validates the token and reads the session and the membership.</param>
/// <param name="signIn">The applied descriptor's self-service policy.</param>
/// <param name="http">The request being resolved, for the issuer in Development.</param>
internal sealed class PersonResolver(PersonSessions sessions, DescriptorSignIn signIn, IHttpContextAccessor http)
    : IAlvoContextResolver
{
    /// <summary>The prefix of a person principal's key id; the session id follows, as 32 hex digits.</summary>
    internal const string KeyIdPrefix = "session:";

    private static readonly IReadOnlySet<Role> _roles = new HashSet<Role> { Role.Authenticated };

    private static readonly IReadOnlySet<ApiKeyScope> _sessionScopes = new HashSet<ApiKeyScope>
    {
        new() { Entity = "*", Access = ScopeAccess.Read },
        new() { Entity = "*", Access = ScopeAccess.Write },
    };

    /// <inheritdoc/>
    public async ValueTask<AlvoPrincipal?> ResolveAsync(
        string? presentedKey, string? requestedTenant, CancellationToken cancellationToken)
    {
        if (presentedKey is null
            || http.HttpContext?.Request is not { } request
            || await sessions.IdentifyAsync(presentedKey, request, cancellationToken).ConfigureAwait(false) is not { } session
            || !TryRequested(requestedTenant, out var requested))
        {
            return null;
        }

        var policy = signIn.SelfService;
        var plan = MembershipRules.Plan(requested, policy);
        if (plan.Kind == MembershipPlanKind.Refuse)
        {
            return null;
        }

        var read = await sessions.ReadAsync(session, plan.Query, cancellationToken).ConfigureAwait(false);
        var membership = plan.Kind == MembershipPlanKind.NoTenant
            ? MembershipResolution.NoTenant
            : MembershipRules.Decide(requested, read.Membership, policy);
        return read.Live && !membership.Refused ? Principal(session, membership) : null;
    }

    /// <summary>Reads the requested tenant: none when empty, refused when it is not a tenant id.</summary>
    /// <param name="presented">The tenant header's value, if any.</param>
    /// <param name="requested">The tenant asked for, or <see langword="null"/> for none.</param>
    private static bool TryRequested(string? presented, out TenantId? requested)
    {
        requested = null;
        if (string.IsNullOrEmpty(presented))
        {
            return true;
        }

        if (!TenantId.TryParse(presented, null, out var tenant))
        {
            return false;
        }

        requested = tenant;
        return true;
    }

    private static AlvoPrincipal Principal(PersonSession session, MembershipResolution membership) => new()
    {
        Context = new AlvoContext
        {
            User = session.Account,
            Roles = _roles,
            Tenant = membership.Tenant,
            TenantRoles = membership.TenantRoles,
            ActorKind = AlvoActorKind.User,
        },
        Scopes = _sessionScopes,
        KeyId = KeyIdPrefix + session.Session.ToString("N"),
    };
}
