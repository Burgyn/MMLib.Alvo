using MMLib.Alvo.Migrations;

namespace MMLib.Alvo.Abstractions.Tests.Migrations;

public class MigrationPlanTests
{
    [Fact]
    public void HasDestructiveChanges_is_true_when_any_step_is_destructive()
    {
        var plan = new MigrationPlan
        {
            Steps =
            [
                new MigrationStep(new SchemaChange { Kind = SchemaChangeKind.AddField, Entity = "v" }, false, null),
                new MigrationStep(new SchemaChange { Kind = SchemaChangeKind.DropField, Entity = "v", IsDestructive = true }, true, "drops column data"),
            ],
        };
        plan.HasDestructiveChanges.ShouldBeTrue();
        plan.IsEmpty.ShouldBeFalse();
    }
}
