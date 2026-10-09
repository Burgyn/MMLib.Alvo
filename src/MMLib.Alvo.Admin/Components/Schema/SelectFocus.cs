using Microsoft.Extensions.Logging;
using MudBlazor;

namespace MMLib.Alvo.Admin.Components.Schema;

/// <summary>
/// Gives focus back to a select inside the hook sheet once its choice is drawn (spec §3.2, §11) — the one helper every
/// select there uses, the sheet's own and the guided condition's.
/// </summary>
/// <remarks>
/// <para>
/// The library closes its list and swaps its box for the one that shows the value, and focus, left on the list's option,
/// fell to <c>&lt;body&gt;</c> — outside the sheet, where Escape no longer reaches it (measured in a browser; a select
/// inside a dialog is new here, spec §11).
/// </para>
/// <para>
/// Through the select's own <c>FocusAsync</c>, not a selector over the library's markup. Not awaited: the library awaits
/// the value-changed handler before it draws the value, so focus is asked for once the handler has returned and the
/// current work has run (<see cref="Task.Yield"/>). A re-choice of the value shown raises no value change and so never
/// runs this; focus stays on the select without it, which <c>AdminSession.ChooseAgainAsync</c> pins (ruling O).
/// </para>
/// </remarks>
internal static partial class SelectFocus
{
    /// <summary>Asks for focus on the select after the current render; a select not drawn is left alone.</summary>
    /// <param name="selects">The selects drawn, by id.</param>
    /// <param name="id">The select's id.</param>
    /// <param name="logger">Where a failure is logged, at Debug.</param>
    /// <returns>A completed task: the focus itself is not awaited.</returns>
    public static Task AfterChoice(IReadOnlyDictionary<string, MudSelect<string>> selects, string id, ILogger logger)
    {
        if (selects.TryGetValue(id, out var select))
        {
            _ = FocusWhenDrawnAsync(select, logger);
        }

        return Task.CompletedTask;
    }

    /// <summary>Focuses the select after the current render. Never throws: focus is a courtesy, and this runs unobserved.</summary>
    /// <remarks>
    /// Everything is caught and logged at Debug rather than filtered: the library's <c>FocusAsync</c> already swallows a
    /// select or circuit that went away in between, so what is left to arrive here is not foreseeable by type, and an
    /// unobserved fault of a fire-and-forget call would otherwise vanish.
    /// </remarks>
    private static async Task FocusWhenDrawnAsync(MudSelect<string> select, ILogger logger)
    {
        await Task.Yield();
        try
        {
            await select.FocusAsync();
        }
        catch (Exception ex)
        {
            RefocusFailed(logger, ex);
        }
    }

    [LoggerMessage(EventId = 22, Level = LogLevel.Debug, Message = "Handing focus back to a select on the hook sheet failed")]
    private static partial void RefocusFailed(ILogger logger, Exception exception);
}
