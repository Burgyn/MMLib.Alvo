---
name: alvo-descriptor-entities-and-fields
description: Use when an Alvo descriptor change adds, renames or removes an entity or a field, or sets a field's required, unique, default, hidden or readOnly — the paths, the keys each level takes, and how a rename keeps its data.
---

# Entities and fields in an Alvo descriptor

An entity lives at `/entities/<entity>` and a field at `/entities/<entity>/fields/<field>`, both named in lower-case
snake_case. Pointers are RFC 6901 and operations RFC 6902 JSON Patch.

An entity takes these keys:

<!-- gen:entity-keys -->
`description` `renamedFrom` `storage` `tenancy` `softDelete` `audit` `fields` `rules` `hooks` `realtime` `indexes`
<!-- /gen:entity-keys -->

A field takes these keys:

<!-- gen:field-keys -->
`type` `description` `renamedFrom` `required` `unique` `nullable` `default` `maxLength` `precision` `scale` `values` `entity` `onDelete` `format` `validation` `index` `hidden` `readOnly` `computed` `rollup`
<!-- /gen:field-keys -->

The schema declares every key above, and this build refuses one of them outright: a field's `validation` is not
evaluated yet, so it is refused at apply rather than accepted as a constraint that holds nothing. Express the rule
with a facet the API does check (`maxLength`, `precision` and `scale`, enum `values`, a `format`) or a before-hook.
Types and facets are in `alvo-descriptor-field-types-and-formats`; `computed` and `rollup` have their own skill.

- `required` makes the field NOT NULL; `nullable` is derived from it and is rarely written.
- `unique` is uniqueness across the entity, per tenant on a tenant-scoped one (see `alvo-descriptor-indexes`).
- `default` is honoured as a JSON literal, which becomes the column default. The schema also allows
  `{"$cel": "…"}`, and this build refuses it: send the value on create instead.
- `hidden` keeps the field out of every API response and `readOnly` keeps a caller from writing it. Each is `true`,
  or a CEL condition that decides per caller: it may read `@user` and `@tenant`, never the row's own fields, which
  is refused.
- A `required` field that is `"readOnly": true` can never be created, and is refused, unless a literal `default`,
  `computed` or `rollup` supplies its value.

## Renames keep data; removals lose it

A rename is a `move` of the member plus `renamedFrom` naming the old name. Without `renamedFrom` the apply sees a
drop and an add, and the column's data is lost. `move` puts the member last in its object; that order means nothing.
The same holds for an entity: `move` it under `/entities` and set its `renamedFrom`.

<!-- example: rename-technician-phone -->
**Rename `technicians.phone` to `phone_number`, keeping every number.**

```json
{"tool": "propose_change", "baseRevision": 1, "summary": "Renames technicians.phone to phone_number.",
 "operations": [{"op": "move", "from": "/entities/technicians/fields/phone", "path": "/entities/technicians/fields/phone_number"},
                {"op": "add", "path": "/entities/technicians/fields/phone_number/renamedFrom", "value": "phone"}]}
```

```json
{"valid": true, "changedPaths": ["/entities/technicians/fields/phone", "/entities/technicians/fields/phone_number",
                                 "/entities/technicians/fields/phone_number/renamedFrom"]}
```

A `remove` of a field or an entity drops its data. Such a plan is destructive, and only the operator can allow it:
say first what data it loses. `validation` is refused the same way whatever its expression:

<!-- example: validation-refused -->
**A field's `validation` is refused.**

```json
{"tool": "check_change", "baseRevision": 1,
 "operations": [{"op": "add", "path": "/entities/technicians/fields/hourly_rate/validation", "value": "value > 0"}]}
```

```json
{"valid": false, "changedPaths": ["/entities/technicians/fields/hourly_rate/validation"],
 "violations": [{"pointer": "/entities/technicians/fields/hourly_rate/validation",
                 "message": "is not evaluated yet", "severity": "error"}]}
```

In the dashboard: read with `get_descriptor`, then `check_change` or `propose_change` the operations.
In this repo: edit the descriptor file and run the validator (`dotnet test`), or the Management API.
