namespace MMLib.Alvo.Admin.Tests.DesignSystem;

/// <summary>
/// Reads one Razor tag's attributes the way the Razor compiler delimits them, for the source-scanning guards.
/// </summary>
/// <remarks>
/// <para>
/// <b>A value starting with <c>@</c> is C#</b>, so a quote inside its parentheses opens a string literal rather than
/// closing the attribute: <c>For="@(on ? null : "new-field-default")"</c> is one value, and a regex that only allowed
/// a quote right before <c>)</c> cut it one literal short. Anything else is text, and ends at the next quote.
/// </para>
/// <para>
/// <b>An id is compared by the values it can take</b>, never as a substring: a value's candidates are the value
/// itself, and for C# its expression, every string literal in it and each branch of a top-level conditional. An input
/// <c>id="name"</c> is not named by <c>for="rename"</c>.
/// </para>
/// </remarks>
internal static class RazorTag
{
    /// <summary>Where the tag opened at <paramref name="start"/> ends: the index after its <c>&gt;</c>.</summary>
    /// <param name="source">The file.</param>
    /// <param name="start">The index of its <c>&lt;</c>.</param>
    public static int End(string source, int start)
    {
        var at = start + 1;
        while (at < source.Length && source[at] != '>')
        {
            at = source[at] == '"' ? ValueEnd(source, at) + 1 : at + 1;
        }

        return Math.Min(at + 1, source.Length);
    }

    /// <summary>A tag's attributes by whole name: <c>AdornmentAriaLabel</c> is not <c>Label</c>.</summary>
    /// <param name="tag">The tag's text, from its <c>&lt;</c> to its <c>&gt;</c>.</param>
    public static Dictionary<string, string> Attributes(string tag)
    {
        var attributes = new Dictionary<string, string>(StringComparer.Ordinal);
        var at = tag.IndexOfAny([' ', '\t', '\r', '\n']);
        while (at >= 0 && at < tag.Length)
        {
            var name = ReadName(tag, ref at);
            if (name.Length == 0)
            {
                at++;
                continue;
            }

            attributes.TryAdd(name, ReadValue(tag, ref at));
        }

        return attributes;
    }

    /// <summary>The value of the attribute whose opening quote is at <paramref name="quote"/>.</summary>
    /// <param name="source">The text the value is in.</param>
    /// <param name="quote">The index of its opening quote.</param>
    public static string Value(string source, int quote) => source[(quote + 1)..ValueEnd(source, quote)];

    /// <summary>The ids an attribute value can evaluate to.</summary>
    /// <param name="value">The value as written, <c>@</c> and all.</param>
    public static IReadOnlySet<string> Ids(string value)
    {
        if (!value.StartsWith('@'))
        {
            return value.Length == 0 ? new HashSet<string>() : new HashSet<string>(StringComparer.Ordinal) { value };
        }

        var expression = Unwrap(value[1..].Trim());
        var ids = new HashSet<string>(StringComparer.Ordinal) { expression };
        ids.UnionWith(Literals(expression));
        ids.UnionWith(Branches(expression));
        ids.Remove("null");
        ids.Remove(string.Empty);
        return ids;
    }

    private static string ReadName(string tag, ref int at)
    {
        while (at < tag.Length && char.IsWhiteSpace(tag[at]))
        {
            at++;
        }

        var start = at;
        while (at < tag.Length && (char.IsLetterOrDigit(tag[at]) || tag[at] is '@' or '-' or ':' or '.' or '_'))
        {
            at++;
        }

        return tag[start..at];
    }

    private static string ReadValue(string tag, ref int at)
    {
        while (at < tag.Length && char.IsWhiteSpace(tag[at]))
        {
            at++;
        }

        if (at >= tag.Length || tag[at] != '=')
        {
            return string.Empty;
        }

        at++;
        while (at < tag.Length && char.IsWhiteSpace(tag[at]))
        {
            at++;
        }

        if (at >= tag.Length || tag[at] != '"')
        {
            return string.Empty;
        }

        var value = Value(tag, at);
        at = ValueEnd(tag, at) + 1;
        return value;
    }

    /// <summary>The index of the quote that closes the value opened at <paramref name="quote"/>.</summary>
    private static int ValueEnd(string source, int quote)
    {
        var code = quote + 1 < source.Length && source[quote + 1] == '@';
        var depth = 0;
        for (var at = quote + 1; at < source.Length; at++)
        {
            switch (source[at])
            {
                case '(' when code:
                    depth++;
                    break;
                case ')' when code && depth > 0:
                    depth--;
                    break;
                case '"' when depth > 0:
                    at = LiteralEnd(source, at);
                    break;
                case '"':
                    return at;
            }
        }

        return source.Length;
    }

    /// <summary>The index of the quote that closes the C# string literal opened at <paramref name="quote"/>.</summary>
    private static int LiteralEnd(string source, int quote)
    {
        for (var at = quote + 1; at < source.Length; at++)
        {
            if (source[at] == '\\')
            {
                at++;
            }
            else if (source[at] == '"')
            {
                return at;
            }
        }

        return source.Length;
    }

    private static string Unwrap(string expression)
        => expression.StartsWith('(') && expression.EndsWith(')') ? expression[1..^1].Trim() : expression;

    /// <summary>Every plain string literal in <paramref name="expression"/>; an interpolated one is not an id.</summary>
    private static IEnumerable<string> Literals(string expression)
    {
        for (var at = expression.IndexOf('"', StringComparison.Ordinal); at >= 0;)
        {
            var end = LiteralEnd(expression, at);
            var interpolated = at > 0 && expression[at - 1] == '$';
            if (!interpolated && end < expression.Length)
            {
                yield return expression[(at + 1)..end];
            }

            at = end + 1 < expression.Length ? expression.IndexOf('"', end + 1) : -1;
        }
    }

    /// <summary>The parts of a top-level <c>a ? b : c</c>, outside parentheses and string literals.</summary>
    private static List<string> Branches(string expression)
    {
        var parts = new List<string>();
        var (depth, start) = (0, 0);
        for (var at = 0; at < expression.Length; at++)
        {
            switch (expression[at])
            {
                case '"':
                    at = LiteralEnd(expression, at);
                    break;
                case '(' or '[' or '{':
                    depth++;
                    break;
                case ')' or ']' or '}':
                    depth--;
                    break;
                case '?' or ':' when depth == 0:
                    parts.Add(expression[start..at].Trim());
                    start = at + 1;
                    break;
            }
        }

        parts.Add(expression[start..].Trim());
        return parts.Count > 1 ? parts : [];
    }
}
