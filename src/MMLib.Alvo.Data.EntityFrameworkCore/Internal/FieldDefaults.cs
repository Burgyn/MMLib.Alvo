using MMLib.Alvo.Schema;
using System.Text.Json;

namespace MMLib.Alvo.Data.EntityFrameworkCore;

/// <summary>
/// Fills in the declared default of every field a create's payload does not carry.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why the write path fills it rather than the column's own <c>DEFAULT</c> clause.</b> The generated DDL
/// does carry one — and it is what a writer reaching the table without going through Alvo gets — but the
/// insert this provider builds <em>names every mapped column</em>, so a field the payload omits is written as
/// an explicit <c>NULL</c> and the clause never applies. Reaching it instead would mean marking the property
/// store-generated on the runtime model, and EF then reads a property's <em>CLR default</em> as "not set":
/// a caller's explicit <see langword="false"/> on a field defaulting to <see langword="true"/> would be
/// replaced by the declared value. One value, silently wrong — the failure the old refusal existed to prevent.
/// </para>
/// <para>
/// <b>Before the guards, not after.</b> The filled value is part of the row the write will store, so
/// <c>WITH CHECK</c> and the whole-row guard must see it: a rule that admits a row only when
/// <c>done == false</c> is answering about the row that lands, and the default is part of that row.
/// </para>
/// <para>
/// Only on a create. An update's absent field means "leave it alone", and an entity's defaults are what a row
/// starts life with, not what every write re-asserts.
/// </para>
/// </remarks>
internal static class FieldDefaults
{
    /// <summary>
    /// <paramref name="values"/> plus the default of every field it does not mention, or
    /// <paramref name="values"/> itself when the entity declares none that apply.
    /// </summary>
    /// <param name="schema">The entity being written.</param>
    /// <param name="values">The caller's payload.</param>
    /// <param name="frozen">
    /// The fields this caller may not write — <c>readOnly</c> and <c>hidden</c> under their own verdict.
    /// Empty when the row is being created, because there is no stored value a default could destroy.
    /// </param>
    /// <remarks>
    /// <b>A default must not reach a field the caller could not have sent</b>, and on a replacement that is
    /// not a nicety. A frozen field the payload cannot name would otherwise be written back to its declared
    /// default on every <c>PUT</c> — so a caller who is forbidden to touch <c>status</c> resets it by
    /// replacing the row's title, which is a write to a field the policy froze, through the one door
    /// <c>WholeRowGuard</c> deliberately leaves open. Its <c>CallerOwnedFields</c> exemption cannot
    /// catch it, because the key is already in the bag by the time the guard looks.
    /// </remarks>
    internal static IReadOnlyDictionary<string, object?> Applied(
        EntitySchema schema, IReadOnlyDictionary<string, object?> values, IReadOnlySet<string>? frozen = null)
    {
        ArgumentNullException.ThrowIfNull(schema);
        ArgumentNullException.ThrowIfNull(values);

        var missing = schema.Fields.Where(field =>
            field.Default is not null
            && !values.ContainsKey(field.Name)
            && frozen?.Contains(field.Name) != true);

        if (!missing.Any())
        {
            return values;
        }

        var filled = new Dictionary<string, object?>(values, StringComparer.Ordinal);
        foreach (var field in missing)
        {
            filled[field.Name] = Value(field);
        }

        return filled;
    }

    /// <summary>
    /// The literal as the column holds it, through the funnel a caller's own value goes through — so a default
    /// is stored by the same rules as a value somebody sent, including the refusals.
    /// </summary>
    private static object? Value(FieldSchema field)
        => ColumnValue.For(FieldClrTypeMap.Exact(field), field.Name, Raw(field.Default!.Value));

    /// <summary>The literal as the CLR value its JSON kind denotes, before the column's own conversion.</summary>
    /// <remarks>
    /// A number arrives as <see cref="decimal"/> whatever the column is, because the conversion below knows
    /// the column's type and this does not: a JSON <c>1</c> is as much an <c>integer</c> column's value as a
    /// <c>decimal</c> one's, and deciding here would be deciding twice.
    /// </remarks>
    private static object? Raw(JsonElement literal) => literal.ValueKind switch
    {
        JsonValueKind.True => true,
        JsonValueKind.False => false,
        JsonValueKind.String => literal.GetString(),
        JsonValueKind.Number => literal.GetDecimal(),
        _ => literal.GetRawText(),
    };
}
