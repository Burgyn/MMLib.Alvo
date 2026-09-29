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
/// One marker makes the reply Czech, since a Slovak reply with one Czech word is the defect.
/// </para>
/// <para>Code spans and fences are identifiers, not language, so they are taken out first.</para>
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

    private static readonly HashSet<string> _english = new(StringComparer.OrdinalIgnoreCase)
    {
        "the", "is", "and", "of", "it", "for", "can", "will", "this", "that", "are", "not", "with", "you", "your", "now",
        "be", "until", "once", "from", "nothing", "there",
    };

    private static readonly HashSet<string> _slovak = new(StringComparer.OrdinalIgnoreCase)
    {
        "je", "sa", "na", "v", "pre", "sú", "nie", "ako", "že", "bude", "môže", "alebo", "ktorý", "ktorá", "ktoré",
        "pri", "po", "aby", "ak", "už", "iba", "som", "ho", "ju", "kým", "nič", "teraz", "preto",
    };

    private static readonly HashSet<string> _czechWords = new(StringComparer.OrdinalIgnoreCase)
    {
        "se", "pro", "jsem", "jsi", "jsou", "jste", "pojďme", "který", "která", "které", "nebo", "protože",
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
        var slovak = words.Count(_slovak.Contains);
        return english == slovak ? Unknown : english > slovak ? English : Slovak;
    }

    /// <summary>The turn's own prose is in <paramref name="asked"/>, the language the question was asked in.</summary>
    internal static Verdict Judge(TurnRecord turn, string asked)
    {
        var said = Of(ReplyText.OwnProse(turn.Answer, turn.FrameworkTexts));
        return Verdict.When(said == asked, $"language={said} asked={asked}");
    }

    private static bool IsCzech(string word) => word.AsSpan().ContainsAny(_czechLetters) || _czechWords.Contains(word);

    [GeneratedRegex(@"\p{L}+", RegexOptions.CultureInvariant)]
    private static partial Regex Word();

    [GeneratedRegex("```.*?```", RegexOptions.Singleline | RegexOptions.CultureInvariant)]
    private static partial Regex Fence();

    [GeneratedRegex("`[^`]*`", RegexOptions.CultureInvariant)]
    private static partial Regex CodeSpan();
}
