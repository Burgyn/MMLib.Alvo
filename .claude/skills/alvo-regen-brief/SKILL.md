---
name: alvo-regen-brief
description: Use when docs/product/alvo-specifikacia.md or docs/product/baas-analyza.md change, or when the brief freshness gate blocks a commit, to regenerate docs/design-brief.en.md.
---

# Regenerate the Alvo design brief

`docs/design-brief.en.md` is a generated, deliberately lossy English compression
of the two Slovak source documents. It exists so the agent building Alvo can
load "the whole context in one breath" instead of re-reading ~200 KB of source
on every task. This skill is the repeatable procedure for regenerating it —
run it end to end whenever a source changes, or whenever
`scripts/check-brief-freshness` blocks a commit because the brief is stale.

This is a **semantic** compression task, not a text-processing script: read
both sources with understanding, then write real prose/bullets per section,
not a heading-only skeleton.

## Audience — read this before writing a word

The brief is for the agent **building** Alvo (the maintainer/contributor
persona: needs principles, invariants, and decisions to build correctly).
It is **not** the consumer `llms.txt` tracked in issue #26 / `[26]`, which
will describe Alvo to agents/users **consuming** the finished framework.
Same generated/English/lossy family, different audience and content — do not
merge or duplicate the two.

## Inputs

Read both source files **in full** before writing anything:

1. `docs/product/alvo-specifikacia.md` — the delivery strategy & technical
   spec. Contributes the **how** and the **order**: phases, ports, contracts,
   the technical shape of the solution.
2. `docs/product/baas-analyza.md` — the domain analysis. Contributes the
   **what** and the **why**: domain concepts, decisions, and the reasoning
   behind them.

Both are authoritative. The brief is a compression of their union, not of
either one alone.

## Output

Write one file: `docs/design-brief.en.md`.

- **English**, regardless of the sources being Slovak.
- **Deliberately lossy** — this is a compression, not a translation. See
  "Compression quality test" below for how lossy is too lossy.
- Split into exactly these sections, **in this order**:
  1. **Principles** (§0)
  2. **Two modes**
  3. **Ports & guarantees**
  4. **Hard invariants / contracts**
  5. **Key decisions + why**
  6. **Boundaries** — must cover: descriptor ≠ infra, MCP is an adapter (not a
     special case), the two sources of truth (repo file vs. DB record), and
     the computed/rollup/hook ladder.
  7. **Phase map**

## Keep / drop rules

Apply these while drafting every section:

- **Keep:** principles, hard invariants / port guarantees, decisions together
  with their *why*, boundaries.
- **Drop:** narrative prose, competitor case studies, deliberation history
  (the back-and-forth that led to a decision — keep only the decision + why),
  illustrative code examples.

When in doubt whether something survives compression, apply the compression
quality test below rather than guessing.

## Compression quality test

State this test to yourself while drafting, and re-check the finished brief
against it:

> After reading only the brief, would the agent make any decision it would
> **not** have made after reading the full spec + analysis? If dropping
> something would cause a shortcut or a wrong call, that thing was kept
> incorrectly out — put it back.

A brief that passes this test can be much shorter than the sources while
still being safe to build from. A brief that fails it is not a compression,
it is data loss.

## Header — write it, but hash it last

At the very top of `docs/design-brief.en.md`, emit one warning line and one
`brief-source:` marker per source file, each on its own HTML-comment line
(this exact single-line-per-marker form is what
`scripts/check-brief-freshness` parses — do not fold the two markers into one
multi-line comment):

```
<!-- GENERATED — do not hand-edit. Regenerate via the alvo-regen-brief skill. -->
<!-- brief-source: docs/product/alvo-specifikacia.md sha256:<hash> -->
<!-- brief-source: docs/product/baas-analyza.md sha256:<hash> -->
```

`<hash>` is the lowercase-hex SHA-256 of the source file's raw bytes:

```bash
sha256sum docs/product/alvo-specifikacia.md   # or: shasum -a 256 <file> if sha256sum is unavailable
sha256sum docs/product/baas-analyza.md
```

**Compute the hashes as the last step of this procedure**, after the body of
the brief is finished and about to be saved. If you hash first and then keep
editing, the header will not match what actually gets committed and
`scripts/check-brief-freshness` (and the `.githooks/pre-commit` hook that
calls it) will report the brief as stale.

## Freshness enforcement (why this matters)

The brief must be regenerated every time either source file changes.
Freshness is enforced two ways, both anchored on the header hashes this skill
writes:

- `scripts/check-brief-freshness` recomputes each source's SHA-256 and
  compares it against the corresponding `brief-source:` marker; it exits
  non-zero on any mismatch or missing marker.
- `.githooks/pre-commit` runs that check automatically whenever a commit
  stages a change to either source file or to the brief itself, and blocks
  the commit on failure.

On top of that deterministic gate, the `alvo-plan-guard` subagent may
additionally flag a brief that is technically fresh (hashes match) but
**shallowly compressed** — i.e. it passes the hash check but fails the
compression quality test above. Treat that flag the same as a failed hash
check: regenerate properly, don't just patch the hash.

## Procedure summary

1. Read `docs/product/alvo-specifikacia.md` and `docs/product/baas-analyza.md`
   in full.
2. Draft the seven sections in order, applying the keep/drop rules.
3. Run the compression quality test against the draft; fix any section that
   fails it.
4. Compute the SHA-256 of both source files (last step).
5. Prepend the GENERATED warning line and the two `brief-source:` markers
   with those hashes.
6. Save `docs/design-brief.en.md` and commit it together with the header
   hashes that match the sources as they exist in that commit.
