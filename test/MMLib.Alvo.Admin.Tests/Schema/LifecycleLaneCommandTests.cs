using MMLib.Alvo.Admin.Components.Schema;
using MMLib.Alvo.Management;

namespace MMLib.Alvo.Admin.Tests.Schema;

/// <summary>The Commands lane (spec 2026-10-14 §4.13 dashboard row, HX-08): commands bound to the entity's rows, and the badge counts them.</summary>
public sealed class LifecycleLaneCommandTests
{
    private static readonly ManagementExtension[] _extensions =
    [
        new() { Id = "notify-reschedule", Kind = AlvoExtensionKind.EventHandler, Stage = AlvoExtensionStage.PostCommit, Entities = ["work_orders"], Operations = ["update"] },
        new() { Id = "complete_work_order", Kind = AlvoExtensionKind.Command, Stage = AlvoExtensionStage.Http, Entities = ["work_orders"], Route = "/commands/work-orders/{id:guid}/complete", HttpMethod = "POST", Access = "status == 'in_progress'" },
        new() { Id = "notify_customer", Kind = AlvoExtensionKind.Command, Stage = AlvoExtensionStage.PreTransaction, Access = "'dispatcher' in @user.roles" },
    ];

    [Fact]
    public void The_lane_lists_the_commands_bound_to_the_entity_only() =>
        LifecycleLane.Commands("work_orders", _extensions).Select(command => command.Id).ShouldBe(["complete_work_order"]);

    [Fact]
    public void The_after_commit_lane_never_lists_a_command() =>
        LifecycleLane.AfterCommit("work_orders", _extensions).Select(extension => extension.Id).ShouldBe(["notify-reschedule"]);

    [Fact]
    public void The_badge_counts_a_bound_command_as_code_that_runs_on_the_entity() =>
        LifecycleLane.Binding("work_orders", _extensions).Count.ShouldBe(2);

    [Fact]
    public void The_lane_is_drawn_when_the_host_maps_a_command_on_any_entity() =>
        LifecycleLane.HasMappedCommands(_extensions).ShouldBeTrue();

    [Fact]
    public void The_lane_is_absent_when_the_host_maps_no_command() =>
        LifecycleLane.HasMappedCommands([_extensions[0], _extensions[2]]).ShouldBeFalse("an internal command maps no route, and a handler is no command");

    [Fact]
    public void A_command_s_route_line_is_its_method_and_pattern_or_says_it_is_invoked_in_process()
    {
        LifecycleLane.Route(_extensions[1]).ShouldBe("POST /commands/work-orders/{id:guid}/complete");
        LifecycleLane.Route(_extensions[2]).ShouldBe("invoked in process");
    }
}
