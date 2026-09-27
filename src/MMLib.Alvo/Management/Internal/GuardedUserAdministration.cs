using Microsoft.Extensions.DependencyInjection;
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
/// There are four guards, and they are not the same kind of thing:
/// </para>
/// <list type="number">
/// <item><b>The gate.</b> <c>ManageUsers</c> is an <c>admin</c> operation, and every member is
/// refused for a caller the project does not resolve to that level.</item>
/// <item><b>Nobody grants themselves a level.</b> A mistake-guard, not a malice-guard — see the
/// remark on <see cref="EnsureNoSelfEscalation"/> for what it does and does not claim.</item>
/// <item><b>The bootstrap administrator is not a target.</b> Two members would otherwise remove the
/// one identity the whole default-deny story rests on.</item>
/// <item><b>The reserved tenant is not a tenant.</b> The only one of the four that fails
/// <em>open</em> if it is missing — see <see cref="EnsureTenantIsReal"/>.</item>
/// </list>
/// <para>
/// <b>Every call is its own unit of work: the implementation is resolved from a scope created for that
/// call and disposed when it returns.</b> This decorator is resolved from whatever scope its caller has,
/// and in the dashboard that scope is the Blazor circuit — open for as long as the tab. An implementation
/// resolved alongside it would keep one <c>DbContext</c>, and one change tracker, for all of that time:
/// a write left its row tracked, so the next write was made against a stale copy, and a write that
/// failed left rows behind that every later write re-flushed — one lost race and the tab could disable
/// nobody until it was reloaded. A scope per call rules that out for any implementation, including a
/// host's own that caches in a scoped service. The guards still read the caller from the decorator's own
/// scope; the implementation authorizes nothing, so it needs nothing from there.
/// </para>
/// </remarks>
/// <param name="scopes">Creates the scope each call resolves the implementation from.</param>
/// <param name="callers">The ambient caller the management routes publish.</param>
/// <param name="access">Resolves a caller's management level from the project's own access block.</param>
/// <param name="roles">The role catalogue the descriptor declares.</param>
/// <param name="bootstrap">Who the bootstrap administrator is.</param>
internal sealed class GuardedUserAdministration(
    IServiceScopeFactory scopes,
    IAlvoContextAccessor callers,
    ManagementAccessEvaluator access,
    IRoleCatalogProvider roles,
    IAlvoBootstrapAdmin bootstrap) : IAlvoUserAdministration
{
    /// <inheritdoc/>
    public Task<AlvoUserPage> ListAsync(AlvoUserQuery query, CancellationToken cancellationToken = default)
    {
        EnsureMayManage();
        return InScopeAsync(inner => inner.ListAsync(query, cancellationToken));
    }

    /// <inheritdoc/>
    public Task<AlvoUser> CreateAsync(
        AlvoUserCreation creation, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(creation);

        EnsureMayManage();
        EnsureTenantIsReal(creation.Tenant);
        return InScopeAsync(inner => inner.CreateAsync(creation, cancellationToken));
    }

    /// <inheritdoc/>
    public Task<AlvoUser> SetRolesAsync(
        UserId user, IReadOnlyList<string> roleNames, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(roleNames);

        EnsureMayManage();
        EnsureNoSelfEscalation(user, roleNames);
        return InScopeAsync(inner => inner.SetRolesAsync(user, roleNames, cancellationToken));
    }

    /// <inheritdoc/>
    public Task<AlvoUser> SetTenantAsync(
        UserId user, TenantId? tenant, CancellationToken cancellationToken = default)
    {
        EnsureMayManage();
        EnsureTenantIsReal(tenant);
        EnsureNotSelfTenantGrant(user, tenant);
        return InScopeAsync(inner => inner.SetTenantAsync(user, tenant, cancellationToken));
    }

    /// <inheritdoc/>
    public Task<AlvoUser> SetDisabledAsync(
        UserId user, bool disabled, CancellationToken cancellationToken = default)
    {
        EnsureMayManage();
        EnsureNotBootstrap(user, "disabled");
        return InScopeAsync(inner => inner.SetDisabledAsync(user, disabled, cancellationToken));
    }

    /// <inheritdoc/>
    /// <remarks>
    /// <para>
    /// <b>The gate, and only the gate.</b> Ending a lockout raises no level and grants no tenant, so the
    /// self-grant guards have nothing to compare; and it is not refused for the bootstrap administrator,
    /// whose lockout is the one a stranger most wants to keep standing (<c>docs/todo-admin.md</c> §8d item 46) —
    /// ending it gives back the account every recovery rests on rather than removing it.
    /// </para>
    /// <para>
    /// <b>A disabled person is refused by the implementation, not here</b>, because only the implementation reads
    /// the person in the unit of work that writes them: a check made here would read before a disable another
    /// administrator commits and let the write undo it. The contract suite holds every implementation to it.
    /// </para>
    /// </remarks>
    public Task<AlvoUser> ClearLockoutAsync(UserId user, CancellationToken cancellationToken = default)
    {
        EnsureMayManage();
        return InScopeAsync(inner => inner.ClearLockoutAsync(user, cancellationToken));
    }

    /// <inheritdoc/>
    public Task<AlvoCredentialToken> IssueCredentialTokenAsync(
        UserId user, CancellationToken cancellationToken = default)
    {
        EnsureMayManage();
        EnsureNotBootstrap(user, "issued a credential token");
        return InScopeAsync(inner => inner.IssueCredentialTokenAsync(user, cancellationToken));
    }

    private AlvoContext Caller => callers.Principal?.Context ?? AlvoContext.Anonymous;

    /// <summary>Runs one call against an implementation resolved from a scope of its own.</summary>
    /// <remarks>
    /// Awaited here rather than returned, so the scope outlives the call it serves and ends with it —
    /// returning the task would dispose the implementation's <c>DbContext</c> under a write in flight.
    /// </remarks>
    /// <typeparam name="T">What the call answers with.</typeparam>
    /// <param name="call">The call, made against the implementation.</param>
    /// <returns>Whatever the implementation answered.</returns>
    private async Task<T> InScopeAsync<T>(Func<IAlvoUserAdministration, Task<T>> call)
    {
        var scope = scopes.CreateAsyncScope();
        await using (scope.ConfigureAwait(false))
        {
            var inner = scope.ServiceProvider.GetRequiredKeyedService<IAlvoUserAdministration>(
                AlvoUserAdministration.UnguardedKey);
            return await call(inner).ConfigureAwait(false);
        }
    }

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
    /// <b>No caller can reach this today, and it is kept anyway.</b> The gate above admits only a
    /// caller the project resolves to <c>admin</c>, and <c>admin</c> is the highest level — so
    /// <c>Resolve(prospective) &gt; Resolve(caller)</c> is false for everyone who gets this far.
    /// It is written, and stated as unreachable, because the condition that makes it unreachable is
    /// one line of the level table: the day <c>ManageUsers</c> moves to <c>developer</c>, or a
    /// fourth level appears above <c>admin</c>, a self-grant becomes possible and this is already
    /// in the path. Deleting it would make that change silently dangerous; pretending it is load-
    /// bearing today would be the other kind of lie. There is deliberately no test asserting an
    /// outcome no caller can produce.
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
    /// Refuses the reserved all-zero uuid as a tenant grant.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The one refusal on this path that fails open rather than closed.</b> Every other bad
    /// tenant grant narrows what its holder reaches; this one widens it. <see cref="TenantId"/>
    /// reserves the all-zero uuid to mean <i>no tenant</i>, and a grant of it produces a principal
    /// that is not <see langword="null"/> — so the tenant predicate <em>is</em> attached, and
    /// compares equal to every row whose <c>tenant_id</c> was defaulted rather than assigned. A
    /// caller holding it would read another tenant's leftovers and believe they had been admitted
    /// properly.
    /// </para>
    /// <para>
    /// <b>Why here rather than only in the type.</b> The parsing paths already refuse it, but the
    /// management route binds a bare <c>uuid</c> out of the request body and constructs the
    /// <see cref="TenantId"/> itself, as an in-process caller may. This guard is the one place
    /// every transport passes through, so it is where the refusal cannot be routed around — the
    /// same argument that put the other three guards here rather than in the implementation.
    /// </para>
    /// <para>
    /// Removing a tenant is <see langword="null"/>, not all-zero, and the message says so: the
    /// distinction is the whole point, and a caller who sent all-zero meaning "none" needs to be
    /// told which one expresses it.
    /// </para>
    /// </remarks>
    /// <param name="tenant">The tenant being granted, or <see langword="null"/> to remove one.</param>
    private static void EnsureTenantIsReal(TenantId? tenant)
    {
        if (tenant is { Value: var value } && value == Guid.Empty)
        {
            throw new ManagementRequestException(
                "The all-zero uuid is reserved to mean \"no tenant\" and cannot be granted as one: "
                + "a caller holding it would match every row whose tenant was never assigned. Send "
                + "null to remove a person's tenant, or a real tenant id to grant one.");
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
