using System.Net;
using System.Text;

namespace MMLib.Alvo.Api.Tests.Management;

/// <summary>
/// <c>Idempotency-Key</c> on <c>PUT {m}/projects/{p}/descriptor</c>.
/// </summary>
/// <remarks>
/// <para>
/// <b>What the key buys that <c>If-Match</c> does not.</b> The apply is already <em>at-most-once</em>: a
/// retry names a revision that has moved and is refused with 412, and nothing has to be stored for that to
/// hold. What it is not is <em>attributable</em> — a 412 means both "my own write landed and the response
/// was lost" and "somebody else changed the descriptor", and those two need opposite recoveries. The key
/// converts the first into a replay carrying the revision the first attempt appended.
/// <c>A_retry_without_a_key_is_refused_with_a_412_the_caller_cannot_attribute</c> is the fact that keeps the
/// first one from passing vacuously: it measures the hole.
/// </para>
/// <para>
/// <b>Every rule about the key itself is the port's</b>, so the refusals here are the wordings the Data API
/// already publishes: a blank key, one over <c>AlvoIdempotency.MaxKeyBytes</c> UTF-8 bytes, and a holder
/// with no identity to scope by. A 422 rather than a 401 for the last of them — nothing failed
/// authentication, so a 401 would owe a challenge for a request that never attempted one.
/// </para>
/// </remarks>
public class ManagementIdempotencyTests
{
    /// <summary>A caller the project's <c>access</c> block admits at <c>developer</c>.</summary>
    private static readonly TestApiKey _dev = new("mgmt-dev", ["dispatcher"], ["*:write"]);

    /// <summary>A second caller at <c>admin</c>, so two identities can spend one key.</summary>
    private static readonly TestApiKey _owner = new("mgmt-owner", ["owner"], ["*:write"]);

    [Fact]
    public async Task A_retry_with_the_same_key_and_body_replays_the_first_apply()
    {
        await using var world = await ManagedFleet.StartAsync([_dev]);
        var sent = WithExtraField(await CurrentAsync(world));

        var first = await ApplyAsync(world, sent, key: "k1");
        var retry = await ApplyAsync(world, sent, key: "k1");

        first.StatusCode.ShouldBe(HttpStatusCode.OK);
        retry.StatusCode.ShouldBe(HttpStatusCode.OK, "the retry is the first apply's own outcome, not a race");
        (await retry.ReadJsonObjectAsync())["revision"]!.GetValue<int>().ShouldBe(2);
        (await retry.ReadJsonObjectAsync())["applied"]!.GetValue<bool>().ShouldBeTrue();
        (await RevisionAsync(world)).ShouldBe(2, "a replay appends nothing");
    }

    /// <summary>The hole the key closes, asserted so the fact above cannot pass vacuously.</summary>
    [Fact]
    public async Task A_retry_without_a_key_is_refused_with_a_412_the_caller_cannot_attribute()
    {
        await using var world = await ManagedFleet.StartAsync([_dev]);
        var sent = WithExtraField(await CurrentAsync(world));

        await ApplyAsync(world, sent, key: null);
        var retry = await ApplyAsync(world, sent, key: null);

        retry.StatusCode.ShouldBe(
            HttpStatusCode.PreconditionFailed,
            "without a key the caller cannot tell their own landed write from somebody else's");
        (await RevisionAsync(world)).ShouldBe(2);
    }

    [Fact]
    public async Task The_same_key_with_a_different_body_is_409_idempotency_conflict()
    {
        await using var world = await ManagedFleet.StartAsync([_dev]);
        var current = await CurrentAsync(world);

        await ApplyAsync(world, WithExtraField(current), key: "k1");
        var second = await ApplyAsync(world, WithAnotherField(current), key: "k1");

        second.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        (await second.ReadProblemTypeAsync()).ShouldBe(AlvoProblemTypes.IdempotencyConflict);
        (await RevisionAsync(world)).ShouldBe(2, "a reused key is refused before anything is planned");
    }

    [Fact]
    public async Task An_anonymous_caller_sending_a_key_is_422_rather_than_401()
    {
        await using var world = await ManagedOpen.StartAsync();

        var response = await world.SendAsync(
            HttpMethod.Put,
            ManagedOpen.Routes + "/descriptor",
            key: null,
            body: ManagementApplyWorld.Body(await ManagedOpen.CurrentAsync(world)),
            headers: ManagementApplyWorld.Headers("\"1\"", "k1"));

        response.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);
        (await response.ReadProblemTypeAsync()).ShouldBe(AlvoProblemTypes.Validation);
        (await response.ReadProblemDetailAsync()).ShouldContain(
            "identity", Case.Insensitive, "the refusal names why a key needs one");
    }

    [Fact]
    public async Task A_key_over_the_byte_bound_is_refused_rather_than_truncated()
    {
        await using var world = await ManagedFleet.StartAsync([_dev]);

        var response = await ApplyAsync(
            world, WithExtraField(await CurrentAsync(world)), key: OverLongKey());

        response.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);
        (await response.ReadProblemTypeAsync()).ShouldBe(AlvoProblemTypes.Validation);
        (await RevisionAsync(world)).ShouldBe(1);
    }

    /// <summary>A key counted in characters would let this one through; the bound is bytes.</summary>
    [Fact]
    public async Task A_key_within_the_character_count_but_over_the_byte_bound_is_still_refused()
    {
        await using var world = await ManagedFleet.StartAsync([_dev]);

        var response = await ApplyAsync(
            world, WithExtraField(await CurrentAsync(world)), key: new string('é', 200));

        response.StatusCode.ShouldBe(
            HttpStatusCode.UnprocessableEntity, "200 characters is 400 UTF-8 bytes, past the port's 255");
        (await RevisionAsync(world)).ShouldBe(1);
    }

    [Fact]
    public async Task A_dry_run_may_not_carry_a_key()
    {
        await using var world = await ManagedFleet.StartAsync([_dev]);

        var response = await ApplyAsync(
            world, WithExtraField(await CurrentAsync(world)), key: "k1", query: "?dryRun=true");

        response.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);
        (await response.ReadProblemTypeAsync()).ShouldBe(AlvoProblemTypes.Validation);
        (await RevisionAsync(world)).ShouldBe(1);
    }

    /// <summary>
    /// A dry run's refused key is not spent, so the caller's real apply is still an apply.
    /// </summary>
    /// <remarks>
    /// This is what the refusal is <em>for</em>: filing a record for a request that changed nothing would turn
    /// the caller's later real apply into a replay of a preview, reporting a revision nobody appended.
    /// </remarks>
    [Fact]
    public async Task A_key_refused_on_a_dry_run_is_not_spent()
    {
        await using var world = await ManagedFleet.StartAsync([_dev]);
        var sent = WithExtraField(await CurrentAsync(world));
        await ApplyAsync(world, sent, key: "k1", query: "?dryRun=true");

        var applied = await ApplyAsync(world, sent, key: "k1");

        applied.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await RevisionAsync(world)).ShouldBe(2, "the real apply appended, rather than replaying a preview");
    }

    [Fact]
    public async Task A_repeated_idempotency_key_header_is_refused_rather_than_disambiguated()
    {
        await using var world = await ManagedFleet.StartAsync([_dev]);

        var response = await world.SendAsync(
            HttpMethod.Put,
            ManagementApplyWorld.DescriptorPath,
            _dev,
            body: ManagementApplyWorld.Body(WithExtraField(await CurrentAsync(world))),
            headers:
            [
                new KeyValuePair<string, string>("If-Match", "\"1\""),
                new KeyValuePair<string, string>("Idempotency-Key", "k1"),
                new KeyValuePair<string, string>("Idempotency-Key", "k2"),
            ]);

        response.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);
        (await RevisionAsync(world)).ShouldBe(1, "two values are two keys and one record can hold one");
    }

    [Fact]
    public async Task Two_callers_may_use_the_same_key_without_colliding()
    {
        await using var world = await ManagedFleet.StartAsync([_dev, _owner]);

        await ApplyAsync(world, WithExtraField(await CurrentAsync(world)), key: "shared");
        var second = await ManagementApplyWorld.ApplyAsync(
            world, _owner, WithAnotherField(await CurrentAsync(world)), ifMatch: "\"2\"", idempotencyKey: "shared");

        second.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await RevisionAsync(world)).ShouldBe(3, "neither caller replayed the other's record");
    }

    private static Task<HttpResponseMessage> ApplyAsync(
        AlvoApiWorld world, string descriptorJson, string? key, string query = "") =>
        ManagementApplyWorld.ApplyAsync(
            world, _dev, descriptorJson, ifMatch: "\"1\"", query: query, idempotencyKey: key);

    private static Task<string> CurrentAsync(AlvoApiWorld world) =>
        ManagementApplyWorld.CurrentAsync(world, _dev);

    private static Task<int> RevisionAsync(AlvoApiWorld world) =>
        ManagementApplyWorld.RevisionAsync(world, _dev);

    private static string WithExtraField(string descriptorJson) =>
        DescriptorEdits.AddOptionalTextField(descriptorJson, entity: "vehicles", field: "nickname");

    private static string WithAnotherField(string descriptorJson) =>
        DescriptorEdits.AddOptionalTextField(descriptorJson, entity: "depots", field: "postcode");

    /// <summary>One byte past <c>AlvoIdempotency.MaxKeyBytes</c>, in ASCII so bytes and characters agree.</summary>
    private static string OverLongKey() =>
        new('k', Encoding.UTF8.GetByteCount(new string('k', Data.AlvoIdempotency.MaxKeyBytes)) + 1);
}
