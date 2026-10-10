using System.Text.Json.Nodes;

namespace MMLib.Alvo.DocsGen.Tests.Schema;

internal static class RealSchema
{
    internal static string Path => System.IO.Path.Combine(RepositoryRoot.Find(), "schema", "project.schema.json");

    internal static JsonObject Load() => JsonNode.Parse(File.ReadAllText(Path))!.AsObject();
}
