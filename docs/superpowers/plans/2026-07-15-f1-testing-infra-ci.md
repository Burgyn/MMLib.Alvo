# F1 Testing infrastructure + CI gate — Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Give F1 a working testing foundation (rings, conventions test, shared architecture rules, test-support lib) and a CI gate that runs it on Linux + Windows.

**Architecture:** Extend existing scaffolding. Two solution-wide test axes — os A (file-scanning conventions, no build) in a dedicated `Conventions.Tests`, and os B (NetArchTest type rules) as linked source compiled per test project against its sibling assembly. Ring scripts orchestrate; CI calls the rings.

**Tech Stack:** net10.0, Microsoft.Testing.Platform, xUnit v3, Shouldly, NetArchTest, `System.Xml.Linq`, `dotnet-affected` (tool), GitHub Actions.

## Global Constraints

- Target framework `net10.0`; SDK pinned `10.0.100` (`global.json`).
- MTP runner (`global.json` `test.runner`). Run tests with plain `dotnet test`; **never** pass `--nologo` (forwarded to the test app under MTP → breaks the run); no VSTest `--logger`.
- Central Package Management: no inline `Version` on `PackageReference`; versions in `Directory.Packages.props`.
- Shared MSBuild props inherited from `Directory.Build.props` — never re-declare `TargetFramework`/`Nullable`/etc. per project.
- `TreatWarningsAsErrors=true` already on — code must be warning-clean.
- Apache-2.0-compatible dependencies only. Shouldly for assertions (no FluentAssertions v8+, no AwesomeAssertions).
- New projects created via `dotnet` CLI, then generated `.csproj` stripped of inherited props.
- Never push/merge to `main`; end on an open PR.

---

### Task 1: `MMLib.Alvo.Testing` test-support skeleton

**Files:**
- Create: `src/MMLib.Alvo.Testing/MMLib.Alvo.Testing.csproj` (`IsPackable=false`)
- Create: `src/MMLib.Alvo.Testing/ArchTargetAttribute.cs`
- Create: `src/MMLib.Alvo.Testing/RepositoryRoot.cs`
- Modify: `MMLib.Alvo.slnx` (add project)

**Interfaces produced:**
- `[assembly: ArchTarget("MMLib.Alvo.X")]` — assembly attribute naming the production assembly a test project's shared arch rules target.
- `RepositoryRoot.Find()` → `string` — walks up from `AppContext.BaseDirectory` to the dir containing `MMLib.Alvo.slnx`; throws if not found.

- [ ] Create project via `dotnet new classlib -n MMLib.Alvo.Testing -o src/MMLib.Alvo.Testing`, strip inherited props from the csproj, set `<IsPackable>false</IsPackable>`.
- [ ] `dotnet sln MMLib.Alvo.slnx add src/MMLib.Alvo.Testing/MMLib.Alvo.Testing.csproj`.
- [ ] Implement `ArchTargetAttribute` (`[AttributeUsage(AttributeTargets.Assembly)]`, one `string TargetAssemblyName` prop) and `RepositoryRoot.Find()`.
- [ ] `dotnet build src/MMLib.Alvo.Testing` — expect success.
- [ ] Commit.

### Task 2: os B — shared linked architecture rules

**Files:**
- Create: `test/_shared/SharedArchitectureRules.cs`
- Modify: `test/Directory.Build.props` (link `_shared/*.cs` when `AlvoSharedArchTests != false`; add `ProjectReference` to `MMLib.Alvo.Testing`)
- Modify: `test/MMLib.Alvo.Abstractions.Tests/ArchitectureTests.cs` (keep the Abstractions-specific "depends on nothing" rule as a local test; universal rules move to shared)

**Interfaces consumed:** `ArchTargetAttribute`, `RepositoryRoot` (Task 1).

- [ ] In `test/Directory.Build.props`: add `<ProjectReference Include="…/src/MMLib.Alvo.Testing/…" />` and, guarded by `Condition="'$(AlvoSharedArchTests)' != 'false'"`, `<Compile Include="$(MSBuildThisFileDirectory)_shared/*.cs" Link="_shared/%(Filename)%(Extension)" />`.
- [ ] Write `SharedArchitectureRules` (concrete class): resolve target assembly = `[assembly:ArchTarget]` if present, else own assembly name minus `.Tests`; `Assembly.Load(target)`.
  - Active rule: `Public_types_do_not_live_in_Internal_namespaces` — `Types.InAssembly(target).That().ResideInNamespaceContaining(".Internal").ShouldNot().BePublic()` (passes trivially on empty Abstractions).
  - Latent rule: `[Fact(Skip="ožije s core — F3")] Core_depends_only_on_Abstractions`.
- [ ] `dotnet test test/MMLib.Alvo.Abstractions.Tests` — expect green (shared active rule runs against Abstractions, latent skipped, local Abstractions rule passes).
- [ ] Commit.

### Task 3: os A — `MMLib.Alvo.Conventions.Tests`

**Files:**
- Create: `test/MMLib.Alvo.Conventions.Tests/MMLib.Alvo.Conventions.Tests.csproj` (`AlvoSharedArchTests=false` — no sibling assembly)
- Create: `test/MMLib.Alvo.Conventions.Tests/SolutionConventionTests.cs`
- Modify: `MMLib.Alvo.slnx`

**Interfaces consumed:** `RepositoryRoot.Find()` (Task 1).

- [ ] Create via `dotnet new`, strip inherited props, set `<AlvoSharedArchTests>false</AlvoSharedArchTests>`, add to slnx.
- [ ] Write tests scanning `RepositoryRoot.Find()`:
  - `No_project_pins_an_inline_package_version` — no `PackageReference/@Version` and no `<Version>` in any `*.csproj`.
  - `No_project_redeclares_inherited_props` — no `<TargetFramework>`/`<Nullable>`/`<ImplicitUsings>` in project csproj (they inherit).
  - `Every_project_is_registered_in_the_solution` — every `**/MMLib.Alvo.*.csproj` appears in `MMLib.Alvo.slnx`.
  - `All_projects_follow_the_family_naming` — every project dir/name matches `MMLib.Alvo.*`.
  - `Src_projects_do_not_reference_test_projects`.
  - `Every_packable_src_project_has_a_tests_project` — for each `src` csproj without `IsPackable=false`, a `*.Tests` project exists.
- [ ] `dotnet test test/MMLib.Alvo.Conventions.Tests` — expect green.
- [ ] Commit.

### Task 4: Ring scripts (`.sh` + `.ps1`)

**Files:**
- Modify: `scripts/test-ring0`, `scripts/test-ring1`, `scripts/test-ring2`
- Create: `scripts/test-ring0.ps1`, `scripts/test-ring1.ps1`, `scripts/test-ring2.ps1`

- [ ] ring0 = `dotnet test` (fast, all current tests). ring1 = ring0 (arch + conventions already inside `dotnet test`; public-API approval note = PR2). ring2 = ring1 + integration step that globs `**/*.Tests.Integration.csproj` and runs only if any exist (else logs "none, skipping") + Vacuum note (PR later).
- [ ] Mirror each in a `.ps1` (same behavior, PowerShell).
- [ ] Run `bash scripts/test-ring2` locally — expect green.
- [ ] Commit.

### Task 5: CI extension + `dotnet-affected`

**Files:**
- Modify: `.github/workflows/ci.yml`
- Create: `.config/dotnet-tools.json` (tool manifest with `dotnet-affected`)

- [ ] `dotnet new tool-manifest`; `dotnet tool install dotnet-affected`. Verify it runs on net10 (`dotnet affected --help`); if broken, drop the tool and add a `scripts/affected` git-diff fallback + note.
- [ ] Extend `ci.yml` `build-and-test` job: `strategy.matrix.os: [ubuntu-latest, windows-latest]`, `runs-on: ${{ matrix.os }}`; add `dotnet format --verify-no-changes` step (after restore); replace the bare test step with `scripts/test-ring2` (bash on both — GitHub Windows runners have bash); publish TRX via MTP reporting flags (`dotnet test -- --report-trx --results-directory TestResults` — verify exact flag) uploaded with `actions/upload-artifact`.
- [ ] Keep `brief-freshness` job as-is.
- [ ] Commit.

### Task 6: Format + full verify

- [ ] `dotnet format` (whole solution), then `dotnet format --verify-no-changes` → clean.
- [ ] `dotnet build -c Release` + `bash scripts/test-ring2` → green.
- [ ] Commit.
- [ ] Dispatch `alvo-plan-guard`; address any blocking findings.
- [ ] Push branch, open PR (do **not** merge).

## Self-review notes

- Spec coverage: #10 rings/conventions/shared-arch/test-lib → Tasks 1–4; #9 CI/format/matrix/affected → Task 5. ✓
- Latent rules use `Skip`, not vacuum-pass (Task 2). ✓
- No empty product projects created (integration/e2e wired-and-skip in Task 4). ✓
