namespace MMLib.Alvo.Schema;

/// <summary>
/// The one answer to "does the framework maintain this field's value?" — a computed column, a rollup or a sequence —
/// so every layer that refuses a write to such a field, or excuses a create for omitting it, asks the same question.
/// </summary>
internal static class MaintainedFields
{
    /// <summary>Whether no write may set <paramref name="field"/>, because the database or the framework assigns it.</summary>
    /// <param name="field">The applied field.</param>
    internal static bool IsMaintained(this FieldSchema field) =>
        field.ComputedExpression is not null || field.Rollup is not null || field.Sequence is not null;
}
