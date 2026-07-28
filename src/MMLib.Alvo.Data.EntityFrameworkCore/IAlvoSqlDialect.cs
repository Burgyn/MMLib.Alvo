using MMLib.Alvo.Schema;

namespace MMLib.Alvo.Data.EntityFrameworkCore;

/// <summary>
/// The storage driver's half of <b>statement</b> shape, as
/// <see cref="MMLib.Alvo.Expressions.IFieldSqlRenderer"/> is its half of <b>expression</b> shape. The
/// shared EF data path composes only structure — a <c>SELECT</c> list, a <c>FROM</c>, the
/// <c>AND</c>-joined <c>WHERE</c> — and asks this interface for everything a dialect owns: how a table
/// is named, how a column is quoted, how a typed SQL <c>NULL</c> is spelled for a masked field, and
/// whether a pre-image read can be locked.
/// </summary>
/// <remarks>
/// <para>
/// This lives beside the EF data path rather than in <c>Abstractions</c> on purpose: statement shape is
/// a relational concern, and <c>Abstractions</c> is required to stay free of one. It is also why
/// <see cref="MMLib.Alvo.Expressions.IFieldSqlRenderer"/> was not extended with a table-rendering
/// member — that port renders expressions, and every existing implementation (including
/// <c>MMLib.Alvo.Testing.TestFieldSqlRenderer</c>) would have had to grow a member it has no table for.
/// </para>
/// <para>
/// F7's dynamic-entity driver implements this interface too: there <see cref="RenderTable"/> returns a
/// JSON-projecting sub-select over the one shared partitioned store rather than a table name, which is
/// what makes "the same adversarial suite passes over a physical and a virtual entity" a matter of
/// registering another dialect instead of rewriting the data path.
/// </para>
/// <para>
/// <b>Every obligation below is asserted generically</b> by
/// <c>MMLib.Alvo.Testing.Data.AlvoSqlDialectContractTests</c>, which an implementation's own test class
/// inherits — this repo's idiom for a port, and the reason the return grammars here are a contract rather
/// than advice. An implementation is expected to pair its dialect with its
/// <see cref="MMLib.Alvo.Expressions.IFieldSqlRenderer"/> there, because the two halves of one driver's SQL
/// have to agree about where a row lock lives.
/// </para>
/// </remarks>
public interface IAlvoSqlDialect
{
    /// <summary>
    /// Renders the table source <paramref name="entity"/>'s rows are read from — a quoted table name on
    /// a physical entity — plus, where the engine's grammar puts it there, the row-locking hint a
    /// pre-image read needs.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A driver must not qualify the name with a database schema unless it actually has one: SQLite has
    /// no schemas at all, and <c>AlvoOptions.SchemaPrefix</c> is a framework-<em>table</em> name prefix,
    /// not a schema. Both in-repo drivers return the bare quoted entity name, matching the
    /// <c>ToTable(entity.Name)</c> the migration model already uses.
    /// </para>
    /// <para>
    /// <b>Return grammar.</b> The result is interpolated verbatim as the <c>FROM</c> clause's table
    /// source. It must not include the <c>FROM</c> keyword, must not carry an alias, and must not carry a
    /// statement terminator or any surrounding whitespace. A dialect whose table source is a query rather
    /// than a name — F7's dynamic driver, which projects JSON paths out of one shared partitioned store —
    /// must return it <b>parenthesised</b> (<c>(SELECT … FROM …)</c>), because the composer adds no
    /// parentheses of its own; EF then wraps the whole <c>FromSql</c> root in its own derived table and
    /// supplies the alias.
    /// </para>
    /// <para>
    /// <b>Why this member is told about the lock, when <see cref="RowLockClause"/> exists.</b> Row locking
    /// has two grammars, and only one of them is a trailing clause. PostgreSQL appends
    /// <c>FOR NO KEY UPDATE</c> at the end of the statement; T-SQL (SQL Server / Azure SQL) has no
    /// trailing equivalent at all and expresses the same thing as a <b>table hint in the <c>FROM</c></b>
    /// (<c>FROM notes WITH (UPDLOCK, ROWLOCK)</c>). A seam offering only the trailing position would leave
    /// a T-SQL driver two choices, both wrong: answer a hint from <see cref="RowLockClause"/> and produce a
    /// syntax error, or answer <see cref="string.Empty"/> — which is <em>legitimate</em> there (it is
    /// SQLite's answer) and therefore silently ships unlocked <c>WITH CHECK</c> pre-images, a real
    /// time-of-check/time-of-use race on an engine defaulting to READ COMMITTED, indistinguishable from
    /// correct SQLite behaviour. §0 principle 3 names Azure SQL explicitly and no such driver exists yet, so
    /// the seam's shape is the only thing protecting its author. <c>MMLib.Alvo.Testing.Data.TSqlSqlDialect</c>
    /// is the rehearsal that it is sufficient.
    /// </para>
    /// <para>
    /// <b>Exactly one position may carry the lock.</b> A dialect that answers a different table source for a
    /// locking pre-image than for an ordinary read must return <see cref="string.Empty"/> from
    /// <see cref="RowLockClause"/> for that same mutation: locking twice is not twice as safe, it is an
    /// engine-dependent error. <c>MMLib.Alvo.Testing.Data.AlvoSqlDialectContractTests</c> asserts that pairing,
    /// so a driver cannot satisfy half of it.
    /// </para>
    /// </remarks>
    /// <param name="entity">The entity being read.</param>
    /// <param name="lockedPreImageFor">
    /// The mutation this read's row is a locked pre-image for, or <see langword="null"/> when the read takes
    /// no lock (a <c>list</c>, a <c>get</c>, a <c>create</c>'s re-read). A dialect whose locking grammar is
    /// the trailing clause ignores it; the argument's presence is what lets one whose grammar is a table hint
    /// answer at all. It is <see cref="PreImageMutation"/> rather than a bare flag for the reason
    /// <see cref="RowLockClause"/> takes one: a delete's pre-image needs a stronger lock than an update's, on
    /// either grammar.
    /// </param>
    /// <exception cref="ArgumentNullException"><paramref name="entity"/> is <see langword="null"/>.</exception>
    string RenderTable(EntitySchema entity, PreImageMutation? lockedPreImageFor);

    /// <summary>Renders a column reference in a <c>SELECT</c> list or an <c>ORDER BY</c>.</summary>
    /// <remarks>
    /// <para>
    /// <b>Return grammar.</b> A bare column reference: quoted per this dialect, with no table or alias
    /// qualifier, no <c>AS</c> alias of its own, and no separating comma — the composer joins the
    /// <c>SELECT</c> list and appends an alias where one is needed (see
    /// <see cref="RenderNullProjection"/>).
    /// </para>
    /// <para>
    /// <b>Delimit unconditionally, and escape rather than concatenate.</b> A name is quoted even where the
    /// engine would accept it bare: Npgsql's own <c>DelimitIdentifier</c> returns <c>plate</c> undelimited,
    /// PostgreSQL then case-folds it, and the same field renders differently per driver — so a rule and a
    /// caller filter over one column can disagree about which column that is (spike <c>Q8</c>). Escaping is
    /// what makes the rendering injective: two different names must never produce one string, because a
    /// rendering that collapses them is a rendering a name can escape through. Both are asserted generically
    /// by <c>MMLib.Alvo.Testing.Data.AlvoSqlDialectContractTests</c>.
    /// </para>
    /// </remarks>
    /// <param name="columnName">The column's name.</param>
    string RenderColumn(string columnName);

    /// <summary>
    /// Renders a typed SQL <c>NULL</c> standing in for a masked field's value — the mechanism that keeps
    /// a <c>hidden</c> field's data inside the table. An untyped bare <c>NULL</c> is not enough: the
    /// result set has to satisfy the mapped property's store type, so the cast names
    /// <paramref name="storeType"/>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A dialect decides only the <em>cast syntax</em>; it must not decide the type. The one authority for
    /// "what store type does this column have" is EF's own <c>IRelationalTypeMappingSource</c>, reached
    /// through the mapped property (<c>IProperty.GetColumnType()</c>) — the very thing that produced the
    /// table's DDL, so it honours the <c>HasMaxLength</c>/<c>HasPrecision</c> calls
    /// <c>DescriptorModelBuilder.ConfigureField</c> makes, and it is per provider by construction. A
    /// dialect deriving a type name from a <see cref="FieldSchema"/> instead would be a second,
    /// unreconciled authority, and this port's first revision proved that drifts immediately: it answered
    /// <c>numeric(18,2)</c> for every <c>decimal</c> regardless of its declared precision, <c>jsonb</c>
    /// for a <c>json</c> field whose column the migrator creates as <c>text</c>, and <c>text</c> for a
    /// length-bounded string whose column is <c>character varying(N)</c>.
    /// </para>
    /// <para>
    /// <b>Return grammar.</b> The result is a bare SQL <em>expression</em>, interpolated verbatim into a
    /// <c>SELECT</c> list. It must not carry an <c>AS &lt;column&gt;</c> alias — the composer appends the
    /// masked field's alias itself, through <see cref="RenderColumn"/> — and it must not carry a
    /// separating comma.
    /// </para>
    /// <para>
    /// <paramref name="storeType"/> reaches the SQL text unparameterized, because a type name has no
    /// bind-parameter form. That is safe only because it comes from EF's type mapping; a dialect must never
    /// be handed one assembled from caller input.
    /// </para>
    /// </remarks>
    /// <param name="storeType">
    /// The masked column's EF-resolved store type, exactly as this provider spells it (e.g.
    /// <c>character varying(32)</c>, <c>numeric(10,4)</c>, <c>TEXT</c>).
    /// </param>
    /// <exception cref="ArgumentException"><paramref name="storeType"/> is null, empty or whitespace.</exception>
    string RenderNullProjection(string storeType);

    /// <summary>
    /// Renders the row-locking clause appended to the pre-image read that precedes
    /// <paramref name="mutation"/>, so a concurrent writer cannot change the row between the decision and
    /// the write — <c>FOR NO KEY UPDATE</c> before an update and <c>FOR UPDATE</c> before a delete on
    /// PostgreSQL, the empty string where the engine has no such clause and serializes write transactions
    /// instead (SQLite), or where the dialect has already taken the lock as a table hint in the
    /// <c>FROM</c> (T-SQL — see <see cref="RenderTable"/>).
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Return grammar.</b> The clause itself, carrying <b>no separator of its own</b> — no leading
    /// space, no trailing space, no terminator. The composer inserts the separating space, and only when
    /// the value is non-empty. A dialect that shipped its own leading space would double the separator
    /// here and a dialect that omitted one under the opposite convention would produce
    /// <c>… WHERE &lt;predicate&gt;FOR NO KEY UPDATE</c> — a syntax error in the one statement a
    /// <c>WITH CHECK</c> verdict is based on. Return <see cref="string.Empty"/>, not <c>" "</c>, when the
    /// engine has no such clause.
    /// </para>
    /// <para>
    /// <b>The empty string means two different things, and that is why <see cref="RenderTable"/> takes the
    /// mutation.</b> It means "this engine needs no locking read" (SQLite, which serializes write
    /// transactions database-wide) <em>and</em> "this dialect locks in the other position" (T-SQL). Both are
    /// legitimate; what would not be legitimate is a third reading — "this engine locks with a trailing
    /// clause and I forgot to emit one" — which is precisely what a seam with only this member left a T-SQL
    /// driver author to produce. A dialect must not answer here <b>and</b> hint the table source for the same
    /// mutation.
    /// </para>
    /// <para>
    /// <b>Why the lock mode depends on the operation, and is therefore an argument rather than a fixed
    /// value.</b> PostgreSQL documents <c>FOR NO KEY UPDATE</c> as the mode for a locking read that
    /// precedes an <c>UPDATE</c> not touching the row's key: it takes a weaker lock than
    /// <c>FOR UPDATE</c> and, unlike it, does not block a concurrent transaction that needs a
    /// <c>FOR KEY SHARE</c> lock on this row — which is exactly what a foreign-key check from another
    /// table takes (PostgreSQL, <i>SELECT</i> reference, "The Locking Clause"; <i>Explicit Locking</i>
    /// §13.3.2, which describes <c>FOR NO KEY UPDATE</c> as the mode that does not block
    /// <c>FOR KEY SHARE</c>). Alvo's update path provably never changes a key: the row id is
    /// framework-owned and a caller-supplied <c>id</c> in an update payload is rejected before the
    /// pre-image is read, and since a <c>Ref</c> field carries a real foreign key
    /// (<c>DescriptorModelBuilder.ConfigureReferences</c>), taking the stronger lock there would
    /// serialize unrelated inserts against the row for no benefit. A <b>delete</b> is the opposite case:
    /// it removes the key, so the very <c>FOR KEY SHARE</c> lock <c>FOR NO KEY UPDATE</c> declines to
    /// block is the one that must be blocked, and the pre-image read takes the full <c>FOR UPDATE</c>.
    /// </para>
    /// <para>
    /// The argument is <see cref="PreImageMutation"/> rather than the policy vocabulary's
    /// <see cref="MMLib.Alvo.Rules.DataOperation"/> because only two operations read a row they are about
    /// to change; the other three have no pre-image, and a dialect should not have to refuse them at
    /// runtime — on an engine with no locking clause the empty string is a <em>legitimate</em> answer, so
    /// answering a list or a create with it would make a caller's bug indistinguishable from a real
    /// result. A two-member enum makes that mistake a compile error instead, in every dialect including
    /// ones Alvo will never see.
    /// </para>
    /// </remarks>
    /// <param name="mutation">The mutation the locked pre-image read precedes.</param>
    string RowLockClause(PreImageMutation mutation);

    /// <summary>
    /// Renders the clause that truncates an ordered page to at most the bound number of rows —
    /// <c>LIMIT &lt;marker&gt;</c> on both engines Alvo ships, <c>FETCH FIRST &lt;marker&gt; ROWS ONLY</c> in
    /// standard SQL, an <c>OFFSET … FETCH</c> pair on T-SQL.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A <b>default interface member</b>, exactly like
    /// <see cref="MMLib.Alvo.Expressions.IFieldSqlRenderer"/>'s three two-valued members and for the same
    /// reason: the default is right for every engine that spells this the PostgreSQL/SQLite way, so only a
    /// dialect that genuinely differs implements it and adding it breaks no existing implementation.
    /// </para>
    /// <para>
    /// <b>Why the limit is inside this statement rather than a LINQ <c>Take</c>.</b> EF pushes a
    /// <c>FromSql</c> body into a derived table as soon as anything is composed over it, and a derived
    /// table's row order is not guaranteed to survive into the outer query — so a limit applied outside
    /// truncates a set whose order is undefined, which is a page that can skip or repeat a row. Ordering and
    /// truncation live in one statement so they cannot come apart.
    /// </para>
    /// <para>
    /// <b>Return grammar.</b> The clause itself, carrying <b>no separator of its own</b> — no leading space,
    /// no terminator. The composer inserts the separating space and places the result after the
    /// <c>ORDER BY</c> and before <see cref="RowLockClause"/>, which is where both engines' grammar puts it.
    /// </para>
    /// </remarks>
    /// <param name="rowCountParameterMarker">
    /// The already-rendered bind-parameter reference holding the row count (e.g. <c>@alvo_limit</c>). A
    /// marker rather than a number, because a limit is caller-supplied and this data path formats no
    /// caller-supplied value into SQL text.
    /// </param>
    string RowLimitClause(string rowCountParameterMarker) => $"LIMIT {rowCountParameterMarker}";

    /// <summary>
    /// Renders the clause that skips <paramref name="rowOffsetParameterMarker"/> leading rows of the
    /// ordered, policy-filtered set — <c>OFFSET &lt;marker&gt;</c> on both engines Alvo ships.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A <b>default interface member</b>, exactly like <see cref="RowLimitClause"/> and for the same
    /// reason: the default is right for every engine that spells this the PostgreSQL/SQLite way, so only a
    /// dialect that genuinely differs implements it.
    /// </para>
    /// <para>
    /// Separate from <see cref="RowLimitClause"/> rather than folded into it, because the two engines Alvo
    /// ships agree an offset is composed <em>after</em> a limit (<c>LIMIT n OFFSET m</c>) while T-SQL spells
    /// the pair the other way around and cannot render a limit without one (<c>OFFSET n ROWS FETCH NEXT m
    /// ROWS ONLY</c> — the leading <c>OFFSET</c> is not optional there). A driver that needs to fuse them,
    /// the way T-SQL does, overrides both members rather than gaining a second parameter on this one; see
    /// <c>MMLib.Alvo.Testing.Data.TSqlSqlDialect</c>.
    /// </para>
    /// <para>
    /// <b>Return grammar.</b> The clause itself, carrying <b>no separator of its own</b> — no leading space,
    /// no terminator — exactly like <see cref="RowLimitClause"/>. The composer inserts the separating space
    /// and places the result immediately after the limit clause, which is where both shipped engines'
    /// grammar puts it.
    /// </para>
    /// </remarks>
    /// <param name="rowOffsetParameterMarker">
    /// The already-rendered bind-parameter reference holding the number of rows to skip (e.g.
    /// <c>@alvo_offset</c>). A marker rather than a number, for the same reason
    /// <paramref name="rowOffsetParameterMarker"/>'s sibling on <see cref="RowLimitClause"/> is.
    /// </param>
    string RowOffsetClause(string rowOffsetParameterMarker) => $"OFFSET {rowOffsetParameterMarker}";
}
