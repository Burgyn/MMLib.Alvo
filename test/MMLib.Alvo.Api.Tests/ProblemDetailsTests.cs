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
    /// A refused query and a refused body each carry an <c>alvo.dev/errors</c> <c>type</c> and their
    /// <c>violations</c> — not the framework's default status-code URI, which classifies a refusal by the
    /// number the caller already read off the status line.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Named for the two paths it drives rather than for "every problem response", which it cannot show: the
    /// claim about <em>every</em> slug belongs to
    /// <see cref="Every_problem_type_slug_is_one_the_factory_actually_emits"/> and
    /// <see cref="Only_the_slugs_awaiting_a_later_task_are_unreachable_over_http"/>, which enumerate the
    /// catalogue and drive one request per slug. A fact whose name promises more than it asserts is how a gap
    /// comes to look covered.
    /// </para>
    /// <para>
    /// Two paths and not one, because the query parser and the record validator render through different
    /// factory entry points: a single call site left on <c>Results.Problem</c>'s default is invisible in a
    /// one-path fact.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task A_refused_query_and_a_refused_body_both_carry_an_alvo_type_and_their_violations()
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
    /// <para>
    /// <b>What this adds, now that the media type is pinned per slug.</b> Both per-slug helpers assert it —
    /// over every factory entry point and every endpoint probe — so the refusal half of this fact is covered
    /// many times over. What is left here, and nowhere else, is the <em>success</em> half: a 200 must be
    /// <c>application/json</c>, because a server answering <c>problem+json</c> for everything would satisfy
    /// every refusal assertion in the suite and still be wrong.
    /// </para>
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
    /// <c>GuardAsync</c> renders a <b>malformed-argument</b> refusal from the port as a 422, and lets a
    /// <b>null-argument</b> failure propagate — because the second is Alvo's own broken invariant, not a
    /// caller's malformed request.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <c>IAlvoData</c>'s family table says "<c>ArgumentException</c>, including its derived types" is the
    /// malformed-query channel, and <see cref="ArgumentNullException"/> derives from it — so the widest arm
    /// swallowed it. A request cannot express a null argument: reaching that arm means this layer or the port
    /// passed a null where its own contract forbids one, which is family 5 (rendered 500 by the host, with the
    /// stack trace its logging exists to record). Rendering it as 422 tells the caller to fix a request that
    /// was fine — the same laundering the payload reader's <c>NotSupportedException</c> arm was fixed for.
    /// </para>
    /// <para>
    /// It matters now rather than in principle: the region <c>GuardAsync</c> wraps grew several
    /// <c>ArgumentNullException.ThrowIfNull</c> calls of its own when validation and the format catalogue
    /// landed inside it.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task A_malformed_argument_is_rendered_but_a_null_argument_propagates()
    {
        var malformed = await ProblemResultFactory.GuardAsync(
            () => throw new ArgumentException("the filter is not a filter", "query"));

        (await SlugWrittenByAsync(malformed)).ShouldBe(
            AlvoProblemTypes.MalformedQuery, "the port's malformed-query channel is a 422 the caller can act on");

        var propagated = await Should.ThrowAsync<ArgumentNullException>(
            () => ProblemResultFactory.GuardAsync(() => throw new ArgumentNullException("values")));

        propagated.ParamName.ShouldBe(
            "values",
            "a null argument is Alvo's own broken invariant — it must reach the host with its stack trace, "
            + "never be rendered to the caller as a malformed request");
    }

    /// <summary>
    /// <see cref="AlvoProblemTypes.UriOf"/> mints a URI only for a slug the catalogue declares, and refuses
    /// anything else — so a call site cannot invent a <c>type</c> that no documentation exists for.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The guard is the whole reason <see cref="AlvoProblemTypes.UriOf"/> exists rather than a string
    /// concatenation at each call site: an un-catalogued slug would be an <c>alvo.dev/errors/…</c> URI
    /// resolving to nothing, which is worse than the framework default because it <em>looks</em> documented.
    /// It had no fact, so deleting it changed nothing observable.
    /// </para>
    /// <para>
    /// The refusal names the slug and lists the declared ones, because the caller of this method is a
    /// framework author reading an exception message, and "invalid slug" without the list is a trip to the
    /// source.
    /// </para>
    /// </remarks>
    [Fact]
    public void UriOf_mints_a_uri_only_for_a_declared_slug()
    {
        foreach (var slug in AlvoProblemTypes.All)
        {
            AlvoProblemTypes.UriOf(slug).ShouldBe(AlvoProblemTypes.BaseUri + slug);
        }

        var refused = Should.Throw<ArgumentException>(() => AlvoProblemTypes.UriOf("quota-exceeded"));

        refused.Message.ShouldContain("quota-exceeded");
        refused.Message.ShouldContain(
            AlvoProblemTypes.NotFound, Case.Sensitive, "the refusal lists the declared slugs, or it sends the reader to the source");
        Should.Throw<ArgumentException>(() => AlvoProblemTypes.UriOf(AlvoProblemTypes.NotFound.ToUpperInvariant()))
            .ShouldNotBeNull("the comparison is ordinal — a slug is a wire token, not a word");
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
    /// one that is not yet reachable is named, so the task that makes it reachable has to come back here.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The pending set is asserted in <em>both</em> directions: each pending slug must still be unreachable,
    /// and every other slug must be reachable. Task 6 wiring <c>If-Match</c> failed this fact until
    /// <c>precondition-failed</c> was moved out of the list and given the probe below — which was the point,
    /// and Task 7's <c>Idempotency-Key</c> owes it the same visit.
    /// </para>
    /// <para>
    /// <b>The "still unreachable" direction is a request, not a list comparison.</b> It used to compare
    /// <c>PendingUntilALaterTask</c> with its own literal, which could only fail on the cleanup edit — so
    /// Task 6 landing did not fail it, and would not have failed it had the slug been left parked. The pending
    /// slug is now driven by a request that <em>presents the header that causes it</em>
    /// (<see cref="ADuplicateIdempotencyKeyIsStillIgnored"/>): today the header is inert and the second write
    /// succeeds, so the day Task 7 honours it, that assertion fails and the list has to shrink.
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

        await ADuplicateIdempotencyKeyIsStillIgnored(world);
    }

    /// <summary>
    /// Presents <c>Idempotency-Key</c> twice with two <em>different</em> bodies and asserts the header is
    /// still inert — the one thing that makes <see cref="AlvoProblemTypes.IdempotencyConflict"/>'s place on
    /// the pending list a fact rather than a note.
    /// </summary>
    /// <remarks>
    /// A reused key with a different fingerprint is precisely what <c>IAlvoData</c> answers
    /// <c>AlvoIdempotencyConflictException</c> for, so once Task 7 turns the header into an
    /// <c>AlvoIdempotency</c> token the second request becomes a 409 and this assertion fails — forcing the
    /// slug out of <see cref="PendingUntilALaterTask"/> and into a probe. Until then the two creates both
    /// succeed and produce two distinct rows, which is the behaviour a caller sees today and the reason the
    /// slug is unreachable.
    /// </remarks>
    /// <param name="world">The running API.</param>
    private static async Task ADuplicateIdempotencyKeyIsStillIgnored(AlvoApiWorld world)
    {
        var key = new Dictionary<string, string>(StringComparer.Ordinal) { ["Idempotency-Key"] = "task-7-owes-this" };

        using var first = await world.SendAsync(
            HttpMethod.Post, "/api/owners", _admin, body: new JsonObject { ["name"] = "First Ltd" }, headers: key);
        using var second = await world.SendAsync(
            HttpMethod.Post, "/api/owners", _admin, body: new JsonObject { ["name"] = "Second Ltd" }, headers: key);

        first.StatusCode.ShouldBe(HttpStatusCode.Created);
        second.StatusCode.ShouldBe(
            HttpStatusCode.Created,
            "the same Idempotency-Key with a different body is still accepted, so 'idempotency-conflict' is "
            + "genuinely unreachable — when Task 7 makes this a 409, move the slug out of the pending list");
        (await second.ReadJsonObjectAsync())["id"]!.GetValue<Guid>().ShouldNotBe(
            (await first.ReadJsonObjectAsync())["id"]!.GetValue<Guid>(),
            "two rows, not a replay — the header is inert rather than half-honoured");
    }

    /// <summary>
    /// The slugs no request can yet produce, because the header that causes them is not honoured yet.
    /// </summary>
    /// <remarks>
    /// <c>ProblemResultFactory.GuardAsync</c> already maps the exception family — the mapping is
    /// <c>IAlvoData</c>'s contract and was settled in Task 3 — so what is missing is a caller-facing way to
    /// raise it, not the rendering. That "missing" is asserted by
    /// <see cref="ADuplicateIdempotencyKeyIsStillIgnored"/> rather than described here.
    /// </remarks>
    private static string[] PendingUntilALaterTask => [AlvoProblemTypes.IdempotencyConflict];

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

        // An If-Match against an entity that keeps no version of a row, so it cannot answer the question.
        // Decided from the schema alone, before any row is looked up, which is why the id needs to exist no
        // more than the query-parser probe's does. ConcurrencyTests owns the behaviour; this owns the slug.
        new(
            HttpMethod.Patch,
            "/api/inspections/8e2b1f5c-0000-4000-8000-000000000000",
            _admin,
            new JsonObject { ["notes"] = "conditional" },
            new Dictionary<string, string>(StringComparer.Ordinal) { ["If-Match"] = "\"638000000000000000\"" }),
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

        // RFC 9457 §3 requires application/problem+json, and pinning it *here* rather than in one fact is
        // what makes the requirement hold for every entry point: this helper executes all of them, so one
        // line covers N results instead of the single path a dedicated fact could reach. The regression it
        // catches is a new call site that bypasses the factory — the 401 challenge being likeliest, since it
        // is the one result that wraps another.
        context.Response.ContentType.ShouldBe(
            ProblemMediaType, "every problem this factory writes must be a problem document on the wire");

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
        using var response = await world.SendAsync(
            probe.Method, probe.Path, probe.Key, body: probe.Body, headers: probe.Headers);

        // The same pin, over the live API: every refusal a real endpoint produces is a problem document,
        // not only the two A_problem_response_is_application_problem_json happens to drive.
        response.Content.Headers.ContentType!.MediaType.ShouldBe(
            ProblemMediaType, $"{probe.Method} {probe.Path} answered a refusal that is not a problem document");

        return await response.ReadProblemTypeAsync();
    }

    /// <summary>The media type RFC 9457 §3 requires of a problem document.</summary>
    private const string ProblemMediaType = "application/problem+json";

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
    /// <param name="Headers">Any further request headers the refusal needs, by name.</param>
    private sealed record Probe(
        HttpMethod Method,
        string Path,
        TestApiKey? Key,
        JsonObject? Body,
        IReadOnlyDictionary<string, string>? Headers = null);
}
