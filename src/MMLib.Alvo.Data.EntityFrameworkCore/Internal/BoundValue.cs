namespace MMLib.Alvo.Data.EntityFrameworkCore;

/// <summary>Where a value a composed statement binds came from, which decides how it may be bound.</summary>
internal enum BoundValueOrigin
{
    /// <summary>
    /// A value compared against one named column. It <b>must</b> be bound through that column's own type
    /// mapping, because the column decides the representation EF wrote and a value that arrived as some other
    /// CLR type matches nothing and raises nothing.
    /// </summary>
    ColumnComparison,

    /// <summary>
    /// A value the resolved policy predicate carries — a context value or a CEL literal, typed by the CEL type
    /// checker rather than by a caller. See <see cref="BoundValue.FromPolicyPredicate"/> for why that is
    /// sufficient.
    /// </summary>
    PolicyPredicate,

    /// <summary>
    /// A value this data path generated itself, in a CLR type it chose, with no column behind it — the page's
    /// row limit.
    /// </summary>
    Framework,
}

/// <summary>
/// One value a composed statement binds, together with what the binder needs in order to bind it safely.
/// </summary>
/// <remarks>
/// <para>
/// This type exists so that "bind through the column, not through the value's own CLR type" is enforced by
/// the shape of the data rather than remembered at each call site. A flat <c>name → value</c> bag cannot
/// carry the column, so a binder handed one has no choice but to pick a mapping from the value — which is a
/// silent wrong answer on SQLite, where a <c>uuid</c> is upper-case <c>TEXT</c>, a timestamp is
/// <c>'yyyy-MM-dd HH:mm:ss'</c> and a <c>date</c> is a bare calendar day. There is deliberately <b>no</b>
/// constructor that takes a value alone: a fragment author has to name which of the three cases theirs is.
/// </para>
/// <para>
/// The three origins are not interchangeable and the enum is not a hint — <c>PredicateParameterBinder</c>
/// switches on it exhaustively, so a fourth kind of value cannot be added without deciding how it binds.
/// </para>
/// </remarks>
internal sealed class BoundValue
{
    private BoundValue(object? value, string? column, BoundValueOrigin origin)
    {
        Value = value;
        Column = column;
        Origin = origin;
    }

    /// <summary>The value itself.</summary>
    internal object? Value { get; }

    /// <summary>
    /// The declared field name whose column this value is compared against, or <see langword="null"/> for
    /// every origin but <see cref="BoundValueOrigin.ColumnComparison"/>.
    /// </summary>
    internal string? Column { get; }

    /// <summary>Where this value came from.</summary>
    internal BoundValueOrigin Origin { get; }

    /// <summary>
    /// A value compared against <paramref name="column"/> — a caller filter's operand, a keyset cursor's
    /// anchor value, a row id. The only shape a caller-supplied value may take.
    /// </summary>
    /// <param name="column">The declared field name, resolved against the entity's schema before this call.</param>
    /// <param name="value">The value.</param>
    internal static BoundValue ForColumn(string column, object? value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(column);
        return new BoundValue(value, column, BoundValueOrigin.ColumnComparison);
    }

    /// <summary>
    /// A value the resolved policy predicate carries, bound through its own CLR type because there is no
    /// column to consult: a rendered <c>SqlPredicate</c> records names and values only.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Why that is sufficient, and why it is not an exception waiting to bite.</b> Every value here is
    /// either a context value or a CEL literal, and the CEL type checker has already forced both operands of
    /// the comparison to one type. The literal kinds the grammar admits are exactly <c>Int</c>,
    /// <c>Decimal</c>, <c>String</c>, <c>Bool</c> and <c>Null</c>, and the context values are a
    /// <see cref="System.Guid"/> or the role set; there is <b>no date or timestamp literal in the language at
    /// all</b>, so the one mismatch the collapse of <c>date</c> and <c>timestamp</c> into one CEL type could
    /// otherwise produce is unreachable — a rule comparing a <c>date</c> field against anything the grammar
    /// can express fails to compile. The one reachable numeric mismatch is an <c>Int</c> literal against a
    /// <c>Decimal</c> column, which promotes to a <c>Decimal</c> comparison and is repaired on both operands
    /// by <c>IFieldSqlRenderer.RenderComparableOperands</c>.
    /// </para>
    /// <para>
    /// Pinned by <c>CelRuleBindingTests</c>: if the grammar ever grows a temporal literal, that test fails
    /// and this argument has to be replaced by carrying the field through <c>SqlPredicate</c>.
    /// </para>
    /// </remarks>
    /// <param name="value">The value.</param>
    internal static BoundValue FromPolicyPredicate(object? value) =>
        new(value, null, BoundValueOrigin.PolicyPredicate);

    /// <summary>
    /// A value this data path generated itself, in a CLR type it chose — today only a page's row limit. Named
    /// so that it cannot be reached for by a fragment that is really binding a caller's value.
    /// </summary>
    /// <param name="value">The value.</param>
    internal static BoundValue FromFramework(object? value) => new(value, null, BoundValueOrigin.Framework);
}
