using System.Text.Json.Nodes;

namespace MMLib.Alvo.Tests.Rules;

/// <summary>Descriptor JSON for relation-rule facts: entities with fields and rules, written as compactly as a fact reads.</summary>
internal static class RelationDescriptors
{
    /// <summary>A descriptor holding <paramref name="entities"/>.</summary>
    /// <param name="entities">Each entity's name and JSON body.</param>
    internal static string Project(params (string Name, JsonObject Body)[] entities)
    {
        var all = new JsonObject();
        foreach (var (name, body) in entities)
        {
            all[name] = body;
        }

        return new JsonObject { ["apiVersion"] = "alvo.dev/v1", ["name"] = "relations", ["entities"] = all }.ToJsonString();
    }

    /// <summary>An entity with the given fields and rules.</summary>
    /// <param name="fields">Each field's name and JSON body.</param>
    /// <param name="rules">Each rule's operation and source.</param>
    internal static JsonObject Entity((string Name, JsonObject Body)[] fields, params (string Operation, string Source)[] rules)
    {
        var fieldObject = new JsonObject();
        foreach (var (name, body) in fields)
        {
            fieldObject[name] = body;
        }

        var entity = new JsonObject { ["fields"] = fieldObject };
        if (rules.Length > 0)
        {
            var ruleObject = new JsonObject();
            foreach (var (operation, source) in rules)
            {
                ruleObject[operation] = source;
            }

            entity["rules"] = ruleObject;
        }

        return entity;
    }

    internal static (string, JsonObject) Ref(string name, string target) =>
        (name, new JsonObject { ["type"] = "ref", ["entity"] = target });

    internal static (string, JsonObject) Field(string name, string type) => (name, new JsonObject { ["type"] = type });
}
