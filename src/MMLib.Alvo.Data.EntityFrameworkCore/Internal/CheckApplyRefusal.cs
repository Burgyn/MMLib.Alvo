using MMLib.Alvo.Descriptor;
using MMLib.Alvo.Migrations;
using MMLib.Alvo.Schema;
using System.Data.Common;

namespace MMLib.Alvo.Data.EntityFrameworkCore;

/// <summary>
/// What an apply the database refused answers, the same from both executors — the migrator's apply and the runtime writer
/// the Management API, the dashboard, rollback, restore and the boot apply through (HX-11 Rulings 20, 22).
/// </summary>
/// <remarks>
/// <para>
/// <b>Two refusals, everything else untouched.</b> A lock the apply gave up waiting for is a retryable
/// <see cref="AlvoApplyLockTimeoutException"/>; a <c>CHECK</c> the engine refused is a <see cref="DescriptorValidationException"/>
/// at the validation's pointer. Any other failure answers <see langword="null"/>, so the caller's <c>when</c> filter lets
/// it propagate with the provider's own stack.
/// </para>
/// <para>
/// <b>The pointer comes from the desired schema, never from the plan's steps.</b> The constraint the engine names may be
/// one the plan adds, one a rename re-creates, or one that existed before the plan and is broken by something it does —
/// a rollup backfill has no step of its own — so it is found by recomputing names over the schema being applied
/// (<see cref="CheckConstraintNames.Owner(SchemaModel, string)"/>), and the message says "could not be added or kept". A name no validated field
/// recomputes to still refuses, at the document's root: the engine refused the apply, and nothing was applied.
/// </para>
/// </remarks>
internal static class CheckApplyRefusal
{
    private const string Fix = "Correct those rows first — or relax the expression — then apply again.";

    /// <summary>The refusal <paramref name="failure"/> stands for, or <see langword="null"/> when it is neither.</summary>
    /// <param name="failure">What the apply threw.</param>
    /// <param name="dialect">The driver's dialect, which decodes the engine's failure.</param>
    /// <param name="desired">The schema the apply was bringing the database to.</param>
    /// <param name="sql">The plan's statements, which name the partial indexes the apply creates.</param>
    internal static Exception? Translate(Exception failure, IAlvoSqlDialect dialect, SchemaModel desired, IReadOnlyList<string>? sql = null)
    {
        ArgumentNullException.ThrowIfNull(failure);
        ArgumentNullException.ThrowIfNull(dialect);
        ArgumentNullException.ThrowIfNull(desired);

        if (Provider(failure) is not { } provider)
        {
            return null;
        }

        if (dialect.IsIndexPredicateRefusal(provider))
        {
            return PredicateRefused(desired, sql ?? []);
        }

        return dialect.IsLockTimeout(provider)
            ? new AlvoApplyLockTimeoutException(provider)
            : dialect.DecodeConstraintViolation(provider) is { Kind: AlvoConstraintKind.Check } violation
                ? Refused(desired, violation.ConstraintName)
                : null;
    }

    // The engine does not name the index whose predicate it refused, so the pointer is the first partial index the plan
    // creates — in a plan that creates several, the one the engine reached first — and the document's root when none is found.
    private static DescriptorValidationException PredicateRefused(SchemaModel desired, IReadOnlyList<string> sql)
    {
        var created = desired.Entities
            .SelectMany(entity => entity.Indexes.Where(index => index.Where is not null).Select(index => (Entity: entity, Index: index)))
            .FirstOrDefault(pair => sql.Any(statement => Creates(statement, IndexNames.Partial(pair.Entity, pair.Index))));
        return new(new DescriptorValidationResult([new(
            created.Entity is null ? "/" : IndexFilterSql.Pointer(created.Entity, created.Index),
            "The database refused this index's 'where' as a partial index predicate. Nothing was applied.",
            "Compare this entity's fields with constants directly — no duration arithmetic — then apply again.",
            DescriptorValidationSeverity.Error)]));
    }

    private static bool Creates(string statement, string name) =>
        statement.Contains("CREATE", StringComparison.OrdinalIgnoreCase)
        && statement.Contains(name, StringComparison.Ordinal);

    private static DescriptorValidationException Refused(SchemaModel desired, string? name) =>
        name is not null && CheckConstraintNames.Owner(desired, name) is { } owner
            ? new(new DescriptorValidationResult([new(
                $"/entities/{owner.Entity.Name}/fields/{owner.Field.Name}/validation",
                $"Rows of '{owner.Entity.Name}' break this validation, so the CHECK constraint could not be added or kept. Nothing was applied.",
                Fix,
                DescriptorValidationSeverity.Error)]))
            : new(new DescriptorValidationResult([new(
                "/",
                "A row breaks a validation this apply needs to hold. Nothing was applied.",
                Fix,
                DescriptorValidationSeverity.Error)]));

    private static DbException? Provider(Exception failure)
    {
        for (var current = failure; current is not null; current = current.InnerException)
        {
            if (current is DbException provider)
            {
                return provider;
            }
        }

        return null;
    }
}
