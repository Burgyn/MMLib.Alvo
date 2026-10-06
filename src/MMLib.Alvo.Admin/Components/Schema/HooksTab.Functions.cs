using Microsoft.Extensions.Logging;
using MMLib.Alvo.Expressions;

namespace MMLib.Alvo.Admin.Components.Schema;

/* The functions a CEL box may call, offered under it, and Insert (spec §9, E8). */
public partial class HooksTab
{
    private const string ConditionInputId = "hook-condition";

    /// <summary>What each profile's boxes offer, from the catalog; empty until it is read, or when it could not be.</summary>
    private Dictionary<CelProfile, IReadOnlyList<OfferedFunction>> _offered = [];

    /// <summary>
    /// Reads the catalog when a sheet opens, through the gateway (cached per circuit), and draws the lists it fills.
    /// </summary>
    /// <remarks>
    /// <para>
    /// On open rather than in a lifecycle override: an override of a public component is public API (the Admin baseline
    /// lists it), and a tab whose sheet never opens never needs the catalog. Fire-and-forget like the checks, so the sheet
    /// draws at once and the lists follow; they never take focus, so one arriving while the operator types moves nothing.
    /// </para>
    /// <para>
    /// A catalog that could not be read is an empty one (<see cref="Internal.ManagementGateway.CelFunctionsAsync"/>): the
    /// boxes then offer nothing, and every other part of the sheet works as before.
    /// </para>
    /// </remarks>
    private void LoadFunctions() => _ = LoadFunctionsAsync();

    private async Task LoadFunctionsAsync()
    {
        try
        {
            var functions = await Gateway.CelFunctionsAsync(CancellationToken.None);
            if (_lifetime.Ended)
            {
                return;
            }

            // An empty answer clears what an earlier one offered: a list the deployment no longer gives is not kept.
            _offered = functions.Count == 0 ? [] : new()
            {
                [CelProfile.Mutate] = FunctionOffer.For(functions, CelProfile.Mutate),
                [CelProfile.Condition] = FunctionOffer.For(functions, CelProfile.Condition),
            };
            Redraw();
        }
        catch (Exception ex)
        {
            FunctionsFailed(Logger, ex);
        }
    }

    /// <summary>Fixed text and the exception only, as <see cref="CheckFailed"/>.</summary>
    [LoggerMessage(EventId = 23, Level = LogLevel.Warning, Message = "Reading the CEL functions for the hook editor failed")]
    private static partial void FunctionsFailed(ILogger logger, Exception exception);

    private IReadOnlyList<OfferedFunction> Offered(CelProfile profile) => _offered.GetValueOrDefault(profile) ?? [];

    /// <summary>Inserts into a mutate row's expression, with <c>new.{field}</c> first when the box is blank and the type fits.</summary>
    /// <remarks>
    /// The row is held by reference across the caret's round trip and found again after it: a row removed meanwhile
    /// takes no text, and one that moved takes it at its new index rather than writing into the row now at the old one.
    /// </remarks>
    private async Task InsertIntoMutateAsync(int index, OfferedFunction function)
    {
        if (index >= Current.MutateRows.Count)
        {
            return;
        }

        var row = Current.MutateRows[index];
        var first = string.IsNullOrWhiteSpace(row.Text) ? FunctionOffer.FirstArgument(function, Current.Point, FieldOf(row)) : null;
        var inserted = await InsertAsync(MutateValueId(index), row.Text, FunctionOffer.Template(function, first));
        var now = Current.MutateRows.IndexOf(row);
        if (now < 0)
        {
            return;
        }

        TypeMutateText(now, inserted.Text);
        await SelectInsertedAsync(MutateValueId(now), inserted);
    }

    /// <summary>Inserts into the condition; only its text mode offers the list.</summary>
    private async Task InsertIntoConditionAsync(OfferedFunction function)
    {
        var inserted = await InsertAsync(ConditionInputId, Current.Condition, FunctionOffer.Template(function, null));
        TypeCondition(inserted.Text);
        await SelectInsertedAsync(ConditionInputId, inserted);
    }

    /// <summary>Writes the template at the box's caret, or at its end when the caret is unknown.</summary>
    /// <remarks>
    /// The caret is the browser's: the box keeps its selection while the Insert button holds focus, so it is read when
    /// Insert is pressed rather than tracked on every keystroke.
    /// </remarks>
    private async Task<Insertion> InsertAsync(string id, string text, Insertion template)
    {
        var caret = await Interop.CaretAsync(id);
        return caret is [var start, var end]
            ? FunctionOffer.Insert(text, start, end, template)
            : FunctionOffer.Insert(text, text.Length, text.Length, template);
    }

    /// <summary>Draws the inserted text, then focuses the box and selects the placeholder in it.</summary>
    /// <remarks>
    /// The text goes along, and the browser selects only once the box holds exactly it (<c>selectRange</c> in admin.js):
    /// the sheet is drawn by the dialog provider, so nothing on this side proves when the box has been redrawn.
    /// </remarks>
    private Task SelectInsertedAsync(string id, Insertion inserted)
    {
        StateHasChanged();
        return Interop.SelectRangeAsync(id, inserted.SelectFrom, inserted.SelectLength, inserted.Text);
    }
}
