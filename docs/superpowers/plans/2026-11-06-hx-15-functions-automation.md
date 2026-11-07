# HX-15 — Functions, automation runtime, secrets — Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** A descriptor can call host C# (`{"type":"function","name":"issue-invoice","input":{…}}` resolved to an `AddFunction<T>` registration, its input checked at apply), the `automation` block runs (event and cron triggers; `webhook`, `email`, `function`, `entity.update`), every declarative action — after-hook or automation — is its own retried delivery, `entity.update` writes exactly once per event, and a host can declare secrets it cannot start without.

**Architecture:** Nothing new runs beside the delivery engine. Each compiled after-hook action and each automation action becomes a **synthetic bound handler**, built per policy catalog by `ExtensionBindingCompiler` next to the host's handlers. Its subscriber id is the action's JSON pointer plus a content hash, so fan-out, claim, retry, lanes, dead-lettering and the Operations page treat it exactly like a host handler. The `OutboxDispatcher` stops running actions inline: it only fans out. `entity.update` claims a key in the existing command ledger and commits the key inside the write's own `TransactAsync` unit — the Idempotent Consumer pattern the command path already uses. Cron-triggered rules ride HX-13's schedule pump as trigger-only schedules.

**Tech Stack:** .NET 10 (C# 14), `System.Text.Json.Schema.JsonSchemaExporter`, Cronos (HX-13), Microsoft.Data.Sqlite, Npgsql (PostgreSQL 16 Testcontainers), xUnit v3 on MTP, Shouldly, `Microsoft.Extensions.TimeProvider.Testing`, Verify + PublicApiGenerator, Blazor/MudBlazor, Microsoft.Playwright, Astro/Starlight. No new package.

**Spec:** `docs/superpowers/specs/2026-10-14-host-extensibility-design.md` — §7 *HX-15* (scope, docs, dashboard, acceptance (1)–(7)); §4.11; §2.3 X12 (and C6); §4.8 (the `function`/`action` delivery kinds, *HX-03 settled details* (16) and the *Order trade-off until HX-15*); §4.13 + *HX-04 settled details* (1), (2), (17); §4.10 + *HX-13/HX-14 settled details* (schedules, steps, the delivery lease); §4.14 (logs never carry secrets); §5 row `15`; §8.1 (#328 closed; #33, #35, #120 advanced). Issue **#392** (epic #370; blocked-by #391).

Frozen artifacts read: `schema/project.schema.json` (`$defs/action`, `automationRule`, `eventPattern`, `cron`, `identifier`, `functions`, `webhooks`); Abstractions `Descriptor/{AutomationAction,AutomationRule,FunctionDescriptor,ValueOrExpr}.cs`, `Management/{ManagementExtension,AlvoExtensionKind,AlvoExtensionStage}.cs`, `Secrets/{ISecretStore,SecretName}.cs`, `Data/{ICommandLedger,IAlvoCommandUnit,IAlvoTransactions}.cs`; core `Events/Internal/{OutboxDispatcher,EventActionExecutor,EventSubscriptions,AfterHookCompiler,DeliveryFanOut,DeliveryRunner,DeliveryDispatcher,DeliveryVocabulary,ActionVocabulary}.cs`, `Extensions/Internal/{ExtensionBindingCompiler,ExtensionBindings,ExtensionRegistration,HandlerInvoker,ExtensionStateGuard,ExtensionApplyGuard,ExtensionCatalog}.cs`, `Scheduling/{SchedulePump,ScheduleRuns,JobSpec}.cs`, `Descriptor/Internal/{UnhonouredFeatures,UnhonouredSubsystems}.cs`, `Rules/Internal/PolicyCatalogBuilder.cs`, `Commands/Internal/CommandRunner.cs`; EF `EfCoreCommandLedger.cs`, `Internal/EfAlvoTransaction.cs`; `docs/product/baas-analyza.md` §2.7 (functions), §7.1 (secrets).

**Prior art** (CLAUDE.md *Study the problem*):
- **Idempotent Consumer** (Hohpe/Woolf *EIP*; Richardson *Microservices Patterns*, "transactional outbox + idempotent consumer"): record the message id in a processed-messages table **in the same transaction** as the consumer's own writes; a redelivery finds the id and skips. Alvo already has that table: the command ledger, committed inside a unit by `IAlvoCommandUnit.CommitCommandAsync` (HX-08). `entity.update` reuses it.
- **CloudEvents `id`** is the consumer's dedup key; Standard Webhooks tells receivers to dedupe on `webhook-id`. Webhook and email actions stay at-least-once and say so (unchanged from today).
- **JSON Schema 2020-12** `properties`/`required`/`type` — what `JsonSchemaExporter` emits for `TInput`, and the only keywords the apply check reads (Decision 4).
- **Cron, 5 fields, UTC** — the schema's `$defs/cron`; Cronos parses it exactly as HX-13's `AddSchedule` does.
- **Appwrite/Firebase function triggers** (analysis §2.7): an event trigger and a schedule trigger, async, retried, depth-limited recursion. Alvo's chain-depth limit (HX-01) is the recursion guard.

## Global Constraints

- Worktree `/Users/martiniak/Developer/GitHub/Burgyn/MMLib.Alvo-wt-hx15`, branch `hx/15-functions-automation`, off `origin/hx/14a-steps-engine` (HX-14 slice A, PR #462 pending; contains HX-12/13/16a-b). One writer per worktree; never `git add -A` (stage by path); `git branch --show-current` before every commit and the push; never `git clean`; use `/bin/ls` or `find`.
- **One PR for the whole of HX-15; two slices are commit groups, not PRs.** Slice A = Tasks 1–7 (engine), slice B = Tasks 8–10 (surfaces, docs). The PR targets `main`; if #462 has not merged when the PR opens, it states "lands after #462", and after #462 merges the branch is rebased onto `main` (never merge `main` in). This plan's own `docs(plan)` commit opens slice A and lands in the PR. PR body: `Closes #392`, `Closes #328` — and no `Closes`/`Fixes` keyword in any commit message.
- **Parallel work:** HX-16 and HX-18 are built at the same time. Shared files — the spec, `docs/PLAN.md`, `CHANGELOG.md`, `schema/project.schema.json`, `PublicApi.*.verified.txt`, `plugins/alvo/skills/**` (size-capped), `ManagementExtension.cs`, `AlvoExtensionKind.cs`, `DeliveryVocabulary.cs` — are edited small and **append-only**; on a rebase conflict keep both sides.
- **Enum values:** taken already `AlvoExtensionKind` `Narrowing = 7`, `Enricher = 8`, `Schedule = 9`, `DataMigration = 10`; `AlvoExtensionStage` `Read = 4`, `Scheduled = 5`. **HX-15 takes `AlvoExtensionKind.Function = 11` and no stage** (a function runs `PostCommit`); `12` and stage `6` stay free; HX-18 starts at `13` / `8`.
- **Public surface added is exactly** (each beyond §5 row `15` is a Decision):
  - Core `MMLib.Alvo.Functions` (new namespace): `IAlvoFunction<TInput>`, `AlvoFunctionContext`, `AlvoFunctionSkippedException`.
  - Core `MMLib.Alvo.Extensions.AlvoExtensionBuilderExtensions`: `AddFunction<TFunction>(string name)`, `AddRequiredSecret(string name)`.
  - Abstractions `MMLib.Alvo.Management`: `AlvoExtensionKind.Function = 11`; `ManagementExtension.InputSchema` (`JsonElement?`, init).
  - Abstractions `MMLib.Alvo.Descriptor`: `FunctionInput` (sealed record, `Transform`/`Fields`); `FunctionAction.Input` changes type `string?` → `FunctionInput?` (breaking, Decision 13).
  - Everything else is `internal`.
- **Wire constants (verbatim):**
  - Delivery kinds: `action` (webhook, email, entity.update), `function`. Dead reasons appended to `DeliveryErrors`: `record-not-found`, `write-refused`, `function-input-invalid`, `action-input-invalid`.
  - Subscriber id of a descriptor action: `<JSON pointer>#<8 lower-case hex of SHA-256>` — `/entities/orders/hooks/afterCreate/0#1f0c9a2e`, `/automation/notify-sales/actions/1#77ab03d4`; a cron rule's trigger-only schedule: `/automation/nightly-close#c01d5e9a`.
  - Cron rule event type: `alvo.automation.<rule name>`; subject `schedules/<subscriber id>`.
  - Ledger scope for `entity.update`: `alvo.action`; key `<subscriber id>/<event id "N">`; fingerprint = subscriber id.
- **Engine parity:** every data-path fact runs on SQLite and PostgreSQL — the new abstract `test/_shared/testhost/AutomationFacts.cs`, run as `SqliteAutomationTests` (`test/MMLib.Alvo.TestHost.Tests`) and `PostgreSqlAutomationTests` (`test/MMLib.Alvo.Data.PostgreSql.Tests.Integration`), one database per fact (the `StepFacts` pattern). **Every new PostgreSQL test class carries both guards** in its database helper: `Assert.SkipUnless(!OperatingSystem.IsWindows(), "PostgreSQL Testcontainers requires a Linux Docker daemon; unavailable on Windows-container runners.")` and `Assert.SkipWhen(string.IsNullOrEmpty(postgres.ConnectionString), "Docker is unavailable on this platform, so the PostgreSQL engine cannot be started.")`.
- **Secrets:** no secret value ever reaches a log, a problem document, an exception message, the catalog, a capability report or the dashboard. Only names. The apply path reads names only (`ListNamesAsync`); the boot check reads a value only to test it for emptiness and drops it. A store read failure fails **closed** (refuses boot) and reports the exception type, never its message.
- **Security core** (§6.2 lists HX-15: secrets, a system-context write path, tenant isolation of `entity.update`): the stack-level security review runs once after the stack, outside this plan; no per-PR `/security-review`. The PR keeps the `needs-deep-review` label.
- **Deterministic tests:** `FakeTimeProvider`; no `Task.Delay`/`Thread.Sleep`; races by ordering calls; webhook failures via a scripted `HttpMessageHandler` passed to `AlvoTestHost.StartAsync(…, webhooks:)`; email via a capturing `IEmailSender` registered in the test's services.
- C# style per `alvo-dotnet-conventions`: no inline comments; methods ≲ 25 lines; `ConfigureAwait(false)` in `src/` only; `TestContext.Current.CancellationToken` in every test call; XML docs on every public member citing no `§`/`#issue`. **`.cs` files are UTF-8 BOM + CRLF**: after editing run `dotnet format whitespace MMLib.Alvo.slnx --include <file> …`.
- **Schema or description change → `python3 scripts/gen-prototype-fixtures`** (Task 1, and Task 4 for the refused-slot table the prototype mirrors), then `python3 scripts/gen-prototype-fixtures --check`.
- **Testing per task:** affected test projects only (`dotnet test --project <proj> --filter-class '*<Class>'`) plus pre-commit's scoped ring0. **[slice end]** tasks: `scripts/test-ring1`, then `dotnet build MMLib.Alvo.slnx -c Release` (CI is Release). Task 10 also: `scripts/test-ring2`. Admin e2e only in Task 8, filtered (`scripts/test-admin-e2e --filter FunctionHook`, `--filter Automations`). No Stryker, no load run.
- **Public-API baselines:** `test/MMLib.Alvo.Abstractions.Tests` and `test/MMLib.Alvo.Tests`; accept a `*.received.txt` only when it adds exactly the symbols above; the turn-review gate then asks for `alvo-snapshot-judge` and an `alvo-architecture-rules` justification per symbol. The Management OpenAPI snapshot moves once (Task 2, `ManagementExtension.inputSchema`, `kind: function`).
- Commits: Conventional Commits, one per task, each ending `Claude-Session: https://claude.ai/code/session_01NJAtafM29iddLwfhb5gnmB`.

## Decisions (the spec does not settle these)

1. **Subscriber id = pointer + content hash** (SHA-256 over the canonical JSON of trigger, condition and action). An edit makes a new subscriber; the old one's pending rows are never claimed, never run the edited action, and do not refuse the boot (`ExtensionStateGuard` skips ids starting with `/`). Cost: stale `pending` rows after an edit, visible in Operations, until a follow-up sweeps them to `cancelled`.
2. **Actions run as synthetic `BoundHandler`s** with default settings: timeout 30 s, `PerRecord`, default retry, run as `System(event tenant)`; they are not catalog entries (HX-04 (2): the catalog lists registrations). Cost: no per-action tuning.
3. **`entity.update` is exactly-once through the command ledger** committed inside `TransactAsync`; horizon = the ledger's 1-day sweep. Cost: an operator redeliver of a *succeeded* `entity.update` more than a day later writes again.
4. **Function `input` is validated by top-level `properties`, `required` and the JSON `type` of literals only.** Cost: a nested typo dies at runtime as `function-input-invalid`.
5. **`$cel` values** in function input and `entity.update.payload` compile in the `Mutate` profile over the **triggering** row's images (`new.`/`old.` per point); the result type is checked against the target. Cost: no `@user`/`@tenant` there (the function reads `context.Event`).
6. **The JSONata string form of `input` stays refused** by the `JSONata` slot, fix "use the object form". X12 widens the schema additively; the build honours the object form only.
7. **`AlvoFunctionSkippedException`** ends the delivery `succeeded` with a `FunctionSkipped` log line and span outcome `skipped`; no store change. Cost: Operations shows it as succeeded.
8. **`secretRef`/`headersSecretRef`: grammar refused, existence warned.** A name `SecretName.TryParse` rejects is an apply error; a well-formed name absent from the store is a `Warning` finding (names only). Not refused: nothing reads it until #120 signs, and dashboard-first authors add the endpoint before the secret. Cost: breaking for a malformed name (CHANGELOG).
9. **Required-secret boot check reads each value** through `ISecretStore.GetAsync`; empty = missing; a throwing store refuses the boot. Runs on file and dashboard-first boots.
10. **Triggers honoured:** `entity.<entity>.<created|updated|deleted>` and a 5-field UTC cron. Refused: wildcard (existing slot), `.batch` and `delivery: batch` (new `batch` slot, #193), `auth.*`/`storage.*` (new `trigger.source` slot — no such event exists), a `condition` on a cron rule.
11. **A cron rule runs once globally** on a project without tenancy, **once per tenant** with tenancy; then apply refuses it when the host registers no `IAlvoTenantCatalog`.
12. **`entity.update` into a tenant-scoped entity from a trigger with no tenant is refused at apply;** a `recordId` not found in the event's tenant ends the delivery dead `record-not-found`.
13. **`FunctionAction.Input` becomes `FunctionInput?`** (pre-1.0 breaking; nothing outside the validator read it). `ManagementExtension.Secrets` is **not** added: no extension fills it (HX-04 (1)). `ManagementExtension.InputSchema` is added (§4.11's "catalog entry with an input JSON Schema").
14. **Dashboard:** the Automations page is read-only (edits go through the descriptor editor or the assistant); `/admin/functions` stays *Not yet* (csx, #34) and points at the hooks editor.

## Review Focus

1. **`entity.update` across tenants:** a tenant-A event naming a tenant-B `recordId` writes nothing and ends dead `record-not-found`, on both engines. Pinned by `AutomationFacts.An_entity_update_never_writes_across_tenants` (Task 6).
2. **A rule that re-triggers itself** (`entity.orders.updated` → `entity.update` of the same order) stops at the chain-depth limit with `chain-depth-exceeded`, never loops. Pinned by `AutomationFacts.An_entity_update_that_retriggers_its_rule_stops_at_the_depth_limit` (Task 6).
3. **Editing a hook with deliveries pending:** the edited action never runs for the old pending rows, and the next boot starts. Pinned by `AutomationFacts.Editing_a_hook_leaves_its_pending_deliveries_unclaimed_and_the_boot_starts` (Task 5).
4. **No secret value anywhere:** a boot refusal, an apply warning and every log line of a run with one present and one missing secret contain neither secret's value. Pinned by `RequiredSecretTests.No_secret_value_reaches_a_refusal_a_warning_or_a_log` (Task 7).
5. **A function input the type cannot bind** (a `$cel` value evaluating to null into a non-nullable property) is dead `function-input-invalid` after one attempt, and the log carries neither the input nor the row. Pinned by `AutomationFacts.A_function_input_the_type_cannot_bind_is_dead_without_its_values_in_the_log` (Task 5).

## File structure

**Abstractions:** `Descriptor/AutomationAction.cs` (`FunctionAction.Input`), `Descriptor/FunctionInput.cs` (new, with its converter), `Management/{AlvoExtensionKind,ManagementExtension}.cs`.

**Core:**
- `Functions/{IAlvoFunction,AlvoFunctionContext,AlvoFunctionSkippedException}.cs` (new); `Functions/Internal/{FunctionRegistration,FunctionInputSchema,FunctionInputCheck,FunctionInvocation}.cs` (new).
- `Extensions/AlvoExtensionBuilderExtensions.cs` (`AddFunction`, `AddRequiredSecret`); `Extensions/Internal/{ExtensionRegistration,ExtensionRegistry,ExtensionIdProblems,ExtensionBindingCompiler,ExtensionStateGuard,ExtensionCatalog,HandlerInvoker}.cs`; `Extensions/Internal/DescriptorActionBindings.cs` (new).
- `Rules/PolicyCatalog.cs` (`Automation`, `CompiledAction` members), `Rules/Internal/{PolicyCatalogBuilder,DescriptorHost}.cs` (`DescriptorHost` new).
- `Events/Internal/{AfterHookCompiler,ActionCompiler,AutomationCompiler,DescriptorActionIds,DescriptorActionRunner,EntityUpdateEffect,EventActionExecutor,OutboxDispatcher,DeliveryFanOut,DeliveryRunner,DeliveryVocabulary}.cs` (`ActionCompiler`, `AutomationCompiler`, `DescriptorActionIds`, `DescriptorActionRunner`, `EntityUpdateEffect` new); `EventSubscriptions.cs` deleted.
- `Scheduling/SchedulePump.cs` (sync per catalog).
- `Descriptor/Internal/{UnhonouredFeatures,UnhonouredSubsystems}.cs`; `Management/Internal/{CapabilityReport,AlvoManagementService}.cs`.
- `Secrets/Internal/{RequiredSecret,RequiredSecretsCheck,SecretReferenceCheck}.cs` (new); `Migrations/Internal/AlvoBootService.cs`.

**Schema:** `schema/project.schema.json` (`$defs/action` function `input`, descriptions), `test/MMLib.Alvo.Schema.Tests` fixtures, prototype fixtures.

**Tests:** `test/_shared/testhost/AutomationFacts.cs` (+ `SqliteAutomationTests`, `PostgreSqlAutomationTests`); `test/MMLib.Alvo.Tests/{Functions/*,Events/{ActionCompileTests,AutomationApplyTests,DescriptorActionBindingTests},Secrets/RequiredSecretTests}.cs`; `test/MMLib.Alvo.Host.Tests.Integration/KilledHostRecoveryTests.cs` (+ `ChildHostHarness` delivery rows).

**Admin (B):** `Components/Schema/{HookBuilder.cs,HooksTab.razor,FunctionInputRows.cs}`, `Components/Automations/{Automations.razor,AutomationRows.cs}` (new), `Components/Shell/{AdminSection.cs,NotYet.razor}`, `test/MMLib.Alvo.Admin.Tests/**`, `test/MMLib.Alvo.Admin.Tests.EndToEnd/{FunctionHookScenarios,AutomationsScenarios}.cs`.

**Docs (B):** `website/src/content/docs/extending/{functions.mdx,automation.mdx}`, `website/src/snippets/extending/{functions,automation}/*`, `website/astro.config.mjs`, `docs/architecture/automation.md` (new), `docs/architecture/events.md` (one link), `plugins/alvo/skills/{alvo-descriptor-hooks,alvo-descriptor-capabilities-and-limits}/SKILL.md`, `CHANGELOG.md`, the spec, `docs/PLAN.md`.

---

# Slice A — engine (Tasks 1–7)

## Task 1: X12 — the function `input` object form in the schema and the model

**Files:**
- Modify: `schema/project.schema.json`, `src/MMLib.Alvo.Abstractions/Descriptor/AutomationAction.cs`, `AlvoDescriptorJsonContext.cs` (register `FunctionInput` if the context lists types), `test/MMLib.Alvo.Schema.Tests/**` (valid/invalid fixtures per `alvo-schema-testing`), Abstractions baseline, prototype fixtures.
- Create: `src/MMLib.Alvo.Abstractions/Descriptor/FunctionInput.cs`.

**Interfaces — Produces** (namespace `MMLib.Alvo.Descriptor`):

```csharp
[JsonConverter(typeof(FunctionInputConverter))]
public sealed record FunctionInput
{
    /// <summary>The JSONata transformation, when the input is written as a string.</summary>
    public string? Transform { get; init; }
    /// <summary>The object form: input property to literal value or tagged CEL expression.</summary>
    public IReadOnlyDictionary<string, ValueOrExpr?>? Fields { get; init; }
}
// FunctionAction.Input: public FunctionInput? Input { get; init; }
```

`FunctionInputConverter.Read`: a JSON string → `Transform`; an object → `Fields`, each property read through `ValueOrExprConverter`, and a JSON `null` property value stored as `ValueOrExpr.FromLiteral(<JSON null>)` (the #326 rule, so no C# null reaches a compiler); any other token → `JsonException`. `Write` mirrors it.

**Schema** — `$defs/action`, the `function` branch:

```json
"input": {
  "description": "The function's input: an object of input property -> literal value or tagged {\"$cel\": \"...\"} expression (checked at apply against the function's input schema), or a JSONata transformation.",
  "oneOf": [
    { "$ref": "#/$defs/jsonata" },
    { "type": "object", "additionalProperties": { "$ref": "#/$defs/valueOrExpr" } }
  ]
}
```
and the `name` description: `"The name of a function the host registers (AddFunction)."`.

- [ ] **Step 1: Failing tests.** Schema: `FunctionActionSchemaTests.An_object_input_of_literals_and_cel_is_valid`, `.A_string_input_is_still_valid`, `.An_input_that_is_a_number_is_invalid`. Model: `FunctionInputJsonTests.An_object_input_round_trips`, `.A_string_input_round_trips`, `.A_null_input_property_reads_as_the_null_literal`.
- [ ] **Step 2: Run** `dotnet test --project test/MMLib.Alvo.Schema.Tests` and `--project test/MMLib.Alvo.Abstractions.Tests --filter-class '*FunctionInputJsonTests'`: FAIL.
- [ ] **Step 3: Implement** the schema branch, `FunctionInput` + converter, the property type change. Fix every compile error the type change causes (only `AfterHookCompiler`/validator read it; leave their behaviour — refusal of `function` — unchanged until Task 3).
- [ ] **Step 4: Run** the two classes, the Abstractions approval test (baseline: `FunctionInput` + the changed `Input` type only), `python3 scripts/gen-prototype-fixtures` then `--check`. PASS.
- [ ] **Step 5: Commit** — `feat(schema): function input takes the object form (X12)`.

## Task 2: `AddFunction<T>` — registration, input schema, catalog entry

**Files:**
- Create: `src/MMLib.Alvo/Functions/{IAlvoFunction.cs,AlvoFunctionContext.cs,AlvoFunctionSkippedException.cs}`, `Functions/Internal/{FunctionRegistration.cs,FunctionInputSchema.cs}`, `test/MMLib.Alvo.Tests/Functions/{AddFunctionTests,FunctionInputSchemaTests}.cs`.
- Modify: `Extensions/AlvoExtensionBuilderExtensions.cs`, `Extensions/Internal/{ExtensionRegistry,ExtensionIdProblems,ExtensionCatalog}.cs`, `Management/Internal/CapabilityReport.cs` (kind name `function`), Abstractions `Management/{AlvoExtensionKind,ManagementExtension}.cs`, both baselines, the Management OpenAPI snapshot.

**Interfaces — Produces:**

```csharp
namespace MMLib.Alvo.Functions;
public interface IAlvoFunction<in TInput> where TInput : class
{
    Task InvokeAsync(TInput input, AlvoFunctionContext context, CancellationToken cancellationToken);
}
public sealed class AlvoFunctionContext
{
    internal AlvoFunctionContext(string function, AlvoEvent @event, int attempt, IAlvoSession data, IAlvoSteps steps);
    public static AlvoFunctionContext Create(string function, AlvoEvent @event, IAlvoSession data, int attempt = 1); // pass-through steps
    public string Function { get; }      public AlvoEvent Event { get; }      public Guid EventId => Event.Id;
    public int Attempt { get; }          public IAlvoSession Data { get; }    public AlvoContext Caller => Data.Context;
    public IAlvoSteps Steps { get; }
}
public sealed class AlvoFunctionSkippedException : Exception   // the three standard constructors
// AlvoExtensionBuilderExtensions:
public static IAlvoBuilder AddFunction<TFunction>(this IAlvoBuilder builder, string name) where TFunction : class;

namespace MMLib.Alvo.Functions.Internal;
internal sealed record FunctionRegistration(string Id, int Sequence, Type Function, Type Input, FunctionInputSchema Schema)
    : ExtensionRegistration(Id, Sequence);
internal sealed record FunctionInputSchema(JsonElement Document, IReadOnlyDictionary<string, JsonTypes> Properties, IReadOnlySet<string> Required)
{
    internal static FunctionInputSchema For(Type input);
}
[Flags] internal enum JsonTypes { None = 0, String = 1, Number = 2, Integer = 4, Boolean = 8, Object = 16, Array = 32, Null = 64 }
// ExtensionRegistry: internal IReadOnlyList<FunctionRegistration> Functions { get; }   FunctionRegistration? Function(string name)
// Abstractions: AlvoExtensionKind.Function = 11;  ManagementExtension: public JsonElement? InputSchema { get; init; }
```

`AddFunction<TFunction>`: `ArgumentException` unless `TFunction` is concrete and implements exactly one `IAlvoFunction<>` (message names the type and what it implements); the name must match `^[a-z][a-z0-9_-]{0,62}$` (`$defs/identifier`). Registers `TFunction` as a **keyed transient** under `name` and a `FunctionRegistration` through `ExtensionRegistrations.Add`. Ids share the extension namespace (E3): `ExtensionIdProblems` reports a function name equal to any other registration's id (boot refusal, existing shape).

`FunctionInputSchema.For`:

```csharp
internal static FunctionInputSchema For(Type input)
{
    var node = JsonSchemaExporter.GetJsonSchemaAsNode(
        JsonSerializerOptions.Web, input, new JsonSchemaExporterOptions { TreatNullObliviousAsNonNullable = true });
    var document = JsonSerializer.SerializeToElement(node);
    return new(document, Properties(document), Required(document));
}
private static JsonTypes TypesOf(JsonElement property) =>
    !property.TryGetProperty("type", out var type) ? AllTypes
    : type.ValueKind is JsonValueKind.String ? Parse(type.GetString())
    : type.EnumerateArray().Aggregate(JsonTypes.None, (all, one) => all | Parse(one.GetString()));
```
(`Parse` maps the seven JSON Schema type names; `integer` also accepts a whole `number` literal; a property with no `type` accepts anything.)

`ExtensionCatalog` appends one `ManagementExtension` per function: `Kind = Function`, `Stage = PostCommit`, `Event = null`, `Entities = []`, `Operations = []`, `TypeName` per `ExposeTypeNames`, `InputSchema = Schema.Document`. `CapabilityReport` names the kind `function` (→ `extensions.function` under `Honoured`).

- [ ] **Step 1: Failing tests** (`AddFunctionTests`): `A_function_is_a_keyed_service_under_its_name`, `A_type_implementing_no_function_interface_is_refused`, `A_type_implementing_two_function_interfaces_is_refused`, `A_name_outside_the_identifier_grammar_is_refused`, `A_function_named_like_a_handler_refuses_the_boot`, `The_catalog_lists_the_function_with_its_input_schema`, `The_capability_report_honours_extensions_function`. (`FunctionInputSchemaTests`): `Required_and_optional_properties_are_read_in_camel_case`, `A_nullable_property_accepts_null`, `An_int_property_accepts_a_whole_number_only`.
- [ ] **Step 2: Run** `dotnet test --project test/MMLib.Alvo.Tests --filter-class '*AddFunctionTests'` (and `*FunctionInputSchemaTests`): FAIL.
- [ ] **Step 3: Implement.**
- [ ] **Step 4: Run** both classes, the extension catalog classes (`*ExtensionCatalogTests`), both approval tests (exactly the Task 2 symbols), `test/MMLib.Alvo.Api.Tests` OpenAPI snapshot. PASS.
- [ ] **Step 5: Commit** — `feat(functions): AddFunction registers a host function with its input schema`.

## Task 3: Function and `entity.update` actions compile — names, input, payload, #328

**Files:**
- Create: `src/MMLib.Alvo/Rules/Internal/DescriptorHost.cs`, `Events/Internal/ActionCompiler.cs` (the per-action compile moved out of `AfterHookCompiler`, shared with Task 4), `Functions/Internal/FunctionInputCheck.cs`, `test/MMLib.Alvo.Tests/Events/ActionCompileTests.cs`.
- Modify: `Rules/PolicyCatalog.cs` (`Build`/`TryBuild` internal overloads taking `DescriptorHost`; `CompiledAction` gains `Function`, `Fields`, `Target`, `RecordId`), `Rules/Internal/PolicyCatalogBuilder.cs`, `Events/Internal/AfterHookCompiler.cs`, `Descriptor/Internal/{DescriptorValidator,UnhonouredFeatures}.cs`, `Migrations/Internal/DescriptorBootPlan.cs`, `Migrations/RuntimeSchemaService.cs` (internal ctor overload), `Extensions/Internal/ExtensionApplyGuard.cs`, the DI factories; existing tests that pin `function`/`entity.update` as refused (`UnhonouredFeaturesTests`, `DescriptorValidatorTests`) are repointed to `http.call`.

**Interfaces — Produces:**

```csharp
namespace MMLib.Alvo.Rules.Internal;
/// <summary>The host facts a descriptor's actions compile against; Empty refuses every function action (fail closed).</summary>
internal sealed record DescriptorHost(
    IReadOnlyDictionary<string, FunctionRegistration> Functions, bool HasTenantCatalog, bool HasActionLedger)
{
    internal static DescriptorHost Empty { get; } = new(new Dictionary<string, FunctionRegistration>(), false, false);
    internal static DescriptorHost From(ExtensionRegistry registry, IServiceProviderIsService services); // ledger = ICommandLedger and IAlvoTransactions resolvable
}
// PolicyCatalog: internal static PolicyCatalog Build(AlvoDescriptor d, SchemaModel s, ICelCompiler c, DescriptorHost host);
//                internal static bool TryBuild(…, DescriptorHost host, out PolicyCatalog? catalog, out IReadOnlyList<DescriptorValidationError> errors);
internal sealed record CompiledAction(
    AutomationAction Action, IReadOnlyDictionary<string, AlvoTemplate> Templates, WebhookTarget? Endpoint, string TypeName)
{
    internal FunctionRegistration? Function { get; init; }
    internal IReadOnlyDictionary<string, CompiledValue> Fields { get; init; } = Empty;   // function input or entity.update payload
    internal string? Target { get; init; }                                               // entity.update target
    internal AlvoTemplate? RecordId { get; init; }
}
internal sealed record CompiledValue(JsonElement? Literal, CompiledExpression? Expression);
```

Every DI-built site passes `DescriptorHost.From(registry, services)`: `DescriptorValidator`, `DescriptorBootPlan`, `RuntimeSchemaService` (through the internal ctor overload the DI factory calls), `ExtensionApplyGuard`. The public parameterless-host overloads pass `DescriptorHost.Empty`.

**Function action rules** (path `…/action`; every error carries a fix):
- `name` not in `host.Functions` → `/name`: `'<name>' is not a function this host registers.` fix `Did you mean '<closest>'?` (`NameSuggestion.Closest`) or `Register it with AddFunction<T>("<name>"), or use one of: <names>.`; when the descriptor's `functions` block declares the name: fix `'<name>' is declared under 'functions', a csx script this build does not run; register a host function instead.`
- the name in **both** `host.Functions` and `descriptor.Functions` → `/functions/<name>` (reported once per name, by `PolicyCatalogBuilder`): `'<name>' is both a host function and a 'functions' entry, so a 'function' action naming it would be ambiguous.` fix `Rename the 'functions' entry, or remove it.`
- `input.Transform` set → the existing `RawJsonata` refusal at `/input`, fix suffixed `Write the input as an object of literals and {"$cel": "…"} values instead.`
- `input.Fields` → `FunctionInputCheck.Check` (below); each `$cel` value compiled in `CelProfile.Mutate` against the triggering entity's schema and point images.

```csharp
internal static IEnumerable<DescriptorValidationError> Check(
    string path, FunctionRegistration function, IReadOnlyDictionary<string, ValueOrExpr?> fields)
{
    var schema = function.Schema;
    foreach (var (name, value) in fields)
    {
        if (!schema.Properties.TryGetValue(name, out var types))
        {
            yield return Unknown(path, function.Id, name, NameSuggestion.Closest(name, schema.Properties.Keys), schema.Properties.Keys);
        }
        else if (value?.Literal is { } literal && !Accepts(types, literal))
        {
            yield return WrongType(path, function.Id, name, types, literal.ValueKind);
        }
    }

    foreach (var missing in schema.Required.Where(name => !fields.ContainsKey(name)).Order(StringComparer.Ordinal))
    {
        yield return Error($"{path}/input", $"Function '{function.Id}' requires '{missing}' in its input.", $"Add '{missing}' to 'input'.");
    }
}
```
Messages: unknown → at `{path}/input/{name}` `'<name>' is not an input of function '<id>'.` fix `Did you mean '<closest>'?` else `Its inputs are: <a, b, c>.`; wrong type → `'<name>' is <kind>, and function '<id>' takes <types> there.` fix `Write a <types> value, or a {"$cel": "…"} that yields one.`

**`entity.update` rules:** `entity` must be a physical entity of the descriptor (NameSuggestion); each `payload` key a writable field of the **target** (not managed, computed, rollup, `readOnly` or `sequence` — the before-hook `mutate` rule set, reused from `BeforeHookCompiler`); a literal checked against the target field's type; a **null entry is the null literal (#328)** — refused for a required field when `recordId` is absent (`Field '<f>' is required on '<target>', so a create cannot set it to null.`), allowed otherwise; `$cel` per Decision 5, its result type assignable to the target field; `recordId` a literal UUID or one `{{…}}` placeholder over the triggering row; a tenant-scoped target with a trigger whose entity is not tenant-scoped → refused (Decision 12); `!host.HasActionLedger` → `/type`: `This composition keeps no command ledger, so 'entity.update' cannot be written exactly once.` fix `Use a relational provider (UseSqlite, UsePostgreSql).`

`UnhonouredFeatures.EveryActionType` becomes `[HttpCallType]`; `FunctionType`/`EntityUpdateType` arms and constants are removed.

- [ ] **Step 1: Failing tests** (`ActionCompileTests`, all through `DescriptorValidator` with a host registering `issue-invoice(InvoiceInput{ string OrderId; decimal Amount; string? Note })`):
  - `An_input_typo_is_refused_with_the_closest_property_as_the_fix` (`"amout"` → fix `Did you mean 'amount'?`, pointer `/entities/orders/hooks/afterCreate/0/action/input/amout`) — **AC (1)**
  - `An_unregistered_function_name_gets_a_suggestion` (`issue-invoce`) — **AC (1)**
  - `A_name_both_registered_and_declared_under_functions_is_refused` — **AC (1)**
  - `A_missing_required_input_is_refused`, `A_literal_of_the_wrong_json_type_is_refused`, `A_cel_input_value_reading_old_on_create_is_refused`, `A_string_input_is_refused_as_jsonata`
  - `A_catalog_built_without_the_host_functions_refuses_a_function_action`
  - `An_entity_update_to_an_unknown_entity_gets_a_suggestion`, `An_entity_update_of_a_computed_field_is_refused`, `An_entity_update_payload_null_entry_is_refused_for_a_required_field_and_accepted_for_an_optional_one` (#328), `An_entity_update_into_a_tenant_scoped_entity_from_an_untenanted_trigger_is_refused`, `An_entity_update_without_a_ledger_is_refused`
  - `Http_call_is_still_refused_by_name`
- [ ] **Step 2: Run** `dotnet test --project test/MMLib.Alvo.Tests --filter-class '*ActionCompileTests'`: FAIL.
- [ ] **Step 3: Implement** `DescriptorHost`, `ActionCompiler` (webhook/email moved verbatim from `AfterHookCompiler`, plus the two new arms), `FunctionInputCheck`, the threading.
- [ ] **Step 4: Run** `ActionCompileTests`, `*UnhonouredFeaturesTests`, `*DescriptorValidatorTests`, `*AfterHook*`, `*ExtensionApplyGuardTests`. PASS. (Actions compile but still do not run: `EventActionExecutor` keeps its "unreachable" arm until Task 5.)
- [ ] **Step 5: Commit** — `feat(rules): function and entity.update actions compile against the host`.

## Task 4: The `automation` block compiles — triggers, refusals by name, honoured

**Files:**
- Create: `src/MMLib.Alvo/Events/Internal/AutomationCompiler.cs`, `test/MMLib.Alvo.Tests/Events/AutomationApplyTests.cs`.
- Modify: `Rules/PolicyCatalog.cs` (`Automation`), `Rules/Internal/PolicyCatalogBuilder.cs`, `Descriptor/Internal/{UnhonouredFeatures,UnhonouredSubsystems}.cs`, `Descriptor/DescriptorToSchemaMapper.cs` (the wildcard refusal stays; it now also refuses `.batch` via the new slot), `Management/Internal/CapabilityReport.cs` (`Honoured` gains `automation`), `UnhonouredSubsystemsTests`, `WildcardSubscriptionTests`, prototype fixtures.

**Interfaces — Produces:**

```csharp
internal sealed record CompiledAutomationRule(string Name, CompiledTrigger Trigger, CompiledExpression? Condition, RequiredContext Required,
    bool ReadsRoles, IReadOnlyList<CompiledRuleAction> Actions);
internal abstract record CompiledTrigger;
internal sealed record EventTrigger(string Entity, HandlerVerb Verb, string Point) : CompiledTrigger;   // Point = afterCreate|afterUpdate|afterDelete
internal sealed record CronTrigger(string Cron, string SubscriberId) : CompiledTrigger;               // UTC, TriggerOnly schedule id
internal sealed record CompiledRuleAction(string SubscriberId, CompiledAction Action);
// PolicyCatalog: internal IReadOnlyList<CompiledAutomationRule> Automation { get; }   (disabled rules are not compiled into it)
// UnhonouredFeatures: internal static UnhonouredSlot BatchDelivery { get; }    Feature "batch"
//                     internal static UnhonouredSlot NonEntityTrigger { get; } Feature "trigger.source"
```

Compile order per rule `r` (pointer `/automation/<r>`): `enabled: false` → skipped whole (validated, not compiled into `Automation`). `delivery: "batch"` → `BatchDelivery` at `/delivery`. Trigger: `event` → `EventPattern.HasWildcard` → `WildcardSubscription`; a `.batch` suffix → `BatchDelivery` at `/trigger/event`; prefix `auth`/`storage` → `NonEntityTrigger`; else entity must exist (NameSuggestion) and the operation be `created|updated|deleted` (`Did you mean 'updated'?`). `schedule` → `CronSchedule.TryParse(cron, "UTC", …)` (refusal carries Cronos' reason); with tenancy on and `!host.HasTenantCatalog` → `/trigger/schedule`: `A schedule rule on a tenanted project runs once per tenant, and this host registers no IAlvoTenantCatalog.` fix `Register an IAlvoTenantCatalog, or trigger the rule from an entity event.`; a `condition` → `/condition` refused (Decision 10). An event rule's `condition` compiles as an after-hook condition at the trigger's point. Each action compiles through `ActionCompiler` with the trigger entity as the triggering row (cron: an empty schema, so only literals, `now()` and `{{event.*}}` placeholders compile). Subscriber ids per Task 5's `DescriptorActionIds`.

The `batch` slot: consequence `Batch delivery is not implemented: one execution per affected row is the only delivery this build runs, and an array payload would silently become one call per row.` fix `Remove 'delivery' (perItem is the default), and trigger on the per-row event (entity.orders.created, not entity.orders.created.batch). Tracked in #193.` `trigger.source`: `This build emits only entity events, so an auth or storage trigger would never fire.` fix `Trigger on an entity event, or react in host code (an event handler) to what you need.`

`UnhonouredSubsystems.All` loses `automation`; `templates` now reads `a template's 'bodyFile' is not read`; `webhooks` reads `no delivery is signed — 'secretRef' is not read for signing and no Standard Webhooks HMAC header is sent, so a receiver cannot yet verify the sender (7.1), nor is the payload projected per endpoint (#152)`; `functions` reads `a csx script never runs; a 'function' action calls a host function registered with AddFunction instead`.

- [ ] **Step 1: Failing tests** (`AutomationApplyTests`):
  - `[Theory] Batch_delivery_http_call_and_a_wildcard_are_refused_by_name` — rows `{"delivery":"batch"}` → message contains `'batch'`; an `http.call` action → `'http.call'`; `entity.orders.*` → `wildcard` — **AC (5)**
  - `A_batch_event_pattern_is_refused_as_batch`, `An_auth_trigger_is_refused_as_trigger_source`, `An_unknown_trigger_entity_gets_a_suggestion`, `An_unknown_operation_gets_a_suggestion`, `A_malformed_cron_is_refused_with_its_reason`, `A_condition_on_a_cron_rule_is_refused`, `A_tenanted_cron_rule_without_a_tenant_catalog_is_refused`, `A_disabled_rule_is_validated_and_not_compiled`
  - `UnhonouredSubsystemsTests.Automation_is_no_longer_warned` (a descriptor with one rule → the warning does not name `automation`) — **AC (7)**; `CapabilityReportTests.Automation_is_honoured`.
- [ ] **Step 2: Run** `--filter-class '*AutomationApplyTests'` and `'*UnhonouredSubsystemsTests'`: FAIL.
- [ ] **Step 3: Implement**; then `python3 scripts/gen-prototype-fixtures` (refused/warned lists move) and `--check`.
- [ ] **Step 4: Run** the two classes, `*WildcardSubscriptionTests`, `*CapabilityReportTests`, `*DescriptorToSchemaMapperTests`, `test/MMLib.Alvo.Ai.Tests` (the skill gen regions: regenerate the capabilities skill's `gen:honoured`/`gen:warned`/`gen:refused` with the repo's regen command the drift test names). PASS.
- [ ] **Step 5: Commit** — `feat(automation): the automation block compiles; batch, http.call and wildcards refused by name`.

## Task 5: Every declarative action is its own delivery

**Files:**
- Create: `src/MMLib.Alvo/Events/Internal/{DescriptorActionIds,DescriptorActionRunner}.cs`, `Extensions/Internal/DescriptorActionBindings.cs`, `Functions/Internal/FunctionInvocation.cs`, `test/_shared/testhost/AutomationFacts.cs`, `test/MMLib.Alvo.TestHost.Tests/SqliteAutomationTests.cs`, `test/MMLib.Alvo.Data.PostgreSql.Tests.Integration/PostgreSqlAutomationTests.cs`, `test/_shared/testhost/descriptors/order-desk.alvo.json`, `test/MMLib.Alvo.Tests/Events/DescriptorActionBindingTests.cs`.
- Modify: `Extensions/Internal/{ExtensionRegistration,ExtensionBindingCompiler,ExtensionBindings,HandlerInvoker,ExtensionStateGuard}.cs`, `Events/Internal/{OutboxDispatcher,EventActionExecutor,DeliveryFanOut,DeliveryRunner,DeliveryVocabulary,AfterHookCompiler,AutomationCompiler}.cs`, `Scheduling/SchedulePump.cs`, `Events/Setup.cs`; delete `EventSubscriptions.cs` (its condition facts move into `DescriptorActionBindingTests`); `test/MMLib.Alvo.Host.Tests.Integration/{KilledHostRecoveryTests,ChildHostHarness}.cs`; every test pinning the inline path (`OutboxDispatcherTests`, `OutboxRecoveryTests`, `EventActionExecutorTests`, drain-result facts asserting hook paths).

**Interfaces — Produces:**

```csharp
// ExtensionRegistration.cs — HandlerRegistration gains two optional members:
internal sealed record HandlerRegistration(…, JobSpec? Job = null, string? Kind = null, bool TriggerOnly = false);
// DeliveryFanOut.Request uses: handler.Registration.Kind ?? handler.Registration.Job?.DeliveryKind ?? DeliveryKind.Handler
// ExtensionBindings.Selecting skips TriggerOnly registrations.
// DeliveryVocabulary: DeliveryKind.Action = "action", DeliveryKind.Function = "function";
//                     DeliveryErrors.RecordNotFound, WriteRefused, FunctionInputInvalid, ActionInputInvalid (values above)
internal sealed class ActionRefusedException(string reason) : Exception(reason) { internal string Reason => reason; } // → AttemptVerdict.Dead(reason)
// HandlerInvoker:
internal static HandlerInvoker ForAction(CompiledAction action);
// CompiledAfterHook gains: string SubscriberId
// EntityAfterHooks gains: internal IEnumerable<(HandlerVerb Verb, IReadOnlyList<CompiledAfterHook> Hooks)> ByVerb()  (afterCreate→Created, afterUpdate→Updated, afterDelete→Deleted)
```

**Subscriber ids** (one authority, used by `AfterHookCompiler` and `AutomationCompiler`):

```csharp
internal static class DescriptorActionIds
{
    internal static string For(string pointer, object identity) =>
        $"{pointer}#{Convert.ToHexStringLower(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(identity, DescriptorJson.Options)))[..8]}";

    internal static bool IsDescriptorAction(string subscriber) => subscriber.StartsWith('/');
}
// after-hook:  For(hook.Path, new { hook.Condition, Action = hook.Action.Action })   (computed in AfterHookCompiler from the source hook)
// rule action: For($"/automation/{rule}/actions/{i}", new { rule.Trigger, rule.Condition, Action = action })
// cron rule:   For($"/automation/{rule}", new { rule.Trigger })
```
(`DescriptorJson.Options` is a new internal static holding the exact `JsonSerializerOptions` the descriptor parser uses, so the polymorphic `type` discriminator is written and the hash is stable across processes.)

**Bindings** — appended to `ExtensionBindingCompiler.Compile` (not `Check`: they come from the catalog, never the registry):

```csharp
internal static IEnumerable<BoundHandler> Bind(PolicyCatalog catalog)
{
    foreach (var (entity, policy) in catalog.Entities)
    {
        foreach (var (verb, hooks) in policy.AfterHooks.ByVerb())
        {
            foreach (var hook in hooks)
            {
                yield return Bound(hook.SubscriberId, new EntityEvents([entity], verb), entity, hook.Condition, hook.Action);
            }
        }
    }

    foreach (var rule in catalog.Automation)
    {
        foreach (var bound in Bind(rule))
        {
            yield return bound;
        }
    }
}

private static BoundHandler Bound(string id, HandlerSource source, string? entity, CompiledExpression? condition, CompiledAction action)
{
    var registration = new HandlerRegistration(id, int.MaxValue, source, When: null, Defaults, HandlerInvoker.ForAction(action),
        Kind: action.Action is FunctionAction ? DeliveryKind.Function : DeliveryKind.Action);
    var bound = condition is null ? BoundCondition.Always : BoundCondition.Of(condition);
    var conditions = entity is null ? new Dictionary<string, BoundCondition>() : new() { [entity] = bound };
    return new BoundHandler(registration, conditions, bound);
}
```
A cron rule yields one `TriggerOnly` registration (`Source = NamedEvent("alvo.automation.<rule>")`, `Job = new JobSpec(DeliveryKind.Schedule, cron, "UTC", tenancy ? EachTenant : Global, null)`, a no-op invoker) plus one action registration per action subscribed to the same `NamedEvent`. `Defaults = HandlerSettings.From(new AlvoHandlerOptions())` (Decision 2).

**Runtime:**
- `OutboxDispatcher.DeliverAsync` fans out and marks dispatched — nothing else; `RunAsync`, `EventSubscriptions` and the executor dependency go. "Dispatched" means fanned out; `alvo.events.filtered` counts an event that fanned out to nothing.
- `DescriptorActionRunner.RunAsync(CompiledAction, AlvoEventContext, CancellationToken)` switches: webhook/email → `EventActionExecutor.ExecuteAsync(action, subscriberId, @event, ct)` (signature moves off `CompiledAfterHook`); function → `FunctionInvocation`; `entity.update` → `EntityUpdateEffect` (Task 6; until then it throws `NotSupportedException("Task 6")`).
- `FunctionInvocation.InvokeAsync`: evaluates `Fields` over the event's images (literal as is; `$cel` through the evaluator under `AlvoFlow.EnterInstant(@event.Time)`), builds a `JsonObject`, deserializes it to `registration.Input` with `JsonSerializerOptions.Web` + `RespectNullableAnnotations = true` + `RespectRequiredConstructorParameters = true`; a `JsonException` → `ActionRefusedException(FunctionInputInvalid)` and a log line carrying function name, delivery subscriber and event id only. Resolves the keyed `IAlvoFunction<TInput>` from the delivery's scope, builds `AlvoFunctionContext` (System(event tenant) session, the delivery's steps), awaits it; `AlvoFunctionSkippedException` → success + `FunctionSkipped` log. The generic call is made through one cached `MethodInfo.MakeGenericMethod` per registration.
- `DeliveryRunner.VerdictOf`: `ActionRefusedException` → `AttemptVerdict.Dead(reason)`.
- `ExtensionStateGuard.RefuseOrphansAsync` filters `DescriptorActionIds.IsDescriptorAction` out of the orphans.
- `SchedulePump`: `_synced` becomes `PolicyCatalog? _syncedFor`; `RoundAsync` syncs when `!ReferenceEquals(catalog, _syncedFor)`, so an apply that adds a cron rule schedules it without a restart.
- `ScheduleRuns.EventsAsync` mints `alvo.automation.<rule>`; confirm `AlvoCustomEvent` refuses the whole `alvo.` namespace (add `A_custom_event_cannot_be_named_alvo_automation` if it does not already hold).

- [ ] **Step 1: Failing facts** (`AutomationFacts`, both engines; `order-desk.alvo.json`: `orders` with an `afterCreate` webhook to `erp` and an `afterCreate` email, an automation rule `invoice-on-create` → `function issue-invoice`, a cron rule `nightly-close` → `entity.update`):
  - `A_failing_webhook_action_retries_alone_and_the_email_of_the_same_event_is_sent_once` — webhook handler answers 503 then 200; drain, advance 5 min, drain → 2 posts, 1 email, both deliveries `succeeded`, webhook attempts 2, email attempts 1 — **AC (2)**
  - `An_automation_rule_runs_its_function_with_the_checked_input`
  - `A_function_that_skips_ends_its_delivery_succeeded`
  - `A_function_input_the_type_cannot_bind_is_dead_without_its_values_in_the_log` (Review Focus 5)
  - `A_cron_rule_fires_its_actions_once_per_occurrence` (fake clock past 02:00 UTC; one `alvo.automation.nightly-close` event, one delivery per action, none for the trigger-only id)
  - `A_cron_rule_added_by_an_apply_fires_without_a_restart`
  - `Editing_a_hook_leaves_its_pending_deliveries_unclaimed_and_the_boot_starts` (Review Focus 3)
  - `DescriptorActionBindingTests`: the condition table of the deleted `EventSubscriptionsTests` (`@tenant.id`, `@user.roles`, `old.` on update, a throwing CEL function) now through fan-out — same verdicts; `A_subscriber_id_is_stable_across_processes_and_changes_with_the_action`.
- [ ] **Step 2: Run** `dotnet test --project test/MMLib.Alvo.TestHost.Tests --filter-class '*SqliteAutomationTests'` and `--project test/MMLib.Alvo.Tests --filter-class '*DescriptorActionBindingTests'`: FAIL.
- [ ] **Step 3: Implement** the bindings, the runner, the dispatcher change, the guard and pump changes.
- [ ] **Step 4: Rewrite `KilledHostRecoveryTests`** for the delivery shape — **AC (6)**: `ChildHostHarness` gains `DeliveryRows()` (id, subscriber, status, attempts, claimed_by, completed_at). Fact 1 additionally asserts the outbox row is dispatched after the restart and the delivery `succeeded`. Fact 2 asserts after the kill: the outbox row **is** dispatched (fan-out preceded the action), the delivery is `running` with `claimed_by` set and `completed_at` null; after the restart it waits for the lease (`WaitForDeliveriesAsync(count: 2, timeout: TimeSpan.FromMinutes(3))` — the action lease is 30 s + 30 s + 30 s) and the second post carries the same event id. Fix the inline-path tests listed under *Files* to the delivery shape (assertions, not behaviour, change).
- [ ] **Step 5: Run** both automation classes (`PostgreSqlAutomationTests` in `test/MMLib.Alvo.Data.PostgreSql.Tests.Integration`), `test/MMLib.Alvo.Tests`, `test/MMLib.Alvo.TestHost.Tests`, `test/MMLib.Alvo.Host.Tests`, `dotnet test --project test/MMLib.Alvo.Host.Tests.Integration --filter-class '*KilledHostRecoveryTests'`. PASS.
- [ ] **Step 6: Commit** — `feat(events): every declarative action runs as its own delivery`.

## Task 6: `entity.update` — exactly once per event, in the event's tenant

**Files:**
- Create: `src/MMLib.Alvo/Events/Internal/EntityUpdateEffect.cs`.
- Modify: `Events/Internal/DescriptorActionRunner.cs`, `Events/Setup.cs`, `test/_shared/testhost/AutomationFacts.cs`, `src/MMLib.Alvo.Data.EntityFrameworkCore/Internal/CommandLedgerTable.cs` only if `ReleaseAsync` can delete a committed key (it must be `… AND committed_at IS NULL`; pin with `CommandLedgerTests.Release_never_deletes_a_committed_key` if changed).

**Interfaces — Consumes:** `ICommandLedger`, `IAlvoTransactions`, `IAlvoCommandUnit.CommitCommandAsync` (HX-08), `CompiledAction.Target/Fields/RecordId` (Task 3). **Produces:** `internal sealed class EntityUpdateEffect`.

```csharp
internal sealed class EntityUpdateEffect(ICommandLedger? ledger, IAlvoTransactions? transactions, IPredicateEvaluator evaluator, TimeProvider time)
{
    internal const string Scope = "alvo.action";
    private static readonly CommandAnswer Applied = new(204, Body: null);

    internal async Task ApplyAsync(CompiledAction action, AlvoEventContext context, CancellationToken ct)
    {
        var key = new CommandKey(Scope, $"{context.Subscriber}/{context.EventId:N}", context.Subscriber);
        var claim = await Ledger.ClaimAsync(key, time.GetUtcNow(), ct).ConfigureAwait(false);
        if (claim.State is CommandClaimState.Answered or CommandClaimState.Unanswered)
        {
            return;
        }

        if (claim.State is not CommandClaimState.Claimed)
        {
            throw new InvalidOperationException($"Another attempt of '{context.Subscriber}' holds event {context.EventId}; retried later.");
        }

        await WriteOnceAsync(action, context, key with { Token = claim.Token }, ct).ConfigureAwait(false);
    }

    private async Task WriteOnceAsync(CompiledAction action, AlvoEventContext context, CommandKey key, CancellationToken ct)
    {
        try
        {
            await Transactions.TransactAsync(context.Caller, (unit, token) => WriteAsync(unit, action, context.Event, key, token), cancellationToken: ct)
                .ConfigureAwait(false);
        }
        catch (Exception failure)
        {
            await Ledger.ReleaseAsync(key, CancellationToken.None).ConfigureAwait(false);
            throw Classified(failure);
        }

        await Ledger.AnswerAsync(key, Applied, CancellationToken.None).ConfigureAwait(false);
    }

    private async Task WriteAsync(IAlvoTransaction unit, CompiledAction action, AlvoEvent @event, CommandKey key, CancellationToken ct)
    {
        var values = EntityUpdateValues.Evaluate(action, @event, evaluator);
        if (EntityUpdateValues.RecordId(action, @event) is { } id)
        {
            await unit.UpdateAsync(action.Target!, id, values, cancellationToken: ct).ConfigureAwait(false);
        }
        else
        {
            await unit.CreateAsync(action.Target!, values, ct).ConfigureAwait(false);
        }

        await ((IAlvoCommandUnit)unit).CommitCommandAsync(key, time.GetUtcNow(), ct).ConfigureAwait(false);
    }
}
```
`Ledger`/`Transactions` are properties that throw like `CommandRunner.Ledger` when the service is absent (apply already refused `entity.update` there). `Classified`: `AlvoRecordNotFoundException` → `ActionRefusedException(RecordNotFound)`; `AlvoConstraintViolationException` and the data port's payload refusal (whatever `AlvoExceptionHandler` maps to 422 `validation`) → `ActionRefusedException(WriteRefused)`; a `recordId` that renders to no UUID → `ActionRefusedException(ActionInputInvalid)` (thrown by `EntityUpdateValues.RecordId`); anything else rethrown (retry). `context.Caller` is `System(event tenant)` with the flow's provenance, so the write's own event is one hop deeper and the chain limit applies. `EntityUpdateValues` lives in the same file (evaluation mirrors `FunctionInvocation`'s).

- [ ] **Step 1: Failing facts** (`AutomationFacts`, both engines):
  - `An_entity_update_fires_once_per_event_under_redelivery` — the effect's write succeeds, then the attempt is made to fail after the commit (a test-only `IAlvoTransactions` decorator throwing after the inner `TransactAsync` returns) → the delivery retries; after the second drain the target has **one** write (`version` advanced once, one `entity.<target>.updated` event in the outbox); then an operator `redeliver` of the succeeded delivery changes nothing either — **AC (3)**
  - `An_entity_update_creates_a_record_when_record_id_is_absent`
  - `An_entity_update_payload_null_entry_writes_null_to_an_optional_field` (#328)
  - `An_entity_update_never_writes_across_tenants` (Review Focus 1)
  - `An_entity_update_that_retriggers_its_rule_stops_at_the_depth_limit` (Review Focus 2)
  - `An_entity_update_breaking_a_validation_is_dead_write_refused`
- [ ] **Step 2: Run** `--filter-class '*SqliteAutomationTests'`: FAIL.
- [ ] **Step 3: Implement**; register `EntityUpdateEffect` in `Events/Setup.cs`.
- [ ] **Step 4: Run** both automation classes, `*CommandLedger*` classes, `test/MMLib.Alvo.Data.Sqlite.Tests`. PASS.
- [ ] **Step 5: Commit** — `feat(automation): entity.update writes exactly once per event`.

## Task 7: Secrets — `AddRequiredSecret` refuses the boot; `secretRef` checked at apply **[slice end]**

**Files:**
- Create: `src/MMLib.Alvo/Secrets/Internal/{RequiredSecret,RequiredSecretsCheck,SecretReferenceCheck}.cs`, `test/MMLib.Alvo.Tests/Secrets/RequiredSecretTests.cs`, `test/MMLib.Alvo.Tests/Secrets/SecretReferenceTests.cs`.
- Modify: `Extensions/AlvoExtensionBuilderExtensions.cs` (`AddRequiredSecret`), `Migrations/Internal/AlvoBootService.cs`, `Management/Internal/AlvoManagementService.cs` (the warning), `Descriptor/Internal/DescriptorValidator.cs` (the grammar), core baseline; descriptors in tests/examples whose `secretRef` breaks the grammar (expected: none — `grep -rn '"secretRef"' examples test website/src` and check each).

**Interfaces — Produces:**

```csharp
public static IAlvoBuilder AddRequiredSecret(this IAlvoBuilder builder, string name);   // SecretName.Parse → ArgumentException; TryAddEnumerable(RequiredSecret)
internal sealed record RequiredSecret(SecretName Name);

internal sealed class RequiredSecretsCheck(IEnumerable<RequiredSecret> required, ISecretStore secrets)
{
    internal async Task RefuseAsync(CancellationToken ct)
    {
        var missing = new List<string>();
        foreach (var name in required.Select(secret => secret.Name).Distinct())
        {
            if (!await PresentAsync(name, ct).ConfigureAwait(false))
            {
                missing.Add(name.Value);
            }
        }

        if (missing.Count > 0)
        {
            throw Missing(missing);
        }
    }

    private async Task<bool> PresentAsync(SecretName name, CancellationToken ct)
    {
        try
        {
            return !string.IsNullOrEmpty(await secrets.GetAsync(name, ct).ConfigureAwait(false));
        }
        catch (Exception failure) when (failure is not OperationCanceledException)
        {
            throw Unreadable(name, failure.GetType().FullName ?? failure.GetType().Name);
        }
    }
}
```
`Missing` → `AlvoStartupRefusedException("Alvo cannot start: this host requires the secret(s) '<a>', '<b>', which the secret store does not hold.", "Set each one under Alvo:Secrets:Values:<name>, or in the dashboard under Settings → Secrets, then start again.")`. `Unreadable` → `"Alvo cannot start: the secret store failed (<type>) while checking the required secret '<name>', so it cannot be confirmed present."`, fix `Check the secret store's configuration and reachability.` Called first in `AlvoBootService.RefuseOrphanedWorkAsync`'s `try`, so it runs on every boot path that step runs on, and is recorded as `Failed` exactly like the orphan refusal.

`SecretReferenceCheck`:
- `Grammar(AlvoDescriptor)` → errors at `/webhooks/endpoints/<e>/secretRef` and at every `http.call` `…/action/headersSecretRef` whose value `SecretName.TryParse` rejects: `'<value>' is not a secret name.` fix `A secret name is lower case, starts with a letter, and has at most 64 letters, digits, dots, dashes and underscores — the name the secret is stored under, never the secret itself.` The value is quoted **only** when it parses as a name-shaped token of ≤ 64 chars with no whitespace; otherwise the message says `This value is not a secret name` without echoing it (it may be the secret pasted in the wrong place). Wired into `DescriptorValidator`.
- `MissingAsync(AlvoDescriptor, ISecretStore, ct)` → `Warning` findings (names only) for well-formed references absent from `ListNamesAsync`: `The secret '<name>' is not in the secret store yet. Deliveries are not signed in this build, so nothing fails today; signing will need it.` A throwing store yields one warning `The secret store could not be listed (<type>), so secret references were not checked.` Appended to `ManagementApplyResult.Warnings` in preview and apply.

- [ ] **Step 1: Failing tests** (`RequiredSecretTests`, `AlvoTestHost` with `Alvo:Secrets:Values:present-secret = "s3cr3t-value"`):
  - `A_missing_required_secret_refuses_boot` (message names `missing-secret`) — **AC (4)**
  - `A_present_required_secret_lets_the_boot_finish`, `An_empty_required_secret_refuses_boot`, `A_throwing_secret_store_refuses_boot_naming_its_exception_type`, `A_required_secret_name_outside_the_grammar_is_refused_at_registration`
  - `No_secret_value_reaches_a_refusal_a_warning_or_a_log` (Review Focus 4: a capturing `ILoggerProvider` — the `test/_shared/events/RecordingEventLog.cs` shape — over the boot and an apply; no message, exception text or warning contains `s3cr3t-value` or the pasted value)
  - (`SecretReferenceTests`) `A_secret_ref_outside_the_grammar_is_refused_at_apply`, `A_secret_pasted_as_a_secret_ref_is_not_echoed`, `A_headers_secret_ref_outside_the_grammar_is_refused`, `A_well_formed_secret_ref_absent_from_the_store_is_a_warning`, `A_listing_failure_is_one_warning_and_no_refusal`
- [ ] **Step 2: Run** `dotnet test --project test/MMLib.Alvo.Tests --filter-class '*RequiredSecretTests'` (and `*SecretReferenceTests`): FAIL.
- [ ] **Step 3: Implement.**
- [ ] **Step 4: Run** both classes, `*AlvoBootServiceTests`, `*AlvoManagementServiceTests`, `test/MMLib.Alvo.Admin.Tests` (Integrations drafts still accept their suggested names), the core approval test (`AddFunction`, `AddRequiredSecret` and the `Functions` types only). PASS.
- [ ] **Step 5: Slice end:** `scripts/test-ring1`; `dotnet build MMLib.Alvo.slnx -c Release`. Both green.
- [ ] **Step 6: Commit** — `feat(secrets): AddRequiredSecret refuses the boot; secret references are checked at apply`.

---

# Slice B — surfaces, docs (Tasks 8–10)

## Task 8: Dashboard — the hooks editor offers functions; Automations leaves *Not yet*

**Files:**
- Create: `src/MMLib.Alvo.Admin/Components/Schema/FunctionInputRows.cs`, `Components/Automations/{Automations.razor,AutomationRows.cs}`, `test/MMLib.Alvo.Admin.Tests/{Schema/FunctionInputRowsTests,Automations/AutomationRowsTests}.cs`, `test/MMLib.Alvo.Admin.Tests.EndToEnd/{FunctionHookScenarios,AutomationsScenarios}.cs`.
- Modify: `Components/Schema/{HookBuilder.cs,HooksTab.razor}`, `Components/Shell/{AdminSection.cs,NotYet.razor}` (route `/admin/automations` moves to `Automations.razor`; `NotYet` keeps `/admin/functions`), the e2e host's descriptor if it needs a host function (`EvalWorld`/e2e host registers `issue-invoice`).

**Interfaces — Produces:**
- `HookBuilder`: `public const string Function = "function";` offered in `Kinds` for after-points only; `public string FunctionName { get; set; }`; `public List<FunctionInputRow> InputRows { get; }`; `Build` writes `{"type":"function","name":…,"input":{…}}` (a row typed as CEL writes `{"$cel": …}`, an empty optional row is omitted). Functions come from `IAlvoManagement.GetExtensionsAsync` filtered `Kind == Function`.
- `FunctionInputRows.From(JsonElement inputSchema)` → one `FunctionInputRow(Name, Types, Required)` per property, required first then by name; the row's input placeholder names its JSON type; a required row empty → the builder's refusal `'<name>' is required by <function>.`
- `AutomationRows.From(JsonElement descriptor)` → `AutomationRow(Name, Enabled, Trigger, Condition, Actions)`; `Trigger` reads `When entity.orders.created` / `Every day at 02:00 UTC` (cron rendered by the existing cron wording helper used by the Operations schedules tile); each action reads its type and target. The page draws the list read-only, a *Deliveries* link per action to Operations filtered by the action's subscriber prefix `/automation/<rule>`, and the capability report's refusals for the `Automations` screen (unchanged component). An empty block draws `No automation rules. Add one in the descriptor, or ask the assistant.`
- `AdminSection`: Automations `NotYet: false`. `NotYet` (functions) "What is here instead": `A host function registered with AddFunction appears in an entity's hooks editor, under an after-hook's 'function' action, with a form for its input.`

- [ ] **Step 1: Failing unit tests:** `FunctionInputRowsTests.Required_rows_come_first`, `.A_nullable_property_is_optional`, `HookBuilderTests.A_function_hook_builds_its_input_object`, `.A_required_input_left_empty_is_refused`, `.Function_is_not_offered_before_a_write`, `AutomationRowsTests.An_event_rule_reads_its_trigger`, `.A_cron_rule_reads_in_words`, `.A_disabled_rule_is_marked`.
- [ ] **Step 2: Run** `dotnet test --project test/MMLib.Alvo.Admin.Tests --filter-class '*FunctionInputRowsTests'` (and the others): FAIL.
- [ ] **Step 3: Implement** the builder, rows, page, nav.
- [ ] **Step 4: Run** the Admin unit classes; `scripts/test-admin-e2e --filter FunctionHook` (add an `afterCreate` function hook through the form, apply, create a record, see the delivery in Operations) and `scripts/test-admin-e2e --filter Automations` (the page lists the seeded rule, no *Not yet* panel). PASS.
- [ ] **Step 5: Commit** — `feat(admin): function hooks with an input form; the automations page`.

## Task 9: Assistant — functions in the catalog, the skill region

**Files:** `plugins/alvo/skills/alvo-descriptor-hooks/SKILL.md`, `plugins/alvo/skills/alvo-descriptor-capabilities-and-limits/SKILL.md`, `src/MMLib.Alvo.Ai/**` only if `get_extensions`' answer drops `inputSchema`, `test/MMLib.Alvo.Ai.Tests/**`.

- [ ] **Step 1: Failing tests:** `GetExtensionsToolTests.A_function_is_listed_with_its_input_schema`; the skill drift tests fail once the capability lists moved (Task 4) — run them first to see which regions.
- [ ] **Step 2:** Hooks skill (**6 144-byte cap, at 6 144 now — net ≤ 0**): replace `The descriptor's \`automation\` does not run in this build.` and `(the \`function\` action is refused)` with one sentence each, and add a `gen:function-action` region (generated from a fixture like the other regions):

  ```
  After-hooks and `automation` rules may call host C#: `{"type":"function","name":"<registered>","input":{…}}`. Call
  `get_extensions` for the functions and their `inputSchema`; apply refuses an unknown name or input key with a fix.
  ```
  Pay for it by shortening the `gen:descriptor-or-code` prose's last sentence and the closing *In this repo* line. Capabilities skill: rewrite the *After-hook `webhook` and `email`, the one exception* paragraph to *Declarative actions*: `webhook`, `email`, `function` and `entity.update` run from after-hooks and `automation` (each action retried on its own; `entity.update` once per event); deliveries stay **unsigned**; `http.call`, `delivery: batch` and auth/storage triggers are refused. Drop the `automation` bullet from *What the missing parts are*.
- [ ] **Step 3: Run** `dotnet test --project test/MMLib.Alvo.Ai.Tests` (drift, size caps, region tests). PASS.
- [ ] **Step 4: Commit** — `feat(skills): the assistant proposes function actions and automation rules`.

## Task 10: Docs, architecture doc, CHANGELOG, spec, PLAN — then the PR **[slice end]**

**Files:**
- Create: `website/src/content/docs/extending/{functions.mdx,automation.mdx}`; `website/src/snippets/extending/functions/{IssueInvoice.cs,IssueInvoiceTests.cs,invoice-desk.alvo.json}`; `website/src/snippets/extending/automation/{AutomationTests.cs,order-desk.alvo.json}`; `docs/architecture/automation.md`.
- Modify: `website/astro.config.mjs` (two `page(…)` entries after *Multi-step side effects*), `docs/architecture/events.md` (one link line under the delivery section), `website/src/content/docs/guides/after-hooks-and-webhooks.mdx` (the "only webhook and email" sentence), `CHANGELOG.md`, the spec, `docs/PLAN.md`.

Content:
- *Functions in the descriptor* (`functions.mdx`): `IAlvoFunction<TInput>`; `AddFunction<IssueInvoice>("issue-invoice")`; the descriptor action with an object `input` mixing a literal and `{"$cel": "new.total"}`; what apply checks (and the typo refusal as the reader will see it); `AlvoFunctionContext` (`Steps` for a multi-call function, `Data` as system in the event's tenant); `AlvoFunctionSkippedException`; at-least-once — dedupe on `context.EventId`. Snippet test: `IssueInvoiceTests.The_descriptor_hook_invokes_the_function_once` through `AlvoTestHost`.
- *Automation* (`automation.mdx`): the block; triggers (entity events, UTC cron, per tenant on a tenanted project); the four actions; one delivery per action, retried alone; `entity.update` exactly once per event; what is refused by name (`batch`, `http.call`, wildcards, auth/storage triggers) and why; secrets: `secretRef` must be a name, deliveries are unsigned until #120 lands, `AddRequiredSecret` for what host code needs. Snippet test: `AutomationTests.A_failing_webhook_retries_alone`.
- `docs/architecture/automation.md`: the synthetic-binding design, the subscriber-id rule, the ledger pattern, the outbox now fan-out-only, the cron trigger-only schedule, Decisions 1–14 in one line each.
- CHANGELOG *Added*: functions, automation, `AddRequiredSecret`; *Changed (breaking)*: `FunctionAction.Input` type; `secretRef`/`headersSecretRef` must be secret names; the outbox no longer runs actions inline (dispatched = fanned out); after-hook deliveries appear in Operations. *Fixed*: #328.
- Spec: append **HX-15 settled details** under §7 *HX-15* — Decisions 1–14 verbatim, plus "as built" amendments; amend §5 row `15` with `ManagementExtension.InputSchema`, `AlvoExtensionKind.Function`, `FunctionInput`; mark the §4.8 *Order trade-off until HX-15* resolved.
- `docs/PLAN.md`: move `← YOU ARE HERE` past HX-15.

- [ ] **Step 1: Write** the pages and snippets; `dotnet test --project test/MMLib.Alvo.DocsSnippets.Tests` (and `*DocsSnippetTests` in `test/MMLib.Alvo.Tests`). PASS.
- [ ] **Step 2: Write** the architecture doc, CHANGELOG, spec amendment, PLAN. `scripts/check-brief-freshness` (the spec is not one of the brief's sources; confirm).
- [ ] **Step 3: Slice end:** `scripts/test-ring1`; `dotnet build MMLib.Alvo.slnx -c Release`; `scripts/test-ring2`; `cd website && npm run build`. All green.
- [ ] **Step 4: Commit** — `docs(automation): functions and automation pages, architecture doc, changelog, spec`.
- [ ] **Step 5: PR:** dispatch `alvo-plan-guard`; fix what it flags; build the report with `alvo-pr-report`; `gh pr create --base main` titled `feat: [HX-15] functions, automation runtime, secrets`, body = the report pointer, `Closes #392`, `Closes #328`, "lands after #462" if it is still open, label `needs-deep-review`.

## Acceptance criteria → tests

| AC | Test | Task |
|---|---|---|
| (1) `input` typo refused with a fix | `ActionCompileTests.An_input_typo_is_refused_with_the_closest_property_as_the_fix` | 3 |
| (1) unregistered name gets a suggestion | `ActionCompileTests.An_unregistered_function_name_gets_a_suggestion` | 3 |
| (1) name in host and `functions` refused | `ActionCompileTests.A_name_both_registered_and_declared_under_functions_is_refused` | 3 |
| (2) failing webhook retries alone, email not resent | `AutomationFacts.A_failing_webhook_action_retries_alone_and_the_email_of_the_same_event_is_sent_once` (SQLite + PostgreSQL) | 5 |
| (3) `entity.update` once per event under redelivery, both engines | `AutomationFacts.An_entity_update_fires_once_per_event_under_redelivery` (SQLite + PostgreSQL) | 6 |
| (4) missing required secret refuses boot | `RequiredSecretTests.A_missing_required_secret_refuses_boot` | 7 |
| (5) `batch`, `http.call`, wildcard refused by name | `AutomationApplyTests.Batch_delivery_http_call_and_a_wildcard_are_refused_by_name` | 4 |
| (6) KilledHostRecovery green | `KilledHostRecoveryTests` (both facts, rewritten for deliveries) | 5 |
| (7) `automation` no longer warned | `UnhonouredSubsystemsTests.Automation_is_no_longer_warned` | 4 |
| #328 null entry | `ActionCompileTests.An_entity_update_payload_null_entry_is_refused_for_a_required_field_and_accepted_for_an_optional_one`; `AutomationFacts.An_entity_update_payload_null_entry_writes_null_to_an_optional_field` | 3, 6 |
