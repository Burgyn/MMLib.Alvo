# Embedded sample + JSON `Content-Type` guard — implementation plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended)
> or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax
> for tracking.

**Goal:** Ship the embedded-run sample #24 asks for, and close the `Content-Type` hole #191 describes, in
one PR — because the sample is the first thing that puts Alvo inside somebody else's pipeline and the
guard is what that context needs.

**Architecture:** Two independent halves. (A) `samples/MMLib.Alvo.Samples.EmbeddedHost` — an ASP.NET Core
host with cookie auth for its own users, which reaches Alvo two ways: its own `/app/*` endpoints call
`IAlvoData` with an `AlvoContext` it builds itself, and `MapAlvoHealth()` + `MapAlvoDataApi()` mount
Alvo's generated Data API under `/api/alvo` for API-key callers. (B) A `JsonContentType` guard called by
the five body-taking delegates, right after the policy decision and before the body read, answering 415
with a new `unsupported-media-type` problem slug, switchable off by `AlvoApiOptions.RequireJsonContentType`.

**Tech Stack:** .NET 10 (`net10.0`), minimal APIs, xunit.v3 + Shouldly on Microsoft.Testing.Platform,
Verify for the OpenAPI snapshot, `WebApplicationFactory` for the sample suite, SQLite for both.

**Spec:** `docs/superpowers/specs/2026-09-09-f4-embedded-sample-and-content-type-design.md` — read it
first; every task below argues from a numbered section of it.

> **Superseded in five places. The spec is the record; this plan is what was tried.** Implementation and
> three review rounds reversed: `AlvoApiOptions.RequireJsonContentType` **does not exist** — the guard is
> unconditional and the spec's §4.2(d) says why, so every mention of the option below is stale, including
> Task 1, Task 3's options threading, and Task 4's saboteur; the sample's write endpoint is
> `PATCH /app/vehicles/{id}` on `vehicles.update`, not a create (`vehicles.create` admits `admin` only);
> the suite uses `TestServer` over each host's own `CreateBuilder`/`Build` seam, not
> `WebApplicationFactory`; the DoD compares route sets off `EndpointDataSource`, not a served OpenAPI path
> set; and the guard is the *first* header guard rather than one placed after `EnsureUnconditional`.

## Global Constraints

- **C# files are UTF-8 with BOM and CRLF line endings.** `.gitattributes` pins `*.cs text eol=crlf` and
  `.editorconfig` asks for the BOM. A file written with LF-only fails the pre-commit `dotnet format`
  check. After creating any `.cs` file with a tool that writes LF, normalise it before committing:
  `python3 -c "import sys,io;p=sys.argv[1];d=open(p,'rb').read().replace(b'\r\n',b'\n').replace(b'\n',b'\r\n');open(p,'wb').write(d if d[:3]==b'\xef\xbb\xbf' else b'\xef\xbb\xbf'+d)" <path>`
- **`TreatWarningsAsErrors=true`, `AnalysisLevel=latest-recommended`, `EnforceCodeStyleInBuild=true`.**
  Every public member in `src/` needs an XML doc comment (`GenerateDocumentationFile` is on for `src`).
- **Short, single-purpose methods.** Extract aggressively; ~25 lines is the ceiling the maintainer holds.
- **No inline package versions.** Central Package Management only — `Directory.Packages.props`.
- **No redeclared inherited MSBuild properties** (`TargetFramework`, `Nullable`, `ImplicitUsings`,
  `LangVersion`) in any csproj — `SolutionConventionTests` fails on it.
- **Every project must be named `MMLib.Alvo` or `MMLib.Alvo.*`** and registered in `MMLib.Alvo.slnx`.
- **Rings:** `scripts/test-ring0` after every step, `scripts/test-ring1` after each half,
  `scripts/test-ring2` before the PR. Never run mutation or e2e locally.
- **Never commit to `main`.** The branch is `feat/24-191-embedded-sample-content-type`.
- **Conventional Commits**, and every commit message ends with
  `Claude-Session: https://claude.ai/code/session_01LMBssp3LFUYfqfbWPZ9pk5`.

## File Structure

| File | Responsibility |
|---|---|
| `src/MMLib.Alvo/Api/AlvoApiOptions.cs` | *modify* — add `RequireJsonContentType` |
| `src/MMLib.Alvo/Api/AlvoProblemTypes.cs` | *modify* — add the `unsupported-media-type` slug + `All` entry |
| `src/MMLib.Alvo/Api/Internal/JsonContentType.cs` | **create** — the whole guard: match, decide, build the refusal |
| `src/MMLib.Alvo/Api/Internal/ProblemResultFactory.cs` | *modify* — `UnsupportedMediaType(...)` + the `Accept-*` result wrapper |
| `src/MMLib.Alvo/Api/Internal/DataApiEndpoints.cs` | *modify* — five guard call sites; thread `options` into `Protect`/`Documenting` |
| `src/MMLib.Alvo/Api/Internal/DataApiDocumentation.cs` | *modify* — the 415 `Response`; `ResponsesFor`/`SharedRefusals` take `AlvoApiOptions` |
| `src/MMLib.Alvo/Api/Internal/AlvoDocumentTransformer.cs` | *modify* — pass `options` to the two document call sites |
| `src/MMLib.Alvo/Api/Internal/DataApiHeaders.cs` | *modify* — pass `options` through `AddTo` |
| `test/MMLib.Alvo.Api.Tests/DataApiContentTypeTests.cs` | **create** — the whole media-type matrix + ordering |
| `test/MMLib.Alvo.Api.Tests/OpenApiDocumentTests.cs` | *modify* — the reachability arm + the omit-when-off fact |
| `test/MMLib.Alvo.Tests/PublicApi.MMLib.Alvo.verified.txt` | *accept* — two added public members |
| `test/MMLib.Alvo.Api.Invariants.Tests.Integration/BehaviourInvariants.cs` | *modify* — the cross-descriptor invariant |
| `test/MMLib.Alvo.Api.Invariants.Tests.Integration/Sabotage.cs` | *modify* — the saboteur that turns it off |
| `samples/Directory.Build.props` | **create** — imports the parent, `IsPackable=false` |
| `samples/MMLib.Alvo.Samples.EmbeddedHost/*` | **create** — csproj, `Program.cs`, `appsettings.json`, `README.md` |
| `test/MMLib.Alvo.Samples.EmbeddedHost.Tests.Integration/*` | **create** — the sample's ring2 proof |
| `docs/architecture/data-api.md`, `extensibility.md`, `host.md` | *modify* — the records of the guard, the sample and the correction |
| `docs/PLAN.md`, `README.md`, `examples/README.md` | *modify* — F4's remaining work is done |

---

## Task 1: The two public members

**Files:**
- Modify: `src/MMLib.Alvo/Api/AlvoApiOptions.cs` (after `MaxIdempotencyKeyBytes`)
- Modify: `src/MMLib.Alvo/Api/AlvoProblemTypes.cs` (after `UnreadableRequest`, and in `All`)
- Test: `test/MMLib.Alvo.Api.Tests/DataApiContentTypeTests.cs` (created here, grown in Task 2)
- Accept: `test/MMLib.Alvo.Tests/PublicApi.MMLib.Alvo.verified.txt`

**Interfaces:**
- Produces: `AlvoApiOptions.RequireJsonContentType` → `bool`, default `true`.
  `AlvoProblemTypes.UnsupportedMediaType` → `const string "unsupported-media-type"`, present in
  `AlvoProblemTypes.All`.
- Consumes: nothing.

- [ ] **Step 1: Write the failing tests**

Create `test/MMLib.Alvo.Api.Tests/DataApiContentTypeTests.cs`:

```csharp
using MMLib.Alvo.Api;

namespace MMLib.Alvo.Api.Tests;

/// <summary>
/// The JSON <c>Content-Type</c> guard (#191): which media types a body-taking route accepts, what it
/// answers when it refuses, and where the refusal sits relative to authorization.
/// </summary>
public class DataApiContentTypeTests
{
    /// <summary>
    /// The default is the whole security value of the feature, and nothing else in this file can hold it:
    /// every other fact either sends JSON or sets the option explicitly, so flipping the initializer to
    /// <see langword="false"/> would leave them all green.
    /// </summary>
    [Fact]
    public void The_guard_is_on_by_default() =>
        new AlvoApiOptions().RequireJsonContentType.ShouldBeTrue(
            "secure-by-default: a host must opt out of the guard, never into it");

    /// <summary>
    /// The slug is a published constant an embedded host branches on, so it is enumerated in
    /// <see cref="AlvoProblemTypes.All"/> and mintable through <see cref="AlvoProblemTypes.UriOf"/> —
    /// which throws for a slug the catalogue does not declare.
    /// </summary>
    [Fact]
    public void The_unsupported_media_type_slug_is_in_the_catalogue()
    {
        AlvoProblemTypes.All.ShouldContain(AlvoProblemTypes.UnsupportedMediaType);
        AlvoProblemTypes.UriOf(AlvoProblemTypes.UnsupportedMediaType)
            .ShouldBe("https://alvo.dev/errors/unsupported-media-type");
    }
}
```

- [ ] **Step 2: Run them to verify they fail**

Run: `dotnet test test/MMLib.Alvo.Api.Tests --filter-class MMLib.Alvo.Api.Tests.DataApiContentTypeTests`
Expected: FAIL to **compile** — `RequireJsonContentType` and `UnsupportedMediaType` do not exist.

- [ ] **Step 3: Add the option**

In `src/MMLib.Alvo/Api/AlvoApiOptions.cs`, after `MaxIdempotencyKeyBytes`:

```csharp
    /// <summary>
    /// Whether a body-taking endpoint requires a JSON <c>Content-Type</c>. Default <see langword="true"/>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>It is a CSRF guard, and the mechanism is the browser's, not Alvo's.</b> WHATWG Fetch safelists
    /// three <c>Content-Type</c> values — <c>application/x-www-form-urlencoded</c>,
    /// <c>multipart/form-data</c>, <c>text/plain</c> — and a request carrying only safelisted headers is
    /// sent cross-origin with cookies and <em>no preflight</em>; a request with no <c>Content-Type</c> at
    /// all is safelisted by omission. Requiring <c>application/json</c> therefore puts every body-taking
    /// route behind a preflight an HTML form cannot generate. The response being unreadable does not undo
    /// a write, which is why this is about the request and not about CORS response headers.
    /// </para>
    /// <para>
    /// <b>Why it matters in embedded mode specifically.</b> Alvo's own credential is a request header, so
    /// a cross-site form POST arrives anonymous and default-deny answers it. But an embedded host may
    /// move <c>AlvoAuthOptions.HeaderName</c> onto <c>Cookie</c> and register its own
    /// <c>IAlvoContextResolver</c> — public API, no framework change — at which point the browser
    /// authenticates the forgery. See <c>docs/architecture/data-api.md</c>, "Requiring a JSON
    /// Content-Type".
    /// </para>
    /// <para>
    /// <b>Set it to <see langword="false"/> only in a host that has its own CSRF defence</b> — ASP.NET
    /// Core antiforgery, or routes no browser can reach. It restores the previous behaviour exactly,
    /// including the generated document, which then lists no 415 because none is reachable.
    /// </para>
    /// </remarks>
    public bool RequireJsonContentType { get; set; } = true;
```

- [ ] **Step 4: Add the slug**

In `src/MMLib.Alvo/Api/AlvoProblemTypes.cs`, after `UnreadableRequest`:

```csharp
    /// <summary>The request carried a body that is not JSON, or carried no <c>Content-Type</c> at all (415).</summary>
    /// <remarks>
    /// <para>
    /// Distinct from <see cref="MalformedQuery"/> and it is the <em>kind</em> that separates them: this one
    /// means Alvo never looked at the content, because the caller did not declare it as JSON, and the fix is
    /// a header. <see cref="MalformedQuery"/> means Alvo read the content and refused it, and the fix is the
    /// body. Distinct from <see cref="UnreadableRequest"/> too — that one is the web server refusing before
    /// Alvo was reached at all.
    /// </para>
    /// <para>
    /// Answered only while <c>AlvoApiOptions.RequireJsonContentType</c> is set; a host that opted out never
    /// emits it, and its generated document lists it nowhere.
    /// </para>
    /// </remarks>
    public const string UnsupportedMediaType = "unsupported-media-type";
```

and add `UnsupportedMediaType,` to the `All` collection expression, after `UnreadableRequest,`.

- [ ] **Step 5: Run the tests to verify they pass**

Run: `dotnet test test/MMLib.Alvo.Api.Tests --filter-class MMLib.Alvo.Api.Tests.DataApiContentTypeTests`
Expected: PASS (2 tests).

- [ ] **Step 6: Accept the public-API baseline**

Run: `scripts/test-ring1`
Expected: `PublicApiApprovalTests` FAILS for `MMLib.Alvo` with two added members. Accept the `.received.txt`
over the `.verified.txt` (Verify writes both beside each other):

```bash
mv test/MMLib.Alvo.Tests/PublicApi.MMLib.Alvo.received.txt \
   test/MMLib.Alvo.Tests/PublicApi.MMLib.Alvo.verified.txt
```

Confirm the diff is **exactly** those two members and nothing else. The turn-review-gate will block on a
grown `PublicApi.*.verified.txt`; the justification is spec §5 — an option a host cannot see cannot be
opted out of, and `AlvoProblemTypes`' own remarks already state why its slugs are public.

- [ ] **Step 7: Run ring0 and commit**

```bash
scripts/test-ring0
git add src/MMLib.Alvo/Api/AlvoApiOptions.cs src/MMLib.Alvo/Api/AlvoProblemTypes.cs \
        test/MMLib.Alvo.Api.Tests/DataApiContentTypeTests.cs \
        test/MMLib.Alvo.Tests/PublicApi.MMLib.Alvo.verified.txt
git commit -m "feat(api): the option and the slug the Content-Type guard answers with (#191)

Two public members, both under the public-API baseline: RequireJsonContentType
(default true -- a host opts out, never in) and the unsupported-media-type
problem slug. Nothing enforces either yet.

Claude-Session: https://claude.ai/code/session_01LMBssp3LFUYfqfbWPZ9pk5"
```

---

## Task 2: The guard, and the five delegates that call it

**Files:**
- Create: `src/MMLib.Alvo/Api/Internal/JsonContentType.cs`
- Modify: `src/MMLib.Alvo/Api/Internal/ProblemResultFactory.cs`
- Modify: `src/MMLib.Alvo/Api/Internal/DataApiEndpoints.cs` (5 call sites)
- Test: `test/MMLib.Alvo.Api.Tests/DataApiContentTypeTests.cs`

**Interfaces:**
- Consumes: `AlvoApiOptions.RequireJsonContentType`, `AlvoProblemTypes.UnsupportedMediaType` (Task 1).
- Produces: `internal static IResult? JsonContentType.Refuse(HttpRequest request, AlvoApiOptions options)`
  — `null` when the request may proceed. `internal static IResult ProblemResultFactory.UnsupportedMediaType(string method)`.
  `internal static readonly string[] JsonContentType.Accepted` — the media types the `Accept-*` headers advertise.

- [ ] **Step 1: Write the failing tests**

Append to `test/MMLib.Alvo.Api.Tests/DataApiContentTypeTests.cs` (inside the class), and add
`using System.Net;`, `using System.Net.Http.Headers;`, `using System.Text;`, `using System.Text.Json.Nodes;`
at the top:

```csharp
    /// <summary>Every route that reads a request body, as (method, path suffix, body) triples.</summary>
    /// <remarks>
    /// A literal rather than a walk of the endpoint table: the claim is "every body-taking route is
    /// guarded", and taking the set from the same place the routes are built from would make it circular.
    /// Seven routes — the create, the partial update, the create-or-replace, the query, and the three
    /// batch verbs. <c>{id}</c> is substituted per call so no two facts collide on a row.
    /// </remarks>
    private static readonly (HttpMethod Method, string Suffix, string Body)[] _bodyTakingRoutes =
    [
        (HttpMethod.Post, "", """{"vin":"1HGCM82633A004352"}"""),
        (HttpMethod.Patch, "/{id}", """{"color":"red"}"""),
        (HttpMethod.Put, "/{id}", """{"vin":"1HGCM82633A004352"}"""),
        (HttpMethod.Post, "/query", """{"limit":1}"""),
        (HttpMethod.Post, "/batch", """{"rows":[{"vin":"1HGCM82633A004352"}]}"""),
        (HttpMethod.Patch, "/batch", """{"rows":[]}"""),
        (HttpMethod.Delete, "/batch", """{"ids":[]}"""),
    ];

    private static TestApiKey Admin() => new("admin-key", ["admin"], ["*:read", "*:write"]);

    private static string Path(string suffix) =>
        "/api/vehicles" + suffix.Replace("{id}", Guid.NewGuid().ToString(), StringComparison.Ordinal);

    /// <summary>A body sent verbatim under <paramref name="mediaType"/>, or under none at all.</summary>
    /// <param name="json">The body text.</param>
    /// <param name="mediaType">The media type to declare, or <see langword="null"/> to declare none.</param>
    private static HttpContent Body(string json, string? mediaType)
    {
        var content = new StringContent(json, Encoding.UTF8);
        content.Headers.ContentType = mediaType is null ? null : MediaTypeHeaderValue.Parse(mediaType);
        return content;
    }

    /// <summary>
    /// The refusal, on every one of the seven body-taking routes. This is the exhaustiveness fact the
    /// design owes: the guard is called from five delegates, so a sixth body-taking route added later
    /// without a call fails here rather than shipping unguarded.
    /// </summary>
    [Fact]
    public async Task Every_body_taking_route_refuses_a_non_json_media_type()
    {
        await using var world = await AlvoApiWorld.VehicleRegistryAsync([Admin()]);

        foreach (var (method, suffix, json) in _bodyTakingRoutes)
        {
            using var response = await world.SendRawAsync(
                method, Path(suffix), Admin(), content: Body(json, "text/plain"));

            response.StatusCode.ShouldBe(
                HttpStatusCode.UnsupportedMediaType,
                $"{method} {suffix} reads a body, so it must refuse a text/plain declaration");
        }
    }

    /// <summary>The same seven routes accept the media type the document declares.</summary>
    /// <remarks>
    /// The non-vacuity control for the fact above: a guard that refused <em>everything</em> would satisfy
    /// it while breaking the API, and every other fact in the suite sends JSON through the world's own
    /// serializer rather than through this path.
    /// </remarks>
    [Fact]
    public async Task Every_body_taking_route_accepts_application_json()
    {
        await using var world = await AlvoApiWorld.VehicleRegistryAsync([Admin()]);

        foreach (var (method, suffix, json) in _bodyTakingRoutes)
        {
            using var response = await world.SendRawAsync(
                method, Path(suffix), Admin(), content: Body(json, "application/json"));

            response.StatusCode.ShouldNotBe(
                HttpStatusCode.UnsupportedMediaType,
                $"{method} {suffix} must accept the media type the generated document declares");
        }
    }

    /// <summary>A charset parameter is parsed and ignored, not treated as a different media type.</summary>
    [Fact]
    public async Task A_charset_parameter_is_accepted()
    {
        await using var world = await AlvoApiWorld.VehicleRegistryAsync([Admin()]);

        using var response = await world.SendRawAsync(
            HttpMethod.Post,
            Path(""),
            Admin(),
            content: Body("""{"vin":"1HGCM82633A004352"}""", "application/json; charset=utf-8"));

        response.StatusCode.ShouldBe(HttpStatusCode.Created);
    }

    /// <summary>
    /// The <c>+json</c> structured suffix is accepted, so RFC 7396's <c>application/merge-patch+json</c>
    /// works — refusing a <em>more</em> precise declaration of the same bytes would be a defect.
    /// </summary>
    [Fact]
    public async Task The_json_structured_suffix_is_accepted()
    {
        await using var world = await AlvoApiWorld.VehicleRegistryAsync([Admin()]);
        var created = await world.SendAsync(
            HttpMethod.Post, "/api/vehicles", Admin(), body: new JsonObject { ["vin"] = "1HGCM82633A004352" });
        var id = await ResponseReading.IdOfAsync(created);
        created.Dispose();

        using var response = await world.SendRawAsync(
            HttpMethod.Patch,
            $"/api/vehicles/{id}",
            Admin(),
            content: Body("""{"color":"red"}""", "application/merge-patch+json"));

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    /// <summary>A form-urlencoded body is refused — the safelisted value a cross-site form actually sends.</summary>
    [Fact]
    public async Task A_form_urlencoded_body_is_refused()
    {
        await using var world = await AlvoApiWorld.VehicleRegistryAsync([Admin()]);

        using var response = await world.SendRawAsync(
            HttpMethod.Post,
            Path(""),
            Admin(),
            content: Body("vin=1HGCM82633A004352", "application/x-www-form-urlencoded"));

        response.StatusCode.ShouldBe(HttpStatusCode.UnsupportedMediaType);
    }

    /// <summary>
    /// A body with no <c>Content-Type</c> at all is refused, which is the half that actually closes the
    /// vector: a request declaring nothing is CORS-safelisted by omission.
    /// </summary>
    [Fact]
    public async Task A_body_with_no_content_type_is_refused()
    {
        await using var world = await AlvoApiWorld.VehicleRegistryAsync([Admin()]);

        using var response = await world.SendRawAsync(
            HttpMethod.Post, Path(""), Admin(), content: Body("""{"vin":"1HGCM82633A004352"}""", null));

        response.StatusCode.ShouldBe(HttpStatusCode.UnsupportedMediaType);
    }

    /// <summary>The refusal is a problem document carrying the slug, the fix, and no violations array.</summary>
    [Fact]
    public async Task The_refusal_names_the_fix_and_carries_no_violations()
    {
        await using var world = await AlvoApiWorld.VehicleRegistryAsync([Admin()]);

        using var response = await world.SendRawAsync(
            HttpMethod.Post, Path(""), Admin(), content: Body("{}", "text/plain"));
        var problem = await ResponseReading.ProblemAsync(response);

        problem["type"]!.GetValue<string>()
            .ShouldBe(AlvoProblemTypes.UriOf(AlvoProblemTypes.UnsupportedMediaType));
        problem["detail"]!.GetValue<string>().ShouldContain("application/json");
        problem.ContainsKey("violations").ShouldBeFalse(
            "a 415 is about the declaration, not about anything inside the body, so there is nothing to point at");
    }

    /// <summary>
    /// The registered <c>Accept-*</c> headers, so the fix is machine-readable and not only prose —
    /// <c>Accept-Post</c> (W3C LDP 1.0 §7.1.2) and <c>Accept-Patch</c> (RFC 5789 §3.1).
    /// </summary>
    [Fact]
    public async Task The_refusal_advertises_what_the_method_accepts()
    {
        await using var world = await AlvoApiWorld.VehicleRegistryAsync([Admin()]);

        using var post = await world.SendRawAsync(
            HttpMethod.Post, Path(""), Admin(), content: Body("{}", "text/plain"));
        using var patch = await world.SendRawAsync(
            HttpMethod.Patch, Path("/{id}"), Admin(), content: Body("{}", "text/plain"));
        using var put = await world.SendRawAsync(
            HttpMethod.Put, Path("/{id}"), Admin(), content: Body("{}", "text/plain"));

        post.Headers.GetValues("Accept-Post").ShouldHaveSingleItem().ShouldContain("application/json");
        patch.Headers.GetValues("Accept-Patch").ShouldHaveSingleItem()
            .ShouldContain("application/merge-patch+json");
        put.Headers.Contains("Accept-Put").ShouldBeFalse(
            "there is no registered Accept-Put, and inventing one to make the set tidy is worse than omitting it");
    }

    /// <summary>Turning the guard off restores the previous behaviour on every one of the seven routes.</summary>
    [Fact]
    public async Task A_host_that_opted_out_accepts_any_media_type()
    {
        await using var world = await AlvoApiWorld.VehicleRegistryAsync(
            [Admin()], new AlvoApiWorldSetup(ConfigureApi: api => api.RequireJsonContentType = false));

        foreach (var (method, suffix, json) in _bodyTakingRoutes)
        {
            using var response = await world.SendRawAsync(
                method, Path(suffix), Admin(), content: Body(json, "text/plain"));

            response.StatusCode.ShouldNotBe(
                HttpStatusCode.UnsupportedMediaType,
                $"{method} {suffix} must accept anything once the host opted out");
        }
    }

    /// <summary>
    /// A caller the policy denies is answered 403, not 415 — the ordering rule this layer states
    /// explicitly: an unauthorized caller must be told they are unauthorized, not something about the
    /// request they were never going to get to send.
    /// </summary>
    [Fact]
    public async Task A_denied_caller_is_answered_403_and_not_415()
    {
        var reader = new TestApiKey("reader-key", ["authenticated"], ["*:read", "*:write"]);
        await using var world = await AlvoApiWorld.VehicleRegistryAsync([reader]);

        using var response = await world.SendRawAsync(
            HttpMethod.Post, Path(""), reader, content: Body("{}", "text/plain"));

        response.StatusCode.ShouldBe(
            HttpStatusCode.Forbidden,
            "vehicles.create admits admin or inspector; the media type is the caller's second problem");
    }

    /// <summary>
    /// A caller whose scopes exclude the operation is answered 403 by the authorization filter, which runs
    /// before the delegate — so the guard cannot be reached at all.
    /// </summary>
    [Fact]
    public async Task A_caller_out_of_scope_is_answered_403_and_not_415()
    {
        var readOnly = new TestApiKey("read-only-key", ["admin"], ["*:read"]);
        await using var world = await AlvoApiWorld.VehicleRegistryAsync([readOnly]);

        using var response = await world.SendRawAsync(
            HttpMethod.Post, Path(""), readOnly, content: Body("{}", "text/plain"));

        response.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
    }

    /// <summary>
    /// A read route is unaffected. The guard must not spread to a route that parses no body: a
    /// <c>GET</c> carrying a stray <c>Content-Type</c> and no body was a fine request before and stays one.
    /// </summary>
    [Fact]
    public async Task A_read_route_is_not_guarded()
    {
        await using var world = await AlvoApiWorld.VehicleRegistryAsync([Admin()]);

        using var response = await world.SendRawAsync(
            HttpMethod.Get,
            "/api/vehicles",
            Admin(),
            headers: [new KeyValuePair<string, string>("Content-Type", "text/plain")]);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
    }
```

If `ResponseReading` has no `IdOfAsync` / `ProblemAsync` with these exact names, read
`test/_shared/api/ResponseReading.cs` and use the helpers it does expose — do **not** add new ones.

- [ ] **Step 2: Run them to verify they fail**

Run: `dotnet test test/MMLib.Alvo.Api.Tests --filter-class MMLib.Alvo.Api.Tests.DataApiContentTypeTests`
Expected: the eleven new facts FAIL — the refusal facts get `201`/`200`/`422` instead of `415`; the
`Accept-*` fact throws on the missing header. `The_guard_is_on_by_default`,
`Every_body_taking_route_accepts_application_json`, `A_read_route_is_not_guarded`,
`A_host_that_opted_out_accepts_any_media_type` and the two 403 facts pass already — that is expected and
correct, they are the controls.

- [ ] **Step 3: Write the guard**

Create `src/MMLib.Alvo/Api/Internal/JsonContentType.cs`:

```csharp
using Microsoft.AspNetCore.Http;
using Microsoft.Net.Http.Headers;

namespace MMLib.Alvo.Api.Internal;

/// <summary>
/// The JSON <c>Content-Type</c> requirement every body-taking route is guarded by (#191).
/// </summary>
/// <remarks>
/// <para>
/// <b>It is a CSRF guard whose mechanism lives in the browser.</b> WHATWG Fetch safelists
/// <c>application/x-www-form-urlencoded</c>, <c>multipart/form-data</c> and <c>text/plain</c>, and a
/// request carrying only safelisted headers crosses origins with cookies attached and no preflight — as
/// does one declaring no <c>Content-Type</c> at all. Requiring JSON is what forces the preflight; nothing
/// here is a defence on its own, and that is why the refusal's <em>position</em> in the pipeline is free
/// to follow the ordering rule instead of racing it (see <see cref="DataApiEndpoints"/>).
/// </para>
/// <para>
/// <b>Called from the five body-taking delegates rather than from a filter or from the body reader</b>, so
/// it sits after the policy decision and before the body — the same place
/// <c>DataApiEndpoints.EnsureUnconditional</c> sits, and for the same two reasons: precedence (a denied
/// caller hears about the denial) and cost (nothing is read on behalf of a caller who cannot succeed).
/// <c>DataApiContentTypeTests.Every_body_taking_route_refuses_a_non_json_media_type</c> is what makes the
/// five call sites exhaustive.
/// </para>
/// </remarks>
internal static class JsonContentType
{
    /// <summary>The media type the generated document declares for a request body.</summary>
    private const string Json = "application/json";

    /// <summary>RFC 7396's spelling, accepted by the <c>+json</c> suffix rule and advertised on a PATCH.</summary>
    private const string MergePatchJson = "application/merge-patch+json";

    /// <summary>The <c>+json</c> structured suffix (RFC 6839), as it appears at the end of a subtype.</summary>
    private const string JsonSuffix = "+json";

    /// <summary>What a refusal advertises this API accepts, in the order a reader wants them.</summary>
    internal static string Advertised(string method) => IsPatch(method)
        ? $"{Json}, {MergePatchJson}"
        : Json;

    /// <summary>
    /// Refuses the request when it does not declare a JSON body, or <see langword="null"/> when it may
    /// proceed.
    /// </summary>
    /// <param name="request">The request whose declaration to judge.</param>
    /// <param name="options">The API options; <see cref="AlvoApiOptions.RequireJsonContentType"/> can turn this off.</param>
    internal static IResult? Refuse(HttpRequest request, AlvoApiOptions options)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(options);

        if (!options.RequireJsonContentType || IsJson(request.ContentType))
        {
            return null;
        }

        return ProblemResultFactory.UnsupportedMediaType(request.Method);
    }

    /// <summary>
    /// Whether <paramref name="contentType"/> declares JSON: <c>application/json</c> or any
    /// <c>application/*+json</c>, with any parameters.
    /// </summary>
    /// <remarks>
    /// <b>An absent or unparseable declaration is not JSON.</b> Absent is the case the guard exists for —
    /// a request declaring nothing is safelisted by omission — and an unparseable one cannot be read as
    /// anything, so treating it as JSON would accept exactly what a forger would send. Parameters are
    /// ignored rather than validated: the readers are UTF-8 by construction, and a body actually encoded
    /// otherwise earns the existing <c>malformed-json</c> 422, which describes the bytes.
    /// </remarks>
    /// <param name="contentType">The request's declared media type, if any.</param>
    private static bool IsJson(string? contentType)
    {
        if (!MediaTypeHeaderValue.TryParse(contentType, out var parsed))
        {
            return false;
        }

        var mediaType = parsed.MediaType.Value;

        return string.Equals(mediaType, Json, StringComparison.OrdinalIgnoreCase)
            || (mediaType is not null
                && mediaType.StartsWith("application/", StringComparison.OrdinalIgnoreCase)
                && mediaType.EndsWith(JsonSuffix, StringComparison.OrdinalIgnoreCase));
    }

    private static bool IsPatch(string method) =>
        HttpMethods.IsPatch(method);
}
```

- [ ] **Step 4: Write the refusal**

In `src/MMLib.Alvo/Api/Internal/ProblemResultFactory.cs`, add before the private `Problem` overloads:

```csharp
    /// <summary>
    /// The 415 for a body-taking route the caller did not declare as JSON — or did not declare at all.
    /// </summary>
    /// <remarks>
    /// <b>It carries the registered <c>Accept-*</c> header for the method, where one exists.</b>
    /// <c>Accept-Post</c> (W3C LDP 1.0 §7.1.2, IANA-registered) and <c>Accept-Patch</c> (RFC 5789 §3.1)
    /// say exactly what is needed here, so the fix is machine-readable and not only prose — §0 principle
    /// 4 served by prior art. <c>PUT</c> and <c>DELETE</c> get no header, because no registered one
    /// exists and minting <c>Accept-Put</c> to tidy the set would be inventing a variant of a standard.
    /// </remarks>
    /// <param name="method">The request's method, which decides the advertised header.</param>
    internal static IResult UnsupportedMediaType(string method) => new AdvertisingResult(
        Problem(
            StatusCodes.Status415UnsupportedMediaType,
            AlvoProblemTypes.UnsupportedMediaType,
            "This endpoint reads a JSON request body. Send it with "
            + $"'Content-Type: {JsonContentType.Advertised(method)}'."),
        AcceptHeaderFor(method),
        JsonContentType.Advertised(method));

    /// <summary>The registered header advertising acceptable media types for <paramref name="method"/>, if any.</summary>
    /// <remarks>
    /// <c>Accept-Post</c> and <c>Accept-Patch</c> are the only two that exist. Returning
    /// <see langword="null"/> for everything else is what keeps <c>PUT</c> and <c>DELETE</c> honest.
    /// </remarks>
    /// <param name="method">The request's method.</param>
    private static string? AcceptHeaderFor(string method) => method switch
    {
        _ when HttpMethods.IsPost(method) => "Accept-Post",
        _ when HttpMethods.IsPatch(method) => "Accept-Patch",
        _ => null,
    };
```

and the wrapper beside `UnauthenticatedResult`:

```csharp
    /// <summary>
    /// A problem response plus the registered header advertising what the method accepts. The same shape
    /// <see cref="UnauthenticatedResult"/> uses, and for the same reason: one place produces the pairing,
    /// so a second path answering 415 cannot forget the header.
    /// </summary>
    /// <param name="problem">The problem response to write.</param>
    /// <param name="header">The header name to advertise under, or <see langword="null"/> for none.</param>
    /// <param name="value">The media types to advertise.</param>
    private sealed class AdvertisingResult(IResult problem, string? header, string value) : IResult
    {
        public Task ExecuteAsync(HttpContext httpContext)
        {
            ArgumentNullException.ThrowIfNull(httpContext);
            if (header is not null)
            {
                httpContext.Response.Headers.Append(header, value);
            }

            return problem.ExecuteAsync(httpContext);
        }
    }
```

- [ ] **Step 5: Call it from the five delegates**

In `src/MMLib.Alvo/Api/Internal/DataApiEndpoints.cs`, add this line immediately after each delegate's
`EnsureOperationIsAllowed(...)` / `EnsureUnconditional(...)` block, in `MapCreate`, `MapUpdate`,
`MapReplace`, `MapQuery` and `BatchAsync`:

```csharp
                    if (JsonContentType.Refuse(http.Request, options) is { } unsupported)
                    {
                        return unsupported;
                    }
```

Two notes for the implementer: `MapQuery`'s delegate is a read, so it has a decision resolved but no
`EnsureUnconditional` — put the guard directly after the decision. `BatchAsync` is a method rather than
an inline lambda, so the same three lines go after its `EnsureUnconditional(http.Request);`.

Add a paragraph to the class's `<remarks>` recording the guard beside the existing pre-body guards, so a
reader meets it where they meet `EnsureUnconditional`:

```csharp
/// <para>
/// <b>Five of the eight delegates read a request body, and each guards its declaration before reading
/// it.</b> <see cref="JsonContentType.Refuse"/> answers 415 for a body that is not declared as JSON —
/// #191's CSRF guard — and it sits <em>after</em> the operation's decision on purpose: the ordering rule
/// this file already states for <see cref="EnsureOperationIsAllowed"/> is that an unauthorized caller
/// hears about the denial, not about their request's shape. Nothing is lost by that order, because the
/// defence is the browser's preflight and is decided before the request is sent.
/// </para>
```

- [ ] **Step 6: Run the tests to verify they pass**

Run: `dotnet test test/MMLib.Alvo.Api.Tests --filter-class MMLib.Alvo.Api.Tests.DataApiContentTypeTests`
Expected: PASS (15 tests).

- [ ] **Step 7: Run ring0**

Run: `scripts/test-ring0`
Expected: PASS, except `OpenApiDocumentTests.The_document_is_stable` and
`Every_documented_status_code_is_one_the_endpoint_can_actually_return` — which are Task 3's, because the
document does not yet list the 415. If anything **else** fails, a fact somewhere sends a non-JSON body
and expected a 422; read it and decide whether it should now expect 415 (it should) or whether the guard
is wrong.

- [ ] **Step 8: Normalise line endings and commit**

```bash
python3 - <<'EOF'
for p in ["src/MMLib.Alvo/Api/Internal/JsonContentType.cs",
          "test/MMLib.Alvo.Api.Tests/DataApiContentTypeTests.cs"]:
    d = open(p, "rb").read().replace(b"\r\n", b"\n").replace(b"\n", b"\r\n")
    open(p, "wb").write(d if d[:3] == b"\xef\xbb\xbf" else b"\xef\xbb\xbf" + d)
EOF
git add src/MMLib.Alvo/Api/Internal/JsonContentType.cs \
        src/MMLib.Alvo/Api/Internal/ProblemResultFactory.cs \
        src/MMLib.Alvo/Api/Internal/DataApiEndpoints.cs \
        test/MMLib.Alvo.Api.Tests/DataApiContentTypeTests.cs
git commit -m "feat(api): body-taking routes require a JSON Content-Type (#191)

application/json and any application/*+json, parameters ignored; anything
else -- and a request declaring nothing at all -- is 415 with the
unsupported-media-type slug and a detail naming the fix. POST carries
Accept-Post and PATCH carries Accept-Patch; PUT and DELETE carry nothing,
because no registered header exists and inventing one is worse.

The guard sits after the operation's decision, so the ordering rule holds
unchanged: a denied caller still hears 403. That costs the defence nothing --
the mechanism is the browser's preflight, decided before the request is sent.

Claude-Session: https://claude.ai/code/session_01LMBssp3LFUYfqfbWPZ9pk5"
```

---

## Task 3: The generated document lists the 415 exactly where it is reachable

**Files:**
- Modify: `src/MMLib.Alvo/Api/Internal/DataApiDocumentation.cs`
- Modify: `src/MMLib.Alvo/Api/Internal/AlvoDocumentTransformer.cs` (2 call sites)
- Modify: `src/MMLib.Alvo/Api/Internal/DataApiHeaders.cs` (`AddTo`)
- Modify: `src/MMLib.Alvo/Api/Internal/DataApiEndpoints.cs` (`Protect` / `Documenting` take `options`)
- Test: `test/MMLib.Alvo.Api.Tests/OpenApiDocumentTests.cs`
- Accept: `test/MMLib.Alvo.Api.Tests/OpenApiDocumentTests.The_document_is_stable.verified.txt`

**Interfaces:**
- Consumes: `AlvoApiOptions.RequireJsonContentType` (Task 1), `AlvoProblemTypes.UnsupportedMediaType` (Task 1).
- Produces: `DataApiDocumentation.ResponsesFor(DataApiEndpointKind kind, EntitySchema entity, AlvoApiOptions options)`
  and `DataApiDocumentation.SharedRefusals(AlvoApiOptions options)` — both now options-aware.

- [ ] **Step 1: Write the failing tests**

In `test/MMLib.Alvo.Api.Tests/OpenApiDocumentTests.cs`, add two facts. Match the file's existing style for
getting a document (it has helpers — read them first rather than inventing):

```csharp
    /// <summary>
    /// The 415 is published on exactly the seven body-taking operations, and on none of the three that
    /// parse no body — <c>ResponsesFor</c>'s contract is that each entry is a claim a request can reach it.
    /// </summary>
    [Fact]
    public async Task The_415_is_listed_on_every_body_taking_operation_and_no_other()
    {
        await using var world = await AlvoApiWorld.VehicleRegistryAsync(setup: DocumentSetup);
        var document = await world.OpenApiDocumentAsync();

        var listed = OperationsWithStatus(document, "415").ToList();

        listed.ShouldBe(
            [
                "POST /api/inspections", "PATCH /api/inspections/{id}", "PUT /api/inspections/{id}",
                "POST /api/inspections/query", "POST /api/inspections/batch",
                "PATCH /api/inspections/batch", "DELETE /api/inspections/batch",
                "POST /api/owners", "PATCH /api/owners/{id}", "PUT /api/owners/{id}",
                "POST /api/owners/query", "POST /api/owners/batch",
                "PATCH /api/owners/batch", "DELETE /api/owners/batch",
                "POST /api/vehicles", "PATCH /api/vehicles/{id}", "PUT /api/vehicles/{id}",
                "POST /api/vehicles/query", "POST /api/vehicles/batch",
                "PATCH /api/vehicles/batch", "DELETE /api/vehicles/batch",
            ],
            ignoreOrder: true,
            "seven body-taking operations per entity, three entities, and nothing on a read or a delete");
    }

    /// <summary>
    /// A host that opted out publishes no 415 anywhere — neither on an operation nor as a component. A
    /// document listing a status no request can reach describes behaviour that does not exist, which is
    /// the same rule that keeps a 304 off a version-less entity.
    /// </summary>
    [Fact]
    public async Task A_host_that_opted_out_publishes_no_415_at_all()
    {
        await using var world = await AlvoApiWorld.VehicleRegistryAsync(
            setup: DocumentSetup with { ConfigureApi = api => api.RequireJsonContentType = false });

        var text = await world.OpenApiTextAsync();

        text.ShouldNotContain("415");
        text.ShouldNotContain("unsupported-media-type");
    }
```

`DocumentSetup` and `OperationsWithStatus` may not exist under those names — the file already builds
documents and already walks operations for other facts. Reuse what is there; add a small private helper
only if none fits, and keep it beside the facts that use it.

- [ ] **Step 2: Run them to verify they fail**

Run: `dotnet test test/MMLib.Alvo.Api.Tests --filter-class MMLib.Alvo.Api.Tests.OpenApiDocumentTests`
Expected: both new facts FAIL — no 415 is published anywhere yet. `The_document_is_stable` still passes.

- [ ] **Step 3: Add the response and make both catalogues options-aware**

In `src/MMLib.Alvo/Api/Internal/DataApiDocumentation.cs`:

```csharp
    /// <summary>The 415, published only while the guard that answers it is on.</summary>
    /// <remarks>
    /// One sentence covering both ways to earn it — a declaration that is not JSON, and no declaration at
    /// all — because OpenAPI keys a response by status and the fix is the same for both. The registered
    /// <c>Accept-Post</c>/<c>Accept-Patch</c> header the refusal carries is named here so a client knows to
    /// read it rather than to parse the prose.
    /// </remarks>
    private static Response UnsupportedMediaType => new(
        StatusCodes.Status415UnsupportedMediaType,
        ResponseBody.Problem,
        "The request body was not declared as JSON, or carried no 'Content-Type' at all. Send "
        + "'Content-Type: application/json' (or any 'application/*+json'); a POST refusal also carries "
        + "'Accept-Post' and a PATCH refusal 'Accept-Patch' naming what this operation accepts. The "
        + "requirement exists because a body-taking route with no media-type requirement is reachable as "
        + "a CORS simple request, and it can be turned off by a host with its own CSRF defence.",
        SharedId: "unsupported-media-type");
```

Change the two members' signatures. `ResponsesFor` gains a third parameter and appends the response to
the seven body-taking arms:

```csharp
    internal static IReadOnlyList<Response> ResponsesFor(
        DataApiEndpointKind kind, EntitySchema entity, AlvoApiOptions options)
    {
        ArgumentNullException.ThrowIfNull(entity);
        ArgumentNullException.ThrowIfNull(options);
        return kind switch
        {
            DataApiEndpointKind.List =>
                [Ok(ResponseBody.Page, "A page of rows the caller's policy admits."), .. Refusals(Malformed)],
            DataApiEndpointKind.Query =>
                [Ok(ResponseBody.Page, "A page of rows the caller's policy admits."),
                 .. Refusals(Malformed), .. MediaType(options)],
            // … Get and Delete unchanged …
            DataApiEndpointKind.Create =>
                [Created(entity), .. Refusals(Malformed, Precondition, Conflict), .. MediaType(options)],
            // … Update, Replace and the three batch arms likewise gain `.. MediaType(options)` …
        };
    }

    /// <summary>The 415, or nothing at all when the host turned the guard off.</summary>
    /// <remarks>
    /// The same construction <see cref="NotModified"/> uses for a version-less entity's 304, and for the
    /// same reason: an unreachable status in the document is a promise about behaviour that does not
    /// exist. It also keeps <see cref="AlvoDocumentTransformer"/>'s "the refusal components are never
    /// orphans" guarantee true — a component nothing can reference is the defect that argument names.
    /// </remarks>
    /// <param name="options">The API options, which decide whether the guard answers at all.</param>
    private static IEnumerable<Response> MediaType(AlvoApiOptions options) =>
        options.RequireJsonContentType ? [UnsupportedMediaType] : [];

    internal static IReadOnlyList<Response> SharedRefusals(AlvoApiOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        return [Unauthenticated, Forbidden, Absent, Precondition, Conflict, Malformed, .. MediaType(options)];
    }
```

`List` and `Query` are split into two arms because only the query route reads a body — they shared one
arm before, and keeping them shared would publish a 415 on the query-string list.

- [ ] **Step 4: Thread `options` to every caller**

Four call sites, all mechanical:

1. `AlvoDocumentTransformer.Reusable` → `DataApiDocumentation.SharedRefusals(options.Value)`, and
   `DataApiHeaders.AddTo(document, operations, options.Value)`.
2. `AlvoDocumentTransformer` line ~613 → `ResponsesFor(marker.Kind, entity, options.Value)`.
3. `DataApiHeaders.AddTo` gains an `AlvoApiOptions options` parameter and passes it through to
   `ResponsesFor`. Document the parameter.
4. `DataApiEndpoints`: `Protect(entity, kind, filters, conventions)` gains `options`, and
   `Documenting(this RouteHandlerBuilder, EntitySchema, DataApiEndpointKind, AlvoApiOptions)` passes it
   to `ResponsesFor`. Every `.Protect(...)` call site in the file gets `options` — `MapList`, `MapQuery`,
   `MapGet`, `MapCreate`, `MapUpdate`, `MapReplace`, `MapDelete` and `MapBatch`'s local `Map`. Note that
   `MapGet` currently takes no `options` parameter at all, so add one and pass it from `Map`.

- [ ] **Step 5: Run the tests to verify they pass, and accept the snapshot**

Run: `dotnet test test/MMLib.Alvo.Api.Tests --filter-class MMLib.Alvo.Api.Tests.OpenApiDocumentTests`
Expected: the two new facts PASS; `The_document_is_stable` FAILS with a diff.

Read the diff before accepting it. It must contain **only**: one new
`components.responses["unsupported-media-type"]` entry, and one `$ref` to it on each of the 21
body-taking operations. Anything else — a moved parameter, a changed description, a 415 on a read — means
Step 3 or 4 is wrong. Then:

```bash
mv "test/MMLib.Alvo.Api.Tests/OpenApiDocumentTests.The_document_is_stable.received.txt" \
   "test/MMLib.Alvo.Api.Tests/OpenApiDocumentTests.The_document_is_stable.verified.txt"
```

The turn-review-gate will block on the changed `*.verified.*` and ask for `alvo-snapshot-judge`. Dispatch
it; the accompanying source change is this task, and the judge should confirm the new response is
justified by it.

- [ ] **Step 6: Run ring1 and commit**

```bash
scripts/test-ring1
git add src/MMLib.Alvo/Api/Internal/DataApiDocumentation.cs \
        src/MMLib.Alvo/Api/Internal/AlvoDocumentTransformer.cs \
        src/MMLib.Alvo/Api/Internal/DataApiHeaders.cs \
        src/MMLib.Alvo/Api/Internal/DataApiEndpoints.cs \
        test/MMLib.Alvo.Api.Tests/OpenApiDocumentTests.cs \
        "test/MMLib.Alvo.Api.Tests/OpenApiDocumentTests.The_document_is_stable.verified.txt"
git commit -m "feat(api): the document lists the 415 exactly where a request can reach it (#191)

Seven body-taking operations per entity get it; the query-string list, the
single-row read and the delete do not, because they parse no body. List and
Query stop sharing a response arm for that reason.

ResponsesFor and SharedRefusals now take AlvoApiOptions, so a host that opted
out publishes no 415 and no orphan component -- the same construction that
keeps a 304 off a version-less entity, and what keeps the transformer's 'the
refusal components are never orphans' guarantee true.

Claude-Session: https://claude.ai/code/session_01LMBssp3LFUYfqfbWPZ9pk5"
```

---

## Task 4: The invariant across generated descriptors, and the saboteur

**Files:**
- Modify: `test/MMLib.Alvo.Api.Invariants.Tests.Integration/BehaviourInvariants.cs`
- Modify: `test/MMLib.Alvo.Api.Invariants.Tests.Integration/BehaviourInvariantTests.cs`
- Modify: `test/MMLib.Alvo.Api.Invariants.Tests.Integration/Sabotage.cs`
- Modify: `test/MMLib.Alvo.Api.Invariants.Tests.Integration/SabotageTests.cs`

**Interfaces:**
- Consumes: the guard (Task 2), `AlvoApiWorldSetup.ConfigureApi`.
- Produces: `BehaviourInvariants.NonJsonBodiesAreRefusedAsync(AlvoApiWorld world, GeneratedProject project)`,
  `Sabotage.TheMediaTypeGuardIsOff()`.

- [ ] **Step 1: Write the invariant**

In `BehaviourInvariants.cs`, add — the existing `_routes` table already carries a `NeedsBody` flag, so
this reuses it rather than restating the route set:

```csharp
    /// <summary>
    /// Every route that reads a body refuses one that is not declared as JSON, on every entity the
    /// descriptor permits — the cross-descriptor half of #191.
    /// </summary>
    /// <param name="world">The running API.</param>
    /// <param name="project">The generated project.</param>
    /// <remarks>
    /// <b>Over the <em>permissive</em> entities, not the denied ones.</b> A denied entity answers 403
    /// before the guard is reached (the ordering rule), so running this over those would assert 415 and
    /// get 403 for a reason that is not the guard. The permissive entities are where the guard is the
    /// thing being measured.
    /// </remarks>
    internal static async Task NonJsonBodiesAreRefusedAsync(AlvoApiWorld world, GeneratedProject project)
    {
        project.PermissiveEntities.ShouldNotBeEmpty(
            "every generated project must declare an entity that admits its admin, or this walks nothing");

        var reached = 0;
        var bodyTaking = _routes.Where(route => route.NeedsBody).ToList();
        foreach (var entity in project.PermissiveEntities)
        {
            foreach (var (method, suffix, _) in bodyTaking)
            {
                var path = $"/api/{entity}{suffix.Replace("{id}", Guid.NewGuid().ToString(), StringComparison.Ordinal)}";
                using var content = new StringContent("{}", System.Text.Encoding.UTF8, "text/plain");
                using var response = await world.SendRawAsync(method, path, project.Admin(), content: content);

                response.StatusCode.ShouldBe(
                    HttpStatusCode.UnsupportedMediaType,
                    $"{method} {path} reads a body, so a text/plain declaration must be refused");
                reached++;
            }
        }

        reached.ShouldBe(
            project.PermissiveEntities.Count * bodyTaking.Count,
            "or this claim did not reach every body-taking route of every permissive entity");
    }
```

If `_routes`' tuple element is not named `NeedsBody`, or `PermissiveEntities` is spelled differently, read
the file and use what is there.

- [ ] **Step 2: Register it and run to verify it passes**

Add `await BehaviourInvariants.NonJsonBodiesAreRefusedAsync(world, project);` to
`BehaviourInvariantTests.A_generated_project_holds_every_behavioural_invariant`, and update that method's
and the class's doc comments — they say "all four" / "the four bodies" and the count is now higher; count
the calls and say the right number, or reword to not carry a number.

Run: `dotnet test test/MMLib.Alvo.Api.Invariants.Tests.Integration`
Expected: PASS. (This invariant passes immediately because Task 2 already shipped the guard — the failing
half is the saboteur below, which is what proves the invariant is not vacuous.)

- [ ] **Step 3: Write the saboteur**

In `Sabotage.cs`:

```csharp
    /// <summary>A host that turned the media-type guard off.</summary>
    /// <remarks>
    /// <b>The one saboteur that needs no decorator, and it is the more honest for it.</b> The regression
    /// this invariant watches for is not an exotic misbehaviour — it is somebody flipping
    /// <c>RequireJsonContentType</c>'s default, or a host setting it to <see langword="false"/> without
    /// the CSRF defence the option assumes. That is exactly what this configures, so the invariant is
    /// seen to fail for the reason it exists.
    /// </remarks>
    internal static AlvoApiWorldSetup TheMediaTypeGuardIsOff() =>
        new(ConfigureApi: api => api.RequireJsonContentType = false);
```

- [ ] **Step 4: Add the sabotage fact and run it**

In `SabotageTests.cs`, following the file's existing shape for the other two saboteurs, add a fact that
starts a project with `Sabotage.TheMediaTypeGuardIsOff()` and asserts
`BehaviourInvariants.NonJsonBodiesAreRefusedAsync` throws.

Run: `dotnet test test/MMLib.Alvo.Api.Invariants.Tests.Integration`
Expected: PASS — the new sabotage fact goes green by observing the invariant go red.

- [ ] **Step 5: Normalise, run ring2, commit**

```bash
python3 - <<'EOF'
import glob
for p in glob.glob("test/MMLib.Alvo.Api.Invariants.Tests.Integration/*.cs"):
    d = open(p, "rb").read().replace(b"\r\n", b"\n").replace(b"\n", b"\r\n")
    open(p, "wb").write(d if d[:3] == b"\xef\xbb\xbf" else b"\xef\xbb\xbf" + d)
EOF
scripts/test-ring2
git add test/MMLib.Alvo.Api.Invariants.Tests.Integration
git commit -m "test(api): the media-type guard holds across the generated descriptors (#191)

The invariant suite is where a claim about N descriptors belongs, and this is
one: every body-taking route of every permissive entity refuses a text/plain
declaration. Over the permissive entities and not the denied ones, because a
denied entity answers 403 first by the ordering rule.

Its saboteur is the only one that needs no decorator -- it just turns the
option off, which is the regression somebody would actually ship.

Claude-Session: https://claude.ai/code/session_01LMBssp3LFUYfqfbWPZ9pk5"
```

---

## Task 5: The sample host

**Files:**
- Create: `samples/Directory.Build.props`
- Create: `samples/MMLib.Alvo.Samples.EmbeddedHost/MMLib.Alvo.Samples.EmbeddedHost.csproj`
- Create: `samples/MMLib.Alvo.Samples.EmbeddedHost/Program.cs`
- Create: `samples/MMLib.Alvo.Samples.EmbeddedHost/appsettings.json`
- Create: `samples/MMLib.Alvo.Samples.EmbeddedHost/README.md`
- Modify: `MMLib.Alvo.slnx`

**Interfaces:**
- Consumes: `IServiceCollection.AddAlvo`, `IAlvoBuilder.UseSqlite`, `.FromDescriptor`, `.AddDataApi`,
  `IEndpointRouteBuilder.MapAlvoHealth`, `.MapAlvoDataApi`, `IAlvoData`, `IRoleCatalogProvider`,
  `AlvoContext`, `UserId`, `RoleCatalog.Resolve`, `AlvoQuery`.
- Produces: a `Program` class reachable from a test project (via `InternalsVisibleTo`), serving
  `POST /app/login`, `GET /app/vehicles`, `POST /app/vehicles`, `GET /health/live`, `GET /health/ready`,
  and Alvo's generated routes under `/api/alvo`.

- [ ] **Step 1: Create the samples props and the csproj**

`samples/Directory.Build.props`:

```xml
<Project>

  <Import Project="$([MSBuild]::GetPathOfFileAbove('Directory.Build.props', '$(MSBuildThisFileDirectory)../'))"
          Condition="'' != $([MSBuild]::GetPathOfFileAbove('Directory.Build.props', '$(MSBuildThisFileDirectory)../'))" />

  <PropertyGroup>
    <!-- A sample is read and run, never referenced: a .nupkg of one would publish a surface nobody
         consumes, exactly as for MMLib.Alvo.Host. It also keeps SolutionConventionTests'
         "every packable src project has a tests project" rule out of the way of a directory it was
         never written for. -->
    <IsPackable>false</IsPackable>
  </PropertyGroup>

</Project>
```

`samples/MMLib.Alvo.Samples.EmbeddedHost/MMLib.Alvo.Samples.EmbeddedHost.csproj`:

```xml
<Project Sdk="Microsoft.NET.Sdk.Web">

  <!--
    The embedded-run sample (#24, spec §"Režim 2"): somebody else's ASP.NET Core app that keeps its
    records in Alvo. Project references rather than PackageReferences, so a breaking change to the
    AddAlvo/MapAlvo seam breaks the sample in the same build instead of rotting until someone runs it.
  -->
  <PropertyGroup>
    <UserSecretsId>mmlib-alvo-samples-embedded-host</UserSecretsId>
    <RootNamespace>MMLib.Alvo.Samples.EmbeddedHost</RootNamespace>
  </PropertyGroup>

  <ItemGroup>
    <ProjectReference Include="../../src/MMLib.Alvo/MMLib.Alvo.csproj" />
    <!-- SQLite so `dotnet run` needs no container. The engine is not part of what this sample
         demonstrates: §0 principle 3 makes it irrelevant, and DataApiEngineTests proves it on both. -->
    <ProjectReference Include="../../src/MMLib.Alvo.Data.Sqlite/MMLib.Alvo.Data.Sqlite.csproj" />
  </ItemGroup>

  <ItemGroup>
    <!-- The suite drives this host through WebApplicationFactory, which needs the generated Program. -->
    <InternalsVisibleTo Include="MMLib.Alvo.Samples.EmbeddedHost.Tests.Integration" />
  </ItemGroup>

</Project>
```

- [ ] **Step 2: Write `Program.cs`**

The whole sample. Keep every comment — they are what the sample is for.

```csharp
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Http.HttpResults;
using MMLib.Alvo;
using MMLib.Alvo.Api;
using MMLib.Alvo.Data;
using System.Security.Claims;

// ─────────────────────────────────────────────────────────────────────────────
// Alvo, embedded: somebody else's app that keeps its records in Alvo.
//
// Two kinds of caller reach the same backend two different ways, and that is the
// thing worth understanding before anything else here:
//
//   /app/*        this app's own cookie users. These endpoints resolve IAlvoData
//                 and build an AlvoContext from the cookie's claims, so Alvo's
//                 policy engine decides what they may do -- this app writes no
//                 authorization logic of its own.
//   /api/alvo/*   Alvo's generated Data API, for agents and machines, with
//                 Alvo's own API-key credential.
//
// Both serve examples/vehicle-registry/vehicles.alvo.json -- the same descriptor
// the repository's root docker-compose.yml mounts into the standalone image.
// That is spec §"Spoločné kontrakty" point 2: one descriptor, several doors,
// identical result.
// ─────────────────────────────────────────────────────────────────────────────

var builder = WebApplication.CreateBuilder(args);

// This app's own authentication. Nothing about it is Alvo's: an embedded host owns
// its pipeline, and Alvo never adds authentication, authorization or routing
// middleware on a host's behalf.
builder.Services
    .AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme)
    .AddCookie(options => options.Cookie.Name = "fleet-desk");
builder.Services.AddAuthorization();

// The one entry point (extensibility.md rule 1). UseSqlite selects infrastructure,
// FromDescriptor supplies domain input, AddDataApi configures the generated HTTP
// surface -- three verbs from the fixed taxonomy, and nothing else to learn.
//
// There is deliberately no ApplyAlvoDescriptorAsync call: AddAlvo registers a
// hosted lifecycle service that brings the schema up before anything else starts,
// so /health/ready gating on it is the whole story. A host that applied the
// descriptor itself would duplicate that boot.
builder.Services.AddAlvo(alvo => alvo
    .UseSqlite($"Data Source={Path.Combine(builder.Environment.ContentRootPath, "fleet-desk.db")}")
    .FromDescriptor(DescriptorPath(builder.Environment.ContentRootPath))
    .AddDataApi(api =>
    {
        // Alvo mounts BESIDE this app's own routes, never over them.
        api.RoutePrefix = "/api/alvo";

        // RequireJsonContentType is left at its default (true), and this is the
        // context that default exists for: a body-taking route with no media-type
        // requirement is reachable as a CORS simple request, which stops being
        // harmless the moment a host authenticates with cookies. See
        // docs/architecture/data-api.md, "Requiring a JSON Content-Type".
    }));

// AddAlvoProblemDetails() is deliberately NOT called. An embedded host owns its own
// error rendering, and Alvo taking over the shape of UseExceptionHandler's document
// inside somebody else's application is worse than one explicit call (#119).

var app = builder.Build();

app.UseAuthentication();
app.UseAuthorization();

// ── This app's own surface ───────────────────────────────────────────────────

// A development sign-in. A real app has its own; what matters here is only that the
// cookie ends up carrying a stable user id and the role names this app grants.
app.MapPost("/app/login", (LoginRequest request, HttpContext http) =>
{
    var identity = new ClaimsIdentity(
        [
            new Claim(ClaimTypes.NameIdentifier, request.User.ToString()),
            .. request.Roles.Select(role => new Claim(ClaimTypes.Role, role)),
        ],
        CookieAuthenticationDefaults.AuthenticationScheme);

    return http.SignInAsync(
        CookieAuthenticationDefaults.AuthenticationScheme, new ClaimsPrincipal(identity));
});

// The host's own read. It calls the port with a caller it built itself -- no HTTP
// round trip through /api/alvo, and no second authorization model: `vehicles.list`
// in the descriptor is what decides.
app.MapGet("/app/vehicles", async (
    HttpContext http, IAlvoData data, IRoleCatalogProvider roles, CancellationToken ct) =>
{
    var caller = CallerOf(http.User, roles);
    if (caller is null)
    {
        return Results.Unauthorized();
    }

    var page = await data.QueryAsync(new AlvoQuery { Entity = "vehicles", Limit = 50 }, caller, ct);

    return Results.Ok(page.Items.Select(row => row.Values));
}).RequireAuthorization();

// The host's own write. `vehicles.create` admits admin or inspector, so a plain
// authenticated user is refused here by the DESCRIPTOR and not by this file --
// which is the single most useful thing this sample demonstrates.
app.MapPost("/app/vehicles", async (
    HttpContext http,
    Dictionary<string, object?> values,
    IAlvoData data,
    IRoleCatalogProvider roles,
    CancellationToken ct) =>
{
    var caller = CallerOf(http.User, roles);
    if (caller is null)
    {
        return Results.Unauthorized();
    }

    try
    {
        var record = await data.CreateAsync("vehicles", values, caller, cancellationToken: ct);
        return Results.Created($"/app/vehicles/{record.Id}", record.Values);
    }
    catch (AlvoAuthorizationException exception)
    {
        // This app renders its own refusals -- see the AddAlvoProblemDetails note above.
        return Results.Problem(detail: exception.Message, statusCode: StatusCodes.Status403Forbidden);
    }
}).RequireAuthorization();

// ── Alvo's own surface ──────────────────────────────────────────────────────

// Health FIRST, then the Data API. The order is part of the seam, not a preference:
// MapAlvoDataApi refuses a host whose Data API services are absent, and an operator
// facing that refusal needs a container that can still be probed (extensibility.md
// rule 10).
app.MapAlvoHealth();

// MapAlvoDataApi returns an IEndpointConventionBuilder over Alvo's generated routes
// and nothing else (#182) -- so a host attaches its own concerns to them without the
// framework owning any of them. Health is deliberately not chainable, because one
// builder over the probes and the data would let an authorization policy reach
// /health/live, and a container probe presents no credential.
app.MapAlvoDataApi().WithTags("alvo-data-api");

app.Run();

// ── The identity seam ───────────────────────────────────────────────────────

/// <summary>
/// This app's cookie user, as an Alvo caller.
/// </summary>
/// <remarks>
/// <para>
/// <b>Application roles can only be minted through the catalog the applied descriptor primed</b>, which is
/// why this goes through <see cref="IRoleCatalogProvider"/> instead of naming roles directly: a typo is
/// rejected where it arrives rather than silently matching no rule. <c>DeclaredRoles</c> is
/// <see langword="null"/> until a descriptor is applied, and the contract is to fail closed on that --
/// hence the <see langword="null"/> return rather than an anonymous caller, which would look like a
/// permission problem instead of a boot one.
/// </para>
/// <para>
/// <b>A role this app grants that the descriptor does not declare is dropped, not fatal.</b> An app's own
/// role vocabulary is bigger than the backend's; only the overlap means anything to Alvo's rules.
/// </para>
/// </remarks>
static AlvoContext? CallerOf(ClaimsPrincipal user, IRoleCatalogProvider roles)
{
    if (roles.DeclaredRoles is not { } catalog
        || user.FindFirst(ClaimTypes.NameIdentifier)?.Value is not { } id
        || !Guid.TryParse(id, out var userId))
    {
        return null;
    }

    var declared = user.FindAll(ClaimTypes.Role)
        .Select(claim => claim.Value)
        .Where(name => catalog.TryGet(name, out _))
        .ToList();

    return new AlvoContext
    {
        User = new UserId(userId),
        // Every signed-in caller holds `authenticated`; the descriptor's read rules key on it.
        Roles = catalog.Resolve(["authenticated", .. declared]),
    };
}

/// <summary>The repository's own vehicle-registry descriptor — the same file the standalone image mounts.</summary>
/// <param name="contentRoot">This host's content root, which the search walks up from.</param>
static string DescriptorPath(string contentRoot)
{
    var directory = new DirectoryInfo(contentRoot);
    while (directory is not null)
    {
        var candidate = Path.Combine(
            directory.FullName, "examples", "vehicle-registry", "vehicles.alvo.json");
        if (File.Exists(candidate))
        {
            return candidate;
        }

        directory = directory.Parent;
    }

    throw new InvalidOperationException(
        "examples/vehicle-registry/vehicles.alvo.json was not found above the content root. This sample "
        + "runs from inside the MMLib.Alvo repository; run it with `dotnet run --project "
        + "samples/MMLib.Alvo.Samples.EmbeddedHost`.");
}

/// <summary>A development sign-in request.</summary>
/// <param name="User">The user id the cookie will carry.</param>
/// <param name="Roles">The role names this app grants the user.</param>
internal sealed record LoginRequest(Guid User, IReadOnlyList<string> Roles);
```

If `AlvoPage`'s item property is not `Items`, or `AlvoRecord`'s value bag is not `Values`/`Id`, read
`src/MMLib.Alvo.Abstractions/Data/AlvoPage.cs` and `AlvoRecord.cs` and use the real names. The same for
`RoleCatalog.TryGet`'s signature.

- [ ] **Step 3: Write `appsettings.json`**

No secret, and no `Alvo:Auth:DevKeys` default at all — the sample refuses to start without one, which is
spec §"Spoločné kontrakty" point 5 applied to a sample:

```json
{
  "Logging": {
    "LogLevel": {
      "Default": "Information",
      "Microsoft.AspNetCore": "Warning"
    }
  },
  "Alvo": {
    "Auth": {
      "DevKeys": [
        {
          "KeyId": "agent",
          "User": "9f1d3c7e-5b2a-4f18-8c6d-2e7a9b4c1d05",
          "Roles": ["admin"],
          "Scopes": ["*:read", "*:write"]
        }
      ]
    }
  }
}
```

Then wire the auth section in `Program.cs`, immediately before `AddAlvo`:

```csharp
// Alvo's own credential for the /api/alvo surface. The secret is deliberately absent from
// appsettings.json: AlvoAuthOptionsValidator refuses to start without one, so the sample cannot
// ship a working default credential (spec §"Spoločné kontrakty" point 5). Supply it with
//   dotnet user-secrets set "Alvo:Auth:DevKeys:0:Secret" "<at least 32 characters>"
builder.Services.Configure<AlvoAuthOptions>(builder.Configuration.GetSection("Alvo:Auth"));
```

with `using MMLib.Alvo.Auth;` added.

- [ ] **Step 4: Register the project and build**

```bash
dotnet sln MMLib.Alvo.slnx add samples/MMLib.Alvo.Samples.EmbeddedHost/MMLib.Alvo.Samples.EmbeddedHost.csproj --solution-folder samples
dotnet build MMLib.Alvo.slnx
```

Expected: builds clean. `TreatWarningsAsErrors` is on, so any analyzer complaint is a build failure — fix
it rather than suppressing it. If `dotnet sln add` does not place the project under a `samples` solution
folder, edit `MMLib.Alvo.slnx` by hand to match the `/src/` and `/test/` folders' shape.

- [ ] **Step 5: Run it, by hand, once**

```bash
dotnet user-secrets --project samples/MMLib.Alvo.Samples.EmbeddedHost \
  set "Alvo:Auth:DevKeys:0:Secret" "sample-secret-long-enough-for-the-floor"
dotnet run --project samples/MMLib.Alvo.Samples.EmbeddedHost &
sleep 8
curl -sf localhost:5000/health/ready && echo " ready"
curl -si -XPOST localhost:5000/api/alvo/vehicles -H 'Content-Type: text/plain' \
     -H 'X-Alvo-Api-Key: agent.sample-secret-long-enough-for-the-floor' -d '{}' | head -1
```

Expected: `ready`, then `HTTP/1.1 415 Unsupported Media Type`. Kill the host afterwards. The port may not
be 5000 — read it off the host's own startup log rather than assuming.

- [ ] **Step 6: Write the sample's README**

`samples/MMLib.Alvo.Samples.EmbeddedHost/README.md` must cover, in this order: how to run it (the two
commands above); the two surfaces and which caller uses which; a table of the seams the sample
demonstrates and what each one is (spec §3.4); the `curl` lines for both surfaces; **the limit** — that a
host cannot publish its own principal to the generated Data API because `AlvoContextFilter` publishes the
principal it resolved from the credential header and clears it again, so cookie users go through `/app/*`
(spec §3.5, with a link to the follow-up issue Task 7 files); and finally the extension points spec
§"Režim 2" sketches which **do not exist yet** (`AddModule<T>`, `AddAuthorizationHandler<T>`, `UseAdmin`,
`Hooks(...)`, `.Embedded(e => e.SchemaPrefix(…))`, `MapAlvo("/path")`), so a reader arriving from the spec
is not left wondering.

- [ ] **Step 7: Normalise, run ring1, commit**

```bash
python3 - <<'EOF'
import glob
for p in glob.glob("samples/MMLib.Alvo.Samples.EmbeddedHost/*.cs"):
    d = open(p, "rb").read().replace(b"\r\n", b"\n").replace(b"\n", b"\r\n")
    open(p, "wb").write(d if d[:3] == b"\xef\xbb\xbf" else b"\xef\xbb\xbf" + d)
EOF
scripts/test-ring1
git add samples MMLib.Alvo.slnx
git commit -m "feat(samples): an embedded host that mounts Alvo into its own app (#24)

The half of #24 that was missing: nothing in the repository demonstrated
AddAlvo/MapAlvo, though extensibility.md documents the seam.

Two surfaces, one backend, over the same descriptor the root compose mounts
into the standalone image: /app/* are the host's own cookie-authenticated
endpoints, which build an AlvoContext through IRoleCatalogProvider and call
IAlvoData directly, so the descriptor's rules decide and the sample writes no
authorization of its own; /api/alvo/* is Alvo's generated Data API for
API-key callers.

No credential ships with it -- the dev key's secret comes from user-secrets
and the host refuses to start without one.

Claude-Session: https://claude.ai/code/session_01LMBssp3LFUYfqfbWPZ9pk5"
```

---

## Task 6: The sample's proof

**Files:**
- Create: `test/MMLib.Alvo.Samples.EmbeddedHost.Tests.Integration/MMLib.Alvo.Samples.EmbeddedHost.Tests.Integration.csproj`
- Create: `test/MMLib.Alvo.Samples.EmbeddedHost.Tests.Integration/EmbeddedSampleTests.cs`
- Modify: `MMLib.Alvo.slnx`

**Interfaces:**
- Consumes: the sample's `Program` (Task 5), `Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactory<T>`.
- Produces: nothing other tasks read.

- [ ] **Step 1: Create the csproj**

```xml
<Project Sdk="Microsoft.NET.Sdk">

  <!--
    The sample's proof, and the mechanised form of #24's Definition of Done. ".Tests.Integration" is what
    puts it in ring2 rather than ring0: it boots a real web host per fact, which is not "after every small
    step" work. scripts/test-ring0 excludes it by that naming alone, so no ring script changes.
    AlvoSharedArchTests=false because it maps to no production assembly of its own.
  -->
  <PropertyGroup>
    <AlvoSharedArchTests>false</AlvoSharedArchTests>
  </PropertyGroup>

  <ItemGroup>
    <ProjectReference Include="../../samples/MMLib.Alvo.Samples.EmbeddedHost/MMLib.Alvo.Samples.EmbeddedHost.csproj" />
  </ItemGroup>

  <ItemGroup>
    <PackageReference Include="Microsoft.AspNetCore.Mvc.Testing" />
  </ItemGroup>

</Project>
```

If `Microsoft.AspNetCore.Mvc.Testing` is not yet in `Directory.Packages.props`, add a
`<PackageVersion Include="Microsoft.AspNetCore.Mvc.Testing" Version="…" />` entry there pinned to the same
`10.0.*` band the other ASP.NET Core packages use — never a version in the csproj.

- [ ] **Step 2: Write the failing tests**

`EmbeddedSampleTests.cs`:

```csharp
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;

namespace MMLib.Alvo.Samples.EmbeddedHost.Tests.Integration;

/// <summary>
/// The embedded sample, proved rather than described — #24's Definition of Done is "both modes start up
/// the same functional backend from the same descriptor", and prose cannot hold that.
/// </summary>
public class EmbeddedSampleTests : IAsyncLifetime
{
    private const string Secret = "sample-secret-long-enough-for-the-floor";

    private SampleFactory _factory = null!;

    public ValueTask InitializeAsync()
    {
        _factory = new SampleFactory();
        return ValueTask.CompletedTask;
    }

    public async ValueTask DisposeAsync()
    {
        await _factory.DisposeAsync();
        GC.SuppressFinalize(this);
    }

    /// <summary>
    /// It starts, and the descriptor applied. A sample that does not boot is worse than no sample, and
    /// readiness is the only thing that distinguishes "the process is up" from "the schema is up".
    /// </summary>
    [Fact]
    public async Task The_sample_boots_and_reports_ready()
    {
        using var client = _factory.CreateClient();

        using var response = await client.GetAsync("/health/ready", TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    /// <summary>
    /// The Definition of Done, mechanised: the descriptor's three entities each get the ten routes the
    /// Data API generates, under this host's own prefix and beside this host's own endpoints.
    /// </summary>
    /// <remarks>
    /// <b>Compared against a literal list rather than against a second booted host.</b> Reading the
    /// expected set out of the same generator that built it would make the claim circular — the discipline
    /// <c>BehaviourInvariants</c> states for its own route table — and publishing the standalone host's
    /// OpenAPI document to diff against would mean booting a second process for a claim this settles
    /// directly. What the literal list holds is the contract: same descriptor, same routes.
    /// </remarks>
    [Fact]
    public void The_generated_routes_are_the_ones_the_descriptor_asks_for()
    {
        using var client = _factory.CreateClient();
        var patterns = _factory.Services.GetRequiredService<EndpointDataSource>().Endpoints
            .OfType<RouteEndpoint>()
            .Select(endpoint => endpoint.RoutePattern.RawText!)
            .Where(pattern => pattern.StartsWith("/api/alvo/", StringComparison.Ordinal))
            .ToList();

        string[] expected =
        [
            .. new[] { "owners", "vehicles", "inspections" }.SelectMany(entity => new[]
            {
                $"/api/alvo/{entity}",
                $"/api/alvo/{entity}/query",
                $"/api/alvo/{entity}/batch",
                $"/api/alvo/{entity}/{{id:guid}}",
            }),
        ];

        patterns.Distinct().ShouldBe(expected, ignoreOrder: true);
    }

    /// <summary>
    /// The host's own surface, and the descriptor deciding on it: an inspector may create a vehicle and a
    /// plain authenticated user may not — a refusal this sample writes no code for.
    /// </summary>
    [Fact]
    public async Task The_hosts_own_endpoints_are_gated_by_the_descriptors_rules()
    {
        var ct = TestContext.Current.CancellationToken;

        using var inspector = await SignedInAsync(["inspector"]);
        using var created = await inspector.PostAsJsonAsync(
            "/app/vehicles", new Dictionary<string, object?> { ["vin"] = "1HGCM82633A004352" }, ct);
        created.StatusCode.ShouldBe(HttpStatusCode.Created);

        using var plain = await SignedInAsync([]);
        using var refused = await plain.PostAsJsonAsync(
            "/app/vehicles", new Dictionary<string, object?> { ["vin"] = "1HGCM82633A004353" }, ct);
        refused.StatusCode.ShouldBe(
            HttpStatusCode.Forbidden,
            "vehicles.create admits admin or inspector, and that rule lives in the descriptor");

        using var reading = await plain.GetAsync("/app/vehicles", ct);
        reading.StatusCode.ShouldBe(
            HttpStatusCode.OK, "vehicles.list admits any authenticated caller");
    }

    /// <summary>An unauthenticated caller reaches neither surface.</summary>
    [Fact]
    public async Task An_anonymous_caller_reaches_neither_surface()
    {
        var ct = TestContext.Current.CancellationToken;
        using var client = _factory.CreateClient();

        using var app = await client.GetAsync("/app/vehicles", ct);
        using var api = await client.GetAsync("/api/alvo/vehicles", ct);

        app.StatusCode.ShouldBeOneOf(HttpStatusCode.Unauthorized, HttpStatusCode.Redirect);
        api.StatusCode.ShouldBe(
            HttpStatusCode.Forbidden, "default-deny: no credential means no role a rule admits");
    }

    /// <summary>Alvo's own surface serves an API-key caller.</summary>
    [Fact]
    public async Task The_generated_data_api_serves_an_api_key_caller()
    {
        using var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Alvo-Api-Key", $"agent.{Secret}");

        using var response = await client.GetAsync(
            "/api/alvo/vehicles", TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    /// <summary>
    /// The guard is on here, which is the point of asserting it in this suite as well as in ring0: this
    /// sample is the context #191's threat model is about, so the end-to-end statement belongs here.
    /// </summary>
    [Fact]
    public async Task A_non_json_write_is_refused_in_the_sample()
    {
        using var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Alvo-Api-Key", $"agent.{Secret}");
        using var content = new StringContent("{}", Encoding.UTF8, "text/plain");

        using var response = await client.PostAsync(
            "/api/alvo/vehicles", content, TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.UnsupportedMediaType);
        response.Headers.GetValues("Accept-Post").ShouldHaveSingleItem().ShouldContain("application/json");
    }

    /// <summary>A client holding this app's sign-in cookie for <paramref name="roles"/>.</summary>
    /// <param name="roles">The role names the sign-in grants.</param>
    private async Task<HttpClient> SignedInAsync(IReadOnlyList<string> roles)
    {
        var client = _factory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = true });
        using var response = await client.PostAsJsonAsync(
            "/app/login",
            new { User = Guid.NewGuid(), Roles = roles },
            TestContext.Current.CancellationToken);
        response.EnsureSuccessStatusCode();

        return client;
    }

    /// <summary>
    /// The sample host, with the one secret it refuses to start without and a database of its own per run.
    /// </summary>
    /// <remarks>
    /// The secret is supplied through configuration rather than committed to the sample, which is the
    /// posture being tested as much as it is a fixture detail — <c>AlvoAuthOptionsValidator</c> fails the
    /// host without it.
    /// </remarks>
    private sealed class SampleFactory : WebApplicationFactory<Program>
    {
        private readonly string _root = Directory.CreateTempSubdirectory("alvo-embedded-sample-").FullName;

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            ArgumentNullException.ThrowIfNull(builder);
            builder.UseSetting("Alvo:Auth:DevKeys:0:Secret", Secret);
            builder.UseSetting("contentRoot", _root);
        }
    }
}
```

**Two things the implementer has to resolve against the code rather than guess.** First,
`WebApplicationFactory` resolves the content root from the entry assembly, and overriding it to a temp
directory will break the sample's `DescriptorPath` walk — so either keep the real content root and give
the database a unique file name through configuration, or add a configuration key the sample reads for
its database path. Prefer the latter: add `Alvo:Sample:DatabasePath` to `Program.cs` with the current
value as its default, and set it from the factory. Second, `IAsyncLifetime` is xunit.v3's; check how the
other suites in this repository spell it and follow them.

- [ ] **Step 3: Register the project and run**

```bash
dotnet sln MMLib.Alvo.slnx add test/MMLib.Alvo.Samples.EmbeddedHost.Tests.Integration/MMLib.Alvo.Samples.EmbeddedHost.Tests.Integration.csproj --solution-folder test
dotnet test test/MMLib.Alvo.Samples.EmbeddedHost.Tests.Integration
```

Expected: all six facts PASS. Fix the sample, not the test, when one fails — these are the DoD.

- [ ] **Step 4: Normalise, run ring2, commit**

```bash
python3 - <<'EOF'
import glob
for p in glob.glob("test/MMLib.Alvo.Samples.EmbeddedHost.Tests.Integration/*.cs"):
    d = open(p, "rb").read().replace(b"\r\n", b"\n").replace(b"\n", b"\r\n")
    open(p, "wb").write(d if d[:3] == b"\xef\xbb\xbf" else b"\xef\xbb\xbf" + d)
EOF
scripts/test-ring2
git add test/MMLib.Alvo.Samples.EmbeddedHost.Tests.Integration MMLib.Alvo.slnx samples
git commit -m "test(samples): the embedded sample's Definition of Done, as a test (#24)

'Both modes start up the same functional backend from the same descriptor' is
prose until something checks it. Six facts: it boots and reports ready (so the
descriptor applied); the generated routes are the ten-per-entity set for the
descriptor's three entities, compared against a literal list so the claim is
not circular; the host's own endpoints are gated by the descriptor's rules and
not by the sample's code; an anonymous caller reaches neither surface; an
API-key caller reaches the generated one; and the Content-Type guard answers
415 here, which is the context #191 is actually about.

Claude-Session: https://claude.ai/code/session_01LMBssp3LFUYfqfbWPZ9pk5"
```

---

## Task 7: The records of what changed

**Files:**
- Modify: `docs/architecture/data-api.md` (the `POST …/query` section, plus a new section)
- Modify: `docs/architecture/extensibility.md` (a pointer to the sample)
- Modify: `docs/architecture/host.md` (*What is left of #24*)
- Modify: `docs/PLAN.md` (§3a, and `← YOU ARE HERE`)
- Modify: `README.md`, `examples/README.md`
- File: one new GitHub issue

**Interfaces:** none — documentation only.

- [ ] **Step 1: Correct and extend `data-api.md`**

Two edits. First, the paragraph beginning **"No endpoint requires a `Content-Type`…"** (around line 270)
is now false in two ways: the requirement exists, and the threat it describes is reachable through a
different door than the one it names. Replace it with a paragraph that says the guard is on, points at the
new section, and states the correction plainly: the vector is **not** reachable through
`IAlvoContextAccessor`, because `AlvoContextFilter` publishes the principal it resolved and clears it
again — it is reachable through `AlvoAuthOptions.HeaderName` plus a custom `IAlvoContextResolver`, which
needs no framework change.

Second, add a section **"Requiring a JSON `Content-Type`"** carrying spec §4.1–§4.6: the Fetch
safelist and why a preflight is the mechanism; the seven guarded routes; the accepted media types and the
`+json` suffix; the refused missing declaration; the 415 slug and the `Accept-Post`/`Accept-Patch`
headers with their sources; the ordering (401, then 403, then 415) and why it costs the defence nothing;
`RequireJsonContentType` as the opt-out and its effect on the document; and §4.6's two recorded behaviour
changes — including that a batch `DELETE` whose body *and* `Content-Type` an intermediary stripped now
earns 415 rather than the empty-batch 422.

- [ ] **Step 2: Point `extensibility.md` at the sample**

A short paragraph after "The seam", naming `samples/MMLib.Alvo.Samples.EmbeddedHost` as the runnable
example of rules 1, 4 and 10, and naming the one thing it cannot do (the principal a host cannot publish),
with the new issue's number.

- [ ] **Step 3: Update `host.md`**

In *What is left of #24*, the embedded half is done: say so, and replace it with the seam gap. The other
bullets (the published image, the dashboard, the Management API, the CLI, the `ALVO_*` vocabulary, the
version contract) are untouched.

- [ ] **Step 4: Update `docs/PLAN.md`**

§3a's *"What remains — one thing"* becomes *nothing*: #24's embedded sample and #191 both land here, and
#25 was already done and only needed closing. Add the sample to the "Done, and verifiable by running it"
table. Move `← YOU ARE HERE` to **F5** on the phase map and in §3, and update the F4 checkbox to `[x]`.
Adjust the counts line (`F4 = 5` → the truth after this PR).

- [ ] **Step 5: Update the two READMEs**

Root `README.md`: a line naming the sample as how to see embedded mode. `examples/README.md`: a note that
`vehicle-registry` is also the embedded sample's descriptor, which is what makes the two modes comparable.

- [ ] **Step 6: File the follow-up issue**

```bash
gh issue create --milestone "F7 — Further components" \
  --title "An embedded host cannot publish its own principal to the generated Data API" \
  --body "<spec §8.1, verbatim, plus the AlvoContextFilter.cs:133 reference and the workaround #191 closes>"
```

Then put its number into the sample's `README.md` and `extensibility.md` from Steps 2 and 6 of Task 5.

- [ ] **Step 7: Check brief freshness, run ring2, commit**

```bash
scripts/check-brief-freshness
scripts/test-ring2
git add docs README.md examples/README.md samples
git commit -m "docs: record the guard, the sample, and one correction (#24, #191)

data-api.md's Content-Type paragraph was wrong in two ways -- the requirement
now exists, and the threat it described is not reachable through
IAlvoContextAccessor, which AlvoContextFilter overwrites on every request. The
reachable door is AlvoAuthOptions.HeaderName plus a custom
IAlvoContextResolver, which needs no framework change; that is what the guard
closes.

PLAN.md §3a's 'what remains' is now empty and YOU ARE HERE moves to F5.

Claude-Session: https://claude.ai/code/session_01LMBssp3LFUYfqfbWPZ9pk5"
```

---

## Task 8: The pre-PR gate

**Files:** none — this task produces reviews and a PR.

- [ ] **Step 1: Freeze the tree**

Stop editing. A reviewer reading a tree that is still moving gives a verdict that does not cover the final
diff. `git status` must be clean.

- [ ] **Step 2: Run ring2 one more time, from a clean tree**

Run: `scripts/test-ring2`
Expected: green. Record the output — the PR report cites it.

- [ ] **Step 3: Correctness and security review**

The diff touches the security core (the Data API's refusal path and its ordering), so both passes are
owed. Dispatch a correctness reviewer and a security reviewer over the frozen diff, and follow the
`alvo-security-core-review` checklist for the second. Fix findings **before** the PR — CodeRabbit and
CodeQL are the outer loop, not a substitute.

- [ ] **Step 4: Dispatch `alvo-plan-guard`**

The last check before the PR. It is read-only and advisory: it reports drift from `docs/PLAN.md`, violated
§0 principles and shortcuts in the security core. Act on what it raises.

- [ ] **Step 5: Build the PR report**

Use the `alvo-pr-report` skill. It dispatches `alvo-pr-reporter` and publishes the fixed 8-section page as
an Artifact; the PR body becomes a five-line pointer to it.

- [ ] **Step 6: Open the PR**

```bash
git push -u origin feat/24-191-embedded-sample-content-type
gh pr create --title "feat: the embedded-run sample + the JSON Content-Type guard (#24, #191)" --body "<the five-line pointer>"
```

The body must close both issues with the keyword **repeated** — `Closes #24, #191` auto-closes only the
first:

```
Closes #24
Closes #191
```

`#25` is already satisfied by `test/teapie-field-service` and needs closing by hand with a comment saying
so; do not let it ride on this PR's keywords.

---

## Self-Review

**Spec coverage.** §2.1/§2.2 (the correction) → Task 7 Step 1. §2.3 (the supported path) → Task 5
Step 2. §2.4 (no explicit apply) → Task 5 Step 2's comment and Task 6's readiness fact. §2.5 (the
convention constraints) → Task 5 Steps 1 and 4. §3.1–§3.3 → Task 5. §3.4 (the seam inventory) → Task 5
Step 6. §3.5 (the limits) → Task 5 Step 6 and Task 7 Step 6. §4.1–§4.4 → Task 2. §4.5 → Task 3. §4.6 →
Task 7 Step 1. §5 (the public API delta) → Task 1 Step 6. §6.2 → Task 2 Step 1. §6.3 → Task 6. §6.4 →
Task 4. §6.5 (what is not tested) → no task, correctly. §7 (deviations) → recorded in the spec, cited by
Task 7. §8.1 → Task 7 Step 6. §9's six acceptance clauses → Tasks 5, 6, 2, 3, 4, 7 and 8 respectively.

**Type consistency.** `JsonContentType.Refuse(HttpRequest, AlvoApiOptions) → IResult?` is declared in
Task 2 and called in Task 2 only. `ProblemResultFactory.UnsupportedMediaType(string method) → IResult` is
declared and called in Task 2. `JsonContentType.Advertised(string method) → string` is declared in Task 2
and used by `ProblemResultFactory` in the same task. `ResponsesFor(kind, entity, options)` and
`SharedRefusals(options)` are declared in Task 3 Step 3 and every caller is updated in Task 3 Step 4.
`BehaviourInvariants.NonJsonBodiesAreRefusedAsync(world, project)` and
`Sabotage.TheMediaTypeGuardIsOff()` are declared and called in Task 4.

**Three places the plan tells the implementer to check the code rather than trust it**, because a name
guessed here would be a bug there: `ResponseReading`'s helper names (Task 2 Step 1), `AlvoPage`/
`AlvoRecord`/`RoleCatalog.TryGet` member names (Task 5 Step 2), and `WebApplicationFactory`'s content-root
interaction with the sample's descriptor walk (Task 6 Step 2). Each is called out at its step.
