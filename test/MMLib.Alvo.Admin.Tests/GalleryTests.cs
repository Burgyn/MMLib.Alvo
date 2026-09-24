using MMLib.Alvo.Admin.Tests.Internal;

namespace MMLib.Alvo.Admin.Tests;

/// <summary>
/// The gallery is a proof of the shipped stylesheet, not a second copy of it.
/// </summary>
/// <remarks>
/// <para>
/// <b>The failure this prevents is the ordinary one.</b> A design gallery that carries its own
/// stylesheet drifts within a week: it stays beautiful while the product changes underneath it, and
/// it is then worse than no gallery, because it is evidence for a claim that has stopped being
/// true. Referencing the shipped file makes a regression visible in both at once.
/// </para>
/// </remarks>
public sealed class GalleryTests
{
    private static readonly string _gallery = File.ReadAllText(Stylesheet.GalleryPath);

    [Fact]
    public void The_gallery_references_the_shipped_stylesheet()
        => _gallery.ShouldContain("src/MMLib.Alvo.Admin/wwwroot/alvo.css");

    [Fact]
    public void The_gallery_declares_no_colour_of_its_own()
        => Stylesheet.LiteralColoursOutsideTokens(_gallery).ShouldBeEmpty();

    /// <summary>
    /// Design §4.1 and §4.3: both classes of "not yet" are shown, and shown differently.
    /// </summary>
    [Fact]
    public void The_gallery_shows_both_classes_of_not_yet()
    {
        _gallery.ShouldContain("a-notyet-panel");
        _gallery.ShouldContain("a-refused");
    }

    /// <summary>
    /// Every design-system primitive is drawn here, in the markup it renders.
    /// </summary>
    /// <remarks>
    /// One class per primitive, the one only that primitive emits — so a component added to
    /// <c>Components/DesignSystem</c> without its drawing, or a drawing left behind when a modifier is
    /// renamed, fails here rather than in review.
    /// </remarks>
    [Theory]
    [InlineData("a-section--bar")]
    [InlineData("a-panel--padded")]
    [InlineData("a-listrow--action")]
    [InlineData("a-hint")]
    [InlineData("role=\"radiogroup\"")]
    [InlineData("a-skeleton--sm")]
    [InlineData("a-notyet-panel__consequence")]
    [InlineData("a-refused__reason")]
    [InlineData("a-spacer")]
    public void The_gallery_draws_each_primitive(string marker)
        => _gallery.ShouldContain(marker);

    [Fact]
    public void The_gallery_shows_the_mobile_substitute_for_a_grid_row()
        => _gallery.ShouldContain("a-row-card");
}
