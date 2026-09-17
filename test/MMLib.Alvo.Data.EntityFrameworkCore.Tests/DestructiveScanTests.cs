using Microsoft.EntityFrameworkCore.Migrations.Operations;
using MMLib.Alvo.Migrations;

namespace MMLib.Alvo.Data.EntityFrameworkCore.Tests;

/// <summary>
/// <see cref="DestructiveScan.Classify"/> itself: which <see cref="SchemaChange"/> an EF migration
/// operation becomes, which entity it names, and which branch of the narrowing analysis a shrinking
/// bound takes.
/// </summary>
/// <remarks>
/// <para>
/// Deliberately complementary to <c>DestructiveClassificationParityTests</c> (real-vs-fake agreement on
/// <em>destructiveness</em> only) and to <c>EfCoreSchemaMigratorPlanTests</c> (the four narrowing cases
/// SQLite's affinity erasure hides from a planned migration). What is asserted here is the classifier's
/// own dispatch — the constraint, index and fallback arms no planner test reaches, and the entity name
/// each arm carries — plus the <em>branch</em> each narrowing analysis takes.
/// </para>
/// <para>
/// <b>A message's prose is not asserted.</b> Every <c>Detail</c> here is human-readable explanation, and
/// pinning its wording would freeze English into the suite and make a reworded sentence a failing test.
/// So a detail is asserted only where it carries a fact the classifier decided: that one exists at all,
/// that add and drop do not collapse onto the same one, and — for a narrowing alter — the bounds it
/// names, which is the only externally visible difference between the "shrinks from" and the "newly
/// bounded" branch.
/// </para>
/// </remarks>
public class DestructiveScanTests
{
    private const string Table = "orders";

    /// <summary>A constraint is a change to the table it constrains, whichever constraint it is.</summary>
    /// <param name="constraint">Which constraint operation is classified.</param>
    [Theory]
    [InlineData("add foreign key")]
    [InlineData("drop foreign key")]
    [InlineData("add primary key")]
    [InlineData("drop primary key")]
    [InlineData("add unique constraint")]
    [InlineData("drop unique constraint")]
    public void A_constraint_operation_is_an_alter_on_its_own_table(string constraint)
    {
        var change = DestructiveScan.Classify(Constraint(constraint));

        change.Kind.ShouldBe(SchemaChangeKind.AlterField);
        change.Entity.ShouldBe(Table);
    }

    /// <summary>
    /// A constraint change is the one classification with no field and no name change to read, so the
    /// detail is all a caller has to tell one from another — it must never be empty.
    /// </summary>
    /// <param name="constraint">Which constraint operation is classified.</param>
    [Theory]
    [InlineData("add foreign key")]
    [InlineData("drop foreign key")]
    [InlineData("add primary key")]
    [InlineData("drop primary key")]
    [InlineData("add unique constraint")]
    [InlineData("drop unique constraint")]
    public void A_constraint_operation_carries_a_detail(string constraint)
        => DestructiveScan.Classify(Constraint(constraint)).Detail.ShouldNotBeNullOrEmpty();

    /// <summary>Adding and dropping the same constraint are different changes, and say so.</summary>
    /// <param name="added">The adding operation.</param>
    /// <param name="dropped">The dropping operation.</param>
    [Theory]
    [InlineData("add foreign key", "drop foreign key")]
    [InlineData("add primary key", "drop primary key")]
    [InlineData("add unique constraint", "drop unique constraint")]
    public void Adding_and_dropping_a_constraint_do_not_share_a_detail(string added, string dropped)
    {
        var add = DestructiveScan.Classify(Constraint(added));
        var drop = DestructiveScan.Classify(Constraint(dropped));

        add.Detail.ShouldNotBe(drop.Detail);
    }

    /// <summary>A foreign key is attributed to the column it is declared on.</summary>
    [Fact]
    public void An_added_foreign_key_names_its_first_column_as_the_field()
        => DestructiveScan.Classify(AddForeignKey("owner_id", "tenant_id")).Field.ShouldBe("owner_id");

    /// <summary>
    /// EF's drop carries no column list at all, so there is no field to attribute the change to and the
    /// classifier must not reach for one.
    /// </summary>
    [Fact]
    public void A_dropped_foreign_key_has_no_field()
        => DestructiveScan.Classify(Constraint("drop foreign key")).Field.ShouldBeNull();

    /// <summary>An empty column list is the same "nothing to attribute it to" case as no list.</summary>
    [Fact]
    public void A_foreign_key_over_no_columns_has_no_field()
        => DestructiveScan.Classify(AddForeignKey()).Field.ShouldBeNull();

    [Fact]
    public void Dropping_a_table_is_destructive_and_explains_itself()
    {
        var change = DestructiveScan.Classify(new DropTableOperation { Name = Table });

        change.Kind.ShouldBe(SchemaChangeKind.DropEntity);
        change.IsDestructive.ShouldBeTrue();
        change.Detail.ShouldNotBeNullOrEmpty();
    }

    /// <summary>A rename is reported under the name the entity ends up with, remembering the old one.</summary>
    [Fact]
    public void Renaming_a_table_names_the_new_table_and_remembers_the_old()
    {
        var change = DestructiveScan.Classify(
            new RenameTableOperation { Name = Table, NewName = "purchase_orders" });

        change.Entity.ShouldBe("purchase_orders");
        change.FromName.ShouldBe(Table);
    }

    [Fact]
    public void Dropping_a_column_is_destructive_and_explains_itself()
    {
        var change = DestructiveScan.Classify(new DropColumnOperation { Table = Table, Name = "code" });

        change.Kind.ShouldBe(SchemaChangeKind.DropField);
        change.IsDestructive.ShouldBeTrue();
        change.Detail.ShouldNotBeNullOrEmpty();
    }

    /// <summary>
    /// EF types a dropped index's table as nullable, and the classification still has to be attributed to
    /// an entity — so the table it does carry must reach the change.
    /// </summary>
    [Fact]
    public void A_dropped_index_is_attributed_to_its_table()
    {
        var change = DestructiveScan.Classify(new DropIndexOperation { Table = Table, Name = "ix_orders_code" });

        change.Kind.ShouldBe(SchemaChangeKind.DropIndex);
        change.Entity.ShouldBe(Table);
    }

    [Fact]
    public void A_renamed_index_is_attributed_to_its_table()
    {
        var change = DestructiveScan.Classify(RenameIndex());

        change.Kind.ShouldBe(SchemaChangeKind.RenameIndex);
        change.Entity.ShouldBe(Table);
    }

    /// <summary>
    /// An index rename carries no <c>FromName</c>, so its detail is the only place the two names it moves
    /// between are recorded — the one part of this message that is contract rather than prose.
    /// </summary>
    [Fact]
    public void A_renamed_index_detail_names_both_index_names()
    {
        var detail = DetailOf(RenameIndex());

        detail.ShouldContain("ix_orders_code");
        detail.ShouldContain("ix_orders_reference");
    }

    /// <summary>
    /// An operation Alvo does not model is still attributed to its table, which the classifier can only
    /// reach reflectively — there is no common table-operation base type in EF's model.
    /// </summary>
    [Fact]
    public void An_unmodelled_operation_is_attributed_to_its_table_property()
    {
        var change = DestructiveScan.Classify(
            new DropCheckConstraintOperation { Table = Table, Name = "ck_orders_total" });

        change.Kind.ShouldBe(SchemaChangeKind.AlterField);
        change.Entity.ShouldBe(Table);
    }

    /// <summary>The operation type is what an unmodelled change has instead of a description.</summary>
    [Fact]
    public void An_unmodelled_operation_reports_its_operation_type()
        => DestructiveScan.Classify(new DropCheckConstraintOperation { Table = Table, Name = "ck" })
            .Detail.ShouldBe(nameof(DropCheckConstraintOperation));

    /// <summary>
    /// An operation with no table at all is still classified rather than refused: <see cref="SchemaChange.Entity"/>
    /// is required, so the scan falls back to an empty name instead of throwing on an unknown operation.
    /// </summary>
    [Fact]
    public void An_unmodelled_operation_without_a_table_is_classified_with_an_empty_entity()
        => DestructiveScan.Classify(new SqlOperation { Sql = "SELECT 1" }).Entity.ShouldBeEmpty();

    /// <summary>
    /// With no previous column there is nothing to compare against, so nothing can be shown to narrow —
    /// and an unknown alter must not be reported as destructive on a guess.
    /// </summary>
    [Fact]
    public void An_alter_with_no_previous_column_is_not_destructive()
    {
        var operation = new AlterColumnOperation
        {
            Table = Table,
            Name = "code",
            ClrType = typeof(string),
            OldColumn = null!,
        };

        DestructiveScan.Classify(operation).IsDestructive.ShouldBeFalse();
    }

    [Fact]
    public void An_alter_that_narrows_nothing_still_carries_a_detail()
    {
        var change = DestructiveScan.Classify(Alter(Old()));

        change.IsDestructive.ShouldBeFalse();
        change.Detail.ShouldNotBeNullOrEmpty();
    }

    /// <summary>
    /// The reason an alter is destructive replaces the generic detail rather than being dropped on the
    /// floor — a narrowing alter and a harmless one must not read the same.
    /// </summary>
    [Fact]
    public void A_narrowing_alter_does_not_reuse_the_generic_alter_detail()
    {
        var narrowing = DestructiveScan.Classify(Alter(Old(isNullable: true)));
        var harmless = DestructiveScan.Classify(Alter(Old()));

        narrowing.Detail.ShouldNotBe(harmless.Detail);
    }

    [Fact]
    public void Making_a_nullable_column_required_is_destructive_and_explains_itself()
    {
        var change = DestructiveScan.Classify(Alter(Old(isNullable: true)));

        change.IsDestructive.ShouldBeTrue();
        change.Detail.ShouldNotBeNullOrEmpty();
    }

    /// <summary>
    /// Both bounds reach the message when the column was already bounded — the shrink branch is the one
    /// that has an old bound to name, which is what distinguishes it from the newly-bounded branch.
    /// </summary>
    [Fact]
    public void A_shrinking_max_length_names_the_old_and_the_new_bound()
    {
        ShouldNarrowFrom(DetailOf(Alter(Old(maxLength: 100), maxLength: 20)), "100", "20");
    }

    /// <summary>An unbounded column gaining a bound has only the new one to name.</summary>
    [Fact]
    public void A_newly_bounded_max_length_names_the_new_bound()
        => DetailOf(Alter(Old(), maxLength: 50)).ShouldContain("50");

    [Fact]
    public void A_shrinking_precision_names_the_old_and_the_new_precision()
    {
        ShouldNarrowFrom(DetailOf(AlterDecimal(Old(Money, precision: 18), precision: 10)), "18", "10");
    }

    [Fact]
    public void A_newly_bounded_precision_names_the_new_precision()
        => DetailOf(AlterDecimal(Old(Money), precision: 9)).ShouldContain("9");

    [Fact]
    public void A_shrinking_scale_names_the_old_and_the_new_scale()
    {
        var old = Old(Money, precision: 18, scale: 4);

        ShouldNarrowFrom(DetailOf(AlterDecimal(old, precision: 18, scale: 2)), "4", "2");
    }

    [Fact]
    public void A_newly_bounded_scale_names_the_new_scale()
        => DetailOf(AlterDecimal(Old(Money, precision: 18), precision: 18, scale: 3)).ShouldContain("3");

    /// <summary>A type change names both types: which one it came from decides whether the data survives.</summary>
    [Fact]
    public void A_type_change_names_both_clr_types()
    {
        ShouldNarrowFrom(DetailOf(Alter(Old(), clrType: typeof(long))), nameof(String), nameof(Int64));
    }

    /// <summary>
    /// The detail names both bounds <b>and</b> names them in the direction the narrowing runs. Asserting
    /// only that both numbers appear is satisfied with the operands swapped — "Scale shrinks from 2 to 4"
    /// passes a pair of <c>ShouldContain</c>s — and an operator reads this sentence while deciding whether
    /// to set <c>AllowDestructive</c>. Position, not prose: the wording around the two values is free to
    /// change.
    /// </summary>
    /// <param name="detail">The classification's detail.</param>
    /// <param name="from">The bound being left, which must appear first.</param>
    /// <param name="to">The bound being moved to.</param>
    private static void ShouldNarrowFrom(string detail, string from, string to)
    {
        var start = detail.IndexOf(from, StringComparison.Ordinal);
        start.ShouldBeGreaterThanOrEqualTo(0, $"the detail must name the bound being left ('{from}'): {detail}");

        var end = detail.IndexOf(to, start + from.Length, StringComparison.Ordinal);
        end.ShouldBeGreaterThan(start, $"'{to}' must be named AFTER '{from}', or the narrowing reads backwards: {detail}");
    }

    private static Type Money => typeof(decimal);

    /// <summary>The detail of a classification that is required to carry one.</summary>
    /// <param name="operation">The operation to classify.</param>
    private static string DetailOf(MigrationOperation operation)
    {
        var detail = DestructiveScan.Classify(operation).Detail;
        detail.ShouldNotBeNullOrEmpty();
        return detail!;
    }

    private static MigrationOperation Constraint(string constraint) => constraint switch
    {
        "add foreign key" => AddForeignKey("owner_id"),
        "drop foreign key" => new DropForeignKeyOperation { Table = Table, Name = "fk_orders_owner" },
        "add primary key" => new AddPrimaryKeyOperation { Table = Table, Name = "pk_orders", Columns = ["id"] },
        "drop primary key" => new DropPrimaryKeyOperation { Table = Table, Name = "pk_orders" },
        "add unique constraint" => new AddUniqueConstraintOperation { Table = Table, Name = "uq_orders_code", Columns = ["code"] },
        "drop unique constraint" => new DropUniqueConstraintOperation { Table = Table, Name = "uq_orders_code" },
        _ => throw new ArgumentOutOfRangeException(nameof(constraint)),
    };

    private static AddForeignKeyOperation AddForeignKey(params string[] columns) => new()
    {
        Table = Table,
        Name = "fk_orders_owner",
        Columns = columns,
        PrincipalTable = "owners",
        PrincipalColumns = ["id"],
    };

    private static RenameIndexOperation RenameIndex() => new()
    {
        Table = Table,
        Name = "ix_orders_code",
        NewName = "ix_orders_reference",
    };

    private static AddColumnOperation Old(
        Type? clrType = null, bool isNullable = false, int? maxLength = null, int? precision = null, int? scale = null) => new()
        {
            Table = Table,
            Name = "code",
            ClrType = clrType ?? typeof(string),
            IsNullable = isNullable,
            MaxLength = maxLength,
            Precision = precision,
            Scale = scale,
        };

    private static AlterColumnOperation Alter(AddColumnOperation old, Type? clrType = null, int? maxLength = null) => new()
    {
        Table = Table,
        Name = "code",
        ClrType = clrType ?? old.ClrType,
        IsNullable = false,
        MaxLength = maxLength,
        OldColumn = old,
    };

    private static AlterColumnOperation AlterDecimal(AddColumnOperation old, int? precision = null, int? scale = null) => new()
    {
        Table = Table,
        Name = "amount",
        ClrType = Money,
        IsNullable = false,
        Precision = precision,
        Scale = scale,
        OldColumn = old,
    };
}
