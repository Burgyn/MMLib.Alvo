# F1 — Architecture rules + Public API gate (issues #11 + #12) — design

> Slice covering GitHub issues **#11 [7] Architecture tests** and **#12 [8]
> Public API approval gate**, the second F1 PR (after #58 = #10 + #9). Builds
> directly on PR1's test infrastructure (os A conventions test, os B linked
> architecture rules, `MMLib.Alvo.Testing`).

## State after PR1

The architecture-test *machinery* already landed in #58: os A
(`MMLib.Alvo.Conventions.Tests`, file-scan conventions) and os B
(`test/_shared/SharedArchitectureRules.cs`, linked NetArchTest rules run against
each project's sibling assembly). #11's active rule ("no public types in a
`*.Internal` namespace") and a latent, skipped `Core_depends_only_on_Abstractions`
are already present. So most of #11 is either done or genuinely latent until a
core/providers exist (F2/F3).

## What this PR adds

### #12 — Public API approval gate (the concrete deliverable)

- **PublicApiGenerator + Verify** (decision Q1) over the **packable** assemblies.
  Today that is only `MMLib.Alvo.Abstractions`.
- A `PublicApiApprovalTests` in `test/MMLib.Alvo.Abstractions.Tests` generates
  the assembly's public API and snapshot-checks it with Verify against a
  committed `*.verified.txt` baseline. Abstractions is source-free, so the
  baseline locks in "nothing public yet"; any future public member fails the
  test until the baseline is consciously updated (= an acknowledged API change).
- **Dedicated per-project test, not linked** (unlike os B): Verify locates its
  baseline via the caller's source-file path, which a single *linked* file would
  collapse onto one shared location for every assembly. A per-project test keeps
  each baseline next to its own project; the `alvo-new-package` runbook records
  "add a public-API approval test" as a step for each new packable package.
- Runs inside `dotnet test` (ring1's "public-API approval" slot).

### #11 — Encapsulation, finishing the editorconfig part

- #11's DoD asks for "internal-by-default … set in `.editorconfig` via analyzer
  severity." Bump `dotnet_style_require_accessibility_modifiers` from `silent`
  to `warning` so every member's accessibility is explicit (no accidental
  defaults); combined with the #12 gate, widening the public surface becomes a
  deliberate, reviewed act. The remaining #11 rules (core→only Abstractions,
  no cross-provider dependency, vertical-slice folder guards) stay latent/skipped
  until the code they guard exists.

### Skill — `alvo-new-package`

- The setup runbook (decision Q18) an agent follows when creating a new package,
  paired with the os A conventions test as its enforcement. Authored per
  `writing-skills` (baseline check before deploy). May land as a small follow-up
  if the baseline process warrants its own PR.

## Decisions carried in

- Q1 PublicApiGenerator + Verify · Q12 Shouldly-only · Q17 latent rules use
  `[Fact(Skip)]` · analyzers-as-gate + English-only-in-code + no-inline-comments
  from the #58 review round all apply.

## Out of scope

- Mutation testing (#13 → PR4), SemVer/release (#14 → PR3), git hooks + PR
  template (#15 → PR5). Real dependency/vertical-slice arch rules land with the
  core in F2/F3.

## Definition of done

Adding a public member to `MMLib.Alvo.Abstractions` without updating the baseline
turns the public-API test red with a readable diff; a member left with no
accessibility modifier warns (→ build error under warnings-as-errors); the whole
suite stays green on Linux + Windows.
