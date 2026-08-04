# F3 PR5b — before-hooks and the `Mutate` CEL profile

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development
> (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use
> checkbox (`- [ ]`) syntax for tracking.

**Goal:** Honour the three `entity.hooks.before*` points — `reject` and `mutate`, in the write
transaction — behind a new interpreter-only `Mutate` CEL profile, and delete the three
`UnhonouredFeatures` entries that refuse them today (closing #114).

**Architecture:** `Mutate` is a fourth `CelProfile` evaluated **only** by `CelInterpreter`,
never rendered to SQL, so no `IFieldSqlRenderer` member and no per-engine snapshot is added.
Two functions join the grammar for this profile alone: `lowerAscii(x)` and `now()`. Before-hooks
run inside the write's own transaction, after the candidate is built and before the row is
written, and a `mutate` rewrites the candidate in place. Network access is structurally
impossible because the hook context exposes no client of any kind.

**Tech Stack:** .NET 10, EF Core 10, xUnit v3 on Microsoft.Testing.Platform, Shouldly, Verify,
`Microsoft.Extensions.TimeProvider.Testing` (`FakeTimeProvider`).

## Global Constraints

- Design source of truth: `docs/superpowers/specs/2026-08-02-f3-pr5-events-hooks-design-addendum.md`.
  `Mutate` is settled at its §"`Mutate` is interpreter-only"; JSONata is settled as Decision 2
  (deferred entirely, `{{…}}` templates only). Neither is reopened here.
- **Scope boundary — this PR is PR5b-1.** The wildcard subscription matcher, the `Publish`
  namespace guard and `examples/complex-crm/crm.alvo.json`'s five defects are PR5b-2. Reason:
  #157 carried 133 files and CodeRabbit skipped it entirely (*"133 files exceed the limit of
  100"*), which is how a flaky fact and a MEDIUM in concurrency SQL reached `main` unreviewed.
  Staying under 100 files is a review-coverage requirement, not tidiness.
- `lowerAscii` folds `A`–`Z` **and nothing else**. Never `ToLowerInvariant()`, which folds a long
  tail of non-ASCII code points; a culture-sensitive fold on a *stored* value is a permanently
  wrong row. **Measured, correcting this plan's first draft and the addendum:** `İ` (U+0130) is
  *not* one of them — `"İ".ToLowerInvariant()` is unchanged, length 1, because .NET's invariant
  casing excludes the dotted capital I. Use `Ž`/`Ä`/`ẞ`/`Σ`, which it does fold, or the fact
  passes under its own mutation and proves nothing.
- `now()` is **not a clock read**. It resolves to the same `DateTimeOffset` the write's audit
  stamp already uses, bound once per write, through `TimeProvider`. `now()` twice in one write
  returns the same value. It is never rendered to SQL (Postgres returns transaction-start time;
  SQLite `CURRENT_TIMESTAMP` has second precision and returns a string — §0 principle 3).
- A before-hook must be **unable** to make a network call, not merely discouraged from it
  (`alvo-security-core-review`). Enforced by what the context type exposes, plus an
  architecture test.
- ring0 after every step; ring2 before the PR. Assert `Build succeeded` before reading any test
  result. State the mutation that proves each significant fact discriminates, and run it.
- `.gitattributes` pins `*.cs` to CRLF — verify a mutation's edit landed with `git diff`, never
  by an LF search string.

---

## File Structure

| File | Responsibility |
|---|---|
| `src/MMLib.Alvo.Abstractions/Expressions/CelProfile.cs` | add `Mutate` |
| `src/MMLib.Alvo/Expressions/Internal/CelTree.cs` | `CelCall` node (function name + one argument) |
| `src/MMLib.Alvo/Expressions/Internal/CelParser.cs` | widen `ParseCall`; the two-entry allow-list, profile-gated |
| `src/MMLib.Alvo/Expressions/Internal/CelTypeChecker.cs` | result type of each allow-listed function |
| `src/MMLib.Alvo/Expressions/Internal/CelInterpreter.cs` | evaluate `CelCall` |
| `src/MMLib.Alvo/Expressions/Internal/SqlPredicateRenderer.cs` | refuse `CelCall` loudly — the guarantee, not an oversight |
| `src/MMLib.Alvo/Rules/BeforeHooks.cs` (new) | the compiled before-hook shape a policy carries |
| `src/MMLib.Alvo/Rules/Internal/BeforeHookCompiler.cs` (new) | compile `reject`/`mutate` at apply |
| `src/MMLib.Alvo.Abstractions/Rules/IBeforeHookRunner.cs` (new) | the port the data layer calls in-transaction |
| `src/MMLib.Alvo/Rules/Internal/BeforeHookRunner.cs` (new) | the implementation; no client of any kind injected |
| `src/MMLib.Alvo.Data.EntityFrameworkCore/Internal/EfAlvoData.cs` | call the runner inside the transaction |
| `src/MMLib.Alvo/Descriptor/Internal/UnhonouredFeatures.cs` | delete the three `before*` entries |

---

## Task 1: `Mutate` joins `CelProfile`, and the renderer refuses it

**Files:**
- Modify: `src/MMLib.Alvo.Abstractions/Expressions/CelProfile.cs`
- Test: `test/MMLib.Alvo.Tests/Expressions/CelProfileTests.cs`

**Interfaces:**
- Produces: `CelProfile.Mutate` — the profile every later task gates on.

- [ ] **Step 1: Write the failing test**

```csharp
[Fact]
public void Mutate_is_a_profile_of_its_own_and_not_an_alias_of_Condition()
{
    Enum.GetValues<CelProfile>().ShouldContain(CelProfile.Mutate);
    ((int)CelProfile.Mutate).ShouldNotBe((int)CelProfile.Condition);
}
```

- [ ] **Step 2: Run it and watch it fail**

Run: `dotnet test --project test/MMLib.Alvo.Tests -- --filter-class '*CelProfileTests*'`
Expected: FAIL — `CelProfile.Mutate` does not compile.

- [ ] **Step 3: Add the member, with the guarantee in its own remarks**

```csharp
    /// <summary>
    /// A before-hook <c>mutate</c> value expression: evaluated by the interpreter against the
    /// candidate row inside the write transaction, and written as a bound parameter.
    /// </summary>
    /// <remarks>
    /// <b>Interpreter-only, and that is a guarantee rather than an accident.</b> A
    /// <c>Mutate</c> expression is never handed to <c>SqlPredicateRenderer</c>, so this profile
    /// adds no <c>IFieldSqlRenderer</c> member, no per-engine golden snapshot and no row to the
    /// differential backend test — there is no second backend to differ from. The two-valued
    /// rendering rule (<c>cel.md:124-134</c>) likewise does not apply: it is a rule both
    /// backends must agree on, and with one backend there is nothing to agree with. The moment
    /// somebody proposes rendering a <c>Mutate</c> expression to SQL, the two-valued fold and
    /// the collation caveat both come back into scope.
    /// </remarks>
    Mutate,
```

- [ ] **Step 4: Run it and watch it pass**

- [ ] **Step 5: Commit**

```bash
git add -A && git commit -m "feat(cel): add the Mutate profile, interpreter-only by contract"
```

---

## Task 2: `SqlPredicateRenderer` refuses a `Mutate` node before the node exists

**Files:**
- Modify: `src/MMLib.Alvo/Expressions/Internal/SqlPredicateRenderer.cs`
- Test: `test/MMLib.Alvo.Tests/Expressions/SqlPredicateRendererTests.cs`

Written **before** Task 3 deliberately: the refusal is the guarantee Task 1's remarks claim, and
adding the node first would leave a window where the renderer silently accepts it.

- [ ] **Step 1: Write the failing test** — a `CelCall` node reaches the renderer and is refused
      by a message naming the profile, not by a `NotSupportedException` from a default arm.

- [ ] **Step 2: Run it, watch it fail to compile** (no `CelCall` yet — that is expected; this
      task lands with Task 3's node type and the two are committed together if the compiler
      forces it. Do not weaken the test to make it compile alone.)

- [ ] **Step 3: Add the explicit refusal arm.**

- [ ] **Step 4: Mutation that proves it discriminates** — delete the arm and confirm the fact
      fails rather than falling through to a generic throw with the same shape.

- [ ] **Step 5: Commit**

---

## Task 3: `lowerAscii(x)` — the grammar, the type, the fold

**Files:**
- Modify: `CelTree.cs`, `CelParser.cs`, `CelTypeChecker.cs`, `CelInterpreter.cs`
- Test: `test/MMLib.Alvo.Tests/Expressions/CelMutateFunctionTests.cs`

**Interfaces:**
- Produces: `internal sealed record CelCall(string Name, CelNode Argument) : CelNode;`

- [ ] **Step 1: The facts, all four, before any implementation**

```csharp
[Fact]
public void lowerAscii_folds_A_to_Z_and_leaves_every_other_code_point_alone()
{
    // Ž, Ä, ẞ and Σ are code points ToLowerInvariant() measurably does fold, so this sample is
    // what makes the mutation in step 5 kill the fact. İ is here for what it legitimately
    // documents — a non-ASCII code point an ASCII fold leaves alone — not as the trap.
    Evaluate("lowerAscii(new.email)", new { email = "AB.Ž.Ä.ẞ.Σ.İ.Z" }).ShouldBe("ab.Ž.Ä.ẞ.Σ.İ.z");
}

[Fact]
public void lower_is_refused_and_the_message_names_lowerAscii()
{
    var refused = Should.Throw<CelSyntaxException>(() => Parse("lower(new.email)", CelProfile.Mutate));
    refused.Suggestion.ShouldContain("lowerAscii");
}

[Fact]
public void lowerAscii_is_refused_outside_the_Mutate_profile()
{
    Should.Throw<CelSyntaxException>(() => Parse("lowerAscii(new.email)", CelProfile.Condition));
}

[Fact]
public void lowerAscii_of_a_non_string_is_refused_by_the_type_checker_not_at_evaluation()
{
    Should.Throw<CelTypeException>(() => Check("lowerAscii(new.amount)", CelProfile.Mutate));
}
```

- [ ] **Step 2: Run them, watch all four fail.**

- [ ] **Step 3: Implement.** The fold is explicit, so nothing culture-sensitive can creep in:

```csharp
private static string LowerAscii(string value)
{
    var folded = value.ToCharArray();
    for (var i = 0; i < folded.Length; i++)
    {
        if (folded[i] is >= 'A' and <= 'Z')
        {
            folded[i] = (char)(folded[i] + 32);
        }
    }

    return new string(folded);
}
```

- [ ] **Step 4: Run them, watch all four pass.**

- [ ] **Step 5: The mutation that matters** — replace the body with `value.ToLowerInvariant()`.
      The fold fact must go red. If it stays green the fact is not measuring the fold; fix the
      fact, not the mutation. This is not hypothetical — the plan's first draft used `İ` alone
      and stayed green under exactly this mutation.

- [ ] **Step 6: Commit**

---

## Task 4: `now()` — one write, one instant

**Files:**
- Modify: `CelParser.cs`, `CelTypeChecker.cs`, `CelInterpreter.cs`
- Test: `test/MMLib.Alvo.Tests/Expressions/CelMutateFunctionTests.cs`

- [ ] **Step 1: The two facts that make it not-a-clock-read**

```csharp
[Fact]
public void now_returns_the_same_instant_twice_in_one_evaluation_context()
{
    var context = MutateContext(FakeTimeProviderAt(Stamp));
    Evaluate("now()", context).ShouldBe(Evaluate("now()", context));
}

[Fact]
public void now_is_the_writes_own_audit_instant_and_not_a_fresh_read()
{
    var clock = FakeTimeProviderAt(Stamp);
    var context = MutateContext(clock);
    clock.Advance(TimeSpan.FromMinutes(5));      // the wall clock moves mid-write

    Evaluate("now()", context).ShouldBe(Stamp,   // and the answer does not
        "now() is the write's bound instant, so a retry re-stamps but one attempt cannot disagree with itself");
}
```

- [ ] **Step 2: Run, watch both fail.**

- [ ] **Step 3: Implement** — the instant is a field on the evaluation context, supplied by the
      caller that already computed the audit stamp. `CelInterpreter` never touches
      `TimeProvider` itself; it reads the bound value.

- [ ] **Step 4: Run, watch both pass.**

- [ ] **Step 5: Mutation** — make the interpreter read `time.GetUtcNow()` per evaluation. The
      second fact must go red.

- [ ] **Step 6: Commit**

---

## Task 5: `reject` — a before-hook that refuses the write

**Files:**
- Create: `src/MMLib.Alvo/Rules/BeforeHooks.cs`, `Rules/Internal/BeforeHookCompiler.cs`
- Create: `src/MMLib.Alvo.Abstractions/Rules/IBeforeHookRunner.cs`
- Test: `test/MMLib.Alvo.Tests/Rules/BeforeHookCompilerTests.cs`

**Interfaces:**
- Produces: `IBeforeHookRunner.Run(EntitySchema, DataOperation, IDictionary<string, object?> candidate, AlvoContext, DateTimeOffset now)` — returns a refusal or mutates `candidate` in place.

- [ ] **Step 1: Facts** — a `reject` whose CEL condition holds refuses the write with a
      structured error naming the hook path; one whose condition is false lets it through;
      an unresolvable field is refused **at apply**, not at write time.

- [ ] **Step 2–4: Red, implement, green.**

- [ ] **Step 5: Commit**

---

## Task 6: `mutate` runs inside the write transaction

**Files:**
- Modify: `src/MMLib.Alvo.Data.EntityFrameworkCore/Internal/EfAlvoData.cs`
- Test: shared contract suite `src/MMLib.Alvo.Testing/Data/AlvoDataBeforeHookTests.cs` (new), so
  both engines run it

**The attach point is the whole content of this task.** `CreatedAsync` builds the candidate at
`:180` (`AuthorizedCandidate`) and opens the transaction at `:183`. A before-hook must run
*inside* the transaction it guards, so the runner call goes after `BeginTransactionAsync` and
before the row write — not next to `AuthorizedCandidate` where the candidate happens to be
built. The same applies to `UpdateAsync` (`:621`), `DeleteAsync` (`:701`),
`CreatedOrReplayedAsync` (`:332`) and `RecordedCreateAsync` (`:367`) — five call sites, and a
sixth that must **not** get one: `ReplayableCreateAsync` replays a stored idempotency record and
runs no hook, because the hook already ran on the original write.

- [ ] **Step 1: The facts** — a `mutate` value reaches the stored row; a `reject` rolls the
      transaction back leaving no row; `now()` inside a `mutate` equals the row's `created_at`;
      an idempotent replay does **not** re-run the hook.

- [ ] **Step 2–4: Red, implement, green — on SQLite and PostgreSQL both.**

- [ ] **Step 5: Mutation** — move the runner call outside the transaction. The rollback fact
      must go red.

- [ ] **Step 6: Commit**

---

## Task 7: A before-hook cannot make a network call, structurally

**Files:**
- Test: `test/MMLib.Alvo.Tests/Rules/BeforeHookIsolationArchitectureTests.cs` (new)

- [ ] **Step 1: The architecture fact** — nothing reachable from `BeforeHookRunner`'s
      constructor closure exposes `HttpClient`, `IHttpClientFactory`, `Socket` or
      `IEmailSender`. Asserted over the type's dependencies, not over a naming convention.

- [ ] **Step 2: Mutation** — inject `IHttpClientFactory` into `BeforeHookRunner`. The fact must
      go red. This is the checklist requirement that a network call be *inexpressible* rather
      than discouraged, so a fact that cannot catch the injection is not the fact.

- [ ] **Step 3: Commit**

---

## Task 8: Delete the three `before*` refusals — #114 closes here

**Files:**
- Modify: `src/MMLib.Alvo/Descriptor/Internal/UnhonouredFeatures.cs`
- Modify: `test/MMLib.Alvo.Tests/Descriptor/UnhonouredFeaturesTests.cs`
- Baseline: `…Every_unhonoured_slot_is_pinned.verified.txt` moves

- [ ] **Step 1:** Delete `Hook("beforeCreate", …)`, `Hook("beforeUpdate", …)`,
      `Hook("beforeDelete", …)` and the now-unused `InTransaction` reason string.

- [ ] **Step 2:** The Verify baseline moves. It is framework-written — **never hand-edit it**;
      run the suite so the tool rewrites it, then dispatch `alvo-snapshot-judge`, which the Stop
      hook will require anyway.

- [ ] **Step 3:** ring1, then ring2.

- [ ] **Step 4: Commit**

---

## Task 9: Docs, then the gates

**Files:**
- Modify: `docs/architecture/events.md` (before-hook section), `docs/architecture/cel.md`
  (the `Mutate` profile and its two functions), `docs/PLAN.md` (untouched — the marker does
  **not** move; #21 stays open)

- [ ] **Step 1:** Record the deviations this PR takes, continuing PR5a's numbering.
- [ ] **Step 2:** `scripts/test-ring2`, then `scripts/test-e2e` (the write path changed).
- [ ] **Step 3:** Dispatch `alvo-plan-guard`.
- [ ] **Step 4:** Reviewer substitutes for `/code-review high` and `/security-review`, paired
      with the `alvo-security-core-review` checklist — and **say in the PR body that they are
      substitutes**. Recommend the `needs-deep-review` label: this touches the rule engine.
- [ ] **Step 5:** Open the PR. Confirm the file count is **under 100** so CodeRabbit actually
      reviews it; if it is over, split before opening rather than after.
