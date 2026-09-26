using Microsoft.AspNetCore.Components;

namespace MMLib.Alvo.Admin.Components.Assistant;

/// <summary>
/// Whether the assistant's pane is open, and how it asks to close, cascaded from the layout that owns both.
/// </summary>
/// <remarks>
/// <b>Cascaded, and therefore internal</b>, for <c>StagedView</c>'s reason: a <c>[Parameter]</c> must be public, and
/// the Razor SDK makes every component public, so an <c>Open</c> and an <c>OnClose</c> on the pane would have put the
/// shell's wiring into the package's published surface. The layout owns the state because the launcher is in its app
/// bar; the pane reads it to move focus on open and asks through it to close.
/// </remarks>
/// <param name="Open">Whether the pane is showing.</param>
/// <param name="Close">Closes the pane and gives focus back to the launcher.</param>
internal sealed record AssistantPane(bool Open, EventCallback Close)
{
    /// <summary>Closed, with nothing to ask — what the pane reads when no layout cascaded one.</summary>
    public static AssistantPane None { get; } = new(false, EventCallback.Empty);
}
