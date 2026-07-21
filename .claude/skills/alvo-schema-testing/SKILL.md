---
name: alvo-schema-testing
description: Use when writing or changing tests against the Alvo descriptor JSON Schema — the four schema test types.
---

# Alvo schema testing

The canonical descriptor JSON Schema now lives at `schema/project.schema.json`
(draft 2020-12, `$id` `https://alvo.dev/schema/v1/project.json`), and the four
test types below are implemented in `test/MMLib.Alvo.Schema.Tests` against it
(Corvus.Json.Validator + CsCheck + Verify), landed in F2 (issues #16/#17/#57).
What follows is the spec/analysis-level shape of those four types; go read
issue #17 and the test project for the full treatment rather than assuming this
document is complete.

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

## Dynamic entities: where they do and don't fit these four

Dynamic (metadata-driven) entity *definitions and records* are created by
end-users at *runtime* and live in metadata tables (`entity_definitions` /
`field_definitions`), not in the descriptor — so their per-entity *runtime*
shape is not what these four test types validate. Their descriptor-level
*configuration*, however, is a different matter. Two things about dynamic
entities do land in scope, and one deliberately does not:

- **In scope — descriptor touchpoints.** The descriptor schema carries
  dynamic-entity configuration, e.g. an index over a JSON path
  (`schema/project.schema.json`: "index … for dynamic entities = generated
  column + index over the JSON path"). An example descriptor exercising that
  belongs in the examples suite (type 2) and its generated artifacts in the
  snapshot suite (type 4).
- **Out of scope — the parity guarantee.** "The same adversarial and policy
  test suite passes identically over a physical and a virtual entity" (spec
  §2.1 acceptance criteria) is a *data-layer / security-core* test, verified per
  `alvo-security-core-review` — **not** a descriptor-schema test. Named here
  only so runtime-entity validation isn't assumed to live here and fall through
  the crack between the two skills.
