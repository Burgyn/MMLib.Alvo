namespace MMLib.Alvo.Admin.Tests.EndToEnd;

/// <summary>
/// The bike-workshop README's "Hook functions" section quotes the expressions <see cref="DemoFunctionScenarios"/> pins
/// against the descriptor, the values that scenario reads back, and the dashboard's own words — read from the README
/// itself, so a reworded hook, screen or README fails here rather than drifting apart. It needs no host and no browser,
/// so it takes no world.
/// </summary>
public sealed class BikeWorkshopReadmeTests
{
    private const string SectionMarker = "## Hook functions";
    private const string NextSection = "\n## ";

    [Fact]
    public async Task The_readme_quotes_the_demo_hooks_and_the_dashboard_words_the_scenarios_assert()
    {
        var path = Path.Combine(RepositoryRoot.Find(), "examples", "bike-workshop", "README.md");
        var readme = (await File.ReadAllTextAsync(path, TestContext.Current.CancellationToken)).ReplaceLineEndings("\n");

        var start = readme.IndexOf(SectionMarker, StringComparison.Ordinal);
        start.ShouldBeGreaterThanOrEqualTo(0, $"{path} has no section {SectionMarker}");
        var section = readme[(start + SectionMarker.Length)..];
        var end = section.IndexOf(NextSection, StringComparison.Ordinal);
        end.ShouldBeGreaterThan(0, $"the {SectionMarker} section in {path} has no section after it");
        section = section[..end];

        foreach (var expression in new[]
        {
            DemoFunctionScenarios.FrameNumber, DemoFunctionScenarios.RackTag, DemoFunctionScenarios.WeekRate,
            DemoFunctionScenarios.WorkshopAddress, DemoFunctionScenarios.AWeekOrLonger,
        })
        {
            section.ShouldContain($"`{expression}`");
        }

        foreach (var bold in new[]
        {
            HookEditInPlaceScenarios.OnWriteTab, HostFunctionScenarios.Disclosure, HostFunctionScenarios.BuiltInBadge,
            HostFunctionScenarios.InsertLabel, HostFunctionScenarios.HostBadge,
        })
        {
            section.ShouldContain($"**{bold}**");
        }

        section.ShouldContain($"*days {DemoFunctionScenarios.AtLeast} 7*");
        section.ShouldContain($"*email {DemoFunctionScenarios.EndsWith} @velo-dielna.example*");
        section.ShouldContain("stored at 39.38, a price of 275.66", Case.Sensitive, "the week rate the scenario reads back");
    }
}
