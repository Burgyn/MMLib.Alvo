using Microsoft.AspNetCore.Components;
using Microsoft.JSInterop;
using MudBlazor;

namespace MMLib.Alvo.Admin.Components.DesignSystem;

/// <summary>
/// Scrolls an element into view once, when this is first drawn beside it: a created item appears in place and is
/// scrolled to (spec §3.5).
/// </summary>
/// <remarks>
/// <para>
/// <b>A component rather than an override on the screen</b>, for <c>FocusOnRender</c>'s reason: every Razor
/// component is public, so an <c>OnAfterRenderAsync</c> written on a tab would be a member of the package's
/// contract. This is internal, and a tab gains one line of markup (<see cref="On"/>).
/// </para>
/// <para>
/// <b>Drawn after the list</b>, so its first after-render runs once the row it names is in the document. Keyed by
/// the attempt, so every new item draws a new one and is scrolled to again.
/// </para>
/// </remarks>
internal sealed class RevealOnRender : ComponentBase
{
    /// <summary>The CSS selector of the element to bring into view.</summary>
    [Parameter, EditorRequired]
    public string Selector { get; set; } = string.Empty;

    [Inject]
    private IScrollManager Scroll { get; set; } = default!;

    /// <summary>
    /// The markup for one, written <c>@RevealOnRender.On(selector, attempt)</c>: the Razor compiler only discovers
    /// public components as tags, and this one is internal on purpose.
    /// </summary>
    /// <param name="selector">The element to bring into view.</param>
    /// <param name="attempt">A count the owner raises per new item, so each one is revealed.</param>
    public static RenderFragment On(string selector, int attempt) => builder =>
    {
        builder.OpenComponent<RevealOnRender>(0);
        builder.SetKey($"{attempt}:{selector}");
        builder.AddComponentParameter(1, nameof(Selector), selector);
        builder.CloseComponent();
    };

    /// <inheritdoc />
    protected override async Task OnAfterRenderAsync(bool firstRender)
    {
        if (!firstRender)
        {
            return;
        }

        try
        {
            await Scroll.ScrollIntoViewAsync(Selector, ScrollBehavior.Auto);
        }
        catch (Exception exception) when (exception is JSDisconnectedException or OperationCanceledException)
        {
            /* The circuit went with the screen; there is nothing left to scroll. */
        }
    }
}
