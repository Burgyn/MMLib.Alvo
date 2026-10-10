using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace MMLib.Alvo.Api.Tests;

/// <summary>
/// A descriptor applied at runtime (<c>PUT …/descriptor</c>) is what the Data API serves from the very next
/// request — fields added, removed, hidden, frozen or narrowed — on every engine, with no restart (#353).
/// </summary>
/// <remarks>
/// <para>
/// <b>The defect these facts pin.</b> Every generated endpoint used to close over the
/// <see cref="Schema.EntitySchema"/> it was mapped with, so the body reader, the record validator, the query
/// parser and the format catalogue went on judging requests against the schema the process <em>started</em>
/// with, while the policy catalogue and the data port followed the apply. A field added at runtime was refused
/// as <c>unknown-field</c> until a restart; a field removed at runtime was still admitted by this layer and
/// refused by the port as a <c>403</c> — and a narrowed facet (a shorter <c>maxLength</c>) was not enforced at
/// all, because the port does not re-measure what this layer validates.
/// </para>
/// <para>
/// <b>Run on every engine</b> because a runtime apply is DDL (an added or dropped column) followed by a model
/// rebuild in the port, and both differ per engine.
/// </para>
/// </remarks>
public abstract partial class DataApiEngineTests
{
    private static readonly TestApiKey _editor =
        new("runtime-editor", ["editor"], ["*:read", "*:write"]);

    [Fact]
    public async Task A_field_added_at_runtime_is_writable_readable_and_filterable_at_once()
    {
        await using var world = await StartRuntimeApplyAsync();
        await ApplyRuntimeAsync(world, fields => fields["category"] = new JsonObject { ["type"] = "string" });

        using var created = await world.SendAsync(
            HttpMethod.Post, "/api/tickets", _editor,
            body: new JsonObject { ["title"] = "Leak", ["category"] = "plumbing" });
        created.StatusCode.ShouldBe(HttpStatusCode.Created, await created.ReadTextAsync());
        var id = (await created.ReadJsonObjectAsync())["id"]!.GetValue<string>();

        using var read = await world.SendAsync(HttpMethod.Get, $"/api/tickets/{id}", _editor);
        read.StatusCode.ShouldBe(HttpStatusCode.OK, await read.ReadTextAsync());
        (await read.ReadJsonObjectAsync())["category"]!.GetValue<string>().ShouldBe("plumbing");

        using var filtered = await world.SendAsync(
            HttpMethod.Get, "/api/tickets?category=eq.plumbing&select=title,category", _editor);
        filtered.StatusCode.ShouldBe(HttpStatusCode.OK, await filtered.ReadTextAsync());
        (await filtered.ReadFieldAsync("category")).ShouldBe(["plumbing"]);
    }

    [Fact]
    public async Task A_field_added_at_runtime_is_patchable_at_once()
    {
        await using var world = await StartRuntimeApplyAsync();
        var id = await CreateTicketAsync(world, new JsonObject { ["title"] = "Before" });
        await ApplyRuntimeAsync(world, fields => fields["category"] = new JsonObject { ["type"] = "string" });

        using var patched = await world.SendAsync(
            HttpMethod.Patch, $"/api/tickets/{id}", _editor, body: new JsonObject { ["category"] = "roof" });

        patched.StatusCode.ShouldBe(HttpStatusCode.OK, await patched.ReadTextAsync());
        (await patched.ReadJsonObjectAsync())["category"]!.GetValue<string>().ShouldBe("roof");
    }

    [Fact]
    public async Task A_field_removed_at_runtime_is_refused_as_unknown_on_a_write_at_once()
    {
        await using var world = await StartRuntimeApplyAsync();
        await ApplyRuntimeAsync(world, fields => fields.Remove("notes"), allowDestructive: true);

        using var created = await world.SendAsync(
            HttpMethod.Post, "/api/tickets", _editor,
            body: new JsonObject { ["title"] = "Leak", ["notes"] = "gone" });

        created.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity, await created.ReadTextAsync());
        (await created.ReadViolationsAsync()).ShouldBe([("/notes", "unknown-field")]);
        (await world.CountRowsAsync("tickets")).ShouldBe(0);
    }

    [Fact]
    public async Task A_field_removed_at_runtime_is_refused_as_unavailable_on_a_read_at_once()
    {
        await using var world = await StartRuntimeApplyAsync();
        await ApplyRuntimeAsync(world, fields => fields.Remove("notes"), allowDestructive: true);

        using var filtered = await world.SendAsync(HttpMethod.Get, "/api/tickets?notes=eq.x", _editor);
        using var selected = await world.SendAsync(HttpMethod.Get, "/api/tickets?select=title,notes", _editor);

        filtered.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity, await filtered.ReadTextAsync());
        (await filtered.ReadViolationsAsync()).Select(violation => violation.Code).ShouldBe(["unavailable-field"]);
        selected.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity, await selected.ReadTextAsync());
        (await selected.ReadViolationsAsync()).Select(violation => violation.Code).ShouldBe(["unavailable-field"]);
    }

    [Fact]
    public async Task A_field_frozen_at_runtime_is_refused_on_a_write_at_once()
    {
        await using var world = await StartRuntimeApplyAsync();
        var id = await CreateTicketAsync(world, new JsonObject { ["title"] = "Open", ["status"] = "open" });
        await ApplyRuntimeAsync(world, fields => fields["status"]!["readOnly"] = true);

        using var patched = await world.SendAsync(
            HttpMethod.Patch, $"/api/tickets/{id}", _editor, body: new JsonObject { ["status"] = "closed" });

        patched.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity, await patched.ReadTextAsync());
        (await patched.ReadViolationsAsync()).ShouldBe([("/status", "read-only-field")]);
    }

    [Fact]
    public async Task A_field_hidden_at_runtime_is_neither_returned_nor_filterable_at_once()
    {
        await using var world = await StartRuntimeApplyAsync();
        var id = await CreateTicketAsync(world, new JsonObject { ["title"] = "Vault", ["secret"] = "s3cr3t" });
        await ApplyRuntimeAsync(world, fields => fields["secret"]!["hidden"] = true);

        using var read = await world.SendAsync(HttpMethod.Get, $"/api/tickets/{id}", _editor);
        using var listed = await world.SendAsync(HttpMethod.Get, "/api/tickets", _editor);
        using var filtered = await world.SendAsync(HttpMethod.Get, "/api/tickets?secret=eq.s3cr3t", _editor);

        read.StatusCode.ShouldBe(HttpStatusCode.OK, await read.ReadTextAsync());
        (await read.ReadJsonObjectAsync()).ContainsKey("secret").ShouldBeFalse("a hidden field is never returned");
        listed.StatusCode.ShouldBe(HttpStatusCode.OK, await listed.ReadTextAsync());
        (await listed.ReadItemsAsync()).ShouldAllBe(item => !item.ContainsKey("secret"));
        filtered.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity, await filtered.ReadTextAsync());
        (await filtered.ReadViolationsAsync()).Select(violation => violation.Code).ShouldBe(["unavailable-field"]);
    }

    [Fact]
    public async Task A_facet_narrowed_at_runtime_is_enforced_at_once()
    {
        await using var world = await StartRuntimeApplyAsync();
        await ApplyRuntimeAsync(world, fields => fields["title"]!["maxLength"] = 5, allowDestructive: true);

        using var created = await world.SendAsync(
            HttpMethod.Post, "/api/tickets", _editor, body: new JsonObject { ["title"] = "Far too long" });

        created.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity, await created.ReadTextAsync());
        (await created.ReadViolationsAsync()).Select(violation => violation.Pointer).ShouldBe(["/title"]);
    }

    [Fact]
    public async Task A_field_made_required_at_runtime_is_enforced_at_once()
    {
        await using var world = await StartRuntimeApplyAsync();
        await ApplyRuntimeAsync(world, fields => fields["status"]!["required"] = true, allowDestructive: true);

        using var created = await world.SendAsync(
            HttpMethod.Post, "/api/tickets", _editor, body: new JsonObject { ["title"] = "No status" });

        created.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity, await created.ReadTextAsync());
        (await created.ReadViolationsAsync()).Select(violation => violation.Pointer).ShouldBe(["/status"]);
    }

    private Task<AlvoApiWorld> StartRuntimeApplyAsync() =>
        AlvoApiWorld.FromDescriptorAsync(
            "runtime-apply.alvo.json",
            [_editor],
            new AlvoApiWorldSetup(MapBeforePriming: true, MapManagementApi: true),
            Engine);

    /// <summary>Applies the current descriptor with one edit to <c>tickets</c>' fields, over the Management API.</summary>
    /// <param name="world">The running world.</param>
    /// <param name="edit">The edit to make to the <c>fields</c> object.</param>
    /// <param name="allowDestructive">Whether the apply asks for a destructive plan.</param>
    private static async Task ApplyRuntimeAsync(
        AlvoApiWorld world, Action<JsonObject> edit, bool allowDestructive = false)
    {
        const string path = "/management/projects/runtime-apply/descriptor";
        var current = await (await world.SendAsync(HttpMethod.Get, path, _editor)).ReadJsonObjectAsync();
        var descriptor = JsonNode.Parse(current["descriptorJson"]!.GetValue<string>())!.AsObject();
        edit(descriptor["entities"]!["tickets"]!["fields"]!.AsObject());
        var revision = current["revision"]!.GetValue<int>();

        using var applied = await world.SendAsync(
            HttpMethod.Put,
            path,
            _editor,
            body: new JsonObject
            {
                ["descriptorJson"] = descriptor.ToJsonString(new JsonSerializerOptions { WriteIndented = true }),
                ["allowDestructive"] = allowDestructive,
                ["author"] = "the-suite",
                ["reason"] = "a runtime-apply fact",
            },
            headers: [new KeyValuePair<string, string>("If-Match", $"\"{revision}\"")]);

        applied.StatusCode.ShouldBe(HttpStatusCode.OK, await applied.ReadTextAsync());
        (await applied.ReadJsonObjectAsync())["revision"]!.GetValue<int>().ShouldBe(revision + 1);
    }

    private static async Task<string> CreateTicketAsync(AlvoApiWorld world, JsonObject body)
    {
        using var created = await world.SendAsync(HttpMethod.Post, "/api/tickets", _editor, body: body);
        created.StatusCode.ShouldBe(HttpStatusCode.Created, await created.ReadTextAsync());
        return (await created.ReadJsonObjectAsync())["id"]!.GetValue<string>();
    }
}
