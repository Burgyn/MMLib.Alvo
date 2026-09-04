# PR-H — an idempotency key that works on every write, and a transactional batch

Closes **#102** (`Idempotency-Key` is ignored on `PATCH` and `DELETE`) and **#106** (bulk operations:
batch insert, update and delete, transactional).

The two are one PR because they are one change to one record. #102 needs the idempotency record to
describe a write that is not a create; #106 needs it to describe a write that touched *many* rows. Done
separately, the table's shape would be decided twice, and the second decision would be made against a
column the first had just settled.

## 0. What this inherits and must not re-decide

- **The record's identity is `(key, scope)`**, scope being `IdentityOf(context)` = tenant + acting user,
  and an anonymous caller cannot hold a key at all (`AlvoIdempotency.EnsureUsableKey`). Unchanged.
- **The record stores a row reference, never a rendered body**, and a replay re-reads through the
  caller's *current* `get` decision. `IdempotencyTable`'s own remarks give the reason; this PR extends
  what "a row reference" means and changes nothing about the rule.
- **A caller whose `get` is denied outright gets an id-only answer rather than a refusal**
  (`IAlvoData.CreateAsync`'s contract). Extended, not re-argued.
- **The record's primary key is the concurrency control**, and `ReplayableCreateAsync`'s bounded retry
  is how a lost race becomes a replay. Reused verbatim for the new write paths.
- **PR-G's `DataApiEndpointKind`** is how a new generated route acquires its own `operationId`, prose,
  parameters and response catalogue. The batch routes are new **kinds** — three of them, not one, because
  `Protect` gates a route with exactly one `DataOperation` and the three batches gate as `create`,
  `update` and `delete`. Three kinds, three routes, three `operationId`s, three response catalogues; the
  routing suite's marker-matches-verb fact enforces it.

## 1. #102's real content is one line the issue does not mention

The issue asks for a replayable-result shape and a widened signature. Both are needed. But the change
that decides whether the feature is *correct* rather than merely present is this:

> **The fingerprint must cover the row id.**

`IdempotencyFingerprint.Of(method, entity, body)` carries no id today, and it did not need one: a create
has no id to carry. On an update it is the difference between a feature and a defect. With the id
absent, `PATCH /vehicles/A` and `PATCH /vehicles/B` with the same body and the same key have the *same*
fingerprint — so the second is answered as a **replay of the first**, and row B is never written while
the caller is told it was. That is exactly the failure mode `IdempotencyFingerprint`'s own remarks call
"silently wrong… the caller holds an id for a row that does not contain what they sent", one level up.

So `Of` gains the id and the digest becomes `(method, entity, id, body)` — the whole request.

Two things this forces, both **requirements on the implementation rather than properties it will have
for free**:

- **The id segment is appended only when there is an id.** The obvious widening —
  `$"{method}\n{entity}\n{id}\n{body}"` — digests a create as `POST\nvehicles\n\n{…}`, which is a
  *different* digest from today's: every in-flight create would become a 409. A create must produce the
  byte-identical input it produces now, and a fact must hold that rather than a comment claiming it.
- **`Of` must accept a body-less request.** It opens with `ArgumentNullException.ThrowIfNull(body)` and a
  `DELETE` has no body at all. The body becomes nullable and canonicalises to nothing; `method` is
  already in the digest, so a `DELETE` cannot collide with a `PATCH` that sent `{}`.

**And the port's own prose is stale, so this PR fixes it.** `AlvoIdempotency.Fingerprint` says the digest
covers "the method, **the path** and the body", while `IdempotencyFingerprint`'s remark records that the
route template was deliberately *removed* — it embedded `RoutePrefix`, so a redeploy invalidated every
fingerprint. The two have disagreed since. Adding the row id restores the part of "the path" that
identifies the row, which is the part that mattered; the port's sentence is corrected to say so.

### 1.1 The precondition is in the digest too

`PATCH … If-Match: "v1"` and the same `PATCH` unconditioned are two different requests: one asks to
write only if the row is at v1, the other asks unconditionally. A key that ignored the difference would
let them share one record, and a caller who retried with a corrected precondition would be answered with
the result of a write that never checked it.

So the precondition joins the digest — as **`Version.UtcTicks`**, not as a formatted timestamp.
`AlvoPrecondition.EnsureMatches` compares instants, so two `DateTimeOffset`s at different offsets are one
precondition to the port; a digest over `"O"` or `ToString()` would make them two, and an embedded host
passing the same instant at a different offset would earn a 409 for a request the port treats as
identical. `RowVersionETag.Encode` already digests the ticks, so this is the existing spelling rather
than a new one.

The cost is precise and acceptable: a client that retries with a
*different* `If-Match` gets a 409 rather than a replay. That is the direction
`IdempotencyFingerprint`'s own remark calls "a conflict is the safe way to be imprecise" — and a client
retrying the *identical* request, which is the whole scenario #102 exists for, is unaffected.

## 2. What a replay answers, per verb

| Verb | Recorded | A replay answers |
|---|---|---|
| `POST` | the created row's id | the row, re-read under the replaying caller's `get` — unchanged |
| `PATCH` | the updated row's id | the row, re-read under `get` — **not** the row as the write left it |
| `DELETE` | the deleted row's id | **204, with no read at all** |

**A `PATCH` replay re-reads and does not replay a stored image**, which means it can answer a row that
has since changed again. That is the honest behaviour and it is the same one a create's replay has: the
record is a statement that *this caller's write happened*, not a snapshot of what it produced. A caller
who needs to know the row is still as they left it has `If-Match` for that, and #102's own scenario asks
only "did my write land" — which the 200 answers.

**A `DELETE` replay reads nothing**, and it cannot: the row is gone. The record's existence *is* the
answer, and it is the whole value of the feature — without it the retry is a 404 (or a 412) that the
caller cannot tell from somebody else's delete, which is what `data-api.md` currently documents as the
cost of ignoring the header.

**Every replay still refuses on a fingerprint mismatch** with `AlvoIdempotencyConflictException` → 409,
exactly as a create does. A key is one request, whichever verb it was.

## 3. The record grows one thing, and #106 is why

For #102 alone the table needs **no change at all** — a single-row write records one row id, which is
the column that already exists. It is #106 that breaks it: a batch is one request under one key, and its
replay must answer for *N* rows.

So the record's `row_id` column **keeps its name and widens what its text means**: it holds a JSON array,
and a single write stores a one-element one.

**The column is deliberately not renamed, and the first draft of this section was wrong to.** It said the
rename was free because nothing outside `IdempotencyTable` reads the column — true — and in the same
breath that there is no DDL change, which cannot both hold. `SystemSchemaInitializer` and
`EfAlvoData.EnsureIdempotencyTableAsync` both create the table with `CREATE TABLE IF NOT EXISTS`, so an
existing database keeps the old shape and every statement naming `row_ids` fails with *no such column*.
That is a `DbException`, which the contended-write retry matches — so it would be retried ten times over
~450 ms and surface as an unattributable 500. A misnamed column is a smaller cost than a migration this
PR does not need.

- **No DDL change, and now that is true.** The column is already `TEXT` — `IdempotencyTable`'s own
  remarks explain why it is text rather than a native uuid — so widening what the text *means* needs no
  `ALTER TABLE` at all.
- **The reader tolerates the old shape.** A value that does not begin with `[` is read as one id. Two
  lines, and they keep a developer's existing local database working across this commit rather than
  failing at the first replay.
- **The name is a documented understatement, not a lie.** `row_id` holds "the rows this record covers",
  which is one for every write the API has ever had and more only for a batch. The column's own remark
  says so, and `docs/architecture/data-api.md` and `data-path.md` both name the column and are corrected
  with it.

## 4. #106 — the port shape: three members, not one

`IAlvoData` gains three members that mirror its three single-row writes:

```csharp
Task<AlvoBatchResult> CreateManyAsync(string entity, IReadOnlyList<IReadOnlyDictionary<string, object?>> rows, AlvoContext context, AlvoIdempotency? idempotency = null, CancellationToken cancellationToken = default);
Task<AlvoBatchResult> UpdateManyAsync(string entity, IReadOnlyList<AlvoRowPatch> rows, AlvoContext context, AlvoIdempotency? idempotency = null, CancellationToken cancellationToken = default);
Task<AlvoBatchResult> DeleteManyAsync(string entity, IReadOnlyList<Guid> ids, AlvoContext context, AlvoIdempotency? idempotency = null, CancellationToken cancellationToken = default);
```

**Rejected: one `BatchAsync` taking a heterogeneous list** of create/update/delete entries. It would
admit a mixed batch — insert some rows and update others in one transaction — which is what an import
actually wants. It is refused here for two reasons: the natural spelling of "insert or update" is
*upsert*, which is **#105** and the next PR, so building the mixed form now would foreclose that
design; and a single member switching on a per-entry mode is one thing a provider can implement three
ways, where three members are three things it implements once each. Recorded as a deviation from the
"one transaction for an import" reading of #106, with the consequence stated: **an import that must both
insert and update atomically is not expressible until #105 lands.**

**Rejected: no precondition per row.** A batch takes no `AlvoPrecondition`. Conditioning 500 rows on 500
versions is a coherent feature and a much larger one — it needs a per-row 412 in a response whose status
is already decided by the batch as a whole. Out of scope, recorded, and the single-row `PATCH` remains
the way to condition a write.

## 4.1 #106 — the route: one path, three operations, gated by the verb it is

`{prefix}/{entity}/batch`, taking `POST` for a batch create, `PATCH` for a batch update and `DELETE` for
a batch delete. One path, three operations, and each gated as the `DataOperation` its verb already means
— so a caller who may `create` but not `delete` is refused by the same filter that refuses them on the
single-row routes, with no new vocabulary.

**Rejected: an array body on the collection route** (PostgREST's own bulk insert). `POST {entity}` already
refuses a non-object body with a published violation (`not-an-object`), and turning that refusal into a
feature is a change to a contract callers may already handle.

**`DELETE` with a body is the one uncomfortable part**, and it is chosen with the discomfort recorded:
RFC 9110 §9.3.5 leaves a `DELETE` body's semantics undefined and an intermediary is entitled to strip it.
A stripped body would arrive as an empty batch — so an empty batch is **refused**, never treated as "no
rows to delete", which turns the hazard into a 422 instead of a silent success. The alternative,
`POST {entity}/batch/delete`, spells a delete as a post and was refused for that.

## 5. #106 — per-row policy is the whole of the security content

**The batch is two passes inside one transaction, and that is forced rather than chosen.** The single-row
helpers `WritePayloadGuard.EnsureWritable` and `EnsureWriteAllowed` **throw on the first failure**, and on
PostgreSQL any statement error aborts the transaction (`25P02`) until it is rolled back — so "catch per row
and keep going" is not available at all. A batch therefore:

1. **judges every row**, collecting refusals rather than throwing;
2. **refuses the whole batch** if the collection is non-empty, having written nothing;
3. **writes every row** only when it is empty.

### 5.1 The judging pass judges what will be written, hooks included

**This is the subtlest thing in the PR and the first draft of this section got it wrong by omission.**
The single-row paths do not judge the caller's payload — they judge the payload *after* the entity's
before-hooks have patched it. `EfAlvoData.RunBeforeCreate`'s own remark says why, in the sharpest terms
available:

> "A patch reaching storage unjudged would be a caller-reachable authorization bypass — a hook writing
> `owner_id` from a field the caller controls would place a row the `create` rule refuses — so the
> post-image verdict runs again over exactly what will be written."

Splitting judge from write puts a seam exactly where that invariant lives. So the rule is stated as an
invariant rather than left to the implementation:

> **Hooks run once, in the judging pass. The write pass writes byte-identically the post-image the verdict
> consumed, and constructs no image of its own.**

The judging pass therefore produces, per row, the *final* stored image — stamped, hook-patched, judged —
and the write pass is a loop over images. A write pass that re-derived an image would be able to write one
nothing judged, which is the bypass at batch scale. §10 asks for a fact, not a remark.

### 5.2 An invisible row and an absent row are one refusal

A row the caller's `USING` predicate excludes raises `AlvoRecordNotFoundException` on the single-row path,
and that refusal is a **third** expression of the write verdict which §6's inversion does not reach.

In a batch it becomes a collected refusal, and **it must be byte-identical for a row that does not exist
and a row that exists but is invisible**. Otherwise `UpdateManyAsync` and `DeleteManyAsync` become a
cross-tenant existence oracle that answers `MaxBatchRows` questions per request — the same oracle the
single-row 404 exists to close, multiplied by the batch size. One code, one message, no distinction.

### 5.3 Ordering, and the one engine the first draft reasoned past

**The rows are sorted by id before either pass.** Two concurrent batches whose id sets overlap take their
row locks in whatever order the callers wrote them, which is a deadlock on PostgreSQL. A fixed order is
the standard fix and costs one sort; the *request* order is kept separately, because a caller's row 3 must
be reported as row 3.

**The contended-write retry stays wide for a batch, and the first draft was wrong to narrow it.** The
narrowing looked obviously right — the retry exists for a rival committing the same idempotency key, which
fails one statement, so retrying N inserts ten times is waste. But `RollupRecompute` carries an in-repo
*measurement* that kills it:

> "SQLite must **not** read the parent before writing inside a deferred transaction — 12 of 24 writers died
> on `SQLITE_BUSY_SNAPSHOT` when they did"

A judging pass is N reads before the first write, in a deferred transaction, which is that shape at N times
the width. `IsStorageWriteFailure` matches `DbException`, so `SQLITE_BUSY_SNAPSHOT` is absorbed by the wide
retry today and would not be by a narrow one. So the retry stays wide, and the cost is stated: an
unrecognised storage failure on row 400 costs ten full batch attempts before it surfaces. A recognised
constraint violation escapes early through `ConstraintViolationTranslator`, which is the common case.

The alternative — opening the batch's transaction as `IMMEDIATE` on SQLite — is a per-engine transaction
mode, therefore an `IAlvoSqlDialect` member, therefore a port change this PR does not need. Recorded as the
better fix if the wide retry ever costs too much.

### 5.4 What each row is judged against

The judgement is the one a single-row write already makes, in the same order:

- `WritePayloadGuard`'s refusal per row — a framework-managed column, a `readOnly` field, an undeclared key.
- The entity's before-hooks per row (§5.1), which are stateless per call and carry no cross-row budget.
- `WITH CHECK` and the tenant scope per row, over *that row's* post-image. For an update that post-image is
  *that row's* stored image merged with *that row's* patch, so a batch cannot smuggle a value past a check
  by pairing it with a row that passes.
- The `USING` predicate per row (§5.2).

**"Checks the first row and admits the rest" is what this arrangement makes unrepresentable**, and the
arrangement is structural: the judging pass calls the same per-row predicates the single-row write calls,
once per row, and the write pass does not run until the judging pass has consumed every row.

**Three things the loop must not do per row**, each of which the single-row path does once:

- **`RecomputeRollupsAsync` is called once**, after the writes, with every stored image.
  `RollupRecompute.TenantOf` throws unless the images carry exactly one distinct `tenant_id` — which a
  batch satisfies only because the tenant scope forces it, so that contract's wording is corrected to say
  "one write's rows" rather than "one stored row".
- **One instant covers the batch**, threaded into every audit stamp, every event's `time` and the record's
  `created_at` — so all N rows share one `updated_at` and one `ETag`. They were written together.
- **`EnsureNotSoftDeleted` is called once**, before the transaction: an entity that cannot be deleted at
  all is not a per-row refusal.

## 6. #106 — the failure report

**The port gets its own refusal type, and it is not `AlvoViolation`.**

```csharp
public sealed record AlvoRowRefusal(int Index, string Code, string Message, string? FixSuggestion);
```

**Rejected: moving `AlvoViolation` into the ports**, which the first draft proposed. It is an HTTP shape:
every member is pinned with `[JsonPropertyName]`, and half its `Pointer` contract is a PostgREST
query-parameter role vocabulary — `filter`, `order`, `limit`, `offset`, `after`, `select` — that no
`IAlvoData` implementor can ever produce. Moving it would put the API's wire format and its query grammar
in a package whose own description is "the ports and pure model". `AlvoRowRefusal` carries an `int` index
instead of a pointer string, which is also the truer type: the port knows a row's position, not a JSON
Pointer.

The HTTP layer maps it. `/rows/3/quoted_price` is the pointer a caller sees, composed where pointers are
already composed, and the `/rows/{index}` prefix is the API's convention rather than the port's.

**The message rule travels as a port obligation.** `AlvoViolation`'s remark — *"nothing here may echo
caller-supplied text… the cheapest oracle in the framework"* — holds today only because every producer is
internal to `MMLib.Alvo`. `AlvoRowRefusal` is produced by a *provider*, so the rule becomes something a
third party must honour: it is stated on the type, and §10 asks the contract suite to hold it.

**Every offending row is reported, not the first** — a 500-row import that reports row 3 and stops will be
run five hundred times.

**A policy refusal reports its index**, which is a deliberate narrowing of what a single write discloses.
Naming *which row* was refused tells the caller something about rows **they themselves sent**, and it is
the only thing that makes a batch fixable.

**A constraint conflict does not.** This is the asymmetry worth reading twice. A `unique` collision
surfaces from the engine during the *write* pass — so on PostgreSQL exactly one is knowable before the
transaction aborts, and "every offending row" is not available for it anyway. But the deciding reason is
the oracle: unique fields are **caller-guessable** where a framework-assigned row id is not, so a batch of
500 candidate emails answered with the colliding indices is 500 registration probes in one request.
`AlvoConstraintViolationException` keeps its message value-free for exactly this reason; an index would
re-attach the value by position. **The batch's 409 names the field, as the single-row 409 does, and no
index.** An intra-batch collision — two rows of one batch carrying the same unique value — is invisible to
the judging pass and surfaces the same way; recorded rather than fixed, because catching it would mean
re-implementing every `unique` constraint in the judging pass.

**The status a refusal maps to**, stated once because the first draft left it to two sections that
disagreed:

| Refusal | Status |
|---|---|
| any policy refusal — `WITH CHECK`, the tenant scope, a `readOnly` field, a managed column, an invisible or absent row | **403** |
| anything the entity's declared shape refuses — a type, a `maxLength`, a missing `required` | **422** |
| a mix | **403** — default-deny dominates, and a caller told to fix a field would fix it and be refused again |
| a constraint the database enforces | **409**, naming the field and no index |

## 7. #106 — the bound, and it will be measured

**A bound already exists, it is the wrong one, and it fires with the wrong message.**
`BoundedJsonBody` counts property names **at every depth**, so a batch body of N rows with K fields
carries `1 + N·K` names against `MaxPayloadKeys` (512): about **511 rows** for a one-field entity, **170**
for three fields, **102** for five, **51** for ten. The first draft of this section said 20 000 rows and
was wrong by up to forty times — it reasoned from the byte bound, which never binds.

So the batch reader does **not** inherit the flat name count. It counts **rows** against
`AlvoApiOptions.MaxBatchRows`, and applies `MaxPayloadKeys` **per row**, which is what that number has
always meant on a single write. A batch refused for its size then says *"more than N rows"* and not
*"more than 512 fields"* — which is the "advice about the wrong thing" this file refuses everywhere else.

`MaxBatchRows` is added, and per #106's own instruction it is **measured the way `AlvoFilter.MaxTerms`
was measured**, not picked:

- On SQLite and PostgreSQL, batch create / update / delete at increasing N, recording wall time,
  allocation, and the transaction's lock duration — **over an entity that carries a `WITH CHECK`
  predicate and a tenant scope**. The entire cost this PR adds is the per-row rule evaluation, so a
  harness over a rule-free entity would calibrate the bound from a cost the real path never pays.
- **The byte bound is measured beside it**, because for a wide row `MaxRequestBodyBytes` (1 MiB) binds
  before `MaxBatchRows` and produces exactly the "advice about the wrong thing" this section exists to
  prevent. The evidence file states the row width at which the two cross.
- The measurement is written to `docs/superpowers/specs/evidence/`, and the number in the code is the
  one the measurement supports, with the failure mode named — as `MaxTerms`' own remark names
  "900 filter terms answered in 14 ms and 1000 threw a raw `SqliteException`".

The plan carries the measurement as its own task, before the constant is written. **If the measurement
does not produce a clean ceiling, the number is recorded as chosen and the reason is recorded with it** —
which is what `MaxPatternLength` did in PR-G rather than pretending to a measurement it did not have.

## 8. #106 — one event per row, and the follow-up that is not this PR

A batch of 500 creates emits **500 outbox rows**, inside the batch's own transaction.

That is the existing semantics and it is deliberately unchanged, because a subscriber's contract is
per-row: `entity.work_orders.created` carries one row, and a batch that emitted one event carrying 500
would be a different event a subscriber has never seen.

**The source names the cost.** `baas-analyza` §3 is explicit: *"import 10k riadkov nesmie znamenať 10k
webhookov — rules deklarujú, či chcú per-item alebo batch doručenie (event
`entity.orders.created.batch` s poľom záznamov)"*, and its acceptance criterion is *"Bulk insert 10k
riadkov s batch pravidlom = 1 batch event"*. It also names Directus's per-item behaviour as a documented
scaling problem — which is exactly what this PR ships.

**The N events order correctly despite sharing one instant**, which is worth one line rather than an
assumption: `AlvoEventId.Create` carries a monotone tail, so N ids minted from one `now` still sort in the
order they were minted.

It is still the right call **for this PR**, and the reason is that coalescing is a *descriptor* feature,
not a write-path one: a rule has to declare `batch` delivery, which is a schema change, a compiler
change and a new event shape. Building it inside a data-path PR would make the descriptor change
invisible. **Filed as its own issue**, referenced from `docs/architecture/events.md`, and named here so
that a 500-row import fanning out to 500 webhook deliveries is a known cost rather than a discovery.

## 9. The public surface this adds, and why each symbol

This is the first PR in the chain to widen the published contract at all, so every symbol is justified
against `alvo-architecture-rules`' *"public is the contract"* individually. Nothing is released, so
there is no compatibility obligation — but a symbol added now is one a consumer can depend on from the
first release, which is the same cost paid later.

| Symbol | Why it must be public |
|---|---|
| `IAlvoData.CreateManyAsync` / `UpdateManyAsync` / `DeleteManyAsync` | The port is the seam a provider implements. A batch that lived only in the core could not be served by a different driver, which is §0 principle 2. |
| `AlvoRowPatch` (`Guid Id`, `IReadOnlyDictionary<string, object?> Values`) | `UpdateManyAsync`'s element type. A tuple would be untitled in every implementor's signature and could not carry documentation. |
| `AlvoBatchResult` | What a batch returns. It carries `Affected` as well as `Rows`, because a delete produces no rows — so without a count a successful delete and a refusal are **both** an empty list, and a caller reading only `Rows` could not tell them apart. A returned-refusal channel that fails open is the wrong default in a port where every other refusal throws. |
| `AlvoApiOptions.MaxBatchRows` | A host must be able to lower it; every other payload bound is configurable for the same reason. |
| `AlvoIdempotency` on `UpdateAsync` / `DeleteAsync` | A **breaking change for implementors**, and — because it is inserted before `precondition` — a compile break for every **positional** caller too, including `DataApiEndpoints`' own two. Optional-with-a-default does not save them; it only makes the break loud rather than silent. Nothing is released. |
| `AlvoRowRefusal` | §6: a batch's refusals travel on its result, so the port must be able to name a row's refusal — and `AlvoAuthorizationException` carries a message and nothing else. **Not** `AlvoViolation`, which is an HTTP shape carrying a query-parameter vocabulary no implementor can produce. |

Everything else — the batch reader, the batch endpoint, the per-row violation shape — stays `internal`.

**Three implementors must gain the three members**, and the first draft listed none:
`EfAlvoData`, `InMemoryAlvoData`, and `test/_shared/api/FaultingAlvoData.cs` — the last a test double whose
whole contract is that every member throws the fifth failure family, so its three additions must too.

**Three approval baselines move**, and the turn hook blocks a turn that grows any of them until the
`alvo-architecture-rules` pass has justified each symbol:
`PublicApi.MMLib.Alvo.Abstractions.verified.txt` (the three port members, the two changed signatures,
`AlvoRowPatch`, `AlvoBatchResult`, `AlvoRowRefusal`), `PublicApi.MMLib.Alvo.verified.txt`
(`MaxBatchRows`), and `PublicApi.MMLib.Alvo.Testing.verified.txt` — that last one lists **test method
names**, so it moves twice: once for `InMemoryAlvoData`'s three members and again for every fact §10 adds
to the shared suite.

## 10. How each claim is proved

The contract suite (`src/MMLib.Alvo.Testing`) is where the port-level facts go, because they must hold
identically on SQLite, PostgreSQL and the in-memory reference — that is what makes them contract rather
than implementation. **Nothing wires that automatically:** a new abstract class needs a hand-written
subclass in each of `MMLib.Alvo.Data.Sqlite.Tests`, `MMLib.Alvo.Data.PostgreSql.Tests.Integration` and
`MMLib.Alvo.Tests/Data`, and the PostgreSQL leg lives in an integration project that ring0 and ring1 skip
entirely and ring2 runs affected-scoped.

**One fact has no in-memory leg and cannot have one:** `InMemoryAlvoData` has no outbox, so "events are
per row" belongs in `AlvoDataOutboxTests`, which runs on the two real engines only. Stated rather than
quietly two-thirds covered.

**And the in-memory reference has no transaction at all** — it mutates a `List<AlvoRecord>` under a lock,
with no rollback. "A batch whose last row fails leaves no row written" has to be *built* there, by staging
into a copy and swapping under the same lock. That is the one place the atomicity claim can be
implemented wrong and still stay green, so the fact must run against all three implementations rather
than being taken from the two that get it from the database.

| Claim | Fact |
|---|---|
| The fingerprint covers the id | The same key and the same body against **two different rows** is a 409, not a replay of the first. This is the fact whose absence would make #102 a defect. |
| The fingerprint covers the precondition | The same key and body with a different `If-Match` is a 409. |
| An update replays | A lost 200, retried: 200 with the row, and **the row was written once** — asserted as a version, not as a status. |
| A delete replays | A lost 204, retried: **204**, not 404 and not 412. |
| A replay honours the *current* `get` | A caller whose `get` is denied outright gets the id-only answer; a caller a configured `get` rule now excludes gets 404. Both inherited from the create's contract and asserted on the new verbs. |
| A batch is one transaction | A batch of N whose **last** row fails leaves **no** row written — asserted as a count over the whole entity, on both engines. |
| Policy is per row | A batch whose rows each pass in isolation but whose combination the `WITH CHECK` refuses is refused; and a batch of N rows for a caller whose tenant covers N−1 of them writes nothing. |
| **A hook cannot patch a row past the check** | An entity whose `beforeCreate` writes a field the `WITH CHECK` predicate reads: the batch refuses and names that row. This is `EfAlvoData`'s own documented bypass at batch scale, and today nothing would catch it. |
| **The row that lands is the row that was judged** | A counting hook asserts it ran exactly once per row, and the committed row is compared with the post-image the verdict consumed. |
| **Invisible and absent are one refusal** | A row of another tenant and a row that never existed produce the **identical** code and message — otherwise a batch answers `MaxBatchRows` existence questions per request. |
| **A refusal carries no caller text** | The port obligation §6 states: every `AlvoRowRefusal` a batch produces is screened for the values and keys the caller sent. |
| Cross-tenant, as a test | A batch naming one row of another tenant writes nothing and reports that index — over a tenant-scoped fixture, with the control that the same batch from the owning tenant succeeds. |
| Every bad row is reported | A batch with three bad rows answers three violations, with indices 1, 4 and 9 — not one. |
| The bound bounds, and the right one speaks | `MaxBatchRows + 1` rows is refused **naming the row bound**, not the field bound — the fact asserts the code, because §7's whole point is that the flat key count would otherwise fire first with the wrong message. A single row past `MaxPayloadKeys` is still refused per row. |
| The batch is idempotent as a unit | One key, replayed: the same N rows, and **N rows exist, not 2N**. |
| A successful delete is not an empty refusal | `DeleteManyAsync` answering `Affected = N` is distinguishable from a refusal answering `Affected = 0` — the fact a caller reading only `Rows` would have got wrong. |
| Events are per row | A batch of 5 emits 5 outbox rows, in the same transaction — asserted so the follow-up issue has a number to change. |

## 11. Scope

**In:** the fingerprint change, the two widened signatures, the record's widened `row_id`, three port members
and their EF + in-memory implementations, the batch route and its OpenAPI operation, the per-row
violation pointer, `MaxBatchRows` and its measurement, the contract facts above, and the `data-api.md`
sections that currently say the header is ignored.

**Out, each for a reason:**

- **A mixed batch** — §4. It is upsert's shape, and upsert is #105.
- **Per-row preconditions** — §4.
- **Event coalescing** — §8, filed separately.
- **A batch `query`** — reads are already unbounded by row count; `POST …/query` (PR-G) answers the
  read-side version of this problem.
- **Retention or eviction for `alvo_idempotency`** — the table grows and always has; unchanged here and
  not made worse, since a batch stores one record rather than N.
- **A multi-row `INSERT`.** The batch issues one statement per row, as the single write does. A composed
  multi-row insert would be a new SQL-composing file, which `ChangeTrackerReachTests`' allow-list governs,
  and it would trade a measured shape for an unmeasured one. If §7's measurement shows the round trips
  dominate, that is the follow-up it justifies.

## 12. Deviations from the sources, recorded

1. **From `baas-analyza` §2.1's "bulk operácie (batch insert/update/delete, transakčné)"** — shipped as
   three separate operations rather than one mixed batch. §4.
2. **From `baas-analyza` §3's batch-delivery requirement** — a batch emits one event per row. §8, with
   the issue that closes it.
3. **From PostgREST** — its bulk insert is an array body on the collection route, and its bulk update
   and delete are *filter-based* (`PATCH /table?status=eq.x`). Alvo takes neither: an array on the
   create route would turn a published refusal (`not-an-object`) into a feature, and a filter-based bulk
   update cannot carry per-row values, which is what an import needs. Alvo's batch names its rows.
4. **From #106 as filed** — it says "extend `alvo_idempotency`"; the extension turns out to be a
   widening of one column's meaning rather than a new column, and #102 alone would have needed none. §3.
5. **From #102 as filed** — it does not mention the fingerprint. §1 is the change that makes the feature
   correct rather than merely present.
6. **From RFC 9110 §9.3.5** — a `DELETE` carrying a body. §4.1: chosen for verb symmetry, with the
   stripped-body hazard turned into a refusal rather than a silent success.
7. **From "every offending row is reported"**, for the 409 alone: a constraint conflict names the field
   and no index, because a `unique` value is caller-guessable where a row id is not, and an index would
   turn one collision probe into `MaxBatchRows` of them per request. §6.
8. **From this design's own first draft**, recorded because the corrections are the substance: the
   `row_id` rename was a DDL change dressed as none (§3); the batch's row ceiling was stated as 20 000
   when `MaxPayloadKeys` already caps it near 100 (§7); "the same helpers in a loop" could report at most
   one bad row and, on PostgreSQL, could not continue after any (§5); the fingerprint's stability for a
   create was asserted as a property rather than required as one (§1); and the batch was described as one
   new endpoint kind where the gate forces three (§0). A second review pass killed five more: the judging
   pass omitted before-hooks, which is `EfAlvoData`'s own documented authorization bypass (§5.1); the
   `USING` refusal was a third expression of the verdict the inversion does not reach, and an invisible
   row was left distinguishable from an absent one (§5.2); narrowing the contended retry would have
   removed the only absorber of a *measured* SQLite failure mode (§5.3); `AlvoBatchResult` failed open,
   since a successful delete and a refusal were both an empty list (§9); and moving `AlvoViolation` into
   the ports would have carried an HTTP wire shape and a PostgREST query vocabulary with it (§6).
