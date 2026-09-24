using MMLib.Alvo.Admin.Tests.Internal;

namespace MMLib.Alvo.Admin.Tests;

/// <summary>
/// The stylesheet says only what the product uses, and says each shared value once
/// (docs/architecture/admin-dashboard-review.md, F-21).
/// </summary>
/// <remarks>
/// <para>
/// <b>A dead selector costs a reader, not a user.</b> Nothing renders it, so nothing fails — and the next person
/// to style a drawer finds <c>.a-drawer</c>, assumes it is the product's, and builds on a rule no screen has ever
/// exercised. A class the markup names and the stylesheet does not define is the mirror image: the hook looks
/// load-bearing and is not. The gallery draws a few components the product has not built yet; their rules carry
/// a <c>/* gallery-only */</c> comment, so the difference is written down rather than remembered.
/// </para>
/// </remarks>
public sealed class StylesheetHygieneTests
{
    /// <summary>The one phone breakpoint: the sidebar gives way to the bottom bar, and every screen stacks.</summary>
    private const string Phone = "720px";

    /// <summary>Where a two-column screen's aside drops under its main column — a layout width, not a device.</summary>
    private const string SplitStacks = "1100px";

    private static readonly string _css = File.ReadAllText(Stylesheet.AlvoCssPath);

    private static readonly string _gallery = File.ReadAllText(Stylesheet.GalleryPath);

    [Fact]
    public void Every_class_the_product_names_is_defined()
        => Stylesheet.ClassesTheProductNames().Except(Stylesheet.DefinedClasses(_css)).Order().ShouldBeEmpty();

    [Fact]
    public void Every_class_the_stylesheet_defines_is_named_by_the_product_or_marked_gallery_only()
        => Stylesheet.DefinedClasses(_css)
            .Except(Stylesheet.ClassesTheProductNames())
            .Except(Stylesheet.GalleryOnlyClasses(_css))
            .Order().ShouldBeEmpty();

    /// <summary>The marker is a claim, and both halves of it are checked: the gallery draws it, and nothing else does.</summary>
    [Fact]
    public void A_gallery_only_class_is_drawn_by_the_gallery_and_by_nothing_the_product_ships()
    {
        var marked = Stylesheet.GalleryOnlyClasses(_css);

        marked.ShouldNotBeEmpty();
        marked.Where(name => !_gallery.Contains(name, StringComparison.Ordinal)).ShouldBeEmpty();
        marked.Intersect(Stylesheet.ClassesTheProductNames()).ShouldBeEmpty();
    }

    /// <summary>The scan behind the three facts above sees a marked rule, and only that rule.</summary>
    [Fact]
    public void The_gallery_only_scan_reads_the_rule_the_marker_opens()
    {
        const string css = "  /* gallery-only: drawn there. */\n  .a-dot,\n  .a-dot--big {\n  }\n\n  .a-real {\n  }\n";

        Stylesheet.GalleryOnlyClasses(css).Order().ShouldBe(["a-dot", "a-dot--big"]);
        Stylesheet.DefinedClasses(css).Order().ShouldBe(["a-dot", "a-dot--big", "a-real"]);
    }

    /// <summary>
    /// The phone breakpoint is one number, written where each media query needs it.
    /// </summary>
    /// <remarks>
    /// A custom property cannot stand in a media query without a build step, which the review rules out, so the
    /// value is written out per query and pinned here instead: a query moved to 719 px would leave a band of
    /// widths drawn half as a phone and half as a desktop, and nothing else would notice.
    /// </remarks>
    [Fact]
    public void Every_media_query_breaks_at_the_phone_width_or_where_a_split_stacks()
    {
        var widths = Stylesheet.MediaWidths(_css);

        widths.ShouldContain(Phone);
        widths.Distinct().Order().ShouldBe([SplitStacks, Phone]);
    }

    /// <summary>Every <c>z-index</c> names a plane, so two overlays cannot drift a number apart.</summary>
    [Fact]
    public void Every_z_index_names_a_stacking_plane()
    {
        var values = Stylesheet.ZIndexValues(_css);

        values.ShouldNotBeEmpty();
        values.Where(value => !value.StartsWith("var(--z-", StringComparison.Ordinal)).ShouldBeEmpty();
    }
}
