using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Migrations.Operations;
using MMLib.Alvo.Migrations;

namespace MMLib.Alvo.Data.EntityFrameworkCore;

/// <summary>
/// The indexes and primary keys a declared rename leaves under an old name, given the names the model gives them (#523):
/// every unfiltered index of a renamed table or over a renamed column, and a renamed table's primary key.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why.</b> EF names an index and a key from the table and column names, and both engines carry them through
/// <c>RENAME</c> unchanged — while the aligned model (<see cref="RenamePrePass"/>) already names them after the renames, so
/// the residual diff sees nothing to do. Left alone, a later plan that drops such an index names one the database does not
/// have, and an entity or field that later takes an old name collides with the index or key still holding it.
/// </para>
/// <para>
/// <b>Found by comparing two models, the way <see cref="ReleasedReferenceDrops"/> finds a released reference:</b> an index
/// of the model the database holds is paired with the aligned model's index over the same columns (renames mapped) and of
/// the same uniqueness, and renamed when EF names the two differently. A partial index, an exclusion and a released
/// reference's index are already dropped and re-created by the pre-pass; none of them has a counterpart here.
/// </para>
/// <para>
/// <b>An index is renamed by EF's own <see cref="RenameIndexOperation"/></b>, which each engine's generator spells — an
/// <c>ALTER INDEX … RENAME</c> on PostgreSQL, a drop and a create from the desired model on SQLite, which cannot rename an
/// index. That create needs the index in the desired model; one the desired model drops in the same plan is not renamed but
/// <see cref="Readdress">readdressed</see>: the residual's drop names the index by the name the database holds.
/// A primary key has no EF rename, so it is renamed by the dialect's
/// <see cref="IAlvoSqlDialect.RenamePrimaryKeyStatement"/>, on the engines where its name is taken by anything.
/// </para>
/// </remarks>
internal sealed class RenamedConstraints
{
    private readonly Dictionary<(string Table, string Name), string> _heldNames = [];

    private RenamedConstraints(IReadOnlyList<RenamePrePass.RenameEntry> renames) => Renames = renames;

    /// <summary>No rename: nothing renamed and nothing to readdress.</summary>
    internal static RenamedConstraints None { get; } = new([]);

    /// <summary>The renames, to run after the declared table and column renames.</summary>
    internal IReadOnlyList<RenamePrePass.RenameEntry> Renames { get; }

    /// <summary>What the declared renames leave under an old name.</summary>
    /// <param name="names">The declared renames' lookups.</param>
    /// <param name="current">The model of the schema the database holds, before the renames.</param>
    /// <param name="aligned">The model of <see cref="RenamePrePass.Result.AlignedCurrent"/>.</param>
    /// <param name="desired">The model the plan reaches.</param>
    /// <param name="dialect">The engine's dialect, which renames a primary key.</param>
    internal static RenamedConstraints For(RenamedNames names, IModel current, IModel aligned, IModel desired, IAlvoSqlDialect dialect)
    {
        var renames = new List<RenamePrePass.RenameEntry>();
        var constraints = new RenamedConstraints(renames);
        foreach (var (index, alignedIndex) in RenamedIndexes(names, current, aligned))
        {
            constraints.RenameIndex(index, alignedIndex, desired, renames);
        }

        renames.AddRange(PrimaryKeyRenames(names, current, aligned, dialect));
        return constraints;
    }

    /// <summary>
    /// <paramref name="residual"/> with every drop of an index this plan would otherwise have renamed addressed by the name
    /// the database holds.
    /// </summary>
    /// <param name="residual">The residual diff.</param>
    internal IReadOnlyList<MigrationOperation> Readdress(IReadOnlyList<MigrationOperation> residual) =>
        _heldNames.Count == 0 ? residual : [.. residual.Select(ByHeldName)];

    private MigrationOperation ByHeldName(MigrationOperation operation) =>
        operation is DropIndexOperation { Table: { } table } drop && _heldNames.TryGetValue((table, drop.Name), out var held)
            ? new DropIndexOperation { Name = held, Table = table, Schema = drop.Schema }
            : operation;

    private static IEnumerable<(IIndex Index, IIndex Aligned)> RenamedIndexes(RenamedNames names, IModel current, IModel aligned)
    {
        var alignedIndexes = Unfiltered(aligned)
            .DistinctBy(index => RenamedNames.None.Signature(index), StringComparer.Ordinal)
            .ToDictionary(index => RenamedNames.None.Signature(index), StringComparer.Ordinal);
        return Unfiltered(current)
            .Select(index => (Index: index, Aligned: alignedIndexes.GetValueOrDefault(names.Signature(index))))
            .Where(pair => pair.Aligned is not null && !string.Equals(pair.Index.GetDatabaseName(), pair.Aligned.GetDatabaseName(), StringComparison.Ordinal))
            .Select(pair => (pair.Index, pair.Aligned!));
    }

    private static IEnumerable<IIndex> Unfiltered(IModel model) =>
        model.GetEntityTypes().SelectMany(type => type.GetIndexes()).Where(index => index.GetFilter() is null);

    private void RenameIndex(IIndex index, IIndex alignedIndex, IModel desired, List<RenamePrePass.RenameEntry> renames)
    {
        var table = RenamedNames.TableOf(alignedIndex.DeclaringEntityType);
        var name = index.GetDatabaseName()!;
        var newName = alignedIndex.GetDatabaseName()!;
        if (DeclaresIndex(desired, table, newName))
        {
            var rename = new RenameIndexOperation { Table = table, Name = name, NewName = newName };
            renames.Add(new RenamePrePass.RenameEntry(rename, DestructiveScan.Classify(rename)));
        }
        else
        {
            _heldNames[(table, newName)] = name;
        }
    }

    private static bool DeclaresIndex(IModel desired, string table, string name) =>
        desired.GetEntityTypes()
            .Where(type => string.Equals(RenamedNames.TableOf(type), table, StringComparison.Ordinal))
            .SelectMany(type => type.GetIndexes())
            .Any(index => string.Equals(index.GetDatabaseName(), name, StringComparison.Ordinal));

    private static IEnumerable<RenamePrePass.RenameEntry> PrimaryKeyRenames(
        RenamedNames names, IModel current, IModel aligned, IAlvoSqlDialect dialect) =>
        from type in current.GetEntityTypes()
        let table = names.Table(RenamedNames.TableOf(type))
        let name = type.FindPrimaryKey()?.GetName()
        let newName = AlignedType(aligned, table)?.FindPrimaryKey()?.GetName()
        where name is not null && newName is not null && !string.Equals(name, newName, StringComparison.Ordinal)
        let statement = dialect.RenamePrimaryKeyStatement(table, name, newName)
        where statement is not null
        select PrimaryKeyRename(table, name, newName, statement);

    private static IEntityType? AlignedType(IModel aligned, string table) =>
        aligned.GetEntityTypes().FirstOrDefault(type => string.Equals(RenamedNames.TableOf(type), table, StringComparison.Ordinal));

    private static RenamePrePass.RenameEntry PrimaryKeyRename(string table, string name, string newName, string statement) => new(
        new SqlOperation { Sql = statement },
        new SchemaChange
        {
            Kind = SchemaChangeKind.AlterField,
            Entity = table,
            Detail = $"Rename primary key '{name}' to '{newName}'.",
        });
}
