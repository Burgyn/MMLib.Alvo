namespace MMLib.Alvo.Admin.Components.DesignSystem;

/// <summary>
/// The item a list just gained, cascaded to the list that draws it so it is scrolled to and lit (spec §3.5): the
/// <c>data-alvo-new</c> wash every created item gets.
/// </summary>
/// <remarks>
/// A cascade rather than a parameter, so a list the page composes (the Fields tab under the entity screen) learns what
/// arrived without a public parameter that names it; internal, like <see cref="RevealOnRender"/>.
/// </remarks>
/// <param name="Key">What names the item in its list: a field's name.</param>
/// <param name="Attempt">A count the owner raises per new item, which keys the scroll so each one is revealed.</param>
internal sealed record Arrival(string Key, int Attempt)
{
    /// <summary>No item has arrived.</summary>
    public static Arrival None { get; } = new(string.Empty, 0);

    /// <summary>The wash attribute's value for the item named <paramref name="key"/>: set on the arrival only.</summary>
    /// <param name="key">The row's item.</param>
    /// <returns><c>"true"</c> on the item that arrived; <see langword="null"/>, which draws no attribute, elsewhere.</returns>
    public string? Wash(string key) => Attempt > 0 && string.Equals(key, Key, StringComparison.Ordinal) ? "true" : null;
}
