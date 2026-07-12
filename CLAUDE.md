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
