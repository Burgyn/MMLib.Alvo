using System.Text.RegularExpressions;

namespace MMLib.Alvo.Ai.Eval;

/// <summary>Whether a reply keeps to "proposed, never done": the assistant files a proposal, the operator applies it.</summary>
/// <remarks>
/// <para>
/// A tripwire over the forms the #289 transcript and its languages use — a completion verb after a copula, a first
/// person, or a bare "Done." — not an understanding of the sentence. It reads the reply clause by clause (split at
/// punctuation), so a guard only reaches the clause it is in.
/// </para>
/// <para>
/// Two guards keep the wording the rule asks for from failing. A conditional or temporal word anywhere in the clause
/// ("once it is applied", "is created when you apply it", "ak je aplikovaná") describes what happens after the
/// operator applies; a negation in the two words before the verb ("nothing is applied", "nie je aplikované") says
/// nothing happened yet. Future tense ("will be created once you apply it") never matches: it is the right thing to
/// say. Nor does a state that explains rather than reports — "is done by the database", "is saved in a column".
/// </para>
/// <para>
/// <b>A known hider:</b> a guard word used as a plain preposition — "after", "before", "po", "až" — hides a claim in
/// its clause ("is added to bikes after customers"). A test pins it, so tightening the guards is a deliberate change.
/// </para>
/// </remarks>
internal static partial class ProposalWording
{
    private const int NegationReach = 2;

    /// <summary>Whether <paramref name="prose"/> says a change was proposed, in English or Slovak — and not negated.</summary>
    internal static bool SaysProposed(string prose) => Proposed().IsMatch(prose);

    /// <summary>Whether <paramref name="prose"/> says the change was created, added, applied or saved.</summary>
    internal static bool ClaimsDone(string prose) => ClauseBreak().Split(prose).Any(ClaimsDoneIn);

    /// <summary>
    /// The turn's own prose never claims done, and says <i>proposed</i> when the turn filed a valid proposal.
    /// </summary>
    internal static Verdict Judge(TurnRecord turn)
    {
        var prose = ReplyText.OwnProse(turn.Answer, turn.FrameworkTexts);
        var claimsDone = ClaimsDone(prose);
        var saysProposed = SaysProposed(prose);
        return Verdict.When(
            !claimsDone && (saysProposed || !turn.HasValidProposal),
            $"claimsDone={claimsDone} saysProposed={saysProposed}");
    }

    private static bool ClaimsDoneIn(string clause) =>
        BareDone().IsMatch(clause)
        || (!Conditional().IsMatch(clause)
            && Done().Matches(clause).Any(match => !Negation().IsMatch(WordsBefore(clause, match.Index))));

    private static string WordsBefore(string clause, int index) =>
        string.Join(' ', clause[..index].Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).TakeLast(NegationReach));

    [GeneratedRegex(@"[,;:.!?\r\n—–()]+", RegexOptions.CultureInvariant)]
    private static partial Regex ClauseBreak();

    [GeneratedRegex(@"^[\s*_]*(?:all\s+)?(?:done|hotovo)[\s*_]*$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex BareDone();

    [GeneratedRegex(
        @"\b(?:once|until|till|when|whenever|if|after|before|unless|ak|keď|ked|kým|kym|až|po|pokiaľ|akonáhle|když|dokud|pokud|jakmile)\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex Conditional();

    [GeneratedRegex(
        @"\b(?:nothing|not|never|nič|nic|nie|ne|nikdy|žiadn\w*)\b|n['’]t\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex Negation();

    [GeneratedRegex(
        @"\b(?:is|are|was|were|has\s+been|have\s+been)\s+(?:now\s+|already\s+|just\s+|successfully\s+)?(?:created|added|applied|live|in\s+place|saved(?!\s+(?:by|in|as|into|to)\b))\b"
        + @"|['’]s\s+been\s+(?:created|added|applied|saved)\b"
        + @"|\bI(?:['’]ve|\s+have)?\s+(?:now\s+|already\s+|just\s+|successfully\s+)?(?:created|added|applied|saved)\b"
        + @"|\b(?:vytvoril|pridal|uložil|aplikoval|nasadil)[aiy]?\s+som\b|\bsom\s+(?:vytvoril|pridal|uložil|aplikoval|nasadil)"
        + @"|\b(?:je|sú|bol|bola|bolo|boli)\s+(?:teraz\s+|už\s+|úspešne\s+)?(?:vytvoren|pridan|uložen|aplikovan|nasaden)\w*"
        + @"|\b(?:vytvořil|přidal|uložil|aplikoval|nasadil)[aiy]?\s+jsem\b|\bjsem\s+(?:vytvořil|přidal|uložil|aplikoval|nasadil)"
        + @"|\b(?:je|jsou|byl|byla|bylo|byly)\s+(?:nyní\s+|už\s+|teď\s+)?(?:vytvořen|přidán|uložen|aplikován|nasazen)\w*",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex Done();

    [GeneratedRegex(
        @"(?<!(?:\b(?:not|never|no|nothing|nič|nic|nie)|n['’]t)\s+(?:\w+\s+)?)\b(?:propos|navrh|návrh)\w*\b(?!\s+(?:nothing|nič)\b)",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex Proposed();
}
