# Dynamic Entities — Orchestration Gap — Design

**Date:** 2026-07-13
**Milestone:** F1 — Quality before code (the *work* targets F2/F7 content)
**Issues:** _none yet — see "Follow-up" below_
**Branch:** `docs/dynamic-entities-orchestration`

> **Autonomy note.** The maintainer asked for the full brainstorming → spec →
> plan → PR flow to run to a PR while he was away, and to review the result.
> There was therefore no interactive approval at each section; every choice
> that would normally be a question is recorded below as an **Assumption** he
> can veto on review. Nothing here is merged — a human still merges the PR.

## Goal

Close a **propagation gap**, not add a feature. The "dynamic / user-defined
entities (schema-at-runtime)" mode — where an ERP end-user creates their own
*evidencia* (record type) at runtime, backed by a shared metadata-driven store
rather than a table per entity — is fully specified in the product docs but is
**absent from the orchestration layer** an agent actually reads while working.
This design makes the existing concept visible in `docs/PLAN.md` and in the
three `alvo-*` skills that fire for the areas the concept touches, so the
guardrails trigger *before* an agent builds a physical-table-only data layer
in F2/F3 and silently forecloses the mode.

## The gap, precisely

The concept is **not lost** — it lives, correctly, in the deepest layers:

- `docs/product/baas-analyza.md` **§2.1** (lines 146–169) — the full treatment:
  the ERP-agent scenario verbatim (line 148), the reason per-entity physical
  tables are wrong (catalog bloat, planner/`vacuum` degradation at N tenants ×
  M entities), and the solution — a **metadata-driven generic store** with a
  fixed, small set of physical tables: `entity_definitions`,
  `field_definitions`, and one **shared, partitioned `entity_records`** table
  (`tenant_id`, `entity_definition_id`, `data JSONB`) — line 160.
- `docs/product/alvo-specifikacia.md` **§2.1** (line 131) — the schema registry
  is *one model, two drivers* (physical introspection + dynamic metadata);
  **§2.14** (line 188) — `.EnableDynamicEntities()`; line 333 — for dynamic
  entities the API-layer validation is the *only* validation layer.
- `docs/design-brief.en.md` (117, 262, 269, 421) and `docs/PLAN.md:29` (in the
  "target end-state" bullet).

It **is** missing from the layers that steer an agent mid-task:

1. **`docs/PLAN.md` phase map F0–F7** never names dynamic entities in any phase.
   They appear only in the coarse end-state bullet. F2 (Schema foundation) and
   F3 (CRUD slice) are where an agent designs the entity model — and nothing
   there says the model must accommodate a *second, non-physical* driver. The
   realistic failure: an agent builds a physical-table-only `TableMeta`, and the
   ERP embedding story is quietly foreclosed with no guard firing.
2. **No `alvo-*` skill mentions it.** `grep -ri "dynamic|entity_records|
   schema-at-runtime|metadata-driven" .claude/skills/` returns nothing. The
   three skills that fire for the areas dynamic entities touch — architecture,
   the security core, schema testing — are all silent, so the discipline never
   reaches the agent at the moment it's needed.

## Approaches considered

- **A — Propagate into PLAN.md (invariant + F2 + F7) and the three relevant
  skills; touch no product docs. (CHOSEN.)** Surgical, and it places each piece
  of discipline in the layer that actually fires for it. Cost: the change spans
  four files.
- **B — One new dedicated `alvo-dynamic-entities` skill.** Rejected. Skills
  activate *by area*. A dynamic-entities skill only fires once the agent already
  knows it is in dynamic-entity territory — which is exactly when it needs no
  reminder. The gap is the agent who thinks it is "just building the schema" or
  "just doing tenancy." A cross-cutting concern belongs in the skills that
  *already* fire for those areas, not in a silo.
- **C — Update `docs/PLAN.md` only; leave skills alone.** Rejected as a
  half-fix. PLAN.md is read at task *start* and is deliberately coarse; the
  load-bearing reminders that fire *during* schema/tenancy work are the skills.
  Without the skill edits the original gap reopens the moment an agent is deep
  in F2/F3.

## The change

### 1. `docs/PLAN.md`

- **§3 Phase map — F2 line.** Append: the entity model is *one model, two
  drivers* (physical introspection + dynamic metadata) from the start; F2 must
  not bake in a physical-table-only assumption, even though the dynamic *store*
  itself lands in F7.
- **§3 Phase map — F7 line.** Name dynamic (metadata-driven) entities as an
  explicit F7 component: the shared `entity_records` store that lets ERP
  end-users create their own record types at runtime without per-entity tables
  (spec §2.1).
- **§4 Key invariants — new invariant.** "Schema registry = one model, two
  drivers" — a virtual (metadata-driven) entity must be indistinguishable from a
  physical one to the Data API, rule engine, realtime, and automation; never
  bake a physical-table assumption into the entity model; all dynamic entities
  of all tenants share one partitioned `entity_records` table, never a table per
  entity (spec §2.1).

### 2. `.claude/skills/alvo-architecture-rules/SKILL.md`

New section **"Schema registry: one model, two drivers (physical + dynamic)"**
— the architectural contract: one `TableMeta`/`ColumnMeta` model from two
drivers; everything above the registry must behave identically over virtual and
physical entities and must never branch on which; dynamic entities are a
distinct data-layer *mode* (a shared, partitioned `entity_records` store), not
physical tables created on the fly; typed C# codegen applies to physical
entities only; materialization (promote a hot dynamic entity to a physical
table) must preserve the public API contract.

### 3. `.claude/skills/alvo-security-core-review/SKILL.md`

New checklist item **"Dynamic entities share one table — isolation rests
entirely on the predicate."** All tenants' dynamic entities live in one shared,
partitioned `entity_records` table; there is **no** per-entity/per-tenant
physical boundary to fall back on, so the `tenant_id` (+ `entity_definition_id`)
filter enforced inside the data port is the *only* separation. Because there is
no DB constraint on `data`, the application-layer validation against
`field_definitions` is the *only* validation layer — the fail-fast/type/required
guarantees must hold there too. Acceptance bar: the **same** adversarial +
policy test suite must pass **identically** over a physical and a virtual
entity (spec §2.1) — run the two-user and two-tenant tests against a dynamic
entity, not only a physical one.

### 4. `.claude/skills/alvo-schema-testing/SKILL.md`

Short section marking the **boundary** so runtime-entity validation doesn't fall
through the crack between two skills: dynamic entities are created at runtime
and live in metadata tables, **not** in the descriptor, so their per-entity
shape is not what the four descriptor-schema test types validate. Two things do
land in scope: (a) the descriptor's dynamic-entity **touchpoints** — e.g. index
config over a JSON path (`alvo-descriptor.schema.json`) — belong in the examples
(type 2) and snapshot (type 4) suites; (b) the **parity** guarantee ("same suite
passes identically over physical and virtual") is a data-layer / security-core
test (see `alvo-security-core-review`), not a descriptor-schema test — named
here only so it isn't assumed to live here.

## Scope / YAGNI — explicitly out

- **No product code.** F1 is "quality before code"; the projects are empty and
  the implementation shape is designed at plan time with real code in front of
  us (spec §2.1 is a *zadanie*, not a finished design).
- **No phase renumbering and no new phase.** Dynamic entities fit existing F2
  (design accommodation) + F7 (build). Milestones are fixed on GitHub;
  renumbering would be churn for no gain.
- **Spec, analysis, and the design brief are untouched.** The concept is
  already correct there. The brief is *generated* and hash-gated by
  `check-brief-freshness`; hand-editing it would desync it from its sources —
  the only correct way to change it is `alvo-regen-brief`, which is unnecessary
  here because the sources don't change.
- **No `alvo-dotnet-conventions` change** — packaging/licensing/test-stack/
  code-style are irrelevant to this concept.

## Assumptions (veto candidates)

1. Two-driver registry belongs as a **PLAN.md invariant** (§4), not only as
   phase prose — it is the load-bearing guard, on the same footing as
   interface-first and default-deny.
2. F2 gets a design-accommodation note; the **store implementation is F7**, not
   F2/F3. Rationale: interface-first says don't design a registry that *can't*
   grow a second driver, but YAGNI says don't build the JSONB store before its
   value is due.
3. Enrich the three existing skills rather than create a new one (approach B
   rejected above).
4. No GitHub issue is created inside this PR (see Follow-up). This keeps the
   PR's outward footprint to exactly what was asked — a PR.
5. Spec and skill edits are written in **English**, matching the existing
   specs and skills.

## Follow-up

- **F7 implementation is already tracked** — issue #41 `[36] DynamicEntities`
  (milestone F7, "metadata-driven records, depends on Tenancy #40") is the home
  for the `entity_records` store. This PR does not duplicate it. _(Corrects an
  earlier draft of this section that assumed no F7 issue existed.)_
- **F2 accommodation guard** — a dedicated F2 issue (milestone #3) ensures the
  descriptor-schema finalization (#16) and the testing mechanism (#17) do not
  foreclose the dynamic (second) driver. Cross-linked to #16 / #17 / #41.

## Verification (how we know it worked)

This PR ships no runtime surface, so verification is documentary:

- `grep -ri "dynamic|entity_records|schema-at-runtime|metadata-driven"
  .claude/skills/` now returns hits in the three edited skills (was empty).
- `docs/PLAN.md` names dynamic entities in the F2 line, the F7 line, and a §4
  invariant.
- `scripts/check-brief-freshness` still passes and the pre-commit hook does
  **not** fire — this PR touches neither the spec/analysis nor the brief.
- `alvo-plan-guard` (dispatched pre-PR, read-only) reports no drift from
  PLAN.md and no violated §0 principle — the change *reduces* drift by aligning
  the orchestration layer with the spec.
