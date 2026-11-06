---
name: alvo-descriptor-indexes
description: Use when an Alvo descriptor change should speed up a query, make fields unique among some rows, or keep date ranges from overlapping — composite, partial and exclusion indexes.
---

# Indexes in an Alvo descriptor

Alvo already indexes, without being asked, the primary key `id`, every field with `"unique": true`, and every `ref`
to a declared entity. A `ref` to the built-in `users` is not indexed by itself. Declare an index only beyond those:

- A field's `"index": true` is the one-field form. Prefer it for a single field.
- The entity's `indexes` list is for composite indexes, fields in query order: filter on the first, then the next.
  `{"fields": ["active", "specialization"]}` on `technicians` speeds up "the active technicians of one
  specialization".
- `{"fields": [...], "unique": true}` makes the **combination** unique — one row per combination of values. A
  field's own `"unique": true` makes that one field unique across the entity, which is a different rule.
- On a tenant-scoped entity both kinds are unique **per tenant**: Alvo puts `tenant_id` first in every unique index,
  so two tenants may hold the same value.
- A new `unique` index cannot be created while duplicate rows exist, so say that when you propose one. A plain index
  never rejects a write.

## Partial indexes and exclusions

- `where` makes an index **partial**: only the rows its CEL accepts are indexed. With `"unique": true` the
  combination is unique **among the rows it accepts** — duplicates outside it are allowed, so
  `{"fields": ["fleet_bike_id", "starts_at"], "unique": true, "where": "status != 'cancelled'"}` lets a cancelled
  rental share its slot. The filter reads this entity's own fields only: no caller, no `now()`, no timestamp
  literal, and no hidden field.
- `exclude` keeps ranges apart: `{"fields": ["fleet_bike_id"], "exclude": {"from": "starts_at", "to": "due_at"}}`
  means no two rows with equal `fields` hold overlapping `[from, to)` ranges. The range is half-open — one rental
  may start the minute another ends — and an empty range overlaps nothing. `from` and `to` are two required `date`
  or two required `datetime` fields a caller writes, neither hidden nor in `fields`. A write that would overlap is
  refused 409 with the code `exclusion`, pointing at the `from` field. An exclusion is never also `unique`; add
  `where` to leave cancelled rows out.
- Rows that already overlap make the apply refuse the exclusion, so say that when you propose one. On PostgreSQL it
  needs the btree_gist extension: the apply installs it when the database role may, and refuses with the fix
  otherwise. Rows deleted by `softDelete` still count, for `exclude` as for `unique`.
- Neither is accepted on a `storage: dynamic` entity.

The shape: `schema/project.schema.json#/$defs/entity/properties/indexes`.

Adding one depends on whether the entity already has the list:

- It has `indexes`: `add` the new index at `/entities/<entity>/indexes/-`.
- It has none: `add` at `/entities/<entity>/indexes` with a list holding the new index,
  `[{"fields": ["active", "specialization"]}]`. An append to a list that does not exist is refused.

<!-- example: index-technicians-by-specialization -->
**Speed up listing the active technicians of one specialization.**

```json
{"tool": "propose_change", "baseRevision": 1, "summary": "Indexes technicians by active and specialization.",
 "operations": [{"op": "add", "path": "/entities/technicians/indexes",
                 "value": [{"fields": ["active", "specialization"]}]}]}
```

```json
{"valid": true, "changedPaths": ["/entities/technicians/indexes"]}
```

<!-- example: exclude-overlapping-rentals -->
**Never rent one bike twice at once, unless a rental is cancelled.**

```json
{"tool": "propose_change", "baseRevision": 1, "summary": "Refuses overlapping rentals of one bike.",
 "operations": [{"op": "add", "path": "/entities/rentals/indexes/-",
                 "value": {"fields": ["fleet_bike_id"], "exclude": {"from": "starts_at", "to": "due_at"},
                           "where": "status != 'cancelled'"}}]}
```

```json
{"valid": true, "changedPaths": ["/entities/rentals/indexes/2"]}
```

In the dashboard: read with `get_descriptor`, then `check_change` or `propose_change` the operation.
In this repo: edit `examples/**/*.alvo.json` or your own descriptor, then run `scripts/test-ring0` or `PUT …/descriptor?dryRun=true`.
