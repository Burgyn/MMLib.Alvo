using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Migrations.Operations;
using MMLib.Alvo.Migrations;

namespace MMLib.Alvo.Data.EntityFrameworkCore;

/// <summary>
/// The drops of what the database holds for the references a rename released (see <see cref="RenamePrePass"/>): each
/// released foreign key, the index EF keeps for it, and a target's alternate key no remaining foreign key uses — by the
/// names the <em>current</em> model gives them, which are the names both engines carried through <c>RENAME</c>.
/// </summary>
/// <remarks>
/// <para>
/// <b>Found by comparing two models rather than by spelling a name.</b> The current model is built from the schema as the
/// database holds it, the aligned one from the same schema with the renames applied and the touched references released;
/// a foreign key of the first with no counterpart in the second — compared by table and columns, renames mapped — is one a
/// rename released. EF's conventions stay the one authority on every constraint and index name.
/// </para>
/// <para>
/// <b>They run after the renames</b>, so a table is named as the renames left it, while a constraint or an index keeps the
/// name it was created under. The index is dropped <c>IF EXISTS</c>, the way a released partial index is, because on SQLite
/// a table rebuild the same plan triggers may already have dropped it.
/// </para>
/// </remarks>
internal static class ReleasedReferenceDrops
{
    /// <summary>The drops, foreign keys first, then their indexes, then the alternate keys left without a referrer.</summary>
    /// <param name="renames">The declared renames, as <see cref="RenamePrePass"/> computed them.</param>
    /// <param name="current">Builds the model of the schema the database holds, before the renames — only when there are any.</param>
    /// <param name="aligned">The model of <see cref="RenamePrePass.Result.AlignedCurrent"/>.</param>
    internal static IReadOnlyList<RenamePrePass.RenameEntry> For(
        IReadOnlyList<RenamePrePass.RenameEntry> renames, Func<IModel> current, IModel aligned)
    {
        if (renames.Count == 0)
        {
            return [];
        }

        var names = RenamedNames.From(renames);
        var kept = aligned.GetEntityTypes()
            .SelectMany(type => type.GetForeignKeys())
            .Select(RenamedNames.None.Signature)
            .ToHashSet(StringComparer.Ordinal);
        var released = current().GetEntityTypes()
            .SelectMany(type => type.GetForeignKeys())
            .Where(key => !kept.Contains(names.Signature(key)))
            .ToList();

        return [.. released.Select(key => KeyDrop(key, names)), .. IndexDrops(released, aligned, names), .. AlternateKeyDrops(released, names)];
    }

    private static RenamePrePass.RenameEntry KeyDrop(IForeignKey key, RenamedNames names) => Entry(new DropForeignKeyOperation
    {
        Table = names.Table(Table(key.DeclaringEntityType)),
        Name = key.GetConstraintName()!,
    });

    // The index EF keeps over a released key's columns, unless the aligned model still declares one over them (a field's own
    // `indexed`), which the residual diff then leaves alone.
    private static IEnumerable<RenamePrePass.RenameEntry> IndexDrops(List<IForeignKey> released, IModel aligned, RenamedNames names)
    {
        var kept = aligned.GetEntityTypes()
            .SelectMany(type => type.GetIndexes())
            .Select(index => RenamedNames.None.Columns(Table(index.DeclaringEntityType), index.Properties))
            .ToHashSet(StringComparer.Ordinal);

        return released
            .SelectMany(key => key.DeclaringEntityType.GetIndexes()
                .Where(index => index.Properties.SequenceEqual(key.Properties) && index.GetFilter() is null))
            .Where(index => !kept.Contains(names.Columns(Table(index.DeclaringEntityType), index.Properties)))
            .Distinct()
            .Select(index => IndexDrop(names.Table(Table(index.DeclaringEntityType)), index.GetDatabaseName()!));
    }

    private static RenamePrePass.RenameEntry IndexDrop(string table, string name) => new(
        new SqlOperation { Sql = $"DROP INDEX IF EXISTS {AlvoSqlIdentifier.Quote(name)}" },
        new SchemaChange { Kind = SchemaChangeKind.DropIndex, Entity = table, Detail = name });

    // A target's alternate key every one of whose referrers was released: the aligned model, which declares no released
    // reference, has no key there, and the residual diff adds it back under the target's new name.
    private static IEnumerable<RenamePrePass.RenameEntry> AlternateKeyDrops(List<IForeignKey> released, RenamedNames names) =>
        released
            .Select(key => key.PrincipalKey)
            .Where(key => !key.IsPrimaryKey() && key.GetReferencingForeignKeys().All(released.Contains))
            .Distinct()
            .Select(key => Entry(new DropUniqueConstraintOperation
            {
                Table = names.Table(Table(key.DeclaringEntityType)),
                Name = key.GetName()!,
            }));

    private static RenamePrePass.RenameEntry Entry(MigrationOperation operation) => new(operation, DestructiveScan.Classify(operation));

    private static string Table(IReadOnlyEntityType type) => type.GetTableName()!;

    /// <summary>
    /// The declared renames as lookups from a current table or column name to its aligned one, and the signatures — table
    /// and columns, never a constraint's name — two models' keys and indexes are compared by.
    /// </summary>
    private sealed class RenamedNames
    {
        /// <summary>No renames: the signatures of a model already aligned.</summary>
        internal static RenamedNames None { get; } = new();

        private readonly Dictionary<string, string> _tables = new(StringComparer.Ordinal);

        private readonly Dictionary<(string Table, string Column), string> _columns = [];

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

        internal string Table(string table) => _tables.GetValueOrDefault(table, table);

        internal string Columns(string table, IReadOnlyList<IProperty> properties)
        {
            var aligned = Table(table);
            var columns = properties.Select(property => property.GetColumnName())
                .Select(column => _columns.GetValueOrDefault((aligned, column), column));
            return $"{aligned}({string.Join(',', columns)})";
        }

        internal string Signature(IForeignKey key) =>
            $"{Columns(ReleasedReferenceDrops.Table(key.DeclaringEntityType), key.Properties)}>"
            + $"{Columns(ReleasedReferenceDrops.Table(key.PrincipalEntityType), key.PrincipalKey.Properties)}";
    }
}
