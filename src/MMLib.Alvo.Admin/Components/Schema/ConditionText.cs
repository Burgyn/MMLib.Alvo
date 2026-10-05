using MMLib.Alvo.Schema;
using System.Text;
using System.Text.RegularExpressions;

namespace MMLib.Alvo.Admin.Components.Schema;

/// <summary>One row of a guided condition.</summary>
/// <param name="Operator">The relation.</param>
/// <param name="Image">The image the field is read from; <c>New</c> for <c>changed</c> and role rows, which have none.</param>
/// <param name="Field">The field, or <see cref="ConditionTable.Writer"/> for a role row.</param>
/// <param name="Kind">What the field is; <c>Text</c> for a role row.</param>
/// <param name="Value">The literal unquoted, the role's name, or empty when the relation takes nothing.</param>
internal sealed record ConditionRow(ConditionOperator Operator, RowImage Image, string Field, ConditionFieldKind Kind, string Value);

/// <summary>A guided condition: rows joined by all (<c>&amp;&amp;</c>) or any (<c>||</c>).</summary>
/// <param name="All">Whether every row must hold; otherwise any one.</param>
/// <param name="Rows">The rows, in order.</param>
internal sealed record GuidedCondition(bool All, IReadOnlyList<ConditionRow> Rows)
{
    /// <summary>No rows: the hook runs on every write.</summary>
    public static GuidedCondition Empty { get; } = new(true, []);

    /// <summary>Equal when the joiner and every row are.</summary>
    /// <param name="other">The other condition.</param>
    public bool Equals(GuidedCondition? other) => other is not null && All == other.All && Rows.SequenceEqual(other.Rows);

    /// <inheritdoc />
    public override int GetHashCode() => HashCode.Combine(All, Rows.Count);
}

/// <summary>A field a condition may name.</summary>
/// <param name="Name">Its name.</param>
/// <param name="Kind">What it is.</param>
/// <param name="Nullable">Whether it may be empty.</param>
/// <param name="Values">An enum's declared values; empty otherwise.</param>
internal sealed record ConditionField(string Name, ConditionFieldKind Kind, bool Nullable, IReadOnlyList<string> Values);

/// <summary>What a condition may name on one entity: its stored fields and the declared roles.</summary>
/// <param name="Fields">The fields, computed and rollup ones excluded (a before-hook's image does not carry them).</param>
/// <param name="Roles">The roles <c>auth.roles</c> declares.</param>
internal sealed record ConditionScope(IReadOnlyList<ConditionField> Fields, IReadOnlyList<string> Roles)
{
    /// <summary>The scope of one entity as the working copy declares it.</summary>
    /// <param name="entity">The entity (<c>PendingSchema.Read</c>), or <see langword="null"/>.</param>
    /// <param name="roles">The declared roles.</param>
    public static ConditionScope From(EntitySchema? entity, IReadOnlyList<string> roles) => new(
        entity is null
            ? []
            : [.. entity.Fields
                .Where(field => field.ComputedExpression is null && field.Rollup is null)
                .Select(field => new ConditionField(field.Name, ConditionTable.KindOf(field.Type), field.Nullable, field.EnumValues ?? Array.Empty<string>()))],
        roles);

    /// <summary>The field by name, or <see langword="null"/>.</summary>
    /// <param name="name">The name.</param>
    public ConditionField? Field(string name) => Fields.FirstOrDefault(field => string.Equals(field.Name, name, StringComparison.Ordinal));
}

/// <summary>
/// The guided condition's text: the one canonical CEL spelling of every row <see cref="ConditionTable"/> allows (spec §7.2,
/// ruling B7); <c>ConditionText.Recognize.cs</c> reads exactly that spelling back (§7.3).
/// </summary>
/// <remarks>
/// <para>
/// <b>Not a CEL parser</b> (analysis §4.5.2: the dashboard has no second one). The recognizer accepts only text the generator
/// writes, byte for byte — it matches the table's own formats and then demands <c>Generate(result) == text</c>; anything else
/// is text mode. The core stays the authority: the text is judged by <c>cel/check</c> and by the apply.
/// </para>
/// <para>
/// Text literals use only the lexer's escapes (<c>\\ \' \n \r \t</c>, <c>CelLexer.ReadEscape</c>). The lexer has no
/// <c>\u</c> escape and reads every other character raw, so a value beyond ASCII is written as typed; a control character
/// it has no escape for is refused (<see cref="Refusal"/>) rather than written raw. <c>HooksEditorAgreementTests</c> lexes
/// the quoted text with the real lexer.
/// </para>
/// </remarks>
internal static partial class ConditionText
{
    private const string And = " && ";
    private const string Or = " || ";

    /// <summary>The condition's CEL; empty for no rows.</summary>
    /// <param name="condition">The condition.</param>
    public static string Generate(GuidedCondition condition)
        => string.Join(condition.All ? And : Or, condition.Rows.Select(Row));

    /// <summary>One row's CEL, in its table format.</summary>
    /// <param name="row">The row.</param>
    public static string Row(ConditionRow row)
    {
        var spec = ConditionTable.Of(row.Operator);
        var text = spec.Format
            .Replace("{f}", $"{ConditionTable.Prefix(row.Image)}.{row.Field}", StringComparison.Ordinal)
            .Replace("{n}", row.Field, StringComparison.Ordinal);

        /* The value goes in last, and only one of the two placeholders exists per format, so a value that happens to hold
           "{r}" is never substituted again. */
        return spec.Operand switch
        {
            OperandKind.Literal => text.Replace("{v}", row.Kind == ConditionFieldKind.Number ? row.Value : Quote(row.Value), StringComparison.Ordinal),
            OperandKind.Role => text.Replace("{r}", Quote(row.Value), StringComparison.Ordinal),
            _ => text,
        };
    }

    /// <summary>A CEL string literal: single quotes, and only the escapes the lexer reads.</summary>
    /// <param name="value">The text.</param>
    public static string Quote(string value)
    {
        var quoted = new StringBuilder(value.Length + 2).Append('\'');
        foreach (var character in value)
        {
            quoted.Append(character switch
            {
                '\\' => @"\\",
                '\'' => @"\'",
                '\n' => @"\n",
                '\r' => @"\r",
                '\t' => @"\t",
                _ => character.ToString(),
            });
        }

        return quoted.Append('\'').ToString();
    }

    /// <summary>Why the rows cannot be written as a condition, or <see langword="null"/>.</summary>
    /// <param name="condition">The condition.</param>
    public static string? Refusal(GuidedCondition condition)
    {
        if (condition.Rows.Select(RowRefusal).FirstOrDefault(refusal => refusal is not null) is { } refusal)
        {
            return refusal;
        }

        /* UTF-16 units, as CelParser.MaxSourceLength counts them: never laxer than the schema's code-point maxLength. */
        var length = Generate(condition).Length;
        return length > ConditionTable.MaxConditionLength
            ? $"These conditions make {length} characters; a condition holds at most {ConditionTable.MaxConditionLength}."
            : null;
    }

    /// <summary>The condition as the recognizer returns it: one joiner for one row, no image where a row reads none.</summary>
    /// <param name="condition">The condition.</param>
    public static GuidedCondition Normalize(GuidedCondition condition)
        => new(condition.All || condition.Rows.Count < 2, [.. condition.Rows.Select(NormalizeRow)]);

    private static ConditionRow NormalizeRow(ConditionRow row)
    {
        var operand = ConditionTable.Of(row.Operator).Operand;
        var imageless = operand == OperandKind.Role || row.Operator == ConditionOperator.Changed;
        return row with
        {
            Image = imageless ? RowImage.New : row.Image,
            Kind = operand == OperandKind.Role ? ConditionFieldKind.Text : row.Kind,
            Value = operand == OperandKind.None ? string.Empty : row.Value,
        };
    }

    private static string? RowRefusal(ConditionRow row) => ConditionTable.Of(row.Operator).Operand switch
    {
        OperandKind.Literal when row.Kind == ConditionFieldKind.Number && !NumberLiteral().IsMatch(row.Value)
            => $"'{row.Value}' is not a number a condition can hold: write digits, with a point for a decimal, such as 12 or 4.5. "
               + "A negative number cannot be written in a condition in this build.",
        OperandKind.Literal when row.Value.Any(character => char.IsControl(character) && character is not ('\n' or '\r' or '\t'))
            => "This value holds a control character a condition cannot spell. Write the condition in text mode.",
        OperandKind.Role when row.Value.Length == 0 => "Choose a role.",
        _ => null,
    };

    [GeneratedRegex(@"^(?:0|[1-9][0-9]*)(?:\.[0-9]+)?$", RegexOptions.CultureInvariant)]
    private static partial Regex NumberLiteral();
}
