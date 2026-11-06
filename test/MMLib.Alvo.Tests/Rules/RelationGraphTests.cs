using MMLib.Alvo.Descriptor;
using MMLib.Alvo.Descriptor.Internal;

using System.Text.Json.Nodes;

using static MMLib.Alvo.Tests.Rules.RelationDescriptors;

namespace MMLib.Alvo.Tests.Rules;

/// <summary>
/// A cycle among list rules has no caller whose rows can be decided, and a chain deeper than three relations is
/// refused at apply, at the rule that starts it; a relation into an entity with no list rule is only a warning.
/// </summary>
/// <remarks>Driven through the real <see cref="DescriptorValidator"/>, the way an apply reaches the graph.</remarks>
public sealed class RelationGraphTests
{
    private const string CycleFix = "Move the condition into one of the two rules, or replace one relation with a rollup or a stored field.";

    private static readonly DescriptorValidator _validator = new();

    [Fact]
    public void A_list_rule_cycle_between_two_entities_is_refused_at_apply()
    {
        var result = _validator.Validate(Project(
            ("projects", Entity([Field("owner_id", "uuid")], ("list", "tasks.exists(t, t.assignee == @user.id)"))),
            ("tasks", Entity([Ref("project_id", "projects"), Field("assignee", "uuid")], ("list", "project_id.owner_id == @user.id")))));

        var error = Errors(result).ShouldHaveSingleItem();
        error.Path.ShouldBe("/entities/projects/rules/list");
        error.Message.ShouldBe(
            "Rule cycle: the list rules of 'projects' → 'tasks' → 'projects' read each other, so no caller's rows can be decided.");
        error.FixSuggestion.ShouldBe(CycleFix);
    }

    [Fact]
    public void A_list_rule_reading_its_own_entity_is_refused()
    {
        var result = _validator.Validate(Project(Employees(("list", "manager_id.region == 'eu'"))));

        var error = Errors(result).ShouldHaveSingleItem();
        error.Path.ShouldBe("/entities/employees/rules/list");
        error.Message.ShouldBe(
            "Rule cycle: the list rules of 'employees' → 'employees' read each other, so no caller's rows can be decided.");
    }

    [Fact]
    public void A_get_rule_reading_its_own_entity_is_not_a_cycle()
    {
        var result = _validator.Validate(Project(Employees(("list", "true"), ("get", "manager_id.region == 'eu'"))));

        Errors(result).ShouldBeEmpty();
    }

    [Fact]
    public void A_three_deep_chain_applies()
    {
        var result = _validator.Validate(Project(Chain("a", "b", "c", "d")));

        Errors(result).ShouldBeEmpty();
    }

    [Fact]
    public void A_four_deep_chain_is_refused_at_the_list_rule_that_starts_it()
    {
        var result = _validator.Validate(Project(Chain("a", "b", "c", "d", "e")));

        var error = Errors(result).ShouldHaveSingleItem();
        error.Path.ShouldBe("/entities/a/rules/list");
        error.Message.ShouldBe("'a.rules.list' reads relations 4 levels deep; at most 3 are allowed.");
        error.FixSuggestion.ShouldBe("Read a stored field or a rollup on a nearer entity.");
    }

    [Fact]
    public void A_too_deep_chain_is_refused_at_every_list_rule_it_starts_wherever_the_walk_meets_it_first()
    {
        var result = _validator.Validate(Project([.. Chain("b", "c", "d", "e", "f"), Linked("a", "b", "list")]));

        Errors(result).Select(error => error.Path).Order(StringComparer.Ordinal)
            .ShouldBe(["/entities/a/rules/list", "/entities/b/rules/list"]);
    }

    [Fact]
    public void A_get_rule_whose_target_list_is_three_deep_is_refused_at_its_own_path()
    {
        var result = _validator.Validate(Project([.. Chain("a", "b", "c", "d"), Linked("x", "a", "get")]));

        var error = Errors(result).ShouldHaveSingleItem();
        error.Path.ShouldBe("/entities/x/rules/get");
        error.Message.ShouldBe("'x.rules.get' reads relations 4 levels deep; at most 3 are allowed.");
    }

    [Fact]
    public void A_relation_to_an_entity_with_no_list_rule_is_a_warning()
    {
        var result = _validator.Validate(Project(
            ("customers", Entity([Field("region", "string")])),
            ("orders", Entity([Ref("customer_id", "customers")], ("list", "customer_id.region == 'eu'")))));

        result.IsValid.ShouldBeTrue();
        var warning = result.Errors.ShouldHaveSingleItem();
        warning.Severity.ShouldBe(DescriptorValidationSeverity.Warning);
        warning.Path.ShouldBe("/entities/orders/rules/list");
        warning.Message.ShouldBe("'customers' has no list rule, so this relation never matches any row.");
        warning.FixSuggestion.ShouldBe("Give 'customers' a list rule.");
    }

    [Fact]
    public void A_three_entity_cycle_is_reported_once()
    {
        var result = _validator.Validate(Project(Linked("a", "b", "list"), Linked("b", "c", "list"), Linked("c", "a", "list")));

        Errors(result).ShouldHaveSingleItem().Message.ShouldBe(
            "Rule cycle: the list rules of 'a' → 'b' → 'c' → 'a' read each other, so no caller's rows can be decided.");
    }

    [Fact]
    public void A_long_cycle_draws_no_depth_error_beside_its_cycle_error()
    {
        var result = _validator.Validate(Project(
            Linked("a", "b", "list"), Linked("b", "c", "list"), Linked("c", "d", "list"), Linked("d", "a", "list"),
            Linked("x", "a", "list"), Linked("y", "a", "get")));

        Errors(result).ShouldHaveSingleItem().Message.ShouldStartWith("Rule cycle: ");
    }

    [Fact]
    public void A_create_rule_whose_only_slot_is_its_check_is_refused_when_too_deep()
    {
        var result = _validator.Validate(Project([.. Chain("a", "b", "c", "d"), Linked("x", "a", "create")]));

        var error = Errors(result).ShouldHaveSingleItem();
        error.Path.ShouldBe("/entities/x/rules/create");
        error.Message.ShouldBe("'x.rules.create' reads relations 4 levels deep; at most 3 are allowed.");
    }

    private static (string, JsonObject) Employees(params (string Operation, string Source)[] rules) =>
        ("employees", Entity([Ref("manager_id", "employees"), Field("region", "string")], rules));

    /// <summary>Each entity's list rule reads the next one; the last lists every row.</summary>
    private static (string, JsonObject)[] Chain(params string[] names) =>
        [.. names.Select((name, index) => index + 1 < names.Length ? Linked(name, names[index + 1], "list") : Leaf(name))];

    private static (string, JsonObject) Linked(string name, string next, string operation) =>
        (name, Entity([Ref("next_id", next), Field("flag", "boolean")], ("list", "true"), (operation, "next_id.flag == true")));

    private static (string, JsonObject) Leaf(string name) => (name, Entity([Field("flag", "boolean")], ("list", "true")));

    private static List<DescriptorValidationError> Errors(DescriptorValidationResult result) =>
        [.. result.Errors.Where(error => error.Severity == DescriptorValidationSeverity.Error)];
}
