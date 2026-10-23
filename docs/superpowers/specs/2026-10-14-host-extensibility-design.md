# Host extensibility — host C# in the write pipeline, events, commands, time and reads

> **Umbrella design.** The approved shape of how embedded-host C# takes part in Alvo's write pipeline,
> its post-commit events, custom commands, time and reads — plus the descriptor-first items that keep
> host code from doing what the database does better. Each HX PR (§7) writes its own Superpowers plan
> against this document; a plan that contradicts it amends it in the same PR.
>
> **Status:** approved design (maintainer, 2026-10-14), revised after an adversarial review (3 blockers,
> 27 majors, 27 minors — dispositions in Appendix A). Source of intent: the page *"Alvo v hoste — host
> extensibility"* (artifact `HXWEQcjR9zEcH3VWMDc3kX`; its header reads *návrh v2 · 11. 10. 2026*, the
> published revision approved is the fourth) plus the maintainer's rulings D1–D5 (§3.2). Where the page
> and today's code disagree, **the code wins** and the correction is recorded (§2.2).

---

## 1. Goal and non-goals

### 1.1 Goal

A host developer — or a coding agent working in the host — can express in C# every piece of backend
logic the descriptor cannot, **through the same pipeline and the same guarantees** the descriptor's own
hooks use, and every such piece is **visible** to the dashboard, the Management API and the admin
assistant as metadata:

1. **Post-commit reaction**: typed event handlers (`AlvoCreated<T>`, `AlvoUpdated<T>`, `AlvoDeleted<T>`,
   `AlvoSaved<T>`, custom `record` events) on the transactional outbox, at-least-once, with per-delivery
   history, retries, dead-letter, redeliver, replay and pause.
2. **The write in three phases**: *validate* (outside any transaction, network allowed), *before-write*
   and *after-write* (in the transaction, no network), plus delete contexts whose `Old` is always set.
3. **Custom commands**: plain minimal API with the resolved caller and `TransactAsync`; named commands
   (`MapAlvoCommand`) and internal commands the simulator, catalog and assistant can see.
4. **Time**: keyed idempotent timers, cron schedules with one run across replicas, memoized steps.
5. **Reads**: an enricher that can only hide or fill declared virtual fields; a narrowing that can only
   AND a filter into SQL.
6. **Two first-class paths for every entity**: an untyped core (entity name + `AlvoRecord` + CEL string
   `when:`) and a typed layer (`Entity<T>`, generated or host types, lambdas translated at startup).
7. **Descriptor-first integrity**: `field.validation` → `CHECK`, `rollup.where`, `now()`/duration
   arithmetic, `sequence`, exclusion and partial indexes, declarative `timers`, `stateMachine`, relation
   rules and lookup `checks`.
8. **Docs and dashboard in every PR**: a docs section *Extending in code* with compiled, executed
   snippets, and an **extension catalog** (metadata only) in the Management API, `IAlvoManagement`, the
   dashboard and the assistant.

### 1.2 Non-goals (decisions, not omissions)

- **No row rewriting after SQL** (Parse `afterFind`, Dataverse Retrieve): breaks paging, counts and
  cursors and can disclose existence; reads may only hide or narrow (§4.12).
- **No network inside any open transaction**: a call holds row locks for a stranger's timeout (§4.4.5).
- **No onion `next()` pipeline** (PocketBase, Strapi): fixed linear stages are what the dashboard,
  simulator and agent can explain; timing comes from `ActivitySource`.
- **No exactly-once claim**: at-least-once; idempotency keyed on the stable event id.
- **No BPM engine**: memoized steps + timers; long human waits go to an adapter (new issue N-4).
- **No mediator**: minimal API + DI handlers + the outbox are the dispatch; MediatR is licence-banned.
- **No MCP surface in this epic**: the page's "to isté cez Management API a MCP" is met by the
  Management API; MCP stays an adapter over it (architecture rule), out of scope.
- **No csx** (#34, trust model) and **no pattern subscriptions** (`events.md` deviation 86) — out by
  the maintainer's ruling D3; **no batch hooks** (N-3), **no outbound signing** (#120), **no batch
  coalescing** (#193); **dynamic entities** (#41, store not built) receive the untyped core only, and
  every DDL-backed integrity item is refused on `storage: dynamic` until #41 defines its enforcement.

---

## 2. Sources and deviations

### 2.1 Sources read

- **Approved page**: all twelve API tabs, *Kam s logikou*, *Garancie*, *Viditeľnosť*, *Čo patrí do
  schémy*, *Fázy*, *Otázky*.
- **Spec** `alvo-specifikacia.md` §0, §0.5 (`:60-98`, incl. the `.Hooks(...)` sketch at `:74`), §1.2
  (`:124-152`), §1.3 (non-binding DX, `:153`).
- **Analysis** `baas-analyza.md` §2.7 (`:341-393`), §3.2–§3.4 (`:605-688`), §4, §7.3 (`:817-819`:
  cron once across replicas, DST, separate queues for long jobs), §10.
- **Architecture**: `events.md`, `extensibility.md` (rule 4 verb taxonomy), `package-boundary.md`,
  `data-path.md`.
- **Specs**: F3 PR5 addendum (R8, PR5b DoD), PR5b-2 subscriptions, F5 admin dashboard, F5 AI agent,
  startup lifecycle, F6 docs site.
- **Frozen artifacts**: `schema/project.schema.json`, `src/MMLib.Alvo.Abstractions/**`.
- **Code read, with every claim in §2.2/§4 checked**: `EfAlvoData` (2 943 lines), `IBeforeHookRunner`,
  `OutboxDispatcher`, `EventSubscriptions`, `IAlvoEvents`, `AlvoEvent`, `AlvoEventName`,
  `AlvoEventProvenance`, `AlvoEventAttributes`, `AlvoContext`, `IAlvoData`, `AlvoFilter`, `CelNode`,
  `AlvoConstraintKind`, `IAlvoSqlDialect`, `AddCelFunction`, `HostCelFunction`, `MapAlvo*`,
  `DataApiOperationMetadata`/`DataApiEndpointKind`, `CallerResolution`, `IAlvoContextAccessor`,
  `IAlvoManagement`, `ManagementModels`, `ManagementOperations`, `ManagementTools`, `AlvoBootService`,
  `UnhonouredFeatures`, `MMLib.Alvo.Testing.csproj`, `website/src/snippets/**`, `.github/workflows/docs.yml`.
- **Prior art adopted**: CloudEvents 1.0.2 + Distributed Tracing, authcontext and correlation
  extensions; Standard Webhooks; transactional outbox; Dataverse stages and depth; Salesforce governor
  limits; Inngest/Convex steps; PostgreSQL `EXCLUDE`, partial indexes, `CHECK`, savepoints; RLS
  `USING`/`WITH CHECK`; EF Core LINQ parameterisation; MassTransit `IConsumer<T>`; OpenTelemetry
  messaging span links; RFC 9457, RFC 5842.

### 2.2 Corrections — the page (or a doc) versus the code

| # | Assumed | Code | This design |
|---|---|---|---|
| C1 | `AlvoTestHost` in `MMLib.Alvo.Testing` | that project is Abstractions-only, `IsPackable=false` (its csproj; `EfDependencyBoundaryTests`) | new package `MMLib.Alvo.TestHost`, rule (a): drags the SQLite provider + `Microsoft.Extensions.TimeProvider.Testing` |
| C2 | `app.MapAlvo().ForEntity(…)` | `MapAlvo()` returns `IEndpointRouteBuilder`; `MapAlvoDataApi()` and `MapAlvoManagementApi()` return `IEndpointConventionBuilder` | `app.MapAlvoDataApi().ForEntity(…)` |
| C3 | `GET /management/extensions` | management routes are project-scoped | `GET {m}/projects/{p}/extensions` |
| C4 | cross-tenant job access is #42 | #42 is *Audit*, though `TenantResolver.cs:10`, `AlvoIdentityContextResolver.cs:117`, `AlvoAdminClaims.cs:26` defer that grant to #42 | new issue N-6; HX-13 repoints those comments |
| C5 | function `invoices.issue` | `$defs/identifier` = `^[a-z][a-z0-9_-]{0,62}$` | function names are identifiers: `issue-invoice` |
| C6 | function `"input": {…}` | `input` is `$defs/jsonata` (string) | HX-15 widens it (X12) |
| C7 | `.Hooks(…)` sketch at "spec:157" | it is `alvo-specifikacia.md:74` (and `baas-analyza.md:1304`) | cited in X2 |
| C8 | "every snippet is tested" | only `*.alvo.json` snippets are validated; C# under `website/src/snippets/embed/**` is compiled by nothing | `test/MMLib.Alvo.DocsSnippets.Tests` (HX-02) for `extending/**`; `embed/**` is #405 (§4.15 *HX-02 settled details*) |
| C9 | the envelope has an actor | `authtype` ∈ {`apikey`,`system`,`anon`} — not authcontext values — and identity users are reported `apikey` (`AlvoEventProvenance.AuthTypeOf`) | HX-01 aligns values, adds `AlvoContext.ActorKind` |
| C10 | `IAlvoData` has transactions | each `EfAlvoData` write creates its own `DbContext` + transaction | HX-07 introduces the internal write unit |
| C11 | step key `(event id, step)` | two handlers of one event may share step names | `(event id, subscriber, step)` |
| C12 | `IBeforeHookRunner` is the C# seam | it is sync and token-free by design (deviation 82) | a separate async port (§4.4); the declarative runner is untouched |
| C13 | `AuthorizedCandidate` runs before every write's transaction; `events.md:1164` says it moved inside | it runs only in `CreatedAsync` and `CreatedOrReplayedAsync` (`EfAlvoData.cs:250`, `:509`), before `BEGIN`; updates authorize inside the transaction (`WriteAsync` → `EnsureWriteAllowed`, `:1878-1890`); deletes have no pre-transaction policy check (`ErasedAsync`, `:1669`) | §4.1 states the real positions; HX-09 corrects `events.md:1164` |
| C14 | `ReplayableWriteAsync` is a storage-retry loop | it is the **idempotency-key race** loop: wraps only idempotent writes (`:410`, `:1073`, `:1253`, `:1659`, `:2402`), retries any `DbException`/`DbUpdateException` up to 10× | nothing new is wrapped in it (§4.5) |
| C15 | idempotent replays are detected before hooks | the replay lookup is **inside** the transaction (`records.FindAsync` at `:517`, `:1120`, `:1290`, `:1343`, `:1713`; batch `:2439`) | validate runs only after a pre-transaction key lookup misses (§4.1) |
| C16 | batches can run per-row work before `BEGIN` | `OneBatchAsync` begins the transaction (`:2433`) before any row is processed | batch validate is hoisted (§4.1) |

### 2.3 Deviations from source recommendations

| # | Source | What we do | Why |
|---|---|---|---|
| X1 | Spec §1.2 / security checklist: network ban "inexpressible / by analyzer" | declarative face keeps the structural guarantee (deviation 82); host C# gets network-free contexts, a **boot refusal** of network/data-port dependencies, an analyzer (`ALVO1001`), timeouts and an operation budget | host code is arbitrary C#; "inexpressible" is unachievable for it, and the docs say so |
| X2 | Spec `:74`: `.Hooks(h => h.BeforeUpdate<Order>(…))` + `HookResult` | `Entity<T>(…)` / `Entity("name", …)` + context rejection methods | the sketch is non-binding (`:153`) and assumes schema-as-code; durable state needs stable ids |
| X3 | Analysis §3.3: depth "~5" | one counter, default **8**, `Alvo:Extensions:MaxDepth` | it spans nested writes *and* event hops; a legitimate hook→write→hook→event→handler→write chain uses 5 |
| X4 | Analysis §3.3: data-action key derived from event id | handlers dedupe on `EventId` themselves | `alvo_idempotency` has no retention (#115) |
| X5 | Analysis §3.4: exponential backoff + jitter | handler deliveries: 5 s, 30 s, 2 min, 15 min, 1 h, 6 h, ±20 %, dead after attempt 7 | per-delivery state now exists |
| X6 | Page: handler `when` evaluated in the write transaction | evaluated at **fan-out**, post-commit, before any delivery row exists | identical semantics to descriptor after-hook conditions; no write amplification under the write's locks; one path for entity events, custom events and timers. The page's promise — an unmatched handler gets no delivery record — holds |
| X7 | Page: lambdas → CEL → SQL for queries and narrowing | queries and narrowing translate to the public **`AlvoFilter`** tree; only `when` translates to CEL | `AlvoFilter` is already rendered injection-safely and bounded by every driver; no new CEL profile and no new public AST node. One driver obligation remains: three literal string operators (R-M8, §4.3) |
| X8 | Page: `alvo.hook.statements` | `alvo.hook.operations` | counts port calls, not SQL statements |
| X9 | Page: narrowing reads `q.Request.Has(…)` | no HTTP request in the narrowing context | page principle 1: hooks bind to the data operation |
| X10 | Page: `.WithDescription("…")` chained | `configure: o => o.Description = "…"` on per-kind options | entity builders chain registrations; options classes grow without binary breaks |
| X11 | `events.md` deviation 85: `InMemoryAlvoData` owes hooks | discharged by `AlvoTestHost` over real SQLite; `InMemoryAlvoData` stays the provider-contract policy double and says so | a second in-memory pipeline is a second security core |
| X12 | Frozen schema: function `input` is a string | `oneOf [$defs/jsonata, object of $defs/valueOrExpr]` | the object form is `entity.update.payload`'s; additive |
| X13 | `events.md` deviation 88: custom events never transactional | `tx.PublishAsync`/`write.Publish` are transactional; `IAlvoEvents.PublishAsync` stays autocommit | a transaction now exists |
| X14 | `extensibility.md` rule 4: only `Use/Add/Enable/From/Apply` | grouping verbs `Entity`, `Entities` (EF `modelBuilder.Entity<T>` precedent); other registrations use `Add*`: `AddEventHandler`, `AddRetiredExtension`, `AddRequiredSecret`, `AddSchedule`, `AddFunction`, `AddCommand`, `AddDataMigration` | the page's `Entity<T>` grouping is the approved DX; rule 4 is amended in HX-03 to name the grouping verb |
| X15 | Analysis §2.7: a function over its timeout "is killed" | cancelled via its token, attempt marked failed | .NET has no thread abort; out-of-process runtimes are #35 |
| X16 | Page: `job.ForEachTenantAsync(…)` | `AddSchedule<TJob>(…, o => o.Scope = AlvoScheduleScope.EachTenant)` + required `IAlvoTenantCatalog` | no tenant registry exists; fail at boot, not at 02:00 |
| X17 | Page: depth overrun "structured error" | `508 Loop Detected`, type `chain-depth-exceeded`, documented **non-retryable** | RFC 5842's registered meaning; agents must not retry it |
| X18 | Page: generated `readonly partial struct` wrappers | generated **`sealed partial class`** wrappers | D2's "no `!`" needs `T?` to mean nullable for `Old`; for a struct `T`, `T?` on an unconstrained generic is `T` |
| X19 | One verb enum per concept | hooks use `AlvoWriteOperation { Create, Update, Replace, Delete }`; endpoint metadata uses the existing public `DataOperation` + a new public `AlvoEndpointKind` | `DataOperation` is the policy verb (no `Replace` policy exists; `ToWireName` throws on an unmapped member); hooks must tell Replace from Update |

---

## 3. Decisions

### 3.1 The page's open questions — ruled by its recommendation, except Q6 and Q8 (D3)

| Q | Ruling | Reason |
|---|---|---|
| Q1 Naming | `IValidateWriteHook`, `IBeforeWriteHook`, `IAfterWriteHook`, `IBeforeDeleteHook`, `IAfterDeleteHook`, `IAlvoEventHandler<T>` | one vocabulary in JSON and C#. **Hazard:** descriptor `afterCreate` is post-commit, C# `IAfterWriteHook` is in-transaction; mitigated by the catalog's `stage`, the docs table, and C# never using `AfterCreate/AfterUpdate` |
| Q2 Registration | `Entity<T>(…)`, `Entity("name", …)`, `Entities([...], …)`, `Entity("*", …)` | X2, X14 |
| Q3 Typed filter | predicate lambdas only (no `IQueryable`): `when` → CEL; queries/narrowing → `AlvoFilter` (X7) | untranslatable `when` fails at registration; an untranslatable query fails at its first call; translations cached per expression shape |
| Q4 Before-write writes other entities | yes, via `write.Transaction`, each nested write under a savepoint, full in-transaction pipeline, depth + 1 | otherwise audits get written outside the transaction |
| Q5 Before-write timeout | **250 ms** (after-write too), validate **5 s**, per registration; calibrated in HX-10 by a `scripts/test-load` scenario published in `docs/performance.md` | the page asks for a measured default |
| Q6 csx trust | **out of scope (D3)**; ruling recorded on #34: trusted operator code; no tenant or assistant writes it without human approval | maintainer |
| Q7 DLQ and per-record order | a dead delivery blocks later deliveries of the same handler for the same record by default; `Ordering = AlvoOrdering.None` opts out | order is correctness for state handlers |
| Q8 Pattern subscriptions | **out of scope (D3)**, although the page recommended segment-end wildcards | the wildcard refusal stands; designed namespace + cross-tenant suite is N-2 |
| Q9 Batch hooks | per-row now; contexts are sealed classes so `Batch` can be added (N-3) | page: keep it open |

### 3.2 Maintainer decisions (binding)

- **D1 Two first-class paths.** Untyped core for every entity; typed layer on top; mixable (typed entity +
  CEL string `when`). `Entities([...])` and `Entity("*", …)` are untyped. Both validated at boot.
- **D2 Typed messages per operation.** `AlvoCreated<T>` (`New`), `AlvoUpdated<T>` (`Old`, `New`),
  `AlvoDeleted<T>` (`Old`), `AlvoSaved<T>` (`New`, `Old` nullable). Validate / before-write / after-write
  contexts, plus before-delete and after-delete contexts with non-null `Old`. No `!` anywhere (X18).
- **D3** Questions ruled as above; csx and pattern subscriptions out.
- **D4** Docs and dashboard in every PR (per-PR rows in §7).
- **D5** The whole design is implemented, descriptor items included.

### 3.3 Further decisions

| # | Decision | Reason |
|---|---|---|
| E1 | **#315**: a built-in CEL function or hook arithmetic failing on the caller's data → **422** `expression-failed`; host function or host hook throwing → 500 `function-failed` / `hook-failed` | the caller can fix only the first |
| E2 | Host rejections reuse existing types: `Invalid` → 422 `validation`, `Conflict` → 409 `conflict`, `Forbidden` → 403 `forbidden`, each with `AlvoViolation { pointer, code, message }` | agents already parse them |
| E3 | **Id grammars, one per kind**: extension ids `^[a-z][a-z0-9-]{0,62}$`; command names `^[a-z][a-z0-9_]*(\.[a-z][a-z0-9_]*)*$`; function names `$defs/identifier`. All share one catalog namespace (a collision refuses boot). Event names derived from ids map `-` → `_` and must stay unique (injective check at boot). Boot refuses an id with pending durable state (deliveries, timers, steps) that is neither registered nor declared `AddRetiredExtension(id, AlvoRetirement.DeadLetter \| Discard)` | rename-safe identity; rolling deploys need an explicit drop |
| E4 | Extensions bind to the **boot project**; a host with extensions whose boot resolves more than one project is refused | embedded mode serves one project |
| E5 | `Cronos` (MIT, no dependencies) becomes a named core dependency (HX-13), recorded in `package-boundary.md` beside Corvus | cron + time zones + DST is prior art |
| E6 | `MMLib.Alvo.Analyzers` (netstandard2.0; analyzer in HX-10, generator in HX-17) is packed **inside** the `MMLib.Alvo` nupkg (`analyzers/dotnet/cs`). Rule (a) names Roslyn because a *runtime* Roslyn dependency is heavy; an analyzer asset adds nothing to a consumer's runtime closure (System.Text.Json ships its generator the same way). The generator bundles its own `System.Text.Json` reader copy and is verified by building a packed consumer on the minimum SDK in `global.json` | the analyzer must reach every hook author |
| E7 | Event images may be limited per entity in the descriptor (`entities.<e>.events.exclude`); `changed` keeps names; boot refuses a typed mapping or a handler `when` that reads an excluded field; apply refuses a revision that excludes a field an extension reads | the outbox is shared by every consumer |
| E8 | `AlvoRunAs.EventActor` rebuilds the caller from the envelope (`authid`, `tenantid`, `authclaims.roles` snapshot) — the actor *as at the event* | deterministic replay; documented |
| E9 | Validate hooks see `Old` from a policy-constrained, autocommit, non-locking read taken **only** when a validate hook is registered for that entity+operation; for update/delete it is the first pre-transaction policy read on those paths | zero cost when unused; TOCTOU is validate's documented nature |
| E10 | Durable extension state lives in framework tables created by `SystemSchemaInitializer` (`CREATE TABLE IF NOT EXISTS`, identical DDL, names via `AlvoFrameworkTables`), each created by the PR that first uses it | no `ALTER` of `alvo_outbox`; nothing created ahead of time |
| E11 | **"Audited"** means: a source-generated `ManagementAudit` log event at Warning (who, operation, target id, reason) **and** a row in `alvo_operations_log (id, at, actor, operation, target, reason)` created in HX-06 | #42 is not built; this is testable |
| E12 | Framework-minted event types live in a reserved namespace `alvo`: `alvo.schedule.<id>`, `alvo.command.<name>.completed`. `alvo` joins `AlvoEventName.ReservedNamespaces` in HX-03; `EventPatternTests.The_reserved_namespaces_are_the_schema_s_own` becomes "the schema's three, plus `alvo`", and `events.md` *wildcard ruling* records it | a host cannot forge a schedule run or a command completion |

---

## 4. Architecture

### 4.1 The pipeline and where each stage plugs into today's code

```
 source (Data API | IAlvoData | dashboard | command | handler | timer)
   ├─ 0 idempotency pre-lookup (autocommit; hit → replay, no hooks)   NEW for keyed writes
   ├─ 1 policy pre-check (create, today) / E9 pre-read                 EfAlvoData.AuthorizedCandidate
   ├─ 2 VALIDATE hooks — top-level writes only, no transaction open    NEW
   ├─ BEGIN (write unit)                                               HX-07
   │   ├─ 3 declarative before-hooks          IBeforeHookRunner.Run (sync)        (today)
   │   ├─ 4 BEFORE-WRITE hooks                IWriteExtensionRunner.BeforeWriteAsync
   │   ├─ 5 WITH CHECK + tenant scope + facets over the final candidate   (today, re-run after 4)
   │   ├─ 6 write + rollups                   InsertAsync / AffectedAsync / RecomputeRollupsAsync
   │   ├─ 7 AFTER-WRITE hooks                 IWriteExtensionRunner.AfterWriteAsync
   │   ├─ 7b post-image re-read if stage 7 wrote anything (event + echo use it)
   │   ├─ 8 emit                              EfAlvoData.EmitAsync → OutboxTable.InsertAsync
   │   └─ COMMIT (refused if the unit is poisoned)
   ├─ 9 fan-out (post-commit)                 OutboxDispatcher claim → DeliveryFanOut, before actions
   ├─ 10 declarative after-hooks              EventActionExecutor (today; HX-15 moves onto deliveries)
   └─ 11 deliveries                           DeliveryDispatcher
```

**Where today's code actually is (C13).** Create paths authorize before `BEGIN` (`CreatedAsync:250`,
`CreatedOrReplayedAsync:509`); update paths authorize inside (`WriteAsync:1878-1890`); deletes have no
pre-transaction check. Stage 2 therefore needs E9's pre-read for update/delete; create reuses the
authorized candidate.

**Stage 2 rules (B1).**
- Validate runs only for a **top-level** write — a write with **no unit on the flow** (§4.5). Writes through a
  unit's `IAlvoTransaction` (a `TransactAsync` delegate, a hook's `write.Transaction`, a command's
  `ctx.TransactAsync`) do **not** run validate; any other data-port call while a unit is on the flow is refused
  (§4.5, R-B1). The catalog marks validate hooks `TopLevelOnly`, and the docs say a command performs its
  external check **before** `ctx.TransactAsync` (§4.9). Reason: no network while any transaction is open.
- **Batches** (`*ManyAsync`) run stage 2 for every row, with E9 pre-reads, **once, outside** the
  `ReplayableWriteAsync` loop and before `OneBatchAsync` opens its transaction (single-row keyed writes
  likewise run stage 2 once, outside their loop); a refusal becomes that row's `AlvoRowRefusal` and refuses the batch (existing
  all-or-nothing contract) without opening a transaction.
- **Idempotent writes** first look up their key with one autocommit read (stage 0). A hit runs
  `EnsureSameRequest` (a different fingerprint → `AlvoIdempotencyConflictException`, as in-transaction) and
  replays with no hooks at all (the rule `events.md` states for the declarative runner); only a miss proceeds to stage 2 and
  then to the in-transaction lookup, which stays authoritative (a race between the two is still resolved by
  the existing in-transaction lookup and `ReplayableWriteAsync`, unchanged).

**Stages 3–5.** After the declarative patch, the host runner receives the merged candidate; the union
patch then passes one re-run of `EnsureWriteAllowed` (WITH CHECK, tenant scope), and every host `Set`
value passes `MutationTarget.Violation` (facets, `required`, decimal widening — Ruling V), failing as
500 `hook-failed` reason `invalid-set` naming the extension. `WritePayloadGuard` is not re-run
(deviation 75's ruling applies to host hooks too).

**Stages 6–8.** After-write runs after rollups and reads `Saved` unmasked through the unit. If any
after-write hook wrote through `write.Transaction`, the post-image is re-read before emit and echo, so the
event carries the committed rollup values. An after-write never writes its own row (§4.5).

**Batches and replays.** Batch rows run stages 3–7 per row inside the batch transaction; replays (stage 0
hit or in-transaction hit) run no hooks.

### 4.2 Registration: untyped core, typed layer

Registration is an extension method on `IAlvoBuilder` (rule 2: no interface members). Each call adds an
internal `ExtensionRegistration` singleton — the `CelFunctionRegistration` precedent. Builders are
**sealed classes** with internal constructors, so new verbs are non-breaking.

```csharp
builder.Services.AddAlvo(alvo => alvo
    .UseSqlite(cs).FromDescriptor("booking.alvo.json")
    .Entity<Booking>(b => b                                      // typed: generated or [AlvoEntity("bookings")]
        .Validate<VatIdIsRegistered>("vat-in-vies",
            when: w => w.Changed(x => x.VatId), configure: o => o.Timeout = TimeSpan.FromSeconds(3))
        .BeforeWrite<WithinOpeningHours>("within-opening-hours",
            when: w => w.Changed(x => x.StartsAt, x => x.EndsAt, x => x.ResourceId))
        .OnUpdated<SendBookingConfirmation>("send-confirmation",
            when: e => e.Changed(x => x.Status) && e.New.Status == BookingStatus.Confirmed))
    .Entity("tickets", t => t.OnUpdated("ticket-resolved-to-slack",   // untyped, CEL string checked at boot
        when: "changed(status) && new.status == 'resolved'",
        handler: (AlvoUpdated<AlvoRecord> e, ISlackNotifier slack, CancellationToken ct) =>
            slack.PostAsync($"Ticket {e.New["number"]} resolved", ct)))
    .Entities(["orders", "invoices"], e => e.BeforeWrite<StampUpdatedBy>("stamp-updated-by"))
    .Entity("*", e => e.AfterWrite<AuditTrail>("audit-trail"))
    .AddEventHandler<OrderApproved, ReleaseApprovedOrder>("release-approved-order",
        configure: o => o.RunAs = AlvoRunAs.EventActor)
    .AddSchedule<RollSlots>("roll-slots", cron: "0 2 * * *", timeZone: "Europe/Bratislava")
    .AddFunction<IssueInvoice>("issue-invoice"));
```

**`Entity("*", …)`** binds to every entity of the boot project, including entities added by later applies;
it accepts write hooks and handlers only, untyped, and its `when` may not name a field — only
`@user`/`@tenant` tests — so it binds no field and the apply guard does not apply to it.

**Boot validation** runs where declarative hooks compile: `PolicyCatalogBuilder` gains an
`ExtensionBindingCompiler` pass (one priming site, R11). It refuses **all problems at once** in one
`AlvoStartupRefusedException`, each with id, kind, locator (`extensions/send-confirmation/when`) and a fix:
unknown or removed entity; a `when` that fails in its profile (typo) or reads an image the operation lacks
(`old.` on `OnCreated`, `new.` on `OnDeleted` — the #325 rule shared with `AfterHookCompiler`); a `when` or
typed mapping reading an `events.exclude` field (E7); a typed property with no field or an incompatible
`FieldClrType`; duplicate or colliding ids (E3); pending state for an unknown id (E3); a forbidden dependency
on an in-transaction hook (§4.4.5); a delegate parameter not resolvable from DI (`IServiceProviderIsService`).
Equal `Order` on one entity+stage only **warns** (`ExtensionOrderTie`).

**Runtime apply and rollback** run the same compiler against the proposed revision. Removing an entity or
field an extension binds to, or adding it to `events.exclude`, is refused with
`DescriptorValidationError { Path = "/entities/bookings/fields/status", Message = "… bound by extension
'send-confirmation' (eventHandler, in this host) …", Fix = "Remove the registration and redeploy first, or
keep the field." }`.

### 4.3 Translation of lambdas

One internal translator, two targets (X7):

| C# | `when` → CEL | query / narrowing → `AlvoFilter` |
|---|---|---|
| `x.Prop` (field via `[AlvoField]`, else snake_case) | `new.status` / `status` | comparison on the field |
| `record["field"]` (constant key) | `new.field` | field |
| `e.Old.X` | `old.x` (refused where the stage has no pre-image) | refused |
| `e.Changed(a, b)` | `changed(a) \|\| changed(b)` | refused |
| `== != < <= > >=`, `&& \|\| !` | same | `eq neq lt lte gt gte`, `and`/`or`/`not` groups |
| `+ - * /` | in `Condition` | refused |
| `x == null`, `HasValue` | `== null`, `!= null` | `is null`, `not is null` |
| `StartsWith/EndsWith/Contains(string)` | `startsWith/endsWith/contains` | new literal operators `AlvoFilterOperator.StartsWith/EndsWith/Contains` (appended), each driver rendering them with its own escaping (`LIKE … ESCAPE` with `%`, `_` and the escape char escaped) — never `Like`, whose pattern semantics differ per engine today (`FilterSqlRenderer.cs:126` emits no `ESCAPE`; filed as N-7) |
| `list.Contains(x)`, `x.In(a, b)` | `x in [a, b]` | `in` (≤ `AlvoFilter.MaxInCandidates`) |
| enum member | the field's `values` string (`[AlvoEnumValue]` or snake_case; boot checks every member) | same |
| `e.Caller.HasRole(r)`, `e.Caller.Tenant` | `'r' in @user.roles`, `@tenant.id` | refused (the port ANDs the caller's policy) |
| `DateTimeOffset.UtcNow` / `.Now` | `now()` (from HX-11; refused before it) | refused — pass a captured value |
| subtree not touching the parameter | **only** a constant, a `static readonly` field or an enum member, rendered as a typed CEL literal | any value, evaluated **per call**, placed as a typed value (bound as a parameter by the driver) |

Everything else (method calls on fields, closures over mutable state in `when`, `Select`, `GroupBy`,
`Join`, field-to-field comparisons in queries) throws `AlvoTranslationException`:

> `Cannot translate 'e.New.Note.GetHashCode()' in extension 'send-confirmation' (when): method
> 'GetHashCode' has no CEL equivalent. Fix: write the condition as a CEL string — when: "…" — or move the
> check into the handler body.`

CEL literals come from one escaping writer; a CsCheck property asserts `parse(render(s)) == s`. No new CEL
profile, no new AST node, no `@args`.

### 4.4 Write hooks

#### 4.4.1 Contexts (D2)

| Context | `Operation` | `Old` | `New` | Extra | Can |
|---|---|---|---|---|---|
| `ValidateWrite<T>` | `AlvoWriteOperation` Create/Update/Replace | `T?` (null on Create) | `T` merged | `Data` (`IAlvoSession` bound to the caller), `Caller` (`AlvoContext`), `Now` | `Invalid/Conflict/Forbidden` |
| `BeforeWrite<T>` | Create/Update/Replace | `T?` | `T` (PATCH merged) | `Transaction`, `Caller`, `Now` | reject, `Set`, `Publish` |
| `AfterWrite<T>` | Create/Update/Replace | `T?` | — | `Saved` (`T`: id, defaults, rollups), `Transaction` | reject (rollback), `Publish` |
| `BeforeDelete<T>` / `AfterDelete<T>` | Delete | `T` non-null | — | `Transaction`, `Caller` | reject, `Publish` |

`T` is a class (generated wrappers are classes, X18; host types are records/classes; `AlvoRecord` is a
class), so `T?` is a real nullable. Untyped hooks implement non-generic interfaces over the same contexts
with `T = AlvoRecord`; `Set("field", v)` is untyped, `Set(x => x.F, v)` typed.

#### 4.4.2 Rejection model

`Invalid(code, message, field?)`, `Conflict(…)`, `Forbidden(code, message)` **record** a rejection; when
the hook returns, a recorded rejection stops the pipeline (later hooks do not run, the rejecting hook's
`Set`s are discarded, the unit rolls back). Precedence when one hook records several kinds: 403 > 409 >
422; `errors` carries every violation of the winning kind. `field:` becomes an RFC 6901 pointer, **except on
a field with any `hidden` flag**, where the violation carries no pointer and no field name (Ruling X).
`IAlvoData` callers receive `AlvoWriteRejectedException { Kind, Code, Pointer, ExtensionId, Violations }` — a
**core** type (`MMLib.Alvo.Hooks`), because `Violations` are the core's `AlvoViolation` and only the core runner
throws it;
`AlvoExceptionHandler` maps it per E2. After rollback an autocommit insert records it in
`alvo_hook_rejections (at, extension, entity, operation, kind, code, actor)` — no payload, no caller text.

A host exception → 500 `hook-failed` (extension id in `extensions`; the exception type is logged, its
message never echoed). Timeout / operation budget / invalid `Set` → 500 `hook-failed` with `reason`; depth
→ 508.

#### 4.4.3 Ordering

Declarative hooks (descriptor order) → host hooks by `Order` (default 0) → registration order; `"*"`
registrations run after entity-specific ones of the same `Order`.

#### 4.4.4 Budgets and poisoning

Per invocation: `Timeout` (250 ms; validate 5 s) through a linked `CancellationToken` passed to every
`write.Transaction` call; a hook that ignores it is measured on return. `MaxOperations` (50) counts calls
through `write.Transaction`; the 51st throws `AlvoBudgetExceededException`. **Timeout, budget and depth
failures poison the unit**: even if host code catches the exception, the commit throws and rolls back (§4.5).

#### 4.4.5 Network and data-port ban for in-transaction host code

At boot, constructor parameters (class hooks) and delegate parameters (inline hooks) of before-write,
after-write, before-delete and after-delete registrations are checked against: `HttpClient`,
`IHttpClientFactory`, `System.Net.Sockets.*`, `System.Net.Http.*`, `DbConnection`, `IEmailSender`,
`IAlvoEvents`, `IAlvoData`, `IAlvoSessions`, `IAlvoTransactions`, `IOutboxStore`. (`IAlvoScheduler` is
allowed: inside a unit it joins the unit's transaction, §4.10.) A hit
refuses boot naming the parameter. Direct dependencies only; the analyzer `ALVO1001` (HX-10) flags
`HttpClient`/`Socket`/`DbConnection`/`IAlvoData` use and any `await` on a task not produced by
`write.Transaction` inside an in-transaction hook type, and the same in `AddCelFunction` delegates (#309).
The `events.md` architecture fact extends to `WriteExtensionRunner`: nothing reachable from its constructor
exposes a network or data port.

#### 4.4.6 Security re-run and system access

`write.Transaction` acts **as the caller**. `write.Transaction.AsSystem()` returns a view whose context is
`AlvoContext.System(caller.Tenant) with { Provenance = caller.Provenance, ActorKind = System }` — never
cross-tenant, depth preserved — logs `ExtensionSystemAccess` (Warning: extension, entity, operation) and
increments `alvo.hook.system_access`. `TransactAsync(…, new AlvoTransactionOptions { SuppressHostHooks =
true, Reason = "…" })` is accepted only with a system context; it skips stages 2, 4 and 7 (never the
declarative hooks, never emission) and is audited (E11).

### 4.5 Transactions and the write unit (B3)

`EfAlvoData` write sites move onto an internal `WriteUnit`: it owns the **connection and the
`DbTransaction`** plus DI scope, depth, in-flight rows, operation counter, timeout and poisoned flag. A
top-level write opens and commits its own unit (unchanged behaviour). `IAlvoTransactions.TransactAsync` opens
one unit and hands the delegate an `IAlvoTransaction`; hooks receive the same unit's `IAlvoTransaction`.

- **One short-lived `DbContext` per nested write** (R-M1). Each write inside a unit builds its own
  `AlvoDataContext` enlisted with `Database.UseTransactionAsync(unit.Transaction)` on the unit's connection —
  the existing per-write-context shape — so a failed insert's tracked entries die with that context
  (`InsertAsync` is `Add` + `SaveChangesAsync`, `EfAlvoData.cs:906-913`, and `ChangeTrackerReachTests` forbids
  detaching them by hand).
- **Savepoint per nested write.** Every write inside a unit runs under `CreateSavepointAsync` /
  `RollbackToSavepointAsync`. A refused or engine-failed nested write rolls back to its savepoint and the
  unit stays usable **on both engines** — without it PostgreSQL aborts the whole transaction (`25P02`) while
  SQLite continues, breaking the engine-agnostic core. (EF's own `SaveChanges` savepoints do not cover the
  `ExecuteUpdate`/`ExecuteDelete` and raw statements of the update/delete paths.)
- **The unit is on the flow, and nothing else may touch the database while it is (R-B1).** `AlvoFlow` carries
  the open unit as well as provenance. Every `IAlvoData`, `IAlvoSession`, nested `TransactAsync`, and
  `IAlvoEvents.PublishAsync` entry that finds a unit on its flow is **refused** with
  `AlvoUnitInProgressException` (Abstractions / Data, an `InvalidOperationException`: "a data call was made
  while a transaction unit is open on this flow; use the unit's `IAlvoTransaction` (`ctx.Transaction`,
  `write.Transaction`) instead"). **Refuse, not join**, because joining would let a call carrying its own
  `AlvoContext` (often a fresh `System`) write inside the caller's transaction — a silent privilege mix — and
  would turn an autocommit `PublishAsync` silently transactional; refusing is deterministic on both engines
  and replaces an undetected PostgreSQL self-deadlock / SQLite 30 s busy wait with an immediate error.
  `IAlvoScheduler` is the one exception: it writes its timer row through the open unit (§4.10).
- **Unit timeout.** `AlvoTransactionOptions.Timeout` (default 30 s, top-level units use the same default)
  cancels the unit's commands and poisons it; a unit holds SQLite's single writer lock and must not hold it
  indefinitely.
- **Poisoning.** Timeout, operation-budget and depth failures, and any exception escaping a hook, poison the
  unit; `CommitAsync` then throws the original failure and rolls back.
- **No retry by Alvo.** A `TransactAsync` is never re-run. SQLite writer contention is absorbed by
  `Microsoft.Data.Sqlite`'s 30 s busy timeout at `BEGIN IMMEDIATE`; any other storage failure surfaces to the
  caller (deterministic violations must not re-run host code). `ReplayableWriteAsync` stays the
  idempotency-key race loop around top-level keyed writes only (C14).
- **Idempotency inside a unit** → `ArgumentException`; a command's idempotency is `MapAlvoCommand`'s (§4.9).
- **One DI scope per unit** (`IServiceScopeFactory.CreateAsyncScope()`), shared by the unit's hooks; it is
  **not** the HTTP request scope (request-scoped host services are not visible to hooks).
- **Self-write.** A nested write to a row currently in a hook stage of the same unit → 500 `hook-failed`,
  reason `self-write`.

### 4.6 Provenance and one depth counter (B2)

`AlvoContext` gains `Provenance` (`AlvoProvenance { CorrelationId, CausationId, Depth, TraceParent,
TraceState }`) and `ActorKind` (`Unknown | Anonymous | System | ApiKey | User`).

Provenance is **explicit and ambient**. An internal `AsyncLocal` in Abstractions (`AlvoFlow`: provenance +
the open unit) is reachable by every assembly Abstractions grants `InternalsVisibleTo`; an architecture fact
(HX-01) holds that only `MMLib.Alvo` and `MMLib.Alvo.Data.EntityFrameworkCore` call its setters. It is set by the write unit (for its duration), `DeliveryDispatcher` (per delivery), `TimerPump` and
`SchedulePump` (per firing). Every port entry computes the **effective** provenance with the public
`AlvoEventProvenance.Effective(AlvoContext)`: depth = `max(explicit, ambient)`; correlation/causation =
explicit when set, else ambient. Host code can raise depth, never lower it. Consequently:

**Who adds +1 (R-M2).** `DeliveryDispatcher`, `TimerPump` and `SchedulePump` set the ambient depth to the
envelope's `chaindepth + 1` for the run. A top-level write's depth = `Effective(context).Depth`. A unit's
nested write's depth = the unit's depth + 1. The event a write emits carries `chaindepth` = that write's depth.
A **write, publish or schedule whose depth is greater than `MaxDepth`** (`AlvoExtensionOptions.MaxDepth`,
`Alvo:Extensions:MaxDepth`, default 8) is refused; depth 8 itself is allowed.

- a handler calling a DI-injected `IAlvoData` with a fresh `AlvoContext.System(tenant)` still writes at the
  delivery's depth (= event depth + 1);
- `AsSystem()` preserves provenance (§4.4.6);
- `IAlvoScheduler` (singleton) reads the effective provenance and tenant at the call; outside any flow it
  records depth 0 and requires the `tenant:` argument when tenancy is enabled.

| Hop | Correlation | Causation | Depth |
|---|---|---|---|
| HTTP request → write | W3C trace id, else new | — | 0 |
| write → nested write | same | — | +1 |
| write → event | same | — | `chaindepth` = write depth |
| event → handler writes | same | event id | event depth + 1 |
| handler → timer → fired event | stored on the timer | timer's causation | stored + 1 |
| schedule run | new per run | — | 0 |

**Enforcement sites (R-M3).** The refusal is the public `AlvoChainDepthExceededException` (Abstractions /
Data), documented on `IAlvoData` as a **seventh exception family** and mapped to 508 `chain-depth-exceeded`
(non-retryable) by `AlvoExceptionHandler`. It is thrown by: the EF driver's write entry points (every
top-level write and batch, before its transaction opens; every nested unit write, which also poisons the
unit); `IAlvoEvents.PublishAsync` and `IAlvoScheduler.ScheduleAsync` (core); and fan-out, which creates no
delivery for an envelope whose `chaindepth + 1 > MaxDepth` but records a `dead` delivery with reason
`chain-depth-exceeded` per matching subscriber and logs `ChainDepthExceeded` at Error (R-M5 — loops without a
write stop too). A provider-contract row in `MMLib.Alvo.Testing`'s data suite makes every `IAlvoData`
implementation honour it; `InMemoryAlvoData` implements it (it reads `AlvoEventProvenance.Effective`). A
delivery whose write is refused is dead-lettered with that reason (not retried). **Limit, stated:** depth follows the .NET
execution context; host code that hands work to its own queue (`Channel`, its own `BackgroundService`) or
suppresses flow leaves the counter's reach, and the docs say so.

### 4.7 Event envelope changes (HX-01)

Pre-release (no v0.1; PLAN F6), so attribute values change without a `payloadversion` bump; a legacy reader
keeps rows already in `alvo_outbox` readable.

| Attribute | Today | After HX-01 |
|---|---|---|
| `authtype` | `apikey`/`system`/`anon` | from `ActorKind`; authcontext values `api_key`/`user`/`system`/`unauthenticated`/`unknown`; consts `ApiKey = "api_key"`, `User`, `System`, `Unauthenticated`, `Unknown`; `Anonymous` **removed** so stale compiled comparisons fail at build |
| `authclaims` | — | authcontext attribute; JSON string `{"roles":[…]}`; stripped from the **default** webhook body (an author template carries roles only through an explicit `{{@user.roles}}`) |
| `tenantid` | — | Alvo extension; the write's tenant; absent for global rows / tenantless system writes |
| `causationid`, `chaindepth` | absent / 0 | from effective provenance |
| `traceparent`, `tracestate` | — | Distributed Tracing extension (registered in 1.0.2) |
| `data.record` / `old_record` / `changed` | unmasked images | minus `events.exclude` fields; `changed` keeps names (E7) |

`@tenant.id` and `@user.roles` become resolvable in after-hook conditions and templates for envelopes that
carry the attributes (closes #153); an envelope without them still answers "absent", fail-closed. The
`#153` texts in `EnvelopeProvenance` and `UnhonouredFeatures.WildcardSubscription` are repointed to N-2
(wildcards stay refused, D3).

**HX-01 settled details.**
- **`ActorKind` default.** `AlvoContext.ActorKind` is an init property whose unset value is **derived from
  `User`** exactly as `AuthTypeOf` derives today: anonymous user → `Anonymous`, the system user → `System`,
  any other → `ApiKey`. So host-built and third-party-resolver contexts keep emitting `api_key` (same meaning
  as today's `apikey`); `ApiKeyContextResolver` sets `ApiKey` explicitly, the Identity cookie resolver sets
  `User`; `Unknown` is only ever set explicitly.
- **Legacy reader.** `AlvoEventJson.Read` **normalises** legacy values on read: `apikey` → `api_key`, `anon`
  → `unauthenticated`; the outbox row itself is not rewritten.
- **`events.exclude` shape.** Schema: `entities.<e>.events` = `{ "exclude": [identifier, …] }`
  (`additionalProperties: false`, `uniqueItems`); each name must be a declared field; `id`, `tenant_id` and the
  managed audit/version/soft-delete columns cannot be excluded (they key the event). Public model member
  `EntitySchema.EventImageExclusions` (`IReadOnlyList<string>`, init, default `[]`). Apply refuses — with the
  pointer of the referencing slot and a fix — any declarative after-hook `condition`, template placeholder
  (`email.to`/`subject`/`body`, webhook `payload`) that reads an excluded field via `new.`/`old.`; the default
  webhook body simply omits the fields. HX-04 adds the same refusal for extensions (E7).

*Rulings taken while HX-01 was built (2026-10-14), recorded so a later reader can tell them from oversights:*
- **`events.exclude` items** use the field-name grammar `^[a-z][a-z0-9_]{0,62}$`, not `$defs/identifier`, whose `-`
  no field can hold. The descriptor model gains public `EntityDescriptor.Events` + `EntityEvents { Exclude }` (§5).
  Excluded-field refusals dedupe by field: one refusal per field per slot.
- **Correlation id.** When a caller states a W3C `traceparent` but no correlation id, `correlationid` is that
  `traceparent`'s trace id, so the correlation and the trace context an envelope carries name one flow. A non-W3C
  ambient `Activity` is ignored (its placeholder trace id would put every such event in one flow).
- **`{{@user.roles}}` in an author-declared template is allowed** (a webhook `payload`, an email template); only the
  **default** webhook body strips `authclaims`, because nobody chose to post it.
- **Templates.** `{{@tenant.id}}` renders `tenantid`; `{{@user.roles}}` renders the role names comma-joined in
  ordinal order; both render empty on an envelope without them, as `{{@user.id}}` does for an anonymous write.
- **An empty `{"roles":[]}` is unreadable** and fails closed like an absent or malformed `authclaims`: Alvo never
  writes an empty role list, and the context's stand-in role must not decide a condition.
- **The #325 image rule covers template placeholders as well as conditions** (`email.to`, a named template's
  `subject`/`body`, webhook `payload`). A shared named template is checked against each referencing hook's point and
  blamed at that hook's `/action/template` pointer, because only that hook's point makes it unanswerable.
- **508 mapping sites.** `ProblemResultFactory.GuardAsync` (where the Data API maps every `IAlvoData` family) and
  `AlvoExceptionHandler` (a refusal escaping an Alvo route) both answer through
  `ProblemResultFactory.ChainDepthExceeded`. A host's own route keeps its own 500, as for every other family
  (`FailedInsideAlvo`). The document carries `"retryable": false`; no other problem carries the member. The slug is
  reachable over HTTP today only through an explicit `Provenance.Depth`.
- **`InMemoryAlvoData`** enforces the default limit (8); it has no configuration to read.
- **Assistant awareness** of `events.exclude` and of `@tenant.id`/`@user.roles` in after-hooks (the
  `alvo-descriptor-*` skills) is HX-04's, with the catalog and the `alvo-descriptor-hooks` region it already owns
  (§4.13).
- **Cross-tenant suite (HX-01 acceptance 6)** covers each tenant writing its own rows, with a mirror hook proving
  `@tenant.id` resolves; a tenantless system write cannot be added there, because the scoped entity's tenant guard
  refuses it before any event exists.
- **Follow-up, not in HX-01:** an apply-time warning when a hook on a **global** entity reads `@tenant.id` — its
  events never carry `tenantid`, so the hook can never fire.

### 4.8 Delivery model

**Tables** (identical DDL; created by the PR named):

```sql
alvo_deliveries (id TEXT PK, event_id TEXT NOT NULL, subscriber TEXT NOT NULL, partition_key TEXT NOT NULL,
  tenant_id TEXT NULL, kind TEXT NOT NULL /* handler|function|schedule|migration|action */,
  status TEXT NOT NULL /* pending|running|succeeded|retrying|dead|superseded|cancelled */,
  attempts INTEGER NOT NULL, not_before TEXT NOT NULL, lease_until TEXT NULL, claimed_by TEXT NULL,
  completed_at TEXT NULL, last_error_type TEXT NULL, UNIQUE (event_id, subscriber))           -- HX-03
CREATE INDEX ix_deliveries_claim ON alvo_deliveries (status, not_before);                    -- HX-03
CREATE INDEX ix_deliveries_lane  ON alvo_deliveries (subscriber, partition_key, event_id);   -- HX-03
alvo_delivery_attempts (delivery_id, attempt, started_at, duration_ms, outcome, error_type)  -- HX-03
alvo_subscriber_controls (subscriber PK, paused_at, paused_by, reason, disabled_at, disabled_by)   -- HX-06
alvo_steps (event_id, subscriber, step, result, completed_at, PK (event_id, subscriber, step))     -- HX-14
alvo_concurrency_slots (key, slot, delivery_id NULL, lease_until NULL, PK (key, slot))             -- HX-14
```

No bodies, URLs or exception messages are stored; the envelope stays in `alvo_outbox`.

**Fan-out.** For each claimed outbox entry, in id order, `DeliveryFanOut` runs **first** — before the
declarative actions — evaluating each registered subscriber's compiled `when` over the envelope and
inserting one row per match (`UNIQUE(event_id, subscriber)` makes re-fan-out a no-op). **A `when` that
throws is an outcome, not a failure (R-M6):** that subscriber gets a `dead` delivery with `last_error_type =
when-failed` (its lane blocks per Q7; redeliverable after a fix). Only a **storage** failure of the fan-out
insert stops processing, and only for that entry's `partitionkey`: later entries of other keys in the batch
proceed, later entries of the same key are released with it. Lane order is `event_id` order.
**Per-record order holds under the outbox's own condition: one outbox dispatcher and one emitting process
per millisecond (#150 still applies).** A subscriber receives events fanned out after its registration on
the fanning replica; rolling deploys use the single-dispatcher shape (`Alvo:Events:Enabled` on one replica).

**Claim.** One `UPDATE … RETURNING` per batch with the outbox's EvalPlanQual-safe shape, restricted to
`subscriber IN (@registered)`, `not_before <= @now`, no pause control, and — for ordered subscribers —
`NOT EXISTS` an earlier delivery of the same subscriber and `partition_key` whose status is
pending/running/retrying/dead. A claim sets `status = running`, `lease_until = now + timeout + 30 s`; an
expired lease is claimable again (crash recovery). Completion and
failure are **fenced**: `UPDATE … WHERE id = @id AND claimed_by = @me AND attempts = @n`; a late finisher whose
lease was reclaimed changes nothing and logs `DeliveryLeaseLost`. Options: `AlvoDeliveryOptions`
(`Alvo:Deliveries`).

**Worker model.** `Alvo:Deliveries:MaxConcurrency` (default 16) for handler/function/action deliveries; a
separate pool `Alvo:Deliveries:MaxJobConcurrency` (default 2) for `schedule`/`migration` so a long job
cannot starve handlers (analysis §7.3). Default timeouts: handler 30 s, job 15 min, per registration.

**Semantics.** At-least-once; same `EventId` every attempt; own DI scope per delivery; one failing
subscriber blocks only its own (subscriber, record) lane; retries X5; `dead` after attempt 7.
`AlvoRunAs.System` (default) = `System(event tenant)`; `AlvoRunAs.EventActor` = E8. Metrics on
`MMLib.Alvo.Events`: `alvo.delivery.duration`, `alvo.delivery.attempts{outcome}`, `alvo.delivery.lag`,
`alvo.delivery.dead`.

**Operations (HX-06)** through a new port `IAlvoOperations`, defined complete in HX-06:

| Route | Level | Effect |
|---|---|---|
| `GET {m}/projects/{p}/deliveries?subscriber&status&from&to&after` | Viewer | keyset page, no payload |
| `GET …/deliveries/{id}` | Viewer | delivery + attempts (+ steps from HX-14) |
| `POST …/deliveries/{id}/redeliver` | Developer | reset to pending; audited (E11) |
| `POST …/deliveries/replay` `{subscriber, from, to, status, dryRun}` | Developer | dry run → count + first 20 ids; real run resets them; `Idempotency-Key`; audited |
| `GET …/deliveries/{id}/fixture` | Developer | envelope + subscriber; **every non-key scalar redacted** (`<redacted:field>`; id, ref, enum kept) |
| `PUT …/subscribers/{id}/paused` `{paused, reason}` | Developer | pause/resume; audited |
| `PUT …/extensions/{id}/enabled` `{enabled, reason}` | Admin | validate hooks only (the page limits it to them); audited; other kinds → 409 |
| `POST …/schedules/{id}/run` | Developer | run now; unknown id → 404 |
| `GET …/extensions/stats` | Viewer | per-instance p50/p95, invocations, rejections, failures over 24 h; `scope: "this-instance"` |

### 4.9 Commands and the HTTP layer

- **Caller binding.** `.WithAlvoCaller()` is an endpoint filter (applied by `MapAlvoCommand`, opt-in on
  plain endpoints) that resolves the caller asynchronously through the same `CallerResolution` the generated
  routes use, publishes it via `IAlvoContextAccessor`, and returns the existing 401/403 problem for a refused
  credential (no credential → `Anonymous`). The handler takes an `AlvoCaller` parameter
  (`IBindableFromHttpContext<AlvoCaller>`, `.Context`), because `AlvoContext` lives in Abstractions, which
  may not reference ASP.NET Core. No scoped `AlvoContext` is registered.
- **`MapAlvoCommand(name, pattern, access, handler)`** — name per E3, input schema from the body type
  (OpenAPI), `access` in `CelProfile.Access` over `@user`/`@tenant`. **`MapAlvoCommand<TEntity>(…)`** binds
  `{id}` to the row through a policy `get` as the caller (unreadable → 404) and evaluates `access` in
  `CelProfile.Condition` with the row as `record.`; a typed access lambda is translated like `when`. The
  handler is **not** wrapped in a transaction (R-M7): `AlvoCommandContext { Caller, Name, Data (IAlvoSession),
  TransactAsync(work) }`, so an external call (payment, VIES) goes before `ctx.TransactAsync`. A handler may
  call `ctx.TransactAsync` **at most once** (a second call throws); the completion event
  `alvo.command.<name>.completed` (E12) and the `Idempotency-Key` record (scope = command name + caller) are
  written **in that unit** at its commit. A handler that never opens a unit gets both written in one
  autocommit transaction after it returns a 2xx. An empty access rule refuses registration.
- **Internal commands.** `AddCommand<TCommand>(name)` with `IAlvoCommand<TInput>`, invoked by timers,
  handlers and jobs through `IAlvoCommands.InvokeAsync(name, input, AlvoContext, ct)`; no route; same access
  rule, transaction and completion event; catalog kind `Command` with `Route = null`.
- **Inbound webhooks.** `MapAlvoWebhookReceiver(pattern, secretName, handler)` verifies Standard Webhooks
  (`webhook-id`, `webhook-timestamp` ± 5 min, `webhook-signature` HMAC-SHA256, multiple signatures, rotation),
  dedupes on `(pattern, webhook-id)` in `alvo_inbound_webhooks` (7-day sweep), and runs the handler as
  `AlvoContext.System(tenant)` with the tenant from `AlvoWebhookReceiverOptions.Tenant` /
  `.ResolveTenant(request)`.
- **Endpoint metadata.** Public `AlvoEndpointMetadata(string Entity, DataOperation Operation,
  AlvoEndpointKind Kind)` on every generated route **beside** the internal `DataApiOperationMetadata`, which
  stays the source for `AlvoDocumentTransformer` (it reads `Kind`, incl. `Query`). `ForEntity(entity,
  configure)` applies inner conventions only to matching endpoints. **#184**: a `Finally` convention verifies
  each marked endpoint still carries Alvo's context filter; if not, the route-table build fails through
  `AlvoDataApiConventionException` (readiness `Failed`).

### 4.10 Time and steps

- **Timers.** `IAlvoScheduler.ScheduleAsync(key, at, message, tenant?)` upserts `alvo_timers (scope, key,
  generation, due_at, message_type, payload, provenance, lease_until)`, scope = effective tenant (or `""`);
  same key = move and `generation + 1`; `CancelAsync(key)` deletes. Inside a unit, `ScheduleAsync`/`CancelAsync`
  write through the unit's transaction (`ITimerStore.UpsertAsync` contract: an implementation that owns the
  unit on the flow enlists in it; the EF store does), so a rolled-back write leaves no timer — the path HX-18's
  declarative timers use. `TimerPump` claims due rows with a lease,
  then calls `ITimerStore.FireAsync(scope, key, generation, event)`, which in **one transaction** deletes
  `WHERE scope AND key AND generation = @g` and appends the event to `alvo_outbox` only if one row was deleted
  — a reschedule or cancel during the claim wins. A provider implements `ITimerStore` and `IOutboxStore` over
  one database; the contract suite asserts both rows commit together.
- **Framework events.** `alvo.*` events cannot pass `AlvoCustomEvent.Create`/`EnsureCustom` (E12); the core
  and driver mint them through an internal `AlvoFrameworkEvent.Create` (Abstractions, IVT) that requires the
  `alvo.` prefix; `ITimerStore.FireAsync` takes the already-built `AlvoEvent`.
- **Schedules.** `AddSchedule<TJob>(id, cron, timeZone, configure)`; `alvo_schedules (id PK, next_run_at,
  lease_until, leased_by, last_run_at, last_outcome)`. `SchedulePump` claims a due schedule and calls
  `ITimerStore.AdvanceScheduleAsync`, which in one transaction appends `alvo.schedule.<id>` (one per tenant
  for `EachTenant`, from `IAlvoTenantCatalog`) and stores the next occurrence (Cronos, in the zone). The job
  runs as a `schedule` delivery: retries, history, DLQ, run-now for free. `AlvoJobContext { ScheduledFor,
  Tenant?, Data (IAlvoSession, system in that tenant) }`.
- **Data migrations.** `AddDataMigration<T>(id, afterRevision)` runs once, as a `migration` delivery, after
  the boot or apply that reaches the revision, as `System(null)` iterating tenants via `IAlvoTenantCatalog`
  when tenancy is enabled; a rollback below the revision neither undoes it nor re-runs it later
  (`alvo_data_migrations`).
- **Steps (HX-14).** `IAlvoSteps.RunAsync(name, work)` memoizes the JSON result (C11).
  `SleepUntilAsync(name, at)` schedules a timer `step:{delivery}:{name}` and ends the attempt as `skipped`;
  firing re-queues the delivery. `Concurrency = AlvoConcurrency.PerKey("tenant" | "record", limit)` is
  enforced by **slot rows** claimed with the same conditional `UPDATE … RETURNING` (claimable only with a
  free slot; released at completion or lease expiry), never by a `COUNT` predicate. `Debounce = TimeSpan`:
  a newer pending delivery on the lane supersedes the older.

### 4.11 Functions and automation (HX-15)

`AddFunction<TFunction>(name)` with `IAlvoFunction<TInput>` registers a keyed service and a catalog entry
with an input JSON Schema from `TInput` (`JsonSchemaExporter`). A descriptor `{"type":"function",
"name":"issue-invoice","input":{…}}` resolves to the host function; a name present both as a host function
and in the `functions` (csx) block is refused at apply. Apply validates `input` against the schema (typo =
structured error with fix) and refuses an unregistered name with a `NameSuggestion`. Execution is a
`function` delivery. Declarative after-hook actions and the `automation` block (event and schedule triggers;
`webhook`, `email`, `function`, `entity.update`) run as `action` deliveries, each action retried
independently. Still refused at apply, by name: `delivery: "batch"` (#193), `http.call`, a wildcard
`eventPattern` (deviation 86 stands although `tenantid` exists — N-2). `AddCelFunction` stays pure,
synchronous, in-transaction.

### 4.12 Reads (HX-16)

- `IRecordEnricher<T>.Enrich(Enrich<T> row)` — synchronous, after authorization, per returned row, on Data
  API, `IAlvoData` reads and write echoes. `Hide(field)` removes a field (narrowing `IAlvoData`'s
  returned-key-set contract, whose docs are updated); `SetVirtual(field, value)` fills a field declared
  `"virtual": true` (schema addition: read-only, in OpenAPI, refused in write payloads, every CEL slot and
  the `filter`/`sort`/`select` keys). Boot ban as §4.4.5.
- `IQueryNarrowing<T>.Narrow(QueryNarrowing q)` returns `Expression<Func<T,bool>>?` (untyped: an
  `AlvoFilter`), ANDed into the compiled SQL for list, get and count of **non-system** callers (handler
  `System` contexts and `AsSystem` are not narrowed). Writes are not narrowed. The simulator lists narrowings.

### 4.13 Extension catalog

```csharp
public sealed record ManagementExtension
{
    public required string Id { get; init; }   public required AlvoExtensionKind Kind { get; init; }
    public required AlvoExtensionStage Stage { get; init; }
    public IReadOnlyList<string> Entities { get; init; } = [];  public IReadOnlyList<string> Operations { get; init; } = [];
    public string? Event { get; init; }  public string? When { get; init; }  public int Order { get; init; }
    public TimeSpan? Timeout { get; init; }  public string? RunAs { get; init; }  public string? Concurrency { get; init; }
    public string? Ordering { get; init; }  public string? Route { get; init; }  public string? HttpMethod { get; init; }
    public string? Access { get; init; }  public string? Cron { get; init; }  public string? TimeZone { get; init; }
    public IReadOnlyList<string> Secrets { get; init; } = [];  public string? Description { get; init; }
    public string? TypeName { get; init; }  public bool TopLevelOnly { get; init; }
    public bool Enabled { get; init; } = true;  public bool Paused { get; init; }
}
// Kind: Validate, BeforeWrite, AfterWrite, BeforeDelete, AfterDelete, EventHandler, Function, Command,
//       Schedule, DataMigration, Enricher, Narrowing      Stage: PreTransaction, InTransaction, PostCommit, Read, Http, Scheduled
public sealed record ManagementExtensionCatalog { public required IReadOnlyList<ManagementExtension> Items { get; init; } }
```

`When` is always text (CEL; narrowing shows its filter). `TypeName` is null unless
`Alvo:Extensions:ExposeTypeNames=true`. Reached by `IAlvoManagement.GetExtensionsAsync(project)` →
`GET {m}/projects/{p}/extensions` (Viewer). The capability report lists registered kinds under `Honoured`.
`ManagementPolicyVerdict` gains `Extensions` as an **init** property defaulting to `[]` (not positional).

**Dashboard** (via `IAlvoManagement`/`IAlvoOperations` only; `BoundaryArchitectureTests` stays green):

| Surface | What appears | PR |
|---|---|---|
| Entity list (`SchemaList.razor`) | badge `⟨/⟩ n` | HX-04 |
| Entity detail (`Entity.razor`) | *Lifecycle* tab: *Before the transaction*, *In the transaction*, *After commit*, *Reads*, *Commands*; descriptor items editable, code items read-only (colour + icon), `when`, timeout, run-as, p95, 24 h rejections | HX-04 post-commit; HX-08 commands; HX-09 write lanes; HX-16 reads |
| Rules/hooks editors | note "1 extension from code also runs on this operation" | HX-09 |
| Policy simulator + apply dry run | `Extensions` list | HX-09 |
| Operations page (new) | handlers: history, pause, replay with count, redeliver, fixture; schedules; timers | HX-06, HX-13 |
| Apply preview | binding refusal with the extension id | HX-04 |

**Assistant**: new read tool `get_extensions` (the sixth `get_*` read); `alvo-descriptor-hooks` gains the
region "descriptor or code" and "read the catalog before proposing a rule an extension enforces"; two eval
cases (names the extension instead of duplicating it; refuses to propose removing a bound field).

### 4.14 Observability

`ActivitySource("MMLib.Alvo.Hooks")`: `alvo.hook {id}` per write-hook invocation (tags extension id,
entity, operation, stage, outcome); `alvo.delivery {subscriber}` per attempt, a consumer span **linked** to
the envelope's `traceparent`. `Meter("MMLib.Alvo.Hooks")`: `alvo.hook.duration`, `alvo.hook.rejections`,
`alvo.hook.operations` (X8), `alvo.hook.system_access`. Logs carry ids — never URLs, bodies, secrets or host
exception messages. The test host's `Hooks.AssertRan` listens to this `ActivitySource`.

### 4.15 Testing kit (HX-02)

```csharp
await using var alvo = await AlvoTestHost.StartAsync(b => b.FromDescriptor("booking.alvo.json")
    .Entity<Booking>(e => e.OnSaved<ArmReminder>("arm-reminder")), time: fakeTime);
await alvo.Data.CreateAsync(…, staff);            // real pipeline, SQLite in-memory (shared, kept open)
await alvo.DrainEventsAsync();                    // outbox → fan-out → deliveries → due timers, until quiet
alvo.Hooks.AssertRan("arm-reminder", times: 1);
```

Dispatchers run in `AlvoEventDispatchMode.Manual` (public option on `AlvoEventOptions`), driven by the
public `IAlvoEventPump.DrainAsync()`. `DrainEventsAsync` stops after 100 rounds and throws
`AlvoDrainNotQuiescentException` naming looping subscribers. `UsePostgreSql(cs)` is accepted for parity
tests. `DeliverAsync(DeliveryFixture)` arrives in HX-06.

**Docs-snippet harness.** `test/MMLib.Alvo.DocsSnippets.Tests` (ring0) compiles
`website/src/snippets/extending/**/*.cs` as ordinary source files of the test project. Extending snippets are
therefore **classes and static configuration methods** (`public static IAlvoBuilder
AddBookingExtensions(this IAlvoBuilder alvo)`), never top-level `Program.cs`; each page has one fact that
runs its snippet through `AlvoTestHost`. Pages excerpt them with the existing `SourceExcerpt` anchors. No
Roslyn dependency is needed.

**HX-02 settled details.**
- **A round.** `IAlvoEventPump.DrainAsync()` runs one round of every dispatcher and returns `AlvoDrainResult { Claimed,
  Failed, Subscribers, IsQuiet }`; neither public type names a dispatcher. The one implementation is the internal
  `EventPump`, which in HX-02 runs one tick of `OutboxDispatcher` (`ManualRoundAsync`); HX-03 and HX-13 add their
  dispatchers as further steps of the same round and sum the results — a step, not a second public seam. A round
  refuses, claiming nothing, unless `DispatchMode == Manual` and `Enabled`, and unless the boot is `Ready` with a primed
  catalog; rounds are serialised and the gate is not re-entrant (a subscriber must never drain). In `Manual` the loop
  logs `DispatcherManual`; `MMLib.Alvo.Host` refuses `Manual` at startup with the fix. A misspelled mode is refused
  before the binder sees it, with the same sentence as an undefined number.
- **An in-process stub receiver writes back outside the dispatcher's flow.** `StubWebhookReceiver` runs its write-back
  under `ExecutionContext.SuppressFlow()`, as an out-of-process receiver writing over HTTP would. From HX-03 the
  dispatchers set the ambient depth (§4.6) for each run; a write-back inheriting it would be refused at depth 9 and the
  R-12 loop fact would go quiet while the real loop is infinite. HX-03 must keep the suppression, not delete the fact.
- **The source exists from HX-02.** `MMLib.Alvo.Hooks` carries one `alvo.delivery` consumer span per descriptor
  after-hook action (tags `alvo.subscriber` = the hook's JSON pointer, `alvo.event.id`, `alvo.event.type`,
  `alvo.outcome`), because `AlvoHookRecorder` (a §5 HX-02 row) needs something to read; HX-03 adds handler deliveries
  and the `traceparent` link (its acceptance (13)), HX-09 the write-hook spans. A descriptor after-hook's id is its JSON
  pointer.
- **The recorder** counts every run whose outcome is present and not `failed` (a run cut short by shutdown carries no
  outcome; HX-09's `rejected` counts as ran) and throws `InvalidOperationException` listing every run on a mismatch. It
  keeps only activities whose trace is one of its host's drains (each drain is a root activity `alvo.test.drain` on
  source `MMLib.Alvo.TestHost` with a fresh trace id), so parallel hosts never see each other's runs. HX-09's write
  hooks, which run outside a drain, will need the host to scope its `Data` calls the same way — which is why no fact
  pins `Data`'s identity and the docs say only that it behaves as the host's `IAlvoData`; writes made through
  `Services.GetRequiredService<IAlvoData>()` stay outside the recorder's reach.
- **`AlvoTestHost` members:** `StartAsync(configure, time = null, webhooks = null, cancellationToken)`, `Data`,
  `Services`, `Hooks`, static `Caller(params string[] roles)`, `DrainEventsAsync` (returns the summed
  `AlvoDrainResult`), `DisposeAsync`. Default clock = system clock (`FakeTimeProvider` reaches tests transitively).
  Without `webhooks`, a refusing handler fails every webhook attempt; a caller's handler is never disposed. Loop naming
  = the distinct subscribers of the last 10 rounds.
- **PostgreSQL parity** = a provider registered in `configure` (detected by its `ISchemaMigrator` registration)
  replaces the in-memory database. The PostgreSQL leg links the shared facts into
  `MMLib.Alvo.Data.PostgreSql.Tests.Integration` (ring2) instead of creating a project. Reading of acceptance (4): the
  facts that carry an acceptance criterion or touch the engine (reject, clock, delivery, loop) run on both engines; the
  package's engine-agnostic facts (isolation, recorder scoping, no-network default, receiver lifetime, start refusal)
  run on SQLite only.
- **Package boundary.** `MMLib.Alvo.TestHost` is the one shipped package that depends on a provider (stated in
  `package-boundary.md`), takes `Microsoft.AspNetCore.App` as a framework reference, and grants no
  `InternalsVisibleTo`; the webhook client name and the source name are literals pinned by behavioural facts. DocsGen
  lists it from HX-02 — `CSharpApiTests.The_package_list_is_every_packable_src_project` requires every packable
  project — with `AlvoTestHost` in `ApiScope.Ports`.
- **Harness conventions.** Each snippet `.cs` declares a type named after its file (`SnippetCoverageTests` proves the
  glob compiled them), descriptor snippets are copied flat to the output and so carry page-unique names, the project
  opts out of the shared architecture tests and removes the test-wide global usings. Acceptance (6) holds by
  construction: a snippet is a source file of a ring0 project. `CA1707` is off for the harness project, because
  `test/.editorconfig`'s relaxation does not reach files under `website/`. "Each page has one fact that runs its
  snippet" stays a convention while there is one page; a fact enforcing it is worth adding at the third.
  `SourceExcerpt`'s `to` became optional (omitted, the excerpt runs to the file's last non-blank line), in the Astro
  component and in DocsGen's `llms-full.txt` renderer alike, so a whole snippet class renders through the component
  that strips the BOM and CRLF rather than through a bare `<Code>` on a raw import.
- **C8 is half discharged, deliberately.** C8 names `website/src/snippets/embed/**` (four top-level `Program.cs`
  files) as compiled by nothing; §4.15 scopes the harness to `extending/**`, whose snippets are classes. Top-level
  programs cannot share one assembly, so compiling them needs one project per snippet (or a Roslyn compile step §4.15
  rules out); HX-02 leaves them out and files the follow-up issue #405.

---

## 5. Public API (complete approval list)

**P** = public under approval; everything not listed is internal. DIM = default interface member. Every
`PublicApi.*.verified.txt` growth is justified against this table. Shapes: §4.

| Symbol | Package / namespace | PR | Notes |
|---|---|---|---|
| `AlvoProvenance`; `AlvoContext.Provenance`, `.ActorKind`; `AlvoActorKind` | Abstractions / `MMLib.Alvo` | 01 | init properties |
| `AlvoEventAuthType` consts renamed (`Anonymous` removed) | Abstractions / Events | 01 | breaking, CHANGELOG |
| `AlvoEventAttributes.TenantId/.AuthClaims/.TraceParent/.TraceState`; same on `AlvoEvent` | Abstractions / Events | 01 | init |
| `AlvoEventProvenance.Effective(AlvoContext)` | Abstractions / Events | 01 | reads internal `AlvoFlow` |
| `AlvoChainDepthExceededException` | Abstractions / Data | 01 | seventh `IAlvoData` exception family → 508 |
| `AlvoExtensionOptions` (`Alvo:Extensions`: `MaxDepth`; `ExposeTypeNames` added in 04) | core / `MMLib.Alvo.Extensions` | 01 | sealed class, validated at start |
| `EntitySchema.EventImageExclusions` | Abstractions / Schema | 01 | init, default `[]`; descriptor model `EntityDescriptor.Events` + `EntityEvents { Exclude }` (Abstractions / Descriptor), init |
| `AlvoProblemTypes.ChainDepthExceeded` | core / Api | 01 | 508, non-retryable; `AlvoEventAuthType` normalised legacy reader |
| `AlvoTestHost`, `AlvoHookRecorder`, `AlvoDrainNotQuiescentException` | TestHost | 02 | new package |
| `IAlvoEventPump`, `AlvoDrainResult`, `AlvoEventDispatchMode`, `AlvoEventOptions.DispatchMode` | core / Events | 02 | |
| `AlvoEntityBuilder`, `AlvoHookOptions`, `AlvoHandlerOptions`, `AlvoRetryPolicy`, `AlvoRetirement`, `AlvoRunAs`, `AlvoOrdering` (`PerRecord` and `None`, both implemented here), `AlvoDeliveryOptions` (`Alvo:Deliveries`) | core / `MMLib.Alvo.Extensions` | 03 | sealed classes / enums |
| `Entity(string, …)`, `Entities(…)`, `AddEventHandler<TMessage,THandler>`, `AddEventHandler(id, type, Delegate)`, `AddRetiredExtension` | core / `Microsoft.Extensions.DependencyInjection` | 03 | X14 |
| `IAlvoEventHandler<TMessage>` (`Task HandleAsync`), `AlvoCreated<T>`, `AlvoUpdated<T>`, `AlvoDeleted<T>`, `AlvoSaved<T>`, `AlvoEventContext`, `EventCondition<T>`, `AlvoEventAttribute` | core / Events | 03 | messages: sealed records, `required init` |
| `IAlvoSession` (context-bound, autocommit per call; reads + writes with `AlvoPrecondition?`), `IAlvoSessions.For(AlvoContext)` | Abstractions / Data | 03 | used by `AlvoEventContext.Data`, `ValidateWrite.Data`, `AlvoJobContext.Data` |
| `IDeliveryStore` + `DeliveryStoreContractTests` | Abstractions / Events; Testing | 03 | provider boot obligation (`package-boundary.md`) |
| `AlvoEventName.ReservedNamespaces` gains `alvo` | Abstractions / Events | 03 | E12 |
| `ManagementExtension`, `ManagementExtensionCatalog`, `AlvoExtensionKind`, `AlvoExtensionStage`; `IAlvoManagement.GetExtensionsAsync`; `ManagementPolicyVerdict.Extensions` (init) | Abstractions / Management | 04 | interface member breaks implementers: CHANGELOG |
| `Entity<T>(…)`, `AlvoEntityBuilder<T>`, `IAlvoEntity`, `AlvoEntityAttribute`, `AlvoFieldAttribute`, `AlvoEnumValueAttribute`, `AlvoRecordExtensions.As<T>()`, `AlvoTranslationException` | core | 05 | |
| typed reads `GetAsync<T>`, `QueryAsync<T>(Expression<Func<T,bool>>, …)` on `IAlvoSession`/`IAlvoData` | core (extensions) | 05 | translate to `AlvoFilter` |
| `IAlvoOperations` + `ManagementDelivery`, `ManagementDeliveryQuery`, `ManagementReplayRequest`, `ManagementReplayPlan`, `ManagementExtensionStats`; `DeliveryFixture`, `AlvoTestHost.DeliverAsync` | Abstractions / Management; TestHost | 06 | complete here |
| `IAlvoTransactions.TransactAsync<T>`, `IAlvoTransaction` (writes with `AlvoPrecondition?`, batch verbs, `PublishAsync`, `AsSystem()`), `AlvoTransactionOptions` (`Timeout`, `SuppressHostHooks`, `Reason`), `AlvoUnitInProgressException`; `AlvoFilterOperator.StartsWith/EndsWith/Contains` (appended, driver obligation + contract rows) | Abstractions / Data | 07 | **new port**; `IAlvoData` unchanged; `InMemoryAlvoData` does not implement it (documented) |
| typed writes `CreateAsync<T>`, `UpdateAsync<T>(Guid, IAlvoPatch<T>)`, `ReplaceAsync<T>`, `ExistsAsync<T>`; `IAlvoPatch<T>` | core | 07 | |
| `AlvoCaller`, `WithAlvoCaller()`, `MapAlvoCommand`, `MapAlvoCommand<TEntity>`, `AlvoCommandContext`, `AddCommand<T>`, `IAlvoCommand<TInput>`, `IAlvoCommands`, `MapAlvoWebhookReceiver`, `AlvoWebhookReceiverOptions`, `AlvoEndpointMetadata`, `AlvoEndpointKind`, `ForEntity` | core | 08 | |
| `AlvoWriteOperation` | Abstractions | 09 | X19 |
| `IValidateWriteHook(<T>)`, `IBeforeWriteHook(<T>)`, `IAfterWriteHook(<T>)`, `IBeforeDeleteHook(<T>)`, `IAfterDeleteHook(<T>)` (`ValueTask`), contexts `ValidateWrite<T>`, `BeforeWrite<T>`, `AfterWrite<T>`, `BeforeDelete<T>`, `AfterDelete<T>`, `WriteCondition<T>` | core / `MMLib.Alvo.Hooks` | 09 | |
| `IWriteExtensionRunner`, `WriteInvocation` (required-init record) | Abstractions / Rules | 09 | driver port, like `IBeforeHookRunner` |
| `AlvoWriteRejectedException` (core / Hooks), `AlvoBudgetExceededException` (Abstractions / Data); `AlvoProblemTypes.HookFailed` | core; Abstractions | 09 | R-M9 |
| `AlvoProblemTypes.ExpressionFailed`; `AddCelFunction` overload with trailing `CancellationToken` | core | 10 | #315, #309 |
| `AlvoConstraintKind.Check` | Abstractions / Data | 11 | appended |
| `AlvoConstraintKind.Exclusion`; `IAlvoSqlDialect.ExclusionConstraint(…)` | Abstractions; EF driver | 12 | **DIM** returning null (precedent `GeneratedColumnDefinition`, `IAlvoSqlDialect.cs:312`); null → apply refuses `exclude` on that engine |
| `IAlvoScheduler`, `IAlvoScheduledJob`, `AlvoJobContext`, `AlvoScheduleOptions`, `AlvoScheduleScope`, `IAlvoTenantCatalog`, `IAlvoDataMigration`, `AddSchedule<T>`, `AddDataMigration<T>`, `ITimerStore` + `TimerStoreContractTests` | core; Abstractions; Testing | 13 | |
| `IAlvoSteps`, `AlvoConcurrency`, `AlvoHandlerOptions.Concurrency`/`Debounce` behaviour | core / Events | 14 | |
| `IAlvoFunction<TInput>`, `AlvoFunctionContext`, `AlvoFunctionSkippedException`, `AddFunction<T>`, `AddRequiredSecret` | core | 15 | |
| `IRecordEnricher<T>`, `Enrich<T>`, `IQueryNarrowing<T>`, `QueryNarrowing`; `FieldSchema.IsVirtual` | core; Abstractions / Schema | 16 | |
| (generator output only) | Analyzers in the `MMLib.Alvo` nupkg | 17 | |
| schema-model members for `stateMachine`, `timers` | Abstractions / Schema | 18 | |
| schema-model members for relation rules and `checks` | Abstractions / Schema | 19 | |

No `CelProfile` member and no CEL AST node is added (X7).

---

## 6. Engine parity and security core

### 6.1 Engine parity (SQLite, PostgreSQL; Azure SQL stays a port, #92)

| Mechanism | SQLite | PostgreSQL | Rule |
|---|---|---|---|
| deliveries / timers / schedules / slots claim | `UPDATE … RETURNING`, autocommit | same; EvalPlanQual-safe outer `WHERE` | identical SQL; contract suites on both |
| nested write failure in a unit | savepoint rollback, unit usable | savepoint rollback avoids `25P02` | parity fact (HX-07) |
| `field.validation` → `CHECK` | table rebuild by the EF SQLite migrator after a violation scan | `ADD CONSTRAINT … NOT VALID` + `VALIDATE` | literal writer; `now()` refused; decoded as `Check` |
| `sequence` | `UPDATE alvo_sequences … RETURNING` in the write's transaction | same, row lock | gapless; rollback undoes |
| `indexes[].exclude` | writers serialize on `BEGIN IMMEDIATE`; the in-transaction overlap check sees the committed row → 409 | `EXCLUDE USING gist` + `btree_gist` (trusted since PG 13; apply refuses with a fix if unavailable) | DIM; decoded as `Exclusion` |
| `indexes[].where` | `CREATE UNIQUE INDEX … WHERE` | same | literal writer |
| relation rules | correlated `EXISTS` | same | related `USING` ANDed inside |
| dynamic entities (#41) | `CHECK`, `sequence`, `exclude`, `where` refused | refused | message names #41 |
| Azure SQL | — | — | every new statement is ANSI or a dialect member; `TSqlSqlDialect` double extended |

Every PR touching SQL runs its integration facts on **both** engines (ring2, Testcontainers).

### 6.2 Security-core impact

| Risk | Guard |
|---|---|
| host hook writes what the caller could not | caller-bound `write.Transaction`; WITH CHECK + facet re-run; `AsSystem` tenant-bound, logged |
| cross-tenant leak via events | `tenantid`; handlers run `System(event tenant)`; two-tenant adversarial facts (HX-01, 03, 13) |
| network/data port in a transaction | §4.4.5 boot ban, analyzer, budgets, arch fact; validate only outside transactions (§4.1) |
| unbounded loops | effective depth (§4.6), poisoning, 508 |
| injection via translator / `CHECK` / relation rules | `AlvoFilter` typed values; one literal writer with `parse(render(s)) == s`; CsCheck "captured strings never in SQL text" (HX-05, 11, 19) |
| commands | access before handler; empty access refused; simulator parity |
| inbound webhooks | Standard Webhooks verification, window, per-receiver dedup |
| reads | narrowing only ANDs; enricher only removes or fills virtual fields |
| hidden-field disclosure | Ruling X in host rejections and in `Check`/`Exclusion` decoding |

**PRs requiring the `alvo-security-core-review` checklist, a `/security-review` (or labelled substitute) and
`needs-deep-review`:** HX-01, 03, 05, 07, 08, 09, 11, 12, 13, 15, 16, 18, 19.

---

## 7. PR decomposition — a linear stack

Each PR is based on the previous PR's branch; **HX-01 is based on `main`** and carries this document. The
maintainer may merge a reviewed prefix at any time (recommended after HX-04 and after HX-10); the next PR is
then rebased onto `main`. Every PR: its Superpowers plan; docs pages under
`website/src/content/docs/extending/` with snippets in `website/src/snippets/extending/<page>/` run by
`test/MMLib.Alvo.DocsSnippets.Tests` (from HX-02 on); its dashboard slice; CHANGELOG; the architecture doc of
its mechanism; `alvo-plan-guard`; the PR report. **Each approved item and each acceptance criterion is owned
by exactly the PR it is listed under.**

| # | Branch | Base | Title |
|---|---|---|---|
| HX-01 | `hx/01-envelope-provenance` | `main` | Event envelope attributes and provenance |
| HX-02 | `hx/02-test-host` | `hx/01-envelope-provenance` | `MMLib.Alvo.TestHost` and the docs-snippet harness |
| HX-03 | `hx/03-event-handlers` | `hx/02-test-host` | Event handlers on the delivery engine (untyped core) |
| HX-04 | `hx/04-extension-catalog` | `hx/03-event-handlers` | Extension catalog, apply guard, dashboard and assistant |
| HX-05 | `hx/05-typed-layer` | `hx/04-extension-catalog` | Typed layer and lambda translation |
| HX-06 | `hx/06-delivery-operations` | `hx/05-typed-layer` | Delivery operations |
| HX-07 | `hx/07-transactions` | `hx/06-delivery-operations` | Write unit, `TransactAsync`, savepoints |
| HX-08 | `hx/08-commands-http` | `hx/07-transactions` | Commands, inbound webhooks, HTTP conventions |
| HX-09 | `hx/09-write-hooks` | `hx/08-commands-http` | Write hooks in three phases |
| HX-10 | `hx/10-hook-guards` | `hx/09-write-hooks` | Analyzer, host-function budget, `expression-failed`, calibration |
| HX-11 | `hx/11-integrity-check-now` | `hx/10-hook-guards` | `CHECK`, `rollup.where`, `now()` and durations |
| HX-12 | `hx/12-integrity-sequence-exclusion` | `hx/11-integrity-check-now` | `sequence`, exclusion and partial indexes |
| HX-13 | `hx/13-timers-schedules` | `hx/12-integrity-sequence-exclusion` | Timers, schedules, data migrations |
| HX-14 | `hx/14-steps-flow-control` | `hx/13-timers-schedules` | Steps and flow control |
| HX-15 | `hx/15-functions-automation` | `hx/14-steps-flow-control` | Functions, automation runtime, secrets |
| HX-16 | `hx/16-reads` | `hx/15-functions-automation` | Enricher, narrowing, virtual fields |
| HX-17 | `hx/17-source-generator` | `hx/16-reads` | Generated typed entities |
| HX-18 | `hx/18-state-machine-timers` | `hx/17-source-generator` | `stateMachine` and declarative timers |
| HX-19 | `hx/19-relation-rules` | `hx/18-state-machine-timers` | Relation rules and lookup `checks` |

### HX-01 — Event envelope attributes and provenance
- **Scope:** §4.7 attributes and consts; `AlvoContext.Provenance`/`ActorKind`, resolvers set `ActorKind`;
  internal `AlvoFlow` + `AlvoEventProvenance.Effective`; `MaxDepth` refusal (508, non-retryable);
  `entities.<e>.events.exclude` (schema + four schema test types); lift `@tenant.id`/`@user.roles` refusals
  (#153) and repoint the #153 texts to N-2; after-hook phase refusal (#325); apply refusal of declarative
  after-hook conditions/templates reading excluded fields; `AlvoChainDepthExceededException` at the EF write
  entry points and `IAlvoEvents.PublishAsync`, mirrored in `InMemoryAlvoData` and a provider-contract row;
  `AlvoExtensionOptions`; `ActorKind` derivation and the normalising legacy reader (§4.7 *HX-01 settled
  details*); the default webhook body strips `authclaims`; arch fact on `AlvoFlow` setters; `docs/PLAN.md` gains a *Host
  extensibility* line under F7 linking this spec. `AlvoFlow` has no production setter yet (HX-03/HX-07 add
  them); its rows are unit tests setting it through the IVT grant.
- **Docs:** *Extending in code* landing, *Where logic goes* (the page's decision table), envelope reference
  (descriptor snippets only). **Dashboard:** field editor toggle *Exclude from event images*.
- **Acceptance:** (1) `CloudEventsConformanceTests` pins the new envelope; the oracle accepts every
  attribute (unit). (2) A legacy `apikey`/`anon` payload reads back
  normalised to `api_key`/`unauthenticated` (unit); a host-built context with a non-reserved user still emits
  `api_key`; the Identity resolver's context emits `user`. (3) `AlvoDataOutboxTests`, both
  engines: `tenantid` present for scoped, absent for global; excluded fields absent from both images and
  present in `changed`. (4) A write at depth 9 → `AlvoChainDepthExceededException` / 508 on both engines, depth 8 accepted; the problem
  doc marks it non-retryable. (5) Ambient beats explicit: explicit depth 0 under ambient 9 is refused, under
  ambient 8 accepted (unit). (5a) `PublishAsync` at depth 9 is refused. (5b) The provider-contract row passes
  for `InMemoryAlvoData` and both engines. (5c) Apply refuses an after-hook `condition` and an `email.to`
  template reading an excluded field, each with its pointer; excluding `id`/`tenant_id` is refused. (6) Adversarial:
  `new.tenant_id != @tenant.id` never fires across tenants (integration). (7) #325 facts for
  `afterCreate`/`afterDelete`. (8) Admin e2e: exclude toggle persists.

### HX-02 — `MMLib.Alvo.TestHost` and the docs-snippet harness
- **Scope:** §4.15 without `DeliverAsync`; `test/MMLib.Alvo.TestHost.Tests`;
  `test/MMLib.Alvo.DocsSnippets.Tests`; `package-boundary.md` lists the package; X11.
- **Docs:** *Testing your backend*. **Dashboard:** none (no runtime surface).
- **Acceptance:** (1) a descriptor `reject` refuses through `alvo.Data`. (2) `DrainEventsAsync` delivers a
  declarative webhook after-hook to a stub. (3) A loop — a webhook after-hook whose stub receiver writes
  the same entity back through `alvo.Data` — throws `AlvoDrainNotQuiescentException`. (4) The suite passes with `UsePostgreSql`. (5) Public API baseline for
  the package. (6) A non-compiling snippet fails ring0.

### HX-03 — Event handlers on the delivery engine (untyped core)
- **Scope:** `Entity("name")`, `Entities`, `Entity("*")`, `OnCreated/OnUpdated/OnDeleted/OnSaved` over
  `AlvoRecord`, `AddEventHandler`, delegate DI binding, `AlvoRunAs`, `AddRetiredExtension`, CEL-string `when`;
  `ExtensionBindingCompiler`; `alvo_deliveries`, `alvo_delivery_attempts`; `IDeliveryStore` + contract suite;
  fan-out first; `DeliveryDispatcher` (pools, leases, X5, dead-letter, lanes, `subscriber IN`);
  `IAlvoSession`; `alvo` reserved namespace (E12); metrics and spans; `extensibility.md` rule 4 amended (X14).
- **Docs:** *React to changes (event handlers)*. **Dashboard:** none in this PR (HX-04 renders it).
- **Acceptance:** (1) contract suite, both engines: claim, lane order, re-fan-out no-op, `not_before`, dead
  at attempt 7, lease-expiry reclaim. (2) Kill mid-delivery: the handler runs again after restart with the
  same `EventId`. (3) A 10 000-event chaos run loses nothing. (4) An unmatched `when` creates no delivery
  row. (5) Event 1's fan-out fails once; event 2 of the same record is not delivered before it. (6) A
  permanently failing webhook action does not keep the handler from its delivery. (7) A `when` typo refuses
  boot naming the id; all problems reported at once. (8) A handler writing the entity it subscribes to —
  once via `ctx.Data`, once via a DI-injected `IAlvoData` with a fresh `AlvoContext.System(tenant)` — is
  dead-lettered `chain-depth-exceeded` when its write reaches depth 9, both engines. (9) Unknown id with pending deliveries
  refuses boot; `AddRetiredExtension(DeadLetter)` dead-letters, `Discard` cancels. (10) Equal `Order` logs
  `ExtensionOrderTie`. (11) `PublishAsync("alvo.schedule.x")` is refused. (12) A `System` handler cannot read
  another tenant's row. (13) The delivery span links to the envelope's `traceparent`. (14) A `"*"`
  registration with a field-referencing `when` refuses boot. (15) A colliding derived event name (`a-b` vs
  `a_b`) refuses boot. (16) A handler on custom event `X` that publishes `X` stops: fan-out records a `dead`
  delivery `chain-depth-exceeded` at envelope depth 8 (no write involved). (17) A `when` that throws on event 1
  of key A yields a `dead` `when-failed` delivery and does not delay event 2 of key B. (18) A reclaimed lease's
  late finisher changes nothing (fencing). (19) An `Ordering.None` handler is not blocked by a dead earlier
  delivery.

### HX-04 — Extension catalog, apply guard, dashboard and assistant
- **Scope:** §4.13 models, `GetExtensionsAsync`, route, capability report, `ManagementPolicyVerdict.Extensions`;
  apply/rollback guard (removal and `events.exclude`); dashboard badge, *Lifecycle* tab (post-commit lane),
  apply-preview refusal; assistant `get_extensions`, skill region, two eval cases.
- **Docs:** *See your extensions in the dashboard*.
- **Acceptance:** (1) catalog snapshot for untyped and `"*"` registrations (Verify). (2) Apply removing a
  bound field, and apply excluding a bound field, are refused naming the extension (Management API
  integration). (3) Admin e2e: badge count and read-only lane. (4) Ai eval graders for the two cases
  unit-tested; `get_extensions` contract test. (5) `BoundaryArchitectureTests` green.

### HX-05 — Typed layer and lambda translation
- **Scope:** `Entity<T>`, attributes, `IAlvoEntity`, `As<T>()`, typed messages, the §4.3 translator, typed
  reads, boot mapping validation incl. `events.exclude` (E7).
- **Docs:** *Typed or CEL*. **Dashboard:** catalog shows translated `when`.
- **Acceptance:** (1) the §4.3 table as a data-driven unit suite. (2) CsCheck: random captured strings never
  appear in emitted SQL text, both engines. (3) CsCheck: `parse(render(s)) == s`. (4) Every refused construct
  throws with id and fix; `UtcNow` in `when` refused until HX-11. (5) A typed record with a missing/mistyped
  field, or reading an excluded field, refuses boot. (6) Typed and untyped registrations of one handler
  produce the same catalog `When`. (7) `As<T>()` with a missing field throws naming it.

### HX-06 — Delivery operations
- **Scope:** `IAlvoOperations` complete, routes and levels, `Idempotency-Key`, `alvo_subscriber_controls`,
  `alvo_operations_log` + `ManagementAudit` (E11), fixtures, `DeliverAsync`, stats (`MeterListener`).
- **Docs:** *Operate deliveries*. **Dashboard:** Operations page.
- **Acceptance:** (1) replay dry run returns the exact count and changes nothing, both engines. (2) A paused
  subscriber accumulates and drains in order after resume. (3) Redeliver of a dead delivery unblocks its
  lane. (4) Viewer gets 403 on redeliver. (5) A fixture contains no non-key scalar. (6) `DeliverAsync`
  reproduces a failure locally. (7) Pause, redeliver and replay each write one `alvo_operations_log` row and
  one `ManagementAudit` event. (8) Stats states `scope: "this-instance"`. (9) `PUT …/extensions/{id}/enabled`
  on a handler → 409. (10) Admin e2e: pause → write → resume → delivered.

### HX-07 — Write unit, `TransactAsync`, savepoints
- **Scope:** §4.5; `IAlvoTransactions`, `IAlvoTransaction`, typed writes, transactional `PublishAsync`
  (X13), savepoints, poisoning, nested depth via `AlvoFlow`.
- **Docs:** *Transactions in your own code*. **Dashboard:** none (no new surface).
- **Acceptance:** (1) two writes + a custom event commit together or not at all, both engines. (2) A
  delegate that catches a unique violation on its first insert and inserts a different key **commits on both
  engines**. (3) A delegate that catches a poisoning failure still rolls back. (4) A caller without `create`
  rights is refused inside as outside (adversarial). (5) An idempotency key inside → `ArgumentException`.
  (6) A deterministic violation runs the delegate once (no retry). (7) Existing `AlvoData*Tests` unchanged
  and green. (8) Two nested writes in one unit resolve one DI scope, distinct from the request scope. (9) **R-B1:** a
  delegate calling a DI-injected `IAlvoData.CreateAsync` (and `IAlvoEvents.PublishAsync`) is refused with
  `AlvoUnitInProgressException` immediately on both engines (no hang, no committed side write). (10) A unit
  exceeding `AlvoTransactionOptions.Timeout` rolls back. (11) Contract rows for the three literal string
  operators: a captured `%`/`_`/`\` matches literally on both engines.

### HX-08 — Commands, inbound webhooks, HTTP conventions
- **Scope:** §4.9 entirely.
- **Docs:** *Commands and inbound webhooks*, *HTTP conventions*. **Dashboard:** *Commands* lane; simulator
  evaluates a command's access rule. **Assistant:** commands in `get_extensions`.
- **Acceptance:** (1) access denied → 403 before the handler; `MapAlvoCommand<TEntity>` on an unreadable
  row → 404. (2) Completion event reaches an `AddEventHandler` handler. (3) A replayed `Idempotency-Key`
  returns the first result without re-running the handler. (4) A reference Standard Webhooks signature
  verifies; a 6-minute-old timestamp and a repeated `(pattern, webhook-id)` are refused; rows older than
  7 days are swept. (5) `ForEntity("bookings")` rate-limits only bookings routes. (6) A convention removing
  Alvo's filter fails the route-table build (#184). (7) OpenAPI lists the command (Vacuum green). (8) An
  internal command invoked from a handler runs its access rule and emits its event. (9) No credential on a
  `WithAlvoCaller()` endpoint binds `Anonymous`; a bad key → 401. (10) A command handler calling a DI-injected
  `IAlvoData` inside `ctx.TransactAsync` is refused (R-B1); a stub external call made before
  `ctx.TransactAsync` runs with no transaction open; a second `ctx.TransactAsync` throws; the completion event
  and idempotency record commit in the handler's unit.

### HX-09 — Write hooks in three phases
- **Scope:** §4.1 stages 0/2/4/5/7/7b, §4.4 (except the analyzer), `IWriteExtensionRunner`,
  `alvo_hook_rejections`, `AsSystem`, `SuppressHostHooks`, `write.Publish`, validate disable (HX-06's route
  now accepts validate kinds), `Hooks.AssertRan` for write hooks; `events.md:1164` corrected (C13).
- **Docs:** *Validate and guard writes*; *Where logic goes* gains write rows; the end-to-end booking recipe
  (descriptor + hooks + handler + test), whose `WithinOpeningHours` reads `opening_hours` via `AsSystem()`.
  **Dashboard:** write lanes, editor note, simulator/dry-run list, p95 and 24 h rejections.
- **Acceptance:** (1) per stage, both engines: reject → no row, no outbox row, correct status and pointer.
  (2) A `Set` writing a forbidden `owner_id` → 403 (adversarial); a `Set` of an undeclared enum value → 500
  `invalid-set` identically on both engines. (3) A rejection on a hidden field carries no pointer.
  (4) After-write sees id/defaults/rollups and can roll back; an after-write inserting a child emits an event
  whose parent rollup equals the committed value. (5) Self-write refused. (6) A 250 ms overrun and a 51st
  operation → 500 with reason and rollback, even when the hook catches the exception. (7) A hook injecting
  `HttpClient` refuses boot. (8) A batch with a validate hook calls it with no transaction open (stub asserts
  the connection state); a validate hook never runs on an idempotent replay; a nested write inside
  `TransactAsync` does not invoke validate. (9) A before-write hook using `AsSystem()` to write the same entity
  recursively is refused 508. (10) `SuppressHostHooks` with a non-system context → `ArgumentException`; with
  system, hooks skipped and one audit row written. (11) Disabling a validate hook writes an audit row and the
  next write skips it. (12) A rejection log row exists after rollback. (13) Two hooks of one unit share the
  unit's scope; a request-scoped service differs.

### HX-10 — Analyzer, host-function budget, `expression-failed`, calibration
- **Scope:** `MMLib.Alvo.Analyzers` packed in the `MMLib.Alvo` nupkg (E6), `ALVO1001`; token-aware
  `AddCelFunction` overload under the per-evaluation budget (#309); E1 (#315; `data-api.md`, `cel.md`);
  `scripts/test-load` hook scenario and `docs/performance.md` section (Q5).
- **Docs:** *Analyzer rules*. **Dashboard:** none (build-time and error-shape changes).
- **Acceptance:** (1) `ALVO1001` on `HttpClient` use in a before-write hook and in an `AddCelFunction`
  delegate; none in a handler. (2) A packed consumer on the minimum SDK builds and reports it. (3) A
  token-aware host CEL function over budget → `function-failed` and rollback, both engines. (4) `int('x')` on
  caller data → 422 `expression-failed`, both engines. (5) `docs/performance.md` has the calibrated overhead.

### HX-11 — `CHECK`, `rollup.where`, `now()` and durations
- **Scope:** `field.validation` → `CHECK` with a pre-apply violation scan; `rollup.where`; `now()` in
  `Condition` (write instant / event time) and `Rule` (request instant, bound); CEL `duration(string)`,
  `timestamp ± duration`, `timestamp - timestamp`; `AlvoConstraintKind.Check` → 422 `validation` with pointer
  (Ruling X); `UtcNow` → `now()` in `when`; refusals on dynamic entities.
- **Docs:** *Integrity in the descriptor* (1). **Dashboard:** field *Validation* and rollup *Where* live.
  **Skills:** computed-and-rollups, rules-and-cel regions.
- **Acceptance:** (1) concurrent oversell refused 422 with pointer, both engines. (2) Apply over violating
  rows refused with the count. (3) A filtered rollup stays correct under concurrent child writes. (4) One
  `now()` instant per request. (5) CsCheck: a literal never escapes a `CHECK`. (6) Duration arithmetic agrees
  between interpreter and SQL renderer on both engines. (7) `CHECK` on a dynamic entity refused naming #41.

### HX-12 — `sequence`, exclusion and partial indexes
- **Scope:** `sequence: { per, period, start, format }` over `alvo_sequences`; `indexes[].exclude` via the
  DIM; `indexes[].where`; `AlvoConstraintKind.Exclusion` → 409.
- **Docs:** *Integrity in the descriptor* (2). **Dashboard:** *Sequence*, *Exclude overlaps*, *Only where*
  editors. **Skills:** indexes, field-types.
- **Acceptance:** (1) 100 concurrent creates → 1..100, no gap, no duplicate, both engines. (2) A rolled-back
  create consumes no number. (3) Overlapping bookings → 409 with pointer under concurrent writers, both
  engines. (4) A partial unique index allows duplicates outside `where`. (5) The T-SQL double renders the
  shape. (6) No `btree_gist` → apply refusal with fix.

### HX-13 — Timers, schedules, data migrations
- **Scope:** §4.10 without steps; `ITimerStore` + contract suite; Cronos (E5); `IAlvoTenantCatalog`;
  `alvo_timers`, `alvo_schedules`, `alvo_data_migrations`; repoint the #42 comments (C4).
- **Docs:** *Time: timers and schedules*. **Dashboard:** Operations → Schedules (next/last run, run now),
  timer counts.
- **Acceptance:** (1) a same-key reschedule fires once at the new time, including when it races a claim.
  (2) Cancel during a claim prevents firing. (3) A timer survives restart. (4) Three dispatchers fire one
  cron occurrence exactly once (PostgreSQL). (5) `Europe/Bratislava` DST occurrences correct (unit).
  (6) `EachTenant` without a catalog refuses boot; with one, one run per tenant, none reading another tenant.
  (7) A data migration runs once per revision across restarts and not again after rollback and re-apply.
  (8) Fire + outbox append commit together (contract suite). (9) A timer scheduled in a handler carries depth
  and causation. (10) Fake clock + drain delivers a 24 h reminder.
  (11) A self-re-arming timer stops at depth 8 (`ScheduleAsync` refused above it). (12) `ScheduleAsync` inside
  a rolled-back `TransactAsync` leaves no timer row. (13) `alvo.schedule.*` events are minted only through
  the internal framework path.

### HX-14 — Steps and flow control
- **Scope:** `IAlvoSteps`, `alvo_steps`, `alvo_concurrency_slots`, `Debounce` (`AlvoOrdering.None` ships in HX-03).
- **Docs:** *Multi-step side effects*. **Dashboard:** delivery detail lists steps.
- **Acceptance:** (1) a failure at step 3 does not re-run steps 1–2, both engines. (2) `SleepUntilAsync`
  resumes after the fake clock passes. (3) `PerKey("tenant", 2)` never runs a third concurrent delivery with
  two dispatchers on PostgreSQL. (4) 10 rapid updates with 5 s debounce deliver once, latest.

### HX-15 — Functions, automation runtime, secrets
- **Scope:** §4.11; X12 schema widening; `AddRequiredSecret` + apply check of `secretRef`/`headersSecretRef`;
  automation on deliveries; #328 null-entry rule.
- **Docs:** *Functions in the descriptor*, *Automation*. **Dashboard:** hooks editor offers functions with an
  input form; automation page leaves *Not yet*. **Assistant:** functions in the catalog; skill region.
- **Acceptance:** (1) an `input` typo refused at apply with a fix; an unregistered name gets a suggestion; a
  name in both host and `functions` refused. (2) One failing webhook action retries alone while the email of
  the same event is not resent. (3) `entity.update` fires once per event under redelivery, both engines.
  (4) A missing required secret refuses boot. (5) `batch` delivery, `http.call` and wildcard patterns refused
  by name. (6) KilledHostRecovery suite green. (7) `UnhonouredSubsystems` no longer warns `automation`.

### HX-16 — Enricher, narrowing, virtual fields
- **Scope:** §4.12; `IAlvoData` docs for the key-set contract.
- **Docs:** *Shape what reads return*. **Dashboard:** *Reads* lane; simulator lists narrowings.
- **Acceptance:** (1) narrowing ANDs into SQL (statement snapshot) and changes `count`. (2) Narrowing cannot
  return a row the policy hides (two-user). (3) Handler `System` reads are not narrowed. (4) A hidden field is
  absent from Data API, `IAlvoData` and write echo. (5) A virtual field is read-only in OpenAPI, refused in
  writes (422) and in `filter`/`sort`/`select`. (6) An enricher depending on `IAlvoData` refuses boot.

### HX-17 — Generated typed entities
- **Scope:** incremental generator in `MMLib.Alvo.Analyzers` reading `AdditionalFiles` with
  `AlvoDescriptor="true"`: `sealed partial class` wrappers (X18), enums, role constants, `Patch`; boot check
  for fields missing from the live schema; generation from a Management API snapshot (dashboard-first);
  DocsGen adds all new public types to the C# reference (`MMLib.Alvo.TestHost` itself is listed from HX-02).
- **Docs:** *Generated types*. **Dashboard:** none (build-time).
- **Acceptance:** (1) Verify snapshots over `examples/*`. (2) A wrapper round-trips unknown fields. (3) A
  removed field is reported at boot. (4) `OnSaved` on a generated type: `e.Old is null` on create compiles and
  is true. (5) Incremental cache test. (6) The docs site's C# reference lists the new packages and types.

### HX-18 — `stateMachine` and declarative timers
- **Scope:** `entities.<e>.stateMachine` enforced in-transaction before host hooks; `entities.<e>.timers`
  (`at` uses HX-11 durations, `TimerAt` typing = timestamp-valued `Condition` expression) armed through the
  unit's `ITimerStore` **in the write's transaction**.
- **Docs:** *State machines and timers in the descriptor*. **Dashboard:** transition graph; simulator answers
  "may role X take transition Y". **Skills:** hooks/traits regions.
- **Acceptance:** (1) undeclared transition → 422, both engines. (2) Transition `when`/`roles` honoured.
  (3) A reminder re-arms on `starts_at` change, cancels on status change, and a rolled-back write leaves no
  timer. (4) Graph renders (admin e2e).
- **Why late:** builds on timers (HX-13) and durations (HX-11); pure descriptor work that blocks no host code.

### HX-19 — Relation rules and lookup `checks`
- **Scope:** Rule-profile navigation over `ref` fields and reverse relations (`via` resolution as rollups) →
  correlated `EXISTS` with the related `USING`; apply-time rule-cycle detection; `entities.<e>.checks: [{
  lookup, expr, message }]` → 422.
- **Docs:** *Rules over relations*. **Dashboard:** rules editor and simulator understand relation terms.
- **Acceptance:** (1) two-user and two-tenant adversarial suites (list, get, count, write), both engines.
  (2) An `EXISTS` never matches a related row the caller cannot list. (3) CsCheck injection property. (4) A
  rule cycle (A's `list` uses B, B's uses A) refused at apply. (5) A lookup check refuses with message and
  pointer.
- **Why last:** the largest change to the predicate compiler and the riskiest security-core PR; lands on a
  stable catalog, simulator and read pipeline.

---

## 8. Issue mapping

### 8.1 Existing issues

| Issue | Relation |
|---|---|
| #22 (closed) | C# half delivered by HX-03/07/09; new child N-1 instead of reopening |
| #153, #325 | closed by HX-01 |
| #309, #315 | closed by HX-10 |
| #184 | closed by HX-08 |
| #328 | closed by HX-15 |
| #33 | advanced by HX-06 (DLQ, redelivery UI), HX-08 (inbound webhooks), HX-15 (automation runtime) |
| #35 | advanced by HX-15 (in-process host functions) |
| #120 | advanced by HX-15 (webhooks on deliveries); signing stays |
| #34 | comment with the Q6 ruling |
| #150 | comment: handlers inherit its condition (§4.8) |
| #154 | comment: retention must cover `alvo_deliveries`, `alvo_delivery_attempts`, `alvo_hook_rejections`, `alvo_inbound_webhooks`, `alvo_operations_log` |
| #41 | comment: dynamic entities get the untyped core; DDL-backed integrity refused until #41 |

### 8.2 New issues

| Id | Title | One-line body |
|---|---|---|
| N-0 | Epic: Host extensibility (HX-01 … HX-19) | Umbrella for this spec; sub-issues below. |
| N-1 | C# face of lifecycle hooks — the unfinished half of #22 | Delivered by HX-03, HX-07, HX-09. |
| N-2 | Pattern subscriptions for host handlers and descriptor rules | Designed namespace + `*` matcher scoped by `tenantid` with a cross-tenant adversarial suite. |
| N-3 | Batch write hooks | `Batch` info on contexts or `IBatchWriteHook`; per-row hooks ship first. |
| N-4 | Durable workflow adapter (DurableTask / Temporal / Dapr Workflow) | Long human waits go to an adapter package. |
| N-5 | `stateMachine` transition commands | Expose declared transitions as named commands. |
| N-6 | System access across tenants for background jobs | `IAlvoTenantCatalog` + `EachTenant` (HX-13); replaces the code's #42 references. |
| N-7 | `like` filter patterns differ between SQLite and PostgreSQL | `FilterSqlRenderer` emits `LIKE` without `ESCAPE`; PostgreSQL escapes with `\`, SQLite has no default escape. |
| HX-01…HX-19 | one issue per PR, titled as in §7 | body = scope + acceptance from §7; sub-issues of N-0. |

---

## Appendix A — Review dispositions

Review: `scratchpad/hooks-impl/spec-review.md` (2026-10-14). Every finding was checked against the code
before its disposition.

### Blockers

| Id | Disposition | Where fixed |
|---|---|---|
| B1 | **accepted** — confirmed `OneBatchAsync:2433`; in-transaction replay lookups `:517/:1120/:1290/:1343/:1713/:2439` | §4.1 stage 0/2 rules; C15, C16; HX-09 (8) |
| B2 | **accepted** — `AlvoContext.System` builds provenance-free contexts | §4.6 ambient `AlvoFlow` + `Effective`; §4.4.6; HX-01 (5), HX-03 (8), HX-09 (9), HX-13 (9) |
| B3 | **accepted** — `ReplayableWriteAsync:436-491` is the idempotency race loop | C14; §4.5 savepoints, poisoning, no retry; HX-07 (2), (3), (6) |

### Majors

| Id | Disposition | Where fixed |
|---|---|---|
| M1 | accepted — `OutboxDispatcher.DeliverAsync:214-225` runs actions, then marks | §4.8 fan-out first, ordering condition, `subscriber IN`; HX-03 (5), (6) |
| M2 | accepted | §4.10 slot rows; HX-14 (3) |
| M3 | accepted | §4.10 generation; HX-13 (1), (2); HX-18 (3) |
| M4 | accepted — `IOutboxStore.AppendAsync:126` is autocommit | §4.10 `ITimerStore.FireAsync`/`AdvanceScheduleAsync`; HX-13 (8) |
| M5 | accepted — `AlvoEventName.CustomName` forbids `-` | E3 mapping, E12; HX-03 (11), (15) |
| M6 | accepted | §4.1 stage 5, §4.4.2; HX-09 (2), (3) |
| M7 | accepted — emit uses `Unmasked(stored)` (`:255-257`) | §4.1 stage 7b; HX-09 (4) |
| M8 | accepted (new port) | `IAlvoTransactions` (§5); `IAlvoData` untouched; precondition + batch verbs on `IAlvoTransaction`; command idempotency §4.9 |
| M9 | **partial** — endpoint metadata reuses `DataOperation` + public `AlvoEndpointKind`, internal marker kept as the transformer's source; hooks keep a separate `AlvoWriteOperation` because `DataOperation` is the policy verb without `Replace` (X19) | §4.9, X19 |
| M10 | accepted — `ManagementPolicyVerdict` is positional (`ManagementModels.cs:160`) | §4.13 required-init records; §5 |
| M11 | accepted | X18 class wrappers; HX-17 (4) |
| M12 | accepted | `IAlvoSession` (§5); `AlvoJobContext`; `AlvoCaller` now names only the ASP.NET binder shim |
| M13 | accepted — `CelContextValue` has three members (`CelNode.cs:85-95`) | §5 complete; X7 removes the AST/profile additions; dialect member is a DIM |
| M14 | accepted | X14; `Add*` names; rule 4 amended in HX-03 |
| M15 | accepted — `CallerResolution.ResolveAsync` returns `ValueTask` (`:65`) | §4.9 `WithAlvoCaller()` + `AlvoCaller`; HX-08 (9) |
| M16 | accepted (its first option) | X7; §4.3 queries → `AlvoFilter` |
| M17 | accepted | §4.3 constant-only captures, `UtcNow` → `now()`; HX-05 (3), (4) |
| M18 | accepted — `AlvoConstraintKind` has `Unique`/`Referenced` only | §5, §6.1; HX-11 (1), HX-12 (3) |
| M19 | accepted — `duration` is only a reserved name (`HostCelFunction.cs:77`) | HX-11 scope and (6); HX-18 `TimerAt` typing |
| M20 | accepted | HX-03/HX-04 and HX-09/HX-10 splits |
| M21 | accepted | §7 linear table with branches and bases |
| M22 | accepted | internal commands §4.9, HX-08 (8); `Entity("*")` §4.2, HX-03 (14); DocsGen reference HX-17 (6); booking recipe HX-09 docs; MCP §1.2 |
| M23 | accepted | E11; HX-06 (7), HX-09 (10), (11) |
| M24 | accepted | owners: depth HX-03 (8); E3 HX-03 (9); validate disable HX-09 (11); `Order` HX-03 (10); stats HX-06 (8); span links HX-03 (13); DI scope HX-07 (8)/HX-09 (13); webhook sweep HX-08 (4) |
| M25 | accepted (pre-release break) | §4.7, §5; "v2" naming dropped |
| M26 | accepted | E7; §4.2; HX-04 (2), HX-05 (5) |
| M27 | accepted | §4.8 worker model, leases, indexes |

### Minors

| Ids | Disposition | Where |
|---|---|---|
| 1, 2 | accepted | C13; HX-09 corrects `events.md:1164` |
| 3 | accepted | C4; HX-13 repoints comments |
| 4 | accepted | C2 |
| 5 | **partial** — header shows "v2"; the published revision approved is the fourth; both cited | status block |
| 6, 7 | accepted | §3.1 heading; the first draft's X14 (validate-only disable) removed — it is the page's own rule |
| 8 | accepted | §4.13 "sixth `get_*` read" (verified `ManagementTools.cs`: five `get_*` + two dry runs) |
| 9, 10, 11 | accepted | E3; §4.9 `CelProfile.Access`, `MapAlvoCommand<TEntity>`, receiver tenant + per-pattern dedup |
| 12, 13 | accepted | §4.8 fixture redaction; §4.4.4 poisoning |
| 14, 15 | accepted | HX-01 has no C# snippet; `DeliverAsync` in HX-06; `alvo_steps` in HX-14 |
| 16, 17, 18, 19, 20 | accepted | §4.11 refusals; HX-19 cycle detection; §1.2/§6.1 dynamic entities; X17; §4.12 |
| 21, 22, 23, 24, 25, 26 | accepted | HX-01 PLAN line; E6; §4.15 harness (classes, no Roslyn); §4.11; §4.10 migrations; §4.5 scope |
| 27 | **partial** — `RunAs` → `AlvoRunAs` and the `AsSystem()` doc example accepted; **rejected sub-point:** aligning `IAlvoEventHandler.HandleAsync` to `ValueTask` — hooks often complete synchronously on the hot write path (`ValueTask` avoids an allocation), handlers always do I/O and match `IConsumer<T>`'s `Task` | §5; HX-09 docs |

**Counts:** accepted 3 blockers, 26 majors, 25 minors; partial 1 major (M9) and 2 minors (5, 27); rejected 0
whole findings (one sub-point of minor 27).

### Second round — scoped re-review (`scratchpad/hooks-impl/spec-rereview.md`)

| Id | Disposition | Where fixed |
|---|---|---|
| R-B1 | **accepted — refuse, not join** (joining would let a call's own, often `System`, context write inside the caller's transaction and make an autocommit publish silently transactional) | §4.5 unit on the flow + `AlvoUnitInProgressException`; §4.1 "top-level" = no unit on the flow; `IAlvoScheduler` the one joining exception (§4.10); HX-07 (9), HX-08 (10) |
| R-M1 | accepted — `InsertAsync` is `Add` + `SaveChangesAsync` (`EfAlvoData.cs:906-913`) | §4.5 per-write `DbContext` enlisted via `UseTransactionAsync`; HX-07 (2) |
| R-M2 | accepted | §4.6 "who adds +1", refuse when depth > `MaxDepth`; HX-01 (4), (5); HX-03 (8) |
| R-M3 | accepted — `IAlvoData` documents six families (`IAlvoData.cs:39`) | `AlvoChainDepthExceededException` (seventh family), enforcement sites §4.6; §5; HX-01 (4), (5a), (5b) |
| R-M4 | accepted — `EntitySchema` is a public record | §4.7 *HX-01 settled details*; `EntitySchema.EventImageExclusions` in §5; HX-01 (5c) |
| R-M5 | accepted | §4.6 fan-out / `PublishAsync` / `ScheduleAsync` refusals; HX-03 (16), HX-13 (11) |
| R-M6 | accepted | §4.8 `when-failed` outcome, per-key stop; HX-03 (17) |
| R-M7 | accepted (option: `ctx.TransactAsync`, at most once) | §4.9; HX-08 (10) |
| R-M8 | accepted — `FilterSqlRenderer.cs:126` emits `LIKE` without `ESCAPE`; chose appended literal operators over changing `Like` semantics on SQLite | §4.3, X7 amended, §5 (HX-07 row), HX-07 (11); `Like` itself filed as N-7 |
| R-M9 | accepted — `AlvoViolation` is core (`src/MMLib.Alvo/Api/AlvoViolation.cs`) | §4.4.2, §5: exception in core |
| R-1, R-2 | accepted | §4.1 stage 2 once outside the replay loop; stage 0 runs `EnsureSameRequest` |
| R-3, R-4, R-5 | accepted | §4.7 *HX-01 settled details* (derived `ActorKind`, normalising reader, #153 texts → N-2) |
| R-6 | accepted | §4.8 fenced completion; HX-03 (18) |
| R-7, R-8 | accepted | §4.10 unit-enlisting `ITimerStore.UpsertAsync`; internal `AlvoFrameworkEvent.Create`; HX-13 (12), (13) |
| R-9 | accepted | §4.6 wording + arch fact (HX-01) |
| R-10 | accepted | `AlvoExtensionOptions` (HX-01), `AlvoDeliveryOptions` (HX-03) in §5 |
| R-11 | accepted | `AlvoOrdering.None` implemented in HX-03 (19); `IAlvoScheduler` removed from the ban list (joins the unit) |
| R-12 | accepted | HX-02 (3) uses a stub receiver that writes back |
| R-13 | accepted | `AlvoTransactionOptions.Timeout` (30 s); HX-07 (10) |

**Second-round counts:** accepted 1 blocker, 9 majors, 13 minors; rejected 0.
