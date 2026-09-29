---
name: alvo-descriptor-field-types-and-formats
description: Use when an Alvo descriptor field needs a type or its facets — string or text, decimal precision and scale, enum values, a ref and its onDelete, or a format such as email.
---

# Field types and formats in an Alvo descriptor

A field's `type` is one of:

<!-- gen:field-types -->
`string` `text` `integer` `decimal` `boolean` `date` `datetime` `uuid` `json` `enum` `ref`
<!-- /gen:field-types -->

Each facet belongs to one type, and a facet on another type is refused:

- `string` is bounded text: `maxLength` counts Unicode code points. `format` validates it (below).
- `text` is unbounded prose — notes, descriptions — and takes neither `maxLength` nor `format`.
- `decimal` needs both `precision` and `scale`. `precision` counts **all** digits and `scale` those after the point,
  so `"precision": 10, "scale": 2` holds up to 99999999.99. `precision` is at most 38.
- `enum` needs `values`, a non-empty list of distinct strings.
- `ref` needs `entity`, the entity it points at. On a ref to a declared entity, `onDelete` says what a delete of
  the target does to this row:

<!-- gen:on-delete -->
`restrict` `cascade` `setNull`
<!-- /gen:on-delete -->

  `restrict` is the default and refuses the delete while a row still points at the target. Such a ref is a foreign
  key and is indexed without being asked.

  A ref may also point at the built-in `users`. It then holds the user's id and nothing more: no foreign key, no
  `onDelete` behaviour and no automatic index. Leave `onDelete` off it, and add `"index": true` if callers filter by
  it.

## Formats

A `string` field's `format` is a built-in:

<!-- gen:built-in-formats -->
`email` `uri` `phone`
<!-- /gen:built-in-formats -->

or the name of a format declared under `/formats/<name>` as `{"pattern": "…", "description": "…"}`. A name that
is neither is refused at apply. Reuse a declared format before adding one: the descriptor lists them.

<!-- example: add-rental-deposit-note -->
**An optional note on how the deposit of a rental was settled.**

```json
{"tool": "propose_change", "baseRevision": 1, "summary": "Adds an optional deposit note to rentals.",
 "operations": [{"op": "add", "path": "/entities/rentals/fields/deposit_note",
                 "value": {"type": "text", "description": "How the deposit was settled or why it was kept."}}]}
```

```json
{"valid": true, "changedPaths": ["/entities/rentals/fields/deposit_note"]}
```

<!-- example: add-rental-checked-in-by -->
**Which technician checked a returned rental in, kept when that technician is deleted.**

```json
{"tool": "propose_change", "baseRevision": 1, "summary": "Adds checked_in_by, the technician who checked the rental in.",
 "operations": [{"op": "add", "path": "/entities/rentals/fields/checked_in_by",
                 "value": {"type": "ref", "entity": "technicians", "onDelete": "setNull"}}]}
```

```json
{"valid": true, "changedPaths": ["/entities/rentals/fields/checked_in_by"]}
```

In the dashboard: read with `get_descriptor`, then `check_change` or `propose_change` the operations.
In this repo: edit the descriptor file and run the validator (`dotnet test`), or the Management API.
