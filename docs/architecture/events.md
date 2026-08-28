# The event backbone

How a committed write becomes a delivered after-hook action, and the decisions that shape it. Written
during F3 PR5a (#22); the *Before-hooks* section was added by PR5b-1 (#114), and *Publishing a custom
application event* plus *The wildcard ruling* by PR5b-2.

> **Status: complete for PR5a**, which is the *durable half* of #22, **plus PR5b-1's before-hooks**
> (#114) **and PR5b-2's publish guard and wildcard ruling**. Everything below describes what the code
> does today; where a decision was deliberately deferred it says so and names the PR or issue that owns
> it — see *What PR5a does not do* and *What PR5b and F7 inherit* at the end, which is where an author
> of the remaining PR5b work (automation) starts.
>
> **Sibling records:** [`data-path.md`](./data-path.md) owns the port and the SQL a read or a write
> becomes; [`host.md`](./host.md) owns the boot and the process; [`cel.md`](./cel.md) owns the profiles,
> including the `Mutate` profile a before-hook's `mutate` value compiles in. This file owns the queue
> and the delivery — the envelope, `alvo_outbox`, the claim, the dispatcher — and both hook pipelines
> that hang off a write. The split is along the same seam: a decision about a statement lives in
> `data-path.md`, a decision about the boot lives in `host.md`, a decision about a hook or an event
> lives here.
>
> **Measured evidence** for every number quoted below is
> `docs/superpowers/specs/evidence/2026-08-03-f3-pr5a-events/spike.txt`, cited by its question number
> (Q1–Q9). Where the spike and a design document disagree, the spike is the authority and the
> disagreement is named.

## Five stages, and one of them is inside somebody else's transaction

```
 emit  ──►  claim  ──►  deliver  ──►  mark  ──►  retire
   │          │            │           │           │
 inside     one        after-hook   dispatched_at  the row stays; nothing
 the write  UPDATE      actions      stamped       deletes or moves it
 txn        statement   over HTTP    (one stmt)
```

| Stage | Where it runs | What it is |
|---|---|---|
| **emit** | `EfAlvoData`'s four write sites, **on the caller's own `DbTransaction`** | `OutboxEventFactory.For(...)` builds the envelope, `OutboxTable.InsertAsync` appends one row on the same transaction and the same connection as the data change |
| **claim** | `OutboxDispatcher` → `IOutboxStore.ClaimAsync` | one portable `UPDATE … RETURNING` on an autocommit connection: take the oldest claimable entries, stamp `claimed_at`/`claimed_by`, increment `attempts` |
| **deliver** | `EventSubscriptions` then `EventActionExecutor` | pick the after-hooks this event is subscribed to (the CEL condition is part of the *subscription*), run each hook's `webhook`/`email` action |
| **mark** | `IOutboxStore.MarkDispatchedAsync` | one statement stamping `dispatched_at`, **after** the last action, so "dispatched" means every matched hook ran |
| **retire** | the counter, and the row that stays | `alvo.events.dispatched` (or `alvo.events.filtered`) is incremented **after** the mark. The row is never deleted or moved — an abandoned event is still countable and inspectable |

**"Mark" and "retire" are separate on purpose, and their order is load-bearing.** A mutation that stamps
`dispatched_at` *before* running the action turns
`KilledHostRecoveryTests.A_kill_mid_action_makes_the_action_repeat_after_a_restart` red — nothing else in
the suite pins that ordering. The counter increments after the mark for the same reason: a counter that
ran first would claim a delivery that the retirement had not yet made final.

**The failure branch is `release`, not a fourth stage.** A delivery that throws increments
`alvo.events.failed`, logs `ActionFailed`, and calls `IOutboxStore.ReleaseAsync`, which clears
`claimed_at`/`claimed_by` and deliberately **does not** roll `attempts` back. That is what makes the
attempt ceiling reachable at all.

## The envelope

CloudEvents **v1.0.2** (there is no 1.0.3; `main` is `1.0.3-wip`). The wire `specversion` value is
`"1.0"`. The type is hand-written in `MMLib.Alvo.Abstractions/Events/AlvoEvent.cs`, because
`Abstractions` may take no external dependency (`package-boundary.md`) — and because nothing in Alvo
needs the SDK at run time: Alvo serializes its own envelope for the outbox row and for the webhook body.
`CloudNative.CloudEvents` is therefore a **test-only** package used as a conformance oracle, never a
shipped dependency.

One real envelope, pinned verbatim by
`CloudEventsConformanceTests.One_real_envelope_is_pinned_verbatim`:

```json
{"specversion":"1.0","id":"019fc77e-be7b-72e8-b7fd-ffd6f6306e3e","source":"/alvo",
 "type":"entity.vehicles.updated","time":"2026-08-03T09:30:00.0000000\u002B00:00",
 "subject":"vehicles/3f2504e0-4f89-41d3-9a0c-0305e82c3301","datacontenttype":"application/json",
 "partitionkey":"vehicles:3f2504e0-4f89-41d3-9a0c-0305e82c3301","payloadversion":1,"chaindepth":0,
 "authtype":"apikey","authid":"key-42","correlationid":"4bf92f3577b34da6a3ce929d0e0e4736",
 "data":{"record":{…},"old_record":{…},"changed":["status"]}}
```

(The line breaks and the two elided images are this document's; the pinned snapshot is one line and
carries both images in full. Note the `\u002B` escape where the timestamp's `+` sits: the default HTML-safe
`System.Text.Json` encoder is kept, so `<`, `>`, `&` and `+` are escaped and a stored payload rendered
into a dashboard — or posted as a webhook body — is safe by default. The escaping is lossless, a named
fact says so, and the visible cost is exactly that timestamp. `causationid` is absent rather than `null`,
which is how the writer spells every absent optional attribute.)

### Three rules decide every name and every type

1. **Names.** Attribute names MUST consist of lower-case ASCII letters and digits only, and SHOULD NOT
   exceed 20 characters
   ([spec v1.0.2:173-175](https://github.com/cloudevents/spec/blob/v1.0.2/cloudevents/spec.md#attribute-naming-convention)).
   Extensions follow the same convention. So the base design's `payload_version`, `chain-depth` and
   `old_record` are **illegal as attribute names**: they became `payloadversion`, `chaindepth`, and
   `data.old_record`.
2. **Types.** Context attribute values are limited to **seven** abstract types — `Boolean`, `Integer`
   (int32), `String`, `Binary`, `URI`, `URI-reference`, `Timestamp` (`:179-217`). **There is no map,
   array or object type.** So `record`, `old_record` and the changed-field list cannot be attributes at
   any spelling; they live inside `data`, where the JSON is Alvo's own and `snake_case` is fine.
3. **Extensions are flat top-level JSON members** (`:439-440`), serialized exactly like standard
   attributes. A nested `"extensions": { … }` wrapper is non-conformant.

`AlvoEventAttributes` is the one authority for the spellings: `AlvoEventJson` writes and reads through
its members rather than through literals, the oracle iterates `Extensions` to prove every one satisfies
the naming rule, and the table below is the third reader.

### The attribute table, with its provenance split spelled out

| Attribute | Kind | Value | Provenance |
|---|---|---|---|
| `specversion` | standard | `"1.0"` | v1.0.2 |
| `id` | standard | a **monotonic** UUIDv7 (see *Ordering*) | v1.0.2 |
| `source` | standard | `/alvo` | v1.0.2 |
| `type` | standard | `entity.{entity}.{created\|updated\|deleted}` | v1.0.2 |
| `time` | standard | the write's own audit instant, always UTC | v1.0.2 |
| `subject` | standard | `{entity}/{rowId}` | v1.0.2 |
| `datacontenttype` | standard | `application/json` | v1.0.2 |
| `partitionkey` | extension | `{entity}:{rowId}` | **registered in v1.0.2** — the Partitioning extension |
| `payloadversion` | extension | `1` | Alvo's, legal by the naming rule (14 chars) |
| `chaindepth` | extension | `0` in this build | Alvo's |
| `authtype` | extension | `apikey` \| `system` \| `anon` | **post-1.0.2** — `extensions/authcontext.md` on `main` |
| `authid` | extension | the credential's id; **absent** for an anonymous caller | **post-1.0.2** — `extensions/authcontext.md` |
| `correlationid` | extension | the ambient W3C trace id, else the event's own id | **post-1.0.2** — `extensions/correlation.md` |
| `causationid` | extension | **always absent** in this build | **post-1.0.2** — `extensions/correlation.md` |
| `data.record` | payload | the post-image, **unmasked**; absent on a delete | Alvo's |
| `data.old_record` | payload | the pre-image, **unmasked**; absent on a create | Alvo's |
| `data.changed` | payload | the fields whose value moved, ordinally ordered | Alvo's |

**The provenance split matters for citation, not for style.** In v1.0.2, `documented-extensions.md`
lists exactly **five** known extensions — Dataref, Distributed Tracing, Partitioning, Sampling,
Sequence. **Auth Context** and **Correlation** are *not* in that registry; they exist as
`cloudevents/extensions/authcontext.md` and `cloudevents/extensions/correlation.md` on `main`
(post-1.0.2). They are adopted anyway, because they are the community's names, they satisfy the naming
rule, and inventing `actor` would be worse — but the document has to say where they come from, or a
reader checking the v1.0.2 registry concludes Alvo invented them. Each `AlvoEvent` member states its own
provenance in its XML docs for the same reason.

**`payloadversion` duplicates `type` + `dataschema`, and it is kept anyway.** The CloudEvents-native way
to version a payload is a versioned `type` and/or a `dataschema` URI. The base design mandates a payload
version from the first day, and an in-process subscriber switching on an integer is cheaper and less
error-prone than parsing a URI. It is recorded as a duplication rather than discovered by whoever notices
the two can disagree: if they ever do, **`type` wins** and `payloadversion` is the member that is wrong.

**`sequence`/`sequencetype` are deliberately not on the envelope.** They *are* registered, with specific
semantics — a lexicographically comparable **String**, scoped per `source`. There is no sequence column
to surface (see *The outbox*), and publishing a monotonic integer under that name would be a different
contract than the registry's.

**The 64 KB rule this envelope can exceed.** Intermediaries MUST forward events of 64 KB or less
(`:510-512`), and `data.record` plus `data.old_record` on a wide row can pass that by themselves. The
registered escape is **`dataref`** (Dataref / claim-check): a `URI-reference` to the payload, which MAY
coexist with `data`. It is documented and **not implemented** — Alvo's own outbox is not an intermediary
and no wire hop in F3 is bound by the rule. Filed as **#151**.

**The read side answers JSON's view of a row, not the row's CLR types.** `AlvoEventJson.Read` gives back
a `uuid` field as its text. That is named and pinned rather than discovered: the read side's consumer
evaluates CEL and renders templates over the textual view anyway, and the authoritative typed record
lives on the write path, where the schema is in scope. A value the *writer* does not recognise is
refused with the field named, never stringified through `ToString()`.

## The outbox

One table, `{prefix}_outbox` (`alvo_outbox` by default), created by `SystemSchemaInitializer` alongside
the descriptor-versions and idempotency tables — a framework bookkeeping table, not a product of the
declarative diff engine. Its name is in `SystemSchemaInitializer.FrameworkTableNames`, so the
introspector does not plan a `DROP` for it and a second apply produces an empty plan.

```sql
CREATE TABLE IF NOT EXISTS alvo_outbox (
    id            TEXT    NOT NULL PRIMARY KEY,   -- monotonic UUIDv7; the queue order
    event_type    TEXT    NOT NULL,
    partition_key TEXT    NOT NULL,               -- named after the registered `partitionkey`
    payload       TEXT    NOT NULL,               -- the whole envelope, as AlvoEventJson writes it
    created_at    TEXT    NOT NULL,               -- StoredInstant.Text of the envelope's own `time`
    claimed_at    TEXT    NULL,
    claimed_by    TEXT    NULL,
    attempts      INTEGER NOT NULL,
    dispatched_at TEXT    NULL
)
```

`claimed_at`, `claimed_by` and `dispatched_at` are the only nullable columns, and their nullability
**is** the queue's state machine: unclaimed is `claimed_at IS NULL`, undelivered is
`dispatched_at IS NULL`. Both read as SQL `NULL` semantics rather than as a sentinel, which is what lets
the claim be one portable statement.

**Provenance is not duplicated into columns.** The base design's column list carries `actor`,
`correlation_id` and `provenance_depth` as columns *and* the same values inside the payload. Two
authorities for one value is how they come to disagree. Only `partition_key` is duplicated, and only
because F7's partitioned claim must index it.

**Every timestamp goes through `StoredInstant.Text`** — `created_at`, `claimed_at`, `dispatched_at` —
the same helper `data-path.md`'s *Every timestamp is one instant* names. `StoredInstant` is `internal` to
the EF driver, so the envelope enforces the same rule at its own boundary: `AlvoEvent.Time` refuses a
value whose `Offset` is not `TimeSpan.Zero`. That resolves `data-path.md:386`'s forward reference to
"PR5's outbox".

### Why there is no `sequence` column

An `AUTOINCREMENT`/`IDENTITY` column would break `SystemSchemaInitializer`'s stated *"identical on
SQLite and PostgreSQL … no per-engine branching"* invariant, with zero precedent for breaking it — and
that is now measurement rather than reasoning. **Each engine refuses the other's spelling** (Q6): SQLite
refuses `… GENERATED BY DEFAULT AS IDENTITY` (`'near "AS": syntax error'`), PostgreSQL refuses
`AUTOINCREMENT` (`42601`).

The third option neither source document named — a **UUIDv7 primary key** — gives an ordering key with
identical ANSI DDL, no new port, and no integer watermark for anyone to be tempted by. Its portability
was measured, not assumed: **UUID text ordering agrees with .NET's ordinal sort on both engines** (Q2),
in both the `'D'` and `'N'` spellings, under `datcollate=en_US.utf8` as well as `COLLATE "C"`,
`COLLATE "POSIX"` and a native `uuid` column. So no collation-spelling fallback is needed. It holds
because every UUID text form is fixed width with its punctuation at fixed positions — a property of
**fixed-width keys**, not of that locale, and nobody should generalise it further.

**Three traps recorded so they are not re-run:**

- **SQLite silently *accepts* `seq SERIAL`** (Q6): it parses `SERIAL` as an unrecognised column type and
  gives a nullable column that never increments. A "portable `SERIAL`" therefore passes every SQLite
  test in CI and loses ordering in production. `OutboxTableTests` asserts its absence for exactly this
  reason.
- **`Guid`'s default byte order is not time-sortable** (Q1): `ToByteArray()` produced 5 050 inversions
  of 9 999, against 4 993 for both the `'D'` string form and `ToByteArray(bigEndian: true)`. So the
  column is safe as `TEXT` and would be unsafe as a `BLOB` written from `ToByteArray()`.
- **`created_at` was disqualified by a wide margin** (Q7): 10 000 successive `GetUtcNow()` reads produce
  **495** distinct `"O"` stamps with tie runs of 26, and **3** distinct values at millisecond precision.
  On top of that the audit stamp binds one instant per write, so ties inside a write are structural
  rather than merely likely.

### The claim, and why its outer `WHERE` is not redundant

```sql
UPDATE alvo_outbox SET claimed_at = @claimed_at, claimed_by = @claimed_by,
                       attempts = attempts + 1
 WHERE dispatched_at IS NULL
   AND (claimed_at IS NULL OR claimed_at < @stale_before)
   AND id IN (SELECT id FROM alvo_outbox
               WHERE dispatched_at IS NULL
                 AND attempts < @max_attempts
                 AND (claimed_at IS NULL OR claimed_at < @stale_before)
               ORDER BY id
               LIMIT @batch)
RETURNING id, event_type, partition_key, payload, attempts
```

**The outer `WHERE` repeats the subquery's claimability predicate, and that repetition is the whole
correctness of the statement.** The plan's first draft had the outer `WHERE` be nothing but
`id IN (subquery)`, and Q4 measured what that costs on PostgreSQL:

> *"A claimed 10, B claimed 10, overlap 10 (must be 0); rows with attempts > 1: 10"*

— **every row delivered twice**, and `attempts` incremented twice. The mechanism is `READ COMMITTED`
EvalPlanQual: when B's block on A's row locks clears, B re-evaluates **only its own outer `WHERE`**
against the row A just updated. The subquery's `claimed_at IS NULL` was evaluated before the block and is
never part of that re-check, so B's `id IN (…)` still holds and B re-claims what A took. With the
predicate repeated:

> *"A claimed 10, B claimed 0, overlap 0; rows with attempts > 1: 0"*

The subquery still chooses **which** rows; the outer `WHERE` re-validates that they are still claimable
at the instant of the write. Anyone tempted to "simplify" it away is reading the subquery as if it re-ran.

**Why the `ORDER BY` and the `LIMIT` are in the subquery.** `UPDATE … ORDER BY … LIMIT` is refused by
**both** engines, and the parser names `ORDER`, not `limit` (Q3): SQLite
`'near "ORDER": syntax error'`, PostgreSQL `42601 syntax error at or near "ORDER"`. So this is a
**portability** constraint, not a SQLite workaround — a correction to the risk register on both counts.
The bundled `e_sqlite3` also reports `SQLITE_ENABLE_UPDATE_DELETE_LIMIT` unset.

**Why the result is re-sorted in process.** `RETURNING`'s row order is arbitrary in measured fact on
both engines — `RETURNING already sorted: False` for SQLite *and* PostgreSQL (Q3) — so the subquery's
`ORDER BY` decides *which* entries are claimed, never in what order they come back. `EfCoreOutboxStore`
sorts what it read, ordinally.

**Why the claim filters `dispatched_at IS NULL` and never a high-water mark.** PostgreSQL sequences
commit out of order — one transaction can take 100 and commit after another took 101 and committed — so
"processed up to N" drops a row silently. Having no monotonic integer in the table *at all* is what makes
that wrong use unavailable rather than merely discouraged.

**Why there is no `SKIP LOCKED` and no new `IAlvoSqlDialect` member.** `SKIP LOCKED` exists to let
several claimants share one queue; PR5a has exactly one dispatcher, so it buys nothing, and adding a
member to a public port in a driver package is a public-API change a later F7 design would have to live
with. Q4 measured the cost of not having it, with the amended statement: a second claimant **blocks** on
the first's row locks and then claims **nothing**. So a second instance is slow, not incorrect — a claim
this design may make *only* because the outer predicate is there. Ordering is a separate matter and still
breaks with two dispatchers (**#150**).

**Why the claim is raw SQL and not LINQ.** `UseRelationalNulls()` is on in both drivers
(`data-path.md`, *`UseRelationalNulls()` is on*), so a LINQ predicate over a nullable column would have
to be written `x != null && x < y` against C#'s reading of the same text. Raw SQL has SQL's semantics
natively, so the constraint is met **by construction** rather than by whoever edits the file next
remembering it. `ChangeTrackerReachTests.The_outbox_claim_is_raw_sql_and_never_linq_over_the_context`
holds the line, and `OutboxTable.cs`/`EfCoreOutboxStore.cs` are both in
`ChangeTrackerReachTests._sqlComposingFiles`. This is why `data-path.md`'s prediction that *"PR5 adds
LINQ to this package"* was corrected rather than left standing: it did not.

**Why no transaction is opened around the claim, and why adding one would break SQLite.** Claim, mark
and release are one autocommit statement each. Q5 measured that a transaction which **reads and then
writes** is the single shape that fails unretryably with `SQLITE_BUSY_SNAPSHOT` (`Extended=517`) after
burning the whole 30-second retry loop under WAL — and fails the *request path* instead under the
shipped journal mode. Wrapping two store calls in a transaction "to be tidy" is the edit that would undo
it.

**Two Q5 corrections worth keeping.** It is *not* true that there is no default timeout anywhere:
`Microsoft.Data.Sqlite`'s `DefaultTimeout` is **30 s** and its retry loop covers `BEGIN`, which is what
already makes the shipped registration correct — a second writer waited ~1 s and then succeeded, in both
directions. **The shared SQLite registration is therefore unchanged.** And `journal_mode=WAL` is neither
needed nor revertible: it does not fix the read-then-write shape (it only moves whose write fails), and a
journal-mode change is **persistent in the database file**, so it is not a connection-string decision a
redeploy can undo.

## Ordering, stated with both of its conditions

> There is **no global ordering** (§3.3 calls it expensive and brittle). **Per-entity-key ordering holds
> while exactly one dispatcher runs *and* no two events for one key are written inside the same
> millisecond** — and only then. Delivery is **at-least-once** regardless, so every after-side action
> must be idempotent or deduplicated by event id.

**The envelope's `id` is the consumer's dedup key.** It is the only value that is stable across
redeliveries of one event — `attempts` changes, the delivery timestamp changes, the payload does not
carry a delivery count. A consumer that dedupes on anything else will double-apply.

**Why the millisecond is part of the guarantee.** The queue order *is* `ORDER BY id`, and the id is a
UUIDv7 whose ordering is exact only above the millisecond. `Guid.CreateVersion7()` has **no monotonic
counter** — it fills everything below the 48-bit millisecond with fresh random bits — and **49.9 %** of
adjacent same-millisecond pairs sort backwards (measured, Q1: 49 839 inversions over 100 000, of which
99 961 pairs shared a millisecond; across a millisecond boundary, 0 inversions in 38 pairs). The claim
that same-millisecond order was merely *unguaranteed* was wrong: it is a coin flip.

Alvo therefore mints every id through **`AlvoEventId`**, which reuses the last emitted millisecond and
increments the random tail whenever the clock's millisecond does not advance — measured at **0
inversions over 100 000**, at **no DDL cost**. So **within one process** the second condition is met and
the guarantee reduces to "one dispatcher". **Across processes it does not:** two hosts minting inside one
millisecond still interleave. That residue is **#150**.

`AlvoEventId` lives in `Abstractions` rather than in the core or the driver, for three reasons: the emit
sites are in a different assembly and see only `Abstractions`' public surface; `id` *is* the queue order,
so the contract belongs to the envelope rather than to one driver, and the failure mode of forgetting —
plain `CreateVersion7()` — is invisible from outside, since both spellings produce a valid v7 id; and it
is testable with no database, no host and no clock injection. `Create(DateTimeOffset)` returns an id whose
millisecond is the **later** of the requested one and the last already minted, which is what keeps the
total order intact. A bonus that falls out for free: a backwards clock step, which Q1 measured reorders
the queue by the size of the step, cannot reorder anything **within** one process.

**Operational constraint PR5a cannot enforce.** There is no distributed lock, so the dispatcher cannot
detect a second instance. Two replicas of the standalone image break the per-entity-key guarantee
**silently** — no error, no log. `partition_key` is written on every row from the first migration so F7's
partitioned claim is additive; until it lands, run one dispatcher. The supported multi-replica shape is
one replica with `Alvo:Events:Enabled` on and the rest off — switching it off stops delivery, never
emission. `FOR UPDATE SKIP LOCKED` is **not** the fix: it skips the row, not the key.

**This wording is deliberate, and it replaces two weaker ones.** The base design states the guarantee
flatly as *"per-entity-key ordering, partitioned by primary key"* — `baas-analyza.md:656` had hedged it
(*"ak sa dá"* — if possible) and the design dropped the hedge. The addendum's deviation 72 restored the
hedge but with only the **first** condition. Q1 is what added the second, and every place in this subsystem
that states the guarantee at all states **both** halves of it — `AlvoEvent`, `AlvoEventId`, `IOutboxStore`
and `AlvoEventOptions`. If you find one that states only the first, it is wrong. **`EfAlvoData`'s emit
remarks were listed here as a fifth and state neither**: they state the one-instant rule and never mention
ordering, which is fine — the guarantee is not the emit site's to make — but citing them was the same class
of citation defect this document corrects twice elsewhere, so the list now names the four places that really
carry it.

## Before-hooks

`beforeCreate`, `beforeUpdate` and `beforeDelete` are the other half of the hook subsystem, and they
are almost the mirror image of the after-hooks below: same compiler pass, same catalog, opposite side
of the commit. An after-hook is *told* about a write that happened and may reach the network; a
before-hook *judges* a write that has not happened yet, holds row locks while it runs, and can reach
nothing at all. Added by **PR5b** (#114).

Two actions, both from the frozen schema, and nothing else is expressible in-transaction:

| Action | Valid on | What it does | How the caller sees it |
|---|---|---|---|
| `reject` | all three points | refuses the write | `AlvoAuthorizationException` → **403** with the author's own text, and the hook's JSON pointer |
| `mutate` | `beforeCreate`, `beforeUpdate` | rewrites fields of the row about to be written | nothing, except the stored row and the emitted event |

`mutate` is absent from `beforeDelete` because there is no row about to be written — the row is about to
stop existing, so a patch has nowhere to land. It is refused at **apply**, not dropped at run time; the
invariant that follows from that is at the end of this section.

Everything is compiled at **apply** by `BeforeHookCompiler` into the same `PolicyCatalog` the rules
and the after-hooks live on (`EntityBeforeHooks`), with a JSON Pointer an author can act on. That is
not tidiness: a before-hook runs inside the same write the rules are judging, so a hook compiled
against a different schema revision than the `WITH CHECK` predicate over the same candidate row would
be two views of one write disagreeing about what the row's fields are. There is no author to report a
mistake to from inside a transaction, so `BeforeHookRunner` parses, resolves and compiles **nothing**
— it evaluates and nothing else.

### The four write-path call sites, and the two that deliberately have none

The runner is called from four places in `EfAlvoData`, every one of them **inside** the transaction
the write commits in:

| Call site | Hook point | Why there |
|---|---|---|
| `CreatedAsync` | `beforeCreate` | the ordinary create, after `BeginTransactionAsync` |
| `RecordedCreateAsync` | `beforeCreate` | the idempotent create's **writing** branch |
| `WriteAsync` | `beforeUpdate` | update's in-transaction body, where both row images exist |
| `EraseAsync` | `beforeDelete` | delete's in-transaction body, over the row-locked pre-image |

And from two places it must **not** be called:

| Not a call site | Why not |
|---|---|
| `CreatedOrReplayedAsync` | it opens the transaction and *then* branches between a replay and a fresh write. A call there would run on the replay too — double-applying a `mutate` whose value the first attempt already stored, and letting a `reject` refuse a retry of a create the caller was already told succeeded. The hook belongs on the branch that writes a row, which is `RecordedCreateAsync` |
| `ReplayableCreateAsync` | it is the retry loop around that method, not a write path of its own |

**A hook may not run where the candidate is built.** `AuthorizedCandidate` runs *before*
`BeginTransactionAsync`, so a hook placed next to it would judge outside the transaction its verdict is
about — and every one of the four things that verdict has to be atomic with is inside: the row-locked
pre-image its `old.` and `changed(...)` are answered from, the `WITH CHECK` re-test of the *patched*
candidate, the row write itself, and the outbox insert. Judged outside, the pre-image is not locked, so
another writer may move the row between the hook's judgement and the write; and a `mutate`'s patch is no
longer part of the same unit as the row it patches. Inside the transaction a refusal is a rolled-back
write with **no row and no outbox event**, which is what #114's DoD asks of it.

**Update and delete hook in the private bodies (`WriteAsync`, `EraseAsync`), not in the public
`UpdateAsync`/`DeleteAsync`, and that is where the pre-image is.** A hook's `old.` references and its
`changed(...)` calls are answered from the **in-transaction, row-locked** pre-image that those bodies
read under `USING`. A pre-image read before the transaction, or on another connection, could be
overwritten between the hook's judgement and the write — the hook would then have judged a row that no
longer exists, which is exactly the merge-then-check discipline `data-path.md` states for the verdict
itself.

**What re-runs over a patch, and what deliberately does not.** After a `mutate`,
`EnsureWriteAllowed` runs again over the **patched** post-image: `WITH CHECK` and the tenant scope
judge the row that will actually be stored, or a hook writing `owner_id` from a caller-controlled
field would place a row the `create` rule refuses — a caller-reachable authorization bypass.
`WritePayloadGuard` is **not** re-run, because it judges *a caller's* keys (framework-managed columns,
fields a policy froze as `readOnly`) and a hook is not the caller; re-running it would refuse a hook
legitimately setting a field callers may not write, which is one of the two things a before-hook
exists for. This asymmetry is the ruling deviation 75 asked PR5b to make explicitly.

**Ordering inside the pipeline.** Hooks fire in declaration order and each sees the candidate as the
hooks before it left it, so a later hook's condition legitimately reads an earlier hook's patch.
Within *one* hook, every mutation is evaluated against the candidate as that hook received it —
because a `mutate` is a JSON object and neither JSON nor .NET promises member enumeration order, so
letting one mutation see another's value would make the stored row depend on an order nobody
specified.

**A `beforeDelete` that produces a patch is an invariant violation, not an authoring mistake.** A
`mutate` on that hook point is refused when the descriptor is applied, so a non-empty patch reaching
`EraseAsync` means the compiler and the write path disagree; it throws rather than dropping the patch
silently, because silently dropping it is how a refusal comes to be untrue with nothing failing.

### Network isolation is structural, not conventional

The frozen schema states the ban in the slot's own description — *"Before-actions run in-transaction:
reject or mutate only. No network, no external calls."* — because a hook holds a write transaction open
while it runs, so one HTTP call inside one is a row lock held for a stranger's timeout. It is enforced
in two places, and neither is a naming convention:

- **The port's signature.** `IBeforeHookRunner.Run` returns no `Task` and takes no
  `CancellationToken`, so an implementation cannot `await` anything; the shortest path to a network
  call is closed at the contract, and taking it would mean *blocking* a transaction-holding thread.
- **The implementation's dependency list.** A signature cannot express "and nothing you hold may do it
  either", so `BeforeHookRunner`'s own dependencies are asserted by an **architecture fact**: nothing
  reachable from its constructor exposes `HttpClient`, `IHttpClientFactory`, a socket or a mail port.
  Injecting `IHttpClientFactory` into it turns that fact red, which is what makes it a fact rather than
  a wish — the `alvo-security-core-review` checklist requires a network call from a before-hook to be
  *inexpressible*, not discouraged.

A hook that genuinely needs a network call belongs one rung over: an after-hook, which runs after the
commit and therefore holds no lock.

### What bounds a hook's execution time — the grammar, not a timeout

There is no wall-clock budget and no cancellation token to carry one, and that is a decision rather
than an omission. **The bound is structural.** A hook is a fixed number of compiled CEL expressions —
the count fixed by the descriptor at apply, never by the request — and the profiles they compile in
(`Condition` for the gate, `Mutate` for the values) have no loop, no comprehension macro, no
recursion, no user-defined function and no I/O. `Mutate`'s entire function allow-list is an ASCII fold
over one string and a read of an instant the caller already bound. Each expression's tree is walked
once and its node count is bounded by its source length, which the frozen schema caps at 2000
characters.

So the work is **O(descriptor), not O(caller input)**: no request can make a hook slower, and a
wall-clock budget could only fire on a machine that had already stopped serving. A timeout would buy a
clock read per hook plus a second failure mode *inside a transaction*, to guard against an overrun the
grammar cannot express. Recorded as deviation 81, because the addendum's DoD names a "budget-overrun
rollback" and a reader is owed the reason there is no budget to overrun. The claim is only as good as
the grammar it rests on: **the PR that admits a loop, a comprehension or a call that can block into a
before-hook profile owes a budget with it.**

## After-hooks

`afterCreate`, `afterUpdate` and `afterDelete` are compiled into the **`PolicyCatalog`** — the same
holder the rules live on, primed by the same pass. That is not tidiness: `EntitySchema`/`SchemaModel`
carry no hooks, and a fourth independently primed holder would mean a hook compiled against a different
schema revision than the rules judging the same write.
`The_hook_compiler_is_reached_from_the_policy_catalog_builder_and_nowhere_else` is the structural fact.

Compilation refuses everything at **apply** time, with a JSON Pointer an author can act on
(`/entities/deals/hooks/afterUpdate/0/action/payload`, keeping the repository's leading slash so there is
one JSON-Pointer spelling in the error list): an unknown field in a condition, an unresolvable template
placeholder, a raw JSONata expression, a `bodyFile`, and each of the three unimplemented action types.
Everything is resolved there — including each `webhook` action's endpoint URL — so **nothing is resolved
at delivery**. There is no primed descriptor at run time, only the primed `PolicyCatalog`, and a
delivery-time lookup would have been the second holder R11 forbids.

### The condition is part of the subscription

`EventSubscriptions.Matching` reads the entity and operation out of the event's `type`, finds the
entity's compiled hooks for that operation, and evaluates each hook's `CelProfile.Condition` expression
**there** — before any execution entry exists. §3.3 records the consequence of getting this wrong as a
documented Directus defect: thousands of execution-log rows describing runs that aborted on their first
condition, which makes debugging impossible. Alvo has the advantage by construction, because the
predicate was compiled when the descriptor was applied. A filtered event costs **one counter increment
and no log entry**.

**Details of that Directus citation, as the PR5 risk register's own corrections record them** — repeated
here so a later reader does not re-cite the uncorrected version, and attributed rather than re-verified:
the setting is `flow.accountability`, a **top-level column on `directus_flows`**, not
`flow.options.accountability`; `FLOWS_EXEC_ALLOWED_MODULES` no longer exists and never concerned logging;
and the user complaint is **one auto-closed discussion reply** — an authentic report, and *not* an
acknowledged Directus bug. Alvo's design does not rest on the citation being exact; it rests on the
subscription-step argument above, which stands on its own.

**A type this cannot parse selects nothing**, which is the fail-closed direction: an unrecognised type is
a queue entry from a build that spoke a different grammar, and running every hook on it would be strictly
worse than running none. The emitting vocabulary (`OutboxEventFactory.Suffix`) is `internal` to the EF
driver and unreachable from the core, so the pairing is held **behaviourally**, by the end-to-end criteria
that drive a real write through a real dispatcher, rather than by a shared constant.

**A condition that throws selects nothing** and does not take the batch down — a broken predicate is a
fail-closed refusal, exactly as an unprimed catalog denies every operation. It is recorded at Debug
(`ConditionRefusedTheHook`), because the loud version is per event, which is the noise the whole criterion
is about.

### The action set: `webhook` and `email`, and three refused by name

| Action | PR5a | Notes |
|---|---|---|
| `webhook` | **runs** | one POST to the endpoint the descriptor declared, resolved at compile time |
| `email` | **runs** | `IEmailSender`, with **only** a console dev provider — no SMTP, no mail service in compose; `email.data` is **refused at apply** (see below) |
| `entity.update` | refused at apply | PR5b's |
| `function` | refused at apply | frozen into `$defs/action`; out of scope for all of PR5 |
| `http.call` | refused at apply | same |

**`email.data` was a dead slot, and a dead slot is worse than an unimplemented one.** `CompileEmail`
parsed it, resolved every placeholder against the entity's schema and stored it under `ActionSlot.Data`; the
executor renders only `to`, `subject` and `body`, and no `data.*` placeholder root exists for either to reach
it with. So an author following the schema's own doc comment got a clean apply and a silently discarded
value — the *exact* failure mode this document cites as the reason a partial JSONata evaluator is refused,
except that the implementation rate was **0 %** and it was not refused. It is now a named
`UnhonouredFeatures` entry, refused whatever it carries (template spelling included, which is the theory's
second case). Adding a `data.*` root instead would be new placeholder surface and belongs to the PR that
reads it.

`email` is not optional. `templates.subject`/`body` and `email.to` are the only slots that exercise the
template engine's plain-string sugar, and deviation 64's consequence (`{{@user.email}}` is refused, never
rendered to `To: ""`) is unreachable without them. `IEmailSender` is registered with `TryAddSingleton`,
so the console provider is a **default rather than a decision** — a host with a real provider registers
its own and takes mail over — which is why the console provider's own log line has to name itself a
development provider.

**Every delivery failure is retried and none is classified.** A 500, a 404, a 503, a connection refused,
a DNS failure and a timeout all throw and all get identical treatment, because nothing at delivery time
can tell a permanently wrong endpoint from one whose deployment is thirty seconds from finishing, and a
per-status "permanent" verdict needs somewhere to route the abandoned event — which 7.1 owns. **The
ceiling lives in exactly one place:** the `maxAttempts` the dispatcher passes to `ClaimAsync`, whose
subquery filters `attempts < @max_attempts`. `WebhookDelivery` adds no retry of its own; an inner loop
would be a second invisible multiplier and would hold a claimed entry past its lease while sleeping.

**One conversion is deliberate.** `HttpClient` reports *its own* timeout as an
`OperationCanceledException` — the same type the host's shutdown raises — so `WebhookDelivery` turns a
timeout into a `TimeoutException` when the caller's token is *not* cancelled. Leaving the two
indistinguishable is how a slow receiver reads as a shutdown and silently ends the pump; both directions
are pinned by a fact.

**The delivery is unsigned and unprojected, and both absences are named at apply.** `secretRef` is never
read and no Standard Webhooks `webhook-id`/`webhook-timestamp`/`webhook-signature` header is sent, so a
receiver **cannot yet verify the sender** — signing belongs to the webhook-management work (7.1). The
body is the whole envelope or the whole rendered template, with no per-endpoint field selection
(**#152**). The descriptor's `webhooks` warning names both, because an unsigned delivery an author
believes is signed is a security absence, which is exactly the misattribution that table exists for.

### The unmasked record, and why that disclosure is accepted

`data.record` and `data.old_record` are the complete images with **no `hidden` mask applied**. D7 is two
separable decisions that were written as one, and only the second of them is retirable — so they are split
here, because a **#152** or 7.1 reader has to be able to tell which half their work closes.

**The unmasked *envelope* is permanent, for two reasons that no later PR removes.**

1. An after-hook condition reading `old.commission_note` or `changed(commission_note)` must see every
   field, and `hidden` is a per-caller **read** mask rather than a data classification.
2. A masked post-image would be worse than incomplete: every masked field would read as moved on every
   update, so `data.changed` would report changes that never happened.

**The unmasked *delivery body* is what #152 closes**, and it rests on one reason only: the consequence is
bounded by who declares what. An after-hook `webhook` delivers hidden fields to an endpoint declared **in
the same descriptor by the same author** as the `hidden` rule. Per-endpoint field projection retires
exactly this half and leaves the two above untouched.

`webhooks.endpoints` is **never caller-supplied** — that sentence is about the webhook endpoint and about
nothing else in the action set. It is *not* true of `email.to`: that slot takes `{{...}}` placeholders and
`AlvoTemplate`'s own remarks suggest `{{new.owner_email}}`, so **anyone who can write a record chooses the
recipient of framework-sent mail carrying that record's data**. It is inert today only because
`ConsoleEmailSender` sends nothing, which is exactly why it is written down here rather than met by the SMTP
PR: the person adding a real sender inherits a caller-controlled recipient and needs to know it. Two
adjacent consequences of the same fact, recorded and not fixed: `To: ""` is still reachable through a
template that renders empty or a NULL column, and `AlvoMailMessage.To` is unvalidated rendered row text
reaching a host's SMTP implementation, which is CRLF header injection in any sender that does not validate
it — named in `IEmailSender`'s own remarks while the port is new.

It is pinned by a **named** fact rather than a paragraph
(`A_webhook_receives_the_unmasked_record_and_that_is_documented`, plus
`An_events_record_carries_a_hidden_field_unmasked_and_that_is_the_documented_disclosure` over a real
engine). Per-endpoint field projection is **#152**.

**The action log stops at the join key, and that is D7 taken one step further.** `ActionExecuted` carries
the hook's JSON pointer, the action `type`, the event id and the event type — and **not** the rendered
body, the recipient, the subject or the endpoint URL. A log line has no author who declared it: logging
the rendered value would take a `hidden` field out of the one place the design accepted it going and put
it into whatever ships logs, which nobody declared. The payload is stored once, in the `alvo_outbox` row —
**without any retention today** (see below). `ConsoleEmailSender` is the one deliberate exception and not an
exception to the rule — for a console provider the log *is* the mailbox.

**The *failure* path used to break that rule, and it was the worse half.** `WebhookDelivery`'s
`TimeoutException` interpolated the whole `endpoint.Url` — scheme, host, path and query — and the dispatcher
logs a failed attempt at **Warning with the exception attached**. Since `secretRef` is never read and no
HMAC is sent, a secret embedded in the URL is the only authentication an author has, and that is how a
Slack, Teams, Zapier or Make endpoint actually works: `https://hooks.slack.com/services/T…/B…/XXXX` *is* the
bearer token. One slow receiver put it in the log pipeline, whose read set is far wider than "whoever
declared the endpoint" — the premise the paragraph above rests on. So the compiled action now carries a
`WebhookTarget` (**name** plus validated `Uri`), and every message this subsystem writes names the
**endpoint's name**, which is the author's own vocabulary and the key they act on.
`No_log_line_carries_a_webhook_url_that_could_be_a_secret` asserts the absence *after* the same run has
proved the secret-shaped segment really was on the wire, over the message **and** the attached exception —
because that is what a pipeline ships. What is still disclosed, and accepted: `HttpRequestException` carries
framework-supplied `host:port` on a DNS or connection failure. The host is not the secret; the path and the
query are.

**The endpoint's URL is validated at apply, and cleartext is refused.** `schema/project.schema.json`'s
`"format": "uri"` is an annotation and asserts nothing, so a relative or malformed URL used to become a
`UriFormatException` *per delivery attempt* — retried to the ceiling and abandoned, read by an author as an
endpoint outage rather than as the typo it is, and it is the endpoint mistake an author is most likely to
make. `AfterHookCompiler` now parses the URL at apply, refuses a non-absolute one, and refuses a non-`https`
scheme **except for a loopback host**. `http` is refused rather than warned because the body is the complete
unmasked image, the delivery is unsigned, and the slot's own description says *HTTPS target* — cleartext is
the one combination where "bounded by who declares what" fails outright, since an on-path observer is
nobody's author; and a warning would describe a tolerance the apply path does not have, which is
`UnhonouredFeatures`' own recorded argument for every entry being an error. The loopback carve-out is
deliberate and narrow: there is no network to observe, and `http://127.0.0.1:port/hook` is the shape a local
receiver — including this repository's own loopback delivery suite — uses.

**`alvo_outbox` has no retention, and that is a disclosure in its own right.** Nothing deletes a row
(`grep "DELETE FROM" src/` returns nothing), and the payload carries the complete **unmasked** post- *and*
pre-image of every create, update and delete, for every entity and every tenant. So one
`SELECT payload FROM alvo_outbox` — or a nightly backup, or a read-replica credential — returns the whole
edit history of a `hidden` field. D7's ground covers **one declared endpoint**; it does not cover an
unbounded permanent store, for the same reason it does not cover a log line: nobody declared the DBA. The
omission is also asymmetric — this branch names exactly this growth for `alvo_idempotency` (**#115**) and
exactly this absence for the execution log. Filed as **#154**: a `dispatched_at`-based prune, carrying the
disclosure argument rather than only the disk-space one.

## Templates, and why JSONata is refused rather than approximated

`$defs/jsonata` is a frozen first-class slot on four action types (`webhook.payload`, `email.data`,
`function.input`, `http.call.payload`), typed `string`, whose own description says `{{...}}` templates are
syntactic sugar. There is no mature .NET JSONata implementation. PR5a honours **only** the template sugar
and **refuses a raw expression at apply** by a named `UnhonouredFeatures` entry (**#149**).

A partial evaluator is not the cheaper option, it is a defect: `CLAUDE.md` states that *inventing a
variant of a standard is a defect, not a shortcut*, and this one's failure mode is the expensive kind —
an agent writes JSONata it knows from training data, Alvo accepts the 80 % it implements and **silently
produces a different payload** for the rest (`$merge`, `$map`, `^(…)` ordering, predicate contexts, `$$`
root scope). A webhook delivered with a wrong body is indistinguishable from a consumer bug. The refusal
is an **error**, not a warning, on `UnhonouredFeatures`' own line: the action still runs, so nothing looks
broken, and the body is not the one the author declared.

### The discriminator, and why both of its clauses are earned

> A string in a `$defs/jsonata`-typed slot is a **template** iff it consists only of literal text
> containing no bare `{` or `}` and one or more well-formed, non-nested `{{ … }}` placeholders — i.e. it
> matches `^(?:[^{}]|\{\{[^{}]+\}\})*$` **and** contains at least one placeholder. Anything else is raw
> JSONata and is refused by name.

Both plausible naive rules fail open, in opposite directions, and the shipped example proves each:

- *"contains `{{`"* would classify `crm.alvo.json`'s `"{\"companyIds\": records.id}"` as literal text and
  deliver the JSONata source as the webhook body. The **no-bare-brace** clause catches it, and
  `"$merge([new, {\"source\": \"alvo\"}])"` with it.
- *"no bare brace"* alone would classify a brace-free expression such as `"records.id"` as a valid
  placeholder-free template and deliver the literal string `records.id`. The
  **at-least-one-placeholder** clause catches that. There is no reason to declare a *transform* that is a
  constant.

**The no-bare-brace clause is load-bearing for *injection*, not only for classification — do not lose it in
#149.** A `payload` template renders row text straight into author-written text and nothing escapes it, so
the only thing standing between a row value and a restructured JSON body is that clause: it refuses any
payload containing `{` outside a placeholder, so a payload template **cannot be a JSON object** and a row
value cannot forge a sibling member. What remains reachable, and is named rather than fixed:

- `[` and `]` are not braces, so `["{{new.a}}", "{{new.b}}"]` *is* a legal template, and a value containing
  `", "` forges **array elements**.
- A bare or quoted string payload posted under `application/json` becomes **invalid** JSON, not restructured
  JSON, when a value carries a `"`, a `\` or a newline. `WebhookDelivery`'s own remarks already name the
  bare-string case; the quoted case is the same defect one character further on.

Neither is a `hidden`-field disclosure — the receiver is the declared endpoint either way — so both are
malformed-body bugs rather than authorization ones, which is why they are recorded here and left to the PR
that gives the payload slot a real evaluator. **A #149 implementation that evaluates JSONata must produce
JSON by construction (serialize a value), never by interpolating rendered text into author-written text.**
If it renders text at all, the no-bare-brace clause has to survive with it.

**The asymmetry with the plain-string sugar slots is deliberate** and comes from the schema's own typing.
In `email.to`, `entity.update.recordId`, `templates.subject`/`body` and string values inside
`entity.update.payload`, a placeholder-free string *is* a legitimate literal — a hard-coded address — so
those slots accept one and go straight to `AlvoTemplate.Parse` without asking the classifier anything.
That is also why `AlvoTemplate.TryParse` exists: a malformed placeholder is unreachable in a
`$defs/jsonata` slot (the classifier rejects it first) but reachable in a sugar slot, and without it
`email.to: "{{new.owner_email}"` was an unhandled `ArgumentException` at apply — an authoring mistake
reported as a framework crash.

### A placeholder is validated at apply, and never rendered empty

Templates are validated against the **schema** at apply time, not against a payload at delivery time — the
same property #20's DoD demands of rules (*a rule referencing a nonexistent column fails at save, not at
request time*). A placeholder naming a field the entity does not declare is a structured apply-time error
with a fix suggestion.

An unresolvable placeholder is **refused, never rendered to `""`**. Rendering `{{@user.email}}` to empty
yields `To: ""` — a mail failure that looks like a broken SMTP server, which is the exact misattribution
`UnhonouredSubsystems` exists to prevent.

`TemplatePlaceholder.Roots` is `new`, `old`, `event`, `@user`, and it is the one authority every refusal
message iterates, so a root added later cannot be missing from the message that lists them.

**Two names the addendum's own table promises and this build refuses — see *What PR5b and F7 inherit*.**

### The JSONata ban, and the test PR5a can honestly write

`alvo-specifikacia.md:300` requires the evaluator's in-transaction ban to be proven *by a test*, and
`PLAN.md` makes it an invariant. In PR5a the invariant is **trivially preserved, because JSONata does not
run at all** — so a test named "JSONata does not run in-transaction" would be vacuously green and would
read, forever after, as though the ban were enforced. PR5a's fact is therefore an **absence** test, named
as one: no JSONata evaluator exists on any path, and every raw expression is refused by name. Its
allow-list of files that may mention JSONata *in code* is `JsonataSlot.cs`, `UnhonouredFeatures.cs` and
`AfterHookCompiler.cs`, with comments stripped — a refusal's whole job is to name the feature it refuses.

**The real ban test is owed by the PR that introduces an evaluator, and it must be architectural** —
nothing on the in-transaction path can reach the evaluator — not behavioural, because a behavioural test
only samples the paths someone thought of. Recorded here so the obligation does not leave with this
document.

## The dispatcher

`OutboxDispatcher` is one `BackgroundService`, registered through `TryAddEnumerable` so a host that
called `AddAlvo` twice still drains the queue once.

**It gates on `AlvoBootState`, and on .NET 10 that is the only way to express the gate.**
`BackgroundService.ExecuteAsync` now runs *entirely* off the startup thread — "no part of it blocks other
services from starting" — so *"not before the schema is primed"* **cannot be expressed by registration
order**, and `await Task.Yield()` as a first line is dead code. None of the documented workarounds
substitutes, because the thing being waited for lives in a different service.

**What the gate is actually worth**, since the standalone host's own ordering nearly settles it: an
unprimed `PolicyCatalog` knows no entity, so every event would match no hook, count as `filtered`, and be
**retired** — silent, permanent loss that no retry recovers, because a filtered event is deliberately not
retried. So the gate covers the boot that *refused*, an embedded host that primes on its own schedule, and
any future change that moves priming out of the startup phase. One layer deeper, the fail-closed direction
is pinned too: reaching a batch with no primed catalog **throws** and the entry is **released** rather than
marked dispatched, with the refusal naming the catalog.

**Nothing escapes `ExecuteAsync`.** `HostOptions.BackgroundServiceExceptionBehavior` defaults to
`StopHost`, and from .NET 11 `RunAsync`/`StopAsync` also throw and the process exits non-zero — with the
documented recommended action being "do nothing", because a failing app should fail. One poison event must
not be that failure, so containment lives **inside** the loop, per entry, and the outermost catch is a
backstop rather than the mechanism.

**The loop observes its token, because the host blocks in `StopAsync`** for up to a 30 s
`ShutdownTimeout`. The idle wait is `Task.Delay(interval, TimeProvider, token)` rather than a sleep, so a
shutdown ends the pump in milliseconds instead of turning a clean stop into a half-minute hang. An entry
claimed when the shutdown arrives is left claimed: its **lease** is what recovers it, which is the same
mechanism that covers a process that died.

**There is no dispatcher-wide caller, and the claim that there was one was wrong in both directions.** A
hook condition's `@user.id` is resolved **per event, from the envelope's own `authid`**
(`EventSubscriptions.CallerOf`), because the actor an author means is the credential that made the change
and not whoever happens to be draining the queue. The two references an envelope cannot answer are refused
when the hook is compiled. What a shared `AlvoContext.System(tenant: null)` actually did:

| Reference | Resolved to | Consequence |
|---|---|---|
| `@user.id` | the framework's reserved id | `new.owner_id != @user.id` **never matched**; `!(new.owner_id == @user.id)` **always** did |
| `@user.roles` | the dispatcher's own `{ admin }` | `'admin' in @user.roles` was **true for every event**, whoever wrote the row |
| `@tenant.id` | `null` | every comparison **`false`** — including `!=` — so a negation reads as "every tenant" |

The `@tenant.id` line is the one this document previously described as *"silent but denying, never matching
'any tenant'"*. **That is true of the positive form only.** `Compare` returns `false` when either operand is
null for *every* operator, and `Not` negates the collapsed boolean — so
`changed(status) && !(new.tenant_id == @tenant.id)`, written to mean "every tenant except ours", was
**`true`** and delivered every tenant's unmasked row to the endpoint. Fail-**open**, not fail-closed.
(The literal `@tenant.id == 'internal'` is unreachable for an unrelated reason: `@tenant.id` is typed
`Uuid`, so the CEL type checker refuses the string comparison first. The reachable shape is a row's own
tenant column, which is how a rule writes it anyway.)

So `AfterHookCompiler` now refuses `@tenant.id` and `@user.roles` in an after-hook condition **by name**,
in the same words `TemplatePlaceholder` refuses them in a template — one authority,
`EnvelopeProvenance`. The rule is symmetric and states itself: **resolve what the envelope can answer,
refuse what it cannot.** The refusal lives at the *after-hook* compile site and **not** in
`CelTypeChecker`'s profile table, because the reason belongs to the envelope and not to the profile: PR5b's
before-hooks compile in the same `Condition` profile and run inside the request, where both names have a
real caller to resolve against.

One consequence of resolving `@user.id`: an **anonymous** write carries no `authid` at all, and the reserved
all-zero `UserId` means "no identity" rather than a caller who owns the all-zero rows. So a hook whose
condition reads `@user.id` is **not selected** for such an event, gated by the same `RequiredContext` shape
the policy engine applies to a rule, and recorded at Debug (`ConditionHasNoActorToRead`) — refuse upstream
rather than fold an absent operand into a verdict.

### The execution log is logs plus metrics, not a table

The criterion is *"a filtered-out event produces no execution log, only a counter"*. A durable, queryable
execution log with retention and a redelivery UI is 7.1. PR5a ships **one source-generated log entry per
executed action** (`ActionExecuted`, written by the executor and by nothing else, after the await, so a
failed action writes none) plus one `Meter`:

| Instrument | Meaning |
|---|---|
| `alvo.events.dispatched` | one per event that matched at least one hook and was retired |
| `alvo.events.filtered` | one per event that matched nothing — **per event, never per hook** |
| `alvo.events.failed` | one per failed **attempt** |

All three are on the meter `MMLib.Alvo.Events`. Recorded as a deviation because a reader could otherwise
expect a table.

### The attempt ceiling is the DLQ stand-in, and "abandoned" is observable

`MaxAttempts` (default 10) is the only bound on an event that can never be delivered. Past it the entry
simply stops being claimed — **not deleted, not moved** — so it sits in `alvo_outbox` with
`dispatched_at IS NULL`, countable and inspectable; `alvo.events.failed` has one increment per attempt;
and `PoisonEvent` is a loud Error line naming the event id and type. A real dead-letter queue with a
redelivery UI is **7.1**.

**A count is not a bound unless there is a backoff, and there was not one.** `Task.Delay(PollInterval)`
runs **only** when a claim came back empty, and `ReleaseSql` set `claimed_at = NULL`, so a released entry was
claimable on the very next iteration: a receiver restarting for thirty seconds burned all ten attempts in
milliseconds and the event was abandoned **permanently** — no DLQ, no redelivery UI, recoverable only by
hand-editing `alvo_outbox` — while hitting the receiver with the whole batch at line rate on the way. That
directly defeats the delivery's own stated reason for not classifying failures: it declines to tell a
permanently wrong endpoint from one *thirty seconds from finishing*, so it has to survive the thirty seconds.

So `IOutboxStore.ReleaseAsync` takes a **`retryAfter`**, and the queue now distinguishes its two waiting
states with the column it already had:

| State | `claimed_by` | `claimed_at` means | Claimable when |
|---|---|---|---|
| never claimed | `NULL` | — (`NULL`) | always |
| **released** | `NULL` | retry **not before** | `claimed_at <= @now` |
| **held** | set | when the claim *started* | `claimed_at < @stale_before` (the lease) |

No new column and no new statement — the claim's predicate grows one branch, repeated in the outer `WHERE`
for Q4's reason and shared as one constant so the two copies cannot diverge. Comparing a released row
against the lease would make every failed delivery wait out a five-minute crash-recovery window; comparing a
held row against `@now` would re-claim an entry still in flight, which is a duplicate delivery per tick.

The dispatcher asks for `attempts × PollInterval`: the poll interval is already the queue's own tick, so
linear growth needs no new option, and at the shipped defaults ten attempts span **at least 45 seconds**
(1+2+…+9) — asserted as a fact over the defaults rather than left in a comment, because the number *is* the
justification for not classifying a failure. It is deliberately not exponential: nothing here classifies a
failure, so a large multiplier would push a transient 503 out by hours for the same reason it would push out
a permanently wrong endpoint. Per-status scheduling belongs with the queue that can absorb it (7.1).

One consequence for the chaos criterion, stated rather than discovered: its clock is a fake one that
previously moved only at an abandoned claim, so the backoff stranded 186 of 10 000 events on the first run.
`OutboxChaosWorld` now advances it one poll interval per claimed batch — the conservative stand-in for what a
real pump spends, since a hundred real deliveries cannot cost less than one tick.

`ClaimLease` (default 5 minutes) is refused at startup unless it **outlasts** `PollInterval`: a lease
shorter than the interval re-claims an entry that is still in flight on the very next tick, which is a
duplicate delivery per tick rather than at-least-once delivery. Every options refusal names the
configuration key, its `Alvo__…` environment spelling, and a value that would have worked;
`ValidateDataAnnotations()` is deliberately **not** used, because a `[Range]` message names the property
rather than the key and would add a second, worse message to every refusal.

## Publishing a custom application event

A host's own event, on the same durable queue a data event travels on. Added by **PR5b-2**, and the
namespace guard is the reason it exists rather than a check bolted onto it.

```
IAlvoEvents.PublishAsync(type, subject, data, context)
        │
        ├─ AlvoEventName.EnsureCustom(type)   ← refuses first, before the clock is read
        │     1. blank
        │     2. first segment in AlvoEventName.ReservedNamespaces  (entity | auth | storage)
        │     3. not two or more lower-case dot-separated segments
        │
        ├─ AlvoEventId.Create(now) · AlvoEvent.DefaultSource · AlvoEventProvenance.*
        ├─ AlvoCustomEvent.Create             ← runs the SAME guard again; the only door to the queue
        └─ IOutboxStore.AppendAsync           ← one autocommit INSERT, the statement OutboxTable emits
```

**The guard is the whole point, and it is stated against a real reader rather than against strings.** Without
it a host could publish `entity.orders.updated`, and every after-hook and descriptor rule subscribing to that
name would fire on an event carrying a `partitionkey`, an `authid` and a `time` for a record nobody wrote.
"Indistinguishable" has exactly one arbiter — `EventSubscriptions`' type reader, which compares segment 0
against `"entity"` with `StringComparison.Ordinal` — so the reserved set is ordinal too, and
`EventSubscriptionsTests.An_event_a_host_published_selects_no_after_hook` drives a **real** published envelope
through that reader. The name it publishes is `crm.deals.updated`: three segments, an entity the catalog
really has hooks on, a suffix the reader really maps — everything a data event has except the namespace,
which is the nearest legal forgery a host can attempt. A two-segment name would have been turned away by the
segment-count check before the prefix was compared, which was measured, not assumed (the mutation that
inverts the prefix comparison survived the two-segment version).

**The refusal is an exception, not a returned result**, on `IBeforeHookRunner.Run`'s precedent (deviation 82):
a refusal a caller can forget to check is a guard that is not one.

**The guard is on the type the port accepts, and that is a correction rather than the first design.** The
first draft put it only in `PublishAsync` and let `AppendAsync` take a bare `AlvoEvent` — and `IOutboxStore`
is public and DI-registered while `AlvoEvent` is a public record with public initializers, so a host could
resolve the port and append `entity.orders.updated` with `authtype: system` and a payload of its choosing.
The PR that added the guard had added the primitive that bypassed it. `AppendAsync` now takes
**`AlvoCustomEvent`**, whose only door — `AlvoCustomEvent.Create` — runs the guard; a check inside a driver
would have to be repeated by every other driver and be absent from the one that forgot. This is
`IOutboxStore`'s own rule for the UUIDv7 id, *"the wrong implementation is unavailable rather than merely
discouraged"*, applied to the caller. An intermediate draft made the constructor `internal` instead and was
wrong twice over: it rested on a forgeable `InternalsVisibleTo`, and it locked out `OutboxStoreContractTests`,
the **public** suite an external driver author inherits — which is what surfaced that the invariant is *"none
carries a reserved name"*, not *"only the framework constructs one"*. `AlvoEventName` moved to `Abstractions`
with it, because the reserved namespaces are wire contract; `EventPattern` kept the wildcard half, which is
descriptor contract.

**`AppendAsync` has two facts in the contract suite both engines inherit** — appended-then-claimable, and
appended-then-retired — because it was, briefly, product code no engine ever ran. Deleting its INSERT turns
`SqliteOutboxStoreTests` red.

**Provenance is one authority across two assemblies now.** The derivation of `authtype`, `authid` and
`correlationid` was private to `OutboxEventFactory` in the EF driver; a second emit path in the core would
have had to restate it, and a second copy of "which caller is the system caller" is how a system-made change
comes to be reported as an ordinary caller's on one path and not the other. It moved to the public
`AlvoEventProvenance` in `Abstractions`, which both paths call. Trusting an event's provenance is the whole
premise of refusing a host the `entity.` namespace, so the two must not be able to disagree.

### Three things a custom event is not, each stated where an author would otherwise assume it

- **It is not subscribable.** `$defs/eventPattern` is **frozen** to
  `^(entity|auth|storage)\.([a-z][a-z0-9_]*|\*)\.([a-z]+|\*)(\.batch)?$`, so `order.approved` is
  unrepresentable as a subscription — and the guard forbids the three namespaces that *are* representable. So
  **every** name this API accepts matches zero descriptor rules and zero after-hooks. What ships is a durable,
  ordered, inspectable outbox row and nothing downstream of it: the dispatcher claims it, matches nothing,
  counts `alvo.events.filtered` and marks it dispatched. Deliberate — the fix is a **designed namespace**,
  taken once, not a prefix added under one PR's schedule — and stated on `IAlvoEvents` itself, where an author
  reads it before calling. **Deviation 87.**
- **It is not transactional with anything.** The spec's *"event sa publikuje v tej istej transakcii ako dátová
  zmena"* is a guarantee about a **data** change; a custom event has none to be atomic with, and
  `AppendAsync` is one autocommit statement. A host needing its own write and its own event to commit
  together does not get that here. **Deviation 88.**
- **It is not ordered per entity key.** The key is `{type}:{subject}`, so per-subject ordering is the only
  ordering a custom event can be given, under the same one-dispatcher, one-millisecond conditions as
  everything else. **The type is in the key on purpose:** `partition_key` exists for F7's partitioned claim
  (**#150**) to index, so a host publishing `subject: "deals:<guid>"` would order itself into a real entity's
  partition the day that claim reads the column — the same "meaning silently widens when the feature lands"
  hazard the wildcard refusal exists for, closed by shape instead of by a warning. The disjointness is
  provable: an entity name is `^[a-z][a-z0-9_]{0,62}$` and carries no dot, and a custom type must contain
  at least one.

**Why the guarantee ships before the feature it guards is useful.** The refusal costs nothing now and cannot
be added later without breaking whichever host is already minting `entity.orders.updated` by then. A guard
added after the fact is a breaking change to that host; added now, it is a rule nobody ever got to break.

## The wildcard ruling

`alvo-specifikacia.md:141` makes `entity.orders.*` a **hard** guarantee. `baas-analyza.md:657` makes tenant
isolation of rules a watch-out. This document set the two against each other and required PR5b to resolve
them one way or the other: implement the matcher **with every subscription scoped to the envelope's tenant
and a named adversarial cross-tenant fact**, or refuse `*` at apply until that exists.

**The first branch is unavailable, and that is measured rather than argued.** `AlvoEvent` carries `authid`
and **no tenant attribute** — `AlvoContext` has a `Tenant`, so the tenant is known at *emit* and dropped at
the envelope boundary, which makes it unknowable at *delivery*, the only place a subscription is evaluated.
So a matcher shipped today could be scoped by nothing, and the adversarial fact the ruling demands would have
no tenant on either side of its comparison: it would assert nothing while looking like coverage. Giving the
envelope a tenant is a public-API and wire-format change with a compatibility question for the outbox
payloads this build already wrote — **#153** owns exactly that.

**So `*` is refused at apply**, in both slots the schema types as `$defs/eventPattern`
(`automation.*.trigger.event`, `functions.*.trigger.event`), on both passes: the typed pass in
`DescriptorToSchemaMapper` that an embedded host reaches through `FromDescriptor`, and the raw-JSON pass in
`DescriptorValidator` that gives a CLI or an agent the JSON Pointer and the fix. An **exact** pattern still
applies and still only earns `UnhonouredSubsystems`' warning, which is what makes this a refusal of the
wildcard rather than of subscriptions.

`EventPattern` is the one authority for the frozen grammar — `HasWildcard` for this refusal,
`ReservedNamespaces` for the publish guard — and `EventPatternTests.The_reserved_namespaces_are_the_schema_s_own`
reads the alternation out of `schema/project.schema.json` itself, so the schema stays the authority over the
authority. A segment is a wildcard only when it is *entirely* `*`, because the grammar admits it nowhere else;
the cheap `pattern.Contains('*')` passes every other fact and is killed by its own.

**This refuses a descriptor whose only defect is being ahead of the build, against `UnhonouredSubsystems`'
own line** — refuse what silently produces wrong data, warn what is observably absent. It is taken anyway
because the two halves of "observable" come apart here: the absence is observable **today** (no rule fires,
and the author sees that), while the consequence is observable **never** — the day automation lands, a
wildcard already sitting in a descriptor becomes a cross-tenant fan-out with nobody re-reading the file that
declared it, and a delivery that reached the wrong tenant is not an absence anyone notices. The descriptor
being the durable artifact is the argument *for* tolerating one that runs ahead of the build in general, and
the argument *against* it in exactly this case. **Deviation 86.**

## What PR5a does not do

Each line with the issue or the PR that owns it.

| Not done | Owner |
|---|---|
| **Global ordering**, and cross-process same-millisecond ordering | **#150** (F7's partitioned claim) |
| **JSONata evaluation** in any of the four `$defs/jsonata` slots | **#149** |
| **`function`**, **`http.call`** | frozen in the schema, out of scope for all of PR5 |
| **`entity.update`** | PR5b's automation half — still open |
| ~~**Before-hooks**, the `CelProfile.Mutate` profile~~ | **done** — PR5b (#114); see *Before-hooks* above |
| **The budget-overrun rollback** | **not built, and not scheduled**: there is no wall-clock budget to overrun — the bound is the grammar (deviation 81, and *What bounds a hook's execution time* above) |
| **Before-hooks in `InMemoryAlvoData`** | **not built** — the public in-memory reference runs the policy engine but no hook pipeline, so a host testing against the double sees a `reject` not refuse and a `mutate` not apply. Deliberate (the contract suite is inherited by the two relational drivers, which have a transaction to run a hook in), and recorded as an **owed obligation** rather than a mere absence: deviation 85 |
| **Automation** (`event` + `schedule` triggers), cron, and cron's distributed lock | PR5b's automation half — still open (the lock: deviation 74) |
| ~~**`Publish`** (custom application events) and its security ruling~~ | **done** — PR5b-2; see *Publishing a custom application event* above |
| ~~**Wildcard subscription** (`entity.orders.*`) and its tenant scoping~~ | **ruled** — PR5b-2 refuses `*` at apply; the matcher itself waits on **#153**, see *The wildcard ruling* above |
| **HMAC signing / `secretRef`** on a delivery | 7.1 (webhook management) |
| **SMTP**, and a mail service in compose | not scheduled; `email` is console-only in F3 |
| **A DLQ and a redelivery UI** | 7.1 |
| **Per-endpoint field projection** on a delivery | **#152** |
| **`dataref`** for an envelope over 64 KB | **#151** |
| **Retention / pruning of `alvo_outbox`** — rows are never deleted, and the payload holds every entity's and tenant's unmasked images forever | **#154** |
| **Validation of a rendered `email.to`** — the recipient is caller-controlled row text, unchecked; inert only because the shipped sender delivers nowhere | **#155** |
| **Reserving the framework's own table names** against an entity declaration (they are excluded from introspection, not reserved) | **#156** |
| **`email.data`** — refused at apply, because nothing rendered it | the PR that gives `email` a `data.*` placeholder root |
| **Bulk coalescing** (`entity.orders.created.batch`) | unscheduled; the base design places it with automation, and `baas-analyza.md:682` is its criterion. Every write emits its own event today |

**This PR does not close #22.** It closes PR5a's half; #22 closes when PR5b merges.

## What PR5b and F7 inherit

Recorded here so that neither the design addendum nor a discarded implementation plan is the only place
these live.

- ~~**`Publish` and its security ruling.**~~ **Done — PR5b-2.** See *Publishing a custom application event*
  above for what shipped, and for the three things a custom event deliberately is not (deviations 87 and 88).
  The gap this bullet recorded was real and is worth keeping named: `Publish` appeared in **neither** PR's
  content row and in neither Definition of Done, so it was a hole in the addendum rather than a deferral.
  What the addendum said about the frozen `$defs/eventPattern` still stands unchanged — no descriptor rule can
  subscribe to `order.approved`, and the right fix is a **designed namespace**, once. PR5b-2 shipped the
  guarantee without the namespace, deliberately.
- ~~**Wildcard subscription.**~~ **Ruled — PR5b-2 took the second branch.** `*` is refused at apply, on both
  passes, because the first branch was unavailable: `AlvoEvent` carries no tenant attribute, so nothing at
  delivery could scope a subscription and the adversarial cross-tenant fact would have had no tenant on
  either side of its comparison. The matcher itself waits on **#153**, which is also the bullet below. See
  *The wildcard ruling* above, and deviation 86 for why this refuses a descriptor that is merely ahead of the
  build.
- **`@tenant.id` and `@user.roles` cannot resolve in a template, and the addendum's own table promises
  `@tenant.id`.** Measured: `AlvoEvent` carries `authid` and *no* tenant attribute and *no* roles, so
  `TemplatePlaceholder.Roots` is `new`/`old`/`event`/`@user` and both names are refused **by name**
  rather than as an unknown root — they are real Alvo CEL context references, so "unknown root" would
  misdescribe why they fail. `@tenant.id` is refused because answering it from the row's own `tenant_id`
  would answer a **different question** (which tenant the *row* belongs to, not which tenant the *caller*
  was in — and an `AlvoContext.System` write has no tenant at all). `@user.roles` is refused because the
  envelope carries authentication and never authorization. Giving `@tenant.id` a real answer is a
  **public-API and wire-format** change — a new attribute on `AlvoEvent`, its `AlvoEventJson` member, and
  compatibility for the outbox payloads this build already wrote — so it is deliberately not a PR5a fix,
  and whoever takes it owns that compatibility question. Filed as **#153**; the identity-claim half
  (`@user.claims`, which is what an `email` recipient actually needs) is the capability question **#146**
  raises and **#37** (RBAC) owns. One adjacent consequence is recorded there too: an after-hook **CEL
  condition** *may* name `@tenant.id`, and against the dispatcher's tenant-less context the interpreter's
  null rule makes every comparison against it `false` — so such a hook never fires. Fail-closed, and silent.
- **`AlvoContext` and provenance.** `chaindepth` is `0` and `causationid` is absent in PR5a because
  nothing yet runs a data action *because of* an event. PR5b needs a way to thread both into an
  `entity.update` action's write, which is either an `AlvoContext` change (a public type in
  `Abstractions`) or a distinct provenance parameter. That shape is PR5b's decision.
- **Do not derive an idempotency key per event id.** Data actions' keys were to be *"derived from the
  event id"*, but nothing prunes `alvo_idempotency` (**#115**), so the table would grow with **event**
  volume rather than with keyed creates. `AlvoIdempotency` is also honoured on create only and an
  anonymous actor cannot hold a key — so a dispatcher must pass a real `AlvoContext.System`, never
  `Anonymous`.
- ~~**`complex-crm`'s five broken expressions and the refusal-reason strengthening.**~~ **Done — deviation 76
  is discharged in full.** One fix landed in PR5b-1 (`lower(new.email)` → `lowerAscii(new.email)`, forced by
  that PR's own rename); one stopped being a defect untouched (`now()` compiles as a side-effect of the
  `Mutate` profile landing); PR5b-2 fixed the remaining three — the two list literals in
  `deals.beforeUpdate`' conditions (`:143`, `:147`, now spelled `(old.stage == 'won' || old.stage == 'lost')`)
  and the unresolvable `{{@user.email}}` template (`:221`, now `{{new.owner_id}}`, because an envelope carries
  authentication and no identity claims, so **no** placeholder root resolves to an address — the rule's own
  `description` says so and names #146 and #37).
  **And `Every_example_marked_not_runnable_really_is_refused` now asserts the refusal's *reason*:** the
  message must carry a fix suggestion from `UnhonouredFeatures.EveryFixSuggestion`, so a CEL syntax error can
  no longer stand in silently for the feature refusal the marker claims, and the marker really does have to
  shrink when `default` lands. Of the two adjacent traps this bullet recorded, `crm.alvo.json:82`'s
  `rollup.where` list literal is now refused by `RollupResolver`'s own structured error (PR6) rather than by
  a CEL failure, and the three `access` expressions are still compiled by nothing in `src/` (**#146**).
- ~~**`UnhonouredFeatures`' `simple-tasks`/`completed_at` citation is still fictional.**~~ **Gone — PR5b-1.**
  It left with the three `before*` entries it lived on, exactly as this bullet predicted: nothing in
  `src/` names `simple-tasks` any more (verified by search, not assumed). Recorded as closed rather than
  deleted, because the deviation it belongs to (77) says "the PR that edits this file fixes it" and a reader
  chasing that instruction should find out which PR did.
- ~~**The three remaining hook refusals.**~~ **Gone — PR5b-1 lifted all three**, so
  `UnhonouredFeatures` now carries no hook entry at all (#114). Deviation 75's structural problem was solved
  as it predicted: one pipeline behind a port, injected into the driver, with `AuthorizedCandidate` moved
  inside the transaction. The `InMemoryAlvoData` half is still owed — deviation 85, and its own row in the
  table above.
- **F7's partitioned claim** (**#150**, which also carries Q1's same-millisecond finding and the
  cross-process half `AlvoEventId` does not close), and **`dataref`** (**#151**).
- **Six things recorded and deliberately not fixed**, each because the fix is larger than the PR that found
  it and none is reachable as a disclosure today:
  - **The `hidden` mask on a create/update *response* is an in-memory post-filter only.** Correct today, but
    two gates became one, so the recommended replacement is a **named two-user fact** (one caller who may see
    the field, one who may not, over one row) rather than a second engine.
  - **Envelope size × batch is unbounded in process memory.** **#151** covers only the 64 KB *wire* rule and
    `dataref`; a batch of 100 large envelopes is a separate, in-process question.
  - **`To: ""` is still reachable** — an empty template render, or a NULL column — and reads as a broken mail
    server. Named in `AlvoMailMessage`'s remarks.
  - **`AlvoMailMessage.To` is unvalidated rendered row text** reaching a host's SMTP implementation: CRLF
    header injection in any sender that concatenates it into a header. One paragraph on the port's own
    remarks, written while the port is new; filed with the `To: ""` half as **#155**.
  - **Framework table names are excluded from introspection but not *reserved* against an entity
    declaration.** Nothing stops a descriptor declaring an entity that maps onto `alvo_outbox`. Filed as
    **#156**.
  - **Conditions are type-checked against CLR types at apply and evaluated against the JSON view at
    delivery.** The recommended pin is **one fact per scalar family** (number, boolean, timestamp, uuid,
    string) driven end to end through a real engine, so a family whose JSON round trip changes shape fails by
    name.
- **PR5b owes the "a network call must be inexpressible in a before-hook" guarantee**, and nothing structural
  holds it now that `IEmailSender` is a **public singleton port**: a before-hook running in the write
  transaction could resolve it and send mail mid-transaction. The guarantee has to become an *architectural*
  fact — nothing on the in-transaction path can reach a network port — for the same reason the JSONata ban's
  real test must be architectural rather than behavioural.

## What a provider owes, and where the port is

`IOutboxStore` is the **earned** port `package-boundary.md` predicted: *"a port is earned the moment a
driver's system schema grows a table no store call touches."* The dispatcher lives in the core, which
depends on `Abstractions` alone, and `OutboxTable` is `internal` to the EF driver — so the port is what
lets the two meet.

`IEmailSender` is the second new port, and the only one with an in-core default
(`ConsoleEmailSender`, via `TryAddSingleton`).

**`IOutboxStore` is required to resolve at boot.** `OutboxDispatcher` takes it as a constructor
dependency and is always registered, so from PR5a a database provider must supply that port to get a
running host. `AddRelationalProvider` registers it, so **no shipped provider is affected**; the cost
falls on a future non-EF or dynamic-storage provider (F7), which would otherwise meet it as a DI failure
at startup rather than as a documented obligation. It is recorded in `OutboxDispatcher`'s own remarks and
in `package-boundary.md`'s *What a database provider must implement to boot*, in the same words deviation
60 used for `IRuntimeSchemaWriter`'s widening.

Note for a reader coming from the base design: there is **no `IEventDispatcher`** in this build. That
name appears in the base design as the seam that would leave an external bus available as a later adapter
package; what PR5a actually shipped is `IOutboxStore`, and it serves the same purpose — an out-of-repo
adapter implements the queue, not the dispatcher.

## Where the code lives

| Assembly | Files |
|---|---|
| `MMLib.Alvo.Abstractions/Events/` | `AlvoEvent`, `AlvoEventId`, `AlvoEventAttributes`, `AlvoEventJson`, `IOutboxStore`, `IEmailSender` — all public |
| `MMLib.Alvo.Abstractions/Rules/` | `IBeforeHookRunner` — public, the port a storage driver calls in-transaction |
| `MMLib.Alvo.Data.EntityFrameworkCore/` | `Internal/OutboxTable`, `Internal/OutboxEventFactory`, `EfCoreOutboxStore`; the four before-hook call sites in `Internal/EfAlvoData` |
| `MMLib.Alvo/Events/` | `AlvoEventOptions`, `Setup`; and `Internal/`: `OutboxDispatcher`, `EventSubscriptions`, `EventActionExecutor`, `WebhookDelivery`, `ConsoleEmailSender`, `AlvoTemplate`, `JsonataSlot`, `AfterHookCompiler`, `ActionVocabulary`, `AlvoEventMetrics`, `EventLog`, `AlvoEventOptionsConfiguration` |
| `MMLib.Alvo/Rules/` | `BeforeHooks` (`EntityBeforeHooks`, `CompiledBeforeHook`, `CompiledMutation`) and `Internal/`: `BeforeHookCompiler`, `BeforeHookRunner` — all `internal` |
| `MMLib.Alvo.Testing/Events/` | `OutboxStoreContractTests` + `IOutboxStoreWorld`, `AlvoEventCriteriaTests` + `IAlvoEventWorld` — the suites a provider inherits |
| `MMLib.Alvo.Testing/Data/` | `AlvoDataBeforeHookTests` + `IAlvoDataBeforeHookWorld` — the before-hook suite every engine inherits |

Only `AlvoEventOptions`, the three ports and the four envelope types are public; everything else in the
core is `internal`.

## What is proved, and where

| Claim | Fact |
|---|---|
| the row rides the same `DbTransaction`, on **update and delete** as well as create | `AlvoDataOutboxTests` (10 facts, inherited by both engines) — incl. `A_write_the_engine_refuses_leaves_no_outbox_row` and `An_insert_on_a_rolled_back_transaction_leaves_no_row` |
| the envelope is CloudEvents-conformant | `CloudEventsConformanceTests`, through `CloudEventAttribute.CreateExtension` as an oracle, plus one pinned envelope |
| `AlvoEventId` is monotonic | `AlvoEventIdTests`, with Q1's own numbers; the forced-repeat run asserts all 100 000 ids share one millisecond as its non-vacuity control |
| the claim protocol | `OutboxStoreContractTests` (8 facts), green on SQLite and on a real PostgreSQL container |
| the claim stays raw SQL | `ChangeTrackerReachTests.The_outbox_claim_is_raw_sql_and_never_linq_over_the_context` |
| a re-apply plans no `DROP` for the table | `OutboxTableTests`, `SystemSchemaInitializer.FrameworkTableNames` |
| `changed(status) && new.status == 'approved'` fires exactly once at the transition | `AlvoEventCriteriaTests`, 4/4 on SQLite and 4/4 on PostgreSQL |
| N events matching nothing → zero log rows, one counter each | same suite; measured **101** filtered against **1** action-log entry, with the positive control in the same fact |
| the readiness gate | `OutboxDispatcherTests.The_pump_claims_nothing_before_the_boot_reports_ready` — in the **core** suite, because the host-suite shape is structurally vacuous (every `StartingAsync` runs before any `StartAsync`) |
| an exception in the loop does not stop the host | `OutboxDispatcherTests`, both suites |
| 10 000 events lose nothing, **with the retry backoff in force** | `OutboxChaosCriteriaTests` — `accepted=10000 distinct=10000 attempts=10526 refused=526 abandoned=20 claims=108 pending=0 retired=10000`, identical on both engines; one line per run in `artifacts/criteria/events.md`. The world advances its fake clock one poll interval per batch, without which the backoff strands the redeliveries (measured: 186 pending) |
| kill between commit and publish → delivered after restart; kill mid-action → the action repeats | `KilledHostRecoveryTests`, against a real child process, exit code **137** on Unix / **-1** on Windows — neither reachable by the host's own exits (0, 78) |
| what the in-process harness does **not** prove | `OutboxRecoveryTests`' own name and remarks; the two files exist separately so neither can be mistaken for the other |
| **no log line carries a webhook URL** that could be a secret | `EventActionExecutorTests.No_log_line_carries_a_webhook_url_that_could_be_a_secret` — the absence is asserted only after the same run proved the secret-shaped segment was on the wire, over the message **and** the attached exception |
| **`@tenant.id`/`@user.roles` are refused** in an after-hook condition, positive and negated form alike | `AfterHookCompilerTests.A_condition_naming_provenance_the_envelope_lacks_is_refused_at_apply`, with `A_condition_reading_user_id_compiles_and_records_that_it_needs_an_actor` as the control that keeps it from being "refuse every `@`" |
| a condition's **`@user.id` is the envelope's actor**, and an actorless event selects no hook that reads it | `EventSubscriptionsTests` — three facts, including the non-vacuity control that a hook reading no caller value is still selected |
| **`email.data` is refused** whatever it carries | `UnhonouredJsonataTests.An_email_data_slot_is_refused_at_apply_whatever_it_carries` (both spellings) + the `UnhonouredFeatures` slot baseline |
| an endpoint **URL is validated at apply**: absolute, and `https` unless loopback | `AfterHookCompilerTests`, two theories — the refusals and the deliverable-shapes control |
| a released entry is **held for its backoff and not for the lease**, and a zero backoff is claimable at once | `OutboxStoreContractTests` (2 facts), green on SQLite and on a real PostgreSQL container |
| a `mutate` reaches the stored row, a `reject` leaves **no row** behind, and a replay runs no hook a second time | `AlvoDataBeforeHookTests` (14 facts, inherited by both engines) — incl. `An_idempotent_replay_runs_no_hook_a_second_time` and `Every_write_face_consults_the_hook_pipeline` |
| a `mutate` may write a field the **caller** may not, and still cannot place a row the `create` rule refuses | same suite — `A_mutate_may_write_a_field_the_caller_is_refused` paired with `A_mutate_that_moves_a_row_out_of_the_create_rule_is_refused` and its positive control |
| the hook runs **inside** the write's own transaction, at exactly **four** call sites and nowhere else | `BeforeHookTransactionArchitectureTests` — the scan asserts the call follows `BeginTransactionAsync` within each body, and has its own facts proving the scan can see a call site and can read order within a member |
| **a before-hook cannot make a network call**, one hop or two from its constructor | `BeforeHookIsolationArchitectureTests` — over `BeforeHookRunner`'s real dependency closure, plus the port's synchronous signature, plus a deliberately offending chain as the non-vacuity control |
| `lowerAscii` folds `A`–`Z` and **nothing else**, `lower(...)` is refused naming it, and both calls compile in `Mutate` alone | `CelMutateFunctionTests` (18 facts and theories) |
| `now()` is the write's **bound** instant — the same value for every evaluation in one write, and unmoved by a clock that advances mid-write | same suite — `Now_is_the_writes_bound_instant_and_not_a_fresh_read`, `Now_is_the_same_instant_for_every_evaluation_in_one_write`, and `The_interpreter_is_handed_an_instant_and_never_a_clock_to_read` |
| the backoff **grows** per attempt, and the shipped defaults spread the ceiling over ≥ 45 s | `OutboxDispatcherTests`, two facts — the second is arithmetic over the defaults so no test waits 45 s |

**Where the numeric criteria live, with the citation corrected.** The addendum and PR5a's plan both cite
`baas-analyza.md:676-680`; **`:676` is a blank line.** The §3 acceptance-criteria block is `:677-684`, and
what PR5a owns of it is:

| Line | Criterion |
|---|---|
| `:677` | the block header, *"Akceptačné kritériá (celý §3)"* |
| `:678` | the outbox crash test — kill between commit and publish → delivered after restart; **kill mid-action → the action repeats**; and **no lost event in a 10k-event chaos test**. All three numbers in one line |
| `:679` | `changed(status) && new.status == 'approved'` triggers **exactly once, at the transition** — not on every update (the Appwrite gap test) |
| `:680-684` | not PR5a's: Standard Webhooks signature verification (7.1), DLQ + bulk redelivery (7.1), bulk coalescing, loop-depth capping, and the before-hook budget rollback (PR5b) |

**The execution-log / counter criterion is *not* in that block**, and it should not be cited to it: it comes
from §3.3's Directus argument and the base design's own *"nearly free if designed in and awkward to
retrofit"* reasoning, promoted into PR5a's Definition of Done by the addendum. It is no less binding — it is
just not one of the analysis's numbered lines.

Each criterion is written the way `PagingPerformanceTests` established: assert the setup **before**
measuring, put the number in the failure message, and append the measurement to `artifacts/criteria/`.

**Ring placement, and why it is the project name.** Both engine legs of the chaos criterion live in
`*.Tests.Integration` projects (`MMLib.Alvo.Data.Sqlite.Tests.Integration`,
`MMLib.Alvo.Data.PostgreSql.Tests.Integration`), and the child-process crash facts in
`MMLib.Alvo.Host.Tests.Integration` — so a 27-second run and a `dotnet publish` are not "after every
small step". The tier is the **project suffix**, not a trait or a class filter: either of those needs a
matching *include* in ring2, and CI already sets `TESTINGPLATFORM_EXITCODE_IGNORE=8` on the Windows leg,
so an include that stopped matching would pass with zero tests executed. A misnamed *project* can only
ever land back in ring0, which is loud.

**One Stryker consequence of that move, stated rather than discovered.** The Stryker configs pin explicit
test-project lists, and `stryker-config.data-ef.json` names `MMLib.Alvo.Data.EntityFrameworkCore.Tests`
and `MMLib.Alvo.Data.Sqlite.Tests` only — so the chaos suite no longer contributes to that assembly's
run. What remains as `data-ef`'s killers for the outbox files, which are **not** excluded from its
`mutate` list: `OutboxTableTests`, `OutboxClaimSqlTests` and `OutboxEventFactoryTests` in
`MMLib.Alvo.Data.EntityFrameworkCore.Tests`, and `SqliteOutboxTableTests`, `SqliteOutboxStoreTests` and
`SqliteAlvoDataOutboxTests` in `MMLib.Alvo.Data.Sqlite.Tests`. Read the report for `OutboxTable.cs`
specifically: a surviving mutant in the claim predicate is a claim that cannot lose a row *because
nothing tests it*. And read every absolute score against **#142** — Stryker reports `Killed` for mutants
that survive the suite here, so no percentage from that run is proof of anything on its own.

**Two facts carry no mutation of their own, deliberately.**
`A_webhook_receives_the_unmasked_record_and_that_is_documented` is D7's named pin and there is no masking
code to mutate — it exists so the disclosure is a decision on the record, and it does go red when the
endpoint stops being carried. `Every_event_counter_is_published_on_the_one_meter_under_its_documented_name`
is a naming pin, and it discriminates against a renamed instrument and against a counter created on a
second meter — which is the failure that would make the criteria suite's listener silently read zero.

**One hole that is written down rather than closed.** Publishing `AlvoBootState`'s completion *before* the
interlocked snapshot swap stayed green over three runs. The completion source is deliberately
`RunContinuationsAsynchronously`, so a waiter always resumes through the thread pool and the ordering
window is nanoseconds against microseconds of dispatch. That ordering is held by the code and its remarks,
not by a fact, and this sentence is the record of it.
