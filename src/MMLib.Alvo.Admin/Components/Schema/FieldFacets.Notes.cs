using MMLib.Alvo.Schema;
using System.Text.Json.Nodes;

namespace MMLib.Alvo.Admin.Components.Schema;

/* What the editor says about the facets it draws no control for (docs/todo-admin.md §8d item 17). */
internal sealed partial class FieldFacets
{
    /// <summary>The keys a control draws; every other key a declaration carries becomes a note.</summary>
    private static readonly HashSet<string> _drawn = new(StringComparer.Ordinal)
    {
        "type", "required", "unique", "index", "maxLength", "precision", "scale", "values", "entity", "default",
    };

    /// <summary>Every declared facet this editor does not draw, with what the save does to it.</summary>
    public IReadOnlyList<FacetNote> Notes()
    {
        List<FacetNote> notes = [.. _declared.Where(pair => !_drawn.Contains(pair.Key)).Select(pair => Undrawn(pair.Key, pair.Value))];

        if (DefaultNote() is { } defaulted)
        {
            notes.Insert(0, defaulted);
        }

        if (Type == FieldType.Ref && !_declared.ContainsKey("onDelete"))
        {
            notes.Add(new("onDelete", "\"restrict\"", FacetFate.Written,
                "the schema's default, written for a new ref — choosing another is #265."));
        }

        return notes;
    }

    /// <summary>One undrawn facet: removed when it belongs to a type the field no longer has, else kept.</summary>
    private FacetNote Undrawn(string facet, JsonNode? value)
    {
        var json = value?.ToJsonString() ?? "null";

        return _typedFacets.Contains(facet) && OwnerOf(facet) != Type
            ? new(facet, json, FacetFate.Removed, $"belongs to a {Word(OwnerOf(facet))}, which this field no longer is.")
            : new(facet, json, FacetFate.Kept, "no control here — kept exactly as declared.");
    }

    /// <summary>The declared default, when no box decides it.</summary>
    private FacetNote? DefaultNote()
    {
        if (_declared["default"] is not { } declared || DefaultFate() is not { } fate)
        {
            return null;
        }

        var reason = fate == FacetFate.Kept ? KeptDefaultReason(declared) : RemovedDefaultReason();
        return new("default", declared.ToJsonString(), fate, reason);
    }

    private string KeptDefaultReason(JsonNode declared)
        => IsCel(declared) ? "a '$cel' expression, which this build refuses at apply — tick \"Remove the declared default\" to save the field."
            : MaintainedElsewhere ? "beside a value that is maintained for it, which the apply refuses — tick \"Remove the declared default\" to save the field."
            : $"no control draws a {Word(Type)} default — kept exactly as declared.";

    private string RemovedDefaultReason()
        => RemoveUndrawnDefault ? "removed, as asked."
            : $"a {Word(Type)} takes no default here, so it does not follow the field into its new type.";
}
