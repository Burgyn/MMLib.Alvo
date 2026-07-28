# F3 PR3 — HTTP Data API + OpenAPI — Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development
> (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use
> checkbox (`- [ ]`) syntax for tracking.

**Goal:** Expose the `IAlvoData` port PR2 proved as a generated minimal-API Data API — PostgREST-style
filtering, keyset + offset paging, schema-derived validation as RFC 7807, `Idempotency-Key`,
`ETag`/`If-Match` — and serve an OpenAPI 3.1 document generated from the routes actually mapped.

**Architecture:** One feature (`MMLib.Alvo.Api`) inside the core, registered explicitly through its own
`Setup.cs`, that reads `SchemaModel` and maps five minimal-API delegates per entity onto `IAlvoData`.
The HTTP layer owns three things the port deliberately does not: parsing (URL → `AlvoQuery`),
schema-derived validation (payload → violations), and rendering (record/exception → HTTP). Everything
authorization-shaped stays inside the port, where PR1's policy engine and PR2's `WHERE`-clause
enforcement already live — the API layer never re-implements a check and never bypasses one.

**Tech Stack:** net10.0 · minimal APIs (`Microsoft.AspNetCore.App` framework reference) ·
`Microsoft.AspNetCore.OpenApi` (OpenAPI 3.1 / JSON Schema draft 2020-12) · `System.Text.Json` ·
xUnit v3 on Microsoft.Testing.Platform · `Microsoft.AspNetCore.TestHost` · Shouldly · CsCheck ·
Verify · Testcontainers (PostgreSQL leg).

## Global Constraints

Every task's requirements implicitly include this section.

- **`MMLib.Alvo.Abstractions` gains no ASP.NET dependency.** Ports live there; nothing HTTP does.
  `#75`'s DoD states this explicitly and an architecture fact must hold it.
- **The core (`MMLib.Alvo`) may add exactly two dependencies:** `<FrameworkReference
  Include="Microsoft.AspNetCore.App" />` and `PackageReference Microsoft.AspNetCore.OpenApi`. No EF, no
  Npgsql, no other `MMLib.Alvo.*` beyond `Abstractions` — `SharedArchitectureRules.Core_depends_only_on_Abstractions`
  already enforces the ban and must stay green untouched.
- **Minimal API, never MVC** (§0 principle 8). Every endpoint is a delegate; no controllers, no
  `[ApiController]`, no model binder attributes beyond minimal-API parameter binding.
- **Scalar is NOT in this PR.** It lives in `MMLib.Alvo.Host` (PR4). This PR produces the document.
- **Problem-detail type URIs are `https://alvo.dev/errors/<kebab-slug>`** — one slug per failure class,
  listed in one place (`AlvoProblemTypes`), never inlined at a call site.
- **PostgREST operator names, exactly:** `eq neq gt gte lt lte like ilike in is`. The set is an
  allow-list; an unknown operator is a 422, never a fallback. No invented spellings.
- **Never concatenate a caller-supplied value into SQL** — the API layer builds `AlvoFilter` nodes and
  the port parameterizes. A caller-supplied *field name* is validated against `EntitySchema` before it
  reaches the port (which validates again — both layers, deliberately).
- **All violations, never the first** (§2.1, #19's DoD). A validation response lists every violation.
- **Framework table names come from `AlvoOptions.SchemaPrefix`** via a single naming authority, as
  `SystemSchemaInitializer.DescriptorVersionsTableName` already does.
- **Short, single-purpose methods** (~25-line ceiling) per `alvo-dotnet-conventions`; extract
  aggressively. Every public type and member carries XML documentation that says *why*, matching the
  density of the surrounding code.
- **`scripts/test-ring0` after every step; `scripts/test-ring1` when a task finishes.** ring0 must stay
  Docker-free — a new test project that needs a container must be named `*.Tests.Integration`.
- **A new test project must be registered in `MMLib.Alvo.slnx`** — `scripts/test-ring0` counts modules
  on disk against the projects the solution registers and fails on a mismatch.
- **A moved `*.verified.*` baseline blocks the turn** until `alvo-snapshot-judge` reviews it. Expected
  here for the OpenAPI document; it is not a nuisance, it is the gate.

## Sources this plan is built on — read them, do not re-derive them

- `docs/superpowers/specs/2026-07-25-f3-crud-vertical-slice-design.md` — **approved, source of
  truth.** Sections *Data API*, *Paging*, *Concurrency, validation, idempotency*, *Exposure and
  field-level behaviour*, *OpenAPI and Scalar*, *PR split*, *Deviations*, *Assumptions*. Do not
  contradict a decision recorded there.
- `docs/product/baas-analyza.md` §2.1 (must-contain list, "pozor na", numeric acceptance criteria)
  and §6 (published OpenAPI 3.1 + contract test, PAT scopes).
- `docs/architecture/data-path.md` — PR2's surviving record: statement shape, reserved bind-parameter
  names, the four engine divergences, `StoredInstant`'s UTC normalization.
- `src/MMLib.Alvo.Abstractions/Data/IAlvoData.cs` — **the failure contract is settled**; this PR maps
  it to status codes and extends it by exactly two families (below).
- Issues `#19` (Data API scope + DoD), `#75` (OpenAPI DoD), `#90` (the precondition gap this PR closes).

## The three port widenings this PR takes, and why now

PR2 shipped `IAlvoData`; nothing is released, so a signature change costs a recompile of in-repo
callers and nothing else. All three are cheaper before PR3's HTTP layer exists than after.

1. **`QueryAsync` returns a page, not a list.** The next-page cursor cannot be produced by the API
   layer: `KeysetCursor` is `internal` to the EF package *on purpose* (`KeysetCursor`'s own remarks —
   "the encoding stays free to change because only this provider ever reads it"), and only the provider
   can answer "is there another page" without a second round trip. A layer that re-encoded the cursor
   would be the second copy of one fact, which is the defect class PR2's review closed four times.
2. **`AlvoQuery` gains `Offset`.** §2.1 requires *both* paging modes; the design says "offset as an
   opt-in with a server-enforced maximum page size". `AlvoQuery`'s own remarks license exactly this
   ("a new optional member … can be added here without breaking an existing caller").
3. **The write methods gain a precondition and an idempotency token** (`#90`). Both must be evaluated
   *inside* the write transaction — the precondition against PR2's row-locked pre-image, the
   idempotency record in the same commit as the row — so neither can live above the port. Doing it
   from the HTTP layer would be a lost update and a duplicate row respectively, each invisible to a
   test that does not look for it.

**Two new exception families** join the settled three, and `IAlvoData`'s remarks table must grow to
five rows in the same voice:

| Family | Means | Rendered |
|---|---|---|
| `AlvoPreconditionFailedException` | The caller's `If-Match` version does not match the row's current version, or the entity has no version source at all. | 412 |
| `AlvoIdempotencyConflictException` | The same `Idempotency-Key` was already used for a *different* request. | 409 |

Both are deliberate: a request layer has nothing but the exception type to map a status from, and
folding either into `ArgumentException` would render 422 for a condition that is neither malformed nor
the caller's mistake.

---

## Task 1: Read-side port widening — `AlvoPage`, `Offset`

**Files:**
- Create: `src/MMLib.Alvo.Abstractions/Data/AlvoPage.cs`
- Modify: `src/MMLib.Alvo.Abstractions/Data/AlvoQuery.cs` (add `Offset`, extend remarks)
- Modify: `src/MMLib.Alvo.Abstractions/Data/IAlvoData.cs` (`QueryAsync` return type + remarks)
- Modify: `src/MMLib.Alvo.Data.EntityFrameworkCore/Internal/EfAlvoData.cs`,
  `Internal/ReadStatementComposer.cs`, `IAlvoSqlDialect.cs` (row-offset clause)
- Modify: `src/MMLib.Alvo.Data.Sqlite/`, `src/MMLib.Alvo.Data.PostgreSql/` dialects
- Modify: `src/MMLib.Alvo.Testing/Data/InMemoryAlvoData.cs`, `Data/AlvoDataOrderingTests.cs`,
  `Data/AlvoDataAdversarialTests.cs` (call sites + new facts)
- Modify: `src/MMLib.Alvo.Testing.EntityFrameworkCore/AlvoSqlDialectContractTests.cs`, `TSqlSqlDialect.cs`
- Test: the contract suites above (they run in `MMLib.Alvo.Tests`, `*.Data.Sqlite.Tests`,
  `*.Data.PostgreSql.Tests.Integration`)

**Interfaces:**
- Consumes: PR2's `ReadStatementComposer`, `KeysetCursor`, `IAlvoSqlDialect.RowLimitClause`.
- Produces:
  ```csharp
  /// <summary>One page of an <see cref="IAlvoData.QueryAsync"/> result.</summary>
  public sealed record AlvoPage
  {
      /// <summary>The rows in this page, in the query's sort order.</summary>
      public required IReadOnlyList<AlvoRecord> Items { get; init; }

      /// <summary>
      /// The opaque cursor that reads the page after this one, or <see langword="null"/> when this
      /// page is the last. Only the implementation that issued it may interpret it.
      /// </summary>
      public string? NextCursor { get; init; }

      /// <summary>
      /// The total number of rows matching the query, or <see langword="null"/> when the caller did
      /// not ask for one — which is always, in F3. Modelled now because §2.1 requires count to be an
      /// opt-in (`Prefer: count=exact`) and a page shape without it could not gain one additively.
      /// </summary>
      public long? TotalCount { get; init; }

      /// <summary>An empty page: no rows, no next cursor.</summary>
      public static AlvoPage Empty { get; } = new() { Items = [] };
  }
  ```
  and `public int? Offset { get; init; }` on `AlvoQuery`, plus
  `Task<AlvoPage> QueryAsync(AlvoQuery query, AlvoContext context, CancellationToken cancellationToken = default)`.

- [ ] **Step 1: Write the failing facts in the shared contract suite**

In `src/MMLib.Alvo.Testing/Data/AlvoDataOrderingTests.cs` (every `IAlvoData` implementation inherits
these), add:

```csharp
[Fact]
public async Task A_page_that_is_not_the_last_carries_a_cursor_and_the_last_one_does_not()
{
    var world = await SeededWorldAsync(rowCount: 5);

    var first = await world.Data.QueryAsync(
        new AlvoQuery { Entity = "notes", Sort = [new AlvoSort("title")], Limit = 2 }, world.Alice);
    first.Items.Count.ShouldBe(2);
    first.NextCursor.ShouldNotBeNull();

    var last = await world.Data.QueryAsync(
        new AlvoQuery { Entity = "notes", Sort = [new AlvoSort("title")], Limit = 10 }, world.Alice);
    last.Items.Count.ShouldBe(5);
    last.NextCursor.ShouldBeNull("a page that returned every remaining row has no next page");
}

[Fact]
public async Task Paging_the_whole_set_by_cursor_visits_every_row_exactly_once()
{
    var world = await SeededWorldAsync(rowCount: 7);
    var seen = new List<object?>();
    string? cursor = null;

    do
    {
        var page = await world.Data.QueryAsync(
            new AlvoQuery { Entity = "notes", Sort = [new AlvoSort("title")], Limit = 2, After = cursor },
            world.Alice);
        seen.AddRange(page.Items.Select(row => row["id"]));
        cursor = page.NextCursor;
    }
    while (cursor is not null);

    seen.Count.ShouldBe(7);
    seen.Distinct().Count().ShouldBe(7);
}

[Fact]
public async Task An_offset_page_skips_exactly_that_many_rows_of_the_same_order()
{
    var world = await SeededWorldAsync(rowCount: 5);
    var all = await world.Data.QueryAsync(
        new AlvoQuery { Entity = "notes", Sort = [new AlvoSort("title")] }, world.Alice);

    var skipped = await world.Data.QueryAsync(
        new AlvoQuery { Entity = "notes", Sort = [new AlvoSort("title")], Limit = 2, Offset = 2 },
        world.Alice);

    skipped.Items.Select(row => row["id"]).ShouldBe(all.Items.Skip(2).Take(2).Select(row => row["id"]));
}

[Fact]
public async Task A_query_asking_for_both_a_cursor_and_an_offset_is_refused_as_malformed()
{
    var world = await SeededWorldAsync(rowCount: 3);

    var refusal = await Should.ThrowAsync<ArgumentException>(() => world.Data.QueryAsync(
        new AlvoQuery { Entity = "notes", Sort = [new AlvoSort("title")], After = "x", Offset = 1 },
        world.Alice));

    refusal.Message.ShouldContain("offset");
}

[Fact]
public async Task A_negative_offset_is_refused_as_malformed()
{
    var world = await SeededWorldAsync(rowCount: 3);

    await Should.ThrowAsync<ArgumentException>(() => world.Data.QueryAsync(
        new AlvoQuery { Entity = "notes", Sort = [new AlvoSort("title")], Offset = -1 }, world.Alice));
}
```

Use the fixture helper the suite already has for a seeded world; if the existing helper cannot seed N
rows, extend it rather than hand-rolling a second seeding path.

`After` + `Offset` is refused rather than merged because the two express different anchors of the same
window and a caller who sent both does not know which one they meant — silently preferring one is how a
client ships a paging bug that only appears under concurrent writes.

- [ ] **Step 2: Run them and watch them fail to compile** (`AlvoPage` does not exist).

Run: `scripts/test-ring0` · Expected: compile error, `AlvoPage` not found.

- [ ] **Step 3: Add `AlvoPage` and `AlvoQuery.Offset`**

Write `AlvoPage.cs` exactly as in *Interfaces* above. Add to `AlvoQuery`:

```csharp
/// <summary>
/// Gets the number of leading rows to skip, or <see langword="null"/> for none. The opt-in second
/// paging mode §2.1 requires beside the keyset default: simple for a UI that shows page numbers,
/// and wrong for a large set — an offset shifts under concurrent writes and degenerates on a million
/// rows, which is why <see cref="After"/> is the default and this is not.
/// </summary>
/// <remarks>
/// Mutually exclusive with <see cref="After"/>: they anchor the same window two different ways, so a
/// query carrying both is refused as malformed rather than served by whichever the implementation
/// happens to check first.
/// </remarks>
public int? Offset { get; init; }
```

`IsPaged` must now read `Limit is not null || After is not null || Offset is not null`.

Add a public static guard beside `EnsureSortKeysCanBePaged`, so both shipped backends and F7's third
one inherit the rule rather than making a third copy:

```csharp
/// <summary>
/// Throws when <paramref name="query"/>'s paging window is self-contradictory or out of range —
/// a negative <see cref="Limit"/> or <see cref="Offset"/>, or both <see cref="After"/> and
/// <see cref="Offset"/> set at once.
/// </summary>
/// <exception cref="ArgumentException">The paging window is malformed.</exception>
public static void EnsurePagingWindowIsSane(AlvoQuery query)
```

Move PR2's existing `EnsureLimitIsSane` logic in `EfAlvoData` into it and call the guard from both
implementations; delete the private copy (verify by deletion — if the old method still has a call
site, the move is incomplete).

- [ ] **Step 4: Widen `QueryAsync` and both implementations**

`IAlvoData.QueryAsync` returns `Task<AlvoPage>`; extend its `<returns>` to say the page's cursor is
opaque and provider-issued, and that `TotalCount` is always `null` in F3.

`EfAlvoData.QueryAsync`:
- call `AlvoQuery.EnsurePagingWindowIsSane(query)` where `EnsureLimitIsSane` was;
- fetch `Limit + 1` rows when `Limit is not null` (over-fetch by one), return the first `Limit` and set
  `NextCursor = KeysetCursor.Encode(<id of the last returned row>)` only when the extra row came back;
- with no `Limit`, the whole visible set is returned and `NextCursor` is `null`;
- `Offset` renders through a new dialect member (Step 5). Return `AlvoPage.Empty` where the method
  previously returned `[]` for a cursor with no anchor.

The over-fetch is what makes `NextCursor` honest. Deriving it from `Items.Count == Limit` instead would
emit a cursor for an exactly-full last page, and the client's next request would return an empty page —
a bug that only shows when the row count is a multiple of the page size.

`InMemoryAlvoData.QueryAsync` mirrors the same semantics over its in-memory list (its cursor stays its
own concern; it may reuse the anchor-id encoding it already has).

- [ ] **Step 5: `IAlvoSqlDialect` learns the offset clause**

```csharp
/// <summary>
/// Renders the clause that skips <paramref name="rowOffsetParameterMarker"/> leading rows, e.g.
/// <c>OFFSET @alvo_offset</c>. Separate from <see cref="RowLimitClause"/> because T-SQL spells the
/// pair <c>OFFSET … ROWS FETCH NEXT … ROWS ONLY</c> and cannot render a limit without an offset —
/// a driver that needs to fuse them overrides both.
/// </summary>
string RowOffsetClause(string rowOffsetParameterMarker) => $"OFFSET {rowOffsetParameterMarker}";
```

A default interface member, like `RowLimitClause`, so no existing implementation breaks. Add the
matching fact to `AlvoSqlDialectContractTests` (a dialect renders an offset clause that names the
marker it was given) and let `TSqlSqlDialect` demonstrate the fused form — that fake exists precisely
so a third engine's shape is exercised before anyone writes the real driver.

Reserve the bind-parameter name in `PolicyParameterPrefix` alongside the existing reserved names, and
extend `data-path.md`'s reserved-name table. An offset marker colliding with a policy parameter is the
same silent-wrong-rows failure the `alvo_p` prefix decision was made to prevent.

- [ ] **Step 6: Fix every call site and run the rings**

Run: `scripts/test-ring0`, then `scripts/test-ring1` · Expected: green, and the five new facts pass on
the in-memory reference and on SQLite.

- [ ] **Step 7: Commit**

```bash
git add -A && git commit -m "feat(data): QueryAsync returns a page with an honest next cursor, and AlvoQuery gains Offset"
```

---

## Task 2: Write-side port widening — precondition + idempotency

**Files:**
- Create: `src/MMLib.Alvo.Abstractions/Data/AlvoPrecondition.cs`,
  `Data/AlvoIdempotency.cs`, `Data/AlvoPreconditionFailedException.cs`,
  `Data/AlvoIdempotencyConflictException.cs`
- Modify: `src/MMLib.Alvo.Abstractions/Data/IAlvoData.cs` (three signatures + the failure-contract table)
- Modify: `src/MMLib.Alvo.Abstractions/Schema/AlvoManagedColumns.cs` (a `VersionColumn`/`HasVersion` answer)
- Create: `src/MMLib.Alvo.Data.EntityFrameworkCore/Internal/IdempotencyTable.cs`
- Modify: `Internal/SystemSchemaInitializer.cs`, `Internal/EfAlvoData.cs`, `EfCoreSchemaIntrospector.cs`
  (exclude the new framework table, as it already excludes the versions table)
- Modify: `src/MMLib.Alvo.Testing/Data/InMemoryAlvoData.cs`, `Data/AlvoDataAdversarialTests.cs`
- Test: `src/MMLib.Alvo.Testing/Data/AlvoDataConcurrencyTests.cs` (new shared contract suite)

**Interfaces:**
- Consumes: PR2's row-locked pre-image read (`PreImageMutation`, `FOR NO KEY UPDATE` / `FOR UPDATE`),
  `StoredInstant`, `AlvoAuditStamp`.
- Produces:
  ```csharp
  /// <summary>
  /// The row version a caller believes it is changing — the port's optimistic-concurrency channel.
  /// </summary>
  /// <param name="Version">
  /// The row's <c>updated_at</c> instant as the caller last read it. Compared for equality against
  /// the row-locked pre-image inside the write transaction, so the comparison cannot race the write
  /// it guards.
  /// </param>
  public readonly record struct AlvoPrecondition(DateTimeOffset Version);

  /// <summary>
  /// A caller-supplied idempotency token: replaying the same key with the same request must return the
  /// first request's row and create nothing new.
  /// </summary>
  /// <param name="Key">The caller's key, verbatim.</param>
  /// <param name="Fingerprint">
  /// A hash of the request this key was first used for. A replay carrying the same key and a different
  /// fingerprint is a conflict, not a replay — the caller reused a key for a different request, and
  /// answering with the first result would silently discard the second one.
  /// </param>
  public readonly record struct AlvoIdempotency(string Key, string Fingerprint);
  ```
  ```csharp
  Task<AlvoRecord> CreateAsync(string entity, IReadOnlyDictionary<string, object?> values,
      AlvoContext context, AlvoIdempotency? idempotency = null, CancellationToken cancellationToken = default);

  Task<AlvoRecord> UpdateAsync(string entity, Guid id, IReadOnlyDictionary<string, object?> values,
      AlvoContext context, AlvoPrecondition? precondition = null, CancellationToken cancellationToken = default);

  Task DeleteAsync(string entity, Guid id, AlvoContext context,
      AlvoPrecondition? precondition = null, CancellationToken cancellationToken = default);
  ```
  Plus, on `AlvoManagedColumns`:
  ```csharp
  /// <summary>
  /// The column whose value versions a row for optimistic concurrency, or <see langword="null"/>
  /// when the entity has none. Only an audited entity has one: <c>updated_at</c> exists because
  /// <c>audit: true</c> asked for it, so a non-audited entity cannot answer "has this row changed"
  /// at all — and a request layer must refuse an <c>If-Match</c> against it rather than pretend.
  /// </summary>
  public static string? VersionColumn(EntitySchema entity)
  ```

- [ ] **Step 1: Write the failing contract suite**

New shared suite `src/MMLib.Alvo.Testing/Data/AlvoDataConcurrencyTests.cs`, inherited by every
implementation exactly as `AlvoDataAdversarialTests` is:

```csharp
[Fact]
public async Task An_update_whose_precondition_matches_the_stored_version_succeeds()
[Fact]
public async Task An_update_whose_precondition_is_stale_is_refused_and_changes_nothing()
[Fact]
public async Task A_delete_whose_precondition_is_stale_is_refused_and_the_row_survives()
[Fact]
public async Task A_precondition_against_an_entity_with_no_version_column_is_refused_not_ignored()
[Fact]
public async Task The_version_a_write_returns_is_the_one_a_following_precondition_accepts()
[Fact]
public async Task A_stale_precondition_is_refused_before_the_policy_check_reveals_anything()
[Fact]
public async Task Replaying_an_idempotency_key_with_the_same_fingerprint_returns_the_first_row()
[Fact]
public async Task Replaying_an_idempotency_key_returns_the_row_and_creates_no_second_one()
[Fact]
public async Task The_same_idempotency_key_with_a_different_fingerprint_is_a_conflict()
[Fact]
public async Task Two_concurrent_creates_with_one_idempotency_key_produce_exactly_one_row()
[Fact]
public async Task An_idempotency_key_is_scoped_to_its_tenant_so_one_tenant_cannot_replay_anothers()
```

Write each body against `IAlvoData` alone — the suite must not know which backend it runs on. Three of
these carry the load and must be written to *discriminate*, not merely pass:

- **`The_version_a_write_returns_is_the_one_a_following_precondition_accepts`** — read `updated_at` off
  the record `UpdateAsync` returned, feed it straight back as the next `AlvoPrecondition`, and require
  acceptance. This is the round-trip fact: PostgreSQL stores microseconds, SQLite stores text, and a
  version that does not survive its own round trip makes every `If-Match` fail with no diagnosis.
- **`A_stale_precondition_is_refused_before_the_policy_check_reveals_anything`** — a stale precondition
  against a row the caller's `USING` predicate excludes must still answer
  `AlvoRecordNotFoundException`, not `AlvoPreconditionFailedException`: the precondition must never
  become an oracle for a row's existence. Order the checks so invisibility wins.
- **`Two_concurrent_creates_with_one_idempotency_key_produce_exactly_one_row`** — run both creates
  concurrently (`Task.WhenAll`) and require exactly one row and two identical results. The unique
  index on the key is what makes this true; the test is what proves the loser is translated into a
  replay rather than surfacing a raw provider exception.

- [ ] **Step 2: Run and watch them fail** — Run: `scripts/test-ring0` · Expected: compile errors.

- [ ] **Step 3: Add the four Abstractions types and the `IAlvoData` remarks**

Write the types as in *Interfaces*. Extend `IAlvoData`'s failure-contract `<list>` from three rows to
five, in the existing voice, and add to the type remarks a paragraph stating:

- the precondition is compared **inside** the write transaction against the row-locked pre-image, so it
  cannot race the write it guards;
- an entity with no version column refuses a precondition (`AlvoPreconditionFailedException`) rather
  than ignoring it — a silently-ignored `If-Match` is a lost update the caller believes it prevented;
- invisibility outranks the precondition: a row the `USING` predicate excludes raises
  `AlvoRecordNotFoundException` whichever precondition was supplied.

- [ ] **Step 4: The EF idempotency table**

`IdempotencyTable.cs` owns the name and the DDL, mirroring `SystemSchemaInitializer`'s existing
authority pattern:

```csharp
internal static class IdempotencyTable
{
    /// <summary>The framework's idempotency table for a prefix, e.g. <c>alvo_idempotency</c>.</summary>
    internal static string NameFor(string schemaPrefix) => $"{schemaPrefix}_idempotency";
}
```

> **As built (#90, after two review rounds).** The DDL and the replay description below are the
> **shipped** ones, not the ones this plan first proposed — a later task reading this passage is reading a
> requirement, so the superseded shapes are gone rather than annotated. What changed and why is in
> `docs/architecture/data-path.md` under *The idempotency-record table*; the short version is that a
> tenant-only scope let two users in one tenant share a key space, and re-reading the recorded row under
> the **create** decision returned it whoever owned it.

DDL, owned by `IdempotencyTable.Ddl` (so the write path and `SystemSchemaInitializer.EnsureAsync` create
it from one string), written to be identical on the two shipped engines:

```sql
CREATE TABLE IF NOT EXISTS alvo_idempotency (
    idempotency_key TEXT NOT NULL,
    scope TEXT NOT NULL,
    fingerprint TEXT NOT NULL,
    row_id TEXT NOT NULL,
    created_at TEXT NOT NULL,
    PRIMARY KEY (idempotency_key, scope)
)
```

`scope` is part of the primary key, not a column beside it, and it carries the tenant **and the acting
user** — `AlvoIdempotency.IdentityOf(context)`, one authority on the port that both implementations call.
A key is the caller's own opaque string, so two clients collide on `"1"` across tenants and just as
easily within one; a key space shared between two users in one tenant is a row-level authorization
bypass, not a collision nuisance. The tenantless sentinel is the literal `global`, which no GUID text can
equal, so no non-empty guard on `TenantId` is needed. An **anonymous** caller has no identity to scope by
— every one carries the same reserved all-zero `UserId` — so a token from one is refused with an
`ArgumentException` (`AlvoIdempotency.EnsureIdentifiableCaller`); `AlvoContext.System`'s user id is a
distinct reserved value, so a system-context token stays legal.

There is **no `entity` column**: `AlvoIdempotency.Fingerprint` covers the entity by contract (an HTTP
fingerprint hashes method, path and body, and the path names the entity), so a matched fingerprint already
proves the replay is for the same entity, and the same key on a different entity is a 409. A caller whose
fingerprint does not distinguish the entity is fail-closed, never cross-entity: the recorded id is re-read
under the entity being served and is not there. Store `row_id` rather than a response body, so a replay
is a real read rather than a cache.

`idempotency_key` is not named `key`, because `KEY` is reserved in T-SQL. The portability claim is scoped
to SQLite and PostgreSQL; `TEXT` would need to become `nvarchar` on T-SQL, which is follow-up work for
whoever writes that driver.

The `CREATE TABLE IF NOT EXISTS` runs **outside** the write transaction. Inside it, the DDL serializes two
concurrent idempotent creates (PostgreSQL will not let two transactions create one table name at once), so
the primary key — the actual concurrency control — is never reached and the concurrency fact passes with
the `PRIMARY KEY` clause deleted.

`EfCoreSchemaIntrospector` must exclude this table and the descriptor-versions one, through
`SystemSchemaInitializer.FrameworkTableNames`; otherwise the runner's introspection fallback reports a
keyless table and the model build throws on **every first run**.

- [ ] **Step 5: Wire both implementations**

`EfAlvoData.CreateAsync` with an idempotency token, inside the existing transaction:

1. `SELECT fingerprint, row_id FROM alvo_idempotency WHERE idempotency_key = @key AND scope = @scope`;
2. found and fingerprints match (`AlvoIdempotency.Matches`, ordinal) → **resolve `get` for this caller**
   and re-read the recorded row under that decision, reading *and* masking through it, then return it (no
   insert). Never under the `create` decision this call arrived with: a create decision's `USING` is
   `null` by contract and renders as a constant true, so reading through it returns the row whoever owns
   it. Not visible under that decision → `AlvoRecordNotFoundException`; no `get` policy for this caller
   at all → `AlvoAuthorizationException`;
3. found and fingerprints differ → `AlvoIdempotencyConflictException`;
4. not found → insert the row, then insert the idempotency record in the same transaction; a failed write
   means a concurrent request may have won, so roll back and restart at (1) — bounded (ten attempts,
   ~450 ms), and on exhaustion an `InvalidOperationException` carrying the provider exception, because a
   raw `DbException` is outside the failure families `IAlvoData` promises.

`UpdateAsync`/`DeleteAsync`: after the pre-image is read under its lock and after the policy `USING`
check has decided visibility, compare the pre-image's version column to `precondition.Version`;
mismatch → `AlvoPreconditionFailedException`. An entity with no version column and a non-null
precondition → the same exception with a message pointing at `audit: true`.

`InMemoryAlvoData` implements the identical semantics over its dictionary, including the conflict and
the record's full identity scope — **(tenant, acting user)**, never the tenant alone. It is the reference
the shipped backends are held to, so it may not be laxer.

- [ ] **Step 6: Rings, then the PostgreSQL leg**

Run: `scripts/test-ring0`, `scripts/test-ring1`, then the integration project for the PostgreSQL leg
(`dotnet test test/MMLib.Alvo.Data.PostgreSql.Tests.Integration`) · Expected: the concurrency suite is
green on the reference, on SQLite and on real PostgreSQL. A row-lock behaviour that differs between
engines is exactly what this suite exists to surface.

- [ ] **Step 7: Commit**

```bash
git add -A && git commit -m "feat(data): the write path gains a precondition and an idempotency token (#90)"
```

---

## Task 3: The API feature — wiring, routes, auth

**Files:**
- Create: `src/MMLib.Alvo/Api/Setup.cs`, `Api/AlvoApiOptions.cs`, `Api/AlvoDataApiExtensions.cs`,
  `Api/Internal/DataApiEndpoints.cs`, `Api/Internal/AlvoContextFilter.cs`,
  `Api/Internal/EntityRouteCatalog.cs`
- Modify: `src/MMLib.Alvo/MMLib.Alvo.csproj` (framework reference + OpenApi package),
  `AlvoServiceCollectionExtensions.cs` / `Internal/AlvoBuilder.cs` (register the feature),
  `src/MMLib.Alvo/Auth/AlvoAuthOptions.cs` (`TenantHeaderName`)
- Modify: `Directory.Packages.props` (pin `Microsoft.AspNetCore.OpenApi`,
  `Microsoft.AspNetCore.TestHost`), `MMLib.Alvo.slnx`
- Create: `test/MMLib.Alvo.Api.Tests/` (project, `AlvoApiWorld.cs` fixture, `DataApiRoutingTests.cs`,
  `DataApiAuthTests.cs`)
- Modify: `test/MMLib.Alvo.Abstractions.Tests/ArchitectureTests.cs` (Abstractions has no ASP.NET
  reference — add the fact if it is not already there)

**Interfaces:**
- Produces:
  ```csharp
  /// <summary>Registers the generated Data API's services.</summary>
  public static IAlvoBuilder AddDataApi(this IAlvoBuilder builder, Action<AlvoApiOptions>? configure = null);

  /// <summary>Maps one minimal-API delegate per operation per entity in the applied schema.</summary>
  public static IEndpointRouteBuilder MapAlvoDataApi(this IEndpointRouteBuilder endpoints);
  ```
  ```csharp
  public sealed class AlvoApiOptions
  {
      /// <summary>The route prefix every generated endpoint sits under. Default <c>/api</c>.</summary>
      public string RoutePrefix { get; set; } = "/api";

      /// <summary>The page size used when a request names none. Default 50.</summary>
      public int DefaultPageSize { get; set; } = 50;

      /// <summary>
      /// The largest page a request may ask for. Default 200. Server-enforced rather than advisory:
      /// §2.1 requires a maximum, because an unbounded limit is a denial-of-service one query long.
      /// </summary>
      public int MaxPageSize { get; set; } = 200;
  }
  ```

- [ ] **Step 1: Write the failing routing and auth facts**

`test/MMLib.Alvo.Api.Tests` uses `Microsoft.AspNetCore.TestHost` over a `WebApplication` built in the
fixture — not `WebApplicationFactory`, which needs an entry-point assembly that does not exist until
PR4 — with SQLite (`Data Source=:memory:` held open for the fixture's lifetime) and the
`examples/vehicle-registry` descriptor applied.

```csharp
[Fact] public async Task Every_entity_in_the_applied_schema_gets_five_routes()
[Fact] public async Task An_entity_the_descriptor_does_not_declare_has_no_route_at_all()
[Fact] public async Task A_request_with_no_api_key_is_served_as_anonymous_and_denied_by_policy()
[Fact] public async Task A_request_with_an_unknown_api_key_is_401_not_403()
[Fact] public async Task A_request_with_a_revoked_api_key_is_401()
[Fact] public async Task A_key_whose_scope_excludes_the_entity_is_403_before_any_row_is_touched()
[Fact] public async Task A_read_scope_cannot_perform_a_write()
[Fact] public async Task A_tenant_scoped_entity_read_with_no_tenant_context_returns_no_rows_of_any_tenant()
[Fact] public async Task The_route_prefix_is_configurable_and_nothing_is_mapped_outside_it()
```

`A_request_with_no_api_key_is_served_as_anonymous_and_denied_by_policy` is the load-bearing one:
missing credentials are **not** 401. Alvo has a real `Role.Anon` and default-deny, so an anonymous
caller is a caller whose policy happens to permit nothing — 403 with a policy reason. 401 is reserved
for a key that was *presented* and is not usable (unknown, revoked, expired), which is a different
diagnosis and a different fix for the agent reading it.

`A_tenant_scoped_entity_read_with_no_tenant_context_returns_no_rows_of_any_tenant` is `[15a]`'s DoD
made true over HTTP; it must assert an empty result, not a status code, because "fails rather than
returning every tenant's rows" is a statement about rows.

- [ ] **Step 2: Run and watch them fail** — Run: `scripts/test-ring0` · Expected: project does not build.

- [ ] **Step 3: Core project gains ASP.NET**

```xml
<ItemGroup>
  <FrameworkReference Include="Microsoft.AspNetCore.App" />
</ItemGroup>
<ItemGroup>
  <!-- First-party ASP.NET Core tooling for a product promise: the OpenAPI document IS the contract an
       agent reads (§0 principle 4), and an embedded host wants its Alvo endpoints documented too.
       Scalar deliberately does NOT live here — picking a docs UI is a hosting decision, so it sits in
       MMLib.Alvo.Host (design: "OpenAPI and Scalar"). -->
  <PackageReference Include="Microsoft.AspNetCore.OpenApi" />
</ItemGroup>
```

Verify `SharedArchitectureRules.Core_depends_only_on_Abstractions` stays green with no edit — it bans
EF, Npgsql and sibling `MMLib.Alvo.*` assemblies, none of which this adds. If it needs an edit, stop
and report: that would mean the rule is broader than its documented intent.

- [ ] **Step 4: Route generation from the applied schema**

`EntityRouteCatalog` reads `ISchemaRegistry.GetSchema()` and yields one route group per entity;
`DataApiEndpoints` maps five delegates per group:

| Method | Route | Port call |
|---|---|---|
| GET | `{prefix}/{entity}` | `QueryAsync` |
| GET | `{prefix}/{entity}/{id:guid}` | `GetAsync` |
| POST | `{prefix}/{entity}` | `CreateAsync` |
| PATCH | `{prefix}/{entity}/{id:guid}` | `UpdateAsync` |
| DELETE | `{prefix}/{entity}/{id:guid}` | `DeleteAsync` |

Entity names are route *literals* taken from the schema, never a `{entity}` catch-all parameter: the
document must list real paths (`#75`'s "every mapped route appears"), and a catch-all would map a route
for an entity that does not exist and answer it with a 404 produced by the port instead of by routing.

PATCH, not PUT: `UpdateAsync` is partial by contract ("a field this dictionary does not mention keeps
its stored value"). PUT would promise whole-resource replacement, which the port does not do — see the
deferred-work list in Task 10.

`AlvoContextFilter` (an `IEndpointFilter`, so it is minimal-API-native and testable without a request
pipeline) resolves the principal via `IAlvoContextResolver`, using
`AlvoAuthOptions.HeaderName` for the key and the new `TenantHeaderName` (`X-Alvo-Tenant`) for the
requested tenant, publishes it to `IAlvoContextAccessor`, and applies `ScopeGate` for the operation the
endpoint represents. No key at all → `AlvoContext.Anonymous`.

- [ ] **Step 5: Rings and commit**

Run: `scripts/test-ring0`, `scripts/test-ring1`

```bash
git add -A && git commit -m "feat(api): generate minimal-API routes per entity, with API-key context and scope gating"
```

---

## Task 4: PostgREST query parsing

**Files:**
- Create: `src/MMLib.Alvo/Api/Internal/QueryStringParser.cs`, `Api/Internal/FilterTermParser.cs`,
  `Api/Internal/FilterValueReader.cs`, `Api/Internal/SortParser.cs`
- Test: `test/MMLib.Alvo.Api.Tests/QueryStringParserTests.cs`,
  `QueryStringParserPropertyTests.cs`, `QueryStringInjectionTests.cs`

**Interfaces:**
- Consumes: `EntitySchema`, `CelFieldType.Of(FieldSchema)` for the type a value must parse as,
  `AlvoFilter`/`AlvoComparison`/`AlvoAnd`/`AlvoOr`/`AlvoNot`, `AlvoFilter.MaxDepth`/`MaxTerms`/`MaxInCandidates`.
- Produces:
  ```csharp
  /// <summary>
  /// Parses a request's query string into an <see cref="AlvoQuery"/>, or into the violations that
  /// stopped it. Every field name and operator is checked against <paramref name="entity"/> here, so
  /// nothing unvalidated reaches the port — which validates again, deliberately.
  /// </summary>
  internal static bool TryParse(
      IQueryCollection query, EntitySchema entity, AlvoApiOptions options,
      out AlvoQuery? parsed, out IReadOnlyList<AlvoViolation> violations);
  ```

**Grammar (PostgREST, no invented spellings):**

```
?<field>=<op>.<value>            year=gte.2020, color=eq.red, notes=is.null
?<field>=in.(<v>,<v>,…)          make=in.(skoda,vw)
?or=(<term>,<term>,…)            or=(color.eq.red,color.eq.blue)
?and=(<term>,…)                  and=(year.gte.2020,year.lte.2024)
  nested:                        or=(year.eq.2020,and=(make.eq.vw,year.gte.2015))
?not.<field>=<op>.<value>        not.color=eq.red
  and inside a group:            or=(not.color.eq.red,year.eq.2020)
?order=<field>[.asc|.desc][.nullsfirst|.nullslast][,<field>…]
?limit=<n>&offset=<n>&after=<cursor>
?select=<field>[,<field>…]
```

- [ ] **Step 1: Write the failing table-driven tests**

```csharp
[Theory]
[InlineData("year=gte.2020", "year >= 2020")]
[InlineData("color=eq.red", "color == red")]
[InlineData("notes=is.null", "notes IS null")]
[InlineData("make=in.(skoda,vw)", "make IN [skoda, vw]")]
[InlineData("or=(color.eq.red,color.eq.blue)", "(color == red OR color == blue)")]
[InlineData("and=(year.gte.2020,year.lte.2024)", "(year >= 2020 AND year <= 2024)")]
[InlineData("or=(year.eq.2020,and=(make.eq.vw,year.gte.2015))",
    "(year == 2020 OR (make == vw AND year >= 2015))")]
[InlineData("not.color=eq.red", "NOT color == red")]
public void A_query_string_parses_to_the_expected_filter_tree(string queryString, string expectedTree)
```

Render the parsed tree with a small test-local formatter so the expectation is readable and a wrong
*shape* (not merely a wrong value) fails. Then the refusals, each asserting the *violation* and not
just "false":

```csharp
[Theory]
[InlineData("nosuchfield=eq.1")]          // unknown field
[InlineData("year=nosuchop.1")]           // operator off the allow-list
[InlineData("year=gte.notanumber")]       // value the field's type cannot hold
[InlineData("year=gte.2020.5")]           // fractional bound on an integral field
[InlineData("notes=is.hello")]            // is-operand that is not null/true/false
[InlineData("make=in.skoda")]             // in without a list
[InlineData("or=(")]                      // unbalanced group
[InlineData("or=()")]                     // empty group
[InlineData("limit=0")]
[InlineData("limit=-1")]
[InlineData("limit=100000")]              // past MaxPageSize
[InlineData("offset=-1")]
[InlineData("after=abc&offset=1")]
[InlineData("order=year.sideways")]
[InlineData("select=nosuchfield")]
public void A_malformed_query_string_is_refused_with_a_violation_naming_the_parameter(string queryString)
```

Plus the confidentiality facts, which must be indistinguishable from each other:

```csharp
[Fact] public void A_filter_over_a_hidden_field_is_refused_exactly_like_an_unknown_one()
[Fact] public void A_sort_over_a_hidden_field_is_refused_exactly_like_an_unknown_one()
[Fact] public void A_select_naming_a_hidden_field_is_refused_exactly_like_an_unknown_one()
```

The refusals must not echo the offending field name: it is attacker-controlled text, and a message
naming it answers "does this entity have a field called X?" one request at a time. Assert the two
messages are *equal*, not merely both non-empty — that is what makes the indistinguishability real
rather than aspirational.

- [ ] **Step 2: Write the property and injection tests**

```csharp
[Fact]
public void No_query_string_makes_the_parser_throw()
{
    Gen.Char.AlphaNumeric.Or(Gen.Const('.')).Or(Gen.Const('(')) /* … plus =,&,',",%,\ */
        .Array[0, 200].Select(chars => new string(chars))
        .Sample(candidate =>
        {
            // Either it parses or it reports violations. It never throws, and never returns a filter
            // past the port's own limits.
        }, iter: 10_000);
}

[Theory]
[MemberData(nameof(EveryOperator))]
public async Task Injection_through_every_operator_changes_no_row_and_leaks_no_error(string @operator)
```

The injection theory runs over the *live API* (not the parser alone) with the classic payloads —
`' OR 1=1 --`, `'; DROP TABLE vehicles; --`, `%27`, a NUL, RTL and combining Unicode — once per
operator, and asserts: the response is 200 with a policy-consistent row set or 422, never 500; the row
count of the table is unchanged; and no response body contains `SELECT`, `WHERE`, or the engine's
error vocabulary. §2.1 names "injection cez každý operátor" as an acceptance criterion, so the theory
is generated from the operator enum — a hand-written list would silently miss the next operator added.

- [ ] **Step 3: Implement, smallest piece first**

Order: `FilterValueReader` (typed value parsing per `CelFieldType`) → `FilterTermParser` (one
`field.op.value` term, `not.` prefix) → group parsing (`or=`/`and=`, recursive, depth-capped *while
parsing*, not after) → `SortParser` → `QueryStringParser` (assembling `AlvoQuery`, applying
`DefaultPageSize`/`MaxPageSize`, `select`).

Cap depth *during* the descent, not by validating the finished tree: a 10 000-deep group must be
refused without a 10 000-deep recursion, or the parser is a stack-overflow away from a 500 and the
fuzz test will find it.

Unknown query-string keys are refused, not ignored (an ignored `oder=name` silently returns unsorted
data and the agent that sent it has no way to notice). Reserved keys — `order`, `limit`, `offset`,
`after`, `select`, `or`, `and` — cannot collide with a field name because a descriptor field is
lower-snake-case and validated at apply; assert that with a fact rather than assuming it.

- [ ] **Step 4: Rings and commit**

Run: `scripts/test-ring0`, `scripts/test-ring1`

```bash
git add -A && git commit -m "feat(api): parse the PostgREST filter, sort and paging surface into AlvoQuery"
```

---

## Task 5: Schema-derived validation and RFC 7807

**Files:**
- Create: `src/MMLib.Alvo/Api/AlvoViolation.cs`, `Api/AlvoProblemTypes.cs`,
  `Api/Internal/RecordValidator.cs`, `Api/Internal/FormatCatalog.cs`,
  `Api/Internal/ProblemResultFactory.cs`
- Test: `test/MMLib.Alvo.Api.Tests/ValidationTests.cs`, `ProblemDetailsTests.cs`

**Interfaces:**
- Produces:
  ```csharp
  /// <summary>One machine-readable reason a request was refused.</summary>
  /// <param name="Pointer">A JSON Pointer (RFC 6901) into the request body, or the query-string key.</param>
  /// <param name="Code">A stable kebab-case code, e.g. <c>required</c>, <c>max-length</c>, <c>unknown-field</c>.</param>
  /// <param name="Message">A human sentence.</param>
  /// <param name="FixSuggestion">What to change — §0 principle 4 makes this part of the contract, not a nicety.</param>
  public sealed record AlvoViolation(string Pointer, string Code, string Message, string? FixSuggestion);
  ```

**The status-code authority — one table, one place (`ProblemResultFactory`):**

| Condition | Status | `type` slug |
|---|---|---|
| Schema-derived validation failed | 422 | `validation` |
| Query string malformed | 422 | `malformed-query` |
| `ArgumentException` out of the port | 422 | `malformed-query` |
| `AlvoAuthorizationException` (policy refused) | 403 | `forbidden` |
| The API key's scope excludes this entity/operation | 403 | `out-of-scope` |
| `AlvoRecordNotFoundException` / `GetAsync` → null | 404 | `not-found` |
| `AlvoPreconditionFailedException` | 412 | `precondition-failed` |
| `AlvoIdempotencyConflictException` | 409 | `idempotency-conflict` |
| Presented API key unusable | 401 | `unauthenticated` |
| `InvalidOperationException` | 500 | `internal` |

**A slug keys on the refusal's *kind*, never on its *reason*.** RFC 9457 makes `type` the
machine-readable classification and `detail` explicitly unparseable prose, so a slug that encoded *why*
policy refused would become exactly the oracle the deny-reason wording is written to avoid. That is also
why `out-of-scope` is a legitimate second 403 while "row invisible to you" is not: a key's own scope is a
fact about the caller's credential, not about whether data exists. Task 3 left a fact asserting a
`detail` literal because status alone could not tell the two 403s apart — re-point it at the slug here,
and delete the literal assertion.

- [ ] **Step 1: Write the failing validation facts**

```csharp
[Fact] public async Task A_create_missing_two_required_fields_reports_both_not_just_the_first()
[Fact] public async Task A_string_past_its_max_length_is_a_violation_naming_the_limit()
[Fact] public async Task A_decimal_past_its_scale_is_a_violation()
[Fact] public async Task A_value_outside_an_enums_declared_values_is_a_violation_listing_them()
[Fact] public async Task A_value_failing_a_named_format_is_a_violation_naming_the_format()
[Fact] public async Task A_ref_field_pointing_at_a_row_that_does_not_exist_is_a_violation()
[Fact] public async Task A_ref_field_pointing_at_a_row_the_caller_cannot_see_is_the_same_violation()
[Fact] public async Task A_write_to_a_read_only_field_is_422_with_a_violation_not_a_silent_drop()
[Fact] public async Task A_payload_key_naming_no_field_is_a_violation_that_does_not_confirm_the_schema()
[Fact] public async Task A_body_that_is_not_a_json_object_is_422_and_not_500()
[Fact] public async Task Every_problem_response_carries_the_alvo_dev_type_uri_and_the_violations_array()
[Fact] public async Task A_problem_response_is_application_problem_json()
```

Two of these carry design weight:

- **`A_write_to_a_read_only_field_is_422_...`** resolves an apparent conflict between the design (a
  `readOnly` write "is rejected with 422") and PR2's port contract (the same write raises
  `AlvoAuthorizationException` → 403). Both are right in their own layer: validation runs *before* the
  port, so an HTTP caller gets 422 with a fix suggestion, and the port's 403 remains the backstop for a
  caller that bypasses the API. Assert both facts — the 422 here, and the port's 403 already asserted in
  the adversarial suite — so a later refactor that moves the check cannot silently swap them.
- **`A_ref_field_pointing_at_a_row_the_caller_cannot_see_is_the_same_violation`** — FK existence must be
  checked *through* the policy (`GetAsync`, which returns null for an invisible row), so "exists but you
  cannot see it" and "does not exist" produce one indistinguishable violation. Anything else is a
  cross-tenant existence oracle with a `201`/`422` shape.

- [ ] **Step 2: Run, watch fail, implement**

`RecordValidator` walks the entity's `FieldSchema` list and returns every violation. Order the checks so
one field can yield at most one violation (a null required field does not also report a type error).
FK existence goes last and only for fields that passed their type check — it costs a round trip per ref
field, so it must not run for input that was going to fail anyway.

`FormatCatalog` resolves the descriptor's `formats` plus a small built-in set (`email`, `phone`, `url`,
`uuid`) as anchored, timeout-bounded `Regex` instances compiled once per applied descriptor. A
caller-supplied value against an unanchored or catastrophic pattern is a ReDoS; use
`RegexOptions.NonBacktracking` where the pattern allows it and a `matchTimeout` where it does not, and
assert that with a fact carrying a pathological input.

`ProblemResultFactory` is the single mapping authority; the endpoints call it and never construct a
`ProblemDetails` themselves. Add a fact that every `AlvoProblemTypes` slug is reachable from at least
one endpoint path — a catalogue with an unreachable entry is documentation of a behaviour that does not
exist.

- [ ] **Step 3: Rings and commit**

```bash
git add -A && git commit -m "feat(api): schema-derived validation reported as RFC 7807 with every violation"
```

---

## Task 6: `ETag` and `If-Match`

**Files:**
- Create: `src/MMLib.Alvo/Api/Internal/RowVersionETag.cs`
- Modify: `Api/Internal/DataApiEndpoints.cs`
- Test: `test/MMLib.Alvo.Api.Tests/ConcurrencyTests.cs`

**Interfaces:**
- Produces:
  ```csharp
  /// <summary>
  /// The one place a row's <c>updated_at</c> becomes an HTTP entity tag and back.
  /// </summary>
  /// <remarks>
  /// <para>
  /// <b>Strong, and over the row version rather than the response bytes.</b> RFC 9110 §13.1.1 compares
  /// <c>If-Match</c> with the <em>strong</em> comparison function, so a weak tag would never match and
  /// the header would silently never protect anything. The cost, stated: two callers whose policies mask
  /// different fields share a tag for one row version even though their representations differ. That is
  /// tolerable because these responses are private and uncacheable by design (<c>Cache-Control:
  /// no-store</c>), and the tag exists for optimistic concurrency, not for a shared cache.
  /// </para>
  /// <para>
  /// <b>Encoded from a value that came out of the database, never from a clock.</b> The tag is the
  /// instant's <see cref="DateTimeOffset.UtcTicks"/> in invariant digits; PostgreSQL keeps microseconds
  /// and SQLite keeps text, so a tag minted from an in-memory instant would not survive its own round
  /// trip and every <c>If-Match</c> would fail with nothing to diagnose. Every write already re-reads
  /// the row (PR2), so a stored value is always at hand.
  /// </para>
  /// </remarks>
  internal static class RowVersionETag
  {
      internal static string? For(AlvoRecord record, EntitySchema entity);
      internal static bool TryParse(string? headerValue, out AlvoPrecondition precondition);
  }
  ```

- [ ] **Step 1: Write the failing facts**

```csharp
[Fact] public async Task A_get_of_an_audited_entity_carries_a_strong_etag()
[Fact] public async Task A_get_of_a_non_audited_entity_carries_no_etag_at_all()
[Fact] public async Task A_create_returns_201_with_a_location_header_and_an_etag()
[Fact] public async Task An_update_with_the_current_etag_succeeds_and_returns_the_new_one()
[Fact] public async Task An_update_with_a_stale_etag_is_412_and_changes_nothing()
[Fact] public async Task A_delete_with_a_stale_etag_is_412_and_the_row_survives()
[Fact] public async Task If_match_star_succeeds_when_the_row_exists()
[Fact] public async Task If_match_against_a_non_audited_entity_is_412_with_a_fix_suggestion()
[Fact] public async Task A_malformed_if_match_is_412_not_422_and_never_writes()
[Fact] public async Task If_none_match_with_the_current_etag_is_304_with_no_body()
[Fact] public async Task An_etag_from_a_get_is_accepted_verbatim_by_a_following_update()
[Fact] public async Task A_lost_update_is_prevented_when_two_callers_read_then_both_write()
```

`A_lost_update_is_prevented_when_two_callers_read_then_both_write` is the fact §2.1 actually asks for
("inak si klienti navzájom prepisujú dáta") — both callers GET, both PATCH with the tag they read, the
second must be 412. Without this test the whole mechanism can be present and inert.

`A_malformed_if_match_is_412_not_422`: a header that cannot possibly match must fail the precondition
rather than be reported as a malformed request, because the caller's intent — "only if unchanged" —
must never be reinterpreted as "unconditionally".

- [ ] **Step 2: Implement and run**

The endpoints read `If-Match` (`*` → "the row must exist", which the port's own not-found already
gives), parse it through `RowVersionETag`, pass an `AlvoPrecondition` down, and set `ETag` on every 200
and 201 of an audited entity. `Cache-Control: no-store` on every response, so no intermediary caches a
policy-masked representation for the next caller.

Run: `scripts/test-ring0`, `scripts/test-ring1`

- [ ] **Step 3: Commit**

```bash
git add -A && git commit -m "feat(api): ETag and If-Match over the row version, backed by the port's precondition"
```

---

## Task 7: `Idempotency-Key`

**Files:**
- Create: `src/MMLib.Alvo/Api/Internal/IdempotencyFingerprint.cs`
- Modify: `Api/Internal/DataApiEndpoints.cs`
- Test: `test/MMLib.Alvo.Api.Tests/IdempotencyTests.cs`

- [ ] **Step 1: Write the failing facts**

```csharp
[Fact] public async Task A_repeated_post_with_the_same_key_and_body_returns_the_first_result()
[Fact] public async Task A_repeated_post_with_the_same_key_creates_no_second_row()
[Fact] public async Task The_replayed_response_carries_the_same_id_and_the_same_etag()
[Fact] public async Task The_same_key_with_a_different_body_is_409_naming_the_conflict()
[Fact] public async Task The_same_key_on_a_different_entity_is_409_not_a_replay()
[Fact] public async Task A_key_longer_than_the_allowed_maximum_is_422()
[Fact] public async Task Two_tenants_may_use_the_same_key_without_colliding()
[Fact] public async Task Ten_concurrent_posts_with_one_key_create_exactly_one_row()
[Fact] public async Task A_post_without_the_header_is_not_deduplicated()
[Fact] public async Task Two_creates_differing_only_in_a_field_the_fingerprint_must_cover_are_a_conflict()
[Fact] public async Task An_anonymous_caller_sending_the_header_is_refused_with_a_fix_suggestion()
```

The last two exist because of what Task 2 settled below the port.

`Two_creates_differing_only_in_a_field_the_fingerprint_must_cover_are_a_conflict` is the one gap Task 2's
re-review could name and the port structurally cannot close: a fingerprint that omits the *entity* is
fail-closed (the port answers a permanent not-found), but a fingerprint too coarse **within** one entity
is **silently wrong** — the second, different request is answered with the first request's row and no
error anywhere. The HTTP layer computes the fingerprint, so this is the only layer that can hold the
guarantee. Write it so it fails if any body field is dropped from the digest: two payloads differing in
exactly one field, same key, must be a 409.

`An_anonymous_caller_sending_the_header_is_refused_with_a_fix_suggestion` pins the port's
`EnsureIdentifiableCaller` guard end to end. **It is a 422, not a 401, and that was decided rather than
inherited:** no credential was presented and rejected, so nothing failed authentication — the caller sent
a well-formed request asking for a facility that requires a stable identity to scope by, which is 422's
meaning and the port's existing malformed-request family. A 401 would also owe a `WWW-Authenticate`
challenge for a request that never attempted authentication, and would blur the anonymous-versus-401
line Task 3 kept deliberately disjoint.

- [ ] **Step 2: Implement**

`IdempotencyFingerprint` hashes method + route template + entity + the canonical JSON of the body
(SHA-256, hex). Canonical means the JSON is re-serialized from the parsed document with sorted property
names, so a reformatted-but-identical retry is a replay rather than a 409 — a retrying HTTP client is
not required to reproduce byte-identical whitespace. **Every field of the body is in the digest**; see
the coarseness fact above for why an omission here is a silent wrong answer rather than a refusal.

Cap the key length (`AlvoApiOptions.MaxIdempotencyKeyLength = 255`, matching the storage column) and
refuse a longer one with 422 rather than truncating: two keys that differ past the cut would become one.

`A_post_without_the_header_is_not_deduplicated` guards the inverse mistake — an implementation that
defaults the key to something derived from the body would deduplicate two legitimately identical
creates.

- [ ] **Step 3: Rings and commit**

```bash
git add -A && git commit -m "feat(api): Idempotency-Key replays the first result and never duplicates a row"
```

---

## Task 8: OpenAPI 3.1 document and transformer

**Files:**
- Create: `src/MMLib.Alvo/Api/Internal/AlvoDocumentTransformer.cs`,
  `Api/Internal/SchemaComponentBuilder.cs`
- Modify: `Api/Setup.cs` (`AddOpenApi` with the transformer), `Api/Internal/DataApiEndpoints.cs`
  (`.Produces<>()`/`.ProducesProblem()` metadata per route)
- Test: `test/MMLib.Alvo.Api.Tests/OpenApiDocumentTests.cs`,
  `OpenApiDocumentTests.The_document_is_stable.verified.txt`

- [ ] **Step 1: Write the failing facts**

```csharp
[Fact] public async Task The_document_declares_openapi_3_1()
[Fact] public async Task Every_mapped_route_appears_in_the_document_and_nothing_else_does()
[Fact] public async Task Every_documented_status_code_is_one_the_endpoint_can_actually_return()
[Fact] public async Task A_field_description_from_the_descriptor_reaches_the_schema()
[Fact] public async Task An_enum_fields_declared_values_reach_the_schema_as_an_enum()
[Fact] public async Task A_hidden_field_appears_in_no_schema_at_all()
[Fact] public async Task The_problem_details_shape_is_a_component_referenced_by_every_error_response()
[Fact] public async Task The_filter_sort_and_paging_parameters_are_documented_per_list_route()
[Fact] public Task The_document_is_stable() // Verify snapshot
```

`Every_mapped_route_appears_in_the_document_and_nothing_else_does` is the anti-drift fact §2.1 and §6
both demand: enumerate `EndpointDataSource` and compare the set against the document's paths in *both*
directions. A one-directional check passes a document that documents routes nobody mapped.

`A_hidden_field_appears_in_no_schema_at_all` is a confidentiality fact, not a tidiness one: a `hidden`
field's *name* in a public document is exactly the schema oracle the port's refusal messages are
worded to avoid.

- [ ] **Step 2: Implement the transformer**

`AlvoDocumentTransformer` is an `IOpenApiDocumentTransformer` that, per entity, builds the request and
response schemas from `EntitySchema` via `GetOrCreateSchemaAsync`/`AddComponent`: type, format,
`maxLength`, `enum`, `description`, `required`, and an example. Generated endpoints carry weakly-typed
payloads, so without this the document says `object` and documents nothing.

Keep .NET 10's OpenAPI 3.1 / draft 2020-12 default — the same draft as `schema/project.schema.json`.
Do not downgrade to 3.0 (`#75` says so explicitly).

- [ ] **Step 3: Snapshot, judge, commit**

The Verify baseline will move; the turn gate will block and ask for `alvo-snapshot-judge`. That is
expected — dispatch it and let it rule.

```bash
git add -A && git commit -m "feat(api): serve an OpenAPI 3.1 document enriched from the applied schema"
```

---

## Task 9: PostgreSQL leg and the performance criteria

**Files:**
- Create: `test/MMLib.Alvo.Api.Tests.Integration/` (project, `PostgresApiFixture.cs`,
  `DataApiOnPostgresTests.cs`, `PagingPerformanceTests.cs`)
- Modify: `MMLib.Alvo.slnx`, `.github/workflows/ci.yml` (the integration leg), `scripts/test-ring2`

- [ ] **Step 1: Run the API suite against real PostgreSQL**

#19's DoD is "tests green on SQLite + Postgres", so the API-level CRUD, filter, paging, ETag and
idempotency facts must run on both engines, not only the port-level suites. Extract the shared
assertions into a base class the SQLite and PostgreSQL fixtures both drive — one suite, two hosts,
mirroring how the data-path suites are already organized.

Build the container lazily inside `InitializeAsync`, never in a field initializer, and skip on Windows
— PR1 lost 28 tests to a Windows runner with no Docker daemon because `Build()` threw during fixture
construction, before any test reached its own skip.

- [ ] **Step 2: The two numeric criteria**

```csharp
[Fact] public async Task P95_of_a_filtered_list_over_100k_rows_on_an_indexed_column_is_under_50ms()
[Fact] public async Task Keyset_paging_stays_stable_over_a_million_rows()
```

Both numbers come from §2.1 verbatim. Seed through the data path in batches, not through the HTTP API
(seeding a million rows one request at a time is the test, not the setup). Measure the p95 over ≥200
requests after a warm-up, and report the measured value in the failure message — a threshold assertion
whose failure says only "false" cannot be acted on.

Keyset stability means: page the whole million with a fixed page size while a concurrent writer
inserts and updates rows, and assert no row is visited twice and no row present for the whole run is
missed. That is the property offset paging *fails* and keyset exists to provide, so a version that
merely pages a static table proves nothing.

If either test cannot hold ring2 to a sane duration, gate it behind an explicit CI job rather than
deleting it, and say so in the PR body. Do not silently drop a DoD row.

- [ ] **Step 3: Wire CI and commit**

```bash
git add -A && git commit -m "test(api): the API suite on real PostgreSQL, plus §2.1's two paging criteria"
```

---

## Task 10: The surviving record — docs, deviations, follow-ups

**Files:**
- Create: `docs/architecture/data-api.md`
- Modify: `docs/superpowers/specs/2026-07-25-f3-crud-vertical-slice-design.md` (a
  *Deviations added by PR3* subsection), `docs/architecture/data-path.md` (reserved names, the
  idempotency table), `docs/architecture/package-boundary.md` (the core's new ASP.NET dependency)

- [ ] **Step 1: Write `docs/architecture/data-api.md`**

The plan is discarded once merged, so anything decided here that outlives it goes in this file: the URL
grammar and its allow-list; the cursor's contract and why the API layer cannot mint one; the ETag's
strong-over-row-version decision and its stated cost; the idempotency record's shape and why it stores
a row id rather than a body; the complete status-code/`type`-slug catalogue; the anonymous-vs-401
distinction; and the alternatives rejected (a `Link` header duplicating `next`, a `{entity}` catch-all
route, PUT-as-upsert, storing response bodies).

- [ ] **Step 2: Record the deviations in the design doc**

Numbered continuation of *Deviations added by PR2* (which ends at 18), each with the reason and the
cost, in the same voice:

19. `QueryAsync` returns `AlvoPage`; `AlvoQuery` gains `Offset`.
20. Two new exception families (412, 409) extend the "three families" contract PR2 called settled.
21. The precondition and the idempotency token enter the port rather than living above it.
22. `readOnly` writes are 422 from validation and 403 from the port — one behaviour per layer.
23. Anonymous is a context, not a 401.
24. PATCH-only partial update; no PUT (the port has no whole-resource replacement).
25. A JSON envelope (`items` / `next`) rather than PostgREST's bare array plus `Content-Range`.

- [ ] **Step 3: File the follow-up issues**

One issue each, labelled `enhancement`, milestone F4 unless noted, each naming what it blocks:

- Bulk operations (batch insert/update/delete, transactional) — §2.1 must-have.
- `POST /query` for filters past the URL length limit — §2.1 "pozor na".
- Relation embedding (`select=…,owner(name)`) with a depth cap of 1–2.
- Aggregations (`count`/`min`/`max`/`sum`) **over the policy-filtered set** — §2.4's named leak.
- `Prefer: count=exact|planned|estimated` filling `AlvoPage.TotalCount`.
- Upsert, and the PUT semantics it needs.
- Projection aliases (`select=name:full_name`).
- A `Link: rel="next"` header, if a consumer asks — deliberately not shipped, so `next` has one home.
- Rate limiting and per-key quotas (§2.12), which the API surface now makes reachable.

- [ ] **Step 4: ring2, the three gates, and the PR**

1. `scripts/test-ring2`
2. `/code-review medium` (or `high` — the diff is large), fix findings
3. `/security-review` **required**: this diff is the security core's front door (auth filter, filter
   parsing, policy-scoped FK checks, error messages that must not become oracles). Pair with the
   `alvo-security-core-review` checklist.
4. Dispatch `alvo-plan-guard`
5. Open the PR against `f3/pr2-alvodata-ef`, label `needs-deep-review`, milestone F3. It closes no
   issue (#19 and #75 close in PR4); say so in the body, with the DoD table showing which rows this PR
   satisfies and which PR4 finishes.

---

## Self-review of this plan

**Spec coverage.** #19's scope: list/get/create/update/delete ✅ (T3), upsert ⏭ deferred with an issue
and a stated reason (T10), filters ✅ (T4), sort ✅ (T4), projections ✅ (T4, top-level only), keyset +
offset ✅ (T1, T4), RFC 7807 ✅ (T5), `Idempotency-Key` ✅ (T2, T7), schema-derived validation before
persistence ✅ (T5), SQLite + PostgreSQL ✅ (T9), TeaPie ⏭ PR4 by design. #75: 3.1 ✅, route-anchored
✅, transformer ✅, consistency contract test ✅, Verify snapshot ✅, Abstractions ASP.NET-free ✅,
Vacuum ⏭ (#26), Scalar ⏭ (PR4). #90 ✅ (T2). §2.1's four acceptance criteria: adversarial/fuzz ✅ (T4),
p95 + keyset ✅ (T9), no entity without policy ✅ (inherited from the port, asserted in T3),
idempotent POST ✅ (T7).

**Placeholders.** None: every task names exact files, exact signatures, and exact test names.
Where a body is not spelled out, the *fact it must discriminate* is stated instead — deliberately, so
an implementer cannot satisfy the name without satisfying the intent.

**Type consistency.** `AlvoPage`, `AlvoQuery.Offset`, `AlvoPrecondition`, `AlvoIdempotency`,
`AlvoViolation`, `AlvoApiOptions`, `AlvoProblemTypes`, `RowVersionETag`, `ProblemResultFactory` are
each defined once (T1, T1, T2, T2, T5, T3, T5, T6, T5) and referenced under those names throughout.
`AlvoManagedColumns.VersionColumn` (T2) is what T6 reads. `EnsurePagingWindowIsSane` (T1) is what T4
relies on for its refusals.

**Known risk, stated rather than hidden.** T1 and T2 change a port two PRs already build on, so the
adversarial, differential, ordering and statement suites all move with them. That is the cheapest this
change will ever be — nothing is released — and it is why both tasks come first, before any HTTP code
depends on the old shape.
