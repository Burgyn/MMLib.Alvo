using System.Text.Json.Nodes;

namespace MMLib.Alvo.DocsGen.Host;

internal sealed record ManagementRouteInfo(string Method, string Route, string Member);

internal sealed record HostSnapshot(
    JsonNode OpenApi,
    JsonNode CelFunctions,
    JsonNode Capabilities,
    IReadOnlyList<ManagementRouteInfo> ManagementRoutes);
