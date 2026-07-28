using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.WebUtilities;
using MMLib.Alvo.Api.Internal;
using MMLib.Alvo.Data;
using MMLib.Alvo.Schema;
using System.Globalization;

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

        violations.Select(violation => violation.Code).ShouldContain(code);
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
    /// The term budget is per <b>request</b>, not per parameter, so a caller cannot multiply the port's breadth
    /// limit by sending many filters. The boundary is asserted on both sides of the cap.
    /// </summary>
    [Fact]
    public void The_term_budget_is_spent_across_every_parameter_not_reset_per_parameter()
    {
        var justInside = string.Join("&", Enumerable.Range(0, AlvoFilter.MaxTerms - 1).Select(_ => "year=gte.1"));
        var justOutside = string.Join("&", Enumerable.Range(0, AlvoFilter.MaxTerms + 1).Select(_ => "year=gte.1"));

        TryParse(justInside, out _, out var accepted).ShouldBeTrue(Because(accepted));
        TryParse(justOutside, out _, out var refused).ShouldBeFalse();

        refused.Select(violation => violation.Code).ShouldContain("filter-too-wide");
    }

    /// <summary>
    /// Every name a query string reserves is a name the descriptor's own field grammar
    /// (<c>^[a-z][a-z0-9_]{0,62}$</c>) accepts, so the collision is <b>real</b> — which is why
    /// <see cref="ReservedQueryKeys.EnsureNoneIsShadowed"/> exists rather than a comment claiming it cannot
    /// happen. The plan asserted the opposite; this fact is what settles it.
    /// </summary>
    [Fact]
    public void Every_reserved_query_parameter_is_a_name_the_descriptor_would_accept_as_a_field() =>
        ReservedQueryKeys.All.ShouldAllBe(key => IsLegalFieldName(key));

    private static bool IsLegalFieldName(string name) =>
        name.Length is > 0 and <= 63
        && char.IsAsciiLetterLower(name[0])
        && name.All(character => char.IsAsciiLetterLower(character) || char.IsAsciiDigit(character) || character == '_');

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

    private static string InList(int candidates) =>
        "year=in.(" + string.Join(",", Enumerable.Range(1, candidates)) + ")";

    private static IReadOnlyList<object?> Candidates(AlvoFilter? filter) =>
        (IReadOnlyList<object?>)((AlvoComparison)filter!).Value!;

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
