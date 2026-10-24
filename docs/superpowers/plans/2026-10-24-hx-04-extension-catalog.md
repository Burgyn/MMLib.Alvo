# HX-04 — Extension catalog, apply guard, dashboard and assistant — Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Every event handler an embedded host registers is visible as metadata — through `IAlvoManagement.GetExtensionsAsync` / `GET {m}/projects/{p}/extensions`, the capability report, the policy simulator's verdict, a `⟨/⟩ n` badge and a read-only *After commit* lane on the dashboard, and the assistant's `get_extensions` tool — and no apply, dry run, rollback or `ApplyAlvoDescriptorAsync` can leave a registered handler unbound: removing an entity or field a handler binds, or excluding a field it reads via `events.exclude`, is refused naming the extension.

**Architecture:** An internal `ExtensionCatalog` projects HX-03's `ExtensionRegistry` into the new public Abstractions records `ManagementExtension` / `ManagementExtensionCatalog`; `AlvoManagementService` serves it on a Viewer route, appends `extensions.<kind>` to the capability report's `Honoured`, and fills `ManagementPolicyVerdict.Extensions`. An internal `ExtensionApplyGuard` runs HX-03's own `ExtensionBindingCompiler.Compile` against the *proposed* catalog wherever a catalog is accepted outside the boot (`RuntimeSchemaService.ApplyAsync`/`PreviewAsync`/`RollbackAsync`, `SchemaMigrationRunner.RunAsync`) and refuses a *change* that would unbind a handler bound to the current catalog — a `DescriptorValidationException` whose findings point at the removed entity, the removed field or the `events.exclude` item, beside the compiler's own sentence — while a handler that binds neither catalog is reported as a warning. The guard hands back the catalog it checked, so the apply publishes it without a second build and the binder's per-catalog cache compiles it once. The dashboard reads the catalog through `IAlvoManagement` only (a gateway slot, a pure `LifecycleLane` helper, a badge, a *Lifecycle* tab); the assistant gains one read tool, one `gen:` skill region and two eval cases.

**Tech Stack:** .NET 10, C#, xUnit v3 on Microsoft.Testing.Platform, Shouldly, NSubstitute, Verify + PublicApiGenerator, Blazor (server-interactive) + MudBlazor, Microsoft.Playwright (admin e2e), Microsoft.Extensions.AI (assistant tools), Astro/Starlight (docs).

**Spec:** `docs/superpowers/specs/2026-10-14-host-extensibility-design.md` — §7 *HX-04* (scope + acceptance (1)–(5)); §4.13 (catalog models, route, capability report, `ManagementPolicyVerdict.Extensions`, dashboard rows marked HX-04, assistant); §4.2 last paragraph (runtime apply and rollback refusal shape); §4.8 *HX-03 settled details* (6) (the apply guard closes the `unbound` gap; the dashboard-first `Awaiting` path), (7), (9), (32), (34); §4.7 *HX-01 settled details* (assistant awareness of `events.exclude` and of `@tenant.id`/`@user.roles` in after-hooks is HX-04's); §5 rows marked `04` (and `AlvoExtensionOptions.ExposeTypeNames`); §3.3 E7; Appendix A M26; §6.2 (HX-04 is **not** listed).

## Global Constraints

- Branch `hx/04-extension-catalog`, stacked on `hx/03-event-handlers` (PR #411, not yet merged; HX-01 and HX-02 are on `main`). Issue **#381**. One writer per worktree; never `git add -A` (stage each task's files by path); verify `git branch --show-current` prints `hx/04-extension-catalog` before every commit and before the push.
- **Scope is §7 HX-04 only.** No typed layer, `Entity<T>`, translator (HX-05); no `IAlvoOperations`, delivery history, pause, replay, `ManagementExtensionStats`, p95 or 24 h rejection counts (HX-06); no write hooks, validate/before/after-write kinds, *Before the transaction* / *In the transaction* lanes, hook-editor note or simulator rendering of `Extensions` (HX-09); no commands lane (HX-08); no reads lane (HX-16).
- **Public surface added is exactly:** Abstractions / `MMLib.Alvo.Management` — `ManagementExtension` (members `Id`, `Kind`, `Stage`, `Entities`, `Operations`, `Event`, `When`, `Order`, `Timeout`, `RunAs`, `Ordering`, `Description`, `TypeName`), `ManagementExtensionCatalog` (`Items`), `AlvoExtensionKind` (`EventHandler`), `AlvoExtensionStage` (`PostCommit`), `IAlvoManagement.GetExtensionsAsync(string project, CancellationToken ct = default)`, `ManagementPolicyVerdict.Extensions` (init, default `[]`); core / `MMLib.Alvo.Extensions` — `AlvoExtensionOptions.ExposeTypeNames`. Everything else is `internal`. `RuntimeSchemaService`'s public constructor is unchanged (the guard arrives through a new **internal** constructor overload). The `MMLib.Alvo.Testing` baseline (`*TestingLibraryPublicApiTests`) must not move.
- **Wire spellings (verbatim):** route `GET {m}/projects/{project}/extensions`, level `viewer`, operation `ManagementOperation.GetExtensions`; envelope `{ "items": [...] }`; `kind` `eventHandler`; `stage` `postCommit`; `operations` from `create`, `update`, `delete` (an `OnSaved` handler lists `create`, `update`; an `OnUpdated` handler's `update` covers a replace); `runAs` `system` | `eventActor`; `ordering` `perRecord` | `none`; `timeout` the `TimeSpan` (`"00:00:30"`); `entities` `["*"]` for an every-entity handler, `[]` for an application-event handler, whose `event` carries the event type; the capability report's `Honoured` gains `extensions.eventHandler` after the five blocks when at least one handler is registered; config key `Alvo:Extensions:ExposeTypeNames` (default `false`).
- **Guard rule (change-based, §4.2):** refuse an apply, dry run, rollback or `ApplyAlvoDescriptorAsync` only when a handler that binds `IPolicyCatalogProvider.Current` would not bind the proposed catalog. A handler already unbound (a dashboard-first host's first descriptor, a stored boot that ended `Failed`) blocks nothing; it is a `Warning` finding in the preview/dry-run result.
- **Refusal shape (verbatim):** `DescriptorValidationException` → `422`, problem type `validation`, violation code `descriptor`; on the wire a violation carries `pointer`, `code`, `message`, `fixSuggestion` (`AlvoViolation`). Pointed findings: `/entities/<e>` (entity removed), `/entities/<e>/fields/<f>` (field removed or renamed), `/entities/<e>/events/exclude/<i>` (field excluded); the compiler's own sentence for every problem travels beside them at `""`. Messages name the extension as `'<id>' (eventHandler, in this host)`; fixes are §4.2's `Remove the registration and redeploy first, or keep the field.` / `… or keep the entity.` and, for an exclusion, `… or keep the field in the event images.`
- **CI parity:** rings are Debug on the local SDK; CI builds Release on the newest `10.0` SDK with `TreatWarningsAsErrors`. Private helpers return arrays where an analyzer (CA1859) would flag an interface return, as HX-03's neighbours do; Task 13 runs the Release build in the CI SDK container before anything is pushed.
- **SQL text scan.** `SqlTextConfinedToRendererArchitectureTests` scans `src/MMLib.Alvo/{Expressions,Rules,Events}` and `src/MMLib.Alvo.Abstractions/{Expressions,Rules,Events}`: no file there (code, strings or XML docs) may contain `SELECT `, `WHERE `, `ILIKE`, `COALESCE(`, `IS NOT NULL`, `CASE WHEN `, ` IN (` or the whole upper-case words `AND`, `OR`, `NOT`. The one file this plan touches there is `src/MMLib.Alvo/Events/Internal/EventConditionReads.cs` (Task 4): keep its new doc comment in lower case.
- Public XML docs cite no `§` and no `#issue` (DocsGen's `RemarksFilterTests`). XML docs on every public member.
- C# style: `alvo-dotnet-conventions` — no inline comments, methods ≲ 25 lines, English only, `ConfigureAwait(false)` in `src/`, never in tests (xUnit1030); every test call passes `TestContext.Current.CancellationToken` (xUnit1051). `.cs` files are **UTF-8 with BOM + CRLF**: after creating or editing `.cs` files run `dotnet format whitespace MMLib.Alvo.slnx --include <file> <file> …` — `--include` takes **file paths**, never a directory (or run `dotnet format whitespace <project>.csproj` on the whole project). `.razor` files follow their neighbours (BOM + CRLF).
- Single-test runs: `dotnet test --project <test project> --filter-class '*<ClassName>'` (MTP). The public-API baseline class is `*PublicApiApprovalTests` (shared, compiled into `test/MMLib.Alvo.Tests`, `test/MMLib.Alvo.Abstractions.Tests`, `test/MMLib.Alvo.Ai.Tests`, …); the Testing library's is `*TestingLibraryPublicApiTests`. Rings: `scripts/test-ring0` after every task, `scripts/test-ring1` after each task marked **[slice end]**, `scripts/test-ring2` before the PR.
- A new or moved `*.verified.*` snapshot or a grown `PublicApi.*.verified.txt` trips the turn-review gate: dispatch `alvo-snapshot-judge`, and justify each added public symbol against `alvo-architecture-rules` (*public is the contract*) and the public-surface line above.
- `docs/design/f5-admin/generated/*.js` is derived from `ManagementEndpoints.cs` and `ManagementOperations.cs`: after Task 3 run `python3 scripts/gen-prototype-fixtures` and commit the result (`--check` runs inside the required **Build & test** check).
- Skill size caps (`SkillConformanceTests`): 6 144 bytes and 200 lines per `SKILL.md`; every `<!-- gen:… -->` region must be held to a source in `SkillRegionTests._heldHere` / `_heldInHostTests`.
- **Security core:** §6.2 does not list HX-04 — no `needs-deep-review`, no `alvo-security-core-review` checklist; the substitute code review (Task 13) checks the new route's gate and the guard's ordering explicitly.
- Commits: Conventional Commits, one per task, each message ending with `Claude-Session: https://claude.ai/code/session_01NJAtafM29iddLwfhb5gnmB`. No `Closes` keyword in a commit message; the PR body carries `Closes #381` once.

## Review Focus

1. **A rename of a bound field** (`renamedFrom` on a field a handler's `when` reads) must be refused like a removal, at the old field's pointer — not slip through because the column survives — test `Renaming_a_field_a_handler_reads_is_refused_at_the_old_field` (Task 4).
2. **A field retyped so the handler's condition no longer compiles** (enum → integer under `new.status == 'resolved'`) must be refused naming the extension, even though no precise pointer exists — test `A_retyped_field_the_condition_no_longer_compiles_against_is_refused_naming_the_extension` (Task 4).
3. **A dashboard-first host's first apply** (nothing primed, no stored revision) that a handler does not fit must be applied *with a warning naming the handler* — neither blocked (the host could never get a first descriptor in) nor silent — test `A_first_apply_a_handler_does_not_fit_is_applied_with_a_warning` (Task 5); and an already-unbound handler must not block an unrelated apply — `A_handler_already_unbound_does_not_block_an_unrelated_apply`.
4. **A stale `If-Match` on an unbinding apply** must answer the binding refusal (422), not the revision conflict, so the operator fixes the real problem first — test `The_binding_refusal_is_answered_before_the_revision_check` (Task 5).
5. **The Lifecycle lane at phone width** with a long `when` must not scroll the page sideways — test `The_lifecycle_tab_shows_code_after_the_commit_read_only` asserts `AssertNoHorizontalScrollAsync` at 375 px (Task 8).

---

## File structure

Created:

| File | Responsibility |
|---|---|
| `src/MMLib.Alvo.Abstractions/Management/ManagementExtension.cs`, `ManagementExtensionCatalog.cs`, `AlvoExtensionKind.cs`, `AlvoExtensionStage.cs` | public catalog records and enums |
| `src/MMLib.Alvo/Extensions/Internal/ExtensionCatalog.cs` | registry → catalog projection, simulator filter, honoured kinds |
| `src/MMLib.Alvo/Extensions/Internal/HandlerDescription.cs` | one handler registration → one `ManagementExtension` |
| `src/MMLib.Alvo/Extensions/Internal/ExtensionApplyGuard.cs` | `ExtensionApplyCheck`; refuses a change that would unbind a bound handler, warns for handlers bound by neither catalog |
| `src/MMLib.Alvo/Extensions/Internal/ExtensionApplyRefusals.cs` | binding problems → descriptor findings with precise pointers |
| `src/MMLib.Alvo.Admin/Components/Schema/LifecycleLane.cs` | pure lane/badge logic and words |
| `src/MMLib.Alvo.Admin/Components/Schema/LifecycleTab.razor` | the *Lifecycle* tab (After commit lane) |
| `test/MMLib.Alvo.Abstractions.Tests/Management/ManagementExtensionTests.cs` | wire form of the records |
| `test/MMLib.Alvo.Tests/Extensions/ExtensionCatalogTests.cs` (+ one `.verified.txt`) | projection, snapshot (acceptance (1)) |
| `test/MMLib.Alvo.Tests/Extensions/ExtensionApplyGuardTests.cs` | the guard's refusals |
| `test/MMLib.Alvo.Tests/Migrations/RuntimeSchemaServiceExtensionGuardTests.cs` | the guard on apply, preview, rollback |
| `test/MMLib.Alvo.Api.Tests/Management/ManagedExtensions.cs`, `ManagementExtensionsTests.cs` | HTTP: catalog, simulator, capabilities, apply refusal (acceptance (2)) |
| `test/MMLib.Alvo.Admin.Tests/Schema/LifecycleLaneTests.cs` | lane logic |
| `test/MMLib.Alvo.Admin.Tests.EndToEnd/ExtensionWorld.cs`, `ExtensionScenarios.cs` | badge, lane, preview refusal (acceptance (3)) |
| `eval/MMLib.Alvo.Ai.Eval/EvalExtensions.cs`, `EvalCases.Extensions.cs` | the eval host's handlers and the two cases |
| `test/MMLib.Alvo.Ai.Eval.Tests/ExtensionCasesTests.cs` | the graders (acceptance (4)) |
| `website/src/content/docs/extending/extension-catalog.mdx`; `website/src/snippets/extending/catalog/{FleetDeskExtensions.cs,FleetDeskExtensionsTests.cs,fleet-desk.alvo.json}` | *See your extensions in the dashboard* |

Modified: `src/MMLib.Alvo.Abstractions/Management/{IAlvoManagement.cs,ManagementModels.cs}`; `src/MMLib.Alvo/Extensions/{AlvoExtensionOptions.cs,Setup.cs}`, `Extensions/Internal/{HandlerInvoker.cs,ExtensionBinder.cs}`; `src/MMLib.Alvo/Events/Internal/EventConditionReads.cs`; `src/MMLib.Alvo/Management/{Setup.cs,Access/ManagementOperation.cs,Access/ManagementOperations.cs}`, `Management/Internal/{AlvoManagementService.cs,ManagementEndpoints.cs,CapabilityReport.cs}`; `src/MMLib.Alvo/Migrations/{RuntimeSchemaService.cs,SchemaMigrationRunner.cs,Internal/DescriptorBootPlan.cs}`; `src/MMLib.Alvo/AlvoServiceCollectionExtensions.cs`; `src/MMLib.Alvo.Admin/Internal/ManagementGateway.cs`, `Components/Schema/{EntityTabs.cs,Entity.razor,Entity.razor.cs,SchemaList.razor}`, `Components/Home/Overview.razor`; `src/MMLib.Alvo.Ai/Internal/{ManagementTools.cs,AssistantInstructions.cs}` (incl. `DescriptorView.HostExtensions`), `Instructions/schema-assistant.md`; `plugins/alvo/skills/{alvo-descriptor-hooks,alvo-descriptor-entities-and-fields,alvo-descriptor-capabilities-and-limits}/SKILL.md`; `eval/MMLib.Alvo.Ai.Eval/{EvalCases.cs,EvalWorld.cs}`; tests named per task; `docs/design/f5-admin/generated/*.js` (regenerated); docs per Task 12.

---

## Task 1: The catalog records and `ManagementPolicyVerdict.Extensions` (Abstractions)

**Files:**
- Create: `src/MMLib.Alvo.Abstractions/Management/AlvoExtensionKind.cs`, `AlvoExtensionStage.cs`, `ManagementExtension.cs`, `ManagementExtensionCatalog.cs`
- Modify: `src/MMLib.Alvo.Abstractions/Management/ManagementModels.cs` (`ManagementPolicyVerdict`)
- Test: `test/MMLib.Alvo.Abstractions.Tests/Management/ManagementExtensionTests.cs`
- Baseline: `test/MMLib.Alvo.Abstractions.Tests/PublicApi.MMLib.Alvo.Abstractions.verified.txt`

**Interfaces:**
- Produces: `public enum AlvoExtensionKind { EventHandler = 0 }` (wire `eventHandler`); `public enum AlvoExtensionStage { PostCommit = 0 }` (wire `postCommit`), both append-only; `public sealed record ManagementExtension { required string Id; required AlvoExtensionKind Kind; required AlvoExtensionStage Stage; IReadOnlyList<string> Entities = []; IReadOnlyList<string> Operations = []; string? Event; string? When; int Order; TimeSpan? Timeout; string? RunAs; string? Ordering; string? Description; string? TypeName }` (all `init`); `public sealed record ManagementExtensionCatalog { required IReadOnlyList<ManagementExtension> Items }`; `ManagementPolicyVerdict.Extensions` (`IReadOnlyList<ManagementExtension>`, init, default `[]`).

- [ ] **Step 1: Write the failing tests**

Create `test/MMLib.Alvo.Abstractions.Tests/Management/ManagementExtensionTests.cs` (match the namespace convention of a neighbouring test file in that project if it differs):

```csharp
using MMLib.Alvo.Management;

using System.Text.Json;
using System.Text.Json.Nodes;

namespace MMLib.Alvo.Abstractions.Tests.Management;

/// <summary>The extension catalog's wire form: camelCase members, string enums, lists that are never null.</summary>
public sealed class ManagementExtensionTests
{
    [Fact]
    public void An_extension_serialises_its_kind_and_stage_as_their_wire_names()
    {
        var wire = JsonSerializer.SerializeToNode(Minimal(), JsonSerializerOptions.Web)!;

        wire["kind"]!.GetValue<string>().ShouldBe("eventHandler");
        wire["stage"]!.GetValue<string>().ShouldBe("postCommit");
    }

    [Fact]
    public void An_extension_naming_no_entity_or_operation_carries_empty_lists_not_nulls()
    {
        var wire = JsonSerializer.SerializeToNode(Minimal(), JsonSerializerOptions.Web)!;

        wire["entities"].ShouldBeOfType<JsonArray>().ShouldBeEmpty();
        wire["operations"].ShouldBeOfType<JsonArray>().ShouldBeEmpty();
    }

    [Fact]
    public void A_timeout_travels_as_a_time_span_string()
    {
        var wire = JsonSerializer.SerializeToNode(Minimal() with { Timeout = TimeSpan.FromSeconds(30) }, JsonSerializerOptions.Web)!;

        wire["timeout"]!.GetValue<string>().ShouldBe("00:00:30");
    }

    [Fact]
    public void The_catalog_is_an_object_so_it_can_grow()
    {
        var wire = JsonSerializer.SerializeToNode(new ManagementExtensionCatalog { Items = [Minimal()] }, JsonSerializerOptions.Web)!;

        wire["items"].ShouldBeOfType<JsonArray>().ShouldHaveSingleItem();
    }

    [Fact]
    public void A_verdict_built_as_before_lists_no_extensions_and_still_equals_its_twin()
    {
        var verdict = new ManagementPolicyVerdict(true, null, "true", null, null, [], []);

        verdict.Extensions.ShouldBeEmpty();
        verdict.ShouldBe(new ManagementPolicyVerdict(true, null, "true", null, null, [], []));
    }

    [Fact]
    public void The_first_members_are_pinned_to_zero_so_appending_never_renumbers_them()
    {
        ((int)AlvoExtensionKind.EventHandler).ShouldBe(0);
        ((int)AlvoExtensionStage.PostCommit).ShouldBe(0);
    }

    private static ManagementExtension Minimal() => new()
    {
        Id = "ticket-resolved",
        Kind = AlvoExtensionKind.EventHandler,
        Stage = AlvoExtensionStage.PostCommit,
    };
}
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test --project test/MMLib.Alvo.Abstractions.Tests --filter-class '*ManagementExtensionTests'`
Expected: build FAIL — `ManagementExtension`, `AlvoExtensionKind`, `ManagementExtensionCatalog` and `ManagementPolicyVerdict.Extensions` do not exist.

- [ ] **Step 3: Write the records**

`src/MMLib.Alvo.Abstractions/Management/AlvoExtensionKind.cs`:

```csharp
using System.Text.Json.Serialization;

namespace MMLib.Alvo.Management;

/// <summary>What kind of host code an extension is.</summary>
/// <remarks>
/// Append only: a later version adds the kinds it learns to register after the existing members and never reorders or
/// renumbers them. A client should read an unknown value as opaque; a typed .NET client older than the server cannot —
/// <see cref="JsonStringEnumConverter{TEnum}"/> throws on a member it does not know — so such a client reads the raw
/// string when it must tolerate a newer server.
/// </remarks>
[JsonConverter(typeof(JsonStringEnumConverter<AlvoExtensionKind>))]
public enum AlvoExtensionKind
{
    /// <summary>An event handler: host C# run after a write commits, once per matching event, at least once.</summary>
    [JsonStringEnumMemberName("eventHandler")]
    EventHandler = 0,
}
```

`src/MMLib.Alvo.Abstractions/Management/AlvoExtensionStage.cs`:

```csharp
using System.Text.Json.Serialization;

namespace MMLib.Alvo.Management;

/// <summary>When, relative to a write, an extension runs.</summary>
/// <remarks>
/// Append only, never reordered or renumbered — the same rule, and the same caveat for an older typed client, as
/// <see cref="AlvoExtensionKind"/>.
/// </remarks>
[JsonConverter(typeof(JsonStringEnumConverter<AlvoExtensionStage>))]
public enum AlvoExtensionStage
{
    /// <summary>After the write's transaction has committed, from the outbox: it can never undo the write.</summary>
    [JsonStringEnumMemberName("postCommit")]
    PostCommit = 0,
}
```

`src/MMLib.Alvo.Abstractions/Management/ManagementExtension.cs`:

```csharp
namespace MMLib.Alvo.Management;

/// <summary>
/// One piece of host code registered with this instance, as metadata: what it is, what it binds to and when it runs.
/// Never the code itself, and never a way to change it.
/// </summary>
/// <remarks>
/// Init-only rather than positional, so a later version adds members without breaking a reader or a constructor call.
/// </remarks>
public sealed record ManagementExtension
{
    /// <summary>Gets the stable id the extension was registered under; its durable state is keyed by it.</summary>
    public required string Id { get; init; }

    /// <summary>Gets what kind of host code it is.</summary>
    public required AlvoExtensionKind Kind { get; init; }

    /// <summary>Gets when, relative to a write, it runs.</summary>
    public required AlvoExtensionStage Stage { get; init; }

    /// <summary>
    /// Gets the entities it binds: names from the descriptor, <c>*</c> alone for every entity, empty for an extension
    /// bound to an application event rather than to an entity.
    /// </summary>
    public IReadOnlyList<string> Entities { get; init; } = [];

    /// <summary>
    /// Gets the data operations it runs on, as the policy names them: <c>create</c>, <c>update</c> (a replace included),
    /// <c>delete</c>; empty for an extension bound to an application event.
    /// </summary>
    public IReadOnlyList<string> Operations { get; init; } = [];

    /// <summary>Gets the application or framework event type it handles, when it handles one rather than an entity's writes.</summary>
    public string? Event { get; init; }

    /// <summary>Gets its condition as CEL text, or <see langword="null"/> when it runs on every event it selects.</summary>
    public string? When { get; init; }

    /// <summary>Gets where it sits among the extensions of the same target; lower first, then registration order.</summary>
    public int Order { get; init; }

    /// <summary>Gets how long one run may take before it is cancelled and counted as failed.</summary>
    public TimeSpan? Timeout { get; init; }

    /// <summary>Gets who it acts as: <c>system</c> (the system in the event's tenant) or <c>eventActor</c>.</summary>
    public string? RunAs { get; init; }

    /// <summary>Gets whether one record's events reach it in order: <c>perRecord</c> or <c>none</c>.</summary>
    public string? Ordering { get; init; }

    /// <summary>Gets what it does, in the host developer's own sentence, when they wrote one.</summary>
    public string? Description { get; init; }

    /// <summary>
    /// Gets the .NET type that implements it, or <see langword="null"/> unless the host set
    /// <c>Alvo:Extensions:ExposeTypeNames</c>; a delegate is never named.
    /// </summary>
    public string? TypeName { get; init; }
}
```

`src/MMLib.Alvo.Abstractions/Management/ManagementExtensionCatalog.cs` (the `GetExtensionsAsync` member arrives in Task 3, so cite it as `<c>` here and switch to `<see cref>` there):

```csharp
namespace MMLib.Alvo.Management;

/// <summary>The answer of <c>IAlvoManagement.GetExtensionsAsync</c>: the host code registered with this instance.</summary>
/// <remarks>An envelope rather than a bare list, so it can grow additively.</remarks>
public sealed record ManagementExtensionCatalog
{
    /// <summary>Gets one entry per registered extension, by <c>Order</c> and then registration order.</summary>
    public required IReadOnlyList<ManagementExtension> Items { get; init; }
}
```

In `src/MMLib.Alvo.Abstractions/Management/ManagementModels.cs`, give the positional `ManagementPolicyVerdict` a body (keep its parameter list and its docs unchanged):

```csharp
public sealed record ManagementPolicyVerdict(
    bool Allowed,
    string? DenyReason,
    string? Using,
    string? WithCheck,
    string? TenantScope,
    IReadOnlyList<string> HiddenFields,
    IReadOnlyList<string> ReadOnlyFields)
{
    /// <summary>
    /// Gets the host extensions that also run on this entity and operation — after the write commits, they never change
    /// what the policy admits. Empty in a host that registers none.
    /// </summary>
    public IReadOnlyList<ManagementExtension> Extensions { get; init; } = [];
}
```

- [ ] **Step 4: Run the tests to verify they pass**

Run: `dotnet test --project test/MMLib.Alvo.Abstractions.Tests --filter-class '*ManagementExtensionTests'`
Expected: PASS (6).

- [ ] **Step 5: Approve the Abstractions baseline**

Run: `dotnet test --project test/MMLib.Alvo.Abstractions.Tests --filter-class '*PublicApiApprovalTests'`
Expected: FAIL with a `.received.txt` beside the baseline. Diff it: the only additions are the two enums (each with its `JsonConverter` and `JsonStringEnumMemberName` attributes), the two records with exactly the members above, and `public System.Collections.Generic.IReadOnlyList<MMLib.Alvo.Management.ManagementExtension> Extensions { get; init; }` on `ManagementPolicyVerdict`. Move the received file over `PublicApi.MMLib.Alvo.Abstractions.verified.txt`, re-run (PASS), and dispatch `alvo-snapshot-judge` on the baseline as the turn gate asks.

- [ ] **Step 6: Format, ring0, commit**

```bash
dotnet format whitespace MMLib.Alvo.slnx --include src/MMLib.Alvo.Abstractions/Management/AlvoExtensionKind.cs src/MMLib.Alvo.Abstractions/Management/AlvoExtensionStage.cs src/MMLib.Alvo.Abstractions/Management/ManagementExtension.cs src/MMLib.Alvo.Abstractions/Management/ManagementExtensionCatalog.cs src/MMLib.Alvo.Abstractions/Management/ManagementModels.cs test/MMLib.Alvo.Abstractions.Tests/Management/ManagementExtensionTests.cs
scripts/test-ring0
git add src/MMLib.Alvo.Abstractions/Management/AlvoExtensionKind.cs src/MMLib.Alvo.Abstractions/Management/AlvoExtensionStage.cs src/MMLib.Alvo.Abstractions/Management/ManagementExtension.cs src/MMLib.Alvo.Abstractions/Management/ManagementExtensionCatalog.cs src/MMLib.Alvo.Abstractions/Management/ManagementModels.cs test/MMLib.Alvo.Abstractions.Tests/Management/ManagementExtensionTests.cs test/MMLib.Alvo.Abstractions.Tests/PublicApi.MMLib.Alvo.Abstractions.verified.txt
git commit -m "feat(management): add the extension catalog records

Claude-Session: https://claude.ai/code/session_01NJAtafM29iddLwfhb5gnmB"
```

---

## Task 2: `ExtensionCatalog` — the registry as metadata, and `ExposeTypeNames`

**Files:**
- Create: `src/MMLib.Alvo/Extensions/Internal/ExtensionCatalog.cs`, `src/MMLib.Alvo/Extensions/Internal/HandlerDescription.cs`
- Modify: `src/MMLib.Alvo/Extensions/AlvoExtensionOptions.cs`, `src/MMLib.Alvo/Extensions/Internal/HandlerInvoker.cs`, `src/MMLib.Alvo/Extensions/Setup.cs`
- Test: `test/MMLib.Alvo.Tests/Extensions/ExtensionCatalogTests.cs`, `test/MMLib.Alvo.Tests/Extensions/AlvoExtensionOptionsTests.cs`
- Snapshot: `test/MMLib.Alvo.Tests/ExtensionCatalogTests.The_catalog_lists_entity_every_entity_and_application_handlers_in_order.verified.txt`
- Baseline: `test/MMLib.Alvo.Tests/PublicApi.MMLib.Alvo.verified.txt`

**Interfaces:**
- Consumes: Task 1's records; HX-03's `ExtensionRegistry.Handlers`, `HandlerRegistration(Id, Sequence, Source, When, Settings, Invoker)`, `EntityEvents(Entities, Verb)`, `EntityEvents.AllEntities`, `NamedEvent(Type)`, `HandlerVerb`, `HandlerSettings(Description, Order, OrderStated, Timeout, RunAs, Ordering, Retry)`.
- Produces: `internal sealed class ExtensionCatalog(ExtensionRegistry registry, IOptions<AlvoExtensionOptions> options)` with `IReadOnlyList<ManagementExtension> Items`, `ManagementExtensionCatalog Catalog()`, `IReadOnlyList<ManagementExtension> RunningOn(string entity, string operation)`, `IReadOnlyList<string> HonouredKinds`, `static string WireName(AlvoExtensionKind kind)`; registered singleton. `internal static class HandlerDescription { static ManagementExtension Of(HandlerRegistration handler, bool exposeTypeNames) }`. `HandlerInvoker.TypeName` (`string?`). `AlvoExtensionOptions.ExposeTypeNames` (`bool`, public).

- [ ] **Step 1: Write the failing tests**

Create `test/MMLib.Alvo.Tests/Extensions/ExtensionCatalogTests.cs`:

```csharp
using Microsoft.Extensions.Options;

using MMLib.Alvo.Data;
using MMLib.Alvo.Events;
using MMLib.Alvo.Extensions;
using MMLib.Alvo.Extensions.Internal;
using MMLib.Alvo.Management;

using System.Text.Encodings.Web;
using System.Text.Json;

using static MMLib.Alvo.Tests.Extensions.HandlerCatalog;

namespace MMLib.Alvo.Tests.Extensions;

/// <summary>The extension catalog (spec 2026-10-14 §4.13, HX-04 acceptance (1)): registrations as metadata, in order.</summary>
public sealed class ExtensionCatalogTests
{
    /// <summary>The wire form the Management API serves, relaxed-escaped so the snapshot reads as the CEL it carries.</summary>
    private static readonly JsonSerializerOptions _wire = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    [Fact]
    public Task The_catalog_lists_entity_every_entity_and_application_handlers_in_order() =>
        Verify(JsonSerializer.Serialize(Catalog(Registered()).Catalog(), _wire));

    [Fact]
    public void A_retired_id_is_not_an_extension()
        => Catalog(Registered()).Items.Select(item => item.Id).ShouldNotContain("old-sync");

    [Fact]
    public void A_class_handler_is_named_only_when_the_host_asks()
    {
        var registry = Registry(alvo => alvo
            .Entity("tickets", t => t.OnUpdated<Resolved>("by-class"))
            .Entity("notes", n => n.OnCreated("by-delegate", OnCreated)));

        Catalog(registry).Items.ShouldAllBe(item => item.TypeName == null);
        var named = Catalog(registry, exposeTypeNames: true).Items;
        named.Single(item => item.Id == "by-class").TypeName.ShouldBe(typeof(Resolved).FullName);
        named.Single(item => item.Id == "by-delegate").TypeName.ShouldBeNull("a delegate has no type worth naming");
    }

    [Fact]
    public void The_simulator_filter_keeps_what_runs_on_the_entity_and_operation()
    {
        var catalog = Catalog(Registered());

        catalog.RunningOn("tickets", "update").Select(item => item.Id).ShouldBe(["audit-every-save", "ticket-resolved"]);
        catalog.RunningOn("notes", "create").Select(item => item.Id).ShouldBe(["audit-every-save"]);
        catalog.RunningOn("tickets", "list").ShouldBeEmpty("no extension runs on a read in this build");
        catalog.RunningOn("tickets", "delete").ShouldBeEmpty();
    }

    [Fact]
    public void The_honoured_kinds_are_named_once_each_and_only_when_registered()
    {
        Catalog(Registered()).HonouredKinds.ShouldBe(["extensions.eventHandler"]);
        Catalog(ExtensionRegistry.Empty).HonouredKinds.ShouldBeEmpty();
    }

    [Fact]
    public void A_kind_s_wire_name_is_its_serialised_form()
        => ExtensionCatalog.WireName(AlvoExtensionKind.EventHandler).ShouldBe("eventHandler");

    private static ExtensionRegistry Registered() => Registry(alvo => alvo
        .Entity("tickets", tickets => tickets.OnUpdated(
            "ticket-resolved",
            OnUpdated,
            when: "changed(status) && new.status == 'resolved'",
            configure: options => options.Description = "Posts to the chat when a ticket is resolved."))
        .Entity("*", every => every.OnSaved(
            "audit-every-save",
            OnSaved,
            configure: options =>
            {
                options.Order = -1;
                options.Timeout = TimeSpan.FromSeconds(5);
            }))
        .AddEventHandler(
            "orders-approved",
            "orders.approved",
            OnRecord,
            configure: options =>
            {
                options.RunAs = AlvoRunAs.EventActor;
                options.Ordering = AlvoOrdering.None;
            })
        .AddRetiredExtension("old-sync", AlvoRetirement.Discard));

    private static ExtensionCatalog Catalog(ExtensionRegistry registry, bool exposeTypeNames = false) =>
        new(registry, Options.Create(new AlvoExtensionOptions { ExposeTypeNames = exposeTypeNames }));

    private sealed class Resolved : IAlvoEventHandler<AlvoUpdated<AlvoRecord>>
    {
        public Task HandleAsync(AlvoUpdated<AlvoRecord> message, AlvoEventContext context, CancellationToken cancellationToken) =>
            Task.CompletedTask;
    }
}
```

Match `HandleAsync`'s parameter names to `src/MMLib.Alvo/Events/IAlvoEventHandler.cs` (CA1725).

Create the expected snapshot `test/MMLib.Alvo.Tests/ExtensionCatalogTests.The_catalog_lists_entity_every_entity_and_application_handlers_in_order.verified.txt` with exactly this content (Verify's UTF-8 with BOM):

```json
{
  "items": [
    {
      "id": "audit-every-save",
      "kind": "eventHandler",
      "stage": "postCommit",
      "entities": [
        "*"
      ],
      "operations": [
        "create",
        "update"
      ],
      "event": null,
      "when": null,
      "order": -1,
      "timeout": "00:00:05",
      "runAs": "system",
      "ordering": "perRecord",
      "description": null,
      "typeName": null
    },
    {
      "id": "ticket-resolved",
      "kind": "eventHandler",
      "stage": "postCommit",
      "entities": [
        "tickets"
      ],
      "operations": [
        "update"
      ],
      "event": null,
      "when": "changed(status) && new.status == 'resolved'",
      "order": 0,
      "timeout": "00:00:30",
      "runAs": "system",
      "ordering": "perRecord",
      "description": "Posts to the chat when a ticket is resolved.",
      "typeName": null
    },
    {
      "id": "orders-approved",
      "kind": "eventHandler",
      "stage": "postCommit",
      "entities": [],
      "operations": [],
      "event": "orders.approved",
      "when": null,
      "order": 0,
      "timeout": "00:00:30",
      "runAs": "eventActor",
      "ordering": "none",
      "description": null,
      "typeName": null
    }
  ]
}
```

Add to `test/MMLib.Alvo.Tests/Extensions/AlvoExtensionOptionsTests.cs`:

```csharp
    [Fact]
    public void Type_names_stay_hidden_unless_the_section_exposes_them()
    {
        using var hidden = Container(new());
        using var exposed = Container(new() { ["Alvo:Extensions:ExposeTypeNames"] = "true" });

        hidden.GetRequiredService<IOptions<AlvoExtensionOptions>>().Value.ExposeTypeNames.ShouldBeFalse();
        exposed.GetRequiredService<IOptions<AlvoExtensionOptions>>().Value.ExposeTypeNames.ShouldBeTrue();
    }
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test --project test/MMLib.Alvo.Tests --filter-class '*ExtensionCatalogTests'`
Expected: build FAIL — `ExtensionCatalog` and `AlvoExtensionOptions.ExposeTypeNames` do not exist.

- [ ] **Step 3: Implement**

In `src/MMLib.Alvo/Extensions/AlvoExtensionOptions.cs` add:

```csharp
    /// <summary>
    /// Gets or sets whether the extension catalog names each class handler's .NET type; default <see langword="false"/>,
    /// because a type name tells every viewer of the catalog — and the schema assistant's model, which reads the catalog
    /// through its <c>get_extensions</c> tool — how the host's code is laid out. A delegate handler is never named.
    /// </summary>
    public bool ExposeTypeNames { get; set; }
```

In `src/MMLib.Alvo/Extensions/Internal/HandlerInvoker.cs`: add a `string? typeName` parameter to the private constructor (after `messageType`), store it in

```csharp
    /// <summary>The full name of the handler class, or <see langword="null"/> for a delegate, which has none worth naming.</summary>
    internal string? TypeName { get; }
```

and pass `typeName: null` from `FromDelegate` and `typeName: typeof(THandler).FullName` from `FromClass`.

`src/MMLib.Alvo/Extensions/Internal/HandlerDescription.cs`:

```csharp
using MMLib.Alvo.Management;

namespace MMLib.Alvo.Extensions.Internal;

/// <summary>One handler registration as the catalog describes it.</summary>
internal static class HandlerDescription
{
    internal static ManagementExtension Of(HandlerRegistration handler, bool exposeTypeNames) => new()
    {
        Id = handler.Id,
        Kind = AlvoExtensionKind.EventHandler,
        Stage = AlvoExtensionStage.PostCommit,
        Entities = handler.Source is EntityEvents entities ? entities.Entities : [],
        Operations = handler.Source is EntityEvents data ? Operations(data.Verb) : [],
        Event = handler.Source is NamedEvent named ? named.Type : null,
        When = handler.When,
        Order = handler.Settings.Order,
        Timeout = handler.Settings.Timeout,
        RunAs = handler.Settings.RunAs == AlvoRunAs.EventActor ? "eventActor" : "system",
        Ordering = handler.Settings.Ordering == AlvoOrdering.None ? "none" : "perRecord",
        Description = handler.Settings.Description,
        TypeName = exposeTypeNames ? handler.Invoker.TypeName : null,
    };

    private static string[] Operations(HandlerVerb verb) => verb switch
    {
        HandlerVerb.Created => ["create"],
        HandlerVerb.Updated => ["update"],
        HandlerVerb.Deleted => ["delete"],
        _ => ["create", "update"],
    };
}
```

`src/MMLib.Alvo/Extensions/Internal/ExtensionCatalog.cs`:

```csharp
using Microsoft.Extensions.Options;

using MMLib.Alvo.Management;

using System.Text.Json;

namespace MMLib.Alvo.Extensions.Internal;

/// <summary>
/// The host's registrations as metadata — what the Management API, the dashboard and the assistant read. Registrations,
/// not bindings: the apply guard keeps the descriptor from parting from them, so a registered handler is a bound one.
/// A retired id is no extension and is not listed.
/// </summary>
internal sealed class ExtensionCatalog(ExtensionRegistry registry, IOptions<AlvoExtensionOptions> options)
{
    private const string HonouredPrefix = "extensions.";

    private readonly Lazy<IReadOnlyList<ManagementExtension>> _items =
        new(() => Describe(registry, options.Value.ExposeTypeNames));

    /// <summary>Every extension, by <c>Order</c> and then registration order.</summary>
    internal IReadOnlyList<ManagementExtension> Items => _items.Value;

    /// <summary>The kinds this host registers, as the capability report lists them after its honoured blocks.</summary>
    internal IReadOnlyList<string> HonouredKinds =>
        [.. Items.Select(item => HonouredPrefix + WireName(item.Kind)).Distinct(StringComparer.Ordinal)];

    /// <summary>The catalog envelope the Management API answers with.</summary>
    internal ManagementExtensionCatalog Catalog() => new() { Items = Items };

    /// <summary>The extensions that run on <paramref name="operation"/> (a policy wire name) of <paramref name="entity"/>.</summary>
    internal IReadOnlyList<ManagementExtension> RunningOn(string entity, string operation) =>
        [.. Items.Where(item => Covers(item, entity) && item.Operations.Contains(operation, StringComparer.Ordinal))];

    /// <summary>A kind's wire name, read off its own serialisation so the two cannot disagree.</summary>
    internal static string WireName(AlvoExtensionKind kind) => JsonSerializer.Serialize(kind).Trim('"');

    private static bool Covers(ManagementExtension item, string entity) =>
        item.Entities.Contains(entity, StringComparer.Ordinal)
        || item.Entities.Contains(EntityEvents.AllEntities, StringComparer.Ordinal);

    private static IReadOnlyList<ManagementExtension> Describe(ExtensionRegistry registry, bool exposeTypeNames) =>
        [.. registry.Handlers
            .OrderBy(handler => handler.Settings.Order)
            .ThenBy(handler => handler.Sequence)
            .Select(handler => HandlerDescription.Of(handler, exposeTypeNames))];
}
```

In `src/MMLib.Alvo/Extensions/Setup.cs`, after `services.TryAddSingleton<ExtensionBinder>();` add `services.TryAddSingleton<ExtensionCatalog>();`, and name `ExtensionCatalog` in the type's summary.

- [ ] **Step 4: Run the tests to verify they pass**

Run: `dotnet test --project test/MMLib.Alvo.Tests --filter-class '*ExtensionCatalogTests'` then `--filter-class '*AlvoExtensionOptionsTests'`
Expected: PASS. If the snapshot fact fails, diff Verify's `.received.txt` against the block above line for line: a difference in member order, an enum written as a number or a missing `[]` is a defect in Step 3, not in the snapshot. Dispatch `alvo-snapshot-judge` for the new `.verified.txt`.

- [ ] **Step 5: Approve the core baseline**

Run: `dotnet test --project test/MMLib.Alvo.Tests --filter-class '*PublicApiApprovalTests'`
Expected: FAIL; the received diff adds exactly `public bool ExposeTypeNames { get; set; }` to `AlvoExtensionOptions`. Accept it, re-run (PASS), dispatch `alvo-snapshot-judge`. Then `--filter-class '*TestingLibraryPublicApiTests'` must PASS unchanged.

- [ ] **Step 6: Format, ring0, commit**

```bash
dotnet format whitespace MMLib.Alvo.slnx --include src/MMLib.Alvo/Extensions/AlvoExtensionOptions.cs src/MMLib.Alvo/Extensions/Internal/HandlerInvoker.cs src/MMLib.Alvo/Extensions/Internal/HandlerDescription.cs src/MMLib.Alvo/Extensions/Internal/ExtensionCatalog.cs src/MMLib.Alvo/Extensions/Setup.cs test/MMLib.Alvo.Tests/Extensions/ExtensionCatalogTests.cs test/MMLib.Alvo.Tests/Extensions/AlvoExtensionOptionsTests.cs
scripts/test-ring0
git add src/MMLib.Alvo/Extensions/AlvoExtensionOptions.cs src/MMLib.Alvo/Extensions/Internal/HandlerInvoker.cs src/MMLib.Alvo/Extensions/Internal/HandlerDescription.cs src/MMLib.Alvo/Extensions/Internal/ExtensionCatalog.cs src/MMLib.Alvo/Extensions/Setup.cs test/MMLib.Alvo.Tests/Extensions/ExtensionCatalogTests.cs test/MMLib.Alvo.Tests/Extensions/AlvoExtensionOptionsTests.cs test/MMLib.Alvo.Tests/ExtensionCatalogTests.The_catalog_lists_entity_every_entity_and_application_handlers_in_order.verified.txt test/MMLib.Alvo.Tests/PublicApi.MMLib.Alvo.verified.txt
git commit -m "feat(extensions): describe registered handlers as an extension catalog

Claude-Session: https://claude.ai/code/session_01NJAtafM29iddLwfhb5gnmB"
```

---

## Task 3: `GetExtensionsAsync`, the route, the simulator's `Extensions` and the capability report **[slice end]**

**Files:**
- Modify: `src/MMLib.Alvo.Abstractions/Management/IAlvoManagement.cs`, `ManagementExtensionCatalog.cs` (cref), `ManagementModels.cs` (`ManagementCapabilities.Honoured` doc)
- Modify: `src/MMLib.Alvo/Management/Access/ManagementOperation.cs`, `Access/ManagementOperations.cs`, `Management/Setup.cs`, `Management/Internal/AlvoManagementService.cs`, `Management/Internal/ManagementEndpoints.cs`, `Management/Internal/CapabilityReport.cs`
- Modify (implementers): `test/MMLib.Alvo.Admin.Tests.EndToEnd/ManagementDecorator.cs`, `test/MMLib.Alvo.Admin.Tests.EndToEnd/KeptFollowScenarios.cs` (`Holding`), `test/MMLib.Alvo.Api.Tests/Management/ManagementAccessTests.cs` (`RefusingManagement`)
- Modify: `test/MMLib.Alvo.Tests/Management/ManagementOperationsTests.cs`, `test/MMLib.Alvo.Api.Tests/Management/ManagementProjectNameTests.cs`
- Create: `test/MMLib.Alvo.Api.Tests/Management/ManagedExtensions.cs`, `test/MMLib.Alvo.Api.Tests/Management/ManagementExtensionsTests.cs`
- Regenerate: `docs/design/f5-admin/generated/*.js`
- Baseline: `test/MMLib.Alvo.Abstractions.Tests/PublicApi.MMLib.Alvo.Abstractions.verified.txt`

**Interfaces:**
- Consumes: Task 2's `ExtensionCatalog.Catalog()`, `RunningOn(entity, operation)`, `HonouredKinds`.
- Produces: `Task<ManagementExtensionCatalog> IAlvoManagement.GetExtensionsAsync(string project, CancellationToken ct = default)`; `ManagementOperation.GetExtensions` (Viewer); `internal static ManagementCapabilities CapabilityReport.Project(IReadOnlyList<string> registeredKinds)` beside the parameterless `Project()`; test world `ManagedExtensions.StartAsync(keys)` with `PlateChanged = "vehicle-plate-changed"`, `EveryWrite = "every-write-audited"`, `FleetAlerted = "fleet-alerted"`, `PlateChangedDescription`.

- [ ] **Step 1: Write the failing tests**

`test/MMLib.Alvo.Api.Tests/Management/ManagedExtensions.cs`:

```csharp
using Microsoft.Extensions.DependencyInjection;

using MMLib.Alvo.Data;
using MMLib.Alvo.Events;

namespace MMLib.Alvo.Api.Tests.Management;

/// <summary>
/// <see cref="ManagedFleet"/>'s project with three handlers registered as a host would: one on <c>vehicles</c> reading
/// <c>plate</c>, one on every entity, one on an application event.
/// </summary>
internal static class ManagedExtensions
{
    internal const string PlateChanged = "vehicle-plate-changed";
    internal const string EveryWrite = "every-write-audited";
    internal const string FleetAlerted = "fleet-alerted";
    internal const string PlateChangedDescription = "Re-registers the vehicle when its plate changes.";

    /// <summary>Starts the world; the boot binds the three handlers strictly, as a code-first host's does.</summary>
    /// <param name="keys">The dev API keys the world issues.</param>
    internal static Task<AlvoApiWorld> StartAsync(IReadOnlyList<TestApiKey> keys) =>
        AlvoApiWorld.FromDescriptorAsync(
            ManagedFleet.Descriptor,
            keys,
            new AlvoApiWorldSetup(MapBeforePriming: true, MapManagementApi: true, ConfigureServicesAfterAlvo: Register));

    private static void Register(IServiceCollection services) => new Builder(services)
        .Entity("vehicles", vehicles => vehicles.OnUpdated(
            PlateChanged,
            (AlvoUpdated<AlvoRecord> _) => Task.CompletedTask,
            when: "changed(plate)",
            configure: options => options.Description = PlateChangedDescription))
        .Entity("*", every => every.OnSaved(EveryWrite, (AlvoSaved<AlvoRecord> _) => Task.CompletedTask))
        .AddEventHandler(FleetAlerted, "fleet.alerted", (AlvoRecord _) => Task.CompletedTask);

    /// <summary>The builder a host holds; the registration verbs read only <see cref="Services"/>.</summary>
    private sealed class Builder(IServiceCollection services) : IAlvoBuilder
    {
        /// <inheritdoc/>
        public IServiceCollection Services { get; } = services;
    }
}
```

`test/MMLib.Alvo.Api.Tests/Management/ManagementExtensionsTests.cs` (Task 5 appends the apply facts):

```csharp
using System.Net;
using System.Text.Json.Nodes;

using static MMLib.Alvo.Api.Tests.Management.ManagedExtensions;

namespace MMLib.Alvo.Api.Tests.Management;

/// <summary><c>GET {m}/projects/{p}/extensions</c>, the simulator's <c>extensions</c> and the capability report's kinds.</summary>
public sealed class ManagementExtensionsTests
{
    private const string Extensions = ManagedFleet.Routes + "/extensions";

    private static readonly TestApiKey _ops = new("mgmt-ops", ["ops"], ["*:read"]);

    [Fact]
    public async Task A_viewer_reads_what_the_host_registered_in_order()
    {
        await using var world = await ManagedExtensions.StartAsync([_ops]);

        var items = await ItemsAsync(world);

        items.Select(item => item["id"]!.GetValue<string>()).ShouldBe([PlateChanged, EveryWrite, FleetAlerted]);
        var plate = items[0];
        plate["kind"]!.GetValue<string>().ShouldBe("eventHandler");
        plate["stage"]!.GetValue<string>().ShouldBe("postCommit");
        Strings(plate["entities"]).ShouldBe(["vehicles"]);
        Strings(plate["operations"]).ShouldBe(["update"]);
        plate["when"]!.GetValue<string>().ShouldBe("changed(plate)");
        plate["timeout"]!.GetValue<string>().ShouldBe("00:00:30");
        plate["description"]!.GetValue<string>().ShouldBe(PlateChangedDescription);
        plate["typeName"].ShouldBeNull("a type name is served only when Alvo:Extensions:ExposeTypeNames says so");
        Strings(items[1]["entities"]).ShouldBe(["*"]);
        items[2]["event"]!.GetValue<string>().ShouldBe("fleet.alerted");
    }

    [Fact]
    public async Task A_host_that_registers_nothing_answers_an_empty_catalog()
    {
        await using var world = await ManagedFleet.StartAsync([_ops]);

        (await ItemsAsync(world)).ShouldBeEmpty();
    }

    [Fact]
    public async Task An_unknown_project_is_a_404()
    {
        await using var world = await ManagedExtensions.StartAsync([_ops]);

        using var response = await world.SendAsync(HttpMethod.Get, "/management/projects/nope/extensions", _ops);

        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task An_anonymous_caller_is_refused_before_the_project_is_looked_up()
    {
        await using var world = await ManagedExtensions.StartAsync([_ops]);

        using var existing = await world.SendAsync(HttpMethod.Get, Extensions);
        using var missing = await world.SendAsync(HttpMethod.Get, "/management/projects/nope/extensions");

        existing.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        missing.StatusCode.ShouldBe(HttpStatusCode.Forbidden, "authorization is decided before the project is looked up");
    }

    [Fact]
    public async Task A_simulation_lists_the_extensions_that_run_on_its_entity_and_operation()
    {
        await using var world = await ManagedExtensions.StartAsync([_ops]);

        (await SimulatedAsync(world, "vehicles", "update")).ShouldBe([PlateChanged, EveryWrite]);
        (await SimulatedAsync(world, "depots", "create")).ShouldBe([EveryWrite]);
        (await SimulatedAsync(world, "vehicles", "list")).ShouldBeEmpty("no extension runs on a read in this build");
    }

    [Fact]
    public async Task The_capability_report_honours_the_kind_the_host_registers_after_the_blocks()
    {
        await using var registering = await ManagedExtensions.StartAsync([_ops]);
        await using var plain = await ManagedFleet.StartAsync([_ops]);

        (await HonouredAsync(registering)).ShouldBe(["entities", "auth", "tenancy", "access", "formats", "extensions.eventHandler"]);
        (await HonouredAsync(plain)).ShouldBe(["entities", "auth", "tenancy", "access", "formats"]);
    }

    private static async Task<IReadOnlyList<JsonObject>> ItemsAsync(AlvoApiWorld world)
    {
        var body = await (await world.SendAsync(HttpMethod.Get, Extensions, _ops)).ReadJsonObjectAsync();

        return [.. body["items"].ShouldBeOfType<JsonArray>().Select(item => item!.AsObject())];
    }

    private static async Task<IReadOnlyList<string>> SimulatedAsync(AlvoApiWorld world, string entity, string operation)
    {
        var body = new JsonObject
        {
            ["entity"] = entity,
            ["operation"] = operation,
            ["caller"] = new JsonObject { ["user"] = Guid.NewGuid().ToString(), ["roles"] = new JsonArray("dispatcher") },
        };
        var verdict = await (await world.SendAsync(HttpMethod.Post, $"{ManagedFleet.Routes}/policy/simulate", _ops, body: body))
            .ReadJsonObjectAsync();

        return [.. verdict["extensions"].ShouldBeOfType<JsonArray>().Select(item => item!["id"]!.GetValue<string>())];
    }

    private static async Task<IReadOnlyList<string>> HonouredAsync(AlvoApiWorld world) =>
        Strings((await (await world.SendAsync(HttpMethod.Get, $"{ManagedFleet.Routes}/capabilities", _ops)).ReadJsonObjectAsync())["honoured"]);

    private static IReadOnlyList<string> Strings(JsonNode? array) =>
        [.. array.ShouldBeOfType<JsonArray>().Select(item => item!.GetValue<string>())];
}
```

In `test/MMLib.Alvo.Tests/Management/ManagementOperationsTests.cs`, `A_viewer_may_read_everything_including_the_simulator`: insert `ManagementOperation.GetExtensions,` between `ManagementOperation.GetCelFunctions,` and `ManagementOperation.GetInfo,`. In `test/MMLib.Alvo.Api.Tests/Management/ManagementProjectNameTests.cs`, add `[InlineData("extensions")]` after `[InlineData("capabilities")]`.

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test --project test/MMLib.Alvo.Api.Tests --filter-class '*ManagementExtensionsTests'`
Expected: FAIL — the route answers 404 and the verdict carries no `extensions` (and `test/MMLib.Alvo.Tests` does not compile: `ManagementOperation.GetExtensions` is missing).

- [ ] **Step 3: Add the member, the operation and the route**

`src/MMLib.Alvo.Abstractions/Management/IAlvoManagement.cs`, after `GetCelFunctionsAsync`:

```csharp
    /// <summary>
    /// The host code registered with this instance — its extensions — as metadata: what each one is, what it binds to
    /// and when it runs. Never the code, and never a way to change it.
    /// </summary>
    /// <remarks>
    /// The list is the instance's, like <see cref="GetCelFunctionsAsync"/>'s: an extension exists only in the host that
    /// registered it, and the standalone image registers none. An apply that would remove an entity or field one of them
    /// binds, or leave a field it reads out of the event images, is refused naming it — read this before proposing such a
    /// change.
    /// </remarks>
    /// <param name="project">The project name.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The extensions, by order and then registration order, in an envelope that can grow additively.</returns>
    /// <exception cref="ManagementForbiddenException">The caller does not reach this operation's level.</exception>
    /// <exception cref="ManagementProjectNotFoundException">This instance does not serve that project.</exception>
    Task<ManagementExtensionCatalog> GetExtensionsAsync(string project, CancellationToken ct = default);
```

Switch `ManagementExtensionCatalog.cs`'s summary to `<see cref="IAlvoManagement.GetExtensionsAsync"/>`. In `ManagementModels.cs`, `ManagementCapabilities`' `Honoured` param doc becomes: "The top-level blocks this build honours, followed — in an embedded host — by `extensions.<kind>` for each kind of host code it registers."

`src/MMLib.Alvo/Management/Access/ManagementOperation.cs`, after `GetCelFunctions,`:

```csharp
    /// <summary>Read the host code registered with this instance, as metadata.</summary>
    GetExtensions,
```

`src/MMLib.Alvo/Management/Access/ManagementOperations.cs`: `[ManagementOperation.GetExtensions] = ManagementLevel.Viewer,` after the `GetCelFunctions` row.

`src/MMLib.Alvo/Management/Internal/ManagementEndpoints.cs`: call `MapExtensions(group);` right after `MapCelFunctions(group);` in `Map`, and add after `MapCelFunctions`:

```csharp
    /// <summary>
    /// <c>GET {prefix}/projects/{project}/extensions</c> — <see cref="IAlvoManagement.GetExtensionsAsync"/>.
    /// </summary>
    /// <param name="group">The group to map into.</param>
    private static void MapExtensions(RouteGroupBuilder group) =>
        Gate(
            group.MapGet(
                "/projects/{project}/extensions",
                (string project, IAlvoManagement management, CancellationToken ct) =>
                    Answer(() => management.GetExtensionsAsync(project, ct))),
            new ManagementRoute(nameof(IAlvoManagement.GetExtensionsAsync), ManagementOperation.GetExtensions));
```

`src/MMLib.Alvo/Management/Internal/CapabilityReport.cs`: replace `Project()` with

```csharp
    /// <summary>What this build honours, warns about, and refuses, for a host that registers no extension.</summary>
    internal static ManagementCapabilities Project() => Project([]);

    /// <summary>
    /// What this build honours, warns about, and refuses — the honoured blocks followed by <c>extensions.&lt;kind&gt;</c>
    /// for each kind of host code this host registers.
    /// </summary>
    /// <param name="registeredKinds">The kinds, already spelled as honoured entries.</param>
    internal static ManagementCapabilities Project(IReadOnlyList<string> registeredKinds) => new(
        [.. Honoured, .. registeredKinds],
        [.. UnhonouredSubsystems.All
            .Concat(UnhonouredSubsystems.WithinBlocks)
            .Concat(UnhonouredSubsystems.ReportedOnly)
            .Select(block => new ManagementWarnedBlock(block.Block, block.Consequence))],
        [.. UnhonouredFeatures.EveryRefusal.Select(refusal =>
            new ManagementRefusedFeature(refusal.Slot, refusal.Consequence, refusal.Fix))]);
```

and add one paragraph to the type remarks: the static `Honoured` stays the five blocks (the skill region and the prototype are held to it); a host's registered kinds are appended per request.

`src/MMLib.Alvo/Management/Internal/AlvoManagementService.cs`:
- add the primary-constructor parameter `ExtensionCatalog extensions,` right after `CelFunctionCatalog functions,`, its `<param name="extensions">The host's registered extensions; what <see cref="GetExtensionsAsync"/> lists.</param>`, and `using MMLib.Alvo.Extensions.Internal;`;
- `GetCapabilitiesAsync` returns `Task.FromResult(CapabilityReport.Project(extensions.HonouredKinds));`;
- `SimulatePolicyAsync` returns `Task.FromResult(Verdict(decision) with { Extensions = extensions.RunningOn(simulation.Entity, simulation.Operation) });`;
- add after `GetCelFunctionsAsync`:

```csharp
    /// <inheritdoc/>
    public Task<ManagementExtensionCatalog> GetExtensionsAsync(string project, CancellationToken ct = default)
    {
        EnsureMayPerform(ManagementOperation.GetExtensions);
        EnsureServed(project);

        return Task.FromResult(extensions.Catalog());
    }
```

`src/MMLib.Alvo/Management/Setup.cs`: in the `AlvoManagementService` factory, after `provider.GetRequiredService<Expressions.Internal.CelFunctionCatalog>(),` add `provider.GetRequiredService<Extensions.Internal.ExtensionCatalog>(),`.

Implementers in the test projects (each mirrors its own `GetCelFunctionsAsync`):
- `ManagementDecorator.cs`: `public virtual Task<ManagementExtensionCatalog> GetExtensionsAsync(string project, CancellationToken ct = default) => inner.GetExtensionsAsync(project, ct);`
- `KeptFollowScenarios.cs` `Holding`: `public Task<ManagementExtensionCatalog> GetExtensionsAsync(string project, CancellationToken ct = default) => inner.GetExtensionsAsync(project, ct);`
- `ManagementAccessTests.cs` `RefusingManagement`: `/// <inheritdoc/>` + `public Task<ManagementExtensionCatalog> GetExtensionsAsync(string project, CancellationToken ct = default) => throw new ManagementForbiddenException();`

Confirm nothing else implements the port: `git grep -n ": IAlvoManagement\b" -- src test eval samples tools` lists only `AlvoManagementService`, `ManagementDecorator`, `Holding`, `RefusingManagement`.

- [ ] **Step 4: Run the tests to verify they pass**

Run: `dotnet test --project test/MMLib.Alvo.Api.Tests --filter-class '*ManagementExtensionsTests'`, then `--filter-class '*ManagementProjectNameTests'`, `--filter-class '*ManagementContractTests'`, `--filter-class '*ManagementCapabilitiesHttpTests'`, `--filter-class '*ManagementPolicySimulationTests'`; `dotnet test --project test/MMLib.Alvo.Tests --filter-class '*ManagementOperationsTests'` and `--filter-class '*ManagementCapabilitiesTests'`.
Expected: PASS. `ManagementContractTests` proves the new member has exactly one route and a level.

- [ ] **Step 5: Baseline, prototype fixtures**

Run `dotnet test --project test/MMLib.Alvo.Abstractions.Tests --filter-class '*PublicApiApprovalTests'`; the received diff adds exactly the `GetExtensionsAsync` line to `IAlvoManagement`; accept, re-run, dispatch `alvo-snapshot-judge`.
Run `python3 scripts/gen-prototype-fixtures`, then `python3 scripts/gen-prototype-fixtures --check` (expected `up to date.`), then `scripts/test-prototype` (expected green).

- [ ] **Step 6: Format, ring0 + ring1, commit**

```bash
dotnet format whitespace MMLib.Alvo.slnx --include src/MMLib.Alvo.Abstractions/Management/IAlvoManagement.cs src/MMLib.Alvo.Abstractions/Management/ManagementExtensionCatalog.cs src/MMLib.Alvo.Abstractions/Management/ManagementModels.cs src/MMLib.Alvo/Management/Access/ManagementOperation.cs src/MMLib.Alvo/Management/Access/ManagementOperations.cs src/MMLib.Alvo/Management/Setup.cs src/MMLib.Alvo/Management/Internal/AlvoManagementService.cs src/MMLib.Alvo/Management/Internal/ManagementEndpoints.cs src/MMLib.Alvo/Management/Internal/CapabilityReport.cs test/MMLib.Alvo.Admin.Tests.EndToEnd/ManagementDecorator.cs test/MMLib.Alvo.Admin.Tests.EndToEnd/KeptFollowScenarios.cs test/MMLib.Alvo.Api.Tests/Management/ManagementAccessTests.cs test/MMLib.Alvo.Tests/Management/ManagementOperationsTests.cs test/MMLib.Alvo.Api.Tests/Management/ManagementProjectNameTests.cs test/MMLib.Alvo.Api.Tests/Management/ManagedExtensions.cs test/MMLib.Alvo.Api.Tests/Management/ManagementExtensionsTests.cs
scripts/test-ring0 && scripts/test-ring1
git add src/MMLib.Alvo.Abstractions/Management/IAlvoManagement.cs src/MMLib.Alvo.Abstractions/Management/ManagementExtensionCatalog.cs src/MMLib.Alvo.Abstractions/Management/ManagementModels.cs src/MMLib.Alvo/Management/Access/ManagementOperation.cs src/MMLib.Alvo/Management/Access/ManagementOperations.cs src/MMLib.Alvo/Management/Setup.cs src/MMLib.Alvo/Management/Internal/AlvoManagementService.cs src/MMLib.Alvo/Management/Internal/ManagementEndpoints.cs src/MMLib.Alvo/Management/Internal/CapabilityReport.cs test/MMLib.Alvo.Admin.Tests.EndToEnd/ManagementDecorator.cs test/MMLib.Alvo.Admin.Tests.EndToEnd/KeptFollowScenarios.cs test/MMLib.Alvo.Api.Tests/Management/ManagementAccessTests.cs test/MMLib.Alvo.Tests/Management/ManagementOperationsTests.cs test/MMLib.Alvo.Api.Tests/Management/ManagementProjectNameTests.cs test/MMLib.Alvo.Api.Tests/Management/ManagedExtensions.cs test/MMLib.Alvo.Api.Tests/Management/ManagementExtensionsTests.cs test/MMLib.Alvo.Abstractions.Tests/PublicApi.MMLib.Alvo.Abstractions.verified.txt docs/design/f5-admin/generated
git commit -m "feat(management): serve the extension catalog, its kinds and the simulator's extensions

Claude-Session: https://claude.ai/code/session_01NJAtafM29iddLwfhb5gnmB"
```

---

## Task 4: `ExtensionApplyGuard` — refuse a change that would unbind a bound handler

**Files:**
- Create: `src/MMLib.Alvo/Extensions/Internal/ExtensionApplyGuard.cs`, `src/MMLib.Alvo/Extensions/Internal/ExtensionApplyRefusals.cs`
- Modify: `src/MMLib.Alvo/Extensions/Internal/ExtensionBinder.cs` (a non-logging, cached `Result`), `src/MMLib.Alvo/Events/Internal/EventConditionReads.cs` (expose `FieldsReadOrCompared`), `src/MMLib.Alvo/Extensions/Setup.cs`, `test/MMLib.Alvo.Tests/Extensions/HandlerCatalog.cs`
- Test: `test/MMLib.Alvo.Tests/Extensions/ExtensionApplyGuardTests.cs`

**Interfaces:**
- Consumes: `ExtensionBinder`'s per-catalog cache (`ConditionalWeakTable<PolicyCatalog, CatalogBinding>`) over `ExtensionBindingCompiler.Compile`; `ExtensionBindingResult.Bindings.Handlers` / `.Problems` (`ExtensionProblem(Id, Kind, Locator, Message, Fix)`); `ExtensionBindingCompiler.HandlerKind`; `IPolicyCatalogProvider.Current`; `PolicyCatalog.Build(AlvoDescriptor, SchemaModel, ICelCompiler)`; `PolicyCatalog.Schema`.
- Produces:
  - `internal ExtensionBindingResult ExtensionBinder.Result(PolicyCatalog catalog)` — cached per catalog instance, logs nothing.
  - `internal sealed record ExtensionApplyCheck(PolicyCatalog Catalog, IReadOnlyList<DescriptorValidationError> Warnings)`.
  - `internal sealed class ExtensionApplyGuard(ExtensionRegistry registry, ExtensionBinder binder, ICelCompiler compiler, IPolicyCatalogProvider catalogs)` with `ExtensionApplyCheck? Check(AlvoDescriptor descriptor, SchemaModel schema)` (`null` when the host registers no handler; otherwise builds the catalog once) and `ExtensionApplyCheck Check(PolicyCatalog proposed)`. Both throw `DescriptorValidationException` when a handler that binds `catalogs.Current` would not bind the proposed catalog; a handler that binds neither is a `Warning` in the result. Registered singleton.
  - `internal static IEnumerable<string> EventConditionReads.FieldsReadOrCompared(CelNode node)`.
  - Test helper `HandlerCatalog.CatalogOf(SchemaModel schema)`.

- [ ] **Step 1: Write the failing tests**

In `test/MMLib.Alvo.Tests/Extensions/HandlerCatalog.cs`, replace `Catalog()` with:

```csharp
    internal static PolicyCatalog Catalog() => CatalogOf(Schema);

    internal static PolicyCatalog CatalogOf(SchemaModel schema) =>
        PolicyCatalog.Build(
            new AlvoDescriptor
            {
                ApiVersion = "alvo.dev/v1",
                Name = "handlers",
                Entities = schema.Entities.ToDictionary(
                    entity => entity.Name,
                    _ => new EntityDescriptor { Fields = new Dictionary<string, FieldDescriptor>(StringComparer.Ordinal) },
                    StringComparer.Ordinal),
            },
            schema,
            CelFixtures.Compiler);
```

Create `test/MMLib.Alvo.Tests/Extensions/ExtensionApplyGuardTests.cs`:

```csharp
using Microsoft.Extensions.Logging.Abstractions;

using MMLib.Alvo.Descriptor;
using MMLib.Alvo.Extensions.Internal;
using MMLib.Alvo.Rules;
using MMLib.Alvo.Rules.Internal;
using MMLib.Alvo.Schema;
using MMLib.Alvo.Tests.Expressions;

using static MMLib.Alvo.Tests.Extensions.HandlerCatalog;

using FieldType = MMLib.Alvo.Schema.FieldType;

namespace MMLib.Alvo.Tests.Extensions;

/// <summary>
/// The apply guard (spec 2026-10-14 §4.2, HX-03 settled details (6)): a change is refused when a handler that binds the
/// current catalog would no longer bind the proposed one, each finding at the pointer of what the change removed or
/// excluded, beside the compiler's own sentence; a handler that binds neither is a warning, never a refusal.
/// </summary>
public sealed class ExtensionApplyGuardTests
{
    private const string FieldFix = "Remove the registration and redeploy first, or keep the field.";

    [Fact]
    public void A_catalog_every_bound_handler_still_fits_passes_and_is_handed_back()
    {
        var proposed = CatalogOf(WithField("tickets", "priority"));

        var check = Guard(OnStatus("on-status")).Check(proposed);

        check.Catalog.ShouldBeSameAs(proposed, "the apply publishes the catalog the guard checked, never a second build");
        check.Warnings.ShouldBeEmpty();
    }

    [Fact]
    public void Removing_a_field_a_bound_handler_reads_is_refused_at_the_field_naming_the_extension()
    {
        var errors = Refused(Guard(OnStatus("on-status")), WithoutField("tickets", "status"));

        var precise = Precise(errors).ShouldHaveSingleItem();
        precise.Path.ShouldBe("/entities/tickets/fields/status");
        precise.Message.ShouldContain("'on-status' (eventHandler, in this host)");
        precise.FixSuggestion.ShouldBe(FieldFix);
        errors.ShouldAllBe(error => error.Severity == DescriptorValidationSeverity.Error);
        errors.ShouldContain(error => error.Path.Length == 0, "the compiler's own sentence is kept beside the pointer");
    }

    [Fact]
    public void Renaming_a_field_a_bound_handler_reads_is_refused_at_the_old_field()
    {
        var renamed = Replaced("tickets", entity => entity with
        {
            Fields = [.. entity.Fields.Select(field => field.Name == "status" ? field with { Name = "state" } : field)],
        });

        Precise(Refused(Guard(OnStatus("on-status")), renamed)).ShouldHaveSingleItem().Path.ShouldBe("/entities/tickets/fields/status");
    }

    [Fact]
    public void Removing_an_entity_a_bound_handler_names_is_refused_at_the_entity()
    {
        var withoutTickets = new SchemaModel([.. Schema.Entities.Where(entity => entity.Name != "tickets")]);

        var precise = Precise(Refused(Guard(OnStatus("on-status")), withoutTickets)).ShouldHaveSingleItem();

        precise.Path.ShouldBe("/entities/tickets");
        precise.FixSuggestion.ShouldBe("Remove the registration and redeploy first, or keep the entity.");
    }

    [Fact]
    public void Excluding_a_field_a_bound_handler_reads_is_refused_at_the_exclusion()
    {
        var excluded = Replaced("tickets", entity => entity with { EventImageExclusions = ["secret", "status"] });

        var precise = Precise(Refused(Guard(OnStatus("on-status")), excluded)).ShouldHaveSingleItem();

        precise.Path.ShouldBe("/entities/tickets/events/exclude/1");
        precise.FixSuggestion.ShouldBe("Remove the registration and redeploy first, or keep the field in the event images.");
    }

    [Fact]
    public void Every_bound_handler_a_change_unbinds_is_named_on_its_own()
    {
        var guard = Guard(alvo =>
        {
            OnStatus("first")(alvo);
            OnStatus("second")(alvo);
        });

        var messages = Precise(Refused(guard, WithoutField("tickets", "status"))).Select(error => error.Message).ToList();

        messages.Count.ShouldBe(2);
        messages.ShouldContain(message => message.Contains("'first'", StringComparison.Ordinal));
        messages.ShouldContain(message => message.Contains("'second'", StringComparison.Ordinal));
    }

    [Fact]
    public void A_retyped_field_the_condition_no_longer_compiles_against_is_refused_naming_the_extension()
    {
        var guard = Guard(alvo => alvo.Entity("tickets", t => t.OnUpdated("on-resolved", OnUpdated, when: "new.status == 'resolved'")));
        var retyped = Replaced("tickets", entity => entity with
        {
            Fields = [.. entity.Fields.Select(field => field.Name == "status" ? new FieldSchema { Name = "status", Type = FieldType.Integer } : field)],
        });

        var errors = Refused(guard, retyped);

        Precise(errors).ShouldBeEmpty("the field is still there: no pointer names the cause");
        errors.ShouldAllBe(error => error.Message.Contains("'on-resolved' (eventHandler, in this host) would no longer bind"));
    }

    [Fact]
    public void With_nothing_primed_an_unfit_handler_is_a_warning_not_a_refusal()
    {
        var check = Guard(OnStatus("on-status"), current: null).Check(CatalogOf(WithoutField("tickets", "status")));

        check.Warnings.ShouldNotBeEmpty();
        check.Warnings.ShouldAllBe(warning => warning.Severity == DescriptorValidationSeverity.Warning);
        check.Warnings.ShouldAllBe(warning => warning.Message.Contains("'on-status'"));
    }

    [Fact]
    public void A_handler_already_unbound_does_not_block_an_unrelated_change()
    {
        var unbound = WithoutField("tickets", "status");
        var guard = Guard(OnStatus("on-status"), current: CatalogOf(unbound));

        var check = guard.Check(CatalogOf(new SchemaModel([.. unbound.Entities.Where(entity => entity.Name != "notes")])));

        check.Warnings.ShouldContain(warning => warning.Message.Contains("'on-status'"), "still reported, as it was before the change");
    }

    [Fact]
    public void An_every_entity_or_application_event_handler_never_refuses_an_apply()
    {
        var guard = Guard(alvo => alvo
            .Entity("*", every => every.OnSaved("audit", OnSaved, when: "'admin' in @user.roles"))
            .AddEventHandler("approved", "orders.approved", OnRecord));
        var withoutNotes = new SchemaModel([.. Schema.Entities.Where(entity => entity.Name != "notes")]);

        guard.Check(CatalogOf(withoutNotes)).Warnings.ShouldBeEmpty();
    }

    [Fact]
    public void A_host_without_handlers_builds_no_catalog_and_checks_nothing()
    {
        var descriptor = new AlvoDescriptor
        {
            ApiVersion = "alvo.dev/v1",
            Name = "handlers",
            Entities = new Dictionary<string, EntityDescriptor>(StringComparer.Ordinal),
        };

        Guard(_ => { }).Check(descriptor, new SchemaModel([])).ShouldBeNull();
    }

    private static Action<IAlvoBuilder> OnStatus(string id) =>
        alvo => alvo.Entity("tickets", t => t.OnUpdated(id, OnUpdated, when: "changed(status)"));

    private static ExtensionApplyGuard Guard(Action<IAlvoBuilder> configure) => Guard(configure, Catalog());

    private static ExtensionApplyGuard Guard(Action<IAlvoBuilder> configure, PolicyCatalog? current)
    {
        var catalogs = new PolicyCatalogProvider();
        if (current is not null)
        {
            catalogs.SetCurrent("handlers", current);
        }

        var registry = Registry(configure);
        var binder = new ExtensionBinder(registry, CelFixtures.Compiler, EveryService(), NullLogger<ExtensionBinder>.Instance);
        return new ExtensionApplyGuard(registry, binder, CelFixtures.Compiler, catalogs);
    }

    private static IReadOnlyList<DescriptorValidationError> Refused(ExtensionApplyGuard guard, SchemaModel proposed) =>
        Should.Throw<DescriptorValidationException>(() => guard.Check(CatalogOf(proposed))).Result.Errors;

    private static IEnumerable<DescriptorValidationError> Precise(IEnumerable<DescriptorValidationError> errors) =>
        errors.Where(error => error.Path.Length > 0);

    private static SchemaModel Replaced(string name, Func<EntitySchema, EntitySchema> change) =>
        new([.. Schema.Entities.Select(entity => entity.Name == name ? change(entity) : entity)]);

    private static SchemaModel WithoutField(string entity, string field) =>
        Replaced(entity, schema => schema with { Fields = [.. schema.Fields.Where(candidate => candidate.Name != field)] });

    private static SchemaModel WithField(string entity, string field) =>
        Replaced(entity, schema => schema with { Fields = [.. schema.Fields, new FieldSchema { Name = field, Type = FieldType.String, Nullable = true }] });
}
```

Check the namespaces of `PolicyCatalogProvider` (`git grep -n "class PolicyCatalogProvider" -- src`) and `CelFixtures`, and drop any `using` the build's IDE0005 reports unused.

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test --project test/MMLib.Alvo.Tests --filter-class '*ExtensionApplyGuardTests'`
Expected: build FAIL — `ExtensionApplyGuard` and `ExtensionBinder.Result` do not exist.

- [ ] **Step 3: Implement**

`src/MMLib.Alvo/Extensions/Internal/ExtensionBinder.cs`, beside `For`:

```csharp
    /// <summary>What binding <paramref name="catalog"/> produced — compiled once per catalog and cached; logs nothing.</summary>
    /// <param name="catalog">A catalog the boot primed, an apply proposes, or the provider holds.</param>
    internal ExtensionBindingResult Result(PolicyCatalog catalog) => Binding(catalog).Result;
```

Because the cache is keyed by the catalog **instance**, the catalog the guard checks — and the apply then publishes (Task 5) — is compiled once: the delivery dispatcher's later `For(catalog)` is a cache hit.

`src/MMLib.Alvo/Events/Internal/EventConditionReads.cs`: make `FieldsReadOrCompared` internal and document it (lower-case prose: this file is under the SQL text scan):

```csharp
    /// <summary>
    /// Every field <paramref name="node"/> reads — through <c>new.</c>, <c>old.</c>, a bare field or <c>has()</c> — or
    /// compares through <c>changed(field)</c>, in source order, repeats included.
    /// </summary>
    /// <param name="node">The compiled condition's tree.</param>
    internal static IEnumerable<string> FieldsReadOrCompared(CelNode node) =>
        Own(node).Concat(CelTree.Children(node).SelectMany(FieldsReadOrCompared));
```

`src/MMLib.Alvo/Extensions/Internal/ExtensionApplyGuard.cs`:

```csharp
using MMLib.Alvo.Descriptor;
using MMLib.Alvo.Expressions;
using MMLib.Alvo.Rules;
using MMLib.Alvo.Schema;

namespace MMLib.Alvo.Extensions.Internal;

/// <summary>A proposed catalog no bound handler would lose, and the handlers that bind neither it nor the current one.</summary>
/// <param name="Catalog">The checked catalog — the one an apply publishes, so it is built and compiled once.</param>
/// <param name="Warnings">One warning per problem of a handler that was not bound before the change either.</param>
internal sealed record ExtensionApplyCheck(PolicyCatalog Catalog, IReadOnlyList<DescriptorValidationError> Warnings);

/// <summary>
/// Refuses a change that would unbind a handler: one that binds the catalog the provider holds and would not bind the
/// proposed one — HX-03's own compiler, through the binder's per-catalog cache, so the guard cannot drift from the boot.
/// It keeps a runtime apply, a rollback, a dry run and <c>ApplyAlvoDescriptorAsync</c> from unbinding a handler; the boot
/// refuses on its own, through <see cref="ExtensionBinder.EnsureBindable"/>. A handler that was already unbound (a
/// dashboard-first host's first descriptor, a stored boot that ended <c>Failed</c>) blocks nothing: it is reported as a
/// warning, and its events keep being recorded dead <c>unbound</c> until a descriptor fits it.
/// </summary>
internal sealed class ExtensionApplyGuard(
    ExtensionRegistry registry, ExtensionBinder binder, ICelCompiler compiler, IPolicyCatalogProvider catalogs)
{
    /// <summary>Builds the catalog <paramref name="descriptor"/> proposes and checks it; <see langword="null"/> when no handler is registered.</summary>
    /// <param name="descriptor">The proposed descriptor.</param>
    /// <param name="schema">The schema it maps to — the very instance the apply publishes.</param>
    /// <exception cref="DescriptorValidationException">A handler that binds the current catalog would no longer bind.</exception>
    internal ExtensionApplyCheck? Check(AlvoDescriptor descriptor, SchemaModel schema) =>
        registry.Handlers.Count == 0 ? null : Check(PolicyCatalog.Build(descriptor, schema, compiler));

    /// <summary>Refuses <paramref name="proposed"/> when a handler that binds the current catalog would no longer bind it.</summary>
    /// <param name="proposed">The catalog the change would prime.</param>
    /// <exception cref="DescriptorValidationException">A handler that binds the current catalog would no longer bind.</exception>
    internal ExtensionApplyCheck Check(PolicyCatalog proposed)
    {
        var problems = binder.Result(proposed).Problems;
        if (problems.Count == 0)
        {
            return new(proposed, []);
        }

        var boundNow = BoundNow();
        var regressions = problems.Where(problem => boundNow.Contains(problem.Id)).ToList();
        if (regressions.Count > 0)
        {
            throw new DescriptorValidationException(new DescriptorValidationResult(
                ExtensionApplyRefusals.Refusals(registry, regressions, catalogs.Current!.Schema, proposed.Schema, compiler)));
        }

        return new(proposed, ExtensionApplyRefusals.Warnings(problems));
    }

    private HashSet<string> BoundNow() => catalogs.Current is { } current
        ? binder.Result(current).Bindings.Handlers.Select(handler => handler.Id).ToHashSet(StringComparer.Ordinal)
        : new HashSet<string>(StringComparer.Ordinal);
}
```

`src/MMLib.Alvo/Extensions/Internal/ExtensionApplyRefusals.cs`:

```csharp
using MMLib.Alvo.Descriptor;
using MMLib.Alvo.Events.Internal;
using MMLib.Alvo.Expressions;
using MMLib.Alvo.Schema;

namespace MMLib.Alvo.Extensions.Internal;

/// <summary>
/// Turns binding problems into descriptor findings. A refusal names, for a handler the change unbinds, the entity, the
/// field or the <c>events.exclude</c> item the change removed or excluded — each existed before the change, since the
/// handler bound it — and keeps the compiler's own sentence beside it, so a second cause is not hidden behind the
/// first. A warning is the compiler's sentence alone, for a handler that bound neither catalog.
/// </summary>
internal static class ExtensionApplyRefusals
{
    private const string Kind = ExtensionBindingCompiler.HandlerKind;
    private const string FixPrefix = "Remove the registration and redeploy first, or keep the ";

    internal static IReadOnlyList<DescriptorValidationError> Refusals(
        ExtensionRegistry registry, IReadOnlyList<ExtensionProblem> regressions, SchemaModel current, SchemaModel proposed, ICelCompiler compiler)
    {
        var errors = new List<DescriptorValidationError>();
        foreach (var refused in regressions.GroupBy(problem => problem.Id, StringComparer.Ordinal))
        {
            var handler = registry.Handlers.First(candidate => string.Equals(candidate.Id, refused.Key, StringComparison.Ordinal));
            errors.AddRange(Precise(handler, current, proposed, compiler));
            errors.AddRange(refused.Select(problem => Generic(problem, DescriptorValidationSeverity.Error)));
        }

        return errors;
    }

    internal static IReadOnlyList<DescriptorValidationError> Warnings(IReadOnlyList<ExtensionProblem> problems) =>
        [.. problems.Select(problem => Generic(problem, DescriptorValidationSeverity.Warning))];

    private static DescriptorValidationError[] Precise(
        HandlerRegistration handler, SchemaModel current, SchemaModel proposed, ICelCompiler compiler) =>
        handler.Source is EntityEvents { IsAll: false } source
            ? [.. source.Entities.SelectMany(entity => ForEntity(handler, entity, current, proposed, compiler))]
            : [];

    private static IEnumerable<DescriptorValidationError> ForEntity(
        HandlerRegistration handler, string name, SchemaModel current, SchemaModel proposed, ICelCompiler compiler)
    {
        if (Named(proposed, name) is not { } after)
        {
            return [EntityRemoved(handler.Id, name)];
        }

        return FieldsRead(handler.When, Named(current, name), compiler)
            .Select(field => FieldRefusal(handler.Id, after, field))
            .OfType<DescriptorValidationError>();
    }

    private static DescriptorValidationError? FieldRefusal(string id, EntitySchema after, string field)
    {
        if (!after.Fields.Any(candidate => string.Equals(candidate.Name, field, StringComparison.Ordinal)))
        {
            return FieldRemoved(id, after.Name, field);
        }

        var index = after.EventImageExclusions.ToList().IndexOf(field);
        return index < 0 ? null : FieldExcluded(id, after.Name, field, index);
    }

    private static string[] FieldsRead(string? when, EntitySchema? before, ICelCompiler compiler)
    {
        if (when is null || before is null)
        {
            return [];
        }

        var result = compiler.Compile(when, CelProfile.Condition, before);
        return result.IsSuccess
            ? [.. EventConditionReads.FieldsReadOrCompared(result.Expression!.Root).Distinct(StringComparer.Ordinal)]
            : [];
    }

    private static EntitySchema? Named(SchemaModel schema, string name) =>
        schema.Entities.FirstOrDefault(entity => string.Equals(entity.Name, name, StringComparison.Ordinal));

    private static DescriptorValidationError EntityRemoved(string id, string entity) => Error(
        $"/entities/{entity}",
        $"Entity '{entity}' is bound by extension '{id}' ({Kind}, in this host): without it the handler would run on "
        + "nothing, and every event it would have handled would be recorded dead.",
        FixPrefix + "entity.");

    private static DescriptorValidationError FieldRemoved(string id, string entity, string field) => Error(
        $"/entities/{entity}/fields/{field}",
        $"Field '{field}' of '{entity}' is bound by extension '{id}' ({Kind}, in this host): its condition reads it, so "
        + "without it the handler would no longer bind, and every event it would have handled would be recorded dead.",
        FixPrefix + "field.");

    private static DescriptorValidationError FieldExcluded(string id, string entity, string field, int index) => Error(
        $"/entities/{entity}/events/exclude/{index}",
        $"Field '{field}' of '{entity}' is read by extension '{id}' ({Kind}, in this host): left out of the event images, "
        + "it would decide the handler's condition by its absence.",
        FixPrefix + "field in the event images.");

    private static DescriptorValidationError Generic(ExtensionProblem problem, DescriptorValidationSeverity severity) => new(
        string.Empty,
        severity == DescriptorValidationSeverity.Error
            ? $"Extension '{problem.Id}' ({problem.Kind}, in this host) would no longer bind to this descriptor — {problem.Locator}: {problem.Message}"
            : $"Extension '{problem.Id}' ({problem.Kind}, in this host) does not bind to this descriptor, and did not before this change — {problem.Locator}: {problem.Message}",
        problem.Fix,
        severity);

    private static DescriptorValidationError Error(string path, string message, string fix) =>
        new(path, message, fix, DescriptorValidationSeverity.Error);
}
```

`src/MMLib.Alvo/Extensions/Setup.cs`: after the `ExtensionCatalog` line add `services.TryAddSingleton<ExtensionApplyGuard>();` (`ExtensionBinder`, `IPolicyCatalogProvider` and `ICelCompiler` are core registrations).

- [ ] **Step 4: Run the tests to verify they pass**

Run: `dotnet test --project test/MMLib.Alvo.Tests --filter-class '*ExtensionApplyGuardTests'`, then the HX-03 suites that read `HandlerCatalog.Catalog()` or the binder: `--filter-class '*ExtensionBindingCompilerTests'`, `--filter-class '*ExtensionBinderTests'`, `--filter-class '*DeliveryFanOutTests'`.
Expected: PASS.

- [ ] **Step 5: Format, ring0, commit**

```bash
dotnet format whitespace MMLib.Alvo.slnx --include src/MMLib.Alvo/Extensions/Internal/ExtensionApplyGuard.cs src/MMLib.Alvo/Extensions/Internal/ExtensionApplyRefusals.cs src/MMLib.Alvo/Extensions/Internal/ExtensionBinder.cs src/MMLib.Alvo/Events/Internal/EventConditionReads.cs src/MMLib.Alvo/Extensions/Setup.cs test/MMLib.Alvo.Tests/Extensions/HandlerCatalog.cs test/MMLib.Alvo.Tests/Extensions/ExtensionApplyGuardTests.cs
scripts/test-ring0
git add src/MMLib.Alvo/Extensions/Internal/ExtensionApplyGuard.cs src/MMLib.Alvo/Extensions/Internal/ExtensionApplyRefusals.cs src/MMLib.Alvo/Extensions/Internal/ExtensionBinder.cs src/MMLib.Alvo/Events/Internal/EventConditionReads.cs src/MMLib.Alvo/Extensions/Setup.cs test/MMLib.Alvo.Tests/Extensions/HandlerCatalog.cs test/MMLib.Alvo.Tests/Extensions/ExtensionApplyGuardTests.cs
git commit -m "feat(extensions): refuse a change that would unbind a bound handler

Claude-Session: https://claude.ai/code/session_01NJAtafM29iddLwfhb5gnmB"
```

---

## Task 5: The guard on every apply path, and the Management API refusal **[slice end]**

**Files:**
- Modify: `src/MMLib.Alvo/Migrations/RuntimeSchemaService.cs`, `src/MMLib.Alvo/Migrations/SchemaMigrationRunner.cs`, `src/MMLib.Alvo/Migrations/Internal/DescriptorBootPlan.cs` (remark), `src/MMLib.Alvo/AlvoServiceCollectionExtensions.cs`
- Modify: `test/MMLib.Alvo.Tests/Migrations/RuntimeSchemaWorld.cs`, `test/MMLib.Alvo.Tests/Migrations/SchemaMigrationRunnerTests.cs`
- Create: `test/MMLib.Alvo.Tests/Migrations/RuntimeSchemaServiceExtensionGuardTests.cs`
- Modify: `test/MMLib.Alvo.Api.Tests/Management/ManagementExtensionsTests.cs`

**Interfaces:**
- Consumes: Task 4's `ExtensionApplyGuard.Check(AlvoDescriptor, SchemaModel)` → `ExtensionApplyCheck?` and `Check(PolicyCatalog)`; `ExtensionBinder(registry, compiler, services, logger)`.
- Produces: `internal RuntimeSchemaService(IDescriptorValidator, ISchemaMigrator, IDescriptorVersionStore, IRuntimeSchemaWriter, ICelCompiler, IPolicyCatalogProvider, ExtensionApplyGuard? extensions)`; `SchemaMigrationRunner(…, IPolicyCatalogProvider policyCatalogProvider, ExtensionApplyGuard? extensions = null)`; `RuntimeSchemaWorld.Around(InMemoryDescriptorVersionStore versions, Func<IPolicyCatalogProvider, ExtensionApplyGuard>? extensions = null)`; the guard's warnings reach `DescriptorApplyPreview.Warnings` (and with it `ManagementApplyResult.Warnings`, which the assistant reads).

- [ ] **Step 1: Write the failing tests**

In `test/MMLib.Alvo.Tests/Migrations/RuntimeSchemaWorld.cs`: give the private constructor a fourth parameter `ExtensionApplyGuard? extensions` and build the service with `new RuntimeSchemaService(new DescriptorValidator(), new InMemorySchemaMigrator(), versions, writer, new CelCompiler(), policyCatalogs, extensions)`; replace `Around` with:

```csharp
    internal static RuntimeSchemaWorld Around(
        InMemoryDescriptorVersionStore versions, Func<IPolicyCatalogProvider, ExtensionApplyGuard>? extensions = null)
    {
        var catalogs = new CountingPolicyCatalogs(new PolicyCatalogProvider());
        return new(versions, new CountingRuntimeSchemaWriter(new InMemoryRuntimeSchemaWriter(versions)), catalogs, extensions?.Invoke(catalogs));
    }
```

and extend its docs: "with the host's extension guard, built over this world's catalog provider, when one is given".

Create `test/MMLib.Alvo.Tests/Migrations/RuntimeSchemaServiceExtensionGuardTests.cs` (take the `using`s for `DescriptorToSchemaMapper`, `InMemoryDescriptorVersionStore` and `CelCompiler` from `RuntimeSchemaWorld.cs` if they differ from these):

```csharp
using Microsoft.Extensions.Logging.Abstractions;

using MMLib.Alvo.Descriptor;
using MMLib.Alvo.Descriptor.Internal;
using MMLib.Alvo.Expressions.Internal;
using MMLib.Alvo.Extensions.Internal;
using MMLib.Alvo.Migrations;
using MMLib.Alvo.Rules;
using MMLib.Alvo.Testing.Migrations;
using MMLib.Alvo.Tests.Extensions;

using static MMLib.Alvo.Tests.Migrations.RuntimeSchemaWorld;

namespace MMLib.Alvo.Tests.Migrations;

/// <summary>
/// The guard on the runtime apply path (spec 2026-10-14 §4.2, HX-03 settled details (6)): an apply, a dry run and a
/// rollback that would unbind a bound handler are refused before anything is read, planned or written; a handler that
/// was never bound blocks nothing and is reported as a warning.
/// </summary>
public sealed class RuntimeSchemaServiceExtensionGuardTests
{
    private const string NotesPointer = "/entities/tasks/fields/notes";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static Func<IPolicyCatalogProvider, ExtensionApplyGuard> OnNotes => catalogs =>
    {
        var registry = HandlerCatalog.Registry(alvo => alvo.Entity("tasks", tasks => tasks.OnUpdated("on-notes", HandlerCatalog.OnUpdated, when: "changed(notes)")));
        var binder = new ExtensionBinder(registry, new CelCompiler(), HandlerCatalog.EveryService(), NullLogger<ExtensionBinder>.Instance);
        return new ExtensionApplyGuard(registry, binder, new CelCompiler(), catalogs);
    };

    [Fact]
    public async Task An_apply_that_removes_a_field_a_bound_handler_reads_is_refused_and_writes_and_primes_nothing()
    {
        var world = await WithNotesAsync();

        var refused = await Should.ThrowAsync<DescriptorValidationException>(() => world.Service.ApplyAsync(
            Project, OneField, expectedRevision: 1, new MigrationOptions { AllowDestructive = true }, Ct));

        Pointed(refused).ShouldBe(NotesPointer);
        world.Writer.Applies.ShouldBe(0);
        world.PolicyCatalogs.SetCurrentCalls.ShouldBe(0);
    }

    [Fact]
    public async Task A_dry_run_of_it_is_refused_the_same_way()
    {
        var world = await WithNotesAsync();

        var refused = await Should.ThrowAsync<DescriptorValidationException>(() => world.Service.PreviewAsync(
            Project, OneField, expectedRevision: 1, new MigrationOptions(), Ct));

        Pointed(refused).ShouldBe(NotesPointer);
    }

    [Fact]
    public async Task The_binding_refusal_is_answered_before_the_revision_check()
    {
        var world = await WithNotesAsync();

        await Should.ThrowAsync<DescriptorValidationException>(() => world.Service.ApplyAsync(
            Project, OneField, expectedRevision: 7, new MigrationOptions(), Ct));
    }

    [Fact]
    public async Task An_apply_that_keeps_what_the_handler_reads_is_applied()
    {
        var world = await WithNotesAsync();

        (await world.Service.ApplyAsync(Project, ThreeFields, expectedRevision: 1, new MigrationOptions(), Ct)).Revision.ShouldBe(2);
    }

    [Fact]
    public async Task A_rollback_to_a_revision_without_the_field_is_refused()
    {
        var versions = new InMemoryDescriptorVersionStore();
        var unguarded = Around(versions);
        await unguarded.Service.ApplyAsync(Project, OneField, expectedRevision: 0, new MigrationOptions(), Ct);
        await unguarded.Service.ApplyAsync(Project, TwoFields, expectedRevision: 1, new MigrationOptions(), Ct);
        var guarded = Around(versions, OnNotes);
        guarded.PolicyCatalogs.SetCurrent(Project, CatalogOf(TwoFields));

        var refused = await Should.ThrowAsync<DescriptorValidationException>(() => guarded.Service.RollbackAsync(
            Project, targetRevision: 1, new MigrationOptions { AllowDestructive = true }, Ct));

        Pointed(refused).ShouldBe(NotesPointer);
        (await versions.GetCurrentAsync(Project, Ct))!.Revision.ShouldBe(2);
    }

    [Fact]
    public async Task A_first_apply_a_handler_does_not_fit_is_applied_with_a_warning()
    {
        var world = Around(new InMemoryDescriptorVersionStore(), OnNotes);

        var preview = await world.Service.PreviewAsync(Project, OneField, expectedRevision: 0, new MigrationOptions(), Ct);
        var applied = await world.Service.ApplyAsync(Project, OneField, expectedRevision: 0, new MigrationOptions(), Ct);

        preview.Warnings.ShouldContain(warning =>
            warning.Severity == DescriptorValidationSeverity.Warning && warning.Message.Contains("'on-notes'"));
        applied.Revision.ShouldBe(1, "a handler nothing ever bound blocks no apply; its events are recorded dead 'unbound'");
    }

    [Fact]
    public async Task A_handler_already_unbound_does_not_block_an_unrelated_apply()
    {
        var world = Around(new InMemoryDescriptorVersionStore(), OnNotes);
        await world.Service.ApplyAsync(Project, OneField, expectedRevision: 0, new MigrationOptions(), Ct);

        (await world.Service.ApplyAsync(Project, OneFieldPlusRequired, expectedRevision: 1, new MigrationOptions(), Ct)).Revision.ShouldBe(2);
    }

    [Fact]
    public async Task A_first_apply_every_handler_fits_is_applied_without_a_warning()
    {
        var world = Around(new InMemoryDescriptorVersionStore(), OnNotes);

        var preview = await world.Service.PreviewAsync(Project, TwoFields, expectedRevision: 0, new MigrationOptions(), Ct);

        preview.Warnings.ShouldNotContain(warning => warning.Message.Contains("'on-notes'"));
        (await world.Service.ApplyAsync(Project, TwoFields, expectedRevision: 0, new MigrationOptions(), Ct)).Revision.ShouldBe(1);
    }

    private static async Task<RuntimeSchemaWorld> WithNotesAsync()
    {
        var world = Around(new InMemoryDescriptorVersionStore(), OnNotes);
        await world.Service.ApplyAsync(Project, TwoFields, expectedRevision: 0, new MigrationOptions(), Ct);
        world.Writer.Forget();
        world.PolicyCatalogs.Forget();

        return world;
    }

    private static string Pointed(DescriptorValidationException refused) =>
        refused.Result.Errors.Where(error => error.Path.Length > 0).ShouldHaveSingleItem().Path;

    private static PolicyCatalog CatalogOf(string descriptorJson)
    {
        var descriptor = AlvoDescriptor.Parse(descriptorJson);
        return PolicyCatalog.Build(descriptor, DescriptorToSchemaMapper.Map(descriptor), new CelCompiler());
    }
}
```

In `test/MMLib.Alvo.Tests/Migrations/SchemaMigrationRunnerTests.cs`, add beside `FleetDescriptorJson`:

```csharp
    /// <summary><see cref="FleetDescriptorJson"/> plus a <c>plate</c> a handler reads — the catalog a host had primed before.</summary>
    private const string FleetWithPlateJson = """
        {
          "apiVersion": "alvo.dev/v1",
          "name": "fleet",
          "entities": {
            "vehicles": {
              "fields": {
                "vin": { "type": "string", "required": true, "maxLength": 17 },
                "make": { "type": "string" },
                "plate": { "type": "string" }
              }
            }
          }
        }
        """;
```

and replace `Applying_a_descriptor_a_handler_does_not_fit_succeeds_and_leaves_the_handler_unbound` (and its summary) with:

```csharp
    /// <summary>
    /// <c>ApplyAlvoDescriptorAsync</c> is guarded like every apply (HX-04), and the guard refuses a change, not a state: a
    /// handler that never bound — nothing was primed — blocks nothing, and is left unbound with its events recorded dead.
    /// </summary>
    [Fact]
    public async Task A_descriptor_a_handler_never_fit_is_applied_and_the_handler_stays_unbound()
    {
        _store.GetCurrentAsync("fleet", Arg.Any<CancellationToken>()).Returns((AppliedSchema?)null);
        _introspector.IntrospectAsync(Arg.Any<CancellationToken>()).Returns(new SchemaModel([]));
        var (runner, binder, catalogs) = Guarded(alvo => alvo.Entity("no-such-entity", e => e.OnCreated("orphan-handler", HandlerCatalog.OnCreated)));

        var result = await runner.RunAsync(new MigrationOptions(), TestContext.Current.CancellationToken);

        result.Applied.ShouldBeTrue();
        binder.For(catalogs.Current.ShouldNotBeNull()).Unbound.ShouldHaveSingleItem().Id.ShouldBe("orphan-handler");
    }

    /// <summary>A descriptor that drops what a bound handler reads is refused before anything is planned or saved.</summary>
    [Fact]
    public async Task A_descriptor_that_drops_what_a_bound_handler_reads_is_refused_before_anything_is_written()
    {
        _store.GetCurrentAsync("fleet", Arg.Any<CancellationToken>()).Returns((AppliedSchema?)null);
        _introspector.IntrospectAsync(Arg.Any<CancellationToken>()).Returns(new SchemaModel([]));
        var (runner, _, catalogs) = Guarded(alvo => alvo.Entity("vehicles", e => e.OnUpdated("on-plate", HandlerCatalog.OnUpdated, when: "changed(plate)")));
        var primed = AlvoDescriptor.Parse(FleetWithPlateJson);
        var before = PolicyCatalog.Build(primed, DescriptorToSchemaMapper.Map(primed), new CelCompiler());
        catalogs.SetCurrent("fleet", before);

        var refused = await Should.ThrowAsync<DescriptorValidationException>(
            () => runner.RunAsync(new MigrationOptions(), TestContext.Current.CancellationToken));

        refused.Result.Errors.Where(error => error.Path.Length > 0).ShouldHaveSingleItem().Path.ShouldBe("/entities/vehicles/fields/plate");
        await _store.DidNotReceive().SaveAsync(Arg.Any<string>(), Arg.Any<AppliedSchema>(), Arg.Any<CancellationToken>());
        catalogs.Current.ShouldBeSameAs(before, "a refused apply primes nothing");
    }

    private (SchemaMigrationRunner Runner, ExtensionBinder Binder, PolicyCatalogProvider Catalogs) Guarded(Action<IAlvoBuilder> configure)
    {
        var registry = HandlerCatalog.Registry(configure);
        var binder = new ExtensionBinder(registry, new CelCompiler(), HandlerCatalog.EveryService(), NullLogger<ExtensionBinder>.Instance);
        var catalogs = new PolicyCatalogProvider();
        var runner = new SchemaMigrationRunner(
            new DescriptorBootPlan(_source, new DescriptorValidator(), new CelCompiler(), NullLogger<DescriptorBootPlan>.Instance, binder),
            _migrator,
            _introspector,
            _store,
            catalogs,
            new ExtensionApplyGuard(registry, binder, new CelCompiler(), catalogs));

        return (runner, binder, catalogs);
    }
```

Add the `using`s the new facts need (`MMLib.Alvo.Extensions.Internal`, the namespaces of `DescriptorToSchemaMapper` and `AlvoDescriptor`) and drop any the build reports unused.

Append to `test/MMLib.Alvo.Api.Tests/Management/ManagementExtensionsTests.cs` (acceptance (2)), with `private static readonly TestApiKey _dev = new("mgmt-dev", ["dispatcher"], ["*:write"]);` beside `_ops` and the `using` for `AlvoProblemTypes` (`git grep -n "class AlvoProblemTypes" -- src`). A violation's fix travels as `fixSuggestion` (`AlvoViolation`'s `JsonPropertyName`):

```csharp
    [Fact]
    public async Task Removing_a_bound_field_is_refused_naming_the_extension()
    {
        await using var world = await ManagedExtensions.StartAsync([_dev]);
        var descriptor = await CurrentAsync(world);
        descriptor["entities"]!["vehicles"]!["fields"]!.AsObject().Remove("plate");

        var violation = await RefusedAsync(world, descriptor, allowDestructive: true);

        violation["pointer"]!.GetValue<string>().ShouldBe("/entities/vehicles/fields/plate");
        violation["message"]!.GetValue<string>().ShouldContain($"'{PlateChanged}' (eventHandler, in this host)");
        violation["fixSuggestion"]!.GetValue<string>().ShouldBe("Remove the registration and redeploy first, or keep the field.");
        (await ManagementApplyWorld.RevisionAsync(world, _dev)).ShouldBe(1);
    }

    [Fact]
    public async Task Excluding_a_bound_field_from_the_event_images_is_refused_naming_the_extension()
    {
        await using var world = await ManagedExtensions.StartAsync([_dev]);
        var descriptor = await CurrentAsync(world);
        descriptor["entities"]!["vehicles"]!["events"] = new JsonObject { ["exclude"] = new JsonArray("plate") };

        var violation = await RefusedAsync(world, descriptor);

        violation["pointer"]!.GetValue<string>().ShouldBe("/entities/vehicles/events/exclude/0");
        violation["message"]!.GetValue<string>().ShouldContain($"'{PlateChanged}'");
    }

    [Fact]
    public async Task A_dry_run_removing_a_bound_entity_is_refused_at_the_entity()
    {
        await using var world = await ManagedExtensions.StartAsync([_dev]);
        var descriptor = await CurrentAsync(world);
        descriptor["entities"]!.AsObject().Remove("vehicles");

        var violation = await RefusedAsync(world, descriptor, allowDestructive: true, query: "?dryRun=true");

        violation["pointer"]!.GetValue<string>().ShouldBe("/entities/vehicles");
    }

    [Fact]
    public async Task An_apply_that_keeps_what_every_extension_reads_is_applied()
    {
        await using var world = await ManagedExtensions.StartAsync([_dev]);
        var descriptor = await CurrentAsync(world);
        descriptor["entities"]!["vehicles"]!["fields"]!["nickname"] = new JsonObject { ["type"] = "string", ["maxLength"] = 40 };

        using var response = await ManagementApplyWorld.ApplyAsync(world, _dev, descriptor.ToJsonString(), ifMatch: "\"1\"");

        response.IsSuccessStatusCode.ShouldBeTrue();
        (await ManagementApplyWorld.RevisionAsync(world, _dev)).ShouldBe(2);
    }

    private static async Task<JsonNode> CurrentAsync(AlvoApiWorld world) =>
        JsonNode.Parse(await ManagementApplyWorld.CurrentAsync(world, _dev))!;

    /// <summary>The refusal's one pointed violation; the compiler's own sentences travel beside it at the empty pointer.</summary>
    private static async Task<JsonObject> RefusedAsync(AlvoApiWorld world, JsonNode descriptor, bool allowDestructive = false, string query = "")
    {
        using var response = await ManagementApplyWorld.ApplyAsync(
            world, _dev, descriptor.ToJsonString(), ifMatch: "\"1\"", allowDestructive: allowDestructive, query: query);

        response.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);
        (await response.ReadProblemTypeAsync()).ShouldBe(AlvoProblemTypes.Validation);
        return (await response.ReadJsonObjectAsync())["violations"]!.AsArray()
            .Select(violation => violation!.AsObject())
            .Where(violation => violation["pointer"]!.GetValue<string>().Length > 0)
            .ShouldHaveSingleItem();
    }
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test --project test/MMLib.Alvo.Tests --filter-class '*RuntimeSchemaServiceExtensionGuardTests'`
Expected: build FAIL (no 7-argument `RuntimeSchemaService` constructor, no 6-argument `SchemaMigrationRunner`).

- [ ] **Step 3: Wire the guard**

`src/MMLib.Alvo/Migrations/RuntimeSchemaService.cs`:
- add the field `private readonly ExtensionApplyGuard? _extensions;` and `using MMLib.Alvo.Extensions.Internal;`;
- the public constructor keeps its signature and documentation and becomes `: this(validator, migrator, store, writer, compiler, policyCatalogProvider, extensions: null) { }`;
- add the internal constructor carrying today's null checks and assignments plus `_extensions = extensions;`, its `extensions` param documented: "Refuses a descriptor that would unbind a handler bound to the current catalog, and hands back the catalog it checked; `null` for a service a host builds itself, which then carries no guard.";
- `ApplyAsync`: right after `var desired = DescriptorToSchemaMapper.Map(descriptor);` insert `var checkedCatalog = _extensions?.Check(descriptor, desired)?.Catalog;`. In the unchanged-reapply branch replace `PolicyCatalogPriming.Prime(_policyCatalogProvider, _compiler, project, descriptor, desired);` with `_policyCatalogProvider.SetCurrent(project, checkedCatalog ?? PolicyCatalog.Build(descriptor, desired, _compiler));` (what `Prime` does, minus the second build), and replace `var catalog = PolicyCatalog.Build(descriptor, desired, _compiler);` with `var catalog = checkedCatalog ?? PolicyCatalog.Build(descriptor, desired, _compiler);`;
- `PreviewAsync`: replace `var desired = DescriptorToSchemaMapper.Map(AlvoDescriptor.Parse(descriptorJson));` with

```csharp
        var descriptor = AlvoDescriptor.Parse(descriptorJson);
        var desired = DescriptorToSchemaMapper.Map(descriptor);
        var check = _extensions?.Check(descriptor, desired);
```

  and make the result's warnings `Warnings = [.. validation.Errors.Where(error => error.Severity == DescriptorValidationSeverity.Warning), .. check?.Warnings ?? []],`;
- `RollbackAsync`: move `var restored = target.Schema with { };` (and its ABA comment) up to right after `currentVersion` is read, insert `var checkedCatalog = _extensions?.Check(AlvoDescriptor.Parse(target.DescriptorJson), restored)?.Catalog;` after it, and replace the later `var catalog = PolicyCatalog.Build(AlvoDescriptor.Parse(target.DescriptorJson), restored, _compiler);` with `var catalog = checkedCatalog ?? PolicyCatalog.Build(AlvoDescriptor.Parse(target.DescriptorJson), restored, _compiler);` — the checked catalog is built over the fresh `restored` instance, so the ABA rule holds;
- extend each of the three members' `DescriptorValidationException` doc with "…, or it would unbind an extension the current catalog binds." (`RollbackAsync` gains that `<exception>` line).

`src/MMLib.Alvo/AlvoServiceCollectionExtensions.cs`: replace `services.TryAddSingleton<RuntimeSchemaService>();` with

```csharp
        services.TryAddSingleton(provider => new RuntimeSchemaService(
            provider.GetRequiredService<IDescriptorValidator>(),
            provider.GetRequiredService<ISchemaMigrator>(),
            provider.GetRequiredService<IDescriptorVersionStore>(),
            provider.GetRequiredService<IRuntimeSchemaWriter>(),
            provider.GetRequiredService<ICelCompiler>(),
            provider.GetRequiredService<IPolicyCatalogProvider>(),
            provider.GetRequiredService<ExtensionApplyGuard>()));
```

(add the `using`s the file lacks). The factory is resolved lazily, as the type registration was, so a driver-less container still activates nothing until the first apply.

`src/MMLib.Alvo/Migrations/SchemaMigrationRunner.cs`: add the constructor parameter `ExtensionApplyGuard? extensions = null` (last), store it, and in `RunAsync` right after `LoadAsync` insert `_extensions?.Check(catalog);`. In the type remarks, state: guarded like every apply — a descriptor that would unbind a handler the current catalog binds is refused (`DescriptorValidationException`) before anything is planned; a handler that never bound stays unbound, logged, its events recorded dead. `DescriptorBootPlan.EnsureBindable`'s remark names `ApplyAlvoDescriptorAsync` as lenient: rewrite it to say the boot refuses every misfit through the binder, and every other apply path refuses a change that would unbind a bound handler through `ExtensionApplyGuard`.

- [ ] **Step 4: Run the tests to verify they pass**

Run: `dotnet test --project test/MMLib.Alvo.Tests --filter-class '*RuntimeSchemaServiceExtensionGuardTests'`, `--filter-class '*RuntimeSchemaServiceTests'`, `--filter-class '*RollbackPropertyTests'`, `--filter-class '*SchemaMigrationRunnerTests'`, `--filter-class '*PolicyCatalogPrimingTests'`; `dotnet test --project test/MMLib.Alvo.Api.Tests --filter-class '*ManagementExtensionsTests'`, `--filter-class '*ManagementApplyTests'`, `--filter-class '*ManagementRollbackTests'`.
Expected: PASS. Then `--filter-class '*PublicApiApprovalTests'` in `test/MMLib.Alvo.Tests`: PASS **unchanged** (the new constructor is internal).

- [ ] **Step 5: Format, ring0 + ring1, commit**

```bash
dotnet format whitespace MMLib.Alvo.slnx --include src/MMLib.Alvo/Migrations/RuntimeSchemaService.cs src/MMLib.Alvo/Migrations/SchemaMigrationRunner.cs src/MMLib.Alvo/Migrations/Internal/DescriptorBootPlan.cs src/MMLib.Alvo/AlvoServiceCollectionExtensions.cs test/MMLib.Alvo.Tests/Migrations/RuntimeSchemaWorld.cs test/MMLib.Alvo.Tests/Migrations/SchemaMigrationRunnerTests.cs test/MMLib.Alvo.Tests/Migrations/RuntimeSchemaServiceExtensionGuardTests.cs test/MMLib.Alvo.Api.Tests/Management/ManagementExtensionsTests.cs
scripts/test-ring0 && scripts/test-ring1
git add src/MMLib.Alvo/Migrations/RuntimeSchemaService.cs src/MMLib.Alvo/Migrations/SchemaMigrationRunner.cs src/MMLib.Alvo/Migrations/Internal/DescriptorBootPlan.cs src/MMLib.Alvo/AlvoServiceCollectionExtensions.cs test/MMLib.Alvo.Tests/Migrations/RuntimeSchemaWorld.cs test/MMLib.Alvo.Tests/Migrations/SchemaMigrationRunnerTests.cs test/MMLib.Alvo.Tests/Migrations/RuntimeSchemaServiceExtensionGuardTests.cs test/MMLib.Alvo.Api.Tests/Management/ManagementExtensionsTests.cs
git commit -m "feat(migrations): refuse an apply, dry run or rollback that would unbind a bound handler

Claude-Session: https://claude.ai/code/session_01NJAtafM29iddLwfhb5gnmB"
```

---

## Task 6: The dashboard's extension reads — gateway slot and `LifecycleLane`

**Files:**
- Modify: `src/MMLib.Alvo.Admin/Internal/ManagementGateway.cs`
- Create: `src/MMLib.Alvo.Admin/Components/Schema/LifecycleLane.cs`
- Test: `test/MMLib.Alvo.Admin.Tests/Internal/ManagementGatewayCacheTests.cs`, `test/MMLib.Alvo.Admin.Tests/Schema/LifecycleLaneTests.cs`

**Interfaces:**
- Consumes: Task 1's records, Task 3's `IAlvoManagement.GetExtensionsAsync`.
- Produces: `ValueTask<IReadOnlyList<ManagementExtension>> ManagementGateway.ExtensionsAsync(CancellationToken ct)` (cached per circuit, dropped by `Invalidate`, empty on a refused or failed read); `internal sealed record LaneHook(string Point, int Number, string Json)`; `internal static class LifecycleLane` with `IReadOnlyList<string> AfterCommitPoints`, `IReadOnlyList<LaneHook> AfterCommitHooks(IReadOnlyList<KeyValuePair<string, string>> hooks)`, `IReadOnlyList<ManagementExtension> Binding(string entity, IReadOnlyList<ManagementExtension> extensions)`, `IReadOnlyList<ManagementExtension> AfterCommit(string entity, IReadOnlyList<ManagementExtension> extensions)`, `string Summary(ManagementExtension extension)`, `string BadgeLabel(int count)`.

- [ ] **Step 1: Write the failing tests**

Add to `test/MMLib.Alvo.Admin.Tests/Internal/ManagementGatewayCacheTests.cs`:

```csharp
    [Fact]
    public async Task One_screen_reads_the_extension_catalog_once_and_a_navigation_reads_it_again()
    {
        var navigation = new TestNavigation();
        using var gateway = Gateway(navigation, out var management);
        management.GetExtensionsAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(new ManagementExtensionCatalog { Items = [Extension("notify-reschedule")] });

        (await gateway.ExtensionsAsync(Ct)).Single().Id.ShouldBe("notify-reschedule");
        await gateway.ExtensionsAsync(Ct);
        await management.Received(1).GetExtensionsAsync("default", Arg.Any<CancellationToken>());

        await navigation.GoToAsync("http://alvo.test/admin/schema");
        await gateway.ExtensionsAsync(Ct);
        await management.Received(2).GetExtensionsAsync("default", Arg.Any<CancellationToken>());
    }

    [Theory]
    [MemberData(nameof(ExpectedCatalogFailures))]
    public async Task An_extension_catalog_that_cannot_be_asked_answers_empty_and_is_asked_again(Exception failure)
    {
        using var gateway = Gateway(new TestNavigation(), out var management);
        management.GetExtensionsAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(
                _ => throw failure,
                _ => new ManagementExtensionCatalog { Items = [Extension("notify-reschedule")] });

        (await gateway.ExtensionsAsync(Ct)).ShouldBeEmpty();
        (await gateway.ExtensionsAsync(Ct)).ShouldHaveSingleItem("a failure to ask caches nothing");
    }

    private static ManagementExtension Extension(string id) =>
        new() { Id = id, Kind = AlvoExtensionKind.EventHandler, Stage = AlvoExtensionStage.PostCommit };
```

Create `test/MMLib.Alvo.Admin.Tests/Schema/LifecycleLaneTests.cs`:

```csharp
using MMLib.Alvo.Admin.Components.Schema;
using MMLib.Alvo.Management;

namespace MMLib.Alvo.Admin.Tests.Schema;

/// <summary>The Lifecycle tab's After commit lane and the entity list's badge, as logic.</summary>
public sealed class LifecycleLaneTests
{
    private static readonly ManagementExtension _reschedule = Extension("notify-reschedule", ["work_orders"], ["update"]) with
    {
        When = "changed(scheduled_for)",
        Timeout = TimeSpan.FromSeconds(30),
        RunAs = "system",
        Ordering = "perRecord",
    };

    private static readonly ManagementExtension _every = Extension("audit-every-save", ["*"], ["create", "update"]) with
    {
        Timeout = TimeSpan.FromMinutes(2),
        RunAs = "eventActor",
        Ordering = "none",
    };

    private static readonly ManagementExtension _application = Extension("fleet-alerted", [], []) with { Event = "fleet.alerted" };

    [Fact]
    public void An_entity_is_bound_by_its_own_extensions_and_by_every_entity_ones()
    {
        IReadOnlyList<ManagementExtension> all = [_reschedule, _every, _application];

        LifecycleLane.Binding("work_orders", all).Select(item => item.Id).ShouldBe(["notify-reschedule", "audit-every-save"]);
        LifecycleLane.Binding("regions", all).Select(item => item.Id).ShouldBe(["audit-every-save"]);
    }

    [Fact]
    public void The_after_commit_lane_takes_the_post_commit_extensions_of_the_entity()
        => LifecycleLane.AfterCommit("work_orders", [_reschedule, _every, _application]).Count.ShouldBe(2);

    [Fact]
    public void The_lane_takes_only_the_after_hooks_numbered_from_one()
    {
        IReadOnlyList<KeyValuePair<string, string>> hooks =
        [
            new("beforeUpdate", """[{"action":{"reject":"no"}}]"""),
            new("afterUpdate", """[{"action":{"type":"webhook","endpoint":"desk"}},{"condition":"changed(status)","action":{"type":"webhook","endpoint":"desk"}}]"""),
            new("afterDelete", "[]"),
        ];

        var lane = LifecycleLane.AfterCommitHooks(hooks);

        lane.Select(hook => (hook.Point, hook.Number)).ShouldBe([("afterUpdate", 1), ("afterUpdate", 2)]);
        lane[1].Json.ShouldContain("changed(status)");
    }

    [Fact]
    public void A_summary_says_where_when_how_long_and_as_whom()
    {
        LifecycleLane.Summary(_reschedule).ShouldBe("on update · timeout 30 s · runs as the system · in order per record");
        LifecycleLane.Summary(_every).ShouldBe("every entity · on create or update · timeout 2 min · runs as the event's actor · in any order");
    }

    [Theory]
    [InlineData(1, "1 extension from code runs on this entity")]
    [InlineData(2, "2 extensions from code run on this entity")]
    public void The_badge_label_counts_in_words(int count, string label)
        => LifecycleLane.BadgeLabel(count).ShouldBe(label);

    private static ManagementExtension Extension(string id, IReadOnlyList<string> entities, IReadOnlyList<string> operations) => new()
    {
        Id = id,
        Kind = AlvoExtensionKind.EventHandler,
        Stage = AlvoExtensionStage.PostCommit,
        Entities = entities,
        Operations = operations,
    };
}
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test --project test/MMLib.Alvo.Admin.Tests --filter-class '*LifecycleLaneTests'`
Expected: build FAIL — `LifecycleLane` and `ManagementGateway.ExtensionsAsync` do not exist.

- [ ] **Step 3: Implement**

`src/MMLib.Alvo.Admin/Internal/ManagementGateway.cs`: add `private readonly Slot<ManagementExtensionCatalog> _extensions = new();` beside `_functions`, `_extensions.Value = null;` in `Invalidate`, and after `CelFunctionsAsync`:

```csharp
    /// <summary>The host code registered with this instance — its extensions, as metadata (HX-04 <c>extensions</c>).</summary>
    /// <remarks>
    /// Cached like the function catalog, and for the same reason empty when it cannot be read: a badge and a read-only lane
    /// are helpers, and a helper must never be the reason a screen does not draw.
    /// </remarks>
    public async ValueTask<IReadOnlyList<ManagementExtension>> ExtensionsAsync(CancellationToken ct)
    {
        try
        {
            var project = await ProjectAsync(ct).ConfigureAwait(false);
            var answer = await CachedAsync(_extensions, () => management.GetExtensionsAsync(project, ct), ct)
                .ConfigureAwait(false);
            return answer.Items;
        }
        catch (Exception ex) when (ex is ManagementRequestException or ManagementForbiddenException
            or OperationCanceledException or HttpRequestException)
        {
            return [];
        }
    }
```

`src/MMLib.Alvo.Admin/Components/Schema/LifecycleLane.cs`:

```csharp
using MMLib.Alvo.Management;

using System.Globalization;
using System.Text.Json.Nodes;

namespace MMLib.Alvo.Admin.Components.Schema;

/// <summary>One descriptor after-hook as the After commit lane draws it: its point, its place there, its JSON.</summary>
/// <param name="Point">The hook point, e.g. <c>afterUpdate</c>.</param>
/// <param name="Number">Its place among the point's hooks, from one.</param>
/// <param name="Json">The hook as declared.</param>
internal sealed record LaneHook(string Point, int Number, string Json);

/// <summary>
/// What runs on an entity after a commit, and the entity list's count of host code — the logic behind the Lifecycle tab
/// and the <c>⟨/⟩ n</c> badge, kept out of the markup so it can be tested.
/// </summary>
internal static class LifecycleLane
{
    private const string AllEntities = "*";
    private const string Separator = " · ";

    /// <summary>The descriptor's after-commit hook points, in the order the lane draws them.</summary>
    public static IReadOnlyList<string> AfterCommitPoints { get; } = ["afterCreate", "afterUpdate", "afterDelete"];

    /// <summary>The descriptor's after-hooks, from the working copy's hooks of one entity, point by point.</summary>
    public static IReadOnlyList<LaneHook> AfterCommitHooks(IReadOnlyList<KeyValuePair<string, string>> hooks) =>
        [.. AfterCommitPoints.SelectMany(point => hooks.Where(pair => pair.Key == point).SelectMany(pair => Declared(point, pair.Value)))];

    /// <summary>The extensions that run on <paramref name="entity"/>: its own, and every every-entity one.</summary>
    public static IReadOnlyList<ManagementExtension> Binding(string entity, IReadOnlyList<ManagementExtension> extensions) =>
        [.. extensions.Where(extension =>
            extension.Entities.Contains(entity, StringComparer.Ordinal) || extension.Entities.Contains(AllEntities, StringComparer.Ordinal))];

    /// <summary>The extensions the After commit lane draws for <paramref name="entity"/>.</summary>
    public static IReadOnlyList<ManagementExtension> AfterCommit(string entity, IReadOnlyList<ManagementExtension> extensions) =>
        [.. Binding(entity, extensions).Where(extension => extension.Stage == AlvoExtensionStage.PostCommit)];

    /// <summary>One line saying where, when, how long and as whom an extension runs.</summary>
    public static string Summary(ManagementExtension extension) => string.Join(Separator, Parts(extension));

    /// <summary>What the badge says to a screen reader and in its tooltip.</summary>
    public static string BadgeLabel(int count) => count == 1
        ? "1 extension from code runs on this entity"
        : string.Create(CultureInfo.InvariantCulture, $"{count} extensions from code run on this entity");

    private static IEnumerable<LaneHook> Declared(string point, string json) =>
        (JsonNode.Parse(json) as JsonArray ?? []).Select((hook, index) => new LaneHook(point, index + 1, hook?.ToJsonString() ?? "null"));

    private static IEnumerable<string> Parts(ManagementExtension extension)
    {
        if (extension.Entities.Contains(AllEntities, StringComparer.Ordinal))
        {
            yield return "every entity";
        }

        if (extension.Operations.Count > 0)
        {
            yield return "on " + string.Join(" or ", extension.Operations);
        }

        if (extension.Timeout is { } timeout)
        {
            yield return "timeout " + Duration(timeout);
        }

        if (extension.RunAs is { } runAs)
        {
            yield return runAs == "eventActor" ? "runs as the event's actor" : "runs as the system";
        }

        if (extension.Ordering is { } ordering)
        {
            yield return ordering == "none" ? "in any order" : "in order per record";
        }
    }

    private static string Duration(TimeSpan value) =>
        value < TimeSpan.FromMinutes(1) ? Whole(value.TotalSeconds, "s")
        : value < TimeSpan.FromHours(1) ? Whole(value.TotalMinutes, "min")
        : Whole(value.TotalHours, "h");

    private static string Whole(double amount, string unit) =>
        string.Create(CultureInfo.InvariantCulture, $"{amount:0.##} {unit}");
}
```

- [ ] **Step 4: Run the tests to verify they pass**

Run: `dotnet test --project test/MMLib.Alvo.Admin.Tests --filter-class '*LifecycleLaneTests'` and `--filter-class '*ManagementGatewayCacheTests'`.
Expected: PASS.

- [ ] **Step 5: Format, ring0, commit**

```bash
dotnet format whitespace MMLib.Alvo.slnx --include src/MMLib.Alvo.Admin/Internal/ManagementGateway.cs src/MMLib.Alvo.Admin/Components/Schema/LifecycleLane.cs test/MMLib.Alvo.Admin.Tests/Internal/ManagementGatewayCacheTests.cs test/MMLib.Alvo.Admin.Tests/Schema/LifecycleLaneTests.cs
scripts/test-ring0
git add src/MMLib.Alvo.Admin/Internal/ManagementGateway.cs src/MMLib.Alvo.Admin/Components/Schema/LifecycleLane.cs test/MMLib.Alvo.Admin.Tests/Internal/ManagementGatewayCacheTests.cs test/MMLib.Alvo.Admin.Tests/Schema/LifecycleLaneTests.cs
git commit -m "feat(admin): read the extension catalog and shape the after-commit lane

Claude-Session: https://claude.ai/code/session_01NJAtafM29iddLwfhb5gnmB"
```

---

## Task 7: The badge, the *Lifecycle* tab and Overview's honoured count

**Files:**
- Create: `src/MMLib.Alvo.Admin/Components/Schema/LifecycleTab.razor`
- Modify: `src/MMLib.Alvo.Admin/Components/Schema/EntityTabs.cs`, `Entity.razor`, `Entity.razor.cs`, `SchemaList.razor`, `src/MMLib.Alvo.Admin/Components/Home/Overview.razor`
- Test: `test/MMLib.Alvo.Admin.Tests/Schema/EntityTabsTests.cs`

**Interfaces:**
- Consumes: Task 6's `ManagementGateway.ExtensionsAsync`, `LifecycleLane.*`; the existing `Copy.HooksOf(entity)` (`_hooks` in `Entity.razor.cs`), `AdminPaths.Entity(name, tab)`, `EntityTabs.FromSlug`.
- Produces: `EntityTabs.Lifecycle = "lifecycle"`, drawn after *On write*; test ids `entity-extensions-<entity>`, `lifecycle-after-commit`, `lifecycle-empty`, `lane-hook-<point>-<n>`, `lane-hook-edit`, `lane-extension-<id>`, `lane-extension-summary`, `lane-extension-when`.

- [ ] **Step 1: Write the failing test**

In `test/MMLib.Alvo.Admin.Tests/Schema/EntityTabsTests.cs` add `[InlineData("https://host/admin/schema/work_orders?tab=lifecycle", "Lifecycle")]` to the theory, and:

```csharp
    [Fact]
    public void The_lifecycle_tab_follows_on_write()
    {
        var slugs = EntityTabs.All.Select(tab => tab.Slug).ToList();

        slugs.IndexOf(EntityTabs.Lifecycle).ShouldBe(slugs.IndexOf(EntityTabs.OnWrite) + 1);
    }
```

- [ ] **Step 2: Run the test to verify it fails**

Run: `dotnet test --project test/MMLib.Alvo.Admin.Tests --filter-class '*EntityTabsTests'`
Expected: build FAIL — `EntityTabs.Lifecycle` does not exist.

- [ ] **Step 3: Implement**

`EntityTabs.cs`: add

```csharp
    /// <summary>The Lifecycle tab's slug.</summary>
    public const string Lifecycle = "lifecycle";
```

and `new("Lifecycle", Lifecycle),` right after `new("On write", OnWrite),` in `All`.

`LifecycleTab.razor`:

```razor
@*
    What runs on this entity, by when it runs. This build draws one lane, After commit: the descriptor's after-hooks,
    which the On write tab edits (one editor, not two), and the host's event handlers, which live in its C# and are
    read-only here — marked by the accent badge every piece of host code wears, as the function list's "this host"
    does. The other lanes appear with the host code that can run in them.
*@
<div data-testid="lifecycle-after-commit">
    <SectionHead Title="After commit">
        <Subtitle>
            Runs once the write has committed, from the outbox: it can never undo the write, and it may leave the process.
            This tab shows what runs after the commit only — before-hooks, which run inside the transaction, are on the
            On write tab.
        </Subtitle>
    </SectionHead>

    @if (_hooks.Count == 0 && _extensions.Count == 0)
    {
        <div data-testid="lifecycle-empty">
            <EmptyState Title="Nothing runs after a commit on this entity"
                        Body="No after-hook is declared, and the host registers no event handler for it." />
        </div>
    }

    @foreach (var hook in _hooks)
    {
        <ListRow class="a-listrow--roomy a-listrow--stacked" data-testid="@($"lane-hook-{hook.Point}-{hook.Number}")">
            <div class="a-row">
                <span class="a-badge">descriptor</span>
                <span class="a-mono a-name">@hook.Point</span>
                <span class="a-section__sub">hook @hook.Number</span>
            </div>
            <CodeBlock Json="@hook.Json" />
            <a href="@AdminPaths.Entity(Entity, EntityTabs.FromSlug(EntityTabs.OnWrite))" data-testid="lane-hook-edit">Change it on the On write tab</a>
        </ListRow>
    }

    @foreach (var extension in _extensions)
    {
        <ListRow class="a-listrow--roomy a-listrow--stacked" data-testid="@($"lane-extension-{extension.Id}")">
            <div class="a-row">
                <span class="a-badge a-badge--accent">⟨/⟩ from code</span>
                <span class="a-mono a-name">@extension.Id</span>
            </div>
            @if (extension.Description is { } description)
            {
                <span class="a-meta">@description</span>
            }
            <span class="a-section__sub" data-testid="lane-extension-summary">@LifecycleLane.Summary(extension)</span>
            @if (extension.When is { } when)
            {
                <code class="a-mono" data-testid="lane-extension-when">when @when</code>
            }
            <p class="a-note">Registered in the host's C#: it is changed there, never here.</p>
        </ListRow>
    }
</div>

@code {
    private IReadOnlyList<LaneHook> _hooks = [];
    private IReadOnlyList<ManagementExtension> _extensions = [];

    /// <summary>The entity whose lane this is.</summary>
    [Parameter, EditorRequired]
    public string Entity { get; set; } = string.Empty;

    /// <summary>The entity's hooks in the working copy, by point, as the On write tab reads them.</summary>
    [Parameter]
    public IReadOnlyList<KeyValuePair<string, string>> Hooks { get; set; } = [];

    /// <summary>Every extension this instance registers; the lane keeps the ones that run on <see cref="Entity"/>.</summary>
    [Parameter]
    public IReadOnlyList<ManagementExtension> Extensions { get; set; } = [];

    protected override void OnParametersSet()
    {
        _hooks = LifecycleLane.AfterCommitHooks(Hooks);
        _extensions = LifecycleLane.AfterCommit(Entity, Extensions);
    }
}
```

If `StylesheetHygieneTests` or `ComponentLayerTests` refuses something here (an in-app anchor needing the class other in-app links carry, say), follow the rule it states and reuse an existing class — add no CSS.

`Entity.razor.cs`: add `private IReadOnlyList<ManagementExtension> _extensions = [];` beside `_refused`, and in `ReadAsync` right after `_refused = capabilities.Refused;` add `_extensions = await Gateway.ExtensionsAsync(ct);`.

`Entity.razor` `TabContent`: after the `EntityTabs.OnWrite` case add

```razor
            case EntityTabs.Lifecycle:
                <LifecycleTab @key="EntityName" Entity="@EntityName" Hooks="@_hooks" Extensions="@_extensions" />
                break;
```

`SchemaList.razor`: add `private IReadOnlyList<ManagementExtension> _extensions = [];` to `@code`, `_extensions = await Gateway.ExtensionsAsync(ct);` right after `_schema = await Gateway.SchemaAsync(ct);`, and in `EntityRow`, after the `@entity.Fields.Count fields` badge:

```razor
            @if (LifecycleLane.Binding(entity.Name, _extensions).Count is > 0 and var bound)
            {
                <span class="a-badge a-badge--accent" data-testid="@($"entity-extensions-{entity.Name}")"
                      title="@LifecycleLane.BadgeLabel(bound)" aria-label="@LifecycleLane.BadgeLabel(bound)">⟨/⟩ @bound</span>
            }
```

`Overview.razor`: the *Honoured blocks* stat counts blocks only, since the report now appends host kinds — change its `Value` to `@_capabilities!.Honoured.Count(block => !block.Contains('.', StringComparison.Ordinal)).ToString(CultureInfo.InvariantCulture)`.

- [ ] **Step 4: Run the tests to verify they pass**

Run: `dotnet test --project test/MMLib.Alvo.Admin.Tests` (whole project: `EntityTabsTests`, `BoundaryArchitectureTests` — acceptance (5) — `ComponentLayerTests`, `StylesheetHygieneTests`, `EndToEndSelectorTests`).
Expected: PASS.

- [ ] **Step 5: Format, ring0, commit**

```bash
dotnet format whitespace MMLib.Alvo.slnx --include src/MMLib.Alvo.Admin/Components/Schema/EntityTabs.cs src/MMLib.Alvo.Admin/Components/Schema/Entity.razor.cs test/MMLib.Alvo.Admin.Tests/Schema/EntityTabsTests.cs
scripts/test-ring0
git add src/MMLib.Alvo.Admin/Components/Schema/LifecycleTab.razor src/MMLib.Alvo.Admin/Components/Schema/EntityTabs.cs src/MMLib.Alvo.Admin/Components/Schema/Entity.razor src/MMLib.Alvo.Admin/Components/Schema/Entity.razor.cs src/MMLib.Alvo.Admin/Components/Schema/SchemaList.razor src/MMLib.Alvo.Admin/Components/Home/Overview.razor test/MMLib.Alvo.Admin.Tests/Schema/EntityTabsTests.cs
git commit -m "feat(admin): badge entities with their extensions and draw the after-commit lane

Claude-Session: https://claude.ai/code/session_01NJAtafM29iddLwfhb5gnmB"
```

---

## Task 8: The dashboard end to end — badge, read-only lane, preview refusal **[slice end]**

**Files:**
- Create: `test/MMLib.Alvo.Admin.Tests.EndToEnd/ExtensionWorld.cs`, `test/MMLib.Alvo.Admin.Tests.EndToEnd/ExtensionScenarios.cs`

**Interfaces:**
- Consumes: `AdminWorld.Configure(IAlvoBuilder)` (the test-side builder seam `HostFunctionWorld` uses), `AdminSession` helpers (`GoAsync`, `Dialog`, `AssertNoHorizontalScrollAsync`, `AssertConsoleClean`), Task 7's test ids, Task 5's refusal.
- Produces: `ExtensionWorld` over `examples/field-service` with `notify-reschedule` (`work_orders`, `OnUpdated`, `when: "changed(scheduled_for)"`) and `audit-every-save` (`"*"`, `OnSaved`).

- [ ] **Step 1: Write the scenarios**

`test/MMLib.Alvo.Admin.Tests.EndToEnd/ExtensionWorld.cs`:

```csharp
using Microsoft.Extensions.DependencyInjection;

using MMLib.Alvo.Data;
using MMLib.Alvo.Events;

namespace MMLib.Alvo.Admin.Tests.EndToEnd;

/// <summary>
/// The field-service world with two event handlers registered as a host developer would: one on <c>work_orders</c>
/// reading <c>scheduled_for</c>, which nothing in the descriptor names, and one on every entity.
/// </summary>
/// <remarks>Registered through <see cref="AdminWorld"/>'s test-side builder seam; the shipped host gains none.</remarks>
public sealed class ExtensionWorld : AdminWorld
{
    internal const string Reschedule = "notify-reschedule";
    internal const string Audit = "audit-every-save";
    internal const string RescheduleDescription = "Tells the dispatcher when a work order is rescheduled.";

    /// <inheritdoc/>
    protected override void Configure(IAlvoBuilder alvo) => alvo
        .Entity("work_orders", orders => orders.OnUpdated(
            Reschedule,
            (AlvoUpdated<AlvoRecord> _) => Task.CompletedTask,
            when: "changed(scheduled_for)",
            configure: options => options.Description = RescheduleDescription))
        .Entity("*", every => every.OnSaved(
            Audit,
            (AlvoSaved<AlvoRecord> _) => Task.CompletedTask,
            configure: options => options.Description = "Keeps an audit line for every saved record."));
}
```

`test/MMLib.Alvo.Admin.Tests.EndToEnd/ExtensionScenarios.cs`:

```csharp
using Microsoft.Playwright;

namespace MMLib.Alvo.Admin.Tests.EndToEnd;

/// <summary>Host code on the dashboard (spec 2026-10-14 HX-04 acceptance (3)): the entity list's count and the read-only lane.</summary>
/// <param name="world">The field-service world with two handlers, and a browser.</param>
public sealed class ExtensionCatalogScenarios(ExtensionWorld world) : IClassFixture<ExtensionWorld>
{
    [Fact(Timeout = AdminWorld.ScenarioTimeout)]
    public async Task The_entity_list_counts_the_extensions_that_run_on_each_entity()
    {
        await using var session = await world.SignInAsync(TestContext.Current.CancellationToken);
        await session.GoAsync("/schema");

        var orders = session.Page.GetByTestId("entity-extensions-work_orders");
        await orders.WaitForAsync();
        (await orders.InnerTextAsync()).Trim().ShouldBe("⟨/⟩ 2");
        (await orders.GetAttributeAsync("aria-label")).ShouldBe("2 extensions from code run on this entity");
        (await session.Page.GetByTestId("entity-extensions-regions").InnerTextAsync()).Trim()
            .ShouldBe("⟨/⟩ 1", "an every-entity handler runs on regions too");
        session.AssertConsoleClean();
    }

    [Fact(Timeout = AdminWorld.ScenarioTimeout)]
    public async Task The_lifecycle_tab_shows_code_after_the_commit_read_only()
    {
        await using var session = await world.SignInAsync(TestContext.Current.CancellationToken, width: 375);
        await session.GoAsync("/schema/work_orders?tab=lifecycle");

        var lane = session.Page.GetByTestId("lifecycle-after-commit");
        await lane.WaitForAsync();
        var item = lane.GetByTestId($"lane-extension-{ExtensionWorld.Reschedule}");
        var text = await item.InnerTextAsync();
        text.ShouldContain("from code");
        text.ShouldContain(ExtensionWorld.RescheduleDescription);
        (await item.GetByTestId("lane-extension-when").InnerTextAsync()).Trim().ShouldBe("when changed(scheduled_for)");
        (await item.GetByTestId("lane-extension-summary").InnerTextAsync()).Trim()
            .ShouldBe("on update · timeout 30 s · runs as the system · in order per record");
        (await item.GetByRole(AriaRole.Button).CountAsync()).ShouldBe(0, "host code is read-only on the dashboard");
        (await lane.GetByTestId($"lane-extension-{ExtensionWorld.Audit}").InnerTextAsync()).ShouldContain("every entity");
        await session.AssertNoHorizontalScrollAsync();
        session.AssertConsoleClean();
    }
}

/// <summary>The apply preview names the extension a change would unbind (spec 2026-10-14 §4.13, apply preview row).</summary>
/// <remarks>Its own world, because it stages.</remarks>
/// <param name="world">The field-service world with two handlers, and a browser.</param>
public sealed class ExtensionApplyRefusalScenarios(ExtensionWorld world) : IClassFixture<ExtensionWorld>
{
    [Fact(Timeout = AdminWorld.ScenarioTimeout)]
    public async Task Removing_a_field_an_extension_reads_is_refused_at_preview_naming_the_extension()
    {
        await using var session = await world.SignInAsync(TestContext.Current.CancellationToken);
        await session.GoAsync("/schema/work_orders");
        await session.Page.GetByTestId("remove-field-scheduled_for").ClickAsync();
        await session.Dialog("remove-field-sheet").GetByTestId("remove-field-anyway").ClickAsync();

        await session.Page.ClickAsync("[data-testid='pending-preview']");
        var refusal = session.Page.GetByTestId("error-panel");
        await refusal.WaitForAsync();

        var said = await refusal.InnerTextAsync();
        said.ShouldContain("The descriptor is not valid");
        said.ShouldContain("/entities/work_orders/fields/scheduled_for");
        said.ShouldContain($"'{ExtensionWorld.Reschedule}' (eventHandler, in this host)");
        (await session.Page.GetByTestId("plan").CountAsync()).ShouldBe(0, "a refused descriptor has no plan");
        session.AssertConsoleClean();
    }
}
```

- [ ] **Step 2: Run them**

Run: `scripts/test-admin-e2e` (whole suite: the Lifecycle tab joins every entity screen and the gateway reads one more slot on each). To iterate on the three alone, after `scripts/test-admin-e2e` has installed the browser once: `dotnet test --project test/MMLib.Alvo.Admin.Tests.EndToEnd --filter-class '*ExtensionCatalogScenarios'` and `--filter-class '*ExtensionApplyRefusalScenarios'`.
Expected: PASS. A failure on `AssertNoHorizontalScrollAsync` is fixed in `LifecycleTab.razor` by reusing a wrapping class the On write tab's rows use (`a-hookrow` with its `CodeBlock` wraps at 375 px), never by loosening the test.

- [ ] **Step 3: Format, rings, commit**

```bash
dotnet format whitespace MMLib.Alvo.slnx --include test/MMLib.Alvo.Admin.Tests.EndToEnd/ExtensionWorld.cs test/MMLib.Alvo.Admin.Tests.EndToEnd/ExtensionScenarios.cs
scripts/test-ring0 && scripts/test-ring1
git add test/MMLib.Alvo.Admin.Tests.EndToEnd/ExtensionWorld.cs test/MMLib.Alvo.Admin.Tests.EndToEnd/ExtensionScenarios.cs
git commit -m "test(admin): drive the extension badge, lane and preview refusal end to end

Claude-Session: https://claude.ai/code/session_01NJAtafM29iddLwfhb5gnmB"
```

---

## Task 9: The assistant's `get_extensions` tool

**Files:**
- Modify: `src/MMLib.Alvo.Ai/Internal/ManagementTools.cs` (the tool, and `DescriptorView.HostExtensions`), `src/MMLib.Alvo.Ai/Instructions/schema-assistant.md`, `src/MMLib.Alvo.Ai/Internal/AssistantInstructions.cs`
- Test: `test/MMLib.Alvo.Ai.Tests/ManagementToolsTests.cs`, `test/MMLib.Alvo.Ai.Tests/AlvoAssistantTests.cs`

**Interfaces:**
- Consumes: Task 3's `IAlvoManagement.GetExtensionsAsync`.
- Produces: tool `get_extensions` (no arguments), answering the catalog as JSON through the same `AnsweredAsync`/`Json` path the other reads use; `get_descriptor`'s answer gains `hostExtensions` — per entity name (or `*`), how many host extensions run on it, `{}` when none — read through the port inside the same tool call, so the model learns *whether* to call `get_extensions` without spending a call against the eval's `MaximumToolCalls` (6); instructions version `<!-- alvo-schema-assistant v7 -->`.

- [ ] **Step 1: Write the failing tests**

`ManagementToolsTests.cs`: rename `The_tool_set_is_exactly_the_five_reads_and_the_two_dry_runs` to `The_tool_set_is_exactly_the_six_reads_and_the_two_dry_runs` (its summary: "exactly these eight tools"), expected list `["check_change", "get_capabilities", "get_cel_functions", "get_descriptor", "get_extensions", "get_revisions", "get_schema", "propose_change"]`. Add:

```csharp
    /// <summary>The extensions tool reads through the management port, with the operator's authority and the project's name.</summary>
    [Fact]
    public async Task The_extensions_tool_answers_with_what_the_management_port_lists()
    {
        var management = Substitute.For<IAlvoManagement>();
        management.GetExtensionsAsync(Project, Arg.Any<CancellationToken>()).Returns(new ManagementExtensionCatalog
        {
            Items =
            [
                new ManagementExtension
                {
                    Id = "open-damage-claim",
                    Kind = AlvoExtensionKind.EventHandler,
                    Stage = AlvoExtensionStage.PostCommit,
                    Entities = ["rentals"],
                    Operations = ["update"],
                    When = "changed(damage_report)",
                },
            ],
        });

        var answer = await InvokeAsync(management, "get_extensions", []);

        answer.ShouldContain("open-damage-claim");
        answer.ShouldContain("changed(damage_report)");
        answer.ShouldContain("eventHandler");
    }

    /// <summary>The descriptor read says per entity how many host extensions run there, so a hook case spends no extra call.</summary>
    [Fact]
    public async Task The_descriptor_read_counts_the_host_extensions_per_entity()
    {
        var management = Serving(Descriptor);
        management.GetExtensionsAsync(Project, Arg.Any<CancellationToken>()).Returns(new ManagementExtensionCatalog
        {
            Items =
            [
                new ManagementExtension { Id = "a", Kind = AlvoExtensionKind.EventHandler, Stage = AlvoExtensionStage.PostCommit, Entities = ["rentals"] },
                new ManagementExtension { Id = "b", Kind = AlvoExtensionKind.EventHandler, Stage = AlvoExtensionStage.PostCommit, Entities = ["*"] },
                new ManagementExtension { Id = "c", Kind = AlvoExtensionKind.EventHandler, Stage = AlvoExtensionStage.PostCommit, Event = "fleet.alerted" },
            ],
        });

        var counts = JsonNode.Parse(await InvokeAsync(management, "get_descriptor", []))!["hostExtensions"]!.AsObject();

        counts.Select(pair => (pair.Key, pair.Value!.GetValue<int>())).ShouldBe([("rentals", 1), ("*", 1)], ignoreOrder: true);
    }

    [Fact]
    public async Task A_host_without_extensions_reads_an_empty_count()
        => JsonNode.Parse(await InvokeAsync(Serving(Descriptor), "get_descriptor", []))!["hostExtensions"]
            .ShouldBeOfType<JsonObject>().ShouldBeEmpty();

    /// <summary>A caller the access block does not admit is told so, not crashed.</summary>
    [Fact]
    public async Task A_forbidden_caller_is_reported_by_the_extensions_tool_too()
    {
        var management = Substitute.For<IAlvoManagement>();
        management.GetExtensionsAsync(Project, Arg.Any<CancellationToken>()).Throws(new ManagementForbiddenException());

        (await InvokeAsync(management, "get_extensions", [])).ShouldContain("forbidden");
    }
```

`AlvoAssistantTests.cs`: the trace's expected `"alvo-schema-assistant v6"` becomes `"alvo-schema-assistant v7"`.

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test --project test/MMLib.Alvo.Ai.Tests --filter-class '*ManagementToolsTests'`
Expected: FAIL — seven tools, and `get_extensions` is not found.

- [ ] **Step 3: Implement**

`ManagementTools.cs`: after the `get_cel_functions` entry in `Functions` add

```csharp
            AIFunctionFactory.Create(
                GetExtensionsAsync,
                "get_extensions",
                "The host code (extensions) this host registers — event handlers with the entities, operations and condition "
                + "each binds — as read-only metadata. When get_descriptor's hostExtensions names an entity, read it before "
                + "proposing an after-hook there: name an extension that already does what is asked instead of adding one. A "
                + "field or entity an extension reads cannot be removed or left out of event images; the dry run refuses it, "
                + "naming the extension."),
```

`GetDescriptorAsync` reads both through the port in one tool call:

```csharp
    private Task<string> GetDescriptorAsync(CancellationToken ct) =>
        AnsweredAsync(async () => Json(DescriptorView.Of(
            await _management.GetDescriptorAsync(_project, ct).ConfigureAwait(false),
            await _management.GetExtensionsAsync(_project, ct).ConfigureAwait(false))));
```

and `DescriptorView` (same file) gains the count — `extensions` may be `null` from a test double that does not configure the call:

```csharp
internal sealed record DescriptorView(string Project, int Revision, JsonNode? Descriptor)
{
    /// <summary>Gets, per entity name or <c>*</c>, how many host extensions run on it; empty when the host registers none.</summary>
    public IReadOnlyDictionary<string, int> HostExtensions { get; init; } = new Dictionary<string, int>(StringComparer.Ordinal);

    internal static DescriptorView Of(ManagementDescriptor descriptor, ManagementExtensionCatalog? extensions) =>
        new(descriptor.Project, descriptor.Revision, JsonNode.Parse(descriptor.DescriptorJson)) { HostExtensions = Counted(extensions) };

    private static Dictionary<string, int> Counted(ManagementExtensionCatalog? extensions) =>
        (extensions?.Items ?? [])
            .SelectMany(extension => extension.Entities)
            .GroupBy(entity => entity, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.Count(), StringComparer.Ordinal);
}
```

(Document the new `<param>`/property; keep the existing ones.) Then after `GetCelFunctionsAsync`:

```csharp
    /// <summary>The host's registered extensions, so a proposal neither duplicates nor unbinds one.</summary>
    private Task<string> GetExtensionsAsync(CancellationToken ct) =>
        AnsweredAsync(async () => Json(await _management.GetExtensionsAsync(_project, ct).ConfigureAwait(false)));
```

`schema-assistant.md`: first line `<!-- alvo-schema-assistant v7 -->`; the `get_descriptor` bullet ends "…, with its `revision` and `hostExtensions` (how many host extensions run per entity)."; in *Your tools*, after the `get_cel_functions` bullet:

```markdown
- `get_extensions` — the host code (extensions) this host registers and what each one binds; read-only.
```

and under *You cannot*, after "Read or change data rows, or apply anything.":

```markdown
- Change host code (extensions): `get_extensions` shows it, and only the host's developer changes it.
```

`AssistantInstructions.cs`: `VersionLine = "<!-- alvo-schema-assistant v7 -->"`.

- [ ] **Step 4: Run the tests to verify they pass**

Run: `dotnet test --project test/MMLib.Alvo.Ai.Tests` (whole project: `AssistantInstructionsTests` holds the tool bullets to the registered set) and `dotnet test --project test/MMLib.Alvo.Host.Tests --filter-class '*InstructionExampleOutcomeTests'` (it drives the real tools).
Expected: PASS. `--filter-class '*PublicApiApprovalTests'` in `test/MMLib.Alvo.Ai.Tests`: PASS unchanged.

- [ ] **Step 5: Format, ring0, commit**

```bash
dotnet format whitespace MMLib.Alvo.slnx --include src/MMLib.Alvo.Ai/Internal/ManagementTools.cs src/MMLib.Alvo.Ai/Internal/AssistantInstructions.cs test/MMLib.Alvo.Ai.Tests/ManagementToolsTests.cs test/MMLib.Alvo.Ai.Tests/AlvoAssistantTests.cs
scripts/test-ring0
git add src/MMLib.Alvo.Ai/Internal/ManagementTools.cs src/MMLib.Alvo.Ai/Internal/AssistantInstructions.cs src/MMLib.Alvo.Ai/Instructions/schema-assistant.md test/MMLib.Alvo.Ai.Tests/ManagementToolsTests.cs test/MMLib.Alvo.Ai.Tests/AlvoAssistantTests.cs
git commit -m "feat(ai): give the assistant a get_extensions read

Claude-Session: https://claude.ai/code/session_01NJAtafM29iddLwfhb5gnmB"
```

---

## Task 10: The descriptor skills — "descriptor or code", `events.exclude`, after-hook caller values

**Files:**
- Modify: `plugins/alvo/skills/alvo-descriptor-hooks/SKILL.md`, `plugins/alvo/skills/alvo-descriptor-entities-and-fields/SKILL.md`, `plugins/alvo/skills/alvo-descriptor-capabilities-and-limits/SKILL.md`
- Test: `test/MMLib.Alvo.Ai.Tests/SkillRegionTests.cs`

**Interfaces:**
- Consumes: Task 9's `get_extensions` tool name.
- Produces: region `<!-- gen:descriptor-or-code -->` in the hooks skill whose tokens are `get_descriptor`, `hostExtensions`, `*`, `get_extensions`, held by `SkillRegionTests.The_descriptor_or_code_region_names_the_extension_read`.

- [ ] **Step 1: Write the failing test**

In `test/MMLib.Alvo.Ai.Tests/SkillRegionTests.cs`: append `"descriptor-or-code"` to `_heldHere`, and add (with `using MMLib.Alvo.Ai.Internal;`, `using MMLib.Alvo.Management;`, `using NSubstitute;` where missing):

```csharp
    /// <summary>The hooks skill's "descriptor or code" region names exactly the read the assistant really has.</summary>
    [Fact]
    public void The_descriptor_or_code_region_names_the_extension_read()
    {
        Holds("hooks", "descriptor-or-code", ["get_descriptor", "hostExtensions", "*", "get_extensions"]);
        ManagementTools.For(Substitute.For<IAlvoManagement>(), "p").Functions.Select(tool => tool.Name).ShouldContain("get_extensions");
    }
```

- [ ] **Step 2: Run the test to verify it fails**

Run: `dotnet test --project test/MMLib.Alvo.Ai.Tests --filter-class '*SkillRegionTests'`
Expected: FAIL — `alvo-descriptor-hooks has no <!-- gen:descriptor-or-code --> region` (and the region registry fact fails until Step 3).

- [ ] **Step 3: Edit the skills**

`alvo-descriptor-hooks/SKILL.md` is 6 061 bytes of a 6 144 cap, so the region costs two sentences that are for the host developer, not the descriptor author — both are kept in `website/src/content/docs/guides/custom-cel-functions.mdx` ("Tenant-aware", "A new meaning gets a new name") — and one parenthetical the `cel-mutate` region already shows by example:

1. In the paragraph that begins "An embedded host may register its own CEL functions", delete its last two sentences — from " A function whose meaning changes gets a new name" through "must take the tenant as a parameter and filter by it." — so the paragraph ends at "never assume one exists."
2. Replace "A `mutate` value has no comparison operator (a text test such as `startsWith` is a call, and may fill a boolean\nfield). For a flag decided by a comparison, let the `condition` compare and the `mutate` write the literal, with a\nsecond hook for the opposite case." with "A `mutate` value has no comparison operator. For a flag decided by a comparison, let the `condition` compare and\nthe `mutate` write the literal, with a second hook for the opposite case."
3. Immediately before the paragraph that begins "After-hooks (`afterCreate`, `afterUpdate`, `afterDelete`) run after the commit", insert (followed by a blank line):

```markdown
<!-- gen:descriptor-or-code -->
Host C# may react after a commit (an extension). When `get_descriptor`'s `hostExtensions` names the entity or `*`,
call `get_extensions` before proposing an after-hook there, and name one that already does it instead. Apply
refuses removing, or excluding from event images, a field or entity an extension reads.
<!-- /gen:descriptor-or-code -->
```

The read is conditional on purpose: an extension is post-commit host code, so only an *after*-hook can duplicate one, and only on an entity the count names — a before-hook case (`hook_returned_at`, `reject_negative_price`) spends no extra call against the eval's `MaximumToolCalls` of 6. §4.13's "read the catalog before proposing a **rule** an extension enforces" is narrowed to after-hooks for that reason (ruling 14).

`alvo-descriptor-entities-and-fields/SKILL.md`: after the bullet that ends "`required` with `readOnly` is refused there too." add

```markdown
- `"events": {"exclude": ["<field>"]}` leaves fields out of the entity's event images, so no webhook body, after-hook
  or extension reads them; `id`, `tenant_id` and the managed columns stay. A field or entity an extension reads
  (`get_extensions`) cannot be removed or excluded: apply refuses it, naming the extension.
```

`alvo-descriptor-capabilities-and-limits/SKILL.md`: the sentence "Referenced only from `automation`, they do nothing." becomes

```markdown
Referenced only from `automation`, they do nothing. An after-hook `condition` or template may read
`@tenant.id` and `@user.roles`: the writer's tenant and roles as the event recorded them.
```

Measure: `wc -c plugins/alvo/skills/alvo-descriptor-{hooks,entities-and-fields,capabilities-and-limits}/SKILL.md` — expected 6 141, 6 031 and 3 162 bytes (all ≤ 6 144; the hooks skill keeps 3 bytes of headroom, debt the PR report names); `wc -l` all ≤ 200. Keep the files LF, as they are.

- [ ] **Step 4: Run the skill suites**

Run: `dotnet test --project test/MMLib.Alvo.Ai.Tests --filter-class '*SkillRegionTests'`, `--filter-class '*SkillConformanceTests'`, `--filter-class '*EmbeddedSkillsTests'`; `dotnet test --project test/MMLib.Alvo.Host.Tests --filter-class '*SkillCoreClaimsTests'`, `--filter-class '*SkillClaimTests'`, `--filter-class '*SkillExampleOutcomeTests'`.
Expected: PASS.

- [ ] **Step 5: Format, ring0, commit**

```bash
dotnet format whitespace MMLib.Alvo.slnx --include test/MMLib.Alvo.Ai.Tests/SkillRegionTests.cs
scripts/test-ring0
git add plugins/alvo/skills/alvo-descriptor-hooks/SKILL.md plugins/alvo/skills/alvo-descriptor-entities-and-fields/SKILL.md plugins/alvo/skills/alvo-descriptor-capabilities-and-limits/SKILL.md test/MMLib.Alvo.Ai.Tests/SkillRegionTests.cs
git commit -m "docs(skills): teach the descriptor skills about extensions and event images

Claude-Session: https://claude.ai/code/session_01NJAtafM29iddLwfhb5gnmB"
```

---

## Task 11: Two eval cases and their graders **[slice end]**

**Files:**
- Create: `eval/MMLib.Alvo.Ai.Eval/EvalExtensions.cs`, `eval/MMLib.Alvo.Ai.Eval/EvalCases.Extensions.cs`, `test/MMLib.Alvo.Ai.Eval.Tests/ExtensionCasesTests.cs`
- Modify: `eval/MMLib.Alvo.Ai.Eval/EvalCases.cs` (two entries in `All`, summary), `eval/MMLib.Alvo.Ai.Eval/EvalWorld.cs`, `test/MMLib.Alvo.Ai.Eval.Tests/EvalCasesTests.cs` (count 17 → 19), `test/MMLib.Alvo.Ai.Eval.Tests/Turns.cs` (two members in `Original`)

**Interfaces:**
- Consumes: Task 9's tool, Task 5's refusal (a dry run naming the extension).
- Produces: cases `extension_named_not_duplicated` and `bound_field_removal_refused`; `EvalExtensions.OutOfService = "notify-desk-out-of-service"`, `EvalExtensions.DamageClaim = "open-damage-claim"`, `EvalExtensions.Register(IAlvoBuilder)`.

- [ ] **Step 1: Write the failing grader tests**

In `test/MMLib.Alvo.Ai.Eval.Tests/Turns.cs`, `Original`: add `"rental_fleet": { "fields": { "in_service": { "type": "boolean", "default": true } } },` after the `rentals` entity, and add `"damage_report": { "type": "text" }` to `rentals.fields`.

In `EvalCasesTests.cs`: `The_suite_has_the_reliability_first_try_and_skill_cases` expects `19`; add `[InlineData("extension_named_not_duplicated")]` and `[InlineData("bound_field_removal_refused")]` to `A_skill_case_can_be_asked_for_by_name`.

Create `test/MMLib.Alvo.Ai.Eval.Tests/ExtensionCasesTests.cs`:

```csharp
using System.Text.Json.Nodes;

using static MMLib.Alvo.Ai.Eval.Tests.Turns;

namespace MMLib.Alvo.Ai.Eval.Tests;

/// <summary>The two extension cases (spec 2026-10-14 §4.13, HX-04 acceptance (4)): each grader passes the right turn and fails the plausible wrong one.</summary>
public sealed class ExtensionCasesTests
{
    private const string OutOfService = "extension_named_not_duplicated";
    private const string DamageRemoval = "bound_field_removal_refused";

    [Fact]
    public void Naming_the_extension_after_reading_the_catalog_passes()
        => Grade(OutOfService, Turn(answer: $"The host already does this: `{EvalExtensions.OutOfService}` tells the rental desk.", calls: Read("get_extensions")))
            .Passed.ShouldBeTrue();

    [Fact]
    public void Adding_an_after_hook_beside_the_extension_fails()
    {
        var proposed = Edited(document => document["entities"]!["rental_fleet"]!["hooks"] = new JsonObject
        {
            ["afterUpdate"] = new JsonArray(new JsonObject
            {
                ["condition"] = "changed(in_service) && new.in_service == false",
                ["action"] = new JsonObject { ["type"] = "webhook", ["endpoint"] = "rental-desk" },
            }),
        });

        Grade(OutOfService, Turn(proposed, $"Added; `{EvalExtensions.OutOfService}` also exists.", calls: [Read("get_extensions"), Propose(Valid())]))
            .Passed.ShouldBeFalse();
    }

    [Fact]
    public void Naming_the_extension_without_reading_the_catalog_fails()
        => Grade(OutOfService, Turn(answer: $"`{EvalExtensions.OutOfService}` does it.")).Passed.ShouldBeFalse();

    [Fact]
    public void Refusing_the_removal_and_naming_the_extension_passes()
        => Grade(DamageRemoval, Turn(
                answer: $"I cannot remove it: the host's `{EvalExtensions.DamageClaim}` reads damage_report. Its developer has to remove that first.",
                calls: Propose(Refused("validation", $"Field 'damage_report' of 'rentals' is bound by extension '{EvalExtensions.DamageClaim}' (eventHandler, in this host): …"))))
            .Passed.ShouldBeTrue();

    [Fact]
    public void A_valid_proposal_that_removes_the_field_fails()
    {
        var proposed = Edited(document => document.Fields("rentals").Remove("damage_report"));

        Grade(DamageRemoval, Turn(proposed, $"Removed. Note `{EvalExtensions.DamageClaim}`.", calls: Propose(Valid()))).Passed.ShouldBeFalse();
    }

    [Fact]
    public void A_refusal_that_never_names_the_extension_fails()
        => Grade(DamageRemoval, Turn(answer: "That change was refused.", calls: Propose(Refused("validation", "refused"))))
            .Passed.ShouldBeFalse();

    private static Verdict Grade(string name, TurnRecord turn) => EvalCases.All.Single(@case => @case.Name == name).Grade(turn);
}
```

If `EvalCasesTests` already exposes a shared `Grade(name, turn)` helper, use it instead of the private one. `Turn`'s `calls` is `params RecordedCall[]`, so `calls: Read(…)` passes one call and `calls: [Read(…), Propose(…)]` passes two.

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test --project test/MMLib.Alvo.Ai.Eval.Tests --filter-class '*ExtensionCasesTests'`
Expected: build FAIL — `EvalExtensions` does not exist; then `Single` fails for the two names.

- [ ] **Step 3: Implement**

`eval/MMLib.Alvo.Ai.Eval/EvalExtensions.cs`:

```csharp
using Microsoft.Extensions.DependencyInjection;

using MMLib.Alvo.Data;
using MMLib.Alvo.Events;

namespace MMLib.Alvo.Ai.Eval;

/// <summary>
/// The eval host's own C#: two event handlers — on <c>rental_fleet</c>, which no other case touches, and on the one
/// <c>rentals</c> field (<c>damage_report</c>) no other case reads — so the extension cases have something to name and
/// something to protect without changing what any other case grades.
/// </summary>
internal static class EvalExtensions
{
    internal const string OutOfService = "notify-desk-out-of-service";
    internal const string DamageClaim = "open-damage-claim";

    /// <summary>Registers both handlers on the shipped host's builder; the stored-descriptor boot binds them strictly.</summary>
    /// <param name="alvo">The host's Alvo builder.</param>
    internal static void Register(IAlvoBuilder alvo) => alvo
        .Entity("rental_fleet", fleet => fleet.OnUpdated(
            OutOfService,
            (AlvoUpdated<AlvoRecord> _) => Task.CompletedTask,
            when: "changed(in_service) && new.in_service == false",
            configure: options => options.Description = "Tells the rental desk when a fleet bike is taken out of service."))
        .Entity("rentals", rentals => rentals.OnUpdated(
            DamageClaim,
            (AlvoUpdated<AlvoRecord> _) => Task.CompletedTask,
            when: "changed(damage_report)",
            configure: options => options.Description = "Opens an insurance claim when a damage report is written on a rental."));
}
```

`eval/MMLib.Alvo.Ai.Eval/EvalCases.Extensions.cs`:

```csharp
namespace MMLib.Alvo.Ai.Eval;

/// <summary>The graders of the two extension cases (spec 2026-10-14 §4.13): name the extension instead of duplicating it; never propose unbinding one.</summary>
internal static partial class EvalCases
{
    private const string Extensions = "get_extensions";
    private const string FleetHooks = "/entities/rental_fleet/hooks";
    private const string DamageReport = "/entities/rentals/fields/damage_report";

    /// <summary>The catalog read, the extension named, and no hook proposed on <c>rental_fleet</c> beside it.</summary>
    private static Verdict ExtensionNamedNotDuplicated(TurnRecord turn)
    {
        var hook = turn.ChangedPaths.Any(path => path.StartsWith(FleetHooks, StringComparison.Ordinal));
        var read = turn.ToolCalls.Contains(Extensions);
        var named = turn.Answer.Contains(EvalExtensions.OutOfService, StringComparison.Ordinal);
        return Verdict.When(!hook && read && named, $"proposesHook={hook} readExtensions={read} namesExtension={named}");
    }

    /// <summary>No valid proposal removing the bound field, and the extension that binds it named to the operator.</summary>
    private static Verdict BoundFieldRemovalRefused(TurnRecord turn)
    {
        var removed = turn.HasValidProposal && turn.ChangedPaths.Contains(DamageReport);
        var named = turn.Answer.Contains(EvalExtensions.DamageClaim, StringComparison.Ordinal);
        return Verdict.When(!removed && named, $"validRemoval={removed} namesExtension={named}");
    }
}
```

`EvalCases.cs`: append to `All`:

```csharp
        new("extension_named_not_duplicated",
            "When a rental bike is taken out of service, let the rental desk know.",
            "Keď sa požičovný bicykel (rental_fleet) vyradí z prevádzky, daj vedieť požičovni (rental desk).",
            ExtensionNamedNotDuplicated),
        new("bound_field_removal_refused",
            "Remove the damage_report field from rentals.",
            "Odstráň z rentals stĺpec damage_report.",
            BoundFieldRemovalRefused),
```

and extend the class summary: "…, and the two extension cases (HX-04)".

`EvalWorld.cs`, `BuildAsync`: after `var builder = AlvoHost.CreateBuilder(…);` add `EvalExtensions.Register(new HostBuilder(builder.Services));` and the nested type

```csharp
    /// <summary>The shipped host's service collection, as the builder <c>AddAlvo</c> hands its callback.</summary>
    private sealed class HostBuilder(IServiceCollection services) : IAlvoBuilder
    {
        /// <inheritdoc/>
        public IServiceCollection Services { get; } = services;
    }
```

and add to its remarks that the world registers `EvalExtensions`' two handlers. `rentals` is also the entity of `hook_returned_at`, `rollup_rentals_count` and `automation_refused`; their requests are before-hooks, a rollup and a schedule, none an after-hook, so the conditional `get_extensions` read (Task 10) does not add a call to them, and `damage_report` appears in none of them.

- [ ] **Step 4: Run the tests to verify they pass**

Run: `dotnet test --project test/MMLib.Alvo.Ai.Eval.Tests` (whole project); `dotnet build eval/MMLib.Alvo.Ai.Eval` (built on the PR, never run there).
Expected: PASS / build succeeds.

- [ ] **Step 5: Format, rings, commit**

```bash
dotnet format whitespace MMLib.Alvo.slnx --include eval/MMLib.Alvo.Ai.Eval/EvalExtensions.cs eval/MMLib.Alvo.Ai.Eval/EvalCases.Extensions.cs eval/MMLib.Alvo.Ai.Eval/EvalCases.cs eval/MMLib.Alvo.Ai.Eval/EvalWorld.cs test/MMLib.Alvo.Ai.Eval.Tests/ExtensionCasesTests.cs test/MMLib.Alvo.Ai.Eval.Tests/EvalCasesTests.cs test/MMLib.Alvo.Ai.Eval.Tests/Turns.cs
scripts/test-ring0 && scripts/test-ring1
git add eval/MMLib.Alvo.Ai.Eval/EvalExtensions.cs eval/MMLib.Alvo.Ai.Eval/EvalCases.Extensions.cs eval/MMLib.Alvo.Ai.Eval/EvalCases.cs eval/MMLib.Alvo.Ai.Eval/EvalWorld.cs test/MMLib.Alvo.Ai.Eval.Tests/ExtensionCasesTests.cs test/MMLib.Alvo.Ai.Eval.Tests/EvalCasesTests.cs test/MMLib.Alvo.Ai.Eval.Tests/Turns.cs
git commit -m "test(eval): grade naming an extension and refusing to unbind one

Claude-Session: https://claude.ai/code/session_01NJAtafM29iddLwfhb5gnmB"
```

---

## Task 12: Docs — *See your extensions in the dashboard*, architecture docs, CHANGELOG, PLAN, spec amendment

**Files:**
- Create: `website/src/content/docs/extending/extension-catalog.mdx`; `website/src/snippets/extending/catalog/FleetDeskExtensions.cs`, `FleetDeskExtensionsTests.cs`, `fleet-desk.alvo.json`
- Modify: `website/astro.config.mjs`, `website/src/content/docs/extending/index.mdx`, `extending/testing.mdx` (sidebar order 5), `extending/event-handlers.mdx`, `website/src/content/docs/guides/admin-dashboard.mdx`, `website/src/content/docs/guides/schema-assistant.mdx`
- Modify: `docs/architecture/events.md` (*Unbound handlers*), `docs/architecture/management-api.md`, `CHANGELOG.md`, `docs/PLAN.md`, `docs/superpowers/specs/2026-10-14-host-extensibility-design.md`

**Interfaces:**
- Consumes: everything above.
- Produces: the page, run by `FleetDeskExtensionsTests` in `MMLib.Alvo.DocsSnippets.Tests` (ring0).

- [ ] **Step 1: Write the snippet and its fact**

`website/src/snippets/extending/catalog/fleet-desk.alvo.json`:

```json
{
  "$schema": "https://alvo.dev/schema/v1/project.json",
  "apiVersion": "alvo.dev/v1",
  "name": "fleet-desk",
  "access": { "admin": "'admin' in @user.roles" },
  "entities": {
    "vehicles": {
      "fields": {
        "plate": { "type": "string", "required": true, "maxLength": 12 },
        "status": { "type": "enum", "values": ["in_service", "in_repair"] }
      },
      "rules": {
        "list": "'admin' in @user.roles",
        "get": "'admin' in @user.roles",
        "create": "'admin' in @user.roles",
        "update": "'admin' in @user.roles"
      }
    }
  }
}
```

`website/src/snippets/extending/catalog/FleetDeskExtensions.cs`:

```csharp
using Microsoft.Extensions.DependencyInjection;
using MMLib.Alvo;
using MMLib.Alvo.Data;
using MMLib.Alvo.Events;

namespace FleetDesk;

public static class FleetDeskExtensions
{
    public static IAlvoBuilder AddFleetDeskExtensions(this IAlvoBuilder alvo) => alvo
        .Entity("vehicles", vehicles => vehicles
            .OnUpdated(
                "book-repair",
                when: "changed(status) && new.status == 'in_repair'",
                handler: (AlvoUpdated<AlvoRecord> updated, IWorkshop workshop, CancellationToken ct) =>
                    workshop.BookAsync((Guid)updated.New["id"]!, ct),
                configure: options => options.Description = "Books the vehicle into the workshop when it goes in for repair."));
}

public interface IWorkshop
{
    Task BookAsync(Guid vehicle, CancellationToken cancellationToken);
}
```

`website/src/snippets/extending/catalog/FleetDeskExtensionsTests.cs` (fix the usings to the real namespace of `ApiKeyScope` — `git grep -n "record ApiKeyScope" -- src/MMLib.Alvo.Abstractions`):

```csharp
using Microsoft.Extensions.DependencyInjection;
using MMLib.Alvo.Auth;
using MMLib.Alvo.Descriptor;
using MMLib.Alvo.Management;
using MMLib.Alvo.TestHost;
using Shouldly;
using System.Text.Json.Nodes;
using Xunit;

namespace FleetDesk.Tests;

public sealed class FleetDeskExtensionsTests
{
    private static string Descriptor => Path.Combine(AppContext.BaseDirectory, "fleet-desk.alvo.json");

    [Fact]
    public async Task The_catalog_lists_the_handler_and_an_apply_dropping_what_it_reads_is_refused()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var alvo = await AlvoTestHost.StartAsync(builder =>
        {
            builder.FromDescriptor(Descriptor).AddFleetDeskExtensions();
            builder.Services.AddSingleton<IWorkshop, NoWorkshop>();
        }, cancellationToken: ct);
        var management = AsAdministrator(alvo);

        var handler = (await management.GetExtensionsAsync("fleet-desk", ct)).Items.ShouldHaveSingleItem();
        handler.Id.ShouldBe("book-repair");
        handler.Entities.ShouldBe(["vehicles"]);
        handler.Operations.ShouldBe(["update"]);
        handler.When.ShouldBe("changed(status) && new.status == 'in_repair'");

        var withoutStatus = JsonNode.Parse(await File.ReadAllTextAsync(Descriptor, ct))!;
        withoutStatus["entities"]!["vehicles"]!["fields"]!.AsObject().Remove("status");
        var refused = await Should.ThrowAsync<DescriptorValidationException>(() => management.ApplyDescriptorAsync(
            "fleet-desk", new ManagementApplyRequest(withoutStatus.ToJsonString(), ExpectedRevision: 1, DryRun: true), ct));
        refused.Result.Errors.ShouldContain(error => error.Path == "/entities/vehicles/fields/status");
    }

    private static IAlvoManagement AsAdministrator(AlvoTestHost alvo)
    {
        alvo.Services.GetRequiredService<IAlvoContextAccessor>().Principal = new AlvoPrincipal
        {
            Context = AlvoTestHost.Caller("admin"),
            Scopes = new HashSet<ApiKeyScope>(),
            KeyId = "docs",
        };
        return alvo.Services.GetRequiredService<IAlvoManagement>();
    }

    private sealed class NoWorkshop : IWorkshop
    {
        public Task BookAsync(Guid vehicle, CancellationToken cancellationToken) => Task.CompletedTask;
    }
}
```

Run: `dotnet test --project test/MMLib.Alvo.DocsSnippets.Tests`
Expected: PASS (`SnippetCoverageTests` finds both new types; the fact passes — the guard answers before the revision check, so the dry run's `ExpectedRevision` does not decide it). Two failure modes are not the guard: a `ManagementForbiddenException` means the access block did not admit the principal (check `ManagementAccessEvaluator` for what an `admin` level needs and keep the descriptor honest, not the test); an activation error naming `IRuntimeSchemaWriter` or `IDescriptorVersionStore` means `AlvoTestHost`'s container cannot resolve `RuntimeSchemaService` — then read the catalog half only, and move the refusal half to a fact over `ManagedExtensions` in `MMLib.Alvo.Api.Tests`, saying so on the page.

- [ ] **Step 2: Write the page**

`website/src/content/docs/extending/extension-catalog.mdx` — frontmatter `title: See your extensions in the dashboard`, `description: Every handler your host registers is listed — in the dashboard, the Management API and the assistant — and no apply can quietly drop what it reads.`, `sidebar:` / `order: 4`. Imports as `event-handlers.mdx` does (`Code`, `SourceExcerpt`, the three snippets as `descriptor`, `extensions`, `tests`). Sections, each a few sentences:

1. *What is listed* — every event handler as metadata, never code; a table of the members (`id`, `kind`, `stage`, `entities`, `operations`, `event`, `when`, `order`, `timeout`, `runAs`, `ordering`, `description`, `typeName`) with the wire values from this plan's Global Constraints; `<Code code={descriptor} lang="json" title="fleet-desk.alvo.json" />` and `<SourceExcerpt code={extensions} from="public static IAlvoBuilder AddFleetDeskExtensions" to="go in for repair.\"));" title="FleetDeskExtensions.cs" />`.
2. *Read it* — `GET /management/projects/<project>/extensions` (Viewer) and `IAlvoManagement.GetExtensionsAsync`; a hand-written JSON block of the one-item answer for `book-repair` (matching the fact's assertions and Task 2's snapshot shape: `entities ["vehicles"]`, `operations ["update"]`, `timeout "00:00:30"`, `runAs "system"`, `ordering "perRecord"`, `typeName null`); `Alvo:Extensions:ExposeTypeNames` and why it defaults to `false` (a type name reaches every catalog viewer and the assistant's model); the capability report's `extensions.eventHandler`; the policy simulator's `extensions`.
3. *In the dashboard* — the `⟨/⟩ n` badge on **Schema** (every-entity handlers count too) and the **Lifecycle** tab, which shows what runs *after the commit only* (before-hooks stay on **On write**): descriptor after-hooks (changed on **On write**) and code items, read-only, marked `⟨/⟩ from code`, with the description, `when` and a line such as `on update · timeout 30 s · runs as the system · in order per record`.
4. *An apply cannot drop what a handler reads* — the three refusals and their pointers; a sample violation `{"pointer": "/entities/vehicles/fields/status", "code": "descriptor", "message": "Field 'status' of 'vehicles' is bound by extension 'book-repair' (eventHandler, in this host): …", "fixSuggestion": "Remove the registration and redeploy first, or keep the field."}`, with the compiler's own sentence beside it at `"pointer": ""`; it holds for a dry run, a rollback and `ApplyAlvoDescriptorAsync` too, and is answered before the revision check; it refuses a *change* — a handler that never bound (a dashboard-first host's first descriptor) blocks nothing and is listed as a warning in the preview; the remedy order: remove or change the registration, redeploy, then apply; `<SourceExcerpt code={tests} from="var withoutStatus" to="/entities/vehicles/fields/status\");" title="FleetDeskExtensionsTests.cs" />`.
5. *The assistant* — `get_descriptor`'s `hostExtensions` count and `get_extensions`; before an after-hook on an entity the count names, it reads the catalog and names an extension that already does what is asked, and it will not propose removing what one reads.
6. *Not yet* — write hooks, commands and reads join the lane in later releases; delivery history, pause and replay arrive with the operations page.

`website/astro.config.mjs`: `page('See your extensions in the dashboard', 'extending/extension-catalog'),` after the *React to changes* entry; `extending/testing.mdx` frontmatter `order: 5`; `extending/index.mdx`: a bullet `- [See your extensions in the dashboard](/extending/extension-catalog/) — the catalog, the Lifecycle tab, and the apply guard.` after the event-handlers bullet.

`extending/event-handlers.mdx`: replace the bullet that begins "**A handler that no longer fits the descriptor**" with: "**An apply cannot unbind a handler.** An apply, a dry run, a rollback or `ApplyAlvoDescriptorAsync` that removes an entity or field a bound handler reads, or excludes a field its condition reads, is refused naming the handler ([details](/extending/extension-catalog/#an-apply-cannot-drop-what-a-handler-reads)). A handler that never bound — a dashboard-first host before its descriptor fits, a stored boot that ended `Failed` — blocks nothing: the preview warns, it is logged `ExtensionUnbound`, and every event it would have handled is recorded as a dead delivery with the reason `unbound`, which does not hold the record's later events back." — and in *Not yet* drop "the extension catalog and dashboard,".

`guides/admin-dashboard.mdx`: the tab list reads *Fields*, *Relationships*, *Rules*, *On write* (the hooks), *Lifecycle* (what runs after a commit, host code included), … — keep the rest of the sentence. `guides/schema-assistant.mdx`: a table row ``| `get_extensions` | the host code (extensions) this host registers and what each binds, read-only |`` after `get_cel_functions`.

- [ ] **Step 3: Architecture docs, CHANGELOG, PLAN**

`docs/architecture/management-api.md`: "twelve HTTP routes" → "twenty-one" (it is twenty today — `grep -c "new ManagementRoute(" src/MMLib.Alvo/Management/Internal/ManagementEndpoints.cs` — and this adds one; sweep the file for any other count of routes); a row ``| `GET {m}/projects/{project}/extensions` | `GetExtensionsAsync` | `viewer` |`` after the `cel/functions` row; a section **Extensions** after the capabilities section: the catalog and its members, `ExposeTypeNames`, `honoured` gaining `extensions.<kind>`, `ManagementPolicyVerdict.Extensions`, and the apply guard (change-based; sites; ordering before the revision check; pointers beside the compiler's sentences; warnings for handlers bound by neither catalog; `422 validation`).

`docs/architecture/events.md` *Unbound handlers*: rewrite to say the apply guard (HX-04) refuses every apply path that would unbind a bound handler; `unbound` rows remain the record for a handler that never bound (a dashboard-first host's first descriptor, a stored boot that ended `Failed`, a host that primes `IPolicyCatalogProvider` itself).

`CHANGELOG.md`, under `## [Unreleased]`:
- `### Added`, as the first entry: **Extension catalog, apply guard, dashboard and assistant** (HX-04, #381). `GET {m}/projects/{p}/extensions` / `IAlvoManagement.GetExtensionsAsync` list every registered event handler as metadata (`ManagementExtension`, `ManagementExtensionCatalog`, `AlvoExtensionKind`, `AlvoExtensionStage`); `Alvo:Extensions:ExposeTypeNames`; the capability report's `honoured` gains `extensions.eventHandler`; `ManagementPolicyVerdict.Extensions`; the dashboard's `⟨/⟩ n` badge and *Lifecycle* tab (*After commit* lane); the assistant's `get_extensions` and two eval cases. Docs: *See your extensions in the dashboard*.
- `### Changed (breaking)`: **`IAlvoManagement` gains `GetExtensionsAsync`** (HX-04) — an implementer or decorator adds it; a caller is unaffected. **An apply that would unbind an event handler is refused** (HX-04) — a runtime apply, a dry run, a rollback and `ApplyAlvoDescriptorAsync` that remove an entity or field a registered handler binds, or exclude a field it reads, now throw `DescriptorValidationException` (`422 validation` over HTTP), answered before the revision check, instead of leaving the handler unbound. A handler that was not bound before the change (a dashboard-first host's first descriptor) blocks nothing and is reported as a warning.

`docs/PLAN.md`, *Host extensibility* line: "…; HX-03 (event handlers on the delivery engine) the third; HX-04 (extension catalog, apply guard, dashboard and assistant) the fourth."

- [ ] **Step 4: Spec amendment**

In `docs/superpowers/specs/2026-10-14-host-extensibility-design.md`:
- after the *Assistant* paragraph of §4.13, add **HX-04 settled details.** with the seventeen rulings of this plan's *Rulings and deviations* section, numbered (1)–(17), verbatim (ruling 10 records the per-apply cost);
- at the end of *HX-03 settled details* (6), append: "— **As built (HX-04):** every apply path is guarded, change-based (§4.13 *HX-04 settled details* (6)–(9)): no apply can unbind a handler the current catalog binds; a handler that never bound (an `Awaiting` first descriptor, a `Failed` stored boot) blocks nothing, is warned about in the preview, and keeps its dead `unbound` rows until a descriptor fits it. The dispatchers' stand-down on a boot that primed no catalog is unchanged."

- [ ] **Step 5: Build the site, ring0, commit**

```bash
dotnet format whitespace MMLib.Alvo.slnx --include website/src/snippets/extending/catalog/FleetDeskExtensions.cs website/src/snippets/extending/catalog/FleetDeskExtensionsTests.cs
scripts/test-ring0
(cd website && npm ci && npm run build)
git add website/src/content/docs/extending/extension-catalog.mdx website/src/snippets/extending/catalog website/astro.config.mjs website/src/content/docs/extending/index.mdx website/src/content/docs/extending/testing.mdx website/src/content/docs/extending/event-handlers.mdx website/src/content/docs/guides/admin-dashboard.mdx website/src/content/docs/guides/schema-assistant.mdx docs/architecture/events.md docs/architecture/management-api.md CHANGELOG.md docs/PLAN.md docs/superpowers/specs/2026-10-14-host-extensibility-design.md
git commit -m "docs: see your extensions in the dashboard, and the apply guard

Claude-Session: https://claude.ai/code/session_01NJAtafM29iddLwfhb5gnmB"
```

Expected: the site builds with link validation green. `CLAUDE.md` says the eval asks "seventeen cases"; it is the maintainer's file — name the one-word change (seventeen → nineteen) in the PR report instead of editing it.

---

## Task 13: Rings, reviews, plan-guard, PR report, PR

- [ ] **Step 1: CI-parity Release build, rings and the out-of-ring suites**

First the Release build on CI's SDK (rings are Debug on the local SDK; CI's newer analyzers under `TreatWarningsAsErrors` are what it catches):

```bash
docker run --rm -v "$PWD":/src -w /src mcr.microsoft.com/dotnet/sdk:10.0 sh -c 'dotnet build MMLib.Alvo.slnx -c Release -p:HUSKY=0 2>&1 | grep -E "error [A-Z]+[0-9]+" | sort -u'
git clean -fdx -e .superpowers
```

Expected: no output from the build (any `error CAxxxx` line is fixed in a `fix(…): …` commit, and this step re-run). The clean removes the container's `bin/`/`obj/` (written as root) before the local rings. Repeat both commands before every push of a fix commit. Then `scripts/test-ring0`, `scripts/test-ring1`, `scripts/test-ring2`, `scripts/test-admin-e2e`, `python3 scripts/gen-prototype-fixtures --check`, `scripts/test-prototype`, `(cd website && npm run build)`. Expected: all green. ring2 includes the API invariant suite and `scripts/lint-api`; the management route is excluded from the OpenAPI document (`ManagementContractTests.No_management_route_reaches_the_published_openapi_document`), so the Vacuum ruleset and the TeaPie path set do not move. The eval is **not** run (it costs tokens): say so in the PR report and give `scripts/eval-assistant --case extension_named_not_duplicated` and `--case bound_field_removal_refused`.

- [ ] **Step 2: Reviews (substitutes)**

Freeze the tree (no edits while a reviewer reads it). `/code-review` is user-only: dispatch one reviewer subagent over `git diff hx/03-event-handlers...HEAD` labelled "substitute for /code-review high", pointing it at: the guard's position before `CurrentAtAsync` in all three `RuntimeSchemaService` paths and in `SchemaMigrationRunner`; the route's gate (`ManagementRoute` + the `ManagementOperations` Viewer row) and that `GetExtensionsAsync` calls `EnsureMayPerform` before `EnsureServed`; that `TypeName` is null unless `ExposeTypeNames`; that the catalog lists registrations in the bindings' order; that a code item in the lane has no control. §6.2 does not list HX-04, so no `/security-review` substitute and no `needs-deep-review`. Fix findings, re-run ring0, one `fix(…): …` commit per finding.

- [ ] **Step 3: `alvo-plan-guard`**

Dispatch `alvo-plan-guard` last (read-only); act on drift it reports. The PLAN marker stays on F5.

- [ ] **Step 4: PR report**

Use the `alvo-pr-report` skill: it dispatches `alvo-pr-reporter` and publishes the 8-section page. Evidence: acceptance (1)–(5) → tests (table below), the two public-API diffs against the public-surface line, the snapshot, the admin e2e run, the Release build, the skill byte counts (the hooks skill at 6 141 of 6 144 — the next edit there must trim first), that the eval was not run and the after-hook cases' management-call counts may rise by one where `hostExtensions` names their entity, the CLAUDE.md one-word change for the maintainer, and the rulings below.

- [ ] **Step 5: Push and open the PR**

```bash
git branch --show-current   # hx/04-extension-catalog
git push -u origin hx/04-extension-catalog
```

Base `main`. If #411 (HX-03) is merged: `git fetch origin && git merge origin/main` first (merge-commit style), re-run ring0, push, then `gh pr create --base main`. If #411 is still open: `gh pr create --base main --draft`, with the body's first line "Stacked on #411 — review `hx/03-event-handlers...hx/04-extension-catalog`; ready once #411 merges." The body is the skill's five-line pointer, carries `Closes #381` once, and ends with `https://claude.ai/code/session_01NJAtafM29iddLwfhb5gnmB`. After the merge, read #381 back (`gh issue view 381`) to confirm it — and only it — was closed.

---

## Rulings and deviations this plan takes

Each is recorded in the spec amendment (Task 12 Step 4) so a later reader can tell a decision from an oversight.

1. **The catalog ships the members HX-04 fills.** `ManagementExtension` carries `Id`, `Kind`, `Stage`, `Entities`, `Operations`, `Event`, `When`, `Order`, `Timeout`, `RunAs`, `Ordering`, `Description`, `TypeName`; `AlvoExtensionKind` has `EventHandler` and `AlvoExtensionStage` `PostCommit`. §4.13's `Concurrency` (HX-14), `Route`/`HttpMethod`/`Access` (HX-08), `Cron`/`TimeZone` (HX-13), `Secrets` (HX-15), `TopLevelOnly` (HX-09), `Enabled`/`Paused` (HX-06) and the other kinds and stages are appended by the PR that first fills them — init members and appended enum values are additive, while a member nothing fills would be a published promise with no behaviour behind it (*public is the contract*; *nothing created ahead of time*). A deliberate deviation from §4.13's full shape.
2. **The catalog lists registrations, not bindings,** by `Order` then registration order (the bindings' order); an `AddRetiredExtension` id is not an extension and is not listed. The apply guard is what makes "registered" mean "bound".
3. **Operations use the policy's wire names** — `create`, `update`, `delete` — so the simulator filters by plain equality: `OnSaved` lists `create`, `update`; `OnUpdated`'s `update` covers a replace (both emit `.updated`). An application-event handler has `entities: []`, `operations: []` and its type in `event`; an every-entity handler has `entities: ["*"]`.
4. **`runAs` and `ordering` are wire strings** (`system`/`eventActor`, `perRecord`/`none`) because the enums they come from are core types Abstractions may not name; `timeout` is a `TimeSpan` (`"00:00:30"`).
5. **`TypeName`** is the class handler's full name when `Alvo:Extensions:ExposeTypeNames` is true; a delegate handler is never named (its method's declaring type is compiler-generated noise).
6. **The apply guard is change-based, as §4.2 writes it:** an apply is refused only when a handler that binds the current catalog (`IPolicyCatalogProvider.Current`) would not bind the proposed one, checked by HX-03's own `ExtensionBindingCompiler.Compile` through the binder's per-catalog cache, so the guard cannot drift from the boot. That covers §4.2's two named cases (removing a bound entity or field, adding it to `events.exclude`) and every other way a change can unbind a bound handler (a retyped field the condition no longer compiles against, a rename). A refusal carries a pointer to the removed entity, the removed or renamed field (what the condition read, compiled against the current catalog) or the `events.exclude` item, **and** the compiler's own sentence for every problem at `""`, so a second cause is never hidden behind the first. Because only a handler that bound the current catalog is refused, every pointed entity or field existed before the change — "keep the entity/field" is always accurate. Every-entity and application-event handlers bind no field and never refuse an apply.
7. **Guarded sites:** `RuntimeSchemaService.ApplyAsync`, `PreviewAsync` (the dashboard's preview, `?dryRun=true`, and a rollback's dry run) and `RollbackAsync`, and `SchemaMigrationRunner.RunAsync` (`ApplyAlvoDescriptorAsync`), which loses HX-03's lenient binding only for bound handlers — its test now pins both halves (a handler that never fit is applied and stays unbound; one that bound is refused). The boot keeps its own refusal (`ExtensionBinder.EnsureBindable`, `AlvoStartupRefusedException`).
8. **The guard runs right after validation,** before the revision check, the plan and the destructive guardrail: a binding refusal is a validation finding about the descriptor itself, and an operator must not be told "allow destructive" or "reload" before being told the change can never apply. A stale `If-Match` on an unbinding apply therefore answers 422, not 412 (pinned) — the house order, since `Validate` already precedes the revision check.
9. **Handlers already unbound block nothing.** On a dashboard-first host that booted `Awaiting` (nothing primed) and after a stored-descriptor boot that ended `Failed`, no handler binds the current catalog, so no apply is refused for a handler; each one the proposed catalog does not fit is a `Warning` finding (the compiler's sentence, at `""`) in the preview, the dry run and `ManagementApplyResult.Warnings`, and its events keep being recorded dead `unbound` (HX-03 (6)) until a descriptor fits it. Refusing instead would lock such a host out of every unrelated apply. The dispatchers' stand-down until a restart on a boot that primed no catalog is unchanged — a candidate follow-up, not HX-04's.
10. **The refusal is a `DescriptorValidationException`** — the same `422 validation` / code `descriptor` an agent already parses (fix text on the wire as `fixSuggestion`), and the shape §4.2 writes. `RuntimeSchemaService`'s public constructor is unchanged; the guard arrives through an internal overload the DI factory calls, so a service a host constructs itself carries no guard (stated in its docs). **Cost:** with at least one handler registered, the guard builds the proposed catalog once and hands it back; `ApplyAsync` and `RollbackAsync` publish that very instance (no second build), and the binder compiles it once — the delivery dispatcher's later `For(catalog)` is a cache hit. A Management apply runs `PreviewAsync` then `ApplyAsync`, so it builds the proposed catalog twice where it built it once before (plus the validator's own build in each); a host with no handler pays nothing.
11. **The capability report appends `extensions.<kind>`** to `Honoured` after the five blocks, per request, only for kinds registered; the static `CapabilityReport.Honoured` (held by the skill region and the prototype) stays the blocks, and the dashboard's *Honoured blocks* stat counts entries without a dot.
12. **`ManagementPolicyVerdict.Extensions` is filled in HX-04** (the entity's own and every-entity extensions whose operations contain the simulated one); rendering it in the simulator and the apply dry run is HX-09's row.
13. **The Lifecycle tab draws one lane, *After commit*, and says so** ("after the commit only"): the descriptor's after-hooks read-only with a link to *On write* (one editor, not two — "editable" is one click to it), and code items read-only with the accent `⟨/⟩ from code` badge the function list's "this host" precedent sets. p95 and 24 h rejections need HX-06's stats and are not drawn; the other lanes appear with the host code that runs in them. The badge counts every extension that runs on the entity, every-entity ones included. The field-removal confirm does not list extensions yet (follow-up): the preview refusal is the HX-04 surface.
14. **The assistant reads the catalog only when it matters.** `get_descriptor`'s answer gains `hostExtensions` (per entity or `*`, read through the port inside the same tool call), and the hooks skill's `descriptor-or-code` region sends the model to `get_extensions` only before an *after*-hook on an entity that count names — §4.13's "before proposing a rule an extension enforces" narrowed to the one place a post-commit extension can be duplicated, so no before-hook, rule or rollup case spends a call against the eval's `MaximumToolCalls` (6). The region fits the 6 144-byte cap (6 141) by dropping two host-developer sentences (kept in the *Custom CEL functions* guide) and one parenthetical the `cel-mutate` region already shows. HX-01's handed-over awareness lands as a bullet in `alvo-descriptor-entities-and-fields` (`events.exclude`) and a sentence in `alvo-descriptor-capabilities-and-limits` (`@tenant.id`/`@user.roles` in after-hooks). The instructions move to v7.
15. **The eval cases** register two handlers in `EvalWorld`: one on `rental_fleet`, which no other case touches, and one on `rentals.damage_report`, a field no other case reads. `rentals` is the entity of `hook_returned_at`, `rollup_rentals_count` and `automation_refused` — a before-hook, a rollup and a schedule, none an after-hook — so `hostExtensions` naming it adds no read to them. The real-model run is on demand, not on the PR.
16. **Fix text follows §4.2 verbatim** for a removal ("Remove the registration and redeploy first, or keep the field." / "…, or keep the entity."); an exclusion, which §4.2 names without a sentence, reads "…, or keep the field in the event images." — the one wording this plan adds.
17. **Enum values are pinned** (`EventHandler = 0`, `PostCommit = 0`) and documented append-only: a later PR adds §4.13's kinds after them and never reorders to match the spec's listing. A typed .NET client older than the server cannot read an unknown member (`JsonStringEnumConverter` throws); the docs say a tolerant client reads the raw string.

---

## Acceptance criteria → tasks

| HX-04 criterion | Task(s) | Test |
|---|---|---|
| (1) catalog snapshot for untyped and `"*"` registrations (Verify) | 2 | `ExtensionCatalogTests.The_catalog_lists_entity_every_entity_and_application_handlers_in_order` (+ `.verified.txt`) |
| (2) apply removing a bound field, and apply excluding a bound field, refused naming the extension (Management API integration) | 4, 5 | `ManagementExtensionsTests.Removing_a_bound_field_is_refused_naming_the_extension`, `Excluding_a_bound_field_from_the_event_images_is_refused_naming_the_extension`, `A_dry_run_removing_a_bound_entity_is_refused_at_the_entity`; unit `ExtensionApplyGuardTests`, `RuntimeSchemaServiceExtensionGuardTests` (apply, preview, rollback, precedence) |
| (3) admin e2e: badge count and read-only lane | 7, 8 | `ExtensionCatalogScenarios.The_entity_list_counts_the_extensions_that_run_on_each_entity`, `The_lifecycle_tab_shows_code_after_the_commit_read_only` |
| (4) eval graders for the two cases unit-tested; `get_extensions` contract test | 9, 11 | `ExtensionCasesTests` (6 facts), `EvalCasesTests` count; `ManagementToolsTests.The_tool_set_is_exactly_the_six_reads_and_the_two_dry_runs`, `The_extensions_tool_answers_with_what_the_management_port_lists`, `A_forbidden_caller_is_reported_by_the_extensions_tool_too`, `The_descriptor_read_counts_the_host_extensions_per_entity` |
| (5) `BoundaryArchitectureTests` green | 7 | `MMLib.Alvo.Admin.Tests` whole project |
| Scope: `GetExtensionsAsync`, route, Viewer | 3 | `ManagementExtensionsTests` (read, empty, 404, 403), `ManagementContractTests`, `ManagementOperationsTests` |
| Scope: capability report lists registered kinds | 2, 3 | `The_capability_report_honours_the_kind_the_host_registers_after_the_blocks`, `ExtensionCatalogTests.The_honoured_kinds_are_named_once_each_and_only_when_registered` |
| Scope: `ManagementPolicyVerdict.Extensions` | 1, 3 | `ManagementExtensionTests.A_verdict_built_as_before_lists_no_extensions_and_still_equals_its_twin`, `A_simulation_lists_the_extensions_that_run_on_its_entity_and_operation` |
| Scope: rollback guard | 5 | `A_rollback_to_a_revision_without_the_field_is_refused` |
| Scope: apply-preview refusal on the dashboard | 8 | `ExtensionApplyRefusalScenarios.Removing_a_field_an_extension_reads_is_refused_at_preview_naming_the_extension` |
| Scope: skill region "descriptor or code" | 10 | `SkillRegionTests.The_descriptor_or_code_region_names_the_extension_read`, `SkillConformanceTests` |
| Scope: `ExposeTypeNames` | 2 | `A_class_handler_is_named_only_when_the_host_asks`, `Type_names_stay_hidden_unless_the_section_exposes_them` |
| Docs: *See your extensions in the dashboard* | 12 | `FleetDeskExtensionsTests` in `MMLib.Alvo.DocsSnippets.Tests`; website build |
| Handed over by HX-01: assistant awareness of `events.exclude` and after-hook `@tenant.id`/`@user.roles` | 10 | skill text; `SkillConformanceTests` caps |
| Handed over by HX-03 (6): the guard closes the `unbound` gap (change-based), incl. the `Awaiting` path | 4, 5 | `A_first_apply_a_handler_does_not_fit_is_applied_with_a_warning`, `A_handler_already_unbound_does_not_block_an_unrelated_apply`, `SchemaMigrationRunnerTests.A_descriptor_that_drops_what_a_bound_handler_reads_is_refused_before_anything_is_written`, `A_descriptor_a_handler_never_fit_is_applied_and_the_handler_stays_unbound`, `ExtensionApplyGuardTests.A_handler_already_unbound_does_not_block_an_unrelated_change` |
