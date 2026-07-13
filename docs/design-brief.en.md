<!-- GENERATED — do not hand-edit. Regenerate via the alvo-regen-brief skill. -->
<!-- brief-source: docs/product/alvo-specifikacia.md sha256:40f4293f8314ca04c790410b24647279a91f62df0703325b9aa923f8946a2ddc -->
<!-- brief-source: docs/product/baas-analyza.md sha256:d3b8776861f1369381e34207a4843eed7d05d9e90ae942b3519c31571323ae47 -->

# Alvo design brief (compressed)

> Generated, deliberately lossy English compression of the two Slovak sources
> (`alvo-specifikacia.md` = how & in what order; `baas-analyza.md` = what & why).
> Audience: the agent **building** Alvo. Load this as "the whole context in one
> breath." When it disagrees with the sources, the sources win — regenerate.

Alvo is a .NET-native Backend-as-a-Service for the agentic era (`MMLib.Alvo.*`
NuGet family, Apache-2.0 core). One framework, two distributions. The primary
user is a coding agent: describe intent, Alvo carries the whole backend
(auth, data, authorization, events, storage, automation, multi-tenancy) with
production-grade guarantees, not a prototype.

---

## 1. Principles (§0) — these govern every implementation choice

1. **Interface-first.** Contracts first, then tests against them, then
   implementation. Interfaces are the public API — they change most expensively,
   so they are designed first.
2. **Provider model everywhere.** Every infrastructure capability is a *port*
   with swappable adapters (Azure / Kubernetes / on-prem / in-memory for tests).
   The core never touches concrete infrastructure directly.
3. **Engine-agnostic core.** Rule engine, event system and tenancy are
   application-level — they behave identically on SQLite (dev), PostgreSQL and
   Azure SQL (prod). Native DB mechanisms (RLS, WAL, Change Tracking) are
   *optional hardening, never a dependency*.
4. **Agent-first.** The agent is the primary user. Declarative configuration
   (descriptor / schema-as-code, rules-as-code), structured errors (RFC 7807 +
   a fix suggestion), idempotent operations. A backend is built by **generating
   a descriptor against a published JSON Schema** — no extra protocol. **MCP is
   an optional adapter over the Management API**, not a building block. The
   descriptor is a *format, not a place* — one canonical shape, two sources of
   truth (repo file / DB record). **Security-by-path:** compile-time certainty
   (C# codegen + Roslyn) is a bonus of the GitOps/embedded path; dashboard-first
   has no build and relies on runtime JSON-Schema validation + a check at apply.
5. **Secure-by-default.** Nothing is exposed without an explicit policy.
   Default = deny.
6. **One language for conditions, one for transformations — a sharp boundary.**
   - *Conditions/predicates* (authorization rules, hook conditions, automation
     conditions, computed fields) = a **CEL subset** (non-Turing-complete,
     safe-by-construction), with a custom compiler to two backends:
     (a) a parametrized SQL predicate, (b) compiled delegates for in-memory
     evaluation over an event payload. Extensions: `changed(field)`, `old`/`new`,
     `@user`/`@tenant`. Adopt the CEL *spec & syntax* (known to agents), **not**
     an existing library (.NET ports are immature; a SQL backend exists nowhere).
   - *Transformations* (webhook/action payload mapping, `transform`) = **JSONata**
     (AWS Step Functions pattern); `{{...}}` templates are sugar. JSONata is
     Turing-complete, so it **never runs in-transaction** (before-hooks and rules
     use CEL only) and runs only in after-side actions with depth/time limits.
7. **Descriptor format: JSON, the single format.** One published JSON Schema
   (validation, IntelliSense, reliable agent generation). No YAML/JSONC — one
   format, one parser, one truth; export and API both return JSON.
8. **Minimal API, not MVC controllers.** All endpoints (schema-generated and
   custom) use minimal API + `RouteGroupBuilder`. Reasons: consistency with .NET
   10 routing, lower overhead, and an endpoint-as-delegate generates
   programmatically from schema better than a controller via reflection.
9. **Vertical slice architecture inside packages.** Organize by *feature*
   (endpoint + handler + validator + model together), not technical layers.
   Package split (§3/boundary) is the framework's *modular* architecture (public
   API + distribution/license boundary); vertical slice is organization *inside*
   each package — not a replacement for packages. Mediator: **not MediatR**
   (commercial since Apr 2025) — consider direct DI handlers or Wolverine.

---

## 2. Two modes — one codebase, two distributions

Both modes run the same NuGet core; the standalone image is just a pre-built host.

- **Mode 1 — Standalone (Docker image `mmlib/alvo`).** Run it, open the
  dashboard, create a project → a backend. Or drop in a project descriptor
  (JSON) and the container boots a ready, configured backend with no clicking.
  Agents/automation use the CLI / Management API (MCP is an optional adapter over
  it). A **project** is the standalone isolation unit — **one DB per project**
  (dev: one SQLite file per project) — *distinct from multi-tenancy, which lives
  inside a project*. Extensibility here = declarative + **csx scripts (Roslyn)**:
  compile-on-load with content-hash cache, run in an `AssemblyLoadContext`, full
  Roslyn diagnostics returned to the agent. **Trust model is hardcoded:** a
  script is admin-level code — no sandbox; the analyzer against `System.IO`/raw
  SQL is defense-in-depth, not a boundary. UI editing is opt-in
  (`ALVO_SCRIPTS_ALLOW_UI_EDIT`, default off), audited, activated only after
  compile + dry-run. Script before-hooks obey the same budget and no-network rule
  as C# hooks.
- **Mode 2 — Embedded (NuGet in your host).** Pin `MMLib.Alvo` into your own
  ASP.NET Core project. Configure via C#, full extensibility: custom modules,
  custom authorization handlers, hooks, endpoints, providers. The ERP scenario.
- **Mode boundary:** needing compiled modules, custom providers, or full DI is
  the signal to move from Mode 1 to Mode 2 — a clear upgrade path, not a limit.

**Binding cross-mode contracts:**
1. **One code:** the standalone image is a pre-built host (`MMLib.Alvo.Host`)
   over the same NuGet as Mode 2.
2. **The project descriptor is one unified artifact:** Docker mount = CLI apply
   = Management API = `FromDescriptor()` in embedded = export from admin UI. One
   format, four paths, identical result — and the migration path standalone →
   embedded ("carry the file over").
3. **Descriptor ≠ infra config** (see Boundaries).
4. **One Management API:** dashboard and `alvo` CLI are clients of the same API;
   MCP is an optional adapter over it, not a separate path.
5. **No default credentials** in the image — password via env/secret or a
   first-run wizard.

---

## 3. Ports & guarantees (what must exist and hold; signatures are open)

Exact signatures, type names, async shape and port granularity are decided in
brainstorming/plan — this fixes *what each port guarantees*, not how it looks.

**Data ports**
- **Schema registry** — supplies the entity model from two drivers: introspection
  of the physical DB (*physical*) and a metadata table for dynamic entities
  (*dynamic*). *Guarantee:* one model, two drivers, the caller cannot tell them
  apart.
- **Data store** — CRUD + query over entities. *Hard guarantee:* every operation
  receives the caller context (identity, tenant, claims) and **policy is enforced
  inside the port, not around it** — it cannot be bypassed by a direct call.

**Rule engine (the security core)** — compiles CEL conditions into a parametrized
SQL predicate and in-memory delegates. *Hard guarantees:* compilation is
**fail-fast at save** (a nonexistent column errors immediately, not at request
time); authorization goes **into the SQL WHERE, never a post-filter in memory**;
user input is **never interpolated** into SQL (a property test proves it).

**Lifecycle hooks** — before/after per operation, two faces (declarative from the
descriptor + typed C# in the embedded host) through **one pipeline** with the
same semantics. *Hard guarantees:* before = in-transaction, has a time budget,
**no network** (enforced structurally/by analyzer — network actions can't even be
expressed), may `reject` (→ RFC 7807) or `mutate`; after = post-commit from the
outbox, durable, retried, network allowed.

**Event system (the backbone)** — publish/subscribe over changes. *Hard
guarantees:* an event is published **in the same transaction** as the data change
(transactional outbox — no lost and no phantom event: "no change without an
event, no event without a change"); subscribe supports wildcard patterns
(`entity.orders.*`); payload carries `record` + `old_record` + changed-columns
(cheap `changed(field)` conditions). **Bulk operations coalesce:** per-item vs
batch delivery is declared per rule, with a batch event shape (e.g.
`entity.orders.created.batch`) — a 10k-row import must not emit 10k events
(the Directus per-item scaling gap). Everything else (realtime, webhooks,
functions, automation, audit) is a consumer of this one stream.

**Infrastructure ports (provider model)** — each has a default in the core and a
swap point: **secret store** (get + rotation/versioning), **object store**
(put/get/list + presigned/Valet Key + capability declaration), **cache store**
(get/set + tag-based invalidation), **email/sms/push sender**, **identity
provider** (challenge/callback → one unified identity model), **change feed**
(optional CDC hardening; the *primary* realtime source is the in-process outbox),
**telemetry sink** (OTel).
- **AI connection** — swappable LLM provider via one interface
  (`Microsoft.Extensions.AI`), local (Ollama/OpenAI-compatible) or cloud; key via
  the secret store. *Guarantee:* switching provider = changing the connection,
  not the code.
- **Function runtime** — where/how custom logic runs; **two independent axes:**
  isolation (in-process / sidecar / microVM) × execution (sync / queued via
  outbox+bus). *Guarantee:* offloading to a worker is independent of the
  isolation type; untrusted code never runs in-process.
- **Provider capabilities** — providers declare capabilities (e.g. presigned
  upload, transactional outbox); the framework degrades in a controlled way when
  one is missing, never silently drops a feature.

**Package dependency rules (hard, §1.1):** `Abstractions` depends on nothing; the
core depends only on `Abstractions`; **no package depends on a provider of
another port**; lockstep SemVer (everything released together as one version).

---

## 4. Hard invariants / contracts

- **Default = deny.** Nothing is reachable without an explicit policy; a query
  without tenant context fails rather than returning everything.
- **Policy is unbypassable.** Enforced inside the data port; even custom C#
  endpoints read via `IAlvoData` + `AlvoContext`, so they cannot see past policy.
- **Authorization compiles to SQL WHERE**, never an in-memory post-filter; user
  input is never concatenated into SQL (parametrization + operator allow-list,
  analyzer-enforced, property-tested).
- **Rules fail-fast at save** (unknown column caught at save time).
- **Transactional outbox:** event and data change commit together; no lost, no
  phantom events. Proven by a crash test (kill between commit and publish →
  delivered after restart).
- **Before-hooks:** in-transaction, time-budgeted, no network (structurally
  impossible), reject-or-mutate only. **After-hooks/rules:** post-commit,
  durable, **at-least-once** — therefore every action must be idempotent or
  deduped by event id.
- **JSONata never runs in-transaction**; evaluated only after commit with
  depth/time limits.
- **Idempotency:** a repeated PUT yields the same state; a repeated create with an
  `Idempotency-Key` never duplicates.
- **Schema-derived validation in the API layer** (400 + RFC 7807 listing the
  violations) is primary; DB constraints are defense-in-depth only. For dynamic
  entities the app-level check is the *only* validation layer.
- **Multi-tenancy (v0.1 = shared DB + shared schema, row-level):** tenant A never
  reads/writes tenant B across REST, realtime, storage, cache, aggregations and
  automation; deleting a tenant removes 100% of its data. Isolation is enforced
  at the lowest layer (the same compiled predicate attached to every query), not
  by app-code checks.
- **Loop protection:** provenance depth carried on each event, capped at ~N
  (default ~5) + cycle detection + alert (not a blanket ban on chaining).
- **Webhook delivery as a product:** at-least-once with backoff+jitter, DLQ +
  redelivery, HMAC-SHA256 over timestamp+payload (Standard Webhooks headers),
  zero-downtime secret rotation, SSRF protection (block private ranges), 2xx =
  success. Envelope = CloudEvents.
- **No config drift:** everything clickable (including AI-proposed) is exportable
  as code (migration/descriptor); the UI and the agent are code editors, not a
  parallel config store.
- **AI safety:** the agent proposes, a human confirms via diff; destructive ops
  need explicit approval; the agent never bypasses policy or audit.
- **Runtime (dashboard-first) descriptor path:** append-only versioning in the DB
  (audit + rollback, the git substitute), rollback via a generated reverse
  migration (a guardrail must catch DROP because the data is already gone), and
  optimistic locking on the descriptor (two admins = conflict, not a git merge).
- **Destructive schema changes** (DROP / type change) need an explicit flag +
  dry-run on both the code-first and runtime paths — stricter at runtime (live
  data).
- **Public API approval gate:** a breaking change to `Abstractions` breaks a test
  and forces a conscious major bump. Architecture/dependency rules are enforced
  as tests (NetArchTest), not code review.
- **Secrets never appear** in logs, telemetry, env dumps or git.
- **SQLite single-writer is a hard cap** (internally serialized write-queue) —
  fine for dev/solo, explicitly documented as not a production path for
  concurrent writes.

---

## 5. Key decisions + why

- **Relational model, full stop** (not document). The target audience (.NET
  devs) thinks relationally and knows EF Core/SQL; joins/constraints/transactions
  are indispensable for business apps; SQL is heavily represented in agent
  training data — a bespoke document query language would be a foreign language.
- **Dev SQLite, production PostgreSQL *and* Azure SQL / SQL Server.** SQLite =
  zero-friction try-it. Postgres = default recommendation (most native
  capabilities). SQL Server is included because a large part of the .NET target
  is a SQL Server shop and a Postgres-only choice would exclude part of the
  market. **Not a least-common-denominator trap:** the core (rules, events,
  tenancy) is app-side and engine-agnostic; only *storage and its native bonuses*
  are abstracted, not authorization and realtime.
- **App-side rules are as safe as native RLS** *because the only path to data is
  through the framework.* This is the trust model. The **escape hatch** (direct
  SQL, a shared DB, `pg_dump` mentality) consciously breaks that premise:
  out-of-band writes bypass the rule engine and emit no events (realtime silent,
  automation skipped, audit gap). Mitigations: (1) enable native Postgres RLS as
  *extra* defense-in-depth; (2) optional `IChangeFeed` (WAL / Change Tracking) to
  backfill events for out-of-band changes; (3) docs must state which guarantees
  hold only when access goes through the framework.
- **In-process outbox for realtime, not external WAL reading** — lower latency,
  engine-agnostic, identical on SQLite/Postgres/SQL Server. WAL/Change Tracking
  is optional hardening only.
- **CEL for conditions + JSONata for transformations** (principle 6); adopt the
  CEL spec, not a library.
- **MCP is an optional adapter over the one Management API**, never a config path
  or a building block. Config stands on the descriptor (the agent generates a
  file); MCP just gives an external agent parity (schema, migrations, data, logs,
  codegen) over a *running* instance — nice-to-have, not a precondition for the
  demo. Minimal set: `get_schema`, `apply_schema_change`, `list_entities`,
  `query`, `upsert_rule`, `get_logs`.
- **Dynamic entities = a metadata-driven generic store** (a fixed, small number
  of physical tables; schema is data, not DDL) — the Salesforce/Airtable/Dataverse
  pattern. A physical table per entity would cause catalog bloat at N clients × M
  evidences and fragile runtime DDL. A *second schema-registry driver* reads
  `entity_definitions`/`field_definitions` and yields the *same* abstract model,
  so Data API, rule engine, realtime and automation work identically over virtual
  and real entities. Caveat: typed C# codegen applies only to design/physical
  entities; dynamic entities are consumed via weakly-typed (JSON/dictionary) access.
- **Multi-tenancy v0.1 = shared DB + shared schema only** — it is the same
  machinery as row-level authorization, so nearly free. Ports (tenant resolution,
  tenant-aware data access) are designed so DB-per-tenant is a *later strategy
  addition, not a rewrite*. Schema-per-tenant is dropped (catalog bloat, breaks
  pooling — the weakest of the three).
- **M2M v0.1 = PAT only** (scoped, expiring, revocable, last-used). OAuth 2.1
  client credentials come later into the prepared token/scope port. **Scopes are
  mandatory even for PAT** — a PAT without scopes is the all-powerful
  `service_role` anti-pattern renamed.
- **OpenAPI v0.1 = publish OpenAPI 3.1 + Scalar docs** (near-free from the schema).
  SDK codegen (Kiota/NSwag) and a first-party `MMLib.Alvo.Client` are on-demand —
  an integrator generates its own client from the published OpenAPI. API
  versioning, dev portal and a dedicated sandbox are out of scope for now.
- **Testing stack (binding):** runtime = **Microsoft.Testing.Platform (MTP)**,
  not VSTest; framework = **xUnit v3**; Shouldly (not FluentAssertions v8+),
  NSubstitute, CsCheck (property-based), Verify (snapshot), PublicApiGenerator
  (API approval), NetArchTest, Testcontainers (3-engine matrix), Vacuum (OpenAPI
  lint), Stryker.NET (mutation, security-core only), Playwright (admin E2E),
  TeaPie (API E2E). **The PR is the only full gate** — no nightly, no post-merge;
  everything (including mutation + e2e) runs in the PR; `dotnet-affected` only
  scopes integration tests. Direct push to `main` is forbidden.
- **Mediator ≠ MediatR** (commercial). Consider Wolverine (MIT: outbox +
  in-process mediator). Avoid MassTransit v9, ImageSharp, Duende, AutoMapper,
  FluentAssertions v8+ (licensing).
- **License: Apache-2.0 core, forever, from the first commit.** Open-core funding:
  only enterprise add-ons (`Alvo.Enterprise.*`) and optional hosting are
  commercial. Later commercialization = *adding* add-ons, never relicensing the
  core. CLA + "Alvo" trademark are the structural safeguards.
- **Package boundary: ~10 packages for v0.1, not 30+.** A package is earned only
  if it (a) pulls a heavy/foreign dependency most users don't want, (b) is a real
  swap point someone actually replaces, or (c) has a different
  distribution/license policy. Everything else is a namespace/vertical slice
  inside the core. Splitting later is cheap; merging back is a breaking change.
- **Admin = Blazor Web App (.NET 10)**, server-interactive default, with a
  *custom design system* (not the default Bootstrap/Blazor look), mobile-first.
  AI in the dashboard = Microsoft Agent Framework + `Microsoft.Extensions.AI`;
  the dashboard works even with no AI configured.

---

## 6. Boundaries (the lines that must not blur)

- **Descriptor ≠ infra config.** The descriptor *defines the backend* — entities,
  rules, automation, auth settings, admin access mapping (via CEL) and branding —
  and is versioned in Git. Env/secrets *define infrastructure* — bootstrap admin
  credentials, admin-portal enable/path, IP allowlist, `ALVO_SCRIPTS_ALLOW_UI_EDIT`,
  connection strings, provider selection. Never mix them: access rules stay
  versioned, credentials never enter the descriptor.
- **MCP is an adapter, not a special case.** It is an optional adapter over the
  *one* Management API for external agents against a running instance — the same
  API the dashboard and CLI use. It is never a parallel config path or a
  building block; removing it changes nothing structural.
- **Two sources of truth (one format).** The descriptor is one canonical *format*
  but can *live* in two places: a **file in the repo** (GitOps — source of truth
  is Git; the UI/agent edits the same files) or a **record in the running
  instance's DB** (dashboard-first — written into the schema registry, no file).
  The bridge is a bidirectional export/import. It is *not* "everything is files";
  it is "one format, two sources of truth." Only the GitOps path gets git's audit
  & rollback for free — the runtime path must supply its own (append-only
  versioning, reverse-migration rollback, optimistic locking).
- **Computed / rollup / hook ladder (where a derived value belongs).**
  - **computed** = pure arithmetic/expression over fields of the *same row*
    (`total = unit_price * amount`) → a **DB stored generated column**; read-only,
    computed by the database, unbypassable.
  - **rollup** = aggregation across *related records* (`sum(items.line_total)`) →
    **transactionally consistent** (DB trigger or in-transaction recompute), a
    first-class declarative concept, **never a manual hook** (a parent-sum hook is
    a classic race condition).
  - **conditional value at write** ("if `stage == 'won'` set `closed_at`") = a
    **before-hook `mutate`** with a CEL condition, *not* a computed field.
  - **contextual / time-valued logic** (e.g. a VAT rate that depends on country,
    supply type and the date of taxable supply) = a **hook/function**, not an
    expression — it is business logic, not arithmetic.
  - **complex branching / external calls** = an **automation action** (post-commit)
    or a **csx function**.
  - The ladder declarative → hook → action → csx is the single extensibility
    gradient; do not push logic below the rung it belongs on.
- **RBAC ≠ row-level authorization** (two complementary layers, not
  alternatives). RBAC (roles/teams/permissions) says "Ján is an editor" — coarse.
  The rule engine says "an editor may change only invoices of his department" —
  fine-grained. RBAC (`@user.role`, `@user.teams`, custom claims) *feeds* the
  row-level rules; without the second layer RBAC is all-or-nothing.

---

## 7. Phase map

```
F1 Interfaces + packages → F2 README → F3 Contract tests → F4 CRUD core → F5 Admin → F6 Demo (descriptor+agent; MCP optional) → F7+ components
                               │              │
                               └ fakes (a product)   └ X.1 Docker (from F4) — X.2 Aspire (from F6)
```

- **F1 — Interfaces + package split.** Deliver *approved contracts and
  boundaries, not finished code*: the ports and their guarantees (§3), the
  package boundary rule (§6), and the **descriptor JSON Schema**
  (`alvo-descriptor.schema.json`, published at `alvo.dev/schema/v1/project.json`).
  Schema: draft 2020-12, `additionalProperties:false` everywhere (agents fail
  loudly, not silently), a description on every field, conditional requirements
  per field type (enum→values, ref→entity, decimal→precision/scale), before-hooks
  structurally limited to reject/mutate. *DoD:* ports/guarantees documented &
  agreed, boundary clear, schema valid — **not** finished signatures.
- **F2 — README + docs.** A README that sells and explains the product before any
  code exists (it doubles as a DX acceptance test — if it's hard to write, the
  design is wrong). `docs/` skeleton + `llms.txt` from day one; Apache-2.0 in the
  repo from the first commit.
- **F3 — Contract tests before implementation.** A runnable suite that defines
  behavior first (`MMLib.Alvo.Testing`). Contract tests per port (the in-memory
  fakes are the first implementation *and* a shipped product). CEL→SQL
  property/golden tests, architecture tests, Testcontainers integration, snapshot
  (Verify), public-API approval. The **adversarial suite** (two-user, two-tenant,
  default-deny) exists here as runnable — red/skipped — setting the bar for F4.
- **F4 — CRUD core.** The first real vertical slice: **Data API + rule engine +
  events** on SQLite and PostgreSQL (SQL Server enabled right after the green
  Postgres run, still within F4). Includes: REST CRUD (filters with allow-listed
  operators, keyset pagination, RFC 7807, Idempotency-Key), rule engine (rules→SQL
  predicates, field-level hidden/read-only, default-deny), the event pipeline
  (transactional outbox), **minimal automation** (ECA over outbox events + cron
  schedule + a basic email sender — enough to build "STK expires in 30 days →
  email"), the lifecycle-hooks pipeline (declarative + C# faces, one pipeline),
  minimal dev auth (API/service key), schema-derived validation, computed +
  rollup fields, and migrations via **one declarative-diff engine with two
  desired-state sources** (repo file / DB record). *DoD:* adversarial suite green
  on all three engines; outbox crash test passes; p95 latencies measured.
- **F5 — Admin mode.** A visual layer over F4 in Blazor (`MMLib.Alvo.Admin`):
  first-run wizard, project management, descriptor export/import, a schema editor
  that *generates migrations/descriptor* (no direct ALTER), data browser with an
  audit of admin actions, rules/automation builder + **policy simulator**, csx
  editor with diagnostics, webhook delivery log + redelivery, RBAC, and the AI
  agent (operating over the same Management API). *DoD:* everything clickable is
  exportable as code (no drift); the policy simulator answers identically to
  production; usable at 375px; fails a visual audit if it looks like a default
  template; key flows covered by Playwright E2E.
- **F6 — Demo (the milestone "public v0.1").** Proof of intent: an agent builds a
  backend from one prompt. Demo app "Evidencia vozidiel" (vehicles/owners/STK)
  with schema + rules + automation, generated as a **descriptor against the JSON
  Schema** → `alvo apply` → working backend, no hand-written code. Same backend as
  a mounted descriptor (Mode 1). Optional `MMLib.Alvo.Mcp` adapter gives an
  external agent the same power over a running instance. TeaPie E2E over the live
  demo API.
- **F7+ — Further components, ordered by value.** Most are vertical slices inside
  the core; a **[package]** is earned only by a foreign dependency:
  7.1 automation (extend F4: HMAC/Standard Webhooks, retries+DLQ, redelivery UI,
  inbound webhooks), 7.2 scripting **[package]** (Roslyn), 7.3 functions runtime
  (slice + **[package]** microVM executor), 7.4 full auth (email/password, magic
  links, OAuth, refresh rotation + reuse detection, anonymous upgrade, OIDC),
  7.5 RBAC, 7.6 realtime (SignalR), 7.7 storage (S3/Blob provider packages on
  demand), 7.8 tenancy, 7.9 dynamic entities (depends on 7.8), 7.10 audit
  (append-only, hash chaining, GDPR export/erasure), 7.11 messaging/caching/
  M2M+OpenAPI/client codegen.
- **Cross-cutting (from the start, not the end):** Docker images from F4, .NET
  Aspire from F6; GitHub Actions CI with a 3-engine matrix and MTP `dotnet test`;
  the PR-only full gate; `dotnet-affected` scoping; Vacuum, Stryker.NET and
  Playwright in the pipeline; local test rings; CHANGELOG + SemVer from v0.1.
