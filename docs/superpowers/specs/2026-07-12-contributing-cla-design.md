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
2. **CLA based on the Project Harmony templates (not a bespoke draft).** After an initial
   custom draft, the maintainer chose to use the recognised, widely-used **Harmony
   Agreements v1.0** templates instead: `HA-CLA-I` ("any licence" variant) for individuals and
   `HA-CLA-E` (outbound Option Five, "any licence") for entities. The contributor **keeps
   copyright** (a licence, not an assignment); §2.3 grants the right to relicense the combined
   Material under *any* licence, including commercial/proprietary — exactly the future-funding
   option the issue requires. Adaptations to the verbatim templates are minimal: the named
   project/maintainer, the submission method (the CLA bot + `CONTRIBUTING.md`), the Media
   licences (Option Five), and the governing law. Harmony is CC-BY 3.0, so each file keeps an
   attribution line. Each document carries a plain-language summary on top and an explicit
   "not lawyer-reviewed" disclaimer at the bottom.
3. **Individual *and* Corporate CLA from the start.** Two documents in `docs/legal/`. The
   Corporate CLA covers employees contributing within the scope of their employment (the case
   where an individual's grant may be legally insufficient because the employer owns the IP).
4. **Layered docs — `CLAUDE.md` is left untouched.** `CLAUDE.md` is auto-loaded into agent
   context; `CONTRIBUTING.md` is not. Agent-critical conventions (the comment policy,
   disallowed packages, CPM, "no direct push to `main`") therefore **stay** in `CLAUDE.md` as
   the source of truth, unchanged. `CONTRIBUTING.md` is the human-facing entry point and
   *links to* `CLAUDE.md` for the detailed style rules, so there is no divergent second copy
   of them. `CLAUDE.md` itself is not modified by this change.
5. **Project identity:** Miňo Martiniak, mino.martiniak@gmail.com — keeps the project's legal
   identity independent of any employer.

## Files shipped in this change

```
CONTRIBUTING.md                  # root — human entry point; GitHub surfaces it on PRs/issues
CODE_OF_CONDUCT.md               # root — Contributor Covenant v2.1, contact = maintainer email
docs/legal/CLA-INDIVIDUAL.md     # canonical Individual CLA text (versioned, PR-reviewable)
docs/legal/CLA-CORPORATE.md      # canonical Corporate CLA text
CHANGELOG.md                     # [Unreleased] → Added entry
docs/superpowers/specs/2026-07-12-contributing-cla-design.md   # this document
```

### `CONTRIBUTING.md` structure

Welcome → Building & testing (`dotnet build`/`dotnet test`, MTP not VSTest, `net10.0`,
SDK pinned) → Coding conventions (short summary + link to `CLAUDE.md` as source of truth) →
Pull request process (branch off `main`, no direct push, PR + CI required) → CLA (the honest
paragraph + links to both CLA docs + how the bot works) → Code of Conduct (link).

### CLA text — substance

Both documents are the Harmony v1.0 templates. Load-bearing clauses: copyright retained by the
contributor (§2.1(a), §2.6 reservation of rights); a broad, sublicensable, irrevocable
copyright licence (§2.1(b)) and patent licence (§2.2); the **§2.3 outbound licence granting the
right to relicense under any licence including commercial/proprietary** — the whole point;
moral-rights waiver (§2.4); representations of authority and ownership (§3); "AS IS" disclaimer
(§4) and consequential-damage waiver (§5). The Individual CLA (§3(c)) points employed
contributors at the Corporate CLA; the Corporate CLA adds Legal Entity / Affiliates definitions
and authority-to-bind. Signing is one-time and electronic via the bot.

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
- `CLAUDE.md` is unchanged and still carries the agent conventions as the source of truth.
- The runbook above lets the maintainer satisfy the issue's operational DoD: bot blocks merge
  without a signed CLA; first PR prompts, subsequent PRs don't.

## Caveats

The CLA / CCLA are the Harmony v1.0 templates — recognised and widely used, but the adaptation
(notably **governing law set to the Slovak Republic**, matching the maintainer's jurisdiction)
is **not** a substitute for review by a lawyer and should be checked before it becomes
load-bearing for a future `Alvo.Enterprise.*` business. Governing law and the maintainer's
legal identity (currently the natural person Miňo Martiniak, not a company) are the two points
most worth a professional's eye.
