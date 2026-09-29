using System.Buffers;
using System.Text.RegularExpressions;

namespace MMLib.Alvo.Ai.Eval;

/// <summary>Which language a reply's own prose is in — English, Slovak, or Czech where Slovak was asked (D17).</summary>
/// <remarks>
/// <para>
/// Stop words decide English against Slovak; words the two share (<c>a</c>, <c>to</c>, <c>do</c>, <c>so</c>) are in
/// neither list. <b>Czech is decided by markers alone</b>, because it is the confusion the #289 transcript shows and
/// the one a count cannot see: the letters <c>ě ř ů</c>, which Slovak does not have, and the function words Slovak
/// spells differently (<c>se</c>/<c>sa</c>, <c>pro</c>/<c>pre</c>, <c>jsem</c>/<c>som</c>, <c>pojďme</c>/<c>poďme</c>).
/// One marker makes the reply Czech, since a Slovak reply with one Czech word is the defect. The marker words include the
/// diacritic-free Czech forms Slovak spells otherwise (<c>bylo</c>/<c>bolo</c>, <c>tedy</c>/<c>teda</c>,
/// <c>jak</c>/<c>ako</c>), so a Czech reply without <c>ě ř ů</c> is still found — and never a word Slovak also has
/// (<c>také</c> is Slovak for "such").
/// </para>
/// <para>
/// A word with a letter only Slovak has (<c>ä ô ľ ĺ ŕ</c>) counts as Slovak, so a short Slovak reply of content words
/// ("Nemôžem …") is not <c>unknown</c>. Code spans and fences are identifiers, not language, so they are taken out
/// first; a word is a run of letters and digits, hyphens inside it included, so <c>v1</c> or <c>pre-existing</c> is
/// one word and never the Slovak <c>v</c> or <c>pre</c>.
/// </para>
/// <para>
/// <b>Known limits.</b> A proper name spelled with a Czech-only letter (<c>Škůdce</c>) makes a Slovak reply Czech. An
/// English refusal the model paraphrases inline — not a quote-block line, not the framework's text verbatim, which
/// <see cref="ReplyText.OwnProse"/> removes — counts toward English and can tip a short Slovak reply.
/// </para>
/// </remarks>
internal static partial class ReplyLanguage
{
    /// <summary>English, as the eval asks it and as the grader reports it.</summary>
    internal const string English = "en";

    /// <summary>Slovak, as the eval asks it and as the grader reports it.</summary>
    internal const string Slovak = "sk";

    /// <summary>Czech: never asked, only ever found in a reply that should have been Slovak.</summary>
    internal const string Czech = "cs";

    /// <summary>A reply with no prose of its own to judge — which fails, since the rule requires an explanation.</summary>
    internal const string Unknown = "unknown";

    private static readonly SearchValues<char> _czechLetters = SearchValues.Create("ěřůĚŘŮ");

    private static readonly SearchValues<char> _slovakLetters = SearchValues.Create("äôľĺŕÄÔĽĹŔ");

    private static readonly HashSet<string> _english = new(StringComparer.OrdinalIgnoreCase)
    {
        "the", "is", "and", "of", "it", "for", "can", "will", "this", "that", "are", "not", "with", "you", "your", "now",
        "be", "until", "once", "from", "nothing", "there", "in", "i", "as", "but", "was", "has", "have", "its", "or",
        "an", "then", "proposed", "propose", "add", "field", "fields", "apply", "applied",
    };

    private static readonly HashSet<string> _slovak = new(StringComparer.OrdinalIgnoreCase)
    {
        "je", "sa", "na", "v", "pre", "sú", "nie", "ako", "že", "bude", "môže", "alebo", "ktorý", "ktorá", "ktoré",
        "pri", "po", "aby", "ak", "už", "iba", "som", "ho", "ju", "kým", "nič", "teraz", "preto", "lebo", "pretože",
        "ale", "aj", "si", "len", "bol", "bola", "bolo", "boli", "nemôžem", "tak", "teda", "keď", "ešte", "však", "sme",
        "ste", "tiež", "ktorú", "ani", "rozumiem", "pridám", "navrhol", "navrhla", "navrhujem",
    };

    private static readonly HashSet<string> _czechWords = new(StringComparer.OrdinalIgnoreCase)
    {
        "se", "pro", "jsem", "jsi", "jsou", "jste", "pojďme", "který", "která", "které", "nebo", "protože", "byl",
        "byla", "bylo", "byly", "taky", "když", "jestli", "tedy", "jsme", "jak", "co",
    };

    /// <summary>The language of <paramref name="prose"/>, with its code spans and fences left out.</summary>
    internal static string Of(string prose)
    {
        var words = Word().Matches(CodeSpan().Replace(Fence().Replace(prose, " "), " ")).Select(match => match.Value).ToList();
        if (words.Any(IsCzech))
        {
            return Czech;
        }

        var english = words.Count(_english.Contains);
        var slovak = words.Count(IsSlovak);
        return english == slovak ? Unknown : english > slovak ? English : Slovak;
    }

    /// <summary>The turn's own prose is in <paramref name="asked"/>, the language the question was asked in.</summary>
    internal static Verdict Judge(TurnRecord turn, string asked)
    {
        var said = Of(ReplyText.OwnProse(turn.Answer, turn.FrameworkTexts));
        return Verdict.When(said == asked, $"language={said} asked={asked}");
    }

    private static bool IsCzech(string word) => word.AsSpan().ContainsAny(_czechLetters) || _czechWords.Contains(word);

    private static bool IsSlovak(string word) => _slovak.Contains(word) || word.AsSpan().ContainsAny(_slovakLetters);

    [GeneratedRegex(@"[\p{L}\p{N}]+(?:-[\p{L}\p{N}]+)*", RegexOptions.CultureInvariant)]
    private static partial Regex Word();

    [GeneratedRegex("```.*?```", RegexOptions.Singleline | RegexOptions.CultureInvariant)]
    private static partial Regex Fence();

    [GeneratedRegex("`[^`]*`", RegexOptions.CultureInvariant)]
    private static partial Regex CodeSpan();
}
