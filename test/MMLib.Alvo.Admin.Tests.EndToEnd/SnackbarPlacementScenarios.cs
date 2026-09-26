using Microsoft.Playwright;

namespace MMLib.Alvo.Admin.Tests.EndToEnd;

/// <summary>A snackbar is bottom-left, never over an open editor's actions (spec §3.3).</summary>
/// <remarks>
/// Found in a screenshot review: "Created …" was anchored bottom-right, where the right-hand sheet keeps Disable,
/// Cancel and Save, and the operator who opened the person they had just created read their editor's actions
/// through it.
/// </remarks>
/// <param name="world">The running host and browser.</param>
public sealed class SnackbarPlacementScenarios(AdminWorld world) : IClassFixture<AdminWorld>
{
    [Fact(Timeout = AdminWorld.ScenarioTimeout)]
    public async Task A_snackbar_never_covers_the_actions_of_the_editor_opened_under_it()
    {
        const string email = "placement@example.com";
        await using var session = await world.SignInAsync(TestContext.Current.CancellationToken);
        await session.GoAsync("/access");
        await session.Page.GetByTestId("person-new").ClickAsync();
        await session.Page.GetByLabel("Email", new() { Exact = true }).FillAsync(email);
        await session.Page.Keyboard.PressAsync("Enter");
        await session.SnackbarAsync($"Created {email}");

        await session.Button($"Change {email}", exact: true).ClickAsync();
        var editor = session.Dialog("person-editor");
        await editor.GetByRole(AriaRole.Button, new() { Name = "Cancel" }).WaitForAsync();
        await session.Page.WaitForFunctionAsync("() => document.getAnimations().every(a => a.playState !== 'running')");

        var snackbar = await BoxAsync(session.Snackbars.First);
        foreach (var button in await editor.GetByRole(AriaRole.Button).AllAsync())
        {
            var box = await button.BoundingBoxAsync();
            if (box is not null)
            {
                Intersects(snackbar, box).ShouldBeFalse($"the snackbar covers '{(await button.InnerTextAsync()).Trim()}'");
            }
        }

        (snackbar.X + snackbar.Width).ShouldBeLessThan(700, "bottom-left, not bottom-right");
    }

    private static async Task<LocatorBoundingBoxResult> BoxAsync(ILocator element)
        => await element.BoundingBoxAsync() ?? throw new InvalidOperationException("the element is not laid out");

    private static bool Intersects(LocatorBoundingBoxResult a, LocatorBoundingBoxResult b)
        => a.X < b.X + b.Width && b.X < a.X + a.Width && a.Y < b.Y + b.Height && b.Y < a.Y + a.Height;
}
