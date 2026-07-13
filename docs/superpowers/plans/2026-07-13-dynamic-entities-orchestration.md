# Dynamic Entities — Orchestration Gap — Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Make the already-specified "dynamic / user-defined entities" mode visible in the orchestration layer (`docs/PLAN.md` + three `alvo-*` skills) so its guardrails fire before an agent builds a physical-table-only data layer.

**Architecture:** Docs-and-skills only. No product code, no new project, no phase renumbering. Propagate an existing spec concept (analysis §2.1, spec §2.1/§2.14) into (1) the PLAN.md phase map + invariants and (2) the three skills that already activate for the areas the concept touches — architecture, security core, schema testing.

**Tech Stack:** Markdown only. Verification via `grep` and `scripts/check-brief-freshness`; pre-PR review via the read-only `alvo-plan-guard` subagent.

## Global Constraints

- **Never touch** `docs/product/alvo-specifikacia.md`, `docs/product/baas-analyza.md`, or `docs/design-brief.en.md` — the concept is already correct there and the brief is generated + hash-gated (editing it desyncs the freshness gate).
- **Never merge or push to `main`.** Branch → PR → a human merges.
- **No product code.** F1 is "quality before code"; projects are empty.
- All prose is **English**, matching existing specs/skills.
- Spec of record: `docs/superpowers/specs/2026-07-13-dynamic-entities-orchestration-design.md`.
- Branch: `docs/dynamic-entities-orchestration`.

---

### Task 1: `docs/PLAN.md` — name dynamic entities in the phase map and invariants

**Files:**
- Modify: `docs/PLAN.md` (F2 line ~58, F7 line ~71, §4 invariants ~84)

**Interfaces:**
- Consumes: nothing.
- Produces: the two-driver invariant that Tasks 2–4 reference by name.

- [ ] **Step 1: Edit the F2 line.** Replace:

```markdown
- [ ] **F2 — Schema foundation** — the schema is the source of truth;
  specify it and work out how to test against it.
```

with:

```markdown
- [ ] **F2 — Schema foundation** — the schema is the source of truth;
  specify it and work out how to test against it. The entity model is
  **one model, two drivers** (physical introspection + dynamic metadata)
  from the start — F2 must not bake in a physical-table-only assumption,
  even though the dynamic *store* itself lands in F7.
```

- [ ] **Step 2: Edit the F7 line.** Replace:

```markdown
- [ ] **F7 — Further components** — by value, gradually, contract tests
  first. ([milestone #8](https://github.com/Burgyn/MMLib.Alvo/milestone/8))
```

with:

```markdown
- [ ] **F7 — Further components** — by value, gradually, contract tests
  first; includes **dynamic (metadata-driven) entities** — the shared
  `entity_records` store that lets ERP end-users create their own record
  types at runtime without a table per entity (spec §2.1).
  ([milestone #8](https://github.com/Burgyn/MMLib.Alvo/milestone/8))
```

- [ ] **Step 3: Add the invariant.** In `## 4. Key invariants that must not break`, immediately after the `**Descriptor ≠ infra config**` bullet, insert:

```markdown
- **Schema registry = one model, two drivers** — a virtual
  (metadata-driven) entity must be indistinguishable from a physical one to
  the Data API, rule engine, realtime, and automation. Never bake a
  physical-table assumption into the entity model; all dynamic entities of
  all tenants share one partitioned `entity_records` table, never a table
  per entity (spec §2.1).
```

- [ ] **Step 4: Verify.** Run:

```bash
grep -n "one model, two drivers\|entity_records\|metadata-driven" docs/PLAN.md
```

Expected: three regions match (F2 line, F7 line, the §4 invariant).

- [ ] **Step 5: Commit.**

```bash
git add docs/PLAN.md
git commit -m "docs(plan): name dynamic entities in F2/F7 + two-driver invariant"
```

---

### Task 2: `alvo-architecture-rules` — the two-driver schema registry contract

**Files:**
- Modify: `.claude/skills/alvo-architecture-rules/SKILL.md` (insert a section between `## Two sources of truth, one format` and `## The computed / rollup / hook ladder`)

**Interfaces:**
- Consumes: the PLAN.md invariant from Task 1 (same wording anchor: "one model, two drivers").
- Produces: the architectural rule Task 3/4 defer to for the non-testing, non-security aspects.

- [ ] **Step 1: Insert the section.** After the "Two sources of truth, one format" section ends (the line ""one format, two sources of truth."") and before `## The computed / rollup / hook ladder`, insert:

```markdown
## Schema registry: one model, two drivers (physical + dynamic)

The schema registry exposes one entity model (`TableMeta`/`ColumnMeta`) from
two drivers: **physical** (introspection of real DB tables) and **dynamic**
(the metadata tables `entity_definitions` / `field_definitions`). Everything
above the registry — Data API, rule engine, realtime, automation — must work
**identically** over a virtual and a physical entity and must never branch on
which driver produced the model. If any code asks "is this a real table?", the
abstraction has leaked and the ERP embedding story breaks.

Dynamic entities are a *distinct data-layer mode*, not physical tables created
on the fly. When an ERP end-user creates an *evidencia* ("evidenciu vozidiel")
at runtime, that is a pure metadata INSERT — no DDL, no lock — and every
tenant's every dynamic entity shares **one partitioned `entity_records` table**
(`tenant_id`, `entity_definition_id`, `data JSONB`). A physical table per
user-defined entity is the specific anti-pattern this mode exists to avoid: N
tenants × M entities would bloat the catalog and degrade the planner. Spec
§2.1; enabled via `.EnableDynamicEntities()` (spec §2.14).

Consequences to hold when designing anything schema-shaped: the entity model
must be expressible *without* a physical table (don't design a physical-only
`TableMeta`); typed C# codegen applies to physical entities only (dynamic ones
are consumed via weakly-typed JSON/dictionary access — two data-access modes,
stated openly, not hidden); and "materialization" — promoting a hot dynamic
entity to a real table — must preserve the public API contract.
```

- [ ] **Step 2: Verify.** Run:

```bash
grep -n "two drivers\|entity_records\|EnableDynamicEntities" .claude/skills/alvo-architecture-rules/SKILL.md
```

Expected: the new section's heading and body lines match.

- [ ] **Step 3: Commit.**

```bash
git add .claude/skills/alvo-architecture-rules/SKILL.md
git commit -m "docs(skills): add two-driver schema-registry rule to alvo-architecture-rules"
```

---

### Task 3: `alvo-security-core-review` — shared-table tenancy checklist item

**Files:**
- Modify: `.claude/skills/alvo-security-core-review/SKILL.md` (add a checklist item after "Cross-tenant isolation", before "Default-deny")

**Interfaces:**
- Consumes: the two-driver rule from Task 2 (references the shared `entity_records` table).
- Produces: nothing downstream.

- [ ] **Step 1: Insert the checklist item.** In `## Checklist`, immediately after the `**Cross-tenant isolation.**` bullet and before the `**Default-deny.**` bullet, insert:

```markdown
- **Dynamic entities share one table — isolation rests entirely on the
  predicate.** All tenants' dynamic entities live in one partitioned
  `entity_records` table (`tenant_id`, `entity_definition_id`, `data JSONB`) —
  there is **no** per-entity or per-tenant *physical* table boundary to fall
  back on. The `tenant_id` (+ `entity_definition_id`) filter in the SQL
  `WHERE`, enforced inside the data port, is the *only* thing separating tenant
  X's records from tenant Y's. And because there is no DB constraint on `data`,
  the application-layer validation against `field_definitions` is the *only*
  validation layer (spec §2.1) — the fail-fast/type/required guarantees must
  hold there, not just for physical columns. Acceptance bar: the **same**
  adversarial + policy test suite must pass **identically** over a physical and
  a virtual entity — run the two-user and two-tenant tests against a dynamic
  entity too, not only a physical one.
```

- [ ] **Step 2: Verify.** Run:

```bash
grep -n "share one table\|entity_records\|field_definitions" .claude/skills/alvo-security-core-review/SKILL.md
```

Expected: the new bullet's lines match.

- [ ] **Step 3: Commit.**

```bash
git add .claude/skills/alvo-security-core-review/SKILL.md
git commit -m "docs(skills): add shared-table tenancy check to alvo-security-core-review"
```

---

### Task 4: `alvo-schema-testing` — mark the dynamic-entity boundary

**Files:**
- Modify: `.claude/skills/alvo-schema-testing/SKILL.md` (append a section after "## Why these four and not fewer")

**Interfaces:**
- Consumes: references `alvo-security-core-review` (Task 3) for the parity guarantee.
- Produces: nothing downstream.

- [ ] **Step 1: Append the section.** After the final paragraph of `## Why these four and not fewer`, add:

```markdown
## Dynamic entities: where they do and don't fit these four

Dynamic (metadata-driven) entities are created by end-users at *runtime* and
live in metadata tables (`entity_definitions` / `field_definitions`), **not**
in the descriptor — so their per-entity shape is not what these four test types
validate. Two things about them do land in scope, and one deliberately does
not:

- **In scope — descriptor touchpoints.** The descriptor schema carries
  dynamic-entity configuration, e.g. an index over a JSON path
  (`alvo-descriptor.schema.json`: "index … for dynamic entities = generated
  column + index over the JSON path"). An example descriptor exercising that
  belongs in the examples suite (type 2) and its generated artifacts in the
  snapshot suite (type 4).
- **Out of scope — the parity guarantee.** "The same adversarial and policy
  test suite passes identically over a physical and a virtual entity" (spec
  §2.1 acceptance criteria) is a *data-layer / security-core* test, verified per
  `alvo-security-core-review` — **not** a descriptor-schema test. Named here
  only so runtime-entity validation isn't assumed to live here and fall through
  the crack between the two skills.
```

- [ ] **Step 2: Verify.** Run:

```bash
grep -n "Dynamic entities\|entity_definitions\|parity guarantee" .claude/skills/alvo-schema-testing/SKILL.md
```

Expected: the new section matches.

- [ ] **Step 3: Commit.**

```bash
git add .claude/skills/alvo-schema-testing/SKILL.md
git commit -m "docs(skills): mark dynamic-entity testing boundary in alvo-schema-testing"
```

---

### Task 5: Pre-PR verification, plan-guard, and PR

**Files:** none modified (verification + PR).

- [ ] **Step 1: Confirm skills are no longer silent.** Run:

```bash
grep -rniE "dynamic|entity_records|schema-at-runtime|metadata-driven" .claude/skills/ | wc -l
```

Expected: non-zero (was `0` before this branch).

- [ ] **Step 2: Confirm the brief-freshness gate is untouched.** Run:

```bash
git diff --name-only main... | grep -E "docs/product/|docs/design-brief" || echo "clean: no spec/analysis/brief changes"
scripts/check-brief-freshness
```

Expected: "clean: …" printed, and the freshness script exits 0.

- [ ] **Step 3: Dispatch `alvo-plan-guard`** (read-only) on the branch diff. Expected verdict: no drift from PLAN.md, no violated §0 principle (the change *reduces* drift). Address any finding before opening the PR.

- [ ] **Step 4: Push and open the PR** against `main`. PR body: link the spec, summarize the gap and the four edits, note "no product code / spec untouched / brief untouched," and record the Follow-up (optional F7 issue). Do **not** merge.

---

## Self-Review

**Spec coverage:** every "The change" item in the spec maps to a task — PLAN.md → Task 1; alvo-architecture-rules → Task 2; alvo-security-core-review → Task 3; alvo-schema-testing → Task 4; the spec's "Verification" section → Task 5. Scope/YAGNI exclusions require no task by definition. No gaps.

**Placeholder scan:** no TBD/TODO/"handle edge cases"; every edit shows its exact final text and an exact verify command. Clean.

**Type consistency:** the anchor phrase "one model, two drivers" and the table/column names (`entity_definitions`, `field_definitions`, `entity_records`, `tenant_id`, `entity_definition_id`, `data JSONB`) are used identically across Tasks 1–4 and match spec §2.1 wording. Consistent.
