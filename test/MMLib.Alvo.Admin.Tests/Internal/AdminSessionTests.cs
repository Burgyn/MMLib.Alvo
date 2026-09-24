using Microsoft.AspNetCore.Components.Authorization;
using MMLib.Alvo.Admin.Components.Schema;
using MMLib.Alvo.Admin.Internal;
using MMLib.Alvo.Auth;
using MMLib.Alvo.Data;
using MMLib.Alvo.Management;
using NSubstitute;
using System.Security.Claims;

namespace MMLib.Alvo.Admin.Tests.Internal;

/// <summary>
/// How a screen follows the operator's working copy, and when the copy is taken from the applied descriptor.
/// </summary>
/// <remarks>
/// <b>The copy outlives every circuit that shows it</b> — it is the operator's, held by a singleton store — so
/// a subscription nothing removes is a leak into another circuit's renderer, not only a stale redraw. That is
/// why the guard and both ways of ending a subscription are measured here rather than trusted per screen.
/// </remarks>
public class AdminSessionTests
{
    private const string Applied = """{"entities":{"orders":{"fields":{}}}}""";

    [Fact]
    public void A_change_reaches_the_screen_through_its_own_invoke()
    {
        var copy = new WorkingCopy();
        var invoked = 0;
        var changed = 0;

        using var following = Session().Follow(copy, work => { invoked++; return work(); }, () => changed++, Ct);
        copy.Take(Applied, 1);

        invoked.ShouldBe(1, "the edit may be another circuit's, so it must be marshalled onto this renderer");
        changed.ShouldBe(1);
    }

    [Fact]
    public void A_screen_disposed_during_the_await_never_subscribes()
    {
        var copy = new WorkingCopy();
        var lifetime = new ComponentLifetime();
        lifetime.Dispose();
        var changed = 0;

        Session().Follow(copy, Invoke, () => changed++, lifetime.Token).Dispose();
        copy.Take(Applied, 1);

        changed.ShouldBe(0, "nothing would unsubscribe it, and the copy outlives the circuit");
    }

    [Fact]
    public void Disposing_the_handle_unsubscribes()
    {
        var copy = new WorkingCopy();
        var changed = 0;

        var following = Session().Follow(copy, Invoke, () => changed++, Ct);
        following.Dispose();
        following.Dispose();
        copy.Take(Applied, 1);

        changed.ShouldBe(0);
    }

    [Fact]
    public void The_end_of_the_screen_unsubscribes()
    {
        var copy = new WorkingCopy();
        var lifetime = new ComponentLifetime();
        var changed = 0;

        var following = Session().Follow(copy, Invoke, () => changed++, lifetime.Token);
        lifetime.Dispose();
        copy.Take(Applied, 1);
        following.Dispose();

        changed.ShouldBe(0, "a screen that only disposes its lifetime must not leave a handler on the copy");
        lifetime.Ended.ShouldBeTrue();
    }

    [Fact]
    public async Task An_unloaded_copy_is_taken_from_the_applied_descriptor()
    {
        var copy = await Session(Management()).WorkingCopyAsync(Ct);

        copy.Loaded.ShouldBeTrue();
        copy.Revision.ShouldBe(3);
        copy.IsDirty.ShouldBeFalse();
    }

    [Fact]
    public async Task A_loaded_copy_keeps_its_unapplied_edits()
    {
        var management = Management();
        var copy = new WorkingCopy();
        copy.Take(Applied, 1);
        copy.RemoveEntity("orders");

        await Session(management).EnsureLoadedAsync(copy, Ct);

        copy.IsDirty.ShouldBeTrue("re-taking a loaded copy would discard the operator's edits on navigation");
        copy.Revision.ShouldBe(1);
        await management.DidNotReceive().GetDescriptorAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task A_copy_another_tab_took_during_the_read_keeps_that_tabs_edits()
    {
        var copy = new WorkingCopy();
        var management = Management();
        management.GetDescriptorAsync("p", Arg.Any<CancellationToken>()).Returns(_ =>
        {
            copy.Take(Applied, 3);
            copy.RemoveEntity("orders");
            return new ManagementDescriptor("p", 3, Applied);
        });

        await Session(management).EnsureLoadedAsync(copy, Ct);

        copy.IsDirty.ShouldBeTrue("the other tab's edit landed while this one was reading the descriptor");
    }

    [Fact]
    public async Task The_copy_is_the_signed_in_operators_own()
    {
        var store = new WorkingCopyStore(TimeProvider.System);
        var eva = UserId.New();

        var copy = await Session(store: store, user: eva).CopyAsync(Ct);

        copy.ShouldBeSameAs(store.For(eva));
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static Task Invoke(Func<Task> work) => work();

    private static IAlvoManagement Management()
    {
        var management = Substitute.For<IAlvoManagement>();
        management.ListProjectsAsync(Arg.Any<CancellationToken>())
            .Returns([new ManagementProject("p", 3, "applied")]);
        management.GetDescriptorAsync("p", Arg.Any<CancellationToken>())
            .Returns(new ManagementDescriptor("p", 3, Applied));

        return management;
    }

    /// <summary>A session over real gateways: both are sealed, so the dashboard's tests construct them.</summary>
    private static AdminSession Session(
        IAlvoManagement? management = null, WorkingCopyStore? store = null, UserId? user = null)
    {
        var authentication = Substitute.For<AuthenticationStateProvider>();
        authentication.GetAuthenticationStateAsync()
            .Returns(Task.FromResult(new AuthenticationState(new ClaimsPrincipal())));

        var callers = Substitute.For<IAlvoAdminCallerResolver>();
        callers.ResolveAsync(Arg.Any<ClaimsPrincipal>(), Arg.Any<CancellationToken>())
            .Returns(new AlvoPrincipal
            {
                Context = new AlvoContext { User = user ?? UserId.New(), Roles = new HashSet<Role> { Role.Admin } },
                Scopes = new HashSet<ApiKeyScope>(),
                KeyId = "the-operator",
            });

        var gateway = new ManagementGateway(
            management ?? Substitute.For<IAlvoManagement>(), people: null, callers, authentication,
            Substitute.For<IAlvoContextAccessor>());

        return new AdminSession(
            gateway,
            new DataGateway(Substitute.For<IAlvoData>(), callers, authentication),
            store ?? new WorkingCopyStore(TimeProvider.System));
    }
}
