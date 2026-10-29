using MMLib.Alvo.Data.EntityFrameworkCore.Internal;

namespace MMLib.Alvo.Data.EntityFrameworkCore.Tests;

/// <summary>
/// Every framework store that writes — the outbox, the deliveries, secrets, descriptor versions, the runtime schema, the
/// management idempotency record — waits at the database's write gate first; inside an open unit the gate refuses, so no
/// store write can wait for the unit's own turn on SQLite, nor commit beside the unit on PostgreSQL.
/// </summary>
public sealed class WriteGateUnitRefusalTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public static TheoryData<string> Gates() => ["no-gate", "serial", "serial-bounded"];

    [Theory]
    [MemberData(nameof(Gates))]
    public async Task A_store_write_inside_an_open_unit_is_refused_at_its_gate_and_fails_the_unit(string gate)
    {
        var unit = new OpenUnit();
        using var _ = AlvoFlow.EnterUnit(unit, AlvoProvenance.None);

        var refusal = await Should.ThrowAsync<AlvoUnitInProgressException>(async () => (await EnterAsync(gate))?.Dispose());

        refusal.Message.ShouldStartWith(StoreWrites.Call);
        unit.Poisoned.ShouldHaveSingleItem().ShouldBeSameAs(refusal);
    }

    [Theory]
    [MemberData(nameof(Gates))]
    public async Task A_store_write_outside_any_unit_enters(string gate)
    {
        using var turn = await EnterAsync(gate);

        turn.ShouldNotBeNull();
    }

    private static async Task<IDisposable?> EnterAsync(string gate) => gate switch
    {
        "no-gate" => await NoRelationalWriteGate.Instance.EnterAsync(Ct),
        "serial" => await new SerialRelationalWriteGate().EnterAsync(Ct),
        _ => await new SerialRelationalWriteGate().TryEnterAsync(TimeSpan.FromSeconds(1), Ct),
    };

    private sealed class OpenUnit : IAlvoFlowUnit
    {
        private readonly List<Exception> _poisoned = [];

        public bool IsOpen => true;

        internal IReadOnlyList<Exception> Poisoned => _poisoned;

        public void Poison(Exception failure) => _poisoned.Add(failure);
    }
}
