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
- allowed: `new.quantity <= 0.0` `old.unit_price != new.unit_price` `changed(unit_price)` `'technician' in @user.roles` `size(new.description) > 3`
- refused: `quantity * unit_price` `now()` `lowerAscii(description)`
<!-- /gen:cel-condition -->

A `mutate` value is a field, a literal, or a call to one of these built-in functions (calls may nest), and nothing
more: no `@user` or `@tenant`, no arithmetic, no joins. `lowerAscii` takes a field only; the others take any value
of the right type, and a null argument makes the value null.

<!-- gen:mutate-functions -->
`lowerAscii` `math.abs` `math.round` `now` `replace` `size` `trim`
<!-- /gen:mutate-functions -->

An embedded host may register its own functions; they work in a `condition` and a `mutate` and nowhere else. Call
`get_cel_functions` for this host's list with each function's parameters and result — never assume one exists. A
function whose meaning changes gets a new name (`vatRate` stays, `vatRate2` is new). Alvo's tenant filter does not
reach inside a function: one that reads stored data must take the tenant as a parameter and filter by it.

<!-- gen:cel-mutate -->
- allowed: `now()` `lowerAscii(new.description)` `new.unit_price` `'part'` `trim(new.description)`
- refused: `quantity * 2.0` `new.quantity > 0.0` `'admin' in @user.roles` `changed(quantity)`
<!-- /gen:cel-mutate -->

A `mutate` value cannot compare. For a flag decided by a comparison, let the `condition` compare and the `mutate`
write the literal, with a second hook for the opposite case.

Adding a hook depends on what the entity already has:

- The slot exists: `add` at `/entities/<entity>/hooks/<slot>/-`.
- `hooks` exists without that slot: `add` at `/entities/<entity>/hooks/<slot>` with a list holding the hook. An
  append to a list that does not exist is refused.
- No `hooks` at all: `add` at `/entities/<entity>/hooks` holding the whole object.

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

<!-- example: service-orders-reject-negative-labour -->
**A new service order may not book negative labour hours** — `service_orders` has `hooks`, but no `beforeCreate`.

```json
{"tool": "propose_change", "baseRevision": 1, "summary": "Refuses a new service order with negative labour hours.",
 "operations": [{"op": "add", "path": "/entities/service_orders/hooks/beforeCreate",
                 "value": [{"condition": "new.labour_hours < 0.0",
                            "action": {"reject": "Labour hours cannot be negative. Enter the hours worked, or 0."}}]}]}
```

```json
{"valid": true, "changedPaths": ["/entities/service_orders/hooks/beforeCreate"]}
```

After-hooks (`afterCreate`, `afterUpdate`, `afterDelete`) run after the commit, and only some of their action types
run in this build: see `alvo-descriptor-capabilities-and-limits`.

In the dashboard: read with `get_descriptor`, then `check_change` or `propose_change` the operations.
In this repo: edit `examples/**/*.alvo.json` or your own descriptor, then run `scripts/test-ring0` or `PUT …/descriptor?dryRun=true`.
