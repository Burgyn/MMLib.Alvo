namespace MMLib.Alvo.Admin.Components.Schema;

/* What names a field or an entity, asked before it is renamed or removed. Kept out of Entity.razor.cs so the
   screen's own file stays about its tabs. */
public partial class Entity
{
    private (string Field, IReadOnlyList<DescriptorReference> References)? _removal;
    private RenameLeftovers? _renameLeftovers;

    /// <summary>
    /// Drops a field from the working copy — at once when nothing names it, otherwise after a sheet has named
    /// what does.
    /// </summary>
    /// <remarks>
    /// No confirmation for a field nothing names, and that is deliberate: nothing has happened to the database
    /// yet, the preview states the cost as a plan, and the apply is where dropping a column is allowed. A field
    /// something <em>does</em> name is different — removing it composes a descriptor the apply refuses (a
    /// rollup, a mutate key, a rule compiled against a column that is gone), so the sheet refuses it until they
    /// are changed (docs/todo-admin.md §8d item 16).
    /// </remarks>
    private void RemoveField(string field)
    {
        var references = Copy.ReferencesToField(EntityName, field);
        if (references.Count > 0)
        {
            _removal = (field, references);
            return;
        }

        StageRemoval(field);
    }

    /// <summary>Removes the field the sheet named, when nothing it named blocks the removal.</summary>
    private void RemoveAnyway()
    {
        if (_removal is { } removal && !removal.References.Any(reference => reference.Blocks))
        {
            StageRemoval(removal.Field);
        }

        _removal = null;
    }

    private void CloseRemoval() => _removal = null;

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
