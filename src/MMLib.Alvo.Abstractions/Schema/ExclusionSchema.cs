namespace MMLib.Alvo.Schema;

/// <summary>
/// The half-open <c>[From, To)</c> range of an exclusion index: no two rows with equal index fields may hold overlapping
/// ranges, and an empty range (<c>From == To</c>) overlaps nothing.
/// </summary>
/// <param name="From">The field where the range starts (inclusive).</param>
/// <param name="To">The field where the range ends (exclusive).</param>
public sealed record ExclusionSchema(string From, string To);
