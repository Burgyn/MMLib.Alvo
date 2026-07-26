using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Storage;
using System.Data.Common;
using System.Globalization;

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
/// <b>The column is the authority, not the value.</b> The representation EF wrote is decided by the
/// column's CLR type, so <see cref="Bind(IProperty, string, object?)"/> — the overload every call site
/// that knows its column must use — takes the mapping from the property and converts the value to the
/// property's type first. Choosing the mapping from the value's own type instead is the same class of
/// silent miss: a <c>uuid</c> column compared against a value that arrived as a <see cref="string"/>, or
/// a <c>date</c> column against a <see cref="DateTimeOffset"/> (which is what a <c>Timestamp</c>-typed
/// CEL operand becomes, since the type checker collapses <c>date</c> and <c>timestamp</c> into one CEL
/// type), matches nothing and raises nothing.
/// </para>
/// <para>
/// <see cref="Bind(IReadOnlyDictionary{string, object?}[])"/> stays value-typed because a rendered
/// <c>SqlPredicate</c>'s parameter bag records names and values only — it carries no field, so there is no
/// column to consult. That is sound for the predicates PR2 renders: the type checker forces both operands
/// of a non-numeric comparison to one CEL type, and the one reachable numeric mismatch is a
/// <c>Decimal</c> comparison, whose operands <c>IFieldSqlRenderer.RenderComparableOperand</c> normalises
/// on both sides. It is <em>not</em> sound for a caller-supplied value, which is why the filter, keyset
/// and row-id call sites bind through the column.
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
    /// Binds every value in <paramref name="bags"/>, refusing a name two bags both claim — the one place a
    /// forgotten explicit parameter prefix would otherwise substitute one predicate's value into another's
    /// comparison, silently and with no exception from the engine.
    /// </summary>
    /// <exception cref="InvalidOperationException">Two bags bound the same parameter name.</exception>
    internal DbParameter[] Bind(params IReadOnlyDictionary<string, object?>[] bags)
    {
        ArgumentNullException.ThrowIfNull(bags);
        using var command = _connection.CreateCommand();
        var bound = new Dictionary<string, DbParameter>(StringComparer.Ordinal);

        foreach (var (name, value) in bags.SelectMany(Pairs))
        {
            RequireUnclaimed(bound, name);
            bound[name] = Bind(command, name, value);
        }

        return [.. bound.Values];
    }

    internal DbParameter Bind(string name, object? value)
    {
        using var command = _connection.CreateCommand();
        return Bind(command, name, value);
    }

    /// <summary>
    /// Binds <paramref name="value"/> for a comparison against <paramref name="column"/> — through that
    /// column's own type mapping, and after converting the value to the column's CLR type.
    /// </summary>
    /// <exception cref="InvalidOperationException"><paramref name="column"/> cannot hold <paramref name="value"/>.</exception>
    internal DbParameter Bind(IProperty column, string name, object? value)
    {
        ArgumentNullException.ThrowIfNull(column);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        using var command = _connection.CreateCommand();

        return column.GetRelationalTypeMapping()
            .CreateParameter(command, Marker(name), AsColumnType(value, column), nullable: true);
    }

    private static IEnumerable<KeyValuePair<string, object?>> Pairs(IReadOnlyDictionary<string, object?> bag)
    {
        ArgumentNullException.ThrowIfNull(bag);
        return bag;
    }

    private static void RequireUnclaimed(Dictionary<string, DbParameter> bound, string name)
    {
        if (bound.ContainsKey(name))
        {
            throw new InvalidOperationException(
                $"Two predicates both bound the parameter name '{name}'. Each predicate must be rendered with its own " +
                "parameter prefix, or one predicate's value is substituted into another's comparison.");
        }
    }

    private DbParameter Bind(DbCommand command, string name, object? value)
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

    /// <summary>
    /// Converts <paramref name="value"/> to <paramref name="column"/>'s CLR type, so the mapping binds the
    /// representation the column holds. A value the column cannot hold is refused rather than coerced into
    /// something arbitrary: a wrong-but-plausible value is exactly the silent miss this class prevents, and
    /// the caller — a caller filter, a keyset cursor — has a structured error to report instead.
    /// </summary>
    private static object? AsColumnType(object? value, IProperty column)
    {
        if (value is null)
        {
            return null;
        }

        var target = Nullable.GetUnderlyingType(column.ClrType) ?? column.ClrType;

        return target.IsInstanceOfType(value) ? value : Converted(value, target, column);
    }

    private static object Converted(object value, Type target, IProperty column)
    {
        try
        {
            return Convert(value, target);
        }
        catch (Exception exception)
            when (exception is FormatException or InvalidCastException or OverflowException or ArgumentException)
        {
            throw new InvalidOperationException(
                $"A value of type '{value.GetType()}' cannot be compared against column '{column.Name}', which holds " +
                $"'{target}'. Supply a value the column's type can hold.",
                exception);
        }
    }

    /// <summary>
    /// The conversions <see cref="System.Convert.ChangeType(object?, Type, IFormatProvider?)"/> cannot
    /// do — none of <see cref="Guid"/>, <see cref="DateOnly"/>, <see cref="DateTimeOffset"/> and
    /// <see cref="TimeOnly"/> implements <see cref="IConvertible"/> — plus that method for the numeric,
    /// string and boolean cases it does handle.
    /// </summary>
    private static object Convert(object value, Type target)
    {
        if (target == typeof(Guid))
        {
            return Guid.Parse(AsText(value), CultureInfo.InvariantCulture);
        }

        if (target == typeof(DateOnly))
        {
            return AsDate(value);
        }

        if (target == typeof(DateTimeOffset))
        {
            return AsInstant(value);
        }

        return target == typeof(TimeOnly)
            ? TimeOnly.Parse(AsText(value), CultureInfo.InvariantCulture)
            : System.Convert.ChangeType(value, target, CultureInfo.InvariantCulture);
    }

    /// <summary>
    /// A <c>date</c> column takes the calendar date the caller wrote, read in the offset they wrote it
    /// with — not the UTC date, which would shift the day for any caller east or west of UTC.
    /// </summary>
    private static DateOnly AsDate(object value) => value switch
    {
        DateTimeOffset offset => DateOnly.FromDateTime(offset.DateTime),
        DateTime instant => DateOnly.FromDateTime(instant),
        _ => DateOnly.Parse(AsText(value), CultureInfo.InvariantCulture),
    };

    private static DateTimeOffset AsInstant(object value) => value switch
    {
        DateTime instant => new DateTimeOffset(instant),
        DateOnly date => new DateTimeOffset(date.ToDateTime(TimeOnly.MinValue), TimeSpan.Zero),
        _ => DateTimeOffset.Parse(AsText(value), CultureInfo.InvariantCulture),
    };

    private static string AsText(object value) =>
        value as string ?? throw new InvalidCastException($"'{value.GetType()}' cannot be read as text.");
}
