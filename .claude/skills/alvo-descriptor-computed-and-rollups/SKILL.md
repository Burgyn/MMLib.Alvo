---
name: alvo-descriptor-computed-and-rollups
description: Use when an Alvo descriptor field should be derived rather than written — a computed value over its own row, or a rollup over related rows — and what a computed expression may contain.
---

# Computed fields and rollups in an Alvo descriptor

Put a derived value on the lowest rung that expresses it:

1. **computed** — an expression over the same row, stored by the database, read-only.
2. **rollup** — an aggregate over related rows, kept consistent in the write's transaction. Never a hook that
   recomputes a parent.
3. **before-hook** — a value decided at write time (`alvo-descriptor-hooks`).
4. actions, automation, functions — mostly not run by this build (`alvo-descriptor-capabilities-and-limits`).

## Rollups

`{"from": "<child>", "op": "…", "field": "<child field>"}`: `from` is an entity with a `ref` to this one, `field` is
needed by every `op` but `count`, and `via` names the child's `ref` when it has several. A `where` is refused.

<!-- gen:rollup-ops -->
`sum` `count` `avg` `min` `max`
<!-- /gen:rollup-ops -->

## What Computed allows

A computed field is same-row CEL rendered into a STORED generated column, maintained by the database. Each rule
names the refusal the framework gives, so you can explain it.

- **Arithmetic**: `+ - * /` and unary `-` over **numeric** fields (`unit_price * amount`, `net_total + vat_total`);
  a computed field may read a rollup field of its own row.
- **Text**: `+` over **two strings** joins them (CEL's `string + string`), left to right:
  `first_name + ' ' + last_name`. A `string`, `text` or `enum` field, or a text constant in single quotes, may be
  joined. There is **no implicit conversion**: `first_name + bikes_count` is refused (*"'+' joins two strings or adds
  two numbers"*), and a computed field has no `string()` to convert with.
- **Null rule**: every joined operand must be **never null** — a `required` field, a constant, or a field read
  inside the branch its own `has()` guards: `(has(middle_name) ? middle_name : '') + last_name`, or with the
  separator only when present, `first_name + (has(middle_name) ? ' ' + middle_name : '') + ' ' + last_name` and
  `has(middle_name) ? first_name + ' ' + middle_name : first_name`. An optional field joined directly is refused
  (*"'+' would join 'street', which may be null…"*, fix: *"Make 'street' required, or write the fallback explicitly:
  (has(street) ? street : '')"*). Reason: CEL's `+` has no null overload, and SQL's `||` makes the whole value NULL
  when any part is.
- **Only a bare `has(f)` is a guard**: `has(f) ? … : …` guards `f` in its first branch, `!has(f) ? … : …` in its
  second, and nothing else guards anything — `has(street) && has(city) ? street + ' ' + city : ''` is refused by
  the null rule (a combined guard is not supported yet). Nest one ternary per optional part:
  `has(street) ? (has(city) ? street + ' ' + city : street) : (has(city) ? city : '')`.
- **Type and length**: the type check is between text and the rest. A join produces text, so it needs a field
  declared `"type": "string"` (or `"text"`) — into any other type it is refused (*"which joins text into a string,
  but the field is declared"*); and a `string` or `text` field needs a text result. Anything else is declared as the type
  it computes (`decimal`, `integer`, `date`, …). With `maxLength` on the computed field, it must hold the longest
  join: a `string` part counts its `maxLength`, an `enum` part its longest value, a text constant its length, a
  ternary its longer branch (`first_name` 60 + `' '` 1 + `last_name` 60 = 121; *"which can be up to 121 characters
  long"*). A `text` part, or a `string` part without `maxLength`, has no bound, so the join cannot declare
  `maxLength` at all (*"which declares no maxLength"*) — omit it. Omitting `maxLength` is always fine.
- **Never boolean**: a computed value is never a `boolean` (*"A computed-field expression must evaluate to a
  non-boolean scalar"*). A comparison or `has(f)` is only ever a ternary's condition, never the value. A flag such as
  `is_vip` is a plain `boolean` field kept by before-hooks, as `alvo-descriptor-hooks` shows: the hook's `condition` does the
  comparison and its `mutate` writes the literal `true`, and an opposite hook writes `false` — a `mutate` value
  cannot compare.
- **Ternary** `c ? a : b` whose condition compares two fields of the row or is `has(field)` / `!has(field)`; both
  branches have the same type.
- **Constants**: a **text** constant may appear in a join or a ternary branch. A **numeric** constant
  (`unit_price * 1.2`) is refused (*"a constant other than a text constant joined into the value cannot be carried
  into it"*) — hold a rate in a field of its own that a before-hook maintains. An expression that reads **no field**
  (`'always the same'`) is refused (*"which reads no field of its row"*) — that is a `default`, not computed. A text
  constant cannot hold a line break (the Unicode line and paragraph separators U+2028 and U+2029 included), a tab or
  another control character.
- **Not another computed field**: a computed field reads stored fields only (a rollup is stored); reading another
  computed field is refused (*"itself a computed field"*) with that field's expression as the fix, and a computed
  field never reads itself.
- **A text constant is joined, never compared**: `first_name == 'Jana' ? …` is refused (*"A text constant can be
  joined, not compared, in a computed field"*); compare two fields instead.
- **Never**: `@user`/`@tenant`, `now()` or any function, `old.`/`new.`, `changed()`, role membership.

<!-- gen:cel-computed -->
- allowed: `quantity * unit_price` `-unit_price` `description + ' ' + kind`
- refused: `now()` `'admin' in @user.roles` `changed(quantity)` `quantity > 1.0`
<!-- /gen:cel-computed -->

In the dashboard: read with `get_descriptor`, then `check_change` or `propose_change` the operations.
In this repo: edit the descriptor file and run the validator (`dotnet test`), or the Management API.
