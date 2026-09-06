# PR-F — `select` reaches the database, and gains aliases — Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** `?select=` stops the database reading the columns the caller did not ask for, and gains PostgREST's `alias:source` renaming — closing #117 and #111.

**Architecture:** `AlvoQuery` gains a `Select` member. An unselected field is rendered `NULL AS <col>` in the `SELECT` list — the mechanism `hidden` already uses, chosen because EF's `FromSql` requires every mapped property in the result set — and its key is dropped when the `AlvoRecord` is assembled. The caller's `select` set and the policy's `hidden` mask travel as **two separate inputs** and are unioned only at render time. Aliases never reach the port: the parser resolves the source name and the API renders the response keys.

**Tech Stack:** .NET 10, EF Core (`FromSqlRaw` over a property-bag shared-type entity), xUnit v3 on Microsoft.Testing.Platform, Shouldly, Verify, CsCheck (property tests), Testcontainers (PostgreSQL integration).

**Spec:** `docs/superpowers/specs/2026-09-04-f4-pr-f-select-projection-design.md` — read it before Task 1. Sections 1.3, 1.4 and 2.4 carry decisions that look like implementation detail and are not.

## Global Constraints

- **Every `.cs` file is UTF-8 **with BOM** and **CRLF**.** `.editorconfig` sets `charset = utf-8-bom` and `end_of_line = crlf`; the Husky pre-commit `dotnet-format` task fails the commit with `error ENDOFLINE`/`error CHARSET` otherwise. Use the Edit/Write tools, which preserve encoding. If you edit a `.cs` file through a shell heredoc, `sed` or python, normalize before committing:
  ```python
  t = io.open(p,'rb').read().decode('utf-8-sig').replace('\r\n','\n').replace('\n','\r\n')
  io.open(p,'w',encoding='utf-8-sig',newline='').write(t)
  ```
- **Never commit to `main`.** All work lands on `f4/pr-f-select-projection`, which already exists and already carries the design commit.
- **Conventional Commits** — the `commit-msg` hook enforces it. Every commit message in this plan is already in that form.
- **`scripts/test-ring0` after every task.** `scripts/test-ring1` after Task 6 and Task 9. `scripts/test-ring2` once, before the PR. Never run the mutation or e2e suites locally.
- **Ordinal string comparison everywhere.** Every field-name set, dictionary and lookup in this plan uses `StringComparer.Ordinal`. The schema, the CEL type checker and the rendered SQL all use the exact declared name; an `OrdinalIgnoreCase` lookup would report a hidden field visible.
- **A caller preference never produces a 403, and a security control never produces a 422.** This is the one invariant the whole PR is arranged around (spec §1.3, §3). If a step seems to require breaking it, stop and re-read §1.3.
- **Short, single-purpose methods.** The house style keeps a method under ~25 lines and extracts aggressively; `alvo-dotnet-conventions` is the authority.
- **Two baselines will move and both need a judge verdict:** `test/MMLib.Alvo.Abstractions.Tests/PublicApi.MMLib.Alvo.Abstractions.verified.txt` (Task 1) and `OpenApiDocumentTests.The_document_is_stable.verified.txt` (Task 9). The Stop hook blocks the turn and asks for `alvo-snapshot-judge`; that is the intended path.

---

## File structure

**Port surface (`src/MMLib.Alvo.Abstractions`)**
- `Data/AlvoQuery.cs` — the `Select` member, its guard, and the type summary that currently denies projection exists.
- `Data/IAlvoData.cs` — the returned-key-set contract paragraph, amended to name `Select` as the one narrowing channel.

**EF driver (`src/MMLib.Alvo.Data.EntityFrameworkCore/Internal`)**
- `ReadProjection.cs` — two sets in, one `SELECT` list out; the store-type throw split by set.
- `ReadStatementComposer.cs` — the `Unselected` option, threaded to `ReadProjection.Compose`.
- `RecordMaterializer.cs` — drops a key present in either set.
- `EfAlvoData.cs` — the survivor set, and `Select` joining the availability guard.

**Reference implementation (`src/MMLib.Alvo.Testing/Data`)**
- `InMemoryAlvoData.cs` — the same two decisions, without a `SELECT` list.
- `AlvoDataProjectionTests.cs` — **new**, the shared contract suite every implementation runs.

**Data API (`src/MMLib.Alvo/Api/Internal`)**
- `QueryStringParser.cs` — `ProjectedField`, the alias grammar, the four refusals and the width bound.
- `QueryViolations.cs` — three new codes.
- `DataApiPage.cs` — `Project` deleted, `Render` in its place.
- `DataApiEndpoints.cs` — one call-site change in `MapList`.
- `DataApiParameters.cs` — the `select` parameter description, which currently asserts the opposite of what this PR ships.

**Docs** — `docs/architecture/data-api.md`, `CHANGELOG.md`.

---

### Task 1: `AlvoQuery.Select` and its guard

The port surface only. Nothing honours the member yet, and that is deliberate: this task is reviewable as "is this the right public shape".

**Files:**
- Modify: `src/MMLib.Alvo.Abstractions/Data/AlvoQuery.cs`
- Create: `test/MMLib.Alvo.Abstractions.Tests/Data/AlvoQueryTests.cs`
- Modify: `test/MMLib.Alvo.Abstractions.Tests/PublicApi.MMLib.Alvo.Abstractions.verified.txt` (accepted, not hand-written)

**Interfaces:**
- Produces: `AlvoQuery.Select` of type `IReadOnlyList<string>?`, `null` meaning every declared field; `static void AlvoQuery.EnsureProjectionIsSane(AlvoQuery query)` throwing `ArgumentException` for a non-null empty list and `ArgumentNullException` for a null query.

- [ ] **Step 1: Write the failing test**

Create `test/MMLib.Alvo.Abstractions.Tests/Data/AlvoQueryTests.cs`, following `AlvoFilterTests.cs` in the same directory for namespace and using style:

```csharp
using MMLib.Alvo.Data;
using Shouldly;
using Xunit;

namespace MMLib.Alvo.Abstractions.Tests.Data;

public sealed class AlvoQueryTests
{
    [Fact]
    public void A_projection_that_names_no_field_is_refused_rather_than_read_as_every_field()
    {
        var query = new AlvoQuery { Entity = "vehicles", Select = [] };

        Should.Throw<ArgumentException>(() => AlvoQuery.EnsureProjectionIsSane(query));
    }

    [Fact]
    public void An_absent_projection_is_every_field_and_is_not_refused()
    {
        var query = new AlvoQuery { Entity = "vehicles" };

        query.Select.ShouldBeNull();
        AlvoQuery.EnsureProjectionIsSane(query);
    }

    [Fact]
    public void A_projection_naming_one_field_is_accepted()
        => AlvoQuery.EnsureProjectionIsSane(new AlvoQuery { Entity = "vehicles", Select = ["name"] });

    [Fact]
    public void The_guard_requires_a_query()
        => Should.Throw<ArgumentNullException>(() => AlvoQuery.EnsureProjectionIsSane(null!));
}
```

- [ ] **Step 2: Run the test and confirm it fails to compile**

Run: `dotnet test test/MMLib.Alvo.Abstractions.Tests --filter-class MMLib.Alvo.Abstractions.Tests.Data.AlvoQueryTests`
Expected: a build error — `AlvoQuery` has no `Select` and no `EnsureProjectionIsSane`.

- [ ] **Step 3: Add the member**

In `AlvoQuery.cs`, after the `IncludeTotalCount` property:

```csharp
    /// <summary>
    /// Gets the declared field names to return, or <see langword="null"/> for every field the caller may
    /// read. Never an empty list — see <see cref="EnsureProjectionIsSane"/>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>An implementation must narrow what it reads, not only what it returns.</b> A member both shipped
    /// drivers ignored would be advisory, and an advisory port member is worse than none: a caller would ask
    /// for two fields, receive every one, and nothing would be raised. What "narrow" means in SQL is the
    /// driver's business — the shipped EF drivers render an unselected column as a typed <c>NULL</c> rather
    /// than dropping it from the <c>SELECT</c> list, because EF requires a <c>FromSql</c> result set to carry
    /// every mapped property — but the observable rule is the same everywhere: the key is absent from the
    /// returned record.
    /// </para>
    /// <para>
    /// <b>A name here is subject to the same confidentiality rule as <see cref="Filter"/> and
    /// <see cref="Sort"/>:</b> a projection naming a field in <see cref="Rules.PolicyDecision.HiddenFields"/>
    /// is refused, with the identical message an undeclared name earns, so the refusal is not an oracle for
    /// "this field exists but is hidden from you".
    /// </para>
    /// <para>
    /// <b>Framework-managed columns survive it.</b> <see cref="IAlvoData"/>'s returned-key-set contract keeps
    /// <c>id</c>, <c>tenant_id</c> and the audit columns in every record whatever this names — the row key
    /// alone is what a keyset cursor is minted from — and so does every field named in <see cref="Sort"/>,
    /// because ordering is not expressible over a column the statement did not read.
    /// </para>
    /// </remarks>
    public IReadOnlyList<string>? Select { get; init; }
```

- [ ] **Step 4: Add the guard**

In the same file, after `EnsurePagingWindowIsSane`:

```csharp
    /// <summary>
    /// Throws when <paramref name="query"/>'s projection names no field — a read that can return nothing.
    /// </summary>
    /// <remarks>
    /// The sibling of <see cref="EnsurePagingWindowIsSane"/>, and here for the same reason: a rule of the
    /// port belongs on the port's own type, so a third implementation inherits it instead of writing another
    /// copy. An empty projection is refused rather than read as "every field", on the same ground the
    /// <c>after</c>+<c>offset</c> pair is refused — silently resolving an ambiguous request is what this port
    /// does not do.
    /// </remarks>
    /// <param name="query">The query about to be served.</param>
    /// <exception cref="ArgumentException"><paramref name="query"/>'s projection names no field.</exception>
    public static void EnsureProjectionIsSane(AlvoQuery query)
    {
        ArgumentNullException.ThrowIfNull(query);

        if (query.Select is { Count: 0 })
        {
            throw new ArgumentException(
                "A query's projection names no fields, so it could return none. Name at least one declared "
                + "field, or leave the projection unset for every field this caller may read.",
                nameof(query));
        }
    }
```

- [ ] **Step 5: Correct the type summary that now lies**

In the same file, the type summary says projection is *"deliberately **not** modelled here yet; they land in PR3"*. Replace that sentence:

```
/// filtering, sorting, keyset paging and projection. Relation embedding, aggregates and bulk
/// operations are deliberately <b>not</b> modelled here yet.
```

- [ ] **Step 6: Run the test and confirm it passes**

Run: `dotnet test test/MMLib.Alvo.Abstractions.Tests --filter-class MMLib.Alvo.Abstractions.Tests.Data.AlvoQueryTests`
Expected: PASS, 4 tests.

- [ ] **Step 7: Accept the public-API baseline**

Run: `dotnet test test/MMLib.Alvo.Abstractions.Tests`
Expected: the public-API approval test FAILS and writes `PublicApi.MMLib.Alvo.Abstractions.received.txt`.

Diff the received file against the verified one and confirm the **only** changes are the two new members. Then accept it by replacing the verified file with the received one and deleting the received file.

- [ ] **Step 8: Run ring0 and commit**

```bash
scripts/test-ring0
git add src/MMLib.Alvo.Abstractions/Data/AlvoQuery.cs \
        test/MMLib.Alvo.Abstractions.Tests/Data/AlvoQueryTests.cs \
        test/MMLib.Alvo.Abstractions.Tests/PublicApi.MMLib.Alvo.Abstractions.verified.txt
git commit -m "feat(data): AlvoQuery.Select, the projection member the port had refused to publish"
```

The turn gate will block on the moved `*.verified.txt` and ask for `alvo-snapshot-judge`. Dispatch it; the justification is this task's source change.

---

### Task 2: a projection naming a hidden field is refused, in both implementations

The security half, before anything honours the member. After this task `Select` is *refused or ignored* — never silently over-served — which is the state `ParsedListQuery` demanded before the member could exist.

**Files:**
- Modify: `src/MMLib.Alvo.Data.EntityFrameworkCore/Internal/EfAlvoData.cs` (`QueryFields`, and the guard calls in `QueryAsync`)
- Modify: `src/MMLib.Alvo.Testing/Data/InMemoryAlvoData.cs` (`QueryFields`, and the guard calls in `QueryAsync`)
- Create: `src/MMLib.Alvo.Testing/Data/AlvoDataProjectionTests.cs` (the refusal cases only; the projection cases arrive in Task 5)
- Create: `test/MMLib.Alvo.Data.Sqlite.Tests/SqliteAlvoDataProjectionTests.cs`
- Create: `test/MMLib.Alvo.Tests/Data/InMemoryAlvoDataProjectionTests.cs`

**Interfaces:**
- Consumes: `AlvoQuery.Select`, `AlvoQuery.EnsureProjectionIsSane` (Task 1).
- Produces: the abstract suite `AlvoDataProjectionTests`, whose per-implementation subclasses Task 5 extends. Its abstract member follows `AlvoDataPagingTests` — read that file for the exact fixture shape before writing this one.

- [ ] **Step 1: Write the failing tests**

Create `src/MMLib.Alvo.Testing/Data/AlvoDataProjectionTests.cs`. The fixture seam is the one
every data-path suite defines — copy it verbatim from `AlvoDataPagingTests.cs`:

```csharp
using MMLib.Alvo.Data;
using MMLib.Alvo.Descriptor;
using MMLib.Alvo.Schema;
using Shouldly;
using Xunit;
using DescField = MMLib.Alvo.Descriptor.FieldType;
using SchemaField = MMLib.Alvo.Schema.FieldType;

namespace MMLib.Alvo.Testing.Data;

/// <summary>
/// The projection as a rule of the <b>port</b>, proved over every <see cref="IAlvoData"/> implementation
/// this suite runs against — the in-memory reference included. Three claims, and the third is the one that
/// nearly shipped broken: a projection narrows the returned key set; it never narrows it below the
/// framework-managed columns <see cref="IAlvoData"/>'s contract promises; and it never changes which rows
/// come back or in what order.
/// </summary>
public abstract class AlvoDataProjectionTests
{
    /// <inheritdoc cref="AlvoDataPagingTests.CreateAsync"/>
    protected abstract Task<IAlvoData> CreateAsync(
        SchemaModel schema, AlvoDescriptor descriptor, IReadOnlyDictionary<string, IReadOnlyList<AlvoRecord>> seed);

    [Fact]
    public async Task A_projection_naming_a_hidden_field_is_refused_exactly_as_an_undeclared_one_is()
    {
        var world = await SeededWorldAsync(rowCount: 3);

        var hidden = await Should.ThrowAsync<AlvoAuthorizationException>(() => world.Data.QueryAsync(
            new AlvoQuery { Entity = "notes", Select = ["secret"] }, world.Alice));
        var undeclared = await Should.ThrowAsync<AlvoAuthorizationException>(() => world.Data.QueryAsync(
            new AlvoQuery { Entity = "notes", Select = ["nosuchfield"] }, world.Alice));

        hidden.Message.ShouldBe(
            undeclared.Message,
            "the refusal must not tell a caller which of the two happened");
    }

    [Fact]
    public async Task A_projection_naming_no_field_is_refused()
    {
        var world = await SeededWorldAsync(rowCount: 1);

        await Should.ThrowAsync<ArgumentException>(() => world.Data.QueryAsync(
            new AlvoQuery { Entity = "notes", Select = [] }, world.Alice));
    }

    [Fact]
    public async Task A_projection_naming_a_declared_readable_field_is_served()
    {
        var world = await SeededWorldAsync(rowCount: 3);

        var page = await world.Data.QueryAsync(
            new AlvoQuery { Entity = "notes", Select = ["title"] }, world.Alice);

        page.Items.Count.ShouldBe(3);
    }

    /// <summary>
    /// One seeded <c>notes</c> database: a title to select, a wide body to leave unselected, a nullable
    /// label to sort by, and one field a <c>hidden</c> rule masks.
    /// </summary>
    private async Task<SeededWorld> SeededWorldAsync(int rowCount)
    {
        var descriptor = new AlvoDescriptor
        {
            ApiVersion = "alvo.dev/v1",
            Name = "projection-fixture",
            Entities = new Dictionary<string, EntityDescriptor>(StringComparer.Ordinal)
            {
                ["notes"] = new EntityDescriptor
                {
                    Tenancy = EntityTenancy.Global,
                    Fields = new Dictionary<string, FieldDescriptor>(StringComparer.Ordinal)
                    {
                        ["title"] = new() { Type = DescField.String, Required = true },
                        ["body"] = new() { Type = DescField.String },
                        ["label"] = new() { Type = DescField.String },
                        ["secret"] = new() { Type = DescField.String, Hidden = BoolOrCel.FromBoolean(true) },
                    },
                    Rules = new AccessRules { List = "true", Get = "true" },
                },
            },
        };

        var schema = new SchemaModel([
            new EntitySchema
            {
                Name = "notes",
                Tenancy = TenancyMode.Global,
                Fields =
                [
                    new FieldSchema { Name = "id", Type = SchemaField.Uuid, Required = true },
                    new FieldSchema { Name = "title", Type = SchemaField.String, Required = true, MaxLength = 32 },
                    new FieldSchema { Name = "body", Type = SchemaField.String, Nullable = true },
                    new FieldSchema { Name = "label", Type = SchemaField.String, Nullable = true },
                    new FieldSchema { Name = "secret", Type = SchemaField.String, Nullable = true },
                ],
            },
        ]);

        // The label order is deliberately the reverse of the insertion order, so a sort over it that
        // silently resolved to a projected NULL would return the insertion order and be caught.
        var seed = Enumerable.Range(0, rowCount)
            .Select(index => new AlvoRecord(new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["id"] = Guid.NewGuid(),
                ["title"] = $"row-{index:D4}",
                ["body"] = new string('x', 256),
                ["label"] = $"label-{rowCount - index:D4}",
                ["secret"] = "classified",
            }))
            .ToList();

        var data = await CreateAsync(
            schema,
            descriptor,
            new Dictionary<string, IReadOnlyList<AlvoRecord>>(StringComparer.Ordinal) { ["notes"] = seed });

        return new SeededWorld(data, Caller);
    }

    private sealed record SeededWorld(IAlvoData Data, AlvoContext Alice);

    private static AlvoContext Caller => new()
    {
        User = UserId.New(),
        Roles = new HashSet<Role> { Role.Authenticated },
        Tenant = null,
    };
}
```

Then the two subclasses. `test/MMLib.Alvo.Data.Sqlite.Tests/SqliteAlvoDataProjectionTests.cs` and
`test/MMLib.Alvo.Tests/Data/InMemoryAlvoDataProjectionTests.cs` are each three lines over the
fixture their sibling paging suite already uses — copy `SqliteAlvoDataPagingTests.cs` and
`InMemoryAlvoDataPagingTests.cs` and change the base class and the class name, nothing else.

- [ ] **Step 2: Run them and confirm the first two fail**

Run: `dotnet test test/MMLib.Alvo.Data.Sqlite.Tests --filter-class MMLib.Alvo.Data.Sqlite.Tests.SqliteAlvoDataProjectionTests`
Expected: the hidden-field case FAILS (no exception raised — the projection is ignored today), the empty case FAILS (no guard call), the third PASSES.

- [ ] **Step 3: Extend the EF driver's availability guard**

In `EfAlvoData.cs`, `QueryFields` currently reads:

```csharp
    private static IEnumerable<string> QueryFields(AlvoQuery query) =>
        AlvoFilter.ReferencedFields(query.Filter).Concat(query.Sort.Select(sort => sort.Field));
```

Add the projection, and say why it belongs in the same feeder:

```csharp
    /// <summary>
    /// Every caller-supplied field name this statement is about to reference — filter terms, sort keys and
    /// the projection alike.
    /// </summary>
    /// <remarks>
    /// The projection is here rather than checked separately because it is the same kind of string and earns
    /// the same refusal: naming a masked field in <c>select</c> is the identical oracle as naming one in a
    /// filter, and one feeder is what keeps the two answers byte-identical.
    /// </remarks>
    private static IEnumerable<string> QueryFields(AlvoQuery query) =>
        AlvoFilter.ReferencedFields(query.Filter)
            .Concat(query.Sort.Select(sort => sort.Field))
            .Concat(query.Select ?? []);
```

- [ ] **Step 4: Call the projection guard in the EF driver**

In `QueryAsync`, beside the two existing port guards:

```csharp
        AlvoFilter.EnsureWithinLimits(query.Filter);
        AlvoQuery.EnsurePagingWindowIsSane(query);
        AlvoQuery.EnsureProjectionIsSane(query);
```

- [ ] **Step 5: Make the same two changes in the reference implementation**

`InMemoryAlvoData.cs` has its own `QueryFields` (identical body) and its own guard calls in `QueryAsync`. Apply the same two edits. The bodies must stay identical to the EF ones — a divergence here is a divergence in what is refused.

- [ ] **Step 6: Run the tests and confirm they pass on both implementations**

Run: `dotnet test test/MMLib.Alvo.Data.Sqlite.Tests --filter-class MMLib.Alvo.Data.Sqlite.Tests.SqliteAlvoDataProjectionTests`
Run: `dotnet test test/MMLib.Alvo.Tests --filter-class MMLib.Alvo.Tests.Data.InMemoryAlvoDataProjectionTests`
Expected: PASS on both.

- [ ] **Step 7: Run ring0 and commit**

```bash
scripts/test-ring0
git add -u && git add src/MMLib.Alvo.Testing/Data/AlvoDataProjectionTests.cs \
        test/MMLib.Alvo.Data.Sqlite.Tests/SqliteAlvoDataProjectionTests.cs \
        test/MMLib.Alvo.Tests/Data/InMemoryAlvoDataProjectionTests.cs
git commit -m "feat(data): a projection naming a hidden field is refused like an undeclared one"
```

---

### Task 3: the EF drivers stop reading the unselected columns

**Files:**
- Modify: `src/MMLib.Alvo.Data.EntityFrameworkCore/Internal/ReadProjection.cs`
- Modify: `src/MMLib.Alvo.Data.EntityFrameworkCore/Internal/ReadStatementComposer.cs`
- Modify: `src/MMLib.Alvo.Data.EntityFrameworkCore/Internal/RecordMaterializer.cs`
- Modify: `src/MMLib.Alvo.Data.EntityFrameworkCore/Internal/EfAlvoData.cs`
- Modify: `test/MMLib.Alvo.Data.EntityFrameworkCore.Tests/ReadProjectionTests.cs` (6 direct `Compose` calls)
- Modify: `test/MMLib.Alvo.Data.EntityFrameworkCore.Tests/RecordMaterializerTests.cs`

**Interfaces:**
- Consumes: `AlvoQuery.Select` (Task 1).
- Produces:
  - `ReadProjection.Compose(EntitySchema entity, IReadOnlySet<string> hiddenFields, IReadOnlySet<string> unselectedFields, IAlvoSqlDialect dialect, IEntityType rows)` — the third parameter is **required**.
  - `ReadStatementComposer.ReadStatementOptions.Unselected` of type `IReadOnlySet<string>`, defaulting to `FrozenSet<string>.Empty`.
  - `RecordMaterializer.ToRecord(IDictionary<string, object> row, IReadOnlySet<string> hiddenFields, IReadOnlySet<string> unselectedFields)` — the third parameter is **required**, and all seven call sites are edited. Six pass `FrozenSet<string>.Empty`; only the page path passes a real set. Required rather than defaulted so the author who later adds `select` to `GET /{entity}/{id}` is made to look at `GetAsync`'s call site — see the design's section 1.6.

- [ ] **Step 1: Write the failing tests**

In `ReadProjectionTests.cs`, add four cases. The private `Compose` helper in that file gains an overload so the existing tests keep reading as they do:

```csharp
    [Fact]
    public void An_unselected_field_is_projected_as_a_typed_null_under_its_own_name()
    {
        var sql = Compose(Hidden(), Unselected("notes"));

        sql.ShouldContain("AS \"notes\"");
        sql.ShouldNotContain(", \"notes\"");
    }

    [Fact]
    public void A_selected_field_is_read_from_the_column()
        => Compose(Hidden(), Unselected("notes")).ShouldContain("\"name\"");

    /// <summary>
    /// The two sets are separate inputs and only the mask reaches <c>EnsureMaskable</c>. Feeding the
    /// projection through the mask parameter would turn a caller's own mistake into a 403 — see the design's
    /// section 1.3.
    /// </summary>
    [Fact]
    public void The_row_key_being_unselected_is_not_an_authorization_failure()
    {
        // id is never in the unselected set in production (the survivor set excludes it), but if it arrives
        // there the answer is a bug report, not a denial.
        Should.NotThrow(() => Compose(Hidden(), Unselected("name")));
        Should.Throw<AlvoAuthorizationException>(() => Compose(Hidden("id"), Unselected()));
    }

    /// <summary>
    /// A masked field the read model does not map is a security condition; an <em>unselected</em> one is a
    /// disagreement between the applied schema and the read model, which is an Alvo bug and must not be
    /// dressed as a decision about the caller.
    /// </summary>
    [Fact]
    public void An_unmappable_unselected_field_is_a_bug_rather_than_a_denial()
    {
        var declared = _entity with { Fields = [.. _entity.Fields, new FieldSchema { Name = "ghost", Type = FieldType.String }] };

        Should.Throw<InvalidOperationException>(
            () => ReadProjection.Compose(declared, Hidden(), Unselected("ghost"), _dialect, ReadModelFixture.Rows(_entity)));
    }
```

Add the helpers at the bottom of the class:

```csharp
    private static string Compose(IReadOnlySet<string> hiddenFields, IReadOnlySet<string> unselectedFields) =>
        ReadProjection.Compose(_entity, hiddenFields, unselectedFields, _dialect, ReadModelFixture.Rows(_entity));

    private static HashSet<string> Unselected(params string[] fields) => fields.ToHashSet(StringComparer.Ordinal);
```

- [ ] **Step 2: Run them and confirm they fail to compile**

Run: `dotnet test test/MMLib.Alvo.Data.EntityFrameworkCore.Tests --filter-class MMLib.Alvo.Data.EntityFrameworkCore.Tests.ReadProjectionTests`
Expected: a build error — `Compose` takes four arguments.

- [ ] **Step 3: Take the second set in `ReadProjection`**

Replace the body of `ReadProjection.cs` below the type summary. Add to that summary a second paragraph:

```csharp
/// <para>
/// <b>Two sets in, never one.</b> The mask is a security control resolved per caller; the unselected set is
/// the caller's own preference. They are unioned here and nowhere else, and only for one decision — which
/// columns become a projected <c>NULL</c>. <see cref="QueryFieldGuard.EnsureMaskable"/> keeps seeing the mask
/// alone, because it answers with <see cref="AlvoAuthorizationException"/> and a caller's projection must
/// never produce a 403.
/// </para>
```

```csharp
    internal static string Compose(
        EntitySchema entity,
        IReadOnlySet<string> hiddenFields,
        IReadOnlySet<string> unselectedFields,
        IAlvoSqlDialect dialect,
        IEntityType rows)
    {
        ArgumentNullException.ThrowIfNull(entity);
        ArgumentNullException.ThrowIfNull(hiddenFields);
        ArgumentNullException.ThrowIfNull(unselectedFields);
        ArgumentNullException.ThrowIfNull(dialect);
        ArgumentNullException.ThrowIfNull(rows);
        QueryFieldGuard.EnsureMaskable(hiddenFields, rows);

        return string.Join(
            ", ", entity.Fields.Select(field => Project(field, hiddenFields, unselectedFields, dialect, rows)));
    }

    private static string Project(
        FieldSchema field,
        IReadOnlySet<string> hiddenFields,
        IReadOnlySet<string> unselectedFields,
        IAlvoSqlDialect dialect,
        IEntityType rows)
    {
        // The mask is tested FIRST, and that order is load-bearing rather than stylistic. The two sets
        // overlap on every projected read of a masked entity — a hidden field is never selected, never a
        // sort key and never framework-managed, so it is always unselected too — and testing `unselected`
        // first would answer a masked field's unresolvable store type with InvalidOperationException,
        // undoing the split below in the one direction that matters.
        if (hiddenFields.Contains(field.Name))
        {
            return NullProjection(field.Name, dialect, rows, masked: true);
        }

        return unselectedFields.Contains(field.Name)
            ? NullProjection(field.Name, dialect, rows, masked: false)
            : dialect.RenderColumn(field.Name);
    }

    private static string NullProjection(string field, IAlvoSqlDialect dialect, IEntityType rows, bool masked)
    {
        var storeType = rows.FindProperty(field)?.GetColumnType() ?? throw NoStoreType(field, masked);
        return $"{dialect.RenderNullProjection(storeType)} AS {dialect.RenderColumn(field)}";
    }

    /// <summary>
    /// The two sets fail differently, and that is the point. A mask the read model cannot apply is the
    /// fail-closed case <see cref="QueryFieldGuard"/> exists for — it can arrive from a source that never ran
    /// the apply-time check, F7's dynamic-entity registry being the next one. An unselected field the model
    /// cannot map is unreachable by construction, because the set is derived from the applied schema's own
    /// fields: reaching it means the schema and the read model disagree, which is an Alvo defect.
    /// </summary>
    private static Exception NoStoreType(string field, bool masked) => masked
        ? new AlvoAuthorizationException(QueryFieldGuard.UnmaskableFieldMessage)
        : new InvalidOperationException(
            $"The applied schema declares '{field}' but the read model maps no such property, so the "
            + "projection has no store type to cast a NULL to. The schema and the read model disagree.");
```

- [ ] **Step 4: Run the `ReadProjection` tests and confirm they pass**

Run: `dotnet test test/MMLib.Alvo.Data.EntityFrameworkCore.Tests --filter-class MMLib.Alvo.Data.EntityFrameworkCore.Tests.ReadProjectionTests`
Expected: PASS. Fix the four pre-existing `Every_argument_is_required` assertions to pass the new argument, and add a fifth for `unselectedFields: null!`.

- [ ] **Step 5: Add the `Unselected` option and thread it**

In `ReadStatementComposer.ReadStatementOptions`, after `Unmasked`:

```csharp
        /// <summary>
        /// The declared fields the caller did not select, rendered as projected <c>NULL</c>s instead of being
        /// read. Empty for every read but a projected page.
        /// </summary>
        /// <remarks>
        /// <b>Separate from the decision's field mask, deliberately</b> — see <see cref="ReadProjection"/>.
        /// <b>And empty on every path but the page:</b> a pre-image, a policy root and a single-row read all
        /// build their own options and get this default. <see cref="ComposeCount"/> is the exception worth
        /// knowing about: it is handed the page's own record and simply ignores this, exactly as it ignores
        /// <see cref="Anchor"/>, <see cref="Sort"/>, <see cref="Limit"/> and <see cref="Offset"/> — it
        /// composes no projection at all, so there is nothing here for it to apply.
        /// </remarks>
        internal IReadOnlySet<string> Unselected { get; init; } = FrozenSet<string>.Empty;
```

Add `using System.Collections.Frozen;` to the file — it is **not** currently imported there, nor in
`EfAlvoData.cs`, which needs it too for `FrozenSet<string>.Empty` and `ToFrozenSet`. In `Compose`,
pass it through:

```csharp
            .Append(ReadProjection.Compose(entity, Mask(decision, options), options.Unselected, _dialect, rows))
```

- [ ] **Step 6: Compute the survivor set in `EfAlvoData`**

`ReadOptions` needs the entity, so its signature grows a parameter. Change the call in `QueryAsync` from `ReadOptions(query, anchor)` to `ReadOptions(query, anchor, entity)` — note `entity` is `EntitySchema?` there, and the existing `PageAsync`/`TotalCountAsync` pattern of `entity ?? throw new AlvoAuthorizationException(UnknownEntityMessage)` is the precedent to follow.

```csharp
    private static ReadStatementComposer.ReadStatementOptions ReadOptions(
        AlvoQuery query, KeysetAnchor? anchor, EntitySchema entity) =>
        new()
        {
            Filter = query.Filter,
            Anchor = anchor,
            Sort = query.Sort,
            Limit = OverFetched(query.Limit),
            Offset = query.Offset,
            Unselected = Unselected(query, entity),
        };

    /// <summary>
    /// The declared fields this read will not fetch: everything the entity declares that the caller did not
    /// select and that nothing else in the statement needs.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Framework-managed columns survive, through <see cref="AlvoManagedColumns.For(EntitySchema)"/> rather
    /// than a list written here.</b> That type is the one authority for which columns the framework owns, and
    /// it exists because two hand-kept copies of this answer drifted. Beyond the contract,
    /// <see cref="Paginated"/> mints the keyset cursor from the fetched row's <c>id</c>, so a NULLed key would
    /// not mis-sort a page — it would break paging outright.
    /// </para>
    /// <para>
    /// <b>Every sort key survives, and this is measured rather than cautious.</b> The projection aliases its
    /// <c>NULL</c> to the column's own name, and a bare identifier in <c>ORDER BY</c> resolves against the
    /// output column names first — on SQLite <em>and</em> PostgreSQL. So NULLing a sort key would order the
    /// page by the <c>NULL</c> while the keyset boundary in <c>WHERE</c> still described the real sequence,
    /// which is the "a page skips or repeats a row" failure the sort renderer is written to make
    /// unrepresentable. A filter term, the keyset anchor and the policy predicates need no such exemption:
    /// they are all in <c>WHERE</c>, where both engines resolve the table column and ignore the alias.
    /// </para>
    /// </remarks>
    private static IReadOnlySet<string> Unselected(AlvoQuery query, EntitySchema entity)
    {
        if (query.Select is null)
        {
            return FrozenSet<string>.Empty;
        }

        var survivors = new HashSet<string>(query.Select, StringComparer.Ordinal);
        survivors.UnionWith(AlvoManagedColumns.For(entity));
        survivors.UnionWith(query.Sort.Select(sort => sort.Field));

        return entity.Fields
            .Select(field => field.Name)
            .Where(name => !survivors.Contains(name))
            .ToFrozenSet(StringComparer.Ordinal);
    }
```

- [ ] **Step 7: Drop the unselected keys when the record is assembled**

In `RecordMaterializer.cs`:

```csharp
    /// <param name="row">The property-bag row the engine returned.</param>
    /// <param name="hiddenFields">The resolved field mask.</param>
    /// <param name="unselectedFields">
    /// The fields the caller's projection excluded; empty when it excluded none. <b>Required rather than
    /// defaulted</b>, though six of the seven call sites pass an empty set: a default would mean the author
    /// who adds <c>select</c> to a single-row read is never made to look at <c>GetAsync</c>'s call site, and
    /// a projection that narrowed that statement while its materialization kept every key is the advisory
    /// member this whole change exists to avoid, one layer down.
    /// </param>
    internal static AlvoRecord ToRecord(
        IDictionary<string, object> row,
        IReadOnlySet<string> hiddenFields,
        IReadOnlySet<string> unselectedFields)
    {
        ArgumentNullException.ThrowIfNull(row);
        ArgumentNullException.ThrowIfNull(hiddenFields);
        ArgumentNullException.ThrowIfNull(unselectedFields);

        var values = new Dictionary<string, object?>(StringComparer.Ordinal);
        foreach (var (field, value) in row)
        {
            if (!hiddenFields.Contains(field) && !unselectedFields.Contains(field))
            {
                values[field] = value;
            }
        }

        return new AlvoRecord(values);
    }
```

The other six sites (`EfAlvoData.cs:219, 260, 536, 605, 821, 1377`) each take
`FrozenSet<string>.Empty`. `:219` is `GetAsync` — a read, not a write — and is the one that will
change when `select` reaches a single-row read.

In `EfAlvoData.QueryAsync`, pass it at the page's materialization only:

```csharp
            Items = [.. kept.Select(row => RecordMaterializer.ToRecord(row, decision.HiddenFields, options.Unselected))],
```

- [ ] **Step 8: Run the driver tests and ring0**

Run: `dotnet test test/MMLib.Alvo.Data.EntityFrameworkCore.Tests`
Run: `dotnet test test/MMLib.Alvo.Data.Sqlite.Tests`
Expected: PASS. If `ReadStatementComposerTests` or `TSqlDialectSeamTests` break, the new member was not defaulted — they construct `ReadStatementOptions` through object initializers at ~32 places and none should need an edit.

- [ ] **Step 9: Commit**

```bash
scripts/test-ring0
git add -u
git commit -m "feat(data): the EF drivers stop reading the columns a projection excluded"
```

---

### Task 4: the reference implementation honours `Select`

**Files:**
- Modify: `src/MMLib.Alvo.Testing/Data/InMemoryAlvoData.cs`

**Interfaces:**
- Consumes: `AlvoQuery.Select`, `AlvoManagedColumns.For` (Task 1, Task 3's survivor rule).
- Produces: nothing new; the reference now answers the same key set as the drivers.

- [ ] **Step 1: Write the failing test**

Add to `AlvoDataProjectionTests` (created in Task 2):

```csharp
    [Fact]
    public async Task A_projected_read_returns_the_selected_keys_and_no_other_declared_field()
    {
        // Seed one row, read with Select = ["name"], assert:
        //   record contains "name"
        //   record does NOT contain a declared, non-managed field that was not selected
        //   record DOES contain AlvoManagedColumns.For(entity) — the returned-key-set contract
    }
```

- [ ] **Step 2: Run it and confirm it fails on the in-memory implementation only**

Run: `dotnet test test/MMLib.Alvo.Tests --filter-class MMLib.Alvo.Tests.Data.InMemoryAlvoDataProjectionTests`
Expected: FAIL — the reference returns every field.
Run: `dotnet test test/MMLib.Alvo.Data.Sqlite.Tests --filter-class MMLib.Alvo.Data.Sqlite.Tests.SqliteAlvoDataProjectionTests`
Expected: PASS — Task 3 already did this half.

- [ ] **Step 3: Honour the projection in the reference**

In `InMemoryAlvoData.QueryAsync`, the page is masked at `Items = [.. page.Select(row => Mask(row, decision.HiddenFields))]`. The reference has no `SELECT` list, so for it the projection *is* the mask, applied over the union — but computed by the same survivor rule as the driver, or the differential suite diverges.

**Three things here are not obvious, and each one is a way to get this silently wrong:**

**(a) There is no `EntitySchema` in scope.** `QueryAsync` never resolves one —
`EnsureQueryFieldsAvailable` looks the fields up internally. Add a local through the file's own
lookup (`private EntitySchema? FindEntity(string entity)`), which is **nullable**:

```csharp
        var schema = FindEntity(query.Entity);
        var unselected = Unselected(query, schema);
```

A null schema returns `FrozenSet<string>.Empty` from `Unselected` — the fail-safe reading, and safe
because the field guard has already refused every named field by the time this runs. Unlike the EF
driver there is no `UnknownEntityMessage` precedent to follow here: `DeclaredFields` fails closed
with an empty set rather than throwing, and this follows that.

**(b) `Mask` has an early return that makes the projection a no-op.** As written it is:

```csharp
    private static AlvoRecord Mask(AlvoRecord record, IReadOnlySet<string> hiddenFields)
    {
        if (hiddenFields.Count == 0)
        {
            return record;
        }
```

Widening the signature is not enough — on an entity with no `hidden` rule, which is most of them
and is exactly the projection fixture's shape, the record would come back whole and the differential
suite would go red against a driver that got it right. The guard must consider both sets:

```csharp
    private static AlvoRecord Mask(
        AlvoRecord record, IReadOnlySet<string> hiddenFields, IReadOnlySet<string> unselectedFields)
    {
        if (hiddenFields.Count == 0 && unselectedFields.Count == 0)
        {
            return record;
        }
```

and the per-key exclusion becomes `hiddenFields.Contains(pair.Key) || unselectedFields.Contains(pair.Key)`.
`Mask`'s other call sites pass `FrozenSet<string>.Empty`, for the same reason the EF driver's do.

**(c) Hoist `unselected` out of the row loop.** `Unselected(query, schema)` allocates a set; computing
it per row would do that once per returned row.

Add the same `Unselected` helper the EF driver has — same survivor set, same
`AlvoManagedColumns.For` call, same sort-key exemption. The sort-key exemption has no SQL reason
here (there is no `ORDER BY` to shadow) and is kept anyway: the reference's job is to answer what
the drivers answer, and one that returned *fewer* keys would make the differential suite red for the
right reason and the wrong implementation.

**Note for the implementer:** the sort-key exemption has no SQL reason here — there is no `ORDER BY` to shadow. It is kept anyway, because the reference's job is to answer what the drivers answer; a reference that returned *fewer* keys than a driver would make the differential suite red for the right reason and the wrong implementation.

- [ ] **Step 4: Run both and confirm they pass**

Run: `dotnet test test/MMLib.Alvo.Tests --filter-class MMLib.Alvo.Tests.Data.InMemoryAlvoDataProjectionTests`
Run: `dotnet test test/MMLib.Alvo.Data.Sqlite.Tests --filter-class MMLib.Alvo.Data.Sqlite.Tests.SqliteAlvoDataProjectionTests`
Expected: PASS on both.

- [ ] **Step 5: Commit**

```bash
scripts/test-ring0
git add -u
git commit -m "feat(data): the in-memory reference honours the projection too"
```

---

### Task 5: the contract suite that proves the three implementations agree

The cases the first draft of the design had none of. Every one of these is a case where the NULL-projection could plausibly diverge from a full read.

**Files:**
- Modify: `src/MMLib.Alvo.Testing/Data/AlvoDataProjectionTests.cs`
- Create: `test/MMLib.Alvo.Data.PostgreSql.Tests.Integration/PostgreSqlAlvoDataProjectionTests.cs`

**Interfaces:**
- Consumes: everything from Tasks 1–4.
- Produces: nothing new.

- [ ] **Step 1: Write the failing tests**

Add to `AlvoDataProjectionTests`. Each is a paired read — once projected, once not — and asserts the two agree on everything but the key set:

```csharp
    /// <summary>
    /// The defect this suite exists for. The projection aliases its <c>NULL</c> to the column's own name, and
    /// a bare identifier in <c>ORDER BY</c> resolves against the output column names first on both engines —
    /// so a NULLed sort key would order the page by the <c>NULL</c>. The survivor set keeps sort keys real;
    /// this is what notices if it stops.
    /// </summary>
    [Fact]
    public async Task A_sort_over_an_unselected_field_orders_exactly_as_the_same_sort_without_a_projection()
    {
        var world = await SeededWorldAsync(rowCount: 5);
        AlvoSort[] sort = [new AlvoSort("label", Descending: true)];

        var unprojected = await world.Data.QueryAsync(
            new AlvoQuery { Entity = "notes", Sort = sort }, world.Alice);
        var projected = await world.Data.QueryAsync(
            new AlvoQuery { Entity = "notes", Sort = sort, Select = ["id"] }, world.Alice);

        projected.Items.Select(row => row["id"])
            .ShouldBe([.. unprojected.Items.Select(row => row["id"])]);
    }

    /// <summary>
    /// The same defect, in the form that fails loudly. Under an alias-shadowed <c>ORDER BY</c> the page's
    /// order and the keyset boundary in <c>WHERE</c> describe two different sequences, so walking the pages
    /// skips or repeats rows — which a single-page order assertion can miss and this cannot.
    /// </summary>
    [Fact]
    public async Task A_projected_paged_read_over_an_unselected_sort_key_returns_each_row_exactly_once()
    {
        var world = await SeededWorldAsync(rowCount: 7);
        AlvoSort[] sort = [new AlvoSort("label", Descending: true)];

        var walked = new List<object?>();
        string? cursor = null;
        do
        {
            var page = await world.Data.QueryAsync(
                new AlvoQuery { Entity = "notes", Sort = sort, Select = ["id"], Limit = 2, After = cursor },
                world.Alice);
            walked.AddRange(page.Items.Select(row => row["id"]));
            cursor = page.NextCursor;
        }
        while (cursor is not null);

        var expected = await world.Data.QueryAsync(new AlvoQuery { Entity = "notes", Sort = sort }, world.Alice);
        walked.ShouldBe([.. expected.Items.Select(row => row["id"])]);
    }

    /// <summary>
    /// A filter term is in <c>WHERE</c>, where both engines resolve the table column and ignore the output
    /// alias — measured, and this is the fact that keeps it measured.
    /// </summary>
    [Fact]
    public async Task A_filter_over_an_unselected_field_matches_the_same_rows_as_without_the_projection()
    {
        var world = await SeededWorldAsync(rowCount: 5);
        var filter = new AlvoComparison("label", AlvoFilterOperator.Eq, "label-0003");

        var unprojected = await world.Data.QueryAsync(
            new AlvoQuery { Entity = "notes", Filter = filter }, world.Alice);
        var projected = await world.Data.QueryAsync(
            new AlvoQuery { Entity = "notes", Filter = filter, Select = ["id"] }, world.Alice);

        projected.Items.Count.ShouldBe(1);
        projected.Items.Select(row => row["id"])
            .ShouldBe([.. unprojected.Items.Select(row => row["id"])]);
    }

    /// <summary>
    /// The one that would have been a bypass rather than a mis-sort. A <c>USING</c> rule of
    /// <c>!has(label)</c> over a NULL-projected <c>label</c> would render <c>NOT("label" IS NOT NULL)</c> →
    /// true and admit every row. <c>WHERE</c> resolves the table column, so it does not — and a compiled
    /// predicate's field references are not enumerable, so there is no survivor set that could have saved
    /// this if it did.
    /// </summary>
    [Fact]
    public async Task A_using_predicate_over_an_unselected_field_admits_exactly_the_rows_it_admits_unprojected()
    {
        var world = await ScopedWorldAsync();

        var unprojected = await world.Data.QueryAsync(new AlvoQuery { Entity = "notes" }, world.Alice);
        var projected = await world.Data.QueryAsync(
            new AlvoQuery { Entity = "notes", Select = ["id"] }, world.Alice);

        projected.Items.Count.ShouldBe(unprojected.Items.Count);
        projected.Items.Select(row => row["id"])
            .OrderBy(id => id?.ToString(), StringComparer.Ordinal)
            .ShouldBe([.. unprojected.Items.Select(row => row["id"]).OrderBy(id => id?.ToString(), StringComparer.Ordinal)]);
    }

    /// <summary>
    /// Every unselected field now goes through <c>dialect.RenderNullProjection(storeType)</c>. Before this PR
    /// only a masked field did, and a mask over every field type was never exercised — so a store type the
    /// cast cannot express would first have shown up on a caller's read.
    /// </summary>
    [Fact]
    public async Task A_projection_over_an_entity_declaring_every_field_type_casts_every_null_it_projects()
    {
        var world = await EveryFieldTypeWorldAsync();

        var page = await world.Data.QueryAsync(
            new AlvoQuery { Entity = AlvoDataFixtures.Vehicle.Name, Select = ["plate"] }, AlvoDataFixtures.Caller);

        page.Items.ShouldNotBeEmpty();
        page.Items[0].Values.ShouldNotContainKey("price");
        page.Items[0].Values.ShouldNotContainKey("due_on");
        page.Items[0].Values.ShouldContainKey("plate");
    }

    /// <summary>The identity case: naming every field must be indistinguishable from naming none.</summary>
    [Fact]
    public async Task A_projection_selecting_every_declared_field_reads_the_same_row_as_no_projection_at_all()
    {
        var world = await SeededWorldAsync(rowCount: 2);
        AlvoSort[] sort = [new AlvoSort("title")];

        var unprojected = await world.Data.QueryAsync(
            new AlvoQuery { Entity = "notes", Sort = sort }, world.Alice);
        var projected = await world.Data.QueryAsync(
            new AlvoQuery { Entity = "notes", Sort = sort, Select = ["id", "title", "body", "label"] },
            world.Alice);

        projected.Items[0].Values.Keys.OrderBy(key => key, StringComparer.Ordinal)
            .ShouldBe([.. unprojected.Items[0].Values.Keys.OrderBy(key => key, StringComparer.Ordinal)]);
    }
```

Two more world builders are needed, both on the `SeededWorldAsync` pattern above:

- **`ScopedWorldAsync()`** — the same `notes` entity, but `Rules = new AccessRules { List = "!has(label)", Get = "true" }`, and seeded so some rows have a null `label` and some do not. The point of the rule is that its truth depends on a field the projection excludes.
- **`EveryFieldTypeWorldAsync()`** — built over `AlvoDataFixtures.Vehicle`, the framework's canonical
  entity with one column of every mapped field type, seeded with one row, queried as
  `AlvoDataFixtures.Caller` (the tenanted, `Admin`-holding identity its scoping expects).

  **No descriptor over this entity exists to copy — this one is hand-written.** Every other consumer
  of `AlvoDataFixtures.Vehicle` uses it as a bare `EntitySchema` (composer, renderer and guard unit
  tests), and `AlvoDataAdversarialTests.BuildFixture` is both private and lossy: its `ToFieldSchema`
  drops `MaxLength`, `Precision` and `Scale`, so it could not reproduce `plate` (MaxLength 32) or
  `price` (Precision 18, Scale 2) — which are exactly the facets this test exists to push a typed
  `NULL` cast through. So: write the `AlvoDescriptor` half by hand and pair it with
  `new SchemaModel([AlvoDataFixtures.Vehicle])` directly.

  Facts that decide the assertions: the entity is named `vehicle` (singular), is
  `TenancyMode.Scoped`, and declares neither `Audit` nor `SoftDelete` — so
  `AlvoManagedColumns.For(Vehicle)` is `{ id, tenant_id }` and `created_at` is an **ordinary**
  nullable field here, not a managed one. Its fields are `id, tenant_id, owner_id, plate, status,
  secret_note, mileage, price, is_public, due_on, created_at`. The descriptor's own field dictionary
  must **exclude** `id` and `tenant_id`, which the framework injects.

- [ ] **Step 2: Run them on SQLite and the reference**

Run: `dotnet test test/MMLib.Alvo.Data.Sqlite.Tests --filter-class MMLib.Alvo.Data.Sqlite.Tests.SqliteAlvoDataProjectionTests`
Run: `dotnet test test/MMLib.Alvo.Tests --filter-class MMLib.Alvo.Tests.Data.InMemoryAlvoDataProjectionTests`
Expected: PASS. If the two sort cases fail, the survivor set in Task 3 Step 6 or Task 4 Step 3 dropped the sort-key exemption.

- [ ] **Step 3: Add the PostgreSQL leg**

Create `test/MMLib.Alvo.Data.PostgreSql.Tests.Integration/PostgreSqlAlvoDataProjectionTests.cs`, copying the subclass shape from `PostgreSqlAlvoDataPagingTests.cs` in the same directory.

- [ ] **Step 4: Run the PostgreSQL leg**

Run: `dotnet test test/MMLib.Alvo.Data.PostgreSql.Tests.Integration --filter-class MMLib.Alvo.Data.PostgreSql.Tests.Integration.PostgreSqlAlvoDataProjectionTests`
Expected: PASS. This needs Docker; it is a ring2 suite.

- [ ] **Step 5: Commit**

```bash
scripts/test-ring0
git add -u && git add test/MMLib.Alvo.Data.PostgreSql.Tests.Integration/PostgreSqlAlvoDataProjectionTests.cs
git commit -m "test(data): the projection agrees across both engines and the reference"
```

---

### Task 6: the statement proof — the column really does leave the read

**Files:**
- Modify: `src/MMLib.Alvo.Testing/Data/AlvoDataStatementTests.cs`

**Interfaces:**
- Consumes: Tasks 1–4.
- Produces: nothing new. Both existing subclasses (`SqliteAlvoDataStatementTests`, ring0; `PostgreSqlAlvoDataStatementTests`, ring2) inherit the new facts automatically.

- [ ] **Step 1: Extend the statement fixture, then write the failing tests**

**The fixture cannot carry these facts as it stands.** `AlvoDataStatementTests`' `notes` entity
declares only `id`, `owner_id`, `tenant_id` and `title` — so `title` is the field to leave
unselected, and there is **no second field to sort by**. Add one nullable `label` field to that
suite's `Fixture(...)`, in the descriptor and the schema both. It is a shared fixture read by every
statement fact in the file, so this is a deliberate change: adding a nullable field changes no
existing statement's `WHERE` or `ORDER BY`, but check the file's other facts still pass before
moving on.

Then add the facts, following the `ShouldContain` style already in that file — it asserts over
captured statements through `IStatementProbe` (`Data`, `Statements`, `ClearStatements()`) plus the
file's own `WhereClauseOf(statement)` and `Token` helpers, **not** through Verify, so **no snapshot
moves here**. The shape to copy, verbatim from the file:

```csharp
        var world = await OwnedNotesAsync();
        world.Probe.ClearStatements();

        await world.Probe.Data.QueryAsync(new AlvoQuery { Entity = Entity }, world.Alice, Token);

        var statement = world.Probe.Statements.ShouldHaveSingleItem();
```

```csharp
    /// <summary>
    /// What is verifiable, and no more. <c>NULL AS col</c> stops the engine reading the column; it does not
    /// make the query proportionally cheaper — the win is real for a wide or TOASTed column and near zero for
    /// a narrow int. So the claim asserted is the one the statement can carry.
    /// </summary>
    [Fact]
    public async Task An_unselected_column_does_not_appear_in_the_emitted_statement()
    {
        // Read with Select = ["label"]. Assert the captured statement contains 'AS "title"' and does not
        // fetch "title" as a plain column. ("notes" is the entity name here, not a field.)
    }

    [Fact]
    public async Task The_row_key_appears_in_the_statement_whatever_the_projection_named()
    {
        // Select = ["title"]. Assert the statement fetches "id" — the cursor is minted from it.
    }

    [Fact]
    public async Task An_unselected_sort_key_appears_in_the_statement_rather_than_as_a_projected_null()
    {
        // Select = ["id"], Sort over the new nullable "label". Assert the statement fetches "label" and
        // does NOT contain 'NULL AS "label"'. The ORDER BY would otherwise resolve to the projected NULL
        // on both engines.
    }

    [Fact]
    public async Task A_read_with_no_projection_emits_the_statement_it_emitted_before()
    {
        // Select = null. Assert no 'AS "' null projection appears for any unmasked field: this PR changes
        // cost, not conduct, for every caller that sends no select.
    }
```

- [ ] **Step 2: Run them and confirm they pass on SQLite**

Run: `dotnet test test/MMLib.Alvo.Data.Sqlite.Tests --filter-class MMLib.Alvo.Data.Sqlite.Tests.SqliteAlvoDataStatementTests`
Expected: PASS.

- [ ] **Step 3: Run ring1 and commit**

```bash
scripts/test-ring1
git add -u
git commit -m "test(data): pin that an unselected column leaves the statement and a sort key does not"
```

---

### Task 7: the alias grammar in the parser

**Files:**
- Modify: `src/MMLib.Alvo/Api/Internal/QueryStringParser.cs`
- Modify: `src/MMLib.Alvo/Api/Internal/QueryViolations.cs`
- Modify: `test/MMLib.Alvo.Api.Tests/QueryStringParserTests.cs`
- Modify: `test/MMLib.Alvo.Api.Tests/QueryStringParserPropertyTests.cs`

**Interfaces:**
- Produces:
  - `internal sealed record ProjectedField(string Key, string Source)` in the `MMLib.Alvo.Api.Internal` namespace, declared in `QueryStringParser.cs` beside `ParsedListQuery`.
  - `ParsedListQuery(AlvoQuery Query, IReadOnlyList<ProjectedField>? Select)` — the second member's type changes from `IReadOnlyList<string>?`.
  - `QueryViolations.MalformedSelectAlias()`, `QueryViolations.CollidingProjectionKey()`, `QueryViolations.ProjectionTooWide(int maxKeys)`.

- [ ] **Step 1: Write the failing tests**

In `QueryStringParserTests.cs`, following the existing cases' arrange/assert style:

```csharp
    [Fact]
    public void An_alias_returns_the_source_field_under_the_requested_key()
    {
        // ?select=label:name -> ParsedListQuery.Select is [("label", "name")]
        // and Query.Select is ["name"] — the port never sees the alias.
    }

    [Fact]
    public void A_field_named_without_an_alias_keys_itself()
    {
        // ?select=name -> [("name", "name")]
    }

    [Fact]
    public void An_alias_over_a_hidden_source_is_refused_exactly_as_an_undeclared_one_is()
    {
        // ?select=label:secret and ?select=label:nosuchfield both yield 'unavailable-field'.
    }

    [Theory]
    [InlineData(":name")]
    [InlineData("name:")]
    [InlineData("Label:name")]     // grammar: must be ^[a-z][a-z0-9_]{0,62}$
    [InlineData("1label:name")]
    [InlineData("la-bel:name")]
    [InlineData("limit:name")]     // a reserved name is not an alias
    public void A_malformed_or_reserved_alias_is_refused(string entry) { /* 'malformed-select-alias' */ }

    [Theory]
    [InlineData("name,name:make")]   // two sources for the key 'name'
    [InlineData("a:name,a:make")]
    [InlineData("id:name")]          // collides with a managed column that always survives
    public void Two_sources_for_one_response_key_are_refused(string value) { /* 'colliding-projection-key' */ }

    [Fact]
    public void A_repeated_identical_entry_dedupes_rather_than_being_refused()
    {
        // ?select=name,name -> [("name","name")], no violation. Pre-PR behaviour, preserved on purpose.
    }

    [Fact]
    public void A_projection_naming_more_keys_than_the_entity_declares_fields_is_refused()
    {
        // An entity with N fields: N+1 aliases over one column yields 'projection-too-wide'.
        // This is the amplification bound aliases make necessary — see the design's section 2.4.
    }
```

- [ ] **Step 2: Run them and confirm they fail**

Run: `dotnet test test/MMLib.Alvo.Api.Tests --filter-class MMLib.Alvo.Api.Tests.QueryStringParserTests`
Expected: FAIL — the alias forms are currently read as field names and refused as unavailable.

- [ ] **Step 3: Add the three violation codes**

In `QueryViolations.cs`, after `EmptySelect`, matching its four-argument shape `(pointer, code, message, fix)`:

```csharp
    /// <summary>
    /// The refusal for a projection entry that is not <c>field</c> or <c>alias:field</c>, or whose alias is
    /// not shaped like a field name.
    /// </summary>
    /// <remarks>
    /// <b>An alias must match the field-name grammar</b> (<c>^[a-z][a-z0-9_]{0,62}$</c>) and must not be one
    /// of the reserved names. A deliberate narrowing of PostgREST, which admits an arbitrary alias: an alias
    /// is a field name <em>in the response</em>, and an agent reading the body should not have to tell a real
    /// field from caller-supplied text. The reserved-name half is consistency rather than necessity — an
    /// alias is never a query key, so it creates no ambiguity — but a response key no descriptor is allowed
    /// to declare should not be reachable by renaming.
    /// </remarks>
    internal static AlvoViolation MalformedSelectAlias() => new(
        ReservedQueryKeys.Select,
        "malformed-select-alias",
        "A projection entry is not a field name or an 'alias:field' pair.",
        $"Write select=name or select=label:name; an alias is lower snake_case, at most 63 characters, and "
        + $"none of {ReservedQueryKeys.AsList}.");

    /// <summary>The refusal for one response key claimed by two different fields.</summary>
    /// <remarks>
    /// Refused rather than resolved: two sources for one key is a request with no correct answer, and
    /// answering with either would silently drop a field the caller asked for. A managed column counts —
    /// <c>select=id:name</c> would put two different values under <c>id</c>, because <c>id</c> survives every
    /// projection. A repeated <em>identical</em> entry is not this: it dedupes, as it always has.
    /// </remarks>
    internal static AlvoViolation CollidingProjectionKey() => new(
        ReservedQueryKeys.Select,
        "colliding-projection-key",
        "Two projected fields would answer under the same response key.",
        "Give each projected field its own key, and avoid aliasing onto a framework-managed column name.");

    /// <summary>The refusal for a projection naming more keys than the entity has fields.</summary>
    /// <remarks>
    /// <b>The bound aliases make necessary.</b> Before aliases the projection was self-bounding — it named
    /// declared fields and duplicates collapsed — so a response could never carry more keys than the entity
    /// has fields. An alias can name one column under arbitrarily many keys, leaving only the transport's URL
    /// limit in the way. The bound is derived rather than chosen: a response with more keys than the entity
    /// has fields is a duplication request, not a read.
    /// </remarks>
    /// <param name="maxKeys">How many fields the entity declares.</param>
    internal static AlvoViolation ProjectionTooWide(int maxKeys) => new(
        ReservedQueryKeys.Select,
        "projection-too-wide",
        "The projection names more keys than this entity has fields.",
        $"Name at most {maxKeys} keys; aliasing one field under many keys returns the same value repeatedly.");
```

- [ ] **Step 4: Add `ProjectedField` and rewrite `ReadSelect`**

In `QueryStringParser.cs`, beside `ParsedListQuery`:

```csharp
/// <summary>
/// One entry of a parsed projection: the response key, and the declared field its value comes from.
/// </summary>
/// <remarks>
/// The two are equal unless the caller wrote an alias. <b>Only <see cref="Source"/> reaches the port</b> —
/// <c>AlvoQuery.Select</c> carries declared field names, so the port's contract that these are the entity's
/// own names stays literally true and the alias never leaves the HTTP layer.
/// </remarks>
/// <param name="Key">The key this field answers under in the response.</param>
/// <param name="Source">The declared field the value is read from.</param>
internal sealed record ProjectedField(string Key, string Source);
```

Change `ParsedListQuery`'s second parameter to `IReadOnlyList<ProjectedField>? Select` and update its XML doc. Change the `_select` field to `List<ProjectedField>?`. Replace `ReadSelect` and `AddOnce`:

```csharp
        private void ReadSelect(string value)
        {
            if (value.Length == 0)
            {
                Add(QueryViolations.EmptySelect());
                return;
            }

            var projected = new List<ProjectedField>();
            foreach (var entry in value.Split(','))
            {
                if (!TryAddProjectedField(entry, projected))
                {
                    return;
                }
            }

            _select = projected;
        }

        /// <summary>
        /// Reads one <c>field</c> or <c>alias:field</c> entry. The <em>source</em> is resolved through the
        /// same resolver every other field name goes through, which is what makes an alias unable to reach a
        /// field the caller may not read.
        /// </summary>
        private bool TryAddProjectedField(string entry, List<ProjectedField> projected)
        {
            if (!TrySplitProjectedField(entry, out var key, out var source))
            {
                Add(QueryViolations.MalformedSelectAlias());
                return false;
            }

            if (_scope.Fields.Resolve(source) is not { } declared)
            {
                Add(QueryViolations.UnavailableField(ReservedQueryKeys.Select));
                return false;
            }

            return TryClaimKey(key ?? declared.Name, declared.Name, projected);
        }

        /// <summary>
        /// Splits <c>alias:field</c>, or reports the whole entry as the field with no alias. An entry with
        /// more than one colon, an empty half, or an alias outside the field-name grammar is malformed.
        /// </summary>
        private static bool TrySplitProjectedField(string entry, out string? key, out string source)
        {
            key = null;
            source = entry;

            var colon = entry.IndexOf(':', StringComparison.Ordinal);
            if (colon < 0)
            {
                return entry.Length > 0;
            }

            if (entry.IndexOf(':', colon + 1) >= 0)
            {
                return false;
            }

            key = entry[..colon];
            source = entry[(colon + 1)..];
            return source.Length > 0 && IsAliasShaped(key);
        }

        /// <summary>
        /// Whether <paramref name="alias"/> is shaped like a declared field name
        /// (<c>^[a-z][a-z0-9_]{0,62}$</c>) and is not a reserved name.
        /// </summary>
        private static bool IsAliasShaped(string alias) =>
            alias.Length is > 0 and <= 63
            && char.IsAsciiLetterLower(alias[0])
            && alias.All(character =>
                char.IsAsciiLetterLower(character) || char.IsAsciiDigit(character) || character == '_')
            && !ReservedQueryKeys.IsReserved(alias);

        /// <summary>
        /// Claims one response key. A repeated identical entry dedupes; a second <em>source</em> for a key
        /// already taken is refused, because there is no correct answer to give; and a key beyond the
        /// entity's field count is refused as the projection's width bound.
        /// </summary>
        /// <remarks>
        /// <b>The bound is charged here, on each newly claimed key, and that is the whole of whether it
        /// works.</b> <c>ChargeTheConjunction</c>'s remark records the measured incident from the filter
        /// side — "a budget spent after the tree is assembled does not bound the tree" — so a cap tested
        /// after the parse loop would leave the entire amplification payable before the 422. Charging on the
        /// <em>distinct</em> key rather than the raw entry count is what keeps a repeat deduping: a repeat
        /// claims nothing and so costs nothing, and <c>?select=id,id,id,id,id,id</c> on a five-field entity
        /// is still one key.
        /// </remarks>
        private bool TryClaimKey(string key, string source, List<ProjectedField> projected)
        {
            var claimed = projected.FirstOrDefault(field => string.Equals(field.Key, key, StringComparison.Ordinal));
            if (claimed is not null)
            {
                if (string.Equals(claimed.Source, source, StringComparison.Ordinal))
                {
                    return true;
                }

                Add(QueryViolations.CollidingProjectionKey());
                return false;
            }

            if (projected.Count == entity.Fields.Count)
            {
                Add(QueryViolations.ProjectionTooWide(entity.Fields.Count));
                return false;
            }

            projected.Add(new ProjectedField(key, source));
            return true;
        }
```

**A managed-column collision needs one more thing:** `select=id:name` must be refused even though `id` was never claimed by an entry, because `id` survives every projection (Task 3). Seed the claim list with the framework-managed columns that will survive, or check `AlvoManagedColumns.For(entity).Contains(key)` in `TryClaimKey` before the lookup. Prefer the explicit check — a seeded claim would also refuse the legitimate `select=id`.

- [ ] **Step 5: Use the membership test that already exists**

No new member is needed. `ReservedQueryKeys` already has
`internal static bool IsReserved(string key) => _reserved.Contains(key);` over a frozen set of all
eight names, and `EnsureNoneIsShadowed` reads the same set. `IsAliasShaped` calls it:

```csharp
            && !ReservedQueryKeys.IsReserved(alias);
```

- [ ] **Step 6: Carry the sources into `AlvoQuery`**

In `TryBuild`:

```csharp
            var query = new AlvoQuery
            {
                Entity = entity.Name,
                Filter = Conjoin(),
                Sort = _sort,
                Limit = _limit ?? options.DefaultPageSize,
                Offset = _offset,
                After = _after,
                Select = _select?.Select(field => field.Source).Distinct(StringComparer.Ordinal).ToList(),
            };
```

`Distinct` matters: `select=a:name,b:name` is two keys over one source, and the port must be asked for that source once.

- [ ] **Step 7: Extend the property test**

`QueryStringParserPropertyTests.cs` already generates query strings with CsCheck. Add the alias form to whatever generator produces `select` values, and assert the standing invariant that class already asserts — a parse either succeeds or reports at least one violation, and never throws.

- [ ] **Step 8: Update the existing projection test that the type change breaks**

`QueryStringParserTests` already has one, and it is not in the file list above because it does not
need new behaviour — only the new type:

```csharp
    [Fact]
    public void A_projection_keeps_the_order_the_request_named_and_drops_a_duplicate()
    {
        TryParse("select=year,make,year", out var parsed, out var violations).ShouldBeTrue(Because(violations));

        parsed!.Select.ShouldBe(["year", "make"]);   // now a list of ProjectedField
    }
```

Assert over `parsed!.Select.Select(field => field.Key)` and add a second assertion over `.Source`, so
the test now says the two lists are what they should be rather than only one of them.

**Fixture facts for every test in this task**, so the cases are written against what exists: the
parser suite's `_vehicles` entity declares **eleven** fields — `id, make, year, color, notes, price,
passed, inspected_on, serviced_at, owner_id, secret` — and `_masked` is `{ "secret" }`. So:
`select=label:make` is the alias case; `select=nosuchfield` and `select=label:secret` are the two
refusals that must read identically; `select=id:make` trips the managed-column collision, because
`id` is declared *and* in `AlvoManagedColumns.For(_vehicles)`; and `projection-too-wide` needs
**twelve** distinct keys, e.g. twelve aliases over `make`. The parser is reached through the file's
own `TryParse(queryString, out parsed, out violations)` wrapper and `OnlyViolation(queryString)`;
new codes go in as `[InlineData]` rows on `A_refused_query_string_carries_the_code_that_names_the_mistake`.

- [ ] **Step 9: Run and commit**

Run: `dotnet test test/MMLib.Alvo.Api.Tests`
Expected: PASS. `DataApiPage.From` will not compile against the new `ParsedListQuery` — that is Task 8; if the build blocks here, do Task 8's Step 3 first and commit the two together.

```bash
scripts/test-ring0
git add -u
git commit -m "feat(api): projection aliases, select=label:name, with the bound they make necessary"
```

---

### Task 8: the response renders keys, and `Project` is deleted

**Files:**
- Modify: `src/MMLib.Alvo/Api/Internal/DataApiPage.cs`
- Modify: `src/MMLib.Alvo/Api/Internal/DataApiEndpoints.cs` (the one `MapList` call site)
- Modify: `test/_shared/api/DataApiEngineTests.cs`

**Interfaces:**
- Consumes: `ProjectedField`, `ParsedListQuery` (Task 7); `AlvoQuery.Select` honoured by the port (Tasks 3–4).
- Produces: `DataApiPage.From(AlvoPage page, IReadOnlyList<ProjectedField>? projection)`.

- [ ] **Step 1: Write the failing tests**

In `test/_shared/api/DataApiEngineTests.cs`, at the wire level. The idiom is
`world.SendAsync(HttpMethod.Get, "/api/vehicles?…", _admin)` then `await response.ReadItemsAsync()`
(an `IReadOnlyList<JsonObject>`) — so a key-set assertion is `items[0].Count` and
`items[0].ContainsKey("label")`. `SeedVehiclesAsync(world)` seeds `vin, plate, make, model, year,
owner_id` from `examples/vehicle-registry/vehicles.alvo.json`, which is where these field names come
from:

```csharp
    [Fact]
    public async Task An_alias_returns_the_value_under_the_requested_key()
    {
        // GET ?select=label:make -> items[0] has "label" and not "make".
    }

    [Fact]
    public async Task A_projection_hides_the_framework_managed_columns_it_did_not_name()
    {
        // GET ?select=make -> items[0] has exactly one key. The port returned id and the audit columns
        // (its contract), and the response must not show them unless asked. This is the pre-PR wire shape.
    }

    [Fact]
    public async Task A_projection_naming_a_managed_column_returns_it()
    {
        // GET ?select=id,make -> both keys present.
    }

    [Fact]
    public async Task The_response_keys_appear_in_the_order_the_request_named_them()
    {
        // GET ?select=model,make -> the JSON object's keys are model then make.
    }
```

Add one test pinning the two key-set authorities together — put it in `test/MMLib.Alvo.Api.Tests/QueryStringParserTests.cs`, since it is a statement about the parser's output:

```csharp
    /// <summary>
    /// <c>Render</c> and the port's <c>Select</c> are two lists that must agree, and <c>Render</c>'s "emit
    /// nothing for a source the row does not carry" behaviour would hide a divergence rather than fail on it.
    /// This is what fails instead.
    /// </summary>
    [Theory]
    [InlineData("make")]
    [InlineData("label:make,model")]
    [InlineData("a:make,b:make")]
    public void Every_projected_source_is_asked_of_the_port_and_nothing_else_is(string select)
    {
        // parsed.Select.Select(f => f.Source).ToHashSet() must equal parsed.Query.Select.ToHashSet().
    }
```

- [ ] **Step 2: Run them and confirm they fail**

Run: `dotnet test test/MMLib.Alvo.Api.Tests`
Expected: FAIL/no-compile — `DataApiPage.From` still takes `IReadOnlyList<string>?`.

- [ ] **Step 3: Replace `Project` with `Render`**

In `DataApiPage.cs`:

```csharp
    /// <summary>Wraps one page the port returned, rendered to the keys the request asked for.</summary>
    /// <param name="page">The page to render.</param>
    /// <param name="projection">The response keys and their sources, or <see langword="null"/> for the row as the port returned it.</param>
    internal static DataApiPage From(AlvoPage page, IReadOnlyList<ProjectedField>? projection)
    {
        ArgumentNullException.ThrowIfNull(page);
        return new DataApiPage
        {
            Items = [.. page.Items.Select(row => Render(row.Values, projection))],
            Next = page.NextCursor,
            Count = page.TotalCount,
        };
    }

    /// <summary>
    /// Renders one row as the response's own key list: each requested key, in the order the request named
    /// it, carrying the value of the field it names.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>This is not the projection — the port applies that.</b> It renames and orders, and it is the only
    /// layer that can, because an alias is an HTTP concern the port is deliberately not told about (see
    /// <c>ProjectedField</c>).
    /// </para>
    /// <para>
    /// <b>It also drops what the port had to keep.</b> <c>IAlvoData</c>'s contract makes a returned record
    /// carry every framework-managed column whatever the caller selected — <c>id</c> because a keyset cursor
    /// is minted from it — and the response must not show them unless the caller asked. So the port's key set
    /// and the response's are two different lists, and this renders the second from the first.
    /// </para>
    /// <para>
    /// A source the row does not carry emits nothing rather than a <see langword="null"/>: the port omits
    /// nothing a caller may read, so an absent key means the port chose not to return it and this layer must
    /// not manufacture the field back into existence.
    /// </para>
    /// </remarks>
    private static IReadOnlyDictionary<string, object?> Render(
        IReadOnlyDictionary<string, object?> values, IReadOnlyList<ProjectedField>? projection)
    {
        if (projection is null)
        {
            return values;
        }

        var rendered = new Dictionary<string, object?>(projection.Count, StringComparer.Ordinal);
        foreach (var field in projection)
        {
            if (values.TryGetValue(field.Source, out var value))
            {
                rendered[field.Key] = value;
            }
        }

        return rendered;
    }
```

The `MapList` call site needs no change — it already reads `DataApiPage.From(page, request.Select)` and `request.Select`'s type is what moved.

- [ ] **Step 4: Run and commit**

Run: `dotnet test test/MMLib.Alvo.Api.Tests`
Run: `dotnet test test/MMLib.Alvo.Host.Tests`
Expected: PASS.

```bash
scripts/test-ring0
git add -u
git commit -m "feat(api): the list response renders projected keys, and DataApiPage.Project is gone"
```

---

### Task 9: the documentation that currently says the opposite

**Files:**
- Modify: `src/MMLib.Alvo/Api/Internal/DataApiParameters.cs`
- Modify: `src/MMLib.Alvo.Abstractions/Data/IAlvoData.cs`
- Modify: `docs/architecture/data-api.md`
- Modify: `CHANGELOG.md`
- Modify: `test/.../OpenApiDocumentTests.The_document_is_stable.verified.txt` (accepted, not hand-written)

**Interfaces:** none.

- [ ] **Step 1: Rewrite the OpenAPI `select` description**

`DataApiParameters.Select` currently asserts what this PR falsifies — *"It narrows the response only — the read still fetches the whole row — so it saves bandwidth to the caller and nothing at the database."* Replace:

```csharp
        Description =
            "Comma-separated field names to return, in the order named, each optionally renamed as "
            + "`alias:field`. It narrows the read as well as the response: a field the projection does not "
            + "name is not read from the row, and framework-managed columns and any field named in `order` "
            + "are read regardless — ordering is not expressible over a column the statement did not read. "
            + "A field the caller may not read is refused exactly as an undeclared one is. An alias is lower "
            + "snake_case, and two fields cannot answer under one key.",
        Schema = new OpenApiSchema { Type = JsonSchemaType.String },
        Example = JsonValue.Create("label:make,model"),
```

- [ ] **Step 2: Amend `IAlvoData`'s returned-key-set contract**

The paragraph that says a returned record *"carries every non-hidden field the schema declares for that entity, including framework-managed columns"* becomes false under a projection. Amend it rather than leaving it to be falsified — add, immediately after that sentence:

```
///     <b><see cref="AlvoQuery.Select"/> is the one thing that narrows this key set, and it never narrows
///     it below the framework-managed columns.</b> A projected read returns the fields it named plus every
///     column <c>AlvoManagedColumns.For</c> reports for the entity, and plus every field named in
///     <see cref="AlvoQuery.Sort"/> — a driver cannot order by a column it did not read. Masking is still
///     the only thing that removes a field the caller asked for.
```

- [ ] **Step 3: Update the architecture doc**

In `docs/architecture/data-api.md`:
- `:155`'s grammar row gains the alias form: `select=a,b`, `select=alias:a`.
- `:177`'s paragraph is now wrong end to end. Replace it with what ships: the projection reaches the `SELECT` list as a typed `NULL` per unselected column (with the EF `FromSql` reason), the survivor set and the measured `ORDER BY` reason for the sort-key exemption, and the width bound. Add the alias refusals to whatever list of refusals that document keeps.
- Its "Alternatives rejected" section holds #117's and #111's old entries; move them to what shipped, and leave #104's `Link` header entry alone.

- [ ] **Step 4: Write the changelog entries**

In `CHANGELOG.md` under `## [Unreleased]`. `AlvoQuery.Select` is **additive**, so it belongs under `### Added`, not `### Changed (breaking)` — but the response of an existing `?select=` request is unchanged, and that is worth stating so a reader does not go looking for a break. Cover: the new member and its guard; that `select` now narrows the read; the alias form; the four alias refusals and the width bound; the `IAlvoData` contract amendment; and `DataApiPage.Project`'s removal (internal, so not a break).

- [ ] **Step 5: Accept the OpenAPI baseline**

Run: `dotnet test test/MMLib.Alvo.Api.Tests --filter-class ...OpenApiDocumentTests`
Expected: FAIL with a received file. Diff it: the only change must be the `select` parameter's description and example. Accept it and delete the received file.

- [ ] **Step 6: Run ring1 and commit**

```bash
scripts/test-ring1
git add -u
git commit -m "docs(api): select narrows the read, and the contract paragraphs say so"
```

The turn gate will block on the moved OpenAPI baseline and ask for `alvo-snapshot-judge`.

---

## Before the PR

- [ ] `scripts/test-ring2` — green. This is the first run of the PostgreSQL projection leg outside Task 5.
- [ ] Dispatch a **C# reviewer subagent** as the local stand-in for `/code-review medium` (that command is user-only), and a **security reviewer subagent** for `/security-review`, paired with the `alvo-security-core-review` checklist. The second is not optional: this PR adds a public port member consumed by the read path's field masking, extends an authorization refusal, and NULL-projects columns the tenant scope and `USING` predicates reference.
- [ ] Dispatch `alvo-plan-guard`.
- [ ] Build the PR report via the `alvo-pr-report` skill.
- [ ] Open the PR with `closes #117, closes #111` — **the keyword repeated per issue**, because GitHub applies it only to the first number otherwise. Add the `needs-deep-review` label.
- [ ] Comment on **#118** with the two facts §5 of the design records: PR-D's pinned resolve count did not move because `select` stays off `GET /{entity}/{id}`, and the `ScopeGate` premise correction is confirmed independently. Leave it open — it is a maintainer-scoped judgement.
- [ ] Do **not** touch `docs/PLAN.md`. PR-F does not complete an F4 line, and a PR must not mark its own phase done.
