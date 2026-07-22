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

`← YOU ARE HERE` sits on **F3**. Each phase = one GitHub milestone; issues
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
- [ ] **F3 — Vertical slice (CRUD)** — the smallest thing that actually
  works: project → table → CRUD API + validations.
  ← YOU ARE HERE ([milestone #4](https://github.com/Burgyn/MMLib.Alvo/milestone/4))
- [ ] **F4 — Demo from the start** — proof of intent + a testing surface, in
  parallel with F3, not after it.
  ([milestone #5](https://github.com/Burgyn/MMLib.Alvo/milestone/5))
- [ ] **F5 — Admin mode** — dashboard, rules/automation builder, AI agent.
  ([milestone #6](https://github.com/Burgyn/MMLib.Alvo/milestone/6))
- [ ] **F6 — v0.1** — documentation, logo, release.
  ([milestone #7](https://github.com/Burgyn/MMLib.Alvo/milestone/7))
- [ ] **F7 — Further components** — by value, gradually, contract tests
  first; includes **dynamic (metadata-driven) entities** — the shared
  `entity_records` store that lets ERP end-users create their own record
  types at runtime without a table per entity (spec §2.1).
  ([milestone #8](https://github.com/Burgyn/MMLib.Alvo/milestone/8))

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
  in-transaction.
- **Never merge to `main` directly** — the PR is the only full gate.
- **The core is one big package** — a package is earned (foreign dependency,
  real swap point, or distinct license policy), not assumed. See
  `docs/architecture/package-boundary.md`.

## 5. Freshness

`← YOU ARE HERE` moves forward as issues finish — one phase at a time, never
skipped ahead speculatively. Upkeep is not manual-only: `alvo-plan-guard`
(issue #8) reads the diff plus this file after a larger change and *proposes*
the marker update; a human/agent still applies it.
