using MMLib.Alvo.Schema;

using System.Text.Json.Nodes;

namespace MMLib.Alvo.Admin.Components.Schema;

/* An entity's indexes: an ordered array, addressed by position and checked against what the screen drew. */
internal sealed partial class WorkingCopy
{
    /// <summary>
    /// Declares one index on an entity.
    /// </summary>
    /// <remarks>
    /// <b>Appended to the array the descriptor already has</b>, not written over it: the block is an ordered
    /// list and an author may have declared indexes this editor cannot draw. Replacing it would be the same
    /// silent narrowing the field editor refuses — an edit that drops what the control did not know about.
    /// </remarks>
    /// <param name="entity">The entity the index is on.</param>
    /// <param name="fields">The fields it covers, in the order they are declared in.</param>
    /// <param name="unique">Whether it enforces uniqueness across them.</param>
    public void AddIndex(string entity, IReadOnlyList<string> fields, bool unique)
    {
        ArgumentNullException.ThrowIfNull(fields);

        Edit(root =>
        {
            if (root["entities"]?[entity] is not JsonObject declared)
            {
                return false;
            }

            if (declared["indexes"] is not JsonArray indexes)
            {
                indexes = [];
                declared["indexes"] = indexes;
            }

            var index = new JsonObject
            {
                ["fields"] = new JsonArray([.. fields.Select(field => JsonValue.Create(field))]),
            };

            /* Written only when true, for the reason the field editor writes its own booleans that way:
               `unique: false` is the schema's default, and a descriptor full of defaults is a descriptor
               whose diffs stop saying what changed. */
            if (unique)
            {
                index["unique"] = true;
            }

            indexes.Add(index);
            return true;
        });
    }

    /// <summary>
    /// Removes the index at one position.
    /// </summary>
    /// <remarks>
    /// <para>
    /// By position rather than by the fields it covers, because the block is an array and the schema does
    /// not forbid two entries over the same fields — removing "the one on (a, b)" would then be ambiguous
    /// in exactly the case an operator is trying to clean up.
    /// </para>
    /// <para>
    /// <b>A position is only as good as the render it came from.</b> The copy is the operator's, shared by
    /// every tab they have open, so another tab can have moved the array since this screen drew it — and a
    /// bare position would then remove somebody else's index. <paramref name="expected"/> is what the screen
    /// drew there; when the entry no longer matches, nothing is removed and the screen redraws from the copy.
    /// </para>
    /// </remarks>
    /// <param name="entity">The entity.</param>
    /// <param name="position">The index's position in the declared array.</param>
    /// <param name="expected">The index the screen rendered at that position, or <see langword="null"/> to skip the check.</param>
    /// <returns><see langword="true"/> when an index was removed.</returns>
    public bool RemoveIndex(string entity, int position, IndexSchema? expected = null) => Edit(root =>
    {
        if (root["entities"]?[entity] is not JsonObject declared
            || declared["indexes"] is not JsonArray indexes
            || position < 0
            || position >= indexes.Count
            || (expected is not null && !SameIndex(Index(indexes[position]), expected)))
        {
            return false;
        }

        indexes.RemoveAt(position);

        /* An empty array is not the same statement as no array, and the descriptor reads better without
           one — the same reason SetRule drops an emptied `rules`. */
        if (indexes.Count == 0)
        {
            declared.Remove("indexes");
        }

        return true;
    });

    /// <summary>The indexes an entity declares in the working copy, in the order it declares them.</summary>
    /// <remarks>
    /// Read from the working document rather than from <c>SchemaModel</c> deliberately: the tab that edits
    /// them must render what it is editing, or an unapplied index disappears the moment it is added — which
    /// is the defect the Rules tab already records for its own case.
    /// </remarks>
    /// <param name="entity">The entity.</param>
    public IReadOnlyList<IndexSchema> IndexesOf(string entity) => Read<IReadOnlyList<IndexSchema>>(
        () => _working?["entities"]?[entity]?["indexes"] is JsonArray indexes
            ? [.. indexes.Select(Index)]
            : []);

    /// <summary>The positions of the indexes this copy declared and the applied revision does not.</summary>
    /// <param name="entity">The entity's name in the working copy.</param>
    public IReadOnlySet<int> StagedIndexesOf(string entity)
        => Read(() => StagedChanges.NewEntries(
            AppliedEntityOf(entity)?["indexes"] as JsonArray, WorkingEntityOf(entity)?["indexes"] as JsonArray));

    /// <summary>Whether two readings of an index declare the same one.</summary>
    private static bool SameIndex(IndexSchema actual, IndexSchema expected)
        => actual.Unique == expected.Unique && actual.Fields.SequenceEqual(expected.Fields, StringComparer.Ordinal);

    /// <summary>
    /// One declared index, in the shape every screen already reads.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>It reads defensively, and that is not belt-and-braces.</b> <see cref="Replace"/> validates nothing
    /// beyond "is it JSON" on purpose — the apply is the authority — so an imported descriptor or an
    /// assistant's proposal can put <c>"unique": "yes"</c> or a number among the field names into the working
    /// copy. <c>JsonNode.GetValue&lt;T&gt;</c> throws <see cref="InvalidOperationException"/> for that, and
    /// <c>Entity.razor</c> reads the indexes on every tab, so one bad entry ended the circuit instead of
    /// spoiling one panel. <c>HooksTab</c> already degraded this way and this path did not.
    /// </para>
    /// <para>
    /// <b>An unreadable entry is kept, not skipped</b>, because <see cref="RemoveIndex"/> addresses the array
    /// by position: dropping it here would shift every position after it and remove the wrong index. It comes
    /// back with no fields, which the tab renders as the one thing an operator wants for it — a row they can
    /// delete.
    /// </para>
    /// </remarks>
    /// <param name="declared">The array entry.</param>
    private static IndexSchema Index(JsonNode? declared)
    {
        if (declared is not JsonObject index)
        {
            return new([], Unique: false);
        }

        return new(
            index["fields"] is JsonArray fields ? [.. fields.Select(Name).OfType<string>()] : [],
            index["unique"] is JsonValue unique && unique.TryGetValue<bool>(out var enforced) && enforced);
    }

    /// <summary>One field name, or nothing when the entry is not one.</summary>
    private static string? Name(JsonNode? field) =>
        field is JsonValue value && value.TryGetValue<string>(out var name) ? name : null;
}
