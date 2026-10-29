using Microsoft.Extensions.DependencyInjection;

using MMLib.Alvo.Data;
using MMLib.Alvo.Events;

using System.Net;
using System.Text.Json.Nodes;

namespace MMLib.Alvo.Api.Tests.Management;

/// <summary>The operations routes over the wire (spec 2026-10-14 HX-06): levels (acceptance (4)), the handler refusal (9), the key header, stats (8).</summary>
public sealed class OperationsRouteTests
{
    private const string Prefix = "/management/projects/operated-notes";
    private const string Handler = "note-audit";
    private const string OperationsLog = "alvo_operations_log";

    private static readonly TestApiKey _viewer = new("ops-viewer", ["ops"], ["*:read"]);
    private static readonly TestApiKey _developer = new("ops-dev", ["dev"], ["*:write"]);
    private static readonly TestApiKey _admin = new("ops-admin", ["admin"], ["*:write"]);

    [Fact]
    public async Task A_viewer_is_refused_a_redeliver()
    {
        await using var world = await WorldAsync();

        var response = await world.SendAsync(HttpMethod.Post, $"{Prefix}/deliveries/{Guid.NewGuid()}/redeliver", _viewer);

        response.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await response.ReadProblemTypeAsync()).ShouldBe(AlvoProblemTypes.Forbidden);
    }

    [Fact]
    public async Task A_viewer_lists_deliveries_and_reads_the_stats()
    {
        await using var world = await WorldAsync();

        (await world.SendAsync(HttpMethod.Get, $"{Prefix}/deliveries?limit=10", _viewer)).StatusCode.ShouldBe(HttpStatusCode.OK);
        var stats = await world.SendAsync(HttpMethod.Get, $"{Prefix}/extensions/stats", _viewer);

        stats.StatusCode.ShouldBe(HttpStatusCode.OK);
        var body = JsonNode.Parse(await stats.Content.ReadAsStringAsync(TestContext.Current.CancellationToken))!;
        body["scope"]!.GetValue<string>().ShouldBe("this-instance");
        body["items"]!.AsArray().Select(item => item!["id"]!.GetValue<string>()).ShouldBe([Handler]);
    }

    [Fact]
    public async Task Switching_a_handler_off_is_409_naming_pausing()
    {
        await using var world = await WorldAsync();

        var response = await world.SendAsync(HttpMethod.Put, $"{Prefix}/extensions/{Handler}/enabled", _admin, body: new JsonObject { ["enabled"] = false });

        response.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        (await response.ReadProblemTypeAsync()).ShouldBe(AlvoProblemTypes.Conflict);
        (await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken)).ShouldContain($"subscribers/{Handler}/paused");
    }

    [Fact]
    public async Task A_developer_may_not_switch_code_off()
    {
        await using var world = await WorldAsync();

        (await world.SendAsync(HttpMethod.Put, $"{Prefix}/extensions/{Handler}/enabled", _developer, body: new JsonObject { ["enabled"] = false }))
            .StatusCode.ShouldBe(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task A_developer_pausing_a_handler_gets_its_catalog_entry_back()
    {
        await using var world = await WorldAsync();

        var response = await world.SendAsync(HttpMethod.Put, $"{Prefix}/subscribers/{Handler}/paused", _developer, body: new JsonObject { ["paused"] = true, ["reason"] = "mail provider down" });

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        JsonNode.Parse(await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken))!["paused"]!.GetValue<bool>().ShouldBeTrue();
    }

    [Fact]
    public async Task A_pause_that_does_not_say_paused_or_resumed_is_422()
    {
        await using var world = await WorldAsync();

        (await world.SendAsync(HttpMethod.Put, $"{Prefix}/subscribers/{Handler}/paused", _developer, body: new JsonObject { ["reason"] = "?" }))
            .StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);
    }

    [Fact]
    public async Task A_replay_reads_its_idempotency_key_from_the_header()
    {
        await using var world = await WorldAsync();
        KeyValuePair<string, string>[] key = [new("Idempotency-Key", "replay-http-1")];
        KeyValuePair<string, string>[] twoKeys = [new("Idempotency-Key", "a"), new("Idempotency-Key", "b")];

        var first = await world.SendAsync(HttpMethod.Post, $"{Prefix}/deliveries/replay", _developer, body: new JsonObject { ["subscriber"] = Handler }, headers: key);
        var again = await world.SendAsync(HttpMethod.Post, $"{Prefix}/deliveries/replay", _developer, body: new JsonObject { ["subscriber"] = Handler }, headers: key);
        var ambiguous = await world.SendAsync(HttpMethod.Post, $"{Prefix}/deliveries/replay", _developer, body: new JsonObject { ["subscriber"] = Handler }, headers: twoKeys);

        first.StatusCode.ShouldBe(HttpStatusCode.OK);
        JsonNode.Parse(await again.Content.ReadAsStringAsync(TestContext.Current.CancellationToken))!["replayed"]!.GetValue<bool>().ShouldBeTrue();
        ambiguous.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity, "two keys are two requests, and a replay is recorded under one");
    }

    [Theory]
    [InlineData("?dryRun=true")]
    [InlineData("?dry_run=true")]
    public async Task A_replay_with_a_query_parameter_is_422_and_resets_nothing(string query)
    {
        await using var world = await WorldAsync();

        var response = await world.SendAsync(HttpMethod.Post, $"{Prefix}/deliveries/replay{query}", _developer, body: new JsonObject { ["subscriber"] = Handler });

        response.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity, "a dry run asked where the apply route takes it must never commit a replay");
        (await response.ReadProblemTypeAsync()).ShouldBe(AlvoProblemTypes.Validation);
        (await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken)).ShouldContain("JSON body");
        (await world.CountRowsAsync(OperationsLog)).ShouldBe(0);
    }

    [Fact]
    public async Task A_replay_with_an_unknown_body_member_is_422_naming_it_and_resets_nothing()
    {
        await using var world = await WorldAsync();

        var response = await world.SendAsync(HttpMethod.Post, $"{Prefix}/deliveries/replay", _developer, body: new JsonObject { ["subscriber"] = Handler, ["dry_run"] = true });

        response.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity, "a misspelled dry-run flag must never commit a replay");
        (await response.ReadProblemTypeAsync()).ShouldBe(AlvoProblemTypes.Validation);
        (await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken)).ShouldContain("'dry_run'");
        (await world.CountRowsAsync(OperationsLog)).ShouldBe(0);
    }

    [Fact]
    public async Task A_real_replay_is_logged_so_a_refusal_that_logs_nothing_reset_nothing()
    {
        await using var world = await WorldAsync();

        (await world.SendAsync(HttpMethod.Post, $"{Prefix}/deliveries/replay", _developer, body: new JsonObject { ["subscriber"] = Handler }))
            .StatusCode.ShouldBe(HttpStatusCode.OK);

        (await world.CountRowsAsync(OperationsLog)).ShouldBe(1);
    }

    [Theory]
    [InlineData("from")]
    [InlineData("to")]
    public async Task A_delivery_list_window_before_the_epoch_is_answered_not_500(string bound)
    {
        await using var world = await WorldAsync();

        var response = await world.SendAsync(HttpMethod.Get, $"{Prefix}/deliveries?{bound}=1969-12-31T00:00:00Z", _viewer);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Theory]
    [InlineData("from")]
    [InlineData("to")]
    public async Task A_replay_window_before_the_epoch_is_answered_not_500(string bound)
    {
        await using var world = await WorldAsync();

        var response = await world.SendAsync(HttpMethod.Post, $"{Prefix}/deliveries/replay", _developer, body: new JsonObject { ["subscriber"] = Handler, [bound] = "0001-01-01T00:00:00Z", ["dryRun"] = true });

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Fact]
    public async Task An_unknown_delivery_is_404()
    {
        await using var world = await WorldAsync();

        (await world.SendAsync(HttpMethod.Get, $"{Prefix}/deliveries/{Guid.NewGuid()}", _viewer)).StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    private static Task<AlvoApiWorld> WorldAsync() => AlvoApiWorld.FromDescriptorAsync(
        "operated-notes.alvo.json",
        [_viewer, _developer, _admin],
        new AlvoApiWorldSetup(
            MapManagementApi: true,
            ConfigureServices: services => services.AddAlvo(alvo =>
                alvo.Entity("notes", notes => notes.OnCreated(Handler, (AlvoCreated<AlvoRecord> _) => Task.CompletedTask)))));
}
