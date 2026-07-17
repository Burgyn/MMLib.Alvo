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
- **Linked, like the os B architecture rules** — `test/_shared/PublicApiApprovalTests.cs`
  runs against each test project's sibling assembly (shared `TestTarget`
  resolver), so a new packable package's `*.Tests` gets the gate automatically
  and it composes with `dotnet-affected`. Verify keys its baseline off the caller
  path — which a single linked file would collapse across assemblies and a
  deterministic CI build would remap — so `VerifyModuleInit` derives the baseline
  directory from each test assembly's name (`Verifier.DerivePathInfo` +
  `RepositoryRoot`), keeping the baseline **next to its own `*.Tests` project**,
  and `UseFileName($"PublicApi.{assembly}")` keeps the baselines distinct. The
  `alvo-new-package` runbook's only per-package step is "accept the generated
  baseline". Verified (local + CI incl. Windows): a fresh packable `*.Tests`
  auto-ran the gate against its sibling and produced its own
  `PublicApi.<assembly>.received.txt` in its own project directory.
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

- The setup runbook (decision Q18), paired with the os A conventions test as its
  enforcement. Landed in this PR. A `writing-skills` RED probe showed a capable
  agent largely rediscovers the steps from the repo (the conventions test +
  existing skills already carry the mechanical load), so the skill is
  intentionally **lean** — judgment (is the package earned?) + the few
  non-obvious steps a test can't check + pointers, rather than a heavy runbook.

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
