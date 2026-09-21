namespace MMLib.Alvo.Admin.Components.Layout;

/// <summary>
/// One entry in the dashboard's navigation.
/// </summary>
/// <param name="Title">What the operator reads.</param>
/// <param name="Path">Where it goes.</param>
/// <param name="Icon">The inline SVG path data the icon is drawn from.</param>
/// <param name="GoKey">
/// The letter that reaches it after <c>g</c>. <see langword="null"/> for a section with no
/// shortcut — which today is every section that is not yet built.
/// </param>
/// <param name="NotYet">
/// Whether this section is declared and not running. The navigation shows it, greyed, under its
/// own separator, because hiding it would make the dashboard look complete when it is not — and an
/// operator who cannot find Automations at all has no way to learn that Alvo means to have them.
/// </param>
internal sealed record AdminSection(
    string Title, string Path, string Icon, char? GoKey = null, bool NotYet = false);

/// <summary>
/// The navigation, in the order design §4.3 fixes.
/// </summary>
/// <remarks>
/// <para>
/// <b>Schema sits above Data</b>, and §4.3 argues it: the dashboard's reason to exist is defining
/// what a backend <i>is</i>. Browsing records proves the model works; it is not why the tool is
/// opened.
/// </para>
/// <para>
/// <b>The separator is an acceptance criterion, not tidiness.</b> The phone bar holds about five
/// items and must contain only what works, so <see cref="Live"/> is what the bar takes and the
/// <c>Not yet</c> entries live below the rule in the sidebar and in the sheet. Interleaving them
/// would force a second navigation to be designed for the phone, which is how a navigation starts
/// lying.
/// </para>
/// </remarks>
internal static class AdminNavigation
{
    /// <summary>The sections that work today, in navigation order.</summary>
    public static IReadOnlyList<AdminSection> Live { get; } =
    [
        new("Overview", AlvoAdmin.BasePath, Icons.Overview, 'o'),
        new("Schema", $"{AlvoAdmin.BasePath}/schema", Icons.Schema, 's'),
        new("Data", $"{AlvoAdmin.BasePath}/data", Icons.Data, 'd'),
        new("Rules", $"{AlvoAdmin.BasePath}/rules", Icons.Rules, 'r'),
        new("Access", $"{AlvoAdmin.BasePath}/access", Icons.Access, 'a'),
        new("Configuration history", $"{AlvoAdmin.BasePath}/history", Icons.History, 'h'),
        new("Integrations", $"{AlvoAdmin.BasePath}/integrations", Icons.Integrations, 'i'),
    ];

    /// <summary>The sections the descriptor may declare and this build does not run.</summary>
    /// <remarks>
    /// Their <c>Not yet</c> badge is rendered from <c>capabilities</c> rather than from this list —
    /// this list only fixes where they sit. A section that stopped being warned about would still
    /// be shown here until somebody moved it, which is why the badge is not hard-coded beside it.
    /// </remarks>
    public static IReadOnlyList<AdminSection> NotYet { get; } =
    [
        new("Automations", $"{AlvoAdmin.BasePath}/automations", Icons.Automations, NotYet: true),
        new("Functions", $"{AlvoAdmin.BasePath}/functions", Icons.Functions, NotYet: true),
    ];

    /// <summary>The sections that sit below the second separator.</summary>
    public static IReadOnlyList<AdminSection> Footer { get; } =
    [
        new("Settings", $"{AlvoAdmin.BasePath}/settings", Icons.Settings, ','),
    ];

    /// <summary>Every section, in the order they are drawn.</summary>
    public static IEnumerable<AdminSection> All => Live.Concat(NotYet).Concat(Footer);

    /// <summary>The five the phone's bottom bar carries.</summary>
    public static IEnumerable<AdminSection> Bar => Live.Take(5);
}
