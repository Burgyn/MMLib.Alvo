# F4 PR-A — four guards: two at apply, one at request time, one at startup

Issues: **#124**, **#156** (apply), **#123** (request time + a remark), **#125** (startup).

Filed separately, designed together because they are one kind of change: *a declared bound and
the thing that enforces it disagreeing* — about units (#123.1), about range (#123.2), about
satisfiability (#124), about ownership (#156), about strength (#125). None of them adds a
capability; each closes a gap between what the descriptor (or the options) says and what the
build does about it. That is why they share a PR rather than four.

None touches the rule engine's SQL rendering, the tenancy predicate, or the policy resolve, so
this is not a security-core change — but #124's half 2 and #125 both sit *beside* it, and both
are reviewed against `alvo-security-core-review` for that reason.

---

## 1. #123.1 — `maxLength` counts Unicode code points, not UTF-16 code units

`RecordValidator.TooLong` compares `text.Length`, which is UTF-16 code units. Six astral-plane
characters are twelve of those, so `maxLength: 10` refuses a value PostgreSQL's `varchar(10)`
stores happily.

### The decision: **runes** (`EnumerateRunes`), not text elements (`StringInfo`)

The issue offers both. They are not interchangeable, and only one of them is safe:

| unit | what counts | vs. PostgreSQL `varchar(n)` |
|---|---|---|
| UTF-16 code units (today) | `string.Length` | **over**-counts → false 422, safe direction |
| Unicode code points (`Rune`) | `char_length` | **exactly** what the column bounds |
| grapheme clusters (`StringInfo`) | user-perceived characters | **under**-counts → Alvo accepts a value the column cannot store |

PostgreSQL's `character_length`/`varchar(n)` bound is characters in the SQL sense = code points.
A family emoji (a ZWJ sequence) is one grapheme cluster and seven code points, so counting
grapheme clusters would admit a value seven times over its column's bound — a store-level error
at INSERT, which is the one direction the current bug does *not* fail in. Runes match the store
exactly, so the check and the column agree by construction rather than by luck.

**SQLite enforces nothing** (no length constraint on a `TEXT` affinity column), so it cannot
break the tie; the tie is broken by the engine that does enforce, which is the engine-agnostic
answer — the bound must be the tightest any registered driver applies.

**And that tie-break carries an obligation the third engine inherits, recorded here so it is not
rediscovered.** §0 principle 3 names **Azure SQL** alongside SQLite and PostgreSQL, and T-SQL does
not agree with PostgreSQL here: `nvarchar(n)` bounds *n* UTF-16 units, so ten astral characters
would pass this validator and fail the INSERT. This is correct today with the two shipped drivers
and inverts the moment a T-SQL dialect is registered. Whoever registers one owes the answer — a
per-dialect length unit on `IAlvoSqlDialect`, or a widened column — and it is **a port member, never
an `if` in `RecordValidator`**, on the standing rule that per-engine behaviour lives in the dialect.
The obligation is stated on `TooLong`'s own remarks as well as here, and **filed as #175** alongside
the T-SQL follow-up already open as #92. The five author-facing sites that publish the unit —
`FieldDescriptor.MaxLength`, `FieldSchema.MaxLength`, `schema/project.schema.json`,
`PayloadViolations.MaxLength` and `CHANGELOG.md` — scope the claim to the shipped drivers rather than
stating it as universal, which is the half of CodeRabbit's review finding on #174 that was acted on.

**The other half — build the seam now — was deferred, and the first reason given for deferring it was
wrong.** That reason was "there is no SQL Server driver in this repository, so a port member would have
no implementer and no test". `MMLib.Alvo.Testing.EntityFrameworkCore` ships **`TSqlSqlDialect`**, a
public T-SQL fake whose whole purpose is to prove a seam sufficient for T-SQL *before* a real driver
exists, and `AlvoSqlDialectContractTests` is the suite that holds it. The repo has already run this
exact exercise once, for `RowLockClause`, and it found that a T-SQL author following the contract would
have shipped silently unlocked `WITH CHECK` pre-images. So the implementer and the test both exist.

**The deferral stands on the honest reason instead:** this is a separate change with a different shape
and a different risk — a new member on the public `IAlvoSqlDialect`, three implementations, both
`HasMaxLength` call sites, a contract fact, two approval baselines — plus an unresolved design question
(`nvarchar` caps at 4000, so a doubled `maxLength` over 2000 needs `nvarchar(max)`, which an `int`
return cannot express). Bolting that onto a PR of four small guards that has already passed plan-guard,
a review round and green CI would put an undesigned change through a review that was not about it.
**#175 carries the seam's shape, the precedent and the open cap question.**

### What ships

- `TooLong` counts runes.
- The unit is **named** in the four places that publish it, because "characters" is exactly the
  ambiguity that produced the bug: `FieldDescriptor.MaxLength`'s doc comment,
  `FieldSchema.MaxLength`'s doc comment, `schema/project.schema.json`'s `maxLength` description,
  and `PayloadViolations.MaxLength`'s own message and fix.
- The published OpenAPI document's `maxLength` keyword is **unchanged**. JSON Schema's own
  `maxLength` is defined in code points, so the document was already right and the validator was
  the half that disagreed with it.

**Cost.** Counting runes is O(n) where `string.Length` was O(1). It runs once per supplied
string field per write, bounded by the body size the reader already caps, and it short-circuits:
a string whose UTF-16 length is already within the bound cannot exceed it in code points
(code points ≤ code units), so the fast path stays O(1) and only a string that *would have been
refused* pays the walk.

## 2. #123.2 — `ExceedsScale`'s `<= 28` bail-out is correct, and gets the remark

The issue leaves the choice open: document the bail-out, or refuse `scale > 28` at apply.

**It is documented, not refused**, for the reason its sibling `ExceedsPrecision` already carries:
a `decimal` holds at most 28 fractional digits, so a bound value can never *exceed* a declared
scale above 28 — the guard skips a comparison whose answer is already known, and skipping it is
what keeps `decimal.Round` from throwing `ArgumentOutOfRangeException` for a scale it cannot
express. Refusing `scale: 30` at apply would refuse a legal `NUMERIC(38,30)` column that this
build can validate every bindable value against, correctly, today.

The fix is therefore the remark the sibling has and this one lacks — stated as the *decision* it
is, so the next reader neither "fixes" it nor copies it as a typo.

## 3. #124 — `required` + `readOnly`, in its two halves

### Half 1 — a statically read-only required field is refused at apply

`required: true` + `readOnly: true` makes every create unsatisfiable: supplying the field is
`read-only-field` 422, omitting it is `required` 422, and there is no third request.

Refused in `DescriptorValidator`'s **semantic pass**, beside `DeclaresAManagedColumn` and
`ShadowsAReservedQueryParameter` — not in `UnhonouredFeatures`. That table is the authority on
*declared-and-unimplemented*; this is a *contradiction between two implemented flags*, and
putting it in the table would say Alvo intends to honour it later, which it does not.

The raw-JSON pass only, for the same reason those two siblings are there: it must report with a
pointer and a fix even when the schema pass has already failed, and the mapper needs no
corresponding throw because nothing downstream mis-maps — the descriptor is applied and every
create then fails, which is precisely the outcome being refused.

**Scope of "statically read-only": the literal `true`, and nothing else.**

- `readOnly: {"$cel": "…"}` is half 2's; a static refusal cannot judge it.
- `computed` and `rollup` also make a field read-only, and `required` + either is **not** refused:
  a generated column is computed by the database on the INSERT itself, so `NOT NULL` is satisfied
  without the caller ever writing it. Refusing those would refuse a working shape.

### Half 2 — a caller whose own mask froze a required field gets an honest answer

When `readOnly` is expression-valued, the combination is satisfiable for one role and impossible
for another. Today that caller is told `required` — "supply this field" — for a field they may
not write.

**The issue's own wording — "skip the `required` check" — is not what ships, and the reason is
measured, not stylistic.** Skipping it lets the create through to the port with the field absent,
where a `required` field is a `NOT NULL` column: the write fails inside the engine and surfaces
as a 500 (or, once #138 lands, a 409). That trades a wrong 422 for a worse answer.

What ships is a **third violation**, `read-only-required-field`, reported instead of `required`
when *all three* hold: the request is a create, the field is `Required`, and this caller's own
`ReadOnlyFields` mask contains it. It says the create is impossible **for this caller**, names
the field, and its fix names the two real ways out — a role that may write the field, or a
descriptor change (a `default` once #113 lands, or `computed`). One violation, not two, and not
one that sends the caller to fix something they cannot.

It is reachable **only** through an expression-valued mask, because half 1 refuses the static
case at apply. That is the tie between the halves: neither is complete alone.

### Half 3, found in review — a field the *store* fills in is never missing

The pre-PR review found the case that makes half 2 wrong on its own: a field declaring
**`required` + `computed` (or `rollup`) + an expression-valued `readOnly`**. The mask freezes it,
the field is `required`, the caller omits it — and `read-only-required-field` would fire, asserting
an impossibility, for a create that **succeeds**: the database computes the generated column on the
INSERT itself, which is the very reason half 1 exempts `required` + `computed` at apply.

Worse than the bug it replaces: the pre-existing `required` refusal in the same shape was merely
unhelpful, while the new message is specific and confidently wrong.

So `IsMissingRequiredValue` gains `IsFilledInByTheStore` — a `computed` or `rollup` field is not
"missing" on a create, in either violation. Deliberately the **create branch only**: an explicit
`null` is a *write* to a framework-maintained field, a different request, and refusing it stays
right. This also closes the same hole for the plain `required` + `computed` case, which predates
this PR.

## 4. #156 — the framework's own table names are reserved against an entity declaration

`SystemSchemaInitializer.FrameworkTableNames` keeps `alvo_descriptor_versions`,
`alvo_idempotency` and `alvo_outbox` out of introspection so a re-apply plans no `DROP` for them.
It does not *reserve* them: `DescriptorModelBuilder.ToTable(entity.Name)` maps an entity onto its
own name verbatim, so an entity named `alvo_outbox` and the framework believe they own one table.

### The authority moves to `MMLib.Alvo.Abstractions` — internal, not public

The refusal belongs at apply, in the **core**; the names are built in
**`MMLib.Alvo.Data.EntityFrameworkCore`**, which the core does not (and must not) reference. A
second list in the core is the exact defect `UnhonouredFeatures`' own opening remark forbids — so
the *naming* moves down to the one assembly both can see:

```
MMLib.Alvo.Abstractions/AlvoFrameworkTables.NamesFor(string schemaPrefix)
```

`SystemSchemaInitializer.DescriptorVersionsTableName`, `IdempotencyTable.NameFor` and
`OutboxTable.NameFor` are then spellings *of* that authority rather than three peers of it, and
a fourth framework table added later reserves itself by being named there.

**`internal`, with a third `InternalsVisibleTo` on Abstractions — not public.** The first draft made it
public and the maintainer challenged it; the challenge was right and the evidence is one-sided:

- The information was **never public**. It lived on `SystemSchemaInitializer`, an `internal sealed`
  class, so its `public` members were public-on-internal. Publishing it here would have been a new
  commitment, not a move — and the PR's public-API delta is therefore **zero**, not `+1 type`.
- **The shipped seam for a new engine is `IAlvoSqlDialect`**, which plugs in *under* the Entity Framework
  Core adapter and never sees a table name — `MMLib.Alvo.Data.Sqlite` contains a dialect, a field
  renderer and DI extensions, and no introspector. The adapter does the creating and the
  introspection-excluding for every dialect, so a third engine joining the intended way never needs
  this list.
- The one consumer who would is someone writing an `ISchemaIntrospector` from scratch. Nothing in the
  repository is evidence that anyone does, and the package-boundary rule's own logic — a package is
  *earned* — applies to public surface for the same reason.
- **The asymmetry settles it.** `internal` → `public` later is additive; a `public const` cannot be
  taken back, and worse, C# inlines it into a consuming assembly at compile time, so renaming a suffix
  would break a host silently on upgrade until it rebuilt. That risk was written into this PR's own
  report as a section-7 line; making the type internal removes it rather than documenting it.

### Threading the prefix

`DescriptorValidator` is static-ish today (`Validate(string)`), and the reserved set depends on
`AlvoOptions.SchemaPrefix`. It gains an `AlvoOptions` constructor parameter, resolved from
`IOptions<AlvoOptions>` in DI and defaulted to `new AlvoOptions()` on the convenience
constructors, so a CLI `validate` with no host still reserves the default `alvo_*` names — which
is the set 100% of projects actually use.

The error is the shape every other name collision gets: the entity's own JSON pointer, the
colliding name, and a fix naming `AlvoOptions.SchemaPrefix` as the other way out.

### Deviation: one leg, not two

**Recorded because it departs from a rule this codebase otherwise keeps.** Every other apply-time
refusal here has *two* legs — the validator reports it from raw JSON, `DescriptorToSchemaMapper`
throws for it from the parsed descriptor — and `UnhonouredFeature`'s own shape exists so a feature
cannot be added to one pass without the other. This refusal has the validator leg only.

**Why the mapper cannot carry it.** The reserved set depends on `AlvoOptions.SchemaPrefix`, which is
*deployment* state; `Map(AlvoDescriptor)` sees only the descriptor. A defaulted prefix there would
refuse `alvo_*` under a project configured with `acme_*` — refusing the wrong names, which is worse
than refusing none.

**Why one leg is sufficient rather than merely convenient**, which is the part a later reader needs:
`DescriptorToSchemaMapper` is `internal` (visible to test assemblies only), so no host can reach it;
and both apply paths — `DescriptorBootPlan.LoadAsync` and `RuntimeSchemaService.ApplyAsync` — run
`IDescriptorValidator` and throw `DescriptorValidationException` on any `Error` *before* they map.

**The residue, stated rather than implied:** a test that maps without validating gets no refusal, and
several such tests exist. If the mapper ever becomes reachable without the validator, this refusal is
the one that does not follow it.

## 5. #125 — an entropy floor under the dev API-key secret

`ApiKeyHash.Compute` is a single unsalted SHA-256 pass. That is standard for a **high-entropy
random** key and stays; what is missing is anything making the secret high-entropy.
`AlvoAuthOptionsValidator` requires only non-empty, so `Secret = "password"` is accepted and its
digest is a rainbow-table lookup away the moment the hash reaches a log or a support bundle.

**The floor is 32 characters**, checked at startup with the rest of the dev-key validation, and it
is set **at this repository's own recipe rather than above it**. `openssl rand -hex 16` — the line
`scripts/test-e2e`, `playground/run` and the examples' READMEs all publish — is 128 bits written as
exactly 32 hex characters. So every secret the docs tell an operator to generate passes, and no
hand-typed word does; a floor that refused the project's own documented recipe would be one nobody
can satisfy by following the instructions. The refusal names that recipe rather than a different
one, which is the review finding that produced this sentence.

**What a length floor cannot do, said on the type rather than implied:** it does not detect a
long *guessable* secret, because entropy is not measurable from the string. That is not a hole to
be plugged here — it is the reason the mechanism is documented as dev-only and the reason **#36**,
the real issuance path, must not inherit this hash: a user-chosen secret needs a password KDF
(Argon2id/PBKDF2) with a per-key salt, not one SHA-256 pass with a length check in front of it.

**Breaking change, stated as one.** Two test fixtures carry secrets under 32 characters
(`AlvoHostWorld.AdminSecret`, `ChildHostHarness.AdminSecret`) and are lengthened. A host with a
short dev secret now fails to start rather than starting weak — which is the intent, and it fails
at startup with a message naming the key, not at request time with a 401.

---

## Acceptance

| # | fact |
|---|---|
| 123.1 | A string of 6 astral-plane runes is accepted against `maxLength: 10` and refused against `maxLength: 5`; both engines store the accepted one. |
| 123.2 | The remark exists; the existing scale facts stay green (no behaviour change). |
| 124.1 | A descriptor with `required: true, readOnly: true` yields one error at that field's pointer, with a fix; a descriptor with `required` + `computed` yields none. |
| 124.2 | With an expression-valued `readOnly` that freezes the field for this caller, a create omitting it answers `read-only-required-field`, not `required`; the same create by a caller the expression does not freeze succeeds. |
| 124.3 | The same shape with `computed` added is **not** refused: the create succeeds and the column carries the computed value. |
| 156 | An entity named `alvo_outbox` is refused at apply with its pointer; under `SchemaPrefix = "acme"` it is accepted and `acme_outbox` is refused instead. |
| 125 | A dev key with a 31-character secret fails startup naming the key; 32 characters starts. |
