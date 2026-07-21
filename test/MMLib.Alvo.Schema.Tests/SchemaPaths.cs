using MMLib.Alvo.Testing;

namespace MMLib.Alvo.Schema.Tests;

internal static class SchemaPaths
{
    private static readonly string _root = RepositoryRoot.Find();

    internal static string SchemaFile => Path.Combine(_root, "schema", "project.schema.json");

    internal static string MetaSchemaFile =>
        Path.Combine(AppContext.BaseDirectory, "meta-schema", "2020-12", "schema.json");

    internal static IEnumerable<string> Examples() =>
        Directory.EnumerateFiles(Path.Combine(_root, "examples"), "*.alvo.json", SearchOption.AllDirectories)
            .OrderBy(path => path, StringComparer.Ordinal);

    internal static string NegativeExpectationsFile =>
        Path.Combine(_root, "examples", "_negative", "expectations.json");

    internal static IEnumerable<string> NegativeExamples() =>
        Directory.EnumerateFiles(Path.Combine(_root, "examples", "_negative"), "*.json")
            .Where(path => Path.GetFileName(path) != "expectations.json")
            .OrderBy(path => path, StringComparer.Ordinal);
}
