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
            if (RelationSpelling(entities, name, declared, at) is { } spelling)
            {
                Rules(declared, name, spelling, at);
                Checks(declared, name, spelling, at);
            }
        }
    }

    /// <summary>
    /// How <paramref name="name"/>'s relation slots spell the field, or <see langword="null"/> when no path joins them.
    /// </summary>
    /// <remarks>
    /// The entity itself reads it bare, through <c>new</c>/<c>old</c> and through a self-ref, and a ref field heads a
    /// path. Another entity reads it only as <c>&lt;ref&gt;.&lt;field&gt;</c> through one of its refs to this entity, or
    /// inside <c>&lt;child&gt;.exists(…)</c> when this entity refs it — a binding macro, so named rather than rewritten.
    /// </remarks>
    private static CelSpelling? RelationSpelling(JsonObject entities, string name, JsonObject declared, Site at)
    {
        var refs = RefsTo(declared, at.Entity);
        if (name == at.Entity)
        {
            return new(Bare: true, [.. CelSpelling.Images, .. refs], Head: IsRef(declared, at));
        }

        var reversed = entities[at.Entity] is JsonObject own && RefsTo(own, name).Count > 0;
        return refs.Count > 0 || reversed ? new(Bare: false, refs, Head: false) : null;
    }

    private static void Rules(JsonObject declared, string entity, CelSpelling spelling, Site at)
    {
        if (declared["rules"] is JsonObject rules)
        {
            foreach (var operation in rules.Select(pair => pair.Key).ToList())
            {
                Expression(rules, operation, $"{entity}.rules.{operation}", spelling, at);
            }
        }
    }

    /// <summary>Each lookup check: its <c>lookup</c> names a ref of its own entity, its <c>expr</c> reads through it.</summary>
    private static void Checks(JsonObject declared, string entity, CelSpelling spelling, Site at)
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

            var place = Indexed($"{entity}.checks", i);
            if (entity == at.Entity)
            {
                Name(check, "lookup", $"{place}.lookup", at);
            }

            Expression(check, "expr", $"{place}.expr", spelling, at);
        }
    }

    private static List<string> RefsTo(JsonObject declared, string target)
        => [.. Members(declared["fields"]).Where(field => IsRefTo(field.Value, target)).Select(field => field.Name)];

    private static bool IsRefTo(JsonObject field, string target) => Is(field["type"], "ref") && Is(field["entity"], target);

    /// <summary>Whether the field is a ref — under its new name once a rename has moved it.</summary>
    private static bool IsRef(JsonObject declared, Site at)
        => declared["fields"]?[at.To ?? at.Field] is JsonObject field && Is(field["type"], "ref");
}
