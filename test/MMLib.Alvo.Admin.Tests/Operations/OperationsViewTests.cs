using MMLib.Alvo.Admin.Components.Operations;
using MMLib.Alvo.Management;

using System.Text.Json;

namespace MMLib.Alvo.Admin.Tests.Operations;

/// <summary>The Operations page's words and tones, as logic.</summary>
public sealed class OperationsViewTests
{
    [Theory]
    [InlineData("succeeded", "a-badge a-badge--ok")]
    [InlineData("dead", "a-badge a-badge--danger")]
    [InlineData("retrying", "a-badge a-badge--warn")]
    [InlineData("pending", "a-badge a-badge--accent")]
    [InlineData("running", "a-badge a-badge--accent")]
    [InlineData("cancelled", "a-badge")]
    public void A_status_has_its_tone(string status, string css) => OperationsView.BadgeClass(status).ShouldBe(css);

    [Theory]
    [InlineData(null, "—")]
    [InlineData(0.4, "0.4 ms")]
    [InlineData(12.0, "12 ms")]
    [InlineData(1_250.0, "1.25 s")]
    [InlineData(90_000.0, "1.5 min")]
    public void A_duration_reads_in_its_own_unit(double? milliseconds, string text) =>
        OperationsView.Duration(milliseconds is { } value ? TimeSpan.FromMilliseconds(value) : null).ShouldBe(text);

    [Fact]
    public void A_handler_s_runs_say_how_many_how_many_failed_and_how_long_on_this_instance()
    {
        OperationsView.RunsLine(null).ShouldBe("no runs on this instance in the last 24 hours");
        OperationsView.RunsLine(new ManagementExtensionStat { Id = "x", Invocations = 42, Failures = 3, P50 = TimeSpan.FromMilliseconds(12), P95 = TimeSpan.FromMilliseconds(80) })
            .ShouldBe("42 runs · 3 failed · p50 12 ms · p95 80 ms — this instance, last 24 hours");
    }

    [Fact]
    public void An_attempt_line_names_its_number_outcome_error_and_duration()
    {
        OperationsView.AttemptLine(new ManagementDeliveryAttempt { Attempt = 1, StartedAt = DateTimeOffset.UnixEpoch, Duration = TimeSpan.FromSeconds(30), Outcome = "failed", ErrorType = "timeout" })
            .ShouldBe("#1 · failed · timeout · 30 s");
        OperationsView.AttemptLine(new ManagementDeliveryAttempt { Attempt = 2, StartedAt = DateTimeOffset.UnixEpoch, Duration = TimeSpan.FromMilliseconds(12), Outcome = "succeeded" })
            .ShouldBe("#2 · succeeded · 12 ms");
    }

    [Fact]
    public void A_replay_asks_with_the_count_it_will_run()
    {
        OperationsView.ReplayQuestion(new ManagementReplayPlan { DryRun = true, Count = 3, Ids = [] }, "send-confirmation")
            .ShouldBe("Run 3 dead deliveries of send-confirmation again?");
        OperationsView.ReplayQuestion(new ManagementReplayPlan { DryRun = true, Count = 1, Ids = [] }, "send-confirmation")
            .ShouldBe("Run 1 dead delivery of send-confirmation again?");
    }

    [Theory]
    [InlineData("dead", true)]
    [InlineData("succeeded", true)]
    [InlineData("cancelled", true)]
    [InlineData("pending", false)]
    [InlineData("running", false)]
    [InlineData("retrying", false)]
    public void Only_a_finished_delivery_offers_a_redeliver(string status, bool offered) =>
        OperationsView.CanRedeliver(Delivery(status)).ShouldBe(offered);

    [Fact]
    public void A_fixture_is_shown_as_the_json_it_downloads_as()
    {
        var fixture = new DeliveryFixture
        {
            Subscriber = "send-confirmation",
            Delivery = Guid.Empty,
            Attempt = 1,
            Envelope = JsonDocument.Parse("""{"id":"e"}""").RootElement,
        };

        var json = OperationsView.FixtureJson(fixture);

        json.ShouldContain("\"subscriber\": \"send-confirmation\"");
        JsonSerializer.Deserialize<DeliveryFixture>(json, JsonSerializerOptions.Web)!.Subscriber.ShouldBe("send-confirmation");
    }

    [Fact]
    public void A_schedule_row_reads_next_run_in_its_zone_and_last_outcome()
    {
        var now = new DateTimeOffset(2026, 11, 6, 22, 0, 0, TimeSpan.Zero);
        var schedule = Schedule() with
        {
            NextRunAt = new DateTimeOffset(2026, 11, 7, 1, 0, 0, TimeSpan.Zero),
            LastRunAt = new DateTimeOffset(2026, 11, 6, 1, 0, 0, TimeSpan.Zero),
            LastOutcome = "dead",
        };

        OperationsView.When(schedule).ShouldBe("0 2 * * * · Europe/Bratislava");
        OperationsView.NextRun(schedule, now).ShouldBe("in 3 h (02:00 Europe/Bratislava)");
        OperationsView.LastRun(schedule, now).ShouldBe("21 h ago");
        OperationsView.BadgeClass(schedule.LastOutcome!).ShouldBe("a-badge a-badge--danger");
        OperationsView.CanRunNow(schedule).ShouldBeTrue();
    }

    [Fact]
    public void A_schedule_that_never_ran_says_so_and_a_due_one_says_due()
    {
        var now = new DateTimeOffset(2026, 11, 6, 22, 0, 0, TimeSpan.Zero);
        var schedule = Schedule() with { NextRunAt = now.AddMinutes(-1) };

        OperationsView.NextRun(schedule, now).ShouldBe("due now (22:59 Europe/Bratislava)");
        OperationsView.LastRun(schedule, now).ShouldBe("never");
        OperationsView.NextRun(Schedule(), now).ShouldBe("—");
    }

    [Fact]
    public void A_zone_this_host_does_not_know_reads_in_utc_rather_than_breaking_the_page()
    {
        var now = new DateTimeOffset(2026, 11, 6, 22, 0, 0, TimeSpan.Zero);
        var schedule = Schedule() with { TimeZone = "Mars/Olympus", NextRunAt = now.AddHours(3) };

        OperationsView.NextRun(schedule, now).ShouldBe("in 3 h (01:00 UTC — Mars/Olympus is unknown here)");
    }

    [Fact]
    public void A_migration_row_reads_its_revision_and_never_offers_run_now()
    {
        var migration = new ManagementExtension
        {
            Id = "backfill-codes",
            Kind = AlvoExtensionKind.DataMigration,
            Stage = AlvoExtensionStage.Scheduled,
            AfterRevision = 3,
        };

        OperationsView.When(migration).ShouldBe("after revision 3");
        OperationsView.NextRun(migration, DateTimeOffset.UnixEpoch).ShouldBe("—");
        OperationsView.CanRunNow(migration).ShouldBeFalse();
    }

    [Fact]
    public void The_timer_tile_counts_pending_and_due_or_says_the_host_keeps_none()
    {
        OperationsView.TimersLine(new ManagementTimerCounts { Pending = 4, Due = 1 }).ShouldBe("4 pending · 1 due");
        OperationsView.TimersLine(null).ShouldBe("this host keeps no timers");
    }

    [Fact]
    public void A_sleeping_delivery_reads_sleeping_until_its_wake_time()
    {
        var now = new DateTimeOffset(2026, 11, 5, 18, 0, 0, TimeSpan.Zero);
        var sleeping = Delivery("pending") with
        {
            NotBefore = new DateTimeOffset(2026, 11, 6, 9, 0, 0, TimeSpan.Zero),
            History = [Attempt(1, "skipped")],
        };

        OperationsView.StatusLabel(sleeping, now).ShouldBe("sleeping until 6 Nov 09:00");
        OperationsView.BadgeClass(sleeping, now).ShouldBe("a-badge a-badge--accent");
        OperationsView.IsSleeping(sleeping, now).ShouldBeTrue();
        OperationsView.StatusLabel(sleeping, sleeping.NotBefore).ShouldBe("pending");
        OperationsView.StatusLabel(sleeping with { History = [Attempt(1, "failed")] }, now).ShouldBe("pending");
        OperationsView.StatusLabel(sleeping with { History = [] }, now).ShouldBe("pending");
        OperationsView.StatusLabel(sleeping with { Status = "retrying" }, now).ShouldBe("retrying");
    }

    [Fact]
    public void A_superseded_delivery_has_its_own_chip_and_no_redeliver()
    {
        var superseded = Delivery("superseded");

        OperationsView.StatusLabel(superseded, DateTimeOffset.UnixEpoch).ShouldBe("superseded");
        OperationsView.BadgeClass(superseded, DateTimeOffset.UnixEpoch).ShouldBe("a-badge");
        OperationsView.CanRedeliver(superseded).ShouldBeFalse();
        OperationsView.StatusFilters.ShouldContain("superseded");
    }

    [Fact]
    public void A_limited_handler_row_shows_its_concurrency()
    {
        var handler = new ManagementExtension { Id = "confirm-booking", Kind = AlvoExtensionKind.EventHandler, Stage = AlvoExtensionStage.PostCommit };

        OperationsView.ConcurrencyLine(handler with { Concurrency = "tenant:2" }).ShouldBe("per tenant · 2");
        OperationsView.ConcurrencyLine(handler with { Concurrency = "record:1" }).ShouldBe("per record · 1");
        OperationsView.ConcurrencyLine(handler).ShouldBeNull();
    }

    [Fact]
    public void A_delivery_s_steps_read_as_name_and_completion_time()
    {
        var delivery = Delivery("retrying") with
        {
            Steps =
            [
                new ManagementDeliveryStep { Name = "charge-deposit", CompletedAt = new DateTimeOffset(2026, 11, 6, 9, 0, 12, TimeSpan.Zero) },
                new ManagementDeliveryStep { Name = "send-confirmation", CompletedAt = new DateTimeOffset(2026, 11, 6, 9, 0, 13, TimeSpan.Zero) },
            ],
        };

        OperationsView.Steps(delivery).ShouldBe(["charge-deposit · 6 Nov 09:00:12", "send-confirmation · 6 Nov 09:00:13"]);
        OperationsView.Steps(Delivery("pending")).ShouldBeEmpty();
    }

    private static ManagementExtension Schedule() => new()
    {
        Id = "nightly-roll",
        Kind = AlvoExtensionKind.Schedule,
        Stage = AlvoExtensionStage.Scheduled,
        Cron = "0 2 * * *",
        TimeZone = "Europe/Bratislava",
    };

    private static ManagementDelivery Delivery(string status) => new()
    {
        Id = Guid.Empty,
        EventId = Guid.Empty,
        Subscriber = "send-confirmation",
        PartitionKey = "tickets:1",
        Status = status,
        Attempts = 1,
        NotBefore = DateTimeOffset.UnixEpoch,
    };

    private static ManagementDeliveryAttempt Attempt(int number, string outcome) => new()
    {
        Attempt = number,
        StartedAt = DateTimeOffset.UnixEpoch,
        Duration = TimeSpan.FromMilliseconds(5),
        Outcome = outcome,
    };
}
