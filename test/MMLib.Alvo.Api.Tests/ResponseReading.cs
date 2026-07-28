using System.Text.Json.Nodes;

namespace MMLib.Alvo.Api.Tests;

/// <summary>
/// Reads a Data API response the way a fact wants to assert on it. Kept in one place because several
/// facts turn on the <em>body</em> rather than the status code — "returns no rows of any tenant" is a
/// statement about rows — and each of them must read the body the same way.
/// </summary>
internal static class ResponseReading
{
    /// <summary>The response body as a JSON object, or <see langword="null"/> when it is not one (empty, or an array).</summary>
    /// <param name="response">The response to read.</param>
    internal static async Task<JsonObject?> ReadJsonObjectAsync(this HttpResponseMessage response)
    {
        ArgumentNullException.ThrowIfNull(response);
        var text = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        return string.IsNullOrWhiteSpace(text) ? null : JsonNode.Parse(text) as JsonObject;
    }

    /// <summary>
    /// The <c>items</c> array's rows, or an empty list when the response carries no envelope at all —
    /// which is what a refusal looks like, and is exactly as much "no rows" as an empty <c>items</c>.
    /// </summary>
    /// <param name="response">The response to read.</param>
    internal static async Task<IReadOnlyList<JsonObject>> ReadItemsAsync(this HttpResponseMessage response)
    {
        var body = await response.ReadJsonObjectAsync();
        return body?["items"] is JsonArray items ? [.. items.OfType<JsonObject>()] : [];
    }

    /// <summary>Every row's value for one field, in the order the page returned them.</summary>
    /// <param name="response">The response to read.</param>
    /// <param name="field">The field to project.</param>
    internal static async Task<IReadOnlyList<string?>> ReadFieldAsync(this HttpResponseMessage response, string field)
    {
        var items = await response.ReadItemsAsync();
        return [.. items.Select(item => item[field]?.GetValue<string>())];
    }

    /// <summary>The RFC 7807 <c>detail</c> member, or <see langword="null"/> when the body carries none.</summary>
    /// <param name="response">The response to read.</param>
    internal static async Task<string?> ReadProblemDetailAsync(this HttpResponseMessage response)
    {
        var body = await response.ReadJsonObjectAsync();
        return body?["detail"]?.GetValue<string>();
    }

    /// <summary>The raw body text, for the facts that must assert a value appears <em>nowhere</em> in it.</summary>
    /// <param name="response">The response to read.</param>
    internal static Task<string> ReadTextAsync(this HttpResponseMessage response)
    {
        ArgumentNullException.ThrowIfNull(response);
        return response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
    }
}
