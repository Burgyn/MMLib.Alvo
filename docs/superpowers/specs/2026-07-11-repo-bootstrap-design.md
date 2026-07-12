# Design — Repo bootstrap + solution skeleton (issue #1)

> Source issue: [#1] Repo bootstrap + solution skeleton (label `ready`).
> Parent spec: `specs/alvo-specifikacia.md` §0.5, §1.1, §3, §X. Domain analysis: `specs/baas-analyza.md` §1.
> Status: **approved in brainstorming**, awaiting implementation plan.

## Goal

Lay the foundation the whole `MMLib.Alvo.*` family stands on: a cloneable repo that
builds and tests green on a clean machine, with the conventions and the first
architectural guard-rail wired in from commit one. The scope is deliberately minimal
and **interface-first** — no ports, no core, no premature empty shells.

## Guiding decisions (from the issue + brainstorming)

1. **First project is `MMLib.Alvo.Abstractions`, not the core `MMLib.Alvo`.** Abstractions
   is the true root of the dependency graph (spec §1.1: Abstractions depends on nothing;
   the core depends only on it) and is *correctly* empty — waiting for interfaces — rather
   than a provisionally empty shell. The core `MMLib.Alvo` is **not** created here; it
   emerges later (issue [6]/[14]) once it has real content.
2. **The first test is a real architectural rule, not a placeholder.** A NetArchTest rule —
   *"Abstractions depends on no other project in the solution"* — is wired from commit 1 and
   grows as projects are added.
3. **Central Package Management + `Directory.Build.props`** manage shared settings and
   lockstep NuGet versions for the family (spec §X: lockstep SemVer). Kept minimal now;
   detail is added in later issues.
4. **`.slnx` solution format** (new .NET 10 XML format — no GUIDs, merge-friendly).
5. **Minimal CI (`build` + `test` on `pull_request`) from the first PR.** Needed now, not
   only in [5], because branch protection requires an *existing* status check before it can
   be marked required. [5] later extends this same workflow into the full gate.
6. **Package boundary is written as a *rule*, not a finished project list** (spec §1.1),
   in a root `CLAUDE.md` (agent-facing) that links to `docs/architecture/package-boundary.md`.

## Target repo layout

```
MMLib.Alvo/
├─ .github/workflows/ci.yml              # build + test on pull_request (minimal gate)
├─ docs/architecture/package-boundary.md # §1.1 rule + illustrative example
├─ specs/                                # (existing) product specs — untouched
├─ src/
│  └─ MMLib.Alvo.Abstractions/
│     └─ MMLib.Alvo.Abstractions.csproj  # net10.0 class lib, no source yet
├─ test/
│  └─ MMLib.Alvo.Abstractions.Tests/
│     ├─ MMLib.Alvo.Abstractions.Tests.csproj
│     └─ ArchitectureTests.cs            # the one real NetArchTest rule
├─ .editorconfig                         # (existing)
├─ .gitignore                            # (existing)
├─ CHANGELOG.md                          # Keep a Changelog
├─ CLAUDE.md                             # agent conventions + boundary rule + build/test howto
├─ Directory.Build.props                 # shared MSBuild settings
├─ Directory.Packages.props              # Central Package Management (versions)
├─ global.json                           # pin SDK 10.0.100, rollForward latestFeature
├─ LICENSE                               # Apache-2.0
└─ MMLib.Alvo.slnx                        # .slnx solution
```

## Component detail

### global.json

Pin the installed SDK with tolerant forward-roll inside the feature band:

- `sdk.version = 10.0.100`
- `sdk.rollForward = latestFeature`

Reproducible builds, yet tolerant of patched 10.0.x SDKs on CI / other machines.

### Directory.Build.props (shared MSBuild settings)

Kept intentionally lean — packaging metadata and analyzer/warning gates are added in later
issues.

- `TargetFramework = net10.0`
- `Nullable = enable`
- `ImplicitUsings = enable`
- `LangVersion = latest`
- `VersionPrefix = 0.1.0` — a single lockstep version for the whole family.

### Directory.Packages.props (Central Package Management)

- `ManagePackageVersionsCentrally = true`
- Pinned `PackageVersion` entries for the test dependencies only (nothing else exists yet):
  xUnit v3, NetArchTest.Rules, Shouldly. Exact package IDs/versions confirmed against
  current docs at implementation time.

### src/MMLib.Alvo.Abstractions

A `net10.0` class library with **zero source files** — it compiles to an empty assembly.
This is the interface-first root; no ports/interfaces are defined here yet (those are
designed in phase 1 brainstorming, spec §1.2).

### test/MMLib.Alvo.Abstractions.Tests

- xUnit v3 wired for **Microsoft.Testing.Platform** — `dotnet test` runs in MTP mode, not
  VSTest (spec §X.3). The exact wiring (`xunit.v3` package, `TestingPlatformDotnetTestSupport`
  and related properties) is verified against current docs at implementation time.
- References: `MMLib.Alvo.Abstractions` (ProjectReference), `NetArchTest.Rules`, `Shouldly`
  (spec's assertion pick, §3).

**`ArchitectureTests.cs` — the one real rule.** Loads the Abstractions assembly *by name*
(keeping Abstractions source-free) and asserts via NetArchTest that no type in it depends on
any other `MMLib.Alvo.*` assembly. Today it is vacuously green (empty assembly, no sibling
projects), but the rule is wired from commit 1 and becomes load-bearing the moment interfaces
and sibling projects arrive — exactly the issue's intent (a real growing rule, not a
`1+1=2` placeholder). Assertion via Shouldly (`result.IsSuccessful.ShouldBeTrue(...)`).

### MMLib.Alvo.slnx

`.slnx` solution listing the two projects, grouped under `/src/` and `/test/` solution
folders.

### .github/workflows/ci.yml

- Trigger: **`pull_request` only** — no push/nightly (spec §X.3: "PR is the only full gate").
- Runner: `ubuntu-latest`.
- Steps: checkout → `actions/setup-dotnet` (reads `global.json`) → `dotnet build` →
  `dotnet test`.
- This is the status check that issue [5] later marks as *required* for branch protection.

### CLAUDE.md (root, agent-facing)

- Summary of the package-boundary rule (§1.1) with a link to the full doc.
- The "further projects are not created ahead of time" rule.
- Build/test commands (`dotnet build`, `dotnet test`).
- Pointers to `specs/` (product specs) and `docs/`.

### docs/architecture/package-boundary.md

The full §1.1 rule: a component earns its own NuGet package only if it (a) pulls a heavy /
unwanted foreign dependency, (b) is a real swap point someone actually replaces, or (c) has a
different distribution/license policy. Everything else lives as a namespace / vertical slice
inside the core. Includes the non-binding illustrative example list and the hard dependency
rules (Abstractions depends on nothing; core depends only on Abstractions; no package depends
on another port's provider; lockstep SemVer).

### LICENSE / CHANGELOG.md

- `LICENSE` — full Apache-2.0 text (core is Apache-2.0 from the first commit; open-core model,
  commercial only future `Alvo.Enterprise.*` add-ons + hosting).
- `CHANGELOG.md` — Keep a Changelog format, `## [Unreleased]` section describing the bootstrap.

## Out of scope (deferred to later issues)

- The core `MMLib.Alvo` project and any port/interface content (phase 1, §1.2).
- `MMLib.Alvo.Testing` (contract suite + fakes — a shipped product, issue [6]).
- README (issue [2]).
- NuGet packaging metadata (Authors, license expression, repo URL, etc.).
- Full CI: rings / `dotnet-affected` / DB matrix / mutation / Playwright / Vacuum / TeaPie
  and branch protection wiring (issue [5]).

## Definition of Done

- Repo is cloneable.
- `dotnet build` and `dotnet test` pass on a clean machine — Abstractions builds, the
  NetArchTest rule is green.
- The minimal CI workflow runs on PR.
- Solution is `.slnx`.
- Package boundary is written as a rule (not a finished project list).
- `LICENSE` (Apache-2.0) and `CHANGELOG.md` are in place.

## Open implementation-time confirmations (design is fixed; only exact IDs/wiring)

- Exact xUnit v3 + Microsoft.Testing.Platform package IDs and MSBuild properties.
- Exact `.slnx` authoring path (`dotnet sln` vs hand-written XML) under SDK 10.0.100.
