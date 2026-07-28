using MMLib.Alvo.Data;
using System.Text.Json.Serialization;

namespace MMLib.Alvo.Api.Internal;

/// <summary>
/// The wire shape of a list response: a JSON object carrying the rows and the cursor for the page after
/// this one.
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

    /// <summary>Wraps one page the port returned.</summary>
    /// <param name="page">The page to render.</param>
    internal static DataApiPage From(AlvoPage page)
    {
        ArgumentNullException.ThrowIfNull(page);
        return new DataApiPage
        {
            Items = [.. page.Items.Select(row => row.Values)],
            Next = page.NextCursor,
        };
    }
}
