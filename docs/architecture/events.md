# The event backbone

How a committed write becomes a delivered after-hook action, and the decisions that shape it. Written
during F3 PR5a (#22).

> **Status: complete for PR5a**, which is the *durable half* of #22. Everything below describes what the
> code does today; where a decision was deliberately deferred it says so and names the PR or issue that
> owns it — see *What PR5a does not do* and *What PR5b and F7 inherit* at the end, which is where a PR5b
> author starts.
>
> **Sibling records:** [`data-path.md`](./data-path.md) owns the port and the SQL a read or a write
> becomes; [`host.md`](./host.md) owns the boot and the process. This file owns the queue and the
> delivery: the envelope, `alvo_outbox`, the claim, the dispatcher, and the after-hook pipeline that
> hangs off them. The split is along the same seam — a decision about a statement lives in
> `data-path.md`, a decision about the boot lives in `host.md`, a decision about an event lives here.
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
that states the guarantee at all states **both** halves of it — `AlvoEvent`, `AlvoEventId`, `IOutboxStore`,
`AlvoEventOptions` and `EfAlvoData`'s emit remarks. If you find one that states only the first, it is wrong.

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
| `email` | **runs** | `IEmailSender`, with **only** a console dev provider — no SMTP, no mail service in compose |
| `entity.update` | refused at apply | PR5b's |
| `function` | refused at apply | frozen into `$defs/action`; out of scope for all of PR5 |
| `http.call` | refused at apply | same |

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

`data.record` and `data.old_record` are the complete images with **no `hidden` mask applied**. Three
reasons, in order of weight:

1. An after-hook condition reading `old.commission_note` or `changed(commission_note)` must see every
   field, and `hidden` is a per-caller **read** mask rather than a data classification.
2. A masked post-image would be worse than incomplete: every masked field would read as moved on every
   update, so `data.changed` would report changes that never happened.
3. The consequence is bounded by who declares what. An after-hook `webhook` delivers hidden fields to an
   endpoint declared **in the same descriptor by the same author** as the `hidden` rule — never
   caller-supplied, and a template can never render into a URL.

It is pinned by a **named** fact rather than a paragraph
(`A_webhook_receives_the_unmasked_record_and_that_is_documented`, plus
`An_events_record_carries_a_hidden_field_unmasked_and_that_is_the_documented_disclosure` over a real
engine). Per-endpoint field projection is **#152**.

**The action log stops at the join key, and that is D7 taken one step further.** `ActionExecuted` carries
the hook's JSON pointer, the action `type`, the event id and the event type — and **not** the rendered
body, the recipient, the subject or the endpoint URL. A log line has no author who declared it: logging
the rendered value would take a `hidden` field out of the one place the design accepted it going and put
it into whatever ships logs, which nobody declared. The payload is stored once, in the `alvo_outbox` row,
under that table's retention rather than a log pipeline's. `ConsoleEmailSender` is the one deliberate
exception and not an exception to the rule — for a console provider the log *is* the mailbox.

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

**The dispatcher's caller is `AlvoContext.System(tenant: null)`** — explicit, never an ambient accessor,
because there is no request scope here. It is `System` and never `Anonymous`, which matters because an
anonymous actor cannot hold an idempotency key. It carries **no tenant**, deliberately: the envelope
records which *caller* acted and never which tenant they acted in. The consequence is fail-closed and
worth knowing — an after-hook condition comparing `@tenant.id` resolves against a null tenant, and the
interpreter's null rule makes any comparison against it **`false`**, so such a hook never fires. Silent
but denying, never matching "any tenant".

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

`ClaimLease` (default 5 minutes) is refused at startup unless it **outlasts** `PollInterval`: a lease
shorter than the interval re-claims an entry that is still in flight on the very next tick, which is a
duplicate delivery per tick rather than at-least-once delivery. Every options refusal names the
configuration key, its `Alvo__…` environment spelling, and a value that would have worked;
`ValidateDataAnnotations()` is deliberately **not** used, because a `[Range]` message names the property
rather than the key and would add a second, worse message to every refusal.

## What PR5a does not do

Each line with the issue or the PR that owns it.

| Not done | Owner |
|---|---|
| **Global ordering**, and cross-process same-millisecond ordering | **#150** (F7's partitioned claim) |
| **JSONata evaluation** in any of the four `$defs/jsonata` slots | **#149** |
| **`function`**, **`http.call`** | frozen in the schema, out of scope for all of PR5 |
| **`entity.update`** | PR5b |
| **Before-hooks**, the `CelProfile.Mutate` profile, the budget-overrun rollback | PR5b |
| **Automation** (`event` + `schedule` triggers), cron, and cron's distributed lock | PR5b (the lock: deviation 74) |
| **`Publish`** (custom application events) and its security ruling | unowned — see below |
| **Wildcard subscription** (`entity.orders.*`) and its tenant scoping | PR5b — see below |
| **HMAC signing / `secretRef`** on a delivery | 7.1 (webhook management) |
| **SMTP**, and a mail service in compose | not scheduled; `email` is console-only in F3 |
| **A DLQ and a redelivery UI** | 7.1 |
| **Per-endpoint field projection** on a delivery | **#152** |
| **`dataref`** for an envelope over 64 KB | **#151** |
| **Bulk coalescing** (`entity.orders.created.batch`) | unscheduled; the base design places it with automation, and `baas-analyza.md:682` is its criterion. Every write emits its own event today |

**This PR does not close #22.** It closes PR5a's half; #22 closes when PR5b merges.

## What PR5b and F7 inherit

Recorded here so that neither the design addendum nor a discarded implementation plan is the only place
these live.

- **`Publish` and its security ruling.** `Publish` must **refuse** a name matching
  `^(entity|auth|storage)\.`, or a host can mint an event indistinguishable from a real data change, and
  every descriptor rule and after-hook subscribing to `entity.orders.updated` would fire on a forged one
  — with a `partitionkey` and provenance nobody wrote a row for. Also: `$defs/eventPattern` is frozen to
  `^(entity|auth|storage)\.([a-z][a-z0-9_]*|\*)\.([a-z]+|\*)(\.batch)?$`, so no descriptor rule can
  subscribe to `order.approved` at all, and the same grammar makes `auth.user.password_changed`
  unrepresentable (segment 3 is `[a-z]+`). The right fix is a **designed namespace**, once — not a
  prefix bolted on under one PR's schedule. `Publish` is named in **neither** PR's content row and in
  neither Definition of Done, which is a gap in the addendum rather than a deferral; this bullet is the
  record of it.
- **Wildcard subscription.** `entity.orders.*` is a **hard** spec guarantee
  (`alvo-specifikacia.md:141`) with no matcher today. `baas-analyza.md:657` requires tenant isolation of
  rules, so a wildcard makes cross-tenant fan-out the default failure mode. PR5b either implements the
  matcher **with every subscription scoped to the envelope's tenant and a named adversarial cross-tenant
  fact**, or refuses `*` at apply until it exists. PR5a evaluates no pattern at all, because after-hooks
  are declared per entity per hook point.
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
- **`complex-crm`'s five broken expressions and the refusal-reason strengthening.** `crm.alvo.json` ships
  four hook expressions that do not compile (`lower(new.email)`, two `in ['won','lost']` list literals,
  `now()`) plus a fifth unresolvable template (`{{@user.email}}`), and
  `DescriptorToSchemaMapperTests.Every_example_marked_not_runnable_really_is_refused` asserts only
  `Should.Throw<InvalidDataException>` — so a CEL syntax error can silently stand in for the feature
  refusal the test claims to hold. Safe to defer *only* because the example declares hooks on
  `beforeCreate`/`beforeUpdate` and **zero** `after*` points, which PR5a pinned off the whole `examples/`
  tree rather than off `complex-crm` by name, so a new example declaring an after-hook fails that fact.
  Two adjacent traps recorded and not fixed: `crm.alvo.json:82`'s `rollup.where` list literal (PR6
  inherits the identical shape), and the three `access` expressions, which are compiled by nothing in
  `src/` (**#146**).
- **`UnhonouredFeatures`' `simple-tasks`/`completed_at` citation is still fictional.** That XML comment
  cites `examples/simple-tasks/tasks.alvo.json`' `beforeUpdate` setting `completed_at`; that example
  declares no `hooks` block and no such field. The reasoning is sound, the example is invented, and the
  real case is `deals.beforeUpdate` setting `closed_at`. The addendum assigns the fix to whichever PR
  edits the file; PR5a edited it and left the `before*` half untouched on purpose (no `before*` author
  sees a changed message), so the correction rides with PR5b's removal of those three entries.
- **The three remaining hook refusals.** `beforeCreate`/`beforeUpdate`/`beforeDelete` stay in
  `UnhonouredFeatures` (**#114** tracks all six; three are lifted). A before-hook runs **in the write
  transaction**, and the create path as built cannot satisfy its own DoD line: `AuthorizedCandidate` runs
  before `BeginTransactionAsync`, so a hook placed where the candidate is built has nothing to roll back.
- **F7's partitioned claim** (**#150**, which also carries Q1's same-millisecond finding and the
  cross-process half `AlvoEventId` does not close), and **`dataref`** (**#151**).

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
| `MMLib.Alvo.Data.EntityFrameworkCore/` | `Internal/OutboxTable`, `Internal/OutboxEventFactory`, `EfCoreOutboxStore` |
| `MMLib.Alvo/Events/` | `AlvoEventOptions`, `Setup`; and `Internal/`: `OutboxDispatcher`, `EventSubscriptions`, `EventActionExecutor`, `WebhookDelivery`, `ConsoleEmailSender`, `AlvoTemplate`, `JsonataSlot`, `AfterHookCompiler`, `ActionVocabulary`, `AlvoEventMetrics`, `EventLog`, `AlvoEventOptionsConfiguration` |
| `MMLib.Alvo.Testing/Events/` | `OutboxStoreContractTests` + `IOutboxStoreWorld`, `AlvoEventCriteriaTests` + `IAlvoEventWorld` — the suites a provider inherits |

Only `AlvoEventOptions`, the two ports and the four envelope types are public; everything else in the
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
| 10 000 events lose nothing | `OutboxChaosCriteriaTests` — `accepted=10000 distinct=10000 attempts=10526 refused=526 abandoned=20 claims=108 pending=0 retired=10000`, identical on both engines; one line per run in `artifacts/criteria/events.md` |
| kill between commit and publish → delivered after restart; kill mid-action → the action repeats | `KilledHostRecoveryTests`, against a real child process, exit code **137** on Unix / **-1** on Windows — neither reachable by the host's own exits (0, 78) |
| what the in-process harness does **not** prove | `OutboxRecoveryTests`' own name and remarks; the two files exist separately so neither can be mistaken for the other |

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
