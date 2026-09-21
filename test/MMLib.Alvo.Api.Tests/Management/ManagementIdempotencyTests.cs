using Microsoft.Extensions.DependencyInjection;
using MMLib.Alvo.Management;
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

    /// <summary>
    /// A replay says <b>on the wire</b> that it is one, because that is the only place the HTTP caller reads.
    /// </summary>
    /// <remarks>
    /// Without it the response is indistinguishable from a fresh apply that changed nothing:
    /// <c>applied: true</c>, <c>revision: 2</c>, and a <c>plan</c> whose <c>isEmpty</c> is <c>true</c> — which
    /// the published doc defines as "the descriptor changes nothing about the schema", and which is not what
    /// it means here. A dashboard rendering a diff off that response would show "no changes" for a migration
    /// that really ran. XML remarks do not reach this caller; a field does.
    /// </remarks>
    [Fact]
    public async Task A_replay_says_on_the_wire_that_it_is_a_replay()
    {
        await using var world = await ManagedFleet.StartAsync([_dev]);
        var sent = WithExtraField(await CurrentAsync(world));

        var first = await (await ApplyAsync(world, sent, key: "k1")).ReadJsonObjectAsync();
        var retry = await (await ApplyAsync(world, sent, key: "k1")).ReadJsonObjectAsync();

        first["replayed"]!.GetValue<bool>().ShouldBeFalse("the first apply really ran the migration");
        retry["replayed"]!.GetValue<bool>().ShouldBeTrue();
        retry["plan"]!["isEmpty"]!.GetValue<bool>().ShouldBeTrue(
            "a replay ran no migration, and 'replayed' is what tells the caller why the plan is empty");
    }

    /// <summary>An ordinary apply that changed nothing is still not a replay.</summary>
    /// <remarks>
    /// The other direction, so `replayed` cannot be a second spelling of `plan.isEmpty`: a re-apply of the
    /// current descriptor plans empty and is nonetheless a real request this instance served.
    /// </remarks>
    [Fact]
    public async Task An_apply_that_planned_nothing_is_not_reported_as_a_replay()
    {
        await using var world = await ManagedFleet.StartAsync([_dev]);

        var body = await (await ApplyAsync(world, await CurrentAsync(world), key: null)).ReadJsonObjectAsync();

        body["plan"]!["isEmpty"]!.GetValue<bool>().ShouldBeTrue();
        body["replayed"]!.GetValue<bool>().ShouldBeFalse();
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

    /// <summary>
    /// A record this deployment cannot write does not turn an apply that <b>landed</b> into a failure.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The record is filed after the migration and the revision have already committed, so a failure there
    /// is a failure of bookkeeping for a write that is done. Reporting it to the caller tells them their
    /// apply failed when it succeeded — which is the exact confusion this feature exists to remove,
    /// inverted, and strictly worse than the 412 they would have got with no key at all.
    /// </para>
    /// <para>
    /// The documented crash window covers a process that <em>dies</em> there. It does not cover a store that
    /// answers, and this is that case.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task A_record_that_cannot_be_written_does_not_fail_an_apply_that_landed()
    {
        await using var world = await StartWithAsync(new FaultingIdempotencyStore());

        var response = await ApplyAsync(world, WithExtraField(await CurrentAsync(world)), key: "k1");

        response.StatusCode.ShouldBe(HttpStatusCode.OK, "the migration and the revision both committed");
        (await response.ReadJsonObjectAsync())["revision"]!.GetValue<int>().ShouldBe(2);
        (await RevisionAsync(world)).ShouldBe(2);
    }

    /// <summary>The unrecorded write's retry is the pre-batch 412, which is the documented fallback.</summary>
    [Fact]
    public async Task An_apply_whose_record_was_lost_falls_back_to_the_412_it_had_before()
    {
        await using var world = await StartWithAsync(new FaultingIdempotencyStore());
        var sent = WithExtraField(await CurrentAsync(world));
        await ApplyAsync(world, sent, key: "k1");

        var retry = await ApplyAsync(world, sent, key: "k1");

        retry.StatusCode.ShouldBe(HttpStatusCode.PreconditionFailed);
        (await RevisionAsync(world)).ShouldBe(2, "the retry appended nothing on top of the write that landed");
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

    /// <summary>The <c>managed-fleet</c> world with <paramref name="store"/> in place of the registered one.</summary>
    /// <remarks>
    /// Registered after <c>AddAlvo</c>, so it wins the single-service resolve the provider's own
    /// <c>TryAddSingleton</c> left open — which is also the substitution the port documents as supported.
    /// </remarks>
    /// <param name="store">The store this world's management surface writes through.</param>
    private static Task<AlvoApiWorld> StartWithAsync(IManagementIdempotencyStore store) =>
        AlvoApiWorld.FromDescriptorAsync(
            ManagedFleet.Descriptor,
            [_dev],
            new AlvoApiWorldSetup(
                MapBeforePriming: true,
                MapManagementApi: true,
                ConfigureServicesAfterAlvo: services => services.AddSingleton(store)));

    /// <summary>A store that finds nothing and refuses every record, standing in for a broken one.</summary>
    /// <remarks>
    /// <see cref="FindAsync"/> answers rather than throws, because a store that could not be read at all
    /// would refuse the request <em>before</em> the write and is a different fact: this one exists to put
    /// the failure strictly after the commit, which is the window nothing covered.
    /// </remarks>
    private sealed class FaultingIdempotencyStore : IManagementIdempotencyStore
    {
        /// <inheritdoc/>
        public Task<int?> FindAsync(
            string key, string scope, string fingerprint, CancellationToken ct = default) =>
            Task.FromResult<int?>(null);

        /// <inheritdoc/>
        public Task RecordAsync(
            string key, string scope, string fingerprint, int revision, CancellationToken ct = default) =>
            throw new InvalidOperationException("This store cannot write.");
    }
}
