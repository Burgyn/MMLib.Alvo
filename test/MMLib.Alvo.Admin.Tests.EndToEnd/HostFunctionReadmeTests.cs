namespace MMLib.Alvo.Admin.Tests.EndToEnd;

/// <summary>
/// The embedded-host sample's README ("In the dashboard") quotes the strings <see cref="HostFunctionScenarios"/> asserts on
/// the screen — read from the README itself, so a reworded screen or a reworded README fails here rather than drifting apart
/// (ruling S). It needs no host and no browser, so it takes no world.
/// </summary>
public sealed class HostFunctionReadmeTests
{
    private const string ParagraphMarker = "**In the dashboard.**";
    private const string ParagraphEnd = "\n\n";

    [Fact]
    public async Task The_readme_quotes_the_strings_the_host_function_scenario_asserts()
    {
        var path = Path.Combine(RepositoryRoot.Find(), "samples", "MMLib.Alvo.Samples.EmbeddedHost", "README.md");
        var readme = (await File.ReadAllTextAsync(path, TestContext.Current.CancellationToken)).ReplaceLineEndings("\n");

        var start = readme.IndexOf(ParagraphMarker, StringComparison.Ordinal);
        start.ShouldBeGreaterThanOrEqualTo(0, $"{path} has no paragraph that starts with {ParagraphMarker}");
        var paragraph = readme[start..];
        var end = paragraph.IndexOf(ParagraphEnd, StringComparison.Ordinal);
        end.ShouldBeGreaterThan(0, $"the {ParagraphMarker} paragraph in {path} has no blank line after it");
        paragraph = paragraph[..end];

        foreach (var bold in new[]
        {
            HostFunctionScenarios.Disclosure, HostFunctionScenarios.HostBadge,
            HostFunctionScenarios.BuiltInBadge, HostFunctionScenarios.InsertLabel,
        })
        {
            paragraph.ShouldContain($"**{bold}**");
        }

        paragraph.ShouldContain($"`{HostFunctionScenarios.Signature}`");
        paragraph.ShouldContain($"`{HostFunctionScenarios.InsertedCall}`");
        readme.ShouldContain(HostFunctionWorld.NormalizeVinSummary, Case.Sensitive, "the summary the README registers is the one the world registers");
    }
}
