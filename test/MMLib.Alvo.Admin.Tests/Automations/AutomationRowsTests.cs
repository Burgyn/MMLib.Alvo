using MMLib.Alvo.Admin.Components.Automations;
using System.Text.Json;

namespace MMLib.Alvo.Admin.Tests.Automations;

/// <summary>The descriptor's <c>automation</c> block, as the Automations page reads it.</summary>
public class AutomationRowsTests
{
    [Fact]
    public void An_event_rule_reads_its_trigger()
    {
        var row = Single("""
            {"notify-sales":{"trigger":{"event":"entity.orders.created"},"condition":"new.total > 1000",
              "actions":[{"type":"webhook","endpoint":"crm"},{"type":"entity.update","entity":"orders","recordId":"{{new.id}}","payload":{"flag":true}}]}}
            """);

        row.Name.ShouldBe("notify-sales");
        row.Enabled.ShouldBeTrue();
        row.Trigger.ShouldBe("When entity.orders.created");
        row.Condition.ShouldBe("new.total > 1000");
        row.Actions.ShouldBe([new AutomationActionRow("webhook", "crm"), new AutomationActionRow("entity.update", "orders → record {{new.id}}")]);
        row.DeliveryPrefix.ShouldBe("/automation/notify-sales/actions#");
    }

    [Theory]
    [InlineData("0 2 * * *", "Every day at 02:00 UTC")]
    [InlineData("30 7 * * 1", "Every Monday at 07:30 UTC")]
    [InlineData("0 0 1 * *", "On day 1 of every month at 00:00 UTC")]
    [InlineData("*/15 * * * *", "Every 15 minutes")]
    [InlineData("5 * * * *", "Every hour at minute 5")]
    [InlineData("0 2 1-5 * *", "Cron 0 2 1-5 * * (UTC)")]
    public void A_cron_rule_reads_in_words(string cron, string words) =>
        Single($$$"""{"nightly":{"trigger":{"schedule":"{{{cron}}}"},"actions":[{"type":"function","name":"close-day"}]}}""")
            .Trigger.ShouldBe(words);

    [Fact]
    public void A_disabled_rule_is_marked() =>
        Single("""{"off":{"enabled":false,"trigger":{"event":"entity.orders.deleted"},"actions":[{"type":"email","template":"t","to":"a@b.c"}]}}""")
            .Enabled.ShouldBeFalse();

    [Fact]
    public void A_descriptor_without_automation_has_no_rows() =>
        AutomationRows.From(JsonDocument.Parse("""{"name":"x"}""").RootElement).ShouldBeEmpty();

    private static AutomationRow Single(string automation) =>
        AutomationRows.From(JsonDocument.Parse($"{{\"automation\":{automation}}}").RootElement).ShouldHaveSingleItem();
}
