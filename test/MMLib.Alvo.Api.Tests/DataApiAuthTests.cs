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
public sealed class DataApiAuthTests
{
    /// <summary>The message the port raises when a candidate write fails its policy check — the text that proves *policy* refused, not the transport and not the scope gate.</summary>
    private const string WriteRejectedByPolicy = "The write was rejected by policy.";

    private static readonly TestApiKey _admin = new("admin-key", ["admin", "authenticated"], ["*:read", "*:write"]);

    /// <summary>
    /// The load-bearing one. A missing credential is <b>not</b> 401: Alvo has a real
    /// <see cref="Role.Anon"/> and default-deny, so an anonymous caller is a caller whose policy happens
    /// to permit nothing, and the refusal must come from the policy engine inside the port.
    /// </summary>
    /// <remarks>
    /// <para>
    /// It asserts the problem <c>detail</c>, not only the status, and it uses a <b>write</b>: the
    /// vehicle-registry rules are row predicates, so an anonymous <em>list</em> is an honest 200 with
    /// zero visible rows, which says nothing about who refused. A create is refused outright, and the
    /// port's own wording ("<c>The write was rejected by policy.</c>") is what distinguishes a policy
    /// refusal from the scope gate's — the one regression that would otherwise hide here, since
    /// applying the gate to an anonymous caller with no scopes also produces 403.
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
        (await anonymous.ReadProblemDetailAsync()).ShouldBe(
            WriteRejectedByPolicy, "the refusal must come from the policy inside the port, not from the scope gate");
        authorized.StatusCode.ShouldBe(
            HttpStatusCode.Created, "or the anonymous refusal above could be a blanket denial of every write");
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

        response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        response.StatusCode.ShouldNotBe(
            HttpStatusCode.Forbidden, "403 would send the agent to the policy for a credential problem");
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
        await using var world = await AlvoApiWorld.VehicleRegistryAsync([_admin, revoked], revokedKeyId: revoked.KeyId);

        using var response = await world.SendAsync(HttpMethod.Get, "/api/owners", revoked);
        using var control = await world.SendAsync(HttpMethod.Get, "/api/owners", _admin);

        response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        control.StatusCode.ShouldBe(
            HttpStatusCode.OK, "or the 401 above could be this world refusing every key, revoked or not");
    }

    /// <summary>
    /// The scope gate runs above the port, so a key whose scopes exclude the entity never reaches a row.
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
    /// <c>read</c> does not imply <c>write</c> (<see cref="MMLib.Alvo.Auth.ScopeAccess"/>'s own rule), so
    /// a read-scoped key cannot create — and, again, not by reaching the store and being refused there.
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
    /// <c>[15a]</c>'s definition of done, made true over HTTP: a caller with no tenant sees no tenant's
    /// rows, and a caller cannot acquire a tenant by asking for one in a header.
    /// </summary>
    /// <remarks>
    /// <para>
    /// It asserts <b>rows</b>, not a status code, and it is seeded in two tenants — with one row in each,
    /// a fact that "returns nothing" could be satisfied by an empty database.
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

        (await withoutTenant.ReadItemsAsync()).ShouldBeEmpty();
        (await withoutTenant.ReadTextAsync()).ShouldNotContain("note-");
        (await askingForTenantA.ReadItemsAsync()).ShouldBeEmpty();
        (await askingForTenantA.ReadTextAsync()).ShouldNotContain("note-");
        (await control.ReadFieldAsync("title")).ShouldBe(
            ["note-a"], "the tenant's own key must see its own row, and only its own");
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
