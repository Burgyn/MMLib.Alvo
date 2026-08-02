using MMLib.Alvo.Migrations;

namespace MMLib.Alvo.Abstractions.Tests.Migrations;

/// <summary>
/// <see cref="MigrationResult.EnsureApplied"/>, which is the whole predicate stated four ways: refused
/// throws, and the three shapes of <c>Applied == false</c> that are <em>not</em> a refusal do not.
/// </summary>
public class MigrationResultTests
{
    [Fact]
    public void EnsureApplied_throws_and_names_the_step_when_a_destructive_plan_was_refused()
    {
        var result = new MigrationResult(Applied: false, DestructivePlan(), WasDryRun: false);

        var refusal = Should.Throw<DestructiveChangeNotAllowedException>(() => result.EnsureApplied());

        refusal.Message.ShouldContain("DropField");
        refusal.Message.ShouldContain("vehicles.license_plate");
        refusal.Message.ShouldContain("AllowDestructive");
    }

    /// <summary>
    /// The ordinary restart: an unchanged descriptor plans nothing, and the applied schema already matches.
    /// </summary>
    /// <remarks>
    /// The one that a guard of bare <c>!Applied</c> would break — and it is the common case, so breaking it
    /// is a worse outage than the silent one <see cref="MigrationResult.EnsureApplied"/> exists to prevent.
    /// </remarks>
    [Fact]
    public void EnsureApplied_passes_an_empty_plan_through()
    {
        var result = new MigrationResult(Applied: false, new MigrationPlan { Steps = [] }, WasDryRun: false);

        result.EnsureApplied().ShouldBeSameAs(result);
    }

    /// <summary>A dry run asked for a plan rather than an apply, so its <c>Applied == false</c> is the answer.</summary>
    [Fact]
    public void EnsureApplied_passes_a_dry_run_through()
    {
        var result = new MigrationResult(Applied: false, DestructivePlan(), WasDryRun: true);

        result.EnsureApplied().ShouldBeSameAs(result);
    }

    [Fact]
    public void EnsureApplied_passes_an_applied_plan_through()
    {
        var result = new MigrationResult(Applied: true, DestructivePlan(), WasDryRun: false);

        result.EnsureApplied().ShouldBeSameAs(result);
    }

    /// <summary>
    /// A migrator that reported an apply it did not perform. No in-repo <c>ISchemaMigrator</c> does this —
    /// both refuse only on destructive steps — but the member is public, so a third-party one can, and
    /// "un-applied for a reason nobody stated" must not be the one shape that starts a host serving nothing.
    /// </summary>
    [Fact]
    public void EnsureApplied_throws_on_an_unapplied_plan_that_is_not_even_destructive()
    {
        var plan = new MigrationPlan
        {
            Steps = [new MigrationStep(new SchemaChange { Kind = SchemaChangeKind.AddField, Entity = "vehicles", Field = "colour" }, IsDestructive: false, Reason: null)],
        };
        var result = new MigrationResult(Applied: false, plan, WasDryRun: false);

        var failure = Should.Throw<InvalidOperationException>(() => result.EnsureApplied());

        failure.Message.ShouldContain("ISchemaMigrator");
    }

    private static MigrationPlan DestructivePlan() =>
        new()
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
}
