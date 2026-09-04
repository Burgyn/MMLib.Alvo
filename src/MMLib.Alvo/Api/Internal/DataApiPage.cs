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
    /// <summary>The rows in this page, each already masked and projected by the port.</summary>
    [JsonPropertyName("items")]
    public required IReadOnlyList<IReadOnlyDictionary<string, object?>> Items { get; init; }

    /// <summary>
    /// The opaque cursor that reads the page after this one, or <see langword="null"/> when this page is
    /// the last. Only the provider that issued it may interpret it, so it is echoed and never parsed here.
    /// </summary>
    [JsonPropertyName("next")]
    public string? Next { get; init; }

    /// <summary>
    /// How many rows the query matches in total, or <see langword="null"/> when the request sent no
    /// <c>count</c> preference this server recognises. Never the size of this page.
    /// </summary>
    /// <remarks>
    /// <b>Always present, and <see langword="null"/> when unasked</b> — the same rule <see cref="Next"/>
    /// already follows, and for the same reason: the envelope's members are a statement about the bytes, so
    /// all three are <c>required</c> in the published schema. A third member that appeared only sometimes
    /// would be one a client has to probe for.
    /// </remarks>
    [JsonPropertyName("count")]
    public long? Count { get; init; }

    /// <summary>Wraps one page the port returned, rendered to the keys the request asked for.</summary>
    /// <param name="page">The page to render.</param>
    /// <param name="projection">
    /// The response keys and their sources, or <see langword="null"/> for each row as the port returned it.
    /// </param>
    internal static DataApiPage From(AlvoPage page, IReadOnlyList<ProjectedField>? projection)
    {
        ArgumentNullException.ThrowIfNull(page);
        return new DataApiPage
        {
            Items = [.. page.Items.Select(row => Render(row.Values, projection))],
            Next = page.NextCursor,
            Count = page.TotalCount,
        };
    }

    /// <summary>
    /// Renders one row as the response's own key list: each requested key, in the order the request named
    /// it, carrying the value of the field it names.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>This is not the projection — the port applies that</b>, by not reading the excluded columns at all
    /// and by omitting their keys from the record. What is left here is renaming and ordering, and this is
    /// the only layer that can do it: an alias is an HTTP concern the port is deliberately not told about
    /// (see <see cref="ProjectedField"/>).
    /// </para>
    /// <para>
    /// <b>It also drops what the port had to keep, and that is not a second projection.</b>
    /// <c>IAlvoData</c>'s contract makes a returned record carry every framework-managed column whatever the
    /// caller selected — <c>id</c> because a keyset cursor is minted from it — and every field named in
    /// <c>order</c>, because no engine can sort by a column it did not read. The response must show none of
    /// them unless the caller asked. So the port's key set and the response's are two different lists by
    /// construction, and this renders the second from the first.
    /// </para>
    /// <para>
    /// A source the row does not carry emits nothing rather than a <see langword="null"/>: the port omits
    /// nothing a caller may read, so an absent key means the port chose not to return it and this layer must
    /// not manufacture the field back into existence.
    /// </para>
    /// </remarks>
    private static IReadOnlyDictionary<string, object?> Render(
        IReadOnlyDictionary<string, object?> values, IReadOnlyList<ProjectedField>? projection)
    {
        if (projection is null)
        {
            return values;
        }

        var rendered = new Dictionary<string, object?>(projection.Count, StringComparer.Ordinal);
        foreach (var field in projection)
        {
            if (values.TryGetValue(field.Source, out var value))
            {
                rendered[field.Key] = value;
            }
        }

        return rendered;
    }
}
