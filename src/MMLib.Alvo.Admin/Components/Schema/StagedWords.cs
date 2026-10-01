namespace MMLib.Alvo.Admin.Components.Schema;

/// <summary>
/// The one confirmation a staged schema change says, whatever it staged (batch-B re-review N6, the snackbar half of
/// spec §3.1's submit wording): "Field notes added to the working copy", "Index on status, priority added…".
/// </summary>
internal static class StagedWords
{
    /// <summary>What a create staged: "<paramref name="kind"/> <paramref name="name"/> added to the working copy".</summary>
    /// <param name="kind">What it is, capitalised: Entity, Field, Index, Hook.</param>
    /// <param name="name">What names it.</param>
    /// <returns>The sentence.</returns>
    public static string Added(string kind, string name) => $"{kind} {name} added to the working copy";

    /// <summary>What an edit staged: "<paramref name="kind"/> <paramref name="name"/> saved to the working copy".</summary>
    /// <param name="kind">What it is, capitalised.</param>
    /// <param name="name">What names it.</param>
    /// <returns>The sentence.</returns>
    public static string Saved(string kind, string name) => $"{kind} {name} saved to the working copy";

    /// <summary>What a removal staged: "<paramref name="kind"/> <paramref name="name"/> removed from the working copy".</summary>
    /// <param name="kind">What it is, capitalised.</param>
    /// <param name="name">What names it.</param>
    /// <returns>The sentence.</returns>
    public static string Removed(string kind, string name) => $"{kind} {name} removed from the working copy";
}
