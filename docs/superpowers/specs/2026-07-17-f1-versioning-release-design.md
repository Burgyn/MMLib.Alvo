# F1 — SemVer versioning + release skeleton (issue #14) — design

> Added to the same PR as #11 + #12 (#59), at the maintainer's request. Issue
> **#14 [10] Semantic versioning + release pipeline**.

## Decisions carried in (from the F1 walkthrough)

- **MinVer** (Q2) — tag-driven, lockstep version across all packages from one git
  tag. `Directory.Build.props` already leaves `Version` absent for exactly this.
- **Pack-only artifact now** (Q8) — `dotnet pack` in CI uploads the `.nupkg` as a
  build artifact; no publish to any feed yet.
- **Release skeleton** (Q9) — a tag-triggered workflow that packs and drafts a
  GitHub release; no real NuGet publish.
- **Changelog from Conventional Commits** (Q5) — the release draft's notes are
  generated from the commits/PRs since the last tag (GitHub auto-generated
  notes). Enforcing the Conventional-Commits *format* on commit messages is a
  git-hook concern and lands with #15.

## What this adds

- **MinVer** as a `PrivateAssets=all` package in `Directory.Build.props` (applies
  to every project; only packable ones are packed), tag prefix `v`
  (`MinVerTagPrefix=v`). With no tags yet the computed version is a
  `0.0.0-alpha.0.N` pre-release — correct for a pre-tag repo.
- **CI (`ci.yml`)** — a Linux-only `dotnet pack -c Release` step that uploads the
  produced `.nupkg`(s) via `actions/upload-artifact`. Pack runs on the PR (the
  single gate); it does not publish.
- **`release.yml`** — `on: push: tags: 'v*'`; packs and creates a **draft**
  GitHub release with auto-generated notes. No NuGet push (a `TODO` marks where
  the publish step goes once v0.1 is ready). Least-privilege `contents: write`
  only for the release job.

## Out of scope

- Real NuGet/GitHub Packages publish (deferred; Q8 pack-only).
- Conventional-Commits *enforcement* (commit-msg hook) → #15.
- Tying a baseline-changing public-API diff (#12) to a major bump is a process
  note in the release checklist, not automated here.

## Definition of done

`dotnet pack` produces a package whose version is MinVer-computed (not 1.0.0);
the CI pack step uploads it as an artifact; a `v*` tag would trigger the release
workflow (packs + draft release); nothing publishes to a live feed.
