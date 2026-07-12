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
- Contributor onboarding: `CONTRIBUTING.md` (build/test, PR process, transparent CLA
  explanation), Individual and Corporate CLAs (`docs/legal/`) based on the Project Harmony
  v1.0 templates that keep contributor copyright while allowing future relicensing, and a
  Contributor Covenant `CODE_OF_CONDUCT.md`.
