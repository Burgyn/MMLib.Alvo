using MMLib.Alvo.Auth;

namespace MMLib.Alvo.Management.Internal;

/// <summary>
/// The guards every <see cref="IAlvoUserAdministration"/> implementation is reached through.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why the guards are here and not in the implementation.</b> A rule enforced inside a
/// swappable adapter is optional by construction — the second implementation simply does not have
/// it, and principles 2 and 5 both fail quietly. <c>AlvoManagementService</c>'s own remark names
/// the same failure: a guard living in one adapter <i>"would be the divergent authorization path
/// contract 4 forbids"</i>. So the implementation administers people and this decides who may.
/// </para>
/// <para>
/// There are three guards, and they are not the same kind of thing:
/// </para>
/// <list type="number">
/// <item><b>The gate.</b> <c>ManageUsers</c> is an <c>admin</c> operation, and every member is
/// refused for a caller the project does not resolve to that level.</item>
/// <item><b>Nobody grants themselves a level.</b> A mistake-guard, not a malice-guard — see the
/// remark on <see cref="EnsureNoSelfEscalation"/> for what it does and does not claim.</item>
/// <item><b>The bootstrap administrator is not a target.</b> Two members would otherwise remove the
/// one identity the whole default-deny story rests on.</item>
/// </list>
/// </remarks>
/// <param name="inner">The implementation, resolved through its key so nothing else can.</param>
/// <param name="callers">The ambient caller the management routes publish.</param>
/// <param name="access">Resolves a caller's management level from the project's own access block.</param>
/// <param name="roles">The role catalogue the descriptor declares.</param>
/// <param name="bootstrap">Who the bootstrap administrator is.</param>
internal sealed class GuardedUserAdministration(
    IAlvoUserAdministration inner,
    IAlvoContextAccessor callers,
    ManagementAccessEvaluator access,
    IRoleCatalogProvider roles,
    IAlvoBootstrapAdmin bootstrap) : IAlvoUserAdministration
{
    /// <inheritdoc/>
    public Task<AlvoUserPage> ListAsync(AlvoUserQuery query, CancellationToken cancellationToken = default)
    {
        EnsureMayManage();
        return inner.ListAsync(query, cancellationToken);
    }

    /// <inheritdoc/>
    public Task<AlvoUser> CreateAsync(
        AlvoUserCreation creation, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(creation);

        EnsureMayManage();
        return inner.CreateAsync(creation, cancellationToken);
    }

    /// <inheritdoc/>
    public Task<AlvoUser> SetRolesAsync(
        UserId user, IReadOnlyList<string> roleNames, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(roleNames);

        EnsureMayManage();
        EnsureNoSelfEscalation(user, roleNames);
        return inner.SetRolesAsync(user, roleNames, cancellationToken);
    }

    /// <inheritdoc/>
    public Task<AlvoUser> SetTenantAsync(
        UserId user, TenantId? tenant, CancellationToken cancellationToken = default)
    {
        EnsureMayManage();
        EnsureNotSelfTenantGrant(user, tenant);
        return inner.SetTenantAsync(user, tenant, cancellationToken);
    }

    /// <inheritdoc/>
    public Task<AlvoUser> SetDisabledAsync(
        UserId user, bool disabled, CancellationToken cancellationToken = default)
    {
        EnsureMayManage();
        EnsureNotBootstrap(user, "disabled");
        return inner.SetDisabledAsync(user, disabled, cancellationToken);
    }

    /// <inheritdoc/>
    public Task<AlvoCredentialToken> IssueCredentialTokenAsync(
        UserId user, CancellationToken cancellationToken = default)
    {
        EnsureMayManage();
        EnsureNotBootstrap(user, "issued a credential token");
        return inner.IssueCredentialTokenAsync(user, cancellationToken);
    }

    private AlvoContext Caller => callers.Principal?.Context ?? AlvoContext.Anonymous;

    private void EnsureMayManage()
    {
        if (!access.Allows(ManagementOperation.ManageUsers, Caller))
        {
            throw new ManagementForbiddenException();
        }
    }

    /// <summary>
    /// Refuses a caller raising their own management level.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The level is re-resolved, never name-matched.</b> The obvious implementation — <i>did they
    /// add <c>admin</c> to themselves?</i> — is wrong, because a level is any CEL predicate over
    /// declared roles: <c>access.admin: "'dispatcher' in @user.roles"</c> is legal, so a self-grant
    /// of <c>dispatcher</c> resolves to <c>admin</c> and a name match waves it through. This builds
    /// the caller's <em>prospective</em> context — their roles after the write, intersected with
    /// the declared catalogue exactly as the context resolver does — and compares the level before
    /// against the level after.
    /// </para>
    /// <para>
    /// <b>It is a mistake-guard, not a malice-guard, and claiming otherwise would be the more
    /// dangerous statement.</b> An administrator who wants the reach has it in one hop: create a
    /// puppet with the role, mint its credential token, sign in as it. That is not a hole at this
    /// level — it is the trust boundary itself, since an administrator already holds
    /// <c>ApplyDescriptor</c> and can rewrite the rules to admit themselves to every row. What the
    /// guard buys is the honest mistake caught at zero cost, and the audited route staying the
    /// obvious one: an apply carries an <c>Author</c> that configuration history renders forever.
    /// </para>
    /// </remarks>
    private void EnsureNoSelfEscalation(UserId user, IReadOnlyList<string> roleNames)
    {
        if (user != Caller.User)
        {
            return;
        }

        var declared = roles.DeclaredRoles;
        if (declared is null)
        {
            /* No catalogue means no role can be minted at all — the same fail-closed rule the
               context resolver applies — so the write cannot raise anything. */
            return;
        }

        var prospective = Caller with
        {
            Roles = roleNames
                .Where(name => declared.TryGet(name, out _))
                .Select(declared.Get)
                .Append(Role.Authenticated)
                .ToHashSet(),
        };

        if (access.Resolve(prospective) > access.Resolve(Caller))
        {
            throw new ManagementEscalationException(
                "A caller cannot grant themselves a role that raises the management level this "
                + "project's access block resolves for them. Ask another administrator, or change "
                + "the access block through an apply — which is recorded.");
        }
    }

    /// <summary>
    /// Refuses a caller granting themselves a tenant.
    /// </summary>
    /// <remarks>
    /// Weighed rather than assumed: a tenant is not a management level, so this is not the same
    /// rule as <see cref="EnsureNoSelfEscalation"/>. It is refused for the reason that makes the
    /// level guard worth having — a tenant grant decides which rows an operator reaches, the
    /// alternative route to the same reach is an apply that records an <c>Author</c> forever, and
    /// an administrator quietly widening their own data access is exactly the honest mistake worth
    /// catching. Removing one's own tenant is allowed: it narrows.
    /// </remarks>
    private void EnsureNotSelfTenantGrant(UserId user, TenantId? tenant)
    {
        if (user == Caller.User && tenant is not null && Caller.Tenant != tenant)
        {
            throw new ManagementEscalationException(
                "A caller cannot grant themselves a tenant. Ask another administrator — the grant "
                + "decides which rows you reach, and the recorded route for widening your own "
                + "access is an apply.");
        }
    }

    /// <summary>
    /// Refuses a write aimed at the bootstrap administrator.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The invariant <c>ManagementAccessEvaluator</c> states: <i>a project whose access block locks
    /// everyone out still has exactly one person who can fix it.</i> Two members would remove that
    /// person.
    /// </para>
    /// <para>
    /// <b>Disabling.</b> The context resolver answers <see langword="null"/> for a locked-out
    /// account <em>before</em> anything consults the bootstrap branch, and the seed does not reset
    /// an existing row — so a deployment whose access block admits nobody else, which §3.5 says is
    /// the default, would be locked out of its own management surface with no way back but editing
    /// the identity database by hand.
    /// </para>
    /// <para>
    /// <b>A credential token.</b> Without the refusal, any administrator mints one for the account
    /// the access block does not govern, sets its password, and signs in as it — the capability the
    /// host documentation refuses when it says seeding never resets an existing account's password.
    /// </para>
    /// </remarks>
    private void EnsureNotBootstrap(UserId user, string what)
    {
        if (bootstrap.IsBootstrapAdmin(user))
        {
            throw new ManagementEscalationException(
                $"The bootstrap administrator cannot be {what}. It is the account that can always "
                + "recover a project whose access block admits nobody, and its credential comes "
                + "from a mounted file — rotating it is a deployment operation.");
        }
    }
}
