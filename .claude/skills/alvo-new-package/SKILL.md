---
name: alvo-new-package
description: Use when creating a new project or package in the MMLib.Alvo.* family — a shipped library, a provider adapter, or a test project.
---

# Adding a project to MMLib.Alvo

The `MMLib.Alvo.Conventions.Tests` (os A) suite mechanically enforces most of
this — run it and it names what is wrong. This runbook covers the judgment and
the few steps a test can't check.

## Is the package earned?

Default to adding code **inside the core**, not a new package. A separate
package is justified only by a foreign/heavy dependency, a real swap point, or a
distinct license — see `docs/architecture/package-boundary.md`. `alvo-plan-guard`
will challenge an unearned package before the PR.

## Steps

1. **Create via the `dotnet` CLI** (`dotnet new classlib` / a test template),
   then **strip the generated inherited props** — `TargetFramework`, `Nullable`,
   `ImplicitUsings`, `LangVersion` all come from `Directory.Build.props`. Leaving
   them fails the convention test.
2. **Dependencies via CPM only** — add the version to `Directory.Packages.props`,
   never an inline `Version`/`VersionOverride` on the `PackageReference`.
3. **Register it in `MMLib.Alvo.slnx`** (`dotnet sln … add`), under the matching
   `/src/` or `/test/` folder.
4. **Every packable `src` project needs a matching `<name>.Tests`.** The test
   project **must** `ProjectReference` its production project — the linked shared
   architecture rules `Assembly.Load` the sibling assembly and throw without it.
5. **Shipped (`IsPackable` not false) → add a public-API approval test**
   (PublicApiGenerator + Verify) and commit the `*.verified.txt` baseline, so any
   later public-surface change is a conscious, reviewed act (ties to SemVer).
6. **Encapsulation:** mark `public` only what is genuinely the contract; default
   to `internal`. Widening the public surface should be deliberate.

## Then

`dotnet build -c Release`, `dotnet format --verify-no-changes`,
`scripts/test-ring2` — all green — then branch → PR → `alvo-plan-guard`. See
`alvo-dotnet-conventions` (packaging, test stack, code style) and
`alvo-architecture-rules` (dependency direction, encapsulation).
