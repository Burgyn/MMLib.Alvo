# HX-12 — `sequence`, exclusion and partial indexes — Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** The descriptor gains three integrity declarations the database keeps: a field's `sequence` (a gapless
per-tenant counter, optionally per another field's value and per period, optionally formatted), an index's
`where` (a partial index — a partial `unique` allows duplicates outside it), and an index's `exclude` (no two rows
with equal `fields` may hold overlapping `[from, to)` ranges — **409 with a pointer** under concurrent writers).

**Architecture:** A sequence number is allocated by one upsert on the framework table `<prefix>_sequences` inside
the create's own transaction, just before the `INSERT`, so the counter row is locked until commit and a rollback
returns the number (gapless by construction). The statement is a dialect member (`SequenceAllocationStatement`);
exclusion DDL is a dialect member (`ExclusionConstraint`) — PostgreSQL answers `EXCLUDE USING gist` (+ `btree_gist`),
SQLite answers two `AFTER` triggers that raise a tagged error, a dialect answering `null` (the T-SQL double, any
third-party driver) makes the apply refuse the keyword. Partial indexes are EF's own `HasFilter` over the HX-11
constraint renderer's inline-literal SQL. A refusal is decoded as the new `AlvoConstraintKind.Exclusion` and
answered 409 at the range's pointer.

**Tech Stack:** .NET 10 (C# 14), EF Core 10 migrations (`HasFilter`, `SqlOperation`), Npgsql + PostgreSQL 16
(Testcontainers, `btree_gist` from contrib), Microsoft.Data.Sqlite (≥ 3.42: `RETURNING`, upsert, `unixepoch(…,'subsec')`),
xUnit v3 on MTP, Shouldly, Verify, `Microsoft.Extensions.Time.Testing`, Blazor/MudBlazor + bUnit, Microsoft.Playwright,
Astro/Starlight.

**Spec:** `docs/superpowers/specs/2026-10-14-host-extensibility-design.md` — §7 *HX-12* (scope, docs, dashboard,
acceptance (1)–(6)); §1.1 goal 7; §5 row `12` (`AlvoConstraintKind.Exclusion`; `IAlvoSqlDialect.ExclusionConstraint(…)`
as a DIM returning null, precedent `GeneratedColumnDefinition`); §6.1 rows `sequence`, `indexes[].exclude`,
`indexes[].where`, *dynamic entities (#41)*, *Azure SQL*; §6.2 (security core lists HX-12; hidden-field disclosure
in `Exclusion` decoding); *HX-11 settled details* (17) `RenderConstraint`, (22) the pre-apply scan, (28) the in-memory
store has no engine, (51) the literal writer, (71) a filter over a hidden field is refused; Appendix A M18.

**Prior art read:** PostgreSQL *5.5.6 Exclusion Constraints* and *CREATE TABLE … EXCLUDE* (`USING gist`, `WITH =`,
`WITH &&`, an optional `WHERE (predicate)`; SQLSTATE `23P01 exclusion_violation`); `btree_gist` (contrib; **trusted
since PostgreSQL 13**, so a role with `CREATE` on the database may install it); range types `tstzrange`/`daterange`
with `'[)'` bounds — the canonical booking shape, an empty range overlaps nothing, a lower bound above the upper is
an error (`22000`); *11.8 Partial Indexes* (the predicate must use `IMMUTABLE` functions only — else `42P17`);
SQLite *Partial Indexes*, *CREATE TRIGGER* (`RAISE(ABORT, msg)` → `SQLITE_CONSTRAINT_TRIGGER`, extended code 1811),
*UPSERT* and *RETURNING*; the gapless-counter pattern (a counter row updated in the business transaction, the
standard answer for invoice numbers where `CREATE SEQUENCE` is gap-prone by design — `nextval` is never rolled back).

## Global Constraints

- Worktree `/Users/martiniak/Developer/GitHub/Burgyn/MMLib.Alvo-wt-hx12`. Issue **#389**, label `needs-deep-review`.
  **Three slices, three PRs to `main`**, each green and shippable alone, each rebased onto `main` after the previous
  merges: **A** `hx/12a-sequence` (Tasks 1–3; this plan file lands here — rename the current branch
  `git branch -m hx/12a-sequence` before Task 1), **B** `hx/12b-partial-exclusion-indexes` (Tasks 4–7),
  **C** `hx/12c-integrity-surfaces` (Tasks 8–9). Only slice C's PR body carries `Closes #389`; A and B say `Part of #389`.
- One writer per worktree; never `git add -A` (stage by path); `git branch --show-current` before every commit and
  push. Never `git clean`. Never run Stryker locally (mutation is configuration only — new EF files fall into
  `data-ef-rest`, core files into the `core`/`expressions` shards by their globs; no config change).
- **Scope is §7 HX-12 only.** No timers (HX-13), no relation rules (HX-19), no soft-delete awareness in indexes (D13).
- **Schema changes** (`schema/project.schema.json`, each with the four schema test types — `alvo-schema-testing`):
  slice A adds `$defs/field/properties/sequence`; slice B adds `where` and `exclude` to `$defs/entity/properties/indexes/items`.
- **Public surface added is exactly** (each beyond §5 row `12` is a Decision):
  - A — Abstractions `MMLib.Alvo.Descriptor`: `FieldDescriptor.Sequence` (`FieldSequence?`), `FieldSequence`
    (`Per` `string?`, `Period` `SequencePeriod?`, `Start` `long?`, `Format` `string?`), `SequencePeriod` {`Year`,`Month`,`Day`}
    (string-enum converter like `RollupOp`). `MMLib.Alvo.Schema`: `FieldSchema.Sequence` (`SequenceSchema?`, init),
    `SequenceSchema` (`Per` `string?`, `Period` `SequenceResetPeriod?`, `Start` `long` = 1, `Format` `string?`),
    `SequenceResetPeriod` {`Year`,`Month`,`Day`} (numeric, append-only, like `RollupOperation`). EF:
    `IAlvoSqlDialect.SequenceAllocationStatement(string table)` (DIM → `null`, D5).
  - B — Abstractions `MMLib.Alvo.Data`: `AlvoConstraintKind.Exclusion` (appended after `Check`). `MMLib.Alvo.Descriptor`:
    `EntityIndex.Where` (`string?`), `EntityIndex.Exclude` (`IndexExclusion?`), `IndexExclusion` (`From`, `To`, required).
    `MMLib.Alvo.Schema`: `IndexSchema.Where` (`string?`, init), `IndexSchema.Exclude` (`ExclusionSchema?`, init),
    `ExclusionSchema(string From, string To)`. EF: `IAlvoSqlDialect.ExclusionConstraint(ExclusionConstraintDefinition)`
    (DIM → `null`), `ExclusionConstraintDefinition`, `ExclusionConstraintSql`.
  - Everything else is `internal`; `IPredicateRenderer.RenderConstraint` **stays internal** (D9). Core `MMLib.Alvo`
    public API must not grow. Admin may grow only by Razor components (public by construction, HX-04 ruling 19).
- **Wire and naming constants (verbatim):**
  - framework table `<prefix>_sequences` (`AlvoFrameworkTables.SequencesSuffix = "_sequences"`), columns
    `entity, field, tenant_key, scope_key, period_key TEXT NOT NULL, last_value BIGINT NOT NULL`, primary key the five keys.
  - keys: `tenant_key` = the tenant id `"D"` format, `""` on a global entity; `scope_key` = the `per` value in invariant
    text, `""` without `per`; `period_key` = `yyyy` / `yyyy-MM` / `yyyy-MM-dd` of the write's UTC stamp, `""` without `period`.
  - `format` tokens: `{n}`, `{n:W}` (W = 1–18, zero-padded), `{yyyy}`, `{yy}`, `{MM}`, `{dd}`; exactly one `{n…}`; no other brace.
  - names: partial index `ix_` + entity cut to 46 + `_` + 8 hex of SHA-256(`<entity>|<fields,>|<unique>|<where>`);
    exclusion `xc_` + entity cut to 46 + `_` + 8 hex of SHA-256(`<entity>|<fields,>|<from>|<to>|<where>`); SQLite
    triggers `<xc name>_i`, `<xc name>_u`; SQLite raise text `alvo_exclusion:<xc name>`.
  - 409 for `Exclusion`: type `conflict` (as `Unique`), violation code **`exclusion`**, pointer `/<from>` (or `""` when
    the field carries any `hidden` flag — Ruling X), message `Another record already holds a range here that overlaps this one.`,
    fix `Choose a range that does not overlap one another record already holds, then retry.`;
    `AlvoConstraintViolationException` message for `Exclusion`: `The write would overlap a range another record already holds.`
  - apply refusals (`DescriptorValidationException`, 422 `validation`, code `descriptor`):
    overlap scan at `/entities/<e>/indexes/<i>/exclude`: `<n> pair(s) of '<e>' rows overlap, so this exclusion cannot be added. Nothing was applied.`
    fix `Correct or remove the overlapping rows first — or narrow the exclusion with 'where' — then apply again.`;
    btree_gist at the same pointer: `Excluding overlaps needs PostgreSQL's btree_gist extension, which this database does not have and this connection's role may not install. Nothing was applied.`
    fix `Ask the database owner to run CREATE EXTENSION btree_gist; in this database (it ships with PostgreSQL and is trusted since PostgreSQL 13), then apply again.`;
    declining dialect: `This database engine cannot enforce '<keyword>'. Nothing was applied.` fix `Remove '<keyword>', or run Alvo on SQLite or PostgreSQL.`
  - dynamic refusal (one sentence per keyword, `FieldValidation`'s precedent): `Field '<e>.<f>' declares 'sequence', …`
    / `An index of '<e>' declares 'where'|'exclude', …` `… and '<e>' declares 'storage: dynamic' — dynamic entities have no table to hold one until #41 defines how their integrity is enforced.`
    fix `Remove '<keyword>', or make '<e>' a physical entity.`
- **Engine parity:** every data-path fact runs on SQLite and PostgreSQL — abstract suites in `src/MMLib.Alvo.Testing/Data/`
  subclassed in `test/MMLib.Alvo.Data.Sqlite.Tests` and `test/MMLib.Alvo.Data.PostgreSql.Tests.Integration`; HTTP facts
  in `test/_shared/api/DataApiEngineTests.*.cs` (`SqliteDataApiTests`, `DataApiOnPostgresTests`). PostgreSQL DDL goldens
  skip on Windows runners like their siblings (`ba4943d2`).
- **Security core** (§6.2 lists HX-12): constraint DDL, decoding and the write path change. The `alvo-security-core-review`
  checklist and `/security-review` run **once, at stack level, after slice C** — not per slice.
- **Tests are deterministic:** `FakeTimeProvider` for every period fact; concurrency facts assert outcome counts behind a
  start barrier (`TaskCompletionSource`), never timings; no `Task.Delay`.
- C# style per `alvo-dotnet-conventions`: no inline comments, methods ≲ 25 lines, `ConfigureAwait(false)` in `src/`,
  `TestContext.Current.CancellationToken` in tests; XML docs on every public member, no `§`/`#issue` in public docs.
  **`.cs` files are UTF-8 BOM + CRLF** — run `dotnet format whitespace MMLib.Alvo.slnx --include <files…>` after edits.
- **Per task:** run only the affected test projects (`dotnet test --project <p> --filter-class '*<Class>'`), then commit —
  the pre-commit hook runs `dotnet format` and the scoped ring0. **`[slice end]`:** `scripts/test-ring1`, then
  `alvo-plan-guard`, the `alvo-pr-report` skill, push, `gh pr create`. Admin e2e runs filtered only in slice C
  (`scripts/test-admin-e2e --filter IntegrityEditor`). Public-API baselines: run the approval test, check the
  `*.received.txt` adds exactly the symbols above, move it over `*.verified.txt` (the snapshot-judge gate will ask).
- Commits: Conventional Commits, one per task, ending `Claude-Session: https://claude.ai/code/session_01NJAtafM29iddLwfhb5gnmB`;
  no `Closes`/`Fixes` keyword in any commit message.

## Review Focus

1. **A SQLite table rebuild silently drops triggers.** Any later apply that rebuilds the table (a validation added to
   another field, a column type change) must leave the exclusion enforced. `AlvoDataExclusionTests.An_exclusion_survives_a_later_apply_that_rebuilds_its_table` (Task 6, both engines).
2. **Empty and inverted ranges.** `[t, t)` overlaps nothing on PostgreSQL; SQLite must agree (an existing empty row
   must not block). An inverted range (`to < from`) is a 422 at `/<to>`, never PostgreSQL's `22000` 500.
   `AlvoDataExclusionTests.An_empty_range_overlaps_nothing_on_either_engine`, `…An_inverted_range_is_refused_at_its_end_field` (Task 7).
3. **A PostgreSQL index predicate must be immutable.** A `where` comparing a `datetime` field with a timestamp literal must
   apply, and anything the engine still refuses (`42P17`) is a refusal at the `where` pointer, not a 500.
   `AlvoDataPartialIndexTests.A_where_comparing_a_datetime_with_a_timestamp_literal_applies` (Task 4).
4. **Renaming a sequenced entity or field keeps counting** — a restart would re-issue numbers.
   `AlvoDataSequenceTests.Renaming_a_sequenced_entity_and_field_keeps_counting` (Task 2).
5. **Partitions start at `start`:** two tenants, two `per` values, and a year rollover (FakeTimeProvider) each count
   from `start`; the same partition never restarts. `AlvoDataSequenceTests.Each_tenant_per_value_and_period_counts_from_start` (Task 2).

## Decisions (the spec does not settle these; one line each, with the cost if wrong)

- D1 `exclude: { from, to }` is a half-open `[from, to)` range of two `date`/`datetime` fields; `fields` are `WITH =` columns, `tenant_id` prepended on a scoped entity (as `UniqueColumns`). — Cost: an inclusive-end use case needs a second keyword.
- D2 SQLite enforces an exclusion with `AFTER INSERT`/`AFTER UPDATE` triggers (§6.1's "in-transaction overlap check", run by the engine so every write path is covered; `BEGIN IMMEDIATE` makes it see the committed winner). — Cost: two triggers per exclusion to keep through rebuilds (Review Focus 1).
- D3 On PostgreSQL, a write to an entity with an exclusion first takes `TransactionLockStatement` keys (one per exclusion: SHA-256 of name + tenant + equality values), so concurrent inserters of one resource queue instead of hitting `40P01` deadlocks between exclusion waiters. — Cost: writers to one resource serialize (they must anyway).
- D4 Inverted ranges: the mapper ANDs `value >= new.<from>` into the `to` field's `CheckExpression`, reusing HX-11's `CHECK` end to end (DDL, rebuild, scan, 422 decode). — Cost: an apply refusal over inverted rows points at `/fields/<to>/validation`, a path the author did not write.
- D5 `SequenceAllocationStatement` is a **public** DIM returning `null` (§5 names only `ExclusionConstraint`; the statement is not ANSI, §6.1 *Azure SQL*); `null` → apply refuses `sequence`. The T-SQL double answers `MERGE … WITH (HOLDLOCK) … OUTPUT`. — Cost: one public member.
- D6 The number is allocated after before-hooks, immediately before the `INSERT`, keyed on the write's stamp instant in UTC; before-hooks see no number; `per` reads the post-hook row; a later change of `per` does not renumber. — Cost: local-time period boundaries need a follow-up `timeZone`.
- D7 Counter rows are never deleted (removing then re-adding a sequence resumes; a rollback never re-issues); a declared entity/field rename rewrites their keys in the apply transaction. — Cost: orphan rows of a few bytes.
- D8 `sequence` is allowed on `integer` (no `format`) and `string` (`format` required); refused with `default`, `computed`, `rollup`, `maxLength`; `per` names a `required` field of type string/integer/enum/ref/uuid/date/boolean, not itself. — Cost: authors relax a refusal later, never the reverse.
- D9 `IPredicateRenderer.RenderConstraint` stays internal: Ruling 17 made it public only "when `where` or a third-party renderer needs it", and the EF driver renders `where` through its existing `InternalsVisibleTo`. — Cost: one member to publish later, non-breaking.
- D10 Partial and exclusion indexes carry hashed names (constants above); a changed definition is a new name, so the plan is drop-old + create-new and the diff is a set difference. — Cost: a rebuild of the index on any `where` edit.
- D11 `where` (index and exclusion) admits HX-11's stored-predicate surface minus `now()` and temporal arithmetic, over this entity's own declared fields, and refuses a field carrying any `hidden` flag (Ruling 71's oracle argument). — Cost: a legitimate filter on a hidden field is refused.
- D12 `btree_gist`: the plan probes `pg_extension`/`pg_available_extensions`/`has_database_privilege(…,'CREATE')`; installed → nothing; installable → the plan's first statement is `CREATE EXTENSION IF NOT EXISTS btree_gist`; otherwise the refusal above. — Cost: an apply may install an extension (trusted, contrib).
- D13 Soft-deleted rows still count for exclusions and partial unique indexes, exactly as for `unique` today (follow-up issue filed in Task 9). — Cost: a deleted booking blocks its slot until purged.
- D14 `InMemoryAlvoData` assigns sequence numbers (a host's unit test reads them) but enforces neither exclusion nor partial index (HX-11 Ruling 28). `EfCoreSchemaIntrospector` is unchanged (first-run adoption only). — Cost: in-memory tests cannot see a 409 from `exclude`.

## File structure

| Path | Responsibility |
|---|---|
| `schema/project.schema.json` | `sequence` (A), `indexes[].where`/`exclude` (B) |
| `src/MMLib.Alvo.Abstractions/Descriptor/{FieldDescriptor,EntityDescriptor,DescriptorEnums}.cs` | descriptor model |
| `src/MMLib.Alvo.Abstractions/Schema/{FieldSchema,IndexSchema,SequenceSchema,ExclusionSchema}.cs` | schema model |
| `src/MMLib.Alvo.Abstractions/Data/AlvoConstraint{Kind,ViolationException}.cs` | `Exclusion` + its message |
| `src/MMLib.Alvo/Descriptor/DescriptorToSchemaMapper.cs` | maps; D4 range check synthesis |
| `src/MMLib.Alvo/Descriptor/Internal/SequenceFieldCheck.cs` (new) | `sequence` findings incl. dynamic |
| `src/MMLib.Alvo/Descriptor/Internal/IndexFilterCheck.cs`, `ExclusionIndexCheck.cs` (new) | `where` / `exclude` findings |
| `src/MMLib.Alvo/Schema/MaintainedFields.cs` (new, internal) | one `IsMaintained` (computed ∨ rollup ∨ sequence) for the four call sites |
| `src/MMLib.Alvo.Data.EntityFrameworkCore/Internal/SequenceTable.cs`, `SequenceNumbers.cs`, `SequenceFormat.cs` (new) | table, allocation, rendering |
| `src/MMLib.Alvo.Data.EntityFrameworkCore/Internal/IndexNames.cs`, `IndexFilterSql.cs` (new) | hashed names, `where` SQL |
| `src/MMLib.Alvo.Data.EntityFrameworkCore/Internal/ExclusionPlan.cs`, `ExclusionOverlapSql.cs`, `ExclusionLocks.cs` (new) | migrator ops, scan + trigger join, D3 locks |
| `src/MMLib.Alvo.Data.EntityFrameworkCore/ExclusionConstraint{Definition,Sql}.cs` (new, public) | DIM shapes |
| `src/MMLib.Alvo.Data.{Sqlite,PostgreSql}/*SqlDialect.cs` | the DIMs, decode `1811` / `23P01`, PG `btree_gist` probe |
| `src/MMLib.Alvo.Testing.EntityFrameworkCore/TSqlSqlDialect.cs` | `MERGE`; exclusion declined |
| `src/MMLib.Alvo.Testing/Data/AlvoData{Sequence,PartialIndex,Exclusion}Tests.cs` (new, abstract) | engine suites |
| `src/MMLib.Alvo.Admin/Components/Schema/{FieldEditor.razor(.cs),FieldFacets.Maintained.cs,Indexes.razor,WorkingCopy.Indexes.cs}` | editors |
| `plugins/alvo/skills/alvo-descriptor-{indexes,field-types-and-formats}/SKILL.md` | skills |
| `website/src/content/docs/extending/integrity.mdx`, `website/src/snippets/extending/integrity/` | docs + executed snippet |
| `docs/architecture/data-path.md`, `CHANGELOG.md`, the spec | architecture doc, changelog, *HX-12 settled details* |

---

# Slice A — `hx/12a-sequence`

## Task 1: `sequence` in the descriptor — schema, models, findings, maintained-field semantics

**Files:**
- Modify: `schema/project.schema.json` (`$defs/field/properties/sequence`), `src/MMLib.Alvo.Abstractions/Descriptor/FieldDescriptor.cs`, `DescriptorEnums.cs`, `src/MMLib.Alvo.Abstractions/Schema/FieldSchema.cs`, `src/MMLib.Alvo/Descriptor/DescriptorToSchemaMapper.cs`, `src/MMLib.Alvo/Descriptor/Internal/DescriptorValidator.cs`, `src/MMLib.Alvo.Data.EntityFrameworkCore/Internal/WritePayloadGuard.cs`, `src/MMLib.Alvo/Hooks/Internal/HookSets.cs`, `src/MMLib.Alvo/Hooks/Internal/WriteExtensionRunner.cs`, `src/MMLib.Alvo/Api/Internal/RecordValidator.cs`, `src/MMLib.Alvo/Data/Internal/RecordWriter.cs`, the OpenAPI field schema writer (where `computed` sets `readOnly: true`)
- Create: `src/MMLib.Alvo.Abstractions/Schema/SequenceSchema.cs`, `src/MMLib.Alvo/Descriptor/Internal/SequenceFieldCheck.cs`, `src/MMLib.Alvo/Schema/MaintainedFields.cs`, `examples/_negative/sequence-two-numbers.json`, `examples/_negative/sequence-unknown-period.json` (+ `expectations.json` rows)
- Test: `test/MMLib.Alvo.Schema.Tests/{RoundTripTests,ExamplesTests,SnapshotTests}.cs`, `test/MMLib.Alvo.Tests/Descriptor/SequenceFieldCheckTests.cs`, `test/MMLib.Alvo.Abstractions.Tests` public-API baseline

**Interfaces:**
- Produces: `FieldSchema.Sequence` (`SequenceSchema? { Per, Period, Start, Format }`), `SequenceResetPeriod`, internal
  `MaintainedFields.IsMaintained(this FieldSchema)` (`ComputedExpression is not null || Rollup is not null || Sequence is not null`).

- [ ] **Step 1: Schema + failing schema tests.** Add to `$defs/field/properties`:

```json
"sequence": {
  "type": "object",
  "additionalProperties": false,
  "description": "A number the framework assigns on create: gapless and never reused, counted per tenant, and optionally per another field's value (`per`) and per calendar period (`period`, in UTC). type=integer stores the number; type=string requires `format`. The field becomes read-only.",
  "properties": {
    "per": { "type": "string", "pattern": "^[a-z][a-z0-9_]{0,62}$", "description": "A required field of this entity whose value starts its own count." },
    "period": { "enum": ["year", "month", "day"], "description": "Restart the count each UTC calendar period." },
    "start": { "type": "integer", "minimum": 0, "default": 1, "description": "The first number of every count." },
    "format": {
      "type": "string",
      "pattern": "^[^{}]*(\\{(yyyy|yy|MM|dd)\\}[^{}]*)*\\{n(:([1-9]|1[0-8]))?\\}[^{}]*(\\{(yyyy|yy|MM|dd)\\}[^{}]*)*$",
      "description": "type=string only: the text stored, e.g. \"SO-{yyyy}-{n:4}\" → SO-2026-0042. Exactly one {n} or {n:width}; {yyyy} {yy} {MM} {dd} read the create's UTC date."
    }
  }
}
```

Negative examples: `sequence-two-numbers.json` (`"format": "{n}-{n}"`), `sequence-unknown-period.json` (`"period": "week"`),
each with its `expectations.json` row. Extend the CsCheck descriptor generator in `RoundTripTests` with an optional
`sequence` on integer and string fields. Run `dotnet test --project test/MMLib.Alvo.Schema.Tests` — FAIL (round-trip drops
the member: no model property yet).

- [ ] **Step 2: Models.** `FieldDescriptor.Sequence` (`FieldSequence?`) and `FieldSequence` (init members, XML docs);
`SequencePeriod` in `DescriptorEnums.cs` with `[JsonConverter(typeof(JsonStringEnumConverter<SequencePeriod>))]` like
`RollupOp`. `SequenceSchema` (record, `Start` defaults to `1`) and `SequenceResetPeriod` (numeric, append-only remark
copied from `RollupOperation`). `FieldSchema.Sequence`. Mapper: `Sequence = f.Sequence is { } s ? new SequenceSchema { Per = s.Per, Period = Map(s.Period), Start = s.Start ?? 1, Format = s.Format } : null`.
Re-run the schema tests — PASS. Accept the Abstractions public-API baseline (exactly the A symbols).

- [ ] **Step 3: Failing findings tests** (`SequenceFieldCheckTests`, one fact per finding, each asserting pointer
`/entities/<e>/fields/<f>/sequence[/<member>]` and the message):
`A_sequence_on_a_decimal_field_is_refused`, `A_string_sequence_without_format_is_refused`,
`An_integer_sequence_with_format_is_refused`, `A_sequence_with_a_default_computed_rollup_or_maxLength_is_refused`,
`A_per_naming_no_field_itself_or_an_optional_field_is_refused`, `A_per_of_type_json_decimal_or_datetime_is_refused`,
`A_sequence_on_a_dynamic_entity_is_refused_naming_41`, `A_valid_sequence_has_no_findings`.

- [ ] **Step 4: Implement `SequenceFieldCheck`** (internal, `IReadOnlyList<DescriptorValidationError> Check(AlvoDescriptor)`),
called from `DescriptorValidator` beside the HX-11 validation checks, the dynamic refusal before the physical filter
drops dynamic entities (as `FieldValidation`'s Ruling 30 refusal does). The format grammar lives once, in
`src/MMLib.Alvo.Abstractions/Schema/SequenceSchema.cs` as `internal static bool IsValidFormat(string)` (the token regex
of Task 2's `SequenceFormat` plus "exactly one `{n…}`, no other brace"), reached by core, EF and Admin through
Abstractions' existing `InternalsVisibleTo` grants; facts `SequenceFieldCheckTests.A_format_with_two_numbers_or_an_unknown_token_is_refused`. Run `dotnet test --project test/MMLib.Alvo.Tests --filter-class '*SequenceFieldCheckTests'` — PASS.

- [ ] **Step 5: Maintained-field semantics, failing first.** Add facts:
`WritePayloadGuardTests.A_payload_naming_a_sequence_field_is_refused_like_a_computed_one` (same code, pointer at the field),
`HookSetsTests.A_mutate_targeting_a_sequence_field_is_refused_at_apply`,
`RecordValidatorTests.A_required_sequence_field_absent_from_a_create_is_not_reported_missing`,
`OpenApiDocumentFacts.A_sequence_field_is_read_only_in_the_document` (in `test/_shared/api`). Then replace the four
`ComputedExpression is not null || Rollup is not null` predicates with `field.IsMaintained()` and extend
`WritePayloadGuard.ComputedRefusal`'s field set to `IsMaintained()` with the message naming "assigned by the framework".
Run the four classes in `MMLib.Alvo.Tests`, `MMLib.Alvo.Data.EntityFrameworkCore.Tests`, `MMLib.Alvo.Api.Tests` — PASS.

- [ ] **Step 6: Commit** — `feat(descriptor): declare a field's sequence`.

## Task 2: `<prefix>_sequences` and allocation in the write's transaction — both engines

**Files:**
- Create: `src/MMLib.Alvo.Data.EntityFrameworkCore/Internal/SequenceTable.cs`, `SequenceNumbers.cs`, `SequenceFormat.cs`;
  `src/MMLib.Alvo.Testing/Data/AlvoDataSequenceTests.cs`; `test/MMLib.Alvo.Data.Sqlite.Tests/SqliteSequenceTests.cs`;
  `test/MMLib.Alvo.Data.PostgreSql.Tests.Integration/PostgreSqlSequenceTests.cs`; `test/MMLib.Alvo.Data.EntityFrameworkCore.Tests/SequenceFormatTests.cs`
- Modify: `src/MMLib.Alvo.Abstractions/AlvoFrameworkTables.cs` (suffix + the introspection-exclusion list),
  `SystemSchemaInitializer.cs`, `IAlvoSqlDialect.cs`, `SqliteSqlDialect.cs`, `PostgreSqlSqlDialect.cs`,
  `src/MMLib.Alvo.Testing.EntityFrameworkCore/TSqlSqlDialect.cs`, `AlvoSqlDialectContractTests.cs`,
  `EfAlvoData.cs` (`InsertAsync`, `InsertManyAsync` and their callers pass `now`), `EfCoreSchemaMigrator.cs`
  (refusal for a declining dialect; rename rewrite), `src/MMLib.Alvo.Testing/Data/InMemoryAlvoData.cs`
- Test: `test/MMLib.Alvo.Data.EntityFrameworkCore.Tests/DialectContractTests.cs`

**Interfaces:**
- Consumes: `FieldSchema.Sequence`, `SequenceSchema.IsValidFormat` (Task 1).
- Produces: `string? IAlvoSqlDialect.SequenceAllocationStatement(string table)` — one statement binding `@entity @field
  @tenant @scope @period` (text) and `@start` (bigint), answering one row, one bigint column: the allocated number.

- [ ] **Step 1: Format + period, failing first** (`SequenceFormatTests`): `Render("SO-{yyyy}-{n:4}", 42, 2026-11-06Z) == "SO-2026-0042"`;
`{n}` never pads; a number wider than W is not truncated (`{n:2}`, 1234 → `1234`); `{yy}{MM}{dd}`; `PeriodKey` for each
period and `null`. Then:

```csharp
// src/MMLib.Alvo.Data.EntityFrameworkCore/Internal/SequenceFormat.cs
internal static partial class SequenceFormat
{
    [GeneratedRegex(@"\{(?<token>n(?::(?<width>1[0-8]|[1-9]))?|yyyy|yy|MM|dd)\}", RegexOptions.CultureInvariant)]
    private static partial Regex Token();

    internal static string Render(string format, long number, DateTimeOffset instant) =>
        Token().Replace(format, match => Part(match, number, instant.UtcDateTime));

    internal static string PeriodKey(SequenceResetPeriod? period, DateTimeOffset instant) => period switch
    {
        SequenceResetPeriod.Year => Text(instant.UtcDateTime, "yyyy"),
        SequenceResetPeriod.Month => Text(instant.UtcDateTime, "yyyy-MM"),
        SequenceResetPeriod.Day => Text(instant.UtcDateTime, "yyyy-MM-dd"),
        _ => string.Empty,
    };

    private static string Part(Match match, long number, DateTime utc) => match.Groups["token"].Value switch
    {
        "yyyy" or "yy" or "MM" or "dd" => Text(utc, match.Groups["token"].Value),
        _ when match.Groups["width"].Success => number.ToString(
            "D" + match.Groups["width"].Value, CultureInfo.InvariantCulture),
        _ => number.ToString(CultureInfo.InvariantCulture),
    };

    private static string Text(DateTime utc, string pattern) => utc.ToString(pattern, CultureInfo.InvariantCulture);
}
```

Run `dotnet test --project test/MMLib.Alvo.Data.EntityFrameworkCore.Tests --filter-class '*SequenceFormatTests'` — PASS.

- [ ] **Step 2: The DIM and its three answers, failing first.** Contract facts in `AlvoSqlDialectContractTests`:
`A_sequence_allocation_statement_is_declined_or_binds_every_key_once` (null, or each of the six markers occurs exactly
once). In `DialectContractTests.cs` (`TSqlSqlDialectContractTests`) — **AC (5), sequence half:**
`The_TSql_double_allocates_a_sequence_number_with_one_MERGE_under_HOLDLOCK` (golden string equality). Then:

```csharp
// IAlvoSqlDialect.cs — remarks: return grammar (one statement, one row, one bigint column; the six markers above);
// it runs inside the create's transaction and must lock the counter row until that transaction ends, so a rollback
// returns the number; null = this engine cannot keep a gapless counter and the apply refuses `sequence`.
string? SequenceAllocationStatement(string table) => null;
```

```csharp
// src/MMLib.Alvo.Data.EntityFrameworkCore/Internal/SequenceTable.cs
internal static class SequenceTable
{
    internal static string NameFor(string schemaPrefix) => schemaPrefix + AlvoFrameworkTables.SequencesSuffix;

    internal static string Ddl(string table) =>
        $"""
        CREATE TABLE IF NOT EXISTS {table} (
            entity TEXT NOT NULL,
            field TEXT NOT NULL,
            tenant_key TEXT NOT NULL,
            scope_key TEXT NOT NULL,
            period_key TEXT NOT NULL,
            last_value BIGINT NOT NULL,
            PRIMARY KEY (entity, field, tenant_key, scope_key, period_key)
        )
        """;

    internal static string Upsert(string table) =>
        $"""
        INSERT INTO {table} (entity, field, tenant_key, scope_key, period_key, last_value)
        VALUES (@entity, @field, @tenant, @scope, @period, @start)
        ON CONFLICT (entity, field, tenant_key, scope_key, period_key)
        DO UPDATE SET last_value = {table}.last_value + 1
        RETURNING last_value
        """;

    internal static string RenameEntity(string table, string from, string to) =>
        $"UPDATE {table} SET entity = {AlvoSqlStringLiteral.Quote(to)} WHERE entity = {AlvoSqlStringLiteral.Quote(from)}";

    internal static string RenameField(string table, string entity, string from, string to) =>
        $"UPDATE {table} SET field = {AlvoSqlStringLiteral.Quote(to)} WHERE entity = {AlvoSqlStringLiteral.Quote(entity)} AND field = {AlvoSqlStringLiteral.Quote(from)}";

    internal static Task EnsureAsync(DbConnection connection, string table, CancellationToken ct) =>
        IfNotExistsDdl.CreateTableAsync(connection, table, Ddl(table), ct);
}
```

SQLite and PostgreSQL implement `SequenceAllocationStatement(table) => SequenceTable.Upsert(table)` (on PostgreSQL
`INSERT … ON CONFLICT DO UPDATE` under READ COMMITTED: a concurrent allocator of the same key blocks on the row — or on
the unique index for a new key — until the first commits or rolls back, then takes the next value; a rolled-back
first insert lets the waiter insert `start`). The T-SQL double:

```csharp
public string? SequenceAllocationStatement(string table) =>
    $"""
    MERGE {table} WITH (HOLDLOCK) AS s
    USING (SELECT @entity AS entity, @field AS field, @tenant AS tenant_key, @scope AS scope_key, @period AS period_key) AS k
    ON s.entity = k.entity AND s.field = k.field AND s.tenant_key = k.tenant_key AND s.scope_key = k.scope_key AND s.period_key = k.period_key
    WHEN MATCHED THEN UPDATE SET last_value = s.last_value + 1
    WHEN NOT MATCHED THEN INSERT (entity, field, tenant_key, scope_key, period_key, last_value)
        VALUES (k.entity, k.field, k.tenant_key, k.scope_key, k.period_key, @start)
    OUTPUT inserted.last_value;
    """;
```

`SystemSchemaInitializer` creates the table at boot beside the hook-rejections table; `AlvoFrameworkTables` lists it so
introspection never reports it. Run `EntityFrameworkCore.Tests` dialect classes — PASS.

- [ ] **Step 3: The engine suite, failing.** `AlvoDataSequenceTests` (abstract; `CreateAsync(SchemaModel, AlvoDescriptor)`,
`TimeProvider`-injected host like `AlvoDataCheckConstraintTests`; a scoped `orders` entity with `number` integer
sequence, `code` string sequence `"SO-{yyyy}-{n:4}"` `period: year`, `branch` required string, `qty` with
`validation: "value > 0"`). Facts:
  - **AC (1)** `A_hundred_concurrent_creates_number_one_to_a_hundred_without_gap_or_duplicate` — 100 creates released
    together by one `TaskCompletionSource`, `Task.WhenAll`; the stored `number`s sorted equal `1..100`.
  - **AC (2)** `A_rolled_back_create_consumes_no_number` — create (1), a create with `qty: 0` refused by its CHECK
    (`AlvoConstraintViolationException { Kind: Check }`), a create → `2`.
  - **AC (2)** `A_create_rolled_back_with_its_unit_consumes_no_number` — inside `TransactAsync`, a create then a thrown
    host exception; the next create outside gets the number the rolled-back one held.
  - Review Focus 5 `Each_tenant_per_value_and_period_counts_from_start` — two tenants, `per: branch` two values, and the
    FakeTimeProvider moved from 2026-12-31T23:59:59Z to 2027-01-01T00:00:00Z: each partition starts at `start`; `code` is `SO-2027-0001`.
  - `A_batch_create_numbers_its_rows_in_order` (`CreateManyAsync` of 3 → consecutive, in request order).
  - `A_replace_that_creates_assigns_a_number_and_one_that_updates_keeps_it`.
  - Review Focus 4 `Renaming_a_sequenced_entity_and_field_keeps_counting` — two creates, an apply renaming `orders`
    → `sales_orders` and `number` → `seq` via `renamedFrom`, a create → `3`.
  - `A_declining_engine_refuses_the_apply_at_the_sequence` — host with a dialect decorator answering `null`: the plan
    throws `DescriptorValidationException` at `/entities/orders/fields/number/sequence` with the declining-dialect sentence.
Subclasses `SqliteSequenceTests` / `PostgreSqlSequenceTests` (fixtures as `SqliteCheckConstraintTests` /
`PostgreSqlCheckConstraintTests`). Run both — FAIL (no number assigned).

- [ ] **Step 4: Allocation.**

```csharp
// src/MMLib.Alvo.Data.EntityFrameworkCore/Internal/SequenceNumbers.cs
internal sealed class SequenceNumbers(IAlvoSqlDialect dialect, string table)
{
    internal async Task AssignAsync(
        AlvoDataContext db, EntitySchema schema, IDictionary<string, object> row, DateTimeOffset instant, CancellationToken ct)
    {
        foreach (var field in schema.Fields.Where(field => field.Sequence is not null))
        {
            var sequence = field.Sequence!;
            var number = await NextAsync(db, Keys(schema, field, row, instant), sequence.Start, ct).ConfigureAwait(false);
            row[field.Name] = sequence.Format is { } format ? SequenceFormat.Render(format, number, instant) : number;
        }
    }

    private static (string Entity, string Field, string Tenant, string Scope, string Period) Keys(
        EntitySchema schema, FieldSchema field, IDictionary<string, object> row, DateTimeOffset instant) => (
        schema.Name,
        field.Name,
        row.TryGetValue(AlvoDataContext.TenantIdColumn, out var tenant) ? Invariant(tenant) : string.Empty,
        field.Sequence!.Per is { } per ? Invariant(row[per]) : string.Empty,
        SequenceFormat.PeriodKey(field.Sequence.Period, instant));

    private async Task<long> NextAsync(
        AlvoDataContext db, (string Entity, string Field, string Tenant, string Scope, string Period) keys, long start,
        CancellationToken ct)
    {
        var command = db.Database.GetDbConnection().CreateCommand();
        await using (command.ConfigureAwait(false))
        {
            command.Transaction = db.Database.CurrentTransaction?.GetDbTransaction()
                ?? throw new InvalidOperationException("A sequence number is allocated only inside the create's transaction.");
            command.CommandText = dialect.SequenceAllocationStatement(table)
                ?? throw new InvalidOperationException($"{dialect.GetType().Name} cannot allocate a sequence number.");
            Bind(command, keys, start);
            return Convert.ToInt64(await command.ExecuteScalarAsync(ct).ConfigureAwait(false), CultureInfo.InvariantCulture);
        }
    }
}
```

`Bind` adds the six parameters through `RelationalSqlBatch.AddParameter`; `Invariant` is `Convert.ToString(value,
CultureInfo.InvariantCulture)` (a `Guid` tenant renders `"D"`). `EfAlvoData` constructs one `SequenceNumbers` from
`_dialect` and the prefixed table; `InsertAsync`/`InsertManyAsync` gain a `DateTimeOffset now` parameter (every caller
already holds `WriteInstantNow()`'s value) and call `AssignAsync` per candidate **before** `db.Rows(…).Add(...)` — after
hooks, policy and payload checks (D6). If `ChangeTrackerReachTests` flags the new file, add it to that allow-list with
the reason "reads the write's connection and transaction". `InMemoryAlvoData` (D14) keeps a `Dictionary<(…), long>`
under its existing lock and assigns the same way (rolled back with its own snapshot restore).

- [ ] **Step 5: The migrator.** In `EfCoreSchemaMigrator.PlanAsync`, when `desired` declares any sequence and
`_dialect.SequenceAllocationStatement("t") is null`, throw the declining-dialect refusal at the first sequence's
pointer. In `AssembleOperations`, for each declared rename in `prePass.Renames` append a `SqlOperation` with
`SequenceTable.RenameEntity`/`RenameField` (identifiers are descriptor names, `^[a-z][a-z0-9_]*$`, quoted by the
literal writer). Counter rows are never deleted (D7). Run both engine subclasses — PASS.

- [ ] **Step 6: Commit** — `feat(data): gapless sequence numbers in the create's transaction on both engines`.

## Task 3: Sequences over HTTP, both engines, and slice A's ship **[slice end]**

**Files:**
- Create: `test/_shared/api/DataApiEngineTests.Sequence.cs`, `test/_shared/api/descriptors/sequence-orders.alvo.json`
- Modify: `CHANGELOG.md` (`[Unreleased] / Added`)

- [ ] **Step 1: Failing HTTP facts** (`SqliteDataApiTests`, `DataApiOnPostgresTests`):
`A_create_answers_its_sequence_number_and_a_sent_one_is_refused_on_every_engine` (201 body carries `number: 1`,
`code: "SO-2026-0001"`; a create sending `number` → 422 at `/number`), `A_patch_cannot_change_a_sequence_number`
(422 at `/number`). Run — FAIL until the descriptor fixture `sequence-orders.alvo.json` exists; add it; PASS.

- [ ] **Step 2: CHANGELOG** — under *Added*: "`sequence` on a field: a gapless number per tenant, optionally per another
field's value and per UTC year/month/day, stored as an integer or formatted text (`SO-{yyyy}-{n:4}`); the T-SQL double
and any dialect without `SequenceAllocationStatement` refuse it at apply."

- [ ] **Step 3: Commit** — `test(api): sequence numbers over HTTP on both engines`.

- [ ] **Step 4: Rings and ship.** `scripts/test-ring1`; dispatch `alvo-plan-guard`; build the report via the
`alvo-pr-report` skill; `git push -u origin hx/12a-sequence`; `gh pr create --base main --label needs-deep-review`
with the five-line body (`Part of #389`).

---

# Slice B — `hx/12b-partial-exclusion-indexes` (branch from `main` after A merges)

## Task 4: `indexes[].where` — partial indexes

**Files:**
- Modify: `schema/project.schema.json` (`indexes.items.properties.where`), `EntityDescriptor.cs` (`EntityIndex.Where`),
  `IndexSchema.cs` (`Where`), `DescriptorToSchemaMapper.cs`, `DescriptorValidator.cs`,
  `DescriptorModelBuilder.ConfigureIndexes`, `EfCoreSchemaMigrator.cs` (`42P17` → refusal), `RenamePrePass.AlignIndexes`,
  `RenameGuessSplitter.cs` (carry `Where`)
- Create: `src/MMLib.Alvo/Descriptor/Internal/IndexFilterCheck.cs`, `src/MMLib.Alvo.Data.EntityFrameworkCore/Internal/IndexNames.cs`,
  `IndexFilterSql.cs`, `src/MMLib.Alvo.Testing/Data/AlvoDataPartialIndexTests.cs` + `Sqlite`/`PostgreSql` subclasses,
  `test/MMLib.Alvo.Tests/Descriptor/IndexFilterCheckTests.cs`, `examples/_negative/index-where-not-cel-string.json`

**Interfaces:**
- Consumes: HX-11's `CheckConstraintCheck` compile path and `IPredicateRenderer.RenderConstraint` (internal).
- Produces: `IndexSchema.Where`; `IndexNames.Partial(EntitySchema, IndexSchema)` and `IndexNames.Exclusion(EntitySchema, IndexSchema)`
  (Task 5 uses the second); `IndexFilterSql.For(EntitySchema, string where) : string` (inline-literal SQL, unqualified columns).

- [ ] **Step 1: Schema + model.** `"where": { "$ref": "#/$defs/cel", "description": "Index only the rows this CEL predicate over the entity's own fields accepts — a partial index. With unique, duplicates outside it are allowed." }`.
`EntityIndex.Where`, `IndexSchema.Where` (init; record equality now includes it). Round-trip generator gains an optional
`where`. Run `MMLib.Alvo.Schema.Tests` — PASS; accept the Abstractions baseline (`EntityIndex.Where`, `IndexSchema.Where`).

- [ ] **Step 2: Findings, failing first** (`IndexFilterCheckTests`, pointer `/entities/<e>/indexes/<i>/where`):
`A_where_reading_now_or_a_duration_is_refused`, `A_where_reading_the_caller_is_refused`, `A_where_reading_an_undeclared_field_is_refused`,
`A_where_reading_a_hidden_field_is_refused` (D11), `A_where_on_a_dynamic_entity_is_refused_naming_41`,
`A_where_that_is_not_a_predicate_is_refused`, `A_valid_where_has_no_findings`. Implement `IndexFilterCheck` by
compiling with the same profile and surface `CheckConstraintCheck` uses for `validation` (without `value`; `new.`
optional, bare field names as in `rollup.where`) and rejecting the temporal construct kind. PASS.

- [ ] **Step 3: Names and DDL.**

```csharp
// src/MMLib.Alvo.Data.EntityFrameworkCore/Internal/IndexNames.cs
internal static class IndexNames
{
    internal static string Partial(EntitySchema entity, IndexSchema index) =>
        Named("ix_", entity.Name, $"{entity.Name}|{string.Join(',', index.Fields)}|{index.Unique}|{index.Where}");

    internal static string Exclusion(EntitySchema entity, IndexSchema index) =>
        Named("xc_", entity.Name, $"{entity.Name}|{string.Join(',', index.Fields)}|{index.Exclude!.From}|{index.Exclude.To}|{index.Where}");

    private static string Named(string prefix, string entity, string canonical) =>
        $"{prefix}{entity[..Math.Min(entity.Length, 46)]}_{Hash(canonical)}";

    private static string Hash(string text) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text)))[..8];
}
```

`ConfigureIndexes` (shared by the migrator's model and the runtime model, so `ConstraintViolationTranslator` resolves a
partial unique index by name):

```csharp
foreach (var index in entity.Indexes.Where(index => index.Exclude is null))
{
    var columns = index.Unique ? UniqueColumns(entity, index.Fields) : index.Fields;
    var built = index.Where is null
        ? entityBuilder.HasIndex([.. columns])
        : entityBuilder.HasIndex([.. columns], IndexNames.Partial(entity, index)).HasFilter(filters!.For(entity, index.Where));
    built.IsUnique(index.Unique);
}
```

`ConfigureIndexes` gains an `IndexFilterSql? filters` parameter (both call sites pass the one their model builder
already holds beside `CheckConstraintSql`), and `UniqueColumns` becomes `internal` (Tasks 6–7 reuse it). `filters` is
an `IndexFilterSql` built like `CheckConstraintSql` (refuse with the "call AddAlvo()" sentence when absent —
`EnsureChecksAreRenderable`'s precedent). `RenamePrePass.AlignIndexes` and `RenameGuessSplitter` copy `Where`/`Exclude`
with `with { … }`. A rename of a field the `where` reads is the same name change HX-11 handles for `CHECK`: the
`where` text is re-rendered, so the name changes and the index is recreated.

- [ ] **Step 4: Engine suite, failing then passing.** `AlvoDataPartialIndexTests` (scoped `members`: `email` string,
`active` boolean required, index `{fields:["email"], unique:true, where:"active"}`):
  - **AC (4)** `A_partial_unique_index_allows_duplicates_outside_its_where` — two inactive rows with one email: both
    stored; one active row more: stored; a second active one → `AlvoConstraintViolationException { Kind: Unique, Fields: ["email"] }`.
  - `A_partial_unique_index_is_per_tenant` — the same active email in two tenants: both stored.
  - `Changing_a_where_recreates_the_index` — plan SQL drops the old name and creates the new one.
  - Review Focus 3 `A_where_comparing_a_datetime_with_a_timestamp_literal_applies` (`where: "joined_at > timestamp('2026-01-01T00:00:00Z')"`) — applies on both engines; and
    `An_engine_refusal_of_a_where_is_refused_at_its_pointer` (PostgreSQL leg asserts a `42P17` from a forced non-immutable
    rendering is translated by the migrator to a `DescriptorValidationException` at `/entities/members/indexes/0/where`;
    the SQLite leg asserts the same pointer for SQLite's "non-deterministic functions prohibited in partial index" error).
If the timestamp literal renders through a `STABLE` cast on PostgreSQL, change `PostgreSqlFieldSqlRenderer`'s literal to a
typed constant (`TIMESTAMPTZ '…'`), which the literal writer property (HX-11 Ruling 51) still covers.
Run `SqlitePartialIndexTests`, `PostgreSqlPartialIndexTests` — PASS.

- [ ] **Step 5: Commit** — `feat(data): partial indexes from indexes[].where on both engines`.

## Task 5: `indexes[].exclude` — the model, the findings and the dialect DDL

**Files:**
- Modify: `schema/project.schema.json` (`indexes.items.properties.exclude`), `EntityDescriptor.cs`, `IndexSchema.cs`,
  `DescriptorToSchemaMapper.cs` (D4 synthesis), `DescriptorValidator.cs`, `IAlvoSqlDialect.cs`, `SqliteSqlDialect.cs`,
  `PostgreSqlSqlDialect.cs`, `TSqlSqlDialect.cs`, `AlvoSqlDialectContractTests.cs`
- Create: `src/MMLib.Alvo.Abstractions/Schema/ExclusionSchema.cs`, `src/MMLib.Alvo/Descriptor/Internal/ExclusionIndexCheck.cs`,
  `src/MMLib.Alvo.Data.EntityFrameworkCore/ExclusionConstraintDefinition.cs`, `ExclusionConstraintSql.cs`,
  `Internal/ExclusionOverlapSql.cs`, `test/MMLib.Alvo.Tests/Descriptor/ExclusionIndexCheckTests.cs`,
  `test/MMLib.Alvo.Data.Sqlite.Tests/SqliteExclusionDdlTests.cs`, `test/MMLib.Alvo.Data.PostgreSql.Tests.Integration/PostgreSqlExclusionDdlTests.cs`,
  `examples/_negative/index-exclude-missing-to.json`

**Interfaces:**
- Produces:

```csharp
// public, MMLib.Alvo.Data.EntityFrameworkCore
public sealed record ExclusionConstraintDefinition(
    string Table, string Name, IReadOnlyList<string> EqualityColumns, string StartColumn, string EndColumn,
    MMLib.Alvo.Schema.FieldType RangeType, string? Filter);

public sealed record ExclusionConstraintSql(
    IReadOnlyList<string> Create, IReadOnlyList<string> Drop, bool SurvivesTableRebuild);

// IAlvoSqlDialect — DIM; remarks: Filter is inline-literal SQL over unqualified columns from the constraint renderer,
// never caller text; Create statements may be replayed when SurvivesTableRebuild is false, so they must be idempotent.
ExclusionConstraintSql? ExclusionConstraint(ExclusionConstraintDefinition definition) => null;
```

  `ExclusionOverlapSql.Pairs(definition, Func<string, string> epoch) : string` — the `FROM … JOIN …` body both the SQLite
  trigger and the pre-apply scan (Task 6) select from.

- [ ] **Step 1: Schema + model.**

```json
"exclude": {
  "type": "object",
  "additionalProperties": false,
  "required": ["from", "to"],
  "description": "No two rows with equal `fields` may hold overlapping [from, to) ranges (an empty range overlaps nothing). A write that would is refused 409. PostgreSQL needs the btree_gist extension.",
  "properties": {
    "from": { "type": "string", "pattern": "^[a-z][a-z0-9_]{0,62}$", "description": "A required date or datetime field: where the range starts (inclusive)." },
    "to": { "type": "string", "pattern": "^[a-z][a-z0-9_]{0,62}$", "description": "A required field of the same type: where the range ends (exclusive)." }
  }
}
```

`IndexExclusion { required From, required To }`, `EntityIndex.Exclude`, `ExclusionSchema(From, To)`, `IndexSchema.Exclude`.
Mapper (D4): for each exclusion, the `To` field's `CheckExpression` becomes `value >= new.<from>` or
`(<own>) && value >= new.<from>`. Negative example with `exclude` lacking `to`; round-trip generator gains an optional
`exclude` over two generated datetime fields. Run `MMLib.Alvo.Schema.Tests` — PASS; accept the Abstractions baseline.

- [ ] **Step 2: Findings, failing first** (`ExclusionIndexCheckTests`, pointer `/entities/<e>/indexes/<i>/exclude[/from|/to]`):
`A_range_field_that_is_missing_optional_or_not_temporal_is_refused`, `Range_fields_of_different_types_are_refused`,
`A_range_field_listed_in_fields_is_refused`, `An_equality_field_of_type_json_is_refused`,
`An_exclusion_marked_unique_is_refused`, `A_computed_rollup_or_sequence_range_field_is_refused`,
`An_exclusion_on_a_dynamic_entity_is_refused_naming_41`, `A_valid_exclusion_has_no_findings`; the `where` beside an
`exclude` goes through Task 4's `IndexFilterCheck`. Implement `ExclusionIndexCheck`. PASS.

- [ ] **Step 3: DDL goldens, failing.** For a scoped `bookings` (`room` ref, `starts_at`/`ends_at` datetime required,
`status` enum) with `{fields:["room"], exclude:{from:"starts_at", to:"ends_at"}, where:"status != 'cancelled'"}`:
`PostgreSqlExclusionDdlTests.An_exclusion_is_one_gist_constraint_with_its_where` and
`SqliteExclusionDdlTests.An_exclusion_is_an_insert_and_an_update_trigger_over_one_overlap_join` (Verify the
`ExclusionConstraintSql` as text); contract fact `An_exclusion_is_declined_or_answered_with_create_and_drop`;
**AC (5), exclusion half:** `TSqlSqlDialectContractTests.The_TSql_double_declines_an_exclusion`.

- [ ] **Step 4: Implement.** PostgreSQL:

```csharp
public ExclusionConstraintSql? ExclusionConstraint(ExclusionConstraintDefinition d)
{
    var range = d.RangeType == FieldType.Date ? "daterange" : "tstzrange";
    var equal = d.EqualityColumns.Select(column => $"{RenderColumn(column)} WITH =");
    var overlap = $"{range}({RenderColumn(d.StartColumn)}, {RenderColumn(d.EndColumn)}, '[)') WITH &&";
    var where = d.Filter is null ? string.Empty : $" WHERE ({d.Filter})";
    return new(
        Create: [$"ALTER TABLE {RenderColumn(d.Table)} ADD CONSTRAINT {RenderColumn(d.Name)} EXCLUDE USING gist ({string.Join(", ", [.. equal, overlap])}){where}"],
        Drop: [$"ALTER TABLE {RenderColumn(d.Table)} DROP CONSTRAINT IF EXISTS {RenderColumn(d.Name)}"],
        SurvivesTableRebuild: true);
}
```

The shared join (`ExclusionOverlapSql`, internal, in the EF package; `epoch` wraps a `datetime` operand — SQLite passes
its field renderer's `RenderEpochMicroseconds`, PostgreSQL the identity — and is the identity for `date`):

```csharp
internal static string Pairs(ExclusionConstraintDefinition d, Func<string, string> epoch, string? newRowId)
{
    string Side(string alias) => $"(SELECT * FROM {Quote(d.Table)}{(d.Filter is null ? "" : $" WHERE {d.Filter}")}) AS {Quote(alias)}";
    string Col(string alias, string column) => $"{Quote(alias)}.{Quote(column)}";
    string Ep(string alias, string column) => d.RangeType == FieldType.Date ? Col(alias, column) : epoch(Col(alias, column));
    var on = d.EqualityColumns.Select(column => $"{Col("o", column)} = {Col("n", column)}")
        .Append(newRowId is null ? $"{Col("o", "id")} < {Col("n", "id")}" : $"{Col("o", "id")} <> {Col("n", "id")}")
        .Append($"{Ep("o", d.StartColumn)} < {Ep("o", d.EndColumn)}")
        .Append($"{Ep("n", d.StartColumn)} < {Ep("n", d.EndColumn)}")
        .Append($"{Ep("o", d.StartColumn)} < {Ep("n", d.EndColumn)}")
        .Append($"{Ep("n", d.StartColumn)} < {Ep("o", d.EndColumn)}");
    var row = newRowId is null ? string.Empty : $" WHERE {Col("n", "id")} = {newRowId}";
    return $"{Side("n")} JOIN {Side("o")} ON {string.Join(" AND ", on)}{row}";
}
```

(`Quote` is the public `AlvoSqlIdentifier.Quote` — double quotes, valid on both engines. Each side filters inside its
own single-table subquery, so the unqualified `Filter` resolves unambiguously; the two
`start < end` terms make an empty or inverted row overlap nothing — Review Focus 2.) SQLite:

```csharp
public ExclusionConstraintSql? ExclusionConstraint(ExclusionConstraintDefinition d) => new(
    Create: [Trigger(d, "_i", "INSERT"), Trigger(d, "_u", "UPDATE")],
    Drop: [$"DROP TRIGGER IF EXISTS {RenderColumn(d.Name + "_i")}", $"DROP TRIGGER IF EXISTS {RenderColumn(d.Name + "_u")}"],
    SurvivesTableRebuild: false);

private string Trigger(ExclusionConstraintDefinition d, string suffix, string verb) =>
    $"""
    CREATE TRIGGER IF NOT EXISTS {RenderColumn(d.Name + suffix)} AFTER {verb} ON {RenderColumn(d.Table)}
    BEGIN
        SELECT RAISE(ABORT, '{ExclusionRaisePrefix}{d.Name}')
        WHERE EXISTS (SELECT 1 FROM {ExclusionOverlapSql.Pairs(d, _fields.RenderEpochMicroseconds!, "NEW.\"id\"")});
    END
    """;
```

`ExclusionRaisePrefix = "alvo_exclusion:"` (an internal constant shared with the decoder in Task 7). Accept the goldens.
Run `SqliteExclusionDdlTests`, `PostgreSqlExclusionDdlTests`, `DialectContractTests` — PASS; accept the EF package's
public-API baseline (exactly `ExclusionConstraint`, the two records).

- [ ] **Step 5: Commit** — `feat(data): exclusion constraints in the descriptor and the dialects`.

## Task 6: Exclusions in the migrator — plan, rebuild survival, overlap scan, `btree_gist`

**Files:**
- Create: `src/MMLib.Alvo.Data.EntityFrameworkCore/Internal/ExclusionPlan.cs`, `src/MMLib.Alvo.Testing/Data/AlvoDataExclusionTests.cs`
  (+ `SqliteExclusionTests`, `PostgreSqlExclusionTests`), `test/MMLib.Alvo.Data.PostgreSql.Tests.Integration/PostgreSqlExclusionPrerequisiteTests.cs`
- Modify: `EfCoreSchemaMigrator.cs`, `IAlvoSqlDialect.cs` (internal DIM), `PostgreSqlSqlDialect.cs`

**Interfaces:**
- Consumes: `IndexNames.Exclusion`, `IndexFilterSql.For`, `IAlvoSqlDialect.ExclusionConstraint`, `ExclusionOverlapSql.Pairs`.
- Produces: `internal ValueTask<ExclusionPrerequisite> IAlvoSqlDialect.ExclusionPrerequisiteAsync(DbConnection, CancellationToken)`
  (default `ExclusionPrerequisite.Ready`), `internal sealed record ExclusionPrerequisite(string? Statement, bool Refused)`.

- [ ] **Step 1: Failing facts** (`AlvoDataExclusionTests`, shared `bookings` world from Task 5):
  - `Adding_an_exclusion_creates_it_and_removing_it_drops_it` (a raw out-of-band overlapping insert fails, then succeeds after removal).
  - `Changing_an_exclusion_s_where_replaces_it` (plan SQL drops the old name, creates the new one).
  - Review Focus 1 `An_exclusion_survives_a_later_apply_that_rebuilds_its_table` — apply the exclusion, then an apply
    adding a `validation` to `status` (a SQLite rebuild), then an out-of-band overlapping insert still fails.
  - `Adding_an_exclusion_over_overlapping_rows_is_refused_with_their_count` — three rows, two pairs overlapping: the plan
    (dry run included) throws at `/entities/bookings/indexes/0/exclude` with `2 pair(s) of 'bookings' rows overlap…`.
  - `A_declining_engine_refuses_an_exclusion_at_its_pointer` (dialect decorator answering `null`).
  - **AC (6)** `PostgreSqlExclusionPrerequisiteTests.Without_btree_gist_and_no_right_to_install_it_the_apply_is_refused_with_the_fix`
    — as the fixture's superuser: `CREATE DATABASE alvo_noext_<n>`, `CREATE ROLE alvo_noext_<n> LOGIN PASSWORD '…'`,
    `GRANT CONNECT`, and, connected to that database, `GRANT CREATE ON SCHEMA public`; a host on that role (no database
    `CREATE`) plans the exclusion → `DescriptorValidationException` at `/entities/bookings/indexes/0/exclude` with the
    btree_gist message and fix verbatim; nothing applied.
  - `PostgreSqlExclusionPrerequisiteTests.With_the_right_to_install_it_the_apply_installs_btree_gist_and_the_exclusion`
    (same, plus `GRANT CREATE ON DATABASE`): plan SQL starts with `CREATE EXTENSION IF NOT EXISTS btree_gist`; applied.
Run — FAIL.

- [ ] **Step 2: The plan.**

```csharp
// src/MMLib.Alvo.Data.EntityFrameworkCore/Internal/ExclusionPlan.cs
internal static class ExclusionPlan
{
    internal static (IReadOnlyList<string> Before, IReadOnlyList<string> After) Statements(
        SchemaModel current, SchemaModel desired, IAlvoSqlDialect dialect, IndexFilterSql filters, ISet<string> touchedTables)
    {
        var had = Definitions(current, filters).ToDictionary(d => d.Name);
        var wants = Definitions(desired, filters).ToDictionary(d => d.Name);
        var before = had.Keys.Except(wants.Keys).SelectMany(name => Sql(dialect, had[name]).Drop);
        var after = wants.Values
            .Where(d => !had.ContainsKey(d.Name) || Replayed(dialect, d, touchedTables))
            .SelectMany(d => Sql(dialect, d).Create);
        return ([.. before], [.. after]);
    }

    private static bool Replayed(IAlvoSqlDialect dialect, ExclusionConstraintDefinition d, ISet<string> touchedTables) =>
        !Sql(dialect, d).SurvivesTableRebuild && touchedTables.Contains(d.Table);

    private static ExclusionConstraintSql Sql(IAlvoSqlDialect dialect, ExclusionConstraintDefinition d) =>
        dialect.ExclusionConstraint(d) ?? throw new InvalidOperationException($"{dialect.GetType().Name} declined '{d.Name}' after planning admitted it.");

    internal static IEnumerable<ExclusionConstraintDefinition> Definitions(SchemaModel schema, IndexFilterSql filters) =>
        from entity in schema.Entities
        from index in entity.Indexes.Where(index => index.Exclude is not null)
        select new ExclusionConstraintDefinition(
            entity.Name, IndexNames.Exclusion(entity, index),
            [.. DescriptorModelBuilder.UniqueColumns(entity, index.Fields)],
            index.Exclude!.From, index.Exclude.To, entity.Fields.Single(f => f.Name == index.Exclude.From).Type,
            index.Where is null ? null : filters.For(entity, index.Where));
}
```

`touchedTables` = every table named by an EF operation in the residual (any `TableOperation`/`ColumnOperation`/index or
check operation), which is exactly the set SQLite may rebuild. `EfCoreSchemaMigrator.AssembleOperations` wraps the EF
operations as `SqlOperation`s `Before` + EF + `After`. When `desired` declares an exclusion and the dialect answers `null`,
throw the declining-dialect refusal at the first one's pointer. A dropped table's exclusion needs no drop (the table
takes it along): `Before` skips names whose table `desired` no longer has. A renamed table's (or column's) definition gets
a new name or new text, so the diff recreates it.

- [ ] **Step 3: The overlap scan and the prerequisite.** In `PlanAsync`'s counted path (beside
`RefuseRowsThatBreakNewChecksAsync`), for every **added** definition whose table already exists in `current`, run
`SELECT COUNT(*) FROM {ExclusionOverlapSql.Pairs(d, epoch, newRowId: null)}` and refuse a positive count with the
constants above (a `DbException` → the HX-11 *Uncountable* shape at the same pointer, logged). Then, when any
definition is added, `await _dialect.ExclusionPrerequisiteAsync(connection, ct)` — PostgreSQL:

```csharp
async ValueTask<ExclusionPrerequisite> IAlvoSqlDialect.ExclusionPrerequisiteAsync(DbConnection connection, CancellationToken ct)
{
    const string Probe = """
        SELECT EXISTS (SELECT 1 FROM pg_extension WHERE extname = 'btree_gist'),
               EXISTS (SELECT 1 FROM pg_available_extensions WHERE name = 'btree_gist'),
               has_database_privilege(current_database(), 'CREATE')
        """;
    var command = connection.CreateCommand();
    await using (command.ConfigureAwait(false))
    {
        command.CommandText = Probe;
        var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        await using (reader.ConfigureAwait(false))
        {
            await reader.ReadAsync(ct).ConfigureAwait(false);
            return (reader.GetBoolean(0), reader.GetBoolean(1) && reader.GetBoolean(2)) switch
            {
                (true, _) => ExclusionPrerequisite.Ready,
                (false, true) => new ExclusionPrerequisite("CREATE EXTENSION IF NOT EXISTS btree_gist", Refused: false),
                _ => new ExclusionPrerequisite(null, Refused: true),
            };
        }
    }
}
```

`Refused` → the btree_gist refusal at the first added exclusion's pointer; a `Statement` is prepended to the plan's SQL
(after the HX-11 lock-timeout statement). The uncounted planner path (`IUncountedSchemaPlanner`) runs the probe too —
three catalog reads, no table scan. Run `AlvoDataExclusionTests` (both), `PostgreSqlExclusionPrerequisiteTests` — PASS.

- [ ] **Step 4: Commit** — `feat(data): plan, scan and survive exclusion constraints; refuse without btree_gist`.

## Task 7: A write that overlaps — decode, 409 with a pointer, concurrent writers **[slice end]**

**Files:**
- Modify: `AlvoConstraintKind.cs` (append `Exclusion`), `AlvoConstraintViolationException.cs` (message),
  `SqliteSqlDialect.cs` (`1811` + prefix), `PostgreSqlSqlDialect.cs` (`23P01`), `ConstraintViolationTranslator.cs`,
  `EfAlvoData.cs` (D3 locks before insert, insert-many, update and replace saves), `ProblemResultFactory.cs`
  (`CodeFor`/`FixFor`/`ConflictViolations`), `src/MMLib.Alvo.Admin/Internal/AdminProblemFixes.cs`, `CHANGELOG.md`
- Create: `src/MMLib.Alvo.Data.EntityFrameworkCore/Internal/ExclusionLocks.cs`, `test/_shared/api/DataApiEngineTests.Exclusion.cs`,
  `test/_shared/api/descriptors/exclusion-bookings.alvo.json`
- Test: `AlvoDataExclusionTests` (Task 6's), `SqliteSqlDialect`/`PostgreSqlSqlDialect` decode tests

**Interfaces:**
- Produces: `AlvoConstraintKind.Exclusion`; `AlvoConstraintViolationException(Exclusion, [from])` or `[]` when the range
  start carries any `hidden` flag.

- [ ] **Step 1: Failing facts.** Decode: `SqliteSqlDialectTests.A_trigger_abort_tagged_as_an_exclusion_decodes_its_name`,
`…An_untagged_trigger_abort_is_not_a_constraint_violation`; `PostgreSqlSqlDialectTests.An_exclusion_violation_decodes_its_constraint_name`.
`AlvoDataExclusionTests`:
  - **AC (3)** `Concurrent_overlapping_bookings_admit_one_and_refuse_the_rest_with_the_exclusion_kind` — 20 creates of
    one room, pairwise overlapping, released together: exactly one stored; 19 `AlvoConstraintViolationException
    { Kind: Exclusion, Fields: ["starts_at"] }`; no other exception type (no `40P01`).
  - `Back_to_back_bookings_do_not_overlap` (`[9,10)` then `[10,11)`), `Other_rooms_and_other_tenants_do_not_collide`,
    `A_cancelled_booking_outside_the_where_does_not_block`, `A_patch_moving_a_booking_onto_another_is_refused`.
  - Review Focus 2 `An_empty_range_overlaps_nothing_on_either_engine`, `An_inverted_range_is_refused_at_its_end_field`
    (`Kind: Check`, `Fields: ["ends_at"]` — D4).
  - `An_overlap_on_a_hidden_range_names_no_field` (`starts_at` with `hidden: true` → `Fields: []`).
HTTP (`DataApiEngineTests.Exclusion.cs`, both engines): **AC (3)**
`Overlapping_bookings_are_409_at_the_range_pointer_under_concurrent_writers_on_every_engine` (10 concurrent POSTs →
one 201, nine 409 with `type` conflict and one violation `{pointer:"/starts_at", code:"exclusion"}`),
`An_inverted_booking_is_422_at_its_end_on_every_engine`. Run — FAIL.

- [ ] **Step 2: Decode and translate.** Append `Exclusion` (XML: "A row would hold a range overlapping another
record's on an index declaring `exclude`; fixed by choosing another range.") and its message. SQLite: extended code
`ConstraintTrigger = 1811` → `message.Contains(ExclusionRaisePrefix)` ? `new() { Kind = Exclusion, ConstraintName = name after the prefix }` : `null`
(also in the message-only fallback path); PostgreSQL: `ExclusionViolation = "23P01"` → `new() { Kind = Exclusion, ConstraintName = pg.ConstraintName }`.
`ConstraintViolationTranslator`: for `Exclusion`, find the applied entity's index with
`IndexNames.Exclusion(entity, index) == name`; the named field is `index.Exclude.From` unless in `written.Unnameable`
(Ruling X); an unresolvable name → no field (pointer `""`), never a 500. `ProblemResultFactory`: `CodeFor(Exclusion) => "exclusion"`,
`FixFor` per the constants, and `ConflictViolations` maps an `Exclusion` with fields to one violation at `/<from>` with the
409 message above. `AdminProblemFixes` gets the same fix text.

- [ ] **Step 3: D3 locks.**

```csharp
// src/MMLib.Alvo.Data.EntityFrameworkCore/Internal/ExclusionLocks.cs
internal static class ExclusionLocks
{
    internal static async Task TakeAsync(
        AlvoDataContext db, IAlvoSqlDialect dialect, EntitySchema schema, IEnumerable<IReadOnlyDictionary<string, object?>> rows,
        CancellationToken ct)
    {
        var keys = Keys(schema, rows).Order().ToList();
        if (keys.Count == 0 || dialect.TransactionLockStatement([.. keys.Select((_, i) => $"@xk{i}")], exclusive: true) is not { } sql)
        {
            return;
        }

        var command = db.Database.GetDbConnection().CreateCommand();
        await using (command.ConfigureAwait(false))
        {
            command.Transaction = db.Database.CurrentTransaction!.GetDbTransaction();
            command.CommandText = sql;
            for (var i = 0; i < keys.Count; i++)
            {
                RelationalSqlBatch.AddParameter(command, $"@xk{i}", keys[i]);
            }

            await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }
    }

    private static IEnumerable<long> Keys(EntitySchema schema, IEnumerable<IReadOnlyDictionary<string, object?>> rows) =>
        (from index in schema.Indexes.Where(index => index.Exclude is not null)
         from row in rows
         select Key(IndexNames.Exclusion(schema, index), DescriptorModelBuilder.UniqueColumns(schema, index.Fields), row)).Distinct();

    private static long Key(string name, IEnumerable<string> columns, IReadOnlyDictionary<string, object?> row) =>
        BinaryPrimitives.ReadInt64BigEndian(SHA256.HashData(Encoding.UTF8.GetBytes(
            name + "|" + string.Join("|", columns.Select(c => Convert.ToString(row.GetValueOrDefault(c), CultureInfo.InvariantCulture))))));
}
```

`EfAlvoData` calls it immediately before each `SaveChangesAsync` of a create, batch create, update (post-image values)
and replace on an entity with an exclusion; SQLite's `TransactionLockStatement` is `null`, so it is a no-op there.
Run every Step 1 class on both engines — PASS. Accept the Abstractions baseline (`AlvoConstraintKind.Exclusion`).

- [ ] **Step 4: CHANGELOG** — *Added*: "`indexes[].where` (partial indexes; a partial `unique` allows duplicates outside
it) and `indexes[].exclude` (no overlapping `[from, to)` ranges among rows with equal `fields`; 409 `exclusion` at the
range's pointer; PostgreSQL installs or requires `btree_gist`); `AlvoConstraintKind.Exclusion`."

- [ ] **Step 5: Commit, rings, ship.** Commit `feat(data): refuse overlapping writes 409 with a pointer on both engines`;
`scripts/test-ring1`; `alvo-plan-guard`; `alvo-pr-report`; push `hx/12b-partial-exclusion-indexes`;
`gh pr create --base main --label needs-deep-review` (`Part of #389`).

---

# Slice C — `hx/12c-integrity-surfaces` (branch from `main` after B merges)

## Task 8: Dashboard — *Sequence*, *Exclude overlaps* and *Only where* editors

**Files:**
- Modify: `src/MMLib.Alvo.Admin/Components/Schema/FieldFacets.Maintained.cs` (+ `.Notes.cs`), `FieldEditor.razor(.cs)`,
  `FieldBadges.cs`, `Indexes.razor`, `WorkingCopy.Indexes.cs`, `test/MMLib.Alvo.Admin.Tests.EndToEnd/IntegrityEditorScenarios.cs`
- Test: `test/MMLib.Alvo.Admin.Tests/Schema/{FieldFacetsSequenceTests,WorkingCopyIndexTests,IndexesEditorTests}.cs`

**Interfaces:**
- Produces: `WorkingCopy.AddIndex(string entity, IReadOnlyList<string> fields, bool unique, string? where = null, IndexExclusion? exclude = null)`
  (the existing three-argument call sites keep compiling); `FieldFacets.SequencePer/SequencePeriod/SequenceStart/SequenceFormat`.

- [ ] **Step 1: Failing bUnit / model tests.**
`FieldFacetsSequenceTests.A_sequence_round_trips_through_the_maintained_facets` (load → save leaves the JSON equal),
`…Choosing_sequence_on_a_string_field_requires_a_format`, `…The_format_is_checked_live_and_previews_the_next_number`
(the check is `SequenceSchema.IsValidFormat`, reached through Abstractions' existing grant to `MMLib.Alvo.Admin` — the
Admin has no `MMLib.Alvo` reference; the preview renders `{n}` as `1` and the date tokens from the clock), `…Editing_a_sequenced_field_s_description_keeps_its_sequence`;
`WorkingCopyIndexTests.An_index_with_where_and_exclude_round_trips`, `…Removing_an_exclusion_index_matches_it_by_its_exclude`
(`SameIndex` compares `Where`/`Exclude`); `IndexesEditorTests.Exclude_overlaps_offers_only_required_date_and_datetime_fields`,
`…Only_where_is_checked_live_against_the_entity_s_fields`. Run `dotnet test --project test/MMLib.Alvo.Admin.Tests` — FAIL.

- [ ] **Step 2: Implement.** *Value comes from* gains **Sequence** beside computed/rollup (per picker over required
fields of the allowed types, period select *none/year/month/day*, start number, format text with live check and a
next-number preview; the type stays integer or string). `Indexes.razor`'s add form gains **Only where** (a CEL text box
using the existing live `check_change`-style validation the rollup *Where* uses) and **Exclude overlaps** (from/to
pickers; choosing it hides *Unique*). The list row shows `where …` and `no overlap of from–to` badges. PASS.

- [ ] **Step 3: Admin e2e, filtered.** Add `IntegrityEditorSequenceScenarios.IntegrityEditor_a_sequence_is_saved_applied_and_numbers_a_record`
and `IntegrityEditorExclusionScenarios.IntegrityEditor_an_exclusion_with_where_is_saved_applied_and_refuses_an_overlap`
(over `BikeWorkshopWorld`: rentals `fleet_bike_id` + `[starts_at, due_at)`, where `status != 'cancelled'`; a second
overlapping rental through the Data screen shows the 409 fix text). Run `scripts/test-admin-e2e --filter IntegrityEditor` — PASS.

- [ ] **Step 4: Commit** — `feat(admin): sequence, exclude-overlaps and only-where editors`.

## Task 9: Skills, docs, architecture doc, CHANGELOG, spec amendment **[slice end]**

**Files:**
- Modify: `plugins/alvo/skills/alvo-descriptor-indexes/SKILL.md`, `plugins/alvo/skills/alvo-descriptor-field-types-and-formats/SKILL.md`,
  `website/src/content/docs/extending/integrity.mdx`, `docs/architecture/data-path.md`, `CHANGELOG.md`,
  `docs/superpowers/specs/2026-10-14-host-extensibility-design.md`
- Create: `website/src/snippets/extending/integrity/bookings.alvo.json`, `website/src/snippets/extending/integrity/BookingIntegrityTests.cs`
- Test: `test/MMLib.Alvo.Ai.Tests/EmbeddedSkillsTests.cs` (drift + size caps), `test/MMLib.Alvo.DocsSnippets.Tests`

- [ ] **Step 1: Skills as product text.** Indexes skill: a paragraph on `where` (partial; with `unique`, "unique among
the rows it accepts"; no `now()`, no hidden field) and on `exclude` (shape, half-open, 409, `btree_gist` on PostgreSQL,
cannot be `unique`), plus one drift-tested example region `<!-- example: exclude-overlapping-rentals -->` with its
`propose_change` and `{"valid": true, …}` answer. Field-types skill: `sequence` (integer vs formatted string, tokens,
`per`/`period`/`start`, read-only, never reused, refused on dynamic entities). Keep each file under its cap; run
`dotnet test --project test/MMLib.Alvo.Ai.Tests --filter-class '*EmbeddedSkillsTests'` and
`dotnet test --project test/MMLib.Alvo.Ai.Eval.Tests --filter-class '*SkillCasesTests'` — PASS.

- [ ] **Step 2: Executed snippet, failing then passing.** `bookings.alvo.json`: `rooms`, scoped `bookings` with
`number` (`sequence: { "per": "room", "format": "B-{yyyy}-{n:3}", "period": "year" }`), `starts_at`/`ends_at`,
`status`, index `{fields:["room"], exclude:{from:"starts_at",to:"ends_at"}, where:"status != 'cancelled'"}`, and a
partial unique `{fields:["room","external_ref"], unique:true, where:"status != 'cancelled'"}`. `BookingIntegrityTests`
(SQLite `AlvoTestHost`, `FakeTimeProvider` at 2026-11-06): first booking `B-2026-001`, back-to-back accepted, an overlap
→ `AlvoConstraintViolationException { Kind: Exclusion }`, a cancelled overlap accepted. Run
`dotnet test --project test/MMLib.Alvo.DocsSnippets.Tests` — PASS.

- [ ] **Step 3: Docs page.** `integrity.mdx` gains *Numbers that never skip: `sequence`*, *Unique among some rows:
`where`*, *No double booking: `exclude`* (each embedding the snippet regions, the 409 body, the btree_gist note and
D13's soft-delete caveat). `data-path.md` gains *Sequences are a counter row in the create's transaction* (D5–D7, the
PostgreSQL upsert wait, why not `CREATE SEQUENCE`) and *Exclusions: gist on PostgreSQL, triggers on SQLite* (D1–D3,
rebuild replay, the scan, the prerequisite).

- [ ] **Step 4: CHANGELOG + spec.** CHANGELOG *Added*: the dashboard editors and docs. In the spec, after §7 *HX-12*, add
**HX-12 settled details**: D1–D14 verbatim from this plan, the three-slice delivery, and the amendments — §5 row `12`
gains `SequenceAllocationStatement`, `ExclusionConstraintDefinition`, `ExclusionConstraintSql`, the schema/descriptor
members; Ruling 17's `RenderConstraint` stays internal (D9). File follow-up issues (D13 soft-delete awareness; a
`timeZone` for `period`, D6) with `gh issue create`, linked to #370.

- [ ] **Step 5: Commit, rings, ship.** Commit `docs(integrity): sequence, partial and exclusion indexes`;
`scripts/test-ring1`; `alvo-plan-guard`; `alvo-pr-report`; push `hx/12c-integrity-surfaces`;
`gh pr create --base main --label needs-deep-review` with `Closes #389`. Then report to the coordinator that the
stack-level `alvo-security-core-review` + `/security-review` (labelled substitute) is due.

---

## Acceptance criteria → tests

| AC | Test (class.method) | Task |
|---|---|---|
| (1) 100 concurrent creates → 1..100, no gap/duplicate, both engines | `AlvoDataSequenceTests.A_hundred_concurrent_creates_number_one_to_a_hundred_without_gap_or_duplicate` (`SqliteSequenceTests`, `PostgreSqlSequenceTests`) | 2 |
| (2) A rolled-back create consumes no number | `AlvoDataSequenceTests.A_rolled_back_create_consumes_no_number`, `…A_create_rolled_back_with_its_unit_consumes_no_number` (both engines) | 2 |
| (3) Overlapping bookings → 409 with pointer under concurrent writers, both engines | `AlvoDataExclusionTests.Concurrent_overlapping_bookings_admit_one_and_refuse_the_rest_with_the_exclusion_kind`; `DataApiEngineTests.Overlapping_bookings_are_409_at_the_range_pointer_under_concurrent_writers_on_every_engine` | 7 |
| (4) A partial unique index allows duplicates outside `where` | `AlvoDataPartialIndexTests.A_partial_unique_index_allows_duplicates_outside_its_where` (both engines) | 4 |
| (5) The T-SQL double renders the shape | `TSqlSqlDialectContractTests.The_TSql_double_allocates_a_sequence_number_with_one_MERGE_under_HOLDLOCK` (2); `…The_TSql_double_declines_an_exclusion` (5) | 2, 5 |
| (6) No `btree_gist` → apply refusal with fix | `PostgreSqlExclusionPrerequisiteTests.Without_btree_gist_and_no_right_to_install_it_the_apply_is_refused_with_the_fix` | 6 |
