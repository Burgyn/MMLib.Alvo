namespace MMLib.Alvo.Expressions.Internal;

/// <summary>
/// Every function the compiler knows, by name: the built-ins, plus whatever the embedding host registered. The parser
/// asks it whether a name is a function, the type checker which overload a call binds, and the Management read what
/// to list.
/// </summary>
/// <remarks>
/// <b>Internal on purpose</b> (spec X1): nothing outside the core reads it — the dashboard and the assistant reach the
/// list through <c>IAlvoManagement</c>. Publishing it is additive later; un-publishing would not be.
/// </remarks>
internal sealed class CelFunctionCatalog
{
    private readonly Dictionary<string, IReadOnlyList<CelFunction>> _byName;

    /// <summary>Initializes a new instance of the <see cref="CelFunctionCatalog"/> class.</summary>
    /// <param name="functions">Every overload; overloads of one name keep the order given.</param>
    /// <exception cref="InvalidOperationException">One name mixes provenance or profiles, or a host name repeats.</exception>
    internal CelFunctionCatalog(IEnumerable<CelFunction> functions)
    {
        ArgumentNullException.ThrowIfNull(functions);
        Functions = [.. functions.OrderBy(function => function.Name, StringComparer.Ordinal)];
        _byName = Functions
            .GroupBy(function => function.Name, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => (IReadOnlyList<CelFunction>)[.. group], StringComparer.Ordinal);
        EnsureOneFunctionPerName();
    }

    /// <summary>Gets a catalog of the built-ins only — what a host with no registrations, the standalone image and the CLI know.</summary>
    internal static CelFunctionCatalog BuiltIns => new(CelBuiltInFunctions.All);

    /// <summary>Gets every overload, ordered by name (ordinal), overloads of one name in declaration order.</summary>
    internal IReadOnlyList<CelFunction> Functions { get; }

    /// <summary>Every overload as discovery shows it, in <see cref="Functions"/> order.</summary>
    /// <returns>The public descriptions.</returns>
    internal IReadOnlyList<CelFunctionInfo> Describe() => [.. Functions.Select(function => function.Describe())];

    /// <summary>Gets every distinct name, ordinal order.</summary>
    internal IReadOnlyList<string> Names => [.. _byName.Keys.Order(StringComparer.Ordinal)];

    /// <summary>Whether <paramref name="name"/> is a function this catalog knows.</summary>
    /// <param name="name">A name as written in source.</param>
    /// <returns><see langword="true"/> when it has at least one overload.</returns>
    internal bool Contains(string name) => _byName.ContainsKey(name);

    /// <summary>The overloads of <paramref name="name"/>, or none.</summary>
    /// <param name="name">A name as written in source.</param>
    /// <returns>The overloads, in declaration order.</returns>
    internal IReadOnlyList<CelFunction> Overloads(string name) => _byName.TryGetValue(name, out var overloads) ? overloads : [];

    /// <summary>This catalog plus <paramref name="functions"/>, none of which may reuse a name already here.</summary>
    /// <param name="functions">The functions to add.</param>
    /// <returns>A new catalog.</returns>
    /// <exception cref="InvalidOperationException">A name is already in the catalog.</exception>
    internal CelFunctionCatalog With(IEnumerable<CelFunction> functions)
    {
        ArgumentNullException.ThrowIfNull(functions);
        var added = functions.ToList();
        if (added.FirstOrDefault(function => Contains(function.Name)) is { } clash)
        {
            throw new InvalidOperationException(
                $"A CEL function named '{clash.Name}' is already in the catalog; every function has its own name.");
        }

        return new CelFunctionCatalog([.. Functions, .. added]);
    }

    private void EnsureOneFunctionPerName()
    {
        foreach (var (name, overloads) in _byName)
        {
            var first = overloads[0];
            var mixed = overloads.Any(o => o.IsHost != first.IsHost || !o.Profiles.SetEquals(first.Profiles));
            if (mixed || (first.IsHost && overloads.Count > 1))
            {
                throw new InvalidOperationException(
                    $"The CEL function '{name}' is declared more than once with different provenance or profiles, or "
                    + "a host function is declared twice; a name is one function.");
            }
        }
    }
}
