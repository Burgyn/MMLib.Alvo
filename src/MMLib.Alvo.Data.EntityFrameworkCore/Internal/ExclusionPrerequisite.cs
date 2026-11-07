using MMLib.Alvo.Schema;

namespace MMLib.Alvo.Data.EntityFrameworkCore;

/// <summary>What an engine needs before an apply adds an exclusion (<see cref="IAlvoSqlDialect.ExclusionPrerequisiteAsync"/>).</summary>
/// <param name="Statement">A statement the apply runs before its DDL, or <see langword="null"/> when none is needed.</param>
/// <param name="Refusals">
/// Why the database cannot hold the exclusions being added, or empty (the default) when it can — the engine's own words, so
/// the shared migrator never names an engine's extension.
/// </param>
internal sealed record ExclusionPrerequisite(string? Statement, IReadOnlyList<ExclusionRefusal>? Refusals = null)
{
    /// <summary>Nothing is needed.</summary>
    internal static ExclusionPrerequisite Ready { get; } = new(Statement: null);

    /// <summary>Gets why the apply is refused; empty when it is not.</summary>
    internal IReadOnlyList<ExclusionRefusal> Refusals { get; } = Refusals ?? [];

    /// <summary>Gets whether the apply is refused.</summary>
    internal bool Refused => Refusals.Count > 0;
}

/// <summary>One reason an engine refuses the exclusions an apply adds.</summary>
/// <param name="Pointer">
/// The JSON pointer the refusal is about, or <see langword="null"/> for the database as a whole — the migrator then reports it
/// at the first added exclusion's <c>exclude</c>.
/// </param>
/// <param name="Message">Why, in the engine's words.</param>
/// <param name="Fix">What an operator or an author does about it.</param>
internal sealed record ExclusionRefusal(string? Pointer, string Message, string Fix);

/// <summary>An author-declared equality field of an exclusion an apply adds, for an engine whose comparison depends on its type.</summary>
/// <param name="Pointer">Its JSON pointer, <c>/entities/&lt;e&gt;/indexes/&lt;i&gt;/fields/&lt;n&gt;</c>.</param>
/// <param name="Name">The field's name.</param>
/// <param name="Type">The field's type.</param>
internal sealed record ExclusionEqualityField(string Pointer, string Name, FieldType Type);
