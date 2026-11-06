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

    private Task<AlvoApiWorld> StartReadsAsync() =>
        AlvoApiWorld.FromDescriptorAsync(
            "reads-desk.alvo.json",
            [_auditor],
            new AlvoApiWorldSetup(
                MapAlvoProblemDetails: true,
                ConfigureAlvo: alvo => alvo.Entity("notes", n => n.Narrow(
                    "north-only", (QueryNarrowing _) => new AlvoComparison("region", AlvoFilterOperator.Eq, "north")))),
            Engine);
}
