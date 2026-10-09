using Microsoft.AspNetCore.WebUtilities;
using MMLib.Alvo.DocsGen.Host;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json.Nodes;

namespace MMLib.Alvo.DocsGen.Exchanges;

internal static class ExchangeRunner
{
    internal const string JsonContentType = "application/json";

    private const string TestServerBase = "http://localhost";

    private static readonly string[] _renderedHeaders = ["Content-Type", "ETag", "Location", "Preference-Applied"];

    internal static async Task<IReadOnlyList<CapturedStep>> RunAsync(ExchangeSpec spec, string repoRoot, CancellationToken ct)
    {
        var host = await HostBoot.StartAsync(Path.Combine(repoRoot, spec.Descriptor), spec.Keys, ct).ConfigureAwait(false);
        await using (host.ConfigureAwait(false))
        {
            return await RunAsync(spec, host, repoRoot, ct).ConfigureAwait(false);
        }
    }

    internal static async Task<IReadOnlyList<CapturedStep>> RunAsync(ExchangeSpec spec, BootedHost host, string repoRoot, CancellationToken ct)
    {
        var captured = new List<CapturedStep>();
        for (var i = 0; i < spec.Steps.Count; i++)
        {
            var request = await RequestOf(spec, i, captured, repoRoot, ct).ConfigureAwait(false);
            var response = await SendAsync(host, spec.Steps[i], request, ct).ConfigureAwait(false);
            Check(spec, i, request, response);
            captured.Add(new CapturedStep(request, response));
        }

        return captured;
    }

    private static async Task<CapturedRequest> RequestOf(ExchangeSpec spec, int index, IReadOnlyList<CapturedStep> earlier, string repoRoot, CancellationToken ct)
    {
        var step = spec.Steps[index];
        try
        {
            var headers = new List<KeyValuePair<string, string>>();
            if (step.Key is not null)
            {
                headers.Add(new(HostBoot.ApiKeyHeader, $"{step.Key}.${spec.Keys[step.Key].SecretVariable}"));
            }

            headers.AddRange(step.Headers.Select(header => new KeyValuePair<string, string>(header.Key, Placeholders.Resolve(header.Value, earlier))));
            var body = await BodyOf(step, repoRoot, ct).ConfigureAwait(false);
            if (body is not null)
            {
                headers.Add(new("Content-Type", step.ContentType ?? JsonContentType));
            }

            return new CapturedRequest(step.Method, Placeholders.Resolve(step.Path, earlier), headers, body);
        }
        catch (InvalidOperationException e)
        {
            throw new InvalidOperationException($"{spec.Name}: step {index} ({step.Method} {step.Path}): {e.Message}", e);
        }
    }

    private static async Task<string?> BodyOf(ExchangeStep step, string repoRoot, CancellationToken ct)
    {
        if (step.Body is not null)
        {
            return step.Body.ToJsonString(ExchangeRenderer.Compact);
        }

        return step.BodyFile is null
            ? null
            : (await File.ReadAllTextAsync(Path.Combine(repoRoot, step.BodyFile), ct).ConfigureAwait(false)).TrimEnd();
    }

    private static async Task<CapturedResponse> SendAsync(BootedHost host, ExchangeStep step, CapturedRequest request, CancellationToken ct)
    {
        using var client = host.Client(step.Key);
        using var message = Message(request);
        using var response = await client.SendAsync(message, ct).ConfigureAwait(false);
        var body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        var status = (int)response.StatusCode;
        return new CapturedResponse(status, ReasonPhrases.GetReasonPhrase(status), RenderedHeaders(response, step.ShowHeaders), body.Length == 0 ? null : body);
    }

    private static HttpRequestMessage Message(CapturedRequest request)
    {
        var message = new HttpRequestMessage(new HttpMethod(request.Method), new Uri(request.Path, UriKind.Relative));
        foreach (var header in request.Headers.Where(header => !IsContentType(header.Key) && header.Key != HostBoot.ApiKeyHeader))
        {
            message.Headers.TryAddWithoutValidation(header.Key, header.Value);
        }

        if (request.Body is not null)
        {
            message.Content = new ByteArrayContent(Encoding.UTF8.GetBytes(request.Body));
            message.Content.Headers.ContentType = MediaTypeHeaderValue.Parse(request.Headers.Single(header => IsContentType(header.Key)).Value);
        }

        return message;
    }

    private static List<KeyValuePair<string, string>> RenderedHeaders(HttpResponseMessage response, IReadOnlyList<string> extra) =>
    [
        .. _renderedHeaders.Concat(extra).Distinct(StringComparer.OrdinalIgnoreCase)
            .Select(name => (name, value: HeaderValue(response, name)))
            .Where(header => header.value is not null)
            .Select(header => new KeyValuePair<string, string>(header.name, Displayed(header.name, header.value!))),
    ];

    private static string? HeaderValue(HttpResponseMessage response, string name) =>
        response.Headers.TryGetValues(name, out var values) || response.Content.Headers.TryGetValues(name, out values)
            ? string.Join(", ", values)
            : null;

    private static string Displayed(string name, string value) =>
        string.Equals(name, "Location", StringComparison.OrdinalIgnoreCase) && value.StartsWith(TestServerBase + "/", StringComparison.Ordinal)
            ? ExchangeRenderer.DisplayBase + value[TestServerBase.Length..]
            : value;

    private static bool IsContentType(string name) => string.Equals(name, "Content-Type", StringComparison.OrdinalIgnoreCase);

    private static void Check(ExchangeSpec spec, int index, CapturedRequest request, CapturedResponse response)
    {
        var step = spec.Steps[index];
        var type = (ExchangeRenderer.ParseJson(response.Body) as JsonObject)?["type"]?.ToString();
        var typeMatches = step.ExpectType is null || (type?.EndsWith($"/{step.ExpectType}", StringComparison.Ordinal) ?? false);
        if (response.Status != step.Expect || !typeMatches)
        {
            var expected = step.ExpectType is null ? $"{step.Expect}" : $"{step.Expect} (problem type '{step.ExpectType}')";
            throw new InvalidOperationException(
                $"{spec.Name}: step {index} ({request.Method} {request.Path}) answered {response.Status}, expected {expected}. Body: {response.Body}");
        }
    }
}
