using MMLib.Alvo.Admin.Internal;

namespace MMLib.Alvo.Admin.Components.Schema;

/* What names a field or an entity, asked before it is renamed or removed. Kept out of Entity.razor.cs so the
   screen's own file stays about its tabs. */
public partial class Entity
{
    private (string Field, IReadOnlyList<DescriptorReference> References)? _removal;
    private RenameLeftovers? _renameLeftovers;

    /// <summary>
    /// Asks before a field leaves the working copy, naming what names it.
    /// </summary>
    /// <remarks>
    /// Every removal asks, a field nothing names included (spec §3.2): the confirm is where the operator reads that
    /// the column and its data go at the apply. A field something <em>does</em> name is refused there as well —
    /// removing it composes a descriptor the apply refuses (a rollup, a mutate key, a rule compiled against a column
    /// that is gone), so the confirm refuses it until they are changed (docs/todo-admin.md §8d item 16).
    /// </remarks>
    private void RemoveField(string field) => _removal = (field, Copy.ReferencesToField(EntityName, field));

    /// <summary>
    /// Removes the field the confirm named, when nothing it named blocks the removal; focus goes to its row's Undo, or,
    /// for a field only the copy declared (its row goes with it), to the list's New field (spec §3.2).
    /// </summary>
    private Task RemoveAnyway()
    {
        var removal = _removal;
        _removal = null;
        if (removal is not { } asked || asked.References.Any(reference => reference.Blocks))
        {
            return Task.CompletedTask;
        }

        StageRemoval(asked.Field);
        return Interop.FocusFirstOnceClosedAsync([$"[data-testid='restore-field-{asked.Field}']", "[data-testid='add-field']"]);
    }

    /// <summary>Cancel and Escape keep the field, and focus goes back to its Remove.</summary>
    private Task CloseRemoval()
    {
        var removal = _removal;
        _removal = null;
        return removal is not { } asked
            ? Task.CompletedTask
            : Interop.FocusFirstOnceClosedAsync([$"[data-testid='remove-field-{asked.Field}']", "[data-testid='add-field']"]);
    }

    /// <summary>
    /// Drops the leftovers note once it no longer describes the copy on screen.
    /// </summary>
    /// <remarks>
    /// Read with every redraw of the working copy, so it covers both ways the note goes stale: moving to another
    /// entity (the parameters set again) and the shell's Discard (the copy changed under the screen), which leaves
    /// the copy clean and the renamed field gone.
    /// </remarks>
    private void ForgetStaleLeftovers()
    {
        if (_renameLeftovers is { } leftovers
            && (!string.Equals(leftovers.Entity, EntityName, StringComparison.Ordinal)
                || !Copy.IsDirty
                || !_fieldNames.Contains(leftovers.To, StringComparer.Ordinal)))
        {
            _renameLeftovers = null;
        }
    }

    private void StageRemoval(string field)
    {
        Copy.RemoveField(EntityName, field);
        CloseEditor();
        ReadWorking();
    }
}

/// <summary>What a field rename could not carry, and on which entity and field it happened.</summary>
/// <param name="Entity">The entity the rename was on.</param>
/// <param name="From">The field's old name.</param>
/// <param name="To">Its new name.</param>
/// <param name="Uncarried">The places that still name the old one.</param>
internal sealed record RenameLeftovers(string Entity, string From, string To, IReadOnlyList<DescriptorReference> Uncarried);
