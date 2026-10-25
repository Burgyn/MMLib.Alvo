using MMLib.Alvo.Management;

using System.Text.Json;

namespace MMLib.Alvo.Abstractions.Tests.Management;

/// <summary>The defaults an operator's request starts from, and the shape a fixture travels in.</summary>
public sealed class ManagementOperationsModelsTests
{
    [Fact]
    public void A_replay_replays_dead_deliveries_for_real_unless_told_otherwise()
    {
        var request = new ManagementReplayRequest { Subscriber = "send-confirmation" };

        request.Status.ShouldBe("dead");
        request.DryRun.ShouldBeFalse();
    }

    [Fact]
    public void A_page_holds_fifty_deliveries_unless_asked_for_another_size() =>
        new ManagementDeliveryQuery().Limit.ShouldBe(50);

    [Fact]
    public void A_fixture_reads_back_from_the_json_it_is_downloaded_as()
    {
        var fixture = new DeliveryFixture
        {
            Subscriber = "send-confirmation",
            Delivery = Guid.Parse("0192f1a4-0000-7000-8000-000000000001"),
            Attempt = 3,
            Envelope = JsonDocument.Parse("""{"id":"x","data":{"record":{"title":"<redacted:title>"}}}""").RootElement,
            Redacted = ["/data/record/title"],
        };

        var read = JsonSerializer.Deserialize<DeliveryFixture>(JsonSerializer.Serialize(fixture, JsonSerializerOptions.Web), JsonSerializerOptions.Web)!;

        read.Subscriber.ShouldBe(fixture.Subscriber);
        read.Envelope.GetProperty("data").GetProperty("record").GetProperty("title").GetString().ShouldBe("<redacted:title>");
        read.Redacted.ShouldBe(["/data/record/title"]);
    }

    [Fact]
    public void A_refusal_names_what_was_asked_for_and_what_exists()
    {
        new ManagementExtensionNotFoundException("send-confirmaton", ["send-confirmation"]).Message
            .ShouldBe("This host registers no extension 'send-confirmaton'. It registers: send-confirmation.");
        new ManagementDeliveryNotFoundException(Guid.Empty).Message
            .ShouldBe("No delivery '00000000-0000-0000-0000-000000000000' was recorded. List the deliveries to find its id.");
    }
}
