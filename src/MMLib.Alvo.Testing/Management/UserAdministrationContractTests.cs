using MMLib.Alvo.Management;
using Shouldly;
using Xunit;

namespace MMLib.Alvo.Testing.Management;

/// <summary>
/// The contract every reachable <see cref="IAlvoUserAdministration"/> satisfies.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why this is a contract suite and not an integration test.</b> The guards it measures are the
/// core's, applied by a decorator every registration goes through — and a rule enforced in one
/// place is only a rule if every implementation is reached through that place. A test written
/// against the one implementation that exists today would measure the implementation; this
/// measures the arrangement, and a second membership store inherits it by running the same suite.
/// </para>
/// <para>
/// What it deliberately does not measure is the store's own behaviour: whether creating a user
/// twice is an error, how a directory paginates, what an OIDC deployment does about passwords.
/// Those differ legitimately, and the port says so by letting any member refuse by name.
/// </para>
/// </remarks>
public abstract class UserAdministrationContractTests
{
    /// <summary>
    /// The surface under test, as a caller with no management level reaches it.
    /// </summary>
    /// <returns>The administration port, guarded exactly as a container hands it out.</returns>
    protected abstract IAlvoUserAdministration AsUnprivilegedCaller();

    /// <summary>
    /// The surface under test, as an administrator reaches it.
    /// </summary>
    /// <returns>The administration port, guarded exactly as a container hands it out.</returns>
    protected abstract IAlvoUserAdministration AsAdministrator();

    /// <summary>The administrator's own user id, for the self-grant facts.</summary>
    protected abstract UserId Administrator { get; }

    /// <summary>The bootstrap administrator's user id.</summary>
    protected abstract UserId BootstrapAdministrator { get; }

    /// <summary>No-op unless the implementation must be skipped in this environment.</summary>
    protected virtual void EnsureAvailable()
    {
    }

    /// <summary>
    /// Every member is refused for a caller the project admits at no level.
    /// </summary>
    /// <remarks>
    /// Including the read. The people list is the one place a project's administrators are
    /// enumerated, and that is reconnaissance a default-deny posture has no reason to hand out.
    /// </remarks>
    [Fact]
    public async Task A_caller_with_no_level_is_refused_every_member()
    {
        EnsureAvailable();
        var surface = AsUnprivilegedCaller();

        await Should.ThrowAsync<ManagementForbiddenException>(
            () => surface.ListAsync(new AlvoUserQuery()));
        await Should.ThrowAsync<ManagementForbiddenException>(
            () => surface.CreateAsync(new AlvoUserCreation("nobody@example.com", [])));
        await Should.ThrowAsync<ManagementForbiddenException>(
            () => surface.SetRolesAsync(Administrator, []));
        await Should.ThrowAsync<ManagementForbiddenException>(
            () => surface.SetTenantAsync(Administrator, null));
        await Should.ThrowAsync<ManagementForbiddenException>(
            () => surface.SetDisabledAsync(Administrator, disabled: true));
        await Should.ThrowAsync<ManagementForbiddenException>(
            () => surface.IssueCredentialTokenAsync(Administrator));
    }

    /// <summary>
    /// Nobody grants themselves a tenant.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A mistake-guard rather than a malice-guard — an administrator can reach the same rows
    /// through a puppet account, or by rewriting the entity's rules through an apply, and no
    /// arrangement of guards makes an untrusted administrator safe. What it buys is the honest
    /// mistake caught for nothing, and the recorded route staying the obvious one: an apply carries
    /// an author that configuration history renders forever, and a tenant grant does not.
    /// </para>
    /// <para>
    /// <b>This is the reachable half of the self-grant rule.</b> Its sibling — a caller granting
    /// themselves a role that raises their management level — cannot be exercised while
    /// <c>ManageUsers</c> is an <c>admin</c> operation, because <c>admin</c> is the highest level
    /// and nothing raises a caller who already holds it. The guard is kept for the day that
    /// changes; there is deliberately no test asserting an outcome no caller can produce.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task A_caller_cannot_grant_themselves_a_tenant()
    {
        EnsureAvailable();

        await Should.ThrowAsync<ManagementEscalationException>(
            () => AsAdministrator().SetTenantAsync(Administrator, TenantId.New()));
    }

    /// <summary>The reserved all-zero uuid is refused as a tenant grant.</summary>
    /// <remarks>
    /// <b>The one refusal on this port that fails open rather than closed, which is why it is a
    /// contract fact rather than a unit test.</b> A grant of the all-zero uuid produces a caller
    /// whose tenant is <em>present</em>, so the tenant predicate is attached and matches every row
    /// whose <c>tenant_id</c> was defaulted rather than assigned. Removing a tenant is
    /// <see langword="null"/>; the two must never be conflated by an implementation.
    /// </remarks>
    [Fact]
    public async Task The_reserved_tenant_cannot_be_granted()
    {
        EnsureAvailable();

        await Should.ThrowAsync<ManagementRequestException>(
            () => AsAdministrator().SetTenantAsync(Administrator, new TenantId(Guid.Empty)));
    }

    /// <summary>The reserved all-zero uuid is refused when a person is created, too.</summary>
    /// <remarks>
    /// <see cref="AlvoUserCreation"/> carries a tenant, so creation is the second door onto the
    /// same grant. A guard on one of the two doors is not a guard.
    /// </remarks>
    [Fact]
    public async Task The_reserved_tenant_cannot_be_granted_at_creation()
    {
        EnsureAvailable();

        await Should.ThrowAsync<ManagementRequestException>(
            () => AsAdministrator().CreateAsync(
                new AlvoUserCreation(
                    $"reserved-{Guid.CreateVersion7():N}@alvo.test", [], new TenantId(Guid.Empty))));
    }

    /// <summary>A caller may still remove their own tenant, because that narrows.</summary>
    [Fact]
    public async Task A_caller_may_remove_their_own_tenant()
    {
        EnsureAvailable();

        var after = await AsAdministrator().SetTenantAsync(Administrator, null);
        after.Tenant.ShouldBeNull();
    }

    /// <summary>A caller may still narrow their own membership.</summary>
    /// <remarks>
    /// The guard compares the level before against the level after, so removing a role is allowed
    /// by construction. Asserting it keeps a future implementation from turning the guard into
    /// "a caller may not touch their own row", which would be a different and more annoying rule.
    /// </remarks>
    [Fact]
    public async Task A_caller_may_narrow_their_own_membership()
    {
        EnsureAvailable();

        var after = await AsAdministrator().SetRolesAsync(Administrator, []);
        after.RoleNames.ShouldBeEmpty();
    }

    /// <summary>The bootstrap administrator cannot be disabled.</summary>
    [Fact]
    public async Task The_bootstrap_administrator_cannot_be_disabled()
    {
        EnsureAvailable();

        await Should.ThrowAsync<ManagementEscalationException>(
            () => AsAdministrator().SetDisabledAsync(BootstrapAdministrator, disabled: true));
    }

    /// <summary>The bootstrap administrator's credential cannot be reset from here.</summary>
    /// <remarks>
    /// Otherwise any administrator mints a token for the account the descriptor's access block does
    /// not govern, sets its password and signs in as it. The bootstrap credential comes from a
    /// mounted file; rotating it is a deployment operation.
    /// </remarks>
    [Fact]
    public async Task The_bootstrap_administrators_credential_cannot_be_reset()
    {
        EnsureAvailable();

        await Should.ThrowAsync<ManagementEscalationException>(
            () => AsAdministrator().IssueCredentialTokenAsync(BootstrapAdministrator));
    }
}
