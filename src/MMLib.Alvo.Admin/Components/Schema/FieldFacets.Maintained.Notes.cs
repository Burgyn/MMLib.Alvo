using MMLib.Alvo.Schema;
using System.Text.Json.Nodes;

namespace MMLib.Alvo.Admin.Components.Schema;

/* What the editor says about a maintained field's facets that the save carries, drops or rewrites without a
   control — each one said, never silent (docs/todo-admin.md §8d item 17's rule, applied to rollup and computed). */
internal sealed partial class FieldFacets
{
    private static readonly string[] _withheld = ["required", "unique"];

    private static readonly string[] _maintainedFacets = ["rollup", "computed"];

    /// <summary>Every note the field's kind owes: withheld flags, a dropped kind, a rewritten type, a dropped rollup key.</summary>
    private IEnumerable<FacetNote> MaintainedNotes()
        => [.. Withheld(), .. DroppedKinds(), .. RetypedRollup(), .. DroppedFromRollup()];

    /// <summary>A required or unique on a maintained value: carried, and said.</summary>
    private IEnumerable<FacetNote> Withheld()
        => MaintainedElsewhere
            ? _withheld.Where(facet => Flag(_declared[facet])).Select(facet => new FacetNote(
                facet, "true", FacetFate.Kept, "not offered for a value that is maintained for it — kept as declared."))
            : [];

    /// <summary>A declared <c>rollup</c> or <c>computed</c> the current kind is not, which the save removes.</summary>
    private IEnumerable<FacetNote> DroppedKinds()
        => _maintainedFacets
            .Where(facet => _declared[facet] is not null && facet != KindFacet)
            .Select(facet => new FacetNote(facet, _declared[facet]!.ToJsonString(), FacetFate.Removed, DroppedKindReason));

    private string? KindFacet => Kind switch
    {
        FieldKind.Rollup => "rollup",
        FieldKind.Computed => "computed",
        _ => null,
    };

    private string DroppedKindReason => Kind switch
    {
        FieldKind.Rollup => "a field is a rollup or computed, never both — the apply refuses the pair, so it opens as the rollup and this is removed.",
        FieldKind.Computed => "the field is computed now, so it does not follow.",
        _ => "the field is written by callers now, so it does not follow.",
    };

    /// <summary>A declared type the rollup cannot be stored as, rewritten to the derived one.</summary>
    private IEnumerable<FacetNote> RetypedRollup()
        => Kind == FieldKind.Rollup && _declared.ContainsKey("type") && RollupType != _declaredType
            ? [new("type", JsonValue.Create(Word(RollupType)).ToJsonString(), FacetFate.Written,
                $"was {Word(_declaredType)}, which cannot hold this rollup's value — the type is derived from the rollup.")]
            : [];

    /// <summary>The rollup keys <see cref="WriteRollupObject"/> drops rather than writes.</summary>
    private IEnumerable<FacetNote> DroppedFromRollup()
    {
        if (Kind != FieldKind.Rollup)
        {
            yield break;
        }

        if (RollupOp == "count" && RollupField.Length > 0)
        {
            yield return new("rollup.field", Quoted(RollupField), FacetFate.Removed,
                "a count aggregates rows rather than a field's values — the apply ignores it, so it is removed.");
        }

        if (RollupVia.Length > 0 && Source is { Via.Count: 1 } source && !source.Via.Contains(RollupVia, StringComparer.Ordinal))
        {
            yield return new("rollup.via", Quoted(RollupVia), FacetFate.Removed,
                $"not a ref from {RollupFrom} to this entity, which the apply refuses — removed, so the apply follows {source.Via[0]}, the one ref here.");
        }
    }

    private static string Quoted(string text) => JsonValue.Create(text).ToJsonString();
}
