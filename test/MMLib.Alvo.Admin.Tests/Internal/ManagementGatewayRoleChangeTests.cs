using Microsoft.AspNetCore.Components.Authorization;
using MMLib.Alvo.Admin.Internal;
using MMLib.Alvo.Auth;
using MMLib.Alvo.Data;
using MMLib.Alvo.Management;
using NSubstitute;
using System.Security.Claims;

namespace MMLib.Alvo.Admin.Tests.Internal;

/// <summary>
/// <b>A role chip is a change, not a snapshot.</b> Pressing one grants or revokes that one role against the
/// person's roles as they are stored now — never writes back the whole set the screen happened to show.
/// </summary>
/// <remarks>
/// <b>The defect this pins</b> (security review of <c>c10f75c</c>, Q1): the chip group emits the full selected
/// set, computed from the row as it was loaded, and the port's <c>SetRolesAsync</c> is a replacement. A tab
/// showing <c>[admin, editor]</c> after another administrator revoked <c>admin</c> sent
/// <c>[admin, editor, viewer]</c> when its operator pressed <c>viewer</c>, and silently restored <c>admin</c>.
/// </remarks>
public sealed class ManagementGatewayRoleChangeTests
{
    private static readonly UserId _eva = UserId.New();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Granting_a_role_from_a_stale_screen_does_not_restore_one_revoked_elsewhere()
    {
        var shown = Person("admin", "editor");
        var people = new People(Person("editor"));

        var after = await Gateway(people).ChangeRolesAsync(shown, grant: ["viewer"], revoke: [], Ct);

        people.Stored.RoleNames.ShouldBe(["editor", "viewer"], "the revoked admin must stay revoked");
        after.RoleNames.ShouldBe(["editor", "viewer"]);
    }

    [Fact]
    public async Task Revoking_a_role_from_a_stale_screen_keeps_one_granted_elsewhere()
    {
        var shown = Person("admin", "editor");
        var people = new People(Person("admin", "editor", "auditor"));

        await Gateway(people).ChangeRolesAsync(shown, grant: [], revoke: ["editor"], Ct);

        people.Stored.RoleNames.ShouldBe(["admin", "auditor"]);
    }

    [Fact]
    public async Task A_person_who_is_no_longer_there_is_a_refusal_that_says_to_reload()
    {
        var people = new People(stored: null);

        await Should.ThrowAsync<AlvoPreconditionFailedException>(
            () => Gateway(people).ChangeRolesAsync(Person("admin"), grant: ["viewer"], revoke: [], Ct));

        people.Writes.ShouldBe(0, "nothing is written for a person the store no longer holds");
    }

    [Fact]
    public void The_screen_delta_is_the_chip_that_changed()
    {
        var (grant, revoke) = ManagementGateway.RoleChange(shown: ["admin", "editor"], selected: ["editor", "viewer"]);

        grant.ShouldBe(["viewer"]);
        revoke.ShouldBe(["admin"]);
    }

    private static AlvoUser Person(params string[] roles) => new()
    {
        Id = _eva,
        Email = "eva@example.test",
        RoleNames = roles,
        IsDisabled = false,
        Tenant = null,
    };

    private static ManagementGateway Gateway(IAlvoUserAdministration people)
    {
        var callers = Substitute.For<IAlvoAdminCallerResolver>();
        callers.ResolveAsync(Arg.Any<ClaimsPrincipal>(), Arg.Any<CancellationToken>()).Returns((AlvoPrincipal?)null);

        var authentication = Substitute.For<AuthenticationStateProvider>();
        authentication.GetAuthenticationStateAsync()
            .Returns(Task.FromResult(new AuthenticationState(new ClaimsPrincipal())));

        return new ManagementGateway(
            Substitute.For<IAlvoManagement>(), people, callers, authentication, Substitute.For<IAlvoContextAccessor>());
    }

    /// <summary>An in-memory membership of one person, whose roles another administrator may already have changed.</summary>
    private sealed class People(AlvoUser? stored) : IAlvoUserAdministration
    {
        public AlvoUser Stored { get; private set; } = stored!;

        public int Writes { get; private set; }

        public Task<AlvoUserPage> ListAsync(AlvoUserQuery query, CancellationToken cancellationToken = default)
            => Task.FromResult(new AlvoUserPage(Stored is null ? [] : [Stored]));

        public Task<AlvoUser> SetRolesAsync(
            UserId user, IReadOnlyList<string> roleNames, CancellationToken cancellationToken = default)
        {
            Writes++;
            Stored = Stored with { RoleNames = roleNames };
            return Task.FromResult(Stored);
        }

        public Task<AlvoUser> CreateAsync(AlvoUserCreation creation, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<AlvoUser> SetTenantAsync(UserId user, TenantId? tenant, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<AlvoUser> SetDisabledAsync(UserId user, bool disabled, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<AlvoCredentialToken> IssueCredentialTokenAsync(UserId user, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();
    }
}
