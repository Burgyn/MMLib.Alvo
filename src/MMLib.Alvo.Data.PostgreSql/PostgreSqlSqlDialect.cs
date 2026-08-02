using MMLib.Alvo.Data.EntityFrameworkCore;
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

    /// <summary>SQLSTATE 23505 — <c>unique_violation</c> (PostgreSQL, Appendix A, class 23).</summary>
    private const string UniqueViolation = "23505";

    /// <summary>SQLSTATE 23503 — <c>foreign_key_violation</c>, what <c>ON DELETE RESTRICT</c> raises.</summary>
    private const string ForeignKeyViolation = "23503";

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
            _ => null,
        };
    }

    /// <summary>
    /// <see langword="null"/> for an absent name, because <c>PostgresException</c> answers
    /// <see cref="string.Empty"/> rather than <see langword="null"/> when the server sent no constraint
    /// field — and an empty name would then be matched against every index that also has none.
    /// </summary>
    /// <param name="name">The constraint name as the server reported it.</param>
    private static string? NullIfEmpty(string? name) => string.IsNullOrEmpty(name) ? null : name;
}
