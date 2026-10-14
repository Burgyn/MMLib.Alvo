using MMLib.Alvo.Data;
using MMLib.Alvo.Events;

namespace MMLib.Alvo.Tests.Events;

/// <summary>
/// Explicit and ambient provenance, merged by <see cref="AlvoEventProvenance.Effective"/> (spec §4.6, HX-01 acceptance 5).
/// </summary>
/// <remarks>
/// Ambient provenance has no production setter in HX-01 — the delivery and timer pumps arrive in HX-03/HX-13 — so these
/// facts set it through the Abstractions grant, which is exactly the seam those pumps will use.
/// </remarks>
public class EffectiveProvenanceTests
{
    private static readonly AlvoContext _caller = new()
    {
        User = UserId.New(),
        Roles = new HashSet<Role> { Role.Authenticated },
    };

    [Fact]
    public void With_nothing_ambient_the_explicit_provenance_is_the_effective_one()
    {
        var context = _caller with { Provenance = new AlvoProvenance { Depth = 2, CorrelationId = "c", CausationId = "e" } };

        AlvoEventProvenance.Effective(context).ShouldBe(context.Provenance);
    }

    [Fact]
    public void An_ambient_depth_beats_a_shallower_explicit_one()
    {
        using var _ = AlvoFlow.Enter(new AlvoProvenance { Depth = 9 });

        AlvoEventProvenance.Effective(_caller).Depth.ShouldBe(9, "a fresh context cannot lower the depth of the flow it runs in");
    }

    [Fact]
    public void An_explicit_depth_beats_a_shallower_ambient_one()
    {
        using var _ = AlvoFlow.Enter(new AlvoProvenance { Depth = 2 });

        AlvoEventProvenance.Effective(_caller with { Provenance = new AlvoProvenance { Depth = 5 } }).Depth.ShouldBe(5);
    }

    [Fact]
    public void Explicit_ids_win_and_ambient_ids_fill_what_is_missing()
    {
        using var _ = AlvoFlow.Enter(new AlvoProvenance { CorrelationId = "ambient-c", CausationId = "ambient-e" });

        var effective = AlvoEventProvenance.Effective(_caller with { Provenance = new AlvoProvenance { CorrelationId = "mine" } });

        effective.CorrelationId.ShouldBe("mine");
        effective.CausationId.ShouldBe("ambient-e");
    }

    [Fact]
    public void The_trace_context_travels_as_a_pair_from_whichever_side_names_a_parent()
    {
        using var _ = AlvoFlow.Enter(new AlvoProvenance { TraceParent = "00-a-b-01", TraceState = "ambient=1" });

        var explicitParent = _caller with { Provenance = new AlvoProvenance { TraceParent = "00-c-d-01" } };
        var effective = AlvoEventProvenance.Effective(explicitParent);

        (effective.TraceParent, effective.TraceState).ShouldBe(("00-c-d-01", null));
        AlvoEventProvenance.Effective(_caller).TraceState.ShouldBe("ambient=1");
    }

    [Fact]
    public void Explicit_depth_zero_under_an_ambient_nine_is_refused()
    {
        using var _ = AlvoFlow.Enter(new AlvoProvenance { Depth = 9 });

        var refused = Should.Throw<AlvoChainDepthExceededException>(() => AlvoChainDepthLimit.Default.EnsureWithin(_caller));

        refused.Message.ShouldContain("9");
        refused.Message.ShouldContain("8");
    }

    [Fact]
    public void Explicit_depth_zero_under_an_ambient_eight_is_accepted()
    {
        using var _ = AlvoFlow.Enter(new AlvoProvenance { Depth = 8 });

        AlvoChainDepthLimit.Default.EnsureWithin(_caller).ShouldBe(8);
    }

    [Fact]
    public void Leaving_a_nested_flow_restores_the_outer_one()
    {
        using (AlvoFlow.Enter(new AlvoProvenance { Depth = 1 }))
        {
            using (AlvoFlow.Enter(new AlvoProvenance { Depth = 4 }))
            {
                AlvoFlow.Provenance!.Depth.ShouldBe(4);
            }

            AlvoFlow.Provenance!.Depth.ShouldBe(1);
        }

        AlvoFlow.Provenance.ShouldBeNull();
    }

    [Fact]
    public async Task The_ambient_flow_follows_an_await_into_its_continuation()
    {
        using var _ = AlvoFlow.Enter(new AlvoProvenance { Depth = 3 });

        var seen = await Task.Run(() => AlvoFlow.Provenance?.Depth);

        seen.ShouldBe(3);
    }

    [Fact]
    public async Task A_flow_entered_inside_a_child_task_does_not_leak_back_to_its_caller()
    {
        await Task.Run(() => AlvoFlow.Enter(new AlvoProvenance { Depth = 7 }));

        AlvoFlow.Provenance.ShouldBeNull();
    }

    [Fact]
    public void A_negative_maximum_is_refused()
        => Should.Throw<ArgumentOutOfRangeException>(() => new AlvoChainDepthLimit(-1));
}
