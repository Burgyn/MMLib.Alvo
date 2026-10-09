using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Routing;
using MMLib.Alvo.DocsGen.Exchanges;
using MMLib.Alvo.Host;
using System.Text.Json.Nodes;

namespace MMLib.Alvo.DocsGen.Host;

internal static class HostCapture
{
    internal const string Project = "vehicle-registry";

    private const string KeyId = "docsgen";
    private const string ManagementEndpointPrefix = "Alvo.Management.";
    private const string AccessBlock = """{ "viewer": "'admin' in @user.roles" }""";
    private const string ProjectRoute = $"/management/projects/{Project}";

    private static readonly ExchangeKey _admin = new(["admin", "authenticated"], ["*:read", "*:write"], null, "ALVO_KEY_SECRET");

    internal static async Task<HostSnapshot> CaptureAsync(string repoRoot, CancellationToken ct)
    {
        var work = Directory.CreateTempSubdirectory("alvo-docsgen-capture-");
        try
        {
            var descriptor = await WriteDescriptorWithAccessAsync(repoRoot, work.FullName, ct).ConfigureAwait(false);
            var host = await HostBoot.StartAsync(descriptor, new Dictionary<string, ExchangeKey>(StringComparer.Ordinal) { [KeyId] = _admin }, ct).ConfigureAwait(false);
            await using (host.ConfigureAwait(false))
            {
                return await ReadAsync(host, ct).ConfigureAwait(false);
            }
        }
        finally
        {
            work.Delete(recursive: true);
        }
    }

    private static async Task<HostSnapshot> ReadAsync(BootedHost host, CancellationToken ct)
    {
        using var client = host.Client(KeyId);
        return new HostSnapshot(
            await GetJsonAsync(client, AlvoHost.OpenApiDocumentPath, ct).ConfigureAwait(false),
            await GetJsonAsync(client, $"{ProjectRoute}/cel/functions", ct).ConfigureAwait(false),
            await GetJsonAsync(client, $"{ProjectRoute}/capabilities", ct).ConfigureAwait(false),
            ManagementRoutes(host.App));
    }

    private static async Task<JsonNode> GetJsonAsync(HttpClient client, string path, CancellationToken ct)
    {
        using var response = await client.GetAsync(new Uri(path, UriKind.Relative), ct).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        return JsonNode.Parse(await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false))
            ?? throw new InvalidOperationException($"GET {path} answered an empty document.");
    }

    private static IReadOnlyList<ManagementRouteInfo> ManagementRoutes(WebApplication app) =>
    [
        .. ((IEndpointRouteBuilder)app).DataSources
            .SelectMany(source => source.Endpoints)
            .OfType<RouteEndpoint>()
            .SelectMany(RoutesOf),
    ];

    private static IEnumerable<ManagementRouteInfo> RoutesOf(RouteEndpoint endpoint)
    {
        var name = endpoint.Metadata.GetMetadata<IEndpointNameMetadata>()?.EndpointName;
        if (name is null || !name.StartsWith(ManagementEndpointPrefix, StringComparison.Ordinal))
        {
            return [];
        }

        var methods = endpoint.Metadata.GetMetadata<IHttpMethodMetadata>()?.HttpMethods ?? [];
        return methods.Select(method => new ManagementRouteInfo(method, endpoint.RoutePattern.RawText ?? string.Empty, name[ManagementEndpointPrefix.Length..]));
    }

    private static async Task<string> WriteDescriptorWithAccessAsync(string repoRoot, string work, CancellationToken ct)
    {
        var source = Path.Combine(repoRoot, "examples", "vehicle-registry", "vehicles.alvo.json");
        var descriptor = JsonNode.Parse(await File.ReadAllTextAsync(source, ct).ConfigureAwait(false))!.AsObject();
        descriptor["access"] = JsonNode.Parse(AccessBlock);
        var target = Path.Combine(work, "descriptor.alvo.json");
        await File.WriteAllTextAsync(target, descriptor.ToJsonString(), ct).ConfigureAwait(false);
        return target;
    }
}
