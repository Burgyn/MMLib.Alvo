using Microsoft.AspNetCore.Components;

namespace MMLib.Alvo.Admin.Internal;

/// <summary>
/// What an entity's tabs need to badge their staged rows, cascaded from the entity screen.
/// </summary>
/// <remarks>
/// <b>Cascaded, and therefore internal.</b> A component's <c>[Parameter]</c> must be public, and the Razor SDK
/// makes every component public, so a parameter per tab would have put the staging model into the package's
/// published surface. A <c>[CascadingParameter]</c> may be private: the tabs read this without anything about
/// it becoming a contract a host can depend on.
/// </remarks>
/// <param name="fields">Each field the working copy changed, with how; an unchanged field is absent.</param>
/// <param name="indexes">The index positions the applied revision does not hold.</param>
/// <param name="hooks">The hooks, by point and position, the applied revision does not hold.</param>
/// <param name="restore">Puts a removed field back from the applied revision.</param>
internal sealed class StagedView(
    IReadOnlyDictionary<string, StagedChange> fields,
    IReadOnlySet<int> indexes,
    IReadOnlySet<(string Point, int Position)> hooks,
    EventCallback<string> restore)
{
    /// <summary>Nothing staged — what a tab reads when no entity screen cascaded one.</summary>
    public static StagedView None { get; } = new(
        new Dictionary<string, StagedChange>(), new HashSet<int>(), new HashSet<(string, int)>(),
        EventCallback<string>.Empty);

    /// <summary>Puts a removed field back from the applied revision.</summary>
    public EventCallback<string> Restore { get; } = restore;

    /// <summary>How the working copy changed one field.</summary>
    /// <param name="field">The field's name.</param>
    public StagedChange FieldChange(string field) => fields.GetValueOrDefault(field);

    /// <summary>Whether the index at one position exists only in the working copy.</summary>
    /// <param name="position">Its position in the declared array.</param>
    public bool IsNewIndex(int position) => indexes.Contains(position);

    /// <summary>Whether the hook at one point and position exists only in the working copy.</summary>
    /// <param name="point">The hook point.</param>
    /// <param name="position">Its position within the point.</param>
    public bool IsNewHook(string point, int position) => hooks.Contains((point, position));
}
