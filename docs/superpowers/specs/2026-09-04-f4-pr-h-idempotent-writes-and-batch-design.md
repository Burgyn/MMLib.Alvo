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

**The batch is two passes, not one loop, and that is forced rather than chosen.** The single-row helpers
`WritePayloadGuard.EnsureWritable` and `EnsureWriteAllowed` **throw on the first failure**, and on
PostgreSQL any statement error aborts the transaction (`25P02`) until it is rolled back — so "catch per
row and keep going" is not available at all. A batch therefore:

1. **judges every row**, collecting refusals rather than throwing — which for an update means taking all
   N row-locked pre-images first, because the `WITH CHECK` verdict is reached over *that row's* locked
   pre-image merged with *that row's* patch;
2. **refuses the whole batch** if the collection is non-empty, having written nothing;
3. **writes every row** only when it is empty.

The first draft said "the same helpers, in a loop" and that was wrong twice over: it could not report more
than one bad row, and on PostgreSQL it could not report even one and then continue.

**The rows are sorted by id before either pass.** Two concurrent batches whose id sets overlap take their
row locks in whatever order the caller wrote them, which is a deadlock on PostgreSQL; a fixed order is the
standard fix and it costs one sort. The same applies to the rollup parents.

**Every row is judged individually**, and the judgement is the one a single-row write already makes:

- `WritePayloadGuard.EnsureWritable` per row — a framework-managed column, a `readOnly` field.
- `EnsureWriteAllowed` per row — the `WITH CHECK` predicate and the tenant scope, over that row's own
  post-image. For an update that post-image is *that row's* stored image merged with *that row's* patch,
  so a batch cannot smuggle a value past a check by pairing it with a row that passes.
- The `USING` predicate per row, through the same policy-rooted query the single-row path uses — so a
  row the caller cannot see is `AlvoRecordNotFoundException` for that row, not silently skipped.

**"Checks the first row and admits the rest" is the failure this design is arranged to make
unrepresentable**, and the arrangement is structural rather than careful: the judging pass calls the
*same* per-row predicates the single-row write calls, once per row, and the writing pass does not run at
all until the judging pass has consumed every row. There is no batch-shaped variant of the check to get
wrong, and an optimisation that hoisted one out of the loop would have to delete a call site to do it —
a diff a reviewer sees.

**The collecting shape is new and is where the risk actually lives.** The single-row path proves its
refusals by throwing; a collecting variant is a second expression of the same rule, and two expressions
of one rule is exactly how they come to differ. So the collecting predicate is the **only** implementation
and the single-row path is refactored to throw on *its* result — one rule, one evaluation, the throw
becoming a caller's choice rather than the check's.

**Three things the loop must not do per row**, each of which the single-row path does once and a naive
batch would do N times:

- **`RecomputeRollupsAsync` takes a list already** and `RollupRecompute` groups by parent precisely
  because one write can name a parent twice. Called per row, N children of one parent become N lock-plus-
  `SUM` statements instead of one. The batch collects its images and calls it **once**, after the writes.
- **The contended-write retry wraps the whole attempt.** Around a batch it retries N inserts, N hooks and
  N outbox rows, ten times — so an unrecognised storage failure on row 400 costs ten full batches before
  it surfaces. The batch keeps the retry (it is what turns a lost race on the idempotency key into a
  replay) and **narrows what it retries to a failure on the record insert itself**, which is the only one
  a rival can cause.
- **One instant covers the batch.** Every write site reads `WriteInstantNow()` once and threads it into
  the audit stamp, the event `time` and the record's `created_at`; a batch does the same, so all N rows
  share one `updated_at` and therefore one `ETag`. That is right — they were written together — and it is
  stated because `data-path.md`'s "one write is one instant" now has to say what "one write" means.

§10 states what a fact must prove rather than assert here, and it is not "a bad row is refused" — it is
**a batch whose *last* row fails leaves no row written**, plus a batch whose rows individually pass and
whose *combination* would not.

## 6. #106 — the failure report

One refusal for the batch, `422` with slug `validation` for a payload the entity's shape refuses and
`403` for a policy refusal, exactly as a single write. What changes is the pointer:

> `/rows/3/quoted_price` — RFC 6901 into the request body, where `3` is the batch index.

**The index needs a carrier, and today there is none.** `AlvoAuthorizationException` carries a message and
nothing else, and a 403 that says only "refused" cannot name a row. So a batch's refusals travel on
`AlvoBatchResult` as an `IReadOnlyList<AlvoViolation>` — which makes `AlvoViolation` reachable from the
port, and therefore moves it out of `MMLib.Alvo.Api` into `MMLib.Alvo.Abstractions`. §9 lists that as the
public-surface item it is; it is also the change that lets the *port* report a per-row policy refusal at
all, rather than the HTTP layer inventing one.

That is the existing `PayloadViolations.PointerTo` shape with a prefix, and it needs no new convention:
the pointer is already documented as "a JSON Pointer into the request body, or the role of a query
parameter" (PR-G published the rule that tells the two apart), and a batch body genuinely has that
structure.

**Every offending row is reported, not the first**, for the reason the single-row validator collects:
one violation per request is one round trip per mistake, and a 500-row import that reports row 3 and
stops will be run five hundred times.

**A policy refusal reports its index too**, which is a deliberate narrowing of what a single write
discloses: a single `403` says only "refused". Naming *which row* was refused tells the caller something
about the rows — but only about rows **they themselves sent**, and it is the only thing that makes a
batch fixable. Recorded as a decision, not a slip.

**A constraint conflict reports its index as well.** A batch import is exactly where a duplicate key
arrives, and `AlvoConstraintViolationException` names *fields* and no row — so a 500-row import that
collides on one `unique` value would say "some row collides on `reference`". The batch's 409 carries the
index on the same `violations` array, for the same reason the 422 does. The single-row 409 is unchanged.

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
  allocation, and the transaction's lock duration.
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
| `AlvoBatchResult` | What a batch returns: the rows, in request order. A bare `IReadOnlyList<AlvoRecord>` cannot say what a delete returns (nothing) or carry a count. |
| `AlvoApiOptions.MaxBatchRows` | A host must be able to lower it; every other payload bound is configurable for the same reason. |
| `AlvoIdempotency` on `UpdateAsync` / `DeleteAsync` | A **breaking change for implementors**, and — because it is inserted before `precondition` — a compile break for every **positional** caller too, including `DataApiEndpoints`' own two. Optional-with-a-default does not save them; it only makes the break loud rather than silent. Nothing is released. |
| `AlvoViolation` moves from `MMLib.Alvo.Api` to `MMLib.Alvo.Abstractions` | §6: a batch's refusals travel on `AlvoBatchResult`, so the port must be able to name a row's refusal. It is already public; this changes its assembly, which is why the baseline moves in two places at once. |

Everything else — the batch reader, the batch endpoint, the per-row violation shape — stays `internal`.

**Three implementors must gain the three members**, and the first draft listed none:
`EfAlvoData`, `InMemoryAlvoData`, and `test/_shared/api/FaultingAlvoData.cs` — the last a test double whose
whole contract is that every member throws the fifth failure family, so its three additions must too.

**Three approval baselines move, not one**, and the turn hook blocks a turn that grows any of them until
the `alvo-architecture-rules` pass has justified each symbol:
`PublicApi.MMLib.Alvo.Abstractions.verified.txt` (the port members, the two signatures, `AlvoRowPatch`,
`AlvoBatchResult`, `AlvoViolation` arriving), `PublicApi.MMLib.Alvo.verified.txt`
(`MaxBatchRows`, `AlvoViolation` leaving) and `PublicApi.MMLib.Alvo.Testing.verified.txt` — that last one
lists **test method names**, so every fact §10 adds to the shared suite moves it too.

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
| Cross-tenant, as a test | A batch naming one row of another tenant writes nothing and reports that index — over a tenant-scoped fixture, with the control that the same batch from the owning tenant succeeds. |
| Every bad row is reported | A batch with three bad rows answers three violations, with indices 1, 4 and 9 — not one. |
| The bound bounds, and the right one speaks | `MaxBatchRows + 1` rows is refused **naming the row bound**, not the field bound — the fact asserts the code, because §7's whole point is that the flat key count would otherwise fire first with the wrong message. A single row past `MaxPayloadKeys` is still refused per row. |
| The batch is idempotent as a unit | One key, replayed: the same N rows, and **N rows exist, not 2N**. |
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
7. **From this design's own first draft**, recorded because the corrections are the substance: the
   `row_id` rename was a DDL change dressed as none (§3); the batch's row ceiling was stated as 20 000
   when `MaxPayloadKeys` already caps it near 100 (§7); "the same helpers in a loop" could report at most
   one bad row and, on PostgreSQL, could not continue after any (§5); the fingerprint's stability for a
   create was asserted as a property rather than required as one (§1); and the batch was described as one
   new endpoint kind where the gate forces three (§0).
