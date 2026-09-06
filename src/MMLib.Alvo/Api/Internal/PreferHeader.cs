using Microsoft.Extensions.Primitives;

namespace MMLib.Alvo.Api.Internal;

/// <summary>Which <c>count</c> preference a request asked for, in RFC 7240's own spellings.</summary>
internal enum CountPreference
{
    /// <summary>A real count of the matching rows.</summary>
    Exact,

    /// <summary>The planner's estimate, if the engine has one.</summary>
    Planned,

    /// <summary>Any estimate the server can produce cheaply.</summary>
    Estimated,
}

/// <summary>
/// Reads the RFC 7240 <c>Prefer</c> request header, for the one preference Alvo honours:
/// <c>count=exact|planned|estimated</c> on a list.
/// </summary>
/// <remarks>
/// <para>
/// <b>An unrecognised preference is ignored, and that is a deliberate departure from this API's own
/// "refuse, never ignore" rule.</b> Everywhere in the query string an unknown key or modifier is a 422,
/// because an ignored <c>?oder=name</c> answers with unsorted data and the sender cannot tell.
/// <c>Prefer</c> is different <em>by definition</em>: RFC 7240 §2 says a server that does not recognise or
/// cannot satisfy a preference MUST ignore it, and §3 gives <c>Preference-Applied</c> as the channel a
/// client learns what actually happened. So <c>Prefer: count=exakt</c> yields no count and no
/// <c>Preference-Applied</c>, which is precisely how the standard says that is reported. Inventing a
/// stricter variant of a standard is a defect rather than a shortcut; the detection the house rule protects
/// is present, in the standard's place rather than ours.
/// </para>
/// <para>
/// <b><c>planned</c> and <c>estimated</c> are accepted and degrade to an exact count</b>, because a planner
/// estimate is engine-specific — PostgreSQL has <c>EXPLAIN</c>, SQLite has no equivalent worth the name —
/// and §0 principle 3 makes identical behaviour across engines the contract. Degrading is not silent: the
/// response says <c>Preference-Applied: count=exact</c>, so a caller who asked for an estimate is told they
/// received the real thing. This is the layer that decides that, because <c>Prefer</c> is an HTTP
/// vocabulary; <see cref="MMLib.Alvo.Data.AlvoQuery.IncludeTotalCount"/> models only what a driver can
/// honestly do.
/// </para>
/// <para>
/// The list is scanned rather than split naively: RFC 7240's <c>word</c> may be a quoted string, and a
/// preference may carry <c>;</c>-delimited parameters this vocabulary never uses but a proxy may add. Both
/// are handled here so that a header carrying something else beside <c>count</c> still has its <c>count</c>
/// read, which a whole-header parse that failed would not.
/// </para>
/// </remarks>
internal static class PreferHeader
{
    /// <summary>The header a preference arrives in.</summary>
    internal const string Name = "Prefer";

    /// <summary>The header the applied preference is reported in (RFC 7240 §3).</summary>
    internal const string AppliedName = "Preference-Applied";

    /// <summary>The one preference token Alvo reads.</summary>
    private const string CountToken = "count";

    /// <summary>
    /// What <paramref name="header"/> asks the count to be, or <see langword="null"/> when it names no
    /// <c>count</c> preference this server recognises.
    /// </summary>
    /// <remarks>
    /// <b>The <em>first</em> <c>count</c> decides, whether or not its value is one this server knows.</b>
    /// RFC 7240 §2 says a preference should not be repeated and that where one is, the first occurrence
    /// applies — so scanning past an unrecognised <c>count=exakt</c> to honour a later <c>count=exact</c>
    /// would let a value appended by an intermediary override the client's own. A repeat whose first
    /// occurrence is unrecognised therefore applies nothing, which is also what a lone <c>count=exakt</c>
    /// does.
    /// </remarks>
    /// <param name="header">Every value of the request's <c>Prefer</c> header, in arrival order.</param>
    internal static CountPreference? Count(StringValues header)
    {
        foreach (var value in header)
        {
            foreach (var preference in Preferences(value))
            {
                if (NamesCount(preference))
                {
                    return Recognised(preference);
                }
            }
        }

        return null;
    }

    /// <summary>
    /// Whether one <c>token[=word]</c> is the <c>count</c> preference at all — read from the token alone, so
    /// that a <c>count</c> carrying a word this server does not know is still <em>the</em> count preference
    /// and still the one the first-occurrence rule applies to.
    /// </summary>
    private static bool NamesCount(ReadOnlySpan<char> preference)
    {
        var separator = preference.IndexOf('=');
        var token = separator < 0 ? preference : preference[..separator];

        return token.Trim().Equals(CountToken, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// What one <c>count</c> preference asks for, or <see langword="null"/> when its word is not one of the
    /// three spellings — a bare <c>count</c> with no word included.
    /// </summary>
    private static CountPreference? Recognised(ReadOnlySpan<char> preference)
    {
        var separator = preference.IndexOf('=');
        if (separator < 0)
        {
            return null;
        }

        return Unquote(preference[(separator + 1)..].Trim()) switch
        {
            var word when word.Equals("exact", StringComparison.OrdinalIgnoreCase) => CountPreference.Exact,
            var word when word.Equals("planned", StringComparison.OrdinalIgnoreCase) => CountPreference.Planned,
            var word when word.Equals("estimated", StringComparison.OrdinalIgnoreCase) => CountPreference.Estimated,
            _ => null,
        };
    }

    /// <summary>RFC 7240's <c>word</c> is a token or a quoted-string; the quotes are not part of the value.</summary>
    private static ReadOnlySpan<char> Unquote(ReadOnlySpan<char> word) =>
        word.Length >= 2 && word[0] == '"' && word[^1] == '"' ? word[1..^1] : word;

    /// <summary>
    /// Splits one header value into its preferences — on top-level commas, with each preference truncated at
    /// its first top-level <c>;</c>, since what follows qualifies a preference rather than names one.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>One scan, and both delimiters are found by it.</b> An earlier revision split the commas with this
    /// quote-tracking loop and then cut the parameters with a plain <c>IndexOf(';')</c>, which is a different
    /// grammar for the same string: <c>count="a;b", count=exact</c> truncated inside the quoted word, left an
    /// unterminated quote, and dropped a <c>count</c> the header really carried. A separator is either
    /// structural or literal, and only one pass can know which.
    /// </para>
    /// <para>
    /// RFC 7230's <c>quoted-pair</c> is honoured as an <em>escape</em> — a backslash inside a quoted string
    /// makes the next character literal, so <c>\"</c> does not end the string. The escaped character is
    /// deliberately <b>not</b> unescaped in <see cref="Unquote"/>: none of the three words this reads
    /// contains a character that would ever need escaping, so a value carrying one is not one of them
    /// whichever way it is read, and an unescaper here would be code no input can distinguish.
    /// </para>
    /// </remarks>
    private static IEnumerable<string> Preferences(string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            yield break;
        }

        var quoted = false;
        var escaped = false;
        var start = 0;
        var parameters = -1;
        for (var index = 0; index < value.Length; index++)
        {
            var character = value[index];
            if (escaped)
            {
                escaped = false;
            }
            else if (quoted && character == '\\')
            {
                escaped = true;
            }
            else if (character == '"')
            {
                quoted = !quoted;
            }
            else if (quoted)
            {
                continue;
            }
            else if (character == ';' && parameters < 0)
            {
                parameters = index;
            }
            else if (character == ',')
            {
                yield return value[start..(parameters < 0 ? index : parameters)];
                (start, parameters) = (index + 1, -1);
            }
        }

        yield return value[start..(parameters < 0 ? value.Length : parameters)];
    }
}
