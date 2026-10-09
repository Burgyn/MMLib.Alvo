using System.Text.Json;
using System.Text.Json.Serialization;

namespace MMLib.Alvo.DocsGen.Problems;

internal sealed record ProblemTypeNote(
    IReadOnlyList<string> Causes,
    string Fix,
    IReadOnlyList<string> ViolationCodes,
    IReadOnlyList<string> Guides,
    bool NeedsProblemDetails);

internal static class ProblemTypeNotes
{
    private static readonly JsonSerializerOptions _options = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = false,
        RespectRequiredConstructorParameters = true,
        RespectNullableAnnotations = true,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
    };

    internal static string PathIn(string siteRoot) => Path.Combine(siteRoot, "src", "data", "problem-types.json");

    internal static IReadOnlyDictionary<string, ProblemTypeNote> Load(string siteRoot) => Parse(File.ReadAllText(PathIn(siteRoot)));

    internal static IReadOnlyDictionary<string, ProblemTypeNote> Parse(string json) =>
        JsonSerializer.Deserialize<Dictionary<string, ProblemTypeNote>>(json, _options)
        ?? throw new InvalidOperationException("The problem-type notes are empty.");
}
