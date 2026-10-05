using System.Text.Json.Nodes;

namespace MMLib.Alvo.Admin.Components.Schema;

/// <summary>
/// Writes what the hook editor holds onto the hook it opened — or into a new one — changing only what the form changed.
/// </summary>
/// <remarks>
/// <para>
/// <b>A patch, never a rebuild</b> (ruling B1, spec §5.3). The editor only opens a hook it can draw whole
/// (<see cref="HookShape"/>), so a rebuild would lose nothing — except the author's key order, and Preview would then show
/// every reordered line as a change nobody made. The condition is replaced where it stands; the action is merged in place
/// while its kind is unchanged and replaced when the operator chose another kind (D2).
/// </para>
/// <para>A new hook is written condition first, the order <see cref="WorkingCopy.AddHook"/> writes.</para>
/// </remarks>
internal static class HookPatch
{
    /// <summary>The hook to write.</summary>
    /// <param name="original">The hook as declared, or <see langword="null"/> for a new one; not modified.</param>
    /// <param name="condition">The condition, or blank for a hook that always runs.</param>
    /// <param name="action">The action the form built; not modified.</param>
    public static JsonObject Apply(JsonObject? original, string? condition, JsonObject action)
    {
        ArgumentNullException.ThrowIfNull(action);
        if (original?.DeepClone() is not JsonObject hook)
        {
            return Fresh(condition, action);
        }

        SetCondition(hook, condition);
        hook["action"] = hook["action"] is JsonObject old && KindOf(old) == KindOf(action)
            ? Merged(old, action)
            : action.DeepClone();
        return hook;
    }

    private static JsonObject Fresh(string? condition, JsonObject action)
    {
        var hook = new JsonObject();
        if (!string.IsNullOrWhiteSpace(condition))
        {
            hook["condition"] = condition;
        }

        hook["action"] = action.DeepClone();
        return hook;
    }

    private static void SetCondition(JsonObject hook, string? condition)
    {
        if (string.IsNullOrWhiteSpace(condition))
        {
            hook.Remove("condition");
            return;
        }

        if (hook.ContainsKey("condition"))
        {
            hook["condition"] = condition;
            return;
        }

        var rest = hook.Select(pair => (pair.Key, Value: pair.Value?.DeepClone())).ToList();
        hook.Clear();
        hook["condition"] = condition;
        foreach (var (key, value) in rest)
        {
            hook[key] = value;
        }
    }

    private static string? KindOf(JsonObject action)
        => action.ContainsKey(HookBuilder.Reject) ? HookBuilder.Reject
            : action.ContainsKey(HookBuilder.Mutate) ? HookBuilder.Mutate
            : action["type"] is JsonValue type && type.TryGetValue<string>(out var text) ? text : null;

    private static JsonObject Merged(JsonObject old, JsonObject action)
    {
        var merged = (JsonObject)old.DeepClone();
        foreach (var key in merged.Select(pair => pair.Key).Where(key => !action.ContainsKey(key)).ToList())
        {
            merged.Remove(key);
        }

        foreach (var (key, value) in action)
        {
            merged[key] = key == HookBuilder.Mutate && merged[key] is JsonObject before && value is JsonObject after
                ? Ordered(before, after)
                : value?.DeepClone();
        }

        return merged;
    }

    /// <summary>The new patch with the fields it keeps in the old order, then the fields it adds.</summary>
    private static JsonObject Ordered(JsonObject before, JsonObject after)
    {
        var patch = new JsonObject();
        foreach (var key in before.Select(pair => pair.Key).Where(after.ContainsKey))
        {
            patch[key] = after[key]?.DeepClone();
        }

        foreach (var (key, value) in after.Where(pair => !before.ContainsKey(pair.Key)))
        {
            patch[key] = value?.DeepClone();
        }

        return patch;
    }
}
