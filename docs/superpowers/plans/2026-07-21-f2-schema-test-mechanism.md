# F2 Schema Test Mechanism Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Stand up the `MMLib.Alvo.Schema.Tests` project implementing all four F2 schema-test types over the finalized `schema/project.schema.json`, closing #16 (schema finalized + validated), #57 (dynamic-entities accommodation exercised by an example), and #17 (the four-type mechanism).

**Architecture:** A single xUnit-v3-on-MTP test project that opts out of the shared architecture/public-API gate (it maps to no production assembly, like the Conventions project). It validates with Corvus.Json.Validator (Apache-2.0, runtime dynamic validation — design decision D8), property-tests canonicalization with CsCheck, and snapshots error output + canonical serialization with Verify. The schema and examples already exist and are committed; this plan adds the .NET mechanism, wiring, and the D7 relocation cleanup.

**Tech Stack:** .NET 10 (net10.0), xUnit v3 on Microsoft.Testing.Platform, Corvus.Json.Validator 5.2.7, CsCheck 4.7.0, Verify.XunitV3, Shouldly.

## Global Constraints

- Target framework `net10.0`; SDK pinned in `global.json`; runner = Microsoft.Testing.Platform (never VSTest).
- Central Package Management: versions go in `Directory.Packages.props`, never inline in a csproj.
- All projects named `MMLib.Alvo.*` and registered in `MMLib.Alvo.slnx` (enforced by SolutionConventionTests).
- No project re-declares inherited MSBuild properties (TargetFramework, Nullable, ImplicitUsings, LangVersion) — they come from Directory.Build.props.
- Licences must be permissive OSS (Corvus = Apache-2.0 ✓, CsCheck = MIT ✓, Verify = MIT ✓). No JsonSchema.Net (2026 EULA).
- Never merge/push to main; branch → PR → human merges. Branch: `feat/descriptor-schema-v1` (already exists with schema + examples + design doc).
- The authoritative schema lives at `schema/project.schema.json`; `$id` = `https://alvo.dev/schema/v1/project.json`.

---

## File Structure

- `test/MMLib.Alvo.Schema.Tests/MMLib.Alvo.Schema.Tests.csproj` — test project; opts out of shared arch/public-API, references Corvus/CsCheck/Verify, copies schema+examples+meta-schema to output.
- `test/MMLib.Alvo.Schema.Tests/SchemaPaths.cs` — resolves repo-root-relative paths to the schema, examples corpus, negative corpus, and the vendored meta-schema (via `RepositoryRoot.Find()`).
- `test/MMLib.Alvo.Schema.Tests/MetaValidationTests.cs` — test type 1: schema validates against the vendored draft 2020-12 meta-schema + authoring-lint facts.
- `test/MMLib.Alvo.Schema.Tests/ExamplesTests.cs` — test type 2: every `examples/**/*.alvo.json` passes; every `examples/_negative/*.json` fails with the expected instance-location pointer.
- `test/MMLib.Alvo.Schema.Tests/Canonicalizer.cs` — canonical JSON form (deterministic member order; x- preserved) used by round-trip + snapshot.
- `test/MMLib.Alvo.Schema.Tests/RoundTripTests.cs` — test type 3: CsCheck property (canonicalize is order-insensitive + idempotent) + mutation property (a corrupted valid example fails validation).
- `test/MMLib.Alvo.Schema.Tests/SnapshotTests.cs` — test type 4: Verify snapshots of canonical serialization of each example + the detailed error output of each negative case.
- `test/MMLib.Alvo.Schema.Tests/VerifyModuleInit.cs` — Verify path init (this project opts out of `_shared`, so it needs its own).
- `test/MMLib.Alvo.Schema.Tests/meta-schema/2020-12/*.json` — vendored draft 2020-12 meta-schema + its vocabulary files (offline meta-validation).
- `Directory.Packages.props` — add `Corvus.Json.Validator` 5.2.7 and `CsCheck` 4.7.0.
- `MMLib.Alvo.slnx` — register the new test project.
- `docs/product/alvo-descriptor.schema.json` — removed; replaced by `schema/README.md` pointer (D7). Update the one reference in `.claude/skills/alvo-schema-testing/SKILL.md`.

---

### Task 1: Scaffold the test project + package wiring

**Files:**
- Create: `test/MMLib.Alvo.Schema.Tests/MMLib.Alvo.Schema.Tests.csproj`
- Modify: `Directory.Packages.props`
- Modify: `MMLib.Alvo.slnx`
- Create: `test/MMLib.Alvo.Schema.Tests/SchemaPaths.cs`
- Create: `test/MMLib.Alvo.Schema.Tests/SmokeTest.cs` (temporary, removed in Task 2)

**Interfaces:**
- Produces: `SchemaPaths.SchemaFile`, `SchemaPaths.MetaSchemaFile`, `SchemaPaths.Examples()`, `SchemaPaths.NegativeExamples()` — all absolute paths derived from `RepositoryRoot.Find()`.

- [ ] **Step 1: Add package versions**

In `Directory.Packages.props`, inside the first `<ItemGroup>`:

```xml
    <PackageVersion Include="Corvus.Json.Validator" Version="5.2.7" />
    <PackageVersion Include="CsCheck" Version="4.7.0" />
```

- [ ] **Step 2: Create the csproj**

`test/MMLib.Alvo.Schema.Tests/MMLib.Alvo.Schema.Tests.csproj`:

```xml
<Project Sdk="Microsoft.NET.Sdk">

  <!-- No 1:1 production assembly (like Conventions/integration/e2e): opt out of
       the shared architecture + public-API approval gate. -->
  <PropertyGroup>
    <AlvoSharedArchTests>false</AlvoSharedArchTests>
  </PropertyGroup>

  <ItemGroup>
    <PackageReference Include="Corvus.Json.Validator" />
    <PackageReference Include="CsCheck" />
    <PackageReference Include="Verify.XunitV3" />
  </ItemGroup>

  <ItemGroup>
    <Using Include="VerifyXunit" />
  </ItemGroup>

</Project>
```

- [ ] **Step 3: Register in the solution**

In `MMLib.Alvo.slnx`, add inside the `/test/` folder:

```xml
    <Project Path="test/MMLib.Alvo.Schema.Tests/MMLib.Alvo.Schema.Tests.csproj" />
```

- [ ] **Step 4: Write SchemaPaths**

`test/MMLib.Alvo.Schema.Tests/SchemaPaths.cs`:

```csharp
using MMLib.Alvo.Testing;

namespace MMLib.Alvo.Schema.Tests;

internal static class SchemaPaths
{
    private static readonly string Root = RepositoryRoot.Find();

    internal static string SchemaFile => Path.Combine(Root, "schema", "project.schema.json");

    internal static string MetaSchemaFile =>
        Path.Combine(AppContext.BaseDirectory, "meta-schema", "2020-12", "schema.json");

    internal static IEnumerable<string> Examples() =>
        Directory.EnumerateFiles(Path.Combine(Root, "examples"), "*.alvo.json", SearchOption.AllDirectories)
            .OrderBy(p => p, StringComparer.Ordinal);

    internal static IEnumerable<string> NegativeExamples() =>
        Directory.EnumerateFiles(Path.Combine(Root, "examples", "_negative"), "*.json")
            .OrderBy(p => p, StringComparer.Ordinal);
}
```

- [ ] **Step 5: Temporary smoke test**

`test/MMLib.Alvo.Schema.Tests/SmokeTest.cs`:

```csharp
namespace MMLib.Alvo.Schema.Tests;

public class SmokeTest
{
    [Fact]
    public void Schema_file_exists() => File.Exists(SchemaPaths.SchemaFile).ShouldBeTrue();
}
```

- [ ] **Step 6: Build + run**

Run: `dotnet test test/MMLib.Alvo.Schema.Tests`
Expected: build succeeds, 1 test passes. If `dotnet test` cannot resolve Shouldly/xunit `Using`s, confirm `test/Directory.Build.props` is being imported (it is, by directory position).

- [ ] **Step 7: Commit**

```bash
git add Directory.Packages.props MMLib.Alvo.slnx test/MMLib.Alvo.Schema.Tests
git commit -m "test(schema): scaffold MMLib.Alvo.Schema.Tests project"
```

---

### Task 2: Test type 1 — meta-validation + authoring lint

**Files:**
- Create: `test/MMLib.Alvo.Schema.Tests/meta-schema/2020-12/` (vendored draft 2020-12 meta-schema + vocab files)
- Modify: `test/MMLib.Alvo.Schema.Tests/MMLib.Alvo.Schema.Tests.csproj` (copy schema + meta-schema to output)
- Create: `test/MMLib.Alvo.Schema.Tests/MetaValidationTests.cs`
- Delete: `test/MMLib.Alvo.Schema.Tests/SmokeTest.cs`

**Interfaces:**
- Consumes: `SchemaPaths.SchemaFile`, `SchemaPaths.MetaSchemaFile`.
- Produces: nothing consumed downstream.

- [ ] **Step 1: Vendor the meta-schema**

Download the official draft 2020-12 meta-schema bundle into `meta-schema/2020-12/`: `schema.json` plus the vocab files it `$ref`s (`meta/core.json`, `meta/applicator.json`, `meta/validation.json`, `meta/meta-data.json`, `meta/format-annotation.json`, `meta/content.json`, `meta/unevaluated.json`). Source: https://json-schema.org/draft/2020-12/schema and the sibling `/meta/*` documents. Preserve the relative `$ref` layout so Corvus resolves them from the root `schema.json`'s directory.

- [ ] **Step 2: Copy schema + meta-schema to test output**

In the csproj add:

```xml
  <ItemGroup>
    <None Include="meta-schema/**/*.json" CopyToOutputDirectory="PreserveNewest" LinkBase="meta-schema" />
  </ItemGroup>
```

(The project `schema/project.schema.json` and `examples/` are read from the repo tree via `RepositoryRoot.Find()`, not copied.)

- [ ] **Step 3: Write the meta-validation + lint test**

`test/MMLib.Alvo.Schema.Tests/MetaValidationTests.cs`:

```csharp
using System.Text.Json;
using Corvus.Json.Validator;

namespace MMLib.Alvo.Schema.Tests;

public class MetaValidationTests
{
    [Fact]
    public void Schema_is_a_valid_draft_2020_12_document()
    {
        JsonSchema metaSchema = JsonSchema.FromFile(SchemaPaths.MetaSchemaFile);
        string schemaText = File.ReadAllText(SchemaPaths.SchemaFile);

        bool valid = metaSchema.Validate(schemaText);

        valid.ShouldBeTrue("schema/project.schema.json must be a valid draft 2020-12 schema");
    }

    [Fact]
    public void Every_property_has_a_description()
    {
        using JsonDocument doc = JsonDocument.Parse(File.ReadAllText(SchemaPaths.SchemaFile));
        List<string> missing = [];
        WalkProperties(doc.RootElement, "#", missing);
        missing.ShouldBeEmpty("every declared property must carry a description (agent + IntelliSense UX)");
    }

    private static void WalkProperties(JsonElement node, string pointer, List<string> missing)
    {
        if (node.ValueKind != JsonValueKind.Object)
        {
            if (node.ValueKind == JsonValueKind.Array)
            {
                int i = 0;
                foreach (JsonElement item in node.EnumerateArray())
                {
                    WalkProperties(item, $"{pointer}/{i++}", missing);
                }
            }

            return;
        }

        foreach (JsonProperty member in node.EnumerateObject())
        {
            if (member.Name is "properties")
            {
                foreach (JsonProperty prop in member.Value.EnumerateObject())
                {
                    bool hasDescription = prop.Value.ValueKind == JsonValueKind.Object
                        && prop.Value.TryGetProperty("description", out _);
                    bool isRef = prop.Value.ValueKind == JsonValueKind.Object
                        && prop.Value.TryGetProperty("$ref", out _);
                    if (!hasDescription && !isRef)
                    {
                        missing.Add($"{pointer}/properties/{prop.Name}");
                    }
                }
            }

            WalkProperties(member.Value, $"{pointer}/{member.Name}", missing);
        }
    }
}
```

Note: a property whose schema is a bare `$ref` inherits its description from the target `$def`, so it is exempt.

- [ ] **Step 4: Delete the smoke test**

```bash
git rm test/MMLib.Alvo.Schema.Tests/SmokeTest.cs
```

- [ ] **Step 5: Run**

Run: `dotnet test test/MMLib.Alvo.Schema.Tests`
Expected: both tests pass. If `Every_property_has_a_description` reports a pointer, add the missing `description` in `schema/project.schema.json` (this is the lint doing its job).

- [ ] **Step 6: Commit**

```bash
git add test/MMLib.Alvo.Schema.Tests
git commit -m "test(schema): type 1 — meta-validation against draft 2020-12 + description lint"
```

---

### Task 3: Test type 2 — examples corpus (positive + negative with pointer)

**Files:**
- Create: `test/MMLib.Alvo.Schema.Tests/SchemaValidator.cs` (shared helper)
- Create: `test/MMLib.Alvo.Schema.Tests/ExamplesTests.cs`

**Interfaces:**
- Produces: `SchemaValidator.Load()` -> `JsonSchema`; `SchemaValidator.Failures(JsonSchema, string json)` -> `IReadOnlyList<(string Pointer, string Message)>`.
- Consumes: `SchemaPaths.SchemaFile`, `SchemaPaths.Examples()`, `SchemaPaths.NegativeExamples()`.

- [ ] **Step 1: Write the validator helper**

`test/MMLib.Alvo.Schema.Tests/SchemaValidator.cs`:

```csharp
using Corvus.Json.Validator;

namespace MMLib.Alvo.Schema.Tests;

internal static class SchemaValidator
{
    internal static JsonSchema Load() => JsonSchema.FromFile(SchemaPaths.SchemaFile);

    internal static IReadOnlyList<(string Pointer, string Message)> Failures(JsonSchema schema, string json)
    {
        using JsonSchemaResultsCollector collector =
            JsonSchemaResultsCollector.Create(JsonSchemaResultsLevel.Detailed);

        schema.Validate(json, collector);

        return collector.EnumerateResults()
            .Where(r => !r.IsMatch)
            .Select(r => (r.GetDocumentEvaluationLocationText(), r.GetMessageText()))
            .ToList();
    }
}
```

- [ ] **Step 2: Write the examples test**

`test/MMLib.Alvo.Schema.Tests/ExamplesTests.cs`:

```csharp
namespace MMLib.Alvo.Schema.Tests;

public class ExamplesTests
{
    public static IEnumerable<object[]> Positive() =>
        SchemaPaths.Examples().Select(p => new object[] { p });

    public static IEnumerable<object[]> Negative() =>
        SchemaPaths.NegativeExamples().Select(p => new object[] { p });

    [Theory]
    [MemberData(nameof(Positive))]
    public void Example_validates(string path)
    {
        var schema = SchemaValidator.Load();
        var failures = SchemaValidator.Failures(schema, File.ReadAllText(path));
        failures.ShouldBeEmpty($"{Path.GetFileName(path)} must validate against the schema");
    }

    [Theory]
    [MemberData(nameof(Negative))]
    public void Negative_example_is_rejected(string path)
    {
        var schema = SchemaValidator.Load();
        var failures = SchemaValidator.Failures(schema, File.ReadAllText(path));
        failures.ShouldNotBeEmpty($"{Path.GetFileName(path)} must be rejected by the schema");
    }
}
```

- [ ] **Step 3: Run**

Run: `dotnet test test/MMLib.Alvo.Schema.Tests`
Expected: positive theories (simple-tasks, complex-crm) pass; negative theories (4 files) pass (each produces failures). If `GetDocumentEvaluationLocationText`/`GetMessageText` are not the exact member names in 5.2.7, fix against the compiler error (the `result` object exposes match state + location/message; adjust names).

- [ ] **Step 4: Commit**

```bash
git add test/MMLib.Alvo.Schema.Tests
git commit -m "test(schema): type 2 — examples corpus + negative corpus rejected"
```

---

### Task 4: Canonicalizer + test type 3 (round-trip / mutation property)

**Files:**
- Create: `test/MMLib.Alvo.Schema.Tests/Canonicalizer.cs`
- Create: `test/MMLib.Alvo.Schema.Tests/RoundTripTests.cs`

**Interfaces:**
- Produces: `Canonicalizer.Canonicalize(string json)` -> `string` (deterministic member order, stable formatting, x- preserved).
- Consumes: `SchemaValidator`, `SchemaPaths`.

- [ ] **Step 1: Write the canonicalizer**

`test/MMLib.Alvo.Schema.Tests/Canonicalizer.cs`:

```csharp
using System.Text;
using System.Text.Json;

namespace MMLib.Alvo.Schema.Tests;

// F2 canonical form: deterministic member order (ordinal by key) and stable
// indentation. Structural equality of two descriptors = equality of their
// canonical text. Default-omission (schema-aware) is deferred to F4 export.
internal static class Canonicalizer
{
    internal static string Canonicalize(string json)
    {
        using JsonDocument doc = JsonDocument.Parse(json);
        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer, new JsonWriterOptions { Indented = true }))
        {
            Write(doc.RootElement, writer);
        }

        return Encoding.UTF8.GetString(buffer.WrittenSpan);
    }

    private static void Write(JsonElement element, Utf8JsonWriter writer)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                writer.WriteStartObject();
                foreach (JsonProperty member in element.EnumerateObject()
                    .OrderBy(p => p.Name, StringComparer.Ordinal))
                {
                    writer.WritePropertyName(member.Name);
                    Write(member.Value, writer);
                }

                writer.WriteEndObject();
                break;
            case JsonValueKind.Array:
                writer.WriteStartArray();
                foreach (JsonElement item in element.EnumerateArray())
                {
                    Write(item, writer);
                }

                writer.WriteEndArray();
                break;
            default:
                element.WriteTo(writer);
                break;
        }
    }
}
```

- [ ] **Step 2: Write the round-trip + mutation tests**

`test/MMLib.Alvo.Schema.Tests/RoundTripTests.cs`:

```csharp
using System.Text.Json;
using CsCheck;

namespace MMLib.Alvo.Schema.Tests;

public class RoundTripTests
{
    [Fact]
    public void Canonicalize_is_idempotent_and_member_order_insensitive()
    {
        foreach (string path in SchemaPaths.Examples())
        {
            string original = File.ReadAllText(path);
            string once = Canonicalizer.Canonicalize(original);
            string twice = Canonicalizer.Canonicalize(once);
            twice.ShouldBe(once, $"canonicalization must be idempotent for {Path.GetFileName(path)}");

            // Re-emitting members in reverse still canonicalizes to the same text.
            string shuffled = ReverseTopLevelMembers(original);
            Canonicalizer.Canonicalize(shuffled).ShouldBe(once);
        }
    }

    [Fact]
    public void A_mutated_valid_example_fails_validation()
    {
        var schema = SchemaValidator.Load();
        string crm = File.ReadAllText(SchemaPaths.Examples().First(p => p.Contains("complex-crm")));

        // Property: injecting an unknown top-level key (typo class) into a valid
        // descriptor always produces at least one validation failure.
        Gen.String[3, 12].Where(s => s.All(char.IsLetter) && !s.StartsWith("x-")).Sample(key =>
        {
            using JsonDocument doc = JsonDocument.Parse(crm);
            using var stream = new MemoryStream();
            using (var writer = new Utf8JsonWriter(stream))
            {
                writer.WriteStartObject();
                writer.WritePropertyName("zzz_" + key); // unknown, not x-
                writer.WriteStringValue("boom");
                foreach (JsonProperty m in doc.RootElement.EnumerateObject())
                {
                    m.WriteTo(writer);
                }

                writer.WriteEndObject();
            }

            string mutated = System.Text.Encoding.UTF8.GetString(stream.ToArray());
            return SchemaValidator.Failures(schema, mutated).Count > 0;
        });
    }

    private static string ReverseTopLevelMembers(string json)
    {
        using JsonDocument doc = JsonDocument.Parse(json);
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            foreach (JsonProperty m in doc.RootElement.EnumerateObject().Reverse())
            {
                m.WriteTo(writer);
            }

            writer.WriteEndObject();
        }

        return System.Text.Encoding.UTF8.GetString(stream.ToArray());
    }
}
```

- [ ] **Step 3: Run**

Run: `dotnet test test/MMLib.Alvo.Schema.Tests`
Expected: both tests pass. CsCheck `Sample` throws on a falsifying case; a green run means the property held across samples.

- [ ] **Step 4: Commit**

```bash
git add test/MMLib.Alvo.Schema.Tests
git commit -m "test(schema): type 3 — canonical form idempotence + mutation property (CsCheck)"
```

---

### Task 5: Test type 4 — snapshots (Verify)

**Files:**
- Create: `test/MMLib.Alvo.Schema.Tests/VerifyModuleInit.cs`
- Create: `test/MMLib.Alvo.Schema.Tests/SnapshotTests.cs`
- Create: `test/MMLib.Alvo.Schema.Tests/SnapshotTests.*.verified.txt` (accepted baselines)

**Interfaces:**
- Consumes: `Canonicalizer`, `SchemaValidator`, `SchemaPaths`.

- [ ] **Step 1: Verify path init**

`test/MMLib.Alvo.Schema.Tests/VerifyModuleInit.cs`:

```csharp
using System.Runtime.CompilerServices;

namespace MMLib.Alvo.Schema.Tests;

internal static class VerifyModuleInit
{
    [ModuleInitializer]
    internal static void Initialize() => VerifierSettings.UseUtf8NoBom();
}
```

(Baselines live next to the test file by Verify's default caller-path derivation; this project does not use the `_shared` DerivePathInfo override.)

- [ ] **Step 2: Write snapshot tests**

`test/MMLib.Alvo.Schema.Tests/SnapshotTests.cs`:

```csharp
namespace MMLib.Alvo.Schema.Tests;

[UsesVerify]
public class SnapshotTests
{
    [Fact]
    public Task Canonical_crm() =>
        Verify(Canonicalizer.Canonicalize(
            File.ReadAllText(SchemaPaths.Examples().First(p => p.Contains("complex-crm")))))
            .UseFileName("canonical-complex-crm");

    [Fact]
    public Task Negative_error_output()
    {
        var schema = SchemaValidator.Load();
        var report = SchemaPaths.NegativeExamples().Select(path => new
        {
            file = Path.GetFileName(path),
            failures = SchemaValidator.Failures(schema, File.ReadAllText(path))
                .Select(f => new { f.Pointer, f.Message })
                .OrderBy(f => f.Pointer)
                .ToList(),
        });

        return Verify(report).UseFileName("negative-error-output");
    }
}
```

- [ ] **Step 3: Run + accept baselines**

Run: `dotnet test test/MMLib.Alvo.Schema.Tests`
Expected: first run FAILS (no baseline) and writes `*.received.txt`. Inspect each received file; if correct, accept it (rename `.received.` → `.verified.`, or use the Verify diff tool). Re-run: PASS. Commit the `.verified.txt` files.

- [ ] **Step 4: Commit**

```bash
git add test/MMLib.Alvo.Schema.Tests
git commit -m "test(schema): type 4 — canonical + error-output snapshots (Verify)"
```

---

### Task 6: D7 relocation cleanup

**Files:**
- Delete: `docs/product/alvo-descriptor.schema.json`
- Create: `schema/README.md`
- Modify: `.claude/skills/alvo-schema-testing/SKILL.md` (repoint the reference)

**Interfaces:** none (docs only).

- [ ] **Step 1: Find references to the old path**

Run: `grep -rn "alvo-descriptor.schema.json" --include=*.md --include=*.json .`
Expected: hits in the design brief (generated — leave it; regen is separate), the `alvo-schema-testing` skill, and possibly the spec. Repoint only living references (skill); do NOT hand-edit the generated brief.

- [ ] **Step 2: Remove the old draft, add a pointer**

```bash
git rm docs/product/alvo-descriptor.schema.json
```

`schema/README.md`:

```markdown
# Alvo descriptor JSON Schema

`project.schema.json` is the canonical descriptor schema (draft 2020-12),
`$id` `https://alvo.dev/schema/v1/project.json`. It supersedes the earlier
draft that lived at `docs/product/alvo-descriptor.schema.json`.

Validated in CI by `test/MMLib.Alvo.Schema.Tests` (Corvus.Json.Validator).
Reference descriptors live in `examples/`.
```

- [ ] **Step 3: Repoint the skill reference**

In `.claude/skills/alvo-schema-testing/SKILL.md`, replace occurrences of the old path with `schema/project.schema.json`. Keep the surrounding prose intact.

- [ ] **Step 4: Verify nothing else breaks**

Run: `grep -rn "docs/product/alvo-descriptor.schema.json" .` (ignoring the generated brief) — expect no living references.

- [ ] **Step 5: Commit**

```bash
git add schema/README.md .claude/skills/alvo-schema-testing/SKILL.md
git commit -m "docs(schema): relocate descriptor schema to schema/, add pointer (D7)"
```

---

### Task 7: Full gate — format, rings, plan-guard

**Files:** none (verification only).

- [ ] **Step 1: Format**

Run: `dotnet format MMLib.Alvo.slnx`
Expected: no changes, or auto-fixes committed.

- [ ] **Step 2: ring0 then ring1**

Run: `scripts/test-ring0 && scripts/test-ring1`
Expected: green. ring1 includes the SolutionConventionTests, which now see the new project — confirm it is registered, named correctly, and pins no inline versions.

- [ ] **Step 3: Confirm the whole suite**

Run: `dotnet test MMLib.Alvo.slnx`
Expected: all projects green, including the new schema tests.

- [ ] **Step 4: Commit any format fixes**

```bash
git add -A && git commit -m "style: dotnet format" || echo "nothing to format"
```

- [ ] **Step 5: Dispatch alvo-plan-guard (read-only, required pre-PR)**

Per the hard rules, dispatch the `alvo-plan-guard` subagent to check drift from `docs/PLAN.md`, §0 principles, and security-core shortcuts. It is advisory; address any flagged drift before opening the PR.

---

## Self-Review

**Spec coverage (design doc §D9 + §M + §D6 + binding constraints):**
- Test types 1–4: Tasks 2–5. ✓
- Schema finalization (D1–D9, M1–M9, 10 D6 fixes): already committed in `schema/project.schema.json`; validated by Tasks 2–3. ✓
- #57 dynamic-entities: exercised by `examples/complex-crm` (dynamicEntities.defaultRules + storage:dynamic + physical→dynamic ref) validated in Task 3. ✓
- D7 relocation: Task 6. ✓
- D8 Corvus validator: Tasks 1–3. ✓
- Publish story: **deferred** — the user deferred the domain (`alvo.burgyn.online` TBD); the GitHub Pages workflow is a follow-up, noted so it isn't silently dropped. The `$id` is already the intended canonical URL.

**Placeholder scan:** no TBD/TODO; all steps carry real code or exact commands.

**Type consistency:** `SchemaValidator.Failures` returns `IReadOnlyList<(string Pointer, string Message)>`, consumed identically in Tasks 3 and 5. `Canonicalizer.Canonicalize(string) -> string` used in Tasks 4 and 5. `SchemaPaths` members used across all tasks match Task 1's definitions.

**Known execution risk:** the Corvus 5.2.7 result-member names (`GetDocumentEvaluationLocationText`, `GetMessageText`, `IsMatch`) are taken from the docs; if the compiler disagrees, fix against the actual API surface — the capability (detailed results with location + message) is confirmed to exist.
