using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Storage;
using System.Data.Common;

namespace MMLib.Alvo.Data.EntityFrameworkCore;

/// <summary>
/// Turns a rendered predicate's parameter bag into real <see cref="DbParameter"/>s through <b>EF Core's
/// own relational type mapping</b> — the only binding guaranteed to agree with the representation EF used
/// when it wrote the column.
/// </summary>
/// <remarks>
/// <para>
/// Formatting a value into text instead is not a style choice with a cosmetic cost: EF's SQLite
/// <c>Guid</c> mapping stores an upper-case <c>TEXT</c>, so a lower-case hand-formatted Guid in a
/// <c>WHERE</c> clause matches no row and raises nothing at all. Under an equality predicate that fails
/// closed; under a negated one it fails open. <c>decimal</c>, <c>bool</c>, <c>DateTimeOffset</c> and
/// <c>DateOnly</c> are all stored as <c>TEXT</c>/<c>INTEGER</c> on SQLite by mappings only EF knows, so
/// the same argument applies to every type, not only to <see cref="Guid"/>.
/// </para>
/// <para>
/// <b>The column is the authority, not the value — and that is enforced by the argument types, not by a
/// convention.</b> The representation EF wrote is decided by the column's CLR type, so a value compared
/// against a column binds through <see cref="BindColumnValue"/>, which cannot be called without an
/// <see cref="IProperty"/>. There is deliberately <b>no</b> overload that takes a bare
/// <c>name → value</c> bag: an earlier shape of this class had one, every production call site used it,
/// and the column-aware overload — the one whose own documentation said it was mandatory — ended up with
/// zero callers while its tests kept passing. Choosing the mapping from the value's own type is the same
/// class of silent miss as formatting it: a <c>uuid</c> column compared against a value that arrived as a
/// <see cref="string"/>, or a <c>date</c> column against a <see cref="DateTimeOffset"/>, matches nothing
/// and raises nothing.
/// </para>
/// <para>
/// The two values with no column behind them each have their own narrowly named path, so neither can be
/// reached for by a caller who really is binding a caller value: <see cref="BindPolicyPredicate"/> for a
/// rendered <c>SqlPredicate</c>'s bag (which records names and values only — see
/// <see cref="BoundValue.FromPolicyPredicate"/> for why the CEL type checker makes that sufficient) and the
/// framework arm of <see cref="Bind(IEntityType, IReadOnlyDictionary{string, BoundValue})"/> for the page's
/// row limit.
/// </para>
/// </remarks>
internal sealed class PredicateParameterBinder
{
    private readonly IRelationalTypeMappingSource _mappings;
    private readonly ISqlGenerationHelper _sql;
    private readonly DbConnection _connection;

    internal PredicateParameterBinder(AlvoDataContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        _mappings = context.GetService<IRelationalTypeMappingSource>();
        _sql = context.GetService<ISqlGenerationHelper>();
        _connection = context.Database.GetDbConnection();
    }

    /// <summary>
    /// The provider's own bind-parameter marker, so the name this class creates and the name
    /// <see cref="MMLib.Alvo.Expressions.IFieldSqlRenderer.RenderParameter"/> writes into the SQL cannot
    /// drift apart. Hardcoding <c>@</c> here would be a second authority for the same decision, and a
    /// driver whose marker is <c>:</c> would produce a statement referencing a parameter that was never
    /// supplied. This is <see cref="ISqlGenerationHelper"/>'s parameter half only — the
    /// <c>DelimitIdentifier</c> half stays banned (spike <c>Q8</c>), because that one is about
    /// identifiers, which Alvo always quotes itself.
    /// </summary>
    private string Marker(string name) => _sql.GenerateParameterName(name);

    /// <summary>
    /// Binds every value one composed statement carries, dispatching on where each came from: a value
    /// compared against a column goes through that column's mapping, and the two column-less origins go
    /// through their own paths.
    /// </summary>
    /// <param name="rows">The read model's entity type, the one authority for what column a field maps to.</param>
    /// <param name="parameters">The statement's bound values, by parameter name.</param>
    /// <exception cref="InvalidOperationException">
    /// A value names a column this read model does not map, or a column cannot hold its value.
    /// </exception>
    internal DbParameter[] Bind(IEntityType rows, IReadOnlyDictionary<string, BoundValue> parameters)
    {
        ArgumentNullException.ThrowIfNull(rows);
        ArgumentNullException.ThrowIfNull(parameters);
        using var command = _connection.CreateCommand();

        return [.. parameters.Select(pair => Bind(command, rows, pair.Key, pair.Value))];
    }

    /// <summary>
    /// Binds a rendered policy predicate's bag, whose values carry no column. Named for exactly that case so
    /// it cannot stand in for <see cref="BindColumnValue"/>.
    /// </summary>
    /// <param name="parameters">The rendered predicate's values, by parameter name.</param>
    internal DbParameter[] BindPolicyPredicate(IReadOnlyDictionary<string, object?> parameters)
    {
        ArgumentNullException.ThrowIfNull(parameters);
        using var command = _connection.CreateCommand();

        return [.. parameters.Select(pair => WithoutColumn(command, pair.Key, pair.Value))];
    }

    /// <summary>
    /// Binds <paramref name="value"/> for a comparison against <paramref name="column"/> — through that
    /// column's own type mapping, and after converting the value to the column's CLR type.
    /// </summary>
    /// <exception cref="InvalidOperationException"><paramref name="column"/> cannot hold <paramref name="value"/>.</exception>
    internal DbParameter BindColumnValue(IProperty column, string name, object? value)
    {
        ArgumentNullException.ThrowIfNull(column);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        using var command = _connection.CreateCommand();

        return BindThroughColumn(command, column, name, value);
    }

    private DbParameter Bind(DbCommand command, IEntityType rows, string name, BoundValue bound) => bound.Origin switch
    {
        BoundValueOrigin.ColumnComparison => BindThroughColumn(command, Column(rows, bound.Column!), name, bound.Value),
        BoundValueOrigin.PolicyPredicate or BoundValueOrigin.Framework => WithoutColumn(command, name, bound.Value),
        _ => throw new InvalidOperationException(
            $"'{bound.Origin}' is not a known bound-value origin, so parameter '{name}' cannot be bound safely."),
    };

    /// <summary>
    /// The mapped property for a declared field name. A field this read model does not map has no
    /// representation to bind through, so it is refused rather than bound by the value's own type — the
    /// fallback that would silently reintroduce the defect this class's shape exists to prevent.
    /// </summary>
    private static IProperty Column(IEntityType rows, string field) => rows.FindProperty(field)
        ?? throw new InvalidOperationException(
            $"'{field}' is not mapped by this read model, so a value cannot be bound through its column.");

    /// <summary>
    /// Binds through the column's own mapping, after converting the value to what that column holds —
    /// through <see cref="ColumnValue"/>, the same funnel the write paths use. The conversion used to live
    /// here, which is how the write path came to have a different answer.
    /// </summary>
    private DbParameter BindThroughColumn(DbCommand command, IProperty column, string name, object? value) =>
        column.GetRelationalTypeMapping()
            .CreateParameter(command, Marker(name), ColumnValue.For(column.ClrType, column.Name, value), nullable: true);

    /// <summary>
    /// The one path for a value with no column behind it: the mapping comes from the value's own CLR type.
    /// Reachable only for a rendered policy predicate's values and for the framework's own row limit — both
    /// typed by something other than a caller.
    /// </summary>
    private DbParameter WithoutColumn(DbCommand command, string name, object? value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        var mapping = value is null ? null : _mappings.FindMapping(value.GetType());

        return mapping is null
            ? Untyped(command, name, value)
            : mapping.CreateParameter(command, Marker(name), value, nullable: true);
    }

    /// <summary>
    /// The two cases with no CLR type to map: a <see langword="null"/> value, and a value whose type the
    /// provider has no mapping for. The second is a bug in whoever produced it, not something to guess
    /// at — a value that reaches ADO.NET with a provider-inferred type is exactly the silent
    /// misrepresentation this class exists to prevent.
    /// </summary>
    private DbParameter Untyped(DbCommand command, string name, object? value)
    {
        if (value is not null)
        {
            throw new InvalidOperationException(
                $"No relational type mapping exists for '{value.GetType()}', so parameter '{name}' cannot be bound safely.");
        }

        var parameter = command.CreateParameter();
        parameter.ParameterName = Marker(name);
        parameter.Value = DBNull.Value;
        return parameter;
    }
}
