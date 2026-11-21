using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Migrations.Operations;

namespace MMLib.Alvo.Data.EntityFrameworkCore;

/// <summary>
/// The declared renames as lookups from a current table or column name to its aligned one, and the signatures — table
/// and columns, never a constraint's name — by which a key or an index of the model the database holds is paired with
/// its counterpart in the aligned model (<see cref="RenamePrePass"/>).
/// </summary>
internal sealed class RenamedNames
{
    /// <summary>No renames: the signatures of a model already aligned.</summary>
    internal static RenamedNames None { get; } = new();

    private readonly Dictionary<string, string> _tables = new(StringComparer.Ordinal);

    private readonly Dictionary<(string Table, string Column), string> _columns = [];

    /// <summary>The lookups the declared <paramref name="renames"/> make.</summary>
    /// <param name="renames">The declared renames, as <see cref="RenamePrePass"/> computed them.</param>
    internal static RenamedNames From(IReadOnlyList<RenamePrePass.RenameEntry> renames)
    {
        var names = new RenamedNames();
        foreach (var rename in renames.Select(entry => entry.Operation))
        {
            if (rename is RenameTableOperation table)
            {
                names._tables[table.Name] = table.NewName!;
            }
            else if (rename is RenameColumnOperation column)
            {
                names._columns[(column.Table, column.Name)] = column.NewName;
            }
        }

        return names;
    }

    /// <summary>The table an entity type is mapped to.</summary>
    /// <param name="type">The entity type.</param>
    internal static string TableOf(IReadOnlyEntityType type) => type.GetTableName()!;

    /// <summary>The name <paramref name="table"/> has after the renames.</summary>
    /// <param name="table">A table as the database holds it.</param>
    internal string Table(string table) => _tables.GetValueOrDefault(table, table);

    /// <summary>The aligned table and columns of <paramref name="properties"/> on <paramref name="table"/>.</summary>
    /// <param name="table">A table as the model being signed names it.</param>
    /// <param name="properties">The key's or the index's properties.</param>
    internal string Columns(string table, IReadOnlyList<IProperty> properties)
    {
        var aligned = Table(table);
        var columns = properties.Select(property => property.GetColumnName())
            .Select(column => _columns.GetValueOrDefault((aligned, column), column));
        return $"{aligned}({string.Join(',', columns)})";
    }

    /// <summary>A foreign key's aligned columns and the aligned columns of the key it references.</summary>
    /// <param name="key">The foreign key.</param>
    internal string Signature(IForeignKey key) =>
        $"{Columns(TableOf(key.DeclaringEntityType), key.Properties)}>"
        + $"{Columns(TableOf(key.PrincipalEntityType), key.PrincipalKey.Properties)}";

    /// <summary>An unfiltered index's aligned columns and its uniqueness.</summary>
    /// <param name="index">The index.</param>
    internal string Signature(IIndex index) =>
        $"{Columns(TableOf(index.DeclaringEntityType), index.Properties)}{(index.IsUnique ? "!" : string.Empty)}";
}
