using MMLib.Alvo.DocsGen.Markdown;
using System.Text.Json.Nodes;

namespace MMLib.Alvo.DocsGen.Schema;

internal static class SchemaConditions
{
    internal static bool TryRead(JsonObject condition, Dictionary<string, List<string>> notes)
    {
        var ifNode = condition["if"] as JsonObject;
        var then = condition["then"] as JsonObject;
        if (ifNode is null || then is null || condition.ContainsKey("else"))
        {
            return false;
        }

        return TryEquals(ifNode, then, notes) || TryNotEquals(ifNode, then, notes) || TryExclusive(ifNode, then, notes);
    }

    private static bool TryEquals(JsonObject ifNode, JsonObject then, Dictionary<string, List<string>> notes) =>
        ConstTest(ifNode) is { } test
        && Add(notes, RequiredNames(then), $"Required when {Md.Code(test.Name)} is {Md.Code(test.Value)}.");

    private static bool TryNotEquals(JsonObject ifNode, JsonObject then, Dictionary<string, List<string>> notes)
    {
        if (ifNode.Count != 1 || ifNode["not"] is not JsonObject negated || ConstTest(negated) is not { } test)
        {
            return false;
        }

        return Add(notes, ForbiddenNames(then), $"Allowed only when {Md.Code(test.Name)} is {Md.Code(test.Value)}.")
            || Add(notes, RequiredNames(then), $"Required unless {Md.Code(test.Name)} is {Md.Code(test.Value)}.");
    }

    private static bool TryExclusive(JsonObject ifNode, JsonObject then, Dictionary<string, List<string>> notes)
    {
        if (ifNode.Count != 1 || ifNode["required"] is not JsonArray { Count: 1 } present)
        {
            return false;
        }

        return Add(notes, ForbiddenNames(then), $"Not allowed together with {Md.Code(present[0]!.GetValue<string>())}.");
    }

    private static (string Name, string Value)? ConstTest(JsonObject ifNode)
    {
        if (ifNode.Count != 1 || ifNode["properties"] is not JsonObject { Count: 1 } properties)
        {
            return null;
        }

        var (name, schema) = properties.First();
        return schema is JsonObject { Count: 1 } test && test.ContainsKey("const")
            ? (name, SchemaNode.Literal(test["const"]))
            : null;
    }

    private static List<string> RequiredNames(JsonObject then) =>
        then.Count == 1 && then["required"] is JsonArray names ? names.Select(name => name!.GetValue<string>()).ToList() : [];

    private static List<string> ForbiddenNames(JsonObject then) =>
        then.Count == 1 && then["properties"] is JsonObject properties && properties.All(IsFalse)
            ? properties.Select(property => property.Key).ToList()
            : [];

    private static bool IsFalse(KeyValuePair<string, JsonNode?> property) =>
        property.Value is JsonValue value && value.TryGetValue<bool>(out var flag) && !flag;

    private static bool Add(Dictionary<string, List<string>> notes, List<string> names, string note)
    {
        foreach (var name in names)
        {
            if (!notes.TryGetValue(name, out var list))
            {
                notes[name] = list = [];
            }

            list.Add(note);
        }

        return names.Count > 0;
    }
}
