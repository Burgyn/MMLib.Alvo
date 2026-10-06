using MMLib.Alvo.Schema;

namespace MMLib.Alvo.Admin.Components.Schema;

/// <summary>What a field is, as far as a hook condition can compare it.</summary>
/// <remarks>
/// Not <see cref="FieldKind"/>, which says where a field's value comes from (supplied, rollup, computed); this one says
/// what a condition may compare it with.
/// </remarks>
internal enum ConditionFieldKind
{
    /// <summary>A <c>string</c> or <c>text</c> field: compared with a quoted literal.</summary>
    Text,

    /// <summary>An <c>enum</c> field: compared with one of its declared values.</summary>
    Choice,

    /// <summary>An <c>integer</c> or <c>decimal</c> field: compared with a number.</summary>
    Number,

    /// <summary>A <c>boolean</c> field.</summary>
    Flag,

    /// <summary>A <c>date</c> or <c>datetime</c> field: CEL has no literal for one, so only presence and change.</summary>
    Moment,

    /// <summary>A <c>uuid</c> or <c>ref</c> field: no literal either, but it can be the person writing.</summary>
    Identity,

    /// <summary>A <c>json</c> field: not comparable (<c>CelTypeChecker</c>), so only presence.</summary>
    Json,
}

/// <summary>The relations the guided condition offers; each is exactly one row of <see cref="ConditionTable"/>.</summary>
internal enum ConditionOperator
{
    /// <summary><c>f == v</c>.</summary>
    Is,

    /// <summary><c>f != v</c> — false when the field is empty.</summary>
    IsNot,

    /// <summary><c>!(f == v)</c> — true when the field is empty.</summary>
    IsNotOrEmpty,

    /// <summary><c>f &lt; v</c>.</summary>
    Less,

    /// <summary><c>f &lt;= v</c>.</summary>
    LessOrEqual,

    /// <summary><c>f &gt; v</c>.</summary>
    Greater,

    /// <summary><c>f &gt;= v</c>.</summary>
    GreaterOrEqual,

    /// <summary><c>f == true</c>.</summary>
    IsTrue,

    /// <summary><c>f == false</c>.</summary>
    IsFalse,

    /// <summary><c>has(f)</c>.</summary>
    HasValue,

    /// <summary><c>!has(f)</c>.</summary>
    IsEmpty,

    /// <summary><c>changed(f)</c>.</summary>
    Changed,

    /// <summary><c>f == @user.id</c>.</summary>
    IsTheWriter,

    /// <summary><c>'r' in @user.roles</c>.</summary>
    HasRole,

    /// <summary><c>!('r' in @user.roles)</c>.</summary>
    LacksRole,
}

/// <summary>Which image of the row a condition reads: the one being written, or the one before the write.</summary>
internal enum RowImage
{
    /// <summary><c>new.</c> — the row as the write leaves it.</summary>
    New,

    /// <summary><c>old.</c> — the row before the write.</summary>
    Old,
}

/// <summary>What an operator takes on its right.</summary>
internal enum OperandKind
{
    /// <summary>Nothing: <c>has(f)</c>, <c>changed(f)</c>, <c>f == true</c>.</summary>
    None,

    /// <summary>A literal the field's kind spells.</summary>
    Literal,

    /// <summary>A role name from <c>auth.roles</c>.</summary>
    Role,
}

/// <summary>One relation: its words, its canonical CEL and where it is legal.</summary>
/// <param name="Operator">The relation.</param>
/// <param name="Words">What the form calls it.</param>
/// <param name="Format">The canonical CEL, with <c>{f}</c>, <c>{n}</c>, <c>{v}</c> or <c>{r}</c> in place of its parts.</param>
/// <param name="Operand">What it takes on its right.</param>
/// <param name="Kinds">The field kinds it applies to; empty for a role row.</param>
/// <param name="NullableOnly">Whether it is offered only on a field that may be empty.</param>
/// <param name="UpdateOnly">Whether it is offered only where a write has both images.</param>
/// <param name="BeforeOnly">Whether it is offered only before the commit (an event envelope carries no roles).</param>
/// <param name="WhenEmpty">What it evaluates to when the field is empty — the hint the form shows.</param>
/// <param name="ReadsImage">
/// Whether its CEL names an image (<c>new.</c> or <c>old.</c>); <c>changed</c> and the role rows do not, so the form asks for
/// none and the generator writes none.
/// </param>
internal sealed record OperatorSpec(
    ConditionOperator Operator,
    string Words,
    string Format,
    OperandKind Operand,
    IReadOnlyList<ConditionFieldKind> Kinds,
    bool NullableOnly,
    bool UpdateOnly,
    bool BeforeOnly,
    string WhenEmpty,
    bool ReadsImage = true);

/// <summary>
/// The one table of what the guided condition may write: operator × field kind × point × canonical CEL × null semantics.
/// </summary>
/// <remarks>
/// <para>
/// <b>One source for four readers</b> (spec §7.1): the form offers from it, <c>ConditionText</c> generates and recognizes
/// with it, and <c>GuidedConditionConformanceTests</c> compiles every cell against the real validator. It replaces the
/// per-point strings <see cref="HookBuilder"/> carried, which drifted (an <c>afterDelete</c> example named <c>new.</c>).
/// </para>
/// <para>
/// <b>It decides what to offer, never what is valid.</b> The live <c>cel/check</c> and the apply judge the text written.
/// Narrower than apply on purpose: no after-commit image the point lacks, no string relational, no negative number, no
/// literal for a moment or an id — each of those is refused by the core, meaningless, or (a negative number, which apply
/// admits since hook arithmetic) a shape the rows do not read back, and stays in text mode.
/// </para>
/// </remarks>
internal static class ConditionTable
{
    /// <summary>The pseudo-field a role row names: the person writing.</summary>
    public const string Writer = "@user";

    /// <summary>The most characters a condition holds (schema <c>$defs/cel</c>).</summary>
    public const int MaxConditionLength = 2000;

    private static readonly ConditionFieldKind[] _compared = [ConditionFieldKind.Text, ConditionFieldKind.Choice, ConditionFieldKind.Number];
    private static readonly ConditionFieldKind[] _number = [ConditionFieldKind.Number];
    private static readonly ConditionFieldKind[] _flag = [ConditionFieldKind.Flag];
    private static readonly ConditionFieldKind[] _identity = [ConditionFieldKind.Identity];
    private static readonly ConditionFieldKind[] _none = [];

    private static readonly ConditionFieldKind[] _every =
        [ConditionFieldKind.Text, ConditionFieldKind.Choice, ConditionFieldKind.Number, ConditionFieldKind.Flag, ConditionFieldKind.Moment, ConditionFieldKind.Identity, ConditionFieldKind.Json];

    private static readonly ConditionFieldKind[] _changeable =
        [ConditionFieldKind.Text, ConditionFieldKind.Choice, ConditionFieldKind.Number, ConditionFieldKind.Flag, ConditionFieldKind.Moment, ConditionFieldKind.Identity];

    /// <summary>Every relation, in the order the form lists them.</summary>
    public static IReadOnlyList<OperatorSpec> Rows { get; } =
    [
        new(ConditionOperator.Is, "is", "{f} == {v}", OperandKind.Literal, _compared, false, false, false, "false: an empty value equals nothing"),
        new(ConditionOperator.IsNot, "is not (and has a value)", "{f} != {v}", OperandKind.Literal, _compared, false, false, false, "false: every comparison with an empty value is false, != included"),
        new(ConditionOperator.IsNotOrEmpty, "is not, or is empty", "!({f} == {v})", OperandKind.Literal, _compared, true, false, false, "true"),
        new(ConditionOperator.Less, "is less than", "{f} < {v}", OperandKind.Literal, _number, false, false, false, "false"),
        new(ConditionOperator.LessOrEqual, "is at most", "{f} <= {v}", OperandKind.Literal, _number, false, false, false, "false"),
        new(ConditionOperator.Greater, "is more than", "{f} > {v}", OperandKind.Literal, _number, false, false, false, "false"),
        new(ConditionOperator.GreaterOrEqual, "is at least", "{f} >= {v}", OperandKind.Literal, _number, false, false, false, "false"),
        new(ConditionOperator.IsTrue, "is true", "{f} == true", OperandKind.None, _flag, false, false, false, "false: empty is neither true nor false"),
        new(ConditionOperator.IsFalse, "is false", "{f} == false", OperandKind.None, _flag, false, false, false, "false: empty is neither true nor false"),
        new(ConditionOperator.HasValue, "has a value", "has({f})", OperandKind.None, _every, true, false, false, "false"),
        new(ConditionOperator.IsEmpty, "is empty", "!has({f})", OperandKind.None, _every, true, false, false, "true"),
        new(ConditionOperator.Changed, "changed", "changed({n})", OperandKind.None, _changeable, false, true, false, "decided by the build", ReadsImage: false),
        new(ConditionOperator.IsTheWriter, "is the person writing", "{f} == @user.id", OperandKind.None, _identity, false, false, false, "false"),
        new(ConditionOperator.HasRole, "has the role", "{r} in @user.roles", OperandKind.Role, _none, false, false, true, "false", ReadsImage: false),
        new(ConditionOperator.LacksRole, "does not have the role", "!({r} in @user.roles)", OperandKind.Role, _none, false, false, true, "true", ReadsImage: false),
    ];

    /// <summary>The row of one relation.</summary>
    /// <param name="relation">The relation.</param>
    public static OperatorSpec Of(ConditionOperator relation) => Rows.Single(spec => spec.Operator == relation);

    /// <summary>What a declared field type is, for a condition.</summary>
    /// <param name="type">The field's declared type.</param>
    /// <exception cref="ArgumentOutOfRangeException">A type this table has no arm for — a new one must be given a kind, not guessed.</exception>
    public static ConditionFieldKind KindOf(FieldType type) => type switch
    {
        FieldType.String or FieldType.Text => ConditionFieldKind.Text,
        FieldType.Enum => ConditionFieldKind.Choice,
        FieldType.Integer or FieldType.Decimal => ConditionFieldKind.Number,
        FieldType.Boolean => ConditionFieldKind.Flag,
        FieldType.Date or FieldType.DateTime => ConditionFieldKind.Moment,
        FieldType.Uuid or FieldType.Ref => ConditionFieldKind.Identity,
        FieldType.Json => ConditionFieldKind.Json,
        _ => throw new ArgumentOutOfRangeException(nameof(type), type, "No condition kind is declared for this field type."),
    };

    /// <summary>Whether a point has the row as the write leaves it.</summary>
    /// <param name="point">The hook point.</param>
    public static bool HasNew(string point) => point is "beforeCreate" or "beforeUpdate" or "afterCreate" or "afterUpdate";

    /// <summary>Whether a point has the row as it was before the write.</summary>
    /// <param name="point">The hook point.</param>
    public static bool HasOld(string point) => point is "beforeUpdate" or "beforeDelete" or "afterUpdate" or "afterDelete";

    /// <summary>Whether a point is an update's, the one write with both images.</summary>
    /// <param name="point">The hook point.</param>
    public static bool IsUpdate(string point) => point.EndsWith("Update", StringComparison.Ordinal);

    /// <summary>The images a condition at this point may read, <c>new</c> first.</summary>
    /// <param name="point">The hook point.</param>
    public static IReadOnlyList<RowImage> ImagesAt(string point)
    {
        var images = new List<RowImage>(2);
        if (HasNew(point))
        {
            images.Add(RowImage.New);
        }

        if (HasOld(point))
        {
            images.Add(RowImage.Old);
        }

        return images;
    }

    /// <summary>Whether a relation is offered on a field of this kind at this point.</summary>
    /// <param name="spec">The relation.</param>
    /// <param name="point">The hook point.</param>
    /// <param name="kind">The field's kind.</param>
    /// <param name="nullable">Whether the field may be empty.</param>
    public static bool Allows(OperatorSpec spec, string point, ConditionFieldKind kind, bool nullable)
        => spec.Operand != OperandKind.Role
           && spec.Kinds.Contains(kind)
           && (!spec.NullableOnly || nullable)
           && (!spec.UpdateOnly || IsUpdate(point))
           && (!spec.BeforeOnly || HookBuilder.IsBefore(point));

    /// <summary>The relations offered on a field of this kind at this point, in the table's order.</summary>
    /// <param name="point">The hook point.</param>
    /// <param name="kind">The field's kind.</param>
    /// <param name="nullable">Whether the field may be empty.</param>
    public static IReadOnlyList<OperatorSpec> For(string point, ConditionFieldKind kind, bool nullable)
        => [.. Rows.Where(spec => Allows(spec, point, kind, nullable))];

    /// <summary>The role relations offered at this point — none after the commit.</summary>
    /// <param name="point">The hook point.</param>
    public static IReadOnlyList<OperatorSpec> ForWriter(string point)
        => [.. Rows.Where(spec => spec.Operand == OperandKind.Role && (!spec.BeforeOnly || HookBuilder.IsBefore(point)))];

    /// <summary>The CEL prefix of an image.</summary>
    /// <param name="image">The image.</param>
    public static string Prefix(RowImage image) => image == RowImage.New ? "new" : "old";

    /// <summary>The images a point has, as the condition hint's markup.</summary>
    /// <param name="point">The hook point.</param>
    public static string ImagesMarkup(string point)
        => string.Join(", ", ImagesAt(point).Select(image => $"<code class=\"a-mono\">{Prefix(image)}</code>"));

    /// <summary>A condition valid at this point, for the placeholder.</summary>
    /// <param name="point">The hook point.</param>
    public static string Example(string point) => point switch
    {
        "beforeCreate" => "new.priority == 'high'",
        "beforeDelete" or "afterDelete" => "old.status == 'completed'",
        _ => "new.status == 'completed'",
    };
}
