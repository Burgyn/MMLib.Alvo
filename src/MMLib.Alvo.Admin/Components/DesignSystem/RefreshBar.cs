using Microsoft.AspNetCore.Components;
using MudBlazor;

namespace MMLib.Alvo.Admin.Components.DesignSystem;

/// <summary>
/// The refresh indicator at the top of a pane (spec §3.6): a thin indeterminate bar while the pane reads again what it
/// already shows, after a write, a Reload, a search or a page turn. The content stays in place under it.
/// </summary>
/// <remarks>
/// <para>
/// <b>Not for the first read</b>, which the pane's skeleton stands for; this is for the read that follows, where a
/// click on a slow store would otherwise look ignored.
/// </para>
/// <para>
/// <b>Drawn over the pane's top edge, in a slot of no height</b>, so the rows under it do not move down when it
/// appears and back up when it goes. Internal and written as markup (<c>@RefreshBar.While(_refreshing)</c>), for
/// <see cref="RevealOnRender"/>'s reason: no public member of a component names a library type (D5).
/// </para>
/// </remarks>
internal static class RefreshBar
{
    /// <summary>The bar, while <paramref name="refreshing"/>; nothing otherwise.</summary>
    /// <param name="refreshing">Whether the pane is reading again.</param>
    /// <returns>The markup.</returns>
    public static RenderFragment While(bool refreshing) => builder =>
    {
        if (!refreshing)
        {
            return;
        }

        builder.OpenElement(0, "div");
        builder.AddAttribute(1, "class", "a-refresh");
        builder.OpenComponent<MudProgressLinear>(2);
        builder.AddComponentParameter(3, nameof(MudProgressLinear.Indeterminate), true);
        builder.AddComponentParameter(4, nameof(MudProgressLinear.Color), Color.Primary);
        builder.AddAttribute(5, "aria-label", "Refreshing");
        builder.AddAttribute(6, "data-testid", "refreshing");
        builder.CloseComponent();
        builder.CloseElement();
    };
}
