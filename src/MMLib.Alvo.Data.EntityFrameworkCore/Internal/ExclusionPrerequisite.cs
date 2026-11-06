namespace MMLib.Alvo.Data.EntityFrameworkCore;

/// <summary>What an engine needs before an apply adds an exclusion (<see cref="IAlvoSqlDialect.ExclusionPrerequisiteAsync"/>).</summary>
/// <param name="Statement">A statement the apply runs before its DDL, or <see langword="null"/> when none is needed.</param>
/// <param name="RefusalMessage">
/// Why the database cannot hold an exclusion and this connection's role may not make it able to, or <see langword="null"/>
/// when it can — the engine's own words, so the shared migrator never names an engine's extension.
/// </param>
/// <param name="RefusalFix">What an operator does about <paramref name="RefusalMessage"/>.</param>
internal sealed record ExclusionPrerequisite(string? Statement, string? RefusalMessage = null, string? RefusalFix = null)
{
    /// <summary>Nothing is needed.</summary>
    internal static ExclusionPrerequisite Ready { get; } = new(Statement: null);

    /// <summary>Gets whether the apply is refused.</summary>
    internal bool Refused => RefusalMessage is not null;
}
