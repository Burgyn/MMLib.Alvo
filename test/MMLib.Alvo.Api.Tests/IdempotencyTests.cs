using System.Globalization;
using System.Net;
using System.Text.Json.Nodes;

namespace MMLib.Alvo.Api.Tests;

/// <summary>
/// <c>Idempotency-Key</c> over HTTP: a retried create is answered with the first create's row, a key reused
/// for a <em>different</em> request is a 409, and neither ever writes a second row.
/// </summary>
/// <remarks>
/// <para>
/// §2.1 asks for this because of who Alvo's callers are — "pre agentov a mobilné retry je kľúčový". An agent
/// that times out and retries, or a mobile client that resends on a flaky link, must not create a duplicate
/// row; and it must be able to retry <em>without</em> knowing whether the first attempt landed, which is
/// exactly what a key buys.
/// </para>
/// <para>
/// <b>The dangerous direction is not the 409, it is the replay.</b> The port fails closed on the entity axis —
/// a fingerprint that omitted the entity makes a replay re-read a row id that is not there, and the caller
/// gets a not-found. A fingerprint too coarse <em>within</em> one entity fails <em>open</em>: the second,
/// different request matches, is answered with the first request's row, and nothing anywhere raises. So
/// <see cref="Two_creates_differing_only_in_a_field_the_fingerprint_must_cover_are_a_conflict"/> is the
/// load-bearing fact in this file, and it is written over every field the entity declares rather than over a
/// field somebody chose.
/// </para>
/// <para>
/// Every fact drives real HTTP against a real store, and every "no second row" claim is read straight from the
/// table with <see cref="AlvoApiWorld.CountRowsAsync"/> rather than from a list — a list is filtered by the
/// caller's own policy, so it cannot tell "no row was written" from "you cannot see it".
/// </para>
/// </remarks>
public sealed class IdempotencyTests
{
    private static readonly TestApiKey _admin = new("admin-key", ["admin", "authenticated"], ["*:read", "*:write"]);

    /// <summary>
    /// The retry an agent actually sends: same key, same body, and the answer is the first create's row —
    /// including when the retry reformatted its own JSON.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The reformatted third request is the fingerprint's canonicalization, and it is the half a digest over the
    /// raw request bytes fails: it carries the same two members in the opposite order with different whitespace,
    /// which is a difference two runs of one serializer are entitled to produce, and no HTTP client promises
    /// byte-identical retries. Under a raw-bytes digest it is a 409 — the caller is told they reused a key for a
    /// different request when they did not.
    /// </para>
    /// <para>
    /// It goes through <see cref="AlvoApiWorld.SendRawAsync"/> because a <see cref="JsonObject"/> cannot express
    /// "the same body, spelled differently" — the client would serialize it into whatever shape it likes.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task A_repeated_post_with_the_same_key_and_body_returns_the_first_result()
    {
        await using var world = await AlvoApiWorld.VehicleRegistryAsync([_admin]);
        var body = new JsonObject { ["name"] = "Retry Ltd", ["email"] = "retry@example.com" };

        using var first = await PostAsync(world, "owners", body, "retry-1");
        using var retried = await PostAsync(world, "owners", body, "retry-1");
        using var reformatted = await world.SendRawAsync(
            HttpMethod.Post,
            "/api/owners",
            _admin,
            content: AlvoApiWorld.RawJson("{\n  \"email\" :  \"retry@example.com\",\n  \"name\": \"Retry Ltd\"\n}"),
            headers: Key("retry-1"));

        first.StatusCode.ShouldBe(HttpStatusCode.Created, await first.ReadTextAsync());
        retried.StatusCode.ShouldBe(HttpStatusCode.Created, await retried.ReadTextAsync());
        reformatted.StatusCode.ShouldBe(
            HttpStatusCode.Created,
            $"a retry may reorder and re-indent its own JSON: {await reformatted.ReadTextAsync()}");
        (await IdOfAsync(retried)).ShouldBe(await IdOfAsync(first), "a replay must answer with the first row");
        (await IdOfAsync(reformatted)).ShouldBe(
            await IdOfAsync(first),
            "the fingerprint is over the canonical body, not over the bytes the caller happened to send");
    }

    /// <summary>
    /// The promise itself: the second create writes nothing. Read off the table, because the response alone
    /// cannot tell a replay from a second row that happens to look the same.
    /// </summary>
    [Fact]
    public async Task A_repeated_post_with_the_same_key_creates_no_second_row()
    {
        await using var world = await AlvoApiWorld.VehicleRegistryAsync([_admin]);
        var body = new JsonObject { ["name"] = "Once Ltd" };

        using var first = await PostAsync(world, "owners", body, "once-1");
        using var retried = await PostAsync(world, "owners", body, "once-1");

        first.StatusCode.ShouldBe(HttpStatusCode.Created, await first.ReadTextAsync());
        retried.StatusCode.ShouldBe(HttpStatusCode.Created, await retried.ReadTextAsync());
        (await world.CountRowsAsync("owners")).ShouldBe(1, "one key, one row — whatever the second response said");
    }

    /// <summary>
    /// A replay is indistinguishable from the response it replays: same id, same <c>Location</c>, and the same
    /// <c>ETag</c> — so a caller who retried can still write conditionally without re-reading.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The tag is the part that could quietly go wrong. The port answers a replay by <em>re-reading</em> the
    /// recorded row rather than by returning a stored response, so an implementation that re-stamped the audit
    /// columns on the way through — or minted the tag from a clock — would answer 201 with the right id and a
    /// tag no <c>If-Match</c> the first caller holds would ever match.
    /// </para>
    /// <para>
    /// Both tags are also compared against the one a plain <c>GET</c> hands out, because "the two 201s agree"
    /// can be satisfied by two responses that are equally wrong.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task The_replayed_response_carries_the_same_id_and_the_same_etag()
    {
        await using var world = await AlvoApiWorld.VehicleRegistryAsync([_admin]);
        var body = new JsonObject { ["name"] = "Tagged Ltd" };

        using var first = await PostAsync(world, "owners", body, "tagged-1");
        using var retried = await PostAsync(world, "owners", body, "tagged-1");
        var id = await IdOfAsync(first);
        using var read = await world.SendAsync(HttpMethod.Get, $"/api/owners/{id}", _admin);

        retried.StatusCode.ShouldBe(HttpStatusCode.Created, await retried.ReadTextAsync());
        (await IdOfAsync(retried)).ShouldBe(id);
        retried.Headers.Location!.ToString().ShouldBe(first.Headers.Location!.ToString());
        retried.ETagOf().ShouldBe(first.ETagOf(), "a replay must hand back the version the first create stored");
        read.ETagOf().ShouldBe(
            first.ETagOf(), "and that version must be the row's, or both 201s agree on a tag nothing accepts");
    }

    /// <summary>
    /// The same key with a different body is a 409 that says to send a fresh key — never the first row, and
    /// never a second row.
    /// </summary>
    /// <remarks>
    /// Answering with the first row would report success for a create that never happened: the caller would
    /// hold an id for a row that does not contain what they sent. Creating a second row would break the promise
    /// the key exists to make. So the row count is asserted as well as the status — a 409 raised <em>after</em>
    /// the write would satisfy the status assertion alone.
    /// </remarks>
    [Fact]
    public async Task The_same_key_with_a_different_body_is_409_naming_the_conflict()
    {
        await using var world = await AlvoApiWorld.VehicleRegistryAsync([_admin]);

        using var first = await PostAsync(world, "owners", new JsonObject { ["name"] = "First Ltd" }, "reused-1");
        using var second = await PostAsync(world, "owners", new JsonObject { ["name"] = "Second Ltd" }, "reused-1");

        first.StatusCode.ShouldBe(HttpStatusCode.Created, await first.ReadTextAsync());
        second.StatusCode.ShouldBe(HttpStatusCode.Conflict, await second.ReadTextAsync());
        (await second.ReadProblemTypeAsync()).ShouldBe(AlvoProblemTypes.IdempotencyConflict);
        (await second.ReadProblemDetailAsync()).ShouldContain(
            "fresh key", Case.Sensitive, "§0 principle 4: the refusal must name what fixes it");
        (await world.CountRowsAsync("owners")).ShouldBe(1, "a refused create must not have written a row");
    }

    /// <summary>
    /// One key, two entities: a 409, because the fingerprint covers the entity — not a replay, and not a
    /// second row under one key.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The two bodies are byte-identical, and the first version of this fact was worthless because they were
    /// not.</b> Written over <c>owners</c> and <c>vehicles</c> — whose valid bodies necessarily differ — it
    /// passed with the entity and the route <em>removed from the digest entirely</em>, because the bodies alone
    /// still made the fingerprints differ. Measured, not reasoned: that mutant killed no fact in the file. Two
    /// entities of the same declared shape are what make the entity axis the only thing under test.
    /// </para>
    /// <para>
    /// Three outcomes are distinguishable and only one is right. With the entity in the digest the fingerprints
    /// differ and this is a <b>409</b>. With it out, they match, the port replays — re-reading the recorded
    /// <c>notes</c> row id under <c>ledgers</c>, which answers a not-found or a policy refusal depending on the
    /// second entity's own rules, never the row the caller asked about. That is the fail-closed direction the
    /// port promises, and it is still the wrong answer. And an implementation that recorded the key per entity
    /// would answer <b>201</b>, which is a second row under one key.
    /// </para>
    /// <para>
    /// <c>masked-notes</c> is the descriptor because <c>notes</c> and <c>ledgers</c> declare the same fields, so
    /// one body is valid for both.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task The_same_key_on_a_different_entity_is_409_not_a_replay()
    {
        var key = new TestApiKey(
            "both-key", ["authenticated"], ["notes:read", "notes:write", "ledgers:read", "ledgers:write"]);
        await using var world = await AlvoApiWorld.FromDescriptorAsync("masked-notes.alvo.json", [key]);
        var body = new JsonObject { ["title"] = "Crossed" };

        using var note = await world.SendAsync(
            HttpMethod.Post, "/api/notes", key, body: body, headers: Key("crossed-1"));
        using var ledger = await world.SendAsync(
            HttpMethod.Post, "/api/ledgers", key, body: body, headers: Key("crossed-1"));

        note.StatusCode.ShouldBe(HttpStatusCode.Created, await note.ReadTextAsync());
        ledger.StatusCode.ShouldBe(
            HttpStatusCode.Conflict,
            $"the same key on another entity is a different request, not a replay: {await ledger.ReadTextAsync()}");
        (await ledger.ReadProblemTypeAsync()).ShouldBe(AlvoProblemTypes.IdempotencyConflict);
        (await world.CountRowsAsync("ledgers")).ShouldBe(0, "the refused create must not have written a ledger");
    }

    /// <summary>
    /// A key past the configured bound is refused with 422 rather than shortened, and the bound is the one the
    /// host configured.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Truncating is the failure this prevents</b>, and it is silent: two keys that differ only past the cut
    /// become one, so the second create is answered with the first create's row. Refusing tells the caller
    /// instead, and the message names the bound so an agent can shorten its own key rather than guess.
    /// </para>
    /// <para>
    /// The bound is configured down to 8 rather than measured at the default 255, so a build that hard-coded the
    /// number fails: <see cref="AlvoApiOptions.MaxIdempotencyKeyLength"/> has to be what is read. A key of
    /// exactly the bound is accepted in the same fact, because "refuse everything longer than zero" passes any
    /// one-sided version of this.
    /// </para>
    /// <para>
    /// The last request pairs the over-long key with a body that would earn a 422 of its own, and requires the
    /// <em>key's</em> diagnosis. Both answers are 422, so only the slug can tell them apart — and an
    /// implementation that read the header after validating the body would send an agent to fix a payload that
    /// was never the problem.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task A_key_longer_than_the_allowed_maximum_is_422()
    {
        const int bound = 8;
        await using var world = await AlvoApiWorld.VehicleRegistryAsync(
            [_admin], new AlvoApiWorldSetup(ConfigureApi: api => api.MaxIdempotencyKeyLength = bound));
        var body = new JsonObject { ["name"] = "Bounded Ltd" };

        using var atTheBound = await PostAsync(world, "owners", body, new string('k', bound));
        using var pastIt = await PostAsync(world, "owners", body, new string('k', bound + 1));
        using var pastItWithABadBody = await PostAsync(
            world, "owners", new JsonObject { ["name"] = new string('n', 200) }, new string('k', bound + 1));

        atTheBound.StatusCode.ShouldBe(
            HttpStatusCode.Created, $"a key of exactly the bound must be usable: {await atTheBound.ReadTextAsync()}");
        pastIt.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);
        (await pastIt.ReadProblemTypeAsync()).ShouldBe(AlvoProblemTypes.MalformedQuery);
        (await pastIt.ReadProblemDetailAsync()).ShouldContain(
            bound.ToString(CultureInfo.InvariantCulture),
            Case.Sensitive,
            "the refusal must name the bound the host configured, or the caller cannot act on it");
        (await pastItWithABadBody.ReadProblemTypeAsync()).ShouldBe(
            AlvoProblemTypes.MalformedQuery, "an unusable key outranks whatever the body says");
        (await world.CountRowsAsync("owners")).ShouldBe(1, "only the accepted key may have written a row");
        new AlvoApiOptions().MaxIdempotencyKeyLength.ShouldBe(
            255, "the default is what the record's own storage is sized for — see the option's remarks");
    }

    /// <summary>
    /// A header this API cannot turn into exactly one key is refused, whichever way it is unusable: sent twice,
    /// or blank.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Beyond the brief, and for the same reason a multi-tag <c>If-Match</c> is refused: two field lines are two
    /// keys and a create can be recorded under one, so picking either answers a question the caller did not ask.
    /// A blank key is worse than useless — every caller who sent one would share it, which is precisely the
    /// shared key space scoping the record per user exists to remove.
    /// </para>
    /// <para>
    /// The duplicate cannot be expressed through a dictionary, which is why
    /// <see cref="AlvoApiWorld.SendRawAsync"/> takes pairs.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task A_header_that_is_not_one_usable_key_is_refused_rather_than_resolved()
    {
        await using var world = await AlvoApiWorld.VehicleRegistryAsync([_admin]);
        var body = new JsonObject { ["name"] = "Ambiguous Ltd" };

        using var twice = await world.SendAsync(
            HttpMethod.Post, "/api/owners", _admin, body: body, headers: [Pair("a"), Pair("b")]);
        using var blank = await PostAsync(world, "owners", body, "   ");

        twice.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity, await twice.ReadTextAsync());
        (await twice.ReadProblemTypeAsync()).ShouldBe(AlvoProblemTypes.MalformedQuery);
        blank.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity, await blank.ReadTextAsync());
        (await world.CountRowsAsync("owners")).ShouldBe(0, "neither refused create may have written a row");
    }

    /// <summary>
    /// Two tenants may hold the same key at once: a record is scoped to the tenant as well as the acting user,
    /// so one tenant's retry can never be answered with the other tenant's row.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Both callers are the same user</b>, and that is what makes the fact about tenancy. With two different
    /// users, dropping the tenant from the record's identity still leaves two distinct scopes, so the fact would
    /// pass over a store that ignored the tenant entirely — which is the bug it exists to catch. One user in two
    /// tenants leaves the tenant as the only thing keeping the two keys apart.
    /// </para>
    /// <para>
    /// <b>The two bodies differ in exactly one member, and they have to.</b> A create on a tenant-scoped entity
    /// states its own <c>tenant_id</c> — the tenant guard's <c>WITH CHECK</c> refuses one that does not — so
    /// there is no way to send two byte-identical bodies from two tenants. That decides <em>how</em> this fact
    /// fails rather than whether it does: with the tenant dropped from the record's identity the second request
    /// is a 409 (same key, same scope, different fingerprint) instead of a replay of the first tenant's row.
    /// Both are failures of the assertion below; the replay would have been the worse one, and it is not
    /// reachable over a scoped entity.
    /// </para>
    /// <para>
    /// <b>The third request is what keeps this from passing on a build that ignores the header entirely.</b>
    /// Two tenants getting two rows is also what happens when nothing is deduplicated at all — measured: with
    /// the header unread, every other fact in this file fails and this one stayed green. Tenant A resending its
    /// own body under its own key has to be a replay, which is only true if the key is honoured <em>and</em>
    /// scoped.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task Two_tenants_may_use_the_same_key_without_colliding()
    {
        var user = Guid.NewGuid();
        var scopes = new[] { "notes:read", "notes:write" };
        var tenantA = new TestApiKey("tenant-a", ["authenticated"], scopes, Guid.NewGuid()) { User = user };
        var tenantB = new TestApiKey("tenant-b", ["authenticated"], scopes, Guid.NewGuid()) { User = user };
        await using var world = await AlvoApiWorld.TenantNotesAsync([tenantA, tenantB]);

        using var inA = await world.SendAsync(
            HttpMethod.Post, "/api/notes", tenantA, body: Note(tenantA), headers: Key("shared-1"));
        using var inB = await world.SendAsync(
            HttpMethod.Post, "/api/notes", tenantB, body: Note(tenantB), headers: Key("shared-1"));
        using var inAAgain = await world.SendAsync(
            HttpMethod.Post, "/api/notes", tenantA, body: Note(tenantA), headers: Key("shared-1"));

        inA.StatusCode.ShouldBe(HttpStatusCode.Created, await inA.ReadTextAsync());
        inB.StatusCode.ShouldBe(HttpStatusCode.Created, await inB.ReadTextAsync());
        (await IdOfAsync(inB)).ShouldNotBe(
            await IdOfAsync(inA), "a key is the caller's own opaque string — two tenants using '1' are two requests");
        (await IdOfAsync(inAAgain)).ShouldBe(
            await IdOfAsync(inA),
            "and tenant A's own retry must still replay, or the key is simply not being honoured");
        (await world.CountRowsAsync("notes")).ShouldBe(2, "both tenants' rows must exist, and no third");
    }

    /// <summary>
    /// Ten requests carrying one key, in flight together, produce one row and one record — and every one of
    /// them is answered with that row.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>What this proves, and what it does not — measured, not assumed.</b> It proves the <em>outcome</em>
    /// over real HTTP: ten 201s, one id, one row in the entity's table and one row in the idempotency table.
    /// It does <b>not</b> prove which mechanism produced that outcome. Instrumenting the run showed
    /// <em>exactly one</em> <c>INSERT INTO "owners"</c> attempted across all ten requests, so on this world the
    /// nine losers never raced the insert at all: SQLite's shared cache serialized them and each found the
    /// record the winner had already committed. The record's <c>PRIMARY KEY (idempotency_key, scope)</c> — the
    /// constraint that actually decides a real race — is therefore <em>not</em> what this fact exercises. That
    /// claim belongs to the port's own suite on real PostgreSQL
    /// (<c>Two_concurrent_creates_with_one_idempotency_key_produce_exactly_one_row</c>), where deleting the
    /// <c>PRIMARY KEY</c> clause makes it fail. What is left here, and is worth having, is that ten in-flight
    /// requests under one key are all answered with the one row rather than nine of them failing or forking.
    /// </para>
    /// <para>
    /// The idempotency table's row count is asserted as well as the entity's, because they are two different
    /// claims: one row with two records would mean two winners agreed by accident, and a record committed
    /// without its row would answer every later replay with an id that never existed.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task Ten_concurrent_posts_with_one_key_create_exactly_one_row()
    {
        await using var world = await AlvoApiWorld.VehicleRegistryAsync([_admin]);
        var body = new JsonObject { ["name"] = "Contended Ltd" };

        var responses = await Task.WhenAll(
            Enumerable.Range(0, 10).Select(_ => PostAsync(world, "owners", body, "contended-1")));

        try
        {
            var ids = new List<Guid>();
            foreach (var response in responses)
            {
                response.StatusCode.ShouldBe(HttpStatusCode.Created, await response.ReadTextAsync());
                ids.Add(await IdOfAsync(response));
            }

            ids.Distinct().Count().ShouldBe(1, "every caller must be answered with the one row that was created");
            (await world.CountRowsAsync("owners")).ShouldBe(1, "ten requests, one key, one row");
            (await world.CountRowsAsync("alvo_idempotency")).ShouldBe(1, "and one record, committed by one winner");
        }
        finally
        {
            foreach (var response in responses)
            {
                response.Dispose();
            }
        }
    }

    /// <summary>
    /// Without the header, two identical creates are two rows — a create is not deduplicated by looking like
    /// one that came before it.
    /// </summary>
    /// <remarks>
    /// The inverse mistake, and it is a real one: an implementation that defaulted the key to something derived
    /// from the body — a hash of it, say — would pass every other fact in this file and silently refuse a caller
    /// the second of two legitimately identical rows. Deduplication is something a caller asks for by sending a
    /// key, never something the server decides for them.
    /// </remarks>
    [Fact]
    public async Task A_post_without_the_header_is_not_deduplicated()
    {
        await using var world = await AlvoApiWorld.VehicleRegistryAsync([_admin]);
        var body = new JsonObject { ["name"] = "Twin Ltd" };

        using var first = await world.SendAsync(HttpMethod.Post, "/api/owners", _admin, body: body);
        using var second = await world.SendAsync(HttpMethod.Post, "/api/owners", _admin, body: body);

        first.StatusCode.ShouldBe(HttpStatusCode.Created, await first.ReadTextAsync());
        second.StatusCode.ShouldBe(HttpStatusCode.Created, await second.ReadTextAsync());
        (await IdOfAsync(second)).ShouldNotBe(
            await IdOfAsync(first), "two identical creates without a key are two rows the caller asked for");
        (await world.CountRowsAsync("owners")).ShouldBe(2);
    }

    /// <summary>
    /// The fact the port cannot hold: two creates differing in exactly one field, under one key, are a 409 —
    /// for <b>every</b> field the entity declares.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A fingerprint that drops a field fails open. The second request matches the stored fingerprint, so it is
    /// answered with the first request's row, no exception is raised anywhere, and both responses are a
    /// perfectly ordinary 201 — the caller ends up holding an id for a row that does not contain what they sent.
    /// The HTTP layer computes the fingerprint, so no layer below can notice.
    /// </para>
    /// <para>
    /// <b>The field list comes from the descriptor, not from this file.</b> A hand-written table cannot notice
    /// itself losing an entry, and it cannot notice the entity gaining a field — the two ways this fact would
    /// quietly stop covering what its name claims. So the names are read out of
    /// <c>examples/vehicle-registry/vehicles.alvo.json</c> and required to match the variants below exactly: add
    /// a field to <c>inspections</c> and this fails until the variant exists.
    /// </para>
    /// <para>
    /// <c>inspections</c> is the entity because it declares five fields of five different types — a reference, a
    /// string, a date, a boolean and free text — and no unique constraint, so every pair below can seed its own
    /// base row without colliding with the last one.
    /// </para>
    /// <para>
    /// The replay control at the top is what stops this from passing for the wrong reason. A fingerprint over
    /// something per-request — a timestamp, a fresh GUID — makes <em>every</em> pair a 409, so the file's most
    /// important fact would be green while idempotency was entirely broken. The control requires that the same
    /// body twice under one key is still a replay.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task Two_creates_differing_only_in_a_field_the_fingerprint_must_cover_are_a_conflict()
    {
        await using var world = await AlvoApiWorld.VehicleRegistryAsync([_admin]);
        var vehicles = await SeedTwoVehiclesAsync(world);
        var variants = InspectionVariants(vehicles);

        variants.Keys.Order(StringComparer.Ordinal).ShouldBe(
            DeclaredFields("inspections").Order(StringComparer.Ordinal),
            "every field the entity declares needs a variant, or this fact stops covering the field it lost");
        await AReplayIsStillAReplayAsync(world, Inspection(vehicles[0]));

        foreach (var (field, variant) in variants)
        {
            await DifferingInOneFieldIsAConflictAsync(world, field, Inspection(vehicles[0]), variant);
        }
    }

    /// <summary>
    /// An anonymous caller cannot hold a key at all — every anonymous caller shares one reserved identity, so
    /// there is no scope to record one under — and the refusal is a <b>422</b> with a fix, not a 401.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Nothing failed authentication here: no credential was presented and rejected. A 401 would owe a
    /// <c>WWW-Authenticate</c> challenge for a request that never attempted to authenticate, and would blur the
    /// anonymous-versus-unusable-credential line the auth filter keeps disjoint. What the caller sent is a
    /// well-formed request asking for a facility that needs a stable identity, which is the port's
    /// malformed-request family.
    /// </para>
    /// <para>
    /// The baseline is what makes the refusal attributable to the header: the same anonymous create
    /// <em>without</em> it is served, so this descriptor really does let an anonymous caller write. Without that,
    /// a 422 could just as well be a policy that refuses everyone.
    /// </para>
    /// <para>
    /// The third request pairs the header with a body that would earn its own 422, and still requires the key's
    /// diagnosis — an implementation that read the header only after validating the body would tell an agent to
    /// fix a payload that was never the problem, and the agent would fix it, resend, and be refused again.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task An_anonymous_caller_sending_the_header_is_refused_with_a_fix_suggestion()
    {
        await using var world = await AlvoApiWorld.FromDescriptorAsync("masked-notes.alvo.json");
        var body = new JsonObject { ["title"] = "Anonymous" };

        using var withKey = await world.SendAsync(
            HttpMethod.Post, "/api/notes", body: body, headers: Key("anonymous-1"));
        using var withoutKey = await world.SendAsync(HttpMethod.Post, "/api/notes", body: body);
        using var withKeyAndABadBody = await world.SendAsync(
            HttpMethod.Post,
            "/api/notes",
            body: new JsonObject { ["title"] = new string('t', 200) },
            headers: Key("anonymous-2"));

        withoutKey.StatusCode.ShouldBe(
            HttpStatusCode.Created,
            "an anonymous create must be permitted here, or the refusal is not about the key: "
            + await withoutKey.ReadTextAsync());
        withKey.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity, await withKey.ReadTextAsync());
        (await withKey.ReadProblemTypeAsync()).ShouldBe(AlvoProblemTypes.MalformedQuery);
        (await withKey.ReadProblemDetailAsync()).ShouldContain(
            "Authenticate the caller",
            Case.Sensitive,
            "§0 principle 4: the refusal must name the two ways out, not only that it is refused");
        (await withKeyAndABadBody.ReadProblemTypeAsync()).ShouldBe(
            AlvoProblemTypes.MalformedQuery, "a key this caller cannot hold outranks whatever the body says");
        (await world.CountRowsAsync("notes")).ShouldBe(1, "only the create without a key may have written a row");
    }

    /// <summary>
    /// Requires that the same body twice under one key is a replay — the control that keeps
    /// <see cref="Two_creates_differing_only_in_a_field_the_fingerprint_must_cover_are_a_conflict"/> from
    /// passing on a fingerprint that differs per request.
    /// </summary>
    /// <param name="world">The running API.</param>
    /// <param name="body">The base body.</param>
    private static async Task AReplayIsStillAReplayAsync(AlvoApiWorld world, JsonObject body)
    {
        using var first = await PostAsync(world, "inspections", body, "control-1");
        using var again = await PostAsync(world, "inspections", body, "control-1");

        first.StatusCode.ShouldBe(HttpStatusCode.Created, await first.ReadTextAsync());
        again.StatusCode.ShouldBe(HttpStatusCode.Created, await again.ReadTextAsync());
        (await IdOfAsync(again)).ShouldBe(
            await IdOfAsync(first),
            "an unchanged body must still replay, or every conflict below passes for the wrong reason");
    }

    /// <summary>
    /// Requires that two creates differing only in <paramref name="field"/>, under one key, are a 409.
    /// </summary>
    /// <param name="world">The running API.</param>
    /// <param name="field">The field the two bodies differ in, which names the key and the failure.</param>
    /// <param name="body">The base body.</param>
    /// <param name="different">The same body with <paramref name="field"/> changed.</param>
    private static async Task DifferingInOneFieldIsAConflictAsync(
        AlvoApiWorld world, string field, JsonObject body, JsonObject different)
    {
        using var first = await PostAsync(world, "inspections", body, $"differs-{field}");
        using var second = await PostAsync(world, "inspections", different, $"differs-{field}");

        first.StatusCode.ShouldBe(
            HttpStatusCode.Created, $"seeding the '{field}' pair must succeed: {await first.ReadTextAsync()}");
        second.StatusCode.ShouldBe(
            HttpStatusCode.Conflict,
            $"a body differing only in '{field}' was answered {(int)second.StatusCode} — that field is missing "
            + "from the fingerprint, so the caller was handed a row that does not contain what they sent: "
            + await second.ReadTextAsync());
        (await second.ReadProblemTypeAsync()).ShouldBe(AlvoProblemTypes.IdempotencyConflict);
    }

    /// <summary>One tenant's note, stating the tenant the row belongs to as a scoped create must.</summary>
    /// <param name="key">The key whose tenant the note is created in.</param>
    private static JsonObject Note(TestApiKey key) =>
        new() { ["title"] = "Shared key", ["tenant_id"] = key.Tenant!.Value.ToString() };

    /// <summary>The base inspection body, referencing <paramref name="vehicle"/>.</summary>
    /// <param name="vehicle">The vehicle the inspection is of.</param>
    private static JsonObject Inspection(Guid vehicle) => new()
    {
        ["vehicle_id"] = vehicle.ToString(),
        ["inspector_name"] = "Ivan Inspector",
        ["inspected_on"] = "2026-01-15",
        ["passed"] = true,
        ["notes"] = "Nothing to report.",
    };

    /// <summary>
    /// One variant of <see cref="Inspection"/> per declared field, each differing from the base in exactly that
    /// field and in nothing else.
    /// </summary>
    /// <param name="vehicles">Two seeded vehicles, so the reference can differ too.</param>
    private static Dictionary<string, JsonObject> InspectionVariants(IReadOnlyList<Guid> vehicles) => new(
        StringComparer.Ordinal)
    {
        ["vehicle_id"] = With(Inspection(vehicles[0]), "vehicle_id", vehicles[1].ToString()),
        ["inspector_name"] = With(Inspection(vehicles[0]), "inspector_name", "Iva Inspector"),
        ["inspected_on"] = With(Inspection(vehicles[0]), "inspected_on", "2026-02-15"),
        ["passed"] = With(Inspection(vehicles[0]), "passed", false),
        ["notes"] = With(Inspection(vehicles[0]), "notes", "A crack in the windscreen."),
    };

    /// <summary>The body with one member replaced, so a variant cannot accidentally differ in two.</summary>
    private static JsonObject With(JsonObject body, string field, JsonNode value)
    {
        body[field] = value;
        return body;
    }

    /// <summary>The field names one entity declares, read out of the descriptor the world is built from.</summary>
    /// <remarks>
    /// Read from the file rather than restated here, so the entity gaining a field breaks the fact that claims
    /// to cover every field — which is the only thing that makes such a claim more than a wish.
    /// </remarks>
    /// <param name="entity">The entity to read.</param>
    private static IReadOnlyList<string> DeclaredFields(string entity)
    {
        var path = Path.Combine(RepositoryRoot.Find(), "examples", "vehicle-registry", "vehicles.alvo.json");
        var descriptor = JsonNode.Parse(File.ReadAllText(path))
            ?? throw new InvalidOperationException($"'{path}' is not a JSON document.");

        return [.. descriptor["entities"]![entity]!["fields"]!.AsObject().Select(field => field.Key)];
    }

    /// <summary>Two vehicles (and the owner they need), so a reference field has two values to differ by.</summary>
    private static async Task<IReadOnlyList<Guid>> SeedTwoVehiclesAsync(AlvoApiWorld world)
    {
        using var owner = await world.SendAsync(
            HttpMethod.Post, "/api/owners", _admin, body: new JsonObject { ["name"] = "Fleet Ltd" });
        owner.StatusCode.ShouldBe(HttpStatusCode.Created, await owner.ReadTextAsync());
        var ownerId = await IdOfAsync(owner);

        return [await SeedVehicleAsync(world, ownerId, 1), await SeedVehicleAsync(world, ownerId, 2)];
    }

    private static async Task<Guid> SeedVehicleAsync(AlvoApiWorld world, Guid owner, int ordinal)
    {
        using var response = await world.SendAsync(
            HttpMethod.Post, "/api/vehicles", _admin, body: Vehicle(owner, ordinal));
        response.StatusCode.ShouldBe(HttpStatusCode.Created, await response.ReadTextAsync());
        return await IdOfAsync(response);
    }

    /// <summary>A valid vehicle body, unique per <paramref name="ordinal"/> in the fields that demand it.</summary>
    /// <param name="owner">The owner the vehicle belongs to.</param>
    /// <param name="ordinal">Which vehicle this is, for the unique <c>vin</c> and <c>plate</c>.</param>
    private static JsonObject Vehicle(Guid owner, int ordinal) => new()
    {
        ["vin"] = $"VIN0000000000000{ordinal}",
        ["plate"] = $"AA-11{ordinal}-BB",
        ["make"] = "Skoda",
        ["model"] = "Octavia",
        ["year"] = 2020,
        ["owner_id"] = owner.ToString(),
    };

    /// <summary>Posts <paramref name="body"/> to one entity, presenting <paramref name="key"/>.</summary>
    private static Task<HttpResponseMessage> PostAsync(
        AlvoApiWorld world, string entity, JsonObject body, string key) =>
        world.SendAsync(HttpMethod.Post, $"/api/{entity}", _admin, body: body, headers: Key(key));

    private static KeyValuePair<string, string>[] Key(string key) => [Pair(key)];

    private static KeyValuePair<string, string> Pair(string key) => new("Idempotency-Key", key);

    private static async Task<Guid> IdOfAsync(HttpResponseMessage response) =>
        (await response.ReadJsonObjectAsync())["id"]!.GetValue<Guid>();
}
