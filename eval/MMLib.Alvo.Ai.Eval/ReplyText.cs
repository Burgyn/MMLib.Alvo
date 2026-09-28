using System.Text;

namespace MMLib.Alvo.Ai.Eval;

/// <summary>What a grader reads of a reply or an expression, once what it must not grade is taken out.</summary>
internal static class ReplyText
{
    private const char QuoteMarker = '>';
    private const char SingleQuote = '\'';
    private const char DoubleQuote = '"';
    private static readonly char[] _sentenceEnds = ['.', '!', '?'];

    /// <summary>
    /// The model's own words: the reply without its quote-block lines and without any framework text it repeated.
    /// </summary>
    /// <remarks>
    /// The instructions tell the model to quote a refusal and its fix verbatim before explaining it, and a refusal's fix
    /// can itself say what the model is asked to say (<i>"Say first what data it loses"</i>). A grader that read the quote
    /// would grade the framework, not the model.
    /// </remarks>
    /// <param name="answer">The turn's reply.</param>
    /// <param name="frameworkTexts">Every message and fix the dry runs returned.</param>
    internal static string OwnProse(string answer, IEnumerable<string> frameworkTexts)
    {
        var unquoted = string.Join('\n', answer.Split('\n').Where(line => !line.TrimStart().StartsWith(QuoteMarker)));
        return frameworkTexts
            .OrderByDescending(text => text.Length)
            .Aggregate(unquoted, (prose, text) => prose.Replace(text, string.Empty, StringComparison.Ordinal));
    }

    /// <summary>The first sentence of <paramref name="prose"/>: up to the first sentence end or line break.</summary>
    /// <param name="prose">Prose, already stripped of what is not the model's.</param>
    internal static string FirstSentence(string prose)
    {
        var text = prose.Trim();
        for (var index = 0; index < text.Length; index++)
        {
            if (text[index] == '\n' || (_sentenceEnds.Contains(text[index]) && (index + 1 == text.Length || char.IsWhiteSpace(text[index + 1]))))
            {
                return text[..(index + 1)].Trim();
            }
        }

        return text;
    }

    /// <summary>
    /// A CEL expression with the whitespace between its tokens removed, and the text inside its string literals kept.
    /// </summary>
    /// <remarks>
    /// The graders compare the shape of an expression — <c>has( middle_name )</c> is <c>has(middle_name)</c> — not its
    /// spacing, and the space inside <c>' '</c> is a value, not layout.
    /// </remarks>
    /// <param name="expression">The expression as the model wrote it.</param>
    internal static string Compact(string expression)
    {
        var compact = new StringBuilder(expression.Length);
        char? literal = null;
        foreach (var character in expression)
        {
            literal = LiteralAfter(literal, character);
            if (literal is not null || !char.IsWhiteSpace(character))
            {
                compact.Append(character);
            }
        }

        return compact.ToString();
    }

    private static char? LiteralAfter(char? literal, char character) => literal switch
    {
        null when character is SingleQuote or DoubleQuote => character,
        { } open when character == open => null,
        _ => literal,
    };
}
