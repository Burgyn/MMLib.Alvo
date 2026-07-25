using MMLib.Alvo.Schema;

namespace MMLib.Alvo.Expressions;

/// <summary>
/// The storage driver's half of SQL predicate rendering. <see cref="IPredicateRenderer"/> composes
/// only SQL <i>structure</i> — <c>COALESCE</c>, <c>AND</c>/<c>OR</c>/<c>NOT</c>, parentheses; every
/// identifier and every dialect-specific keyword or literal comes from this interface instead, so a
/// new storage driver — including F7's dynamic entities, where a field is a JSON path
/// (<c>data->>'owner_id'</c>) rather than a column — only has to implement this interface, never
/// touch the renderer itself.
/// </summary>
public interface IFieldSqlRenderer
{
    /// <summary>Renders a row field as SQL: a quoted column on a physical entity, a JSON path on a dynamic one.</summary>
    /// <remarks>
    /// <paramref name="fieldName"/> is the one string that crosses this boundary unparameterized —
    /// there is no bind-parameter form of a column name. Today it is safe only because the type
    /// checker resolves it ordinally against the entity's own schema and descriptor field names are
    /// pattern-bound at the JSON Schema layer; neither of those guarantees holds for an
    /// <see cref="Schema.EntitySchema"/> a host assembles programmatically, and F7's dynamic-entity
    /// driver interpolates it into a SQL <i>string literal</i> (<c>data->>'owner_id'</c>) rather than
    /// a quoted identifier, where quoting rules differ. An implementation must therefore treat
    /// <paramref name="fieldName"/> as untrusted input and quote or escape it accordingly — never
    /// emit it verbatim.
    /// </remarks>
    /// <param name="entity">The entity <paramref name="fieldName"/> belongs to.</param>
    /// <param name="fieldName">The field's name.</param>
    string RenderField(EntitySchema entity, string fieldName);

    /// <summary>Renders a bind parameter reference for a generated parameter name (e.g. <c>p0</c> → <c>@p0</c>).</summary>
    /// <param name="parameterName">The generated parameter name, without any dialect-specific prefix.</param>
    string RenderParameter(string parameterName);

    /// <summary>Gets the dialect's boolean true literal (e.g. <c>TRUE</c> on PostgreSQL, <c>1</c> on SQLite).</summary>
    string TrueLiteral { get; }

    /// <summary>Gets the dialect's boolean false literal (e.g. <c>FALSE</c> on PostgreSQL, <c>0</c> on SQLite).</summary>
    string FalseLiteral { get; }

    /// <summary>
    /// Renders a case-insensitive <c>LIKE</c> comparison between two already-rendered SQL operands
    /// (e.g. <c>ILIKE</c> on PostgreSQL, an upper-cased <c>LIKE</c> on SQLite).
    /// </summary>
    /// <remarks>
    /// This method only composes the comparison; it does not decide what <paramref name="right"/>'s
    /// bound value may contain. Whether a user-supplied literal reaching a <c>like</c>/<c>matches</c>
    /// operator may itself carry <c>LIKE</c> wildcards (<c>%</c>, <c>_</c>) — and whether that should
    /// be escaped before binding — is unresolved as of this renderer and is for whoever wires up that
    /// operator to decide.
    /// </remarks>
    /// <param name="left">The already-rendered left operand.</param>
    /// <param name="right">The already-rendered right operand.</param>
    string RenderCaseInsensitiveLike(string left, string right);
}
