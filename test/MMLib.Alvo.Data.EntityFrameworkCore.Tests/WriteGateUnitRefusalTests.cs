using Microsoft.Data.Sqlite;
using MMLib.Alvo.Data.EntityFrameworkCore.Internal;
using MMLib.Alvo.Migrations;
using MMLib.Alvo.Schema;

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

    public static TheoryData<string> StoresThatEnsureFirst() => ["descriptor-version", "runtime-schema"];

    /// <summary>
    /// The two stores whose write ensures its tables before it reaches the gate refuse before that — before they even
    /// open a connection — so a cold process never runs DDL beside an open unit (fix round 1, M1).
    /// </summary>
    [Theory]
    [MemberData(nameof(StoresThatEnsureFirst))]
    public async Task A_store_write_that_ensures_its_tables_first_is_refused_before_it_opens_a_connection(string store)
    {
        var opened = 0;
        var connections = new RelationalConnectionFactory(() =>
        {
            opened++;
            return new SqliteConnection("Data Source=:memory:");
        });
        var unit = new OpenUnit();
        using var _ = AlvoFlow.EnterUnit(unit, AlvoProvenance.None);

        var refusal = await Should.ThrowAsync<AlvoUnitInProgressException>(() => WriteAsync(store, connections));

        refusal.Message.ShouldStartWith(StoreWrites.Call);
        unit.Poisoned.ShouldHaveSingleItem().ShouldBeSameAs(refusal);
        opened.ShouldBe(0, "the refusal comes before the store's connection and its ensure step");
    }

    private static Task<DescriptorVersion> WriteAsync(string store, RelationalConnectionFactory connections)
    {
        var version = new DescriptorVersion(new SchemaModel([]), "{}", 1, DateTimeOffset.UnixEpoch);
        return store switch
        {
            "descriptor-version" => new EfCoreDescriptorVersionStore(connections, new AlvoOptions())
                .AppendAsync("p", version, expectedRevision: 0, Ct),
            _ => new EfCoreRuntimeSchemaWriter(connections, new AlvoOptions(), new SqliteTestDialect())
                .ApplyAndAppendAsync("p", new MigrationPlan { Steps = [] }, version, expectedRevision: 0, new MigrationOptions(), Ct),
        };
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
