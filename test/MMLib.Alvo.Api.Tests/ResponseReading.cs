using System.Net;
using System.Text.Json.Nodes;

namespace MMLib.Alvo.Api.Tests;

/// <summary>
/// Reads a Data API response the way a fact wants to assert on it — and <b>refuses to read one it does
/// not understand</b>.
/// </summary>
/// <remarks>
/// The refusal is the whole point. An earlier version of <see cref="ReadItemsAsync"/> returned an empty
/// list for a body with no envelope, so a fact asserting "no rows came back" passed on <em>any</em>
/// refusal — including a 401 nobody had noticed the request was earning. A helper that answers quietly
/// for output it cannot parse is a vacuity generator for every fact written after it, so each reader here
/// states the shape it requires and throws, naming the status and the body, when it does not get it.
/// </remarks>
internal static class ResponseReading
{
    /// <summary>The response body as a JSON object; throws when the body is absent or is not an object.</summary>
    /// <param name="response">The response to read.</param>
    internal static async Task<JsonObject> ReadJsonObjectAsync(this HttpResponseMessage response)
    {
        ArgumentNullException.ThrowIfNull(response);
        var text = await response.ReadTextAsync();
        return Parse(text) as JsonObject ?? throw Unexpected(response, text, "a JSON object");
    }

    /// <summary>
    /// The rows of a successful list response. Requires a 200 carrying an <c>items</c> array — a fact
    /// about rows must not be satisfiable by a response that carried none because it was refused.
    /// </summary>
    /// <param name="response">The response to read.</param>
    internal static async Task<IReadOnlyList<JsonObject>> ReadItemsAsync(this HttpResponseMessage response)
    {
        ArgumentNullException.ThrowIfNull(response);
        var text = await response.ReadTextAsync();
        if (response.StatusCode != HttpStatusCode.OK)
        {
            throw Unexpected(response, text, "a 200 carrying a page envelope");
        }

        return Parse(text) is JsonObject body && body["items"] is JsonArray items
            ? [.. items.OfType<JsonObject>()]
            : throw Unexpected(response, text, "an { items, next } envelope");
    }

    /// <summary>Every row's value for one field, in the order the page returned them.</summary>
    /// <param name="response">The response to read.</param>
    /// <param name="field">The field to project.</param>
    internal static async Task<IReadOnlyList<string?>> ReadFieldAsync(this HttpResponseMessage response, string field)
    {
        var items = await response.ReadItemsAsync();
        return [.. items.Select(item => item[field]?.GetValue<string>())];
    }

    /// <summary>
    /// The RFC 7807 <c>detail</c> of a refusal. Requires a failed status and a body carrying
    /// <c>detail</c>, so a fact asserting <em>why</em> a request was refused cannot pass against a success.
    /// </summary>
    /// <param name="response">The response to read.</param>
    internal static async Task<string> ReadProblemDetailAsync(this HttpResponseMessage response)
    {
        ArgumentNullException.ThrowIfNull(response);
        var text = await response.ReadTextAsync();
        if (response.IsSuccessStatusCode)
        {
            throw Unexpected(response, text, "a refusal carrying a problem document");
        }

        return (Parse(text) as JsonObject)?["detail"]?.GetValue<string>()
            ?? throw Unexpected(response, text, "a problem document with a 'detail' member");
    }

    /// <summary>The raw body text, for the facts that must assert a value appears <em>nowhere</em> in it.</summary>
    /// <param name="response">The response to read.</param>
    internal static Task<string> ReadTextAsync(this HttpResponseMessage response)
    {
        ArgumentNullException.ThrowIfNull(response);
        return response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
    }

    private static JsonNode? Parse(string text) =>
        string.IsNullOrWhiteSpace(text) ? null : JsonNode.Parse(text);

    private static InvalidOperationException Unexpected(
        HttpResponseMessage response, string body, string expected) =>
        new($"Expected {expected}, but the response was {(int)response.StatusCode} with body: {body}");
}
