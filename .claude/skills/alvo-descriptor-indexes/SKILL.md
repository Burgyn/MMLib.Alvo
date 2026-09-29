---
name: alvo-descriptor-indexes
description: Use when an Alvo descriptor change should speed up a query or make a combination of fields unique — composite and unique-per-group indexes, and the indexes Alvo already creates by itself.
---

# Indexes in an Alvo descriptor

Alvo already indexes, without being asked, the primary key `id`, every field with `"unique": true`, and every `ref`
field. Declare an index only beyond those:

- A field's `"index": true` is the one-field form. Use it for a single field.
- The entity's `indexes` list is for two or more fields, in query order: filter on the first, then the next.
  `{"fields": ["active", "specialization"]}` on `technicians` speeds up "the active technicians of one
  specialization".
- `{"fields": [...], "unique": true}` makes the **combination** unique — one row per pair. A field's own
  `"unique": true` is uniqueness across the whole table, which is a different rule.
- A new `unique` index cannot be created while duplicate rows exist, so say that when you propose one. A plain index
  never rejects a write.

The shape: `schema/project.schema.json#/$defs/entity/properties/indexes`.

Adding one depends on whether the entity already has the list:

- It has `indexes`: `add` the new index at `/entities/<entity>/indexes/-`.
- It has none: `add` at `/entities/<entity>/indexes` with a list holding the new index,
  `[{"fields": ["active", "specialization"]}]`. An append to a list that does not exist is refused.

In the dashboard: read with `get_descriptor`, then `check_change` or `propose_change` the operation.
In this repo: edit the descriptor file and run the validator (`dotnet test`), or the Management API.
