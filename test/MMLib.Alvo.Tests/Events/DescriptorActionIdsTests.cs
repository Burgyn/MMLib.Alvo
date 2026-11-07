using MMLib.Alvo.Descriptor;
using MMLib.Alvo.Events.Internal;

using System.Text.Json;

namespace MMLib.Alvo.Tests.Events;

/// <summary>A descriptor action's subscriber id: its point, <c>#</c>, and a hash of what it does — stable across processes.</summary>
public sealed class DescriptorActionIdsTests
{
    private static readonly AutomationAction _webhook = Action("""{ "type": "webhook", "endpoint": "erp" }""");

    [Fact]
    public void A_subscriber_id_is_stable_across_processes_and_changes_with_the_action()
    {
        var id = DescriptorActionIds.Hook("/entities/orders/hooks/afterCreate", "new.total > 1.0", _webhook);

        id.ShouldBe("/entities/orders/hooks/afterCreate#e40e8b74", "the hash is pinned, so a second process computes the same id");
        DescriptorActionIds.Hook("/entities/orders/hooks/afterCreate", "new.total > 1.0", Action("""{ "type": "webhook", "endpoint": "erp" }""")).ShouldBe(id);
        DescriptorActionIds.Hook("/entities/orders/hooks/afterCreate", "new.total > 2.0", _webhook).ShouldNotBe(id);
        DescriptorActionIds.Hook("/entities/orders/hooks/afterCreate", "new.total > 1.0", Action("""{ "type": "webhook", "endpoint": "crm" }""")).ShouldNotBe(id);
    }

    [Fact]
    public void A_rule_action_and_a_cron_schedule_hash_their_trigger()
    {
        var nightly = new AutomationTrigger { Schedule = "0 2 * * *" };
        var hourly = new AutomationTrigger { Schedule = "0 * * * *" };

        DescriptorActionIds.Schedule("nightly-close", nightly).ShouldStartWith("/automation/nightly-close#");
        DescriptorActionIds.Schedule("nightly-close", nightly).ShouldNotBe(DescriptorActionIds.Schedule("nightly-close", hourly));
        DescriptorActionIds.RuleAction("r", nightly, null, _webhook).ShouldNotBe(DescriptorActionIds.RuleAction("r", hourly, null, _webhook));
    }

    [Fact]
    public void An_exact_duplicate_gets_an_ordinal_and_only_a_descriptor_action_starts_with_a_slash()
    {
        var taken = new HashSet<string>(StringComparer.Ordinal);

        DescriptorActionIds.Unique("/p#abc", taken).ShouldBe("/p#abc");
        DescriptorActionIds.Unique("/p#abc", taken).ShouldBe("/p#abc-2");
        DescriptorActionIds.Unique("/p#abc", taken).ShouldBe("/p#abc-3");
        DescriptorActionIds.IsDescriptorAction("/p#abc").ShouldBeTrue();
        DescriptorActionIds.IsDescriptorAction("send-invoice").ShouldBeFalse();
    }

    private static AutomationAction Action(string json) =>
        AlvoDescriptor.Parse($$"""{ "apiVersion": "alvo.dev/v1", "name": "x", "entities": {}, "automation": { "r": { "trigger": { "event": "entity.o.created" }, "actions": [{{json}}] } } }""")
            .Automation!["r"].Actions[0];
}
