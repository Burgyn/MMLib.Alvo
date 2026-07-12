# Repo bootstrap + solution skeleton — Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Bootstrap the `MMLib.Alvo` repository into a cloneable solution that builds and tests green on a clean machine, with the family conventions and the first architectural guard-rail wired in from the first commit.

**Architecture:** Interface-first. The only production project is `MMLib.Alvo.Abstractions` — the root of the dependency graph — which is intentionally source-free (waiting for ports designed in a later phase). A single xUnit v3 test project on Microsoft.Testing.Platform holds one real NetArchTest rule that enforces "Abstractions depends on no other project in the solution." Shared build settings and package versions are centralized. Everything ships under one lockstep version.

**Tech Stack:** .NET 10 SDK (10.0.100), C# / `net10.0`, `.slnx` solution, Central Package Management, xUnit v3 on Microsoft.Testing.Platform, NetArchTest.Rules, Shouldly, GitHub Actions.

## Global Constraints

Every task's requirements implicitly include this section. Values are copied verbatim from the spec (`specs/alvo-specifikacia.md`) and issue #1.

- **.NET SDK:** pinned to `10.0.100`, `rollForward: latestFeature`.
- **Target framework:** `net10.0` for all projects.
- **Versioning:** lockstep SemVer — one `VersionPrefix` (`0.1.0`) for the whole `MMLib.Alvo.*` family.
- **Solution format:** `.slnx` (the .NET 10 default), not `.sln`.
- **Package versions:** Central Package Management — versions live only in `Directory.Packages.props`; `PackageReference` entries carry no `Version`.
- **Test platform:** Microsoft.Testing.Platform (MTP), **not** VSTest. Framework: xUnit v3. Assertions: **Shouldly** (not FluentAssertions v8+ — commercial). Architecture rules: NetArchTest.Rules.
- **License:** Apache-2.0 for the core, present from the first commit.
- **Package boundary (hard rule, spec §1.1):** a component earns its own NuGet package only if it (a) pulls a heavy/foreign dependency, (b) is a real swap point, or (c) has a different distribution/license policy — otherwise it is a namespace/vertical slice inside the core. `Abstractions` depends on nothing; the core depends only on `Abstractions`; no package depends on another port's provider.
- **Do not create projects ahead of time.** The core `MMLib.Alvo` project is **not** created in this issue.
- **Forbidden libraries (licensing):** MediatR, FluentAssertions v8+.
- **Branching:** direct pushes to `main` are forbidden; work lands via reviewed PR. CI triggers on `pull_request` only.

**Working branch:** `repo-bootstrap` (already created; the design doc for this issue is already committed there).

---

### Task 1: Repo foundation (build infrastructure + solution + Abstractions project)

Creates the pinned SDK, shared build settings, central package versions, the license and changelog, the `.slnx` solution, and the empty `MMLib.Alvo.Abstractions` project. Deliverable: `dotnet build` is green and the governance files are in place.

**Files:**
- Create: `global.json`
- Create: `Directory.Build.props`
- Create: `Directory.Packages.props`
- Create: `LICENSE`
- Create: `CHANGELOG.md`
- Create: `src/MMLib.Alvo.Abstractions/MMLib.Alvo.Abstractions.csproj`
- Create: `MMLib.Alvo.slnx` (generated via CLI)

**Interfaces:**
- Consumes: nothing (first task).
- Produces:
  - Assembly `MMLib.Alvo.Abstractions` (root namespace `MMLib.Alvo.Abstractions`), a `net10.0` class library, source-free.
  - Central package versions available to consumers: `xunit.v3` = `3.2.2`, `NetArchTest.Rules` = `1.3.2`, `Shouldly` = `4.3.0`.
  - `MMLib.Alvo.slnx` at repo root containing the Abstractions project under a `/src/` solution folder.

- [ ] **Step 1: Create `global.json`** (pins the SDK and selects native MTP mode for `dotnet test`)

```json
{
  "sdk": {
    "version": "10.0.100",
    "rollForward": "latestFeature"
  },
  "test": {
    "runner": "Microsoft.Testing.Platform"
  }
}
```

- [ ] **Step 2: Create `Directory.Build.props`** (shared MSBuild settings — kept minimal; expands in later issues)

```xml
<Project>

  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
    <LangVersion>latest</LangVersion>
    <Nullable>enable</Nullable>
    <ImplicitUsings>enable</ImplicitUsings>
    <!-- Single lockstep version for the whole MMLib.Alvo.* family (spec §X). -->
    <VersionPrefix>0.1.0</VersionPrefix>
  </PropertyGroup>

</Project>
```

- [ ] **Step 3: Create `Directory.Packages.props`** (Central Package Management — only the test dependencies exist yet)

```xml
<Project>

  <PropertyGroup>
    <ManagePackageVersionsCentrally>true</ManagePackageVersionsCentrally>
  </PropertyGroup>

  <ItemGroup>
    <PackageVersion Include="xunit.v3" Version="3.2.2" />
    <PackageVersion Include="NetArchTest.Rules" Version="1.3.2" />
    <PackageVersion Include="Shouldly" Version="4.3.0" />
  </ItemGroup>

</Project>
```

- [ ] **Step 4: Create the Apache-2.0 `LICENSE`**

Fetch the canonical text (do not hand-type it):

Run: `curl -fsSL https://www.apache.org/licenses/LICENSE-2.0.txt -o LICENSE`
Then verify: `head -2 LICENSE`
Expected: the file begins with a line containing `Apache License` and a line containing `Version 2.0, January 2004`.

If the machine has no network, fall back to: `gh api /licenses/apache-2.0 --jq .body > LICENSE` and verify the same two lines.

- [ ] **Step 5: Create `CHANGELOG.md`** (Keep a Changelog format)

```markdown
# Changelog

All notable changes to this project are documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/),
and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [Unreleased]

### Added

- Repository and solution skeleton: `MMLib.Alvo.Abstractions` (the interface-first
  root of the dependency graph) and its test project.
- Central Package Management, shared build settings, pinned .NET SDK, `.slnx` solution.
- First architectural guard-rail (NetArchTest): Abstractions depends on no other
  project in the solution.
- Apache-2.0 license and minimal pull-request CI (build + test).
```

- [ ] **Step 6: Create the Abstractions project file** `src/MMLib.Alvo.Abstractions/MMLib.Alvo.Abstractions.csproj`

The project is intentionally source-free — `TargetFramework` and the rest are inherited from `Directory.Build.props`. It compiles to an empty assembly.

```xml
<Project Sdk="Microsoft.NET.Sdk">

  <!--
    The interface-first root of the MMLib.Alvo.* dependency graph (spec §1.1).
    Intentionally contains no source yet — ports/interfaces are designed in a
    later phase (spec §1.2). It compiles to an empty assembly.
  -->

</Project>
```

- [ ] **Step 7: Create the `.slnx` solution and add the project** (CLI generates a correct `.slnx`)

Run:
```bash
dotnet new sln --name MMLib.Alvo
dotnet sln MMLib.Alvo.slnx add src/MMLib.Alvo.Abstractions/MMLib.Alvo.Abstractions.csproj --solution-folder src
```
Expected: `MMLib.Alvo.slnx` created; `Project ... added to the solution.`

- [ ] **Step 8: Build to verify the foundation is green**

Run: `dotnet build MMLib.Alvo.slnx`
Expected: `Build succeeded` with `0 Error(s)`. `MMLib.Alvo.Abstractions.dll` is produced under `src/MMLib.Alvo.Abstractions/bin/`.

- [ ] **Step 9: Commit**

```bash
git add global.json Directory.Build.props Directory.Packages.props LICENSE CHANGELOG.md src MMLib.Alvo.slnx
git commit -m "chore: bootstrap solution foundation (Abstractions, CPM, license)

Claude-Session: https://claude.ai/code/session_0188Gs6GcPUbra9ndnUtJCte"
```

---

### Task 2: Architecture test project + first NetArchTest rule

Adds the xUnit v3 (MTP) test project and the single real architectural rule. Deliverable: `dotnet test` is green and runs exactly one real test.

> **Note on TDD for this task:** the rule is an *invariant guard*, green by construction — the structure it protects (an empty Abstractions with no sibling references) is already correct after Task 1, so there is no meaningful "red" phase. Instead of a contrived failure, we verify the test (a) executes (exactly 1 test, not 0 — guarding against a vacuous no-test run) and (b) passes. The rule gains teeth automatically the moment a type in Abstractions references a sibling `MMLib.Alvo.*` assembly.

**Files:**
- Create: `test/MMLib.Alvo.Abstractions.Tests/MMLib.Alvo.Abstractions.Tests.csproj`
- Create: `test/MMLib.Alvo.Abstractions.Tests/ArchitectureTests.cs`
- Modify: `MMLib.Alvo.slnx` (add the test project via CLI)

**Interfaces:**
- Consumes: assembly `MMLib.Alvo.Abstractions` (via ProjectReference); central package versions `xunit.v3`, `NetArchTest.Rules`, `Shouldly` from Task 1.
- Produces: test class `MMLib.Alvo.Abstractions.Tests.ArchitectureTests` with `[Fact] public void Abstractions_depends_on_no_other_project_in_the_solution()`.

- [ ] **Step 1: Create the test project file** `test/MMLib.Alvo.Abstractions.Tests/MMLib.Alvo.Abstractions.Tests.csproj`

xUnit v3 test projects are standalone executables (`OutputType=Exe`); MTP is provided by the `xunit.v3` package and selected as the `dotnet test` runner via `global.json`. No `xunit.runner.visualstudio` and no `TestingPlatformDotnetTestSupport` needed.

```xml
<Project Sdk="Microsoft.NET.Sdk">

  <PropertyGroup>
    <OutputType>Exe</OutputType>
    <IsPackable>false</IsPackable>
  </PropertyGroup>

  <ItemGroup>
    <PackageReference Include="xunit.v3" />
    <PackageReference Include="NetArchTest.Rules" />
    <PackageReference Include="Shouldly" />
  </ItemGroup>

  <ItemGroup>
    <ProjectReference Include="..\..\src\MMLib.Alvo.Abstractions\MMLib.Alvo.Abstractions.csproj" />
  </ItemGroup>

</Project>
```

- [ ] **Step 2: Write the test** `test/MMLib.Alvo.Abstractions.Tests/ArchitectureTests.cs`

```csharp
using System;
using System.Linq;
using System.Reflection;
using NetArchTest.Rules;
using Shouldly;
using Xunit;

namespace MMLib.Alvo.Abstractions.Tests;

public class ArchitectureTests
{
    // Every project that ships as its own assembly shares this root prefix.
    private const string FamilyPrefix = "MMLib.Alvo";
    private const string AbstractionsAssemblyName = "MMLib.Alvo.Abstractions";

    // Architectural invariant (spec §1.1): MMLib.Alvo.Abstractions is the root of
    // the dependency graph — it may depend on NO other project in the solution.
    // Wired from the first commit; stays green today (Abstractions is empty) and
    // becomes load-bearing the moment a type here references a sibling package.
    [Fact]
    public void Abstractions_depends_on_no_other_project_in_the_solution()
    {
        var abstractions = Assembly.Load(AbstractionsAssemblyName);

        // Sibling MMLib.Alvo.* assemblies that Abstractions actually references
        // (everything except itself). This set must always be empty.
        var forbiddenSiblings = abstractions
            .GetReferencedAssemblies()
            .Select(name => name.Name!)
            .Where(name => name.StartsWith(FamilyPrefix, StringComparison.Ordinal))
            .Where(name => name != AbstractionsAssemblyName)
            .ToArray();

        if (forbiddenSiblings.Length == 0)
        {
            // No sibling references at the assembly level — invariant holds.
            return;
        }

        // A sibling is referenced: use NetArchTest to name the offending types.
        var result = Types.InAssembly(abstractions)
            .Should()
            .NotHaveDependencyOnAny(forbiddenSiblings)
            .GetResult();

        result.IsSuccessful.ShouldBeTrue(
            "MMLib.Alvo.Abstractions must not depend on any other project in the " +
            $"solution, but references: {string.Join(", ", forbiddenSiblings)}. " +
            "Offending types: " +
            string.Join(", ", (result.FailingTypes ?? Enumerable.Empty<Type>())
                .Select(t => t.FullName)));
    }
}
```

- [ ] **Step 3: Add the test project to the solution**

Run: `dotnet sln MMLib.Alvo.slnx add test/MMLib.Alvo.Abstractions.Tests/MMLib.Alvo.Abstractions.Tests.csproj --solution-folder test`
Expected: `Project ... added to the solution.`

- [ ] **Step 4: Run the tests — verify green and non-vacuous**

Run: `dotnet test MMLib.Alvo.slnx`
Expected: `Build succeeded`, tests run on Microsoft.Testing.Platform, and the summary reports **1 test, passed: 1, failed: 0** (a passing count of exactly 1 confirms the test actually executed, not zero).

- [ ] **Step 5: Commit**

```bash
git add test MMLib.Alvo.slnx
git commit -m "test: add architecture guard — Abstractions depends on no sibling project

Claude-Session: https://claude.ai/code/session_0188Gs6GcPUbra9ndnUtJCte"
```

---

### Task 3: Agent-facing conventions + architecture doc

Writes the durable rules that keep future work aligned: a root `CLAUDE.md` (agent-facing) and the full package-boundary rule in `docs/`. Deliverable: both files present; build and test still green.

**Files:**
- Create: `CLAUDE.md`
- Create: `docs/architecture/package-boundary.md`

**Interfaces:**
- Consumes: nothing at runtime.
- Produces: documentation only.

- [ ] **Step 1: Create `CLAUDE.md`**

```markdown
# MMLib.Alvo — working agreements for agents

Alvo is a .NET-native Backend-as-a-Service. The product specs live in `specs/`
(`alvo-specifikacia.md` = delivery strategy & technical spec; `baas-analyza.md` =
domain analysis). Architecture notes live in `docs/`.

## Build & test

- `dotnet build` — build the whole solution (`MMLib.Alvo.slnx`).
- `dotnet test` — run all tests. Tests run on **Microsoft.Testing.Platform (MTP)**,
  not VSTest (selected via the `test` section in `global.json`).
- Target framework: `net10.0`. The SDK is pinned in `global.json`.

## Package boundary (hard rule — spec §1.1)

A component earns its **own NuGet package** only if it meets at least one of:

1. it pulls a **heavy/foreign dependency** most users don't want (a DB driver,
   the Azure SDK, Roslyn, Blazor), or
2. it is a **real swap point** someone actually replaces (DB engine, secret store), or
3. it has a **different distribution/license policy**.

Everything else lives as a **namespace / vertical slice inside the core**, not a
separate project. Conceptual tidiness is not a reason for a package. See
`docs/architecture/package-boundary.md` for the full rule and examples.

Hard dependency rules: `MMLib.Alvo.Abstractions` depends on nothing; the core
depends only on `Abstractions`; no package depends on another port's provider;
lockstep SemVer (one version for the whole family).

## Do not create projects ahead of time

New projects are added **when their turn comes**, not preemptively. The core
`MMLib.Alvo` project does not exist yet — it appears once it has real content.

## Conventions

- Central Package Management: add/adjust versions in `Directory.Packages.props`;
  `PackageReference` entries carry no `Version`.
- Shared MSBuild settings live in `Directory.Build.props`.
- Not permitted (licensing): MediatR, FluentAssertions v8+. Use Shouldly for
  assertions.
- Direct pushes to `main` are forbidden; every change lands via a reviewed PR.
```

- [ ] **Step 2: Create `docs/architecture/package-boundary.md`**

```markdown
# Package boundary

> The rule that decides what becomes a separate NuGet package in the
> `MMLib.Alvo.*` family. Source: spec `specs/alvo-specifikacia.md` §1.1.

## The rule (hard)

A standalone NuGet package is justified only when a component meets **at least one**:

- **(a) Foreign / heavy dependency** — it drags in a dependency most consumers don't
  want: a database driver, the Azure SDK, Roslyn, Blazor, etc.
- **(b) Real swap point** — someone genuinely replaces it: the database engine, a
  secret store, an object store.
- **(c) Different distribution / license policy** — e.g. a commercial
  `Alvo.Enterprise.*` add-on versus the Apache-2.0 core.

Anything else lives as a **namespace / vertical slice inside the core**, not as its
own project. Conceptual neatness is **not** a reason to split.

## Consequence

The core is **one large package** (schema registry, data API, rule engine, events,
auth, rbac, realtime, automation, tenancy, audit, caching, Management API, plus the
in-core default providers as vertical slices). Packages exist only where the rule
above applies — roughly **~10 packages for v0.1, not 30+**. Start conservative:
extracting a namespace into a package later is cheap; merging too many packages back
is a breaking change.

## Illustrative example (non-binding)

- `MMLib.Alvo.Abstractions` (ports, no dependencies) · `MMLib.Alvo` (core + builder)
- data providers as separate packages (each drags a driver): SQLite (dev),
  PostgreSQL, SQL Server
- `MMLib.Alvo.Admin` (Blazor — heavy dep) · `MMLib.Alvo.Host` (Docker) ·
  `MMLib.Alvo.Cli`
- `MMLib.Alvo.Testing` (contract suite + fakes) · `MMLib.Alvo.Templates`
- later, when the feature lands: Scripting (Roslyn), Functions.ContainerApps (Azure),
  Azure/Kubernetes provider bundles, Aspire, client codegen, MCP adapter — each
  justified by a foreign dependency. Concrete provider adapters (SendGrid, S3, …) are
  added **on demand**, not preemptively.

## Hard dependency rules

- `MMLib.Alvo.Abstractions` depends on nothing.
- The core depends only on `Abstractions`.
- **No package depends on another port's provider.**
- Lockstep SemVer: everything is versioned and released together as one version.
```

- [ ] **Step 3: Verify nothing broke**

Run: `dotnet build MMLib.Alvo.slnx`
Expected: `Build succeeded`, `0 Error(s)`.

- [ ] **Step 4: Commit**

```bash
git add CLAUDE.md docs/architecture/package-boundary.md
git commit -m "docs: add agent conventions and package-boundary rule

Claude-Session: https://claude.ai/code/session_0188Gs6GcPUbra9ndnUtJCte"
```

---

### Task 4: Minimal CI workflow

Adds the minimal GitHub Actions workflow that runs build + test on every PR. This is the status check issue [5] will later mark as required (branch protection needs a check that has already run at least once). Deliverable: a valid workflow file.

**Files:**
- Create: `.github/workflows/ci.yml`

**Interfaces:**
- Consumes: `global.json` (SDK version), `MMLib.Alvo.slnx`.
- Produces: a GitHub Actions workflow named `CI` with job `build-and-test`.

- [ ] **Step 1: Create `.github/workflows/ci.yml`**

```yaml
name: CI

on:
  pull_request:

jobs:
  build-and-test:
    name: Build & test
    runs-on: ubuntu-latest
    steps:
      - name: Checkout
        uses: actions/checkout@v4

      - name: Setup .NET
        uses: actions/setup-dotnet@v4
        with:
          global-json-file: global.json

      - name: Restore
        run: dotnet restore

      - name: Build
        run: dotnet build --configuration Release --no-restore

      - name: Test
        run: dotnet test --configuration Release --no-restore
```

- [ ] **Step 2: Validate the workflow is syntactically well-formed**

Run: `python3 -c "import yaml,sys; yaml.safe_load(open('.github/workflows/ci.yml')); print('YAML OK')"`
Expected: `YAML OK`.

- [ ] **Step 3: Locally reproduce what CI will do (clean-machine confidence)**

Run:
```bash
dotnet restore
dotnet build --configuration Release --no-restore
dotnet test --configuration Release --no-restore
```
Expected: restore succeeds, `Build succeeded` with `0 Error(s)`, and the test run reports **1 test, passed: 1**.

- [ ] **Step 4: Commit**

```bash
git add .github/workflows/ci.yml
git commit -m "ci: add minimal build+test workflow on pull_request

Claude-Session: https://claude.ai/code/session_0188Gs6GcPUbra9ndnUtJCte"
```

---

## Finishing up

After all four tasks are committed on `repo-bootstrap`, push the branch and open a PR against `main` so the new CI workflow runs on the PR (this first run is what lets issue [5] mark the check as required). Do not merge directly to `main`.

Run:
```bash
git push -u origin repo-bootstrap
gh pr create --base main --title "[1] Repo bootstrap + solution skeleton" \
  --body "Implements #1. See docs/superpowers/specs/2026-07-11-repo-bootstrap-design.md.

https://claude.ai/code/session_0188Gs6GcPUbra9ndnUtJCte"
```

## Definition of Done (from issue #1)

- [ ] Repo is cloneable.
- [ ] `dotnet build` and `dotnet test` pass on a clean machine (Abstractions builds; the NetArchTest rule is green).
- [ ] The minimal CI workflow runs on PR.
- [ ] Solution is `.slnx`.
- [ ] Package boundary is written as a rule (not a finished project list).
- [ ] `LICENSE` (Apache-2.0) and `CHANGELOG.md` are in place.
- [ ] The core `MMLib.Alvo` project was **not** created; no projects created ahead of time.

## Self-review notes (author)

- **Spec coverage:** every issue #1 scope item maps to a task — `.slnx` (T1), Abstractions-first / no core (T1), CPM + `Directory.Build.props` (T1), `global.json` (T1), `LICENSE`+`CHANGELOG` (T1), NetArchTest rule (T2), package boundary as a rule (T3 + `CLAUDE.md`), minimal `pull_request` CI (T4). `.gitignore`/`.editorconfig` already exist in the repo.
- **Placeholder scan:** no TBD/TODO; every code and config step contains complete content.
- **Type consistency:** the assembly name `MMLib.Alvo.Abstractions`, namespace `MMLib.Alvo.Abstractions.Tests`, and package IDs/versions are identical across Task 1 (produced) and Task 2 (consumed).
