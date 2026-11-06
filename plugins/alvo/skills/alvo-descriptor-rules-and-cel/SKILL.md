---
name: alvo-descriptor-rules-and-cel
description: Use when an Alvo descriptor change decides who may list, read, create, update or delete the rows of an entity — CEL rules over the caller and the row, and what a rule may contain.
---

# Rules in an Alvo descriptor

An entity's `rules` hold one CEL condition per operation: `list`, `get`, `create`, `update`, `delete`. A missing
operation is **denied**, never unrestricted. They follow Postgres row-level security:

- `list`, `get` and `delete` filter the rows the caller may reach (`USING`).
- `create` checks the row being written (`WITH CHECK`).
- `update` does both with the same condition: the row before and the row after must pass.

A rule reads the caller as `@user.id` and `@user.roles`, the tenant as `@tenant.id`, and the row's own fields by name.
Test a role with `in`: `'admin' in @user.roles`. A role literal must be a built-in role or one declared in
`auth.roles`: a typo is refused at apply, because it would otherwise match nobody.

A rule's value is the bare CEL expression as one JSON string: `"author_id == @user.id"`. Single quotes go only around
a text value inside it, such as a role name. `"'author_id == @user.id'"` is one string literal, not a rule, and is
refused with *"Remove the outer quotes"*.

What a rule may contain:

<!-- gen:cel-rule -->
- allowed: `'admin' in @user.roles` `user_id == @user.id` `active == true` `has(phone)` `hourly_rate > 40.0` `created_at > now() - duration('24h')`
- refused: `hourly_rate * 2.0` `changed(active)` `old.active == true` `trim(full_name) == 'x'` `'user_id == @user.id'` `'true'`
<!-- /gen:cel-rule -->

A rule compares and combines with `&&`, `||` and `!`. It has no numeric arithmetic (timestamp ± duration is the
exception: `now() - duration('24h')`), no `old.`/`new.` and no `changed()`: those belong to hooks and computed fields,
each in its own skill.

Also allowed: `order_id.assigned_user_id == @user.id` and `service_orders.exists(o, o.assigned_user_id == @user.id)`.
A rule may follow a ref (`customer_id.city == 'Brno'`) or reach the rows that reference this one
(`service_orders.exists(o, o.status == 'ready')`); the related entity's own list rule always applies, never under `!`,
one hop. An entity's `checks`, e.g. `{"lookup": "part_id", "expr": "!part_id.discontinued && quantity <= part_id.stock_quantity", "message": "…"}`,
refuse a write whose looked-up row fails `expr` with 422 and that message; a check reads no `@user` or `@tenant`.

A rule calls no function — not `trim`, not a host function: a rule filters in SQL. Two forms are not calls: `now()`,
the instant of the request, and `duration('…')`, a constant such as `duration('90m')` (units `h m s ms us ns`).
`created_at > now() - duration('24h')` admits the last day's rows. To filter by a normalised value, write it into a
field with a before-hook `mutate` and compare that field.

Keep the grants an operation already has unless the request removes them: add a clause with `||` rather than
replacing the rule. Before a `replace` of a rule, `test` its current value.

<!-- example: technician-updates-own-profile -->
**A technician may update their own record; admins and managers still may.**

```json
{"tool": "propose_change", "baseRevision": 1, "summary": "Lets a technician update their own technician record.",
 "operations": [{"op": "test", "path": "/entities/technicians/rules/update", "value": "'admin' in @user.roles || 'manager' in @user.roles"},
                {"op": "replace", "path": "/entities/technicians/rules/update",
                 "value": "'admin' in @user.roles || 'manager' in @user.roles || user_id == @user.id"}]}
```

```json
{"valid": true, "changedPaths": ["/entities/technicians/rules/update"]}
```

A new entity carries its whole `rules` object in the `add` that creates it. Say every operation the request allows;
a missing one is denied. An owner clause compares a `ref` to `users` with `@user.id`.
`@user.id` is a `users` id: compare it only with a field that refs `users`, or with a `uuid` that holds a user id. A
ref to any other entity holds that entity's id and never equals it, and the validator warns.

<!-- example: new-entity-with-rules -->
**Comments on a service order: everyone signed in reads them, each author edits and deletes their own, admins delete any.**

```json
{"tool": "propose_change", "baseRevision": 1, "summary": "Adds comments on service orders, each editable by its author.",
 "operations": [{"op": "add", "path": "/entities/order_comments",
                 "value": {"audit": true,
                           "fields": {"order_id": {"type": "ref", "entity": "service_orders", "onDelete": "cascade", "required": true},
                                      "author_id": {"type": "ref", "entity": "users", "required": true},
                                      "body": {"type": "text", "required": true}},
                           "rules": {"list": "'authenticated' in @user.roles", "get": "'authenticated' in @user.roles",
                                     "create": "author_id == @user.id", "update": "author_id == @user.id",
                                     "delete": "'admin' in @user.roles || author_id == @user.id"}}}]}
```

```json
{"valid": true, "changedPaths": ["/entities/order_comments"]}
```

A `ref` to `users` takes no `onDelete`; a `ref` to `service_orders` does.

In the dashboard: read with `get_descriptor`, then `check_change` or `propose_change` the operations.
In this repo: edit `examples/**/*.alvo.json` or your own descriptor, then run `scripts/test-ring0` or `PUT …/descriptor?dryRun=true`.
