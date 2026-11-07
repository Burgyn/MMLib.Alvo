using MMLib.Alvo.Data.EntityFrameworkCore;
using MMLib.Alvo.Data.EntityFrameworkCore.Internal;
using MMLib.Alvo.Schema;
using Npgsql;
using System.Data.Common;

namespace MMLib.Alvo.Data.PostgreSql;

/// <summary>
/// PostgreSQL's <see cref="IAlvoSqlDialect"/>: unqualified quoted tables (<c>AlvoOptions.SchemaPrefix</c>
/// is a table-name prefix, not a database schema), a standard <c>CAST</c> around the store type EF
/// resolved, and a real row lock whose mode depends on the mutation
/// (see <see cref="IAlvoSqlDialect.RowLockClause"/>).
/// </summary>
public sealed class PostgreSqlSqlDialect : IAlvoSqlDialect
{
    private const string NoKeyUpdate = "FOR NO KEY UPDATE";
    private const string FullUpdate = "FOR UPDATE";

    /// <inheritdoc/>
    /// <remarks>
    /// An update provably never changes the row's key, so it takes the weaker mode PostgreSQL documents
    /// for exactly that case (<i>SELECT</i>, "The Locking Clause"); a delete removes the key, so it needs
    /// the stronger mode that also blocks the <c>FOR KEY SHARE</c> a concurrent foreign-key check would
    /// take (<i>Explicit Locking</i> §13.3.2, which defines <c>FOR NO KEY UPDATE</c> as the mode that does
    /// not block it).
    /// </remarks>
    public string RowLockClause(PreImageMutation mutation) =>
        mutation == PreImageMutation.Delete ? FullUpdate : NoKeyUpdate;

    /// <inheritdoc/>
    /// <remarks>
    /// Transaction-level advisory locks (<i>Explicit Locking</i> §13.3.5): released at the end of the transaction with no
    /// explicit unlock, so a failed or cancelled caller cannot leave one held on a pooled connection. The single-<c>bigint</c>
    /// form takes the key the caller hashed; the shared and exclusive forms conflict exactly as a reader and a writer should.
    /// A shared request also queues behind a conflicting exclusive one that is still <em>waiting</em>, so while an operator's
    /// lock on one handler waits, every later claim naming that handler waits behind it, whatever else it would claim — the
    /// cross-handler stall the delivery store's remarks accept.
    /// </remarks>
    public string? TransactionLockStatement(IReadOnlyList<string> keyParameterMarkers, bool exclusive)
    {
        ArgumentNullException.ThrowIfNull(keyParameterMarkers);
        var function = exclusive ? "pg_advisory_xact_lock" : "pg_advisory_xact_lock_shared";

        return $"SELECT {string.Join(", ", keyParameterMarkers.Select(marker => $"{function}({marker})"))}";
    }

    /// <inheritdoc/>
    /// <remarks>
    /// <c>INSERT … ON CONFLICT DO UPDATE … RETURNING</c> under READ COMMITTED: a concurrent allocator of the same count
    /// blocks on the counter row — or, for a count's first number, on the primary key's index entry — until the first
    /// commits or rolls back, then takes the next value; a rolled-back first insert lets the waiter insert the start.
    /// Implemented explicitly: the create path's question, not part of this type's own surface.
    /// </remarks>
    string? IAlvoSqlDialect.SequenceAllocationStatement(string table) => SequenceTable.Upsert(table);

    /// <inheritdoc/>
    /// <remarks>
    /// One <c>EXCLUDE USING gist</c> table constraint: each equality column <c>WITH =</c> (a scalar in a GiST index needs the
    /// <c>btree_gist</c> extension) and the half-open range — <c>tstzrange</c> for a <c>datetime</c>, <c>daterange</c> for a
    /// <c>date</c>, bounds <c>'[)'</c> — <c>WITH &amp;&amp;</c>, under the index's <c>where</c>. An empty range overlaps nothing.
    /// A table constraint, so it survives anything short of dropping the table.
    /// </remarks>
    ExclusionConstraintSql? IAlvoSqlDialect.ExclusionConstraint(ExclusionConstraintDefinition definition)
    {
        ArgumentNullException.ThrowIfNull(definition);
        var range = definition.RangeType == FieldType.Date ? "daterange" : "tstzrange";
        var equal = definition.EqualityColumns.Select(column => $"{RenderColumn(column)} WITH =");
        var overlap = $"{range}({RenderColumn(definition.StartColumn)}, {RenderColumn(definition.EndColumn)}, '[)') WITH &&";
        var where = definition.Filter is null ? string.Empty : $" WHERE ({definition.Filter})";
        return new(
            Create:
            [
                $"ALTER TABLE {RenderColumn(definition.Table)} ADD CONSTRAINT {RenderColumn(definition.Name)} "
                + $"EXCLUDE USING gist ({string.Join(", ", [.. equal, overlap])}){where}",
            ],
            Drop: [$"ALTER TABLE {RenderColumn(definition.Table)} DROP CONSTRAINT IF EXISTS {RenderColumn(definition.Name)}"],
            SurvivesTableRebuild: true);
    }

    private const string BtreeGistProbe = """
        SELECT EXISTS (SELECT 1 FROM pg_extension WHERE extname = 'btree_gist'),
               EXISTS (SELECT 1 FROM pg_available_extensions WHERE name = 'btree_gist'),
               COALESCE((has_database_privilege(current_database(), 'CREATE')
                         AND has_schema_privilege(current_schema(), 'CREATE')
                         AND current_setting('server_version_num')::int >= 130000)
                        OR (SELECT rolsuper FROM pg_roles WHERE rolname = current_user), FALSE),
               current_setting('server_version_num')::int,
        """ + BtreeGistComparison.UncomparableTypesColumn;

    /// <inheritdoc/>
    /// <remarks>
    /// <c>btree_gist</c> (HX-12 D12): a scalar <c>WITH =</c> in a GiST index needs it. Installed → nothing; available and this
    /// role may install it → the plan's first statement installs it; otherwise the apply is refused, naming what the database
    /// owner runs. A role may install it as a superuser, or with <c>CREATE</c> on the database <em>and</em> on the schema the
    /// extension lands in (the first on the search path; since PostgreSQL 15 nobody's by default) on a server that trusts it
    /// (PostgreSQL 13 and later) — review M2. An equality field whose type this server's btree_gist cannot compare is refused
    /// at its own pointer, naming the version that can (<see cref="BtreeGistComparison"/>, issue 477).
    /// </remarks>
    async ValueTask<ExclusionPrerequisite> IAlvoSqlDialect.ExclusionPrerequisiteAsync(
        DbConnection connection, IReadOnlyList<ExclusionEqualityField> equalityFields, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(equalityFields);
        var command = connection.CreateCommand();
        await using (command.ConfigureAwait(false))
        {
            command.CommandText = BtreeGistProbe;
            var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
            await using (reader.ConfigureAwait(false))
            {
                await reader.ReadAsync(ct).ConfigureAwait(false);
                var installed = reader.GetBoolean(0);
                var uncomparable = BtreeGistComparison.Refusals(equalityFields, installed, reader.GetInt32(3), reader.GetString(4));
                return Prerequisite(installed, installable: reader.GetBoolean(1) && reader.GetBoolean(2), [.. uncomparable]);
            }
        }
    }

    /// <summary>
    /// A database that lacks btree_gist and may not get it is refused, together with every field it could not compare
    /// anyway; any uncomparable field is refused; otherwise an installed extension needs nothing and an installable one is
    /// installed first.
    /// </summary>
    private static ExclusionPrerequisite Prerequisite(bool installed, bool installable, IReadOnlyList<ExclusionRefusal> uncomparable)
    {
        if (!installed && !installable)
        {
            return new ExclusionPrerequisite(Statement: null, [MissingBtreeGist, .. uncomparable]);
        }

        if (uncomparable.Count > 0)
        {
            return new ExclusionPrerequisite(Statement: null, uncomparable);
        }

        return installed ? ExclusionPrerequisite.Ready : new ExclusionPrerequisite("CREATE EXTENSION IF NOT EXISTS btree_gist");
    }

    private static ExclusionRefusal MissingBtreeGist { get; } = new(
        Pointer: null,
        "Excluding overlaps needs PostgreSQL's btree_gist extension, which this database does not have and this connection's role may not install. Nothing was applied.",
        "Ask the database owner to run CREATE EXTENSION btree_gist; in this database (it ships with PostgreSQL and is trusted since PostgreSQL 13), then apply again.");

    /// <inheritdoc/>
    /// <remarks>
    /// <paramref name="lockedPreImageFor"/> is ignored because PostgreSQL's locking grammar is the trailing
    /// clause this dialect already answers from <see cref="RowLockClause"/>. Hinting the table source as well
    /// would be locking twice, which the port forbids.
    /// </remarks>
    public string RenderTable(EntitySchema entity, PreImageMutation? lockedPreImageFor)
    {
        ArgumentNullException.ThrowIfNull(entity);
        return AlvoSqlIdentifier.Quote(entity.Name);
    }

    /// <inheritdoc/>
    public string RenderColumn(string columnName) => AlvoSqlIdentifier.Quote(columnName);

    /// <inheritdoc/>
    public string RenderNullProjection(string storeType)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(storeType);
        return $"CAST(NULL AS {storeType})";
    }

    /// <inheritdoc/>
    /// <remarks>
    /// PostgreSQL's own spelling, since 12: <c>&lt;column&gt; &lt;type&gt; GENERATED ALWAYS AS
    /// (&lt;expr&gt;) STORED</c>. It has no <c>VIRTUAL</c> at all before 18 — <c>ADD COLUMN … VIRTUAL</c> is a
    /// syntax error on 16 — which is the other half of why
    /// <see cref="IAlvoSqlDialect.GeneratedColumnDefinition"/> commits to stored rather than letting each
    /// engine pick.
    /// </remarks>
    public string? GeneratedColumnDefinition(string columnName, string storeType, string renderedExpression)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(columnName);
        ArgumentException.ThrowIfNullOrWhiteSpace(storeType);
        ArgumentException.ThrowIfNullOrWhiteSpace(renderedExpression);

        return $"{RenderColumn(columnName)} {storeType} GENERATED ALWAYS AS ({renderedExpression}) STORED";
    }

    /// <summary>SQLSTATE 23505 — <c>unique_violation</c> (PostgreSQL, Appendix A, class 23).</summary>
    private const string UniqueViolation = "23505";

    /// <summary>SQLSTATE 23503 — <c>foreign_key_violation</c>, what <c>ON DELETE RESTRICT</c> raises.</summary>
    private const string ForeignKeyViolation = "23503";

    /// <summary>SQLSTATE 23514 — <c>check_violation</c>, what a field's validation raises.</summary>
    private const string CheckViolation = "23514";

    /// <summary>SQLSTATE 23P01 — <c>exclusion_violation</c>, what an <c>EXCLUDE</c> constraint raises.</summary>
    private const string ExclusionViolation = "23P01";

    /// <inheritdoc/>
    /// <remarks>
    /// <para>
    /// Decided from the <b>SQLSTATE</b> alone — the five-character code the standard defines and PostgreSQL
    /// documents per condition name — never from <c>MessageText</c>, which is localized by the server's
    /// <c>lc_messages</c> and would make this dialect's classification depend on an operator's locale.
    /// </para>
    /// <para>
    /// <b>The constraint name is the only handle, and that is Npgsql's default rather than an omission.</b>
    /// <c>PostgresException.ConstraintName</c> is populated for both classes, while the <c>Detail</c> line
    /// that would carry the columns — <c>Key (tenant_id, reference)=(…)</c> — is withheld unless the
    /// connection string sets <c>Include Error Detail</c>. It is withheld because it quotes the offending
    /// <em>values</em>, so asking for it to read column names would pull caller data into every logged
    /// exception. The name is enough: the shared data path matches it against the model's own index names.
    /// </para>
    /// <para>
    /// A foreign-key violation reports the <em>referencing</em> table and constraint, not this row's — which
    /// is why nothing but the kind is returned for it. The refusal names no entity; see
    /// <see cref="AlvoConstraintViolationException"/>.
    /// </para>
    /// </remarks>
    public SqlConstraintViolation? DecodeConstraintViolation(DbException failure)
    {
        ArgumentNullException.ThrowIfNull(failure);
        if (failure is not PostgresException postgres)
        {
            return null;
        }

        return postgres.SqlState switch
        {
            UniqueViolation => new SqlConstraintViolation
            {
                Kind = AlvoConstraintKind.Unique,
                ConstraintName = NullIfEmpty(postgres.ConstraintName),
            },
            ForeignKeyViolation => new SqlConstraintViolation { Kind = AlvoConstraintKind.Referenced },
            CheckViolation => new SqlConstraintViolation
            {
                Kind = AlvoConstraintKind.Check,
                ConstraintName = NullIfEmpty(postgres.ConstraintName),
            },
            ExclusionViolation => new SqlConstraintViolation
            {
                Kind = AlvoConstraintKind.Exclusion,
                ConstraintName = NullIfEmpty(postgres.ConstraintName),
            },
            _ => null,
        };
    }

    /// <summary>SQLSTATE 55P03 — <c>lock_not_available</c>, what a statement raises when <c>lock_timeout</c> runs out.</summary>
    private const string LockNotAvailable = "55P03";

    /// <inheritdoc/>
    /// <remarks>
    /// <c>SET LOCAL</c>, so the bound ends with the apply's transaction and never leaks onto a pooled connection. Five
    /// seconds is long enough for an ordinary request transaction ahead of the apply to finish and short enough that the
    /// readers queued behind the apply's lock request stall at most that long (HX-11 Ruling 20).
    /// </remarks>
    string? IAlvoSqlDialect.ApplyLockTimeoutStatement => "SET LOCAL lock_timeout = '5s'";

    /// <inheritdoc/>
    /// <remarks>Decided from the SQLSTATE alone, like <see cref="DecodeConstraintViolation"/>.</remarks>
    bool IAlvoSqlDialect.IsLockTimeout(DbException failure) => failure is PostgresException { SqlState: LockNotAvailable };

    /// <summary>SQLSTATE 42P17 — <c>invalid_object_definition</c>, what an index predicate calling a non-immutable function raises.</summary>
    private const string InvalidObjectDefinition = "42P17";

    /// <inheritdoc/>
    /// <remarks>Decided from the SQLSTATE alone, like <see cref="DecodeConstraintViolation"/>.</remarks>
    bool IAlvoSqlDialect.IsIndexPredicateRefusal(DbException failure) => failure is PostgresException { SqlState: InvalidObjectDefinition };

    /// <inheritdoc/>
    /// <remarks>
    /// <c>SHARE</c> admits readers and the apply's own statements, and conflicts with the <c>ROW EXCLUSIVE</c> every insert,
    /// update and delete takes — so a child write queues behind the apply and commits after it, recomputing under the new
    /// definition (HX-11 Ruling 32). Held until the apply commits, and bounded by its <c>lock_timeout</c> like every other
    /// lock the apply waits for.
    /// </remarks>
    string? IAlvoSqlDialect.BackfillLockStatement(EntitySchema child)
    {
        ArgumentNullException.ThrowIfNull(child);
        return $"LOCK TABLE {RenderTable(child, lockedPreImageFor: null)} IN SHARE MODE";
    }

    /// <summary>
    /// <see langword="null"/> for an absent name, because <c>PostgresException</c> answers
    /// <see cref="string.Empty"/> rather than <see langword="null"/> when the server sent no constraint
    /// field — and an empty name would then be matched against every index that also has none.
    /// </summary>
    /// <param name="name">The constraint name as the server reported it.</param>
    private static string? NullIfEmpty(string? name) => string.IsNullOrEmpty(name) ? null : name;
}
