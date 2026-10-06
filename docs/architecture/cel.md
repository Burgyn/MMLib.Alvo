# CEL — profiles, two-valued rendering, and the storage-driver seam

> How Alvo's one CEL compiler (`ICelCompiler`, `src/MMLib.Alvo/Expressions`) turns authored
> condition strings into an enforceable predicate: the five profiles and what each allows, the
> `USING`/`WITH CHECK` mapping `PolicyCatalog` compiles rules into, the two-valued rendering rule
> both backends must agree on, the `IFieldSqlRenderer` seam a new storage driver implements, and
> every deliberate narrowing of conformant CEL Alvo's grammar makes. Spec §0 principle 6 (CEL for
> conditions, JSONata for transforms — CEL is safe-by-construction and runs in-transaction).

## The five profiles

One CEL grammar, one lexer/parser, one type checker (`CelTypeChecker`) — but a construct's
legality is deny-by-default and varies by which descriptor slot the source came from
(`CelProfile`). The checker's `_allowedProfiles` table is the single positive list; a construct
kind missing from it compiles in **no** profile rather than every profile.

**This table is the `_allowedProfiles` table as it is written today, not as any design document
proposes it** — see *`Mutate`, the fourth profile* below for where the two differ and why.

| Construct | Rule | Computed | Condition | Mutate | Access |
|---|---|---|---|---|---|
| Literal | ✓ | ✓ | ✓ | ✓ | ✓ |
| Field ref, current row (`owner_id`) | ✓ | ✓ | ✓ | ✓ | ✗ |
| Field ref, `old.`/`new.` | ✗ | ✗ | ✓ | ✓ | ✗ |
| `@user` context ref | ✓ | ✗ | ✓ | ✗ | ✓ |
| `@tenant` context ref | ✓ | ✗ | ✓ | ✗ | ✗ |
| `&&` / `\|\|` / `!` | ✓ | ✓ | ✓ | ✗ | ✓ |
| Comparison (`==`, `!=`, `<`, `<=`, `>`, `>=`) | ✓ | ✓ | ✓ | ✗ | ✓ |
| `in` (role membership) | ✓ | ✗ | ✓ | ✗ | ✓ |
| `has(field)` | ✓ | ✓ | ✓ | ✗ | ✗ |
| Arithmetic (`+ - * /`, unary `-`) | ✗ | ✓ | ✓ | ✓ | ✗ |
| String concatenation (`+` over two strings) | ✗ | ✓ | ✗ | ✓ | ✗ |
| Ternary conditional | ✗ | ✓ | ✗ | ✗ | ✗ |
| `changed(field)` | ✗ | ✗ | ✓ | ✗ | ✗ |
| Legacy call (`now()`) | ✗ | ✗ | ✗ | ✓ | ✗ |
| Catalogued function call (built-ins and host functions; each function's own profiles narrow this ceiling) | ✗ | ✗ | ✓ | ✓ | ✗ |

**Note the row that split.** `@user` and `@tenant` were one row (`@user`/`@tenant` context ref)
because no profile had ever wanted one without the other. `Access` does, so the row is two rows and
`_allowedProfiles` has two construct kinds (`ContextRefUser`, `ContextRefTenant`). A reader
comparing this file to an older revision should read the split as that change, not as a
transcription error.

**An unrecognised context value is refused in every profile**, which is the same deny-by-default
rule one level down from the table. `CelTypeChecker` maps a `CelContextValue` to a construct kind,
and an unmapped one falls out as an *unrecognised node* rather than onto the `@user` row — so a
context member added to the enum and forgotten here compiles nowhere instead of everywhere `@user`
is legal.

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
  no caller. The only profile that allows the ternary; arithmetic it shares with `Condition` and `Mutate`, and string
  concatenation with `Mutate`, where both fail closed instead of answering null (*Operators in hook slots*).
  **String `+`** is CEL's `(string, string) → string` overload, left-associative, with no implicit
  conversion (a mixed pair is a type error; there is no `string()` here), and an operand that can be
  null is **refused** — CEL's `+` has no null overload and SQL's `||` yields `NULL` — unless it is read
  inside the branch its own presence test guards (`has(f) ? f : ''`, `has(f) ? a + ' ' + f : a`). A text constant
  in a value position is written inline into the generated column's DDL through the dialect's
  `IFieldSqlRenderer.RenderStringLiteral` (so it may not hold a control character, a line or paragraph
  separator — U+2028, U+2029 — or an unpaired surrogate); every other constant is still a bind parameter, which a computed field refuses.
- **Condition** — a hook's `condition` (`hooks.beforeUpdate[].condition`, etc.). Must evaluate to
  `Bool`. The only profile that allows `changed(field)`, and one of the two — with `Mutate` — that
  sees `old.`/`new.` field references, since a hook is the one place a "before" row exists to
  compare against. Admits the catalogued functions and arithmetic, which fails closed here.
- **Mutate** — a before-hook `mutate` value (`hooks.beforeUpdate[].action.mutate.<field>`). Must
  evaluate to a value a field can hold: any scalar **or** a `Bool`, which is the one profile with no
  constraint at all on the result shape — a `boolean` column is a legitimate `mutate` target, and
  that is exactly the case `Computed` has to reject because a generated column cannot hold
  "predicate" as a value. Admits a function call (as does `Condition` for the catalogued functions), arithmetic and
  string `+`, and no comparison or logical operator. See the next section.
- **Access** — a management-access level (`access.admin` / `access.developer` / `access.viewer`).
  Must evaluate to `Bool`. Sees `@user` and **nothing else**: no row, so no field reference of
  either state and no `has()`/`changed()`; and no `@tenant`, because an access level is
  *project*-scoped by the frozen schema's own description and admitting `@tenant` would make a
  project-level predicate answer differently per request. **Interpreter-only**, like `Mutate`, and
  refused at the renderer by *profile* before any tree is walked — which it has to be, because
  every construct an access level can contain is one the renderer would otherwise render perfectly
  well, into a `WHERE` clause over a table an access level never names. Role literals are validated
  at apply against `auth.roles`, by the same walk that validates a rule's.

  **What `@user.id` can and cannot do here, stated so it is not read as an oversight.** The column
  admits it, because the frozen schema names it as one of the two members a level may read — but
  Alvo's grammar has no `uuid` literal — no deviation records that, correctly, since conformant CEL
  has none either, so it is not a narrowing — and an access level sees no row, so there is
  no `Uuid`-typed operand in scope for it to meet. A level written as `@user.id == '…'` is
  therefore refused by the *comparison* rule ("Cannot compare Uuid to String"), not by the profile
  table. Both the member and the comparison operator stay admitted deliberately: widening `@user`
  is additive, so a level written against a future typed claim compiles without this table
  changing, and admitting the operator today expresses nothing a role membership could not.

**A result-type refusal quotes what it refused (D42).** A predicate wrapped whole in a string literal
(`'owner_id == @user.id'` under `Rule`, `Condition` or `Access`) is refused with a fix that names the outer quotes
and gives the unwrapped content, and every result-type refusal echoes its source in backticks (at most 120 characters;
control, line- and paragraph-separator and format characters, bidi overrides among them, as spaces). Only the refusal's text changes: the check runs on a source already refused, its one inner
compile has the check off, and `CelAcceptanceCorpusTests` holds the accepted set to its pre-D42 baseline.

## `Mutate`, the fourth profile

`Mutate` compiles a before-hook's `mutate` value — the one descriptor slot that is a *value*
expression over a *hook* context, which the three-profile design had no cell for (`Rule` and
`Condition` are predicates over a row; `Computed` is a value with no hook context).

### Interpreter-only, and that is a guarantee rather than an accident

A `Mutate` expression is evaluated by `CelInterpreter` and is **never** handed to
`SqlPredicateRenderer`. **This now covers two profiles, not one** — `Access` is interpreter-only on
its own reasons (a predicate over the caller alone, with no row to push into), and everything in
this section holds for it unchanged. Three things follow, and each is an absence a later reader
could otherwise mistake for an omission:

- **No `IFieldSqlRenderer` member.** Nothing in the profile has a SQL rendering, so the seam a
  storage driver implements does not grow for it.
- **No per-engine golden snapshot, and no row in the differential backend test.** Those facts prove
  two backends agree; there is one backend here.
- **The two-valued rendering rule below does not apply.** It is a rule *both backends must agree
  on*, and with one backend there is nothing to agree with — so `Mutate` inherits
  `CelInterpreter`'s semantics unchanged and states no separate null rule.

The refusal is enforced rather than merely documented, and it is enforced **per profile, at both
entry points, before either walks a tree** — not per node. An earlier revision refused a `CelCall`
node by name inside the walk and admitted the profile itself, so every `Mutate` expression whose
tree held no call reached the renderer and came back as SQL: a bare `true` is legal in the profile
and rendered to `TRUE`, which made the guarantee hold for the shapes that arm happened to name
rather than for the profile. `Access` could not have been guarded that way at all — every construct
it admits renders perfectly well. The per-node arms are therefore gone: with the profile guard in
place nothing could reach them, and an unreachable refusal is one no test can hold to its claim. The
moment somebody proposes rendering a `Mutate` expression to SQL, the two-valued fold and the
`==`/`!=` collation caveat both come back into scope.

### The function catalog

A function call is the one construct whose legality is decided twice: by the profile ceiling in the table above, and
by the function's own list of profiles (*the two gates*). Both must admit the profile, so a function can narrow the
ceiling and never widen it. The catalog (`CelFunctionCatalog`, internal to the core) holds the legacy call `now()`, the
eighteen other built-ins and whatever a host registered with `AddCelFunction`; the parser, the type checker and the
interpreter are given the same instance, so apply and `cel/check` cannot disagree about what a name means. Design and
rationale: [the CEL functions design](../superpowers/specs/2026-10-05-f5-cel-functions-design.md) and
[slice D](../superpowers/specs/2026-10-06-f5-hook-functions-end-to-end-design.md).

#### The legacy call

Outside the catalog an identifier followed by `(` is a syntax error (deviation 7). `now()` keeps its own grammar, byte
for byte, and stays `Mutate`-only — positive and closed, on `_allowedProfiles`' own principle that a name missing from
the list compiles in no profile rather than in every one:

| Call | Result type | What it is |
|---|---|---|
| `now()` | `Timestamp` | the instant this write is already stamped with |

**Why `now()` is not a clock read.** It resolves to the `DateTimeOffset` the write's own audit stamp
already used, bound once per write by the caller and threaded in — `CelInterpreter` never touches
`TimeProvider` itself. So `now()` twice in one write returns the same value, a value a hook writes
and the row's own `created_at`/`updated_at` cannot disagree, and the whole thing is testable with a
fake clock. It is also **never rendered to SQL**, and that is §0 principle 3 rather than a
limitation: Postgres's `now()` is *transaction-start* time while SQLite's `CURRENT_TIMESTAMP` is
second-precision *text*, so rendering it would let two engines answer differently for one descriptor.
`@now` was considered and rejected — it would widen the closed `@`-context set and be its first
memberless context reference.

`lowerAscii` was the legacy pair's other half until slice D, field-only and `Mutate`-only; it is now an ordinary
built-in (below), so `lowerAscii(trim(new.email))` parses and a condition may call it.

#### Built-in functions

`Condition` and `Mutate`, interpreter only. Every parameter is non-nullable, so a null argument makes the call
null; nothing is culture-sensitive; a position or a size counts Unicode code points. Overloads of one name share
parameter names by position, so the dashboard's insert template is stable. `now` alone is `Mutate`-only. A failure
named here fails the write closed (*Resolution, null and failure* below) with the reason quoted, which never carries a
value from the row.

| Function | Signature(s) | Semantics |
|---|---|---|
| `contains` | `(text: String, search: String) -> Bool` | ordinal, no case folding (`contains(lowerAscii(x), 'b')` for that); an empty `search` is `true` |
| `endsWith` | `(text: String, suffix: String) -> Bool` | ordinal; an empty suffix is `true` |
| `int` | `(value: Decimal) -> Int`, `(value: String) -> Int` | a Decimal is cut toward zero (`int(-2.9)` = `-2`); a text must be `[+-]?[0-9]+`, base 10, no spaces (`int(trim(x))`); fails closed outside the 64-bit range ("the value is outside the range of an Int") or on any other text ("the text is not a whole number such as 42 or -7") |
| `lowerAscii` | `(text: String) -> String` | `A`–`Z` → `a`–`z`, and **nothing else** (`'ÄBC'` → `'Äbc'`) |
| `math.abs` | `(x: Int) -> Int`, `(x: Decimal) -> Decimal` | magnitude, same type; the smallest `Int` fails closed |
| `math.ceil` | `(x: Int) -> Int`, `(x: Decimal) -> Decimal` | the smallest whole number ≥ `x`, scale 0 (`-1.5` → `-1`); the `Int` overload is the identity |
| `math.floor` | `(x: Int) -> Int`, `(x: Decimal) -> Decimal` | the largest whole number ≤ `x`, scale 0 (`-1.2` → `-2`); the `Int` overload is the identity |
| `math.greatest` | `(a: Int, b: Int) -> Int`, `(a: Decimal, b: Decimal) -> Decimal` | the larger, `a` when equal; an `Int` beside a `Decimal` binds the `Decimal` overload |
| `math.least` | `(a: Int, b: Int) -> Int`, `(a: Decimal, b: Decimal) -> Decimal` | the smaller, `a` when equal; as `math.greatest` |
| `math.round` | `(x: Int) -> Int`, `(x: Decimal) -> Decimal`, `(x: Decimal, digits: Int) -> Decimal` | to a whole number, or to `digits` fractional digits, halves away from zero (`math.round(2.345, 2)` = `2.35`, `math.round(-2.345, 2)` = `-2.35`, no padding); the `Int` overload is the identity; `digits` outside 0–28 fails closed ("digits must be from 0 to 28"), and a literal one is refused at apply |
| `now` | `() -> Timestamp` | the instant this write is stamped with — the legacy call above, `Mutate` only |
| `replace` | `(text: String, search: String, replacement: String) -> String` | every non-overlapping occurrence, left to right, ordinal; an empty `search` returns `text`; a result that would grow the text past 1,048,576 characters fails closed |
| `size` | `(text: String) -> Int` | Unicode code points |
| `startsWith` | `(text: String, prefix: String) -> Bool` | ordinal, no Unicode normalisation (a decomposed `é` starts with `e`); an empty prefix is `true` |
| `string` | `(value: Int)`, `(value: Decimal)`, `(value: Bool)`, `(value: Uuid)`, `(value: Timestamp)` `-> String` | invariant digits; a Decimal in its shortest form (`string(1.50)` = `'1.5'`, `string(2.0)` = `'2'`); `'true'`/`'false'`; a lower-case UUID; an instant as RFC 3339 in UTC with `Z` and fractional seconds only when non-zero. A `date` field is refused at apply (deviation 36) |
| `substring` | `(text: String, start: Int) -> String`, `(text: String, start: Int, end: Int) -> String` | 0-based, `start` inclusive, `end` exclusive, the one-argument form to the end; fails closed ("a position is outside the text") when `start < 0`, `end < start`, or either is past the text's end — **a cut past the end fails the write**, so cut with the recipe below |
| `timestamp` | `(text: String) -> Timestamp` | RFC 3339 only — `YYYY-MM-DDTHH:MM:SS[.f](Z\|±HH:MM)`, upper-case `T` and `Z`, years 0001–9999, the fraction kept to 100 ns; anything else fails closed ("the text is not an RFC 3339 timestamp such as 2026-10-05T12:00:00Z"), and a literal one is refused at apply |
| `trim` | `(text: String) -> String` | removes U+0020, U+0009, U+000A, U+000D from both ends, nothing else |
| `upperAscii` | `(text: String) -> String` | `a`–`z` → `A`–`Z`, and **nothing else** (`'ß'` unchanged) |

**The truncation recipe.** A `mutate` value longer than its field's `maxLength` refuses the write (Ruling V, below), and
a `substring` past the end fails it, so cut to fit: `substring(new.title, 0, math.least(size(new.title), 40))`.

A value derived from other fields of the row (a total, a full name) belongs in a **computed field**, which stays true on
every write; a `mutate` stamps a value once, when its hook runs (D-17).

**A built-in call over literals that always fails is refused at apply** (deviation 31): `timestamp('2026-10-05')`,
`math.round(new.price, 30)` (a literal `digits` is checked alone, though `new.price` is a field). The message is
`'{name}(...)' always fails with these constant arguments: {reason}.`, with the fix "Correct the constant, or pass a
field instead of a literal." A host function is never invoked at apply.

**Why the folds are explicit `A`–`Z` loops.** `lowerAscii` and `upperAscii` are deliberately **not**
`ToLowerInvariant()`/`ToUpperInvariant()`, which fold a long tail of non-ASCII code points (`Ž`, `Ä`, `ẞ`, `Σ` are
measurably among them) — a culture-sensitive fold applied to a *stored* value is a permanently wrong row, not a display
quirk. A non-string argument is a type error when the descriptor is applied, never a surprise inside a transaction.

**Why `lower(...)` is refused with a fix suggestion naming `lowerAscii`.** Conformant CEL spells the
ASCII fold `lowerAscii`, as the receiver-style macro `x.lowerAscii()`; there is no conformant
`lower(x)` at all. Alvo's grammar cannot express the receiver shape — a bare identifier is always
zero-dot and `old`/`new` are the only one-dot prefixes (deviation 8) — so the profile adopts the
standard's **name and semantics** and deviates only on the **call shape**, which is the smaller of
the two deviations and the same trade `has(...)` and `changed(...)` already make. An author who
writes the SQL spelling `lower(...)` gets the standard's name back as the suggestion.

**SQL translation is slice C2**, which must produce exactly these answers; until then no built-in compiles in `Rule`
or `Computed`. Slice D's design records, per function, what each engine needs for that.

#### Operators in hook slots

Since slice D, arithmetic is legal in `Condition` and `Mutate`, and string `+` in `Mutate` (D-6, D-7):

| Operator | Profiles | Operands → result | Null | Fails closed (`Condition`, `Mutate` only) |
|---|---|---|---|---|
| `+ - *` | Computed, Condition, Mutate | Int, Int → Int (checked 64-bit); any Decimal → Decimal | a null operand → null | Int overflow; Decimal overflow |
| `/` | Computed, Condition, Mutate | Int, Int → Int, **truncated toward zero** (`7 / 2` = `3`, `-7 / 2` = `-3`); any Decimal → Decimal (28 significant digits) | null → null | a zero divisor (Int or Decimal); the smallest Int `/ -1` |
| unary `-` | Computed, Condition, Mutate | Int → Int; Decimal → Decimal | null → null | `-` of the smallest Int |
| string `+` | Computed, Mutate | String, String → String; no implicit conversion (write `string(x)`) | **Mutate: null → null** (deviation 34); Computed: an operand that may be null is refused at compile | never for two texts, unless the result would pass 1,048,576 characters (R-2); a present operand that is no text and no number fails (Ruling P); a stored value over `maxLength` is refused by Ruling V |

- **Only the hook profiles fail closed.** One predicate, `CelHookArithmetic.FailsClosed`, sets the interpreter's
  fail-closed flag from the compiled expression's profile. `Computed` and a `Rule` keep answering `null` on overflow
  and division by zero, exactly as before, so no generated column and no rule changed behaviour. In a hook, `null`
  would be a `reject` that never fires.
- **Int or Decimal is decided by the operands' values** at run time: two integral operands take the checked 64-bit
  path, anything else the `decimal` path.
- **A present operand an operator cannot take throws, on the hook path** — a NaN, infinite or out-of-range double, a
  string in a numeric field, a value of an unexpected CLR type — and so does a present non-Bool under `!`, `&&`, `||`,
  a ternary's condition or as the whole condition (controller Rulings P, Q and R): `false` there is a reject that never
  fires. Only an embedded caller's own record can hold one; the HTTP binder types every value. A **null** operand
  answers as before. `Rule` and `Access` do not move — their `!` over such an operand is an embedded-only residual,
  [#324](https://github.com/Burgyn/MMLib.Alvo/issues/324).
- **The failure names the operator** by CEL's own overload name, so the problem detail reads
  `The CEL function '_/_' failed: the divisor is zero. Nothing was written.` A whole condition that evaluates to a
  present non-Bool is logged under `<condition>`, a token no CEL identifier can be, and its detail is the reason alone:
  `The hook's condition evaluated to a present value that is not a Bool. Nothing was written.` **Guard a division in the condition** rather than letting a zero
  field fail the write: `new.qty != 0 && new.total / new.qty > 100` (`&&` short-circuits left to right).
- **A literal zero divisor is refused at apply** in `Condition` and `Mutate` (`x / 0`, `x / 0.0`, `x / 0.00`):
  `'/' always fails with this constant divisor: the divisor is zero.` **No other operator is folded, deliberately**
  (preflight R-13): `x / (1 - 1)` or `1 + x` past the Int range passes the apply and fails the write. Folding every
  operator over literals is additive and can come later; `Computed` keeps `x / 0` legal, because its division answers
  `null`.
- **Not here:** `%` (deviation 12), comparison and the ternary in `Mutate`, string `+` in `Condition`.

#### Host functions

`AddCelFunction(name, delegate, summary?)` registers a function from host code, validated eagerly (an
`ArgumentException` at the call). The limits, stated so they are not read as oversights:

| Limit | Consequence |
|---|---|
| name `^[a-z][a-zA-Z0-9_]*\z`, at most 64 characters, not reserved | the reserved set is `has changed now in true false null`, `old`, `new`, `math` (the namespace of the math built-ins), CEL's macros, keywords and standard type and function names; a built-in's name is refused first, as a built-in ("it is a built-in function"), never as a standard CEL name |
| at most 4 parameters of `string long int decimal bool DateTimeOffset Guid` (and nullable forms); same result set | no `DateOnly` yet; no async, `ref`, `out`, `params` or multicast delegate |
| singleton closure, no DI scope | a function cannot use a scoped service |
| synchronous, no `CancellationToken`, no timeout | a slow function holds the write's transaction; it must be thread-safe |
| embedded only | the standalone image and `alvo validate` refuse a host-function name as unknown |
| `Condition` and `Mutate` only | refused in `Rule`, `Computed` and `Access`, with the recipe: store the value in a field with a `mutate`, compare that field |

**Trust.** A host function is host code. The descriptor author — an operator or an agent — can call only what the host
exposed and still cannot express a network call; the host developer who registered the code can, and already owns the
process. Purity, speed and thread-safety are by contract, not enforced (spec §5.8). **This narrows a product-spec
guarantee, deliberately:** `alvo-specifikacia.md` §1.2 promises before-hooks a time budget and a network ban enforced
by an analyzer or structurally for both faces, C# included, and `baas-analyza.md` §2.7 a `CancellationToken` the
framework enforces. For a host function there is **no time budget, no `CancellationToken` and no analyzer** — host
code is trusted code (the embedded host's own process, §2.7's in-process trust model). The descriptor author's face
has no millisecond budget and no `CancellationToken` either; it needs neither, because it can express no I/O and its
work is bounded by the descriptor. Trust does not remove the budget: §2.7 keeps it even for fully trusted csx, as a
liveness guarantee — a before-hook runs while the row's locks are held — so a slow host function is an open gap, not
a covered case. The open mitigations — an analyzer over registered delegates, and a token-aware delegate shape with a
framework budget (#309) — are follow-ups (spec §5.8, X14).

#### Resolution, null and failure

- **Overloads** are resolved by arity then argument types; an `Int` argument may bind a `Decimal` parameter
  (deviation 22), nothing else converts.
- **Null.** A null argument for a non-nullable parameter makes the call null without invoking the body; a nullable
  parameter receives the null. A **present** argument that does not convert to the parameter's CLR type (an `Int` past
  `int`'s range for an `int` parameter, a fraction for an `Int` one, a text that is no `Guid`) is never read as null —
  null would make a `reject` condition `false` and let the write through — it fails the call closed, the reason naming
  the parameter and its type (`an argument does not fit parameter 'n' (Int32)`), never the value.
- **Failure fails closed.** A function that throws aborts evaluation as a `CelFunctionException`, which the
  interpreter's catch-alls let through. In a before-hook condition or `mutate` the write is refused and the
  transaction rolls back; what the caller sees depends on who wrote: a **Data API** request answers HTTP 500
  `function-failed`, naming the function and carrying no exception text; an **in-process `IAlvoData` caller** (a host
  endpoint, the dashboard) receives an exception (the internal type surfaces as a plain `Exception`; the dashboard shows
  its generic fault). An **after-hook condition** is already post-commit, so the after-hook is dropped and a Warning is
  logged.
- **A `mutate` value honours its field's facets** (Ruling V, #308). Whatever produced it — a literal, a field copy,
  `replace`, a host function — the value a before-hook writes is measured by the same checks a caller's payload passes:
  `maxLength` (code points), enum membership, `format`, decimal precision and scale (an `Int` widened into a `decimal`
  field is measured as that decimal), and `required` (a null into a required field). A literal that breaks one is
  refused at apply; a computed value that breaks one refuses the write **as the hook's refusal** — the family a `reject`
  uses: HTTP 403 `forbidden` (a per-row refusal in a batch, an `AlvoAuthorizationException` in process), nothing
  written, the detail naming the hook's pointer, the field and the facet, never the value. **A field the descriptor
  flags `hidden`** — a static `true` or a per-role expression, the rule the OpenAPI document uses to leave an optional
  field's name out, applied more strictly (Ruling X) — is not named: its refusal names the hook's pointer only ("computed a value one of the fields it writes
  cannot hold"), with no field, facet or limit, because a refusal naming a field the caller never sent and cannot see
  would disclose that it exists and how wide it is. (The text only stops naming it: a caller-driven value
  copied into a hidden field still makes 403-versus-201 an oracle for its facets — owned by the descriptor author.) **The check runs once, on the final patch** after the whole hook
  chain (Ruling W): a later hook may shorten or replace what an earlier one wrote, and the refusal names the hook that
  last wrote the field. Measured in the core before
  any driver sees the patch, so SQLite (no length enforcement) and PostgreSQL (`varchar(n)`) give the same answer. Not
  422 — that tells the caller to fix a field of *their* payload, and the field may be one they never sent; not
  `function-failed` — the same overrun is reachable with no function at all.
- **Tenancy does not reach inside a function.** Alvo's tenant predicate filters what *Alvo* reads; a host function
  that reads stored data itself (a lookup table, a rate per tenant) must take the tenant as a parameter and filter by
  it. On a tenant-scoped entity pass the row's own `new.tenant_id`, which works in a `condition` and in a `mutate`
  alike — the tenant scope has already admitted it before any hook runs. `@tenant.id` works in a `condition` only: the
  `Mutate` profile refuses it (its refusal message is tracked in #310). One that closes over a store and reads it unfiltered is a
  cross-tenant read Alvo cannot see.
- **The host's exception is logged, never shown.** What a function throws — message and stack trace — is logged at
  Error for a write and at Warning for an after-hook condition. Never put caller data (a field's value, an argument)
  in an exception message: it lands in every log sink the host ships to.
- **Prefer null to a throw.** A host function should answer `null` (or `false`) on input it cannot handle rather than
  throw: every throw is a 500 and an Error log entry, once per request.
- **Versioning.** A function whose meaning changes gets a new name (`vatRate` stays, `vatRate2` is new): a descriptor
  holds names, not versions. Removing or renaming a registered function makes a stored descriptor that calls it fail
  the apply at boot — refused as calling an unknown function.
- **Discovery.** `GET …/cel/functions` (`IAlvoManagement.GetCelFunctionsAsync`, Viewer) lists one entry per overload as
  `{ "functions": [...] }`; the assistant reads it through its `get_cel_functions` tool.

### The profile is narrower than the design addendum's table, deliberately

The PR5 design addendum's *Decision 1* (deviation 79) prints a `Mutate` column with ✓ in **every** row:
`@user`/`@tenant`, `&&`/`||`/`!`, comparison, `in`, `has`, arithmetic, the ternary and
`changed(field)`. **As implemented, only arithmetic is admitted** (slice D, failing closed, with string `+`).
`_allowedProfiles` gives `Mutate` seven rows and no more — literals, current-row field references, `old.`/`new.` field
references, arithmetic, string concatenation, the legacy call and the catalogued call — and the table at the top of
this file is the one that is true.

That is a deliberate deferral, not a half-finished table, and deny-by-default is what makes it safe:
an unlisted pairing compiles in **no** profile, so nothing is silently permitted while it waits. Each
construct arrives with the fact that needs it — a `mutate` such as
`{"is_closed": {"$cel": "new.stage == 'won'"}}` will bring the comparison row with it, and the
addendum's argument for why `Mutate` *may* hold it stands unchanged.

**Measured, so the deferral is measured rather than convenient:** the only descriptor in the tree
that declares a `mutate` is `examples/complex-crm/crm.alvo.json`, and its two values are
`lowerAscii(new.email)` (`:110`) and `now()` (`:148`). Both compile. Nothing that ships is blocked by
the narrowness, and the profile's own truth table records what a widening would have to add.

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

**The three `access` levels go through the same walk**, against the same `RoleCatalog`, in the same
apply pass — reported at `/access/admin`, `/access/developer` and `/access/viewer`. Not a second
check written beside the first: a level's typo fails exactly the way a rule's does, with the same
"did you mean" suggestion, and one apply reports a bad level and a bad rule together rather than
half-applying the descriptor.

## Two-valued rendering: the rule both backends must agree on

Alvo has two `CompiledExpression` backends — `CelInterpreter` (in-memory, used for `WITH CHECK`
when there is a candidate row but no stored row to filter: a `create`, a hook `Condition`, or a
before-hook `Mutate` value) and `SqlPredicateRenderer` (SQL, used for `USING`) — and a differential
property test proves they never disagree on any well-typed expression and record **that both can
hold**, which never includes a `Mutate` or an `Access` expression: the renderer refuses both
profiles at its entry points, so each has one backend and this whole section is inapplicable to
them. Both backends follow the **same** null
rule, which is
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
deviation below is a stated narrowing (or, for 1–3, 15–16, 31 and 33, an addition), not an invented variant
of a standard:

**Additions (constructs conformant CEL does not have):**

1. **The `@user`/`@tenant` context-reference syntax entirely** — CEL has no `@`-prefixed syntax at
   all. Alvo's closed set is exactly `@user.id`, `@user.roles`, `@tenant.id`; every other member on
   an otherwise-recognized context name throws a syntax error with a specific fix suggestion
   (`@user.role` → test membership via `in @user.roles`; `@user.claims`/`@user.teams` → tracked by
   `#37`), and an unrecognized `@name` throws at the lexer.

   **Deviation from `baas-analyza.md` §16.1 (line 1155), stated because the analysis sketches what
   this forecloses.** Its worked `access` block reads
   `"admin": "'manager' in @user.roles && @user.email.endsWith('@firma.sk')"` — an *attribute* gate
   on an email domain. `@user.email` is not in the closed set above and there is no descriptor, in
   any block, that can express it today. So the descriptor's `access` is **role-based only** — the
   decision recorded on `#146`, taken because an attribute-based one would put the hardest half of
   `#37` on F5's critical path, while a runtime string dictionary on `AlvoContext` would forfeit
   fail-fast rule compilation. The narrowing costs nothing later: `@user` gaining typed members is
   additive, so `'manager' in @user.roles` keeps compiling once `#37` lands.

   **What it costs now is visible in `examples/complex-crm`**, and its `NOT-RUNNABLE.md` records it
   rather than papering over it: the domain gate was the only thing distinguishing that example's
   `admin` from its `developer`, so under this narrowing the two became one predicate. A two-level
   distinction resting on an attribute cannot survive a role-only context, and inventing a role to
   keep them apart would assert an org shape the example never had. Once the levels are *enforced*,
   two identical predicates are worse than a smaller example — highest-match-wins makes the lower
   one a level no caller can ever hold — so that file now declares `admin` and `viewer` only, and
   `NOT-RUNNABLE.md`'s *"`access` is role-based only"* section carries the whole history and the
   reason `developer` left. The schema key stays covered by the schema suite's own sample
   (`MMLib.Alvo.Schema.Tests.AccessLevelsTests`).

   **It is now an enforcement narrowing too, and that is #146 landing.** `PolicyCatalogBuilder`
   compiles the three `access` levels in the same pass as every rule, against the same
   `RoleCatalog`, so a level naming a role `auth.roles` does not declare is refused at apply with the
   same "did you mean" suggestion a rule's typo gets. `UnhonouredSubsystems` no longer carries an
   `access` entry — the file's own doc comment demanded that transition — and a caller who matches no
   level is refused `403` on every management operation. "Gated" was load-bearing when this was
   written — the gate was attached per route by `RequireAlvoManagementAccess(<operation>)`, and a
   route without it was refused by nothing. It is not any more: `AlvoManagementService` reads the
   same table at the head of every `IAlvoManagement` member, so the route filter is an early
   rejection and the surface is closed whether or not a route carries it. See
   `management-api.md`, *One path, two transports*.
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
   immediately followed by `(` that is neither `has`/`changed` nor a catalogued function is refused, with a suggestion to move the
   logic into a hook instead.
8. **No nested field access beyond exactly one level of `old.`/`new.`** — real CEL supports
   arbitrary `a.b.c`-style navigation; Alvo's row model is flat, so a bare identifier is always
   zero-dot and `old`/`new` are the only one-dot prefixes. A catalogued dotted name (`math.round(`) is a call, never a
   path. `CelNames` (Admin) relies on this: a name followed by `.` is never a column, so if JSON paths ever land, a
   rename would silently skip `meta.x` — revisit `CelNames.IsColumn` with them.
9. **`has(...)` narrowed to exactly one argument**, a bare field name or an `old.`/`new.`-qualified
   one — real CEL's `has()` tests presence over arbitrary qualified paths into nested messages.
10. **`==`/`!=` against a `null` literal rejected in favor of `has()`** — `owner_id == null` would
    otherwise always evaluate to `false` under the two-valued null rule above regardless of whether
    `owner_id` is actually `null`, silently making `!(owner_id == null)` always `true`. The compiler
    rejects it outright and redirects the author to `has(field)`/`!has(field)`, which is exactly what
    the author actually means.
11. **String relational operators (`<`, `<=`, `>`, `>=`) rejected outside the Computed profile** —
    collation-dependent comparison is only meaningful where the database itself evaluates the
    expression (a computed column); in the Rule/Condition/Access profiles it is refused with a
    suggestion to use `==`/`!=` instead, or move the comparison into a computed field. The check is
    written as "every profile but `Computed`", so `Access` inherited it without a second rule.
12. **No modulo (`%`)** — the lexer has no case for it; arithmetic is limited to `+ - * /`.
13. **Numeric literals are plain decimal digit runs only** — no hex integers (`0x1A`), scientific
    notation (`1e10`), or an unsigned-literal suffix (`123u`) that CEL supports.
14. **Relational operators are non-associative** (`a == b == c` throws) — **not** a narrowing;
    conformant CEL itself forbids chained relational operators, listed here only so this doc doesn't
    have to be re-derived from the spec by a future reader.

**Added by the `Mutate` profile.** Numbered at the end of the series rather than inserted into the
group they belong to, because other documents cite these numbers (*deviation 6*, *7*, *8*) and
renumbering would silently repoint every citation:

15. **`lowerAscii(x)` takes CEL's standard-library name in Alvo's own call shape** — an *addition* to
    the grammar and a partial reversal of narrowing 7 (for `Mutate` when it was added; since slice D an ordinary
    built-in in `Condition` and `Mutate`, under deviation 27). Conformant CEL spells the
    ASCII fold as the receiver macro `x.lowerAscii()`, which narrowing 8's one-level dot rule cannot
    express, so the standard's name and semantics are adopted and only the call shape deviates. The
    SQL spelling `lower(...)` is refused, with `lowerAscii` as the fix suggestion. The fold is
    ASCII-only *by definition*, hence an explicit `A`–`Z` loop and never `ToLowerInvariant()`.
16. **`now()` as a nullary function** — conformant CEL has no `now`; a host supplies time as a
    variable. Alvo spells it as a call because `Mutate` needs the call machinery for `lowerAscii`
    regardless, and because `@now` would widen the closed `@`-context set and be its first
    memberless member. It is a **bound instant** rather than a clock read, and never rendered to SQL
    — both stated above.

The legacy call `now()` is admitted in `Mutate` only; the catalogued functions (built-ins and host functions) are
admitted in `Condition` and `Mutate`, each within its own profile list. Narrowing 7 stands for every other profile and every other
name: an identifier followed by `(` that is neither `has`/`changed` nor a catalogued function is still a syntax error.

**Added by the function catalog** (spec §11):

17. **`trim` and `replace` take the global call shape** — cel-go spells them as receiver macros; Alvo adopts the names
    and semantics and not the shape, as for `lowerAscii` (15). The receiver spelling is refused with the global form as
    the fix. (`size` is conformant, and so, since slice D, are `math.abs` and `math.round`, which C1 shipped as `abs`
    and `round`.) A catalogued dotted name is matched over tokens, so `math .round(x)` with spaces is the same call;
    a type error inside that spelling is reported at the end of the last name located before it rather than at the
    call, because the position search looks for the name's text (slice D preflight F1-4, accepted).
18. **Null in, null out** — a null argument makes the call null where CEL has no matching overload; SQL parity, and the
    `lowerAscii` precedent.
19. **A failing call aborts evaluation** — CEL's error values and commutative `&&`/`||` absorption are not modelled, so
    `f(x) && false` fails closed rather than answering `false`.
20. **`trim` removes four ASCII characters** (space, tab, line feed, carriage return) where cel-go removes Unicode
    whitespace — SQL parity, and those are the escapes the lexer has.
21. **`replace` with an empty search returns the text** where cel-go inserts between code points — SQL parity.
22. **`Int` may bind a `Decimal` parameter** — CEL has no implicit conversion; Alvo's comparisons already widen
    numerics, so a call does the same.
23. **`math.round` sends ties away from zero** (as cel-go's does), the `Int` overload is the identity, and the
    `Decimal` overload stays `Decimal` rather than `double`. The `digits` overload is deviation 33.
24. **Host function names are narrower than CEL identifiers** and reserve CEL's standard names — a name an agent trained
    on CEL reads as syntax is refused at registration, which costs nothing.

**Added by slice D** ([design](../superpowers/specs/2026-10-06-f5-hook-functions-end-to-end-design.md) §16, F9–F20):

25. **`math.ceil` and `math.floor` take and return `Decimal`** (cel-go: `double`) and have an `Int` identity overload,
    extending 23. Alvo has no `double`, and money is decimal.
26. **`math.greatest` and `math.least` take exactly two arguments** (cel-go: one or more, or a list). The catalog has no
    variadic parameter and Alvo no list literal (deviation 6); nesting composes three.
27. **`lowerAscii`, `upperAscii`, `substring`, `contains`, `startsWith` and `endsWith` take the global call shape**
    (the standard and cel-go: the member form, `x.contains(y)`) — deviation 17 extended. Receiver syntax is deferred,
    additively: it would desugar to the same call, and a catalogued dotted name would still win over it.
28. **`string(Decimal)` writes the shortest form** (no trailing fractional zeros: `string(1.50)` = `'1.5'`). CEL has no
    decimal; this is `string(double)`'s behaviour and the only engine-agnostic answer, since a column's scale differs
    per engine. **`string(Timestamp)` always writes UTC** (`Z`): cel-go keeps the parsed offset, and Alvo writes UTC
    because it stores instants, not offsets. Read in cel-go v0.29.2: `String.ConvertToType(TimestampType)` is
    `time.Parse(time.RFC3339, …)`, which keeps the text's offset, and `Timestamp.ConvertToType(StringType)` is
    `t.Format(time.RFC3339Nano)` with no `UTC()`, so `string(timestamp('2026-10-05T14:00:00+02:00'))` is
    `'2026-10-05T14:00:00+02:00'` there and `'2026-10-05T12:00:00Z'` in Alvo. The cel-spec identity overload
    `string(string)` is deliberately absent — text needs no conversion — and a call to it is refused at apply.
29. **`timestamp(text)` admits upper-case `T` and `Z` only and keeps 100 ns of the fraction** (CEL: nanoseconds) —
    .NET's resolution; Go's RFC 3339 parser is case-sensitive too. The identity `timestamp(timestamp)` and
    `timestamp(int)` (Unix seconds) are deliberately absent, and a call to either is refused at apply.
30. **`int(String)` admits `[+-]?[0-9]+` only** — no whitespace, no other base, as Go's `ParseInt(s, 10, 64)`.
    `int(timestamp)` (Unix seconds in cel-spec) is deliberately absent: no hook asks for epoch seconds yet, and an
    overload added later is additive. `int(int)` has no overload of its own and still compiles: an `Int` binds
    `int(value: Decimal)` (deviation 22), and cutting a whole number toward zero is the number itself.
31. **A built-in call over literals that always fails is refused at apply**, and so is a literal `digits` out of range
    and a literal zero divisor in a hook. An addition, not a change of any value: CEL defines no compile-time
    evaluation, and cel-go's constant folding is the precedent. Checking is shallow on purpose — only literal
    arguments of a call, and a literal divisor; `x / (1 - 1)` fails at write time.
32. **`contains`, `startsWith` and `endsWith` compare UTF-16 code units ordinally** — code points for well-formed text;
    a lone-surrogate search can match half a pair, as C1 recorded for `replace`.
33. **`math.round(x: Decimal, digits: Int)`** — cel-go's `math.round` has one argument. Rounding a price to cents is
    the commonest money rule a hook has, and the one-argument form cannot express it. Halves away from zero, like the
    one-argument form; `digits` 0–28, `decimal`'s scale limit; a literal out of range is refused at apply, a computed
    one fails closed.
34. **String `+` in `Mutate` answers null when an operand is null** (CEL: `+` has no null overload, so an error) — the
    null policy every Alvo function has (18), and the answer SQL's `||` gives, so a later SQL rendering of a mutate
    needs no guard. `Computed` keeps refusing a nullable operand at compile.
35. **Decimal arithmetic.** CEL has no decimal: `+ - * /` over `Decimal` follow `System.Decimal` (28 significant
    digits), and overflow and division by zero fail closed in `Condition` and `Mutate`, as CEL's `int` errors do. A
    null operand still makes the result null rather than an error (`baas-analyza.md` §3.3, "null-safe operators"; a
    condition reading null does not fire). Int arithmetic is CEL-conformant: checked 64-bit, `/` truncates toward
    zero, and overflow and division by zero are errors.
36. **`string()` refuses a `date` field** at apply. CEL's core has no date type; Alvo's `date` reaches CEL as midnight
    UTC, and pinning that text now would make a future Date type's natural `2026-10-05` a breaking change.

**Residual caveat, not a narrowing:** the string-collation caveat on `==`/`!=` documented above — it
is a real divergence *risk* between the two backends under a non-default collation, not a construct
Alvo's grammar refuses.
