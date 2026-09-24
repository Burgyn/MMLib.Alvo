using Microsoft.AspNetCore.Components;

namespace MMLib.Alvo.Admin.Internal;

/// <summary>
/// Moves focus to an element once, when this is first drawn beside it — for a form that opens from the
/// keyboard and should be typed into straight away.
/// </summary>
/// <remarks>
/// <para>
/// <b>A component rather than an override on the screen.</b> Every Razor component is public, so an
/// <c>OnAfterRenderAsync</c> written on a page is a member of the package's contract; this is internal,
/// and a page gains only a line of markup (<see cref="On"/>).
/// </para>
/// <para>
/// <b>After its own first render, which is the only moment that works.</b> Arriving from another screen,
/// the router's <c>FocusOnNavigate</c> moves focus to the heading once the new page is drawn. This is
/// drawn inside the page, after the router's own children, so its after-render runs after that one and
/// the focus it makes is the one that stays.
/// </para>
/// <para>
/// The target is a function, not an <see cref="ElementReference"/>: a parameter is evaluated while the
/// parent builds its render tree, which is before the element beside it has been captured.
/// </para>
/// </remarks>
internal sealed class FocusOnRender : ComponentBase
{
    /// <summary>
    /// The markup for one, written <c>@FocusOnRender.On(() =&gt; _input)</c> — the Razor compiler only
    /// discovers public components as tags, and this one is internal on purpose.
    /// </summary>
    public static RenderFragment On(Func<ElementReference> target) => builder =>
    {
        builder.OpenComponent<FocusOnRender>(0);
        builder.AddComponentParameter(1, nameof(Target), target);
        builder.CloseComponent();
    };

    /// <summary>The element to focus, read after the render that drew it.</summary>
    [Parameter, EditorRequired]
    public Func<ElementReference> Target { get; set; } = default!;

    protected override async Task OnAfterRenderAsync(bool firstRender)
    {
        if (firstRender)
        {
            await Target().FocusAsync();
        }
    }
}
