namespace MMLib.Alvo.Admin.Components.DesignSystem;

/// <summary>
/// The one sentence under every multi-line box that submits (spec §3.4): Ctrl/Cmd+Enter does what the form's primary
/// action does, and Enter is a new line.
/// </summary>
/// <remarks>
/// The chord itself is alvo.js's, on a <c>form[data-alvo-chord-submit]</c>, for every such box: the assistant, the
/// import, a rule and an editor's text. Written once here so the hint says it the same way on every screen, as the
/// assistant's always did.
/// </remarks>
internal static class ChordHint
{
    /// <summary>The chord, as the hint names it on either keyboard.</summary>
    public const string Chord = "Ctrl+Enter or ⌘+Enter";

    /// <summary>The hint for a box whose chord <paramref name="does"/>: "Ctrl+Enter or ⌘+Enter sends."</summary>
    /// <param name="does">What the chord does, as a verb phrase: "sends", "saves the rule".</param>
    /// <returns>The sentence, and that Enter is a new line.</returns>
    public static string Of(string does) => $"{Chord} {does}. Enter is a new line.";
}
