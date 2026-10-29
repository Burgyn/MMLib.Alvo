namespace MMLib.Alvo.Data.EntityFrameworkCore.Tests;

/// <summary>
/// R-B1 (spec 2026-10-14 §4.5): every member of the data port, reads included, is refused while a unit is open on the
/// flow — at once, before any statement — and the refusal fails the unit.
/// </summary>
public sealed class EfAlvoDataUnitRefusalTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static readonly (string Member, Func<EfAlvoData, AlvoContext, Task> Call)[] _members =
    [
        ("IAlvoData.QueryAsync", (data, caller) => data.QueryAsync(new AlvoQuery { Entity = WritePathFixture.Entity }, caller, Ct)),
        ("IAlvoData.GetAsync", (data, caller) => data.GetAsync(WritePathFixture.Entity, Guid.NewGuid(), caller, Ct)),
        ("IAlvoData.CreateAsync", (data, caller) => data.CreateAsync(WritePathFixture.Entity, WritePathFixture.CreatePayload("x"), caller, cancellationToken: Ct)),
        ("IAlvoData.UpdateAsync", (data, caller) => data.UpdateAsync(WritePathFixture.Entity, Guid.NewGuid(), Title("x"), caller, cancellationToken: Ct)),
        ("IAlvoData.ReplaceAsync", (data, caller) => data.ReplaceAsync(WritePathFixture.Entity, Guid.NewGuid(), WritePathFixture.CreatePayload("x"), caller, cancellationToken: Ct)),
        ("IAlvoData.DeleteAsync", (data, caller) => data.DeleteAsync(WritePathFixture.Entity, Guid.NewGuid(), caller, cancellationToken: Ct)),
        ("IAlvoData.CreateManyAsync", (data, caller) => data.CreateManyAsync(WritePathFixture.Entity, [WritePathFixture.CreatePayload("x")], caller, cancellationToken: Ct)),
        ("IAlvoData.UpdateManyAsync", (data, caller) => data.UpdateManyAsync(WritePathFixture.Entity, [new AlvoRowPatch(Guid.NewGuid(), Title("x"))], caller, cancellationToken: Ct)),
        ("IAlvoData.DeleteManyAsync", (data, caller) => data.DeleteManyAsync(WritePathFixture.Entity, [Guid.NewGuid()], caller, cancellationToken: Ct)),
    ];

    /// <summary>Every member's name; the row's call is looked up by it, because <see cref="EfAlvoData"/> is internal.</summary>
    public static TheoryData<string> EveryMember() => [.. _members.Select(row => row.Member)];

    /// <summary>A tenth member of the port cannot ship without its refusal row (Ruling 39).</summary>
    [Fact]
    public void The_refusal_rows_name_every_member_of_the_port()
        => _members.Select(row => row.Member).ShouldBe(
            typeof(IAlvoData).GetMethods().Select(method => "IAlvoData." + method.Name).Distinct(), ignoreOrder: true);

    [Theory]
    [MemberData(nameof(EveryMember))]
    public async Task Every_member_is_refused_inside_an_open_unit_before_any_statement(string member)
    {
        var call = _members.Single(row => row.Member == member).Call;
        await using var world = await StartAsync();
        var unit = new OpenUnit();
        var seen = world.Statements.Seen.Count;

        using (AlvoFlow.EnterUnit(unit, AlvoProvenance.None))
        {
            var refusal = await Should.ThrowAsync<AlvoUnitInProgressException>(() => call(world.Data, world.Caller));

            refusal.Message.ShouldStartWith($"{member} was called while a transaction unit is open on this flow");
            unit.Poisoned.ShouldHaveSingleItem().ShouldBeSameAs(refusal);
        }

        world.Statements.Seen.Count.ShouldBe(seen, "a refusal reaches no statement");
    }

    private static readonly AlvoIdempotency _key = new("key-1", "fp-1");

    private static readonly (string Member, Func<EfAlvoData, AlvoContext, Task> Call)[] _keyedWrites =
    [
        ("IAlvoData.CreateAsync", (data, caller) => data.CreateAsync(WritePathFixture.Entity, WritePathFixture.CreatePayload("x"), caller, _key, Ct)),
        ("IAlvoData.UpdateAsync", (data, caller) => data.UpdateAsync(WritePathFixture.Entity, Guid.NewGuid(), Title("x"), caller, idempotency: _key, cancellationToken: Ct)),
        ("IAlvoData.ReplaceAsync", (data, caller) => data.ReplaceAsync(WritePathFixture.Entity, Guid.NewGuid(), WritePathFixture.CreatePayload("x"), caller, idempotency: _key, cancellationToken: Ct)),
        ("IAlvoData.DeleteAsync", (data, caller) => data.DeleteAsync(WritePathFixture.Entity, Guid.NewGuid(), caller, idempotency: _key, cancellationToken: Ct)),
        ("IAlvoData.CreateManyAsync", (data, caller) => data.CreateManyAsync(WritePathFixture.Entity, [WritePathFixture.CreatePayload("x")], caller, _key, Ct)),
        ("IAlvoData.UpdateManyAsync", (data, caller) => data.UpdateManyAsync(WritePathFixture.Entity, [new AlvoRowPatch(Guid.NewGuid(), Title("x"))], caller, _key, Ct)),
        ("IAlvoData.DeleteManyAsync", (data, caller) => data.DeleteManyAsync(WritePathFixture.Entity, [Guid.NewGuid()], caller, _key, Ct)),
    ];

    /// <summary>Every write that takes a key; the row's call is looked up by its name.</summary>
    public static TheoryData<string> EveryKeyedWrite() => [.. _keyedWrites.Select(row => row.Member)];

    [Theory]
    [MemberData(nameof(EveryKeyedWrite))]
    public async Task A_keyed_write_inside_an_open_unit_is_an_argument_exception(string member)
    {
        await using var world = await StartAsync();
        var call = _keyedWrites.Single(row => row.Member == member).Call;
        var unit = new OpenUnit();
        var seen = world.Statements.Seen.Count;
        using var _ = AlvoFlow.EnterUnit(unit, AlvoProvenance.None);

        var refusal = await Should.ThrowAsync<ArgumentException>(() => call(world.Data, world.Caller));

        refusal.ParamName.ShouldBe("idempotency");
        refusal.Message.ShouldStartWith($"{member} carried an idempotency key");
        unit.Poisoned.ShouldHaveSingleItem().ShouldBeSameAs(refusal);
        world.Statements.Seen.Count.ShouldBe(seen, "a refusal reaches no statement");
    }

    [Fact]
    public async Task A_unit_that_has_ended_refuses_nothing()
    {
        await using var world = await StartAsync();
        var unit = new OpenUnit { IsOpen = false };
        using var _ = AlvoFlow.EnterUnit(unit, AlvoProvenance.None);

        var created = await world.Data.CreateAsync(WritePathFixture.Entity, WritePathFixture.CreatePayload("after"), world.Caller, cancellationToken: Ct);

        created["title"].ShouldBe("after");
        unit.Poisoned.ShouldBeEmpty();
    }

    private static Task<WritePathWorld> StartAsync() =>
        WritePathWorld.StartAsync(WritePathFixture.Descriptor(), WritePathFixture.Schema());

    private static Dictionary<string, object?> Title(string title) => new(StringComparer.Ordinal) { ["title"] = title };

    private sealed class OpenUnit : IAlvoFlowUnit
    {
        private readonly List<Exception> _poisoned = [];

        public bool IsOpen { get; set; } = true;

        internal IReadOnlyList<Exception> Poisoned => _poisoned;

        public void Poison(Exception failure) => _poisoned.Add(failure);
    }
}
