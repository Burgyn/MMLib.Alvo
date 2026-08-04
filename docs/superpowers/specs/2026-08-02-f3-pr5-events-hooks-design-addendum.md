# F3 PR5 (#22) — event pipeline + lifecycle hooks — Design addendum

> Amends the *Events, hooks, automation* section of
> `docs/superpowers/specs/2026-07-25-f3-crud-vertical-slice-design.md`
> (lines 542–618) and the PR5 row of its PR table (`:733`). It does **not** replace that
> section: everything it does not contradict still stands.
>
> Two decisions are settled here and argued in full — the **fourth CEL profile** for
> `mutate`, and the **total deferral of JSONata**. Three more are *recommended* and
> marked as needing the maintainer's ratification: the ordering guarantee, custom
> application events, and the PR split. Deviations are numbered from **58**; the base
> design ends at 51 and the in-flight startup PR has taken 52–57.
>
> This is a design, not an implementation plan.

## Sources consulted

Written against a prior read-only study of the repo (the PR5 risk register, `018d47b`),
whose findings are inherited rather than re-derived, plus the following read directly
while writing this document:

- **The frozen descriptor surface** — `schema/project.schema.json`: `$defs/cel` (`:352`),
  `$defs/celExpr` (`:358`), `$defs/valueOrExpr` (`:371`), `$defs/jsonata` (`:398`),
  `$defs/cron` (`:404`), `$defs/eventPattern` (`:409`), `$defs/beforeHookList` (`:897`),
  `$defs/afterHookList` (`:947`), `$defs/automationRule` (`:963`), `$defs/action` (`:1067`).
- **The compiler as built** — `src/MMLib.Alvo/Expressions/Internal/CelCompiler.cs:79-106`
  (result-type validation), `CelParser.cs:277-281` (no list literals),
  `CelParser.cs:378-417` (`ParseCall`, hard-wired to `changed`),
  `src/MMLib.Alvo.Abstractions/Expressions/CelProfile.cs` (the three profiles),
  and `docs/architecture/cel.md:10-45` + `:226-282` (the profile truth table and every
  deliberate narrowing).
- **The write seam** — `docs/architecture/data-path.md:1380-1391` (*"The transaction is
  already the right seam"*; and the `ExecuteUpdate`/`ExecuteDelete` interceptor trap),
  `:121-145` (`UseRelationalNulls()`, whose cost *"PR5 is the first PR to bind"*),
  `:354-391` (every timestamp is one instant, naming PR5's outbox at `:386`),
  `src/MMLib.Alvo.Data.EntityFrameworkCore/Internal/EfAlvoData.cs:175-179` (the create
  path's pre-transaction candidate).
- **The refusal mechanism to reuse** —
  `src/MMLib.Alvo/Descriptor/Internal/UnhonouredFeatures.cs` (errors; per hook point,
  `:126-161`) and `UnhonouredSubsystems.cs` (warnings; `:72-96`), plus the line that
  states the difference (`UnhonouredSubsystems.cs:12-19`).
- **The sources the design compresses** — `docs/product/alvo-specifikacia.md:141`
  (wildcard subscribe is a *hard* guarantee), `:300` (the JSONata in-transaction ban,
  *"testom"*), `:330` (the F4 automation action set, **including `function`**, and *"Demo
  pravidlo „STK končí o 30 dní → email" musí byť postaviteľné už z F4"*);
  `docs/product/baas-analyza.md:656` (the ordering **hedge**), `:657` (tenant isolation of
  rules), `:676-680` (the §3 acceptance criteria), `:819` (the scheduling component's
  *"cron job sa v 3-instance nasadení spustí práve raz"*).
- **Prior art, read rather than recalled** — CloudEvents **v1.0.2**
  [`cloudevents/spec.md`](https://github.com/cloudevents/spec/blob/v1.0.2/cloudevents/spec.md):
  attribute naming (`:162-175`), the seven-type system (`:177-217`), extension attributes
  (`:430-437`), the 64 KB forwarding rule (`:510-512`); and the extension registry
  ([`documented-extensions.md`](https://github.com/cloudevents/spec/blob/v1.0.2/cloudevents/documented-extensions.md),
  [`extensions/`](https://github.com/cloudevents/spec/tree/main/cloudevents/extensions)).
  .NET hosting: [BackgroundService runs all of ExecuteAsync as a Task](https://learn.microsoft.com/dotnet/core/compatibility/extensions/10.0/backgroundservice-executeasync-task)
  and [IHost.RunAsync/StopAsync throw when a BackgroundService fails](https://learn.microsoft.com/dotnet/core/compatibility/extensions/11/ihost-runasync-stopasync-throw-backgroundservice-failure).

Deliberate deviations are collected in *Deviations* below, numbered from 58, so a later
reader can tell a decision from an oversight.

---

# Decision 1 — a fourth CEL profile for `mutate`, not a widening of `Computed`

## The hole, stated exactly

A before-hook's `mutate` maps a field name to `$defs/valueOrExpr`
(`schema/project.schema.json:936-941`), i.e. either a JSON literal or `{"$cel": "…"}`. So
a `mutate` expression **must produce a value**, and it must be able to see the row being
written — that is the entire point of a before-hook.

No profile the compiler has can compile that expression:

| Profile | Refuses it because |
|---|---|
| `Rule` | rejects a non-`Bool` result (`CelCompiler.cs:97-102`); and cannot see `new.`/`old.` at all (`cel.md:21`) |
| `Condition` | rejects a non-`Bool` result — the **same** branch, `CelCompiler.cs:97-102` |
| `Computed` | accepts a non-boolean scalar (`CelCompiler.cs:81-95`) but forbids `old.`/`new.`, `@user`/`@tenant` and `in` (`cel.md:21-23`) |

The gap is not an oversight in one table; it is structural. `Rule` and `Condition` are the
two *predicate* profiles and `Computed` is the one *value* profile, and `mutate` is a value
expression over the hook context — a combination the three-profile design has no cell for.
Nothing in `cel.md`, the base design, or the brief mentions it.

## The decision

Add a fourth profile, **`CelProfile.Mutate`**, appended to the enum
(`src/MMLib.Alvo.Abstractions/Expressions/CelProfile.cs`), with this row in
`cel.md`'s truth table:

> **Corrected by PR5b, which implemented it.** This table's `Mutate` column was written as
> the *upper bound* the profile may hold, and it read as a promise. What shipped is
> narrower: `_allowedProfiles` admits **four** constructs in `Mutate` and no more. The
> **Shipped** column below is what `CelTypeChecker` really does; the **Design** column is
> this decision's original bound, kept because the argument for why each construct *may*
> live in `Mutate` is still the argument a widening would use.
> `docs/architecture/cel.md`'s truth table is the one that is authoritative, and it prints
> the shipped state only. See deviation 79.

| Construct | Rule | Computed | Condition | **Mutate (shipped)** | Mutate (design bound) |
|---|---|---|---|---|---|
| Literal | ✓ | ✓ | ✓ | **✓** | ✓ |
| Field ref, current row | ✓ | ✓ | ✓ | **✓** | ✓ |
| Field ref, `old.`/`new.` | ✗ | ✗ | ✓ | **✓** | ✓ |
| `@user`/`@tenant` | ✓ | ✗ | ✓ | **✗ deferred** | ✓ |
| `&&` / `\|\|` / `!` | ✓ | ✓ | ✓ | **✗ deferred** | ✓ |
| Comparison | ✓ | ✓ | ✓ | **✗ deferred** | ✓ |
| `in` (role membership) | ✓ | ✗ | ✓ | **✗ deferred** | ✓ |
| `has(field)` | ✓ | ✓ | ✓ | **✗ deferred** | ✓ |
| Arithmetic | ✗ | ✓ | ✗ | **✗ deferred** | ✓ |
| Ternary | ✗ | ✓ | ✗ | **✗ deferred** | ✓ |
| `changed(field)` | ✗ | ✗ | ✓ | **✗ deferred** | ✓ |
| **Allow-listed function call** | ✗ | ✗ | ✗ | **✓** | ✓ |
| **Result type** | `Bool` | non-boolean scalar | `Bool` | **any scalar, incl. `Bool`** | any scalar, incl. `Bool` |

**Deferred, not refused.** Deny-by-default is what makes the deferral safe: an unlisted
pairing compiles in **no** profile, so nothing waits in a half-admitted state, and each
construct arrives with the fact that needs it. Measured against the tree: the only `mutate`
values that exist are `lowerAscii(new.email)` and `now()` (`crm.alvo.json:110`, `:148`), so
the four shipped rows are exactly what the descriptors need — the deferral costs nothing
that ships.

At its design bound `Mutate` is the union of `Condition`'s visibility with `Computed`'s
expressive constructs, plus a small function allow-list. The **result type** row shipped in
full and is not deferred: `Mutate` is the only profile with no constraint on the result type
at all — a `boolean` column is a legitimate `mutate` target
(`"mutate": {"is_closed": {"$cel": "new.stage == 'won'"}}`), which is exactly the case
`Computed` has to reject because a generated column cannot hold "predicate" as a value
(`CelCompiler.cs:81-87`). That example is also the one that will bring the comparison row
with it, because it needs `==`, which today's checker refuses.

**The name.** `Mutate`, not `Value`. Alvo's profiles are named after the **descriptor slot**
they compile, never after the result shape: `Rule` is `entities.*.rules.*` (plus the
`hidden`/`readOnly` masks), `Computed` is a `computed` field, `Condition` is a hook's
`condition` (`cel.md:31-44`). `Value` would name a *property of the result* — and it is a
property `Computed` shares, so a reader could not tell which slot maps to which profile.
The cost, stated: `Mutate` will also compile `entity.update.payload`
(`schema/project.schema.json:1145-1156`), which is an automation action, not a hook mutation,
so the name is slightly wider than one slot. That is accepted on `Rule`'s own precedent —
it already covers two slots (`cel.md:31-33`) — because the alternative is a fifth profile
with a byte-identical truth table, and two profiles that differ in nothing but their name
is how the `_allowedProfiles` table starts lying.

## Why `Computed` must not be widened — the load-bearing reason

`Computed` is not merely "the profile that returns values". It is the profile whose output
**must be renderable as SQL inside a column definition**, because PR6 emits
`GENERATED ALWAYS AS (…) STORED` from the compiled expression — from *compiled, validated
SQL*, never from raw descriptor text (base design `:621-625`). Everything `Computed`
forbids, it forbids for that reason and states so:

- no `@user`/`@tenant`, because *"a computed column is evaluated by the database with no
  caller context"* (`cel.md:38-40`);
- no `old.`/`new.`, because a generated column has no before-row;
- SQLite independently restricts a generated column's expression to *"constant literals and
  columns within the same row … only scalar deterministic functions … no subqueries"*, which
  the base design already records as *"independent confirmation of the Computed profile's
  allow-list"* (`:642-645`).

So widening `Computed` to admit `new.`, `@user` and `now()` would not relax a rule — it
would make the profile **unable to do its own job by construction**. A `Computed`
expression reading `@user.id` cannot be emitted into a column definition at all, and one
calling `now()` is non-deterministic, which SQLite refuses in a generated column outright.
The widening would be discovered by PR6, in the form of a profile whose expressions
sometimes compile to SQL and sometimes cannot, with no type-level way to tell which.

That is the whole argument: **`Computed` is the SQL-renderable profile, and `mutate` is
never rendered to SQL.**

## `Mutate` is interpreter-only, and that is a guarantee, not an accident

A `mutate` expression is evaluated by `CelInterpreter`
(`src/MMLib.Alvo/Expressions/Internal/CelInterpreter.cs`) against the candidate row, in
the write transaction, and its result is written as a bound parameter. It is **never**
handed to `SqlPredicateRenderer`. Three consequences worth writing down, because each is
a cost the design does not pay:

1. **No new `IFieldSqlRenderer` member.** The seam every storage driver implements
   (`cel.md:174-224`) is untouched, so no in-repo or out-of-repo driver grows a member for
   this profile — including the T-SQL fake that proves the seam is sufficient
   (`TSqlSeamTests`).
2. **No per-engine golden snapshot.** The base design's CEL→SQL-per-engine snapshots
   (`:696`) gain no rows, and the differential backend test (`:694`) is not extended,
   because there is no second backend to differ from.
3. **The two-valued rendering rule does not apply.** `cel.md:124-134`'s null collapse is a
   rule *both backends must agree on*; with one backend there is nothing to agree with.
   `Mutate` therefore inherits `CelInterpreter`'s semantics unchanged, and the moment
   somebody proposes rendering a `Mutate` expression to SQL, the two-valued fold and the
   collation caveat (`cel.md:165-171`) both come back into scope. Say so at the profile.

## The function allow-list: exactly two entries, and one of them is not a function

`CelParser.ParseCall` (`:402-417`) is hard-wired: any identifier followed by `(` other
than `changed`/`has` throws *"'x' is not a recognized function"* with the
comprehension-macro suggestion (`CelParser.cs:63`). This is `cel.md`'s deviation 7, and
`Mutate` reverses it for one profile only.

**What the shipped descriptor actually needs.** `examples/complex-crm/crm.alvo.json` uses
two: `lower(new.email)` (`:110`) and `now()` (`:148`). Nothing else in the repo needs a
function in a value position. The allow-list should be exactly what a shipped descriptor
needs and nothing more, because each entry is a permanent grammar addition that every
future engine, every future profile and every agent's training-data expectation has to
carry.

**`lower` → `lowerAscii`, adopting CEL's own name.** Conformant CEL has no `lower(x)`; its
standard library spells it `x.lowerAscii()` — a receiver-style macro. Alvo's grammar cannot
express that: `cel.md`'s deviation 8 allows *"exactly one level of `old.`/`new.`"*, so
`new.email.lowerAscii()` is two dots and structurally impossible. The honest resolution is
to adopt the **name and semantics** from the standard and deviate only on the **call shape**,
which `has(...)`/`changed(...)` already established: `lowerAscii(new.email)`. `lower(...)`
is refused, with a fix suggestion naming `lowerAscii`. The name matters beyond recognition:
`lowerAscii` means an ASCII-only fold, so the implementation is pinned to folding `A`–`Z`
and nothing else — **not** `ToLowerInvariant()`, which folds a long tail of
non-ASCII code points. A culture- or Unicode-sensitive fold on a *stored* value is a
permanently wrong row, which is the same class of defect `UnhonouredFeatures` refuses
`default` for (`UnhonouredFeatures.cs:68-75`).

**Correction — the example this paragraph originally used was wrong, and it made the
pinning fact vacuous.** It named `İ` (U+0130) as the code point `ToLowerInvariant()` folds
to two code points. Measured on .NET 10 here: `"İ".ToLowerInvariant()` is **unchanged**,
length 1 — .NET's *invariant* casing deliberately excludes the dotted capital I, and only a
full Unicode case mapping produces `i̇`. The consequence is worse than a wrong sentence: the
implementation fact written from it passed under its own `ToLowerInvariant()` mutation, so it
proved nothing. Code points `ToLowerInvariant()` demonstrably *does* fold, and which the fact
now uses: `Ž` (U+017D→U+017E), `Ä` (U+00C4→U+00E4), `ẞ` (U+1E9E→ß), `Σ` (U+03A3→σ). `İ` is
kept in the sample for what it legitimately documents — a non-ASCII code point an ASCII fold
must leave alone.

**`now()` is safe in-transaction only under one rule: it is not a clock read.**

- It must never be rendered to SQL. Postgres's `now()` returns the *transaction start*
  time; SQLite's `CURRENT_TIMESTAMP` has second precision and returns a string. Two engines
  would answer differently, which §0 principle 3 forbids the core to allow.
- The instant comes from **`TimeProvider`**, which is already the repo's clock port —
  `AlvoAuditStamp` states the rule (*"The instant comes from a `TimeProvider`, never from
  `DateTimeOffset.UtcNow` inline. An inline clock cannot be asserted on"*,
  `src/MMLib.Alvo.Abstractions/Schema/AlvoAuditStamp.cs:24-27`), `EfAlvoData` takes one
  (`:51,:61`), `AlvoEfCoreProvider` registers `TimeProvider.System` (`:78`). No new port.
- **One write, one instant.** `data-path.md:354` is titled *"Every timestamp is one
  instant"* and normalises every `datetime` through `StoredInstant` on every path,
  explicitly naming *"PR5's outbox"* as a future write path that must honour it (`:386`).
  So `now()` resolves to the **same** `DateTimeOffset` the write's audit stamp uses, bound
  once for the whole write and reused by every evaluation in it. `now()` twice in one write
  returns the same value; two hooks on one write agree; the outbox row's `time` agrees with
  the row's `updated_at`. The value goes through `StoredInstant` like every other timestamp.
- That rule is what makes `Mutate` **referentially transparent within a write**, which is
  what makes it testable (a `FakeTimeProvider` pins it) and what makes it safe to evaluate
  inside a transaction that may be retried: a retry re-stamps, exactly as
  `EfAlvoData.AuthorizedCandidate` already does per attempt (`:188-192`).

**`@now` was considered and rejected.** CEL itself has no `now`; deployments bind a
timestamp *variable* (Cloud IAM's `request.time`), and Alvo already owns an `@`-context
syntax (`cel.md:234-238`) where a memberless value would fit. Rejected on two counts: it
widens the closed `@`-context set that the lexer refuses everything else against, and it
would be the first memberless context reference in a set whose three members are all
`@name.member`. Since `Mutate` needs the call machinery for `lowerAscii` regardless,
`now()` costs nothing extra — and it keeps the shipped example's spelling, which is one
fewer edit.

**What the allow-list deliberately does not contain**, and where the pressure actually is:
`upper`, `trim`, `size`, `concat`, string indexing — no shipped descriptor uses any of
them; add each on demand with a named issue. The real unmet need the register found is
**date arithmetic** for the F6 demo rule, and it does not land here: `new.stk_valid_until <
now() + duration('720h')` is a **`Condition`** expression, and `Condition` allows no
arithmetic at all (`cel.md:27`). Widening `Condition` is a separate decision with a
separate blast radius (it is a security-core profile that PR1's adversarial suite is
written against), and it is not what "STK expires in 30 days" is blocked on anyway — see
Decision 2's last section.

## This is a public API change, and it moves a baseline

`CelProfile` is public and lives in `MMLib.Alvo.Abstractions`
(`test/MMLib.Alvo.Abstractions.Tests/PublicApi.MMLib.Alvo.Abstractions.verified.txt:813-818`).
`CelNode` is public in the same assembly (`:809-811`), so the new call node is public too.
Therefore:

- **Append `Mutate = 3`.** Never insert it before `Condition` — the values are in the
  approved baseline (`Rule = 0, Computed = 1, Condition = 2`), and renumbering an enum whose
  members are published is a silent break for anything that persisted or logged the number.
- The change moves `PublicApi.MMLib.Alvo.Abstractions.verified.txt`, so it needs its own
  public-API approval and it trips `.claude/hooks/turn-review-gate`, which blocks the turn
  until `alvo-snapshot-judge` has reviewed the moved baseline (base design `:718-723`).
  Expected, not a surprise.
- `CelValueType` needs **no** new member: `String` and `Timestamp` already exist
  (`PublicApi.MMLib.Alvo.Abstractions.verified.txt:845-856`), which is what `lowerAscii` and
  `now` return.
- `CelConstructKind` and `_allowedProfiles` are internal to `CelTypeChecker`
  (`src/MMLib.Alvo/Expressions/Internal/CelTypeChecker.cs:50,:76`), so the allow-list table
  itself costs no surface.

## The trap: `complex-crm` ships expressions that do not compile, and a test hides it

The register found this and it is worse than reported.

`examples/complex-crm/crm.alvo.json` is the only example with hooks, and it is the only one
carrying a `NOT-RUNNABLE.md` marker. Its hook block contains **four** expressions that
throw today, not two:

| Location | Expression | Refused by |
|---|---|---|
| `crm.alvo.json:110` | `{"$cel": "lower(new.email)"}` | `CelParser.cs:404-410` — not a recognized function |
| `crm.alvo.json:143` | `"old.stage in ['won', 'lost'] && !('manager' in @user.roles)"` | `CelParser.cs:278-281` — **"Alvo has no list literals"** |
| `crm.alvo.json:147` | `"changed(stage) && new.stage in ['won', 'lost']"` | `CelParser.cs:278-281` — same |
| `crm.alvo.json:148` | `{"$cel": "now()"}` | `CelParser.cs:404-410` |

Only the first and last are the `mutate`/allow-list problem. The middle two are
**`Condition`** expressions using a list literal on the right of `in`, which `cel.md`'s
deviation 6 refuses outright — so lifting the `beforeUpdate` refusal breaks that example
even with Decision 1 fully implemented.

All four are invisible because `UnhonouredFeatures.HookPoints()` refuses the hook point
before anything is compiled (`UnhonouredFeatures.cs:126-134`). The day PR5 deletes a hook
entry from that table, the example's refusal reason silently changes from a structured
unhonoured-feature error to a CEL syntax error — and
`DescriptorToSchemaMapperTests.Every_example_marked_not_runnable_really_is_refused`
(`test/MMLib.Alvo.Tests/Descriptor/DescriptorToSchemaMapperTests.cs:243-252`) **keeps
passing**, because it asserts only `Should.Throw<InvalidDataException>` — any
`InvalidDataException`, for any reason. The test's own XML doc claims it is what forces the
marker to shrink (`:230-238`); with a syntax error standing in for the feature refusal, it
forces nothing.

**What must be done, both halves:**

1. **Fix the example in the same PR that lifts a hook refusal.** `lower` → `lowerAscii`;
   `in ['won','lost']` → the equality chain the parser's own fix suggestion names
   (`CelParser.cs:281`: *"Use an equality chain instead, e.g. status == 'draft' || status ==
   'review'"*) — `old.stage == 'won' || old.stage == 'lost'`; `now()` stays. An example is
   the descriptor an agent copies, so a shipped example that cannot compile is worse than
   no example.
2. **Make the test assert the refusal *reason*, not merely that a refusal happened.** The
   fact must read: every example marked not-runnable is refused **by an
   `UnhonouredFeatures` entry**, named. Mechanically that means the mapper's refusal
   carries the entry's `Path` (`UnhonouredFeature<T>.Path`, `UnhonouredFeatures.cs:181-185`)
   in a form the test can read, and the test asserts the pointer — the same discipline
   `UnhonouredSubsystemsTests` already applies by asserting *which blocks the warning names*
   rather than that a warning was logged (`UnhonouredSubsystems.cs:115-120`). Then a syntax
   error can never stand in for a feature refusal, in this PR or in PR6's.

Two adjacent latent traps, recorded so the next reader does not rediscover them:

- **PR6 inherits the same shape.** `crm.alvo.json:82` carries
  `"rollup": {…, "where": "stage in ['lead', 'offer']"}` — another list literal, refused
  today only by the `rollup` entry in `UnhonouredFeatures`.
- **`access` is a latent trap owned by nobody.** `crm.alvo.json:16-20` holds three
  `$defs/cel`-typed strings (`schema:149-167`) using `@user.role` (singular — replaced by
  `@user.roles`, base design deviation 3), a list literal, and
  `@user.email.endsWith('@firma.sk')` (a method call *and* two dots). Nothing in `src/`
  reads `descriptor.Access`, so none of them is compiled; whoever honours `access` inherits
  all four defects at once. Not PR5's, but it should be written down once.

## Correcting a stale justification in the file PR5 edits

`UnhonouredFeatures.cs:114-119` justifies the per-phase wording with *"the clearest case in
this repo's own examples is `simple-tasks`' `beforeUpdate`, which sets `completed_at` when a
task is marked done"*. `examples/simple-tasks/tasks.alvo.json` declares **no `hooks` block
and no `completed_at` field** — verified: `complex-crm` is the only example in the tree with
hooks, and `completed_at` appears nowhere outside that XML comment and its build artefacts.
The reasoning is sound; the example is fictional. PR5 is the PR that edits this file, so it
is the PR that fixes the citation (the real case is `deals.beforeUpdate` setting `closed_at`,
`crm.alvo.json:148`).

---

# Decision 2 — JSONata is deferred entirely; only `{{…}}` templates are honoured

## The hole

`$defs/jsonata` is a frozen first-class slot on four action types —
`webhook.payload` (`schema:1086`), `email.data` (`:1108`), `function.input` (`:1124`),
`http.call.payload` (`:1186`) — and `docs/PLAN.md:101-104` elevates *"CEL for conditions,
JSONata for transforms … JSONata is Turing-complete and **never** runs in-transaction"* to
a project invariant. There is no mature .NET JSONata implementation, and the analysis —
which names a .NET library for every other building block — names none for this one.

## The decision

PR5 honours **only** the `{{…}}` template sugar, and **refuses a raw JSONata expression at
apply time** with a named "not yet honoured" error, through the existing
`UnhonouredFeatures`/`UnhonouredSubsystems` mechanism. No new mechanism, no partial
evaluator, no vendored subset.

**Why refusal is right and a subset is not.** `CLAUDE.md` states it as a rule, not a
preference: *"Alvo deliberately adopts known specs so agents recognize them from training
data; inventing a variant of a standard is a defect, not a shortcut."* A hand-rolled
"JSONata-like" evaluator is precisely that variant. Its failure mode is the expensive one:
an agent writes JSONata it knows from training data, Alvo accepts the 80% it implements and
**silently produces a different payload** for the rest — `$merge`, `$map`, `^(…)` ordering,
predicate contexts, `$$` root scope. A webhook delivered with a wrong body is indistinguishable
from a consumer bug, and the descriptor is meant to be the durable artifact that outlives
any one build (`UnhonouredSubsystems.cs:16-18`). A refusal that names the feature and the
issue costs the author one line and loses nothing they can observe. This is exactly the
line `UnhonouredFeatures`/`UnhonouredSubsystems` already draw, and it is why the mechanism
is reused rather than replaced.

**Where each refusal lands, and why the two tables split the way they do.** The existing
rule is: `UnhonouredFeatures` **errors** on what *silently produces wrong data*;
`UnhonouredSubsystems` **warns** on what an author *observes the absence of*
(`UnhonouredSubsystems.cs:12-19`). Applied here:

- A raw JSONata expression in a `$defs/jsonata` slot is an **error**, in
  `UnhonouredFeatures`. Not because the absent transform is unobservable — the *action*
  still runs — but because it runs **with the wrong payload**: an author who wrote
  `webhook.payload` and gets the canonical envelope instead has a delivery that succeeded
  and a body that is not what they declared. That is the `default` case
  (`UnhonouredFeatures.cs:68-75`), not the `webhooks` case.
- `automation`, `templates`, `webhooks`, `functions` keep their existing
  `UnhonouredSubsystems` **warning** entries (`:79-95`) until the PR that honours each
  removes them, exactly as designed.

## Telling a template from a raw expression — Alvo's rule, not the schema's

`$defs/jsonata` is typed `string` (`schema:398-403`) and its own description says
*"`{{...}}` templates are syntactic sugar."* So the schema cannot distinguish them; the
apply path must, and the rule has to be written down because both plausible naive rules
fail open.

> A string in a `$defs/jsonata`-typed slot is a **template** iff it consists only of literal
> text containing no bare `{` or `}` and one or more well-formed, non-nested `{{ … }}`
> placeholders — i.e. it matches `^(?:[^{}]|\{\{[^{}]+\}\})*$` **and** contains at least one
> placeholder. Anything else is raw JSONata and is refused by name.

Both halves earn their place against the shipped example:

- *"Contains `{{`"* would classify `crm.alvo.json`'s
  `"payload": "{\"companyIds\": records.id}"` (the `bulk-import-index` rule) as literal
  text and deliver the JSONata source as the body. The no-bare-brace clause catches it, and
  `"$merge([new, {\"source\": \"alvo\"}])"` (the `deal-won` rule) with it.
- The **at-least-one-placeholder** clause catches the fail-open remainder: a brace-free
  JSONata expression such as `"records.id"` would otherwise be a valid placeholder-free
  template and deliver the literal string `records.id`. There is no reason to declare a
  *transform* that is a constant, so in a `$defs/jsonata` slot a placeholder-free string is
  refused too.
- The asymmetry that follows is deliberate and comes from the schema's own typing: in the
  slots that are **plain strings with `{{…}}` sugar** — `email.to` (`:1104`),
  `entity.update.recordId` (`:1141`), `templates.subject`/`body` (`:1098-1105`), and string
  values inside `entity.update.payload` (`:1150`) — a placeholder-free string *is* a
  legitimate literal (a hard-coded address), so it is accepted.

## What the template engine must support, and against what it resolves

The shipped example fixes the minimum: `{{new.title}}`, `{{new.amount}}`, `{{new.number}}`
in templates (`crm.alvo.json:48-52`) and `{{@user.email}}` in `email.to` (`:221`). So the
template root must expose, at least:

| Placeholder root | Resolves to |
|---|---|
| `new.<field>` | `data.record` on the envelope |
| `old.<field>` | `data.old_record` |
| `event.id` / `event.type` / `event.time` / `event.subject` | the envelope's own attributes |
| `@user.id`, `@tenant.id` | the provenance the envelope carries |

**Two rulings the engine needs, and both follow from principles already in force:**

1. **Templates are validated at apply time against the schema, not at delivery time
   against the payload.** A placeholder naming a field the entity does not declare is a
   structured apply-time error with a "did you mean" suggestion — the same property #20's
   DoD already demands of rules (*"a rule referencing a nonexistent column **fails at save,
   not at request time**"*, base design `:104-106`), and the same machinery
   `PolicyCatalogBuilder` already uses for unknown fields and role literals
   (`cel.md:111-122`).
2. **An unresolvable placeholder never renders to empty.** `{{@user.email}}` is
   unresolvable: `AlvoContext` carries `User`, `Roles` and `Tenant` and no email address
   (`src/MMLib.Alvo.Abstractions/Identity/AlvoContext.cs:17-34`), and the closed
   `@`-context set is exactly `@user.id`, `@user.roles`, `@tenant.id` (`cel.md:234-238`).
   Rendering it to `""` yields `To: ""` — a mail failure that looks like a broken SMTP
   server, which is the *same misattribution* `UnhonouredSubsystems` exists to prevent
   (*"a webhook that never fires looks exactly like a webhook whose endpoint is down"*,
   `:21-24`). So it is refused at apply, naming the resolvable roots. `crm.alvo.json:221`
   is therefore a fifth expression that example must fix — recipient resolution needs
   either a field on the record (`{{new.owner_email}}`) or an identity claim Alvo does not
   yet carry (`#37` tracks `@user.claims`).

## The in-transaction ban, restated for PR5

`alvo-specifikacia.md:300` requires the JSONata evaluator's *"zákaz in-transaction
(testom)"* — prove the ban with a test — and `PLAN.md:101-104` makes it an invariant. In
PR5 the invariant is **trivially preserved, because JSONata does not run at all**, and that
changes what the test can honestly assert. A test named "JSONata does not run
in-transaction" would be vacuously green and would read, forever after, as though the ban
were enforced.

So PR5's fact is an **absence** test, named as one and stated as one: no JSONata evaluator
exists, and every `$defs/jsonata`-typed slot carrying a raw expression is refused at apply
by a named `UnhonouredFeatures` entry. The real ban test is owed by the PR that introduces
an evaluator, and it must be written **then** — as an architectural fact (nothing in the
in-transaction path can reach the evaluator), not as a behavioural one, because a
behavioural test can only sample the paths someone thought of. Recorded as a deviation so
the obligation is not lost with this document.

## Is the F6 demo rule reachable? — no, and JSONata is not why

`alvo-specifikacia.md:330` is explicit: *"Demo pravidlo „STK končí o 30 dní → email" musí
byť postaviteľné už z F4"* — the demo rule must be buildable from this milestone. It is
not, and the reason is a frozen shape, not a missing transform language:

- The rule needs a **scheduled scan**: find rows whose inspection expiry is 30 days out.
- A `schedule` trigger carries **no record**. The frozen `automationRule`
  (`schema:963-1033`) is `{trigger} + {condition?} + {delivery?} + {actions[≥1]}`, with no
  entity binding, no query and no scan concept on the schedule branch.
- `delivery` is defined as *"perItem = one execution per affected row"* (`schema:1019`);
  a cron tick affects no rows, so `perItem` has nothing to iterate.
- `condition` is `$defs/cel` — a row predicate with no row to evaluate against.
- The **only** frozen escape is the `function` action, and `complex-crm`'s own
  `stale-deal-reminder` rule takes exactly that escape, with the author's own comment:
  *"delegates the per-record scan to a function"*. That is independent confirmation that
  the declarative form is insufficient — the example's author hit the same wall.
- `function` is out of PR5 scope. And **`alvo-specifikacia.md:330` lists `function` in the
  F4 action set**, in the very sentence that requires the STK rule to be buildable. The
  base design silently drops it from the action set (`:609-611` names only `webhook` /
  `email` / `entity.update`), which is not merely an undeclared deviation — it removes the
  mechanism the same source line needs.

**Template sugar is sufficient for everything else the email leg needs** — a template
subject/body with `{{new.…}}` placeholders and a recipient from a record field — so
Decision 2 does not stand in the way. The DoD line *"ECA + cron + email end to end"* is
satisfiable in PR5 (event-triggered rule → email; cron-triggered rule → email to a
descriptor-declared address). The **STK rule specifically** is not, and it needs either
`function` or a new declarative scheduled-scan shape — the latter being a **schema** change
and therefore a decision above PR5. Recorded as a deviation and recommended as its own
issue now, rather than discovered in F6.

---

# CloudEvents 1.0.2 conformance

Design against **v1.0.2** (there is no 1.0.3; `main` is `1.0.3-wip`). The wire
`specversion` value is `"1.0"`.

**The three rules that decide every row below:**

1. **Names.** *"CloudEvents attribute names MUST consist of lower-case letters ('a' to 'z')
   or digits ('0' to '9') from the ASCII character set. Attribute names SHOULD be
   descriptive and terse and SHOULD NOT exceed 20 characters in length."*
   ([spec.md v1.0.2:173-175](https://github.com/cloudevents/spec/blob/v1.0.2/cloudevents/spec.md#attribute-naming-convention)).
   Extensions *"MUST follow the same naming convention and use the same type system as
   standard attributes"* (`:433-435`). No `_`, no `-`, no `.`, no uppercase — and the
   20-character advisory bites some candidate names, which the register did not note.
2. **Types.** Context attribute values are limited to **seven** abstract types — `Boolean`,
   `Integer` (int32), `String`, `Binary`, `URI`, `URI-reference`, `Timestamp` — and *"All
   context attribute values MUST be of one of the types listed above"* (`:179-217`). **There
   is no map, array or object type.** Extensions are serialized *"according to binding rules
   like standard attributes"* (`:439-440`), i.e. as **flat top-level** JSON members; a
   nested `"extensions": { … }` wrapper is non-conformant.
3. **Size.** *"Intermediaries MUST forward events of a size of 64 KByte or less"* (`:510-512`).

| Candidate (as the base design names it) | Legal? | Ruling |
|---|---|---|
| `payload_version` (`:548`) | ❌ | `_` is outside `[a-z0-9]`. → `payloadversion` (14 chars, within the advisory). |
| `chain-depth` / `provenance_depth` (`:563`) | ❌ | `-`/`_` illegal. → `chaindepth`. Type `Integer`. |
| the actor (`:550`) | ⚠ | `actor` is legal, but unregistered and vague. Prefer **`authtype`** + **`authid`** — the community's Auth Context names, which also distinguish "a user did this" from "an automation rule did this", which §3.3's *"as system / as the originator"* requirement needs. |
| the correlation id (`:551`) | ✅ | **`correlationid`**, plus **`causationid`** for the immediate cause — the chain the §2.12 end-to-end trace needs is a correlation/causation pair, not one id. |
| the per-entity-key partition | ✅ | **`partitionkey`** — the **registered** Partitioning name, `String`. Use it for the outbox `partition_key` column too, so the column and the attribute cannot drift. |
| the outbox `sequence` column (`:561`) | ✅ *if surfaced* | **`sequence`** + **`sequencetype`** are the registered Sequence names. If `sequence` is exposed at all it must use them — and note the registered semantics: a **`String`**, lexicographically comparable, scoped **per `source`**. The outbox's monotonic integer is not that; see R2 below before publishing it. |
| `record` (`:557`) | ❌ as an attribute | An object; no map type exists. Lives in **`data.record`**, where JSON is unrestricted and `snake_case` is fine. |
| `old_record` (`:557`) | ❌ as an attribute | Same, twice over (object *and* illegal name). → **`data.old_record`**. |
| the changed-columns list (`:557`) | ❌ as an attribute | An array; no array type. → **`data.changed`** (or `data.changed_columns`). |
| a wide row exceeding 64 KB | — | The registered escape is **`dataref`** (Dataref / claim-check): a `URI-reference` to the payload. `data` and `dataref` MAY coexist. Relevant because `record` + `old_record` on a wide row can exceed the forwarding floor by itself. |

**A provenance correction the register got slightly wrong, and it matters for citation.**
In **v1.0.2**, `documented-extensions.md` lists exactly **five** known extensions —
Dataref, Distributed Tracing, Partitioning, Sampling, Sequence. **Auth Context**
(`authtype`/`authid`/`authclaims`) and **Correlation** (`correlationid`/`causationid`) are
*not* in the v1.0.2 registry; they exist as
[`extensions/authcontext.md`](https://github.com/cloudevents/spec/blob/main/cloudevents/extensions/authcontext.md)
and [`extensions/correlation.md`](https://github.com/cloudevents/spec/blob/main/cloudevents/extensions/correlation.md)
on `main` (post-1.0.2). The recommendation stands unchanged — those are the community's
names, they satisfy the naming rule, and adopting them is strictly better than inventing
`actor` — but the design must say *where they come from*, or a reader checking the v1.0.2
registry finds nothing and concludes the names were invented.

**A free conformance test.** `CloudNative.CloudEvents` 2.9.0 (Apache-2.0, `net10.0`)
enforces the character set at runtime in `CloudEventAttribute.CreateExtension`, so
`CreateExtension("payload_version", …)` throws. Note the constraint this cannot resolve:
`Abstractions` may take **no** new external dependency (`package-boundary.md:96-103`), so
the envelope type is hand-written in `Abstractions` and the SDK, if used at all, is a
**core-side mapper** and a test-time conformance oracle — never the envelope itself
(register R13).

---

# The in-flight startup PR: the dispatcher must gate on state, not on ordering

.NET 10 changed `BackgroundService`: *"Starting in .NET 10, all of `ExecuteAsync` runs on a
background thread, and no part of it blocks other services from starting"* — previously the
synchronous portion before the first `await` ran on the main thread during startup
([BackgroundService runs all of ExecuteAsync as a Task](https://learn.microsoft.com/dotnet/core/compatibility/extensions/10.0/backgroundservice-executeasync-task)).

**Consequence for this design:** *"the dispatcher must not run before the schema is primed"*
**cannot be expressed by ordering**, and `await Task.Yield()` as a first line is dead code
on `net10.0`. None of the four workarounds that doc recommends substitutes for a readiness
signal, because the thing being waited for lives in a *different* service:

1. constructor work cannot await priming;
2. overriding `StartAsync` restores synchronous-during-startup behaviour but only relative
   to services registered *after* it — registration-order coupling is exactly what must not
   be relied on;
3. `IHostedLifecycleService` gives phases (`Starting`/`Started`/…), not a signal that
   another component finished;
4. a hand-rolled `IHostedService` inherits the same problem.

So the dispatcher **awaits `AlvoBootState`** — the readiness state the in-flight startup PR
introduces. Note plainly: `AlvoBootState` does **not exist in this worktree** (verified: no
match in `src/`, `test/` or `docs/`), so this is a forward reference. The two PRs compose,
but **PR5 cannot merge before the startup PR**, and the gate must be an explicit await on
the state, never an assumption about `AddHostedService` order.

**Two more hosting facts that are design-level, not implementation detail:**

- `HostOptions.BackgroundServiceExceptionBehavior` defaults to `StopHost`, so an exception
  escaping the dispatcher's `ExecuteAsync` **takes down a host that is serving HTTP
  traffic** — one poison event kills the API. From .NET 11, `RunAsync`/`StopAsync` also
  *throw* and the process exits non-zero
  ([extensions/11](https://learn.microsoft.com/dotnet/core/compatibility/extensions/11/ihost-runasync-stopasync-throw-backgroundservice-failure)),
  with the documented recommended action being "do nothing" — a failing app *should* fail.
  The dispatcher must therefore contain its own failures per batch and per event and never
  let one escape the loop; a poison event is a DLQ-shaped concern (7.1) whose PR5 stand-in
  is an attempt counter plus a loud log, not a host exit.
- The host **blocks in `StopAsync` waiting for `ExecuteAsync`**, with a 30 s
  `ShutdownTimeout`, so the claim/dispatch loop must observe its cancellation token
  promptly — and `ServicesStartConcurrently` stays at its default `false`.

---

# Recommendations that need the maintainer's ratification

## R3 — the ordering guarantee: one dispatcher in F3, partitioning in F7

**Recommendation (a): exactly one dispatcher in F3, with `partition_key` written from day
one.** Ratification needed.

The base design states *"per-entity-key ordering, partitioned by primary key"* as a
*documented guarantee, not an accident* (`:574-577`), and the claim query it specifies —
`FOR UPDATE SKIP LOCKED` (`:565-567`) — cannot deliver it: `SKIP LOCKED` skips the **row**,
not the key, so two dispatchers can concurrently claim two rows of the same entity key and
deliver them in either order. Postgres's own Caution note adds that `ORDER BY` is applied
*before* locking, so the ordered claim is not even ordered among the rows it does return.
The analysis hedged exactly here — *"garantuj per-entity-key ordering, **ak sa dá**
(partition podľa PK), a dokumentuj to"* (`baas-analyza.md:656`, "if possible") — and the
design dropped the hedge.

Every mature system solves it one of three ways: hash the key → partition → one worker per
partition (Kafka, Debezium); block the key while an item is in flight (SQS FIFO); or lock
the key (Service Bus sessions, Postgres advisory locks). **A single dispatcher satisfies
the guarantee trivially**, and it is the honest choice for a milestone whose own analysis
scopes the MVP to a single node. Building the partition scheme now means building it
without the load that would validate it, and inheriting a claim protocol nobody has
stressed.

What the recommendation costs and what must be written down:

- **Restore the hedge as documentation, precisely:** per-entity-key ordering holds *while
  exactly one dispatcher runs*; there is no global ordering (§3.3 calls it expensive and
  brittle); delivery is at-least-once regardless.
- **State the operational constraint, because PR5 cannot enforce it.** With no distributed
  lock (see R14 below) the dispatcher cannot detect a second instance. Two replicas of the
  standalone image break the ordering guarantee silently. That is a documented deployment
  constraint in F3, and it is the same gap cron has.
- **Write `partition_key` into `alvo.outbox` from the first migration**, populated from the
  entity key, even though nothing reads it in F3. This is the cheap forward-compatibility
  move: F7's partitioned claim becomes additive instead of a migration of a shipped table.
  Name the column after the registered CloudEvents `partitionkey` so the column and the
  envelope attribute cannot drift.
- **File the partitioning work as an F7 issue now**, referencing this section.

Adjacent, and inherited from the register rather than re-derived — the ordering column is
also where two traps live, and both are settled by the same rule: **the claim predicate is
`claimed_at IS NULL` / `dispatched_at IS NULL`, never a high-water mark on `sequence`.**
PostgreSQL sequences commit out of order (a transaction can take 100 and commit after
another took 101 and committed), so a "processed up to N" watermark drops a row silently
(R2); and under `UseRelationalNulls()` — on in both drivers, and *"PR5 is the first PR its
cost binds"* (`data-path.md:121-145`) — those predicates must be written the way SQL reads
them. Having `sequence` in the column list at all invites the wrong use, which is a reason
to keep it out of the envelope unless it is spelled as the registered Sequence extension.

## R8 — custom application events: C#-subscriber-only in F3

**Recommendation: `Publish` reaches C# subscribers only in F3; the schema change is designed
once, as its own issue.** Ratification needed.

`$defs/eventPattern` is frozen to
`^(entity|auth|storage)\.([a-z][a-z0-9_]*|\*)\.([a-z]+|\*)(\.batch)?$`
(`schema/project.schema.json:409-419`), so **no descriptor-declared automation rule can
subscribe to `Publish("order.approved", …)`** — the name does not match the pattern at all.
Yet custom application events are in the design *"included"* (`:609-612`) precisely because
§3.2 names their absence as the reason Directus users listen to generic UPDATE events and
filter thousands of false triggers.

**Say the narrowing plainly rather than shipping the claim.** With C#-subscriber-only,
Alvo's advantage over the cited Directus defect in F3 is available **only to a host that
writes C#** — which is not the audience the defect was cited on behalf of. The declarative
half, which is the half that matters for the comparison, is not delivered.

Why not widen the pattern in PR5: it is a frozen artifact, and the register found the same
grammar blocks a second case — segment 3 is `[a-z]+`, so `auth.user.password_changed` is
unrepresentable too. The right fix is therefore a **designed namespace**, once (a `custom.`
prefix or a fourth alternation, plus `[a-z][a-z0-9_]*` on the third segment), not a one-off
prefix bolted on under PR5's schedule. File it with the grammar written out.

**One security ruling that PR5 must ship regardless of the above:** `Publish` must
**refuse** a name matching `^(entity|auth|storage)\.`. Without it a host can mint an event
indistinguishable from a real data change, and every descriptor rule and after-hook
subscribing to `entity.orders.updated` would fire on a forged one — with a `partitionkey`
and provenance nobody wrote a row for.

Related and unresolved, recorded here because it belongs to the same subscription
machinery: **wildcard subscribe (`entity.orders.*`) is a *hard* spec guarantee**
(`alvo-specifikacia.md:141`) with no matcher and no security story in the design, while
`baas-analyza.md:657` requires tenant isolation of rules (*"tenant vidí a triggeruje len
svoje"*). A wildcard makes cross-tenant fan-out the default failure mode. **Recommendation:**
implement the matcher (it is trivial) but scope every subscription to the envelope's tenant,
and make the cross-tenant case a named adversarial fact — not a paragraph. If that cannot
be done in the first PR, refuse `*` at apply rather than shipping an unscoped matcher.

## The PR split — endorse PR5a / PR5b, with one boundary moved

**Recommendation: endorse the split, and move the JSONata classifier plus the
execution-log/counter and transition criteria into PR5a.** Ratification needed.

The register's split is right in its core judgement: R6 and R10 are **design** work, and
PR5 as scoped cannot start until they are settled. The refinement concerns exactly where
after-hooks sit, because the register's PR5a includes after-hooks whose action set is
`webhook` + `email` — and `webhook.payload` and `email.data` are *the JSONata slots*. So
PR5a needs Decision 2's machinery whether or not it "does JSONata".

| | Content | Why here |
|---|---|---|
| **PR5a — the event backbone** | CloudEvents envelope (conformant, per the table above) · `alvo.outbox` incl. `partition_key` · the single dispatcher gated on `AlvoBootState` · **after-hooks** · the `{{…}}` template engine + the raw-JSONata refusal · the crash and 10k chaos tests · both engines | Everything whose correctness is about *durability and delivery*. The template/refusal classifier is the smallest honest unit that lets an after-hook's `webhook`/`email` action exist at all. Decides R1–R5, R11–R13. |
| **PR5b — before-hooks and automation** | the **`Mutate` profile** (Decision 1) · the before-hook pipeline (R7) · the budget-overrun rollback · automation: `event` + `schedule` triggers, `entity.update` · the `function` / `http.call` named refusals · cron's no-lock deviation | Everything that needs Decision 1 and touches the in-transaction write path. |

**Two criteria the register put in 5b and that belong in 5a:**

- **The transition test** (`changed(status) && new.status == 'approved'` fires exactly once,
  `baas-analyza.md:677`) is a `Condition`-profile expression on an **after-hook**, which 5a
  ships. Deferring it would leave 5a's whole point — that events fire correctly — unproven.
- **The execution-log / counter criterion** (a filtered-out event produces no execution log,
  only a counter) is about the **subscription step**, and after-hook conditions are the
  first predicate evaluated there. The base design's own argument is that this is *"nearly
  free if designed in and awkward to retrofit"* (`:585-592`); putting it in 5b is the
  retrofit.

**The ordering dependency, named:** PR5a cannot merge before the startup PR, because it
gates on `AlvoBootState`. If that PR slips, 5a slips — this is not a split that can be
reordered around it.

**Both are security-core PRs** by the base design's own rule (`:747-750`): the
`alvo-security-core-review` checklist plus a security review, and a `workflow_dispatch`
mutation run before each merge, since mutation is post-merge on `main`.

---

# What PR5 does not do, and why

Written out because the base design over-claims in several places, and an over-claim in a
design is more expensive than a gap.

1. **No global ordering, and per-entity-key ordering only under one dispatcher.** The design
   states the guarantee flatly (`:574-577`); it is conditional. Corrected above (R3).
2. **No JSONata.** Four frozen action slots refuse a raw expression by name. The invariant
   *"JSONata never runs in-transaction"* holds vacuously and its test is an absence test
   (Decision 2).
3. **No `function` action and no `http.call` action.** Both are frozen into `$defs/action`
   (`schema:1126,:1156`) and neither is implemented; both get named refusals. `function`'s
   absence is a **scope** defect, not just an undeclared deviation, because
   `alvo-specifikacia.md:330` lists it in the F4 action set *and* makes it the mechanism the
   STK demo rule needs.
4. **The F6 demo rule "STK ends in 30 days → email" is not reachable from the descriptor.**
   The frozen `automationRule` has no scheduled-scan shape (Decision 2's last section). The
   generic DoD line "ECA + cron + email end to end" *is* satisfiable; the named demo rule is
   not.
5. **A before-hook cannot roll back a create as `EfAlvoData` is built today.** The DoD
   requires *"a before-hook that exceeds its budget rolls the transaction back cleanly with
   an RFC 7807 error"* (base design `:118-120`), but on the create path
   `AuthorizedCandidate` runs at `EfAlvoData.cs:175` and `BeginTransactionAsync` at `:177`
   — a hook placed where the candidate is built has **nothing to roll back**. This is a
   correction to the design, not a deferral: the pipeline must run inside `InsertAsync`,
   after the audit stamp, on both the create and the update faces. The update face has the
   mirror-image constraint — `old`/`changed()` need the in-transaction locked pre-image, so
   a `mutate` must apply *inside* the write, and re-running `WritePayloadGuard` over the
   mutated payload would reject a hook legitimately setting a `readOnly` field, because that
   guard is written for **callers**. Security-relevant and currently unanswered; PR5b owes an
   explicit ruling.
6. **The crash test does not exercise a real crash.** `AlvoHostWorld` runs in-process over
   `TestServer` and a graceful stop calls `StopAsync`, so "kill between commit and publish"
   is simulated, not performed. Either build a child-process harness or state in the test's
   own name and remarks what it does not prove. The register also notes the second half of
   the criterion the issue body drops: *"kill uprostred akcie → akcia sa zopakuje"*
   (`baas-analyza.md:676`) — kill mid-action and the action repeats.
7. **"Email end to end" is proved against the console dev provider only.** The compose stack
   is `alvo` + `postgres` (base design deviations 40 and 50); there is no mail service, so
   an SMTP leg needs one added plus a TeaPie leg.
8. **Cron has no distributed lock**, against `baas-analyza.md:819`'s explicit criterion
   (*"cron job sa v 3-instance nasadení spustí práve raz"*). Recorded as a deviation, not
   discovered later. Note the register attributed this criterion to spec §7.3; it is in the
   **analysis**, in the scheduling component, which also names the .NET blocks (Postgres
   advisory locks / Redis).
9. **The idempotency table is the wrong place to dedupe events at volume.** Data actions'
   idempotency keys are *"derived from the event id"* (`:577-578`), and nothing prunes that
   table (#115, `data-path.md:1347-1378`), so it would grow with **event** volume rather
   than with keyed creates. Also `AlvoIdempotency` is honoured on create only and an
   anonymous actor cannot hold a key — a dispatcher acting as `AlvoContext.System(tenant)`
   must therefore pass a real context, never `Anonymous`.

---

# Deviations

Continuing the base design's numbering. 1–51 are its own; 52–57 are the in-flight startup
PR's. Entries marked **[unratified]** depend on a recommendation above.

58. **A fourth CEL profile (`Mutate`) is added rather than widening `Computed`.**
    `docs/architecture/cel.md` is titled around *three* profiles and its truth table has
    three columns; this adds a fourth. The alternative — widening `Computed` — is rejected
    because `Computed` must stay same-row and SQL-renderable for PR6 to emit
    `GENERATED ALWAYS AS (…) STORED` from it (base design `:621-625`), and admitting
    `new.`/`@user`/`now()` would make the profile unable to do that **by construction**
    (SQLite forbids non-deterministic functions in a generated column, `:642-645`). Cost:
    a public enum member, a public `CelNode` kind, a fourth column in every profile table,
    and a fourth row in every profile-matrix test.
59. **`Mutate` admits a closed function allow-list, reversing `cel.md`'s deviation 7 for one
    profile.** Deviation 7 refuses *any* identifier followed by `(` other than
    `has`/`changed` (`CelParser.cs:404-410`). `Mutate` admits exactly two names. The
    allow-list is positive and closed, on `_allowedProfiles`' own principle: a name missing
    from it compiles in no profile rather than in every profile (`cel.md:14-16`).
60. **`lowerAscii(x)` adopts CEL's standard-library name in Alvo's own call shape.**
    Conformant CEL spells it as the receiver macro `x.lowerAscii()`, which Alvo's one-level
    dot rule (`cel.md` deviation 8) cannot express. Taking the standard's *name and
    semantics* and deviating only on the *call shape* is the smaller deviation; the shipped
    `lower(...)` spelling is refused with a fix suggestion. The fold is ASCII-only by
    definition, so the implementation is an explicit `A`–`Z` fold, **not**
    `ToLowerInvariant()` — a Unicode-sensitive fold on a stored value is a permanently
    wrong row.
61. **`now()` is a nullary allow-listed function bound to one instant per write, not a clock
    read, and never rendered to SQL.** The instant comes from `TimeProvider`
    (`AlvoAuditStamp.cs:24-27`) and is the same one the write's audit stamp uses, so
    `now()` is referentially transparent within a write and testable with a fake clock;
    every value goes through `StoredInstant` (`data-path.md:354-391`, which names PR5's
    outbox at `:386`). SQL rendering is refused because Postgres's `now()` is transaction
    start time and SQLite's `CURRENT_TIMESTAMP` is second-precision text — two engines, two
    answers, against §0 principle 3. **`@now` was considered and rejected**: it widens the
    closed `@`-context set and would be the first memberless context reference.
62. **JSONata is deferred entirely from PR5; only `{{…}}` sugar is honoured, and a raw
    expression is refused at apply by a named `UnhonouredFeatures` entry.** A partial
    implementation would be *inventing a variant of a standard*, which `CLAUDE.md` calls a
    defect rather than a shortcut, and its failure mode is a delivered-but-wrong payload.
    The refusal is an **error**, not a warning, on `UnhonouredFeatures`' own line
    (`UnhonouredSubsystems.cs:12-19`): the action still runs, with a body the author did not
    declare. Cost: four frozen action slots are unusable in their expression form until an
    evaluator lands, and `webhook.payload` authors must accept the canonical envelope.
63. **The template-versus-raw discriminator is Alvo's, and a `$defs/jsonata` slot with no
    placeholder is refused too.** The schema types both as one `string` (`:398-403`), so the
    distinction is made at apply. The rule is stated in Decision 2; the two clauses exist
    because *"contains `{{`"* would deliver `crm.alvo.json`'s `$merge([...])` as literal
    text, and a brace-free expression such as `records.id` would otherwise render as the
    literal string. The asymmetry with the plain-string sugar slots (`email.to`,
    `entity.update.recordId`, `templates.*`) is deliberate and comes from the schema's own
    typing.
64. **Templates resolve at apply time against the schema; an unresolvable placeholder is
    refused, never rendered empty.** Same property #20's DoD demands of rules (*"fails at
    save, not at request time"*, `:104-106`). Consequence: `{{@user.email}}`
    (`crm.alvo.json:221`) is refused — `AlvoContext` carries no email
    (`AlvoContext.cs:17-34`) and the closed `@`-context set has three members
    (`cel.md:234-238`). Rendering `To: ""` would be the exact misattribution
    `UnhonouredSubsystems` exists to prevent (`:21-24`).
65. **The spec's "prove the JSONata in-transaction ban by a test" is satisfied in PR5 by an
    *absence* test, named as one.** `alvo-specifikacia.md:300` requires *"zákaz
    in-transaction (testom)"*; with no evaluator the ban is vacuous, and a test named as a
    ban would read forever after as though one were enforced. PR5 asserts instead that no
    evaluator exists and every raw expression is refused. The real ban test is owed by the
    PR that introduces the evaluator and must be **architectural** (nothing on the
    in-transaction path can reach it), not behavioural.
66. **`function` and `http.call` are refused by name, and the design's silent drop of
    `function` is recorded as a scope defect.** `alvo-specifikacia.md:330` lists `function`
    in the F4 action set and, in the same sentence, requires the STK demo rule to be
    buildable from F4; the base design's action set (`:609-611`) omits it without saying so.
    So the omission removes the mechanism the same source line depends on.
67. **The F6 demo rule "STK ends in 30 days → email" is not reachable from the descriptor in
    PR5, and JSONata is not the reason.** A `schedule` trigger carries no record and the
    frozen `automationRule` (`schema:963-1033`) has no scan or query concept; `perItem` is
    defined per *affected row* (`:1019`) and a cron tick affects none. `complex-crm`'s own
    `stale-deal-reminder` delegates the scan to a `function`, with the author's comment
    saying so. Recorded, with a follow-up issue for a declarative scheduled-scan shape —
    which is a **schema** decision, above PR5.
68. **Envelope attribute names are CloudEvents-legal and prefer the community's registered
    names; `record`, `old_record` and the changed-column list live inside `data`.** Names
    are `[a-z0-9]+` only and SHOULD stay under 20 characters
    ([spec v1.0.2:173-175](https://github.com/cloudevents/spec/blob/v1.0.2/cloudevents/spec.md#attribute-naming-convention)),
    and the seven-type system has no map or array (`:179-217`), so the design's
    `payload_version`, `chain-depth` and object-valued `record`/`old_record` (base design
    `:546-558`) are all non-conformant as written. Corrected per the table above. Extensions
    are flat top-level members (`:439-440`), never a nested `extensions` object. **Stated
    provenance:** `partitionkey`, `sequence`/`sequencetype` and `dataref` are registered in
    v1.0.2; `authtype`/`authid` and `correlationid`/`causationid` are post-1.0.2
    (`extensions/authcontext.md`, `extensions/correlation.md` on `main`) and are adopted
    anyway, because they are the community's names and inventing `actor` is worse.
69. **`payloadversion` is kept even though `type` + `dataschema` already carry the same
    information.** The CloudEvents-native way to version a payload is a versioned `type`
    and/or a `dataschema` URI. The design mandates *"a payload version from the first day"*
    (`:548`) and it is kept, because an in-process subscriber switching on an integer is
    cheaper and less error-prone than parsing a URI — but it is a duplication, recorded here
    rather than discovered by whoever notices the two can disagree.
70. **The dispatcher gates on `AlvoBootState`, never on hosted-service registration order.**
    .NET 10 runs the whole of `ExecuteAsync` off the startup thread
    ([compat/extensions/10.0](https://learn.microsoft.com/dotnet/core/compatibility/extensions/10.0/backgroundservice-executeasync-task)),
    so "not before the schema is primed" is inexpressible as ordering and
    `await Task.Yield()` is dead code on `net10.0`. Cost: **PR5 cannot merge before the
    startup PR** — `AlvoBootState` does not exist in this worktree.
71. **The dispatcher never lets an exception escape `ExecuteAsync`.**
    `BackgroundServiceExceptionBehavior` defaults to `StopHost`, so one poison event would
    stop a host that is serving HTTP; from .NET 11 the process also exits non-zero. PR5's
    stand-in for a DLQ (7.1) is a per-event attempt counter plus a loud log, and the loop
    observes its cancellation token promptly because the host blocks in `StopAsync` for up
    to `ShutdownTimeout`.
72. **[unratified] Exactly one dispatcher in F3; `partition_key` is written from the first
    migration.** Restores the hedge `baas-analyza.md:656` states and the base design drops
    (`:574-577`): `FOR UPDATE SKIP LOCKED` skips rows, not keys. Per-entity-key ordering
    holds *while one dispatcher runs* — a documented deployment constraint PR5 cannot
    enforce, since there is no distributed lock to detect a second instance. The claim
    predicate is `claimed_at IS NULL` / `dispatched_at IS NULL` and **never** a high-water
    mark on `sequence`, because PostgreSQL sequences commit out of order; and under
    `UseRelationalNulls()` those predicates are written as SQL reads them
    (`data-path.md:121-145`).
73. **[unratified] `Publish` is C#-subscriber-only in F3, and may not mint a name in the
    framework namespaces.** `$defs/eventPattern` is closed to `^(entity|auth|storage)\.`
    (`schema:409-419`), so no descriptor rule can subscribe to `order.approved`. The
    narrowing is real and stated: the advantage over the cited Directus defect is available
    only to a host writing C#, which is not the audience the defect was cited for. The
    schema change is filed as its own issue with the whole namespace designed, because the
    same grammar also makes `auth.user.password_changed` unrepresentable. Regardless of
    ratification, `Publish` **refuses** a framework-namespace name, or a host could forge an
    event indistinguishable from a real data change.
74. **Cron ships with no distributed lock**, against `baas-analyza.md:819`'s *"cron job sa v
    3-instance nasadení spustí práve raz"*. Recorded rather than discovered. Same underlying
    gap as deviation 72's, and the analysis names the .NET blocks (Postgres advisory locks /
    Redis) for whoever closes it.
75. **The before-hook pipeline runs inside the write, and the create path as built cannot
    satisfy its own DoD line.** `AuthorizedCandidate` runs at `EfAlvoData.cs:175`, before
    `BeginTransactionAsync` at `:177`, so a hook there has nothing to roll back. Both faces
    must share one pipeline, which means a port injected into a driver
    (`EfAlvoData` is `internal sealed` in a driver; the pipeline belongs in the core) and
    `InMemoryAlvoData` invoking it identically. PR5b owes an explicit ruling on whether
    `WritePayloadGuard` re-runs over a mutated payload — it must not, because it is written
    for callers and would reject a hook legitimately setting a `readOnly` field.
76. **`examples/complex-crm/crm.alvo.json` is corrected in the same PR that lifts a hook
    refusal, and `Every_example_marked_not_runnable_really_is_refused` is strengthened to
    assert the refusal *reason*.** Four of its hook expressions do not compile today
    (`:110`, `:143`, `:147`, `:148`) plus a fifth unresolvable template (`:221`), and the
    test asserts only `Should.Throw<InvalidDataException>`
    (`DescriptorToSchemaMapperTests.cs:243-252`) — so a CEL syntax error would silently
    stand in for the feature refusal the test claims to hold, and the marker its own XML doc
    says it forces to shrink (`:230-238`) would never shrink. Two adjacent traps recorded,
    not fixed: `crm.alvo.json:82`'s `rollup.where` list literal (PR6's), and the three
    `access` expressions (`:16-20`), which are `$defs/cel`-typed, never compiled by anything
    in `src/`, and carry four separate defects for whoever honours `access`.
77. **`UnhonouredFeatures`' `simple-tasks` / `completed_at` justification is corrected.**
    `UnhonouredFeatures.cs:114-119` cites an example that does not exist —
    `examples/simple-tasks/tasks.alvo.json` declares no `hooks` and no `completed_at`, and
    `complex-crm` is the only example in the tree with hooks. The reasoning is right, the
    citation is fictional; the real case is `deals.beforeUpdate` setting `closed_at`
    (`crm.alvo.json:148`). PR5 is the PR that edits this file, so it is the PR that fixes it.
78. **[unratified] PR5 is split into PR5a (event backbone) and PR5b (before-hooks +
    automation), with the template/refusal classifier, the transition test and the
    execution-log criterion in 5a.** R6 and R10 are design work, not implementation, and
    both change public surface or the dependency set. The classifier is in 5a because
    `webhook.payload`/`email.data` are JSONata slots and 5a ships after-hooks; the transition
    test is in 5a because it is an after-hook `Condition`; the execution-log criterion is in
    5a because the base design's own argument is that it is *"nearly free if designed in and
    awkward to retrofit"* (`:585-592`). Cost: PR5 closes #22 only when 5b merges, and 5a
    cannot merge before the startup PR.

---

# Ratification needed from the maintainer

Nothing below is decided by this document.

1. **R3 — one dispatcher in F3, `partition_key` written now, partitioning filed for F7**
   (deviation 72). The alternative is building the partition scheme in PR5. The trade: (a)
   is honest for a milestone whose analysis scopes the MVP to a single node and satisfies
   the guarantee by construction, at the cost of a documented deployment constraint PR5
   cannot enforce; (b) delivers multi-worker ordering now, built without the load that
   would validate it, and enlarges the claim protocol PR5 owns.
2. **R8 — `Publish` is C#-subscriber-only in F3** (deviation 73), with the `eventPattern`
   grammar redesigned as its own issue. The alternative is widening the frozen pattern in
   PR5. The trade: a stated narrowing of a benefit the design and the analysis both claim,
   versus a frozen-artifact change made under PR5's schedule when a second case
   (`auth.user.password_changed`) proves the whole namespace needs designing.
3. **The PR5a / PR5b split** (deviation 78), including the three criteria moved into 5a and
   the acknowledgement that 5a is blocked on the startup PR.
4. **The profile name `Mutate`** (deviation 58), and the fact that it will also compile
   `entity.update.payload` rather than a fifth profile being added for it.
5. **`now()` as an allow-listed nullary function** rather than a memberless `@now` context
   reference (deviation 61), and the ASCII-only `lowerAscii` rename that changes a shipped
   example (deviation 60).
6. **The wildcard-subscription ruling** — implement the matcher with tenant scoping and an
   adversarial cross-tenant fact, or refuse `*` at apply until it exists. A *"hard"* spec
   guarantee (`alvo-specifikacia.md:141`) is being either scoped or deferred, and either is
   the maintainer's call.
7. **Whether PR5 adds a mail service to compose**, or the DoD's *"email end to end"* is
   explicitly recorded as console-provider-only in F3.
8. **`payloadversion`, kept despite duplicating `type` + `dataschema`** (deviation 69).

---

# Definition of Done

Amends the base design's `#22` DoD (`:116-121`) and PR5 verification bullet (`:1287-1289`).
Numeric criteria are lifted from `baas-analyza.md:676-680` and the issue's maintainer
comment, not invented. **Split per the recommended PRs**; if the split is not ratified, the
union applies to one PR.

## PR5a — the event backbone

- A 10k-event chaos run **loses no event**, on SQLite and PostgreSQL.
- Kill **between commit and publish** → the event is delivered after restart; **kill
  mid-action → the action repeats** (the half the issue body drops,
  `baas-analyza.md:676`). The harness states in its own name what it does and does not
  prove about a real process kill.
- The outbox row rides the **same `DbTransaction`** as the data change, proven by a test
  that rolls the transaction back and finds no outbox row — and it is **not** hung off
  `SaveChanges`, because `ExecuteUpdate`/`ExecuteDelete` fire no interceptor
  (`data-path.md:1386-1391`), so a test covers **update and delete**, not only create.
- The envelope **passes a CloudEvents conformance check**: every extension name matches
  `[a-z0-9]+`, every attribute value is one of the seven types, extensions are flat
  top-level members, and `record`/`old_record`/changed columns are inside `data`. Asserted
  through `CloudNative.CloudEvents`' own `CloudEventAttribute.CreateExtension` as an oracle
  (core-side only — `Abstractions` takes no new dependency).
- `changed(status) && new.status == 'approved'` on an after-hook fires **exactly once, at
  the transition**.
- **N events matching nothing produce zero execution-log rows and one counter increment.**
- A raw JSONata expression in any of the four `$defs/jsonata` slots is **refused at apply**
  by a named `UnhonouredFeatures` entry, and the four classifier cases are pinned:
  `$merge([...])` → refused; `{"companyIds": records.id}` → refused; `records.id` → refused;
  `{{new.title}}` → template.
- An **absence** fact: no JSONata evaluator exists on any path (deviation 65).
- A template placeholder naming an undeclared field, or an unresolvable root such as
  `@user.email`, is **refused at apply** with a fix suggestion — never rendered empty.
- The dispatcher **awaits `AlvoBootState`** and does not run before the schema is primed;
  proven by a fact that does not depend on registration order. An exception thrown inside
  the loop **does not stop the host**.
- The outbox table name is in `SystemSchemaInitializer.FrameworkTableNames` (`:67`), so a
  re-apply does not plan a `DROP`; a second apply produces an **empty** plan.
- Every new SQL-composing file is added to `ChangeTrackerReachTests._sqlComposingFiles`
  (`:177-189`); no change-tracker write appears anywhere in the dispatcher.
- Every timestamp goes through `StoredInstant`; every claim predicate is written for
  `UseRelationalNulls()` semantics.
- Public-API baselines approved; `alvo-snapshot-judge` has passed on every moved
  `*.verified.*`; `workflow_dispatch` mutation run green before merge.

## PR5b — before-hooks and automation

- `CelProfile.Mutate` exists, **appended** to the enum, with the four-column truth table
  in `cel.md` and a profile-matrix fact per construct kind — including the negative legs:
  a function call is refused in `Rule`, `Computed` and `Condition`; `new.` is still refused
  in `Computed`.
- `lowerAscii(new.email)` compiles in `Mutate` and nowhere else; `lower(...)` is refused
  with a fix suggestion naming `lowerAscii`; the fold is ASCII-only, proven on a non-ASCII
  input.
- `now()` compiles in `Mutate` only, resolves from `TimeProvider`, and **returns the same
  instant twice within one write** and the same instant the audit stamp records — proven
  with a fake clock. A `Mutate` expression is never handed to `SqlPredicateRenderer`
  (asserted structurally).
- A before-hook can **reject** (RFC 7807 carrying the `reject` text) and can **mutate**, on
  **create, update and delete**, with the mutation applied **inside** the write transaction
  after the audit stamp.
- **A budget overrun rolls the transaction back cleanly with RFC 7807 — on the create path
  too**, which requires the hook to run inside the transaction rather than where
  `AuthorizedCandidate` runs today (`EfAlvoData.cs:175`).
- An explicit, tested ruling on whether `WritePayloadGuard` re-runs over a mutated payload
  (it must not), with a fact showing a hook may set a `readOnly` field a caller may not.
- The declarative and C# faces run through **one** pipeline, invoked identically by
  `EfAlvoData` and `InMemoryAlvoData`.
- Hooks and automation join the **`PolicyCatalog`'s** priming, not a fourth priming site, so
  a hook cannot be compiled against a different schema revision than the rules judging the
  same write.
- An ECA rule fires on an event and runs `entity.update`; a cron rule fires on schedule; an
  email is sent — end to end, on both engines. `function` and `http.call` are refused by
  name, with the consequence stated per action.
- `UnhonouredFeatures`' six hook entries are gone; its `simple-tasks`/`completed_at` comment
  is corrected; `Every_example_marked_not_runnable_really_is_refused` asserts the refusal
  **reason**, and `complex-crm`'s five broken expressions are fixed.
- `alvo-plan-guard` dispatched as the last pre-PR check; security review plus the
  `alvo-security-core-review` checklist; `workflow_dispatch` mutation run green before merge.

---

# Corrections to the risk register

Recorded so the register can be trusted where it is not corrected. Every `file:line` in it
that this document reused was re-checked against the tree.

**Confirmed as written** (spot-checked): `CelCompiler.cs:97` (the Rule/Condition non-`Bool`
branch is exactly that line); `cel.md:10-45` and `:226-262`; `EfAlvoData.cs:175`/`:193` and
the four begin/commit pairs `177/179, 321/330, 582/586, 620/622`;
`SystemSchemaInitializer.cs:15-17` (the identical-DDL invariant) and `:67`
(`FrameworkTableNames`); `ChangeTrackerReachTests.cs:177-189`; `data-path.md:1385-1391` and
`:121-145`; `package-boundary.md:96-103`; `AlvoContext` carries no email;
`schema/project.schema.json`'s frozen `eventPattern`, `cron`, `action` and hook shapes;
`baas-analyza.md:656` (the dropped hedge) and `:657` (tenant isolation of rules); the .NET 10
`BackgroundService` change, verbatim.

**Corrections and additions:**

1. **The register undercounts `complex-crm`'s broken hook expressions: there are four, not
   two.** `crm.alvo.json:143` and `:147` are **`Condition`** expressions using list literals
   (`in ['won', 'lost']`), refused by `CelParser.cs:278-281` — *"Alvo has no list literals"*
   (`cel.md` deviation 6). So lifting the `beforeUpdate` refusal breaks the example **even
   with the `Mutate` profile fully implemented**, which strengthens the register's own
   conclusion. A fifth defect sits in the same file: `{{@user.email}}` (`:221`) is
   unresolvable.
2. **The cron "exactly once in a 3-instance deployment" criterion is in
   `baas-analyza.md:819`, not spec §7.3.** The analysis's scheduling component states it,
   with the acceptance criterion and the .NET blocks (Postgres advisory locks / Redis).
3. **`authtype`/`authid` and `correlationid`/`causationid` are NOT registered in CloudEvents
   v1.0.2.** The v1.0.2 `documented-extensions.md` lists exactly five: Dataref, Distributed
   Tracing, Partitioning, Sampling, Sequence. Auth Context and Correlation exist as
   `extensions/authcontext.md` and `extensions/correlation.md` on `main` (post-1.0.2). The
   recommendation is unchanged; the provenance must be stated, or a reader checking the
   v1.0.2 registry concludes the names were invented.
4. **The register omits a constraint from the same naming rule:** names *"SHOULD be
   descriptive and terse and SHOULD NOT exceed 20 characters in length"*
   (spec v1.0.2:174-175). Every candidate above is within it, but it must be checked, not
   assumed, for any name added later.
5. **The register does not mention that `sequence`/`sequencetype` are registered CloudEvents
   extension names** with specific semantics — a lexicographically comparable **String**,
   scoped per `source`. The outbox's monotonic integer is not that, which is one more reason
   not to surface it on the envelope (and it compounds the register's own R2).
6. **A stale claim inside a file PR5 edits:** `UnhonouredFeatures.cs:114-119` cites
   `simple-tasks`' `beforeUpdate` setting `completed_at`. That example has no hooks and no
   such field; `complex-crm` is the only example with hooks. Not a register error — an
   in-repo one the register did not reach.
7. **Two latent traps beyond the register's scope, in the same example:**
   `crm.alvo.json:82`'s `rollup.where` uses a list literal (PR6 inherits the identical
   shape), and `crm.alvo.json:16-20`'s three `access` expressions are `$defs/cel`-typed
   (`schema:149-167`) and compiled by nothing in `src/` — they carry `@user.role`
   (singular, superseded by base design deviation 3), a list literal, and
   `@user.email.endsWith(...)` (a method call and two levels of dot). Owned by nobody today.
8. **`function`'s omission is more than an undeclared deviation.**
   `alvo-specifikacia.md:330` — the line the register cites as `:332` — lists `function` in
   the F4 action set **and**, in the same sentence, requires the STK demo rule to be
   buildable from F4. So dropping `function` removes the mechanism that source line depends
   on. The line number is 330.
9. **One claim in the task framing is not in the register:** an over-claim about *"resolution
   A's OpenAPI accuracy"*. The register contains no OpenAPI finding, and the base design's
   OpenAPI section (`:516-540`) already corrects an earlier "emit from `SchemaModel`"
   formulation and anchors the document to the mapped routes. Nothing was carried forward
   for it, because there is nothing cited to carry. If that finding exists, it is in a
   source this document did not receive.
