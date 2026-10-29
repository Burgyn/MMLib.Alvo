using MMLib.Alvo.Management;

namespace MMLib.Alvo.Tests.Management;

/// <summary>The operations port's fixture and stats reads.</summary>
public sealed class AlvoOperationsFixtureAndStatsTests
{
    private const string Project = OperationsTestWorld.Project;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task A_fixture_redacts_the_delivery_s_envelope()
    {
        using var world = new OperationsTestWorld();
        var delivery = await world.DeliveryAsync("tickets:a", deadReason: "when-failed");

        var fixture = await world.Service.GetFixtureAsync(Project, delivery.Id, Ct);

        fixture.Subscriber.ShouldBe(OperationsTestWorld.Handler);
        fixture.Delivery.ShouldBe(delivery.Id);
        fixture.Envelope.GetRawText().ShouldNotContain("Printer jam");
    }

    [Fact]
    public async Task A_fixture_of_a_delivery_whose_event_is_gone_is_a_conflict()
    {
        using var world = new OperationsTestWorld();
        var delivery = await world.DeliveryAsync("tickets:a", keepEnvelope: false);

        (await Should.ThrowAsync<ManagementConflictException>(() => world.Service.GetFixtureAsync(Project, delivery.Id, Ct)))
            .Message.ShouldContain("no longer holds");
    }

    [Fact]
    public async Task A_fixture_of_an_unreadable_envelope_is_a_conflict_that_repeats_nothing_of_it()
    {
        using var world = new OperationsTestWorld();
        var delivery = await world.DeliveryAsync("tickets:a");
        world.Store.Overwrite(delivery.EventId, "[\"Printer jam\"");

        var refusal = await Should.ThrowAsync<ManagementConflictException>(() => world.Service.GetFixtureAsync(Project, delivery.Id, Ct));

        refusal.Message.ShouldNotContain("Printer jam");
        refusal.InnerException.ShouldBeNull();
    }

    [Fact]
    public async Task A_fixture_of_an_unknown_delivery_is_not_found()
    {
        using var world = new OperationsTestWorld();

        await Should.ThrowAsync<ManagementDeliveryNotFoundException>(() => world.Service.GetFixtureAsync(Project, Guid.NewGuid(), Ct));
    }

    [Fact]
    public async Task Stats_list_every_registered_extension_scoped_to_this_instance()
    {
        using var world = new OperationsTestWorld();

        var stats = await world.Service.GetExtensionStatsAsync(Project, Ct);

        stats.Scope.ShouldBe("this-instance");
        stats.Items.ShouldHaveSingleItem().Id.ShouldBe(OperationsTestWorld.Handler);
    }

    [Fact]
    public async Task The_fixture_and_the_stats_refuse_an_unpublished_caller()
    {
        using var world = new OperationsTestWorld(admitted: false);

        await Should.ThrowAsync<ManagementForbiddenException>(() => world.Service.GetFixtureAsync(Project, Guid.NewGuid(), Ct));
        await Should.ThrowAsync<ManagementForbiddenException>(() => world.Service.GetExtensionStatsAsync(Project, Ct));
    }

    [Fact]
    public async Task The_fixture_and_the_stats_refuse_a_project_this_instance_does_not_serve()
    {
        using var world = new OperationsTestWorld();

        await Should.ThrowAsync<ManagementProjectNotFoundException>(() => world.Service.GetFixtureAsync("elsewhere", Guid.NewGuid(), Ct));
        await Should.ThrowAsync<ManagementProjectNotFoundException>(() => world.Service.GetExtensionStatsAsync("elsewhere", Ct));
    }
}
