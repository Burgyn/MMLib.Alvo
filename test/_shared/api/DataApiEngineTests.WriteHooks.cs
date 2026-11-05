using Microsoft.Extensions.DependencyInjection;
using MMLib.Alvo.Data;
using MMLib.Alvo.Hooks;
using System.Net;
using System.Text.Json.Nodes;

namespace MMLib.Alvo.Api.Tests;

/// <summary>
/// Host write hooks over HTTP (spec 2026-10-14 §7 HX-09 acceptance (1)–(3)): each stage's rejection answers its status and
/// pointer with nothing written, a <c>Set</c> the rules or the facets refuse answers the same on every engine, and a hidden
/// field is never pointed at.
/// </summary>
public abstract partial class DataApiEngineTests
{
    private static readonly TestApiKey _orderWriter = new("order-writer", ["writer"], ["orders:read", "orders:write"]);

    public static TheoryData<string, HttpStatusCode, string, string> StageRejections => new()
    {
        { "validate", HttpStatusCode.UnprocessableEntity, "validation", "/title" },
        { "before", HttpStatusCode.Conflict, "conflict", "/title" },
        { "after", HttpStatusCode.Forbidden, "forbidden", "" },
    };

    [Theory]
    [MemberData(nameof(StageRejections))]
    public async Task A_rejection_at_each_write_stage_answers_its_status_and_pointer_and_writes_nothing_on_every_engine(
        string title, HttpStatusCode status, string type, string expectedPointer)
    {
        await using var world = await StartHookGuardsAsync();

        using var refused = await world.SendAsync(HttpMethod.Post, "/api/orders", _orderWriter, body: Order(title));

        refused.StatusCode.ShouldBe(status, await refused.ReadTextAsync());
        (await refused.ReadProblemTypeAsync()).ShouldBe(type);
        (await refused.ReadViolationsAsync()).ShouldHaveSingleItem().Pointer.ShouldBe(expectedPointer);
        (await refused.ReadJsonObjectAsync())["extensions"]!.AsArray().Select(node => node!.GetValue<string>()).ShouldBe([$"{title}-guard"]);
        (await world.CountRowsAsync("orders")).ShouldBe(0);
        (await world.CountRowsAsync("alvo_outbox")).ShouldBe(0);
    }

    [Fact]
    public async Task A_rejection_before_a_delete_answers_409_and_keeps_the_row_on_every_engine()
    {
        await using var world = await StartHookGuardsAsync();
        using var created = await world.SendAsync(HttpMethod.Post, "/api/orders", _orderWriter, body: Order("keep"));
        var id = (await created.ReadJsonObjectAsync())["id"]!.GetValue<Guid>();

        using var refused = await world.SendAsync(HttpMethod.Delete, $"/api/orders/{id}", _orderWriter);

        refused.StatusCode.ShouldBe(HttpStatusCode.Conflict, await refused.ReadTextAsync());
        (await world.CountRowsAsync("orders")).ShouldBe(1);
    }

    [Fact]
    public async Task A_rejection_of_one_batch_row_refuses_the_batch_at_that_row_and_writes_none_on_every_engine()
    {
        await using var world = await StartHookGuardsAsync();

        using var refused = await world.SendAsync(
            HttpMethod.Post, "/api/orders/batch", _orderWriter, body: new JsonObject { ["rows"] = new JsonArray(Order("plain"), Order("before")) });

        refused.StatusCode.ShouldBe(HttpStatusCode.Forbidden, await refused.ReadTextAsync());
        (await refused.ReadViolationsAsync()).ShouldHaveSingleItem().ShouldBe(("/rows/1", "taken"));
        (await world.CountRowsAsync("orders")).ShouldBe(0, "a batch is all-or-nothing (Ruling 4)");
    }

    [Fact]
    public async Task A_set_of_another_owner_is_forbidden_on_every_engine()
    {
        await using var world = await StartHookGuardsAsync();

        using var refused = await world.SendAsync(HttpMethod.Post, "/api/orders", _orderWriter, body: Order("steal"));

        refused.StatusCode.ShouldBe(HttpStatusCode.Forbidden, await refused.ReadTextAsync());
        (await refused.ReadProblemTypeAsync()).ShouldBe("forbidden");
        (await world.CountRowsAsync("orders")).ShouldBe(0);
    }

    [Fact]
    public async Task A_set_of_a_read_only_field_gets_the_port_s_403_while_the_caller_s_own_gets_the_payload_layer_s_422_on_every_engine()
    {
        await using var world = await StartHookGuardsAsync();
        var asCaller = new JsonObject { ["title"] = "plain", ["owner_id"] = _orderWriter.User, ["reference"] = "R-1" };

        using var byCaller = await world.SendAsync(HttpMethod.Post, "/api/orders", _orderWriter, body: asCaller);
        using var byHook = await world.SendAsync(HttpMethod.Post, "/api/orders", _orderWriter, body: Order("freeze"));

        byCaller.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity, await byCaller.ReadTextAsync());
        (await byCaller.ReadViolationsAsync()).ShouldHaveSingleItem().ShouldBe(("/reference", "read-only-field"));
        byHook.StatusCode.ShouldBe(HttpStatusCode.Forbidden, await byHook.ReadTextAsync());
        (await byHook.ReadProblemTypeAsync()).ShouldBe("forbidden");
        (await byHook.ReadProblemDetailAsync()).ShouldBe("Field 'reference' is read-only and cannot be written.", "the port's own read-only refusal, as IAlvoData answers a caller (Ruling 48)");
        (await world.CountRowsAsync("orders")).ShouldBe(0);
    }

    [Fact]
    public async Task A_set_of_an_undeclared_enum_value_is_invalid_set_on_every_engine()
    {
        await using var world = await StartHookGuardsAsync();

        using var refused = await world.SendAsync(HttpMethod.Post, "/api/orders", _orderWriter, body: Order("promote"));

        refused.StatusCode.ShouldBe(HttpStatusCode.InternalServerError);
        (await refused.ReadProblemTypeAsync()).ShouldBe("hook-failed");
        var document = await refused.ReadJsonObjectAsync();
        (document["reason"]!.GetValue<string>(), document["extensions"]![0]!.GetValue<string>()).ShouldBe(("invalid-set", "promote-guard"));
        (await refused.ReadTextAsync()).ShouldNotContain("superadmin");
        (await world.CountRowsAsync("orders")).ShouldBe(0);
    }

    [Fact]
    public async Task A_rejection_on_a_hidden_field_answers_with_no_pointer_on_every_engine()
    {
        await using var world = await StartHookGuardsAsync();

        using var refused = await world.SendAsync(HttpMethod.Post, "/api/orders", _orderWriter, body: Order("score"));

        refused.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);
        (await refused.ReadViolationsAsync()).ShouldHaveSingleItem().Pointer.ShouldBe(string.Empty);
        (await refused.ReadTextAsync()).ShouldNotContain("\"/score\"");
    }

    [Fact]
    public async Task A_hook_exception_answers_500_hook_failed_and_never_its_message_on_every_engine()
    {
        await using var world = await StartHookGuardsAsync();

        using var failed = await world.SendAsync(HttpMethod.Post, "/api/orders", _orderWriter, body: Order("crash"));

        failed.StatusCode.ShouldBe(HttpStatusCode.InternalServerError);
        (await failed.ReadJsonObjectAsync())["reason"]!.GetValue<string>().ShouldBe("exception");
        (await failed.ReadTextAsync()).ShouldNotContain("SECRET");
    }

    private static JsonObject Order(string title) => new() { ["title"] = title, ["owner_id"] = _orderWriter.User };

    private Task<AlvoApiWorld> StartHookGuardsAsync() =>
        AlvoApiWorld.FromDescriptorPathAsync(
            Path.Combine(RepositoryRoot.Find(), "test", "MMLib.Alvo.Api.Tests", "descriptors", "hook-guards.alvo.json"),
            [_orderWriter],
            new AlvoApiWorldSetup(MapAlvoProblemDetails: true, ConfigureAlvo: RegisterGuards),
            Engine);

    /// <summary>A hook limit no CI runner reaches: these facts are about answers, not time (Ruling 40).</summary>
    private static void Roomy(MMLib.Alvo.Extensions.AlvoHookOptions options) => options.Timeout = TimeSpan.FromSeconds(30);

    private static void RegisterGuards(IAlvoBuilder alvo) => alvo.Entity("orders", orders => orders
        .Validate("validate-guard", (ValidateWrite<AlvoRecord> write) => Reject(() => write.Invalid("too-plain", "Pick a better title.", "title")), when: "new.title == 'validate'", configure: Roomy)
        .BeforeWrite("before-guard", (BeforeWrite<AlvoRecord> write) => Reject(() => write.Conflict("taken", "Taken.", "title")), when: "new.title == 'before'", configure: Roomy)
        .BeforeWrite("steal-guard", (BeforeWrite<AlvoRecord> write) => Reject(() => write.Set("owner_id", Guid.NewGuid())), when: "new.title == 'steal'", configure: Roomy)
        .BeforeWrite("promote-guard", (BeforeWrite<AlvoRecord> write) => Reject(() => write.Set("status", "superadmin")), when: "new.title == 'promote'", configure: Roomy)
        .BeforeWrite("freeze-guard", (BeforeWrite<AlvoRecord> write) => Reject(() => write.Set("reference", "R-1")), when: "new.title == 'freeze'", configure: Roomy)
        .BeforeWrite("score-guard", (BeforeWrite<AlvoRecord> write) => Reject(() => write.Invalid("too-low", "Below the bar.", "score")), when: "new.title == 'score'", configure: Roomy)
        .BeforeWrite("crash-guard", ValueTask (BeforeWrite<AlvoRecord> _) => throw new InvalidOperationException("SECRET connection string"), when: "new.title == 'crash'", configure: Roomy)
        .AfterWrite("after-guard", (AfterWrite<AlvoRecord> write) => Reject(() => write.Forbidden("not-now", "Not now.")), when: "new.title == 'after'", configure: Roomy)
        .BeforeDelete("delete-guard", (BeforeDelete<AlvoRecord> write) => Reject(() => write.Conflict("kept", "Kept.")), when: "old.title == 'keep'", configure: Roomy));

    private static ValueTask Reject(Action record)
    {
        record();
        return ValueTask.CompletedTask;
    }
}
