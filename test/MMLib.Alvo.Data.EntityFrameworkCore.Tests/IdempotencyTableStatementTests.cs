using Microsoft.Data.Sqlite;
using MMLib.Alvo.Data.EntityFrameworkCore.Internal;
using System.Data.Common;

namespace MMLib.Alvo.Data.EntityFrameworkCore.Tests;

/// <summary>
/// What the idempotency table's three statements actually do against a relational engine: the DDL creates a
/// table whose primary key is the concurrency control, the lookup answers a record only for the key
/// <em>and</em> the scope it was filed under, and the insert stores every column the lookup reads back.
/// </summary>
/// <remarks>
/// <para>
/// Against SQLite in-memory rather than a fixture-owned database, because none of these facts is
/// engine-specific — the DDL is one hand-written <c>CREATE TABLE IF NOT EXISTS</c> and both statements are
/// single fragments. Note the portability claim is narrower than "ANSI": <c>IdempotencyTable</c>'s own remarks
/// scope it to SQLite and PostgreSQL, and record that T-SQL would need <c>nvarchar</c> — and
/// <c>IF NOT EXISTS</c> is not valid T-SQL at all. What this file pins is the behaviour on the two engines
/// that claim is scoped to. The same claims are exercised end-to-end through the port in
/// <c>MMLib.Alvo.Data.Sqlite.Tests</c>; they are asserted here too because a statement whose only coverage is
/// a full-stack test is a statement nobody can change confidently.
/// </para>
/// <para>
/// <b>Why a database at all in this assembly.</b> A bind parameter that is never added and a statement that is
/// never executed are both invisible to a test that only inspects the SQL text — the command still carries the
/// right words. Only running it says whether the record is there afterwards.
/// </para>
/// </remarks>
public sealed class IdempotencyTableStatementTests : IDisposable
{
    private const string TableName = "alvo_idempotency";
    private const string Scope = "tenant-a:user-1";

    private static readonly DateTimeOffset _writtenAt = new(2026, 3, 4, 5, 6, 7, TimeSpan.Zero);

    private readonly SqliteConnection _connection = new("Data Source=:memory:");

    public IdempotencyTableStatementTests() => _connection.Open();

    public void Dispose()
    {
        _connection.Dispose();
        GC.SuppressFinalize(this);
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>The DDL runs and leaves the table behind.</summary>
    [Fact]
    public async Task EnsureAsync_creates_the_table()
    {
        await IdempotencyTable.EnsureAsync(_connection, TableName, Ct);

        var tables = await ScalarAsync<long>(
            $"SELECT COUNT(*) FROM sqlite_master WHERE type = 'table' AND name = '{TableName}'");

        tables.ShouldBe(1L);
    }

    /// <summary>
    /// And a second run is a no-op rather than a failure — the caller's ensure-once memo is an optimisation,
    /// not the thing that makes a repeat safe.
    /// </summary>
    [Fact]
    public async Task EnsureAsync_is_safe_to_run_again_over_an_existing_table()
    {
        await IdempotencyTable.EnsureAsync(_connection, TableName, Ct);
        await InsertRecordAsync(new AlvoIdempotency("key-1", "fp-1"), Scope, [Guid.NewGuid()]);

        await Should.NotThrowAsync(() => IdempotencyTable.EnsureAsync(_connection, TableName, Ct));

        (await ScalarAsync<long>($"SELECT COUNT(*) FROM {TableName}"))
            .ShouldBe(1L, "a re-run must not drop the records already filed");
    }

    /// <summary>
    /// The composite primary key is the concurrency control: two requests carrying one key in one scope can
    /// both find no record, and this is what lets exactly one of them commit.
    /// </summary>
    [Fact]
    public async Task A_second_record_for_one_key_and_scope_is_refused_by_the_primary_key()
    {
        await IdempotencyTable.EnsureAsync(_connection, TableName, Ct);
        var token = new AlvoIdempotency("key-1", "fp-1");
        await InsertRecordAsync(token, Scope, [Guid.NewGuid()]);

        await Should.ThrowAsync<DbException>(() => InsertRecordAsync(token, Scope, [Guid.NewGuid()]));
    }

    /// <summary>An unused key has no record, which is what makes a first attempt a first attempt.</summary>
    [Fact]
    public async Task FindAsync_answers_null_for_a_key_that_was_never_used()
    {
        await IdempotencyTable.EnsureAsync(_connection, TableName, Ct);

        (await FindRecordAsync("key-1", Scope)).ShouldBeNull();
    }

    /// <summary>A stored record comes back with the fingerprint and the row the insert filed under the key.</summary>
    [Fact]
    public async Task FindAsync_answers_the_fingerprint_and_row_InsertAsync_stored()
    {
        await IdempotencyTable.EnsureAsync(_connection, TableName, Ct);
        var rowId = Guid.NewGuid();
        await InsertRecordAsync(new AlvoIdempotency("key-1", "fp-1"), Scope, [rowId]);

        var record = await FindRecordAsync("key-1", Scope);

        record.ShouldNotBeNull();
        record.Value.Fingerprint.ShouldBe("fp-1");
        record.Value.RowIds.ShouldBe([rowId]);
    }

    /// <summary>
    /// A batch's rows come back in the order it wrote them, because a caller correlates them with the rows
    /// they sent by position.
    /// </summary>
    [Fact]
    public async Task FindAsync_answers_every_row_a_batch_wrote_in_order()
    {
        await IdempotencyTable.EnsureAsync(_connection, TableName, Ct);
        IReadOnlyList<Guid> rowIds = [Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid()];
        await InsertRecordAsync(new AlvoIdempotency("key-1", "fp-1"), Scope, rowIds);

        var record = await FindRecordAsync("key-1", Scope);

        record.ShouldNotBeNull();
        record.Value.RowIds.ShouldBe(rowIds, ignoreOrder: false);
    }

    /// <summary>
    /// The lookup is qualified by scope, which is the whole reason the scope exists: a shared key space lets
    /// one client's replay answer another client's row.
    /// </summary>
    [Fact]
    public async Task FindAsync_does_not_answer_a_record_filed_under_another_scope()
    {
        await IdempotencyTable.EnsureAsync(_connection, TableName, Ct);
        await InsertRecordAsync(new AlvoIdempotency("key-1", "fp-1"), Scope, [Guid.NewGuid()]);

        (await FindRecordAsync("key-1", "tenant-b:user-1")).ShouldBeNull();
    }

    /// <summary>And by the key, so two records in one scope do not answer each other.</summary>
    [Fact]
    public async Task FindAsync_does_not_answer_a_record_filed_under_another_key()
    {
        await IdempotencyTable.EnsureAsync(_connection, TableName, Ct);
        await InsertRecordAsync(new AlvoIdempotency("key-1", "fp-1"), Scope, [Guid.NewGuid()]);

        (await FindRecordAsync("key-2", Scope)).ShouldBeNull();
    }

    /// <summary>
    /// The instant is stored as the framework's own round-trippable text, not as whatever the provider makes
    /// of a <see cref="DateTimeOffset"/> in a <c>TEXT</c> column.
    /// </summary>
    [Fact]
    public async Task InsertAsync_stores_created_at_as_the_frameworks_round_trippable_text()
    {
        await IdempotencyTable.EnsureAsync(_connection, TableName, Ct);
        await InsertRecordAsync(new AlvoIdempotency("key-1", "fp-1"), Scope, [Guid.NewGuid()]);

        var stored = await ScalarAsync<string>($"SELECT created_at FROM {TableName}");

        stored.ShouldBe(StoredInstant.Text(_writtenAt));
    }

    private async Task InsertRecordAsync(AlvoIdempotency token, string scope, IReadOnlyList<Guid> rowIds)
    {
        await using var transaction = _connection.BeginTransaction();

        await IdempotencyTable.InsertAsync(
            _connection, transaction, TableName, token, scope, rowIds, _writtenAt, Ct);
        await transaction.CommitAsync(Ct);
    }

    private async Task<IdempotencyTable.IdempotencyRecord?> FindRecordAsync(string key, string scope)
    {
        await using var transaction = _connection.BeginTransaction();

        return await IdempotencyTable.FindAsync(_connection, transaction, TableName, key, scope, Ct);
    }

    private async Task<T> ScalarAsync<T>(string sql)
    {
        await using var command = _connection.CreateCommand();
        command.CommandText = sql;

        return (T)(await command.ExecuteScalarAsync(Ct))!;
    }
}
