namespace MMLib.Alvo.Data.EntityFrameworkCore;

/// <summary>
/// The statements an engine needs run <b>outside</b> a migration's transaction, around the plan's own SQL —
/// the answer to <see cref="IAlvoSqlDialect.MigrationFraming"/>.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why "outside the transaction" is the whole point of this type.</b> SQLite's
/// <c>PRAGMA foreign_keys</c> is a documented <em>no-op</em> while a transaction is open. EF's own SQLite
/// generator emits that pragma around a table rebuild and marks those commands transaction-suppressed, but a
/// <see cref="Migrations.MigrationPlan"/> carries plain SQL strings and cannot carry the flag — so the pragma
/// arrived inside Alvo's single migration transaction and silently did nothing. The consequence was data
/// loss, not a warning: <c>DROP TABLE parent</c> performs an implicit <c>DELETE FROM</c> when foreign keys are
/// enforced, so a rebuild of a parent table <b>cascaded away the child rows</b> of every
/// <c>onDelete: "cascade"</c> reference to it. Measured on SQLite: one invoice and one invoice item in, one
/// invoice and <em>zero</em> items out.
/// </para>
/// <para>
/// It is a dialect answer rather than a branch in the migrator for the usual reason — the migrator must not
/// know which engine it is running on — and it is a <em>pair</em> rather than two members because a
/// suspension that is never restored is a different bug: the two halves are one decision and a dialect
/// answering only one of them should not be expressible.
/// </para>
/// </remarks>
public sealed record MigrationBatchFraming
{
    /// <summary>The framing for an engine that needs none — the default every dialect inherits.</summary>
    public static MigrationBatchFraming None { get; } = new();

    /// <summary>Statements run before the migration transaction begins.</summary>
    public IReadOnlyList<string> Before { get; init; } = [];

    /// <summary>Statements run after it commits, restoring whatever <see cref="Before"/> suspended.</summary>
    public IReadOnlyList<string> After { get; init; } = [];
}
