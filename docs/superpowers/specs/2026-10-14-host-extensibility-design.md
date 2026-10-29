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
| X15 | Analysis §2.7: a function over its timeout "is killed" | cancelled via its token, attempt marked failed; a handler with `MaxConcurrency` abandoned invocations still alive is not claimed for until one ends (HX-03 settled details (45)) | .NET has no thread abort; out-of-process runtimes are #35 |
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

**HX-05 settled details.** Rulings and deviations taken while planning (1–25, verbatim from
`docs/superpowers/plans/2026-10-24-hx-05-typed-layer.md`; 20–25 from its review round), so a later reader can tell a
decision from an oversight. Where a ruling and §4.3's table disagree, the ruling is what shipped.

1. **Canonical CEL.** `CelText` writes one space around every binary operator, `, ` between call arguments, and parentheses only around a looser child or a right operand of equal precedence. A typed registration and an untyped one therefore show the same `When` exactly when the CEL string is written canonically (acceptance (6)); a CEL string spaced differently is shown as the host wrote it — the catalog never rewrites host text.
2. **`== null` and `HasValue` become `has()`.** Alvo's CEL refuses `== null`/`!= null` (every comparison already treats a missing value as false), so `x == null` translates to `!has(new.x)` and `x != null`/`x.HasValue` to `has(new.x)` — a deviation from §4.3's row, same meaning. The query side keeps §4.3's `is null` / `not is null`.
3. **Lists become `||` chains.** Alvo's CEL has no list literal, so `list.Contains(x)` in a `when` translates to `new.x == 'a' || new.x == 'b'` (parenthesised when nested), the spelling the parser's own fix recommends; an empty constant list is refused. `x.In(a, b)` is not shipped: §5 approves no type to carry it, and an array's `Contains` says the same. The query side is §4.3's `in` (≤ `AlvoFilter.MaxInCandidates`; an empty list matches nothing).
4. **Typed constants without a CEL literal.** A `Guid` constant compares as `string(new.x) == '<guid D>'` (Alvo's CEL cannot compare a Uuid with a String and has no uuid literal; `string()` renders a Uuid in the same `D` form); a `DateTimeOffset` constant as `timestamp('<UTC, RFC 3339, up to seven fraction digits>Z')`; a `DateOnly`, `DateTime`, `double`, `float`, `char` and `long.MinValue` constant is refused in a `when` with a fix (no exact literal). The query side places every value as a typed value for the driver to bind.
5. **`e.Caller` is `AlvoConditionCaller`** (`User`, `Tenant`, `HasRole(string)`), a public type §5 does not list: `AlvoContext.HasRole(Role)` cannot serve, because a host cannot construct an application `Role`. Translation-only members throw `NotSupportedException` if called.
6. **Text tests are ordinal.** `StartsWith`/`EndsWith` translate in their one-argument form and with `StringComparison.Ordinal`, `Contains(string)` as is — CEL compares characters exactly; any other `StringComparison` is refused. (The one-argument .NET overloads are culture-sensitive; the condition is judged by CEL, and the docs say so.)
7. **Query text operators wait for HX-07.** §4.3's table maps `StartsWith/EndsWith/Contains(string)` to new `AlvoFilterOperator` members, but §5 assigns those members (and their driver obligation and contract rows) to HX-07; HX-05 refuses them in a typed query with a fix and never falls back to `Like` (N-7). §5 decides because it is the approval list. — **Superseded by HX-07:** the three literal operators shipped (§4.5 *HX-07 settled details* (16)–(18)).
8. **An untranslatable `when` throws at registration** (Q3) as `AlvoTranslationException`, first problem only — the lambda is wrong whatever the descriptor says, and the registration call is where the host sees it. Mapping problems are boot problems, reported all at once (§4.2) at `extensions/<id>/record`: catalog-free ones (no constructor, a value type no field holds, a field mapped twice, a `[Flags]` enum) even at an `Awaiting` boot (HX-03 (39)).
9. **No per-shape translation cache** (Q3's "translations cached per expression shape" deferred): a `when` is translated once anyway; a typed query is walked per call and its captured values evaluated per call — a constant or closure field read by reflection, anything else through `Compile(preferInterpretation: true)` — which also rules out a cache serving a stale captured value. A cache waits for a measurement (HX-10's calibration).
10. **Typed `QueryAsync<T>` takes a required `limit` and returns `IReadOnlyList<T>`** — one bounded page, no cursor; paging stays the untyped `AlvoQuery`. A typed page would need an `AlvoPage<T>` §5 does not approve.
11. **Mapping rules.** Field: `[AlvoField]`, else `JsonNamingPolicy.SnakeCaseLower`; enum value: `[AlvoEnumValue]`, else the same policy (prior art: `System.Text.Json`'s naming policy). Mapped: public, readable, not an indexer, with a public setter or `[AlvoField]`. Compatible CLR types per field type as the docs table lists (`integer` takes `long` or `int`; `enum` takes `string` or an enum whose every member names a declared value); nullability is read with `NullabilityInfoContext`, and a non-nullable property on an optional field refuses boot — D2's "no `!`" applied to the mapping.
12. **The unit-test seam is two public members** — `AlvoEventContext.Create(...)` and `AlvoTestHost.EventContext(caller, …)` — closing HX-03 (53). Not on §5's list. Both ship because they do different jobs: `Create` is the factory the core must expose (the constructor is internal, and `InternalsVisibleTo` into the published TestHost is refused by the repository's own rule — forgeable); `EventContext` is the helper a test actually calls, because building an `AlvoEvent` by hand means filling nine required members and fetching an `IAlvoSessions` from the container. `Create` carries only the session it is given, so it grants nothing an `IAlvoSessions` caller lacks.
13. **`IAlvoEntity` is `AlvoRecord Record { get; }` plus a public `(AlvoRecord)` constructor**: `As<T>()` wraps instead of copying, so unknown fields survive (the protocol HX-17's generated classes implement), and `x.Record["field"]` translates to the field in both translators — the reachable form of §4.3's `record["field"]` row, since the untyped builder keeps CEL strings (D1). A wrapper's `[AlvoField]` properties are checked like any mapping, and `As<T>()` refuses a record lacking one.
14. **`As<T>()` throws `InvalidOperationException`** naming the field — missing (distinct from null), null for a property that cannot hold null, or a value the property cannot take — and never echoes the value.
15. **Typed application events.** `AddEventHandler<TMessage, THandler>` reads the event type from `[AlvoEvent]`; its `when` stays a CEL string (an application event's condition tests only `@user`/`@tenant`, HX-03 (9)); `TMessage` is checked catalog-free only (a payload declares no fields), so a missing payload field fails the delivery: it is recorded dead at once with the fixed reason `record-unreadable` (never retried — the same record fails again), and the log entry `DeliveryRecordUnreadable` names the type and the field, never the value. The same holds for any typed message — an entity image too — that cannot be built.
16. **The apply guard counts mapped fields as reads.** Removing, excluding or retyping a field a typed record maps is refused at its pointer with sentences of its own, each saying "its typed record `<Type>` maps it" — the condition's exclusion sentence ("would decide the handler's condition by its absence") is not reused, because it is wrong for a mapping; a field both read and mapped gets the condition's sentence once.
17. **Constants in a `when`** are literal constants, `const` (folded by the compiler), `static readonly` fields, enum members, conversions and negations of those, and array initializers of those for `Contains`. A captured variable, an instance member, a static property and any call are refused (§4.3); the clock (`DateTime(Offset).Now/UtcNow/Today`, `TimeProvider.GetUtcNow/GetLocalNow`) is refused in both translators — in a `when` until HX-11's `now()`, in a query with "capture it first".
18. **Acceptance (2) is proven through the real pipeline**: `SqlCapture` records every statement of a typed query on SQLite and PostgreSQL (`TypedLayerFacts`, linked into both legs), and 200 hostile captured strings per engine must yield statements byte-identical to a benign baseline. The alphabet is the filter-renderer property's hostile one, not the whole code space — PostgreSQL refuses NUL in a text parameter, which is the engine refusing the value, not text leaking into SQL.
19. **The dashboard slice adds no UI**: the *Lifecycle* lane already renders `When`, which for a typed handler is the translated CEL; the admin e2e world gains a typed handler and pins its condition (the `work_orders` badge moves from 2 to 3).
20. **Typed conditions and queries keep CEL's and SQL's null semantics, not C#'s.** A comparison with an empty value is false — `e.New.OwnerId != Fleet` does not match a record with no owner (`CelFunction.Invoke` answers null for a null argument; the interpreter folds it to false), and a query's `neq` never matches a null column (three-valued logic, `AlvoFilterOperator`'s remarks). The translator does not rewrite `!=` into `== null || !=`: that would make the typed and the CEL spelling of one condition mean different things. *Typed or CEL* says it in plain words, with the explicit spelling.
21. **Field names must be CEL identifiers.** Field names are the one part of the CEL text no literal escape covers, so `CelText` refuses a field or `changed()` name outside the lexer's identifier grammar (a letter or `_` first, then letters, digits and `_`) or equal to `old`, `new`, `true`, `false`, `null`, `in` or `has`; the translator refuses such an `x.Record["…"]` key with `AlvoTranslationException`, and `ShapeProblems` refuses such an `[AlvoField]` name at boot (catalog-free). Without it, `e.New.Record["a == 'x' || true"]` would be written as `new.a == 'x' || true` — a different tree (§6.2, "one literal writer"). The query side already checks names against the schema (`FilterSqlRenderer`).
22. **Registration parses what it wrote.** After `CelText.Write`, the translator runs `CelParser.Parse` on the text and turns a `CelSyntaxException` into `AlvoTranslationException`, so a lambda nested past `CelParser.MaxDepth` (32) fails at registration like an over-long one (Ruling 8), never at boot.
23. **`when: null` is a compile error by design.** The `Expression` and `string?` overloads both accept a null literal (CS0121); `when` is simply left out instead. Renaming one parameter would remove the ambiguity at the cost of the `when:` spelling the spec shows for both; the class remarks and the docs page say so.
24. **`Contains` in a `when` takes only an ordered list** — an array or an `IList` (`List<T>`). A `HashSet<T>` or another unordered collection is refused with a fix: its iteration order (string hashes are randomised per process) would make the condition's text, and with it the catalog's `When`, change between restarts. The query side accepts any `IEnumerable` — its value is bound, never written.
25. **Query values take their field's CLR type.** `FilterTranslator` widens an integral value — and each `in` candidate — to `long`, the CLR type of an `integer` field (`FieldClrType.Of(Integer)`), because `AlvoComparison`'s value must match its field's type; an `int?` property compared with `9` therefore sends `9L`.

Rulings taken during execution (recorded in the plan's ledger), continuing the numbering:

26. **`CelText` brackets a comparison operand on both sides.** The parser refuses chained comparisons, so an operand of
    a comparison that is itself a comparison is parenthesised whichever side it stands on — an extension of (1).
27. **A literal is spelled by its `(Type, Value)` pair**, never by the runtime value alone: a String holds a `string`,
    an Int a `long`, a Decimal a `decimal`, a Bool a `bool`; any other pairing is refused by `CelText`.
28. **The enum value → member direction is not checked at boot.** §4.2 asks that every member names a declared value
    (member → value); a declared value no member names is not refused, and a record holding it fails `As<T>()` — for a
    handler, that delivery attempt — with `InvalidOperationException`. Cost if wrong: a runtime error instead of a boot
    refusal.
29. **`decimal`'s conversion operators are folded** like built-in conversions (they are operator calls in the
    expression tree); any other user-defined operator is refused, because folding it would run host code.
30. **Underlying → enum conversion is not transparent**: a number converted to an enum is refused in a `when` rather
    than read as the member.
31. **An enum constant cast to a number compares as that number**, as C# computes it; an enum *field* converted to a
    number translates only where it meets a member of its own enum.
32. **A comparison through `(object)` is refused** in both translators: C# compares references, which neither CEL nor a
    filter has.
33. **Relational operators on an enum field are refused** (`<`, `<=`, `>`, `>=`), in both translators: CEL holds the
    field as its value text, so an ordering would compare strings, not members.
34. **A lazy `Contains` source in a query is enumerated once per translation and capped at 1,001 items** — one past
    `AlvoFilter.MaxInCandidates`, enough to refuse it without reading the rest; so a typed read translates once per
    call and never reuses a filter.
35. **A `HashSet<T>` with a non-default comparer is refused in a query**, as is any other `ICollection<T>` that may
    carry its own equality; an array, a `List<T>`, a default-comparer `HashSet<T>` and a sequence that is not an
    `ICollection<T>` are accepted.
36. **A typed handler with shape or mapping problems never binds**: it is refused at boot at `extensions/<id>/record`,
    or — at a boot that cannot refuse (a dashboard-first host before its descriptor fits) — stays unbound, its events
    recorded dead `unbound`. The apply guard points at the mapped field a change breaks, including a field made
    optional under a non-nullable property and an enum value a member maps that the change removes.
37. **`AlvoEventId.Create` mints the synthetic event id** of `AlvoTestHost.EventContext`, as `AlvoEvent.Id`'s
    documentation prescribes, so a test sees an id of the real shape.

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
- **Unit timeout.** `AlvoTransactionOptions.Timeout` (default 30 s, top-level units use the same default
  — **not in HX-07:** the top-level clause is deferred to HX-09 (§4.5 *HX-07 settled details* (2), (43));
  `TransactAsync` units carry the timeout)
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

**HX-07 settled details.** Rulings and deviations taken while planning (1–54, verbatim from
`docs/superpowers/plans/2026-10-29-hx-07-transactions.md`; 28–45 from its review round, 46–47 from Task 6's review,
48–49 from Task 7's, 50–51 from Task 8's, 52–53 from Task 9's, 54 from the final security review), so a later reader can tell a decision from an
oversight. Where a ruling and §4.5 or §4.6 disagree, the ruling is what shipped.

1. **A write through `TransactAsync`'s own `IAlvoTransaction` is at the unit's depth, not one deeper.** §4.6 says "a unit's nested write's depth = the unit's depth + 1". Read literally for the delegate's writes, wrapping two writes in a unit would report their events one hop deeper than the same writes made one by one, and a command at HTTP depth 0 would emit `chaindepth` 1 — a chain grows because it was made atomic. The unit's depth is the caller's effective depth (`max(explicit, ambient)`), set on the flow for the delegate, and "nested" means a write made under another write's stage: `EfAlvoTransaction.Deeper()` (internal) is that view, one hop deeper, refused and poisoning past `MaxDepth` — the seam HX-09 hands hooks as `write.Transaction`; a call through it made from inside the running write is admitted by the unit's per-call gate (Ruling 30). Tested now (`A_write_one_hop_deeper_past_the_limit_is_refused_and_a_caught_refusal_still_rolls_back`).
2. **Top-level writes move onto the seam but are not yet flow-visible units.** §4.5 says every write opens its own unit and "top-level units use the same default" timeout. Here every `EfAlvoData` write site goes through one `BeginAsync(db, unit)`, whose top-level branch is today's turn + transaction, byte for byte; it puts nothing on the flow and has no deadline. Reason: acceptance (7) — a 30 s cap on every existing write, and refusing a host CEL function's data call during a top-level write (R-B1 on its flow), change shipped behaviour while nothing async runs inside a top-level write yet. HX-09, which first runs host hooks inside one, turns that branch into a unit (the one place to change).
3. **`IAlvoTransaction` derives from `IAlvoSession`.** §5 lists its writes with `AlvoPrecondition?`, batch verbs, `PublishAsync`, `AsSystem()`; the session already has the reads and writes in that shape, and deriving makes HX-05's typed reads and this PR's typed writes work on a unit with no second copy. A session's "each call commits on its own" is restated as the property of `IAlvoSessions`' sessions. Reads are on the unit because R-B1 refuses every other read while it is open.
4. **`IAlvoTransactions` has a non-generic `TransactAsync`**, a default interface member over `TransactAsync<T>` (§5 lists only the generic one): a unit that answers nothing is the common case, and a `Task`-returning lambda must not have to `return true`. Overload resolution follows `Task.Run(Func<Task>)`/`Task.Run(Func<Task<T>>)`.
5. **The refusal covers every data-port member, reads included, and fails the unit.** R-B1 names `IAlvoData`, `IAlvoSession`, nested `TransactAsync` and `IAlvoEvents.PublishAsync`; all nine `IAlvoData` members refuse before any statement, the session through `IAlvoData`, and the refusal poisons the unit so a caught one cannot commit. **Acceptance (5)'s idempotency refusal**: no `IAlvoTransaction` member takes a key, so the only key that can reach a unit is an `IAlvoData` write's; inside a unit it is refused as `ArgumentException` (`ParamName` `idempotency`) instead of `AlvoUnitInProgressException`, naming the more specific mistake, and it poisons too.
6. **"On the flow" means "its `TransactAsync` has not returned."** `IAlvoFlowUnit.IsOpen` stays true until the unit is released, so a delegate whose unit timed out and rolled back is still refused; work that outlives `TransactAsync` (a `Task.Run` the delegate started) carries the unit in its execution context but is not refused — it runs after the unit.
7. **One call at a time.** A `DbConnection` runs one command at a time; a second call while one runs is refused (`InvalidOperationException`) and poisons the unit, rather than queued — queueing would hide a missing `await` and make the delegate's result depend on scheduling. "At a time" is per level: a call made by the running call itself is a nested call (Ruling 30).
8. **Every call through a unit runs under its own savepoint** (`alvo_unit_<n>`, EF `IDbContextTransaction` savepoints over ADO.NET `DbTransaction.Save/Rollback/Release`), reads included. An exception, or a call that returns without committing, rolls back to it; the unit goes on. A rollback-to that itself fails poisons the unit — SQLite may roll the whole transaction back after `INTERRUPT`, `BUSY`, `IOERR`, `FULL` or `NOMEM` (*Response To Errors Within A Transaction*), and PostgreSQL's aborted state (`25P02`) is exactly what the savepoint exists to leave. Top-level writes take none (Ruling 2).
9. **What poisons.** The unit's timeout; any `OperationCanceledException` from a call (a cancelled SQLite command may have rolled the transaction back); `AlvoChainDepthExceededException`; a data-port refusal (Ruling 5); a concurrent call (Ruling 7); a failed savepoint rollback (Ruling 8). Not poisoning: a call whose own savepoint rolled it back — authorization, constraint, precondition, not-found, validation, a raw `DbException`. Hook exceptions and the per-hook operation budget join the set with HX-09.
10. **The deadline.** `Timeout` is greater than zero and at most one hour (a unit holds the write lock while it runs), 30 s by default, on the host's `TimeProvider`. At the deadline the unit's token is cancelled and the unit poisoned; as soon as no call is running it rolls back and releases the connection and the write turn — **without waiting for a delegate that ignores its token** — and every later call is refused. `TransactAsync` then throws `TimeoutException` whatever the delegate threw or returned (`A_timed_out_unit_lets_go_of_the_database_before_its_delegate_returns`).
11. **The caller's own cancellation is not a timeout**: it rolls back and surfaces `OperationCanceledException`.
12. **One correlation id per unit.** When the caller's effective provenance carries none, the unit mints one (`AlvoEventProvenance.CorrelationIdOf(Guid.CreateVersion7())` — the ambient W3C trace id when there is one), so every data event and custom event of one unit shares it.
13. **One DI scope per unit, created when it opens, disposed when it ends.** In HX-07 a write inside a unit resolves the declarative before-hook runner from it (`WriteUnit.Hooks`, read through the flow) — the seam HX-09's `IWriteExtensionRunner` joins — while top-level writes keep the runner the driver was built with (Ruling 2). Acceptance (8) is proven on that resolution.
14. **Deferred to HX-09, where they first have something to act on:** in-flight rows and the `self-write` refusal, the per-hook operation counter, `AsSystem()`'s `ExtensionSystemAccess` log and `alvo.hook.system_access` counter (both name an extension; a `TransactAsync` caller already chose its own context, so elevating its own unit grants nothing it lacked), and `SuppressHostHooks`'s effect and audit. `SuppressHostHooks` is validated now — system context and a `Reason` of at most 500 characters with no control character — so its contract does not change when its effect arrives. Also for HX-09 (final security review, note 1): `IAlvoTransaction.AsSystem()` is an in-tenant role escalation reachable by whoever holds the transaction — harmless while only the host's own delegate holds it, so HX-09 decides whether the `write.Transaction` it hands a hook (`Deeper()`) exposes `AsSystem` at all.
15. **No retry.** `TransactAsync` runs the delegate once; SQLite contention is absorbed before it runs, at the unit's single `BEGIN IMMEDIATE`, by the process write gate and the provider's busy timeout. `ReplayableWriteAsync` stays the idempotency-key race loop of top-level keyed writes (C14).
16. **Literal text operators render `field LIKE @pattern ESCAPE @escape`, both bound.** The escape character (`\`) is a parameter, never a literal in the statement, so the renderer's property suite still finds no quote or backslash in any statement text; the value's `\`, `%`, `_` and `[` are escaped — `[` means nothing to SQLite's or PostgreSQL's `LIKE`, where any character after the escape matches itself, and escaping it closes T-SQL's character class before a T-SQL driver (#92) exists. Prior art: EF Core's `StartsWith`/`Contains` translation. Text fields (`string`, `text`) only, a string value only, case-sensitive and ordinal on both engines (SQLite's `case_sensitive_like`, on per connection already). The escaping lives in the shared EF renderer, not in a new `IAlvoSqlDialect`/`IFieldSqlRenderer` member: both are public, and an ANSI `ESCAPE` serves both shipped engines.
17. **Not on the wire.** The three members have no HTTP query-string spelling: `FilterOperators` derives the allow-list from the enum minus them, so it stays PostgREST's ten; `QueryStringParserTests` pins it and the new members' lower-cased names answer `unknown-operator`.
18. **Typed queries' text tests** translate in their one-argument form or with `StringComparison.Ordinal` (HX-05 (6)'s rule for `when`); a culture-folding comparison, a `char` overload, a field-to-field test and a null value are refused when the read is called. Supersedes HX-05 (7).
19. **Typed writes are `IAlvoSession` extensions** (so they work on any session and on a unit), not `IAlvoData` ones: a host endpoint gets a session with `IAlvoSessions.For(caller)`. They return `T?` — `null` when the caller's own read-back lacks a field the class maps (a `get` rule excluding the row, a hidden field), because the write happened and an exception would look like a failure (and inside a unit, roll it back). `ExistsAsync<T>` takes a predicate (a by-id existence is `GetAsync<T>(id) is not null`). `AlvoPatch.For<T>()` is a public static factory §5 does not list — an interface needs a way in, and the house precedent for a guarded factory is `AlvoCustomEvent.Create`. A write never sends `id`, the audit columns or `deleted_at` — and a replace never sends `tenant_id` — by `AlvoManagedColumns.All` and `IsCallerWritable`, the port's own list, not a second copy; an enum is written as its declared value, a whole number as `long`. A create leaves out null properties (Ruling 36).
20. **One custom-event envelope builder.** `AlvoEvents`' guards and envelope move, unchanged, into Abstractions (`AlvoCustomEnvelope`, internal) so the transactional publish and the autocommit one cannot drift. The autocommit publish keeps its order of refusals, now name → unit → depth → clock.
21. **`EfAlvoData` becomes `partial`**; `EfAlvoData.Units.cs` joins `ChangeTrackerReachTests`' allow-list (it reads the unit's connection and writes the unit's outbox row). `WriteUnit`, `UnitSavepoint`, `TurnHeldTransaction`, `EfAlvoTransactions` and `EfAlvoTransaction` receive their connection and transaction as parameters and need no entry.
22. **`InMemoryAlvoData` implements the three literal operators** (the reference evaluator, ordinal) — the contract suite runs on it — **but not `IAlvoTransactions`** (§5).
23. **`AlvoUnitInProgressException` is an `InvalidOperationException`** — `IAlvoData`'s invariant family, rendered 500 by the HTTP layer: it is a programming error in host code, not a request the client can fix.
24. **`IAlvoTransactions` is registered over the concrete `EfAlvoData` singleton**, and `IAlvoData` now forwards to the same instance. A host decorating `IAlvoData` therefore does not decorate a unit's calls — a unit must reach this driver's connection — and the docs say so.
25. **Public types §5 does not list:** `AlvoPatch` (Ruling 19), the non-generic `TransactAsync` (Ruling 4), and `AlvoDataLiteralTextTests` (the "contract rows" §5 assigns to HX-07, as a shipped suite every provider inherits).
26. **`IAlvoScheduler` joining a unit is HX-13's** (§4.10): nothing here touches `ITimerStore`; the unit's connection and transaction are exposed internally for it.
27. **Dashboard: none** (§7). No admin page shows units; the operations page's history is unchanged — a unit's events reach it as any event.

28. **(Review H1) `BeginAsync(` is a fourth transaction marker for `BeforeHookTransactionArchitectureTests`.** After Task 7 `CreatedAsync` holds its transaction from the seam, which the text scan did not know: `BeginAsync` always answers an open transaction (the write's own, or its unit's under a savepoint), so a pipeline call after it is inside one. Two control rows pin the new marker both ways (a call after it guarded, a call before it not); the eight-site count is unchanged.
29. **(Review H2) EF's `__EFSavePoint` already recovers a failed insert; the unit savepoint is for everything else.** Inside a transaction EF did not start, `SaveChanges` wraps itself in a savepoint and rolls back to it on failure, so acceptance (2)'s literal insert fact passes on PostgreSQL with or without `UnitSavepoint` — the earlier claim that it would fail with `25P02` was wrong and is withdrawn. The fact that needs `UnitSavepoint` on PostgreSQL is the update path: an `ExecuteUpdate` unique violation, caught, then another write that commits (`A_caught_unique_violation_on_an_update_lets_the_unit_go_on_and_commit`, both engines). The insert fact stays as acceptance (2)'s literal row.
30. **(Review H3) The one-call gate is per level, so a call made by the running call is admitted.** Each call records itself in an `AsyncLocal` `CallFrame` while it runs; a call whose flow carries a frame of the same unit waits on that frame's own gate instead of the unit's, under its own savepoint. A sibling — another call on the delegate's flow, or a second nested call beside the first — is still refused and poisons. This is what lets HX-09's hooks write through `Deeper()` from inside the write that runs them; `Deeper()` stays. Work a running call hands to `Task.Run` inherits its frame and counts as nested — stated, not guarded.
31. **(Review H4) The dispose-without-commit rollback has a fact of its own on both engines, and the refused-batch fact is dropped.** A batch the rules refuse judges every row before writing any, so it can never exercise the rollback; its fact passed whatever `UnitSavepoint` did and was removed. `UnitSavepointFacts.A_savepoint_disposed_without_commit_takes_its_insert_back_and_the_unit_goes_on` inserts through EF (EF's own savepoint already released), disposes the savepoint uncommitted, inserts again and commits — on SQLite and PostgreSQL. The engine-failure batch fact stays: it fails a `SELECT` after a successful insert, so it too fails without the rollback.
32. **(Review M1) A unit's connection is opened through EF.** `OpenUnitAsync` calls `root.Database.OpenConnectionAsync` before `EnsureOutboxTableAsync`, which opens a closed connection raw and so runs no interceptor; otherwise the first unit of a process ran SQLite's `like` and the literal text tests case-insensitively for its whole life. Held by `A_text_test_in_the_first_unit_of_a_fresh_host_is_case_sensitive` (both engines; the defect is SQLite's).
33. **(Review M2) Every framework store's write is refused inside a unit, at the write gate.** `IOutboxStore`, `IDeliveryStore` and the secret, descriptor-version, runtime-schema and management-idempotency stores are public and DI-registered, so host code can reach them; each waits at the database's write gate before writing, and on SQLite would wait for the unit's own turn, then commit beside the unit after its deadline. `NoRelationalWriteGate.EnterAsync` and `SerialRelationalWriteGate.EnterAsync`/`TryEnterAsync` (which SQLite's gate waits through) refuse with `AlvoUnitInProgressException` while a unit is open on the flow — fail closed, every engine, every present and future store. A unit takes its own turn before it is on the flow, and calls inside it never take one. A store's reads take no turn and stay allowed: they commit nothing. The refusal, like all of R-B1, follows the execution context; the docs say what escapes it.
34. **(Review M3) A read's savepoint is released on success**, so the unit's savepoint stack does not grow with every read; a failed read still rolls back to it. On PostgreSQL every writing call is a subtransaction, and past `PGPROC_MAX_CACHED_SUBXIDS` (64) in one transaction the per-backend cache overflows and snapshots slow down for every session; the docs and `data-path.md` say to keep a unit small and to write many rows with one batch call. Not enforced: a cap would be a guess.
35. **(Review M4) The both-engine timeout facts run on a `FakeTimeProvider`.** The unit's deadline runs on the host's clock (Ruling 10), so each fact lets the unit write, then advances the clock past a one-second `Timeout`: no wall-clock budget, so a cold first write on a slow runner cannot trip it.
36. **(Review M5) A typed create leaves out every property that is `null`**, so the field's descriptor `default` applies (or, with none, the column stays empty) — exactly what an untyped create that does not name the field gets. A replace sends every field, `null` included, because it replaces the whole row. A non-nullable property's CLR default (`0`, `false`, the first enum member) is a value and is sent: to leave a field to its default, make the property nullable. Decided now because changing it after release would break.
37. **(Review M6) The CI-parity build runs the container as the user** (`docker run -u "$(id -u):$(id -g)"`), so its output is the user's own and nothing is cleaned afterwards; `git clean -fdx` is gone — it removed ignored local state.
38. **(Review M7) The mutation shards follow the new files.** `EfAlvoData.Units.cs` is mutated with `EfAlvoData.cs` in `data-ef-core` (its killers live in `Data.Sqlite.Tests`) and excluded from `data-ef-rest`; the other new driver files fall into `data-ef-rest`. Task 12 re-probes every shard's declared `mutants:`/`suite:`, not one, as the workflow's own header requires.
39. **(Review L2) The refusal rows are checked against `typeof(IAlvoData).GetMethods()`**, so a tenth member of the port cannot ship without its refusal row.
40. **(Review L3) A literal text test on a field that is not text is refused on every implementation.** The reference evaluator alone answered UNKNOWN (an empty page) where the EF renderer throws; `InMemoryAlvoData.QueryAsync` now refuses it by the field's declared type with the drivers' wording, and the contract suite gains that eighth row.
41. **Superseded by Ruling 46.** ~~**(Review L4) A call the delegate does not await is not part of the unit.** If it starts while the commit holds the gate it is refused; the commit, already decided, still commits. Stated on `IAlvoTransaction` and on the docs page.~~
42. **(Review L5) Inside a unit the hook runner comes from the unit's scope, outside from the driver's constructor.** One singleton in production; in `WritePathWorld` the stub runs outside and `AddAlvo`'s real runner inside, which `EfAlvoTransactionsTests`' remarks state.
43. **(Review L6) The spec amendment quotes the deferred clause.** §4.5's "top-level units use the same default" is marked deferred to HX-09 beside Ruling 2, so it reads as a decision.
44. **(Review L7) `IAlvoEvents.PublishAsync` keeps its guard order** — name, subject, caller, payload — and only then the unit refusal, the depth refusal and the clock; `AlvoCustomEnvelope.EnsureWritable` is internal so the order is unchanged.
45. **(Review L1) The base is `main`.** HX-06 (#422) merged and `main` is merged into this branch at `facc8a69`; every diff in Task 12 is against `origin/main`, and the PR targets `main`.
46. **(Task 6 review I2) A call still running when the delegate returns fails the unit; the commit decision is atomic.** Supersedes Ruling 41's "runs after the commit decided without it": `CommitAsync` takes the gate and reads the failure under one lock, and from then on no call is admitted and a later poison is ignored, so a decided commit commits. A call still running at that moment — started by the delegate and not awaited — is cancelled and awaited, the unit rolls back, and `TransactAsync` throws `InvalidOperationException` naming the un-awaited call. Fail closed and deterministic: no silent commit of work the caller did not await. Cost if wrong: a caller that fires and forgets a write inside a unit gets a rollback instead of a commit. Stated on `IAlvoTransaction` and on the docs page (Task 11). Tested by `WriteUnitTests.A_call_still_running_when_the_delegate_returns_rolls_the_unit_back` and `A_call_refused_while_a_decided_commit_runs_does_not_undo_it`.
47. **(Task 6 re-review N2) A committed unit reports success even if its cleanup then fails.** After a successful `COMMIT`, a failure disposing the transaction or the context is swallowed and logged as a `Warning` once the write turn is released, and `TransactAsync` reports success: the data is committed, and a host that retried on the exception would write it twice. A failure before or during `COMMIT` still surfaces. Tested by `WriteUnitTests.A_cleanup_failure_after_a_successful_commit_is_logged_not_thrown`.
48. **(Task 7 review) A unit is joined only on its own flow, at one choke point.** The flow-vs-unit guard lives in `UnitSavepoint.JoinAsync`, which every join passes, and a refused join also poisons the unit; `WriteUnit.EnterFlow()` is the one way a unit gets onto the flow, and `TransactAsync` uses it. Cost if wrong: none observable.
49. **(Task 7 review) `MMLib.Alvo.Identity` writes inside a unit are documented, not refused, in HX-07.** Spec R-B1 names `IAlvoData` and `IAlvoEvents` only, and Identity has its own unit of work. Flagged for the user. Cost if wrong: on SQLite an identity write inside a unit waits on `busy_timeout`.
50. **(Task 8 review M5) A decided commit is not cancellable.** Once `WriteUnit.CommitAsync` has decided to commit (Ruling 46), its `COMMIT` runs with `CancellationToken.None` — `CommitAsync` takes no token — for the same reason as Ruling 47: a caller's cancel landing during `COMMIT` must never report as cancelled data the engine committed. The caller's token still cancels and rolls back the unit at any point before the decision. Top-level writes still pass the caller's token to their `COMMIT` and are unchanged in HX-07 (follow-up). A caller's cancel that landed before the decision is part of the decision: the unit rolls back and throws `OperationCanceledException` even when the delegate never observed its token (Task 8 re-review R1). After the decision a hung `COMMIT` is bounded by the provider's command timeout, not by `AlvoTransactionOptions.Timeout` — Task 11's docs say so (R2). Cost if wrong: a caller's cancel during `COMMIT` waits for the `COMMIT` to finish. Tested by `EfAlvoTransactionsTests.A_cancel_during_the_commit_reports_success_with_the_data_committed` and `A_cancel_before_the_commit_rolls_back_even_when_the_delegate_ignores_its_token`.
51. **(Task 8 review, carried into Task 9) A deadline that has already fired wins over the commit.** `WriteUnit.Decide()` reads the deadline itself: when it has fired, the unit's failure is a `TimeoutException` and the unit rolls back, even when the deadline's callback has not yet recorded the timeout — the timer thread can lose the race for the unit's lock to a returning delegate, and without this the decision would commit a unit that exceeded its `Timeout`, against acceptance (10). The deadline is read before the caller's cancellation, so a timed-out unit reports a timeout, not a cancellation. Cost if wrong: a delegate that returns in the instant the deadline fires is rolled back instead of committed. Tested by `WriteUnitTests.A_deadline_that_fired_before_the_commit_decision_rolls_the_unit_back`.
52. **(Task 9 review I1) A typed create or replace never sends a descriptor `computed` or `rollup` field.** The store maintains them — the engine a computed (generated) column, the framework a rollup — so they are read-only like the managed columns of Ruling 19, and a read-model type that maps one (an invoice with its rollup total) stays writable instead of being refused by the port. The typed layer learns them from the applied schema through an internal `IAlvoSessionSchema` in Abstractions, which Alvo's own sessions implement (`AlvoSession` over `ISchemaRegistry`, the EF unit's `EfAlvoTransaction` over the port's applied schema); a session that does not implement it (a host's fake) sends every mapped field and the port judges it. Both implementations answer by name alone, whatever the entity's `storage`: the typed layer reads only field traits, and the descriptor treats a dynamic entity like a physical one, so `EfAlvoData.AppliedEntity` (inside a unit) does not apply the driver's physical-only filter that `AlvoSession.EntityOf` (outside) never had (Task 9 re-review N1; `EfAlvoTransactionsTests.A_unit_names_a_dynamic_entity_as_a_session_outside_one_does`). A patch is unchanged: `Set` on a computed or rollup field is sent, and refused by the port. Cost if wrong: a host expecting an error for writing a computed field gets it silently dropped (it was never writable). Tested by `TypedWriteTests.A_typed_create_never_sends_a_computed_or_rollup_field`, `A_typed_replace_never_sends_a_computed_or_rollup_field` and `A_wrapper_read_back_with_its_computed_and_rollup_fields_creates_without_them`, and on both engines through the real `AddAlvo` composition — inside a unit and through `IAlvoSessions` — by `TransactionFacts.A_typed_record_mapping_a_computed_and_a_rollup_field_creates_and_replaces_in_a_unit_and_outside`, which fails if the registered session stops naming the schema (Task 9 re-review N2).
53. **(Task 9 review I2) A typed create whose `tenant_id` is `Guid.Empty` is refused** with `ArgumentException` (`ParamName` `record`) before the port, whoever the caller is — a named exception to Ruling 36: the CLR default of the tenant column is the reserved "no tenant" of `TenantId`, not a value. The port already refuses it today (a tenant caller and a system caller in a tenant by the tenant scope, a system caller with no tenant by "the caller has no tenant"), but as a policy refusal that reads like an authorization failure; the typed layer names the fix. The extension remarks state that a typed create on a tenant-scoped entity must map `tenant_id` and set it. Fail closed for system callers too. Tested by `TypedWriteTests.A_typed_create_whose_tenant_is_the_empty_uuid_is_refused_before_anything_is_sent_whoever_the_caller_is` and `A_wrapper_whose_record_carries_the_empty_uuid_as_its_tenant_is_refused`.
54. **(Final security review, note 2) A nested call ends inside the call that started it.** `WriteUnit.RunAsync` finishes a call's `CallFrame` when the call returns. A call started inside it — on its flow, so it inherits the frame — that is still running then poisons the unit and is refused when it returns; one that only starts after is refused and poisons the unit. Before, such a task waited on the finished frame's own gate, outside the unit's one-at-a-time gate, so it could run on the shared connection beside a sibling call or, racing the commit decision, beside `COMMIT` — a row committed without the outbox event its write was still adding. No authorization step was skipped, and in HX-07 the only host code running inside a call is a host-registered CEL function, but HX-09 runs host hooks there. Cost if wrong: a host CEL function (or, in HX-09, a hook) that fires and forgets a call through the unit fails the unit instead of racing it. Tested by `WriteUnitTests.A_call_started_inside_a_call_and_still_running_after_it_returned_poisons_the_unit` and `A_call_started_inside_a_call_that_starts_after_it_returned_is_refused_and_poisons_the_unit`, both deterministic (`TaskCompletionSource`, no delays).

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
nested write's depth = the unit's depth + 1. (A write through `TransactAsync`'s own `IAlvoTransaction` is at the
unit's depth; "nested" is a write made under another write's stage — §4.5 *HX-07 settled details* (1).) The event a write emits carries `chaindepth` = that write's depth.
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
proceed, later entries of the same key are held back without spending an attempt, and an ordered delivery is not claimable while an earlier entry of its key is undispatched below the outbox ceiling. Lane order is `event_id` order.
**Per-record order holds across batches, failed fan-outs and dispatcher crashes; #150's id condition still applies
(two emitting processes within one millisecond)** — a recorded deviation, *HX-03 settled details* (20). A subscriber receives events fanned out after its registration on
the fanning replica; rolling deploys use the single-dispatcher shape (`Alvo:Events:Enabled` on one replica).

**Claim.** One `UPDATE … RETURNING` per batch with the outbox's EvalPlanQual-safe shape, restricted to
`subscriber IN (@registered)`, `not_before <= @now`, no pause control, and — for ordered subscribers —
`NOT EXISTS` an earlier delivery of the same subscriber and `partition_key` whose status is
pending/running/retrying/dead (other than `unbound`). A claim sets `status = running`, `lease_until = now + longest timeout + 30 s + 30 s` (*HX-03 settled details* (11), (24)); an
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
`alvo.delivery.dead`, `alvo.delivery.abandoned` (HX-03).

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

**HX-03 settled details.** Rulings and deviations taken while planning (1–31, verbatim from `docs/superpowers/plans/2026-10-23-hx-03-event-handlers.md`), while building (32–44) and in the final review round (45–53), so a later reader can tell a decision from an oversight. Where a ruling and this section's text above disagree, the ruling is what shipped.
- (1) **Kill mid-delivery (2) is proven in process.** Host A's handler never returns, leaving a `running` delivery under an unanswered lease (read off the database); host B — a separate container over the same database, on a clock past the lease — is the restart. A SIGKILL child harness exists only for the standalone image, which cannot carry a C# handler. What this cannot catch is a defect in a real process exit (something on `ApplicationStopping` recording an attempt); `A_shutdown_mid_handler_records_nothing_and_leaves_the_claim` covers that half.
- (2) **(8)'s "write reaches depth 9"** is the delivery that would run at `chaindepth + 1 = 9`: fan-out records it `dead` `chain-depth-exceeded` and never runs it. A handler write refused by the driver (reachable with an explicitly deeper context) is dead at once, never retried — a separate fact — also when the refusal arrives wrapped as an inner exception.
- (3) **(15) is reachable in HX-03 through `AddRetiredExtension`,** whose id may follow any catalog grammar (a retired command or function name may hold `_`); handler ids cannot, so `a-b` beside a retired `a_b` is the live collision. The injective check runs over every id in the namespace; in HX-03 no event name is minted from an id yet, so it protects what HX-08 (command completions) and HX-13 (schedule runs) mint.
- (4) **A re-entrant drain is refused**, not documented: `EventPump` marks its execution flow, and a nested `DrainAsync` throws `InvalidOperationException` at once. Code that suppresses flow escapes the mark (stated).
- (5) **Delivery spans keep the ambient parent and link the envelope's trace;** the delivery flow carries no `TraceParent`, so a handler's writes are traced under its delivery span.
- (6) **Binding is strict at boot, visible after.** The pass runs where the boot plan compiles the catalog (`DescriptorBootPlan`, through `ExtensionBinder`), not inside the public `PolicyCatalog.Build`, which keeps its seven call sites. A code-first boot refuses to start; a stored-descriptor boot records the project `Failed` and stays up (I7). A catalog the boot plan did not bind — a runtime apply, `ApplyAlvoDescriptorAsync` (`SchemaMigrationRunner`), the first catalog of a dashboard-first host that booted `Awaiting` — binds what fits, never a stale catalog, logs `ExtensionUnbound` once, and records a **dead `unbound` delivery** for every event an unbound handler's source selects (I3): visible, replayable by HX-06, never a silent drop. **An `unbound` row does not block its (handler, record) lane** (N6): it is a record of missed work, selected without `when`, so it may name records the condition would never have matched; once the handler rebinds, later events flow, and the missed ones are replayed explicitly — a deliberate order trade-off. HX-04's apply guard then refuses such an apply; until it does, acceptance (7) holds for a dashboard-first host only in that form. — **As built:** the lenient first catalog of an `Awaiting` boot is reachable only in the manual dispatch mode (test hosts). In the background mode both dispatchers stand down on a boot that primed no catalog — each logs a Warning (`DispatcherStoodDownUnprimed`, `DeliveryDispatcherStoodDownUnprimed`) — and stay down until a restart, whose stored-descriptor boot binds strictly (`Failed` on a misfit). — **As built (HX-04):** every apply path is guarded, change-based (§4.13 *HX-04 settled details* (6)–(9)): no apply can unbind a handler the current catalog binds; a handler that never bound (an `Awaiting` first descriptor, a `Failed` stored boot) blocks nothing, is warned about in the dry run's result (internal, (21)), and keeps its dead `unbound` rows until a descriptor fits it. The dispatchers' stand-down on a boot that primed no catalog is unchanged.
- (7) **`changed()` on an excluded field is refused in a handler's `when`** (stricter than after-hooks), so #398 cannot reach handlers; #399 is inherited through the shared `EnvelopeReading` and noted on the issue.
- (8) **`OnSaved` carries only the post-image:** its `when` may not read `old.` or call `changed()` (the #325 rule); use `OnUpdated` for a condition on the change.
- (9) **A `"*"` or application-event handler's `when` tests only `@user`/`@tenant`** — compiled against a fieldless schema; an application event's payload declares no fields to check.
- (10) **`ExtensionOrderTie`** warns when two registrations on one entity+operation (or one event name) share an `Order` and at least one **stated** it; untouched defaults never tie. For handlers `Order` orders fan-out and listing only — deliveries run independently.
- (11) **The lease is per claim:** the longest registered timeout + 30 s, never shorter than any row's `timeout + 30 s`. The cost: crash recovery for a short handler waits out the longest handler's timeout. — **As built, superseded by (24):** the lease is the longest registered timeout + `LeaseGrace` (30 s) + `SettleGrace` (30 s), **90 s at the defaults**, not "timeout + 30 s" — that lease did not cover the settle wait a timed-out handler gets, so a second claim could start a delivery whose attempt was still unanswered. A deliberate deviation from this section's *Claim* (N4).
- (12) **Pools:** the handler pool ships; the job pool (`MaxJobConcurrency`) arrives with its first job kind (HX-13) — a pool with nothing to run is configuration that does nothing.
- (13) **Deferred public types:** `AlvoHookOptions` → HX-09; `EventCondition<T>`, `AlvoEventAttribute`, `AddEventHandler<TMessage,THandler>` → HX-05. HX-03 ships the untyped `AddEventHandler(id, eventType, Delegate)` and `AddEventHandler<THandler>(id, eventType)` over `AlvoRecord`.
- (14) **`EfCoreDeliveryStore` is internal;** the port is the contract.
- (15) **A poison crash is bounded:** a delivery reclaimed past its policy's last attempt is dead `lease-expired` without running. The cost: a claim a graceful shutdown left behind also counts an attempt, so repeated rolling restarts can dead-letter a healthy delivery — bounded by the policy, and redeliverable in HX-06.
- (16) **Declarative after-hook actions do not enter `AlvoFlow` in HX-03** (they move onto deliveries in HX-15); the stub receiver's `SuppressFlow` and the R-12 loop fact stay.
- (17) **`AlvoRunAs.EventActor`** resolves the envelope's role names against the roles the descriptor still declares; an undeclared name is dropped, none left is `anon`.
- (18) **"Pending state" for the E3 boot check** is pending, running or retrying; a dead delivery is history and does not block a start. A handler re-using a retired id is a duplicate.
- (19) **`ON CONFLICT … DO NOTHING`** is SQLite's and PostgreSQL's spelling, like the outbox's `LIMIT`/`RETURNING`; Alvo's own tables on T-SQL belong to #92.
- (20) **Lane order is event-id order, kept across batches and crashes** (B1): an ordered delivery is not claimable while an earlier outbox entry of its key is undispatched below the outbox ceiling, so a failed or abandoned fan-out holds the record's later deliveries back; an entry past the ceiling stops blocking (it is lost to after-hooks as well — the outbox's own DLQ stand-in). The outbox claim is unchanged. An entry under a claim younger than the outbox's claim lease blocks even at the ceiling (N1): its last attempt's claim already counted it there while it is being fanned out; once that claim is stale it was abandoned and stops blocking. A same-key entry held behind a failed fan-out is handed back with `HoldBackAsync`, **without spending an attempt** (I6). `HoldBackAsync` is an abstract interface member, not a default one: a default falling back to `ReleaseAsync` would silently burn attempts for every third-party store (the repo's `DecodeConstraintViolation` precedent). `HoldBackSql`, like `ReleaseSql`, is not fenced on `claimed_by` — sound under #150's one dispatcher. What remains of #150 is its id condition.
- (21) **An event that produced only dead deliveries** counts as `alvo.events.dispatched`, not `filtered`.
- (22) **`IAlvoSession` writes carry no idempotency key** (a handler deduplicates on `EventId`) and no batch verbs (HX-07's `IAlvoTransaction` has them).
- (23) **The port's records are `required init`, never positional** (I5), so HX-06/13/14 can add members without breaking a provider.
- (24) **A timeout is enforced** (I4, N4): the runner awaits the handler with `WaitAsync(Timeout)`, cancels, waits at most `SettleGrace` (30 s), then answers — exactly once by the fence; the lease is `longest Timeout + LeaseGrace + SettleGrace`, so the answer lands inside it with 30 s to spare and no second claim runs the delivery while its attempt is unanswered. Abandoned handler tasks do not count against `MaxConcurrency`. A handler that ignores its token is abandoned to the background (X15), may overlap its retry, and must be idempotent. — **Amended by (45) and (47):** abandoned invocations are bounded per handler, and a handler that ends within the settle grace is answered by its own result.
- (25) **A malformed `tenantid` (or, for `EventActor`, `authid`) fails closed** (M10): the delivery is dead `event-unreadable`, never run as a tenantless system or an anonymous caller.
- (26) **A manual round loops its claims** (M5, N5): at most `MaxConcurrency` at a time, claim again until nothing is due or `Alvo:Events:BatchSize` deliveries were claimed in the round — no false loop on a backlog, and no claimed delivery waits for a slot with its lease running (ruling 11's premise holds in both modes).
- (27) **E4 holds by construction** (M4): `PolicyCatalogProvider` primes one project per host, and handlers bind to that catalog only.
- (28) **`DeliveryRunFailed` logs the exception's type name, not the exception** (M9).
- (29) **A dead delivery — other than `unbound` — blocks its (handler, record) lane until HX-06's redeliver** (M11); the docs give the manual remedy — set the row `cancelled`, or retire the handler. **As built**, the guide also gives a by-hand retry: `attempts = 0` and the delivery's `alvo_delivery_attempts` rows deleted in one transaction, because a re-pended row at its last attempt is reclaimed dead `lease-expired` and the attempt number keys the attempts table. — **Superseded by HX-06:** redeliver (§4.8 *HX-06 settled details* (2)).
- (30) **`EventActor` resolves roles by name against today's declaration** (M16): a role redefined since grants today's rules; a system-originated event runs its actor handler as `admin`.
- (31) **Rejected: a contract fact for fan-out atomicity** (M8, one item of four) — a partial insert cannot be forced through the port portably; the EF store's single multi-row statement is atomic by construction. The other three M8 facts ship.
- (32) **`DeliveryRequest.UnboundReason`** is a public `const` (`"unbound"`), review-driven: a third-party store must recognise an `unbound` row to keep it out of the lane condition (6), so the word is part of the port, not an internal detail.
- (33) **`IOutboxStore.HoldBackAsync` and its implementation `EfCoreOutboxStore.HoldBackAsync`** are public — an abstract member of a public port on an already-public class (20). They shipped with the delivery store rather than with the dispatcher's use of them.
- (34) **The fixed error types** a delivery records are `when-failed`, `chain-depth-exceeded`, `timeout`, `event-missing`, `event-unreadable` (25), `lease-expired`, `retired` and `unbound` (6); any other failure records the exception's type name, never its message.
- (35) **SQLite has one in-process write line per database** (#410). Measured first: 10 000 chaos events at `MaxConcurrency` 16 took 1 029 s on SQLite (the PostgreSQL leg, at 64: 5.6 s), the cost being `Microsoft.Data.Sqlite`'s busy-retry sleeps, not work. The EF driver gained an internal `IRelationalWriteGate` — `SerialRelationalWriteGate` per database file for SQLite (`SqliteWriteGate`), the no-op `NoRelationalWriteGate` as the driver's default (PostgreSQL) — around every write transaction of the EF driver — the outbox and delivery stores, the EF data path (an outbox append inside a data write holds the turn once; no re-entrancy), `EfCoreRuntimeSchemaWriter`, `EfCoreSecretStore`, `EfCoreDescriptorVersionStore` and `EfCoreManagementIdempotencyStore`: 1 029 s → 55 s, and 1 098 s → 84 s when every handler also writes through `IAlvoData`. A wait is bounded by the connection's busy timeout (`Default Timeout`; 0 leaves it unbounded, as the engine's own wait is) and then fails with the engine's own `SQLITE_BUSY` "database is locked", the error the replay loop and the API already answer. Code that runs while the turn is held — a host CEL function, a replacement `IBeforeHookRunner` — must not write through Alvo. Another process on the file, or an ungated write path, still meets the engine's lock. Not a per-engine branch in the core: the gate is a driver registration.
- (36) **Both claims run write-first on SQLite** (`BEGIN IMMEDIATE`): `Microsoft.Data.Sqlite` retries a busy engine only on a statement's first step, so an auto-committed `UPDATE … RETURNING` could fail its commit with "database is locked". The outbox claim had the same latent defect on `main` and is fixed with it.
- (37) **Neither dispatcher dies on a failed round.** The outbox dispatcher stopped for good on any round exception (pre-existing on `main`); both now log the exception's type (`DispatcherRoundFailed`, `DeliveryDispatcherRoundFailed`) and wait a backoff that doubles from `PollInterval` up to one minute, reset by a round that succeeds. A PostgreSQL deadlock between two delivery claimants (`40P01`) is such a failed round — there is no `SKIP LOCKED`.
- (38) **The E3 guard is two steps of `AlvoBootService`, not a hosted service of its own** (a separate waiter hung hosts that never run the boot): a **read-only** orphan check right after stage 1 brings the system tables up and before anything converges, so a refused start changes nothing a rollback would find; and `AddRetiredExtension` applied last, just before Ready. A retirement applies only at boot; a delivery of the id still running elsewhere may finish its side effect — only its answer is fenced off.
- (39) **An `Awaiting` boot runs the catalog-free registration checks** — ids, duplicates, derived-name collisions, timeout and enum ranges, services the container cannot provide — and refuses at start: a host-code bug does not depend on the descriptor, so a dashboard-first host fails fast too.
- (40) **Bounds on host-supplied numbers:** `AlvoHandlerOptions.Timeout` positive and at most 1 h (a longer one would overflow the lease arithmetic); each `AlvoRetryPolicy` wait non-negative and at most 7 days; `Alvo:Deliveries:MaxConcurrency` 1–256; an out-of-range `AlvoRunAs`/`AlvoOrdering` value set through `configure` is refused at boot.
- (41) **Abandoned handlers are counted** on `alvo.delivery.abandoned{alvo.subscriber}`, and a later failure of one is logged by type (24, X15): they are unbounded across deliveries — at most `MaxConcurrency` per timeout + settle grace — and the docs say so. — **Superseded by (45):** they are bounded per handler.
- (42) **The runner's own shape** (accepted at review): the timeout races a `Task.Run`-started invocation, so a handler that blocks synchronously is still timed out; any exception while the host is stopping records nothing; `EventActor` is built through `EnvelopeReading.CallerOf` — an envelope with no readable credential is anonymous — and resolves roles against the catalog's declared roles.
- (43) **`IAlvoSessions.For` keeps the spec's name** with a scoped CA1716 suppression; a VB consumer calls `[For]`. `AlvoEventContext.Data` is always bound through it, never a raw `IAlvoData`.
- (44) **The outbox index `ix_<outbox>_unfanned`** (20) is built by the first boot; a failed build is retried once, idempotently, then rethrown — never swallowed by the table-existence recovery. On PostgreSQL a plain `CREATE INDEX` blocks outbox writes while it builds (CHANGELOG upgrade note).
- (45) **Abandoned handlers are bounded per handler** (final security review F1, amending X15 and (24)/(41)). `AbandonedHandlers` tracks every abandoned invocation until its task ends; a handler with `Alvo:Deliveries:MaxConcurrency` of them alive is left out of every claim — the background loop's and a manual round's — until one ends, and entering that stand-down logs a Warning `HandlerStoodDown` once. Other handlers keep flowing. Claiming stops at the cap, but deliveries of that handler already running may still be abandoned, so one handler strands at most `2 × MaxConcurrency − 1` threads (exactly the cap with one delivery at a time) — instead of one per timed-out attempt across every record, which could starve the process's thread pool. The bound is per host and per handler.
- (46) **The answer is retried inside the lease** (F4). A storage refusal of `CompleteAsync`/`FailAsync` — any `DbException` in the chain, because the core cannot classify an engine's transient codes and SQLite's busy is not marked `IsTransient` (measured) — is written again after 50 ms, doubling to 1 s, while the retry would still start `AnswerMargin` (5 s) before the handler's timeout + `LeaseGrace` + `SettleGrace` since the attempt began, which never exceeds the claim's lease, and no later than `SettleGrace` after the first refusal (a permanent refusal under a 1 h timeout is not retried for an hour); `DeliveryAnswerRetried` logs the first retry, then every tenth. The bound is on a retry's **start**: on SQLite the write may then wait at the write gate and land after the lease, and the fence decides — recorded if nobody reclaimed the delivery, `false` otherwise. A fence that no longer holds (`false`) is never retried, a stop ends the retries, any other exception is not retried. The write is fenced and idempotent, so retrying a permanent refusal costs only time inside the lease; a retry after an ambiguous commit reads as `lease-lost`.
- (47) **A handler that ends within its settle grace is answered by its own result** (review M3, consistent with (42)'s deadline race): succeeded, its own exception type, or dead `chain-depth-exceeded` — unless it ended by giving in to the cancellation (an `OperationCanceledException` or a cancelled task), which is `timeout`. Only a handler still running after the grace is abandoned and answered `timeout`.
- (48) **An answer that throws still ends its span**, as `failed`, before `DeliveryRunFailed` logs the type (review M4).
- (49) **`ix_<deliveries>_lane_open`** — a recorded addition to the *Tables* DDL above (review M2): a partial index on `(subscriber, partition_key, event_id) WHERE status IN ('pending', 'running', 'retrying', 'dead')`, created `IF NOT EXISTS` on both engines beside the unchanged `ix_<deliveries>_lane`. The claim's lane check carries the predicate verbatim (SQLite uses a partial index only then), so it seeks the rows that can still block instead of walking a lane's succeeded history, never purged before #154. The engine suites pin the plan (`EXPLAIN QUERY PLAN` on SQLite; `EXPLAIN` on PostgreSQL).
- (50) **The E3 guard brings the store up first** (review I1): both steps call the idempotent `IDeliveryStore.EnsureAsync` before they read or write, so a third-party store that builds its storage there — as the port promises — is not read before its tables exist; the contract suite pins that ensured, empty storage reads as no work.
- (51) **The orphan check's operator cost is stated** (F3, E3): it refuses a rollback to a build that does not know a handler with unfinished work, and two applications with different handler sets over one database. The guide gives the remedy — roll forward, drain first (or cancel the rows), roll back to a build carrying `AddRetiredExtension`, or register the same handlers / use two databases.
- (52) **Every dispatcher log line names an exception by its type's full name only** (F6, review M9): `DeliveryDispatcherStopped` no longer carries the exception, and the outbox's `DispatcherRoundFailed` and `HoldBackFailed` use the full name like the delivery lines. The outbox's own `DispatcherStopped` backstop, pre-existing on `main`, still logs the exception.
- (53) **Parked: unit-testing a class handler** (review M10). `AlvoEventContext` has only an internal constructor, so a class handler is tested through `AlvoTestHost` (the guide says so); a public factory or a Testing helper belongs to HX-05's typed layer.
- **Order trade-off until HX-15.** Acceptance (6) holds for the failing event's own delivery; a later delivery of the same record waits behind an event whose descriptor action keeps failing — about `attempts × PollInterval` per failing event (≈ 45 s at the defaults: 1 + 2 + … + 9 s), bounded by the outbox ceiling — so with a webhook endpoint down, a record with *n* quick updates reaches its handler after ≈ *n* × 45 s. If that bites before HX-15 moves declarative actions onto deliveries, decouple "fanned out" from `dispatched_at` with a per-event fan-out marker in `alvo_deliveries` (no `ALTER` of the outbox).
- **Follow-ups this PR leaves open:** #398 and #399 (after-hook `changed()` defects — (7): #398 cannot reach a handler, #399 is inherited through `EnvelopeReading`), #400 (an apply-time warning for a condition reading `@tenant.id` on a global entity, which a handler's `when` meets the same way), #405 (the `embed/**` snippets; the handler page's C# is under `extending/**`, compiled by the HX-02 harness), #410 (SQLite delivery throughput — (35) answers its done-when, 1 029 s → 55 s at concurrency 16; closing it is the maintainer's call after the PR).
- **Recorded deviation from the *Fan-out* paragraph** (review B1): its in-batch stop alone did not keep order across batches, a failed fan-out and a dead dispatcher, so the claim also waits for an earlier undispatched outbox entry of the key (20).

**HX-06 settled details.** Rulings and deviations taken while planning (1–25, verbatim from `docs/superpowers/plans/2026-10-24-hx-06-delivery-operations.md`), so a later reader can tell a decision from an oversight. Where a ruling and §4.8's *Operations* table disagree, the ruling is what shipped. (3) is reworded to what was built, and (26) records what building added.

- (1) **The delivery port grows instead of a second store port.** §5 lists no store additions for HX-06, but operations need storage a provider implements: `IDeliveryStore` gains nine **abstract** members (`ListAsync`, `FindAsync`, `AttemptsAsync`, `SelectAsync`, `RedeliverAsync`, `ReplayAsync`, `SetPausedAsync`, `ControlsAsync`, `FindOperationAsync`) and six `required init` records, and `ClaimAsync`'s contract gains "never a paused subscriber". One port, not a second one, because it has one implementer per provider and one contract suite (`DeliveryStoreContractTests`) that already owns the delivery tables' semantics; because no release tag carries `IDeliveryStore`, so growing it reaches no published consumer (the CHANGELOG names it for anyone building from `main` or `:edge`, and has said the port will grow before 1.0); and because `HoldBackAsync` (HX-03 (20), (33)) set the precedent of growing it. Abstract, not default members: a default `RedeliverAsync` that did nothing would report success and leave the lane blocked. Held by the new contract rows on the EF store (both engines) and the core's fake.
- (2) **A redelivered or replayed delivery restarts its attempt budget; its history keeps numbering.** The reset sets `attempts = 0` — what the retry policy, the backoff and `DeliveryRunner.Admit`'s ceiling read — so a delivery that died at its last attempt runs again instead of being reclaimed dead `lease-expired` at once; the history numbers an attempt `COALESCE(MAX(attempt), 0) + 1` per delivery, so it neither collides with the `(delivery_id, attempt)` key nor loses earlier attempts. The handler sees `AlvoEventContext.Attempt` start again at 1. **The reset is safe only because a claimant names one claim:** with the attempt count restarting, a claim from before a redeliver and the claim after it both hold attempt 1, so the dispatcher mints a fresh claimant for every `ClaimAsync` call (`"{process}:{guid}"`) and answers with it; `DeliveryClaim.Claimant` and the port's *Fencing* remarks say so, and `A_late_answer_from_before_a_redeliver_changes_nothing` holds every store to it. Prior art: Hangfire's and MassTransit's requeue reset the retry count and keep the history. Supersedes HX-03 (29)'s by-hand recipe (which deleted the history).
- (3) **The pause lives in the claim.** A `NOT EXISTS` over `alvo_subscriber_controls` in both halves of the claim, not a filter of the dispatcher's subscriber lists: it holds on every replica and across restarts, with no read-then-claim window (spec §4.8 *Claim*, "no pause control"). Fan-out is untouched, so a paused handler's deliveries accumulate; a delivery already running finishes; on PostgreSQL the pause takes the subscriber's lane lock exclusive (the claim takes it shared), so it waits for a claim already under way and no batch is claimed after the pause commits — **reworded as built** (the plan said one more batch could be taken; the lane lock of (2)'s fix closed that); a drain over a paused handler is quiet, not a loop.
- (4) **The operations log carries the replay's idempotency record.** E11 names six columns; the log adds `affected`, `sample`, `idempotency_scope`, `idempotency_key`, `request_fingerprint`, and a partial unique index on the key. The record is written in the reset's own transaction — the log insert is the transaction's first write and `ON CONFLICT DO NOTHING` makes a concurrent duplicate a no-op — so a crash cannot leave a reset without its record (the window `IManagementIdempotencyStore`'s remarks state for the apply). `IManagementIdempotencyStore` is not reused: it records a revision number, and a replay's answer is a count and ids.
- (5) **A change that changed nothing writes nothing.** Pausing a paused handler, resuming a running one and redelivering a delivery already pending or retrying answer the current state with no log row and no audit line — idempotent operations (§0 *agent-first*); acceptance (7) is read as "each change". A replay always writes its row, even for a count of zero: the row is its key's record.
- (6) **`Idempotency-Key` follows the house rules, which differ from the IETF draft in one answer.** Optional (as on apply and rollback); read from one header line (two lines are 422); refused on a dry run; scoped by `AlvoIdempotency.IdentityOf(caller)`; the fingerprint is the operation, the project, the subscriber, the status and the window — not the reason, so the same replay retried with a reworded note is the same request. A retry answers the stored count and ids with `replayed: true` (the draft's "return the stored response"). The same key with a different request is 409 `idempotency-conflict`, the slug the data path and the apply already send, where the draft suggests 422. The scope is the caller, not the project — as `ManagementIdempotency`'s is — so one caller reusing a key across two projects of one database is answered 409 too.
- (7) **What a replay selects.** One registered handler (required), one finished status (`dead` unless set; `succeeded` and `cancelled` allowed; an unfinished status is 422), and a half-open window over **event time**, mapped onto event ids through an internal `AlvoEventId.Floor` (UUIDv7 ids sort by time; the stores already compare them for lane order). The sample is the first 20 ids in event order — the order they run. A real run resets at most 10 000 deliveries, checked by a count before the reset; a concurrent fan-out can push one replay slightly past it, which is stated rather than locked against. A replay never selects a delivery dead with `chain-depth-exceeded` (`DeliveryFilter.Replayable`): that row is the record of a loop the depth limit stopped, and running it again restarts the loop and its non-Alvo side effects. A real run is refused (409) when any selected delivery's record has a delivery of the handler running (`DeliverySelection.Busy`), and the reset statement itself skips such rows, so a race cannot put two deliveries of one record in flight; the dry run still counts them.
- (8) **Conditions are not evaluated again.** A redelivered or replayed delivery runs its handler; `when` was decided at fan-out (X6). A delivery recorded `when-failed` or `unbound` was recorded without a decided condition, so replaying it runs the handler even for a record the condition would not match — HX-03 (6)'s stated trade-off, now in the operator's hands: the docs and the dashboard's replay confirm say so. Re-evaluating at claim time would change every delivery's semantics and is left to a measured need.
- (9) **Redeliver's guards.** Running → 409; a finished delivery whose record has another delivery of the handler running → 409, and the store's statement refuses the same case (two deliveries of one record must never be in flight together); dead with `chain-depth-exceeded` → 409 (Ruling 7's loop reason); a handler this host does not register → 409 (a pending row for an unknown id refuses the next start, E3); pending or retrying → answered unchanged. A handler registered but not bound to the current schema may be redelivered: the delivery waits until it binds — and, being pending, **holds that handler's later deliveries of the record back until then**, unlike the `unbound` dead row it replaced. The docs say so.
- (10) **`IAlvoOperations` is complete now, including two members that only refuse.** §4.8 defines the port complete in HX-06: `SetEnabledAsync` answers 404 or 409 for every kind that exists until validate hooks (HX-09), and `RunScheduleAsync` 404 until schedules (HX-13); it returns the `ManagementDelivery` that will run the schedule. Unlike HX-04 (1), which defers *init members* nothing fills, an *interface member* added later breaks every implementer and every test double of the port (the dashboard's), so the spec's "complete" stands.
- (11) **`ManagementExtension.Paused` ships; `Enabled` waits.** HX-04 (1) appends `Enabled`/`Paused` "by the PR that first fills them": `Paused` is filled here (the catalog reads the controls); `Enabled` can be false for no kind before HX-09, so it waits for it.
- (12) **Public types §5 does not list.** `ManagementDeliveryAttempt` (the detail's history), `ManagementDeliveryPage` (a keyset page needs its cursor), `ManagementExtensionStat` (the stats envelope's items, the house's envelope-with-items shape), three exceptions (the HTTP layer maps only types: 404 delivery, 404 extension, 409 conflict), the store records of Ruling 1, `IDeliveryStoreWorld.OperationsAsync` and the contract rows (Testing), and `IAlvoEventPump.DeliverAsync` — the TestHost holds no `InternalsVisibleTo` (HX-02, HX-05 (12)), so its `DeliverAsync` needs a public core seam, and the pump is the manual-mode surface it already drives.
- (13) **Statuses and operations are wire strings**, not public enums — as HX-04 (4)'s `runAs`/`ordering`: the values are the store's own words, append-safe for a tolerant client.
- (14) **What a fixture keeps.** The record images keep `id`, `tenant_id`, `ref` and `enum` fields and null values (an empty string is a value and is redacted); every other value — `uuid` and `json` included — becomes the string `<redacted:FIELD>`, also in a numeric or boolean field (the developer fills in what a reproduction needs; a typed handler names the field it cannot read). An application event, or an entity the current schema no longer declares, keeps only `id`. The envelope's attributes stay — ids, type, time, tenant, `authid`, the role names in `authclaims`, a data event's `subject` and `partitionkey` (the record's id) — because they identify rather than describe, and a fixture must run as the event's tenant and actor. An application event's `subject` and the value part of its `partitionkey` are host-chosen text and are redacted (the namespace before the first `:` stays, so the envelope still reads as an application event); `tracestate` carries vendor data and is dropped. `Redacted` lists the JSON pointers. Developer level: it discloses a payload's shape.
- (15) **`DeliverAsync` runs as a delivery and records nothing.** Manual mode only (a fixture is an event nobody wrote); the handler's own scope, its `RunAs`, one hop deeper in the chain, its timeout and one delivery span, so `Hooks.AssertRan` counts it; its exception propagates unwrapped; a handler the host does not bind, or an unreadable envelope, is `InvalidOperationException`.
- (16) **Stats are this process's, from a `MeterListener`.** On `alvo.delivery.duration` (one measurement per answered attempt, with subscriber and outcome): runs, failures (`failed`, `dead`), and p50/p95 from 128 quarter-octave bins (within 19 %), in 24 hourly buckets. Alvo's meters are process-wide, so `this-instance` means this process — two hosts in one test process see each other's runs, another replica's are never seen; `since` is the later of a day ago and the process start. Every registered extension is listed, zeros included; `rejections` is 0 until write hooks (HX-09) record them.
- (17) **The audit line.** A source-generated `ManagementAudit` event at Warning (event name = the method's), written after the change commits, carrying actor (`<key id>:<user id>`), operation, target, count and reason — ids and the operator's own words only. A reason is at most 500 characters with no control character, refused 422 before anything is written (it is one log field). A crash between the commit and the log line loses the line, never the row.
- (18) **A gate class beside the management service.** `ManagementGate` holds the level check and the project check for the operations port; `AlvoManagementService` keeps its two private copies unchanged, so the surface every existing management fact pins is not edited to serve a second port. Folding them is a later touch's.
- (19) **A viewer sees every tenant's deliveries.** Ids, statuses, partition keys, tenant ids and error types — never a payload: the Management API is project-wide by design ("data is deliberately absent", `IAlvoManagement`'s remarks) and a delivery row holds no data. The fixture, which holds a payload's shape, is Developer.
- (20) **Security core by trigger, not by list.** §6.2 does not list HX-06; it adds nine authorization rows, a second gated port and a disclosure surface, and `alvo-security-core-review` triggers on authorization — so the checklist, a labelled `/security-review` substitute and `needs-deep-review` apply (Task 22).
- (21) **The two tables** are created by both `SystemSchemaInitializer` and `IDeliveryStore.EnsureAsync` (HX-03's shape, E10), reserved in `AlvoFrameworkTables` (an entity cannot take the name; introspection never plans a drop), with no `ALTER`; the control table carries `disabled_at`/`disabled_by` now (Ruling 11's later need) because a column added later would be one.
- (22) **A page is newest first by delivery id** (fan-out time), continued with `after`, 1–200 rows (50 unless set); `next` is set whenever a page is full, so the last page may be empty — the cost of not counting.
- (23) **The dashboard slice.** One *Operations* page (`g p`, after *Integrations*): handlers with their runs on this instance, Pause/Resume (a reason asked for on pause), Replay dead (the dry run's count in the confirm; the key minted when it is drawn, so a double press replays rather than runs twice); the delivery list by status; a delivery's attempts, **Run again** and **Fixture**. No level-aware hiding: the port refuses and the page draws the refusal, as every other page does. Schedules and timers join with HX-13; the *Lifecycle* tab's p95 and 24 h columns remain HX-09's row (HX-04 (13)).
- (24) **The docs retire the by-hand SQL.** *React to changes* loses *Retry a dead delivery by hand* and its *Not yet*; *Operate deliveries* carries the operator's story with executed snippets.
- (25) **Retention is not built.** `alvo_operations_log` grows without bound until #154; the merge comments there.
- (26) **As built: a per-subscriber lane lock on PostgreSQL.** Review of the redeliver found an ordered-lane race (a reset racing a claim could put two deliveries of one record in flight). Every claim takes a transaction-scoped advisory lock per subscriber it may claim for, shared; redeliver, replay and pause take it exclusive. The key is FNV-1a 64-bit over the UTF-8 of `"{table}:{subscriber}"`, computed in .NET so every process derives the same one. SQLite's writes are already serialized by the write gate, so its dialect answers `null`; `IAlvoSqlDialect.TransactionLockStatement`'s default throws `NotSupportedException`, so a third engine must say how it serializes. Cost: one more statement per claim on PostgreSQL.

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

**HX-04 settled details.** Rulings and deviations taken while planning (1–17, verbatim from `docs/superpowers/plans/2026-10-24-hx-04-extension-catalog.md` *Rulings and deviations this plan takes*, with (1) and (16) amended as built) and while building (18–21), so a later reader can tell a decision from an oversight. Where a ruling and §4.13's text above disagree, the ruling is what shipped.
- (1) **The catalog ships the members HX-04 fills.** `ManagementExtension` carries `Id`, `Kind`, `Stage`, `Entities`, `Operations`, `Event`, `When`, `Order`, `Timeout`, `RunAs`, `Ordering`, `Description`, `TypeName`; `AlvoExtensionKind` has `EventHandler` and `AlvoExtensionStage` `PostCommit`. §4.13's `Concurrency` (HX-14), `Route`/`HttpMethod`/`Access` (HX-08), `Cron`/`TimeZone` (HX-13), `Secrets` (HX-15), `TopLevelOnly` (HX-09), `Enabled`/`Paused` (HX-06) and the other kinds and stages are appended by the PR that first fills them — init members and appended enum values are additive, while a member nothing fills would be a published promise with no behaviour behind it (*public is the contract*; *nothing created ahead of time*). A deliberate deviation from §4.13's full shape. — **Left out, member by member:** of §4.13's `ManagementExtension`, `Concurrency`, `Route`, `HttpMethod`, `Access`, `Cron`, `TimeZone`, `Secrets`, `TopLevelOnly`, `Enabled` and `Paused`; of `AlvoExtensionKind`, `Validate`, `BeforeWrite`, `AfterWrite`, `BeforeDelete`, `AfterDelete`, `Function`, `Command`, `Schedule`, `DataMigration`, `Enricher` and `Narrowing` (only `EventHandler` ships); of `AlvoExtensionStage`, `PreTransaction`, `InTransaction`, `Read`, `Http` and `Scheduled` (only `PostCommit` ships). `ManagementExtensionCatalog.Items` ships as §4.13 writes it. Of the dashboard row, the Lifecycle tab's *Before the transaction*, *In the transaction*, *Reads* and *Commands* lanes and its p95 and 24 h rejection columns are left out (ruling 13).
- (2) **The catalog lists registrations, not bindings,** by `Order` then registration order (the bindings' order); an `AddRetiredExtension` id is not an extension and is not listed. The apply guard is what makes "registered" mean "bound".
- (3) **Operations use the policy's wire names** — `create`, `update`, `delete` — so the simulator filters by plain equality: `OnSaved` lists `create`, `update`; `OnUpdated`'s `update` covers a replace (both emit `.updated`). An application-event handler has `entities: []`, `operations: []` and its type in `event`; an every-entity handler has `entities: ["*"]`.
- (4) **`runAs` and `ordering` are wire strings** (`system`/`eventActor`, `perRecord`/`none`) because the enums they come from are core types Abstractions may not name; `timeout` is a `TimeSpan` (`"00:00:30"`).
- (5) **`TypeName`** is the class handler's full name when `Alvo:Extensions:ExposeTypeNames` is true; a delegate handler is never named (its method's declaring type is compiler-generated noise).
- (6) **The apply guard is change-based, as §4.2 writes it:** an apply is refused only when a handler that binds the current catalog (`IPolicyCatalogProvider.Current`) would not bind the proposed one, checked by HX-03's own `ExtensionBindingCompiler.Compile` through the binder's per-catalog cache, so the guard cannot drift from the boot. That covers §4.2's two named cases (removing a bound entity or field, adding it to `events.exclude`) and every other way a change can unbind a bound handler (a retyped field the condition no longer compiles against, a rename). A refusal carries a pointer to the removed entity, the removed or renamed field (what the condition read, compiled against the current catalog) or the `events.exclude` item, **and** the compiler's own sentence for every problem at `""`, so a second cause is never hidden behind the first. Because only a handler that bound the current catalog is refused, every pointed entity or field existed before the change — "keep the entity/field" is always accurate. Every-entity and application-event handlers bind no field and never refuse an apply.
- (7) **Guarded sites:** `RuntimeSchemaService.ApplyAsync`, `PreviewAsync` (the dashboard's preview, `?dryRun=true`, and a rollback's dry run) and `RollbackAsync`, and `SchemaMigrationRunner.RunAsync` (`ApplyAlvoDescriptorAsync`), which loses HX-03's lenient binding only for bound handlers — its test now pins both halves (a handler that never fit is applied and stays unbound; one that bound is refused). The boot keeps its own refusal (`ExtensionBinder.EnsureBindable`, `AlvoStartupRefusedException`).
- (8) **The guard runs right after validation,** before the revision check, the plan and the destructive guardrail: a binding refusal is a validation finding about the descriptor itself, and an operator must not be told "allow destructive" or "reload" before being told the change can never apply. A stale `If-Match` on an unbinding apply therefore answers 422, not 412 (pinned) — the house order, since `Validate` already precedes the revision check.
- (9) **Handlers already unbound block nothing.** On a dashboard-first host that booted `Awaiting` (nothing primed) and after a stored-descriptor boot that ended `Failed`, no handler binds the current catalog, so no apply is refused for a handler; each one the proposed catalog does not fit is a `Warning` finding (the compiler's sentence, at `""`) in the preview, the dry run and `ManagementApplyResult.Warnings`, and its events keep being recorded dead `unbound` (HX-03 (6)) until a descriptor fits it. Refusing instead would lock such a host out of every unrelated apply. The dispatchers' stand-down until a restart on a boot that primed no catalog is unchanged — a candidate follow-up, not HX-04's.
- (10) **The refusal is a `DescriptorValidationException`** — the same `422 validation` / code `descriptor` an agent already parses (fix text on the wire as `fixSuggestion`), and the shape §4.2 writes. `RuntimeSchemaService`'s public constructor is unchanged; the guard arrives through an internal overload the DI factory calls, so a service a host constructs itself carries no guard (stated in its docs). **Cost:** with at least one handler registered, the guard builds the proposed catalog once and hands it back; `ApplyAsync` and `RollbackAsync` publish that very instance (no second build), and the binder compiles it once — the delivery dispatcher's later `For(catalog)` is a cache hit. A Management apply runs `PreviewAsync` then `ApplyAsync`, so it builds the proposed catalog twice where it built it once before (plus the validator's own build in each); a host with no handler pays nothing.
- (11) **The capability report appends `extensions.<kind>`** to `Honoured` after the five blocks, per request, only for kinds registered; the static `CapabilityReport.Honoured` (held by the skill region and the prototype) stays the blocks, and the dashboard's *Honoured blocks* stat counts entries without a dot.
- (12) **`ManagementPolicyVerdict.Extensions` is filled in HX-04** (the entity's own and every-entity extensions whose operations contain the simulated one); rendering it in the simulator and the apply dry run is HX-09's row.
- (13) **The Lifecycle tab draws one lane, *After commit*, and says so** ("after the commit only"): the descriptor's after-hooks read-only with a link to *On write* (one editor, not two — "editable" is one click to it), and code items read-only with the accent `⟨/⟩ from code` badge the function list's "this host" precedent sets. p95 and 24 h rejections need HX-06's stats and are not drawn; the other lanes appear with the host code that runs in them. The badge counts every extension that runs on the entity, every-entity ones included. The field-removal confirm does not list extensions yet (follow-up): the preview refusal is the HX-04 surface.
- (14) **The assistant reads the catalog only when it matters.** `get_descriptor`'s answer gains `hostExtensions` (per entity or `*`, read through the port inside the same tool call), and the hooks skill's `descriptor-or-code` region sends the model to `get_extensions` only before an *after*-hook on an entity that count names — §4.13's "before proposing a rule an extension enforces" narrowed to the one place a post-commit extension can be duplicated, so no before-hook, rule or rollup case spends a call against the eval's `MaximumToolCalls` (6). The region fits the 6 144-byte cap (6 141) by dropping two host-developer sentences (kept in the *Custom CEL functions* guide) and one parenthetical the `cel-mutate` region already shows. HX-01's handed-over awareness lands as a bullet in `alvo-descriptor-entities-and-fields` (`events.exclude`) and a sentence in `alvo-descriptor-capabilities-and-limits` (`@tenant.id`/`@user.roles` in after-hooks). The instructions move to v7.
- (15) **The eval cases** register two handlers in `EvalWorld`: one on `rental_fleet`, which no other case touches, and one on `rentals.damage_report`, a field no other case reads. `rentals` is the entity of `hook_returned_at`, `rollup_rentals_count` and `automation_refused` — a before-hook, a rollup and a schedule, none an after-hook — so `hostExtensions` naming it adds no read to them. The real-model run is on demand, not on the PR.
- (16) **Fix text follows §4.2 verbatim** for a removal ("Remove the registration and redeploy first, or keep the field." / "…, or keep the entity."); §4.2 names two cases without a sentence, and each gets one this plan adds: an exclusion reads "…, or keep the field in the event images.", and a retyped field the condition no longer compiles against reads "…, or keep the field's type." (both in `ExtensionApplyRefusals`). — **As built:** the plan named only the exclusion sentence; the retype pointer and its sentence arrived in Task 4's review round, which supersedes the plan's Task 4 text.
- (17) **Enum values are pinned** (`EventHandler = 0`, `PostCommit = 0`) and documented append-only: a later PR adds §4.13's kinds after them and never reorders to match the spec's listing. A typed .NET client older than the server cannot read an unknown member (`JsonStringEnumConverter` throws); the docs say a tolerant client reads the raw string.
- (18) **The guard reads the in-process current catalog.** `ExtensionApplyGuard` compares against `IPolicyCatalogProvider.Current` of this process: an apply racing another apply before either publishes, or an apply on another replica whose catalog has not moved yet, is not covered — the same window the catalog publish race already has. Closing it would mean checking against the stored revision inside the writer's transaction; not HX-04's.
- (19) **Razor components in `MMLib.Alvo.Admin` are public by construction.** Razor emits a component as a `public partial class`, so `LifecycleTab` joins the Admin public-API baseline (as 71 of 73 Admin components already had, precedent 356f9a6e). This amends the plan's *Public surface added is exactly* list (plan Global Constraints): the Admin package's surface grows by that one component type, which is not part of the Management contract and is removable later.
- (20) **The badge and the lane count differently on purpose.** The entity list's `⟨/⟩ n` badge counts the extensions of **every** stage that run on the entity (`LifecycleLane.Binding`); the Lifecycle tab's *After commit* lane draws only the `PostCommit` ones (`LifecycleLane.AfterCommit`). In HX-04 the two are identical, since every extension is a post-commit handler; they diverge once a second stage lands (HX-09), when the badge keeps counting all and each lane draws its own.
- (21) **A handler-unbound warning stays off the wire.** The warning ruling (9) adds travels in the internal `ManagementApplyResult.Warnings` (D52), which the assistant reads; neither the HTTP answer nor the dashboard's preview shows it yet. The docs say "the dry run carries a warning", not "the preview shows one".

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
| `AlvoEntityBuilder`, `AlvoHandlerOptions`, `AlvoRetryPolicy`, `AlvoRetirement`, `AlvoRunAs`, `AlvoOrdering` (`PerRecord` and `None`, both implemented here), `AlvoDeliveryOptions` (`Alvo:Deliveries`) | core / `MMLib.Alvo.Extensions` | 03 | sealed classes / enums |
| `AlvoExtensionBuilderExtensions`: `Entity(string, …)`, `Entities(…)`, `AddEventHandler(id, type, Delegate, when, configure)`, `AddEventHandler<THandler>(id, type, when, configure)` over `AlvoRecord`, `AddRetiredExtension` | core / `Microsoft.Extensions.DependencyInjection` | 03 | X14 |
| `IAlvoEventHandler<TMessage>` (`Task HandleAsync`), `AlvoCreated<T>`, `AlvoUpdated<T>`, `AlvoDeleted<T>`, `AlvoSaved<T>`, `AlvoEventContext` | core / Events | 03 | messages: sealed records, `required init` |
| `IAlvoSession` (context-bound, autocommit per call; reads + writes with `AlvoPrecondition?`), `IAlvoSessions.For(AlvoContext)` | Abstractions / Data | 03 | used by `AlvoEventContext.Data`, `ValidateWrite.Data`, `AlvoJobContext.Data` |
| `IDeliveryStore` with `DeliveryRequest` (incl. the `UnboundReason` const), `DeliveryClaim`, `ClaimedDelivery`, `DeliveryAttempt`, `DeliveryFailure` (all `required init`); `DeliveryStoreContractTests` + `IDeliveryStoreWorld`; the EF implementation is internal | Abstractions / Events; Testing | 03 | provider boot obligation (`package-boundary.md`) |
| `IOutboxStore.HoldBackAsync` (abstract member; breaks implementers: CHANGELOG); `EfCoreOutboxStore.HoldBackAsync` | Abstractions / Events; EF driver | 03 | *HX-03 settled details* (20), (33) |
| `AlvoEventName.ReservedNamespaces` gains `alvo` | Abstractions / Events | 03 | E12 |
| `ManagementExtension`, `ManagementExtensionCatalog`, `AlvoExtensionKind`, `AlvoExtensionStage`; `IAlvoManagement.GetExtensionsAsync`; `ManagementPolicyVerdict.Extensions` (init) | Abstractions / Management | 04 | interface member breaks implementers: CHANGELOG |
| `EventCondition<T>`, `AlvoEventAttribute`, `AddEventHandler<TMessage,THandler>` (moved from 03, *HX-03 settled details* (13)) | core | 05 | typed layer |
| `Entity<T>(…)`, `AlvoEntityBuilder<T>`, `IAlvoEntity`, `AlvoEntityAttribute`, `AlvoFieldAttribute`, `AlvoEnumValueAttribute`, `AlvoRecordExtensions.As<T>()`, `AlvoTranslationException` | core | 05 | `AlvoConditionCaller`, `AlvoEventContext.Create` and `AlvoTestHost.EventContext` added (§4.3 *HX-05 settled details* (5), (12)). |
| typed reads `GetAsync<T>`, `QueryAsync<T>(Expression<Func<T,bool>>, …)` on `IAlvoSession`/`IAlvoData` | core (extensions) | 05 | translate to `AlvoFilter` |
| `IAlvoOperations` + `ManagementDelivery`, `ManagementDeliveryQuery`, `ManagementReplayRequest`, `ManagementReplayPlan`, `ManagementExtensionStats`; `DeliveryFixture`, `AlvoTestHost.DeliverAsync` | Abstractions / Management; TestHost | 06 | complete here; `ManagementDeliveryAttempt`, `ManagementDeliveryPage`, `ManagementExtensionStat`, `ManagementDeliveryNotFoundException`, `ManagementExtensionNotFoundException`, `ManagementConflictException`, `ManagementExtension.Paused`, `IAlvoEventPump.DeliverAsync`, and nine `IDeliveryStore` members with their records added (§4.8 *HX-06 settled details* (1), (11), (12)). |
| `IAlvoTransactions.TransactAsync<T>`, `IAlvoTransaction` (writes with `AlvoPrecondition?`, batch verbs, `PublishAsync`, `AsSystem()`), `AlvoTransactionOptions` (`Timeout`, `SuppressHostHooks`, `Reason`), `AlvoUnitInProgressException`; `AlvoFilterOperator.StartsWith/EndsWith/Contains` (appended, driver obligation + contract rows) | Abstractions / Data | 07 | **new port**; `IAlvoData` unchanged; `InMemoryAlvoData` does not implement it (documented). `IAlvoTransaction : IAlvoSession`; a non-generic `TransactAsync` default member (§4.5 *HX-07 settled details* (3), (4)). |
| typed writes `CreateAsync<T>`, `UpdateAsync<T>(Guid, IAlvoPatch<T>)`, `ReplaceAsync<T>`, `ExistsAsync<T>`; `IAlvoPatch<T>` | core | 07 | `AlvoPatch.For<T>()` added; the writes are `IAlvoSession` extensions returning `T?`, and `ExistsAsync<T>` takes a predicate (§4.5 *HX-07 settled details* (19)). |
| `AlvoCaller`, `WithAlvoCaller()`, `MapAlvoCommand`, `MapAlvoCommand<TEntity>`, `AlvoCommandContext`, `AddCommand<T>`, `IAlvoCommand<TInput>`, `IAlvoCommands`, `MapAlvoWebhookReceiver`, `AlvoWebhookReceiverOptions`, `AlvoEndpointMetadata`, `AlvoEndpointKind`, `ForEntity` | core | 08 | |
| `AlvoWriteOperation`; `AlvoHookOptions` (moved from 03 — it ships with its first user, *HX-03 settled details* (13)) | Abstractions; core / `MMLib.Alvo.Extensions` | 09 | X19 |
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
  fan-out first; `DeliveryDispatcher` (pools, leases, X5, dead-letter, lanes, `subscriber IN`) (the handler pool; the job pool
  arrives with its first job kind, HX-13);
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
