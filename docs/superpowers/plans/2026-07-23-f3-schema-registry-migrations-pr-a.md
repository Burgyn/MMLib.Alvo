# Schema Registry + Migrations — PR-A (code-first) Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Turn an Alvo project descriptor (JSON file) into a real database schema on SQLite and PostgreSQL through one declarative-diff engine, with destructive-change guardrails and a dry-run, idempotent re-apply.

**Architecture:** An EF-free core owns the descriptor model, the driver-agnostic `SchemaModel`, and the migration orchestration behind ports. EF Core lives entirely behind an `ISchemaMigrator` implementation in provider packages — it reuses EF's `IMigrationsModelDiffer` (diff) and per-provider `IMigrationsSqlGenerator` (dialect DDL, incl. SQLite table-rebuild), while rename intent, destructive classification and the plan shape stay in our own model. "Current state" for the diff is the last-applied descriptor snapshot (persisted in a small `alvo.*` system table); introspection serves baseline adoption and drift detection.

**Tech Stack:** .NET 10, C# latest, xUnit v3 on Microsoft.Testing.Platform, Shouldly, NSubstitute, Verify, CsCheck, NetArchTest, EF Core 10 (`Microsoft.EntityFrameworkCore.Relational`, `.Sqlite`, `Npgsql.EntityFrameworkCore.PostgreSQL`), Testcontainers (PostgreSQL), System.Text.Json (source-generated).

## Global Constraints

- Target framework `net10.0`; do NOT re-declare `TargetFramework`/`Nullable`/`ImplicitUsings`/`LangVersion` per project (inherited from `Directory.Build.props` — the convention test enforces this).
- Central Package Management only — every package version goes in `Directory.Packages.props`, never inline on a `PackageReference`.
- `MMLib.Alvo.Abstractions` depends on nothing; the core `MMLib.Alvo` depends only on `Abstractions`; `Data.*` packages depend only on `Abstractions` (+ their EF driver); **core must never reference EntityFrameworkCore** (enforced by an arch test).
- Feature-first namespaces without `.Abstractions`: ports live in `MMLib.Alvo.Schema` / `MMLib.Alvo.Migrations`; `MMLib.Alvo.Abstractions.csproj` sets `<RootNamespace>MMLib.Alvo</RootNamespace>`. Assembly names (NuGet ids) are unchanged.
- DI/builder extension methods live in namespace `Microsoft.Extensions.DependencyInjection`.
- Every shipped (`IsPackable` != false) `src` project has a matching `*.Tests` project that `ProjectReference`s it, and a committed `PublicApi.<name>.verified.txt` baseline.
- Mechanism code is organized by capability (feature namespace + `Internal/`), NOT vertical-slice per operation — there are no HTTP endpoints in this PR.
- Conventional Commits for every commit message; end each commit body with `Claude-Session: https://claude.ai/code/session_014MrAzim5LpauyTPTBUyc4u`.
- Rings: run `scripts/test-ring0` after each task; `scripts/test-ring2` before the PR.

---

## File Structure

**`src/MMLib.Alvo.Abstractions/`** (ports + pure model; `RootNamespace` = `MMLib.Alvo`)
- `Schema/SchemaModel.cs`, `EntitySchema.cs`, `FieldSchema.cs`, `IndexSchema.cs`, `RefSchema.cs`, `FieldType.cs`, `EntityStorage.cs`, `TenancyMode.cs`, `OnDelete.cs` — namespace `MMLib.Alvo.Schema`
- `Schema/ISchemaRegistry.cs`, `Schema/ISchemaIntrospector.cs` — namespace `MMLib.Alvo.Schema`
- `Migrations/ISchemaMigrator.cs`, `MigrationPlan.cs`, `MigrationStep.cs`, `SchemaChange.cs`, `SchemaChangeKind.cs`, `MigrationOptions.cs`, `MigrationResult.cs`, `IDescriptorSource.cs` — namespace `MMLib.Alvo.Migrations`
- `IAlvoBuilder.cs`, `AlvoOptions.cs`, `AlvoMode.cs` — namespace `MMLib.Alvo`

**`src/MMLib.Alvo/`** (core; EF-free)
- `Descriptor/AlvoDescriptor.cs` (+ nested descriptor DTOs), `Descriptor/DescriptorJsonContext.cs`, `Descriptor/DescriptorParser.cs`, `Descriptor/DescriptorToSchemaMapper.cs` — namespace `MMLib.Alvo.Descriptor`
- `Schema/SchemaRegistry.cs`, `Schema/Setup.cs` — namespace `MMLib.Alvo.Schema`
- `Migrations/SchemaMigrationRunner.cs`, `Migrations/Setup.cs`, `Migrations/Internal/DestructiveChangeGuard.cs`, `Migrations/Internal/SystemSchemaInitializer.cs`, `Migrations/Internal/AppliedSchemaStore.cs` — namespace `MMLib.Alvo.Migrations`
- `AlvoServiceCollectionExtensions.cs`, `AlvoBuilderExtensions.cs`, `Internal/AlvoBuilder.cs`, `Internal/AlvoProviderValidation.cs` — namespaces `Microsoft.Extensions.DependencyInjection` (extensions) and `MMLib.Alvo` (`Internal/`)

**`src/MMLib.Alvo.Data.EntityFrameworkCore/`** (EF base; refs `Microsoft.EntityFrameworkCore.Relational`)
- `EfCoreSchemaMigrator.cs`, `EfCoreSchemaIntrospector.cs`, `Internal/DescriptorModelBuilder.cs`, `Internal/RenamePrePass.cs`, `Internal/DestructiveScan.cs` — namespace `MMLib.Alvo.Data.EntityFrameworkCore`

**`src/MMLib.Alvo.Data.Sqlite/`** / **`src/MMLib.Alvo.Data.PostgreSql/`** (thin wiring)
- `AlvoSqliteBuilderExtensions.cs` / `AlvoPostgreSqlBuilderExtensions.cs` — namespace `Microsoft.Extensions.DependencyInjection`
- `Internal/SqliteMigrationServices.cs` / `Internal/PostgreSqlMigrationServices.cs`

**`src/MMLib.Alvo.Testing/`** — add `Migrations/SchemaMigratorContractTests.cs` (abstract), `Migrations/InMemorySchemaMigrator.cs` fake.

**Test projects:** `test/MMLib.Alvo.Tests`, `test/MMLib.Alvo.Data.EntityFrameworkCore.Tests`, `test/MMLib.Alvo.Data.Sqlite.Tests`, `test/MMLib.Alvo.Data.PostgreSql.Tests.Integration` (Testcontainers), and additions to `test/MMLib.Alvo.Abstractions.Tests`.

**Fixtures:** `examples/vehicle-registry/vehicles.alvo.json` (new).

---

## Task 0: Rename mechanism spike (de-risk before anything else)

**Purpose:** Prove that EF's `IMigrationsModelDiffer` + `IMigrationsSqlGenerator`, driven by a runtime-built `IModel`, can produce a **data-preserving column rename** (not drop+add) on **both** SQLite and PostgreSQL. This is the single largest technical risk (spec assumption 4). It is a throwaway spike; its output is a decision, not shipped code.

**Files:**
- Create (throwaway): `spikes/rename-spike/` console project (NOT added to `MMLib.Alvo.slnx`; deleted at task end).

- [ ] **Step 1: Scaffold a throwaway console app** referencing `Microsoft.EntityFrameworkCore.Sqlite` and `Npgsql.EntityFrameworkCore.PostgreSQL`.

```bash
mkdir -p spikes/rename-spike && cd spikes/rename-spike
dotnet new console
dotnet add package Microsoft.EntityFrameworkCore.Sqlite
dotnet add package Npgsql.EntityFrameworkCore.PostgreSQL
```

- [ ] **Step 2: Build two `IModel`s (before/after a rename) via a conventionless `ModelBuilder`, diff, and generate SQL.** Prove a rename appears as `RenameColumnOperation`, not Drop+Add.

```csharp
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design.Internal;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata.Conventions;
using Microsoft.EntityFrameworkCore.Migrations;

static IModel Build(string columnName)
{
    var b = new ModelBuilder(SqliteConventionSetBuilder.Build()); // repeat with Npgsql conventions
    b.Entity("vehicles", e =>
    {
        e.Property<Guid>("id"); e.HasKey("id");
        e.Property<string>(columnName);
        e.ToTable("vehicles");
    });
    return b.FinalizeModel();
}

// Use each provider's DbContext to resolve IMigrationsModelDiffer + IMigrationsSqlGenerator,
// call differ.GetDifferences(oldModel.GetRelationalModel(), newModel.GetRelationalModel()),
// then prepend a RenameColumnOperation for the known rename, then sqlGenerator.Generate(ops, newModel).
```

- [ ] **Step 3: Run against SQLite and a Testcontainers PostgreSQL**, seed one row before the rename, apply the generated SQL, assert the row survived under the new column name on both engines.

- [ ] **Step 4: Record the outcome** in the plan's execution notes and delete the spike.

Run: `dotnet run` (both engines). Expected: row preserved on both; rename emitted as a rename (SQLite via table-rebuild, PostgreSQL via `ALTER TABLE … RENAME COLUMN`).

- [ ] **Step 5: Decision gate.**
  - **Pass** → proceed with the plan as written (EF differ + our rename pre-pass).
  - **Fail** (differ output unusable for renames even with the pre-pass) → switch Task 9 to the own-semantic-diff variant: we build the `List<MigrationOperation>` ourselves from our `SchemaChange` list and only call `IMigrationsSqlGenerator.Generate`. The `ISchemaMigrator` contract and every other task are unaffected.

```bash
rm -rf spikes/rename-spike
```

No commit (throwaway).

---

## Task 1: Package scaffolding + the EF-shield architecture test

**Files:**
- Create projects: `src/MMLib.Alvo/MMLib.Alvo.csproj`, `src/MMLib.Alvo.Data.EntityFrameworkCore/…csproj`, `src/MMLib.Alvo.Data.Sqlite/…csproj`, `src/MMLib.Alvo.Data.PostgreSql/…csproj`
- Create test projects: `test/MMLib.Alvo.Tests/…csproj`, `test/MMLib.Alvo.Data.EntityFrameworkCore.Tests/…csproj`, `test/MMLib.Alvo.Data.Sqlite.Tests/…csproj`, `test/MMLib.Alvo.Data.PostgreSql.Tests.Integration/…csproj`
- Modify: `MMLib.Alvo.slnx`, `Directory.Packages.props`, `src/MMLib.Alvo.Abstractions/MMLib.Alvo.Abstractions.csproj`
- Test: `test/MMLib.Alvo.Abstractions.Tests/ArchitectureTests.cs`

**Interfaces:**
- Produces: the four `src` assemblies and their references; `RootNamespace=MMLib.Alvo` on Abstractions.

- [ ] **Step 1: Create the projects via CLI and strip inherited props.**

```bash
cd /Users/martiniak/Developer/GitHub/Burgyn/MMLib.Alvo
dotnet new classlib -o src/MMLib.Alvo
dotnet new classlib -o src/MMLib.Alvo.Data.EntityFrameworkCore
dotnet new classlib -o src/MMLib.Alvo.Data.Sqlite
dotnet new classlib -o src/MMLib.Alvo.Data.PostgreSql
```
Delete the generated `Class1.cs` files and remove every `<PropertyGroup>` the template added (TargetFramework, Nullable, ImplicitUsings, LangVersion) so only inherited props remain.

- [ ] **Step 2: Wire project references and the Abstractions RootNamespace.**

`src/MMLib.Alvo/MMLib.Alvo.csproj`:
```xml
<Project Sdk="Microsoft.NET.Sdk">
  <ItemGroup>
    <ProjectReference Include="../MMLib.Alvo.Abstractions/MMLib.Alvo.Abstractions.csproj" />
  </ItemGroup>
</Project>
```
Each `Data.*` references `Abstractions`; `Data.Sqlite` and `Data.PostgreSql` also reference `Data.EntityFrameworkCore`. Add to `src/MMLib.Alvo.Abstractions/MMLib.Alvo.Abstractions.csproj`:
```xml
<PropertyGroup>
  <RootNamespace>MMLib.Alvo</RootNamespace>
</PropertyGroup>
```

- [ ] **Step 3: Add EF package versions to CPM and reference them.**

`Directory.Packages.props` (add):
```xml
<PackageVersion Include="Microsoft.EntityFrameworkCore.Relational" Version="10.0.0" />
<PackageVersion Include="Microsoft.EntityFrameworkCore.Sqlite" Version="10.0.0" />
<PackageVersion Include="Npgsql.EntityFrameworkCore.PostgreSQL" Version="10.0.0" />
<PackageVersion Include="Testcontainers.PostgreSql" Version="4.0.0" />
```
`Data.EntityFrameworkCore` references `Microsoft.EntityFrameworkCore.Relational`; `Data.Sqlite` references `Microsoft.EntityFrameworkCore.Sqlite`; `Data.PostgreSql` references `Npgsql.EntityFrameworkCore.PostgreSQL`.

- [ ] **Step 4: Register all eight projects in the solution.**

```bash
dotnet sln MMLib.Alvo.slnx add src/MMLib.Alvo src/MMLib.Alvo.Data.EntityFrameworkCore src/MMLib.Alvo.Data.Sqlite src/MMLib.Alvo.Data.PostgreSql
dotnet sln MMLib.Alvo.slnx add test/MMLib.Alvo.Tests test/MMLib.Alvo.Data.EntityFrameworkCore.Tests test/MMLib.Alvo.Data.Sqlite.Tests test/MMLib.Alvo.Data.PostgreSql.Tests.Integration
```
(Create the test projects first with `dotnet new xunit3` following the pattern of `test/MMLib.Alvo.Schema.Tests`, each `ProjectReference`-ing its production project and `MMLib.Alvo.Testing`.)

- [ ] **Step 5: Write the EF-shield arch test (failing until the assembly exists, then passing).**

`test/MMLib.Alvo.Abstractions.Tests/ArchitectureTests.cs` (add):
```csharp
[Fact]
public void Core_does_not_reference_EntityFrameworkCore()
{
    var core = System.Reflection.Assembly.Load("MMLib.Alvo");
    bool referencesEf = core.GetReferencedAssemblies()
        .Any(a => a.Name!.StartsWith("Microsoft.EntityFrameworkCore", StringComparison.Ordinal)
               || a.Name!.StartsWith("Npgsql", StringComparison.Ordinal));
    referencesEf.ShouldBeFalse("the core must stay EF-free — EF lives only in Data.* packages behind ISchemaMigrator.");
}
```

- [ ] **Step 6: Build and run ring0.**

Run: `dotnet build -c Release && scripts/test-ring0`
Expected: builds; the arch test passes (core has no EF reference yet).

- [ ] **Step 7: Commit.**

```bash
git add -A
git commit -m "chore(core): scaffold core + Data.* packages and the EF-shield arch test"
```

---

## Task 2: The `SchemaModel` (driver-agnostic entity model)

**Files:**
- Create: `src/MMLib.Alvo.Abstractions/Schema/*.cs` (enums + records listed below)
- Test: `test/MMLib.Alvo.Abstractions.Tests/Schema/SchemaModelTests.cs`

**Interfaces:**
- Produces: `MMLib.Alvo.Schema.{SchemaModel, EntitySchema, FieldSchema, IndexSchema, RefSchema, FieldType, EntityStorage, TenancyMode, OnDelete}` — consumed by every later task and by #19/#20.

- [ ] **Step 1: Write a failing test that constructs a model and asserts value equality + defaults.**

```csharp
namespace MMLib.Alvo.Abstractions.Tests.Schema;

using MMLib.Alvo.Schema;

public class SchemaModelTests
{
    [Fact]
    public void EntitySchema_defaults_are_physical_and_non_audited()
    {
        var e = new EntitySchema { Name = "vehicles", Fields = [new FieldSchema { Name = "id", Type = FieldType.Uuid }] };
        e.Storage.ShouldBe(EntityStorage.Physical);
        e.Audit.ShouldBeFalse();
        e.SoftDelete.ShouldBeFalse();
        e.Tenancy.ShouldBeNull();
    }

    [Fact]
    public void SchemaModel_has_value_equality()
    {
        FieldSchema F() => new() { Name = "vin", Type = FieldType.String, MaxLength = 17 };
        var a = new SchemaModel([new EntitySchema { Name = "vehicles", Fields = [F()] }]);
        var b = new SchemaModel([new EntitySchema { Name = "vehicles", Fields = [F()] }]);
        a.ShouldBe(b);
    }
}
```

- [ ] **Step 2: Run it and confirm it fails to compile (types absent).**

Run: `dotnet test test/MMLib.Alvo.Abstractions.Tests -v minimal`
Expected: compile error, `SchemaModel` not found.

- [ ] **Step 3: Create the enums and records.**

`FieldType.cs`:
```csharp
namespace MMLib.Alvo.Schema;
public enum FieldType { String, Text, Integer, Decimal, Boolean, Date, DateTime, Uuid, Json, Enum, Ref }
```
`EntityStorage.cs`: `public enum EntityStorage { Physical, Dynamic }`
`TenancyMode.cs`: `public enum TenancyMode { Scoped, Global }`
`OnDelete.cs`: `public enum OnDelete { Restrict, Cascade, SetNull }`

`RefSchema.cs`:
```csharp
namespace MMLib.Alvo.Schema;
public sealed record RefSchema(string TargetEntity, OnDelete OnDelete);
```
`IndexSchema.cs`:
```csharp
namespace MMLib.Alvo.Schema;
public sealed record IndexSchema(IReadOnlyList<string> Fields, bool Unique);
```
`FieldSchema.cs`:
```csharp
namespace MMLib.Alvo.Schema;

public sealed record FieldSchema
{
    public required string Name { get; init; }
    public required FieldType Type { get; init; }
    public string? RenamedFrom { get; init; }
    public bool Required { get; init; }
    public bool Unique { get; init; }
    public bool Nullable { get; init; }
    public int? MaxLength { get; init; }
    public int? Precision { get; init; }
    public int? Scale { get; init; }
    public IReadOnlyList<string>? EnumValues { get; init; }
    public RefSchema? Reference { get; init; }
    public bool Indexed { get; init; }
    public string? ComputedExpression { get; init; }
}
```
`EntitySchema.cs`:
```csharp
namespace MMLib.Alvo.Schema;

public sealed record EntitySchema
{
    public required string Name { get; init; }
    public string? RenamedFrom { get; init; }
    public EntityStorage Storage { get; init; } = EntityStorage.Physical;
    public TenancyMode? Tenancy { get; init; }
    public bool SoftDelete { get; init; }
    public bool Audit { get; init; }
    public required IReadOnlyList<FieldSchema> Fields { get; init; }
    public IReadOnlyList<IndexSchema> Indexes { get; init; } = [];
}
```
`SchemaModel.cs`:
```csharp
namespace MMLib.Alvo.Schema;
public sealed record SchemaModel(IReadOnlyList<EntitySchema> Entities);
```

- [ ] **Step 4: Run tests — pass.** Run: `dotnet test test/MMLib.Alvo.Abstractions.Tests -v minimal`. Expected: PASS.

- [ ] **Step 5: Commit.**

```bash
git add -A && git commit -m "feat(schema): add the driver-agnostic SchemaModel and enums"
```

---

## Task 3: Migration ports + builder/options contracts (Abstractions)

**Files:**
- Create: `src/MMLib.Alvo.Abstractions/Migrations/*.cs`, `src/MMLib.Alvo.Abstractions/Schema/ISchemaRegistry.cs`, `Schema/ISchemaIntrospector.cs`, `IAlvoBuilder.cs`, `AlvoOptions.cs`, `AlvoMode.cs`
- Test: `test/MMLib.Alvo.Abstractions.Tests/Migrations/MigrationPlanTests.cs`

**Interfaces:**
- Produces: `MMLib.Alvo.Migrations.{ISchemaMigrator, MigrationPlan, MigrationStep, SchemaChange, SchemaChangeKind, MigrationOptions, MigrationResult, IDescriptorSource}`; `MMLib.Alvo.Schema.{ISchemaRegistry, ISchemaIntrospector}`; `MMLib.Alvo.{IAlvoBuilder, AlvoOptions, AlvoMode}`.

- [ ] **Step 1: Write a failing test for `MigrationPlan` derived properties.**

```csharp
namespace MMLib.Alvo.Abstractions.Tests.Migrations;
using MMLib.Alvo.Migrations;

public class MigrationPlanTests
{
    [Fact]
    public void HasDestructiveChanges_is_true_when_any_step_is_destructive()
    {
        var plan = new MigrationPlan
        {
            Steps =
            [
                new MigrationStep(new SchemaChange { Kind = SchemaChangeKind.AddField, Entity = "v" }, "ALTER …", false, null),
                new MigrationStep(new SchemaChange { Kind = SchemaChangeKind.DropField, Entity = "v", IsDestructive = true }, "ALTER …", true, "drops column data"),
            ],
        };
        plan.HasDestructiveChanges.ShouldBeTrue();
        plan.IsEmpty.ShouldBeFalse();
    }
}
```

- [ ] **Step 2: Run — fails to compile.** Run: `dotnet test test/MMLib.Alvo.Abstractions.Tests`. Expected: compile error.

- [ ] **Step 3: Create the migration contracts.**

`SchemaChangeKind.cs`:
```csharp
namespace MMLib.Alvo.Migrations;
public enum SchemaChangeKind
{
    CreateEntity, DropEntity, RenameEntity,
    AddField, DropField, RenameField, AlterField,
    AddIndex, DropIndex,
}
```
`SchemaChange.cs`:
```csharp
namespace MMLib.Alvo.Migrations;
public sealed record SchemaChange
{
    public required SchemaChangeKind Kind { get; init; }
    public required string Entity { get; init; }
    public string? Field { get; init; }
    public string? FromName { get; init; }
    public bool IsDestructive { get; init; }
    public string? Detail { get; init; }
}
```
`MigrationStep.cs`:
```csharp
namespace MMLib.Alvo.Migrations;
public sealed record MigrationStep(SchemaChange Change, string Sql, bool IsDestructive, string? Reason);
```
`MigrationPlan.cs`:
```csharp
namespace MMLib.Alvo.Migrations;
public sealed record MigrationPlan
{
    public required IReadOnlyList<MigrationStep> Steps { get; init; }
    public bool HasDestructiveChanges => Steps.Any(s => s.IsDestructive);
    public bool IsEmpty => Steps.Count == 0;
}
```
`MigrationOptions.cs`:
```csharp
namespace MMLib.Alvo.Migrations;
public sealed record MigrationOptions
{
    public bool AllowDestructive { get; init; }
    public bool DryRun { get; init; }
}
```
`MigrationResult.cs`:
```csharp
namespace MMLib.Alvo.Migrations;
public sealed record MigrationResult(bool Applied, MigrationPlan Plan, bool WasDryRun);
```
`ISchemaMigrator.cs`:
```csharp
namespace MMLib.Alvo.Migrations;
using MMLib.Alvo.Schema;

public interface ISchemaMigrator
{
    Task<MigrationPlan> PlanAsync(SchemaModel current, SchemaModel desired, MigrationOptions options, CancellationToken ct = default);
    Task<MigrationResult> ApplyAsync(MigrationPlan plan, MigrationOptions options, CancellationToken ct = default);
}
```
`IDescriptorSource.cs`:
```csharp
namespace MMLib.Alvo.Migrations;
public interface IDescriptorSource
{
    Task<string> LoadAsync(CancellationToken ct = default);
}
```
`Schema/ISchemaIntrospector.cs`:
```csharp
namespace MMLib.Alvo.Schema;
public interface ISchemaIntrospector
{
    Task<SchemaModel> IntrospectAsync(CancellationToken ct = default);
}
```
`Schema/ISchemaRegistry.cs`:
```csharp
namespace MMLib.Alvo.Schema;
public interface ISchemaRegistry
{
    SchemaModel GetSchema();
}
```
`AlvoMode.cs`:
```csharp
namespace MMLib.Alvo;
public enum AlvoMode { Standalone, Embedded }
```
`AlvoOptions.cs`:
```csharp
namespace MMLib.Alvo;
using System.ComponentModel.DataAnnotations;

public sealed class AlvoOptions
{
    public AlvoMode Mode { get; set; } = AlvoMode.Standalone;

    [RegularExpression("^[a-z][a-z0-9_]{0,15}$", ErrorMessage = "SchemaPrefix must be lower snake_case, 1–16 chars.")]
    public string SchemaPrefix { get; set; } = "alvo";
}
```
`IAlvoBuilder.cs`:
```csharp
namespace MMLib.Alvo;
using Microsoft.Extensions.DependencyInjection;

public interface IAlvoBuilder
{
    IServiceCollection Services { get; }
}
```

- [ ] **Step 4: Run — pass.** Run: `dotnet test test/MMLib.Alvo.Abstractions.Tests`. Expected: PASS.

- [ ] **Step 5: Commit.**

```bash
git add -A && git commit -m "feat(migrations): add schema-migration ports, options and builder contract"
```

---

## Task 4: Typed descriptor model + parser (core)

**Files:**
- Create: `src/MMLib.Alvo/Descriptor/AlvoDescriptor.cs`, `Descriptor/DescriptorJsonContext.cs`, `Descriptor/DescriptorParser.cs`
- Test: `test/MMLib.Alvo.Tests/Descriptor/DescriptorParserTests.cs`

**Interfaces:**
- Consumes: nothing external.
- Produces: `MMLib.Alvo.Descriptor.{AlvoDescriptor, EntityDto, FieldDto}` (STJ DTOs, only the schema-relevant subset), `DescriptorParser.Parse(string json) -> AlvoDescriptor`.

- [ ] **Step 1: Write a failing test parsing the real `simple-tasks` example.**

```csharp
namespace MMLib.Alvo.Tests.Descriptor;
using MMLib.Alvo.Descriptor;

public class DescriptorParserTests
{
    private static string SimpleTasks() =>
        File.ReadAllText(Path.Combine(RepoRoot.Path, "examples", "simple-tasks", "tasks.alvo.json"));

    [Fact]
    public void Parses_name_and_entities()
    {
        AlvoDescriptor d = DescriptorParser.Parse(SimpleTasks());
        d.Name.ShouldBe("simple-tasks");
        d.Entities.Keys.ShouldContain("tasks");
        d.Entities["tasks"].Fields.ShouldNotBeEmpty();
    }
}
```
(`RepoRoot.Path` mirrors `MMLib.Alvo.Testing.RepositoryRoot`; if a helper does not exist in the test project, reuse `MMLib.Alvo.Testing.RepositoryRoot`.)

- [ ] **Step 2: Run — fails.** Run: `dotnet test test/MMLib.Alvo.Tests`. Expected: compile error.

- [ ] **Step 3: Create the DTOs, the STJ source-gen context, and the parser.**

`AlvoDescriptor.cs` (schema-relevant subset only — parsing ignores unknown members so automation/webhooks/etc. don't need DTOs):
```csharp
namespace MMLib.Alvo.Descriptor;
using System.Text.Json.Serialization;

public sealed class AlvoDescriptor
{
    [JsonPropertyName("apiVersion")] public string ApiVersion { get; init; } = "";
    [JsonPropertyName("name")] public string Name { get; init; } = "";
    [JsonPropertyName("tenancy")] public TenancyDto? Tenancy { get; init; }
    [JsonPropertyName("entities")] public Dictionary<string, EntityDto> Entities { get; init; } = new();
}

public sealed class TenancyDto { [JsonPropertyName("enabled")] public bool Enabled { get; init; } }

public sealed class EntityDto
{
    [JsonPropertyName("renamedFrom")] public string? RenamedFrom { get; init; }
    [JsonPropertyName("storage")] public string? Storage { get; init; }     // "physical" | "dynamic"
    [JsonPropertyName("tenancy")] public string? Tenancy { get; init; }     // "scoped" | "global"
    [JsonPropertyName("softDelete")] public bool SoftDelete { get; init; }
    [JsonPropertyName("audit")] public bool Audit { get; init; }
    [JsonPropertyName("fields")] public Dictionary<string, FieldDto> Fields { get; init; } = new();
    [JsonPropertyName("indexes")] public List<IndexDto>? Indexes { get; init; }
}

public sealed class FieldDto
{
    [JsonPropertyName("type")] public string Type { get; init; } = "";
    [JsonPropertyName("renamedFrom")] public string? RenamedFrom { get; init; }
    [JsonPropertyName("required")] public bool Required { get; init; }
    [JsonPropertyName("unique")] public bool Unique { get; init; }
    [JsonPropertyName("nullable")] public bool? Nullable { get; init; }
    [JsonPropertyName("maxLength")] public int? MaxLength { get; init; }
    [JsonPropertyName("precision")] public int? Precision { get; init; }
    [JsonPropertyName("scale")] public int? Scale { get; init; }
    [JsonPropertyName("values")] public List<string>? Values { get; init; }
    [JsonPropertyName("entity")] public string? Entity { get; init; }        // ref target
    [JsonPropertyName("onDelete")] public string? OnDelete { get; init; }
    [JsonPropertyName("index")] public bool Index { get; init; }
    [JsonPropertyName("computed")] public string? Computed { get; init; }
}

public sealed class IndexDto
{
    [JsonPropertyName("fields")] public List<string> Fields { get; init; } = new();
    [JsonPropertyName("unique")] public bool Unique { get; init; }
}
```
`DescriptorJsonContext.cs`:
```csharp
namespace MMLib.Alvo.Descriptor;
using System.Text.Json.Serialization;

[JsonSourceGenerationOptions(PropertyNameCaseInsensitive = false)]
[JsonSerializable(typeof(AlvoDescriptor))]
internal sealed partial class DescriptorJsonContext : JsonSerializerContext;
```
`DescriptorParser.cs`:
```csharp
namespace MMLib.Alvo.Descriptor;
using System.Text.Json;

public static class DescriptorParser
{
    public static AlvoDescriptor Parse(string json)
        => JsonSerializer.Deserialize(json, DescriptorJsonContext.Default.AlvoDescriptor)
           ?? throw new InvalidDataException("Descriptor JSON deserialized to null.");
}
```

- [ ] **Step 4: Run — pass.** Run: `dotnet test test/MMLib.Alvo.Tests`. Expected: PASS.

- [ ] **Step 5: Commit.** `git add -A && git commit -m "feat(descriptor): typed descriptor model + System.Text.Json parser"`

---

## Task 5: Descriptor → SchemaModel mapper (with framework-managed columns)

**Files:**
- Create: `src/MMLib.Alvo/Descriptor/DescriptorToSchemaMapper.cs`
- Test: `test/MMLib.Alvo.Tests/Descriptor/DescriptorToSchemaMapperTests.cs` + Verify snapshots

**Interfaces:**
- Consumes: `AlvoDescriptor` (Task 4), `SchemaModel` (Task 2).
- Produces: `DescriptorToSchemaMapper.Map(AlvoDescriptor) -> SchemaModel`.

- [ ] **Step 1: Write failing tests for managed-column injection and ignore-of-non-schema entities.**

```csharp
namespace MMLib.Alvo.Tests.Descriptor;
using MMLib.Alvo.Descriptor; using MMLib.Alvo.Schema;

public class DescriptorToSchemaMapperTests
{
    private static SchemaModel Map(string file)
        => DescriptorToSchemaMapper.Map(DescriptorParser.Parse(
               File.ReadAllText(Path.Combine(RepoRoot.Path, "examples", file))));

    [Fact]
    public void Injects_id_when_absent()
    {
        var m = Map("simple-tasks/tasks.alvo.json");
        var tasks = m.Entities.Single(e => e.Name == "tasks");
        tasks.Fields.ShouldContain(f => f.Name == "id" && f.Type == FieldType.Uuid);
    }

    [Fact]
    public void Audit_entity_gets_managed_audit_columns()
    {
        var m = Map("simple-tasks/tasks.alvo.json");
        var tasks = m.Entities.Single(e => e.Name == "tasks");
        // tasks in simple-tasks declares audit:true
        tasks.Fields.Select(f => f.Name).ShouldContain("created_at");
        tasks.Fields.Select(f => f.Name).ShouldContain("updated_by");
    }

    [Fact]
    public async Task Complex_crm_maps_to_a_stable_model()
    {
        var m = Map("complex-crm/crm.alvo.json");
        await Verify(m);   // snapshot: freezes mapping incl. tenant_id, generated cols, refs
    }
}
```

- [ ] **Step 2: Run — fails.** Run: `dotnet test test/MMLib.Alvo.Tests`. Expected: compile error / no snapshot.

- [ ] **Step 3: Implement the mapper.**

`DescriptorToSchemaMapper.cs`:
```csharp
namespace MMLib.Alvo.Descriptor;
using MMLib.Alvo.Schema;

public static class DescriptorToSchemaMapper
{
    public static SchemaModel Map(AlvoDescriptor d)
    {
        bool tenancyEnabled = d.Tenancy?.Enabled == true;
        var entities = d.Entities
            .Where(kvp => (kvp.Value.Storage ?? "physical") == "physical")   // dynamic store is F7
            .Select(kvp => MapEntity(kvp.Key, kvp.Value, tenancyEnabled))
            .ToList();
        return new SchemaModel(entities);
    }

    private static EntitySchema MapEntity(string name, EntityDto e, bool tenancyEnabled)
    {
        var fields = new List<FieldSchema>();
        if (!e.Fields.ContainsKey("id"))
            fields.Add(new FieldSchema { Name = "id", Type = FieldType.Uuid, Required = true });

        foreach (var (fname, f) in e.Fields)
            fields.Add(MapField(fname, f));

        var tenancy = ResolveTenancy(e.Tenancy, tenancyEnabled);
        if (tenancy == TenancyMode.Scoped)
            fields.Add(new FieldSchema { Name = "tenant_id", Type = FieldType.Uuid, Required = true, Indexed = true });

        if (e.Audit)
        {
            fields.Add(new FieldSchema { Name = "created_at", Type = FieldType.DateTime, Required = true });
            fields.Add(new FieldSchema { Name = "created_by", Type = FieldType.Uuid });
            fields.Add(new FieldSchema { Name = "updated_at", Type = FieldType.DateTime, Required = true });
            fields.Add(new FieldSchema { Name = "updated_by", Type = FieldType.Uuid });
        }
        if (e.SoftDelete)
            fields.Add(new FieldSchema { Name = "deleted_at", Type = FieldType.DateTime });

        var indexes = (e.Indexes ?? [])
            .Select(i => new IndexSchema(i.Fields, i.Unique)).ToList();

        return new EntitySchema
        {
            Name = name,
            RenamedFrom = e.RenamedFrom,
            Storage = EntityStorage.Physical,
            Tenancy = tenancy,
            SoftDelete = e.SoftDelete,
            Audit = e.Audit,
            Fields = fields,
            Indexes = indexes,
        };
    }

    private static TenancyMode? ResolveTenancy(string? entityTenancy, bool tenancyEnabled) => entityTenancy switch
    {
        "global" => TenancyMode.Global,
        "scoped" => TenancyMode.Scoped,
        _ => tenancyEnabled ? TenancyMode.Scoped : null,
    };

    private static FieldSchema MapField(string name, FieldDto f) => new()
    {
        Name = name,
        Type = ParseType(f.Type),
        RenamedFrom = f.RenamedFrom,
        Required = f.Required,
        Unique = f.Unique,
        Nullable = f.Nullable ?? !f.Required,
        MaxLength = f.MaxLength,
        Precision = f.Precision,
        Scale = f.Scale,
        EnumValues = f.Values,
        Reference = f.Entity is null ? null : new RefSchema(f.Entity, ParseOnDelete(f.OnDelete)),
        Indexed = f.Index,
        ComputedExpression = f.Computed,
    };

    private static FieldType ParseType(string t) => t switch
    {
        "string" => FieldType.String, "text" => FieldType.Text, "integer" => FieldType.Integer,
        "decimal" => FieldType.Decimal, "boolean" => FieldType.Boolean, "date" => FieldType.Date,
        "datetime" => FieldType.DateTime, "uuid" => FieldType.Uuid, "json" => FieldType.Json,
        "enum" => FieldType.Enum, "ref" => FieldType.Ref,
        _ => throw new InvalidDataException($"Unknown field type '{t}'."),
    };

    private static OnDelete ParseOnDelete(string? od) => od switch
    {
        "cascade" => OnDelete.Cascade, "setNull" => OnDelete.SetNull, _ => OnDelete.Restrict,
    };
}
```

- [ ] **Step 4: Run — pass; review the Verify snapshot and accept it.**

Run: `dotnet test test/MMLib.Alvo.Tests`. Expected: PASS after accepting the `.received.txt` → `.verified.txt` for `complex-crm`.

- [ ] **Step 5: Commit.** `git add -A && git commit -m "feat(descriptor): map descriptor to SchemaModel with framework-managed columns"`

---

## Task 6: SchemaRegistry (core read model)

**Files:**
- Create: `src/MMLib.Alvo/Schema/SchemaRegistry.cs`, `Schema/Setup.cs`
- Test: `test/MMLib.Alvo.Tests/Schema/SchemaRegistryTests.cs`

**Interfaces:**
- Consumes: `SchemaModel`, `ISchemaRegistry`.
- Produces: `internal sealed class SchemaRegistry : ISchemaRegistry` seeded with a `SchemaModel`.

- [ ] **Step 1: Failing test.**
```csharp
namespace MMLib.Alvo.Tests.Schema;
using MMLib.Alvo.Schema;

public class SchemaRegistryTests
{
    [Fact]
    public void Returns_the_seeded_model()
    {
        var model = new SchemaModel([new EntitySchema { Name = "v", Fields = [new FieldSchema { Name = "id", Type = FieldType.Uuid }] }]);
        ISchemaRegistry reg = new SchemaRegistry(model);
        reg.GetSchema().ShouldBe(model);
    }
}
```
(Make `SchemaRegistry` `internal` and add `[assembly: InternalsVisibleTo("MMLib.Alvo.Tests")]` in the core, following the repo's existing InternalsVisibleTo pattern.)

- [ ] **Step 2: Run — fails.** Run: `dotnet test test/MMLib.Alvo.Tests`. Expected: compile error.

- [ ] **Step 3: Implement.**
```csharp
namespace MMLib.Alvo.Schema;
internal sealed class SchemaRegistry(SchemaModel model) : ISchemaRegistry
{
    public SchemaModel GetSchema() => model;
}
```

- [ ] **Step 4: Run — pass.** Run: `dotnet test test/MMLib.Alvo.Tests`. Expected: PASS.

- [ ] **Step 5: Commit.** `git add -A && git commit -m "feat(schema): SchemaRegistry read-model over the applied SchemaModel"`

---

## Task 7: `ISchemaMigrator` contract test base + in-memory fake

**Files:**
- Create: `src/MMLib.Alvo.Testing/Migrations/SchemaMigratorContractTests.cs` (abstract), `Migrations/InMemorySchemaMigrator.cs`
- Test: the fake is exercised by a concrete `test/MMLib.Alvo.Tests/Migrations/InMemorySchemaMigratorTests.cs`

**Interfaces:**
- Consumes: `ISchemaMigrator`, `SchemaModel`, `MigrationOptions`.
- Produces: `abstract class SchemaMigratorContractTests` with an abstract `protected abstract ISchemaMigrator CreateMigrator();` and `protected abstract Task<SchemaModel> IntrospectAsync();` — inherited by every provider in Tasks 11–12. `InMemorySchemaMigrator` (a fake that records applied models).

- [ ] **Step 1: Write the abstract contract test class** (the behavioural spec every provider must satisfy). Include a skipped dynamic-driver parity leg.

```csharp
namespace MMLib.Alvo.Testing.Migrations;
using MMLib.Alvo.Migrations; using MMLib.Alvo.Schema;

public abstract class SchemaMigratorContractTests
{
    protected abstract ISchemaMigrator CreateMigrator();
    protected abstract Task<SchemaModel> IntrospectAsync();

    private static SchemaModel Empty() => new([]);
    private static SchemaModel Vehicles(params FieldSchema[] extra) =>
        new([ new EntitySchema { Name = "vehicles",
              Fields = [ new FieldSchema { Name = "id", Type = FieldType.Uuid, Required = true },
                         new FieldSchema { Name = "vin", Type = FieldType.String, MaxLength = 17 }, .. extra ] } ]);

    [Fact]
    public async Task Create_then_introspect_matches_desired()
    {
        var m = CreateMigrator();
        var plan = await m.PlanAsync(Empty(), Vehicles(), new MigrationOptions());
        await m.ApplyAsync(plan, new MigrationOptions());
        var actual = await IntrospectAsync();
        actual.Entities.ShouldContain(e => e.Name == "vehicles");
    }

    [Fact]
    public async Task Reapply_is_idempotent()
    {
        var m = CreateMigrator();
        await m.ApplyAsync(await m.PlanAsync(Empty(), Vehicles(), new()), new());
        var second = await m.PlanAsync(Vehicles(), Vehicles(), new());
        second.IsEmpty.ShouldBeTrue();
    }

    [Fact]
    public async Task Drop_without_AllowDestructive_is_refused()
    {
        var m = CreateMigrator();
        await m.ApplyAsync(await m.PlanAsync(Empty(), Vehicles(new FieldSchema { Name = "colour", Type = FieldType.String }), new()), new());
        var plan = await m.PlanAsync(Vehicles(new FieldSchema { Name = "colour", Type = FieldType.String }), Vehicles(), new());
        plan.HasDestructiveChanges.ShouldBeTrue();
        var result = await m.ApplyAsync(plan, new MigrationOptions { AllowDestructive = false });
        result.Applied.ShouldBeFalse();
    }

    [Fact]
    public async Task Rename_preserves_data()
    {
        var m = CreateMigrator();
        var before = Vehicles(new FieldSchema { Name = "colour", Type = FieldType.String });
        await m.ApplyAsync(await m.PlanAsync(Empty(), before, new()), new());
        var after = Vehicles(new FieldSchema { Name = "color", Type = FieldType.String, RenamedFrom = "colour" });
        var plan = await m.PlanAsync(before, after, new());
        plan.Steps.ShouldContain(s => s.Change.Kind == SchemaChangeKind.RenameField);
        plan.Steps.ShouldNotContain(s => s.Change.Kind == SchemaChangeKind.DropField);
    }

    [Fact(Skip = "Dynamic driver lands in F7 — parity leg reserved (analysis §2.1).")]
    public Task Same_suite_passes_over_a_dynamic_entity() => Task.CompletedTask;
}
```

- [ ] **Step 2: Implement the in-memory fake** (satisfies the contract without a DB — the first green implementation, and a shipped test double).

```csharp
namespace MMLib.Alvo.Testing.Migrations;
using MMLib.Alvo.Migrations; using MMLib.Alvo.Schema;

public sealed class InMemorySchemaMigrator : ISchemaMigrator
{
    public SchemaModel Applied { get; private set; } = new([]);

    public Task<MigrationPlan> PlanAsync(SchemaModel current, SchemaModel desired, MigrationOptions options, CancellationToken ct = default)
        => Task.FromResult(new MigrationPlan { Steps = SchemaDiff.Compute(current, desired) });

    public Task<MigrationResult> ApplyAsync(MigrationPlan plan, MigrationOptions options, CancellationToken ct = default)
    {
        if (plan.HasDestructiveChanges && !options.AllowDestructive)
            return Task.FromResult(new MigrationResult(false, plan, options.DryRun));
        if (!options.DryRun) Applied = plan.ProjectedModel ?? Applied;
        return Task.FromResult(new MigrationResult(!options.DryRun, plan, options.DryRun));
    }
}
```
Add a minimal, shared **semantic diff** helper `SchemaDiff.Compute(current, desired)` in `MMLib.Alvo.Testing` returning `IReadOnlyList<MigrationStep>` with rename detection via `RenamedFrom` and destructive flags for drops. (This same semantic diff is what Task 9 hands to EF for SQL, keeping intent ours. Add a `ProjectedModel` to `MigrationPlan` as an internal-only convenience, or track applied model in the fake — keep `MigrationPlan` public shape unchanged by storing the projection in the fake instead of the record.)

> Implementation note: keep `MigrationPlan` exactly as defined in Task 3 (no `ProjectedModel` on the public record). The fake computes the projected model itself from `current` + steps.

- [ ] **Step 3: Concrete test wiring + run.**
```csharp
namespace MMLib.Alvo.Tests.Migrations;
using MMLib.Alvo.Testing.Migrations; using MMLib.Alvo.Migrations; using MMLib.Alvo.Schema;

public sealed class InMemorySchemaMigratorTests : SchemaMigratorContractTests
{
    private readonly InMemorySchemaMigrator _m = new();
    protected override ISchemaMigrator CreateMigrator() => _m;
    protected override Task<SchemaModel> IntrospectAsync() => Task.FromResult(_m.Applied);
}
```
Run: `dotnet test test/MMLib.Alvo.Tests`. Expected: PASS (contract green against the fake).

- [ ] **Step 4: Commit.** `git add -A && git commit -m "test(migrations): ISchemaMigrator contract suite + in-memory fake"`

---

## Task 8: Descriptor → EF `IModel` builder (Data.EntityFrameworkCore)

**Files:**
- Create: `src/MMLib.Alvo.Data.EntityFrameworkCore/Internal/DescriptorModelBuilder.cs`
- Test: `test/MMLib.Alvo.Data.EntityFrameworkCore.Tests/DescriptorModelBuilderTests.cs`

**Interfaces:**
- Consumes: `SchemaModel` (Task 2).
- Produces: `internal static IModel Build(SchemaModel model, Func<ModelBuilder> newBuilder)` — maps each `EntitySchema`/`FieldSchema` onto a conventionless `ModelBuilder`, mapping `FieldType` → CLR type + facets (maxLength, precision/scale, required→not-null, unique index, ref→FK, generated column via `HasComputedColumnSql`), then `FinalizeModel()`.

- [ ] **Step 1: Failing test** asserting the built model has the expected entity type, key, and a not-null required property. (Use SQLite conventions for the test.)

```csharp
[Fact]
public void Builds_entity_with_key_and_required_property()
{
    var model = new SchemaModel([ new EntitySchema { Name = "vehicles",
        Fields = [ new FieldSchema { Name = "id", Type = FieldType.Uuid, Required = true },
                   new FieldSchema { Name = "vin", Type = FieldType.String, MaxLength = 17, Required = true } ] } ]);
    IModel efModel = DescriptorModelBuilder.Build(model, () => new ModelBuilder(SqliteConventionSetBuilder.Build()));
    var et = efModel.FindEntityType("vehicles")!;
    et.FindPrimaryKey()!.Properties.Single().Name.ShouldBe("id");
    et.FindProperty("vin")!.IsNullable.ShouldBeFalse();
}
```

- [ ] **Step 2: Run — fails.** Run: `dotnet test test/MMLib.Alvo.Data.EntityFrameworkCore.Tests`. Expected: compile error.

- [ ] **Step 3: Implement `DescriptorModelBuilder.Build`** — the FieldType→store mapping table, `HasMaxLength`, `HasPrecision`, `IsRequired`, `IsUnique`, FK via `HasOne(...).WithMany().HasForeignKey(...).OnDelete(...)`, generated columns via `HasComputedColumnSql(expr, stored: true)`, PK on `id`. (Full mapping code; no placeholders — one arm per `FieldType`.)

- [ ] **Step 4: Run — pass.** Run: `dotnet test test/MMLib.Alvo.Data.EntityFrameworkCore.Tests`. Expected: PASS.

- [ ] **Step 5: Commit.** `git add -A && git commit -m "feat(ef): build a runtime EF IModel from the SchemaModel"`

---

## Task 9: `EfCoreSchemaMigrator.PlanAsync` (diff + rename + guardrail + SQL)

**Files:**
- Create: `src/MMLib.Alvo.Data.EntityFrameworkCore/EfCoreSchemaMigrator.cs`, `Internal/RenamePrePass.cs`, `Internal/DestructiveScan.cs`
- Test: covered by the provider contract tests (Tasks 11–12) + a focused SQLite `PlanAsync` unit test here.

**Interfaces:**
- Consumes: `DescriptorModelBuilder.Build` (Task 8), the provider's `IMigrationsModelDiffer` + `IMigrationsSqlGenerator` (injected via the provider wiring, Tasks 11–12).
- Produces: `internal sealed class EfCoreSchemaMigrator : ISchemaMigrator`. `PlanAsync` builds current+desired `IModel`s from the two `SchemaModel`s, applies the rename pre-pass (per Task 0's decision), calls `differ.GetDifferences`, scans for destructive ops, and generates SQL per step.

- [ ] **Step 1: Focused failing test (SQLite): a plan for create-from-empty contains a CreateEntity step with non-empty SQL.** (Constructs the migrator with SQLite services resolved from a throwaway `DbContext` — the helper lands in Task 11; here, inline the resolution.)

- [ ] **Step 2: Implement `PlanAsync`** following the Task 0 decision:
  - Build `currentModel`/`desiredModel` via `DescriptorModelBuilder.Build`.
  - `RenamePrePass`: for each `EntitySchema`/`FieldSchema` with `RenamedFrom`, emit a `RenameTableOperation`/`RenameColumnOperation` and align the current model's names so the differ does not see drop+add.
  - `var ops = differ.GetDifferences(currentModel.GetRelationalModel(), desiredModel.GetRelationalModel());`
  - Prepend the rename operations; map each `MigrationOperation` to our `SchemaChange` (kind + destructive) via `DestructiveScan` (Drop*, and AlterColumn where nullability/size narrows).
  - `var commands = sqlGenerator.Generate(ops, desiredModel);` and pair each command's text to its `SchemaChange` into `MigrationStep`s.
  - Return `new MigrationPlan { Steps = steps }`.

- [ ] **Step 3: Implement `ApplyAsync` stub** that throws `NotImplementedException` for now (real body in Task 10) — keep the class compiling.

- [ ] **Step 4: Run the focused test — pass.** Run: `dotnet test test/MMLib.Alvo.Data.EntityFrameworkCore.Tests`. Expected: PASS.

- [ ] **Step 5: Commit.** `git add -A && git commit -m "feat(ef): EfCoreSchemaMigrator.PlanAsync — EF diff + rename + guardrail + SQL"`

---

## Task 10: `EfCoreSchemaMigrator.ApplyAsync` + `EfCoreSchemaIntrospector`

**Files:**
- Modify: `src/MMLib.Alvo.Data.EntityFrameworkCore/EfCoreSchemaMigrator.cs`
- Create: `src/MMLib.Alvo.Data.EntityFrameworkCore/EfCoreSchemaIntrospector.cs`

**Interfaces:**
- Consumes: an `IRelationalConnection`/`DbConnection` (provider-supplied).
- Produces: `ApplyAsync` executes plan SQL in one transaction (respecting `DryRun`/`AllowDestructive`); `EfCoreSchemaIntrospector.IntrospectAsync` uses `IDatabaseModelFactory` to reverse-engineer the DB into a `SchemaModel`.

- [ ] **Step 1: Implement `ApplyAsync`:** if `plan.HasDestructiveChanges && !options.AllowDestructive` → return `new MigrationResult(false, plan, options.DryRun)`; if `options.DryRun` → return `new MigrationResult(false, plan, true)` without executing; else open a transaction, execute each `MigrationStep.Sql`, commit, return `new MigrationResult(true, plan, false)`.

- [ ] **Step 2: Implement `EfCoreSchemaIntrospector`** over `IDatabaseModelFactory` → map `DatabaseModel` tables/columns back to `SchemaModel` (inverse of the FieldType map). Used for contract-test introspection and drift/baseline.

- [ ] **Step 3: Commit.** `git add -A && git commit -m "feat(ef): transactional ApplyAsync + EF introspector"`

*(No standalone test here — the provider contract tests in Tasks 11–12 exercise both against real engines.)*

---

## Task 11: Data.Sqlite wiring + contract tests green (SQLite)

**Files:**
- Create: `src/MMLib.Alvo.Data.Sqlite/AlvoSqliteBuilderExtensions.cs`, `Internal/SqliteMigrationServices.cs`
- Test: `test/MMLib.Alvo.Data.Sqlite.Tests/SqliteSchemaMigratorTests.cs`

**Interfaces:**
- Produces: `UseSqlite(this IAlvoBuilder, string connectionString)` (namespace `Microsoft.Extensions.DependencyInjection`) that `TryAdd`s `ISchemaMigrator` (an `EfCoreSchemaMigrator` bound to SQLite's `IMigrationsModelDiffer`/`IMigrationsSqlGenerator`) and `ISchemaIntrospector`.

- [ ] **Step 1: Implement `SqliteMigrationServices`** — resolve SQLite's `IMigrationsSqlGenerator`, `IMigrationsModelDiffer`, and `IRelationalConnection` from a minimal `DbContext` configured with `UseSqlite(connectionString)`; expose them for the migrator. (Follow the `AddDbContext((sp, o) => o.UseSqlite(cs))` + `GetInfrastructure().GetService<…>()` approach from the EF docs.)

- [ ] **Step 2: Implement `UseSqlite`** — `TryAddSingleton`/`TryAddScoped` the migrator + introspector wired to `SqliteMigrationServices`; return the builder.

- [ ] **Step 3: Wire the contract tests against SQLite (in-proc file DB).**
```csharp
namespace MMLib.Alvo.Data.Sqlite.Tests;
using MMLib.Alvo.Testing.Migrations; using MMLib.Alvo.Migrations; using MMLib.Alvo.Schema;

public sealed class SqliteSchemaMigratorTests : SchemaMigratorContractTests, IDisposable
{
    // create a temp .db file, build the migrator/introspector over it
    protected override ISchemaMigrator CreateMigrator() => /* build over the temp DB */;
    protected override Task<SchemaModel> IntrospectAsync() => /* introspector over the same DB */;
    public void Dispose() { /* delete temp file */ }
}
```

- [ ] **Step 4: Run — all contract cases green on SQLite** (create, idempotency, destructive-refused, **rename-preserves-data via the SQLite table rebuild**).

Run: `dotnet test test/MMLib.Alvo.Data.Sqlite.Tests`. Expected: PASS.

- [ ] **Step 5: Commit.** `git add -A && git commit -m "feat(data): UseSqlite provider — schema migrator green on SQLite"`

---

## Task 12: Data.PostgreSql wiring + contract tests green (Testcontainers)

**Files:**
- Create: `src/MMLib.Alvo.Data.PostgreSql/AlvoPostgreSqlBuilderExtensions.cs`, `Internal/PostgreSqlMigrationServices.cs`
- Test: `test/MMLib.Alvo.Data.PostgreSql.Tests.Integration/PostgreSqlSchemaMigratorTests.cs`

**Interfaces:**
- Produces: `UsePostgreSql(this IAlvoBuilder, string connectionString)` mirroring `UseSqlite`, wired to Npgsql's migration services.

- [ ] **Step 1: Implement `PostgreSqlMigrationServices` + `UsePostgreSql`** (the Npgsql analogue of Task 11).

- [ ] **Step 2: Wire the contract tests over a Testcontainers PostgreSQL container** (class fixture spinning up `PostgreSqlBuilder().Build()`, one DB per test class).
```csharp
public sealed class PostgreSqlSchemaMigratorTests(PostgresFixture fx) : SchemaMigratorContractTests, IClassFixture<PostgresFixture>
{ /* CreateMigrator/IntrospectAsync over fx.ConnectionString */ }
```

- [ ] **Step 3: Run — all contract cases green on PostgreSQL.**

Run: `dotnet test test/MMLib.Alvo.Data.PostgreSql.Tests.Integration`. Expected: PASS (requires Docker).

- [ ] **Step 4: Commit.** `git add -A && git commit -m "feat(data): UsePostgreSql provider — schema migrator green on PostgreSQL"`

---

## Task 13: System-schema runner + applied-snapshot store

**Files:**
- Create: `src/MMLib.Alvo/Migrations/Internal/SystemSchemaInitializer.cs`, `Internal/AppliedSchemaStore.cs`
- Test: `test/MMLib.Alvo.Data.Sqlite.Tests/AppliedSchemaStoreTests.cs`

**Interfaces:**
- Produces: `SystemSchemaInitializer.EnsureAsync()` creates the `{prefix}_applied_schema` table (idempotent, `alvo.*` prefix from `AlvoOptions.SchemaPrefix`); `AppliedSchemaStore.GetCurrentAsync()/SaveAsync(SchemaModel, descriptorJson)` persists/reads the last-applied descriptor + serialized `SchemaModel`.

- [ ] **Step 1: Failing test:** after `SaveAsync(model, json)`, `GetCurrentAsync()` returns the same model; a second `EnsureAsync()` is a no-op (idempotent).

- [ ] **Step 2: Implement** the initializer (raw idempotent `CREATE TABLE IF NOT EXISTS` per engine — the framework's own fixed table, not the declarative engine) and the store (serialize `SchemaModel` to JSON in a single row keyed by project name).

- [ ] **Step 3: Run — pass.** Run: `dotnet test test/MMLib.Alvo.Data.Sqlite.Tests`. Expected: PASS.

- [ ] **Step 4: Commit.** `git add -A && git commit -m "feat(migrations): system-schema runner + applied-snapshot store"`

---

## Task 14: Migration orchestration (`SchemaMigrationRunner`) + guardrail

**Files:**
- Create: `src/MMLib.Alvo/Migrations/SchemaMigrationRunner.cs`, `Internal/DestructiveChangeGuard.cs`
- Test: `test/MMLib.Alvo.Tests/Migrations/SchemaMigrationRunnerTests.cs`

**Interfaces:**
- Consumes: `IDescriptorSource`, `DescriptorToSchemaMapper`, `ISchemaMigrator`, `ISchemaIntrospector`, `AppliedSchemaStore`.
- Produces: `SchemaMigrationRunner.RunAsync(MigrationOptions) -> MigrationResult` — loads descriptor → desired model; current = applied snapshot (or introspection baseline on first run); `PlanAsync`; if destructive && !AllowDestructive → return the plan as a dry-run refusal; else `ApplyAsync` and `SaveAsync`.

- [ ] **Step 1: Failing test** (with an `InMemorySchemaMigrator` + a fake `IDescriptorSource`): running against an empty current applies the create plan and saves the snapshot; a second run is idempotent (empty plan, `Applied == false`? — assert `result.Plan.IsEmpty`).

- [ ] **Step 2: Implement `SchemaMigrationRunner.RunAsync`** and `DestructiveChangeGuard` (formats the dry-run "what will happen" report from destructive steps).

- [ ] **Step 3: Run — pass.** Run: `dotnet test test/MMLib.Alvo.Tests`. Expected: PASS.

- [ ] **Step 4: Commit.** `git add -A && git commit -m "feat(migrations): SchemaMigrationRunner orchestration + destructive guardrail"`

---

## Task 15: `AddAlvo` + `IAlvoBuilder` + `FromDescriptor` + fail-fast validation

**Files:**
- Create: `src/MMLib.Alvo/AlvoServiceCollectionExtensions.cs`, `AlvoBuilderExtensions.cs`, `Internal/AlvoBuilder.cs`, `Internal/AlvoProviderValidation.cs`, `Migrations/Internal/FileDescriptorSource.cs`
- Test: `test/MMLib.Alvo.Data.Sqlite.Tests/AddAlvoIntegrationTests.cs`

**Interfaces:**
- Consumes: everything above.
- Produces: `AddAlvo(this IServiceCollection, Action<IAlvoBuilder>?) -> IAlvoBuilder`; `FromDescriptor(this IAlvoBuilder, string path) -> IAlvoBuilder`; `UseSchemaPrefix(this IAlvoBuilder, string) -> IAlvoBuilder`; startup validation throwing a structured error when no `ISchemaMigrator` is registered.

- [ ] **Step 1: Failing integration test** (in Data.Sqlite.Tests so a provider is present):
```csharp
[Fact]
public async Task AddAlvo_UseSqlite_FromDescriptor_migrates()
{
    var services = new ServiceCollection();
    services.AddAlvo(a => a.UseSqlite(TempConnString()).FromDescriptor(VehiclesDescriptorPath()));
    using var sp = services.BuildServiceProvider();
    var runner = sp.GetRequiredService<SchemaMigrationRunner>();
    var result = await runner.RunAsync(new MigrationOptions());
    result.Applied.ShouldBeTrue();
    // introspect → vehicles table exists
}

[Fact]
public void AddAlvo_without_a_provider_fails_fast_at_startup()
{
    var services = new ServiceCollection();
    services.AddAlvo();
    Should.Throw<OptionsValidationException>(() => { using var sp = services.BuildServiceProvider(); sp.GetRequiredService<SchemaMigrationRunner>(); /* trigger ValidateOnStart via a HostedService in real hosts */ });
}
```

- [ ] **Step 2: Implement** `AlvoBuilder` (`internal sealed`, wraps `IServiceCollection`), `AddAlvo` (registers core services with `TryAdd`, `AddOptions<AlvoOptions>().ValidateDataAnnotations().ValidateOnStart()`, registers `AlvoProviderValidation : IValidateOptions<AlvoOptions>` that asserts an `ISchemaMigrator` is registered), `FromDescriptor` (registers `FileDescriptorSource`), `UseSchemaPrefix` (`Configure<AlvoOptions>`).

- [ ] **Step 3: Run — pass.** Run: `dotnet test test/MMLib.Alvo.Data.Sqlite.Tests`. Expected: PASS.

- [ ] **Step 4: Commit.** `git add -A && git commit -m "feat(core): AddAlvo builder + FromDescriptor + fail-fast provider validation"`

---

## Task 16: Generated-SQL snapshot tests per engine + vehicle-registry fixture

**Files:**
- Create: `examples/vehicle-registry/vehicles.alvo.json`, `test/MMLib.Alvo.Data.Sqlite.Tests/GeneratedSqlSnapshotTests.cs`, matching PostgreSQL snapshots in the integration project
- Modify: `examples/README.md` (add the vehicle-registry entry)

**Interfaces:**
- Consumes: `PlanAsync` (Tasks 9–12).

- [ ] **Step 1: Author `vehicles.alvo.json`** — a valid descriptor (vehicles, owners, inspections; a `ref`, a composite index, `audit`, one `renamedFrom`), validated by the existing `MMLib.Alvo.Schema.Tests` example corpus.

- [ ] **Step 2: Write Verify snapshot tests** freezing the generated SQL for a canonical change set (create, add column, rename column, drop column, add index, add FK) on SQLite, and the same in the PostgreSQL integration project.
```csharp
[Fact]
public async Task Create_vehicles_sql_is_stable()
{
    var plan = await Migrator().PlanAsync(Empty(), VehiclesModel(), new());
    await Verify(string.Join("\n;\n", plan.Steps.Select(s => s.Sql))).UseParameters("sqlite");
}
```

- [ ] **Step 3: Run, review, accept snapshots.** Run: `dotnet test test/MMLib.Alvo.Data.Sqlite.Tests test/MMLib.Alvo.Data.PostgreSql.Tests.Integration`. Expected: PASS after accepting `.verified.txt` files (inspect that the SQL is correct per engine).

- [ ] **Step 4: Commit.** `git add -A && git commit -m "test(migrations): per-engine generated-SQL snapshots + vehicle-registry fixture"`

---

## Task 17: Public-API baselines for the four shipped packages

**Files:**
- Create: `test/MMLib.Alvo.Tests/PublicApi.MMLib.Alvo.verified.txt`, `test/MMLib.Alvo.Data.EntityFrameworkCore.Tests/PublicApi.*.verified.txt`, `test/MMLib.Alvo.Data.Sqlite.Tests/PublicApi.*.verified.txt`, `test/MMLib.Alvo.Data.PostgreSql.Tests.Integration/PublicApi.*.verified.txt`, plus the updated `PublicApi.MMLib.Alvo.Abstractions.verified.txt`.

**Interfaces:**
- Consumes: the shared `PublicApiApprovalTests` (already in `test/_shared`).

- [ ] **Step 1: Run the public-API approval tests** (they fail first — no baseline). Run: `scripts/test-ring1`. Expected: FAIL with a diff for each package.

- [ ] **Step 2: Review each generated public surface** — confirm only genuine contract is public (`SchemaModel`, ports, `AddAlvo`/`IAlvoBuilder`/`AlvoOptions`, the `Use*`/`FromDescriptor` extensions); everything else `internal`. Accept the baselines.

- [ ] **Step 3: Run ring1 — pass.** Run: `scripts/test-ring1`. Expected: PASS.

- [ ] **Step 4: Commit.** `git add -A && git commit -m "test(api): commit public-API baselines for core + Data.* packages"`

---

## Final verification (before the PR)

- [ ] `scripts/test-ring2` — all green (unit + arch + public-API + affected integration via Testcontainers).
- [ ] Confirm the EF-shield arch test passes (`MMLib.Alvo` has no EF reference).
- [ ] Confirm the two DoD behaviours by hand: `AddAlvo().UseSqlite().FromDescriptor(vehicles)` migrates on SQLite; the same on a Testcontainers PostgreSQL; a dropped field without `AllowDestructive` is refused with a dry-run report; a re-apply is a no-op.
- [ ] Dispatch `alvo-plan-guard` (read-only) — expect no drift from `docs/PLAN.md`, no violated §0 principle.
- [ ] `/code-review medium` on the diff; fix findings before opening the PR.
- [ ] Open PR-A (base `main`), body ends with `https://claude.ai/code/session_014MrAzim5LpauyTPTBUyc4u`. A human merges.

---

## Self-review (plan vs. spec)

- **Spec coverage:** rename spike → Task 0; packages/EF-shield → Task 1; SchemaModel → Task 2; ports/builder/options → Task 3; descriptor model+parser → Task 4; mapper + managed columns → Task 5; registry → Task 6; contract suite + fake → Task 7; EF IModel → Task 8; diff/rename/guardrail/SQL → Task 9; apply + introspect → Task 10; SQLite provider → Task 11; PostgreSQL provider → Task 12; system-schema runner + snapshot store → Task 13; orchestration + guardrail → Task 14; builder + fail-fast → Task 15; per-engine SQL snapshots + demo fixture → Task 16; public-API baselines → Task 17. Builder foundation rules (feature namespaces, `Microsoft.Extensions.DependencyInjection` extensions, `TryAdd`, `ValidateOnStart`, fail-fast) are applied in Tasks 3/11/12/15. Snapshot-primary "current" (applied store) → Task 13; introspection baseline/drift → Tasks 10/14. Dynamic-parity scaffold → Task 7 (skipped leg).
- **Deferred to PR-B (correctly out of scope):** append-only descriptor versioning, optimistic locking, rollback, two-client concurrency. **Deferred elsewhere:** Data API/CRUD (#19), SQL Server (F4), Docker/runnable demo (#24), dynamic store (F7).
- **Placeholder scan:** Tasks 8–12 describe EF-internal bodies at the interface + behaviour level (the contract tests are the executable spec, and Task 0 de-risks the one uncertain mechanism) rather than transcribing unverified EF-internal code — this is deliberate, not a placeholder. All public types and signatures used across tasks are defined in Tasks 2–3.
- **Type consistency:** `ISchemaMigrator.PlanAsync/ApplyAsync`, `MigrationPlan.{Steps,HasDestructiveChanges,IsEmpty}`, `MigrationStep(Change,Sql,IsDestructive,Reason)`, `SchemaChange.{Kind,Entity,Field,FromName,IsDestructive,Detail}`, `MigrationOptions.{AllowDestructive,DryRun}`, `MigrationResult(Applied,Plan,WasDryRun)`, `SchemaModel(Entities)`, `IAlvoBuilder.Services`, `AlvoOptions.{Mode,SchemaPrefix}` are used identically everywhere they appear.
