namespace MMLib.Alvo.Admin.Components.Schema;

/// <summary>Narrows the entity list to what the operator typed.</summary>
internal static class EntityFilter
{
    /// <summary>The names containing <paramref name="term"/>, ignoring case and the term's surrounding space, in their order.</summary>
    public static IReadOnlyList<string> Apply(IEnumerable<string> names, string? term)
    {
        ArgumentNullException.ThrowIfNull(names);
        var wanted = term?.Trim() ?? string.Empty;
        return wanted.Length == 0
            ? [.. names]
            : [.. names.Where(name => name.Contains(wanted, StringComparison.OrdinalIgnoreCase))];
    }
}
