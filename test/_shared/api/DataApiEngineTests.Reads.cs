using Microsoft.Extensions.DependencyInjection;

using MMLib.Alvo.Data;
using MMLib.Alvo.Reads;

using System.Net;
using System.Text.Json.Nodes;

namespace MMLib.Alvo.Api.Tests;

/// <summary>
/// Query narrowing over HTTP: a narrowing is a view, not an authorization boundary, so the Data API's reference probe
/// resolves a reference under the rules alone — as the port checks it — on every engine.
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

        var schemas = (await world.OpenApiDocumentAsync())["components"]!["schemas"]!.AsObject();

        schemas["notes"]!["properties"]!["display"]!["readOnly"]!.GetValue<bool>().ShouldBeTrue();
        foreach (var request in new[] { "notesCreate", "notesPatch", "notesReplace" })
        {
            schemas[request]!["properties"]!.AsObject().ContainsKey("display").ShouldBeFalse(request);
        }
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

    private Task<AlvoApiWorld> StartReadsAsync() =>
        AlvoApiWorld.FromDescriptorAsync(
            "reads-desk.alvo.json",
            [_auditor, _noteClerk],
            new AlvoApiWorldSetup(
                MapOpenApiDocument: true,
                MapAlvoProblemDetails: true,
                ConfigureAlvo: alvo => alvo.Entity("notes", n => n.Narrow(
                    "north-only", (QueryNarrowing _) => new AlvoComparison("region", AlvoFilterOperator.Eq, "north")))),
            Engine);
}
