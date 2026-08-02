# Startup lifecycle and configuration DX — Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development
> (recommended) or superpowers:executing-plans to implement this plan task-by-task.
> Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Move Alvo's descriptor boot out of `AlvoHost.BuildAsync` into a hosted
service that runs before the server listens, split "prime the schema" from
"migrate the database" so only the latter is governed by a mode, and collapse the
host's configuration to `AddAlvo(…)` + `MapAlvo()`.

**Architecture:** Five boot stages run in `IHostedLifecycleService.StartingAsync`
(measured: before Kestrel binds, and after `ValidateOnStart`). Stage 0 loads,
validates, maps and compiles the descriptor with no database access. Stage 1
brings the framework's own `alvo.*` tables up unconditionally. Stage 2 compares
the descriptor against `IAppliedSchemaStore` and branches on
*uninitialized/unchanged/drifted*, with only *drifted* governed by
`AlvoSchemaStartupMode`. Stage 3 primes the policy catalog and schema registry.
Routes then materialise lazily from an `EndpointDataSource` registered — empty —
during `MapAlvo()`.

**Tech Stack:** .NET 10 (`net10.0`), ASP.NET Core minimal APIs, EF Core 10,
Microsoft.Testing.Platform (MTP), xUnit v3, Shouldly, Verify (snapshot),
`PublicApiGenerator` (API approval), Husky.Net hooks, TeaPie (e2e).

## Global Constraints

- **Design doc is the source of truth:**
  `docs/superpowers/specs/2026-08-02-startup-lifecycle-and-config-dx-design.md`.
  Measured evidence: `docs/superpowers/specs/evidence/2026-08-02-startup-lifecycle/spike.txt`.
- **Never merge or push to `main`.** Branch → PR → a human merges.
- **`Abstractions` depends on no `MMLib.Alvo.*` package and no provider**, and
  stays ASP.NET-free (`package-boundary.md`). `AlvoSchemaStartupMode` therefore
  goes in `Abstractions`; the hosted service does **not**.
- **`extensibility.md` verb taxonomy:** `Use{Provider}` infra, `Add{Thing}`
  additive, `Enable{Feature}` toggle, `From{Source}` descriptor source,
  `Apply{Thing}` runtime operation. Do not invent a verb.
- **`extensibility.md` rule 5:** every options type is validated at startup with
  `ValidateDataAnnotations().ValidateOnStart()` or `IValidateOptions<T>`, producing
  a structured error **with a fix suggestion**.
- **Never call `BuildServiceProvider()` during registration.** Use
  `IConfigureOptions<T>` / `OptionsBuilder.Configure<TDep>`.
- **`public` is the contract.** Default to `internal`. Every public change moves a
  `PublicApi.*.verified.txt` baseline, which the snapshot judge rules on.
- **Never hand-edit a `*.verified.*` baseline.** Let the test framework write it,
  and expect `.claude/hooks/turn-review-gate` to require the
  `alvo-snapshot-judge` subagent when one moves.
- **`.gitattributes` pins `*.cs` to CRLF.** A search string with LF endings
  matches nothing — assert any mutation's edit actually landed.
- **Short, single-purpose methods** (~25-line ceiling) per `alvo-dotnet-conventions`.
- **Assert `Build succeeded` before reading any test result.** A broken build
  silently runs the previous binary.
- **Rings:** `scripts/test-ring0` after each task, `scripts/test-ring2` before the
  PR, `scripts/test-e2e` because the host and compose are touched.
- **Commit after every task**, and commit before mutating anything.

## File Structure

**New — core (`src/MMLib.Alvo`)**

| File | Responsibility |
|---|---|
| `Migrations/AlvoSchemaOptions.cs` | `AlvoSchemaOptions` (`Startup`, `AllowDestructive`), bound from `Alvo:Schema:*`. Public. |
| `Migrations/Internal/DescriptorBootPlan.cs` | Stage 0: load → JSON-Schema validate → parse → map → the reserved-name/format checks. **No database access.** Returns a `BootPlan` record. |
| `Migrations/Internal/SchemaStartupDecision.cs` | Stage 2: turns (`BootPlan`, `AppliedSchema?`, plan) into `Unchanged` / `Uninitialized` / `Drifted`, and applies the mode. |
| `Migrations/Internal/AlvoBootService.cs` | `IHostedLifecycleService`; runs stages 0–3 in `StartingAsync`. |
| `Migrations/AlvoBootState.cs` | The published state (`Pending`/`Ready`/`Failed`) + applied revision. Read by the health check. Public (the health check seam crosses to Host). |
| `Migrations/AlvoStartupRefusedException.cs` | Carries the operator-readable message + fix suggestion. Public. |
| `Api/Internal/AlvoEndpointDataSource.cs` | Lazy `EndpointDataSource`; builds through the real `Map*` helpers on a nested `IEndpointRouteBuilder`. |
| `Api/Internal/NestedRouteBuilder.cs` | Minimal `IEndpointRouteBuilder` so the `Map*` helpers can be used off-app. |
| `Api/AlvoHealthEndpointRouteBuilderExtensions.cs` | `MapAlvoHealth()` — `/health/live` + `/health/ready`. Public. |
| `AlvoEndpointRouteBuilderExtensions.cs` | `MapAlvo()` — the umbrella. Public. |

**Modified**

| File | Change |
|---|---|
| `src/MMLib.Alvo.Abstractions/Migrations/AlvoSchemaStartupMode.cs` *(new)* | The enum. `Abstractions`, ASP.NET-free. |
| `src/MMLib.Alvo/AlvoServiceCollectionExtensions.cs` | Register `AlvoSchemaOptions`, `AlvoBootState`, `AlvoBootService`; call `AddAlvoApi()` such that the Data API is default-on. |
| `src/MMLib.Alvo/Api/AlvoDataApiExtensions.cs` | `AddDataApi()` becomes configuration-only (registration already happened). |
| `src/MMLib.Alvo/Api/AlvoDataApiEndpointRouteBuilderExtensions.cs` | Register the lazy data source instead of the eager `foreach`. |
| `src/MMLib.Alvo/Migrations/SchemaMigrationRunner.cs` | Delegate stage 0 to `DescriptorBootPlan`; keep `RunAsync` behaviour identical for the CLI/Management-API path. |
| `src/MMLib.Alvo.Host/AlvoHost.cs` | Delete the apply, `EnsureApplied`, and `ValidateOptions`. Composition only. |
| `src/MMLib.Alvo.Host/AlvoHostOptions.cs` | Data annotations. |
| `src/MMLib.Alvo.Host/Internal/AlvoHostOptionsValidation.cs` *(new)* | `IValidateOptions<AlvoHostOptions>` — #132's structured refusals. |
| `src/MMLib.Alvo.Host/Program.cs` | Catch `AlvoStartupRefusedException`, print, dispose, deliberate exit code. |
| `src/MMLib.Alvo.Host/appsettings.json` | `Alvo:Schema:Startup = Apply` — the image's visible policy. |
| `docker-compose.yml`, `docker-compose.field-service.yml` | `healthcheck` → `/health/ready`. |
| `docs/architecture/host.md`, `data-api.md`, `extensibility.md` | Record the new lifecycle, the readiness split, `MapAlvo`. |

---

### Task 1: The startup mode and its options, validated

**Files:**
- Create: `src/MMLib.Alvo.Abstractions/Migrations/AlvoSchemaStartupMode.cs`
- Create: `src/MMLib.Alvo/Migrations/AlvoSchemaOptions.cs`
- Modify: `src/MMLib.Alvo/AlvoServiceCollectionExtensions.cs`
- Test: `test/MMLib.Alvo.Tests/Migrations/AlvoSchemaOptionsTests.cs`

**Interfaces:**
- Produces: `enum AlvoSchemaStartupMode { Verify = 0, Apply = 1, Skip = 2 }`;
  `sealed class AlvoSchemaOptions { AlvoSchemaStartupMode Startup { get; set; } = AlvoSchemaStartupMode.Verify; bool AllowDestructive { get; set; } }`.
  Bound from section `Alvo:Schema`.

- [ ] **Step 1: Write the failing tests**

```csharp
[Fact]
public void Verify_is_the_default_so_an_embedded_host_never_runs_ddl_it_did_not_ask_for()
{
    new AlvoSchemaOptions().Startup.ShouldBe(AlvoSchemaStartupMode.Verify);
    new AlvoSchemaOptions().AllowDestructive.ShouldBeFalse();
}

// Verify == 0 matters: a mis-bound or absent configuration value lands on the
// safe mode, exactly as default(Role) lands on anon.
[Fact]
public void The_default_enum_value_is_the_safe_one()
    => default(AlvoSchemaStartupMode).ShouldBe(AlvoSchemaStartupMode.Verify);

[Fact]
public void The_mode_binds_from_configuration_case_insensitively()
{
    var services = new ServiceCollection();
    var config = new ConfigurationBuilder()
        .AddInMemoryCollection([new("alvo:schema:startup", "apply")]).Build();
    services.AddSingleton<IConfiguration>(config);
    services.AddAlvo(alvo => alvo.UseSqlite("Data Source=:memory:"));
    services.Configure<AlvoSchemaOptions>(config.GetSection("Alvo:Schema"));

    using var sp = services.BuildServiceProvider();
    sp.GetRequiredService<IOptions<AlvoSchemaOptions>>().Value.Startup
        .ShouldBe(AlvoSchemaStartupMode.Apply);
}

[Fact]
public void An_unknown_mode_is_refused_at_startup_naming_the_choices()
{
    var services = new ServiceCollection();
    var config = new ConfigurationBuilder()
        .AddInMemoryCollection([new("Alvo:Schema:Startup", "yolo")]).Build();
    services.AddSingleton<IConfiguration>(config);
    services.AddAlvo(alvo => alvo.UseSqlite("Data Source=:memory:"));
    services.Configure<AlvoSchemaOptions>(config.GetSection("Alvo:Schema"));

    using var sp = services.BuildServiceProvider();
    var refusal = Should.Throw<Exception>(
        () => sp.GetRequiredService<IOptions<AlvoSchemaOptions>>().Value);
    refusal.Message.ShouldContain("Verify");
    refusal.Message.ShouldContain("Apply");
    refusal.Message.ShouldContain("Skip");
}
```

- [ ] **Step 2: Run to verify they fail**

Run: `dotnet test test/MMLib.Alvo.Tests --filter AlvoSchemaOptionsTests`
Expected: FAIL — `AlvoSchemaOptions` / `AlvoSchemaStartupMode` do not exist.

- [ ] **Step 3: Implement the enum and options**

`AlvoSchemaStartupMode` in `Abstractions` (`namespace MMLib.Alvo.Migrations`),
with `Verify = 0` **explicitly numbered** and an XML doc per member saying what
each does on drift. `AlvoSchemaOptions` in the core.

Register in `AddAlvo`, beside the existing `AlvoOptions` registration:

```csharp
services.AddOptions<AlvoSchemaOptions>()
    .ValidateDataAnnotations()
    .ValidateOnStart();
services.TryAddEnumerable(
    ServiceDescriptor.Singleton<IValidateOptions<AlvoSchemaOptions>, AlvoSchemaOptionsValidation>());
```

`AlvoSchemaOptionsValidation` rejects an out-of-range enum with a message naming
all three modes and the configuration key — the binder maps an unknown string to
`0`, i.e. silently to `Verify`, so **validation is the only thing that can catch a
typo**. Make that the reason in the XML docs.

> Note: the binder produces `Verify` for `"yolo"`, so the test above must assert on
> a *validated* read. If `Configure` alone cannot see the raw value, have the
> validator take `IConfiguration` via `IConfigureOptions` and compare the bound
> value against the raw string. Whichever shape you land on, the fact must fail if
> the validation is removed — prove that by deleting the validator and watching it
> go red.

- [ ] **Step 4: Run to verify they pass**

Run: `dotnet test test/MMLib.Alvo.Tests --filter AlvoSchemaOptionsTests`
Expected: PASS. Confirm `Build succeeded` first.

- [ ] **Step 5: Accept the public-API baseline**

Run: `dotnet test test/MMLib.Alvo.Tests --filter PublicApi` then
`dotnet test test/MMLib.Alvo.Abstractions.Tests --filter PublicApi`.
Both baselines move (two new public types). **Do not hand-edit them.** Let Verify
write the `.received.` file and accept it with the repo's usual mechanism; then
dispatch `alvo-snapshot-judge` as the Stop hook will require.

- [ ] **Step 6: ring0 + commit**

```bash
scripts/test-ring0
git add src/MMLib.Alvo.Abstractions/Migrations/AlvoSchemaStartupMode.cs \
        src/MMLib.Alvo/Migrations/AlvoSchemaOptions.cs \
        src/MMLib.Alvo/Migrations/Internal/AlvoSchemaOptionsValidation.cs \
        src/MMLib.Alvo/AlvoServiceCollectionExtensions.cs \
        test/MMLib.Alvo.Tests/Migrations/AlvoSchemaOptionsTests.cs \
        test/MMLib.Alvo.Tests/PublicApi.MMLib.Alvo.verified.txt \
        test/MMLib.Alvo.Abstractions.Tests/PublicApi.MMLib.Alvo.Abstractions.verified.txt
git commit -m "feat(startup): add the schema startup mode, defaulting to Verify"
```

---

### Task 2: Stage 0 — a boot plan that touches no database

**Files:**
- Create: `src/MMLib.Alvo/Migrations/Internal/DescriptorBootPlan.cs`
- Modify: `src/MMLib.Alvo/Migrations/SchemaMigrationRunner.cs:84-139`
- Test: `test/MMLib.Alvo.Tests/Migrations/DescriptorBootPlanTests.cs`

**Interfaces:**
- Consumes: `IDescriptorSource`, `IDescriptorValidator`, `ICelCompiler`, `ILogger`.
- Produces:
  ```csharp
  internal sealed record BootPlan(
      AlvoDescriptor Descriptor, SchemaModel Desired, string DescriptorJson, PolicyCatalog Catalog);

  internal sealed class DescriptorBootPlan(
      IDescriptorSource source, IDescriptorValidator validator,
      ICelCompiler compiler, ILogger<DescriptorBootPlan> logger)
  {
      internal Task<BootPlan> LoadAsync(CancellationToken ct);
  }
  ```

- [ ] **Step 1: Write the failing tests**

```csharp
// The whole point of stage 0: it must be runnable with no database at all.
[Fact]
public async Task A_boot_plan_is_produced_with_no_database_registered()
{
    var plan = await Subject(VehiclesDescriptorJson).LoadAsync(default);

    plan.Descriptor.Name.ShouldNotBeNullOrWhiteSpace();
    plan.Desired.Entities.ShouldNotBeEmpty();
    plan.Catalog.ShouldNotBeNull();
}

[Fact]
public async Task An_invalid_descriptor_is_refused_before_anything_else_happens()
    => await Should.ThrowAsync<DescriptorValidationException>(
        () => Subject("{\"not\":\"a descriptor\"}").LoadAsync(default));

// This is the refusal that MapAlvoDataApi used to raise at map time. It has to
// keep failing the START, not the first request.
[Fact]
public async Task A_field_named_after_a_reserved_query_key_is_refused_at_stage_zero()
{
    var refusal = await Should.ThrowAsync<InvalidOperationException>(
        () => Subject(DescriptorDeclaringAFieldNamed(ReservedQueryKeys.Limit)).LoadAsync(default));

    refusal.Message.ShouldContain(ReservedQueryKeys.Limit);
    refusal.Message.ShouldContain("Rename the field");
}

// A rule that no longer compiles must reject the boot, not be discovered later.
[Fact]
public async Task An_uncompilable_rule_is_refused_at_stage_zero()
    => await Should.ThrowAsync<DescriptorValidationException>(
        () => Subject(DescriptorWithRule("'amdin' in @user.roles")).LoadAsync(default));
```

- [ ] **Step 2: Run to verify they fail**

Run: `dotnet test test/MMLib.Alvo.Tests --filter DescriptorBootPlanTests`
Expected: FAIL — `DescriptorBootPlan` does not exist.

- [ ] **Step 3: Implement `DescriptorBootPlan`**

Move, verbatim, out of `SchemaMigrationRunner.RunAsync` lines 88–103: the
`LoadAsync`, `Validate`, `Parse`, `Map` and `UnhonouredSubsystems.Warn` sequence.
Then add the two checks that currently live in `MapAlvoDataApi`:
`ReservedQueryKeys.EnsureNoneIsShadowed(desired.Entities)` and
`FormatCatalog.Build(desired.Entities)`, plus `PolicyCatalog.Build(...)`.

Keep `UnhonouredSubsystems.Warn` here, and keep its existing reason intact: it
warns on **every** boot including the unchanged restart, so an author is told
about unhonoured blocks more than once.

- [ ] **Step 4: Make `SchemaMigrationRunner` delegate to it**

`RunAsync` becomes: `var plan = await _bootPlan.LoadAsync(ct);` then the existing
store-read / plan / guard / apply / save flow, unchanged. **Behaviour must be
byte-identical** — the CLI and the Management API still use this path.

- [ ] **Step 5: Run the full existing migration suite**

Run: `dotnet test test/MMLib.Alvo.Tests --filter Migration`
Expected: PASS, unchanged. This is a refactor; any behaviour change here is a bug.

- [ ] **Step 6: Prove the extraction discriminates**

Delete the `ReservedQueryKeys.EnsureNoneIsShadowed` line from `DescriptorBootPlan`
and confirm `A_field_named_after_a_reserved_query_key_is_refused_at_stage_zero`
goes **red**. Restore it. Verify the edit landed (CRLF) before believing the
result.

- [ ] **Step 7: ring0 + commit**

```bash
scripts/test-ring0
git add src/MMLib.Alvo/Migrations/ test/MMLib.Alvo.Tests/Migrations/DescriptorBootPlanTests.cs
git commit -m "refactor(startup): extract stage 0 as a boot plan that needs no database"
```

---

### Task 3: Stage 2 — the uninitialized / unchanged / drifted decision

**Files:**
- Create: `src/MMLib.Alvo/Migrations/Internal/SchemaStartupDecision.cs`
- Test: `test/MMLib.Alvo.Tests/Migrations/SchemaStartupDecisionTests.cs`

**Interfaces:**
- Produces:
  ```csharp
  internal enum SchemaStartupOutcome { Unchanged, Initialize, Apply, Refuse }

  internal readonly record struct SchemaStartupDecision(
      SchemaStartupOutcome Outcome, MigrationPlan Plan, string? Refusal);

  internal static class SchemaStartupPolicy
  {
      internal static SchemaStartupDecision Decide(
          AppliedSchema? applied, MigrationPlan plan, AlvoSchemaOptions options);
  }
  ```

- [ ] **Step 1: Write the failing tests — the decision table in full**

```csharp
// Uninitialized: allowed in Verify too. This is what keeps zero-config dev and
// the 60-second docker run working (A:553, S:157) without an unsafe default.
[Theory]
[InlineData(AlvoSchemaStartupMode.Verify)]
[InlineData(AlvoSchemaStartupMode.Apply)]
public void An_empty_database_initializes_in_every_mode_but_Skip(AlvoSchemaStartupMode mode)
    => Decide(applied: null, NonEmptyPlan, mode).Outcome.ShouldBe(SchemaStartupOutcome.Initialize);

[Fact]
public void Skip_never_touches_the_database_even_when_uninitialized()
    => Decide(applied: null, NonEmptyPlan, AlvoSchemaStartupMode.Skip)
        .Outcome.ShouldBe(SchemaStartupOutcome.Unchanged);

// The ordinary restart, and the common case.
[Theory]
[InlineData(AlvoSchemaStartupMode.Verify)]
[InlineData(AlvoSchemaStartupMode.Apply)]
[InlineData(AlvoSchemaStartupMode.Skip)]
public void An_unchanged_descriptor_serves_in_every_mode(AlvoSchemaStartupMode mode)
    => Decide(AppliedAt(1), MigrationPlan.Empty, mode).Outcome.ShouldBe(SchemaStartupOutcome.Unchanged);

[Fact]
public void Drift_under_Verify_refuses_and_the_refusal_names_the_steps()
{
    var decision = Decide(AppliedAt(1), PlanAdding("orders", "discount"), AlvoSchemaStartupMode.Verify);

    decision.Outcome.ShouldBe(SchemaStartupOutcome.Refuse);
    decision.Refusal.ShouldContain("orders");
    decision.Refusal.ShouldContain("discount");
    decision.Refusal.ShouldContain("Alvo__Schema__Startup=Apply");
}

[Fact]
public void Drift_under_Apply_applies()
    => Decide(AppliedAt(1), PlanAdding("orders", "discount"), AlvoSchemaStartupMode.Apply)
        .Outcome.ShouldBe(SchemaStartupOutcome.Apply);

// The destructive guardrail is NOT weakened by Apply. This is the line that
// separates "apply on boot" from "lose data on boot".
[Fact]
public void A_destructive_plan_is_refused_under_Apply_unless_AllowDestructive()
{
    Decide(AppliedAt(1), DestructivePlan, AlvoSchemaStartupMode.Apply)
        .Outcome.ShouldBe(SchemaStartupOutcome.Refuse);

    Decide(AppliedAt(1), DestructivePlan, AlvoSchemaStartupMode.Apply, allowDestructive: true)
        .Outcome.ShouldBe(SchemaStartupOutcome.Apply);
}

[Fact]
public void A_destructive_refusal_marks_which_step_is_destructive()
    => Decide(AppliedAt(1), DestructivePlan, AlvoSchemaStartupMode.Apply)
        .Refusal.ShouldContain("destructive");
```

- [ ] **Step 2: Run to verify they fail**

Run: `dotnet test test/MMLib.Alvo.Tests --filter SchemaStartupDecisionTests`
Expected: FAIL — the type does not exist.

- [ ] **Step 3: Implement `SchemaStartupPolicy.Decide`**

Pure function, no I/O, so the whole table above is a unit test. Reuse
`DestructiveChangeGuard.Describe(plan)` for the step listing rather than writing a
second formatter. Keep the method under the ~25-line ceiling by extracting the
refusal-message building.

- [ ] **Step 4: Run to verify they pass**

Run: `dotnet test test/MMLib.Alvo.Tests --filter SchemaStartupDecisionTests`
Expected: PASS.

- [ ] **Step 5: Prove the destructive guard discriminates**

Change `!options.AllowDestructive` to `false` in the destructive branch and
confirm `A_destructive_plan_is_refused_under_Apply_unless_AllowDestructive` goes
red. Restore.

- [ ] **Step 6: ring0 + commit**

```bash
scripts/test-ring0
git add src/MMLib.Alvo/Migrations/Internal/SchemaStartupDecision.cs \
        test/MMLib.Alvo.Tests/Migrations/SchemaStartupDecisionTests.cs
git commit -m "feat(startup): decide the boot outcome from initialized-vs-drifted, not a flag"
```

---

### Task 4: The boot service, and the state it publishes

**Files:**
- Create: `src/MMLib.Alvo/Migrations/AlvoBootState.cs`
- Create: `src/MMLib.Alvo/Migrations/AlvoStartupRefusedException.cs`
- Create: `src/MMLib.Alvo/Migrations/Internal/AlvoBootService.cs`
- Modify: `src/MMLib.Alvo/AlvoServiceCollectionExtensions.cs`
- Test: `test/MMLib.Alvo.Tests/Migrations/AlvoBootServiceTests.cs`

**Interfaces:**
- Consumes: `DescriptorBootPlan`, `SchemaStartupPolicy.Decide`, `ISchemaMigrator`,
  `IAppliedSchemaStore`, `IPolicyCatalogProvider`, `IOptions<AlvoSchemaOptions>`.
- Produces:
  ```csharp
  public enum AlvoBootPhase { Pending, Ready, Failed }

  public sealed class AlvoBootState   // singleton, thread-safe
  {
      public AlvoBootPhase Phase { get; }
      public int? AppliedRevision { get; }
      public string? Failure { get; }
      internal void Ready(int revision);
      internal void Failed(string reason);
  }

  public sealed class AlvoStartupRefusedException : Exception
  {
      public string FixSuggestion { get; }
  }
  ```

- [ ] **Step 1: Write the failing tests**

```csharp
// The guarantee the whole design rests on, and the one deviation 38 protects.
[Fact]
public async Task The_boot_runs_before_the_server_listens()
{
    await using var world = await AlvoHostWorld.StartAsync();   // real host, TestServer

    // Observed from inside StartingAsync by a probe the fixture registers:
    world.ServerWasListeningDuringBoot.ShouldBeFalse();
    world.BootState.Phase.ShouldBe(AlvoBootPhase.Ready);
}

[Fact]
public async Task A_descriptor_that_cannot_apply_leaves_the_state_Failed_and_stops_the_start()
{
    var refusal = await Should.ThrowAsync<AlvoStartupRefusedException>(
        () => AlvoHostWorld.StartAsync(descriptor: DropsAField));

    refusal.FixSuggestion.ShouldNotBeNullOrWhiteSpace();
}

[Fact]
public async Task A_successful_boot_publishes_the_applied_revision()
{
    await using var world = await AlvoHostWorld.StartAsync();
    world.BootState.AppliedRevision.ShouldBe(1);
}

// Priming is the thing the old code got for free from the apply, and the thing
// RuntimeSchemaService's remarks call a real gap. Prove it happens on a restart
// where NOTHING is applied.
[Fact]
public async Task An_unchanged_restart_still_primes_the_policy_catalog()
{
    await using var first = await AlvoHostWorld.StartAsync(database: Shared);
    await first.DisposeAsync();

    await using var second = await AlvoHostWorld.StartAsync(database: Shared);

    // A read that a rule permits must succeed — which is only possible if the
    // catalog was primed without any DDL running on this boot.
    (await second.Client.GetAsync("/api/vehicles")).StatusCode.ShouldBe(HttpStatusCode.OK);
    second.MigrationsRunOnThisBoot.ShouldBe(0);
}
```

- [ ] **Step 2: Run to verify they fail**

Run: `dotnet test test/MMLib.Alvo.Host.Tests --filter AlvoBootService`
Expected: FAIL.

- [ ] **Step 3: Implement `AlvoBootService : IHostedLifecycleService`**

`StartingAsync` runs stages 0–3 and nothing else; the other five members are
`Task.CompletedTask`. Order, and each stage in its own private method:

1. `var plan = await _bootPlan.LoadAsync(ct);`
2. system schema up (Task 5 fills this in; for now call the existing
   `SystemSchemaInitializer` seam).
3. read the applied snapshot, plan, `SchemaStartupPolicy.Decide`.
4. on `Refuse` → `state.Failed(refusal)` then
   `throw new AlvoStartupRefusedException(...)`.
   On `Initialize`/`Apply` → apply, save the snapshot, `SetCurrent`.
   On `Unchanged` → `PolicyCatalogPriming.Prime(...)`.
5. `state.Ready(revision)`.

Do **not** call `IStartupValidator.Validate()` — the framework already ran it
(design doc, fact 8). Register with
`services.TryAddEnumerable(ServiceDescriptor.Singleton<IHostedService, AlvoBootService>())`
and `TryAddSingleton<AlvoBootState>()`.

- [ ] **Step 4: Run to verify they pass**

Run: `dotnet test test/MMLib.Alvo.Host.Tests --filter AlvoBootService`
Expected: PASS. Assert `Build succeeded` first.

- [ ] **Step 5: Prove the before-listening fact discriminates**

Change `StartingAsync` to `StartedAsync` and confirm
`The_boot_runs_before_the_server_listens` goes **red**. Restore. This is the
mutation that proves the fact is not vacuous — without it the test would pass on
any lifecycle hook.

- [ ] **Step 6: ring0 + commit**

```bash
scripts/test-ring0
git add src/MMLib.Alvo/Migrations/ src/MMLib.Alvo/AlvoServiceCollectionExtensions.cs \
        test/MMLib.Alvo.Host.Tests/
git commit -m "feat(startup): drive the boot from a hosted service, before the server listens"
```

---

### Task 5: Concurrent cold start converges

**Files:**
- Modify: `src/MMLib.Alvo/Migrations/Internal/AlvoBootService.cs`
- Test: `test/MMLib.Alvo.Data.Sqlite.Tests/ConcurrentBootTests.cs`
- Test: `test/MMLib.Alvo.Data.PostgreSql.Tests.Integration/PostgreSqlConcurrentBootTests.cs`

**Interfaces:**
- Consumes: `AlvoBootService` from Task 4; `DescriptorConcurrencyException`.
- Produces: no new surface — a behaviour guarantee only.

- [ ] **Step 1: Write the failing test**

```csharp
// Three replicas, one empty database, started at once. Without convergence this
// is a crash loop on an ordinary cold start of a replica set.
[Fact]
public async Task Three_hosts_cold_starting_against_one_empty_database_all_serve()
{
    var database = SharedSqliteFile();

    var hosts = await Task.WhenAll(Enumerable.Range(0, 3)
        .Select(_ => AlvoHostWorld.StartAsync(database: database)));

    foreach (var host in hosts)
    {
        host.BootState.Phase.ShouldBe(AlvoBootPhase.Ready);
        (await host.Client.GetAsync("/api/vehicles")).StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    // Exactly one of them did the initialising.
    hosts.Count(h => h.MigrationsRunOnThisBoot > 0).ShouldBe(1);

    foreach (var host in hosts) await host.DisposeAsync();
}
```

- [ ] **Step 2: Run to verify it fails**

Run: `dotnet test test/MMLib.Alvo.Data.Sqlite.Tests --filter ConcurrentBoot`
Expected: FAIL — the losers throw `DescriptorConcurrencyException` (or a unique
constraint violation) out of `StartingAsync`.

- [ ] **Step 3: Implement convergence**

Catch the optimistic-lock loss around the initialize/apply step, **re-read** the
applied snapshot, re-`Decide`, and proceed. Bound it: at most one retry, then
fail. A loop here would hang a boot instead of failing it.

Do **not** introduce a lock. The design records why: EF's SQLite migration lock
is a table row with no timeout that survives a killed process, so a lock is the
shape that turns an OOM-kill into a permanently wedged boot. The revision check
has nothing to leak.

- [ ] **Step 4: Run to verify it passes, on both engines**

Run: `dotnet test test/MMLib.Alvo.Data.Sqlite.Tests --filter ConcurrentBoot`
Then: `dotnet test test/MMLib.Alvo.Data.PostgreSql.Tests.Integration --filter ConcurrentBoot`
(needs Docker). Expected: PASS on both. SQLite's single-writer cap makes this the
harder engine, so do not skip it as "covered by Postgres".

- [ ] **Step 5: Prove it discriminates**

Remove the retry and confirm the test goes red. Restore.

- [ ] **Step 6: ring0 + commit**

```bash
scripts/test-ring0
git add src/MMLib.Alvo/Migrations/Internal/AlvoBootService.cs test/
git commit -m "fix(startup): converge when replicas race an initialising cold start"
```

---

### Task 6: The lazy endpoint data source

**Files:**
- Create: `src/MMLib.Alvo/Api/Internal/NestedRouteBuilder.cs`
- Create: `src/MMLib.Alvo/Api/Internal/AlvoEndpointDataSource.cs`
- Modify: `src/MMLib.Alvo/Api/AlvoDataApiEndpointRouteBuilderExtensions.cs:33-60`
- Test: `test/MMLib.Alvo.Api.Tests/LazyRouteMaterialisationTests.cs`

**Interfaces:**
- Consumes: `EntityRouteCatalog`, `AlvoApiOptions`, `AlvoContextFilterFactory`,
  `FormatCatalog`.
- Produces: `internal sealed class AlvoEndpointDataSource : EndpointDataSource`
  with `IReadOnlyList<Endpoint> Endpoints`, `IChangeToken GetChangeToken()`.

- [ ] **Step 1: Write the failing tests**

```csharp
// The coupling this task exists to break.
[Fact]
public async Task Routes_materialise_from_a_schema_primed_after_the_app_started()
{
    await using var world = await AlvoHostWorld.StartAsync();   // maps before priming

    (await world.Client.GetAsync("/api/vehicles")).StatusCode.ShouldBe(HttpStatusCode.OK);
}

[Fact]
public async Task An_entity_the_descriptor_does_not_declare_still_has_no_route()
{
    await using var world = await AlvoHostWorld.StartAsync();

    (await world.Client.GetAsync("/api/nope")).StatusCode.ShouldBe(HttpStatusCode.NotFound);
}

// Fact 4 of the design's measured table: the trap. Hand-built endpoints route
// perfectly and are invisible to OpenAPI, so a routing-only suite passes while
// the document silently empties.
[Fact]
public async Task The_OpenApi_document_lists_every_mapped_entity_route()
{
    await using var world = await AlvoHostWorld.StartAsync();

    var document = await world.Client.GetStringAsync(AlvoHost.OpenApiDocumentPath);

    document.ShouldContain("/api/vehicles");
    foreach (var entity in world.DeclaredEntities)
        document.ShouldContain($"/api/{entity}");
}

// UseRouting/UseEndpoints are only wired when DataSources.Count > 0, and the
// count is of SOURCES not endpoints. An empty source must still be registered.
[Fact]
public void MapAlvoDataApi_registers_a_data_source_even_before_the_schema_is_known()
{
    var builder = WebApplication.CreateSlimBuilder();
    builder.Services.AddAlvo(alvo => alvo.UseSqlite("Data Source=:memory:"));
    using var app = builder.Build();

    app.MapAlvoDataApi();

    ((IEndpointRouteBuilder)app).DataSources.ShouldNotBeEmpty();
}
```

- [ ] **Step 2: Run to verify they fail**

Run: `dotnet test test/MMLib.Alvo.Api.Tests --filter LazyRouteMaterialisation`
Expected: FAIL — mapping currently enumerates eagerly, so a host that maps before
priming maps nothing.

- [ ] **Step 3: Implement `NestedRouteBuilder`**

```csharp
internal sealed class NestedRouteBuilder(IServiceProvider services) : IEndpointRouteBuilder
{
    public IServiceProvider ServiceProvider { get; } = services;
    public ICollection<EndpointDataSource> DataSources { get; } = [];
    public IApplicationBuilder CreateApplicationBuilder() => new ApplicationBuilder(ServiceProvider);
}
```

- [ ] **Step 4: Implement `AlvoEndpointDataSource`**

`Endpoints` builds a `NestedRouteBuilder`, calls the **existing**
`DataApiEndpoints.Map(...)` for each entity in `EntityRouteCatalog.Entities`
exactly as the old `foreach` did, then flattens
`inner.DataSources.SelectMany(d => d.Endpoints)`.

**Building through `DataApiEndpoints.Map` is not a style choice — it is the only
measured way the ApiExplorer metadata survives** (design doc, facts 4 and 5).
Say so in the XML docs, so a later "simplification" to hand-built
`RouteEndpointBuilder`s is recognisable as the regression it would be.

`GetChangeToken()` returns a token that never fires for now (#103 owns mutation).
When it does grow one, replace the token **before** cancelling the old CTS —
aspnetcore#44392.

- [ ] **Step 5: Reduce `MapAlvoDataApi` to registration**

It resolves `EntityRouteCatalog` (keep the crafted "Alvo is not registered"
refusal), constructs the data source and adds it to `endpoints.DataSources`. The
`ReservedQueryKeys`/`FormatCatalog` calls are **gone from here** — Task 2 moved
them to stage 0.

- [ ] **Step 6: Update the moved refusal's test**

`DataApiQueryTests.A_schema_reaching_mapping_without_validation_is_still_refused_for_a_reserved_field_name`
currently asserts `MapAlvoDataApi()` throws. The refusal now happens at stage 0.
**Move the assertion, do not delete it** — it is a defence-in-depth belt against a
hostile `ISchemaRegistry`, and the design requires the refusal to stay at start
rather than becoming a runtime 500.

- [ ] **Step 7: Run to verify they pass**

Run: `dotnet test test/MMLib.Alvo.Api.Tests`
Expected: PASS. Confirm `Build succeeded` first.

- [ ] **Step 8: Prove the OpenAPI fact discriminates**

Temporarily replace the `DataApiEndpoints.Map` call with a hand-built
`RouteEndpointBuilder` for one entity. Confirm the routing tests stay **green**
and `The_OpenApi_document_lists_every_mapped_entity_route` goes **red**. That is
the whole reason the fact exists. Restore, and check the edit landed (CRLF).

- [ ] **Step 9: ring0 + commit**

```bash
scripts/test-ring0
git add src/MMLib.Alvo/Api/ test/MMLib.Alvo.Api.Tests/
git commit -m "feat(api): materialise Data API routes lazily from the primed schema"
```

---

### Task 7: `/health/live` and `/health/ready`

**Files:**
- Create: `src/MMLib.Alvo/Api/AlvoHealthEndpointRouteBuilderExtensions.cs`
- Create: `src/MMLib.Alvo/Api/Internal/AlvoSchemaHealthCheck.cs`
- Modify: `src/MMLib.Alvo.Host/Internal/AlvoHostEndpoints.cs` (drop its own liveness)
- Test: `test/MMLib.Alvo.Host.Tests/AlvoHealthTests.cs`

**Interfaces:**
- Consumes: `AlvoBootState`.
- Produces:
  ```csharp
  public static IEndpointRouteBuilder MapAlvoHealth(this IEndpointRouteBuilder endpoints);
  // constants: AlvoHealth.LivenessPath = "/health/live", ReadinessPath = "/health/ready",
  //            AlvoHealth.ReadyTag = "ready"
  ```

- [ ] **Step 1: Write the failing tests**

```csharp
[Fact]
public async Task Liveness_answers_even_while_the_boot_is_still_pending()
{
    using var app = HostWithBootBlockedAt(AlvoBootPhase.Pending);

    (await app.Client.GetAsync(AlvoHealth.LivenessPath)).StatusCode.ShouldBe(HttpStatusCode.OK);
}

// THE assertion that matters. Degraded maps to 200 by default and Kubernetes
// treats any 2xx as success, so asserting the health *string* would pass while
// the pod happily received traffic with no schema.
[Fact]
public async Task Readiness_is_503_while_the_boot_is_pending()
{
    using var app = HostWithBootBlockedAt(AlvoBootPhase.Pending);

    var response = await app.Client.GetAsync(AlvoHealth.ReadinessPath);

    response.StatusCode.ShouldBe(HttpStatusCode.ServiceUnavailable);
}

[Fact]
public async Task Readiness_is_200_once_the_boot_is_ready()
{
    await using var world = await AlvoHostWorld.StartAsync();

    (await world.Client.GetAsync(AlvoHealth.ReadinessPath)).StatusCode.ShouldBe(HttpStatusCode.OK);
}

// Liveness must contain ZERO checks, so a future health check cannot make
// liveness fail and start killing containers under load.
[Fact]
public async Task Liveness_runs_no_checks_at_all()
{
    using var app = HostWithAnAlwaysUnhealthyCheck();

    (await app.Client.GetAsync(AlvoHealth.LivenessPath)).StatusCode.ShouldBe(HttpStatusCode.OK);
    (await app.Client.GetAsync(AlvoHealth.ReadinessPath)).StatusCode.ShouldBe(HttpStatusCode.ServiceUnavailable);
}

[Fact]
public async Task Readiness_answers_an_unauthenticated_probe()
{
    await using var world = await AlvoHostWorld.StartAsync();
    world.Client.DefaultRequestHeaders.Clear();

    (await world.Client.GetAsync(AlvoHealth.ReadinessPath)).StatusCode.ShouldBe(HttpStatusCode.OK);
}
```

- [ ] **Step 2: Run to verify they fail**

Run: `dotnet test test/MMLib.Alvo.Host.Tests --filter AlvoHealth`
Expected: FAIL — `/health/ready` does not exist.

- [ ] **Step 3: Implement the check and the mapping**

`AlvoSchemaHealthCheck` reads `AlvoBootState` and returns
`HealthCheckResult.Unhealthy($"…")` while `Phase != Ready`. **Never `Degraded`** —
put the reason in the XML docs, citing the 200 mapping.

`MapAlvoHealth` maps liveness with `Predicate = _ => false` and readiness with
`Predicate = hc => hc.Tags.Contains(AlvoHealth.ReadyTag)`, registering the schema
check under that tag. `AddHealthChecks()` is called from `AddAlvo` via `TryAdd`
semantics so a host that already called it is unaffected.

Keep `AlvoHost.LivenessPath` as a forwarding constant so nothing external breaks,
and have the Host delegate to `MapAlvoHealth` instead of mapping its own.

- [ ] **Step 4: Run to verify they pass**

Run: `dotnet test test/MMLib.Alvo.Host.Tests --filter AlvoHealth`
Expected: PASS.

- [ ] **Step 5: Prove the status-code fact discriminates**

Change `Unhealthy` to `Degraded` and confirm `Readiness_is_503_while_the_boot_is_pending`
goes **red** (it would return 200). Restore. This mutation is the entire reason
the test asserts a status code.

- [ ] **Step 6: ring0 + accept baselines + commit**

```bash
scripts/test-ring0
git add src/MMLib.Alvo/Api/ src/MMLib.Alvo.Host/ test/ \
        test/MMLib.Alvo.Tests/PublicApi.MMLib.Alvo.verified.txt \
        test/MMLib.Alvo.Host.Tests/PublicApi.MMLib.Alvo.Host.verified.txt
git commit -m "feat(health): split readiness from liveness on the boot state"
```

---

### Task 8: `MapAlvo()`, and the Data API on by default

**Files:**
- Create: `src/MMLib.Alvo/AlvoEndpointRouteBuilderExtensions.cs`
- Modify: `src/MMLib.Alvo/Api/AlvoDataApiExtensions.cs:24`
- Modify: `src/MMLib.Alvo/AlvoServiceCollectionExtensions.cs`
- Test: `test/MMLib.Alvo.Api.Tests/MinimalRegistrationTests.cs`

**Interfaces:**
- Produces: `public static IEndpointRouteBuilder MapAlvo(this IEndpointRouteBuilder endpoints);`
  — composes `MapAlvoDataApi()` + `MapAlvoHealth()`.

- [ ] **Step 1: Write the failing tests**

```csharp
// The DX claim, as a test: two calls and nothing else.
[Fact]
public async Task Two_calls_are_enough_for_a_working_backend()
{
    var builder = WebApplication.CreateSlimBuilder();
    builder.WebHost.UseTestServer();
    builder.Services.AddAlvo(alvo => alvo
        .UseSqlite(FreshSqliteConnectionString())
        .FromDescriptor(VehiclesDescriptorPath));

    await using var app = builder.Build();
    app.MapAlvo();
    await app.StartAsync();

    var client = app.GetTestClient();
    (await client.GetAsync("/api/vehicles")).StatusCode.ShouldBe(HttpStatusCode.OK);
    (await client.GetAsync(AlvoHealth.ReadinessPath)).StatusCode.ShouldBe(HttpStatusCode.OK);
}

// No AddDataApi() call anywhere above — it must be on by default.
[Fact]
public void The_data_api_is_registered_without_an_explicit_AddDataApi()
{
    var services = new ServiceCollection();
    services.AddAlvo(alvo => alvo.UseSqlite("Data Source=:memory:"));

    using var sp = services.BuildServiceProvider();
    sp.GetService<EntityRouteCatalog>().ShouldNotBeNull();
}

[Fact]
public void AddDataApi_still_configures_and_is_idempotent()
{
    var services = new ServiceCollection();
    services.AddAlvo(alvo => alvo
        .UseSqlite("Data Source=:memory:")
        .AddDataApi(api => api.RoutePrefix = "/data")
        .AddDataApi());

    using var sp = services.BuildServiceProvider();
    sp.GetRequiredService<IOptions<AlvoApiOptions>>().Value.RoutePrefix.ShouldBe("/data");
}

[Fact]
public void MapAlvo_maps_both_the_data_api_and_health()
{
    var builder = WebApplication.CreateSlimBuilder();
    builder.Services.AddAlvo(alvo => alvo.UseSqlite("Data Source=:memory:"));
    using var app = builder.Build();

    app.MapAlvo();

    var paths = ((IEndpointRouteBuilder)app).DataSources
        .SelectMany(d => d.Endpoints).OfType<RouteEndpoint>()
        .Select(e => e.RoutePattern.RawText).ToList();
    paths.ShouldContain(AlvoHealth.LivenessPath);
    paths.ShouldContain(AlvoHealth.ReadinessPath);
}
```

- [ ] **Step 2: Run to verify they fail**

Run: `dotnet test test/MMLib.Alvo.Api.Tests --filter MinimalRegistration`
Expected: FAIL — `MapAlvo` does not exist; `EntityRouteCatalog` is not registered
without `AddDataApi()`.

- [ ] **Step 3: Make the Data API default-on**

`AddAlvo` already calls `AddAlvoApi()`. Move whatever `AddDataApi()` registers
into that call, leaving `AddDataApi(configure)` as **configuration only** (an
`AlvoApiOptions` `Configure`), idempotent per `extensibility.md` rule 7.

- [ ] **Step 4: Implement `MapAlvo`**

```csharp
public static IEndpointRouteBuilder MapAlvo(this IEndpointRouteBuilder endpoints)
{
    ArgumentNullException.ThrowIfNull(endpoints);
    endpoints.MapAlvoHealth();
    endpoints.MapAlvoDataApi();
    return endpoints;
}
```

Health first, so a probe route exists even if the Data API's registration
refuses. Document that `MapAlvo` is a *composition*, that the parts stay public,
and that Alvo never calls `UseRouting`/`UseEndpoints` on the host's behalf
(routing docs, "DO NOT").

- [ ] **Step 5: Run to verify they pass**

Run: `dotnet test test/MMLib.Alvo.Api.Tests`
Expected: PASS.

- [ ] **Step 6: ring0 + baselines + commit**

```bash
scripts/test-ring0
git add src/MMLib.Alvo/ test/ test/MMLib.Alvo.Tests/PublicApi.MMLib.Alvo.verified.txt
git commit -m "feat(dx): add MapAlvo and register the Data API by default"
```

---

### Task 9: `AlvoHostOptions` validated, and #132's operator-readable refusal

**Files:**
- Modify: `src/MMLib.Alvo.Host/AlvoHostOptions.cs`
- Create: `src/MMLib.Alvo.Host/Internal/AlvoHostOptionsValidation.cs`
- Modify: `src/MMLib.Alvo.Host/Program.cs`
- Test: `test/MMLib.Alvo.Host.Tests/AlvoHostConfigurationRefusalTests.cs`

**Interfaces:**
- Produces: `internal sealed class AlvoHostOptionsValidation : IValidateOptions<AlvoHostOptions>`.

- [ ] **Step 1: Write the failing tests**

```csharp
// #132: today this is an unhandled FileNotFoundException and exit 139.
[Fact]
public async Task A_missing_descriptor_is_refused_by_name_with_the_mount_fix()
{
    var refusal = await Should.ThrowAsync<OptionsValidationException>(
        () => AlvoHostWorld.StartAsync(descriptorPath: "/nope/missing.json"));

    refusal.Message.ShouldContain("/nope/missing.json");
    refusal.Message.ShouldContain("Alvo__DescriptorPath");
    refusal.Message.ShouldContain("-v");            // the docker mount fix
}

[Fact]
public async Task An_unknown_database_provider_is_refused_with_the_choices_named()
{
    var refusal = await Should.ThrowAsync<OptionsValidationException>(
        () => AlvoHostWorld.StartAsync(provider: "mongo"));

    refusal.Message.ShouldContain("mongo");
    refusal.Message.ShouldContain(AlvoHostDatabaseOptions.Sqlite);
    refusal.Message.ShouldContain(AlvoHostDatabaseOptions.PostgreSql);
}

[Fact]
public async Task PostgreSql_with_no_connection_string_is_refused()
{
    var refusal = await Should.ThrowAsync<OptionsValidationException>(
        () => AlvoHostWorld.StartAsync(
            provider: AlvoHostDatabaseOptions.PostgreSql, connectionString: null));

    refusal.Message.ShouldContain("ConnectionStrings__Alvo");
}

// The validation must precede the DDL, which is the property the existing
// credential test protects. Restate it for the descriptor path.
[Fact]
public async Task A_configuration_refusal_leaves_the_database_untouched()
{
    var database = FreshSqliteFile();

    await Should.ThrowAsync<OptionsValidationException>(
        () => AlvoHostWorld.StartAsync(database: database, provider: "mongo"));

    TableCount(database).ShouldBe(0);
}
```

- [ ] **Step 2: Run to verify they fail**

Run: `dotnet test test/MMLib.Alvo.Host.Tests --filter ConfigurationRefusal`
Expected: FAIL — today these are `FileNotFoundException` / hand-crafted throws
from elsewhere, not `OptionsValidationException`.

- [ ] **Step 3: Implement the validator**

`IValidateOptions<AlvoHostOptions>` checking, each with a fix suggestion:
descriptor path non-empty **and the file exists**; `Database.Provider` is one of
the two constants; PostgreSQL has a connection string. Register with
`AddOptions<AlvoHostOptions>().Bind(...).ValidateOnStart()` plus the validator.

Move `AlvoDatabaseSelector`'s crafted refusal here rather than duplicating the
message; the selector runs while the container is still being built, so keep its
guard but source the wording from one place.

- [ ] **Step 4: Give `Program.cs` a deliberate exit**

```csharp
using MMLib.Alvo.Host;

try
{
    var app = await AlvoHost.BuildAsync(AlvoHost.CreateBuilder(args));
    await using (app) await app.RunAsync();
    return 0;
}
catch (Exception ex) when (AlvoHostExit.IsConfigurationFailure(ex))
{
    Console.Error.WriteLine(AlvoHostExit.Describe(ex));
    return AlvoHostExit.ConfigurationFailure;   // 78, EX_CONFIG
}
```

`await using (app)` is the disposal the design requires: PR4 fixed a leak where a
refused start kept the SQLite file open, and this is where that regression would
reappear.

- [ ] **Step 5: Run to verify they pass**

Run: `dotnet test test/MMLib.Alvo.Host.Tests`
Expected: PASS, including the pre-existing
`A_refused_restart_disposes_the_application_it_had_already_built`.

- [ ] **Step 6: ring0 + commit**

```bash
scripts/test-ring0
git add src/MMLib.Alvo.Host/ test/MMLib.Alvo.Host.Tests/
git commit -m "fix(host): refuse a bad configuration by name with a fix (#132)"
```

---

### Task 10: Collapse `AlvoHost`

**Files:**
- Modify: `src/MMLib.Alvo.Host/AlvoHost.cs:139-229`
- Modify: `src/MMLib.Alvo.Host/appsettings.json`
- Test: `test/MMLib.Alvo.Host.Tests/AlvoHostRestartTests.cs` (existing, must pass)

**Interfaces:**
- Consumes: everything from Tasks 4, 7, 8.
- Produces: `BuildAsync` with no apply and no `EnsureApplied`;
  `ValidateOptions` **deleted**.

- [ ] **Step 1: Delete the apply, `EnsureApplied` and `ValidateOptions`**

`ComposeAsync` becomes: `UseExceptionHandler` → forwarded headers → path base →
`app.MapAlvo()` → docs. The three `ApplyAlvoDescriptorAsync` /
`EnsureApplied` / `ValidateOptions` lines go, and so do the parts of the XML docs
that explain an ordering that no longer exists — **rewrite those remarks, do not
leave them describing the old shape.**

Set `"Alvo": { "Schema": { "Startup": "Apply" } }` in the Host's
`appsettings.json`, with a comment-free but documented rationale in `host.md`:
the image is a pre-wired host, and A:553 requires a working backend from a bare
`docker run`.

- [ ] **Step 2: Run the whole host suite**

Run: `dotnet test test/MMLib.Alvo.Host.Tests`
Expected: PASS. Specifically these four pre-existing facts must survive:
`A_descriptor_that_cannot_apply_stops_the_host_from_starting`,
`An_unchanged_descriptor_restarts_over_the_database_the_first_boot_created`,
`A_descriptor_that_drops_a_field_fails_the_restart_and_names_the_step`,
`A_refused_restart_disposes_the_application_it_had_already_built`.

If any needs its *mechanism* updated, update the mechanism and keep the
**assertion** — these encode deviation 38's guarantee, and weakening one to get
green is the failure mode this step is most exposed to.

- [ ] **Step 3: Confirm the docs assertion still holds**

Run: `dotnet test test/MMLib.Alvo.Host.Tests --filter Docs`
Expected: PASS — the OpenAPI document is generated on request, so the "docs map
last" ordering no longer matters. If a docs test was pinning that ordering,
replace it with the content assertion from Task 6.

- [ ] **Step 4: ring2**

Run: `scripts/test-ring2`
Expected: green. Assert `Build succeeded` before believing it.

- [ ] **Step 5: Commit**

```bash
git add src/MMLib.Alvo.Host/ test/MMLib.Alvo.Host.Tests/
git commit -m "refactor(host): compose only — the boot service owns the apply"
```

---

### Task 11: Compose, e2e, and the docs of record

**Files:**
- Modify: `docker-compose.yml`, `docker-compose.field-service.yml`
- Modify: `test/teapie/` — add a readiness case
- Modify: `docs/architecture/host.md`, `data-api.md`, `extensibility.md`
- Modify: `docs/superpowers/specs/2026-07-25-f3-crud-vertical-slice-design.md`
  (note deviations 38 and 48 as superseded/discharged, and correct the EF
  value-converter line found while closing #74)
- Modify: `examples/complex-crm/crm.alvo.json` (retired singular `@user.role`)

- [ ] **Step 1: Point compose at readiness**

Change both stacks' `healthcheck` to `/health/ready` and, where a stack has
`depends_on: condition: service_healthy`, leave it — it now means "the schema is
applied", which is what it always intended.

- [ ] **Step 2: Add the TeaPie readiness case**

A case asserting `/health/ready` is 200 and `/health/live` is 200 on a booted
stack. TeaPie tests are **exercised in the pipeline**, not merely committed.

- [ ] **Step 3: Run the e2e**

Run: `scripts/test-e2e`
Expected: both stacks boot and pass. This is the acceptance criterion for A:553 —
if the container no longer reaches a working backend from a bare `docker compose
up`, the `Apply` setting in `appsettings.json` is wrong.

- [ ] **Step 4: Rewrite the docs of record**

- `host.md`: replace "The order in `BuildAsync` is load-bearing" with the five
  stages; rewrite `## Health` for the live/ready split, noting that `Degraded`
  would have been invisible; record the `Apply` setting and why.
- `data-api.md`: rewrite "Route generation happens at mapping time" — it now
  happens at *enumeration* time. **Correct the #103 table**: resolution A does not
  keep the OpenAPI document accurate for free (measured fact 6).
- `extensibility.md`: add `MapAlvo` to rule 10, and record that `Apply{Thing}`
  survives for the CLI/Management API even though no host calls it.

- [ ] **Step 5: Update the issues**

- **#132** → closed by Task 9.
- **#103** → narrowed: the lazy half is delivered; record measured fact 6 (the
  document does not refresh on invalidation) as its real remaining cost.
- **#133** → narrowed: `/health/ready` and its seam exist; the reachability port
  is still owed.
- **#141** → note the project-keyed boot state, so the next reader knows the door
  is closed but unlocked.

- [ ] **Step 6: ring2 + e2e + commit**

```bash
scripts/test-ring2 && scripts/test-e2e
git add -u docs/ docker-compose*.yml test/teapie/ examples/
git commit -m "docs(startup): record the boot lifecycle and correct the #103 claim"
```

---

## Before opening the PR

- [ ] `scripts/test-ring2` green; `scripts/test-e2e` green.
- [ ] Dispatch `alvo-plan-guard` (read-only, advisory).
- [ ] Dispatch reviewer subagents as substitutes for `/code-review high` and
      `/security-review` — say plainly in the PR body that they are substitutes.
- [ ] Invoke the `alvo-security-core-review` skill: the boot path decides when the
      policy catalog is primed, and an unprimed catalog denies everything, so the
      fail-closed direction must be re-argued rather than assumed.
- [ ] Dispatch `alvo-snapshot-judge` for every moved `*.verified.*` baseline.
- [ ] PR body carries the four **ratification** items from the design doc verbatim.

## Self-review of this plan

**Spec coverage.** Five stages → Tasks 2–4. Mode and defaults → Tasks 1, 3, 10.
Initialization exemption and convergence → Tasks 3, 5. Failure presentation and
#132 → Task 9. Disposal → Task 9 step 4. Readiness and deviation 38 → Task 7,
Task 10 step 2. Breaking `apply → map → listen`, and the OpenAPI and eager-refusal
traps → Task 6. DX surface → Task 8. Deviation 48 → Task 9. Not foreclosing #141 →
Task 4 (`AlvoBootState` keyed by project) and Task 11 step 5. Prior-art
constraints (empty data source registered at `Map` time; no `UseRouting` on the
host's behalf; change-token ordering) → Task 6 steps 3–5.

**Gap found and closed:** deviation 55 (the NuGet-version ↔ system-schema-version
upgrade contract) has **no task**, deliberately — the design defers it to #24.
Task 11 step 5 must therefore leave a note on #24, or the deferral is invisible.

**Type consistency.** `AlvoSchemaStartupMode` / `AlvoSchemaOptions` (Task 1) are
consumed by `SchemaStartupPolicy.Decide` (Task 3) and `AlvoBootService` (Task 4).
`BootPlan` (Task 2) is consumed by Task 4. `AlvoBootState` / `AlvoBootPhase`
(Task 4) are consumed by `AlvoSchemaHealthCheck` (Task 7). `AlvoHealth.ReadyTag`
and the two path constants (Task 7) are consumed by Tasks 8 and 11. `MapAlvoHealth`
+ `MapAlvoDataApi` (Tasks 6, 7) are composed by `MapAlvo` (Task 8). No name is
used before it is defined.
