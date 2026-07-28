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
/// <b>Why the live API rather than the parser.</b> The parser could refuse every payload and this suite would
/// still be worthless if a value reached the engine as statement text somewhere below it. The three claims
/// asserted here are only observable end to end: the response is a policy-consistent 200 or a refusal and
/// <em>never</em> a 500; the table's row count is unchanged; and no response body leaks SQL or the engine's own
/// error vocabulary — which is what turns a refusal into a schema-disclosure channel.
/// </para>
/// </remarks>
public sealed class QueryStringInjectionTests
{
    private static readonly TestApiKey _admin = new("admin-key", ["admin", "authenticated"], ["*:read", "*:write"]);

    /// <summary>
    /// The classic payloads, plus the three byte-level ones an ASCII-only corpus misses: a NUL (which
    /// PostgreSQL cannot represent at all and SQLite silently accepts), a right-to-left override, and a
    /// combining sequence.
    /// </summary>
    private static readonly string[] _payloads =
    [
        "' OR 1=1 --",
        "'; DROP TABLE vehicles; --",
        "%27",
        "\" OR \"\"=\"",
        "1; DELETE FROM vehicles",
        "1' UNION SELECT * FROM sqlite_master --",
        "\0",
        "‮evil",
        "évil",
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
        "unrecognized token", "syntax error", "Exception", "vehicles\"",
    ];

    /// <summary>Every operator the port declares, by its wire spelling — the enum is the source, not a literal list.</summary>
    public static TheoryData<string> EveryOperator() => [.. FilterOperators.WireNames];

    [Theory]
    [MemberData(nameof(EveryOperator))]
    public async Task Injection_through_every_operator_changes_no_row_and_leaks_no_error(string @operator)
    {
        await using var world = await SeededAsync();
        var before = await world.CountRowsAsync("vehicles");

        foreach (var payload in _payloads)
        {
            using var response = await world.SendAsync(
                HttpMethod.Get, $"/api/vehicles?make={Uri.EscapeDataString(Term(@operator, payload))}", _admin);

            var body = await response.ReadTextAsync();
            ShouldBeServedOrRefused(response, body, @operator, payload);
            ShouldLeakNothing(body, @operator, payload);
            (await world.CountRowsAsync("vehicles")).ShouldBe(
                before, $"'{@operator}' with payload {Describe(payload)} changed the table");
        }
    }

    /// <summary>
    /// The same corpus through <c>order</c> and <c>select</c>, where a caller's text would reach SQL as an
    /// <b>identifier</b> rather than as a bind parameter — the one position parameterisation cannot defend, and
    /// therefore the one that must be refused outright.
    /// </summary>
    [Theory]
    [InlineData("order")]
    [InlineData("select")]
    public async Task Injection_through_an_identifier_position_is_refused_and_leaks_no_error(string parameter)
    {
        await using var world = await SeededAsync();
        var before = await world.CountRowsAsync("vehicles");

        foreach (var payload in _payloads)
        {
            using var response = await world.SendAsync(
                HttpMethod.Get, $"/api/vehicles?{parameter}={Uri.EscapeDataString(payload)}", _admin);

            var body = await response.ReadTextAsync();
            response.StatusCode.ShouldBe(
                HttpStatusCode.UnprocessableEntity,
                $"an identifier position must refuse {Describe(payload)}, not compose it — body: {body}");
            ShouldLeakNothing(body, parameter, payload);
            (await world.CountRowsAsync("vehicles")).ShouldBe(before);
        }
    }

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
            $"/api/vehicles?make=eq.{Uri.EscapeDataString("'; DROP TABLE vehicles; --")}",
            _admin);
        using var honest = await world.SendAsync(HttpMethod.Get, "/api/vehicles?make=eq.skoda", _admin);

        (await injected.ReadItemsAsync()).ShouldBeEmpty("no row's make is the payload");
        (await honest.ReadItemsAsync()).Count.ShouldBe(1, "or the empty result above proves nothing");
        (await world.CountRowsAsync("vehicles")).ShouldBe(SeededVehicles);
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

        using var response = await world.SendAsync(HttpMethod.Get, "/api/vehicles?make=eq.sko%00da", _admin);

        response.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);
        (await response.ReadProblemDetailAsync()).ShouldContain("NUL");
    }

    /// <summary>
    /// A refusal must not become a field-existence oracle over HTTP either. Asserted on the whole response body
    /// rather than on a parsed member, so a leak through <c>detail</c>, a <c>violations</c> entry or an
    /// extension all fail it.
    /// </summary>
    [Fact]
    public async Task A_refusal_over_http_never_echoes_the_field_name_the_caller_asked_about()
    {
        await using var world = await SeededAsync();

        using var response = await world.SendAsync(HttpMethod.Get, "/api/vehicles?zqmarkerqz=eq.1", _admin);

        response.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);
        (await response.ReadTextAsync()).ShouldNotContain("zqmarkerqz", Case.Insensitive);
    }

    /// <summary>
    /// Each operator with a payload in the position that operator actually reads: <c>in</c> takes a list and
    /// <c>is</c> takes only null/true/false, so handing either a bare payload would test the wrong refusal.
    /// </summary>
    private static string Term(string @operator, string payload) => @operator switch
    {
        "in" => $"in.({payload})",
        _ => $"{@operator}.{payload}",
    };

    /// <summary>
    /// A payload is either answered or refused — never a 500 — and an answer never carries more rows than the
    /// caller's policy already showed them. The row-count half is the "policy-consistent" claim: a payload that
    /// widened the predicate would come back with rows this caller's own unfiltered read does not contain.
    /// </summary>
    private static void ShouldBeServedOrRefused(
        HttpResponseMessage response, string body, string @operator, string payload)
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
        items.Count.ShouldBeLessThanOrEqualTo(
            SeededVehicles,
            $"'{@operator}' with payload {Describe(payload)} returned more rows than this caller can see at all");
    }

    private const int SeededVehicles = 3;

    private static void ShouldLeakNothing(string body, string parameter, string payload)
    {
        foreach (var leak in _mustNotLeak)
        {
            body.ShouldNotContain(
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
    /// Three vehicles behind one owner, created through the API so every row is one the port itself wrote.
    /// </summary>
    private static async Task<AlvoApiWorld> SeededAsync()
    {
        var world = await AlvoApiWorld.VehicleRegistryAsync([_admin]);
        var owner = await CreateAsync(world, "owners", new JsonObject { ["name"] = "Acme Ltd" });

        foreach (var make in new[] { "skoda", "vw", "audi" })
        {
            await CreateAsync(world, "vehicles", new JsonObject
            {
                ["vin"] = $"VIN-{make}",
                ["plate"] = $"PLATE-{make}",
                ["make"] = make,
                ["model"] = "model",
                ["year"] = 2020,
                ["owner_id"] = owner,
            });
        }

        return world;
    }

    private static async Task<string> CreateAsync(AlvoApiWorld world, string entity, JsonObject body)
    {
        using var created = await world.SendAsync(HttpMethod.Post, $"/api/{entity}", _admin, body: body);
        created.StatusCode.ShouldBe(HttpStatusCode.Created, await created.ReadTextAsync());
        return (await created.ReadJsonObjectAsync())["id"]!.GetValue<Guid>().ToString();
    }
}
