# Contributing to MMLib.Alvo

Thanks for your interest in Alvo — a .NET-native Backend-as-a-Service. This guide
covers how to build, test, and submit changes, and explains the Contributor License
Agreement (CLA) up front and honestly.

If you want the full picture of what Alvo is and where it's going, the product specs
live in [`specs/`](specs/) and architecture notes in [`docs/`](docs/).

## Building & testing

- **Build the whole solution:** `dotnet build` (`MMLib.Alvo.slnx`)
- **Run all tests:** `dotnet test`

Tests run on **Microsoft.Testing.Platform (MTP)**, not VSTest — this is selected via
the `test` section in `global.json`, so `dotnet test` does the right thing without extra
flags. The target framework is `net10.0`; the exact SDK is pinned in `global.json`, so
install that SDK (or a compatible patch release) and the build is reproducible.

## Coding conventions

The project's coding conventions — the "why, not what" comment policy, the XML-doc
requirement on public API, Central Package Management, and which packages are off-limits
for licensing reasons — are defined once, authoritatively, in [`CLAUDE.md`](CLAUDE.md).
That file is the single source of truth for style; please read its **Conventions** and
**Code style** sections before opening a pull request.

In short:

- **Comments say _why_, not _what_.** Prefer self-documenting code — a well-named method or
  variable — over prose that narrates the logic.
- **Public API members of shipped libraries carry XML doc comments** (`/// <summary>`).
- **Central Package Management:** versions live in `Directory.Packages.props`;
  `PackageReference` entries carry no `Version`.
- **Not permitted (licensing):** MediatR, FluentAssertions v8+. Use **Shouldly** for
  assertions.

## Pull request process

1. **Never push directly to `main`.** It's forbidden — every change lands via a reviewed
   pull request.
2. Branch off `main`, make your change, and keep the diff focused on one concern.
3. Push your branch and open a pull request against `main`.
4. CI (build + test) runs on every pull request and must pass before merge.
5. Sign the CLA when the bot asks (see below) — this happens automatically on your first PR.

## Contributor License Agreement (CLA)

**Alvo's core is, and will remain, licensed under Apache-2.0 — that will not change.**

Contributions require signing a lightweight CLA. This is not about taking anything away
from you. It exists so the maintainer can fund the project's future through optional
commercial add-ons (for example, `Alvo.Enterprise.*` packages) **without ever having to
track down and re-clear permission from every past contributor**. A project that skips this
step from the start can find itself unable to evolve its licensing later — we're avoiding
that trap deliberately, on day one.

Two things worth being clear about:

- **You keep full copyright over your contribution.** The CLA grants a license; it does
  **not** transfer ownership to the maintainer.
- **The license you grant is broad** — broad enough to allow the maintainer to license the
  combined work beyond Apache-2.0 in the future. That breadth is the whole point, and we'd
  rather say so plainly than bury it.

The exact terms:

- Individual contributors: [`docs/legal/CLA-INDIVIDUAL.md`](docs/legal/CLA-INDIVIDUAL.md)
- Contributing on behalf of an employer: [`docs/legal/CLA-CORPORATE.md`](docs/legal/CLA-CORPORATE.md)

**Signing is automatic and one-time.** The [CLA Assistant](https://cla-assistant.io/) bot
comments on your first pull request and asks you to accept via your GitHub identity. Once
you've signed, every subsequent PR is cleared automatically — you won't be asked again
unless the agreement itself changes.

## Code of Conduct

This project follows the [Contributor Covenant](CODE_OF_CONDUCT.md). By participating, you
agree to uphold it. Please read [`CODE_OF_CONDUCT.md`](CODE_OF_CONDUCT.md).
