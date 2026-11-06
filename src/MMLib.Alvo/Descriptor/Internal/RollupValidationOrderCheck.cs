using MMLib.Alvo.Schema;

namespace MMLib.Alvo.Descriptor.Internal;

/// <summary>
/// The apply-time refusal of a <c>validation</c> added or changed in the same apply that reads — directly, or through a
/// derived field (<see cref="ValidationReadSet"/>) — a rollup whose definition that apply changes (HX-11 Ruling 73).
/// </summary>
/// <remarks>
/// <para>
/// <b>Why it is refused rather than tried.</b> The apply adds the CHECK constraint in its DDL and recomputes the changed rollup
/// after it (<c>RollupBackfill</c>), so the engine judges the new constraint against the aggregate the <em>old</em> definition
/// stored — and the pre-apply violation count would count the same stale values. Either can refuse an apply whose rows comply
/// once it has run, or let it through, depending on data the author cannot see. Refusing the pair when it is planned is
/// deterministic and names the order that works: the rollup change in one apply, the validation in the next.
/// </para>
/// <para>
/// <b>What it deliberately does not refuse</b> (Ruling 76, amended). A rollup the same apply adds as a <em>new</em> field holds
/// no value yet — its column is new, so the DDL admits the NULLs and the backfill's write is held to the constraint, refused by
/// the engine at the validation's pointer if a row breaks it. A stored <em>plain</em> field that becomes a rollup is not that
/// case: its column holds author-written values the backfill replaces, so it counts as a changed definition and is refused. An <em>unchanged</em> validation over a changed rollup is kept, not re-added, and the backfill's write is held
/// to it the same way. A parent the apply creates has no rows.
/// </para>
/// <para>
/// <b>Engine-agnostic, so it lives above the port.</b> The read set needs the derived fields seen through, which only the core
/// can expand; every core path that plans an apply — the runtime apply, its preview, a rollback, the code-first runner and the
/// boot — calls <see cref="ThrowIfAny"/> before it plans.
/// </para>
/// </remarks>
internal static class RollupValidationOrderCheck
{
    private const string Fix = "Apply the rollup change first, then the validation.";

    /// <summary>
    /// The fix a rollback names instead: a rollback cannot be split in two, so the order that works goes through an
    /// intermediate descriptor (Task 12 review m3).
    /// </summary>
    internal const string RollbackFix = "Apply the target's rollup change as an intermediate descriptor first, then roll back.";

    /// <summary>Throws when <see cref="Errors"/> refuses anything.</summary>
    /// <param name="current">The schema the store holds.</param>
    /// <param name="desired">The schema the apply produces.</param>
    /// <param name="fix">The fix each refusal names; the apply's own order when omitted, <see cref="RollbackFix"/> for a rollback.</param>
    /// <exception cref="DescriptorValidationException">A validation reads a rollup the same apply changes.</exception>
    internal static void ThrowIfAny(SchemaModel current, SchemaModel desired, string fix = Fix)
    {
        var errors = Errors(current, desired, fix);
        if (errors.Count > 0)
        {
            throw new DescriptorValidationException(new DescriptorValidationResult(errors));
        }
    }

    /// <summary>One refusal per validation added or changed over a rollup whose definition the apply changes.</summary>
    /// <param name="current">The schema the store holds.</param>
    /// <param name="desired">The schema the apply produces.</param>
    /// <param name="fix">The fix each refusal names.</param>
    internal static IReadOnlyList<DescriptorValidationError> Errors(SchemaModel current, SchemaModel desired, string fix = Fix)
    {
        ArgumentNullException.ThrowIfNull(current);
        ArgumentNullException.ThrowIfNull(desired);

        var reads = new ValidationReadSet(desired, descriptor: null);
        return [.. from entity in desired.Entities
                   let stored = Stored(current, entity)
                   where stored is not null
                   from field in entity.Fields
                   where field.CheckExpression is not null && AddedOrChanged(stored, field)
                   let changed = ChangedRollups(current, desired, reads.ValidationClosure(entity, field))
                   where changed.Count > 0
                   select Refusal(entity, field, changed, fix)];
    }

    private static bool AddedOrChanged(EntitySchema stored, FieldSchema field) =>
        StoredField(stored, field) is not { } before || !string.Equals(before.CheckExpression, field.CheckExpression, StringComparison.Ordinal);

    private static List<string> ChangedRollups(SchemaModel current, SchemaModel desired, IEnumerable<ValidationReadSet.Read> reads) =>
        [.. reads.Where(read => Changed(current, desired, read)).Select(read => $"'{read.Entity}.{read.Field}'").Order(StringComparer.Ordinal)];

    // Any stored field of that name holds values the backfill replaces — an old aggregate, or plain values a field that becomes
    // a rollup now held; only a brand-new field holds none (see the remarks, Ruling 76 amended).
    private static bool Changed(SchemaModel current, SchemaModel desired, ValidationReadSet.Read read) =>
        Named(desired, read.Entity) is { } entity
        && Named(entity, read.Field) is { Rollup: not null } field
        && Stored(current, entity) is { } stored
        && StoredField(stored, field) is { } before
        && !Equals(before.Rollup, field.Rollup);

    private static EntitySchema? Stored(SchemaModel current, EntitySchema desired) =>
        Named(current, desired.Name) ?? (desired.RenamedFrom is { } from ? Named(current, from) : null);

    private static FieldSchema? StoredField(EntitySchema stored, FieldSchema desired) =>
        Named(stored, desired.Name) ?? (desired.RenamedFrom is { } from ? Named(stored, from) : null);

    private static EntitySchema? Named(SchemaModel schema, string name) =>
        schema.Entities.FirstOrDefault(entity => string.Equals(entity.Name, name, StringComparison.Ordinal));

    private static FieldSchema? Named(EntitySchema entity, string name) =>
        entity.Fields.FirstOrDefault(field => string.Equals(field.Name, name, StringComparison.Ordinal));

    private static DescriptorValidationError Refusal(EntitySchema entity, FieldSchema field, IReadOnlyList<string> rollups, string fix) => new(
        $"/entities/{entity.Name}/fields/{field.Name}/validation",
        $"Field '{entity.Name}.{field.Name}' declares a validation that reads the rollup {string.Join(", ", rollups)}, whose "
        + "definition this apply changes: the CHECK constraint would be judged against the values the old definition stored, "
        + "before the apply recomputes them. Nothing was applied.",
        fix,
        DescriptorValidationSeverity.Error);
}
