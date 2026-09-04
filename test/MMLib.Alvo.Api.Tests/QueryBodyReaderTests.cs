using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.WebUtilities;
using MMLib.Alvo.Api.Internal;
using System.Text;
using System.Text.Json;

namespace MMLib.Alvo.Api.Tests;

/// <summary>
/// The transposition, on its own. What is under test here is that a JSON object becomes the <em>same
/// collection</em> ASP.NET Core would have handed the parser — not that the grammar works, which is
/// <c>QueryStringParserTests</c>' subject and is deliberately not re-asserted against a second surface.
/// </summary>
public sealed class QueryBodyReaderTests
{
    private static readonly AlvoApiOptions _options = new();

    /// <summary>
    /// The corpus fact, and the reason it is the only equivalence claim worth making: for every query string
    /// the parser's own suite drives, the JSON transposition of the collection ASP.NET Core parses it into
    /// reads back as that same collection. Transposing the <em>parsed</em> collection rather than the raw
    /// text is what makes the claim about decoded values true by construction — a body carries values, a
    /// query string carries their percent-encoding.
    /// </summary>
    [Theory]
    [InlineData("year=gte.2020")]
    [InlineData("make=in.(skoda,vw)")]
    [InlineData("or=(color.eq.red,color.eq.blue)")]
    [InlineData("not.color=eq.red")]
    [InlineData("year=gte.2020&year=lte.2024")]
    [InlineData("select=id,label:make&order=year.desc.nullsfirst&limit=10&offset=5")]
    [InlineData("make=like.sko%25")]
    [InlineData("notes=is.null")]
    [InlineData("after=3q2-796tvE-cKTMlvKYbGw")]
    public async Task A_transposed_query_string_reads_back_as_the_same_collection(string queryString)
    {
        var expected = new QueryCollection(QueryHelpers.ParseQuery("?" + queryString));

        var actual = await ReadAsync(AsJson(expected));

        actual.Violations.ShouldBeEmpty();
        Rendered(actual.Parameters!).ShouldBe(Rendered(expected));
    }

    /// <summary>
    /// A number contributes the literal the caller wrote, so a decimal filter survives without a round trip
    /// through a CLR type and a format provider.
    /// </summary>
    [Fact]
    public async Task A_json_number_contributes_its_raw_text()
    {
        var read = await ReadAsync("""{"limit":100,"price":"lt.1500.50"}""");

        read.Parameters!["limit"].ToString().ShouldBe("100");
        read.Parameters["price"].ToString().ShouldBe("lt.1500.50");
    }

    /// <summary>An array is a repeated parameter, which is how a caller writes two groups.</summary>
    [Fact]
    public async Task An_array_is_the_same_parameter_twice()
    {
        var read = await ReadAsync("""{"or":["(a.eq.1)","(b.eq.2)"]}""");

        read.Violations.ShouldBeEmpty();
        read.Parameters!["or"].Count.ShouldBe(2);
    }

    /// <summary>
    /// Keys are compared the way <c>QueryCollection</c> compares them, so two names differing only in case
    /// are one parameter sent twice — the same collection the query string produces, and therefore the same
    /// refusal downstream rather than a different one.
    /// </summary>
    /// <remarks>
    /// Driven in <b>both orders</b>, because which spelling survives as the key is what decides whether the
    /// parser reads the parameter as a setting or as a filter — and the two surfaces must agree on that. A
    /// dictionary indexer keeps the first key it saw, exactly as <c>KeyValueAccumulator</c> does for a query
    /// string, so <c>LIMIT</c> first is a filter on both surfaces and <c>limit</c> first is a repeated
    /// setting on both.
    /// </remarks>
    [Theory]
    [InlineData("""{"limit":1,"LIMIT":2}""", "limit")]
    [InlineData("""{"LIMIT":1,"limit":2}""", "LIMIT")]
    public async Task Two_names_differing_only_in_case_are_one_parameter_sent_twice(string body, string key)
    {
        var expected = new QueryCollection(
            QueryHelpers.ParseQuery("?" + string.Join('&', body.Trim('{', '}')
                .Replace("\"", string.Empty, StringComparison.Ordinal)
                .Split(',')
                .Select(member => member.Replace(':', '=')))));

        var read = await ReadAsync(body);

        read.Violations.ShouldBeEmpty();
        read.Parameters!.Count.ShouldBe(1);
        read.Parameters[key].Count.ShouldBe(2);
        Rendered(read.Parameters).ShouldBe(
            Rendered(expected), "the two surfaces must merge a case-differing repeat identically");
    }

    /// <summary>
    /// A value that is not a scalar names no parameter value, and the refusal points at the parameter's
    /// role — never at the caller's own key, which on this surface would answer "does this entity have a
    /// field called X".
    /// </summary>
    [Theory]
    [InlineData("""{"year":null}""", "filter")]
    [InlineData("""{"year":{"gte":2020}}""", "filter")]
    [InlineData("""{"year":[["nested"]]}""", "filter")]
    [InlineData("""{"or":[]}""", "filter")]
    [InlineData("""{"limit":null}""", "limit")]
    [InlineData("""{"select":{}}""", "select")]
    [InlineData("""{"order":[]}""", "order")]
    public async Task A_value_that_is_not_a_scalar_is_refused_at_the_parameters_role(string body, string role)
    {
        var read = await ReadAsync(body);

        read.Parameters.ShouldBeNull();
        var violation = read.Violations.ShouldHaveSingleItem();
        violation.Code.ShouldBe("unrepresentable-query-value");
        violation.Pointer.ShouldBe(role);
    }

    /// <summary>
    /// A body-level refusal points at the body, carries the write path's stable code and the read path's
    /// prose — an agent told to "send only the fields you are changing" on a read is told to fix another
    /// operation.
    /// </summary>
    [Theory]
    [InlineData("[1,2,3]", "not-an-object")]
    [InlineData("{", "malformed-json")]
    [InlineData("""{"or":"(a.eq.1)","or":"(b.eq.2)"}""", "duplicate-field")]
    public async Task A_body_that_is_not_a_bindable_object_is_refused_with_the_reads_own_wording(
        string body, string code)
    {
        var read = await ReadAsync(body);

        read.Parameters.ShouldBeNull();
        var violation = read.Violations.ShouldHaveSingleItem();
        violation.Code.ShouldBe(code);
        violation.Pointer.ShouldBeEmpty();
        var fix = violation.FixSuggestion.ShouldNotBeNull();
        fix.ShouldNotContain("writ", Case.Insensitive);
        fix.ShouldNotContain("field you", Case.Insensitive);
    }

    /// <summary>
    /// A body carrying more parameter <em>values</em> than this API reads is refused — and the bound is
    /// reached while the values are being read, not after.
    /// </summary>
    /// <remarks>
    /// <b>Nothing above this counts them.</b> <c>BoundedJsonBody</c>'s key bound counts property names at
    /// every depth, and an array's elements are not property names — so one parameter repeated half a
    /// million times is one key, satisfies every shape bound, and fits inside <c>MaxRequestBodyBytes</c>.
    /// The parser would refuse the 257th filter term, but only after the transposition had built all half
    /// million of them.
    /// </remarks>
    [Fact]
    public async Task A_body_carrying_more_values_than_the_reader_reads_is_refused()
    {
        var repeated = string.Join(',', Enumerable.Repeat("\"(a.eq.1)\"", _options.MaxPayloadKeys + 1));

        var read = await ReadAsync($$"""{"or":[{{repeated}}]}""");

        read.Parameters.ShouldBeNull();
        read.Violations.ShouldHaveSingleItem().Code.ShouldBe("too-many-query-values");
    }

    /// <summary>
    /// And it is genuinely bounded rather than merely refused: a body far past the bound is answered
    /// promptly, which is the property a quadratic accumulation would have destroyed.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Appending one value at a time to a <c>StringValues</c> copies the whole of it each time, so the cost
    /// of N values was quadratic in N — a length the caller chooses. The bound caps N, and building the
    /// values once rather than N times is what makes the capped work linear.
    /// </para>
    /// <para>
    /// <b>Sized to fit inside <c>MaxRequestBodyBytes</c> on purpose.</b> A body big enough to be refused by
    /// the byte bound proves nothing about this one — the point is that a body the byte bound <em>admits</em>
    /// can still carry a hundred thousand values, which is where the quadratic cost lived.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task A_body_far_past_the_value_bound_is_refused_promptly()
    {
        var repeated = string.Join(',', Enumerable.Repeat("\"1\"", 100_000));
        var body = $$"""{"or":[{{repeated}}]}""";
        System.Text.Encoding.UTF8.GetByteCount(body).ShouldBeLessThan(
            _options.MaxRequestBodyBytes, "or the byte bound refuses this before the value bound can");
        var clock = System.Diagnostics.Stopwatch.StartNew();

        var read = await ReadAsync(body);

        clock.Stop();
        read.Violations.ShouldHaveSingleItem().Code.ShouldBe("too-many-query-values");
        clock.Elapsed.ShouldBeLessThan(
            TimeSpan.FromSeconds(5),
            "the transposition must stop at the bound rather than building every value first");
    }

    /// <summary>
    /// One kind of bad value is one violation, however many elements carry it — the de-duplication
    /// <c>QueryStringParser</c> already applies, for the same reason: a response that repeats one refusal
    /// ten thousand times tells a caller nothing the first told them.
    /// </summary>
    [Fact]
    public async Task An_array_of_bad_values_is_one_violation_not_one_per_element()
    {
        var read = await ReadAsync("""{"year":[null,null,null,null,null]}""");

        read.Parameters.ShouldBeNull();
        read.Violations.ShouldHaveSingleItem().Code.ShouldBe("unrepresentable-query-value");
    }

    /// <summary>An empty object is the empty query, not a refusal: every readable field, the default page.</summary>
    [Fact]
    public async Task An_empty_object_is_the_empty_query()
    {
        var read = await ReadAsync("{}");

        read.Violations.ShouldBeEmpty();
        read.Parameters!.Count.ShouldBe(0);
    }

    /// <summary>
    /// The shape scan is exactly one level stricter than the parse it hands the buffer to. At the bound the
    /// scan admits the body and the parse accepts it — the fact reads that off the refusal the
    /// <em>transposition</em> then produces, because reaching the transposition at all is what proves the
    /// parse did not throw. One level deeper the scan refuses first, by name.
    /// </summary>
    /// <remarks>
    /// The two conventions count differently — <c>JsonDocumentOptions.MaxDepth</c> counts the outermost
    /// container as level 1 where <c>Utf8JsonReader.CurrentDepth</c> reports it as 0 — which looks like an
    /// off-by-one waiting to turn an accepted body into an uncaught <c>JsonException</c> that
    /// <c>ProblemResultFactory.GuardAsync</c> would render as a 500. It is not, and this is what holds that.
    /// </remarks>
    [Fact]
    public async Task A_body_at_the_depth_bound_reaches_the_transposition_and_one_past_it_does_not()
    {
        var atBound = await ReadAsync(Nested(_options.MaxPayloadDepth));
        var pastBound = await ReadAsync(Nested(_options.MaxPayloadDepth + 1));

        atBound.Violations.ShouldHaveSingleItem().Code.ShouldBe(
            "unrepresentable-query-value",
            "a body at the bound must reach the transposition, which is only possible if the parse accepted "
            + "what the scan admitted");
        pastBound.Violations.ShouldHaveSingleItem().Code.ShouldBe("body-too-deep");
    }

    /// <summary>
    /// A body carrying exactly <paramref name="containers"/> nested containers — the root object plus
    /// <paramref name="containers"/> minus one arrays, which is the number both the scan's
    /// <c>CurrentDepth</c> and <c>JsonDocumentOptions.MaxDepth</c> count.
    /// </summary>
    /// <param name="containers">How many containers deep the innermost value should sit.</param>
    private static string Nested(int containers)
    {
        var arrays = containers - 1;
        return "{\"year\":" + new string('[', arrays) + "\"x\"" + new string(']', arrays) + "}";
    }

    private static Task<QueryBodyReader.Result> ReadAsync(string body)
    {
        var context = new DefaultHttpContext();
        context.Request.Body = new MemoryStream(Encoding.UTF8.GetBytes(body));
        context.Request.ContentLength = context.Request.Body.Length;
        return QueryBodyReader.ReadAsync(context.Request, _options, TestContext.Current.CancellationToken);
    }

    /// <summary>The transposition under test: the already-parsed collection, written out as JSON.</summary>
    private static string AsJson(IQueryCollection query) =>
        JsonSerializer.Serialize(
            query.ToDictionary(
                parameter => parameter.Key,
                parameter => parameter.Value.Select(value => value ?? string.Empty).ToArray(),
                StringComparer.Ordinal));

    /// <summary>
    /// One collection as a comparable string, so a fact fails on a missing parameter or a lost repeat rather
    /// than on reference equality.
    /// </summary>
    private static string Rendered(IQueryCollection query) =>
        string.Join(
            "&",
            query
                .OrderBy(parameter => parameter.Key, StringComparer.Ordinal)
                .Select(parameter => $"{parameter.Key}={string.Join('|', parameter.Value.ToArray())}"));
}
