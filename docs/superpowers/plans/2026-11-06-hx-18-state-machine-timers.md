# HX-18 — `stateMachine` and declarative timers — Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** A descriptor author declares which values an enum field may move between — named transitions with optional
`roles` and a CEL `when` — enforced in the write's transaction before any host hook (an undeclared move is 422), and
declares per-record timers (`at: "starts_at - duration('24h')"`, `when: "status == 'confirmed'"`) that are armed, moved
and cancelled in the write's own transaction and fire a custom event through HX-13's `TimerPump`.

**Architecture:** Both features are descriptor members compiled into the policy catalog beside the before-hooks
(`EntityPolicy.Lifecycle`), so they are judged against the same schema revision as the rules. The EF driver reaches the
compiled form through one **internal** Abstractions port, `IEntityLifecycle` (the `IBeforeHookRunner` precedent, but
not replaceable by a host): `EnsureTransition` runs at stage 3 over the candidate the declarative hooks left, and
`TimerChanges` runs at stage 8 beside the outbox append, whose changes the driver writes into `alvo_timers` on the
write's own connection and transaction. Firing is HX-13's, unchanged.

**Tech Stack:** .NET 10 (C# 14), Microsoft.Data.Sqlite, Npgsql (PostgreSQL 16 Testcontainers), xUnit v3 on MTP,
Shouldly, `Microsoft.Extensions.TimeProvider.Testing`, Verify + PublicApiGenerator, Corvus.Json.Validator + CsCheck,
Blazor, Microsoft.Playwright, Astro/Starlight.

**Spec:** `docs/superpowers/specs/2026-10-14-host-extensibility-design.md` — §7 *HX-18* (scope, docs, dashboard, skills,
acceptance (1)–(4)); §4.1 (stages 3–5, 8); §4.10 (timers; "the path HX-18's declarative timers use") and *HX-13 settled
details* (1), (4), (5), (15), (18), (20); *HX-11 settled details* (3) (`now()` in `Condition` = the write instant), (6)
(`CelValueType.Duration`, "HX-18's `TimerAt` typing"), (7)–(10) (durations); §5 row `18`; Appendix A M3, M19; §8 N-5
(transition commands — follow-up #375, out of scope). Issue **#395** (epic #370). Frozen artifacts read:
`schema/project.schema.json` (`$defs/entity`, `$defs/cel`, `$defs/identifier`, `$defs/eventPattern`),
`src/MMLib.Alvo.Abstractions/{Descriptor/EntityDescriptor.cs,Rules/IBeforeHookRunner.cs,Rules/IWriteExtensionRunner.cs,Scheduling/{ITimerStore,TimerEntry}.cs,Data/{AlvoConstraintKind,AlvoConstraintViolationException}.cs,Events/AlvoEventName.cs,Identity/AlvoContext.cs}`,
`src/MMLib.Alvo/{Rules/BeforeHooks.cs,Rules/Internal/{BeforeHookRunner,BeforeHookCompiler,PolicyCatalogBuilder}.cs,Scheduling/AlvoScheduler.cs,Hooks/Internal/WriteExtensionRunner.cs}`,
`src/MMLib.Alvo.Data.EntityFrameworkCore/Internal/{EfAlvoData.cs,EfAlvoData.Hooks.cs,EfCoreTimerStore.cs,TimerTable.cs}`,
`docs/architecture/time.md`. `docs/product/baas-analyza.md` has no state-machine section (only §3's `changed(status)`
"fires once, at the transition" gap test, which `when` honours); its scheduled-jobs section ("delayed jobs (pripomienka o
24 h)") is the timer use case.

**Prior art** (CLAUDE.md *Study the problem*): **django-fsm** `@transition(field, source=[…]|'*', target, conditions,
permission)` — named transitions over one field, `'*'` = any source, conditions and permission checked before the move:
the shape adopted (`from`/`to`/`when`/`roles`). **XState** event-keyed `on` maps — rejected: the machine here is a
guard over writes, not an interpreter driven by events. **Postgres RLS `WITH CHECK`** — a post-image predicate judged
in the write; `when` is that over `old`/`new`. **Durable timers** (Temporal, Durable Functions) — keyed, re-armable,
cancelled by key; the timer row lives in the same transaction as the state that armed it (**transactional outbox**).

## Global Constraints

- Worktree `/Users/martiniak/Developer/GitHub/Burgyn/MMLib.Alvo-wt-hx18`, branch `hx/18-state-machine-timers` (off
  `origin/main`, HX-12 and HX-13 merged; HX-17 is **not** on `main` and nothing here needs it). **One PR for the whole
  HX** (maintainer speed ruling 2026-11-06): slices **A** (Tasks 1–6, engine core) and **B** (Tasks 7–10, surfaces)
  are commit groups, each ending green. This plan file lands in the PR. PR body: `Closes #395`.
- One writer per worktree; never `git add -A` (stage by path); `git branch --show-current` before every commit and push.
  Never `git clean`. A bare `ls` hides git-ignored files — use `/bin/ls` or `find`.
- **Parallel work:** HX-15 is being built at the same time. Shared files stay small and **append-only**: the spec,
  `docs/PLAN.md`, `CHANGELOG.md`, `schema/project.schema.json`, `PublicApi.*.verified.txt`, `plugins/alvo/skills/**`
  (byte caps: `SkillConformanceTests` — 6 144 bytes, 200 lines per skill). On a rebase conflict keep both sides.
- **Enum values:** HX-18 uses **no** `AlvoExtensionKind` / `AlvoExtensionStage` value — the 13 / 8 reserved for it stay
  free (Decision 15).
- **Public surface added is exactly** (each item beyond §5 row `18` is a Decision):
  - Abstractions `MMLib.Alvo.Descriptor`: `EntityDescriptor.StateMachine` (`EntityStateMachine?`),
    `EntityDescriptor.Timers` (`IReadOnlyDictionary<string, EntityTimer>?`); `EntityStateMachine` (`Field` required,
    `Initial` `string?`, `Transitions` required `IReadOnlyDictionary<string, StateTransition>`); `StateTransition`
    (`From` required `IReadOnlyList<string>`, `To` required, `Roles` `IReadOnlyList<string>?`, `When` `string?`,
    `Description` `string?`); `EntityTimer` (`At` required, `When` `string?`, `Event` `string?`, `Description` `string?`).
  - Abstractions `MMLib.Alvo.Data`: `AlvoConstraintKind.Transition` (appended after `Exclusion`).
  - Abstractions `MMLib.Alvo.Management`: `ManagementTransitionVerdict` (init-only: `Name`, `From`, `To`, `Roles`, `When`,
    `Allowed` `bool?`), `ManagementPolicyVerdict.Transitions` (init, default `[]`).
  - Everything else is `internal` — `IEntityLifecycle`, `EntityTimerWrite`, `TimerChange` (Abstractions, IVT to core and
    the EF driver), every core type. No `EntitySchema` member (Decision 7).
- **Wire constants (verbatim):** schema members `stateMachine{field,initial,transitions}`, `transitions.<name>{from,to,roles,when,description}`,
  `timers.<name>{at,when,event,description}`; wildcard source `"*"`; violation code `transition`; timer key
  `alvo:<entity>:<timer>:<id>`; fired data keys `entity`, `id`, `timer`, `at` (ISO-8601 UTC); default event
  `<entity>.<timer>` with `-` → `_`.
- **Engine parity:** every data-path fact runs on SQLite and PostgreSQL through abstract `test/_shared/testhost/`
  facts (`StateMachineFacts`, `EntityTimerFacts`; the `TimeFacts` pattern — a temp file / one database per fact).
  **Every new PostgreSQL test class carries the Windows skip guard** (`EnsureEngineAvailable` →
  `Assert.SkipUnless(!OperatingSystem.IsWindows(), …)`, copied from `PostgreSqlTimeTests`).
- **Fail closed:** a `when` that throws refuses the write; an unprimed catalog cannot reach the guard (policy denies
  first); a role the project does not declare is refused at apply; the guard is not host-replaceable (internal port).
- **Deterministic tests:** `FakeTimeProvider`, `Advance` + `DrainEventsAsync`; no `Task.Delay`.
- **Security core** (authorization-adjacent transitions, in-transaction writes): the stack-level security review
  (checklist, `/security-review` substitute, `needs-deep-review`) runs later, outside this plan. Label the PR
  `needs-deep-review`.
- C# style per `alvo-dotnet-conventions`: no inline comments, methods ≲ 25 lines, `ConfigureAwait(false)` in `src/`,
  `TestContext.Current.CancellationToken` in tests, XML docs on every public member citing no `§`/`#issue`. `.cs` files
  are UTF-8 BOM + CRLF: after editing run `dotnet format whitespace MMLib.Alvo.slnx --include <file> …`.
- **Any change to `schema/project.schema.json` or a description in it → `python3 scripts/gen-prototype-fixtures`**, and
  commit what it regenerates.
- **Testing per task:** affected test projects only — `dotnet test --project <proj> --filter-class '*<Class>'` — plus
  pre-commit's scoped ring0. On each **[slice end]**: `scripts/test-ring1` **and** `dotnet build MMLib.Alvo.slnx -c
  Release` (CI is Release). `scripts/test-ring2` once before the PR. Admin e2e only in Task 8:
  `scripts/test-admin-e2e --filter StateMachine`. No Stryker, no load run.
- Public-API baselines: `test/MMLib.Alvo.Abstractions.Tests`. Accept a `*.received.txt` only when its diff adds exactly
  the symbols above; the turn-review gate then asks for `alvo-snapshot-judge` and an `alvo-architecture-rules`
  justification per symbol.
- Commits: Conventional Commits, one per task, ending `Claude-Session: https://claude.ai/code/session_01NJAtafM29iddLwfhb5gnmB`.
  No `Closes`/`Fixes` in any commit. Before the PR: `alvo-plan-guard`, then the PR report (`alvo-pr-report`).

## Decisions (the spec does not settle these)

1. **Shape = named transitions** `transitions.<name> {from: [...], to, roles?, when?}` (django-fsm; names feed N-5 #375). Cost if wrong: a per-state `on` map would be a breaking schema change later.
2. **Checked at stage 3**, over the candidate after the declarative `mutate`s, before host before-write; a host `Set` of the state field fails `invalid-set` (500). Cost: host code moves state only through a separate write (a command, a handler).
3. **Undeclared move or false `when` → 422 `validation`** via appended `AlvoConstraintKind.Transition` (pointer `/<field>`, code `transition`, no value named); **no listed role → 403** (`AlvoAuthorizationException`). Cost: one public enum member; an exhaustive `switch` gains a case (CHANGELOG).
4. **Several transitions may match a (from, to) pair**: the move is allowed when any one passes both `roles` and `when`, in declaration order; 403 only when none passes `roles`. Cost: none.
5. **Unchanged state is no transition; a delete is no transition; a create must store `initial`** when declared (any value otherwise). Cost: "only drafts may be deleted" stays a `rules.delete`.
6. **No system bypass**: `AlvoContext.System` holds `admin`, so a job moves a role-gated state only through a transition listing `admin`; `from: ["*"]` matches any stored value, including null and values outside the machine. Cost: one more transition for jobs.
7. **Descriptor members only** — no `EntitySchema`/`SchemaModel` member (deviation from §5 row `18`): nothing below the core reads them, and the applied-schema JSON stays unchanged. Cost: a provider sees the machine only through the internal port.
8. **Refused at apply:** a non-enum, non-stored or `hidden` state field; a value outside `values`; `"*"` beside other sources; `to` inside its own `from`; `initial` ≠ a declared field `default`; an undeclared role; either member on `storage: "dynamic"`. Cost: dynamic entities wait for a follow-up.
9. **Timer key `alvo:<entity>:<timer>:<id>`, scope = the row's `tenant_id` text or `""`**; `IAlvoScheduler.ScheduleAsync`/`CancelAsync` refuse a key starting `alvo:`. Cost: a host key with that prefix (none can predate HX-18's release) is refused.
10. **Arming:** `due(row) = when(row) ? at(row) : null`; create arms a non-null due; update upserts only when `due(new) ≠ due(old)`; a null `due(new)` and every delete cancel by key, unconditionally. Cost: one PK delete per timer per write; a changed `at` expression, or a timer added to an entity, reaches an existing row only at its next write that changes its due (backfill = an HX-13 data migration).
11. **Written by the driver at the emit site on the write's own connection and transaction** through `TimerWrites` (shared with `EfCoreTimerStore`), not `ITimerStore.UpsertAsync`: its unit-join re-enters the unit the write already holds, and a hook-free write has no unit. Cost: §7's "through the unit's `ITimerStore`" holds in substance (same table, same SQL, same transaction), not in the call.
12. **`at` compiles in `Condition` with result type `Timestamp`** through an internal `IValueCompiler` that `CelCompiler` implements (no new `CelProfile`, HX-11 (12)); timer `at`/`when` read bare fields only — `old.`, `new.`, `changed()` refused. Cost: a host-replaced `ICelCompiler` makes apply refuse `timers`.
13. **Fired event** = `event` (default `<entity>.<timer>`, `-` → `_`, refused at apply if malformed or in a reserved namespace), as `System(scope)` carrying the writer's provenance, data `{entity, id, timer, at}`; consumed by host handlers — descriptor automation cannot subscribe (`$defs/eventPattern` stays frozen). Cost: a descriptor-only project needs one handler to act on a timer.
14. **In a batch** a transition refusal is that row's `AlvoRowRefusal` (code `transition`); a role refusal is the existing `Forbidden(index, …)`. Cost: none.
15. **Not an extension:** no `AlvoExtensionKind`/`Stage` value and no catalog row; the dashboard shows them on the entity's new *States* tab and in the simulator. Cost: the Lifecycle tab does not list them.

## Review Focus

1. **A declarative `beforeUpdate` `mutate` that moves the state** must still pass the machine (the guard reads candidate ⊕ declarative patch). `StateMachineFacts.A_declarative_mutate_cannot_skip_the_machine` (Task 4).
2. **A PATCH that does not name the state field** on a row whose stored state has no outgoing transition is never refused by the machine. `StateMachineFacts.A_patch_not_naming_the_state_is_never_refused_by_the_machine` (Task 4).
3. **An update moving a timer while `TimerPump` holds a claim on it** fires once, at the new time (the generation fence). `EntityTimerFacts.A_move_racing_a_claimed_fire_fires_once_at_the_new_time` (Task 6).
4. **Clearing the due field** (`starts_at = null`) cancels the timer rather than throwing. `EntityTimerPlannerTests.A_null_due_cancels` (Task 5).
5. **Deleting the record** (hard and soft) cancels its timers. `EntityTimerFacts.Deleting_the_record_cancels_its_timers` (Task 6).

## File structure

**Abstractions:** `Descriptor/EntityDescriptor.cs` (+2 members), `Descriptor/EntityLifecycleDescriptors.cs` (new: three
records), `Data/{AlvoConstraintKind,AlvoConstraintViolationException}.cs`, `Rules/IEntityLifecycle.cs` (new, internal),
`Management/{ManagementTransitionVerdict.cs (new),ManagementModels.cs}`.
**Core** `src/MMLib.Alvo/Lifecycle/` (new slice): `EntityLifecycle.cs` (compiled records), `LifecycleCompiler.cs`,
`StateMachineGuard.cs`, `EntityTimerPlanner.cs`, `EntityLifecycleService.cs`, `TransitionSimulator.cs`, `Setup.cs`;
`Descriptor/Internal/{StateMachineCheck,EntityTimerCheck}.cs`; modified `Rules/PolicyCatalog.cs` (`EntityPolicy.Lifecycle`),
`Rules/Internal/PolicyCatalogBuilder.cs`, `Expressions/Internal/{CelCompiler,CelInterpreter,IValueCompiler}.cs`,
`Descriptor/Internal/DescriptorValidator.cs`, `Scheduling/{AlvoScheduler,TimerEntries}.cs`,
`Hooks/Internal/{WriteExtensionRunner,HookSets}.cs`, `Api/Internal/ProblemResultFactory.cs`,
`Management/Internal/AlvoManagementService.cs`, `AlvoServiceCollectionExtensions.cs`.
**EF driver:** `Internal/{EfAlvoData.cs,TimerWrites.cs (new),TimerTable.cs,EfCoreTimerStore.cs}`, `AlvoEfCoreProvider.cs`.
**Schema:** `schema/project.schema.json`, `examples/_negative/{state-machine-no-transitions,timer-without-at}.json` + `expectations.json`.
**Tests:** `test/MMLib.Alvo.Tests/{Descriptor/{StateMachineCheckTests,EntityTimerCheckTests},Lifecycle/{LifecycleCompilerTests,StateMachineGuardTests,EntityTimerPlannerTests,TransitionSimulatorTests},Scheduling/AlvoSchedulerTests}.cs`;
`test/MMLib.Alvo.Api.Tests/TransitionViolationAnswerTests.cs`; `test/_shared/testhost/{StateMachineFacts,EntityTimerFacts}.cs`,
`test/_shared/testhost/descriptors/lifecycle-desk.alvo.json`; `test/MMLib.Alvo.TestHost.Tests/{SqliteStateMachineTests,SqliteEntityTimerTests}.cs`;
`test/MMLib.Alvo.Data.PostgreSql.Tests.Integration/{PostgreSqlStateMachineTests,PostgreSqlEntityTimerTests}.cs`;
`test/MMLib.Alvo.Admin.Tests/Schema/StateGraphLayoutTests.cs`; `test/MMLib.Alvo.Admin.Tests.EndToEnd/{StateMachineWorld,StateMachineScenarios}.cs`.
**Admin:** `Components/Schema/{EntityTabs.cs,Entity.razor,StatesTab.razor,StatesTab.razor.cs,StateGraphLayout.cs}`,
`Components/Rules/{PolicyVerdictView.razor,TransitionVerdicts.cs}`.
**Docs:** `website/src/content/docs/extending/state-machines.mdx`, `website/src/snippets/extending/state-machines/{bookings.alvo.json,BookingLifecycleTests.cs}`;
`docs/architecture/state-machines-and-timers.md` (new), `docs/architecture/time.md`;
`plugins/alvo/skills/{alvo-descriptor-traits-and-tenancy,alvo-descriptor-hooks}/SKILL.md`; `CHANGELOG.md`; spec; `docs/PLAN.md`.

---

# Slice A — engine core (Tasks 1–6)

## Task 1: The descriptor members, the JSON Schema and the apply checks

**Files:**
- Modify: `schema/project.schema.json` (`$defs/entity/properties`: `stateMachine`, `timers`), `src/MMLib.Alvo.Abstractions/Descriptor/EntityDescriptor.cs`, `src/MMLib.Alvo/Descriptor/Internal/DescriptorValidator.cs` (`TryReadModel`, beside `ExclusionIndexCheck.Check`), `examples/_negative/expectations.json`
- Create: `src/MMLib.Alvo.Abstractions/Descriptor/EntityLifecycleDescriptors.cs`, `src/MMLib.Alvo/Descriptor/Internal/{StateMachineCheck,EntityTimerCheck}.cs`, `examples/_negative/{state-machine-no-transitions,timer-without-at}.json`
- Test: `test/MMLib.Alvo.Schema.Tests/RoundTripTests.cs` (CsCheck generator), `test/MMLib.Alvo.Tests/Descriptor/{StateMachineCheckTests,EntityTimerCheckTests}.cs`, Abstractions public-API baseline

**Interfaces — Produces:** the public descriptor records of Global Constraints (`init` members, XML docs, the
`IndexExclusion` style); `internal static class StateMachineCheck { internal static IReadOnlyList<DescriptorValidationError> Check(AlvoDescriptor d); }`
and `EntityTimerCheck` likewise; `internal static string EntityTimerCheck.EventOf(string entity, string timer, EntityTimer t)`
(the declared `event`, else `<entity>.<timer>` with `-` → `_`) — Task 5 uses it.

- [ ] **Step 1: Schema + failing schema tests.** Add under `$defs/entity/properties`:

```json
"stateMachine": {
  "type": "object", "additionalProperties": false, "required": ["field", "transitions"],
  "description": "Which values an enum field may move between. An update that changes `field` must match a declared transition, judged in the write's transaction before any host hook, or it is refused 422 (403 when the caller holds none of its roles). A delete is not a transition.",
  "properties": {
    "field": { "type": "string", "pattern": "^[a-z][a-z0-9_]{0,62}$", "description": "A stored enum field that holds the state." },
    "initial": { "type": "string", "description": "The value every create must store in `field`." },
    "transitions": {
      "type": "object", "minProperties": 1, "propertyNames": { "$ref": "#/$defs/identifier" },
      "description": "Named transitions. Several may reach one state; the move is allowed when any one admits it.",
      "additionalProperties": {
        "type": "object", "additionalProperties": false, "required": ["from", "to"],
        "properties": {
          "description": { "type": "string" },
          "from": { "type": "array", "minItems": 1, "uniqueItems": true, "items": { "type": "string" }, "description": "The states it leaves; [\"*\"] alone is any state, including none." },
          "to": { "type": "string", "description": "The state it enters." },
          "roles": { "type": "array", "minItems": 1, "uniqueItems": true, "items": { "type": "string" }, "description": "The caller must hold one of these roles, or the move is refused 403." },
          "when": { "$ref": "#/$defs/cel", "description": "A condition over old, new, changed() and @user; false refuses the move 422." }
        }
      }
    }
  }
},
"timers": {
  "type": "object", "propertyNames": { "$ref": "#/$defs/identifier" },
  "description": "Durable per-record timers, armed, moved and cancelled in the write's own transaction. Each fires its event once at its latest time, for host event handlers.",
  "additionalProperties": {
    "type": "object", "additionalProperties": false, "required": ["at"],
    "properties": {
      "description": { "type": "string" },
      "at": { "$ref": "#/$defs/cel", "description": "When it fires: a timestamp over the record's own fields, now() and duration('…'), e.g. starts_at - duration('24h'). Null leaves it unarmed." },
      "when": { "$ref": "#/$defs/cel", "description": "Armed only while this condition over the record's own fields holds; a write that makes it false cancels the timer." },
      "event": { "type": "string", "pattern": "^[a-z][a-z0-9_]*(\\.[a-z][a-z0-9_]*)+$", "description": "The custom event it fires; defaults to <entity>.<timer> with '-' as '_'." }
    }
  }
}
```

Negative examples (schema-invalid only — apply refusals live in Step 3): `state-machine-no-transitions.json`
(`"transitions": {}` → `minProperties`) and `timer-without-at.json`, each with its `expectations.json` row. Extend the `RoundTripTests` descriptor generator with
an optional `stateMachine` over a generated enum field and an optional `timers` entry. Run
`dotnet test --project test/MMLib.Alvo.Schema.Tests` — FAIL (round trip drops the members).

- [ ] **Step 2: Models.** `EntityDescriptor.StateMachine`, `EntityDescriptor.Timers`; the three records in
`EntityLifecycleDescriptors.cs`. The source-generated `AlvoDescriptorJsonContext` picks them up through `AlvoDescriptor`.
Re-run the schema tests — PASS. `python3 scripts/gen-prototype-fixtures`. Accept the Abstractions baseline (exactly the
descriptor symbols).

- [ ] **Step 3: Failing apply-check tests** (`StateMachineCheckTests`, each asserting the error's `Path` and that the
fix names the remedy; every problem reported at once):
`A_state_machine_over_a_field_that_is_not_a_stored_enum_is_refused` (text field; computed enum),
`A_transition_naming_an_undeclared_state_is_refused` (`/entities/e/stateMachine/transitions/t/to`),
`An_initial_state_outside_the_values_or_unlike_the_default_is_refused`,
`A_wildcard_source_beside_other_sources_is_refused`, `A_transition_into_a_state_it_leaves_is_refused`,
`A_state_machine_over_a_hidden_field_is_refused` (static `true` and a role expression),
`A_state_machine_or_timers_on_a_dynamic_entity_is_refused`, `Every_problem_is_reported_at_once`.
`EntityTimerCheckTests`: `A_default_event_in_a_reserved_namespace_is_refused` (entity `entity`, timer `x` →
`entity.x`; fix: declare `event`), `An_event_in_the_alvo_namespace_is_refused`, `The_default_event_maps_dashes_to_underscores`
(`work-orders` + `visit-reminder` → `work_orders.visit_reminder`). Run — FAIL.

- [ ] **Step 4: Implement** `StateMachineCheck` and `EntityTimerCheck` in `ExclusionIndexCheck`'s shape (a `Declared`
record carrying entity name, descriptor and path; one finding method per rule returning `DescriptorValidationError?`;
`DescriptorValidationSeverity.Error`). The event check calls `AlvoEventName.IsWellFormed` and
`AlvoEventName.IsReservedNamespace(type.Split('.')[0])` (both reachable through IVT). Register both in
`DescriptorValidator.TryReadModel` after `ExclusionIndexCheck.Check`. Run — PASS.

- [ ] **Step 5: Commit** — `feat(descriptor): declare stateMachine and timers on an entity`.

## Task 2: Compile into the catalog — `EntityPolicy.Lifecycle`, timestamp-valued `at`

**Files:**
- Create: `src/MMLib.Alvo/Lifecycle/{EntityLifecycle,LifecycleCompiler}.cs`, `src/MMLib.Alvo/Expressions/Internal/IValueCompiler.cs`
- Modify: `src/MMLib.Alvo/Rules/PolicyCatalog.cs`, `src/MMLib.Alvo/Rules/Internal/{PolicyCatalogBuilder,BeforeHookCompiler}.cs`, `src/MMLib.Alvo/Expressions/Internal/{CelCompiler,CelInterpreter}.cs`
- Test: `test/MMLib.Alvo.Tests/Lifecycle/LifecycleCompilerTests.cs`

**Interfaces — Produces:**

```csharp
namespace MMLib.Alvo.Lifecycle;
internal sealed record EntityLifecycle(CompiledStateMachine? StateMachine, IReadOnlyList<CompiledTimer> Timers)
{
    internal static EntityLifecycle None { get; } = new(null, []);
}
internal sealed record CompiledStateMachine(
    string Field, string? Initial, string? Default /* the field's literal default */, IReadOnlyList<CompiledTransition> Transitions);
internal sealed record CompiledTransition(
    string Name, IReadOnlySet<string>? From /* null = "*" */, string To, IReadOnlySet<Role>? Roles, CompiledExpression? When);
internal sealed record CompiledTimer(string Name, CompiledExpression At, CompiledExpression? When, string EventType);

// Rules/PolicyCatalog.cs — appended, so no positional call site moves:
internal sealed record EntityPolicy(...) { internal EntityLifecycle Lifecycle { get; init; } = EntityLifecycle.None; }

namespace MMLib.Alvo.Expressions.Internal;
internal interface IValueCompiler
{
    CelCompilationResult CompileValue(string source, CelProfile profile, EntitySchema entity, CelValueType expected);
}
// CelInterpreter:
internal static DateTimeOffset? EvaluateInstant(CompiledExpression expression, AlvoRecord current, AlvoContext context, DateTimeOffset now);
```

- [ ] **Step 1: Failing tests** (`LifecycleCompilerTests`, through `PolicyCatalog.Build` over a small descriptor, asserting
the errors' paths): `A_transition_role_the_project_does_not_declare_is_refused`,
`A_transition_when_that_is_not_a_boolean_is_refused`, `A_transition_when_may_read_old_new_and_changed`,
`A_timer_at_must_be_a_timestamp` (`starts_at > now()` and `note` refused, naming `Timestamp`),
`A_timer_at_may_use_now_and_durations` (`starts_at - duration('24h')`, `now() + duration('48h')`),
`A_timer_expression_reading_old_new_or_changed_is_refused` (in `at` and in `when`),
`An_entity_without_either_member_compiles_to_none` (`ReferenceEquals(policy.Lifecycle, EntityLifecycle.None)`).
Run — FAIL.

- [ ] **Step 2: `CompileValue`.** `CelCompiler : ICelCompiler, IValueCompiler`. `CompileValue` runs the same
`TryParse` → `ValidateTreeDepth` → `CelTypeChecker.Check` pipeline, then replaces the predicate-result rule with:

```csharp
private static CelCompilationError? ExpectedResult(Authored authored, CelValueType resultType, CelValueType expected, int position) =>
    resultType == expected ? null : new CelCompilationError(
        $"This expression must evaluate to a {expected}; it evaluates to {resultType}.{EchoSentence(authored.Source)}",
        expected == CelValueType.Timestamp
            ? "Give a point in time, e.g. starts_at - duration('24h') or now() + duration('48h')."
            : $"Change the expression so it yields a {expected}.",
        position);
```

(thread an optional `CelValueType? expected` through `Authored`/`CheckAndAssemble`; `ValidateResultType` uses
`ExpectedResult` when it is set, and the existing rules otherwise — `Compile` keeps its behaviour byte for byte.)

- [ ] **Step 3: `EvaluateInstant`** — `EvaluateMutation`'s body with `new EvalState(current, null, context, now,
failClosed: true)`; returns `null` for a null result and the `DateTimeOffset` otherwise; the same
`CelFunctionException` judging and `HookDefect` wrapping as `EvaluateVerdict`.

- [ ] **Step 4: `LifecycleCompiler`.** `internal static EntityLifecycle Compile(EntityDescriptor? d, LifecycleScope scope)`
with `LifecycleScope(EntitySchema Schema, ICelCompiler Compiler, RoleCatalog Roles, string Path, List<DescriptorValidationError> Errors)`.
`Default` = the state field's literal `default` as text (a `$cel` default is already refused, `UnhonouredFeatures`).
Transitions: `From` → `null` for `["*"]`, else an ordinal set; `Roles` → `scope.Roles.TryGet` each (an unknown one
appends an error at `…/roles/<i>` with the declared roles in the fix); `When` → `Compile(source, CelProfile.Condition,
schema)`. Timers: `At` → `(scope.Compiler as IValueCompiler)?.CompileValue(source, CelProfile.Condition, schema,
CelValueType.Timestamp)` — a compiler that is not an `IValueCompiler` appends "timers need Alvo's CEL compiler" at
`…/timers/<t>/at`; `When` → `Condition`; both refused when they read an image: move `BeforeHookCompiler`'s private
`Reads(CelNode, CelRecordState)` and `ReadsChanged(CelNode)` to `internal static class CelImageReads` (same file
folder) and call them. `EventType` = `EntityTimerCheck.EventOf(…)`. In `PolicyCatalogBuilder.BuildEntity`:
`return new EntityPolicy(…) { Lifecycle = LifecycleCompiler.Compile(descriptor, new(build.Schema, build.Compiler, build.Roles, build.Path, build.Errors)) };`.
Run — PASS; run `dotnet test --project test/MMLib.Alvo.Tests --filter-class '*BeforeHook*'` (the moved helpers).

- [ ] **Step 5: Commit** — `feat(lifecycle): compile state machines and timers into the policy catalog`.

## Task 3: The guard, the internal port, and the 422

**Files:**
- Create: `src/MMLib.Alvo.Abstractions/Rules/IEntityLifecycle.cs`, `src/MMLib.Alvo/Lifecycle/{StateMachineGuard,EntityLifecycleService,Setup}.cs`
- Modify: `src/MMLib.Alvo.Abstractions/Data/{AlvoConstraintKind,AlvoConstraintViolationException}.cs`, `src/MMLib.Alvo/Api/Internal/ProblemResultFactory.cs`, `src/MMLib.Alvo/AlvoServiceCollectionExtensions.cs` (call `Lifecycle.Setup.Add`)
- Test: `test/MMLib.Alvo.Tests/Lifecycle/StateMachineGuardTests.cs`, `test/MMLib.Alvo.Api.Tests/TransitionViolationAnswerTests.cs`, Abstractions baseline

**Interfaces — Produces** (Abstractions, `internal`, IVT already grants core and the EF driver):

```csharp
namespace MMLib.Alvo.Rules;
/// The descriptor's stateMachine and timers for the data path. Internal: a host cannot replace it, so the machine cannot be
/// switched off by a registration (fail closed). Synchronous and token-free for IBeforeHookRunner's reason: no network
/// while the write holds its transaction.
internal interface IEntityLifecycle
{
    void EnsureTransition(string entity, DataOperation operation, AlvoRecord candidate, AlvoRecord? previous, AlvoContext caller);
    bool ArmsTimers(string entity);
    IReadOnlyList<TimerChange> TimerChanges(EntityTimerWrite write);
}

namespace MMLib.Alvo.Scheduling;
internal sealed record EntityTimerWrite(string Entity, AlvoRecord? Previous, AlvoRecord? Stored, AlvoContext Caller, DateTimeOffset Now);
internal sealed record TimerChange(string Scope, string Key, TimerEntry? Arm)   // Arm null = cancel by key
{
    internal static TimerChange Cancel(string scope, string key) => new(scope, key, null);
}
```

- [ ] **Step 1: Failing tests.** `StateMachineGuardTests` (pure, over a `CompiledStateMachine` built by
`LifecycleCompiler` from an inline descriptor — draft/confirmed/cancelled/done, `confirm` with `when: new.starts_at > now()`,
`close` with `roles: ["manager"]`, `reopen` from `["*"]` with `roles: ["admin"]`):
`An_update_that_keeps_the_state_is_not_a_transition`, `An_undeclared_transition_is_refused_as_a_transition_violation`
(`Kind == Transition`, `Fields == ["status"]`), `A_transition_whose_when_is_false_is_refused`,
`A_transition_needing_a_role_the_caller_lacks_is_forbidden`, `The_system_caller_takes_a_role_gated_transition_only_when_admin_is_listed`,
`A_wildcard_transition_leaves_any_state_including_none`, `A_create_must_store_the_initial_state`,
`A_create_without_the_state_takes_the_default_as_initial` (a column `DEFAULT` is not in the candidate at stage 3),
`A_when_that_throws_refuses_the_write` (a host CEL function that throws → the exception propagates, never "allowed"),
`Any_one_matching_transition_admits_the_move`. `TransitionViolationAnswerTests`:
`A_transition_violation_is_422_validation_pointing_at_the_state_field` (code `transition`), `The_wording_names_no_state`.
Run — FAIL.

- [ ] **Step 2: Kind and answer.** Append `Transition` to `AlvoConstraintKind` (XML: "A write would move a record's
state field along no transition its state machine declares, or one whose condition refuses this record; fixed by
sending a state a declared transition reaches."). `AlvoConstraintViolationException.MessageFor` gains
`TransitionMessage = "This write would move a record to a state its state machine does not allow from its current one."`.
`ProblemResultFactory.PortRefusalFor`: the `Check` arm becomes `{ Kind: AlvoConstraintKind.Check or AlvoConstraintKind.Transition }`;
`CodeFor`/`FixFor` gain `transition` and `"Send a state a declared transition reaches from the current one — the entity's stateMachine lists them — or leave the field unchanged."`;
`CheckViolation` takes the kind so the field entry carries `transition` and that fix.

- [ ] **Step 3: The guard** (`src/MMLib.Alvo/Lifecycle/StateMachineGuard.cs`):

```csharp
internal static class StateMachineGuard
{
    internal static void Ensure(
        CompiledStateMachine machine, DataOperation operation, AlvoRecord candidate, AlvoRecord? previous, AlvoContext caller)
    {
        var to = candidate[machine.Field] as string;
        if (operation == DataOperation.Create)
        {
            EnsureInitial(machine, candidate.TryGetValue(machine.Field, out var sent) ? sent as string : machine.Default);
            return;
        }

        if (operation != DataOperation.Update || previous is null || string.Equals(previous[machine.Field] as string, to, StringComparison.Ordinal))
        {
            return;
        }

        var declared = Declared(machine, previous[machine.Field] as string, to);
        var permitted = declared.Where(t => t.Roles is null || t.Roles.Overlaps(caller.Roles)).ToList();
        if (declared.Count > 0 && permitted.Count == 0)
        {
            throw new AlvoAuthorizationException(RolesRefusal(declared, to));
        }

        if (!permitted.Any(t => t.When is null || CelInterpreter.EvaluatePredicate(t.When, candidate, previous, caller)))
        {
            throw new AlvoConstraintViolationException(AlvoConstraintKind.Transition, [machine.Field]);
        }
    }

    private static List<CompiledTransition> Declared(CompiledStateMachine machine, string? from, string? to) =>
        [.. machine.Transitions.Where(t => string.Equals(t.To, to, StringComparison.Ordinal) && (t.From is null || (from is not null && t.From.Contains(from))))];

    private static void EnsureInitial(CompiledStateMachine machine, string? stored)
    {
        if (machine.Initial is { } initial && !string.Equals(initial, stored, StringComparison.Ordinal))
        {
            throw new AlvoConstraintViolationException(AlvoConstraintKind.Transition, [machine.Field]);
        }
    }

    private static string RolesRefusal(IReadOnlyList<CompiledTransition> declared, string? to) =>
        $"Moving to '{to}' needs one of the roles {string.Join(", ", declared.SelectMany(t => t.Roles!).Distinct().Select(r => $"'{r}'"))} "
        + $"(transition {string.Join(", ", declared.Select(t => $"'{t.Name}'"))} of the entity's state machine).";
}
```

(`to` is the caller's own value and the roles and names are descriptor-authored — the `AlvoAuthorizationException`
disclosure argument. An empty `declared` falls through to the 422, never to "allowed": `Any` over an empty list is
false.)

- [ ] **Step 4: The service** (`EntityLifecycleService : IEntityLifecycle`, ctor `(IPolicyCatalogProvider catalog,
CelFunctionBudgetPolicy budget)`): `EnsureTransition` reads `catalog.Current?.TryGetEntity(entity, out var p)`;
no machine → return; else `using var pass = budget.Enter();` then `StateMachineGuard.Ensure(…)`. `ArmsTimers` =
`Lifecycle.Timers.Count > 0`. `TimerChanges` delegates to Task 5's planner (stub returning `[]` until then).
`Lifecycle/Setup.cs`: `services.TryAddSingleton<IEntityLifecycle, EntityLifecycleService>();` (internal interface — no
host can supply one). Run — PASS. Accept the Abstractions baseline (`AlvoConstraintKind.Transition` only).

- [ ] **Step 5: Commit** — `feat(lifecycle): refuse undeclared state transitions with a 422`.

## Task 4: Enforce in the write — stage 3, batches, host `Set` (acceptance 1, 2)

**Files:**
- Modify: `src/MMLib.Alvo.Data.EntityFrameworkCore/Internal/EfAlvoData.cs` (ctor + `RunBeforeCreate`, `RunBeforeUpdate`, `RunBeforeCreateOrRefuse`, the batch-update `catch` at the `RunBeforeUpdate` call), `src/MMLib.Alvo.Data.EntityFrameworkCore/AlvoEfCoreProvider.cs` (`CreateData` passes `services.GetRequiredService<IEntityLifecycle>()`), `src/MMLib.Alvo/Hooks/Internal/{WriteExtensionRunner,HookSets}.cs`
- Create: `test/_shared/testhost/{StateMachineFacts.cs,descriptors/lifecycle-desk.alvo.json}`, `test/MMLib.Alvo.TestHost.Tests/SqliteStateMachineTests.cs`, `test/MMLib.Alvo.Data.PostgreSql.Tests.Integration/PostgreSqlStateMachineTests.cs`

**Interfaces — Consumes:** `IEntityLifecycle.EnsureTransition` (Task 3). **Produces:** `lifecycle-desk.alvo.json`
(Task 6 reuses it):

```json
{
  "$schema": "https://alvo.dev/schema/v1/project.json",
  "apiVersion": "alvo.dev/v1",
  "name": "lifecycle-desk",
  "description": "The state-machine and declarative-timer facts' desk.",
  "tenancy": { "enabled": true },
  "auth": { "roles": ["staff", "manager"] },
  "entities": {
    "bookings": {
      "tenancy": "scoped",
      "softDelete": true,
      "fields": {
        "status": { "type": "enum", "values": ["draft", "confirmed", "cancelled", "done"], "default": "draft" },
        "starts_at": { "type": "datetime" },
        "note": { "type": "text" }
      },
      "rules": { "list": "true", "get": "true", "create": "true", "update": "true", "delete": "true" },
      "hooks": { "beforeUpdate": [ { "condition": "new.note == 'skip'", "action": { "mutate": { "status": "done" } } } ] },
      "stateMachine": {
        "field": "status",
        "initial": "draft",
        "transitions": {
          "confirm": { "from": ["draft"], "to": "confirmed", "when": "new.starts_at > now()" },
          "cancel": { "from": ["draft", "confirmed"], "to": "cancelled" },
          "close": { "from": ["confirmed"], "to": "done", "roles": ["manager"] },
          "reopen": { "from": ["*"], "to": "draft", "roles": ["admin"] }
        }
      },
      "timers": {
        "reminder": { "at": "starts_at - duration('24h')", "when": "status == 'confirmed'", "event": "bookings.reminder_due" }
      }
    }
  }
}
```

- [ ] **Step 1: Failing facts** (`StateMachineFacts`, abstract, the `TimeFacts` shape — `DatabaseAsync()`,
`EnsureEngineAvailable()` first in every fact, `AlvoTestHost.StartAsync(…, Time)`):
`An_undeclared_transition_is_refused_and_writes_nothing` (draft → done: `AlvoConstraintViolationException { Kind:
Transition, Fields: ["status"] }`; read back `draft`; no `entity.bookings.updated` row in the outbox — count through the
database connection),
`A_declared_transition_is_taken` (draft → confirmed with a future `starts_at`),
`A_transition_whose_when_is_false_is_refused` (`starts_at` in the past),
`A_transition_needing_a_role_the_caller_lacks_is_forbidden_and_the_role_holder_takes_it` (confirmed → done: `staff` →
`AlvoAuthorizationException`; `manager` → stored),
`A_declarative_mutate_cannot_skip_the_machine` (PATCH `note = "skip"` on a draft → the `mutate` sets `done` → 422),
`A_patch_not_naming_the_state_is_never_refused_by_the_machine` (a `cancelled` row, PATCH `note`),
`A_batch_row_taking_an_undeclared_transition_is_refused_by_its_index` (`UpdateManyAsync` of two rows, the second
draft → done: row refusal index 1, code `transition`, nothing written),
`A_host_before_write_setting_the_state_fails_invalid_set` (`alvo.Entity("bookings", b => b.BeforeWrite("force",
(BeforeWrite<AlvoRecord> w) => { w.Set("status", "confirmed"); return ValueTask.CompletedTask; }))` →
`InvalidOperationException` whose message contains `invalid-set`; row unchanged).
`SqliteStateMachineTests` (temp file, `SqliteTempFile`) and `PostgreSqlStateMachineTests` (one database per fact, the
Windows skip guard and the Docker skip, copied from `PostgreSqlTimeTests`). Run on SQLite — FAIL.

- [ ] **Step 2: The driver.** `EfAlvoData` gains a required `IEntityLifecycle lifecycle` parameter after `depthLimit`
(DI is the only constructor caller) and:

```csharp
private void EnsureTransition(EntitySchema schema, DataOperation operation, AlvoRecord candidate, AlvoRecord? previous, AlvoContext context) =>
    _lifecycle.EnsureTransition(schema.Name, operation, candidate, previous, context);
```

Called **after** the declarative patch is applied and before the method returns, on every path including the no-patch
one: `RunBeforeCreate` → `EnsureTransition(schema, DataOperation.Create, Unmasked(result), null, context)`;
`RunBeforeUpdate` → `EnsureTransition(schema, DataOperation.Update, Merge(preImage, result), preImage, context)`;
`RunBeforeCreateOrRefuse` → after the patch is applied (and on its no-patch return), in a `try` whose
`catch (AlvoConstraintViolationException { Kind: AlvoConstraintKind.Transition } refused)` returns
`(null, Transitioned(index, refused))`; the batch update's `try` around
`RunBeforeUpdate` gains the same `catch`, adding `Transitioned(index, refused)` and `continue`. 
`private static AlvoRowRefusal Transitioned(int index, AlvoConstraintViolationException refused) => new(index, "transition", refused.Message, TransitionFix);`
with the Task 3 fix text as a constant. Replace (an existing row) reaches `RunBeforeUpdate`; a replace that creates
reaches `RunBeforeCreate` — no other site.

- [ ] **Step 3: Host `Set`.** `HookSets` gains `internal const string StateMachineField = "a state-machine field, which moves only through a declared transition";`.
`WriteExtensionRunner.InvalidSet`, before `HookSets.FirstProblem`:

```csharp
if (StateField(catalog, run.Invocation.Entity) is { } state && sets.ContainsKey(state))
{
    return SetRefused(run, state, HookSets.StateMachineField);
}
```

with `private static string? StateField(PolicyCatalog? catalog, string entity) => catalog is not null &&
catalog.TryGetEntity(entity, out var policy) ? policy.Lifecycle.StateMachine?.Field : null;`. Run both engine classes —
PASS (`dotnet test --project test/MMLib.Alvo.TestHost.Tests --filter-class '*SqliteStateMachineTests'`, then the
PostgreSQL project's `*PostgreSqlStateMachineTests`); then `--filter-class '*WriteHook*'` in both (no regression).

- [ ] **Step 4: Commit** — `feat(data): enforce the state machine in the write before host hooks`.

## Task 5: The timer planner and the shared timer writer

**Files:**
- Create: `src/MMLib.Alvo/Lifecycle/EntityTimerPlanner.cs`, `src/MMLib.Alvo/Scheduling/TimerEntries.cs`
- Modify: `src/MMLib.Alvo/Scheduling/AlvoScheduler.cs`, `src/MMLib.Alvo/Lifecycle/EntityLifecycleService.cs`
- Test: `test/MMLib.Alvo.Tests/Lifecycle/EntityTimerPlannerTests.cs`, `test/MMLib.Alvo.Tests/Scheduling/AlvoSchedulerTests.cs`

**Interfaces — Produces:**

```csharp
namespace MMLib.Alvo.Scheduling;
internal static class TimerEntries
{
    internal const string ReservedKeyPrefix = "alvo:";
    /// The one writer of a TimerEntry (HX-13 Decision 15): AlvoScheduler and the planner both build through it.
    internal static TimerEntry Of(string scope, string key, DateTimeOffset at, string eventType,
        IReadOnlyDictionary<string, object?>? data, AlvoContext context, DateTimeOffset now);
}
namespace MMLib.Alvo.Lifecycle;
internal static class EntityTimerPlanner
{
    internal static IReadOnlyList<TimerChange> Plan(IReadOnlyList<CompiledTimer> timers, EntityTimerWrite write);
}
```

- [ ] **Step 1: Failing tests.** `EntityTimerPlannerTests` (pure, compiled from the `lifecycle-desk` entity, a fixed `now`):
`A_create_that_is_due_arms_at_its_time` (key `alvo:bookings:reminder:<id>`, `DueAt` = `starts_at − 24 h`, scope = the
row's tenant), `A_create_that_is_not_due_arms_nothing_and_cancels` (a draft → one cancel),
`An_update_that_moves_the_due_re_arms`, `An_update_that_keeps_the_due_changes_nothing` (empty list),
`A_null_due_cancels` (`starts_at = null` on a confirmed row), `Leaving_the_when_cancels` (confirmed → cancelled),
`A_delete_cancels`, `The_fired_event_carries_entity_id_timer_and_at` (`Arm.MessageType == "bookings.reminder_due"`,
payload has the four keys), `A_global_row_arms_in_the_empty_scope`. `AlvoSchedulerTests`:
`A_key_in_the_reserved_prefix_is_refused` (`ScheduleAsync("alvo:x", …)` and `CancelAsync("alvo:x")` →
`ArgumentException` whose fix says the prefix belongs to declarative timers). Run — FAIL.

- [ ] **Step 2: `TimerEntries`** — lift `AlvoScheduler.TimerOf`'s body (subject `timers/<key>`,
`AlvoCustomEnvelope.Ensure`/`Build`, `StoredProvenance`) into `Of`, with `context` supplied by the caller; `AlvoScheduler`
calls `TimerEntries.Of(scope, key, at, eventType, data, AlvoContext.System(tenant), time.GetUtcNow())` after
`depth.EnsureWithin`, and both `ScheduleAsync` and `CancelAsync` refuse a key starting with `ReservedKeyPrefix` (inside
`Refused(…)`, so an open unit is poisoned as for every other refusal). Existing `TimeFacts` stay green.

- [ ] **Step 3: The planner:**

```csharp
internal static class EntityTimerPlanner
{
    internal static IReadOnlyList<TimerChange> Plan(IReadOnlyList<CompiledTimer> timers, EntityTimerWrite write)
    {
        var row = write.Stored ?? write.Previous ?? throw new ArgumentException("A timer write has a row.", nameof(write));
        var id = (Guid)row[AlvoManagedColumns.Id]!;
        var scope = row[AlvoManagedColumns.TenantId]?.ToString() ?? string.Empty;

        return [.. timers.Select(timer => Change(timer, write, scope, id)).OfType<TimerChange>()];
    }

    private static TimerChange? Change(CompiledTimer timer, EntityTimerWrite write, string scope, Guid id)
    {
        var key = $"{TimerEntries.ReservedKeyPrefix}{write.Entity}:{timer.Name}:{id:D}";
        var next = write.Stored is { } stored ? DueAt(timer, stored, write) : null;
        if (next is not { } due)
        {
            return TimerChange.Cancel(scope, key);
        }

        var before = write.Previous is { } previous ? DueAt(timer, previous, write) : null;
        return before == due ? null : new TimerChange(scope, key, Armed(timer, write, scope, key, id, due));
    }

    private static DateTimeOffset? DueAt(CompiledTimer timer, AlvoRecord row, EntityTimerWrite write) =>
        timer.When is { } when && !CelInterpreter.EvaluatePredicate(when, row, null, write.Caller)
            ? null
            : CelInterpreter.EvaluateInstant(timer.At, row, write.Caller, write.Now);

    private static TimerEntry Armed(CompiledTimer timer, EntityTimerWrite write, string scope, string key, Guid id, DateTimeOffset due) =>
        TimerEntries.Of(scope, key, due, timer.EventType, Data(write.Entity, timer.Name, id, due),
            AlvoContext.System(scope.Length == 0 ? null : TenantId.Parse(scope, null)) with { Provenance = write.Caller.Provenance },
            write.Now);

    private static Dictionary<string, object?> Data(string entity, string timer, Guid id, DateTimeOffset due) => new(StringComparer.Ordinal)
    {
        ["entity"] = entity, ["id"] = id.ToString("D"), ["timer"] = timer, ["at"] = due.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture),
    };
}
```

`EntityLifecycleService.TimerChanges`: look up the entity's `Lifecycle.Timers` (none → `[]`), `using var pass =
budget.Enter();`, return `EntityTimerPlanner.Plan(…)`. A `when`/`at` that throws propagates — the write rolls back
(fail closed). Run — PASS, plus `dotnet test --project test/MMLib.Alvo.TestHost.Tests --filter-class '*SqliteTimeTests'`.

- [ ] **Step 4: Commit** — `feat(lifecycle): plan declarative timers from a write's two images`.

## Task 6: Arm in the write's transaction (acceptance 3) **[slice end]**

**Files:**
- Create: `src/MMLib.Alvo.Data.EntityFrameworkCore/Internal/TimerWrites.cs`, `test/_shared/testhost/EntityTimerFacts.cs`, `test/MMLib.Alvo.TestHost.Tests/SqliteEntityTimerTests.cs`, `test/MMLib.Alvo.Data.PostgreSql.Tests.Integration/PostgreSqlEntityTimerTests.cs`
- Modify: `src/MMLib.Alvo.Data.EntityFrameworkCore/Internal/{EfAlvoData,EfCoreTimerStore,TimerTable}.cs`

**Interfaces — Consumes:** `IEntityLifecycle.ArmsTimers`/`TimerChanges` (Tasks 3, 5). **Produces:**

```csharp
internal static class TimerWrites   // the SQL and binding EfCoreTimerStore used privately, now shared
{
    internal static Task<int> UpsertAsync(DbConnection connection, DbTransaction transaction, string table, TimerEntry timer, Guid generation, CancellationToken ct);
    internal static Task<int> CancelAsync(DbConnection connection, DbTransaction transaction, string table, string scope, string key, CancellationToken ct);
}
// TimerTable: internal static Task EnsureAsync(DbConnection connection, string table, CancellationToken ct)  — Ddl + DueIndexDdl
```

- [ ] **Step 1: Failing facts** (`EntityTimerFacts`, abstract; `lifecycle-desk`; a `ReminderLog` handler on
`bookings.reminder_due` as `TimeFacts` registers one; `staff` with the fact's tenant):
`A_reminder_re_arms_when_starts_at_moves` — create draft (`starts_at` = now + 48 h), confirm; update `starts_at` to
now + 72 h; `Advance(25 h)` + drain → nothing; `Advance(24 h)` + drain → exactly one event, data `id` = the row;
`A_reminder_is_cancelled_when_the_status_leaves_confirmed` — confirm, cancel; `Advance(48 h)` + drain → nothing,
`CountTimersAsync().Pending == 0`;
`A_rolled_back_write_leaves_no_timer` — inside `TransactAsync`: create a draft, update it to confirmed, throw → no timer
row and no booking; and a top-level confirm refused by a later failure in the same write (a host after-write that
throws) → `Pending == 0`;
`A_move_racing_a_claimed_fire_fires_once_at_the_new_time` — `Advance` to the due, `ClaimTimersAsync` (the pump's claim),
then update `starts_at` +24 h, then drain → nothing now; `Advance(24 h)` + drain → one event;
`Deleting_the_record_cancels_its_timers` — confirm, soft-delete (the desk's `softDelete`) → `Pending == 0` (a hard
delete takes the same `EmitAsync(Deleted, postImage: null)` site; the planner's `A_delete_cancels` pins the rule);
`The_fired_event_carries_the_writers_correlation` — the fired event's `CorrelationId` equals the confirming write's
event's. `SqliteEntityTimerTests` / `PostgreSqlEntityTimerTests` (Windows skip guard). Run on SQLite — FAIL.

- [ ] **Step 2: `TimerWrites` + table.** Move `EfCoreTimerStore`'s `ExecuteAsync`, `BindTimer`, `BindKey` into
`TimerWrites` (store calls them; behaviour unchanged). `TimerTable.EnsureAsync` executes `Ddl` and `DueIndexDdl`;
`EfCoreTimerStore.EnsureAsync` calls it; `EfAlvoData.EnsureOutboxTableAsync` adds
`await TimerTable.EnsureAsync(connection, _timerTable, cancellationToken);` (field `_timerTable =
TimerTable.NameFor(options.SchemaPrefix)`).

- [ ] **Step 3: Arm at the emit site.** `EmitAsync` becomes `async`; after `OutboxTable.InsertAsync`:

```csharp
await ArmTimersAsync(db, transaction, schema.Name, context, now, postImage, preImage, cancellationToken);

private async Task ArmTimersAsync(
    AlvoDataContext db, IDbContextTransaction transaction, string entity, AlvoContext context, DateTimeOffset now,
    AlvoRecord? postImage, AlvoRecord? preImage, CancellationToken cancellationToken)
{
    if (!_lifecycle.ArmsTimers(entity))
    {
        return;
    }

    var (connection, inner) = (db.Database.GetDbConnection(), transaction.GetDbTransaction());
    foreach (var change in _lifecycle.TimerChanges(new EntityTimerWrite(entity, preImage, postImage, context, ExpressionInstant(now))))
    {
        _ = change.Arm is { } timer
            ? await TimerWrites.UpsertAsync(connection, inner, _timerTable, timer, Guid.CreateVersion7(_time.GetUtcNow()), cancellationToken)
            : await TimerWrites.CancelAsync(connection, inner, _timerTable, change.Scope, change.Key, cancellationToken);
    }
}
```

Every write path already calls `EmitAsync` once per row with the unmasked images (single, replace, batch, delete), so
this is the one site; a replay never reaches it. The upsert's `ON CONFLICT … DO UPDATE` clears `lease_until` and sets a
fresh generation, so a claimed stale fire finds nothing (HX-13 (18)). Lock order: the write holds its row, then the
timer row; `FireAsync` touches the timer row and the outbox only — no cycle. Run both engine classes — PASS; then
`--filter-class '*TimeTests'` and `'*TimerStore*'` on both engines (no regression).

- [ ] **Step 4: Slice gate.** `scripts/test-ring1`; `dotnet build MMLib.Alvo.slnx -c Release`. Both green.

- [ ] **Step 5: Commit** — `feat(data): arm declarative timers in the write's own transaction`.

---

# Slice B — surfaces (Tasks 7–10)

## Task 7: The simulator answers "may role X take transition Y"

**Files:**
- Create: `src/MMLib.Alvo.Abstractions/Management/ManagementTransitionVerdict.cs`, `src/MMLib.Alvo/Lifecycle/TransitionSimulator.cs`
- Modify: `src/MMLib.Alvo.Abstractions/Management/ManagementModels.cs` (`ManagementPolicyVerdict.Transitions`), `src/MMLib.Alvo/Management/Internal/AlvoManagementService.cs` (trailing `TransitionSimulator? transitions = null`), `src/MMLib.Alvo/Lifecycle/Setup.cs`
- Test: `test/MMLib.Alvo.Tests/Lifecycle/TransitionSimulatorTests.cs`, Abstractions baseline; the Management OpenAPI snapshot if it moves (`alvo-snapshot-judge`)

**Interfaces — Produces:**

```csharp
namespace MMLib.Alvo.Management;
/// What a state machine's transition answers for a simulated caller. Init-only, so later members do not break a reader.
public sealed record ManagementTransitionVerdict
{
    public required string Name { get; init; }
    public required IReadOnlyList<string> From { get; init; }   // ["*"] for any state
    public required string To { get; init; }
    public IReadOnlyList<string> Roles { get; init; } = [];     // empty: any caller the update rule admits
    public string? When { get; init; }
    /// false: the caller holds none of the roles. null: a `when` decides it per record. true: the roles admit the caller.
    public bool? Allowed { get; init; }
}
// ManagementPolicyVerdict: public IReadOnlyList<ManagementTransitionVerdict> Transitions { get; init; } = [];
internal sealed class TransitionSimulator(IPolicyCatalogProvider catalog)
{
    internal IReadOnlyList<ManagementTransitionVerdict> Simulate(string entity, string operation, AlvoContext caller);
}
```

- [ ] **Step 1: Failing tests** — `A_role_outside_the_transition_is_not_allowed`, `A_listed_role_is_allowed`,
`A_transition_with_when_is_decided_per_record` (`Allowed == null` when the roles pass, `false` when they do not),
`Only_an_update_lists_transitions` (operation `create`/`delete` → empty), `An_entity_without_a_machine_lists_none`.
Run — FAIL.

- [ ] **Step 2: Implement** — `Simulate` returns `[]` unless `operation == "update"`; otherwise one verdict per compiled
transition in declaration order, `From` = `["*"]` or the ordinal-sorted set, `Allowed` = `Roles is null ||
Roles.Overlaps(caller.Roles) ? (When is null ? true : null) : false`. `AlvoManagementService.SimulatePolicyAsync` adds
`Transitions = transitions?.Simulate(simulation.Entity, simulation.Operation, caller) ?? []`. `Setup`:
`services.TryAddSingleton<TransitionSimulator>()`. Run — PASS; accept the Abstractions baseline
(`ManagementTransitionVerdict`, `ManagementPolicyVerdict.Transitions`).

- [ ] **Step 3: Commit** — `feat(management): simulate state-machine transitions per caller`.

## Task 8: Dashboard — the *States* tab and the simulator (acceptance 4)

**Files:**
- Create: `src/MMLib.Alvo.Admin/Components/Schema/{StatesTab.razor,StatesTab.razor.cs,StateGraphLayout.cs}`, `src/MMLib.Alvo.Admin/Components/Rules/TransitionVerdicts.cs`, `test/MMLib.Alvo.Admin.Tests/Schema/StateGraphLayoutTests.cs`, `test/MMLib.Alvo.Admin.Tests.EndToEnd/{StateMachineWorld,StateMachineScenarios}.cs`
- Modify: `src/MMLib.Alvo.Admin/Components/Schema/{EntityTabs.cs,Entity.razor}`, `src/MMLib.Alvo.Admin/Components/Rules/PolicyVerdictView.razor`

**Interfaces — Consumes:** `EntityDescriptor.StateMachine`/`Timers` from the working copy (the tab draws what is staged,
as every Schema tab does); `ManagementPolicyVerdict.Transitions` (Task 7). **Produces:**

```csharp
internal sealed record StateGraph(IReadOnlyList<StateNode> Nodes, IReadOnlyList<StateEdge> Edges, double Width, double Height);
internal sealed record StateNode(string State, double X, double Y, bool Initial, bool Any);
internal sealed record StateEdge(string Transition, string From, string To, bool Gated /* roles or when */);
internal static class StateGraphLayout { internal static StateGraph For(EntityStateMachine machine, IReadOnlyList<string> values); }
```

- [ ] **Step 1: Failing layout tests** — `Ranks_follow_the_transitions_from_the_initial_state` (BFS from `initial`, or
the first value; a value no transition reaches gets the last rank), `A_wildcard_transition_starts_at_the_any_state_node`
(one extra node `*`, `Any = true`), `Every_transition_is_one_edge_per_source`, `Nodes_never_overlap` (same rank →
distinct Y). Run `dotnet test --project test/MMLib.Alvo.Admin.Tests --filter-class '*StateGraphLayoutTests'` — FAIL.

- [ ] **Step 2: Layout + tab.** `StateGraphLayout`: column per rank (160 px), row per position in rank (72 px), node
box 120×36. `StatesTab.razor`: an inline `<svg role="img" aria-label="State machine of <entity>">` in the System map's
idiom (`Components/Schema/Map` — tokens, no colours of its own, arrowhead `<marker>`), each node
`<g data-testid="sm-state-<state>">`, each edge `<g data-testid="sm-transition-<name>">` (a path + a label: the name,
then `roles`/`when` in the muted style); below it, a table of transitions (name, from, to, roles, when) for screen readers
and phone width, and a *Timers* table (`data-testid="sm-timer-<name>"`: at, when, event). An entity declaring neither
shows "This entity declares no state machine or timers — add `stateMachine` to its descriptor." `EntityTabs`: append
`new("States", States)` with `public const string States = "states";` after `Lifecycle`; `Entity.razor` renders
`<StatesTab Entity=… />` for it.

- [ ] **Step 3: Simulator.** `PolicyVerdictView.razor`: after the commands block, `@if (Verdict.Transitions.Count > 0)`
a `verdict-transitions` field — "Transitions — may this caller take them?" — one row per transition
(`data-testid="verdict-transition-<name>"`, badge from `TransitionVerdicts.Answer`: *Allowed* / *Not allowed* /
*Depends on the record*, tone from `TransitionVerdicts.Tone`; the `from → to` line and the `when` in mono), and the note
"The update rule above applies too; a condition is decided per record — this screen scores no record."

- [ ] **Step 4: E2E.** `StateMachineWorld : AdminWorld` overrides `Descriptor` with field-service plus, on `work_orders`,
`stateMachine { field: status, initial: scheduled, transitions: start (scheduled→in_progress), complete
(in_progress→completed, roles [technician]), cancel (scheduled,in_progress→cancelled, roles [dispatcher], when
"new.scheduled_for > now()") }` and `timers.visit_reminder { at: "scheduled_for - duration('2h')", when:
"status == 'scheduled'" }` — patched in with `JsonNode` over `Descriptors.FieldService`, never a copied file.
`StateMachineScenarios`: `The_states_tab_draws_every_state_and_transition` (`/schema/work_orders?tab=states` → four
`sm-state-*`, three `sm-transition-*`, `sm-timer-visit_reminder`, console clean);
`The_simulator_answers_whether_a_role_may_take_a_transition` (Rules → simulate `work_orders`/`update` as `technician` →
`complete` *Allowed*, `cancel` *Not allowed*; as `dispatcher` → `cancel` *Depends on the record*), driving the
simulator form as `AccessScenarios` does. Run `scripts/test-admin-e2e --filter StateMachine` — PASS.

- [ ] **Step 5: Commit** — `feat(admin): draw an entity's state machine and simulate its transitions`.

## Task 9: Docs page, executed snippets, skills

**Files:**
- Create: `website/src/content/docs/extending/state-machines.mdx`, `website/src/snippets/extending/state-machines/{bookings.alvo.json,BookingLifecycleTests.cs}`
- Modify: `plugins/alvo/skills/alvo-descriptor-traits-and-tenancy/SKILL.md`, `plugins/alvo/skills/alvo-descriptor-hooks/SKILL.md`

- [ ] **Step 1: Snippet test first.** `bookings.alvo.json` — `lifecycle-desk`'s `bookings` without the test-only `hooks`.
`BookingLifecycleTests.cs` (the `integrity/BookingIntegrityTests.cs` shape, `AlvoTestHost` over SQLite, executed by
`test/MMLib.Alvo.DocsSnippets.Tests`): `Skipping_a_step_is_refused`, `Only_a_manager_closes_a_booking`,
`A_confirmed_booking_is_reminded_a_day_before` (fake clock, drain, one `bookings.reminder_due`). Run
`dotnet test --project test/MMLib.Alvo.DocsSnippets.Tests` — PASS (including `SnippetCoverageTests`).

- [ ] **Step 2: The page** — title *State machines and timers in the descriptor*, `sidebar.order` after *Integrity*;
sections: *Declare the states a record may move between* (shape, `from: ["*"]`, `initial`), *What a refused move
answers* (422 `transition` with pointer, 403 roles, batch rows), *Roles and conditions* (`when` over `old`/`new`,
system caller = `admin`, a `when` that fails refuses), *Host hooks see a legal move* (stage order; a host `Set` of the
state is `invalid-set`), *Timers that follow a record* (arming rule of Decision 10, key, scope, the event and its data,
handle it with `AddEventHandler`), *What is not covered* (deletes, dynamic entities, backfill → data migration,
descriptor automation cannot subscribe to the event, transition commands are #375). Both snippets through `<Code>`.
`cd website && npm run build` passes link validation.

- [ ] **Step 3: Skills (product text, byte caps).** `alvo-descriptor-traits-and-tenancy` (≈3.2 KB, room for ~2.5 KB): a
`## State machine and timers` section — the shape in prose with inline code only (no json fence: only worked examples
may carry one), the 422/403, the arming rule, "a timer's event is handled in code"; extend its `description` (≤ 200
chars) with "stateMachine, timers". `alvo-descriptor-hooks` sits at 6 123 / 6 144 bytes: add at most one line ("A host
or declarative before-hook cannot skip a `stateMachine` — see traits-and-tenancy."), trimming an equal number of bytes
from its own prose. Snake-case names in code spans must be names `SkillConformanceTests` knows (use `status`, avoid
`starts_at`). Run `dotnet test --project test/MMLib.Alvo.Ai.Tests --filter-class '*SkillConformanceTests'` — PASS.

- [ ] **Step 4: Commit** — `docs(extending): state machines and timers in the descriptor`.

## Task 10: Architecture doc, CHANGELOG, spec, PLAN **[slice end]**

**Files:** `docs/architecture/state-machines-and-timers.md` (new), `docs/architecture/time.md`, `CHANGELOG.md`, the spec,
`docs/PLAN.md`

- [ ] **Step 1: Architecture doc** — where each check runs (the §4.1 diagram with stage 3a *state machine* and stage 8a
*timers*), the internal port and why it is not replaceable, the guard's decision table (unchanged / undeclared / role /
when / create-initial), the arming table (create / update due moved / unchanged / inactive / delete), the key and scope,
lock order against `FireAsync`, and the 15 Decisions with their cost. `time.md`: a short *Declarative timers* section
pointing at it (the `alvo:` prefix, written at the emit site).

- [ ] **Step 2: CHANGELOG** (`[Unreleased]` → *Added*): **State machines** (HX-18) and **Declarative timers** (HX-18),
one entry each; *Changed*: `AlvoConstraintKind.Transition` appended (an exhaustive `switch` gains a case),
`IAlvoScheduler` refuses keys starting `alvo:`.

- [ ] **Step 3: Spec** — append to §7 *HX-18* a `#### HX-18 settled details` block: the 15 Decisions, one line each,
"verbatim from `docs/superpowers/plans/2026-11-06-hx-18-state-machine-timers.md`", and amend §5 row `18`'s note to the
public list of Global Constraints (Decision 7's deviation named). `docs/PLAN.md`: tick HX-18 where the HX list is kept;
move `← YOU ARE HERE` only if the plan-guard proposes it.

- [ ] **Step 4: Slice gate** — `scripts/test-ring1`; `dotnet build MMLib.Alvo.slnx -c Release`; `scripts/test-ring2`
before the PR; `alvo-plan-guard`; the PR report (`alvo-pr-report`); PR labelled `needs-deep-review`, body `Closes #395`.

- [ ] **Step 5: Commit** — `docs(architecture): state machines and declarative timers`.

## Acceptance criteria → tests

| Issue AC | Test (task) | Engines |
|---|---|---|
| (1) undeclared transition → 422, both engines | `StateMachineFacts.An_undeclared_transition_is_refused_and_writes_nothing` (4) in `SqliteStateMachineTests` + `PostgreSqlStateMachineTests`; the 422 rendering `TransitionViolationAnswerTests.A_transition_violation_is_422_validation_pointing_at_the_state_field` (3) | SQLite, PostgreSQL |
| (2) transition `when`/`roles` honoured | `StateMachineFacts.A_transition_whose_when_is_false_is_refused`, `…A_transition_needing_a_role_the_caller_lacks_is_forbidden_and_the_role_holder_takes_it` (4); `StateMachineGuardTests.The_system_caller_takes_a_role_gated_transition_only_when_admin_is_listed`, `…A_when_that_throws_refuses_the_write` (3) | SQLite, PostgreSQL |
| (3) reminder re-arms on `starts_at` change, cancels on status change, rolled-back write leaves no timer | `EntityTimerFacts.A_reminder_re_arms_when_starts_at_moves`, `…A_reminder_is_cancelled_when_the_status_leaves_confirmed`, `…A_rolled_back_write_leaves_no_timer` (6) | SQLite, PostgreSQL |
| (4) graph renders (admin e2e) | `StateMachineScenarios.The_states_tab_draws_every_state_and_transition` (8) | browser over SQLite host |
| dashboard simulator (§7 *Dashboard*) | `StateMachineScenarios.The_simulator_answers_whether_a_role_may_take_a_transition` (8); `TransitionSimulatorTests` (7) | — |
| docs with executed snippets | `BookingLifecycleTests` (9) | SQLite |
