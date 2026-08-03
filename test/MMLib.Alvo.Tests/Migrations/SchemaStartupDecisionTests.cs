using MMLib.Alvo.Migrations;
using MMLib.Alvo.Migrations.Internal;
using MMLib.Alvo.Schema;

namespace MMLib.Alvo.Tests.Migrations;

public sealed class SchemaStartupDecisionTests
{
    private const string StartupApplyFix = "Alvo__Schema__Startup=Apply";

    private const string StartupVerifyFix = "Alvo__Schema__Startup=Verify";

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

    /// <summary>
    /// The one <c>Skip</c> state nothing has verified: Alvo has recorded no schema <em>and</em> the live schema
    /// does not match the descriptor — the shape of a migration job that never ran.
    /// </summary>
    /// <remarks>
    /// Serving here published <c>Ready</c> with no applied revision at all, so every replica answered 200 to a
    /// readiness probe while every request died at the SQL layer — the exact state readiness exists to prevent.
    /// The refusal has to name a way out, or an operator whose schema is genuinely somebody else's business is
    /// stuck.
    /// </remarks>
    [Fact]
    public void Skip_refuses_when_nothing_has_verified_the_schema_it_would_report_ready_over()
    {
        var decision = Decide(applied: null, NonEmptyPlan, AlvoSchemaStartupMode.Skip);

        decision.Outcome.ShouldBe(SchemaStartupOutcome.Refuse);

        var fix = decision.Fix.ShouldNotBeNull();
        fix.ShouldContain(StartupVerifyFix);
        fix.ShouldContain(StartupApplyFix);
        decision.Refusal.ShouldNotBeNull().ShouldContain("Skip");
    }

    /// <summary>
    /// The composition that must keep working: a database somebody else's migrations brought up, whose live
    /// schema already matches the descriptor. Nothing is recorded, and nothing needs to be — the empty plan
    /// <em>is</em> the verification.
    /// </summary>
    [Fact]
    public void Skip_serves_an_adopted_database_whose_live_schema_already_matches()
        => Decide(applied: null, EmptyPlan, AlvoSchemaStartupMode.Skip)
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

    /// <summary>
    /// A boot holding a descriptor the database has moved on from stands down — a distinct outcome from
    /// <c>Refuse</c>, because it does not stop the process.
    /// </summary>
    /// <remarks>
    /// <c>Refuse</c> throws and the host exits 78, which for an ordering problem is a crash loop an orchestrator
    /// retries forever over a condition no restart can fix. Standing down publishes <c>Failed</c> instead, so
    /// readiness answers 503 and the pod is drained. Asserting the outcome rather than "it did not apply" is
    /// what pins the exit path.
    /// </remarks>
    [Fact]
    public void An_out_of_order_boot_stands_down_instead_of_refusing()
    {
        var decision = Decide(
            AppliedAt(2), PlanAdding("orders", "discount"), AlvoSchemaStartupMode.Apply, outOfOrder: OlderPod);

        decision.Outcome.ShouldBe(SchemaStartupOutcome.StandDown);
        decision.Refusal.ShouldNotBeNull().ShouldContain("older descriptor");
        decision.Fix.ShouldNotBeNull().ShouldContain("Deploy the descriptor");
    }

    /// <summary>
    /// The ordering gate is decided <em>before</em> the destructive gate, so a rollback is diagnosed as "you are
    /// older" rather than as "destructive change refused".
    /// </summary>
    /// <remarks>
    /// This is the whole reason the gate sits ahead of a gate that would refuse the same boot anyway: both
    /// verdicts are true, and only one of them tells an operator that the artifact they deployed is behind the
    /// database. The mutation that proves the ordering — moving the destructive check back in front — turns this
    /// fact red and leaves every other fact in this class green.
    /// </remarks>
    [Fact]
    public void An_out_of_order_boot_is_diagnosed_as_older_rather_than_as_destructive()
    {
        var decision = Decide(
            AppliedAt(2), DestructivePlan, AlvoSchemaStartupMode.Apply, outOfOrder: OlderPod);

        decision.Outcome.ShouldBe(SchemaStartupOutcome.StandDown);

        var fix = decision.Fix.ShouldNotBeNull();
        fix.ShouldContain("Deploy the descriptor");
        fix.Contains(AllowDestructiveFix, StringComparison.Ordinal).ShouldBeFalse(
            "the destructive gate's own fix would send an operator to discard data to recover from being one "
            + "deploy behind");
    }

    /// <summary>
    /// A boot with nothing to apply serves, whatever the ordering says — the gate governs the apply, not the
    /// serve.
    /// </summary>
    /// <remarks>
    /// It is also what lets <c>AlvoBootService</c> skip the O(N) history read on the most common boot there is,
    /// so this fact is the policy half of that decision: an empty plan cannot be the boot that rewrites a newer
    /// schema with an older one.
    /// </remarks>
    [Fact]
    public void An_out_of_order_verdict_does_not_stand_down_a_boot_with_nothing_to_apply()
        => Decide(AppliedAt(2), EmptyPlan, AlvoSchemaStartupMode.Apply, outOfOrder: OlderPod)
            .Outcome.ShouldBe(SchemaStartupOutcome.Unchanged);

    /// <summary>
    /// <c>AllowDestructive</c> does <b>not</b> wave an out-of-order boot through, and that is a deliberate
    /// narrowing of what the flag used to buy.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Found in review, and it is a real behaviour change worth pinning rather than discovering.</b> Before
    /// the ordering gate, <c>AllowDestructive=true</c> was exactly how an operator forced a deliberate rollback
    /// through: the plan back drops a column, the flag allows the drop, the apply proceeds. It no longer does,
    /// because the two settings answer different questions — the flag says "I accept losing data", never "I
    /// accept serving an older descriptor than the database".
    /// </para>
    /// <para>
    /// Conflating them would make the ordering protection evaporate for anyone who set an unrelated flag in a
    /// staging environment, and the oscillation the gate exists to stop discards no data at all, so the flag
    /// would be no evidence of intent about it. The way to force an older artifact through is to make it a new
    /// one — bump its <c>revision</c>, which changes its canonical content — and the refusal text says so,
    /// including that the destructive gate is still waiting behind it. Recorded as deviation 74.
    /// </para>
    /// </remarks>
    [Fact]
    public void AllowDestructive_does_not_wave_an_out_of_order_boot_through()
        => Decide(
                AppliedAt(2),
                DestructivePlan,
                AlvoSchemaStartupMode.Apply,
                allowDestructive: true,
                outOfOrder: OlderPod)
            .Outcome.ShouldBe(SchemaStartupOutcome.StandDown);

    /// <summary>
    /// <c>Skip</c> ignores the ordering exactly as it ignores every other drift: it never applies, so it cannot
    /// be the replica that rewrites the schema.
    /// </summary>
    [Fact]
    public void Skip_ignores_the_ordering_because_it_applies_nothing()
        => Decide(AppliedAt(2), DestructivePlan, AlvoSchemaStartupMode.Skip, outOfOrder: OlderPod)
            .Outcome.ShouldBe(SchemaStartupOutcome.Unchanged);

    /// <summary>
    /// <c>Verify</c> stands an older pod down too, rather than refusing it with the drift message — the mode
    /// decides what may be <em>applied</em>, and this boot is not asking to apply anything it is entitled to.
    /// </summary>
    [Fact]
    public void An_out_of_order_boot_under_Verify_stands_down_rather_than_reporting_ordinary_drift()
        => Decide(AppliedAt(2), PlanAdding("orders", "discount"), AlvoSchemaStartupMode.Verify, outOfOrder: OlderPod)
            .Outcome.ShouldBe(SchemaStartupOutcome.StandDown);

    private static OutOfOrderBoot OlderPod => new(
        "Alvo cannot start: this process's descriptor was already applied to this database as revision 1, and "
        + "the database has since moved on to revision 2. This process is running an older descriptor than the "
        + "database, so it must not apply its schema over a newer one.",
        ["  Deploy the descriptor this database is on (revision 2)."]);

    private static SchemaStartupDecision Decide(
        AppliedSchema? applied,
        MigrationPlan plan,
        AlvoSchemaStartupMode mode,
        bool allowDestructive = false,
        OutOfOrderBoot? outOfOrder = null)
        => SchemaStartupPolicy.Decide(
            applied,
            plan,
            new AlvoSchemaOptions { Startup = mode, AllowDestructive = allowDestructive },
            outOfOrder);

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
