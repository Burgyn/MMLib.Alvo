using MMLib.Alvo.Data.EntityFrameworkCore.Internal;
using System.Data;
using System.Data.Common;
using System.Text.RegularExpressions;

namespace MMLib.Alvo.Data.EntityFrameworkCore;

/// <summary>
/// Idempotently creates Alvo's own fixed bookkeeping tables — the append-only descriptor-versions table, the
/// idempotency-record table and the transactional outbox — none of which is produced by the declarative
/// descriptor-diff engine (<see cref="Migrations.ISchemaMigrator"/>).
/// </summary>
/// <remarks>
/// <para>
/// Every DDL statement here is written to be identical on SQLite and PostgreSQL (a single <c>CREATE TABLE
/// IF NOT EXISTS</c> with only ANSI-portable column types), so this class needs no per-engine branching.
/// That invariant is what decided the outbox's ordering key: a <c>sequence</c> column would need
/// <c>AUTOINCREMENT</c> on one engine and <c>… AS IDENTITY</c> on the other, each of which the other engine
/// refuses outright, so the key is a UUIDv7 <c>id</c> instead (see <see cref="OutboxTable"/>).
/// </para>
/// <para>
/// The idempotency table's own DDL lives beside its name in <see cref="IdempotencyTable"/> rather than
/// inline here, because the write path also has to create it on demand: nothing calls this initializer on
/// the data path (only a descriptor <em>apply</em> reaches it), so a host whose schema never came through
/// the mapper would otherwise have the table missing exactly when a create carrying a token needs it. Two
/// creators, one DDL string. <see cref="OutboxTable"/> follows the same arrangement for the same reason —
/// every write emits an event, so the outbox has to exist on the data path too.
/// </para>
/// <para>
/// <b>The outbox is the moment <c>docs/architecture/package-boundary.md</c> predicted</b> — <em>"a port is
/// earned the moment a driver's system schema grows a table no store call touches"</em>. It is the first such
/// table: the descriptor-version history has <see cref="Migrations.IDescriptorVersionStore"/> and the
/// idempotency record is read through the write path itself, but nothing above the driver can reach a queued
/// event. That debt is paid by <c>IOutboxStore</c>, which the dispatcher depends on and
/// <c>EfCoreOutboxStore</c> implements over the statements in <see cref="OutboxTable"/>.
/// </para>
/// </remarks>
internal sealed partial class SystemSchemaInitializer
{
    private readonly DbConnection _connection;

    public SystemSchemaInitializer(DbConnection connection, string schemaPrefix)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentException.ThrowIfNullOrWhiteSpace(schemaPrefix);

        if (!SchemaPrefixPattern().IsMatch(schemaPrefix))
        {
            throw new ArgumentException(
                $"Schema prefix '{schemaPrefix}' must be lower snake_case, 1-16 chars (matching AlvoOptions.SchemaPrefix's validation).",
                nameof(schemaPrefix));
        }

        _connection = connection;
        TableName = DescriptorVersionsTableName(schemaPrefix);
        _idempotencyTableName = IdempotencyTable.NameFor(schemaPrefix);
        _outboxTableName = OutboxTable.NameFor(schemaPrefix);
        _secretsTableName = SecretsTable.NameFor(schemaPrefix);
        _deliveriesTableName = DeliveryTable.NameFor(schemaPrefix);
        _deliveryAttemptsTableName = DeliveryTable.AttemptsNameFor(schemaPrefix);
        _subscriberControlsTableName = DeliveryOperationsTable.ControlsNameFor(schemaPrefix);
        _operationsLogTableName = DeliveryOperationsTable.LogNameFor(schemaPrefix);
        _commandRequestsTableName = CommandLedgerTable.NameFor(schemaPrefix);
        _inboundWebhooksTableName = InboundWebhookTable.NameFor(schemaPrefix);
        _hookRejectionsTableName = HookRejectionTable.NameFor(schemaPrefix);
        _sequencesTableName = SequenceTable.NameFor(schemaPrefix);
        _timersTableName = TimerTable.NameFor(schemaPrefix);
        _schedulesTableName = ScheduleTable.NameFor(schemaPrefix);
        _dataMigrationsTableName = DataMigrationTable.NameFor(schemaPrefix);
        _stepsTableName = StepTable.NameFor(schemaPrefix);
        _concurrencySlotsTableName = ConcurrencySlotTable.NameFor(schemaPrefix);
        _authTables = new AuthTables(schemaPrefix);
    }

    private readonly string _idempotencyTableName;
    private readonly string _outboxTableName;
    private readonly string _secretsTableName;
    private readonly string _deliveriesTableName;
    private readonly string _deliveryAttemptsTableName;
    private readonly string _subscriberControlsTableName;
    private readonly string _operationsLogTableName;
    private readonly string _commandRequestsTableName;
    private readonly string _inboundWebhooksTableName;
    private readonly string _hookRejectionsTableName;
    private readonly string _sequencesTableName;
    private readonly string _timersTableName;
    private readonly string _schedulesTableName;
    private readonly string _dataMigrationsTableName;
    private readonly string _stepsTableName;
    private readonly string _concurrencySlotsTableName;
    private readonly AuthTables _authTables;

    /// <summary>Gets the fully-prefixed descriptor-versions table name, e.g. <c>alvo_descriptor_versions</c>.</summary>
    public string TableName { get; }

    /// <summary>
    /// Computes the descriptor-versions table name for a given prefix — the single source of truth
    /// <see cref="EfCoreSchemaIntrospector"/> reuses to exclude Alvo's own bookkeeping table from
    /// what it reports as the user's schema.
    /// </summary>
    public static string DescriptorVersionsTableName(string schemaPrefix) =>
        schemaPrefix + AlvoFrameworkTables.DescriptorVersionsSuffix;

    /// <summary>
    /// Every table this initializer owns, for a given prefix — the set
    /// <see cref="EfCoreSchemaIntrospector"/> excludes from what it reports as the user's schema. One
    /// member rather than a name per caller: an introspector that knows about one framework table and not
    /// the next one added would plan a <c>DROP</c> for it on the following re-apply, silently, and the
    /// symptom would be a lost idempotency history rather than an error.
    /// </summary>
    /// <param name="schemaPrefix">The validated <see cref="AlvoOptions.SchemaPrefix"/>.</param>
    public static IReadOnlyList<string> FrameworkTableNames(string schemaPrefix) =>
        AlvoFrameworkTables.NamesFor(schemaPrefix);

    /// <summary>
    /// Creates the framework's bookkeeping tables if they do not already exist. Safe to call repeatedly —
    /// a second (or Nth) call is a no-op — and safe to call from several processes at once.
    /// </summary>
    /// <remarks>
    /// Every bookkeeping table and its indexes, including those a store also ensures for itself: a store that
    /// first created a table once its replica was serving would race a replica still introspecting the schema
    /// in its boot — PostgreSQL's catalog reads are separate statements, so the table read could miss a table
    /// whose index the index read then found, and the introspection fails.
    /// </remarks>
    // Deferred: Postgres schema cohabitation (spec §2.13) — embedded mode living inside a host's
    // own Postgres schema — is intentionally out of scope for this PR and tracked as follow-up
    // work. PR-A deliberately uses a plain, cross-engine table-name prefix instead, identically on
    // both SQLite and PostgreSQL, rather than a real Postgres DB schema.
    public async Task EnsureAsync(CancellationToken ct = default)
    {
        if (_connection.State != ConnectionState.Open)
        {
            await _connection.OpenAsync(ct).ConfigureAwait(false);
        }

        // The table names are validated identifiers (see the ctor guard above), not attacker-controlled
        // data, so interpolating them is safe — SQL parameters can only bind values, never identifiers,
        // so this is the only way to parameterize them anyway.
        await CreateIfMissingAsync(TableName, DescriptorVersionsDdl, ct).ConfigureAwait(false);
        await CreateIfMissingAsync(
            _idempotencyTableName, IdempotencyTable.Ddl(_idempotencyTableName), ct).ConfigureAwait(false);
        await CreateIfMissingAsync(
            _outboxTableName, OutboxTable.Ddl(_outboxTableName), ct).ConfigureAwait(false);
        await CreateIfMissingAsync(
            _secretsTableName, SecretsTable.Ddl(_secretsTableName), ct).ConfigureAwait(false);
        await CreateIfMissingAsync(
            _deliveriesTableName, DeliveryTable.Ddl(_deliveriesTableName), ct).ConfigureAwait(false);
        await CreateIndexIfMissingAsync(DeliveryTable.ClaimIndexDdl(_deliveriesTableName), ct).ConfigureAwait(false);
        await CreateIndexIfMissingAsync(DeliveryTable.LaneIndexDdl(_deliveriesTableName), ct).ConfigureAwait(false);
        await CreateIndexIfMissingAsync(DeliveryTable.OpenLaneIndexDdl(_deliveriesTableName), ct).ConfigureAwait(false);
        await CreateIndexIfMissingAsync(DeliveryOperationsTable.SubscriberPageIndexDdl(_deliveriesTableName), ct).ConfigureAwait(false);
        await CreateIfMissingAsync(
            _deliveryAttemptsTableName, DeliveryTable.AttemptsDdl(_deliveryAttemptsTableName), ct).ConfigureAwait(false);
        await CreateIndexIfMissingAsync(DeliveryTable.OutboxUnfannedIndexDdl(_outboxTableName), ct).ConfigureAwait(false);
        await CreateIfMissingAsync(
            _subscriberControlsTableName, DeliveryOperationsTable.ControlsDdl(_subscriberControlsTableName), ct).ConfigureAwait(false);
        await CreateIfMissingAsync(
            _operationsLogTableName, DeliveryOperationsTable.LogDdl(_operationsLogTableName), ct).ConfigureAwait(false);
        await CreateIndexIfMissingAsync(DeliveryOperationsTable.LogKeyIndexDdl(_operationsLogTableName), ct).ConfigureAwait(false);
        await CreateIfMissingAsync(
            _commandRequestsTableName, CommandLedgerTable.Ddl(_commandRequestsTableName), ct).ConfigureAwait(false);
        await CreateIndexIfMissingAsync(CommandLedgerTable.IndexDdl(_commandRequestsTableName), ct).ConfigureAwait(false);
        await CreateIfMissingAsync(
            _inboundWebhooksTableName, InboundWebhookTable.Ddl(_inboundWebhooksTableName), ct).ConfigureAwait(false);
        await CreateIndexIfMissingAsync(InboundWebhookTable.IndexDdl(_inboundWebhooksTableName), ct).ConfigureAwait(false);
        await CreateIfMissingAsync(
            _hookRejectionsTableName, HookRejectionTable.Ddl(_hookRejectionsTableName), ct).ConfigureAwait(false);
        await CreateIndexIfMissingAsync(HookRejectionTable.IndexDdl(_hookRejectionsTableName), ct).ConfigureAwait(false);
        await CreateIfMissingAsync(_sequencesTableName, SequenceTable.Ddl(_sequencesTableName), ct).ConfigureAwait(false);
        await CreateIfMissingAsync(_timersTableName, TimerTable.Ddl(_timersTableName), ct).ConfigureAwait(false);
        await CreateIndexIfMissingAsync(TimerTable.DueIndexDdl(_timersTableName), ct).ConfigureAwait(false);
        await CreateIfMissingAsync(_schedulesTableName, ScheduleTable.Ddl(_schedulesTableName), ct).ConfigureAwait(false);
        await CreateIfMissingAsync(_dataMigrationsTableName, DataMigrationTable.Ddl(_dataMigrationsTableName), ct).ConfigureAwait(false);
        await CreateIfMissingAsync(_stepsTableName, StepTable.Ddl(_stepsTableName), ct).ConfigureAwait(false);
        await CreateIfMissingAsync(_concurrencySlotsTableName, ConcurrencySlotTable.Ddl(_concurrencySlotsTableName), ct).ConfigureAwait(false);
        await CreateIndexIfMissingAsync(ConcurrencySlotTable.DeliveryIndexDdl(_concurrencySlotsTableName), ct).ConfigureAwait(false);
        await _authTables.EnsureAsync(_connection, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Runs one <c>CREATE TABLE IF NOT EXISTS</c>, treating "another connection created it a moment ago" as the success
    /// <c>IF NOT EXISTS</c> was asked for — see <see cref="IfNotExistsDdl"/>, which holds the measured reason and is
    /// shared with every store that ensures its own table on the data path.
    /// </summary>
    /// <param name="tableName">The table the DDL creates.</param>
    /// <param name="ddl">The <c>CREATE TABLE IF NOT EXISTS</c> statement.</param>
    /// <param name="ct">A token to cancel the operation.</param>
    private Task CreateIfMissingAsync(string tableName, string ddl, CancellationToken ct) =>
        IfNotExistsDdl.CreateTableAsync(_connection, tableName, ddl, ct);

    /// <summary>The index-shaped twin of <see cref="CreateIfMissingAsync"/>: <see cref="IfNotExistsDdl.CreateIndexAsync"/>.</summary>
    /// <param name="ddl">The <c>CREATE INDEX IF NOT EXISTS</c> statement.</param>
    /// <param name="ct">A token to cancel the operation.</param>
    private Task CreateIndexIfMissingAsync(string ddl, CancellationToken ct) =>
        IfNotExistsDdl.CreateIndexAsync(_connection, ddl, ct);

    private string DescriptorVersionsDdl =>
        $"""
        CREATE TABLE IF NOT EXISTS {TableName} (
            project TEXT NOT NULL,
            revision INTEGER NOT NULL,
            descriptor_json TEXT NOT NULL,
            schema_json TEXT NOT NULL,
            author TEXT NULL,
            reason TEXT NULL,
            rolled_back_from INTEGER NULL,
            created_at TEXT NOT NULL,
            PRIMARY KEY (project, revision)
        )
        """;

    /// <remarks>
    /// <c>\A…\z</c> rather than <c>^…$</c>: in .NET <c>$</c> also matches immediately before a trailing
    /// <c>\n</c>, so <c>"alvo\n"</c> passed this check and reached the <b>interpolated</b> table name in the
    /// DDL below. Benign as it stands — a newline is SQL whitespace, so it yields a syntax error rather than a
    /// second statement, and the prefix comes from configuration rather than from a caller — but it is the
    /// identical defect found in <c>AlvoEventName</c>'s guard in the same PR, and the fix is two characters.
    /// </remarks>
    [GeneratedRegex(@"\A[a-z][a-z0-9_]{0,15}\z")]
    private static partial Regex SchemaPrefixPattern();
}
