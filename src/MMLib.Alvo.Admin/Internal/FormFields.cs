using MMLib.Alvo.Schema;

namespace MMLib.Alvo.Admin.Internal;

/// <summary>
/// Which of an entity's fields the record form offers as controls, and which it only shows.
/// </summary>
/// <remarks>
/// <para>
/// <b>Calculated: computed, rollup and read-only</b>, shown at the bottom of the form and never sent. The
/// form used to leave them out altogether, and they are the most interesting values on a record — "why
/// is this order's total 43.50?" (usability test T5) failed because the total was nowhere on the sheet.
/// A control for one would produce a value the engine overwrites or refuses.
/// </para>
/// <para>
/// A field hidden from every caller is never shown, because it never comes back; it stays a control,
/// because a write-only field is still one the caller may set. Alvo's own columns are neither.
/// </para>
/// </remarks>
internal static class FormFields
{
    /// <summary>Whether the engine derives the field's value, from its own row or from related ones.</summary>
    public static bool Derived(FieldSchema field)
    {
        ArgumentNullException.ThrowIfNull(field);

        return field.ComputedExpression is not null || field.Rollup is not null;
    }

    /// <summary>Whether the form shows the field read-only rather than as a control.</summary>
    public static bool Calculated(FieldSchema field, FieldLocks locks, bool creating)
    {
        ArgumentNullException.ThrowIfNull(field);
        ArgumentNullException.ThrowIfNull(locks);

        return Derived(field) || locks.Locked(field.Name, creating);
    }

    /// <summary>The fields the form offers a control for, in declaration order.</summary>
    public static IReadOnlyList<FieldSchema> Editable(EntitySchema entity, FieldLocks locks, bool creating)
        => [.. Own(entity).Where(field => !Calculated(field, locks, creating))];

    /// <summary>The fields the form shows read-only under "Calculated"; none on a create, which has no values yet.</summary>
    public static IReadOnlyList<FieldSchema> Shown(EntitySchema entity, FieldMasks masks, FieldLocks locks, bool creating)
    {
        ArgumentNullException.ThrowIfNull(masks);

        return creating
            ? []
            : [.. Own(entity).Where(field => Calculated(field, locks, creating) && !masks.NeverReturned(field.Name))];
    }

    private static IEnumerable<FieldSchema> Own(EntitySchema entity)
    {
        ArgumentNullException.ThrowIfNull(entity);

        var managed = AlvoManagedColumns.For(entity);
        return entity.Fields.Where(field => !managed.Contains(field.Name));
    }
}
