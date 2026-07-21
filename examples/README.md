# Alvo descriptor examples

Reference descriptors validated against `schema/project.schema.json`
(the type-2 "examples against the schema" corpus, F2 issue #17).

- **`simple-tasks/`** — the smallest real backend: two owned entities
  (`projects`, `tasks`), ownership rules, a `count` rollup, `audit` +
  `softDelete`, a `beforeUpdate` mutate, one composite index.
- **`complex-crm/`** — the analysis §16 CRM adapted to v1, exercising most of
  the surface: multi-tenancy (`tenancy.enabled` + a `global` číselník),
  dynamic-entities governance (`dynamicEntities.defaultRules` + quotas),
  `rollup.via`, a `computed` field reading a `rollup` (`gross_total`),
  a declarative `formats` entry (`sk-ico`) referenced by a field,
  field-level per-role masking (`hidden` as CEL), tagged `{"$cel": …}` values,
  `renamedFrom`, `templates`, outbound `webhooks`, a `batch`-delivery
  automation rule, a scheduled rule delegating to a `function`, and `x-` keys.
  It is a real **bundle** (D3): `crm.alvo.json` alongside
  `templates/invoice-issued.html` (referenced via `bodyFile`) and
  `functions/remind-stale-deals.csx` (referenced via `script`).
- **`_negative/`** — descriptors that MUST be rejected, each proving one
  constraint (unknown property, `decimal` missing `scale`, the reserved
  `users` entity name, a wrong `apiVersion`). The test asserts they fail with
  the expected JSON-pointer location, not merely that they fail.

## Validating

Validated in CI by `test/MMLib.Alvo.Schema.Tests` (Corvus.Json.Validator):
every descriptor here must validate against `schema/project.schema.json`, and
every fixture under `_negative/` must be rejected at the JSON-pointer location
declared in `_negative/expectations.json`.
