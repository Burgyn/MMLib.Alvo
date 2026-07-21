# Alvo descriptor JSON Schema

`project.schema.json` is the canonical Alvo project-descriptor schema (JSON
Schema draft 2020-12), `$id` `https://alvo.dev/schema/v1/project.json`. It is
the single contract for every path: Docker mount, `alvo apply`, the Management
API, `AddAlvo().FromDescriptor()`, and admin-UI export.

It supersedes the earlier draft that lived at
`docs/product/alvo-descriptor.schema.json`.

- **Validated in CI** by `test/MMLib.Alvo.Schema.Tests` (Corvus.Json.Validator,
  Apache-2.0) across four test types: meta-validation against the draft
  2020-12 meta-schema, the `examples/` corpus (positive + negative), a
  canonical-form round-trip/mutation property (CsCheck), and error-output +
  canonical snapshots (Verify).
- **Reference descriptors** live in `examples/` (`simple-tasks`, `complex-crm`).

Versioning: within `v1` the format evolves additively only (new optional
properties, new enum values, loosened constraints). A breaking change becomes
`alvo.dev/v2` at a new URL; `v1` stays published.
