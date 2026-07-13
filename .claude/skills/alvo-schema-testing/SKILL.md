---
name: alvo-schema-testing
description: Use when writing or changing tests against the Alvo descriptor JSON Schema — the four schema test types.
---

# Alvo schema testing

This skill is deliberately thin. The descriptor JSON Schema
(`alvo-descriptor.schema.json`) and its full test mechanism are F2 work
(issue #17, bracketed `[13]` in the plan, milestone F2) and haven't landed
yet. What follows is the spec/analysis-level shape of the four test types the
schema needs, so that any test written against the schema before F2 lands
uses the right shape from the start instead of inventing a fifth pattern.
When F2 lands, expect this skill to grow — go read issue #17 for the full
treatment rather than assuming this document is complete.

## The four schema test types

1. **Meta-validation** — the schema itself must be a valid JSON Schema draft
   2020-12 document. This is a CI gate: it catches a malformed schema before
   it reaches anyone who'd try to validate a real descriptor against it.

2. **Examples against the schema** — every descriptor under `examples/`
   (including the demo project) must validate against the current schema. A
   schema change that breaks an example turns CI red. This is what stops
   "I tightened a constraint" from silently invalidating real, previously
   valid descriptors — the examples are the schema's regression suite.

3. **Round-trip property tests (CsCheck)** — generate a random valid
   descriptor, apply it, export it back out, and assert the export equals
   the input. This catches divergence between what the schema *says* is
   valid and what the implementation actually *does* with it — a class of
   bug that fixed examples alone can't reach because they only cover the
   cases someone thought to write down.

4. **Snapshot tests (Verify)** — snapshot the artifacts generated from a
   descriptor (the generated DB schema, the generated OpenAPI document, …).
   A change to the generator shows up as a visible diff in review, rather
   than as a silent behavior change nobody notices until it breaks something
   downstream.

## Why these four and not fewer

Each type catches a different failure mode: meta-validation catches a broken
schema, examples catch a schema that no longer accepts known-good input,
round-trip property tests catch schema↔implementation drift that no fixed
example would trigger, and snapshots catch unintended changes to what the
schema *produces*. Dropping one leaves a gap the others don't cover — treat
all four as required, not as options to pick from.
