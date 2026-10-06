namespace MMLib.Alvo.Admin.Components.Schema;

/// <summary>
/// What the hook sheet asks before it adds or saves: whether the guided condition's rows can be written as they stand
/// (ruling S-B). The rows answer by being asked, never by telling.
/// </summary>
/// <remarks>
/// <para>
/// <b>Pull, not push.</b> A refusal the rows pushed into the sheet would stay behind when the rows are unmounted (the
/// switch to text) or rebuilt (another hook opened), and refuse a save nothing on screen explains. Asked at the moment of
/// the save, the answer is always that of the rows on screen — or none when there are none.
/// </para>
/// <para>
/// Cascaded by the sheet with <c>IsFixed</c>: one gate for the sheet's life, so attaching never re-renders anything. The
/// rows attach every time they are drawn and never detach (a detach would need the public form to be disposable, spec
/// §14): the sheet asks only while it draws rows, and the rows it draws attach over any it drew before.
/// </para>
/// </remarks>
internal sealed class ConditionGate
{
    private Func<string?>? _ask;

    /// <summary>Why the rows on screen cannot be written, or <see langword="null"/> when they can or there are none.</summary>
    public string? Refusal => _ask?.Invoke();

    /// <summary>Puts rows on screen: the gate asks <paramref name="ask"/> from now on, instead of any rows before.</summary>
    /// <param name="ask">What the rows answer.</param>
    public void Attach(Func<string?> ask) => _ask = ask;
}
