# Agentic Context Skeleton — Design

**Date:** 2026-07-13
**Milestone:** F0 — Skeleton
**Issues:** #4 [3] CLAUDE.md v1, #5 [3b] docs/PLAN.md, #6 [3c] docs/design-brief.en.md,
#7 [4] Alvo domain skills, #8 [4b] alvo-plan-guard
**Branch:** `f0-agentic-context-skeleton`

## Goal

Build the **agent operating system** for this repository: the layered context an
agent (Superpowers / Claude) reads to know what it is building, plus the guards
that stop it from drifting off the plan or shipping a stale, false-confidence
document. These five issues are the **last remaining F0 work** — together they
complete the Skeleton milestone's agent layer.

They are not five independent documents. They are one system with two sides:

- a **read path** (the context pyramid) — what the agent reads to know;
- a **verify path** (the guard system) — what keeps that knowledge honest and
  the work on-plan.

## The system in one picture

### A) Read path — context pyramid

```
CLAUDE.md              #4   always in context — router: role, what Alvo is,
   │                        9 principles, build/test + rings, hard rules, POINTERS
   ├─► docs/PLAN.md            #5   "where we are in time" — target end-state +
   │                               F0–F7 map + ← YOU ARE HERE
   ├─► docs/design-brief.en.md #6   "whole context in one breath" — generated, EN,
   │                               lossy (source: spec + analysis, sectioned)
   └─► docs/product/*.md       full spec + analysis (SK, ~200 KB) — read rarely,
                                   on demand
```

Skills (#7) are **not** a pyramid layer — they **activate contextually** when a
task touches their area.

### B) Verify path — guard system

```
at commit  ── .githooks/pre-commit (cheap, deterministic) ─► check-brief-freshness:
              hash(spec + analysis) == brief header hashes ?
                                             no → BLOCK: "brief is stale, regenerate"

before PR  ── alvo-plan-guard subagent (#8, own context) ─► reads diff + PLAN.md + spec:
              • drift from plan?  • violated §0 principle?  • shortcut in security core?
                → needs-deep-review
              • proposes ← YOU ARE HERE shift in PLAN.md
              • may semantically flag a stale / shallow brief

during task ── alvo-* skills (#7) ─► inject domain discipline (incl. security-core-review)
```

**Freshness has two complementary levels (they do not compete):**

1. **Cheap deterministic gate at commit** — a hash of the two source files
   compared against hashes stored in the brief header. Catches "you forgot to
   regenerate." This is exact hashing, not the "dumb regex" #8 warns against
   (that warning is about judging *intent* with regex).
2. **Intelligent subagent before PR (#8)** — understands intent: whether the
   brief actually reflects the decisions, whether the change drifted from
   PLAN.md, whether there is a shortcut in the security core; and it proposes
   the `← YOU ARE HERE` shift.

Regenerating the brief (compression + translation) after a block is done by the
agent via a **committed generator skill** (`alvo-regen-brief`), so "how to
compress" is versioned, not ad-hoc.

## Components

### #4 — `CLAUDE.md` v1 (~150 lines, router)

Rewrite the root `CLAUDE.md` so it stays a **router** — mostly pointers, not
content. Detail lives in skills and the pyramid documents.

**Always-on (cannot rely on skill activation; the agent needs it constantly):**

- **Role / expertise up front:** "You are an expert at building .NET frameworks
  and libraries — you care about a clean public API, encapsulation, backward
  compatibility, DX, and idiomatic .NET patterns."
- **What Alvo is** — 5 sentences.
- **The 9 principles** as one-line bullets (spec §0) — issue #4 mandates these
  live here.
- **Repo map** — `src/`, `test/`, `docs/product/`, `docs/architecture/`,
  `docs/superpowers/`, `.claude/skills/`, `.claude/agents/`, `scripts/`,
  `.githooks/`.
- **Build, test & rings** — `dotnet build`, `dotnet test` (MTP, net10.0), and the
  ring table (see below).
- **Hard rules** — NEVER merge/push directly to `main` (branch → PR → human
  merges after review); PR is the only full gate (no nightly / post-merge).
- **Context pyramid pointers** — PLAN.md ("where we are"), design-brief.en.md
  ("the whole context in one breath"), full spec/analysis ("read rarely, on
  demand"), package-boundary.md.
- **Skills & guard** — the `alvo-*` skills activate when a task touches their
  area; **before opening a PR, dispatch the `alvo-plan-guard` subagent** as the
  last check before human review.
- **One-time setup pointer** — `git config core.hooksPath .githooks` to enable
  the freshness hook.

**Kept as short always-on framing (with pointers):** package boundary (points to
`docs/architecture/package-boundary.md`), "do not create projects ahead of time."

**Moved OUT of CLAUDE.md into the `alvo-dotnet-conventions` skill (#7):** Central
Package Management, `Directory.Build.props`, licensing bans (no MediatR, no
FluentAssertions v8+, use Shouldly), test stack, and code style. CLAUDE.md keeps
only a one-line pointer to that skill.

#### Test rings (`scripts/test-ring0|1|2`)

Layered, so they run and pass now and grow by **adding steps, not rewriting**:

| Ring | Script | When | Today | Grows to |
|---|---|---|---|---|
| ring0 | `scripts/test-ring0` | after every small step | `dotnet test` | + fast-only filter once slow tests exist |
| ring1 | `scripts/test-ring1` | after finishing a slice | calls ring0 | + arch + public-API tests |
| ring2 | `scripts/test-ring2` | before PR | calls ring1 | + integration (affected) + API invariant + Vacuum |

Full run (+ mutation + e2e) stays in CI on the PR; the agent does not run it
locally. Scripts are POSIX `bash`, executable, no extension (spec names them
`scripts/test-ring0` …). Each script echoes what it ran and what is still a
placeholder, so "empty pass" is never mistaken for "covered everything."

### #5 — `docs/PLAN.md` (coarse, ~1 page)

Coarse so it does not eat context. Structure:

1. **What this is** — a *map of the country* (long-lived, "where this is all
   heading"); a Superpowers plan is an *itinerary for one trip* (short-lived, per
   issue). Superpowers brainstorming reads PLAN.md and gets the decomposition
   ready-made instead of reinventing it. They do not overwrite each other —
   PLAN.md points to issues; a Superpowers plan implements one line of PLAN.md.
2. **Target end-state (from analysis)** — an *ultra-coarse* sketch (~10–15
   bullets) of the final Alvo, so the agent knows where we are heading and does
   not take a locally-sensible shortcut that breaks the whole: full BaaS surface
   (auto-API/CRUD, rule engine, events + automation, auth, RBAC, realtime,
   storage, tenancy, dynamic entities, audit), admin dashboard with an AI agent,
   optional MCP adapter, descriptor as the unified artifact, two modes
   (standalone / embedded), provider model everywhere, deploy via Aspire to
   Azure / Kubernetes. **Where, not how.**
3. **Phase map F0–F7** — a checklist with a one-sentence goal per phase (from the
   GitHub milestone descriptions), a link to each milestone/its issues, and a
   `← YOU ARE HERE` pointer (currently at **F0**, finishing the agent layer).
4. **Key invariants that must not break** — interface-first; provider model;
   default-deny; descriptor ≠ infra; MCP = adapter; two sources of truth (repo
   file vs DB record); CEL for conditions / JSONata for transforms (JSONata never
   in-transaction); never merge to `main`; the core is one big package.
5. **Freshness** — shift `← YOU ARE HERE` after finishing an issue; upkeep is
   tied to `alvo-plan-guard` (#8), which proposes the update.

**Altitude split vs the brief (so they do not drift into duplicates):**

- **PLAN.md → Target end-state** = *headline* sketch of the destination (~10–15
  bullets), cheap to read, orientation ("where").
- **design-brief.en.md** = *detailed compression* (principles, ports,
  invariants, decisions + why), read at brainstorming.

Canonical phase numbering for PLAN.md is the **GitHub milestones F0–F7** (issues
are grouped under them). The spec's "Fáza 1–7" is the "how" detail PLAN.md points
into.

### #6 — `docs/design-brief.en.md` + generator + freshness gate

**The brief** — generated, in English, deliberately lossy, one file split into
sections:

- *Principles* (§0) · *Two modes* · *Ports & guarantees* · *Hard invariants /
  contracts* · *Key decisions + why* · *Boundaries* (descriptor ≠ infra, MCP =
  adapter, two sources of truth, computed/rollup/hook ladder) · *Phase map*.
- **Keep:** principles, hard invariants / port guarantees, decisions + their
  *why*, boundaries. **Drop:** prose, competitor case studies, deliberation
  history, illustrative examples.
- **Header** (machine-readable freshness anchor + human warning):

  ```
  <!-- GENERATED — do not hand-edit. Regenerate via the alvo-regen-brief skill. -->
  <!-- brief-source: docs/product/alvo-specifikacia.md sha256:<hash> -->
  <!-- brief-source: docs/product/baas-analyza.md sha256:<hash> -->
  ```

  (One single-line `brief-source:` marker per source — the exact form
  `scripts/check-brief-freshness` parses.)

- **Audience:** the agent that is **building** Alvo. (Distinct from the `llms.txt`
  in #26, which is for agents/users **consuming** the framework. Same format
  family, different content — not duplicates.)
- **Compression quality test:** after reading the brief, the agent must not make
  a decision it would not make after reading the full spec. If omitting something
  causes a shortcut, the compression was bad.

**`alvo-regen-brief` skill** (`.claude/skills/alvo-regen-brief/SKILL.md`) — the
committed, repeatable generator. It defines: inputs (both source files), the
keep/drop rules above, the output section structure, "write in English," and
"recompute the source sha256 hashes and write them into the header." The v1 brief
content is produced by running this skill during implementation (an agent task —
the compression is semantic, so a deterministic text script cannot do it).

**Freshness gate:**

- `scripts/check-brief-freshness` — computes sha256 of the two source files,
  reads the hashes from the brief header, compares. Exit 0 on match; non-zero
  with a clear message on mismatch or a missing brief. Reusable by the hook and
  CI.
- `.githooks/pre-commit` — if the staged change touches either source file or the
  brief, run `check-brief-freshness`; block the commit on failure with a message
  pointing at `alvo-regen-brief`. Enabled once per clone via
  `git config core.hooksPath .githooks` (documented in CLAUDE.md).
- **CI belt-and-suspenders** — a `brief-freshness` job/step (new small workflow or
  a step in `ci.yml`) runs `check-brief-freshness` on every PR, so freshness is
  enforced even if a contributor never enabled the local hook.

When the agent changes a source and regenerates the brief in the same session,
the header hashes update to match the new sources, so the commit passes. Forget
the regen → the gate blocks.

### #7 — `.claude/skills/` — domain skills

Superpowers skill format: one directory per skill with `SKILL.md`, YAML
frontmatter (`name`, `description` written as a strong activation trigger).

- **`alvo-architecture-rules`** — two sources of truth (repo file vs DB record),
  computed vs rollup vs hook ladder, MCP is just an adapter, minimal API +
  vertical slice (organized by features, not layers), **encapsulation: `public`
  only what is contract, default `internal`/`private`**, descriptor ≠ infra.
  Trigger: touching architecture, package structure, ports, or public API.
- **`alvo-security-core-review`** — checklist for rule engine / tenancy / CEL:
  verify the SQL predicate against injection, verify cross-tenant isolation,
  default-deny; **mark the change `needs-deep-review`**. Trigger: touching the
  rule engine, tenancy, CEL compilation, or authorization.
- **`alvo-schema-testing`** — the four types of tests against the schema. F2
  (#13) is not in this batch, so this skill is **thinner and
  forward-referencing** — it states the four test types at the level the
  spec/analysis defines and points to #13 for the full treatment. Trigger:
  writing or changing tests against the descriptor schema.
- **`alvo-dotnet-conventions`** — Central Package Management
  (`Directory.Packages.props`, `PackageReference` without `Version`),
  `Directory.Build.props`, licensing bans (no MediatR, no FluentAssertions v8+,
  use Shouldly), test stack (xUnit v3 on MTP, NSubstitute, CsCheck, Verify), code
  style (comments say *why* not *what*, XML docs on public API). Trigger: adding a
  NuGet package, choosing a library, or writing C# in MMLib.Alvo.

### #8 — `.claude/agents/alvo-plan-guard.md` — subagent

A subagent with its **own context** (does not eat the main one), **read-only**
(tools: `Read, Grep, Glob, Bash` for `git diff` — no `Write`/`Edit`; it returns a
verdict, it does not rewrite code).

- **Job 1 — drift check (before PR):** reads the diff + `docs/PLAN.md` + the
  relevant part of the spec, and answers: *deviation from the plan? violated §0
  principle? shortcut in the security core?* Returns a verdict + a list of
  issues.
- **Escalation:** if the change touches the core / rule engine / tenancy →
  `needs-deep-review` (tie-in with `alvo-security-core-review`).
- **Job 2 — master-plan upkeep:** on finishing an issue, **proposes** shifting
  `← YOU ARE HERE` in `docs/PLAN.md` (proposal only — a human/agent applies it).
- May also semantically flag a stale or shallow brief (compression-quality
  tie-in with #6).
- **Better than a hook:** a subagent understands intent; runs before the PR as
  the last check before human review.

**Output format:** a structured verdict — `PASS` or `ISSUES` + the list; a
`needs-deep-review` flag when escalated; a proposed PLAN.md edit when an issue
finished.

**Invocation:** the agent (or human) dispatches `alvo-plan-guard` before opening
a PR. This is documented in CLAUDE.md's hard rules.

### `needs-deep-review` marker (no new infrastructure)

The skill / subagent **emits `needs-deep-review` in its verdict** and recommends
adding the `needs-deep-review` PR label (created once via `gh label create`). No
marker file, no new machinery.

## File inventory

**Created:**

- `docs/PLAN.md`
- `docs/design-brief.en.md` (generated v1)
- `.claude/skills/alvo-architecture-rules/SKILL.md`
- `.claude/skills/alvo-security-core-review/SKILL.md`
- `.claude/skills/alvo-schema-testing/SKILL.md`
- `.claude/skills/alvo-dotnet-conventions/SKILL.md`
- `.claude/skills/alvo-regen-brief/SKILL.md`
- `.claude/agents/alvo-plan-guard.md`
- `scripts/test-ring0`, `scripts/test-ring1`, `scripts/test-ring2`
- `scripts/check-brief-freshness`
- `.githooks/pre-commit`
- CI freshness check (a step in `.github/workflows/ci.yml` or a new small
  workflow)

**Modified:**

- `CLAUDE.md` (rewrite to router; move conventions/style out to the skill)

## Verification

This batch is docs + shell + skill/agent definitions — no product C# yet.

- `scripts/test-ring0|1|2` each run and exit 0 (they call `dotnet test`, which
  finds nothing today) and print what ran / what is a placeholder.
- `scripts/check-brief-freshness` exits 0 when the brief header matches the
  sources, non-zero when they diverge (verify by touching a source without
  regenerating).
- `.githooks/pre-commit` blocks a commit that stages a source change without a
  matching brief (verify manually once).
- `alvo-plan-guard` can be dispatched and returns a verdict against the current
  diff.
- Skills carry valid frontmatter and activate on a representative prompt.
- CLAUDE.md fits a reasonable context, references PLAN.md / brief / spec / schema,
  and states "always via PR, never alone into main."

## Out of scope

- Actual product code, ports, or tests (F1+).
- The four schema test *implementations* (#13, F2) — `alvo-schema-testing` only
  describes them here.
- `llms.txt` for consumers (#26).
- Any change to the spec/analysis content itself.

## Decisions log

- **Brief generation:** gate + agent regenerates (a deterministic hash gate at
  commit; semantic compression by the agent via `alvo-regen-brief`), **not** an
  LLM call inside the git hook — avoids per-commit CLI/network dependency and
  noisy non-deterministic diffs.
- **Freshness enforcement:** built now (commit-time hook + CI check), deepened by
  `alvo-plan-guard` (#8), which is included in this batch.
- **Conventions/style:** moved out of CLAUDE.md into `alvo-dotnet-conventions` so
  CLAUDE.md stays a router.
- **PLAN.md:** includes a coarse "Target end-state (from analysis)" so the agent
  sees the destination, not just the next phase.
- **Scope:** all five F0 issues (#4–#8) land together via one branch → one
  reviewed PR.
