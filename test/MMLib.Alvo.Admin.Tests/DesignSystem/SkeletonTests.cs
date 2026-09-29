namespace MMLib.Alvo.Admin.Tests.DesignSystem;

/// <summary>Loading has one look (spec §3.6); <c>SettingsScenarios</c> shows it reaching the browser, labelled.</summary>
public sealed class SkeletonTests
{
    private static readonly string _components = Path.Combine(RepositoryRoot.Find(), "src", "MMLib.Alvo.Admin", "Components");

    [Fact]
    public void No_screen_draws_a_loading_placeholder_of_its_own()
        => Directory.EnumerateFiles(_components, "*.razor", SearchOption.AllDirectories)
            .Where(path => Path.GetFileName(path) != "Skeleton.razor")
            .Where(path => File.ReadAllText(path) is var source
                && (source.Contains("<MudSkeleton", StringComparison.Ordinal)
                    || source.Contains("a-skeleton", StringComparison.Ordinal)))
            .Select(path => Path.GetRelativePath(_components, path))
            .ShouldBeEmpty("a screen that is loading renders <Skeleton Size=\"…\" />");
}
