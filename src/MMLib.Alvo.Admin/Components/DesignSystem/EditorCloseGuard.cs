namespace MMLib.Alvo.Admin.Components.DesignSystem;

/// <summary>Whether an editor may close now, or must first ask "Discard your changes?" (spec §3.4).</summary>
internal sealed class EditorCloseGuard
{
    /// <summary>Whether the question is on screen.</summary>
    public bool Asking { get; private set; }

    /// <summary>
    /// Answers whether the editor may close. For a dirty editor it answers <see langword="false"/> and starts
    /// asking. A second request while asking stays a question: pressing Escape twice must not discard.
    /// </summary>
    public bool RequestClose(bool dirty)
    {
        if (!dirty)
        {
            Asking = false;
            return true;
        }

        Asking = true;
        return false;
    }

    /// <summary>The operator chose to go on editing.</summary>
    public void KeepEditing() => Asking = false;

    /// <summary>The operator chose to lose the changes; the editor closes next.</summary>
    public void Discard() => Asking = false;
}
