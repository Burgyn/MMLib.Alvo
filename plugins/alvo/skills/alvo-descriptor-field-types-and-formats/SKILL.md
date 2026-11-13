---
name: alvo-descriptor-field-types-and-formats
description: Use when an Alvo descriptor field needs a type or its facets — string or text, decimal precision and scale, enum values, a ref and its onDelete, a format such as email, or a sequence number.
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

## Sequence numbers

A field's `sequence` makes Alvo number each create — gapless, never reused, counted per tenant:

- `"type": "integer"` stores the number itself. `"type": "string"` needs `format`, the text stored:
  `"SO-{yyyy}-{n:4}"` gives SO-2026-0042. Exactly one `{n}`, or `{n:4}` to pad to four digits (1 to 18), plus any
  of `{yyyy}` `{yy}` `{MM}` `{dd}` from the create's date.
- `per` names a required, stored string, integer, enum, ref, uuid, date or boolean field a caller writes: each value
  counts on its own — never a computed, rollup, sequence or virtual field. `period` (`year`, `month` or `day`) restarts the count each calendar period; dates read in `timeZone` (IANA, e.g. `"Europe/Bratislava"`; default UTC; changing it later never restarts a count). `start` is the first
  number, 1 by default.
- Alvo writes the value: a write that sends it is refused 403. `default`, `computed`, `rollup`, `maxLength`, `virtual`
  and a field-level `format` (such as `email`) are each refused beside it. A rolled-back create gives its number back. Not accepted on a `storage: dynamic` entity.

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

<!-- example: add-rental-check-in-technician -->
**Which technician checked a returned rental in, kept when that technician is deleted.**

```json
{"tool": "propose_change", "baseRevision": 1, "summary": "Adds check_in_technician_id, the technician who checked the rental in.",
 "operations": [{"op": "add", "path": "/entities/rentals/fields/check_in_technician_id",
                 "value": {"type": "ref", "entity": "technicians", "onDelete": "setNull"}}]}
```

```json
{"valid": true, "changedPaths": ["/entities/rentals/fields/check_in_technician_id"]}
```

In the dashboard: read with `get_descriptor`, then `check_change` or `propose_change` the operations.
In this repo: dry-run the edited `*.alvo.json`: `PUT /management/projects/<project>/descriptor?dryRun=true` (Alvo repo: `scripts/test-ring0`).
