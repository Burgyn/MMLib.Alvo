using MMLib.Alvo.Events;
using MMLib.Alvo.Management;

namespace MMLib.Alvo.Tests.Management;

/// <summary>The operations port's reads: a page and one delivery.</summary>
public sealed class AlvoOperationsServiceTests
{
    private const string Project = OperationsTestWorld.Project;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task A_page_lists_newest_first_and_hands_over_the_next_cursor()
    {
        using var world = new OperationsTestWorld();
        var first = await world.DeliveryAsync("tickets:a");
        var second = await world.DeliveryAsync("tickets:b");

        var page = await world.Service.ListDeliveriesAsync(Project, new ManagementDeliveryQuery { Limit = 1 }, Ct);
        var rest = await world.Service.ListDeliveriesAsync(Project, new ManagementDeliveryQuery { Limit = 1, After = page.Next }, Ct);

        page.Items.ShouldHaveSingleItem().Id.ShouldBe(second.Id);
        page.Next.ShouldBe(second.Id);
        rest.Items.ShouldHaveSingleItem().Id.ShouldBe(first.Id);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(201)]
    public async Task A_page_size_outside_one_to_two_hundred_is_refused(int limit)
    {
        using var world = new OperationsTestWorld();

        (await Should.ThrowAsync<ManagementRequestException>(() => world.Service.ListDeliveriesAsync(Project, new ManagementDeliveryQuery { Limit = limit }, Ct)))
            .Message.ShouldContain("1 to 200");
    }

    [Fact]
    public async Task An_unknown_status_is_refused_naming_the_known_ones()
    {
        using var world = new OperationsTestWorld();

        (await Should.ThrowAsync<ManagementRequestException>(() => world.Service.ListDeliveriesAsync(Project, new ManagementDeliveryQuery { Status = "failed" }, Ct)))
            .Message.ShouldContain("pending, running, succeeded, retrying, dead, cancelled");
    }

    [Fact]
    public async Task A_window_that_ends_before_it_starts_is_refused()
    {
        using var world = new OperationsTestWorld();
        var now = world.Clock.GetUtcNow();

        await Should.ThrowAsync<ManagementRequestException>(() =>
            world.Service.ListDeliveriesAsync(Project, new ManagementDeliveryQuery { From = now, To = now.AddMinutes(-1) }, Ct));
    }

    [Fact]
    public async Task One_delivery_reads_with_its_attempts()
    {
        using var world = new OperationsTestWorld();
        var delivery = await world.DeliveryAsync("tickets:a");
        var claimed = (await world.Store.ClaimAsync(new DeliveryClaim { Claimant = "w", Ordered = [OperationsTestWorld.Handler], BatchSize = 1, Lease = TimeSpan.FromMinutes(1), OutboxMaxAttempts = 10, OutboxClaimLease = TimeSpan.FromMinutes(5) }, Ct)).Single();
        await world.Store.CompleteAsync(new DeliveryAttempt { Delivery = claimed, Claimant = "w", StartedAt = world.Clock.GetUtcNow(), Duration = TimeSpan.FromMilliseconds(12) }, Ct);

        var read = await world.Service.GetDeliveryAsync(Project, delivery.Id, Ct);

        read.Status.ShouldBe("succeeded");
        read.History.ShouldHaveSingleItem().Outcome.ShouldBe("succeeded");
        read.History[0].Duration.ShouldBe(TimeSpan.FromMilliseconds(12));
    }

    [Fact]
    public async Task An_unknown_delivery_is_not_found()
    {
        using var world = new OperationsTestWorld();

        await Should.ThrowAsync<ManagementDeliveryNotFoundException>(() => world.Service.GetDeliveryAsync(Project, Guid.NewGuid(), Ct));
    }

    [Fact]
    public async Task Every_read_refuses_an_unpublished_caller()
    {
        using var world = new OperationsTestWorld(admitted: false);

        await Should.ThrowAsync<ManagementForbiddenException>(() => world.Service.ListDeliveriesAsync(Project, new ManagementDeliveryQuery(), Ct));
        await Should.ThrowAsync<ManagementForbiddenException>(() => world.Service.GetDeliveryAsync(Project, Guid.NewGuid(), Ct));
    }
}
