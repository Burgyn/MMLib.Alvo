using MMLib.Alvo.Management;

namespace MMLib.Alvo.Admin.Internal;

/// <summary>The screens a refusal is shown on.</summary>
internal enum RefusalScreen
{
    /// <summary>The field editor's refused-facets list.</summary>
    FieldEditor,

    /// <summary>The entity header, beside the flags.</summary>
    EntityHeader,

    /// <summary>The entity's On write tab.</summary>
    OnWrite,

    /// <summary>Integrations' "why there is no new endpoint button".</summary>
    Integrations,

    /// <summary>The Automations page.</summary>
    Automations,

    /// <summary>The Functions page.</summary>
    Functions,
}

/// <summary>
/// Which screen each refusal the build publishes belongs to — an explicit map, not a prefix.
/// </summary>
/// <remarks>
/// <para>
/// <b>Prefix matching dropped four refusals on the floor</b> (docs/todo-admin.md §8d item 19): the field editor
/// admitted <c>field.*</c> and so never <c>rollup.where</c>; <c>trigger.event</c>, <c>JSONata</c> and <c>bodyFile</c>
/// matched no screen's prefix at all; and <c>entity.update</c> is an action type that an <c>entity.*</c> prefix would
/// have drawn on the entity header. A slot is a feature's own name, and only a table can say where it belongs.
/// </para>
/// <para>
/// <b>Admin-side, deliberately.</b> The core-side answer is an owner or area on <c>ManagementRefusedFeature</c>
/// itself — a port change, and #269's to decide. Until then a slot this map does not place is <see cref="Unplaced"/>,
/// which Overview shows and an end-to-end fact asserts is empty against the real host, so a new core slot is loud.
/// The slots are the ones <c>UnhonouredFeatures.EveryRefusal</c> publishes
/// (src/MMLib.Alvo/Descriptor/Internal/UnhonouredFeatures.cs).
/// </para>
/// </remarks>
internal static class RefusalPlaces
{
    private static readonly Dictionary<string, RefusalScreen[]> _owners = new(StringComparer.Ordinal)
    {
        ["field.validation"] = [RefusalScreen.FieldEditor],
        ["field.default"] = [RefusalScreen.FieldEditor],
        ["rollup.where"] = [RefusalScreen.FieldEditor],
        ["entity.softDelete"] = [RefusalScreen.EntityHeader],
        ["function"] = [RefusalScreen.OnWrite],
        ["http.call"] = [RefusalScreen.OnWrite],
        ["entity.update"] = [RefusalScreen.OnWrite],
        ["JSONata"] = [RefusalScreen.OnWrite, RefusalScreen.Integrations],
        ["email.data"] = [RefusalScreen.OnWrite, RefusalScreen.Integrations],
        ["bodyFile"] = [RefusalScreen.Integrations],
        ["trigger.event"] = [RefusalScreen.Automations, RefusalScreen.Functions],
    };

    /// <summary>The refusals one screen shows, in the order the build published them.</summary>
    /// <param name="screen">The screen asking.</param>
    /// <param name="refused">Every refusal the build publishes, as <c>ManagementCapabilities.Refused</c> reports it.</param>
    /// <returns>The ones that belong on <paramref name="screen"/>.</returns>
    public static IReadOnlyList<ManagementRefusedFeature> On(RefusalScreen screen, IReadOnlyList<ManagementRefusedFeature> refused)
        => [.. refused.Where(refusal => _owners.TryGetValue(refusal.Slot, out var screens) && screens.Contains(screen))];

    /// <summary>The refusals no screen shows — a slot this build publishes and this map has not placed yet.</summary>
    /// <param name="refused">Every refusal the build publishes.</param>
    /// <returns>The ones no screen shows.</returns>
    public static IReadOnlyList<ManagementRefusedFeature> Unplaced(IReadOnlyList<ManagementRefusedFeature> refused)
        => [.. refused.Where(refusal => !_owners.ContainsKey(refusal.Slot))];
}
