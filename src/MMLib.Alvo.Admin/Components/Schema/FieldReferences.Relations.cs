using System.Text.Json.Nodes;

namespace MMLib.Alvo.Admin.Components.Schema;

/* The rules and lookup checks that may read the field: its own entity's, and those of every entity a relation path
   joins to it (docs/architecture/relation-rules.md). */
internal static partial class FieldReferences
{
    private static void Relations(JsonObject entities, Site at)
    {
        foreach (var (name, declared) in Members(entities))
        {
            if (ReachOf(entities, name, declared, at) is { } reach)
            {
                Rules(declared, reach, at);
                Checks(declared, reach, at);
            }
        }
    }

    /// <summary>
    /// How <paramref name="name"/>'s relation slots read the field, or <see langword="null"/> when no path joins them.
    /// </summary>
    /// <remarks>
    /// The entity itself reads it bare, through <c>new</c>/<c>old</c> and through a self-ref, and a ref field heads a
    /// path. Another entity reads it only as <c>&lt;ref&gt;.&lt;field&gt;</c> through one of its refs to this entity, or
    /// inside <c>&lt;child&gt;.exists(…)</c> when this entity refs it — a binding macro, so named rather than rewritten,
    /// and only when the macro over this entity names it.
    /// </remarks>
    private static Reach? ReachOf(JsonObject entities, string name, JsonObject declared, Site at)
    {
        var refs = OldNames(RefsTo(declared, at.Entity), name, at);
        var children = entities[at.Entity] is JsonObject own ? OldNames(RefsTo(own, name), at.Entity, at) : [];
        var severed = Severed(children, at);
        if (name == at.Entity)
        {
            var spelling = new CelSpelling(Bare: true, [.. CelSpelling.Images, .. refs], Head: IsRef(declared, at));
            return new(name, new(spelling, cel => MayName(cel, at)), severed);
        }

        var receivers = Receivers(children, at);
        return refs.Count > 0 || children.Count > 0
            ? new(name, new(new(Bare: false, refs, Head: false), cel => MayReadThrough(cel, refs, receivers, at)), severed)
            : null;
    }

    /// <summary>
    /// The <c>exists</c> receivers a removal leaves unresolved: <c>&lt;child&gt;_via_&lt;ref&gt;</c> for the removed ref,
    /// and the bare <c>&lt;child&gt;</c> when that ref was the child's only one to the parent — the rollup's implicit
    /// <c>via</c>, for rules (<c>RelationCatalogBuilder</c> resolves it the same way).
    /// </summary>
    private static List<string> Severed(List<string> children, Site at)
    {
        if (at.To is not null || !children.Contains(at.Field))
        {
            return [];
        }

        return children.Count == 1 ? [at.Entity, Via(at.Entity, at.Field)] : [Via(at.Entity, at.Field)];
    }

    private static bool MayReadThrough(string cel, List<string> refs, List<string> receivers, Site at)
        => refs.Any(reference => CelNames.MayReadThrough(cel, reference, at.Field))
            || receivers.Any(receiver => CelNames.ExistsBodies(cel, receiver).Any(body => CelNames.MayName(body, at.Field)))
            || CelNames.MayName(cel, Via(at.Entity, at.Field));

    /// <summary>How the parent spells this entity as a child: by its name, or <c>&lt;child&gt;_via_&lt;ref&gt;</c> per ref.</summary>
    private static List<string> Receivers(List<string> children, Site at)
        => children.Count == 0 ? [] : [at.Entity, .. children.Select(reference => Via(at.Entity, reference))];

    private static void Rules(JsonObject declared, Reach reach, Site at)
    {
        if (declared["rules"] is JsonObject rules)
        {
            foreach (var operation in rules.Select(pair => pair.Key).ToList())
            {
                RelationSlot(rules, operation, $"{reach.Entity}.rules.{operation}", reach, at);
            }
        }
    }

    /// <summary>Each lookup check: its <c>lookup</c> names a ref of its own entity, its <c>expr</c> reads through it.</summary>
    private static void Checks(JsonObject declared, Reach reach, Site at)
    {
        if (declared["checks"] is not JsonArray checks)
        {
            return;
        }

        for (var i = 0; i < checks.Count; i++)
        {
            if (checks[i] is not JsonObject check)
            {
                continue;
            }

            var place = Indexed($"{reach.Entity}.checks", i);
            if (reach.Entity == at.Entity)
            {
                Name(check, "lookup", $"{place}.lookup", at);
            }

            RelationSlot(check, "expr", $"{place}.expr", reach, at);
        }
    }

    /// <summary>A rule or check: blocking when the removal leaves one of its <c>exists</c> receivers unresolved, otherwise one CEL slot.</summary>
    private static void RelationSlot(JsonObject owner, string key, string place, Reach reach, Site at)
    {
        if (Text(owner, key) is { } cel && reach.Severed.Any(receiver => CelNames.ExistsBodies(cel, receiver).Any()))
        {
            at.Add(place, blocks: true);
            return;
        }

        Expression(owner, key, place, reach.Reading, at);
    }

    private static List<string> RefsTo(JsonObject declared, string target)
        => [.. Members(declared["fields"]).Where(field => IsRefTo(field.Value, target)).Select(field => field.Name)];

    /// <summary>The field's entity's refs as the expressions still spell them: a rename has already moved the field.</summary>
    private static List<string> OldNames(List<string> refs, string entity, Site at)
        => entity == at.Entity && at.To is { } to ? [.. refs.Select(reference => reference == to ? at.Field : reference)] : refs;

    private static bool IsRefTo(JsonObject field, string target) => Is(field["type"], "ref") && Is(field["entity"], target);

    /// <summary>Whether the field is a ref — under its new name once a rename has moved it.</summary>
    private static bool IsRef(JsonObject declared, Site at)
        => declared["fields"]?[at.To ?? at.Field] is JsonObject field && Is(field["type"], "ref");

    private static string Via(string child, string reference) => $"{child}_via_{reference}";

    /// <summary>One entity's relation slots: whose they are, how they read the field, and the receivers a removal severs.</summary>
    private sealed record Reach(string Entity, Reading Reading, List<string> Severed);
}
