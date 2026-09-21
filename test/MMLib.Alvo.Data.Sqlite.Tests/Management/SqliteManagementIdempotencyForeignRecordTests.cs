using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using MMLib.Alvo.Data;
using MMLib.Alvo.Data.EntityFrameworkCore.Internal;
using MMLib.Alvo.Management;

// EF1001 matches on a namespace ending in ".Internal", so here it flags Alvo's OWN internals — this project
// is granted them by InternalsVisibleTo — rather than an Entity Framework internal API.
#pragma warning disable EF1001

namespace MMLib.Alvo.Data.Sqlite.Tests.Management;

/// <summary>
/// A record this surface cannot read is a <b>refusal</b>, not a 500.
/// </summary>
/// <remarks>
/// <b>The two idempotency surfaces share one table.</b> The Data API files a <c>row_id</c> that is a row
/// key; the Management API files one that is a revision number. Each reads only records whose fingerprint
/// it minted, so the orders never cross in practice — but "in practice" is exactly the kind of invariant
/// that fails after an operator's restore, a half-finished migration, or a third writer nobody remembered.
/// <c>int.Parse</c> answered that with a <see cref="FormatException"/>, which the management surface renders
/// as a 500; the caller's fix is "send a different key", which is what the 409 already says.
/// </remarks>
public sealed class SqliteManagementIdempotencyForeignRecordTests : IDisposable
{
    private readonly string _databasePath =
        Path.Combine(Path.GetTempPath(), $"alvo-management-idempotency-foreign-{Guid.NewGuid():N}.db");

    private readonly ServiceProvider _services;

    /// <summary>Initializes a new instance of the <see cref="SqliteManagementIdempotencyForeignRecordTests"/> class.</summary>
    public SqliteManagementIdempotencyForeignRecordTests()
    {
        var builder = new TestAlvoBuilder(new ServiceCollection());
        builder.UseSqlite($"Data Source={_databasePath}");
        _services = builder.Services.BuildServiceProvider();
    }

    /// <summary>A row whose <c>row_id</c> is not a revision is refused rather than thrown over.</summary>
    [Fact]
    public async Task A_record_whose_row_id_is_not_a_revision_is_a_conflict_rather_than_a_throw()
    {
        var store = _services.GetRequiredService<IManagementIdempotencyStore>();
        await store.RecordAsync("k1", "t/u", "fp", revision: 3, TestContext.Current.CancellationToken);
        await OverwriteRowIdAsync("k1", "t/u", Guid.NewGuid().ToString("N"));

        await Should.ThrowAsync<AlvoIdempotencyConflictException>(
            () => store.FindAsync("k1", "t/u", "fp", TestContext.Current.CancellationToken));
    }

    /// <summary>Disposes the container and best-effort deletes the temporary database file.</summary>
    public void Dispose()
    {
        _services.Dispose();

        try
        {
            File.Delete(_databasePath);
        }
        catch (IOException)
        {
        }

        GC.SuppressFinalize(this);
    }

    /// <summary>
    /// Rewrites one record's <c>row_id</c> in place, which is the state a foreign writer leaves.
    /// </summary>
    /// <remarks>
    /// Written through the connection rather than through the store, deliberately: the store has no member
    /// that can produce this row, and inventing one to make the fact reachable would ship a way in.
    /// </remarks>
    /// <param name="key">The record's key.</param>
    /// <param name="scope">The record's scope.</param>
    /// <param name="rowId">The value to leave behind.</param>
    private async Task OverwriteRowIdAsync(string key, string scope, string rowId)
    {
        var connection = new SqliteConnection($"Data Source={_databasePath}");
        await using (connection.ConfigureAwait(false))
        {
            await connection.OpenAsync(TestContext.Current.CancellationToken);

            var command = connection.CreateCommand();
            await using (command.ConfigureAwait(false))
            {
                command.CommandText =
                    $"UPDATE {IdempotencyTable.NameFor("alvo")} SET row_id = $rowId "
                    + "WHERE idempotency_key = $key AND scope = $scope";
                command.Parameters.AddWithValue("$rowId", rowId);
                command.Parameters.AddWithValue("$key", key);
                command.Parameters.AddWithValue("$scope", scope);

                (await command.ExecuteNonQueryAsync(TestContext.Current.CancellationToken)).ShouldBe(
                    1, "the fact needs the record it is about to corrupt to exist");
            }
        }
    }

    /// <summary>The minimal <see cref="IAlvoBuilder"/> the provider extension needs.</summary>
    /// <param name="services">The collection the provider registers into.</param>
    private sealed class TestAlvoBuilder(IServiceCollection services) : IAlvoBuilder
    {
        /// <inheritdoc/>
        public IServiceCollection Services { get; } = services;
    }
}
