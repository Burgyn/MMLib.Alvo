using System.Globalization;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace MMLib.Alvo.DocsGen.Exchanges;

internal static class ExchangeRenderer
{
    internal const string DisplayBase = "http://localhost:8080";

    private const string Continuation = " \\\n  ";

    internal static JsonSerializerOptions Compact { get; } = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    private static readonly JsonSerializerOptions _indented = new()
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        WriteIndented = true,
        NewLine = "\n",
    };

    internal static string Curl(CapturedRequest request)
    {
        var parts = new List<string> { $"curl -sS -X {request.Method} {ShellWord(DisplayBase + request.Path)}" };
        parts.AddRange(request.Headers.Select(header => $"-H \"{DoubleQuoted(header.Key)}: {DoubleQuoted(header.Value)}\""));
        if (request.Body is not null)
        {
            parts.Add($"-d {SingleQuoted(request.Body)}");
        }

        return string.Join(Continuation, parts);
    }

    private static string ShellWord(string value) =>
        value.All(c => char.IsAsciiLetterOrDigit(c) || "/:.-_~".Contains(c, StringComparison.Ordinal)) ? value : SingleQuoted(value);

    private static string SingleQuoted(string value) => $"'{value.Replace("'", "'\\''", StringComparison.Ordinal)}'";

    internal static string HttpRequest(CapturedRequest request) =>
        Message($"{request.Method} {request.Path} HTTP/1.1", request.Headers, request.Body);

    internal static string HttpResponse(CapturedResponse response) =>
        Message($"HTTP/1.1 {response.Status.ToString(CultureInfo.InvariantCulture)} {response.Reason}", response.Headers, response.Body);

    internal static GeneratedPage Render(ExchangeSpec spec, IReadOnlyList<CapturedStep> steps)
    {
        var page = new JsonObject
        {
            ["name"] = spec.Name,
            ["descriptor"] = spec.Descriptor,
            ["steps"] = new JsonArray([.. steps.Select(StepNode)]),
        };
        return new GeneratedPage(OutputRoot.Generated, $"exchanges/{spec.Name}.json", page.ToJsonString(_indented) + "\n");
    }

    internal static JsonNode? ParseJson(string? body)
    {
        if (string.IsNullOrWhiteSpace(body))
        {
            return null;
        }

        try
        {
            return JsonNode.Parse(body);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static JsonObject StepNode(CapturedStep step) => new()
    {
        ["curl"] = Curl(step.Request),
        ["httpRequest"] = HttpRequest(step.Request),
        ["status"] = step.Response.Status,
        ["reason"] = step.Response.Reason,
        ["httpResponse"] = HttpResponse(step.Response),
        ["responseBody"] = ParseJson(step.Response.Body),
    };

    private static string Message(string startLine, IReadOnlyList<KeyValuePair<string, string>> headers, string? body)
    {
        var message = new StringBuilder(startLine);
        foreach (var header in headers)
        {
            message.Append('\n').Append(header.Key).Append(": ").Append(header.Value);
        }

        if (body is not null)
        {
            message.Append("\n\n").Append(Indented(body));
        }

        return message.ToString();
    }

    private static string Indented(string body) => ParseJson(body) is { } json ? json.ToJsonString(_indented) : body;

    private static string DoubleQuoted(string value) =>
        value.Replace("\\", "\\\\", StringComparison.Ordinal)
            .Replace("\"", "\\\"", StringComparison.Ordinal)
            .Replace("`", "\\`", StringComparison.Ordinal);
}
