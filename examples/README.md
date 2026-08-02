# Alvo descriptor examples

Reference descriptors validated against `schema/project.schema.json`
(the type-2 "examples against the schema" corpus, F2 issue #17).

- **`simple-tasks/`** — the smallest real backend, and the one to start from:
  two owned entities (`projects`, `tasks`), ownership rules, an `enum`, `audit`,
  one composite index. **Applies as it stands.** It used to carry a `count`
  rollup, per-field `default`s and a `beforeUpdate` mutate; those were removed
  when the apply-time refusals below landed, because an example that cannot be
  applied is worse than a smaller one.
- **`complex-crm/`** — **a format showcase, not a runnable backend** (see
  `complex-crm/NOT-RUNNABLE.md`): the analysis §16 CRM adapted to v1, exercising
  most of the surface *including keys this build refuses*, which is exactly why
  applying it fails. It is the schema corpus's one full-surface fixture, covering
  multi-tenancy (`tenancy.enabled` + a `global` číselník),
  dynamic-entities governance (`dynamicEntities.defaultRules` + quotas),
  `rollup.via`, a `computed` field reading a `rollup` (`gross_total`),
  a declarative `formats` entry (`sk-ico`) referenced by a field,
  field-level per-role masking (`hidden` as CEL), tagged `{"$cel": …}` values,
  `renamedFrom`, `templates`, outbound `webhooks`, a `batch`-delivery
  automation rule, a scheduled rule delegating to a `function`, and `x-` keys.
  It is a real **bundle** (D3): `crm.alvo.json` alongside
  `templates/invoice-issued.html` (referenced via `bodyFile`) and
  `functions/remind-stale-deals.csx` (referenced via `script`).
- **`vehicle-registry/`** — **applies as it stands.** The #23 demo: owners, their vehicles, and
  periodic roadworthiness inspections. Exercises two `ref` chains
  (`vehicles.owner_id` → `owners`, `inspections.vehicle_id` → `vehicles`,
  the latter `onDelete: cascade`), a composite index on each of `vehicles`
  and `inspections`, `audit` on both `owners` and `vehicles`, and a
  `renamedFrom` on `vehicles.plate` (was `license_plate`). Doubles as the
  fixture for the per-engine generated-SQL snapshot tests (the EF-drift
  guard) in `MMLib.Alvo.Data.Sqlite.Tests` / `.Data.PostgreSql.Tests.Integration`.
- **`_negative/`** — descriptors that MUST be rejected, each proving one
  constraint (unknown property, `decimal` missing `scale`, the reserved
  `users` entity name, a wrong `apiVersion`). The test asserts they fail with
  the expected JSON-pointer location, not merely that they fail.

## Declared in the schema, refused at apply

The descriptor schema is the **v1 format**, and this build does not implement all of it yet. Rather than
accepting a key and quietly doing nothing with it, Alvo **refuses the descriptor at apply**, naming what
would silently have happened and what to do instead — a descriptor that asks for behaviour it does not get
is a lie its author cannot see. Five features are in that state, and each is one entry in
`UnhonouredFeatures`, the single table both the mapper and the descriptor validator read:

| Feature | What you would silently get | Tracked in |
|---|---|---|
| `field.computed` | the expression never runs; the column stays null | #21 (CEL→SQL) |
| `field.rollup` | nothing maintains the aggregate; a null column that looks like data | PR6 |
| `field.validation` | the expression is never evaluated, so a value it forbids is accepted — the field is not constrained at all | #22 (before-hooks) |
| `field.default` | no column default is emitted and the value is dropped; on a `required` field, an INSERT of NULL into NOT NULL | PR6 |
| `entity.softDelete` | DELETE removes the row and reads do not exclude it — irrecoverable loss where the contract promises recovery | soft-delete issue |
| `entity.hooks.*` | the hooks never run, so a write the author believes is vetted or patched is neither | #22 (hooks pipeline) |

Hooks are refused **per hook point** (`beforeCreate`, `afterUpdate`, …) rather than as a block, so #22 can
lift them one at a time as each starts working.

Writing `softDelete: false`, or an empty `beforeUpdate: []`, is **not** a declaration and maps normally —
declining a feature is not asking for it.

Every entry leaves the table on the day its feature lands, and
`DescriptorToSchemaMapperTests.Every_runnable_example_maps_without_refusal` holds the examples here to it.

## Validating

Validated in CI by `test/MMLib.Alvo.Schema.Tests` (Corvus.Json.Validator):
every descriptor here must validate against `schema/project.schema.json`, and
every fixture under `_negative/` must be rejected at the JSON-pointer location
declared in `_negative/expectations.json`.
