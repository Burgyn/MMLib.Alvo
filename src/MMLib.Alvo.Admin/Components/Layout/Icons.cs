namespace MMLib.Alvo.Admin.Components.Layout;

/// <summary>
/// The inline icon set, as the path data inside a 20×20 stroked viewBox.
/// </summary>
/// <remarks>
/// <para>
/// <b>Inline, and no icon package.</b> An icon font or an SVG sprite package is a second
/// dependency and a second network request for eleven shapes that never change, and this package's
/// whole justification is that it is the heavy one — so it does not get heavier for this. They are
/// drawn stroked from <c>currentColor</c>, which is what makes them correct in both themes with no
/// second asset.
/// </para>
/// <para>
/// The shapes are the reference drawing's own (<c>docs/design/f5-admin</c>), so the built screen
/// and the drawing it was reviewed against are not two different pictures.
/// </para>
/// </remarks>
internal static class Icons
{
    /// <summary>A four-pane grid — the overview.</summary>
    public const string Overview = """<path d="M3 3h6v6H3zM11 3h6v6h-6zM3 11h6v6H3zM11 11h6v6h-6z"/>""";

    /// <summary>Boxes joined by edges — entities and their relationships.</summary>
    public const string Schema = """<path d="M4 4h5v4H4zM11 8h5v4h-5zM4 12h5v4H4z"/><path d="M9 6h2v4M9 14h2v-2"/>""";

    /// <summary>Three rules — rows.</summary>
    public const string Data = """<path d="M3 5h14M3 10h14M3 15h14"/>""";

    /// <summary>Lines of decreasing length with a decision on the end.</summary>
    public const string Rules = """<path d="M4 6h12M4 10h8M4 14h5"/><circle cx="15" cy="13" r="2.4"/>""";

    /// <summary>A shield.</summary>
    public const string Access = """<path d="M10 3l6 2v5c0 4-3 6-6 7-3-1-6-3-6-7V5z"/>""";

    /// <summary>A clock — the append-only record of what changed.</summary>
    public const string History = """<circle cx="10" cy="10" r="7"/><path d="M10 6v4l3 2"/>""";

    /// <summary>A plug.</summary>
    public const string Integrations = """<path d="M7 3v5M13 3v5"/><path d="M4 8h12v2a6 6 0 01-12 0z"/><path d="M10 16v2"/>""";

    /// <summary>A bolt — something happens, so something else runs.</summary>
    public const string Automations = """<path d="M11 2L4 11h5l-1 7 7-9h-5z"/>""";

    /// <summary>An italic f.</summary>
    public const string Functions = """<path d="M7 16c2 0 2-4 2-6s0-6 2-6"/><path d="M6 10h7"/>""";

    /// <summary>A cog.</summary>
    public const string Settings = """<circle cx="10" cy="10" r="3"/><path d="M10 2v2M10 16v2M2 10h2M16 10h2M4.5 4.5l1.5 1.5M14 14l1.5 1.5M15.5 4.5L14 6M6 14l-1.5 1.5"/>""";

    /// <summary>A magnifier.</summary>
    public const string Search = """<circle cx="9" cy="9" r="5.5"/><path d="M13 13l4 4"/>""";
}
