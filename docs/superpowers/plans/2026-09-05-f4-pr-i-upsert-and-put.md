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
  A branch that skips it is a policy bypass, not a bug.
- **`id` in a request body stays refused on every route**, this one included. Only the path carries it.
- **`WritePayloadGuard` is called with `isUpdate: true` unconditionally on this route** (spec §5) — a
  branch-dependent value is an existence oracle decided from the payload alone.
- **The pre-image is read under the *update* decision's `Using`** (spec §4). Reading it under a `create`
  decision is the F3 PR3 bypass, verbatim (`PolicyDecision.cs:62-69`).
- **Assertions use Shouldly.** Never FluentAssertions. Tests are descriptive `snake_case` in the inherited
  suites (see the `.editorconfig` files that scope `CA1707`).
- **Any `.cs` written through bash/python must be CRLF + UTF-8 BOM**, or the pre-commit `dotnet format` task
  fails. Prefer the editing tools.
- **`scripts/test-ring0` after every step that touches code**; `scripts/test-ring2` before the PR.
- **Verify each test by injecting the bug it claims to catch.** A test that stays green when you delete the
  code it covers is a vacuous test. PR-H found three this way.
- Every deliberate deviation from a source goes into the design doc, not into a code comment.

---

## File structure

**Created**

| File | Responsibility |
|---|---|
| `src/MMLib.Alvo.Abstractions/Data/AlvoReplaceResult.cs` | the port's answer: the row, and whether it was created |
| `test/MMLib.Alvo.Abstractions.Tests/AlvoReplaceResultTests.cs` | the states that type refuses |
| `src/MMLib.Alvo.Testing/Data/AlvoDataReplaceTests.cs` | the inherited contract, held by all three drivers |
| `test/teapie-field-service/120-Replace/` | the E2E pins |

**Modified**

| File | Change |
|---|---|
| `src/MMLib.Alvo.Abstractions/Data/IAlvoData.cs` | `ReplaceAsync` + its contract remarks |
| `src/MMLib.Alvo.Abstractions/Data/AlvoConstraintViolationException.cs` | the managed-column exclusion says *which* writes it speaks for |
| `src/MMLib.Alvo.Testing/Data/InMemoryAlvoData.cs` | the reference implementation |
| `src/MMLib.Alvo.Data.EntityFrameworkCore/Internal/EfAlvoData.cs` | `ReplaceAsync`, the branch, the candidate builder |
| `src/MMLib.Alvo.Data.EntityFrameworkCore/Internal/ConstraintViolationTranslator.cs` | a caller-keyed write keeps `id` in `CallerFields` |
| `src/MMLib.Alvo/Api/Internal/DataApiEndpointKind.cs` | `Replace` + its `ToWireName` arm |
| `src/MMLib.Alvo/Api/Internal/DataApiEndpoints.cs` | `MapReplace`, gating both operations |
| `src/MMLib.Alvo/Api/Internal/DataApiDocumentation.cs` | three switches gain a `Replace` arm |
| `src/MMLib.Alvo/Api/Internal/SchemaComponentBuilder.cs` | the request body component |
| `src/MMLib.Alvo/Api/Internal/RecordValidator.cs` | the "every required field present" mode |
| `docs/architecture/data-api.md`, `data-path.md`, `events.md` | the route, the branch, the events |

---

### Task 1: `AlvoReplaceResult`

**Files:**
- Create: `src/MMLib.Alvo.Abstractions/Data/AlvoReplaceResult.cs`
- Test: `test/MMLib.Alvo.Abstractions.Tests/AlvoReplaceResultTests.cs`

**Interfaces:**
- Consumes: `AlvoRecord` (existing, `src/MMLib.Alvo.Abstractions/Data/AlvoRecord.cs`).
- Produces: `AlvoReplaceResult(AlvoRecord Row, bool Created)` with get-only `Row` and `Created`, and two
  factories `AlvoReplaceResult.CreatedRow(AlvoRecord row)` / `AlvoReplaceResult.ReplacedRow(AlvoRecord row)`.
  Task 3 returns it; Task 7 branches on `Created` for `201` vs `200`.

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
    /// <remarks>
    /// The lesson <see cref="AlvoBatchResult"/> learned: an <c>init</c> setter is reachable from a
    /// <c>with</c>, which does not run the constructor, so a validated type with <c>init</c> members
    /// validates only the paths nobody was going to take.
    /// </remarks>
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
Expected: FAIL — `AlvoReplaceResult` does not exist (build error CS0246).

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

- [ ] **Step 4: Run it and watch it pass**

Run the same command. Expected: PASS, 3 tests.

- [ ] **Step 5: Verify the `with` test is not vacuous**

Temporarily change `public AlvoRecord Row { get; }` to `{ get; init; }`, re-run, and confirm
`The_members_are_not_settable` goes **red** naming `Row`. Then revert. If it stays green, the reflection
test is looking at the wrong thing — fix it before continuing.

- [ ] **Step 6: Accept the public-API baseline**

Run `scripts/test-ring1`. The Abstractions baseline grows by `AlvoReplaceResult` and its synthesized record
members. The `turn-review-gate` Stop hook will fire because a `PublicApi.*.verified.txt` grew — answer it
against the design's §11 table, which lists exactly these symbols.

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

**Files:**
- Modify: `src/MMLib.Alvo.Data.EntityFrameworkCore/Internal/ConstraintViolationTranslator.cs:142-149`
- Modify: `src/MMLib.Alvo.Abstractions/Data/AlvoConstraintViolationException.cs:26-32`
- Test: `test/MMLib.Alvo.Data.EntityFrameworkCore.Tests/ConstraintViolationTranslatorTests.cs`

**Interfaces:**
- Produces: `ConstraintViolationTranslator.Translate(..., bool callerKeyed)` — when `callerKeyed` is true,
  `id` survives `CallerFields` and the violation translates to `AlvoConstraintKind.Unique` with
  `Fields: ["id"]`. Task 3 passes `callerKeyed: true` from the create branch and nothing else does.

- [ ] **Step 1: Write the failing tests**

Add to `ConstraintViolationTranslatorTests.cs`:

```csharp
/// <summary>A collision on an id the caller supplied is a conflict, not a broken invariant.</summary>
/// <remarks>
/// The exclusion of framework-managed columns reads "because a caller cannot change one". On the
/// create-or-replace route the caller supplies the id, so the premise expires and the collision is an
/// ordinary conflict — the caller can act on it by choosing another id.
/// </remarks>
[Fact]
public void A_caller_keyed_collision_on_id_translates_to_a_unique_conflict()
{
    var translated = Translate(ViolationOn("id"), callerKeyed: true);

    translated.ShouldNotBeNull();
    translated.Kind.ShouldBe(AlvoConstraintKind.Unique);
    translated.Fields.ShouldBe(["id"]);
}

/// <summary>And a collision on an id the framework minted still propagates as the invariant break it is.</summary>
[Fact]
public void A_framework_minted_collision_on_id_is_still_untranslated()
{
    Translate(ViolationOn("id"), callerKeyed: false).ShouldBeNull(
        "every route but create-or-replace mints its own key, so a collision there is not the caller's");
}

/// <summary>tenant_id is never caller-keyed, on any route.</summary>
[Fact]
public void A_collision_on_tenant_id_is_untranslated_even_on_a_caller_keyed_write()
{
    Translate(ViolationOn("tenant_id"), callerKeyed: true).ShouldBeNull(
        "the flag says the caller supplied the row key, not that it supplied every managed column");
}
```

`ViolationOn` and `Translate` are local helpers built the way the existing tests in this file build them —
read the file's existing arrange helpers and reuse them rather than inventing a second shape.

- [ ] **Step 2: Run and watch it fail**

Run: `dotnet test --project test/MMLib.Alvo.Data.EntityFrameworkCore.Tests/MMLib.Alvo.Data.EntityFrameworkCore.Tests.csproj --filter-query "/*/*/ConstraintViolationTranslatorTests/*"`
Expected: FAIL — `Translate` has no `callerKeyed` parameter (CS1739).

- [ ] **Step 3: Thread the flag through**

In `ConstraintViolationTranslator.cs`, add `bool callerKeyed` to `Translate` and to `TranslatedAsync`, and
pass it into `CallerFields`. In `CallerFields`, the managed-column strip keeps `id` when `callerKeyed` is
true and nothing else changes:

```csharp
private static IReadOnlyList<string> CallerFields(
    SqlConstraintViolation violation, IEntityType rows, EntitySchema schema, bool callerKeyed)
```

and where the strip happens, exempt exactly `AlvoManagedColumns.Id` and only when `callerKeyed`. Do **not**
exempt `tenant_id`: the flag says the caller supplied the row key, not every managed column.

Give the parameter a `<param>` doc that states the premise it encodes, in one sentence.

- [ ] **Step 4: Amend the public remark it contradicts**

`AlvoConstraintViolationException.cs:26-32` currently reads "…because a caller cannot change one — a
collision confined to them is a broken invariant rather than a conflict, and an implementation must let that
keep propagating as one." Extend it to say which writes it speaks for: every write whose key the framework
mints, which is all of them except create-or-replace, where the caller supplies `id` and a collision on it
is the caller's to fix. Do not weaken the rule for `tenant_id` or the audit columns.

- [ ] **Step 5: Run and watch it pass**

Same command. Expected: PASS.

- [ ] **Step 6: Verify by injection**

Remove the `callerKeyed` exemption from `CallerFields` and confirm
`A_caller_keyed_collision_on_id_translates_to_a_unique_conflict` goes red **and** the other two stay green.
Restore. If the second test also flips, the exemption is too wide.

- [ ] **Step 7: Run ring0 and commit**

```bash
scripts/test-ring0
git add src/MMLib.Alvo.Data.EntityFrameworkCore/Internal/ConstraintViolationTranslator.cs \
        src/MMLib.Alvo.Abstractions/Data/AlvoConstraintViolationException.cs \
        test/MMLib.Alvo.Data.EntityFrameworkCore.Tests/ConstraintViolationTranslatorTests.cs
git commit -m "fix(data): a collision on an id the caller supplied is a conflict, not an invariant break"
```

---

### Task 3: The port member and both implementations

The core. Spec §2, §3, §4, §5. A reviewer cannot approve one branch without the other, which is why this is
one task.

**Files:**
- Modify: `src/MMLib.Alvo.Abstractions/Data/IAlvoData.cs` (after `UpdateAsync`, `:433`)
- Modify: `src/MMLib.Alvo.Testing/Data/InMemoryAlvoData.cs`
- Modify: `src/MMLib.Alvo.Data.EntityFrameworkCore/Internal/EfAlvoData.cs`
- Create: `src/MMLib.Alvo.Testing/Data/AlvoDataReplaceTests.cs`
- Modify: `src/MMLib.Alvo.Testing/Data/.editorconfig` (add the new file to the `CA1707` scope list)

**Interfaces:**
- Consumes: `AlvoReplaceResult` (Task 1); `ConstraintViolationTranslator.Translate(..., callerKeyed)` (Task 2).
- Produces:
  ```csharp
  Task<AlvoReplaceResult> ReplaceAsync(
      string entity, Guid id, IReadOnlyDictionary<string, object?> values, AlvoContext context,
      AlvoPrecondition? precondition = null, AlvoIdempotency? idempotency = null,
      CancellationToken cancellationToken = default);
  ```
  Tasks 4–7 extend its behaviour; Task 7 calls it from the endpoint.

- [ ] **Step 1: Write the inherited contract test for the create branch**

`src/MMLib.Alvo.Testing/Data/AlvoDataReplaceTests.cs`, following the shape of the sibling
`AlvoDataBatchTests.cs` (same base class, same fixture, same `[Fact]` naming):

```csharp
/// <summary>A PUT on an id no row holds creates that row, with that id.</summary>
[Fact]
public async Task Replacing_an_absent_row_creates_it_under_the_caller_s_own_id()
{
    var id = Guid.CreateVersion7();

    var result = await Data.ReplaceAsync(
        "work_orders", id, Payload(title: "Fix the boiler"), Editor);

    result.Created.ShouldBeTrue();
    result.Row.Values["id"].ShouldBe(id, "the path's id is the row's id");
}
```

- [ ] **Step 2: Write the one test this whole PR exists for**

```csharp
/// <summary>WITH CHECK judges the candidate on the create branch, not only on the replace branch.</summary>
/// <remarks>
/// An upsert that checks the update branch and lets the create branch through is a policy bypass. This is
/// the failure this design is most likely to have, so it is asserted on both branches from one rule.
/// </remarks>
[Fact]
public async Task The_write_check_refuses_the_candidate_on_the_create_branch()
{
    var id = Guid.CreateVersion7();

    await Should.ThrowAsync<AlvoAuthorizationException>(
        () => Data.ReplaceAsync("work_orders", id, Payload(title: "Refused by the rule"), RefusedByCheck));
}

/// <summary>…and on the replace branch, from the same rule.</summary>
[Fact]
public async Task The_write_check_refuses_the_candidate_on_the_replace_branch()
{
    var existing = await Data.CreateAsync("work_orders", Payload(title: "Already here"), Editor);
    var id = (Guid)existing.Values["id"]!;

    await Should.ThrowAsync<AlvoAuthorizationException>(
        () => Data.ReplaceAsync("work_orders", id, Payload(title: "Refused by the rule"), RefusedByCheck));
}
```

`RefusedByCheck` is a context whose resolved policy carries a `WITH CHECK` no candidate satisfies — build it
the way `AlvoDataAdversarialTests` builds its refusing contexts.

- [ ] **Step 3: Write the replace-branch test**

```csharp
/// <summary>A PUT on an id a visible row holds replaces that row rather than creating a second.</summary>
[Fact]
public async Task Replacing_a_visible_row_replaces_it()
{
    var existing = await Data.CreateAsync("work_orders", Payload(title: "First"), Editor);
    var id = (Guid)existing.Values["id"]!;

    var result = await Data.ReplaceAsync("work_orders", id, Payload(title: "Second"), Editor);

    result.Created.ShouldBeFalse();
    result.Row.Values["title"].ShouldBe("Second");
    result.Row.Values["id"].ShouldBe(id, "a replaced row is the same row");
}
```

- [ ] **Step 4: Write the invisible-row test**

```csharp
/// <summary>An id held by a row the caller cannot see answers a conflict, never a silent overwrite.</summary>
/// <remarks>
/// The residual oracle the design records in §3: the caller learns the id is taken. What must never happen
/// is the other two outcomes — writing over a row their USING excludes, or reporting success.
/// </remarks>
[Fact]
public async Task Replacing_a_row_the_caller_cannot_see_is_refused_and_writes_nothing()
{
    var hidden = await Data.CreateAsync("work_orders", Payload(title: "Someone else's"), OtherTenantEditor);
    var id = (Guid)hidden.Values["id"]!;

    await Should.ThrowAsync<AlvoConstraintViolationException>(
        () => Data.ReplaceAsync("work_orders", id, Payload(title: "Mine now"), Editor));

    var stillTheirs = await Data.GetAsync("work_orders", id, OtherTenantEditor);
    stillTheirs!.Values["title"].ShouldBe("Someone else's");
}
```

- [ ] **Step 5: Run and watch every one of them fail**

Run: `scripts/test-ring0`
Expected: build failure — `ReplaceAsync` is not a member of `IAlvoData`.

- [ ] **Step 6: Declare the port member**

In `IAlvoData.cs`, after `UpdateAsync` (`:433`), add the signature from **Interfaces** above with XML docs
that state, at minimum: that the path's `id` is the row's identity and `id` in `values` is still refused;
that `WITH CHECK` is evaluated on the candidate in both branches; that a row excluded by `USING` takes the
create branch and its collision surfaces as `AlvoConstraintViolationException`; that a replay answers
`Created: false`; and that omitted fields are **not** preserved (pointing at `UpdateAsync` for the partial
write).

- [ ] **Step 7: Implement it in `InMemoryAlvoData`**

The reference implementation. Resolve **both** decisions (`DataOperation.Create` and `DataOperation.Update`)
and deny unless both allow. Read the pre-image under the **update** decision's `Using`. Found → replace;
absent → create. Run `WritePayloadGuard`-equivalent checks with `isUpdate: true`. Evaluate `WITH CHECK` on
the candidate in both branches, with `previous: null` on create and the pre-image on replace. Stamp with
`isUpdate: false` on create and `isUpdate: true` on replace.

- [ ] **Step 8: Implement it in `EfAlvoData`**

`ReplaceAsync` mirrors `UpdateAsync`'s outer shape (`:922-957`): validate, `AlvoIdempotency.EnsureUsableToken`,
resolve both decisions, then `ReplacedAsync` opens the context, the outbox table and the transaction and
calls a new `ReplaceWriteAsync`.

`ReplaceWriteAsync` follows `WriteAsync` (`:1320-1345`) up to the not-found point and then branches:

- `SingleAsync(db, schema, updateDecision, context, id, PreImageMutation.Update, …, unmasked: true)`.
- **Row returned** → the existing replace path: precondition, `RunBeforeUpdate`, `EnsureWriteAllowed` over
  the merged post-image, `AffectedAsync`, re-read.
- **No row** → the create path: build the candidate with the caller's `id` rather than a minted one, run
  the guard with `isUpdate: true` and the stamp with `isUpdate: false`, `EnsureWriteAllowed` with
  `previous: null`, `RunBeforeCreate`'s re-verdict, then insert with `callerKeyed: true` threaded into
  `ConstraintViolationTranslator`.

**Do not reuse `AuthorizedCandidate`** (`:352-363`): it passes one `isUpdate` to both the guard and the
stamp, and this route needs `true` for the guard and `false` for the stamp (spec §5). Write a sibling that
takes the two separately, and give each parameter a name that says which question it answers.

- [ ] **Step 9: Run and watch them pass**

Run: `scripts/test-ring0`, then `scripts/test-ring2` for the SQLite and PostgreSQL legs.
Expected: PASS on all three drivers, from one inherited suite.

- [ ] **Step 10: Verify the two `WITH CHECK` tests by injection**

Delete the `EnsureWriteAllowed` call on the **create** branch only. Confirm
`The_write_check_refuses_the_candidate_on_the_create_branch` goes red on all three drivers and the replace
one stays green. Restore, then do the mirror image on the replace branch. **Confirm the build had 0 errors
before trusting either result** — a failed build tests a stale binary and reports a meaningless pass.

- [ ] **Step 11: Commit**

```bash
scripts/test-ring0
git add src/MMLib.Alvo.Abstractions/Data/IAlvoData.cs \
        src/MMLib.Alvo.Testing/Data/InMemoryAlvoData.cs \
        src/MMLib.Alvo.Testing/Data/AlvoDataReplaceTests.cs \
        src/MMLib.Alvo.Testing/Data/.editorconfig \
        src/MMLib.Alvo.Data.EntityFrameworkCore/Internal/EfAlvoData.cs \
        test/MMLib.Alvo.Tests/PublicApi.MMLib.Alvo.Testing.verified.txt \
        test/MMLib.Alvo.Abstractions.Tests/PublicApi.MMLib.Alvo.Abstractions.verified.txt
git commit -m "feat(data): create-or-replace, with the write check on the candidate in both branches"
```

---

### Task 4: `tenant_id` is refused on both branches

Spec §5. The oracle this closes is decided from the payload alone, which makes it worse than §3's.

**Files:**
- Test: `src/MMLib.Alvo.Testing/Data/AlvoDataReplaceTests.cs`

**Interfaces:**
- Consumes: `ReplaceAsync` (Task 3).
- Produces: nothing new — this task pins behaviour Task 3 built.

- [ ] **Step 1: Write the failing test**

```csharp
/// <summary>tenant_id is refused on this route whether or not the row exists.</summary>
/// <remarks>
/// tenant_id is caller-writable on a create and refused on an update. If this route asked that question
/// per branch, "is my tenant_id refused?" would answer "does this row exist?" — an existence oracle decided
/// from the payload alone, before any row is read. So the guard is told isUpdate: true unconditionally.
/// </remarks>
[Theory]
[InlineData(true)]
[InlineData(false)]
public async Task Tenant_id_in_the_payload_is_refused_whether_or_not_the_row_exists(bool rowExists)
{
    var id = Guid.CreateVersion7();
    if (rowExists)
    {
        var existing = await Data.CreateAsync("work_orders", Payload(title: "Here"), Editor);
        id = (Guid)existing.Values["id"]!;
    }

    var refusal = await Should.ThrowAsync<AlvoAuthorizationException>(
        () => Data.ReplaceAsync("work_orders", id, PayloadWithTenant(Editor.Tenant.Value), Editor));

    refusal.Message.ShouldContain("tenant_id");
}
```

- [ ] **Step 2: Run and confirm it already passes**

Run: `scripts/test-ring0`
Expected: PASS both cases, because Task 3 already passes `isUpdate: true`. **A test that passes on the first
run has proven nothing yet** — go to Step 3 before believing it.

- [ ] **Step 3: Verify by injection — this is the whole task**

Change the guard call on the create branch to `isUpdate: false`. Confirm the `rowExists: false` case goes
**red** and the `rowExists: true` case stays green. That divergence *is* the oracle, made visible. Restore
and confirm both go green again.

- [ ] **Step 4: Commit**

```bash
git add src/MMLib.Alvo.Testing/Data/AlvoDataReplaceTests.cs \
        test/MMLib.Alvo.Tests/PublicApi.MMLib.Alvo.Testing.verified.txt
git commit -m "test(data): pin that a branch-dependent tenant_id refusal cannot come back"
```

---

### Task 5: Replacement semantics for omitted fields

Spec §6.

**Files:**
- Modify: `src/MMLib.Alvo.Data.EntityFrameworkCore/Internal/EfAlvoData.cs` (the replace branch's candidate)
- Modify: `src/MMLib.Alvo.Testing/Data/InMemoryAlvoData.cs`
- Modify: `src/MMLib.Alvo/Api/Internal/RecordValidator.cs`
- Test: `src/MMLib.Alvo.Testing/Data/AlvoDataReplaceTests.cs`

**Interfaces:**
- Consumes: `ReplaceAsync` (Task 3), `EntitySchema.Fields` with `Required`, `Nullable`,
  `ComputedExpression` (`FieldSchema.cs`).
- Produces: `RecordValidator` gains a mode in which a missing `required` field is a violation; Task 7 calls
  it in that mode.

- [ ] **Step 1: Write the failing tests**

```csharp
/// <summary>A field the replacement omits does not keep its stored value.</summary>
/// <remarks>
/// This is what separates PUT from PATCH. Preserving it would make two identical PUTs from two different
/// starting states produce two different rows — which is why the assertion starts from a row that has the
/// field set. One starting state cannot tell replacement from a merge.
/// </remarks>
[Fact]
public async Task A_nullable_field_the_replacement_omits_becomes_null()
{
    var existing = await Data.CreateAsync(
        "work_orders", Payload(title: "First", notes: "Ring the bell"), Editor);
    var id = (Guid)existing.Values["id"]!;

    var replaced = await Data.ReplaceAsync("work_orders", id, Payload(title: "Second"), Editor);

    replaced.Row.Values["notes"].ShouldBeNull("a replacement replaces; it does not merge");
}

/// <summary>created_at and created_by survive a replacement, because it is the same row.</summary>
[Fact]
public async Task The_creation_stamp_survives_a_replacement()
{
    var existing = await Data.CreateAsync("work_orders", Payload(title: "First"), Editor);
    var id = (Guid)existing.Values["id"]!;

    var replaced = await Data.ReplaceAsync("work_orders", id, Payload(title: "Second"), Editor);

    replaced.Row.Values["created_at"].ShouldBe(existing.Values["created_at"]);
    replaced.Row.Values["created_by"].ShouldBe(existing.Values["created_by"]);
}

/// <summary>A required field the replacement omits is refused, naming the field.</summary>
[Fact]
public async Task A_required_field_the_replacement_omits_is_refused()
{
    var existing = await Data.CreateAsync("work_orders", Payload(title: "First"), Editor);
    var id = (Guid)existing.Values["id"]!;

    var refusal = await Should.ThrowAsync<ArgumentException>(
        () => Data.ReplaceAsync("work_orders", id, PayloadWithout("title"), Editor));

    refusal.Message.ShouldContain("title");
}
```

- [ ] **Step 2: Run and watch them fail**

Run: `scripts/test-ring0`. Expected: FAIL — the first because the omitted field is currently preserved, the
third because nothing refuses it yet.

- [ ] **Step 3: Implement the replacement candidate**

On the replace branch, the candidate is built from the payload **plus** the framework-managed columns read
off the pre-image — not from `Merge(preImage, values)`, which is `UpdateAsync`'s partial semantics. Every
descriptor field the payload omits is written as `null`; every `computed` field is omitted entirely so the
engine keeps owning it; `created_at`/`created_by` are copied from the pre-image; `updated_at`/`updated_by`
are stamped.

Extract the "which columns come from the pre-image" question into a named method rather than a comment.

- [ ] **Step 4: Implement the required-field refusal**

In `RecordValidator`, add the mode that reports a missing `required` field as a violation, with a message
naming the field and suggesting `PATCH`. Mirror it in the port so a direct port caller gets the same
refusal — a rule enforced only at the HTTP layer is a rule an embedded host does not have.

State in the message that a `required` + `hidden` field makes the entity un-replaceable for a caller who
cannot read it, since that is the case the caller will actually hit and the fix is different.

- [ ] **Step 5: Run and watch them pass**

Run: `scripts/test-ring0`, then `scripts/test-ring2`.

- [ ] **Step 6: Verify by injection**

Change the omitted-field rule back to `Merge(preImage, values)` and confirm
`A_nullable_field_the_replacement_omits_becomes_null` goes red while
`The_creation_stamp_survives_a_replacement` stays green — if both flip, the candidate builder is also
dropping the creation stamp, which is a second bug.

- [ ] **Step 7: Commit**

```bash
git add -u && git commit -m "feat(data): a replacement replaces, and refuses a body that cannot express the row"
```

---

### Task 6: Idempotency, preconditions, and the replay's answer

Spec §7.

**Files:**
- Modify: `src/MMLib.Alvo.Data.EntityFrameworkCore/Internal/EfAlvoData.cs`
- Test: `src/MMLib.Alvo.Testing/Data/AlvoDataReplaceTests.cs`

**Interfaces:**
- Consumes: `ReplayableWriteAsync` (`:424-450`), `IdempotencyScope` (`:726-743`), `AlvoPrecondition.EnsureMatches`.
- Produces: nothing new on the port.

- [ ] **Step 1: Write the failing tests**

```csharp
/// <summary>The same PUT twice leaves the same state — from two different starting states.</summary>
/// <remarks>
/// One starting state cannot tell replacement from a merge, so the property is asserted from two. This is
/// the spec's own acceptance criterion for the API invariant suite (alvo-specifikacia.md:309).
/// </remarks>
[Fact]
public async Task The_same_replacement_from_two_starting_states_lands_on_one_row()
{
    var sparse = await Data.CreateAsync("work_orders", Payload(title: "A"), Editor);
    var full = await Data.CreateAsync("work_orders", Payload(title: "B", notes: "noisy"), Editor);

    var one = await Data.ReplaceAsync("work_orders", (Guid)sparse.Values["id"]!, Payload(title: "Same"), Editor);
    var two = await Data.ReplaceAsync("work_orders", (Guid)full.Values["id"]!, Payload(title: "Same"), Editor);

    one.Row.Values["notes"].ShouldBe(two.Row.Values["notes"]);
    one.Row.Values["title"].ShouldBe(two.Row.Values["title"]);
}

/// <summary>A replay reports the state a previous request left, never an act of creation.</summary>
[Fact]
public async Task A_replayed_replacement_answers_that_it_created_nothing()
{
    var id = Guid.CreateVersion7();
    var token = Token("put-once");

    var first = await Data.ReplaceAsync("work_orders", id, Payload(title: "Once"), Editor, idempotency: token);
    var replay = await Data.ReplaceAsync("work_orders", id, Payload(title: "Once"), Editor, idempotency: token);

    first.Created.ShouldBeTrue();
    replay.Created.ShouldBeFalse("201 reports an act of creation and a replay performs none");
    replay.Row.Values["id"].ShouldBe(id);
}

/// <summary>If-Match on the create branch cannot match anything.</summary>
[Fact]
public async Task An_if_match_on_an_absent_row_fails_its_precondition()
{
    await Should.ThrowAsync<AlvoPreconditionFailedException>(
        () => Data.ReplaceAsync(
            "work_orders", Guid.CreateVersion7(), Payload(title: "Nope"), Editor,
            precondition: AlvoPrecondition.Match(DateTimeOffset.UtcNow)));
}
```

- [ ] **Step 2: Run and watch them fail**

Run: `scripts/test-ring0`.

- [ ] **Step 3: Wire idempotency and the precondition**

Route `ReplaceAsync` through `ReplayableWriteAsync` when a token is present, exactly as
`ReplayableCreateAsync` (`:405`) does, with the idempotency lookup inside the write's own transaction and
the record inserted **last**, after the outbox emit. On the replace branch call
`AlvoPrecondition.EnsureMatches(precondition, StoredVersion(schema, stored))` after the not-found check and
before `WITH CHECK`, preserving the existing ordering. On the create branch let
`EnsureMatches(precondition, null)` do its own work — it already throws
`AlvoPreconditionFailedException` for a version match against nothing.

A replay returns `AlvoReplaceResult.ReplacedRow(...)` unconditionally.

- [ ] **Step 4: Run, then verify by injection**

Run `scripts/test-ring0` and `scripts/test-ring2`. Then change the replay to return `CreatedRow(...)` and
confirm `A_replayed_replacement_answers_that_it_created_nothing` goes red. Restore.

- [ ] **Step 5: Commit**

```bash
git add -u && git commit -m "feat(data): Idempotency-Key and If-Match on create-or-replace"
```

---

### Task 7: The route

Spec §4, §8. **Three switches must gain an arm or every route in the document disappears.**

**Files:**
- Modify: `src/MMLib.Alvo/Api/Internal/DataApiEndpointKind.cs:44-51` and `:94-101`
- Modify: `src/MMLib.Alvo/Api/Internal/DataApiEndpoints.cs` (`Map` at `:64-85`, new `MapReplace`)
- Modify: `src/MMLib.Alvo/Api/Internal/DataApiDocumentation.cs` (`:158`, `:344`, `:366`)
- Test: `test/MMLib.Alvo.Api.Tests/DataApiReplaceTests.cs`

**Interfaces:**
- Consumes: `IAlvoData.ReplaceAsync` (Task 3), `AlvoReplaceResult.Created` (Task 1),
  `ProblemResultFactory.Created`/`Row` (`ProblemResultFactory.cs:1272`, `:1252`).
- Produces: `DataApiEndpointKind.Replace`, mapped `PUT` on `{prefix}/{entity}/{id:guid}`.

- [ ] **Step 1: Write the failing tests**

`test/MMLib.Alvo.Api.Tests/DataApiReplaceTests.cs`:

```csharp
[Fact]
public async Task A_put_on_a_free_id_answers_201_with_a_location()
{
    var id = Guid.CreateVersion7();

    var response = await Client.PutAsJsonAsync($"/data/work_orders/{id}", Body(title: "New"));

    response.StatusCode.ShouldBe(HttpStatusCode.Created);
    response.Headers.Location!.ToString().ShouldEndWith(id.ToString());
}

[Fact]
public async Task A_put_on_an_existing_row_answers_200_and_no_location()
{
    var id = await CreatedRowId();

    var response = await Client.PutAsJsonAsync($"/data/work_orders/{id}", Body(title: "Replaced"));

    response.StatusCode.ShouldBe(HttpStatusCode.OK);
    response.Headers.Location.ShouldBeNull();
}

/// <summary>Adding a kind without extending the document's switches makes every route vanish.</summary>
/// <remarks>
/// AlvoEndpointDataSource.BuildOrRefuseToRoute catches the InvalidOperationException the switches throw,
/// so a missing arm does not fail loudly — it empties the route table. 229 tests went red on exactly this
/// during PR-H. This counts the routes so the failure is one test, not all of them.
/// </remarks>
[Fact]
public async Task Every_verb_on_the_item_route_is_still_mapped()
{
    var document = await OpenApiDocument();

    document.Paths["/data/work_orders/{id}"].Operations.Keys
        .ShouldBe([HttpMethod.Get, HttpMethod.Put, HttpMethod.Patch, HttpMethod.Delete], ignoreOrder: true);
}

/// <summary>The operationId does not collide with update's.</summary>
[Fact]
public async Task The_replace_operation_has_an_id_of_its_own()
{
    var document = await OpenApiDocument();
    var ids = document.Paths.Values.SelectMany(path => path.Operations.Values).Select(o => o.OperationId);

    ids.ShouldBeUnique();
}
```

- [ ] **Step 2: Run and watch them fail**

Run: `dotnet test --project test/MMLib.Alvo.Api.Tests/MMLib.Alvo.Api.Tests.csproj --filter-query "/*/*/DataApiReplaceTests/*"`

- [ ] **Step 3: Add the kind and its wire name**

In `DataApiEndpointKind.cs`, add `Replace` with a `<summary>` saying it is the create-or-replace, and add an
explicit arm to `ToWireName`:

```csharp
DataApiEndpointKind.Replace => "replace",
```

**The explicit arm is mandatory.** Without it the default arm falls through to
`ToDataOperation().ToWireName()` and spells it `"update"`, colliding with the single-row `PATCH` — two routes
minting one `operationId`, with one route's prose published for the other.

Add `DataApiEndpointKind.Replace => DataOperation.Update` to `ToDataOperation`.

- [ ] **Step 4: Extend the three `DataApiDocumentation` switches**

Each of the switches at `:158`, `:344` and `:366` throws `InvalidOperationException` on an unmapped kind, and
`AlvoEndpointDataSource.BuildOrRefuseToRoute` (`:205-230`) catches it and returns
`RouteTable.NothingIsRoutable`. Add a `Replace` arm to all three: the summary, the parameter list and the
response catalogue.

- [ ] **Step 5: Map the route**

Add `MapReplace` next to `MapUpdate` (`:497-535`), mapping `endpoints.MapPut(item, …)`. The delegate follows
`MapUpdate` step for step with two differences:

1. **It checks both operations up front**, not one:
   ```csharp
   EnsureOperationIsAllowed(policies, entity.Name, DataOperation.Create, context);
   EnsureOperationIsAllowed(policies, entity.Name, DataOperation.Update, context);
   ```
   `DataApiEndpoints`' own remarks state a symmetric invariant — "nothing is admitted here that the port
   would refuse, and nothing is refused here that the port would admit" — and checking only `update` would
   break the first half.
2. **It answers on the branch**: `result.Created ? Created(result.Row, entity) : Row(result.Row, entity)`.

Body reading and validation go through `ReadAndValidateAsync` (`:755-787`) in the required-fields-present
mode Task 5 added.

- [ ] **Step 6: Run and watch them pass**

Run `scripts/test-ring0`. Expected: PASS, and **the route count test is the canary** — if every other API
test also went red, a switch arm is missing.

- [ ] **Step 7: Verify by injection**

Remove the `Replace` arm from one `DataApiDocumentation` switch and confirm the whole API suite goes red,
then restore. Remove the `ToWireName` arm and confirm `The_replace_operation_has_an_id_of_its_own` goes red
while the others stay green. Restore.

- [ ] **Step 8: Commit**

```bash
scripts/test-ring0
git add -u && git commit -m "feat(api): PUT on the item route, gated on both create and update"
```

---

### Task 8: The published document

**Files:**
- Modify: `src/MMLib.Alvo/Api/Internal/SchemaComponentBuilder.cs`
- Test: the existing OpenAPI Verify snapshots

**Interfaces:**
- Consumes: `DataApiEndpointKind.Replace` (Task 7).
- Produces: the replace request-body component.

- [ ] **Step 1: Run the snapshot suite and read the diff**

Run: `scripts/test-ring1`. The OpenAPI snapshots move because a `PUT` operation appeared. **Read the
`.received.` file before accepting anything** — a snapshot is the one place a check goes green with no
product change, and the `turn-review-gate` hook will send you to `alvo-snapshot-judge` for exactly this.

- [ ] **Step 2: Add the request-body component**

Add the replace body component to `SchemaComponentBuilder`, distinct from the update body's: the update
body's fields are all optional, the replace body's `required` fields are required. Publishing the update
body for `PUT` would document a merge.

- [ ] **Step 3: Re-run, judge the diff, accept**

Run `scripts/test-ring1`, read the diff, and accept only the additions the route explains — a new path item
for `PUT`, a new component, no change to any existing operation.

- [ ] **Step 4: Run Vacuum**

Run `scripts/test-ring2`, which lints the document. Expected: no new findings.

- [ ] **Step 5: Commit**

```bash
git add -u && git commit -m "feat(api): publish the replace body as a whole-resource representation"
```

---

### Task 9: The events

Spec §7.

**Files:**
- Modify: `src/MMLib.Alvo.Data.EntityFrameworkCore/Internal/EfAlvoData.cs` (`EmitAsync`, `:1640`)
- Test: `src/MMLib.Alvo.Testing/Data/AlvoDataOutboxTests.cs`

**Interfaces:**
- Consumes: `OutboxEventFactory.For` (`OutboxEventFactory.cs:43-75`).
- Produces: nothing new.

- [ ] **Step 1: Write the failing tests**

```csharp
/// <summary>The branch emits its own event type; there is no third one to subscribe to.</summary>
/// <remarks>
/// A new "entity.x.replaced" type would make every existing entity.x.updated subscriber silently
/// incomplete — it would stop seeing a whole class of write without any of them changing.
/// </remarks>
[Fact]
public async Task A_replacement_that_created_emits_created()
{
    await Data.ReplaceAsync("work_orders", Guid.CreateVersion7(), Payload(title: "New"), Editor);

    (await LastEventType()).ShouldBe("entity.work_orders.created");
}

[Fact]
public async Task A_replacement_that_replaced_emits_updated_with_both_images()
{
    var existing = await Data.CreateAsync("work_orders", Payload(title: "Before"), Editor);

    await Data.ReplaceAsync("work_orders", (Guid)existing.Values["id"]!, Payload(title: "After"), Editor);

    var last = await LastEvent();
    last.Type.ShouldBe("entity.work_orders.updated");
    last.Data.OldRecord!.Values["title"].ShouldBe("Before");
    last.Data.Record!.Values["title"].ShouldBe("After");
}
```

- [ ] **Step 2: Run, implement, run**

Each branch calls `EmitAsync` with its own `DataOperation` — `Create` on the create branch with
`previous: null`, `Update` on the replace branch with the pre-image. Both stay inside the write's own
transaction, after the re-read, so a refused write leaves no event.

- [ ] **Step 3: Verify by injection**

Make the create branch emit `DataOperation.Update` and confirm only
`A_replacement_that_created_emits_created` goes red.

- [ ] **Step 4: Commit**

```bash
git add -u && git commit -m "feat(events): a replacement emits its branch's own event"
```

---

### Task 10: Docs

**Files:**
- Modify: `docs/architecture/data-api.md`, `docs/architecture/data-path.md`, `docs/architecture/events.md`

- [ ] **Step 1: `data-api.md`**

Add the `PUT` section beside `PATCH`. Move `PUT, and PUT-as-upsert` **out of "Alternatives rejected"** — it
is no longer rejected — and replace it with what *is* rejected now: `If-None-Match: *`, the mixed batch, the
natural-key upsert, and the composite `(tenant_id, id)` key, each with the design's reason. Record §3's
residual oracle here too: `data-api.md` is where a reader looks for what an endpoint discloses.

- [ ] **Step 2: `data-path.md`**

Add the branch to the write path's walkthrough: the locked policy-scoped read, the two branches, `WITH CHECK`
on the candidate in both, and the guard's unconditional `isUpdate: true` with its one-sentence reason.

- [ ] **Step 3: `events.md`**

One paragraph: a replacement emits its branch's own type, and there is deliberately no `replaced` type.

- [ ] **Step 4: Commit**

```bash
git add -u && git commit -m "docs(architecture): PUT is no longer an alternative rejected"
```

---

### Task 11: E2E

**Files:**
- Create: `test/teapie-field-service/120-Replace/`

- [ ] **Step 1: Write the pins**

Four requests: a `PUT` on a free id answering `201` with a `Location`; the same `PUT` again answering `200`;
a `PUT` omitting a field that was set, asserting the field came back `null`; and a `PUT` on `work_orders`
omitting `access_code` — which is `required: true` **and** `hidden: true`
(`examples/field-service/field-service.alvo.json:165-171`) — asserting `422` naming the field. That last one
is §6's un-replaceable case, pinned on the entity that really has the shape.

- [ ] **Step 2: Run the E2E suite**

Run: `scripts/test-e2e`. Note the standalone host adds `traceId` to problem documents, so assert on the
fields you mean rather than on the whole body.

- [ ] **Step 3: Commit**

```bash
git add -u && git commit -m "test(e2e): pin PUT, including the entity a caller cannot restate"
```

---

### Task 12: Follow-ups, and the PR

- [ ] **Step 1: File the follow-up issues**

Four, each quoting the design section that argued it:

1. **The composite `(tenant_id, id)` primary key** — closes §3's residual cross-tenant oracle. Quote
   `DescriptorModelBuilder.cs:64-72` (#137) and the design's deviation table.
2. **`If-None-Match: *` on PUT** — RFC 9110 §13.1.2, "create only if absent".
3. **Batch upsert / the mixed batch** — the shape `data-api.md` deferred to #105, now unblocked.
4. **Upsert on a natural unique key** — PostgREST's `on_conflict` / `resolution=merge-duplicates`, for a
   caller who owns an external key and has no UUID.

- [ ] **Step 2: Run everything**

```bash
scripts/test-ring2
scripts/test-e2e
```

Then the PostgreSQL integration leg on its own, since ring2 is affected-scoped.

- [ ] **Step 3: Reviewer subagents, on a frozen tree**

Dispatch the correctness and security reviewers **only once you have stopped editing**. A reviewer reading a
tree you are still changing gives a verdict that does not cover the final diff. Pair the security pass with
the `alvo-security-core-review` checklist — this diff is squarely in the security core.

- [ ] **Step 4: `alvo-plan-guard`, then `alvo-pr-report`, then the PR**

In that order, per CLAUDE.md. The PR body repeats the closing keyword per issue:

```
Closes #105.
```

(One issue this time, but keep the habit — `Closes #a, #b` closes only the first.)

- [ ] **Step 5: After the merge**

Sync `main`, delete the local **and** remote branch, and verify #105 actually closed. Then check the
post-merge mutation run on `main` — the `data-ef` shard ran 110 minutes against its 120-minute ceiling after
PR-H, and this PR adds to `EfAlvoData.cs` again. If it is now cancelled, that shard needs splitting or a
higher timeout before the next data PR.

---

## Self-review

**Spec coverage.** §0 → Tasks 3, 7. §1 → Task 3 (the path carries the id). §2 → Tasks 1, 3. §3 → Tasks 2, 3
(the collision, the branch, the invisible-row test). §4 → Tasks 3, 7 (both decisions, both operations). §5 →
Tasks 3, 4. §6 → Task 5. §7 → Tasks 5, 6, 9. §8 → Tasks 7, 8. §9 → Task 12 (each rejection becomes an
issue or a `data-api.md` entry). §10 → Tasks 3–6, 9, 11 and the injection step in every one. §11 → the
baseline acceptance in Tasks 1 and 3.

**Type consistency.** `AlvoReplaceResult(AlvoRecord Row, bool Created)` with `CreatedRow`/`ReplacedRow` is
the name used in Tasks 1, 3, 6, 7. `ReplaceAsync`'s signature is written once in Task 3's **Interfaces** and
called with the same parameter order in Tasks 4, 5, 6, 9. `ConstraintViolationTranslator.Translate(...,
bool callerKeyed)` is defined in Task 2 and consumed in Task 3.

**Gap found and closed during review.** Task 5 originally validated the required-field rule only in
`RecordValidator`, which is the HTTP layer — an embedded host calling the port directly would have got the
partial-update semantics the route exists not to have. Step 4 now mirrors the rule in the port.
