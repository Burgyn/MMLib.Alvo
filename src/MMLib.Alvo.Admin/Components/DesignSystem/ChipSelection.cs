namespace MMLib.Alvo.Admin.Components.DesignSystem;

/// <summary>
/// What a press or a key does to a group of chips, without the markup — so it is a fact under test rather
/// than an expression repeated at every site that draws chips.
/// </summary>
internal static class ChipSelection
{
    /// <summary>
    /// The selection after <paramref name="item"/> is pressed in a group that allows several.
    /// </summary>
    /// <remarks>
    /// Removed where it was, or appended at the end — the order the rest keep is the order they were chosen
    /// in, which is what a list such as a person's roles is written back as.
    /// </remarks>
    /// <typeparam name="TValue">What a chip stands for.</typeparam>
    /// <param name="selected">What is chosen now.</param>
    /// <param name="item">The chip that was pressed.</param>
    /// <returns>A new list; <paramref name="selected"/> is not changed.</returns>
    public static IReadOnlyList<TValue> Toggle<TValue>(IReadOnlyCollection<TValue> selected, TValue item)
    {
        ArgumentNullException.ThrowIfNull(selected);

        var comparer = EqualityComparer<TValue>.Default;
        return selected.Contains(item, comparer)
            ? [.. selected.Where(chosen => !comparer.Equals(chosen, item))]
            : [.. selected, item];
    }

    /// <summary>
    /// The chip an arrow, Home or End key moves to in a group that allows one, or <see langword="null"/>
    /// for any other key.
    /// </summary>
    /// <remarks>
    /// The WAI-ARIA radio group: the arrows wrap and choose as they move, in both axes because a group of
    /// chips wraps onto several lines, and Home and End reach the ends.
    /// </remarks>
    /// <param name="count">How many chips the group has.</param>
    /// <param name="at">The index of the chip that has focus.</param>
    /// <param name="key">The key, as the browser names it.</param>
    /// <returns>The index to move to.</returns>
    public static int? Move(int count, int at, string key)
    {
        if (count == 0)
        {
            return null;
        }

        return key switch
        {
            "ArrowRight" or "ArrowDown" => (at + 1) % count,
            "ArrowLeft" or "ArrowUp" => (at - 1 + count) % count,
            "Home" => 0,
            "End" => count - 1,
            _ => null,
        };
    }
}
