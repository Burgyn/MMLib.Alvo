using MMLib.Alvo.Api.Internal;
using MMLib.Alvo.Data;
using System.Net;
using System.Text.Json.Nodes;

namespace MMLib.Alvo.Api.Tests;

/// <summary>
/// §2.1's named acceptance criterion — "injection cez každý operátor" — run against the <b>live API</b> over a
/// real SQLite database, not against the parser alone.
/// </summary>
/// <remarks>
/// <para>
/// <b>The theory is generated from <see cref="AlvoFilterOperator"/>, never from a hand-written list.</b> A
/// literal list of ten operators is a list that stays at ten when the port grows an eleventh, and the untested
/// operator is always the newest one.
/// </para>
/// <para>
/// <b>The world is tenant-scoped, and that is what makes the row-set claim mean anything.</b> The first version
/// of this suite ran over the vehicle registry, whose rules are role predicates, and asserted
/// <c>items.Count &lt;= 3</c> against a seed of exactly three visible rows — a bound equal to the total, which no
/// injection could ever exceed. Here the caller is keyed to one tenant and rows exist in another, so
/// "policy-consistent" is checkable: a payload that widened the predicate returns a foreign row and fails the
/// theory by <em>identity</em>, not by count.
/// </para>
/// <para>
/// <b>Why the live API rather than the parser.</b> The parser could refuse every payload and this suite would
/// still be worthless if a value reached the engine as statement text somewhere below it. The claims asserted
/// here are only observable end to end: the response is a policy-consistent 200 or a refusal and <em>never</em> a
/// 500; the table's row count is unchanged; and no response body leaks SQL or the engine's own error
/// vocabulary — which is what turns a refusal into a schema-disclosure channel.
/// </para>
/// </remarks>
public sealed class QueryStringInjectionTests
{
    private const string Table = "notes";

    private const string Field = "title";

    private static readonly Guid _ourTenant = Guid.NewGuid();

    private static readonly Guid _theirTenant = Guid.NewGuid();

    private static readonly TestApiKey _caller =
        new("tenant-ours", ["authenticated"], ["notes:read", "notes:write"], _ourTenant);

    private static readonly TestApiKey _other =
        new("tenant-theirs", ["authenticated"], ["notes:read", "notes:write"], _theirTenant);

    /// <summary>Titles seeded in the caller's own tenant, and in the one they may never read.</summary>
    private static readonly string[] _ourTitles = ["alpha", "beta", "gamma"];

    private static readonly string[] _theirTitles = ["delta", "epsilon"];

    private static int SeededRows => _ourTitles.Length + _theirTitles.Length;

    /// <summary>
    /// The classic payloads, plus the three byte-level ones an ASCII-only corpus misses: a NUL (which PostgreSQL
    /// cannot represent at all and SQLite silently accepts), a right-to-left override, and a combining sequence.
    /// </summary>
    private static readonly string[] _payloads =
    [
        "' OR 1=1 --",
        $"'; DROP TABLE {Table}; --",
        "%27",
        "\" OR \"\"=\"",
        $"1; DELETE FROM {Table}",
        "1' UNION SELECT * FROM sqlite_master --",
        "\0",
        "‮evil",
        "évil",
        "%' OR '1'='1",
    ];

    /// <summary>
    /// Words a response must never carry. The SQL keywords are the brief's own two; the rest is the engine's
    /// error vocabulary, which is how a raw provider exception announces itself — <c>SQLite Error 1</c>,
    /// <c>no such column</c>, and the type names EF puts in a materialization failure.
    /// </summary>
    private static readonly string[] _mustNotLeak =
    [
        "SELECT", "WHERE", "FROM", "sqlite", "SQLite", "no such column", "no such table",
        "unrecognized token", "syntax error", "Exception", QuotedIdentifier,
    ];

    /// <summary>
    /// The table's name as a <b>quoted SQL identifier</b>, which is how a composed statement spells it.
    /// </summary>
    /// <remarks>
    /// The one entry here that catches an identifier escaping into a response — the injection symptom most likely
    /// to be silent, since a leaked identifier carries no SQL keyword and no engine error with it. It is separated
    /// out because it was <em>lost once</em>: it read <c>vehicles"</c>, and re-seeding this suite onto a
    /// tenant-scoped entity renamed the table without renaming the check, so it silently stopped matching anything.
    /// Deriving it from <see cref="Table"/> makes that unrepresentable.
    /// </remarks>
    private const string QuotedIdentifier = Table + "\"";

    /// <summary>Every operator the port declares, by its wire spelling — the enum is the source, not a literal list.</summary>
    public static TheoryData<string> EveryOperator() => [.. FilterOperators.WireNames];

    [Theory]
    [MemberData(nameof(EveryOperator))]
    public async Task Injection_through_every_operator_changes_no_row_and_leaks_no_error(string @operator)
    {
        await using var world = await SeededAsync();
        var visible = await VisibleTitlesAsync(world);

        foreach (var payload in _payloads)
        {
            using var response = await world.SendAsync(
                HttpMethod.Get, $"/api/{Table}?{Field}={Uri.EscapeDataString(Term(@operator, payload))}", _caller);

            var body = await response.ReadTextAsync();
            ShouldBeServedOrRefused(response, body, @operator, payload, visible);
            ShouldLeakNothing(body, @operator, payload);
            (await world.CountRowsAsync(Table)).ShouldBe(
                SeededRows, $"'{@operator}' with payload {Describe(payload)} changed the table");
        }
    }

    /// <summary>
    /// The same corpus through every position a caller's text would reach SQL as an <b>identifier</b> rather than
    /// as a bind parameter — the one position parameterisation cannot defend, and therefore the one that must be
    /// refused outright.
    /// </summary>
    /// <remarks>
    /// <b>The filter's own key is such a position, and it is the one that was missing.</b> In PostgREST's grammar a
    /// non-reserved parameter name <em>is</em> a field name, so <c>?&lt;payload&gt;=eq.1</c> puts the payload where
    /// a column identifier goes — and it is the only one of these three that produces an <c>unavailable-field</c>
    /// refusal, which is the refusal whose message could echo an identifier back. Without this row the
    /// quoted-identifier entry in <see cref="_mustNotLeak"/> screened no body that could ever have carried one:
    /// measured by planting the table name in that message and watching the whole suite stay green.
    /// </remarks>
    [Theory]
    [InlineData("order")]
    [InlineData("select")]
    [InlineData(FieldNamePosition)]
    [InlineData(SelectAliasPosition)]
    [InlineData(SelectSourcePosition)]
    public async Task Injection_through_an_identifier_position_is_refused_and_leaks_no_error(string parameter)
    {
        await using var world = await SeededAsync();

        foreach (var payload in _payloads)
        {
            var query = parameter switch
            {
                FieldNamePosition => $"{Uri.EscapeDataString(payload)}=eq.1",
                SelectAliasPosition => $"select={Uri.EscapeDataString(payload)}:title",
                SelectSourcePosition => $"select=label:{Uri.EscapeDataString(payload)}",
                _ => $"{parameter}={Uri.EscapeDataString(payload)}",
            };
            using var response = await world.SendAsync(HttpMethod.Get, $"/api/{Table}?{query}", _caller);

            var body = await response.ReadTextAsync();
            response.StatusCode.ShouldBe(
                HttpStatusCode.UnprocessableEntity,
                $"an identifier position must refuse {Describe(payload)}, not compose it — body: {body}");
            ShouldLeakNothing(body, parameter, payload);
            (await world.CountRowsAsync(Table)).ShouldBe(SeededRows);
        }
    }

    /// <summary>
    /// The theory row that sends the payload as the parameter <em>name</em> — a field position, since every
    /// non-reserved key in this grammar names a field.
    /// </summary>
    private const string FieldNamePosition = "<field>";

    /// <summary>
    /// The alias half of a projection entry — the one position in this grammar where a caller's bytes reach a
    /// <b>response key</b> rather than an identifier.
    /// </summary>
    /// <remarks>
    /// It never reaches SQL: only a projection's <em>source</em> crosses the port, so this row is not about
    /// injection into a statement. It is here because the suite's own claim is "every position a caller's
    /// text reaches", and an alias is a new such position — and because the interesting failure is the
    /// reverse of the others: not an identifier escaping into a response, but caller bytes being accepted
    /// <em>as</em> a response key. Every payload here is refused by the alias grammar, so nothing is echoed.
    /// </remarks>
    private const string SelectAliasPosition = "<select-alias>";

    /// <summary>The source half of a projection entry — an identifier position like the others.</summary>
    private const string SelectSourcePosition = "<select-source>";

    /// <summary>
    /// The discriminating case, spelled out on its own: the payload that would drop the table is answered as an
    /// ordinary comparison against no row, and the table is still there afterwards. The seeded control is what
    /// makes "no rows came back" mean something — without it the fact would pass against a server that returned
    /// nothing for every request.
    /// </summary>
    [Fact]
    public async Task A_drop_table_payload_is_compared_as_a_value_and_the_table_survives()
    {
        await using var world = await SeededAsync();

        using var injected = await world.SendAsync(
            HttpMethod.Get,
            $"/api/{Table}?{Field}=eq.{Uri.EscapeDataString($"'; DROP TABLE {Table}; --")}",
            _caller);
        using var honest = await world.SendAsync(HttpMethod.Get, $"/api/{Table}?{Field}=eq.alpha", _caller);

        (await injected.ReadItemsAsync()).ShouldBeEmpty("no row's title is the payload");
        (await honest.ReadFieldAsync(Field)).ShouldBe(["alpha"], "or the empty result above proves nothing");
        (await world.CountRowsAsync(Table)).ShouldBe(SeededRows);
    }

    /// <summary>
    /// The row-set claim in its own right: a filter another tenant's row would satisfy still returns nothing,
    /// because a caller's filter is applied <em>in addition to</em> the policy predicate and can only narrow.
    /// </summary>
    /// <remarks>
    /// The control matters twice over — the same filter, run by the tenant that owns the row, must return it, or
    /// this fact would pass against a server that answered every filtered read with nothing.
    /// </remarks>
    [Fact]
    public async Task A_filter_matching_another_tenants_row_returns_nothing_to_this_caller()
    {
        await using var world = await SeededAsync();

        using var ours = await world.SendAsync(HttpMethod.Get, $"/api/{Table}?{Field}=eq.delta", _caller);
        using var theirs = await world.SendAsync(HttpMethod.Get, $"/api/{Table}?{Field}=eq.delta", _other);

        (await ours.ReadItemsAsync()).ShouldBeEmpty("'delta' belongs to the other tenant");
        (await theirs.ReadFieldAsync(Field)).ShouldBe(["delta"], "or the empty result above proves nothing");
    }

    /// <summary>
    /// A NUL is refused rather than passed on, because the two engines disagree about it: PostgreSQL's UTF8 has
    /// no representation for one and Npgsql raises a raw provider error, while SQLite binds it and quietly
    /// answers. One caller-supplied value must not be a 500 on one engine and an answer on the other.
    /// </summary>
    [Fact]
    public async Task A_nul_in_a_filter_value_is_refused_rather_than_answered_differently_per_engine()
    {
        await using var world = await SeededAsync();

        using var response = await world.SendAsync(HttpMethod.Get, $"/api/{Table}?{Field}=eq.al%00pha", _caller);

        response.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);
        (await response.ReadProblemDetailAsync()).ShouldContain("NUL");
    }

    /// <summary>
    /// A refusal must not become a field-existence oracle over HTTP either. Asserted on the whole response body
    /// rather than on a parsed member, so a leak through <c>detail</c>, a <c>violations</c> entry or an extension
    /// all fail it.
    /// </summary>
    [Fact]
    public async Task A_refusal_over_http_never_echoes_the_field_name_the_caller_asked_about()
    {
        await using var world = await SeededAsync();

        using var response = await world.SendAsync(HttpMethod.Get, $"/api/{Table}?zqmarkerqz=eq.1", _caller);

        response.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);
        (await response.ReadTextAsync()).ShouldNotContain("zqmarkerqz", Case.Insensitive);
    }

    /// <summary>
    /// The same screen on the body-shaped read: a refusal never echoes the parameter name the caller asked
    /// about, whichever side of the request it arrived on.
    /// </summary>
    /// <remarks>
    /// The unit facts assert the <em>pointer</em> a refusal carries; this asserts the whole response, which
    /// is the only form that catches a name reaching some other member — a <c>detail</c>, a fix suggestion,
    /// a header. The GET twin is <see cref="A_refusal_over_http_never_echoes_the_field_name_the_caller_asked_about"/>,
    /// and a second route reaching one read is a second place the property has to hold.
    /// </remarks>
    [Fact]
    public async Task A_refusal_over_the_query_body_never_echoes_the_field_name_the_caller_asked_about()
    {
        await using var world = await SeededAsync();

        using var response = await world.SendAsync(
            HttpMethod.Post,
            $"/api/{Table}/query",
            _caller,
            body: new JsonObject { ["zqmarkerqz"] = "eq.1" });

        response.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);
        (await response.ReadTextAsync()).ShouldNotContain("zqmarkerqz", Case.Insensitive);
    }

    /// <summary>
    /// Each operator with a payload in the position that operator actually reads: <c>in</c> takes a list and
    /// <c>is</c> takes only null/true/false, so handing either a bare payload would test the wrong refusal.
    /// </summary>
    private static string Term(string @operator, string payload) =>
        @operator == "in" ? $"in.({payload})" : $"{@operator}.{payload}";

    /// <summary>
    /// A payload is either answered or refused — never a 500 — and an answer carries <b>only rows this caller's
    /// policy already shows them</b>, identified by title rather than counted.
    /// </summary>
    /// <remarks>
    /// The subset check by identity is the property §2.1 asks for. A count bound cannot express it: with the seed
    /// equal to the visible set, <c>Count &lt;= visible</c> is satisfied by any answer at all — including one
    /// carrying the other tenant's rows in place of this caller's.
    /// </remarks>
    private static void ShouldBeServedOrRefused(
        HttpResponseMessage response,
        string body,
        string @operator,
        string payload,
        IReadOnlyList<string> visible)
    {
        ((int)response.StatusCode).ShouldBeOneOf(
            [(int)HttpStatusCode.OK, (int)HttpStatusCode.UnprocessableEntity],
            $"'{@operator}' with payload {Describe(payload)} must be answered or refused, never a 500 — "
            + $"got {(int)response.StatusCode} with body: {body}");

        if (response.StatusCode != HttpStatusCode.OK)
        {
            return;
        }

        var items = (JsonNode.Parse(body) as JsonObject)?["items"] as JsonArray
            ?? throw new InvalidOperationException($"a 200 must carry an items envelope, but the body was: {body}");

        foreach (var title in items.Select(row => row![Field]!.GetValue<string>()))
        {
            visible.ShouldContain(
                title,
                $"'{@operator}' with payload {Describe(payload)} returned a row this caller's policy withholds");
        }
    }

    /// <summary>
    /// Screens a response body for SQL, engine error vocabulary and the table's quoted identifier.
    /// </summary>
    /// <remarks>
    /// <b>The body is JSON-unescaped first, and that is load-bearing rather than tidiness.</b> A quoted SQL
    /// identifier is <c>"notes"</c>, and <c>System.Text.Json</c> writes an interior quote as <c>\"</c> — so
    /// screening the raw body for <c>notes"</c> can <em>never</em> match. Measured: planting the quoted table name
    /// in a refusal message left the whole suite green. The entry was reported as having been lost when this suite
    /// re-seeded onto a tenant-scoped entity; it was worse than that — in its original <c>vehicles"</c> form it had
    /// never been able to fire either. Unescaping is what turns the check from decoration into a check.
    /// </remarks>
    private static void ShouldLeakNothing(string body, string parameter, string payload)
    {
        var unescaped = body.Replace("\\\"", "\"", StringComparison.Ordinal);

        foreach (var leak in _mustNotLeak)
        {
            unescaped.ShouldNotContain(
                leak,
                Case.Sensitive,
                $"'{parameter}' with payload {Describe(payload)} leaked '{leak}' into the response");
        }
    }

    /// <summary>
    /// A payload rendered so an assertion message stays readable — and so a NUL or an RTL override in a failure
    /// message cannot reorder the test log it is written into.
    /// </summary>
    private static string Describe(string payload) =>
        string.Concat(payload.Select(character =>
            char.IsControl(character) || character > '~'
                ? $"\\u{(int)character:x4}"
                : character.ToString()));

    /// <summary>
    /// Every title this caller can see at all, read through an unfiltered list — so the subset check compares
    /// against the policy's own answer rather than against what the seed intended.
    /// </summary>
    private static async Task<IReadOnlyList<string>> VisibleTitlesAsync(AlvoApiWorld world)
    {
        using var response = await world.SendAsync(HttpMethod.Get, $"/api/{Table}", _caller);
        var titles = await response.ReadFieldAsync(Field);

        titles.ShouldBe(_ourTitles, ignoreOrder: true, "the caller must see their own tenant's rows and no others");
        return [.. titles.Select(title => title!)];
    }

    /// <summary>
    /// Rows in the caller's tenant <b>and</b> in one they may never read, each created through the API by the key
    /// that owns it, so every row is one the port itself wrote under the tenant it belongs to.
    /// </summary>
    private static async Task<AlvoApiWorld> SeededAsync()
    {
        var world = await AlvoApiWorld.TenantNotesAsync([_caller, _other]);
        await SeedAsync(world, _caller, _ourTitles);
        await SeedAsync(world, _other, _theirTitles);
        return world;
    }

    private static async Task SeedAsync(AlvoApiWorld world, TestApiKey key, IEnumerable<string> titles)
    {
        foreach (var title in titles)
        {
            var body = new JsonObject { [Field] = title, ["tenant_id"] = key.Tenant!.Value.ToString() };
            using var created = await world.SendAsync(HttpMethod.Post, $"/api/{Table}", key, body: body);
            created.StatusCode.ShouldBe(HttpStatusCode.Created, await created.ReadTextAsync());
        }
    }
}
