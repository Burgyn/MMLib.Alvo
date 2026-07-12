# Design — CONTRIBUTING.md + CLA + Code of Conduct (issue #2)

> Source issue: [#2] `[1b]` CONTRIBUTING.md + CLA (contributor license agreement), label `ready`.
> Parent spec: `specs/alvo-specifikacia.md` §264, §419 (open-core: Apache-2.0 core forever,
> commercial `Alvo.Enterprise.*` add-ons + hosting; possible later commercialization is
> *adding add-ons, never relicensing the core*).
> Status: **brainstormed and implemented in the same branch** (`contributing-cla`) at the
> maintainer's request — the design was fully delegated ("do it properly, I won't read it").

## Goal

Introduce the contributor-onboarding set **on day one**, before any external PR exists, so
that copyright on future contributions never fragments. Without a CLA, copyright on external
PRs stays with each contributor, making a later relicensing impossible — the trap the spec
explicitly calls out. The CLA is honest and transparent about *why* it exists, to soften the
reputational cost a CLA usually carries.

## Guiding decisions

1. **Runbook, not automation, for the bot wiring.** Linking `cla-assistant.io` to the repo
   requires the maintainer's GitHub OAuth (authorizing a third-party app, granting it
   status-check/webhook access). An agent cannot and should not perform that on the
   maintainer's behalf, so this design ships every *file* and provides a manual checklist
   (below) for the one-time bot setup.
2. **Custom CLA text, "light" version.** The contributor **keeps copyright**; grants a broad,
   perpetual, sublicensable, irrevocable copyright + patent license that permits relicensing
   beyond Apache-2.0. This is a grant, not an assignment. Plain-English, not a legal
   template, because transparency is the point.
3. **Individual *and* Corporate CLA from the start.** Two documents in `docs/legal/`. The
   Corporate CLA covers employees contributing within the scope of their employment (the case
   where an individual's grant may be legally insufficient because the employer owns the IP).
4. **Layered, de-duplicated docs — but `CLAUDE.md` is not stripped.** `CLAUDE.md` is
   auto-loaded into agent context; `CONTRIBUTING.md` is not. Agent-critical conventions (the
   comment policy, disallowed packages, CPM, "no direct push to `main`") therefore **stay** in
   `CLAUDE.md` as the source of truth. `CONTRIBUTING.md` is the human-facing entry point and
   *links back* to `CLAUDE.md` for the detailed style rules, so there is no divergent second
   copy of them. `CLAUDE.md` gains only a one-paragraph pointer to `CONTRIBUTING.md`.
5. **Project identity:** Miňo Martiniak, mino.martiniak@gmail.com — keeps the project's legal
   identity independent of any employer.

## Files shipped in this change

```
CONTRIBUTING.md                  # root — human entry point; GitHub surfaces it on PRs/issues
CODE_OF_CONDUCT.md               # root — Contributor Covenant v2.1, contact = maintainer email
docs/legal/CLA-INDIVIDUAL.md     # canonical Individual CLA text (versioned, PR-reviewable)
docs/legal/CLA-CORPORATE.md      # canonical Corporate CLA text
CLAUDE.md                        # + one pointer paragraph to CONTRIBUTING.md (no stripping)
CHANGELOG.md                     # [Unreleased] → Added entry
docs/superpowers/specs/2026-07-12-contributing-cla-design.md   # this document
```

### `CONTRIBUTING.md` structure

Welcome → Building & testing (`dotnet build`/`dotnet test`, MTP not VSTest, `net10.0`,
SDK pinned) → Coding conventions (short summary + link to `CLAUDE.md` as source of truth) →
Pull request process (branch off `main`, no direct push, PR + CI required) → CLA (the honest
paragraph + links to both CLA docs + how the bot works) → Code of Conduct (link).

### CLA text — substance

Both documents share the same load-bearing clauses: copyright retained by the contributor;
broad copyright license *including the right to relicense beyond Apache-2.0*; patent license
with defensive termination; representations of originality / right to contribute; "AS IS", no
warranty; electronic one-time signing via the bot. The Individual CLA adds a third-party
materials clause and points employed contributors at the Corporate CLA. Each document carries
a plain-language summary at the top and an explicit "not reviewed by a lawyer" disclaimer at
the bottom.

## Manual runbook — wiring `cla-assistant.io` (maintainer, one-time)

This is the part not automatable from the repo. Do it once:

1. **Create the CLA Gist.** At <https://gist.github.com>, create a Gist (public) whose body
   is the CLA text. `cla-assistant.io` supports **both an Individual and a Corporate section
   in one Gist** — paste both, using `docs/legal/CLA-INDIVIDUAL.md` and
   `docs/legal/CLA-CORPORATE.md` as the source. Keep the Gist in sync with those files
   whenever the canonical text changes (the repo files are the source of truth; the Gist is a
   mirror the bot can read).
2. **(Optional) Add a custom field for the corporate case.** Add a metadata file to the Gist
   describing a form field such as *"Company / Organization — leave blank if contributing as
   an individual"*, so corporate contributors self-identify at signing.
3. **Authorize the bot.** Go to <https://cla-assistant.io/>, sign in with GitHub, install /
   authorize the CLA Assistant app for the `Burgyn/MMLib.Alvo` repository.
4. **Link repo → Gist.** In the CLA Assistant dashboard, "Configure CLA", select
   `Burgyn/MMLib.Alvo`, and choose the Gist from step 1.
5. **Verify the definition of done** with a throwaway test PR from a second account (or ask a
   collaborator): the bot must **block merge** until the CLA is signed, the **first** PR must
   prompt for signing, and a **subsequent** PR from the same account must pass without
   re-prompting.
6. **Make it required (defer to issue [5]).** Marking the `CLA Assistant` status check as
   *required* in branch protection is owned by issue [5] "Build system + basic CI", which
   handles branch-protection wiring. Note it there; it is intentionally **not** done in this
   change.

## Out of scope (deferred)

- The actual OAuth authorization + repo linking (manual runbook above — needs the
  maintainer's GitHub account).
- Marking the CLA status check *required* in branch protection (issue [5]).
- Git hooks / richer PR tooling (issue [11]).
- A README (issue #30 `[25]`).

## Definition of Done

- `CONTRIBUTING.md`, `CODE_OF_CONDUCT.md`, and both CLA documents exist and are internally
  consistent and cross-linked.
- `CONTRIBUTING.md` transparently explains *why* the CLA exists (fund the future via
  commercial add-ons without relicensing contributions), and that the core stays Apache-2.0.
- `CLAUDE.md` still carries the agent conventions and now points to `CONTRIBUTING.md`.
- The runbook above lets the maintainer satisfy the issue's operational DoD: bot blocks merge
  without a signed CLA; first PR prompts, subsequent PRs don't.

## Caveats

The CLA / CCLA text is a plain-English draft aimed at achieving what the issue describes. It
is **not** a substitute for review by a lawyer and should be checked before it becomes
load-bearing for a future `Alvo.Enterprise.*` business.
