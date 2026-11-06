namespace MMLib.Alvo.Data.EntityFrameworkCore;

/// <summary>How one engine enforces an <see cref="ExclusionConstraintDefinition"/>: the statements that create and drop it.</summary>
/// <param name="Create">
/// The statements that create the enforcement, run in order inside the apply's transaction. When
/// <paramref name="SurvivesTableRebuild"/> is <see langword="false"/> they may be replayed after a rebuild, so each must be
/// idempotent (<c>IF NOT EXISTS</c>).
/// </param>
/// <param name="Drop">The statements that remove it, each idempotent (<c>IF EXISTS</c>).</param>
/// <param name="SurvivesTableRebuild">
/// Whether the enforcement lives on through an engine's create-copy-drop-rename table rebuild — <see langword="true"/> for
/// a table constraint, <see langword="false"/> for triggers and indexes a rebuild drops with the old table.
/// </param>
public sealed record ExclusionConstraintSql(IReadOnlyList<string> Create, IReadOnlyList<string> Drop, bool SurvivesTableRebuild);
