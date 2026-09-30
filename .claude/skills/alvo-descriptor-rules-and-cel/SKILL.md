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
- allowed: `'admin' in @user.roles` `user_id == @user.id` `active == true` `has(phone)` `hourly_rate > 40.0`
- refused: `hourly_rate * 2.0` `changed(active)` `old.active == true` `now()` `'user_id == @user.id'` `'true'`
<!-- /gen:cel-rule -->

A rule compares and combines with `&&`, `||` and `!`. It has no arithmetic, no `old.`/`new.` and no `changed()`: those
belong to hooks and computed fields, each in its own skill.

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
