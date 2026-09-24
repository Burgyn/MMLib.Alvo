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

        navigation.GoTo("http://alvo.test/admin/history");

        (await gateway.DescriptorAsync(Ct)).Revision.ShouldBe(2);
    }

    [Fact]
    public async Task The_invalidation_runs_ahead_of_a_handler_that_subscribed_after_it()
    {
        var navigation = new TestNavigation();
        using var gateway = Gateway(navigation, out _);
        await gateway.DescriptorAsync(Ct);

        /* The project card's shape: subscribed in OnInitialized, after the gateway was injected, and re-reading
           synchronously inside the handler. The substitute's reads complete at once, as the in-process ones do. */
        int? seen = null;
        navigation.LocationChanged += (_, _) => seen = gateway.DescriptorAsync(Ct).AsTask().GetAwaiter().GetResult().Revision;

        navigation.GoTo("http://alvo.test/admin/schema");

        seen.ShouldBe(2);
    }

    [Fact]
    public async Task A_disposed_gateway_no_longer_follows_the_circuit()
    {
        var navigation = new TestNavigation();
        var gateway = Gateway(navigation, out var management);
        await gateway.DescriptorAsync(Ct);

        gateway.Dispose();
        navigation.GoTo("http://alvo.test/admin/schema");

        (await gateway.DescriptorAsync(Ct)).Revision.ShouldBe(1, "a scope that ended must not keep a handler on the navigation");
        await management.Received(1).GetDescriptorAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
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

    /// <summary>A circuit's navigation, moved by the test rather than by a browser.</summary>
    private sealed class TestNavigation : NavigationManager
    {
        public TestNavigation() => Initialize("http://alvo.test/", "http://alvo.test/admin");

        public void GoTo(string uri)
        {
            Uri = uri;
            NotifyLocationChanged(isInterceptedLink: true);
        }

        protected override void NavigateToCore(string uri, bool forceLoad) => GoTo(ToAbsoluteUri(uri).ToString());
    }
}
