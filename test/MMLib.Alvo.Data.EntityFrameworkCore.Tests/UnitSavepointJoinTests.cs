using MMLib.Alvo.Data.EntityFrameworkCore.Internal;

namespace MMLib.Alvo.Data.EntityFrameworkCore.Tests;

/// <summary>
/// A call joins a unit only from the flow that carries it, and a savepoint the engine fails to create fails the unit
/// (Task 7 fix round 1, I2 and M3). Both refusals are what <c>BeginAsync</c> and <c>ReadScopeAsync</c> meet, because
/// every join of a unit goes through <see cref="UnitSavepoint.JoinAsync"/>.
/// </summary>
public sealed class UnitSavepointJoinTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task A_join_from_a_flow_without_the_unit_is_refused_and_fails_the_unit()
    {
        await using var world = await StartAsync();
        await using var unit = await OpenAsync(world);
        using var db = world.CreateContext();

        Task<UnitSavepoint> join;
        using (unit.EnterFlow())
        using (ExecutionContext.SuppressFlow())
        {
            join = Task.Run(() => UnitSavepoint.JoinAsync(db, unit, Ct), Ct);
        }

        var refusal = await Should.ThrowAsync<InvalidOperationException>(join);

        refusal.Message.ShouldStartWith("A call joined this transaction unit from a flow that does not carry it");
        (await Should.ThrowAsync<InvalidOperationException>(() => unit.CommitAsync(Ct))).ShouldBeSameAs(refusal);
    }

    [Fact]
    public async Task A_join_from_the_flow_of_another_unit_is_refused()
    {
        await using var world = await StartAsync();
        await using var unit = await OpenAsync(world);
        using var db = world.CreateContext();
        var other = new OtherUnit();
        using var _ = AlvoFlow.EnterUnit(other, AlvoProvenance.None);

        await Should.ThrowAsync<InvalidOperationException>(() => UnitSavepoint.JoinAsync(db, unit, Ct));
    }

    [Fact]
    public async Task A_join_on_the_unit_s_own_flow_is_admitted()
    {
        await using var world = await StartAsync();
        await using var unit = await OpenAsync(world);
        using var db = world.CreateContext();
        using (unit.EnterFlow())
        {
            await using var savepoint = await UnitSavepoint.JoinAsync(db, unit, Ct);
            await savepoint.CommitAsync(Ct);
        }

        await unit.CommitAsync(Ct);
    }

    [Fact]
    public async Task A_savepoint_the_engine_fails_to_create_fails_the_unit()
    {
        await using var world = await StartAsync();
        await using var unit = await OpenAsync(world);
        using var db = world.CreateContext();
        world.Savepoints.FailNext = true;

        Exception failure;
        using (unit.EnterFlow())
        {
            failure = await Should.ThrowAsync<Microsoft.Data.Sqlite.SqliteException>(() => UnitSavepoint.JoinAsync(db, unit, Ct));
        }

        (await Should.ThrowAsync<Microsoft.Data.Sqlite.SqliteException>(() => unit.CommitAsync(Ct))).ShouldBeSameAs(failure);
    }

    private static Task<WritePathWorld> StartAsync() =>
        WritePathWorld.StartAsync(WritePathFixture.Descriptor(), WritePathFixture.Schema());

    private static Task<WriteUnit> OpenAsync(WritePathWorld world) =>
        world.Data.OpenUnitAsync(world.Caller, new AlvoTransactionOptions(), world.Scopes, Ct);

    private sealed class OtherUnit : IAlvoFlowUnit
    {
        public bool IsOpen => true;

        public void Poison(Exception failure)
        {
        }
    }
}
