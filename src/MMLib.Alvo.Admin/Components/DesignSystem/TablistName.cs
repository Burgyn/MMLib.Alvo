using Microsoft.AspNetCore.Components;
using Microsoft.JSInterop;

namespace MMLib.Alvo.Admin.Components.DesignSystem;

/// <summary>
/// Gives a <c>MudTabs</c> strip its accessible name: the library draws <c>role=tablist</c> a level inside the frame
/// its attributes land on, where no parameter reaches.
/// </summary>
/// <remarks>
/// Internal, for <c>RevealOnRender</c>'s reason, and drawn right after the strip, so its first after-render runs once
/// the tablist is in the document. Keyed by the label, so a move to another entity names the strip again. Once per
/// label, not a watcher: the library keeps the tablist element across its own redraws.
/// </remarks>
internal sealed class TablistName : ComponentBase
{
    /// <summary>A selector for the strip's outer frame.</summary>
    [Parameter, EditorRequired]
    public string Frame { get; set; } = string.Empty;

    /// <summary>The name the tablist is read by.</summary>
    [Parameter, EditorRequired]
    public string Label { get; set; } = string.Empty;

    [Inject]
    private IJSRuntime Js { get; set; } = default!;

    /// <summary>The markup for one, written <c>@TablistName.On(frame, label)</c>.</summary>
    /// <param name="frame">A selector for the strip's outer frame.</param>
    /// <param name="label">The name.</param>
    public static RenderFragment On(string frame, string label) => builder =>
    {
        builder.OpenComponent<TablistName>(0);
        builder.SetKey(label);
        builder.AddComponentParameter(1, nameof(Frame), frame);
        builder.AddComponentParameter(2, nameof(Label), label);
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
            await Js.InvokeVoidAsync("alvo.nameTablist", Frame, Label);
        }
        catch (Exception exception) when (exception is JSDisconnectedException or OperationCanceledException)
        {
            /* The circuit went with the screen; there is no strip left to name. */
        }
    }
}
