# F1 — Mutation testing + git hooks & PR process (issues #13 + #15) — design

> Folded into PR #59 with #11/#12/#14 (maintainer: "do it all in this PR"), which
> completes the F1 milestone. Issues **#13 [9] Stryker.NET** and **#15 [11] Git
> hooks + PR process**.

## #13 — Stryker mutation gate (wired, dormant)

The security core (rule engine, CEL, tenancy) does **not exist yet** — it lands
in F3 — so there is nothing to mutate today (decision Q6: wire the mechanism now
as a no-op, break threshold **80**).

- **`dotnet-stryker`** (4.16.0) as a tool in `.config/dotnet-tools.json`.
- **`stryker-config.json`** — `mutate` globs scoped to the future security core
  (`src/MMLib.Alvo/**` — the core project, not the whole repo), `thresholds`
  `break: 80`. Deliberately narrow; mutation never runs on provider boilerplate.
- **`.github/workflows/mutation.yml`** — `on: pull_request: paths:` restricted to
  the core paths. Those paths don't exist yet, so the workflow **never triggers**
  until the core lands; a guard step also skips cleanly if the core project is
  absent. When F3 adds the core, PRs touching it run path-filtered mutation.
- Verifiable now: the tool installs, the config is valid JSON, the workflow YAML
  lints. The actual mutation run (and confirming Stryker's MTP integration end to
  end) is exercised when the core exists — flagged for F3.

## #15 — Git hooks + PR process

- **`.githooks/pre-commit`** (extend the existing brief-freshness hook): when a
  commit stages code (`*.cs`/`*.csproj`/`*.props`/`*.targets`), run
  `dotnet format --verify-no-changes` + `scripts/test-ring0` (fast tests). Docs-
  only commits stay fast (skipped). Requires the one-time
  `git config core.hooksPath .githooks` already documented in CLAUDE.md.
- **`.githooks/commit-msg`** — validate the subject against Conventional Commits
  (the changelog in #14 is generated from them; Q5). Merge/revert/fixup subjects
  are exempt.
- **`.github/pull_request_template.md`** — issue link, DoD checklist, and the
  "touches the security core? → `needs-deep-review` + `alvo-security-core-review`"
  prompt.
- **Branch protection** already exists as a ruleset, and
  `scripts/ci/update-required-checks.sh` (shipped in #58) keeps its required
  checks current — the maintainer runs it. No code change needed here.

## Out of scope / deferred

- Real mutation runs + Stryker↔MTP end-to-end confirmation → when the core exists
  (F3). Real NuGet publish → v0.1. Pre-push variant of the test hook if the
  pre-commit run becomes slow as the suite grows.

## Definition of done

`dotnet-stryker` installs and `stryker-config.json`/`mutation.yml` are valid and
dormant (no core to mutate); a commit that stages malformed C# is caught locally
by the pre-commit format/test gate; a non-Conventional-Commit subject is rejected
by `commit-msg`; the PR template prompts the security-core question. With this,
**F1 is complete** (#9,#10 in #58; #11,#12,#13,#14,#15 in #59).
