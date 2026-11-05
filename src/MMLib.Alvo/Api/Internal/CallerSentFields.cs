using Microsoft.AspNetCore.Http;

namespace MMLib.Alvo.Api.Internal;

/// <summary>
/// The fields a generated write route's body carried, recorded on the request once the body bound, so a CEL refusal can be
/// answered 422 only when it refused values the caller sent (Ruling 44).
/// </summary>
/// <remarks>
/// A feature on the request rather than a parameter threaded through the data port: <c>IAlvoData</c> and the hook ports
/// are public, and whose data a value is matters only to the HTTP answer. <c>new.</c> reaches the stored row on an update
/// and a default on a create or a replace, so only a field the body named is the caller's. A delete carries no body, so
/// it records nothing and never answers 422; a batch records the fields every row carried, because a refusal does not say
/// which row it came from.
/// </remarks>
/// <param name="names">The field names.</param>
internal sealed class CallerSentFields(IReadOnlySet<string> names)
{
    /// <summary>Gets the field names.</summary>
    internal IReadOnlySet<string> Names { get; } = names;

    /// <summary>Records the fields one body carried.</summary>
    /// <param name="http">The request.</param>
    /// <param name="names">The body's field names.</param>
    internal static void Record(HttpContext http, IEnumerable<string> names) =>
        http.Features.Set(new CallerSentFields(names.ToHashSet(StringComparer.Ordinal)));

    /// <summary>Records the fields every row of a batch carried; a batch with no rows records nothing.</summary>
    /// <param name="http">The request.</param>
    /// <param name="rows">The rows' field names.</param>
    internal static void RecordCommon(HttpContext http, IEnumerable<IEnumerable<string>> rows)
    {
        HashSet<string>? common = null;
        foreach (var row in rows)
        {
            common ??= row.ToHashSet(StringComparer.Ordinal);
            common.IntersectWith(row);
        }

        if (common is not null)
        {
            http.Features.Set(new CallerSentFields(common));
        }
    }

    /// <summary>The fields this request's body carried, or <see langword="null"/> when no body was recorded.</summary>
    /// <param name="http">The request.</param>
    /// <returns>The names, or <see langword="null"/>.</returns>
    internal static IReadOnlySet<string>? Of(HttpContext http) => http.Features.Get<CallerSentFields>()?.Names;
}
