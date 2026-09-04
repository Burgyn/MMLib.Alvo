# PR-G — `POST {prefix}/{entity}/query` Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Give every entity a `POST {prefix}/{entity}/query` route that takes the PostgREST query parameters in a JSON body and answers the same page envelope as `GET {prefix}/{entity}`, through the same parser.

**Architecture:** The body is a JSON object whose members *are* the query-string parameters. `QueryBodyReader` transposes it into an `IQueryCollection` and hands that to the existing `QueryStringParser` — there is no second grammar and no second refusal catalogue for the grammar. Two enabling refactors come first: the bounded-read/shape-scan half of `JsonPayloadReader` becomes a shared `BoundedJsonBody`, and the documentation layer stops keying on `DataOperation` (the policy vocabulary) and starts keying on a new API-layer `DataApiEndpointKind`, so a second `list`-gated endpoint can carry its own `operationId` and prose.

**Tech Stack:** .NET 10 (`net10.0`), minimal APIs, `System.Text.Json` (`JsonDocument` for the query body, `JsonNode` for write payloads), Microsoft.OpenApi, xUnit v3 + Shouldly + Verify + CsCheck on Microsoft.Testing.Platform.

**Spec:** `docs/superpowers/specs/2026-09-04-f4-pr-g-post-query-design.md` — read it before Task 1. The plan argues from it and does not restate its reasoning.

## Global Constraints

- **Branch:** `f4/pr-g-post-query`. Never commit to `main`.
- **C# files are CRLF + UTF-8 BOM.** `.gitattributes` pins `*.cs text eol=crlf`; the pre-commit `dotnet format` gate fails on a file written LF-without-BOM through a shell heredoc. Prefer the `Write`/`Edit` tools; if a shell writes a `.cs`, normalise before staging.
- **Zero inline comments** (`alvo-dotnet-conventions`). Rationale goes in `/// <remarks>`; a `//` is a signal to rename or extract. XML docs are required on public members and are the house style for internal ones too.
- **Methods stay short** — a ~25-line ceiling; extract by default.
- **Nothing in this PR becomes `public`.** Every new type and member is `internal`. `PublicApi.*.verified.txt` must not move; if it does, the change is wrong.
- **No message may echo caller-supplied text.** Every violation's `Message` and `FixSuggestion` is built from constants plus server-owned values. The `Pointer` carries a parameter *role* (`filter`, `order`, `limit`, `offset`, `after`, `select`) or `""` for the body.
- **Assertions are Shouldly.** Never FluentAssertions.
- **Run `scripts/test-ring0` after every task**, `scripts/test-ring1` at the end of the last one, `scripts/test-ring2` before opening the PR.
- **Conventional Commits** — the `commit-msg` hook enforces it. Every commit message ends with `Claude-Session: https://claude.ai/code/session_01Uh7NkobnQZy5fDftEZbVLp`.

---

### Task 1: `BoundedJsonBody` — the shared bounded read and shape scan

Behaviour-neutral. `JsonPayloadReader` keeps answering exactly what it answers today; the mechanics move so a second reader can use them without inheriting the write path's prose.

**Files:**
- Create: `src/MMLib.Alvo/Api/Internal/BoundedJsonBody.cs`
- Modify: `src/MMLib.Alvo/Api/Internal/JsonPayloadReader.cs`
- Modify: `src/MMLib.Alvo/Api/Internal/PayloadViolations.cs`
- Test: `test/MMLib.Alvo.Api.Tests/PayloadBindingTests.cs` (existing — must stay green untouched)

**Interfaces:**
- Consumes: nothing.
- Produces:
  - `internal enum BodyRefusal { NotAnObject, MalformedJson, TooLarge, TooDeep, TooManyKeys, DuplicateName }`
  - `internal static Task<BodyRefusal?> BoundedJsonBody.ReadAsync(HttpRequest request, MemoryStream destination, AlvoApiOptions options, CancellationToken cancellationToken)`
  - `internal static string BoundedJsonBody.CodeOf(BodyRefusal refusal)`
  - `internal static AlvoViolation PayloadViolations.Body(BodyRefusal refusal, AlvoApiOptions options)`

- [ ] **Step 1: Create `BoundedJsonBody.cs`**

```csharp
using Microsoft.AspNetCore.Http;
using System.Text.Json;

namespace MMLib.Alvo.Api.Internal;

/// <summary>Why a request body was refused before anything could be read out of it.</summary>
/// <remarks>
/// An enum rather than a violation, because the <em>mechanics</em> of bounding a body are shared by the
/// write path and the query path while the <em>wording</em> is not: a read endpoint that answered "a write
/// payload is a flat map of the entity's declared fields" would hand an agent a fix for another operation.
/// Each surface's own catalogue maps this to its own violation, under the code
/// <see cref="BoundedJsonBody.CodeOf"/> gives — so the two can differ in prose and cannot differ in code.
/// </remarks>
internal enum BodyRefusal
{
    /// <summary>The body is not a JSON object.</summary>
    NotAnObject,

    /// <summary>The body is not well-formed JSON.</summary>
    MalformedJson,

    /// <summary>The body is past the configured byte bound.</summary>
    TooLarge,

    /// <summary>The body nests past the configured depth bound.</summary>
    TooDeep,

    /// <summary>The body carries more property names, at any depth, than the bound allows.</summary>
    TooManyKeys,

    /// <summary>One object in the body uses the same property name twice.</summary>
    DuplicateName,
}

/// <summary>
/// Reads a request body under Alvo's three payload bounds and decides its shape, without binding anything
/// to an entity and without composing a message.
/// </summary>
/// <remarks>
/// <para>
/// <b>Every bound refuses <em>before</em> the work it exists to prevent.</b> The size bound stops at the
/// first chunk that would cross it rather than buffering the body first, and the depth and key bounds are
/// decided by a forward-only <see cref="Utf8JsonReader"/> scan that builds no node tree. A bound applied to
/// a finished document has already paid the cost it exists to prevent.
/// </para>
/// <para>
/// <b>It hands back the buffer, not a parsed document.</b> The two callers parse differently on purpose:
/// <see cref="JsonPayloadReader"/> needs a <c>JsonNode</c> tree to bind field values and to digest for an
/// idempotency fingerprint, while <see cref="QueryBodyReader"/> needs a <c>JsonDocument</c> so a number can
/// contribute the literal text the caller wrote. Parsing here would force one of them to convert.
/// </para>
/// </remarks>
internal static class BoundedJsonBody
{
    /// <summary>One buffer's worth of body; the size bound trips on chunk boundaries, so this only sets the granularity.</summary>
    private const int ReadChunkBytes = 8 * 1024;

    /// <summary>
    /// Copies the request body into <paramref name="destination"/> and decides its shape, or reports the one
    /// bound that stopped it.
    /// </summary>
    /// <param name="request">The request whose body to read.</param>
    /// <param name="destination">Where the body is buffered; the caller owns it.</param>
    /// <param name="options">The payload bounds to enforce.</param>
    /// <param name="cancellationToken">A token to cancel the read.</param>
    internal static async Task<BodyRefusal?> ReadAsync(
        HttpRequest request,
        MemoryStream destination,
        AlvoApiOptions options,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(destination);
        ArgumentNullException.ThrowIfNull(options);

        var readFailure = await ReadBoundedAsync(
            request, destination, options.MaxRequestBodyBytes, cancellationToken).ConfigureAwait(false);

        return readFailure
            ?? EnsureWithinShapeBounds(destination.GetBuffer().AsSpan(0, (int)destination.Length), options);
    }

    /// <summary>The stable code a refusal is published under, whichever surface words it.</summary>
    /// <param name="refusal">The bound that stopped the body.</param>
    internal static string CodeOf(BodyRefusal refusal) => refusal switch
    {
        BodyRefusal.NotAnObject => "not-an-object",
        BodyRefusal.MalformedJson => "malformed-json",
        BodyRefusal.TooLarge => "body-too-large",
        BodyRefusal.TooDeep => "body-too-deep",
        BodyRefusal.TooManyKeys => "body-too-many-fields",
        BodyRefusal.DuplicateName => "duplicate-field",
        _ => throw new ArgumentOutOfRangeException(
            nameof(refusal), refusal, "Unmapped body refusal; give it a stable code here."),
    };

    /// <summary>
    /// Copies the body into <paramref name="destination"/>, refusing at the first chunk that would cross
    /// <paramref name="maxBytes"/>. A declared <c>Content-Length</c> past the bound is refused without
    /// reading a byte; a chunked body that declares no length is bounded all the same, because the check
    /// is on what has actually arrived.
    /// </summary>
    private static async Task<BodyRefusal?> ReadBoundedAsync(
        HttpRequest request, MemoryStream destination, int maxBytes, CancellationToken cancellationToken)
    {
        if (request.ContentLength > maxBytes)
        {
            return BodyRefusal.TooLarge;
        }

        var chunk = new byte[ReadChunkBytes];
        int read;
        while ((read = await request.Body.ReadAsync(chunk, cancellationToken).ConfigureAwait(false)) > 0)
        {
            if (destination.Length + read > maxBytes)
            {
                return BodyRefusal.TooLarge;
            }

            destination.Write(chunk, 0, read);
        }

        return null;
    }

    /// <summary>
    /// Decides the shape bounds — is it an object at all, how deep does it nest, how many property names does
    /// it carry <em>anywhere</em> — from a forward-only scan that builds nothing.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The reader's own <see cref="JsonReaderOptions.MaxDepth"/> is deliberately given headroom over
    /// <see cref="AlvoApiOptions.MaxPayloadDepth"/>. The reader raises the same <see cref="JsonException"/>
    /// for a too-deep body as for a malformed one, so anything the reader refuses could only ever be reported
    /// as "not well-formed JSON" — the one bound whose message could not name itself, which sends an agent
    /// hunting a syntax error that is not there. Checking <see cref="Utf8JsonReader.CurrentDepth"/> first
    /// means the depth refusal names the depth.
    /// </para>
    /// <para>
    /// The headroom is <b>two</b> levels, not one, because the two numbers are counted differently:
    /// <see cref="JsonReaderOptions.MaxDepth"/> counts the outermost container as level 1 while
    /// <see cref="Utf8JsonReader.CurrentDepth"/> reports it as 0. With only one level of slack the reader
    /// threw on the very token whose <see cref="Utf8JsonReader.CurrentDepth"/> the check needed to see, and
    /// the named message was unreachable — measured, not reasoned. The reader remains a hard backstop; it is
    /// simply never the first to speak.
    /// </para>
    /// </remarks>
    private static BodyRefusal? EnsureWithinShapeBounds(ReadOnlySpan<byte> utf8Body, AlvoApiOptions options)
    {
        var reader = new Utf8JsonReader(
            utf8Body,
            new JsonReaderOptions { MaxDepth = options.MaxPayloadDepth + 2, AllowTrailingCommas = false });

        try
        {
            if (!reader.Read() || reader.TokenType != JsonTokenType.StartObject)
            {
                return BodyRefusal.NotAnObject;
            }

            return ScanShape(ref reader, options);
        }
        catch (JsonException)
        {
            return BodyRefusal.MalformedJson;
        }
    }

    /// <summary>
    /// Walks every token of the body, refusing as soon as the property-name count or the nesting depth
    /// crosses its bound — or as soon as one object uses a name twice.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Property names are counted at every depth, not just the top level.</b> Counting only depth 1 was a
    /// bound that did not bound: <c>{"name":{…150 000 keys…}}</c> satisfied it, satisfied the depth cap at
    /// depth 2, fitted inside <see cref="AlvoApiOptions.MaxRequestBodyBytes"/>, and was then materialised in
    /// full — a ~20–40× memory amplification per request, refused only afterwards.
    /// </para>
    /// <para>
    /// <b>The duplicate-name check is here for the same reason, and it is the one bound whose absence was a
    /// leak rather than a cost.</b> A repeated name passed this scan and passed <c>JsonNode.Parse</c> too,
    /// because a <c>JsonObject</c>'s backing dictionary materialises lazily — so the binder was the first
    /// thing to touch it and threw <see cref="ArgumentException"/> with a .NET dictionary message that ended
    /// in the caller's own key. Deciding it here refuses the body <em>before</em> the node tree exists.
    /// </para>
    /// <para>
    /// The comparison matches <c>JsonObject</c>'s own exactly, which is what makes this a pre-emption rather
    /// than a second opinion: the name is read through <see cref="Utf8JsonReader.GetString"/>, and it is
    /// <see cref="StringComparer.Ordinal"/>, so <c>a</c> and <c>A</c> are two names.
    /// </para>
    /// </remarks>
    private static BodyRefusal? ScanShape(ref Utf8JsonReader reader, AlvoApiOptions options)
    {
        var names = 0;
        var seen = new NamesByDepth();

        seen.Enter(reader.CurrentDepth + 1);

        while (reader.Read())
        {
            if (reader.CurrentDepth > options.MaxPayloadDepth)
            {
                return BodyRefusal.TooDeep;
            }

            if (reader.TokenType == JsonTokenType.StartObject)
            {
                seen.Enter(reader.CurrentDepth + 1);
            }
            else if (reader.TokenType == JsonTokenType.PropertyName)
            {
                if (++names > options.MaxPayloadKeys)
                {
                    return BodyRefusal.TooManyKeys;
                }

                if (!seen.Add(reader.CurrentDepth, reader.GetString()!))
                {
                    return BodyRefusal.DuplicateName;
                }
            }
        }

        return null;
    }

    /// <summary>
    /// The property names seen so far in the object currently open at each depth — enough to decide "this
    /// object already has that name" from a forward-only scan that keeps no node tree.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Keyed by depth and cleared on entry, which is what makes sibling objects independent.</b> In
    /// <c>{"a":{"x":1},"b":{"x":2}}</c> both <c>x</c> sit at the same depth and are <em>not</em> duplicates, so
    /// a single set for the whole body would refuse a perfectly ordinary payload.
    /// </para>
    /// <para>
    /// The sets are reused rather than allocated per object, because a wide array of small objects would
    /// otherwise allocate one <see cref="HashSet{T}"/> per element on a path that already refuses to build a
    /// node tree.
    /// </para>
    /// </remarks>
    private sealed class NamesByDepth
    {
        private readonly List<HashSet<string>> _byDepth = [];

        /// <summary>Opens a fresh object whose own property names will be reported at <paramref name="depth"/>.</summary>
        internal void Enter(int depth)
        {
            while (_byDepth.Count <= depth)
            {
                _byDepth.Add(new HashSet<string>(StringComparer.Ordinal));
            }

            _byDepth[depth].Clear();
        }

        /// <summary>
        /// Records one property name, answering <see langword="false"/> when the object open at
        /// <paramref name="depth"/> already carried it.
        /// </summary>
        internal bool Add(int depth, string name) => _byDepth[depth].Add(name);
    }
}
```

- [ ] **Step 2: Add `PayloadViolations.Body` and re-word the six producers through it**

In `PayloadViolations.cs`, add this member and change the six existing producers (`NotAnObject`, `MalformedJson`, `TooLarge`, `TooDeep`, `TooManyKeys`, `DuplicateField`) to take their `Code` from `BoundedJsonBody.CodeOf(...)` instead of a literal. Keep every existing message and fix suggestion byte-identical — the write path's wording does not change in this PR.

```csharp
    /// <summary>The write path's wording for a body one of the shared bounds refused.</summary>
    /// <remarks>
    /// The <em>code</em> comes from <see cref="BoundedJsonBody.CodeOf"/> so it cannot drift from the query
    /// path's, and the <em>prose</em> stays here so a write's fix suggestion can talk about writing.
    /// </remarks>
    /// <param name="refusal">The bound that stopped the body.</param>
    /// <param name="options">The options the bounds are published from.</param>
    internal static AlvoViolation Body(BodyRefusal refusal, AlvoApiOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        return refusal switch
        {
            BodyRefusal.NotAnObject => NotAnObject(),
            BodyRefusal.MalformedJson => MalformedJson(),
            BodyRefusal.TooLarge => TooLarge(options.MaxRequestBodyBytes),
            BodyRefusal.TooDeep => TooDeep(options.MaxPayloadDepth),
            BodyRefusal.TooManyKeys => TooManyKeys(options.MaxPayloadKeys),
            BodyRefusal.DuplicateName => DuplicateField(),
            _ => throw new ArgumentOutOfRangeException(
                nameof(refusal), refusal, "Unmapped body refusal; give it the write path's wording here."),
        };
    }
```

- [ ] **Step 3: Rewrite `JsonPayloadReader.ReadAsync` to use the helper, and delete what moved**

Replace `ReadAsync`'s body, and delete `ReadBoundedAsync`, `EnsureWithinShapeBounds`, `ScanShape`, `NamesByDepth` and the `ReadChunkBytes` constant from `JsonPayloadReader`. Keep `Payload`, `Refused`, `Bind`, `BindOne`, `DeclaredFields`, `TryBind` and `Convert` exactly as they are.

```csharp
    internal static async Task<Payload> ReadAsync(
        HttpRequest request, EntitySchema entity, AlvoApiOptions options, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(entity);
        ArgumentNullException.ThrowIfNull(options);

        using var body = new MemoryStream();
        var refusal = await BoundedJsonBody
            .ReadAsync(request, body, options, cancellationToken).ConfigureAwait(false);

        return refusal is { } refused
            ? Refused(PayloadViolations.Body(refused, options))
            : Bind(body, entity, options);
    }
```

Update the type's `<remarks>` where it describes the bounds: the sentence beginning *"It is still bounded three ways"* now says the bounds are enforced by `BoundedJsonBody` and why the parse stays here (the fingerprint needs the node tree).

- [ ] **Step 4: Run the existing payload facts to prove nothing moved**

Run: `dotnet test --test-modules "test/MMLib.Alvo.Api.Tests/bin/Debug/*/MMLib.Alvo.Api.Tests.dll" --filter-class "*PayloadBindingTests" --root-directory .`
Expected: PASS, with the same count as before the change. `ValidationTests` and `ProblemDetailsTests` must also be green.

- [ ] **Step 5: Run ring0**

Run: `scripts/test-ring0`
Expected: `[ring0] OK`

- [ ] **Step 6: Commit**

```bash
git add src/MMLib.Alvo/Api/Internal/BoundedJsonBody.cs \
        src/MMLib.Alvo/Api/Internal/JsonPayloadReader.cs \
        src/MMLib.Alvo/Api/Internal/PayloadViolations.cs
git commit -m "refactor(api): hoist the bounded body read and shape scan out of the write path

The mechanics of bounding a JSON body are the same for a write and for a
query; the wording is not. BoundedJsonBody answers with a BodyRefusal and
each surface's catalogue words it, sharing the stable code and nothing else.

Claude-Session: https://claude.ai/code/session_01Uh7NkobnQZy5fDftEZbVLp"
```

---

### Task 2: `DataApiEndpointKind` — the documentation layer stops keying on the policy vocabulary

Behaviour-neutral. No route is added; every `operationId`, summary, description, parameter list and response set stays byte-identical. What changes is what they are keyed by, so Task 5 can add a second `list`-gated endpoint.

**Files:**
- Create: `src/MMLib.Alvo/Api/Internal/DataApiEndpointKind.cs`
- Modify: `src/MMLib.Alvo/Api/Internal/DataApiOperationMetadata.cs`
- Modify: `src/MMLib.Alvo/Api/Internal/DataApiEndpoints.cs` (`Map`, `Protect`, `Documenting`)
- Modify: `src/MMLib.Alvo/Api/Internal/DataApiDocumentation.cs` (`ResponsesFor`, `SummaryOf`, `DescriptionOf`)
- Modify: `src/MMLib.Alvo/Api/Internal/DataApiParameters.cs` (`For`, `UsedSharedIds`, `Names`, `HeaderNames`, `AddressesOneRow`)
- Modify: `src/MMLib.Alvo/Api/Internal/DataApiHeaders.cs` (`AddTo`, `UsedIds`)
- Modify: `src/MMLib.Alvo/Api/Internal/AlvoDocumentTransformer.cs` (`Operations`, `Reusable`, `Enrich`, `OperationId`, `RequestBody`, `BodyComponent`, `Responses`)
- Test: `test/MMLib.Alvo.Api.Tests/AlvoExceptionHandlerTests.cs`, `test/MMLib.Alvo.Api.Tests/AlvoExceptionHandlerScopeTests.cs`, `test/MMLib.Alvo.Api.Tests/DataApiRoutingTests.cs`

**Interfaces:**
- Consumes: nothing from Task 1.
- Produces:
  - `internal enum DataApiEndpointKind { List, Query, Get, Create, Update, Delete }`
  - `internal static DataOperation DataApiEndpointKinds.ToDataOperation(this DataApiEndpointKind kind)`
  - `internal static string DataApiEndpointKinds.ToWireName(this DataApiEndpointKind kind)`
  - `internal sealed record DataApiOperationMetadata(string Entity, DataApiEndpointKind Kind)` with `internal DataOperation Operation => Kind.ToDataOperation();`

- [ ] **Step 1: Write the failing test**

Add to `test/MMLib.Alvo.Api.Tests/DataApiRoutingTests.cs`:

```csharp
    /// <summary>
    /// The kind is the API layer's own vocabulary and the operation is policy's. Two kinds map to
    /// <c>list</c> on purpose — a second, body-shaped way to reach the same read — and every other kind is
    /// one-to-one, so a kind added later cannot silently gate as the wrong operation.
    /// </summary>
    [Fact]
    public void Every_endpoint_kind_maps_to_the_operation_its_filter_must_gate()
    {
        DataApiEndpointKind.List.ToDataOperation().ShouldBe(DataOperation.List);
        DataApiEndpointKind.Query.ToDataOperation().ShouldBe(DataOperation.List);
        DataApiEndpointKind.Get.ToDataOperation().ShouldBe(DataOperation.Get);
        DataApiEndpointKind.Create.ToDataOperation().ShouldBe(DataOperation.Create);
        DataApiEndpointKind.Update.ToDataOperation().ShouldBe(DataOperation.Update);
        DataApiEndpointKind.Delete.ToDataOperation().ShouldBe(DataOperation.Delete);
    }

    /// <summary>
    /// A kind's wire name is what the document's <c>operationId</c> is built from, so the five that existed
    /// before this split must keep the spelling they published — and the sixth must not collide with them.
    /// </summary>
    [Fact]
    public void Every_endpoint_kind_has_its_own_wire_name_and_the_five_original_ones_are_unchanged()
    {
        DataApiEndpointKind.List.ToWireName().ShouldBe("list");
        DataApiEndpointKind.Get.ToWireName().ShouldBe("get");
        DataApiEndpointKind.Create.ToWireName().ShouldBe("create");
        DataApiEndpointKind.Update.ToWireName().ShouldBe("update");
        DataApiEndpointKind.Delete.ToWireName().ShouldBe("delete");
        DataApiEndpointKind.Query.ToWireName().ShouldBe("query");

        Enum.GetValues<DataApiEndpointKind>()
            .Select(kind => kind.ToWireName())
            .Distinct(StringComparer.Ordinal)
            .Count()
            .ShouldBe(Enum.GetValues<DataApiEndpointKind>().Length);
    }
```

- [ ] **Step 2: Run it to verify it fails**

Run: `dotnet build MMLib.Alvo.slnx -c Debug`
Expected: FAIL — `DataApiEndpointKind` does not exist.

- [ ] **Step 3: Create `DataApiEndpointKind.cs`**

```csharp
using MMLib.Alvo.Rules;

namespace MMLib.Alvo.Api.Internal;

/// <summary>
/// The generated Data API's own vocabulary for "which endpoint is this" — one member per mapped route,
/// which is <b>not</b> the same thing as one member per <see cref="DataOperation"/>.
/// </summary>
/// <remarks>
/// <para>
/// <b>It exists because <see cref="DataOperation"/> is the policy vocabulary.</b> A descriptor's
/// <c>rules</c> name those operations and <c>PolicyCatalog</c> is keyed by them, so a member added there
/// would let a descriptor configure a rule for a <em>transport</em> — and would make "<c>list</c> is
/// unconfigured" stop answering for a route that is a list. <see cref="Query"/> is a second way to reach
/// the same read, not a sixth thing a caller may be permitted to do.
/// </para>
/// <para>
/// <b>Everything the published document keys on keys on this</b> — the <c>operationId</c>, the summary,
/// the description, the parameter list, the request body and the response catalogue — because two routes
/// gated as one operation would otherwise mint one <c>operationId</c> twice and publish one route's prose
/// for the other.
/// </para>
/// </remarks>
internal enum DataApiEndpointKind
{
    /// <summary>The collection read, with its parameters in the query string.</summary>
    List,

    /// <summary>The collection read, with its parameters in a JSON request body.</summary>
    Query,

    /// <summary>The single-row read.</summary>
    Get,

    /// <summary>The create.</summary>
    Create,

    /// <summary>The partial update.</summary>
    Update,

    /// <summary>The delete.</summary>
    Delete,
}

/// <summary>What a <see cref="DataApiEndpointKind"/> means to the layers below and above it.</summary>
internal static class DataApiEndpointKinds
{
    /// <summary>The operation this endpoint's authorization filter gates it as.</summary>
    /// <param name="kind">The endpoint kind.</param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="kind"/> is not one of the named cases.</exception>
    internal static DataOperation ToDataOperation(this DataApiEndpointKind kind) => kind switch
    {
        DataApiEndpointKind.List or DataApiEndpointKind.Query => DataOperation.List,
        DataApiEndpointKind.Get => DataOperation.Get,
        DataApiEndpointKind.Create => DataOperation.Create,
        DataApiEndpointKind.Update => DataOperation.Update,
        DataApiEndpointKind.Delete => DataOperation.Delete,
        _ => throw new ArgumentOutOfRangeException(
            nameof(kind), kind, "Unmapped endpoint kind; state which operation gates it here."),
    };

    /// <summary>The spelling this endpoint's <c>operationId</c> is built from.</summary>
    /// <remarks>
    /// The five that existed before <see cref="DataApiEndpointKind.Query"/> read their spelling from
    /// <see cref="DataOperation"/>'s own table rather than repeating it, so no published
    /// <c>operationId</c> can move; only a kind whose name is <em>not</em> an operation's needs a spelling
    /// of its own, and it is spelled here rather than in <c>Abstractions</c>, where a transport's name has
    /// no business being.
    /// </remarks>
    /// <param name="kind">The endpoint kind.</param>
    internal static string ToWireName(this DataApiEndpointKind kind) => kind switch
    {
        DataApiEndpointKind.Query => "query",
        _ => kind.ToDataOperation().ToWireName(),
    };
}
```

- [ ] **Step 4: Change the marker**

`src/MMLib.Alvo/Api/Internal/DataApiOperationMetadata.cs` — change the record's second positional parameter and add the derived member. Keep the whole existing `<remarks>` block and add one paragraph explaining the split.

```csharp
/// <param name="Entity">The entity the endpoint serves, as the applied schema names it.</param>
/// <param name="Kind">The endpoint this route is, which is finer than the operation it gates.</param>
internal sealed record DataApiOperationMetadata(string Entity, DataApiEndpointKind Kind)
{
    /// <summary>The operation the endpoint performs, and the one its filter gates.</summary>
    /// <remarks>
    /// Derived rather than stored, so a marker cannot claim a kind and an operation that disagree —
    /// which would be a route gated as something other than what it is.
    /// </remarks>
    internal DataOperation Operation => Kind.ToDataOperation();
}
```

- [ ] **Step 5: Thread the kind through the six consumers**

Mechanical, and every existing output must stay identical. In each case the parameter type changes from `DataOperation` to `DataApiEndpointKind` and every `switch` arm gains a `Query` case that answers exactly what `List` answers:

1. `DataApiEndpoints.Map` — pass `DataApiEndpointKind.List/Get/Create/Update/Delete` to the five `Map*` helpers instead of `DataOperation.*`.
2. `DataApiEndpoints.Protect(this RouteHandlerBuilder builder, EntitySchema entity, DataApiEndpointKind kind, …)` — build the filter with `filters.For(entity.Name, kind.ToDataOperation())`, stamp `new DataApiOperationMetadata(entity.Name, kind)`, and call `.Documenting(entity, kind)`.
3. `DataApiEndpoints.Documenting(this RouteHandlerBuilder builder, EntitySchema entity, DataApiEndpointKind kind)` — `DataApiDocumentation.ResponsesFor(kind, entity)`.
4. `DataApiDocumentation.ResponsesFor/SummaryOf/DescriptionOf` take a kind. `Query` reuses `List`'s arm in all three **for now**; Task 6 gives it its own summary and description.
5. `DataApiParameters.For/UsedSharedIds/Names/HeaderNames/AddressesOneRow` take kinds. `Query` reuses `List`'s arms **for now**; Task 6 narrows them.
6. `DataApiHeaders.AddTo/UsedIds` take `IEnumerable<(DataApiEndpointKind Kind, EntitySchema Entity)>`.
7. `AlvoDocumentTransformer.Operations` returns `IReadOnlyList<(DataApiEndpointKind Kind, EntitySchema Entity)>` built from `endpoint.Marker.Kind`, and `Reusable` takes that type. `Enrich` passes `endpoint.Marker.Kind` to `SummaryOf`, `DescriptionOf` and `DataApiParameters.For`. `OperationId(DataApiOperationMetadata marker)`, `RequestBody(DataApiOperationMetadata marker, …)` and `Responses(DataApiOperationMetadata marker, …)` keep taking the marker and change what they read from it: `marker.Kind.ToWireName()`, `BodyComponent(marker.Kind, …)` and `ResponsesFor(marker.Kind, entity)` respectively. None of them takes an `Endpoint`.

`BodyComponent` becomes:

```csharp
    private static string? BodyComponent(DataApiEndpointKind kind, string entity) => kind switch
    {
        DataApiEndpointKind.Create => SchemaComponentBuilder.CreateId(entity),
        DataApiEndpointKind.Update => SchemaComponentBuilder.PatchId(entity),
        _ => null,
    };
```

- [ ] **Step 6: Fix the two positional constructions in tests**

`AlvoExceptionHandlerTests.cs` and `AlvoExceptionHandlerScopeTests.cs` each build `new DataApiOperationMetadata("owners", DataOperation.List)`. Change both to `new DataApiOperationMetadata("owners", DataApiEndpointKind.List)` and add `using MMLib.Alvo.Api.Internal;` if it is not already there.

- [ ] **Step 7: Run the tests**

Run: `scripts/test-ring0`
Expected: `[ring0] OK`. **`OpenApiDocumentTests.The_document_is_stable` must pass with the snapshot untouched** — if it does not, the threading changed an output and the refactor is wrong.

- [ ] **Step 8: Commit**

```bash
git add src/MMLib.Alvo/Api/Internal/ test/MMLib.Alvo.Api.Tests/
git commit -m "refactor(api): key the published document on an endpoint kind, not on the policy operation

DataOperation is what a descriptor's rules name, so it cannot grow a member
for a transport. The API layer gets its own six-valued kind; the filter still
gates on the operation the kind maps to, and every operationId, summary and
response set is byte-identical.

Claude-Session: https://claude.ai/code/session_01Uh7NkobnQZy5fDftEZbVLp"
```

---

### Task 3: The three comma-splitting readers stop materialising before they charge

The one budget the URL length was silently providing. Design §2.5.

**Files:**
- Modify: `src/MMLib.Alvo/Api/Internal/ParenthesisedList.cs`
- Modify: `src/MMLib.Alvo/Api/Internal/FilterGroupParser.cs` (the `TrySplit` call site)
- Modify: `src/MMLib.Alvo/Api/Internal/FilterTermParser.cs` (`TryReadCandidates`)
- Modify: `src/MMLib.Alvo/Api/Internal/SortParser.cs` (`TryParse`, `TryAddKey`)
- Modify: `src/MMLib.Alvo/Api/Internal/QueryStringParser.cs` (`ReadSelect`, plus a new `MaxSelectEntries`)
- Modify: `src/MMLib.Alvo/Api/Internal/QueryViolations.cs` (`TooManySelectEntries`)
- Test: `test/MMLib.Alvo.Api.Tests/QueryStringParserTests.cs`

**Interfaces:**
- Consumes: nothing.
- Produces:
  - `internal enum ListSplit { Ok, Malformed, TooMany }`
  - `internal static ListSplit ParenthesisedList.Split(string raw, int maxMembers, out IReadOnlyList<string> members)`
  - `internal static int QueryStringParser.MaxSelectEntries { get; }`
  - `internal static int QueryStringParser.MaxPatternLength { get; }`
  - `internal static AlvoViolation QueryViolations.PatternTooLong(int maxLength)`
  - `internal static AlvoViolation QueryViolations.TooManySelectEntries(int maxEntries)`

- [ ] **Step 1: Write the failing tests**

Add to `QueryStringParserTests.cs`:

```csharp
    /// <summary>
    /// A projection's <em>entry</em> count is bounded, and it has to be separately from the width bound: a
    /// repeated entry claims no new key, so <c>projection-too-wide</c> can never fire on one. Until POST
    /// query the only thing bounding it was the URL length, which is a property of the transport rather
    /// than a decision this layer made.
    /// </summary>
    [Fact]
    public void A_projection_naming_more_entries_than_the_parser_reads_is_refused()
    {
        var entries = string.Join(',', Enumerable.Repeat("id", QueryStringParser.MaxSelectEntries + 1));

        TryParse($"select={entries}", out _, out var violations).ShouldBeFalse();

        violations.ShouldContain(violation => violation.Code == "too-many-select-entries");
    }

    /// <summary>
    /// The entry bound does not retire the deduplication the width bound was written around: a projection
    /// naming one field far more often than the entity has fields is still one key and still a 200. The
    /// existing <c>A_projection_repeating_one_field_past_the_field_count_still_dedupes</c> asserts the same
    /// property against the <em>width</em> bound; this one asserts it survives the new entry bound, which is
    /// a different thing to lose.
    /// </summary>
    [Fact]
    public void A_repeated_projection_entry_still_deduplicates_under_the_entry_bound()
    {
        var entries = string.Join(',', Enumerable.Repeat("id", QueryStringParser.MaxSelectEntries));

        TryParse($"select={entries}", out var parsed, out var violations).ShouldBeTrue(Because(violations));

        parsed!.Select!.Count.ShouldBe(1);
    }

    /// <summary>
    /// A group carrying more members than the node budget is refused as too wide — the code it already
    /// earned. What changed is that it is reached before the member list is materialised, which no
    /// assertion on the answer can see; the existing candidate-limit fact
    /// (<c>An_in_list_is_capped_at_the_ports_candidate_limit</c>) already pins the <c>in</c> side of the
    /// same boundary, so only the group side is added here.
    /// </summary>
    [Fact]
    public void A_group_past_the_node_budget_is_refused_as_too_wide()
    {
        var members = string.Join(',', Enumerable.Repeat("year.eq.1", AlvoFilter.MaxTerms + 1));

        TryParse($"or=({members})", out _, out var violations).ShouldBeFalse();

        violations.ShouldContain(violation => violation.Code == "filter-too-wide");
    }

    /// <summary>
    /// A <c>like</c> pattern is bounded and every other operand is not, because the two cost different
    /// things: an <c>eq</c> operand is a bound value whose comparison is linear in its size and
    /// short-circuits on the first differing byte, while a pattern is matched against every row and its
    /// cost is not linear in its length. Under a URL both were capped by the request line; a body caps
    /// neither.
    /// </summary>
    [Fact]
    public void A_like_pattern_longer_than_the_parser_matches_is_refused()
    {
        var pattern = new string('%', QueryStringParser.MaxPatternLength + 1);

        TryParse($"make=like.{pattern}", out _, out var violations).ShouldBeFalse();

        violations.ShouldContain(violation => violation.Code == "pattern-too-long");
    }

    /// <summary>
    /// And the bound reaches only the two pattern operators: an equality against a long value is a
    /// comparison a caller may legitimately want, and refusing it would be a bound on data rather than on
    /// cost.
    /// </summary>
    [Fact]
    public void A_long_equality_operand_is_not_a_pattern_and_is_not_refused()
    {
        var value = new string('a', QueryStringParser.MaxPatternLength + 1);

        TryParse($"make=eq.{value}", out _, out var violations).ShouldBeTrue(Because(violations));
    }
```

- [ ] **Step 2: Run to verify they fail**

Run: `dotnet test --test-modules "test/MMLib.Alvo.Api.Tests/bin/Debug/*/MMLib.Alvo.Api.Tests.dll" --filter-class "*QueryStringParserTests" --root-directory .`
Expected: the first fails to compile (`MaxSelectEntries` does not exist).

- [ ] **Step 3: Give `ParenthesisedList` a maximum and a third outcome**

```csharp
/// <summary>What splitting a bracketed list produced.</summary>
internal enum ListSplit
{
    /// <summary>The members, in the order written.</summary>
    Ok,

    /// <summary>The text is not a balanced, non-empty <c>(…)</c>.</summary>
    Malformed,

    /// <summary>The list carries more members than the caller will accept.</summary>
    TooMany,
}
```

Replace `TrySplit`/`SplitTopLevel` with:

```csharp
    /// <summary>
    /// Splits <paramref name="raw"/> — which must be a balanced, non-empty <c>(…)</c> — into its
    /// top-level, comma-separated members, stopping as soon as there are more than
    /// <paramref name="maxMembers"/> of them.
    /// </summary>
    /// <remarks>
    /// <b>The maximum is taken rather than left to the caller to check afterwards, and that is the whole of
    /// why it is a parameter.</b> Both callers already refused an over-long list one line later — a group
    /// past <see cref="AlvoFilter.MaxTerms"/>, an <c>in</c> list past
    /// <see cref="AlvoFilter.MaxInCandidates"/> — but only after this method had allocated every member. A
    /// request line capped that at a few hundred; a request <em>body</em> does not, so the bound has to be
    /// spent while splitting, exactly as <c>FilterParseScope</c>'s node budget is spent while descending.
    /// </remarks>
    /// <remarks>
    /// The refusal is raised after a member is added and only when a separator proves another is coming, so
    /// exactly <paramref name="maxMembers"/> members split cleanly and the first one past it refuses. Written
    /// the other way round — refusing before the add — the trailing member the loop appends afterwards made
    /// the effective bound <paramref name="maxMembers"/> plus one, which no test could see because the
    /// caller's own budget then produced the same code one line later.
    /// </remarks>
    /// <param name="raw">The caller-supplied bracketed text.</param>
    /// <param name="maxMembers">The most members the caller will accept.</param>
    /// <param name="members">The members, in the order written; empty unless the outcome is <see cref="ListSplit.Ok"/>.</param>
    internal static ListSplit Split(string raw, int maxMembers, out IReadOnlyList<string> members)
    {
        ArgumentNullException.ThrowIfNull(raw);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxMembers);

        members = [];
        if (raw.Length < 3 || raw[0] != '(' || raw[^1] != ')')
        {
            return ListSplit.Malformed;
        }

        var outcome = SplitTopLevel(raw[1..^1], maxMembers, out var split);
        if (outcome == ListSplit.Ok)
        {
            members = split!;
        }

        return outcome;
    }

    /// <summary>The members of an already-unwrapped list, or why it produced none.</summary>
    private static ListSplit SplitTopLevel(string inner, int maxMembers, out List<string>? members)
    {
        members = [];
        var depth = 0;
        var start = 0;

        for (var index = 0; index < inner.Length; index++)
        {
            var character = inner[index];
            if (character == '(')
            {
                depth++;
            }
            else if (character == ')' && --depth < 0)
            {
                members = null;
                return ListSplit.Malformed;
            }
            else if (character == ',' && depth == 0)
            {
                members.Add(inner[start..index]);
                start = index + 1;
                if (members.Count == maxMembers)
                {
                    members = null;
                    return ListSplit.TooMany;
                }
            }
        }

        if (depth != 0)
        {
            members = null;
            return ListSplit.Malformed;
        }

        members.Add(inner[start..]);
        return ListSplit.Ok;
    }
```

- [ ] **Step 4: Update the two call sites**

`FilterGroupParser`, replacing the `TrySplit` block:

```csharp
        var split = ParenthesisedList.Split(list, AlvoFilter.MaxTerms, out var members);
        if (split != ListSplit.Ok)
        {
            violation = split == ListSplit.TooMany
                ? QueryViolations.FilterTooWide()
                : QueryViolations.MalformedGroup();
            return false;
        }
```

`FilterTermParser.TryReadCandidates`, replacing both the `TrySplit` block and the now-subsumed `candidates.Count > AlvoFilter.MaxInCandidates` comparison:

```csharp
        value = null;
        var split = ParenthesisedList.Split(operand, AlvoFilter.MaxInCandidates, out var candidates);
        if (split != ListSplit.Ok)
        {
            violation = split == ListSplit.TooMany
                ? QueryViolations.TooManyInCandidates()
                : QueryViolations.MalformedInList();
            return false;
        }

        if (!scope.TryChargeCandidates(candidates.Count))
        {
            violation = QueryViolations.TooManyInCandidates();
            return false;
        }
```

- [ ] **Step 5: Make `SortParser` lazy**

In `TryParse`, replace `foreach (var token in raw.Split(','))` with:

```csharp
        foreach (var token in raw.AsSpan().Split(','))
        {
            if (!TryAddKey(raw[token], fields, keys, out violation))
            {
                return false;
            }
        }
```

In `TryAddKey`, replace `var parts = token.Split('.');` with:

```csharp
        var parts = token.Split('.', SortKeyParts + 1);
```

and add beside the other constants:

```csharp
    /// <summary>How many dot-separated parts one sort key can carry: the field, a direction, a null placement.</summary>
    /// <remarks>
    /// Passed as a split limit rather than checked afterwards, so a single key of a million dots costs three
    /// substrings and a refusal instead of a million substrings and the same refusal. A fourth part is
    /// refused by <c>TryReadModifiers</c> exactly as it was, because the limit leaves the tail in the last
    /// part rather than discarding it.
    /// </remarks>
    private const int SortKeyParts = 3;
```

`order` needs no entry bound of its own: once every readable field is named once, the next entry repeats one and earns `repeated-sort-key`, and the loop returns on the first violation — so the work is capped by the entity's field count.

- [ ] **Step 6: Bound and lazily split `select`**

In `QueryStringParser`, beside `MaxCursorLength`:

```csharp
    /// <summary>How many comma-separated entries one <c>select</c> may carry.</summary>
    /// <remarks>
    /// <b>Separate from the width bound, because a repeated entry claims no key.</b>
    /// <c>projection-too-wide</c> counts <em>distinct</em> response keys, which is what keeps
    /// <c>?select=id,id,id</c> deduplicating — and is therefore something a repeat can never trip. Until the
    /// query body existed, the only thing bounding the entry count was the URL length: a property of the
    /// transport rather than a decision this layer made, and one a request body does not have. The number is
    /// <see cref="AlvoFilter.MaxTerms"/> rather than a new one, because that is the framework's single
    /// measured "how many of a thing may one request carry", and 256 entries of at least two characters is
    /// under 800 bytes — unreachable from any query string a proxy would have carried.
    /// </remarks>
    /// <remarks>
    /// <b>The coupling to <see cref="AlvoFilter.MaxTerms"/> is deliberate and is the only thing tying these
    /// two budgets together</b>: they count different things — filter nodes and projection entries — and a
    /// change to the filter's breadth would move this one with it. That is accepted rather than overlooked,
    /// because the alternative is a second unmeasured number, and both answer the same question about the
    /// same request. Give this its own literal the day the two need to differ.
    /// </remarks>
    internal static int MaxSelectEntries { get; } = AlvoFilter.MaxTerms;

    /// <summary>The longest <c>like</c>/<c>ilike</c> pattern this API passes to an engine.</summary>
    /// <remarks>
    /// <para>
    /// <b>Only the two pattern operators are bounded, and the asymmetry is about cost rather than about
    /// size.</b> Every other operand is a bound value: the engine compares it per row, the comparison is
    /// linear in its length and short-circuits on the first differing byte, and its total size is already
    /// bounded by the request body. A <em>pattern</em> is matched rather than compared, and its cost is not
    /// linear in its length — so a caller who could send a megabyte of <c>%_%_%_…</c> would be buying an
    /// engine-side match per row for the price of one request.
    /// </para>
    /// <para>
    /// <b>Until the query body existed, the request line was what bounded this</b> — the same transport
    /// budget <see cref="MaxSelectEntries"/> exists to replace, applied to the one channel that never
    /// splits on a comma and so was not covered by making the three splitters lazy.
    /// </para>
    /// <para>
    /// <b>512 is chosen rather than measured, and it is recorded as chosen.</b> It is the number
    /// <see cref="MaxCursorLength"/> uses, for the same kind of reason: far past anything a real caller
    /// sends, and the length past which the string has stopped being plausibly the thing it claims to be. A
    /// search pattern longer than a keyset cursor is not a search.
    /// </para>
    /// </remarks>
    internal static int MaxPatternLength { get; } = MaxCursorLength;
```

and replace `ReadSelect`:

```csharp
        private void ReadSelect(string value)
        {
            if (value.Length == 0)
            {
                Add(QueryViolations.EmptySelect());
                return;
            }

            _claimedKeys = new Dictionary<string, string>(StringComparer.Ordinal);

            var projected = new List<ProjectedField>();
            var entries = 0;
            foreach (var entry in value.AsSpan().Split(','))
            {
                if (++entries > MaxSelectEntries)
                {
                    Add(QueryViolations.TooManySelectEntries(MaxSelectEntries));
                    return;
                }

                if (!TryAddProjectedField(value[entry], projected))
                {
                    return;
                }
            }

            _select = projected;
        }
```

- [ ] **Step 7: Bound the one channel that never splits — a `like`/`ilike` pattern**

`FilterTermParser.TryReadPattern(string operand, …)` is the single place both pattern operators pass
through. Add the guard at its head:

```csharp
    private static bool TryReadPattern(string operand, out object? value, out AlvoViolation? violation)
    {
        if (operand.Length > QueryStringParser.MaxPatternLength)
        {
            value = null;
            violation = QueryViolations.PatternTooLong(QueryStringParser.MaxPatternLength);
            return false;
        }

        var read = FilterValueReader.TryReadPattern(operand, out var pattern, out violation);
        …
    }
```

Nothing else gains a length bound: the other operands are bound values whose total size the request body
already caps, and refusing a long `eq` operand would be a bound on the caller's *data* rather than on the
server's cost.

- [ ] **Step 8: Add the violations**

In `QueryViolations.cs`, beside `ProjectionTooWide`:

```csharp
    /// <summary>The refusal for a projection carrying more comma-separated entries than the parser reads.</summary>
    /// <remarks>
    /// A separate code from <see cref="ProjectionTooWide"/> because it has a different fix and a different
    /// cause: that one means "you asked for more keys than there are fields", this one means "you sent more
    /// entries than this API will read", and a caller who repeated one field ten thousand times has hit only
    /// the second. Charged while splitting rather than after, for the reason
    /// <c>FilterParseScope</c>'s node budget is: a budget spent after the list is built does not bound it.
    /// </remarks>
    /// <param name="maxEntries">The most entries the parser reads.</param>
    internal static AlvoViolation TooManySelectEntries(int maxEntries) => new(
        ReservedQueryKeys.Select,
        "too-many-select-entries",
        "The projection carries more comma-separated entries than this API reads.",
        $"List at most {maxEntries} entries. A repeated entry answers under one key, so naming one field "
        + "many times returns the same value once and costs a parse each time.");

    /// <summary>The refusal for a <c>like</c>/<c>ilike</c> pattern longer than this API passes to an engine.</summary>
    /// <remarks>
    /// Its own code rather than <see cref="UnrepresentableValue"/>'s, because nothing is wrong with the
    /// <em>value</em>: it is a perfectly representable string, and what is refused is the cost of matching
    /// it against every row. A caller told their value was unrepresentable would go looking for a type
    /// mistake.
    /// </remarks>
    /// <param name="maxLength">The longest pattern this API passes through.</param>
    internal static AlvoViolation PatternTooLong(int maxLength) => new(
        FilterPointer,
        "pattern-too-long",
        "A 'like' or 'ilike' pattern is longer than this API matches.",
        $"Send a pattern of at most {maxLength} characters. Only the two pattern operators are bounded this "
        + "way: every other operand is compared rather than matched, so its cost is its size.");
```

- [ ] **Step 9: Run the tests**

Run: `scripts/test-ring0`
Expected: `[ring0] OK`, with the five new facts passing and `QueryStringParserPropertyTests` still green.

- [ ] **Step 10: Commit**

```bash
git add src/MMLib.Alvo/Api/Internal/ test/MMLib.Alvo.Api.Tests/QueryStringParserTests.cs
git commit -m "fix(api): spend the list bounds while splitting, and bound the one channel that never splits

A group's members, an in list's candidates and a projection's entries were
all materialised in full and refused afterwards; the only thing bounding
them was the URL length, which the query body removes. Two of the three
reach the refusal they already had; select gains an entry bound, because a
repeated entry claims no key and can never trip the width bound.

A like/ilike pattern never splits at all and had no bound but the request
line either. It is matched per row rather than compared, so it is the one
operand whose cost is not its size, and it is the one that gains a length.

Claude-Session: https://claude.ai/code/session_01Uh7NkobnQZy5fDftEZbVLp"
```

---

### Task 4: `QueryBodyReader` — the transposition

**Files:**
- Create: `src/MMLib.Alvo/Api/Internal/QueryBodyReader.cs`
- Modify: `src/MMLib.Alvo/Api/Internal/QueryViolations.cs` (`Body`, `UnrepresentableQueryValue`)
- Modify: `src/MMLib.Alvo/Api/AlvoViolation.cs` (the `pointer` parameter's documentation only)
- Test: `test/MMLib.Alvo.Api.Tests/QueryBodyReaderTests.cs` (create)

**Interfaces:**
- Consumes: `BoundedJsonBody.ReadAsync`, `BoundedJsonBody.CodeOf`, `BodyRefusal` (Task 1).
- Produces:
  - `internal static Task<QueryBodyReader.Result> ReadAsync(HttpRequest request, AlvoApiOptions options, CancellationToken cancellationToken)`
  - `internal sealed record QueryBodyReader.Result(IQueryCollection? Parameters, IReadOnlyList<AlvoViolation> Violations)`
  - `internal static AlvoViolation QueryViolations.Body(BodyRefusal refusal, AlvoApiOptions options)`
  - `internal static AlvoViolation QueryViolations.UnrepresentableQueryValue(string pointer)`

- [ ] **Step 1: Write the failing tests**

Create `test/MMLib.Alvo.Api.Tests/QueryBodyReaderTests.cs`:

```csharp
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.WebUtilities;
using MMLib.Alvo.Api.Internal;
using System.Text;

namespace MMLib.Alvo.Api.Tests;

/// <summary>
/// The transposition, on its own. What is under test here is that a JSON object becomes the
/// <em>same collection</em> ASP.NET Core would have handed the parser — not that the grammar works, which
/// is <c>QueryStringParserTests</c>' subject and is deliberately not re-asserted against a second surface.
/// </summary>
public sealed class QueryBodyReaderTests
{
    private static readonly AlvoApiOptions _options = new();

    /// <summary>
    /// The corpus fact, and the reason it is the only equivalence claim worth making: for every query
    /// string the parser's own suite drives, the JSON transposition of the collection ASP.NET Core parses
    /// it into reads back as that same collection. Transposing the <em>parsed</em> collection rather than
    /// the raw text is what makes the claim about decoded values true by construction — a body carries
    /// values, a query string carries their percent-encoding.
    /// </summary>
    [Theory]
    [InlineData("year=gte.2020")]
    [InlineData("make=in.(skoda,vw)")]
    [InlineData("or=(color.eq.red,color.eq.blue)")]
    [InlineData("not.color=eq.red")]
    [InlineData("year=gte.2020&year=lte.2024")]
    [InlineData("select=id,label:make&order=year.desc.nullsfirst&limit=10&offset=5")]
    [InlineData("make=like.sko%25")]
    [InlineData("notes=is.null")]
    public async Task A_transposed_query_string_reads_back_as_the_same_collection(string queryString)
    {
        var expected = new QueryCollection(QueryHelpers.ParseQuery(queryString));

        var actual = await ReadAsync(AsJson(expected));

        actual.Violations.ShouldBeEmpty();
        Rendered(actual.Parameters!).ShouldBe(Rendered(expected));
    }

    /// <summary>
    /// A number contributes the literal the caller wrote, so a decimal filter survives without a
    /// round trip through a CLR type and a format provider.
    /// </summary>
    [Fact]
    public async Task A_json_number_contributes_its_raw_text()
    {
        var read = await ReadAsync("""{"limit":100,"price":"lt.1500.50"}""");

        read.Parameters!["limit"].ToString().ShouldBe("100");
        read.Parameters["price"].ToString().ShouldBe("lt.1500.50");
    }

    /// <summary>An array is a repeated parameter, which is how a caller writes two groups.</summary>
    [Fact]
    public async Task An_array_is_the_same_parameter_twice()
    {
        var read = await ReadAsync("""{"or":["(a.eq.1)","(b.eq.2)"]}""");

        read.Parameters!["or"].Count.ShouldBe(2);
    }

    /// <summary>
    /// Keys are compared the way <c>QueryCollection</c> compares them, so two names differing only in case
    /// are one parameter sent twice — the refusal the query string already gives.
    /// </summary>
    [Fact]
    public async Task Two_names_differing_only_in_case_are_one_parameter_sent_twice()
    {
        var read = await ReadAsync("""{"limit":1,"LIMIT":2}""");

        read.Violations.ShouldBeEmpty();
        read.Parameters!.Count.ShouldBe(1);
        read.Parameters["limit"].Count.ShouldBe(2);
    }

    /// <summary>
    /// A value that is not a scalar names no parameter value, and the refusal points at the parameter's
    /// role — never at the caller's own key, which on this surface would answer "does this entity have a
    /// field called X".
    /// </summary>
    [Theory]
    [InlineData("""{"year":null}""", "filter")]
    [InlineData("""{"year":{"gte":2020}}""", "filter")]
    [InlineData("""{"year":[["nested"]]}""", "filter")]
    [InlineData("""{"or":[]}""", "filter")]
    [InlineData("""{"limit":null}""", "limit")]
    [InlineData("""{"select":{}}""", "select")]
    public async Task A_value_that_is_not_a_scalar_is_refused_at_the_parameters_role(string body, string pointer)
    {
        var read = await ReadAsync(body);

        read.Parameters.ShouldBeNull();
        var violation = read.Violations.ShouldHaveSingleItem();
        violation.Code.ShouldBe("unrepresentable-query-value");
        violation.Pointer.ShouldBe(pointer);
    }

    /// <summary>
    /// A body-level refusal points at the body, carries the write path's stable code and the read path's
    /// prose — an agent told to "send only the fields you are changing" on a read is told to fix another
    /// operation.
    /// </summary>
    [Theory]
    [InlineData("[1,2,3]", "not-an-object")]
    [InlineData("{", "malformed-json")]
    [InlineData("""{"or":"(a.eq.1)","or":"(b.eq.2)"}""", "duplicate-field")]
    public async Task A_body_that_is_not_a_bindable_object_is_refused_with_the_reads_own_wording(
        string body, string code)
    {
        var read = await ReadAsync(body);

        read.Parameters.ShouldBeNull();
        var violation = read.Violations.ShouldHaveSingleItem();
        violation.Code.ShouldBe(code);
        violation.Pointer.ShouldBeEmpty();
        violation.FixSuggestion.ShouldNotContain("writ", Case.Insensitive);
    }

    /// <summary>An empty object is the empty query, not a refusal: every readable field, the default page.</summary>
    [Fact]
    public async Task An_empty_object_is_the_empty_query()
    {
        var read = await ReadAsync("{}");

        read.Violations.ShouldBeEmpty();
        read.Parameters!.Count.ShouldBe(0);
    }

    private static Task<QueryBodyReader.Result> ReadAsync(string body)
    {
        var context = new DefaultHttpContext();
        context.Request.Body = new MemoryStream(Encoding.UTF8.GetBytes(body));
        context.Request.ContentLength = context.Request.Body.Length;
        return QueryBodyReader.ReadAsync(context.Request, _options, TestContext.Current.CancellationToken);
    }

    private static string AsJson(IQueryCollection query) =>
        System.Text.Json.JsonSerializer.Serialize(
            query.ToDictionary(
                parameter => parameter.Key,
                parameter => parameter.Value.Select(value => value ?? string.Empty).ToArray(),
                StringComparer.Ordinal));

    private static string Rendered(IQueryCollection query) =>
        string.Join(
            "&",
            query
                .OrderBy(parameter => parameter.Key, StringComparer.Ordinal)
                .Select(parameter => $"{parameter.Key}={string.Join('|', parameter.Value.ToArray())}"));
}
```

- [ ] **Step 2: Run to verify they fail**

Run: `dotnet build MMLib.Alvo.slnx -c Debug`
Expected: FAIL — `QueryBodyReader` does not exist.

- [ ] **Step 3: Add the two violations**

In `QueryViolations.cs`:

```csharp
    /// <summary>The read path's wording for a body one of the shared bounds refused.</summary>
    /// <remarks>
    /// <b>The same stable code as the write path's, and deliberately not the same fix suggestion.</b>
    /// <see cref="PayloadViolations"/>' four bound refusals tell a caller to send fewer fields, to flatten a
    /// <c>json</c> field's value, or that a write payload is a flat map of declared fields — every one of
    /// which is advice about an operation this endpoint does not perform. A code keys on the kind of
    /// refusal; the sentence belongs to the surface.
    /// </remarks>
    /// <param name="refusal">The bound that stopped the body.</param>
    /// <param name="options">The options the bounds are published from.</param>
    internal static AlvoViolation Body(BodyRefusal refusal, AlvoApiOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        return new AlvoViolation(
            PayloadViolations.BodyPointer, BoundedJsonBody.CodeOf(refusal), BodyMessage(refusal, options),
            BodyFix(refusal, options));
    }

    private static string BodyMessage(BodyRefusal refusal, AlvoApiOptions options) => refusal switch
    {
        BodyRefusal.NotAnObject => "The query body must be a JSON object of query parameters.",
        BodyRefusal.MalformedJson => "The query body is not well-formed JSON.",
        BodyRefusal.TooLarge =>
            $"The query body is larger than {options.MaxRequestBodyBytes} bytes, the configured maximum.",
        BodyRefusal.TooDeep =>
            $"The query body nests deeper than {options.MaxPayloadDepth} levels, the configured maximum.",
        BodyRefusal.TooManyKeys =>
            $"The query body carries more than {options.MaxPayloadKeys} parameters, the configured maximum.",
        BodyRefusal.DuplicateName => "The query body names one parameter twice.",
        _ => throw new ArgumentOutOfRangeException(
            nameof(refusal), refusal, "Unmapped body refusal; give it the read path's wording here."),
    };

    private static string BodyFix(BodyRefusal refusal, AlvoApiOptions options) => refusal switch
    {
        BodyRefusal.NotAnObject =>
            "Send {\"<parameter>\":\"<operator>.<operand>\",…} — the same parameters the query string "
            + "carries. An empty object {} reads the first page with no filter.",
        BodyRefusal.MalformedJson =>
            "Check for an unterminated string, a trailing comma, or a truncated body.",
        BodyRefusal.TooLarge =>
            $"Narrow the query. {AlvoFilter.MaxInCandidates} 'in' candidates and {AlvoFilter.MaxTerms} "
            + "filter terms fit well inside the bound; split a larger read across requests.",
        BodyRefusal.TooDeep =>
            "A query body is one level deep, or two where a repeated parameter is an array of strings.",
        BodyRefusal.TooManyKeys =>
            $"Send at most {options.MaxPayloadKeys} parameters. Repeat one parameter as an array of "
            + "strings rather than spreading a filter across many.",
        BodyRefusal.DuplicateName =>
            "Send a repeated parameter once, as an array of strings: {\"or\":[\"(a.eq.1)\",\"(b.eq.2)\"]}. "
            + "Two members with one name have no defined order, so answering with either would be a guess.",
        _ => throw new ArgumentOutOfRangeException(
            nameof(refusal), refusal, "Unmapped body refusal; give it the read path's fix suggestion here."),
    };

    /// <summary>The refusal for a query parameter whose JSON value is not a value a query string could carry.</summary>
    /// <remarks>
    /// One code for null, an object, a nested array and an empty array alike: what they have in common is
    /// that none of them names a value, and distinguishing them would describe the caller's own body back
    /// to them for no fix they could not already make. The pointer is the parameter's <em>role</em>, so a
    /// filter on an undeclared field cannot be told from one on a declared field by where the refusal
    /// points.
    /// </remarks>
    /// <param name="pointer">The role of the parameter the value belongs to.</param>
    internal static AlvoViolation UnrepresentableQueryValue(string pointer) => new(
        pointer,
        "unrepresentable-query-value",
        "A query parameter's value is not a string, a number, a boolean, or a non-empty array of those.",
        "Write {\"year\":\"gte.2020\"} — a parameter's value is the text a query string would carry. "
        + "Repeat a parameter as an array of strings; null, an object and an empty array name no value.");
```

`PayloadViolations.BodyPointer` is already `internal`, so `QueryViolations` can reference it rather than
declaring a second empty-string constant.

**And state the disambiguation rule where a caller reads it.** This endpoint is the first whose `violations`
array can carry both conventions at once — `""` for the body and `filter`/`limit` for a parameter — so
`AlvoViolation`'s `pointer` parameter documentation gains the rule that resolves them, in one sentence: a
value that is empty or begins with `/` is an RFC 6901 pointer into the request body; any other value is the
role of a query parameter. XML doc only; no member moves and the public baseline does not.

- [ ] **Step 4: Create `QueryBodyReader.cs`**

```csharp
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Primitives;
using System.Text.Json;

namespace MMLib.Alvo.Api.Internal;

/// <summary>
/// Reads a <c>POST …/query</c> body — a JSON object whose members <b>are</b> the query-string parameters —
/// into the collection <see cref="QueryStringParser"/> takes, or into the violations that stopped it.
/// </summary>
/// <remarks>
/// <para>
/// <b>It transposes; it never interprets.</b> No field name is resolved, no operator is recognised and no
/// bound of the grammar's is applied here — all of that is the one parser, reached unmodified. A second
/// grammar for the body is how the two surfaces come to disagree, and this type exists precisely so there
/// is not one.
/// </para>
/// <para>
/// <b>Nothing is percent-decoded, and the two surfaces are therefore equal on <em>values</em> rather than
/// on bytes.</b> ASP.NET Core hands the parser query values it has already decoded, and a JSON string is
/// already decoded — so the body carries the operand and the query string carries its encoding. Three
/// consequences follow and all are intended: <c>+</c> is a space in a query string and a plus here;
/// <c>%25</c> is an escape there and a literal here; and a caller assembling a four-hundred-element
/// <c>in</c> list — the request this endpoint exists for — escapes nothing.
/// </para>
/// <para>
/// <b>Keys are compared as <see cref="QueryCollection"/> compares them</b>, ordinal-ignoring-case, and
/// repeated names accumulate exactly as <c>QueryHelpers.ParseQuery</c> accumulates them. So
/// <c>{"limit":1,"LIMIT":2}</c> is one parameter carrying two values and earns the same
/// <c>repeated-parameter</c> the query string earns — where an ordinal comparer would have made one
/// request answer two different refusals depending on which side it arrived on, which is the single
/// divergence this transposition could have introduced.
/// </para>
/// <para>
/// <b>Everything about the body's size and shape is decided before this type sees it</b>, by
/// <see cref="BoundedJsonBody"/> and under the same three payload bounds a write is read under. The
/// document is therefore known to be a bounded JSON object with no duplicate names by the time it is
/// parsed here, which is why the parse cannot fail.
/// </para>
/// </remarks>
internal static class QueryBodyReader
{
    /// <summary>What one query body produced.</summary>
    /// <param name="Parameters">The parameters to parse, or <see langword="null"/> when nothing was refused-free.</param>
    /// <param name="Violations">Every reason the body was refused; empty on success.</param>
    internal sealed record Result(IQueryCollection? Parameters, IReadOnlyList<AlvoViolation> Violations);

    /// <summary>Reads the request body into the parameters a list query is parsed from.</summary>
    /// <param name="request">The request whose body to read.</param>
    /// <param name="options">The payload bounds to enforce.</param>
    /// <param name="cancellationToken">A token to cancel the read.</param>
    internal static async Task<Result> ReadAsync(
        HttpRequest request, AlvoApiOptions options, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(options);

        using var body = new MemoryStream();
        var refusal = await BoundedJsonBody
            .ReadAsync(request, body, options, cancellationToken).ConfigureAwait(false);
        if (refusal is { } refused)
        {
            return new Result(null, [QueryViolations.Body(refused, options)]);
        }

        body.Position = 0;
        using var document = JsonDocument.Parse(
            body, new JsonDocumentOptions { MaxDepth = options.MaxPayloadDepth });

        return Transpose(document.RootElement);
    }

    private static Result Transpose(JsonElement root)
    {
        var parameters = new Dictionary<string, StringValues>(StringComparer.OrdinalIgnoreCase);
        var violations = new List<AlvoViolation>();
        foreach (var member in root.EnumerateObject())
        {
            Read(member, parameters, violations);
        }

        return violations.Count > 0
            ? new Result(null, violations)
            : new Result(new QueryCollection(parameters), []);
    }

    /// <summary>Reads one member — a single value, or an array standing for a repeated parameter.</summary>
    private static void Read(
        JsonProperty member, Dictionary<string, StringValues> parameters, List<AlvoViolation> violations)
    {
        if (member.Value.ValueKind != JsonValueKind.Array)
        {
            Append(member.Name, member.Value, parameters, violations);
            return;
        }

        if (member.Value.GetArrayLength() == 0)
        {
            violations.Add(QueryViolations.UnrepresentableQueryValue(RoleOf(member.Name)));
            return;
        }

        foreach (var element in member.Value.EnumerateArray())
        {
            Append(member.Name, element, parameters, violations);
        }
    }

    /// <summary>Adds one value to a parameter, accumulating a repeat the way a query string accumulates one.</summary>
    private static void Append(
        string name,
        JsonElement value,
        Dictionary<string, StringValues> parameters,
        List<AlvoViolation> violations)
    {
        if (Scalar(value) is not { } text)
        {
            violations.Add(QueryViolations.UnrepresentableQueryValue(RoleOf(name)));
            return;
        }

        parameters[name] = parameters.TryGetValue(name, out var existing)
            ? StringValues.Concat(existing, text)
            : new StringValues(text);
    }

    /// <summary>
    /// The text a query string would have carried for this value, or <see langword="null"/> when it carries
    /// none.
    /// </summary>
    /// <remarks>
    /// A number contributes <see cref="JsonElement.GetRawText"/> — the literal the caller wrote — rather
    /// than a re-rendered CLR value. Round-tripping through <see cref="decimal"/> or <see cref="double"/>
    /// would put a formatting decision between the two surfaces, and the parser reads every value as text
    /// anyway.
    /// </remarks>
    private static string? Scalar(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.String => value.GetString(),
        JsonValueKind.Number or JsonValueKind.True or JsonValueKind.False => value.GetRawText(),
        _ => null,
    };

    /// <summary>
    /// The role a refusal about this parameter points at: the reserved parameter's own name, or
    /// <c>filter</c> for everything else.
    /// </summary>
    /// <remarks>
    /// The same roles <see cref="QueryViolations"/> uses, and for its reason: in PostgREST's grammar a
    /// filter's parameter name <em>is</em> a field name, so a pointer carrying it would answer "does this
    /// entity have a field called X" for exactly the caller most likely to be asking. <c>or</c>, <c>and</c>
    /// and <c>not</c> are reserved and still point at <c>filter</c>, because they are filters.
    /// </remarks>
    private static string RoleOf(string name) => name switch
    {
        ReservedQueryKeys.Order or ReservedQueryKeys.Limit or ReservedQueryKeys.Offset
            or ReservedQueryKeys.After or ReservedQueryKeys.Select => name,
        _ => QueryViolations.FilterPointer,
    };
}
```

- [ ] **Step 5: Run the tests**

Run: `dotnet test --test-modules "test/MMLib.Alvo.Api.Tests/bin/Debug/*/MMLib.Alvo.Api.Tests.dll" --filter-class "*QueryBodyReaderTests" --root-directory .`
Expected: PASS, every fact.

- [ ] **Step 6: Run ring0 and commit**

```bash
scripts/test-ring0
git add src/MMLib.Alvo/Api/Internal/QueryBodyReader.cs \
        src/MMLib.Alvo/Api/Internal/QueryViolations.cs \
        test/MMLib.Alvo.Api.Tests/QueryBodyReaderTests.cs
git commit -m "feat(api): transpose a JSON query body into the collection the one parser takes

A member is a query parameter, an array is a repeated one, and a number
contributes the literal the caller wrote. Keys compare the way
QueryCollection compares them, so a repeated parameter earns the refusal it
earns in a query string rather than a different one.

Claude-Session: https://claude.ai/code/session_01Uh7NkobnQZy5fDftEZbVLp"
```

---

### Task 5: The endpoint

**Files:**
- Modify: `src/MMLib.Alvo/Api/Internal/DataApiEndpoints.cs` (`Map`, `MapList`, new `MapQuery`, new `PageAsync`)
- Test: `test/MMLib.Alvo.Api.Tests/DataApiRoutingTests.cs`, `test/MMLib.Alvo.Api.Tests/DataApiQueryTests.cs`, `test/MMLib.Alvo.Api.Tests/DataApiAuthTests.cs`, `test/MMLib.Alvo.Api.Tests/LazyRouteMaterialisationTests.cs`

**Interfaces:**
- Consumes: `QueryBodyReader.ReadAsync` (Task 4), `DataApiEndpointKind.Query` (Task 2).
- Produces: the route `POST {prefix}/{entity}/query`.

- [ ] **Step 1: Write the failing tests**

In `DataApiRoutingTests.cs`, rename and widen the route-table fact and fix the marker helper:

```csharp
    /// <summary>
    /// The whole route table, spelled out rather than derived from the code that builds it. The count is
    /// asserted too: a seventh route per entity, or a stray catch-all, has to fail something.
    /// </summary>
    [Fact]
    public async Task Every_entity_in_the_applied_schema_gets_six_routes()
    {
        await using var world = await AlvoApiWorld.VehicleRegistryAsync();

        var routes = world.Routes;

        foreach (var entity in _entities)
        {
            routes.ShouldContain($"GET /api/{entity}");
            routes.ShouldContain($"GET /api/{entity}/{{id:guid}}");
            routes.ShouldContain($"POST /api/{entity}");
            routes.ShouldContain($"POST /api/{entity}/query");
            routes.ShouldContain($"PATCH /api/{entity}/{{id:guid}}");
            routes.ShouldContain($"DELETE /api/{entity}/{{id:guid}}");
        }

        routes.Count.ShouldBe(
            _entities.Length * 6,
            $"exactly six routes per declared entity and nothing else: {string.Join(", ", routes)}");
    }

    /// <summary>
    /// A verb the query route does not answer is a 405 from routing itself, not a 404 and not a problem
    /// document: the path exists, and this is the one response on these paths that Alvo does not write.
    /// Asserted so the change from 404 is a recorded behaviour rather than a discovery.
    /// </summary>
    [Theory]
    [InlineData("GET")]
    [InlineData("PATCH")]
    [InlineData("DELETE")]
    public async Task A_verb_the_query_route_does_not_answer_is_a_405_from_routing(string method)
    {
        var reader = new TestApiKey("reader", ["authenticated"], ["*:read"]);
        await using var world = await AlvoApiWorld.VehicleRegistryAsync([reader]);

        using var response = await world.SendAsync(new HttpMethod(method), "/api/owners/query", reader);

        response.StatusCode.ShouldBe(HttpStatusCode.MethodNotAllowed);
        (await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken)).ShouldBeEmpty();
    }
```

and in the marker fact, `endpoints.Count.ShouldBe(_entities.Length * 6, …)` plus:

```csharp
    /// <summary>
    /// The operation a route's own shape implies, derived from the verb, whether the pattern addresses one
    /// row, and whether it is the body-shaped read — so a marker that says <c>List</c> on a <c>DELETE</c>
    /// fails rather than being taken at its word.
    /// </summary>
    private static DataOperation ExpectedOperation(RouteEndpoint endpoint)
    {
        var method = endpoint.Metadata.GetMetadata<IHttpMethodMetadata>()!.HttpMethods.Single();
        var pattern = endpoint.RoutePattern.RawText!;
        var addressesOneRow = pattern.EndsWith("{id:guid}", StringComparison.Ordinal);
        var isQueryByBody = pattern.EndsWith("/query", StringComparison.Ordinal);

        return method switch
        {
            "GET" when addressesOneRow => DataOperation.Get,
            "GET" => DataOperation.List,
            "POST" when isQueryByBody => DataOperation.List,
            "POST" => DataOperation.Create,
            "PATCH" => DataOperation.Update,
            "DELETE" => DataOperation.Delete,
            _ => throw new InvalidOperationException($"Unexpected generated route: {method} {pattern}"),
        };
    }
```

In `DataApiQueryTests.cs`, add two usings the file does not have —
`using Microsoft.AspNetCore.WebUtilities;` and `using System.Net.Http.Json;` — and add the end-to-end
equivalence and the issue's own case. **Every fact below uses `SeededAsync()`**, the file's own three-vehicle
seed: minting a fresh key over an unseeded world would leave each of them comparing two empty pages, which
is a fact that cannot fail.

```csharp
    /// <summary>
    /// The two surfaces are one read. Asserted on the bytes, for a query carrying a projection alias, a
    /// group, a sort and a page size — if these ever diverge, one of them has grown a grammar of its own.
    /// </summary>
    [Theory]
    [InlineData("make=eq.skoda&order=year.desc&limit=2")]
    [InlineData("select=id,label:make&order=year.asc")]
    [InlineData("or=(make.eq.skoda,make.eq.vw)&limit=3")]
    public async Task A_query_body_answers_exactly_what_the_same_query_string_answers(string queryString)
    {
        await using var world = await SeededAsync();

        using var byUrl = await world.SendAsync(HttpMethod.Get, $"/api/vehicles?{queryString}", _admin);
        using var byBody = await world.SendRawAsync(
            HttpMethod.Post, "/api/vehicles/query", _admin, content: QueryBody(queryString));

        byBody.StatusCode.ShouldBe(byUrl.StatusCode);
        (await byBody.Content.ReadAsStringAsync(TestContext.Current.CancellationToken))
            .ShouldBe(await byUrl.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
    }

    /// <summary>
    /// The request the endpoint exists for: a candidate list far past what a request line carries, sent as
    /// a body and answered with the rows it names.
    /// </summary>
    [Fact]
    public async Task A_candidate_list_past_any_request_line_limit_is_answered_through_the_body()
    {
        await using var world = await SeededAsync();

        using var seeded = await world.SendAsync(HttpMethod.Get, "/api/vehicles?limit=200", _admin);
        var wanted = (await seeded.ReadJsonObjectAsync())["items"]!.AsArray()
            .Select(row => row!["id"]!.GetValue<string>())
            .ToList();
        wanted.Count.ShouldBe(_fleet.Length, "or the fact below compares two empty pages");
        var padding = Enumerable.Range(0, 400).Select(_ => Guid.NewGuid().ToString());
        var candidates = string.Join(',', wanted.Concat(padding));

        using var response = await world.SendRawAsync(
            HttpMethod.Post,
            "/api/vehicles/query",
            _admin,
            content: JsonContent.Create(new Dictionary<string, string>
            {
                ["id"] = $"in.({candidates})",
                ["limit"] = "200",
            }));

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var body = await response.ReadJsonObjectAsync();
        body["items"]!.AsArray().Count.ShouldBe(wanted.Count);
    }

    /// <summary>
    /// A `Prefer: count` preference is a header, so it is unaffected by which side the parameters arrived
    /// on and is honoured here exactly as on the list.
    /// </summary>
    [Fact]
    public async Task The_query_body_honours_the_count_preference()
    {
        await using var world = await SeededAsync();

        using var response = await world.SendRawAsync(
            HttpMethod.Post,
            "/api/vehicles/query",
            _admin,
            content: QueryBody("limit=1"),
            headers: [new KeyValuePair<string, string>(PreferHeader.Name, "count=exact")]);

        response.Headers.GetValues(PreferHeader.AppliedName).ShouldContain("count=exact");
        (await response.ReadJsonObjectAsync())["count"]!.GetValue<long>().ShouldBeGreaterThan(1);
    }

    /// <summary>
    /// The mask really is threaded into the parser on this surface, and a masked field is refused exactly
    /// as an undeclared one is — byte for byte, on both surfaces, so neither the surface a caller picks nor
    /// the refusal they read can be used to tell "hidden from you" from "does not exist".
    /// </summary>
    /// <remarks>
    /// Over <c>masked-notes.alvo.json</c> and not the vehicle registry, which declares no hidden field
    /// anywhere: there <c>PolicyDecision.HiddenFields</c> is empty, both names are simply undeclared, and
    /// the fact would pass while exercising none of the mask threading it exists to hold.
    /// </remarks>
    [Fact]
    public async Task A_masked_field_is_refused_exactly_as_an_undeclared_one_on_both_surfaces()
    {
        var reader = new TestApiKey("reader", ["authenticated"], ["*:read"]);
        await using var world = await AlvoApiWorld.FromDescriptorAsync("masked-notes.alvo.json", [reader]);

        using var maskedByUrl = await world.SendAsync(HttpMethod.Get, "/api/notes?secret=eq.x", reader);
        using var maskedByBody = await world.SendRawAsync(
            HttpMethod.Post, "/api/notes/query", reader, content: QueryBody("secret=eq.x"));
        using var unknownByBody = await world.SendRawAsync(
            HttpMethod.Post, "/api/notes/query", reader, content: QueryBody("nosuchfield=eq.x"));

        var masked = await maskedByBody.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        maskedByBody.StatusCode.ShouldBe(maskedByUrl.StatusCode);
        masked.ShouldBe(await maskedByUrl.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        masked.ShouldBe(
            await unknownByBody.Content.ReadAsStringAsync(TestContext.Current.CancellationToken),
            "a masked field and an undeclared one must be one refusal, byte for byte");
    }

    /// <summary>
    /// A denied caller is refused <em>before</em> their body is read, not after — so an oversized body from
    /// one earns the 403 they were always going to get, never <c>body-too-large</c>. That is the fact
    /// separating "the decision precedes the read" from "the decision precedes the parse", and no
    /// database-statement assertion can see it: a refusal after buffering touches no database either.
    /// </summary>
    /// <remarks>
    /// <c>ledgers</c> configures no <c>list</c> rule at all, so default-deny refuses every reader outright —
    /// which is the only way to reach a genuinely denied decision, since a rule that excludes a caller
    /// compiles to an allow carrying a predicate that matches nothing.
    /// </remarks>
    [Fact]
    public async Task A_denied_caller_sending_an_oversized_body_is_refused_before_it_is_read()
    {
        var reader = new TestApiKey("reader", ["authenticated"], ["*:read"]);
        await using var world = await AlvoApiWorld.FromDescriptorAsync("masked-notes.alvo.json", [reader]);
        var oversized = new string('x', new AlvoApiOptions().MaxRequestBodyBytes + 1024);

        using var response = await world.SendRawAsync(
            HttpMethod.Post,
            "/api/ledgers/query",
            reader,
            content: new StringContent(
                $$"""{"title":"eq.{{oversized}}"}""", Encoding.UTF8, "application/json"));

        response.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
    }

    /// <summary>The transposition every end-to-end fact above sends: the parsed collection, as JSON.</summary>
    private static HttpContent QueryBody(string queryString) =>
        JsonContent.Create(
            new QueryCollection(QueryHelpers.ParseQuery(queryString)).ToDictionary(
                parameter => parameter.Key,
                parameter => parameter.Value.Select(value => value ?? string.Empty).ToArray(),
                StringComparer.Ordinal));
```

In `DataApiAuthTests.cs`, add the three gating facts on the new route, following the file's existing
per-verb pattern: a key whose scopes exclude the entity's read is 403; an anonymous caller is 403; and a
descriptor whose `list` is unconfigured is 403 for everybody. Each must call **`world.ClearStatements()`
before the request** — `Statements` is everything since the last clear and world startup runs the whole
code-first migration, so without it `ShouldBeEmpty` fails on DDL — and then assert
`world.Statements.ShouldBeEmpty()`, which is what proves nothing reached the store.

In `ConcurrencyTests.cs`, `Every_response_a_generated_endpoint_produces_is_no_store` enumerates its probes
explicitly in `Everything(owner)`, so the new route is otherwise unmeasured — §4 promises the same
`Cache-Control: no-store` and nothing would hold it. Add a `POST /api/owners/query` probe with a `{}` body,
expecting 200.

In `LazyRouteMaterialisationTests.cs`, change `private const int PathsPerEntity = 2;` to `3`, update the
constant's summary to name the third path, and extend the `foreach` that asserts the two known paths with
`paths.ShouldContain($"/api/{entity}/query");` — the count alone would pass with the wrong third path.

- [ ] **Step 2: Run to verify they fail**

Run: `scripts/test-ring0`
Expected: FAIL — no `POST /api/{entity}/query` route exists.

- [ ] **Step 3: Extract the page tail out of `MapList`**

In `DataApiEndpoints.cs`, replace `MapList`'s delegate body so both readers share one path:

```csharp
    private static void MapList(
        IEndpointRouteBuilder endpoints,
        EntitySchema entity,
        string pattern,
        AlvoApiOptions options,
        AlvoContextFilterFactory filters,
        AlvoDataApiConventions conventions) =>
        endpoints.MapGet(pattern, (
                    HttpContext http,
                    IAlvoData data,
                    IPolicyEngine policies,
                    IAlvoContextAccessor caller,
                    CancellationToken ct) =>
                ProblemResultFactory.GuardAsync(async () =>
                {
                    var context = Caller(caller);
                    var decision = EnsureOperationIsAllowed(
                        policies, entity.Name, DataApiEndpointKind.List.ToDataOperation(), context);

                    return await PageAsync(
                        http, data, entity, options, decision, http.Request.Query, context, ct)
                        .ConfigureAwait(false);
                }))
            .Protect(entity, DataApiEndpointKind.List, filters, conventions);

    /// <summary>
    /// Parses one set of list parameters and answers the page they describe — the whole of what the two
    /// collection reads have in common, which is everything after the parameters have been obtained.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>One method rather than two, and that is the design rather than an economy.</b> #107's binding
    /// constraint is that the body must not become a second grammar; a second copy of this tail would be a
    /// second place for the projection, the count preference and the envelope to be assembled, which is
    /// where the two surfaces would begin to differ without any test noticing.
    /// </para>
    /// <para>
    /// The parser is handed the caller's mask, which is why the decision is resolved by the caller and
    /// passed in: a filter over a hidden field must be refused exactly as one over an undeclared field is —
    /// see <see cref="EnsureOperationIsAllowed"/> for the oracle that closes.
    /// </para>
    /// </remarks>
    private static async Task<IResult> PageAsync(
        HttpContext http,
        IAlvoData data,
        EntitySchema entity,
        AlvoApiOptions options,
        PolicyDecision decision,
        IQueryCollection parameters,
        AlvoContext context,
        CancellationToken ct)
    {
        if (!QueryStringParser.TryParse(
                parameters, entity, decision.HiddenFields, options, out var request, out var violations))
        {
            return ProblemResultFactory.MalformedQuery(violations);
        }

        var counted = PreferHeader.Count(http.Request.Headers[PreferHeader.Name]);
        var query = request!.Query with { IncludeTotalCount = counted is not null };
        var page = await data.QueryAsync(query, context, ct).ConfigureAwait(false);
        ApplyCountPreference(http.Response, counted);
        return Json(DataApiPage.From(page, request.Select));
    }
```

- [ ] **Step 4: Add `MapQuery` and map it**

```csharp
    /// <summary>
    /// Maps the body-shaped collection read: the same parameters, the same parser and the same page, for a
    /// filter a request line cannot carry.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Gated as <c>list</c>, and it resolves that decision before reading a byte of the body</b> — the
    /// precedence a create keeps, for the same three reasons: a denied caller must be told they are denied
    /// rather than that their body is malformed; parsing up to
    /// <see cref="AlvoApiOptions.MaxRequestBodyBytes"/> for a caller who cannot succeed is the amplifier the
    /// payload bounds exist against; and the allow decision's <see cref="PolicyDecision.HiddenFields"/> is
    /// the mask the parser needs, so the resolve replaces the one the mask already required.
    /// </para>
    /// <para>
    /// <b>A POST that reads is not a cross-site vector here</b>, and the reason is worth stating so the
    /// absence of a token reads as a decision: a credential is presented in an explicit request header and
    /// never in a cookie, so a cross-site form POST carries none and is judged anonymous — which
    /// default-deny answers exactly as it answers any other credential-less caller.
    /// </para>
    /// <para>
    /// <c>Idempotency-Key</c> is accepted and ignored here. It is a read: no second row exists to prevent
    /// and nothing could be replayed. It is declared in the operation's own prose rather than left to be
    /// discovered, because <c>POST</c> is the verb that triggers the blanket-attach habit several SDKs have.
    /// </para>
    /// </remarks>
    private static void MapQuery(
        IEndpointRouteBuilder endpoints,
        EntitySchema entity,
        string pattern,
        AlvoApiOptions options,
        AlvoContextFilterFactory filters,
        AlvoDataApiConventions conventions) =>
        endpoints.MapPost(pattern, (
                    HttpContext http,
                    IAlvoData data,
                    IPolicyEngine policies,
                    IAlvoContextAccessor caller,
                    CancellationToken ct) =>
                ProblemResultFactory.GuardAsync(async () =>
                {
                    var context = Caller(caller);
                    var decision = EnsureOperationIsAllowed(
                        policies, entity.Name, DataApiEndpointKind.Query.ToDataOperation(), context);

                    var body = await QueryBodyReader.ReadAsync(http.Request, options, ct).ConfigureAwait(false);
                    if (body.Parameters is not { } parameters)
                    {
                        return ProblemResultFactory.MalformedQuery(body.Violations);
                    }

                    return await PageAsync(http, data, entity, options, decision, parameters, context, ct)
                        .ConfigureAwait(false);
                }))
            .Protect(entity, DataApiEndpointKind.Query, filters, conventions);
```

and in `Map`:

```csharp
        var collection = $"{prefix}/{entity.Name}";
        var item = $"{collection}/{{id:guid}}";
        var query = $"{collection}/query";

        MapList(endpoints, entity, collection, options, filters, conventions);
        MapQuery(endpoints, entity, query, options, filters, conventions);
        MapGet(endpoints, entity, item, filters, conventions);
        MapCreate(endpoints, entity, collection, options, filters, formats, conventions);
        MapUpdate(endpoints, entity, item, options, filters, formats, conventions);
        MapDelete(endpoints, entity, item, filters, conventions);
```

Update the type's `<remarks>`: it says "the five minimal-API delegates one entity gets, onto the five `IAlvoData` members" — it is now six delegates onto five members, and the paragraph should say which two share one.

- [ ] **Step 5: Run the tests**

Run: `scripts/test-ring0`
Expected: the new routing, query and auth facts pass. `OpenApiDocumentTests` will still fail — Task 6 owns that.

- [ ] **Step 6: Commit**

```bash
git add src/MMLib.Alvo/Api/Internal/DataApiEndpoints.cs test/MMLib.Alvo.Api.Tests/
git commit -m "feat(api): POST {prefix}/{entity}/query, the same read with its parameters in a body

Gated as list and resolved before the body is read. The two collection
reads share one tail, so the projection, the count preference and the
envelope are assembled in exactly one place.

Closes #107

Claude-Session: https://claude.ai/code/session_01Uh7NkobnQZy5fDftEZbVLp"
```

---

### Task 6: The published document

**Files:**
- Modify: `src/MMLib.Alvo/Api/Internal/DataApiDocumentation.cs` (`SummaryOf`, `DescriptionOf`, a `Query` prose member)
- Modify: `src/MMLib.Alvo/Api/Internal/DataApiParameters.cs` (`Names`, `HeaderNames`, new `QueryBody`)
- Modify: `src/MMLib.Alvo/Api/Internal/SchemaComponentBuilder.cs` (new `QueryId`)
- Modify: `src/MMLib.Alvo/Api/Internal/AlvoDocumentTransformer.cs` (`Describe`, `BodyComponent`, `RequestBody`)
- Test: `test/MMLib.Alvo.Api.Tests/OpenApiDocumentTests.cs`, `test/MMLib.Alvo.Api.Tests/OpenApiDocumentCostTests.cs`, and the `.verified.txt` snapshot

**Interfaces:**
- Consumes: `DataApiEndpointKind.Query` (Task 2).
- Produces: `internal static string SchemaComponentBuilder.QueryId(string entity)`, `internal static OpenApiSchema DataApiParameters.QueryBody(EntitySchema entity, IReadOnlySet<string> hidden, AlvoApiOptions options)`.

- [ ] **Step 1: Update the pinned counts and drive the new operation**

In `OpenApiDocumentTests.cs`:
- `private const int RoutesPerEntity = 5;` → `6`.
- `documented.Count.ShouldBe(55, "27 … and 28 …")` → `63`, with the reason text updated to name the four statuses the query operation adds per entity (200, 401, 403, 422).
- `refusals.Count.ShouldBe(44, "twenty-two per entity — three on a list and a read, …")` → `50`, reason updated.
- The new probes go in **`ProbesAsync`**, not in `ProvokeEveryStatusAsync` (which is a generic loop over
  whatever `ProbesAsync` returns). A probe's `Operation` string is keyed into `$"{entity}.{Operation} {Status}"`,
  so it must be `"query"` to match the `{entity}.query` operationId. Add to the returned collection
  expression, where `Gated` already emits the 401 and the 403:

  ```csharp
  .. Gated($"{collection}/query", "query", HttpMethod.Post, new JsonObject()),
  new("query", 200, HttpMethod.Post, $"{collection}/query", _admin, new JsonObject()),
  new("query", 422, HttpMethod.Post, $"{collection}/query", _admin, new JsonObject { ["nope"] = "eq.1" }),
  ```
- `The_count_preference_is_documented_on_the_list_and_nowhere_else` → rename to `…_on_the_two_collection_reads_and_nowhere_else` and assert `prefer` is referenced by exactly the `get` on the collection path and the `post` on the `/query` path.

In `OpenApiDocumentCostTests.cs`: `Operations(document).ShouldBe(entities.Count * 5, "five operations per entity…")` → `* 6`, prose updated, and the class summary at the top.

- [ ] **Step 2: Run to verify they fail**

Run: `dotnet test --test-modules "test/MMLib.Alvo.Api.Tests/bin/Debug/*/MMLib.Alvo.Api.Tests.dll" --filter-class "*OpenApiDocument*" --root-directory .`
Expected: FAIL — the query operation carries the list's summary and prose and no request body.

- [ ] **Step 3: Give the kind its own summary and prose**

In `DataApiDocumentation.cs`, `SummaryOf` gains:

```csharp
        DataApiEndpointKind.Query => $"Query '{entity}' rows through a request body",
```

and `DescriptionOf` gains `DataApiEndpointKind.Query => QueryByBody`, with:

```csharp
    /// <summary>
    /// The body-shaped collection read's prose: what it is for, what makes it the same read, and the two
    /// things a caller can only learn here — that a value is not percent-encoded, and that a key is
    /// accepted and does nothing.
    /// </summary>
    private static string QueryByBody =>
        "Reads a page of rows the caller's policy admits, taking the same parameters in a JSON request "
        + "body.\n\n"
        + "**It exists for one reason: a filter a request line cannot carry.** Alvo's own budgets are "
        + $"generous — {AlvoFilter.MaxTerms} filter terms and {AlvoFilter.MaxInCandidates} `in` candidates "
        + "— and a proxy's URL limit is reached first, so `?id=in.(…400 ids…)` is refused by an "
        + "intermediary with a 414 and no `violations` array. Sent as a body it is answered normally.\n\n"
        + "**The body is a JSON object whose members are the query parameters**, and the grammar inside "
        + "each value is exactly the one the query string carries: `{\"year\": \"gte.2020\", \"or\": "
        + "[\"(color.eq.red,color.eq.blue)\"], \"select\": \"id,label:make\", \"limit\": 50}`. A repeated "
        + "parameter is an array of strings; the same name twice in one object is refused, because JSON "
        + "leaves the order of two such members undefined. `{}` is the empty query.\n\n"
        + "**Values are not percent-encoded here, and that is the point.** A query string carries the "
        + "escaping of a value; a JSON string carries the value. So `{\"make\": \"like.100%\"}` is what "
        + "`?make=like.100%25` means, and `+` is a plus rather than a space. Everything else is identical: "
        + "the same parser, the same refusals, the same page envelope.\n\n"
        + "**This is a read and is gated as `list`.** A caller whose `list` is unconfigured is refused "
        + "here exactly as on the collection `GET`, before the body is read at all — so a 403 never "
        + "arrives dressed as a complaint about the body.\n\n"
        + "**`Idempotency-Key` is accepted and ignored.** There is nothing to make idempotent: no row is "
        + "written, so a retry costs a second read and nothing else. It is accepted rather than refused "
        + "because several SDKs attach it to every `POST`.\n\n"
        + Grammar;
```

`ResponsesFor` already answers `List`'s catalogue for `Query` from Task 2 and needs no change; the 200's description reads "A page of rows the caller's policy admits", which is true of both.

- [ ] **Step 4: Narrow the query operation's parameters**

In `DataApiParameters.cs`, `Names` and `HeaderNames` must answer for `Query` **without** the seven query parameters, which now live in the body:

```csharp
    private static IEnumerable<string> Names(DataApiEndpointKind kind, EntitySchema entity) =>
    [
        .. AddressesOneRow(kind) ? new[] { RowIdId } : [],
        .. entity.Tenancy == TenancyMode.Scoped ? new[] { TenantId } : [],
        .. HeaderNames(kind, entity),
        .. kind == DataApiEndpointKind.List
            ? new[] { SelectId, OrderId, LimitId, OffsetId, AfterId, OrId, AndId }
            : [],
    ];
```

and in `HeaderNames`, `DataApiEndpointKind.List or DataApiEndpointKind.Query => [PreferId]`. `For`'s filter-parameter arm stays `kind == DataApiEndpointKind.List`.

- [ ] **Step 5: Build the body component**

In `SchemaComponentBuilder.cs`, beside the other ids:

```csharp
    /// <summary>The component id of the body the collection query accepts.</summary>
    /// <param name="entity">The entity name.</param>
    internal static string QueryId(string entity) => entity + "Query";
```

In `DataApiParameters.cs`:

```csharp
    /// <summary>The body the collection query accepts: the list's own query parameters, as an object.</summary>
    /// <remarks>
    /// <para>
    /// <b>Derived from the same parameters the collection <c>GET</c> publishes</b>, filtered to the ones
    /// that live in the query string — the tenant and <c>Prefer</c> are headers on both operations and stay
    /// headers — and mapped from component id back to parameter name, since two of them are published under
    /// ids (<c>orGroup</c>, <c>andGroup</c>) that are not their spelling. One source, so the two surfaces
    /// cannot come to describe different parameters.
    /// </para>
    /// <para>
    /// <b>A field property carries the parameter's schema and not its description.</b> A filter's
    /// description is a per-field sentence, and copying every one of them here would put the same prose in
    /// the document twice per entity — the cost <see cref="DataApiHeaders"/> states its own "described
    /// once" rule against. The grammar is on the operation, exactly as the <c>not.</c> prefix already is.
    /// </para>
    /// <para>
    /// <b>Every property is <c>oneOf: [string, array of string]</c> except the five settings</b>, because a
    /// repeated <em>filter</em> conjoins while a repeated setting is refused — which is the same asymmetry
    /// the query string has, published rather than restated.
    /// </para>
    /// <para>
    /// <c>not</c> is not a property: it is only ever a prefix on another parameter's name, so there is no
    /// member for it to be. <c>additionalProperties</c> is deliberately not <c>false</c>, for the reason
    /// <see cref="SchemaComponentBuilder"/> gives for the write bodies: no other Alvo body component closes
    /// itself, and the rule is stated once, in prose, on the operation.
    /// </para>
    /// </remarks>
    /// <param name="entity">The entity being queried.</param>
    /// <param name="hidden">Every field carrying a <c>hidden</c> flag, which contributes no property.</param>
    /// <param name="options">The API options the paging bounds are published from.</param>
    internal static OpenApiSchema QueryBody(
        EntitySchema entity, IReadOnlySet<string> hidden, AlvoApiOptions options)
    {
        ArgumentNullException.ThrowIfNull(entity);
        ArgumentNullException.ThrowIfNull(hidden);
        ArgumentNullException.ThrowIfNull(options);

        var properties = new Dictionary<string, IOpenApiSchema>(StringComparer.Ordinal);
        foreach (var (name, schema, repeatable) in QueryProperties(options))
        {
            properties[name] = repeatable ? Repeatable(schema) : schema;
        }

        foreach (var field in entity.Fields.Where(field => !hidden.Contains(field.Name)))
        {
            properties[field.Name] = Repeatable(new OpenApiSchema { Type = JsonSchemaType.String });
        }

        return new OpenApiSchema
        {
            Type = JsonSchemaType.Object,
            Description =
                "The query parameters, as an object. A member's name is a parameter and its value is the "
                + "text the query string would carry; an array repeats the parameter. See the operation "
                + "description for the grammar and for what a field property accepts.",
            Properties = properties,
        };
    }

    /// <summary>The five settings and the two grouping keywords, as body properties.</summary>
    private static IEnumerable<(string Name, OpenApiSchema Schema, bool Repeatable)> QueryProperties(
        AlvoApiOptions options) =>
    [
        (ReservedQueryKeys.Select, SelectSchema, false),
        (ReservedQueryKeys.Order, OrderSchema, false),
        (ReservedQueryKeys.Limit, LimitSchema(options), false),
        (ReservedQueryKeys.Offset, OffsetSchema, false),
        (ReservedQueryKeys.After, AfterSchema, false),
        (ReservedQueryKeys.Or, TextSchema, true),
        (ReservedQueryKeys.And, TextSchema, true),
    ];

    /// <summary>A property that accepts one value or several, which is how a repeated parameter is written.</summary>
    private static OpenApiSchema Repeatable(OpenApiSchema single) => new()
    {
        OneOf = [single, new OpenApiSchema { Type = JsonSchemaType.Array, Items = single }],
    };
```

The five setting schemas must be **lifted out of the parameter definitions and shared**, rather than
copied — that is the whole of §6's "one source". `OpenApiParameter.Schema` is typed `IOpenApiSchema`, so
reading it back and casting would be both ugly and fragile. Instead, extract each parameter's schema into
a private factory and have the parameter read from it:

```csharp
    /// <summary>The value shape a plain text parameter takes, on either surface.</summary>
    private static OpenApiSchema TextSchema => new() { Type = JsonSchemaType.String };

    /// <summary>The projection's value shape.</summary>
    private static OpenApiSchema SelectSchema => TextSchema;

    /// <summary>The sort parameter's value shape.</summary>
    private static OpenApiSchema OrderSchema => TextSchema;

    /// <summary>The page size's value shape, carrying the host's configured bounds.</summary>
    /// <param name="options">The API options the bounds are published from.</param>
    private static OpenApiSchema LimitSchema(AlvoApiOptions options) => new()
    {
        Type = JsonSchemaType.Integer,
        Format = "int32",
        Minimum = "1",
        Maximum = Text(options.MaxPageSize),
        Default = JsonValue.Create(options.DefaultPageSize),
    };

    /// <summary>The row-skip parameter's value shape.</summary>
    private static OpenApiSchema OffsetSchema =>
        new() { Type = JsonSchemaType.Integer, Format = "int32", Minimum = "0" };

    /// <summary>The cursor's value shape, carrying the bound the parser enforces.</summary>
    private static OpenApiSchema AfterSchema => new()
    {
        Type = JsonSchemaType.String,
        MinLength = 1,
        MaxLength = QueryStringParser.MaxCursorLength,
    };
```

and change the five parameter definitions to `Schema = SelectSchema`, `Schema = OrderSchema`,
`Schema = LimitSchema(options)`, `Schema = OffsetSchema`, `Schema = AfterSchema`, and `Filter`/`Group`/
`IfMatch`/`IfNoneMatch`/`Prefer` to `Schema = TextSchema`, which is what they already are. `RowId` and
`Tenant` keep their own literal: both carry `Format = "uuid"` and are not bare strings. The document must
not move for any of them — check the snapshot after this step, before Task 6 Step 6's intended move.

In `AlvoDocumentTransformer.cs`, `Describe` is `private static` today and **drops `static`** so it can
reach the injected `options.Value`; its one call site is already inside the instance `TransformAsync`, so
nothing else moves. It then registers the component:

```csharp
    private void Describe(OpenApiDocument document, EntityView view)
    {
        new SchemaComponentBuilder(view.Schema, view.Hidden, view.ReadOnly).AddTo(document);
        document.AddComponent(
            SchemaComponentBuilder.QueryId(view.Schema.Name),
            DataApiParameters.QueryBody(view.Schema, view.Hidden, options.Value));
        Tag(document, view.Schema.Name).Description = view.Schema.Description;
    }
```

`BodyComponent` gains `DataApiEndpointKind.Query => SchemaComponentBuilder.QueryId(entity)`, and `RequestBody`'s description must stop saying "The row to write":

```csharp
    private static OpenApiRequestBody? RequestBody(
        DataApiOperationMetadata marker, EntitySchema entity, OpenApiDocument document) =>
        BodyComponent(marker.Kind, entity.Name) is not { } component
            ? null
            : new OpenApiRequestBody
            {
                Required = true,
                Description = BodyDescription(marker.Kind),
                Content = Json(new OpenApiSchemaReference(component, document)),
            };

    private static string BodyDescription(DataApiEndpointKind kind) => kind == DataApiEndpointKind.Query
        ? "The query parameters, as an object. An empty object reads the first page with no filter."
        : "The row to write, as the entity's declared fields.";
```

- [ ] **Step 6: Accept the snapshot**

Run: `dotnet test --test-modules "test/MMLib.Alvo.Api.Tests/bin/Debug/*/MMLib.Alvo.Api.Tests.dll" --filter-class "*OpenApiDocumentTests" --root-directory .`
Read the `.received.txt` diff **before** accepting it. It must contain exactly: one new path per entity with one `post` operation, one new `{entity}Query` schema component, and nothing else. Then move `.received.txt` over `.verified.txt`.

The Stop hook will require the `alvo-snapshot-judge` subagent for the moved baseline — dispatch it and act on its verdict.

- [ ] **Step 7: Run ring0 and commit**

```bash
scripts/test-ring0
git add src/MMLib.Alvo/Api/Internal/ test/MMLib.Alvo.Api.Tests/
git commit -m "feat(api): publish the query operation and the body it accepts

The body schema is derived from the same parameter definitions the
collection GET publishes, so the two surfaces cannot describe different
grammars; the per-field prose stays on the operation rather than being
copied per property.

Claude-Session: https://claude.ai/code/session_01Uh7NkobnQZy5fDftEZbVLp"
```

---

### Task 7: The architecture record

**Files:**
- Modify: `docs/architecture/data-api.md`

**Interfaces:**
- Consumes: everything above.
- Produces: nothing code-facing.

- [ ] **Step 1: Make the six edits**

1. **Every "five routes" site**, not only the two in this file. Grep the repo for `five routes`, `all five`
   and `five operations` and fix each: `docs/architecture/data-api.md:3`, `:577` and `:655`
   ("attached to all five"); `src/MMLib.Alvo/Api/Internal/DataApiEndpoints.cs` — both the type `<remarks>`
   *and* `Map`'s own `<summary>`; `src/MMLib.Alvo/Api/Internal/AlvoEndpointDataSource.cs`'s `Build`
   `<summary>`; and the doc comments in `test/MMLib.Alvo.Api.Tests/OpenApiDocumentTests.cs` and
   `OpenApiDocumentCostTests.cs`. (The source-file ones belong to Tasks 5 and 2 respectively; do them there
   and check here that none is left.)
2. **The URL grammar block** (§"The URL grammar, and the two allow-lists that bound it") gains `POST   {prefix}/{entity}/query` and a paragraph: the body is the parameters as an object, values are not percent-encoded, an array repeats a parameter, a duplicate name is refused, `{}` is the empty query, and it is gated as `list`.
3. **The budget table** — the "Request body" row stops being a write-only row ("1 MiB, depth 32, 512 keys —
   a write payload **or a query body**"), and gains two rows: the projection's entry bound (`select` entries,
   256, API) and the pattern bound (`like`/`ilike` pattern length, 512, API), each with one line on why the
   URL used to provide it.
4. **A new subsection under the URL grammar**, "Why a JSON object and not the query string in a `text/plain` body", recording the OData §11.2.6.1 deviation and the `x-www-form-urlencoded` rejection, in the design's §1.3 terms.
5. **The status/slug catalogue** — note that the six body-shape codes (`not-an-object`, `malformed-json`,
   `body-too-large`, `body-too-deep`, `body-too-many-fields`, `duplicate-field`) are now reachable under
   `malformed-query` as well as under `validation`, with the read's own fix suggestions; and add the three
   codes this PR mints: `unrepresentable-query-value`, `too-many-select-entries` and `pattern-too-long`.
7. **The 405, and one consequence of it that is not about status codes.** Record that `GET`/`PATCH`/`DELETE`
   on `{entity}/query` are now 405 from routing rather than 404, and that a **host convention keyed on the
   HTTP verb** — "POST means a write", a common shape for rate limiting or audit logging — now applies write
   shaping to a read, while a GET-keyed convention misses this route. A host that shapes by verb should
   switch to the operation marker, which is what it is for.
8. **A pointer-disambiguation sentence**, because this is the first endpoint whose `violations` array can
   mix both conventions: a `pointer` that is empty or begins with `/` is an RFC 6901 pointer into the body;
   anything else is the *role* of a query parameter. The same sentence goes on `AlvoViolation`'s own
   `pointer` parameter documentation (Task 4).
6. **"Alternatives rejected"** gains: a second JSON query DSL, `X-HTTP-Method-Override`, a
   `POST {entity}/{id}/query`, and `application/x-www-form-urlencoded`.

- [ ] **Step 2: Check the brief-freshness gate**

`data-api.md` is not one of the three files the gate watches (`alvo-specifikacia.md`, `baas-analyza.md`, `design-brief.en.md`), so no regeneration is needed. Confirm with `scripts/check-brief-freshness` if the pre-commit hook complains.

- [ ] **Step 3: Commit**

```bash
git add docs/architecture/data-api.md
git commit -m "docs(architecture): record the query route, its deviation from OData, and the entry bound

Claude-Session: https://claude.ai/code/session_01Uh7NkobnQZy5fDftEZbVLp"
```

---

## Before the PR

- [ ] `scripts/test-ring1` — green.
- [ ] `scripts/test-ring2` — green. A transient failure in `Api.Tests.Integration` is **not** assumed flaky: find the failing test by name and diagnose it.
- [ ] Reviewer subagents on a **frozen tree**: a C# reviewer (`csharp-reviewer`) and a security reviewer against the `alvo-security-core-review` checklist. Do not edit while they read.
- [ ] `alvo-plan-guard`.
- [ ] `alvo-pr-report`.
- [ ] `gh pr create`, body pointing at the report. **`Closes #107`** once — one issue, so the repeated-keyword trap does not apply.
