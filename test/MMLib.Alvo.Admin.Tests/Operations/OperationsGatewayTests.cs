using Microsoft.AspNetCore.Components.Authorization;

using MMLib.Alvo.Admin.Internal;
using MMLib.Alvo.Auth;
using MMLib.Alvo.Management;

using NSubstitute;

using System.Security.Claims;

namespace MMLib.Alvo.Admin.Tests.Operations;

/// <summary>The dashboard asks the operations port for the project it is looking at, and drops the catalog after a pause.</summary>
public sealed class OperationsGatewayTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task A_page_of_deliveries_is_asked_for_the_dashboard_s_project()
    {
        var operations = Substitute.For<IAlvoOperations>();
        operations.ListDeliveriesAsync(Arg.Any<string>(), Arg.Any<ManagementDeliveryQuery>(), Arg.Any<CancellationToken>())
            .Returns(new ManagementDeliveryPage { Items = [] });
        var gateway = new OperationsGateway(operations, Management(out _));

        await gateway.DeliveriesAsync("dead", Ct);

        await operations.Received(1).ListDeliveriesAsync("default", Arg.Is<ManagementDeliveryQuery>(query => query.Status == "dead"), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task A_pause_drops_the_cached_catalog_so_the_handler_reads_paused()
    {
        var operations = Substitute.For<IAlvoOperations>();
        var management = Management(out var contract);
        contract.GetExtensionsAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(new ManagementExtensionCatalog { Items = [] });
        var gateway = new OperationsGateway(operations, management);
        await gateway.HandlersAsync(Ct);

        await gateway.SetPausedAsync("send-confirmation", paused: true, "down", Ct);
        await gateway.HandlersAsync(Ct);

        await operations.Received(1).SetPausedAsync("default", "send-confirmation", true, "down", Arg.Any<CancellationToken>());
        await contract.Received(2).GetExtensionsAsync("default", Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Each_handler_read_asks_the_catalog_again_so_a_pause_made_elsewhere_shows()
    {
        var management = Management(out var contract);
        contract.GetExtensionsAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(
            new ManagementExtensionCatalog { Items = [Handler(paused: false)] },
            new ManagementExtensionCatalog { Items = [Handler(paused: true)] });
        var gateway = new OperationsGateway(Substitute.For<IAlvoOperations>(), management);
        await gateway.HandlersAsync(Ct);

        var refreshed = await gateway.HandlersAsync(Ct);

        refreshed.ShouldHaveSingleItem().Paused.ShouldBeTrue("another operator paused it; a refresh must show that");
    }

    [Fact]
    public async Task A_refused_catalog_read_reaches_the_page_rather_than_reading_as_no_handlers()
    {
        var management = Management(out var contract);
        contract.GetExtensionsAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns<ManagementExtensionCatalog>(_ => throw new ManagementForbiddenException("Viewer level is needed."));
        var gateway = new OperationsGateway(Substitute.For<IAlvoOperations>(), management);

        await Should.ThrowAsync<ManagementForbiddenException>(() => gateway.HandlersAsync(Ct));
    }

    [Fact]
    public async Task A_replay_dry_run_carries_no_key_and_the_real_run_carries_the_one_it_was_given()
    {
        var operations = Substitute.For<IAlvoOperations>();
        operations.ReplayAsync(Arg.Any<string>(), Arg.Any<ManagementReplayRequest>(), Arg.Any<CancellationToken>())
            .Returns(new ManagementReplayPlan { DryRun = true, Count = 0, Ids = [] });
        var gateway = new OperationsGateway(operations, Management(out _));

        await gateway.PlanReplayAsync("send-confirmation", Ct);
        await gateway.ReplayAsync("send-confirmation", "endpoint back", "key-1", Ct);

        await operations.Received(1).ReplayAsync("default", Arg.Is<ManagementReplayRequest>(request => request.DryRun && request.IdempotencyKey == null), Arg.Any<CancellationToken>());
        await operations.Received(1).ReplayAsync("default", Arg.Is<ManagementReplayRequest>(request => !request.DryRun && request.IdempotencyKey == "key-1" && request.Reason == "endpoint back"), Arg.Any<CancellationToken>());
    }

    private static ManagementExtension Handler(bool paused) => new()
    {
        Id = "send-confirmation",
        Kind = AlvoExtensionKind.EventHandler,
        Stage = AlvoExtensionStage.PostCommit,
        Paused = paused,
    };

    private static ManagementGateway Management(out IAlvoManagement contract)
    {
        contract = Substitute.For<IAlvoManagement>();
        contract.ListProjectsAsync(Arg.Any<CancellationToken>()).Returns([new ManagementProject("default", 1, "applied")]);
        var authentication = Substitute.For<AuthenticationStateProvider>();
        authentication.GetAuthenticationStateAsync().Returns(Task.FromResult(new AuthenticationState(new ClaimsPrincipal())));

        return new ManagementGateway(contract, people: null, Substitute.For<IAlvoAdminCallerResolver>(), authentication, Substitute.For<IAlvoContextAccessor>());
    }
}
