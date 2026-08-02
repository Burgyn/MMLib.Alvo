using MMLib.Alvo.Migrations;
using MMLib.Alvo.Migrations.Internal;
using MMLib.Alvo.Schema;

namespace MMLib.Alvo.Tests.Migrations;

public sealed class SchemaStartupDecisionTests
{
    private const string StartupApplyFix = "Alvo__Schema__Startup=Apply";

    private const string AllowDestructiveFix = "Alvo__Schema__AllowDestructive=true";

    [Theory]
    [InlineData(AlvoSchemaStartupMode.Verify)]
    [InlineData(AlvoSchemaStartupMode.Apply)]
    public void An_empty_database_initializes_in_every_mode_but_Skip(AlvoSchemaStartupMode mode)
    {
        var decision = Decide(applied: null, NonEmptyPlan, mode);

        decision.Outcome.ShouldBe(SchemaStartupOutcome.Initialize);
        decision.Refusal.ShouldBeNull();
    }

    [Fact]
    public void Skip_never_touches_the_database_even_when_uninitialized()
        => Decide(applied: null, NonEmptyPlan, AlvoSchemaStartupMode.Skip)
            .Outcome.ShouldBe(SchemaStartupOutcome.Unchanged);

    [Fact]
    public void Skip_refuses_nothing_even_when_the_plan_would_discard_data()
    {
        var decision = Decide(AppliedAt(1), DestructivePlan, AlvoSchemaStartupMode.Skip);

        decision.Outcome.ShouldBe(SchemaStartupOutcome.Unchanged);
        decision.Refusal.ShouldBeNull();
    }

    [Theory]
    [InlineData(AlvoSchemaStartupMode.Verify)]
    [InlineData(AlvoSchemaStartupMode.Apply)]
    [InlineData(AlvoSchemaStartupMode.Skip)]
    public void An_unchanged_descriptor_serves_in_every_mode(AlvoSchemaStartupMode mode)
        => Decide(AppliedAt(1), EmptyPlan, mode).Outcome.ShouldBe(SchemaStartupOutcome.Unchanged);

    [Fact]
    public void Drift_under_Verify_refuses_and_the_refusal_names_the_steps()
    {
        var decision = Decide(AppliedAt(1), PlanAdding("orders", "discount"), AlvoSchemaStartupMode.Verify);

        decision.Outcome.ShouldBe(SchemaStartupOutcome.Refuse);

        var refusal = decision.Refusal.ShouldNotBeNull();
        refusal.ShouldContain("orders");
        refusal.ShouldContain("discount");
        refusal.ShouldContain(StartupApplyFix);
    }

    [Fact]
    public void A_drift_refusal_names_the_revision_it_compared_against()
        => Decide(AppliedAt(7), PlanAdding("orders", "discount"), AlvoSchemaStartupMode.Verify)
            .Refusal.ShouldNotBeNull().ShouldContain("7");

    [Fact]
    public void Drift_under_Apply_applies()
        => Decide(AppliedAt(1), PlanAdding("orders", "discount"), AlvoSchemaStartupMode.Apply)
            .Outcome.ShouldBe(SchemaStartupOutcome.Apply);

    [Fact]
    public void A_destructive_plan_is_refused_under_Apply_unless_AllowDestructive()
    {
        Decide(AppliedAt(1), DestructivePlan, AlvoSchemaStartupMode.Apply)
            .Outcome.ShouldBe(SchemaStartupOutcome.Refuse);

        Decide(AppliedAt(1), DestructivePlan, AlvoSchemaStartupMode.Apply, allowDestructive: true)
            .Outcome.ShouldBe(SchemaStartupOutcome.Apply);
    }

    [Fact]
    public void A_destructive_refusal_marks_which_step_is_destructive()
    {
        var refusal = Decide(AppliedAt(1), DestructivePlan, AlvoSchemaStartupMode.Apply)
            .Refusal.ShouldNotBeNull();

        refusal.ShouldContain("destructive");
        refusal.ShouldContain("orders.legacy_ref");
        refusal.ShouldContain(AllowDestructiveFix);
    }

    [Fact]
    public void An_absent_snapshot_does_not_mean_an_empty_database_so_a_destructive_initialization_is_refused()
    {
        Decide(applied: null, DestructivePlan, AlvoSchemaStartupMode.Apply)
            .Outcome.ShouldBe(SchemaStartupOutcome.Refuse);

        Decide(applied: null, DestructivePlan, AlvoSchemaStartupMode.Apply, allowDestructive: true)
            .Outcome.ShouldBe(SchemaStartupOutcome.Initialize);
    }

    [Fact]
    public void A_decision_carries_the_plan_it_judged_so_the_caller_applies_exactly_what_was_decided()
    {
        var plan = PlanAdding("orders", "discount");

        Decide(AppliedAt(1), plan, AlvoSchemaStartupMode.Apply).Plan.ShouldBeSameAs(plan);
    }

    private static SchemaStartupDecision Decide(
        AppliedSchema? applied,
        MigrationPlan plan,
        AlvoSchemaStartupMode mode,
        bool allowDestructive = false)
        => SchemaStartupPolicy.Decide(
            applied,
            plan,
            new AlvoSchemaOptions { Startup = mode, AllowDestructive = allowDestructive });

    private static AppliedSchema AppliedAt(int revision)
        => new(new SchemaModel([]), "{}", revision, DateTimeOffset.UtcNow);

    private static MigrationPlan EmptyPlan => new() { Steps = [] };

    private static MigrationPlan NonEmptyPlan => PlanAdding("orders", "discount");

    private static MigrationPlan PlanAdding(string entity, string field) => new()
    {
        Steps =
        [
            new MigrationStep(
                new SchemaChange { Kind = SchemaChangeKind.AddField, Entity = entity, Field = field },
                IsDestructive: false,
                Reason: null),
        ],
    };

    private static MigrationPlan DestructivePlan => new()
    {
        Steps =
        [
            new MigrationStep(
                new SchemaChange
                {
                    Kind = SchemaChangeKind.DropField,
                    Entity = "orders",
                    Field = "legacy_ref",
                    IsDestructive = true,
                },
                IsDestructive: true,
                Reason: "drops field 'orders.legacy_ref' and its data"),
        ],
    };
}
