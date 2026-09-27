using Microsoft.Extensions.DependencyInjection;
using Microsoft.Playwright;

namespace MMLib.Alvo.Admin.Tests.EndToEnd;

/// <summary>
/// People past the first 50 are reachable, by page and by search (docs/todo-admin.md §8d item 23).
/// </summary>
/// <remarks>
/// Fifty-one people are seeded through the unguarded administration — the test standing in for a directory that
/// grew, as <c>RevokedSessionScenarios</c> stands in for a second administrator. The list is ordered by address, so
/// the bootstrap administrator and <c>person-00</c>…<c>person-48</c> fill the first page. Its own world.
/// </remarks>
/// <param name="world">The running host and browser.</param>
public sealed class PeoplePagingScenarios(AdminWorld world) : IClassFixture<AdminWorld>
{
    [Fact(Timeout = AdminWorld.ScenarioTimeout)]
    public async Task The_next_page_and_a_search_reach_everybody()
    {
        await SeedAsync(count: 51);
        await using var session = await world.SignInAsync(TestContext.Current.CancellationToken);
        await session.GoAsync("/access");

        (await session.Content.InnerTextAsync()).ShouldNotContain("person-50@alvo.test");
        await session.Page.GetByTestId("people-next").ClickAsync();
        await session.Page.GetByTestId("people-previous").WaitForAsync();
        (await session.Content.InnerTextAsync()).ShouldContain("person-50@alvo.test");

        var search = session.Page.GetByRole(AriaRole.Searchbox, new() { Name = "Find a person by address" });
        await search.FillAsync("person-07");
        await session.Page.GetByTestId("people-previous").WaitForAsync(new() { State = WaitForSelectorState.Detached });
        await session.Content.GetByText("person-08@alvo.test").WaitForAsync(new() { State = WaitForSelectorState.Detached });

        var found = await session.Content.InnerTextAsync();
        found.ShouldContain("person-07@alvo.test");
        found.ShouldNotContain("person-08@alvo.test");

        session.AssertConsoleClean();
    }

    /// <summary>
    /// A person created while the list shows a search they do not match, or a page they do not sort onto, is still
    /// drawn and revealed where the operator is looking (spec §3.5): the list narrows to their address.
    /// </summary>
    [Fact(Timeout = AdminWorld.ScenarioTimeout)]
    public async Task A_person_created_off_the_page_on_screen_is_still_shown()
    {
        await using var session = await world.SignInAsync(TestContext.Current.CancellationToken);
        await session.GoAsync("/access");
        var search = session.Page.GetByRole(AriaRole.Searchbox, new() { Name = "Find a person by address" });
        await search.FillAsync("nobody-matches");
        await session.Content.GetByText("Nobody’s address contains “nobody-matches”").WaitForAsync();

        await session.CreatePersonAsync("zz-arrived@alvo.test");

        (await search.InputValueAsync()).ShouldBe("zz-arrived@alvo.test");
        (await session.Content.InnerTextAsync()).ShouldNotContain("nobody-matches");
        session.AssertConsoleClean();
    }

    private async Task SeedAsync(int count)
    {
        using var scope = world.Services.CreateScope();
        var people = scope.ServiceProvider.GetRequiredKeyedService<IAlvoUserAdministration>(AlvoUserAdministration.UnguardedKey);
        for (var i = 0; i < count; i++)
        {
            await people.CreateAsync(new AlvoUserCreation(string.Create(System.Globalization.CultureInfo.InvariantCulture, $"person-{i:D2}@alvo.test"), []));
        }
    }
}
