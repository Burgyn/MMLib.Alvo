using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Storage;
using System.Data.Common;

namespace MMLib.Alvo.Data.EntityFrameworkCore;

/// <summary>
/// Turns a rendered predicate's parameter bag into real <see cref="DbParameter"/>s through <b>EF Core's
/// own relational type mapping</b> — the only binding guaranteed to agree with the representation EF used
/// when it wrote the column.
/// </summary>
/// <remarks>
/// Formatting a value into text instead is not a style choice with a cosmetic cost: EF's SQLite
/// <c>Guid</c> mapping stores an upper-case <c>TEXT</c>, so a lower-case hand-formatted Guid in a
/// <c>WHERE</c> clause matches no row and raises nothing at all. Under an equality predicate that fails
/// closed; under a negated one it fails open. <c>decimal</c>, <c>bool</c>, <c>DateTimeOffset</c> and
/// <c>DateOnly</c> are all stored as <c>TEXT</c>/<c>INTEGER</c> on SQLite by mappings only EF knows, so
/// the same argument applies to every type, not only to <see cref="Guid"/>.
/// </remarks>
internal sealed class PredicateParameterBinder
{
    private readonly IRelationalTypeMappingSource _mappings;
    private readonly DbConnection _connection;

    internal PredicateParameterBinder(AlvoDataContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        _mappings = context.GetService<IRelationalTypeMappingSource>();
        _connection = context.Database.GetDbConnection();
    }

    internal DbParameter[] Bind(params IReadOnlyDictionary<string, object?>[] bags)
    {
        ArgumentNullException.ThrowIfNull(bags);
        using var command = _connection.CreateCommand();
        return [.. bags.SelectMany(bag => bag).Select(pair => Bind(command, pair.Key, pair.Value))];
    }

    internal DbParameter Bind(string name, object? value)
    {
        using var command = _connection.CreateCommand();
        return Bind(command, name, value);
    }

    private DbParameter Bind(DbCommand command, string name, object? value)
    {
        var mapping = value is null ? null : _mappings.FindMapping(value.GetType());
        if (mapping is not null)
        {
            return mapping.CreateParameter(command, "@" + name, value, nullable: true);
        }

        return Untyped(command, name, value);
    }

    /// <summary>
    /// The two cases with no CLR type to map: a <see langword="null"/> value, and a value whose type the
    /// provider has no mapping for. The second is a bug in whoever produced it, not something to guess
    /// at — a value that reaches ADO.NET with a provider-inferred type is exactly the silent
    /// misrepresentation this class exists to prevent.
    /// </summary>
    private static DbParameter Untyped(DbCommand command, string name, object? value)
    {
        if (value is not null)
        {
            throw new InvalidOperationException(
                $"No relational type mapping exists for '{value.GetType()}', so parameter '{name}' cannot be bound safely.");
        }

        var parameter = command.CreateParameter();
        parameter.ParameterName = "@" + name;
        parameter.Value = DBNull.Value;
        return parameter;
    }
}
