namespace MMLib.Alvo.Expressions;

/// <summary>
/// Base type for a CEL expression node. The AST is the product the core hands to a provider — no
/// SQL, no column names — and every case here is one every profile-checked expression can contain;
/// an out-of-profile construct is rejected by the parser or the type checker, never represented.
/// </summary>
/// <remarks>
/// The core's CEL parser builds these records straight off the grammar with
/// <see cref="CelValueType.Null"/> placeholders on every <see cref="CelFieldRef"/> — only the type
/// checker (a later task) knows a row field's real type. A tree fresh off the parser is therefore
/// <b>not</b> a <c>CompiledExpression</c> and must never be rendered to SQL; only a tree that has
/// been through the checker (and, from there, the renderer) is safe to render.
/// </remarks>
public abstract record CelNode;

/// <summary>A constant value. Valid in every profile.</summary>
/// <param name="Type">The literal's runtime type.</param>
/// <param name="Value">The literal's value, or <see langword="null"/> for the CEL <c>null</c> literal.</param>
public sealed record CelLiteral(CelValueType Type, object? Value) : CelNode;

/// <summary>
/// A reference to a row field. Valid in every profile; a Rule/Computed expression only ever sees
/// <see cref="CelRecordState.Current"/> — <see cref="CelRecordState.New"/>/<see cref="CelRecordState.Old"/>
/// are legal only in the <see cref="CelProfile.Condition"/> profile.
/// </summary>
/// <param name="FieldName">The row field's name.</param>
/// <param name="Type">
/// The field's runtime type — <see cref="CelValueType.Null"/> until the type checker resolves it
/// against the entity's schema.
/// </param>
/// <param name="State">Which version of the row this reference reads.</param>
public sealed record CelFieldRef(string FieldName, CelValueType Type, CelRecordState State) : CelNode;

/// <summary>
/// A reference to a caller/tenant context value (Alvo's <c>@user</c>/<c>@tenant</c> extension to
/// CEL). Valid in every profile.
/// </summary>
/// <param name="Value">Which context value this resolves to.</param>
/// <param name="Type">The context value's runtime type.</param>
public sealed record CelContextRef(CelContextValue Value, CelValueType Type) : CelNode;

/// <summary>A unary operator applied to one operand. Valid in every profile.</summary>
/// <param name="Operator">The unary operator.</param>
/// <param name="Operand">The operand.</param>
public sealed record CelUnary(CelUnaryOperator Operator, CelNode Operand) : CelNode;

/// <summary>A binary operator applied to two operands. Valid in every profile.</summary>
/// <param name="Operator">The binary operator.</param>
/// <param name="Left">The left-hand operand.</param>
/// <param name="Right">The right-hand operand.</param>
public sealed record CelBinary(CelBinaryOperator Operator, CelNode Left, CelNode Right) : CelNode;

/// <summary>The standard CEL presence test, <c>has(field)</c>. Valid in every profile.</summary>
/// <param name="Field">The field whose presence is being tested.</param>
public sealed record CelHas(CelFieldRef Field) : CelNode;

/// <summary>A ternary conditional, <c>condition ? whenTrue : whenFalse</c>. Valid in every profile.</summary>
/// <param name="Condition">The condition.</param>
/// <param name="WhenTrue">The value when <paramref name="Condition"/> is true.</param>
/// <param name="WhenFalse">The value when <paramref name="Condition"/> is false.</param>
public sealed record CelConditional(CelNode Condition, CelNode WhenTrue, CelNode WhenFalse) : CelNode;

/// <summary>
/// Tests whether a field's value differs between the old and new row, <c>changed(field)</c>.
/// Legal only in the <see cref="CelProfile.Condition"/> profile.
/// </summary>
/// <param name="FieldName">The field being tested.</param>
public sealed record CelChanged(string FieldName) : CelNode;

/// <summary>Which version of a row a <see cref="CelFieldRef"/> reads.</summary>
public enum CelRecordState
{
    /// <summary>The current row — the only state legal in the Rule/Computed profiles.</summary>
    Current,

    /// <summary>The proposed new row. Legal only in the <see cref="CelProfile.Condition"/> profile.</summary>
    New,

    /// <summary>The row as it was before the change. Legal only in the <see cref="CelProfile.Condition"/> profile.</summary>
    Old,
}

/// <summary>Which caller/tenant context value a <see cref="CelContextRef"/> resolves to.</summary>
public enum CelContextValue
{
    /// <summary>The authenticated caller's user id (<c>@user.id</c>).</summary>
    UserId,

    /// <summary>The authenticated caller's role set (<c>@user.roles</c>).</summary>
    UserRoles,

    /// <summary>The current tenant id (<c>@tenant.id</c>).</summary>
    TenantId,
}

/// <summary>A unary CEL operator.</summary>
public enum CelUnaryOperator
{
    /// <summary>Logical negation, <c>!</c>.</summary>
    Not,

    /// <summary>Arithmetic negation, <c>-</c>.</summary>
    Negate,
}

/// <summary>A binary CEL operator.</summary>
public enum CelBinaryOperator
{
    /// <summary>Equality, <c>==</c>.</summary>
    Equal,

    /// <summary>Inequality, <c>!=</c>.</summary>
    NotEqual,

    /// <summary>Less than, <c>&lt;</c>.</summary>
    Less,

    /// <summary>Less than or equal, <c>&lt;=</c>.</summary>
    LessOrEqual,

    /// <summary>Greater than, <c>&gt;</c>.</summary>
    Greater,

    /// <summary>Greater than or equal, <c>&gt;=</c>.</summary>
    GreaterOrEqual,

    /// <summary>Logical and, <c>&amp;&amp;</c>.</summary>
    And,

    /// <summary>Logical or, <c>||</c>.</summary>
    Or,

    /// <summary>List/set membership, <c>in</c>.</summary>
    In,

    /// <summary>Addition, <c>+</c>.</summary>
    Add,

    /// <summary>Subtraction, <c>-</c>.</summary>
    Subtract,

    /// <summary>Multiplication, <c>*</c>.</summary>
    Multiply,

    /// <summary>Division, <c>/</c>.</summary>
    Divide,
}
