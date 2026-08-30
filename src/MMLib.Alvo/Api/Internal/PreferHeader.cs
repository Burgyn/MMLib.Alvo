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
    /// The <b>first</b> recognised <c>count</c> wins when the header repeats it. A repeat is a malformed
    /// request rather than a choice, and RFC 7240 §2 says the first occurrence of a repeated preference is
    /// the one that applies — answering with the last would let a value appended by an intermediary override
    /// the client's own.
    /// </remarks>
    /// <param name="header">Every value of the request's <c>Prefer</c> header, in arrival order.</param>
    internal static CountPreference? Count(StringValues header)
    {
        foreach (var value in header)
        {
            foreach (var preference in Preferences(value))
            {
                if (Recognised(preference) is { } count)
                {
                    return count;
                }
            }
        }

        return null;
    }

    /// <summary>
    /// The <c>count</c> preference one <c>token[=word]</c> names, or <see langword="null"/> for anything
    /// else — another preference entirely, or a <c>count</c> whose value is not one of the three spellings.
    /// </summary>
    private static CountPreference? Recognised(ReadOnlySpan<char> preference)
    {
        var separator = preference.IndexOf('=');
        if (separator < 0)
        {
            return null;
        }

        if (!preference[..separator].Trim().Equals(CountToken, StringComparison.OrdinalIgnoreCase))
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
    /// Splits one header value into its preferences: on top-level commas, with anything inside a
    /// quoted-string kept whole, and each preference's <c>;</c>-delimited parameters dropped — they qualify
    /// a preference rather than name one.
    /// </summary>
    private static IEnumerable<string> Preferences(string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            yield break;
        }

        var quoted = false;
        var start = 0;
        for (var index = 0; index < value.Length; index++)
        {
            if (value[index] == '"')
            {
                quoted = !quoted;
            }
            else if (value[index] == ',' && !quoted)
            {
                yield return WithoutParameters(value[start..index]);
                start = index + 1;
            }
        }

        yield return WithoutParameters(value[start..]);
    }

    private static string WithoutParameters(string preference)
    {
        var parameters = preference.IndexOf(';', StringComparison.Ordinal);
        return parameters < 0 ? preference : preference[..parameters];
    }
}
