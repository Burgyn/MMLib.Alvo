# F3 Schema Registry PR-B (runtime / dashboard-first) Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Make the Alvo descriptor pipeline safe for runtime / dashboard-first schema changes — append-only versioning, optimistic-lock conflicts, git-revert rollback — and close the two #20 guardrails (`computed` raw-DDL splice; unvalidated descriptor input) that this untrusted-input path makes acute.

**Architecture:** The single-row `applied_schema` table becomes an append-only `descriptor_versions` history keyed `(project, revision)`; code-first and runtime apply write to the same history. Optimistic locking is a conditional insert (engine-agnostic — no DB row lock). Because the core is EF/ADO-free, the one transaction that must apply DDL *and* claim a revision atomically lives behind a new EF-package port `IRuntimeSchemaWriter`; a `RuntimeSchemaService` in core orchestrates validate → plan → guardrail → writer. All three EF services drop their single owned connection for a per-call connection factory so two clients can be genuinely concurrent.

**Tech Stack:** .NET `net10.0`, EF Core 9 (`IMigrationsModelDiffer` / `IMigrationsSqlGenerator` behind `ISchemaMigrator`), ADO.NET (`DbConnection`/`DbTransaction`), System.Text.Json source-gen, Corvus.Json.Validator 5.2.7 (promoted to shipped), xUnit v3 on Microsoft.Testing.Platform, Shouldly, Verify, CsCheck, Testcontainers.PostgreSql.

## Global Constraints

- Target framework `net10.0`; tests run on Microsoft.Testing.Platform (not VSTest).
- `MMLib.Alvo.Abstractions` is **EF-free and ADO.NET-free** — pure model + ports only. `RootNamespace` is `MMLib.Alvo`; a file under `Migrations/` is namespace `MMLib.Alvo.Migrations`.
- `MMLib.Alvo` (core) has **no reference to EntityFrameworkCore** — enforced by a NetArchTest rule. Core may reference Corvus.Json.Validator (not EF).
- Ports live in feature namespaces (`MMLib.Alvo.Migrations`, `MMLib.Alvo.Schema`, `MMLib.Alvo.Descriptor`) — never `MMLib.Alvo.Abstractions.*`.
- DI registration is idempotent: `TryAdd*` everywhere; provider services are singletons.
- Commits are Conventional Commits. Run `scripts/test-ring0` after each implementation step; `scripts/test-ring1` after each task; `scripts/test-ring2` before the PR.
- Methods stay short and single-purpose (~25-line ceiling); extract aggressively.
- Central package versions are pinned in `Directory.Packages.props`; reference by name only in `.csproj` (no inline `Version=`).
- Every shipped package change updates its committed public-API baseline.

---

## File Structure

**`src/MMLib.Alvo.Abstractions/`** (ports + pure model)
- Create `Migrations/DescriptorVersion.cs` — append-only version record.
- Create `Migrations/IDescriptorVersionStore.cs` — read history + non-atomic append.
- Create `Migrations/IRuntimeSchemaWriter.cs` — atomic apply-DDL + conditional append.
- Create `Migrations/DescriptorConcurrencyException.cs` — optimistic-lock conflict.
- Create `Descriptor/IDescriptorValidator.cs` — validation port.
- Create `Descriptor/DescriptorValidationResult.cs` — result + `DescriptorValidationError`.
- Keep `Migrations/IAppliedSchemaStore.cs` unchanged (re-implemented on the versions table).

**`src/MMLib.Alvo/`** (core — no EF)
- Create `Descriptor/Internal/DescriptorValidator.cs` — layered Corvus + semantic validator.
- Create `Descriptor/Internal/DescriptorSchemaSource.cs` — embeds `project.schema.json` as a resource.
- Create `Migrations/RuntimeSchemaService.cs` — runtime apply + rollback orchestrator.
- Modify `Descriptor/DescriptorToSchemaMapper.cs` — reject `computed` (defensive).
- Modify `Migrations/SchemaMigrationRunner.cs` — validate before parse (code-first path).
- Modify `AlvoServiceCollectionExtensions.cs` — register validator + runtime service.
- Modify `MMLib.Alvo.csproj` — add Corvus.Json.Validator + embed the schema.

**`src/MMLib.Alvo.Data.EntityFrameworkCore/`** (EF)
- Create `Internal/RelationalConnectionFactory.cs` — per-call `DbConnection` factory.
- Create `Internal/RelationalSqlBatch.cs` — shared "run SQL list in a transaction".
- Create `EfCoreDescriptorVersionStore.cs` — implements `IDescriptorVersionStore` + `IAppliedSchemaStore`.
- Create `EfCoreRuntimeSchemaWriter.cs` — implements `IRuntimeSchemaWriter`.
- Modify `Internal/SystemSchemaInitializer.cs` — create `{prefix}_descriptor_versions` (composite PK).
- Modify `EfCoreSchemaMigrator.cs` — per-call connection via factory; reuse `RelationalSqlBatch`.
- Modify `EfCoreSchemaIntrospector.cs` — per-call connection; exclude the new table name.
- Modify `AlvoEfCoreProvider.cs` — register factory + version store + writer.
- Delete `Internal/AppliedSchemaStore.cs` (superseded by `EfCoreDescriptorVersionStore`).
- Modify `Internal/AppliedSchemaJsonContext.cs` — add `DescriptorVersion` if snapshot serialization changes (see Task 4).

**`src/MMLib.Alvo.Testing/`** (shipped fakes + contract bases)
- Create `Migrations/InMemoryDescriptorVersionStore.cs` — fake store.
- Create `Migrations/InMemoryRuntimeSchemaWriter.cs` — fake writer.
- Create `Migrations/DescriptorVersionStoreContractTests.cs` — abstract contract.
- Create `Migrations/RuntimeSchemaWriterContractTests.cs` — abstract contract.

**`test/`**
- `MMLib.Alvo.Data.Sqlite.Tests` / `MMLib.Alvo.Data.PostgreSql.Tests.Integration` — concrete contract subclasses + integration.
- `MMLib.Alvo.Tests` — validator, mapper-rejection, runtime-service, property-based tests.
- Public-API baseline files under each package's `*.Tests`.

---

## Task 1: Reject `computed` — close the raw-DDL-splice vector (#20 HIGH)

Standalone security fix, no new ports. Today `DescriptorToSchemaMapper` copies `f.Computed` into `FieldSchema.ComputedExpression`, and `DescriptorModelBuilder.ConfigureField` splices it raw into `GENERATED ALWAYS AS (<raw>) STORED`. Until the CEL→SQL compiler (#21), a `computed` field is refused at mapping time and the raw splice is removed.

**Files:**
- Modify: `src/MMLib.Alvo/Descriptor/DescriptorToSchemaMapper.cs`
- Modify: `src/MMLib.Alvo.Data.EntityFrameworkCore/Internal/DescriptorModelBuilder.cs:66-69`
- Modify (fixture): `examples/complex-crm/*.json` (the descriptor carrying `computed`)
- Test: `test/MMLib.Alvo.Tests/Descriptor/DescriptorToSchemaMapperTests.cs`

**Interfaces:**
- Consumes: `AlvoDescriptor`, `FieldDescriptor.Computed` (existing).
- Produces: `DescriptorToSchemaMapper.Map` now throws `InvalidDataException` when any field has a non-null `Computed`. `FieldSchema.ComputedExpression` is no longer populated by the mapper (leave the property on the model — #21 revives it).

- [ ] **Step 1: Find the fixture that uses `computed`**

Run: `grep -rln '"computed"' examples/`
Expected: at least `examples/complex-crm/...json`. Note the exact path + field for Step 6.

- [ ] **Step 2: Write the failing test**

Create `test/MMLib.Alvo.Tests/Descriptor/DescriptorToSchemaMapperTests.cs`:

```csharp
using MMLib.Alvo.Descriptor;
using Shouldly;
using Xunit;

namespace MMLib.Alvo.Tests.Descriptor;

public class DescriptorToSchemaMapperTests
{
    private const string WithComputed = """
    {
      "apiVersion": "alvo.dev/v1",
      "name": "demo",
      "entities": {
        "invoices": {
          "fields": {
            "net": { "type": "decimal" },
            "gross": { "type": "decimal", "computed": "net * 1.2" }
          }
        }
      }
    }
    """;

    [Fact]
    public void Map_rejects_computed_until_cel_compiler()
    {
        var descriptor = AlvoDescriptor.Parse(WithComputed);

        var ex = Should.Throw<InvalidDataException>(() => DescriptorToSchemaMapper.Map(descriptor));

        ex.Message.ShouldContain("computed");
        ex.Message.ShouldContain("#21");
    }
}
```

- [ ] **Step 3: Run test to verify it fails**

Run: `scripts/test-ring0` (or `dotnet test test/MMLib.Alvo.Tests`)
Expected: FAIL — mapper currently maps `computed` without throwing.

- [ ] **Step 4: Reject `computed` in the mapper**

In `DescriptorToSchemaMapper.MapField`, before constructing the `FieldSchema`, add a guard and stop populating `ComputedExpression`:

```csharp
private static FieldSchema MapField(string name, FieldDescriptor f)
{
    if (f.Computed is not null)
    {
        throw new InvalidDataException(
            $"Field '{name}' declares 'computed', which is not supported yet: computed fields " +
            "require the CEL→SQL compiler arriving in #21. Remove 'computed' or track #21.");
    }

    return new()
    {
        Name = name,
        Type = MapType(f.Type),
        RenamedFrom = f.RenamedFrom,
        Required = f.Required == true,
        Unique = f.Unique == true,
        Nullable = f.Nullable ?? f.Required != true,
        MaxLength = f.MaxLength,
        Precision = f.Precision,
        Scale = f.Scale,
        EnumValues = f.Values,
        Reference = f.Entity is null ? null : new RefSchema(f.Entity, MapOnDelete(f.OnDelete)),
        Indexed = f.Index == true,
        // ComputedExpression intentionally not set — revived by #21 (CEL→SQL).
    };
}
```

- [ ] **Step 5: Remove the raw splice in `DescriptorModelBuilder`**

In `src/MMLib.Alvo.Data.EntityFrameworkCore/Internal/DescriptorModelBuilder.cs`, delete the `ComputedExpression` block (lines ~66-69):

```csharp
        // REMOVE these lines:
        if (field.ComputedExpression is { } computedExpression)
        {
            property.HasComputedColumnSql(computedExpression, stored: true);
        }
```

- [ ] **Step 6: Fix the `complex-crm` fixture**

Remove the `computed` key from the field found in Step 1 (keep the field as a plain column, or drop the field if it existed only to demo `computed`). Do NOT touch other fixtures. Re-run the schema examples test which validates every example against `project.schema.json`:

Run: `dotnet test test/MMLib.Alvo.Schema.Tests`
Expected: PASS (fixture still schema-valid; `computed` is optional in the JSON schema).

- [ ] **Step 7: Run tests to verify they pass**

Run: `scripts/test-ring0`
Expected: PASS — mapper rejects `computed`; existing migrator/model tests still green (no fixture reaches the removed splice).

- [ ] **Step 8: Commit**

```bash
git add src/MMLib.Alvo/Descriptor/DescriptorToSchemaMapper.cs \
        src/MMLib.Alvo.Data.EntityFrameworkCore/Internal/DescriptorModelBuilder.cs \
        test/MMLib.Alvo.Tests/Descriptor/DescriptorToSchemaMapperTests.cs examples/
git commit -m "fix(descriptor): reject 'computed' until CEL→SQL compiler (#20 HIGH)

Removes the raw descriptor-string -> GENERATED ALWAYS AS (...) STORED splice that
would let an untrusted runtime descriptor inject arbitrary DDL. Refused at mapping
time with a fix suggestion pointing at #21."
```

---

## Task 2: Descriptor validation port + layered validator (#20 MEDIUM)

Add the `IDescriptorValidator` port and a core implementation: a Corvus JSON-schema pass against `project.schema.json` (promoted into shipped code) + a semantic pass, both producing `DescriptorValidationError { Path, Message, FixSuggestion, Severity }`. Wire it into the code-first path (`SchemaMigrationRunner`), closing the gap that neither path validates today.

> **Packaging note (run `alvo-dotnet-conventions` first):** this adds `Corvus.Json.Validator` as a **shipped runtime dependency of `MMLib.Alvo`**. Confirm its licence is compatible with Alvo's before referencing it in `src/`. The central version (5.2.7) already exists in `Directory.Packages.props`.

**Files:**
- Create: `src/MMLib.Alvo.Abstractions/Descriptor/IDescriptorValidator.cs`
- Create: `src/MMLib.Alvo.Abstractions/Descriptor/DescriptorValidationResult.cs`
- Create: `src/MMLib.Alvo/Descriptor/Internal/DescriptorValidator.cs`
- Create: `src/MMLib.Alvo/Descriptor/Internal/DescriptorSchemaSource.cs`
- Modify: `src/MMLib.Alvo/MMLib.Alvo.csproj` (Corvus ref + embed `project.schema.json`)
- Modify: `src/MMLib.Alvo/Migrations/SchemaMigrationRunner.cs`
- Test: `test/MMLib.Alvo.Tests/Descriptor/DescriptorValidatorTests.cs`

**Interfaces:**
- Produces:
  - `record DescriptorValidationError(string Path, string Message, string? FixSuggestion, DescriptorValidationSeverity Severity)`
  - `enum DescriptorValidationSeverity { Error, Warning }`
  - `sealed record DescriptorValidationResult(IReadOnlyList<DescriptorValidationError> Errors) { bool IsValid => Errors.All(e => e.Severity != DescriptorValidationSeverity.Error); }`
  - `interface IDescriptorValidator { DescriptorValidationResult Validate(string descriptorJson); }`
  - `class DescriptorValidationException(DescriptorValidationResult result) : Exception` (thrown by the code-first path on invalid input).
- Consumes: `project.schema.json` (repo root `schema/`), Corvus `JsonSchema`.

- [ ] **Step 1: Write the port + result types**

Create `src/MMLib.Alvo.Abstractions/Descriptor/DescriptorValidationResult.cs`:

```csharp
namespace MMLib.Alvo.Descriptor;

/// <summary>Severity of a single descriptor validation finding.</summary>
public enum DescriptorValidationSeverity
{
    /// <summary>A blocking problem: the descriptor must not be applied.</summary>
    Error,

    /// <summary>A non-blocking advisory.</summary>
    Warning,
}

/// <summary>A single descriptor validation finding, agent-first: a JSON path, a message, and a fix suggestion.</summary>
/// <param name="Path">JSON pointer / path to the offending node (e.g. <c>/entities/invoices/fields/gross</c>).</param>
/// <param name="Message">What is wrong.</param>
/// <param name="FixSuggestion">How to fix it, if known.</param>
/// <param name="Severity">Whether this blocks apply.</param>
public sealed record DescriptorValidationError(
    string Path, string Message, string? FixSuggestion, DescriptorValidationSeverity Severity);

/// <summary>The outcome of validating a descriptor.</summary>
/// <param name="Errors">All findings, in document order.</param>
public sealed record DescriptorValidationResult(IReadOnlyList<DescriptorValidationError> Errors)
{
    /// <summary>Gets a value indicating whether the descriptor may be applied (no <see cref="DescriptorValidationSeverity.Error"/>).</summary>
    public bool IsValid => Errors.All(e => e.Severity != DescriptorValidationSeverity.Error);

    /// <summary>An empty, valid result.</summary>
    public static DescriptorValidationResult Valid { get; } = new([]);
}
```

Create `src/MMLib.Alvo.Abstractions/Descriptor/IDescriptorValidator.cs`:

```csharp
namespace MMLib.Alvo.Descriptor;

/// <summary>Validates a project descriptor before it is parsed and applied — the untrusted-input guardrail for the runtime path.</summary>
public interface IDescriptorValidator
{
    /// <summary>Validates descriptor JSON against the schema and Alvo's semantic rules.</summary>
    /// <param name="descriptorJson">The raw descriptor JSON.</param>
    /// <returns>All findings; check <see cref="DescriptorValidationResult.IsValid"/>.</returns>
    DescriptorValidationResult Validate(string descriptorJson);
}
```

- [ ] **Step 2: Embed `project.schema.json` in the core assembly**

In `src/MMLib.Alvo/MMLib.Alvo.csproj`, add the Corvus reference and embed the schema (the file lives at repo `schema/project.schema.json`; embed via a linked resource):

```xml
  <ItemGroup>
    <PackageReference Include="Microsoft.Extensions.Options" />
    <PackageReference Include="Microsoft.Extensions.Options.DataAnnotations" />
    <PackageReference Include="Corvus.Json.Validator" />
  </ItemGroup>

  <ItemGroup>
    <EmbeddedResource Include="../../schema/project.schema.json" Link="Descriptor/project.schema.json" LogicalName="MMLib.Alvo.project.schema.json" />
  </ItemGroup>
```

- [ ] **Step 3: Write the failing validator tests**

Create `test/MMLib.Alvo.Tests/Descriptor/DescriptorValidatorTests.cs`:

```csharp
using MMLib.Alvo.Descriptor;
using MMLib.Alvo.Descriptor.Internal;
using Shouldly;
using Xunit;

namespace MMLib.Alvo.Tests.Descriptor;

public class DescriptorValidatorTests
{
    private static readonly IDescriptorValidator Validator = new DescriptorValidator();

    [Fact]
    public void Valid_descriptor_passes()
    {
        var json = """
        { "apiVersion": "alvo.dev/v1", "name": "demo",
          "entities": { "tasks": { "fields": { "title": { "type": "string" } } } } }
        """;

        Validator.Validate(json).IsValid.ShouldBeTrue();
    }

    [Fact]
    public void Schema_violation_is_a_structured_error()
    {
        // 'name' is required by the schema; omit it.
        var json = """{ "apiVersion": "alvo.dev/v1", "entities": {} }""";

        var result = Validator.Validate(json);

        result.IsValid.ShouldBeFalse();
        result.Errors.ShouldContain(e => e.Severity == DescriptorValidationSeverity.Error);
        result.Errors.ShouldAllBe(e => e.Message.Length > 0);
    }

    [Fact]
    public void Computed_field_is_rejected_with_fix_suggestion()
    {
        var json = """
        { "apiVersion": "alvo.dev/v1", "name": "demo",
          "entities": { "invoices": { "fields": {
            "gross": { "type": "decimal", "computed": "net * 1.2" } } } } }
        """;

        var result = Validator.Validate(json);

        var computed = result.Errors.ShouldHaveSingleItem();
        computed.Path.ShouldContain("gross");
        computed.FixSuggestion.ShouldContain("#21");
    }

    [Fact]
    public void Ref_to_unknown_entity_is_rejected()
    {
        var json = """
        { "apiVersion": "alvo.dev/v1", "name": "demo",
          "entities": { "orders": { "fields": {
            "customer": { "type": "ref", "entity": "missing" } } } } }
        """;

        var result = Validator.Validate(json);

        result.IsValid.ShouldBeFalse();
        result.Errors.ShouldContain(e => e.Message.Contains("missing"));
    }
}
```

- [ ] **Step 4: Run tests to verify they fail**

Run: `dotnet test test/MMLib.Alvo.Tests`
Expected: FAIL — `DescriptorValidator` does not exist.

- [ ] **Step 5: Implement the schema source**

Create `src/MMLib.Alvo/Descriptor/Internal/DescriptorSchemaSource.cs`:

```csharp
using System.Reflection;

namespace MMLib.Alvo.Descriptor.Internal;

/// <summary>Reads the embedded <c>project.schema.json</c> once, so the validator needs no filesystem access.</summary>
internal static class DescriptorSchemaSource
{
    private const string ResourceName = "MMLib.Alvo.project.schema.json";

    /// <summary>Gets the embedded schema JSON text.</summary>
    public static string Json { get; } = Read();

    private static string Read()
    {
        var assembly = typeof(DescriptorSchemaSource).Assembly;
        using var stream = assembly.GetManifestResourceStream(ResourceName)
            ?? throw new InvalidOperationException($"Embedded resource '{ResourceName}' was not found.");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
}
```

- [ ] **Step 6: Implement the layered validator**

Create `src/MMLib.Alvo/Descriptor/Internal/DescriptorValidator.cs`. Layer 1 = Corvus schema pass (with a fix-suggestion adapter for keyword failures that Corvus leaves message-less — the same gap noted in `SnapshotTests`); Layer 2 = semantic pass (computed rejection, ref-target existence, duplicate field names). Keep each method short:

```csharp
using Corvus.Json;
using Corvus.Json.Validator;
using System.Text.Json;

namespace MMLib.Alvo.Descriptor.Internal;

/// <summary>
/// Layered descriptor validator: (1) a Corvus JSON-schema pass against the embedded
/// project.schema.json, (2) a semantic pass for cross-field rules Corvus cannot express, each
/// producing agent-first <see cref="DescriptorValidationError"/>s with fix suggestions.
/// </summary>
internal sealed class DescriptorValidator : IDescriptorValidator
{
    private static readonly JsonSchema Schema = JsonSchema.FromText(DescriptorSchemaSource.Json);

    public DescriptorValidationResult Validate(string descriptorJson)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(descriptorJson);

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(descriptorJson);
        }
        catch (JsonException ex)
        {
            return new DescriptorValidationResult([Malformed(ex)]);
        }

        using (document)
        {
            var errors = new List<DescriptorValidationError>();
            errors.AddRange(SchemaErrors(document.RootElement));
            errors.AddRange(SemanticErrors(document.RootElement));
            return new DescriptorValidationResult(errors);
        }
    }

    private static DescriptorValidationError Malformed(JsonException ex) =>
        new("/", $"Descriptor is not valid JSON: {ex.Message}", "Fix the JSON syntax.", DescriptorValidationSeverity.Error);

    private static IEnumerable<DescriptorValidationError> SchemaErrors(JsonElement root)
    {
        var context = Schema.Validate(root, ValidationLevel.Detailed);
        if (context.IsValid)
        {
            return [];
        }

        return context.Results
            .Where(r => !r.Valid)
            .Select(r => new DescriptorValidationError(
                r.Location?.DocumentLocation.ToString() ?? "/",
                MessageOrFallback(r),
                FixFor(r),
                DescriptorValidationSeverity.Error));
    }

    private static string MessageOrFallback(ValidationResult result) =>
        string.IsNullOrWhiteSpace(result.Message)
            ? "Value does not satisfy the project schema at this location."
            : result.Message;

    private static string? FixFor(ValidationResult result) =>
        "See schema/project.schema.json for the allowed shape at this path.";

    private static IEnumerable<DescriptorValidationError> SemanticErrors(JsonElement root)
    {
        if (!root.TryGetProperty("entities", out var entities) || entities.ValueKind != JsonValueKind.Object)
        {
            return [];
        }

        var entityNames = entities.EnumerateObject().Select(p => p.Name).ToHashSet(StringComparer.Ordinal);
        var errors = new List<DescriptorValidationError>();
        foreach (var entity in entities.EnumerateObject())
        {
            errors.AddRange(EntitySemanticErrors(entity, entityNames));
        }

        return errors;
    }

    private static IEnumerable<DescriptorValidationError> EntitySemanticErrors(
        JsonProperty entity, HashSet<string> entityNames)
    {
        if (!entity.Value.TryGetProperty("fields", out var fields) || fields.ValueKind != JsonValueKind.Object)
        {
            yield break;
        }

        foreach (var field in fields.EnumerateObject())
        {
            var path = $"/entities/{entity.Name}/fields/{field.Name}";
            if (field.Value.TryGetProperty("computed", out _))
            {
                yield return new DescriptorValidationError(
                    path,
                    "Computed fields are not supported yet.",
                    "Remove 'computed' or track the CEL→SQL compiler in #21.",
                    DescriptorValidationSeverity.Error);
            }

            if (IsUnknownRef(field.Value, entityNames, out var target))
            {
                yield return new DescriptorValidationError(
                    path,
                    $"Field references unknown entity '{target}'.",
                    $"Add an entity named '{target}', or point 'entity' at an existing one.",
                    DescriptorValidationSeverity.Error);
            }
        }
    }

    private static bool IsUnknownRef(JsonElement field, HashSet<string> entityNames, out string target)
    {
        target = "";
        if (!field.TryGetProperty("entity", out var entity) || entity.ValueKind != JsonValueKind.String)
        {
            return false;
        }

        target = entity.GetString() ?? "";
        return target.Length > 0 && !entityNames.Contains(target);
    }
}
```

- [ ] **Step 7: Run tests to verify they pass**

Run: `dotnet test test/MMLib.Alvo.Tests`
Expected: PASS.

- [ ] **Step 8: Wire validation into the code-first path**

Add `DescriptorValidationException` in Abstractions (`src/MMLib.Alvo.Abstractions/Descriptor/DescriptorValidationResult.cs`, append):

```csharp
/// <summary>Thrown when a descriptor fails validation before being applied.</summary>
public sealed class DescriptorValidationException(DescriptorValidationResult result)
    : Exception(BuildMessage(result))
{
    /// <summary>Gets the validation result whose errors caused this exception.</summary>
    public DescriptorValidationResult Result { get; } = result;

    private static string BuildMessage(DescriptorValidationResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        var lines = result.Errors
            .Where(e => e.Severity == DescriptorValidationSeverity.Error)
            .Select(e => $"  {e.Path}: {e.Message}{(e.FixSuggestion is null ? "" : $" — {e.FixSuggestion}")}");
        return "Descriptor validation failed:\n" + string.Join("\n", lines);
    }
}
```

In `SchemaMigrationRunner`, inject `IDescriptorValidator` and validate right after loading, before `AlvoDescriptor.Parse`:

```csharp
    private readonly IDescriptorValidator _validator;
    // ... add to ctor params + ArgumentNullException.ThrowIfNull + assignment ...

    // inside RunAsync, replace the load+parse block:
        var descriptorJson = await _source.LoadAsync(ct).ConfigureAwait(false);
        var validation = _validator.Validate(descriptorJson);
        if (!validation.IsValid)
        {
            throw new DescriptorValidationException(validation);
        }

        var descriptor = AlvoDescriptor.Parse(descriptorJson);
```

- [ ] **Step 9: Register the validator + run ring0**

In `AlvoServiceCollectionExtensions.AddAlvo`, register it (idempotent) before `SchemaMigrationRunner`:

```csharp
        services.TryAddSingleton<IDescriptorValidator, MMLib.Alvo.Descriptor.Internal.DescriptorValidator>();
        services.TryAddSingleton<SchemaMigrationRunner>();
```

Run: `scripts/test-ring0`
Expected: PASS. (If any existing `SchemaMigrationRunner` unit test constructs it directly, update it to pass a validator — use `new DescriptorValidator()`.)

- [ ] **Step 10: Commit**

```bash
git add src/MMLib.Alvo.Abstractions/Descriptor/ src/MMLib.Alvo/Descriptor/Internal/ \
        src/MMLib.Alvo/MMLib.Alvo.csproj src/MMLib.Alvo/Migrations/SchemaMigrationRunner.cs \
        src/MMLib.Alvo/AlvoServiceCollectionExtensions.cs test/MMLib.Alvo.Tests/Descriptor/DescriptorValidatorTests.cs
git commit -m "feat(descriptor): layered IDescriptorValidator with structured fix-suggestions (#20 MEDIUM)"
```

---

## Task 3: Per-call connection factory refactor

Replace each EF service's single owned `DbConnection` with a per-call factory, so two clients can hold independent connections/transactions. Pure refactor — the existing SQLite + PostgreSQL contract and integration tests are the safety net and must stay green.

**Files:**
- Create: `src/MMLib.Alvo.Data.EntityFrameworkCore/Internal/RelationalConnectionFactory.cs`
- Modify: `src/MMLib.Alvo.Data.EntityFrameworkCore/EfCoreSchemaMigrator.cs`
- Modify: `src/MMLib.Alvo.Data.EntityFrameworkCore/EfCoreSchemaIntrospector.cs`
- Modify: `src/MMLib.Alvo.Data.EntityFrameworkCore/AlvoEfCoreProvider.cs`

**Interfaces:**
- Produces: `sealed class RelationalConnectionFactory(Func<DbConnection> create) { DbConnection Create(); }` — each call returns a **new**, unopened connection the caller owns and disposes.
- Consumes: `RelationalProviderRegistration.CreateConnection` + resolved connection string (existing).

- [ ] **Step 1: Write the failing test**

Create `test/MMLib.Alvo.Data.EntityFrameworkCore.Tests/RelationalConnectionFactoryTests.cs`:

```csharp
using Microsoft.Data.Sqlite;
using MMLib.Alvo.Data.EntityFrameworkCore.Internal;
using Shouldly;
using Xunit;

namespace MMLib.Alvo.Data.EntityFrameworkCore.Tests;

public class RelationalConnectionFactoryTests
{
    [Fact]
    public void Create_returns_a_fresh_connection_each_call()
    {
        var factory = new RelationalConnectionFactory(() => new SqliteConnection("Data Source=:memory:"));

        using var a = factory.Create();
        using var b = factory.Create();

        a.ShouldNotBeSameAs(b);
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test test/MMLib.Alvo.Data.EntityFrameworkCore.Tests`
Expected: FAIL — type does not exist.

- [ ] **Step 3: Implement the factory**

Create `src/MMLib.Alvo.Data.EntityFrameworkCore/Internal/RelationalConnectionFactory.cs`:

```csharp
using System.Data.Common;

namespace MMLib.Alvo.Data.EntityFrameworkCore.Internal;

/// <summary>
/// Creates a fresh, unopened ADO.NET <see cref="DbConnection"/> per call, so callers that need
/// genuinely concurrent work (runtime schema changes by independent clients) each own their own
/// connection and transaction instead of serializing on one shared connection.
/// </summary>
internal sealed class RelationalConnectionFactory(Func<DbConnection> create)
{
    private readonly Func<DbConnection> _create = create ?? throw new ArgumentNullException(nameof(create));

    /// <summary>Creates a new, unopened connection the caller owns and must dispose.</summary>
    public DbConnection Create() => _create();
}
```

- [ ] **Step 4: Refactor `EfCoreSchemaMigrator` to per-call connections**

Replace the owned `_connection` + `_gate` with a `RelationalConnectionFactory`. `PlanAsync` never touched the connection (pure diff) — leave it. In `ApplyAsync`, open a fresh connection per call in a `using`, drop the semaphore (per-call connections don't race), and drop `IDisposable`:

```csharp
    private readonly RelationalConnectionFactory _connections;
    // ctor: replace `DbConnection connection` param with `RelationalConnectionFactory connections`,
    // ArgumentNullException.ThrowIfNull(connections), assign. Remove _gate and IDisposable/Dispose.

    public async Task<MigrationResult> ApplyAsync(MigrationPlan plan, MigrationOptions options, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(options);
        ct.ThrowIfCancellationRequested();

        if (plan.HasDestructiveChanges && !options.AllowDestructive)
        {
            return new MigrationResult(false, plan, options.DryRun);
        }

        if (options.DryRun)
        {
            return new MigrationResult(false, plan, true);
        }

        var connection = _connections.Create();
        await using (connection.ConfigureAwait(false))
        {
            await RelationalSqlBatch.ExecuteAsync(connection, plan.Sql, ct).ConfigureAwait(false);
        }

        return new MigrationResult(true, plan, false);
    }
```

> `RelationalSqlBatch.ExecuteAsync` is created in Task 6 Step 3. For THIS task, temporarily inline the existing open→begin-transaction→execute→commit loop against the fresh `connection` (copy the body of the current `ApplyAsync` + `ExecuteInTransactionAsync`, using the local `connection`). Task 6 replaces the inline copy with the shared helper. This keeps Task 3 self-contained and green.

- [ ] **Step 5: Refactor `EfCoreSchemaIntrospector` to per-call connections**

Change its ctor to take `RelationalConnectionFactory` instead of an owned `DbConnection`; open a fresh connection per `IntrospectAsync` call inside a `using`; drop `IDisposable` if present. Keep the excluded-table-name parameter.

- [ ] **Step 6: Rewire `AlvoEfCoreProvider` to build one factory**

In `AlvoEfCoreProvider`, register a `RelationalConnectionFactory` singleton and hand it to the migrator + introspector (the applied-schema store is replaced in Task 5, so leave `AppliedSchemaStore` wiring untouched here):

```csharp
    // In AddRelationalProvider, before the TryAddSingleton calls:
    builder.Services.TryAddSingleton(sp => CreateConnectionFactory(sp, registration));

    // Update CreateMigrator / CreateIntrospector to resolve the factory from sp and pass it in.

    private static RelationalConnectionFactory CreateConnectionFactory(IServiceProvider services, RelationalProviderRegistration registration)
    {
        var connectionString = registration.ConnectionString(services);
        return new RelationalConnectionFactory(() => registration.CreateConnection(connectionString));
    }
```

Update `CreateMigrator` / `CreateIntrospector` to resolve `RelationalConnectionFactory` from `services` and pass it to the constructors (drop the `registration.CreateConnection(connectionString)` argument they passed before). Update the `AlvoEfCoreProvider` XML remark ("each owns one ADO.NET connection for the container's lifetime") to describe per-call connections.

- [ ] **Step 7: Run the full EF + provider test suites**

Run: `dotnet test test/MMLib.Alvo.Data.Sqlite.Tests test/MMLib.Alvo.Data.EntityFrameworkCore.Tests`
Then (PostgreSQL): `scripts/test-ring2` locally if Docker is available, else rely on CI.
Expected: PASS — behavior unchanged, connections now per-call.

- [ ] **Step 8: Commit**

```bash
git add src/MMLib.Alvo.Data.EntityFrameworkCore/ test/MMLib.Alvo.Data.EntityFrameworkCore.Tests/RelationalConnectionFactoryTests.cs
git commit -m "refactor(data): per-call connection factory for EF schema services

Replaces each service's single owned DbConnection with a RelationalConnectionFactory
so runtime concurrent clients hold independent connections/transactions. Existing
SQLite + PostgreSQL contract/integration tests unchanged and green."
```

---

## Task 4: `DescriptorVersion` model + `IDescriptorVersionStore` port + in-memory fake + contract base

Pure model/ports in Abstractions, plus a shipped in-memory fake and an abstract contract base (red until Task 5 wires real providers). Append-only history, revision-based conflict.

**Files:**
- Create: `src/MMLib.Alvo.Abstractions/Migrations/DescriptorVersion.cs`
- Create: `src/MMLib.Alvo.Abstractions/Migrations/IDescriptorVersionStore.cs`
- Create: `src/MMLib.Alvo.Abstractions/Migrations/DescriptorConcurrencyException.cs`
- Create: `src/MMLib.Alvo.Testing/Migrations/InMemoryDescriptorVersionStore.cs`
- Create: `src/MMLib.Alvo.Testing/Migrations/DescriptorVersionStoreContractTests.cs`
- Test: `test/MMLib.Alvo.Tests/Migrations/InMemoryDescriptorVersionStoreTests.cs`

**Interfaces:**
- Produces:
  - `sealed record DescriptorVersion(SchemaModel Schema, string DescriptorJson, int Revision, DateTimeOffset CreatedAt, string? Author, string? Reason, int? RolledBackFrom)`
  - `class DescriptorConcurrencyException(string project, int expectedRevision, int actualRevision) : Exception`
  - ```csharp
    interface IDescriptorVersionStore
    {
        Task<DescriptorVersion?> GetCurrentAsync(string project, CancellationToken ct = default);
        Task<DescriptorVersion?> GetAsync(string project, int revision, CancellationToken ct = default);
        Task<IReadOnlyList<DescriptorVersion>> ListAsync(string project, CancellationToken ct = default);
        Task<DescriptorVersion> AppendAsync(string project, DescriptorVersion candidate, int expectedRevision, CancellationToken ct = default);
    }
    ```
  - `AppendAsync` contract: inserts `candidate` iff `expectedRevision == current revision (0 when empty)`; the inserted row's `Revision` is `expectedRevision + 1`; throws `DescriptorConcurrencyException` otherwise. History is never mutated.

- [ ] **Step 1: Write the record, exception, and port**

Create `src/MMLib.Alvo.Abstractions/Migrations/DescriptorVersion.cs`:

```csharp
using MMLib.Alvo.Schema;

namespace MMLib.Alvo.Migrations;

/// <summary>
/// One immutable entry in a project's append-only descriptor history. Code-first and runtime
/// apply both append here; the latest revision is the "current" side of the migration diff.
/// </summary>
/// <param name="Schema">The applied <see cref="SchemaModel"/> at this revision.</param>
/// <param name="DescriptorJson">The raw descriptor JSON this revision was derived from.</param>
/// <param name="Revision">The monotonically increasing revision number (first applied revision is 1).</param>
/// <param name="CreatedAt">When this revision was appended.</param>
/// <param name="Author">Who appended it (null for code-first / system).</param>
/// <param name="Reason">Optional human/agent-supplied reason.</param>
/// <param name="RolledBackFrom">If this revision was produced by a rollback, the revision it restored; otherwise null.</param>
public sealed record DescriptorVersion(
    SchemaModel Schema,
    string DescriptorJson,
    int Revision,
    DateTimeOffset CreatedAt,
    string? Author = null,
    string? Reason = null,
    int? RolledBackFrom = null);
```

Create `src/MMLib.Alvo.Abstractions/Migrations/DescriptorConcurrencyException.cs`:

```csharp
namespace MMLib.Alvo.Migrations;

/// <summary>
/// Thrown when an append loses the optimistic-lock race: the caller's expected revision no longer
/// matches the store's current revision, so another client changed the descriptor first.
/// </summary>
public sealed class DescriptorConcurrencyException(string project, int expectedRevision, int actualRevision)
    : Exception($"Descriptor for project '{project}' changed concurrently: expected revision {expectedRevision}, but current is {actualRevision}. Reload the latest revision and retry.")
{
    /// <summary>Gets the project whose append conflicted.</summary>
    public string Project { get; } = project;

    /// <summary>Gets the revision the caller expected to be current.</summary>
    public int ExpectedRevision { get; } = expectedRevision;

    /// <summary>Gets the revision that was actually current.</summary>
    public int ActualRevision { get; } = actualRevision;
}
```

Create `src/MMLib.Alvo.Abstractions/Migrations/IDescriptorVersionStore.cs` with the four methods and full XML docs (signatures from **Interfaces** above).

- [ ] **Step 2: Write the in-memory fake**

Create `src/MMLib.Alvo.Testing/Migrations/InMemoryDescriptorVersionStore.cs`:

```csharp
using MMLib.Alvo.Migrations;

namespace MMLib.Alvo.Testing.Migrations;

/// <summary>A DB-less append-only <see cref="IDescriptorVersionStore"/> fake for tests.</summary>
public sealed class InMemoryDescriptorVersionStore : IDescriptorVersionStore
{
    private readonly Dictionary<string, List<DescriptorVersion>> _history = new(StringComparer.Ordinal);
    private readonly Lock _gate = new();

    public Task<DescriptorVersion?> GetCurrentAsync(string project, CancellationToken ct = default)
    {
        lock (_gate)
        {
            return Task.FromResult(Current(project));
        }
    }

    public Task<DescriptorVersion?> GetAsync(string project, int revision, CancellationToken ct = default)
    {
        lock (_gate)
        {
            var version = History(project).FirstOrDefault(v => v.Revision == revision);
            return Task.FromResult(version);
        }
    }

    public Task<IReadOnlyList<DescriptorVersion>> ListAsync(string project, CancellationToken ct = default)
    {
        lock (_gate)
        {
            return Task.FromResult<IReadOnlyList<DescriptorVersion>>([.. History(project)]);
        }
    }

    public Task<DescriptorVersion> AppendAsync(string project, DescriptorVersion candidate, int expectedRevision, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(project);
        ArgumentNullException.ThrowIfNull(candidate);

        lock (_gate)
        {
            var current = Current(project)?.Revision ?? 0;
            if (current != expectedRevision)
            {
                throw new DescriptorConcurrencyException(project, expectedRevision, current);
            }

            var appended = candidate with { Revision = expectedRevision + 1 };
            History(project).Add(appended);
            return Task.FromResult(appended);
        }
    }

    private DescriptorVersion? Current(string project) => History(project).LastOrDefault();

    private List<DescriptorVersion> History(string project)
    {
        if (!_history.TryGetValue(project, out var list))
        {
            list = [];
            _history[project] = list;
        }

        return list;
    }
}
```

- [ ] **Step 3: Write the abstract contract base**

Create `src/MMLib.Alvo.Testing/Migrations/DescriptorVersionStoreContractTests.cs`:

```csharp
using MMLib.Alvo.Migrations;
using MMLib.Alvo.Schema;
using Shouldly;
using Xunit;

namespace MMLib.Alvo.Testing.Migrations;

/// <summary>Behavioral contract every <see cref="IDescriptorVersionStore"/> must satisfy — fake and real alike.</summary>
public abstract class DescriptorVersionStoreContractTests
{
    /// <summary>Creates the store under test.</summary>
    protected abstract IDescriptorVersionStore CreateStore();

    /// <summary>No-op unless the engine must be skipped in this environment.</summary>
    protected virtual void EnsureEngineAvailable() { }

    private static DescriptorVersion Candidate(string json = "{}") =>
        new(new SchemaModel([]), json, Revision: 0, CreatedAt: DateTimeOffset.UnixEpoch);

    [Fact]
    public async Task First_append_is_revision_1()
    {
        EnsureEngineAvailable();
        var store = CreateStore();

        var appended = await store.AppendAsync("p", Candidate(), expectedRevision: 0);

        appended.Revision.ShouldBe(1);
        (await store.GetCurrentAsync("p"))!.Revision.ShouldBe(1);
    }

    [Fact]
    public async Task History_is_append_only_and_ordered()
    {
        EnsureEngineAvailable();
        var store = CreateStore();
        await store.AppendAsync("p", Candidate("{\"v\":1}"), 0);
        await store.AppendAsync("p", Candidate("{\"v\":2}"), 1);

        var history = await store.ListAsync("p");

        history.Select(v => v.Revision).ShouldBe([1, 2]);
    }

    [Fact]
    public async Task Stale_expected_revision_conflicts()
    {
        EnsureEngineAvailable();
        var store = CreateStore();
        await store.AppendAsync("p", Candidate(), 0);

        var ex = await Should.ThrowAsync<DescriptorConcurrencyException>(
            () => store.AppendAsync("p", Candidate(), expectedRevision: 0));

        ex.ExpectedRevision.ShouldBe(0);
        ex.ActualRevision.ShouldBe(1);
    }

    [Fact]
    public async Task Get_returns_a_specific_historical_revision()
    {
        EnsureEngineAvailable();
        var store = CreateStore();
        await store.AppendAsync("p", Candidate("{\"v\":1}"), 0);
        await store.AppendAsync("p", Candidate("{\"v\":2}"), 1);

        (await store.GetAsync("p", 1))!.DescriptorJson.ShouldContain("\"v\":1");
    }
}
```

- [ ] **Step 4: Write the fake's concrete test + run**

Create `test/MMLib.Alvo.Tests/Migrations/InMemoryDescriptorVersionStoreTests.cs`:

```csharp
using MMLib.Alvo.Migrations;
using MMLib.Alvo.Testing.Migrations;

namespace MMLib.Alvo.Tests.Migrations;

public sealed class InMemoryDescriptorVersionStoreTests : DescriptorVersionStoreContractTests
{
    protected override IDescriptorVersionStore CreateStore() => new InMemoryDescriptorVersionStore();
}
```

Run: `dotnet test test/MMLib.Alvo.Tests`
Expected: PASS (fake satisfies the contract).

- [ ] **Step 5: Commit**

```bash
git add src/MMLib.Alvo.Abstractions/Migrations/ src/MMLib.Alvo.Testing/Migrations/ test/MMLib.Alvo.Tests/Migrations/
git commit -m "feat(migrations): append-only IDescriptorVersionStore port + in-memory fake + contract"
```

---

## Task 5: `EfCoreDescriptorVersionStore` — append-only table + optimistic append

Evolve the system table from single-row `applied_schema` to append-only `descriptor_versions` `(project, revision)`, implement the real store (also satisfying `IAppliedSchemaStore` so the code-first runner is untouched), and pass the contract on SQLite + PostgreSQL.

> No data migration is needed: #18 has not shipped a release, so the system table is greenfield — change its shape directly.

**Files:**
- Modify: `src/MMLib.Alvo.Data.EntityFrameworkCore/Internal/SystemSchemaInitializer.cs`
- Create: `src/MMLib.Alvo.Data.EntityFrameworkCore/EfCoreDescriptorVersionStore.cs`
- Delete: `src/MMLib.Alvo.Data.EntityFrameworkCore/Internal/AppliedSchemaStore.cs`
- Modify: `src/MMLib.Alvo.Data.EntityFrameworkCore/Internal/AppliedSchemaJsonContext.cs` (serialize `SchemaModel` — already present; reuse)
- Modify: `src/MMLib.Alvo.Data.EntityFrameworkCore/EfCoreSchemaIntrospector.cs` (excluded table name)
- Modify: `src/MMLib.Alvo.Data.EntityFrameworkCore/AlvoEfCoreProvider.cs` (register store for both interfaces)
- Test: `test/MMLib.Alvo.Data.Sqlite.Tests/SqliteDescriptorVersionStoreTests.cs`
- Test: `test/MMLib.Alvo.Data.PostgreSql.Tests.Integration/PostgreSqlDescriptorVersionStoreTests.cs`

**Interfaces:**
- Consumes: `RelationalConnectionFactory` (Task 3), `IDescriptorVersionStore` + `DescriptorConcurrencyException` (Task 4), `AlvoOptions.SchemaPrefix`.
- Produces: `EfCoreDescriptorVersionStore : IDescriptorVersionStore, IAppliedSchemaStore` (namespace `MMLib.Alvo.Data.EntityFrameworkCore`). `SystemSchemaInitializer` now exposes `DescriptorVersionsTableName(prefix)` returning `{prefix}_descriptor_versions`.

- [ ] **Step 1: Evolve `SystemSchemaInitializer` (append-only DDL)**

Rename the table + make the PK composite. Update the method name and DDL:

```csharp
    // Replace AppliedSchemaTableName with:
    public static string DescriptorVersionsTableName(string schemaPrefix) => $"{schemaPrefix}_descriptor_versions";

    // In the ctor, set TableName = DescriptorVersionsTableName(schemaPrefix);

    // DDL (identical on SQLite + PostgreSQL):
    command.CommandText =
        $"""
        CREATE TABLE IF NOT EXISTS {TableName} (
            project TEXT NOT NULL,
            revision INTEGER NOT NULL,
            descriptor_json TEXT NOT NULL,
            schema_json TEXT NOT NULL,
            author TEXT NULL,
            reason TEXT NULL,
            rolled_back_from INTEGER NULL,
            created_at TEXT NOT NULL,
            PRIMARY KEY (project, revision)
        )
        """;
```

- [ ] **Step 2: Write the concrete SQLite contract test (red)**

Create `test/MMLib.Alvo.Data.Sqlite.Tests/SqliteDescriptorVersionStoreTests.cs`, mirroring how the existing `SqliteSchemaMigratorTests` builds its provider services (reuse that test's fixture/helper for a temp-file SQLite connection + `AlvoOptions`):

```csharp
using MMLib.Alvo.Migrations;
using MMLib.Alvo.Testing.Migrations;

namespace MMLib.Alvo.Data.Sqlite.Tests;

public sealed class SqliteDescriptorVersionStoreTests : DescriptorVersionStoreContractTests, IDisposable
{
    // Build a RelationalConnectionFactory over a temp .db file exactly as the migrator tests do.
    // (Copy the temp-file setup helper already used by SqliteSchemaMigratorTests.)
    protected override IDescriptorVersionStore CreateStore() => /* new EfCoreDescriptorVersionStore(factory, options) */;

    public void Dispose() { /* delete temp file */ }
}
```

Run: `dotnet test test/MMLib.Alvo.Data.Sqlite.Tests`
Expected: FAIL — `EfCoreDescriptorVersionStore` does not exist.

- [ ] **Step 3: Implement `EfCoreDescriptorVersionStore`**

Create `src/MMLib.Alvo.Data.EntityFrameworkCore/EfCoreDescriptorVersionStore.cs`. Open a fresh connection per call via the factory; ensure the table once per connection; `AppendAsync` inserts `(project, expectedRevision+1)` and translates the PK unique violation (or a pre-check under a transaction) into `DescriptorConcurrencyException`. Use a transactional read-max-then-insert so the check and insert are atomic on one connection:

```csharp
using MMLib.Alvo.Data.EntityFrameworkCore.Internal;
using MMLib.Alvo.Migrations;
using MMLib.Alvo.Schema;
using System.Data;
using System.Data.Common;
using System.Globalization;
using System.Text.Json;

namespace MMLib.Alvo.Data.EntityFrameworkCore;

/// <summary>
/// Append-only <see cref="IDescriptorVersionStore"/> (and back-compatible <see cref="IAppliedSchemaStore"/>)
/// over a single <c>{prefix}_descriptor_versions</c> table, reached through per-call connections and
/// engine-agnostic SQL (identical on SQLite and PostgreSQL).
/// </summary>
internal sealed class EfCoreDescriptorVersionStore : IDescriptorVersionStore, IAppliedSchemaStore
{
    private readonly RelationalConnectionFactory _connections;
    private readonly string _schemaPrefix;

    public EfCoreDescriptorVersionStore(RelationalConnectionFactory connections, AlvoOptions options)
    {
        ArgumentNullException.ThrowIfNull(connections);
        ArgumentNullException.ThrowIfNull(options);
        _connections = connections;
        _schemaPrefix = options.SchemaPrefix;
    }

    private string TableName => SystemSchemaInitializer.DescriptorVersionsTableName(_schemaPrefix);

    public async Task<DescriptorVersion> AppendAsync(string project, DescriptorVersion candidate, int expectedRevision, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(project);
        ArgumentNullException.ThrowIfNull(candidate);

        var connection = _connections.Create();
        await using (connection.ConfigureAwait(false))
        {
            await EnsureReadyAsync(connection, ct).ConfigureAwait(false);
            var transaction = await BeginAsync(connection, ct).ConfigureAwait(false);
            await using (transaction.ConfigureAwait(false))
            {
                var current = await ReadCurrentRevisionAsync(connection, transaction, project, ct).ConfigureAwait(false);
                if (current != expectedRevision)
                {
                    await transaction.RollbackAsync(ct).ConfigureAwait(false);
                    throw new DescriptorConcurrencyException(project, expectedRevision, current);
                }

                var appended = candidate with { Revision = expectedRevision + 1 };
                await InsertAsync(connection, transaction, project, appended, ct).ConfigureAwait(false);
                await transaction.CommitAsync(ct).ConfigureAwait(false);
                return appended;
            }
        }
    }

    // GetCurrentAsync / GetAsync / ListAsync: open connection, EnsureReady, SELECT ... ORDER BY revision.
    // IAppliedSchemaStore.GetCurrentAsync(project) -> map latest DescriptorVersion to AppliedSchema.
    // IAppliedSchemaStore.SaveAsync(project, snapshot) -> AppendAsync(project, ToVersion(snapshot), snapshot.Revision - 1).
    // ... (read/insert/EnsureReady/serialize helpers, each short; SchemaModel (de)serialized via AppliedSchemaJsonContext) ...
}
```

Add the remaining short private helpers (`EnsureReadyAsync` using `SystemSchemaInitializer` against the passed connection; `BeginAsync`; `ReadCurrentRevisionAsync` = `SELECT COALESCE(MAX(revision),0) FROM {TableName} WHERE project=@project`; `InsertAsync` = parameterized `INSERT`; row↔record mappers reusing `AppliedSchemaJsonContext.Default.SchemaModel`).

- [ ] **Step 4: Implement `IAppliedSchemaStore` on top**

`GetCurrentAsync` maps the latest `DescriptorVersion` → `AppliedSchema(Schema, DescriptorJson, Revision, CreatedAt)`. `SaveAsync(project, snapshot)` → `AppendAsync(project, new DescriptorVersion(snapshot.Schema, snapshot.DescriptorJson, 0, snapshot.UpdatedAt), snapshot.Revision - 1)`. This keeps `SchemaMigrationRunner` (which computes `revision = prev + 1` then saves) working unchanged.

- [ ] **Step 5: Update introspector's excluded table + provider wiring**

In `EfCoreSchemaIntrospector`, change the excluded-table argument source from `AppliedSchemaTableName` to `DescriptorVersionsTableName`. In `AlvoEfCoreProvider`, delete the `AppliedSchemaStore` registration and register the new store for both ports:

```csharp
    builder.Services.TryAddSingleton<EfCoreDescriptorVersionStore>(sp =>
        new EfCoreDescriptorVersionStore(
            sp.GetRequiredService<RelationalConnectionFactory>(),
            sp.GetRequiredService<IOptions<AlvoOptions>>().Value));
    builder.Services.TryAddSingleton<IDescriptorVersionStore>(sp => sp.GetRequiredService<EfCoreDescriptorVersionStore>());
    builder.Services.TryAddSingleton<IAppliedSchemaStore>(sp => sp.GetRequiredService<EfCoreDescriptorVersionStore>());
```

Delete `src/MMLib.Alvo.Data.EntityFrameworkCore/Internal/AppliedSchemaStore.cs` and its `CreateAppliedSchemaStore` factory in `AlvoEfCoreProvider`.

- [ ] **Step 6: Run SQLite tests, then wire the PostgreSQL contract**

Run: `dotnet test test/MMLib.Alvo.Data.Sqlite.Tests`
Expected: PASS.

Create `test/MMLib.Alvo.Data.PostgreSql.Tests.Integration/PostgreSqlDescriptorVersionStoreTests.cs` subclassing the contract, reusing the existing PostgreSQL Testcontainers fixture (mirror `PostgreSqlSchemaMigratorTests`), overriding `EnsureEngineAvailable` to skip when Docker is unavailable.

- [ ] **Step 7: Run ring1 (+ ring2 if Docker present)**

Run: `scripts/test-ring1`
Expected: PASS. Existing code-first integration tests still green (store swap is transparent via `IAppliedSchemaStore`).

- [ ] **Step 8: Commit**

```bash
git add src/MMLib.Alvo.Data.EntityFrameworkCore/ test/MMLib.Alvo.Data.Sqlite.Tests/ test/MMLib.Alvo.Data.PostgreSql.Tests.Integration/
git commit -m "feat(data): append-only EfCoreDescriptorVersionStore with optimistic-lock append"
```

---

## Task 6: `IRuntimeSchemaWriter` port + shared SQL batch + atomic EF writer

The atomicity seam: apply `plan.Sql` **and** the conditional version-insert in one transaction. Extract the migrator's SQL-execution loop into a shared helper first (DRY), then build the writer on it.

**Files:**
- Create: `src/MMLib.Alvo.Abstractions/Migrations/IRuntimeSchemaWriter.cs`
- Create: `src/MMLib.Alvo.Data.EntityFrameworkCore/Internal/RelationalSqlBatch.cs`
- Create: `src/MMLib.Alvo.Data.EntityFrameworkCore/EfCoreRuntimeSchemaWriter.cs`
- Create: `src/MMLib.Alvo.Testing/Migrations/InMemoryRuntimeSchemaWriter.cs`
- Create: `src/MMLib.Alvo.Testing/Migrations/RuntimeSchemaWriterContractTests.cs`
- Modify: `src/MMLib.Alvo.Data.EntityFrameworkCore/EfCoreSchemaMigrator.cs` (use `RelationalSqlBatch`)
- Modify: `src/MMLib.Alvo.Data.EntityFrameworkCore/AlvoEfCoreProvider.cs` (register writer)
- Test: `test/MMLib.Alvo.Data.Sqlite.Tests/SqliteRuntimeSchemaWriterTests.cs`

**Interfaces:**
- Produces:
  - ```csharp
    interface IRuntimeSchemaWriter
    {
        Task<DescriptorVersion> ApplyAndAppendAsync(
            string project, MigrationPlan plan, DescriptorVersion candidate,
            int expectedRevision, MigrationOptions options, CancellationToken ct = default);
    }
    ```
  - Contract: refuse a destructive plan when `!options.AllowDestructive` (return without applying — throw `InvalidOperationException`? no: mirror migrator, but here the caller pre-checks the guardrail, so the writer **executes** and appends; the guardrail lives in `RuntimeSchemaService`). The writer atomically runs `plan.Sql` then conditionally inserts `(project, expectedRevision+1)`; a lost race rolls back **both** and throws `DescriptorConcurrencyException`.
  - `internal static class RelationalSqlBatch { static Task ExecuteAsync(DbConnection open, IReadOnlyList<string> sql, DbTransaction tx, CancellationToken ct); static Task ExecuteAsync(DbConnection connection, IReadOnlyList<string> sql, CancellationToken ct); }` — the two-arg overload opens+transacts+commits; the tx overload runs within a caller's transaction.

- [ ] **Step 1: Write the port**

Create `src/MMLib.Alvo.Abstractions/Migrations/IRuntimeSchemaWriter.cs` with the signature + full docs (from **Interfaces**). Emphasize in the docs: the guardrail (destructive check) is the caller's responsibility; this port is the atomic writer.

- [ ] **Step 2: Write the writer contract base + fake**

Create `src/MMLib.Alvo.Testing/Migrations/RuntimeSchemaWriterContractTests.cs`. Because a real plan's SQL is provider-specific, the contract exercises the **conflict + append semantics** with an empty-SQL plan (`new MigrationPlan { Steps = [] , Sql = [] }`), leaving DDL-execution proof to the integration test:

```csharp
[Fact]
public async Task Winner_appends_loser_conflicts()
{
    EnsureEngineAvailable();
    var writer = CreateWriter();
    var store = /* the same store the writer writes to */;
    var plan = new MigrationPlan { Steps = [] };
    var candidate = new DescriptorVersion(new SchemaModel([]), "{}", 0, DateTimeOffset.UnixEpoch);

    await writer.ApplyAndAppendAsync("p", plan, candidate, expectedRevision: 0, new MigrationOptions());

    await Should.ThrowAsync<DescriptorConcurrencyException>(
        () => writer.ApplyAndAppendAsync("p", plan, candidate, expectedRevision: 0, new MigrationOptions()));
}
```

Create `InMemoryRuntimeSchemaWriter` that delegates to an injected `InMemoryDescriptorVersionStore` (append with the expected revision; ignore `plan.Sql`).

- [ ] **Step 3: Extract `RelationalSqlBatch`**

Create `src/MMLib.Alvo.Data.EntityFrameworkCore/Internal/RelationalSqlBatch.cs` by lifting the loop from `EfCoreSchemaMigrator.ExecuteInTransactionAsync` + the open/begin/commit wrapper from `ApplyAsync`:

```csharp
using System.Data;
using System.Data.Common;

namespace MMLib.Alvo.Data.EntityFrameworkCore.Internal;

/// <summary>Runs an ordered list of SQL commands, either in a caller's transaction or in its own.</summary>
internal static class RelationalSqlBatch
{
    /// <summary>Opens <paramref name="connection"/>, runs <paramref name="sql"/> in one transaction, and commits.</summary>
    public static async Task ExecuteAsync(DbConnection connection, IReadOnlyList<string> sql, CancellationToken ct)
    {
        await OpenAsync(connection, ct).ConfigureAwait(false);
        var transaction = await connection.BeginTransactionAsync(ct).ConfigureAwait(false);
        await using (transaction.ConfigureAwait(false))
        {
            await ExecuteAsync(connection, sql, transaction, ct).ConfigureAwait(false);
            await transaction.CommitAsync(ct).ConfigureAwait(false);
        }
    }

    /// <summary>Runs <paramref name="sql"/> against an already-open connection within <paramref name="transaction"/>.</summary>
    public static async Task ExecuteAsync(DbConnection connection, IReadOnlyList<string> sql, DbTransaction transaction, CancellationToken ct)
    {
        foreach (var commandText in sql)
        {
            if (string.IsNullOrWhiteSpace(commandText))
            {
                continue;
            }

            var command = connection.CreateCommand();
            await using (command.ConfigureAwait(false))
            {
                command.CommandText = commandText;
                command.Transaction = transaction;
                await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
            }
        }
    }

    public static async Task OpenAsync(DbConnection connection, CancellationToken ct)
    {
        if (connection.State != ConnectionState.Open)
        {
            await connection.OpenAsync(ct).ConfigureAwait(false);
        }
    }
}
```

Then replace the inline copy in `EfCoreSchemaMigrator.ApplyAsync` (from Task 3 Step 4) with `RelationalSqlBatch.ExecuteAsync(connection, plan.Sql, ct)` and delete the now-dead `ExecuteInTransactionAsync`.

- [ ] **Step 4: Implement `EfCoreRuntimeSchemaWriter`**

Create `src/MMLib.Alvo.Data.EntityFrameworkCore/EfCoreRuntimeSchemaWriter.cs`. One connection, one transaction, both DDL and the conditional insert; reuse the store's insert SQL via a shared internal method or duplicate the short parameterized insert here:

```csharp
using MMLib.Alvo.Data.EntityFrameworkCore.Internal;
using MMLib.Alvo.Migrations;

namespace MMLib.Alvo.Data.EntityFrameworkCore;

internal sealed class EfCoreRuntimeSchemaWriter(RelationalConnectionFactory connections, AlvoOptions options)
    : IRuntimeSchemaWriter
{
    private readonly RelationalConnectionFactory _connections = connections ?? throw new ArgumentNullException(nameof(connections));
    private readonly VersionRowWriter _rows = new(options);

    public async Task<DescriptorVersion> ApplyAndAppendAsync(
        string project, MigrationPlan plan, DescriptorVersion candidate,
        int expectedRevision, MigrationOptions options, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(project);
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(candidate);

        var connection = _connections.Create();
        await using (connection.ConfigureAwait(false))
        {
            await _rows.EnsureReadyAsync(connection, ct).ConfigureAwait(false);
            await RelationalSqlBatch.OpenAsync(connection, ct).ConfigureAwait(false);
            var transaction = await connection.BeginTransactionAsync(ct).ConfigureAwait(false);
            await using (transaction.ConfigureAwait(false))
            {
                var current = await _rows.ReadCurrentRevisionAsync(connection, transaction, project, ct).ConfigureAwait(false);
                if (current != expectedRevision)
                {
                    await transaction.RollbackAsync(ct).ConfigureAwait(false);
                    throw new DescriptorConcurrencyException(project, expectedRevision, current);
                }

                await RelationalSqlBatch.ExecuteAsync(connection, plan.Sql, transaction, ct).ConfigureAwait(false);
                var appended = candidate with { Revision = expectedRevision + 1 };
                await _rows.InsertAsync(connection, transaction, project, appended, ct).ConfigureAwait(false);
                await transaction.CommitAsync(ct).ConfigureAwait(false);
                return appended;
            }
        }
    }
}
```

> Extract the shared `EnsureReadyAsync` / `ReadCurrentRevisionAsync` / `InsertAsync` from Task 5 into an internal `VersionRowWriter` helper so both `EfCoreDescriptorVersionStore` and `EfCoreRuntimeSchemaWriter` use one implementation (DRY). Do this refactor as part of this step and re-run Task 5's tests.

- [ ] **Step 5: Register the writer + concrete SQLite test**

In `AlvoEfCoreProvider.AddRelationalProvider`:

```csharp
    builder.Services.TryAddSingleton<IRuntimeSchemaWriter>(sp =>
        new EfCoreRuntimeSchemaWriter(
            sp.GetRequiredService<RelationalConnectionFactory>(),
            sp.GetRequiredService<IOptions<AlvoOptions>>().Value));
```

Create `test/MMLib.Alvo.Data.Sqlite.Tests/SqliteRuntimeSchemaWriterTests.cs` subclassing `RuntimeSchemaWriterContractTests` (writer + store share one factory/temp-file so the writer's rows are visible to the store).

- [ ] **Step 6: Run tests**

Run: `dotnet test test/MMLib.Alvo.Data.Sqlite.Tests test/MMLib.Alvo.Data.EntityFrameworkCore.Tests`
Expected: PASS (writer conflict semantics; migrator still green on the shared batch).

- [ ] **Step 7: Commit**

```bash
git add src/MMLib.Alvo.Abstractions/Migrations/IRuntimeSchemaWriter.cs src/MMLib.Alvo.Data.EntityFrameworkCore/ src/MMLib.Alvo.Testing/Migrations/ test/MMLib.Alvo.Data.Sqlite.Tests/
git commit -m "feat(data): IRuntimeSchemaWriter — atomic apply-plan + optimistic version append"
```

---

## Task 7: `RuntimeSchemaService` — apply + rollback orchestration (core)

The core orchestrator composing validator + migrator (plan) + guardrail + writer + version store. No DB connection (core is EF-free).

**Files:**
- Create: `src/MMLib.Alvo/Migrations/RuntimeSchemaService.cs`
- Modify: `src/MMLib.Alvo/AlvoServiceCollectionExtensions.cs` (register it)
- Test: `test/MMLib.Alvo.Tests/Migrations/RuntimeSchemaServiceTests.cs`

**Interfaces:**
- Consumes: `IDescriptorValidator`, `ISchemaMigrator`, `IDescriptorVersionStore`, `IRuntimeSchemaWriter` (fakes in tests).
- Produces (public API — this is the seam the Management API will call):
  - `sealed class RuntimeSchemaService(...)` with:
    - `Task<DescriptorVersion> ApplyAsync(string project, string descriptorJson, int expectedRevision, MigrationOptions options, CancellationToken ct = default)`
    - `Task<DescriptorVersion> RollbackAsync(string project, int targetRevision, MigrationOptions options, CancellationToken ct = default)`
  - Throws `DescriptorValidationException` (invalid input), `DestructiveChangeNotAllowedException` (new — guardrail refusal), `DescriptorConcurrencyException` (lost race).

> **Decision:** make `RuntimeSchemaService` **public** (not internal): it is the service the Management-API endpoint composes later, and the spec calls it "the service-level operation it will call." Its methods go into the public-API baseline (Task 9).

- [ ] **Step 1: Add the guardrail-refusal exception**

The code-first path today returns `Applied=false` on a refused destructive change; the runtime path needs to *signal* refusal to a caller. Add `src/MMLib.Alvo.Abstractions/Migrations/DestructiveChangeNotAllowedException.cs`:

```csharp
namespace MMLib.Alvo.Migrations;

/// <summary>Thrown when a runtime apply/rollback would destroy data but <see cref="MigrationOptions.AllowDestructive"/> is false.</summary>
public sealed class DestructiveChangeNotAllowedException(string project, MigrationPlan plan)
    : Exception($"The change to project '{project}' is destructive and was refused. Re-issue with AllowDestructive=true after reviewing the dry-run.")
{
    /// <summary>Gets the project whose change was refused.</summary>
    public string Project { get; } = project;

    /// <summary>Gets the refused plan (inspect its steps for the destructive changes).</summary>
    public MigrationPlan Plan { get; } = plan;
}
```

- [ ] **Step 2: Write failing tests**

Create `test/MMLib.Alvo.Tests/Migrations/RuntimeSchemaServiceTests.cs` using the in-memory fakes (`InMemoryDescriptorVersionStore`, `InMemoryRuntimeSchemaWriter`, `InMemorySchemaMigrator`, `new DescriptorValidator()`):

```csharp
[Fact]
public async Task Apply_appends_a_new_revision()
{
    var service = CreateService();
    var v1 = await service.ApplyAsync("demo", TasksV1, expectedRevision: 0, new MigrationOptions());
    v1.Revision.ShouldBe(1);
}

[Fact]
public async Task Apply_with_stale_revision_conflicts()
{
    var service = CreateService();
    await service.ApplyAsync("demo", TasksV1, 0, new MigrationOptions());
    await Should.ThrowAsync<DescriptorConcurrencyException>(
        () => service.ApplyAsync("demo", TasksV2, expectedRevision: 0, new MigrationOptions()));
}

[Fact]
public async Task Apply_rejects_invalid_descriptor()
{
    var service = CreateService();
    await Should.ThrowAsync<DescriptorValidationException>(
        () => service.ApplyAsync("demo", "{ \"apiVersion\": \"alvo.dev/v1\" }", 0, new MigrationOptions()));
}

[Fact]
public async Task Rollback_appends_a_revert_revision_marked_with_source()
{
    var service = CreateService();
    await service.ApplyAsync("demo", TasksV1, 0, new MigrationOptions());          // rev 1
    await service.ApplyAsync("demo", TasksV1WithExtra, 1, new MigrationOptions()); // rev 2
    var reverted = await service.RollbackAsync("demo", targetRevision: 1, new MigrationOptions { AllowDestructive = true });
    reverted.Revision.ShouldBe(3);
    reverted.RolledBackFrom.ShouldBe(1);
}
```

(Provide `TasksV1`, `TasksV2`, `TasksV1WithExtra` as valid descriptor JSON constants — `TasksV2` differs from `TasksV1` so its plan is non-empty; `TasksV1WithExtra` adds a required field so rollback to rev 1 is destructive.)

- [ ] **Step 3: Run tests to verify they fail**

Run: `dotnet test test/MMLib.Alvo.Tests`
Expected: FAIL — `RuntimeSchemaService` does not exist.

- [ ] **Step 4: Implement `RuntimeSchemaService`**

```csharp
using MMLib.Alvo.Descriptor;
using MMLib.Alvo.Schema;

namespace MMLib.Alvo.Migrations;

/// <summary>
/// Orchestrates a runtime (dashboard-first) schema change: validate untrusted descriptor input,
/// plan against the latest applied version, enforce the destructive guardrail, then atomically apply
/// and append a new version. The service-level operation the Management-API runtime-apply endpoint
/// will call; it owns no DB connection (the atomic transaction lives behind <see cref="IRuntimeSchemaWriter"/>).
/// </summary>
public sealed class RuntimeSchemaService
{
    private readonly IDescriptorValidator _validator;
    private readonly ISchemaMigrator _migrator;
    private readonly IDescriptorVersionStore _store;
    private readonly IRuntimeSchemaWriter _writer;

    public RuntimeSchemaService(
        IDescriptorValidator validator, ISchemaMigrator migrator,
        IDescriptorVersionStore store, IRuntimeSchemaWriter writer)
    {
        ArgumentNullException.ThrowIfNull(validator);
        ArgumentNullException.ThrowIfNull(migrator);
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(writer);
        _validator = validator;
        _migrator = migrator;
        _store = store;
        _writer = writer;
    }

    /// <summary>Validates, plans, guards, and atomically applies + versions a runtime descriptor change.</summary>
    public async Task<DescriptorVersion> ApplyAsync(string project, string descriptorJson, int expectedRevision, MigrationOptions options, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(project);
        ArgumentNullException.ThrowIfNull(options);

        Validate(descriptorJson);
        var desired = DescriptorToSchemaMapper.Map(AlvoDescriptor.Parse(descriptorJson));
        var current = await CurrentSchemaAsync(project, ct).ConfigureAwait(false);
        var plan = await _migrator.PlanAsync(current, desired, options, ct).ConfigureAwait(false);
        Guard(project, plan, options);

        var candidate = new DescriptorVersion(desired, descriptorJson, 0, DateTimeOffset.UtcNow, options.Author, options.Reason);
        return await _writer.ApplyAndAppendAsync(project, plan, candidate, expectedRevision, options, ct).ConfigureAwait(false);
    }

    /// <summary>Rolls the project back to <paramref name="targetRevision"/> by appending a git-revert version.</summary>
    public async Task<DescriptorVersion> RollbackAsync(string project, int targetRevision, MigrationOptions options, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(project);
        ArgumentNullException.ThrowIfNull(options);

        var target = await _store.GetAsync(project, targetRevision, ct).ConfigureAwait(false)
            ?? throw new InvalidOperationException($"Project '{project}' has no revision {targetRevision} to roll back to.");
        var currentVersion = await _store.GetCurrentAsync(project, ct).ConfigureAwait(false)
            ?? throw new InvalidOperationException($"Project '{project}' has no applied schema to roll back.");

        var plan = await _migrator.PlanAsync(currentVersion.Schema, target.Schema, options, ct).ConfigureAwait(false);
        Guard(project, plan, options);

        var candidate = new DescriptorVersion(
            target.Schema, target.DescriptorJson, 0, DateTimeOffset.UtcNow,
            options.Author, options.Reason ?? $"Rollback to revision {targetRevision}", RolledBackFrom: targetRevision);
        return await _writer.ApplyAndAppendAsync(project, plan, candidate, currentVersion.Revision, options, ct).ConfigureAwait(false);
    }

    private void Validate(string descriptorJson)
    {
        var result = _validator.Validate(descriptorJson);
        if (!result.IsValid)
        {
            throw new DescriptorValidationException(result);
        }
    }

    private static void Guard(string project, MigrationPlan plan, MigrationOptions options)
    {
        if (plan.HasDestructiveChanges && !options.AllowDestructive)
        {
            throw new DestructiveChangeNotAllowedException(project, plan);
        }
    }

    private async Task<SchemaModel> CurrentSchemaAsync(string project, CancellationToken ct)
    {
        var current = await _store.GetCurrentAsync(project, ct).ConfigureAwait(false);
        return current?.Schema ?? new SchemaModel([]);
    }
}
```

> This references `MigrationOptions.Author` / `MigrationOptions.Reason`. Add those two optional `string?` init properties to `MigrationOptions` (Abstractions) in this step — they carry the audit provenance into `DescriptorVersion`. Update `MigrationOptions` XML docs.

- [ ] **Step 5: Register the service**

In `AlvoServiceCollectionExtensions.AddAlvo`, after `SchemaMigrationRunner`:

```csharp
        services.TryAddSingleton<RuntimeSchemaService>();
```

- [ ] **Step 6: Run tests**

Run: `dotnet test test/MMLib.Alvo.Tests`
Expected: PASS.

- [ ] **Step 7: Commit**

```bash
git add src/MMLib.Alvo/Migrations/RuntimeSchemaService.cs src/MMLib.Alvo.Abstractions/Migrations/ \
        src/MMLib.Alvo/AlvoServiceCollectionExtensions.cs test/MMLib.Alvo.Tests/Migrations/RuntimeSchemaServiceTests.cs
git commit -m "feat(migrations): RuntimeSchemaService — runtime apply + git-revert rollback orchestration"
```

---

## Task 8: Integration tests (Testcontainers PostgreSQL + SQLite)

The real end-to-end proof: apply a runtime change, roll it back, introspect the live DB; a data-dropping rollback trips the guardrail; two concurrent appends conflict.

**Files:**
- Test: `test/MMLib.Alvo.Data.PostgreSql.Tests.Integration/RuntimeSchemaIntegrationTests.cs`
- Test: `test/MMLib.Alvo.Data.Sqlite.Tests/RuntimeSchemaSqliteIntegrationTests.cs`

**Interfaces:**
- Consumes: the real DI graph (`AddAlvo().UseSqlite(...)` / `UsePostgreSql(...)`), `RuntimeSchemaService`, `ISchemaIntrospector`.

- [ ] **Step 1: Write the round-trip + rollback test**

Mirror the existing code-first integration fixtures. Assert: `ApplyAsync(v1)` → introspect shows v1 entities; `ApplyAsync(v2, expected=1)` → introspect shows v2; `RollbackAsync(target=1, AllowDestructive=true)` → introspect shows v1 again and the new version's `RolledBackFrom == 1`.

- [ ] **Step 2: Write the guardrail + concurrency tests**

- Rollback of a change that dropped no data but whose reverse drops a column → without `AllowDestructive` throws `DestructiveChangeNotAllowedException`; the DB is unchanged (introspect proves it).
- Two `ApplyAsync(..., expectedRevision: 1)` calls started concurrently against the same project (both derived from revision 1) → exactly one succeeds, the other throws `DescriptorConcurrencyException`; the history has exactly one new revision.

```csharp
[Fact]
public async Task Two_concurrent_appends_one_conflicts()
{
    EnsureEngineAvailable();
    var service = BuildService(out var store);
    await service.ApplyAsync("demo", V1, 0, new MigrationOptions());

    var a = service.ApplyAsync("demo", V2a, expectedRevision: 1, new MigrationOptions());
    var b = service.ApplyAsync("demo", V2b, expectedRevision: 1, new MigrationOptions());

    var outcomes = await Task.WhenAll(Wrap(a), Wrap(b));
    outcomes.Count(o => o.Ok).ShouldBe(1);
    outcomes.Count(o => o.Conflict).ShouldBe(1);
    (await store.ListAsync("demo")).Count.ShouldBe(2);
}
```

(`Wrap` catches `DescriptorConcurrencyException` into a small result struct.)

- [ ] **Step 3: Run**

Run: `scripts/test-ring2` (Docker required for the PG leg; SQLite leg always runs)
Expected: PASS.

- [ ] **Step 4: Commit**

```bash
git add test/MMLib.Alvo.Data.PostgreSql.Tests.Integration/ test/MMLib.Alvo.Data.Sqlite.Tests/RuntimeSchemaSqliteIntegrationTests.cs
git commit -m "test(data): runtime versioning integration — rollback, guardrail, two-client conflict"
```

---

## Task 9: Property-based rollback symmetry + public-API baselines + arch tests

**Files:**
- Test: `test/MMLib.Alvo.Tests/Migrations/RollbackPropertyTests.cs` (CsCheck)
- Modify: the committed public-API baseline files for `MMLib.Alvo.Abstractions`, `MMLib.Alvo`, `MMLib.Alvo.Data.EntityFrameworkCore`, `MMLib.Alvo.Testing`.
- Verify: the existing NetArchTest rule (`MMLib.Alvo` has no EF reference) still passes.

- [ ] **Step 1: Property test — rollback restores the prior schema**

Using CsCheck + `InMemorySchemaMigrator` + the in-memory store/writer: generate a random valid field-set A and a superset B; apply A (rev1), apply B (rev2), roll back to rev1; the current version's schema equals A. Reuse the generators from the existing PR-A property tests.

- [ ] **Step 2: Run property test**

Run: `dotnet test test/MMLib.Alvo.Tests`
Expected: PASS.

- [ ] **Step 3: Regenerate + review public-API baselines**

Run the repo's public-API approval flow (as ring1/ring2 use it). Review the diff: it must show exactly the new public surface — `DescriptorVersion`, `IDescriptorVersionStore`, `IRuntimeSchemaWriter`, `DescriptorConcurrencyException`, `DestructiveChangeNotAllowedException`, `IDescriptorValidator`, `DescriptorValidationResult`/`Error`/`Severity`/`Exception`, `RuntimeSchemaService`, `MigrationOptions.Author`/`Reason`, and the removed `AppliedSchemaTableName`/added `DescriptorVersionsTableName` (internal — should NOT appear). Confirm no unintended public additions (e.g. `RelationalConnectionFactory`, `RelationalSqlBatch`, `EfCore*` store/writer must stay internal).

- [ ] **Step 4: Confirm the arch test is green**

Run: `dotnet test test/MMLib.Alvo.Conventions.Tests`
Expected: PASS — core still has no EF reference; Corvus is allowed.

- [ ] **Step 5: Commit**

```bash
git add test/ src/**/PublicAPI.*.txt
git commit -m "test(schema): rollback-symmetry property test + PR-B public-API baselines"
```

---

## Task 10: Pre-PR review gate

**Files:** none (review + fixes only).

- [ ] **Step 1: Run ring2**

Run: `scripts/test-ring2`
Expected: PASS.

- [ ] **Step 2: Dispatch `alvo-plan-guard`** (read-only) — confirm no drift from `docs/PLAN.md`, no violated §0 principle, no security-core shortcut. It also proposes whether the `← YOU ARE HERE` marker moves (it should NOT — F3 has open issues #19–#22; #18 only *closes* with this PR).

- [ ] **Step 3: Run `/code-review medium`** on the diff; fix findings.

- [ ] **Step 4: Run `/security-review` + the `alvo-security-core-review` checklist** — the diff touches the untrusted-input path (validator), the destructive guardrail, and SQL execution. Confirm: no raw descriptor value reaches DDL (computed closed); all data values are parameterized (only the validated table name is interpolated); the optimistic lock cannot be bypassed; rollback cannot silently drop data.

- [ ] **Step 5: Open the PR** — title referencing issue #18 (PR-B). Body summarizes the runtime-versioning slice + the two folded-in #20 guardrails, notes the new public ports, and states CodeRabbit/CodeQL are the outer gate. A human merges.

---

## Self-Review

**Spec coverage:**
- `IDescriptorVersionStore` append-only + impl → Tasks 4, 5. ✅
- Optimistic locking (revision → conflict) → Tasks 4 (contract), 5 (SQL), 6 (atomic), 8 (concurrency IT). ✅
- Per-call connection refactor → Task 3. ✅
- Rollback git-revert + DROP guardrail → Task 7 (`RollbackAsync`), 8 (guardrail IT), 9 (symmetry). ✅
- `RuntimeSchemaService` (service-level runtime apply) → Task 7. ✅
- `IRuntimeSchemaWriter` atomicity seam → Task 6. ✅
- Reject `computed` (#20 HIGH) → Task 1 (mapper) + Task 2 (validator). ✅
- Layered validation with fix-suggestions (#20 MEDIUM) → Task 2. ✅
- Testing (contract, integration, property, public-API, arch) → Tasks 4/5/6/8/9. ✅
- Deferred items explicitly out → not implemented (correct). ✅

**Placeholder scan:** Task 5 Step 3 and Task 8 leave some helper bodies described rather than fully coded (row mappers, temp-file fixtures) — these are deliberately delegated to the *existing* PR-A patterns the implementer copies (`SqliteSchemaMigratorTests` fixture, `AppliedSchemaJsonContext`); every novel/tricky piece has full code. Acceptable, but the implementer must read the referenced PR-A files.

**Type consistency:** `AppendAsync(project, candidate, expectedRevision)` returns the appended `DescriptorVersion` with `Revision = expectedRevision + 1` (Tasks 4, 5, fake). `ApplyAndAppendAsync` signature identical across port (Task 6 Step 1), fake, EF impl (Task 6 Step 4), and caller (Task 7). `DescriptorVersion` field names (`RolledBackFrom`, `Author`, `Reason`) consistent across Tasks 4, 7. `MigrationOptions.Author/Reason` added in Task 7 Step 4 and consumed there. `DescriptorVersionsTableName` replaces `AppliedSchemaTableName` consistently (Tasks 5 Steps 1, 5).

**One open dependency to flag at execution:** Task 3 Step 4 temporarily inlines the SQL loop; Task 6 Step 3 replaces it with `RelationalSqlBatch`. If Tasks are run out of order, Task 6 must follow Task 3.
