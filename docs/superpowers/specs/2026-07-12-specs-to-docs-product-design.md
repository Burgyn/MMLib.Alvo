# Design — move `specs/` into `docs/product/`

> Status: brainstormed and approved by the maintainer in chat.

## Goal

`specs/` currently sits at the repo root, separate from `docs/`, holding the product spec
(`alvo-specifikacia.md`), domain analysis (`baas-analyza.md`), and the descriptor JSON Schema
(`alvo-descriptor.schema.json`). Consolidate all narrative/reference documentation under
`docs/`, and give the product-definition material a name that doesn't collide with
`docs/superpowers/specs/` (which holds this skill's own design specs — a different kind of
"spec").

## Decision

New location: **`docs/product/`**, mirroring the existing `docs/architecture/` and
`docs/legal/` topic-folder pattern. File names are unchanged — only the containing directory
moves and is renamed.

| Before | After |
|---|---|
| `specs/alvo-specifikacia.md` | `docs/product/alvo-specifikacia.md` |
| `specs/baas-analyza.md` | `docs/product/baas-analyza.md` |
| `specs/alvo-descriptor.schema.json` | `docs/product/alvo-descriptor.schema.json` |

`specs/` is removed once empty.

## References updated

- `CLAUDE.md` — "The product specs live in `specs/`" → `docs/product/`.
- `CONTRIBUTING.md` — "product specs live in [`specs/`](specs/)" → `docs/product/`.
- `docs/architecture/package-boundary.md` — "Source: spec `specs/alvo-specifikacia.md` §1.1"
  → `docs/product/alvo-specifikacia.md`.

## References intentionally left alone

`docs/superpowers/plans/2026-07-11-repo-bootstrap.md` and
`docs/superpowers/specs/2026-07-11-repo-bootstrap-design.md`,
`2026-07-12-contributing-cla-design.md` also cite `specs/alvo-specifikacia.md` /
`specs/baas-analyza.md`. These are dated, completed records of past brainstorming/plan runs —
they describe what was true when they were written. Rewriting them to point at the new path
would be revisionist; maintainer confirmed leaving them as-is.

## Non-goals

- No renaming of the individual files themselves.
- No change to the content of the spec/analysis/schema files.

## Plan shape

Mechanical move, no architecture or API involved: `git mv` the three files into
`docs/product/`, update the three referencing files above, commit.
