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
    [Fact]
    public async Task Two_names_differing_only_in_case_are_one_parameter_sent_twice()
    {
        var read = await ReadAsync("""{"limit":1,"LIMIT":2}""");

        read.Violations.ShouldBeEmpty();
        read.Parameters!.Count.ShouldBe(1);
        read.Parameters["limit"].Count.ShouldBe(2);
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
