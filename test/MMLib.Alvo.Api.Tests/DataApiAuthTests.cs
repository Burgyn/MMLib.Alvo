using System.Net;
using System.Text.Json.Nodes;

namespace MMLib.Alvo.Api.Tests;

/// <summary>
/// The authorization seam in front of the generated routes: who a request is served as, which
/// diagnosis a bad credential earns, and where the refusal happens. The distinctions asserted here are
/// the ones an agent reading the response has to act on — "you are not who you claimed" (401) and "you
/// are, and it is not allowed" (403) have different fixes, and conflating them sends the agent looking
/// in the wrong place.
/// </summary>
/// <remarks>
/// <b>Every one of the five endpoints has its own gating fact</b>, and each is written so that swapping
/// <em>that</em> endpoint's <c>DataOperation</c> constant fails <em>that</em> fact. The first round had
/// one gating fact over one verb, which meant <c>MapDelete</c>'s filter could have been built for
/// <c>List</c> — a read-scoped key deleting rows — with the whole suite green.
/// </remarks>
public sealed class DataApiAuthTests
{
    private static readonly TestApiKey _admin = new("admin-key", ["admin", "authenticated"], ["*:read", "*:write"]);

    /// <summary>
    /// The load-bearing one. A missing credential is <b>not</b> 401: Alvo has a real
    /// <see cref="Role.Anon"/> and default-deny, so an anonymous caller is a caller whose policy happens
    /// to permit nothing, and the refusal must come from the policy engine inside the port.
    /// </summary>
    /// <remarks>
    /// <para>
    /// It asserts the problem <c>type</c>, not only the status, and it uses a <b>write</b>: the
    /// vehicle-registry rules are row predicates, so an anonymous <em>list</em> is an honest 200 with
    /// zero visible rows, which says nothing about who refused. A create is refused outright, and the
    /// <c>forbidden</c> slug is what distinguishes a policy refusal from the scope gate's <c>out-of-scope</c>:
    /// the one regression that would otherwise hide, since applying the gate to an anonymous caller with no
    /// scopes also produces 403.
    /// </para>
    /// <para>
    /// It used to assert the port's <c>detail</c> literal, because before Task 5 that prose was the only thing
    /// that could tell the two 403s apart. Re-pointed at the slug on purpose: RFC 9457 makes <c>type</c> the
    /// classification a client may branch on and <c>detail</c> prose it "ought not" parse, and a test pinning
    /// that literal held the deny reason's deliberate genericity hostage — the wording could not be made
    /// <em>more</em> generic without failing a test. <c>ProblemDetailsTests</c> holds the pair of slugs.
    /// </para>
    /// <para>
    /// The admin control is not decoration: without it, this fact would pass on a server that refused
    /// every write for an unrelated reason.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task A_request_with_no_api_key_is_served_as_anonymous_and_denied_by_policy()
    {
        await using var world = await AlvoApiWorld.VehicleRegistryAsync([_admin]);

        using var anonymous = await world.SendAsync(HttpMethod.Post, "/api/owners", body: Owner("Anonymous Ltd"));
        using var authorized = await world.SendAsync(HttpMethod.Post, "/api/owners", _admin, body: Owner("Acme Ltd"));

        anonymous.StatusCode.ShouldBe(
            HttpStatusCode.Forbidden, "a missing credential is an anonymous caller the policy denies, never a 401");
        (await anonymous.ReadProblemTypeAsync()).ShouldBe(
            AlvoProblemTypes.Forbidden,
            "the refusal must come from the policy inside the port, not from the scope gate — whose slug is "
            + "out-of-scope");
        authorized.StatusCode.ShouldBe(
            HttpStatusCode.Created, "or the anonymous refusal above could be a blanket denial of every write");
    }

    /// <summary>
    /// An anonymous caller has <b>no principal</b> — there is no key for one to describe — so none is
    /// published on the ambient accessor and no later reader needs a sentinel convention to spot one. The
    /// keyed control is what makes the absence meaningful rather than a broken recorder.
    /// </summary>
    [Fact]
    public async Task An_anonymous_request_publishes_no_principal_and_still_resolves_an_anonymous_context()
    {
        await using var world = await AlvoApiWorld.VehicleRegistryAsync([_admin]);

        using var anonymous = await world.SendAsync(HttpMethod.Post, "/api/owners", body: Owner("Anonymous Ltd"));
        var afterAnonymous = world.PublishedPrincipals;
        using var keyed = await world.SendAsync(HttpMethod.Get, "/api/owners", _admin);

        afterAnonymous.ShouldNotBeEmpty(
            "the filter must have reached the accessor at all, or 'every publish was null' is vacuously true");
        afterAnonymous.ShouldAllBe(principal => principal == null);
        (await anonymous.ReadProblemTypeAsync()).ShouldBe(
            AlvoProblemTypes.Forbidden,
            "the anonymous context still reached the port — 'no principal' is not 'no caller', and the scope "
            + "gate would have answered out-of-scope");
        keyed.StatusCode.ShouldBe(HttpStatusCode.OK);
        world.PublishedPrincipals.ShouldContain(
            principal => principal != null && principal.KeyId == _admin.KeyId,
            "a keyed request does publish one, or the absence above proves nothing about the recorder");
    }

    /// <summary>
    /// A key that was <em>presented</em> and cannot be resolved is a different diagnosis: the caller
    /// believes they have a credential and they do not, so the fix is the credential, not the policy.
    /// </summary>
    [Fact]
    public async Task A_request_with_an_unknown_api_key_is_401_not_403()
    {
        var ghost = new TestApiKey("ghost-key", ["admin"], ["*:read"]);
        await using var world = await AlvoApiWorld.VehicleRegistryAsync([_admin]);

        using var response = await world.SendAsync(HttpMethod.Get, "/api/owners", ghost);
        using var control = await world.SendAsync(HttpMethod.Get, "/api/owners", _admin);

        response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        control.StatusCode.ShouldBe(
            HttpStatusCode.OK, "the same request with a known key succeeds, so the 401 is about the credential");
    }

    /// <summary>
    /// RFC 7235 §3.1 makes <c>WWW-Authenticate</c> a MUST on a 401, and it is what makes the status
    /// actionable without documentation: it names the scheme and the header the caller should have used,
    /// so an agent can discover how to authenticate instead of guessing. The header it names has to be
    /// the one the server actually reads.
    /// </summary>
    [Fact]
    public async Task A_401_carries_a_www_authenticate_challenge_naming_the_api_key_header()
    {
        var ghost = new TestApiKey("ghost-key", ["admin"], ["*:read"]);
        await using var world = await AlvoApiWorld.VehicleRegistryAsync([_admin]);

        using var response = await world.SendAsync(HttpMethod.Get, "/api/owners", ghost);

        response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        var challenge = response.Headers.WwwAuthenticate.ToString();
        challenge.ShouldContain("AlvoApiKey");
        challenge.ShouldContain("X-Alvo-Api-Key");
    }

    /// <summary>
    /// Revocation is the same 401 as an unknown key, and deliberately indistinguishable from it — but it
    /// travels a different production path (the key authenticates, then
    /// <see cref="MMLib.Alvo.Auth.ApiKeyRecord.IsUsable"/> refuses it), so it is its own fact. The
    /// control proves the very same key id and secret is otherwise accepted.
    /// </summary>
    [Fact]
    public async Task A_request_with_a_revoked_api_key_is_401()
    {
        var revoked = new TestApiKey("revoked-key", ["authenticated"], ["*:read"]);
        await using var world = await AlvoApiWorld.VehicleRegistryAsync(
            [_admin, revoked], new AlvoApiWorldSetup(RevokedKeyId: revoked.KeyId));

        using var response = await world.SendAsync(HttpMethod.Get, "/api/owners", revoked);
        using var control = await world.SendAsync(HttpMethod.Get, "/api/owners", _admin);

        response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        control.StatusCode.ShouldBe(
            HttpStatusCode.OK, "or the 401 above could be this world refusing every key, revoked or not");
    }

    /// <summary>
    /// The <c>list</c> endpoint's gate. The scope gate runs above the port, so a key whose scopes exclude
    /// the entity never reaches a row.
    /// </summary>
    /// <remarks>
    /// "Before any row is touched" is evidence, not assertion: the statement recorder must show that the
    /// database saw nothing at all. The <c>vehicles</c> control is what makes that non-vacuous — the same
    /// key, over an entity with a <em>character-for-character identical</em> <c>list</c> rule, does reach
    /// the store — so the only difference between the two responses is the scope.
    /// </remarks>
    [Fact]
    public async Task A_key_whose_scope_excludes_the_entity_is_403_before_any_row_is_touched()
    {
        var narrow = new TestApiKey("narrow-key", ["authenticated"], ["vehicles:read"]);
        await using var world = await AlvoApiWorld.VehicleRegistryAsync([narrow]);
        world.ClearStatements();

        using var refused = await world.SendAsync(HttpMethod.Get, "/api/owners", narrow);
        var statementsAfterRefusal = world.Statements;
        using var allowed = await world.SendAsync(HttpMethod.Get, "/api/vehicles", narrow);

        refused.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        statementsAfterRefusal.ShouldBeEmpty("the scope gate must refuse before the port composes a statement");
        allowed.StatusCode.ShouldBe(HttpStatusCode.OK);
        world.Statements.ShouldNotBeEmpty(
            "the in-scope read must reach the store, or 'no statement' above proves nothing");
    }

    /// <summary>
    /// The body-shaped read's gate. Its own fact rather than a variation of the <c>list</c> one: it is a
    /// second route with its own filter, and the whole risk of a second way to reach one read is that only
    /// one of the two is gated.
    /// </summary>
    /// <remarks>
    /// The <c>vehicles</c> control is what makes "no statement" non-vacuous — the same key, over an entity
    /// whose <c>list</c> rule is character-for-character identical, does reach the store through this same
    /// route.
    /// </remarks>
    [Fact]
    public async Task A_key_whose_scope_excludes_the_entity_cannot_query_it_through_a_body()
    {
        var narrow = new TestApiKey("narrow-key", ["authenticated"], ["vehicles:read"]);
        await using var world = await AlvoApiWorld.VehicleRegistryAsync([narrow]);
        world.ClearStatements();

        using var refused = await world.SendAsync(
            HttpMethod.Post, "/api/owners/query", narrow, body: new JsonObject());
        var statementsAfterRefusal = world.Statements;
        using var allowed = await world.SendAsync(
            HttpMethod.Post, "/api/vehicles/query", narrow, body: new JsonObject());

        refused.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        statementsAfterRefusal.ShouldBeEmpty("the scope gate must refuse before the port composes a statement");
        allowed.StatusCode.ShouldBe(HttpStatusCode.OK);
        world.Statements.ShouldNotBeEmpty(
            "the in-scope query must reach the store, or 'no statement' above proves nothing");
    }

    /// <summary>
    /// An entity whose <c>list</c> the descriptor configures no rule for is refused for everybody through
    /// this route too — default-deny is a property of the operation, and a second transport must not be a
    /// second answer.
    /// </summary>
    /// <remarks>
    /// The same caller's <c>create</c> on that entity is the control: <c>ledgers</c> configures one, so a
    /// 403 here is about the unconfigured <c>list</c> and not about this key.
    /// </remarks>
    [Fact]
    public async Task An_entity_whose_list_is_unconfigured_cannot_be_queried_through_a_body()
    {
        var caller = new TestApiKey("caller-key", ["authenticated"], ["*:read", "*:write"]);
        await using var world = await AlvoApiWorld.FromDescriptorAsync("masked-notes.alvo.json", [caller]);
        world.ClearStatements();

        using var refused = await world.SendAsync(
            HttpMethod.Post, "/api/ledgers/query", caller, body: new JsonObject());
        var statementsAfterRefusal = world.Statements;
        using var created = await world.SendAsync(
            HttpMethod.Post, "/api/ledgers", caller, body: new JsonObject { ["title"] = "Ledger" });

        refused.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        statementsAfterRefusal.ShouldBeEmpty("default-deny must refuse before the port composes a statement");
        created.StatusCode.ShouldBe(
            HttpStatusCode.Created, "or the 403 above is about this key rather than about the missing rule");
    }

    /// <summary>
    /// An anonymous caller is judged by policy on the body-shaped read exactly as on the collection
    /// <c>GET</c> — same status, same bytes — and a rule that excludes them yields an empty page rather
    /// than rows.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Worth its own fact because a <c>POST</c>-that-reads is the shape a cross-site form produces.</b>
    /// Alvo's credential is an explicit request header and never a cookie, so such a request arrives with no
    /// credential at all — which is this fact's caller. What it must not do is answer differently from the
    /// <c>GET</c>, or hand back a row.
    /// </para>
    /// <para>
    /// This descriptor's <c>list</c> rule is a role test, which compiles to a row-level <c>USING</c>
    /// predicate — so an anonymous caller receives an <em>allow</em> carrying a predicate that matches
    /// nothing, and the documented answer is 200 with an empty page rather than 403. The unconfigured-rule
    /// case, which is the one that really is a denial, is the fact above.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task An_anonymous_caller_is_answered_the_same_way_on_both_collection_reads()
    {
        await using var world = await AlvoApiWorld.VehicleRegistryAsync([_admin]);
        await CreateOwnerAsync(world, "Acme Ltd");

        using var byUrl = await world.SendAsync(HttpMethod.Get, "/api/owners", key: null);
        using var byBody = await world.SendAsync(
            HttpMethod.Post, "/api/owners/query", key: null, body: new JsonObject());

        byBody.StatusCode.ShouldBe(byUrl.StatusCode);
        var bodyText = await byBody.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        bodyText.ShouldBe(await byUrl.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        (await byBody.ReadJsonObjectAsync())["items"]!.AsArray().ShouldBeEmpty(
            "a caller no rule admits must see no row, whichever surface they used");
    }

    /// <summary>
    /// The <c>get</c> endpoint's gate. Its own fact rather than a variation of the <c>list</c> one:
    /// <c>MapGet</c> carries its own filter with its own operation constant, and a read of one row by id
    /// is exactly the request a caller reaches for when a list was refused.
    /// </summary>
    /// <remarks>
    /// The positive control is a key scoped <c>owners:read</c> and nothing else — deliberately not the
    /// admin key, which also holds <c>*:write</c>. Only a read-<em>only</em> control can notice
    /// <c>MapGet</c>'s constant being swapped to a write operation, because an admin key would satisfy
    /// that too.
    /// </remarks>
    [Fact]
    public async Task A_key_scoped_to_another_entity_cannot_get_a_row_by_id()
    {
        var narrow = new TestApiKey("narrow-key", ["authenticated"], ["vehicles:read"]);
        var ownersReader = new TestApiKey("owners-reader", ["authenticated"], ["owners:read"]);
        await using var world = await AlvoApiWorld.VehicleRegistryAsync([_admin, narrow, ownersReader]);
        var ownerId = await CreateOwnerAsync(world, "Acme Ltd");
        world.ClearStatements();

        using var refused = await world.SendAsync(HttpMethod.Get, $"/api/owners/{ownerId}", narrow);
        var statementsAfterRefusal = world.Statements;
        using var allowed = await world.SendAsync(HttpMethod.Get, $"/api/owners/{ownerId}", ownersReader);

        refused.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        statementsAfterRefusal.ShouldBeEmpty("the gate must refuse before the row is read");
        allowed.StatusCode.ShouldBe(
            HttpStatusCode.OK, "a read scope on this entity is enough — a get must not demand write access");
    }

    /// <summary>
    /// The <c>create</c> endpoint's gate: <c>read</c> does not imply <c>write</c>
    /// (<see cref="MMLib.Alvo.Auth.ScopeAccess"/>'s own rule), and the refusal happens without reaching
    /// the store.
    /// </summary>
    [Fact]
    public async Task A_read_scope_cannot_perform_a_write()
    {
        var reader = new TestApiKey("reader-key", ["admin", "authenticated"], ["owners:read"]);
        await using var world = await AlvoApiWorld.VehicleRegistryAsync([reader]);
        world.ClearStatements();

        using var write = await world.SendAsync(HttpMethod.Post, "/api/owners", reader, body: Owner("Acme Ltd"));
        var statementsAfterWrite = world.Statements;
        using var read = await world.SendAsync(HttpMethod.Get, "/api/owners", reader);

        write.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        statementsAfterWrite.ShouldBeEmpty("a scope refusal must not compose a statement");
        read.StatusCode.ShouldBe(
            HttpStatusCode.OK, "the same key reads fine — the refusal above is about the access, not the key");
    }

    /// <summary>
    /// The <c>update</c> endpoint's gate. The row exists and this caller can read it, so the only thing
    /// between a read-scoped key and a mutation is <c>MapUpdate</c>'s own operation constant — and the
    /// row is re-read afterwards, because a 403 that still wrote would be the real defect.
    /// </summary>
    [Fact]
    public async Task A_read_scoped_key_cannot_patch_a_row()
    {
        var reader = new TestApiKey("reader-key", ["admin", "authenticated"], ["owners:read"]);
        await using var world = await AlvoApiWorld.VehicleRegistryAsync([_admin, reader]);
        var ownerId = await CreateOwnerAsync(world, "Acme Ltd");
        world.ClearStatements();

        using var patch = await world.SendAsync(
            HttpMethod.Patch, $"/api/owners/{ownerId}", reader, body: Owner("Renamed Ltd"));
        var statementsAfterPatch = world.Statements;
        using var stillOriginal = await world.SendAsync(HttpMethod.Get, $"/api/owners/{ownerId}", reader);

        patch.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        statementsAfterPatch.ShouldBeEmpty("the gate must refuse before the row is locked or written");
        (await stillOriginal.ReadJsonObjectAsync())["name"]!.GetValue<string>().ShouldBe("Acme Ltd");

        // The positive half. Without it this fact only ever sees a refusal, and a refusal is what a broken
        // PATCH endpoint produces too — a 403 for a caller who cannot patch proves nothing until some caller
        // can.
        using var allowed = await world.SendAsync(
            HttpMethod.Patch, $"/api/owners/{ownerId}", _admin, body: Owner("Renamed Ltd"));
        using var afterPatch = await world.SendAsync(HttpMethod.Get, $"/api/owners/{ownerId}", reader);

        allowed.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await afterPatch.ReadJsonObjectAsync())["name"]!.GetValue<string>().ShouldBe("Renamed Ltd");
    }

    /// <summary>
    /// The <c>delete</c> endpoint's gate — the swap the first round could not have caught: with
    /// <c>MapDelete</c>'s filter built for <c>List</c>, this read-scoped key would delete the row and
    /// nothing in the suite would have noticed.
    /// </summary>
    [Fact]
    public async Task A_read_scoped_key_cannot_delete_a_row()
    {
        var reader = new TestApiKey("reader-key", ["admin", "authenticated"], ["owners:read"]);
        await using var world = await AlvoApiWorld.VehicleRegistryAsync([_admin, reader]);
        var ownerId = await CreateOwnerAsync(world, "Acme Ltd");
        world.ClearStatements();

        using var delete = await world.SendAsync(HttpMethod.Delete, $"/api/owners/{ownerId}", reader);
        var statementsAfterDelete = world.Statements;
        using var stillThere = await world.SendAsync(HttpMethod.Get, $"/api/owners/{ownerId}", reader);

        delete.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        statementsAfterDelete.ShouldBeEmpty("the gate must refuse before the delete is composed");
        stillThere.StatusCode.ShouldBe(HttpStatusCode.OK, "the row must survive a refused delete");

        // The positive half: a write-scoped key does delete it, and the row is then gone. Without this the
        // fact is satisfied by a DELETE endpoint that refuses everyone.
        using var allowed = await world.SendAsync(HttpMethod.Delete, $"/api/owners/{ownerId}", _admin);
        using var afterDelete = await world.SendAsync(HttpMethod.Get, $"/api/owners/{ownerId}", reader);

        allowed.StatusCode.ShouldBe(HttpStatusCode.NoContent);
        afterDelete.StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    /// <summary>
    /// <c>[15a]</c>'s definition of done, made true over HTTP: a caller with no tenant sees no tenant's
    /// rows, and a caller cannot acquire a tenant by asking for one in a header.
    /// </summary>
    /// <remarks>
    /// <para>
    /// It asserts <b>rows</b>, and it is seeded in two tenants — with one row in each, a fact that
    /// "returns nothing" could be satisfied by an empty database. Each refusal's status is now asserted
    /// explicitly as well: the first round let the header case pass as a silently unnoticed 401, because
    /// the reader it used answered "no rows" for a body it could not parse.
    /// </para>
    /// <para>
    /// The header case is the discriminating one. The descriptor's rules are all <c>true</c>, so nothing
    /// but tenancy can withhold a row; a filter that read <c>X-Alvo-Tenant</c> straight into the caller's
    /// context — instead of letting <c>TenantResolver</c> treat it as a mere confirmation of the key's own
    /// tenant — would hand this tenantless caller tenant A's row, and that is precisely the bug
    /// <c>[15a]</c> is about.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task A_tenant_scoped_entity_read_with_no_tenant_context_returns_no_rows_of_any_tenant()
    {
        var tenantA = Guid.NewGuid();
        var keyA = new TestApiKey("tenant-a", ["authenticated"], ["notes:read", "notes:write"], tenantA);
        var keyB = new TestApiKey("tenant-b", ["authenticated"], ["notes:read", "notes:write"], Guid.NewGuid());
        var tenantless = new TestApiKey("no-tenant", ["authenticated"], ["notes:read"]);
        await using var world = await AlvoApiWorld.TenantNotesAsync([keyA, keyB, tenantless]);
        await SeedNoteAsync(world, keyA, "note-a");
        await SeedNoteAsync(world, keyB, "note-b");

        using var withoutTenant = await world.SendAsync(HttpMethod.Get, "/api/notes", tenantless);
        using var askingForTenantA = await world.SendAsync(
            HttpMethod.Get, "/api/notes", tenantless, tenant: tenantA.ToString());
        using var control = await world.SendAsync(HttpMethod.Get, "/api/notes", keyA);

        withoutTenant.StatusCode.ShouldBe(
            HttpStatusCode.Forbidden, "the tenant guard denies a tenantless caller on a tenant-scoped entity");
        (await withoutTenant.ReadTextAsync()).ShouldNotContain("note-");
        askingForTenantA.StatusCode.ShouldBe(
            HttpStatusCode.Unauthorized,
            "a key with no tenant of its own cannot request one — TenantResolver refuses the credential outright");
        (await askingForTenantA.ReadTextAsync()).ShouldNotContain("note-");
        (await control.ReadFieldAsync("title")).ShouldBe(
            ["note-a"], "the tenant's own key must see its own row, and only its own");
    }

    /// <summary>
    /// One tenant's row is unreachable through the body-shaped read, by a caller of another tenant asking
    /// for it <b>by name</b> — and the same body sent by its own tenant returns it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Its own fact rather than an inheritance from the collection <c>GET</c>'s.</b> The mitigating
    /// argument — that both routes share one <c>PageAsync</c> and the tenant predicate is enforced inside the
    /// port — is exactly the argument this file already rejects for the scope gate: a second way to reach one
    /// read is a second thing that has to be gated, and "it cannot possibly be wrong" is not a fact.
    /// </para>
    /// <para>
    /// <b>The control is what makes the empty page mean something.</b> Without it, an empty answer would be
    /// satisfied by a filter that matched nothing, by a route that returned nothing, or by a body the reader
    /// silently dropped. The same body, from the row's own tenant, must return the row.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task One_tenants_row_is_unreachable_through_the_body_shaped_read_by_another_tenant()
    {
        var keyA = new TestApiKey("tenant-a", ["authenticated"], ["notes:read", "notes:write"], Guid.NewGuid());
        var keyB = new TestApiKey("tenant-b", ["authenticated"], ["notes:read", "notes:write"], Guid.NewGuid());
        await using var world = await AlvoApiWorld.TenantNotesAsync([keyA, keyB]);
        await SeedNoteAsync(world, keyA, "note-a");
        await SeedNoteAsync(world, keyB, "note-b");
        var askingForAsRow = new JsonObject { ["title"] = "eq.note-a" };

        using var asOtherTenant = await world.SendAsync(
            HttpMethod.Post, "/api/notes/query", keyB, body: askingForAsRow);
        using var asOwnTenant = await world.SendAsync(
            HttpMethod.Post, "/api/notes/query", keyA, body: askingForAsRow);

        asOtherTenant.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await asOtherTenant.ReadTextAsync()).Contains("note-a", StringComparison.Ordinal).ShouldBeFalse(
            "another tenant's row must not be reachable by filtering for it");
        (await asOwnTenant.ReadFieldAsync("title")).ShouldBe(
            ["note-a"], "the same body from the row's own tenant must return it, or the empty page above "
            + "proves nothing");
    }

    /// <summary>
    /// The body-shaped twin of
    /// <see cref="A_tenant_scoped_entity_read_with_no_tenant_context_returns_no_rows_of_any_tenant"/>: a
    /// tenantless caller cannot read a tenant-scoped entity through it, and cannot acquire a tenant by
    /// asking for one in a header either.
    /// </summary>
    [Fact]
    public async Task A_tenantless_caller_cannot_query_a_tenant_scoped_entity_through_a_body()
    {
        var tenantA = Guid.NewGuid();
        var keyA = new TestApiKey("tenant-a", ["authenticated"], ["notes:read", "notes:write"], tenantA);
        var tenantless = new TestApiKey("no-tenant", ["authenticated"], ["notes:read"]);
        await using var world = await AlvoApiWorld.TenantNotesAsync([keyA, tenantless]);
        await SeedNoteAsync(world, keyA, "note-a");

        using var withoutTenant = await world.SendAsync(
            HttpMethod.Post, "/api/notes/query", tenantless, body: new JsonObject());
        using var askingForTenantA = await world.SendAsync(
            HttpMethod.Post, "/api/notes/query", tenantless, tenant: tenantA.ToString(),
            body: new JsonObject());
        using var control = await world.SendAsync(
            HttpMethod.Post, "/api/notes/query", keyA, body: new JsonObject());

        withoutTenant.StatusCode.ShouldBe(
            HttpStatusCode.Forbidden, "the tenant guard denies a tenantless caller on a tenant-scoped entity");
        (await withoutTenant.ReadTextAsync()).ShouldNotContain("note-");
        askingForTenantA.StatusCode.ShouldBe(
            HttpStatusCode.Unauthorized,
            "a key with no tenant of its own cannot request one — TenantResolver refuses the credential outright");
        (await askingForTenantA.ReadTextAsync()).ShouldNotContain("note-");
        (await control.ReadFieldAsync("title")).ShouldBe(
            ["note-a"], "the tenant's own key must see its own row through this route too");
    }

    private static async Task<Guid> CreateOwnerAsync(AlvoApiWorld world, string name)
    {
        using var response = await world.SendAsync(HttpMethod.Post, "/api/owners", _admin, body: Owner(name));
        response.StatusCode.ShouldBe(
            HttpStatusCode.Created, $"seeding owner '{name}' must succeed, or the facts over it prove nothing");
        return (await response.ReadJsonObjectAsync())["id"]!.GetValue<Guid>();
    }

    private static async Task SeedNoteAsync(AlvoApiWorld world, TestApiKey key, string title)
    {
        var body = new JsonObject { ["title"] = title, ["tenant_id"] = key.Tenant!.Value.ToString() };
        using var response = await world.SendAsync(HttpMethod.Post, "/api/notes", key, body: body);
        response.StatusCode.ShouldBe(
            HttpStatusCode.Created, $"seeding '{title}' must succeed, or the facts over it prove nothing");
    }

    private static JsonObject Owner(string name) => new() { ["name"] = name };
}
