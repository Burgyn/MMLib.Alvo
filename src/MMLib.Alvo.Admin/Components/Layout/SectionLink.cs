using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Routing;
using MMLib.Alvo.Admin.Internal;

namespace MMLib.Alvo.Admin.Components.Layout;

/// <summary>
/// A navigation entry's link: a <see cref="NavLink"/> that also stays active on the screens its section owns.
/// </summary>
/// <remarks>
/// <para>
/// <b>One link for the sidebar, the phone's sheet and its bottom bar</b>, so the three cannot disagree about
/// which entry is current (docs/architecture/admin-dashboard-review.md, F-26). Overview is the dashboard's
/// root, so it matches its own address only — a prefix match would mark it active on every screen.
/// </para>
/// <para>
/// <b>Internal</b>, being written as a class rather than a <c>.razor</c> file: its parameter is an internal
/// type, and nothing outside the shell renders one. Razor markup only finds a public component by its tag,
/// so the shell draws it through <see cref="For"/>.
/// </para>
/// </remarks>
internal sealed class SectionLink : NavLink
{
    /// <summary>The section the link goes to.</summary>
    [Parameter, EditorRequired]
    public AdminSection Section { get; set; } = default!;

    /// <inheritdoc />
    protected override void OnParametersSet()
    {
        Match = Section.Path == AdminPaths.Overview ? NavLinkMatch.All : NavLinkMatch.Prefix;
        base.OnParametersSet();
    }

    /// <inheritdoc />
    protected override bool ShouldMatch(string uriAbsolute)
        => base.ShouldMatch(uriAbsolute) || Section.OwnsAlso(uriAbsolute);

    /// <summary>The link to <paramref name="section"/>, as a fragment the shell's markup can place.</summary>
    /// <param name="section">Where it goes.</param>
    /// <param name="cssClass">The link's class.</param>
    /// <param name="activeClass">The class it adds while its section is current.</param>
    /// <param name="content">What the link shows.</param>
    /// <param name="attributes">Anything else for the anchor, such as a click handler.</param>
    public static RenderFragment For(
        AdminSection section, string cssClass, string activeClass, RenderFragment content,
        IReadOnlyDictionary<string, object>? attributes = null) => builder =>
    {
        builder.OpenComponent<SectionLink>(0);
        builder.AddComponentParameter(1, nameof(Section), section);
        builder.AddComponentParameter(2, "class", cssClass);
        builder.AddComponentParameter(3, nameof(ActiveClass), activeClass);
        builder.AddComponentParameter(4, "href", section.Path);
        builder.AddMultipleAttributes(5, attributes);
        builder.AddComponentParameter(6, nameof(ChildContent), content);
        builder.CloseComponent();
    };
}
