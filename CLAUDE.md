# MMLib.Alvo — working agreements for agents

Alvo is a .NET-native Backend-as-a-Service. The product specs live in `specs/`
(`alvo-specifikacia.md` = delivery strategy & technical spec; `baas-analyza.md` =
domain analysis). Architecture notes live in `docs/`.

The human-facing contribution workflow (build/test, PR process, CLA, Code of
Conduct) lives in `CONTRIBUTING.md`. This file remains the source of truth for the
coding conventions and code style below; `CONTRIBUTING.md` links back here for them.

## Build & test

- `dotnet build` — build the whole solution (`MMLib.Alvo.slnx`).
- `dotnet test` — run all tests. Tests run on **Microsoft.Testing.Platform (MTP)**,
  not VSTest (selected via the `test` section in `global.json`).
- Target framework: `net10.0`. The SDK is pinned in `global.json`.

## Package boundary

See: `docs/architecture/package-boundary.md` for the hard rule (spec §1.1),
examples, and the current project list — for your orientation.

A package is earned, not assumed. Default to adding new code inside the core.

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

## Code style

- **Comments say _why_, not _what_.** Do not narrate what the code does. If a reader
  needs prose to follow the logic, extract it into a well-named method or variable
  instead — self-documenting code over comments. Reserve a comment for genuinely
  non-obvious rationale only (a workaround, a subtle invariant, why the obvious
  approach was avoided).
- **XML doc comments (`/// <summary>`) ARE required on public API members** of shipped
  library projects (public types/methods/properties on the ports and the core) — that
  is the published API surface (IntelliSense, generated docs). This is the one place
  comments are expected by default.
- Tests rely on descriptive names, not comment blocks.
