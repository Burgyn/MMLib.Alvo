using Microsoft.Extensions.DependencyInjection;
using MMLib.Alvo.Data;
using MMLib.Alvo.Rules;
using System.Net;
using System.Text.Json.Nodes;

namespace MMLib.Alvo.Api.Tests;

/// <summary>
/// How many times one request resolves the policy — measured, and pinned, so a fourth resolution cannot
/// appear unnoticed (#118).
/// </summary>
/// <remarks>
/// <para>
/// <b>#118 was filed claiming three resolutions per list request, and that is not what the code does.</b>
/// It claimed <c>AlvoContextFilter</c>'s scope gate as the first. That filter holds no
/// <see cref="IPolicyEngine"/> at all: its gate is <c>ScopeGate.Allows</c>, a <c>principal.Scopes.Any(…)</c>
/// over the credential's own scope list — no catalog lookup, no CEL, no <c>FrozenSet</c>, and skipped
/// outright for an anonymous caller. The real count is <b>two</b>: the HTTP tier's
/// <c>EnsureOperationIsAllowed</c>, and the port's own resolve, which remains the authority.
/// </para>
/// <para>
/// <b>The request-scoped decision cache #118 proposed is deliberately not built, and this suite is what is
/// delivered instead.</b> Measured, that cache saves one <see cref="PolicyDecision"/> allocation per
/// request — the record itself — plus, and only for an entity declaring CEL-valued <c>hidden</c>/<c>readOnly</c>
/// masks, one <c>HashSet</c> and one <c>FrozenSet</c>; an entity with no masks allocates neither and
/// evaluates no CEL, because <c>hidden: false</c> compiles to <see langword="null"/> and never reaches the
/// mask dictionary. That is not worth a memo layer inside the authorization path, on a request already
/// making a database round trip.
/// </para>
/// <para>
/// <b>What is <em>not</em> the argument against it:</b> that such a cache would necessarily hand the port a
/// stale decision across a mid-request descriptor apply. It would not have to —
/// <c>PolicyCatalogProvider.Current</c> is one <c>Volatile.Read</c> of an immutable catalog, so a cache
/// keyed additionally on the <b>catalog reference</b> invalidates itself the moment a new catalog is
/// published and preserves the fail-closed window exactly. Recorded because a decline resting on a hazard
/// that can be disproved in five minutes gets dismissed wholesale along with the hazard; the decline rests
/// on the number. Whoever later has a real reason to want the memo should build it, and key it on the
/// catalog reference.
/// </para>
/// <para>
/// <b>These facts pin numbers rather than move them.</b> #118's own first constraint — "a third resolution
/// would be refused review" — was prose, and prose refuses nothing. Two of the numbers below are expected
/// to move one day, and each says so on itself; what must not happen is that one moves without anyone
/// deciding it should.
/// </para>
/// </remarks>
public sealed class PolicyResolutionCountTests
{
    private static readonly TestApiKey _admin = new("admin-key", ["admin", "authenticated"], ["*:read", "*:write"]);

    /// <summary>
    /// The HTTP tier's refusal and the port's authority. Both are deliberate and neither is removable.
    /// </summary>
    /// <remarks>
    /// The HTTP resolve exists so <c>QueryStringParser</c> can refuse a filter over a hidden field with the
    /// same 403 an unknown field gets — without it a denied lister reached the parser with an empty mask,
    /// and a filter over a declared-but-hidden field parsed cleanly while one over an undeclared field was
    /// refused, which answers "does this entity have a field called X" for exactly the caller most likely to
    /// be asking. The port's resolve is the authority and cannot be removed on any account.
    /// </remarks>
    [Fact]
    public async Task A_list_resolves_the_policy_exactly_twice()
    {
        var (world, engine) = await CountedAsync();
        await using var _ = world;

        engine.Clear();
        using var response = await world.SendAsync(HttpMethod.Get, "/api/owners", _admin);

        response.StatusCode.ShouldBe(HttpStatusCode.OK, await response.ReadTextAsync());
        engine.Resolved.Count.ShouldBe(2, $"a list resolves the HTTP gate and the port: {engine.Trace()}");
        engine.Resolved.ShouldAllBe(call => call.Operation == DataOperation.List, engine.Trace());
    }

    /// <summary>
    /// A read by id resolves <b>once</b> — the port only. <c>MapGet</c> is the one generated delegate with no
    /// <c>EnsureOperationIsAllowed</c> call, and that is deliberate.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>This number is expected to move, and this fact exists to make that a decision rather than an
    /// accident.</b> <c>DataApiEndpoints</c>'s own remarks instruct a future author to add the guard — and
    /// the <c>IPolicyEngine</c> parameter — back <em>the moment this delegate interprets caller input before
    /// the port call</em>, and they name the two things that will do it: <c>select</c> (#117, PR-D2) and
    /// honouring <c>If-Match</c> on a read.
    /// </para>
    /// <para>
    /// So a failure here is not "someone broke the invariant". It means the delegate started interpreting
    /// caller input, and the right response is to check the guard came with it and then move this number to
    /// two — never to delete the guard to keep the number at one.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task A_read_by_id_resolves_the_policy_exactly_once()
    {
        var (world, engine) = await CountedAsync();
        await using var _ = world;
        var id = await CreatedIdAsync(world);

        engine.Clear();
        using var response = await world.SendAsync(HttpMethod.Get, $"/api/owners/{id}", _admin);

        response.StatusCode.ShouldBe(HttpStatusCode.OK, await response.ReadTextAsync());
        engine.Resolved.Count.ShouldBe(
            1, $"MapGet interprets no caller input, so only the port resolves: {engine.Trace()}");
        engine.Resolved[0].Operation.ShouldBe(DataOperation.Get);
    }

    /// <summary>Each write is the same two as a list: the HTTP gate, then the port.</summary>
    /// <remarks>
    /// Deliberately <b>unkeyed</b> creates. A create carrying an <c>Idempotency-Key</c> whose key has already
    /// been recorded resolves a third time, and that third resolve is a security control rather than waste —
    /// see <see cref="A_keyed_create_replayed_resolves_get_a_third_time"/>.
    /// </remarks>
    [Fact]
    public async Task A_create_an_update_and_a_delete_each_resolve_the_policy_exactly_twice()
    {
        var (world, engine) = await CountedAsync();
        await using var _ = world;
        var id = await CreatedIdAsync(world);

        await ResolvesTwiceAsync(world, engine, HttpMethod.Post, "/api/owners", Owner("Ada"));
        await ResolvesTwiceAsync(
            world, engine, HttpMethod.Patch, $"/api/owners/{id}", new JsonObject { ["name"] = "Ada Lovelace" });
        await ResolvesTwiceAsync(world, engine, HttpMethod.Delete, $"/api/owners/{id}", body: null);
    }

    /// <summary>
    /// The counterweight: a <b>replayed</b> keyed create resolves three times, and the third one must stay.
    /// </summary>
    /// <remarks>
    /// The replay branch re-resolves <c>get</c> for the caller now asking, rather than reusing the
    /// <c>create</c> decision recorded with the key — because the caller replaying a key is not necessarily
    /// the caller who created the row, and reusing the original decision would hand them a row their own
    /// policy forbids them to read. Without this fact, the one above would read as "a create resolves twice,
    /// full stop", and the cheapest way to make a failing count green would be to delete a row-level
    /// authorization check.
    /// </remarks>
    [Fact]
    public async Task A_keyed_create_replayed_resolves_get_a_third_time()
    {
        var (world, engine) = await CountedAsync();
        await using var _ = world;
        var key = new[] { new KeyValuePair<string, string>("Idempotency-Key", $"key-{Guid.NewGuid():N}") };
        var body = Owner("Grace");

        using var first = await world.SendAsync(HttpMethod.Post, "/api/owners", _admin, body: body, headers: key);
        first.StatusCode.ShouldBe(HttpStatusCode.Created, await first.ReadTextAsync());

        engine.Clear();
        using var replay = await world.SendAsync(HttpMethod.Post, "/api/owners", _admin, body: body, headers: key);

        replay.StatusCode.ShouldBe(HttpStatusCode.Created, await replay.ReadTextAsync());
        engine.Resolved.Count.ShouldBe(
            3, $"a replay re-resolves Get for the caller now asking: {engine.Trace()}");
        engine.Resolved.ShouldContain(
            call => call.Operation == DataOperation.Get,
            $"the replay's row-level read check must survive: {engine.Trace()}");
    }

    private static async Task ResolvesTwiceAsync(
        AlvoApiWorld world, CountingPolicyEngine engine, HttpMethod method, string path, JsonObject? body)
    {
        engine.Clear();
        using var response = await world.SendAsync(method, path, _admin, body: body);

        response.IsSuccessStatusCode.ShouldBeTrue(
            $"{method} {path} answered {(int)response.StatusCode}: {await response.ReadTextAsync()}");
        engine.Resolved.Count.ShouldBe(2, $"{method} {path} resolved {engine.Trace()}");
    }

    /// <summary>
    /// A world whose <see cref="IPolicyEngine"/> counts, decorated <b>after</b> <c>AddAlvo</c> so the real
    /// engine is the one being wrapped rather than the one that was never registered.
    /// </summary>
    private static async Task<(AlvoApiWorld World, CountingPolicyEngine Engine)> CountedAsync()
    {
        var world = await AlvoApiWorld.VehicleRegistryAsync(
            [_admin],
            new AlvoApiWorldSetup(ConfigureServicesAfterAlvo: services =>
                services.Decorate<IPolicyEngine>(inner => new CountingPolicyEngine(inner))));

        // Resolved from the built container rather than captured in the decorating factory: the endpoint
        // delegates take IPolicyEngine as a parameter, so nothing resolves it until the first request and a
        // captured variable would still be null here. Asking the container is also the stronger check — it
        // asserts what the request will actually be handed, not what the factory happened to build.
        var engine = world.Services.GetRequiredService<IPolicyEngine>()
            .ShouldBeOfType<CountingPolicyEngine>("the decoration must have replaced the registered engine");

        return (world, engine);
    }

    private static async Task<Guid> CreatedIdAsync(AlvoApiWorld world)
    {
        using var response = await world.SendAsync(
            HttpMethod.Post, "/api/owners", _admin, body: Owner("Alan"));

        response.StatusCode.ShouldBe(HttpStatusCode.Created, await response.ReadTextAsync());
        var created = await response.ReadJsonObjectAsync();
        return Guid.Parse((string)created["id"]!);
    }

    /// <summary>The registry's simplest entity — <c>name</c> is its only required field and it holds no ref.</summary>
    private static JsonObject Owner(string name) => new() { ["name"] = name };
}
