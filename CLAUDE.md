# MMLib.Alvo — working agreements for agents

You are an expert at building .NET frameworks and libraries — you care about
a clean public API, encapsulation, backward compatibility, DX, and idiomatic
.NET patterns.

## What Alvo is

Alvo is a .NET-native Backend-as-a-Service for the agentic era. It ships as
the NuGet family `MMLib.Alvo.*` — one core package today, more earned over
time as real boundaries appear. It runs in two primary modes on one
codebase: standalone (a Docker image with a dashboard and CLI/Management
API) or embedded (a NuGet package inside your own host). Every backend —
entities, rules, automation, webhooks — is driven by one JSON project
descriptor, whether it lives as a repo file (GitOps) or a DB record
(dashboard-first). The primary user is a coding agent, not a human clicking
through a wizard: declarative config, structured errors with fix
suggestions, and idempotent operations throughout.

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
- `docs/superpowers/specs/` — per-issue specs (the what/why for one issue).
- `docs/superpowers/plans/` — per-issue Superpowers implementation plans (the how, for one PR).
- `.claude/skills/` — the `alvo-*` skills (see below).
- `.claude/agents/` — subagents, e.g. `alvo-plan-guard`.
- `scripts/` — `test-ring0`/`test-ring1`/`test-ring2` plus `check-brief-freshness`.
- `.githooks/` — the brief-freshness pre-commit hook.
- `.github/` — CI workflows; this is where the full run (mutation + e2e) lives.

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
| full (+ mutation + e2e) | CI on the PR | never run locally |

Each ring wraps the previous one and adds a layer: ring1 adds architecture
tests (already inside `dotnet test`) and, once it lands, public-API
approval; ring2 adds affected-scoped integration tests, the API invariant
check, and Vacuum. See each script's own comments for what is a placeholder
today.

## Hard rules

- **NEVER merge or push directly to `main`.** Branch → PR → a human merges
  after review.
- **The PR is the only full gate** — there is no nightly or post-merge
  safety net catching what the PR missed.
- **Before opening a PR, dispatch the `alvo-plan-guard` subagent** as the
  last check — it flags drift from `docs/PLAN.md`, violated §0 principles,
  and shortcuts in the security core. It is read-only and advisory: it
  reports a verdict, it does not fix, tidy, or commit anything itself.
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

The `alvo-*` skills activate automatically when a task touches their area —
you should not need to invoke them by name:

- `alvo-architecture-rules` — ports, public API, package structure, the descriptor.
- `alvo-security-core-review` — rule engine, tenancy, CEL compilation, authorization; runs a deep-review checklist and marks the change `needs-deep-review`.
- `alvo-schema-testing` — tests against the descriptor JSON Schema.
- `alvo-dotnet-conventions` — packaging, licensing, test stack, and code-style conventions live here now, **not** inline in this file.
- `alvo-regen-brief` — regenerate `docs/design-brief.en.md` when the spec/analysis sources change.

## One-time setup

`git config core.hooksPath .githooks` — enables the pre-commit hook that
blocks a commit touching the spec/analysis or the brief while
`docs/design-brief.en.md` is stale against its sources (checked via
`scripts/check-brief-freshness`). If it fires, regenerate via the
`alvo-regen-brief` skill and re-commit.

## Always on

- **Package boundary** — a package is earned, not assumed; default to
  adding new code inside the core. Splitting early buys nothing but extra
  versioning and dependency surface. See `docs/architecture/package-boundary.md`.
- **Do not create projects ahead of time** — new projects appear when their
  turn comes, not preemptively. The core `MMLib.Alvo` project itself does
  not exist until it has real content — an empty scaffold is noise, not progress.
