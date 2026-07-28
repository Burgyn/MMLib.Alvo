using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using MMLib.Alvo.Api.Internal;
using MMLib.Alvo.Data;
using MMLib.Alvo.Descriptor;
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
    public async Task A_filter_narrows_the_rows_the_store_returns()
    {
        await using var world = await SeededAsync();

        using var filtered = await world.SendAsync(HttpMethod.Get, "/api/vehicles?make=eq.vw", _admin);
        using var everything = await world.SendAsync(HttpMethod.Get, "/api/vehicles", _admin);

        (await filtered.ReadFieldAsync("make")).ShouldBe(["vw"]);
        (await everything.ReadFieldAsync("make")).Count.ShouldBe(3, "or the filtered read proves nothing");
    }

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

    [Fact]
    public async Task An_order_parameter_orders_the_page_and_the_direction_is_honoured()
    {
        await using var world = await SeededAsync();

        using var ascending = await world.SendAsync(HttpMethod.Get, "/api/vehicles?order=make", _admin);
        using var descending = await world.SendAsync(HttpMethod.Get, "/api/vehicles?order=make.desc", _admin);

        (await ascending.ReadFieldAsync("make")).ShouldBe(["audi", "skoda", "vw"]);
        (await descending.ReadFieldAsync("make")).ShouldBe(["vw", "skoda", "audi"]);
    }

    /// <summary>
    /// A filter over a numeric field is compared as a number, end to end.
    /// </summary>
    /// <remarks>
    /// <b>Honest about what this does and does not discriminate.</b> The port's own <c>ColumnValue</c> converts a
    /// caller value through the column's CLR type, so it would repair a string operand this parser wrongly
    /// emitted — which means this fact does not currently fail if the parser stops typing its operands
    /// (<c>QueryStringParserTests.An_accepted_operand_reaches_the_port_as_the_type_the_field_is_carried_as</c> is
    /// the fact that does). It is here because the behaviour is the contract a caller reads, and because it fails
    /// if that repair is ever removed — the two layers each defending it is deliberate, and neither is evidence
    /// for the other.
    /// </remarks>
    [Fact]
    public async Task A_numeric_filter_compares_numerically_rather_than_lexically()
    {
        await using var world = await SeededAsync();

        using var response = await world.SendAsync(HttpMethod.Get, "/api/vehicles?year=gt.500&order=year", _admin);

        var years = (await response.ReadItemsAsync()).Select(row => row["year"]!.GetValue<int>());
        years.ShouldBe([1999, 2020], "as text every seeded year sorts below '500', so a lexical comparison returns none");
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
    /// The default page size is applied to a request that names none — which is also why a nullable sort key is
    /// unusable over HTTP, since the port refuses a paged read sorted by one.
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
    /// stripped off, and its fix suggestion names something this surface can actually do.
    /// </summary>
    /// <remarks>
    /// The live leg of two defects. <c>(Parameter 'query')</c> shipped in this body, because the method meant to
    /// strip it cut at a newline the suffix is not behind — <c>AlvoApiWorld</c> now screens every response for
    /// it. And the fix suggestion used to offer "ask for the whole set with no limit", which this surface
    /// forbids: every list gets a default page size, so an agent following it would retry the identical request
    /// forever.
    /// </remarks>
    [Fact]
    public async Task A_paged_read_sorted_by_a_nullable_field_is_refused_in_the_ports_own_words()
    {
        await using var world = await SeededAsync();

        using var response = await world.SendAsync(HttpMethod.Get, "/api/vehicles?order=color", _admin);

        response.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);
        var violation = (await response.ReadJsonObjectAsync())["violations"]!.AsArray()
            .ShouldHaveSingleItem()!.AsObject();
        violation["code"]!.GetValue<string>().ShouldBe("unpageable-sort-key");
        violation["message"]!.GetValue<string>().ShouldNotContain("(Parameter '");
        violation["fixSuggestion"]!.GetValue<string>().ShouldNotContain(
            "no limit", Case.Insensitive, "this surface always applies a page size, so that is not achievable");
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
    /// The mapping-time reserved-name belt, exercised on the <b>only</b> path it exists for: an applied schema that
    /// reaches route generation without ever having passed descriptor validation.
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
    /// </remarks>
    [Fact]
    public void A_schema_reaching_mapping_without_validation_is_still_refused_for_a_reserved_field_name()
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

        var refusal = Should.Throw<InvalidOperationException>(() => app.MapAlvoDataApi());
        refusal.Message.ShouldContain("widgets");
        refusal.Message.ShouldContain(ReservedQueryKeys.Limit);
        refusal.Message.ShouldContain("Rename the field");
    }

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
    /// The years are chosen so that <b>lexical and numeric ordering disagree</b>: as text, every one of
    /// <c>300</c>, <c>1999</c> and <c>2020</c> sorts below <c>500</c>, so a filter compared as <c>TEXT</c>
    /// answers <c>year=gt.500</c> with nothing while a numeric comparison answers with two rows. Uniform years
    /// would make <see cref="A_numeric_filter_compares_numerically_rather_than_lexically"/> unable to fail.
    /// </remarks>
    private static readonly (string Make, int Year)[] _fleet = [("skoda", 300), ("vw", 1999), ("audi", 2020)];

    private static async Task SeedAsync(AlvoApiWorld world)
    {
        var owner = await CreateAsync(world, "owners", new JsonObject { ["name"] = "Acme Ltd" });

        foreach (var (make, year) in _fleet)
        {
            await CreateAsync(world, "vehicles", new JsonObject
            {
                ["vin"] = $"VIN-{make}",
                ["plate"] = $"PLATE-{make}",
                ["make"] = make,
                ["model"] = "model",
                ["year"] = year,
                ["owner_id"] = owner,
            });
        }
    }

    private static async Task<string> CreateAsync(AlvoApiWorld world, string entity, JsonObject body)
    {
        using var created = await world.SendAsync(HttpMethod.Post, $"/api/{entity}", _admin, body: body);
        created.StatusCode.ShouldBe(HttpStatusCode.Created, await created.ReadTextAsync());
        return (await created.ReadJsonObjectAsync())["id"]!.GetValue<Guid>().ToString();
    }
}
