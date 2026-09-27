using Microsoft.AspNetCore.Components;
using MMLib.Alvo.Admin.Internal;

namespace MMLib.Alvo.Admin.Components.DesignSystem;

/// <summary>
/// Moves focus to the first of some elements that is drawn, once, after the render that drew this beside them — for
/// a control that goes with the content it replaced, such as a pager's Next on the last page.
/// </summary>
/// <remarks>
/// <para>
/// <b>A component rather than an override on the screen</b>, for <see cref="FocusOnRender"/>'s reason: every Razor
/// component is public, so an <c>OnAfterRenderAsync</c> written on a screen would be a member of the package's
/// contract. This is internal, and a screen gains one line of markup (<see cref="On"/>).
/// </para>
/// <para>
/// <b>After the render, never before it</b>: until then the pressed button is still in the document, and focusing it
/// would hand focus to an element about to be removed, which leaves it on <c>&lt;body&gt;</c>. Keyed by the attempt,
/// so every press draws a new one.
/// </para>
/// </remarks>
internal sealed class FocusFirstOnRender : ComponentBase
{
    /// <summary>CSS selectors, most wanted first.</summary>
    [Parameter, EditorRequired]
    public IReadOnlyList<string> Selectors { get; set; } = [];

    [Inject]
    private AdminInterop Interop { get; set; } = default!;

    /// <summary>
    /// The markup for one, written <c>@FocusFirstOnRender.On(selectors, attempt)</c>: the Razor compiler only
    /// discovers public components as tags, and this one is internal on purpose.
    /// </summary>
    /// <param name="selectors">Where focus may go, most wanted first.</param>
    /// <param name="attempt">A count the owner raises per move, so each one happens.</param>
    public static RenderFragment On(IReadOnlyList<string> selectors, int attempt) => builder =>
    {
        builder.OpenComponent<FocusFirstOnRender>(0);
        builder.SetKey(attempt);
        builder.AddComponentParameter(1, nameof(Selectors), selectors);
        builder.CloseComponent();
    };

    /// <inheritdoc />
    protected override async Task OnAfterRenderAsync(bool firstRender)
    {
        if (firstRender)
        {
            await Interop.FocusFirstOnceShownAsync(Selectors);
        }
    }
}
