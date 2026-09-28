using MMLib.Alvo.Ai.Internal;

using System.Text.Json;
using System.Text.Json.Nodes;

namespace MMLib.Alvo.Ai.Tests;

/// <summary>
/// The engine against the community RFC 6902 suite, record by record, so a failure names the case.
/// </summary>
/// <remarks>
/// Records marked <c>disabled</c> by the suite and records that are only a comment are skipped, as its README says.
/// A record with <c>error</c> must fail; one with <c>expected</c> must produce exactly that document.
/// </remarks>
public sealed class JsonPatchConformanceTests
{
    private static readonly string[] _files = ["tests.json", "spec_tests.json"];

    public static TheoryData<string, int, string> Records()
    {
        var data = new TheoryData<string, int, string>();
        foreach (var file in _files)
        {
            AddRecords(data, file);
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(Records))]
    public void A_suite_record_behaves_as_the_suite_says(string file, int index, string comment)
    {
        var record = Load(file)[index];

        var result = JsonPatch.Apply(Node(record.GetProperty("doc")), record.GetProperty("patch"));

        if (record.TryGetProperty("error", out _))
        {
            result.Succeeded.ShouldBeFalse(comment);
            return;
        }

        result.Succeeded.ShouldBeTrue($"{comment}: {result.Error}");
        if (record.TryGetProperty("expected", out var expected))
        {
            JsonNode.DeepEquals(result.Document, Node(expected)).ShouldBeTrue(comment);
        }
    }

    private static void AddRecords(TheoryData<string, int, string> data, string file)
    {
        var records = Load(file);
        for (var index = 0; index < records.Count; index++)
        {
            if (IsRunnable(records[index]))
            {
                data.Add(file, index, Comment(records[index], index));
            }
        }
    }

    private static bool IsRunnable(JsonElement record) =>
        record.TryGetProperty("patch", out _)
        && !(record.TryGetProperty("disabled", out var disabled) && disabled.ValueKind == JsonValueKind.True);

    private static string Comment(JsonElement record, int index) =>
        record.TryGetProperty("comment", out var comment) ? comment.GetString()! : $"record {index}";

    private static List<JsonElement> Load(string file)
    {
        var path = Path.Combine(
            RepositoryRoot.Find(), "test", "MMLib.Alvo.Ai.Tests", "TestData", "json-patch-tests", file);
        using var document = JsonDocument.Parse(File.ReadAllText(path));

        return [.. document.RootElement.EnumerateArray().Select(record => record.Clone())];
    }

    private static JsonNode? Node(JsonElement element) => JsonNode.Parse(element.GetRawText());
}
