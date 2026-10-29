using MMLib.Alvo.Data;

namespace MMLib.Alvo.Tests.Data;

/// <summary>The open unit on the flow (spec 2026-10-14 §4.5, R-B1): carried beside provenance, refused by every port entry.</summary>
public sealed class AlvoFlowUnitTests
{
    [Fact]
    public void No_unit_is_on_a_flow_nothing_entered()
        => AlvoFlow.Unit.ShouldBeNull();

    [Fact]
    public void An_entered_unit_is_on_the_flow_with_its_provenance_until_the_scope_ends()
    {
        var unit = new FakeFlowUnit();

        using (AlvoFlow.EnterUnit(unit, new AlvoProvenance { Depth = 3, CorrelationId = "c-1" }))
        {
            AlvoFlow.Unit.ShouldBeSameAs(unit);
            AlvoFlow.Provenance!.Depth.ShouldBe(3);
        }

        AlvoFlow.Unit.ShouldBeNull();
        AlvoFlow.Provenance.ShouldBeNull();
    }

    [Fact]
    public void Entering_a_delivery_s_provenance_inside_a_unit_keeps_the_unit()
    {
        var unit = new FakeFlowUnit();
        using var _ = AlvoFlow.EnterUnit(unit, new AlvoProvenance { Depth = 1 });

        using (AlvoFlow.Enter(new AlvoProvenance { Depth = 4 }))
        {
            AlvoFlow.Unit.ShouldBeSameAs(unit);
        }

        AlvoFlow.Provenance!.Depth.ShouldBe(1);
    }

    [Fact]
    public async Task A_unit_that_has_ended_is_not_on_the_flow()
    {
        var unit = new FakeFlowUnit();
        Task<IAlvoFlowUnit?> outliving;
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        using (AlvoFlow.EnterUnit(unit, AlvoProvenance.None))
        {
            outliving = Task.Run(async () =>
            {
                await release.Task;
                return AlvoFlow.Unit;
            }, TestContext.Current.CancellationToken);
        }

        unit.IsOpen = false;
        release.SetResult();

        (await outliving).ShouldBeNull("work that outlives its unit inherits the context but not the refusal");
    }

    [Fact]
    public void A_call_inside_an_open_unit_is_refused_and_poisons_it()
    {
        var unit = new FakeFlowUnit();
        using var _ = AlvoFlow.EnterUnit(unit, AlvoProvenance.None);

        var refusal = Should.Throw<AlvoUnitInProgressException>(() => AlvoFlow.RefuseInsideUnit("IAlvoData.CreateAsync"));

        refusal.Message.ShouldStartWith("IAlvoData.CreateAsync was called while a transaction unit is open on this flow");
        refusal.ShouldBeAssignableTo<InvalidOperationException>();
        unit.Poisoned.ShouldHaveSingleItem().ShouldBeSameAs(refusal);
    }

    [Fact]
    public void A_keyed_call_inside_an_open_unit_is_an_argument_exception_and_poisons_it()
    {
        var unit = new FakeFlowUnit();
        using var _ = AlvoFlow.EnterUnit(unit, AlvoProvenance.None);

        var refusal = Should.Throw<ArgumentException>(() => AlvoFlow.RefuseInsideUnit("IAlvoData.CreateAsync", keyed: true));

        refusal.ParamName.ShouldBe("idempotency");
        refusal.Message.ShouldContain("idempotency key");
        unit.Poisoned.ShouldHaveSingleItem().ShouldBeSameAs(refusal);
    }

    [Fact]
    public void A_call_outside_any_unit_is_let_through()
        => Should.NotThrow(() => AlvoFlow.RefuseInsideUnit("IAlvoData.CreateAsync", keyed: true));
}
