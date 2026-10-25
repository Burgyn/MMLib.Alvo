using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Time.Testing;

using MMLib.Alvo.Events.Internal;
using MMLib.Alvo.Extensions;
using MMLib.Alvo.Extensions.Internal;

using static MMLib.Alvo.Tests.Extensions.HandlerCatalog;

namespace MMLib.Alvo.Tests.Extensions;

/// <summary>Stats state their scope (spec 2026-10-14 HX-06 acceptance (8)) and count the last 24 hours of this process.</summary>
public sealed class ExtensionStatsTests
{
    private static readonly DateTimeOffset _start = new(2026, 10, 24, 9, 30, 0, TimeSpan.Zero);

    /// <summary>
    /// Every fact names an extension of its own: Alvo's meters are process-wide, so a handler id another test's deliveries
    /// also use would be counted here too.
    /// </summary>
    private readonly string _id = $"stats-{Guid.NewGuid():N}";

    [Fact]
    public void A_report_says_it_covers_this_instance_over_a_day()
    {
        using var stats = Stats(new FakeTimeProvider(_start));

        var report = stats.Report([_id]);

        report.Scope.ShouldBe("this-instance");
        report.Window.ShouldBe(TimeSpan.FromHours(24));
        report.Since.ShouldBe(_start, "nothing earlier than this process's start is counted");
        report.Items.ShouldHaveSingleItem().ShouldBe(new() { Id = _id });
    }

    [Fact]
    public void Runs_and_failures_are_counted_per_extension_with_their_percentiles()
    {
        using var stats = Stats(new FakeTimeProvider(_start), $"{_id}-other");
        foreach (var duration in Enumerable.Range(1, 20))
        {
            stats.Record(_id, duration, failed: duration > 18);
        }

        var report = stats.Report([_id, $"{_id}-other"]);
        var item = report.Items[0];

        item.Invocations.ShouldBe(20);
        item.Failures.ShouldBe(2);
        item.Rejections.ShouldBe(0);
        item.P50!.Value.TotalMilliseconds.ShouldBeInRange(10, 12);
        item.P95!.Value.TotalMilliseconds.ShouldBeInRange(19, 23);
        report.Items[1].ShouldBe(new() { Id = $"{_id}-other" });
    }

    [Fact]
    public void A_run_older_than_a_day_is_no_longer_counted()
    {
        var clock = new FakeTimeProvider(_start);
        using var stats = Stats(clock);
        stats.Record(_id, 5, failed: false);

        clock.Advance(TimeSpan.FromHours(23));
        stats.Report([_id]).Items[0].Invocations.ShouldBe(1);
        clock.Advance(TimeSpan.FromHours(2));
        stats.Report([_id]).Items[0].Invocations.ShouldBe(0);
    }

    [Fact]
    public void A_delivery_s_duration_measurement_reaches_the_report()
    {
        using var stats = Stats(new FakeTimeProvider(_start));

        AlvoDeliveryMetrics.Durations.Record(
            42,
            new KeyValuePair<string, object?>(AlvoDeliveryMetrics.SubscriberTag, _id),
            new KeyValuePair<string, object?>(AlvoDeliveryMetrics.OutcomeTag, AlvoDeliveryMetrics.DeadOutcome));

        var item = stats.Report([_id]).Items[0];
        item.Invocations.ShouldBe(1);
        item.Failures.ShouldBe(1);
    }

    [Fact]
    public void A_measurement_for_a_subscriber_this_host_does_not_register_is_ignored()
    {
        var stranger = $"{_id}-stranger";
        using var stats = Stats(new FakeTimeProvider(_start));

        AlvoDeliveryMetrics.Durations.Record(
            42, new KeyValuePair<string, object?>(AlvoDeliveryMetrics.SubscriberTag, stranger));
        stats.Record(stranger, 7, failed: true);

        stats.Report([stranger]).Items[0].ShouldBe(new() { Id = stranger });
    }

    [Fact]
    public async Task A_stopped_host_stops_listening()
    {
        using var stats = Stats(new FakeTimeProvider(_start));

        await stats.StopAsync(TestContext.Current.CancellationToken);
        AlvoDeliveryMetrics.Durations.Record(
            42, new KeyValuePair<string, object?>(AlvoDeliveryMetrics.SubscriberTag, _id));

        stats.Report([_id]).Items[0].Invocations.ShouldBe(0);
    }

    [Fact]
    public void The_container_holds_one_instance_that_is_also_a_hosted_service()
    {
        var services = new ServiceCollection();
        services.AddSingleton<TimeProvider>(new FakeTimeProvider(_start));
        services.AddSingleton(ExtensionRegistry.Empty);
        services.AddAlvoExtensions();
        using var provider = services.BuildServiceProvider();

        provider.GetServices<IHostedService>().ShouldContain(provider.GetRequiredService<ExtensionStats>());
    }

    private ExtensionStats Stats(TimeProvider clock, params string[] others) =>
        new(clock, Registry(alvo => alvo.Entity("tickets", tickets => Register(tickets, [_id, .. others]))));

    private static void Register(AlvoEntityBuilder tickets, IEnumerable<string> ids)
    {
        foreach (var id in ids)
        {
            tickets.OnCreated(id, OnCreated);
        }
    }
}
