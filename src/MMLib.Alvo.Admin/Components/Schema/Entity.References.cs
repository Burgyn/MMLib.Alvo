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

    /// <summary>Where a Cancel of the entity's removal puts focus: its Remove, or the heading when the header folded it away.</summary>
    private static readonly string[] _removeEntityTrigger = ["[data-testid='remove-entity']", "h1"];

    private bool _removingEntity;
    private IReadOnlyList<DescriptorReference> _inbound = [];

    /// <summary>Whether something the confirm named would leave the apply refusing the descriptor without the entity.</summary>
    private bool EntityRemovalBlocked => _inbound.Any(reference => reference.Blocks);

    /// <summary>The confirm's first sentence: what goes, now and at the apply.</summary>
    private string RemoveEntityConsequence => _pending
        ? "It leaves the working copy with its fields, rules, hooks and indexes. It was never applied, so there is no table to drop."
        : "It leaves the working copy with its fields, rules, hooks and indexes; when you apply, its table and every row in it are dropped, and the apply asks for that again.";

    /// <summary>Asks before the entity leaves the working copy, naming what points at it.</summary>
    /// <remarks>
    /// <c>WorkingCopy.RemoveEntity</c> was written and no screen called it — the §5f <c>Discard</c> pattern again
    /// (docs/todo-admin.md §8d item 24). A confirm with the typed name rather than an immediate removal with an Undo
    /// (spec §3.2 allows either for a staged removal): §3.2 names "remove entity" among the wide-blast-radius actions
    /// that type the name, the removal leaves this screen, whose address then names nothing, and the apply it prepares
    /// drops a table with every row in it.
    /// </remarks>
    private void OpenRemoveEntity()
    {
        _inbound = Copy.ReferencesToEntity(EntityName);
        _removingEntity = true;
    }

    /// <summary>Cancel and Escape keep the entity, and focus goes back to its Remove.</summary>
    private Task CloseRemoveEntity()
    {
        _removingEntity = false;
        return Interop.FocusFirstOnceClosedAsync(_removeEntityTrigger);
    }

    /// <summary>
    /// Removes the entity the confirm named, when what points at it — asked again now, not taken from the press —
    /// still lets the apply accept the copy; otherwise the confirm stays open, naming what arrived since.
    /// </summary>
    /// <remarks>
    /// The verb leaves the screen, so focus is the list's heading, which the router gives it (spec §3.2). The unsaved
    /// rules go with the entity, so they are dropped first and the guard does not ask about rules on nothing. An
    /// entity another tab removed meanwhile is already what the operator asked for: the screen leaves all the same.
    /// </remarks>
    private void RemoveEntity()
    {
        _inbound = Copy.ReferencesToEntity(EntityName);
        if (DeclaredHere && EntityRemovalBlocked)
        {
            return;
        }

        var removed = EntityName;
        _removingEntity = false;
        _ruleDrafts.Clear();
        if (DeclaredHere)
        {
            Copy.RemoveEntity(removed);
            Snackbar.Confirm(StagedWords.Removed("Entity", removed));
        }

        Navigation.NavigateTo(AdminPaths.Schema);
    }
}

/// <summary>What a field rename could not carry, and on which entity and field it happened.</summary>
/// <param name="Entity">The entity the rename was on.</param>
/// <param name="From">The field's old name.</param>
/// <param name="To">Its new name.</param>
/// <param name="Uncarried">The places that still name the old one.</param>
internal sealed record RenameLeftovers(string Entity, string From, string To, IReadOnlyList<DescriptorReference> Uncarried);
