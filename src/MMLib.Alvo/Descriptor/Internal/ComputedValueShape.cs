using MMLib.Alvo.Expressions;
using MMLib.Alvo.Expressions.Internal;
using MMLib.Alvo.Schema;

namespace MMLib.Alvo.Descriptor.Internal;

/// <summary>
/// What a compiled <c>computed</c> expression's <em>value</em> must agree with before it is rendered: it reads a field
/// of its row, its type is one the declared field holds, and — for text — it fits the declared length.
/// </summary>
/// <remarks>
/// <para>
/// <b>A constant is not a computed column.</b> A text constant is written inline into the generated column's DDL, so
/// an expression made only of constants (<c>'a' + 'b'</c>) would render and store the same value on every row — which
/// is a <c>default</c>, declared wrongly. It is refused naming that.
/// </para>
/// <para>
/// <b>Text against the declared type, both ways round.</b> A join of text into an <c>integer</c> column is refused by
/// PostgreSQL's DDL and stored as text by SQLite; a number into a <c>string</c> column the other way round. Refusing
/// the mismatch here is what keeps the two engines from answering differently.
/// </para>
/// <para>
/// <b>A declared <c>maxLength</c> must hold the longest value the join can produce</b> — the sum of its parts'
/// declared lengths in code points, the unit <see cref="FieldSchema.MaxLength"/> is in. PostgreSQL's
/// <c>varchar(n)</c> refuses a longer value at write time while SQLite stores it, so an unbounded or too-long join
/// into a bounded column is refused here, where the fix is one number away.
/// </para>
/// </remarks>
internal static class ComputedValueShape
{
    /// <summary>The first shape refusal, as the message and fix the caller places at the field's pointer.</summary>
    /// <param name="entity">The entity the field belongs to.</param>
    /// <param name="field">The computed field.</param>
    /// <param name="expression">Its expression, compiled for <see cref="CelProfile.Computed"/>.</param>
    internal static (string Message, string Fix)? Refusal(EntitySchema entity, FieldSchema field, CompiledExpression expression) =>
        ConstantOnly(entity, field, expression)
        ?? DeclaredTypeMismatch(entity, field, expression)
        ?? LengthBound(entity, field, expression);

    private static (string, string)? ConstantOnly(EntitySchema entity, FieldSchema field, CompiledExpression expression) =>
        ReadsAField(expression.Root)
            ? null
            : ($"{Declares(entity, field, expression)}, which reads no field of its row: every row would hold the same "
                + "value, which is a default rather than a computed column.",
                "Declare a plain field with a \"default\" instead, or join the constant to a field of the row "
                + "(\"'Mr. ' + last_name\").");

    private static bool ReadsAField(CelNode node) =>
        node is CelFieldRef or CelHas || CelTree.Children(node).Any(ReadsAField);

    private static (string, string)? DeclaredTypeMismatch(EntitySchema entity, FieldSchema field, CompiledExpression expression)
    {
        var computesText = expression.ResultType == CelValueType.String;
        var holdsText = field.Type is Schema.FieldType.String or Schema.FieldType.Text;
        if (computesText == holdsText)
        {
            return null;
        }

        return computesText
            ? ($"{Declares(entity, field, expression)}, which joins text into a string, but the field is declared "
                + $"'{Spelling(field.Type)}'.",
                "Declare the field \"type\": \"string\" (or \"text\" when it has no length limit): a join of text "
                + "produces a string.")
            : ($"{Declares(entity, field, expression)}, which computes {expression.ResultType}, but the field is declared "
                + $"'{Spelling(field.Type)}', which holds text.",
                "Declare the field with the type the expression computes, or make the expression a join of text "
                + "(\"first_name + ' ' + last_name\").");
    }

    private static (string, string)? LengthBound(EntitySchema entity, FieldSchema field, CompiledExpression expression)
    {
        if (field.MaxLength is not { } declared || expression.ResultType != CelValueType.String)
        {
            return null;
        }

        var (longest, unbounded) = Longest(expression.Root, entity);
        if (longest is null)
        {
            return ($"{Declares(entity, field, expression)} into a field declared maxLength {declared}, but it joins "
                + $"'{unbounded}', which declares no maxLength — so the value can be longer than the column. PostgreSQL "
                + "refuses such a write and SQLite stores it.",
                $"Give '{unbounded}' a maxLength, or drop maxLength from '{field.Name}'.");
        }

        return longest <= declared
            ? null
            : ($"{Declares(entity, field, expression)}, which can be up to {longest} characters long, into a field "
                + $"declared maxLength {declared}. PostgreSQL refuses such a write and SQLite stores it.",
                $"Declare maxLength {longest} or more on '{field.Name}', or drop maxLength.");
    }

    /// <summary>
    /// The longest value a text node can produce, in code points, or <see langword="null"/> with the field that has
    /// no bound — a <c>text</c>, or a <c>string</c> that declares no <c>maxLength</c>.
    /// </summary>
    private static (int? Longest, string? Unbounded) Longest(CelNode node, EntitySchema entity) => node switch
    {
        CelLiteral { Value: string text } => (text.EnumerateRunes().Count(), null),
        CelFieldRef fieldRef => LongestOf(entity.Fields.First(field => field.Name == fieldRef.FieldName)),
        CelBinary join => Combine(Longest(join.Left, entity), Longest(join.Right, entity), (left, right) => left + right),
        CelConditional choice => Combine(Longest(choice.WhenTrue, entity), Longest(choice.WhenFalse, entity), Math.Max),
        _ => (null, null),
    };

    private static (int?, string?) LongestOf(FieldSchema field) => field switch
    {
        { Type: Schema.FieldType.Enum, EnumValues: { Count: > 0 } values } => (values.Max(value => value.EnumerateRunes().Count()), null),
        { Type: Schema.FieldType.String, MaxLength: { } maxLength } => (maxLength, null),
        _ => (null, field.Name),
    };

    private static (int?, string?) Combine((int? Longest, string? Unbounded) left, (int? Longest, string? Unbounded) right, Func<int, int, int> combine) =>
        left.Longest is { } l && right.Longest is { } r ? (combine(l, r), null) : (null, left.Unbounded ?? right.Unbounded);

    private static string Declares(EntitySchema entity, FieldSchema field, CompiledExpression expression) =>
        $"Field '{entity.Name}.{field.Name}' declares \"computed\": \"{expression.Source}\"";

    private static string Spelling(Schema.FieldType type) => type.ToString().ToLowerInvariant();
}
