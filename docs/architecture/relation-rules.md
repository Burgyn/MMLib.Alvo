# Relation rules and lookup checks

A rule may read a related row, and an entity may refuse a write whose related row fails a condition. Both
compile to one correlated `EXISTS` that carries the related entity's own `list` rule and tenant scope, so a
relation never matches a row the caller could not list. This is PostgreSQL RLS's behaviour for a policy whose
`USING` holds a sub-select: the sub-select is itself filtered by the referenced table's policies.

Prior art: CEL's `e.exists(x, p)` macro and field selection; PocketBase's `relation.field` and
`collection_via_field`; Firebase's `get()`/`exists()`; PostgreSQL `RI_FKey_check_upd` (a foreign key is
re-checked on update only when its key changed) and SQL `CHECK` (`UNKNOWN` passes).

## The term and its SQL

- **Forward:** `<ref>.<field>` — `customer_id.owner_id == @user.id`. The related row is the one the ref names.
- **Reverse:** `<child>.exists(x, p)` — `project_members.exists(m, m.user_id == @user.id)`. The related rows are
  the children that reference this row; `<child>_via_<field>` names the ref when the child references this
  entity more than once (the rollup `via` resolution, reused).

Each term compiles to one internal node, `CelRelation`, which the published AST does not know: a third-party
renderer meets an unknown node and refuses it. A rule's forward comparisons are *lifted*: every leaf that reads
a path becomes its own relation, its outer fields become `CelOuterFieldRef`, and only `&&`/`||` are descended.

Rendered:

```sql
EXISTS (SELECT 1 FROM <related> AS _alvo_rN
        WHERE _alvo_rN.<related key> = <outer key>
          AND (<body>)
          AND (<related list USING>)
          AND (<related tenant scope>))
```

- **Aliases** are `_alvo_r1`, `_alvo_r2`, … per top-level render. The leading underscore is one no entity name
  can start with (`^[a-z]…`), so an alias never names the outer table it is correlated with. An entity called
  `alvo_r1` once captured its own qualifier; `RelationEngineFacts` pins the fix on both engines.
- **The outer row is never read bare** inside an `EXISTS`, since the related table may declare a same-named
  column. In a read it is qualified by its table (`"orders"."customer_id"`); `has(outer)` reads the outer column.
- **Parameters.** Every nested scope renders into its parent's bag, so one top-level predicate has one prefix and
  its names never repeat: `alvo_u` for `USING`, `alvo_c` for `WITH CHECK`, `alvo_q` for a write probe. In a probe,
  post-image values are bound into the probe's own bag by the outer field's declared type; a null or
  unconvertible one renders its comparison `FALSE`, and a null outer key renders the whole relation `FALSE`
  without reading a table.
- **A denied related `list`** (no rule, a tenantless caller on a scoped entity, a required context the caller
  lacks, or the depth cap) renders the constant `FALSE`.

## Binding

`PolicyEngine.Resolve` *binds* every relation to the related entity's `list` decision for the same caller,
before any renderer sees it, so a decision's predicates are self-contained and the renderer stays a pure
function of tree and caller.

- **One catalog snapshot per call.** The public `Resolve` reads `IPolicyCatalogProvider.Current` once and passes
  it down the whole recursion, so a re-apply landing mid-call cannot mix two revisions' rules in one decision.
- **The cache key** is (catalog, entity, operation, slot, depth, has-identity, has-tenant). Those are the only
  inputs a related decision's allow or deny depends on. Role literals stay in the bound tree and fold per caller
  at render. *Has-identity* is `RequiredContext.HasIdentity`, the required-context gate's own test, so the
  all-zero user id is anonymous and an anonymous caller is never served a scope bound for an authenticated one.
- A lookup check is bound the same way, under slot `checks/<i>`, on `create` and `update` decisions only.

## Writes

`WITH CHECK` and lookup checks are judged over the post-image, which is not stored yet. The EF driver judges each
relation term with one statement, `SELECT CASE WHEN EXISTS (…) THEN 1 ELSE 0 END`, substitutes the verdict into
the tree and lets the interpreter judge the rest.

- **The probe runs inside the write's transaction** — its own, or a unit's under its savepoint. A create defers a
  relation-reading `WITH CHECK`, and its lookup checks, until `BeginAsync`. The validate-hook stage before it may
  still answer early, autocommit, but the authoritative verdict is the in-transaction one.
- **Isolation** is the RLS sub-select's: under PostgreSQL's READ COMMITTED the probe sees the statement's
  snapshot; SQLite serialises writers, so nothing commits between probe and write.
- **No lock is taken** (no `FOR KEY SHARE`). A concurrent transaction may change the related row between the probe
  and the commit — the gap an RLS sub-select has too.
- One round trip per relation term per written row.

## Apply-time refusals

At the rule's or check's own pointer: `/entities/<e>/rules/<op>` and `/entities/<e>/checks/<i>/expr`; a problem
with a check's lookup itself (not a ref, an unreadable target) at `/entities/<e>/checks/<i>/lookup`, and a check on a
`storage: dynamic` entity at `/entities/<e>/checks/<i>`.

- **Cycles.** A cycle among `list` rules — a self-loop included — has no caller whose rows can be decided, and is
  refused once, at its first entity. Other operations and checks only *consume* `list` rules, so a `get` reading
  its own entity is no cycle.
- **Depth.** One hop per term; deeper reach composes through the related entity's own `list` rule. A chain deeper
  than 3 is refused at every rule that starts it, and a non-`list` rule or a check whose target's `list` depth plus
  one exceeds 3 is refused at its own path. The binder's runtime cap is therefore never met on a catalog that
  applied.
- **Negation.** A rule only narrows as related rows vanish, so a relation under `!` — including `== false` and any
  node other than `&&`/`||` — is refused. `!=` inside a term is fine.
- **Slots.** Relations are legal in `rules.*` and `checks[].expr` only; every other slot keeps today's refusal,
  because the parser builds relation nodes only for a compile given a catalog.
- **Targets** are declared physical entities: the built-in `users` and `storage: dynamic` (#41) are refused.
  Tenancy may cross; each caller's own related scope applies.
- **A related entity with no `list` rule** draws a warning: the relation never matches.

## Lookup checks

`entities.<e>.checks: [{ lookup, expr, message }]`.

- **One relation, forward over `lookup`.** The whole `expr` is the body of that relation, compiled unlifted, so a
  negation inside it stays inside the `EXISTS` and fails closed when the looked-up row is invisible. A check that
  reads no `<lookup>.<field>`, reads another ref, reaches a child, or reads `@user`/`@tenant` is refused; `now()`
  is allowed.
- **When.** After `WITH CHECK`, so a caller the rules refuse learns nothing from a check. On create and batch
  create; on update or replace only when a field the check reads changed (`RI_FKey_check_upd`). A null lookup
  skips its check (SQL `CHECK`). The first failing check in declaration order refuses.
- **The refusal** is `AlvoConstraintViolationException` with `Kind = Check`, `Fields = [lookup]` (empty when the
  field is hidden) and the author's message, which the Data API answers 422 `validation`, code `check`, with that
  message and a pointer at the lookup. A batch reports the row at its index. No `AlvoConstraintKind` value was
  added, so a host tells a lookup check from a column `CHECK` by its fields and message only.
- **TOCTOU.** The probe takes no `FOR KEY SHARE`, so a concurrent change to the looked-up row after the probe is
  not seen, and a row changed later is not re-validated — as with an unlocked foreign-key read.
- **A keyed create's replay** answers the recorded row without re-running its checks: the replay writes nothing,
  and a related row changed since is, by the line above, not re-validated. The deferred verdict runs on the branch
  that inserts only.
- **A hidden related field answers one bit per request.** A check may read a related field the caller cannot read
  (`quantity * product_id.cost <= 1000`), and its 422 then tells the caller whether that value passes. A rule over a
  related field has the same property. The check's author chose the condition and owns what it discloses.

## Not done

- **Host narrowings inside `EXISTS`.** A narrowing is a view, not a boundary; the related scope is the rules'.
- **`has()` over a path** (`has(customer_id.note)`).
- **`softDelete` on a related entity.** Refused today by `UnhonouredFeatures`; when it lands, the related scope
  must carry `deleted_at IS NULL`.
- **Dynamic entities** (#41), as targets or as an entity declaring checks.
- **`InMemoryAlvoData`.** It throws `NotSupportedException` for a decision that reads a relation or carries a
  check, including a `create`/`update` whose echo `get` reads one, before it writes.
