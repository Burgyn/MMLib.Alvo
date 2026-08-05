using MMLib.Alvo.Schema;
using System.Data.Common;

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
    /// Renders the clause that limits a read to <paramref name="rowCountParameterMarker"/> rows, skipping
    /// <paramref name="rowOffsetParameterMarker"/> leading rows when one is given — <c>LIMIT &lt;marker&gt;
    /// [OFFSET &lt;marker&gt;]</c> on both engines Alvo ships, an <c>OFFSET … FETCH</c> pair on T-SQL.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The limit and the offset are ONE member because they are one clause on at least one target
    /// engine.</b> T-SQL spells the window <c>OFFSET &lt;m&gt; ROWS FETCH NEXT &lt;n&gt; ROWS ONLY</c> —
    /// offset first, and <c>FETCH</c> cannot appear without a preceding <c>OFFSET</c> — while SQLite spells
    /// it the other way around (<c>LIMIT &lt;n&gt; OFFSET &lt;m&gt;</c>) and rejects a bare <c>OFFSET</c>
    /// with no <c>LIMIT</c> at all. An earlier revision of this port split the two into
    /// <c>RowLimitClause</c> and <c>RowOffsetClause</c>, and the split let a dialect answer each half
    /// correctly on its own while the *pair* was wrong: <c>MMLib.Alvo.Testing.Data.TSqlSqlDialect</c>'s
    /// <c>RowLimitClause</c> hard-coded <c>OFFSET 0 ROWS</c> because it had no way to see the caller's real
    /// offset, so a driver also implementing <c>RowOffsetClause</c> would have emitted two conflicting
    /// <c>OFFSET</c> clauses — a silently wrong page, not a compile error or a thrown exception. One member
    /// that receives both markers together makes that shape unrepresentable.
    /// </para>
    /// <para>
    /// A <b>default interface member</b>, exactly like
    /// <see cref="MMLib.Alvo.Expressions.IFieldSqlRenderer"/>'s three two-valued members and for the same
    /// reason: the default is right for every engine that spells this the PostgreSQL/SQLite way, so only a
    /// dialect that genuinely differs implements it and adding it breaks no existing implementation.
    /// </para>
    /// <para>
    /// <b>Why the window is inside this statement rather than a LINQ <c>Take</c>/<c>Skip</c>.</b> EF pushes
    /// a <c>FromSql</c> body into a derived table as soon as anything is composed over it, and a derived
    /// table's row order is not guaranteed to survive into the outer query — so a window applied outside
    /// truncates a set whose order is undefined, which is a page that can skip or repeat a row. Ordering and
    /// truncation live in one statement so they cannot come apart.
    /// </para>
    /// <para>
    /// <b>An offset with no explicit caller limit is not this member's problem to solve.</b> SQLite cannot
    /// render a bare <c>OFFSET</c>, so the composer always supplies a row count — the caller's own
    /// <see cref="MMLib.Alvo.Data.AlvoQuery.Limit"/> when one was given, or a sentinel meaning "no bound"
    /// when only <see cref="MMLib.Alvo.Data.AlvoQuery.Offset"/> was. <paramref name="rowCountParameterMarker"/>
    /// is therefore never optional here: a dialect is never asked to render an offset alone.
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
    /// <param name="rowOffsetParameterMarker">
    /// The already-rendered bind-parameter reference holding the number of leading rows to skip (e.g.
    /// <c>@alvo_offset</c>), or <see langword="null"/> when the read has no offset. A marker rather than a
    /// number, for the same reason <paramref name="rowCountParameterMarker"/> is.
    /// </param>
    string RowWindowClause(string rowCountParameterMarker, string? rowOffsetParameterMarker = null) =>
        rowOffsetParameterMarker is null
            ? $"LIMIT {rowCountParameterMarker}"
            : $"LIMIT {rowCountParameterMarker} OFFSET {rowOffsetParameterMarker}";

    /// <summary>
    /// The column definition this engine spells for a <b>stored generated column</b> — the mechanism a
    /// descriptor's <c>field.computed</c> is honoured by — or <see langword="null"/> when the engine cannot
    /// express one, in which case the migrator refuses the field and names the engine.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Stored, never virtual, and that is a portability decision rather than a default.</b> SQLite accepts
    /// <c>VIRTUAL</c> exactly where it refuses <c>STORED</c>, so emitting it there would make one descriptor
    /// produce a column PostgreSQL can index and filter on and SQLite cannot — §0 principle 3's own failure
    /// mode, silently rather than loudly, and precisely the drawback <c>baas-analyza</c> names for the
    /// aggregate-at-read alternative.
    /// </para>
    /// <para>
    /// A <b>default interface member</b>, like <see cref="RowWindowClause"/>, so adding it breaks no existing
    /// implementation. The default is <see langword="null"/> rather than a spelling, and that is the one
    /// difference from that precedent: there is no majority spelling to inherit — SQL Server / Azure SQL says
    /// <c>AS (&lt;expr&gt;) PERSISTED</c> and names no type — so a guessed default would produce either DDL the
    /// engine rejects at migration time or, worse, an ordinary column nothing maintains.
    /// </para>
    /// <para>
    /// <b>Return grammar.</b> One column definition, exactly as it appears inside a <c>CREATE TABLE</c> column
    /// list or after <c>ADD COLUMN</c>: the quoted column name, the store type where the engine wants one, and
    /// the generation clause. No separating comma, no <c>ADD COLUMN</c> keyword, no terminator, no surrounding
    /// whitespace.
    /// </para>
    /// <para>
    /// <paramref name="renderedExpression"/> reaches the SQL text unparameterized because DDL has no
    /// bind-parameter form at all. That is safe only because it comes from
    /// <see cref="MMLib.Alvo.Expressions.IPredicateRenderer"/>'s scalar entry point over a <b>compiled</b> CEL
    /// AST, so it can contain nothing but this entity's own field references, arithmetic and
    /// <c>CASE WHEN</c> — never a descriptor string spliced in, which is what #20 removed as an
    /// arbitrary-DDL-injection vector. A dialect must never be handed one assembled from caller input.
    /// </para>
    /// </remarks>
    /// <param name="columnName">The column's name, to be delimited by this dialect.</param>
    /// <param name="storeType">
    /// The column's EF-resolved store type, exactly as this provider spells it — the same authority
    /// <see cref="RenderNullProjection"/> takes it from, for the same reason.
    /// </param>
    /// <param name="renderedExpression">The already-rendered SQL scalar expression the column is generated from.</param>
    string? GeneratedColumnDefinition(string columnName, string storeType, string renderedExpression) => null;

    /// <summary>
    /// The statements this engine needs run <b>outside</b> a migration's transaction, around the plan's own
    /// SQL — <see cref="MigrationBatchFraming.None"/> for an engine that needs none.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A <b>default interface member</b> answering "nothing", so no existing implementation breaks and no
    /// engine is assumed to have a peculiarity it does not have. See
    /// <see cref="MigrationBatchFraming"/> for the measured data loss that made this necessary: SQLite's
    /// <c>PRAGMA foreign_keys</c> is a no-op inside a transaction, and a table rebuild inside one therefore
    /// cascades away the child rows of every <c>onDelete: "cascade"</c> reference to the table being rebuilt.
    /// </para>
    /// <para>
    /// The migrator runs <see cref="MigrationBatchFraming.After"/> even when the batch failed, so a suspension
    /// is never left in place on a connection a pool may hand out again.
    /// </para>
    /// </remarks>
    MigrationBatchFraming MigrationFraming => MigrationBatchFraming.None;

    /// <summary>
    /// Decides whether <paramref name="failure"/> is this engine refusing a write on a constraint a
    /// <em>caller</em> can do something about — a <c>unique</c> collision or a <c>restrict</c>-ed reference —
    /// and recovers whatever it names, or answers <see langword="null"/> when it is anything else.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Why this is on the dialect and not in a <c>catch</c>.</b> Constraint surfacing is the most
    /// engine-specific thing in the write path: PostgreSQL raises <c>PostgresException</c> with SQLSTATE
    /// <c>23505</c>/<c>23503</c> and a constraint name, SQLite raises <c>SqliteException</c> with extended
    /// result codes <c>2067</c>/<c>1555</c>/<c>787</c> and names the columns only in its message text, and
    /// T-SQL raises <c>SqlException</c> with error numbers <c>2627</c>/<c>2601</c>/<c>547</c>. §0 principle 3
    /// requires the behaviour above this seam to be identical on all three, which is only achievable if the
    /// part that differs sits <em>behind</em> a seam each driver owns. The alternative the shared path must
    /// never grow — a <c>catch</c> matching a substring of the exception's message — is wrong twice over: it
    /// is a shared-layer dependency on one provider's prose, and a caller who can influence a value that ends
    /// up quoted in that prose can steer the classification.
    /// </para>
    /// <para>
    /// <b>Abstract rather than a default interface member, unlike <see cref="RowWindowClause"/>.</b> That one
    /// has a default because the PostgreSQL/SQLite spelling really is right for most engines. Here there is no
    /// such default: <see langword="null"/> means "this is not a constraint violation", which is a
    /// <em>legitimate</em> answer for every other failure, so a driver that inherited it would silently answer
    /// <c>500 internal</c> for every duplicate — indistinguishable from correct behaviour on an engine that
    /// genuinely reported something else. A compile error is the only signal that reaches an author who has
    /// not read this page.
    /// </para>
    /// <para>
    /// <b>A dialect must not guess.</b> Answering a violation for an exception this engine did not raise, or
    /// inferring a kind from anything weaker than the engine's own code, turns an unrelated failure into a
    /// <c>409</c> telling the caller to change a value that was never the problem. When in doubt, answer
    /// <see langword="null"/> and let the failure propagate as the broken invariant it is —
    /// <c>MMLib.Alvo.Testing.Data.AlvoSqlDialectContractTests</c> asserts that on an exception no engine
    /// raised.
    /// </para>
    /// <para>
    /// <b>What it may return, and what it must not.</b> A kind, and the constraint's name and/or its columns
    /// as the engine reports them — see <see cref="SqlConstraintViolation"/>. Never a message, never a value,
    /// never a row count. Resolving a name or a column list into the entity's own <em>field</em> names is the
    /// shared data path's job, because only it holds the model; a driver that resolved them itself would be a
    /// second authority for what an entity's fields are.
    /// </para>
    /// </remarks>
    /// <param name="failure">
    /// The exception the write raised, already unwrapped from EF's <c>DbUpdateException</c> to the provider's
    /// own — so a dialect matches on its own exception type rather than on EF's wrapper.
    /// </param>
    /// <returns>
    /// What the engine reported, or <see langword="null"/> when <paramref name="failure"/> is not a
    /// constraint violation this engine raised.
    /// </returns>
    /// <exception cref="ArgumentNullException"><paramref name="failure"/> is <see langword="null"/>.</exception>
    SqlConstraintViolation? DecodeConstraintViolation(DbException failure);
}
