using System.Text.Json.Nodes;

namespace MMLib.Alvo.Admin.Internal;

/* An entity's hooks: an ordered list per hook point, addressed by position like the indexes. */
internal sealed partial class WorkingCopy
{
    /// <summary>
    /// Declares one hook at one point.
    /// </summary>
    /// <remarks>
    /// <b>Appended, like an index, and for the same reason</b>: a point's value is an ordered list and the
    /// order is the order they run in, so a writer that replaced it would both drop what it cannot draw and
    /// silently reorder what it can.
    /// </remarks>
    /// <param name="entity">The entity the hook is on.</param>
    /// <param name="point">The hook point — <c>beforeCreate</c> through <c>afterDelete</c>.</param>
    /// <param name="condition">The CEL guard, or empty for a hook that always runs.</param>
    /// <param name="action">The action, already in the schema's own shape.</param>
    public void AddHook(string entity, string point, string? condition, JsonObject action)
    {
        ArgumentNullException.ThrowIfNull(action);

        Edit(root =>
        {
            if (root["entities"]?[entity] is not JsonObject declared)
            {
                return false;
            }

            var hooks = Ensure(declared, "hooks");
            if (hooks[point] is not JsonArray list)
            {
                list = [];
                hooks[point] = list;
            }

            var hook = new JsonObject();

            /* Condition before action, because that is the order the schema lists them and the order a
               reader of the committed file wants: what guards this, then what it does. */
            if (!string.IsNullOrWhiteSpace(condition))
            {
                hook["condition"] = condition;
            }

            hook["action"] = action;
            list.Add(hook);
            return true;
        });
    }

    /// <summary>
    /// Removes the hook at one position of one point.
    /// </summary>
    /// <remarks>
    /// By position for <see cref="RemoveIndex"/>'s reason — nothing forbids two hooks with the same action —
    /// and an emptied point goes with it, then an emptied <c>hooks</c> block, so removing the only hook
    /// leaves the document an author would have written rather than two empty containers. Checked against
    /// what the screen rendered, for <see cref="RemoveIndex"/>'s reason: another tab may have moved the list.
    /// </remarks>
    /// <param name="entity">The entity.</param>
    /// <param name="point">The hook point.</param>
    /// <param name="position">The hook's position within that point.</param>
    /// <param name="expectedList">
    /// The point's list as the screen rendered it (<see cref="HooksOf"/>'s shape), or <see langword="null"/> to
    /// skip the check. The whole list rather than one entry, because a hook is only addressable by its place in it.
    /// </param>
    /// <returns><see langword="true"/> when a hook was removed.</returns>
    public bool RemoveHook(string entity, string point, int position, string? expectedList = null) => Edit(root =>
    {
        if (root["entities"]?[entity] is not JsonObject declared
            || declared["hooks"] is not JsonObject hooks
            || hooks[point] is not JsonArray list
            || position < 0
            || position >= list.Count
            || (expectedList is not null
                && !string.Equals(Readable(list, "[]"), expectedList, StringComparison.Ordinal)))
        {
            return false;
        }

        list.RemoveAt(position);

        if (list.Count == 0)
        {
            hooks.Remove(point);
        }

        if (hooks.Count == 0)
        {
            declared.Remove("hooks");
        }

        return true;
    });

    /// <summary>
    /// The hooks an entity declares in the working copy: hook point to the raw JSON of its list.
    /// </summary>
    /// <remarks>
    /// The same shape <c>DescriptorLens.Hooks</c> reads off the applied descriptor, so the tab renders one
    /// or the other without knowing which — and reads the working copy, because an editor whose additions
    /// do not appear until the apply is worse than no editor.
    /// </remarks>
    /// <param name="entity">The entity.</param>
    public IReadOnlyList<KeyValuePair<string, string>> HooksOf(string entity)
        => Read<IReadOnlyList<KeyValuePair<string, string>>>(
            () => _working?["entities"]?[entity]?["hooks"] is JsonObject hooks
                ? [.. hooks.Select(pair => new KeyValuePair<string, string>(
                    pair.Key, Readable(pair.Value, "[]")))]
                : []);

    /// <summary>The hooks, by point and position, this copy declared and the applied revision does not.</summary>
    /// <param name="entity">The entity's name in the working copy.</param>
    public IReadOnlySet<(string Point, int Position)> StagedHooksOf(string entity) => Read(() => HooksStagedOn(entity));

    private HashSet<(string Point, int Position)> HooksStagedOn(string entity)
    {
        var applied = AppliedEntityOf(entity)?["hooks"] as JsonObject;
        var working = WorkingEntityOf(entity)?["hooks"] as JsonObject ?? [];

        return working
            .SelectMany(point => StagedChanges
                .NewEntries(applied?[point.Key] as JsonArray, point.Value as JsonArray)
                .Select(position => (point.Key, position)))
            .ToHashSet();
    }
}
