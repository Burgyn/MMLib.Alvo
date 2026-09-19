using System.Net;
using System.Text.Json.Nodes;

namespace MMLib.Alvo.Api.Tests.Management;

/// <summary>
/// <c>PUT {m}/projects/{p}/descriptor</c> — the one write path to a project's configuration.
/// </summary>
/// <remarks>
/// <para>
/// <b><c>If-Match</c> carries the descriptor's <c>revision</c>, not an ETag over a row version</b> (the F5
/// design's deviation D3). <c>schema/project.schema.json</c> already froze that integer as the
/// optimistic-concurrency token, and minting a second token beside a frozen one would leave two answers in
/// the repo for one decision. Same header, same RFC 9110 semantics, different source.
/// </para>
/// <para>
/// <b>The header is required, and its absence is 428 rather than a default.</b> An apply with no expected
/// revision is a lost update with nothing to detect it, on the one document that defines the whole backend.
/// </para>
/// </remarks>
public class ManagementApplyTests
{
    /// <summary>A caller the project's <c>access</c> block admits at <c>developer</c>.</summary>
    private static readonly TestApiKey _dev = new("mgmt-dev", ["dispatcher"], ["*:write"]);

    /// <summary>A caller the same block admits at <c>viewer</c> only.</summary>
    private static readonly TestApiKey _ops = new("mgmt-ops", ["ops"], ["*:read"]);

    /// <summary>A caller the same block admits at <c>admin</c>.</summary>
    private static readonly TestApiKey _owner = new("mgmt-owner", ["owner"], ["*:write"]);

    private const string Path = ManagedFleet.Routes + "/descriptor";

    [Fact]
    public async Task An_apply_without_if_match_is_428_and_changes_nothing()
    {
        await using var world = await ManagedFleet.StartAsync([_dev]);

        var response = await world.SendAsync(
            HttpMethod.Put, Path, _dev, body: Body(await CurrentAsync(world)));

        response.StatusCode.ShouldBe((HttpStatusCode)428);
        (await response.ReadProblemTypeAsync()).ShouldBe(AlvoProblemTypes.PreconditionRequired);
        (await RevisionAsync(world)).ShouldBe(1);
    }

    [Fact]
    public async Task An_if_match_naming_a_revision_that_is_not_current_is_412()
    {
        await using var world = await ManagedFleet.StartAsync([_dev]);

        var response = await ApplyAsync(world, await CurrentAsync(world), ifMatch: "\"7\"");

        response.StatusCode.ShouldBe(HttpStatusCode.PreconditionFailed);
        (await response.ReadProblemTypeAsync()).ShouldBe(AlvoProblemTypes.PreconditionFailed);
        (await RevisionAsync(world)).ShouldBe(1);
    }

    [Theory]
    [InlineData("W/\"1\"", "a weak tag names no revision this API can compare")]
    [InlineData("*", "'*' means 'any current representation', which no revision comparison can honour")]
    [InlineData("\"latest\"", "a tag that is not a number names no revision")]
    public async Task An_if_match_that_is_not_a_revision_is_412_rather_than_ignored(string ifMatch, string because)
    {
        await using var world = await ManagedFleet.StartAsync([_dev]);

        var response = await ApplyAsync(world, await CurrentAsync(world), ifMatch);

        response.StatusCode.ShouldBe(HttpStatusCode.PreconditionFailed, because);
        (await RevisionAsync(world)).ShouldBe(1, "an uncomparable precondition is refused, never ignored");
    }

    [Fact]
    public async Task Applying_a_new_field_appends_a_revision_and_reports_its_plan()
    {
        await using var world = await ManagedFleet.StartAsync([_dev]);

        var response = await ApplyAsync(world, WithExtraField(await CurrentAsync(world)), ifMatch: "\"1\"");
        var body = await response.ReadJsonObjectAsync();

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        body["applied"]!.GetValue<bool>().ShouldBeTrue();
        body["revision"]!.GetValue<int>().ShouldBe(2);
        body["plan"]!["isEmpty"]!.GetValue<bool>().ShouldBeFalse();
        body["plan"]!["hasDestructiveChanges"]!.GetValue<bool>().ShouldBeFalse();
        body["plan"]!["steps"]!.AsArray().ShouldNotBeEmpty();
        (await RevisionAsync(world)).ShouldBe(2);
    }

    [Fact]
    public async Task A_dry_run_reports_the_plan_and_appends_nothing()
    {
        await using var world = await ManagedFleet.StartAsync([_dev]);

        var body = await (await ApplyAsync(
            world, WithExtraField(await CurrentAsync(world)), ifMatch: "\"1\"", query: "?dryRun=true"))
            .ReadJsonObjectAsync();

        body["applied"]!.GetValue<bool>().ShouldBeFalse();
        body["revision"]!.GetValue<int>().ShouldBe(1, "a dry run reports the base it planned against");
        body["plan"]!["steps"]!.AsArray().ShouldNotBeEmpty();
        (await RevisionAsync(world)).ShouldBe(
            1, "a dry run that appended would be an apply with a friendlier name");
    }

    [Fact]
    public async Task A_destructive_apply_is_409_destructive_change_unless_it_was_asked_for()
    {
        await using var world = await ManagedFleet.StartAsync([_dev]);

        var response = await ApplyAsync(world, WithoutAnEntity(await CurrentAsync(world)), ifMatch: "\"1\"");

        response.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        (await response.ReadProblemTypeAsync()).ShouldBe(AlvoProblemTypes.DestructiveChange);
        (await RevisionAsync(world)).ShouldBe(1);
    }

    /// <summary>
    /// A dry run of a destructive change is refused exactly as the real one is, and still writes nothing.
    /// </summary>
    /// <remarks>
    /// The guardrail is part of the answer a preview owes: a dry run that reported a plan the apply would
    /// then refuse would tell an editor its change is ready when it is not.
    /// </remarks>
    [Fact]
    public async Task A_dry_run_of_a_destructive_change_is_refused_the_same_way()
    {
        await using var world = await ManagedFleet.StartAsync([_dev]);

        var response = await ApplyAsync(
            world, WithoutAnEntity(await CurrentAsync(world)), ifMatch: "\"1\"", query: "?dryRun=true");

        response.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        (await response.ReadProblemTypeAsync()).ShouldBe(AlvoProblemTypes.DestructiveChange);
        (await RevisionAsync(world)).ShouldBe(1);
    }

    [Fact]
    public async Task The_same_destructive_apply_lands_when_the_body_asks_for_it()
    {
        await using var world = await ManagedFleet.StartAsync([_dev]);

        var response = await ApplyAsync(
            world, WithoutAnEntity(await CurrentAsync(world)), ifMatch: "\"1\"", allowDestructive: true);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await RevisionAsync(world)).ShouldBe(2);
    }

    /// <summary>
    /// A <c>dryRun</c> this API cannot read is refused, never treated as a real apply.
    /// </summary>
    /// <remarks>
    /// The same rule the <c>If-Match</c> arm above states, one parameter over: a caller who wrote
    /// <c>?dryRun=yes</c> asked for a preview, and answering it with a committed schema change is the one
    /// failure mode a dry run exists to make impossible.
    /// </remarks>
    [Fact]
    public async Task A_dry_run_value_this_api_cannot_read_is_refused_rather_than_applied()
    {
        await using var world = await ManagedFleet.StartAsync([_dev]);

        var response = await ApplyAsync(
            world, WithExtraField(await CurrentAsync(world)), ifMatch: "\"1\"", query: "?dryRun=yes");

        response.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);
        (await RevisionAsync(world)).ShouldBe(1, "an unreadable dryRun must not commit a schema change");
    }

    /// <summary>
    /// A query parameter this route does not read is refused, rather than ignored into a real apply.
    /// </summary>
    /// <remarks>
    /// <b>The misspelling that matters is the one in the <em>name</em>.</b> Every <c>?dryRun=</c> value is
    /// already safe, but an ignored unknown key means <c>?dry_run=true</c> and <c>?dry-run=true</c> commit
    /// the change the caller asked to preview — the same outcome, reached one character earlier. The
    /// argument the dry-run refusal makes from caller intent is exactly as true here, so this route reads
    /// one query parameter and refuses every other.
    /// </remarks>
    /// <param name="query">A query string naming something this route does not read.</param>
    [Theory]
    [InlineData("?dry_run=true")]
    [InlineData("?dry-run=true")]
    [InlineData("?dryrun=true")]
    [InlineData("?DryRun=true")]
    [InlineData("?dryRun=true&force=true")]
    public async Task A_query_parameter_this_route_does_not_read_is_refused(string query)
    {
        await using var world = await ManagedFleet.StartAsync([_dev]);

        var response = await ApplyAsync(
            world, WithExtraField(await CurrentAsync(world)), ifMatch: "\"1\"", query: query);

        response.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);
        (await RevisionAsync(world)).ShouldBe(
            1, "an ignored misspelling would apply the change the caller asked to preview");
    }

    [Fact]
    public async Task An_invalid_descriptor_is_422_with_the_validator_s_own_violations()
    {
        await using var world = await ManagedFleet.StartAsync([_dev]);

        var response = await ApplyAsync(world, "{ \"apiVersion\": \"alvo.dev/v1\" }", ifMatch: "\"1\"");

        response.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);
        (await response.ReadProblemTypeAsync()).ShouldBe(AlvoProblemTypes.Validation);
        (await response.ReadJsonObjectAsync())["violations"]!.AsArray().ShouldNotBeEmpty();
        (await RevisionAsync(world)).ShouldBe(1);
    }

    /// <summary>
    /// A blank <c>descriptorJson</c> is the named 422, not a 500.
    /// </summary>
    /// <remarks>
    /// <b>Blank is not null on this surface, and that is the whole point.</b>
    /// <c>DescriptorValidator.Validate</c> opens with <c>ArgumentException.ThrowIfNullOrWhiteSpace</c>, and
    /// <c>Answer</c> catches named refusals only — so a guard written as "null" let a whitespace descriptor
    /// through to family 5, which a shipped host renders as a <b>500</b>. It is the same defect
    /// <c>6c69c63</c> fixed one segment over for a blank project name, and
    /// <c>ManagementApplyBody.ToRequest</c> maps an absent descriptor onto exactly this value, so the two
    /// shapes have to answer alike.
    /// </remarks>
    /// <param name="descriptorJson">A descriptor that is present and says nothing.</param>
    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("\t\n")]
    public async Task A_blank_descriptor_is_refused_rather_than_thrown(string descriptorJson)
    {
        await using var world = await ManagedFleet.StartAsync([_dev]);

        var response = await ApplyAsync(world, descriptorJson, ifMatch: "\"1\"");

        response.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);
        (await response.ReadProblemTypeAsync()).ShouldBe(AlvoProblemTypes.Validation);
        (await RevisionAsync(world)).ShouldBe(1);
    }

    /// <summary>
    /// An administrator sending a blank descriptor gets the same 422, and this is the case the escalation
    /// guard would otherwise have hidden.
    /// </summary>
    /// <remarks>
    /// <b>Written because a fix can be masked by an unrelated guard.</b> With the blank check still written
    /// as "null", a <em>developer</em>'s blank descriptor was answered 403 — the access-change guard read
    /// blank as "declares no access block", saw that differ from the applied one, and refused first. The
    /// 500 was still there for anyone the guard did not stop. A refusal that only holds for callers some
    /// other rule already refuses is not a refusal.
    /// </remarks>
    [Fact]
    public async Task An_administrator_sending_a_blank_descriptor_is_refused_rather_than_thrown()
    {
        await using var world = await ManagedFleet.StartAsync([_owner]);

        var response = await world.SendAsync(
            HttpMethod.Put,
            Path,
            _owner,
            body: Body("   "),
            headers: [new KeyValuePair<string, string>("If-Match", "\"1\"")]);

        response.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);
        (await response.ReadProblemTypeAsync()).ShouldBe(AlvoProblemTypes.Validation);
    }

    /// <summary>A body that names no descriptor at all takes the same path as a blank one.</summary>
    [Fact]
    public async Task An_apply_naming_no_descriptor_is_refused_rather_than_thrown()
    {
        await using var world = await ManagedFleet.StartAsync([_dev]);

        var response = await world.SendAsync(
            HttpMethod.Put,
            Path,
            _dev,
            body: new JsonObject { ["author"] = "the-suite" },
            headers: [new KeyValuePair<string, string>("If-Match", "\"1\"")]);

        response.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);
        (await RevisionAsync(world)).ShouldBe(1);
    }

    [Fact]
    public async Task A_viewer_may_not_apply()
    {
        await using var world = await ManagedFleet.StartAsync([_dev, _ops]);

        var response = await world.SendAsync(
            HttpMethod.Put,
            Path,
            _ops,
            body: Body(await CurrentAsync(world)),
            headers: [new KeyValuePair<string, string>("If-Match", "\"1\"")]);

        response.StatusCode.ShouldBe(
            HttpStatusCode.Forbidden, "spec §3.3: applying is developer, not viewer");
        (await response.ReadProblemTypeAsync()).ShouldBe(AlvoProblemTypes.Forbidden);
    }

    /// <summary>
    /// A <c>developer</c> may not rewrite the <c>access</c> block, because that is deciding who may reach
    /// the backend rather than what the backend is.
    /// </summary>
    /// <remarks>
    /// <b>Spec §3.3:</b> <i>"<c>developer</c> edits what the backend is, <c>admin</c> also decides who may
    /// reach it."</i> <c>access</c> lives inside the descriptor and every accepted apply re-primes the
    /// catalog the management gate reads, so without this guard the one write route turns
    /// <c>ApplyDescriptor</c>'s <c>Developer</c> level into <c>Admin</c> for anyone who edits three lines of
    /// JSON. The last assertion is the half that matters: the caller the old block named is still admitted,
    /// so nothing was escalated rather than merely not reported.
    /// </remarks>
    [Fact]
    public async Task A_developer_may_not_change_who_may_reach_the_project()
    {
        await using var world = await ManagedFleet.StartAsync([_dev, _ops]);

        var response = await ApplyAsync(world, Escalated(await CurrentAsync(world)), ifMatch: "\"1\"");

        response.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await response.ReadProblemTypeAsync()).ShouldBe(AlvoProblemTypes.Forbidden);
        (await RevisionAsync(world)).ShouldBe(1);
        (await world.SendAsync(HttpMethod.Get, Path, _ops)).StatusCode.ShouldBe(
            HttpStatusCode.OK, "the viewer the old access block named is still admitted, so nothing escalated");
    }

    /// <summary>The same refusal on a dry run, so a preview cannot report a plan the apply would refuse.</summary>
    [Fact]
    public async Task A_developer_may_not_preview_a_change_to_who_may_reach_the_project()
    {
        await using var world = await ManagedFleet.StartAsync([_dev]);

        var response = await ApplyAsync(
            world, Escalated(await CurrentAsync(world)), ifMatch: "\"1\"", query: "?dryRun=true");

        response.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
    }

    /// <summary>The other direction: a <c>developer</c> still edits everything that is not <c>access</c>.</summary>
    [Fact]
    public async Task A_developer_may_apply_a_change_that_leaves_the_access_block_alone()
    {
        await using var world = await ManagedFleet.StartAsync([_dev]);

        var response = await ApplyAsync(world, WithExtraField(await CurrentAsync(world)), ifMatch: "\"1\"");

        response.StatusCode.ShouldBe(
            HttpStatusCode.OK, "the guard is about the access block, not about applying at all");
        (await RevisionAsync(world)).ShouldBe(2);
    }

    /// <summary>An administrator may make exactly the change the developer was refused.</summary>
    /// <remarks>
    /// Without this, the guard would be indistinguishable from a blanket ban on editing <c>access</c> over
    /// the API, which is not what §3.3 says.
    /// </remarks>
    [Fact]
    public async Task An_administrator_may_change_who_may_reach_the_project()
    {
        await using var world = await ManagedFleet.StartAsync([_owner]);

        var response = await world.SendAsync(
            HttpMethod.Put,
            Path,
            _owner,
            body: Body(Escalated(await CurrentAsync(world, _owner))),
            headers: [new KeyValuePair<string, string>("If-Match", "\"1\"")]);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    /// <summary>
    /// Re-emitting the same <c>access</c> block with different whitespace is not a change.
    /// </summary>
    /// <remarks>
    /// This is what holds the guard to comparing the <em>parsed</em> blocks. A guard written over raw text
    /// would refuse a developer here for changing nothing, and an editor that pretty-prints on save would
    /// need an administrator for every apply.
    /// </remarks>
    [Fact]
    public async Task A_reformatted_access_block_is_not_a_change()
    {
        await using var world = await ManagedFleet.StartAsync([_dev]);

        var response = await ApplyAsync(
            world, DescriptorEdits.Reformat(await CurrentAsync(world)), ifMatch: "\"1\"");

        response.StatusCode.ShouldBe(HttpStatusCode.OK, "the same block, spelled differently, is the same block");
        (await RevisionAsync(world)).ShouldBe(1, "an unchanged re-apply appends nothing");
    }

    [Fact]
    public async Task What_the_editor_sent_is_what_the_export_returns()
    {
        await using var world = await ManagedFleet.StartAsync([_dev]);
        var sent = WithExtraField(await CurrentAsync(world));

        await ApplyAsync(world, sent, ifMatch: "\"1\"");

        (await CurrentAsync(world)).ShouldBe(sent, "F5 acceptance criterion 3: no config drift");
    }

    private static Task<HttpResponseMessage> ApplyAsync(
        AlvoApiWorld world, string descriptorJson, string ifMatch, bool allowDestructive = false,
        string query = "") =>
        world.SendAsync(
            HttpMethod.Put,
            Path + query,
            _dev,
            body: Body(descriptorJson, allowDestructive),
            headers: [new KeyValuePair<string, string>("If-Match", ifMatch)]);

    private static JsonObject Body(string descriptorJson, bool allowDestructive = false) => new()
    {
        ["descriptorJson"] = descriptorJson,
        ["allowDestructive"] = allowDestructive,
        ["author"] = "the-suite",
        ["reason"] = "a fact",
    };

    private static async Task<string> CurrentAsync(AlvoApiWorld world, TestApiKey? key = null) =>
        (await (await world.SendAsync(HttpMethod.Get, Path, key ?? _dev)).ReadJsonObjectAsync())
            ["descriptorJson"]!.GetValue<string>();

    private static async Task<int> RevisionAsync(AlvoApiWorld world) =>
        (await (await world.SendAsync(HttpMethod.Get, Path, _dev)).ReadJsonObjectAsync())
            ["revision"]!.GetValue<int>();

    private static string WithExtraField(string descriptorJson) =>
        DescriptorEdits.AddOptionalTextField(descriptorJson, entity: "vehicles", field: "nickname");

    private static string WithoutAnEntity(string descriptorJson) =>
        DescriptorEdits.RemoveEntity(descriptorJson, entity: "audits");

    /// <summary>The descriptor with the developer's own role handed administration and the viewer dropped.</summary>
    private static string Escalated(string descriptorJson) =>
        DescriptorEdits.GrantAdminTo(descriptorJson, role: "dispatcher");
}
