using Microsoft.AspNetCore.Components;
using MMLib.Alvo.Admin.Internal;

namespace MMLib.Alvo.Admin.Components.DesignSystem;

/// <summary>
/// Learns the operator's offset from UTC from the browser, once, after its own first render, and tells its owner so
/// the owner can draw again in the operator's own time (<see cref="AdminInterop.UtcOffset"/>).
/// </summary>
/// <remarks>
/// <para>
/// <b>A component rather than an override on the screen</b>, for <see cref="FocusFirstOnRender"/>'s reason: every
/// Razor component is public, so an <c>OnAfterRenderAsync</c> written on a screen would be a member of the package's
/// contract. This is internal, and a screen gains one line of markup (<see cref="On"/>).
/// </para>
/// <para>
/// <b>After the render because the browser is only reachable then.</b> Until it answers, a time the screen shows
/// says UTC (<c>LockoutWords</c>); the owner is told only when this call learned the offset, so a circuit that already
/// knows it draws nothing twice.
/// </para>
/// </remarks>
internal sealed class OperatorClock : ComponentBase
{
    /// <summary>Raised once the offset has been learned, so the owner draws again.</summary>
    [Parameter, EditorRequired]
    public Action Learned { get; set; } = default!;

    [Inject]
    private AdminInterop Interop { get; set; } = default!;

    /// <summary>
    /// The markup for one, written <c>@OperatorClock.On(StateHasChanged)</c>: the Razor compiler only discovers public
    /// components as tags, and this one is internal on purpose.
    /// </summary>
    /// <param name="learned">What the owner does once the offset is known: draw again.</param>
    /// <returns>The fragment.</returns>
    public static RenderFragment On(Action learned) => builder =>
    {
        builder.OpenComponent<OperatorClock>(0);
        builder.AddComponentParameter(1, nameof(Learned), learned);
        builder.CloseComponent();
    };

    /// <inheritdoc />
    protected override async Task OnAfterRenderAsync(bool firstRender)
    {
        if (firstRender && await Interop.LearnUtcOffsetAsync())
        {
            Learned();
        }
    }
}
