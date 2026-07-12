# Central Package Management (issue #3) Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Finish issue #3 — every build setting and every shippable-package metadata field lives once in `Directory.Build.props`/`Directory.Packages.props`, inherited by all projects with zero per-project configuration.

**Architecture:** Pure MSBuild/config change. No source code, no new projects. Three deliverables: a packed icon asset, a root README, and the `Directory.Build.props`/`Directory.Packages.props` edits that wire warnings-as-errors, determinism, SourceLink, and shared NuGet metadata (including packing the icon + README into every packable project automatically).

**Tech Stack:** .NET 10 SDK-style MSBuild props files, Central Package Management (`ManagePackageVersionsCentrally`), `Microsoft.SourceLink.GitHub`.

**Design doc:** `docs/superpowers/specs/2026-07-12-central-package-management-design.md`

## Global Constraints

- Target framework: `net10.0` (pinned via `global.json`, SDK `10.0.100`).
- Tests run on **Microsoft.Testing.Platform (MTP)**, not VSTest.
- Central Package Management: package versions ONLY in `Directory.Packages.props`; no `.csproj` may carry an inline `Version` on a `PackageReference`.
- Not permitted (licensing): MediatR, FluentAssertions v8+. Use Shouldly for assertions (not exercised by this plan — no test code is written).
- Direct pushes to `main` are forbidden; this work happens on branch `central-package-management` and lands via a reviewed PR.
- Comments say *why*, not *what* — this plan adds no comments beyond the one rationale comment already specified below.
- License: Apache-2.0. Repo: `https://github.com/Burgyn/MMLib.Alvo`.

---

## Task 1: Commit the package icon (SVG source + generated PNG)

**Files:**
- Create: `docs/assets/alvo-logo.svg` (copy of the maintainer-supplied source logo — source of truth for regenerating the icon)
- Create: `icon.png` (root of repo — 256×256 PNG, what `PackageIcon` will point to)

**Interfaces:**
- Produces: a root-level `icon.png` that Task 3's `Directory.Build.props` references via `<PackageIcon>icon.png</PackageIcon>` and packs via `<None Include="$(MSBuildThisFileDirectory)icon.png" ...>`.

- [ ] **Step 1: Copy the source SVG into the repo**

```bash
mkdir -p docs/assets
cp "/Users/martiniak/Downloads/alvo-logo.svg" docs/assets/alvo-logo.svg
```

- [ ] **Step 2: Generate the 256×256 PNG icon from it**

```bash
rsvg-convert -w 256 -h 256 docs/assets/alvo-logo.svg -o icon.png
```

If `rsvg-convert` is not on `PATH`, use ImageMagick instead: `magick -background none docs/assets/alvo-logo.svg -resize 256x256 icon.png`.

- [ ] **Step 3: Verify the PNG is valid and within NuGet's limits**

Run: `file icon.png && ls -la icon.png`
Expected: output identifies it as `PNG image data, 256 x 256`, and the file size is well under NuGet's 1 MB `PackageIcon` limit (this logo compresses to a few KB).

- [ ] **Step 4: Commit**

```bash
git add docs/assets/alvo-logo.svg icon.png
git commit -m "assets: add Alvo package icon (256x256 PNG, generated from alvo-logo.svg)"
```

---

## Task 2: Write the root README

**Files:**
- Create: `README.md` (repo root)

**Interfaces:**
- Produces: `README.md`, which Task 3's `Directory.Build.props` references via `<PackageReadmeFile>README.md</PackageReadmeFile>` and packs via `<None Include="$(MSBuildThisFileDirectory)README.md" ...>`.
- Consumes: nothing from Task 1 (independent deliverable); no ordering dependency between Task 1 and Task 2.

- [ ] **Step 1: Write `README.md`**

```markdown
# Alvo

> **Alvo** · *Application Layer for Vision & Operations* · "Your intent, running in production."

Alvo is a .NET-native Backend-as-a-Service framework for the agentic age, distributed as the
`MMLib.Alvo.*` NuGet package family. It runs standalone (Docker) or embedded in an existing
ASP.NET Core host — same code, two distributions.

The full delivery strategy and technical spec live in
[`docs/product/alvo-specifikacia.md`](docs/product/alvo-specifikacia.md); the domain analysis
behind it is in [`docs/product/baas-analyza.md`](docs/product/baas-analyza.md).

## Building & testing

Requires the .NET SDK pinned in [`global.json`](global.json) (`10.0.100`).

```bash
dotnet build
dotnet test
```

Tests run on **Microsoft.Testing.Platform (MTP)**, not VSTest (see the `test` section in
`global.json`).

## Packages

Alvo ships as a family of focused NuGet packages, added as they're earned rather than assumed
up front — see [`docs/architecture/package-boundary.md`](docs/architecture/package-boundary.md)
for the rule and the current list. Today that list is:

| Package | Description |
| --- | --- |
| `MMLib.Alvo.Abstractions` | The interface-first root of the dependency graph — no source yet, ports/interfaces land in a later phase. |

## Contributing

See [`CONTRIBUTING.md`](CONTRIBUTING.md) for the build/test workflow, coding conventions, and
the pull request process (including the CLA).

## License

Apache-2.0 — see [`LICENSE`](LICENSE).
```

- [ ] **Step 2: Verify the relative links resolve**

Run: `ls docs/product/alvo-specifikacia.md docs/product/baas-analyza.md docs/architecture/package-boundary.md CONTRIBUTING.md LICENSE global.json`
Expected: all six paths listed with no "No such file or directory" errors.

- [ ] **Step 3: Commit**

```bash
git add README.md
git commit -m "docs: add root README (build/test, package list, contributing, license)"
```

---

## Task 3: Wire build settings, shared metadata, and SourceLink into `Directory.*.props`

**Files:**
- Modify: `Directory.Build.props` (full replacement below)
- Modify: `Directory.Packages.props` (add one `PackageVersion` entry)

**Interfaces:**
- Consumes: `icon.png` (Task 1) and `README.md` (Task 2) must exist at the repo root before the `dotnet pack` verification step in this task, or that step fails with `NU5046`.
- Produces: every project in the solution inherits `TreatWarningsAsErrors`, `Deterministic`, the full NuGet metadata block, and — for packable projects — the icon/readme pack items and the `Microsoft.SourceLink.GitHub` reference. Nothing downstream in this plan consumes these programmatically; this task's own verification is the final check.

- [ ] **Step 1: Replace `Directory.Build.props` with this content**

```xml
<Project>

  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
    <LangVersion>latest</LangVersion>
    <Nullable>enable</Nullable>
    <ImplicitUsings>enable</ImplicitUsings>

    <TreatWarningsAsErrors>true</TreatWarningsAsErrors>
    <Deterministic>true</Deterministic>
    <ContinuousIntegrationBuild Condition="'$(GITHUB_ACTIONS)' == 'true'">true</ContinuousIntegrationBuild>
    <PublishRepositoryUrl>true</PublishRepositoryUrl>
  </PropertyGroup>

  <PropertyGroup>
    <!-- Shared NuGet/assembly metadata — inherited by every project, no per-project config. -->
    <Authors>Milan Martiniak</Authors>
    <Product>Alvo</Product>
    <Copyright>Copyright (c) Milan Martiniak</Copyright>
    <Description>Alvo — a .NET-native Backend-as-a-Service framework for the agentic age.</Description>
    <PackageProjectUrl>https://github.com/Burgyn/MMLib.Alvo</PackageProjectUrl>
    <RepositoryUrl>https://github.com/Burgyn/MMLib.Alvo</RepositoryUrl>
    <RepositoryType>git</RepositoryType>
    <PackageLicenseExpression>Apache-2.0</PackageLicenseExpression>
    <PackageTags>baas;backend-as-a-service;dotnet;alvo</PackageTags>
    <PackageIcon>icon.png</PackageIcon>
    <PackageReadmeFile>README.md</PackageReadmeFile>
    <!-- Version is intentionally absent — MinVer/git tags own it (issue #14, "[10]"). -->
  </PropertyGroup>

  <ItemGroup>
    <PackageReference Include="Microsoft.SourceLink.GitHub" PrivateAssets="All" />
  </ItemGroup>

  <ItemGroup Condition="'$(IsPackable)' != 'false'">
    <None Include="$(MSBuildThisFileDirectory)icon.png" Pack="true" PackagePath="\" Visible="false" />
    <None Include="$(MSBuildThisFileDirectory)README.md" Pack="true" PackagePath="\" Visible="false" />
  </ItemGroup>

</Project>
```

Note: `<VersionPrefix>0.1.0</VersionPrefix>` from the bootstrap commit is deliberately removed — see the design doc's guiding decision #1.

- [ ] **Step 2: Add the SourceLink package version to `Directory.Packages.props`**

Resulting full file:

```xml
<Project>

  <PropertyGroup>
    <ManagePackageVersionsCentrally>true</ManagePackageVersionsCentrally>
  </PropertyGroup>

  <ItemGroup>
    <PackageVersion Include="xunit.v3" Version="3.2.2" />
    <PackageVersion Include="NetArchTest.Rules" Version="1.3.2" />
    <PackageVersion Include="Shouldly" Version="4.3.0" />
    <PackageVersion Include="Microsoft.SourceLink.GitHub" Version="10.0.300" />
  </ItemGroup>

</Project>
```

- [ ] **Step 3: Restore and build with warnings-as-errors on**

Run: `dotnet restore && dotnet build --configuration Release`
Expected: `Build succeeded.` with `0 Warning(s)` and `0 Error(s)`. If SourceLink emits an unexpected warning here, read it — do not suppress it blindly; `TreatWarningsAsErrors` is intentional (issue's own DoD implies a clean, uniform build).

- [ ] **Step 4: Pack the one packable project and inspect the result**

Run:
```bash
dotnet pack src/MMLib.Alvo.Abstractions/MMLib.Alvo.Abstractions.csproj --configuration Release --output /tmp/alvo-pack-check
unzip -l /tmp/alvo-pack-check/MMLib.Alvo.Abstractions.*.nupkg
```
Expected: the listing includes `icon.png`, `README.md`, and a `.nuspec` file; the pack command prints no `NU50xx` warnings (they'd be build errors anyway under `TreatWarningsAsErrors`, so a non-zero exit here is the signal something's missing).

- [ ] **Step 5: Inspect the generated nuspec metadata**

Run:
```bash
unzip -p /tmp/alvo-pack-check/MMLib.Alvo.Abstractions.*.nupkg '*.nuspec'
```
Expected: `<authors>Milan Martiniak</authors>`, `<owners>` absent or matching, `<description>Alvo — a .NET-native Backend-as-a-Service framework for the agentic age.</description>`, `<projectUrl>https://github.com/Burgyn/MMLib.Alvo</projectUrl>`, `<license type="expression">Apache-2.0</license>`, `<icon>icon.png</icon>`, `<readme>README.md</readme>`, `<repository type="git" url="https://github.com/Burgyn/MMLib.Alvo" .../>`, and no `<version>` pinned to `0.1.0` (the SDK's implicit default, e.g. `1.0.0`, is expected and fine).

- [ ] **Step 6: Confirm no `.csproj` carries an inline package version (DoD check)**

Run: `grep -rn "PackageReference.*Version=" --include="*.csproj" .`
Expected: no output (empty) — every `PackageReference` across the solution carries no `Version` attribute.

- [ ] **Step 7: Run the full test suite**

Run: `dotnet test --configuration Release`
Expected: existing tests (the NetArchTest architecture guard) still pass; no new warnings/errors introduced by the props changes.

- [ ] **Step 8: Clean up the throwaway pack output**

Run: `rm -rf /tmp/alvo-pack-check`

- [ ] **Step 9: Commit**

```bash
git add Directory.Build.props Directory.Packages.props
git commit -m "build: shared NuGet metadata, warnings-as-errors, determinism, SourceLink"
```

---

## Task 4: Changelog entry

**Files:**
- Modify: `CHANGELOG.md`

**Interfaces:**
- Consumes: nothing (documentation-only task, runs last so it can summarize Tasks 1–3 together).

- [ ] **Step 1: Read the current `[Unreleased]` → `Added` section**

Run: `sed -n '1,25p' CHANGELOG.md`

- [ ] **Step 2: Add a bullet documenting this change**

Add this line under the existing `### Added` list in `[Unreleased]` (after the CONTRIBUTING/CLA bullet, matching the existing bullet style):

```markdown
- Central package management finished: shared assembly/NuGet metadata (author, product,
  license, repo link, tags, icon, readme), warnings-as-errors, deterministic builds, and
  SourceLink in `Directory.Build.props`; root `README.md` and package icon (`icon.png`,
  generated from `docs/assets/alvo-logo.svg`).
```

- [ ] **Step 3: Verify the file still renders as valid Markdown structure**

Run: `sed -n '1,30p' CHANGELOG.md`
Expected: the new bullet appears under `### Added` inside `## [Unreleased]`, formatting consistent with surrounding bullets.

- [ ] **Step 4: Commit**

```bash
git add CHANGELOG.md
git commit -m "docs: changelog entry for central package management completion"
```

---

## Definition of Done (verified across the tasks above)

- [x] No `.csproj` has an inline package version (Task 3, Step 6).
- [x] Adding a package is a one-line entry in `Directory.Packages.props` (already true; Task 3 Step 2 demonstrates it by adding `Microsoft.SourceLink.GitHub`).
- [x] Every built package carries author, product, description, license, repo link, icon, and readme with no per-project configuration (Task 3, Steps 4–5).
- [x] `dotnet build` and `dotnet test` pass with `TreatWarningsAsErrors=true` (Task 3, Steps 3 and 7).
