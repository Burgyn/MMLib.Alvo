using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using MMLib.Alvo.Migrations;
using MMLib.Alvo.Testing.Data;
using Npgsql;
using Xunit;

namespace MMLib.Alvo.Data.PostgreSql.Tests.Integration;

/// <summary>
/// PostgreSQL only: an apply that adds a CHECK needs ACCESS EXCLUSIVE, and one queued behind a reader gives up after its
/// lock_timeout as a retryable refusal instead of stalling every later reader (HX-11 Ruling 20). SQLite has one writer and
/// no lock queue to bound.
/// </summary>
/// <remarks>
/// The wait is the database's own <c>lock_timeout</c> — the shipped five seconds, because the migrator takes the dialect
/// its driver registered and the bound is not configurable — never a client-side delay: the reader holds its lock until
/// the apply has given up, and only then lets go.
/// </remarks>
public sealed class PostgreSqlApplyLockTimeoutTests : IAsyncLifetime
{
    private readonly PostgreSqlAlvoDataFixture _fixture = new();

    public ValueTask InitializeAsync() => _fixture.InitializeAsync();

    public ValueTask DisposeAsync() => _fixture.DisposeAsync();

    [Fact]
    public async Task An_apply_queued_behind_a_reader_gives_up_after_its_lock_timeout_as_a_retryable_refusal()
    {
        var ct = TestContext.Current.CancellationToken;
        var host = await _fixture.StartAsync(CheckSchemas.Unvalidated, CheckSchemas.Descriptor);
        var migrator = host.Services.GetRequiredService<ISchemaMigrator>();
        var plan = await migrator.PlanAsync(CheckSchemas.Unvalidated, CheckSchemas.Validated, new MigrationOptions(), ct);

        await using var reader = new NpgsqlConnection(host.Services.GetRequiredService<IOptions<PostgreSqlProviderOptions>>().Value.ConnectionString);
        await reader.OpenAsync(ct);
        await using var holding = await reader.BeginTransactionAsync(ct);
        await using (var read = new NpgsqlCommand("SELECT 1 FROM \"products\" LIMIT 1", reader, holding))
        {
            await read.ExecuteScalarAsync(ct);
        }

        var refusal = await Should.ThrowAsync<AlvoApplyLockTimeoutException>(() => migrator.ApplyAsync(plan, new MigrationOptions(), ct));
        await holding.RollbackAsync(ct);

        refusal.Message.ShouldBe(AlvoApplyLockTimeoutException.Refusal);
        await host.Data.CreateAsync(
            "products", new Dictionary<string, object?>(StringComparer.Ordinal) { ["stock"] = -1L }, Caller, cancellationToken: ct);
    }

    private static AlvoContext Caller { get; } = new() { User = UserId.New(), Roles = new HashSet<Role> { Role.Authenticated } };
}
