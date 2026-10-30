using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using MMLib.Alvo.Data.EntityFrameworkCore.Internal;
using MMLib.Alvo.Descriptor;
using MMLib.Alvo.Schema;
using MMLib.Alvo.Tests.Ef;
using Shouldly;
using System.Data.Common;
using Xunit;

#pragma warning disable EF1001

namespace MMLib.Alvo.Data.PostgreSql.Tests.Integration;

/// <summary>
/// PostgreSQL's leg of <see cref="LedgerFacts"/>, plus the one fact only PostgreSQL can reach: two first claims of one key
/// colliding at the insert (Ruling 46). SQLite's write gate serialises claims in-process, so there they never overlap.
/// </summary>
public sealed class PostgreSqlLedgerTests : LedgerFacts, IAsyncLifetime
{
    private static readonly DateTimeOffset _now = new(2026, 10, 30, 9, 0, 0, TimeSpan.Zero);

    private readonly PostgreSqlAlvoDataFixture _fixture = new();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <inheritdoc/>
    public ValueTask InitializeAsync() => _fixture.InitializeAsync();

    /// <inheritdoc/>
    protected override async Task<IServiceProvider> StartAsync(SchemaModel schema, AlvoDescriptor descriptor) =>
        (await _fixture.StartAsync(schema, descriptor)).Services;

    /// <inheritdoc/>
    public ValueTask DisposeAsync() => _fixture.DisposeAsync();

    [Fact]
    public async Task A_first_claim_that_collides_at_the_insert_with_a_concurrent_first_claim_reads_in_flight()
    {
        var services = await StartAsync(Schema(), Descriptor());
        var ledger = services.GetRequiredService<ICommandLedger>();
        var connections = services.GetRequiredService<RelationalConnectionFactory>();
        var table = CommandLedgerTable.NameFor(services.GetRequiredService<IOptions<AlvoOptions>>().Value.SchemaPrefix);
        var key = new CommandKey("command:quote_stay/global/race/-", "race", "fp");
        await ledger.SweepAsync(_now.AddYears(-1), Ct);

        await using var rival = await OpenAsync(connections);
        await using var uncommitted = await rival.BeginTransactionAsync(Ct);
        await CommandLedgerTable.InsertClaimAsync(rival, uncommitted, table, key, "rival-token", _now, Ct);
        var claim = ledger.ClaimAsync(key, _now, Ct);
        await WaitUntilAnInsertWaitsOnTheRivalAsync(connections, claim);
        await uncommitted.CommitAsync(Ct);

        (await claim).State.ShouldBe(CommandClaimState.InFlight, "the loser's unique violation is read as the winner's claim in flight");
        (await ledger.ClaimAsync(key with { Fingerprint = "other" }, _now, Ct)).State.ShouldBe(
            CommandClaimState.Mismatch, "the rival's claim is the one that stands");
    }

    /// <summary>
    /// Proves the collision rather than assuming it: the claim reads before it inserts, and the rival's row is uncommitted,
    /// so the claim's read finds nothing and its insert blocks on the rival's key — a backend of this database waiting on a
    /// lock. Committing the rival before that would let the claim's read see the row and never reach the insert.
    /// </summary>
    private static async Task WaitUntilAnInsertWaitsOnTheRivalAsync(RelationalConnectionFactory connections, Task<CommandClaim> claim)
    {
        await using var observer = await OpenAsync(connections);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        deadline.CancelAfter(TimeSpan.FromSeconds(30));
        while (await WaitingOnALockAsync(observer, deadline.Token) == 0)
        {
            claim.IsCompleted.ShouldBeFalse("the claim finished without waiting on the rival's insert, so nothing collided");
            await Task.Delay(TimeSpan.FromMilliseconds(10), deadline.Token);
        }
    }

    private static async Task<long> WaitingOnALockAsync(DbConnection observer, CancellationToken ct)
    {
        await using var command = observer.CreateCommand();
        command.CommandText = "SELECT count(*) FROM pg_stat_activity WHERE datname = current_database() AND wait_event_type = 'Lock'";
        return (long)(await command.ExecuteScalarAsync(ct))!;
    }

    private static async Task<DbConnection> OpenAsync(RelationalConnectionFactory connections)
    {
        var connection = connections.Create();
        await connection.OpenAsync(Ct);
        return connection;
    }
}
