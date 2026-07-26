# CEL — profiles, two-valued rendering, and the storage-driver seam

> How Alvo's one CEL compiler (`ICelCompiler`, `src/MMLib.Alvo/Expressions`) turns authored
> condition strings into an enforceable predicate: the three profiles and what each allows, the
> `USING`/`WITH CHECK` mapping `PolicyCatalog` compiles rules into, the two-valued rendering rule
> both backends must agree on, the `IFieldSqlRenderer` seam a new storage driver implements, and
> every deliberate narrowing of conformant CEL Alvo's grammar makes. Spec §0 principle 6 (CEL for
> conditions, JSONata for transforms — CEL is safe-by-construction and runs in-transaction).

## The three profiles

One CEL grammar, one lexer/parser, one type checker (`CelTypeChecker`) — but a construct's
legality is deny-by-default and varies by which descriptor slot the source came from
(`CelProfile`). The checker's `_allowedProfiles` table is the single positive list; a construct
kind missing from it compiles in **no** profile rather than every profile.

| Construct | Rule | Computed | Condition |
|---|---|---|---|
| Literal | ✓ | ✓ | ✓ |
| Field ref, current row (`owner_id`) | ✓ | ✓ | ✓ |
| Field ref, `old.`/`new.` | ✗ | ✗ | ✓ |
| `@user`/`@tenant` context ref | ✓ | ✗ | ✓ |
| `&&` / `\|\|` / `!` | ✓ | ✓ | ✓ |
| Comparison (`==`, `!=`, `<`, `<=`, `>`, `>=`) | ✓ | ✓ | ✓ |
| `in` (role membership) | ✓ | ✗ | ✓ |
| `has(field)` | ✓ | ✓ | ✓ |
| Arithmetic (`+ - * /`, unary `-`) | ✗ | ✓ | ✗ |
| Ternary conditional | ✗ | ✓ | ✗ |
| `changed(field)` | ✗ | ✗ | ✓ |

- **Rule** — `entities.*.rules.*` (the `USING`/`WITH CHECK` predicates) and `hidden`/`readOnly`
  field flags. Must evaluate to `Bool`. Sees the current row and `@user`/`@tenant`; never `old.`/
  `new.` (there is no "before" row for an authorization check) and never arithmetic or a ternary
  (a row-scoping predicate is a filter, not a calculation).
- **Computed** — a `computed` field's expression (source `#21`, not yet compiled to SQL as of this
  PR). Must evaluate to a non-boolean scalar (`Int`/`Decimal`/`String`/`Timestamp`/`Uuid`) — a bare
  boolean is rejected with a "wrap it in a ternary" fix suggestion, since a database column can't
  hold a boolean-as-value distinction the way a predicate can. Never sees `@user`/`@tenant`
  (`ComputedNoContextMessage`: "a computed column is evaluated by the database with no caller
  context") and never role membership, since both are caller-dependent and a computed column has
  no caller. The only profile that allows arithmetic and the ternary.
- **Condition** — a hook's `condition` (`hooks.beforeUpdate[].condition`, etc.). Must evaluate to
  `Bool`. The only profile that sees `old.`/`new.` field references and `changed(field)`, since a
  hook is the one place a "before" row exists to compare against.

## `USING`/`WITH CHECK` per operation

`PolicyCatalogBuilder.CompileRules` maps the descriptor's five nullable rule strings
(`list`/`get`/`delete`/`create`/`update`) onto Postgres's own `CREATE POLICY` shape — a rule not
configured for an operation compiles to `null` for both slots, and `IPolicyEngine` denies that
operation outright rather than treating a missing rule as "no restriction":

| Operation | `Using` (row filter) | `WithCheck` (candidate-row guard) |
|---|---|---|
| `list` | ✓ | — |
| `get` | ✓ | — |
| `delete` | ✓ | — |
| `create` | — | ✓ |
| `update` | ✓ | ✓ (same compiled expression as `Using`) |

`update` compiles its rule string **once** and reuses the identical `CompiledExpression` instance
for both slots — never two independently compiled copies of the same source — so `Using` and
`WithCheck` can never drift apart for the same descriptor entry. A tenant-scoped entity additionally
gets a synthesized `tenant_id == @tenant.id` scope, compiled through the same `ICelCompiler` as any
authored rule (never hand-built), so it is type-checked and fails loudly, naming the entity, if the
schema has no `tenant_id` column.

### The required-context gate: no expression runs against a context value the caller lacks

`PolicyCatalogBuilder` also precomputes, at **apply** time (walking the compiled tree, never
re-parsing the source), whether an expression reads `@tenant.id` or `@user.id`. The measurement is one
type — `RequiredContext` — and it is recorded for **every** compiled expression the engine hands out
or evaluates, in two groups:

- **per operation**, over its three predicate slots together — `Using`, `WithCheck`, and the entity's
  `TenantScope`;
- **per `hidden` / `readOnly` mask**, over that one field's expression.

`IPolicyEngine` then refuses to resolve any of them against a caller who has no tenant, or who carries
the reserved all-zero `UserId` (`AlvoContext.Anonymous`, i.e. no identity). **The two channels fail in
opposite directions, and both directions are "closed":**

| Channel | Caller lacks what the expression reads → |
|---|---|
| An operation's predicate (`Using` / `WithCheck` / `TenantScope`) | **deny the call**, before any predicate is assembled into a `PolicyDecision` |
| A `hidden` / `readOnly` mask | **keep the field masked** — hidden stays hidden, read-only stays read-only — without evaluating the expression |

Neither direction may be inferred from the other, and the mask half is not optional. `CelInterpreter`
fails closed on an *exception*, but an absent `@tenant.id` is not an exception: it resolves to `null`,
collapses the comparison to `false`, and a **positive-form** mask (`hidden: "@tenant.id == @user.id"`)
would therefore report the field **visible** — the same two-valued collapse as below, one channel over,
on the one invariant that has to fail the other way.

This gate is *different* from the tenant guard, and both are needed:

| Gate | Question | Fires on |
|---|---|---|
| Tenant guard | is this entity tenant-scoped while the caller has no tenant? | `Scoped` entities only, before any operation lookup |
| Required-context gate | does an expression this call would resolve read a context value the caller cannot supply? | any entity, incl. `Global`; predicates after the operation lookup, masks while assembling the allow decision |

The guard runs first, so a tenant-scoped entity's tenantless caller still gets the guard's own reason.
The gate is what closes the **global**-entity hole: a global entity gets no tenant guard, so
`!(region_id == @tenant.id)` for a tenantless caller used to render as `(NOT FALSE)` with an empty
parameter bag — every row. Same shape for `@user.id`, where the all-zero uuid would otherwise make the
anonymous caller the owner of every all-zero-owner row.

An unrecognized `CelNode` kind counts as *referencing* the value (deny-by-default), so a future
construct added without updating the walk errs towards refusing rather than towards resolving an
expression against an absent operand.

### Role literals are validated at apply, not at request time

`PolicyCatalogBuilder` also walks every compiled Rule-profile tree (rules *and* `hidden`/`readOnly`
flags) for string literals tested against `@user.roles`, and rejects any that is neither a built-in role
nor declared in the descriptor's `auth.roles` — with the same "did you mean" fix suggestion an unknown
field or enum value gets. A typo'd literal (`'amdin' in @user.roles`) compiles and type-checks perfectly
and then simply never matches, so a rule written to admit admins admits nobody — and, negated, everybody.

This is deliberately a **post-compile walk in the catalog builder, not a check inside
`ICelCompiler.Compile`**: the compiler judges one expression against one entity schema and holds no role
catalog, declared roles are a project-level concern, and the compiler is reachable from callers with no
descriptor at all.

## Two-valued rendering: the rule both backends must agree on

Alvo has two `CompiledExpression` backends — `CelInterpreter` (in-memory, used for `WITH CHECK`
when there is a candidate row but no stored row to filter: a `create`, or a hook `Condition`) and
`SqlPredicateRenderer` (SQL, used for `USING`) — and a differential property test proves they never
disagree on any well-typed expression and record. Both follow the **same** null rule, which is
**two-valued, not SQL's native three-valued (`UNKNOWN`) logic**:

> A comparison where either operand is `null` evaluates to `false` — never "unknown", never an
> exception. `!` applies to the *already-collapsed* boolean.

Worked example: `!(owner_id == @user.id)` over a row whose `owner_id` is `null`.

1. `owner_id == @user.id` — one operand is `null` → the comparison collapses to `false`.
2. `!(false)` → `true`.

So a row with no owner matches the negated rule — this is deliberate (a `null` owner is not
"nobody's row, hide it from everyone", it is a row the negated condition is stated to include), but
it means an author negating an ownership check must reason about the null case explicitly, not
assume "the opposite of who I excluded before."

**An absent `@tenant.id`/`@user.id` is not covered by this rule — it is refused upstream.** Both
backends *would* collapse a comparison against an absent context value to `false`, and that collapse
inverts under negation (`!(region_id == @tenant.id)` becomes `true` for every row), so it was never a
safe guarantee. The required-context gate above denies such a call before either backend sees the
predicate, which makes the collapse **unreachable defence-in-depth** for anything driven by
`IPolicyEngine`: it stays only because `IPredicateRenderer`/`IPredicateEvaluator` are public seams a
provider may drive directly, where rendering `FALSE` is still the right answer. Never read
"it renders `FALSE`" as the tenant- or owner-isolation guarantee itself.

`SqlPredicateRenderer` reproduces this by folding every place `UNKNOWN` could otherwise leak into
Postgres's own three-valued semantics — a raw comparison, a nullable boolean field read as a
predicate, and (defensively, for a future node kind that forgets to self-collapse) the whole rendered
predicate at its root — through the dialect's own fold (`COALESCE(<value>, FALSE)` on
PostgreSQL/SQLite; see the `IFieldSqlRenderer` seam below). `AND`/`OR`/`NOT` over already-two-valued
operands need no extra fold — `(a AND b)`/`(a OR b)`/`(NOT a)` over two folded operands is already
two-valued by construction. This is why the renderer tracks, per rendered subtree, whether
it is already two-valued rather than wrapping indiscriminately: over-wrapping would still be
*correct* but would bury the actual predicate in redundant `COALESCE`s a query planner has to see
through.

**Residual `==`/`!=` collation caveat.** `CelInterpreter` compares strings with ordinal
(`StringComparer.Ordinal`) semantics — case-sensitive, byte-for-byte. A SQL backend's `==`/`!=`
instead uses the compared column's actual collation. F3 does not support a non-default column
collation, so the two backends agree in every configuration F3 ships — but this is a real
divergence risk the differential test cannot see, since it only proves agreement under the
ordinal/default-collation assumption both backends are built on. **A future collation-aware storage
driver must revisit `CelInterpreter`'s and `SqlPredicateRenderer`'s remarks before shipping.**

## The `IFieldSqlRenderer` seam

`SqlPredicateRenderer` composes only SQL **structure** — `AND`/`OR`/`NOT`, parentheses, `CASE WHEN`.
Every identifier, every dialect-specific keyword or literal, and the two-valued fold itself cross
through `IFieldSqlRenderer` instead:

- `RenderField(entity, fieldName)` — a quoted column on a physical entity.
- `RenderParameter(parameterName)` — a bind-parameter reference (dialect-specific prefix).
- `TrueLiteral` / `FalseLiteral` — the dialect's boolean literals in **value** position (`TRUE`/`FALSE`
  on PostgreSQL, `1`/`0` on SQLite).
- `RenderCaseInsensitiveLike(left, right)` — `ILIKE` on PostgreSQL, an upper-cased `LIKE` on SQLite.
- `RenderTwoValued(predicate)` — fold a possibly-`UNKNOWN` **predicate** back into a two-valued one.
- `RenderBooleanFieldAsPredicate(booleanValue)` — read a nullable boolean **value** (a column, or F7's
  JSON path to one) as a two-valued predicate.
- `RenderBooleanPredicate(bool)` — a boolean **constant** in predicate position.

**Why the last three exist, and why they are default interface members: T-SQL has no boolean type.**
PostgreSQL and SQLite fold with `COALESCE(<x>, FALSE)` in boolean position, which is exactly what the
three defaults emit — so an existing implementation keeps compiling and keeps its current rendering, and
`SqlPredicateRenderer` itself spells no `COALESCE` at all. SQL Server / Azure SQL, which §0 principle 3
requires the engine-agnostic core to support, cannot use that shape: a `bit` is a value and never a
predicate, so `COALESCE(<predicate>, 0)` is unparseable where a `WHERE` clause expects a predicate, and
`WHERE 1` is not valid either. A T-SQL driver overrides the three with:

| Member | T-SQL rendering |
|---|---|
| `RenderTwoValued(p)` | `(CASE WHEN <p> THEN 1 ELSE 0 END = 1)` |
| `RenderBooleanFieldAsPredicate(v)` | `(COALESCE(<v>, 0) = 1)` — `COALESCE` in *value* position is fine |
| `RenderBooleanPredicate(true/false)` | `(1 = 1)` / `(1 = 0)` |

The predicate and the value fold are two members, not one, precisely because T-SQL treats them
differently: wrapping a bare `bit` column in `CASE WHEN` would not parse, and comparing a predicate
with `= 1` would not either. `TSqlSeamTests` renders the whole rule matrix through a T-SQL fake that
implements *only* `IFieldSqlRenderer`, which is what proves the seam is sufficient.

**Why this seam exists, not just "because it's an interface": F7's dynamic entities.** A physical
entity's field is a real column; a dynamic (metadata-driven, `evidencie`) entity's field is a JSON
path into one shared, partitioned store (`data->>'owner_id'`), not a column at all. Splitting field
rendering out of the structural renderer is what lets F7 add a JSON-path-rendering
`IFieldSqlRenderer` **without touching `SqlPredicateRenderer` itself** — the renderer that composes
`AND`/`OR`/`NOT` and asks the dialect for the fold never needs to know or care whether a field is a
column or a JSON path.
The same split is what lets a second SQL dialect (SQLite today, PostgreSQL from PR2) share one
structural renderer and differ only in their `IFieldSqlRenderer`.

**A new storage driver must implement exactly `IFieldSqlRenderer`, never `SqlPredicateRenderer`
itself, and never grow a second place that composes SQL text** — see "Deliberate deviations" for
the one caveat every implementation must honor: `fieldName` crosses this boundary **unparameterized**
(there is no bind-parameter form of a column name), so an implementation must quote or escape it as
untrusted input, never emit it verbatim — this matters especially for F7's dynamic driver, which
interpolates it into a SQL string literal rather than a quoted identifier, where quoting rules
differ.

## Deliberate deviations from CEL

Alvo deliberately adopts the CEL spec so agents recognize the grammar from training data — every
deviation below is a stated narrowing (or, for the first three, an addition), not an invented
variant of a standard:

**Additions (constructs conformant CEL does not have):**

1. **The `@user`/`@tenant` context-reference syntax entirely** — CEL has no `@`-prefixed syntax at
   all. Alvo's closed set is exactly `@user.id`, `@user.roles`, `@tenant.id`; every other member on
   an otherwise-recognized context name throws a syntax error with a specific fix suggestion
   (`@user.role` → test membership via `in @user.roles`; `@user.claims`/`@user.teams` → tracked by
   `#37`), and an unrecognized `@name` throws at the lexer.
2. **`changed(field)`** — not a CEL macro; an Alvo addition for the Condition profile only, parsed
   with the same one-bare-identifier-argument shape as `has(...)`.
3. **`old.field`/`new.field` state-qualified row references** — Alvo's own way of expressing a
   hook's before/after row; not CEL syntax.

**Narrowings (constructs conformant CEL has that Alvo's grammar refuses):**

4. **The closed `@`-context set** (see 1 above) — real CEL has no context-reference concept to
   narrow, but within Alvo's own addition, only three members exist; every other member is refused
   rather than silently accepted.
5. **A reduced string-escape set** — `\n`, `\t`, `\r`, `\\`, `\'`, `\"` only; no octal/hex/Unicode
   escapes (`\uXXXX`) that conformant CEL supports, no triple-quoted strings, and no byte-string
   literals (`b"..."`).
6. **No list or map literals** (`[1, 2]`, `{...}`) — `[`/`]` tokenize cleanly (so `@user.claims[...]`
   doesn't abort lexing before the parser can special-case it) but the parser rejects any actual use
   of them as a value, with a "use an equality chain instead" fix suggestion when `[` appears where a
   value is expected.
7. **No comprehension macros** (`all`, `exists`, `exists_one`, `map`, `filter`) — any identifier
   immediately followed by `(` other than `has`/`changed` is refused, with a suggestion to move the
   logic into a hook instead.
8. **No nested field access beyond exactly one level of `old.`/`new.`** — real CEL supports
   arbitrary `a.b.c`-style navigation; Alvo's row model is flat, so a bare identifier is always
   zero-dot and `old`/`new` are the only one-dot prefixes.
9. **`has(...)` narrowed to exactly one argument**, a bare field name or an `old.`/`new.`-qualified
   one — real CEL's `has()` tests presence over arbitrary qualified paths into nested messages.
10. **`==`/`!=` against a `null` literal rejected in favor of `has()`** — `owner_id == null` would
    otherwise always evaluate to `false` under the two-valued null rule above regardless of whether
    `owner_id` is actually `null`, silently making `!(owner_id == null)` always `true`. The compiler
    rejects it outright and redirects the author to `has(field)`/`!has(field)`, which is exactly what
    the author actually means.
11. **String relational operators (`<`, `<=`, `>`, `>=`) rejected outside the Computed profile** —
    collation-dependent comparison is only meaningful where the database itself evaluates the
    expression (a computed column); in the Rule/Condition profiles it is refused with a suggestion to
    use `==`/`!=` instead, or move the comparison into a computed field.
12. **No modulo (`%`)** — the lexer has no case for it; arithmetic is limited to `+ - * /`.
13. **Numeric literals are plain decimal digit runs only** — no hex integers (`0x1A`), scientific
    notation (`1e10`), or an unsigned-literal suffix (`123u`) that CEL supports.
14. **Relational operators are non-associative** (`a == b == c` throws) — **not** a narrowing;
    conformant CEL itself forbids chained relational operators, listed here only so this doc doesn't
    have to be re-derived from the spec by a future reader.

**Residual caveat, not a narrowing:** the string-collation caveat on `==`/`!=` documented above — it
is a real divergence *risk* between the two backends under a non-default collation, not a construct
Alvo's grammar refuses.
