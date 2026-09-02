# PR-D — measured cost: what the four performance issues actually cost

*Design, 2026-09-02. Issues #126, #118, #127 (this PR) and #117 (PR-D2).*

## 0. Why this document leads with measurements rather than a design

PR-C ended with a lesson worth writing down: of its four issues, **three described a
defect that had moved**. #119 had already been delivered whole by PR4; #130's stated
cause was untrue for the shipped runtime (ASP.NET Core OpenAPI 10.0.11 emits
`servers[0].url` from `Request.PathBase` per request and does not cache it), leaving
only a missing test; and one lettered sub-item was not a missing capability at all.

So PR-D began by **measuring all four issues before designing any of them**. A probe and
its deletion cost fifteen minutes. A design against a defect that no longer exists costs
a whole PR.

The measurement changed three of the four. This document records what was measured, what
survives, and what the PR therefore is.

### 0.1 The same pass closed #121

#121 ("a 201's `Location` ignores `HttpRequest.PathBase`") was still open and was the
named candidate for this treatment. `DataApiEndpoints.cs:979` already reads:

```csharp
httpContext.Request.PathBase.Add($"{Collection(httpContext)}/{Id}").ToUriComponent();
```

Its acceptance criterion — *follow the header with a real request and assert 200* — is
met literally by `PathBaseTests` (4 facts: no base, `/alvo`, non-ASCII `/účty`) and
`AlvoHostPathBaseTests` (6 facts: configured base, trusted `X-Forwarded-Prefix`). Both
run green. Closed with the evidence, no code written.

## 1. What was measured

| Issue | The claim as filed | Measured at `259872b` | Outcome |
|---|---|---|---|
| **#126** | O(N²) name scans, 6N set allocations per `/openapi/v1.json` | **True**, and the allocation count is *under*-stated | **Fix it** |
| **#118** | The list endpoint resolves the policy 3× (filter, endpoint, port) | **False.** It is **2×**; the filter resolves **zero** | **Decline the fix, pin the count** |
| **#127** | A keyed create costs 10 write transactions before its 500 | **Already fixed** by #138 | **Close it, add the missing fact** |
| **#117** | `select` never reaches the `SELECT` list | **True**, and the fix is smaller than the issue thought | **PR-D2** |

## 2. #126 — confirmed, and worse than filed

`AlvoDocumentTransformer` has exactly two call sites into its injected ports, which
makes the cost exactly attributable:

- `schema.GetSchema()` — one site, `:247`, inside `EntityOf`.
- `policies.Current` — one site, `:235`, inside `FlagsOf`.

`EntityOf` linear-scans `Entities` by name. `FlagsOf` builds **two** fresh `HashSet`s
(`policy.Hidden.Keys.ToHashSet(...)` and `policy.ReadOnly.Keys.ToHashSet(...)`). Both
run once per entity in `Describe` **and once per endpoint** in `Enrich`, and the Data API
maps five endpoints per entity.

For N entities:

| | calls | consequence |
|---|---|---|
| `EntityOf` | N (from `Entities`) + 5N (from `Enrich`) = **6N** | 6N linear scans over N ⇒ **O(N²)** comparisons |
| `FlagsOf` | N (`Describe`) + 5N (`Enrich`) = **6N** | **12N** `HashSet` allocations |

The issue said "6N hash-set constructions". It is **12N** — `FlagsOf` allocates two per
call, not one. Recorded because the fix should be judged against the true number.

The document is rebuilt per request, so this is per-request work on an anonymous-reachable
endpoint.

**The fix is the one the issue names**, and the code already almost does it: `Operations`
(`:119-124`) already builds a `byName` dictionary from the entity list. Hoist a single
`Dictionary<string, (EntitySchema Schema, IReadOnlySet<string> Hidden, IReadOnlySet<string> ReadOnly)>`
in `TransformAsync` and pass it down. After: `GetSchema()` once, `Current` once per
entity, `EntityOf` O(1).

**Deliberately preserved:** both `EntityOf` and `FlagsOf` throw on absence, and the
throw is a documented framework invariant ("the routes were mapped from that schema").
The hoisted build performs the same lookups, so the same absence still throws — the
dictionary is built by the same code that used to scan, not by a lenient `TryGetValue`
that would turn a broken invariant into a silently thinner document.

## 3. #118 — the premise is false

Measured on `GET {prefix}/{entity}`:

1. `DataApiEndpoints.cs:92` → `EnsureOperationIsAllowed` → `policies.Resolve` (`:431`).
2. `EfAlvoData.cs:112` → `_policy.Resolve` (`:1158`).

That is **two**, not three. `AlvoContextFilter` performs **no** policy resolution at all:
it holds `IAlvoContextResolver`, `IAlvoContextAccessor`, `ScopeGate` and `IOptions<AlvoAuthOptions>`
and has no `IPolicyEngine` reference. Its gate is `ScopeGate.Allows`
(`src/MMLib.Alvo/Auth/ScopeGate.cs:25-29`):

```csharp
return principal.Scopes.Any(scope => scope.Allows(entity, operation));
```

A LINQ `Any` over the credential's own scope list. No catalog lookup, no CEL evaluation,
no `FrozenSet`. For an anonymous caller it is skipped entirely.

### 3.1 What the remaining duplication actually costs

`PolicyEngine.Resolve` on the allow path:

- 1 `PolicyDecision` — a `public sealed record`, so one heap allocation.
- 2 × `ResolveFieldMask` (`Hidden`, `ReadOnly`). Each returns a **static empty
  `FrozenSet` with zero allocations and zero CEL evaluations when the entity declares no
  mask of that kind** — and `hidden: false` compiles to `null` and is absent from the
  dictionary entirely (`PolicyCatalogBuilder.cs:402-415`), so the empty case is the
  common one.
- Only when masks *are* declared: 1 `HashSet` + 1 boxed dictionary enumerator + 1
  `FrozenSet` per kind, plus one context-only CEL tree-walk per expression-valued mask
  whose `RequiredContext` the caller satisfies.

Nothing in `Resolve` compiles, parses, or reads a catalog that is not already built:
`_provider.Current` is a `Volatile.Read` of a catalog built once at apply time, and
`TryGetEntity` is a dictionary lookup.

So the cache the issue proposes would save, per list or write request, **one
`PolicyDecision` allocation** — plus, only for entities declaring CEL-valued
`hidden`/`readOnly`, one `HashSet` + one `FrozenSet` + a few context-only tree walks.

### 3.2 Why the cache is declined

**The reason is the measurement, not a safety argument.** One `PolicyDecision` record
allocation per request — on a request already performing a database round trip — is not
worth a memo layer in the security core, whose own cost is a scoped-service resolution
plus a cache lookup and whose maintenance cost is that every future reader of the
authorization path has to reason about it.

An earlier draft of this section argued the stronger claim: that a cache would
necessarily hand the port a **stale** decision when `RuntimeSchemaService` publishes a
tightened catalog mid-request, narrowing the fail-closed window the issue demands be
preserved:

> It must not become a way to hold a stale decision across a descriptor apply. […] the
> current behaviour of failing closed on the *next* resolve must be preserved for
> anything that is not the same logical decision within one request.

**That claim is too strong and is withdrawn.** `_provider.Current` is a single
`Volatile.Read` of an immutable catalog, so a cache that additionally keys on the
**catalog reference** invalidates itself the instant a new catalog is published and
preserves the fail-closed window exactly. A correct cache is therefore constructible;
it is simply not worth building for one allocation.

Recorded this way deliberately: the decline must rest on a number that is true, not on
a hazard that sounds decisive. A future reader who finds a real reason to want the memo
(a fourth consumer, or a mask expensive enough to matter) should find the actual trade
here, plus the one constraint that makes such a cache safe — **key on the catalog
reference, not only on `(entity, operation, context)`** — rather than a prohibition
resting on an argument they can disprove in five minutes and then dismiss wholesale.

### 3.3 What is delivered instead

The issue's first constraint is currently prose:

> **A third resolution would be refused review.** The cache is the answer to "we now need
> the decision in a fourth place", not a licence to need it there.

Prose does not refuse anything. PR-D makes it enforceable: a counting decorator over
`IPolicyEngine`, registered through the existing `AlvoApiWorldSetup.ConfigureServices`
hook, pinning the count per operation.

**Two things the fact must not claim**, both found by `alvo-plan-guard` and both fatal to
a naïvely written version of it:

**A create does not always resolve twice.** The keyed-create **replay** branch resolves
`get` a third time — `EfAlvoData.cs:596`, mirrored at `InMemoryAlvoData.cs:223` — and the
surrounding remark records that resolving `get` there, rather than reusing the `create`
decision, is what closed a row-level authorization bypass. A fact asserting "exactly two"
across all creates would either fail, or — worse, if someone made it pass — become an
argument for deleting that resolve. **The count is therefore scoped to the non-replay
path, and the fact's own doc names the replay's third resolve as correct.**

**"A read by id resolves exactly once" is a number that is expected to move.**
`DataApiEndpoints.cs:152-166` instructs a future author in as many words to *add* the
guard back the moment that delegate interprets caller input before the port call, and it
names the triggers: `select` (#117, i.e. PR-D2) and `If-Match`. So the fact must not be
written as "nobody may add a resolve here". It is written as *the count today is one
because this delegate interprets no caller input*, with the doc stating that **the number
moves together with the guard** — the fact exists to make that a deliberate edit rather
than an accident, not to forbid it.

This is the same move #184 asks for elsewhere: turn an invariant that is only written
down into one that fails a build.

## 4. #127 — already fixed, and the docs contradict each other

`EfAlvoData.cs:384-392` states it outright:

> **A duplicate in the caller's own entity no longer reaches this loop at all (#138).**
> […] That write now goes through `ConstraintViolationTranslator`, which turns a
> recognised violation into `AlvoConstraintViolationException`; that is not a
> `DbException`, so `IsStorageWriteFailure` does not match it and it leaves on the first
> attempt.

The seam #127 proposes building — "ask the dialect whether a failing constraint is
Alvo's own […] That is an `IAlvoSqlDialect` question" — **already exists and already
ships**: `IAlvoSqlDialect.DecodeConstraintViolation(DbException)`, implemented by
`SqliteSqlDialect` (extended result codes 2067/1555/787), `PostgreSqlSqlDialect`
(SQLSTATE 23505/23503) and, honestly returning `null`, `TSqlSqlDialect`. It is pinned in
four public-API baselines.

### 4.1 What is genuinely left

**One fact, and it is the one the issue asked for:**

> Worth a fact asserting the *number* of attempts for a caller-constraint violation,
> since "it eventually 500s" passes today and would pass after a fix that changed
> nothing.

`SqliteIdempotentCreateFailureTests` asserts the **outcome** (`AlvoConstraintViolationException`,
`Kind`, `Fields`) — which is strictly stronger than "it eventually 500s", but still
**does not pin the attempt count**: a build that retried ten times and *then* threw
`AlvoConstraintViolationException` would pass every assertion in that file. The class
doc even claims the improvement — "since #138 it must not turn it into ten transactions
either" — while asserting nothing that would catch its loss. That is the gap.

The count is observable with infrastructure that already exists: `SqlCapture`
(`test/_shared/ef/SqlCapture.cs`) records every EF `CommandExecuting`, the entity insert
goes through EF, and `SqliteAlvoDataFixture` already re-exposes `Statements` /
`ClearStatements()` on the host the existing test uses. Counting `INSERT INTO "vehicles"`
occurrences gives **1** on the fixed build and **10** on the old one.

**And a documentation contradiction.** `docs/architecture/data-api.md` currently says
both things in one file:

- `:643-647` — "A duplicate no longer costs ten transactions. […] it leaves on the first attempt."
- `:735-742` — "**One 500 *is* caller-reachable, and it costs ten write transactions to get there.** […] ten full write transactions run with a linear backoff (~450 ms total) before the exception surfaces as the family-5 500 above. […] Tracked in **#127**."

The second paragraph describes behaviour that no longer exists and points a reader at an
issue for a fix that has shipped. It is rewritten, not deleted: the residual cost is
real but different, and §4.2 is what replaces it.

### 4.2 The residual, stated honestly

#127 does not close completely, and the replacement paragraph must say what remains:

- The **idempotency record's own** primary-key failure is *deliberately* untranslated —
  losing that race is the entire reason the loop exists — so it still drives up to ten
  attempts. That is correct and must not be "fixed".
- An **unrecognised** `DbException`/`DbUpdateException` still burns ten attempts. The
  dialect answers `null` when it does not recognise the code, when the constraint name
  matches no model index, or when the surviving columns are all framework-managed
  (`ConstraintViolationTranslator.cs:74-104`, `:135-148`). That is the fail-safe
  direction #127 explicitly demanded be preserved ("a fix that narrows the catch must not
  narrow it to the point where a genuine insert race escapes as a 500"), so it stays.

- A dialect that answers `null` because it recognises nothing — which
  `TSqlSqlDialect` does honestly, shipping no `Microsoft.Data.SqlClient` — burns ten
  attempts for *every* duplicate. The amplification is therefore a **per-dialect
  property**, not something fixed once for all engines.

### 4.3 The engine question, and why #127 is not closed unconditionally

The attempt count is a function of `IAlvoSqlDialect.DecodeConstraintViolation`, so it is
an **engine-specific** number. The fact this PR adds runs on **SQLite only**:
`MMLib.Alvo.Data.PostgreSql.Tests.Integration` carries no idempotent-create-failure test
at all.

§0 principle 3 requires behaviour to be identical on SQLite and PostgreSQL, and open
**#139** exists precisely to demand that constraint-violation behaviour be verified per
engine rather than on one. Closing #127 on one engine's evidence would assert an
engine-agnostic property from an engine-specific measurement — the exact gap #139 was
filed to track.

So: #127 is closed on its stated defect with the closure comment **stating explicitly
that the count is pinned on SQLite and that the PostgreSQL leg belongs to #139**, and the
doc rewrite says the same. The alternative — mirroring the fact into the Testcontainers
integration project — was considered and rejected for this PR: it would put a
container-backed test in the PR for a defect that is already fixed, and #139 already owns
that work as a batch across every constraint behaviour rather than this one in isolation.

With that stated, the issue closes on its stated defect — *the caller's own constraint
violation* — and the doc records the paths that legitimately still retry.

## 5. #117 — real, and smaller than the issue thought (deferred to PR-D2)

`select` is applied by `DataApiPage.Project` (`DataApiPage.cs:65-96`) after the port has
returned every column, so `?select=id` costs the database a full read. Confirmed.

The issue's proposed route — publish `AlvoQuery.Select` and narrow each driver's
`SELECT` list — meets a wall the issue did not know about. `ReadProjection`'s own remark:

> Omitting a masked field from the list is not an option — EF requires a `FromSql` result
> set to contain every mapped property and fails with "The required column '…' was not
> present in the results of a 'FromSql' operation", identically on both engines.

The reads go through `FromSqlRaw` over a property-bag shared-type entity mapping **every**
schema field, so a literally narrowed column list breaks materialization on both engines.
Working around that means abandoning EF's type mapping, which `RecordMaterializer.cs:10-15`
argues is the reason values come back correctly typed at all ("a raw SQLite reader over
the identical statement returns `string` for all three").

**But the same remark states the answer in its next sentence:**

> Projecting the `NULL` instead means **the masked column is never read from the page at
> all**, and the key is dropped again when the `AlvoRecord` is assembled.

That is exactly the push-down #117 wants, it is already implemented, and it is already
proven on both engines — it is how `hidden` works. `select` should reuse it: an
unselected field is rendered `dialect.RenderNullProjection(storeType) AS <col>` instead
of being fetched, the result set keeps every mapped column so EF is satisfied, and the
key is dropped when the record is assembled.

This makes #117 a change to `ReadProjection.Compose`'s input rather than a materialization
rewrite, and keeps `IAlvoSqlDialect` — and its four implementations and contract suite —
entirely out of the change.

**Deferred to PR-D2, not folded in here**, because it is still the only one of the four
that ships a public API member (`AlvoQuery.Select`), moves Verify SQL baselines on two
engines, and changes what the wire returns on the list path. This PR changes no behaviour
at all; mixing the two would forfeit that property, which is the one thing that makes
this PR cheap to review.

Constraints for PR-D2, recorded here so they are not rediscovered:

- **`id` and the version column must survive the projection regardless of `select`.**
  Not only for `RowVersionETag` and `Location`: the keyset cursor is minted from the
  fetched row at `EfAlvoData.cs:204` (`(Guid)kept[^1][AlvoDataContext.IdColumn]`), so a
  NULLed `id` breaks paging outright. The version column is `updated_at` and exists only
  when the entity is audited (`AlvoManagedColumns.VersionColumn`).
- **The narrowing applies to the page path only** — never to `PolicyRoot`, `SingleAsync`,
  `AnchorAsync` or `ComposeCount`.
- **Honest claim.** `NULL AS col` stops the engine reading that column; it does not make
  the query proportionally cheaper. The win is real for wide/TOASTed columns and near
  zero for a narrow int. The fact should therefore assert what is *verifiable* — that the
  unselected column no longer appears in the statement — via `AlvoDataStatementTests`,
  which exists on both engines for exactly this kind of claim.
- `AlvoQuery`'s own remark pre-authorises the member by name: *"a new optional member
  (e.g. a future `Select` projection list) can be added here without breaking an existing
  caller or provider"*.
- `ParsedListQuery`'s constraint is satisfied only if **all three** implementations honour
  it in the same change — `ReadProjection` (both shipped engines) and
  `InMemoryAlvoData.QueryAsync:109`. Then `DataApiPage.Project` is deleted rather than
  left as a second projection.
- **`select` must not be fed through `ReadProjection.Compose`'s `hiddenFields`
  parameter.** This is the trap the "reuse the mask mechanism" framing invites, and it
  would be a security defect rather than a shortcut. That parameter is guarded by
  `QueryFieldGuard.EnsureMaskable`, which throws `AlvoAuthorizationException` — so
  merging the two sets would (a) make a caller *preference* and a *security control*
  indistinguishable at the one point the mask is enforced, and (b) turn a malformed
  `select` into a **403** where it must be a **400**. The two stay **separate inputs**,
  unioned only at render time, where the union decides one thing and one thing only:
  which columns are rendered `NULL AS col`. `EnsureMaskable` keeps seeing the mask alone.
- The push-down proof belongs on the statement suite, but note the asymmetry:
  `SqliteAlvoDataStatementTests` lives in a **unit** project while
  `PostgreSqlAlvoDataStatementTests` lives in the **Testcontainers integration** project.
  The two legs therefore have different gating cost — the SQLite one runs in ring0, the
  PostgreSQL one in ring2.

## 6. "No behaviour change", made provable

The PR's claim is that it changes cost, not conduct.

**The rule is additions-only, not "no file is touched."** An earlier draft of this section
said "not one existing test file is touched", which was both false of this PR — it appends
a fact to `SqliteIdempotentCreateFailureTests` and edits `CHANGELOG.md` and
`data-api.md` — and, as `alvo-plan-guard` pointed out, actively harmful as a rule: the
cheapest way to satisfy it is to bolt a duplicate test class beside a legitimately
affected one. The checkable property is:

> **No existing assertion is weakened, deleted, or re-baselined. Additions only. Every
> Verify baseline is byte-identical.**

That forbids the thing that would make the claim false and permits appending a fact to the
file where it belongs.

**And what is added is measurement, not restatement.** Counting facts, each stating a
number, and the first two **failing on today's code**:

| Fact | Today | After |
|---|---|---|
| `/openapi/v1.json` for N entities: `GetSchema()` calls | 6N | 1 |
| `/openapi/v1.json` for N entities: `policies.Current` reads | 6N | 1 |
| `GET {prefix}/{entity}`: `IPolicyEngine.Resolve` calls | 2 | 2 *(pinned, not changed)* |
| `GET {prefix}/{entity}/{id}` | 1 | 1 *(pinned; moves with the guard — §3.3)* |
| Keyed create, non-replay path | 2 | 2 *(pinned; the replay path is 3 — §3.3)* |
| Keyed create violating the caller's unique constraint: entity `INSERT`s | 1 | 1 *(pinned, SQLite only — §4.3)* |

Both `/openapi/v1.json` counts land at **1**, not `1` and `N`: the hoisted build reads
`policies.Current` once and indexes the catalog per entity from that one read, the same
way it reads the schema once. An earlier draft said `N` for the catalog; one read is both
achievable and the honest target, and the fact is written against it.

One subtlety the decorator has to respect, or both numbers become meaningless:
`PolicyCatalogProvider.GetSchema()` is itself implemented as `Current?.Schema ?? …`. A
decorator that implements `GetSchema()` in terms of *its own* `Current` property
double-counts and entangles the two counters. It must forward `GetSchema()` to the inner
provider and count only the outer call.

The last two pin a number rather than move it — which is the point. #118's and #127's
defects are gone; what was missing is anything that would notice their return.

A counting fact is used rather than a timing one throughout. All four questions here are
about *how many times*, and a count is deterministic where a duration is a coin flip on a
loaded machine. `PagingPerformanceTests` remains the right instrument for the one
question that genuinely is about latency, and it is untouched.

## 7. The mutation gate — an observation, and a correction

Run `33593316805` on `259872b` **passed on all five legs**; `data-ef` took **91.7 min** of
its 120-minute budget. That is the answer to the question this PR was asked to check
first: mutation is green, and no aggregate `cancelled` needs interpreting.

**A claim made in an earlier draft of this document is withdrawn.** It read the data-ef
leg's `suite: 858` against a measured 1062 tests in the two projects and called the gate
stale — arguing the growth curve `mutation.yml` exists to keep visible had gone invisible,
and that PR-D must refresh the number.

That misreads what the field is for. `scripts/assert-mutation-run` checks 3 and 4 document
it explicitly:

> **TOLERANCE: a one-sided floor at 60 % of the declared count, not a band.** […] growth
> must not fail a run […] 60 % because the declared numbers are a dated snapshot, not a
> contract.

`suite:` is a **floor against the suite shrinking or being substituted** — the #99 failure
where solution mode silently ran a different suite — not a cost model. Growth is
explicitly the case it must tolerate, because "more code under measurement […] is never
the bug", and a band would force an edit to `mutation.yml` on every ordinary PR that adds
a test. 1062 against a floor of 514 is nowhere near failing. The growth curve is visible
where it was always visible: **run duration against `timeout`**, which is exactly the
91.7 min this PR measured.

So **PR-D changes nothing in `.github/workflows/mutation.yml`.** It was never in scope,
the premise for adding it was wrong, and refreshing `suite:` while leaving `mutants: 596`
un-remeasured would be half a recalibration anyway. **#143** already owns the re-shard
work, and the measurement is posted there instead so it is not lost.

What remains true and worth someone's attention — recorded on #143, not acted on here —
is that the leg has ~28 minutes of headroom and the *previous* merge's run died on this
same leg. That is a re-shard question, not a PR-D question.

## 8. Scope

**In (PR-D):** #126 fix + measurement; #118 measurement; #127 measurement + doc
correction.

**Out:** #117 (PR-D2). Any policy-decision cache (§3.2). Any narrowing of the retry catch
(§4.2). `#111` projection aliases, which #117 already distinguishes itself from. Anything
in `mutation.yml` (§7).

### 8.1 Which issues this PR closes, and which it does not

- **`Closes #126`** — the defect existed, it is fixed, the measurement is in the suite.
- **`Closes #127`** — the defect is gone, the missing fact is added, the contradicting
  doc is corrected. The closure comment states the SQLite-only scope (§4.3).
- **#118 stays OPEN.** The PR posts the measurement — that the count is two, not three,
  and that `AlvoContextFilter` resolves nothing — and argues the decline in §3.2, but does
  **not** auto-close it.

The asymmetry is deliberate. #126 and #127 close on evidence that the work is *done*.
#118 would close on a judgement that the maintainer-scoped work should *not be done* — and
a `Closes` keyword in a PR footer makes that judgement disappear the moment the PR merges,
before anyone has had the chance to disagree with it. Declining scoped work is the
maintainer's call to ratify, not a side effect of a merge. The decline is therefore
argued in the PR body and the issue is left for a human to close.

**Carried:** the `#125` follow-up — a note on `ApiKeyHash` that #36 must not inherit
single-pass SHA-256, since a user-chosen secret needs a password KDF. #125's item 1 (the
entropy floor, `AlvoAuthOptionsValidator.MinimumSecretLength = 32`) shipped; item 2 did
not. It is a doc comment in the security core and belongs with PR-E's tenancy work, not
in a PR whose whole claim is that it changes no behaviour.
