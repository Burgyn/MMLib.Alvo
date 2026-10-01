using MMLib.Alvo.Admin.Internal;

namespace MMLib.Alvo.Admin.Components.DesignSystem;

/// <summary>
/// The one refusal a screen or an editor has on show, and the attempt that drew it: every refusal is a new
/// <see cref="ErrorPanel"/> that takes focus when a press caused it (spec §3.3).
/// </summary>
/// <remarks>
/// <para>
/// <b>Why one type.</b> The same four fields (the refusal, a count keying the panel, whether a press caused it, a
/// headline) were written out in a dozen components, and each copy was a place the rule could drift: an alert without
/// a title here, a panel that did not take focus again there. The key is what makes a second refusal of the same
/// words a new panel, which is what moves focus and makes a screen reader announce it again.
/// </para>
/// <para>
/// Internal, like <see cref="RevealOnRender"/>: a field on a component is not a member of its contract, and
/// <see cref="RefusalPanel"/> is how its markup is written.
/// </para>
/// </remarks>
/// <typeparam name="T">
/// What the refusal is: a sentence the screen wrote (<see cref="string"/>), or an <see cref="AdminProblem"/> it
/// classified.
/// </typeparam>
internal sealed class RefusalState<T>
    where T : class
{
    /// <summary>The refusal on show, or <see langword="null"/>.</summary>
    public T? Current { get; private set; }

    /// <summary>How many refusals have been shown, which keys the panel so each one is drawn anew.</summary>
    public int Key { get; private set; }

    /// <summary>Whether the refusal answered a press, and so takes focus (spec §3.3).</summary>
    public bool TakesFocus { get; private set; }

    /// <summary>The headline for this refusal, when the screen has a better one than its panel's own.</summary>
    public string? Title { get; private set; }

    /// <summary>Whether a refusal is on show.</summary>
    public bool Shown => Current is not null;

    /// <summary>Shows <paramref name="refusal"/> as a new panel; a <see langword="null"/> one clears what is shown.</summary>
    /// <param name="refusal">The refusal.</param>
    /// <param name="fromPress">
    /// Whether a press caused it. A refusal met while the page loads does not take focus, which the router gave the
    /// page's heading.
    /// </param>
    /// <param name="title">The headline, or <see langword="null"/> for the panel's own.</param>
    public void Show(T? refusal, bool fromPress = true, string? title = null)
    {
        Current = refusal;
        Title = title;
        if (refusal is null)
        {
            return;
        }

        Key++;
        TakesFocus = fromPress;
    }

    /// <summary>Takes the refusal off the screen, as an answer to it does.</summary>
    public void Clear()
    {
        Current = null;
        Title = null;
    }
}
