using MMLib.Alvo.Admin.Components.Schema;
using MMLib.Alvo.Management;

namespace MMLib.Alvo.Admin.Tests.Schema;

/// <summary>What the Lifecycle tab's write lanes draw (spec 2026-10-14 §4.13, Ruling 29), and the editors' note.</summary>
public sealed class LifecycleLaneWriteHookTests
{
    private static readonly IReadOnlyList<ManagementExtension> _extensions =
    [
        Hook("check-region", AlvoExtensionKind.Validate, AlvoExtensionStage.PreTransaction, ["work_orders"], ["create", "update"]) with { TopLevelOnly = true },
        Hook("stamp", AlvoExtensionKind.BeforeWrite, AlvoExtensionStage.InTransaction, ["work_orders"], ["create", "update"]),
        Hook("audit", AlvoExtensionKind.AfterWrite, AlvoExtensionStage.InTransaction, ["*"], ["create", "update"]),
        Hook("tidy", AlvoExtensionKind.AfterDelete, AlvoExtensionStage.InTransaction, ["work_orders"], ["delete"]),
        Hook("notify", AlvoExtensionKind.EventHandler, AlvoExtensionStage.PostCommit, ["work_orders"], ["update"]),
    ];

    [Fact]
    public void The_before_lane_holds_validate_hooks_and_the_in_lane_every_in_transaction_hook_every_entity_ones_included()
    {
        LifecycleLane.BeforeTransaction("work_orders", _extensions).Select(item => item.Id).ShouldBe(["check-region"]);
        LifecycleLane.InTransaction("work_orders", _extensions).Select(item => item.Id).ShouldBe(["stamp", "audit", "tidy"]);
        LifecycleLane.AfterCommit("work_orders", _extensions).Select(item => item.Id).ShouldBe(["notify"]);
    }

    [Fact]
    public void The_lanes_are_drawn_only_in_a_host_that_registers_a_write_hook()
    {
        LifecycleLane.HasWriteHooks(_extensions).ShouldBeTrue();
        LifecycleLane.HasWriteHooks([_extensions[^1]]).ShouldBeFalse();
    }

    [Fact]
    public void A_code_item_s_numbers_are_its_p95_and_its_rejections_over_a_day_and_nothing_without_stats()
    {
        var stats = new ManagementExtensionStats
        {
            Scope = "this-instance",
            Window = TimeSpan.FromHours(24),
            Since = DateTimeOffset.UnixEpoch,
            Items = [new ManagementExtensionStat { Id = "stamp", Invocations = 40, Rejections = 3, P95 = TimeSpan.FromMilliseconds(12) }],
        };

        LifecycleLane.Numbers(_extensions[1], stats).ShouldBe("p95 12 ms · 3 rejections in 24 h");
        LifecycleLane.Numbers(_extensions[1], null).ShouldBeNull();
        LifecycleLane.Numbers(_extensions[0], stats).ShouldBeNull();
    }

    [Fact]
    public void An_event_handler_has_no_numbers_even_when_the_stats_list_it()
    {
        var stats = new ManagementExtensionStats
        {
            Scope = "this-instance",
            Window = TimeSpan.FromHours(24),
            Since = DateTimeOffset.UnixEpoch,
            Items = [new ManagementExtensionStat { Id = "notify", Invocations = 4, Rejections = 0, P95 = TimeSpan.FromMilliseconds(9) }],
        };

        LifecycleLane.Numbers(_extensions[^1], stats).ShouldBeNull("rejections are a write hook's, and the numbers belong to the write lanes");
    }

    [Fact]
    public void The_editors_note_does_not_count_a_switched_off_hook() =>
        LifecycleLane.AlsoRuns("work_orders", "create", [.. _extensions.Select(item => item.Id == "check-region" ? item with { Enabled = false } : item)])
            .ShouldBe("2 extensions from code also run on this operation");

    [Theory]
    [InlineData("create", "3 extensions from code also run on this operation")]
    [InlineData("delete", "1 extension from code also runs on this operation")]
    [InlineData("list", null)]
    public void The_editors_note_counts_the_write_hooks_of_one_operation(string operation, string? note) =>
        LifecycleLane.AlsoRuns("work_orders", operation, _extensions).ShouldBe(note);

    [Fact]
    public void The_preview_lists_the_code_bound_to_any_entity_the_change_touches_once()
    {
        LifecycleLane.Touching(["work_orders", "work_orders"], _extensions).Select(item => item.Id).ShouldBe(["check-region", "stamp", "audit", "tidy", "notify"]);
        LifecycleLane.Touching(["regions"], _extensions).Select(item => item.Id).ShouldBe(["audit"]);
        LifecycleLane.Touching([""], _extensions).ShouldBeEmpty("a plan line the preview does not recognise names no entity, so no every-entity hook either");
    }

    [Fact]
    public void Each_kind_has_a_stage_label() =>
        _extensions.Select(LifecycleLane.StageLabel).ShouldBe(["validate", "before write", "after write", "after delete", "after commit"]);

    private static ManagementExtension Hook(string id, AlvoExtensionKind kind, AlvoExtensionStage stage, string[] entities, string[] operations) =>
        new() { Id = id, Kind = kind, Stage = stage, Entities = entities, Operations = operations };
}
