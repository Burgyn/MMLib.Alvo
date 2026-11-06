using System.Data.Common;

namespace MMLib.Alvo.Data.EntityFrameworkCore.Internal;

/// <summary>
/// Alvo's concurrency slots: a capped subscriber's key owns <c>limit</c> lockable rows, (slot key, slot), and a delivery of
/// that key runs only while it holds one. A counting semaphore made of rows, so the cap holds under concurrent claimants on
/// every engine with no <c>COUNT</c> predicate. Identical SQL on both shipped engines.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why the cap holds on PostgreSQL's READ COMMITTED.</b> A slot is taken by a conditional <c>UPDATE</c> of one row. A
/// second claimant that reaches a row the first just took waits on that row's lock, then re-checks the outer condition
/// against the committed row, fails, and moves to the next slot; once all <c>limit</c> rows are held, no claimant gets one,
/// whatever snapshot it read its candidates from. A <c>COUNT(*) &lt; limit</c> test would pass for every claimant that read
/// the same snapshot.
/// </para>
/// <para>
/// <b>Rows are created lazily and deleted when freed.</b> A claim inserts a key's rows with <c>ON CONFLICT DO NOTHING</c>
/// before it takes one, and an answer deletes the row its delivery held, so a per-record key leaves no row behind once its
/// deliveries are done. A row is <em>free</em> when it holds no delivery or its lease expired — a crashed runner's slot frees
/// with its claim's lease.
/// </para>
/// <para>
/// <b>Two kinds of row outlive their delivery.</b> A crashed runner's row, and the row of a running capped delivery that an
/// operator cancelled or whose subscriber was retired, whose late answer then fails its fence: no answer deletes either. Each
/// is free once its lease expires, and a taker overwrites it the next time its key is claimed; a per-record key whose record
/// never gets another event keeps the row, and a key holds one slot less until the lease expires. Neither breaks the cap —
/// the cost is a stale row and, for up to one lease, a slot.
/// </para>
/// <para>
/// <b>The column is <c>slot_key</c>, not the spec's <c>key</c></b>: <c>KEY</c> is reserved in T-SQL, and the names stay
/// portable to the Azure SQL provider. A deliberate deviation from the spec's DDL.
/// </para>
/// </remarks>
internal static class ConcurrencySlotTable
{
    /// <summary>The key a capped delivery counts under when its subscriber is capped per tenant.</summary>
    internal const string TenantKey = "tenant";

    /// <summary>The key a capped delivery counts under when its subscriber is capped per record of a tenant.</summary>
    internal const string RecordKey = "record";

    /// <summary>The highest cap a store accepts.</summary>
    internal const int MaxLimit = 100;

    private const char Separator = '\u001f';

    /// <summary>The framework's slot table for a prefix, e.g. <c>alvo_concurrency_slots</c>.</summary>
    /// <param name="schemaPrefix">The validated <see cref="AlvoOptions.SchemaPrefix"/>.</param>
    internal static string NameFor(string schemaPrefix) => schemaPrefix + AlvoFrameworkTables.ConcurrencySlotsSuffix;

    /// <summary>The <c>CREATE TABLE IF NOT EXISTS</c> for <paramref name="table"/>.</summary>
    /// <param name="table">The validated table name.</param>
    internal static string Ddl(string table) =>
        $"""
        CREATE TABLE IF NOT EXISTS {table} (
            slot_key TEXT NOT NULL,
            slot INTEGER NOT NULL,
            delivery_id TEXT NULL,
            lease_until TEXT NULL,
            PRIMARY KEY (slot_key, slot)
        )
        """;

    /// <summary>The index an answer's release finds its delivery's slot by.</summary>
    /// <param name="table">The validated table name.</param>
    internal static string DeliveryIndexDdl(string table) =>
        $"CREATE INDEX IF NOT EXISTS ix_{table}_delivery ON {table} (delivery_id)";

    /// <summary>The table and its index, each through <see cref="IfNotExistsDdl"/>.</summary>
    /// <param name="connection">An open connection.</param>
    /// <param name="table">The validated table name.</param>
    /// <param name="ct">A token to cancel the operation.</param>
    internal static async Task EnsureAsync(DbConnection connection, string table, CancellationToken ct)
    {
        await IfNotExistsDdl.CreateTableAsync(connection, table, Ddl(table), ct).ConfigureAwait(false);
        await IfNotExistsDdl.CreateIndexAsync(connection, DeliveryIndexDdl(table), ct).ConfigureAwait(false);
    }

    /// <summary>Creates slots <c>0 … limit − 1</c> of <c>@slot_key</c> that do not exist yet.</summary>
    /// <param name="table">The validated table name.</param>
    /// <param name="limit">The key's cap.</param>
    internal static string EnsureSlotsSql(string table, int limit) =>
        $"INSERT INTO {table} (slot_key, slot) VALUES {string.Join(", ", Enumerable.Range(0, limit).Select(slot => $"(@slot_key, {slot})"))} "
        + "ON CONFLICT (slot_key, slot) DO NOTHING";

    /// <summary>Takes slot <c>@slot</c> of <c>@slot_key</c> for <c>@delivery_id</c> if it is free.</summary>
    /// <param name="table">The validated table name.</param>
    internal static string TakeSql(string table) =>
        $"""
        UPDATE {table} SET delivery_id = @delivery_id, lease_until = @lease_until
         WHERE slot_key = @slot_key AND slot = @slot AND (delivery_id IS NULL OR lease_until < @now)
        """;

    /// <summary>Gives back a slot this claim took for a delivery it then could not claim.</summary>
    /// <param name="table">The validated table name.</param>
    internal static string GiveBackSql(string table) =>
        $"DELETE FROM {table} WHERE slot_key = @slot_key AND slot = @slot AND delivery_id = @delivery_id";

    /// <summary>
    /// Frees the slot of the delivery <c>@id</c> while the answering claimant still holds it — run first in an answer's
    /// transaction, before the fenced update of the delivery, so every writer locks a slot before a delivery.
    /// </summary>
    /// <remarks>
    /// The fence here is read from the statement's snapshot; what makes it exact is the answer's transaction, which rolls
    /// back when its fenced update then matches nothing, so a late finisher frees no slot its delivery's new claimant holds.
    /// </remarks>
    /// <param name="table">The validated table name.</param>
    /// <param name="deliveries">The validated delivery table name.</param>
    internal static string ReleaseSql(string table, string deliveries) =>
        $"""
        DELETE FROM {table}
         WHERE delivery_id = @id
           AND EXISTS (SELECT 1 FROM {deliveries} fenced
                        WHERE fenced.id = @id AND fenced.claimed_by = @claimed_by AND fenced.attempts = @attempts AND fenced.status = 'running')
        """;

    /// <summary>
    /// The slot key of a capped delivery: subscriber, cap key, tenant and — for a per-record cap — partition key, joined
    /// with U+001F.
    /// </summary>
    /// <remarks>
    /// The partition key is the last part, so whatever it holds cannot make two keys collide; the parts before it — a
    /// registration id and a tenant id — are refused at fan-out when they hold a control character.
    /// </remarks>
    /// <param name="subscriber">The registration id.</param>
    /// <param name="key"><see cref="TenantKey"/> or <see cref="RecordKey"/>.</param>
    /// <param name="tenantId">The delivery's tenant, or <see langword="null"/>.</param>
    /// <param name="partitionKey">The delivery's partition key.</param>
    internal static string SlotKey(string subscriber, string key, string? tenantId, string partitionKey) =>
        string.Join(Separator, subscriber, key, tenantId ?? string.Empty, key == RecordKey ? partitionKey : string.Empty);
}
