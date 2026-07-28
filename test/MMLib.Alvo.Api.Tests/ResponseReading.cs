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

    /// <summary>
    /// The RFC 9457 <c>type</c> of a refusal, as the slug alone. Requires a failed status and a
    /// <c>type</c> under Alvo's own namespace, so a fact about <em>which kind</em> of refusal this is cannot
    /// pass against a success or against the framework's default status-code URI.
    /// </summary>
    /// <param name="response">The response to read.</param>
    internal static async Task<string> ReadProblemTypeAsync(this HttpResponseMessage response)
    {
        ArgumentNullException.ThrowIfNull(response);
        var text = await response.ReadTextAsync();
        if (response.IsSuccessStatusCode)
        {
            throw Unexpected(response, text, "a refusal carrying a problem document");
        }

        var type = (Parse(text) as JsonObject)?["type"]?.GetValue<string>()
            ?? throw Unexpected(response, text, "a problem document with a 'type' member");

        return type.StartsWith(AlvoProblemTypes.BaseUri, StringComparison.Ordinal)
            ? type[AlvoProblemTypes.BaseUri.Length..]
            : throw Unexpected(response, text, $"a 'type' under {AlvoProblemTypes.BaseUri}");
    }

    /// <summary>
    /// The <c>violations</c> array of a refusal, as (pointer, code) pairs. Requires a failed status and a
    /// non-empty array: a refusal with no violation gives an agent nothing to act on, so a fact asserting
    /// <em>which</em> violations came back must not be satisfiable by a response carrying none.
    /// </summary>
    /// <param name="response">The response to read.</param>
    internal static async Task<IReadOnlyList<(string Pointer, string Code)>> ReadViolationsAsync(
        this HttpResponseMessage response)
    {
        ArgumentNullException.ThrowIfNull(response);
        var text = await response.ReadTextAsync();
        if (response.IsSuccessStatusCode)
        {
            throw Unexpected(response, text, "a refusal carrying a problem document");
        }

        if (Parse(text) is not JsonObject body || body["violations"] is not JsonArray violations)
        {
            throw Unexpected(response, text, "a problem document with a 'violations' array");
        }

        return violations.Count == 0
            ? throw Unexpected(response, text, "at least one violation")
            : [.. violations.OfType<JsonObject>().Select(violation =>
                (violation["pointer"]!.GetValue<string>(), violation["code"]!.GetValue<string>()))];
    }

    /// <summary>Every <c>fixSuggestion</c> the refusal's violations carry, so a fact can hold every one to §0 principle 4.</summary>
    /// <param name="response">The response to read.</param>
    internal static async Task<IReadOnlyList<string?>> ReadFixSuggestionsAsync(this HttpResponseMessage response)
    {
        ArgumentNullException.ThrowIfNull(response);
        _ = await response.ReadViolationsAsync();
        var body = await response.ReadJsonObjectAsync();
        return [.. body["violations"]!.AsArray().OfType<JsonObject>()
            .Select(violation => violation["fixSuggestion"]?.GetValue<string>())];
    }

    /// <summary>
    /// The strong entity tag a response carries, quotes included — and a refusal when it carries none.
    /// </summary>
    /// <remarks>
    /// It reads the <em>raw</em> header rather than <c>Headers.ETag</c>, and then requires that
    /// <c>HttpClient</c> could parse it as a strong tag. <c>Headers.ETag</c> alone answers
    /// <see langword="null"/> for a header that is present but unparsable, so a fact feeding a tag back as
    /// <c>If-Match</c> would send the string "null" and read the resulting 412 as the mechanism working.
    /// Requiring the tag here is what makes "no tag was minted" fail at the fact that needed one, naming the
    /// response, rather than three requests later.
    /// </remarks>
    /// <param name="response">The response to read.</param>
    internal static string ETagOf(this HttpResponseMessage response)
    {
        ArgumentNullException.ThrowIfNull(response);
        var raw = response.Headers.TryGetValues("ETag", out var values) ? string.Join(", ", values) : null;
        if (raw is null)
        {
            throw new InvalidOperationException(
                $"Expected an ETag, but the {(int)response.StatusCode} response carried no such header.");
        }

        var parsed = response.Headers.ETag
            ?? throw new InvalidOperationException($"The ETag '{raw}' is not a well-formed entity tag.");

        return parsed.IsWeak
            ? throw new InvalidOperationException(
                $"The ETag '{raw}' is weak, so RFC 9110 §13.1.1's strong comparison could never match it.")
            : parsed.Tag;
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
