using System.Net;
using System.Text.Json.Nodes;

namespace MMLib.Alvo.Api.Tests.Management;

/// <summary>
/// <c>POST {m}/projects/{p}/revisions/{n}/rollback</c> — the tenth route.
/// </summary>
/// <remarks>
/// <para>
/// <b>A rollback is an apply of a past descriptor</b>, which is why it carries two numbers with two jobs:
/// the target revision in the route says <em>what</em> to restore, <c>If-Match</c> says <em>from where</em>.
/// Conflating them would let a caller roll back from a state they never saw.
/// </para>
/// <para>
/// <b><c>allowDestructive</c> is explicit here for the reason the guardrail exists.</b> A reverse migration
/// routinely drops what the forward one added, so a rollback is the case the gate was written for — same
/// 409, same slug, and a dry run is refused identically.
/// </para>
/// <para>
/// <b>C-1 applies to a rollback too, and that is this suite's own finding.</b> <c>access</c> lives inside
/// the descriptor, so restoring a revision whose block differs is exactly "changing who may reach the
/// project" — reached by a <c>developer</c>, at a route gated at <c>Developer</c>, without applying anything
/// they wrote. A history that ever held a looser block would otherwise be a standing escalation.
/// </para>
/// </remarks>
public class ManagementRollbackTests
{
    /// <summary>A caller the project's <c>access</c> block admits at <c>developer</c>.</summary>
    private static readonly TestApiKey _dev = new("mgmt-dev", ["dispatcher"], ["*:write"]);

    /// <summary>A caller the same block admits at <c>viewer</c> only.</summary>
    private static readonly TestApiKey _ops = new("mgmt-ops", ["ops"], ["*:read"]);

    /// <summary>A caller the same block admits at <c>admin</c>.</summary>
    private static readonly TestApiKey _owner = new("mgmt-owner", ["owner"], ["*:write"]);

    [Fact]
    public async Task A_rollback_restores_the_target_descriptor_and_appends_a_revision()
    {
        await using var world = await ManagedFleet.StartAsync([_dev]);
        var original = await CurrentAsync(world);
        await ApplyAsync(world, WithExtraField(original));

        var response = await RollbackAsync(world, target: 1, ifMatch: "\"2\"", allowDestructive: true);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await response.ReadJsonObjectAsync())["revision"]!.GetValue<int>().ShouldBe(3);
        (await response.ReadJsonObjectAsync())["applied"]!.GetValue<bool>().ShouldBeTrue();
        (await CurrentAsync(world)).ShouldBe(original, "history is never rewritten; the past is re-applied");
    }

    [Fact]
    public async Task The_appended_revision_records_what_it_rolled_back_from()
    {
        await using var world = await ManagedFleet.StartAsync([_dev]);
        await ApplyAsync(world, WithExtraField(await CurrentAsync(world)));
        await RollbackAsync(world, target: 1, ifMatch: "\"2\"", allowDestructive: true);

        var appended = await ReadAsync(world, ManagedFleet.Routes + "/revisions/3");

        appended["version"]!["rolledBackFrom"]!.GetValue<int>().ShouldBe(1);
    }

    [Fact]
    public async Task A_rollback_that_would_discard_data_is_409_unless_it_was_asked_for()
    {
        await using var world = await ManagedFleet.StartAsync([_dev]);
        await ApplyAsync(world, WithExtraField(await CurrentAsync(world)));

        var response = await RollbackAsync(world, target: 1, ifMatch: "\"2\"");

        response.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        (await response.ReadProblemTypeAsync()).ShouldBe(AlvoProblemTypes.DestructiveChange);
        (await RevisionAsync(world)).ShouldBe(2);
    }

    [Fact]
    public async Task A_rollback_without_if_match_is_428()
    {
        await using var world = await ManagedFleet.StartAsync([_dev]);
        await ApplyAsync(world, WithExtraField(await CurrentAsync(world)));

        var response = await RollbackAsync(world, target: 1, ifMatch: null, allowDestructive: true);

        response.StatusCode.ShouldBe((HttpStatusCode)428);
        (await response.ReadProblemTypeAsync()).ShouldBe(AlvoProblemTypes.PreconditionRequired);
        (await RevisionAsync(world)).ShouldBe(2);
    }

    [Fact]
    public async Task An_if_match_naming_a_revision_that_is_not_current_is_412()
    {
        await using var world = await ManagedFleet.StartAsync([_dev]);
        await ApplyAsync(world, WithExtraField(await CurrentAsync(world)));

        var response = await RollbackAsync(world, target: 1, ifMatch: "\"1\"", allowDestructive: true);

        response.StatusCode.ShouldBe(HttpStatusCode.PreconditionFailed);
        (await RevisionAsync(world)).ShouldBe(2, "the target says what to restore, If-Match says from where");
    }

    [Fact]
    public async Task A_rollback_to_a_revision_that_does_not_exist_is_404()
    {
        await using var world = await ManagedFleet.StartAsync([_dev]);

        var response = await RollbackAsync(world, target: 99, ifMatch: "\"1\"", allowDestructive: true);

        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await response.ReadProblemTypeAsync()).ShouldBe(AlvoProblemTypes.NotFound);
    }

    [Fact]
    public async Task A_viewer_may_not_roll_back()
    {
        await using var world = await ManagedFleet.StartAsync([_dev, _ops]);
        await ApplyAsync(world, WithExtraField(await CurrentAsync(world)));

        var response = await RollbackAsync(
            world, target: 1, ifMatch: "\"2\"", allowDestructive: true, caller: _ops);

        response.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await RevisionAsync(world)).ShouldBe(2);
    }

    [Fact]
    public async Task A_retried_rollback_with_the_same_key_replays_rather_than_rolling_back_twice()
    {
        await using var world = await ManagedFleet.StartAsync([_dev]);
        await ApplyAsync(world, WithExtraField(await CurrentAsync(world)));

        var first = await RollbackAsync(world, target: 1, ifMatch: "\"2\"", allowDestructive: true, key: "r1");
        var retry = await RollbackAsync(world, target: 1, ifMatch: "\"2\"", allowDestructive: true, key: "r1");

        first.StatusCode.ShouldBe(HttpStatusCode.OK);
        retry.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await retry.ReadJsonObjectAsync())["revision"]!.GetValue<int>().ShouldBe(3);
        (await retry.ReadJsonObjectAsync())["replayed"]!.GetValue<bool>().ShouldBeTrue(
            "the reverse plan it reports is empty because nothing ran, not because nothing would have");
        (await first.ReadJsonObjectAsync())["replayed"]!.GetValue<bool>().ShouldBeFalse();
        (await RevisionAsync(world)).ShouldBe(3, "a replay rolls nothing back a second time");
    }

    /// <summary>A key spent on an apply is never answered with a rollback's revision.</summary>
    /// <remarks>
    /// The operation name is in the fingerprint, so the two writes are two requests even when the project,
    /// the base and the allowance all match. Without that, one key would replay across verbs and a caller
    /// retrying a rollback could be handed the apply's revision instead.
    /// </remarks>
    [Fact]
    public async Task A_key_already_spent_on_an_apply_is_not_replayed_by_a_rollback()
    {
        await using var world = await ManagedFleet.StartAsync([_dev]);
        await ManagementApplyWorld.ApplyAsync(
            world, _dev, WithExtraField(await CurrentAsync(world)), ifMatch: "\"1\"", idempotencyKey: "k1");

        var response = await RollbackAsync(
            world, target: 1, ifMatch: "\"2\"", allowDestructive: true, key: "k1");

        response.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        (await response.ReadProblemTypeAsync()).ShouldBe(AlvoProblemTypes.IdempotencyConflict);
        (await RevisionAsync(world)).ShouldBe(2, "a key reused across verbs is refused, never replayed");
    }

    [Fact]
    public async Task A_rollback_dry_run_reports_the_reverse_plan_and_appends_nothing()
    {
        await using var world = await ManagedFleet.StartAsync([_dev]);
        await ApplyAsync(world, WithExtraField(await CurrentAsync(world)));

        var body = await (await RollbackAsync(
            world, target: 1, ifMatch: "\"2\"", allowDestructive: true, query: "?dryRun=true"))
            .ReadJsonObjectAsync();

        body["applied"]!.GetValue<bool>().ShouldBeFalse();
        body["revision"]!.GetValue<int>().ShouldBe(2, "a dry run reports the base it planned against");
        body["plan"]!["steps"]!.AsArray().ShouldNotBeEmpty();
        body["plan"]!["hasDestructiveChanges"]!.GetValue<bool>().ShouldBeTrue();
        (await RevisionAsync(world)).ShouldBe(2);
    }

    /// <summary>
    /// A dry run of a destructive rollback is refused exactly as the real one is.
    /// </summary>
    /// <remarks>
    /// The precedent the apply path set: a preview that reported a plan the rollback would then refuse tells
    /// an operator their restore is ready when it is not.
    /// </remarks>
    [Fact]
    public async Task A_rollback_dry_run_of_a_destructive_plan_is_refused_the_same_way()
    {
        await using var world = await ManagedFleet.StartAsync([_dev]);
        await ApplyAsync(world, WithExtraField(await CurrentAsync(world)));

        var response = await RollbackAsync(world, target: 1, ifMatch: "\"2\"", query: "?dryRun=true");

        response.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        (await response.ReadProblemTypeAsync()).ShouldBe(AlvoProblemTypes.DestructiveChange);
    }

    /// <summary>C-1, carried forward: a rollback across an <c>access</c> change needs an administrator.</summary>
    [Fact]
    public async Task A_developer_may_not_roll_back_to_a_revision_with_a_different_access_block()
    {
        await using var world = await ManagedFleet.StartAsync([_dev, _owner]);
        await ApplyAsync(world, Reviewed(await CurrentAsync(world)), caller: _owner);

        var response = await RollbackAsync(world, target: 1, ifMatch: "\"2\"");

        response.StatusCode.ShouldBe(
            HttpStatusCode.Forbidden, "restoring a different access block is changing who may reach it");
        (await response.ReadProblemTypeAsync()).ShouldBe(AlvoProblemTypes.Forbidden);
        (await RevisionAsync(world, _owner)).ShouldBe(2);
    }

    /// <summary>The other direction: the guard is a level requirement, not a ban.</summary>
    [Fact]
    public async Task An_administrator_may_roll_back_to_a_revision_with_a_different_access_block()
    {
        await using var world = await ManagedFleet.StartAsync([_dev, _owner]);
        await ApplyAsync(world, Reviewed(await CurrentAsync(world)), caller: _owner);

        var response = await RollbackAsync(world, target: 1, ifMatch: "\"2\"", caller: _owner);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await RevisionAsync(world, _owner)).ShouldBe(3);
    }

    /// <summary>A developer may still roll back across a revision that left the block alone.</summary>
    [Fact]
    public async Task A_developer_may_roll_back_across_a_revision_that_left_the_access_block_alone()
    {
        await using var world = await ManagedFleet.StartAsync([_dev]);
        await ApplyAsync(world, WithExtraField(await CurrentAsync(world)));

        var response = await RollbackAsync(world, target: 1, ifMatch: "\"2\"", allowDestructive: true);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await RevisionAsync(world)).ShouldBe(3);
    }

    [Theory]
    [InlineData("?dry_run=true")]
    [InlineData("?dryRun=true&force=true")]
    [InlineData("?DryRun=true")]
    public async Task A_query_parameter_this_route_does_not_read_is_refused(string query)
    {
        await using var world = await ManagedFleet.StartAsync([_dev]);
        await ApplyAsync(world, WithExtraField(await CurrentAsync(world)));

        var response = await RollbackAsync(
            world, target: 1, ifMatch: "\"2\"", allowDestructive: true, query: query);

        response.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);
        (await RevisionAsync(world)).ShouldBe(2, "a misspelled name would otherwise roll back for real");
    }

    private static Task<HttpResponseMessage> RollbackAsync(
        AlvoApiWorld world, int target, string? ifMatch, bool allowDestructive = false, string query = "",
        string? key = null, TestApiKey? caller = null) =>
        world.SendAsync(
            HttpMethod.Post,
            $"{ManagedFleet.Routes}/revisions/{target}/rollback{query}",
            caller ?? _dev,
            body: new JsonObject
            {
                ["allowDestructive"] = allowDestructive,
                ["author"] = "the-suite",
                ["reason"] = "a fact",
            },
            headers: ManagementApplyWorld.Headers(ifMatch, key));

    private static Task<HttpResponseMessage> ApplyAsync(
        AlvoApiWorld world, string descriptorJson, TestApiKey? caller = null) =>
        ManagementApplyWorld.ApplyAsync(world, caller ?? _dev, descriptorJson, ifMatch: "\"1\"");

    private static Task<string> CurrentAsync(AlvoApiWorld world, TestApiKey? caller = null) =>
        ManagementApplyWorld.CurrentAsync(world, caller ?? _dev);

    private static Task<int> RevisionAsync(AlvoApiWorld world, TestApiKey? caller = null) =>
        ManagementApplyWorld.RevisionAsync(world, caller ?? _dev);

    private static async Task<JsonObject> ReadAsync(AlvoApiWorld world, string path) =>
        await (await world.SendAsync(HttpMethod.Get, path, _dev)).ReadJsonObjectAsync();

    private static string WithExtraField(string descriptorJson) =>
        DescriptorEdits.AddOptionalTextField(descriptorJson, entity: "vehicles", field: "nickname");

    /// <summary>
    /// The <c>access</c> block changed and nothing else — the <c>viewer</c> level points at a role that is
    /// neither caller's, so both keep the levels these facts address them with.
    /// </summary>
    /// <param name="descriptorJson">The descriptor to edit.</param>
    private static string Reviewed(string descriptorJson) =>
        DescriptorEdits.GrantViewTo(descriptorJson, role: "owner");
}
