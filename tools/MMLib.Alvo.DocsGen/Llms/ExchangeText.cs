using System.Globalization;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace MMLib.Alvo.DocsGen.Llms;

/// <summary>
/// An <c>&lt;Exchange&gt;</c> tag as plain text, honouring the props <c>Exchange.astro</c> renders by:
/// <c>steps</c>, <c>fields</c>, <c>form</c>, <c>request</c> and <c>requestBody</c>.
/// </summary>
internal static class ExchangeText
{
    private static readonly JsonSerializerOptions _indented = new() { WriteIndented = true, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    internal static string Render(string attrs, string generatedDir)
    {
        var name = Quoted(attrs, "name") ?? throw new InvalidOperationException($"'<Exchange {attrs.Trim()} />' has no name=\"…\" attribute.");
        var file = Path.Combine(generatedDir, "exchanges", name + ".json");
        if (!File.Exists(file))
        {
            throw new InvalidOperationException($"Exchange \"{name}\" has not been captured: '{file}' does not exist.");
        }

        var steps = JsonNode.Parse(File.ReadAllText(file))!["steps"]!.AsArray();
        var shape = Shape.Of(name, attrs);
        return string.Join("\n\n", Chosen(name, steps, shape.Steps).Select(step => Step(name, step!, shape)));
    }

    private static IEnumerable<JsonNode?> Chosen(string name, JsonArray steps, IReadOnlyList<int>? indexes) =>
        indexes is null
            ? steps
            : indexes.Select(i => i >= 0 && i < steps.Count ? steps[i] : throw new InvalidOperationException($"Exchange \"{name}\" has no step {i}."));

    private static string Step(string name, JsonNode step, Shape shape)
    {
        var response = ContentBody.Fence("http", Response(name, step, shape.Fields));
        return shape.Request ? Request(step, shape) + "\n\n" + response : response;
    }

    private static string Request(JsonNode step, Shape shape)
    {
        if (!shape.Http)
        {
            return ContentBody.Fence("sh", Text(step, "curl"));
        }

        var request = Text(step, "httpRequest");
        return ContentBody.Fence("http", shape.RequestBody ? request : Head(request));
    }

    private static string Response(string name, JsonNode step, IReadOnlyList<string>? fields)
    {
        var response = Text(step, "httpResponse");
        if (fields is null)
        {
            return response;
        }

        var body = step["responseBody"] as JsonObject ?? throw new InvalidOperationException($"Exchange \"{name}\" has no JSON response body to pick fields from.");
        var picked = new JsonObject();
        foreach (var field in fields)
        {
            picked[field] = body.TryGetPropertyValue(field, out var value)
                ? value?.DeepClone()
                : throw new InvalidOperationException($"Exchange \"{name}\" has no response field \"{field}\".");
        }

        return Head(response) + "\n\n" + picked.ToJsonString(_indented);
    }

    private static string Text(JsonNode step, string member) => step[member]!.GetValue<string>();

    private static string Head(string message) => message.Split("\n\n")[0];

    private static string? Quoted(string attrs, string name)
    {
        var match = Regex.Match(attrs, $"(?:^|\\s){Regex.Escape(name)}=\"(?<value>[^\"]*)\"");
        return match.Success ? match.Groups["value"].Value : null;
    }

    private static JsonNode? Braced(string attrs, string name)
    {
        var match = Regex.Match(attrs, $"(?:^|\\s){Regex.Escape(name)}=\\{{(?<value>[^{{}}]*)\\}}");
        return match.Success ? JsonNode.Parse(match.Groups["value"].Value) : null;
    }

    private sealed record Shape(IReadOnlyList<int>? Steps, IReadOnlyList<string>? Fields, bool Http, bool Request, bool RequestBody)
    {
        internal static Shape Of(string name, string attrs)
        {
            var shape = new Shape(
                Braced(attrs, "steps")?.AsArray().Select(n => n!.GetValue<int>()).ToList(),
                Braced(attrs, "fields")?.AsArray().Select(n => n!.GetValue<string>()).ToList(),
                string.Equals(Quoted(attrs, "form"), "http", StringComparison.Ordinal),
                Braced(attrs, "request")?.GetValue<bool>() ?? true,
                Braced(attrs, "requestBody")?.GetValue<bool>() ?? true);
            if (!shape.RequestBody && !shape.Http)
            {
                throw new InvalidOperationException(string.Create(CultureInfo.InvariantCulture, $"Exchange \"{name}\" can drop the request body only in the http form."));
            }

            return shape;
        }
    }
}
