namespace MMLib.Alvo.Data.EntityFrameworkCore;

/// <summary>
/// What a driver managed to recover from its engine's own constraint-violation exception, before anything
/// has been resolved against the model — the raw half of the translation
/// <see cref="IAlvoSqlDialect.DecodeConstraintViolation"/> performs.
/// </summary>
/// <remarks>
/// <para>
/// <b>Two ways to name the constraint, because the engines genuinely differ and pretending otherwise loses
/// one of them.</b> PostgreSQL reports the violated constraint by <em>name</em> (<c>PostgresException</c>'s
/// <c>ConstraintName</c>, e.g. <c>IX_work_orders_tenant_id_reference</c>) and, with <c>Include Error
/// Detail</c> off — Npgsql's default, and the right default, since the detail line carries the offending
/// values — reports no columns at all. SQLite reports no name (there is no such property on
/// <c>SqliteException</c>) but does name the <em>columns</em>, and its foreign-key failure names neither.
/// A shape offering only one of the two would force one driver to synthesize the other, which is how a
/// decoder starts inventing facts. Both are optional; a driver fills in whichever its engine gives it, and
/// the EF data path resolves either against the entity's own model.
/// </para>
/// <para>
/// <b>It carries no message, and no engine text of any kind.</b> Everything the caller is told is built
/// from <see cref="AlvoConstraintKind"/> and the field names the model resolves — see
/// <see cref="AlvoConstraintViolationException"/> for why. The provider's exception survives as the
/// translated exception's inner one, which is where a host's logging reads it.
/// </para>
/// </remarks>
public sealed record SqlConstraintViolation
{
    /// <summary>Which constraint the engine refused the statement on.</summary>
    public required AlvoConstraintKind Kind { get; init; }

    /// <summary>
    /// The engine's own name for the violated constraint, or <see langword="null"/> when it reports none.
    /// Matched against the model's index names; never shown to a caller.
    /// </summary>
    public string? ConstraintName { get; init; }

    /// <summary>
    /// The column names the engine named, or empty when it named none. Unqualified — a driver whose message
    /// qualifies them (<c>table.column</c>) strips the table itself, because only that driver knows its own
    /// engine's spelling.
    /// </summary>
    public IReadOnlyList<string> Columns { get; init; } = [];
}
