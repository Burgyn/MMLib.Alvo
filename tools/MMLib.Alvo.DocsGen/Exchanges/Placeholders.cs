using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace MMLib.Alvo.DocsGen.Exchanges;

internal sealed record CapturedRequest(string Method, string Path, IReadOnlyList<KeyValuePair<string, string>> Headers, string? Body);

internal sealed record CapturedResponse(int Status, string Reason, IReadOnlyList<KeyValuePair<string, string>> Headers, string? Body);

internal sealed record CapturedStep(CapturedRequest Request, CapturedResponse Response);

internal static partial class Placeholders
{
    internal static string Resolve(string template, IReadOnlyList<CapturedStep> earlier) =>
        PlaceholderPattern().Replace(template, match => ValueOf(match, earlier));

    internal static JsonNode? ResolveIn(JsonNode? node, IReadOnlyList<CapturedStep> earlier) => node switch
    {
        null => null,
        JsonObject obj => new JsonObject(obj.Select(member => KeyValuePair.Create(member.Key, ResolveIn(member.Value, earlier)))),
        JsonArray array => new JsonArray([.. array.Select(item => ResolveIn(item, earlier))]),
        JsonValue value when value.GetValueKind() == JsonValueKind.String => JsonValue.Create(Resolve(value.GetValue<string>(), earlier)),
        _ => node.DeepClone(),
    };

    private static string ValueOf(Match match, IReadOnlyList<CapturedStep> earlier)
    {
        var step = int.Parse(match.Groups["step"].Value, CultureInfo.InvariantCulture);
        var path = match.Groups["path"].Value;
        var value = step < earlier.Count
            ? match.Groups["source"].Value == "body" ? BodyValue(earlier[step].Response.Body, path) : HeaderValue(earlier[step].Response, path)
            : null;
        return value ?? throw new InvalidOperationException(
            $"The placeholder {match.Value} does not resolve: step {step} has no such value (a header must be one the step renders; add it to showHeaders).");
    }

    private static string? HeaderValue(CapturedResponse response, string name) =>
        response.Headers.Where(header => string.Equals(header.Key, name, StringComparison.OrdinalIgnoreCase))
            .Select(header => header.Value).FirstOrDefault();

    private static string? BodyValue(string? body, string path)
    {
        var node = ExchangeRenderer.ParseJson(body);
        foreach (var segment in path.Split('.'))
        {
            node = Child(node, segment);
        }

        return node switch
        {
            null => null,
            JsonValue value when value.GetValueKind() == JsonValueKind.String => value.GetValue<string>(),
            _ => node.ToJsonString(),
        };
    }

    private static JsonNode? Child(JsonNode? node, string segment) => node switch
    {
        JsonObject obj => obj[segment],
        JsonArray array when int.TryParse(segment, NumberStyles.None, CultureInfo.InvariantCulture, out var index) && index < array.Count => array[index],
        _ => null,
    };

    [GeneratedRegex(@"\{(?<step>\d+)\.(?<source>body|header)\.(?<path>[^{}]+)\}", RegexOptions.CultureInvariant)]
    private static partial Regex PlaceholderPattern();
}
