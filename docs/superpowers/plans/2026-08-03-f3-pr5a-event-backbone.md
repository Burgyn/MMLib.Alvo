# F3 PR5a — the event backbone — Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development
> (recommended) or superpowers:executing-plans to implement this plan task-by-task.
> Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Ship the durable half of issue #22 — a CloudEvents-1.0.2-conformant envelope, an
`alvo_outbox` row written on the *same* `DbTransaction` as every create/update/delete, a single
dispatcher gated on `AlvoBootState`, after-hooks with a `webhook`/`email` action set, the `{{…}}`
template engine with a raw-JSONata refusal, and the numeric crash/chaos criteria — green on SQLite
and PostgreSQL.

**Architecture:** The write path emits. `EfAlvoData`'s four begin/commit sites gain one explicit
outbox insert each, on `transaction.GetDbTransaction()` — never a `SaveChanges` interceptor, which
`ExecuteUpdate`/`ExecuteDelete` would silently bypass. The envelope is a hand-written type in
`Abstractions` (which may take no new external dependency), serialized by hand, with
`CloudNative.CloudEvents` used **test-only** as a conformance oracle. A new `IOutboxStore` port —
earned, per `package-boundary.md`, because the outbox is the first driver system-schema table no
store call touches — carries claim/mark/release; the claim is one portable raw-SQL statement whose
outer `WHERE` **repeats** the subquery's `claimed_at IS NULL`
(`UPDATE … WHERE dispatched_at IS NULL AND (claimed_at IS NULL OR …) AND id IN (SELECT … ORDER BY …
LIMIT n) RETURNING …`), never a high-water mark. The outer predicate is not redundant: spike Q4
measured that without it two claimants deliver **every** row twice. The envelope's `id` is minted by
a monotonic UUIDv7 generator, because `Guid.CreateVersion7()` alone inverts 49.9 % of
same-millisecond pairs (spike Q1). The
dispatcher is a `BackgroundService` that awaits `AlvoBootState` (ordering cannot express readiness on
.NET 10) and lets nothing escape `ExecuteAsync` (`BackgroundServiceExceptionBehavior` defaults to
`StopHost`). After-hook conditions are compiled into the **`PolicyCatalog`** — one priming site — and
evaluated at *subscription* time, so a filtered-out event costs one counter increment and no
execution-log entry.

**Tech Stack:** .NET 10 (`net10.0`), EF Core 10, ASP.NET Core minimal APIs,
Microsoft.Testing.Platform (MTP), xUnit v3, Shouldly, Verify (snapshot), CsCheck, NSubstitute,
`PublicApiGenerator` (API approval), Testcontainers (PostgreSQL), Husky.Net hooks, TeaPie (e2e).
One new **test-only** package: `CloudNative.CloudEvents` (Apache-2.0).

---

## Global Constraints

Every task's requirements implicitly include this section.

**Authority**

- **The two authoritative documents, in this order:**
  `docs/superpowers/specs/2026-08-02-f3-pr5-events-hooks-design-addendum.md` (deviations 58–78, the
  CloudEvents conformance table, the PR5a/PR5b split, the PR5a Definition of Done at `:982-1018`)
  and `.superpowers/sdd/2026-08-02-f3-pr5-events/risk-register.md` (R1–R14, the frozen descriptor
  surface, the hard constraints from the code). **Both are established. Do not re-derive them.**
- The base design's *Events, hooks, automation* section
  (`docs/superpowers/specs/2026-07-25-f3-crud-vertical-slice-design.md:542-618`) still stands where
  the addendum does not contradict it — and it **over-claims ordering** (`:574-577`); see the
  ordering constraint below.
- The write seam is `docs/architecture/data-path.md:1481-1493` (*"The transaction is already the
  right seam"*). Measured evidence for this PR lands in
  `docs/superpowers/specs/evidence/2026-08-03-f3-pr5a-events/`.
- **`…/evidence/2026-08-03-f3-pr5a-events/spike.txt` has been captured and outranks this plan
  wherever the two disagree.** It refuted two things this plan asserted — D1's monotonicity claim and
  D2's claim statement — and both are amended above. Do not re-derive a measured answer from
  reasoning, and do not restore a shape the spike measured broken.
- **Out of scope — PR5b:** before-hooks, the `CelProfile.Mutate` profile, automation (`event` +
  `schedule` triggers), `entity.update`, cron, `Publish`, wildcard subscription matching, the
  `complex-crm` example's five broken expressions, and
  `Every_example_marked_not_runnable_really_is_refused`'s strengthening. Do not start any of them.

**Process**

- **Never merge or push to `main`.** Branch `f3/pr5-events` → PR → a human merges. This PR is based
  on `f3/startup-lifecycle` and **cannot merge before it** (deviation 70).
- **This is a security-core PR** (base design `:747-750`): the `alvo-security-core-review`
  checklist, a security review, `alvo-plan-guard` as the last pre-PR check, and a
  `workflow_dispatch` mutation run green before merge.
- **Commit after every task**, and commit *before* mutating anything.
- **Rings:** `scripts/test-ring0` after each task; `scripts/test-ring2` before the PR;
  `scripts/test-e2e` because compose and the host are touched.

**Code and API**

- **`Abstractions` may take no new external dependency** (`package-boundary.md:96-103`). The
  envelope is therefore hand-written there and `IEventDispatcher` is **not** `IHostedService`
  (register R13). `System.Text.Json` is in the shared framework, not a package, and
  `AlvoDescriptorJsonContext` already proves it is available in `Abstractions`.
- **`public` is the contract.** Default to `internal`. Every public change moves a
  `PublicApi.*.verified.txt` baseline, which `alvo-snapshot-judge` rules on.
- **Never hand-edit a `*.verified.*` baseline.** Let Verify write the `.received.` file and accept
  it; expect `.claude/hooks/turn-review-gate` to require `alvo-snapshot-judge` when one moves.
- **XML docs are required on every public member** of `Abstractions` and the core.
- **Short, single-purpose methods** (~25-line ceiling) and **zero inline comments** — lift the value
  into a named constant, the branch into a named predicate (`alvo-dotnet-conventions`). Rationale
  that a name cannot carry goes in `<remarks>`.
- **`CA1848` is an error here:** every log line is a source-generated `[LoggerMessage]` partial,
  never `logger.LogWarning(...)`.
- **Never call `BuildServiceProvider()` during registration.** Use `IConfigureOptions<T>` /
  `OptionsBuilder.Configure<TDep>`.
- **`extensibility.md` verb taxonomy:** `Use{Provider}` infra, `Add{Thing}` additive,
  `Enable{Feature}` toggle, `From{Source}` descriptor source, `Apply{Thing}` runtime operation. Do
  not invent a verb. Every options type is validated at startup with
  `ValidateDataAnnotations().ValidateOnStart()` or `IValidateOptions<T>`, producing a structured
  error **with a fix suggestion** (rule 5).

**The data path's own rules — these are from the code, not preferences**

- **The outbox row rides the same `DbTransaction`** as the data change:
  `db.Database.GetDbConnection()` + `transaction.GetDbTransaction()`, `command.Transaction =
  transaction`. Copy `IdempotencyTable.cs:140,151,183,196`'s shape. The four begin/commit sites in
  `EfAlvoData.cs` are `177/179`, `321/330`, `582/586`, `620/622`.
- **Never hang the outbox off `SaveChanges`.** `ExecuteUpdate`/`ExecuteDelete` fire **no**
  interceptor, so an interceptor silently misses update and delete — the two operations that most
  need an event (`data-path.md:1486-1493`).
- **Add the outbox name to `SystemSchemaInitializer.FrameworkTableNames`** (`:67`) or the
  introspector plans a `DROP` for it on the next re-apply.
- **Add every new SQL-composing file to `ChangeTrackerReachTests._sqlComposingFiles`**
  (`test/MMLib.Alvo.Data.EntityFrameworkCore.Tests/ChangeTrackerReachTests.cs:177-189`) — it is an
  allow-list and the arch test fails until you do.
- **No change-tracker writes** anywhere new: no `AsTracking`, `EntityState.`, `Attach`, `Update`,
  `Remove`, `Entry`.
- **Every timestamp through `StoredInstant`** (UTC, TEXT). `StoredInstant` is `internal` to the EF
  driver, so the envelope enforces the same rule at its own boundary: `AlvoEvent` refuses a `Time`
  whose `Offset` is not `TimeSpan.Zero`.
- **Claim predicates run under `UseRelationalNulls()`** (`data-path.md:121-145`): in LINQ, write
  `x != null && x != y`, not `x != y`. This plan writes the claim as **raw SQL**, where SQL's
  semantics are native — and adds an arch fact that it stays raw SQL, so the constraint is met by
  construction rather than by memory.
- **`AlvoContext` is a required parameter on every `IAlvoData` call.** The dispatcher passes one
  explicitly, never an ambient accessor; data actions run as `AlvoContext.System(tenant)`.
- **Idempotency:** honoured on create only; an anonymous actor cannot hold a key; nothing prunes the
  table (#115). Do **not** derive an idempotency key per event id in this PR (addendum
  *What PR5 does not do* item 9).

**CloudEvents 1.0.2 (design against v1.0.2; there is no 1.0.3)**

- Wire `specversion` is `"1.0"`.
- Extension attribute **names** are `[a-z0-9]+` only and SHOULD stay ≤ 20 characters
  ([spec v1.0.2:173-175](https://github.com/cloudevents/spec/blob/v1.0.2/cloudevents/spec.md#attribute-naming-convention)).
  So `payload_version`, `chain-depth` and `old_record` are **illegal as attribute names** →
  `payloadversion`, `chaindepth`, and `data.old_record`.
- Context attribute **values** are limited to seven types (`Boolean`, `Integer` int32, `String`,
  `Binary`, `URI`, `URI-reference`, `Timestamp`) — **no map, array or object type** (`:179-217`). So
  `record`, `old_record` and the changed-column list live inside `data`.
- Extensions are **flat top-level** JSON members (`:439-440`) — never a nested `extensions` object.
- Prefer the registered names: **`partitionkey`** (Partitioning), `sequence`/`sequencetype`
  (Sequence), `dataref` (Dataref). `authtype`/`authid` and `correlationid`/`causationid` are the
  community's names but are **not registered in v1.0.2** — they live in `extensions/authcontext.md`
  and `extensions/correlation.md` on `main`. Adopt them; **state that provenance in the XML docs**,
  or a reader checking the v1.0.2 registry concludes they were invented.
- Intermediaries MUST forward events ≤ 64 KB (`:510-512`). The registered escape is `dataref`, which
  this PR documents and does not implement (**#151**).

**Hosting (.NET 10)**

- `BackgroundService.ExecuteAsync` runs **entirely** off the startup thread, so *"the dispatcher must
  not run before the schema is primed"* **cannot be expressed by ordering** and `await Task.Yield()`
  as a first line is dead code. Gate on **`AlvoBootState`** (deviation 70).
- `HostOptions.BackgroundServiceExceptionBehavior` defaults to `StopHost`, so one poison event would
  take down a host serving HTTP. Nothing may escape `ExecuteAsync` (deviation 71).
- The host **blocks in `StopAsync`** waiting for `ExecuteAsync`, with a 30 s `ShutdownTimeout`, so
  the loop observes its cancellation token promptly. `ServicesStartConcurrently` stays `false`.

**Ordering — do not repeat the base design's over-claim, and do not repeat this plan's own first one**

- Exactly **one dispatcher** in F3. `FOR UPDATE SKIP LOCKED` skips the **row**, not the **key**, so
  it delivers neither global nor per-entity-key ordering (deviation 72; `baas-analyza.md:656`'s
  hedge, which the base design dropped).
- **The guarantee, in the only wording this plan may use** — every doc, XML doc, test message and PR
  body says it with **both** conditions, because the first draft of this plan stated only the first
  and spike Q1 measured that the second is where it actually breaks:

  > **Per-entity-key ordering holds with one dispatcher *and* no two events for one key inside the
  > same millisecond.**

  The **in-process** half of the millisecond condition is closed by the monotonic generator below,
  so within one process the guarantee reduces to "one dispatcher". Across processes it does not:
  two hosts minting inside one millisecond still interleave (**#150**).
- **The `id` is minted by a monotonic UUIDv7 generator, never `Guid.CreateVersion7()` directly.**
  Measured (Q1): `Guid.CreateVersion7()` has no monotonic counter — it fills everything below the
  48-bit millisecond with fresh random data, so **49.9 %** of adjacent same-millisecond pairs sort
  backwards (49 839 inversions over 100 000; 99 961 of those pairs shared a millisecond). Bumping
  the random tail whenever the millisecond repeats measured **0 inversions over 100 000** and costs
  **no DDL change**. `AlvoEventId.Create` (Task 2) is the one entry point.
- **There is no distributed lock**, so PR5a cannot detect a second instance: two replicas break the
  guarantee **silently**. That is a documented deployment constraint, filed as **#150**.
- **`partition_key` is written from the first migration** even though nothing reads it in F3, so
  F7's partitioned claim is additive. It is named after the registered `partitionkey` attribute so
  the column and the attribute cannot drift.
- **The claim filters `dispatched_at IS NULL`, never a high-water mark** — PostgreSQL sequences
  commit out of order (R2), so a watermark drops a row silently.

**Engine facts — measured in Task 1's spike (`spike.txt`), not guessed**

Each of these corrects something a source document or an earlier draft of this plan asserted. Cite
the question, not the reasoning, wherever the plan leans on one.

- **`ORDER BY` inside `UPDATE` is refused by *both* engines**, and the parser names `ORDER`, not
  `limit`: SQLite `'near "ORDER": syntax error'`, PostgreSQL `42601 syntax error at or near "ORDER"`
  (Q3). So the subquery `LIMIT` is a **portability** constraint, not a SQLite workaround, and R4's
  recorded message text was wrong about which token fails.
- **`RETURNING` comes back unsorted on both engines** — `RETURNING already sorted: False` for
  SQLite *and* PostgreSQL (Q3). The in-process re-sort is load-bearing in fact, not only on paper.
- **`SERIAL` is silently *accepted* by SQLite** as an unrecognised column type, yielding a nullable
  column that never increments (Q6). A "portable `SERIAL`" therefore passes CI and loses ordering in
  production. Nobody may reach for it; `OutboxTableTests` asserts its absence for this reason.
- **Each engine refuses the other's identity spelling** — SQLite refuses `… AS IDENTITY`,
  PostgreSQL refuses `AUTOINCREMENT` (Q6). `SystemSchemaInitializer.cs:15-17`'s "no per-engine
  branching" invariant survives only because there is no sequence column at all.
- **UUID text ordering agrees with .NET's ordinal sort on both engines** under
  `datcollate=en_US.utf8`, in both the `'D'` and `'N'` spellings, and also under `COLLATE "C"`,
  `COLLATE "POSIX"` and a native `uuid` column (Q2). **No collation-spelling fallback is needed** and
  D1's `"N"` fallback is withdrawn. It holds because every UUID text form is fixed width with its
  punctuation at fixed positions — a property of fixed-width keys, not of that locale.
- **`Guid`'s default byte order is not time-sortable** (`ToByteArray()`: 5 050 inversions of 9 999;
  `ToByteArray(bigEndian: true)`: 4 993, i.e. the same as the text form) (Q1). The id is therefore
  safe as `TEXT` and would be unsafe as a `BLOB` written from `ToByteArray()`.
- **`created_at` is disqualified by a wide margin** (Q7): 10 000 successive `GetUtcNow()` reads
  produce **495** distinct `"O"` stamps with tie runs of 26, and **3** distinct values at
  millisecond precision.
- **R5's premise is corrected and the WAL stop-condition did *not* trigger** (Q5). It is not true
  that there is "no `Default Timeout` anywhere": `Microsoft.Data.Sqlite`'s `DefaultTimeout` is
  **30 s** and its retry loop covers `BEGIN`, which is what already makes the shipped registration
  correct — a second writer waited ~1 s and then succeeded, in both directions. **Do not change the
  shared SQLite registration.** WAL does **not** fix the read-then-write shape; it converts the
  failure into an unretryable `SQLITE_BUSY_SNAPSHOT` (`Extended=517`) on the *dispatcher*, and
  `journal_mode=WAL` is **persistent in the database file**, so it is not revertible by redeploy.
  The constraint therefore lands on the **dispatcher**: the claim is a **single write statement** on
  an autocommit connection, or the first statement of a write-first transaction — **never** a read
  followed by a write inside one transaction.

**Shell and tooling traps — measured, not guessed**

- **Running one test class — the MTP invocation.** `dotnet test <proj> --filter X` does **not**
  work: `dotnet test` needs `--project`, and MTP rejects `--filter` outright
  (`Unknown option '--filter'`). Use
  `dotnet test --project test/MMLib.Alvo.Tests -- --filter-class '*OutboxDispatcherTests*'`
  or `-- --filter-namespace 'MMLib.Alvo.Tests.Events'`.
- **`grep` is aliased to `ugrep` in this shell**, and it miscounts CRLF patterns. Use
  **`command grep -c`** when confirming a mutation's edit landed.
- **`.gitattributes` pins `*.cs` to CRLF**, and new `.cs` files need a **UTF-8 BOM** to match the
  tree. A search string with LF endings matches nothing.
- **Assert `Build succeeded` before believing any test result.** A broken build silently runs the
  previous binary.
- **CI may use a newer analyzer set than local** (#129 — `CA1873` broke the image build and only the
  e2e caught it). A green local build is not a green CI build.
- `ci.yml`'s `build-test` is `timeout-minutes: 20` for **all** of ring2. The 10k chaos test has to
  fit inside that with everything else.

**Dependencies**

- Four foreign dependencies are candidates across PR5 (envelope, cron, SMTP, JSONata) and **none is
  in CPM**. **PR5a takes at most one, test-only:** `CloudNative.CloudEvents` as a conformance
  oracle. Cron, SMTP and JSONata are **not added**. Every version goes in
  `Directory.Packages.props`; `PackageReference` carries no `Version`.
- **Doc-drift to fix in this PR:** `.claude/skills/alvo-dotnet-conventions/SKILL.md` still
  recommends **Wolverine** for the outbox, which the base design's deviation 1 rejected (Alvo owns
  the outbox; `IEventDispatcher` leaves a bus available as a later adapter package). Correct the
  skill rather than leaving two answers in the repo.

---

## Definition of Done — PR5a, quoted from the addendum (`:982-1018`), not invented

Each line is followed by the task that owns it.

1. A 10k-event chaos run **loses no event**, on SQLite and PostgreSQL. → **Task 11**
2. Kill **between commit and publish** → delivered after restart; **kill mid-action → the action
   repeats**. The harness states in its own name what it does and does not prove. → **Task 12**
3. The outbox row rides the **same `DbTransaction`**, proven by a rollback test finding no outbox
   row — and **not** hung off `SaveChanges`, so the test covers **update and delete**, not only
   create. → **Task 4**
4. The envelope **passes a CloudEvents conformance check** (names `[a-z0-9]+`, seven types, flat
   extensions, `record`/`old_record`/changed inside `data`), asserted through
   `CloudEventAttribute.CreateExtension` as an oracle. → **Task 2**
5. `changed(status) && new.status == 'approved'` on an after-hook fires **exactly once, at the
   transition**. → **Task 10**
6. **N events matching nothing produce zero execution-log rows and one counter increment.** →
   **Task 10**
7. A raw JSONata expression in any `$defs/jsonata` slot is **refused at apply** by a named
   `UnhonouredFeatures` entry, with the four classifier cases pinned. → the four classifier cases in
   **Task 6**, the apply-time refusal in **Task 7**
8. An **absence** fact: no JSONata evaluator exists on any path. → **Task 7**
9. A template placeholder naming an undeclared field, or an unresolvable root such as
   `@user.email`, is **refused at apply** with a fix suggestion — never rendered empty. → the
   placeholder rules in **Task 6**, the apply-time refusal in **Task 7**
10. The dispatcher **awaits `AlvoBootState`**, proven by a fact that does not depend on registration
    order; an exception inside the loop **does not stop the host**. → **Task 9**
11. The outbox table name is in `SystemSchemaInitializer.FrameworkTableNames`, so a second apply
    produces an **empty** plan. → **Task 3**
12. Every new SQL-composing file is in `ChangeTrackerReachTests._sqlComposingFiles`; no
    change-tracker write appears anywhere in the dispatcher. → **Tasks 3, 5**
13. Every timestamp goes through `StoredInstant`; every claim predicate is written for
    `UseRelationalNulls()` semantics. → **Tasks 3, 5**
14. Public-API baselines approved; `alvo-snapshot-judge` passed on every moved `*.verified.*`;
    `workflow_dispatch` mutation run green before merge. → **Task 13**

**DoD items with no task, and why** — recorded here so the gap is a decision:

- **`Publish` (custom application events).** Named in neither PR's content row nor either DoD list;
  R8's security ruling (`Publish` must refuse a name matching `^(entity|auth|storage)\.`) is owed by
  whichever PR ships `Publish`. **Not in PR5a.** Task 13 records it as an open obligation so it
  cannot be lost with this plan.
- **Wildcard subscription (`entity.orders.*`) and its tenant scoping.** After-hooks are declared per
  entity per hook point, so PR5a evaluates no pattern at all. The matcher belongs to
  `automation.trigger.event` = PR5b. Recorded in Task 13.
- **`dataref` / the 64 KB forwarding rule.** Documented in Task 2's remarks and filed as **#151** in
  Task 1; not implemented, because Alvo's outbox is not an intermediary and no wire hop in F3 is
  bound by the rule.
- **`complex-crm`'s five broken expressions and the refusal-reason strengthening** (deviation 76).
  PR5b's, and it is safe there: the example declares **only** `beforeCreate`/`beforeUpdate` hooks
  (`crm.alvo.json:106-113`, `:141-150`), so removing PR5a's three `after*` entries from
  `UnhonouredFeatures` leaves the example refused by the two `before*` entries and exposes none of
  its CEL defects. Task 7 Step 6 asserts exactly that, so the claim is measured rather than assumed.

---

## Decisions this plan makes, and what Task 1's spike changed

Six decisions the addendum leaves to the plan. Each is stated with its cost; the three marked
**measured** were settled by Task 1's evidence, which has **run** — `spike.txt` is now the authority
over this section wherever the two disagree.

Two decisions came back changed, and both amendments are ratified:

- **D1's portability half survived; its monotonicity half did not.** UUIDv7 stays; a monotonic
  generator is added, and the ordering language is corrected everywhere (Q1).
- **D2's claim SQL was wrong** — as first written it delivers every row **twice** under two
  claimants, so its stated cost ("slow, not incorrect") was false. The one-line fix measured clean
  in the same run and is adopted verbatim (Q4).

**D1 — There is no `sequence` column. The ordering key is `id`, a *monotonic* UUIDv7.
(measured: Q1, Q2, Q6, Q7 — amended after the spike)**

R1 is real: `AUTOINCREMENT` vs `IDENTITY` is per-engine DDL, and
`SystemSchemaInitializer.cs:15-17` states *"identical on SQLite and PostgreSQL … no per-engine
branching"* as an invariant with **zero** precedent for breaking it. Falling back to
`created_at TEXT` is worse — the audit stamp binds **one instant per write**
(`data-path.md:354`), so ties are structural, not merely likely.

The third option neither document names: a UUIDv7 primary key (.NET 9+ mints one with
`Guid.CreateVersion7()`), time-ordered in its most-significant 48 bits, whose standard string form
sorts lexicographically in time order. Store it as the primary key and order by it. That gives an
ordering key with **identical ANSI DDL**, no invariant break, no new port, and — the part that
matters — **no integer watermark for anyone to be tempted by**, which closes R2 by construction
rather than by discipline.

**What the spike confirmed.** Portability, in full: identical ANSI DDL on both engines, each engine
refusing the other's identity spelling (Q6); text ordering matching .NET's ordinal sort on both
engines in both spellings, under `en_US.utf8` as well as `COLLATE "C"`/`"POSIX"` and a native `uuid`
column (Q2) — so the `"N"` fallback this decision once carried is **withdrawn**, not merely unused;
and `created_at` disqualified at 3 distinct values per 10 000 at millisecond precision (Q7).

**What the spike refuted, and the amendment.** `Guid.CreateVersion7()` is **not** monotonic: it has
no counter and fills everything below the millisecond with fresh random bits, so **49.9 %** of
adjacent same-millisecond pairs sort backwards (49 839 inversions over 100 000). The claim that
same-millisecond order was merely *unguaranteed* was wrong — it is a coin flip.

The amendment, measured green in the same run at **0 inversions over 100 000** and costing **no DDL
change**: an in-process **monotonic** generator that reuses the last emitted millisecond and
increments the random tail whenever the clock's millisecond does not advance. It lives in
`Abstractions` as `AlvoEventId` (Task 2), for three reasons:

1. **The write path must reach it, and the write path is a different assembly.** The id is minted in
   `OutboxEventFactory` inside `MMLib.Alvo.Data.EntityFrameworkCore`, which sees only
   `Abstractions`' public surface — `Abstractions` grants `InternalsVisibleTo` to `MMLib.Alvo` and
   `MMLib.Alvo.Tests` only. A generator in the core, or `internal` anywhere, is unreachable from the
   emit sites.
2. **The ordering contract belongs to the envelope, not to one driver.** `id` *is* the queue order;
   a driver-local generator would have to be re-derived by the next `IOutboxStore` implementation,
   and the failure mode of forgetting — plain `CreateVersion7()` — is invisible from outside, since
   both spellings produce a valid v7 id. One authority in `Abstractions` makes it unforgettable.
3. **It is testable on its own**, with no database, no host and no clock injection: monotonicity is
   observable through the public API, and `Create(DateTimeOffset)` — the shape
   `Guid.CreateVersion7(DateTimeOffset)` already established in the BCL — makes the
   same-millisecond and backwards-clock cases deterministic. That overload is not a test seam: the
   emit sites pass the write's own audit instant, so the envelope's `time`, the row's `created_at`
   and the id's embedded millisecond are **one** instant (`data-path.md`, *Every timestamp is one
   instant*).

Cost, stated: the generator closes the **in-process** half only. Two processes minting inside one
millisecond still interleave, so the guarantee is *one dispatcher **and** no two events for one key
in one millisecond* — the wording every artifact must use — with the cross-process half filed as
**#150**. A second, smaller cost: a process-wide lock on the mint path (nanoseconds, on a path that
is already doing I/O) and a `Create(DateTimeOffset)` whose returned millisecond is the **later** of
the requested one and the last one already minted, which is what keeps the total order intact and is
documented on the member.

Recorded because it decides how the column may ever be typed (Q1): `Guid`'s **default** byte order
is not time-sortable (5 050 inversions of 9 999 from `ToByteArray()`), so the id is safe as `TEXT`
and would be unsafe as a `BLOB`. And a bonus the generator buys for free: a backwards clock step,
which Q1 measured reorders the queue by the size of the step, cannot reorder anything **within** one
process, because the last emitted millisecond never moves backwards.

**D2 — `IAlvoSqlDialect` is not widened. There is no `SKIP LOCKED`. (measured: Q3, Q4 — the
statement is amended)**

`SKIP LOCKED` exists to let *several* claimants work one queue. PR5a has exactly one dispatcher
(deviation 72), so it buys nothing, and adding a member to a public port in a driver package is a
public-API change that a later F7 design would have to live with. The claim is therefore **one
portable statement** owned by the driver — **this text, which is the spike's, verbatim:**

```sql
UPDATE {outbox} SET claimed_at = @claimed_at, claimed_by = @claimed_by,
                    attempts = attempts + 1
 WHERE dispatched_at IS NULL
   AND (claimed_at IS NULL OR claimed_at < @stale_before)
   AND id IN (SELECT id FROM {outbox}
               WHERE dispatched_at IS NULL
                 AND attempts < @max_attempts
                 AND (claimed_at IS NULL OR claimed_at < @stale_before)
               ORDER BY id
               LIMIT @batch)
RETURNING id, event_type, partition_key, payload, attempts
```

**The outer `WHERE` is load-bearing, not redundant — do not "simplify" it away.** This plan's first
draft had the outer `WHERE` be nothing but `id IN (subquery)`, and Q4 measured what that costs:
*"A claimed 10, B claimed 10, overlap 10 (must be 0); rows with attempts > 1: 10"* — **every row
delivered twice, and `attempts` incremented twice.** The mechanism is PostgreSQL's `READ COMMITTED`
EvalPlanQual re-check: when B's block on A's row locks clears, B re-evaluates **only its own outer
`WHERE`** against the updated row. The subquery's `claimed_at IS NULL` was already evaluated and is
not part of that re-check, so B's outer `id IN (…)` still holds and B re-claims A's rows. Repeating
the predicate in the outer `WHERE` puts the invariant where EvalPlanQual can see it: *"A claimed 10,
B claimed 0, overlap 0; rows with attempts > 1: 0"*. Both engines accept the statement (Q3, *"the
Q4 variant — ACCEPTED"* on SQLite and PostgreSQL). The subquery still chooses **which** rows; the
outer `WHERE` re-validates that they are still claimable at the moment of the write.

`UPDATE … ORDER BY … LIMIT` is **not** available on **either** engine — the parser names `ORDER`
(SQLite `'near "ORDER": syntax error'`, PostgreSQL `42601`), correcting R4 on both counts — so the
`ORDER BY` and the `LIMIT` go in the subquery. `RETURNING` order is arbitrary in **measured** fact
on both engines, not only on paper (`RETURNING already sorted: False` twice), so the result is
re-sorted in process by `id`, ordinally.

Cost, stated: with no `SKIP LOCKED`, a second dispatcher on PostgreSQL **blocks** on the first's row
locks rather than skipping them, and then claims nothing (Q4, with the amended statement). So a
second instance is slow, not incorrect — a claim this decision may make **only** because the outer
predicate is there. Ordering is a separate matter and still breaks with two dispatchers (**#150**).

**D3 — The envelope is hand-written in `Abstractions`; the SDK is test-only. (settled)**

R13's two options were "hand-written envelope + SDK as a core-side mapper" or "ports in the core".
Ports in the core is rejected: it breaks `Data.EntityFrameworkCore` implementing `IOutboxStore`
against `Abstractions` alone. A **core-side mapper** is rejected too, one step further than the
addendum goes: nothing in PR5a needs the SDK at run time — Alvo serializes its own envelope for the
outbox row and for webhook delivery — so a shipped foreign dependency would be a package-boundary
cost with no consumer (`package-boundary.md`: a dependency is earned). `CloudNative.CloudEvents`
therefore lands as a **test-only** `PackageReference` in `test/MMLib.Alvo.Tests`, used as the
conformance oracle the DoD asks for.

Cost, stated: Alvo's JSON is not produced by the SDK, so *structural* conformance is proven by the
oracle over the attribute **names and types** plus a Verify snapshot of one real envelope — not by
round-tripping through the SDK's own formatter. Task 2 says so at the fact.

**D4 — Provenance lives in the envelope, not in duplicate outbox columns. (settled)**

The base design's column list (`:557-561`) carries `actor`, `correlation_id`, `provenance_depth` as
columns *and* the same values inside the payload. Two authorities for one value is how they come to
disagree. Only `partition_key` is duplicated, and only because F7 must index it.

**D5 — After-hook actions in PR5a are `webhook` and `email`; the other three are refused by name.
(settled)**

`function` and `http.call` are frozen-in and out of scope for all of PR5 (deviation 66).
`entity.update` is PR5b's. `email` is **in** PR5a and is not optional: `templates.subject`/`body` and
`email.to` are the only slots that exercise the template engine's plain-string sugar, and deviation
64's consequence (`{{@user.email}}` is refused, never rendered to `To: ""`) is unreachable without
them. Delivery is an `IEmailSender` port with a **console dev provider only** — no SMTP, no new
dependency, no mail service in compose (addendum *What PR5 does not do* item 7).

**D6 — The "execution log" in F3 is structured logs plus metrics, not a table. (settled)**

The criterion is *"a filtered-out event produces no execution log, only a counter"*. A durable,
queryable execution log with retention and a redelivery UI is 7.1. PR5a ships one
source-generated log entry **per executed action** and a `Meter` with
`alvo.events.dispatched` / `alvo.events.filtered` / `alvo.events.failed`, and the fact asserts that a
filtered event produces **no** action entry and exactly **one** filtered increment. Recorded as a
deviation, because a reader could otherwise expect a table.

**D7 — The envelope carries the *unmasked* row, and that is a disclosure worth naming. (settled)**

`data.record` is the complete post-image with **no `hidden` mask applied**, because an after-hook
condition reading `old.commission_note` or `changed(commission_note)` must see every field, and
`hidden` is a per-caller *read* mask rather than a data classification. The consequence is real: an
after-hook `webhook` delivers hidden fields to the declared endpoint. It is accepted because the
endpoint is declared in the same descriptor by the same author as the `hidden` rule — never
caller-supplied — and it is pinned by a **named** fact rather than a paragraph
(Task 8, `A_webhook_receives_the_unmasked_record_and_that_is_documented`), with per-endpoint field
projection filed as **#152** in Task 1.

---

## File Structure

**New — `Abstractions` (`src/MMLib.Alvo.Abstractions/Events/`)**

| File | Responsibility |
|---|---|
| `AlvoEvent.cs` | `AlvoEvent` + `AlvoEventData`: the hand-written CloudEvents 1.0.2 envelope and its `data` payload. Public. Guards `Time.Offset == TimeSpan.Zero`. |
| `AlvoEventId.cs` | The **monotonic** UUIDv7 generator every event id is minted by (D1, spike Q1). Public, because the emit sites live in a different assembly. |
| `AlvoEventAttributes.cs` | The **one** authority on wire attribute names and which of them are extensions. Public; the conformance oracle iterates it. |
| `AlvoEventJson.cs` | Hand-written `Write`/`Read` over `Utf8JsonWriter`/`JsonDocument`. Flat top-level extensions; `snake_case` only inside `data`. Public. |
| `IOutboxStore.cs` | `IOutboxStore` + `OutboxEntry`: the claim/mark/release port the dispatcher depends on. Public. |
| `IEmailSender.cs` | The mail provider port + `AlvoMailMessage`. Public. |

**New — EF driver (`src/MMLib.Alvo.Data.EntityFrameworkCore/`)**

| File | Responsibility |
|---|---|
| `Internal/OutboxTable.cs` | The table's name, its DDL, and every statement: `InsertAsync` (on the caller's transaction), `ClaimAsync`, `MarkDispatchedAsync`, `ReleaseAsync`. **SQL-composing** → allow-list. |
| `Internal/OutboxEventFactory.cs` | Builds an `AlvoEvent` from (schema, operation, context, post-image, pre-image). No SQL, no I/O. |
| `EfCoreOutboxStore.cs` | `IOutboxStore` over `OutboxTable`, on its own connection. Public (the port's implementation, like `EfCoreDescriptorVersionStore`). |

**New — core (`src/MMLib.Alvo/Events/`)**

| File | Responsibility |
|---|---|
| `AlvoEventOptions.cs` | `PollInterval`, `BatchSize`, `MaxAttempts`, `ClaimLease`, `Enabled`. Bound from `Alvo:Events`. Public. |
| `Internal/AlvoEventOptionsValidation.cs` | `IValidateOptions<AlvoEventOptions>` with a fix suggestion per member. |
| `Internal/OutboxDispatcher.cs` | The single `BackgroundService`: gate on `AlvoBootState`, claim → dispatch → mark, contain every failure. |
| `Internal/EventSubscriptions.cs` | Which after-hooks an event selects, and the **condition evaluated here** — before any execution entry exists. |
| `Internal/EventActionExecutor.cs` | Runs one action: `webhook`, `email`; refuses the other three by name. |
| `Internal/WebhookDelivery.cs` | One `HttpClient` POST to a declared endpoint. No HMAC (7.1). |
| `Internal/ConsoleEmailSender.cs` | The dev `IEmailSender`. |
| `Internal/AlvoTemplate.cs` | The `{{…}}` engine: parse, validate against a schema, render against an envelope. |
| `Internal/JsonataSlot.cs` | The template-vs-raw-JSONata classifier (deviation 63's rule). |
| `Internal/AlvoEventMetrics.cs` | The `Meter` and its three counters. |
| `Internal/EventLog.cs` | Every `[LoggerMessage]` partial this subsystem writes. |

**Modified**

| File | Change |
|---|---|
| `src/MMLib.Alvo.Data.EntityFrameworkCore/Internal/SystemSchemaInitializer.cs` | Create the outbox table; add its name to `FrameworkTableNames`. |
| `src/MMLib.Alvo.Data.EntityFrameworkCore/Internal/EfAlvoData.cs` | Four emit sites, inside the four existing transactions. |
| `src/MMLib.Alvo.Data.EntityFrameworkCore/AlvoEfCoreProvider.cs` | Register `EfCoreOutboxStore` as `IOutboxStore`. |
| `src/MMLib.Alvo/Rules/PolicyCatalog.cs` | `EntityPolicy` gains `AfterHooks`; add `CompiledAfterHook`, `EntityAfterHooks`, `CompiledAction`. All internal. |
| `src/MMLib.Alvo/Rules/Internal/PolicyCatalogBuilder.cs` | Compile after-hook conditions (`CelProfile.Condition`) and validate every template — in the **same** pass (R11). |
| `src/MMLib.Alvo/Descriptor/Internal/UnhonouredFeatures.cs` | Drop the three `after*` entries; add `RawJsonata` and `UnhonouredAction`. |
| `src/MMLib.Alvo/Descriptor/Internal/UnhonouredSubsystems.cs` | Reword `templates`/`webhooks`: they are now honoured **from an after-hook** and unhonoured from automation; name that `secretRef`/HMAC is not applied. |
| `src/MMLib.Alvo/Migrations/AlvoBootState.cs` | An `internal Task<AlvoBootPhase> SettledAsync(CancellationToken)`. |
| `src/MMLib.Alvo/AlvoServiceCollectionExtensions.cs` | Register the options, the metrics, the executor, the dispatcher. |
| `test/MMLib.Alvo.Data.EntityFrameworkCore.Tests/ChangeTrackerReachTests.cs` | `OutboxTable.cs` and `EfCoreOutboxStore.cs` into `_sqlComposingFiles`. |
| `Directory.Packages.props` | `CloudNative.CloudEvents` (test-only). |
| `docs/architecture/data-path.md` | Replace the PR5 forward-looking section with what shipped. |
| `docs/architecture/events.md` *(new)* | The subsystem of record: envelope, outbox, claim, ordering guarantee **and its condition**, deviations. |
| `.claude/skills/alvo-dotnet-conventions/SKILL.md` | Remove the Wolverine recommendation (base design deviation 1). |

---

### Task 1: The de-risking spike, and the follow-up issues

**DONE** — commit `dabca3b`. The evidence is
`docs/superpowers/specs/evidence/2026-08-03-f3-pr5a-events/spike.txt` and **it outranks this plan**
wherever the two disagree. The spike program was deleted in the same commit. The four issues exist:
**#149** JSONata evaluator · **#150** per-entity-key ordering (F7; it also carries Q1's monotonicity
finding) · **#151** `dataref` over 64 KB · **#152** per-endpoint field projection. Two decisions came
back changed — see D1 and D2 above, both amended, both ratified. The steps below are kept as the
record of what was measured and how.

Nothing in Tasks 2–13 may assume an answer this task did not measure. The spike is throwaway: it
must not survive the task. What survives is
`docs/superpowers/specs/evidence/2026-08-03-f3-pr5a-events/spike.txt` — verbatim captured output
with a provenance header — and four filed issues.

**Files:**
- Create: `spike/MMLib.Alvo.Events.Spike/MMLib.Alvo.Events.Spike.csproj` (net10.0, `OutputType=Exe`)
- Create: `spike/MMLib.Alvo.Events.Spike/Program.cs`
- Create: `docs/superpowers/specs/evidence/2026-08-03-f3-pr5a-events/spike.txt`
- Delete (at the end of this task): the whole `spike/` directory and its `MMLib.Alvo.slnx` entry

**Interfaces:**
- Consumes: `Microsoft.Data.Sqlite`, `Npgsql`, `Testcontainers.PostgreSql` — all already in CPM.
- Produces: no shipped surface. Its output is the evidence file plus the four issue numbers Task 7,
  Task 8 and Task 13 reference.

- [x] **Step 1: Scaffold the spike and answer Q1, Q6 and Q7 — the ordering key**

The spike is one `Program.cs` with one method per question, each printing a banner and a verdict
line. Q1, Q6 and Q7 need no container.

```csharp
static void Q1_V7OrderingIsMonotonic()
{
    const int Samples = 100_000;
    var ids = new string[Samples];
    for (var i = 0; i < Samples; i++) ids[i] = Guid.CreateVersion7().ToString();

    var inversions = 0;
    for (var i = 1; i < Samples; i++)
        if (string.CompareOrdinal(ids[i - 1], ids[i]) >= 0) inversions++;

    Console.WriteLine($"Q1 v7 'D' ordinal inversions over {Samples}: {inversions}");

    var nInversions = 0;
    var n = new string[Samples];
    for (var i = 0; i < Samples; i++) n[i] = Guid.CreateVersion7().ToString("N");
    for (var i = 1; i < Samples; i++)
        if (string.CompareOrdinal(n[i - 1], n[i]) >= 0) nInversions++;
    Console.WriteLine($"Q1 v7 'N' ordinal inversions over {Samples}: {nInversions}");
}

static void Q6_IdentityDdlIsNotPortable()
{
    using var connection = new SqliteConnection("Data Source=:memory:");
    connection.Open();
    foreach (var ddl in new[]
    {
        "CREATE TABLE q6a (seq BIGINT GENERATED BY DEFAULT AS IDENTITY, id TEXT)",
        "CREATE TABLE q6b (seq INTEGER PRIMARY KEY AUTOINCREMENT, id TEXT)",
    })
    {
        try
        {
            using var command = connection.CreateCommand();
            command.CommandText = ddl;
            command.ExecuteNonQuery();
            Console.WriteLine($"Q6 SQLite ACCEPTED: {ddl}");
        }
        catch (SqliteException failure)
        {
            Console.WriteLine($"Q6 SQLite REFUSED: {ddl}\n    {failure.Message}");
        }
    }
}

static void Q7_OneWriteIsOneInstant()
{
    var clock = TimeProvider.System;
    const int Writes = 10_000;
    var stamps = new string[Writes];
    for (var i = 0; i < Writes; i++) stamps[i] = clock.GetUtcNow().ToUniversalTime().ToString("O");

    Console.WriteLine(
        $"Q7 distinct 'O' stamps over {Writes} successive GetUtcNow(): {stamps.Distinct().Count()}"
        + $"; largest tie run: {LongestRun(stamps)}");
}
```

`LongestRun` is a four-line local helper counting the longest run of equal adjacent values. Print
the same three banners the evidence file will carry.

Run: `dotnet run --project spike/MMLib.Alvo.Events.Spike 2>&1 | tee /tmp/spike-q1.txt`
Expected: Q1 reports **0** inversions for at least one of the two spellings; Q6 reports SQLite
REFUSING the `IDENTITY` form (and accepting `AUTOINCREMENT`, which PostgreSQL refuses — the pair is
the point); Q7 reports a tie run > 1, which is what disqualifies `created_at` as the ordering key.

**Measured — the Q1 expectation was falsified.** Both spellings inverted about half their adjacent
pairs (49 839 and 49 898 over 100 000), because `Guid.CreateVersion7()` carries no monotonic counter.
The spelling is a Q2 question, not a Q1 one. The monotonic wrapper measured **0 inversions over
100 000** and is now D1's amendment. Q6 and Q7 landed as expected, plus one trap worth its own line:
SQLite **accepts** `SERIAL` as an unrecognised column type and never increments it.

- [x] **Step 2: Q2 and Q3 — cross-engine text ordering, and the portable claim statement**

Both engines, same shuffled input, same assertions.

```csharp
static async Task Q2_TextOrderingAgrees(DbConnection connection, string engine)
{
    var ids = Enumerable.Range(0, 5_000).Select(_ => Guid.CreateVersion7().ToString()).ToList();
    var shuffled = ids.OrderBy(_ => Random.Shared.Next()).ToList();

    await Exec(connection, "CREATE TABLE q2 (id TEXT NOT NULL PRIMARY KEY)");
    foreach (var chunk in shuffled.Chunk(500)) await InsertIds(connection, "q2", chunk);

    var fromEngine = await ReadIds(connection, "SELECT id FROM q2 ORDER BY id");
    var fromDotnet = shuffled.Order(StringComparer.Ordinal).ToList();

    Console.WriteLine(
        $"Q2 {engine}: engine ORDER BY matches .NET ordinal: {fromEngine.SequenceEqual(fromDotnet)}"
        + $"; first divergence at {FirstDivergence(fromEngine, fromDotnet)}");
}

static async Task Q3_PortableClaim(DbConnection connection, string engine)
{
    await Exec(connection,
        "CREATE TABLE q3 (id TEXT NOT NULL PRIMARY KEY, claimed_at TEXT NULL, attempts INTEGER NOT NULL)");
    var ids = Enumerable.Range(0, 50).Select(_ => Guid.CreateVersion7().ToString()).Order(StringComparer.Ordinal).ToList();
    foreach (var id in ids) await Exec(connection, $"INSERT INTO q3 VALUES ('{id}', NULL, 0)");

    await TryStatement(connection, engine, "UPDATE … LIMIT (expected to fail on SQLite)",
        "UPDATE q3 SET claimed_at = 'x' WHERE claimed_at IS NULL ORDER BY id LIMIT 10");

    var claimed = await ReadIds(connection,
        """
        UPDATE q3 SET claimed_at = 'x', attempts = attempts + 1
         WHERE id IN (SELECT id FROM q3 WHERE claimed_at IS NULL ORDER BY id LIMIT 10)
        RETURNING id
        """);
    Console.WriteLine(
        $"Q3 {engine}: portable claim returned {claimed.Count} rows; RETURNING already sorted: "
        + $"{claimed.SequenceEqual(claimed.Order(StringComparer.Ordinal))}; "
        + $"claimed the 10 lowest ids: {claimed.Order(StringComparer.Ordinal).SequenceEqual(ids.Take(10))}");
}
```

Run against SQLite (a temp **file**, not `:memory:` — Q5 needs two connections on one file) and
against `postgres:16-alpine` through `Testcontainers.PostgreSql`, printing the container's
`lc_collate`/`datcollate` first so the collation the result was measured under is on the record.

Run: `dotnet run --project spike/MMLib.Alvo.Events.Spike 2>&1 | tee /tmp/spike-q23.txt`
Expected: Q2 `True` on both engines (if PostgreSQL is `False`, D1's fallback to `"N"` applies —
re-run Q2 with `"N"` in the same session and record both); Q3 shows `UPDATE … LIMIT` failing on
SQLite with the exact message R4 predicts, the portable statement returning 10 rows on both, and
`RETURNING` order **not** guaranteed sorted.

**Measured:** Q2 is `True` on both engines in both spellings, and also under `COLLATE "C"`,
`COLLATE "POSIX"` and a native `uuid` column, under `datcollate=en_US.utf8` — so the `"N"` fallback is
**withdrawn**, not merely unused. Q3 refused the statement on **both** engines and named **`ORDER`**,
not `limit`, which corrects R4 twice; and `RETURNING already sorted: False` on both, so the in-process
re-sort is load-bearing in fact.

- [x] **Step 3: Q4 and Q5 — two claimants, and the dispatcher as a second SQLite writer**

```csharp
static async Task Q4_TwoClaimantsOnPostgres(string connectionString)
{
    // Two connections, no SKIP LOCKED: the loser must claim NOTHING, not the same rows.
    await using var a = new NpgsqlConnection(connectionString);
    await using var b = new NpgsqlConnection(connectionString);
    await a.OpenAsync(); await b.OpenAsync();

    await using var ta = await a.BeginTransactionAsync();
    var claimedByA = await ReadIds(a, ta, ClaimSql(batch: 10));

    var bTask = Task.Run(async () =>
    {
        await using var tb = await b.BeginTransactionAsync();
        var ids = await ReadIds(b, tb, ClaimSql(batch: 10));
        await tb.CommitAsync();
        return ids;
    });

    await Task.Delay(500);
    Console.WriteLine($"Q4 B still blocked while A holds its rows: {!bTask.IsCompleted}");
    await ta.CommitAsync();
    var claimedByB = await bTask;

    Console.WriteLine(
        $"Q4 A claimed {claimedByA.Count}, B claimed {claimedByB.Count}, overlap "
        + $"{claimedByA.Intersect(claimedByB).Count()} (must be 0)");
}

static async Task Q5_SecondSqliteWriter(string file)
{
    // Exactly the shipped configuration: no journal_mode, no busy_timeout, no Default Timeout.
    await using var writer = new SqliteConnection($"Data Source={file}");
    await using var dispatcher = new SqliteConnection($"Data Source={file}");
    await writer.OpenAsync(); await dispatcher.OpenAsync();

    await using var held = await writer.BeginTransactionAsync();
    await Exec(writer, held, "INSERT INTO q5 (id) VALUES ('held')");

    try
    {
        await ReadIds(dispatcher, ClaimSql(batch: 10));
        Console.WriteLine("Q5 claim SUCCEEDED while a write transaction was open");
    }
    catch (SqliteException failure)
    {
        Console.WriteLine($"Q5 claim REFUSED while a write transaction was open: {failure.SqliteErrorCode} {failure.Message}");
    }

    await held.CommitAsync();
    // …then the same probe with `Default Timeout=5` on the dispatcher connection only.
}
```

Run: `dotnet run --project spike/MMLib.Alvo.Events.Spike 2>&1 | tee /tmp/spike-q45.txt`
Expected: Q4 shows B blocked, then claiming **0** rows with **0** overlap — the finding that makes
`SKIP LOCKED` unnecessary. Q5 shows whether the shipped SQLite configuration refuses a concurrent
claim and whether a **dispatcher-connection-only** `Default Timeout` fixes it.

> **The one blocking outcome in this whole spike.** If Q5 shows that only
> `journal_mode=WAL` on the *shared* registration makes the claim work, **stop**. That is a
> behaviour change for every existing SQLite consumer (register R5) and it is the maintainer's call,
> not this plan's. Record it in the evidence file, report it, and do not start Task 5.

**Measured — Q4 was the blocking finding instead, and the WAL stop-condition did not trigger.**

- **Q4 failed as the plan wrote it:** B claimed the same 10 rows and `attempts` reached 2 on all of
  them. The one-line fix — repeat `claimed_at IS NULL` in the **outer** `WHERE` — measured clean, and
  D2 now carries that statement verbatim. Task 5 must not use the original.
- **Q5 cleared:** the shipped registration is correct as it stands (`DefaultTimeout` 30 s, whose
  retry loop covers `BEGIN`), a second writer waits ~1 s and succeeds in both directions, and an
  explicit `PRAGMA busy_timeout=5000` changes nothing measurable. **Do not touch the shared SQLite
  registration.** The one shape that reaches R5's mechanism is a `DEFERRED` transaction that reads
  and then writes; WAL turns that into an unretryable `SQLITE_BUSY_SNAPSHOT` on the dispatcher rather
  than fixing it, and `journal_mode=WAL` persists in the database file. The constraint therefore
  lands on Task 5's and Task 9's shapes, not on any registration.

- [x] **Step 4: Q8 and Q9 — the two budgets ring2 has to absorb**

Q8: insert 10 000 rows through one transaction per 1 000, then run the portable claim to
exhaustion at `batch = 100`, on both engines. Print insert seconds, claim seconds, and total. Q9:
publish `MMLib.Alvo.Host` once, start it as a child process against a temp SQLite file, wait for
`/health/ready`, `Process.Kill(entireProcessTree: true)`, restart, wait for ready again. Print each
phase's elapsed time.

Run: `dotnet run --project spike/MMLib.Alvo.Events.Spike 2>&1 | tee /tmp/spike-q89.txt`
Expected: Q8 well under a minute per engine; Q9's publish-once-plus-two-boots under ~30 s. **The
decision rule:** if Q9's total exceeds 120 s, Task 12 ships the in-process fact **only**, with the
disclosure its own name carries, and the child-process harness is filed as an issue instead.

**Measured:** Q8 is 0.37 s (SQLite) and 0.51 s (PostgreSQL) for the storage floor, and Q9's budget is
**6.0 s** — publish 3.2 s + boots 1.6 s and 1.3 s, with exit code 137 confirming a real SIGKILL. So
the 120 s fallback does **not** apply and Task 12 ships the child-process harness.

- [x] **Step 5: Write the evidence file, verbatim, with a provenance header**

Concatenate the captured output into
`docs/superpowers/specs/evidence/2026-08-03-f3-pr5a-events/spike.txt`, prefixed exactly in the shape
`2026-08-02-startup-lifecycle/spike.txt` uses:

```text
Captured verbatim from: dotnet run --project spike/MMLib.Alvo.Events.Spike
SDK: <dotnet --version>   Runtime: .NET <version> (TFM net10.0)
Engines: Microsoft.Data.Sqlite <version> (bundled e_sqlite3 <version>), postgres:16-alpine
         (datcollate=<value>, datctype=<value>)
Host: <uname -sm>
Plan: docs/superpowers/plans/2026-08-03-f3-pr5a-event-backbone.md
Questions: Q1 v7 monotonicity · Q2 cross-engine TEXT ordering · Q3 the portable claim ·
           Q4 two claimants with no SKIP LOCKED · Q5 the dispatcher as a second SQLite writer ·
           Q6 identity-column DDL portability · Q7 clock ties · Q8 the 10k budget ·
           Q9 the child-process kill budget
```

Then add a **Verdicts** block at the end: one line per question, each either confirming a decision
in *Decisions this plan makes* or naming the fallback that now applies. Nothing paraphrased —
paste the measured lines.

- [x] **Step 6: Open the four follow-up issues, and record their numbers**

Later tasks quote these numbers in refusal messages, so they must exist now.

```bash
gh issue create --title "JSONata evaluator for the four \$defs/jsonata action slots" --body "$(cat <<'EOF'
PR5a refuses a raw JSONata expression in `webhook.payload`, `email.data`, `function.input` and
`http.call.payload`, honouring only `{{...}}` template sugar (design addendum deviations 62-65).

This issue owns the evaluator. Two constraints are already settled and must not be re-litigated:

- **No partial or vendored subset.** `CLAUDE.md`: inventing a variant of a standard is a defect, not
  a shortcut. The failure mode is a webhook delivered with a body the author did not declare.
- **The in-transaction ban must be proven ARCHITECTURALLY, not behaviourally** (deviation 65):
  nothing on the in-transaction path can *reach* the evaluator. PR5a's own test is an *absence*
  fact and does not discharge `alvo-specifikacia.md:300`.
EOF
)"

gh issue create --title "Per-entity-key ordering: partition the outbox claim (F7)" --body "$(cat <<'EOF'
PR5a ships exactly one dispatcher (design addendum deviation 72, R3). Per-entity-key ordering holds
while one dispatcher runs AND no two events for one key land in the same millisecond, and PR5a cannot
detect a second instance — two replicas break the guarantee silently.

Spike Q1 (measured) is why the second condition is in that sentence: `ORDER BY id` over a UUIDv7 is
exact only above the millisecond, and `Guid.CreateVersion7()` inverts 49.9% of adjacent
same-millisecond pairs. `AlvoEventId`'s monotonic wrapper closes that within one process (0 inversions
over 100 000); two processes minting inside one millisecond still interleave, which is this issue's.

`partition_key` is already written on every outbox row from the first migration, so the partitioned
claim is additive. `FOR UPDATE SKIP LOCKED` is NOT the answer: it skips the row, not the key.
Hash the key -> partition -> one worker per partition (Kafka/Debezium), or lock the key (Postgres
advisory locks).
EOF
)"

gh issue create --title "Outbox: dataref (claim-check) for an envelope over 64 KB" --body "$(cat <<'EOF'
CloudEvents v1.0.2 (spec.md:510-512): intermediaries MUST forward events of 64 KB or less.
`data.record` + `data.old_record` on a wide row can exceed that by themselves.

The registered escape is the `dataref` extension (Dataref / claim-check): a `URI-reference` to the
payload, which MAY coexist with `data`. PR5a documents the limit and does not implement the escape,
because Alvo's own outbox is not an intermediary.
EOF
)"

gh issue create --title "Per-endpoint field projection for webhook and email deliveries" --body "$(cat <<'EOF'
PR5a's envelope carries the UNMASKED post-image, because an after-hook condition reading `old.x` or
`changed(x)` must see every field and `hidden` is a per-caller read mask, not a data classification
(plan decision D7). Consequence: an after-hook `webhook` delivers `hidden` fields to the declared
endpoint. Accepted in F3 because the endpoint is descriptor-declared by the same author as the
`hidden` rule, and pinned by a named test rather than a paragraph.

This issue owns the projection: a per-endpoint allow-list of fields, or applying a named role's
mask to the delivered `data`.
EOF
)"
```

Record the four numbers in the evidence file's Verdicts block. **Tasks 7, 8 and 13 quote them.**

**The four numbers, which every later task must use:**

| Issue | Title | Quoted by |
|---|---|---|
| **#149** | JSONata evaluator for the four `$defs/jsonata` action slots | Task 7's refusal message |
| **#150** | Per-entity-key ordering: partition the outbox claim (F7) | Tasks 5, 13; the ordering wording everywhere |
| **#151** | Outbox: `dataref` (claim-check) for an envelope over 64 KB | Task 2's remarks, Task 13 |
| **#152** | Per-endpoint field projection for webhook and email deliveries | Task 8 (D7), Task 13 |

`#150` additionally carries Q1's finding: the ordering it owns is **already** broken by a
same-millisecond tie on **one** dispatcher, not only by a second replica — and the in-process half of
that is what `AlvoEventId` closes, so what remains on `#150` is the cross-process half.

- [x] **Step 7: Delete the spike, and commit**

```bash
git rm -r spike/MMLib.Alvo.Events.Spike
# remove its <Project Path="spike/..."/> line from MMLib.Alvo.slnx
scripts/test-ring0
git add docs/superpowers/specs/evidence/2026-08-03-f3-pr5a-events/spike.txt MMLib.Alvo.slnx
git commit -m "docs(events): record the PR5a de-risking spike's measured verdicts"
```

Expected: ring0 green, and `git status` shows no `spike/` directory. A spike that survives becomes a
second implementation nobody maintains.

---

### Task 2: The envelope in `Abstractions`, and the CloudEvents conformance oracle

**DONE.** Four decisions this task had to make that the plan did not specify, recorded so they are
decisions rather than accidents:

1. **`AlvoEventAttributes.Standard`** joins `Extensions`. The oracle's strongest fact is that Alvo's
   *standard* attribute names are the SDK's own — which is what catches `datacontentype` and every
   other near-miss a hand-written writer can ship — and that fact needs the list to exist on the
   authority rather than in the test.
2. **The default HTML-safe JSON encoder is kept.** `System.Text.Json`'s default escapes `<`, `>`, `&`
   and `+`, so a webhook body and a dashboard rendering of a stored payload are safe by default; the
   visible cost is `+` inside timestamps in the pinned snapshot. The escaping is lossless and a named
   fact says so, so the alternative buys readability only.
3. **`AlvoEventJson.Read` returns JSON's view of a row, not the row's CLR types** — a `uuid` field
   reads back as its text. Named and pinned rather than discovered: the read side's consumer evaluates
   CEL and renders templates over the textual view anyway, and the authoritative typed record lives on
   the write path, where the schema is in scope. A value the *writer* does not recognise is refused
   with the field named, never stringified through `ToString()`.
4. **`AlvoEventData` implements `Equals`/`GetHashCode` by hand.** The compiler-generated equality
   compares `Changed` by reference, so the round-trip fact would have rested on identity.

Step 5's mutation 0 also caught a defect in **this task's own tests**: because the generator's state is
process-wide, the 100 000-mint fact passed under the mutation whenever an earlier test in the class had
already pushed the last millisecond into the future. The facts now derive their starting instant from a
freshly minted id instead of the clock, and the forced-repeat run asserts that all 100 000 ids share
one millisecond as its own non-vacuity control. Recorded because it is the exact failure mode this
project keeps paying for: a fact that passes without reaching the path it names.

**Files:**
- Create: `src/MMLib.Alvo.Abstractions/Events/AlvoEvent.cs`
- Create: `src/MMLib.Alvo.Abstractions/Events/AlvoEventId.cs`
- Create: `src/MMLib.Alvo.Abstractions/Events/AlvoEventAttributes.cs`
- Create: `src/MMLib.Alvo.Abstractions/Events/AlvoEventJson.cs`
- Modify: `Directory.Packages.props`
- Modify: `test/MMLib.Alvo.Tests/MMLib.Alvo.Tests.csproj`
- Test: `test/MMLib.Alvo.Abstractions.Tests/Events/AlvoEventTests.cs`
- Test: `test/MMLib.Alvo.Abstractions.Tests/Events/AlvoEventIdTests.cs`
- Test: `test/MMLib.Alvo.Abstractions.Tests/Events/AlvoEventJsonTests.cs`
- Test: `test/MMLib.Alvo.Tests/Events/CloudEventsConformanceTests.cs`
- Modify: `test/MMLib.Alvo.Abstractions.Tests/PublicApi.MMLib.Alvo.Abstractions.verified.txt`

**Interfaces:**
- Consumes: `MMLib.Alvo.Data.AlvoRecord` (already public in `Abstractions`), `System.Text.Json`.
- Produces:
  ```csharp
  namespace MMLib.Alvo.Events;

  public sealed record AlvoEvent
  {
      public const string SpecVersion = "1.0";
      public const string DataContentType = "application/json";
      public const string DefaultSource = "/alvo";
      public const int CurrentPayloadVersion = 1;

      public required Guid Id { get; init; }
      public required string Source { get; init; }
      public required string Type { get; init; }
      public required DateTimeOffset Time { get; init; }   // refuses a non-UTC offset
      public required string Subject { get; init; }
      public required string PartitionKey { get; init; }
      public required string AuthType { get; init; }
      public required string CorrelationId { get; init; }
      public int PayloadVersion { get; init; } = CurrentPayloadVersion;
      public int ChainDepth { get; init; }
      public string? AuthId { get; init; }
      public string? CausationId { get; init; }
      public required AlvoEventData Data { get; init; }
  }

  public sealed record AlvoEventData
  {
      public AlvoRecord? Record { get; init; }
      public AlvoRecord? OldRecord { get; init; }
      public IReadOnlyList<string> Changed { get; init; } = [];
  }

  public static class AlvoEventAttributes
  {
      public const string SpecVersion = "specversion";
      public const string Id = "id";
      public const string Source = "source";
      public const string Type = "type";
      public const string Time = "time";
      public const string Subject = "subject";
      public const string DataContentType = "datacontenttype";
      public const string PartitionKey = "partitionkey";
      public const string PayloadVersion = "payloadversion";
      public const string ChainDepth = "chaindepth";
      public const string AuthType = "authtype";
      public const string AuthId = "authid";
      public const string CorrelationId = "correlationid";
      public const string CausationId = "causationid";
      public const string Data = "data";

      public static IReadOnlyList<string> Extensions { get; }   // the seven above, in this order
  }

  public static class AlvoEventJson
  {
      public static string Write(AlvoEvent @event);
      public static AlvoEvent Read(string json);
  }

  public static class AlvoEventId
  {
      public static Guid Create();
      public static Guid Create(DateTimeOffset timestamp);
  }
  ```
  `AuthType` values are the three constants `AlvoEventAuthType.ApiKey = "apikey"`,
  `System = "system"`, `Anonymous = "anon"` on a public static class in the same file.

- [x] **Step 1: Write the failing tests**

```csharp
// Abstractions/Events/AlvoEventTests.cs
[Fact]
public void An_event_refuses_a_time_that_is_not_utc()
{
    var refusal = Should.Throw<ArgumentException>(() => Sample() with
    {
        Time = new DateTimeOffset(2026, 8, 3, 12, 0, 0, TimeSpan.FromHours(-2)),
    });

    refusal.Message.ShouldContain("UTC");
}

// StoredInstant is internal to the EF driver, so this is where the same rule is enforceable at
// the envelope's own boundary. Without it, an offset would reach the wire and two engines would
// order the same instant differently (data-path.md's "Every timestamp is one instant").
[Fact]
public void An_event_accepts_a_utc_time()
    => (Sample() with { Time = DateTimeOffset.UtcNow }).Time.Offset.ShouldBe(TimeSpan.Zero);

[Fact]
public void The_payload_version_defaults_to_the_current_one_so_no_producer_can_forget_it()
    => Sample().PayloadVersion.ShouldBe(AlvoEvent.CurrentPayloadVersion);
```

```csharp
// Abstractions/Events/AlvoEventIdTests.cs — D1's amendment. Every number here is spike Q1's.
// Guid.CreateVersion7() inverted 49 839 of 100 000 adjacent pairs; the wrapper measured 0.
[Fact]
public void A_hundred_thousand_successive_ids_sort_in_the_order_they_were_minted()
{
    var ids = Enumerable.Range(0, Samples).Select(_ => AlvoEventId.Create().ToString()).ToList();

    Inversions(ids).ShouldBe(
        0,
        $"ORDER BY id is the outbox queue order, so an inversion is a delivery out of order. "
        + $"Guid.CreateVersion7() alone measured 49 839 over {Samples} (spike Q1).");
}

// The non-vacuity control for the fact above: it proves nothing unless the run really hit the
// repeated-millisecond path, which is the only path the wrapper changes.
[Fact]
public void The_run_really_exercises_the_repeated_millisecond_path()
{
    var ids = Enumerable.Range(0, Samples).Select(_ => AlvoEventId.Create()).ToList();

    MillisecondsOf(ids).Distinct().Count().ShouldBeLessThan(
        Samples / 100,
        "spike Q1 measured 39 distinct millisecond stamps over 100 000 mints; if this run spread "
        + "across a stamp per id, no pair shared a millisecond and the fact above is vacuous");
}

// Deterministic where the loop above is statistical: one fixed timestamp, so the repeated-millisecond
// branch is the only branch taken. Guid.CreateVersion7(fixed) fails this half the time (Q1: 515/999).
[Fact]
public void Two_ids_minted_in_one_millisecond_sort_in_the_order_they_were_minted()
{
    var fixedInstant = new DateTimeOffset(2026, 8, 3, 12, 0, 0, TimeSpan.Zero);

    var first = AlvoEventId.Create(fixedInstant).ToString();
    var second = AlvoEventId.Create(fixedInstant).ToString();

    string.CompareOrdinal(first, second).ShouldBeLessThan(0);
}

// Q1 measured that a backwards clock step reorders the queue by the size of the step. It cannot,
// within one process, because the last emitted millisecond never moves backwards.
[Fact]
public void A_backwards_clock_step_cannot_reorder_ids_within_one_process()
{
    var now = DateTimeOffset.UtcNow;

    var before = AlvoEventId.Create(now).ToString();
    var afterTheClockWentBack = AlvoEventId.Create(now - TimeSpan.FromSeconds(5)).ToString();

    string.CompareOrdinal(before, afterTheClockWentBack).ShouldBeLessThan(0);
}

[Fact]
public void An_id_is_still_a_uuid_version_7_with_the_rfc_variant()
{
    var bytes = AlvoEventId.Create().ToByteArray(bigEndian: true);

    (bytes[6] & 0xF0).ShouldBe(0x70);
    (bytes[8] & 0xC0).ShouldBe(0x80);
}

// Q1: Guid's DEFAULT byte order is not time-sortable (5 050 inversions of 9 999), which is why the
// outbox stores the id as TEXT and never as a BLOB written from ToByteArray().
[Fact]
public void The_text_form_sorts_like_the_big_endian_bytes_and_not_like_the_default_ones()
{
    var ids = Enumerable.Range(0, 1_000).Select(_ => AlvoEventId.Create()).ToList();

    Inversions([.. ids.Select(id => Convert.ToHexString(id.ToByteArray(bigEndian: true)))]).ShouldBe(0);
    Inversions([.. ids.Select(id => Convert.ToHexString(id.ToByteArray()))]).ShouldBeGreaterThan(0);
}

private const int Samples = 100_000;
```

`Inversions` counts adjacent pairs where `string.CompareOrdinal(previous, next) >= 0`;
`MillisecondsOf` reads the 48-bit stamp out of each id's big-endian bytes. Both are private helpers in
the test class, mirroring the spike's own two loops so the numbers quoted above stay comparable.

```csharp
// Abstractions/Events/AlvoEventJsonTests.cs
[Fact]
public void Extensions_are_flat_top_level_members_never_a_nested_object()
{
    using var document = JsonDocument.Parse(AlvoEventJson.Write(Sample()));
    var root = document.RootElement;

    root.TryGetProperty("extensions", out _).ShouldBeFalse(
        "CloudEvents v1.0.2:439-440 serializes extensions like standard attributes; a nested "
        + "wrapper is non-conformant");
    foreach (var extension in AlvoEventAttributes.Extensions.Where(NotOptional))
    {
        root.TryGetProperty(extension, out _).ShouldBeTrue(extension);
    }
}

// The seven-type system has no map or array (spec v1.0.2:179-217), so these three cannot be
// context attributes at all — which is the single most-repeated defect in the base design's
// envelope (:546-558).
[Theory]
[InlineData("record")]
[InlineData("old_record")]
[InlineData("changed")]
public void The_row_images_and_the_changed_list_live_inside_data(string member)
{
    using var document = JsonDocument.Parse(AlvoEventJson.Write(Sample()));

    document.RootElement.TryGetProperty(member, out _).ShouldBeFalse();
    document.RootElement.GetProperty(AlvoEventAttributes.Data)
        .TryGetProperty(member, out _).ShouldBeTrue();
}

[Fact]
public void The_wire_specversion_is_1_0_not_1_0_2()
{
    using var document = JsonDocument.Parse(AlvoEventJson.Write(Sample()));
    document.RootElement.GetProperty(AlvoEventAttributes.SpecVersion).GetString().ShouldBe("1.0");
}

[Fact]
public void An_envelope_round_trips_through_write_and_read()
    => AlvoEventJson.Read(AlvoEventJson.Write(Sample())).ShouldBe(Sample());

// An absent optional attribute is absent, not null: CloudEvents forbids an attribute present with
// no value, and a consumer switching on presence must not see `causationid: null`.
[Fact]
public void An_absent_optional_attribute_is_omitted_rather_than_written_as_null()
{
    using var document = JsonDocument.Parse(
        AlvoEventJson.Write(Sample() with { CausationId = null, AuthId = null }));

    document.RootElement.TryGetProperty(AlvoEventAttributes.CausationId, out _).ShouldBeFalse();
    document.RootElement.TryGetProperty(AlvoEventAttributes.AuthId, out _).ShouldBeFalse();
}
```

```csharp
// MMLib.Alvo.Tests/Events/CloudEventsConformanceTests.cs — the ORACLE, test-only by design (D3)
[Fact]
public void Every_extension_name_is_one_the_cloudevents_sdk_itself_accepts()
{
    foreach (var name in AlvoEventAttributes.Extensions)
    {
        Should.NotThrow(
            () => CloudEventAttribute.CreateExtension(name, CloudEventAttributeType.String),
            $"'{name}' must match [a-z0-9]+ (spec v1.0.2:173-175)");
    }
}

// The oracle's own non-vacuity control: it must reject the three names the BASE DESIGN proposed,
// or a green run above would prove only that the SDK was called.
[Theory]
[InlineData("payload_version")]
[InlineData("chain-depth")]
[InlineData("old_record")]
public void The_oracle_really_rejects_the_names_the_base_design_proposed(string illegal)
    => Should.Throw<Exception>(
        () => CloudEventAttribute.CreateExtension(illegal, CloudEventAttributeType.String));

[Fact]
public void Every_extension_name_stays_within_the_specs_twenty_character_advisory()
    => AlvoEventAttributes.Extensions.ShouldAllBe(name => name.Length <= 20);

[Fact]
public Task One_real_envelope_is_pinned_verbatim()
    => Verify(AlvoEventJson.Write(SampleEnvelopeWithAFixedClockAndFixedIds()));
```

- [x] **Step 2: Run to verify they fail**

Run: `dotnet test --project test/MMLib.Alvo.Abstractions.Tests -- --filter-namespace 'MMLib.Alvo.Abstractions.Tests.Events'`
Expected: FAIL — `AlvoEvent` does not exist.

- [x] **Step 3: Add the test-only package, then implement**

```xml
<!-- Directory.Packages.props — test-only: the CloudEvents conformance oracle. Apache-2.0.
     NOT a shipped dependency: Abstractions may take no new external dependency
     (package-boundary.md:96-103), and nothing in the core needs the SDK at run time because Alvo
     serializes its own envelope for the outbox row and for webhook delivery. -->
<PackageVersion Include="CloudNative.CloudEvents" Version="2.9.0" />
```

`AlvoEvent`'s `Time` guard is an `init` accessor over a backing field, on `AlvoContext.Roles`'
precedent:

```csharp
private readonly DateTimeOffset _time;

/// <summary>Gets the instant the change committed, always UTC (CloudEvents <c>time</c>).</summary>
/// <remarks>
/// An offset is a spelling of a timestamp, never part of its value
/// (<c>docs/architecture/data-path.md</c>, <em>Every timestamp is one instant</em>). The driver
/// normalises through its own <c>StoredInstant</c> before constructing an event; this guard is the
/// same rule at the envelope's boundary, where that helper is not reachable.
/// </remarks>
public required DateTimeOffset Time
{
    get => _time;
    init => _time = value.Offset == TimeSpan.Zero
        ? value
        : throw new ArgumentException(
            "An event's time must be UTC; convert with ToUniversalTime() before constructing the "
            + "event. An offset is a spelling of a timestamp, never part of its value.",
            nameof(Time));
}
```

`AlvoEventJson.Write` is a `Utf8JsonWriter` over a pooled buffer, one private method per section
(`WriteRequiredAttributes`, `WriteExtensions`, `WriteData`, `WriteRecord`) so no method passes ~25
lines. Attribute names come from `AlvoEventAttributes` — never a literal — so the wire names have one
authority. Inside `data`, the keys are `record`, `old_record`, `changed`; a record is written as its
`Values` dictionary, and each value through a small `WriteValue` switch over the CLR types
`AlvoRecord` can hold (`string`, `Guid`, `bool`, the integral and floating types, `decimal`,
`DateTimeOffset` as `"O"`, `DateOnly`, `null`). `Read` is the mirror over `JsonDocument`.

The `AuthType`/`AuthId` and `CorrelationId`/`CausationId` XML docs must state their provenance
verbatim, or a reader checking the v1.0.2 registry concludes the names were invented:

```csharp
/// <summary>
/// Gets how the caller authenticated — <see cref="AlvoEventAuthType"/>'s three values. The
/// CloudEvents <c>authtype</c> extension attribute.
/// </summary>
/// <remarks>
/// <b>Provenance.</b> <c>authtype</c>/<c>authid</c> are the community's Auth Context extension
/// names, and they are <em>not</em> in the v1.0.2 registry — that lists exactly five known
/// extensions (Dataref, Distributed Tracing, Partitioning, Sampling, Sequence). They live in
/// <c>cloudevents/extensions/authcontext.md</c> on <c>main</c> (post-1.0.2). They are adopted
/// anyway, because they are the community's names, they satisfy the naming rule, and inventing
/// <c>actor</c> would be worse. Alvo needs the distinction they carry: §3.3's "as system / as the
/// originator" cannot be expressed by one opaque actor string.
/// </remarks>
```

`PartitionKey`'s docs state the opposite provenance — `partitionkey` **is** registered
(Partitioning) — and that the outbox column carries the same value under the same name so the two
cannot drift. `PayloadVersion`'s docs record deviation 69: it duplicates what the spec assigns to
`type` + `dataschema`, kept because an in-process subscriber switching on an integer is cheaper than
parsing a URI, and recorded here rather than discovered by whoever notices the two can disagree.
Add a `<remarks>` paragraph on `AlvoEvent` itself naming the 64 KB forwarding rule and **#151**.

`AlvoEventId` is the monotonic generator D1's amendment adds. It takes the BCL's own v7 mint as its
candidate — so the timestamp packing, the version and the variant bits stay the BCL's business — and
fixes only the ordering:

```csharp
public static Guid Create() => Create(DateTimeOffset.UtcNow);

public static Guid Create(DateTimeOffset timestamp)
{
    Span<byte> candidate = stackalloc byte[GuidByteCount];
    Guid.CreateVersion7(timestamp).TryWriteBytes(candidate, bigEndian: true, out _);

    lock (_gate)
    {
        return NextInOrder(candidate);
    }
}

private static Guid NextInOrder(ReadOnlySpan<byte> candidate)
{
    var milliseconds = MillisecondsOf(candidate);

    if (milliseconds > _lastMilliseconds)
    {
        return Remember(milliseconds, TailOf(candidate));
    }

    return _lastTail < TailCeiling
        ? Remember(_lastMilliseconds, _lastTail + UInt128.One)
        : Remember(_lastMilliseconds + 1, TailOf(candidate));
}
```

`Remember` stores the pair and composes the id; `MillisecondsOf`/`TailOf` read the 48-bit stamp and
the **74-bit** tail (the four low bits of byte 6, byte 7, the six low bits of byte 8, bytes 9–15 —
i.e. everything that is neither the timestamp nor the version nor the variant nibble); `Compose`
writes them back, so incrementing can never corrupt the version or the variant. The saturation arm
exists because a 74-bit counter cannot overflow in practice but *can* be reasoned about wrongly if
the branch is missing.

Its XML docs carry three things a later reader would otherwise re-litigate: the measured reason it
exists (`spike.txt` Q1 — 49.9 % of same-millisecond pairs invert without it, 0 with it); that
`Create(DateTimeOffset)` returns an id whose embedded millisecond is the **later** of the requested
one and the last already minted, because a total order outranks an exact stamp, and that the emit
sites pass the write's own audit instant so `time`, `created_at` and the id agree; and that the
guarantee is **in-process** — two processes minting inside one millisecond still interleave (#150).

- [x] **Step 4: Run to verify they pass**

Run:
```
dotnet test --project test/MMLib.Alvo.Abstractions.Tests -- --filter-namespace 'MMLib.Alvo.Abstractions.Tests.Events'
dotnet test --project test/MMLib.Alvo.Tests -- --filter-class '*CloudEventsConformanceTests*'
```
Expected: PASS, and the Verify snapshot accepted. Confirm `Build succeeded` first.

- [x] **Step 5: Prove the conformance facts discriminate**

Four mutations, each restored immediately:

0. **Replace `AlvoEventId.Create`'s body with `Guid.CreateVersion7(timestamp)`** — the exact code D1
   originally specified. `A_hundred_thousand_successive_ids_sort_in_the_order_they_were_minted` must
   go **red**, and `Two_ids_minted_in_one_millisecond_sort_in_the_order_they_were_minted` must go red
   or flake, which is the point: this is the mutation that proves the wrapper is doing work rather
   than wrapping.

1. Rename `AlvoEventAttributes.PayloadVersion` from `"payloadversion"` to `"payload_version"` →
   `Every_extension_name_is_one_the_cloudevents_sdk_itself_accepts` **and** the Verify snapshot go
   red. Without this, the oracle would prove only that the SDK is callable.
2. Move `Changed` out of `data` into a top-level `changed` array →
   `The_row_images_and_the_changed_list_live_inside_data` goes red for that case.
3. Write `causationid: null` instead of omitting it →
   `An_absent_optional_attribute_is_omitted_rather_than_written_as_null` goes red.

Confirm each edit landed with `command grep -c` (not bare `grep` — it is `ugrep` here and miscounts
CRLF).

- [x] **Step 6: Accept the public-API baseline, ring0, commit**

```bash
dotnet test --project test/MMLib.Alvo.Abstractions.Tests -- --filter-class '*PublicApi*'
# accept the moved baseline with the repo's usual mechanism; never hand-edit it.
# The Stop hook will require alvo-snapshot-judge — dispatch it.
scripts/test-ring0
git add src/MMLib.Alvo.Abstractions/Events/ Directory.Packages.props \
        test/MMLib.Alvo.Abstractions.Tests/ test/MMLib.Alvo.Tests/ \
        test/MMLib.Alvo.Abstractions.Tests/PublicApi.MMLib.Alvo.Abstractions.verified.txt
git commit -m "feat(events): add a CloudEvents 1.0.2 envelope, hand-written in Abstractions"
```

---

### Task 3: `alvo_outbox` — the table, its DDL, and the insert that rides the caller's transaction

**Files:**
- Create: `src/MMLib.Alvo.Data.EntityFrameworkCore/Internal/OutboxTable.cs`
- Modify: `src/MMLib.Alvo.Data.EntityFrameworkCore/Internal/SystemSchemaInitializer.cs:26-91`
- Modify: `test/MMLib.Alvo.Data.EntityFrameworkCore.Tests/ChangeTrackerReachTests.cs:177-189`
- Test: `test/MMLib.Alvo.Data.EntityFrameworkCore.Tests/OutboxTableTests.cs`
- Test: `test/MMLib.Alvo.Data.Sqlite.Tests/SqliteOutboxTableTests.cs`
- Test: `test/MMLib.Alvo.Data.PostgreSql.Tests.Integration/PostgreSqlOutboxTableTests.cs`

**Interfaces:**
- Consumes: `AlvoEvent`/`AlvoEventJson` (Task 2); `RelationalSqlBatch.AddParameter`;
  `StoredInstant.Text`; `IdempotencyTable`'s shape.
- Produces:
  ```csharp
  internal static class OutboxTable
  {
      internal static string NameFor(string schemaPrefix);         // "{prefix}_outbox"
      internal static string Ddl(string tableName);
      internal static Task EnsureAsync(DbConnection connection, string tableName, CancellationToken ct);

      internal static Task InsertAsync(
          DbConnection connection, DbTransaction transaction, string tableName,
          AlvoEvent @event, CancellationToken ct);

      internal static Task<IReadOnlyList<OutboxEntry>> ClaimAsync(
          DbConnection connection, string tableName, string claimant, int batchSize,
          int maxAttempts, DateTimeOffset now, TimeSpan lease, CancellationToken ct);

      internal static Task MarkDispatchedAsync(
          DbConnection connection, string tableName, Guid id, DateTimeOffset now, CancellationToken ct);

      internal static Task ReleaseAsync(
          DbConnection connection, string tableName, Guid id, CancellationToken ct);
  }
  ```
  `OutboxEntry` is the port's own record, defined in Task 5 and referenced here — implement Task 3's
  `ClaimAsync`/`MarkDispatchedAsync`/`ReleaseAsync` bodies in **Task 5**; this task ships `NameFor`,
  `Ddl`, `EnsureAsync` and `InsertAsync` only, so the table and the write side land as one reviewable
  unit.

- [ ] **Step 1: Write the failing tests**

```csharp
// EntityFrameworkCore.Tests/OutboxTableTests.cs — DDL shape, no database
[Fact]
public void The_ddl_is_identical_ansi_portable_with_no_per_engine_branching()
{
    var ddl = OutboxTable.Ddl("alvo_outbox");

    ddl.ShouldContain("CREATE TABLE IF NOT EXISTS alvo_outbox");
    foreach (var perEngine in new[] { "AUTOINCREMENT", "IDENTITY", "SERIAL", "nextval", "bigserial" })
    {
        ddl.ShouldNotContain(
            perEngine,
            Case.Insensitive,
            "SystemSchemaInitializer's stated invariant is identical ANSI-portable DDL on both "
            + "engines with no per-engine branching (:15-17); the ordering key is a UUIDv7 id "
            + "instead (plan decision D1, spike Q1/Q6). SERIAL is in this list even though SQLite "
            + "ACCEPTS it: SQLite parses it as an unrecognised column type and gives a nullable "
            + "column that never increments, so it would pass CI and lose ordering in production");
    }
}

// R2: a high-water mark on a monotonic integer silently drops a row, because PostgreSQL sequences
// commit out of order. There is no such column to be tempted by — asserted, not intended.
[Fact]
public void There_is_no_sequence_column()
    => OutboxTable.Ddl("alvo_outbox").ShouldNotContain("sequence", Case.Insensitive);

[Fact]
public void Partition_key_is_written_from_the_first_migration_even_though_nothing_reads_it_in_f3()
    => OutboxTable.Ddl("alvo_outbox").ShouldContain("partition_key TEXT NOT NULL");

[Fact]
public void The_outbox_is_one_of_the_framework_tables_the_introspector_excludes()
    => SystemSchemaInitializer.FrameworkTableNames("alvo").ShouldContain("alvo_outbox");
```

```csharp
// Data.Sqlite.Tests/SqliteOutboxTableTests.cs — and its byte-identical PostgreSQL twin
[Fact]
public async Task An_inserted_event_round_trips_through_the_production_writer_and_reader()
{
    await using var world = await OutboxWorld.StartAsync();
    var @event = SampleEvent();

    await world.InsertAsync(@event);

    var stored = await world.ReadPayloadAsync(@event.Id);
    AlvoEventJson.Read(stored).ShouldBe(@event);
}

// The insert must ride the CALLER's transaction, so a rollback leaves nothing. This is the whole
// point of the seam (data-path.md:1481-1487) and it is asserted before any emit site exists.
[Fact]
public async Task An_insert_on_a_rolled_back_transaction_leaves_no_row()
{
    await using var world = await OutboxWorld.StartAsync();
    var @event = SampleEvent();

    await world.InsertAndRollBackAsync(@event);

    (await world.CountAsync()).ShouldBe(0);
}

[Fact]
public async Task Every_timestamp_is_stored_in_the_frameworks_own_round_trip_text_form()
{
    await using var world = await OutboxWorld.StartAsync();
    var @event = SampleEvent() with { Time = new DateTimeOffset(2026, 8, 3, 9, 30, 0, TimeSpan.Zero) };

    await world.InsertAsync(@event);

    (await world.ReadCreatedAtTextAsync(@event.Id)).ShouldBe("2026-08-03T09:30:00.0000000+00:00");
}

[Fact]
public async Task A_second_ensure_is_a_no_op_so_the_ddl_is_safe_to_run_on_every_boot()
{
    await using var world = await OutboxWorld.StartAsync();
    await world.EnsureAsync();
    await world.EnsureAsync();

    (await world.CountAsync()).ShouldBe(0);
}
```

`OutboxWorld` is a small fixture in each driver's test project wrapping an open connection, the
prefix `"alvo"`, and the four helpers above — the same shape
`SqliteDescriptorVersionStoreTests`/`PostgreSqlDescriptorVersionStoreTests` already use.

- [ ] **Step 2: Run to verify they fail**

Run:
```
dotnet test --project test/MMLib.Alvo.Data.EntityFrameworkCore.Tests -- --filter-class '*OutboxTableTests*'
dotnet test --project test/MMLib.Alvo.Data.Sqlite.Tests -- --filter-class '*SqliteOutboxTableTests*'
```
Expected: FAIL — `OutboxTable` does not exist.

- [ ] **Step 3: Implement `OutboxTable`, mirroring `IdempotencyTable`**

```csharp
internal static string NameFor(string schemaPrefix) => $"{schemaPrefix}_outbox";

internal static string Ddl(string tableName) =>
    $"""
    CREATE TABLE IF NOT EXISTS {tableName} (
        id TEXT NOT NULL PRIMARY KEY,
        event_type TEXT NOT NULL,
        partition_key TEXT NOT NULL,
        payload TEXT NOT NULL,
        created_at TEXT NOT NULL,
        claimed_at TEXT NULL,
        claimed_by TEXT NULL,
        attempts INTEGER NOT NULL,
        dispatched_at TEXT NULL
    )
    """;
```

The type-level `<remarks>` must carry four paragraphs, each of which is a decision a later reader
would otherwise re-litigate:

- **Why `id` is the ordering key and there is no `sequence`.** A UUIDv7 is time-ordered in its high
  48 bits and its stored text sorts lexicographically in time order on both engines, so the primary
  key *is* the queue order — with identical ANSI DDL, honouring `SystemSchemaInitializer`'s stated
  invariant (`:15-17`), which an `AUTOINCREMENT`/`IDENTITY` column would break with zero precedent in
  this repository (each engine refuses the other's spelling — measured, Q6). Two consequences of the
  same measurement belong in this paragraph, because both are invisible from the DDL: the id is
  minted by **`AlvoEventId.Create`**, never `Guid.CreateVersion7()`, since the latter inverts 49.9 %
  of same-millisecond pairs (Q1); and the column is `TEXT` rather than a `BLOB`, since `Guid`'s
  default byte order is not time-sortable (Q1). Measured: `spike.txt` Q1, Q2, Q6, Q7.
- **Why `SERIAL` is in the forbidden list even though SQLite does not reject it.** SQLite **accepts**
  `seq SERIAL` as an unrecognised column type and silently gives a nullable column that never
  increments (Q6). A "portable `SERIAL`" therefore passes every SQLite test in CI and loses ordering
  in production, which is why the absence is asserted rather than assumed.
- **Why there is no high-water mark, ever.** PostgreSQL sequences commit out of order — a
  transaction can take 100 and commit after another took 101 and committed — so "processed up to N"
  drops a row silently. The claim filters `dispatched_at IS NULL`. Having no monotonic integer at all
  is what makes the wrong use unavailable rather than merely discouraged.
- **Why `partition_key` exists with no reader in F3.** F7's partitioned claim becomes additive
  instead of a migration of a shipped table, and the column is named after the **registered**
  CloudEvents `partitionkey` attribute so the column and the attribute cannot drift.
- **Why provenance is not duplicated into columns.** The base design's `actor`,
  `correlation_id` and `provenance_depth` (`:557-561`) would be a second authority for values the
  envelope already carries; only `partition_key` is duplicated, and only because F7 must index it.

`InsertAsync` is `IdempotencyTable.InsertAsync`'s shape exactly — `command.Transaction =
transaction`, `RelationalSqlBatch.AddParameter` per value, `StoredInstant.Text(@event.Time)` for
`created_at`, `attempts` seeded to `0`, `claimed_at`/`claimed_by`/`dispatched_at` left `NULL`.
`EnsureAsync` matches `IdempotencyTable.EnsureAsync`: outside any transaction, so a caller's
ensure-once memo is honest.

Then extend `SystemSchemaInitializer`:

```csharp
private readonly string _outboxTableName;   // ctor: OutboxTable.NameFor(schemaPrefix)

public static IReadOnlyList<string> FrameworkTableNames(string schemaPrefix) =>
    [DescriptorVersionsTableName(schemaPrefix), IdempotencyTable.NameFor(schemaPrefix),
     OutboxTable.NameFor(schemaPrefix)];

// inside EnsureAsync, after the idempotency table:
await CreateIfMissingAsync(_outboxTableName, OutboxTable.Ddl(_outboxTableName), ct).ConfigureAwait(false);
```

Update the type's summary — it names *"the append-only descriptor-versions table and the
idempotency-record table"* — to name three, and add one paragraph recording that
`package-boundary.md:152-155` predicted this exact moment (*"A port is earned the moment a driver's
system schema grows a table no store call touches — PR5's outbox is the first candidate"*) and that
Task 5 pays it with `IOutboxStore`.

Add both new files to the allow-list:

```csharp
private static readonly string[] _sqlComposingFiles =
[
    "EfAlvoData.cs",
    "EfCoreDescriptorVersionStore.cs",
    "EfCoreOutboxStore.cs",
    "EfCoreRuntimeSchemaWriter.cs",
    "EfCoreSchemaMigrator.cs",
    "IdempotencyTable.cs",
    "OutboxTable.cs",
    "PredicateParameterBinder.cs",
    "RelationalSqlBatch.cs",
    "SqliteCaseSensitiveLike.cs",
    "SystemSchemaInitializer.cs",
    "VersionRowWriter.cs",
];
```

and extend that member's `<remarks>` sentence about what each file earns: `OutboxTable` reads and
writes only the outbox table and touches no entity table, exactly as `IdempotencyTable` does.

- [ ] **Step 4: Run to verify they pass**

Run:
```
dotnet test --project test/MMLib.Alvo.Data.EntityFrameworkCore.Tests -- --filter-class '*OutboxTableTests*'
dotnet test --project test/MMLib.Alvo.Data.EntityFrameworkCore.Tests -- --filter-class '*ChangeTrackerReachTests*'
dotnet test --project test/MMLib.Alvo.Data.Sqlite.Tests -- --filter-class '*SqliteOutboxTableTests*'
dotnet test --project test/MMLib.Alvo.Data.PostgreSql.Tests.Integration -- --filter-class '*PostgreSqlOutboxTableTests*'
```
Expected: PASS on both engines. Assert `Build succeeded` first.

- [ ] **Step 5: Prove the re-apply fact discriminates**

The DoD's *"a second apply produces an empty plan"* is the fact that would most plausibly pass
vacuously. Write it against the real migrator and then mutate:

```csharp
// Data.Sqlite.Tests/SqliteOutboxTableTests.cs
[Fact]
public async Task A_second_apply_plans_nothing_for_the_outbox_table()
{
    await using var world = await AlvoDataWorlds.VehicleRegistryAsync();
    await world.ApplyAsync();

    var plan = await world.PlanAsync();

    plan.Steps.ShouldBeEmpty(
        "the introspector must exclude alvo_outbox via SystemSchemaInitializer.FrameworkTableNames "
        + "(:67); otherwise the next re-apply plans a DROP for it, silently, and the symptom is a "
        + "lost event history rather than an error");
}
```

Mutation: remove `OutboxTable.NameFor(schemaPrefix)` from `FrameworkTableNames` and confirm this
fact goes **red** with a `DROP TABLE alvo_outbox` step in the plan. Restore. Without the mutation,
the fact passes on any table the introspector happens not to see.

- [ ] **Step 6: ring0 + commit**

```bash
scripts/test-ring0
git add src/MMLib.Alvo.Data.EntityFrameworkCore/Internal/OutboxTable.cs \
        src/MMLib.Alvo.Data.EntityFrameworkCore/Internal/SystemSchemaInitializer.cs \
        test/MMLib.Alvo.Data.EntityFrameworkCore.Tests/ \
        test/MMLib.Alvo.Data.Sqlite.Tests/SqliteOutboxTableTests.cs \
        test/MMLib.Alvo.Data.PostgreSql.Tests.Integration/PostgreSqlOutboxTableTests.cs
git commit -m "feat(events): add the alvo_outbox framework table and its transactional insert"
```

---

### Task 4: The four write sites emit, inside their own transactions

The trap this task exists to avoid is named in `data-path.md:1486-1493`: the idiomatic EF place to
hang an outbox is a `SaveChangesInterceptor`, and on this data path it would **never fire for an
update or a delete** — the two operations that most need an event — because `ExecuteUpdate` and
`ExecuteDelete` do not go through the change tracker. So the emit is sequenced explicitly on the
transaction, at each of the four sites, and the tests cover update and delete first.

**Files:**
- Create: `src/MMLib.Alvo.Data.EntityFrameworkCore/Internal/OutboxEventFactory.cs`
- Modify: `src/MMLib.Alvo.Data.EntityFrameworkCore/Internal/EfAlvoData.cs` (sites `177/179`,
  `321/330`, `582/586`, `620/622`; plus `InsertAsync`, `WriteAsync`, `EraseAsync`,
  `RecordedCreateAsync`)
- Test: `test/MMLib.Alvo.Data.EntityFrameworkCore.Tests/OutboxEventFactoryTests.cs`
- Test: `src/MMLib.Alvo.Testing/Data/AlvoDataOutboxTests.cs` (the shared, per-engine suite)
- Test: `test/MMLib.Alvo.Data.Sqlite.Tests/SqliteAlvoDataOutboxTests.cs`
- Test: `test/MMLib.Alvo.Data.PostgreSql.Tests.Integration/PostgreSqlAlvoDataOutboxTests.cs`
- Modify: `test/_shared/PublicApi.MMLib.Alvo.Testing.verified.txt`

**Interfaces:**
- Consumes: `AlvoEvent`, `AlvoEventJson`, `AlvoEventAttributes`, `AlvoEventAuthType` (Task 2);
  `OutboxTable.InsertAsync` (Task 3); `EntitySchema`, `AlvoContext`, `AlvoRecord`,
  `AlvoManagedColumns`, `StoredInstant`.
- Produces:
  ```csharp
  internal static class OutboxEventFactory
  {
      internal static AlvoEvent For(
          EntitySchema entity,
          OutboxOperation operation,
          AlvoContext context,
          DateTimeOffset now,
          AlvoRecord? postImage,
          AlvoRecord? preImage);

      internal static string PartitionKeyFor(string entity, Guid rowId);   // "vehicles:<guid>"
      internal static IReadOnlyList<string> ChangedFields(AlvoRecord? postImage, AlvoRecord? preImage);
  }

  internal enum OutboxOperation { Created, Updated, Deleted }
  ```
  `public abstract class MMLib.Alvo.Testing.Data.AlvoDataOutboxTests` with
  `protected abstract Task<IAlvoDataOutboxWorld> WorldAsync();` — the same shape
  `AlvoDataAdversarialTests` already uses, so both engines inherit one suite.

- [x] **Step 1: Write the failing factory tests**

```csharp
// EntityFrameworkCore.Tests/OutboxEventFactoryTests.cs — pure, no database
[Theory]
[InlineData(OutboxOperation.Created, "entity.vehicles.created")]
[InlineData(OutboxOperation.Updated, "entity.vehicles.updated")]
[InlineData(OutboxOperation.Deleted, "entity.vehicles.deleted")]
public void The_event_type_matches_the_frozen_event_pattern_grammar(
    OutboxOperation operation, string expected)
{
    var @event = Subject(operation);

    @event.Type.ShouldBe(expected);
    // schema/project.schema.json:409-419 — a type no eventPattern can name is a type no rule
    // could ever subscribe to.
    EventPatternRegex().IsMatch(@event.Type).ShouldBeTrue(@event.Type);
}

[Fact]
public void The_partition_key_carries_the_entity_so_two_entities_cannot_collide_on_one_row_id()
{
    var id = Guid.CreateVersion7();

    OutboxEventFactory.PartitionKeyFor("vehicles", id)
        .ShouldNotBe(OutboxEventFactory.PartitionKeyFor("owners", id));
}

// changed(field) must be cheap for the dispatcher, which is why data carries the list rather than
// recomputing it per subscription (base design :557-558).
[Fact]
public void Changed_names_only_the_fields_whose_value_really_moved()
{
    var before = Record(("make", "vw"), ("color", "red"));
    var after = Record(("make", "vw"), ("color", "blue"));

    OutboxEventFactory.ChangedFields(after, before).ShouldBe(["color"]);
}

[Fact]
public void A_create_carries_no_old_record_and_names_every_field_as_changed()
{
    var @event = Subject(OutboxOperation.Created);

    @event.Data.OldRecord.ShouldBeNull();
    @event.Data.Changed.ShouldBe(@event.Data.Record!.Values.Keys, ignoreOrder: true);
}

[Fact]
public void A_delete_carries_the_pre_image_and_no_record()
{
    var @event = Subject(OutboxOperation.Deleted);

    @event.Data.Record.ShouldBeNull();
    @event.Data.OldRecord.ShouldNotBeNull();
}

[Theory]
[InlineData("anon")]
[InlineData("system")]
[InlineData("apikey")]
public void The_auth_type_distinguishes_the_system_from_the_originator(string expected)
    => OutboxEventFactory.For(Vehicles, OutboxOperation.Created, ContextFor(expected), Now, Post, null)
        .AuthType.ShouldBe(expected);

// §3.3 needs "as system / as the originator" to be readable off the envelope; an anonymous caller
// must not be reported as an identified one.
[Fact]
public void An_anonymous_caller_discloses_no_auth_id()
    => OutboxEventFactory.For(Vehicles, OutboxOperation.Created, AlvoContext.Anonymous, Now, Post, null)
        .AuthId.ShouldBeNull();

[Fact]
public void The_events_time_is_the_writes_own_instant_never_a_second_clock_read()
{
    var stamped = new DateTimeOffset(2026, 8, 3, 9, 30, 0, TimeSpan.Zero);

    OutboxEventFactory.For(Vehicles, OutboxOperation.Created, Admin, stamped, Post, null)
        .Time.ShouldBe(stamped);
}
```

- [x] **Step 2: Write the failing per-engine suite — update and delete FIRST**

```csharp
// src/MMLib.Alvo.Testing/Data/AlvoDataOutboxTests.cs
/// <summary>
/// The outbox emits on every one of the port's three write faces, inside the write's own
/// transaction.
/// </summary>
/// <remarks>
/// <para>
/// <b>Update and delete come first in this file on purpose.</b> The idiomatic EF place to hang an
/// outbox is a <c>SaveChangesInterceptor</c>, and on this data path it fires for <em>neither</em>:
/// writes run as <c>ExecuteUpdate</c>/<c>ExecuteDelete</c> over the policy-carrying root, which
/// never touches the change tracker (<c>docs/architecture/data-path.md</c>). A create-only suite
/// would pass over exactly that mistake.
/// </para>
/// </remarks>
public abstract class AlvoDataOutboxTests
{
    [Fact]
    public async Task An_update_emits_exactly_one_event_carrying_both_images()
    {
        await using var world = await WorldAsync();
        var created = await world.CreateVehicleAsync(make: "vw");
        await world.ClearOutboxAsync();

        await world.UpdateVehicleAsync(created.Id, color: "blue");

        var events = await world.EventsAsync();
        var updated = events.ShouldHaveSingleItem();
        updated.Type.ShouldBe("entity.vehicles.updated");
        updated.Data.OldRecord.ShouldNotBeNull();
        updated.Data.Record!["color"].ShouldBe("blue");
        updated.Data.Changed.ShouldContain("color");
    }

    [Fact]
    public async Task A_delete_emits_exactly_one_event_carrying_the_pre_image()
    {
        await using var world = await WorldAsync();
        var created = await world.CreateVehicleAsync(make: "vw");
        await world.ClearOutboxAsync();

        await world.DeleteVehicleAsync(created.Id);

        var deleted = (await world.EventsAsync()).ShouldHaveSingleItem();
        deleted.Type.ShouldBe("entity.vehicles.deleted");
        deleted.Data.OldRecord!["make"].ShouldBe("vw");
        deleted.Data.Record.ShouldBeNull();
    }

    [Fact]
    public async Task A_create_emits_exactly_one_event()
    {
        await using var world = await WorldAsync();

        await world.CreateVehicleAsync(make: "vw");

        (await world.EventsAsync()).ShouldHaveSingleItem().Type.ShouldBe("entity.vehicles.created");
    }

    /// <summary>
    /// The atomicity claim itself: the row and its event commit together or not at all.
    /// </summary>
    /// <remarks>
    /// Forced through a write the engine itself refuses <em>after</em> the outbox insert has run —
    /// a duplicate on the entity's own unique index — so the rollback is the production path's,
    /// not a test-only rollback of a transaction the production code never opened.
    /// </remarks>
    [Fact]
    public async Task A_write_the_engine_refuses_leaves_no_outbox_row()
    {
        await using var world = await WorldAsync();
        await world.CreateVehicleAsync(vin: "TAKEN");
        await world.ClearOutboxAsync();

        await Should.ThrowAsync<AlvoConstraintViolationException>(
            () => world.CreateVehicleAsync(vin: "TAKEN"));

        (await world.EventsAsync()).ShouldBeEmpty();
    }

    [Fact]
    public async Task A_denied_write_emits_nothing()
    {
        await using var world = await WorldAsync();

        await Should.ThrowAsync<AlvoAuthorizationException>(() => world.CreateVehicleAsAnonymousAsync());

        (await world.EventsAsync()).ShouldBeEmpty();
    }

    /// <summary>
    /// A replayed idempotent create wrote no row, so it must emit no second event — or every
    /// client retry would fan out one more time through every subscription.
    /// </summary>
    [Fact]
    public async Task A_replayed_idempotent_create_emits_no_second_event()
    {
        await using var world = await WorldAsync();
        var token = new AlvoIdempotency("k-1", "fingerprint");

        await world.CreateVehicleAsync(vin: "ONCE", idempotency: token);
        await world.CreateVehicleAsync(vin: "ONCE", idempotency: token);

        (await world.EventsAsync()).Count.ShouldBe(1);
    }

    /// <summary>
    /// The envelope's <c>time</c> is the same instant the audit stamp recorded — one write, one
    /// instant (<c>docs/architecture/data-path.md</c>, <em>Every timestamp is one instant</em>).
    /// </summary>
    [Fact]
    public async Task The_events_time_equals_the_rows_own_audit_instant()
    {
        await using var world = await WorldAsync();

        var created = await world.CreateVehicleAsync(make: "vw");

        var @event = (await world.EventsAsync()).ShouldHaveSingleItem();
        @event.Time.ShouldBe((DateTimeOffset)created.Values[AlvoManagedColumns.CreatedAt]!);
    }
}
```

`IAlvoDataOutboxWorld` is the fixture seam (`CreateVehicleAsync`, `UpdateVehicleAsync`,
`DeleteVehicleAsync`, `CreateVehicleAsAnonymousAsync`, `EventsAsync`, `ClearOutboxAsync`), added
beside `IStatementProbe`/`IDifferentialProbe` in `src/MMLib.Alvo.Testing/Data/`. `EventsAsync`
reads the raw `payload` column and returns `AlvoEventJson.Read` of each, ordered by `id` — never a
second copy of the serializer.

- [x] **Step 3: Run to verify they fail**

Run:
```
dotnet test --project test/MMLib.Alvo.Data.EntityFrameworkCore.Tests -- --filter-class '*OutboxEventFactoryTests*'
dotnet test --project test/MMLib.Alvo.Data.Sqlite.Tests -- --filter-class '*SqliteAlvoDataOutboxTests*'
```
Expected: FAIL — `OutboxEventFactory` does not exist and no site emits.

- [x] **Step 4: Implement the factory, then the four sites**

`OutboxEventFactory.For` is four short private helpers plus one composition:

```csharp
internal static AlvoEvent For(
    EntitySchema entity, OutboxOperation operation, AlvoContext context, DateTimeOffset now,
    AlvoRecord? postImage, AlvoRecord? preImage)
{
    var rowId = RowIdOf(postImage ?? preImage);

    return new AlvoEvent
    {
        Id = AlvoEventId.Create(now),
        Source = AlvoEvent.DefaultSource,
        Type = $"entity.{entity.Name}.{Suffix(operation)}",
        Time = now,
        Subject = $"{entity.Name}/{rowId}",
        PartitionKey = PartitionKeyFor(entity.Name, rowId),
        AuthType = AuthTypeOf(context),
        AuthId = AuthIdOf(context),
        CorrelationId = CorrelationIdOf(),
        Data = new AlvoEventData
        {
            Record = postImage,
            OldRecord = preImage,
            Changed = ChangedFields(postImage, preImage),
        },
    };
}
```

- `Id` comes from **`AlvoEventId.Create(now)`** and never from `Guid.CreateVersion7()`: the id is the
  outbox's queue order, and the plain BCL mint inverts 49.9 % of same-millisecond pairs (D1, spike
  Q1). Passing `now` — the write's own audit instant — makes the envelope's `time`, the row's
  `created_at` and the id's embedded millisecond one instant instead of three clock reads. Add a fact
  in `OutboxEventFactoryTests` that two events minted for one entity inside one millisecond sort in
  emit order, so a future edit back to `Guid.CreateVersion7()` fails here too and not only in
  `AlvoEventIdTests`.
- `Suffix` is a three-arm switch returning `"created"`/`"updated"`/`"deleted"` — each matching
  `$defs/eventPattern`'s third segment `[a-z]+`.
- `AuthTypeOf` reads the context: the reserved system user id → `System`; `AlvoContext.Anonymous`'s
  all-zero id → `Anonymous`; otherwise `ApiKey`. **Not** a role check — a role is authorization, not
  authentication.
- `CorrelationIdOf` is `Activity.Current?.TraceId.ToString()`, falling back to the event's own id.
  `System.Diagnostics.Activity` is BCL; no dependency. Its `<remarks>` records that this is the
  W3C trace id §2.12's end-to-end trace needs, and that `CausationId` stays `null` in PR5a because
  nothing yet runs a data action *because of* an event — PR5b sets it, and `ChainDepth` with it,
  which is why both members exist now rather than being added later.
- `ChangedFields` compares each key's value with `Equals` over the union of both records' keys;
  on a create it returns every post-image key, on a delete every pre-image key.

Then the four sites. Each takes the same three arguments and is one line, so the emit reads
identically everywhere:

```csharp
// EfAlvoData, one new private method — the ONLY place an event is written.
private Task EmitAsync(
    AlvoDataContext db, IDbContextTransaction transaction, EntitySchema schema,
    OutboxOperation operation, AlvoContext context, DateTimeOffset now,
    AlvoRecord? postImage, AlvoRecord? preImage, CancellationToken cancellationToken) =>
    OutboxTable.InsertAsync(
        db.Database.GetDbConnection(),
        transaction.GetDbTransaction(),
        _outboxTable,
        OutboxEventFactory.For(schema, operation, context, now, postImage, preImage),
        cancellationToken);
```

Placement, one site at a time — and the ordering rule is the same at all four: **emit last, inside
the transaction, after the write's own re-read has succeeded**, so an event never describes a row
the write did not produce.

1. `CreatedAsync` (`:170-182`): after `InsertAsync` returns `stored`, before `CommitAsync`.
2. `RecordedCreateAsync` (`:339-347`): after `InsertAsync`, beside the idempotency record's insert.
   **Not** in `ReplayedAsync` — a replay wrote no row.
3. `UpdateAsync` (`:567-589`): `WriteAsync` already holds both the locked pre-image and the
   re-read post-image; return them both so the site can emit with `preImage` non-null.
4. `DeleteAsync` (`:606-623`): `EraseAsync` already reads the unmasked pre-image for exactly this
   reason — `data-path.md:630-637` says so — so pass it out and emit after `affected != 0`.

The instant is the write's own: `_time.GetUtcNow()` is read **once** per write and threaded to both
the audit stamp and the emit, never read twice. Where `Stamped` already read it, lift it to the
caller. `_outboxTable` is a new readonly field, `OutboxTable.NameFor(options.SchemaPrefix)`, beside
`_idempotencyTable`.

`WriteAsync` and `EraseAsync` change signature to return the images they already have. Do not add a
second read anywhere: every image this task needs is already in hand, which is why the seam is cheap.

- [x] **Step 5: Run to verify they pass, on both engines**

Run:
```
dotnet test --project test/MMLib.Alvo.Data.EntityFrameworkCore.Tests -- --filter-class '*OutboxEventFactoryTests*'
dotnet test --project test/MMLib.Alvo.Data.Sqlite.Tests -- --filter-class '*SqliteAlvoDataOutboxTests*'
dotnet test --project test/MMLib.Alvo.Data.PostgreSql.Tests.Integration -- --filter-class '*PostgreSqlAlvoDataOutboxTests*'
```
Expected: PASS. Assert `Build succeeded` first.

- [x] **Step 6: Prove the interceptor trap is really closed**

Two mutations, restored immediately. The first is the one this whole task exists for:

1. **Replace the four explicit emits with a `SaveChangesInterceptor`** that writes the outbox row on
   `SavedChanges`. Confirm `A_create_emits_exactly_one_event` **passes** while
   `An_update_emits_exactly_one_event_carrying_both_images` and
   `A_delete_emits_exactly_one_event_carrying_the_pre_image` go **red**. Restore. This is the
   measurement that turns `data-path.md`'s warning into a fact the suite holds: a create-only suite
   would have shipped the trap.
2. **Move `EmitAsync` after `transaction.CommitAsync`** on the create path. Confirm
   `A_write_the_engine_refuses_leaves_no_outbox_row` goes **red**. Restore.

- [x] **Step 7: Accept the `Testing` baseline, ring0, commit**

```bash
dotnet test --project test/MMLib.Alvo.Data.Sqlite.Tests -- --filter-class '*PublicApi*'
# AlvoDataOutboxTests and IAlvoDataOutboxWorld are new public members of MMLib.Alvo.Testing.
# Accept the baseline; dispatch alvo-snapshot-judge when the Stop hook asks.
scripts/test-ring0
git add src/MMLib.Alvo.Data.EntityFrameworkCore/Internal/ src/MMLib.Alvo.Testing/Data/ \
        test/_shared/PublicApi.MMLib.Alvo.Testing.verified.txt \
        test/MMLib.Alvo.Data.EntityFrameworkCore.Tests/ test/MMLib.Alvo.Data.Sqlite.Tests/ \
        test/MMLib.Alvo.Data.PostgreSql.Tests.Integration/
git commit -m "feat(events): emit an outbox event on the same transaction as every write"
```

**What Task 4 changed from this plan as written, and why — measured, not preferred**

1. **The suite owns the entity; the world is a store plus a reader.** `WorldAsync()` became
   `WorldAsync(SchemaModel, AlvoDescriptor)` and `IAlvoDataOutboxWorld` is `Data` +
   `EventsAsync()` — no `CreateVehicleAsync`/`ClearOutboxAsync`. Reason: the descriptor decides
   whether a fact can fail (the `unique` field, `audit`, the `hidden` field, a rule anonymous fails),
   and `AlvoDataConstraintTests`/`AlvoDataConcurrencyTests` already put it in the shipped suite so *"the
   subclass supplies a store and nothing else"*. Dropping `ClearOutboxAsync` also removed a test-only
   write to a framework table: every fact asserts the whole ordered sequence instead, which is the
   stronger question. The per-engine world implementation is linked from `test/_shared/ef/` for the
   reason `OutboxTableFacts` is.
2. **`A_write_the_engine_refuses_leaves_no_outbox_row` cannot see the transaction, and one added fact
   can.** With the emit last, *nothing* on the create path can fail after it — the duplicate-`vin`
   refusal comes out of `SaveChanges`, before the emit — so that fact discriminates "emits before the
   write succeeded", not "rides the transaction". The atomicity leg is therefore
   **`Two_concurrent_idempotent_creates_on_one_key_emit_exactly_one_event`**: the loser of the
   idempotency-key race has already emitted when the record's primary key refuses its write. Measured:
   moving the emit onto its own connection leaves that fact — and only that fact — red, with two events
   for one row.
3. **The idempotent create emits *before* the idempotency record**, not after it. Measured, as a
   two-mutation combination: emit-after-record plus emit-on-its-own-connection leaves the whole suite
   **green**, so the ordering is what makes the atomicity claim observable at all.
4. **The write path's re-reads are `unmasked: true`, and D7 is pinned by a named fact.** A masked
   post-image is not merely incomplete: every `hidden` field compares unequal to its own stored value,
   so `changed` would report it as moved on every update. `Unmasked()` reads without the null
   projection and `RecordMaterializer` masks what the caller is returned. Pinned by
   `An_events_record_carries_a_hidden_field_unmasked_and_that_is_the_documented_disclosure` (#152).
5. **The instant is threaded through a private `WriteInstant : TimeProvider`** rather than by widening
   `AlvoAuditStamp.Applied` with an instant overload — the need is this driver's, the port stays frozen.
6. **`command.Transaction` is the contract, not the mechanism.** Measured: deleting
   `command.Transaction = transaction` leaves every fact green on *both* engines, because a transaction
   belongs to the connection on SQLite and on PostgreSQL alike. It stays (ADO.NET requires it, and
   `SqlCommand` throws without it), and `OutboxTable`'s remarks now say so, so nobody reads the green as
   permission to drop it.
7. **`EnsureOutboxTableAsync` is load-bearing, not belt-and-braces.** The fixtures' `ISchemaMigrator`
   apply does **not** reach `SystemSchemaInitializer` (only a descriptor version write does), so without
   the write path's own ensure the first emit fails with *no such table: alvo_outbox*. That is exactly
   the "two creators, one DDL string" arrangement Task 3's remarks promised.

---

### Task 5: `IOutboxStore` — the earned port, and the portable claim

`package-boundary.md:152-155` predicted this: *"A port is earned the moment a driver's system schema
grows a table no store call touches — PR5's outbox is the first candidate."* The dispatcher lives in
the core, which depends on `Abstractions` alone, and `OutboxTable` is `internal` to the driver. So
the port is paid here.

**Files:**
- Create: `src/MMLib.Alvo.Abstractions/Events/IOutboxStore.cs`
- Create: `src/MMLib.Alvo.Data.EntityFrameworkCore/EfCoreOutboxStore.cs`
- Modify: `src/MMLib.Alvo.Data.EntityFrameworkCore/Internal/OutboxTable.cs` (the three statements)
- Modify: `src/MMLib.Alvo.Data.EntityFrameworkCore/AlvoEfCoreProvider.cs`
- Create: `src/MMLib.Alvo.Testing/Events/OutboxStoreContractTests.cs`
- Test: `test/MMLib.Alvo.Data.Sqlite.Tests/SqliteOutboxStoreTests.cs`
- Test: `test/MMLib.Alvo.Data.PostgreSql.Tests.Integration/PostgreSqlOutboxStoreTests.cs`
- Test: `test/MMLib.Alvo.Data.EntityFrameworkCore.Tests/OutboxClaimSqlTests.cs`
- Modify: `test/MMLib.Alvo.Abstractions.Tests/PublicApi.MMLib.Alvo.Abstractions.verified.txt`,
  `test/_shared/PublicApi.MMLib.Alvo.Data.EntityFrameworkCore.verified.txt`,
  `test/_shared/PublicApi.MMLib.Alvo.Testing.verified.txt`

**Interfaces:**
- Consumes: `OutboxTable` (Task 3), `RelationalConnectionFactory`, `StoredInstant`,
  `AlvoOptions.SchemaPrefix`, the spike's Q3/Q4/Q5 verdicts.
- Produces:
  ```csharp
  namespace MMLib.Alvo.Events;

  public sealed record OutboxEntry(Guid Id, string Type, string PartitionKey, string Payload, int Attempts);

  public interface IOutboxStore
  {
      Task EnsureAsync(CancellationToken cancellationToken = default);

      Task<IReadOnlyList<OutboxEntry>> ClaimAsync(
          string claimant, int batchSize, int maxAttempts, TimeSpan lease,
          CancellationToken cancellationToken = default);

      Task MarkDispatchedAsync(Guid id, CancellationToken cancellationToken = default);

      Task ReleaseAsync(Guid id, CancellationToken cancellationToken = default);
  }

  public sealed class MMLib.Alvo.Data.EntityFrameworkCore.EfCoreOutboxStore : IOutboxStore;
  public abstract class MMLib.Alvo.Testing.Events.OutboxStoreContractTests;   // both drivers inherit
  ```

- [ ] **Step 1: Write the failing contract suite**

```csharp
// src/MMLib.Alvo.Testing/Events/OutboxStoreContractTests.cs
/// <summary>
/// The claim protocol every <see cref="IOutboxStore"/> must implement identically, on every engine.
/// </summary>
public abstract class OutboxStoreContractTests
{
    protected abstract Task<IOutboxStoreWorld> WorldAsync();

    [Fact]
    public async Task A_claim_returns_the_oldest_undispatched_entries_in_order()
    {
        await using var world = await WorldAsync();
        var ids = await world.SeedAsync(count: 10);

        var claimed = await world.Store.ClaimAsync("worker-1", batchSize: 4, MaxAttempts, Lease);

        claimed.Select(entry => entry.Id).ShouldBe(ids.Take(4));
    }

    /// <summary>
    /// <c>RETURNING</c>'s row order is documented as arbitrary on both engines, so the store
    /// re-sorts in process. Without that, "in order" above would hold only by luck.
    /// </summary>
    [Fact]
    public async Task A_claim_is_sorted_in_process_because_returning_order_is_arbitrary()
    {
        await using var world = await WorldAsync();
        await world.SeedAsync(count: 50);

        var claimed = await world.Store.ClaimAsync("worker-1", batchSize: 50, MaxAttempts, Lease);

        claimed.Select(entry => entry.Id.ToString())
            .ShouldBe(claimed.Select(entry => entry.Id.ToString()).Order(StringComparer.Ordinal));
    }

    [Fact]
    public async Task A_claimed_entry_is_not_claimed_twice_while_its_lease_holds()
    {
        await using var world = await WorldAsync();
        await world.SeedAsync(count: 4);

        var first = await world.Store.ClaimAsync("worker-1", batchSize: 4, MaxAttempts, Lease);
        var second = await world.Store.ClaimAsync("worker-1", batchSize: 4, MaxAttempts, Lease);

        first.Count.ShouldBe(4);
        second.ShouldBeEmpty();
    }

    /// <summary>
    /// The recovery path the crash criterion rests on: a claim whose process died is re-claimed once
    /// its lease expires. Without it, one kill strands an event forever.
    /// </summary>
    [Fact]
    public async Task A_claim_whose_lease_expired_is_claimed_again()
    {
        await using var world = await WorldAsync();
        await world.SeedAsync(count: 1);
        await world.Store.ClaimAsync("dead-worker", batchSize: 1, MaxAttempts, Lease);

        world.Clock.Advance(Lease + TimeSpan.FromSeconds(1));
        var reclaimed = await world.Store.ClaimAsync("worker-2", batchSize: 1, MaxAttempts, Lease);

        reclaimed.ShouldHaveSingleItem().Attempts.ShouldBe(2);
    }

    [Fact]
    public async Task A_dispatched_entry_is_never_claimed_again()
    {
        await using var world = await WorldAsync();
        var ids = await world.SeedAsync(count: 1);
        await world.Store.ClaimAsync("worker-1", batchSize: 1, MaxAttempts, Lease);

        await world.Store.MarkDispatchedAsync(ids[0]);
        world.Clock.Advance(Lease + TimeSpan.FromSeconds(1));

        (await world.Store.ClaimAsync("worker-1", batchSize: 1, MaxAttempts, Lease)).ShouldBeEmpty();
    }

    [Fact]
    public async Task A_released_entry_is_claimable_immediately_without_waiting_for_the_lease()
    {
        await using var world = await WorldAsync();
        var ids = await world.SeedAsync(count: 1);
        await world.Store.ClaimAsync("worker-1", batchSize: 1, MaxAttempts, Lease);

        await world.Store.ReleaseAsync(ids[0]);

        (await world.Store.ClaimAsync("worker-1", batchSize: 1, MaxAttempts, Lease)).ShouldHaveSingleItem();
    }

    /// <summary>
    /// PR5a's stand-in for a DLQ (7.1) is an attempt ceiling plus a loud log: past the ceiling the
    /// entry stops being claimed, so one poison event cannot occupy the pump forever.
    /// </summary>
    [Fact]
    public async Task An_entry_past_the_attempt_ceiling_is_no_longer_claimed()
    {
        await using var world = await WorldAsync();
        var ids = await world.SeedAsync(count: 1);

        foreach (var _ in Enumerable.Range(0, MaxAttempts))
        {
            await world.Store.ClaimAsync("worker-1", batchSize: 1, MaxAttempts, Lease);
            await world.Store.ReleaseAsync(ids[0]);
        }

        (await world.Store.ClaimAsync("worker-1", batchSize: 1, MaxAttempts, Lease)).ShouldBeEmpty();
    }

    /// <summary>
    /// R2: PostgreSQL sequences commit out of order, so a "processed up to N" watermark drops a row
    /// silently. This proves the claim is a flag filter and not a watermark: an entry inserted with
    /// an id BELOW one already dispatched is still claimed.
    /// </summary>
    [Fact]
    public async Task An_entry_whose_id_sorts_below_an_already_dispatched_one_is_still_claimed()
    {
        await using var world = await WorldAsync();
        var late = await world.SeedAsync(count: 1);
        await world.Store.ClaimAsync("worker-1", batchSize: 1, MaxAttempts, Lease);
        await world.Store.MarkDispatchedAsync(late[0]);

        var earlier = await world.SeedWithExplicitIdAsync(Guid.Empty);

        (await world.Store.ClaimAsync("worker-1", batchSize: 1, MaxAttempts, Lease))
            .ShouldHaveSingleItem().Id.ShouldBe(earlier);
    }

    private const int MaxAttempts = 5;
    private static readonly TimeSpan Lease = TimeSpan.FromMinutes(5);
}
```

`IOutboxStoreWorld` exposes `Store`, a `FakeTimeProvider`-backed `Clock`, `SeedAsync`,
`SeedWithExplicitIdAsync`. `FakeTimeProvider` is BCL (`Microsoft.Extensions.TimeProvider.Testing` is
**not** needed — write a four-line `TestClock : TimeProvider` overriding `GetUtcNow`, as the repo
already does for the audit tests).

```csharp
// EntityFrameworkCore.Tests/OutboxClaimSqlTests.cs — the statement's shape, no database
[Fact]
public void The_claim_filters_the_dispatch_flag_and_never_a_high_water_mark()
{
    var sql = OutboxTable.ClaimSql("alvo_outbox");

    sql.ShouldContain("dispatched_at IS NULL");
    sql.ShouldNotContain(">", "a high-water mark on a monotonic key drops a row silently (R2)");
}

// Measured on BOTH engines (Q3), which corrects R4: the parser dies on ORDER, not on limit, and
// PostgreSQL refuses the same statement — so this is portability, not a SQLite workaround.
// SQLITE_ENABLE_UPDATE_DELETE_LIMIT is also unset in the bundled e_sqlite3.
[Fact]
public void The_order_by_and_limit_are_in_the_subquery_because_neither_engine_allows_them_in_update()
{
    var sql = OutboxTable.ClaimSql("alvo_outbox");

    sql.ShouldContain("id IN (SELECT id FROM alvo_outbox");
    sql.ShouldContain("ORDER BY id");
    sql.ShouldContain("LIMIT @batch");
    Regex.IsMatch(sql, @"UPDATE\s+alvo_outbox[\s\S]*?LIMIT", RegexOptions.None)
        .ShouldBeTrue("the only LIMIT must be the subquery's");
}

[Fact]
public void The_claim_takes_no_row_lock_hint_because_one_dispatcher_needs_none()
{
    var sql = OutboxTable.ClaimSql("alvo_outbox");

    sql.ShouldNotContain("SKIP LOCKED");
    sql.ShouldNotContain("FOR UPDATE");
}

/// <summary>
/// Spike Q4: the outer <c>WHERE</c> must repeat the subquery's claimability predicate, or two
/// claimants deliver every row twice.
/// </summary>
/// <remarks>
/// Under <c>READ COMMITTED</c>, PostgreSQL's EvalPlanQual re-check re-evaluates the <b>outer</b>
/// <c>WHERE</c> against the row the winner just updated — and nothing else. A subquery-only predicate
/// is not part of that re-check, so the loser's <c>id IN (…)</c> still holds and it re-claims rows
/// that are already claimed: measured as <em>"A claimed 10, B claimed 10, overlap 10; rows with
/// attempts &gt; 1: 10"</em>. This fact is a shape assertion rather than a behaviour one because it is
/// the one that survives in a project with no database; the behaviour is pinned on PostgreSQL by
/// <c>PostgreSqlOutboxStoreTests.A_second_claimant_claims_nothing_rather_than_the_same_rows</c>.
/// </remarks>
[Fact]
public void The_outer_where_repeats_the_claimability_predicate_it_is_not_redundant()
{
    var outerWhere = OutboxTable.ClaimSql("alvo_outbox").Split("AND id IN (")[0];

    outerWhere.ShouldContain("dispatched_at IS NULL");
    outerWhere.ShouldContain("claimed_at IS NULL");
}
```

```csharp
// Data.PostgreSql.Tests.Integration/PostgreSqlOutboxStoreTests.cs — the fact the SHAPE cannot prove.
// This is the spike's Q4 harness, kept: it is the only fact in the suite that would have caught D2's
// original statement, and a shape assertion cannot notice a row claimed twice.
[Fact]
public async Task A_second_claimant_claims_nothing_rather_than_the_same_rows()
{
    await using var world = await WorldAsync();
    await world.SeedAsync(count: 10);

    var (first, second) = await world.TwoConcurrentClaimsAsync(batchSize: 10);

    first.Count.ShouldBe(10);
    second.ShouldBeEmpty(
        "no SKIP LOCKED means the loser BLOCKS and then re-checks; with the claimability predicate "
        + "only in the subquery it re-claimed all 10 and attempts reached 2 on every row (spike Q4)");
    (await world.MaxAttemptsAsync()).ShouldBe(1);
}
```

`TwoConcurrentClaimsAsync` is the spike's Q4 harness, kept as a test: two `NpgsqlConnection`s on the
container's database, each running **`OutboxTable.ClaimSql`** — the production statement, reachable
because the driver grants `InternalsVisibleTo` to this project — inside its own explicit transaction.
A's transaction stays open, B's claim is started and asserted to be **blocked**, A commits, then B is
awaited. Two connections with explicit transactions rather than two `EfCoreOutboxStore` calls, because
the store issues one autocommit statement per call and therefore has no window a test can hold open:
the race must be constructed, or the fact is a coin toss. `MaxAttemptsAsync` reads `MAX(attempts)`,
which is the half of Q4's finding that survives even when the overlap looks empty — a double claim
increments `attempts` whether or not both callers see the rows.

- [ ] **Step 2: Run to verify they fail**

Run:
```
dotnet test --project test/MMLib.Alvo.Data.EntityFrameworkCore.Tests -- --filter-class '*OutboxClaimSqlTests*'
dotnet test --project test/MMLib.Alvo.Data.Sqlite.Tests -- --filter-class '*SqliteOutboxStoreTests*'
```
Expected: FAIL — `IOutboxStore` does not exist.

- [ ] **Step 3: Implement — the Q5 gate is already resolved**

**The gate cleared, and the branch it chose was the first one: implement as written, change no
registration.** Measured (Q5): the shipped SQLite registration carries `journal_mode=delete`,
`busy_timeout=0` **and** `Microsoft.Data.Sqlite`'s `DefaultTimeout = 30 s`, whose retry loop covers
`BEGIN` — so a second writer waits (~1 s in the harness) and then succeeds, in both directions, and an
explicit `PRAGMA busy_timeout=5000` changes nothing measurable. R5's claim that there is "no
`Default Timeout` anywhere" was wrong, which is why the shipped configuration was already correct.

Two things that follow, and they are constraints on **this** task rather than on the registration:

- **Do not set a `Default Timeout`, a `busy_timeout` or a `journal_mode` anywhere** — not on
  `EfCoreOutboxStore`'s own connection string either. Nothing measured needs one.
- **Never read then write inside one transaction.** That is the single shape that reaches R5's
  mechanism: under WAL it fails unretryably with `SQLITE_BUSY_SNAPSHOT` (`Extended=517`) after
  burning the whole 30 s loop, and in the shipped journal mode it fails the *other* party — the
  request path. WAL moves whose write fails; it does not fix it, and `journal_mode=WAL` persists in
  the database file, so it is not a redeploy away from being undone. Each `EfCoreOutboxStore` member
  therefore opens a connection and issues **one** statement.

`OutboxTable` gains `ClaimSql(tableName)` as a member (so the arch tests above can read it without a
database), plus `MarkDispatchedSql` and `ReleaseSql`:

```csharp
internal static string ClaimSql(string tableName) =>
    $"""
    UPDATE {tableName} SET claimed_at = @claimed_at, claimed_by = @claimed_by,
                           attempts = attempts + 1
     WHERE dispatched_at IS NULL
       AND (claimed_at IS NULL OR claimed_at < @stale_before)
       AND id IN (SELECT id FROM {tableName}
                   WHERE dispatched_at IS NULL
                     AND attempts < @max_attempts
                     AND (claimed_at IS NULL OR claimed_at < @stale_before)
                   ORDER BY id
                   LIMIT @batch)
    RETURNING id, event_type, partition_key, payload, attempts
    """;
```

This is D2's **amended** statement, from `spike.txt` Q4, verbatim. The version without the outer
`dispatched_at`/`claimed_at` predicates — which is what this plan's first draft specified — is
measured broken; do not restore it.

Its `<remarks>` records five things, each measured or cited:

- **Why the outer `WHERE` repeats the subquery's predicate.** It is the whole correctness of the
  statement, not a belt on a brace. Measured (Q4) without it: *"A claimed 10, B claimed 10, overlap
  10 (must be 0); rows with attempts > 1: 10"* — two claimants deliver **every** row twice. Under
  `READ COMMITTED`, PostgreSQL's EvalPlanQual re-check runs the **outer** `WHERE` again against the
  row the winner just updated, and nothing else; the subquery's `claimed_at IS NULL` was evaluated
  before the block and is not re-checked. With the predicate repeated: *"A claimed 10, B claimed 0,
  overlap 0; rows with attempts > 1: 0"*. Anyone tempted to call it redundant is reading the subquery
  as if it re-ran.
- **Why raw SQL and not LINQ.** `UseRelationalNulls()` is on in both drivers and *"PR5 is the first
  PR its cost binds"* (`data-path.md:121-145`): a LINQ predicate over a nullable column would have to
  be written `x != null && x < y` against C#'s reading of the same text. Raw SQL has SQL's semantics
  natively, so the constraint is met by construction rather than by whoever edits this next
  remembering it. The arch fact below holds the line.
- **Why the `ORDER BY` and the `LIMIT` are in the subquery.** Measured on **both** engines (Q3), which
  corrects R4 twice: it is not a SQLite quirk, and the parser names `ORDER`, not `limit` — SQLite
  `'near "ORDER": syntax error'`, PostgreSQL `42601 syntax error at or near "ORDER"`. The bundled
  `e_sqlite3` also confirms `SQLITE_ENABLE_UPDATE_DELETE_LIMIT` is unset.
- **Why the result is re-sorted in process.** `RETURNING`'s row order is arbitrary in measured fact on
  both engines — `RETURNING already sorted: False` for SQLite *and* PostgreSQL (Q3) — so `ORDER BY` in
  the subquery decides *which* rows, never in what order they come back.
- **Why there is no `SKIP LOCKED`.** It skips the **row**, not the **key**, so it delivers neither
  global nor per-entity-key ordering (deviation 72); with exactly one dispatcher it buys nothing, and
  a new `IAlvoSqlDialect` member would be a public-API change in a driver package for a seam F7 will
  design properly. Spike Q4 measured that a second claimant blocks and then claims **nothing** —
  **with the amended statement**, and every row twice without it.

One shape constraint from Q5, which belongs on `ClaimAsync` rather than on any registration: the claim
must be **one write statement** on an autocommit connection (or the first statement of a write-first
transaction) and must never be preceded by a read inside the same transaction. Measured: a `DEFERRED`
read-then-write transaction is the one shape that reaches R5's mechanism, and under WAL it fails
unretryably with `SQLITE_BUSY_SNAPSHOT` (`Extended=517`) after burning the full 30 s retry loop.
`EfCoreOutboxStore` opens a connection per member and issues one statement, which satisfies this by
construction — say so, or the next edit wraps the pair in a transaction to be tidy.

`EfCoreOutboxStore` is a public sealed class taking `RelationalConnectionFactory`, `AlvoOptions` and
`TimeProvider`; each member opens a connection, calls the matching `OutboxTable` statement, and
disposes. `ClaimAsync` computes `stale_before = now - lease`, binds through
`StoredInstant.Text`, and returns `[.. entries.OrderBy(entry => entry.Id.ToString(), StringComparer.Ordinal)]`.
Register it in `AlvoEfCoreProvider` beside the other stores: `services.TryAddSingleton<IOutboxStore>(…)`.

Add the arch fact that keeps the claim raw:

```csharp
// EntityFrameworkCore.Tests/ChangeTrackerReachTests.cs
/// <summary>
/// The claim and dispatch statements stay raw SQL rather than LINQ over the context.
/// </summary>
/// <remarks>
/// <c>UseRelationalNulls()</c> is on in both drivers, so a LINQ comparison over a nullable column
/// no longer means what it means in C# (<c>docs/architecture/data-path.md</c>). Raw SQL carries
/// SQL's semantics natively; this fact is what stops the next edit from reaching for
/// <c>Where(entry =&gt; entry.ClaimedAt != stale)</c> and silently changing the predicate's meaning.
/// </remarks>
[Theory]
[InlineData("OutboxTable.cs")]
[InlineData("EfCoreOutboxStore.cs")]
public void The_outbox_claim_is_raw_sql_and_never_linq_over_the_context(string file)
{
    var source = ReadSource(file);

    foreach (var linq in new[] { ".Where(", ".FirstOrDefault(", "IQueryable", "db.Rows(" })
    {
        source.ShouldNotContain(linq, Case.Sensitive, file);
    }
}
```

- [ ] **Step 4: Run to verify they pass, on both engines**

Run:
```
dotnet test --project test/MMLib.Alvo.Data.EntityFrameworkCore.Tests -- --filter-namespace 'MMLib.Alvo.Data.EntityFrameworkCore.Tests'
dotnet test --project test/MMLib.Alvo.Data.Sqlite.Tests -- --filter-class '*SqliteOutboxStoreTests*'
dotnet test --project test/MMLib.Alvo.Data.PostgreSql.Tests.Integration -- --filter-class '*PostgreSqlOutboxStoreTests*'
```
Expected: PASS on both. Assert `Build succeeded` first.

- [ ] **Step 5: Prove the claim facts discriminate**

Four mutations, restored immediately:

0. **Delete the outer `dispatched_at IS NULL AND (claimed_at IS NULL OR …)`**, leaving `WHERE id IN
   (subquery)` — D2's original statement. Confirm
   `The_outer_where_repeats_the_claimability_predicate_it_is_not_redundant` **and**
   `A_second_claimant_claims_nothing_rather_than_the_same_rows` go **red** on PostgreSQL. This is the
   one mutation whose result is already known from outside the suite: the spike measured it as
   overlap 10 of 10 with `attempts` at 2, so a green run here means the *test* is wrong, not the SQL.

1. **Replace `dispatched_at IS NULL AND attempts < @max_attempts AND (claimed_at IS NULL OR …)` with
   `id > @high_water`.** Confirm
   `An_entry_whose_id_sorts_below_an_already_dispatched_one_is_still_claimed` goes **red** on
   PostgreSQL. This is R2's whole point, and it is the mutation that proves the fact is about the
   predicate rather than about the seed.
2. **Delete the in-process re-sort** from `ClaimAsync`. Confirm
   `A_claim_is_sorted_in_process_because_returning_order_is_arbitrary` goes **red on at least one
   engine**; if it stays green on both, the fact is not adversarial enough — raise the seed count
   until it fails, and record the count that made it fail in the test's own message.
3. **Drop `AND attempts < @max_attempts`.** Confirm
   `An_entry_past_the_attempt_ceiling_is_no_longer_claimed` goes **red**.

- [ ] **Step 6: Accept the three baselines, ring0, commit**

```bash
dotnet test --project test/MMLib.Alvo.Abstractions.Tests -- --filter-class '*PublicApi*'
dotnet test --project test/MMLib.Alvo.Data.Sqlite.Tests -- --filter-class '*PublicApi*'
# IOutboxStore + OutboxEntry (Abstractions), EfCoreOutboxStore (driver),
# OutboxStoreContractTests + IOutboxStoreWorld (Testing). Accept; dispatch alvo-snapshot-judge.
scripts/test-ring0
git add src/MMLib.Alvo.Abstractions/Events/IOutboxStore.cs \
        src/MMLib.Alvo.Data.EntityFrameworkCore/ src/MMLib.Alvo.Testing/Events/ \
        test/MMLib.Alvo.Abstractions.Tests/PublicApi.MMLib.Alvo.Abstractions.verified.txt \
        test/_shared/ test/MMLib.Alvo.Data.EntityFrameworkCore.Tests/ \
        test/MMLib.Alvo.Data.Sqlite.Tests/ test/MMLib.Alvo.Data.PostgreSql.Tests.Integration/
git commit -m "feat(events): add the IOutboxStore port and its portable claim statement"
```

---

### Task 6: The `{{…}}` template engine, and the JSONata classifier

Two pure units with no descriptor wiring, so their rules can be pinned exhaustively before anything
depends on them. Deviation 63's discriminator is written down here because **both plausible naive
rules fail open**, and the shipped example proves it.

**Files:**
- Create: `src/MMLib.Alvo/Events/Internal/AlvoTemplate.cs`
- Create: `src/MMLib.Alvo/Events/Internal/JsonataSlot.cs`
- Test: `test/MMLib.Alvo.Tests/Events/AlvoTemplateTests.cs`
- Test: `test/MMLib.Alvo.Tests/Events/JsonataSlotTests.cs`

**Interfaces:**
- Consumes: `AlvoEvent`/`AlvoEventData` (Task 2); `EntitySchema`; `MMLib.Alvo.Internal.NameSuggestion`.
- Produces:
  ```csharp
  namespace MMLib.Alvo.Events.Internal;

  internal sealed record AlvoTemplate(IReadOnlyList<AlvoTemplateSegment> Segments)
  {
      internal static AlvoTemplate Parse(string source);
      internal IReadOnlyList<string> Placeholders { get; }
      internal string Render(AlvoEvent @event);
  }

  internal readonly record struct AlvoTemplateSegment(string Text, bool IsPlaceholder);

  internal static class TemplatePlaceholder
  {
      internal static bool TryResolve(string placeholder, EntitySchema entity, out string? refusal);
      internal static object? ValueOf(string placeholder, AlvoEvent @event);
      internal static IReadOnlyList<string> Roots { get; }   // "new", "old", "event", "@user", "@tenant"
  }

  internal static class JsonataSlot
  {
      internal static bool IsTemplate(string source);
  }
  ```

- [ ] **Step 1: Write the failing classifier tests — the four cases the DoD names, verbatim**

```csharp
// MMLib.Alvo.Tests/Events/JsonataSlotTests.cs
/// <summary>
/// Deviation 63's rule: a string in a <c>$defs/jsonata</c> slot is a template iff it matches
/// <c>^(?:[^{}]|\{\{[^{}]+\}\})*$</c> <b>and</b> carries at least one placeholder.
/// </summary>
/// <remarks>
/// Both clauses earn their place against <c>examples/complex-crm/crm.alvo.json</c>, and the
/// four cases below are the DoD's own list.
/// </remarks>
[Theory]
// "contains {{" would classify this as literal text and deliver the JSONata source as the body.
[InlineData("{\"companyIds\": records.id}", false)]
[InlineData("$merge([new, {\"source\": \"alvo\"}])", false)]
// The fail-open remainder: a brace-free expression is a valid placeholder-free template, and would
// render as the literal string "records.id".
[InlineData("records.id", false)]
[InlineData("{{new.title}}", true)]
public void The_four_classifier_cases_are_pinned(string source, bool isTemplate)
    => JsonataSlot.IsTemplate(source).ShouldBe(isTemplate);

[Theory]
[InlineData("Deal won: {{new.title}}", true)]
[InlineData("{{new.title}} ({{new.amount}})", true)]
[InlineData("{{ new.title }}", true)]
[InlineData("{{new.{{title}}}}", false)]        // nested
[InlineData("{{}}", false)]                      // empty placeholder
[InlineData("{{new.title}", false)]              // unbalanced
[InlineData("a { b", false)]                     // a bare brace
[InlineData("", false)]                          // no placeholder
public void The_rule_admits_only_well_formed_non_nested_placeholders(string source, bool isTemplate)
    => JsonataSlot.IsTemplate(source).ShouldBe(isTemplate);
```

- [ ] **Step 2: Write the failing template-engine tests**

```csharp
// MMLib.Alvo.Tests/Events/AlvoTemplateTests.cs
[Fact]
public void A_template_renders_literals_and_placeholders_in_order()
{
    var @event = SampleEvent(record: Record(("title", "Big deal"), ("amount", 1200m)));

    AlvoTemplate.Parse("Deal won: {{new.title}} ({{new.amount}})").Render(@event)
        .ShouldBe("Deal won: Big deal (1200)");
}

[Theory]
[InlineData("{{event.id}}")]
[InlineData("{{event.type}}")]
[InlineData("{{event.time}}")]
[InlineData("{{event.subject}}")]
[InlineData("{{@user.id}}")]
[InlineData("{{@tenant.id}}")]
[InlineData("{{old.title}}")]
public void Every_documented_root_resolves(string template)
    => AlvoTemplate.Parse(template).Render(SampleEvent()).ShouldNotBeNullOrWhiteSpace();

/// <summary>
/// Deviation 64: an unresolvable placeholder is refused at apply, never rendered to empty.
/// </summary>
/// <remarks>
/// Rendering <c>{{@user.email}}</c> to <c>""</c> yields <c>To: ""</c> — a mail failure that looks
/// like a broken SMTP server, which is the same misattribution <c>UnhonouredSubsystems</c> exists
/// to prevent (<c>:21-24</c>). <c>AlvoContext</c> carries <c>User</c>, <c>Roles</c> and
/// <c>Tenant</c> and no email address, and the closed <c>@</c>-context set is exactly
/// <c>@user.id</c>, <c>@user.roles</c>, <c>@tenant.id</c> (<c>cel.md:234-238</c>).
/// </remarks>
[Fact]
public void The_shipped_examples_unresolvable_recipient_is_refused_and_the_message_names_the_roots()
{
    TemplatePlaceholder.TryResolve("@user.email", Deals, out var refusal).ShouldBeFalse();

    refusal.ShouldNotBeNull();
    refusal.ShouldContain("@user.id");
    refusal.ShouldContain("@tenant.id");
    refusal.ShouldContain("@user.email");
}

[Fact]
public void A_placeholder_naming_an_undeclared_field_is_refused_with_a_did_you_mean()
{
    TemplatePlaceholder.TryResolve("new.titel", Deals, out var refusal).ShouldBeFalse();

    refusal.ShouldContain("titel");
    refusal.ShouldContain("title", Case.Insensitive);
}

[Fact]
public void A_placeholder_naming_a_declared_field_resolves()
    => TemplatePlaceholder.TryResolve("new.title", Deals, out _).ShouldBeTrue();

[Fact]
public void An_unknown_root_is_refused_naming_every_root_that_exists()
{
    TemplatePlaceholder.TryResolve("record.title", Deals, out var refusal).ShouldBeFalse();

    foreach (var root in TemplatePlaceholder.Roots)
    {
        refusal.ShouldContain(root);
    }
}

/// <summary>
/// A value absent from the row renders as the empty string only because the FIELD is declared and
/// its value is genuinely null — which is a data fact, not an authoring mistake. The authoring
/// mistake is refused at apply instead, which is the whole point of validating there.
/// </summary>
[Fact]
public void A_declared_field_whose_value_is_null_renders_as_empty()
    => AlvoTemplate.Parse("[{{new.title}}]").Render(SampleEvent(record: Record(("title", null))))
        .ShouldBe("[]");

/// <summary>
/// A rendered timestamp is the framework's own round-trip form, so a template can never introduce a
/// second spelling of an instant.
/// </summary>
[Fact]
public void A_rendered_timestamp_is_the_frameworks_round_trip_utc_form()
{
    var time = new DateTimeOffset(2026, 8, 3, 9, 30, 0, TimeSpan.Zero);

    AlvoTemplate.Parse("{{event.time}}").Render(SampleEvent() with { Time = time })
        .ShouldBe("2026-08-03T09:30:00.0000000+00:00");
}

/// <summary>
/// A rendered value is never re-scanned for placeholders, or a record whose own text contained
/// <c>{{…}}</c> would inject one.
/// </summary>
[Fact]
public void A_rendered_value_is_never_itself_treated_as_a_template()
    => AlvoTemplate.Parse("{{new.title}}").Render(SampleEvent(record: Record(("title", "{{@user.id}}"))))
        .ShouldBe("{{@user.id}}");
```

- [x] **Step 3: Run to verify they fail**

Run: `dotnet test --project test/MMLib.Alvo.Tests -- --filter-namespace 'MMLib.Alvo.Tests.Events'`
Expected: FAIL — neither type exists.

- [ ] **Step 4: Implement both units**

`JsonataSlot`:

```csharp
internal static bool IsTemplate(string source) =>
    !string.IsNullOrEmpty(source) && WellFormedTemplate().IsMatch(source) && ContainsPlaceholder(source);

private static bool ContainsPlaceholder(string source) => source.Contains(PlaceholderOpen, StringComparison.Ordinal);

private const string PlaceholderOpen = "{{";

[GeneratedRegex(@"^(?:[^{}]|\{\{[^{}]+\}\})*$")]
private static partial Regex WellFormedTemplate();
```

The type's `<remarks>` states, in this order: that `$defs/jsonata` is typed `string`
(`schema:398-403`) so the schema cannot make this distinction and the apply path must; that *"contains
`{{`"* alone would deliver `crm.alvo.json`'s `"{\"companyIds\": records.id}"` as literal text; that
the at-least-one-placeholder clause exists because a brace-free expression such as `records.id`
would otherwise render as its own source; and that the **asymmetry** with the plain-string sugar
slots (`email.to`, `entity.update.recordId`, `templates.subject`/`body`) is deliberate and comes from
the schema's own typing — there, a placeholder-free string is a legitimate literal (a hard-coded
address), so it is accepted.

`AlvoTemplate.Parse` is a single forward scan producing alternating segments; `Placeholders` is the
trimmed inner text of each placeholder segment. `Render` maps each segment through
`TemplatePlaceholder.ValueOf` and formats with one small `Format` switch — `DateTimeOffset` as `"O"`,
`null` as `string.Empty`, everything else `InvariantCulture`. `TryResolve` splits on the first `.`,
switches on the root, and for `new`/`old` checks `entity.Fields` plus `AlvoManagedColumns`, producing
a `NameSuggestion`-backed refusal. `Roots` is the one authority the refusal message iterates, so a
root added later cannot be missing from the message.

- [ ] **Step 5: Run to verify they pass**

Run: `dotnet test --project test/MMLib.Alvo.Tests -- --filter-namespace 'MMLib.Alvo.Tests.Events'`
Expected: PASS. Assert `Build succeeded` first.

- [ ] **Step 6: Prove the classifier's two clauses each discriminate**

Two mutations, restored immediately. Each corresponds to one of the two naive rules the design says
fails open, so this is the measurement that the rule is not one clause with decoration:

1. **Replace the whole body with `source.Contains("{{")`.** Confirm the two `crm.alvo.json` cases
   (`{"companyIds": records.id}` and `$merge([...])`) go **red**.
2. **Delete the `ContainsPlaceholder` clause.** Confirm the `records.id` case goes **red**.

Then a third, on the engine: **make `Render` re-scan its output**, and confirm
`A_rendered_value_is_never_itself_treated_as_a_template` goes red — the injection case.

- [ ] **Step 7: ring0 + commit**

```bash
scripts/test-ring0
git add src/MMLib.Alvo/Events/Internal/AlvoTemplate.cs \
        src/MMLib.Alvo/Events/Internal/JsonataSlot.cs test/MMLib.Alvo.Tests/Events/
git commit -m "feat(events): add the {{...}} template engine and the JSONata slot classifier"
```

---

### Task 7: After-hooks compiled into the `PolicyCatalog`, and every refusal at apply

**DONE.** Step 6 ran first and cleared the gate: the whole `examples/` tree declares hooks on
`complex-crm` only, and there `contacts.beforeCreate` and `deals.beforeUpdate` — **zero** occurrences of
`afterCreate`/`afterUpdate`/`afterDelete` in the file — so removing the three `after*` entries leaves the
example refused by the two `before*` entries and exposes none of its five uncompilable expressions. The
fact is driven off the tree rather than off `complex-crm` by name, so a *new* example declaring an
after-hook fails it.

**Five deliberate deviations from this task as written, each for a reason in the code:**

1. **Error pointers keep the repository's leading slash** — `/entities/deals/hooks/afterUpdate/0/action/payload`,
   not `entities/…`. Every other apply-time refusal in the tree is spelled with one
   (`PolicyCatalogBuilder`'s `/entities/{name}/rules/{op}`, `DescriptorValidator`'s
   `/entities/{name}/fields/{name}`), and two spellings of a JSON Pointer in one error list is a defect.
   `EntityBuild` gained a `Path` property so the prefix has one authority.
2. **`EntityAfterHooks.For` takes `DataOperation`, not `OutboxOperation`.** `OutboxOperation` is
   `internal` to `MMLib.Alvo.Data.EntityFrameworkCore` and unreachable from the core — this is a compile
   constraint, not a preference. `DataOperation` is the core's own operation enum, public in
   `Abstractions`, and a read operation selects no hooks rather than throwing.
3. **A message template's `subject`/`body`/`bodyFile` are refused at `/templates/{name}/…`, not under the
   hook.** That is the file position an author edits, and one template may be referenced from several
   entities — in which case it is validated once per referencing entity, against that entity's schema.
   `bodyFile` is refused **per reference**: a template nothing references keeps its
   `UnhonouredSubsystems` warning.
4. **`AlvoTemplate.TryParse` was added** (Task 6's file). A malformed placeholder is unreachable in a
   `$defs/jsonata` slot — the classifier rejects it first — but reachable in a *sugar* slot, which asks
   the classifier nothing. Without it, `email.to: "{{new.owner_email}"` was an unhandled
   `ArgumentException` at apply: an authoring mistake reported as a framework crash.
5. **The absence fact's allow-list is `JsonataSlot.cs`, `UnhonouredFeatures.cs` and
   `AfterHookCompiler.cs`, with comments stripped**, not the two files this plan named. Three files
   mention JSONata in *code* after this task, and `AutomationAction.cs`/`AlvoTemplate.cs` mention it only
   in XML docs — stripping comments is what keeps the fact about code, since a refusal's whole job is to
   name the feature it refuses.

**Two extra pins this task earned.** `UnhonouredFeaturesTests.Every_unhonoured_slot_is_pinned` is a new
Verify baseline over the three `UnhonouredSlot`s, because they carry no path and therefore no
table-driven theory can be written over them at all — and every fact about them asserts equality with
the property that owns the words, so nothing else would notice a wording change.
`UnhonouredJsonataTests.Every_action_type_the_frozen_schema_declares_is_named` anchors the action switch
to `$defs/action`'s five `const` values, so a sixth action type fails rather than being silently accepted.

**One existing fact had to be narrowed, and it said so itself.**
`DescriptorValidatorTests.The_unhonoured_table_covers_every_hook_point_the_schema_declares` asserted the
table covered *exactly* the schema's six points; it is now
`Every_hook_point_the_schema_declares_is_either_refused_or_honoured`, a partition (refused ∪ honoured =
declared, nothing in both, nothing in neither). That keeps the anchor's strength — a point dropped from
the table without being implemented lands in neither half — while letting each point leave on its own day.

R11 is the constraint that shapes this task: `EntitySchema`/`SchemaModel` carry **no hooks**, and
rules live on the separately-primed `PolicyCatalog`. A hook catalog must join **that** priming, not
become a fourth priming site — two independently primed holders means a hook compiled against a
different schema revision than the rules judging the same write.

**Files:**
- Modify: `src/MMLib.Alvo/Rules/PolicyCatalog.cs:118-129` (`EntityPolicy`) and the new records
- Modify: `src/MMLib.Alvo/Rules/Internal/PolicyCatalogBuilder.cs:70-77`
- Create: `src/MMLib.Alvo/Events/Internal/AfterHookCompiler.cs`
- Modify: `src/MMLib.Alvo/Descriptor/Internal/UnhonouredFeatures.cs:98-161`
- Modify: `src/MMLib.Alvo/Descriptor/Internal/UnhonouredSubsystems.cs:72-96`
- Test: `test/MMLib.Alvo.Tests/Events/AfterHookCompilerTests.cs`
- Test: `test/MMLib.Alvo.Tests/Descriptor/UnhonouredJsonataTests.cs`
- Modify: `test/MMLib.Alvo.Tests/Descriptor/UnhonouredSubsystemsTests.cs`,
  `test/MMLib.Alvo.Tests/Descriptor/DescriptorToSchemaMapperTests.cs`

**Interfaces:**
- Consumes: `AlvoTemplate`, `TemplatePlaceholder`, `JsonataSlot` (Task 6); `ICelCompiler`,
  `CelProfile.Condition`, `CompiledExpression`; `EntityHooks`, `AfterHook`, `AutomationAction` and
  its five derived records; `DescriptorValidationError`.
- Produces:
  ```csharp
  // PolicyCatalog.cs — all internal, so no public surface moves.
  internal sealed record EntityPolicy(
      TenancyMode? Tenancy,
      CompiledExpression? TenantScope,
      IReadOnlyDictionary<DataOperation, OperationPolicy> Operations,
      IReadOnlyDictionary<string, FieldMask> Hidden,
      IReadOnlyDictionary<string, FieldMask> ReadOnly,
      EntityAfterHooks AfterHooks);

  internal sealed record EntityAfterHooks(
      IReadOnlyList<CompiledAfterHook> AfterCreate,
      IReadOnlyList<CompiledAfterHook> AfterUpdate,
      IReadOnlyList<CompiledAfterHook> AfterDelete)
  {
      internal static EntityAfterHooks None { get; }
      internal IReadOnlyList<CompiledAfterHook> For(OutboxOperation operation);
  }

  internal sealed record CompiledAfterHook(
      string Path, CompiledExpression? Condition, CompiledAction Action);

  internal sealed record CompiledAction(
      AutomationAction Action, IReadOnlyDictionary<string, AlvoTemplate> Templates);

  // UnhonouredFeatures.cs
  internal sealed record UnhonouredSlot(string Feature, string Consequence, string Fix);
  // UnhonouredFeatures.RawJsonata, UnhonouredFeatures.UnhonouredAction(string type)

  // AfterHookCompiler.cs
  internal static class AfterHookCompiler
  {
      internal static EntityAfterHooks Compile(
          EntityDescriptor? descriptor, EntitySchema schema, ICelCompiler compiler,
          IReadOnlyDictionary<string, MessageTemplate> templates, string entityPath,
          List<DescriptorValidationError> errors);
  }
  ```

- [x] **Step 1: Write the failing tests — the refusals first**

```csharp
// MMLib.Alvo.Tests/Descriptor/UnhonouredJsonataTests.cs
/// <summary>
/// Deviation 62: a raw JSONata expression in a <c>$defs/jsonata</c> slot is an <b>error</b> at
/// apply, by name, through the existing <c>UnhonouredFeatures</c> wording.
/// </summary>
/// <remarks>
/// It is an error rather than a warning on <c>UnhonouredSubsystems</c>' own line
/// (<c>:12-19</c>): not because the absent transform is unobservable — the action still runs — but
/// because it runs <b>with the wrong payload</b>. An author who wrote <c>webhook.payload</c> and
/// gets the canonical envelope has a delivery that succeeded carrying a body they did not declare,
/// which is the <c>default</c> case, not the <c>webhooks</c> case.
/// </remarks>
[Fact]
public void A_raw_jsonata_webhook_payload_is_refused_at_apply_with_a_pointer_and_a_fix()
{
    var refusal = Should.Throw<DescriptorValidationException>(
        () => Apply(DealsWithAfterUpdateWebhook(payload: "$merge([new, {\"source\": \"alvo\"}])")));

    var error = refusal.Result.Errors.ShouldHaveSingleItem();
    error.Path.ShouldBe("entities/deals/hooks/afterUpdate/0/action/payload");
    error.Message.ShouldBe(UnhonouredFeatures.RawJsonata.Consequence);
    error.Fix.ShouldBe(UnhonouredFeatures.RawJsonata.Fix);
}

[Fact]
public void A_template_webhook_payload_applies()
    => Should.NotThrow(() => Apply(DealsWithAfterUpdateWebhook(payload: "{{new.title}}")));

[Fact]
public void A_raw_jsonata_email_data_is_refused_at_apply()
    => Should.Throw<DescriptorValidationException>(
        () => Apply(DealsWithAfterUpdateEmail(data: "records.id")))
        .Result.Errors.ShouldHaveSingleItem()
        .Path.ShouldBe("entities/deals/hooks/afterUpdate/0/action/data");

/// <summary>
/// Deviation 65: with no evaluator, "JSONata never runs in-transaction" is <b>vacuous</b>, so this
/// is an <em>absence</em> test named as one. A test called "JSONata does not run in-transaction"
/// would be green forever and would read as though the ban were enforced.
/// </summary>
/// <remarks>
/// The real ban test is owed by the PR that introduces an evaluator, and it must be
/// <b>architectural</b> — nothing on the in-transaction path can reach the evaluator — not
/// behavioural, because a behavioural test only samples the paths someone thought of.
/// Tracked in the JSONata evaluator issue.
/// </remarks>
[Fact]
public void No_jsonata_evaluator_exists_on_any_path()
{
    var offenders = SourceFiles("src")
        .Where(file => File.ReadAllText(file).Contains("Jsonata", StringComparison.OrdinalIgnoreCase))
        .Select(Path.GetFileName)
        .ToList();

    offenders.ShouldBe(["JsonataSlot.cs", "UnhonouredFeatures.cs"], ignoreOrder: true,
        "the only mentions of JSONata in shipped code are the classifier that refuses it and the "
        + "table that words the refusal; anything else is an evaluator (deviation 65)");
}

/// <summary>
/// Deviation 66: <c>function</c> and <c>http.call</c> are frozen into <c>$defs/action</c> and
/// neither is implemented; <c>entity.update</c> is PR5b's. All three are refused <b>by name</b>,
/// each naming what does not happen.
/// </summary>
[Theory]
[InlineData("function")]
[InlineData("http.call")]
[InlineData("entity.update")]
public void An_after_hook_action_this_build_does_not_run_is_refused_by_name(string type)
{
    var refusal = Should.Throw<DescriptorValidationException>(() => Apply(DealsWithAfterUpdate(type)));

    var error = refusal.Result.Errors.ShouldHaveSingleItem();
    error.Message.ShouldContain(type);
    error.Message.ShouldBe(UnhonouredFeatures.UnhonouredAction(type).Consequence);
    error.Fix.ShouldNotBeNullOrWhiteSpace();
}
```

```csharp
// MMLib.Alvo.Tests/Events/AfterHookCompilerTests.cs
[Fact]
public void An_after_hook_condition_compiles_in_the_condition_profile_so_changed_is_legal()
{
    var hooks = Compile(AfterUpdate(condition: "changed(stage) && new.stage == 'won'"));

    hooks.AfterUpdate.ShouldHaveSingleItem().Condition.ShouldNotBeNull();
}

[Fact]
public void An_after_hook_condition_naming_an_undeclared_column_fails_at_save_not_at_request_time()
{
    var errors = CompileErrors(AfterUpdate(condition: "new.stagee == 'won'"));

    errors.ShouldHaveSingleItem().Path.ShouldBe("entities/deals/hooks/afterUpdate/0/condition");
}

[Fact]
public void An_after_hook_with_no_condition_compiles_to_a_null_condition_and_always_fires()
    => Compile(AfterUpdate(condition: null)).AfterUpdate.ShouldHaveSingleItem().Condition.ShouldBeNull();

/// <summary>
/// R11: hooks join the <see cref="PolicyCatalog"/>'s priming, not a fourth priming site.
/// </summary>
/// <remarks>
/// Two independently primed holders means a hook could be compiled against a different schema
/// revision than the rules judging the same write. Asserted structurally, because the failure is
/// invisible at run time until the revisions differ.
/// </remarks>
[Fact]
public void The_after_hook_catalog_is_reachable_from_the_one_primed_policy_catalog()
{
    var catalog = PolicyCatalog.Build(DealsDescriptor, DealsSchema, Compiler);

    catalog.TryGetEntity("deals", out var policy).ShouldBeTrue();
    policy.AfterHooks.AfterUpdate.ShouldNotBeEmpty();
}

[Fact]
public void An_entity_declaring_no_hooks_carries_the_empty_catalog_rather_than_null()
{
    PolicyCatalog.Build(NoHooksDescriptor, DealsSchema, Compiler)
        .TryGetEntity("vehicles", out var policy).ShouldBeTrue();

    policy.AfterHooks.ShouldBe(EntityAfterHooks.None);
}

/// <summary>
/// Every template in an action is parsed and validated once, at apply — so no placeholder is ever
/// resolved for the first time on the dispatch path, where a refusal would be a delivery failure
/// instead of an authoring error.
/// </summary>
[Fact]
public void Every_template_in_an_action_is_parsed_at_apply_and_carried_compiled()
{
    var hook = Compile(AfterUpdateEmail(to: "{{new.owner_email}}", template: "deal-won"))
        .AfterUpdate.ShouldHaveSingleItem();

    hook.Action.Templates.Keys.ShouldBe(["to", "subject", "body"], ignoreOrder: true);
}

[Fact]
public void A_template_in_an_action_naming_an_undeclared_field_is_refused_at_apply()
    => CompileErrors(AfterUpdateEmail(to: "{{new.owner_emial}}", template: "deal-won"))
        .ShouldHaveSingleItem().Path.ShouldBe("entities/deals/hooks/afterUpdate/0/action/to");

[Fact]
public void An_email_action_naming_an_undeclared_template_is_refused_at_apply()
    => CompileErrors(AfterUpdateEmail(to: "x@y.z", template: "no-such-template"))
        .ShouldHaveSingleItem().Message.ShouldContain("no-such-template");

[Fact]
public void A_webhook_action_naming_an_undeclared_endpoint_is_refused_at_apply()
    => CompileErrors(AfterUpdateWebhook(endpoint: "no-such-endpoint"))
        .ShouldHaveSingleItem().Message.ShouldContain("no-such-endpoint");
```

- [x] **Step 2: Run to verify they fail**

Run:
```
dotnet test --project test/MMLib.Alvo.Tests -- --filter-class '*AfterHookCompilerTests*'
dotnet test --project test/MMLib.Alvo.Tests -- --filter-class '*UnhonouredJsonataTests*'
```
Expected: FAIL — `AfterHookCompiler` and `EntityPolicy.AfterHooks` do not exist.

- [x] **Step 3: Add the refusal wording to `UnhonouredFeatures`**

A third shape in the same file, because the two existing ones are keyed on *a declared key's
presence* and this one is keyed on *a declared string's syntax*, which no path predicate can express:

```csharp
/// <summary>
/// One feature this build does not honour that is detected by a <b>compiler</b> rather than by a
/// descriptor-shape predicate, so it carries the words without carrying a path.
/// </summary>
/// <remarks>
/// <see cref="UnhonouredFeature{T}"/>'s two-pass tie does not apply: the raw-JSON pass cannot ask
/// whether a string is a well-formed <c>{{…}}</c> template without reimplementing the classifier
/// over <see cref="System.Text.Json.JsonElement"/>, and the typed pass already knows the exact JSON
/// Pointer of the slot it is looking at. So the <em>detection</em> lives where the action is
/// compiled and only the <em>wording</em> lives here — which is what keeps one authority for the
/// words, exactly as the other two shapes do.
/// </remarks>
internal sealed record UnhonouredSlot(string Feature, string Consequence, string Fix);

internal static UnhonouredSlot RawJsonata { get; } = new(
    "JSONata",
    "JSONata transformations are not evaluated yet: the action still runs, but with Alvo's canonical "
    + "event envelope as its body instead of the transformation declared here — a delivery that "
    + "succeeded carrying data you did not declare, which is indistinguishable from a bug in the "
    + "consumer.",
    "Use a '{{...}}' template instead (e.g. \"{{new.title}}\"), which this build does render, or "
    + "remove the transformation and accept the canonical envelope. A partial JSONata "
    + "implementation is deliberately not offered: silently producing a different payload for the "
    + "part it does not implement costs more than this refusal. Tracked in #149.");

internal static UnhonouredSlot UnhonouredAction(string type) => new(
    type,
    $"The '{type}' action is declared in the schema but not implemented in this build, so this hook "
    + $"{ActionConsequence(type)}.",
    ActionFix(type));
```

`ActionConsequence`/`ActionFix` are two three-arm switches so each of the three actions names what
*specifically* does not happen — `function` names that no function runs on any trigger it declares
and that the F4 action set requires it (`alvo-specifikacia.md:330`, deviation 66); `http.call` names
that no request is made and `headersSecretRef` is never read; `entity.update` names that no record is
written and that it lands with automation. The number is **#149**, from Task 1 Step 6.

Then **remove the three `after*` entries** from `HookPoints()`, keeping the three `before*` ones:

```csharp
private static IEnumerable<UnhonouredFeature<EntityDescriptor>> HookPoints() =>
[
    Hook("beforeCreate", "create", hooks => hooks.BeforeCreate, InTransaction),
    Hook("beforeUpdate", "update", hooks => hooks.BeforeUpdate, InTransaction),
    Hook("beforeDelete", "delete", hooks => hooks.BeforeDelete, InTransaction),
];
```

Delete the now-unused `PostCommit` constant and update `HookPoints()`'s `<remarks>`: the per-hook-point
shape was chosen *"so PR5 can shrink this incrementally"* (`:90-96`) and this is the PR that proves
it — three entries leave, three stay, and no author of a `before*` hook sees a changed message.
**Do not touch** the `simple-tasks`/`completed_at` citation at `:114-119`: deviation 77 assigns that
correction to the PR that lifts a `before*` refusal, which is PR5b.

- [x] **Step 4: Reword the two subsystem warnings that stopped being true**

`templates` and `webhooks` are now honoured **from an after-hook** and unhonoured **from
automation**, so their current consequences (`"nothing renders a template, because the automation
actions that would reference one never run"`, `"no event is ever delivered to the endpoint"`) are
false the moment an after-hook references one:

```csharp
new(
    "templates",
    descriptor => descriptor.Templates is { Count: > 0 },
    "a template referenced by an after-hook 'email' action is rendered, but one referenced only "
    + "from an automation rule is not, because no rule is evaluated yet"),
new(
    "webhooks",
    descriptor => descriptor.Webhooks?.Endpoints is { Count: > 0 },
    "an endpoint an after-hook posts to is delivered to, but one referenced only from an automation "
    + "rule never receives anything; and no delivery is signed — 'secretRef' is not read and no "
    + "Standard Webhooks HMAC header is sent, so a receiver cannot yet verify the sender (7.1)"),
```

The `secretRef` half matters: it is a *security* absence an author would otherwise assume was
handled, and `UnhonouredSubsystems` exists exactly for the case where absence is misattributed.
Update `UnhonouredSubsystemsTests`' expected wording — that suite asserts **which blocks the line
names** (`:115-120`), so it will fail until the new text is pinned, which is the tie working.

- [x] **Step 5: Implement `AfterHookCompiler` and thread it through the builder**

`Compile` walks the three `after*` lists, and per hook: compiles `condition` through
`compiler.Compile(source, CelProfile.Condition, schema)`, then compiles the action. Action
compilation is one method per action type, each ≤ 15 lines:

- `webhook` → resolve `endpoint` against `descriptor.Webhooks?.Endpoints` (refuse if unknown);
  if `payload` is present, `JsonataSlot.IsTemplate(payload)` decides between `AlvoTemplate.Parse`
  and the `RawJsonata` refusal.
- `email` → resolve `template` against the descriptor's `templates` (refuse if unknown); parse `to`
  as a template **without** the classifier (it is a plain-string sugar slot — a placeholder-free
  literal address is legitimate); parse the resolved `MessageTemplate`'s `subject` and `body`; if
  `data` is present, run the classifier. `bodyFile` is refused for now, with its own `UnhonouredSlot`
  — nothing in this build reads a file out of a descriptor bundle, and rendering an empty body would
  be the same silent-wrong-output failure the whole table exists for.
- `function`, `http.call`, `entity.update` → `UnhonouredFeatures.UnhonouredAction(type)`.

Every parsed template is validated placeholder-by-placeholder via
`TemplatePlaceholder.TryResolve(placeholder, schema, out refusal)`, and every refusal becomes a
`DescriptorValidationError` at the slot's exact pointer. Then in `PolicyCatalogBuilder.BuildEntity`:

```csharp
private static EntityPolicy BuildEntity(EntityDescriptor? descriptor, EntityBuild build)
{
    // …existing tenantScope / operations / hidden / readOnly…
    var afterHooks = AfterHookCompiler.Compile(
        descriptor, build.Schema, build.Compiler, build.Templates, build.EntityPath, build.Errors);

    return new EntityPolicy(build.Schema.Tenancy, tenantScope, operations, hidden, readOnly, afterHooks);
}
```

`EntityBuild` gains `Templates` (the descriptor's `templates` map, or empty) and `EntityPath`
(`$"entities/{name}"`) if it does not already carry them. Nothing else changes: one pass, one
priming site, one set of errors — which is R11 discharged.

- [x] **Step 6: Prove the example's refusal reason did not silently change**

The hazard deviation 76 describes — a CEL syntax error standing in for a feature refusal — is
created the moment `UnhonouredFeatures` shrinks. PR5a shrinks it, so PR5a must show the hazard did
**not** land here:

```csharp
// MMLib.Alvo.Tests/Descriptor/DescriptorToSchemaMapperTests.cs
/// <summary>
/// PR5a removes the three <c>after*</c> entries from <see cref="UnhonouredFeatures"/>. This fact
/// records why that is safe for the one example with hooks: <c>complex-crm</c> declares
/// <c>beforeCreate</c> and <c>beforeUpdate</c> only, so it stays refused by an unhonoured-feature
/// entry and none of its four uncompilable CEL expressions is exposed.
/// </summary>
/// <remarks>
/// Deviation 76 assigns the example's own five fixes and the refusal-reason strengthening of
/// <see cref="Every_example_marked_not_runnable_really_is_refused"/> to the PR that lifts a
/// <c>before*</c> refusal (PR5b). This fact is what makes that assignment safe rather than assumed.
/// </remarks>
[Fact]
public void The_only_example_with_hooks_declares_no_after_hooks_so_pr5a_exposes_none_of_its_cel_defects()
{
    var hooks = AlvoExamples.ComplexCrm().Entities.Values
        .Select(entity => entity.Hooks)
        .OfType<EntityHooks>()
        .ToList();

    hooks.ShouldNotBeEmpty("complex-crm is the only example with hooks; if that changes, so must this");
    hooks.ShouldAllBe(h => h.AfterCreate == null && h.AfterUpdate == null && h.AfterDelete == null);
}
```

- [x] **Step 7: Run to verify they pass, and prove the refusals discriminate**

Run:
```
dotnet test --project test/MMLib.Alvo.Tests -- --filter-namespace 'MMLib.Alvo.Tests.Events'
dotnet test --project test/MMLib.Alvo.Tests -- --filter-namespace 'MMLib.Alvo.Tests.Descriptor'
```
Expected: PASS. Assert `Build succeeded` first.

Then three mutations, restored immediately:

1. **Make `AfterHookCompiler` accept any `$defs/jsonata` string** (skip the classifier) → both
   raw-JSONata facts go **red**, and `A_template_webhook_payload_applies` stays green, so the fact is
   about the classification rather than about JSONata being mentioned.
2. **Compile after-hook conditions in `CelProfile.Rule` instead of `Condition`** →
   `An_after_hook_condition_compiles_in_the_condition_profile_so_changed_is_legal` goes **red** with
   `CelCompiler.cs:97`'s non-`Bool`/`changed` refusal. Without this, the profile choice is untested.
3. **Return `EntityAfterHooks.None` unconditionally from `Compile`** →
   `The_after_hook_catalog_is_reachable_from_the_one_primed_policy_catalog` goes **red**, which is
   R11's structural fact proving it is not vacuous.

- [x] **Step 8: ring0 + commit**

```bash
scripts/test-ring0
git add src/MMLib.Alvo/Rules/ src/MMLib.Alvo/Events/Internal/AfterHookCompiler.cs \
        src/MMLib.Alvo/Descriptor/Internal/ test/MMLib.Alvo.Tests/
git commit -m "feat(events): compile after-hooks into the policy catalog and refuse JSONata at apply"
```

---

### Task 8: The action executor — `webhook` and `email`, and nothing else

**DONE.** 17 facts in `EventActionExecutorTests` plus 2 real-socket facts in `WebhookDeliveryTests`;
ring0 green at 2 488 tests. `IEmailSender` + `AlvoMailMessage` are the only public surface added, and the
core's baseline did not move — everything else is `internal`.

**The two judgement calls, decided:**

1. **A failed delivery is retried, and nothing is classified.** A 500, a 404, a 503, a connection refused,
   a DNS failure and a timeout all throw and all get identical treatment. Nothing at delivery time can tell a
   permanently wrong endpoint from one whose deployment is thirty seconds from finishing, and a per-status
   "permanent" verdict needs somewhere to route the abandoned event — which 7.1 owns and this build does not
   have. **The ceiling lives in exactly one place**: the `maxAttempts` the dispatcher passes to
   `IOutboxStore.ClaimAsync`, whose subquery filters `attempts < @max_attempts`. It is reachable *because*
   `ReleaseAsync` deliberately does not roll the count back, so `WebhookDelivery` adds no retry of its own —
   an inner loop would be a second invisible multiplier and would hold a claimed entry past its lease while
   sleeping. **"Abandoned" is observable, not silent**: at the ceiling the entry stops being claimed but is
   never deleted or moved, so it sits in `alvo_outbox` with `dispatched_at IS NULL` and is countable and
   inspectable, `alvo.events.failed` has one increment per attempt, and Task 9's `PoisonEvent` is the loud
   Error line naming the event id and type. **One conversion is deliberate**: `HttpClient` reports *its own*
   timeout as `OperationCanceledException`, the same type the host's shutdown raises, so `WebhookDelivery`
   turns a timeout into a `TimeoutException` when the caller's token is *not* cancelled. Leaving the two
   indistinguishable is how a slow receiver reads as a shutdown and silently ends the pump; both directions
   are pinned by a fact.
2. **The action log records descriptor coordinates and event identity, and never a rendered value.**
   `ActionExecuted` carries the hook's JSON pointer, the action `type`, the event id and the event type — and
   not the rendered body, the recipient, the subject or the endpoint URL. The reasoning is D7's, taken one
   step further: the envelope carries the **unmasked** post-image, and D7 accepted that disclosure on the
   ground that the endpoint is *declared in the same descriptor by the same author* as the `hidden` rule.
   A log line has no such author. Logging the rendered value would take a `hidden` field out of the one place
   the design accepted it going and put it into whatever ships logs, which nobody declared and no author
   chose — so the ground D7 stands on does not extend to the log, and the log stops at the join key. The event
   id is that key: the payload is stored once, in the `alvo_outbox` row, under that table's retention rather
   than a log pipeline's. **`ConsoleEmailSender` is the one deliberate exception and not an exception to the
   rule** — for a console provider the log *is* the mailbox, and a redacted body would deliver nowhere and
   report nothing. That is exactly why its line has to say `development`, which a fact pins.

**Six deliberate deviations from this task as written, each for a reason in the code:**

1. **`CompiledAction` gained the resolved `WebhookEndpoint`**, instead of `WebhookDelivery` resolving the URL
   "from the primed descriptor's `webhooks.endpoints`" as this task's Step 3 said. There **is no primed
   descriptor** at run time — only the primed `PolicyCatalog` — so a delivery-time lookup would have needed a
   second, independently primed holder, which is precisely R11's failure and would let an action post one
   apply's URL while rendering another apply's templates. Task 7's own doctrine already settled it:
   *"everything is resolved here so that nothing is resolved at delivery."* `A_webhook_action_posts_to_the_url_its_endpoint_declared`
   is the behavioural half.
2. **The action-type vocabulary moved out of `AfterHookCompiler` into a new
   `Events/Internal/ActionVocabulary.cs`** (`ActionType.NameOf` plus `ActionSlot`). Adding the executor as a
   second consumer of `AfterHookCompiler.ActionTypeName` turned R11's structural fact
   `The_hook_compiler_is_reached_from_the_policy_catalog_builder_and_nowhere_else` **red** — it allows exactly
   two files to mention the type. The fact was left exactly as coarse as Task 7 wrote it and the mapping was
   extracted instead: a guard that can be narrowed to fit new code is not a guard, and the mapping was never
   the compiler's work — it is the descriptor's vocabulary, which is why the slot names belong beside it.
   One file more than this task listed.
3. **`ActionSlot` is a shared authority for the template-dictionary keys**, read by the executor and written
   by the compiler, because two spellings of a key fail neither the build nor the apply: the slot simply has
   no entry, and the executor renders an empty recipient or posts the canonical envelope where a payload was
   declared — a wrong delivery that looks exactly like a successful one.
4. **`EventLog` carries only the two entries this task writes.** `ActionFailed`, `PoisonEvent` and
   `DispatcherStopped` land in the same file **in Task 9**, with their callers and their facts; declaring them
   here would be unreferenced code with untested wording, which is the opposite of the point.
5. **The executor writes `ActionExecuted`, not the dispatcher** — it is the one place that knows an action
   *ran*, and the entry goes after the await, which is what makes `An_action_that_failed_writes_no_execution_log_entry`
   true. **Task 9 must not log it a second time**, or the execution-log criterion counts every action twice.
6. **`CapturingLogger` was widened rather than a second `RecordingLoggerProvider` added** — its own remarks
   argue exactly that, and `Warnings` is now a view over `Entries`, so no existing fact changed meaning.

**One mutation came back green and the test was not at fault.** Renaming `ActionSlot.To` to `"recipient"`
left all 17 facts passing, because the constant feeds **both** the compiler's write and the executor's read —
a symmetric rename is invisible *by construction*, which is the single-authority property working rather than
a hole in the suite. The discriminating mutation has to be **one-sided**: making the executor read a literal
`"recipient"` while the compiler still writes `to` turns both recipient facts red.

**Two facts carry no mutation of their own, deliberately.**
`A_webhook_receives_the_unmasked_record_and_that_is_documented` is D7's named pin and there is no masking code
to mutate — it exists so that the disclosure is a decision on the record; it does go red when the endpoint
stops being carried. `Every_event_counter_is_published_on_the_one_meter_under_its_documented_name` is a naming
pin, because the increments are Task 9's; it discriminates against a renamed instrument and against a counter
created on a second meter, which is the failure that would make Task 10's listener silently read zero.

Decision D5: PR5a's after-hook action set is `webhook` + `email`-to-console. `email` is not optional
— `templates.subject`/`body` and `email.to` are the only slots that exercise the template engine's
plain-string sugar, and deviation 64's consequence is unreachable without them.

**Files:**
- Create: `src/MMLib.Alvo.Abstractions/Events/IEmailSender.cs`
- Create: `src/MMLib.Alvo/Events/Internal/EventActionExecutor.cs`
- Create: `src/MMLib.Alvo/Events/Internal/WebhookDelivery.cs`
- Create: `src/MMLib.Alvo/Events/Internal/ConsoleEmailSender.cs`
- Create: `src/MMLib.Alvo/Events/Internal/EventLog.cs`
- Create: `src/MMLib.Alvo/Events/Internal/AlvoEventMetrics.cs`
- Test: `test/MMLib.Alvo.Tests/Events/EventActionExecutorTests.cs`
- Test: `test/MMLib.Alvo.Api.Tests/Events/WebhookDeliveryTests.cs`
- Modify: `test/MMLib.Alvo.Abstractions.Tests/PublicApi.MMLib.Alvo.Abstractions.verified.txt`,
  `test/MMLib.Alvo.Tests/PublicApi.MMLib.Alvo.verified.txt`

**Interfaces:**
- Consumes: `CompiledAction`, `AlvoTemplate` (Tasks 6–7); `AlvoEvent`; `IHttpClientFactory`;
  `WebhookEndpoint`; `ILogger`.
- Produces:
  ```csharp
  namespace MMLib.Alvo.Events;

  public sealed record AlvoMailMessage(string To, string Subject, string Body);

  /// <summary>The mail provider port. A console dev provider ships; SMTP does not (F3).</summary>
  public interface IEmailSender
  {
      Task SendAsync(AlvoMailMessage message, CancellationToken cancellationToken = default);
  }

  // internal
  internal sealed class EventActionExecutor
  {
      internal Task ExecuteAsync(CompiledAfterHook hook, AlvoEvent @event, CancellationToken ct);
  }

  internal static class AlvoEventMetrics
  {
      internal const string MeterName = "MMLib.Alvo.Events";
      internal static Counter<long> Dispatched { get; }   // alvo.events.dispatched
      internal static Counter<long> Filtered { get; }     // alvo.events.filtered
      internal static Counter<long> Failed { get; }       // alvo.events.failed
  }
  ```
  `EventActionExecutor` throws on a delivery failure; the dispatcher (Task 9) is what contains it.
  That split is deliberate: an executor that swallowed a failure could never be retried.

- [x] **Step 1: Write the failing tests**

```csharp
// MMLib.Alvo.Tests/Events/EventActionExecutorTests.cs
[Fact]
public async Task A_webhook_action_posts_the_canonical_envelope_when_no_payload_is_declared()
{
    var receiver = new RecordingWebhookReceiver();
    var executor = Subject(receiver);

    await executor.ExecuteAsync(WebhookHook(payload: null), SampleEvent(), default);

    AlvoEventJson.Read(receiver.Bodies.ShouldHaveSingleItem()).ShouldBe(SampleEvent());
}

[Fact]
public async Task A_webhook_action_posts_its_rendered_template_when_one_is_declared()
{
    var receiver = new RecordingWebhookReceiver();

    await Subject(receiver).ExecuteAsync(
        WebhookHook(payload: "{{new.title}}"),
        SampleEvent(record: Record(("title", "Big deal"))),
        default);

    receiver.Bodies.ShouldHaveSingleItem().ShouldBe("Big deal");
}

[Fact]
public async Task An_email_action_renders_its_recipient_subject_and_body_from_the_envelope()
{
    var mail = new RecordingEmailSender();

    await Subject(mail: mail).ExecuteAsync(
        EmailHook(to: "{{new.owner_email}}", subject: "Deal won: {{new.title}}", body: "{{new.amount}}"),
        SampleEvent(record: Record(("owner_email", "o@x.z"), ("title", "Big deal"), ("amount", 1200m))),
        default);

    var sent = mail.Messages.ShouldHaveSingleItem();
    sent.To.ShouldBe("o@x.z");
    sent.Subject.ShouldBe("Deal won: Big deal");
    sent.Body.ShouldBe("1200");
}

[Fact]
public async Task A_literal_recipient_with_no_placeholder_is_a_legitimate_address()
{
    var mail = new RecordingEmailSender();

    await Subject(mail: mail).ExecuteAsync(EmailHook(to: "ops@example.com"), SampleEvent(), default);

    mail.Messages.ShouldHaveSingleItem().To.ShouldBe("ops@example.com");
}

/// <summary>
/// A failure must reach the caller, because the dispatcher's retry is the only thing that makes
/// delivery at-least-once. An executor that logged and returned would turn every transient 503 into
/// a silently dropped event.
/// </summary>
[Fact]
public async Task A_refused_delivery_throws_so_the_dispatcher_can_retry_it()
{
    var receiver = new RecordingWebhookReceiver { Status = HttpStatusCode.ServiceUnavailable };

    await Should.ThrowAsync<Exception>(
        () => Subject(receiver).ExecuteAsync(WebhookHook(payload: null), SampleEvent(), default));
}

/// <summary>
/// Decision D7, named rather than left in a paragraph: the envelope carries the <b>unmasked</b>
/// post-image, so a <c>hidden</c> field reaches a descriptor-declared endpoint.
/// </summary>
/// <remarks>
/// Accepted in F3 because an after-hook condition reading <c>old.commission_note</c> or
/// <c>changed(commission_note)</c> must see every field — <c>hidden</c> is a per-caller read mask,
/// not a data classification — and because the endpoint is declared in the same descriptor by the
/// same author as the <c>hidden</c> rule, never caller-supplied. Per-endpoint field projection is
/// filed as #152. This fact exists so the disclosure is a decision on the record: if it
/// ever becomes wrong, this is the test that has to change, deliberately.
/// </remarks>
[Fact]
public async Task A_webhook_receives_the_unmasked_record_and_that_is_documented()
{
    var receiver = new RecordingWebhookReceiver();

    await Subject(receiver).ExecuteAsync(
        WebhookHook(payload: null),
        SampleEvent(record: Record(("commission_note", "12%"))),
        default);

    AlvoEventJson.Read(receiver.Bodies.ShouldHaveSingleItem())
        .Data.Record!["commission_note"].ShouldBe("12%");
}

/// <summary>
/// The console provider is a <em>dev</em> provider and says so, so nobody ships it believing mail is
/// going out. There is no SMTP sender in F3 and no mail service in compose.
/// </summary>
[Fact]
public async Task The_console_sender_writes_the_whole_message_and_names_itself_a_dev_provider()
{
    var logs = new RecordingLoggerProvider();

    await new ConsoleEmailSender(logs.CreateLogger<ConsoleEmailSender>())
        .SendAsync(new AlvoMailMessage("o@x.z", "s", "b"));

    var line = logs.Entries.ShouldHaveSingleItem().Message;
    line.ShouldContain("o@x.z");
    line.ShouldContain("development");
}
```

`RecordingWebhookReceiver` is an `HttpMessageHandler` subclass recording bodies and returning
`Status` (default `200`) — no real socket, so this suite stays in the fast ring.
`test/MMLib.Alvo.Api.Tests/Events/WebhookDeliveryTests.cs` adds one fact over a real loopback
`HttpListener` proving the content type is `application/json` and the method is `POST`.

- [x] **Step 2: Run to verify they fail**

Run: `dotnet test --project test/MMLib.Alvo.Tests -- --filter-class '*EventActionExecutorTests*'`
Expected: FAIL — `EventActionExecutor` does not exist.

- [x] **Step 3: Implement**

`EventActionExecutor.ExecuteAsync` is a three-arm switch, one private method per arm, plus the
default arm which throws `InvalidOperationException` naming the type — unreachable from a
descriptor, because Task 7 refuses the other three at apply, and the arm exists so a *host*-built
catalog cannot reach a silent no-op:

```csharp
internal Task ExecuteAsync(CompiledAfterHook hook, AlvoEvent @event, CancellationToken ct) =>
    hook.Action.Action switch
    {
        WebhookAction webhook => DeliverAsync(webhook, hook.Action, @event, ct),
        EmailAction email => SendAsync(email, hook.Action, @event, ct),
        _ => throw UnreachableAction(hook),
    };
```

`WebhookDelivery` resolves the endpoint's URL from the primed descriptor's `webhooks.endpoints`,
POSTs `Content-Type: application/json`, and calls `EnsureSuccessStatusCode()`. Its `<remarks>`
records what is **not** there and why: no HMAC signature, no `secretRef` read, no retry of its own
(the outbox's claim/release is the retry), and no DLQ — all 7.1, all named in the `webhooks`
subsystem warning Task 7 reworded so an author reads it rather than infers it.

`AlvoEventMetrics` is a static `Meter` with three counters, each with an explicit unit and
description. `EventLog` holds every `[LoggerMessage]` partial — `ActionExecuted` (Information; the
execution-log entry D6 defines), `ActionFailed` (Warning, with the attempt count), `PoisonEvent`
(Error, at the attempt ceiling, naming the event id and type — the loud log that is PR5a's DLQ
stand-in), `DispatcherStopped` (Error).

`ConsoleEmailSender`'s single log line must contain the word `development` — pinned by the fact
above — because the failure mode this provider has is an operator believing mail is being sent.

- [x] **Step 4: Run to verify they pass**

Run:
```
dotnet test --project test/MMLib.Alvo.Tests -- --filter-class '*EventActionExecutorTests*'
dotnet test --project test/MMLib.Alvo.Api.Tests -- --filter-class '*WebhookDeliveryTests*'
```
Expected: PASS. Assert `Build succeeded` first.

- [x] **Step 5: Prove the retry contract discriminates**

**Swallow the failure** in `WebhookDelivery` (replace `EnsureSuccessStatusCode()` with nothing) and
confirm `A_refused_delivery_throws_so_the_dispatcher_can_retry_it` goes **red**. Restore. This is the
one mutation that matters here: an executor that cannot fail makes at-least-once delivery a claim
with nothing behind it, and every downstream chaos assertion would pass over it.

- [x] **Step 6: Accept the baselines, ring0, commit**

```bash
dotnet test --project test/MMLib.Alvo.Abstractions.Tests -- --filter-class '*PublicApi*'
dotnet test --project test/MMLib.Alvo.Tests -- --filter-class '*PublicApi*'
# IEmailSender + AlvoMailMessage (Abstractions). Accept; dispatch alvo-snapshot-judge.
scripts/test-ring0
git add src/MMLib.Alvo.Abstractions/Events/IEmailSender.cs src/MMLib.Alvo/Events/Internal/ \
        test/MMLib.Alvo.Tests/ test/MMLib.Alvo.Api.Tests/ \
        test/MMLib.Alvo.Abstractions.Tests/PublicApi.MMLib.Alvo.Abstractions.verified.txt
git commit -m "feat(events): run after-hook webhook and email actions, with metrics and an action log"
```

---

### Task 9: The dispatcher — gated on `AlvoBootState`, containing every failure

**DONE.** 11 facts in `MMLib.Alvo.Tests.Events.OutboxDispatcherTests`, 13 in `EventSubscriptionsTests`, 5 new
`SettledAsync` facts in `AlvoBootStateTests`, and 6 end-to-end facts in
`MMLib.Alvo.Host.Tests.Events.OutboxDispatcherTests` over a real write, a real claim and a real (failing)
delivery. ring0 green at 2 526 tests. `AlvoEventOptions` is the only public surface added — `SettledAsync`
stayed internal and does **not** appear in the moved core baseline.

**The one finding that changed where a fact lives, and it is a vacuity finding.** This task's Step 2 specified
the readiness fact against a running host, with `HoldTheBootAtStageThree` + `RegisterTheDispatcherFirst`. **That
shape is structurally vacuous and cannot be fixed by ordering the registrations.** `AlvoBootService` is an
`IHostedLifecycleService` that does *all* of its work in `StartingAsync` (`AlvoBootService.cs:122`), and the host
runs **every** service's `StartingAsync` before **any** service's `StartAsync` — so holding the boot mid-flight
holds the whole `StartAsync` phase with it and the dispatcher's `ExecuteAsync` has not begun at all. The fact
would pass because the pump never ran, which is precisely the failure a background-service test invites, and no
registration order changes it. The gate is therefore pinned in the **core** suite, where the pump really is
running while the state is `Pending`: `The_pump_claims_nothing_before_the_boot_reports_ready` starts the service,
waits fifteen poll intervals, asserts **zero claims**, then publishes `Ready` and waits for all three events to
be dispatched — so the "it did not run" half and the "it was alive" half are one fact. The ordering half the
plan wanted is met by construction rather than by a flag: there is no registration and no boot service in that
fact at all. The host suite says so in its own remarks, so the absence there reads as a decision.

**What the gate is actually worth, since the standalone host's ordering already settles it.** An unprimed
`PolicyCatalog` knows no entity, so every event would match no hook, count as `filtered` and be **retired** —
silent, permanent loss that no retry recovers, because a filtered event is deliberately not retried. That is why
the gate is not decoration on .NET 10: it covers the boot that *refused* (`A_boot_that_refused_leaves_the_pump_claiming_nothing`),
an embedded host that primes on its own schedule, and any future change that moves priming out of the startup
phase. `A_batch_with_no_primed_catalog_retires_nothing` pins the fail-closed direction one layer deeper: the
invariant throws, the entry is **released** rather than marked dispatched, and the refusal names the catalog.

**Seven deliberate deviations from this task as written, each for a reason in the code:**

1. **Registration lives in a new `src/MMLib.Alvo/Events/Setup.cs` (`AddAlvoEvents`)**, called from `AddAlvo`,
   not inline in `AlvoServiceCollectionExtensions`. Every other feature in the core registers that way
   (`Auth/Setup.cs`, `Rules/Setup.cs`, `Expressions/Setup.cs`, `Api`), and §0 principle 9 is vertical slice —
   the alternative was the ninth feature's registrations in a file that already reads as a table of contents.
2. **`AlvoEventOptionsConfiguration` replaces `AlvoEventOptionsValidation`, and it binds as well as validates.**
   The plan's snippet registered `ValidateDataAnnotations()` and *nothing that binds the section*, so
   `Alvo:Events:*` would have had no effect at all — and the refusal has to quote the key an operator sets, which
   makes the binder's key set and the validator's key set one list or two lists that drift. This is exactly
   `AlvoSchemaOptionsConfiguration`'s shape and reason, including the optional `IConfiguration` through a factory
   (never `BuildServiceProvider`), so a plain console host embedding Alvo still gets the defaults.
3. **`ValidateDataAnnotations()` is not called.** A `[Range]` message names the property and not the
   configuration key, so it would add a second, worse message to every refusal for one mistake. Global
   Constraints allow either that or `IValidateOptions<T>`; this takes the second, and every refusal names the
   key, its `Alvo__…` environment spelling, and a value that would have worked.
4. **`EventSubscriptions.Matching` takes an `ILogger`.** The specified signature had none and the specified
   behaviour ("logged once at Debug") is unreachable without one.
5. **`EventLog` gained a fourth entry, `ConditionRefusedTheHook` (Debug)**, beside the three Task 8 assigned
   here. Task 8's note lists what must land, not a cap, and this is the plan's own "diagnosable without becoming
   the per-event noise the whole criterion is about".
6. **The event type's third segment is parsed against a table in the core, not against a shared constant.** The
   emitting `OutboxEventFactory.Suffix` is `internal` to the EF driver and unreachable from the core, so sharing
   the vocabulary would mean widening a port for three string literals. The pairing is held **behaviourally** by
   Task 10's end-to-end criteria, which drive a real write through a real dispatcher; an unparseable type selects
   **nothing** (five `[Theory]` rows), which is the fail-closed direction if they ever disagree.
7. **`PumpOneBatchAsync` is `internal` from the start**, because Task 10 Step 3 needs exactly that for a
   deterministic drain, and every fact here that does not need the loop drives it directly rather than sleeping.

**One thing this task deliberately does not do.** No counter is asserted here. The three increments are written
(`Dispatched` after the retirement, `Filtered` once per event, `Failed` once per attempt) but observing them
needs the `RecordingMeterListener` **Task 10 owns**, and a second listener written here would be the thing that
makes the execution-log criterion count twice. `ActionExecuted` is likewise written only by the executor — Task 8's
instruction, honoured.

**Provider obligation this task creates, stated the way deviation 60 states `IRuntimeSchemaWriter`'s.** The
dispatcher **requires `IOutboxStore` to resolve**, so from this commit a database provider must supply that port
to get a running host. `AddRelationalProvider` registers it, so no shipped provider is affected; the cost falls on
a non-EF or dynamic-storage provider (F7), which would otherwise discover it as a DI failure at startup. It is
recorded in `OutboxDispatcher`'s own remarks, and **Task 13 owns adding it to
`docs/architecture/package-boundary.md`'s "What a database provider must implement to boot" list** — that file is
untouched here on purpose.


Three hosting facts shape this task and none of them is optional. `BackgroundService.ExecuteAsync`
runs **entirely** off the startup thread on .NET 10, so readiness cannot be expressed by ordering.
`BackgroundServiceExceptionBehavior` defaults to `StopHost`, so one poison event would take down a
host serving HTTP. The host **blocks in `StopAsync`** for up to 30 s waiting for `ExecuteAsync`.

**Files:**
- Create: `src/MMLib.Alvo/Events/AlvoEventOptions.cs`
- Create: `src/MMLib.Alvo/Events/Internal/AlvoEventOptionsValidation.cs`
- Create: `src/MMLib.Alvo/Events/Internal/EventSubscriptions.cs`
- Create: `src/MMLib.Alvo/Events/Internal/OutboxDispatcher.cs`
- Modify: `src/MMLib.Alvo/Migrations/AlvoBootState.cs`
- Modify: `src/MMLib.Alvo/AlvoServiceCollectionExtensions.cs`
- Test: `test/MMLib.Alvo.Tests/Migrations/AlvoBootStateTests.cs`
- Test: `test/MMLib.Alvo.Tests/Events/EventSubscriptionsTests.cs`
- Test: `test/MMLib.Alvo.Host.Tests/Events/OutboxDispatcherTests.cs`
- Modify: `test/MMLib.Alvo.Tests/PublicApi.MMLib.Alvo.verified.txt`

**Interfaces:**
- Consumes: `IOutboxStore` (Task 5), `EntityAfterHooks`/`CompiledAfterHook` (Task 7),
  `EventActionExecutor`/`AlvoEventMetrics`/`EventLog` (Task 8), `IPolicyCatalogProvider`,
  `IPredicateEvaluator`, `AlvoBootState`, `TimeProvider`.
- Produces:
  ```csharp
  public sealed class AlvoEventOptions
  {
      public bool Enabled { get; set; } = true;
      public TimeSpan PollInterval { get; set; } = TimeSpan.FromSeconds(1);
      public int BatchSize { get; set; } = 100;
      public int MaxAttempts { get; set; } = 10;
      public TimeSpan ClaimLease { get; set; } = TimeSpan.FromMinutes(5);
  }

  // internal
  internal sealed class OutboxDispatcher : BackgroundService;

  internal static class EventSubscriptions
  {
      internal static IReadOnlyList<CompiledAfterHook> Matching(
          PolicyCatalog catalog, AlvoEvent @event, IPredicateEvaluator evaluator, AlvoContext context);
  }

  // AlvoBootState gains, internal so no public baseline moves:
  internal Task<AlvoBootPhase> SettledAsync(CancellationToken cancellationToken);
  ```

- [x] **Step 1: Write the failing `AlvoBootState` and subscription tests**

```csharp
// MMLib.Alvo.Tests/Migrations/AlvoBootStateTests.cs
/// <summary>
/// The readiness signal a <see cref="BackgroundService"/> can await.
/// </summary>
/// <remarks>
/// .NET 10 runs all of <c>ExecuteAsync</c> off the startup thread, so "not before the schema is
/// primed" is inexpressible as registration order and <c>await Task.Yield()</c> as a first line is
/// dead code. The state has to be awaited, and this member is internal on purpose: only the
/// dispatcher needs it, and a public awaitable would foreclose the state's shape for #141.
/// </remarks>
[Fact]
public async Task Settled_completes_when_a_project_reports_ready()
{
    var state = new AlvoBootState();
    var settled = state.SettledAsync(TestContext.Current.CancellationToken);
    settled.IsCompleted.ShouldBeFalse();

    state.Ready("demo", appliedRevision: 1);

    (await settled).ShouldBe(AlvoBootPhase.Ready);
}

[Fact]
public async Task Settled_completes_with_failed_when_the_boot_refused()
{
    var state = new AlvoBootState();
    state.Failed("stage 0 refused");

    (await state.SettledAsync(TestContext.Current.CancellationToken)).ShouldBe(AlvoBootPhase.Failed);
}

[Fact]
public async Task Settled_returns_immediately_when_the_boot_already_finished()
{
    var state = new AlvoBootState();
    state.Ready("demo", 1);

    state.SettledAsync(TestContext.Current.CancellationToken).IsCompleted.ShouldBeTrue();
    (await state.SettledAsync(TestContext.Current.CancellationToken)).ShouldBe(AlvoBootPhase.Ready);
}

[Fact]
public async Task Settled_observes_its_cancellation_token_so_shutdown_never_waits_thirty_seconds()
{
    var state = new AlvoBootState();
    using var cancellation = new CancellationTokenSource();
    var settled = state.SettledAsync(cancellation.Token);

    await cancellation.CancelAsync();

    await Should.ThrowAsync<OperationCanceledException>(() => settled);
}
```

```csharp
// MMLib.Alvo.Tests/Events/EventSubscriptionsTests.cs
/// <summary>
/// The condition is part of the <b>subscription</b>, not the run's first step (base design
/// <c>:583-592</c>).
/// </summary>
/// <remarks>
/// §3.3 records the consequence of getting this wrong as a documented Directus defect: thousands of
/// log entries for runs that abort immediately on their condition. Alvo has the advantage by
/// construction — the CEL <c>Condition</c> profile is compiled at apply time, so the predicate is
/// available here.
/// </remarks>
[Fact]
public void An_event_selects_only_the_hooks_of_its_own_operation()
{
    var matched = EventSubscriptions.Matching(Catalog, UpdatedEvent, Evaluator, Context);

    matched.ShouldAllBe(hook => hook.Path.Contains("afterUpdate"));
}

[Fact]
public void An_event_for_an_entity_with_no_hooks_selects_nothing()
    => EventSubscriptions.Matching(Catalog, VehicleCreatedEvent, Evaluator, Context).ShouldBeEmpty();

[Fact]
public void A_hook_whose_condition_is_false_is_not_selected()
    => EventSubscriptions.Matching(Catalog, StageStillLeadEvent, Evaluator, Context).ShouldBeEmpty();

[Fact]
public void A_hook_with_no_condition_is_always_selected()
    => EventSubscriptions.Matching(Catalog, UnconditionalEvent, Evaluator, Context)
        .ShouldHaveSingleItem();

/// <summary>
/// A condition that throws must not select the hook, and must not take the batch down either: a
/// broken predicate is a fail-closed refusal, exactly as an unprimed catalog denies every operation.
/// </summary>
[Fact]
public void A_condition_that_throws_selects_nothing_rather_than_everything()
    => EventSubscriptions.Matching(Catalog, EventWhoseRecordIsMissingAField, ThrowingEvaluator, Context)
        .ShouldBeEmpty();
```

- [x] **Step 2: Write the failing dispatcher tests**

```csharp
// MMLib.Alvo.Host.Tests/Events/OutboxDispatcherTests.cs
/// <summary>
/// The gate is on the boot <b>state</b>, not on registration order — proven by a fact that would
/// still pass if the dispatcher were registered first.
/// </summary>
[Fact]
public async Task The_dispatcher_does_not_claim_anything_before_the_boot_reports_ready()
{
    await using var world = await AlvoHostWorld.StartAsync(new HostWorldSetup
    {
        HoldTheBootAtStageThree = true,
        RegisterTheDispatcherFirst = true,
    });
    await world.SeedOutboxAsync(count: 3);

    await world.WaitAtLeastAsync(world.EventOptions.PollInterval * 3);

    world.UndispatchedCountAsync().Result.ShouldBe(3);
    world.ReleaseTheBoot();
    await world.WaitUntilDispatchedAsync(count: 3);
}

/// <summary>
/// Deviation 71: one poison event must not stop a host serving HTTP.
/// </summary>
/// <remarks>
/// <c>HostOptions.BackgroundServiceExceptionBehavior</c> defaults to <c>StopHost</c>, and from
/// .NET 11 <c>RunAsync</c>/<c>StopAsync</c> also throw and the process exits non-zero — with the
/// documented recommended action being "do nothing", because a failing app should fail. So the
/// containment belongs inside the loop, per batch and per event, never at the host's edge.
/// </remarks>
[Fact]
public async Task A_delivery_that_always_throws_does_not_stop_the_host()
{
    await using var world = await AlvoHostWorld.StartAsync(new HostWorldSetup { WebhooksAlwaysThrow = true });
    await world.SeedOutboxAsync(count: 1);

    await world.WaitUntilAttemptsReachAsync(world.EventOptions.MaxAttempts);

    (await world.Client.GetAsync("/health/ready")).StatusCode.ShouldBe(HttpStatusCode.OK);
    world.Logs.ShouldContainOneEntryMatching(entry =>
        entry.Level == LogLevel.Error && entry.Message.Contains("attempt"));
}

/// <summary>
/// The poison event stops occupying the pump once it hits the ceiling, so an unrelated event queued
/// behind it is still delivered. PR5a's stand-in for a DLQ is an attempt ceiling plus a loud log
/// (7.1 owns the queue), and this is what makes the stand-in adequate rather than merely present.
/// </summary>
[Fact]
public async Task A_poison_event_does_not_block_the_events_queued_behind_it()
{
    await using var world = await AlvoHostWorld.StartAsync(new HostWorldSetup { FirstWebhookAlwaysThrows = true });
    await world.SeedOutboxAsync(count: 3);

    await world.WaitUntilDispatchedAsync(count: 2);

    (await world.UndispatchedCountAsync()).ShouldBe(1);
}

[Fact]
public async Task A_shutdown_returns_promptly_rather_than_waiting_out_the_shutdown_timeout()
{
    await using var world = await AlvoHostWorld.StartAsync();
    await world.SeedOutboxAsync(count: 1);
    await world.WaitUntilDispatchedAsync(count: 1);

    var elapsed = await world.MeasureStopAsync();

    elapsed.ShouldBeLessThan(
        TimeSpan.FromSeconds(5),
        "the host blocks in StopAsync waiting for ExecuteAsync, with a 30 s ShutdownTimeout, so the "
        + "loop must observe its cancellation token promptly");
}

[Fact]
public async Task The_dispatcher_can_be_switched_off_entirely()
{
    await using var world = await AlvoHostWorld.StartAsync(
        new HostWorldSetup { Configuration = [new("Alvo:Events:Enabled", "false")] });
    await world.SeedOutboxAsync(count: 1);

    await world.WaitAtLeastAsync(TimeSpan.FromSeconds(2));

    (await world.UndispatchedCountAsync()).ShouldBe(1);
}

[Fact]
public async Task A_batch_size_of_zero_is_refused_at_startup_naming_the_key_and_a_usable_value()
{
    var refusal = await Should.ThrowAsync<OptionsValidationException>(
        () => AlvoHostWorld.StartAsync(
            new HostWorldSetup { Configuration = [new("Alvo:Events:BatchSize", "0")] }));

    refusal.Message.ShouldContain("Alvo:Events:BatchSize");
    refusal.Message.ShouldContain("1");
}
```

- [x] **Step 3: Run to verify they fail**

Run:
```
dotnet test --project test/MMLib.Alvo.Tests -- --filter-class '*AlvoBootStateTests*'
dotnet test --project test/MMLib.Alvo.Host.Tests -- --filter-class '*OutboxDispatcherTests*'
```
Expected: FAIL — `SettledAsync` and `OutboxDispatcher` do not exist.

- [x] **Step 4: Implement `SettledAsync`, then the dispatcher**

On `AlvoBootState`, beside the existing snapshot publication:

```csharp
private readonly TaskCompletionSource<AlvoBootPhase> _settled =
    new(TaskCreationOptions.RunContinuationsAsynchronously);

internal Task<AlvoBootPhase> SettledAsync(CancellationToken cancellationToken) =>
    _settled.Task.WaitAsync(cancellationToken);

private void Publish(Func<BootSnapshot, BootSnapshot> transition)
{
    ImmutableInterlocked.Update(ref _snapshot, transition);

    if (Current.Phase is not AlvoBootPhase.Pending)
    {
        _settled.TrySetResult(Current.Phase);
    }
}
```

`RunContinuationsAsynchronously` matters: without it the dispatcher's loop would begin **on the boot
thread**, inside `StartingAsync`, which is the exact coupling this whole mechanism exists to remove.
`TrySetResult` after the interlocked update, never before, so a waiter that wakes reads a settled
snapshot. `WaitAsync(cancellationToken)` is what makes the shutdown fact above possible.

The dispatcher:

```csharp
protected override async Task ExecuteAsync(CancellationToken stoppingToken)
{
    try
    {
        await PumpUntilStoppedAsync(stoppingToken).ConfigureAwait(false);
    }
    catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
    {
    }
    catch (Exception failure)
    {
        EventLog.DispatcherStopped(_logger, failure);
    }
}

private async Task PumpUntilStoppedAsync(CancellationToken stoppingToken)
{
    if (!_options.Value.Enabled) return;
    if (await _boot.SettledAsync(stoppingToken).ConfigureAwait(false) is not AlvoBootPhase.Ready) return;

    await _store.EnsureAsync(stoppingToken).ConfigureAwait(false);

    while (!stoppingToken.IsCancellationRequested)
    {
        if (await PumpOneBatchAsync(stoppingToken).ConfigureAwait(false) == 0)
        {
            await Task.Delay(_options.Value.PollInterval, _time, stoppingToken).ConfigureAwait(false);
        }
    }
}
```

**One shape rule from spike Q5, on this loop.** `PumpOneBatchAsync` may not open a transaction that
reads before it writes: that is the single shape that reaches R5's SQLite mechanism, and under WAL it
fails unretryably (`SQLITE_BUSY_SNAPSHOT`, `Extended=517`) after burning the full 30 s retry loop,
while in the shipped journal mode it fails the *request path* instead. Claim, mark and release are each
one autocommit statement through `IOutboxStore`, and nothing here wraps them in a transaction to be
tidy. The shipped SQLite registration needs no change for this — measured — and must not get one.

`PumpOneBatchAsync` claims, then per entry: deserialize, `EventSubscriptions.Matching`, and either
increment `Filtered` **once** and `MarkDispatchedAsync` (no execution-log entry — D6), or run every
matched action, log one `ActionExecuted` per action, increment `Dispatched`, and
`MarkDispatchedAsync`. A per-entry `try/catch` increments `Failed`, logs `ActionFailed`, calls
`ReleaseAsync`, and logs `PoisonEvent` when `entry.Attempts >= MaxAttempts` — so one bad event
cannot take its batch or its host down. `Task.Delay(…, _time, …)` takes the `TimeProvider` so a test
clock can drive the loop.

`EventSubscriptions.Matching` reads the entity name out of the event's `type` (segment two), looks
the entity up in the **primed** catalog, selects the list for the operation, and keeps a hook when
`Condition is null` or `evaluator.Evaluate(condition, @event.Data.Record ?? AlvoRecord.Empty,
@event.Data.OldRecord, context)`. An evaluation that throws is caught and the hook is **not**
selected — fail closed, and logged once at Debug so it is diagnosable without becoming the
per-event noise the whole criterion is about.

Registration, in `AlvoServiceCollectionExtensions.AddAlvo` beside the boot service:

```csharp
services.AddOptions<AlvoEventOptions>().ValidateDataAnnotations().ValidateOnStart();
services.TryAddEnumerable(
    ServiceDescriptor.Singleton<IValidateOptions<AlvoEventOptions>, AlvoEventOptionsValidation>());
services.TryAddSingleton<EventActionExecutor>();
services.TryAddSingleton<IEmailSender, ConsoleEmailSender>();
services.AddHttpClient(WebhookDelivery.HttpClientName);
services.TryAddEnumerable(ServiceDescriptor.Singleton<IHostedService, OutboxDispatcher>());
```

`AlvoEventOptionsValidation` names the configuration key and a usable value per member —
`BatchSize` ≥ 1, `MaxAttempts` ≥ 1, `PollInterval` > `TimeSpan.Zero`, `ClaimLease` >
`PollInterval` (a lease shorter than the poll interval would re-claim an in-flight entry on the very
next tick, which is a duplicate delivery per tick rather than at-least-once).

- [x] **Step 5: Run to verify they pass**

Run:
```
dotnet test --project test/MMLib.Alvo.Tests -- --filter-class '*AlvoBootStateTests*'
dotnet test --project test/MMLib.Alvo.Tests -- --filter-class '*EventSubscriptionsTests*'
dotnet test --project test/MMLib.Alvo.Host.Tests -- --filter-class '*OutboxDispatcherTests*'
```
Expected: PASS. Assert `Build succeeded` first.

- [x] **Step 6: Prove the two hosting facts discriminate**

Two mutations, restored immediately. These are the ones the .NET 10 change makes non-obvious:

1. **Delete the `await _boot.SettledAsync(...)` gate** and confirm
   `The_dispatcher_does_not_claim_anything_before_the_boot_reports_ready` goes **red**. Then, still
   without the gate, **re-register the dispatcher last** and confirm the fact **stays red** — that
   second half is the point: it proves the fact is about the state and not about ordering, which is
   the only version of it worth having on .NET 10.
2. **Remove the blanket `catch (Exception)`** from `ExecuteAsync` and confirm
   `A_delivery_that_always_throws_does_not_stop_the_host` goes **red** with the host stopped. Restore.

Confirm each edit landed with `command grep -c`.

- [x] **Step 7: Accept the core baseline, ring0, commit**

```bash
dotnet test --project test/MMLib.Alvo.Tests -- --filter-class '*PublicApi*'
# AlvoEventOptions is new public surface. SettledAsync is internal and must NOT appear.
scripts/test-ring0
git add src/MMLib.Alvo/Events/ src/MMLib.Alvo/Migrations/AlvoBootState.cs \
        src/MMLib.Alvo/AlvoServiceCollectionExtensions.cs \
        test/MMLib.Alvo.Tests/ test/MMLib.Alvo.Host.Tests/ \
        test/MMLib.Alvo.Tests/PublicApi.MMLib.Alvo.verified.txt
git commit -m "feat(events): dispatch the outbox from a background service gated on AlvoBootState"
```

---

### Task 10: The transition fact, and the execution-log / counter criterion

Two of `baas-analyza.md:676-680`'s acceptance criteria, end to end over the real write path on both
engines. The addendum moved both into PR5a deliberately: the transition test is a `Condition`-profile
expression on an after-hook, which 5a ships, and deferring it *"would leave 5a's whole point — that
events fire correctly — unproven"*; the execution-log criterion is about the **subscription** step and
is *"nearly free if designed in and awkward to retrofit"* (base design `:585-592`).

**Files:**
- Create: `src/MMLib.Alvo.Testing/Events/AlvoEventCriteriaTests.cs`
- Test: `test/MMLib.Alvo.Data.Sqlite.Tests/SqliteAlvoEventCriteriaTests.cs`
- Test: `test/MMLib.Alvo.Data.PostgreSql.Tests.Integration/PostgreSqlAlvoEventCriteriaTests.cs`
- Create: `test/_shared/RecordingMeterListener.cs`
- Modify: `test/_shared/PublicApi.MMLib.Alvo.Testing.verified.txt`

**Interfaces:**
- Consumes: everything from Tasks 3–9, through a host: the descriptor declares an `afterUpdate` hook
  on `vehicles` with the condition `changed(status) && new.status == 'approved'` and a `webhook`
  action pointed at a recording receiver.
- Produces:
  ```csharp
  public abstract class MMLib.Alvo.Testing.Events.AlvoEventCriteriaTests
  {
      protected abstract Task<IAlvoEventWorld> WorldAsync();
  }

  // test/_shared — BCL only, so no new test dependency.
  internal sealed class RecordingMeterListener : IDisposable
  {
      internal RecordingMeterListener(string meterName);
      internal long CountOf(string instrumentName);
  }
  ```

- [ ] **Step 1: Write the failing criteria suite**

```csharp
// src/MMLib.Alvo.Testing/Events/AlvoEventCriteriaTests.cs
public abstract class AlvoEventCriteriaTests
{
    /// <summary>
    /// <c>baas-analyza.md:677</c>: <c>changed(status) &amp;&amp; new.status == 'approved'</c> fires
    /// <b>exactly once, at the transition</b>.
    /// </summary>
    /// <remarks>
    /// The second update is the fact. A hook that fired on every write to an already-approved row
    /// would satisfy a bare "fired at least once" assertion perfectly, and that is the shape the
    /// criterion exists to rule out — <c>changed(status)</c> must be false when the value did not
    /// move, which is only true if the envelope's <c>old_record</c> is the real pre-image.
    /// </remarks>
    [Fact]
    public async Task An_approval_transition_fires_exactly_once_and_a_second_approval_does_not()
    {
        await using var world = await WorldAsync();
        var created = await world.CreateAsync(status: "draft");

        await world.UpdateAsync(created.Id, status: "approved");
        await world.DrainAsync();
        world.Deliveries.Count.ShouldBe(1);

        await world.UpdateAsync(created.Id, status: "approved", color: "blue");
        await world.DrainAsync();

        world.Deliveries.Count.ShouldBe(
            1,
            "changed(status) must be false when status did not move; a second delivery means the "
            + "condition was evaluated against a pre-image that was not the row's own");
    }

    [Fact]
    public async Task A_transition_to_a_different_value_does_not_fire()
    {
        await using var world = await WorldAsync();
        var created = await world.CreateAsync(status: "draft");

        await world.UpdateAsync(created.Id, status: "rejected");
        await world.DrainAsync();

        world.Deliveries.ShouldBeEmpty();
    }

    /// <summary>
    /// <c>baas-analyza.md:678</c>: N events matching nothing produce <b>zero execution-log rows and
    /// one counter increment</b>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// §3.3 records the consequence of getting this wrong as a documented Directus defect —
    /// thousands of log entries for runs that abort immediately on their condition, making debugging
    /// impossible. Confirmed from Directus source: <c>api/src/flows.ts</c> subscribes with no
    /// predicate and the activity/revision write happens <em>after</em> the operation loop, so a
    /// flow that dies on its first condition still writes one activity row and one revision row;
    /// per-item fan-out then multiplies it, so 10 000 inserts are 10 000 runs and 10 000 rows.
    /// </para>
    /// <para>
    /// In F3 the "execution log" is one structured entry per <em>executed action</em> plus three
    /// metrics counters, not a table — a durable queryable log with retention and a redelivery UI is
    /// 7.1 (plan decision D6). The criterion is unchanged by that: a filtered event costs one
    /// counter increment and no action entry.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task A_hundred_events_matching_nothing_produce_no_action_log_and_one_counter_each()
    {
        const int Events = 100;
        await using var world = await WorldAsync();

        foreach (var index in Enumerable.Range(0, Events))
        {
            await world.CreateAsync(status: "draft", vin: $"NOMATCH{index:D9}");
        }
        await world.DrainAsync();

        world.ActionLogEntries.ShouldBeEmpty(
            $"{Events} filtered events must produce no execution-log entry at all; this is the "
            + "documented Directus defect §3.3 cites, and Alvo avoids it by construction because the "
            + "CEL Condition profile is compiled at apply time and evaluated at subscription time");
        world.Metrics.CountOf("alvo.events.filtered").ShouldBe(Events);
        world.Metrics.CountOf("alvo.events.dispatched").ShouldBe(0);
        world.Deliveries.ShouldBeEmpty();
    }

    [Fact]
    public async Task A_matched_event_writes_exactly_one_action_log_entry_naming_the_hook()
    {
        await using var world = await WorldAsync();
        var created = await world.CreateAsync(status: "draft");

        await world.UpdateAsync(created.Id, status: "approved");
        await world.DrainAsync();

        world.ActionLogEntries.ShouldHaveSingleItem().Message.ShouldContain("afterUpdate");
        world.Metrics.CountOf("alvo.events.dispatched").ShouldBe(1);
    }
}
```

`IAlvoEventWorld` exposes `CreateAsync`, `UpdateAsync`, `DrainAsync` (pump until the outbox has no
undispatched entry, with a bounded wait that fails loudly on timeout), `Deliveries`,
`ActionLogEntries` (only `EventLog.ActionExecuted` entries) and `Metrics`.

`RecordingMeterListener` is a `MeterListener` over `AlvoEventMetrics.MeterName`, summing per
instrument — BCL only, so no `Microsoft.Extensions.Diagnostics.Testing` dependency is added.

- [ ] **Step 2: Run to verify they fail**

Run: `dotnet test --project test/MMLib.Alvo.Data.Sqlite.Tests -- --filter-class '*SqliteAlvoEventCriteriaTests*'`
Expected: FAIL — the suite and its world do not exist.

- [ ] **Step 3: Implement the world for both engines**

One world per driver, over the existing fixtures (`AlvoDataWorlds` on SQLite,
`PostgreSqlAlvoDataFixture` on PostgreSQL) plus a descriptor that declares the hook. `DrainAsync`
drives the dispatcher's own pump rather than sleeping: expose an internal
`OutboxDispatcher.PumpOneBatchAsync` to the test assembly through the existing `InternalsVisibleTo`
and loop it until a claim comes back empty, with a hard cap that throws naming how many entries were
left. A drain that silently gave up would make every count above an under-count.

- [ ] **Step 4: Run to verify they pass, on both engines**

Run:
```
dotnet test --project test/MMLib.Alvo.Data.Sqlite.Tests -- --filter-class '*SqliteAlvoEventCriteriaTests*'
dotnet test --project test/MMLib.Alvo.Data.PostgreSql.Tests.Integration -- --filter-class '*PostgreSqlAlvoEventCriteriaTests*'
```
Expected: PASS on both. Assert `Build succeeded` first.

- [ ] **Step 5: Prove both criteria discriminate**

Three mutations, restored immediately:

1. **Evaluate the condition *after* logging the action** (move `EventSubscriptions.Matching` inside
   the executed-action path, i.e. build the Directus defect on purpose). Confirm
   `A_hundred_events_matching_nothing_produce_no_action_log_and_one_counter_each` goes **red** with
   100 entries. This is the measurement that the criterion is about the subscription step.
2. **Pass `previous: null` to the evaluator** in `EventSubscriptions`. Confirm
   `An_approval_transition_fires_exactly_once_and_a_second_approval_does_not` goes **red** on its
   *second* assertion — `changed(status)` with no pre-image reads as changed, so the second update
   fires. Without this mutation the fact would pass on any always-true condition.
3. **Increment `Filtered` per matched hook instead of per event.** Confirm the counter assertion goes
   **red**. "One counter increment" is per *event*, and an entity with two non-matching hooks would
   otherwise report double.

- [ ] **Step 6: Accept the `Testing` baseline, ring0, commit**

```bash
dotnet test --project test/MMLib.Alvo.Data.Sqlite.Tests -- --filter-class '*PublicApi*'
scripts/test-ring0
git add src/MMLib.Alvo.Testing/Events/ test/_shared/ test/MMLib.Alvo.Data.Sqlite.Tests/ \
        test/MMLib.Alvo.Data.PostgreSql.Tests.Integration/ \
        test/_shared/PublicApi.MMLib.Alvo.Testing.verified.txt
git commit -m "test(events): pin the transition and execution-log criteria on both engines"
```

---

### Task 11: The 10k-event chaos criterion

`baas-analyza.md:676`'s first number: a 10 000-event chaos run **loses no event**, on SQLite and
PostgreSQL. Modelled on `test/MMLib.Alvo.Api.Tests.Integration/PagingPerformanceTests.cs`, which is
the repo's own answer to how a numeric criterion is written so it cannot pass vacuously: **assert the
setup before measuring, put the number in the failure message, and write the measurement to
`artifacts/criteria/`**.

**Files:**
- Create: `test/MMLib.Alvo.Api.Tests.Integration/OutboxChaosTests.cs` (PostgreSQL)
- Create: `test/MMLib.Alvo.Data.Sqlite.Tests/SqliteOutboxChaosTests.cs` (SQLite)
- Modify: `.github/workflows/ci.yml` (publish + upload `artifacts/criteria/events.md`)

**Interfaces:**
- Consumes: `OutboxTable.InsertAsync` (the **production** writer), `IOutboxStore`,
  `OutboxDispatcher.PumpOneBatchAsync`, `RecordingMeterListener`.
- Produces: no shipped surface. One line per run in `artifacts/criteria/events.md`.

- [ ] **Step 1: Write the failing chaos test**

```csharp
/// <summary>
/// <c>baas-analyza.md:676</c>: a <b>10 000-event chaos run loses no event</b>.
/// </summary>
/// <remarks>
/// <para>
/// <b>Three things make this fact rather than a loop that terminates.</b> The seed count and the
/// undispatched count are asserted <em>before</em> anything is dispatched, so a run over an empty
/// outbox cannot pass. A configurable fraction of deliveries fails transiently, so the claim /
/// release / re-claim path is exercised rather than only the happy one. And the dispatcher is
/// <em>stopped and restarted</em> twice mid-run, so an in-flight claim is abandoned and has to be
/// recovered through its lease — which is the same recovery the crash criterion depends on.
/// </para>
/// <para>
/// <b>The seed goes through the production writer</b>, in transactions of 1 000, rather than through
/// <c>COPY</c> or 10 000 HTTP creates. <c>PagingPerformanceTests</c>' own lesson is the reason: a
/// hand seed can store a representation the read path cannot match, leaving every later query fast
/// and empty. Here the writer <em>is</em> the production one, and one seeded entry is claimed and
/// deserialized before the run starts, so an unreadable payload fails the setup rather than
/// flattering the result.
/// </para>
/// <para>
/// Seeding time is reported apart from the run, because it is setup cost and folding it in would
/// make the number mean something other than what the criterion says.
/// </para>
/// </remarks>
[Fact]
public async Task Ten_thousand_events_are_all_delivered_exactly_once_or_more_and_none_is_lost()
{
    await using var world = await WorldAsync();

    var seed = await SeedOutboxAsync(world, EventCount);
    (await world.UndispatchedCountAsync()).ShouldBe(
        EventCount, "the criterion is about 10 000 events, and a chaos run is trivial on fewer");
    (await world.ClaimOneAndReleaseAsync()).ShouldNotBeNull(
        "one seeded entry must be claimable and deserializable before the run starts, or the seed "
        + "stored a payload the dispatch path cannot read");

    var run = await RunWithChaosAsync(world, failEvery: FailEvery, restarts: Restarts);

    await ReportAsync(
        $"§3 outbox chaos ({EventCount} events, fail 1-in-{FailEvery}, {Restarts} restarts, "
        + $"{EngineDescription}): {run.Describe()}; seeded in {seed.Elapsed.TotalSeconds:F1}s");

    world.DeliveredIds.Distinct().Count().ShouldBe(
        EventCount,
        $"every one of the {EventCount} events must be delivered at least once; "
        + $"{EventCount - world.DeliveredIds.Distinct().Count()} were never delivered. {run.Describe()}");
    (await world.UndispatchedCountAsync()).ShouldBe(
        0, $"no event may be left unclaimed or stuck below the attempt ceiling. {run.Describe()}");
    run.TransientFailures.ShouldBeGreaterThan(
        EventCount / (FailEvery * 2),
        $"the chaos must really have happened, not been configured; {run.Describe()}");
    run.Restarts.ShouldBe(
        Restarts, $"the dispatcher must have been stopped and restarted mid-run; {run.Describe()}");
    run.RedeliveredIds.ShouldNotBeEmpty(
        "at-least-once delivery means a released or lease-expired entry is delivered again; a run "
        + $"with no redelivery did not exercise the recovery path. {run.Describe()}");
}

private const int EventCount = 10_000;
private const int FailEvery = 20;
private const int Restarts = 2;
```

`RunWithChaosAsync` pumps batches, injecting a transient throw on every `FailEvery`-th delivery,
disposing and recreating the dispatcher after each third of the run, advancing the test clock past
the claim lease each time so abandoned claims are recoverable, and stopping when a claim comes back
empty *and* the undispatched count is zero. It returns a small record with
`Delivered`, `RedeliveredIds`, `TransientFailures`, `Restarts`, `Elapsed`, and a `Describe()` that
puts all of them in one line — the same shape `Walk.Describe()` uses, for the same reason.

`ReportAsync` is `PagingPerformanceTests.ReportAsync` verbatim in shape: `TestOutputHelper`,
`AddAttachment`, and an append to `artifacts/criteria/events.md` — the one copy a reader can compare
across runs without re-running anything.

- [ ] **Step 2: Run to verify it fails, then measure the budget**

Run: `dotnet test --project test/MMLib.Alvo.Data.Sqlite.Tests -- --filter-class '*SqliteOutboxChaosTests*'`
Expected: FAIL — the harness does not exist.

Once it passes (Step 4), **time both engines** and compare against `spike.txt`'s Q8:

```bash
time dotnet test --project test/MMLib.Alvo.Data.Sqlite.Tests -- --filter-class '*SqliteOutboxChaosTests*'
time dotnet test --project test/MMLib.Alvo.Api.Tests.Integration -- --filter-class '*OutboxChaosTests*'
```

**The budget rule.** `ci.yml`'s `build-test` is `timeout-minutes: 20` for *all* of ring2, and
`PagingPerformanceTests` already spends ~26 s of it. If the two chaos runs together exceed **120 s**,
reduce `Restarts` to 1 and `BatchSize` upward before reducing `EventCount` — the event count is the
criterion and must not move. If they still exceed it, move the PostgreSQL leg to its own CI job
**and add that job to the branch ruleset in the same change**, on the reasoning
`PagingPerformanceTests:42-49` already records: a job outside the ruleset puts a numeric criterion
outside the one check that blocks a merge.

- [ ] **Step 3: Extend `ci.yml` to publish the new criterion file**

Copy the existing `Publish paging criteria` / `Upload paging criteria` steps for
`artifacts/criteria/events.md`. The reasoning is already in `ci.yml:86-90`: `TestOutputHelper` output
is not printed on a passing test under MTP and an attachment lands in OS temp, so the appended file
is the only durable copy.

- [ ] **Step 4: Run to verify they pass, on both engines**

Run:
```
dotnet test --project test/MMLib.Alvo.Data.Sqlite.Tests -- --filter-class '*SqliteOutboxChaosTests*'
dotnet test --project test/MMLib.Alvo.Api.Tests.Integration -- --filter-class '*OutboxChaosTests*'
```
Expected: PASS, and `artifacts/criteria/events.md` carries one line per run. Assert `Build succeeded`
first.

- [ ] **Step 5: Prove the chaos run is not a loop that terminates**

Three mutations, restored immediately. The first two are the ways this test would most plausibly pass
while proving nothing:

1. **Seed 10 events instead of 10 000.** Confirm the *setup* assertion goes **red** — not the
   delivery one. A chaos test whose setup is unasserted measures whatever happened to be in the table.
2. **Set `FailEvery` so high that nothing fails** (e.g. `100_000`). Confirm
   `run.TransientFailures.ShouldBeGreaterThan(...)` goes **red**. Chaos that is configured but never
   happens is the static-table version of this fact.
3. **Make `ReleaseAsync` a no-op.** Confirm the undispatched-count assertion goes **red** with the
   failed entries stuck. This is what proves "loses no event" covers the failure path and not only the
   happy one.

- [ ] **Step 6: ring0 + commit**

```bash
scripts/test-ring0
git add test/MMLib.Alvo.Api.Tests.Integration/OutboxChaosTests.cs \
        test/MMLib.Alvo.Data.Sqlite.Tests/SqliteOutboxChaosTests.cs .github/workflows/ci.yml
git commit -m "test(events): pin the 10k-event chaos criterion on both engines"
```

---

### Task 12: The crash criteria, and what the harness does not prove

`baas-analyza.md:676`'s second and third numbers: kill **between commit and publish** → the event is
delivered after restart; **kill mid-action → the action repeats** (the half the issue body drops).
Register R12 is the constraint: **no process-kill harness exists.** `AlvoHostWorld` runs in-process
over `TestServer`, and a graceful stop calls `StopAsync`, which does *not* exercise the crash path.

**Files:**
- Create: `test/MMLib.Alvo.Host.Tests/Events/OutboxRecoveryTests.cs` (in-process)
- Create: `test/MMLib.Alvo.Host.Tests/Events/KilledHostRecoveryTests.cs` (child process)
- Create: `test/MMLib.Alvo.Host.Tests/Events/ChildHostHarness.cs`

**Interfaces:**
- Consumes: `MMLib.Alvo.Host`'s published output, `IOutboxStore`, a loopback `HttpListener` receiver.
- Produces: no shipped surface.

- [ ] **Step 1: Write the in-process facts, named for what they do not prove**

These ship **regardless** of the spike's Q9 verdict, because they are fast and they are the ones
that isolate the *store's* recovery from the process's.

```csharp
/// <summary>
/// Recovery facts over an in-process host. <b>None of these exercises a real process kill.</b>
/// </summary>
/// <remarks>
/// <c>AlvoHostWorld</c> runs over <c>TestServer</c> and a graceful stop calls <c>StopAsync</c>, so a
/// simulated crash here is a dispatcher that never claimed, or a claim abandoned by disposal — not a
/// process that died mid-write. What that leaves unproven is exactly one thing: that an OS-level kill
/// between the engine's commit and the dispatcher's claim loses nothing. That is
/// <c>KilledHostRecoveryTests</c>' job, and the two files exist separately so neither can be mistaken
/// for the other.
/// </remarks>
public sealed class OutboxRecoveryTests
{
    [Fact]
    public async Task An_event_committed_while_the_dispatcher_was_off_is_delivered_by_the_next_host()
    {
        var database = SharedDatabase();

        await using (var first = await AlvoHostWorld.StartAsync(
            new HostWorldSetup { Database = database, Configuration = [new("Alvo:Events:Enabled", "false")] }))
        {
            await first.CreateVehicleAsync(status: "approved");
            (await first.UndispatchedCountAsync()).ShouldBe(1);
        }

        await using var second = await AlvoHostWorld.StartAsync(new HostWorldSetup { Database = database });

        await second.WaitUntilDispatchedAsync(count: 1);
        second.Deliveries.ShouldHaveSingleItem();
    }

    [Fact]
    public async Task A_claim_abandoned_by_a_disposed_host_is_recovered_after_its_lease_expires()
    {
        var database = SharedDatabase();

        await using (var first = await AlvoHostWorld.StartAsync(
            new HostWorldSetup { Database = database, WebhooksHangForever = true }))
        {
            await first.CreateVehicleAsync(status: "approved");
            await first.WaitUntilClaimedAsync(count: 1);
        }

        await using var second = await AlvoHostWorld.StartAsync(
            new HostWorldSetup { Database = database, ClaimLease = TimeSpan.Zero });

        await second.WaitUntilDispatchedAsync(count: 1);
        second.Deliveries.ShouldHaveSingleItem();
    }
}
```

- [ ] **Step 2: Check the spike's Q9 verdict, then write the child-process facts**

**Gate — already resolved: build the harness.** Q9 measured **6.0 s** (publish 3.2 s + boots 1.6 s and
1.3 s), with exit code **137** confirming the kill was a real SIGKILL and not a graceful `StopAsync`.
The 120 s fallback below does not apply and is kept only as the record of the rule.

- *Q9's publish-plus-two-boots total is ≤ 120 s* → build the harness as below.
- *Q9's total exceeds 120 s* → **do not build it.** Ship Step 1's facts only, add one paragraph to
  `OutboxRecoveryTests`' `<remarks>` stating plainly that no fact in this repository exercises an
  OS-level kill and naming the measured cost that decided it, and file the harness as an issue. An
  honest disclosure is worth more than a harness that makes ring2 time out.

```csharp
/// <summary>
/// <c>baas-analyza.md:676</c>, both halves, against a host that is really killed:
/// <b>kill between commit and publish → delivered after restart</b>, and
/// <b>kill mid-action → the action repeats</b>.
/// </summary>
/// <remarks>
/// <para>
/// The host runs as a <em>child process</em> against a temp SQLite file and is ended with
/// <c>Process.Kill(entireProcessTree: true)</c> — SIGKILL, no <c>StopAsync</c>, no disposal, no
/// flush. That is the only shape in this repository that exercises the crash path at all, and it is
/// why it does not reuse <c>AlvoHostWorld</c>.
/// </para>
/// <para>
/// <b>The mid-action kill is deterministic, not timed.</b> The webhook receiver records the delivery
/// and then kills the child itself, from inside the request, before the response is written — so the
/// action provably completed while <c>dispatched_at</c> provably did not. A <c>Task.Delay</c>-timed
/// kill would be a flaky approximation of the same idea.
/// </para>
/// <para>
/// What this still does not prove: that a kill during the engine's own commit is atomic. That is the
/// engine's guarantee, not Alvo's, and Alvo's part of it — the outbox row riding the same
/// <c>DbTransaction</c> — is proven by <c>AlvoDataOutboxTests</c>.
/// </para>
/// </remarks>
public sealed class KilledHostRecoveryTests
{
    [Fact]
    public async Task An_event_committed_before_a_kill_is_delivered_after_a_restart()
    {
        await using var harness = await ChildHostHarness.StartAsync(new ChildHostSetup { DispatcherEnabled = false });
        await harness.CreateVehicleAsync(status: "approved");

        harness.Kill();
        await harness.RestartAsync(new ChildHostSetup { DispatcherEnabled = true });

        await harness.Receiver.WaitForDeliveriesAsync(count: 1);
    }

    [Fact]
    public async Task A_kill_mid_action_makes_the_action_repeat_after_a_restart()
    {
        await using var harness = await ChildHostHarness.StartAsync(
            new ChildHostSetup { KillOnFirstDelivery = true, ClaimLease = TimeSpan.Zero });

        await harness.CreateVehicleAsync(status: "approved");
        await harness.Receiver.WaitForDeliveriesAsync(count: 1);
        await harness.WaitUntilExitedAsync();

        await harness.RestartAsync(new ChildHostSetup { ClaimLease = TimeSpan.Zero });

        await harness.Receiver.WaitForDeliveriesAsync(count: 2);
        harness.Receiver.Deliveries.Select(EventIdOf).Distinct().ShouldHaveSingleItem(
            "the repeat must be the SAME event redelivered, not a second event; at-least-once "
            + "delivery is the claim, and a different id would mean the write path emitted twice");
    }
}
```

`ChildHostHarness` publishes `MMLib.Alvo.Host` once per test class (cached in a static
`Lazy<Task<string>>`), starts `dotnet <published>/MMLib.Alvo.Host.dll` with environment variables for
the SQLite path, the descriptor path, the webhook endpoint URL and `Alvo:Events:*`, polls
`/health/ready`, and exposes `Kill()`, `RestartAsync`, `WaitUntilExitedAsync` and `Receiver`. The
receiver is an `HttpListener` on a free loopback port; with `KillOnFirstDelivery` it records the body
and then kills the child **before** responding.

- [x] **Step 3: Run to verify they fail**

Run: `dotnet test --project test/MMLib.Alvo.Host.Tests -- --filter-namespace 'MMLib.Alvo.Host.Tests.Events'`
Expected: FAIL.

- [ ] **Step 4: Implement, and run**

Run: `dotnet test --project test/MMLib.Alvo.Host.Tests -- --filter-namespace 'MMLib.Alvo.Host.Tests.Events'`
Expected: PASS. Assert `Build succeeded` first, and note that CI may use a newer analyzer set than
local (#129) — the child host is *published*, so an analyzer error that only CI sees would surface
here first rather than in the e2e.

- [ ] **Step 5: Prove the crash facts discriminate**

Two mutations, restored immediately:

1. **Mark the entry dispatched *before* running the action** (swap the order in `PumpOneBatchAsync`).
   Confirm `A_kill_mid_action_makes_the_action_repeat_after_a_restart` goes **red** with one delivery.
   This is the ordering the second half of the criterion is entirely about, and nothing else in the
   suite pins it.
2. **Replace `harness.Kill()` with a graceful `StopAsync`-equivalent shutdown.** Confirm
   `An_event_committed_before_a_kill_is_delivered_after_a_restart` **still passes** — and record that
   in the test's `<remarks>` as the reason the kill is worth the harness: it is the only version that
   *could* fail differently, and the graceful version proves nothing about the crash path. Restore.

- [ ] **Step 6: ring0 + commit**

```bash
scripts/test-ring0
git add test/MMLib.Alvo.Host.Tests/Events/
git commit -m "test(events): prove crash recovery and mid-action repeat against a killed host"
```

---

### Task 13: The docs of record, the doc-drift, and the obligations PR5b inherits

An over-claim in a document is more expensive than a gap, so this task's whole job is to make the
repository say what shipped — including the four things it deliberately does not do.

**Files:**
- Create: `docs/architecture/events.md`
- Modify: `docs/architecture/data-path.md:1480-1493` (the PR5 forward-looking section)
- Modify: `docs/architecture/extensibility.md` (the two new ports)
- Modify: `.claude/skills/alvo-dotnet-conventions/SKILL.md`
- Modify: `docs/architecture/package-boundary.md` — "What a database provider must implement to boot" gains
  **`IOutboxStore`**: Task 9's dispatcher requires it to resolve, so a provider without it can no longer boot.
  Word it as deviation 60 words `IRuntimeSchemaWriter`'s widening — both in-repo drivers get it from
  `AddRelationalProvider`, so the cost falls on a future non-EF or dynamic-storage provider, which would
  otherwise meet it as a DI failure at startup rather than as a documented obligation.
- Modify: `docs/PLAN.md`
- Modify: `README.md` (the descriptor feature table's hooks/events rows, if it carries them)

**Interfaces:**
- Consumes: every decision D1–D7, deviations 58–78, and the four issue numbers from Task 1 Step 6.
- Produces: no code.

- [ ] **Step 1: Write `docs/architecture/events.md`**

Sections, in this order, each stating the decision *and* its cost:

1. **The envelope.** CloudEvents 1.0.2, wire `specversion` `"1.0"`; the attribute table with the
   registered-versus-post-1.0.2 provenance split spelled out (`partitionkey`, `sequence`,
   `dataref` registered in v1.0.2; `authtype`/`authid` and `correlationid`/`causationid` from
   `extensions/authcontext.md` and `extensions/correlation.md` on `main`); why `record`, `old_record`
   and the changed list live inside `data`; deviation 69's `payloadversion` duplication; the 64 KB
   forwarding rule and the `dataref` issue.
2. **The outbox.** The column list; why there is no `sequence` and why `id` is a **monotonic** UUIDv7
   minted through `AlvoEventId`, citing `spike.txt` Q1/Q2/Q6/Q7; why the claim filters
   `dispatched_at IS NULL` and never a high-water mark (R2); why the `ORDER BY` and the `LIMIT` are in
   the subquery — refused by **both** engines, naming `ORDER` (Q3, correcting R4); why `RETURNING` is
   re-sorted in process (measured unsorted on both engines, Q3); **why the outer `WHERE` repeats the
   claimability predicate** and what happened without it (Q4: overlap 10 of 10, `attempts` at 2); why
   there is no `SKIP LOCKED` and no new `IAlvoSqlDialect` member; why the claim is raw SQL rather than
   LINQ under `UseRelationalNulls()`; and three traps recorded so they are not re-run: SQLite silently
   accepts `SERIAL` (Q6), `Guid`'s default byte order is not sortable so the column is `TEXT` (Q1),
   and `journal_mode=WAL` is neither needed nor revertible (Q5).
3. **The ordering guarantee, with both of its conditions in the same sentence.** Verbatim, because
   the base design over-claims it (`:574-577`) and this plan's own first draft stated only the first
   condition:

   > There is **no global ordering** (§3.3 calls it expensive and brittle). **Per-entity-key ordering
   > holds while exactly one dispatcher runs *and* no two events for one key are written inside the
   > same millisecond** — and only then. Delivery is **at-least-once** regardless, so every
   > after-side action must be idempotent or deduplicated by event id.
   >
   > **Why the millisecond is part of the guarantee.** The queue order *is* `ORDER BY id`, and the id
   > is a UUIDv7 whose ordering is exact only above the millisecond. `Guid.CreateVersion7()` fills
   > everything below it with fresh random bits, and **49.9 %** of adjacent same-millisecond pairs
   > sort backwards (measured: `spike.txt` Q1). Alvo therefore mints ids through `AlvoEventId`, which
   > reuses the last emitted millisecond and increments the random tail — **0 inversions over
   > 100 000** — so **within one process** the condition is met and the guarantee reduces to "one
   > dispatcher". Across processes it does not: two hosts minting inside one millisecond still
   > interleave (#150).
   >
   > **Operational constraint PR5a cannot enforce.** There is no distributed lock, so the dispatcher
   > cannot detect a second instance. Two replicas of the standalone image break the per-entity-key
   > guarantee **silently** — no error, no log. `partition_key` is written on every row from the first
   > migration so F7's partitioned claim is additive; until it lands, run one instance.
   > `FOR UPDATE SKIP LOCKED` is **not** the fix: it skips the row, not the key.

4. **After-hooks.** Compiled into the `PolicyCatalog` (R11 — one priming site); the condition is part
   of the **subscription**, with §3.3's Directus defect and the corrected details from the register
   (the setting is `flow.accountability`, a top-level column on `directus_flows`, **not**
   `flow.options.accountability`; `FLOWS_EXEC_ALLOWED_MODULES` no longer exists and never concerned
   logging; the user complaint is one auto-closed discussion reply, an authentic report and **not** an
   acknowledged Directus bug); the action set (`webhook`, `email`-to-console) and the three refused by
   name; D7's unmasked-record disclosure and its issue.
5. **Templates and JSONata.** Deviation 63's discriminator verbatim, both clauses with the shipped
   example that earns each; the asymmetry with the plain-string sugar slots; deviation 64's
   refuse-never-render-empty rule; deviation 65's absence test and the **architectural** ban test the
   evaluator's PR owes.
6. **The dispatcher.** The `AlvoBootState` gate and why ordering cannot express it on .NET 10; the
   containment and `StopHost`; the 30 s `ShutdownTimeout`; D6's logs-and-metrics execution log and the
   three counter names; the attempt ceiling as PR5a's DLQ stand-in and 7.1 as its owner.
7. **What PR5a does not do.** The addendum's list, scoped to 5a: no global ordering; no JSONata; no
   `function`/`http.call`/`entity.update`; no `Publish`; no wildcard matcher; no HMAC and no
   `secretRef`; no SMTP and no mail service in compose; no DLQ or redelivery UI; no per-endpoint field
   projection; no `dataref`. Each with the issue or the PR that owns it.

- [ ] **Step 2: Replace `data-path.md`'s forward-looking PR5 section**

`data-path.md:1480-1493` currently predicts this work. Rewrite it as what shipped: the four emit
sites, the transaction seam honoured, the `SaveChanges`-interceptor trap **closed and proven by a
mutation** (Task 4 Step 6 — say so, because that mutation is the evidence the warning became a fact),
and a pointer to `events.md`. Then update two nearby claims that this PR made stale:

- **`:121-145`'s "PR5 adds LINQ to this package"** — it did not. The claim and dispatch statements are
  raw SQL, so `UseRelationalNulls()`'s cost is met by construction, and
  `ChangeTrackerReachTests.The_outbox_claim_is_raw_sql_and_never_linq_over_the_context` holds the
  line. Record the change of approach rather than deleting the paragraph: a future LINQ addition to
  this package still pays the cost the paragraph describes.
- **`:354-391`'s "PR5's outbox" forward reference** — resolve it: the outbox's `created_at`,
  `claimed_at` and `dispatched_at` all go through `StoredInstant.Text`, and the envelope enforces the
  same rule at its own boundary because `StoredInstant` is `internal` to the driver.

- [ ] **Step 3: Fix the Wolverine doc-drift**

`.claude/skills/alvo-dotnet-conventions/SKILL.md`'s licensing section says *"If you need a
mediator/outbox pattern, **Wolverine** is the suggested alternative"*. The base design's deviation 1
rejected that for the outbox: Alvo owns the outbox, the core takes no foreign dependency for it, and
`IEventDispatcher`/`IOutboxStore` leave Wolverine or an external bus available later as an **adapter
package**. Two answers in the repo is the drift; correct the skill to say so, keeping Wolverine named
as the in-process-mediator alternative to MediatR, which is what that section is actually about.

- [ ] **Step 4: Record the obligations PR5b and F7 inherit**

In `docs/PLAN.md`, beside `#22`, and in `events.md`'s last section — so neither the addendum nor this
plan is the only place they live:

- **`Publish` and its security ruling.** `Publish` must **refuse** a name matching
  `^(entity|auth|storage)\.` or a host can mint an event indistinguishable from a real data change,
  and every descriptor rule and after-hook subscribing to `entity.orders.updated` would fire on a
  forged one. Not shipped in PR5a; owed by whichever PR ships `Publish`.
- **Wildcard subscription.** `entity.orders.*` is a **hard** spec guarantee
  (`alvo-specifikacia.md:141`) with no matcher; `baas-analyza.md:657` requires tenant isolation of
  rules, so a wildcard makes cross-tenant fan-out the default failure mode. PR5b either implements the
  matcher **with every subscription scoped to the envelope's tenant and a named adversarial
  cross-tenant fact**, or refuses `*` at apply until it exists.
- **`AlvoContext` and provenance.** `ChainDepth` and `CausationId` are on the envelope and are `0`/
  absent in PR5a because nothing yet runs a data action *because of* an event. PR5b needs a way to
  thread them into an `entity.update` action's write — which is an `AlvoContext` change (a public type
  in `Abstractions`) or a distinct provenance parameter, and that shape is PR5b's decision.
- **Do not derive an idempotency key per event id.** Data actions' keys were to be *"derived from the
  event id"* (base design `:577-578`), but nothing prunes `alvo_idempotency` (#115), so the table
  would grow with **event** volume rather than with keyed creates; and `AlvoIdempotency` is honoured
  on create only, and an anonymous actor cannot hold a key — so a dispatcher must pass a real
  `AlvoContext.System(tenant)`, never `Anonymous`.
- **The `complex-crm` corrections and the refusal-reason strengthening** (deviation 76), safe to defer
  because Task 7 Step 6 pins that the example declares no after-hooks.
- **`@tenant.id` and `@user.roles` cannot resolve in a template, and the addendum's own table promises
  both** (its *"the provenance the envelope carries"* row names `@user.id` **and** `@tenant.id`).
  Measured in Task 6: `AlvoEvent` carries `authid` and *no* tenant attribute and *no* roles, so
  `TemplatePlaceholder.Roots` is `new`, `old`, `event`, `@user` and both names are refused **by name**
  — `@tenant.id` because answering it from the row's own `tenant_id` would answer a different question
  (which tenant the *row* belongs to, not which tenant the *caller* was in, and a
  `AlvoContext.System` write has no tenant at all), `@user.roles` because the envelope carries
  authentication and never authorization. Giving `@tenant.id` a real answer is a **public-API and
  wire-format** change — a new attribute on `AlvoEvent` plus its `AlvoEventJson` member and the
  outbox payloads already written — so it is deliberately not a PR5a fix. Whoever takes it owns the
  compatibility question for rows written by this build. `#37` tracks the identity-claim half
  (`@user.claims`, which is what an `email` recipient actually needs).
- **F7's partitioned claim** (**#150**, which also carries Q1's same-millisecond finding and the
  cross-process half `AlvoEventId` does not close) and **`dataref`** (**#151**).

- [ ] **Step 5: Regenerate nothing, and check the freshness gate**

This PR touches no file under `docs/product/`, so the brief-freshness gate must not fire. Confirm:

Run: `scripts/check-brief-freshness`
Expected: OK. If it fires, a `docs/product/` file was edited by mistake — revert that edit rather
than regenerating the brief.

- [ ] **Step 6: ring0 + commit**

```bash
scripts/test-ring0
git add docs/architecture/events.md docs/architecture/data-path.md docs/architecture/extensibility.md \
        docs/PLAN.md README.md .claude/skills/alvo-dotnet-conventions/SKILL.md
git commit -m "docs(events): record the event backbone, its ordering condition, and PR5b's obligations"
```

---

## Before opening the PR

- [ ] **`scripts/test-ring2`** — green. This is the first run that includes the affected-scoped
      integration projects, the API invariant check and Vacuum.
- [ ] **`scripts/test-e2e`** — green. The host and its published output are touched (Task 12), and
      #129 is the precedent: CI may use a **newer analyzer set** than local, and only the e2e caught
      it last time.
- [ ] **`artifacts/criteria/events.md`** carries a line from this run, and its numbers are quoted in
      the PR body beside `baas-analyza.md:676`'s three criteria.
- [ ] **`docs/superpowers/specs/evidence/2026-08-03-f3-pr5a-events/spike.txt`** is committed, with its
      provenance header and its Verdicts block, and every decision in this plan that says *measured*
      points at a verdict in it.
- [ ] **Every moved `*.verified.*` baseline has an `alvo-snapshot-judge` verdict.** Four are expected:
      `PublicApi.MMLib.Alvo.Abstractions`, `PublicApi.MMLib.Alvo`,
      `PublicApi.MMLib.Alvo.Data.EntityFrameworkCore`, `PublicApi.MMLib.Alvo.Testing`, plus Task 2's
      envelope snapshot. **No baseline was hand-edited.**
- [ ] **The `alvo-security-core-review` checklist**, run against the whole diff. This PR touches the
      rule engine's compile pass (after-hook conditions), the policy catalog's priming, and a new
      network egress path — the checklist's own territory. Three findings to look for specifically:
      D7's unmasked disclosure is *named and tested*; `EventSubscriptions` fails **closed** when a
      condition throws; and the dispatcher's `AlvoContext` is `System(tenant)` with a real tenant,
      never `Anonymous`.
- [ ] **A security review** of the diff (injection, authz, insecure data handling), paired with the
      checklist above. The webhook URL comes from the descriptor and never from a caller — confirm
      that end to end, including that a template can never render *into* a URL.
- [ ] **`alvo-plan-guard`** dispatched as the last check: drift from `docs/PLAN.md`, violated §0
      principles, shortcuts in the security core. Read-only and advisory.
- [ ] **A `workflow_dispatch` mutation run, green**, before merge. Mutation runs post-merge on `main`,
      so a security-core PR gets its run on demand first. Two files to read the report for
      specifically: `OutboxTable.cs` (the claim predicate — a surviving mutant there is a claim that
      cannot lose a row *because nothing tests it*) and `JsonataSlot.cs` (the classifier's two
      clauses).
- [ ] **The PR body** states, in its own words and not only by reference: the ordering guarantee
      **and both of its conditions** — one dispatcher *and* no two events for one key in one
      millisecond, with the in-process half closed by `AlvoEventId` and the cross-process half filed
      as #150; that per-entity-key ordering breaks silently on two
      replicas; that JSONata is refused rather than partially implemented; that `email` is
      console-only; that D7 discloses hidden fields to a declared endpoint; and which crash fact the
      harness does **not** prove.
- [ ] **This PR does not close #22.** It closes PR5a's half; #22 closes when PR5b merges (deviation
      78). Say so in the body, or the issue closes with before-hooks and automation unshipped.

---

## Self-review of this plan

Run against the addendum with fresh eyes, per the writing-plans skill.

**1. Spec coverage.** Every one of the addendum's fourteen PR5a Definition-of-Done lines maps to a
task in the table above, and the four items with **no** task are named there with the reason —
`Publish`, the wildcard matcher, `dataref`, and `complex-crm`'s corrections. Three of the four are
out-of-scope by the addendum's own split; the fourth (`Publish`) is named in neither PR's content row
nor either DoD list, which is a **gap in the addendum**, and Task 13 Step 4 records the security
ruling it owes so it cannot be lost.

**2. Placeholder scan.** No `TBD`, no "add appropriate error handling", no "similar to Task N", no
step that says what without showing how. Two places named a *decision rule* rather than an answer, and
**both are now resolved by Task 1's measurements**: Task 5 Step 3's SQLite-configuration gate cleared
on its first branch (implement as written, change no registration — the `journal_mode=WAL` stop
condition did not trigger), and Task 12 Step 2's harness gate cleared at a 6.0 s budget, so the
harness ships. Task 7 Step 3's issue number is **#149**, filled from Task 1 Step 6, which ran first;
`#150`, `#151` and `#152` are filled the same way.

**2a. What the spike changed after this self-review was first written.** Recorded here because a
reader comparing the plan against its own review would otherwise find the review stale:

- **D1's monotonicity claim was refuted and amended** — `Guid.CreateVersion7()` inverts 49.9 % of
  same-millisecond pairs, so `AlvoEventId` (Task 2) is added and the ordering wording gains its second
  condition everywhere. D1's portability half became evidence rather than reasoning, and its `"N"`
  fallback is withdrawn as unnecessary.
- **D2's claim SQL was measured wrong and is amended** — the outer `WHERE` now repeats the
  claimability predicate, Task 5 carries the spike's text verbatim, and the shape fact plus a
  PostgreSQL two-claimant fact hold the line. D2's stated cost ("slow, not incorrect") was false for
  the original statement and is true only for the amended one.

**3. Type consistency.** Checked across tasks: `AlvoEvent`/`AlvoEventData`/`AlvoEventAttributes`/
`AlvoEventJson` (Task 2) are used unchanged in Tasks 3–12. `OutboxEntry` is declared in Task 5 and
referenced by Task 3's signature list with an explicit note that its three claim members are
*implemented* in Task 5 — the one forward reference in the plan, made visible rather than left to be
discovered. `OutboxOperation` is declared in Task 4 and consumed by `EntityAfterHooks.For` in Task 7.
`CompiledAfterHook`/`CompiledAction` (Task 7) are consumed unchanged by Tasks 8–9.
`AlvoTemplate.Parse`/`Render` and `TemplatePlaceholder.TryResolve` (Task 6) keep their signatures in
Tasks 7–8. `IOutboxStore.ClaimAsync(claimant, batchSize, maxAttempts, lease, ct)` has one spelling
everywhere. `AlvoBootState.SettledAsync` is `internal` in every mention, which is what keeps it off
the public baseline.

**4. Three things this plan deliberately does differently from the addendum, each with its reason.**
Recorded here so a reviewer can judge them as decisions:

- **No `sequence` column at all** (D1), where the addendum treats `sequence` as present-but-unsurfaced
  and R1 frames the question as "which per-engine DDL". The third option — a UUIDv7 primary key — was
  in neither document, and it satisfies `SystemSchemaInitializer`'s invariant instead of breaking it.
  Gated on spike Q1/Q2/Q6/Q7, and **amended by them**: the key is monotonic only because
  `AlvoEventId` makes it so, which is the part neither document nor the first draft of this plan saw.
- **The CloudEvents SDK is test-only**, where the addendum allows a core-side mapper (D3). Nothing in
  PR5a needs the SDK at run time, and `package-boundary.md` says a dependency is earned.
- **The claim is raw SQL, not LINQ** (D2), where `data-path.md:121-145` and the register both expect
  PR5 to be the first PR whose `UseRelationalNulls()` cost binds. Meeting the constraint by
  construction is better than meeting it by memory; Task 13 Step 2 corrects the doc rather than
  leaving it predicting something that did not happen.

**5. What I found reason to doubt while tracing the two authorities.** Four items, each already
handled in the plan above and repeated here because a later reader should not have to re-derive them:

- The addendum's DoD assigns `complex-crm`'s five fixes to PR5b as though PR5a's shrinking of
  `UnhonouredFeatures` were risk-free. It *is* risk-free, but only because the example declares no
  after-hooks — which neither document states. Task 7 Step 6 pins it.
- The addendum's reworded `UnhonouredSubsystems` obligation is not in either DoD list, yet PR5a makes
  the `templates` and `webhooks` consequences **false** the moment an after-hook references one.
  Task 7 Step 4 fixes both, and adds the `secretRef`/HMAC absence, which is a *security* absence an
  author would otherwise assume was handled.
- The addendum's PR5a row says the after-hook action set "can start with `webhook` + `email`-to-console"
  as though `email` were optional. It is not: without it the template engine has no plain-string sugar
  slot and deviation 64's own consequence is untestable. D5 states that.
- R12's note that `ci.yml` is `timeout-minutes: 20` "for all of ring2" is the binding constraint on
  **two** tasks at once (11 and 12), not one, and `PagingPerformanceTests` already spends ~26 s of it.
  Both tasks carry an explicit budget rule with a named fallback, and Task 11's fallback preserves the
  event count because the count *is* the criterion.
