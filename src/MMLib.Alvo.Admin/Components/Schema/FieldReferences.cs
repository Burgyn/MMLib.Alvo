using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace MMLib.Alvo.Admin.Components.Schema;

/// <summary>
/// Every place a descriptor names one field, so a rename can carry them and a removal can name them first.
/// </summary>
/// <remarks>
/// <para>
/// <b>A rename that left these behind was a plan the apply refuses</b> (docs/todo-admin.md §8d item 16): a child
/// rollup's <c>field</c>/<c>via</c> naming a field that is gone (<c>RollupResolver.cs:252-327</c>), a mutate key
/// that is not a field (BHC), a rule or computed expression compiled against columns that no longer include the
/// name, an index over a missing column. The <see cref="EntityReferences"/> pattern, one level down.
/// </para>
/// <para>
/// <b>The places, from the frozen schema:</b> the entity's <c>indexes[].fields</c>; its <c>rules</c>; its fields'
/// <c>computed</c>, <c>hidden</c>/<c>readOnly</c>/<c>validation</c> CEL and <c>default.$cel</c>; its hooks'
/// <c>condition</c>, <c>mutate</c> keys and <c>$cel</c> values, and the <c>{{new.…}}</c>/<c>{{old.…}}</c>
/// placeholders of its after-actions; and every rollup whose <c>from</c> is this entity — its <c>field</c>,
/// <c>via</c> and <c>where</c>. Top-level templates are shared between entities and are not attributed.
/// </para>
/// </remarks>
internal static class FieldReferences
{
    /// <summary>A name no field can have: a find is a rename to it that must change nothing it keeps.</summary>
    private const string Probe = "\u0001";

    private static readonly string[] _celFacets = ["computed", "hidden", "readOnly", "validation"];
    private static readonly string[] _rollupKeys = ["field", "via"];

    /// <summary>Every place that names the field; nothing is changed.</summary>
    public static IReadOnlyList<DescriptorReference> Of(JsonObject root, string entity, string field)
        => [.. Walk(root, entity, field, to: null).Select(found => found.Reference)];

    /// <summary>Renames the field everywhere it safely can, and answers with the places it could not.</summary>
    public static IReadOnlyList<DescriptorReference> Rename(JsonObject root, string entity, string from, string to)
        => [.. Walk(root, entity, from, to).Where(found => !found.Carried).Select(found => found.Reference)];

    private static List<Found> Walk(JsonObject root, string entity, string field, string? to)
    {
        var at = new Site(entity, field, to, []);
        if (root["entities"] is not JsonObject entities)
        {
            return at.Found;
        }

        if (entities[entity] is JsonObject declared)
        {
            Indexes(declared, at);
            Rules(declared, at);
            FieldExpressions(declared, at);
            Hooks(declared, at);
        }

        Rollups(entities, at);
        return at.Found;
    }

    private static void Indexes(JsonObject declared, Site at)
    {
        if (declared["indexes"] is not JsonArray indexes)
        {
            return;
        }

        for (var i = 0; i < indexes.Count; i++)
        {
            if (indexes[i]?["fields"] is JsonArray fields && fields.Any(name => Is(name, at.Field)))
            {
                Replace(fields, at);
                at.Add(string.Create(System.Globalization.CultureInfo.InvariantCulture, $"{at.Entity}.indexes[{i}]"), blocks: true);
            }
        }
    }

    private static void Rules(JsonObject declared, Site at)
    {
        if (declared["rules"] is JsonObject rules)
        {
            foreach (var operation in rules.Select(pair => pair.Key).ToList())
            {
                Expression(rules, operation, $"{at.Entity}.rules.{operation}", at);
            }
        }
    }

    /// <summary>Each field's own CEL facets — except, for a removal, the removed field's, which go with it.</summary>
    private static void FieldExpressions(JsonObject declared, Site at)
    {
        if (declared["fields"] is not JsonObject fields)
        {
            return;
        }

        foreach (var (name, node) in fields.ToList())
        {
            if (node is not JsonObject field || (at.To is null && name == at.Field))
            {
                continue;
            }

            foreach (var facet in _celFacets)
            {
                Expression(field, facet, $"{at.Entity}.fields.{name}.{facet}", at);
            }

            if (field["default"] is JsonObject tagged)
            {
                Expression(tagged, "$cel", $"{at.Entity}.fields.{name}.default", at);
            }
        }
    }

    private static void Hooks(JsonObject declared, Site at)
    {
        if (declared["hooks"] is not JsonObject hooks)
        {
            return;
        }

        foreach (var (point, node) in hooks.ToList())
        {
            for (var i = 0; node is JsonArray list && i < list.Count; i++)
            {
                if (list[i] is JsonObject hook)
                {
                    Hook(hook, string.Create(System.Globalization.CultureInfo.InvariantCulture, $"{at.Entity}.hooks.{point}[{i}]"), at);
                }
            }
        }
    }

    private static void Hook(JsonObject hook, string place, Site at)
    {
        Expression(hook, "condition", $"{place}.condition", at);
        if (hook["action"] is not JsonObject action)
        {
            return;
        }

        if (action["mutate"] is JsonObject mutate)
        {
            Mutate(mutate, $"{place}.mutate", at);
            return;
        }

        Placeholders(action, $"{place}.action", at);
    }

    private static void Mutate(JsonObject mutate, string place, Site at)
    {
        foreach (var (key, value) in mutate.ToList())
        {
            if (value is JsonObject tagged)
            {
                Expression(tagged, "$cel", $"{place}.{key}", at);
            }
        }

        if (!mutate.ContainsKey(at.Field))
        {
            return;
        }

        /* A mutate that already sets the new name as well cannot be merged by a rename — it is named instead. */
        if (at.To is { } to && mutate.ContainsKey(to))
        {
            at.Found.Add(new(new(place, Blocks: true), Carried: false));
            return;
        }

        if (at.To is { } target)
        {
            Rekey(mutate, at.Field, target);
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

    private static void Rollups(JsonObject entities, Site at)
    {
        foreach (var (parent, node) in entities.ToList())
        {
            if (node?["fields"] is not JsonObject fields)
            {
                continue;
            }

            foreach (var (name, field) in fields.ToList())
            {
                if (field?["rollup"] is JsonObject rollup && Is(rollup["from"], at.Entity))
                {
                    Rollup(rollup, $"{parent}.fields.{name}.rollup", at);
                }
            }
        }
    }

    private static void Rollup(JsonObject rollup, string place, Site at)
    {
        Expression(rollup, "where", $"{place}.where", at);
        foreach (var key in _rollupKeys.Where(key => Is(rollup[key], at.Field)))
        {
            if (at.To is { } to)
            {
                rollup[key] = to;
            }

            at.Add($"{place}.{key}", blocks: true);
        }
    }

    /// <summary>One CEL slot: rewritten token by token when that is safe; otherwise named, and not blocking.</summary>
    private static void Expression(JsonObject owner, string key, string place, Site at)
    {
        if (owner[key] is not JsonValue value || !value.TryGetValue<string>(out var cel))
        {
            return;
        }

        if (CelNames.Rename(cel, at.Field, at.To ?? Probe) is not { } renamed)
        {
            if (CelNames.MayName(cel, at.Field))
            {
                at.Found.Add(new(new(place, Blocks: false), Carried: false));
            }

            return;
        }

        if (!string.Equals(renamed, cel, StringComparison.Ordinal))
        {
            if (at.To is not null)
            {
                owner[key] = renamed;
            }

            at.Add(place, blocks: true);
        }
    }

    private static void Replace(JsonArray fields, Site at)
    {
        for (var i = 0; at.To is { } to && i < fields.Count; i++)
        {
            if (Is(fields[i], at.Field))
            {
                fields[i] = to;
            }
        }
    }

    /// <summary>Moves a key without moving its position — <c>WorkingCopy.Rekey</c>'s reason.</summary>
    private static void Rekey(JsonObject owner, string from, string to)
    {
        var order = owner.Select(pair => (Key: pair.Key == from ? to : pair.Key, Value: pair.Value?.DeepClone())).ToList();
        owner.Clear();
        foreach (var (key, value) in order)
        {
            owner[key] = value;
        }
    }

    private static Regex Placeholder(string field)
        => new(@"(\{\{\s*(?:new|old)\.)" + Regex.Escape(field) + @"(\s*\}\})", RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));

    private static bool Is(JsonNode? node, string expected)
        => node is JsonValue value && value.TryGetValue<string>(out var text) && text == expected;

    /// <summary>What one walk is about, and what it found.</summary>
    private sealed record Site(string Entity, string Field, string? To, List<Found> Found)
    {
        /// <summary>A place found; carried when this walk renames, since every caller rewrote it first.</summary>
        public void Add(string place, bool blocks) => Found.Add(new(new(place, blocks), Carried: To is not null));
    }

    private sealed record Found(DescriptorReference Reference, bool Carried);
}
