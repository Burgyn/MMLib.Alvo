---
name: alvo-descriptor-field-types-and-formats
description: Use when an Alvo descriptor field needs a type or its facets — string versus text, decimal precision and scale, enum values, a ref and its onDelete, or a validation format such as email or a named pattern.
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
- `ref` needs `entity`, the entity it points at, and may point at the built-in `users`. Its `onDelete` says what a
  delete of the target does to this row:

<!-- gen:on-delete -->
`restrict` `cascade` `setNull`
<!-- /gen:on-delete -->

  `restrict` is the default and refuses the delete while a row still points at the target. A `ref` field is indexed
  without being asked.

## Formats

A `string` field's `format` is a built-in:

<!-- gen:built-in-formats -->
`email` `uri` `phone`
<!-- /gen:built-in-formats -->

or the name of a format declared under `/formats/<name>` as `{"pattern": "…", "description": "…"}`. A name that
is neither is refused at apply. Reuse a declared format before adding one; list them with `get_descriptor`.

<!-- example: add-rental-deposit-note -->
**An optional note on how a rental's deposit was settled.**

```json
{"tool": "propose_change", "baseRevision": 1, "summary": "Adds an optional deposit note to rentals.",
 "operations": [{"op": "add", "path": "/entities/rentals/fields/deposit_note",
                 "value": {"type": "text", "description": "How the deposit was settled or why it was kept."}}]}
```

```json
{"valid": true, "changedPaths": ["/entities/rentals/fields/deposit_note"]}
```

<!-- example: add-rental-checked-in-by -->
**Who checked a returned rental in: a `ref` to `users` that survives the user's deletion.**

```json
{"tool": "propose_change", "baseRevision": 1, "summary": "Adds checked_in_by, the user who checked the rental in.",
 "operations": [{"op": "add", "path": "/entities/rentals/fields/checked_in_by",
                 "value": {"type": "ref", "entity": "users", "onDelete": "setNull"}}]}
```

```json
{"valid": true, "changedPaths": ["/entities/rentals/fields/checked_in_by"]}
```

In the dashboard: read with `get_descriptor`, then `check_change` or `propose_change` the operations.
In this repo: edit the descriptor file and run the validator (`dotnet test`), or the Management API.
