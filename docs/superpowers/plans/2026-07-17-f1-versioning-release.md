# F1 SemVer versioning + release skeleton — Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: superpowers:executing-plans. Config-heavy (MSBuild/YAML), so each task ends in a verification command (pack/build), not a unit test. Added to PR #59.

**Goal:** MinVer-driven versioning + a pack-only CI artifact + a tag-triggered release skeleton for issue #14.

**Tech Stack:** MinVer 7.0.0, `dotnet pack`, GitHub Actions.

## Global Constraints

Same as the rest of F1 (net10.0, CPM, no inline versions, warnings-as-errors, analyzers, no inline comments / English-only in code, file-scoped namespaces). Never push/merge to `main`.

---

### Task 1: MinVer versioning

**Files:** `Directory.Packages.props` (version), `Directory.Build.props` (PackageReference + `MinVerTagPrefix`).

- [ ] Add `<PackageVersion Include="MinVer" Version="7.0.0" />` to CPM.
- [ ] In `Directory.Build.props`: `<MinVerTagPrefix>v</MinVerTagPrefix>` and `<PackageReference Include="MinVer" PrivateAssets="all" />`.
- [ ] **Verify:** `dotnet pack src/MMLib.Alvo.Abstractions -c Release` → the `.nupkg` version is MinVer-computed (`0.0.0-alpha.0.N`), not `1.0.0`.
- [ ] Commit.

### Task 2: CI pack + artifact

**Files:** `.github/workflows/ci.yml`.

- [ ] Add a Linux-only `Pack` step after Build: `dotnet pack -c Release --no-build -o artifacts`; then `actions/upload-artifact` uploading `artifacts/*.nupkg`.
- [ ] **Verify:** YAML lints; `dotnet pack -c Release -o artifacts` locally produces `artifacts/*.nupkg`.
- [ ] Commit.

### Task 3: Release skeleton

**Files:** Create `.github/workflows/release.yml`.

- [ ] `on: push: tags: ['v*']`; `permissions: contents: write`; checkout + setup-dotnet + `dotnet pack -c Release -o artifacts` + `gh release create "$GITHUB_REF_NAME" artifacts/*.nupkg --draft --generate-notes`. No NuGet push (TODO marker for v0.1).
- [ ] **Verify:** YAML lints; steps reviewed (cannot run a tag push here).
- [ ] Commit.

### Task 4: Verify, guard, push

- [ ] `dotnet build -c Release`, `dotnet format --verify-no-changes`, `bash scripts/test-ring2` green.
- [ ] Update memory.
- [ ] Dispatch `alvo-plan-guard` over the combined #11+#12+#14 diff.
- [ ] Push to the PR branch (updates #59); update the PR title/body to include #14. Watch CI green (incl. Windows + the new pack step).

## Self-review notes

- Version is MinVer-computed, not hardcoded. ✓
- Pack-only (no publish); release workflow is a draft skeleton. ✓
- Conventional-Commits *enforcement* correctly deferred to #15. ✓
