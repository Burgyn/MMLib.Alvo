# F1 Architecture rules + Public API gate — Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:executing-plans (or superpowers:subagent-driven-development) with superpowers:test-driven-development. Steps use checkbox (`- [ ]`) syntax.

**Goal:** Add the public-API approval gate (#12) over packable assemblies and finish the encapsulation part of the architecture rules (#11), on top of PR1's test infrastructure.

**Architecture:** Public API is snapshot-tested with PublicApiGenerator + Verify, one dedicated test per packable assembly (Abstractions today). Encapsulation is tightened via `.editorconfig` analyzer severity + the API gate. Most other #11 rules are already latent/skipped from PR1.

**Tech Stack:** net10.0, MTP, xUnit v3, Shouldly, PublicApiGenerator, Verify.XunitV3, NetArchTest.

## Global Constraints

- Same as PR1: net10.0, MTP (plain `dotnet test`, never `--nologo`), CPM (no inline versions), inherited props not re-declared, `TreatWarningsAsErrors`, analyzers-as-gate, Shouldly-only, **no inline comments, English-only in code, file-scoped namespaces**, new projects via `dotnet` CLI. Never push/merge to `main`; end on an open PR.

---

### Task 1: Public API approval gate over Abstractions (#12)

**Files:**
- Modify: `Directory.Packages.props` (add `PublicApiGenerator`, `Verify.XunitV3`)
- Modify: `test/MMLib.Alvo.Abstractions.Tests/MMLib.Alvo.Abstractions.Tests.csproj` (reference both)
- Create: `test/MMLib.Alvo.Abstractions.Tests/PublicApiApprovalTests.cs`
- Create: `test/MMLib.Alvo.Abstractions.Tests/PublicApi.MMLib.Alvo.Abstractions.verified.txt` (baseline)

- [ ] **Step 1 — write the failing test.** `PublicApiApprovalTests.Public_api_has_not_changed()`: `var api = typeof(<marker in Abstractions>).Assembly.GeneratePublicApi(); await Verify(api).UseFileName("PublicApi.MMLib.Alvo.Abstractions");`. (Abstractions is source-free — resolve the assembly via `Assembly.Load("MMLib.Alvo.Abstractions")` since there is no type to reference.)
- [ ] **Step 2 — run, watch it fail.** `dotnet test --project ...Abstractions.Tests` → FAIL (no `.verified.txt`; Verify writes `.received.txt`).
- [ ] **Step 3 — accept the baseline.** Review the received content (empty/near-empty for a source-free assembly), rename to `PublicApi.MMLib.Alvo.Abstractions.verified.txt`, commit it.
- [ ] **Step 4 — run, watch it pass.** `dotnet test` green.
- [ ] **Step 5 — negative check.** Temporarily add a `public` type to Abstractions → test RED with a readable diff; revert → green.
- [ ] **Step 6 — commit.**

### Task 2: Encapsulation — explicit accessibility (#11)

**Files:** Modify `.editorconfig`.

- [ ] **Step 1 — baseline.** Confirm current code has explicit modifiers (build is clean).
- [ ] **Step 2 — tighten.** `dotnet_style_require_accessibility_modifiers = for_non_interface_members:warning` (was `:silent`).
- [ ] **Step 3 — build.** Expect green (no fallout) since existing members are explicit.
- [ ] **Step 4 — negative check.** A type declared with no accessibility modifier → IDE0040 warning → build error under warnings-as-errors; revert.
- [ ] **Step 5 — commit.**

### Task 3: ring1 wording — public-API approval now runs

**Files:** `scripts/test-ring1`, `scripts/test-ring1.ps1`.

- [ ] Replace the "land in PR2" placeholder line with a note that public-API approval runs inside `dotnet test`. Run `bash scripts/test-ring1` green. Commit.

### Task 4: `alvo-new-package` skill (Q18)

**Files:** Create `.claude/skills/alvo-new-package/SKILL.md`.

- [ ] **RED** — baseline: dispatch one subagent with "create a new MMLib.Alvo package" WITHOUT the skill; record which setup steps it misses (slnx registration, stripping inherited props, CPM, IsPackable decision, public-API baseline, `.Tests` project).
- [ ] **GREEN** — write the runbook addressing those gaps; description = "Use when creating a new project or package in the MMLib.Alvo.* family" (triggers only, no workflow summary). Reference package-boundary.md, alvo-dotnet-conventions, the os A conventions test, and the #12 public-API step.
- [ ] **Verify** — re-run the scenario WITH the skill; confirm the steps are followed.
- [ ] **Commit.** (If the baseline process warrants isolation, land this as a small follow-up PR instead.)

### Task 5: Verify, guard, PR

- [ ] `dotnet build -c Release`, `dotnet format --verify-no-changes`, `bash scripts/test-ring2` all green.
- [ ] Update the spec/plan + memory.
- [ ] Dispatch `alvo-plan-guard`; address blocking findings.
- [ ] Push branch, open PR (do **not** merge). Watch CI green (incl. Windows); triage CodeRabbit (≤2 rounds).

## Self-review notes

- #12 is the concrete new gate; #11 active surface is the encapsulation severity (rest latent from PR1). ✓
- No inline comments / Slovak; file-scoped namespaces; Shouldly-only. ✓
- Verify baseline committed so the gate is meaningful from the first run. ✓
