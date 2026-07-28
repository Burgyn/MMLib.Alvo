# Changelog

All notable changes to this project are documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/),
and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [Unreleased]

### Changed (breaking)

- **A descriptor may no longer name a field `order`, `limit`, `offset`, `after`, `select`,
  `or`, `and` or `not`.** The generated Data API's query string reserves each of these
  (`?limit=10`, `?or=(...)`, `?not.color=eq.red`), so a request could not tell a filter on
  such a field from the parameter itself. The descriptor is now **rejected when it is
  applied**, with an error naming the entity, the field, the full reserved list and
  `Rename the field`; previously such a descriptor applied and then failed when routes were
  mapped — or, for an embedded host that never maps the Data API, was never refused at all.
  `order` in particular is a plausible business field name (an `orders` entity with an
  `order` column is not exotic), so this will hit real descriptors. Rename the field; there
  is no opt-out, because the ambiguity has no correct per-request resolution.
  `schema/project.schema.json` documents the exclusion on the `fields` description — the
  JSON Schema pattern cannot express it, so it is stated there rather than validated.

### Added

- Repository and solution skeleton: `MMLib.Alvo.Abstractions` (the interface-first
  root of the dependency graph) and its test project.
- Central Package Management, shared build settings, pinned .NET SDK, `.slnx` solution.
- First architectural guard-rail (NetArchTest): Abstractions depends on no other
  project in the solution.
- Apache-2.0 license and minimal pull-request CI (build + test).
- Contributor onboarding: `CONTRIBUTING.md` (build/test, PR process, transparent CLA
  explanation), Individual and Corporate CLAs (`docs/legal/`) based on the Project Harmony
  v1.0 templates that keep contributor copyright while allowing future relicensing, and a
  Contributor Covenant `CODE_OF_CONDUCT.md`.
- Central package management finished: shared assembly/NuGet metadata (author, product,
  license, repo link, tags, icon, readme), warnings-as-errors, deterministic builds, and
  SourceLink in `Directory.Build.props`; root `README.md` and package icon (`icon.png`,
  generated from `assets/alvo-logo.svg`).
- Repo tooling: CodeQL analysis, `Dependabot` version updates (NuGet + GitHub Actions),
  a Dependency Review check on pull requests (fails on moderate+ severity or
  non-allow-listed licenses), and a CodeRabbit config (`.coderabbit.yaml`) tuned to this
  project's conventions (Central Package Management, disallowed packages, XML doc and
  comment-style rules).
