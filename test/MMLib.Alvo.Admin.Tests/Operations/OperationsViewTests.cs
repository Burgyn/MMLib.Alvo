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
}
