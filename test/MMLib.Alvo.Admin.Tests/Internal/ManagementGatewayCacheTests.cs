using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Authorization;
using MMLib.Alvo.Admin.Internal;
using MMLib.Alvo.Auth;
using MMLib.Alvo.Management;
using NSubstitute;
using System.Security.Claims;

namespace MMLib.Alvo.Admin.Tests.Internal;

/// <summary>
/// How long the gateway's cached descriptor lives: one screen, not one circuit.
/// </summary>
/// <remarks>
/// <b>The defect this pins</b> (docs/architecture/admin-dashboard-review.md, F-15): the cache was dropped only
/// by an apply this circuit made, so an apply from another tab or another operator left the project card, and
/// every screen, a revision behind until the browser reloaded. Each read here answers with a higher revision
/// than the one before, so a test can tell a cached read from a fresh one.
/// </remarks>
public class ManagementGatewayCacheTests
{
    [Fact]
    public async Task One_screen_reads_the_descriptor_once()
    {
        using var gateway = Gateway(new TestNavigation(), out _);

        (await gateway.DescriptorAsync(Ct)).Revision.ShouldBe(1);
        (await gateway.DescriptorAsync(Ct)).Revision.ShouldBe(1, "the three screens that share a read are the point of the cache");
    }

    [Fact]
    public async Task A_navigation_reads_the_revision_somebody_else_applied()
    {
        var navigation = new TestNavigation();
        using var gateway = Gateway(navigation, out _);
        await gateway.DescriptorAsync(Ct);

        await navigation.GoToAsync("http://alvo.test/admin/history");

        (await gateway.DescriptorAsync(Ct)).Revision.ShouldBe(2);
    }

    [Fact]
    public async Task A_query_only_navigation_reads_again_too()
    {
        var navigation = new TestNavigation();
        using var gateway = Gateway(navigation, out _);
        await gateway.DescriptorAsync(Ct);

        await navigation.GoToAsync("http://alvo.test/admin?tab=rules");

        (await gateway.DescriptorAsync(Ct)).Revision.ShouldBe(2);
    }

    [Fact]
    public async Task A_page_the_router_renders_on_location_changed_reads_after_the_drop()
    {
        /* The router's shape: subscribed before the gateway existed, and rendering the destination inside its
           handler. The substitute's reads complete at once, as the in-process ones do. */
        var navigation = new TestNavigation();
        ManagementGateway? gateway = null;
        int? seen = null;
        navigation.LocationChanged += (_, _) => seen = gateway!.DescriptorAsync(Ct).AsTask().GetAwaiter().GetResult().Revision;

        gateway = Gateway(navigation, out _);
        await gateway.DescriptorAsync(Ct);
        await navigation.GoToAsync("http://alvo.test/admin/schema");

        seen.ShouldBe(2);
        gateway.Dispose();
    }

    [Fact]
    public async Task A_read_in_flight_across_an_invalidation_is_not_kept()
    {
        using var gateway = Gateway(new TestNavigation(), out var management);
        var slow = new TaskCompletionSource<ManagementDescriptor>();
        management.GetDescriptorAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(slow.Task, Task.FromResult(new ManagementDescriptor("default", 2, "{}")));

        var inFlight = gateway.DescriptorAsync(Ct).AsTask();
        gateway.Invalidate();
        slow.SetResult(new ManagementDescriptor("default", 1, "{}"));

        (await inFlight).Revision.ShouldBe(1, "the caller still gets the answer it asked for");
        (await gateway.DescriptorAsync(Ct)).Revision.ShouldBe(2, "the stale answer must not refill the cache");
    }

    [Fact]
    public async Task A_disposed_gateway_no_longer_follows_the_circuit()
    {
        var navigation = new TestNavigation();
        var gateway = Gateway(navigation, out var management);
        await gateway.DescriptorAsync(Ct);

        gateway.Dispose();
        await navigation.GoToAsync("http://alvo.test/admin/schema");

        (await gateway.DescriptorAsync(Ct)).Revision.ShouldBe(1, "a scope that ended must not keep a handler on the navigation");
        await management.Received(1).GetDescriptorAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task A_static_render_caches_for_its_request()
    {
        using var gateway = Gateway(new StaticNavigation(), out _);

        (await gateway.DescriptorAsync(Ct)).Revision.ShouldBe(1);
        (await gateway.DescriptorAsync(Ct)).Revision.ShouldBe(1);
    }

    private static ManagementGateway Gateway(NavigationManager navigation, out IAlvoManagement management)
    {
        management = Substitute.For<IAlvoManagement>();
        management.ListProjectsAsync(Arg.Any<CancellationToken>())
            .Returns([new ManagementProject("default", 1, "applied")]);

        var revision = 0;
        management.GetDescriptorAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(_ => new ManagementDescriptor("default", ++revision, "{}"));

        var authentication = Substitute.For<AuthenticationStateProvider>();
        authentication.GetAuthenticationStateAsync()
            .Returns(Task.FromResult(new AuthenticationState(new ClaimsPrincipal())));

        var gateway = new ManagementGateway(
            management, people: null, Substitute.For<IAlvoAdminCallerResolver>(), authentication,
            Substitute.For<IAlvoContextAccessor>());
        gateway.FollowNavigation(navigation);

        return gateway;
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>
    /// A circuit's navigation, moved by the test rather than by a browser: changing handlers first, then
    /// <see cref="NavigationManager.LocationChanged"/>, as the framework raises them.
    /// </summary>
    private sealed class TestNavigation : NavigationManager
    {
        public TestNavigation() => Initialize("http://alvo.test/", "http://alvo.test/admin");

        public async Task GoToAsync(string uri)
        {
            (await NotifyLocationChangingAsync(uri, state: null, isNavigationIntercepted: true)).ShouldBeTrue();
            Uri = uri;
            NotifyLocationChanged(isInterceptedLink: true);
        }

        protected override void SetNavigationLockState(bool value)
        {
        }

        protected override void NavigateToCore(string uri, bool forceLoad)
            => throw new NotSupportedException("The tests move this with GoToAsync.");
    }

    /// <summary>A statically rendered request's navigation, which supports no location-changing handlers.</summary>
    private sealed class StaticNavigation : NavigationManager
    {
        public StaticNavigation() => Initialize("http://alvo.test/", "http://alvo.test/admin");
    }
}
