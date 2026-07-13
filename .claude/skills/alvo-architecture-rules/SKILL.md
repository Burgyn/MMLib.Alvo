---
name: alvo-architecture-rules
description: Use when touching Alvo's architecture, ports, public API, package structure, or the descriptor — enforces the framework's structural rules.
---

# Alvo architecture rules

Alvo's structural rules exist to keep one framework working identically across
two distributions (standalone Docker, embedded NuGet) and two config paths
(GitOps file, dashboard DB record). Bending a rule "just this once" quietly
breaks that equivalence somewhere else in the system. Check every rule below
before touching architecture, ports, the public API, package structure, or the
descriptor.

Background: `docs/product/alvo-specifikacia.md` §0 (principles) and the ladder/
boundaries section; `docs/design-brief.en.md` (compressed English source);
`docs/architecture/package-boundary.md` (the package rule in full, spec §1.1).

## Two sources of truth, one format

The descriptor is one canonical JSON *format* that can *live* in two places:
a file in the repo (GitOps — Git is the source of truth, the UI/agent edits
the same files) or a record in the running instance's DB (dashboard-first —
written into the schema registry, no file). A bidirectional export/import
bridge connects them. This is not "everything is files" — it is "one format,
two sources of truth."

Why it matters: only the GitOps path gets git's audit/rollback for free. The
runtime path has to supply its own equivalent — append-only versioning,
rollback via a generated reverse migration, optimistic locking on the
descriptor (two admins editing = a conflict, not a silent overwrite). If you
add a descriptor-mutating feature, ask which path(s) it must work on before
writing it, and don't let one path get an escape hatch the other lacks.

## The computed / rollup / hook ladder

A derived value belongs on the rung that matches how it is computed — pushing
logic below the rung it belongs on reintroduces bugs the rung above prevents
by construction:

- **computed** — pure arithmetic/expression over fields of the *same row*
  (`total = unit_price * amount`). Implement as a **DB stored generated
  column**: read-only, computed by the database itself, unbypassable by any
  write path.
- **rollup** — aggregation over *related records* (`sum(items.line_total)`).
  Must be **transactionally consistent** — a DB trigger or an in-transaction
  recompute — and is a first-class declarative concept, **never a manual
  hook**. A hand-written "recompute parent sum" hook is a classic race
  condition: two concurrent child writes can both read the stale parent total.
- **conditional value at write** (e.g. "if `stage == 'won'` set `closed_at`")
  — a **before-hook `mutate`** with a CEL condition, not a computed field.
  It's conditional on write-time state, not a pure expression.
- **contextual / time-valued logic** (e.g. a VAT rate depending on country,
  supply type, and date of taxable supply) — a **hook or function**, not an
  expression. This is business logic, not arithmetic, and will keep changing;
  don't force it into a declarative expression shape it doesn't fit.
- **complex branching / external calls** — an **automation action**
  (post-commit) or a **csx function**.

Order: declarative → hook → action → csx is the single extensibility
gradient. When designing a feature, find the lowest rung that can express it;
when reviewing one, check nothing was implemented below the rung it belongs
on (e.g. a rollup done as a hook, or a computed field done as a hook).

## MCP is only an adapter

MCP is an optional adapter over the *one* Management API for external agents
against a running instance — the same API the dashboard and `alvo` CLI use.
It is never a parallel config path, never a building block, and never a
precondition for anything else to work: removing the MCP adapter must change
nothing structural. If a feature is designed such that it only works "through
MCP," that's a sign the feature was placed on the wrong side of the Management
API boundary — fix that instead of special-casing MCP.

## Minimal API, not MVC controllers

All endpoints — schema-generated and custom — use minimal API +
`RouteGroupBuilder`, never MVC controllers. Reasons: consistency with .NET 10
routing conventions, lower overhead, and — the decisive one — an endpoint as a
delegate can be **generated programmatically from the schema**; a controller
generated via reflection is a worse fit for that job. This applies equally to
custom endpoints added in the embedded host.

## Vertical slice inside packages — distinct from the package boundary

Organize code by *feature* (a slice like "create record" or "dispatch
webhook" keeps its endpoint + handler + validator + model together), not by
technical layer (`Controllers/`, `Services/`, `Validators/`). This is a rule
about organization *inside* a package.

Do not confuse it with the package boundary (spec §1.1,
`docs/architecture/package-boundary.md`), which is a different axis entirely:
that rule decides what becomes a separate **NuGet package / distribution
unit**, driven by a foreign/heavy dependency, a real swap point, or a
different license policy. Vertical slice never justifies splitting a package,
and the package boundary never justifies organizing by technical layer inside
one. Applying the wrong rule to a question ("should this be its own project?"
answered by feature-organization logic, or "how should this be organized?"
answered by dependency logic) produces the wrong answer either way.

## Encapsulation: `public` is the contract

Mark `public` only what is genuinely part of the contract; default to
`internal`/`private` for everything else. The public surface of the ports and
the core is the framework's published API — every `public` member is a thing
a consumer can now depend on and a thing the public-API approval gate
(PublicApiGenerator, spec §X) will flag as a breaking change if it moves or
disappears. Widening the surface is cheap to do and expensive to undo; narrow
by default and widen only when a real external caller needs it.

## Descriptor ≠ infra config

The descriptor *defines the backend*: entities, rules, automation, auth
settings, admin access mapping (via CEL), branding. Env/secrets *define
infrastructure*: bootstrap admin credentials, admin-portal enable/path, IP
allowlist, `ALVO_SCRIPTS_ALLOW_UI_EDIT`, connection strings, provider
selection. Never mix the two: access rules stay versioned in the descriptor;
credentials must never enter it. If you're adding a new setting, ask "does
this define the backend's behavior, or does it define where/how the backend
runs" — the answer picks the file it belongs in.

## A package is earned, not assumed

Default to adding new code inside the core. A standalone NuGet package is
justified only when a component meets at least one of: (a) it drags in a
foreign/heavy dependency most consumers don't want (Azure SDK, a DB driver,
Roslyn, Blazor); (b) it's a real swap point someone actually replaces (the DB
engine, a secret store); (c) it has a different distribution/license policy
(e.g. a commercial `Alvo.Enterprise.*` add-on vs. the Apache-2.0 core).
Conceptual neatness is not a reason to split. New projects are added when
their turn comes, not preemptively — the target is roughly ~10 packages for
v0.1, not 30+, because splitting a namespace out later is cheap and merging
packages back is a breaking change.

See `docs/architecture/package-boundary.md` for the full rule, the current
project list, and the hard dependency rules (`Abstractions` depends on
nothing; the core depends only on `Abstractions`; no package depends on
another port's provider; lockstep SemVer). See spec §1.1 for the source
rationale.
