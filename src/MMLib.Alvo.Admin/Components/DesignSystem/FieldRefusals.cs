using Microsoft.AspNetCore.Components;
using Microsoft.JSInterop;

namespace MMLib.Alvo.Admin.Components.DesignSystem;

/// <summary>
/// The refusals of a form that belong to one field each: drawn under that field and handed focus (spec §3.3), where
/// an <c>ErrorPanel</c> is for a refusal no field owns.
/// </summary>
/// <remarks>
/// <para>
/// <b>How a form uses it.</b> The form keeps one, keyed by each input's <c>id</c>. The input takes
/// <c>Error="@refusals.Has(id)"</c>, which draws it in the error tone and marks it <c>aria-invalid</c>, and
/// <c>aria-describedby="@refusals.DescribedBy(id)"</c>. Its <c>ValueChanged</c> calls <see cref="Clear"/>. Straight
/// after it, the form writes <c>@refusals.Under(id, () =&gt; input.FocusAsync())</c>: the sentence, and the focus.
/// </para>
/// <para>
/// <b>The sentence is drawn here, not as the library's <c>ErrorText</c>.</b> MudTextField 9 renders its error
/// text with no id and points no <c>aria-describedby</c> at it, so a screen reader landing on the refused input
/// would hear that it is invalid and not why. The sentence is <c>.a-field__problem</c>, the record form's own.
/// </para>
/// <para>
/// <b>Internal, and it names no library type</b>, so no public component parameter carries it (spec D5); the input
/// it describes is the form's own.
/// </para>
/// </remarks>
internal sealed class FieldRefusals
{
    private readonly Dictionary<string, string> _refused = new(StringComparer.Ordinal);

    /// <summary>Whether any field is refused.</summary>
    public bool Any => _refused.Count > 0;

    /// <summary>The field the last refusal asked to take focus, until it is cleared.</summary>
    public string? Focus { get; private set; }

    /// <summary>A count raised per refusal, so the same field refused twice takes focus twice.</summary>
    public int Attempt { get; private set; }

    /// <summary>Refuses <paramref name="field"/> with <paramref name="message"/>, and asks it to take focus.</summary>
    /// <param name="field">The input's id.</param>
    /// <param name="message">Why, and what to type instead.</param>
    public void Refuse(string field, string message)
    {
        _refused[field] = message;
        Focus = field;
        Attempt++;
    }

    /// <summary>Whether <paramref name="field"/> is refused.</summary>
    /// <param name="field">The input's id.</param>
    public bool Has(string field) => _refused.ContainsKey(field);

    /// <summary>Why <paramref name="field"/> is refused, or <see langword="null"/>.</summary>
    /// <param name="field">The input's id.</param>
    public string? For(string field) => _refused.GetValueOrDefault(field);

    /// <summary>Forgets the refusal of <paramref name="field"/>, which the operator is now changing.</summary>
    /// <param name="field">The input's id.</param>
    public void Clear(string field)
    {
        _refused.Remove(field);
        if (Focus == field)
        {
            Focus = null;
        }
    }

    /// <summary>Forgets every refusal, as a form does when it closes.</summary>
    public void ClearAll()
    {
        _refused.Clear();
        Focus = null;
    }

    /// <summary>The id of the sentence that says why <paramref name="field"/> is refused, or <see langword="null"/>.</summary>
    /// <param name="field">The input's id.</param>
    public string? DescribedBy(string field) => Has(field) ? ProblemId(field) : null;

    /// <summary>
    /// The markup under <paramref name="field"/>: the sentence that says why it is refused, and the focus handed to
    /// it after the render that refused it. Nothing when the field is not refused.
    /// </summary>
    /// <param name="field">The input's id.</param>
    /// <param name="focus">Focuses the input.</param>
    public RenderFragment? Under(string field, Func<ValueTask> focus)
    {
        if (For(field) is not { } message)
        {
            return null;
        }

        var takeFocus = Focus == field;
        var attempt = Attempt;
        return builder =>
        {
            builder.OpenElement(0, "span");
            builder.AddAttribute(1, "class", "a-field__problem");
            builder.AddAttribute(2, "id", ProblemId(field));
            builder.AddAttribute(3, "data-testid", "field-problem");
            builder.AddContent(4, message);
            builder.CloseElement();
            if (takeFocus)
            {
                builder.AddContent(5, FocusOnAttempt.On(focus, $"{attempt}:{field}"));
            }
        };
    }

    private static string ProblemId(string field) => $"{field}-problem";

    /// <summary>Runs a focus once, after its first render; keyed by the attempt, so every refusal draws a new one.</summary>
    /// <remarks>
    /// A component rather than an override on the form, for <c>FocusOnRender</c>'s reason: every Razor component is
    /// public, and an <c>OnAfterRenderAsync</c> written on one is a member of the package's contract.
    /// </remarks>
    private sealed class FocusOnAttempt : ComponentBase
    {
        /// <summary>Focuses the input.</summary>
        [Parameter, EditorRequired]
        public Func<ValueTask> Focus { get; set; } = default!;

        /// <summary>The markup for one, keyed by <paramref name="key"/>.</summary>
        /// <param name="focus">Focuses the input.</param>
        /// <param name="key">The attempt and the field.</param>
        public static RenderFragment On(Func<ValueTask> focus, string key) => builder =>
        {
            builder.OpenComponent<FocusOnAttempt>(0);
            builder.SetKey(key);
            builder.AddComponentParameter(1, nameof(Focus), focus);
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
                await Focus();
            }
            catch (Exception exception) when (exception is JSDisconnectedException or OperationCanceledException)
            {
                /* The circuit went with the form; there is nothing left to focus. */
            }
        }
    }
}
