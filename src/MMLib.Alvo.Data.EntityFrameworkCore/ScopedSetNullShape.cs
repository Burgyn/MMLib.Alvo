namespace MMLib.Alvo.Data.EntityFrameworkCore;

/// <summary>
/// How an engine keeps <c>onDelete: setNull</c> on a reference between two scoped entities, whose foreign key spans
/// <c>(tenant_id, id)</c> (#161) — the answer of <see cref="IAlvoSqlDialect.ScopedSetNull"/>.
/// </summary>
/// <remarks>
/// A plain composite <c>ON DELETE SET NULL</c> nulls <em>every</em> column of the key, <c>tenant_id</c> included, and both
/// engines refuse that delete with a <c>NOT NULL</c> failure — measured on SQLite 3.54 and PostgreSQL 16. So the shape is
/// a capability, not one line.
/// </remarks>
internal enum ScopedSetNullShape
{
    /// <summary>
    /// The field's own single-column key keeps <c>SET NULL</c>, and a separate composite guard with <c>NO ACTION</c>
    /// refuses a cross-tenant value. The default; correct on SQLite in either constraint order.
    /// </summary>
    SeparateGuard,

    /// <summary>
    /// One composite key whose action names the column it clears, <c>ON DELETE SET NULL (field)</c> — PostgreSQL 15+, where
    /// the separate guard is unsafe because its trigger may fire before the action's.
    /// </summary>
    NamedColumns,
}
