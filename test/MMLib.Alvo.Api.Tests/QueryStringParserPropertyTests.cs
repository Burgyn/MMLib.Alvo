using CsCheck;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.WebUtilities;
using MMLib.Alvo.Api.Internal;
using MMLib.Alvo.Data;
using MMLib.Alvo.Schema;

namespace MMLib.Alvo.Api.Tests;

/// <summary>
/// The parser's robustness property: arbitrary query strings either parse or are refused with violations —
/// never an exception, and never a filter past the port's own limits.
/// </summary>
/// <remarks>
/// <para>
/// <b>A "never throws" property is trivially true of a parser that refuses everything</b>, so every run here
/// counts how many inputs it <em>accepted</em> and asserts a floor, alongside how many it refused. Without
/// both counters a later narrowing of the generator — or a parser that started rejecting a whole production —
/// would turn the property vacuous while staying green, which is the exact failure mode three earlier rounds
/// of this PR shipped.
/// </para>
/// <para>
/// The counters are also what pin the <em>corpus</em>: the productions listed must really be spelled by the
/// generator, so a shrunken token pool fails loudly instead of quietly testing less.
/// </para>
/// </remarks>
public sealed class QueryStringParserPropertyTests
{
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
            new FieldSchema { Name = "secret", Type = FieldType.String },
        ],
    };

    private static readonly IReadOnlySet<string> _masked = new HashSet<string>(StringComparer.Ordinal) { "secret" };

    private static readonly AlvoApiOptions _options = new();

    private const int Iterations = 10_000;

    /// <summary>
    /// Every parameter name the grammar gives meaning to, plus ones it does not — a masked field, an
    /// undeclared field, and a mistyped keyword, since those are the three refusals that must be
    /// indistinguishable.
    /// </summary>
    private static readonly string[] _names =
    [
        "year", "color", "make", "notes", "price", "passed", "id",
        "secret", "nosuchfield", "oder",
        "or", "and", "not.color", "not.or", "order", "limit", "offset", "after", "select",
    ];

    /// <summary>Every operator on the allow-list, one that is not, and the empty token a reserved parameter uses.</summary>
    private static readonly string[] _operators =
    [
        "eq", "neq", "gt", "gte", "lt", "lte", "like", "ilike", "in", "is", "nosuchop", string.Empty,
    ];

    /// <summary>
    /// Values a legitimate caller sends, values no field can hold, and the classic injection payloads — so
    /// the property fuzzes the operand position rather than only the structural one.
    /// </summary>
    private static readonly string[] _values =
    [
        "2020", "red", "null", "true", "false", "2020.5", "1500.50", "(skoda,vw)", "(", ")", "()",
        string.Empty, "1", "10", "-1", "0", "100000", "year.desc", "year.sideways", "make,year",
        "abc", "'", "\"", "%", "\\", "' OR 1=1 --", "'; DROP TABLE vehicles; --", "\0", "\u202E", "e\u0301",
    ];

    /// <summary>Productions the structured generator must really spell, or the corpus is smaller than it claims.</summary>
    private static readonly string[] _productions =
        ["eq.", "in.(", "is.", "like.", "or=", "and=", "not.", "order=", "limit=", "select=", "' OR 1=1 --", "\0"];

    [Fact]
    public void No_query_string_makes_the_parser_throw()
    {
        var coverage = new Coverage(_productions, breadthBoundary: AlvoFilter.MaxTerms - 1);

        Gen.OneOf(HostileQueries, WideQueries)
            .Sample(candidate => coverage.Observe(candidate, ParsesWithinPortLimits(candidate)), iter: Iterations);

        coverage.ShouldHaveReachedBothOutcomes(atLeastAccepted: Iterations / 20);
        coverage.ShouldHaveSpelledEveryProduction();
        coverage.ShouldHaveStraddled(AlvoFilter.MaxTerms - 1, "parameters");
    }

    /// <summary>
    /// A handful of parameters drawn from the full hostile pools — the leg that fuzzes the <em>operand</em> and
    /// <em>structural</em> positions, and the one a correct parser accepts often enough for "it never throws" to
    /// mean something.
    /// </summary>
    private static Gen<string> HostileQueries =>
        Gen.Select(Gen.OneOfConst(_names), Gen.OneOfConst(_operators), Gen.OneOfConst(_values))
            .Array[0, 4]
            .Select(parameters => string.Join("&", parameters.Select(Compose)));

    /// <summary>
    /// One well-formed parameter repeated a number of times that <b>straddles</b>
    /// <see cref="AlvoFilter.MaxTerms"/> — the leg that reaches the breadth boundary.
    /// </summary>
    /// <remarks>
    /// It exists because the hostile leg alone cannot: with at most four parameters, the corpus-wide claim that
    /// no generated query reaches <c>filter-beyond-port-limits</c> was true of inputs that could not have
    /// reached it either way, while <c>?year=gte.1</c> × 256 did. A single generator covering both was tried
    /// first and does not work — a uniform length up to 300 makes nearly every query carry a bad parameter, and
    /// the acceptance share collapsed from 24 % to 0.5 %. Two legs keep both properties: this one is
    /// deliberately well-formed, so its low side really is accepted and its high side really is refused.
    /// </remarks>
    private static Gen<string> WideQueries =>
        Gen.Select(Gen.OneOfConst(_wellFormedParameters), Gen.Int[AlvoFilter.MaxTerms - 8, AlvoFilter.MaxTerms + 8])
            .Select(repeated => string.Join("&", Enumerable.Repeat(repeated.Item1, repeated.Item2)));

    /// <summary>Parameters every one of which a correct parser accepts, however many times they are repeated.</summary>
    private static readonly string[] _wellFormedParameters =
        ["year=gte.1", "make=eq.vw", "color=like.re%", "price=lt.99.5", "notes=is.null", "passed=is.true"];

    /// <summary>
    /// The companion property, over raw characters rather than tokens. It deliberately makes <b>no</b>
    /// acceptance claim: a character-level generator cannot spell a field name, so almost nothing it produces
    /// can parse, and asserting a floor here would only be satisfiable by the empty string. Its job is the one
    /// the structured property cannot do — reaching byte sequences no token pool contains.
    /// </summary>
    [Fact]
    public void Arbitrary_characters_never_make_the_parser_throw()
    {
        const string alphabet = "aceilmnorstuwy_0123456789.,()=&%'\"\\+-*/:;<>!?[]{}@\0 \n\t\u202E";
        var coverage = new Coverage([".", "=", "&", "("]);

        Gen.Char[alphabet].Array[0, 200]
            .Select(characters => new string(characters))
            .Sample(candidate => coverage.Observe(candidate, ParsesWithinPortLimits(candidate)), iter: Iterations);

        coverage.ShouldHaveRefusedSomething();
        coverage.ShouldHaveSpelledEveryProduction();
    }

    /// <summary>
    /// Ten thousand nested groups must be refused <b>without</b> ten thousand stack frames. A
    /// <see cref="StackOverflowException"/> is not an exception a request pipeline turns into a 500 — it ends
    /// the process — so this is the one fact whose failure mode is the test host disappearing rather than a red
    /// assertion.
    /// </summary>
    [Theory]
    [InlineData(64)]
    [InlineData(1_000)]
    [InlineData(10_000)]
    public void A_deeply_nested_group_is_refused_without_exhausting_the_stack(int depth)
    {
        var nested = "or=" + string.Concat(Enumerable.Repeat("(or=", depth)) + "(year.eq.1"
            + new string(')', depth + 1);

        TryParse(nested, out _, out var violations).ShouldBeFalse();

        Codes(violations).ShouldBe(["filter-too-deep"]);
    }

    /// <summary>
    /// The breadth counterpart, which a depth cap misses entirely: one group carrying a hundred thousand
    /// members is shallow and still a statement no engine should be asked to compose.
    /// </summary>
    [Fact]
    public void A_very_wide_group_is_refused()
    {
        var wide = "or=(" + string.Join(",", Enumerable.Repeat("year.eq.1", 100_000)) + ")";

        TryParse(wide, out _, out var violations).ShouldBeFalse();

        Codes(violations).ShouldBe(["filter-too-wide"]);
    }

    /// <summary>
    /// The parser's own nesting cap sits strictly inside the port's, by the two levels a descent never checks:
    /// the comparison leaf and the top-level conjunction. Asserted, because "strictly inside" is what makes the
    /// port's guard unreachable, and an equal cap would put it back in play for a multi-parameter query.
    /// </summary>
    [Fact]
    public void The_parsers_nesting_cap_reserves_the_two_levels_a_descent_never_checks() =>
        FilterGroupParser.MaxNesting.ShouldBe(AlvoFilter.MaxDepth - 2);

    /// <summary>
    /// The nesting cap is asserted on both sides of the boundary. A fact only past the cap would pass against a
    /// parser whose limit was two.
    /// </summary>
    [Fact]
    public void Nesting_up_to_the_parsers_cap_parses_and_one_level_more_does_not()
    {
        TryParse(Nested(FilterGroupParser.MaxNesting), out var atCap, out var accepted)
            .ShouldBeTrue(Because(accepted));
        TryParse(Nested(FilterGroupParser.MaxNesting + 1), out _, out var refused).ShouldBeFalse();

        atCap!.Query.Filter.ShouldNotBeNull();
        Codes(refused).ShouldBe(["filter-too-deep"]);
    }

    /// <summary>
    /// The reservation's <b>real</b> worst case: the deepest nesting a caller may send <em>plus</em> a second
    /// parameter, so the conjunction that wraps them lands exactly on <see cref="AlvoFilter.MaxDepth"/>.
    /// </summary>
    /// <remarks>
    /// The margin is zero by design, which means it is defended by this fact or by nothing. The boundary fact
    /// above only exercises a single parameter, where one of the two reserved levels goes unused — so it would
    /// stay green with the reservation one level too small, and the port's own guard would answer instead.
    /// </remarks>
    [Fact]
    public void The_deepest_nesting_beside_a_second_parameter_lands_exactly_on_the_ports_depth_cap()
    {
        var deepestPlusOne = $"{Nested(FilterGroupParser.MaxNesting)}&year=eq.1";
        var oneLevelTooDeep = $"{Nested(FilterGroupParser.MaxNesting + 1)}&year=eq.1";

        TryParse(deepestPlusOne, out var parsed, out var accepted).ShouldBeTrue(Because(accepted));
        TryParse(oneLevelTooDeep, out _, out var refused).ShouldBeFalse();

        parsed!.Query.Filter.ShouldBeOfType<AlvoAnd>();
        Should.NotThrow(
            () => AlvoFilter.EnsureWithinLimits(parsed.Query.Filter),
            "the parser must never produce a tree the port then refuses");
        Codes(refused).ShouldBe(["filter-too-deep"]);
    }

    private static string Because(IReadOnlyList<AlvoViolation> violations) =>
        violations.Count == 0
            ? "the query was expected to parse"
            : "the query was expected to parse, but was refused: "
                + string.Join("; ", violations.Select(violation => $"{violation.Pointer}/{violation.Code}"));

    /// <summary>
    /// A group nested <paramref name="groups"/> deep around one comparison, so the tree's depth is
    /// <paramref name="groups"/> connectives plus one leaf.
    /// </summary>
    private static string Nested(int groups) =>
        "or=" + string.Concat(Enumerable.Repeat("(or=", groups - 1)) + "(year.eq.1" + new string(')', groups);

    private static string Compose((string Name, string Operator, string Value) parameter) =>
        parameter.Operator.Length == 0
            ? $"{parameter.Name}={parameter.Value}"
            : $"{parameter.Name}={parameter.Operator}.{parameter.Value}";

    /// <summary>
    /// Whether <paramref name="candidate"/> parsed — and, when it did, that what came out is inside every rule
    /// the port enforces for itself. A parser that accepted a tree the port then refuses would have moved the
    /// failure from a 422 with a fix suggestion to an <c>ArgumentException</c> from inside a query.
    /// </summary>
    private static bool ParsesWithinPortLimits(string candidate)
    {
        if (!TryParse(candidate, out var parsed, out var violations))
        {
            violations.ShouldNotBeEmpty("a refusal with no violation gives the caller nothing to act on");
            violations.ShouldNotContain(
                violation => violation.Code == "filter-beyond-port-limits",
                "the port's own guard refused what this parser produced, so the parser's caps are not strictly "
                + "inside the port's — that code is a defect report, not a caller-facing refusal");
            return false;
        }

        AlvoFilter.EnsureWithinLimits(parsed!.Query.Filter);
        AlvoQuery.EnsurePagingWindowIsSane(parsed.Query);
        parsed.Query.Limit!.Value.ShouldBeInRange(1, _options.MaxPageSize);
        return true;
    }

    /// <summary>Every code a refusal carried, in order and with repeats — so both a missing and a repeated one fail.</summary>
    private static IReadOnlyList<string> Codes(IReadOnlyList<AlvoViolation> violations) =>
        [.. violations.Select(violation => violation.Code).Order(StringComparer.Ordinal)];

    private static bool TryParse(
        string queryString, out ParsedListQuery? parsed, out IReadOnlyList<AlvoViolation> violations) =>
        QueryStringParser.TryParse(
            new QueryCollection(QueryHelpers.ParseQuery("?" + queryString)),
            _vehicles, _masked, _options, out parsed, out violations);

    /// <summary>
    /// What a run actually covered: how often each production was spelled, and how often the parser accepted
    /// and refused. CsCheck may sample in parallel, so every counter moves through <see cref="Interlocked"/>.
    /// </summary>
    /// <param name="productions">The substrings the generator must be able to spell.</param>
    /// <param name="breadthBoundary">
    /// The parameter count the corpus must straddle, or <see langword="null"/> when this property makes no
    /// breadth claim.
    /// </param>
    private sealed class Coverage(IReadOnlyList<string> productions, int? breadthBoundary = null)
    {
        private readonly long[] _spelled = new long[productions.Count];
        private long _accepted;
        private long _refused;
        private long _atOrBelow;
        private long _above;

        internal void Observe(string candidate, bool accepted)
        {
            ObserveBreadth(candidate);
            if (accepted)
            {
                Interlocked.Increment(ref _accepted);
            }
            else
            {
                Interlocked.Increment(ref _refused);
            }

            for (var index = 0; index < productions.Count; index++)
            {
                if (candidate.Contains(productions[index], StringComparison.Ordinal))
                {
                    Interlocked.Increment(ref _spelled[index]);
                }
            }
        }

        internal void ShouldHaveReachedBothOutcomes(long atLeastAccepted)
        {
            _accepted.ShouldBeGreaterThanOrEqualTo(
                atLeastAccepted,
                $"only {_accepted} generated queries parsed, so 'it never throws' is close to vacuous — the "
                + "generator must produce queries a correct parser accepts, not only ones it refuses.");
            ShouldHaveRefusedSomething();
        }

        internal void ShouldHaveRefusedSomething() =>
            _refused.ShouldBeGreaterThan(0, "no generated query was refused, so the refusal path never ran.");

        /// <summary>
        /// Asserts the corpus really reached both sides of a boundary it claims to guard. An assertion about
        /// inputs a generator cannot produce is not evidence — which is precisely how the corpus-wide
        /// "the port's guard never fires" claim came to be worthless.
        /// </summary>
        /// <param name="boundary">The number of parameters the corpus must straddle.</param>
        /// <param name="what">What is being counted, for the failure message.</param>
        internal void ShouldHaveStraddled(int boundary, string what)
        {
            _atOrBelow.ShouldBeGreaterThan(0, $"no generated query carried {boundary} {what} or fewer.");
            _above.ShouldBeGreaterThan(
                0,
                $"no generated query carried more than {boundary} {what}, so the corpus never reached the "
                + "boundary it is asserted to guard — an assertion that cannot fire is not evidence.");
        }

        /// <summary>
        /// Counts a candidate's parameters — one more than its <c>&amp;</c> separators — against the boundary
        /// this coverage was built for, when it was built for one.
        /// </summary>
        private void ObserveBreadth(string candidate)
        {
            if (breadthBoundary is not { } boundary)
            {
                return;
            }

            var parameters = candidate.Length == 0 ? 0 : candidate.Count(character => character == '&') + 1;
            Interlocked.Increment(ref parameters > boundary ? ref _above : ref _atOrBelow);
        }

        internal void ShouldHaveSpelledEveryProduction()
        {
            for (var index = 0; index < productions.Count; index++)
            {
                _spelled[index].ShouldBeGreaterThan(
                    0,
                    $"the generator never spelled '{productions[index]}', so no sample exercised it.");
            }
        }
    }
}
