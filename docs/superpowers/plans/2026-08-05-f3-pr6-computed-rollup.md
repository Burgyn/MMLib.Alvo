# F3 PR6 — computed & rollup fields (#21)

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development
> (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use
> checkbox (`- [ ]`) syntax for tracking.

**Goal:** Honour `field.computed` as a **stored generated column the database maintains** and
`field.rollup` as a **lock-then-recompute inside the child write's own transaction**, enforce the
computed/rollup/hook ladder at apply, and delete the two `UnhonouredFeatures` entries that refuse
them today (closing #21).

**Architecture:** `computed` is DDL. The CEL expression is compiled once at apply for the
`Computed` profile, rendered to SQL through the existing scalar `IPredicateRenderer` entry point and
the driver's `IFieldSqlRenderer`, and becomes a stored generated column — so the value is
unbypassable by construction: the engine itself refuses a write to it. `rollup` is a write-path
concern, never DDL and never a trigger: inside the child write's own transaction the parent row's
write lock is taken through `IAlvoSqlDialect.RowLockClause` **before** a single `UPDATE parent SET
<rollup> = (SELECT <op>(<field>) FROM <child> WHERE <fk> = @parent)` recomputes the aggregate from
scratch.

**Tech Stack:** .NET 10, EF Core 10, xUnit v3 on Microsoft.Testing.Platform, Shouldly, Verify,
Testcontainers (PostgreSQL 16).

## Global Constraints

- Design source of truth:
  `docs/superpowers/specs/2026-08-04-f3-pr6-computed-rollup-design.md`. Measurements:
  `docs/superpowers/specs/evidence/2026-08-04-f3-pr6-computed-rollup/spike.txt` — **read its third
  pass before writing any test**, because it is the pass measured against the product's own migrator
  rather than against raw SQL, and it moves three of this plan's decisions.
- **The generated-column fact asserts the ENGINE REFUSING A WRITE**, never the value read back. A
  value read back is also produced by an ordinary column somebody happened to fill in.
- **The add-a-computed-field fact WRITES A ROW FIRST.** On an empty table SQLite accepts
  `ALTER TABLE … ADD COLUMN … STORED`, so a fact on a fresh fixture passes while the only case that
  matters fails. This is the single most likely way to ship a broken migration with a green suite.
- **The rollup race fact runs on PostgreSQL and widens its window.** SQLite admits one writer at a
  time, which makes the lost update structurally impossible there, so a SQLite-only fact proves
  nothing; and without the delay the PostgreSQL lost update measured 40 of 40 and looked correct.
- Engine-agnostic behaviour goes in a **shared contract suite** under `src/MMLib.Alvo.Testing/Data/`
  so both engines inherit it — following `AlvoDataConstraintTests`.
- ring0 (`ALVO_CONFIGURATION=Release scripts/test-ring0`) after every step; ring2 and
  `scripts/test-e2e` before finishing (the write path and the schema DDL both change). Assert the
  literal string `Build succeeded` before reading any test result.
- MTP, not VSTest: `dotnet test --project X --configuration Release -- --filter-class '*Y*'`.
- `.gitattributes` pins `*.cs` to CRLF — verify a mutation's edit landed with `git diff`, never with
  an LF search string. `grep` is aliased to `ugrep`; use `command grep`.
- State the mutation that proves each significant fact discriminates, and **run it**.
- Commit after each task, and commit BEFORE mutating.

---

## Deviations from the design, all forced by the third-pass measurements

Recorded here rather than discovered later. Each is a place this plan does **not** do what the
design's prose says, with the measurement that made it wrong.

**Dev-1 — the dialect needs TWO members, not one.** The design's D1 asks for a single
`GeneratedColumnDefinition` returning the column definition, and for the core never to spell the
DDL. Q7 measured that the SQLite table rebuild can only come from EF's own SQLite migrations
generator — it needs every column's DDL, which only EF's type mapping knows — so on that engine EF
spells the generated column, while the one-statement in-place add on PostgreSQL is spelled by the
dialect. The bit that selects between them ("can this engine add a stored generated column to a
table that already holds rows") cannot be carried by a member whose non-null answer means "here is
the definition": SQLite has a perfectly good definition and still cannot add it in place. A second
member, `GeneratedColumnAddRequiresTableRebuild`, carries it. Both are default-implemented, so no
implementor breaks.

**Dev-2 — SQLite always rebuilds, even when the table is empty.** §0 principle 3 requires one
observable outcome, and a per-engine branch that also depends on the table's row count is a
migration whose shape depends on production data. Measured cost: one extra table copy on a
create-then-add sequence.

**Dev-3 — `FOR NO KEY UPDATE`, not `FOR UPDATE`.** The design names `FOR UPDATE`. The rollup write
provably never touches the parent's key, and `PreImageMutation.Update` already means exactly that on
this dialect (`RowLockClause`'s own remarks explain why the weaker mode is the right one: it does not
block the `FOR KEY SHARE` another table's foreign-key check takes). It still conflicts with itself,
so two concurrent recomputes serialise — which is the entire correctness argument. Using
`PreImageMutation.Delete` to obtain the literal words `FOR UPDATE` would serialise unrelated inserts
against the parent for no benefit.

**Dev-4 — a `computed` expression may not carry a bound value.** Q9: the scalar renderer routes
every non-boolean literal through its parameter bag, and DDL cannot carry a bind parameter. A
`computed` whose render produces parameters is refused at apply with a structured error. Every
`computed` example in the sources — `unit_price * amount`, `net_total + vat_total` — is field-only
arithmetic and renders clean, and `baas-analyza:1358` puts a *contextual* constant (a VAT rate) in a
before-hook precisely because it does not belong in `computed`.

**Dev-5 — `rollup.where` is refused, not implemented.** The design summarises the frozen shape as
`{ from, op, field? }`. The frozen schema also declares `via` (the FK field, for a child with more
than one ref to the parent) and `where` (a CEL filter on child records). `via` is implemented — it is
the only way to disambiguate `follows.follower` from `follows.followee`. `where` is refused as an
unhonoured slot, because ignoring it aggregates every child instead of the declared subset: a
silently wrong stored number, which is the exact failure `rollup` itself is refused for today.

**Dev-6 — the ladder's before-hook rung is not assemblable in this branch.** The design's
acceptance 5 says "PR5b-1 landed the before-hook rung, so this is now assemblable". PR5b-1 is
**PR #160, still open**; this branch is cut from `origin/main`, which has no before-hooks —
`UnhonouredFeatures` still carries all three `before*` entries. Task 8 therefore assembles
computed → rollup → computed and pins the missing rung as an explicit, named gap rather than
pretending to it. Merging #160 into this branch to close it is refused: it would put an unmerged
PR's ~40 files into this diff and take it past the 100-file review ceiling.

---

## File Structure

| File | Responsibility |
|---|---|
| `src/MMLib.Alvo.Data.EntityFrameworkCore/IAlvoSqlDialect.cs` | the two new default members |
| `src/MMLib.Alvo.Data.PostgreSql/PostgreSqlSqlDialect.cs` | `GENERATED ALWAYS AS (…) STORED`, adds in place |
| `src/MMLib.Alvo.Data.Sqlite/SqliteSqlDialect.cs` | same spelling, but requires the rebuild |
| `src/MMLib.Alvo.Testing.EntityFrameworkCore/TSqlSqlDialect.cs` | `AS (…) PERSISTED` — the third engine's rehearsal |
| `src/MMLib.Alvo.Testing.EntityFrameworkCore/AlvoSqlDialectContractTests.cs` | the port's generic obligations |
| `src/MMLib.Alvo.Abstractions/Schema/RollupSchema.cs` (new) | the applied-schema shape of one rollup |
| `src/MMLib.Alvo.Abstractions/Schema/FieldSchema.cs` | `Rollup` |
| `src/MMLib.Alvo/Descriptor/DescriptorToSchemaMapper.cs` | revive `ComputedExpression`, map `Rollup`, enforce the ladder |
| `src/MMLib.Alvo/Descriptor/Internal/UnhonouredFeatures.cs` | delete `computed`/`rollup`; add the `rollup.where` slot |
| `src/MMLib.Alvo.Data.EntityFrameworkCore/Internal/ComputedColumnSql.cs` (new) | CEL → the SQL a generated column carries |
| `src/MMLib.Alvo.Data.EntityFrameworkCore/Internal/DescriptorModelBuilder.cs` | mark the property computed-stored |
| `src/MMLib.Alvo.Data.EntityFrameworkCore/Internal/AlvoDataContext.cs` | store-generated, so no write includes it |
| `src/MMLib.Alvo.Data.EntityFrameworkCore/EfCoreSchemaMigrator.cs` | the gate, and the two-hop plan |
| `src/MMLib.Alvo.Data.EntityFrameworkCore/Internal/RollupRecompute.cs` (new) | lock-then-recompute, one statement each |
| `src/MMLib.Alvo.Data.EntityFrameworkCore/Internal/EfAlvoData.cs` | call it inside the child write's transaction |
| `src/MMLib.Alvo.Testing/Data/AlvoDataComputedRollupTests.cs` (new) | the shared, both-engine suite |
| `test/MMLib.Alvo.Data.PostgreSql.Tests.Integration/PostgreSqlRollupRaceTests.cs` (new) | the race, widened |

---

## Task 1: the dialect learns how this engine spells a stored generated column

**Files:**
- Modify: `src/MMLib.Alvo.Data.EntityFrameworkCore/IAlvoSqlDialect.cs`
- Modify: `src/MMLib.Alvo.Data.PostgreSql/PostgreSqlSqlDialect.cs`, `src/MMLib.Alvo.Data.Sqlite/SqliteSqlDialect.cs`
- Modify: `src/MMLib.Alvo.Testing.EntityFrameworkCore/TSqlSqlDialect.cs`, `AlvoSqlDialectContractTests.cs`
- Test: `test/MMLib.Alvo.Data.EntityFrameworkCore.Tests/TSqlDialectSeamTests.cs`, `DialectContractTests.cs`

**Interfaces:**
- Produces: `IAlvoSqlDialect.GeneratedColumnDefinition(string, string, string)` and
  `IAlvoSqlDialect.GeneratedColumnAddRequiresTableRebuild`.

- [ ] **Step 1: Write the failing facts.** In `AlvoSqlDialectContractTests` (generic, so every
      dialect inherits them):

```csharp
[Fact]
public void A_generated_column_definition_names_the_column_the_type_and_the_expression()
{
    var definition = Dialect.GeneratedColumnDefinition("line_total", "numeric(18,2)", "(a * b)");

    definition.ShouldNotBeNull("this dialect ships a generated-column spelling");
    definition.ShouldContain(Dialect.RenderColumn("line_total"));
    definition.ShouldContain("numeric(18,2)");
    definition.ShouldContain("(a * b)");
}
```

      and in `DialectContractTests` (which drives `TestSqlDialect`, an implementor that adds
      nothing), the fact that the default is a refusal rather than a guess:

```csharp
[Fact]
public void A_dialect_that_implements_nothing_answers_null_rather_than_guessing_a_spelling() =>
    new TestSqlDialect().GeneratedColumnDefinition("t", "int", "(1)").ShouldBeNull();
```

- [ ] **Step 2: Run them and watch them fail** — the members do not compile.

Run: `dotnet test --project test/MMLib.Alvo.Data.EntityFrameworkCore.Tests --configuration Release -- --filter-class '*DialectContractTests*'`

- [ ] **Step 3: Add both members, defaulted, with the reasoning in their remarks.**

```csharp
    /// <summary>
    /// The column definition this engine spells for a <b>stored</b> generated column — the mechanism
    /// <c>field.computed</c> is honoured by — or <see langword="null"/> when the engine cannot express
    /// one, in which case the migrator refuses the field and names the engine.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Stored, never virtual, and that is a portability decision rather than a default.</b> SQLite
    /// accepts <c>VIRTUAL</c> where it refuses <c>STORED</c> (spike Q1), and emitting it there would make
    /// the same descriptor produce a column PostgreSQL can index and filter and SQLite cannot — §0
    /// principle 3's exact failure mode, silent rather than loud.
    /// </para>
    /// <para>
    /// A <b>default interface member</b>, like <see cref="RowWindowClause"/>: adding it breaks no existing
    /// implementation. The default is <see langword="null"/> rather than a spelling, because unlike a row
    /// window there is no majority spelling to inherit — Azure SQL says
    /// <c>AS (&lt;expr&gt;) PERSISTED</c> — and a wrong guess would be a column the engine rejects at
    /// migration time or, worse, an ordinary column nothing maintains.
    /// </para>
    /// <para>
    /// <b>Return grammar.</b> One column definition, as it appears inside <c>CREATE TABLE</c>'s column list
    /// or after <c>ADD COLUMN</c>: the quoted column name, the store type, and the generation clause. No
    /// leading or trailing comma, no <c>ADD COLUMN</c> keyword, no terminator.
    /// </para>
    /// <para>
    /// <paramref name="renderedExpression"/> reaches the SQL text unparameterized because DDL has no
    /// bind-parameter form. That is safe only because it comes from
    /// <see cref="MMLib.Alvo.Expressions.IPredicateRenderer"/>'s scalar entry point over a
    /// <b>compiled</b> CEL AST — never from a descriptor string spliced in, which is what #20 removed as
    /// an arbitrary-DDL-injection vector. A dialect must never be handed one assembled from caller input.
    /// </para>
    /// </remarks>
    /// <param name="columnName">The column's name, to be quoted by this dialect.</param>
    /// <param name="storeType">The column's EF-resolved store type, exactly as this provider spells it.</param>
    /// <param name="renderedExpression">The already-rendered, already-parenthesised SQL scalar expression.</param>
    string? GeneratedColumnDefinition(string columnName, string storeType, string renderedExpression) => null;

    /// <summary>
    /// Whether adding a stored generated column to a table that <b>already holds rows</b> requires
    /// rebuilding the table on this engine, rather than one <c>ALTER TABLE … ADD COLUMN</c>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Why this is a second member and not a <see langword="null"/> from
    /// <see cref="GeneratedColumnDefinition"/>.</b> The two questions come apart on a shipped engine:
    /// SQLite has a perfectly good spelling and still refuses
    /// <c>ALTER TABLE … ADD COLUMN … STORED</c> the moment the table is non-empty (<c>cannot add a
    /// STORED column</c>, measured against the bundled provider), while PostgreSQL accepts the same add
    /// and backfills every existing row. Folding the second answer into the first would force SQLite to
    /// deny it can express a generated column at all.
    /// </para>
    /// <para>
    /// <b>It is not asked per table state.</b> A dialect answering <see langword="true"/> rebuilds
    /// whether the table holds rows or not: a migration whose shape depends on how much production data
    /// exists is a migration that was never tested in the shape it will run in.
    /// </para>
    /// </remarks>
    bool GeneratedColumnAddRequiresTableRebuild => false;
```

- [ ] **Step 4: implement all three dialects.**

```csharp
// PostgreSqlSqlDialect — the engine whose spelling the design names, verbatim.
public string? GeneratedColumnDefinition(string columnName, string storeType, string renderedExpression)
{
    ArgumentException.ThrowIfNullOrWhiteSpace(columnName);
    ArgumentException.ThrowIfNullOrWhiteSpace(storeType);
    ArgumentException.ThrowIfNullOrWhiteSpace(renderedExpression);

    return $"{RenderColumn(columnName)} {storeType} GENERATED ALWAYS AS {Parenthesised(renderedExpression)} STORED";
}
```

      SQLite's is the same string — `GENERATED ALWAYS` is optional there but spelling it keeps the
      two engines' DDL comparable by eye — plus
      `public bool GeneratedColumnAddRequiresTableRebuild => true;` carrying the measurement in its
      own remarks. `TSqlSqlDialect` answers
      `$"{RenderColumn(columnName)} AS {Parenthesised(renderedExpression)} PERSISTED"` (T-SQL infers
      the type and rejects one being named), which is the rehearsal that the seam is sufficient for
      the third engine §0 principle 3 names.

- [ ] **Step 5: Run them and watch them pass.** ring0.

- [ ] **Step 6: Mutation that proves the facts discriminate** — delete ` STORED` from
      `SqliteSqlDialect.GeneratedColumnDefinition`. The contract fact must go red; if the assertion
      only looked for the column name it would not, which is why the expression and the store type
      are asserted too. Restore, verify with `git diff` (CRLF).

- [ ] **Step 7: Commit** — `feat(data): let a dialect spell a stored generated column`

---

## Task 2: `rollup` reaches the applied schema

**Files:**
- Create: `src/MMLib.Alvo.Abstractions/Schema/RollupSchema.cs`
- Modify: `src/MMLib.Alvo.Abstractions/Schema/FieldSchema.cs`
- Test: `test/MMLib.Alvo.Abstractions.Tests/Schema/RollupSchemaTests.cs`
- Baseline: `test/MMLib.Alvo.Abstractions.Tests/PublicApi.MMLib.Alvo.Abstractions.verified.txt` moves

**Interfaces:**
- Produces: `RollupSchema { string From; RollupOperation Op; string? Field; string Via; }`,
  `enum RollupOperation { Sum, Count, Avg, Min, Max }`, `FieldSchema.Rollup`.

- [ ] **Step 1: the facts.** `Via` is **non-nullable on the applied schema** even though the
      descriptor's `via` is optional: the mapper resolves it there, once, against the child's ref
      fields, so no layer below has to re-derive which foreign key a rollup follows. A fact pins
      that — `RollupSchema` cannot be constructed without a `Via`.

- [ ] **Step 2–4: red, implement, green.** `RollupOperation` is a separate enum from the
      descriptor's `RollupOp` for the reason `Schema.FieldType` is separate from
      `Descriptor.FieldType`: the applied schema is the frozen artifact a provider reads, and it must
      not move when a descriptor enum gains a member.

- [ ] **Step 5:** the public-API baseline moves. Framework-written — never hand-edited; run the
      suite so the tool rewrites it, then dispatch `alvo-snapshot-judge`.

- [ ] **Step 6: Commit** — `feat(schema): carry a rollup on the applied field schema`

---

## Task 3: the ladder is enforced at apply

**Files:**
- Modify: `src/MMLib.Alvo/Descriptor/DescriptorToSchemaMapper.cs`
- Modify: `src/MMLib.Alvo/Descriptor/Internal/UnhonouredFeatures.cs` (add the `rollup.where` slot)
- Test: `test/MMLib.Alvo.Tests/Descriptor/RollupLadderTests.cs` (new)

Enforced here, not documented, because every one of these is a **silently wrong stored number** if
it is allowed through — the failure class `rollup`'s own refusal text names.

- [ ] **Step 1: one fact per rung, each asserting the message names what to do.**

```csharp
[Fact]
public void A_field_declaring_both_computed_and_rollup_is_refused()
{
    // The two mechanisms disagree about who owns the value: the engine maintains a generated
    // column, the framework maintains a rollup. Whichever won, the other is a lie.
    var refused = Should.Throw<AlvoDescriptorException>(() => Map(FieldWith(computed: "a * b", rollup: Sum("items", "total"))));
    refused.Message.ShouldContain("both 'computed' and 'rollup'");
}

[Fact]
public void A_rollup_whose_from_entity_does_not_reference_this_one_is_refused()
{
    var refused = Should.Throw<AlvoDescriptorException>(() => Map(InvoiceWith(Sum("payments", "amount"))));
    refused.Message.ShouldContain("payments");
    refused.Message.ShouldContain("does not reference");
}

[Fact]
public void A_rollup_over_a_child_with_two_refs_to_this_parent_is_refused_unless_via_names_one()
{
    var refused = Should.Throw<AlvoDescriptorException>(() => Map(PersonWith(Count("follows"))));
    refused.Message.ShouldContain("'via'");
}

[Fact]
public void A_rollup_naming_via_that_is_not_a_ref_to_this_parent_is_refused() { … }

[Fact]
public void A_rollup_op_other_than_count_without_a_field_is_refused() { … }

[Fact]
public void A_rollup_where_filter_is_refused_rather_than_ignored()
{
    // Ignoring it aggregates EVERY child instead of the declared subset — a stored number that
    // looks like data and is not, which is what 'rollup' itself is refused for today.
    var refused = Should.Throw<AlvoDescriptorException>(() => Map(InvoiceWith(Sum("items", "total", where: "status == 'open'"))));
    refused.Message.ShouldContain("aggregates every");
}
```

- [ ] **Step 2: Run, watch all six fail.**

- [ ] **Step 3: Implement** in a `RollupResolver` the mapper calls, short single-purpose methods:
      `EnsureNotAlsoComputed`, `ResolveVia` (which both refuses and returns the resolved FK name so
      the check and the resolution cannot drift), `EnsureFieldPresentForOp`.

- [ ] **Step 4: Run, watch all six pass.** ring0.

- [ ] **Step 5: Mutation** — delete `EnsureNotAlsoComputed`'s throw. Its fact must go red. Repeat
      for `ResolveVia`'s zero-ref arm: a descriptor whose `from` entity does not reference the parent
      must not fall through to "no via, pick the only ref" and crash later with an EF error naming a
      column.

- [ ] **Step 6: Commit** — `feat(descriptor): enforce the computed/rollup ladder at apply`

---

## Task 4: `computed` becomes a stored generated column

**Files:**
- Create: `src/MMLib.Alvo.Data.EntityFrameworkCore/Internal/ComputedColumnSql.cs`
- Modify: `DescriptorToSchemaMapper.cs` (set `ComputedExpression`), `DescriptorModelBuilder.cs`,
  `Internal/AlvoDataContext.cs`, `EfCoreSchemaMigrator.cs`, `AlvoEfCoreProvider.cs`
- Test: `src/MMLib.Alvo.Testing/Data/AlvoDataComputedRollupTests.cs` (new, shared),
  `src/MMLib.Alvo.Testing/Migrations/SchemaSqlSnapshotTests.cs` (one new golden case per engine)

**The DDL is never spelled by the core, and the two authorities are split by path, not duplicated**
(spike Q7, Dev-1): `CREATE TABLE` and the SQLite rebuild come from EF's per-provider migrations
generator, driven by `HasComputedColumnSql(<rendered>, stored: true)`; the one-statement in-place add
comes from `IAlvoSqlDialect.GeneratedColumnDefinition`. Task 5 owns the add; this task owns the
create.

- [ ] **Step 1: the facts.** The load-bearing one asserts the **engine refusing a write**:

```csharp
/// <summary>
/// A computed field is maintained BY THE DATABASE, so no write path can set it — asserted by the
/// engine's own refusal, never by reading the value back. A value read back is equally consistent
/// with an ordinary column somebody happened to fill in correctly, which is exactly the state this
/// build shipped before #21.
/// </summary>
[Fact]
public async Task A_computed_field_cannot_be_written_through_the_port()
{
    var data = await CreateAsync(LadderSchema, LadderDescriptor);
    var invoice = await CreateInvoiceAsync(data);

    await Should.ThrowAsync<AlvoAuthorizationException>(() => data.CreateAsync(
        "invoice_items",
        new Dictionary<string, object?> { ["invoice"] = invoice, ["unit_price"] = 3m, ["amount"] = 2, ["line_total"] = 999m },
        Caller, cancellationToken: Ct));
}

[Fact]
public async Task A_computed_field_is_the_expression_over_the_row_the_write_stored()
{
    var data = await CreateAsync(LadderSchema, LadderDescriptor);
    var stored = await CreateItemAsync(data, unitPrice: 2.5m, amount: 4);

    stored["line_total"].ShouldBe(10.0m);
}

[Fact]
public async Task An_update_to_a_source_field_moves_the_computed_value_with_it() { … }
```

      Plus one golden SQL case per engine in `SchemaSqlSnapshotTests`
      (`Create_entity_with_a_computed_field_sql_is_stable`), which is the EF-version drift guard: a
      provider bump that stops emitting the generation clause breaks a snapshot instead of shipping
      an ordinary column.

- [ ] **Step 2: Run them, watch them fail** — the mapper still drops `computed`.

- [ ] **Step 3: Implement, in four small pieces.**

  1. `DescriptorToSchemaMapper.MapField` sets `ComputedExpression = f.Computed` (the **CEL source**;
     the applied schema stays engine-agnostic, so the SQL is rendered per provider, never persisted).
  2. `ComputedColumnSql` — the one place CEL becomes a generated column's SQL:

```csharp
/// <summary>
/// Renders one <c>computed</c> field's CEL into the SQL a stored generated column carries, for THIS
/// driver — the only place that translation happens.
/// </summary>
/// <remarks>
/// <para>
/// <b>Compiled, never spliced.</b> #20 removed a raw descriptor-string-to-DDL splice as an
/// arbitrary-DDL-injection vector, and this is what revives the feature without reviving the vector:
/// the string that reaches DDL is produced by <see cref="IPredicateRenderer"/> from a CEL AST the
/// compiler accepted for the <see cref="CelProfile.Computed"/> profile, so it can only contain field
/// references this entity declares, arithmetic, and <c>CASE WHEN</c>.
/// </para>
/// <para>
/// <b>A bound value is refused rather than inlined (spike Q9).</b> The scalar renderer routes every
/// non-boolean literal through its parameter bag, and DDL has no bind-parameter form. Inlining one
/// here would put literal formatting — decimal separators, string quoting and escaping, all
/// dialect-specific — in the shared core, which is the one thing this seam exists to prevent. So the
/// field is refused, naming the constant. Every <c>computed</c> example the sources give is
/// field-only arithmetic, and <c>baas-analyza:1358</c> deliberately puts a contextual constant (a VAT
/// rate) in a before-hook instead.
/// </para>
/// </remarks>
internal static string For(EntitySchema entity, FieldSchema field, ICelCompiler compiler, IPredicateRenderer predicates, IFieldSqlRenderer fields)
```

  3. `DescriptorModelBuilder.ConfigureField` calls
     `property.HasComputedColumnSql(sql, stored: true)`, so EF's own provider generator spells the
     DDL — measured to produce `numeric(18,2) GENERATED ALWAYS AS (…) STORED` on PostgreSQL and the
     legal short form `AS (…) STORED` on SQLite.
  4. `AlvoDataContext.ConfigureField` marks it `ValueGeneratedOnAddOrUpdate()`. **This is not
     cosmetic:** without it EF includes the column in the property-bag `INSERT` and both engines
     refuse the statement outright (`cannot INSERT into generated column`), so every create on an
     entity with a computed field would fail. It needs no SQL, which is why the runtime context
     needs no renderer.

  `EfCoreSchemaMigrator` gains the dialect and the renderer trio, and refuses a computed field whose
  dialect answers `null` with a structured error naming the engine — the design's D1 gate. The
  renderers are resolved with `GetService`, not `GetRequiredService`: `UseSqlite` can be attached to
  a bare builder with no `AddAlvo()`, and the refusal for that case must name the missing
  registration rather than throw an `InvalidOperationException` about a service.

- [ ] **Step 4: Run, watch them pass on both engines.** The two golden snapshots are new files, so
      review them by eye before accepting: the point is that the generation clause is *there*.

- [ ] **Step 5: Mutation** — change `HasComputedColumnSql(sql, stored: true)` to `stored: false`.
      On PostgreSQL that is a syntax error and every fact goes red; on SQLite it is legal
      (`VIRTUAL`) and the *write-refusal* fact must still go red, because a virtual column is
      equally read-only. So add the mutation that actually discriminates stored from virtual: drop
      `ValueGeneratedOnAddOrUpdate()` from `AlvoDataContext` — every create on the ladder entity must
      go red with the engine's own refusal.

- [ ] **Step 6: Commit** — `feat(data): honour computed as a stored generated column`

---

## Task 5: adding a computed field to an entity that already holds a row

**Files:**
- Modify: `src/MMLib.Alvo.Data.EntityFrameworkCore/EfCoreSchemaMigrator.cs`
- Test: `src/MMLib.Alvo.Testing/Migrations/…` shared migrator fact, inherited by both engines

**This is the task the spike exists for.** On an empty table SQLite accepts
`ALTER TABLE … ADD COLUMN … STORED`; on a table holding one row it refuses with `cannot add a STORED
column`. A fact built on a fresh fixture therefore passes while the production case — a deployed
entity that already has data — fails.

- [ ] **Step 1: the fact, and the row comes first.**

```csharp
/// <summary>
/// Adding a computed field to an entity that ALREADY HOLDS A ROW succeeds, and the existing row gets
/// the value. The row is written BEFORE the migration deliberately: on an EMPTY table SQLite accepts
/// `ALTER TABLE … ADD COLUMN … STORED`, so the same fact over a fresh fixture is green on both
/// engines while the only case that matters is broken on one of them.
/// </summary>
[Fact]
public async Task A_computed_field_can_be_added_to_an_entity_that_already_holds_a_row()
{
    await ApplyAsync(Model(Items(computed: false)));
    await InsertItemAsync(unitPrice: 3m, amount: 2);          // FIRST. Not a detail.

    await ApplyAsync(Model(Items(computed: true)));

    (await ReadLineTotalAsync()).ShouldBe(6m, "the engine backfilled the existing row");
    await Should.ThrowAsync<Exception>(() => WriteLineTotalAsync(999m));
}
```

- [ ] **Step 2: Run it. It must fail ON SQLITE ONLY**, with `cannot add a STORED column`. If it
      passes on SQLite, the fixture's table was empty — fix the fact, not the product.

- [ ] **Step 3: Implement the two-hop plan** in `PlanAsync`, for a dialect that answers
      `GeneratedColumnAddRequiresTableRebuild`:

```csharp
/// <summary>
/// The plan for a change that adds a stored generated column to an entity that already exists —
/// two diffs rather than one, on the engines that need it.
/// </summary>
/// <remarks>
/// <para>
/// <b>The documented create-new / copy / drop / rename sequence is not written here; it is
/// reached.</b> EF Core's SQLite generator already implements it, and it triggers on an
/// <c>AlterColumnOperation</c> — never on an <c>AddColumnOperation</c>, which is why the single-hop
/// diff emits the bare <c>ADD COLUMN … STORED</c> the engine refuses. Diffing
/// current → (desired with the column PLAIN) → desired therefore yields a legal
/// <c>ALTER TABLE … ADD</c> followed by the rebuild, and the rebuild's own
/// <c>INSERT … SELECT</c> correctly omits the generated column (measured, spike Q7). Writing the
/// sequence in this class instead would put every column's DDL — the type map, the constraints, the
/// index set — in the shared core, which is what <see cref="IAlvoSqlDialect"/> exists to prevent.
/// </para>
/// <para>
/// <b>Both statements are valid against the database at the point they run</b>, which is why this is
/// two hops rather than one diff against a fictional current. Hop one physically adds the plain
/// column; hop two rebuilds. A single diff against a current that pretends the column exists happens
/// to work today only because EF omits generated columns from the copy — a coincidence, and one an EF
/// bump could take away silently.
/// </para>
/// <para>
/// PostgreSQL takes neither hop: it accepts the one-statement add and backfills every existing row,
/// so its plan is unchanged and the added statement is the dialect's own
/// <see cref="IAlvoSqlDialect.GeneratedColumnDefinition"/>.
/// </para>
/// </remarks>
```

- [ ] **Step 4: Run it, green on both engines.** ring0, then the SQLite integration leg.

- [ ] **Step 5: THE MUTATION THIS TASK TURNS ON** — make
      `SqliteSqlDialect.GeneratedColumnAddRequiresTableRebuild` answer `false`. The fact must go red
      with `cannot add a STORED column`. If it stays green, the fact's table was empty and the fact
      is worthless. Restore and confirm with `git diff`.

- [ ] **Step 6: Commit** — `feat(migrations): rebuild the table where a generated column cannot be added in place`

---

## Task 6: `rollup` — lock, write the child, recompute

**Files:**
- Create: `src/MMLib.Alvo.Data.EntityFrameworkCore/Internal/RollupRecompute.cs`
- Modify: `src/MMLib.Alvo.Data.EntityFrameworkCore/Internal/EfAlvoData.cs`
- Test: `src/MMLib.Alvo.Testing/Data/AlvoDataComputedRollupTests.cs`

**The order is the entire correctness argument.** Lock the parent, write the child, recompute from
scratch. Never `total = total + delta`: it drifts with no self-correction and is simply wrong for
`min`/`max`, which the frozen schema allows.

- [ ] **Step 1: the facts** — one per op, so `min`/`max` cannot be satisfied by a sum-shaped
      implementation; a delete lowers the parent's aggregate; an update that moves a child from one
      parent to another recomputes **both** (the design does not name this case; the FK is
      writable, so it exists); `count` needs no `field`; a rollup over zero children reads as the
      op's own empty answer (`0` for `count`, `NULL` for the rest — the engine's answer, not a
      coalesce this layer invents).

- [ ] **Step 2: Run them, watch them fail.**

- [ ] **Step 3: Implement `RollupRecompute`,** three short methods and one statement each:

```csharp
/// <summary>
/// Recomputes every rollup a write to <paramref name="child"/> can change, inside the write's own
/// transaction: the parent's row lock first, then one <c>UPDATE … SET &lt;rollup&gt; = (SELECT
/// &lt;op&gt;(&lt;field&gt;) FROM &lt;child&gt; WHERE &lt;fk&gt; = @parent)</c>.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why the lock comes first, and why the single-statement recompute is NOT enough on its own.</b>
/// Measured on PostgreSQL, READ COMMITTED, 40 concurrent writers against one parent: the atomic
/// <c>UPDATE parent SET total = (SELECT SUM …)</c> wrote <b>31</b> of 40 once a 50 ms delay widened
/// the window. Under READ COMMITTED the <c>SET</c> expression is evaluated from the snapshot taken at
/// statement start, and when the row lock is finally granted EvalPlanQual re-checks only the outer
/// <c>WHERE</c> (<c>id = @p</c>, still true) — so the stale value is written. This is the same
/// EvalPlanQual mechanism that bit the outbox claim in PR5a; second occurrence in this codebase.
/// Taking the row lock BEFORE the recompute makes the following statement take a fresh snapshot, and
/// the same run then wrote 40 of 40.
/// </para>
/// <para>
/// <b>Why the lock is the dialect's and is a no-op on SQLite.</b> The two engines pull in opposite
/// directions. PostgreSQL requires the lock; SQLite must <em>not</em> read the parent before writing
/// inside a deferred transaction — 12 of 24 writers died on <c>SQLITE_BUSY_SNAPSHOT</c> (<c>[5/517]</c>)
/// when they did — and needs no lock at all, because the child insert already took the database-wide
/// write lock and SQLite admits one writer at a time. So an empty
/// <see cref="IAlvoSqlDialect.RowLockClause"/> means "issue no locking read here", not "issue an
/// unlocked one".
/// </para>
/// <para>
/// <b>Why all five ops go through one recompute.</b> A <c>total = total + delta</c> shortcut is
/// commutative for <c>sum</c> and <c>count</c> only, drifts with no self-correction if a single write
/// is ever missed, and is simply wrong for <c>min</c>/<c>max</c>, where removing the extreme child
/// cannot be expressed as a delta at all.
/// </para>
/// <para>
/// <b>What this does not claim.</b> The recompute is unbypassable only for writes that go through
/// this port. A direct <c>INSERT</c> into the child table by another application leaves the rollup
/// stale — the honest difference from <c>computed</c>, whose value the engine itself maintains. Named
/// here rather than discovered.
/// </para>
/// </remarks>
```

      The lock read is skipped entirely when `RowLockClause(PreImageMutation.Update)` is empty, and
      the `PreImageMutation.Update` mode is deliberate (Dev-3).

- [ ] **Step 4: Run them, green on both engines.** ring0 + the SQLite leg.

- [ ] **Step 5: Mutation** — replace the recompute's subquery with `<rollup> + @delta`. The
      `min`/`max` and the delete facts must go red. Then replace it with a recompute that ignores the
      child's own `where`-less filter — no: instead, make the recompute skip a parent whose FK
      changed, and watch the move fact go red.

- [ ] **Step 6: Commit** — `feat(data): maintain a rollup by locking the parent and recomputing`

---

## Task 7: the race, on PostgreSQL, with the window widened

**Files:**
- Create: `test/MMLib.Alvo.Data.PostgreSql.Tests.Integration/PostgreSqlRollupRaceTests.cs`

**Not in the shared suite, and that inverts this repo's usual assumption.** The concurrent-boot facts
treat SQLite as the harder leg because one writer at a time exposes lock contention; here that same
property makes the lost update structurally impossible, so a SQLite leg would be green whatever the
implementation does. This fact must run on PostgreSQL to mean anything.

- [ ] **Step 1: the fact.** N writers insert one child each against one parent, concurrently, and
      the parent's rollup must equal N. **The window is widened** — without the delay the lost
      update measured 40 of 40 and looked correct, so a fact with no widening asserts nothing.

- [ ] **Step 2: Run it. It must PASS** (the lock landed in Task 6) — which is exactly why step 4
      exists: a fact that has never been red is a fact nobody has measured.

- [ ] **Step 3:** pin the criterion (writers, delay, and the assertion that *every* writer
      committed, so an implementation that serialised by failing half of them cannot pass).

- [ ] **Step 4: THE NON-VACUITY CONTROL THE DESIGN NAMES** — remove the lock step from
      `RollupRecompute` and run this fact. **It must go red.** Record the number it produced. If it
      stays green the window is not wide enough or the writers are not concurrent; widen it until it
      fails, then restore the lock. Confirm the restore with `git diff`.

- [ ] **Step 5: Commit** — `test(data): pin the rollup race on PostgreSQL with a widened window`

---

## Task 8: the ladder, end to end over `baas-analyza:1358`'s invoice

**Files:**
- Modify: `src/MMLib.Alvo.Testing/Data/AlvoDataComputedRollupTests.cs`

- [ ] **Step 1: one descriptor, three rungs.** `invoice_items.line_total = unit_price * amount` is
      *computed*; `invoices.net_total = sum(line_total)` is *rollup*; `invoices.gross_total =
      net_total + vat_total` is *computed again over a column the framework maintains* — the
      property spike Q3 confirmed both engines track. Assert the whole chain moves when one child is
      added, and again when one is deleted.

- [ ] **Step 2: name the missing rung rather than skipping it.** The fourth rung — `vat_total` as a
      *before-hook*, because a VAT rate is contextual and time-valid business logic — needs PR5b-1,
      which is **PR #160, open** (Dev-6). The fact writes `vat_total` explicitly and its remarks say
      so, with the assertion that will replace the explicit write once #160 merges. A fact that
      silently omitted the rung would read as a ladder that has been proven end to end.

- [ ] **Step 3: Commit** — `test(data): assemble the computed → rollup → computed ladder`

---

## Task 9: delete the two `UnhonouredFeatures` entries — #21 closes here

**Files:**
- Modify: `src/MMLib.Alvo/Descriptor/Internal/UnhonouredFeatures.cs`
- Modify: `test/MMLib.Alvo.Tests/Descriptor/UnhonouredFeaturesTests.cs`
- Baseline: `…Every_unhonoured_slot_is_pinned.verified.txt` moves

- [ ] **Step 1:** delete the `computed` and `rollup` entries. **The `rollup` entry's fix suggestion
      says "rollups are deferred past F3", which was never true** — #21 sits in milestone F3 and both
      sources require the feature — so that sentence leaves with the entry rather than being
      corrected in place. Also update the class remarks, which say "PR6 owns `computed`, `rollup` and
      `default`": `default` is **not** in this PR, so the sentence must name what actually left.
      `UnhonouredFeatures.InTransaction`'s text ("exactly as an unmaintained 'rollup' column does")
      now points at a feature that works — reword it to the `default` case, which is still refused.

- [ ] **Step 2:** the Verify baseline moves. Framework-written — never hand-edited; run the suite so
      the tool rewrites it, then dispatch `alvo-snapshot-judge`.

- [ ] **Step 3:** ring1, then ring2.

- [ ] **Step 4: Commit** — `feat(descriptor): stop refusing computed and rollup`

---

## Task 10: docs, then the gates

**Files:**
- Modify: `docs/architecture/data-path.md` (the computed/rollup section),
  `docs/superpowers/specs/2026-08-04-f3-pr6-computed-rollup-design.md` (the deviations, numbered
  onward from PR5b's series), `examples/*` if one declares `computed`/`rollup`
- `docs/PLAN.md` — **untouched.** The marker does not move here.

- [ ] **Step 1:** record Dev-1 … Dev-6 where the design records deviations, each with its
      measurement.
- [ ] **Step 2:** `ALVO_CONFIGURATION=Release scripts/test-ring2`, then `scripts/test-e2e` — the
      write path and the schema DDL both changed.
- [ ] **Step 3:** `dotnet format --verify-no-changes`.
- [ ] **Step 4:** dispatch `alvo-plan-guard`.
- [ ] **Step 5:** reviewer substitutes for `/code-review high` and `/security-review` (the diff
      renders descriptor-derived text into DDL, which is #20's vector), paired with the
      `alvo-security-core-review` checklist — and say in the PR body that they are substitutes.
- [ ] **Step 6:** confirm `git diff --name-only origin/main...HEAD | wc -l` is under 100 so
      CodeRabbit reviews the PR at all.
