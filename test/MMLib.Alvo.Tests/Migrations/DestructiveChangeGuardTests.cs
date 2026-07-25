using MMLib.Alvo.Migrations;

namespace MMLib.Alvo.Tests.Migrations;

public sealed class DestructiveChangeGuardTests
{
    [Fact]
    public void Describe_names_the_entity_and_field_of_a_destructive_drop_field_step()
    {
        var plan = new MigrationPlan
        {
            Steps =
            [
                new MigrationStep(
                    new SchemaChange
                    {
                        Kind = SchemaChangeKind.DropField,
                        Entity = "vehicles",
                        Field = "license_plate",
                        IsDestructive = true,
                    },
                    IsDestructive: true,
                    Reason: "drops field 'vehicles.license_plate' and its data"),
            ],
        };

        var summary = DestructiveChangeGuard.Describe(plan);

        summary.ShouldContain("vehicles.license_plate");
        summary.ShouldContain("DropField");
    }

    [Fact]
    public void Describe_ignores_non_destructive_steps()
    {
        var plan = new MigrationPlan
        {
            Steps =
            [
                new MigrationStep(
                    new SchemaChange { Kind = SchemaChangeKind.AddField, Entity = "vehicles", Field = "color" },
                    IsDestructive: false,
                    Reason: null),
            ],
        };

        var summary = DestructiveChangeGuard.Describe(plan);

        summary.ShouldNotContain("vehicles.color");
    }

    [Fact]
    public void Describe_returns_a_stable_message_when_there_are_no_destructive_steps()
    {
        var plan = new MigrationPlan { Steps = [] };

        DestructiveChangeGuard.Describe(plan).ShouldBe("No destructive changes.");
    }

    [Fact]
    public void Describe_names_only_the_entity_when_the_step_has_no_field()
    {
        var plan = new MigrationPlan
        {
            Steps =
            [
                new MigrationStep(
                    new SchemaChange { Kind = SchemaChangeKind.DropEntity, Entity = "vehicles", IsDestructive = true },
                    IsDestructive: true,
                    Reason: "drops entity 'vehicles' and all its data"),
            ],
        };

        var summary = DestructiveChangeGuard.Describe(plan);

        summary.ShouldBe("DropEntity vehicles: drops entity 'vehicles' and all its data");
    }

    [Fact]
    public void Describe_omits_the_colon_suffix_when_the_step_has_no_reason()
    {
        var plan = new MigrationPlan
        {
            Steps =
            [
                new MigrationStep(
                    new SchemaChange
                    {
                        Kind = SchemaChangeKind.DropField,
                        Entity = "vehicles",
                        Field = "color",
                        IsDestructive = true,
                    },
                    IsDestructive: true,
                    Reason: null),
            ],
        };

        var summary = DestructiveChangeGuard.Describe(plan);

        summary.ShouldBe("DropField vehicles.color");
    }

    [Fact]
    public void Describe_joins_multiple_destructive_steps_with_one_line_each()
    {
        var plan = new MigrationPlan
        {
            Steps =
            [
                new MigrationStep(
                    new SchemaChange
                    {
                        Kind = SchemaChangeKind.DropField,
                        Entity = "vehicles",
                        Field = "color",
                        IsDestructive = true,
                    },
                    IsDestructive: true,
                    Reason: "drops field 'vehicles.color' and its data"),
                new MigrationStep(
                    new SchemaChange { Kind = SchemaChangeKind.DropEntity, Entity = "inspections", IsDestructive = true },
                    IsDestructive: true,
                    Reason: "drops entity 'inspections' and all its data"),
            ],
        };

        var summary = DestructiveChangeGuard.Describe(plan);

        summary.ShouldBe(string.Join(
            Environment.NewLine,
            "DropField vehicles.color: drops field 'vehicles.color' and its data",
            "DropEntity inspections: drops entity 'inspections' and all its data"));
    }
}
