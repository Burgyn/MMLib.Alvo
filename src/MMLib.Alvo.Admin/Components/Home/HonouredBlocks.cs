namespace MMLib.Alvo.Admin.Components.Home;

/// <summary>
/// Overview's "Honoured blocks" stat: how many descriptor blocks the build honours.
/// </summary>
/// <remarks>
/// The capability report's honoured list appends host kinds after the blocks (<c>extensions.eventHandler</c> once a
/// handler is registered). A block name never carries a dot and a host kind always does, so the dot is what tells
/// them apart: counting the whole list once made a host with one event handler read six blocks out of five.
/// </remarks>
internal static class HonouredBlocks
{
    /// <summary>The descriptor blocks among <paramref name="honoured"/>, host kinds left out.</summary>
    public static int Count(IReadOnlyList<string> honoured) =>
        honoured.Count(entry => !entry.Contains('.', StringComparison.Ordinal));
}
