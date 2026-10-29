using Microsoft.AspNetCore.Components.Authorization;

using MMLib.Alvo.Admin.Components.Operations;
using MMLib.Alvo.Admin.Internal;
using MMLib.Alvo.Auth;
using MMLib.Alvo.Management;

using NSubstitute;

using System.Security.Claims;

namespace MMLib.Alvo.Admin.Tests.Operations;

/// <summary>
/// The Operations page's loads, latest wins (final review L2): a slower older load never overwrites what a newer one drew,
/// and a stale load's failure is not reported over the newer answer.
/// </summary>
public sealed class OperationsLoaderTests
{
    private static readonly ManagementDelivery _deadOne = Delivery("dead");
    private static readonly ManagementDelivery _succeededOne = Delivery("succeeded");

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task A_slower_older_load_is_dropped_once_a_newer_one_answered()
    {
        var dead = new TaskCompletionSource<ManagementDeliveryPage>(TaskCreationOptions.RunContinuationsAsynchronously);
        var loader = Loader(status => status == "dead" ? dead.Task : Task.FromResult(Page(_succeededOne)));

        var older = loader.LoadAsync("dead", selected: null, Ct);
        var newer = await loader.LoadAsync(status: null, selected: null, Ct);
        dead.SetResult(Page(_deadOne));

        newer.ShouldNotBeNull().Deliveries.ShouldHaveSingleItem().ShouldBe(_succeededOne);
        (await older).ShouldBeNull("the operator asked for All after dead; the dead-only list must not land under All");
        loader.Loading.ShouldBeFalse();
    }

    [Fact]
    public async Task A_stale_load_that_fails_is_dropped_rather_than_reported()
    {
        var dead = new TaskCompletionSource<ManagementDeliveryPage>(TaskCreationOptions.RunContinuationsAsynchronously);
        var loader = Loader(status => status == "dead" ? dead.Task : Task.FromResult(Page(_succeededOne)));

        var older = loader.LoadAsync("dead", selected: null, Ct);
        await loader.LoadAsync(status: null, selected: null, Ct);
        dead.SetException(new ManagementForbiddenException("Viewer level is needed."));

        (await older).ShouldBeNull();
    }

    [Fact]
    public async Task The_newest_load_s_failure_is_reported()
    {
        var loader = Loader(_ => Task.FromException<ManagementDeliveryPage>(new ManagementForbiddenException("Viewer level is needed.")));

        await Should.ThrowAsync<ManagementForbiddenException>(() => loader.LoadAsync(status: null, selected: null, Ct));
        loader.Loading.ShouldBeFalse();
    }

    [Fact]
    public async Task A_load_is_loading_until_the_newest_one_ends()
    {
        var first = new TaskCompletionSource<ManagementDeliveryPage>(TaskCreationOptions.RunContinuationsAsynchronously);
        var loader = Loader(_ => first.Task);

        var running = loader.LoadAsync(status: null, selected: null, Ct);

        loader.Loading.ShouldBeTrue();
        first.SetResult(Page(_deadOne));
        await running;
        loader.Loading.ShouldBeFalse();
    }

    private static OperationsLoader Loader(Func<string?, Task<ManagementDeliveryPage>> deliveries)
    {
        var operations = Substitute.For<IAlvoOperations>();
        operations.ListDeliveriesAsync(Arg.Any<string>(), Arg.Any<ManagementDeliveryQuery>(), Arg.Any<CancellationToken>())
            .Returns(call => deliveries(call.Arg<ManagementDeliveryQuery>().Status));
        operations.GetExtensionStatsAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(new ManagementExtensionStats { Scope = "this-instance", Window = TimeSpan.FromHours(24), Since = DateTimeOffset.UnixEpoch, Items = [] });

        return new OperationsLoader(new OperationsGateway(operations, Management()));
    }

    private static ManagementGateway Management()
    {
        var contract = Substitute.For<IAlvoManagement>();
        contract.ListProjectsAsync(Arg.Any<CancellationToken>()).Returns([new ManagementProject("default", 1, "applied")]);
        contract.GetExtensionsAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(new ManagementExtensionCatalog { Items = [] });
        var authentication = Substitute.For<AuthenticationStateProvider>();
        authentication.GetAuthenticationStateAsync().Returns(Task.FromResult(new AuthenticationState(new ClaimsPrincipal())));

        return new ManagementGateway(contract, people: null, Substitute.For<IAlvoAdminCallerResolver>(), authentication, Substitute.For<IAlvoContextAccessor>());
    }

    private static ManagementDeliveryPage Page(ManagementDelivery delivery) => new() { Items = [delivery] };

    private static ManagementDelivery Delivery(string status) => new()
    {
        Id = Guid.NewGuid(),
        EventId = Guid.NewGuid(),
        Subscriber = "send-confirmation",
        PartitionKey = "tickets:1",
        Status = status,
        Attempts = 1,
        NotBefore = DateTimeOffset.UnixEpoch,
    };
}
