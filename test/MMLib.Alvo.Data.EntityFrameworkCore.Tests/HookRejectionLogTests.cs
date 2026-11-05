using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

using MMLib.Alvo.Data.EntityFrameworkCore.Internal;
using MMLib.Alvo.Rules;

using System.Collections.Concurrent;

namespace MMLib.Alvo.Data.EntityFrameworkCore.Tests;

/// <summary>The rejection log's table (HX-09 Ruling 8): ids and codes only, written autocommit, and a failed insert never thrown.</summary>
public sealed class HookRejectionLogTests
{
    private static readonly HookRejection _rejection =
        new(DateTimeOffset.UnixEpoch, "within-opening-hours", "bookings", "create", "conflict", "outside-hours", "tenant/user");

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task A_rejection_outside_a_unit_is_inserted_at_once()
    {
        var database = $"Data Source=hook-rejections-{Guid.NewGuid():N};Mode=Memory;Cache=Shared";
        await using var keeper = new SqliteConnection(database);
        await keeper.OpenAsync(Ct);
        var log = EfCoreHookRejectionLog.ForTests(() => new SqliteConnection(database), "alvo", NullLogger<EfCoreHookRejectionLog>.Instance);

        await log.RecordAsync(_rejection, Ct);

        await using var command = keeper.CreateCommand();
        command.CommandText = "SELECT extension, entity, operation, kind, code, actor FROM alvo_hook_rejections";
        await using var reader = await command.ExecuteReaderAsync(Ct);
        (await reader.ReadAsync(Ct)).ShouldBeTrue();
        Enumerable.Range(0, 6).Select(reader.GetString).ShouldBe(["within-opening-hours", "bookings", "create", "conflict", "outside-hours", "tenant/user"]);
        (await reader.ReadAsync(Ct)).ShouldBeFalse();
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task A_rejection_inside_a_unit_is_held_by_the_unit_and_recorded_once_it_ends_whether_it_commits_or_not(bool commits)
    {
        var primed = await PrimedLogAsync();
        await using var keeper = primed.Keeper;
        var log = primed.Log;
        await using var world = await WritePathWorld.StartAsync(WritePathFixture.Descriptor(), WritePathFixture.Schema(), rejections: log);

        var run = world.Transactions.TransactAsync(world.Caller, async (_, ct) =>
        {
            await log.RecordAsync(_rejection, ct);
            ((WriteUnit)AlvoFlow.Unit!).Rejections.ShouldBe([_rejection]);
            (await RowsAsync(keeper)).ShouldBe(1L, "held by the unit, not inserted while it runs");
            return commits ? true : throw new InvalidOperationException("rolled back");
        }, cancellationToken: Ct);

        if (commits)
        {
            await run;
        }
        else
        {
            await Should.ThrowAsync<InvalidOperationException>(run);
        }

        (await RowsAsync(keeper)).ShouldBe(2L);
    }

    [Fact]
    public async Task A_rejection_recorded_after_its_unit_s_commit_but_before_its_release_is_still_held_and_flushed()
    {
        var primed = await PrimedLogAsync();
        await using var keeper = primed.Keeper;
        var log = primed.Log;
        await using var world = await WritePathWorld.StartAsync(WritePathFixture.Descriptor(), WritePathFixture.Schema(), rejections: log);
        var unit = await world.Data.OpenUnitAsync(world.Caller, new AlvoTransactionOptions(), world.Scopes, Ct);
        using (unit.EnterFlow())
        {
            await unit.CommitAsync();
            await log.RecordAsync(_rejection, Ct);
        }

        unit.Rejections.ShouldBe([_rejection]);
        (await RowsAsync(keeper)).ShouldBe(1L);
        await unit.DisposeAsync();
        await world.Data.FlushRejectionsAsync(unit);
        (await RowsAsync(keeper)).ShouldBe(2L);
    }

    [Fact]
    public async Task A_rejection_recorded_on_a_flow_still_carrying_its_released_unit_is_inserted_at_once()
    {
        var primed = await PrimedLogAsync();
        await using var keeper = primed.Keeper;
        var log = primed.Log;
        await using var world = await WritePathWorld.StartAsync(WritePathFixture.Descriptor(), WritePathFixture.Schema(), rejections: log);
        var unit = await world.Data.OpenUnitAsync(world.Caller, new AlvoTransactionOptions(), world.Scopes, Ct);
        using (unit.EnterFlow())
        {
            await unit.DisposeAsync();
            await log.RecordAsync(_rejection, Ct);
            (await RowsAsync(keeper)).ShouldBe(2L, "a released unit keeps nothing more, so the row is inserted at once");
        }

        unit.Rejections.ShouldBeEmpty();
    }

    [Fact]
    public async Task The_table_holds_no_payload_and_no_message()
    {
        var database = $"Data Source=hook-rejections-{Guid.NewGuid():N};Mode=Memory;Cache=Shared";
        await using var keeper = new SqliteConnection(database);
        await keeper.OpenAsync(Ct);
        await EfCoreHookRejectionLog.ForTests(() => new SqliteConnection(database), "alvo", NullLogger<EfCoreHookRejectionLog>.Instance)
            .RecordAsync(_rejection, Ct);

        await using var command = keeper.CreateCommand();
        command.CommandText = "SELECT name FROM pragma_table_info('alvo_hook_rejections') ORDER BY cid";
        await using var reader = await command.ExecuteReaderAsync(Ct);
        var columns = new List<string>();
        while (await reader.ReadAsync(Ct))
        {
            columns.Add(reader.GetString(0));
        }

        columns.ShouldBe(["id", "at", "extension", "entity", "operation", "kind", "code", "actor"]);
    }

    [Fact]
    public async Task A_failed_insert_is_logged_not_thrown()
    {
        var logger = new RecordingLogger();
        var log = EfCoreHookRejectionLog.ForTests(
            () => new SqliteConnection("Data Source=/nonexistent/dir/alvo.db;Mode=ReadOnly"), "alvo", logger);

        await log.RecordAsync(_rejection, Ct);

        logger.EventNames.ShouldContain("HookRejectionNotRecorded");
    }

    /// <summary>A log over a shared in-memory database, its table created by one rejection recorded outside any unit.</summary>
    private static async Task<(SqliteConnection Keeper, EfCoreHookRejectionLog Log)> PrimedLogAsync()
    {
        var database = $"Data Source=hook-rejections-{Guid.NewGuid():N};Mode=Memory;Cache=Shared";
        var keeper = new SqliteConnection(database);
        await keeper.OpenAsync(Ct);
        var log = EfCoreHookRejectionLog.ForTests(() => new SqliteConnection(database), "alvo", NullLogger<EfCoreHookRejectionLog>.Instance);
        await log.RecordAsync(_rejection with { Code = "primer" }, Ct);

        return (keeper, log);
    }

    private static async Task<long> RowsAsync(SqliteConnection keeper)
    {
        await using var command = keeper.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM alvo_hook_rejections";

        return (long)(await command.ExecuteScalarAsync(Ct))!;
    }

    private sealed class RecordingLogger : ILogger<EfCoreHookRejectionLog>
    {
        internal ConcurrentQueue<string?> EventNames { get; } = new();

        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
            EventNames.Enqueue(eventId.Name);
    }
}
