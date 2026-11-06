namespace MMLib.Alvo.Schema;

/// <summary>
/// How a data port refuses a write payload naming a <see cref="FieldSchema.IsVirtual">virtual</see> field — one wording,
/// shared by the relational driver and the in-memory reference so the two cannot drift.
/// </summary>
/// <remarks>
/// A virtual field the caller's policy hides is refused as an undeclared one, in the undeclared-field wording: naming it
/// would tell the caller the field exists, which the mask exists to withhold.
/// </remarks>
internal static class VirtualFieldWrites
{
    /// <summary>The refusal of a payload key the entity does not declare, or that the caller may not know of.</summary>
    internal const string UndeclaredPayloadField = "The payload names a field that is not writable on this entity.";

    /// <summary>Why <paramref name="values"/> may not be written for its virtual fields, or <see langword="null"/>.</summary>
    /// <param name="values">The caller-supplied payload.</param>
    /// <param name="entity">The entity being written.</param>
    /// <param name="hidden">The fields the caller's policy hides.</param>
    internal static string? Refusal(IReadOnlyDictionary<string, object?> values, EntitySchema? entity, IReadOnlySet<string> hidden) =>
        entity?.Fields.Where(field => field.IsVirtual).FirstOrDefault(field => values.ContainsKey(field.Name)) is not { } field
            ? null
            : hidden.Contains(field.Name)
                ? UndeclaredPayloadField
                : $"Field '{field.Name}' is virtual and cannot be written: an enricher fills it when the record is read. Remove it from the payload.";
}
