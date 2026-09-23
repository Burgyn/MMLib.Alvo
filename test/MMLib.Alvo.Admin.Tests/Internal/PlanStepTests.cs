using MMLib.Alvo.Admin.Internal;

namespace MMLib.Alvo.Admin.Tests.Internal;

/// <summary>
/// A planner line said as a sentence, without losing what the planner said.
/// </summary>
/// <remarks>
/// The lines are spelled the way <c>DestructiveChangeGuard.DescribeStep</c> writes them — including the
/// two-space marker — because a parser tested against a tidier grammar than the planner's is tested
/// against nothing.
/// </remarks>
public class PlanStepTests
{
    [Theory]
    [InlineData("CreateEntity technicians", "Create table", "technicians")]
    [InlineData("AddField technicians.skill_level", "Add column", "technicians.skill_level")]
    [InlineData("RenameField customers.contact_email", "Rename a column to", "customers.contact_email")]
    [InlineData("RenameEntity service_areas", "Rename a table to", "service_areas")]
    [InlineData("AddIndex work_orders", "Add an index on", "work_orders")]
    [InlineData("DropIndex work_orders", "Drop an index on", "work_orders")]
    [InlineData("AlterField work_orders.region_id", "Change column", "work_orders.region_id")]
    public void A_safe_step_is_a_verb_and_its_target(string line, string verb, string target)
    {
        var step = PlanStep.Parse(line);

        step.ShouldBe(new PlanStep(verb, target, Consequence: null, Destructive: false));
    }

    [Fact]
    public void A_dropped_column_says_it_destroys_its_data_and_loses_the_marker()
    {
        var step = PlanStep.Parse("DropField customers.notes: Drops the column and all its data.  <- destructive");

        step.ShouldBe(new PlanStep("Drop column", "customers.notes", "destroys its data", Destructive: true));
    }

    [Fact]
    public void A_dropped_table_says_it_destroys_its_rows()
    {
        var step = PlanStep.Parse("DropEntity invoices: Drops the table and all its rows.  <- destructive");

        step.ShouldBe(new PlanStep("Drop table", "invoices", "destroys its rows", Destructive: true));
    }

    [Fact]
    public void A_narrowing_keeps_the_planners_reason_as_the_rest_of_the_sentence()
    {
        var step = PlanStep.Parse(
            "AlterField customers.email: Max length shrinks from 200 to 120; longer values would be truncated.  <- destructive");

        step.Verb.ShouldBe("Change column");
        step.Consequence.ShouldBe("max length shrinks from 200 to 120; longer values would be truncated");
        step.Destructive.ShouldBeTrue();
    }

    [Fact]
    public void A_table_level_change_is_said_of_the_table()
        => PlanStep.Parse("AlterField work_orders").Verb.ShouldBe("Change table");

    [Fact]
    public void A_step_this_build_does_not_know_is_shown_as_it_arrived()
    {
        var step = PlanStep.Parse("ReticulateSplines work_orders: Something new.  <- destructive");

        step.ShouldBe(new PlanStep(
            "ReticulateSplines work_orders: Something new.", string.Empty, Consequence: null, Destructive: true));
    }

    [Theory]
    [InlineData("AddField technicians.skill_level", "Add skill_level to technicians")]
    [InlineData("CreateEntity suppliers", "Add suppliers")]
    [InlineData("DropField customers.notes: Drops the column and all its data.  <- destructive", "Remove notes from customers")]
    public void A_one_step_plan_suggests_a_reason(string line, string reason)
        => PlanStep.SuggestReason([line]).ShouldBe(reason);

    [Fact]
    public void A_plan_of_several_steps_suggests_nothing()
        => PlanStep.SuggestReason(["CreateEntity suppliers", "AddIndex suppliers"]).ShouldBeNull();

    [Fact]
    public void A_step_with_no_reason_to_offer_suggests_nothing()
        => PlanStep.SuggestReason(["AddIndex suppliers"]).ShouldBeNull();
}
