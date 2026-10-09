using MMLib.Alvo.Expressions;

namespace MMLib.Alvo.Management;

/// <summary>The answer of <see cref="IAlvoManagement.GetCelFunctionsAsync"/>: the CEL functions a descriptor may call.</summary>
/// <remarks>
/// An envelope rather than a bare list, and init-only rather than positional: the response is the part of this
/// surface most likely to grow (a catalog version, a documentation link, deprecation notes), and an added optional
/// init member is additive where a bare JSON array or an added positional parameter is not.
/// </remarks>
public sealed record ManagementCelFunctions
{
    /// <summary>Gets one entry per overload — the built-ins and the host's registrations — ordered by name.</summary>
    public required IReadOnlyList<CelFunctionInfo> Functions { get; init; }
}
