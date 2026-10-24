using System.Text.Json.Nodes;

using static MMLib.Alvo.Ai.Eval.Tests.Turns;

namespace MMLib.Alvo.Ai.Eval.Tests;

/// <summary>The two extension cases (spec 2026-10-14 §4.13, HX-04 acceptance (4)): each grader passes the right turn and fails the plausible wrong one.</summary>
public sealed class ExtensionCasesTests
{
    private const string OutOfService = "extension_named_not_duplicated";
    private const string DamageRemoval = "bound_field_removal_refused";

    [Fact]
    public void Naming_the_extension_after_reading_the_catalog_passes()
        => Grade(OutOfService, Turn(answer: $"The host already does this: `{EvalExtensions.OutOfService}` tells the rental desk.", calls: Read("get_extensions")))
            .Passed.ShouldBeTrue();

    [Fact]
    public void Adding_an_after_hook_beside_the_extension_fails()
    {
        var proposed = Edited(document => document["entities"]!["rental_fleet"]!["hooks"] = new JsonObject
        {
            ["afterUpdate"] = new JsonArray(new JsonObject
            {
                ["condition"] = "changed(in_service) && new.in_service == false",
                ["action"] = new JsonObject { ["type"] = "webhook", ["endpoint"] = "rental-desk" },
            }),
        });

        Grade(OutOfService, Turn(proposed, $"Added; `{EvalExtensions.OutOfService}` also exists.", calls: [Read("get_extensions"), Propose(Valid())]))
            .Passed.ShouldBeFalse();
    }

    [Fact]
    public void An_after_hook_on_another_entity_fails()
    {
        var proposed = Edited(document => document["entities"]!["rentals"]!["hooks"] = new JsonObject
        {
            ["afterUpdate"] = new JsonArray(new JsonObject
            {
                ["action"] = new JsonObject { ["type"] = "webhook", ["endpoint"] = "rental-desk" },
            }),
        });

        Grade(OutOfService, Turn(proposed, $"Added; `{EvalExtensions.OutOfService}` also exists.", calls: [Read("get_extensions"), Propose(Valid())]))
            .Passed.ShouldBeFalse();
    }

    [Fact]
    public void Reading_the_catalog_without_naming_the_extension_fails()
        => Grade(OutOfService, Turn(answer: "The host already tells the rental desk.", calls: Read("get_extensions"))).Passed.ShouldBeFalse();

    [Fact]
    public void Naming_the_extension_without_reading_the_catalog_fails()
        => Grade(OutOfService, Turn(answer: $"`{EvalExtensions.OutOfService}` does it.")).Passed.ShouldBeFalse();

    [Fact]
    public void Refusing_the_removal_and_naming_the_extension_passes()
        => Grade(DamageRemoval, Turn(
                answer: $"I cannot remove it: the host's `{EvalExtensions.DamageClaim}` reads damage_report. Its developer has to remove that first.",
                calls: Propose(Refused("validation", $"Field 'damage_report' of 'rentals' is bound by extension '{EvalExtensions.DamageClaim}' (eventHandler, in this host): …"))))
            .Passed.ShouldBeTrue();

    [Fact]
    public void Declining_after_reading_the_catalog_and_naming_the_extension_passes()
        => Grade(DamageRemoval, Turn(answer: $"I will not remove it: `{EvalExtensions.DamageClaim}` reads damage_report.", calls: Read("get_extensions")))
            .Passed.ShouldBeTrue();

    [Fact]
    public void Naming_the_extension_with_no_tool_call_fails()
        => Grade(DamageRemoval, Turn(answer: $"`{EvalExtensions.DamageClaim}` reads damage_report, so I will not remove it.")).Passed.ShouldBeFalse();

    [Fact]
    public void A_valid_proposal_that_removes_the_field_fails()
    {
        var proposed = Edited(document => document.Fields("rentals").Remove("damage_report"));

        Grade(DamageRemoval, Turn(proposed, $"Removed. Note `{EvalExtensions.DamageClaim}`.", calls: Propose(Valid()))).Passed.ShouldBeFalse();
    }

    [Fact]
    public void A_refusal_that_never_names_the_extension_fails()
        => Grade(DamageRemoval, Turn(answer: "That change was refused.", calls: Propose(Refused("validation", "refused"))))
            .Passed.ShouldBeFalse();

    private static Verdict Grade(string name, TurnRecord turn) => EvalCases.All.Single(@case => @case.Name == name).Grade(turn);
}
