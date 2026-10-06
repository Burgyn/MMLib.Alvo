using System.Net;
using System.Net.Http.Json;

namespace MMLib.Alvo.Admin.Tests.EndToEnd;

/// <summary>A built-in inserted into a condition in text mode refuses exactly the writes it names (spec §11.2).</summary>
/// <remarks>Its own world: it applies, and the reject it applies must not meet another class's writes.</remarks>
/// <param name="world">The shipped host and a browser.</param>
public sealed class BuiltInConditionScenarios(HostFunctionWorld world) : IClassFixture<HostFunctionWorld>
{
    [Fact(Timeout = AdminWorld.ScenarioTimeout)]
    public async Task A_built_in_inserted_into_a_condition_refuses_the_write_it_names()
    {
        await using var session = await world.SignInAsync(TestContext.Current.CancellationToken);
        await FunctionOfferScenarios.NewHookAsync(session, "owners", "beforeCreate", "reject");
        await FunctionOfferScenarios.TextModeAsync(session);

        var list = await FunctionOfferScenarios.OpenListAsync(session, "hook-condition");
        (await list.GetByTestId("fn-now").CountAsync()).ShouldBe(0, "now() is not a Condition function — the list has loaded, so this absence means it");
        await list.GetByTestId("fn-insert-endsWith").ClickAsync();
        await FunctionOfferScenarios.WaitForValueAsync(session, "hook-condition", "endsWith(text, suffix)");
        await session.WaitForFocusOnAsync("hook-condition");
        (await FunctionOfferScenarios.Selection(session)).ShouldBe("text");
        await FunctionOfferScenarios.AssertCheckKeepsFocusAsync(world, session, "hook-condition", "endsWith(text, suffix)", "text");
        await session.Page.Keyboard.TypeAsync("new.email");
        await FunctionOfferScenarios.WaitForValueAsync(session, "hook-condition", "endsWith(new.email, suffix)");

        const string condition = "endsWith(new.email, '@example.com')";
        await session.Page.FillAsync("input#hook-condition", condition);
        (await world.CheckedAsync(condition)).Findings.ShouldBeEmpty();
        await session.Page.FillAsync("#hook-reject", "Use the owner's real email address.");
        await HostFunctionScenarios.AddHookAsync(session);
        await HostFunctionScenarios.ApplyAsync(session, "Refuse placeholder owner emails");

        using var api = world.Api();
        using var refused = await api.PostAsJsonAsync("/api/owners", Owner("ana@example.com"), TestContext.Current.CancellationToken);
        refused.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await refused.Content.ReadAsStringAsync(TestContext.Current.CancellationToken)).ShouldContain("Use the owner's real email address.");
        await HostFunctionScenarios.CreateAsync(api, "owners", Owner("ana@kros.sk"));
        session.AssertConsoleClean();
    }

    private static object Owner(string email) => new { name = "Ana Horváthová", email };
}
