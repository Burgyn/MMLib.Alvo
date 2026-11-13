using System.Text.Json.Nodes;

namespace MMLib.Alvo.Admin.Components.Schema;

/* After-actions and automation: what reads the triggering row, and what an entity.update writes. */
internal static partial class FieldReferences
{
    private const string EntityUpdate = "entity.update";
    private const string EntityEvents = "entity";

    /// <summary>The action slots whose <c>{"$cel": …}</c> values read the triggering row.</summary>
    private static readonly string[] _taggedSlots = ["payload", "input"];

    /// <summary>
    /// What an after-action reads of the row that triggered it: <c>{{new.…}}</c> placeholders, and the tagged values of an
    /// <c>entity.update</c> payload or a function's input (<c>ActionCompiler</c> compiles both over the triggering row).
    /// </summary>
    private static void RowReads(JsonObject action, string place, Site at)
    {
        Placeholders(action, place, at);
        foreach (var slot in _taggedSlots)
        {
            if (action[slot] is JsonObject values)
            {
                TaggedValues(values, $"{place}.{slot}", at);
            }
        }
    }

    private static void TaggedValues(JsonObject values, string place, Site at)
    {
        foreach (var (key, value) in values.ToList())
        {
            if (value is JsonObject tagged)
            {
                Expression(tagged, "$cel", $"{place}.{key}", at);
            }
        }
    }

    /// <summary>A mutate's or an <c>entity.update</c> payload's keys, which are the written entity's fields.</summary>
    private static void Keys(JsonObject owner, string place, Site at)
    {
        if (!owner.ContainsKey(at.Field))
        {
            return;
        }

        /* An object that already sets the new name as well cannot be merged by a rename — it is named instead. */
        if (at.To is { } to && owner.ContainsKey(to))
        {
            at.Found.Add(new(new(place, Blocks: true), Carried: false));
            return;
        }

        if (at.To is { } target)
        {
            Rekey(owner, at.Field, target);
        }

        at.Add(place, blocks: true);
    }

    /// <summary><c>{{new.field}}</c> and <c>{{old.field}}</c> in an after-action's strings — exact placeholder syntax, so always carried.</summary>
    private static void Placeholders(JsonObject action, string place, Site at)
    {
        var pattern = Placeholder(at.Field);
        foreach (var (key, value) in action.ToList())
        {
            if (value is JsonObject nested)
            {
                Placeholders(nested, $"{place}.{key}", at);
            }
            else if (value is JsonValue text && text.TryGetValue<string>(out var template) && pattern.IsMatch(template))
            {
                if (at.To is { } to)
                {
                    action[key] = pattern.Replace(template, "${1}" + to + "${2}");
                }

                at.Add($"{place}.{key}", blocks: true);
            }
        }
    }

    /// <summary>Every <c>entity.update</c> that writes this entity, from any entity's after-hook or any automation rule.</summary>
    private static void UpdateTargets(JsonObject root, JsonObject entities, Site at)
    {
        foreach (var (name, declared) in Members(entities))
        {
            foreach (var (hook, place) in HooksOf(declared, name))
            {
                UpdateTarget(hook["action"] as JsonObject, $"{place}.action", at);
            }
        }

        foreach (var (name, rule) in Members(root["automation"]))
        {
            foreach (var (action, place) in ActionsOf(rule, $"automation.{name}"))
            {
                UpdateTarget(action, place, at);
            }
        }
    }

    private static void UpdateTarget(JsonObject? action, string place, Site at)
    {
        if (action is not null && Is(action["type"], EntityUpdate) && Is(action["entity"], at.Entity) && action["payload"] is JsonObject payload)
        {
            Keys(payload, $"{place}.payload", at);
        }
    }

    /// <summary>Each automation rule this entity's events trigger: its condition and its actions read the row, as an after-hook's do.</summary>
    private static void Automation(JsonObject root, Site at)
    {
        foreach (var (name, rule) in Members(root["automation"]))
        {
            if (!IsTriggeredBy(rule, at.Entity))
            {
                continue;
            }

            Expression(rule, "condition", $"automation.{name}.condition", at);
            foreach (var (action, place) in ActionsOf(rule, $"automation.{name}"))
            {
                RowReads(action, place, at);
            }
        }
    }

    /// <summary>Whether the rule's trigger is <c>entity.&lt;entity&gt;.&lt;operation&gt;</c> — the shape <c>AutomationCompiler</c> accepts.</summary>
    private static bool IsTriggeredBy(JsonObject rule, string entity)
        => rule["trigger"]?["event"] is JsonValue value && value.TryGetValue<string>(out var pattern)
            && pattern.Split('.') is [EntityEvents, var source, _] && source == entity;

    private static IEnumerable<(JsonObject Action, string Place)> ActionsOf(JsonObject rule, string place)
    {
        if (rule["actions"] is not JsonArray actions)
        {
            yield break;
        }

        for (var i = 0; i < actions.Count; i++)
        {
            if (actions[i] is JsonObject action)
            {
                yield return (action, Indexed($"{place}.actions", i));
            }
        }
    }

    private static IEnumerable<(JsonObject Hook, string Place)> HooksOf(JsonObject declared, string entity)
    {
        foreach (var (point, node) in declared["hooks"] is JsonObject hooks ? hooks.ToList() : [])
        {
            for (var i = 0; node is JsonArray list && i < list.Count; i++)
            {
                if (list[i] is JsonObject hook)
                {
                    yield return (hook, Indexed($"{entity}.hooks.{point}", i));
                }
            }
        }
    }
}
