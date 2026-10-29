using MMLib.Alvo.Data;
using MMLib.Alvo.Events;
using MMLib.Alvo.Events.Internal;
using MMLib.Alvo.Tests.Data;
using NSubstitute;

namespace MMLib.Alvo.Tests.Events;

/// <summary><c>IAlvoEvents.PublishAsync</c> stays autocommit (spec 2026-10-14 X13), so inside an open unit it is refused (R-B1).</summary>
public sealed class AlvoEventsUnitTests
{
    private static readonly AlvoContext _caller = new() { User = UserId.New(), Roles = new HashSet<Role> { Role.Authenticated } };

    [Fact]
    public async Task Publishing_inside_an_open_unit_is_refused_and_fails_the_unit()
    {
        var outbox = Substitute.For<IOutboxStore>();
        var events = new AlvoEvents(outbox, TimeProvider.System, AlvoChainDepthLimit.Default);
        var unit = new FakeFlowUnit();
        using var _ = AlvoFlow.EnterUnit(unit, AlvoProvenance.None);

        var refusal = await Should.ThrowAsync<AlvoUnitInProgressException>(() => events.PublishAsync(
            "orders.approved", "orders/1", null, _caller, TestContext.Current.CancellationToken));

        refusal.Message.ShouldStartWith("IAlvoEvents.PublishAsync was called while a transaction unit is open");
        unit.Poisoned.ShouldHaveSingleItem().ShouldBeSameAs(refusal);
        await outbox.DidNotReceiveWithAnyArgs().AppendAsync(default!, TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task A_malformed_name_is_still_refused_first_inside_a_unit()
    {
        var events = new AlvoEvents(Substitute.For<IOutboxStore>(), TimeProvider.System, AlvoChainDepthLimit.Default);
        var unit = new FakeFlowUnit();
        using var _ = AlvoFlow.EnterUnit(unit, AlvoProvenance.None);

        await Should.ThrowAsync<ArgumentException>(() => events.PublishAsync(
            "entity.orders.created", "orders/1", null, _caller, TestContext.Current.CancellationToken));
        unit.Poisoned.ShouldBeEmpty();
    }

    [Fact]
    public async Task An_unwritable_payload_is_still_refused_before_the_unit_refusal()
    {
        var events = new AlvoEvents(Substitute.For<IOutboxStore>(), TimeProvider.System, AlvoChainDepthLimit.Default);
        var unit = new FakeFlowUnit();
        using var _ = AlvoFlow.EnterUnit(unit, AlvoProvenance.None);

        await Should.ThrowAsync<ArgumentException>(() => events.PublishAsync(
            "orders.approved", "orders/1", new Dictionary<string, object?> { ["value"] = new Dictionary<string, object?> { ["nested"] = 1 } },
            _caller, TestContext.Current.CancellationToken));
        unit.Poisoned.ShouldBeEmpty();
    }

    [Fact]
    public async Task The_unit_refusal_comes_before_the_depth_refusal()
    {
        var events = new AlvoEvents(Substitute.For<IOutboxStore>(), TimeProvider.System, AlvoChainDepthLimit.Default);
        var unit = new FakeFlowUnit();
        using var _ = AlvoFlow.EnterUnit(unit, AlvoProvenance.None);
        var deep = _caller with { Provenance = new AlvoProvenance { Depth = AlvoChainDepthLimit.DefaultMaxDepth + 1 } };

        var refusal = await Should.ThrowAsync<AlvoUnitInProgressException>(() => events.PublishAsync(
            "orders.approved", "orders/1", null, deep, TestContext.Current.CancellationToken));
        unit.Poisoned.ShouldHaveSingleItem().ShouldBeSameAs(refusal);
    }

    [Fact]
    public async Task A_publish_outside_any_unit_appends_one_envelope_built_by_the_shared_builder()
    {
        var outbox = Substitute.For<IOutboxStore>();
        var events = new AlvoEvents(outbox, TimeProvider.System, AlvoChainDepthLimit.Default);

        await events.PublishAsync("orders.approved", "orders/1", new Dictionary<string, object?> { ["total"] = 12.5m }, _caller, TestContext.Current.CancellationToken);

        await outbox.Received(1).AppendAsync(
            Arg.Is<AlvoCustomEvent>(custom => custom.Envelope.Type == "orders.approved" && custom.Envelope.PartitionKey == "custom.event:orders/1"),
            Arg.Any<CancellationToken>());
    }
}
