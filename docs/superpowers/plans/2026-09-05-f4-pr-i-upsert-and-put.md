# PR-I — Upsert and PUT semantics Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Ship `PUT {prefix}/{entity}/{id}` — create-or-replace the row the path names, with the policy
`WITH CHECK` predicate evaluated on the candidate row in **both** branches.

**Architecture:** One new port member, `IAlvoData.ReplaceAsync`, returning `AlvoReplaceResult(Row, Created)`.
It reads the pre-image through the same policy-scoped, row-locking `FromSql` root `UpdateAsync` uses; a row
found takes the replace branch, no row takes the create branch. Both branches run `WITH CHECK` on the
candidate. The route is a new `DataApiEndpointKind.Replace` on the existing `{prefix}/{entity}/{id:guid}`
pattern, gated on **both** `create` and `update`.

**Tech Stack:** .NET 10 (`net10.0`), EF Core over SQLite + PostgreSQL, xUnit v3 on Microsoft.Testing.Platform,
Shouldly, Verify, PublicApiGenerator, Testcontainers, TeaPie for E2E.

**Spec:** `docs/superpowers/specs/2026-09-05-f4-pr-i-upsert-and-put-semantics-design.md` — read it first;
every task below argues from a numbered section of it.

## Global Constraints

- **Never merge or push to `main`.** Branch `f4/pr-i-upsert-and-put` → PR.
- **`WITH CHECK` runs on the candidate row in both branches.** This is the one thing that must not be wrong.
- **`id` in a request body stays refused on every route**, this one included. Only the path carries it.
- **`WritePayloadGuard` is called with `isUpdate: true` unconditionally on this route** (spec §5).
- **The pre-image is read under the *update* decision's `Using`** (spec §4). A `create` decision reading a
  stored row is the F3 PR3 bypass, verbatim (`PolicyDecision.cs:62-69`).
- **Assertions use Shouldly.** Never FluentAssertions.
- **Any `.cs` written through bash/python must be CRLF + UTF-8 BOM**, or the pre-commit `dotnet format` task
  fails. Prefer the editing tools.
- **`scripts/test-ring0` after every step that touches code**; `scripts/test-ring2` before the PR.
- **Verify each test by injecting the bug it claims to catch**, and confirm the build had **0 errors** first —
  a failed build tests a stale binary and reports a meaningless pass.
- Every deliberate deviation from a source goes into the design doc, not into a code comment.

## The three test idioms this repo has, and which is which

The single most common way to get this plan wrong is to write a test against the wrong one. They are not
interchangeable and none of them has a `Client`, a `Data` property, or a `work_orders` entity.

| Layer | Base | How a fact starts | Entities / fields |
|---|---|---|---|
| **port contract** (Tasks 3–6) | `AlvoDataFixture` (`src/MMLib.Alvo.Testing/Data/AlvoDataFixture.cs`) | `var world = await AuditedWorldAsync();` then `world.Data.…`, `Ct` for the token | `Orders`/`Receipts`/`Tickets`/`Drafts`/`Invoices`/`Vaults`/`Dropbox`; fields are `title` (**nullable**) plus an optional `Extra` (**required**) |
| **outbox** (Task 9) | `IAlvoDataOutboxWorld` (`test/_shared/ef/AlvoDataOutboxWorld.cs`) | `world.EventsAsync()`, asserted as the **whole ordered sequence** | `vehicles` |
| **HTTP** (Task 7) | `AlvoApiWorld` (`test/_shared/api/AlvoApiWorld.cs`) | `await using var world = await AlvoApiWorld.VehicleRegistryAsync([...]); world.SendAsync(HttpMethod.Put, …)` | `owners`/`vehicles` under `/api`; `OpenApiDocumentAsync()` returns a **`JsonObject`**, not a typed document |

Fixture helpers that exist, spelled exactly: `Payload(string title)`, `OwnedPayload(title, owner)`,
`TenantPayload(title, tenant)`, `IdOf(record)`, `VersionOf(record)`, `TokenFor(entity)`, `NewKey()`, `Ct`.
Callers come off the world: `world.Caller`, `world.Alice`, `world.Bob`, `world.AcmeCaller`,
`world.GlobexCaller`.

**There is no `AlvoPrecondition.Match`.** The type is `readonly record struct AlvoPrecondition(DateTimeOffset
Version)` with only `EnsureSupported` and `EnsureMatches` as statics. Construct it `new
AlvoPrecondition(VersionOf(record))` — and never from a clock, which the type's own remarks call out.

---

## File structure

**Created**

| File | Responsibility |
|---|---|
| `src/MMLib.Alvo.Abstractions/Data/AlvoReplaceResult.cs` | the port's answer: the row, and whether it was created |
| `test/MMLib.Alvo.Abstractions.Tests/AlvoReplaceResultTests.cs` | the states that type refuses |
| `src/MMLib.Alvo.Testing/Data/AlvoDataReplaceTests.cs` | the inherited contract |
| `test/MMLib.Alvo.Tests/Data/InMemoryAlvoDataReplaceTests.cs` | **the in-memory leg — without it the suite runs zero tests and the ring is green** |
| `test/MMLib.Alvo.Data.Sqlite.Tests/SqliteAlvoDataReplaceTests.cs` | the SQLite leg |
| `test/MMLib.Alvo.Data.PostgreSql.Tests.Integration/PostgreSqlAlvoDataReplaceTests.cs` | the PostgreSQL leg |
| `test/MMLib.Alvo.Api.Tests/DataApiReplaceTests.cs` | the route |
| `test/teapie-field-service/120-Replace/` | the E2E pins |

**Modified**

| File | Change |
|---|---|
| `src/MMLib.Alvo.Abstractions/Data/IAlvoData.cs:433` | `ReplaceAsync` after `UpdateAsync` |
| `src/MMLib.Alvo.Abstractions/Data/AlvoConstraintViolationException.cs:32-34` | the managed-column exclusion says *which* writes it speaks for |
| `src/MMLib.Alvo.Testing/Data/InMemoryAlvoData.cs:28` | the reference implementation |
| `src/MMLib.Alvo.Data.EntityFrameworkCore/Internal/EfAlvoData.cs` | `ReplaceAsync`, the branch, the candidate builder |
| `src/MMLib.Alvo.Data.EntityFrameworkCore/Internal/ConstraintViolationTranslator.cs:145-154` | a caller-keyed write keeps `id` in `CallerFields` |
| **`test/_shared/api/FaultingAlvoData.cs:15`** | **a third `IAlvoData` implementor — compiled into two projects, breaks the moment the member is declared** |
| `src/MMLib.Alvo.Testing/Data/.editorconfig` | add `AlvoDataReplaceTests.cs` to the `CA1707` brace list |
| `src/MMLib.Alvo/Api/Internal/DataApiEndpointKind.cs` | `Replace`, `ToDataOperation`, `ToWireName` |
| `src/MMLib.Alvo/Api/Internal/DataApiEndpoints.cs:497` | `MapReplace` beside `MapUpdate` |
| `src/MMLib.Alvo/Api/Internal/DataApiDocumentation.cs:158,344,366` | three switches that **throw** |
| **`src/MMLib.Alvo/Api/Internal/DataApiParameters.cs:164,232`** | **two switches that fail *silently*** |
| `src/MMLib.Alvo/Api/Internal/SchemaComponentBuilder.cs` | the replace request body |
| `docs/architecture/data-api.md`, `data-path.md`, `events.md` | the route, the branch, the events |

---

### Task 1: `AlvoReplaceResult`

**Files:**
- Create: `src/MMLib.Alvo.Abstractions/Data/AlvoReplaceResult.cs`
- Test: `test/MMLib.Alvo.Abstractions.Tests/AlvoReplaceResultTests.cs`

**Interfaces:**
- Consumes: `AlvoRecord`.
- Produces: `AlvoReplaceResult(AlvoRecord Row, bool Created)`, get-only, plus `CreatedRow(row)` /
  `ReplacedRow(row)`. Task 3 returns it; Task 7 branches on `Created` for `201` vs `200`.

- [ ] **Step 1: Write the failing test**

`test/MMLib.Alvo.Abstractions.Tests/AlvoReplaceResultTests.cs`:

```csharp
using MMLib.Alvo.Data;

namespace MMLib.Alvo.Abstractions.Tests;

/// <summary>The states <see cref="AlvoReplaceResult"/> refuses to be in.</summary>
public sealed class AlvoReplaceResultTests
{
    private static readonly AlvoRecord _row =
        new(new Dictionary<string, object?>(StringComparer.Ordinal) { ["id"] = Guid.NewGuid() });

    [Fact]
    public void A_result_must_carry_a_row() =>
        Should.Throw<ArgumentNullException>(() => new AlvoReplaceResult(null!, Created: true));

    [Fact]
    public void The_two_shapes_are_accepted()
    {
        AlvoReplaceResult.CreatedRow(_row).Created.ShouldBeTrue();
        AlvoReplaceResult.ReplacedRow(_row).Created.ShouldBeFalse();
        AlvoReplaceResult.CreatedRow(_row).Row.ShouldBeSameAs(_row);
    }

    /// <summary>A `with` expression cannot rebuild a state the constructor refused.</summary>
    [Fact]
    public void The_members_are_not_settable()
    {
        foreach (var name in new[] { "Row", "Created" })
        {
            typeof(AlvoReplaceResult).GetProperty(name)!.SetMethod.ShouldBeNull(
                $"'{name}' has a setter, so a 'with' expression bypasses the constructor");
        }
    }
}
```

- [ ] **Step 2: Run it and watch it fail**

Run: `dotnet test --project test/MMLib.Alvo.Abstractions.Tests/MMLib.Alvo.Abstractions.Tests.csproj --filter-query "/*/*/AlvoReplaceResultTests/*"`
Expected: FAIL — build error CS0246, `AlvoReplaceResult` does not exist.

- [ ] **Step 3: Write the type**

`src/MMLib.Alvo.Abstractions/Data/AlvoReplaceResult.cs`:

```csharp
namespace MMLib.Alvo.Data;

/// <summary>What one create-or-replace produced: the row, and which branch produced it.</summary>
/// <remarks>
/// <b><see cref="Created"/> exists because the caller has to answer <c>201</c> or <c>200</c>, and that
/// answer is not derivable from the row.</b> A replaced row and a created one are the same shape; only the
/// port knows which branch ran, so only the port can say.
/// </remarks>
public sealed record AlvoReplaceResult
{
    /// <summary>Initializes a new instance of the <see cref="AlvoReplaceResult"/> class.</summary>
    /// <param name="Row">The row as it now stands.</param>
    /// <param name="Created">Whether the row did not exist and this write created it.</param>
    /// <exception cref="ArgumentNullException"><paramref name="Row"/> is <see langword="null"/>.</exception>
    public AlvoReplaceResult(AlvoRecord Row, bool Created)
    {
        ArgumentNullException.ThrowIfNull(Row);

        this.Row = Row;
        this.Created = Created;
    }

    /// <summary>The row as it now stands.</summary>
    /// <remarks>
    /// <b>Get-only, not <c>init</c>.</b> A <c>with</c> expression does not run the constructor, so an
    /// <c>init</c> setter would let a caller rebuild a state the constructor refused — the hole
    /// <see cref="AlvoBatchResult"/> closed the same way.
    /// </remarks>
    public AlvoRecord Row { get; }

    /// <inheritdoc cref="Row"/>
    /// <summary>Whether the row did not exist and this write created it.</summary>
    public bool Created { get; }

    /// <summary>The result of a write that created the row.</summary>
    /// <param name="row">The row it created.</param>
    public static AlvoReplaceResult CreatedRow(AlvoRecord row) => new(row, Created: true);

    /// <summary>The result of a write that replaced an existing row.</summary>
    /// <param name="row">The row as it now stands.</param>
    public static AlvoReplaceResult ReplacedRow(AlvoRecord row) => new(row, Created: false);
}
```

- [ ] **Step 4: Run it and watch it pass** — same command, 3 tests.

- [ ] **Step 5: Verify the `with` test is not vacuous**

Change `public AlvoRecord Row { get; }` to `{ get; init; }`, re-run, confirm
`The_members_are_not_settable` goes **red naming `Row`**. Revert.

- [ ] **Step 6: Accept the public-API baseline**

Run `scripts/test-ring1`. `PublicApi.MMLib.Alvo.Abstractions.verified.txt` grows by `AlvoReplaceResult` and
its synthesized record members. The `turn-review-gate` Stop hook fires on a grown baseline — answer it with
the design's §11 table, which lists exactly these.

- [ ] **Step 7: Commit**

```bash
git add src/MMLib.Alvo.Abstractions/Data/AlvoReplaceResult.cs \
        test/MMLib.Alvo.Abstractions.Tests/AlvoReplaceResultTests.cs \
        test/MMLib.Alvo.Abstractions.Tests/PublicApi.MMLib.Alvo.Abstractions.verified.txt
git commit -m "feat(abstractions): the port's answer to a create-or-replace"
```

---

### Task 2: A caller-keyed primary-key collision becomes a conflict

Spec §3. Today a collision on `id` is a `500` and, on the idempotent path, ten retries first.

**Scope note, and why this task is not a unit test.** `ConstraintViolationTranslator.Translate` is
`private static` (`:82`), there is no translator test file anywhere in the repo, and building its arrange
would mean constructing a `SqlConstraintViolation`, an EF `IEntityType` and an `EntitySchema` by hand. The
translator's behaviour is pinned today through the **inherited `AlvoDataConstraintTests`** against real
engines, and that is where this change is pinned too. Do **not** widen `Translate` to `internal` to make a
unit test possible — the engine-level pin is the stronger claim, because it proves what the driver actually
does with a real provider exception.

**Files:**
- Modify: `src/MMLib.Alvo.Data.EntityFrameworkCore/Internal/ConstraintViolationTranslator.cs:145-154`
- Modify: `src/MMLib.Alvo.Abstractions/Data/AlvoConstraintViolationException.cs:32-34`
- Test: `src/MMLib.Alvo.Testing/Data/AlvoDataConstraintTests.cs` (Task 3 adds the fact that exercises it,
  because it needs `ReplaceAsync` to exist)

**Interfaces:**
- Produces: `ConstraintViolationTranslator.TranslatedAsync<T>(…, bool callerKeyed)` and the private
  `Translate(…, bool callerKeyed)`. Task 3's create branch passes `callerKeyed: true`; every other call site
  passes `false`.

- [ ] **Step 1: Thread the flag through**

Add `bool callerKeyed` to both `TranslatedAsync` overloads (`:46`, `:60`), to `Translate` (`:82`) and to
`CallerFields` (`:145`). In `CallerFields`, the managed-column strip keeps `AlvoManagedColumns.Id` when
`callerKeyed` is true. **Do not exempt `tenant_id`** — the flag says the caller supplied the *row key*, not
every managed column.

Give the parameter a `<param>` doc stating the premise it encodes: *the caller chose this row's key, so a
collision on it is theirs to fix.*

- [ ] **Step 2: Update every existing call site to `callerKeyed: false`**

Every current caller mints its own key. Passing the argument explicitly at each site — rather than
defaulting it — is what makes the create branch's `true` visible in review.

- [ ] **Step 3: Amend the public remark it contradicts**

`AlvoConstraintViolationException.cs:32-34` reads "…because a caller cannot change one — a collision confined
to them is a broken invariant rather than a conflict, and an implementation must let that keep propagating as
one." Extend it to say which writes it speaks for: every write whose key the framework mints, which is all of
them except create-or-replace, where the caller supplies `id`. Do not weaken it for `tenant_id` or the audit
columns.

- [ ] **Step 4: Run ring0 and commit**

Expected: green — nothing has changed behaviour yet, because no caller passes `true` until Task 3.

```bash
scripts/test-ring0
git add src/MMLib.Alvo.Data.EntityFrameworkCore/Internal/ConstraintViolationTranslator.cs \
        src/MMLib.Alvo.Abstractions/Data/AlvoConstraintViolationException.cs
git commit -m "refactor(data): the translator asks whether the caller chose the row key"
```

---

### Task 3: The port member and both implementations

The core. Spec §2, §3, §4, §5. A reviewer cannot approve one branch without the other, which is why this is
one task.

**Files:**
- Modify: `src/MMLib.Alvo.Abstractions/Data/IAlvoData.cs` (after `UpdateAsync`, `:433`)
- Modify: `src/MMLib.Alvo.Testing/Data/InMemoryAlvoData.cs`
- Modify: `src/MMLib.Alvo.Data.EntityFrameworkCore/Internal/EfAlvoData.cs`
- Modify: `test/_shared/api/FaultingAlvoData.cs`
- Create: `src/MMLib.Alvo.Testing/Data/AlvoDataReplaceTests.cs`
- Create: the three concrete legs (see Step 9 — **the suite is dead without them**)
- Modify: `src/MMLib.Alvo.Testing/Data/.editorconfig`

**Interfaces:**
- Consumes: `AlvoReplaceResult` (Task 1); `TranslatedAsync(…, callerKeyed)` (Task 2).
- Produces:
  ```csharp
  Task<AlvoReplaceResult> ReplaceAsync(
      string entity, Guid id, IReadOnlyDictionary<string, object?> values, AlvoContext context,
      AlvoPrecondition? precondition = null, AlvoIdempotency? idempotency = null,
      CancellationToken cancellationToken = default);
  ```

- [ ] **Step 1: Write the create-branch fact**

`src/MMLib.Alvo.Testing/Data/AlvoDataReplaceTests.cs`, opening exactly like `AlvoDataBatchTests.cs` (UTF-8
BOM, `public abstract class AlvoDataReplaceTests : AlvoDataFixture`, a `<remarks>` naming the failure the
suite exists to catch):

```csharp
/// <summary>A PUT on an id no row holds creates that row, under the caller's own id.</summary>
[Fact]
public async Task Replacing_an_absent_row_creates_it_under_the_caller_s_own_id()
{
    var world = await AuditedWorldAsync();
    var id = Guid.NewGuid();

    var result = await world.Data.ReplaceAsync(Orders, id, Payload("Fix the boiler"), world.Caller, cancellationToken: Ct);

    result.Created.ShouldBeTrue();
    IdOf(result.Row).ShouldBe(id, "the path's id is the row's id");
}
```

- [ ] **Step 2: Write the two facts this whole PR exists for**

`OwnedWorldAsync()` applies `OwnerRules`, whose `WITH CHECK` is `owner_id == @user.id` — so Bob writing a row
owned by Alice is refused, on either branch. That is the refusing rule; there is no `RefusedByCheck` helper
and none is needed.

```csharp
/// <summary>WITH CHECK judges the candidate on the create branch, not only on the replace branch.</summary>
/// <remarks>
/// An upsert that checks the update branch and lets the create branch through is a policy bypass. It is the
/// failure this design is most likely to have, so one rule is asserted against both branches.
/// </remarks>
[Fact]
public async Task The_write_check_refuses_the_candidate_on_the_create_branch()
{
    var world = await OwnedWorldAsync();

    await Should.ThrowAsync<AlvoAuthorizationException>(
        () => world.Data.ReplaceAsync(
            Orders, Guid.NewGuid(), OwnedPayload("Not mine", world.Alice), world.Bob, cancellationToken: Ct));
}

/// <summary>…and on the replace branch, from the same rule.</summary>
[Fact]
public async Task The_write_check_refuses_the_candidate_on_the_replace_branch()
{
    var world = await OwnedWorldAsync();
    var mine = await world.Data.CreateAsync(Orders, OwnedPayload("Mine", world.Bob), world.Bob, cancellationToken: Ct);

    await Should.ThrowAsync<AlvoAuthorizationException>(
        () => world.Data.ReplaceAsync(
            Orders, IdOf(mine), OwnedPayload("Handed to Alice", world.Alice), world.Bob, cancellationToken: Ct));
}
```

- [ ] **Step 3: Write the replace-branch fact**

```csharp
/// <summary>A PUT on an id a visible row holds replaces that row rather than creating a second.</summary>
[Fact]
public async Task Replacing_a_visible_row_replaces_it()
{
    var world = await AuditedWorldAsync();
    var existing = await world.Data.CreateAsync(Orders, Payload("First"), world.Caller, cancellationToken: Ct);

    var result = await world.Data.ReplaceAsync(
        Orders, IdOf(existing), Payload("Second"), world.Caller, cancellationToken: Ct);

    result.Created.ShouldBeFalse();
    result.Row["title"].ShouldBe("Second");
    IdOf(result.Row).ShouldBe(IdOf(existing), "a replaced row is the same row");
}
```

- [ ] **Step 4: Write the invisible-row fact**

`TenantedWorldAsync()` gives two tenants whose rows are invisible to each other — the sharpest form of §3's
case, and the one that would be catastrophic if the write went through.

```csharp
/// <summary>An id held by a row in another tenant is refused, and that row is left exactly as it was.</summary>
/// <remarks>
/// The design records the residual disclosure in §3: the caller learns the id is taken. What must never
/// happen is either of the other two outcomes — writing over a row their USING excludes, or reporting
/// success. This asserts the row itself, not a count, because a count cannot tell an overwrite from a no-op.
/// </remarks>
[Fact]
public async Task Replacing_a_row_in_another_tenant_is_refused_and_leaves_it_untouched()
{
    var world = await TenantedWorldAsync();
    var theirs = await world.Data.CreateAsync(
        Orders, TenantPayload("Theirs", world.Globex), world.GlobexCaller, cancellationToken: Ct);

    await Should.ThrowAsync<AlvoConstraintViolationException>(
        () => world.Data.ReplaceAsync(
            Orders, IdOf(theirs), TenantPayload("Mine now", world.Acme), world.AcmeCaller, cancellationToken: Ct));

    var stillTheirs = await world.Data.GetAsync(Orders, IdOf(theirs), world.GlobexCaller, cancellationToken: Ct);
    stillTheirs!["title"].ShouldBe("Theirs");
}
```

- [ ] **Step 5: Run and watch every one of them fail**

Run: `scripts/test-ring0`
Expected: build failure — `ReplaceAsync` is not a member of `IAlvoData`.

- [ ] **Step 6: Declare the port member**

In `IAlvoData.cs`, after `UpdateAsync` (`:433`), add the signature from **Interfaces**, with XML docs
stating: the path's `id` is the row's identity and `id` in `values` is still refused; `WITH CHECK` is
evaluated on the candidate in **both** branches; a row excluded by `USING` takes the create branch and its
collision surfaces as `AlvoConstraintViolationException`; a replay answers `Created: false`; and omitted
fields are **not** preserved — pointing at `UpdateAsync` for the partial write.

- [ ] **Step 7: Add the arm to `FaultingAlvoData`**

`test/_shared/api/FaultingAlvoData.cs:15` is a third `IAlvoData` implementor, compiled into **two** projects
via `<Compile Include="…/_shared/api/*.cs" />`. It implements all eight members explicitly; add the ninth the
same way the others are written (`throw new InvalidOperationException(FailureMessage);`). **Without this the
branch does not compile, and the error will look like it comes from somewhere else.**

- [ ] **Step 8: Implement it in `InMemoryAlvoData`, then `EfAlvoData`**

`InMemoryAlvoData` is the reference. Resolve **both** decisions (`Create` and `Update`) and deny unless both
allow. Read the pre-image under the **update** decision. Found → replace; absent → create. Run the payload
guard with `isUpdate: true` against **both** decisions' `ReadOnlyFields`, refusing on the first non-null
answer (spec §4). Evaluate `WITH CHECK` on the candidate in both branches, `previous: null` on create and the
pre-image on replace. Stamp with `isUpdate: false` on create and `isUpdate: true` on replace. Mask the
response with the branch's own write decision, as `UpdatedAsync` does.

`EfAlvoData.ReplaceAsync` mirrors `UpdateAsync`'s outer shape (`:922-957`) and its inner work follows
`WriteAsync` (`:1320-1345`) up to the not-found point, then branches:

- `SingleAsync(db, schema, updateDecision, context, id, PreImageMutation.Update, …, unmasked: true)`;
- **row returned** → precondition, `RunBeforeUpdate`, `EnsureWriteAllowed` over the merged post-image,
  `AffectedAsync`, re-read;
- **no row** → build the candidate with the caller's `id`, guard with `isUpdate: true`, stamp with
  `isUpdate: false`, `EnsureWriteAllowed` with `previous: null`, `RunBeforeCreate`'s re-verdict, then insert
  with `callerKeyed: true`.

**Do not reuse `AuthorizedCandidate`** (`:352-363`): it passes one `isUpdate` to both the guard and the stamp,
and this route needs `true` for one and `false` for the other (spec §5). Write a sibling taking the two
separately, each parameter named for the question it answers.

- [ ] **Step 9: Create the three concrete legs — without these the suite runs zero tests**

An inherited port suite is `public abstract` and runs only through a per-driver subclass. `AlvoDataBatchTests`
has exactly three; copy each one's shape verbatim, changing only the base class and the prose:

- `test/MMLib.Alvo.Tests/Data/InMemoryAlvoDataReplaceTests.cs`
- `test/MMLib.Alvo.Data.Sqlite.Tests/SqliteAlvoDataReplaceTests.cs`
- `test/MMLib.Alvo.Data.PostgreSql.Tests.Integration/PostgreSqlAlvoDataReplaceTests.cs`

**This is the step whose omission fails silently.** With the abstract suite and no subclass, the ring is
green, the count is unchanged, and deleting the code under test changes nothing — the exact vacuous pass the
Global Constraints warn about, and one no injection check can catch. Confirm the ring0 test **count** rose by
the number of facts you wrote before believing any of them.

- [ ] **Step 10: Add the file to the `CA1707` brace list**

`src/MMLib.Alvo.Testing/Data/.editorconfig` scopes `CA1707` by an explicit list of filenames, not by folder.
Add `AlvoDataReplaceTests.cs` to it or every snake_case fact is a build error.

- [ ] **Step 11: Run and watch them pass**

Run `scripts/test-ring0`, then `scripts/test-ring2` for the SQLite and PostgreSQL legs.

- [ ] **Step 12: Verify the two `WITH CHECK` facts by injection**

Delete the `EnsureWriteAllowed` call on the **create** branch only. Confirm
`The_write_check_refuses_the_candidate_on_the_create_branch` goes red on all three legs and the replace one
stays green. Restore, then do the mirror image. **Check the build had 0 errors before trusting either.**

- [ ] **Step 13: Commit**

```bash
scripts/test-ring0
git add -u && git add test/MMLib.Alvo.Tests/Data/InMemoryAlvoDataReplaceTests.cs \
        test/MMLib.Alvo.Data.Sqlite.Tests/SqliteAlvoDataReplaceTests.cs \
        test/MMLib.Alvo.Data.PostgreSql.Tests.Integration/PostgreSqlAlvoDataReplaceTests.cs \
        src/MMLib.Alvo.Testing/Data/AlvoDataReplaceTests.cs
git commit -m "feat(data): create-or-replace, with the write check on the candidate in both branches"
```

---

### Task 4: `tenant_id` is refused on both branches

Spec §5. This oracle is decided from the payload alone, which makes it worse than §3's.

**Files:** Test only — `src/MMLib.Alvo.Testing/Data/AlvoDataReplaceTests.cs`

- [ ] **Step 1: Write the fact**

```csharp
/// <summary>tenant_id is refused on this route whether or not the row exists.</summary>
/// <remarks>
/// tenant_id is caller-writable on a create and refused on an update. If this route asked that question per
/// branch, "is my tenant_id refused?" would answer "does this row exist?" — an existence oracle decided from
/// the payload alone, before any row is read. So the guard is told isUpdate: true unconditionally.
/// </remarks>
[Theory]
[InlineData(true)]
[InlineData(false)]
public async Task Tenant_id_in_the_payload_is_refused_whether_or_not_the_row_exists(bool rowExists)
{
    var world = await TenantedWorldAsync();
    var id = Guid.NewGuid();
    if (rowExists)
    {
        var existing = await world.Data.CreateAsync(
            Orders, TenantPayload("Here", world.Acme), world.AcmeCaller, cancellationToken: Ct);
        id = IdOf(existing);
    }

    var refusal = await Should.ThrowAsync<AlvoAuthorizationException>(
        () => world.Data.ReplaceAsync(
            Orders, id, TenantPayload("Moved", world.Acme), world.AcmeCaller, cancellationToken: Ct));

    refusal.Message.ShouldContain(AlvoManagedColumns.TenantId);
}
```

`[Theory]`/`[InlineData]` are already used in this folder (`AlvoDataComparisonTests.cs:48-54`).

- [ ] **Step 2: Run and confirm it passes**

Task 3 already passes `isUpdate: true`, so both cases pass. **A test that passes on its first run has proven
nothing** — Step 3 is the task.

- [ ] **Step 3: Verify by injection — this is the whole task**

Change the guard call on the create branch to `isUpdate: false`. Confirm the `rowExists: false` case goes
**red** while `rowExists: true` stays green. That divergence *is* the oracle, made visible. Restore and
confirm both are green again.

- [ ] **Step 4: Commit**

```bash
git add -u && git commit -m "test(data): pin that a branch-dependent tenant_id refusal cannot come back"
```

---

### Task 5: Replacement semantics for omitted fields

Spec §6. The fixture already carries both shapes: `title` is **nullable** and an entity's `Extra` field is
**required** (`AlvoDataFixture.cs:216-241`, `:255-276`). No fixture change is needed.

**Files:**
- Modify: `src/MMLib.Alvo.Data.EntityFrameworkCore/Internal/EfAlvoData.cs`, `InMemoryAlvoData.cs`
- Test: `src/MMLib.Alvo.Testing/Data/AlvoDataReplaceTests.cs`

- [ ] **Step 1: Write the failing facts**

```csharp
/// <summary>A nullable field the replacement omits does not keep its stored value.</summary>
/// <remarks>
/// This is what separates PUT from PATCH, and it is asserted from a row that has the field set — one
/// starting state cannot tell replacement from a merge.
/// </remarks>
[Fact]
public async Task A_nullable_field_the_replacement_omits_becomes_null()
{
    var world = await ExtraWorldAsync();
    var existing = await world.Data.CreateAsync(Extras, ExtraPayload("First", 7), world.Caller, cancellationToken: Ct);

    var replaced = await world.Data.ReplaceAsync(
        Extras, IdOf(existing), ExtraOnlyPayload(7), world.Caller, cancellationToken: Ct);

    replaced.Row["title"].ShouldBeNull("a replacement replaces; it does not merge");
}

/// <summary>A required field the replacement omits is refused, naming the field.</summary>
[Fact]
public async Task A_required_field_the_replacement_omits_is_refused()
{
    var world = await ExtraWorldAsync();
    var existing = await world.Data.CreateAsync(Extras, ExtraPayload("First", 7), world.Caller, cancellationToken: Ct);

    var refusal = await Should.ThrowAsync<ArgumentException>(
        () => world.Data.ReplaceAsync(Extras, IdOf(existing), Payload("Second"), world.Caller, cancellationToken: Ct));

    refusal.Message.ShouldContain(ExtraField);
}

/// <summary>created_at and created_by survive a replacement, because it is the same row.</summary>
[Fact]
public async Task The_creation_stamp_survives_a_replacement()
{
    var world = await AuditedWorldAsync();
    var existing = await world.Data.CreateAsync(Orders, Payload("First"), world.Caller, cancellationToken: Ct);

    var replaced = await world.Data.ReplaceAsync(
        Orders, IdOf(existing), Payload("Second"), world.Caller, cancellationToken: Ct);

    replaced.Row[AlvoManagedColumns.CreatedAt].ShouldBe(existing[AlvoManagedColumns.CreatedAt]);
    replaced.Row[AlvoManagedColumns.CreatedBy].ShouldBe(existing[AlvoManagedColumns.CreatedBy]);
}
```

`ExtraWorldAsync()`, `Extras`, `ExtraField`, `ExtraPayload(title, value)` and `ExtraOnlyPayload(value)` do
not exist yet — add them to `AlvoDataFixture` in the same shape as its siblings (`WorldAsync` over an
`EntityFixture` carrying an `Extra`, and `private protected static` payload helpers next to `Payload`). This
is fixture work, so it belongs in this task rather than a task of its own.

- [ ] **Step 2: Run and watch them fail**

Run: `scripts/test-ring0`. Expected: the first fails because the omitted field is preserved, the second
because nothing refuses it, the third passes already (stamp behaviour is Task 3's).

- [ ] **Step 3: Implement the replacement candidate**

On the replace branch the candidate is the payload **plus** the framework-managed columns read off the
pre-image — **not** `Merge(preImage, values)` (`:1439`), which is `UpdateAsync`'s partial semantics. Every
descriptor field the payload omits is written `null`; every `computed` field is omitted so the engine keeps
owning it; `created_at`/`created_by` are copied from the pre-image; `updated_at`/`updated_by` are stamped.

Extract "which columns come from the pre-image" into a named method rather than a comment.

- [ ] **Step 4: Implement the required-field refusal in the port**

The refusal must live in the port, not only in `RecordValidator`: an embedded host calling `ReplaceAsync`
directly must get the same answer, or the route's contract is an HTTP-layer courtesy. Refuse with an
`ArgumentException` naming the field — the port's fifth failure family, "a broken caller", which
`ProblemResultFactory.GuardAsync` (`:289-299`) already renders `422`.

State in the message that a field which is `required` **and** `hidden` makes the entity un-replaceable for a
caller who cannot read it, and point at `PATCH` — that is the case a caller will actually hit, and its fix
is different.

- [ ] **Step 5: Run, then verify by injection**

Run `scripts/test-ring0` and `scripts/test-ring2`. Then change the candidate back to
`Merge(preImage, values)` and confirm `A_nullable_field_the_replacement_omits_becomes_null` goes red while
`The_creation_stamp_survives_a_replacement` stays **green** — if both flip, the candidate builder is also
dropping the creation stamp, which is a second bug hiding behind the first.

- [ ] **Step 6: Commit**

```bash
git add -u && git commit -m "feat(data): a replacement replaces, and refuses a body that cannot express the row"
```

---

### Task 6: Idempotency, preconditions, and the replay's answer

Spec §7.

**Files:**
- Modify: `src/MMLib.Alvo.Data.EntityFrameworkCore/Internal/EfAlvoData.cs`, `InMemoryAlvoData.cs`
- Test: `src/MMLib.Alvo.Testing/Data/AlvoDataReplaceTests.cs`

- [ ] **Step 1: Write the failing facts**

```csharp
/// <summary>The same replacement applied to two different starting states lands on one row shape.</summary>
/// <remarks>
/// One starting state cannot tell replacement from a merge, so the property is asserted from two. This is
/// the spec's own acceptance criterion for the API invariant suite (alvo-specifikacia.md:309).
/// </remarks>
[Fact]
public async Task The_same_replacement_from_two_starting_states_lands_on_one_row()
{
    var world = await ExtraWorldAsync();
    var sparse = await world.Data.CreateAsync(Extras, ExtraOnlyPayload(1), world.Caller, cancellationToken: Ct);
    var full = await world.Data.CreateAsync(Extras, ExtraPayload("noisy", 1), world.Caller, cancellationToken: Ct);

    var one = await world.Data.ReplaceAsync(Extras, IdOf(sparse), ExtraPayload("Same", 2), world.Caller, cancellationToken: Ct);
    var two = await world.Data.ReplaceAsync(Extras, IdOf(full), ExtraPayload("Same", 2), world.Caller, cancellationToken: Ct);

    one.Row["title"].ShouldBe(two.Row["title"]);
    one.Row[ExtraField].ShouldBe(two.Row[ExtraField]);
}

/// <summary>A replay reports the state a previous request left, never an act of creation.</summary>
[Fact]
public async Task A_replayed_replacement_answers_that_it_created_nothing()
{
    var world = await AuditedWorldAsync();
    var id = Guid.NewGuid();
    var token = TokenFor(Orders);

    var first = await world.Data.ReplaceAsync(Orders, id, Payload("Once"), world.Caller, idempotency: token, cancellationToken: Ct);
    var replay = await world.Data.ReplaceAsync(Orders, id, Payload("Once"), world.Caller, idempotency: token, cancellationToken: Ct);

    first.Created.ShouldBeTrue();
    replay.Created.ShouldBeFalse("201 reports an act of creation and a replay performs none");
    IdOf(replay.Row).ShouldBe(id);
}

/// <summary>An If-Match on the create branch cannot match anything.</summary>
[Fact]
public async Task An_if_match_on_an_absent_row_fails_its_precondition()
{
    var world = await AuditedWorldAsync();
    var other = await world.Data.CreateAsync(Orders, Payload("Something else"), world.Caller, cancellationToken: Ct);

    await Should.ThrowAsync<AlvoPreconditionFailedException>(
        () => world.Data.ReplaceAsync(
            Orders, Guid.NewGuid(), Payload("Nope"), world.Caller,
            precondition: new AlvoPrecondition(VersionOf(other)), cancellationToken: Ct));
}
```

The precondition is built `new AlvoPrecondition(VersionOf(other))` — a version that came out of the database.
There is no `AlvoPrecondition.Match`, and the type's own remarks forbid minting a version from a clock.

- [ ] **Step 2: Run and watch them fail**

Run: `scripts/test-ring0`.

- [ ] **Step 3: Wire idempotency and the precondition**

Route `ReplaceAsync` through `ReplayableWriteAsync` (`:424-450`) when a token is present, as
`ReplayableCreateAsync` (`:405`) does: the lookup inside the write's own transaction, the record inserted
**last**, after the outbox emit. On the replace branch call
`AlvoPrecondition.EnsureMatches(precondition, StoredVersion(schema, stored))` after the not-found check and
before `WITH CHECK`, preserving the existing ordering. On the create branch let
`EnsureMatches(precondition, null)` do its own work — it already throws `AlvoPreconditionFailedException`
(`AlvoPrecondition.cs:93-103`) for a version match against nothing.

A replay returns `AlvoReplaceResult.ReplacedRow(...)` unconditionally.

- [ ] **Step 4: Run, then verify by injection**

`scripts/test-ring0`, then `scripts/test-ring2`. Change the replay to return `CreatedRow(...)` and confirm
`A_replayed_replacement_answers_that_it_created_nothing` goes red. Restore.

- [ ] **Step 5: Commit**

```bash
git add -u && git commit -m "feat(data): Idempotency-Key and If-Match on create-or-replace"
```

---

### Task 7: The route

Spec §4, §8. **Five switches, and two of them fail silently.**

**Files:**
- Modify: `src/MMLib.Alvo/Api/Internal/DataApiEndpointKind.cs`
- Modify: `src/MMLib.Alvo/Api/Internal/DataApiEndpoints.cs` (`Map` `:64-85`, new `MapReplace` beside
  `MapUpdate` `:497-534`)
- Modify: `src/MMLib.Alvo/Api/Internal/DataApiDocumentation.cs:158,344,366`
- Modify: `src/MMLib.Alvo/Api/Internal/DataApiParameters.cs:164,232`
- Create: `test/MMLib.Alvo.Api.Tests/DataApiReplaceTests.cs`

**Interfaces:**
- Consumes: `IAlvoData.ReplaceAsync`, `AlvoReplaceResult.Created`.
- Note: `Row` (`DataApiEndpoints.cs:1252`) and `Created` (`:1272`) are **private static members of
  `DataApiEndpoints` itself**, not of `ProblemResultFactory`. Call them unqualified.

- [ ] **Step 1: Write the failing tests**

`test/MMLib.Alvo.Api.Tests/DataApiReplaceTests.cs`, following the idioms in `DataApiBatchTests.cs`:
`await using var world = await AlvoApiWorld.VehicleRegistryAsync([...])`, `world.SendAsync(...)`, and
`world.OpenApiDocumentAsync()` which returns a **`System.Text.Json.Nodes.JsonObject`** — navigate it as JSON,
not as a typed `OpenApiDocument`.

Four tests: a `PUT` on a free id answers `201` with a `Location` ending in that id; a `PUT` on an existing row
answers `200` with no `Location`; every verb on the item path is still present in the document; and every
`operationId` in the document is unique.

- [ ] **Step 2: Write the test for the two silent switches — this is the one nothing else catches**

```csharp
/// <summary>The published PUT declares its id parameter and the headers it accepts.</summary>
/// <remarks>
/// DataApiParameters.AddressesOneRow (:164) and HeaderNames (:232) are switches whose miss is `false` and
/// `[]` — not an exception. So a missing arm publishes a PUT with no {id} parameter and neither If-Match nor
/// Idempotency-Key, and every other test in the suite stays green. PR-H shipped exactly this bug for the
/// batch kinds. Nothing but naming them catches it.
/// </remarks>
[Fact]
public async Task The_published_put_declares_its_id_parameter_and_its_headers()
{
    await using var world = await AlvoApiWorld.VehicleRegistryAsync([_admin]);
    var document = await world.OpenApiDocumentAsync();

    var put = document["paths"]!["/api/vehicles/{id}"]!["put"]!;
    var names = put["parameters"]!.AsArray().Select(p => (string)p!["name"]!).ToArray();

    names.ShouldContain("id");
    names.ShouldContain("If-Match");
    names.ShouldContain("Idempotency-Key");
}
```

Confirm the exact JSON path and the parameter shape against an existing document assertion in
`OpenApiDocumentTests` before writing it — the header parameters may be `$ref`s rather than inline names, in
which case assert on the resolved component ids instead.

- [ ] **Step 3: Run and watch them fail**

Run: `dotnet test --project test/MMLib.Alvo.Api.Tests/MMLib.Alvo.Api.Tests.csproj --filter-query "/*/*/DataApiReplaceTests/*"`

- [ ] **Step 4: Add the kind, its operation and its wire name**

```csharp
DataApiEndpointKind.Replace => DataOperation.Update,   // in ToDataOperation
DataApiEndpointKind.Replace => "replace",              // in ToWireName
```

**The explicit `ToWireName` arm is mandatory.** Without it the default arm falls through to
`ToDataOperation().ToWireName()` and spells it `"update"`, colliding with the single-row `PATCH`.

- [ ] **Step 5: Extend the three throwing switches**

`DataApiDocumentation.cs:158`, `:344` and `:366` each throw `InvalidOperationException` on an unmapped kind,
and `AlvoEndpointDataSource.BuildOrRefuseToRoute` (`:205`, catch at `:224-230`) turns that into
`RouteTable.NothingIsRoutable`. Add a `Replace` arm to all three.

- [ ] **Step 6: Extend the two silent switches**

`DataApiParameters.AddressesOneRow` (`:164`) gains `or DataApiEndpointKind.Replace`. `HeaderNames` (`:232`)
gains `Replace` to the `Update`/`Delete` arms so `PUT` publishes `If-Match` (when the entity has a version
column) and `Idempotency-Key`.

- [ ] **Step 7: Map the route**

`MapReplace` beside `MapUpdate`, mapping `endpoints.MapPut(item, …)`. It follows `MapUpdate` step for step
with two differences:

1. **Both operations are checked up front**, not one — `DataApiEndpoints`' remarks (`:46-49`) state a
   symmetric invariant, "nothing is admitted here that the port would refuse, and nothing is refused here
   that the port would admit", and checking only `update` breaks the first half:
   ```csharp
   EnsureOperationIsAllowed(policies, entity.Name, DataOperation.Create, context);
   EnsureOperationIsAllowed(policies, entity.Name, DataOperation.Update, context);
   ```
2. **It answers on the branch**: `result.Created ? Created(result.Row, entity) : Row(result.Row, entity)`.

Body reading goes through `ReadAndValidateAsync` (`:750`) with **`isCreate: true`** — `RecordValidator`
already treats a missing `required` field as a violation in that mode
(`RecordValidator.IsMissingRequiredValue`, `:121-124`, via `RecordValidationRequest.IsCreate`), and the same
flag already exempts store-filled fields and already mints `read-only-required-field` for the
`required` + masked case. Do not add a second message beside it.

- [ ] **Step 8: Run and watch them pass**

Run `scripts/test-ring0`. The route-count test is the canary for the throwing switches; the parameter test is
the only canary for the silent ones.

- [ ] **Step 9: Verify by injection, three times**

Remove the `Replace` arm from one `DataApiDocumentation` switch → the whole API suite goes red. Remove the
`ToWireName` arm → only the `operationId` uniqueness test goes red. Remove the `HeaderNames` arm → **only**
the parameter test goes red, and everything else stays green. That third one is the point of Step 2.

- [ ] **Step 10: Commit**

```bash
scripts/test-ring0
git add -u && git commit -m "feat(api): PUT on the item route, gated on both create and update"
```

---

### Task 8: The published document

**Files:**
- Modify: `src/MMLib.Alvo/Api/Internal/SchemaComponentBuilder.cs`
- Baseline: `test/MMLib.Alvo.Api.Tests/OpenApiDocumentTests.The_document_is_stable.verified.txt` — the
  **only** Verify snapshot covering the document

- [ ] **Step 1: Add the request-body component**

Add a replace body component distinct from the update body's: the update body's fields are all optional, the
replace body's `required` fields are required. Publishing the update body for `PUT` would document a merge.

- [ ] **Step 2: Run the snapshot suite and read the diff before accepting anything**

Run `scripts/test-ring1` and open the `.received.` file. A snapshot is the one place a check goes green with
no product change, and `turn-review-gate` will send you to `alvo-snapshot-judge` for exactly this. Accept only
what the route explains: a new `put` on the item path, a new component, **no change to any existing
operation**.

- [ ] **Step 3: Run Vacuum**

Run `scripts/test-ring2`. Expected: no new findings — and if it reports a path parameter missing from an
operation, Task 7 Step 6 was skipped.

- [ ] **Step 4: Commit**

```bash
git add -u && git commit -m "feat(api): publish the replace body as a whole-resource representation"
```

---

### Task 9: The events

Spec §7. This suite is `IAlvoDataOutboxWorld` over `vehicles`, and it asserts the **whole ordered sequence**
of events one act produced — its own docs reject a "last event" seam
(`test/_shared/ef/IAlvoDataOutboxWorld.cs:20-23`), so do not add one.

**Files:**
- Modify: `src/MMLib.Alvo.Data.EntityFrameworkCore/Internal/EfAlvoData.cs` (`EmitAsync`, `:1640`)
- Test: `src/MMLib.Alvo.Testing/Data/AlvoDataOutboxTests.cs`

- [ ] **Step 1: Write the failing facts**

Two facts, each asserting the sequence with `world.EventsAsync()` and
`events.Select(e => e.Type).ShouldBe([...])`, in the file's existing style:

- a replacement that created emits exactly `entity.vehicles.created`;
- a replacement that replaced emits `entity.vehicles.created` then `entity.vehicles.updated`, and the
  update's `Data.OldRecord` carries the pre-image while `Data.Record` carries the post-image.

Add a `<remarks>` saying why there is no third type: a new `entity.x.replaced` would make every existing
`updated` subscriber silently incomplete.

- [ ] **Step 2: Run, implement, run**

Each branch calls `EmitAsync` with its own `DataOperation` — `Create` with `previous: null`, `Update` with
the pre-image — inside the write's own transaction, after the re-read, so a refused write leaves no event.

- [ ] **Step 3: Verify by injection**

Make the create branch emit `DataOperation.Update` and confirm only the created fact goes red.

- [ ] **Step 4: Commit**

```bash
git add -u && git commit -m "feat(events): a replacement emits its branch's own event"
```

---

### Task 10: Docs

- [ ] **Step 1: `docs/architecture/data-api.md`**

Add the `PUT` section beside `PATCH`. **Move `PUT, and PUT-as-upsert` out of "Alternatives rejected"** — it
is no longer rejected — and put in its place what is: `If-None-Match: *`, the mixed batch, the natural-key
upsert, and the composite `(tenant_id, id)` key. Record §3's residual oracle here too; `data-api.md` is where
a reader looks for what an endpoint discloses.

- [ ] **Step 2: `docs/architecture/data-path.md`**

The branch in the write path's walkthrough: the locked policy-scoped read, the two branches, `WITH CHECK` on
the candidate in both, and the guard's unconditional `isUpdate: true` with its one-sentence reason.

- [ ] **Step 3: `docs/architecture/events.md`** — one paragraph: the branch's own type, and deliberately no
`replaced`.

- [ ] **Step 4: Commit**

```bash
git add -u && git commit -m "docs(architecture): PUT is no longer an alternative rejected"
```

---

### Task 11: E2E

**Files:** Create `test/teapie-field-service/120-Replace/`

- [ ] **Step 1: Write the pins**

Four requests against `work_orders`: a `PUT` on a free id → `201` + `Location`; the same `PUT` again → `200`;
a `PUT` omitting `description` (nullable) after it was set → the field comes back `null`; and a `PUT`
omitting `access_code` — `required: true` **and** `hidden: true`
(`examples/field-service/field-service.alvo.json:165-171`) — → `422` naming the field.

Use `description`, **not** `internal_notes`: a hidden field is masked out of the response, so asserting it is
`null` would pass vacuously whether or not the code works.

- [ ] **Step 2: Run** — `scripts/test-e2e`. The standalone host adds `traceId` to problem documents, so
assert on the fields you mean rather than on the whole body.

- [ ] **Step 3: Commit**

```bash
git add -u && git commit -m "test(e2e): pin PUT, including the entity a caller cannot restate"
```

---

### Task 12: Follow-ups, and the PR

- [ ] **Step 1: File four follow-up issues**, each quoting the design section that argued it: the composite
`(tenant_id, id)` key (quoting `DescriptorModelBuilder.cs:64-72` and the §3 deviation table);
`If-None-Match: *`; batch/mixed upsert; upsert on a natural unique key.

- [ ] **Step 2: Run everything** — `scripts/test-ring2`, `scripts/test-e2e`, and the PostgreSQL integration
leg on its own, since ring2 is affected-scoped.

- [ ] **Step 3: Reviewer subagents, on a frozen tree.** Dispatch only once you have stopped editing — a
reviewer reading a tree you are still changing gives a verdict that does not cover the final diff. Pair the
security pass with `alvo-security-core-review`; this diff is squarely in the security core.

- [ ] **Step 4: `alvo-plan-guard`, then `alvo-pr-report`, then the PR**, in that order per CLAUDE.md. The PR
body: `Closes #105.`

- [ ] **Step 5: After the merge.** Sync `main`, delete the local **and** remote branch, verify #105 actually
closed. Then read the post-merge mutation run: the `data-ef` shard took 110 minutes against a 120-minute
ceiling after PR-H, and this PR adds to `EfAlvoData.cs` again. If it is cancelled, that shard needs splitting
or a higher timeout before the next data PR.

---

## Self-review

**Spec coverage.** §0 → Tasks 3, 7. §1 → Task 3. §2 → Tasks 1, 3. §3 → Tasks 2, 3. §4 → Tasks 3 (both
decisions, both `ReadOnlyFields`, masking by the write decision), 7 (both operations). §5 → Tasks 3, 4.
§6 → Task 5. §7 → Tasks 5, 6, 9. §8 → Tasks 7 (five switches), 8. §9 → Tasks 10, 12. §10 → the injection step
in Tasks 1, 3, 4, 5, 6, 7, 9. §11 → the baseline acceptance in Tasks 1 and 3.

**Type consistency.** `AlvoReplaceResult(AlvoRecord Row, bool Created)` with `CreatedRow`/`ReplacedRow` is
used identically in Tasks 1, 3, 6, 7. `ReplaceAsync`'s signature is written once in Task 3 and called with
the same parameter order everywhere. `TranslatedAsync(…, bool callerKeyed)` is defined in Task 2 and consumed
in Task 3.

**What the first draft of this plan got wrong, kept here so it is not repeated.** It invented `Data`,
`Editor`, `OtherTenantEditor`, `RefusedByCheck`, `work_orders` in the port suite, a `notes` field,
`PayloadWithout`, `Token`, `Client`, `CreatedRowId`, `Body`, `LastEvent`, and `AlvoPrecondition.Match` —
none of which exist. It cited `Row`/`Created` on `ProblemResultFactory` when they are private members of
`DataApiEndpoints`. It missed `FaultingAlvoData`, a third `IAlvoData` implementor that breaks the build. It
missed the three concrete per-driver subclasses, without which the new suite runs **zero tests and reports
green**. It said "three switches" when there are five, and the two it missed fail silently. And it described
`RecordValidator` as needing a new mode when `RecordValidationRequest.IsCreate` already is that mode. Every
one of those was found by reading the repo rather than the plan.
