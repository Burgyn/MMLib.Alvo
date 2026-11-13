using System.Globalization;
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
/// <b>The places, from the frozen schema:</b> the entity's <c>indexes[]</c> — <c>fields</c>, <c>where</c> and
/// <c>exclude.from</c>/<c>to</c>; its <c>events.exclude</c>; its <c>stateMachine.field</c> and each transition's
/// <c>when</c>; its <c>timers</c>' <c>at</c> and <c>when</c>; its fields' <c>computed</c>,
/// <c>hidden</c>/<c>readOnly</c>/<c>validation</c> CEL, <c>default.$cel</c> and <c>sequence.per</c>; its hooks'
/// <c>condition</c>, <c>mutate</c> keys and <c>$cel</c> values, and the <c>{{new.…}}</c>/<c>{{old.…}}</c>
/// placeholders and <c>$cel</c> values of its after-actions; and every rollup whose <c>from</c> is this entity — its
/// <c>field</c>, <c>via</c> and <c>where</c>. Beyond the entity (<c>FieldReferences.Relations.cs</c>,
/// <c>FieldReferences.Actions.cs</c>): the <c>rules</c> and <c>checks</c> of every entity that may read it through a
/// relation path, each automation rule its events trigger, and every <c>entity.update</c> payload that writes it.
/// Top-level templates are shared between entities and are not attributed.
/// </para>
/// </remarks>
internal static partial class FieldReferences
{
    /// <summary>A name no field can have: a find is a rename to it that must change nothing it keeps.</summary>
    private const string Probe = "\u0001";

    private static readonly string[] _celFacets = ["computed", "hidden", "readOnly", "validation"];
    private static readonly string[] _rollupKeys = ["field", "via"];
    private static readonly string[] _rangeKeys = ["from", "to"];
    private static readonly string[] _timerSlots = ["at", "when"];

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
            Own(declared, at);
        }

        Relations(entities, at);
        Rollups(entities, at);
        UpdateTargets(root, entities, at);
        Automation(root, at);
        return at.Found;
    }

    private static void Own(JsonObject declared, Site at)
    {
        Indexes(declared, at);
        EventExclusions(declared, at);
        StateMachine(declared, at);
        Timers(declared, at);
        FieldExpressions(declared, at);
        Hooks(declared, at);
    }

    private static void Indexes(JsonObject declared, Site at)
    {
        if (declared["indexes"] is not JsonArray indexes)
        {
            return;
        }

        for (var i = 0; i < indexes.Count; i++)
        {
            if (indexes[i] is JsonObject index)
            {
                Index(index, Indexed($"{at.Entity}.indexes", i), at);
            }
        }
    }

    private static void Index(JsonObject index, string place, Site at)
    {
        if (index["fields"] is JsonArray fields && fields.Any(name => Is(name, at.Field)))
        {
            Replace(fields, at);
            at.Add(place, blocks: true);
        }

        Expression(index, "where", $"{place}.where", at);
        if (index["exclude"] is JsonObject range)
        {
            foreach (var key in _rangeKeys)
            {
                Name(range, key, $"{place}.exclude.{key}", at);
            }
        }
    }

    /// <summary>The entity's <c>events.exclude</c>: carried by a rename, and blocking a removal (apply refuses a name that is no field).</summary>
    private static void EventExclusions(JsonObject declared, Site at)
    {
        if (declared["events"]?["exclude"] is JsonArray excluded && excluded.Any(name => Is(name, at.Field)))
        {
            Replace(excluded, at);
            at.Add($"{at.Entity}.events.exclude", blocks: true);
        }
    }

    /// <summary>The machine's <c>field</c>, and each transition's <c>when</c> over <c>old</c>/<c>new</c>.</summary>
    private static void StateMachine(JsonObject declared, Site at)
    {
        if (declared["stateMachine"] is not JsonObject machine)
        {
            return;
        }

        var place = $"{at.Entity}.stateMachine";
        Name(machine, "field", $"{place}.field", at);
        foreach (var (name, transition) in Members(machine["transitions"]))
        {
            Expression(transition, "when", $"{place}.transitions.{name}.when", at);
        }
    }

    private static void Timers(JsonObject declared, Site at)
    {
        foreach (var (name, timer) in Members(declared["timers"]))
        {
            foreach (var slot in _timerSlots)
            {
                Expression(timer, slot, $"{at.Entity}.timers.{name}.{slot}", at);
            }
        }
    }

    /// <summary>Each field's own facets — except, for a removal, the removed field's, which go with it.</summary>
    private static void FieldExpressions(JsonObject declared, Site at)
    {
        foreach (var (name, field) in Members(declared["fields"]))
        {
            if (at.To is not null || name != at.Field)
            {
                FieldFacets(field, $"{at.Entity}.fields.{name}", at);
            }
        }
    }

    private static void FieldFacets(JsonObject field, string place, Site at)
    {
        foreach (var facet in _celFacets)
        {
            Expression(field, facet, $"{place}.{facet}", at);
        }

        if (field["default"] is JsonObject tagged)
        {
            Expression(tagged, "$cel", $"{place}.default", at);
        }

        if (field["sequence"] is JsonObject sequence)
        {
            Name(sequence, "per", $"{place}.sequence.per", at);
        }
    }

    private static void Hooks(JsonObject declared, Site at)
    {
        foreach (var (hook, place) in HooksOf(declared, at.Entity))
        {
            Hook(hook, place, at);
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
            TaggedValues(mutate, $"{place}.mutate", at);
            Keys(mutate, $"{place}.mutate", at);
            return;
        }

        RowReads(action, $"{place}.action", at);
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
                    ImplicitVia(rollup, parent, entities, $"{parent}.fields.{name}.rollup.via", at);
                }
            }
        }
    }

    /// <summary>
    /// A rollup with no <c>via</c> rolls up through the child's one ref to the parent, so removing that ref
    /// leaves it nothing to roll up through.
    /// </summary>
    /// <remarks>
    /// <c>RollupResolver.ResolveVia</c> (<c>src/MMLib.Alvo/Descriptor/Internal/RollupResolver.cs</c>) resolves an
    /// absent <c>via</c> to the child's single field whose <c>entity</c> is the parent, and refuses the descriptor
    /// when there is none. A rename needs nothing here: no key names the field, and the resolution follows it.
    /// </remarks>
    private static void ImplicitVia(JsonObject rollup, string parent, JsonObject entities, string place, Site at)
    {
        if (at.To is null && !rollup.ContainsKey("via")
            && entities[at.Entity]?["fields"]?[at.Field] is JsonObject field
            && Is(field["type"], "ref") && Is(field["entity"], parent))
        {
            at.Add(place, blocks: true);
        }
    }

    private static void Rollup(JsonObject rollup, string place, Site at)
    {
        Expression(rollup, "where", $"{place}.where", at);
        foreach (var key in _rollupKeys)
        {
            Name(rollup, key, $"{place}.{key}", at);
        }
    }

    /// <summary>One slot that holds the field's name as plain text: carried by a rename, and blocking a removal.</summary>
    private static void Name(JsonObject owner, string key, string place, Site at)
    {
        if (!Is(owner[key], at.Field))
        {
            return;
        }

        if (at.To is { } to)
        {
            owner[key] = to;
        }

        at.Add(place, blocks: true);
    }

    /// <inheritdoc cref="Expression(JsonObject, string, string, CelSpelling, Site)"/>
    private static void Expression(JsonObject owner, string key, string place, Site at)
        => Expression(owner, key, place, CelSpelling.Own, at);

    /// <summary>One CEL slot: rewritten token by token when that is safe; otherwise named, and not blocking.</summary>
    private static void Expression(JsonObject owner, string key, string place, CelSpelling spelling, Site at)
    {
        if (owner[key] is not JsonValue value || !value.TryGetValue<string>(out var cel))
        {
            return;
        }

        if (CelNames.Rename(cel, at.Field, at.To ?? Probe, spelling) is not { } renamed)
        {
            if (MayName(cel, at))
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

    /// <summary>Whether a declined expression may name the field — bare, as a member, or as the ref of a <c>&lt;child&gt;_via_&lt;ref&gt;</c> receiver.</summary>
    private static bool MayName(string cel, Site at)
        => CelNames.MayName(cel, at.Field) || CelNames.MayName(cel, $"{at.Entity}_via_{at.Field}");

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

    /// <summary>The members of an object whose values are objects, captured first so a rewrite may replace them.</summary>
    private static List<(string Name, JsonObject Value)> Members(JsonNode? node)
        => node is JsonObject owner
            ? [.. owner.Where(pair => pair.Value is JsonObject).Select(pair => (pair.Key, (JsonObject)pair.Value!))]
            : [];

    private static string Indexed(string place, int index) => string.Create(CultureInfo.InvariantCulture, $"{place}[{index}]");

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
