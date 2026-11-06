using Microsoft.Extensions.DependencyInjection;

using MMLib.Alvo.Data;
using MMLib.Alvo.Extensions;
using MMLib.Alvo.Reads;

using System.Net;
using System.Text.Json.Nodes;

namespace MMLib.Alvo.Api.Tests;

/// <summary>
/// Read extensions over HTTP, on every engine: a narrowing is a view, not an authorization boundary, so the Data API's
/// reference probe resolves a reference under the rules alone — as the port checks it; an enricher shapes every returned
/// row and write echo, and one that throws answers 500 hook-failed naming it.
/// </summary>
public abstract partial class DataApiEngineTests
{
    private static readonly Guid _readsTenant = Guid.NewGuid();

    private static readonly TestApiKey _auditor = new(
        "reads-auditor", ["auditor"], ["notes:read", "notes:write", "note_links:read", "note_links:write"], _readsTenant);

    private static readonly TestApiKey _noteClerk = new("reads-clerk", ["clerk"], ["notes:read", "notes:write"], _readsTenant);

    public static TheoryData<string, string> VirtualQueries => new()
    {
        { "display=eq.x", "filter" },
        { "order=display.asc", "order" },
        { "select=id,display", "select" },
    };

    [Fact]
    public async Task A_reference_to_a_row_the_narrowing_hides_still_resolves()
    {
        await using var world = await StartReadsAsync();
        using var note = await world.SendAsync(HttpMethod.Post, "/api/notes", _auditor, body: new JsonObject
        {
            ["title"] = "south note",
            ["owner_id"] = Guid.NewGuid(),
            ["region"] = "south",
            ["tenant_id"] = _readsTenant,
        });
        note.StatusCode.ShouldBe(HttpStatusCode.Created, await note.ReadTextAsync());
        var noteId = (await note.ReadJsonObjectAsync())["id"]!.GetValue<Guid>();
        using var hidden = await world.SendAsync(HttpMethod.Get, $"/api/notes/{noteId}", _auditor);

        using var link = await world.SendAsync(HttpMethod.Post, "/api/note_links", _auditor, body: new JsonObject
        {
            ["note_id"] = noteId,
            ["tenant_id"] = _readsTenant,
        });

        hidden.StatusCode.ShouldBe(HttpStatusCode.NotFound, "the narrowing hides the note from the auditor's get");
        link.StatusCode.ShouldBe(HttpStatusCode.Created, await link.ReadTextAsync());
    }

    [Theory]
    [InlineData("POST", "/api/notes")]
    [InlineData("PATCH", "/api/notes/{id}")]
    public async Task A_virtual_field_in_a_create_or_patch_is_422_virtual_on_every_engine(string method, string route)
    {
        await using var world = await StartReadsAsync();
        var id = await CreateNoteAsync(world, "kept");
        var body = method == "POST" ? NoteBody("x") : new JsonObject { ["title"] = "x" };
        body["display"] = "forged";

        using var refused = await world.SendAsync(
            new HttpMethod(method), route.Replace("{id}", id.ToString(), StringComparison.Ordinal), _noteClerk, body: body);

        refused.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity, await refused.ReadTextAsync());
        var violation = (await refused.ReadViolationsAsync()).ShouldHaveSingleItem();
        (violation.Code, violation.Pointer).ShouldBe(("virtual", "/display"));
        (await refused.ReadTextAsync()).ShouldContain("This field is virtual: the host fills it when a record is read, so a write cannot set it.");
    }

    [Fact]
    public async Task A_write_naming_a_virtual_field_the_policy_hides_is_422_unknown_field_on_every_engine()
    {
        await using var world = await StartReadsAsync();
        var body = NoteBody("x");
        body["internal_code"] = "forged";

        using var refused = await world.SendAsync(HttpMethod.Post, "/api/notes", _noteClerk, body: body);

        refused.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity, await refused.ReadTextAsync());
        var violation = (await refused.ReadViolationsAsync()).ShouldHaveSingleItem();
        (violation.Code, violation.Pointer).ShouldBe(("unknown-field", "/internal_code"));
        (await refused.ReadTextAsync()).ShouldNotContain("virtual");
    }

    [Theory]
    [MemberData(nameof(VirtualQueries))]
    public async Task A_virtual_field_in_filter_order_or_select_is_422_on_every_engine(string query, string parameter)
    {
        await using var world = await StartReadsAsync();

        using var refused = await world.SendAsync(HttpMethod.Get, $"/api/notes?{query}", _noteClerk);

        refused.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity, await refused.ReadTextAsync());
        var violation = (await refused.ReadViolationsAsync()).ShouldHaveSingleItem();
        (violation.Code, violation.Pointer).ShouldBe(("virtual-field", parameter));
        (await refused.ReadTextAsync()).ShouldContain("'display' is virtual: the host fills it after the read, so it cannot be filtered, sorted or selected.");
    }

    [Fact]
    public async Task A_hidden_virtual_field_in_a_filter_reads_as_unavailable_on_every_engine()
    {
        await using var world = await StartReadsAsync();

        using var refused = await world.SendAsync(HttpMethod.Get, "/api/notes?internal_code=eq.x", _noteClerk);

        refused.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity, await refused.ReadTextAsync());
        (await refused.ReadViolationsAsync()).ShouldHaveSingleItem().Code.ShouldBe("unavailable-field");
    }

    [Fact]
    public async Task A_virtual_field_is_read_only_in_the_read_schema_and_absent_from_request_bodies()
    {
        await using var world = await StartReadsAsync();

        var document = await world.OpenApiDocumentAsync();
        var schemas = document["components"]!["schemas"]!.AsObject();

        schemas["notes"]!["properties"]!["display"]!["readOnly"]!.GetValue<bool>().ShouldBeTrue();
        foreach (var request in new[] { "notesCreate", "notesPatch", "notesReplace" })
        {
            schemas[request]!["properties"]!.AsObject().ContainsKey("display").ShouldBeFalse(request);
        }

        schemas["notesQuery"]!["properties"]!.AsObject().ContainsKey("display").ShouldBeFalse("a virtual field is never a query member");
        schemas["notesQuery"]!["properties"]!.AsObject().ContainsKey("title").ShouldBeTrue();
        var listed = document["paths"]!["/api/notes"]!["get"]!["parameters"]!.AsArray()
            .Select(parameter => parameter?["name"]?.GetValue<string>()).ToList();
        listed.ShouldNotContain("display", "a virtual field is never a filter parameter");
        listed.ShouldContain("title");
    }

    [Fact]
    public async Task A_field_an_enricher_hides_is_absent_from_list_get_and_the_create_echo_on_every_engine()
    {
        await using var world = await StartReadsAsync(n => n.Enrich("no-region", (Enrich<AlvoRecord> row) => row.Hide("region")));

        using var created = await world.SendAsync(HttpMethod.Post, "/api/notes", _noteClerk, body: NoteBody("a"));
        created.StatusCode.ShouldBe(HttpStatusCode.Created, await created.ReadTextAsync());
        var echo = await created.ReadJsonObjectAsync();
        using var got = await world.SendAsync(HttpMethod.Get, $"/api/notes/{echo["id"]}", _noteClerk);
        using var listed = await world.SendAsync(HttpMethod.Get, "/api/notes", _noteClerk);

        got.StatusCode.ShouldBe(HttpStatusCode.OK, await got.ReadTextAsync());
        listed.StatusCode.ShouldBe(HttpStatusCode.OK, await listed.ReadTextAsync());
        var item = (await listed.ReadJsonObjectAsync())["items"]!.AsArray().ShouldHaveSingleItem()!.AsObject();
        foreach (var record in new[] { echo, await got.ReadJsonObjectAsync(), item })
        {
            record.ContainsKey("region").ShouldBeFalse();
            record["title"]!.GetValue<string>().ShouldBe("a");
        }
    }

    [Fact]
    public async Task A_throwing_enricher_answers_500_hook_failed_naming_it_on_every_engine()
    {
        await using var world = await StartReadsAsync(n => n.Enrich("boom", (Enrich<AlvoRecord> row) =>
        {
            if (row.Operation == MMLib.Alvo.Rules.DataOperation.List)
            {
                throw new InvalidOperationException("host bug");
            }
        }));
        await CreateNoteAsync(world, "a");

        using var failed = await world.SendAsync(HttpMethod.Get, "/api/notes", _noteClerk);

        failed.StatusCode.ShouldBe(HttpStatusCode.InternalServerError, await failed.ReadTextAsync());
        var problem = await failed.ReadJsonObjectAsync();
        problem["type"]!.GetValue<string>().ShouldEndWith("hook-failed");
        problem["extensions"]!.AsArray().Select(id => id!.GetValue<string>()).ShouldBe(["boom"]);
        problem["reason"]!.GetValue<string>().ShouldBe("exception");
        problem["detail"]!.GetValue<string>().ShouldBe(
            "The read extension 'boom' failed (exception), so the read answered nothing. Its own error is in the server log.");
        (await failed.ReadTextAsync()).ShouldNotContain("host bug");
    }

    private static JsonObject NoteBody(string title) => new()
    {
        ["title"] = title,
        ["owner_id"] = _noteClerk.User,
        ["region"] = "north",
        ["tenant_id"] = _readsTenant,
    };

    private static async Task<Guid> CreateNoteAsync(AlvoApiWorld world, string title)
    {
        using var created = await world.SendAsync(HttpMethod.Post, "/api/notes", _noteClerk, body: NoteBody(title));
        created.StatusCode.ShouldBe(HttpStatusCode.Created, await created.ReadTextAsync());

        return (await created.ReadJsonObjectAsync())["id"]!.GetValue<Guid>();
    }

    private Task<AlvoApiWorld> StartReadsAsync(Action<AlvoEntityBuilder>? extensions = null) =>
        AlvoApiWorld.FromDescriptorAsync(
            "reads-desk.alvo.json",
            [_auditor, _noteClerk],
            new AlvoApiWorldSetup(
                MapOpenApiDocument: true,
                MapAlvoProblemDetails: true,
                ConfigureAlvo: alvo => alvo.Entity("notes", n =>
                {
                    n.Narrow("north-only", (QueryNarrowing _) => new AlvoComparison("region", AlvoFilterOperator.Eq, "north"));
                    extensions?.Invoke(n);
                })),
            Engine);
}
