using MMLib.Alvo.Data;
using System.Text.Json.Serialization;

namespace MMLib.Alvo.Api.Internal;

/// <summary>The body a batch answers with: the rows it wrote, and how many it affected.</summary>
/// <remarks>
/// <para>
/// A record rather than an anonymous object, for the reason <see cref="DataApiPage"/> is one: the member
/// names are a wire contract, so they are pinned with <see cref="JsonPropertyNameAttribute"/> rather than
/// left to whatever naming policy is in effect.
/// </para>
/// <para>
/// <b><see cref="Affected"/> is required and is what a delete is read from.</b> A batch delete produces no
/// rows, so <see cref="Items"/> is empty on it — a caller checking only the rows could not tell a five-row
/// delete from a refusal, which is the same failure <see cref="AlvoBatchResult.Affected"/> exists to close
/// one layer down.
/// </para>
/// </remarks>
internal sealed record DataApiBatch
{
    /// <summary>The rows the batch wrote, in the order the caller sent them.</summary>
    [JsonPropertyName("items")]
    public required IReadOnlyList<IReadOnlyDictionary<string, object?>> Items { get; init; }

    /// <summary>How many rows the batch wrote or removed.</summary>
    [JsonPropertyName("affected")]
    public required int Affected { get; init; }

    /// <summary>Renders what the port produced.</summary>
    /// <param name="result">The batch result.</param>
    internal static DataApiBatch From(AlvoBatchResult result)
    {
        ArgumentNullException.ThrowIfNull(result);

        return new DataApiBatch
        {
            Items = [.. result.Rows.Select(row => row.Values)],
            Affected = result.Affected,
        };
    }
}
