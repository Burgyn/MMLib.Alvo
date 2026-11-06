using Microsoft.Playwright;

namespace MMLib.Alvo.Admin.Tests.EndToEnd;

/// <summary>
/// A rule that reads a related row is checked live like any other, offered under the box, and the simulator names the
/// related entity's own list rule it reads through.
/// </summary>
/// <remarks>
/// The bike-workshop world, because it is not tenant-scoped: the operator holds no tenant, and on a scoped entity the
/// simulator would answer the tenant guard's 403 before any rule — or relation — is read. Its own world: the first
/// scenario applies a rule on <c>bikes</c>.
/// </remarks>
/// <param name="world">The running host and browser.</param>
public sealed class RelationRuleScenarios(BikeWorkshopWorld world) : IClassFixture<BikeWorkshopWorld>
{
    private const string Relation = "customer_id.city == 'Bratislava'";

    [Fact(Timeout = AdminWorld.ScenarioTimeout)]
    public async Task A_relation_rule_is_checked_live_and_the_simulator_names_the_related_rule()
    {
        await using var session = await world.SignInAsync(TestContext.Current.CancellationToken);
        await session.GoAsync("/schema/bikes");
        await session.OpenTabAsync("Rules");
        (await session.Page.GetByTestId("relation-hints-list").InnerTextAsync()).ShouldContain("customer_id.<field>");

        await session.Page.FillAsync("#rule-list", "'amdin' in @user.roles");
        var finding = session.Page.GetByTestId("check-rule-list");
        await finding.WaitForAsync(new() { Timeout = 3_000 });
        await session.Page.FillAsync("#rule-list", Relation);
        await finding.WaitForAsync(new() { State = WaitForSelectorState.Detached, Timeout = 3_000 });

        await session.Page.Locator("#rule-list").FocusAsync();
        await session.Page.Keyboard.PressAsync("Control+Enter");
        await session.SnackbarAsync("Rule saved to the working copy");
        await session.PreviewPendingAsync();
        await session.Page.FillAsync("#apply-reason", "Read through the customer");
        await session.Button("Apply these changes").ClickAsync();
        await session.Content.GetByText("Applied as revision").First.WaitForAsync();

        await session.GoAsync("/rules/bikes");
        var relation = session.Page.GetByTestId("verdict-relation-customer_id");
        await relation.WaitForAsync();
        var text = await relation.InnerTextAsync();
        text.ShouldContain("Reads through customer_id → customers");
        text.ShouldContain("'reception' in @user.roles", Case.Sensitive, "the related entity's own list rule is named");
        session.AssertConsoleClean();
    }

    [Fact(Timeout = AdminWorld.ScenarioTimeout)]
    public async Task A_negated_relation_is_refused_live_with_its_fix()
    {
        await using var session = await world.SignInAsync(TestContext.Current.CancellationToken);
        await session.GoAsync("/schema/bikes");
        await session.OpenTabAsync("Rules");

        await session.Page.FillAsync("#rule-get", $"!({Relation})");

        var finding = session.Page.GetByTestId("check-rule-get").First;
        await finding.WaitForAsync(new() { Timeout = 3_000 });
        var text = await finding.InnerTextAsync();
        text.ShouldContain("A relation is negated here");
        text.ShouldContain("Say what must hold instead");
    }
}
