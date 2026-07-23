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
                    "-- drop field vehicles.license_plate",
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
                    "-- add field vehicles.color",
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
}
