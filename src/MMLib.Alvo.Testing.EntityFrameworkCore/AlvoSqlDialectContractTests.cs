using MMLib.Alvo.Data.EntityFrameworkCore;
using MMLib.Alvo.Expressions;
using MMLib.Alvo.Schema;
using Shouldly;
using System.Text.RegularExpressions;
using Xunit;

namespace MMLib.Alvo.Testing.Data;

/// <summary>
/// Behavioural contract every storage driver's SQL seam must satisfy — the pair of
/// <see cref="IAlvoSqlDialect"/> (statement shape) and <see cref="IFieldSqlRenderer"/> (expression shape),
/// held together because no driver ships one without the other and the two have to agree about where a row
/// lock lives.
/// </summary>
/// <remarks>
/// <para>
/// This exists because the dialect's return grammar was prose-only. Every member documents precisely what it
/// may and may not return — no <c>FROM</c> keyword, no alias, no separator of its own, no terminator — and
/// each obligation was pinned only by the two in-repo drivers' own unit tests, per driver, by exact string. A
/// driver satisfying neither still compiles, and its failure mode is not a red test but a syntax error, or
/// worse a silently unlocked pre-image. §0 principle 1 asks for the contract before the implementation; the
/// shape follows this repo's own idiom (<see cref="Migrations.SchemaMigratorContractTests"/>,
/// <see cref="Migrations.DescriptorVersionStoreContractTests"/>,
/// <see cref="Migrations.RuntimeSchemaWriterContractTests"/>).
/// </para>
/// <para>
/// It is also the seam §2.1's *"the same adversarial suite passes over a physical and a virtual entity"*
/// criterion runs through: F7's dynamic driver is a dialect whose <see cref="IAlvoSqlDialect.RenderTable"/>
/// answers a JSON-projecting sub-select over one shared partitioned store instead of a table name. Everything
/// here is therefore written to hold for a table source that is a parenthesised query, not only for a quoted
/// name — which is why nothing asserts an exact string, and why nothing forbids an inner <c>AS</c> in a table
/// source (a projecting sub-select needs them) while a bare column reference still may not carry one.
/// </para>
/// <para>
/// <b>What this suite deliberately cannot prove.</b>
/// <see cref="IFieldSqlRenderer.RenderComparableOperands"/>' formal obligation is
/// <c>a &lt; b ⇒ repair(a) &lt; repair(b)</c> — a statement about an <em>engine's</em> ordering, so no
/// engine-free suite can decide it. What is generic, and asserted here, is the structural half that obligation
/// rests on: one repair, applied identically to both operands and to an ordering key. The engine half is proved
/// per driver against a real engine (SQLite's <c>REAL</c> cast for a <c>decimal</c> stored as <c>TEXT</c>, in
/// its decimal keyset-paging tests) and cross-engine by <see cref="AlvoDataOrderingTests"/>.
/// </para>
/// </remarks>
public abstract class AlvoSqlDialectContractTests
{
    /// <summary>Creates the dialect under test.</summary>
    protected abstract IAlvoSqlDialect CreateDialect();

    /// <summary>
    /// Creates the field renderer this driver ships <em>alongside</em> that dialect. A driver's two halves are
    /// asserted together because the comparison repair and the statement it lands in are one decision: pairing
    /// them here is what stops a suite from proving one driver's dialect against another driver's renderer.
    /// </summary>
    protected abstract IFieldSqlRenderer CreateFieldRenderer();

    /// <summary>Every CEL type a comparison can be evaluated at — the renderer is asked for all of them.</summary>
    public static TheoryData<CelValueType> CelValueTypes() => [.. Enum.GetValues<CelValueType>()];

    /// <summary>
    /// Names chosen to break a dialect that concatenates its own delimiters instead of escaping: each carries
    /// the closing delimiter of one of the three engines Alvo names, plus a comment opener and a terminator.
    /// </summary>
    private static readonly string[] _hostileNames =
    [
        "plate",
        "a\"; DROP TABLE vehicle; --",
        "a]; DROP TABLE vehicle; --",
        "a'; DROP TABLE vehicle; --",
        "a\"\"b",
        "a\"b",
    ];

    private const string Column = "\"price\"";

    private const string Marker = "@alvo_f0";

    /// <summary>
    /// The <c>FROM</c> clause's body and nothing else, for a plain read and for either locking pre-image. The
    /// composer interpolates the result verbatim and adds no separator, so a leading space, a trailing space, a
    /// <c>FROM</c> keyword or a terminator each produce a broken statement rather than a wrong answer — in the
    /// one statement a <c>WITH CHECK</c> verdict is based on.
    /// </summary>
    [Theory]
    [InlineData(null)]
    [InlineData(PreImageMutation.Update)]
    [InlineData(PreImageMutation.Delete)]
    public void A_table_source_is_a_from_clause_body_and_nothing_else(PreImageMutation? lockedPreImageFor)
    {
        var table = CreateDialect().RenderTable(Entity("vehicle"), lockedPreImageFor);

        table.ShouldNotBeNullOrWhiteSpace();
        table.ShouldBe(table.Trim(), "A table source carries no separator of its own; the composer adds the space.");
        table.StartsWith("FROM", StringComparison.OrdinalIgnoreCase)
            .ShouldBeFalse("A table source is the FROM clause's body, not the clause.");
        table.ShouldNotContain(";");
    }

    /// <summary>
    /// A dialect must not invent an alias, a generated name or anything else that changes between two renders of
    /// one entity: the same read is composed more than once (a pre-image, then the write over it), and a
    /// statement whose text is not a function of its inputs cannot be snapshotted or reasoned about.
    /// </summary>
    [Fact]
    public void A_table_source_is_a_function_of_its_inputs_alone()
    {
        var dialect = CreateDialect();

        dialect.RenderTable(Entity("vehicle"), null).ShouldBe(dialect.RenderTable(Entity("vehicle"), null));
    }

    /// <summary>
    /// <b>The pairing rule, and the reason <see cref="IAlvoSqlDialect.RenderTable"/> is told about the lock at
    /// all.</b> Row locking has two grammars — PostgreSQL's trailing clause and T-SQL's table hint — so a
    /// dialect may answer in either position, and must answer in exactly one. Locking twice is not twice as
    /// safe: it is an engine-dependent error, and on T-SQL a trailing <c>FOR UPDATE</c> does not parse at all.
    /// </summary>
    /// <remarks>
    /// Vacuous for a dialect that hints nothing, which both shipped drivers are; <c>TSqlSqlDialect</c>'s own leg
    /// of this suite is what exercises the other arm, and it is shipped here rather than declared per test
    /// project for exactly that reason.
    /// </remarks>
    [Theory]
    [InlineData(PreImageMutation.Update)]
    [InlineData(PreImageMutation.Delete)]
    public void A_lock_is_expressed_in_the_table_source_or_in_the_trailing_clause_but_never_both(
        PreImageMutation mutation)
    {
        var dialect = CreateDialect();

        if (dialect.RenderTable(Entity("vehicle"), mutation) == dialect.RenderTable(Entity("vehicle"), null))
        {
            return;
        }

        dialect.RowLockClause(mutation).ShouldBeEmpty(
            "This dialect takes the lock in the FROM, so its trailing clause must be empty.");
    }

    /// <summary>
    /// A missing entity is a caller bug, and the alternative to refusing it is a table source reading
    /// <c>FROM ""</c> — a statement whose failure names neither the entity nor the dialect.
    /// </summary>
    [Fact]
    public void A_missing_entity_is_refused_rather_than_rendering_an_empty_table_source()
        => Should.Throw<ArgumentNullException>(() => CreateDialect().RenderTable(null!, null));

    /// <summary>A bare column reference: no comma, no <c>AS</c> alias, nothing to trim.</summary>
    /// <remarks>
    /// The composer joins the <c>SELECT</c> list itself and appends the alias a masked field needs, so a dialect
    /// shipping either would double it.
    /// </remarks>
    [Fact]
    public void A_column_reference_is_bare()
    {
        var column = CreateDialect().RenderColumn("secret_note");

        column.ShouldNotBeNullOrWhiteSpace();
        column.ShouldBe(column.Trim());
        column.ShouldNotContain(",");
        column.ShouldNotContain(" AS ", Case.Insensitive);
        column.ShouldNotContain(";");
    }

    /// <summary>
    /// An identifier is delimited <b>unconditionally</b>, even where the engine would accept it bare. Spike
    /// <c>Q8</c>: Npgsql's own <c>DelimitIdentifier</c> returns <c>plate</c> undelimited, PostgreSQL then
    /// case-folds it, and the same field renders differently per driver — so a rule and a caller filter over one
    /// column can disagree about which column that is.
    /// </summary>
    [Fact]
    public void An_identifier_that_would_not_strictly_need_delimiting_is_delimited_anyway()
    {
        var dialect = CreateDialect();

        dialect.RenderColumn("plate").ShouldNotBe("plate");
        dialect.RenderTable(Entity("plate"), null).ShouldNotBe("plate");
    }

    /// <summary>
    /// Escaping, asserted without naming a delimiter: two different names must never render to the same text. A
    /// dialect that concatenates its own quotes instead of escaping collapses <c>a"b</c> and <c>a""b</c> onto one
    /// string — and a collapsing rendering is exactly one through which a name escapes its own identifier and
    /// reaches the statement as SQL.
    /// </summary>
    [Fact]
    public void Two_different_names_never_render_to_the_same_identifier()
    {
        var dialect = CreateDialect();

        _hostileNames.Select(dialect.RenderColumn).ShouldBeUnique();
        _hostileNames.Select(name => dialect.RenderTable(Entity(name), null)).ShouldBeUnique();
    }

    /// <summary>
    /// A bare expression, interpolated into a <c>SELECT</c> list: nothing to trim, no separating comma, and the
    /// store type EF resolved reaching the cast unrewritten. A dialect that rewrote it would be a second
    /// authority for "what store type does this column have", which is the mistake this member's first revision
    /// made — it answered <c>numeric(18,2)</c> for every decimal regardless of declared precision.
    /// </summary>
    /// <remarks>
    /// The "no <c>AS &lt;column&gt;</c> alias" half of the documented grammar needs no assertion, and asserting
    /// it is in fact impossible: a cast is spelled <c>CAST(NULL AS text)</c>, so the keyword is present in every
    /// legal answer. The signature is what forbids the alias — this member is handed no column name at all, so
    /// there is nothing for a dialect to alias to.
    /// </remarks>
    /// <param name="storeType">A store type the provider's own type mapping could have resolved.</param>
    [Theory]
    [InlineData("TEXT")]
    [InlineData("numeric(10,4)")]
    [InlineData("character varying(32)")]
    public void A_null_projection_is_a_bare_expression_naming_the_store_type_it_was_given(string storeType)
    {
        var projection = CreateDialect().RenderNullProjection(storeType);

        projection.ShouldContain(storeType);
        projection.ShouldBe(projection.Trim());
        projection.ShouldNotEndWith(",");
        projection.ShouldNotStartWith(",");
        projection.ShouldNotContain(";");
    }

    /// <summary>
    /// A masked field's whole guarantee is that its data stays in the table, and a projection cast to nothing is
    /// either a syntax error or an untyped <c>NULL</c> the read model cannot materialise. Refusing beats both.
    /// </summary>
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void A_null_projection_refuses_a_missing_store_type(string? storeType)
        => Should.Throw<ArgumentException>(() => CreateDialect().RenderNullProjection(storeType!));

    /// <summary>
    /// The locking clause carries no separator of its own and is never <see langword="null"/>. The composer
    /// inserts the space and only when the clause is non-empty, so a value shipping its own would double it, and
    /// one under the opposite convention would produce <c>… WHERE &lt;predicate&gt;FOR UPDATE</c>.
    /// </summary>
    [Theory]
    [InlineData(PreImageMutation.Update)]
    [InlineData(PreImageMutation.Delete)]
    public void A_row_lock_clause_carries_no_separator_of_its_own(PreImageMutation mutation)
    {
        var clause = CreateDialect().RowLockClause(mutation);

        clause.ShouldNotBeNull();
        clause.ShouldBe(clause.Trim(), "Return string.Empty rather than whitespace where there is no clause.");
        clause.ShouldNotContain(";");
    }

    /// <summary>
    /// The limit is bound, never formatted, so the clause has to name the marker it was handed — a dialect that
    /// dropped it would truncate to nothing or not at all, on a row count the caller supplied. Called with no
    /// offset, the shape every read without one uses.
    /// </summary>
    [Fact]
    public void A_row_window_clause_names_the_row_count_marker_and_carries_no_separator_of_its_own()
    {
        var clause = CreateDialect().RowWindowClause("@alvo_limit");

        clause.ShouldContain("@alvo_limit");
        clause.ShouldBe(clause.Trim());
        clause.ShouldNotContain(";");
    }

    /// <summary>
    /// The offset is bound, never formatted, so the clause has to name the marker it was handed too — the
    /// same reasoning as <see cref="A_row_window_clause_names_the_row_count_marker_and_carries_no_separator_of_its_own"/>,
    /// for the second argument. Answered generically rather than only over the two shipped dialects' shared
    /// default, so <c>TSqlSqlDialect</c>'s own override — which spells the marker differently — is held to the
    /// same obligation.
    /// </summary>
    [Fact]
    public void A_row_window_clause_with_an_offset_names_both_markers_and_carries_no_separator_of_its_own()
    {
        var clause = CreateDialect().RowWindowClause("@alvo_limit", "@alvo_offset");

        clause.ShouldContain("@alvo_limit");
        clause.ShouldContain("@alvo_offset");
        clause.ShouldBe(clause.Trim());
        clause.ShouldNotContain(";");
    }

    /// <summary>
    /// The defect this member exists to make unrepresentable: a dialect that renders the row count and the
    /// offset as two independently-correct clauses can still get the *pair* wrong, the way an earlier
    /// revision of <c>TSqlSqlDialect</c> did — its old <c>RowLimitClause</c> hard-coded <c>OFFSET 0 ROWS</c>,
    /// so a driver that also answered a separate <c>RowOffsetClause</c> would have emitted two conflicting
    /// <c>OFFSET</c> clauses in one statement. One call receiving both markers together closes that gap; this
    /// asserts the closed shape generically rather than trusting one driver's docstring.
    /// </summary>
    [Fact]
    public void A_row_window_clause_with_an_offset_renders_exactly_one_offset_keyword()
    {
        var clause = CreateDialect().RowWindowClause("@alvo_limit", "@alvo_offset");

        CountOffsetKeywords(clause).ShouldBe(
            1, "two OFFSET clauses in one statement is a silently wrong page, not merely untidy SQL.");
    }

    /// <summary>
    /// Counts the <c>OFFSET</c> <b>keyword</b>, not the substring — <c>@alvo_offset</c>, the marker every
    /// shipped dialect actually binds, itself contains the letters <c>offset</c>, so a plain substring count
    /// would answer 2 for a dialect that is already correct. The word-boundary regex is what tells the
    /// keyword from the parameter name it is followed by.
    /// </summary>
    private static int CountOffsetKeywords(string text) =>
        Regex.Matches(text, @"\bOFFSET\b", RegexOptions.IgnoreCase).Count;

    /// <summary>
    /// Every <see cref="CelValueType"/> is answered rather than refused. The renderer is asked for
    /// <em>every</em> comparison, so a type it has no repair for is returned unchanged — a dialect that threw
    /// would turn a legal rule into a runtime failure on whichever engine happens to be underneath.
    /// </summary>
    /// <param name="type">The type the comparison is evaluated at.</param>
    [Theory]
    [MemberData(nameof(CelValueTypes))]
    public void Every_cel_value_type_is_answered_rather_than_refused(CelValueType type)
    {
        var (left, right) = CreateFieldRenderer().RenderComparableOperands(Column, Marker, type);

        left.ShouldNotBeNullOrWhiteSpace();
        right.ShouldNotBeNullOrWhiteSpace();
    }

    /// <summary>
    /// <b>One repair, applied to both operands.</b> Repairing one side only does not leave the comparison
    /// approximate — it produces a new wrong answer, because SQLite orders every <c>TEXT</c> value above every
    /// <c>REAL</c> one, so a cast column against an uncast parameter <em>inverts</em>. Asserted by mirroring the
    /// call: whatever an operand becomes in the left position it must also become in the right one.
    /// </summary>
    /// <param name="type">The type the comparison is evaluated at.</param>
    [Theory]
    [MemberData(nameof(CelValueTypes))]
    public void Both_operands_of_a_comparison_get_the_same_repair(CelValueType type)
    {
        var fields = CreateFieldRenderer();
        var forward = fields.RenderComparableOperands(Column, Marker, type);
        var mirrored = fields.RenderComparableOperands(Marker, Column, type);

        forward.Left.ShouldBe(mirrored.Right, "A left operand's repair differs from the same operand's on the right.");
        forward.Right.ShouldBe(mirrored.Left, "A right operand's repair differs from the same operand's on the left.");
    }

    /// <summary>
    /// The <c>ORDER BY</c> case, named separately because it breaks paging rather than filtering: a storage
    /// driver asks with the <b>same</b> operand on both sides and uses one result as the ordering key, so a
    /// repair answering asymmetrically would order a page by one expression and bound it by another — a page that
    /// silently skips or repeats rows.
    /// </summary>
    /// <param name="type">The type the comparison is evaluated at.</param>
    [Theory]
    [MemberData(nameof(CelValueTypes))]
    public void An_ordering_key_is_the_repair_both_sides_of_the_boundary_see(CelValueType type)
    {
        var (left, right) = CreateFieldRenderer().RenderComparableOperands(Column, Column, type);

        left.ShouldBe(right, "The same operand was repaired two ways.");
    }

    /// <summary>
    /// A repair <em>wraps</em> its operand; it never substitutes one. Either operand may be a bind-parameter
    /// marker rather than a column, so a dialect cannot introspect what it was handed — and a repair that dropped
    /// it would compare a constant, which is a predicate answering the same for every row.
    /// </summary>
    /// <param name="type">The type the comparison is evaluated at.</param>
    [Theory]
    [MemberData(nameof(CelValueTypes))]
    public void A_repair_wraps_its_operand_rather_than_replacing_it(CelValueType type)
    {
        var (left, right) = CreateFieldRenderer().RenderComparableOperands(Column, Marker, type);

        left.ShouldContain(Column);
        right.ShouldContain(Marker);
    }

    /// <summary>
    /// A rendered fragment must be a function of its inputs alone. The keyset boundary and the <c>ORDER BY</c>
    /// are rendered by two separate calls through this member, and a renderer whose answer varied between them
    /// would describe two different total orders — the exact defect the paired signature exists to prevent.
    /// </summary>
    /// <param name="type">The type the comparison is evaluated at.</param>
    [Theory]
    [MemberData(nameof(CelValueTypes))]
    public void A_repair_is_a_function_of_its_inputs_alone(CelValueType type)
    {
        var fields = CreateFieldRenderer();

        fields.RenderComparableOperands(Column, Marker, type)
            .ShouldBe(fields.RenderComparableOperands(Column, Marker, type));
    }

    private static EntitySchema Entity(string name) => new()
    {
        Name = name,
        Fields = [new FieldSchema { Name = "id", Type = FieldType.Uuid, Required = true }],
    };
}
