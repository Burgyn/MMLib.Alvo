using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.EntityFrameworkCore.Migrations.Operations;
using MMLib.Alvo.Schema;

namespace MMLib.Alvo.Data.EntityFrameworkCore.Tests;

/// <summary>
/// What <see cref="RenameGuessSplitter"/> hands EF when it splits a guessed rename: the two schemas each
/// scoped diff is taken between, which is where the Drop and the Add it emits come from.
/// </summary>
/// <remarks>
/// The splitter takes its model builder and its differ as parameters, so both are supplied here — the
/// builder records the <see cref="SchemaModel"/> it is asked to build, and the differ answers with one
/// marker operation. That makes the scoped pair itself assertable, which the plan-level suites cannot do:
/// they see only the operations EF chose to emit for it, and a schema that dropped one field too many
/// produces a plan that still looks plausible.
/// </remarks>
public class RenameGuessSplitterScopedDiffTests
{
    /// <summary>
    /// A residual with nothing guessed in it is returned untouched — the same list, not a rebuilt copy of
    /// it, because that is the common case and every other operation has to keep its place in it.
    /// </summary>
    [Fact]
    public void A_residual_with_no_guessed_rename_is_returned_untouched()
    {
        IReadOnlyList<MigrationOperation> residual = [AddedColumn()];

        Normalize(residual, out _).ShouldBeSameAs(residual);
    }

    /// <summary>
    /// One guessed rename is enough. A residual that also carries ordinary operations must still be
    /// normalized, or accepting the guess would carry a dropped column's data into an unrelated new one and
    /// slip a destructive change past the guardrail.
    /// </summary>
    [Fact]
    public void A_guessed_rename_beside_ordinary_operations_is_still_split()
    {
        var added = AddedColumn();
        IReadOnlyList<MigrationOperation> residual = [added, ColumnRename()];

        var normalized = Normalize(residual, out _);

        normalized.OfType<RenameColumnOperation>().ShouldBeEmpty();
        normalized.ShouldContain(added, "every other operation keeps its place");
    }

    /// <summary>
    /// The field is removed from the entity the rename names and from no other. Two entities that happen to
    /// declare one field name are two different columns, and dropping both would diff a schema the
    /// descriptor never asked for.
    /// </summary>
    [Fact]
    public void Only_the_named_entity_loses_the_dropped_field()
    {
        Normalize([ColumnRename()], out var built);

        FieldNames(built[1], "orders").ShouldBe(["id"]);
        FieldNames(built[1], "invoices").ShouldBe(["id", "old_name"]);
    }

    /// <summary>
    /// An index over the removed field goes with it — <c>DescriptorModelBuilder</c>'s <c>HasIndex</c> would
    /// otherwise point at a property that no longer exists and <c>FinalizeModel</c> throws. Every other
    /// index stays, because the scoped diff has to differ in exactly one member.
    /// </summary>
    [Fact]
    public void Only_the_index_over_the_dropped_field_goes_with_it()
    {
        Normalize([ColumnRename()], out var built);

        Entity(built[1], "orders").Indexes.Select(index => index.Fields.Single()).ShouldBe(["id"]);
    }

    /// <summary>
    /// A guessed <em>table</em> rename is split the same way, and there too the scoped schema keeps every
    /// entity but the one being dropped.
    /// </summary>
    [Fact]
    public void A_guessed_table_rename_drops_only_the_entity_it_names()
    {
        Normalize([TableRename()], out var built);

        built[1].Entities.Select(entity => entity.Name).ShouldBe(["invoices"]);
    }

    /// <summary>
    /// A guessed column rename of a validated field whose validation also changed: the residual's own drop and add of the
    /// constraint are superseded by the scoped diffs' copies, so exactly one of each runs — the add carrying the
    /// <em>desired</em> expression, after the column it constrains — while a sibling's drop and add survive untouched.
    /// </summary>
    [Fact]
    public void A_guessed_rename_of_a_validated_field_drops_and_adds_its_constraint_once()
    {
        var sibling = (Drop: DropCheck("products", "ck_products_stock"), Add: AddCheck("products", "ck_products_stock", "stock >= 1"));
        IReadOnlyList<MigrationOperation> residual =
        [
            new RenameColumnOperation { Table = "products", Name = "rebate", NewName = "discount" },
            DropCheck("products", RebateCheck),
            AddCheck("products", DiscountCheck, "residual"),
            sibling.Drop,
            sibling.Add,
        ];
        var differ = new ScriptedDiffer(
            [DropCheck("products", RebateCheck), new DropColumnOperation { Table = "products", Name = "rebate" }],
            [new AddColumnOperation { Table = "products", Name = "discount", ClrType = typeof(long), IsNullable = true }, AddCheck("products", DiscountCheck, "desired")]);

        var normalized = RenameGuessSplitter.Normalize(residual, Current, Desired, Record([]), differ).ToList();

        normalized.OfType<DropCheckConstraintOperation>().Count(drop => drop.Name == RebateCheck).ShouldBe(1);
        var added = normalized.OfType<AddCheckConstraintOperation>().Where(add => add.Name == DiscountCheck).ShouldHaveSingleItem();
        added.Sql.ShouldBe("desired");
        normalized.IndexOf(added).ShouldBeGreaterThan(normalized.IndexOf(normalized.OfType<AddColumnOperation>().Single()));
        normalized.ShouldContain(sibling.Drop);
        normalized.ShouldContain(sibling.Add);
    }

    /// <summary>
    /// A guessed <em>table</em> rename of a validated entity is a DropTable and a CreateTable whose constraints are declared
    /// inline, so the residual's own drop and add of them are dropped: kept, the add would run twice and the drop would
    /// name a table the split removed. A check on another table survives.
    /// </summary>
    [Fact]
    public void A_guessed_table_rename_of_a_validated_entity_adds_its_constraints_only_inline()
    {
        var unrelated = AddCheck("invoices", "ck_invoices_total", "total >= 0");
        IReadOnlyList<MigrationOperation> residual =
        [
            TableRename(),
            DropCheck("orders", "ck_orders_total"),
            AddCheck("ledgers", "ck_ledgers_total", "total >= 0"),
            unrelated,
        ];
        var created = new CreateTableOperation { Name = "ledgers" };
        created.CheckConstraints.Add(AddCheck("ledgers", "ck_ledgers_total", "total >= 0"));
        var differ = new ScriptedDiffer([new DropTableOperation { Name = "orders" }], [created]);

        var normalized = RenameGuessSplitter.Normalize(residual, Current, Desired, Record([]), differ);

        normalized.ShouldBe([normalized[0], created, unrelated]);
        normalized[0].ShouldBeOfType<DropTableOperation>();
    }

    private const string RebateCheck = "ck_products_rebate_1";

    private const string DiscountCheck = "ck_products_discount_2";

    private static DropCheckConstraintOperation DropCheck(string table, string name) => new() { Table = table, Name = name };

    private static AddCheckConstraintOperation AddCheck(string table, string name, string sql) =>
        new() { Table = table, Name = name, Sql = sql };

    /// <summary>
    /// The four schemas the splitter builds a model from, in order: the current schema, the current schema
    /// without the dropped member, the desired schema without the added member, and the desired schema.
    /// </summary>
    private static IReadOnlyList<MigrationOperation> Normalize(
        IReadOnlyList<MigrationOperation> residual, out List<SchemaModel> built)
    {
        var recorded = new List<SchemaModel>();
        var result = RenameGuessSplitter.Normalize(residual, Current, Desired, Record(recorded), new MarkerDiffer());
        built = recorded;
        return result;
    }

    private static Func<SchemaModel, IModel> Record(List<SchemaModel> recorded) => schema =>
    {
        recorded.Add(schema);
        return RelationalModel;
    };

    private static IReadOnlyList<string> FieldNames(SchemaModel schema, string entity) =>
        [.. Entity(schema, entity).Fields.Select(field => field.Name)];

    private static EntitySchema Entity(SchemaModel schema, string entity) =>
        schema.Entities.Single(candidate => string.Equals(candidate.Name, entity, StringComparison.Ordinal));

    private static RenameColumnOperation ColumnRename() =>
        new() { Table = "orders", Name = "old_name", NewName = "new_name" };

    private static RenameTableOperation TableRename() => new() { Name = "orders", NewName = "ledgers" };

    private static AddColumnOperation AddedColumn() =>
        new() { Table = "invoices", Name = "note", ClrType = typeof(string), IsNullable = true };

    private static FieldSchema Id { get; } = new() { Name = "id", Type = FieldType.Uuid, Required = true };

    /// <summary>
    /// Two entities that share a field name, and two indexes on the renamed one — so "only this entity" and
    /// "only this index" are facts the schema can actually disprove.
    /// </summary>
    private static SchemaModel Current { get; } = new([
        new EntitySchema
        {
            Name = "orders",
            Fields = [Id, new FieldSchema { Name = "old_name", Type = FieldType.String, Nullable = true }],
            Indexes = [new IndexSchema(["old_name"], Unique: false), new IndexSchema(["id"], Unique: false)],
        },
        new EntitySchema
        {
            Name = "invoices",
            Fields = [Id, new FieldSchema { Name = "old_name", Type = FieldType.String, Nullable = true }],
        },
    ]);

    private static SchemaModel Desired { get; } = new([
        new EntitySchema
        {
            Name = "ledgers",
            Fields = [Id, new FieldSchema { Name = "new_name", Type = FieldType.String, Nullable = true }],
        },
        new EntitySchema
        {
            Name = "invoices",
            Fields = [Id, new FieldSchema { Name = "old_name", Type = FieldType.String, Nullable = true }],
        },
    ]);

    /// <summary>
    /// One real relational model, reused for every scoped diff. Which model it is does not matter — the
    /// differ here answers without reading it — but it has to be a real one, because the splitter asks it
    /// for its relational model on the way in.
    /// </summary>
    private static IModel RelationalModel { get; } = BuildModel();

    private static IModel BuildModel()
    {
        var options = new DbContextOptionsBuilder();
        options.UseSqlite("Data Source=:memory:");
        using var context = new AlvoDataContext(options.Options, Current, Guid.NewGuid());
        return context.Model;
    }

    /// <summary>Answers each scoped diff with the next scripted operation list, in the order the splitter asks.</summary>
    private sealed class ScriptedDiffer(params IReadOnlyList<MigrationOperation>[] answers) : IMigrationsModelDiffer
    {
        private int _next;

        public IReadOnlyList<MigrationOperation> GetDifferences(IRelationalModel? source, IRelationalModel? target) => answers[_next++];

        public bool HasDifferences(IRelationalModel? source, IRelationalModel? target) => true;
    }

    /// <summary>
    /// Answers every scoped diff with one marker operation, so the normalized list shows which operations
    /// the splitter replaced without a second production component deciding what they are.
    /// </summary>
    private sealed class MarkerDiffer : IMigrationsModelDiffer
    {
        public IReadOnlyList<MigrationOperation> GetDifferences(IRelationalModel? source, IRelationalModel? target) =>
            [new SqlOperation { Sql = "scoped diff" }];

        public bool HasDifferences(IRelationalModel? source, IRelationalModel? target) => true;
    }
}
