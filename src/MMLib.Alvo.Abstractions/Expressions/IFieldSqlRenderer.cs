using MMLib.Alvo.Schema;

namespace MMLib.Alvo.Expressions;

/// <summary>
/// The storage driver's half of SQL predicate rendering. <see cref="IPredicateRenderer"/> composes
/// only SQL <i>structure</i> — <c>AND</c>/<c>OR</c>/<c>NOT</c>, parentheses; every identifier, every
/// dialect-specific keyword or literal, and the dialect's own way of forcing a predicate to be
/// two-valued come from this interface instead, so a new storage driver — including F7's dynamic
/// entities, where a field is a JSON path (<c>data->>'owner_id'</c>) rather than a column — only has to
/// implement this interface, never touch the renderer itself.
/// </summary>
/// <remarks>
/// The three two-valued members below ship as <b>default interface members</b> carrying the
/// PostgreSQL/SQLite shape, so an existing implementation keeps compiling and keeps its current
/// rendering. A dialect whose boolean handling differs overrides them; SQL Server / Azure SQL — which
/// §0 principle 3 requires the core to support — has to, since T-SQL has no boolean type and no
/// boolean-valued expression, making <c>COALESCE(&lt;predicate&gt;, 0)</c> unparseable exactly where a
/// <c>WHERE</c> clause expects a predicate.
/// </remarks>
public interface IFieldSqlRenderer
{
    /// <summary>
    /// Folds an already-rendered <b>predicate</b> whose result may be SQL's <c>UNKNOWN</c> back into a
    /// predicate that is only ever true or false — the single rule both of Alvo's expression backends
    /// must agree on (see <c>CelInterpreter</c>'s remarks). The default is
    /// <c>COALESCE(&lt;predicate&gt;, &lt;false&gt;)</c>, which PostgreSQL and SQLite accept in boolean
    /// position; T-SQL needs <c>(CASE WHEN &lt;predicate&gt; THEN 1 ELSE 0 END = 1)</c> instead.
    /// </summary>
    /// <param name="predicate">The already-rendered, possibly three-valued predicate.</param>
    string RenderTwoValued(string predicate) => $"COALESCE({predicate}, {FalseLiteral})";

    /// <summary>
    /// Reads an already-rendered <b>nullable boolean value</b> — a boolean column, or F7's JSON path to
    /// one — as a two-valued predicate. Distinct from <see cref="RenderTwoValued"/> because the input is
    /// a value, not a predicate: on a dialect with a real boolean type the two collapse identically, but
    /// T-SQL has to default the <c>bit</c> in value position and compare
    /// (<c>(COALESCE(&lt;value&gt;, 0) = 1)</c>), where wrapping it in a <c>CASE WHEN</c> would not even
    /// parse.
    /// </summary>
    /// <param name="booleanValue">The already-rendered nullable boolean value.</param>
    string RenderBooleanFieldAsPredicate(string booleanValue) => $"COALESCE({booleanValue}, {FalseLiteral})";

    /// <summary>
    /// Renders a boolean <b>constant</b> in predicate position — the answer for a rule that resolves at
    /// render time (a <c>true</c>/<c>false</c> literal, a role-membership test decided against the known
    /// role set). Defaults to the dialect's boolean literals, which stand alone as predicates on
    /// PostgreSQL and SQLite; T-SQL needs a comparison (<c>(1 = 1)</c>), since <c>WHERE 1</c> is not
    /// valid there.
    /// </summary>
    /// <param name="value">The constant verdict.</param>
    string RenderBooleanPredicate(bool value) => value ? TrueLiteral : FalseLiteral;

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

    /// <summary>
    /// Wraps <b>both</b> already-rendered operands of one comparison so this dialect compares them by
    /// <b>value</b>. A dialect whose storage for <paramref name="type"/> does not order the way the type
    /// does repairs the comparison here, in one place. The default returns the pair unchanged, which is
    /// right for any engine with a real storage type per Alvo field type.
    /// </summary>
    /// <remarks>
    /// <para>
    /// SQLite is why this exists. It has no decimal storage class, so EF maps a
    /// <see cref="CelValueType.Decimal"/> field to a <c>TEXT</c> column and an unguarded
    /// <c>price &gt; 100</c> becomes a <em>string</em> comparison: it matches a row whose price is
    /// <c>12.34</c>, and <c>price != 100</c> matches a row whose price <em>is</em> 100. On PostgreSQL's
    /// <c>numeric</c> the same rule answers correctly — so a rule gating access on an amount admits
    /// different rows per engine, which is a fail-open authorization outcome on one of them and exactly
    /// what §0's engine-agnostic core principle forbids. SQLite's driver therefore casts both operands of a
    /// decimal comparison to <c>REAL</c>.
    /// </para>
    /// <para>
    /// <b>The pair is the signature, not a convention.</b> Repairing one side only does not merely leave the
    /// comparison suboptimal — it produces a <em>new</em> wrong answer, because SQLite orders every
    /// <c>TEXT</c> value above every <c>REAL</c> one, so a cast column against an uncast parameter inverts
    /// rather than approximates. Taking and returning both operands together makes that mistake
    /// unrepresentable at every call site, which a per-operand member left to each caller's memory. The
    /// operator itself stays with the caller: which comparison this is, is Alvo's semantics, not a driver's.
    /// </para>
    /// <para>
    /// <paramref name="type"/> is the type the comparison is <em>evaluated</em> at, after CEL's numeric
    /// promotion, so a whole-number literal compared against a decimal column arrives as
    /// <see cref="CelValueType.Decimal"/> rather than <see cref="CelValueType.Int"/>. It is deliberately a
    /// CEL type and not a store type: a store type is resolved by the provider's own type mapping from the
    /// column, so naming one here would add a second authority for it. This asks a driver only the question
    /// it alone can answer — "does my storage for this type order the way the type does?".
    /// </para>
    /// <para>
    /// <b>What an implementation must expect.</b> It is called for <em>every</em> comparison and therefore
    /// for every <see cref="CelValueType"/>, so a dialect must return the operands unchanged for the types
    /// it has no repair for. Either operand may be a <em>bind-parameter marker</em> (<c>@alvo_f0</c>) rather
    /// than a quoted column, so an implementation must not assume it can qualify or introspect what it is
    /// handed. It is <b>not</b> called for a <c>LIKE</c> or case-insensitive-<c>LIKE</c> pattern match (a
    /// string operation by definition), for <c>has(...)</c> (an <c>IS NOT NULL</c> test), or for CEL role
    /// membership (decided against the caller's own role set, never compared in SQL) — and it <b>is</b>
    /// called once per candidate of a value-membership <c>IN (…)</c> list, which is a set of equality
    /// comparisons sharing one left operand.
    /// </para>
    /// <para>
    /// <b>This member also renders <c>ORDER BY</c>, so the repair must be order-preserving and not merely
    /// comparison-consistent.</b> A storage driver asks it with the <em>same</em> operand on both sides and
    /// uses either result as an ordering key, so whatever it returns decides how rows sort as well as how they
    /// compare. That is deliberate: a keyset page is correct only while its <c>ORDER BY</c> and its cursor
    /// boundary describe the <em>same</em> total order, and rendering both from one member is what makes them
    /// unable to drift. The consequence for an implementation is a real constraint — a repair that is a valid
    /// equivalence but not a valid ordering breaks paging while looking perfectly reasonable in isolation.
    /// <c>LOWER(x)</c> for a case-insensitive string comparison is the trap: it is a sound comparison repair
    /// and a wrong ordering key, and a page built on it silently skips or repeats rows. A dialect needing that
    /// kind of repair must express it in the <em>operator</em> instead (see
    /// <see cref="RenderCaseInsensitiveLike"/>, which is why case-insensitive matching has a member of its
    /// own), and return the operands unchanged here. Formally: for all <c>a</c>, <c>b</c> of
    /// <paramref name="type"/>, <c>a &lt; b</c> must imply <c>repair(a) &lt; repair(b)</c>. A repair that
    /// merges values the type distinguishes (SQLite's <c>REAL</c> cast beyond 53 bits of mantissa) is
    /// acceptable, because both sides then agree the two are tied and the row key breaks the tie; a repair that
    /// <em>reorders</em> them is not.
    /// </para>
    /// <para>
    /// An implementation must return expressions rather than predicates, and must preserve
    /// <see langword="null"/>: a wrapper that turned a <c>NULL</c> operand into a value would break the
    /// three-valued fold every comparison goes through. A cast that costs an index scan is an accepted
    /// price for a correct answer; a dialect with a cheaper repair should prefer it.
    /// </para>
    /// </remarks>
    /// <param name="left">The already-rendered left operand.</param>
    /// <param name="right">The already-rendered right operand.</param>
    /// <param name="type">The type the comparison is evaluated at.</param>
    (string Left, string Right) RenderComparableOperands(string left, string right, CelValueType type) =>
        (left, right);
}
