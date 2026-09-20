using MMLib.Alvo.Data.EntityFrameworkCore.Internal;
using MMLib.Alvo.Management;
using System.Data.Common;
using System.Globalization;

namespace MMLib.Alvo.Data.EntityFrameworkCore;

/// <summary>
/// <see cref="IManagementIdempotencyStore"/> over the <c>{prefix}_idempotency</c> table the data path
/// already keeps, reached through per-call connections and engine-agnostic SQL.
/// </summary>
/// <remarks>
/// <para>
/// <b>Reusing that table is correct rather than thrifty.</b> It is keyed <c>(idempotency_key, scope)</c>
/// with a free-text <c>row_id</c>, and the scope is <c>AlvoIdempotency.IdentityOf</c> on both paths — so one
/// caller reusing one key for a data create and a descriptor apply presents two different fingerprints and
/// is told so, which is exactly what "the key was reused for a different request" means. No new table, no
/// <c>AlvoFrameworkTables</c> entry, no change to <c>SystemSchemaInitializer</c>.
/// </para>
/// <para>
/// <b><c>row_id</c> holds the revision, written and read verbatim.</b> The raw
/// <see cref="IdempotencyTable.FindRecordedAsync"/>/<see cref="IdempotencyTable.InsertRecordedAsync"/> pair
/// exists for that: the data path's own <c>Encode</c>/<c>Decode</c> spell a row list, which a revision is
/// not.
/// </para>
/// <para>
/// <b>It runs outside any transaction, deliberately.</b> The write it records is committed by
/// <c>IRuntimeSchemaWriter.ApplyAndAppendAsync</c>, which owns its transaction and exposes no seam to enlist
/// in — so the record cannot be made atomic with the append without widening that port. The cost is one
/// crash window, recorded on <see cref="IManagementIdempotencyStore"/> itself.
/// </para>
/// </remarks>
internal sealed class EfCoreManagementIdempotencyStore : IManagementIdempotencyStore
{
    private readonly RelationalConnectionFactory _connections;
    private readonly string _tableName;

    /// <summary>Initializes a new instance of the <see cref="EfCoreManagementIdempotencyStore"/> class.</summary>
    /// <param name="connections">The factory each call takes its own connection from.</param>
    /// <param name="options">The deployment options the schema prefix is read from.</param>
    public EfCoreManagementIdempotencyStore(RelationalConnectionFactory connections, AlvoOptions options)
    {
        ArgumentNullException.ThrowIfNull(connections);
        ArgumentNullException.ThrowIfNull(options);
        _connections = connections;
        _tableName = IdempotencyTable.NameFor(options.SchemaPrefix);
    }

    /// <inheritdoc/>
    public async Task<int?> FindAsync(
        string key, string scope, string fingerprint, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        ArgumentException.ThrowIfNullOrWhiteSpace(scope);
        ArgumentException.ThrowIfNullOrWhiteSpace(fingerprint);

        var connection = _connections.Create();
        await using (connection.ConfigureAwait(false))
        {
            await OpenAsync(connection, ct).ConfigureAwait(false);
            var stored = await IdempotencyTable
                .FindRecordedAsync(connection, transaction: null, _tableName, key, scope, ct)
                .ConfigureAwait(false);

            return stored is { } record ? RevisionOf(record, fingerprint) : null;
        }
    }

    /// <inheritdoc/>
    public async Task RecordAsync(
        string key, string scope, string fingerprint, int revision, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        ArgumentException.ThrowIfNullOrWhiteSpace(scope);
        ArgumentException.ThrowIfNullOrWhiteSpace(fingerprint);

        var connection = _connections.Create();
        await using (connection.ConfigureAwait(false))
        {
            await OpenAsync(connection, ct).ConfigureAwait(false);
            await IdempotencyTable.InsertRecordedAsync(
                connection,
                transaction: null,
                _tableName,
                key,
                scope,
                fingerprint,
                revision.ToString(CultureInfo.InvariantCulture),
                DateTimeOffset.UtcNow,
                ct).ConfigureAwait(false);
        }
    }

    /// <summary>The revision a stored record names, refusing one filed for a different request.</summary>
    /// <remarks>
    /// The comparison is ordinal over a hex digest, like every other name in the framework — a fingerprint
    /// that matched case-insensitively would accept a spelling this build never produces.
    /// </remarks>
    /// <param name="record">The stored fingerprint and revision text.</param>
    /// <param name="fingerprint">The fingerprint of the request being served.</param>
    /// <exception cref="AlvoIdempotencyConflictException">The key was spent on a different request.</exception>
    private static int RevisionOf((string Fingerprint, string RowId) record, string fingerprint) =>
        string.Equals(record.Fingerprint, fingerprint, StringComparison.Ordinal)
            ? int.Parse(record.RowId, NumberStyles.None, CultureInfo.InvariantCulture)
            : throw new AlvoIdempotencyConflictException();

    /// <summary>Opens the connection and creates the table if this database has none yet.</summary>
    /// <remarks>
    /// <b>Not memoised, unlike the versions table's ensure-once gate.</b> A management write is rare — one
    /// per descriptor change — so the statement's cost is invisible beside the migration it records, and a
    /// memo would be one more piece of per-instance state to reason about for no measurable gain.
    /// </remarks>
    /// <param name="connection">The connection this call owns.</param>
    /// <param name="ct">Cancellation token.</param>
    private async Task OpenAsync(DbConnection connection, CancellationToken ct)
    {
        await connection.OpenAsync(ct).ConfigureAwait(false);
        await IdempotencyTable.EnsureAsync(connection, _tableName, ct).ConfigureAwait(false);
    }
}
