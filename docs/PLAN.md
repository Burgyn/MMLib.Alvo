# PLAN — the coarse master plan

> Map of the country, not an itinerary. This is the long-lived "where Alvo is
> heading" — it does not compete with a Superpowers plan (a short-lived
> itinerary for one trip: one issue, one PR). PLAN.md points to GitHub issues;
> a Superpowers plan implements one line of this file. Brainstorming reads
> this file first and gets the decomposition ready-made instead of inventing
> one. Neither overwrites the other.
>
> How, not where: see `docs/product/alvo-specifikacia.md` (spec) and
> `docs/design-brief.en.md` (compressed EN context). This file stays coarse
> on purpose — it must never grow to the point of eating an agent's context.

## 1. What this is

- **PLAN.md** = the map: phases, invariants, target shape. Changes rarely,
  survives across many issues.
- **A Superpowers plan** = the itinerary: how to implement *one* line of the
  phase map, for one issue, discarded once merged.
- Relationship: PLAN.md → GitHub issue → Superpowers plan → PR. Nothing here
  duplicates issue detail or spec detail — it links out instead.

## 2. Target end-state (from analysis)

The final Alvo, ultra-coarse — so no local shortcut breaks the whole:

- Full BaaS surface: auto-generated API/CRUD over a schema, a rule engine
  (row + field level authorization), an event backbone with automation on
  top, auth, RBAC, realtime, storage, multi-tenancy, dynamic (metadata-driven)
  entities, audit.
- Admin dashboard (Blazor) with rules/automation builder and an AI agent,
  built on the same Management API as the CLI.
- An optional MCP adapter over that same Management API — never a separate
  config path.
- The **project descriptor** is the one unified artifact: Docker mount = CLI
  apply = Management API = `FromDescriptor()` (embedded) = admin UI export —
  one format, several doors to the same result.
- Two distributions of one codebase: standalone (Docker) and embedded
  (NuGet), with a documented upgrade path between them.
- A provider model everywhere infrastructure is touched (DB, secrets,
  storage, cache, email, identity, AI, functions) — the core never binds to
  a concrete provider.
- Deployment story via .NET Aspire, targeting Azure and Kubernetes.

Where, not how — see the design brief for the reasoning behind each bullet.

## 3. Phase map F0–F7

`← YOU ARE HERE` sits on **F4**. Each phase = one GitHub milestone; issues
are numbered independently of the plan's own bracketed `[N]` step numbers
(see `docs/superpowers/` for the distinction).

- [x] **F0 — Skeleton** — have something to build on; deliberately small.
  ([milestone #1](https://github.com/Burgyn/MMLib.Alvo/milestone/1))
- [x] **F1 — Quality before code** — set up all gates on empty projects, so
  every subsequent commit passes through them.
  ([milestone #2](https://github.com/Burgyn/MMLib.Alvo/milestone/2))
- [x] **F2 — Schema foundation** — the schema is the source of truth;
  specify it and work out how to test against it. The entity model is
  **one model, two drivers** (physical introspection + dynamic metadata)
  from the start — F2 must not bake in a physical-table-only assumption,
  even though the dynamic *store* itself lands in F7.
  ([milestone #3](https://github.com/Burgyn/MMLib.Alvo/milestone/3))
- [x] **F3 — Vertical slice (CRUD)** — the smallest thing that actually
  works: project → table → CRUD API + validations.
  ([milestone #4](https://github.com/Burgyn/MMLib.Alvo/milestone/4))
- [ ] **F4 — Demo from the start** — proof of intent + a testing surface, in
  parallel with F3, not after it.
  ← YOU ARE HERE ([milestone #5](https://github.com/Burgyn/MMLib.Alvo/milestone/5))
- [ ] **F5 — Admin mode** — dashboard, rules/automation builder, AI agent.
  ([milestone #6](https://github.com/Burgyn/MMLib.Alvo/milestone/6))
- [ ] **F6 — v0.1** — documentation, logo, release.
  ([milestone #7](https://github.com/Burgyn/MMLib.Alvo/milestone/7))
- [ ] **F7 — Further components** — by value, gradually, contract tests
  first; includes **dynamic (metadata-driven) entities** — the shared
  `entity_records` store that lets ERP end-users create their own record
  types at runtime without a table per entity (spec §2.1).
  ([milestone #8](https://github.com/Burgyn/MMLib.Alvo/milestone/8))

## 3a. What actually closes F4

*Audited against the code on 2026-09-06, not against issue titles — most of
F4's demo layer had already landed as a by-product of other PRs and nobody
had closed the issues.*

F4's point is **proof of intent plus a testing surface**, not features. Read
that way, most of it is done and the milestone's open count is misleading:
of the twenty issues still open, only two are what the phase is actually
about.

**Done, and verifiable by running it:**

| | |
|---|---|
| demo descriptors | five in `examples/` (`vehicle-registry`, `field-service`, `complex-crm`, `simple-tasks`) plus six in `examples/_negative/` that must be *refused*, all validated by `ExamplesTests` |
| standalone run | `src/MMLib.Alvo.Host/Dockerfile`, two compose stacks (8080 vehicle-registry, 8081 field-service), no credential shipped in the image, `up --wait` gated on `/health/ready` |
| playground | `playground/run` — glob-based projects, `--pg`, `--test`, `--down`; two projects with their own suites, deliberately in no ring |
| E2E | `test/teapie-field-service/`, 12 collections, 404 assertions, a **required check on every PR** with a JUnit artifact |
| the Data API itself | reads, writes, batch, idempotency, preconditions, projections, paging, create-or-replace |

**What remains — two things, in this order:**

1. **[#26] Vacuum + API invariant tests.** The one literal placeholder left:
   `scripts/test-ring2:51` still prints `placeholder: API invariant + Vacuum`.
   Two halves — a contract lint over the generated document, and N *generated*
   descriptors asserting default-deny, idempotency and CRUD shape at runtime.
   **This is the higher-value half of what is left**, because it is the only
   thing that tests the claim a metadata-driven framework actually makes: that
   the rules hold *across descriptors*, not for the one demo they were written
   against. Every gate today measures one fixture.
2. **[#24] An embedded-run sample.** Nothing in the repo demonstrates
   `AddAlvo()` / `IAlvoBuilder`, though `docs/architecture/extensibility.md`
   documents the seam and `AddAlvoIntegrationTests` exercises it. Embedded is
   one of the two declared distribution modes and it has no readable example.

**One debt stays in F4 and should not wait its turn: [#191]** — the Data API
requires no `Content-Type`, which is a CSRF vector in an embedded host that
authenticates by cookie. It is a security issue, and the embedded sample above
is exactly the shape that would ship it. Everything else that was parked in F4
has moved; the rule is below.

### The triage rule, and where the backlog went

F4 had drifted into "finish the Data API", and **45 further issues carried no
milestone at all** — filed as follow-ups during a PR and never placed. All of
it is now sorted by one question, applied on 2026-09-06:

> *Is this a debt on something already shipped, or a capability not yet
> earned?*

- **Debt on shipped code, plus the health of the gates → F6 (v0.1).** You do
  not release with a known hole, a lying gate, or a public API whose prose has
  four authorities. This is the whole F3 follow-up set (`#79`–`#93`, `#101`),
  the per-engine data-layer gaps (`#87`, `#88`, `#92`, `#161`, `#162`, `#175`,
  `#178`), the correctness and disclosure items (`#100`, `#118`, `#122`,
  `#131`, `#134`, `#139`, `#145`, `#146`, `#154`, `#155`, `#183`, `#184`), and
  the mutation/CI gate health (`#98`, `#99`, `#129`, `#142`, `#143`, `#181`).
- **A capability that has to be earned → F7.** Relation embedding, aggregations,
  rate limiting, `field.default`, tenant-resolution strategies, the outbox
  extensions, JSONata, and the create-or-replace follow-ups (`#198`–`#201`).

**Two in F6 deserve naming**, because a milestone label makes them look
ordinary and they are not. **`#142`** — Stryker reports `Killed` for mutants
that survive the suite, so *every 100% score is suspect*: a gate that lies is
worse than no gate. **`#161`** — a scoped `ref` may name a row in another
tenant, because the foreign key does not span `(tenant_id, id)`. That is the
same shape as `#198`, and both are the tenant-isolation seam the composite key
would close.

Counts after the triage: **F4 = 5** (four real, plus `#105` closing with its
PR), F5 = 3, F6 = 45, F7 = 37. Nothing is unfiled. A milestone is one
`gh issue edit` to change and none of this is a commitment to an order.

## 4. Key invariants that must not break

- **Interface-first** — contracts and tests against them before
  implementation.
- **Provider model everywhere** — infrastructure is a swappable port; the
  core never touches a concrete provider directly.
- **Secure-by-default / default-deny** — nothing is reachable without an
  explicit policy.
- **Descriptor ≠ infra config** — the descriptor defines the backend
  (entities, rules, automation); env/secrets define infrastructure. Never
  mixed.
- **Schema registry = one model, two drivers** — a virtual
  (metadata-driven) entity must be indistinguishable from a physical one to
  the Data API, rule engine, realtime, and automation. Never bake a
  physical-table assumption into the entity model; all dynamic entities of
  all tenants share one partitioned `entity_records` table, never a table
  per entity (spec §2.1).
- **MCP = an adapter, not a building block** — sits over the one Management
  API; removing it changes nothing structural.
- **Two sources of truth, one format** — the descriptor can live as a repo
  file (GitOps) or a DB record (dashboard-first), bridged bidirectionally.
- **CEL for conditions, JSONata for transforms** — CEL is safe-by-construction
  and runs in-transaction; JSONata is Turing-complete and **never** runs
  in-transaction. See `docs/architecture/cel.md` for the three profiles, the
  two-valued rendering rule, and every deliberate narrowing from conformant CEL.
  **How the ban is enforced today:** there is no JSONata evaluator at all, so the
  invariant holds *vacuously* and F3 proves it with an **absence** test rather
  than one named as a ban — a raw expression in a `$defs/jsonata` slot is refused
  at apply, by name (#149). Whoever ships an evaluator owes the real ban test,
  and it must be **architectural** (nothing on the in-transaction path can reach
  it), never behavioural. `docs/architecture/events.md` records that obligation
  alongside the rest of the event backbone.
- **Never merge to `main` directly** — the PR is the gate for every layer
  but mutation (Stryker runs post-merge on `main`; see `CLAUDE.md`).
- **The core is one big package** — a package is earned (foreign dependency,
  real swap point, or distinct license policy), not assumed. See
  `docs/architecture/package-boundary.md`.

## 5. Freshness

`← YOU ARE HERE` moves forward as issues finish — one phase at a time, never
skipped ahead speculatively. Upkeep is not manual-only: `alvo-plan-guard`
(issue #8) reads the diff plus this file after a larger change and *proposes*
the marker update; a human/agent still applies it.
