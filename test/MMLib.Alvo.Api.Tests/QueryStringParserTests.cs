using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.WebUtilities;
using MMLib.Alvo.Api.Internal;
using MMLib.Alvo.Data;
using MMLib.Alvo.Schema;
using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace MMLib.Alvo.Api.Tests;

/// <summary>
/// The PostgREST query surface, parsed. Every fact here runs against the parser directly rather than over
/// HTTP, because what is under test is a <em>grammar</em> and a set of refusals — and a fact that had to
/// stand a request up could not assert on the tree that was built, only on the rows that came back.
/// </summary>
/// <remarks>
/// The end-to-end half — that the parse is actually wired into the list endpoint, and that a refusal is a
/// 422 — is <c>DataApiQueryTests</c>; the adversarial half is <c>QueryStringInjectionTests</c>.
/// </remarks>
public sealed class QueryStringParserTests
{
    /// <summary>
    /// One entity carrying every field type the value reader distinguishes, so a type rule can be asserted
    /// without a second fixture. <c>secret</c> exists to be masked and is otherwise ordinary — a hidden field
    /// that differed in any other way would let the confidentiality facts pass for the wrong reason.
    /// </summary>
    private static readonly EntitySchema _vehicles = new()
    {
        Name = "vehicles",
        Fields =
        [
            new FieldSchema { Name = "id", Type = FieldType.Uuid },
            new FieldSchema { Name = "make", Type = FieldType.String },
            new FieldSchema { Name = "year", Type = FieldType.Integer },
            new FieldSchema { Name = "color", Type = FieldType.String, Nullable = true },
            new FieldSchema { Name = "notes", Type = FieldType.Text, Nullable = true },
            new FieldSchema { Name = "price", Type = FieldType.Decimal },
            new FieldSchema { Name = "passed", Type = FieldType.Boolean },
            new FieldSchema { Name = "inspected_on", Type = FieldType.Date },
            new FieldSchema { Name = "serviced_at", Type = FieldType.DateTime },
            new FieldSchema { Name = "owner_id", Type = FieldType.Ref },
            new FieldSchema { Name = "secret", Type = FieldType.String },
        ],
    };

    private static readonly IReadOnlySet<string> _masked = new HashSet<string>(StringComparer.Ordinal) { "secret" };

    private static readonly AlvoApiOptions _options = new();

    /// <summary>Every pointer a query-string refusal may carry — the parameter <em>roles</em>, never a field name.</summary>
    private static readonly string[] _parameterRoles = ["filter", "order", "limit", "offset", "after", "select"];

    [Theory]
    [InlineData("year=gte.2020", "year >= 2020")]
    [InlineData("color=eq.red", "color == red")]
    [InlineData("notes=is.null", "notes IS null")]
    [InlineData("make=in.(skoda,vw)", "make IN [skoda, vw]")]
    [InlineData("or=(color.eq.red,color.eq.blue)", "(color == red OR color == blue)")]
    [InlineData("and=(year.gte.2020,year.lte.2024)", "(year >= 2020 AND year <= 2024)")]
    [InlineData("or=(year.eq.2020,and=(make.eq.vw,year.gte.2015))",
        "(year == 2020 OR (make == vw AND year >= 2015))")]
    [InlineData("not.color=eq.red", "NOT color == red")]
    [InlineData("or=(not.color.eq.red,year.eq.2020)", "(NOT color == red OR year == 2020)")]
    [InlineData("or=(make.in.(skoda,vw),year.eq.2020)", "(make IN [skoda, vw] OR year == 2020)")]
    [InlineData("year=gte.2020&year=lte.2024", "(year >= 2020 AND year <= 2024)")]
    [InlineData("make=eq.vw&year=eq.2020", "(make == vw AND year == 2020)")]
    [InlineData("make=like.sko%", "make LIKE sko%")]
    [InlineData("make=ilike.SKO%", "make ILIKE SKO%")]
    [InlineData("passed=is.true", "passed IS True")]
    [InlineData("price=lt.1500.50", "price < 1500.50")]
    [InlineData("inspected_on=gte.2024-01-31", "inspected_on >= 2024-01-31")]
    public void A_query_string_parses_to_the_expected_filter_tree(string queryString, string expectedTree)
    {
        TryParse(queryString, out var parsed, out var violations).ShouldBeTrue(Because(violations));

        Render(parsed!.Query.Filter).ShouldBe(expectedTree);
    }

    /// <summary>
    /// An accepted operand reaches the port in the CLR type the field is <b>carried</b> as, not as the text it
    /// arrived in.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The tree-shape facts above cannot see this: their formatter stringifies, so a parser that handed the raw
    /// text on after a successful <c>long.TryParse</c> renders identically and passes every one of them. So does
    /// the end-to-end suite, which filtered only a string field.
    /// </para>
    /// <para>
    /// It is the bug class PR2 spent a whole fix wave on. SQLite compares <c>TEXT</c> lexically, so a
    /// <c>decimal</c> column filtered with a string operand answered <c>price &gt; 100</c> with a row priced
    /// 12.34 — the same fail-open, in the channel a caller controls per request.
    /// </para>
    /// <para>
    /// <see cref="FieldClrType"/> is the authority for what the type must be; writing the expected types out
    /// here would be the third copy of a mapping that exists precisely so there is one.
    /// </para>
    /// </remarks>
    [Theory]
    [InlineData("year=eq.2020", "year")]
    [InlineData("year=gte.-7", "year")]
    [InlineData("price=eq.1500.50", "price")]
    [InlineData("price=lt.9", "price")]
    [InlineData("passed=eq.true", "passed")]
    [InlineData("id=eq.8bf3c0de-0000-4000-8000-000000000001", "id")]
    [InlineData("owner_id=eq.8bf3c0de-0000-4000-8000-000000000001", "owner_id")]
    [InlineData("serviced_at=gte.2024-01-31T09:30:00Z", "serviced_at")]
    [InlineData("inspected_on=gte.2024-01-31", "inspected_on")]
    [InlineData("make=eq.vw", "make")]
    [InlineData("make=like.v%", "make")]
    [InlineData("notes=eq.anything", "notes")]
    public void An_accepted_operand_reaches_the_port_as_the_type_the_field_is_carried_as(
        string queryString, string field)
    {
        TryParse(queryString, out var parsed, out var violations).ShouldBeTrue(Because(violations));

        var comparison = parsed!.Query.Filter.ShouldBeOfType<AlvoComparison>();
        comparison.Value.ShouldNotBeNull();
        comparison.Value!.GetType().ShouldBe(FieldClrType.Of(Declared(field)));
    }

    /// <summary>
    /// Every candidate of an <c>in</c> list is typed too — the position a per-value conversion is easiest to
    /// forget, since only the first one is ever eyeballed.
    /// </summary>
    [Fact]
    public void Every_in_candidate_reaches_the_port_as_the_type_the_field_is_carried_as()
    {
        TryParse("year=in.(1999,2020,-7)", out var parsed, out var violations).ShouldBeTrue(Because(violations));

        var candidates = Candidates(parsed!.Query.Filter);
        candidates.Count.ShouldBe(3);
        candidates.ShouldAllBe(candidate => candidate!.GetType() == FieldClrType.Of(Declared("year")));
    }

    /// <summary>
    /// <c>is</c> is the one operator whose operand is not a value of the field's type: it carries SQL's own three
    /// identity operands, and <c>null</c> must arrive as a real <see langword="null"/> rather than the text.
    /// </summary>
    [Theory]
    [InlineData("notes=is.null", null)]
    [InlineData("passed=is.true", true)]
    [InlineData("passed=is.false", false)]
    public void An_is_operand_reaches_the_port_as_null_or_a_boolean(string queryString, bool? expected)
    {
        TryParse(queryString, out var parsed, out var violations).ShouldBeTrue(Because(violations));

        parsed!.Query.Filter.ShouldBeOfType<AlvoComparison>().Value.ShouldBe(expected);
    }

    private static FieldSchema Declared(string field) =>
        _vehicles.Fields.Single(candidate => string.Equals(candidate.Name, field, StringComparison.Ordinal));

    [Theory]
    [InlineData("nosuchfield=eq.1")]          // unknown field
    [InlineData("year=nosuchop.1")]           // operator off the allow-list
    [InlineData("year=gte.notanumber")]       // value the field's type cannot hold
    [InlineData("year=gte.2020.5")]           // fractional bound on an integral field
    [InlineData("notes=is.hello")]            // is-operand that is not null/true/false
    [InlineData("make=in.skoda")]             // in without a list
    [InlineData("or=(")]                      // unbalanced group
    [InlineData("or=()")]                     // empty group
    [InlineData("limit=0")]
    [InlineData("limit=-1")]
    [InlineData("limit=100000")]              // past MaxPageSize
    [InlineData("offset=-1")]
    [InlineData("after=abc&offset=1")]
    [InlineData("order=year.sideways")]
    [InlineData("select=nosuchfield")]
    public void A_malformed_query_string_is_refused_with_a_violation_naming_the_parameter(string queryString)
    {
        TryParse(queryString, out var parsed, out var violations).ShouldBeFalse();

        parsed.ShouldBeNull("a refused query must yield nothing a port could be handed");
        violations.ShouldNotBeEmpty();
        foreach (var violation in violations)
        {
            _parameterRoles.ShouldContain(
                violation.Pointer,
                "a violation must name the parameter's role — and only its role, never the caller's own field name");
            violation.Code.ShouldNotBeNullOrWhiteSpace();
            violation.Message.ShouldNotBeNullOrWhiteSpace();
            violation.FixSuggestion.ShouldNotBeNullOrWhiteSpace("§0 principle 4 makes the fix part of the contract");
        }
    }

    /// <summary>
    /// The same corpus, asserted on the <em>diagnosis</em> rather than only on "something was wrong". Without
    /// this, every row above is satisfied by a parser that refuses everything with one generic code, and the
    /// agent-facing half of the refusal — which mistake was made — is untested.
    /// </summary>
    [Theory]
    [InlineData("nosuchfield=eq.1", "unavailable-field")]
    [InlineData("year=nosuchop.1", "unknown-operator")]
    [InlineData("year=gte.notanumber", "invalid-filter-value")]
    [InlineData("year=gte.2020.5", "invalid-filter-value")]
    [InlineData("notes=is.hello", "malformed-is-operand")]
    [InlineData("make=in.skoda", "malformed-in-list")]
    [InlineData("year=eq", "malformed-filter")]
    [InlineData("or=(", "malformed-filter-group")]
    [InlineData("or=()", "malformed-filter-group")]
    [InlineData("limit=0", "invalid-page-size")]
    [InlineData("limit=100000", "invalid-page-size")]
    [InlineData("offset=-1", "invalid-offset")]
    [InlineData("after=&offset=1", "invalid-cursor")]
    [InlineData("after=abc&offset=1", "conflicting-paging")]
    [InlineData("order=year.sideways", "malformed-order")]
    [InlineData("order=year,year", "repeated-sort-key")]
    [InlineData("order=color", "unpageable-sort-key")]
    [InlineData("select=", "malformed-select")]
    [InlineData("select=nosuchfield", "unavailable-field")]
    [InlineData("limit=1&limit=2", "repeated-parameter")]
    [InlineData("year=like.2", "unsupported-operator-for-field")]
    [InlineData("owner_id=gt.00000000-0000-0000-0000-000000000001", "unsupported-operator-for-field")]
    public void A_refused_query_string_carries_the_code_that_names_the_mistake(string queryString, string code)
    {
        TryParse(queryString, out _, out var violations).ShouldBeFalse();

        Codes(violations).ShouldBe([code]);
    }

    /// <summary>
    /// A masked field and a field that does not exist earn the <b>same</b> refusal, byte for byte. Asserted as
    /// record equality rather than "both messages are non-empty": the whole property is that a caller cannot
    /// tell the two apart, and any difference — a code, a pointer, a fix suggestion — answers "does this entity
    /// have a field called X" one request at a time.
    /// </summary>
    [Fact]
    public void A_filter_over_a_hidden_field_is_refused_exactly_like_an_unknown_one() =>
        OnlyViolation("secret=eq.x").ShouldBe(OnlyViolation("nosuchfield=eq.x"));

    /// <summary>
    /// Sorting by a masked field discloses that field's ordering across the whole page, which is why the mask
    /// is consulted for <c>order</c> too — and why the refusal is the same one an unknown field earns.
    /// </summary>
    [Fact]
    public void A_sort_over_a_hidden_field_is_refused_exactly_like_an_unknown_one() =>
        OnlyViolation("order=secret").ShouldBe(OnlyViolation("order=nosuchfield"));

    [Fact]
    public void A_select_naming_a_hidden_field_is_refused_exactly_like_an_unknown_one() =>
        OnlyViolation("select=secret").ShouldBe(OnlyViolation("select=nosuchfield"));

    /// <summary>
    /// The parser's refusal is <b>byte-equal</b> to the port's, because both read one constant on the port.
    /// </summary>
    /// <remarks>
    /// It was three hand-synced literals — this parser, the EF driver's <c>QueryFieldGuard</c> and the in-memory
    /// reference — pinned by nothing. That is the one message where drift is not a cosmetic inconsistency: the
    /// parser refuses before the port is ever reached, so the two wordings are never observed side by side, and a
    /// caller who could tell "refused by the parser" from "refused by the port" would have the field-existence
    /// oracle the <c>hiddenFields</c> parameter exists to close.
    /// </remarks>
    [Fact]
    public void The_unavailable_field_refusal_is_the_ports_own_wording()
    {
        QueryViolations.UnavailableFieldMessage.ShouldBe(AlvoAuthorizationException.QueryFieldUnavailable);

        OnlyViolation("secret=eq.x").Message.ShouldBe(AlvoAuthorizationException.QueryFieldUnavailable);
        OnlyViolation("order=nosuchfield").Message.ShouldBe(AlvoAuthorizationException.QueryFieldUnavailable);
        OnlyViolation("select=secret").Message.ShouldBe(AlvoAuthorizationException.QueryFieldUnavailable);
    }

    /// <summary>
    /// A mistyped reserved keyword is refused as an unavailable field, and refused <em>identically</em>: in this
    /// grammar every non-reserved key is a filter on a field, so <c>oder=name</c> genuinely is a filter on a
    /// field called <c>oder</c>. A distinct "unknown parameter" diagnosis would be a third refusal a caller
    /// could compare the other two against.
    /// </summary>
    [Fact]
    public void A_mistyped_reserved_parameter_is_refused_exactly_like_an_unknown_field() =>
        OnlyViolation("oder=name").ShouldBe(OnlyViolation("nosuchfield=name"));

    /// <summary>
    /// No refusal reflects the caller's own bytes. A marker is planted in every position a query string has —
    /// key, operator, value, sort key, projection — and must appear in no pointer, message or fix suggestion:
    /// a refusal that echoed it would be both a field-existence oracle and a log-injection surface.
    /// </summary>
    [Theory]
    [InlineData("zqmarkerqz=eq.1")]
    [InlineData("year=zqmarkerqz.1")]
    [InlineData("year=gte.zqmarkerqz")]
    [InlineData("notes=is.zqmarkerqz")]
    [InlineData("make=in.zqmarkerqz")]
    [InlineData("order=zqmarkerqz")]
    [InlineData("order=year.zqmarkerqz")]
    [InlineData("select=zqmarkerqz")]
    [InlineData("limit=zqmarkerqz")]
    [InlineData("offset=zqmarkerqz")]
    [InlineData("or=(zqmarkerqz")]
    public void A_refusal_never_echoes_the_callers_own_text(string queryString)
    {
        const string marker = "zqmarkerqz";

        TryParse(queryString, out _, out var violations).ShouldBeFalse();

        violations.ShouldNotBeEmpty();
        foreach (var violation in violations)
        {
            $"{violation.Pointer} {violation.Code} {violation.Message} {violation.FixSuggestion}"
                .ShouldNotContain(marker, Case.Insensitive);
        }
    }

    /// <summary>
    /// Every refusal this parser raises carries a fix suggestion. <see cref="AlvoViolation.FixSuggestion"/> is
    /// nullable for a violation forwarded from a source that has none; nothing here is such a source, and §0
    /// principle 4 makes the suggestion part of the contract rather than a nicety.
    /// </summary>
    [Fact]
    public void Every_refusal_carries_a_fix_suggestion()
    {
        var refusals = _everyRefusal.SelectMany(queryString =>
        {
            TryParse(queryString, out _, out var violations).ShouldBeFalse(queryString);
            return violations;
        }).ToList();

        refusals.Count.ShouldBeGreaterThanOrEqualTo(_everyRefusal.Length);
        refusals.ShouldAllBe(violation => !string.IsNullOrWhiteSpace(violation.FixSuggestion));
    }

    /// <summary>One query string per refusal this parser can raise, so the fix-suggestion fact covers all of them.</summary>
    private static readonly string[] _everyRefusal =
    [
        "nosuchfield=eq.1", "secret=eq.1", "year=nosuchop.1", "year=eq", "year=gte.notanumber",
        "make=eq.a%00b", "notes=is.hello", "make=in.skoda", "or=(", "or=()", "year=like.2",
        "limit=0", "offset=-1", "after=", "after=abc&offset=1", "order=", "order=year.sideways",
        "order=year,year", "order=color", "select=", "select=nosuchfield", "limit=1&limit=2",
    ];

    /// <summary>
    /// The allow-list is derived from <see cref="AlvoFilterOperator"/>, so this fact is what stops that
    /// derivation from silently inventing a dialect: an operator added to the port whose lower-cased member
    /// name is not its PostgREST spelling fails here rather than shipping under a name nobody chose.
    /// </summary>
    [Fact]
    public void The_operator_allow_list_is_exactly_postgrests_spellings() =>
        FilterOperators.WireNames.ShouldBe(
            ["eq", "neq", "gt", "gte", "lt", "lte", "like", "ilike", "in", "is"], ignoreOrder: true);

    /// <summary>
    /// An unknown operator must be a refusal, never a fallback. The control is the point: without the second
    /// assertion, a parser that quietly read every unknown operator as <c>eq</c> would still fail nothing here.
    /// </summary>
    [Fact]
    public void An_unknown_operator_is_refused_rather_than_read_as_equality()
    {
        TryParse("year=equals.2020", out _, out var refused).ShouldBeFalse();
        TryParse("year=eq.2020", out var accepted, out _).ShouldBeTrue();

        refused.Single().Code.ShouldBe("unknown-operator");
        Render(accepted!.Query.Filter).ShouldBe("year == 2020");
    }

    /// <summary>
    /// An operator's spelling is ordinal, like every other name in the framework — one wire form per operator.
    /// </summary>
    [Fact]
    public void An_operator_spelled_in_a_different_case_is_not_an_operator() =>
        OnlyViolation("year=GTE.2020").Code.ShouldBe("unknown-operator");

    [Fact]
    public void A_query_string_with_no_parameters_carries_no_filter_and_the_default_page_size()
    {
        TryParse(string.Empty, out var parsed, out var violations).ShouldBeTrue(Because(violations));

        parsed!.Query.Filter.ShouldBeNull();
        parsed.Query.Sort.ShouldBeEmpty();
        parsed.Query.Limit.ShouldBe(_options.DefaultPageSize);
        parsed.Query.Offset.ShouldBeNull();
        parsed.Query.After.ShouldBeNull();
        parsed.Select.ShouldBeNull("no projection means every field the port returns");
    }

    /// <summary>
    /// The maximum is enforced, and the boundary is where it is asserted: a fact at 100 000 would also pass
    /// against a parser whose ceiling was 1 000.
    /// </summary>
    [Fact]
    public void The_largest_page_a_request_may_ask_for_is_max_page_size()
    {
        TryParse($"limit={_options.MaxPageSize}", out var atMaximum, out var violations).ShouldBeTrue(Because(violations));
        TryParse($"limit={_options.MaxPageSize + 1}", out _, out var refused).ShouldBeFalse();

        atMaximum!.Query.Limit.ShouldBe(_options.MaxPageSize);
        refused.Single().Code.ShouldBe("invalid-page-size");
    }

    /// <summary>
    /// A page size past the maximum is <b>refused, not clamped</b>. The clamping design is the one this must
    /// exclude, and only a fact reading the refusal can: a clamped parse would return <c>true</c> with
    /// <c>Limit == MaxPageSize</c>, which the boundary fact above cannot tell from a legitimate request.
    /// </summary>
    [Fact]
    public void A_page_size_past_the_maximum_is_refused_rather_than_quietly_reduced() =>
        TryParse($"limit={_options.MaxPageSize + 1}", out var parsed, out _).ShouldBeFalse(
            $"parsed.Limit would otherwise be {parsed?.Query.Limit}, a number no response ever told the caller");

    [Fact]
    public void The_smallest_page_a_request_may_ask_for_is_one_row()
    {
        TryParse("limit=1", out var one, out var violations).ShouldBeTrue(Because(violations));
        TryParse("limit=0", out _, out var refused).ShouldBeFalse();

        one!.Query.Limit.ShouldBe(1);
        refused.Single().Code.ShouldBe("invalid-page-size");
    }

    [Fact]
    public void An_offset_of_zero_is_a_window_and_a_negative_one_is_not()
    {
        TryParse("offset=0", out var zero, out var violations).ShouldBeTrue(Because(violations));
        TryParse("offset=-1", out _, out var refused).ShouldBeFalse();

        zero!.Query.Offset.ShouldBe(0);
        refused.Single().Code.ShouldBe("invalid-offset");
    }

    /// <summary>
    /// A cursor is opaque and provider-owned, so the parser passes it through <b>verbatim</b> — it never
    /// decodes, validates or re-encodes one. A forged cursor is the provider's problem and already yields an
    /// empty page rather than a leak.
    /// </summary>
    [Fact]
    public void A_cursor_reaches_the_port_verbatim()
    {
        const string cursor = "eyJ5ZWFyIjoyMDIwfQ==";

        TryParse($"after={Uri.EscapeDataString(cursor)}", out var parsed, out var violations)
            .ShouldBeTrue(Because(violations));

        parsed!.Query.After.ShouldBe(cursor);
    }

    /// <summary>
    /// A cursor longer than any page could have minted is refused rather than handed to a provider's decoder.
    /// </summary>
    /// <remarks>
    /// The cursor is the one caller-supplied string this layer deliberately does not interpret, and it is reachable
    /// <b>without authentication</b> — so an unbounded one is free work for an anonymous caller on the way to a
    /// decoder. Asserted on both sides of the bound, since a fact only past it would pass against no bound at all
    /// once the transport's own request-line limit did the refusing.
    /// </remarks>
    [Fact]
    public void A_cursor_longer_than_any_page_could_have_issued_is_refused()
    {
        var longest = new string('c', 512);

        TryParse($"after={longest}", out var accepted, out var violations).ShouldBeTrue(Because(violations));
        TryParse($"after={longest}c", out _, out var refused).ShouldBeFalse();

        accepted!.Query.After.ShouldBe(longest);
        Codes(refused).ShouldBe(["invalid-cursor"]);
    }

    [Theory]
    [InlineData("order=year", "year asc nulls-last")]
    [InlineData("order=year.desc", "year desc nulls-last")]
    [InlineData("order=year.asc.nullsfirst", "year asc nulls-first")]
    [InlineData("order=year.desc.nullslast", "year desc nulls-last")]
    [InlineData("order=year.desc,make.asc", "year desc nulls-last, make asc nulls-last")]
    public void A_sort_parameter_parses_to_the_expected_keys(string queryString, string expected)
    {
        TryParse(queryString, out var parsed, out var violations).ShouldBeTrue(Because(violations));

        string.Join(", ", parsed!.Query.Sort.Select(Render)).ShouldBe(expected);
    }

    /// <summary>
    /// A sort modifier out of order is refused rather than tolerated, so one sort key has exactly one spelling.
    /// </summary>
    [Theory]
    [InlineData("order=year.nullsfirst.desc")]
    [InlineData("order=year.asc.desc")]
    [InlineData("order=year.nullsfirst.nullslast")]
    public void A_sort_key_carrying_its_modifiers_out_of_order_or_twice_is_refused(string queryString) =>
        OnlyViolation(queryString).Code.ShouldBe("malformed-order");

    /// <summary>
    /// Every list is paged — the default page size is always applied — so the port's rule that a paged read
    /// cannot sort by a nullable field makes a nullable sort key unusable over HTTP. Asserted rather than
    /// discovered: the required control is what turns this from a bug report into a stated contract.
    /// </summary>
    [Fact]
    public void A_sort_by_a_nullable_field_is_refused_because_every_list_is_paged()
    {
        TryParse("order=color", out _, out var refused).ShouldBeFalse();
        TryParse("order=year", out var required, out var violations).ShouldBeTrue(Because(violations));

        refused.Single().Code.ShouldBe("unpageable-sort-key");
        required!.Query.Sort.Single().Field.ShouldBe("year");
    }

    [Fact]
    public void A_projection_keeps_the_order_the_request_named_and_drops_a_duplicate()
    {
        TryParse("select=year,make,year", out var parsed, out var violations).ShouldBeTrue(Because(violations));

        parsed!.Select.ShouldBe(["year", "make"]);
    }

    /// <summary>
    /// A projection is not a filter: naming a field in <c>select</c> must not change which rows come back.
    /// </summary>
    [Fact]
    public void A_projection_does_not_become_a_filter()
    {
        TryParse("select=year", out var parsed, out var violations).ShouldBeTrue(Because(violations));

        parsed!.Query.Filter.ShouldBeNull();
    }

    /// <summary>
    /// <c>in</c> is capped at the port's own candidate limit, and the boundary is where it is asserted: the cap
    /// exists because each candidate becomes its own bind parameter, and a fact at ten thousand would pass
    /// against a parser whose cap was ten.
    /// </summary>
    [Fact]
    public void An_in_list_is_capped_at_the_ports_candidate_limit()
    {
        TryParse(InList(AlvoFilter.MaxInCandidates), out var atLimit, out var violations).ShouldBeTrue(Because(violations));
        TryParse(InList(AlvoFilter.MaxInCandidates + 1), out _, out var refused).ShouldBeFalse();

        Candidates(atLimit!.Query.Filter).Count.ShouldBe(AlvoFilter.MaxInCandidates);
        refused.Single().Code.ShouldBe("too-many-in-candidates");
    }

    /// <summary>
    /// A request that is wrong in three different ways reports <b>all three</b>, and reports each of them once.
    /// </summary>
    /// <remarks>
    /// The flooding case, which a global cap in arrival order silently loses: three hundred filter parameters
    /// produced twenty identical <c>filter-too-wide</c> entries and the <c>limit</c> and <c>order</c> mistakes in
    /// the same request were never reported at all. #19's definition of done and §2.1 both require every violation.
    /// Asserted on the exact code sequence, so a repeat fails it as loudly as an omission.
    /// </remarks>
    [Fact]
    public void A_request_wrong_in_three_ways_reports_all_three_refusals_once_each()
    {
        var flooded = string.Join("&", Enumerable.Repeat("year=gte.1", 300));

        TryParse($"{flooded}&limit=0&order=color", out _, out var violations).ShouldBeFalse();

        Codes(violations).ShouldBe(["filter-too-wide", "invalid-page-size", "unpageable-sort-key"]);
    }

    /// <summary>
    /// Two parameters failing the same way are one problem, reported once — a caller learns nothing from the
    /// second, and repeats are what crowded out the distinct refusals above.
    /// </summary>
    [Fact]
    public void Two_parameters_failing_the_same_way_are_reported_once()
    {
        TryParse("nosuchfield=eq.1&alsomissing=eq.2", out _, out var violations).ShouldBeFalse();

        Codes(violations).ShouldBe(["unavailable-field"]);
    }

    /// <summary>
    /// The <c>in</c>-candidate allowance is spent across the <b>whole query</b>, not per list — the bound the
    /// port cannot express, because it measures only the longest list.
    /// </summary>
    /// <remarks>
    /// Two lists of 600 are 1200 bind parameters in one statement while each passes the per-list cap, and the
    /// maximum number of terms each with a maximum list is 256 000 — past the 32 766 ceiling the per-list number
    /// was measured against. Asserted at the boundary on both sides: two lists summing to exactly the allowance
    /// parse, and one candidate more does not.
    /// </remarks>
    [Fact]
    public void The_in_candidate_allowance_is_spent_across_the_whole_query()
    {
        var half = AlvoFilter.MaxInCandidates / 2;
        var atAllowance = $"{InList("year", half)}&{InList("price", AlvoFilter.MaxInCandidates - half)}";
        var oneTooMany = $"{InList("year", half)}&{InList("price", AlvoFilter.MaxInCandidates - half + 1)}";

        TryParse(atAllowance, out _, out var accepted).ShouldBeTrue(Because(accepted));
        TryParse(oneTooMany, out _, out var refused).ShouldBeFalse();

        Codes(refused).ShouldBe(["too-many-in-candidates"]);
    }

    /// <summary>
    /// The term budget is per <b>request</b>, not per parameter, so a caller cannot multiply the port's breadth
    /// limit by sending many filters. Asserted at the <b>exact</b> boundary, on both sides, and on the code set
    /// rather than on membership.
    /// </summary>
    /// <remarks>
    /// The boundary is <c>MaxTerms - 1</c> parameters, not <c>MaxTerms</c>: the conjunction those parameters are
    /// wrapped in is itself a node, so N parameters produce N + 1 nodes. Getting that off by one is what let the
    /// port's own guard answer at exactly 256 parameters, emitting the code documented as unreachable — and the
    /// earlier version of this fact asserted <em>membership</em>, so it saw <c>filter-too-wide</c>, was satisfied,
    /// and never noticed the second violation beside it.
    /// </remarks>
    [Fact]
    public void The_term_budget_is_spent_across_every_parameter_not_reset_per_parameter()
    {
        var largestTree = string.Join("&", Enumerable.Range(0, AlvoFilter.MaxTerms - 1).Select(_ => "year=gte.1"));
        var oneNodeTooMany = string.Join("&", Enumerable.Range(0, AlvoFilter.MaxTerms).Select(_ => "year=gte.1"));

        TryParse(largestTree, out _, out var accepted).ShouldBeTrue(Because(accepted));
        TryParse(oneNodeTooMany, out _, out var refused).ShouldBeFalse();

        Codes(refused).ShouldBe(["filter-too-wide"]);
    }

    /// <summary>
    /// The conjunction those parameters are wrapped in is charged like any other node, so the largest tree a
    /// caller can build is exactly the port's own limit — never one node past it.
    /// </summary>
    /// <remarks>
    /// This is the fact whose absence let <c>filter-beyond-port-limits</c> reach a caller. It asserts the node
    /// count the port itself would measure, so it fails whether the conjunction goes uncharged or is charged too
    /// late to bound anything.
    /// </remarks>
    [Fact]
    public void The_largest_filter_a_caller_can_build_is_exactly_the_ports_own_term_limit()
    {
        var largestTree = string.Join("&", Enumerable.Range(0, AlvoFilter.MaxTerms - 1).Select(_ => "year=gte.1"));

        TryParse(largestTree, out var parsed, out var violations).ShouldBeTrue(Because(violations));

        var conjunction = parsed!.Query.Filter.ShouldBeOfType<AlvoAnd>();
        conjunction.Filters.Count.ShouldBe(AlvoFilter.MaxTerms - 1);
        Should.NotThrow(() => AlvoFilter.EnsureWithinLimits(parsed.Query.Filter));
    }

    /// <summary>
    /// Every name a query string reserves is a name the descriptor's own field grammar
    /// (<c>^[a-z][a-z0-9_]{0,62}$</c>) accepts, so the collision is <b>real</b> — which is why
    /// <see cref="ReservedQueryKeys.EnsureNoneIsShadowed"/> exists rather than a comment claiming it cannot
    /// happen. The plan asserted the opposite; this fact is what settles it.
    /// </summary>
    [Fact]
    public void Every_reserved_query_parameter_is_a_name_the_descriptor_would_accept_as_a_field()
    {
        var grammar = FieldNameGrammar();

        ReservedQueryKeys.All.ShouldAllBe(key => Regex.IsMatch(key, grammar, RegexOptions.None, _regexTimeout));
    }

    /// <summary>
    /// The field-name pattern read from <c>schema/project.schema.json</c> itself.
    /// </summary>
    /// <remarks>
    /// Read rather than restated. An inlined copy of the grammar keeps this fact green through exactly the change
    /// that should retire it — the schema growing the reserved-word exclusion that would make the collision
    /// impossible — so the fact would go on justifying a guard that had become dead. Reading the frozen artifact
    /// means a narrowed pattern fails here and the guard's justification is re-examined.
    /// </remarks>
    private static string FieldNameGrammar()
    {
        var schema = JsonDocument.Parse(
            File.ReadAllText(Path.Combine(RepositoryRoot.Find(), "schema", "project.schema.json")));
        return schema.RootElement
            .GetProperty("$defs").GetProperty("entity")
            .GetProperty("properties").GetProperty("fields")
            .GetProperty("propertyNames").GetProperty("pattern")
            .GetString()!;
    }

    private static readonly TimeSpan _regexTimeout = TimeSpan.FromSeconds(1);

    /// <summary>
    /// So the ambiguity is refused once, at mapping, naming the entity, the field and the fix — never
    /// per request, which would make a descriptor problem look like a caller's.
    /// </summary>
    [Fact]
    public void An_entity_declaring_a_field_that_shadows_a_reserved_parameter_is_refused()
    {
        foreach (var reserved in ReservedQueryKeys.All)
        {
            var shadowing = _vehicles with
            {
                Fields = [.. _vehicles.Fields, new FieldSchema { Name = reserved, Type = FieldType.String }],
            };

            var refusal = Should.Throw<InvalidOperationException>(
                () => ReservedQueryKeys.EnsureNoneIsShadowed(shadowing));
            refusal.Message.ShouldContain(reserved);
        }

        Should.NotThrow(() => ReservedQueryKeys.EnsureNoneIsShadowed(_vehicles));
    }

    /// <summary>
    /// The negation prefix is split off a key and passed as a flag, never re-encoded into member text. Written
    /// as a fact because the re-encoding design reads as harmless and is not: joining <c>not</c> back onto its
    /// value turns a filter over a field into a negation over a field called <c>eq</c>.
    /// </summary>
    [Fact]
    public void A_negated_key_negates_the_field_it_names_and_nothing_else()
    {
        TryParse("not.year=eq.2020", out var parsed, out var violations).ShouldBeTrue(Because(violations));

        Render(parsed!.Query.Filter).ShouldBe("NOT year == 2020");
    }

    /// <summary>
    /// A negated group is PostgREST's own <c>not.or=(…)</c>, and it is the same grammar path as a negated term.
    /// </summary>
    [Fact]
    public void A_negated_group_is_the_negation_of_the_whole_group()
    {
        TryParse("not.or=(year.eq.2020,year.eq.2021)", out var parsed, out var violations).ShouldBeTrue(Because(violations));

        Render(parsed!.Query.Filter).ShouldBe("NOT (year == 2020 OR year == 2021)");
    }

    private static string InList(int candidates) => InList("year", candidates);

    private static string InList(string field, int candidates) =>
        $"{field}=in.(" + string.Join(",", Enumerable.Range(1, candidates)) + ")";

    private static IReadOnlyList<object?> Candidates(AlvoFilter? filter) =>
        (IReadOnlyList<object?>)((AlvoComparison)filter!).Value!;

    /// <summary>
    /// Every code a refusal carried, in order and <b>with repeats</b>. Asserted as the whole sequence rather
    /// than with <c>ShouldContain</c>, which is satisfied by a parser that emits every code it knows — and which
    /// is how a second, wrong violation rode along beside the right one unnoticed. It no longer de-duplicates
    /// either: the parser is what must not repeat itself, and a helper that hid repeats hid exactly the flooding
    /// that made distinct refusals vanish.
    /// </summary>
    private static IReadOnlyList<string> Codes(IReadOnlyList<AlvoViolation> violations) =>
        [.. violations.Select(violation => violation.Code).Order(StringComparer.Ordinal)];

    private static AlvoViolation OnlyViolation(string queryString)
    {
        TryParse(queryString, out _, out var violations).ShouldBeFalse();
        return violations.ShouldHaveSingleItem();
    }

    private static bool TryParse(
        string queryString, out ParsedListQuery? parsed, out IReadOnlyList<AlvoViolation> violations) =>
        QueryStringParser.TryParse(Query(queryString), _vehicles, _masked, _options, out parsed, out violations);

    private static QueryCollection Query(string queryString) =>
        new QueryCollection(QueryHelpers.ParseQuery("?" + queryString));

    private static string Because(IReadOnlyList<AlvoViolation> violations) =>
        violations.Count == 0
            ? "the query was expected to parse"
            : "the query was expected to parse, but was refused: "
                + string.Join("; ", violations.Select(violation => $"{violation.Pointer}/{violation.Code}"));

    /// <summary>
    /// A test-local rendering of a filter tree, so an expectation reads as the shape it is and a wrong
    /// <em>shape</em> — not merely a wrong value — fails. Deliberately not the port's own <c>ToString</c>: a
    /// formatter shared with production would make these facts a tautology over whatever production produced.
    /// </summary>
    private static string Render(AlvoFilter? filter) => filter switch
    {
        null => "(none)",
        AlvoComparison comparison =>
            $"{comparison.Field} {Symbol(comparison.Operator)} {RenderValue(comparison.Value)}",
        AlvoAnd and => $"({string.Join(" AND ", and.Filters.Select(Render))})",
        AlvoOr or => $"({string.Join(" OR ", or.Filters.Select(Render))})",
        AlvoNot not => $"NOT {Render(not.Filter)}",
        _ => throw new InvalidOperationException($"'{filter.GetType().Name}' is not a filter case this test renders."),
    };

    private static string Render(AlvoSort key) =>
        $"{key.Field} {(key.Descending ? "desc" : "asc")} nulls-{(key.Nulls == AlvoNullPlacement.First ? "first" : "last")}";

    private static string Symbol(AlvoFilterOperator @operator) => @operator switch
    {
        AlvoFilterOperator.Eq => "==",
        AlvoFilterOperator.Neq => "!=",
        AlvoFilterOperator.Gt => ">",
        AlvoFilterOperator.Gte => ">=",
        AlvoFilterOperator.Lt => "<",
        AlvoFilterOperator.Lte => "<=",
        AlvoFilterOperator.Like => "LIKE",
        AlvoFilterOperator.ILike => "ILIKE",
        AlvoFilterOperator.In => "IN",
        AlvoFilterOperator.Is => "IS",
        _ => throw new InvalidOperationException($"'{@operator}' is not an operator this test renders."),
    };

    private static string RenderValue(object? value) => value switch
    {
        null => "null",
        string text => text,
        DateOnly date => date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
        DateTimeOffset instant => instant.ToString("O", CultureInfo.InvariantCulture),
        IReadOnlyList<object?> candidates => $"[{string.Join(", ", candidates.Select(RenderValue))}]",
        _ => Convert.ToString(value, CultureInfo.InvariantCulture)!,
    };
}
