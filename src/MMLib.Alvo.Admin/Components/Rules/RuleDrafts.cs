namespace MMLib.Alvo.Admin.Components.Rules;

/// <summary>
/// What the operator has typed into each operation's rule and not yet saved.
/// </summary>
/// <remarks>
/// A rule used to be staged on blur, so tabbing away staged a change nobody meant to make (inventory defect #7).
/// A draft is held here until "Save rule" or Ctrl/Cmd+Enter takes it, and Escape reverts it.
/// </remarks>
internal sealed class RuleDrafts
{
    private readonly Dictionary<string, string> _drafts = new(StringComparer.Ordinal);

    /// <summary>What the box shows: the draft, or what the descriptor declares.</summary>
    public string Text(string operation, string? declared)
        => _drafts.TryGetValue(operation, out var draft) ? draft : declared ?? string.Empty;

    /// <summary>Holds what was typed.</summary>
    public void Set(string operation, string text) => _drafts[operation] = text;

    /// <summary>Whether saving would change the descriptor; surrounding space is not a change.</summary>
    public bool IsDirty(string operation, string? declared)
        => _drafts.TryGetValue(operation, out var draft)
            && !string.Equals(draft.Trim(), (declared ?? string.Empty).Trim(), StringComparison.Ordinal);

    /// <summary>Answers the trimmed rule to stage, and forgets the draft.</summary>
    public string Take(string operation, string? declared)
    {
        var text = Text(operation, declared).Trim();
        _drafts.Remove(operation);
        return text;
    }

    /// <summary>Drops the draft, so the box reads the descriptor again.</summary>
    public void Revert(string operation) => _drafts.Remove(operation);

    /// <summary>Whether any operation holds a draft that saving would stage, given what each one declares.</summary>
    /// <remarks>What leaving the entity asks about: an unsaved rule is lost with the screen.</remarks>
    public bool AnyDirty(Func<string, string?> declared)
    {
        ArgumentNullException.ThrowIfNull(declared);
        return _drafts.Keys.Any(operation => IsDirty(operation, declared(operation)));
    }

    /// <summary>Drops every draft: the screen moved to another entity, or the operator chose to discard them.</summary>
    public void Clear() => _drafts.Clear();
}
