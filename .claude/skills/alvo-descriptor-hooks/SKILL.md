---
name: alvo-descriptor-hooks
description: Use when an Alvo descriptor change must refuse a write or fill in a value as a row is written — before-hooks with a condition and a reject or mutate action.
---

# Before-hooks in an Alvo descriptor

An entity's `hooks` hold lists under `beforeCreate`, `beforeUpdate` and `beforeDelete`. Each entry is an optional
`condition` and one `action`:

- `{"reject": "…"}` cancels the write; the text is the error the caller reads, so write it for them.
- `{"mutate": {"<field>": …}}` sets fields before the write: each value is a JSON literal or `{"$cel": "…"}`.

A before-hook runs inside the write's transaction, with no network: it can refuse or change this row, and
nothing else. A hook without a `condition` runs on every write of its kind.

The shape: `schema/project.schema.json#/$defs/beforeHookList`.

A `condition` reads the row as it will be with `new.<field>` on create and update, and as it was with `old.<field>`
on update and delete; `changed(<field>)`, true when an update changes the field, is for update only. The other
combinations are refused at apply: a delete has no `new.`, a create no `old.`.

<!-- gen:cel-condition -->
- allowed: `new.quantity <= 0.0` `old.unit_price != new.unit_price` `changed(unit_price)` `'technician' in @user.roles`
- refused: `quantity * unit_price` `now()` `lowerAscii(description)`
<!-- /gen:cel-condition -->

A `mutate` value is a field, a literal, or a call to one of these functions, and nothing more: no `@user` or `@tenant`,
no arithmetic, no joins.

<!-- gen:mutate-functions -->
`lowerAscii` `now`
<!-- /gen:mutate-functions -->

<!-- gen:cel-mutate -->
- allowed: `now()` `lowerAscii(new.description)` `new.unit_price` `'part'`
- refused: `quantity * 2.0` `new.quantity > 0.0` `'admin' in @user.roles` `changed(quantity)`
<!-- /gen:cel-mutate -->

A `mutate` value cannot compare. For a flag decided by a comparison, let the `condition` compare and the `mutate`
write the literal, with a second hook for the opposite case.

An existing list is extended with an `add` at `/entities/<entity>/hooks/<slot>/-`; an entity without `hooks` gets
an `add` at `/entities/<entity>/hooks` holding the whole object.

<!-- example: order-lines-reject-zero-price -->
**A new order line may not have a zero unit price.**

```json
{"tool": "propose_change", "baseRevision": 1, "summary": "Refuses a new order line with a zero unit price.",
 "operations": [{"op": "add", "path": "/entities/order_lines/hooks/beforeCreate/-",
                 "value": {"condition": "new.unit_price == 0.0",
                           "action": {"reject": "An order line needs a unit price. Enter the price charged for it."}}}]}
```

```json
{"valid": true, "changedPaths": ["/entities/order_lines/hooks/beforeCreate/1"]}
```

After-hooks (`afterCreate`, `afterUpdate`, `afterDelete`) run after the commit, and only some of their action types
run in this build: see `alvo-descriptor-capabilities-and-limits`.

In the dashboard: read with `get_descriptor`, then `check_change` or `propose_change` the operations.
In this repo: edit the descriptor file and run the validator (`dotnet test`), or the Management API.
