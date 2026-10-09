using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using MMLib.Alvo.Host;
using System.Text.Json.Nodes;

namespace MMLib.Alvo.DocsGen.Host;

internal static class HostCapture
{
    internal const string Project = "vehicle-registry";

    private const string KeyId = "docsgen";
    private const string Secret = "docsgen-capture-secret-long-enough-for-the-floor";
    private const string ApiKeyHeader = "X-Alvo-Api-Key";
    private const string ManagementEndpointPrefix = "Alvo.Management.";
    private const string AccessBlock = """{ "viewer": "'admin' in @user.roles" }""";
    private const string ProjectRoute = $"/management/projects/{Project}";

    internal static async Task<HostSnapshot> CaptureAsync(string repoRoot, CancellationToken ct)
    {
        var work = Directory.CreateTempSubdirectory("alvo-docsgen-");
        try
        {
            var descriptor = await WriteDescriptorWithAccessAsync(repoRoot, work.FullName, ct).ConfigureAwait(false);
            return await CaptureFromAsync(descriptor, Path.Combine(work.FullName, "alvo.db"), ct).ConfigureAwait(false);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            work.Delete(recursive: true);
        }
    }

    private static async Task<HostSnapshot> CaptureFromAsync(string descriptor, string database, CancellationToken ct)
    {
        var builder = AlvoHost.CreateBuilder([], configuration => configuration.AddInMemoryCollection(Settings(descriptor, database)));
        builder.Logging.ClearProviders();
        builder.WebHost.UseTestServer();
        var app = await AlvoHost.BuildAsync(builder).ConfigureAwait(false);
        await using (app.ConfigureAwait(false))
        {
            await app.StartAsync(ct).ConfigureAwait(false);
            var snapshot = await ReadAsync(app, ct).ConfigureAwait(false);
            await app.StopAsync(ct).ConfigureAwait(false);
            return snapshot;
        }
    }

    private static async Task<HostSnapshot> ReadAsync(WebApplication app, CancellationToken ct)
    {
        using var client = app.GetTestClient();
        client.DefaultRequestHeaders.Add(ApiKeyHeader, $"{KeyId}.{Secret}");
        return new HostSnapshot(
            await GetJsonAsync(client, AlvoHost.OpenApiDocumentPath, ct).ConfigureAwait(false),
            await GetJsonAsync(client, $"{ProjectRoute}/cel/functions", ct).ConfigureAwait(false),
            await GetJsonAsync(client, $"{ProjectRoute}/capabilities", ct).ConfigureAwait(false),
            ManagementRoutes(app));
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

    private static Dictionary<string, string?> Settings(string descriptor, string database) => new(StringComparer.Ordinal)
    {
        ["Alvo:DescriptorPath"] = descriptor,
        ["Alvo:Database:Provider"] = "sqlite",
        ["Alvo:Database:SqliteConnectionString"] = $"Data Source={database}",
        ["Alvo:Auth:DevKeys:0:KeyId"] = KeyId,
        ["Alvo:Auth:DevKeys:0:Secret"] = Secret,
        ["Alvo:Auth:DevKeys:0:User"] = "0f8fad5b-d9cb-469f-a165-70867728950e",
        ["Alvo:Auth:DevKeys:0:Roles:0"] = "admin",
        ["Alvo:Auth:DevKeys:0:Roles:1"] = "authenticated",
        ["Alvo:Auth:DevKeys:0:Scopes:0"] = "*:read",
        ["Alvo:Auth:DevKeys:0:Scopes:1"] = "*:write",
    };

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
