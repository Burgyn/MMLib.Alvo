using MMLib.Alvo.Migrations;
using MMLib.Alvo.Schema;
using MMLib.Alvo.Testing.Migrations;

namespace MMLib.Alvo.Tests.Migrations;

public sealed class SchemaDiffTests
{
    private static SchemaModel Model(FieldSchema field) =>
        new([
            new EntitySchema
            {
                Name = "t",
                Fields = [new FieldSchema { Name = "id", Type = FieldType.Uuid, Required = true }, field],
            },
        ]);

    private static MigrationStep AlterStepFor(FieldSchema before, FieldSchema after) =>
        SchemaDiff.Compute(Model(before), Model(after)).Single(s => s.Change.Kind == SchemaChangeKind.AlterField);

    [Fact]
    public void Nullable_to_non_nullable_is_a_destructive_alter()
    {
        var step = AlterStepFor(
            new FieldSchema { Name = "note", Type = FieldType.String, Nullable = true },
            new FieldSchema { Name = "note", Type = FieldType.String, Nullable = false });

        step.IsDestructive.ShouldBeTrue();
    }

    [Fact]
    public void Unbounded_to_bounded_maxlength_is_a_destructive_alter()
    {
        var step = AlterStepFor(
            new FieldSchema { Name = "code", Type = FieldType.String },
            new FieldSchema { Name = "code", Type = FieldType.String, MaxLength = 50 });

        step.IsDestructive.ShouldBeTrue();
    }

    [Fact]
    public void Shrinking_maxlength_is_a_destructive_alter()
    {
        var step = AlterStepFor(
            new FieldSchema { Name = "code", Type = FieldType.String, MaxLength = 100 },
            new FieldSchema { Name = "code", Type = FieldType.String, MaxLength = 20 });

        step.IsDestructive.ShouldBeTrue();
    }

    [Fact]
    public void Widening_maxlength_is_a_non_destructive_alter()
    {
        var step = AlterStepFor(
            new FieldSchema { Name = "code", Type = FieldType.String, MaxLength = 20 },
            new FieldSchema { Name = "code", Type = FieldType.String, MaxLength = 100 });

        step.IsDestructive.ShouldBeFalse();
    }

    [Fact]
    public void Precision_shrink_is_a_destructive_alter()
    {
        var step = AlterStepFor(
            new FieldSchema { Name = "amount", Type = FieldType.Decimal, Precision = 18, Scale = 2 },
            new FieldSchema { Name = "amount", Type = FieldType.Decimal, Precision = 10, Scale = 2 });

        step.IsDestructive.ShouldBeTrue();
    }

    [Fact]
    public void Scale_shrink_is_a_destructive_alter()
    {
        var step = AlterStepFor(
            new FieldSchema { Name = "amount", Type = FieldType.Decimal, Precision = 18, Scale = 4 },
            new FieldSchema { Name = "amount", Type = FieldType.Decimal, Precision = 18, Scale = 2 });

        step.IsDestructive.ShouldBeTrue();
    }

    [Fact]
    public void Type_change_is_a_destructive_alter()
    {
        var step = AlterStepFor(
            new FieldSchema { Name = "code", Type = FieldType.String },
            new FieldSchema { Name = "code", Type = FieldType.Integer });

        step.IsDestructive.ShouldBeTrue();
    }

    [Fact]
    public void Required_only_flip_with_unchanged_nullability_yields_no_step()
    {
        // Required does not drive the physical column (only Nullable does), so flipping it alone is
        // not a schema change — mirrors the real EF path, which never reads Required.
        var steps = SchemaDiff.Compute(
            Model(new FieldSchema { Name = "note", Type = FieldType.String, Nullable = true, Required = false }),
            Model(new FieldSchema { Name = "note", Type = FieldType.String, Nullable = true, Required = true }));

        steps.ShouldNotContain(s => s.Change.Kind == SchemaChangeKind.AlterField);
    }
}
