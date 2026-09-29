namespace MMLib.Alvo.Admin.Components.Schema.Map;

/// <summary>Which entity the Schema map centres on, once there are too many to draw whole.</summary>
/// <remarks>
/// Pulled out of <c>SchemaList</c> so the past-the-limit path — which no example descriptor reaches — is a
/// unit test rather than a branch only a thirteen-entity project would ever run.
/// </remarks>
internal static class MapCentre
{
    /// <summary>How many entities the Schema map draws whole; past this it draws one and its neighbours.</summary>
    public const int WholeGraphLimit = 12;

    /// <summary>
    /// Null while the map draws whole; past <paramref name="limit"/>, the requested centre when the copy declares
    /// it, else the first entity.
    /// </summary>
    /// <remarks>
    /// A centre the copy no longer declares (a link sent before a rename) falls back to the first entity rather
    /// than drawing nothing.
    /// </remarks>
    /// <param name="entities">The working copy's entities, in descriptor order.</param>
    /// <param name="requested">The address's <c>centre</c>, or null.</param>
    /// <param name="limit">How many entities are drawn whole.</param>
    public static string? Choose(IReadOnlyList<string> entities, string? requested, int limit = WholeGraphLimit)
    {
        if (entities.Count <= limit)
        {
            return null;
        }

        return requested is not null && entities.Contains(requested, StringComparer.Ordinal) ? requested : entities[0];
    }
}
