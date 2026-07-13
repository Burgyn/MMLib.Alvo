# Agentic Context Skeleton Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Build the repository's agent operating system — the layered context an agent reads (CLAUDE.md, PLAN.md, design-brief.en.md) plus the guards that keep it honest (freshness gate, domain skills, the alvo-plan-guard subagent) — completing all five remaining F0 issues (#4–#8).

**Architecture:** Docs + shell + Claude Code skill/agent definitions. No product C#. A **read path** (context pyramid) feeds the agent; a **verify path** (a deterministic commit-time freshness hook + an intelligent pre-PR subagent + contextual skills) stops drift and staleness. The design-brief is a *generated, lossy, English* compression of the Slovak spec+analysis; its freshness is anchored by source SHA-256 hashes stored in its header.

**Tech Stack:** Markdown, POSIX bash, SHA-256 (`sha256sum`/`shasum`), git hooks (`core.hooksPath`), GitHub Actions, Claude Code skills (`.claude/skills/<name>/SKILL.md`) and subagents (`.claude/agents/<name>.md`).

## Global Constraints

- Branch: `f0-agentic-context-skeleton` (already created). NEVER commit/push to `main`; land via one reviewed PR.
- Design source of truth: `docs/superpowers/specs/2026-07-13-agentic-context-skeleton-design.md`.
- Product spec (SK): `docs/product/alvo-specifikacia.md` (§0 = the 9 principles; "Súhrnná mapa fáz"). Domain analysis (SK): `docs/product/baas-analyza.md`.
- GitHub milestones F0–F7 are canonical for PLAN.md; the spec's "Fáza 1–7" is the "how" detail. **Bracketed `[N]` in issue titles ≠ GitHub issue number** (e.g. `[13]` = issue #17).
- Test stack already in repo (do not change): xUnit v3 (3.2.2) on MTP, NetArchTest.Rules (1.3.2), Shouldly (4.3.0); CPM via `Directory.Packages.props`; net10.0 pinned in `global.json`. `dotnet test` currently passes (1 arch test).
- Licensing bans: no MediatR, no FluentAssertions v8+, use Shouldly.
- Every generated/committed script must be executable (`chmod +x`) and pass `bash -n`.
- Commit trailer on every commit: `Claude-Session: https://claude.ai/code/session_012oedWFLGri4VxM4gp7uyHS`.

## File Structure

**Created:**
- `scripts/test-ring0`, `scripts/test-ring1`, `scripts/test-ring2` — layered local test cadence.
- `scripts/check-brief-freshness` — deterministic brief-vs-sources hash check (reused by hook + CI).
- `.githooks/pre-commit` — blocks a commit that stages a source/brief change with a stale brief.
- `.github/workflows/ci.yml` — add a `brief-freshness` job (belt-and-suspenders).
- `.claude/skills/alvo-regen-brief/SKILL.md` — committed brief generator procedure.
- `.claude/skills/alvo-architecture-rules/SKILL.md`
- `.claude/skills/alvo-security-core-review/SKILL.md`
- `.claude/skills/alvo-schema-testing/SKILL.md`
- `.claude/skills/alvo-dotnet-conventions/SKILL.md`
- `.claude/agents/alvo-plan-guard.md`
- `docs/PLAN.md`
- `docs/design-brief.en.md` (generated)

**Modified:**
- `CLAUDE.md` — rewrite to router; move conventions/style into `alvo-dotnet-conventions`.

**Hash convention (used by check-brief-freshness, the hook, alvo-regen-brief, and the brief header):**
Brief header carries one single-line marker per source:
```
<!-- brief-source: docs/product/alvo-specifikacia.md sha256:<64 hex> -->
<!-- brief-source: docs/product/baas-analyza.md sha256:<64 hex> -->
```
Digest = SHA-256 of the raw file bytes, lowercase hex. Portable helper: use `sha256sum` if present, else `shasum -a 256`.

---

### Task 1: Test ring scripts

**Files:**
- Create: `scripts/test-ring0`, `scripts/test-ring1`, `scripts/test-ring2`

**Interfaces:**
- Produces: `scripts/test-ring0|1|2` — executable, exit 0 today, referenced by CLAUDE.md (Task 8).

- [ ] **Step 1: Write `scripts/test-ring0`**

```bash
#!/usr/bin/env bash
# ring0 — run after every small step (unit + fast contract tests, seconds).
# Today runs the whole suite (it is fast because it is small). When slow
# tests appear, add a fast-only filter here — grow by adding, not rewriting.
set -euo pipefail
echo "[ring0] dotnet test"
dotnet test
echo "[ring0] OK"
```

- [ ] **Step 2: Write `scripts/test-ring1`**

```bash
#!/usr/bin/env bash
# ring1 — run after finishing a slice (ring0 + arch + public-API approval).
# Arch tests already run inside dotnet test today; public-API approval does
# not exist yet — add it here when it lands.
set -euo pipefail
DIR="$(cd "$(dirname "$0")" && pwd)"
"$DIR/test-ring0"
echo "[ring1] placeholder: public-API approval tests — none yet"
echo "[ring1] OK"
```

- [ ] **Step 3: Write `scripts/test-ring2`**

```bash
#!/usr/bin/env bash
# ring2 — run before opening a PR (ring1 + integration (affected) + API
# invariant + Vacuum). None of those exist yet — add them here when they land.
# Full run (+ mutation + e2e) stays in CI on the PR; do not run it locally.
set -euo pipefail
DIR="$(cd "$(dirname "$0")" && pwd)"
"$DIR/test-ring1"
echo "[ring2] placeholder: integration (affected-scoped), API invariant, Vacuum — none yet"
echo "[ring2] OK"
```

- [ ] **Step 4: Make executable**

Run: `chmod +x scripts/test-ring0 scripts/test-ring1 scripts/test-ring2`

- [ ] **Step 5: Verify each ring runs and passes**

Run: `bash -n scripts/test-ring0 scripts/test-ring1 scripts/test-ring2 && scripts/test-ring2`
Expected: ends with `[ring0] OK`, `[ring1] ... OK`, `[ring2] ... OK`, exit 0.

- [ ] **Step 6: Commit**

```bash
git add scripts/test-ring0 scripts/test-ring1 scripts/test-ring2
git commit -m "build: add layered test-ring scripts (#4)

Claude-Session: https://claude.ai/code/session_012oedWFLGri4VxM4gp7uyHS"
```

---

### Task 2: Brief freshness gate (script + hook + CI)

**Files:**
- Create: `scripts/check-brief-freshness`, `.githooks/pre-commit`
- Modify: `.github/workflows/ci.yml`

**Interfaces:**
- Consumes: the hash convention (File Structure section).
- Produces: `scripts/check-brief-freshness` (exit 0 = fresh, non-0 = stale/missing), referenced by the hook, CI, and CLAUDE.md.

- [ ] **Step 1: Write `scripts/check-brief-freshness`**

```bash
#!/usr/bin/env bash
# Deterministic freshness gate: the design-brief must carry SHA-256 markers
# matching its two sources. Exit 0 = fresh; non-zero = stale or missing.
set -euo pipefail

BRIEF="docs/design-brief.en.md"
SOURCES=("docs/product/alvo-specifikacia.md" "docs/product/baas-analyza.md")

sha256_of() {
  if command -v sha256sum >/dev/null 2>&1; then
    sha256sum "$1" | awk '{print $1}'
  else
    shasum -a 256 "$1" | awk '{print $1}'
  fi
}

if [[ ! -f "$BRIEF" ]]; then
  echo "check-brief-freshness: $BRIEF is missing — regenerate via the alvo-regen-brief skill." >&2
  exit 1
fi

stale=0
for src in "${SOURCES[@]}"; do
  if [[ ! -f "$src" ]]; then
    echo "check-brief-freshness: source $src is missing." >&2
    stale=1
    continue
  fi
  actual="$(sha256_of "$src")"
  stored="$(grep -oE "brief-source: ${src//./\\.} sha256:[0-9a-f]{64}" "$BRIEF" | sed 's/.*sha256://' || true)"
  if [[ -z "$stored" ]]; then
    echo "check-brief-freshness: no hash marker for $src in $BRIEF." >&2
    stale=1
  elif [[ "$actual" != "$stored" ]]; then
    echo "check-brief-freshness: $src changed (brief hash is stale)." >&2
    stale=1
  fi
done

if [[ "$stale" -ne 0 ]]; then
  echo "check-brief-freshness: STALE — regenerate $BRIEF via the alvo-regen-brief skill, then re-run." >&2
  exit 1
fi
echo "check-brief-freshness: OK"
```

- [ ] **Step 2: Make executable and syntax-check**

Run: `chmod +x scripts/check-brief-freshness && bash -n scripts/check-brief-freshness`

- [ ] **Step 3: Verify against fixtures (no real brief yet)**

Run:
```bash
# missing brief → fail
scripts/check-brief-freshness; echo "exit=$?"   # expect message + exit=1
# fresh fixture → pass
mkdir -p /tmp/cbf && a=$(shasum -a 256 docs/product/alvo-specifikacia.md | awk '{print $1}') && b=$(shasum -a 256 docs/product/baas-analyza.md | awk '{print $1}') && printf '<!-- brief-source: docs/product/alvo-specifikacia.md sha256:%s -->\n<!-- brief-source: docs/product/baas-analyza.md sha256:%s -->\n' "$a" "$b" > docs/design-brief.en.md && scripts/check-brief-freshness; echo "exit=$?"   # expect OK exit=0
# corrupt one hash → fail
sed -i '' "s/$a/deadbeef/" docs/design-brief.en.md 2>/dev/null || sed -i "s/$a/deadbeef/" docs/design-brief.en.md; scripts/check-brief-freshness; echo "exit=$?"   # expect stale exit=1
rm -f docs/design-brief.en.md
```
Expected: exit=1, exit=0, exit=1 respectively. **Delete the fixture brief afterward** — the real one is produced in Task 4.

- [ ] **Step 4: Write `.githooks/pre-commit`**

```bash
#!/usr/bin/env bash
# Block a commit that stages a source or brief change while the brief is stale.
set -euo pipefail
staged="$(git diff --cached --name-only)"
if echo "$staged" | grep -qE '^docs/product/(alvo-specifikacia|baas-analyza)\.md$|^docs/design-brief\.en\.md$'; then
  if ! scripts/check-brief-freshness; then
    echo "pre-commit: design-brief.en.md is stale vs its sources." >&2
    echo "Regenerate it via the alvo-regen-brief skill, then re-commit." >&2
    exit 1
  fi
fi
```

- [ ] **Step 5: Make hook executable, syntax-check, note enablement**

Run: `chmod +x .githooks/pre-commit && bash -n .githooks/pre-commit`
Note: enabling is a one-time per-clone step (documented in CLAUDE.md): `git config core.hooksPath .githooks`.

- [ ] **Step 6: Add a `brief-freshness` job to `.github/workflows/ci.yml`**

Append this job under `jobs:` (sibling of `build-and-test`, no .NET needed):

```yaml
  brief-freshness:
    name: Brief freshness
    runs-on: ubuntu-latest
    timeout-minutes: 5
    steps:
      - name: Checkout
        uses: actions/checkout@v7
      - name: Check design-brief is fresh vs its sources
        run: bash scripts/check-brief-freshness
```

- [ ] **Step 7: Commit**

```bash
git add scripts/check-brief-freshness .githooks/pre-commit .github/workflows/ci.yml
git commit -m "build: add design-brief freshness gate (hook + CI) (#6)

Claude-Session: https://claude.ai/code/session_012oedWFLGri4VxM4gp7uyHS"
```

---

### Task 3: `alvo-regen-brief` generator skill

**Files:**
- Create: `.claude/skills/alvo-regen-brief/SKILL.md`

**Interfaces:**
- Consumes: the hash convention (File Structure).
- Produces: the committed, repeatable procedure Task 4 executes.

- [ ] **Step 1: Write `.claude/skills/alvo-regen-brief/SKILL.md`**

Frontmatter (verbatim):
```markdown
---
name: alvo-regen-brief
description: Use when docs/product/alvo-specifikacia.md or docs/product/baas-analyza.md change, or when the brief freshness gate blocks a commit, to regenerate docs/design-brief.en.md.
---
```

Body must specify, completely and unambiguously:
- **Inputs:** `docs/product/alvo-specifikacia.md` (how/order) + `docs/product/baas-analyza.md` (what/why). Read both fully.
- **Output:** `docs/design-brief.en.md` — one file, **English**, deliberately lossy, split into sections in this order: *Principles* (§0) · *Two modes* · *Ports & guarantees* · *Hard invariants / contracts* · *Key decisions + why* · *Boundaries* (descriptor ≠ infra, MCP = adapter, two sources of truth, computed/rollup/hook ladder) · *Phase map*.
- **Keep:** principles, hard invariants / port guarantees, decisions + their *why*, boundaries. **Drop:** prose, competitor case studies, deliberation history, illustrative code examples.
- **Header:** emit the GENERATED warning line + one `brief-source:` marker per source with its current SHA-256 (see hash convention). Compute the hashes as the last step so they match what is committed.
- **Compression quality test (state it in the skill):** after reading the brief, the agent must not make a decision it would not make after reading the full spec. If dropping something would cause a shortcut, keep it.
- **Audience note:** this brief is for the agent *building* Alvo (distinct from the consumer `llms.txt`, issue #26 / `[26]`).
- **Freshness note:** the brief is regenerated whenever a source changes; `scripts/check-brief-freshness` and `.githooks/pre-commit` enforce it; `alvo-plan-guard` may additionally flag shallow compression.

- [ ] **Step 2: Verify frontmatter and content**

Run: `head -5 .claude/skills/alvo-regen-brief/SKILL.md` (has `name:` + `description:`); confirm the seven output sections, keep/drop lists, and header instruction are all present.

- [ ] **Step 3: Commit**

```bash
git add .claude/skills/alvo-regen-brief/SKILL.md
git commit -m "docs: add alvo-regen-brief generator skill (#6)

Claude-Session: https://claude.ai/code/session_012oedWFLGri4VxM4gp7uyHS"
```

---

### Task 4: Generate `docs/design-brief.en.md`

**Files:**
- Create: `docs/design-brief.en.md`

**Interfaces:**
- Consumes: `alvo-regen-brief` (Task 3), `check-brief-freshness` (Task 2), both source docs.
- Produces: the brief; makes `check-brief-freshness` pass.

- [ ] **Step 1: Execute the `alvo-regen-brief` procedure**

Read both source docs in full; produce `docs/design-brief.en.md` per the skill: English, lossy, the seven sections in order, keep/drop rules applied. This is a semantic task — write real compressed content, not headings-only.

- [ ] **Step 2: Write the freshness header with current hashes**

Add at the very top (compute with `sha256sum`/`shasum -a 256` over each source):
```
<!-- GENERATED — do not hand-edit. Regenerate via the alvo-regen-brief skill. -->
<!-- brief-source: docs/product/alvo-specifikacia.md sha256:<hash> -->
<!-- brief-source: docs/product/baas-analyza.md sha256:<hash> -->
```

- [ ] **Step 3: Verify freshness and quality**

Run: `scripts/check-brief-freshness`
Expected: `check-brief-freshness: OK`, exit 0.
Manual: all seven sections present; text is English; no long verbatim prose/examples copied; the 9 principles, port guarantees, and boundaries are all represented.

- [ ] **Step 4: Commit**

```bash
git add docs/design-brief.en.md
git commit -m "docs: add generated design-brief.en.md (#6)

Claude-Session: https://claude.ai/code/session_012oedWFLGri4VxM4gp7uyHS"
```

---

### Task 5: `docs/PLAN.md`

**Files:**
- Create: `docs/PLAN.md`

**Interfaces:**
- Produces: the master plan referenced by CLAUDE.md (Task 8) and read by alvo-plan-guard (Task 7).

- [ ] **Step 1: Write `docs/PLAN.md` (coarse, ~1 page)** with these sections:

1. **What this is** — a *map of the country* (long-lived, "where this is all heading"); a Superpowers plan is an *itinerary for one trip* (short-lived, per issue). Superpowers brainstorming reads PLAN.md and gets the decomposition ready-made; they do not overwrite each other — PLAN.md points to issues, a Superpowers plan implements one line of PLAN.md.
2. **Target end-state (from analysis)** — an ultra-coarse ~10–15 bullet sketch of the final Alvo so no local shortcut breaks the whole: full BaaS surface (auto-API/CRUD, rule engine, events + automation, auth, RBAC, realtime, storage, tenancy, dynamic entities, audit); admin dashboard with an AI agent; optional MCP adapter; descriptor as the unified artifact (mount = CLI = API = FromDescriptor = UI export); two modes (standalone Docker / embedded NuGet); provider model everywhere; deploy via Aspire to Azure / Kubernetes. Where, not how.
3. **Phase map F0–F7** — a checklist, one-sentence goal per phase from the GitHub milestone descriptions, with a `← YOU ARE HERE` marker on **F0**. Goals: F0 Skeleton (something to build on); F1 Quality before code (all gates on empty projects); F2 Schema foundation (schema is the source of truth, test against it); F3 Vertical slice CRUD (project → table → CRUD API + validations); F4 Demo from the start (proof + testing surface, parallel with F3); F5 Admin mode (dashboard, rules/automation builder, AI agent); F6 v0.1 (docs, logo, release); F7 Further components (by value, gradually). Link phases to milestones/issues.
4. **Key invariants that must not break** — interface-first; provider model everywhere; secure-by-default / default-deny; descriptor ≠ infra config; MCP = optional adapter over Management API; two sources of truth (repo file vs DB record, bidirectional bridge); CEL for conditions, JSONata for transforms (JSONata never in-transaction); never merge to `main` (PR is the only full gate); the core is one big package (a package is earned).
5. **Freshness** — shift `← YOU ARE HERE` after finishing an issue; upkeep tied to `alvo-plan-guard` (#8), which proposes the update.

Keep it coarse — point to the spec for "how"; do not duplicate detail.

- [ ] **Step 2: Verify**

Run: `grep -c "YOU ARE HERE" docs/PLAN.md` (≥1) and confirm all five sections and F0–F7 are present.

- [ ] **Step 3: Commit**

```bash
git add docs/PLAN.md
git commit -m "docs: add coarse master plan PLAN.md (#5)

Claude-Session: https://claude.ai/code/session_012oedWFLGri4VxM4gp7uyHS"
```

---

### Task 6: Four domain skills

**Files:**
- Create: `.claude/skills/alvo-architecture-rules/SKILL.md`
- Create: `.claude/skills/alvo-security-core-review/SKILL.md`
- Create: `.claude/skills/alvo-schema-testing/SKILL.md`
- Create: `.claude/skills/alvo-dotnet-conventions/SKILL.md`

**Interfaces:**
- Produces: four skills referenced by CLAUDE.md (Task 8). Each is a directory with `SKILL.md`, YAML frontmatter (`name`, `description` = a strong activation trigger).

- [ ] **Step 1: `alvo-architecture-rules/SKILL.md`**

Frontmatter:
```markdown
---
name: alvo-architecture-rules
description: Use when touching Alvo's architecture, ports, public API, package structure, or the descriptor — enforces the framework's structural rules.
---
```
Body (each a rule with its *why*): two sources of truth (descriptor as repo file / GitOps vs DB record / dashboard-first; bidirectional export/import bridge); computed vs rollup vs hook ladder (computed = same-row expression → DB stored generated column, read-only; rollup = aggregation over related records → transactionally consistent trigger/in-tx, never a manual hook; conditional/contextual/time → before-hook mutate or function; complex branching/external → automation action or csx; order declarative → hook → action → csx); MCP is only an adapter over the Management API, not a building block; minimal API + `RouteGroupBuilder`, not MVC controllers; vertical slice inside packages (organize by feature, not technical layer) — distinct from the package boundary (§1.1) which is the distribution/licensing split; encapsulation — `public` only what is contract, default `internal`/`private`; descriptor ≠ infra config; a package is earned (default to code inside the core; do not create projects ahead of time). Point to `docs/architecture/package-boundary.md` and spec §1.1.

- [ ] **Step 2: `alvo-security-core-review/SKILL.md`**

Frontmatter:
```markdown
---
name: alvo-security-core-review
description: Use when changing the rule engine, tenancy, CEL compilation, or authorization — the security core. Runs a deep-review checklist and marks the change needs-deep-review.
---
```
Body — a checklist: SQL predicate — user input is never interpolated (parameterized only; a property test must prove it); authorization goes into the SQL WHERE, never an in-memory post-filter; fail-fast compile (a nonexistent column errors at save); cross-tenant isolation — user A never sees user B's data (two-user + two-tenant tests), policy enforced inside the data port, not around it; default-deny — nothing exposed without an explicit policy; before-hooks are in-transaction, time-budgeted, network-forbidden (structurally enforced). End with: **mark this change `needs-deep-review`** — state it in the output and recommend adding the `needs-deep-review` PR label. Ties in with `alvo-plan-guard`.

- [ ] **Step 3: `alvo-schema-testing/SKILL.md`** (thin, forward-referencing)

Frontmatter:
```markdown
---
name: alvo-schema-testing
description: Use when writing or changing tests against the Alvo descriptor JSON Schema — the four schema test types.
---
```
Body — the four types (from issue #17 / `[13]`, F2), described at spec level, with a note that the full mechanism lands in F2 (#17): (1) **Meta-validation** — the schema is valid draft 2020-12 (CI gate); (2) **Examples against the schema** — every descriptor under `examples/` (incl. the demo) must validate; a schema change that breaks an example → red CI; (3) **Round-trip property tests (CsCheck)** — random valid descriptor → apply → export → equals the input (catches schema↔implementation divergence); (4) **Snapshot (Verify)** — descriptor → generated DB schema / OpenAPI; a change = a visible diff. Note it forward-references #17 and stays thin until F2.

- [ ] **Step 4: `alvo-dotnet-conventions/SKILL.md`**

Frontmatter:
```markdown
---
name: alvo-dotnet-conventions
description: Use when adding a NuGet package, choosing a library, or writing C# in MMLib.Alvo — packaging, licensing, test stack, and code-style conventions.
---
```
Body: Central Package Management — versions in `Directory.Packages.props`, `PackageReference` carries no `Version`; shared MSBuild in `Directory.Build.props`; target `net10.0`, SDK pinned in `global.json`; **licensing bans** — no MediatR, no FluentAssertions v8+, use Shouldly (hint: Wolverine is MIT and does outbox + in-process mediator); **test stack** — xUnit v3 on Microsoft.Testing.Platform (MTP, not VSTest), NSubstitute (fakes), CsCheck (property-based), Verify (snapshot), NetArchTest (arch), PublicApiGenerator (public-API approval), Testcontainers (integration); **code style** — comments say *why* not *what* (self-documenting code; extract a well-named method instead of narrating), XML doc comments (`/// <summary>`) required on public API members of shipped library projects.

- [ ] **Step 5: Verify all four**

Run: `for f in architecture-rules security-core-review schema-testing dotnet-conventions; do echo "== $f =="; head -4 .claude/skills/alvo-$f/SKILL.md; done`
Expected: each has `name:` matching the dir and a `description:` trigger.

- [ ] **Step 6: Commit**

```bash
git add .claude/skills/alvo-architecture-rules .claude/skills/alvo-security-core-review .claude/skills/alvo-schema-testing .claude/skills/alvo-dotnet-conventions
git commit -m "docs: add Alvo domain skills for Superpowers (#7)

Claude-Session: https://claude.ai/code/session_012oedWFLGri4VxM4gp7uyHS"
```

---

### Task 7: `alvo-plan-guard` subagent

**Files:**
- Create: `.claude/agents/alvo-plan-guard.md`

**Interfaces:**
- Consumes: `docs/PLAN.md`, the spec, git diff (read-only).
- Produces: a pre-PR verdict + a proposed PLAN.md `YOU ARE HERE` update.

- [ ] **Step 1: Write `.claude/agents/alvo-plan-guard.md`**

Frontmatter (read-only tools — no Write/Edit):
```markdown
---
name: alvo-plan-guard
description: Validates a larger change against the master plan before a PR — flags drift from docs/PLAN.md, violated §0 principles, and shortcuts in the security core; proposes the ← YOU ARE HERE shift after an issue finishes. Read-only; returns a verdict, does not rewrite code.
tools: Read, Grep, Glob, Bash
---
```
Body (system prompt) must instruct the subagent to:
- Have its **own context** — read `git diff` (via Bash), `docs/PLAN.md`, and the relevant part of `docs/product/alvo-specifikacia.md` / `docs/design-brief.en.md`.
- **Job 1 — drift check:** answer *deviation from the plan? violated §0 principle? shortcut in the security core?* Return a verdict (`PASS` or `ISSUES`) + a concise list of concrete issues.
- **Escalation:** if the change touches the core / rule engine / tenancy → flag `needs-deep-review` (tie-in with `alvo-security-core-review`) and recommend the `needs-deep-review` PR label.
- **Job 2 — plan upkeep:** if an issue finished, **propose** the exact `← YOU ARE HERE` edit for `docs/PLAN.md` (as a suggested diff — do NOT apply it; the tools are read-only).
- May also flag a stale or shallow `docs/design-brief.en.md`.
- **Never rewrite code**; output only the verdict, the issue list, the escalation flag, and the proposed plan edit.
- Runs **before the PR**, as the last check before human review.

- [ ] **Step 2: Verify frontmatter**

Run: `head -6 .claude/agents/alvo-plan-guard.md`
Expected: `name: alvo-plan-guard`, a `description:`, and `tools: Read, Grep, Glob, Bash` (no Write/Edit).

- [ ] **Step 3: Commit**

```bash
git add .claude/agents/alvo-plan-guard.md
git commit -m "docs: add alvo-plan-guard validation subagent (#8)

Claude-Session: https://claude.ai/code/session_012oedWFLGri4VxM4gp7uyHS"
```

---

### Task 8: `CLAUDE.md` v1 rewrite

**Files:**
- Modify: `CLAUDE.md` (whole-file rewrite)

**Interfaces:**
- Consumes: every artifact above (skills, agent, scripts, hook, PLAN.md, brief) — reference them by exact path/name. Run this task **last**.

- [ ] **Step 1: Rewrite `CLAUDE.md` (~150 lines, router — mostly pointers)** with these sections, in order:

1. **Role / expertise (up front):** "You are an expert at building .NET frameworks and libraries — you care about a clean public API, encapsulation, backward compatibility, DX, and idiomatic .NET patterns."
2. **What Alvo is** — 5 sentences (.NET-native BaaS for the agentic age; NuGet family `MMLib.Alvo.*`; two modes standalone/embedded; descriptor-driven; agent-first).
3. **The 9 principles** — one-line bullets condensed from spec §0 (interface-first; provider model everywhere; engine-agnostic core; agent-first; secure-by-default/default-deny; CEL for conditions & JSONata for transforms; JSON single descriptor format; minimal API not MVC; vertical slice inside packages).
4. **Repo map** — `src/`, `test/`, `docs/product/`, `docs/architecture/`, `docs/superpowers/{specs,plans}/`, `.claude/skills/`, `.claude/agents/`, `scripts/`, `.githooks/`.
5. **Build, test & rings** — `dotnet build`; `dotnet test` (MTP, net10.0, SDK pinned in `global.json`); the ring table: ring0 (`scripts/test-ring0`, after every small step) → ring1 (`scripts/test-ring1`, after a slice) → ring2 (`scripts/test-ring2`, before PR); full run (+ mutation + e2e) = CI on the PR, not local.
6. **Hard rules** — NEVER merge/push directly to `main` (branch → PR → a human merges after review); PR is the only full gate (no nightly / post-merge); **before opening a PR, dispatch the `alvo-plan-guard` subagent** as the last check.
7. **Context pyramid (pointers, read big docs rarely / on demand):** `docs/PLAN.md` = where we are + target end-state; `docs/design-brief.en.md` = the whole context in one breath (generated, EN); `docs/product/*.md` = full spec + analysis (SK, ~200 KB), read rarely on demand; `docs/architecture/package-boundary.md`.
8. **Skills & guard:** the `alvo-*` skills activate when a task touches their area — `alvo-architecture-rules`, `alvo-security-core-review`, `alvo-schema-testing`, `alvo-dotnet-conventions` (packaging/licensing/test-stack/code-style live here, not in this file), `alvo-regen-brief` (regenerate the brief when sources change).
9. **One-time setup:** `git config core.hooksPath .githooks` (enables the brief freshness pre-commit hook).
10. **Short always-on framing:** package boundary (a package is earned — see `docs/architecture/package-boundary.md`); do not create projects ahead of time (the core `MMLib.Alvo` appears once it has real content).

Do NOT keep the old detailed Conventions / Code style blocks inline — they now live in `alvo-dotnet-conventions`; leave only the one-line pointer in section 8.

- [ ] **Step 2: Verify**

Run:
```bash
grep -qi "never" CLAUDE.md && grep -qi "main" CLAUDE.md && echo "hard-rule OK"
grep -q "docs/PLAN.md" CLAUDE.md && grep -q "design-brief.en.md" CLAUDE.md && grep -q "test-ring0" CLAUDE.md && grep -q "alvo-plan-guard" CLAUDE.md && echo "pointers OK"
grep -q "MediatR" CLAUDE.md && echo "WARN: conventions still inline" || echo "conventions moved out OK"
wc -l CLAUDE.md
```
Expected: `hard-rule OK`, `pointers OK`, `conventions moved out OK`, line count roughly 120–170.

- [ ] **Step 3: Create the `needs-deep-review` GitHub label (one-time)**

Run: `gh label create needs-deep-review --description "Change to the security core needs deep review" --color B60205 || true`

- [ ] **Step 4: Commit**

```bash
git add CLAUDE.md
git commit -m "docs: rewrite CLAUDE.md as agentic-context router (#4)

Claude-Session: https://claude.ai/code/session_012oedWFLGri4VxM4gp7uyHS"
```

---

## Self-Review Notes

- **Spec coverage:** #4 → Tasks 1 (rings) + 8 (CLAUDE.md); #5 → Task 5; #6 → Tasks 2 (gate) + 3 (generator) + 4 (brief); #7 → Task 6 (4 skills; conventions moved here from CLAUDE.md); #8 → Task 7. `needs-deep-review` marker → Tasks 6 + 7 + 8 (label). Freshness two levels → Task 2 (deterministic) + Task 7 (subagent). No gaps.
- **Placeholder scan:** none — scripts/frontmatter given verbatim; prose deliverables enumerate every required point. The brief (Task 4) is intentionally "execute the procedure" because its content is a semantic compression, not a fixed paste.
- **Type/name consistency:** hash marker format identical across File Structure, Task 2 (script + hook), Task 3 (skill), Task 4 (header). Script/skill/agent names match the file paths and the CLAUDE.md pointers in Task 8. `check-brief-freshness` used by hook, CI, and Task 4 verification with the same contract (exit 0 = fresh).
- **Order/deps:** 1, 5, 6, 7 independent; 3 needs the hash convention; 4 needs 2 + 3; 8 last (references all). Safe for parallel dispatch in that grouping.
