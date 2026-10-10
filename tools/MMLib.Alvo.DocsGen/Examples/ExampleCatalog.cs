using System.Text.Json;

namespace MMLib.Alvo.DocsGen.Examples;

internal sealed record Example(
    string Directory,
    string Descriptor,
    string Summary,
    bool Runnable,
    IReadOnlyList<string> Roles,
    IReadOnlyList<string> Entities,
    bool MultiTenant = false,
    ExampleStack? OwnStack = null);

internal static class ExampleCatalog
{
    internal const string NotRunnableMarker = "NOT-RUNNABLE.md";
    internal const string ReadmePath = "examples/README.md";

    private const string NegativeDirectory = "_negative";
    private const string BulletStart = "- **`";

    internal static IReadOnlyList<Example> Read(string repoRoot)
    {
        var examples = Path.Combine(repoRoot, "examples");
        var summaries = Summaries(File.ReadAllLines(Path.Combine(examples, "README.md")));
        return
        [
            .. Directory.EnumerateDirectories(examples)
                .Where(directory => Path.GetFileName(directory) != NegativeDirectory)
                .Order(StringComparer.Ordinal)
                .Select(directory => ReadOne(directory, summaries) with { OwnStack = ExampleStack.Find(repoRoot, directory) }),
        ];
    }

    private static Example ReadOne(string directory, IReadOnlyDictionary<string, string> summaries)
    {
        var name = Path.GetFileName(directory);
        var descriptor = SingleDescriptor(directory);
        using var document = JsonDocument.Parse(File.ReadAllText(descriptor));
        var root = document.RootElement;
        return new Example(
            name,
            Path.GetFileName(descriptor),
            summaries.GetValueOrDefault(name) ?? throw new InvalidOperationException($"examples/README.md has no '{BulletStart}{name}/`**' bullet."),
            !File.Exists(Path.Combine(directory, NotRunnableMarker)),
            Roles(root),
            root.TryGetProperty("entities", out var entities) ? [.. entities.EnumerateObject().Select(entity => entity.Name)] : [],
            IsMultiTenant(root));
    }

    private static string SingleDescriptor(string directory)
    {
        var descriptors = Directory.GetFiles(directory, "*.alvo.json");
        return descriptors.Length == 1
            ? descriptors[0]
            : throw new InvalidOperationException($"'{directory}' must hold exactly one *.alvo.json descriptor; it holds {descriptors.Length}.");
    }

    private static bool IsMultiTenant(JsonElement root) =>
        root.TryGetProperty("tenancy", out var tenancy)
        && tenancy.TryGetProperty("enabled", out var enabled)
        && enabled.ValueKind == JsonValueKind.True;

    private static List<string> Roles(JsonElement root) =>
        root.TryGetProperty("auth", out var auth) && auth.TryGetProperty("roles", out var roles)
            ? [.. roles.EnumerateArray().Select(role => role.GetString()!)]
            : [];

    private static Dictionary<string, string> Summaries(string[] lines)
    {
        var summaries = new Dictionary<string, string>(StringComparer.Ordinal);
        for (var index = 0; index < lines.Length; index++)
        {
            if (lines[index].StartsWith(BulletStart, StringComparison.Ordinal) && BulletDirectory(lines[index]) is { } name)
            {
                summaries[name] = BulletText(lines, index);
            }
        }

        return summaries;
    }

    private static string? BulletDirectory(string line)
    {
        var end = line.IndexOf("/`**", BulletStart.Length, StringComparison.Ordinal);
        return end < 0 ? null : line[BulletStart.Length..end];
    }

    private static string BulletText(string[] lines, int start)
    {
        var first = lines[start];
        var dash = first.IndexOf('—', StringComparison.Ordinal);
        var text = new List<string> { (dash < 0 ? first : first[(dash + 1)..]).Trim() };
        for (var index = start + 1; index < lines.Length && !EndsBullet(lines[index]); index++)
        {
            text.Add(lines[index].Trim());
        }

        return string.Join(' ', text).Trim();
    }

    private static bool EndsBullet(string line) =>
        line.Trim().Length == 0 || line.StartsWith("- ", StringComparison.Ordinal) || line.StartsWith('#');
}
