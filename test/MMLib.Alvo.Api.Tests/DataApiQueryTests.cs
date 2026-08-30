using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using MMLib.Alvo.Api.Internal;
using MMLib.Alvo.Data;
using MMLib.Alvo.Descriptor;
using MMLib.Alvo.Migrations;
using MMLib.Alvo.Schema;
using System.Net;
using System.Text.Json.Nodes;

namespace MMLib.Alvo.Api.Tests;

/// <summary>
/// The query surface end to end: that the parse is actually <b>wired into</b> the list endpoint, that a refusal
/// is a 422 carrying its violations, and that the filter really narrows the rows a real store returns.
/// </summary>
/// <remarks>
/// <c>QueryStringParserTests</c> proves the grammar; none of it is worth anything if <c>MapList</c> still hands
/// the port an unfiltered query. Every fact here is written so that deleting the parse call — or dropping the
/// projection, or the page size — fails it.
/// </remarks>
public sealed class DataApiQueryTests
{
    private static readonly TestApiKey _admin = new("admin-key", ["admin", "authenticated"], ["*:read", "*:write"]);

    [Fact]
    public async Task A_group_filter_narrows_to_the_union_of_its_terms()
    {
        await using var world = await SeededAsync();

        using var response = await world.SendAsync(
            HttpMethod.Get, "/api/vehicles?or=(make.eq.vw,make.eq.audi)&order=make", _admin);

        (await response.ReadFieldAsync("make")).ShouldBe(["audi", "vw"]);
    }

    [Fact]
    public async Task A_negated_filter_excludes_what_the_term_would_have_matched()
    {
        await using var world = await SeededAsync();

        using var response = await world.SendAsync(
            HttpMethod.Get, "/api/vehicles?not.make=eq.vw&order=make", _admin);

        (await response.ReadFieldAsync("make")).ShouldBe(["audi", "skoda"]);
    }

    /// <summary>
    /// A projection keeps exactly the named fields. The negative half is what makes it a projection rather than
    /// a hint: a row must not carry <c>vin</c> merely because the store returned it.
    /// </summary>
    [Fact]
    public async Task A_select_parameter_projects_only_the_named_fields()
    {
        await using var world = await SeededAsync();

        using var response = await world.SendAsync(HttpMethod.Get, "/api/vehicles?select=make,year", _admin);

        var rows = await response.ReadItemsAsync();
        rows.Count.ShouldBe(3);
        foreach (var row in rows)
        {
            row.Select(pair => pair.Key).ShouldBe(["make", "year"]);
        }
    }

    /// <summary>
    /// A limit is honoured and a cursor continues from it, so paging over HTTP is the keyset paging the port
    /// implements rather than a page the API sliced for itself.
    /// </summary>
    [Fact]
    public async Task A_limit_is_honoured_and_the_cursor_reads_the_next_page()
    {
        await using var world = await SeededAsync();

        using var first = await world.SendAsync(HttpMethod.Get, "/api/vehicles?order=make&limit=2", _admin);
        var body = await first.ReadJsonObjectAsync();
        var cursor = body["next"]!.GetValue<string>();

        using var second = await world.SendAsync(
            HttpMethod.Get, $"/api/vehicles?order=make&limit=2&after={Uri.EscapeDataString(cursor)}", _admin);

        (await first.ReadFieldAsync("make")).ShouldBe(["audi", "skoda"]);
        (await second.ReadFieldAsync("make")).ShouldBe(["vw"]);
    }

    /// <summary>
    /// The configured maximum is enforced by the endpoint, not merely by the parser in isolation, and it is
    /// asserted at the boundary: a fact at 100 000 would pass against an endpoint whose ceiling was 1 000.
    /// </summary>
    [Fact]
    public async Task A_page_size_past_the_configured_maximum_is_refused_by_the_endpoint()
    {
        await using var world = await AlvoApiWorld.VehicleRegistryAsync(
            [_admin],
            new AlvoApiWorldSetup(ConfigureApi: options =>
            {
                options.MaxPageSize = 5;
                options.DefaultPageSize = 5;
            }));

        using var atMaximum = await world.SendAsync(HttpMethod.Get, "/api/vehicles?limit=5", _admin);
        using var past = await world.SendAsync(HttpMethod.Get, "/api/vehicles?limit=6", _admin);

        atMaximum.StatusCode.ShouldBe(HttpStatusCode.OK);
        past.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);
        (await past.ReadProblemDetailAsync()).ShouldContain("page size");
    }

    /// <summary>
    /// The default page size is applied to a request that names none — which is why there is no way to ask
    /// this surface for an unpaged read, and why a sort key that could not be paged could not be used at all.
    /// </summary>
    [Fact]
    public async Task A_request_naming_no_page_size_gets_the_configured_default()
    {
        await using var world = await AlvoApiWorld.VehicleRegistryAsync(
            [_admin], new AlvoApiWorldSetup(ConfigureApi: options => options.DefaultPageSize = 2));
        await SeedAsync(world);

        using var response = await world.SendAsync(HttpMethod.Get, "/api/vehicles?order=make", _admin);

        (await response.ReadFieldAsync("make")).ShouldBe(["audi", "skoda"]);
    }

    /// <summary>
    /// A refusal is a 422 carrying the machine-readable violations §0 principle 4 asks for — a code and a fix
    /// per problem, so an agent can repair the request instead of guessing.
    /// </summary>
    [Fact]
    public async Task A_refused_query_string_is_a_422_carrying_its_violations()
    {
        await using var world = await SeededAsync();

        using var response = await world.SendAsync(HttpMethod.Get, "/api/vehicles?limit=0", _admin);

        response.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);
        var violations = (await response.ReadJsonObjectAsync())["violations"]!.AsArray();
        var only = violations.ShouldHaveSingleItem()!.AsObject();
        only["pointer"]!.GetValue<string>().ShouldBe("limit");
        only["code"]!.GetValue<string>().ShouldBe("invalid-page-size");
        only["fixSuggestion"]!.GetValue<string>().ShouldNotBeNullOrWhiteSpace();
    }

    /// <summary>
    /// A filter past the port's term limit is refused over HTTP with the code that names the caller's fix, and
    /// with <b>only</b> that code.
    /// </summary>
    /// <remarks>
    /// The live leg of the 4a defect: 256 filter parameters used to come back carrying
    /// <c>filter-beyond-port-limits</c> — documented as unreachable — beside the <c>filter-too-wide</c> the
    /// caller needed. It is asserted here as well as at the parser because
    /// <c>AlvoApiWorld</c>'s response screen only sees bodies the suite actually asks for, and nothing else in
    /// the suite sends a filter this wide.
    /// </remarks>
    [Fact]
    public async Task A_filter_past_the_term_limit_is_refused_with_only_the_callers_own_code()
    {
        await using var world = await SeededAsync();
        var tooWide = string.Join("&", Enumerable.Range(0, AlvoFilter.MaxTerms).Select(_ => "year=gte.1"));

        using var response = await world.SendAsync(HttpMethod.Get, $"/api/vehicles?{tooWide}", _admin);

        response.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);
        var codes = (await response.ReadJsonObjectAsync())["violations"]!.AsArray()
            .Select(violation => violation!["code"]!.GetValue<string>())
            .Distinct(StringComparer.Ordinal);
        codes.ShouldBe(["filter-too-wide"]);
    }

    /// <summary>
    /// A port guard's refusal reaches the caller in the port's own words with the .NET argument machinery
    /// stripped off, and with a fix suggestion.
    /// </summary>
    /// <remarks>
    /// The live leg of a shipped defect: <c>(Parameter 'query')</c> reached a response body, because the
    /// method meant to strip it cut at a newline the suffix is not behind — <c>AlvoApiWorld</c> now screens
    /// every response for it. Asserted over the conflicting-window guard because that is the port refusal
    /// this surface can still provoke; it used to be asserted over the nullable-sort-key guard, which F4
    /// deleted along with the refusal itself.
    /// </remarks>
    [Fact]
    public async Task A_port_guards_refusal_reaches_the_caller_in_the_ports_own_words()
    {
        await using var world = await SeededAsync();

        using var response = await world.SendAsync(
            HttpMethod.Get, "/api/vehicles?after=abc&offset=1", _admin);

        response.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);
        var violation = (await response.ReadJsonObjectAsync())["violations"]!.AsArray()
            .ShouldHaveSingleItem()!.AsObject();
        violation["code"]!.GetValue<string>().ShouldBe("conflicting-paging");
        violation["message"]!.GetValue<string>().ShouldNotContain("(Parameter '");
        violation["fixSuggestion"]!.GetValue<string>().ShouldNotBeNullOrWhiteSpace();
    }

    /// <summary>
    /// <c>?order=&lt;nullable field&gt;</c> is the single most obvious thing an agent asks a generated list
    /// for, and until F4 it was a 422 — every HTTP list is paged, and the port refused a paged read over a
    /// nullable sort key. Both null placements are asserted, because their being unobservable was the second
    /// half of the same defect: <c>nullsfirst</c>/<c>nullslast</c> parsed, validated and could not change an
    /// answer.
    /// </summary>
    [Theory]
    [InlineData("nullsfirst", "")]
    [InlineData("nullslast", "red")]
    public async Task A_list_sorted_by_a_nullable_field_answers_and_honours_the_null_placement(
        string placement, string expectedFirstColor)
    {
        await using var world = await SeededAsync();

        using var response = await world.SendAsync(
            HttpMethod.Get, $"/api/vehicles?order=color.{placement}", _admin);

        response.StatusCode.ShouldBe(HttpStatusCode.OK, await response.ReadTextAsync());
        var items = (await response.ReadJsonObjectAsync())["items"]!.AsArray();
        items.Count.ShouldBe(_fleet.Length);
        (items[0]!["color"]?.GetValue<string>() ?? string.Empty).ShouldBe(expectedFirstColor);
    }

    /// <summary>
    /// <c>Prefer: count=exact</c> fills the envelope's <c>count</c> with the size of the matching set — not
    /// of the page — and RFC 7240's <c>Preference-Applied</c> says so.
    /// </summary>
    [Fact]
    public async Task A_count_preference_fills_the_envelope_and_is_reported_as_applied()
    {
        await using var world = await SeededAsync();

        using var response = await world.SendAsync(
            HttpMethod.Get, "/api/vehicles?order=make&limit=2", _admin,
            headers: new Dictionary<string, string> { ["Prefer"] = "count=exact" });

        var body = await response.ReadJsonObjectAsync();
        body["items"]!.AsArray().Count.ShouldBe(2);
        body["count"]!.GetValue<long>().ShouldBe(3);
        response.Headers.GetValues("Preference-Applied").ShouldBe(["count=exact"]);
    }

    /// <summary>
    /// <b>Opt-in, and this is the fact that makes it one over HTTP.</b> A request that sends no preference
    /// gets <c>count: null</c> — present, because the envelope's members are a statement about the bytes —
    /// and no <c>Preference-Applied</c> at all.
    /// </summary>
    [Fact]
    public async Task A_request_that_asks_for_no_count_gets_a_null_one_and_no_applied_header()
    {
        await using var world = await SeededAsync();

        using var response = await world.SendAsync(HttpMethod.Get, "/api/vehicles?order=make", _admin);

        var body = await response.ReadJsonObjectAsync();
        body.ContainsKey("count").ShouldBeTrue("the envelope's members are a statement about the bytes");
        body["count"].ShouldBeNull();
        response.Headers.Contains("Preference-Applied").ShouldBeFalse();
    }

    /// <summary>
    /// <c>planned</c> and <c>estimated</c> degrade to an exact count — a planner estimate exists on one
    /// supported engine and not the other — and the caller is <em>told</em>, which is the whole reason
    /// <c>Preference-Applied</c> is sent.
    /// </summary>
    [Theory]
    [InlineData("count=planned")]
    [InlineData("count=estimated")]
    public async Task An_estimate_preference_degrades_to_an_exact_count_and_says_so(string preference)
    {
        await using var world = await SeededAsync();

        using var response = await world.SendAsync(
            HttpMethod.Get, "/api/vehicles", _admin,
            headers: new Dictionary<string, string> { ["Prefer"] = preference });

        (await response.ReadJsonObjectAsync())["count"]!.GetValue<long>().ShouldBe(3);
        response.Headers.GetValues("Preference-Applied").ShouldBe(["count=exact"]);
    }

    /// <summary>
    /// <b>A preference this server does not recognise is ignored, not refused</b> — RFC 7240 §2 requires it,
    /// and the absence of <c>Preference-Applied</c> is how the standard reports it. The one deliberate
    /// departure from this API's "refuse, never ignore" rule, so it is asserted end to end rather than only
    /// at the parser.
    /// </summary>
    [Theory]
    [InlineData("count=exakt")]
    [InlineData("respond-async")]
    public async Task An_unrecognised_preference_is_ignored_rather_than_refused(string preference)
    {
        await using var world = await SeededAsync();

        using var response = await world.SendAsync(
            HttpMethod.Get, "/api/vehicles", _admin,
            headers: new Dictionary<string, string> { ["Prefer"] = preference });

        response.StatusCode.ShouldBe(HttpStatusCode.OK, await response.ReadTextAsync());
        (await response.ReadJsonObjectAsync())["count"].ShouldBeNull();
        response.Headers.Contains("Preference-Applied").ShouldBeFalse();
    }

    /// <summary>
    /// The count is over the caller's <b>filtered</b> set, not the table: a filter that halves the rows
    /// halves the count. Without this, a count taken over everything visible would pass the facts above.
    /// </summary>
    [Fact]
    public async Task A_count_is_narrowed_by_the_requests_own_filter()
    {
        await using var world = await SeededAsync();

        using var response = await world.SendAsync(
            HttpMethod.Get, "/api/vehicles?make=eq.vw", _admin,
            headers: new Dictionary<string, string> { ["Prefer"] = "count=exact" });

        (await response.ReadJsonObjectAsync())["count"]!.GetValue<long>().ShouldBe(1);
    }

    /// <summary>
    /// An unrecognized parameter is refused rather than ignored: an ignored <c>oder=name</c> answers 200 with
    /// unsorted data and the agent that sent it has no way to notice.
    /// </summary>
    [Fact]
    public async Task An_unrecognized_query_parameter_is_refused_rather_than_ignored()
    {
        await using var world = await SeededAsync();

        using var mistyped = await world.SendAsync(HttpMethod.Get, "/api/vehicles?oder=make", _admin);
        using var correct = await world.SendAsync(HttpMethod.Get, "/api/vehicles?order=make", _admin);

        mistyped.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);
        correct.StatusCode.ShouldBe(HttpStatusCode.OK, "or the refusal above could be a blanket refusal of every list");
    }

    /// <summary>
    /// A query string is parsed <b>before</b> any row is read, so a malformed one costs the store nothing.
    /// </summary>
    [Fact]
    public async Task A_refused_query_string_reaches_no_statement()
    {
        await using var world = await SeededAsync();
        world.ClearStatements();

        using var response = await world.SendAsync(HttpMethod.Get, "/api/vehicles?nosuchfield=eq.1", _admin);

        response.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);
        world.Statements.ShouldBeEmpty();
    }

    /// <summary>
    /// A descriptor whose field shadows a reserved query parameter never gets as far as being served: it is
    /// refused at <b>apply</b>, naming the field and the fix, so no host ever exposes an entity with a field it
    /// cannot address.
    /// </summary>
    /// <remarks>
    /// The refusal moved here from route mapping. The descriptor is wrong whether or not the Data API is mounted,
    /// so an embedded host that never calls <c>MapAlvoDataApi</c> got no refusal at all — and a per-request
    /// resolution would have made a descriptor problem look like a caller's. The mapping-time guard remains as the
    /// belt for a schema that reaches routing without passing descriptor validation (an earlier build's applied
    /// descriptor, or F7's dynamic-entity registry); it is proved by
    /// <c>QueryStringParserTests.An_entity_declaring_a_field_that_shadows_a_reserved_parameter_is_refused</c>.
    /// </remarks>
    [Fact]
    public async Task An_entity_shadowing_a_reserved_query_parameter_never_reaches_a_route()
    {
        var refusal = await Should.ThrowAsync<DescriptorValidationException>(
            () => AlvoApiWorld.FromDescriptorAsync("reserved-field.alvo.json"));

        refusal.Message.ShouldContain("limit");
    }

    /// <summary>
    /// The reserved-name belt, exercised on the <b>only</b> path it exists for: an applied schema that reaches
    /// route generation without ever having passed descriptor validation.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A descriptor declaring such a field is refused at apply, so no descriptor-driven host can reach here — which
    /// is exactly why this guard was pinned by nothing: deleting it left the whole suite green. It is kept for the
    /// schemas that skip the validator, which are real and will grow: one applied by a build predating the
    /// apply-time refusal, and F7's dynamic-entity registry, which never produces a descriptor at all.
    /// </para>
    /// <para>
    /// The registry is substituted rather than the descriptor edited, because substituting it <em>is</em> the
    /// bypass: <c>EntityRouteCatalog</c> reads the applied schema from <c>ISchemaRegistry</c>, so a registry
    /// answering with a hostile schema is precisely the shape those two paths take.
    /// </para>
    /// <para>
    /// <b>The refusal is raised at route materialisation, not by the <c>MapAlvoDataApi</c> call — which is the
    /// mechanism this fact had to change and the assertion it deliberately kept.</b> Route literals are now read
    /// when the endpoint table is first enumerated, so a check made at the map call would have inspected an
    /// unprimed registry and passed vacuously in every real host. The start-time refusal for a
    /// <em>descriptor</em> is boot stage 0's, over the descriptor's own mapped schema
    /// (<c>DescriptorBootPlanTests</c>); for a substituted registry, first enumeration is the earliest anything
    /// can see the hostile schema at all.
    /// </para>
    /// <para>
    /// <b>And the refusal is <em>recorded</em>, not thrown, which is the second thing this fact had to change.</b>
    /// Throwing out of an <c>EndpointDataSource</c> broke the composite the framework matches every request
    /// through — <c>/health/live</c> included — so a hostile registry got the container killed and restart-looped.
    /// What is asserted instead is both halves of the fail-closed answer: the table materialises <em>empty</em>, so
    /// no route exists, and <c>AlvoBootState</c> carries the reason with the phase Failed, so readiness reports it
    /// and an orchestrator drains the pod instead of restarting it.
    /// </para>
    /// </remarks>
    [Fact]
    public void A_schema_reaching_route_materialisation_without_validation_is_still_refused_for_a_reserved_field_name()
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.UseTestServer();
        builder.Services.AddAlvo(alvo => alvo
            .UseSqlite($"Data Source=alvo-belt-{Guid.NewGuid():N};Mode=Memory;Cache=Shared")
            .FromDescriptor(Path.Combine(RepositoryRoot.Find(), "examples", "vehicle-registry", "vehicles.alvo.json"))
            .AddDataApi());
        builder.Services.AddSingleton<ISchemaRegistry>(new FixedSchemaRegistry(new SchemaModel([
            new EntitySchema
            {
                Name = "widgets",
                Fields =
                [
                    new FieldSchema { Name = "id", Type = Schema.FieldType.Uuid },
                    new FieldSchema { Name = ReservedQueryKeys.Limit, Type = Schema.FieldType.Integer },
                ],
            },
        ])));

        using var app = builder.Build();
        app.MapAlvoDataApi();

        MaterialiseRoutes(app).ShouldBeEmpty("a schema the belt refused must produce no route at all");

        var state = app.Services.GetRequiredService<AlvoBootState>();
        state.Phase.ShouldBe(AlvoBootPhase.Failed);

        var refusal = state.Failure.ShouldNotBeNull();
        refusal.ShouldContain("widgets");
        refusal.ShouldContain(ReservedQueryKeys.Limit);
        refusal.ShouldContain("Rename the field");
    }

    /// <summary>
    /// Builds the mapped endpoint table, which is what the first request to arrive does, and returns what it
    /// produced.
    /// </summary>
    /// <remarks>
    /// Read off <see cref="IEndpointRouteBuilder.DataSources"/> rather than by sending a request, because a
    /// request cannot tell the two outcomes apart: a refused schema and a schema with no such entity both answer
    /// 404 from routing. The endpoint list can.
    /// </remarks>
    /// <param name="app">The application whose mapped routes to materialise.</param>
    private static IReadOnlyList<Endpoint> MaterialiseRoutes(WebApplication app) =>
        [.. ((IEndpointRouteBuilder)app).DataSources.SelectMany(source => source.Endpoints)];

    /// <summary>An applied schema handed straight to route generation, with no descriptor behind it.</summary>
    /// <param name="schema">The schema to answer with.</param>
    private sealed class FixedSchemaRegistry(SchemaModel schema) : ISchemaRegistry
    {
        public SchemaModel GetSchema() => schema;
    }

    private static async Task<AlvoApiWorld> SeededAsync()
    {
        var world = await AlvoApiWorld.VehicleRegistryAsync([_admin]);
        await SeedAsync(world);
        return world;
    }

    /// <summary>
    /// Three vehicles behind one owner, created through the API so every row is one the port wrote.
    /// </summary>
    /// <remarks>
    /// The years happen to disagree under lexical and numeric ordering (<c>300</c>, <c>1999</c> and
    /// <c>2020</c> all sort below <c>500</c> as text). That property mattered to a numeric-filter fact that
    /// used to live in this file; it now lives in <see cref="DataApiEngineTests"/>, over its own seed, so
    /// nothing left here depends on it.
    /// </remarks>
    /// <remarks>
    /// One row carries a <c>color</c> and two leave it unset, which is what makes a sort by that nullable
    /// field observable in both placements rather than merely legal.
    /// </remarks>
    private static readonly (string Make, int Year, string? Color)[] _fleet =
        [("skoda", 300, null), ("vw", 1999, "red"), ("audi", 2020, null)];

    private static async Task SeedAsync(AlvoApiWorld world)
    {
        var owner = await CreateAsync(world, "owners", new JsonObject { ["name"] = "Acme Ltd" });

        foreach (var (make, year, color) in _fleet)
        {
            var body = new JsonObject
            {
                ["vin"] = $"VIN-{make}",
                ["plate"] = $"PLATE-{make}",
                ["make"] = make,
                ["model"] = "model",
                ["year"] = year,
                ["owner_id"] = owner,
            };
            if (color is not null)
            {
                body["color"] = color;
            }

            await CreateAsync(world, "vehicles", body);
        }
    }

    private static async Task<string> CreateAsync(AlvoApiWorld world, string entity, JsonObject body)
    {
        using var created = await world.SendAsync(HttpMethod.Post, $"/api/{entity}", _admin, body: body);
        created.StatusCode.ShouldBe(HttpStatusCode.Created, await created.ReadTextAsync());
        return (await created.ReadJsonObjectAsync())["id"]!.GetValue<Guid>().ToString();
    }
}
