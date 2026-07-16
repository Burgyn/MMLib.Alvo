# F1 — Testing infrastructure + CI gate (issues #9 + #10) — design

> Slice covering GitHub issues **#10 [6] Testing infrastructure** and **#9 [5]
> Build system + basic CI**, batched into one PR (per the F1 review
> walkthrough, 2026-07-15). This is the foundation every later F1 gate builds
> on. F1 **extends** existing scaffolding — it is not greenfield.

## Why these two together

#10 defines *how testing works* (rings, project conventions, the test-support
library, shared architecture rules). #9 is the CI that *runs* those rings as a
gate. #9 has nothing to run without #10, so they land together.

## Decisions locked in (from the walkthrough)

- **Test stack:** MTP (already selected in `global.json`) + xUnit v3 + Shouldly
  (Shouldly-only — no AwesomeAssertions) + NetArchTest. Heavier libraries
  (Testcontainers, Playwright, CsCheck, Vacuum) are **not** pulled in now; their
  CI steps are wired but skip when no such project exists yet.
- **Rings:** `test-ring0/1/2` get real content; add `.ps1` variants alongside
  the `.sh` ones (cross-platform, called by both agent and CI).
- **Two solution-wide test axes:**
  - **os A — structural conventions**, a dedicated `MMLib.Alvo.Conventions.Tests`
    that scans `*.csproj` / `.slnx` as **files** (`System.Xml.Linq`), **never
    loading assemblies / forcing a build**. Enforces: no inline package
    `Version` (CPM), no re-declared inherited MSBuild props, every project is in
    the `.slnx`, `MMLib.Alvo.*` naming, `src` never references `test`, and every
    **packable** `src` project has a matching `*.Tests` project.
  - **os B — type rules**, NetArchTest rules written once as **linked source**
    (`test/_shared/*.cs` linked into each test project via
    `test/Directory.Build.props`), compiled into each test assembly and run
    against that project's **sibling** production assembly. Target assembly =
    test-project name minus `.Tests`, overridable via `[assembly: ArchTarget]`.
    Opt-out via `AlvoSharedArchTests=false` for projects that don't map 1:1 to a
    production assembly. Chosen over one central test because linked-per-project
    composes with `dotnet-affected` (arch runs only for changed assemblies); a
    central test referencing all `src` would force building the whole solution.
- **`MMLib.Alvo.Testing`:** internal test-support library (`IsPackable=false`
  now; ships later when earned). Holds shared test-support *types* referenced by
  test projects — starts with `ArchTargetAttribute` and a repository-root
  locator; fakes and the contract-suite base arrive with real ports (F3). Naming
  vs `.Tests` follows the `Microsoft.AspNetCore.Mvc.Testing` convention.
- **Latent rules:** arch rules that need a not-yet-existing core/provider are
  written with `[Fact(Skip = "…F3")]`, not vacuum-passing (visible, not silently
  green).
- **CI (#9):** extend `.github/workflows/ci.yml` (do not recreate). Add a
  `dotnet format --verify-no-changes` gate (Linux only — formatting is
  OS-independent and this avoids line-ending flakiness), run the ring scripts as
  the test step (**never** `--nologo` — in MTP that is forwarded to the test app
  and breaks the run; **not** VSTest `--logger`), a **Linux + Windows** matrix
  with a stable `Build & test` aggregation check for the branch ruleset, and
  `dotnet-affected` wired to scope integration tests (skips cleanly while there
  are none). PR stays the single full gate.
- **`dotnet-affected`:** adopt as a dotnet-tool; fallback to a custom
  git-diff + project-graph script if it proves incompatible with net10/MTP.
- **Analyzers as a build gate** (issue #9 "analyzers as a gate"):
  `AnalysisLevel=latest-recommended` + `EnforceCodeStyleInBuild=true` in
  `Directory.Build.props`, and `Roslynator.Analyzers` wired via a CPM
  `GlobalPackageReference` — the RCS severities in `.editorconfig` predated the
  package and were inert until now. With `TreatWarningsAsErrors` these all fail
  the build. `test/.editorconfig` turns CA1707 off for test code only
  (snake_case test names are the documented convention).
- **Branch protection** already exists as an active repo ruleset; adding new
  required status-check names to it is a separate maintainer (admin) action,
  not part of this PR.

## Out of scope (later PRs)

- Public API approval gate + the remaining architecture rules → PR2 (#11 + #12).
- MinVer / pack / release skeleton → PR3 (#14).
- Stryker → PR4 (#13). Git hooks + PR template → PR5 (#15).
- The `alvo-testing` and `alvo-new-package` skills ship with the PRs that first
  rely on them, each with a `writing-skills` baseline test.
- **Test-result publishing (TRX/JUnit artifacts):** deferred to a follow-up. The
  gate is the ring scripts' exit code (a red run blocks merge); MTP-native TRX
  reporting needs the `Microsoft.Testing.Extensions.TrxReport` extension wired
  into the test projects, which is not pulled in yet. Actions logs already show
  the MTP run summary in the meantime.

## Definition of done

`dotnet test` runs green in CI on Linux + Windows; the ring scripts run on both
shells; the os A conventions test and os B shared arch rules execute (with
latent rules visibly skipped); a mis-formatted file fails the `dotnet format`
gate; integration/e2e CI steps are wired and skip cleanly with no such projects.
