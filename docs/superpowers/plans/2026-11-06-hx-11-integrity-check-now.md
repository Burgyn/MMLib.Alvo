# HX-11 — `CHECK`, `rollup.where`, `now()` and durations — Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** The descriptor states integrity the database enforces: a field's `validation` becomes a SQL `CHECK` constraint (refused at apply, with the count, over rows that already break it; answered **422 `validation` with a pointer** when a write breaks it, on both engines), a rollup's `where` filters the aggregated children inside the same lock-then-recompute, and CEL gains `now()` (one instant per request) and CEL durations (`duration('90m')`, `timestamp ± duration`, `timestamp - timestamp`) that the interpreter and the SQL renderer evaluate identically on SQLite and PostgreSQL.

**Architecture:** Durations are a new value type (`CelValueType.Duration`, appended) built only from a constant — `duration('…')` is folded to a typed `CelLiteral` at type check — and a new internal *construct kind*, temporal arithmetic, admitted in `Rule`, `Condition` and `Mutate` (no new profile, no new AST node — X7). The interpreter evaluates temporal arithmetic over `DateTimeOffset`/`TimeSpan` with CEL's range errors; the SQL renderer evaluates it over **integer microseconds since the Unix epoch** through one new dialect member, `IFieldSqlRenderer.RenderEpochMicroseconds`. `now()` reads one ambient instant, `AlvoFlow.Instant`, bound per HTTP request by Alvo's caller filters (else per `IAlvoData`/`TransactAsync` entry), set to the event's `time` at fan-out, and also used as the write's audit stamp — PostgreSQL's `now()` = transaction start, applied per request. `field.validation` is rewritten by the descriptor mapper into a canonical `Rule`-profile CEL predicate over the row (`FieldSchema.CheckExpression`), compiled under a *stored-predicate surface* (no caller, no clock), rendered with **inline literals** by `IPredicateRenderer.RenderConstraint`, and handed to EF Core as `HasCheckConstraint` — so EF's own SQLite table rebuild and PostgreSQL `ADD CONSTRAINT` emit it; the migrator counts violating rows before it plans, and the dialects decode `SQLITE_CONSTRAINT_CHECK`/`23514` as `AlvoConstraintKind.Check`. `rollup.where` uses the same surface and the same inline renderer, ANDed into `RollupRecompute`'s subquery and into a new apply-time backfill.

**Tech Stack:** .NET 10 (C# 14), EF Core 10 migrations (`HasCheckConstraint`, `AddCheckConstraintOperation`, SQLite table rebuilds), Npgsql (PostgreSQL 16 in Testcontainers), Microsoft.Data.Sqlite (SQLite ≥ 3.42 for `unixepoch(…, 'subsec')`), CsCheck, xUnit v3 on Microsoft.Testing.Platform, Shouldly, Verify + PublicApiGenerator, `Microsoft.Extensions.TimeProvider.Testing`, Blazor/MudBlazor (dashboard), Microsoft.Playwright (admin e2e), Astro/Starlight (docs).

**Spec:** `docs/superpowers/specs/2026-10-14-host-extensibility-design.md` — §7 *HX-11* (scope, docs, dashboard, acceptance (1)–(7)); §1.1 goal 7; §1.2 (dynamic entities refused until #41); §4.3 the `UtcNow → now()` row and *HX-05 settled details* (4), (17), (21), (22); §4.4.2 (Ruling X); §5 row `11` and the closing "no `CelProfile` member and no CEL AST node (X7)"; §6.1 rows `field.validation → CHECK` and *dynamic entities*; §6.2 (injection via `CHECK`, hidden-field disclosure); Appendix A M18, M19; §3.3 E1, E2. Issue **#388** (epic #370, related #41). Product sources: `docs/product/baas-analyza.md` §2.1 (rollup = transactionally consistent, first-class; `closed_at = now()` is a before-hook mutate, line 123) and `docs/product/alvo-specifikacia.md` §0, testing §2 (CEL→SQL property tests: "translation never interpolates user input", line 300). Frozen artifacts: `schema/project.schema.json` `$defs/field/properties/validation` ("Optional CEL value validation (context: value, new)") and `$defs/field/properties/rollup/properties/where` ("Optional CEL filter on child records"); `src/MMLib.Alvo.Abstractions` (`CelValueType`, `IFieldSqlRenderer`, `IPredicateRenderer`, `FieldSchema`, `RollupSchema`, `AlvoConstraintKind`, `AlvoConstraintViolationException`, `PolicyDecision`, `AlvoFlow`).

**Prior art read and cited in the Rulings** (CLAUDE.md, *Study the problem before designing it*):

- **CEL language definition** (`google/cel-spec`, `doc/langdef.md`, *Timestamps and Durations*, *Overloads*): `duration(string)` parses Go's `time.ParseDuration` grammar — an optional sign, then one or more `<decimal><unit>` components, units `h`, `m`, `s`, `ms`, `us`/`µs`, `ns`, fractions allowed (`"-1.5h"`, `"2h45m"`, `"0"`); durations range ±315 576 000 000 s (≈ ±10 000 years), timestamps 0001-01-01T00:00:00Z … 9999-12-31T23:59:59.999999999Z, a result outside either is an evaluation error; overloads `timestamp + duration`, `duration + timestamp`, `timestamp - duration`, `timestamp - timestamp → duration`, `duration ± duration`, and the six comparisons on each type — no `*`, `/` or unary `-` on a duration, no int↔duration conversion in this set. Adopted: the grammar, the ranges, the overload set; deviated: literal-only argument and microsecond precision (Rulings 7, 8).
- **PostgreSQL `CHECK`** (*5.5.1 Check Constraints*): "a check constraint is satisfied if the check expression evaluates to true or the null value"; "PostgreSQL assumes that CHECK constraints' conditions are immutable"; no subqueries, current row only. **`ALTER TABLE … ADD CONSTRAINT … NOT VALID` / `VALIDATE CONSTRAINT`**: the add skips the scan and takes a brief lock; `VALIDATE` scans under `SHARE UPDATE EXCLUSIVE` — the benefit exists only when `VALIDATE` commits in a later transaction (Ruling 20). Error `23514 check_violation` carries `constraint_name` and `table_name`. **PostgreSQL `now()`/`CURRENT_TIMESTAMP`**: the start time of the current transaction, constant within it — the model for Ruling 1. `EXTRACT(EPOCH FROM timestamptz)` returns `numeric` (exact microseconds) since PostgreSQL 14.
- **SQLite `CHECK`** (*lang_createtable.html*, *CHECK constraints*): a violation only when the expression is zero; NULL or non-zero passes; non-deterministic functions are prohibited, date functions are deterministic unless given `'now'`. **SQLite `ALTER TABLE`** cannot add a constraint: the "generalized ALTER TABLE" 12-step rebuild (create new table, copy, drop, rename) is the only way — EF Core's SQLite migrations generator performs exactly that rebuild for `AddCheckConstraintOperation`. Extended code `SQLITE_CONSTRAINT_CHECK` = 275, message `CHECK constraint failed: <name>`. **SQLite date and time functions** (*lang_datefunc.html*): `unixepoch(x, 'subsec')` (3.42.0) answers seconds with millisecond resolution — "only the first three digits [of fractional seconds] are significant".
- **JSON Schema** validation keywords and **SQL `CHECK`**: an absent value passes a constraint on that value — the basis of Ruling 15.
- **EF Core** `ExecuteSqlRaw`: the SQL is a composite-format string when arguments are passed — `{`/`}` in inlined text must be doubled (Ruling 31).
- **RFC 9457** (one `type` per kind of refusal, `violations` with RFC 6901 pointers), **RFC 9110 §15.5.21** (422: the client can change the content) — Rulings 24–26.

## Global Constraints

- Worktree `/Users/martiniak/Developer/GitHub/Burgyn/MMLib.Alvo-wt-hx11`, branch `hx/11-integrity-check-now`, off `main` at `33201577` (HX-01…HX-10 merged). The PR targets `main`. Issue **#388**. One writer per worktree; never `git add -A` (stage each task's files by path); `git branch --show-current` prints `hx/11-integrity-check-now` before every commit and before the push. **Never run `git clean`**; never delete anything under `.superpowers/`. A bare `ls` hides git-ignored files in this shell — use `/bin/ls` or `find`. **No subagent edits this worktree while another writer is active.**
- **Scope is §7 HX-11 only.** No `sequence`, no exclusion or partial index (`indexes[].where` is HX-12 — it will reuse `RenderConstraint`), no timers (HX-13), no `stateMachine` (HX-18), no relation rules (HX-19). **`schema/project.schema.json` does not move** (frozen): `validation` and `rollup.where` already exist there.
- **Public surface added is exactly** (each beyond §5 row `11` is a Ruling — 6, 11, 17, 18):
  - Abstractions `MMLib.Alvo.Data`: `AlvoConstraintKind.Check` (appended after `Referenced`).
  - Abstractions `MMLib.Alvo.Expressions`: `CelValueType.Duration` (appended after `Null`); `IFieldSqlRenderer.RenderEpochMicroseconds(string)` (default interface member returning `null`); `IPredicateRenderer.RenderConstraint(CompiledExpression, IFieldSqlRenderer)` (default interface member throwing `NotSupportedException`).
  - Abstractions `MMLib.Alvo.Schema`: `FieldSchema.CheckExpression` (`string?`, init); `RollupSchema.Where` (`string?`, init).
  - Everything else is `internal` (`AlvoFlow.Instant`/`EnterInstant`/`EnsureInstant`, `PolicyDecision.Unnameable`, every new core and driver type).
- **Wire and naming constants (verbatim):**
  - write-time refusal: status **422**, type `validation`, violation code **`check`**; message for a named field `This value breaks the validation the descriptor declares for this field.`, fix `Send a value the field's validation accepts.`; for no field (pointer `""`) `This write would leave a record breaking a validation the descriptor declares.`, fix `Change the values this write sends so every record it touches keeps its validations.`; `AlvoConstraintViolationException` message for `Check`: `A value this request supplies breaks a validation the descriptor declares. Send values every validation accepts.`
  - apply-time refusal: `DescriptorValidationException` (422 `validation`, code `descriptor` on the Management API) at `/entities/<entity>/fields/<field>/validation`, message `<n> row(s) of '<entity>' break this validation, so the CHECK constraint cannot be added. Nothing was applied.`, fix `Correct those rows first — or relax the expression — then apply again.`; without a count (engine refusal at apply) `Rows of '<entity>' break this validation, so the CHECK constraint could not be added. Nothing was applied.`
  - dynamic refusal message: `Field '<entity>.<field>' declares 'validation', which becomes a database CHECK constraint, and '<entity>' declares 'storage: dynamic' — dynamic entities have no table to hold one until #41 defines how their integrity is enforced.` fix `Remove 'validation', or make '<entity>' a physical entity.`
  - constraint names: `ck_<entity>_<field>`; past 63 bytes `ck_` + the first 51 characters of `<entity>_<field>` + `_` + the first 8 hex digits of SHA-256(`<entity>.<field>`).
  - duration literal canonical text (translator, docs): `duration('<seconds>s')`, seconds with up to six fraction digits, invariant culture (`duration('5400s')`, `duration('0.25s')`).
  - SQL temporal domain: SQLite `CAST(round(unixepoch(<x>, 'subsec') * 1000000) AS INTEGER)`; PostgreSQL `CAST(EXTRACT(EPOCH FROM <x>) * 1000000 AS bigint)`.
- **Engine parity:** every fact touching SQL runs on SQLite and PostgreSQL — shared `test/_shared/api/DataApiEngineTests.Integrity.cs` (`SqliteDataApiTests` in `MMLib.Alvo.Api.Tests`, `DataApiOnPostgresTests` in `MMLib.Alvo.Api.Tests.Integration`), the abstract `src/MMLib.Alvo.Testing/Data/AlvoDataCheckConstraintTests.cs` (one subclass per engine), the extended `AlvoDataDifferentialTests`/`DifferentialRuleCases`, and `test/_shared/testhost/NowInstantFacts.cs` (linked into both TestHost legs, as `TypedLayerFacts` is). The only one-engine fact is the PostgreSQL rollup race (Ruling 33).
- **Security core (§6.2 lists HX-11):** CEL compilation, the SQL renderer, the rule engine's `now()` and constraint decoding change. Task 14 runs the `alvo-security-core-review` checklist, a `/security-review` substitute (labelled) and labels the PR `needs-deep-review`.
- **Tests are deterministic.** Every fact that reads time uses `FakeTimeProvider` (`Microsoft.Extensions.Time.Testing`); no `Task.Delay`/`Thread.Sleep`, no real-clock limit inside a fact about something else (HX-10's lesson). Concurrency facts assert outcome counts, never timings; the PostgreSQL race widens its window with `pg_sleep` inside the statement (the existing `SleepingFieldSqlRenderer` pattern), never with a client-side delay.
- Public XML docs cite no `§` and no `#issue` (DocsGen's `RemarksFilterTests`). XML docs on every public member.
- C# style: `alvo-dotnet-conventions` — no inline comments, methods ≲ 25 lines, English only, `ConfigureAwait(false)` in `src/`, never in tests (xUnit1030); every test call passes `TestContext.Current.CancellationToken` (xUnit1051). **`.cs` files are UTF-8 with BOM + CRLF**: after creating or editing `.cs` files run `dotnet format whitespace MMLib.Alvo.slnx --include <file> <file> …` (file paths, never a directory).
- Single-test runs: `dotnet test --project <test project> --filter-class '*<ClassName>'` (MTP). Public-API baselines: `*PublicApiApprovalTests` in `test/MMLib.Alvo.Abstractions.Tests` (and `test/MMLib.Alvo.Tests` for core, which must not grow). To accept a baseline: run the approval test, read the `*.received.txt` diff — it must add exactly the symbols listed above — then move it over the `*.verified.txt`. A grown `PublicApi.*.verified.txt` (or any moved `*.verified.*`, including `test/MMLib.Alvo.Tests/cel-to-sql-core.verified.txt`) trips the turn-review gate: dispatch `alvo-snapshot-judge`, and justify each added symbol against `alvo-architecture-rules` (*public is the contract*) and the list above.
- Rings: `scripts/test-ring0` after every task, `scripts/test-ring1` after each task marked **[slice end]**, `scripts/test-ring2` before the PR. **`scripts/test-admin-e2e` runs whole once, in Task 14**; Task 11 runs only `scripts/test-admin-e2e --filter IntegrityEditor`. **Never run Stryker locally**; mutation is configuration only (Ruling 38). `scripts/test-load` is not run (no hot-path claim; Ruling 39).
- **Test-gap Importants fold into the next task:** an Important review finding that is only a missing test is fixed as the first step of the following task.
- **CI parity:** rings are Debug on the local SDK; CI builds Release with `TreatWarningsAsErrors`, `AnalysisLevel=latest-recommended` and `EnforceCodeStyleInBuild`. Task 14 runs the Release build in the CI SDK container — in this worktree the main repository's `.git` must be mounted too (the worktree's `.git` is a file pointing into it).
- Commits: Conventional Commits, one per task, each message ending with `Claude-Session: https://claude.ai/code/session_01NJAtafM29iddLwfhb5gnmB`. No `Closes`/`Fixes` keyword anywhere in a commit message (they fire from prose); the PR body carries `Closes #388` alone.

## Review Focus

1. **A text constant holding `{0}`, `{`, `}` or `'` in a `rollup.where`** (`"label == '{0}'"`, `"label != '}'"`) — `RollupRecompute` composes raw SQL through EF's composite-format `ExecuteSqlRaw(sql, args)`; an inlined brace must match literally, never throw `FormatException`, never shift `{0}`/`{1}` onto the wrong argument. `RollupRecomputeFilterTests.A_filter_constant_with_braces_is_written_doubled_and_matched_literally` (Task 9, unit) and `AlvoDataComputedRollupTests.A_filter_whose_constant_holds_braces_aggregates_exactly_the_matching_children` (Task 9, both engines).
2. **An entity with a field named `value`** whose other field declares `validation: "value > 0"` — the frozen schema's `value` and the entity's field collide; the apply must refuse naming both readings, never silently bind to the field `value`. `FieldValidationRewriteTests.A_bare_value_on_an_entity_with_a_field_named_value_is_refused_as_ambiguous` (Task 6).
3. **An optional field's validation over a row that leaves it empty** — `discount` with `validation: "value <= 50"` and a create that omits `discount` must be accepted on both engines (SQL `CHECK` passes NULL; JSON Schema passes an absent value), while a validation reading *another* empty field follows CEL's rule (a comparison with a missing value is false → refused). `AlvoDataCheckConstraintTests.An_empty_validated_field_passes_its_check` and `…A_validation_reading_an_empty_other_field_refuses_the_row` (Task 7, both engines).
4. **A timestamp the caller sent with a non-UTC offset** (`2026-03-29T01:30:00+02:00`) inside duration arithmetic — SQLite stores text with the offset and PostgreSQL a `timestamptz`; the interpreter (`WITH CHECK`) and the SQL `USING` must give the same verdict. `DifferentialRuleCases` rows `offset-*` and the CsCheck property's offset generator (Task 3, both engines).
5. **A `renamedFrom` on a validated field of a populated table** — the constraint's name follows the field, the pre-apply scan is skipped for a renamed table or column (Ruling 22), so the DDL itself must refuse a violating table and accept a compliant one, and the refusal must still point at the validation. `AlvoDataCheckConstraintTests.Renaming_a_validated_field_over_compliant_rows_applies_and_over_violating_rows_is_refused_at_its_validation` (Task 8, both engines).

---

## Rulings and deviations this plan takes

Each is recorded in the spec amendment (Task 13) as *HX-11 settled details* (1–44), so a later reader can tell a decision from an oversight. Format: **decision** — why — cost if wrong.

### `now()`

1. **One instant per request, held by the flow.** `AlvoFlow.Instant` (internal `AsyncLocal<DateTimeOffset?>`, microsecond-truncated) is bound by Alvo's HTTP caller filters (`AlvoContextFilter` for Data API routes, `AlvoCallerFilter` for `WithAlvoCaller()`/command routes, `ManagementCallerFilter`) for the request; outside a request, by each public `EfAlvoData` entry point and `TransactAsync` *if none is bound* (`EnsureInstant`); and it is **overridden** with the event's `time` around a post-commit condition (after-hook conditions, handler `when` at fan-out). Every `now()` — `Rule` (SQL `USING` and in-process `WITH CHECK`), `Condition`, `Mutate` — reads it. Prior art: PostgreSQL's `now()` is the transaction's start, constant within it. — A clock read per evaluation would let a rule's `USING` and its `WITH CHECK` disagree about one row, and acceptance (4) asks for one instant. — Cost: a long request stamps and judges at its start time.
2. **The write's audit stamp is that same instant.** `EfAlvoData.WriteInstantNow()` answers `AlvoFlow.Instant ?? StoredInstant.Storable(_time.GetUtcNow())`, so `mutate: now()` still equals the row's `updated_at` (`data-path.md`, *Every timestamp is one instant*) and the event's `time`. A retried attempt now keeps the instant instead of re-stamping. — Without it, mutate's `now()` would be the stamp and a rule's `now()` the request instant: two instants in one request. — Cost: two updates of one row inside one unit carry the same `updated_at` (= ETag version); both are inside one unit, so no outside caller can hold the intermediate version.
3. **Where `now()` compiles.** `Rule`: the request instant, a bound parameter. `Condition`: the write instant (validate, before- and after-write `when`, declarative before-hook conditions) or the event time (post-commit conditions). `Mutate`: as before. **Refused** in `Computed` (a generated column must be immutable), `Access` (no row, project-scoped), a field's `validation` (PostgreSQL assumes `CHECK` is immutable; a constraint that changes verdict with time breaks rows nobody touched) and `rollup.where` (a stored aggregate filtered by time goes stale with no write to recompute it). — Spec §7 scope, §6.1 "`now()` refused". — Cost: a time-windowed aggregate needs a host job (HX-13).
4. **A rule's temporal subtree with no field is folded in-process.** `created_at > now() - duration('24h')` renders `"created_at" > @alvo_u0` with one bound `DateTimeOffset`, so the predicate stays sargable and needs no dialect function; only a subtree reading a field enters the microsecond domain (Ruling 11). — Cost: none; the fold uses the same `CelTemporal` arithmetic the interpreter does.
5. **An unbound instant fails closed.** The interpreter's `now()` with neither an explicit instant nor `AlvoFlow.Instant` throws the interpreter's defect (`CelFunctionException` `<hook>`, not caller data → 500) and the renderer throws `InvalidOperationException`; neither reads a clock. — A silent clock read would hide a missing binding site and break (1). — Cost: a host that calls `IPredicateRenderer` directly for a `now()` rule must bind an instant (`InMemoryAlvoData` and the differential suite do).

### Durations

6. **`CelValueType.Duration` is appended (public).** §5 lists only `AlvoConstraintKind.Check` for HX-11, but the type checker, `CelFunctionInfo` and HX-18's `TimerAt` typing (Appendix A M19) need a value type; an internal member of a public enum is not expressible. — Cost: one enum member; an exhaustive `switch` in a consumer gains a case (CHANGELOG).
7. **`duration()` takes a constant only, folded at type check.** `duration('90m')` becomes `CelLiteral(CelValueType.Duration, TimeSpan)`; a non-constant argument is refused in every profile. CEL admits any string expression. — A constant is validated at apply (never a runtime parse failure inside a write) and is the only shape a `CHECK` or a SQL `USING` can carry. `duration` is a catalogued built-in (so the parser, the function catalog and the assistant know it) whose call never survives type checking. — Cost: no duration computed from a field (`duration(string(minutes) + 'm')`).
8. **Grammar and range are CEL's; precision is the microsecond.** Sign, compound, fractional components, units `h m s ms us µs μs ns`, bare `"0"`; |value| ≤ 315 576 000 000 s. A value that is not a whole number of microseconds (`'1ns'`, `'0.5us'`) is refused: Alvo stores instants to the microsecond (`StoredInstant.Storable`) and the SQL domain is integer microseconds. Deviation from CEL's nanoseconds. — Cost: sub-microsecond durations are unexpressible.
9. **The overload set is exactly CEL's**: `ts + dur`, `dur + ts`, `ts - dur`, `ts - ts → dur`, `dur + dur`, `dur - dur`; `==`, `!=`, `<`, `<=`, `>`, `>=` on two durations. `*`, `/`, unary `-` on a duration, and mixing a duration with a number, are refused with a fix. A `date` field is a timestamp at midnight UTC (the marshaller's existing rule, `CelDateOperandTests`). — Inventing a variant of a standard is a defect (CLAUDE.md). — Cost: none beyond CEL's own limits.
10. **Range errors.** A timestamp result outside `DateTimeOffset.MinValue…MaxValue` (= CEL's range) or a duration result past ±315 576 000 000 s is `CelFunctionException("_+_" | "_-_", "the result is outside the range of a Timestamp" | "…of a Duration")`, on caller data in the fail-closed profiles (`Condition`, `Mutate` → 422 `expression-failed` per HX-10's E1 when the operands are the caller's), and `null` (→ comparison false) in `Rule`'s in-process evaluation, like numeric overflow there. SQL computes integer microseconds with no such bound, so the two can differ only for results before year 1 or after year 9999; the agreement property generates inside that range. — Cost: a rule over year-10000 arithmetic is decided differently by `USING` and `WITH CHECK` — unreachable with stored values.
11. **The SQL temporal domain is integer microseconds since the Unix epoch**, through `IFieldSqlRenderer.RenderEpochMicroseconds(string timestampSql)` (DIM, default `null` → the renderer throws `NotSupportedException` naming the renderer type, which an apply or a request reports). SQLite `CAST(round(unixepoch(x, 'subsec') * 1000000) AS INTEGER)`; PostgreSQL `CAST(EXTRACT(EPOCH FROM x) * 1000000 AS bigint)`; the T-SQL double `DATEDIFF_BIG(microsecond, '1970-01-01T00:00:00', x)` (golden only — §6.1 "TSqlSqlDialect double extended"). Integers make `==` on a duration exact (a `REAL` difference of two epoch seconds is not). **SQLite's date functions are millisecond-precise**, so on SQLite a temporal comparison reads stored microseconds rounded to the millisecond; PostgreSQL and the interpreter are exact. Deviation from §0 principle 3's "identical", stated like the decimal `REAL` divergence (#162): the property uses millisecond-aligned instants. Requires SQLite ≥ 3.42 (pinned by a fact) and PostgreSQL ≥ 14. — Cost: a row within 1 ms of a boundary may be judged differently by SQLite's `USING` and the in-process `WITH CHECK`.
12. **Temporal arithmetic is a construct kind, not a profile.** `CelConstructKind.TemporalArithmetic` is admitted in `Rule`, `Condition`, `Mutate`; numeric arithmetic stays refused in `Rule` (`hourly_rate * 2.0` remains a refused example). `ValidateSqlOperandShape` admits a temporal-arithmetic operand and `now()` in `Rule`. X7 holds: no `CelProfile` member, no node type. — Cost: the type table grows one row.

### `field.validation` → `CHECK`

13. **`validation` is a `Rule`-profile predicate over its own row, under a stored-predicate surface.** The descriptor mapper rewrites it into `FieldSchema.CheckExpression`: bare `value` → the declaring field, `new.<f>` → `<f>` (the frozen schema says "context: value, new"), written back canonically by `CelText`. Refused, at the `validation` pointer: `@user`, `@tenant`, a role test, `now()`, any function other than the folded `duration()`, `old.`, `changed()`. **Disagreement ruled:** the schema's "context: value, new" reads like a hook condition, the spec's "`field.validation` → `CHECK`" says a constraint over one row; a `CHECK` has one row, so `new.` is accepted as a spelling of the row and `old.`/`changed()` are refused. — Cost: an author who expected a transition rule (`old.status`) gets a refusal pointing at a before-hook.
14. **A field named `value` makes bare `value` ambiguous** on that entity: refused, naming both readings, fix "write the field's own name". — Silent binding either way is a constraint over the wrong column. — Cost: one rewrite for authors on such entities.
15. **The declaring field's NULL passes; everything else follows CEL.** The constraint is `(<subject> IS NULL OR <two-valued predicate>)`: SQL `CHECK` passes NULL, JSON Schema passes an absent value, and `required` is the facet for presence. Other operands go through the renderer's two-valued fold, so a comparison with another empty field is false → refused — the same answer the interpreter gives. — Cost: `ends_at` validated as `value > starts_at` refuses a row with `ends_at` and no `starts_at`; the docs show `!has(starts_at) || value > starts_at`.
16. **One literal writer for DDL.** `RenderConstraint` writes literals inline: integers and decimals invariant-culture (a negative is the unary node's `(-…)`), booleans the dialect's `TrueLiteral`/`FalseLiteral`, text through the dialect's existing `RenderStringLiteral` (control characters, separators and lone surrogates declined → refused at apply, as for `computed`), a duration as its integer microseconds. No parameter can be produced; a context value or `now()` reaching it throws. **Deviation** from the product spec's "translation never interpolates user input": these literals are the *descriptor author's*, never a request's, and acceptance (5) proves no literal escapes on either engine. — Cost: none known; the property is the guard.
17. **`IPredicateRenderer.RenderConstraint` is public (DIM, default throws).** §5 does not list it; the EF driver reaches the core's renderer only through this interface, and HX-12's `indexes[].where` is the second caller. — Cost: one interface member a third-party renderer may implement.
18. **`FieldSchema.CheckExpression` and `RollupSchema.Where` are public init members** — the schema model is how a provider learns what to emit (`ComputedExpression`, `EventImageExclusions` precedents; §5's HX-18/19 rows name "schema-model members" the same way). §5 row `11` omits them. — Cost: two members.
19. **Constraint names are computed, never parsed.** `ck_<entity>_<field>`, hashed past 63 bytes (PostgreSQL's identifier limit truncates silently, and a truncated name would no longer match the model). The owner of a decoded name is found by recomputing every validated field's name over the applied schema (`CheckConstraintNames.Owner`). — Cost: a rename re-creates the constraint (a SQLite rebuild).
20. **PostgreSQL: a plain `ADD CONSTRAINT … CHECK` inside the apply's one transaction — not `NOT VALID` + `VALIDATE`.** Deviation from spec §6.1: Alvo applies a descriptor atomically (`EfCoreSchemaMigrator.ExecuteVerifiedAsync`), and `VALIDATE` only avoids blocking writes when it commits in a *later* transaction; inside one transaction the `ACCESS EXCLUSIVE` lock of the add is held through the validation anyway. The pre-apply scan (Ruling 22) already gives the count `VALIDATE` would have failed on. — Cost: writes to that table wait for one validating scan during the apply.
21. **SQLite: EF Core's own table rebuild**, framed by the existing `MigrationBatchFraming` (foreign keys off around the rebuild, verified after), classified non-destructive with the rebuild cost note (as `GeneratedColumnAdds.RebuildNote`). — Cost: an apply copies the table under the write lock.
22. **The violation scan runs at plan time, so a dry run reports it.** For each added or changed constraint on a table that exists in the current schema and is neither created nor renamed in this plan: `SELECT COUNT(*) FROM <table> WHERE NOT (<check>)` across all tenants; a non-zero count refuses the plan with `DescriptorValidationException` at the validation's pointer. A constraint on a new/renamed table or column is guarded by the DDL alone, and an engine `CHECK` failure during `ApplyAsync` (rows written after the scan, a rename, a backfilled rollup that breaks a parent's validation) is translated into the same refusal without a count. — Cost: one counting scan per new constraint per plan.
23. **The count is operator-facing.** It crosses tenants and goes to a Management API caller (developer/admin), never to a Data API caller; it names no row, id or value. — Cost: a developer learns how many rows break a rule they are adding.
24. **A write that breaks a `CHECK` is 422 `validation`, code `check`, pointer at the validated field** (E2's `Invalid` → 422 `validation`). A field the descriptor carries any `hidden` flag for gets pointer `""` and is never named (Ruling X) — decided from `PolicyDecision.Unnameable`, the descriptor's flagged set, not the caller's mask. The message never echoes a value or the expression. — Cost: a caller learns which field to fix, not why.
25. **A `CHECK` broken through a rollup recompute points at the child's aggregated field.** An order line that pushes `products.reserved` past `stock` answers `/quantity` — the value the caller sent and can change — or `""` for a `count` rollup or an unnameable child field; the parent's field is never named. — Cost: a pointer to a field that is valid in isolation.
26. **A `CHECK` broken by a value a hook wrote is still 422.** The database cannot tell whose value it was, and the descriptor's integrity refused the row the caller asked for. Unlike HX-10's E1 (a function failing), this is a declared rule holding. — Cost: a caller cannot always fix it by changing their own body; recorded as accepted risk.
27. **`Check` follows `Unique` at every site** a constraint violation is translated or answered (single writes, batches, units, the replay loop). — Cost: none.
28. **No in-process pre-check.** The database is the one authority (race-free under concurrent writers — acceptance (1)); `InMemoryAlvoData` does not enforce `CHECK` (documented, like `computed`), and the facts live in a real-engine suite. — Cost: one validation failure at a time, the first the engine reports.
29. **Numeric arithmetic in a `validation` is refused** (the `Rule` surface): `quantity * unit_price <= 10000` waits for a follow-up issue. — Cost: such a rule goes in a computed field compared by a validation (`total <= 10000`).
30. **`validation` on a `storage: dynamic` entity is refused, naming #41** — in the validator (structured error at the pointer) and in the mapper (exception). A rollup from a dynamic child is already refused (`RollupResolver.EnsureChildIsPhysical`); a rollup *on* a dynamic entity stays covered by the existing drop-with-warning. — Spec §1.2/§6.1. — Cost: none.

### `rollup.where`

31. **`rollup.where` is a `Rule`-profile predicate over the child, under the stored-predicate surface**, rendered with `RenderConstraint` and ANDed into `RollupRecompute`'s subquery. Its columns are unqualified: the compiler guarantees each exists on the child, and the innermost scope binds first (unlike `tenant_id`, which the child may lack — the reason `ChildTenantPredicate` qualifies). The inlined text is passed through `RawSqlFormat.Escape` (`{` → `{{`, `}` → `}}`) because EF formats raw SQL with arguments. — Cost: none.
32. **An apply recomputes every rollup it adds or changes** (`from`, `op`, `field`, `via` or `where`) for every parent, in the apply's transaction: one correlated `UPDATE parent SET f = (SELECT … FROM child WHERE child.fk = parent.id [AND child.tenant_id = parent.tenant_id] [AND (where)])` after the DDL. This also fixes a rollup added to a populated parent, which until now read empty until a child write (CHANGELOG *Fixed*). — Cost: one full scan per changed rollup on apply.
33. **Acceptance (3)'s concurrency proof runs on PostgreSQL only** (`PostgreSqlRollupRaceTests`, window widened in-statement): SQLite admits one writer, so a shared race fact would assert nothing there — the existing suite's own reasoning. Moving a child across the filter (status `held` → `released`) is a both-engine fact. — Cost: none.

### Typed layer, host, docs, dashboard, process

34. **Typed `when`:** `DateTimeOffset.UtcNow` and `DateTimeOffset.Now` → `now()`; a `TimeSpan` constant (`const` cannot hold one; a `static readonly` field) → `duration('<seconds>s')`; `DateTimeOffset ± TimeSpan`, `DateTimeOffset - DateTimeOffset`, `TimeSpan ± TimeSpan` → CEL's operators. `DateTime.*`, `DateTimeOffset.Today`-shaped members, `TimeProvider.GetUtcNow/GetLocalNow` stay refused (no offset, or a host clock CEL does not have), with the fix "use DateTimeOffset.UtcNow". The query translator still refuses the clock ("capture it first"). **Disagreement ruled:** spec §4.3 says "`DateTimeOffset.UtcNow`/`.Now` → `now()`"; HX-05 (17) refused `DateTime(Offset).Now/UtcNow/Today` and `TimeProvider` together — only the two `DateTimeOffset` instants translate. — Cost: none.
35. **Host CEL functions get no `TimeSpan`.** `duration` stays a reserved host name (now a built-in's), and a host function taking or returning `TimeSpan` is refused as today. — Cost: a follow-up if a host needs one.
36. **`validation` is not published in OpenAPI.** A CEL predicate has no faithful JSON Schema form, and a partial translation would be a second truth. — Cost: an OpenAPI client learns of it from a 422.
37. **Dashboard:** the field editor gets a *Validation* box (every field kind but `computed`'s own expression box; rollup fields included — the oversell example validates a rollup) and the rollup section a *Where* box, both checked live through the existing `cel/check` endpoint and `ExpressionCheck`; the "refused" sentence, the "Remove the declared filter" checkbox and the two refusal badges go, because the slots leave `UnhonouredFeatures`. — Cost: none.
38. **Mutation is configuration only.** New core files land in the `expressions` shard (`**/Expressions/**`: `CelDuration.cs`, `CelTemporal.cs`, `StoredPredicateSurface.cs`) and the core shard (`Descriptor/Internal/FieldValidation.cs`, `CheckConstraintCheck.cs`, `RollupFilterCheck.cs`, `Api/Internal/RequestInstant.cs` is `**/Api/**` → `api`); EF files in `data-ef-rest` (`CheckConstraintNames.cs`, `CheckConstraintSql.cs`, `CheckViolationScan.cs`, `RollupBackfill.cs`, `RawSqlFormat.cs`) — `EfCoreSchemaMigrator.cs` stays excluded as today; `ConstraintViolationTranslator.cs` is in `data-ef-core`. No config changes; `scripts/test-assert-mutation-run` stays green.
39. **No load run.** No hot path changes cost: a rule without `now()` renders as before; the stamp change removes a clock read. — Cost: none measured.
40. **Golden files move and are judged.** `test/MMLib.Alvo.Tests/cel-to-sql-core.verified.txt` gains temporal rows, `Expressions/CelAcceptanceBaseline.jsonl` flips `now()` in `Rule` from refused to accepted, `UnhonouredFeaturesTests.*.verified.txt` lose `validation` and `rollup.where`, and the Abstractions public-API baseline grows by Global Constraints' list; each move goes to `alvo-snapshot-judge`.
41. **`InMemoryAlvoData` binds an instant too** (it is the reference implementation the contract suites drive): `EnsureInstant` at its public entry points, so a `now()` rule evaluates in memory exactly as in `EfAlvoData`.
42. **The skills change as product text.** `alvo-descriptor-rules-and-cel` (`cel-rule` region: `now()` and `created_at > now() - duration('24h')` move to allowed; "no function call" names the `duration('…')` constant), `alvo-descriptor-computed-and-rollups` (`where` documented; `cel-computed` keeps `now()` refused), `alvo-descriptor-entities-and-fields` (the `validation-refused` example becomes `validation-accepted`), and the generated `refused` list loses both slots. The eval case `reject_negative_price` accepts a `validation` as a passing answer beside a before-hook.
43. **Spec drift recorded, not silently fixed:** §6.1's `NOT VALID` + `VALIDATE` (Ruling 20), §5 row `11` (Rulings 6, 11, 17, 18), §4.3 vs HX-05 (17) (Ruling 34) — amended in Task 13.
44. **Follow-ups opened in Task 14:** numeric arithmetic in `validation` (Ruling 29); a time-windowed aggregate (Ruling 3, points at HX-13); `TimeSpan` for host functions (Ruling 35).

## File structure

**Abstractions** (`src/MMLib.Alvo.Abstractions`)
- `Expressions/CelValueType.cs` — `Duration` appended.
- `Expressions/IFieldSqlRenderer.cs` — `RenderEpochMicroseconds` DIM.
- `Expressions/IPredicateRenderer.cs` — `RenderConstraint` DIM.
- `Schema/FieldSchema.cs` — `CheckExpression`. `Schema/RollupSchema.cs` — `Where`.
- `Data/AlvoConstraintKind.cs` — `Check`. `Data/AlvoConstraintViolationException.cs` — `Check` message.
- `Identity/AlvoFlow.cs` — `Instant`, `EnterInstant`, `EnsureInstant`.
- `Rules/PolicyDecision.cs` — internal `Unnameable`.

**Core** (`src/MMLib.Alvo`)
- `Expressions/Internal/CelDuration.cs` (new) — grammar, range, canonical text.
- `Expressions/Internal/CelTemporal.cs` (new) — static typing of a subtree, arithmetic with range errors.
- `Expressions/Internal/StoredPredicateSurface.cs` (new) — refusals for a predicate with no caller and no clock.
- `Expressions/Internal/CelBuiltInFunctions.cs`, `CelTypeChecker.cs`, `CelInterpreter.cs`, `SqlPredicateRenderer.cs` (+ new partial `SqlPredicateRenderer.Temporal.cs`, `SqlPredicateRenderer.Constraint.cs`), `CelText.cs`.
- `Descriptor/Internal/FieldValidation.cs` (new) — the `value`/`new.` rewrite and the dynamic refusal.
- `Descriptor/Internal/CheckConstraintCheck.cs`, `RollupFilterCheck.cs` (new) — validator findings at their pointers.
- `Descriptor/Internal/UnhonouredFeatures.cs`, `RollupResolver.cs`, `DescriptorValidator.cs`, `Descriptor/DescriptorToSchemaMapper.cs`.
- `Api/Internal/RequestInstant.cs` (new); `AlvoContextFilter.cs`, `AlvoCallerFilter.cs`, `ProblemResultFactory.cs`; `Management/Internal/ManagementCallerFilter.cs`.
- `Rules/Internal/PolicyEngine*` (populate `Unnameable`); `Rules/Internal/BeforeHookRunner.cs`; `Events/Internal/EnvelopeReading.cs` (event time); `Extensions/Internal/ConditionTranslator*.cs`.

**EF driver** (`src/MMLib.Alvo.Data.EntityFrameworkCore`)
- `Internal/CheckConstraintNames.cs`, `CheckConstraintSql.cs`, `CheckViolationScan.cs`, `RollupBackfill.cs`, `RawSqlFormat.cs` (new).
- `Internal/DescriptorModelBuilder.cs`, `DestructiveScan.cs`, `ConstraintViolationTranslator.cs`, `RollupRecompute.cs`, `EfAlvoData*.cs`, `EfAlvoTransactions.cs`; `EfCoreSchemaMigrator.cs`; `AlvoEfCoreProvider.cs` (wiring).

**Dialects** — `src/MMLib.Alvo.Data.Sqlite/SqliteFieldSqlRenderer.cs`, `SqliteSqlDialect.cs`; `src/MMLib.Alvo.Data.PostgreSql/PostgreSqlFieldSqlRenderer.cs`, `PostgreSqlSqlDialect.cs`; `src/MMLib.Alvo.Testing.EntityFrameworkCore/TSqlSqlDialect.cs` (+ its field renderer).

**Testing** — `src/MMLib.Alvo.Testing/Data/AlvoDataCheckConstraintTests.cs` (new, abstract), `AlvoDataComputedRollupTests.cs`, `DifferentialRuleCases.cs`, `AlvoDataDifferentialTests.cs`, `InMemoryAlvoData.cs`.

**Admin** — `src/MMLib.Alvo.Admin/Components/Schema/FieldEditor.razor(.cs)`, `FieldFacets*.cs`, `ExpressionSlots.cs`, `FieldBadges.cs`, `RefusalPlaces.cs`; `test/MMLib.Alvo.Admin.Tests.EndToEnd/IntegrityEditorScenarios.cs` (new).

**Docs & skills** — `website/src/content/docs/extending/integrity.mdx` (new), `website/src/snippets/extending/integrity/{inventory.alvo.json,InventoryIntegrityTests.cs}` (new); `docs/architecture/cel.md`, `docs/architecture/data-path.md`; `CHANGELOG.md`; the spec; `plugins/alvo/skills/alvo-descriptor-{rules-and-cel,computed-and-rollups,entities-and-fields,capabilities-and-limits}/SKILL.md`; `test/_shared/ai/SkillCaseAnswers.cs` and the eval grader.

---
## Task 1: Durations in CEL — the value type, the literal, the arithmetic

**Files:**
- Modify: `src/MMLib.Alvo.Abstractions/Expressions/CelValueType.cs` (append `Duration`)
- Create: `src/MMLib.Alvo/Expressions/Internal/CelDuration.cs`, `src/MMLib.Alvo/Expressions/Internal/CelTemporal.cs`
- Modify: `src/MMLib.Alvo/Expressions/Internal/CelBuiltInFunctions.cs` (+ `CelBuiltInFunctions.Conversions.cs`), `CelTypeChecker.cs`, `CelInterpreter.cs`, `CelText.cs`, `CelCompiler.cs` (`IsScalar` unchanged — a duration is never a field value)
- Test: `test/MMLib.Alvo.Tests/Expressions/CelDurationTests.cs`, `CelTemporalArithmeticTests.cs` (new)
- Baselines: `test/MMLib.Alvo.Abstractions.Tests/PublicApi.MMLib.Alvo.Abstractions.verified.txt`, `test/MMLib.Alvo.Tests/Expressions/CelAcceptanceBaseline.jsonl` (only if the corpus lists a `duration(` row)

**Interfaces:**
- Produces: `CelValueType.Duration`; `CelDuration.TryParse(string text, out TimeSpan value, out string? reason)`, `CelDuration.Text(TimeSpan)` → `"<seconds>s"`, `CelDuration.MaxMagnitude` (`TimeSpan`, 315 576 000 000 s), `CelDuration.Fix` (string); `CelBuiltInFunctions.DurationName = "duration"`; `CelTemporal.TypeOf(CelNode) → CelValueType?` (Timestamp, Duration or null), `CelTemporal.IsTemporal(CelBinary) → bool`, `CelTemporal.Apply(CelBinaryOperator op, object? left, object? right, bool failClosed) → object?`, `CelTemporal.Instant(object) → DateTimeOffset?`; type-checker construct kinds `DurationLiteral` and `TemporalArithmetic` (rows `{Rule, Condition, Mutate}`).
- Consumes: `CelInterpreter.TryToDateTimeOffset` (existing), `CelFunctionException(name, reason)` and its `OnCallerData` classification (HX-10).

- [ ] **Step 1: Write the failing tests**

```csharp
// test/MMLib.Alvo.Tests/Expressions/CelDurationTests.cs
using MMLib.Alvo.Expressions.Internal;

namespace MMLib.Alvo.Tests.Expressions;

/// <summary>CEL's duration grammar (Go's ParseDuration), CEL's range, Alvo's microsecond precision (HX-11 Rulings 7, 8).</summary>
public sealed class CelDurationTests
{
    [Theory]
    [InlineData("0", 0L)]
    [InlineData("90m", 5_400_000_000L)]
    [InlineData("1h30m", 5_400_000_000L)]
    [InlineData("-1.5h", -5_400_000_000L)]
    [InlineData("+2s", 2_000_000L)]
    [InlineData("250ms", 250_000L)]
    [InlineData("1us", 1L)]
    [InlineData("1µs", 1L)]
    [InlineData("1μs", 1L)]
    [InlineData("1000ns", 1L)]
    [InlineData(".5s", 500_000L)]
    [InlineData("1.s", 1_000_000L)]
    [InlineData("315576000000s", 315_576_000_000_000_000L)]
    public void A_cel_duration_parses_to_its_microseconds(string text, long microseconds)
    {
        CelDuration.TryParse(text, out var value, out var reason).ShouldBeTrue(reason);
        (value.Ticks / 10).ShouldBe(microseconds);
    }

    [Theory]
    [InlineData("", "not a duration")]
    [InlineData("1", "not a duration")]
    [InlineData("1d", "not a duration")]
    [InlineData("h", "not a duration")]
    [InlineData("1h 30m", "not a duration")]
    [InlineData("--1s", "not a duration")]
    [InlineData(".s", "not a duration")]
    [InlineData("1ns", "microsecond")]
    [InlineData("0.5us", "microsecond")]
    [InlineData("315576000001s", "range")]
    [InlineData("-315576000001s", "range")]
    public void Anything_else_is_refused_with_its_reason(string text, string reason)
    {
        CelDuration.TryParse(text, out _, out var why).ShouldBeFalse();
        why.ShouldNotBeNull().ShouldContain(reason);
    }

    [Theory]
    [InlineData("90m", "5400s")]
    [InlineData("250ms", "0.25s")]
    [InlineData("-1us", "-0.000001s")]
    [InlineData("0", "0s")]
    public void The_canonical_text_is_seconds(string text, string canonical)
    {
        CelDuration.TryParse(text, out var value, out _).ShouldBeTrue();
        CelDuration.Text(value).ShouldBe(canonical);
        CelDuration.TryParse(canonical, out var again, out _).ShouldBeTrue();
        again.ShouldBe(value);
    }
}
```

```csharp
// test/MMLib.Alvo.Tests/Expressions/CelTemporalArithmeticTests.cs
using MMLib.Alvo.Expressions;
using MMLib.Alvo.Expressions.Internal;
using MMLib.Alvo.Schema;

namespace MMLib.Alvo.Tests.Expressions;

/// <summary>CEL's timestamp/duration overloads, typed at compile time and evaluated with CEL's range errors (HX-11 Rulings 7, 9, 10, 12).</summary>
public sealed class CelTemporalArithmeticTests
{
    private static readonly DateTimeOffset _start = new(2026, 11, 9, 10, 0, 0, TimeSpan.Zero);

    private static readonly EntitySchema _bookings = new()
    {
        Name = "bookings",
        Fields =
        [
            new FieldSchema { Name = "starts_at", Type = FieldType.DateTime, Nullable = true },
            new FieldSchema { Name = "ends_at", Type = FieldType.DateTime, Nullable = true },
            new FieldSchema { Name = "due_on", Type = FieldType.Date, Nullable = true },
            new FieldSchema { Name = "hours", Type = FieldType.Integer, Nullable = true },
        ],
    };

    private static CelCompilationResult Compile(string source, CelProfile profile) =>
        CelFixtures.Compiler.Compile(source, profile, _bookings);

    [Theory]
    [InlineData("ends_at - starts_at <= duration('8h')", CelProfile.Rule)]
    [InlineData("starts_at + duration('1h') < ends_at", CelProfile.Rule)]
    [InlineData("duration('1h') + starts_at < ends_at", CelProfile.Rule)]
    [InlineData("ends_at - duration('30m') > starts_at", CelProfile.Rule)]
    [InlineData("duration('1h') + duration('30m') == duration('90m')", CelProfile.Rule)]
    [InlineData("new.ends_at - new.starts_at > duration('0')", CelProfile.Condition)]
    [InlineData("due_on + duration('24h') > starts_at", CelProfile.Rule)]
    public void Cel_s_overloads_compile_where_temporal_arithmetic_is_admitted(string source, CelProfile profile) =>
        Compile(source, profile).IsSuccess.ShouldBeTrue();

    [Theory]
    [InlineData("starts_at + duration('1h')", CelProfile.Mutate, CelValueType.Timestamp)]
    public void A_mutate_value_may_be_a_shifted_timestamp(string source, CelProfile profile, CelValueType type) =>
        Compile(source, profile).Expression!.ResultType.ShouldBe(type);

    [Theory]
    [InlineData("duration('1h') * 2 == duration('2h')", "'*'")]
    [InlineData("hours + duration('1h') > duration('0')", "number")]
    [InlineData("starts_at + starts_at > ends_at", "two timestamps")]
    [InlineData("-duration('1h') < duration('0')", "'-'")]
    [InlineData("ends_at - starts_at", "boolean")]
    public void Anything_outside_the_overload_set_is_refused(string source, string reason)
    {
        var result = Compile(source, CelProfile.Rule);
        result.IsSuccess.ShouldBeFalse();
        string.Join(" ", result.Errors.Select(error => error.Message)).ShouldContain(reason);
    }

    [Fact]
    public void A_duration_from_a_field_is_refused_because_duration_takes_a_constant()
    {
        var result = CelFixtures.Compiler.Compile("starts_at + duration(string(hours) + 'h') < ends_at", CelProfile.Condition, _bookings);
        result.Errors.ShouldContain(error => error.Message.Contains("constant", StringComparison.Ordinal));
    }

    [Fact]
    public void Numeric_arithmetic_stays_refused_in_a_rule() =>
        Compile("hours * 2 > 4", CelProfile.Rule).IsSuccess.ShouldBeFalse();

    [Fact]
    public void A_duration_is_folded_into_a_typed_literal() =>
        Compile("ends_at - starts_at <= duration('8h')", CelProfile.Rule).Expression!.Root
            .ShouldBeOfType<CelBinary>().Right.ShouldBe(new CelLiteral(CelValueType.Duration, TimeSpan.FromHours(8)));

    [Theory]
    [InlineData(9, true)]
    [InlineData(7, false)]
    public void The_interpreter_subtracts_two_timestamps_into_a_duration(int hours, bool longerThanEight)
    {
        var condition = Compile("new.ends_at - new.starts_at > duration('8h')", CelProfile.Condition).Expression!;
        var row = CelFixtures.Row(("starts_at", _start), ("ends_at", _start.AddHours(hours)));
        CelInterpreter.EvaluatePredicate(condition, row, null, AlvoContext.Anonymous).ShouldBe(longerThanEight);
    }

    [Fact]
    public void A_date_counts_as_midnight_utc()
    {
        var condition = Compile("new.due_on + duration('12h') == new.starts_at", CelProfile.Condition).Expression!;
        var row = CelFixtures.Row(("due_on", new DateOnly(2026, 11, 9)), ("starts_at", new DateTimeOffset(2026, 11, 9, 12, 0, 0, TimeSpan.Zero)));
        CelInterpreter.EvaluatePredicate(condition, row, null, AlvoContext.Anonymous).ShouldBeTrue();
    }

    [Fact]
    public void A_timestamp_past_year_9999_is_a_range_error_on_the_fail_closed_path()
    {
        var mutate = Compile("starts_at + duration('315576000000s')", CelProfile.Mutate).Expression!;
        var failure = Should.Throw<CelFunctionException>(() =>
            CelInterpreter.EvaluateMutation(mutate, CelFixtures.Row(("starts_at", new DateTimeOffset(9000, 1, 1, 0, 0, 0, TimeSpan.Zero))), null, _start));
        failure.Message.ShouldContain("outside the range of a Timestamp");
    }

    [Fact]
    public void A_range_error_in_a_rule_s_in_process_evaluation_is_false_like_an_overflow()
    {
        var rule = Compile("starts_at + duration('315576000000s') > ends_at", CelProfile.Rule).Expression!;
        var row = CelFixtures.Row(("starts_at", new DateTimeOffset(9000, 1, 1, 0, 0, 0, TimeSpan.Zero)), ("ends_at", _start));
        CelInterpreter.EvaluatePredicate(rule, row, null, AlvoContext.Anonymous).ShouldBeFalse();
    }

    [Fact]
    public void An_empty_operand_makes_the_comparison_false()
    {
        var rule = Compile("ends_at - starts_at <= duration('8h')", CelProfile.Rule).Expression!;
        CelInterpreter.EvaluatePredicate(rule, CelFixtures.Row(("ends_at", _start)), null, AlvoContext.Anonymous).ShouldBeFalse();
    }
}
```

- [ ] **Step 2: Run them to verify they fail**

Run: `dotnet test --project test/MMLib.Alvo.Tests --filter-class '*CelDurationTests' --filter-class '*CelTemporalArithmeticTests'`
Expected: build FAIL — `CelDuration` and `CelValueType.Duration` do not exist.

- [ ] **Step 3: Append the value type**

In `CelValueType.cs`, after `Null`:

```csharp
    /// <summary>
    /// A signed length of time, built only from a constant such as <c>duration('90m')</c>, at microsecond precision,
    /// within about ten thousand years either way. The difference of two timestamps, and what a timestamp moves by.
    /// </summary>
    Duration,
```

- [ ] **Step 4: Write `CelDuration`**

```csharp
// src/MMLib.Alvo/Expressions/Internal/CelDuration.cs
using System.Globalization;

namespace MMLib.Alvo.Expressions.Internal;

/// <summary>
/// CEL's <c>duration(string)</c>: Go's <c>time.ParseDuration</c> grammar — an optional sign, then one or more
/// <c>&lt;decimal&gt;&lt;unit&gt;</c> components (<c>h m s ms us µs μs ns</c>), or a bare <c>0</c> — within CEL's range of
/// ±315 576 000 000 seconds, at the microsecond precision Alvo stores instants with.
/// </summary>
internal static class CelDuration
{
    internal static readonly TimeSpan MaxMagnitude = TimeSpan.FromSeconds(315_576_000_000);

    internal const string Fix = "Write a CEL duration such as '90m', '1h30m', '-1.5h' or '250ms' (units h, m, s, ms, us, ns).";

    private const string Grammar = "it is not a duration — one or more numbers each followed by a unit, such as 1h30m";
    private const string Precision = "it is not a whole number of microseconds, the precision Alvo stores instants with";
    private const string Range = "it is outside the range of a Duration, about ten thousand years either way";
    private const decimal MaxNanoseconds = 315_576_000_000m * 1_000_000_000m;

    private static readonly (string Unit, decimal Nanoseconds)[] _units =
    [
        ("h", 3_600_000_000_000m), ("ms", 1_000_000m), ("m", 60_000_000_000m), ("s", 1_000_000_000m),
        ("us", 1_000m), ("µs", 1_000m), ("μs", 1_000m), ("ns", 1m),
    ];

    internal static bool TryParse(string text, out TimeSpan value, out string? reason)
    {
        ArgumentNullException.ThrowIfNull(text);
        value = TimeSpan.Zero;
        var (negative, body) = Signed(text);
        if (body == "0")
        {
            reason = null;
            return true;
        }

        return TrySum(body, out var nanoseconds, out reason) && TryTicks(nanoseconds, negative, out value, out reason);
    }

    internal static string Text(TimeSpan value) =>
        (value.Ticks / (decimal)TimeSpan.TicksPerSecond).ToString("0.######", CultureInfo.InvariantCulture) + "s";

    private static (bool Negative, string Body) Signed(string text) =>
        text.Length > 0 && text[0] is '-' or '+' ? (text[0] == '-', text[1..]) : (false, text);

    private static bool TrySum(string body, out decimal nanoseconds, out string? reason)
    {
        nanoseconds = 0m;
        reason = Grammar;
        var at = 0;
        while (at < body.Length)
        {
            if (!TryComponent(body, ref at, out var component))
            {
                return false;
            }

            nanoseconds += component;
            if (nanoseconds > MaxNanoseconds)
            {
                reason = Range;
                return false;
            }
        }

        reason = body.Length > 0 ? null : Grammar;
        return body.Length > 0;
    }

    private static bool TryComponent(string body, ref int at, out decimal nanoseconds)
    {
        nanoseconds = 0m;
        var start = at;
        while (at < body.Length && (char.IsAsciiDigit(body[at]) || body[at] == '.'))
        {
            at++;
        }

        var number = body[start..at];
        return IsNumber(number)
            && TryUnit(body, ref at, out var perUnit)
            && decimal.TryParse(Padded(number), NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var amount)
            && TryMultiply(amount, perUnit, out nanoseconds);
    }

    private static bool IsNumber(string number) =>
        number.Length is > 0 and <= 30 && number.Count(character => character == '.') <= 1 && number != ".";

    private static string Padded(string number) => number[^1] == '.' ? number + "0" : number[0] == '.' ? "0" + number : number;

    private static bool TryUnit(string body, ref int at, out decimal perUnit)
    {
        foreach (var (unit, nanoseconds) in _units)
        {
            if (string.CompareOrdinal(body, at, unit, 0, unit.Length) == 0 && !char.IsAsciiLetterLower(At(body, at + unit.Length)))
            {
                at += unit.Length;
                perUnit = nanoseconds;
                return true;
            }
        }

        perUnit = 0m;
        return false;
    }

    private static char At(string body, int index) => index < body.Length ? body[index] : '\0';

    private static bool TryMultiply(decimal amount, decimal perUnit, out decimal nanoseconds)
    {
        nanoseconds = amount <= MaxNanoseconds / perUnit ? amount * perUnit : MaxNanoseconds + 1m;
        return true;
    }

    private static bool TryTicks(decimal nanoseconds, bool negative, out TimeSpan value, out string? reason)
    {
        value = TimeSpan.Zero;
        reason = nanoseconds > MaxNanoseconds ? Range : nanoseconds % 1_000m != 0m ? Precision : null;
        if (reason is not null)
        {
            return false;
        }

        var ticks = (long)(nanoseconds / 100m);
        value = TimeSpan.FromTicks(negative ? -ticks : ticks);
        return true;
    }
}
```

`"ms"` precedes `"m"` in `_units`, and `TryUnit` refuses a unit followed by another lower-case letter, so `"1mo"` and `"1d"` fail the grammar rather than reading `"m"`.

- [ ] **Step 5: Write `CelTemporal`**

```csharp
// src/MMLib.Alvo/Expressions/Internal/CelTemporal.cs
namespace MMLib.Alvo.Expressions.Internal;

/// <summary>
/// CEL's timestamp and duration overloads — <c>ts ± dur</c>, <c>dur + ts</c>, <c>ts - ts</c>, <c>dur ± dur</c> — typed from a
/// checked tree's leaves (no new node, X7) and evaluated with CEL's range errors (HX-11 Rulings 9, 10).
/// </summary>
internal static class CelTemporal
{
    internal const string TimestampRange = "the result is outside the range of a Timestamp";
    internal const string DurationRange = "the result is outside the range of a Duration";

    internal static CelValueType? TypeOf(CelNode node) => node switch
    {
        CelLiteral { Type: CelValueType.Duration or CelValueType.Timestamp } literal => literal.Type,
        CelFieldRef { Type: CelValueType.Timestamp } => CelValueType.Timestamp,
        CelCall { ResultType: CelValueType.Timestamp } => CelValueType.Timestamp,
        CelBinary binary when IsTemporal(binary) => ResultOf(binary),
        _ => null,
    };

    internal static bool IsTemporal(CelBinary binary) =>
        binary.Operator is CelBinaryOperator.Add or CelBinaryOperator.Subtract
        && (TypeOf(binary.Left) is not null || TypeOf(binary.Right) is not null);

    private static CelValueType? ResultOf(CelBinary binary) => (TypeOf(binary.Left), TypeOf(binary.Right)) switch
    {
        (CelValueType.Timestamp, CelValueType.Timestamp) => CelValueType.Duration,
        (CelValueType.Duration, CelValueType.Duration) => CelValueType.Duration,
        _ => CelValueType.Timestamp,
    };

    internal static object? Apply(CelBinaryOperator op, object? left, object? right, bool failClosed)
    {
        if (left is null || right is null)
        {
            return null;
        }

        var result = Compute(op, left, right);
        return result.Value ?? (failClosed ? throw new CelFunctionException(Name(op), result.Reason!) : null);
    }

    internal static DateTimeOffset? Instant(object value) =>
        value is not TimeSpan && CelInterpreter.TryToDateTimeOffset(value, out var instant) ? instant : null;

    private static (object? Value, string? Reason) Compute(CelBinaryOperator op, object left, object right) => (left, right) switch
    {
        (TimeSpan l, TimeSpan r) => Duration(op == CelBinaryOperator.Add ? l.Ticks + (decimal)r.Ticks : l.Ticks - (decimal)r.Ticks),
        (TimeSpan d, _) when Instant(right) is { } t => Shift(t, d.Ticks),
        (_, TimeSpan d) when Instant(left) is { } t => Shift(t, op == CelBinaryOperator.Add ? d.Ticks : -(decimal)d.Ticks),
        _ when Instant(left) is { } a && Instant(right) is { } b => Duration(a.UtcTicks - (decimal)b.UtcTicks),
        _ => (null, "an operand is neither a Timestamp nor a Duration"),
    };

    private static (object?, string?) Shift(DateTimeOffset instant, decimal ticks)
    {
        var target = instant.UtcTicks + ticks;
        return target >= DateTimeOffset.MinValue.UtcTicks && target <= DateTimeOffset.MaxValue.UtcTicks
            ? (new DateTimeOffset((long)target, TimeSpan.Zero), null)
            : (null, TimestampRange);
    }

    private static (object?, string?) Duration(decimal ticks) =>
        Math.Abs(ticks) <= CelDuration.MaxMagnitude.Ticks ? (TimeSpan.FromTicks((long)ticks), null) : (null, DurationRange);

    private static string Name(CelBinaryOperator op) => op == CelBinaryOperator.Add ? "_+_" : "_-_";
}
```

`CelFunctionException(name, reason)` is the built-in/operator refusal constructor HX-10 classifies as caller data (`OnCallerData`), exactly as `CelHookArithmetic.Failure` uses it; read `CelHookArithmetic.Failure` and use the same constructor and the same `CelDataOrigin` attribution path.

- [ ] **Step 6: Register `duration` and fold it in the type checker**

In `CelBuiltInFunctions.cs`: add `internal const string DurationName = "duration";`, `internal static IReadOnlySet<CelProfile> RuleConditionAndMutate => new HashSet<CelProfile> { CelProfile.Rule, CelProfile.Condition, CelProfile.Mutate };`, and `Duration` to `All`:

```csharp
    private static CelFunction Duration => new(
        DurationName, [Parameter("text", CelValueType.String)], CelValueType.Duration, ResultNullable: false,
        "A length of time written as a constant, such as duration('90m') or duration('1h30m'): units h, m, s, ms, us and ns, "
        + "an optional sign and fractions; whole microseconds, within about ten thousand years.",
        IsHost: false, RuleConditionAndMutate, Body: arguments => CelDuration.TryParse((string)arguments[0]!, out var value, out var reason)
            ? value
            : throw new CelFunctionException(DurationName, reason!));
```

(Match `CelFunction`'s positional constructor exactly as `Now` and `InProcess` build it — read `CelFunction.cs` first; `ClrTypeOf` gains `CelValueType.Duration => typeof(TimeSpan)`.)

In `CelTypeChecker.cs`:
- add `DurationLiteral` and `TemporalArithmetic` to `CelConstructKind`, and the set `_ruleConditionAndMutate = { Rule, Condition, Mutate }`; table rows `[CelConstructKind.DurationLiteral] = _ruleConditionAndMutate`, `[CelConstructKind.TemporalArithmetic] = _ruleConditionAndMutate`;
- `CheckCall` routes `DurationName` before the catalog:

```csharp
        private (CelNode, CelValueType, bool, int) CheckCall(CelCall call) => call.Name switch
        {
            CelCall.Now => CheckLegacyCall(call),
            CelBuiltInFunctions.DurationName => CheckDurationCall(call),
            _ => CheckCatalogCall(call),
        };

        private (CelNode, CelValueType, bool, int) CheckDurationCall(CelCall call)
        {
            var position = FindPosition(CelBuiltInFunctions.DurationName);
            var profileBad = CheckConstruct(
                CelConstructKind.DurationLiteral, $"duration() is not legal in the {profile} profile.", DurationProfileFix, position);
            if (call.Arguments is not [CelLiteral { Type: CelValueType.String, Value: string text }])
            {
                Errors.Add(new CelCompilationError(
                    "duration() takes one text constant, such as duration('90m'); a duration is never computed from a field.",
                    CelDuration.Fix, position));
                return (call, CelValueType.Duration, true, position);
            }

            if (!CelDuration.TryParse(text, out var value, out var reason))
            {
                Errors.Add(new CelCompilationError($"duration('{text}') is refused: {reason}.", CelDuration.Fix, position));
                return (call, CelValueType.Duration, true, position);
            }

            return (new CelLiteral(CelValueType.Duration, value), CelValueType.Duration, profileBad, position);
        }
```

  `DurationProfileFix`: `"A computed column and an access level carry no duration; compare durations in a rule, a hook condition or a mutate."` The echoed `text` goes through the compiler's existing echo sanitiser if `CelCompilationError` messages are echoed elsewhere — reuse whatever `CelCompiler.Echo` the type checker already uses for constants (search `Echo(` in the checker).
- `CheckArithmetic` gains a first branch: when either operand type is `Timestamp` or `Duration` (and not concatenation), `CheckTemporalArithmetic`:

```csharp
        private (CelNode, CelValueType, bool, int) CheckTemporalArithmetic(
            CelBinary binary, CelValueType leftType, CelValueType rightType, bool operandError, int position)
        {
            var profileBad = CheckConstruct(
                CelConstructKind.TemporalArithmetic,
                $"Timestamp and duration arithmetic is not legal in the {profile} profile.",
                "Compare timestamps directly, or move the arithmetic into a rule, a hook condition or a mutate.",
                position);
            var result = TemporalResult(binary.Operator, leftType, rightType);
            if (result is null && !operandError)
            {
                Errors.Add(new CelCompilationError(TemporalRefusal(binary.Operator, leftType, rightType), TemporalFix, position));
            }

            return (binary, result ?? CelValueType.Null, operandError || profileBad || result is null, position);
        }

        private static CelValueType? TemporalResult(CelBinaryOperator op, CelValueType left, CelValueType right) => (op, left, right) switch
        {
            (CelBinaryOperator.Add, CelValueType.Timestamp, CelValueType.Duration) => CelValueType.Timestamp,
            (CelBinaryOperator.Add, CelValueType.Duration, CelValueType.Timestamp) => CelValueType.Timestamp,
            (CelBinaryOperator.Subtract, CelValueType.Timestamp, CelValueType.Duration) => CelValueType.Timestamp,
            (CelBinaryOperator.Subtract, CelValueType.Timestamp, CelValueType.Timestamp) => CelValueType.Duration,
            (CelBinaryOperator.Add or CelBinaryOperator.Subtract, CelValueType.Duration, CelValueType.Duration) => CelValueType.Duration,
            _ => null,
        };
```

  `TemporalRefusal` names the shape: `'*'`/`'/'` → `"'*' and '/' are not defined on a timestamp or a duration."`; a number beside a duration → `"A duration cannot be added to or subtracted from a number; write the number as a duration constant, such as duration('2h')."`; two timestamps added → `"Two timestamps cannot be added; subtract them to get the duration between them."`. `TemporalFix`: `"CEL defines timestamp ± duration, duration + timestamp, timestamp - timestamp and duration ± duration."` The unary negation check refuses a `Duration` operand with `"'-' is not defined on a duration; write the negative constant, such as duration('-1h')."`
- `ValidateComparisonTypes`: `Duration` vs `Duration` is a legal relational pair (it falls through the existing "same type" rule; make sure `IsRelationRejected` does not list it).
- `ValidateSqlOperandShape`: `IsSqlOperand` also admits a `CelBinary` for which `CelTemporal.IsTemporal` holds and whose leaves are themselves SQL operands, and a `CelCall { Name: CelCall.Now }`.
- The computed-field and mutate result checks: a `Duration` result is not a scalar a column holds (`IsScalar` stays as it is), so `computed`/`mutate` producing a duration is refused by the existing message.

In `CelInterpreter.EvaluateBinary`, route temporal binaries before numeric arithmetic:

```csharp
        CelBinaryOperator.Add or CelBinaryOperator.Subtract when CelTemporal.IsTemporal(binary) =>
            CelTemporal.Apply(binary.Operator, Evaluate(binary.Left, state), Evaluate(binary.Right, state), state.FailClosed),
```

and teach the comparison helpers durations: `IsDirectlyComparable` admits `TimeSpan`; `ValuesEqualCore`/`CompareOrder` gain a `(TimeSpan l, TimeSpan r)` arm. `CelText.Write` writes a `Duration` literal as `duration('<CelDuration.Text>')` (so the canonical writer and the parser round-trip, HX-05 (22)).

- [ ] **Step 7: Run the tests, then the CEL suites**

Run: `dotnet test --project test/MMLib.Alvo.Tests --filter-class '*CelDurationTests' --filter-class '*CelTemporalArithmeticTests' --filter-class '*CelTypeChecker*' --filter-class '*CelTextTests' --filter-class '*CelAcceptanceCorpusTests' --filter-class '*CelProfileTests'`
Expected: PASS. If `CelAcceptanceCorpusTests` reports a changed verdict, it is a `duration(` row going from refused (unknown function) to accepted — confirm that is the only kind of change, regenerate the baseline as the test's own failure message instructs, and dispatch `alvo-snapshot-judge` for it.

- [ ] **Step 8: Accept the Abstractions public-API baseline**

Run: `dotnet test --project test/MMLib.Alvo.Abstractions.Tests --filter-class '*PublicApiApprovalTests'`. The received diff adds exactly `Duration = 9` (after `Null = 8`) to `CelValueType`. Move it over the verified file; dispatch `alvo-snapshot-judge`; justify against Ruling 6.

- [ ] **Step 9: Format, ring0, commit**

```bash
dotnet format whitespace MMLib.Alvo.slnx --include src/MMLib.Alvo.Abstractions/Expressions/CelValueType.cs src/MMLib.Alvo/Expressions/Internal/CelDuration.cs src/MMLib.Alvo/Expressions/Internal/CelTemporal.cs src/MMLib.Alvo/Expressions/Internal/CelBuiltInFunctions.cs src/MMLib.Alvo/Expressions/Internal/CelTypeChecker.cs src/MMLib.Alvo/Expressions/Internal/CelInterpreter.cs src/MMLib.Alvo/Expressions/Internal/CelText.cs test/MMLib.Alvo.Tests/Expressions/CelDurationTests.cs test/MMLib.Alvo.Tests/Expressions/CelTemporalArithmeticTests.cs
scripts/test-ring0
git add <the files above> test/MMLib.Alvo.Abstractions.Tests/PublicApi.MMLib.Alvo.Abstractions.verified.txt
git commit -m "feat(cel): add CEL durations and timestamp arithmetic" -m "Claude-Session: https://claude.ai/code/session_01NJAtafM29iddLwfhb5gnmB"
```

---

## Task 2: `now()` — one instant per request, in every row profile

**Files:**
- Modify: `src/MMLib.Alvo.Abstractions/Identity/AlvoFlow.cs`
- Create: `src/MMLib.Alvo/Api/Internal/RequestInstant.cs`
- Modify: `src/MMLib.Alvo/Api/Internal/AlvoContextFilter.cs`, `AlvoCallerFilter.cs`, `src/MMLib.Alvo/Management/Internal/ManagementCallerFilter.cs`, `src/MMLib.Alvo/Expressions/Internal/CelBuiltInFunctions.cs` (`Now` profiles), `CelTypeChecker.cs` (`Call` row), `CelInterpreter.cs` (`now()` source), `src/MMLib.Alvo/Events/Internal/EnvelopeReading.cs` (event time), `src/MMLib.Alvo.Data.EntityFrameworkCore/Internal/EfAlvoData.cs` (+ `.Units.cs`, `.Hooks.cs`: `EnsureInstant` at every public entry, `WriteInstantNow`), `EfAlvoTransactions.cs`, `src/MMLib.Alvo.Testing/Data/InMemoryAlvoData.cs`
- Test: `test/MMLib.Alvo.Abstractions.Tests/AlvoFlowInstantTests.cs` (new), `test/MMLib.Alvo.Tests/Expressions/CelNowTests.cs` (new), `test/_shared/testhost/NowInstantFacts.cs` (new, linked into both TestHost legs — copy the `<Compile Include>` line `TypedLayerFacts.cs` has in each leg's csproj), `test/_shared/testhost/descriptors/now-instant.alvo.json` (new)

**Interfaces:**
- Produces: `AlvoFlow.Instant` (`DateTimeOffset?`), `AlvoFlow.EnterInstant(DateTimeOffset instant) → IDisposable` (binds, truncated to the microsecond, restores on dispose), `AlvoFlow.EnsureInstant(TimeProvider time) → IDisposable` (binds `time.GetUtcNow()` only when none is bound, else a no-op); `RequestInstant.Enter(HttpContext) → IDisposable`.
- Consumes: Task 1's construct table.

- [ ] **Step 1: Write the failing tests**

```csharp
// test/MMLib.Alvo.Abstractions.Tests/AlvoFlowInstantTests.cs
using Microsoft.Extensions.Time.Testing;

namespace MMLib.Alvo.Abstractions.Tests;

public sealed class AlvoFlowInstantTests
{
    private static readonly DateTimeOffset _t = new(2026, 11, 9, 10, 0, 0, TimeSpan.Zero);

    [Fact]
    public void An_instant_is_bound_to_the_microsecond_and_restored()
    {
        using (AlvoFlow.EnterInstant(_t.AddTicks(7)))
        {
            AlvoFlow.Instant.ShouldBe(_t);
        }

        AlvoFlow.Instant.ShouldBeNull();
    }

    [Fact]
    public void Ensure_binds_only_when_nothing_is_bound()
    {
        var clock = new FakeTimeProvider(_t);
        using (AlvoFlow.EnterInstant(_t.AddHours(-1)))
        using (AlvoFlow.EnsureInstant(clock))
        {
            AlvoFlow.Instant.ShouldBe(_t.AddHours(-1));
        }

        using (AlvoFlow.EnsureInstant(clock))
        {
            clock.Advance(TimeSpan.FromSeconds(5));
            AlvoFlow.Instant.ShouldBe(_t);
        }
    }

    [Fact]
    public async Task The_instant_flows_into_awaited_work()
    {
        using var bound = AlvoFlow.EnterInstant(_t);
        await Task.Yield();
        AlvoFlow.Instant.ShouldBe(_t);
    }
}
```

```csharp
// test/MMLib.Alvo.Tests/Expressions/CelNowTests.cs
using MMLib.Alvo.Expressions;
using MMLib.Alvo.Expressions.Internal;

namespace MMLib.Alvo.Tests.Expressions;

public sealed class CelNowTests
{
    private static readonly DateTimeOffset _t = new(2026, 11, 9, 10, 0, 0, TimeSpan.Zero);

    [Theory]
    [InlineData(CelProfile.Rule)]
    [InlineData(CelProfile.Condition)]
    [InlineData(CelProfile.Mutate)]
    public void Now_compiles_in_every_row_profile(CelProfile profile) =>
        CelFixtures.Compiler.Compile(profile == CelProfile.Mutate ? "now()" : "created_at <= now()", profile, CelFixtures.Orders)
            .IsSuccess.ShouldBeTrue();

    [Theory]
    [InlineData(CelProfile.Computed)]
    [InlineData(CelProfile.Access)]
    public void Now_is_refused_where_there_is_no_instant_to_bind(CelProfile profile) =>
        CelFixtures.Compiler.Compile("now()", profile, CelFixtures.Orders).IsSuccess.ShouldBeFalse();

    [Fact]
    public void A_rule_reads_the_bound_instant()
    {
        var rule = CelFixtures.CompileRule("created_at <= now()");
        var row = CelFixtures.Row(("created_at", _t));
        using (AlvoFlow.EnterInstant(_t))
        {
            CelFixtures.Evaluator.Evaluate(rule, row, null, CelFixtures.Alice).ShouldBeTrue();
        }

        using (AlvoFlow.EnterInstant(_t.AddSeconds(-1)))
        {
            CelFixtures.Evaluator.Evaluate(rule, row, null, CelFixtures.Alice).ShouldBeFalse();
        }
    }

    [Fact]
    public void Now_with_no_bound_instant_fails_closed_and_reads_no_clock()
    {
        var condition = CelFixtures.CompileCondition("new.created_at <= now()");
        Should.Throw<CelFunctionException>(() =>
            CelInterpreter.EvaluatePredicate(condition, CelFixtures.Row(("created_at", _t)), null, CelFixtures.Alice))
            .OnCallerData.ShouldBeFalse();
    }

    [Fact]
    public void A_mutate_s_explicit_instant_wins_over_the_flow_s()
    {
        var mutate = CelFixtures.CompileMutate("now()");
        using var bound = AlvoFlow.EnterInstant(_t.AddDays(1));
        CelInterpreter.EvaluateMutation(mutate, CelFixtures.Row(), null, _t).ShouldBe(_t);
    }
}
```

(`CelFixtures.Orders` must have a `created_at` timestamp field; if it does not, add `new FieldSchema { Name = "created_at", Type = FieldType.DateTime, Nullable = true }` to the fixture entity.)

```json
// test/_shared/testhost/descriptors/now-instant.alvo.json
{
  "$schema": "https://alvo.dev/schema/v1/project.json",
  "apiVersion": "alvo.dev/v1",
  "name": "now-instant",
  "description": "One now() per request (HX-11 acceptance (4)): a rule, a before-hook condition and its mutate, and a write hook's when all read now(), while host hooks move the clock in between.",
  "auth": { "providers": ["local"], "roles": ["clerk"] },
  "entities": {
    "notes": {
      "audit": true,
      "fields": {
        "body": { "type": "string", "maxLength": 200 },
        "stamped_at": { "type": "datetime" },
        "judged_at": { "type": "datetime" }
      },
      "hooks": {
        "beforeCreate": [
          { "condition": "now() == now()", "mutate": { "stamped_at": "now()" } }
        ]
      },
      "rules": {
        "list": "created_at <= now()",
        "get": "created_at <= now()",
        "create": "true",
        "update": "true",
        "delete": "true"
      }
    }
  }
}
```

(Read `alvo-descriptor-hooks` and an existing `hooks.beforeCreate` descriptor in `test/_shared/api/descriptors/hook-dates.alvo.json` and copy its exact `mutate` shape — the snippet above shows intent; the key spelling must be the schema's.)

```csharp
// test/_shared/testhost/NowInstantFacts.cs — both TestHost legs
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;
using MMLib.Alvo.Data;
using MMLib.Alvo.Hooks;
using MMLib.Alvo.TestHost;

namespace MMLib.Alvo.TestHost.Tests;

/// <summary>HX-11 acceptance (4): one now() instant per request, whatever the clock does while the write runs.</summary>
public abstract partial class NowInstantFacts
{
    private static readonly DateTimeOffset _t = new(2026, 11, 9, 10, 0, 0, TimeSpan.Zero);

    protected abstract Task<AlvoTestHost> StartAsync(Action<IAlvoBuilder> configure, TimeProvider time, CancellationToken ct);

    [Fact]
    public async Task Every_now_in_one_write_is_the_instant_the_write_began_at_even_while_the_clock_moves()
    {
        var ct = TestContext.Current.CancellationToken;
        var clock = new FakeTimeProvider(_t);
        DateTimeOffset? judged = null;
        await using var alvo = await StartAsync(builder => builder.Entity("notes", notes => notes
            .AddValidateHook("tick-validate", (ValidateWrite<AlvoRecord> _, CancellationToken _) => { clock.Advance(TimeSpan.FromSeconds(5)); return ValueTask.CompletedTask; })
            .AddBeforeWriteHook("tick-before", (BeforeWrite<AlvoRecord> write, CancellationToken _) => { clock.Advance(TimeSpan.FromSeconds(5)); write.Set("judged_at", clock.GetUtcNow()); return ValueTask.CompletedTask; }, when: "now() <= new.stamped_at")), clock, ct);

        var row = await alvo.Data.CreateAsync("notes", new Dictionary<string, object?> { ["body"] = "x" }, AlvoTestHost.Caller("clerk"), cancellationToken: ct);

        ((DateTimeOffset)row["stamped_at"]!).ShouldBe(_t);
        ((DateTimeOffset)row["created_at"]!).ShouldBe(_t);
        ((DateTimeOffset)row["updated_at"]!).ShouldBe(_t);
        ((DateTimeOffset)row["judged_at"]!).ShouldBe(_t.AddSeconds(10), "the host's own clock read is its business; the write's instant did not move");
        clock.GetUtcNow().ShouldBe(_t.AddSeconds(10));
    }

    [Fact]
    public async Task A_rule_with_now_sees_a_row_stamped_by_this_instant_and_not_one_stamped_later()
    {
        var ct = TestContext.Current.CancellationToken;
        var clock = new FakeTimeProvider(_t);
        await using var alvo = await StartAsync(_ => { }, clock, ct);
        var clerk = AlvoTestHost.Caller("clerk");
        await alvo.Data.CreateAsync("notes", new Dictionary<string, object?> { ["body"] = "first" }, clerk, cancellationToken: ct);

        (await alvo.Data.QueryAsync(new AlvoQuery { Entity = "notes" }, clerk, ct)).Items.Count.ShouldBe(1);
        using (AlvoFlow.EnterInstant(_t.AddSeconds(-1)))
        {
            (await alvo.Data.QueryAsync(new AlvoQuery { Entity = "notes" }, clerk, ct)).Items.ShouldBeEmpty("a list judged one second before the row existed");
        }
    }

    [Fact]
    public async Task Two_writes_in_one_unit_share_the_unit_s_instant()
    {
        var ct = TestContext.Current.CancellationToken;
        var clock = new FakeTimeProvider(_t);
        await using var alvo = await StartAsync(_ => { }, clock, ct);
        var transactions = alvo.Services.GetRequiredService<IAlvoTransactions>();

        var (first, second) = await transactions.TransactAsync(AlvoTestHost.Caller("clerk"), async (unit, token) =>
        {
            var a = await unit.CreateAsync("notes", new Dictionary<string, object?> { ["body"] = "a" }, cancellationToken: token);
            clock.Advance(TimeSpan.FromMinutes(1));
            var b = await unit.CreateAsync("notes", new Dictionary<string, object?> { ["body"] = "b" }, cancellationToken: token);
            return (a, b);
        }, cancellationToken: ct);

        first["created_at"].ShouldBe(second["created_at"]);
        first["stamped_at"].ShouldBe(_t);
    }
}
```

(Check the exact hook-registration and `TransactAsync`/`IAlvoTransaction` signatures in `WriteHookFacts.cs` and `TransactionFacts.cs` before writing — use theirs verbatim; `AlvoTestHost.Services` exists if `TransactionFacts` resolves `IAlvoTransactions` that way, otherwise follow its access path. The facts' assertions are the contract; the registration spelling follows the codebase.)

A Data API (HTTP) leg of acceptance (4) lands in Task 10 (`DataApiEngineTests.Integrity`), where the request filter is the binding site.

- [ ] **Step 2: Run them to verify they fail**

Run: `dotnet test --project test/MMLib.Alvo.Abstractions.Tests --filter-class '*AlvoFlowInstantTests'`; `dotnet test --project test/MMLib.Alvo.Tests --filter-class '*CelNowTests'`; `dotnet test --project test/MMLib.Alvo.TestHost.Tests --filter-class '*NowInstantFacts*'`
Expected: FAIL (`AlvoFlow.Instant` missing; `now()` refused outside Mutate).

- [ ] **Step 3: Implement the flow instant**

In `AlvoFlow.cs`: a third `AsyncLocal<DateTimeOffset?> _instant`; `Restore` records and restores it too.

```csharp
    internal static DateTimeOffset? Instant => _instant.Value;

    internal static IDisposable EnterInstant(DateTimeOffset instant)
    {
        var restore = new Restore(_provenance.Value, _unit.Value, _instant.Value);
        _instant.Value = Microsecond(instant.ToUniversalTime());
        return restore;
    }

    internal static IDisposable EnsureInstant(TimeProvider time)
    {
        ArgumentNullException.ThrowIfNull(time);
        return _instant.Value is null ? EnterInstant(time.GetUtcNow()) : NoInstant.Instance;
    }

    private static DateTimeOffset Microsecond(DateTimeOffset instant) =>
        instant.AddTicks(-(instant.Ticks % TimeSpan.TicksPerMicrosecond));
```

`NoInstant` is a private sealed no-op `IDisposable`. `Enter`/`EnterUnit` keep the instant they found (their `Restore` passes `_instant.Value` through unchanged).

- [ ] **Step 4: Bind it**

```csharp
// src/MMLib.Alvo/Api/Internal/RequestInstant.cs
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;

namespace MMLib.Alvo.Api.Internal;

/// <summary>The one instant an HTTP request's every now() reads (HX-11 Ruling 1), bound where Alvo resolves the request's caller.</summary>
internal static class RequestInstant
{
    internal static IDisposable Enter(HttpContext http)
    {
        ArgumentNullException.ThrowIfNull(http);
        return AlvoFlow.EnsureInstant(http.RequestServices.GetService<TimeProvider>() ?? TimeProvider.System);
    }
}
```

In `AlvoContextFilter.InvokeAsync`, `AlvoCallerFilter.InvokeAsync` and `ManagementCallerFilter.InvokeAsync`: `using var instant = RequestInstant.Enter(context.HttpContext);` as the first statement after the null guards (so the gate and the endpoint run under it). In every public `EfAlvoData` entry point (reads and writes, `.Units.cs` and `.Hooks.cs` included) and in `EfAlvoTransactions.TransactAsync`: `using var instant = AlvoFlow.EnsureInstant(_time);` first. `WriteInstantNow()` becomes `AlvoFlow.Instant ?? StoredInstant.Storable(_time.GetUtcNow())` and its remarks cite Ruling 2. `InMemoryAlvoData`'s public entry points do the same (Ruling 41).

- [ ] **Step 5: Teach the profiles and the interpreter**

- `CelBuiltInFunctions.Now`: profiles `RuleConditionAndMutate`; description `"The instant this request — or, after the commit, the event — happened at: one instant, never a clock read."`
- `CelTypeChecker`: `[CelConstructKind.Call] = _ruleConditionAndMutate`; `CheckLegacyCall`'s profile message for `Computed`/`Access` keeps naming why (`"A computed column is maintained by the database and an access level has no row; neither has an instant to read."`).
- `CelInterpreter.EvaluateCall`: `{ Name: CelCall.Now, Arguments: [] } => state.Now ?? AlvoFlow.Instant ?? throw UnboundInstant()`, where `UnboundInstant()` is `HookDefect(new InvalidOperationException("now() was evaluated with no instant bound to this flow; every Alvo entry point binds one."))` — the defect wrapper, `OnCallerData` false (Ruling 5).
- `EnvelopeReading` (the fan-out condition evaluation, line ~78): wrap the evaluation in `using (AlvoFlow.EnterInstant(@event.Time))` — the event time (Ruling 1).
- `BeforeHookRunner`: its `now` parameter already reaches `EvaluateMutation`; its condition evaluation reads the same instant through the flow, which `EfAlvoData` bound. Assert in a unit test that a before-hook condition and its mutate see one instant (extend `NowInstantFacts` above — the descriptor's `condition: "now() == now()"` plus the stamped value cover it).
- `SqlPredicateRenderer`: a `now()` operand in the predicate path is `bag.Add(AlvoFlow.Instant ?? throw new InvalidOperationException(UnboundNow))` rendered through `fields.RenderParameter` — the same path `RenderContextOperand` uses.

- [ ] **Step 6: Run the tests and the suites that read time**

Run: `dotnet test --project test/MMLib.Alvo.Abstractions.Tests --filter-class '*AlvoFlowInstantTests'`; `dotnet test --project test/MMLib.Alvo.Tests --filter-class '*CelNowTests' --filter-class '*CelMutate*' --filter-class '*BeforeHook*' --filter-class '*CelEvaluationSitesTests'`; `dotnet test --project test/MMLib.Alvo.TestHost.Tests --filter-class '*NowInstantFacts*' --filter-class '*TransactionFacts*'`
Expected: PASS. `CelEvaluationSitesTests` pins who calls the interpreter's hook entry points; `EnvelopeReading`'s new `using` adds no call site.

- [ ] **Step 7: Format, ring0, commit**

```bash
dotnet format whitespace MMLib.Alvo.slnx --include <every .cs file touched in this task>
scripts/test-ring0
git add <the files of this task, by path>
git commit -m "feat(cel): bind one now() instant per request in rules, conditions and mutates" -m "Claude-Session: https://claude.ai/code/session_01NJAtafM29iddLwfhb5gnmB"
```

---

## Task 3: Durations and `now()` in SQL — the microsecond domain and the agreement property **[slice end]**

**Files:**
- Modify: `src/MMLib.Alvo.Abstractions/Expressions/IFieldSqlRenderer.cs` (`RenderEpochMicroseconds` DIM)
- Create: `src/MMLib.Alvo/Expressions/Internal/SqlPredicateRenderer.Temporal.cs` (partial)
- Modify: `src/MMLib.Alvo/Expressions/Internal/SqlPredicateRenderer.cs` (make it `partial`; route temporal comparisons), `src/MMLib.Alvo.Data.Sqlite/SqliteFieldSqlRenderer.cs`, `src/MMLib.Alvo.Data.PostgreSql/PostgreSqlFieldSqlRenderer.cs`, `src/MMLib.Alvo.Testing.EntityFrameworkCore/` (the T-SQL field renderer beside `TSqlSqlDialect.cs`), `src/MMLib.Alvo.Testing/Data/DifferentialRuleCases.cs`, `src/MMLib.Alvo.Testing/Data/AlvoDataDifferentialTests.cs`
- Test: `test/MMLib.Alvo.Tests/Expressions/SqlPredicateRendererTemporalTests.cs` (new); `test/MMLib.Alvo.Data.Sqlite.Tests/SqliteEpochTests.cs` (new); `test/MMLib.Alvo.Data.PostgreSql.Tests.Integration/PostgreSqlEpochTests.cs` (new); the TSQL golden in `test/MMLib.Alvo.Data.EntityFrameworkCore.Tests/DialectContractTests.cs`; `test/MMLib.Alvo.Tests/cel-to-sql-core.verified.txt`

**Interfaces:**
- Produces: `string? IFieldSqlRenderer.RenderEpochMicroseconds(string timestampSql) => null;`; `SqlPredicateRenderer` renders `CelBinary` comparisons whose operands are temporal (`CelTemporal.TypeOf` not null and not a plain leaf) in the microsecond domain; `AlvoDataDifferentialTests.Durations_and_now_agree_between_this_engine_and_the_interpreter_for_any_instants` (CsCheck, both engines).
- Consumes: `CelTemporal` (Task 1), `AlvoFlow.Instant` (Task 2).

- [ ] **Step 1: Write the failing tests**

```csharp
// test/MMLib.Alvo.Tests/Expressions/SqlPredicateRendererTemporalTests.cs
using MMLib.Alvo.Expressions;
using MMLib.Alvo.Expressions.Internal;

namespace MMLib.Alvo.Tests.Expressions;

public sealed class SqlPredicateRendererTemporalTests
{
    private static readonly DateTimeOffset _t = new(2026, 11, 9, 10, 0, 0, TimeSpan.Zero);
    private static readonly SqlPredicateRenderer _renderer = new();

    private static SqlPredicate Render(string rule)
    {
        using var instant = AlvoFlow.EnterInstant(_t);
        return _renderer.Render(CelFixtures.CompileRule(rule), CelFixtures.Alice, EpochFields.Instance, "p");
    }

    [Fact]
    public void A_constant_window_is_folded_into_one_bound_instant_and_stays_sargable()
    {
        var predicate = Render("created_at > now() - duration('24h')");
        predicate.Sql.ShouldBe("COALESCE(\"created_at\" > @p0, FALSE)");
        predicate.Parameters["p0"].ShouldBe(_t.AddHours(-24));
    }

    [Fact]
    public void Arithmetic_over_a_field_enters_the_microsecond_domain()
    {
        var predicate = Render("approved_at - created_at <= duration('8h')");
        predicate.Sql.ShouldBe("COALESCE((EPOCH_US(\"approved_at\") - EPOCH_US(\"created_at\")) <= @p0, FALSE)");
        predicate.Parameters["p0"].ShouldBe(28_800_000_000L);
    }

    [Fact]
    public void A_field_shifted_by_a_duration_is_compared_with_the_bound_instant_in_microseconds()
    {
        var predicate = Render("created_at + duration('1h') < now()");
        predicate.Sql.ShouldBe("COALESCE((EPOCH_US(\"created_at\") + @p0) < @p1, FALSE)");
        predicate.Parameters["p0"].ShouldBe(3_600_000_000L);
        predicate.Parameters["p1"].ShouldBe((_t - DateTimeOffset.UnixEpoch).Ticks / 10);
    }

    [Fact]
    public void A_dialect_with_no_epoch_refuses_by_name()
    {
        using var instant = AlvoFlow.EnterInstant(_t);
        Should.Throw<NotSupportedException>(() => _renderer.Render(
            CelFixtures.CompileRule("approved_at - created_at <= duration('8h')"), CelFixtures.Alice, NoEpochFields.Instance, "p"))
            .Message.ShouldContain(nameof(NoEpochFields));
    }

    [Fact]
    public void Now_with_no_bound_instant_refuses_to_render() =>
        Should.Throw<InvalidOperationException>(() =>
            _renderer.Render(CelFixtures.CompileRule("created_at <= now()"), CelFixtures.Alice, EpochFields.Instance, "p"));
}
```

`EpochFields` is a test `IFieldSqlRenderer` (copy `ComputedFieldCheck.NeutralFields`' members into `test/MMLib.Alvo.Tests/Expressions/EpochFields.cs`) whose `RenderEpochMicroseconds(x)` answers `$"EPOCH_US({x})"`; `NoEpochFields` the same without that member. On the predicate path every constant is a bound parameter — a duration as its `long` microseconds, a folded instant as its epoch microseconds once it is inside the microsecond domain — exactly as every other rule literal is bound; only Task 5's constraint path writes them inline.

```csharp
// test/MMLib.Alvo.Data.Sqlite.Tests/SqliteEpochTests.cs
using Microsoft.Data.Sqlite;
using MMLib.Alvo.Expressions;

namespace MMLib.Alvo.Data.Sqlite.Tests;

/// <summary>SQLite's epoch is unixepoch(…, 'subsec'), millisecond-precise (HX-11 Ruling 11) — asked of the engine.</summary>
public sealed class SqliteEpochTests : IDisposable
{
    private readonly IFieldSqlRenderer _fields = new SqliteFieldSqlRenderer();
    private readonly SqliteConnection _connection = new("Data Source=:memory:");

    public SqliteEpochTests() => _connection.Open();

    [Fact]
    public void The_bundled_engine_has_subsecond_unixepoch() =>
        Version.Parse(Scalar("SELECT sqlite_version()")!.ToString()!).ShouldBeGreaterThanOrEqualTo(new Version(3, 42, 0));

    [Theory]
    [InlineData("'2026-11-09 10:00:00.123+00:00'", 1_762_682_400_123_000L)]
    [InlineData("'2026-11-09T12:00:00.123+02:00'", 1_762_682_400_123_000L)]
    [InlineData("'2026-11-09 10:00:00.1234567+00:00'", 1_762_682_400_123_000L)]
    [InlineData("'2026-11-09'", 1_762_646_400_000_000L)]
    public void A_stored_instant_reads_as_microseconds_rounded_to_the_millisecond(string literal, long microseconds) =>
        Scalar($"SELECT {_fields.RenderEpochMicroseconds(literal)}").ShouldBe(microseconds);

    [Fact]
    public void An_empty_instant_is_empty() => Scalar($"SELECT {_fields.RenderEpochMicroseconds("NULL")}").ShouldBe(DBNull.Value);

    public void Dispose() => _connection.Dispose();

    private object? Scalar(string sql)
    {
        using var command = _connection.CreateCommand();
        command.CommandText = sql;
        return command.ExecuteScalar();
    }
}
```

`PostgreSqlEpochTests` is the same shape over the integration fixture's connection, expecting exact microseconds (`'…10:00:00.123456+00'::timestamptz` → `…123456`, a `date` → midnight) and no version fact.

**The agreement property (acceptance (6)).** In `AlvoDataDifferentialTests`, add:

```csharp
    /// <summary>
    /// HX-11 acceptance (6): duration arithmetic and now() agree between this engine's SQL and the interpreter for any
    /// instants — offsets other than UTC included — at the millisecond alignment SQLite's date functions keep.
    /// </summary>
    [Fact]
    public async Task Durations_and_now_agree_between_this_engine_and_the_interpreter_for_any_instants()
    {
        await using var probe = await CreateProbeAsync(DifferentialEntity);
        var admitted = 0;
        var refused = 0;
        await TemporalCase.Any.SampleAsync(async testCase =>
        {
            var verdicts = await TemporalVerdictsAsync(probe, testCase);
            admitted += verdicts.ViaEngine ? 1 : 0;
            refused += verdicts.ViaEngine ? 0 : 1;
            verdicts.ViaEngine.ShouldBe(verdicts.InMemory, testCase.ToString());
        }, iter: 300, threads: 1);
        admitted.ShouldBeGreaterThan(30, "the space must reach admitted rows");
        refused.ShouldBeGreaterThan(30, "and refused ones, or agreement is vacuous");
    }
```

`TemporalCase` (a nested `sealed record` with a `static Gen<TemporalCase> Any`) draws: a rule from a fixed list over `DifferentialEntity`'s fields — `approved_at - created_at <= duration('<d>')`, `created_at + duration('<d>') < approved_at`, `approved_at - duration('<d>') >= created_at`, `created_at > now() - duration('<d>')`, `due_on + duration('<d>') <= created_at`, `approved_at - created_at == duration('<d>')`; a duration `d` of whole milliseconds in ±10 years (`Gen.Long[-315_360_000_000L, 315_360_000_000L]` milliseconds); two instants in 1970–2100 of whole milliseconds, each with an offset drawn from `{+00:00, +02:00, -05:30, +14:00}`; one value in ten `null`; a `now` instant of whole milliseconds. `TemporalVerdictsAsync` binds `AlvoFlow.EnterInstant(testCase.Now)` around both the render and the in-memory evaluation, exactly as `VerdictsAsync` does for the matrix. The equality rule draws `d` as the exact difference half the time, so `==` is exercised on equal values.

And `DifferentialRuleCases.All` gains named rows (so the matrix fact's divergence report names them): `duration-window-in`, `duration-window-out`, `duration-equal`, `now-window-in`, `now-window-out`, `offset-plus-two-in`, `offset-minus-five-thirty-out`, `date-plus-duration`, `null-operand` — each a `DifferentialRuleCase` built like the existing ones (rule text, a row, a context name), with the matrix's `VerdictsAsync` wrapping its render and evaluation in `AlvoFlow.EnterInstant(DifferentialRuleCases.Now)` (a fixed `2026-11-09T10:00:00Z`).

- [ ] **Step 2: Run them to verify they fail**

Run: `dotnet test --project test/MMLib.Alvo.Tests --filter-class '*SqlPredicateRendererTemporalTests'`; `dotnet test --project test/MMLib.Alvo.Data.Sqlite.Tests --filter-class '*SqliteEpochTests' --filter-class '*SqliteAlvoDataDifferentialTests'`
Expected: FAIL (no `RenderEpochMicroseconds`; the renderer refuses the temporal operand).

- [ ] **Step 3: Add the dialect member and implement it**

```csharp
    /// <summary>
    /// Renders a timestamp or date expression as whole microseconds since 1970-01-01T00:00:00Z, as a SQL integer — the
    /// domain duration arithmetic and comparisons are evaluated in. <see langword="null"/> (the default) declares that this
    /// dialect cannot, and a rule, a validation or a rollup filter using a duration over a field is then refused by name.
    /// </summary>
    /// <param name="timestampSql">A rendered timestamp or date column, parameter or literal.</param>
    /// <returns>The integer expression, or <see langword="null"/>.</returns>
    string? RenderEpochMicroseconds(string timestampSql) => null;
```

SQLite (explicit implementation, like `RenderStringLiteral`): `$"CAST(round(unixepoch({timestampSql}, 'subsec') * 1000000) AS INTEGER)"`. PostgreSQL: `$"CAST(EXTRACT(EPOCH FROM {timestampSql}) * 1000000 AS bigint)"`. T-SQL double: `$"DATEDIFF_BIG(microsecond, '1970-01-01T00:00:00', {timestampSql})"` plus a golden row in `DialectContractTests`.

- [ ] **Step 4: Render temporal comparisons**

Make `SqlPredicateRenderer` `partial`. In `RenderComparison`, before the leaf path: if either operand is a temporal binary, or `PromotedType` is `Duration`, call `RenderTemporalComparison`. The partial:

```csharp
// src/MMLib.Alvo/Expressions/Internal/SqlPredicateRenderer.Temporal.cs
namespace MMLib.Alvo.Expressions.Internal;

/// <summary>
/// Duration arithmetic in SQL (HX-11 Rulings 4, 11): a subtree with no field folds into one bound value in-process; one
/// that reads a field is rendered in whole microseconds since the Unix epoch through the dialect.
/// </summary>
internal sealed partial class SqlPredicateRenderer
{
    private const string UnboundNow =
        "now() was rendered with no instant bound to this flow; every Alvo entry point binds one (AlvoFlow.EnsureInstant).";

    private static bool IsTemporalComparison(CelBinary comparison) =>
        IsTemporalOperand(comparison.Left) || IsTemporalOperand(comparison.Right)
        || CelTemporal.TypeOf(comparison.Left) == CelValueType.Duration;

    private static bool IsTemporalOperand(CelNode node) =>
        node is CelBinary binary && CelTemporal.IsTemporal(binary) || node is CelCall { Name: CelCall.Now };

    private static PredicateFragment RenderTemporalComparison(
        CelBinary comparison, EntitySchema entity, IFieldSqlRenderer fields, ILiteralSink sink)
    {
        if (TryFoldedLeafComparison(comparison, entity, fields, sink) is { } leaf)
        {
            return leaf;
        }

        var left = Microseconds(comparison.Left, entity, fields, sink);
        var right = Microseconds(comparison.Right, entity, fields, sink);
        return new PredicateFragment(fields.RenderTwoValued($"{left} {ComparisonOperatorText(comparison.Operator)} {right}"), true);
    }

    private static string Microseconds(CelNode node, EntitySchema entity, IFieldSqlRenderer fields, ILiteralSink sink) =>
        Folded(node) is { } constant
            ? sink.Integer(ToMicroseconds(constant))
            : node switch
            {
                CelFieldRef field => Epoch(RenderField(field, entity, fields), fields),
                CelBinary binary => $"({Microseconds(binary.Left, entity, fields, sink)} {(binary.Operator == CelBinaryOperator.Add ? "+" : "-")} {Microseconds(binary.Right, entity, fields, sink)})",
                _ => throw Unsupported(node),
            };

    private static string Epoch(string timestampSql, IFieldSqlRenderer fields) =>
        fields.RenderEpochMicroseconds(timestampSql)
        ?? throw new NotSupportedException(
            $"{fields.GetType().Name} declares no epoch rendering (IFieldSqlRenderer.RenderEpochMicroseconds), so duration "
            + "arithmetic over a field cannot be evaluated by this engine.");

    private static object? Folded(CelNode node) => node switch
    {
        CelLiteral { Type: CelValueType.Duration or CelValueType.Timestamp } literal => literal.Value,
        CelCall { Name: CelCall.Now } => AlvoFlow.Instant ?? throw new InvalidOperationException(UnboundNow),
        CelBinary binary when CelTemporal.IsTemporal(binary) && Folded(binary.Left) is { } l && Folded(binary.Right) is { } r =>
            CelTemporal.Apply(binary.Operator, l, r, failClosed: true),
        _ => null,
    };

    private static long ToMicroseconds(object value) => value switch
    {
        TimeSpan duration => duration.Ticks / 10,
        DateTimeOffset instant => (instant.UtcTicks - DateTimeOffset.UnixEpoch.UtcTicks) / 10,
        _ => throw new InvalidOperationException($"'{value.GetType().Name}' is not a temporal constant."),
    };
}
```

`TryFoldedLeafComparison` returns the existing leaf comparison (`"field" OP <bound instant>`) when one side is a bare timestamp field and the other folds to a `DateTimeOffset` — the sargable shape Ruling 4 promises — and `null` otherwise. `ILiteralSink` is the one seam between the predicate path's `ParameterBag` (bound values: `Integer(long)` adds a parameter) and Task 5's inline writer (`Integer(long)` writes digits); introduce it here with the `ParameterBag` implementation only, and make `RenderLiteralOperand`/`RenderContextOperand` go through it. A folded range error (`CelFunctionException` from `failClosed: true`) at render time becomes `NotSupportedException("…the constant arithmetic in this expression is outside the range of a Timestamp…")` — a constant that overflows is the author's, refused where it is rendered.

- [ ] **Step 5: Run the tests, then the golden**

Run: `dotnet test --project test/MMLib.Alvo.Tests --filter-class '*SqlPredicateRenderer*' --filter-class '*NoInterpolationPropertyTests' --filter-class '*CompiledRendersPropertyTests' --filter-class '*DifferentialBackendTests'`; then `dotnet test --project test/MMLib.Alvo.Tests --filter-class '*CelToSql*'` (the test that writes `cel-to-sql-core.verified.txt` — find it with `grep -rln "cel-to-sql-core" test/MMLib.Alvo.Tests`); add the temporal rows to its source list, accept the received file after reading it, and dispatch `alvo-snapshot-judge`.
Expected: PASS.

- [ ] **Step 6: Both engines**

Run: `dotnet test --project test/MMLib.Alvo.Data.Sqlite.Tests --filter-class '*SqliteEpochTests' --filter-class '*SqliteAlvoDataDifferentialTests'`; `dotnet test --project test/MMLib.Alvo.Data.PostgreSql.Tests.Integration --filter-class '*PostgreSqlEpochTests' --filter-class '*PostgreSqlAlvoDataDifferentialTests'`; `dotnet test --project test/MMLib.Alvo.Data.EntityFrameworkCore.Tests --filter-class '*DialectContractTests'`
Expected: PASS on both. A divergence report names the case, the SQL and the parameters — read it before changing anything; a sub-millisecond divergence on SQLite means the generator drew a non-millisecond value.

- [ ] **Step 7: Format, ring0, ring1, commit**

```bash
dotnet format whitespace MMLib.Alvo.slnx --include <every .cs file touched in this task>
scripts/test-ring0 && scripts/test-ring1
git add <the files of this task, by path> test/MMLib.Alvo.Tests/cel-to-sql-core.verified.txt test/MMLib.Alvo.Abstractions.Tests/PublicApi.MMLib.Alvo.Abstractions.verified.txt
git commit -m "feat(cel): render durations and now() in SQL as epoch microseconds on both engines" -m "Claude-Session: https://claude.ai/code/session_01NJAtafM29iddLwfhb5gnmB"
```

(The public-API baseline grows by `RenderEpochMicroseconds` here — accept and judge it in this task.)

---

## Task 4: Typed `when` — `DateTimeOffset.UtcNow`, `TimeSpan` and their operators

**Files:**
- Modify: `src/MMLib.Alvo/Extensions/Internal/ConditionTranslator.cs`, `ConditionTranslator.Calls.cs`, the `LambdaShape` helper that defines `IsClock`/`FindClock` (find it with `grep -rn "static bool IsClock" src/MMLib.Alvo`)
- Test: `test/MMLib.Alvo.Tests/Extensions/ConditionTranslatorClockTests.cs` (new)

**Interfaces:**
- Produces: in a typed `when`, `DateTimeOffset.UtcNow`/`.Now` → `CelCall(now)`; a `TimeSpan` constant → `CelCall("duration", [CelLiteral(String, CelDuration.Text(span))])`; `+`/`-` over `DateTimeOffset`/`TimeSpan` → `CelBinary(Add|Subtract)`.
- Consumes: `CelDuration.Text` (Task 1), `CelText.Write` and `CelParser.Parse` (HX-05 (22): registration parses what it wrote).

- [ ] **Step 1: Write the failing tests**

```csharp
// test/MMLib.Alvo.Tests/Extensions/ConditionTranslatorClockTests.cs
namespace MMLib.Alvo.Tests.Extensions;

public sealed class ConditionTranslatorClockTests
{
    private static readonly TimeSpan _grace = TimeSpan.FromMinutes(90);

    [Fact]
    public void Utc_now_translates_to_now() =>
        TypedConditions.Translate<Booking>(e => e.New.StartsAt > DateTimeOffset.UtcNow).ShouldBe("new.starts_at > now()");

    [Fact]
    public void Now_with_the_local_offset_is_the_same_instant() =>
        TypedConditions.Translate<Booking>(e => e.New.StartsAt > DateTimeOffset.Now).ShouldBe("new.starts_at > now()");

    [Fact]
    public void A_static_readonly_time_span_and_the_operators_translate() =>
        TypedConditions.Translate<Booking>(e => e.New.EndsAt - e.New.StartsAt <= _grace + _grace)
            .ShouldBe("new.ends_at - new.starts_at <= duration('5400s') + duration('5400s')");

    [Fact]
    public void A_shift_of_now_translates() =>
        TypedConditions.Translate<Booking>(e => e.New.StartsAt < DateTimeOffset.UtcNow - _grace)
            .ShouldBe("new.starts_at < now() - duration('5400s')");

    [Theory]
    [MemberData(nameof(Refused))]
    public void Other_clocks_stay_refused_with_the_fix(System.Linq.Expressions.Expression<Func<EventCondition<Booking>, bool>> when, string member) =>
        Should.Throw<AlvoTranslationException>(() => TypedConditions.Translate(when)).Message.ShouldSatisfyAllConditions(
            message => message.ShouldContain(member),
            message => message.ShouldContain("DateTimeOffset.UtcNow"));

    public static TheoryData<System.Linq.Expressions.Expression<Func<EventCondition<Booking>, bool>>, string> Refused => new()
    {
        { e => e.New.StartsAt.UtcDateTime > DateTime.UtcNow, "UtcNow" },
        { e => e.New.StartsAt.Date > DateTime.Today, "Today" },
        { e => e.New.StartsAt > TimeProvider.System.GetUtcNow(), "GetUtcNow" },
    };
}
```

`Booking` and `TypedConditions.Translate` are the fixtures the existing translator tests use — find them with `grep -rln "ConditionTranslator" test/MMLib.Alvo.Tests` and reuse that file's record type and translate helper (add `StartsAt`/`EndsAt` `DateTimeOffset?` properties to its record if it lacks them). The CEL strings follow `CelText`'s canonical spacing (HX-05 (1)).

- [ ] **Step 2: Run them to verify they fail**

Run: `dotnet test --project test/MMLib.Alvo.Tests --filter-class '*ConditionTranslatorClockTests'`
Expected: FAIL — the clock is refused with "conditions have no clock yet".

- [ ] **Step 3: Implement**

- `LambdaShape`: split `IsClock` into `IsCelNow(MemberExpression)` (exactly `DateTimeOffset.UtcNow` or `DateTimeOffset.Now`) and `IsClock` (every other clock member or call it refuses today).
- `ConditionTranslator` member visit: `MemberExpression member when LambdaShape.IsCelNow(member) => new CelCall(CelCall.Now, [])`. `ClockReason`/`ClockFix` become `"this clock has no single instant in CEL"` / `"use DateTimeOffset.UtcNow, which a condition reads as now(): the instant of the request, or of the event after the commit."`.
- `Literal`: `TimeSpan span => new CelCall(CelBuiltInFunctions.DurationName, [new CelLiteral(CelValueType.String, CelDuration.Text(span))])`, refusing a span that `CelDuration.TryParse(CelDuration.Text(span))` does not give back (sub-microsecond or out of range) with `"a TimeSpan finer than a microsecond or longer than ten thousand years has no CEL duration"`.
- Binary visit: `ExpressionType.Add`/`Subtract` whose operands are `DateTimeOffset`/`TimeSpan` (nullable included) → `CelBinary(Add|Subtract)`; the existing arithmetic arm already handles numeric `+`/`-` — route by operand CLR type before it.
- The query-side translator (`QueryAsync<T>` → `AlvoFilter`) keeps refusing every clock with "capture it first": add one assertion to its existing clock test that `DateTimeOffset.UtcNow` is still refused there.

- [ ] **Step 4: Run them, then the translator suites**

Run: `dotnet test --project test/MMLib.Alvo.Tests --filter-class '*ConditionTranslator*' --filter-class '*WriteConditionTranslator*' --filter-class '*QueryTranslator*'`
Expected: PASS.

- [ ] **Step 5: Format, ring0, commit**

```bash
dotnet format whitespace MMLib.Alvo.slnx --include <every .cs file touched>
scripts/test-ring0
git add <the files, by path>
git commit -m "feat(extensions): translate DateTimeOffset.UtcNow and TimeSpan in typed conditions" -m "Claude-Session: https://claude.ai/code/session_01NJAtafM29iddLwfhb5gnmB"
```

---
## Task 5: The constraint renderer — inline literals, the stored-predicate surface, and "a literal never escapes a `CHECK`"

**Files:**
- Modify: `src/MMLib.Alvo.Abstractions/Expressions/IPredicateRenderer.cs` (`RenderConstraint` DIM)
- Create: `src/MMLib.Alvo/Expressions/Internal/SqlPredicateRenderer.Constraint.cs`, `src/MMLib.Alvo/Expressions/Internal/StoredPredicateSurface.cs`
- Test: `test/MMLib.Alvo.Tests/Expressions/SqlPredicateRendererConstraintTests.cs`, `StoredPredicateSurfaceTests.cs` (new); `test/MMLib.Alvo.Data.Sqlite.Tests/SqliteCheckLiteralPropertyTests.cs`, `test/MMLib.Alvo.Data.PostgreSql.Tests.Integration/PostgreSqlCheckLiteralPropertyTests.cs` (new)

**Interfaces:**
- Produces: `string IPredicateRenderer.RenderConstraint(CompiledExpression expression, IFieldSqlRenderer fields)` (DIM: `throw new NotSupportedException(...)`; `SqlPredicateRenderer` implements it); `StoredPredicateSurface.Refusals(CompiledExpression expression, string slot) → IReadOnlyList<CelCompilationError>` — one error per `@user`, `@tenant`, `in` (role test), `now()` and any surviving `CelCall` (a folded `duration()` is a literal, so it never appears), each with `slot` (`"A validation"`/`"A rollup filter"`) in its message.
- Consumes: Task 3's `ILiteralSink` seam.

- [ ] **Step 1: Write the failing tests**

```csharp
// test/MMLib.Alvo.Tests/Expressions/SqlPredicateRendererConstraintTests.cs
using MMLib.Alvo.Expressions;
using MMLib.Alvo.Expressions.Internal;

namespace MMLib.Alvo.Tests.Expressions;

public sealed class SqlPredicateRendererConstraintTests
{
    private static readonly SqlPredicateRenderer _renderer = new();

    private static string Constraint(string rule) => _renderer.RenderConstraint(CelFixtures.CompileRule(rule), EpochFields.Instance);

    [Theory]
    [InlineData("total >= 0", "COALESCE(\"total\" >= 0, FALSE)")]
    [InlineData("total <= 12.5", "COALESCE(\"total\" <= 12.5, FALSE)")]
    [InlineData("status != 'void'", "COALESCE(\"status\" <> 'void', FALSE)")]
    [InlineData("is_public == true", "COALESCE(\"is_public\" = TRUE, FALSE)")]
    [InlineData("approved_at - created_at <= duration('1h')", "COALESCE((EPOCH_US(\"approved_at\") - EPOCH_US(\"created_at\")) <= 3600000000, FALSE)")]
    [InlineData("has(title) && status == 'open'", "((\"title\" IS NOT NULL) AND COALESCE(\"status\" = 'open', FALSE))")]
    public void Every_literal_is_written_inline_and_nothing_is_bound(string rule, string sql) => Constraint(rule).ShouldBe(sql);

    [Theory]
    [InlineData("owner_id == @user.id")]
    [InlineData("tenant_id == @tenant.id")]
    [InlineData("'admin' in @user.roles")]
    [InlineData("created_at <= now()")]
    public void Anything_with_no_constant_value_refuses_to_render(string rule) =>
        Should.Throw<NotSupportedException>(() => Constraint(rule));

    [Fact]
    public void Text_the_dialect_declines_refuses_to_render() =>
        Should.Throw<NotSupportedException>(() => _renderer.RenderConstraint(CelFixtures.CompileRule("status != 'a\u0001b'"), EpochFields.Instance));
}
```

(`EpochFields.RenderStringLiteral` delegates to `AlvoSqlStringLiteral`'s rule — copy its decline predicate into the test double, or make the double return `$"'{value.Replace("'", "''")}'"` for text without control characters and `null` otherwise.)

```csharp
// test/MMLib.Alvo.Tests/Expressions/StoredPredicateSurfaceTests.cs
using MMLib.Alvo.Expressions.Internal;

namespace MMLib.Alvo.Tests.Expressions;

public sealed class StoredPredicateSurfaceTests
{
    [Theory]
    [InlineData("owner_id == @user.id", "@user")]
    [InlineData("tenant_id == @tenant.id", "@tenant")]
    [InlineData("'admin' in @user.roles", "role")]
    [InlineData("created_at <= now()", "now()")]
    public void A_stored_predicate_reads_no_caller_and_no_clock(string rule, string named) =>
        StoredPredicateSurface.Refusals(CelFixtures.CompileRule(rule), "A validation").ShouldHaveSingleItem()
            .Message.ShouldSatisfyAllConditions(m => m.ShouldContain(named), m => m.ShouldStartWith("A validation"));

    [Theory]
    [InlineData("total >= 0")]
    [InlineData("approved_at - created_at <= duration('8h') && status != 'void'")]
    public void A_predicate_over_the_row_alone_passes(string rule) =>
        StoredPredicateSurface.Refusals(CelFixtures.CompileRule(rule), "A validation").ShouldBeEmpty();
}
```

**Acceptance (5) — CsCheck, both engines.** `SqliteCheckLiteralPropertyTests` (and the PostgreSQL twin over the integration fixture's connection):

```csharp
// test/MMLib.Alvo.Data.Sqlite.Tests/SqliteCheckLiteralPropertyTests.cs
using CsCheck;
using Microsoft.Data.Sqlite;
using MMLib.Alvo.Expressions;
using MMLib.Alvo.Expressions.Internal;
using MMLib.Alvo.Schema;
using MMLib.Alvo.Tests.Data;

namespace MMLib.Alvo.Data.Sqlite.Tests;

/// <summary>
/// HX-11 acceptance (5): a text constant in a validation never escapes the CHECK it becomes. For any text the literal
/// writer accepts, the engine's own constraint refuses exactly that text and admits a different one, and the table a
/// payload names survives — asked of the engine, the only decoder whose answer counts.
/// </summary>
public sealed class SqliteCheckLiteralPropertyTests : IDisposable
{
    private static readonly EntitySchema _notes = new()
    {
        Name = "notes",
        Fields = [new FieldSchema { Name = "title", Type = FieldType.String, Nullable = true }],
    };

    private readonly IFieldSqlRenderer _fields = new SqliteFieldSqlRenderer();
    private readonly SqliteConnection _connection = new("Data Source=:memory:");

    public SqliteCheckLiteralPropertyTests() => _connection.Open();

    [Fact]
    public void A_literal_never_escapes_a_check()
    {
        long held = 0;
        LiteralText.Any().Where(text => !LiteralText.MustBeDeclined(text)).Sample(
            text =>
            {
                var check = new SqlPredicateRenderer().RenderConstraint(
                    new CelCompiler().Compile($"title != {CelText.StringLiteral(text)}", CelProfile.Rule, _notes).Expression!, _fields);
                Recreate(check);
                held++;
                return !Inserts(text) && Inserts(text + "x") && SentinelSurvives();
            },
            iter: 500,
            threads: 1);
        held.ShouldBe(500);
    }

    public void Dispose() => _connection.Dispose();

    private void Recreate(string check)
    {
        Execute("DROP TABLE IF EXISTS notes; CREATE TABLE IF NOT EXISTS sentinel (id INTEGER);");
        Execute($"CREATE TABLE notes (title TEXT, CONSTRAINT ck_notes_title CHECK (\"title\" IS NULL OR {check}))");
    }

    private bool Inserts(string title)
    {
        using var command = _connection.CreateCommand();
        command.CommandText = "INSERT INTO notes (title) VALUES ($title)";
        command.Parameters.AddWithValue("$title", title);
        try
        {
            command.ExecuteNonQuery();
            return true;
        }
        catch (SqliteException failure) when (failure.SqliteExtendedErrorCode == 275)
        {
            return false;
        }
    }

    private bool SentinelSurvives()
    {
        using var command = _connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE name = 'sentinel'";
        return (long)command.ExecuteScalar()! == 1;
    }

    private void Execute(string sql)
    {
        using var command = _connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }
}
```

The PostgreSQL twin catches `PostgresException { SqlState: "23514" }` and checks `to_regclass('sentinel') IS NOT NULL`; it excludes NUL (`\u0000`) from the drawn text exactly as `TypedLayerFacts` does (PostgreSQL refuses NUL in a parameter — the engine refusing the value, not text leaking). Both projects already reference `test/_shared/literals/LiteralText.cs` (`SqliteStringLiteralTests` uses it); if the integration project does not, add the same `<Compile Include>` line.

- [ ] **Step 2: Run them to verify they fail**

Run: `dotnet test --project test/MMLib.Alvo.Tests --filter-class '*SqlPredicateRendererConstraintTests' --filter-class '*StoredPredicateSurfaceTests'`
Expected: build FAIL — `RenderConstraint` and `StoredPredicateSurface` do not exist.

- [ ] **Step 3: Implement**

`IPredicateRenderer`:

```csharp
    /// <summary>
    /// Renders a Rule-profile expression that reads the row alone — no caller, no tenant, no now() — as a SQL predicate
    /// with every constant written inline, for a place that has no bind parameters: a CHECK constraint, a stored filter.
    /// </summary>
    /// <param name="expression">A compiled Rule-profile expression.</param>
    /// <param name="fields">The engine's field, literal and epoch rendering.</param>
    /// <returns>A two-valued SQL predicate with no parameters.</returns>
    /// <exception cref="NotSupportedException">The expression reads the caller, the tenant or the clock, or holds a constant this engine cannot write inline.</exception>
    string RenderConstraint(CompiledExpression expression, IFieldSqlRenderer fields) =>
        throw new NotSupportedException($"{GetType().Name} does not render constraints.");
```

`SqlPredicateRenderer.Constraint.cs`: `RenderConstraint` requires `CelProfile.Rule`, walks with an `InlineLiterals` sink (`ILiteralSink`): `Integer(long)` and decimals → `ToString(CultureInfo.InvariantCulture)`; a negative number reaches here only as the unary node, which the predicate path already renders `(-…)`; text → `fields.RenderStringLiteral(text) ?? throw new NotSupportedException("…text constant holds a character a SQL literal cannot carry…")`; a duration → its microseconds; a `Uuid` or `Timestamp` literal → `NotSupportedException` (no CEL literal reaches here for either today; refusing keeps DDL from gaining a format by accident). A `CelContextRef` and `now()` → `NotSupportedException` naming them. The result is the rendered two-valued fragment (root-collapsed exactly as `Render` collapses its own).

`StoredPredicateSurface.Refusals`: walk `CelTree.Children` from the root; collect a `CelCompilationError` for each `CelContextRef` (`"{slot} reads the row alone, and @user.id/@tenant.id is the caller's — a stored constraint has no caller."`, fix `"Compare two of the row's fields, or a field with a constant."`), each `CelBinary { Operator: In }` (role test), each `CelCall { Name: now }` (`"{slot} is stored and judged on every write, so now() would change its answer with no write — refused."`, fix `"Store the instant in a field with a before-hook mutate (now()) and compare that field."`), and any other `CelCall` (`"{slot} cannot call a function."`). Position 0 (the compiled tree carries none); the caller attaches the descriptor pointer.

- [ ] **Step 4: Run them; then the property on both engines**

Run: `dotnet test --project test/MMLib.Alvo.Tests --filter-class '*SqlPredicateRendererConstraintTests' --filter-class '*StoredPredicateSurfaceTests'`; `dotnet test --project test/MMLib.Alvo.Data.Sqlite.Tests --filter-class '*SqliteCheckLiteralPropertyTests'`; `dotnet test --project test/MMLib.Alvo.Data.PostgreSql.Tests.Integration --filter-class '*PostgreSqlCheckLiteralPropertyTests'`
Expected: PASS.

- [ ] **Step 5: Baseline, format, ring0, commit**

Accept the Abstractions public-API diff (`RenderConstraint` only) and dispatch `alvo-snapshot-judge` (Ruling 17). Then:

```bash
dotnet format whitespace MMLib.Alvo.slnx --include <every .cs file touched>
scripts/test-ring0
git add <the files, by path> test/MMLib.Alvo.Abstractions.Tests/PublicApi.MMLib.Alvo.Abstractions.verified.txt
git commit -m "feat(cel): render stored predicates with inline literals and prove no literal escapes a CHECK" -m "Claude-Session: https://claude.ai/code/session_01NJAtafM29iddLwfhb5gnmB"
```

---

## Task 6: `field.validation` in the descriptor — the rewrite, the findings, the dynamic refusal

**Files:**
- Modify: `src/MMLib.Alvo.Abstractions/Schema/FieldSchema.cs` (`CheckExpression`), `src/MMLib.Alvo/Descriptor/Internal/UnhonouredFeatures.cs` (remove the `validation` entry), `src/MMLib.Alvo/Descriptor/DescriptorToSchemaMapper.cs`, `src/MMLib.Alvo/Descriptor/Internal/DescriptorValidator.cs`, `src/MMLib.Alvo.Data.EntityFrameworkCore/Internal/AppliedSchemaJsonContext.cs` (only if it lists properties explicitly)
- Create: `src/MMLib.Alvo/Descriptor/Internal/FieldValidation.cs`, `src/MMLib.Alvo/Descriptor/Internal/CheckConstraintCheck.cs`
- Test: `test/MMLib.Alvo.Tests/Descriptor/FieldValidationRewriteTests.cs`, `CheckConstraintCheckTests.cs` (new); `test/MMLib.Alvo.Tests/Descriptor/UnhonouredFeaturesTests.cs` (its two verified files move)

**Interfaces:**
- Produces: `FieldSchema.CheckExpression` (`string?`, init; canonical CEL over the row); `FieldValidation.Canonical(string entity, string field, string source, IReadOnlyCollection<string> fieldNames) → string` (throws `InvalidDataException` for the ambiguity, a syntax error or `old.`/`changed()`); `FieldValidation.DynamicRefusal(string entity, string field) → DescriptorValidationError` and `FieldValidation.DynamicMessage(string entity, string field) → string` (the mapper's exception text, the same sentence); `CheckConstraintCheck.Errors(SchemaModel, ICelCompiler) → IEnumerable<DescriptorValidationError>` at `/entities/<e>/fields/<f>/validation`.
- Consumes: `CelParser.Parse`, `CelText.Write`, `StoredPredicateSurface` (Task 5).

- [ ] **Step 1: Write the failing tests**

```csharp
// test/MMLib.Alvo.Tests/Descriptor/FieldValidationRewriteTests.cs
using MMLib.Alvo.Descriptor.Internal;

namespace MMLib.Alvo.Tests.Descriptor;

public sealed class FieldValidationRewriteTests
{
    private static readonly string[] _fields = ["stock", "reserved", "starts_at", "ends_at"];

    [Theory]
    [InlineData("stock", "value >= 0", "stock >= 0")]
    [InlineData("reserved", "value <= new.stock", "reserved <= stock")]
    [InlineData("reserved", "reserved <= stock", "reserved <= stock")]
    [InlineData("ends_at", "!has(starts_at) || value - starts_at <= duration('8h')", "!has(starts_at) || ends_at - starts_at <= duration('28800s')")]
    public void Value_and_new_are_spellings_of_the_row(string field, string source, string canonical) =>
        FieldValidation.Canonical("products", field, source, _fields).ShouldBe(canonical);

    [Theory]
    [InlineData("old.stock >= 0", "old.")]
    [InlineData("changed(stock)", "changed")]
    public void A_transition_is_refused_because_a_check_sees_one_row(string source, string named) =>
        Should.Throw<InvalidDataException>(() => FieldValidation.Canonical("products", "stock", source, _fields)).Message.ShouldContain(named);

    [Fact]
    public void A_bare_value_on_an_entity_with_a_field_named_value_is_refused_as_ambiguous() =>
        Should.Throw<InvalidDataException>(() => FieldValidation.Canonical("readings", "limit", "value > 0", ["value", "limit"]))
            .Message.ShouldSatisfyAllConditions(m => m.ShouldContain("'value'"), m => m.ShouldContain("'limit'"));

    [Fact]
    public void The_field_named_value_may_validate_itself_by_name() =>
        FieldValidation.Canonical("readings", "value", "value > 0", ["value", "limit"]).ShouldBe("value > 0");
}
```

(The canonical text of the duration follows Task 1's `CelText` writer — `duration('28800s')`.)

```csharp
// test/MMLib.Alvo.Tests/Descriptor/CheckConstraintCheckTests.cs
using MMLib.Alvo.Descriptor.Internal;

namespace MMLib.Alvo.Tests.Descriptor;

public sealed class CheckConstraintCheckTests
{
    private static IReadOnlyList<MMLib.Alvo.Descriptor.DescriptorValidationError> Validate(string fieldsJson, string storage = "physical") =>
        new DescriptorValidator().Validate(Descriptors.WithEntity("products", fieldsJson, storage)).Errors;

    [Fact]
    public void A_validation_over_the_row_is_valid() =>
        Validate("""{"stock": {"type": "integer", "validation": "value >= 0"}}""").ShouldBeEmpty();

    [Theory]
    [InlineData("value <= now()", "now()")]
    [InlineData("value == @user.id", "@user")]
    [InlineData("'admin' in @user.roles", "role")]
    [InlineData("value * 2 > 4", "Arithmetic")]
    [InlineData("value > ", "")]
    [InlineData("missing > 0", "missing")]
    public void A_refused_validation_is_reported_at_its_pointer(string expression, string named)
    {
        var error = Validate($$"""{"stock": {"type": "integer", "validation": "{{expression}}"}}""").ShouldHaveSingleItem();
        error.Path.ShouldBe("/entities/products/fields/stock/validation");
        error.Message.ShouldContain(named);
    }

    [Fact]
    public void A_validation_on_a_dynamic_entity_is_refused_naming_41()
    {
        var error = Validate("""{"stock": {"type": "integer", "validation": "value >= 0"}}""", storage: "dynamic")
            .ShouldHaveSingleItem();
        error.Path.ShouldBe("/entities/products/fields/stock/validation");
        error.Message.ShouldContain("#41");
    }

    [Fact]
    public void A_validation_is_no_longer_an_unhonoured_feature() =>
        UnhonouredFeatures.EveryRefusal.ShouldNotContain(refusal => refusal.Slot == "field.validation");
}
```

`Descriptors.WithEntity` is a small helper in the test file (or the existing descriptor-JSON builder the other `Descriptor/*Tests` use — reuse it if one exists) that returns a minimal valid descriptor JSON with one entity, its `fields`, `"storage"` and `rules` all `"true"`. A dynamic entity needs whatever `DescriptorValidator` already requires for one (read an existing dynamic-entity test). The duplicate-error rule: the dynamic refusal is the only finding for that field (its expression is not compiled — there is no table to compile against).

- [ ] **Step 2: Run them to verify they fail**

Run: `dotnet test --project test/MMLib.Alvo.Tests --filter-class '*FieldValidationRewriteTests' --filter-class '*CheckConstraintCheckTests'`
Expected: FAIL.

- [ ] **Step 3: Implement**

- `FieldSchema`: `public string? CheckExpression { get; init; }` with docs: "The field's `validation`, as a CEL predicate over the row that a provider enforces as a CHECK constraint — the declaring field's own empty value passes it; null when the field declares none."
- `FieldValidation.Canonical`: `CelParser.Parse(source, CelFunctionCatalog.BuiltIns)`; a `CelSyntaxException` → `InvalidDataException` carrying its message; walk the tree with a rewriter (`CelFieldRef { State: Current, FieldName: "value" }` → `with { FieldName = field }` unless `fieldNames` contains `"value"` and `field != "value"` → ambiguity; `State: New` → `with { State = Current }`; `State: Old` or `CelChanged` → refusal naming `old.`/`changed()` with fix `"A validation sees one row; a rule about a change belongs in a before-hook condition."`); return `CelText.Write(rewritten)`. The ambiguity message: `"Field '{entity}.{field}' declares a validation reading 'value', and '{entity}' also declares a field named 'value' — so it could mean '{field}' or the field 'value'. Write the field's own name: '{field}'."`
- Mapper (`MapField` or wherever `ComputedExpression` is set): `CheckExpression = field.Validation is { } source ? FieldValidation.Canonical(entityName, fieldName, source, entity.Fields.Keys) : null`. In `Map`, before the physical filter, a dynamic entity with any field declaring `validation` throws `InvalidDataException(FieldValidation.DynamicMessage(entity, field))` (the mapper's exception channel, as `RefuseWildcard` does).
- `UnhonouredFeatures.OnAField`: delete the `validation` entry (the class remarks' list and `EveryFixSuggestion` follow automatically); accept the two moved `UnhonouredFeaturesTests.*.verified.txt` after reading them (they lose exactly `field.validation`), and dispatch `alvo-snapshot-judge`.
- `DescriptorValidator.FieldSemanticErrors`: a field with `validation` on an entity whose `storage` is `dynamic` yields `FieldValidation.DynamicRefusal(entity, field)` (the wire text in Global Constraints).
- `CheckConstraintCheck.Errors`: for every field with `CheckExpression`: compile in `CelProfile.Rule` against its entity; compile errors and `StoredPredicateSurface.Refusals(expression, "A validation")` become findings at the pointer, message prefixed `"Field '<e>.<f>' declares a 'validation' that "` + the compiler's message; then `new SqlPredicateRenderer().RenderConstraint(expression, NeutralFields.Instance)` inside a `try` — a `NotSupportedException` becomes a finding (a text constant with a control character). `ComputedFieldCheck.NeutralFields` becomes `internal` (or move it to `Descriptor/Internal/NeutralFields.cs`) and gains `RenderEpochMicroseconds(x) => $"EPOCH_US({x})"` and `RenderStringLiteral` mirroring `AlvoSqlStringLiteral`'s decline rule. Registered in `DescriptorValidator.CompiledErrors` beside `ComputedFieldCheck.Errors`.
- A mapping exception from `FieldValidation.Canonical` reaches the validator through `TryReadModel`'s `InvalidDataException` catch — which today returns "already reported by the semantic pass". Make the semantic pass report it: in `FieldSemanticErrors`, call `FieldValidation.Canonical` inside a `try` and turn its `InvalidDataException` into the finding at the pointer, so the catch in `TryReadModel` stays a non-reporting one.

- [ ] **Step 4: Run them, then the descriptor suites**

Run: `dotnet test --project test/MMLib.Alvo.Tests --filter-class '*FieldValidationRewriteTests' --filter-class '*CheckConstraintCheckTests' --filter-class '*UnhonouredFeaturesTests' --filter-class '*DescriptorValidator*' --filter-class '*DescriptorToSchemaMapperTests'`; `dotnet test --project test/MMLib.Alvo.Schema.Tests`
Expected: PASS. `DescriptorToSchemaMapperTests.Complex_crm_without_its_unhonoured_features…verified.txt` changes only if `examples/` or its fixture declares a `validation` — read the diff, accept, judge.

- [ ] **Step 5: Baseline, format, ring0, commit**

Accept the Abstractions public-API diff (`FieldSchema.CheckExpression`) — Ruling 18 — and judge it.

```bash
dotnet format whitespace MMLib.Alvo.slnx --include <every .cs file touched>
scripts/test-ring0
git add <the files, by path> test/MMLib.Alvo.Abstractions.Tests/PublicApi.MMLib.Alvo.Abstractions.verified.txt test/MMLib.Alvo.Tests/UnhonouredFeaturesTests.*.verified.txt
git commit -m "feat(descriptor): honour field.validation as a CEL predicate over the row" -m "Claude-Session: https://claude.ai/code/session_01NJAtafM29iddLwfhb5gnmB"
```

---

## Task 7: `CHECK` in the database — the DDL on both engines, the decode, the 422

**Files:**
- Modify: `src/MMLib.Alvo.Abstractions/Data/AlvoConstraintKind.cs` (`Check`), `AlvoConstraintViolationException.cs` (`Check` message), `src/MMLib.Alvo.Abstractions/Rules/PolicyDecision.cs` (internal `Unnameable`), the core `PolicyEngine` that builds decisions (populate it from `EntityPolicy.Flagged(policy.Hidden)`)
- Create: `src/MMLib.Alvo.Data.EntityFrameworkCore/Internal/CheckConstraintNames.cs`, `CheckConstraintSql.cs`
- Modify: `src/MMLib.Alvo.Data.EntityFrameworkCore/Internal/DescriptorModelBuilder.cs`, `DestructiveScan.cs`, `ConstraintViolationTranslator.cs`, `EfAlvoData*.cs` (pass the applied `SchemaModel` and the decision's `Unnameable` to the translator), `BatchWrite.cs` (Check beside Unique), `AlvoEfCoreProvider.cs` (construct `CheckConstraintSql` where `ComputedColumnSql` is), `EfCoreSchemaMigrator.cs` (the rebuild framing and cost note), `src/MMLib.Alvo.Data.Sqlite/SqliteSqlDialect.cs`, `src/MMLib.Alvo.Data.PostgreSql/PostgreSqlSqlDialect.cs`, `src/MMLib.Alvo/Api/Internal/ProblemResultFactory.cs`
- Create (tests): `src/MMLib.Alvo.Testing/Data/AlvoDataCheckConstraintTests.cs` (abstract), its subclasses `test/MMLib.Alvo.Data.Sqlite.Tests/SqliteCheckConstraintTests.cs` and `test/MMLib.Alvo.Data.PostgreSql.Tests.Integration/PostgreSqlCheckConstraintTests.cs`; `test/MMLib.Alvo.Data.EntityFrameworkCore.Tests/CheckConstraintNamesTests.cs`; `test/MMLib.Alvo.Tests/Api/CheckViolationAnswerTests.cs`; two goldens `test/MMLib.Alvo.Data.Sqlite.Tests/SqliteCheckDdlTests.The_rebuild_carries_the_check.verified.txt` and the PostgreSQL twin

**Interfaces:**
- Produces: `AlvoConstraintKind.Check`; `CheckConstraintNames.For(string entity, string field) → string`, `CheckConstraintNames.Owner(SchemaModel schema, string constraintName) → (EntitySchema Entity, FieldSchema Field)?`; `CheckConstraintSql.For(EntitySchema, FieldSchema) → string?` = `({subject} IS NULL OR {RenderConstraint})`; `SqlConstraintViolation { Kind = Check, ConstraintName = … }` from both dialects; `AlvoConstraintViolationException(AlvoConstraintKind.Check, fields)` with `fields` = `[pointer field]` or `[]`; `PolicyDecision.Unnameable` (internal `IReadOnlySet<string>`, default empty).
- Consumes: `FieldSchema.CheckExpression` (Task 6), `IPredicateRenderer.RenderConstraint` (Task 5).

- [ ] **Step 1: Write the failing tests**

```csharp
// src/MMLib.Alvo.Testing/Data/AlvoDataCheckConstraintTests.cs
using MMLib.Alvo.Data;
using MMLib.Alvo.Descriptor;
using MMLib.Alvo.Schema;
using Shouldly;
using Xunit;
using DescField = MMLib.Alvo.Descriptor.FieldType;
using SchemaField = MMLib.Alvo.Schema.FieldType;

namespace MMLib.Alvo.Testing.Data;

/// <summary>
/// A field's validation is a CHECK constraint the engine itself enforces (HX-11), asked of both shipped drivers. Not in
/// the adversarial suite: the in-memory reference implementation has no engine to refuse a write (Ruling 28).
/// </summary>
public abstract class AlvoDataCheckConstraintTests
{
    /// <summary>Stands up an engine over the products entity of <see cref="Descriptor"/>; see AlvoDataComputedRollupTests.CreateAsync.</summary>
    protected abstract Task<IAlvoData> CreateAsync(SchemaModel schema, AlvoDescriptor descriptor);

    /// <summary>Runs one statement outside the port and answers the engine's failure, or null when it accepted it.</summary>
    protected abstract Task<Exception?> ExecuteOutsideThePortAsync(string sql);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task A_write_that_breaks_a_validation_is_refused_naming_the_field()
    {
        var data = await CreateAsync(Schema, Descriptor);

        var refusal = await Should.ThrowAsync<AlvoConstraintViolationException>(
            () => data.CreateAsync("products", Row(stock: -1), Caller, cancellationToken: Ct));

        refusal.Kind.ShouldBe(AlvoConstraintKind.Check);
        refusal.Fields.ShouldBe(["stock"]);
        (await data.QueryAsync(new AlvoQuery { Entity = "products" }, Caller, Ct)).Items.ShouldBeEmpty();
    }

    [Fact]
    public async Task The_engine_itself_refuses_an_out_of_band_write_that_breaks_it()
    {
        await CreateAsync(Schema, Descriptor);
        (await ExecuteOutsideThePortAsync(
            "INSERT INTO \"products\" (\"id\", \"stock\", \"discount\") VALUES ('00000000-0000-0000-0000-000000000001', -5, NULL)"))
            .ShouldNotBeNull("a CHECK is the engine's, so a write that bypasses Alvo is refused too");
    }

    [Fact]
    public async Task An_empty_validated_field_passes_its_check()
    {
        var data = await CreateAsync(Schema, Descriptor);
        var row = await data.CreateAsync("products", Row(stock: 3), Caller, cancellationToken: Ct);
        row["discount"].ShouldBeNull();
    }

    [Fact]
    public async Task A_validation_reading_an_empty_other_field_refuses_the_row()
    {
        var data = await CreateAsync(Schema, Descriptor);
        var refusal = await Should.ThrowAsync<AlvoConstraintViolationException>(() => data.CreateAsync(
            "products",
            new Dictionary<string, object?>(StringComparer.Ordinal) { ["stock"] = 3L, ["sale_ends_at"] = Instant },
            Caller,
            cancellationToken: Ct));
        refusal.Fields.ShouldBe(["sale_ends_at"]);
    }

    [Fact]
    public async Task A_hidden_field_s_refusal_names_nothing()
    {
        var data = await CreateAsync(Schema, Descriptor);
        var refusal = await Should.ThrowAsync<AlvoConstraintViolationException>(() => data.CreateAsync(
            "products",
            new Dictionary<string, object?>(StringComparer.Ordinal) { ["stock"] = 3L, ["margin"] = -1m },
            Caller,
            cancellationToken: Ct));
        (refusal.Kind, refusal.Fields.Count).ShouldBe((AlvoConstraintKind.Check, 0));
    }

    [Fact]
    public async Task An_update_that_breaks_it_leaves_the_row_as_it_was()
    {
        var data = await CreateAsync(Schema, Descriptor);
        var id = (Guid)(await data.CreateAsync("products", Row(stock: 3), Caller, cancellationToken: Ct))["id"]!;

        await Should.ThrowAsync<AlvoConstraintViolationException>(() => data.UpdateAsync(
            "products", id, new Dictionary<string, object?>(StringComparer.Ordinal) { ["stock"] = -1L }, Caller, cancellationToken: Ct));

        (await data.GetAsync("products", id, Caller, Ct))!["stock"].ShouldBe(3L);
    }

    [Fact]
    public async Task A_duration_validation_holds_in_the_engine()
    {
        var data = await CreateAsync(Schema, Descriptor);
        var refusal = await Should.ThrowAsync<AlvoConstraintViolationException>(() => data.CreateAsync(
            "products",
            new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["stock"] = 1L, ["sale_starts_at"] = Instant, ["sale_ends_at"] = Instant.AddDays(31),
            },
            Caller,
            cancellationToken: Ct));
        refusal.Fields.ShouldBe(["sale_ends_at"]);
        await data.CreateAsync("products", new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["stock"] = 1L, ["sale_starts_at"] = Instant, ["sale_ends_at"] = Instant.AddDays(30),
        }, Caller, cancellationToken: Ct);
    }

    private static readonly DateTimeOffset Instant = new(2026, 11, 9, 10, 0, 0, TimeSpan.Zero);

    private static Dictionary<string, object?> Row(long stock) => new(StringComparer.Ordinal) { ["stock"] = stock };

    protected static AlvoContext Caller { get; } = new() { User = UserId.New(), Roles = new HashSet<Role> { Role.Authenticated } };

    /// <summary>
    /// products: stock (validation "value >= 0"), discount (optional, "value <= 50"), margin (hidden, "value >= 0"),
    /// sale_starts_at / sale_ends_at (sale_ends_at: "value - sale_starts_at <= duration('720h')").
    /// </summary>
    protected static AlvoDescriptor Descriptor => new()
    {
        ApiVersion = "alvo.dev/v1",
        Name = "check-constraints",
        Entities = new Dictionary<string, EntityDescriptor>(StringComparer.Ordinal)
        {
            ["products"] = new()
            {
                Tenancy = EntityTenancy.Global,
                Fields = new Dictionary<string, FieldDescriptor>(StringComparer.Ordinal)
                {
                    ["stock"] = new() { Type = DescField.Integer, Required = true, Validation = "value >= 0" },
                    ["discount"] = new() { Type = DescField.Integer, Validation = "value <= 50" },
                    ["margin"] = new() { Type = DescField.Decimal, Precision = 10, Scale = 2, Hidden = BoolOrCel.FromBoolean(true), Validation = "value >= 0" },
                    ["sale_starts_at"] = new() { Type = DescField.DateTime },
                    ["sale_ends_at"] = new() { Type = DescField.DateTime, Validation = "value - sale_starts_at <= duration('720h')" },
                },
                Rules = new AccessRules { List = "true", Get = "true", Create = "true", Update = "true", Delete = "true" },
            },
        },
    };

    /// <summary>The same entity as a schema; each CheckExpression is the canonical form the mapper would write.</summary>
    protected static SchemaModel Schema => new([
        new EntitySchema
        {
            Name = "products",
            Tenancy = TenancyMode.Global,
            Fields =
            [
                new FieldSchema { Name = "id", Type = SchemaField.Uuid, Required = true },
                new FieldSchema { Name = "stock", Type = SchemaField.Integer, Required = true, CheckExpression = "stock >= 0" },
                new FieldSchema { Name = "discount", Type = SchemaField.Integer, Nullable = true, CheckExpression = "discount <= 50" },
                new FieldSchema { Name = "margin", Type = SchemaField.Decimal, Nullable = true, Precision = 10, Scale = 2, CheckExpression = "margin >= 0" },
                new FieldSchema { Name = "sale_starts_at", Type = SchemaField.DateTime, Nullable = true },
                new FieldSchema
                {
                    Name = "sale_ends_at", Type = SchemaField.DateTime, Nullable = true,
                    CheckExpression = "sale_ends_at - sale_starts_at <= duration('2592000s')",
                },
            ],
        },
    ]);
}
```

If `EntitySchema` in the rollup suite's builders sets more members (`Audit`, managed columns), copy what `AlvoDataComputedRollupTests.Schema` sets. The `CheckExpression`s are the canonical forms because a hand-built `SchemaModel` does not pass through the mapper; `margin`'s hidden flag reaches `PolicyDecision.Unnameable` through the descriptor's compiled policy.

Subclass in each engine project exactly as `SqliteComputedRollupTests`/`PostgreSqlComputedRollupTests` subclass the rollup suite (find them with `grep -rln "AlvoDataComputedRollupTests" test`).

```csharp
// test/MMLib.Alvo.Data.EntityFrameworkCore.Tests/CheckConstraintNamesTests.cs
namespace MMLib.Alvo.Data.EntityFrameworkCore.Tests;

public sealed class CheckConstraintNamesTests
{
    [Fact]
    public void A_short_name_is_the_entity_and_the_field() =>
        CheckConstraintNames.For("products", "stock").ShouldBe("ck_products_stock");

    [Fact]
    public void A_long_name_is_cut_to_63_bytes_with_a_stable_hash()
    {
        var name = CheckConstraintNames.For(new string('e', 63), new string('f', 63));
        name.Length.ShouldBe(63);
        name.ShouldStartWith("ck_eeee");
        name.ShouldBe(CheckConstraintNames.For(new string('e', 63), new string('f', 63)));
        name.ShouldNotBe(CheckConstraintNames.For(new string('e', 63), new string('f', 62) + "g"));
    }

    [Fact]
    public void The_owner_is_found_by_recomputing_never_by_parsing() =>
        CheckConstraintNames.Owner(CheckSchemas.Products, "ck_products_stock")!.Value.Field.Name.ShouldBe("stock");
}
```

```csharp
// test/MMLib.Alvo.Tests/Api/CheckViolationAnswerTests.cs
using MMLib.Alvo.Api.Internal;
using MMLib.Alvo.Data;

namespace MMLib.Alvo.Tests.Api;

public sealed class CheckViolationAnswerTests
{
    [Fact]
    public async Task A_check_on_a_named_field_is_422_validation_with_its_pointer()
    {
        var (status, type, violations) = await ProblemShape.Of(ProblemResultFactory.PortRefusalFor(
            new AlvoConstraintViolationException(AlvoConstraintKind.Check, ["stock"]))!);
        (status, type).ShouldBe((422, AlvoProblemTypes.Validation));
        violations.ShouldBe([("/stock", "check")]);
    }

    [Fact]
    public async Task A_check_naming_no_field_points_at_the_document()
    {
        var (status, _, violations) = await ProblemShape.Of(ProblemResultFactory.PortRefusalFor(
            new AlvoConstraintViolationException(AlvoConstraintKind.Check, []))!);
        status.ShouldBe(422);
        violations.ShouldBe([("", "check")]);
    }
}
```

(`ProblemShape.Of` executes the `IResult` against a `DefaultHttpContext` and reads status, `type` and `violations` — reuse the helper the existing `ProblemResultFactory` tests use; find it with `grep -rln "PortRefusalFor" test/MMLib.Alvo.Tests`.)

The DDL goldens: one Verify fact per engine that plans an empty → products migration and snapshots `plan.Sql` — on SQLite the `CREATE TABLE` carries `CONSTRAINT "ck_products_stock" CHECK (("stock" IS NULL OR COALESCE("stock" >= 0, 0)))`; on PostgreSQL the same with `FALSE`. A second SQLite golden plans *adding* a validation to an existing populated table and must show EF's rebuild (`CREATE TABLE "ef_temp_products" … CHECK …`, `INSERT INTO "ef_temp_products" … SELECT …`, `DROP TABLE "products"`, `ALTER TABLE "ef_temp_products" RENAME TO "products"`) inside the dialect's migration framing.

- [ ] **Step 2: Run them to verify they fail**

Run: `dotnet test --project test/MMLib.Alvo.Data.Sqlite.Tests --filter-class '*SqliteCheckConstraintTests' --filter-class '*SqliteCheckDdlTests'`; `dotnet test --project test/MMLib.Alvo.Data.EntityFrameworkCore.Tests --filter-class '*CheckConstraintNamesTests'`; `dotnet test --project test/MMLib.Alvo.Tests --filter-class '*CheckViolationAnswerTests'`
Expected: FAIL.

- [ ] **Step 3: Implement the DDL**

```csharp
// src/MMLib.Alvo.Data.EntityFrameworkCore/Internal/CheckConstraintNames.cs
using MMLib.Alvo.Schema;
using System.Security.Cryptography;
using System.Text;

namespace MMLib.Alvo.Data.EntityFrameworkCore;

/// <summary>The one name a field's CHECK constraint has, computed and never parsed (HX-11 Ruling 19).</summary>
internal static class CheckConstraintNames
{
    private const int MaxBytes = 63;

    internal static string For(string entity, string field)
    {
        var name = $"ck_{entity}_{field}";
        return Encoding.UTF8.GetByteCount(name) <= MaxBytes ? name : Hashed(entity, field);
    }

    internal static (EntitySchema Entity, FieldSchema Field)? Owner(SchemaModel schema, string constraintName) =>
        schema.Entities
            .SelectMany(entity => entity.Fields.Where(field => field.CheckExpression is not null).Select(field => (entity, field)))
            .Where(pair => string.Equals(For(pair.entity.Name, pair.field.Name), constraintName, StringComparison.Ordinal))
            .Select(pair => ((EntitySchema, FieldSchema)?)pair)
            .FirstOrDefault();

    private static string Hashed(string entity, string field)
    {
        var hash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes($"{entity}.{field}")))[..8];
        return $"ck_{$"{entity}_{field}"[..51]}_{hash}";
    }
}
```

(Identifiers are `^[a-z][a-z0-9_-]{0,62}$` — ASCII — so a character is a byte and `[..51]` is safe.)

`CheckConstraintSql` mirrors `ComputedColumnSql`: `For(entity, field)` compiles `CheckExpression` with `ICelCompiler` in `CelProfile.Rule`, renders `IPredicateRenderer.RenderConstraint`, and answers `$"({_fields.RenderField(entity, field.Name)} IS NULL OR {rendered})"`; a compile failure or a `NotSupportedException` becomes a `DescriptorValidationException` at `/entities/<e>/fields/<f>/validation` (defence in depth — the validator refused it first). `DescriptorModelBuilder.ConfigureEntity`: after the fields, for each field with `CheckExpression`, `entityBuilder.ToTable(entity.Name, table => table.HasCheckConstraint(CheckConstraintNames.For(entity.Name, field.Name), checks.For(entity, field)))` — one `ToTable` call carrying every constraint (read how `ToTable(entity.Name)` is called today and fold the constraints into it). A `null` `CheckConstraintSql` with a declared validation refuses like `EnsureComputedIsRenderable`.

`DestructiveScan.Classify` gains `AddCheckConstraintOperation op` → `SchemaChange { Kind = AlterField, Entity = op.Table, Detail = "Adds the field's validation as a CHECK constraint." }` and `DropCheckConstraintOperation op` → `"Drops a field's validation."`, both non-destructive; `EfCoreSchemaMigrator.BuildSemanticSteps` fills `Field` from `CheckConstraintNames.Owner(desired or current, op.Name)`. On SQLite (`GeneratedColumnAddRequiresTableRebuild`), `CostNote` gives an `AddCheckConstraintOperation` on an existing table the rebuild note.

- [ ] **Step 4: Implement the decode and the answer**

- `SqliteSqlDialect.DecodeConstraintViolation`: `private const int ConstraintCheck = 275;` arm `ConstraintCheck => new SqlConstraintViolation { Kind = AlvoConstraintKind.Check, ConstraintName = CheckName(sqlite.Message) }`, `CheckName` reading the text after `"CHECK constraint failed: "` up to the end or a quote.
- `PostgreSqlSqlDialect`: `private const string CheckViolation = "23514";` arm `CheckViolation => new SqlConstraintViolation { Kind = AlvoConstraintKind.Check, ConstraintName = NullIfEmpty(postgres.ConstraintName) }`.
- `ConstraintViolationTranslator.Translate` gets the applied `SchemaModel` and the decision's `Unnameable` set (thread them from `EfAlvoData`'s call sites — `db.AppliedSchema` and the write's `PolicyDecision`): a `Check` violation → `CheckFields(violation, written, schema, unnameable)`:

```csharp
    private static IReadOnlyList<string> CheckFields(
        SqlConstraintViolation violation, EntitySchema written, SchemaModel schema, IReadOnlySet<string> unnameable)
    {
        if (violation.ConstraintName is not { } name || CheckConstraintNames.Owner(schema, name) is not { } owner)
        {
            return [];
        }

        var field = string.Equals(owner.Entity.Name, written.Name, StringComparison.Ordinal)
            ? owner.Field.Name
            : RolledUpChildField(owner.Field, written);
        return field is null || unnameable.Contains(field) ? [] : [field];
    }

    private static string? RolledUpChildField(FieldSchema parentField, EntitySchema written) =>
        parentField.Rollup is { Op: not RollupOperation.Count, Field: { } child } rollup
        && string.Equals(rollup.From, written.Name, StringComparison.Ordinal)
            ? child
            : null;
```

  A `Check` never returns `null` from `Translate` (unlike a Unique whose columns are all managed): the refusal is always raised, naming nothing when it cannot name safely (Rulings 24, 25). **The rollup recompute must run inside the translated scope**: read where `RollupRecompute.ForChildWriteAsync` is awaited in `EfAlvoData` and confirm it is inside the lambda `ConstraintViolationTranslator.TranslatedAsync` wraps; if it is not, move the wrap out so a parent's `CHECK` failing during the recompute is translated (acceptance (1) depends on it — Task 10's oversell fact goes 500 otherwise).
- `AlvoConstraintKind.Check` with docs ("A row broke a CHECK constraint — a field's validation"). `AlvoConstraintViolationException.MessageFor`: `Check => CheckMessage` (Global Constraints' text).
- `PolicyDecision`: `internal IReadOnlySet<string> Unnameable { get; init; } = FrozenSet<string>.Empty;`; the core policy engine sets it from the compiled entity policy's `EntityPolicy.Flagged(policy.Hidden)` when it builds an allow decision.
- `ProblemResultFactory.PortRefusalFor`: `AlvoConstraintViolationException { Kind: AlvoConstraintKind.Check } check => Problem(StatusCodes.Status422UnprocessableEntity, AlvoProblemTypes.Validation, CheckViolations(check))` above the `Conflict` arm, with `CheckViolations` building `AlvoViolation("/" + field, "check", <named message>, <named fix>)` per field, or one `AlvoViolation("", "check", <document message>, <document fix>)` (Global Constraints' texts). `CodeFor`/`FixFor` gain the `Check` arms for completeness.
- `BatchWrite` and every other site: `grep -rn "AlvoConstraintKind.Unique" src/MMLib.Alvo.Data.EntityFrameworkCore src/MMLib.Alvo` and give each site the `Check` arm Ruling 27 asks for.

- [ ] **Step 5: Run the tests on both engines**

Run: `dotnet test --project test/MMLib.Alvo.Data.Sqlite.Tests --filter-class '*SqliteCheckConstraintTests' --filter-class '*SqliteCheckDdlTests' --filter-class '*SqliteComputedRollupTests'`; `dotnet test --project test/MMLib.Alvo.Data.PostgreSql.Tests.Integration --filter-class '*PostgreSqlCheckConstraintTests' --filter-class '*PostgreSqlCheckDdlTests'`; `dotnet test --project test/MMLib.Alvo.Data.EntityFrameworkCore.Tests --filter-class '*CheckConstraintNamesTests' --filter-class '*DestructiveScan*'`; `dotnet test --project test/MMLib.Alvo.Tests --filter-class '*CheckViolationAnswerTests' --filter-class '*ProblemResultFactory*'`
Expected: PASS. Accept the two DDL goldens after reading them; dispatch `alvo-snapshot-judge`.

- [ ] **Step 6: Baseline, format, ring0, commit**

Accept the Abstractions public-API diff (`AlvoConstraintKind.Check`). Then:

```bash
dotnet format whitespace MMLib.Alvo.slnx --include <every .cs file touched>
scripts/test-ring0
git add <the files, by path> <the goldens> test/MMLib.Alvo.Abstractions.Tests/PublicApi.MMLib.Alvo.Abstractions.verified.txt
git commit -m "feat(data): enforce field.validation as a CHECK constraint answered 422 with a pointer" -m "Claude-Session: https://claude.ai/code/session_01NJAtafM29iddLwfhb5gnmB"
```

---

## Task 8: The pre-apply violation scan, and a `CHECK` refused during the apply

**Files:**
- Create: `src/MMLib.Alvo.Data.EntityFrameworkCore/Internal/CheckViolationScan.cs`
- Modify: `src/MMLib.Alvo.Data.EntityFrameworkCore/EfCoreSchemaMigrator.cs` (`PlanAsync` becomes truly async and scans; `ApplyAsync` translates)
- Test: `src/MMLib.Alvo.Testing/Data/AlvoDataCheckConstraintTests.cs` (apply facts, both engines)

**Interfaces:**
- Produces: `CheckViolationScan.CountAsync(DbConnection connection, string table, string checkSql, CancellationToken ct) → Task<long>`; `PlanAsync` throws `DescriptorValidationException` (Global Constraints' count text) for a non-zero count; `ApplyAsync` turns a decoded `Check` violation into `DescriptorValidationException` (the no-count text) at the owning step's validation pointer.
- Consumes: `CheckConstraintNames`, `CheckConstraintSql` (Task 7); the abstract suite's `CreateAsync`/`ExecuteOutsideThePortAsync`, plus a new abstract `ApplyAsync(SchemaModel current, SchemaModel desired)` the subclasses implement through their `ISchemaMigrator` (as the rollup suite's apply facts do — read `A_computed_field_can_be_added_to_an_entity_that_already_holds_a_row` and reuse its apply seam).

- [ ] **Step 1: Write the failing tests** (in `AlvoDataCheckConstraintTests`)

```csharp
    [Fact]
    public async Task Adding_a_validation_over_rows_that_break_it_is_refused_with_their_count()
    {
        var data = await CreateAsync(Unvalidated, Descriptor);
        foreach (var stock in new long[] { -1, -2, 5 })
        {
            await data.CreateAsync("products", Row(stock), Caller, cancellationToken: Ct);
        }

        var refusal = await Should.ThrowAsync<DescriptorValidationException>(() => PlanAsync(Unvalidated, Schema));

        var error = refusal.Result.Errors.ShouldHaveSingleItem();
        error.Path.ShouldBe("/entities/products/fields/stock/validation");
        error.Message.ShouldStartWith("2 row(s) of 'products' break this validation");
        (await data.QueryAsync(new AlvoQuery { Entity = "products" }, Caller, Ct)).Items.Count.ShouldBe(3, "nothing was applied");
    }

    [Fact]
    public async Task Adding_a_validation_over_compliant_rows_applies_and_holds_from_then_on()
    {
        var data = await CreateAsync(Unvalidated, Descriptor);
        await data.CreateAsync("products", Row(5), Caller, cancellationToken: Ct);

        await ApplyAsync(Unvalidated, Schema);

        (await ExecuteOutsideThePortAsync("UPDATE \"products\" SET \"stock\" = -1")).ShouldNotBeNull();
    }

    [Fact]
    public async Task Rows_written_after_the_count_are_still_refused_at_the_validation()
    {
        var data = await CreateAsync(Unvalidated, Descriptor);
        var plan = await PlanAsync(Unvalidated, Schema);
        await data.CreateAsync("products", Row(-1), Caller, cancellationToken: Ct);

        var refusal = await Should.ThrowAsync<DescriptorValidationException>(() => ExecuteAsync(plan));

        var error = refusal.Result.Errors.ShouldHaveSingleItem();
        error.Path.ShouldBe("/entities/products/fields/stock/validation");
        error.Message.ShouldStartWith("Rows of 'products' break this validation");
    }

    [Fact]
    public async Task Renaming_a_validated_field_over_compliant_rows_applies_and_over_violating_rows_is_refused_at_its_validation()
    {
        var data = await CreateAsync(Unvalidated, Descriptor);
        await data.CreateAsync("products", Row(5), Caller, cancellationToken: Ct);
        await ApplyAsync(Unvalidated, RenamedAndValidated);

        var violating = await CreateAsync(Unvalidated, Descriptor);
        await violating.CreateAsync("products", Row(-1), Caller, cancellationToken: Ct);
        var refusal = await Should.ThrowAsync<DescriptorValidationException>(() => ApplyAsync(Unvalidated, RenamedAndValidated));
        refusal.Result.Errors.ShouldHaveSingleItem().Path.ShouldBe("/entities/products/fields/on_hand/validation");
    }
```

`Unvalidated` is `Schema` with every `CheckExpression` null; `RenamedAndValidated` renames `stock` → `on_hand` (`RenamedFrom = "stock"`) with `CheckExpression = "on_hand >= 0"`. `PlanAsync`, `ApplyAsync` (plan + execute) and `ExecuteAsync(plan)` are the suite's thin wrappers over the engine's `ISchemaMigrator`.

- [ ] **Step 2: Run them to verify they fail**

Run: `dotnet test --project test/MMLib.Alvo.Data.Sqlite.Tests --filter-class '*SqliteCheckConstraintTests'`
Expected: FAIL — the plan applies (SQLite's rebuild then fails raw) instead of refusing with a count.

- [ ] **Step 3: Implement**

```csharp
// src/MMLib.Alvo.Data.EntityFrameworkCore/Internal/CheckViolationScan.cs
using System.Data.Common;

namespace MMLib.Alvo.Data.EntityFrameworkCore;

/// <summary>Counts the rows a CHECK about to be added would refuse — every tenant's, no row named (HX-11 Rulings 22, 23).</summary>
internal static class CheckViolationScan
{
    internal static async Task<long> CountAsync(DbConnection connection, string table, string checkSql, CancellationToken ct)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = $"SELECT COUNT(*) FROM {table} WHERE NOT ({checkSql})";
        return Convert.ToInt64(await command.ExecuteScalarAsync(ct).ConfigureAwait(false), System.Globalization.CultureInfo.InvariantCulture);
    }
}
```

In `EfCoreSchemaMigrator.PlanAsync` (now `async`): after `ComputeResidualDiff`, for every `AddCheckConstraintOperation` in `residual` whose table is in `current` (not a `CreateTableOperation` in the same plan) and is not the subject of a declared rename (`prePass.Renames`) of the table or of the owning field: render the table with `_dialect.RenderTable(entity, null)`, take `op.Sql`, and `CountAsync` over a connection from `_connections.Create()` (opened, disposed in the method). A positive count → `DescriptorValidationException(new DescriptorValidationResult([new(pointer, CountMessage(count, entity), Fix, Error)]))` with the pointer from `CheckConstraintNames.Owner(desired, op.Name)`. `ApplyAsync`: wrap `ExecuteFramedAsync` in a `catch (Exception failure) when (CheckRefusal(failure, plan) is { } refused) { throw refused; }`: decode with `_dialect.DecodeConstraintViolation`, and map the decoded name to the plan step whose `Change.Kind == AlterField` and whose `(Entity, Field)` gives that name through `CheckConstraintNames.For` — its pointer is `/entities/<Entity>/fields/<Field>/validation`. Every new method ≲ 25 lines; the rename check reads `prePass.Renames`' `SchemaChange`s (`FromName`).

- [ ] **Step 4: Run them on both engines**

Run: `dotnet test --project test/MMLib.Alvo.Data.Sqlite.Tests --filter-class '*SqliteCheckConstraintTests'`; `dotnet test --project test/MMLib.Alvo.Data.PostgreSql.Tests.Integration --filter-class '*PostgreSqlCheckConstraintTests'`; `dotnet test --project test/MMLib.Alvo.Tests --filter-class '*RuntimeSchemaService*' --filter-class '*DescriptorBootPlan*'`
Expected: PASS.

- [ ] **Step 5: Format, ring0, commit**

```bash
dotnet format whitespace MMLib.Alvo.slnx --include <every .cs file touched>
scripts/test-ring0
git add <the files, by path>
git commit -m "feat(migrations): count rows that break a new validation and refuse the apply with the count" -m "Claude-Session: https://claude.ai/code/session_01NJAtafM29iddLwfhb5gnmB"
```

---
## Task 9: `rollup.where` — the filter, the recompute, the backfill, the race **[slice end]**

**Files:**
- Modify: `src/MMLib.Alvo.Abstractions/Schema/RollupSchema.cs` (`Where`), `src/MMLib.Alvo/Descriptor/Internal/RollupResolver.cs` (drop `EnsureNoFilter`, carry `Where`), `src/MMLib.Alvo/Descriptor/Internal/UnhonouredFeatures.cs` (remove `RollupWhere`), `src/MMLib.Alvo.Data.EntityFrameworkCore/Internal/RollupRecompute.cs`, `EfCoreSchemaMigrator.cs` (append the backfill), `AlvoEfCoreProvider.cs` (wiring)
- Create: `src/MMLib.Alvo/Descriptor/Internal/RollupFilterCheck.cs`, `src/MMLib.Alvo.Data.EntityFrameworkCore/Internal/RollupBackfill.cs`, `RawSqlFormat.cs`
- Test: `test/MMLib.Alvo.Tests/Descriptor/RollupFilterCheckTests.cs`, `test/MMLib.Alvo.Data.EntityFrameworkCore.Tests/RollupRecomputeFilterTests.cs` (new); `src/MMLib.Alvo.Testing/Data/AlvoDataComputedRollupTests.cs` (new facts, both engines); `test/MMLib.Alvo.Data.PostgreSql.Tests.Integration/PostgreSqlRollupRaceTests.cs` (one fact)

**Interfaces:**
- Produces: `RollupSchema.Where` (`string?`, init — the authored CEL over the child); `RollupFilterCheck.Errors(SchemaModel, ICelCompiler)` at `/entities/<parent>/fields/<field>/rollup/where`; `RawSqlFormat.Escape(string sql) → string`; `RollupRecompute` constructed with an `ICelCompiler` and an `IPredicateRenderer` beside its dialect and fields; `RollupBackfill.Statements(SchemaModel current, SchemaModel desired) → IReadOnlyList<string>`.
- Consumes: `RenderConstraint`, `StoredPredicateSurface` (Task 5).

- [ ] **Step 1: Write the failing tests**

```csharp
// test/MMLib.Alvo.Data.EntityFrameworkCore.Tests/RollupRecomputeFilterTests.cs
namespace MMLib.Alvo.Data.EntityFrameworkCore.Tests;

public sealed class RollupRecomputeFilterTests
{
    [Fact]
    public void The_filter_is_anded_into_the_aggregate_s_subquery() =>
        RollupFixtures.Recompute().Setter(RollupFixtures.Lines, RollupFixtures.HeldTotal)
            .ShouldBe("\"held_total\" = (SELECT SUM(\"quantity\") FROM \"reservations\" WHERE \"product_id\" = {0} AND (COALESCE(\"status\" = 'held', FALSE)))");

    [Fact]
    public void A_filter_constant_with_braces_is_written_doubled_and_matched_literally() =>
        RollupFixtures.Recompute().Setter(RollupFixtures.Lines, RollupFixtures.Filtered("label == '{0}'"))
            .ShouldEndWith("AND (COALESCE(\"label\" = '{{0}}', FALSE)))");

    [Fact]
    public void Escape_doubles_both_braces_and_nothing_else() =>
        RawSqlFormat.Escape("a{b}c'{{").ShouldBe("a{{b}}c'{{{{");
}
```

`RollupFixtures` builds a `RollupRecompute` over the T-SQL-free test field renderer the existing `RollupLockStatementTests` uses (or `PostgreSqlFieldSqlRenderer` + `PostgreSqlSqlDialect` if that suite uses them — read it and reuse its construction), a real `CelCompiler`/`SqlPredicateRenderer` (the EF tests project reaches core types the way its other tests do; if it does not reference `MMLib.Alvo`, put this test in `test/MMLib.Alvo.Data.PostgreSql.Tests` instead, which builds the driver's services through `AddAlvo()`), and a `reservations` child with `product_id`, `quantity`, `status`, `label`.

```csharp
// test/MMLib.Alvo.Tests/Descriptor/RollupFilterCheckTests.cs
using MMLib.Alvo.Descriptor.Internal;

namespace MMLib.Alvo.Tests.Descriptor;

public sealed class RollupFilterCheckTests
{
    [Fact]
    public void A_filter_over_the_child_s_fields_is_valid() =>
        RollupDescriptors.Errors(where: "status == 'held'").ShouldBeEmpty();

    [Theory]
    [InlineData("status == @user.id", "@user")]
    [InlineData("created_at > now() - duration('24h')", "now()")]
    [InlineData("missing == 'x'", "missing")]
    [InlineData("quantity", "boolean")]
    public void A_filter_with_a_caller_a_clock_or_an_unknown_field_is_refused_at_its_pointer(string where, string named)
    {
        var error = RollupDescriptors.Errors(where).ShouldHaveSingleItem();
        error.Path.ShouldBe("/entities/products/fields/held_total/rollup/where");
        error.Message.ShouldContain(named);
    }

    [Fact]
    public void A_filter_is_no_longer_an_unhonoured_slot() =>
        UnhonouredFeatures.EveryRefusal.ShouldNotContain(refusal => refusal.Slot == "rollup.where");
}
```

(`RollupDescriptors.Errors(where)` validates a descriptor with `products.held_total = { "from": "reservations", "op": "sum", "field": "quantity", "where": <where> }` through `DescriptorValidator` and returns its errors — a helper in the test file.)

In `AlvoDataComputedRollupTests` (both engines), descriptor/schema gain `reservations` (`product_id` ref, `quantity` decimal, `status` enum `held|released`, `label` string) and `products.held_total` (`sum` of `quantity` where `status == 'held'`):

```csharp
    [Fact]
    public async Task A_filtered_rollup_counts_only_the_children_its_filter_admits()
    {
        var data = await CreateAsync(FilteredSchema, FilteredDescriptor);
        var product = await CreateProductAsync(data);
        await CreateReservationAsync(data, product, 2m, "held");
        await CreateReservationAsync(data, product, 5m, "released");

        (await ReadAsync(data, product))["held_total"].ShouldBe(2m);
    }

    [Fact]
    public async Task A_child_moving_across_the_filter_moves_the_aggregate_both_ways()
    {
        var data = await CreateAsync(FilteredSchema, FilteredDescriptor);
        var product = await CreateProductAsync(data);
        var reservation = await CreateReservationAsync(data, product, 2m, "held");

        await UpdateStatusAsync(data, reservation, "released");
        (await ReadAsync(data, product))["held_total"].ShouldBeNull("SUM over no admitted child is the operation's empty answer");
        await UpdateStatusAsync(data, reservation, "held");
        (await ReadAsync(data, product))["held_total"].ShouldBe(2m);
    }

    [Fact]
    public async Task A_filter_whose_constant_holds_braces_aggregates_exactly_the_matching_children()
    {
        var data = await CreateAsync(BracedSchema, BracedDescriptor);
        var product = await CreateProductAsync(data);
        await CreateReservationAsync(data, product, 2m, "held", label: "{0}");
        await CreateReservationAsync(data, product, 3m, "held", label: "x");

        (await ReadAsync(data, product))["held_total"].ShouldBe(2m);
    }

    [Fact]
    public async Task Changing_a_filter_on_an_apply_recomputes_every_parent_at_once()
    {
        var data = await CreateAsync(FilteredSchema, FilteredDescriptor);
        var product = await CreateProductAsync(data);
        await CreateReservationAsync(data, product, 2m, "held");
        await CreateReservationAsync(data, product, 5m, "released");

        await ApplyAsync(FilteredSchema, ReleasedFilterSchema);

        (await ReadAsync(data, product))["held_total"].ShouldBe(5m, "the backfill ran in the apply, before any child write");
    }

    [Fact]
    public async Task A_rollup_added_to_a_populated_parent_is_filled_by_the_apply()
    {
        var data = await CreateAsync(WithoutRollupSchema, FilteredDescriptor);
        var product = await CreateProductAsync(data);
        await CreateReservationAsync(data, product, 2m, "held");

        await ApplyAsync(WithoutRollupSchema, FilteredSchema);

        (await ReadAsync(data, product))["held_total"].ShouldBe(2m);
    }
```

Write the schema/descriptor builders and helpers out in full, following the suite's existing `Every_rollup_operation_is_maintained_over_the_same_children` builders; `ApplyAsync` is the same seam Task 8 adds to the check suite (add it here if this suite lacks one). Tenancy: add one two-tenant fact mirroring `A_tenants_rollup_never_aggregates_another_tenants_children` with a filter, so the backfill's tenant predicate is pinned (remove the predicate and it must go red).

**Acceptance (3), PostgreSQL:** in `PostgreSqlRollupRaceTests`, a second fact `Concurrent_child_writes_on_both_sides_of_the_filter_all_land_correctly`: the descriptor's `orders.total` gains `where: "kind == 'sale'"` (and the child a `kind` string field), `Writers` lines alternate `sale`/`refund`, and the parent's `total` must equal the number of `sale` lines exactly, with every write completed successfully. Same `SleepingFieldSqlRenderer` window.

- [ ] **Step 2: Run them to verify they fail**

Run: `dotnet test --project test/MMLib.Alvo.Tests --filter-class '*RollupFilterCheckTests'`; `dotnet test --project test/MMLib.Alvo.Data.EntityFrameworkCore.Tests --filter-class '*RollupRecomputeFilterTests'`
Expected: FAIL (`rollup.where` refused; no `Where`).

- [ ] **Step 3: Implement**

- `RollupSchema.Where`: `public string? Where { get; init; }` — "A CEL predicate over the child's own fields; only the children it admits are aggregated. Null aggregates every child."
- `RollupResolver.Resolve`: delete `EnsureNoFilter` and its call; `Where = rollup.Where`. Delete `UnhonouredFeatures.RollupWhere`; accept the moved `UnhonouredFeaturesTests` verified file (it loses `rollup.where`); judge it.
- `RollupFilterCheck.Errors`: for each parent field whose `Rollup.Where` is set, compile it in `CelProfile.Rule` against the child `EntitySchema` (`schema.Entities` by `Rollup.From`), add `StoredPredicateSurface.Refusals(expression, "A rollup filter")`, and try `RenderConstraint` with `NeutralFields` — every failure a finding at the pointer, message `"Field '<parent>.<field>' declares a 'rollup.where' that " + …`. Registered beside `CheckConstraintCheck` in `DescriptorValidator.CompiledErrors`.
- `RawSqlFormat.Escape`: `sql.Replace("{", "{{", Ordinal).Replace("}", "}}", Ordinal)`.
- `RollupRecompute(IAlvoSqlDialect dialect, IFieldSqlRenderer fields, ICelCompiler compiler, IPredicateRenderer predicates)`: `Setter` appends `$" AND ({RawSqlFormat.Escape(Filter(child, rollup))})"` when `rollup.Where` is set; `Filter` compiles (cached per `(child.Name, rollup.Where)` in a `ConcurrentDictionary`, since the schema is immutable per revision) and calls `predicates.RenderConstraint`. Update the class remarks: the filter, its unqualified columns (Ruling 31), the escape. `AlvoEfCoreProvider` passes the two services it already resolves for `ComputedColumnSql`.
- `RollupBackfill.Statements(current, desired)`: for every desired parent field with a `Rollup` whose table exists in `current` and whose `RollupSchema` (record equality covers `From`, `Op`, `Field`, `Via`, `Where`) differs from — or is absent in — the current field: one `UPDATE <parent> SET <f> = (SELECT <aggregate> FROM <child> WHERE <child>.<via> = <parent>.<id>[ AND <child>.<tenant_id> = <parent>.<tenant_id>][ AND (<filter>)])`, with the aggregate rendered by the same `Aggregate` method `RollupRecompute` uses (extract it to an `internal static` both call — one aggregate rule). These statements carry no arguments, so no brace escaping applies — they go into `MigrationPlan.Sql` verbatim. `EfCoreSchemaMigrator.PlanAsync` appends them after EF's generated SQL and adds one `MigrationStep` per statement: `SchemaChange { Kind = AlterField, Entity = parent, Field = field, Detail = "Recomputes the rollup for every record." }`, non-destructive.

- [ ] **Step 4: Run them on both engines, plus the race**

Run: `dotnet test --project test/MMLib.Alvo.Tests --filter-class '*RollupFilterCheckTests' --filter-class '*UnhonouredFeaturesTests' --filter-class '*RollupResolver*'`; `dotnet test --project test/MMLib.Alvo.Data.EntityFrameworkCore.Tests --filter-class '*RollupRecomputeFilterTests' --filter-class '*RollupLockStatementTests'`; `dotnet test --project test/MMLib.Alvo.Data.Sqlite.Tests --filter-class '*SqliteComputedRollupTests'`; `dotnet test --project test/MMLib.Alvo.Data.PostgreSql.Tests.Integration --filter-class '*PostgreSqlComputedRollupTests' --filter-class '*PostgreSqlRollupRaceTests'`
Expected: PASS.

- [ ] **Step 5: Baseline, format, ring0, ring1, commit**

Accept the Abstractions public-API diff (`RollupSchema.Where`) and judge it (Ruling 18).

```bash
dotnet format whitespace MMLib.Alvo.slnx --include <every .cs file touched>
scripts/test-ring0 && scripts/test-ring1
git add <the files, by path> test/MMLib.Alvo.Abstractions.Tests/PublicApi.MMLib.Alvo.Abstractions.verified.txt test/MMLib.Alvo.Tests/UnhonouredFeaturesTests.*.verified.txt
git commit -m "feat(data): filter rollups with rollup.where and recompute changed rollups on apply" -m "Claude-Session: https://claude.ai/code/session_01NJAtafM29iddLwfhb5gnmB"
```

---

## Task 10: Integrity over HTTP, both engines — the oversell, the apply refusal, one instant per request **[slice end]**

**Files:**
- Create: `test/_shared/api/DataApiEngineTests.Integrity.cs`, `test/_shared/api/descriptors/integrity-inventory.alvo.json`
- Modify: nothing in `src/` unless a fact finds a defect (fix it here, with the fact).

**Interfaces:**
- Consumes: everything above; `AlvoApiWorld.FromDescriptorAsync(…, new AlvoApiWorldSetup(MapAlvoProblemDetails: true, MapManagementApi: true, ConfigureServices: …), Engine)`; `world.SendAsync`, `ReadViolationsAsync`, `ReadProblemTypeAsync` (existing helpers).

- [ ] **Step 1: The descriptor**

```json
{
  "$schema": "https://alvo.dev/schema/v1/project.json",
  "apiVersion": "alvo.dev/v1",
  "name": "integrity-inventory",
  "description": "HX-11 over HTTP: a product's stock and its held reservations, kept honest by validations the database enforces and a filtered rollup.",
  "auth": { "providers": ["local"], "roles": ["clerk"] },
  "access": { "admin": "'clerk' in @user.roles" },
  "entities": {
    "products": {
      "audit": true,
      "fields": {
        "name": { "type": "string", "maxLength": 80, "required": true },
        "stock": { "type": "integer", "required": true, "validation": "value >= 0" },
        "reserved": {
          "type": "integer",
          "rollup": { "from": "reservations", "op": "sum", "field": "quantity", "where": "status == 'held'" },
          "validation": "value <= stock"
        },
        "cost": { "type": "decimal", "precision": 10, "scale": 2, "hidden": true, "validation": "value >= 0" },
        "sale_starts_at": { "type": "datetime" },
        "sale_ends_at": { "type": "datetime", "validation": "!has(sale_starts_at) || value - sale_starts_at <= duration('720h')" }
      },
      "rules": { "list": "true", "get": "true", "create": "true", "update": "true", "delete": "true" }
    },
    "reservations": {
      "fields": {
        "product_id": { "type": "ref", "entity": "products", "required": true },
        "quantity": { "type": "integer", "required": true, "validation": "value > 0" },
        "status": { "type": "enum", "values": ["held", "released"], "required": true }
      },
      "rules": { "list": "true", "get": "true", "create": "true", "update": "true", "delete": "true" }
    }
  }
}
```

(Check `access` and `hidden` spellings against the schema; `hidden: true` must be the static flag form. If `rules` `true` on a `hidden` field interacts with `get`, keep the row readable — the fact only needs the refusal shape.)

- [ ] **Step 2: Write the facts**

```csharp
// test/_shared/api/DataApiEngineTests.Integrity.cs
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;
using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace MMLib.Alvo.Api.Tests;

/// <summary>HX-11 over HTTP on every engine: validations are CHECK constraints, a rollup is filtered, now() is one instant.</summary>
public abstract partial class DataApiEngineTests
{
    private static readonly TestApiKey _clerk = new("clerk", ["clerk"], ["*:read", "*:write"]);
    private static readonly DateTimeOffset _opening = new(2026, 11, 9, 8, 0, 0, TimeSpan.Zero);

    /// <summary>Acceptance (1): ten concurrent reservations of one item against a stock of three.</summary>
    [Fact]
    public async Task A_concurrent_oversell_is_refused_422_with_a_pointer_on_every_engine()
    {
        await using var world = await StartIntegrityAsync();
        var product = await CreateIntegrityAsync(world, "products", new JsonObject { ["name"] = "Lamp", ["stock"] = 3 });

        var responses = await Task.WhenAll(Enumerable.Range(0, 10).Select(_ => world.SendAsync(
            HttpMethod.Post, "/api/reservations", _clerk,
            body: new JsonObject { ["product_id"] = product, ["quantity"] = 1, ["status"] = "held" })));

        responses.Count(response => response.StatusCode == HttpStatusCode.Created).ShouldBe(3);
        foreach (var refused in responses.Where(response => response.StatusCode != HttpStatusCode.Created))
        {
            refused.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity, await refused.ReadTextAsync());
            (await refused.ReadProblemTypeAsync()).ShouldBe(AlvoProblemTypes.Validation);
            (await refused.ReadViolationsAsync()).ShouldBe([("/quantity", "check")]);
        }

        (await ReadIntegrityAsync(world, "products", product))["reserved"]!.GetValue<long>().ShouldBe(3);
        (await world.CountRowsAsync("reservations")).ShouldBe(3);
        foreach (var response in responses)
        {
            response.Dispose();
        }
    }

    [Fact]
    public async Task A_patch_breaking_a_validation_is_422_at_its_field_and_leaves_the_row()
    {
        await using var world = await StartIntegrityAsync();
        var product = await CreateIntegrityAsync(world, "products", new JsonObject { ["name"] = "Lamp", ["stock"] = 3 });

        using var refused = await world.SendAsync(HttpMethod.Patch, $"/api/products/{product}", _clerk, body: new JsonObject { ["stock"] = -1 });

        refused.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity, await refused.ReadTextAsync());
        (await refused.ReadViolationsAsync()).ShouldBe([("/stock", "check")]);
        (await refused.ReadTextAsync()).ShouldNotContain("-1");
        (await ReadIntegrityAsync(world, "products", product))["stock"]!.GetValue<long>().ShouldBe(3);
    }

    [Fact]
    public async Task A_patch_of_a_field_another_validation_reads_is_refused_at_the_validated_field()
    {
        await using var world = await StartIntegrityAsync();
        var product = await CreateIntegrityAsync(world, "products", new JsonObject
        {
            ["name"] = "Lamp", ["stock"] = 3, ["sale_starts_at"] = "2026-11-01T00:00:00Z", ["sale_ends_at"] = "2026-11-20T00:00:00Z",
        });

        using var refused = await world.SendAsync(HttpMethod.Patch, $"/api/products/{product}", _clerk,
            body: new JsonObject { ["sale_starts_at"] = "2026-09-01T00:00:00Z" });

        refused.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity, await refused.ReadTextAsync());
        (await refused.ReadViolationsAsync()).ShouldBe([("/sale_ends_at", "check")]);
    }

    [Fact]
    public async Task A_hidden_field_s_validation_refuses_without_naming_it()
    {
        await using var world = await StartIntegrityAsync();
        using var refused = await world.SendAsync(HttpMethod.Post, "/api/products", _clerk,
            body: new JsonObject { ["name"] = "Lamp", ["stock"] = 1, ["cost"] = -1 });

        refused.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity, await refused.ReadTextAsync());
        (await refused.ReadViolationsAsync()).ShouldBe([("", "check")]);
        (await refused.ReadTextAsync()).ShouldNotContain("cost");
    }

    [Fact]
    public async Task A_released_reservation_leaves_the_filtered_rollup()
    {
        await using var world = await StartIntegrityAsync();
        var product = await CreateIntegrityAsync(world, "products", new JsonObject { ["name"] = "Lamp", ["stock"] = 3 });
        var reservation = await CreateIntegrityAsync(world, "reservations", new JsonObject { ["product_id"] = product, ["quantity"] = 2, ["status"] = "held" });

        using var released = await world.SendAsync(HttpMethod.Patch, $"/api/reservations/{reservation}", _clerk, body: new JsonObject { ["status"] = "released" });
        released.StatusCode.ShouldBe(HttpStatusCode.OK, await released.ReadTextAsync());

        (await ReadIntegrityAsync(world, "products", product))["reserved"].ShouldBeNull();
    }

    /// <summary>Acceptance (2) over the Management API.</summary>
    [Fact]
    public async Task Adding_a_validation_over_rows_that_break_it_is_refused_with_their_count_on_every_engine()
    {
        await using var world = await StartIntegrityAsync();
        await CreateIntegrityAsync(world, "products", new JsonObject { ["name"] = "Long", ["stock"] = 3, ["sale_starts_at"] = "2026-11-01T00:00:00Z" });
        await CreateIntegrityAsync(world, "products", new JsonObject { ["name"] = "Short", ["stock"] = 3 });

        using var refused = await ApplyIntegrityAsync(world, fields => fields["name"]!["validation"] = "value != 'Long'", dryRun: true);

        refused.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity, await refused.ReadTextAsync());
        var text = await refused.ReadTextAsync();
        text.ShouldContain("/entities/products/fields/name/validation");
        text.ShouldContain("1 row(s) of 'products' break this validation");
    }

    /// <summary>Acceptance (7) over the Management API.</summary>
    [Fact]
    public async Task A_validation_on_a_dynamic_entity_is_refused_naming_41()
    {
        await using var world = await StartIntegrityAsync();
        using var refused = await ApplyIntegrityAsync(world, edit: null, dryRun: true, addDynamic: true);

        refused.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity, await refused.ReadTextAsync());
        (await refused.ReadTextAsync()).ShouldContain("#41");
    }

    /// <summary>Acceptance (4) over HTTP: the request filter binds the instant; the clock moving mid-request moves nothing.</summary>
    [Fact]
    public async Task One_request_is_one_instant_on_every_engine()
    {
        var clock = new FakeTimeProvider(_opening);
        await using var world = await StartIntegrityAsync(clock, configureAlvo: alvo => alvo.Entity("products", products => products
            .AddValidateHook("tick", (ValidateWrite<AlvoRecord> _, CancellationToken _) => { clock.Advance(TimeSpan.FromSeconds(30)); return ValueTask.CompletedTask; })));

        var created = await CreateIntegrityRowAsync(world, "products", new JsonObject { ["name"] = "Lamp", ["stock"] = 1 });

        created["created_at"]!.GetValue<DateTimeOffset>().ShouldBe(_opening);
        created["updated_at"]!.GetValue<DateTimeOffset>().ShouldBe(_opening);
        clock.GetUtcNow().ShouldBe(_opening.AddSeconds(30));
    }
}
```

Helpers (in the same file, private): `StartIntegrityAsync(FakeTimeProvider? clock = null, Action<IAlvoBuilder>? configureAlvo = null)` → `AlvoApiWorld.FromDescriptorAsync("integrity-inventory.alvo.json", [_clerk], new AlvoApiWorldSetup(MapAlvoProblemDetails: true, MapManagementApi: true, ConfigureServicesAfterAlvo: clock is null ? null : services => services.AddSingleton<TimeProvider>(clock), ConfigureAlvo: configureAlvo), Engine)`; `CreateIntegrityAsync`/`CreateIntegrityRowAsync`/`ReadIntegrityAsync` as `CreateAsync`/`ReadOwnerAsync` in `DataApiEngineTests.cs`; `ApplyIntegrityAsync(world, edit, dryRun, addDynamic = false)` as `ApplyRuntimeAsync` in `DataApiEngineTests.RuntimeApply.cs` (GET the descriptor, edit `entities.products.fields`, and with `addDynamic` add `"ledgers": { "storage": "dynamic", "fields": { "amount": { "type": "integer", "validation": "value > 0" } }, "rules": … }`), sending `?dryRun=true` when asked and returning the response. The descriptor's `access.admin` lets `_clerk` call the Management API; if the world's management route needs a different key shape, copy the one `RuntimeApply` uses.

- [ ] **Step 3: Run on both engines**

Run: `dotnet test --project test/MMLib.Alvo.Api.Tests --filter-class '*SqliteDataApiTests'` (scoped with `--filter-method '*oversell*'` etc. while iterating); `dotnet test --project test/MMLib.Alvo.Api.Tests.Integration --filter-class '*DataApiOnPostgresTests'`
Expected: PASS on both. A 500 on the oversell means a `Check` reached `ProblemResultFactory` unclassified or the recompute's failure escaped the translator — fix in `EfAlvoData` (the translator must wrap the recompute) and re-run.

- [ ] **Step 4: Format, ring0, ring1, commit**

```bash
dotnet format whitespace MMLib.Alvo.slnx --include test/_shared/api/DataApiEngineTests.Integrity.cs
scripts/test-ring0 && scripts/test-ring1
git add test/_shared/api/DataApiEngineTests.Integrity.cs test/_shared/api/descriptors/integrity-inventory.alvo.json <any src fix, by path>
git commit -m "test(api): prove CHECK, rollup.where and one now() per request over HTTP on both engines" -m "Claude-Session: https://claude.ai/code/session_01NJAtafM29iddLwfhb5gnmB"
```

---

## Task 11: Dashboard — *Validation* and rollup *Where* are live

**Files:**
- Modify: `src/MMLib.Alvo.Admin/Components/Schema/FieldEditor.razor`, `FieldEditor.razor.cs`, `FieldFacets.cs` (+ `FieldFacets.Maintained.cs`), `ExpressionSlots.cs`, `FieldBadges.cs`, `RefusalPlaces.cs`, `FieldReferences.cs` (only if a pointer list needs `validation`'s form)
- Create: `test/MMLib.Alvo.Admin.Tests.EndToEnd/IntegrityEditorScenarios.cs`
- Test: `test/MMLib.Alvo.Admin.Tests/Schema/FieldFacetsTests.cs` (+ `ExpressionSlotsTests.cs`, `FieldBadgesTests.cs`, `RefusalPlacesTests.cs` where they pin the removed slots)

**Interfaces:**
- Produces: `FieldFacets.Validation` (string), `FieldFacets.RollupWhere` (string), both written into the staged field JSON (`validation`, `rollup.where`) and read back from a declaration; `ExpressionSlots.ForValidation(json, entity, editing, name, facets, source)` → `(Json, "/entities/<e>/fields/<f>/validation")`, `ExpressionSlots.ForRollupWhere(…)` → `(Json, "/entities/<e>/fields/<f>/rollup/where")`; editor boxes with `id`/`data-testid` `field-validation` and `rollup-where`.
- Removes: `FieldFacets.RemoveRollupFilter`, `DeclaresRollupFilter`'s refusal use, the `rollup-where-refused` `Refusal`, the `rollup-remove-where` checkbox, `RollupWhereSlot`, the `field.validation`/`rollup.where` badges and `RefusalPlaces` entries.

- [ ] **Step 1: Write the failing unit tests**

```csharp
// in test/MMLib.Alvo.Admin.Tests/Schema/FieldFacetsTests.cs
    [Fact]
    public void A_validation_round_trips_through_the_form()
    {
        var facets = FieldFacets.From("stock", Json("""{"type": "integer", "validation": "value >= 0"}"""));
        facets.Validation.ShouldBe("value >= 0");
        facets.Build(editing: "stock", editingJson: null, siblings: [], out _)!["validation"]!.GetValue<string>().ShouldBe("value >= 0");
    }

    [Fact]
    public void A_rollup_filter_round_trips_and_an_empty_box_removes_it()
    {
        var facets = FieldFacets.From("held", Json("""{"type": "integer", "rollup": {"from": "reservations", "op": "count", "where": "status == 'held'"}}"""));
        facets.RollupWhere.ShouldBe("status == 'held'");
        facets.RollupWhere = "";
        facets.Build("held", null, [], out _)!["rollup"]!.AsObject().ContainsKey("where").ShouldBeFalse();
    }
```

(Use `FieldFacets`' real factory and `Build` signature — read the file; the tests above state the behaviour.) `ExpressionSlotsTests`: `ForValidation` and `ForRollupWhere` return the pointers above over a working copy whose field carries the typed source.

- [ ] **Step 2: Run them to verify they fail**

Run: `dotnet test --project test/MMLib.Alvo.Admin.Tests --filter-class '*FieldFacetsTests' --filter-class '*ExpressionSlotsTests'`
Expected: FAIL.

- [ ] **Step 3: Implement**

In `FieldEditor.razor`, replace the `rollup-where-refused` `Refusal` and the remove checkbox with:

```razor
                <Field Label="Only children where" For="rollup-where">
                    <ChildContent>
                        <MudTextField T="string" Variant="Variant.Outlined" Immediate="true" Lines="1" id="rollup-where"
                                      data-testid="rollup-where" Class="a-mono" spellcheck="false"
                                      Placeholder="e.g. status == 'held' — leave empty to aggregate every child"
                                      aria-describedby="@_check.DescribedBy(WhereBox)"
                                      Value="_facets.RollupWhere" ValueChanged="TypeWhere" />
                        <ExpressionFindings Check="_check" Key="@WhereBox" />
                    </ChildContent>
                </Field>
```

and, for every kind but `Computed` (after the kind-specific sections, before the refused-facets disclosure):

```razor
        <Field Label="Validation" For="field-validation">
            <ChildContent>
                <MudTextField T="string" Variant="Variant.Outlined" Immediate="true" Lines="1" id="field-validation"
                              data-testid="field-validation" Class="a-mono" spellcheck="false"
                              Placeholder="e.g. value >= 0 — the database refuses any row that breaks it"
                              aria-describedby="@_check.DescribedBy(ValidationBox)"
                              Value="_facets.Validation" ValueChanged="TypeValidation" />
                <ExpressionFindings Check="_check" Key="@ValidationBox" />
            </ChildContent>
        </Field>
```

`ExpressionFindings` is the findings loop the computed box renders today (lines ~262–270), extracted into a component so the three boxes share it; if extracting is more than a move, repeat the loop inline with the box's key. `TypeValidation`/`TypeWhere` mirror `TypeComputed` (`_ = CheckAsync(ValidationBox, …)`), and `AskAsync` gains the slot per key: `ExpressionSlots.ForValidation`/`ForRollupWhere`. Constants `ValidationBox = "field-validation"`, `WhereBox = "rollup-where"`. Remove the dead slot code and its doc comments (`WhereRefusal`, `RollupWhereSlot`, the `Refused` fold's exclusion, `FieldFacets.Maintained`'s `RemoveRollupFilter` and the refusal sentence at line ~295); `FieldBadges` drops `field.validation` and `rollup.where` from its refused-badge table (a declared validation is now an ordinary facet — show it as `validation` in the facet notes the way `computed` is shown, if the badge component lists honoured CEL facets; `FieldReferences._celFacets` already names `validation`).

- [ ] **Step 4: The e2e scenario**

```csharp
// test/MMLib.Alvo.Admin.Tests.EndToEnd/IntegrityEditorScenarios.cs
namespace MMLib.Alvo.Admin.Tests.EndToEnd;

/// <summary>HX-11's dashboard slice: a field's Validation and a rollup's Where are typed, checked live, applied, and enforced.</summary>
[Collection(AdminCollection.Name)]
public sealed class IntegrityEditorScenarios(AdminWorld world)
{
    [Fact]
    public async Task IntegrityEditor_a_validation_is_checked_live_applied_and_enforced()
    {
        await using var session = await world.SignInAsync("admin");
        await session.OpenFieldAsync("parts", "unit_price");

        await session.Page.GetByTestId("field-validation").FillAsync("value >= @user.id");
        await Expect(session.Page.GetByTestId("check-field-validation")).ToContainTextAsync("@user");
        await session.Page.GetByTestId("field-validation").FillAsync("value >= 0");
        await Expect(session.Page.GetByTestId("check-field-validation")).ToHaveCountAsync(0);
        await session.SaveFieldAndApplyAsync();

        var refused = await session.CreateRecordAsync("parts", new() { ["name"] = "Bolt", ["unit_price"] = "-1" });
        refused.ShouldContain("check");
    }

    [Fact]
    public async Task IntegrityEditor_a_rollup_where_is_checked_live_and_saved()
    {
        await using var session = await world.SignInAsync("admin");
        await session.OpenFieldAsync("service_orders", "parts_count");

        await session.Page.GetByTestId("rollup-where").FillAsync("missing == 'x'");
        await Expect(session.Page.GetByTestId("check-rollup-where")).ToContainTextAsync("missing");
        await session.Page.GetByTestId("rollup-where").FillAsync("quantity > 1");
        await Expect(session.Page.GetByTestId("check-rollup-where")).ToHaveCountAsync(0);
        await session.SaveFieldAndApplyAsync();
        await Expect(session.Page.GetByTestId("rollup-where")).ToHaveValueAsync("quantity > 1");
    }
}
```

The world's seeded descriptor, session helpers and the entity/field names come from the existing scenarios — read `ComputedTextScenarios.cs` and `ExpressionCheckScenarios.cs` first and use their sign-in, open-field, save-and-apply and record-create helpers and an entity/rollup the seed already has (rename `parts`/`service_orders` accordingly; add a rollup to the seed only if none exists). Both scenario names start `IntegrityEditor` so the filter below runs exactly them.

- [ ] **Step 5: Run the unit tests and the filtered e2e**

Run: `dotnet test --project test/MMLib.Alvo.Admin.Tests`; `scripts/test-admin-e2e --filter IntegrityEditor`; `scripts/test-prototype` is not needed (the prototype is not touched) but run `python3 scripts/gen-prototype-fixtures --check` — if the prototype's generated facets copy `UnhonouredFeatures`' refusals and report stale, regenerate and commit them in this task.
Expected: PASS.

- [ ] **Step 6: Format, ring0, commit**

```bash
dotnet format whitespace MMLib.Alvo.slnx --include <every .cs file touched>
scripts/test-ring0
git add <the files, by path>
git commit -m "feat(admin): edit a field's validation and a rollup's where with a live check" -m "Claude-Session: https://claude.ai/code/session_01NJAtafM29iddLwfhb5gnmB"
```

---

## Task 12: Skills, the assistant's eval case, and the capability report

**Files:**
- Modify: `plugins/alvo/skills/alvo-descriptor-rules-and-cel/SKILL.md`, `alvo-descriptor-computed-and-rollups/SKILL.md`, `alvo-descriptor-entities-and-fields/SKILL.md`, `alvo-descriptor-capabilities-and-limits/SKILL.md` (generated `refused` region)
- Modify: `test/MMLib.Alvo.Host.Tests/SkillCoreClaimsTests.cs` (only if a region's allowed example needs a declaring field), `test/_shared/ai/SkillCaseAnswers.cs` and the `reject_negative_price` grader (find it with `grep -rn "reject_negative_price" test eval`), `test/MMLib.Alvo.Ai.Eval.Tests/SkillCasesTests.cs`

**Interfaces:**
- Consumes: the regions' generators/tests — `SkillRegionTests` (`test/MMLib.Alvo.Ai.Tests`) and `SkillCoreClaimsTests` (`test/MMLib.Alvo.Host.Tests`) compile every stated example in its profile; the example blocks are dry-run against a real host by the skill example tests.

- [ ] **Step 1: Write the failing claims first**

In `alvo-descriptor-rules-and-cel/SKILL.md`, change the region to:

```markdown
<!-- gen:cel-rule -->
- allowed: `'admin' in @user.roles` `user_id == @user.id` `active == true` `has(phone)` `hourly_rate > 40.0` `created_at > now() - duration('24h')`
- refused: `hourly_rate * 2.0` `changed(active)` `old.active == true` `now() == 1` `'user_id == @user.id'` `'true'`
<!-- /gen:cel-rule -->
```

(`now() == 1` is refused by type, in every profile — if `SkillCoreClaimsTests` requires a refused example to be refused *only* in this profile, pick `trim(name) == 'x'` instead, which a rule refuses and a condition admits.)

Replace the paragraph "No function call works in a rule in this build…" with: "A rule calls no function — not `trim`, not a host function: a rule filters in SQL. Two forms are not calls: `now()`, the instant of the request, and `duration('…')`, a constant such as `duration('90m')` (units `h m s ms us ns`). `created_at > now() - duration('24h')` admits the last day's rows. To filter by a normalised value, write it into a field with a before-hook `mutate` and compare that field."

In `alvo-descriptor-computed-and-rollups/SKILL.md`, replace "A `where` is refused." with: "`where` filters the children with a CEL condition over the child's own fields — `\"where\": \"status == 'held'\"` — kept consistent in the same transaction; it reads no caller (`@user`, `@tenant`) and no `now()`, because the stored number would go stale with no write to move it." Keep `gen:cel-computed` as it is (`now()` stays refused there).

In `alvo-descriptor-entities-and-fields/SKILL.md`: the paragraph at line ~30 becomes "A field's `validation` is a CEL condition over its own row that the database enforces as a `CHECK` constraint: `value` is the field, other fields by name (`\"validation\": \"value <= stock\"`). An empty field passes its own validation — use `required` for presence. It reads no caller and no `now()`; adding one is refused at apply if existing rows break it, with their count." Replace the `validation-refused` example with:

```markdown
<!-- example: validation-accepted -->
**A technician's hourly rate is never negative.**

```json
{"tool": "check_change", "baseRevision": 1,
 "operations": [{"op": "add", "path": "/entities/technicians/fields/hourly_rate/validation", "value": "value >= 0"}]}
```

```json
{"valid": true, "changedPaths": ["/entities/technicians/fields/hourly_rate/validation"]}
```
```

and drop "`validation` is refused the same way whatever its expression:" from the preceding sentence.

In `alvo-descriptor-capabilities-and-limits/SKILL.md`, regenerate the `refused` region (it is generated from `UnhonouredFeatures.EveryRefusal` — run the generator or the region test's own fix instruction): `field.validation` and `rollup.where` disappear.

The eval: `reject_negative_price` now passes with either a before-hook (as today) **or** a `validation` on `parts.unit_price` whose expression refuses a negative and admits zero (grade it by compiling the proposed descriptor and evaluating the canonical `CheckExpression` against `unit_price = -1` and `0` through `CelInterpreter`); add `SkillCasesTests.A_validation_on_the_price_passes_reject_negative_price` and keep the existing hook-based tests green. Its skill-load expectation (`SkillCasesTests` line ~37, `Hooks`) widens to "hooks or entities-and-fields".

- [ ] **Step 2: Run the skill and eval suites**

Run: `dotnet test --project test/MMLib.Alvo.Ai.Tests --filter-class '*SkillRegionTests' --filter-class '*SkillExample*'`; `dotnet test --project test/MMLib.Alvo.Host.Tests --filter-class '*SkillCoreClaimsTests'`; `dotnet test --project test/MMLib.Alvo.Ai.Eval.Tests`
Expected: first run FAILs exactly where the text and the build disagree (e.g. the capability region still lists the two slots until regenerated); fix the text — never the test — until PASS. The skills' size caps are drift-tested: if one grows past its cap, shorten the prose, not the regions.

- [ ] **Step 3: Commit**

```bash
scripts/test-ring0
git add plugins/alvo/skills/alvo-descriptor-rules-and-cel/SKILL.md plugins/alvo/skills/alvo-descriptor-computed-and-rollups/SKILL.md plugins/alvo/skills/alvo-descriptor-entities-and-fields/SKILL.md plugins/alvo/skills/alvo-descriptor-capabilities-and-limits/SKILL.md <eval files, by path>
git commit -m "docs(skills): teach now(), durations, field validation and rollup where" -m "Claude-Session: https://claude.ai/code/session_01NJAtafM29iddLwfhb5gnmB"
```

---

## Task 13: Docs — *Integrity in the descriptor*, the architecture docs, CHANGELOG, the spec amendment **[slice end]**

**Files:**
- Create: `website/src/content/docs/extending/integrity.mdx`, `website/src/snippets/extending/integrity/inventory.alvo.json`, `website/src/snippets/extending/integrity/InventoryIntegrityTests.cs`
- Modify: `docs/architecture/cel.md` (durations, `now()`, the stored-predicate surface), `docs/architecture/data-path.md` (a section *`validation` is the engine's `CHECK`*; the rollup section gains `where` and the backfill; *Every timestamp is one instant* amended by Ruling 2), `docs/architecture/data-api.md` (the 422 `check` violation beside the 409 `unique`), `CHANGELOG.md`, `docs/superpowers/specs/2026-10-14-host-extensibility-design.md` (*HX-11 settled details*, §5 row `11`, §6.1 row amended), `website/src/content/docs/extending/index.mdx` (link)

**Interfaces:**
- Consumes: `AlvoTestHost` (SQLite), the snippet project `test/MMLib.Alvo.DocsSnippets.Tests` (it compiles every `extending/**/*.cs` and copies every `*.alvo.json`).

- [ ] **Step 1: The executed snippet**

`inventory.alvo.json` is `integrity-inventory.alvo.json` from Task 10 without `cost` (a reader's example, not a disclosure test). The test:

```csharp
// website/src/snippets/extending/integrity/InventoryIntegrityTests.cs
using Microsoft.Extensions.Time.Testing;
using MMLib.Alvo;
using MMLib.Alvo.Data;
using MMLib.Alvo.TestHost;
using Shouldly;
using Xunit;

namespace Inventory.Tests;

public sealed class InventoryIntegrityTests
{
    private static readonly DateTimeOffset _monday = new(2026, 11, 9, 8, 0, 0, TimeSpan.Zero);

    private static string Descriptor => Path.Combine(AppContext.BaseDirectory, "inventory.alvo.json");

    [Fact]
    public async Task A_reservation_past_the_stock_is_refused_at_its_quantity()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var alvo = await AlvoTestHost.StartAsync(builder => builder.FromDescriptor(Descriptor), time: new FakeTimeProvider(_monday), cancellationToken: ct);
        var clerk = AlvoTestHost.Caller("clerk");
        var lamp = await alvo.Data.CreateAsync("products", new Dictionary<string, object?> { ["name"] = "Lamp", ["stock"] = 2L }, clerk, cancellationToken: ct);

        await alvo.Data.CreateAsync("reservations", Reservation(lamp, 2), clerk, cancellationToken: ct);
        var refusal = await Should.ThrowAsync<AlvoConstraintViolationException>(
            () => alvo.Data.CreateAsync("reservations", Reservation(lamp, 1), clerk, cancellationToken: ct));

        (refusal.Kind, refusal.Fields.Single()).ShouldBe((AlvoConstraintKind.Check, "quantity"));
        (await alvo.Data.GetAsync("products", (Guid)lamp["id"]!, clerk, ct))!["reserved"].ShouldBe(2L);
    }

    [Fact]
    public async Task A_sale_longer_than_thirty_days_is_refused_at_its_end()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var alvo = await AlvoTestHost.StartAsync(builder => builder.FromDescriptor(Descriptor), time: new FakeTimeProvider(_monday), cancellationToken: ct);

        var refusal = await Should.ThrowAsync<AlvoConstraintViolationException>(() => alvo.Data.CreateAsync("products", new Dictionary<string, object?>
        {
            ["name"] = "Lamp", ["stock"] = 1L, ["sale_starts_at"] = _monday, ["sale_ends_at"] = _monday.AddDays(31),
        }, AlvoTestHost.Caller("clerk"), cancellationToken: ct));

        refusal.Fields.ShouldBe(["sale_ends_at"]);
    }

    private static Dictionary<string, object?> Reservation(AlvoRecord product, long quantity) =>
        new() { ["product_id"] = product["id"], ["quantity"] = quantity, ["status"] = "held" };
}
```

Run: `dotnet test --project test/MMLib.Alvo.DocsSnippets.Tests --filter-class '*InventoryIntegrityTests'` → PASS (it also compiles under `ALVO1001` as an error).

- [ ] **Step 2: The page**

`integrity.mdx`, `sidebar.order` after *Validate and guard writes*: title **Integrity in the descriptor**, description "State in the descriptor what the database must never hold — a field's validation as a CHECK constraint, a filtered rollup, time with now() and durations." Sections, each with the descriptor excerpt (`<SourceExcerpt>` over `inventory.alvo.json`) and the test excerpt:
1. *A validation is a constraint the database enforces* — `value`, other fields by name, the empty-field rule (Ruling 15), what it may not read (Ruling 13), the 422 shape with `code: "check"` and the pointer (a JSON block of the problem document), hidden fields (Ruling 24), the rollup pointer (Ruling 25).
2. *Adding one to rows that break it* — the dry run's refusal with the count (Ruling 22), the fix, and that the apply is atomic.
3. *A rollup that counts some children* — `where`, kept in the child's transaction, recomputed on apply (Ruling 32), no caller or clock (Ruling 3).
4. *Time: `now()` and durations* — one instant per request (Ruling 1), where it compiles (Ruling 3), the duration grammar and range (Rulings 8, 9), the precision note for SQLite (Ruling 11), typed `when` (`DateTimeOffset.UtcNow`, `TimeSpan`, Ruling 34).
5. *Validation, before-hook or write hook?* — a three-row table: a validation for a rule over one row the database must always hold; a before-hook for a value decided at write time; a write hook for a rule across rows or services (link *Validate and guard writes*).
A caution: "A validation is enforced by the database for every write — Alvo's, a hook's, and one that bypasses Alvo. It is not part of the OpenAPI document."

- [ ] **Step 3: Architecture docs, CHANGELOG, spec**

- `cel.md`: a section *Time* — the value types, the overload table, literal-only `duration()`, range errors per profile, the microsecond SQL domain and the dialect member, `now()`'s binding sites and the event-time override, the stored-predicate surface (shared by `validation` and `rollup.where`).
- `data-path.md`: *`validation` is the engine's `CHECK`* — rewrite, naming, the DDL per engine (EF's rebuild; plain `ADD CONSTRAINT` and why not `NOT VALID`, Ruling 20), the scan, decoding, the pointer rules; amend *Every timestamp is one instant* (Ruling 2); in the rollup section, the filter, the escape, the backfill and "Six consequences" → seven (a filtered aggregate's correctness argument is the same lock-then-recompute).
- `CHANGELOG.md` `[Unreleased]`: **Added** — `field.validation` enforced as a database `CHECK` (422 `validation`, code `check`, pointer; apply refused with the count over rows that break it); `rollup.where`; `now()` in rules and conditions (one instant per request); CEL durations (`duration('…')`, timestamp arithmetic) on both engines; typed `when` with `DateTimeOffset.UtcNow`/`TimeSpan`; dashboard *Validation* and *Where*. **Changed** — `AlvoConstraintKind.Check` and `CelValueType.Duration` appended (an exhaustive `switch` gains a case); `IFieldSqlRenderer.RenderEpochMicroseconds` and `IPredicateRenderer.RenderConstraint` default members (a third-party dialect returns `null`/throws until it implements them); a request's writes are stamped with the request's instant (a retried attempt keeps it). **Fixed** — a rollup added to a populated parent is now filled by the apply instead of reading empty until a child write.
- The spec: after §7 *HX-11*, a block **HX-11 settled details** — "Rulings and deviations taken while planning (1–44, verbatim from `docs/superpowers/plans/2026-11-06-hx-11-integrity-check-now.md`)", followed by the Rulings copied verbatim; amend §5 row `11` ("`AlvoConstraintKind.Check`; `CelValueType.Duration`; `FieldSchema.CheckExpression`, `RollupSchema.Where`; DIMs `IFieldSqlRenderer.RenderEpochMicroseconds`, `IPredicateRenderer.RenderConstraint` — *HX-11 settled details* (6), (11), (17), (18)"); amend §6.1's `field.validation` PostgreSQL cell to "`ADD CONSTRAINT … CHECK` in the apply's transaction, after a violation count (*HX-11 settled details* (20))".

- [ ] **Step 4: Build the site and the snippets; ring1; commit**

Run: `cd website && npm run build` (link validation included); `dotnet test --project test/MMLib.Alvo.DocsSnippets.Tests`; `dotnet test --project test/MMLib.Alvo.DocsGen.Tests`; `scripts/check-brief-freshness` (the spec is not a brief source — it must stay quiet); `scripts/test-ring1`.
Expected: PASS.

```bash
dotnet format whitespace MMLib.Alvo.slnx --include website/src/snippets/extending/integrity/InventoryIntegrityTests.cs
git add website/src/content/docs/extending/integrity.mdx website/src/content/docs/extending/index.mdx website/src/snippets/extending/integrity docs/architecture/cel.md docs/architecture/data-path.md docs/architecture/data-api.md CHANGELOG.md docs/superpowers/specs/2026-10-14-host-extensibility-design.md
git commit -m "docs: integrity in the descriptor, CEL time, CHECK in the data path, HX-11 settled details" -m "Claude-Session: https://claude.ai/code/session_01NJAtafM29iddLwfhb5gnmB"
```

---

## Task 14: CI parity, rings, the full admin e2e, the site, reviews, security core, plan-guard, follow-ups, PR report, PR

- [ ] **Step 1: Take `main`**

`git fetch origin`; if `origin/main` moved past `33201577`, `git merge origin/main` (merge-commit style), resolve only what the base change touched, re-run `scripts/test-ring0` and `python3 scripts/gen-prototype-fixtures --check`. Every diff below is against `origin/main`.

- [ ] **Step 2: CI-parity Release build (worktree: mount the main repository's `.git`)**

The worktree's `.git` is a file pointing at `/Users/martiniak/Developer/GitHub/Burgyn/MMLib.Alvo/.git/worktrees/…`; mount that repository's `.git` at the same absolute path so git (and anything reading the commit) resolves inside the container:

```bash
docker run --rm -u "$(id -u):$(id -g)" -e HOME=/tmp -e DOTNET_CLI_HOME=/tmp \
  -v "$PWD":"$PWD" -v /Users/martiniak/Developer/GitHub/Burgyn/MMLib.Alvo/.git:/Users/martiniak/Developer/GitHub/Burgyn/MMLib.Alvo/.git \
  -w "$PWD" mcr.microsoft.com/dotnet/sdk:10.0 \
  sh -c 'dotnet build MMLib.Alvo.slnx -c Release -p:HUSKY=0 2>&1 | grep -E "error [A-Z]+[0-9]+" | sort -u'
```

Expected: no output. The container runs as you — **no `git clean`** afterwards. Watch: CA1305/CA1307 (invariant culture and ordinal comparisons in `CelDuration`, `CheckConstraintNames`, the inline literal writer), CA2000 (`AlvoFlow`'s restore objects, the scan's command), CA1031 nowhere new, CA1812 on DI-only types, IDE0072 on every `switch` over `CelValueType`/`AlvoConstraintKind` that now misses a member (grep `switch` over both enums across `src/` and give each a deliberate arm). Fix in a `fix(…): …` commit and re-run.

- [ ] **Step 3: Rings, the full admin e2e, the site**

`scripts/test-ring0`, `scripts/test-ring1`, `scripts/test-ring2` (PostgreSQL legs: `DataApiOnPostgresTests`' integrity facts, `PostgreSqlCheckConstraintTests`, `PostgreSqlCheckLiteralPropertyTests`, `PostgreSqlAlvoDataDifferentialTests`, `PostgreSqlRollupRaceTests`; the API invariant suite and the Vacuum lint), then **`scripts/test-admin-e2e`** — the one full run of the dashboard suite in this PR — and `cd website && npm run build`. A ring2 paging fact failing on an Npgsql connect timeout before any Alvo code runs is the known local Kerberos flake — read the trace before calling it a regression.

- [ ] **Step 4: Mutation — configuration only, no local run (Ruling 38)**

Do **not** run Stryker. Read `stryker-config.expressions.json`, `stryker-config.api.json`, `stryker-config.json`, `stryker-config.data-ef-core.json`, `stryker-config.data-ef-rest.json`, `stryker-config.data-sqlite.json`, `stryker-config.data-postgresql.json` and confirm each new file lands in the shard Ruling 38 names (and that `EfCoreSchemaMigrator.cs` is still excluded); run `scripts/test-assert-mutation-run` (it reads configs; it runs no mutation) — green. Note in the PR that `data-ef-rest` gains `CheckConstraintSql`, `CheckViolationScan`, `RollupBackfill` and is one of the legs nearest its time ceiling — check the post-merge run.

- [ ] **Step 5: Reviews (labelled substitutes) and the security-core checklist**

Freeze the tree. `/code-review` and `/security-review` are user-only: dispatch two reviewer subagents over `git diff origin/main...HEAD`, labelled **"substitute for /code-review high"** and **"substitute for /security-review"**; check `git branch --show-current` after each returns (a reviewer can switch branches).

Point the code reviewer at: `CelDuration` (grammar edge cases: `"1.s"`, `".5s"`, `"1mo"`, `"µs"` vs `"μs"`, overflow before multiplication); `CelTemporal` (range checks in `decimal`, `date` as midnight); `CelTypeChecker`'s fold and the construct table (no profile gains anything Ruling 3/12 does not list); `AlvoFlow`'s restore on every path and `EnsureInstant`'s no-op; every binding site (`RequestInstant` in three filters, every `EfAlvoData`/`InMemoryAlvoData` entry, `TransactAsync`, the fan-out override); `WriteInstantNow`; the renderer's fold vs microsecond paths and `ILiteralSink`'s two implementations; `RenderConstraint` refusing context and `now()`; `CheckConstraintNames` (63 bytes, `Owner` by recomputation); the scan's skip rules and `ApplyAsync`'s translation; `ConstraintViolationTranslator.CheckFields` (never `null` for a `Check`, rollup pointer, `Unnameable`); `RollupRecompute`'s escape and filter cache; `RollupBackfill`'s tenant predicate; the dashboard's removed refusal code.

Run the `alvo-security-core-review` checklist and give the security reviewer Rulings 1–5, 10, 11, 13, 15, 16, 19, 22–26, 31. Questions to answer explicitly: Can any request-controlled text reach DDL or the recompute's SQL (it must not — every inlined literal is the descriptor author's, through one writer; CsCheck acceptance (5))? Can `now()` read a clock (it must not — Ruling 5)? Can a validation or a rollup filter read the caller or the tenant (it must not — the stored-predicate surface at apply, and `RenderConstraint` refusing at render)? Can a `Check` refusal name a hidden field, echo a value, or name a rollup parent's field (Rulings 24, 25)? Does the scan's count reach a Data API caller (Ruling 23 — Management API only)? Does a rule's `now()` widen what a caller can list beyond what `USING` and `WITH CHECK` agree on (the differential property)? Does the backfill write across tenants (the tenant predicate; the two-tenant fact)? Fix every finding before the PR; record any accepted risk as a ruling in the spec's *HX-11 settled details*. End the checklist with **mark this change `needs-deep-review`**.

- [ ] **Step 6: `alvo-plan-guard`**

Dispatch `alvo-plan-guard` last (read-only); act on drift it reports. The PLAN marker stays where it is.

- [ ] **Step 7: Follow-up issues**

Open three issues and link each from its ruling in the spec's *HX-11 settled details*: "Numeric arithmetic in `field.validation` (HX-11 Ruling 29)"; "A time-windowed aggregate (a rollup with `now()`), on HX-13's timers (HX-11 Ruling 3)"; "`TimeSpan` parameters and results for host CEL functions (HX-11 Ruling 35)". Comment on #41 with the refusal's wording and the PR link once it exists.

- [ ] **Step 8: PR report (in Slovak)**

Use the `alvo-pr-report` skill (it dispatches `alvo-pr-reporter` and publishes the 8-section page) and ask for the report **in Slovak**. Evidence: acceptance (1)–(7) → tests (the table below); the Abstractions public-API diff against Global Constraints' list; the both-engine runs; the CsCheck properties' iteration counts; the DDL goldens; the Release build; the full admin e2e run; the security-core checklist and both substitute reviews; the mutation configuration (no local run). Call out Rulings 1–2 (one instant per request, stamping changed), 11 (SQLite's millisecond precision), 15 (empty field passes), 20 (no `NOT VALID`), 22 (scan at plan time), 25 (rollup pointer), 26 (hook-set value is 422), 32 (backfill fixes an old staleness).

- [ ] **Step 9: Push and open the PR**

```bash
git branch --show-current   # hx/11-integrity-check-now
git push -u origin hx/11-integrity-check-now
```

`gh pr create --base main`. The body is the skill's five-line pointer plus the line `Closes #388`, ending with `https://claude.ai/code/session_01NJAtafM29iddLwfhb5gnmB`. Then `gh pr edit --add-label needs-deep-review`. After the merge, read #388 back (`gh issue view 388`) to confirm it — and only it — closed.

---

## Acceptance criteria → tasks

| HX-11 criterion | Task(s) | Test or artifact |
|---|---|---|
| (1) Concurrent oversell refused 422 with pointer, both engines | 7, 9, 10 | `DataApiEngineTests.A_concurrent_oversell_is_refused_422_with_a_pointer_on_every_engine` (`SqliteDataApiTests`, `DataApiOnPostgresTests`); `AlvoDataCheckConstraintTests` (both engines); `CheckViolationAnswerTests` |
| (2) Apply over violating rows refused with the count | 8, 10 | `AlvoDataCheckConstraintTests.Adding_a_validation_over_rows_that_break_it_is_refused_with_their_count`, `…Rows_written_after_the_count…`, `…Renaming_a_validated_field…` (both engines); `DataApiEngineTests.Adding_a_validation_over_rows_that_break_it_is_refused_with_their_count_on_every_engine` |
| (3) Filtered rollup correct under concurrent child writes | 9, 10 | `PostgreSqlRollupRaceTests.Concurrent_child_writes_on_both_sides_of_the_filter_all_land_correctly`; `AlvoDataComputedRollupTests.A_child_moving_across_the_filter_moves_the_aggregate_both_ways` (both engines); `DataApiEngineTests.A_released_reservation_leaves_the_filtered_rollup` |
| (4) One `now()` instant per request | 2, 10 | `NowInstantFacts` (both TestHost legs: one write, a rule, a unit); `DataApiEngineTests.One_request_is_one_instant_on_every_engine`; `AlvoFlowInstantTests`, `CelNowTests` |
| (5) CsCheck: a literal never escapes a `CHECK` | 5 | `SqliteCheckLiteralPropertyTests.A_literal_never_escapes_a_check`, `PostgreSqlCheckLiteralPropertyTests` (500 iterations each, engine-asked) |
| (6) Duration arithmetic agrees, interpreter vs SQL, both engines | 1, 3 | `AlvoDataDifferentialTests.Durations_and_now_agree_between_this_engine_and_the_interpreter_for_any_instants` (CsCheck, `SqliteAlvoDataDifferentialTests` + `PostgreSqlAlvoDataDifferentialTests`); the matrix's temporal rows; `SqliteEpochTests`, `PostgreSqlEpochTests` |
| (7) `CHECK` on a dynamic entity refused naming #41 | 6, 10 | `CheckConstraintCheckTests.A_validation_on_a_dynamic_entity_is_refused_naming_41`; `DataApiEngineTests.A_validation_on_a_dynamic_entity_is_refused_naming_41` |
| Scope: `UtcNow` → `now()` in `when` | 4 | `ConditionTranslatorClockTests` |
| Docs *Integrity in the descriptor* with executed snippets | 13 | `InventoryIntegrityTests` (DocsSnippets, under `ALVO1001`); website build |
| Dashboard: *Validation* and rollup *Where* live | 11, 14 | `IntegrityEditorScenarios` (filtered in 11, whole suite in 14); `FieldFacetsTests`, `ExpressionSlotsTests` |
| Skills: computed-and-rollups, rules-and-cel regions | 12 | `SkillRegionTests`, `SkillCoreClaimsTests`, the skill example tests, `SkillCasesTests` |
| Every PR: plan, CHANGELOG, architecture doc, plan-guard, PR report | 13, 14 | this file; `CHANGELOG.md`; `docs/architecture/cel.md`, `data-path.md`; Task 14 Steps 6, 8 |
| Security core (§6.2) | 14 | `alvo-security-core-review` checklist, `/security-review` substitute, `needs-deep-review` |
