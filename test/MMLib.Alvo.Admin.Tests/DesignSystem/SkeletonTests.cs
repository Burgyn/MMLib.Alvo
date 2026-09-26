namespace MMLib.Alvo.Admin.Tests.DesignSystem;

/// <summary>Loading has one look, and it is something a screen reader hears (spec §3.6).</summary>
/// <remarks>
/// <para>
/// <b>Read from the source, because no browser sees it reliably.</b> A prerendered screen waits for its load before
/// the response is written, and the gateway answers the next read from its cache without yielding, so the loading
/// branch is on screen for no measurable time on a local host. A scenario that waited for it would pass or fail on
/// the machine's speed; this pins what it is drawn with instead.
/// </para>
/// </remarks>
public sealed class SkeletonTests
{
    private static readonly string _components = Path.Combine(RepositoryRoot.Find(), "src", "MMLib.Alvo.Admin", "Components");

    [Fact]
    public void The_skeleton_is_the_librarys_lines_in_a_frame_labelled_as_loading()
    {
        var source = File.ReadAllText(Path.Combine(_components, "DesignSystem", "Skeleton.razor"));

        source.ShouldContain("""<div class="a-skeleton-frame" aria-busy="true" aria-label="Loading">""");
        source.ShouldContain("<MudSkeleton ");
    }

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
