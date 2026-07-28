using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using MMLib.Alvo.Api.Internal;
using MMLib.Alvo.Data;
using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace MMLib.Alvo.Api.Tests;

/// <summary>
/// The Data API's refusals as an RFC 9457 contract: the media type, the <c>type</c> URI that classifies the
/// refusal, the <c>violations</c> array, and the claim that the catalogue of slugs has no entry Alvo cannot
/// actually produce.
/// </summary>
/// <remarks>
/// <para>
/// A refusal is the part of an API an agent reads most and a human reads least, so it is the part where an
/// unenforced promise survives longest. Every fact here is written against the wire — the status, the header,
/// the JSON members — rather than against the factory's return value, because "the endpoint calls the
/// factory" and "the caller receives a problem document" are two different claims and only the second one is
/// the contract.
/// </para>
/// <para>
/// <b>Two 403s that must be distinguishable, and only by their slug.</b> A policy refusal and a
/// scope refusal have different fixes — change a rule, or grant the key a scope — and they share a status
/// code. Before there was a <c>type</c>, the only thing that could tell them apart was the <c>detail</c>
/// prose, which RFC 9457 §3.1.1 explicitly says a client "ought not" parse; asserting that literal made a
/// test the schema oracle the deny-reason wording is worded to avoid. The slug is the supported way to
/// branch, and <see cref="Two_403s_with_different_fixes_are_told_apart_by_their_slug"/> is where that is
/// held.
/// </para>
/// </remarks>
public sealed class ProblemDetailsTests
{
    private static readonly TestApiKey _admin = new("admin-key", ["admin", "authenticated"], ["*:read", "*:write"]);

    /// <summary>
    /// Every problem <c>type</c> is a <c>https://alvo.dev/errors/…</c> URI and every 422 carries its
    /// violations — not the framework's default status-code URI, which classifies a refusal by the number the
    /// caller already read off the status line.
    /// </summary>
    /// <remarks>
    /// Two refusals from two different code paths (the query parser and the record validator) are asserted,
    /// because they render through different factory entry points and a call site left on
    /// <c>Results.Problem</c>'s default is invisible in any single-path fact.
    /// </remarks>
    [Fact]
    public async Task Every_problem_response_carries_the_alvo_dev_type_uri_and_the_violations_array()
    {
        await using var world = await AlvoApiWorld.VehicleRegistryAsync([_admin]);

        using var badQuery = await world.SendAsync(HttpMethod.Get, "/api/owners?limit=0", _admin);
        using var badBody = await world.SendAsync(
            HttpMethod.Post, "/api/owners", _admin, body: new JsonObject { ["name"] = new string('n', 200) });

        (await badQuery.ReadProblemTypeAsync()).ShouldBe(AlvoProblemTypes.MalformedQuery);
        (await badQuery.ReadViolationsAsync()).ShouldNotBeEmpty();
        (await badBody.ReadProblemTypeAsync()).ShouldBe(AlvoProblemTypes.Validation);
        (await badBody.ReadViolationsAsync()).ShouldBe([("/name", "max-length")]);
    }

    /// <summary>
    /// RFC 9457 §3 requires <c>application/problem+json</c>, and it is what lets a client tell a problem
    /// document from a resource representation without guessing from the status code.
    /// </summary>
    /// <remarks>
    /// The successful control is here on purpose: <c>application/json</c> for a 200 and
    /// <c>application/problem+json</c> for a refusal is the distinction, and a server that answered
    /// <c>problem+json</c> for everything would satisfy the refusal half alone.
    /// </remarks>
    [Fact]
    public async Task A_problem_response_is_application_problem_json()
    {
        await using var world = await AlvoApiWorld.VehicleRegistryAsync([_admin]);

        using var refused = await world.SendAsync(HttpMethod.Get, "/api/owners?limit=0", _admin);
        using var succeeded = await world.SendAsync(HttpMethod.Get, "/api/owners", _admin);

        refused.Content.Headers.ContentType!.MediaType.ShouldBe("application/problem+json");
        succeeded.StatusCode.ShouldBe(HttpStatusCode.OK);
        succeeded.Content.Headers.ContentType!.MediaType.ShouldBe(
            "application/json", "a success is not a problem document, or the media type distinguishes nothing");
    }

    /// <summary>
    /// The two 403s: a policy refusal is <c>forbidden</c>, and a key whose scopes exclude the entity is
    /// <c>out-of-scope</c>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This fact replaces Task 3's assertion on the <c>detail</c> literal. That assertion worked, and it
    /// worked for the wrong reason: it pinned the exact wording of <c>IPolicyEngine</c>'s deny reason into a
    /// test, so a refusal's prose could not be made <em>more</em> generic without a test failing — the prose
    /// being generic is the security property, and a test holding it fixed is a test holding the property
    /// hostage. The slug is the classification RFC 9457 says to branch on, and it says nothing about why.
    /// </para>
    /// <para>
    /// Both halves are needed. Asserting only the scope refusal would pass on a server that answered
    /// <c>out-of-scope</c> for every 403, and asserting only the policy refusal would pass on one that
    /// answered <c>forbidden</c> for both — which is the state this task started from.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task Two_403s_with_different_fixes_are_told_apart_by_their_slug()
    {
        var narrow = new TestApiKey("narrow-key", ["authenticated"], ["vehicles:read"]);
        await using var world = await AlvoApiWorld.VehicleRegistryAsync([_admin, narrow]);

        // No credential at all: a real anon caller the descriptor's rules refuse, inside the port.
        using var refusedByPolicy = await world.SendAsync(
            HttpMethod.Post, "/api/owners", body: new JsonObject { ["name"] = "Anonymous Ltd" });

        // A resolvable key whose scopes do not cover this entity: refused above the port, by the gate.
        using var refusedByScope = await world.SendAsync(HttpMethod.Get, "/api/owners", narrow);

        refusedByPolicy.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        refusedByScope.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await refusedByPolicy.ReadProblemTypeAsync()).ShouldBe(AlvoProblemTypes.Forbidden);
        (await refusedByScope.ReadProblemTypeAsync()).ShouldBe(
            AlvoProblemTypes.OutOfScope,
            "the two 403s have different fixes, and the slug is the only supported way to tell them apart");
    }

    /// <summary>
    /// A slug keys on the refusal's <b>kind</b>, never on its <b>reason</b>: a row that does not exist and a
    /// row this caller may not read share one status <em>and</em> one <c>type</c>.
    /// </summary>
    /// <remarks>
    /// The <c>type</c> is the machine-readable half of the document, so a second slug here would hand back
    /// exactly what the shared 404 wording withholds — and it would do it to a client that never has to parse
    /// prose. The whole body is compared, not only the slug, because a differing <c>detail</c> is the same
    /// oracle one layer down.
    /// </remarks>
    [Fact]
    public async Task An_absent_row_and_an_invisible_row_share_one_slug()
    {
        var reader = new TestApiKey("reader-key", ["authenticated"], ["items:read", "items:write", "catalogs:read", "catalogs:write"]);
        await using var world = await AlvoApiWorld.FromDescriptorAsync("validated-records.alvo.json", [reader]);
        var invisible = await SeedInvisibleCatalogAsync(world, reader);

        using var unreadable = await world.SendAsync(HttpMethod.Get, $"/api/catalogs/{invisible}", reader);
        using var absent = await world.SendAsync(HttpMethod.Get, $"/api/catalogs/{Guid.NewGuid()}", reader);

        unreadable.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await unreadable.ReadProblemTypeAsync()).ShouldBe(AlvoProblemTypes.NotFound);
        (await unreadable.ReadTextAsync()).ShouldBe(
            await absent.ReadTextAsync(),
            "an invisible row must read exactly like an absent one — in the type as much as in the prose");
    }

    /// <summary>
    /// <b>No slug in the catalogue is one the factory cannot produce.</b> A catalogue with an entry nothing
    /// emits is documentation of a behaviour that does not exist, and an agent branching on it waits forever
    /// for a classification it will never see.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Every factory entry point is executed against a real <see cref="HttpContext"/> and the <c>type</c> is
    /// read off the bytes it wrote, so this measures what a caller would receive rather than what a constant
    /// says. Asserted as set <em>equality</em>: a slug added to <see cref="AlvoProblemTypes.All"/> with no
    /// producer fails it, and a producer emitting a slug the catalogue does not declare fails it too.
    /// </para>
    /// <para>
    /// The <see cref="ProblemResultFactory.GuardAsync"/> arms are driven by throwing each of the port's
    /// failure families, which is how <c>precondition-failed</c> and <c>idempotency-conflict</c> are reached:
    /// those two are not yet reachable over HTTP (Tasks 6 and 7 add the <c>If-Match</c> and
    /// <c>Idempotency-Key</c> headers that let a caller cause them), and
    /// <see cref="Only_the_slugs_awaiting_a_later_task_are_unreachable_over_http"/> pins exactly which ones
    /// are still pending so finishing either task cannot quietly leave that list stale.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task Every_problem_type_slug_is_one_the_factory_actually_emits()
    {
        var emitted = new List<string>();
        foreach (var result in EveryFactoryResult())
        {
            emitted.Add(await SlugWrittenByAsync(result));
        }

        emitted.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ShouldBe(
            AlvoProblemTypes.All.Order(StringComparer.Ordinal),
            "a slug nothing emits documents a behaviour that does not exist; a slug the catalogue omits cannot be branched on");
    }

    /// <summary>
    /// Every slug the Data API can answer with today <b>is</b> answered with, over a real endpoint — and the
    /// two that are not yet reachable are named, so the task that makes them reachable has to come back here.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The pending set is asserted in <em>both</em> directions: each pending slug must still be unreachable,
    /// and every other slug must be reachable. So Task 6 wiring <c>If-Match</c> fails this fact until
    /// <c>precondition-failed</c> is moved out of the list and given an endpoint-level fact — which is the
    /// point. A one-directional allow-list would silently absorb them forever.
    /// </para>
    /// <para>
    /// Reachability is measured by driving real requests, one per slug, against a real store — not by
    /// enumerating the catalogue, which is the shape of "fact" that cannot fail.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task Only_the_slugs_awaiting_a_later_task_are_unreachable_over_http()
    {
        await using var world = await AlvoApiWorld.VehicleRegistryAsync([_admin, _narrow]);
        var reached = new List<string>();
        foreach (var probe in EveryReachableSlugProbe())
        {
            reached.Add(await SlugAnsweredByAsync(world, probe));
        }

        reached.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ShouldBe(
            AlvoProblemTypes.All.Except(PendingUntilALaterTask, StringComparer.Ordinal).Order(StringComparer.Ordinal),
            "every slug not pending a later task must be reachable from an endpoint");

        PendingUntilALaterTask.ShouldBe(
            [AlvoProblemTypes.PreconditionFailed, AlvoProblemTypes.IdempotencyConflict],
            ignoreOrder: true,
            "these two need Task 6's If-Match and Task 7's Idempotency-Key before a caller can cause them; "
            + "when either lands, move its slug into a probe above rather than leaving it parked here");
    }

    /// <summary>
    /// The slugs no request can yet produce, because the header that causes them is not wired up.
    /// </summary>
    /// <remarks>
    /// <c>ProblemResultFactory.GuardAsync</c> already maps both exception families — the mapping is
    /// <c>IAlvoData</c>'s contract and was settled in Task 3 — so what is missing is a caller-facing way to
    /// raise them, not the rendering.
    /// </remarks>
    private static string[] PendingUntilALaterTask =>
        [AlvoProblemTypes.PreconditionFailed, AlvoProblemTypes.IdempotencyConflict];

    private static readonly TestApiKey _narrow = new("narrow-key", ["authenticated"], ["vehicles:read"]);

    /// <summary>One request per slug the Data API can answer with today, each reaching a different refusal path.</summary>
    private static IEnumerable<Probe> EveryReachableSlugProbe() =>
    [
        // A body the entity's schema refuses.
        new(HttpMethod.Post, "/api/owners", _admin, new JsonObject { ["name"] = new string('n', 200) }),

        // A query string the parser refuses.
        new(HttpMethod.Get, "/api/owners?limit=0", _admin, null),

        // An anonymous caller the descriptor's own rules deny, inside the port.
        new(HttpMethod.Post, "/api/owners", null, new JsonObject { ["name"] = "Anonymous Ltd" }),

        // A resolvable key whose scopes exclude this entity.
        new(HttpMethod.Get, "/api/owners", _narrow, null),

        // A row id nothing was ever created for.
        new(HttpMethod.Get, "/api/owners/8e2b1f5c-0000-4000-8000-000000000000", _admin, null),

        // A credential that was presented and cannot be resolved.
        new(HttpMethod.Get, "/api/owners", new TestApiKey("ghost-key", ["admin"], ["*:read"]), null),
    ];

    /// <summary>Every problem the factory can render, one per entry point and per <c>GuardAsync</c> arm.</summary>
    /// <remarks>
    /// Written out rather than discovered by reflection: a reflective enumeration would pass over a new entry
    /// point that returns the wrong slug, because it would read the slug from the same place it read the
    /// method. Each line is a claim someone made deliberately.
    /// </remarks>
    private static IEnumerable<IResult> EveryFactoryResult() =>
    [
        ProblemResultFactory.Validation([Violation()]),
        ProblemResultFactory.MalformedQuery([Violation()]),
        ProblemResultFactory.Malformed("a malformed request"),
        ProblemResultFactory.NotFound(),
        ProblemResultFactory.ScopeRefused(),
        ProblemResultFactory.Unauthenticated("X-Alvo-Api-Key"),
        Guarded(new AlvoAuthorizationException("refused")),
        Guarded(new AlvoRecordNotFoundException()),
        Guarded(new AlvoPreconditionFailedException("stale")),
        Guarded(new AlvoIdempotencyConflictException("reused")),
        Guarded(new ArgumentException("malformed")),
    ];

    private static AlvoViolation Violation() => new("/field", "code", "A message.", "A fix.");

    /// <summary>The result <c>GuardAsync</c> renders for one of the port's failure families.</summary>
    private static IResult Guarded(Exception failure) =>
        ProblemResultFactory.GuardAsync(() => throw failure).GetAwaiter().GetResult();

    /// <summary>The slug in the <c>type</c> of the document <paramref name="result"/> writes.</summary>
    private static async Task<string> SlugWrittenByAsync(IResult result)
    {
        // ASP.NET Core's own ProblemHttpResult resolves IProblemDetailsService and the JSON options from the
        // request's services, so the result has to be executed against a container rather than a bare context
        // — which is also the point of executing it at all: this measures the bytes a caller receives, written
        // by the same writer the live endpoints use.
        var services = new ServiceCollection().AddLogging().AddProblemDetails().BuildServiceProvider();
        await using (services)
        {
            return await SlugWrittenByAsync(result, services);
        }
    }

    private static async Task<string> SlugWrittenByAsync(IResult result, IServiceProvider services)
    {
        var context = new DefaultHttpContext { RequestServices = services };
        using var body = new MemoryStream();
        context.Response.Body = body;

        await result.ExecuteAsync(context);

        body.Position = 0;
        var document = await JsonSerializer.DeserializeAsync<JsonObject>(
            body, cancellationToken: TestContext.Current.CancellationToken);
        var type = document?["type"]?.GetValue<string>()
            ?? throw new InvalidOperationException($"A factory result wrote no 'type': {result.GetType().Name}.");

        return type.StartsWith(AlvoProblemTypes.BaseUri, StringComparison.Ordinal)
            ? type[AlvoProblemTypes.BaseUri.Length..]
            : throw new InvalidOperationException($"A factory result wrote a foreign 'type': {type}.");
    }

    private static async Task<string> SlugAnsweredByAsync(AlvoApiWorld world, Probe probe)
    {
        using var response = await world.SendAsync(probe.Method, probe.Path, probe.Key, body: probe.Body);
        return await response.ReadProblemTypeAsync();
    }

    private static async Task<Guid> SeedInvisibleCatalogAsync(AlvoApiWorld world, TestApiKey key)
    {
        var body = new JsonObject { ["name"] = "invisible", ["visible"] = false };
        using var response = await world.SendAsync(HttpMethod.Post, "/api/catalogs", key, body: body);
        response.StatusCode.ShouldBe(
            HttpStatusCode.Created, "the invisible row must really be created, or the comparison below is vacuous");
        return (await response.ReadJsonObjectAsync())["id"]!.GetValue<Guid>();
    }

    /// <summary>One request that must earn one slug.</summary>
    /// <param name="Method">The HTTP method.</param>
    /// <param name="Path">The request path.</param>
    /// <param name="Key">The key to present, or <see langword="null"/> for an anonymous caller.</param>
    /// <param name="Body">The body to send, or <see langword="null"/> for none.</param>
    private sealed record Probe(HttpMethod Method, string Path, TestApiKey? Key, JsonObject? Body);
}
