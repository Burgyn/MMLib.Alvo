using MMLib.Alvo.Admin.Components.Schema;
using MMLib.Alvo.Management;

namespace MMLib.Alvo.Admin.Tests.Schema;

/// <summary>The Lifecycle tab's After commit lane and the entity list's badge, as logic.</summary>
public sealed class LifecycleLaneTests
{
    private static readonly ManagementExtension _reschedule = Extension("notify-reschedule", ["work_orders"], ["update"]) with
    {
        When = "changed(scheduled_for)",
        Timeout = TimeSpan.FromSeconds(30),
        RunAs = "system",
        Ordering = "perRecord",
    };

    private static readonly ManagementExtension _every = Extension("audit-every-save", ["*"], ["create", "update"]) with
    {
        Timeout = TimeSpan.FromMinutes(2),
        RunAs = "eventActor",
        Ordering = "none",
    };

    private static readonly ManagementExtension _application = Extension("fleet-alerted", [], []) with { Event = "fleet.alerted" };

    [Fact]
    public void An_entity_is_bound_by_its_own_extensions_and_by_every_entity_ones()
    {
        IReadOnlyList<ManagementExtension> all = [_reschedule, _every, _application];

        LifecycleLane.Binding("work_orders", all).Select(item => item.Id).ShouldBe(["notify-reschedule", "audit-every-save"]);
        LifecycleLane.Binding("regions", all).Select(item => item.Id).ShouldBe(["audit-every-save"]);
    }

    [Fact]
    public void The_after_commit_lane_takes_the_post_commit_extensions_of_the_entity()
        => LifecycleLane.AfterCommit("work_orders", [_reschedule, _every, _application]).Count.ShouldBe(2);

    [Fact]
    public void The_lane_takes_only_the_after_hooks_numbered_from_one()
    {
        IReadOnlyList<KeyValuePair<string, string>> hooks =
        [
            new("beforeUpdate", """[{"action":{"reject":"no"}}]"""),
            new("afterUpdate", """[{"action":{"type":"webhook","endpoint":"desk"}},{"condition":"changed(status)","action":{"type":"webhook","endpoint":"desk"}}]"""),
            new("afterDelete", "[]"),
        ];

        var lane = LifecycleLane.AfterCommitHooks(hooks);

        lane.Select(hook => (hook.Point, hook.Number)).ShouldBe([("afterUpdate", 1), ("afterUpdate", 2)]);
        lane[1].Json.ShouldContain("changed(status)");
        lane[1].Json.ShouldContain("\n", Case.Sensitive, "the lane draws a hook as the Hooks tab does, written readable");
    }

    [Fact]
    public void A_malformed_hook_list_draws_nothing_rather_than_throwing()
        => LifecycleLane.AfterCommitHooks([new("afterUpdate", "[{not json")]).ShouldBeEmpty();

    [Fact]
    public void A_summary_names_an_unknown_identity_or_ordering_as_declared()
        => LifecycleLane.Summary(_application with { RunAs = "tenantOwner", Ordering = "perTenant" })
            .ShouldBe("runs as tenantOwner · ordering perTenant");

    [Fact]
    public void A_summary_says_where_when_how_long_and_as_whom()
    {
        LifecycleLane.Summary(_reschedule).ShouldBe("on update · timeout 30 s · runs as the system · in order per record");
        LifecycleLane.Summary(_every).ShouldBe("every entity · on create or update · timeout 2 min · runs as the event's actor · in any order");
    }

    [Theory]
    [InlineData(1, "1 extension from code runs on this entity")]
    [InlineData(2, "2 extensions from code run on this entity")]
    public void The_badge_label_counts_in_words(int count, string label)
        => LifecycleLane.BadgeLabel(count).ShouldBe(label);

    private static ManagementExtension Extension(string id, IReadOnlyList<string> entities, IReadOnlyList<string> operations) => new()
    {
        Id = id,
        Kind = AlvoExtensionKind.EventHandler,
        Stage = AlvoExtensionStage.PostCommit,
        Entities = entities,
        Operations = operations,
    };
}
