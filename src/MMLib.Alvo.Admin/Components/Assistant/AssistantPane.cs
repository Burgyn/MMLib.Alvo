using Microsoft.AspNetCore.Components;

namespace MMLib.Alvo.Admin.Components.Assistant;

/// <summary>
/// Whether the assistant's pane is open, and how it asks to close, cascaded from the layout that owns both.
/// </summary>
/// <remarks>
/// <para>
/// <b>Cascaded, and therefore internal</b>, for <c>StagedView</c>'s reason: a <c>[Parameter]</c> must be public, and
/// the Razor SDK makes every component public, so an <c>Open</c> and an <c>OnClose</c> on the pane would have put the
/// shell's wiring into the package's published surface. The layout owns the state because the launcher is in its app
/// bar; the pane reads it to move focus on open and asks through it to close.
/// </para>
/// <para>
/// <b>One instance, cascaded as fixed, that says when it changes</b> (final review M16). A value cascaded afresh on
/// every layout render counts as changed every time, whatever it holds, so the pane redrew, and asked the browser to
/// follow its thread, on every render of the shell, streamed or closed. The pane now redraws when this says it opened
/// or closed, and on its own turns.
/// </para>
/// </remarks>
internal sealed class AssistantPane
{
    /// <summary>A pane that is open or closed, and closes through <paramref name="close"/>.</summary>
    /// <param name="close">Closes the pane and gives focus back to the launcher.</param>
    public AssistantPane(EventCallback close) => Close = close;

    /// <summary>Closed, with nothing to ask: what the pane reads when no layout cascaded one.</summary>
    public static AssistantPane None { get; } = new(EventCallback.Empty);

    /// <summary>Whether the pane is showing.</summary>
    public bool Open { get; private set; }

    /// <summary>Closes the pane and gives focus back to the launcher.</summary>
    public EventCallback Close { get; }

    /// <summary>Raised when the pane opens or closes, on the layout's renderer thread.</summary>
    public event Action? Changed;

    /// <summary>Opens or closes the pane, and says so when that is a change.</summary>
    /// <param name="open">Whether it is to show.</param>
    public void Set(bool open)
    {
        if (open == Open)
        {
            return;
        }

        Open = open;
        Changed?.Invoke();
    }
}
