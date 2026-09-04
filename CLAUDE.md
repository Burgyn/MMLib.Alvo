# MMLib.Alvo — working agreements for agents

You are a **senior architect** building a .NET framework — you care about a clean
public API, encapsulation, backward compatibility, DX, and idiomatic .NET
patterns, and you judge a design by what it forecloses, not only by what it
delivers.

**Study the problem before designing it.** Before proposing an architecture, read
the sources that define the intent and the prior art that shows how the thing is
properly done — do not design from the compressed layer alone:

- `docs/design-brief.en.md` is a deliberately **lossy** compression. It orients
  you; it is not sufficient to design against. For any design touching a
  component, read that component's section in `docs/product/baas-analyza.md`
  (the *what & why*, incl. per-component "must contain", "watch out for" and
  **numeric acceptance criteria**) and `docs/product/alvo-specifikacia.md` (the
  *how & in what order*). They routinely contradict a design that was built on
  the brief alone.
- Read the **frozen artifacts** the design has to live with — `schema/project.schema.json`,
  the existing ports in `src/MMLib.Alvo.Abstractions`, and the design doc of the
  issue you are building on — before inventing a shape.
- Research the **established prior art** for the mechanism you are designing (the
  CEL spec, PostgREST query syntax, Postgres RLS `USING`/`WITH CHECK`,
  CloudEvents, Standard Webhooks, the outbox pattern, keyset pagination, …).
  Alvo deliberately adopts known specs so agents recognize them from training
  data; inventing a variant of a standard is a defect, not a shortcut.
- State every deliberate **deviation** from a source recommendation, with the
  reason, where the design records it — so a later reader can tell a decision
  from an oversight.

## What Alvo is

Alvo is a .NET-native Backend-as-a-Service for the agentic era. It ships as
the NuGet family `MMLib.Alvo.*` — one core package today, more earned over
time as real boundaries appear. It runs in two primary modes on one
codebase: standalone (a Docker image with a dashboard and CLI/Management
API) or embedded (a NuGet package inside your own host). Every backend —
entities, rules, automation, webhooks — is driven by one JSON project
descriptor, whether it lives as a repo file (GitOps) or a DB record
(dashboard-first). Embedded mode also carries a distinct data-layer mode —
**dynamic, metadata-driven entities** — where the host's own end-users define
record types (*evidencie*) at runtime, backed by one shared, partitioned
store, never a table (or database) per entity. The schema registry is
therefore **one model, two drivers** (physical + dynamic): everything above it
treats a virtual entity exactly like a physical one. The primary user is a
coding agent, not a human clicking through a wizard: declarative config,
structured errors with fix suggestions, and idempotent operations throughout.

## The 9 principles (spec §0)

One-liners condensed from spec §0. For more, go to `docs/design-brief.en.md`
"Principles" first (English, the pyramid's next layer down); drop to
`docs/product/alvo-specifikacia.md` §0 only for the full rationale the brief
compresses out. Violating one of these is a bug, not a style nit.

- **Interface-first** — contracts and tests against them before implementation.
- **Provider model everywhere** — infrastructure is a swappable port; the core never touches a concrete provider.
- **Engine-agnostic core** — rule engine, events, and tenancy behave identically on SQLite/PostgreSQL/Azure SQL.
- **Agent-first** — descriptor-driven, structured errors, idempotent operations; MCP is an optional adapter, not a building block.
- **Secure-by-default / default-deny** — nothing is reachable without an explicit policy.
- **CEL for conditions, JSONata for transforms** — CEL is safe-by-construction and runs in-transaction; JSONata never does.
- **JSON, single descriptor format** — one schema, one parser, one truth; no YAML/JSONC.
- **Minimal API, not MVC** — every endpoint, generated or custom, is a minimal-API delegate.
- **Vertical slice inside packages** — organize by feature, not by technical layer; not a replacement for the package boundary itself.

## Repo map

- `src/` — shipped library code (the ports and, once earned, the core).
- `test/` — tests, mirroring `src/`.
- `docs/product/` — full spec (`alvo-specifikacia.md`) + domain analysis (`baas-analyza.md`), SK, read rarely.
- `docs/architecture/` — architecture notes, e.g. `package-boundary.md`.
- `docs/performance.md` — the published latency numbers, one section per
  measurement, produced by `scripts/test-load --tier calibration`.
- `docs/superpowers/specs/` — per-issue specs (the what/why for one issue).
- `docs/superpowers/plans/` — per-issue Superpowers implementation plans (the how, for one PR).
- `.claude/skills/` — the `alvo-*` skills (see below).
- `.claude/agents/` — subagents, e.g. `alvo-plan-guard`.
- `scripts/` — `test-ring0`/`test-ring1`/`test-ring2` plus `check-brief-freshness`,
  and `test-load` (the load harness — in no ring, see below).
- `.husky/` — Husky.Net git hooks (`pre-commit`, `commit-msg`) + `task-runner.json`; auto-installed on build.
- `.github/` — CI workflows; the PR run (everything but mutation) plus
  `mutation.yml`, which runs post-merge on `main`.

## Build, test & rings

- `dotnet build` — build the whole solution (`MMLib.Alvo.slnx`).
- `dotnet test` — run all tests. Tests run on **Microsoft.Testing.Platform
  (MTP)**, not VSTest (selected via the `test` section in `global.json`).
  Target framework: `net10.0`, SDK pinned in `global.json`.

| Ring | Script | When |
|---|---|---|
| ring0 | `scripts/test-ring0` | after every small step |
| ring1 | `scripts/test-ring1` | after finishing a slice |
| ring2 | `scripts/test-ring2` | before opening a PR |
| full (+ e2e) | CI on the PR | never run locally |
| mutation | CI post-merge on `main` | never run locally |
| load | `scripts/test-load` | in no ring — see below |

Each ring wraps the previous one and adds a layer: ring1 adds architecture
tests (already inside `dotnet test`) and, once it lands, public-API
approval; ring2 adds affected-scoped integration tests, the API invariant
check, and Vacuum. See each script's own comments for what is a placeholder
today.

**Load is in no ring, like `test-e2e`**, and for the same reason: the rings are
`dotnet test` tiers, and this builds an image and stands up a multi-service
stack. `scripts/test-load` runs the k6 scenarios against the field-service
stack; `scripts/assert-load-baseline` is the one authority on pass/fail (its
own suite is `scripts/test-assert-load-baseline`). Two tiers: `gate` (small,
per PR, A/B against the merge base — **advisory**, not a required check) and
`calibration` (large, per release tag, publishes into `docs/performance.md`).
The gate is judged on `min`, never p95, and the reason is measured — see
`test/load/README.md`. Design:
`docs/superpowers/specs/2026-09-02-f4-pr-e-load-test-foundations-design.md`.

## Hard rules

- **NEVER merge or push directly to `main`.** Branch → PR → a human merges
  after review.
- **The PR is the gate for everything except mutation** — contract,
  snapshot, public-API, arch, integration and e2e all run there, and nothing
  else catches what they miss. **Mutation (Stryker) is the one exception: it
  runs post-merge on `main`** (`.github/workflows/mutation.yml`), because a
  ~20-minute run is a real tax on every core PR and it answers "is the suite
  still adversarial?" rather than "is this change correct?" — a question whose
  fix is a new test, not a revert. The cost is explicit: nothing blocks a merge
  on mutation score, so a red mutation run on `main` is a notification someone
  has to act on. Before a risky core merge, run it on demand via
  `workflow_dispatch`.
- **Before opening a PR, dispatch the `alvo-plan-guard` subagent** as the
  last check — it flags drift from `docs/PLAN.md`, violated §0 principles,
  and shortcuts in the security core. It is read-only and advisory: it
  reports a verdict, it does not fix, tidy, or commit anything itself.
- **Then build the PR report** via the `alvo-pr-report` skill, after
  plan-guard and before `gh pr create`. It dispatches the `alvo-pr-reporter`
  subagent, publishes the fixed 8-section page as an Artifact, and the PR
  body becomes a five-line pointer to it. The maintainer reviews that page
  instead of the diff, so a PR that changes what Alvo can do does not get
  opened without one. Docs-only and dependency PRs skip it.
- **Also run Claude Code's built-in reviews as the local inner loop** (once
  there is product code to review — from F3 on). They are the general
  correctness/security pass that `alvo-plan-guard` deliberately is not (it
  only judges Alvo domain/plan drift):
  - `/code-review medium` — correctness bugs + reuse/simplify/efficiency.
    Use `low`/`medium` for the fast inner loop; `high`+ for a large or risky
    diff.
  - `/security-review` — an actual vulnerability scan (injection, authz
    flaws, insecure data handling). Run it **whenever the diff touches the
    security core** (rule engine, CEL, tenancy, auth/RBAC), paired with the
    `alvo-security-core-review` checklist.

  Fix findings *before* opening the PR. CodeRabbit and CodeQL are the
  outer-loop gate on the PR itself — not a substitute for reviewing first.

## Context pyramid — read big docs rarely, on demand

Layers get denser and rarer as you go down; start at the top on every task
and only descend when the layer above does not answer your question.

- `docs/PLAN.md` — where we are (`← YOU ARE HERE`) and the target end-state; check this first.
- `docs/design-brief.en.md` — the whole context in one breath (generated, EN).
- `docs/product/*.md` — full spec (`alvo-specifikacia.md`) + domain analysis (`baas-analyza.md`), SK, ~200 KB — read rarely, on demand.
- `docs/architecture/package-boundary.md` — the package-split rule, on demand when adding a package.

## Skills & guard

Domain discipline lives in `.claude/skills/alvo-*` (skills) and the read-only
`alvo-plan-guard` subagent (`.claude/agents/`). You don't invoke skills by
name and this file deliberately doesn't re-list them — the harness surfaces
each skill's `description` and it activates when a task touches its area. Two
things those descriptions won't tell you: packaging / licensing / test-stack /
**code-style conventions live in the `alvo-dotnet-conventions` skill, not
inline here**; and `alvo-plan-guard` is your pre-PR check (see Hard rules).

## Git hooks

Husky.Net, auto-installed on the first `dotnet build`/`dotnet restore` — no
manual `git config` step; `HUSKY=0` and CI skip them. **pre-commit**:
brief-freshness on a staged spec/analysis/brief (regenerate via
`alvo-regen-brief` if it fires), plus `dotnet format` and ring0 on staged code.
**commit-msg**: Conventional Commits. Details in `.husky/task-runner.json`.

## Claude Code hooks

Distinct from the Husky git hooks above: these run inside an agent's turn, not
around a commit. `.claude/hooks/record-edited-paths` (PostToolUse) records every
touched baseline into a per-turn ledger in the git dir — **two shapes qualify**,
a `*.verified.*` snapshot and a load baseline (`test/load/baselines/*.json`),
because both are files whose edit can turn a red check green with no product
change;
`.claude/hooks/turn-review-gate` (Stop) drains that ledger — always, before any
decision — and, if a baseline really moved, blocks the turn with an instruction
to dispatch the read-only `alvo-snapshot-judge`, because a baseline is the one
place a check can be made green with no product-code change: accept a snapshot,
or raise a load ceiling.
`reset-edited-paths` clears a ledger orphaned by an ungraceful exit. The gate is
a **registry**: add a future judgment check as a function inside
`turn-review-gate`, never as a second Stop hook (an event's hooks run in
parallel and would race the drain). Tests: `scripts/test-hooks`. Design:
`docs/superpowers/specs/2026-07-25-snapshot-judge-gate-design.md`.

## Always on

- **Package boundary** — a package is earned, not assumed; default to
  adding new code inside the core. Splitting early buys nothing but extra
  versioning and dependency surface. See `docs/architecture/package-boundary.md`.
- **Do not create projects ahead of time** — new projects appear when their
  turn comes, not preemptively. The core `MMLib.Alvo` project itself does
  not exist until it has real content — an empty scaffold is noise, not progress.
