# Design — Central package management (`Directory.*.props`) (issue #3)

> Source issue: [#3] `[2]` Central package management (Directory.*.props), label `ready`.
> Status: bootstrap (issue #1) already landed part of this — `Directory.Build.props` with
> `TargetFramework`/`LangVersion`/`Nullable`, and `Directory.Packages.props` with
> `ManagePackageVersionsCentrally=true`. This design finishes the rest.

## Goal

Every build setting and every piece of shippable-package metadata lives in exactly one place
(`Directory.Build.props` / `Directory.Packages.props`), inherited by every project with zero
per-project configuration. Adding a package is a one-line version entry; adding a new project
gets correct metadata, warnings-as-errors, determinism, and SourceLink for free.

## What's already done (not touched by this change)

- `Directory.Build.props`: `TargetFramework=net10.0`, `LangVersion=latest`, `Nullable=enable`,
  `ImplicitUsings=enable`.
- `Directory.Packages.props`: `ManagePackageVersionsCentrally=true`, existing test-only package
  versions (`xunit.v3`, `NetArchTest.Rules`, `Shouldly`).
- No `.csproj` in the repo carries an inline `Version` on a `PackageReference` — that part of
  the issue's Definition of Done is already satisfied.

## Guiding decisions

1. **Drop the bootstrap's `<VersionPrefix>0.1.0</VersionPrefix>`.** The issue's own text says
   version is explicitly **not** fixed in `Directory.Build.props` — that's owned by issue #14
   `[10]` "Semantic versioning + release pipeline" (MinVer from git tags). Keeping a hardcoded
   prefix here would contradict that and get silently overwritten later. Removing it now means
   projects fall back to the SDK's implicit default version, which is fine — nothing is
   published yet.
2. **No `Company` property.** Alvo is an individual OSS project, not a registered company;
   `Authors=Milan Martiniak` and `Product=Alvo` already identify it. Adding a placeholder
   `Company` value would be noise.
3. **Ship a real icon and a real README now, sourced from this change** — even though the
   "official" issues for each (`[27]` logo, `[25]` README/DX) are separate and still open. The
   maintainer supplied a finished logo (`alvo-logo.svg`) and asked for a baseline README to be
   created here rather than deferring both. Scope stays narrow: this READMEs is the minimum
   viable landing page (tagline, description, build/test, package list, links) — the "didn't
   forget anything" DX mechanism from `[25]` is explicitly **not** part of this change.
4. **SVG → PNG conversion is required, not optional.** NuGet's `PackageIcon` only accepts
   JPEG/PNG (`.svg` raises `NU5045`); the source logo is an SVG. Converted once, at design time,
   to a 256×256 PNG committed to the repo — no build-time conversion step.
5. **Icon and README are packed via one shared `ItemGroup` in `Directory.Build.props`**,
   conditioned on `'$(IsPackable)' != 'false'`, so every current and future packable project
   gets them automatically. This is what makes the DoD's "no per-project configuration" true
   even as more packages appear.
6. **SourceLink added globally, not conditionally.** `Microsoft.SourceLink.GitHub` as a
   `PackageReference` with `PrivateAssets="All"` in `Directory.Build.props` costs nothing on
   non-packed builds and is required background for every packable project once one exists.
7. **`ContinuousIntegrationBuild` gated on `$(GITHUB_ACTIONS)`.** Standard SourceLink/determinism
   pairing: local builds keep full paths for debugging; CI builds (where `GITHUB_ACTIONS=true`
   is set by Actions) get the deterministic, path-independent build Microsoft recommends before
   packing/publishing.
8. **No `Directory.Build.targets`.** The issue says "if needed (e.g. shared analyzers)" — there
   are none yet. Adding an empty file ahead of need contradicts the repo's "earned, not assumed"
   convention (`CLAUDE.md`); create it when a real shared analyzer/target shows up.

## Files touched

```
Directory.Build.props                                          # metadata + build settings + SourceLink + pack ItemGroup
Directory.Packages.props                                        # + Microsoft.SourceLink.GitHub version
icon.png                                                         # new — 256x256, rendered from alvo-logo.svg
README.md                                                        # new — baseline landing page
CHANGELOG.md                                                     # [Unreleased] → Added entry
docs/superpowers/specs/2026-07-12-central-package-management-design.md   # this document
```

### `Directory.Build.props` — resulting shape

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

### `Directory.Packages.props` — added entry

```xml
<PackageVersion Include="Microsoft.SourceLink.GitHub" Version="10.0.300" />
```

### `icon.png`

Rendered once from the maintainer-supplied `alvo-logo.svg` via `rsvg-convert -w 256 -h 256`,
then committed as a binary asset at the repo root. Square 256×256 PNG, within NuGet's 1 MB /
JPEG-or-PNG requirement.

### `README.md` — structure

Title + tagline (from the spec: "Application Layer for Vision & Operations") → one-paragraph
description → build & test (`dotnet build`, `dotnet test`, note on MTP not VSTest, pinned SDK) →
current packages (today: only `MMLib.Alvo.Abstractions`, with a note that the list grows as
packages are earned — link to `docs/architecture/package-boundary.md`) → links (CONTRIBUTING,
LICENSE, product spec in `docs/product/`). No screenshots, no "didn't forget anything" mechanism
— that is `[25]`'s job.

## Out of scope (deferred to other issues)

- Fixed/derived package `Version` and the release pipeline — issue #14 `[10]`.
- The rest of the logo/visual-identity work (favicon, social card, brand guide) beyond the one
  icon file used for packages — issue #32 `[27]`.
- The full README "didn't forget anything" DX mechanism — issue #30 `[25]`.
- `Directory.Build.targets` / shared analyzers — created when a real one is needed.

## Definition of Done

- No `.csproj` has an inline package version (already true; unchanged).
- Adding a package is a one-line entry in `Directory.Packages.props`.
- Every built assembly/package carries author, product, description, license, repo link, icon,
  and readme with no per-project configuration — verified by `dotnet pack` on
  `MMLib.Alvo.Abstractions` and inspecting the resulting `.nupkg` (nuspec fields + `icon.png` +
  `README.md` present, no `NU50xx` warnings/errors).
- `dotnet build` and `dotnet test` still pass with `TreatWarningsAsErrors=true`.

## Caveats

- `Description`, `PackageTags`, and `Copyright` wording are this design's first draft
  (maintainer-approved during brainstorming) — cheap to tweak later since they're centralized
  in one file.
- `icon.png` is a generated artifact (from `alvo-logo.svg`); if the source SVG changes, the PNG
  must be regenerated manually — there's no build-time SVG→PNG step.
