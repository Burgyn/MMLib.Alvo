using Microsoft.Extensions.Logging;
using MudBlazor;

namespace MMLib.Alvo.Admin.Components.Schema;

/* Focus stays on a select inside the sheet after its list closes (spec §3.2, §11) — one helper for every select here. */
public partial class HooksTab
{
    /// <summary>The sheet's selects by id — the mutate rows', the pickers' — for <see cref="RefocusSelectAsync"/>.</summary>
    private readonly Dictionary<string, MudSelect<string>> _selects = new(StringComparer.Ordinal);

    /// <summary>
    /// Gives focus back to a select once its choice is drawn: the library closes its list and swaps its box for the one
    /// that shows the value, and focus, left on the list's option, fell to <c>&lt;body&gt;</c> — outside the sheet, where
    /// Escape no longer reaches it (measured in a browser; a select inside a dialog is new here, spec §11).
    /// </summary>
    /// <remarks>
    /// Through the select's own <c>FocusAsync</c>, not a selector over the library's markup. Not awaited: the library
    /// awaits this handler before it draws the value, so focus is asked for once the handler has returned and the
    /// current work has run (<see cref="Task.Yield"/>). Measured to hold on a first choice, a re-choice and the enum select.
    /// </remarks>
    /// <param name="id">The select's id in <see cref="_selects"/>.</param>
    private Task RefocusSelectAsync(string id)
    {
        if (_selects.TryGetValue(id, out var select))
        {
            _ = FocusWhenDrawnAsync(select, onlyWhenLost: false);
        }

        return Task.CompletedTask;
    }

    /// <summary>
    /// A select's list opened or closed. A close that chose nothing new — a re-choice of the value it shows, or Escape —
    /// raises no value change, so <see cref="RefocusSelectAsync"/> never runs for it; focus is handed back here instead,
    /// but only when it fell to <c>&lt;body&gt;</c>, so a close by a click on another control leaves focus on that control.
    /// </summary>
    /// <param name="id">The select's id in <see cref="_selects"/>.</param>
    /// <param name="open">Whether the list is open now.</param>
    private void SelectOpenChanged(string id, bool open)
    {
        if (!open && _selects.TryGetValue(id, out var select))
        {
            _ = FocusWhenDrawnAsync(select, onlyWhenLost: true);
        }
    }

    /// <summary>
    /// Focuses the select after the current render — when <paramref name="onlyWhenLost"/>, only if focus is on
    /// <c>&lt;body&gt;</c> by then. Never throws: focus is a courtesy, and this runs unobserved.
    /// </summary>
    /// <remarks>
    /// Everything is caught and logged at Debug rather than filtered: the library's <c>FocusAsync</c> already swallows a
    /// select or circuit that went away in between, so what is left to arrive here is not foreseeable by type, and an
    /// unobserved fault of a fire-and-forget call would otherwise vanish.
    /// </remarks>
    private async Task FocusWhenDrawnAsync(MudSelect<string> select, bool onlyWhenLost)
    {
        await Task.Yield();
        try
        {
            if (!onlyWhenLost || await Interop.FocusIsLostAsync())
            {
                await select.FocusAsync();
            }
        }
        catch (Exception ex)
        {
            RefocusFailed(Logger, ex);
        }
    }

    [LoggerMessage(EventId = 22, Level = LogLevel.Debug, Message = "Handing focus back to a select on the hook sheet failed")]
    private static partial void RefocusFailed(ILogger logger, Exception exception);
}
