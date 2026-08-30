using MMLib.Alvo.Data;
using System.Text.Json.Serialization;

namespace MMLib.Alvo.Api.Internal;

/// <summary>
/// The wire shape of a list response: a JSON object carrying the rows, the cursor for the page after this
/// one, and the total the caller opted into.
/// </summary>
/// <remarks>
/// <para>
/// <b>An envelope, not a bare array.</b> A caller who received exactly as many rows as they asked for
/// cannot tell, from the rows alone, whether that was the whole visible set — <see cref="AlvoPage"/>'s
/// own remarks make that the reason a page is more than its rows. The alternative designs put the answer
/// in a header (<c>Content-Range</c>, <c>Link</c>), which gives a cursor two homes and forces an agent
/// reading a JSON body to parse HTTP headers to keep paging. It has exactly one home: <see cref="Next"/>.
/// </para>
/// <para>
/// The property names are pinned with <see cref="JsonPropertyNameAttribute"/> rather than left to the
/// host's JSON naming policy. The envelope is a published contract (it is what the OpenAPI document will
/// describe), and a host that configures PascalCase for its own endpoints must not silently rename
/// Alvo's.
/// </para>
/// </remarks>
internal sealed record DataApiPage
{
    /// <summary>The rows in this page, each already masked by the port.</summary>
    [JsonPropertyName("items")]
    public required IReadOnlyList<IReadOnlyDictionary<string, object?>> Items { get; init; }

    /// <summary>
    /// The opaque cursor that reads the page after this one, or <see langword="null"/> when this page is
    /// the last. Only the provider that issued it may interpret it, so it is echoed and never parsed here.
    /// </summary>
    [JsonPropertyName("next")]
    public string? Next { get; init; }

    /// <summary>
    /// How many rows the query matches in total, or <see langword="null"/> when the request did not send
    /// <c>Prefer: count=exact</c>. Never the size of this page.
    /// </summary>
    /// <remarks>
    /// <b>Always present, and <see langword="null"/> when unasked</b> — the same rule <see cref="Next"/>
    /// already follows, and for the same reason: the envelope's members are a statement about the bytes, so
    /// all three are <c>required</c> in the published schema. A third member that appeared only sometimes
    /// would be one a client has to probe for.
    /// </remarks>
    [JsonPropertyName("count")]
    public long? Count { get; init; }

    /// <summary>Wraps one page the port returned, projected to the fields the request selected.</summary>
    /// <param name="page">The page to render.</param>
    /// <param name="select">The fields to keep, or <see langword="null"/> to keep every field the port returned.</param>
    internal static DataApiPage From(AlvoPage page, IReadOnlyList<string>? select)
    {
        ArgumentNullException.ThrowIfNull(page);
        return new DataApiPage
        {
            Items = [.. page.Items.Select(row => Project(row.Values, select))],
            Next = page.NextCursor,
            Count = page.TotalCount,
        };
    }

    /// <summary>
    /// Narrows one row to the requested projection, in the order the request named the fields.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The projection is applied to the response, not pushed into the <c>SELECT</c> list</b> — the port has
    /// no projection member yet, and inventing one no driver honours would be worse than over-fetching. So
    /// this saves bandwidth to the caller and none to the database; <c>ParsedListQuery</c> records why, and
    /// the follow-up that fixes it.
    /// </para>
    /// <para>
    /// A selected field the row does not carry is skipped rather than emitted as <see langword="null"/>: the
    /// port omits nothing a caller may read, so an absent key means the port chose not to return it and this
    /// layer must not manufacture the field back into existence.
    /// </para>
    /// </remarks>
    private static IReadOnlyDictionary<string, object?> Project(
        IReadOnlyDictionary<string, object?> values, IReadOnlyList<string>? select)
    {
        if (select is null)
        {
            return values;
        }

        var projected = new Dictionary<string, object?>(select.Count, StringComparer.Ordinal);
        foreach (var field in select.Where(values.ContainsKey))
        {
            projected[field] = values[field];
        }

        return projected;
    }
}
