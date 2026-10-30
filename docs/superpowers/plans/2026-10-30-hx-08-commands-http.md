# HX-08 — Commands, inbound webhooks, HTTP conventions — Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** A host developer can expose a named, access-ruled, idempotent **command** over HTTP (`MapAlvoCommand`, `MapAlvoCommand<TEntity>`) or in process (`AddCommand<T>` + `IAlvoCommands.InvokeAsync`), whose handler makes its external call first and its writes in at most one unit (`ctx.TransactAsync`) that also commits the command's `alvo.command.<name>.completed` event and its `Idempotency-Key` record; can receive **Standard Webhooks** (`MapAlvoWebhookReceiver`: signature, 5-minute window, per-receiver `webhook-id` dedup swept after 7 days) and run the handler as the system in a resolved tenant; can bind the **resolved caller** on any plain endpoint (`.WithAlvoCaller()` + an `AlvoCaller` parameter); can scope conventions to one entity's generated routes (`ForEntity`) and read public endpoint metadata (`AlvoEndpointMetadata`); and can no longer remove Alvo's authorization filter from a generated route by a convention without the route-table build failing (#184). Commands appear in the extension catalog, the dashboard's *Commands* lane and policy simulator, and the assistant's `get_extensions`.

**Architecture:** The HTTP seam lives in core `MMLib.Alvo` (`Api/`, `Commands/`, `Webhooks/`), the durable state in the EF driver behind three **internal** Abstractions seams the core reaches through `InternalsVisibleTo` (`ICommandLedger`, `IAlvoCommandUnit`, `IInboundWebhookLedger`, the `IAlvoSessionSchema` precedent): two new framework tables (`<prefix>_command_requests`, `<prefix>_inbound_webhooks`) created by `SystemSchemaInitializer`. A command is a minimal-API endpoint with one Alvo filter that resolves the caller (`CallerGate`, the Data API's own `CallerResolution`), reads the bound row through the caller's policy (`404` if unreadable), evaluates the access rule — CEL compiled at boot in `CelProfile.Condition` (Ruling 1) — before the handler (`403`), claims the idempotency key, runs the handler **outside any transaction** (R-M7), and finishes: a committed unit already wrote the completion event and the key's commit mark (through `IAlvoCommandUnit` on HX-07's `EfAlvoTransaction`); otherwise a 2xx answer writes both in one autocommit transaction. Registrations join HX-03's `ExtensionRegistry` (one id namespace, E3), boot validation (`ExtensionBindingCompiler`), the apply guard and the catalog. `AlvoCaller` and `AlvoCommandContext` bind from a per-request slot the filter fills, because minimal APIs bind parameters **before** filters run. #184 is made a construction guarantee by a per-route **gate witness** that counts its own composition into the request delegate. Webhook receivers wrap the host's delegate with `RequestDelegateFactory` so Alvo reads and verifies the raw body before the host's binding replays it.

**Tech Stack:** .NET 10 (C# 14), ASP.NET Core minimal APIs (`IEndpointFilter`, `AddEndpointFilterFactory`, `IBindableFromHttpContext<T>`, `RequestDelegateFactory`, `IEndpointConventionBuilder.Finally`, `IStatusCodeHttpResult`/`IValueHttpResult`), `Microsoft.AspNetCore.RateLimiting`, `Microsoft.AspNetCore.OpenApi` (`IOpenApiDocumentTransformer`), `System.Security.Cryptography` (`HMACSHA256`, `CryptographicOperations.FixedTimeEquals`), EF Core relational + ADO.NET, Microsoft.Data.Sqlite, Npgsql, `Microsoft.Extensions.TimeProvider.Testing`, xUnit v3 on Microsoft.Testing.Platform, Shouldly, NSubstitute, Verify + PublicApiGenerator, Testcontainers (PostgreSQL leg), Vacuum (`scripts/ensure-vacuum`), Microsoft.Playwright (admin e2e), Astro/Starlight (docs).

**Spec:** `docs/superpowers/specs/2026-10-14-host-extensibility-design.md` — §7 *HX-08* (scope "§4.9 entirely", docs *Commands and inbound webhooks* + *HTTP conventions*, dashboard *Commands* lane + simulator evaluating a command's access rule, assistant: commands in `get_extensions`, acceptance (1)–(10)); §4.9 (caller binding, `MapAlvoCommand`, `MapAlvoCommand<TEntity>`, `AlvoCommandContext`, R-M7, at-most-once `ctx.TransactAsync`, completion event + `Idempotency-Key` record in the unit, internal commands, inbound webhooks, endpoint metadata, `ForEntity`, #184); §4.1 (stage map: command as a source; "top-level" = no unit on the flow; the external check goes before `ctx.TransactAsync`); §4.5 and *HX-07 settled details* (1)–(54) (the unit `ctx.TransactAsync` builds on: Rulings 1 depth, 5 refusal, 9 poisoning, 12 correlation, 46 un-awaited call, 47 cleanup after commit, 50 decided commit); §4.6 (effective depth; `AlvoChainDepthExceededException` → 508); §4.13 and *HX-04 settled details* (1)–(21) (catalog members appended by the PR that fills them; append-only enums; the Lifecycle lanes; `ManagementPolicyVerdict.Extensions`; the assistant's `get_extensions`); §4.3 and *HX-05 settled details* (typed lambdas → CEL via `ConditionTranslator`); §4.10 *Framework events* (`AlvoFrameworkEvent.Create`); §2.3 X10, X14, X19; §3.3 E2, E3, E10, E12; §5 row `08`; §6.2 (commands: access before handler, empty access refused, simulator parity; inbound webhooks: verification, window, per-receiver dedup; HX-08 is security core); Appendix A M9, M12, M15, M22, M24 (webhook sweep), minors 9–11, R-B1, R-M7; issue #184 (all four of its open questions) and #33. **Prior art read and cited in the Rulings:** Standard Webhooks specification (`webhook-id`/`webhook-timestamp`/`webhook-signature`; signed content `{id}.{timestamp}.{body}`; HMAC-SHA256 keyed by the base64-decoded secret after the `whsec_` prefix; `v1,<base64>` entries, several space-separated for zero-downtime rotation; a 5-minute tolerance both ways; constant-time comparison; dedup on `webhook-id`; the reference libraries' test vector), IETF `draft-ietf-httpapi-idempotency-key-header` (a key scoped per client, a request fingerprint, `409 Conflict` for a request still being processed, `422` for a key reused with a different payload, replay of the first response), RFC 9457 (problem details: one `type` per kind of refusal), RFC 9110 §9.2.2 (POST is neither safe nor idempotent), ASP.NET Core rate limiting (`RequireRateLimiting` is endpoint metadata read by `UseRateLimiter`; partitioned limiters), minimal API endpoint filters (they run after parameter binding; `EndpointBuilder.FilterFactories` is a mutable list a convention can rewrite), `IEndpointConventionBuilder.Finally` ordering (every finally convention runs after the filter pipeline is composed — Ruling 19, measured in Task 2), `IBindableFromHttpContext<T>`, OpenAPI operation transformers.

## Global Constraints

- Worktree `/Users/martiniak/Developer/GitHub/Burgyn/MMLib.Alvo-wt-hx08`, branch `hx/08-commands-http`, based on `hx/07-transactions`; #424 (HX-07) has merged and `main` is merged into this branch at `f18c51e6` — every diff is against `origin/main` and the PR targets `main` (Task 15 merges `origin/main` once more if it moved, merge-commit style). Issue **#385**, epic #370; closes **#184**, advances #33. One writer per worktree; never `git add -A` (stage each task's files by path); `git branch --show-current` prints `hx/08-commands-http` before every commit and before the push. **Never run `git clean`**, and never delete anything under `.superpowers/`. A bare `ls` hides git-ignored files in this shell — use `/bin/ls` or `find`.
- **Scope is §7 HX-08 only.** No write hooks, no `write.Transaction`, no `IWriteExtensionRunner`, no validate stage (HX-09); no `ALVO1001`, no host-function budget (HX-10); no timers or schedules (HX-13: `alvo.schedule.*` is not minted here); no `MaxConcurrency`/steps (HX-14). Nothing in `schema/project.schema.json` changes. The standalone host (`MMLib.Alvo.Host`) maps no command and no receiver: its OpenAPI document, the committed snapshot `OpenApiDocumentTests.The_document_is_stable.verified.txt`, the Vacuum ruleset and the TeaPie path set do not move.
- **Public surface added is exactly** (every addition §5's row `08` does not list is a ruling below — Rulings 3, 5, 14, 18, 21):
  - Core `MMLib.Alvo.Api`: `AlvoCaller` (`Context`, `BindAsync`), `AlvoEndpointMetadata(string Entity, DataOperation Operation, AlvoEndpointKind Kind)`, `AlvoEndpointKind` (ten members, mirroring the internal `DataApiEndpointKind`), `AlvoWebhookReceiverOptions` (`Tenant`, `ResolveTenant`, `Tolerance`, `MaxBodyBytes`).
  - Core `MMLib.Alvo.Commands`: `AlvoCommandContext` (`Name`, `Caller`, `Data`, `Record`, `TransactAsync<T>`, non-generic `TransactAsync`, `BindAsync`), `IAlvoCommand<TInput>` (`HandleAsync`), `IAlvoCommands` (`InvokeAsync<TInput>`).
  - Core `Microsoft.AspNetCore.Builder`: `AlvoCommandEndpointRouteBuilderExtensions` (five `MapAlvoCommand` overloads), `AlvoEndpointConventionBuilderExtensions` (`WithAlvoCaller<TBuilder>`, `ForEntity<TBuilder>`), `AlvoWebhookEndpointRouteBuilderExtensions` (`MapAlvoWebhookReceiver`).
  - Core `Microsoft.Extensions.DependencyInjection`: `AlvoExtensionBuilderExtensions.AddCommand<TCommand>` (CEL string and typed-lambda overloads).
  - Abstractions `MMLib.Alvo.Management`: `AlvoExtensionKind.Command = 1`; `AlvoExtensionStage.Http = 1`, `AlvoExtensionStage.PreTransaction = 2`; `ManagementExtension.Route`, `.HttpMethod`, `.Access` (init); `ManagementPolicyVerdict.Commands` (init, default `[]`); `ManagementCommandVerdict` (required-init record).
    `AlvoWebhookReceiverOptions.ResolveTenant` is `Func<HttpRequest, ReadOnlyMemory<byte>, TenantId?>` — the verified body is passed in (Ruling 37).
  Everything else is `internal`.
- **Wire and naming constants (verbatim):** command names `^[a-z][a-z0-9_]*(\.[a-z][a-z0-9_]*)*$` (E3; one namespace with every extension id); completion event type `alvo.command.<name>.completed` (E12), subject `commands/<name>` (unbound) or `<entity>/<id>` (bound), payload `command`, and for a bound command `entity` and `id`; operationId `commands.<lowerCamelCase of the name with '.' and '_' removed>` (e.g. `confirm_booking` → `commands.confirmBooking`), tag `commands`; commands are `POST`; `Idempotency-Key` (header, one field value, `AlvoIdempotency.EnsureUsableKey`'s rules) scoped `command:<name>/<AlvoIdempotency.IdentityOf(caller)>/<API key id, or ->` (Ruling 34); claim lease **15 minutes**, fenced by a random `claim_token` (Ruling 36); stored answer cap **65 536** bytes; command keys and their answers swept **24 hours** after their claim (Ruling 41); Standard Webhooks headers `webhook-id`, `webhook-timestamp`, `webhook-signature`, secret prefix `whsec_`, signature version `v1`, tolerance **5 minutes** (both directions, configurable), `webhook-id` at most **255** UTF-8 bytes, body at most **1 048 576** bytes by default (`413` past it), dedup retention **7 days**, sweep every **1 hour**; framework tables `<prefix>_command_requests` (with `claim_token`) and `<prefix>_inbound_webhooks` (with `claim_token`) (E10, `AlvoFrameworkTables` suffixes `_command_requests`, `_inbound_webhooks`); catalog wire names `command`, `http`, `preTransaction`.
- **Engine parity:** every command fact over HTTP runs on SQLite (`MMLib.Alvo.Api.Tests`) and PostgreSQL (`MMLib.Alvo.Api.Tests.Integration`) through one shared abstract class in `test/_shared/api` (the `DataApiEngineTests` arrangement); every ledger fact runs on both engines through one shared class in `test/_shared/ef`. New SQL is ANSI only (`INSERT`, `UPDATE … WHERE`, `DELETE … WHERE`, `SELECT`, `CREATE TABLE IF NOT EXISTS`, `CREATE INDEX IF NOT EXISTS`) — no dialect member is added.
- **Security core (§6.2 lists HX-08):** access is judged by **one** `CommandAccessJudge` — the HTTP filter, `IAlvoCommands` and the simulator all ask it — which refuses before evaluating when the rule reads a context value the caller lacks (Ruling 33); a rule may not read a `hidden` field (Ruling 35); access is evaluated **before** the handler; an empty access rule refuses registration; an unreadable row is `404` before the access rule and the handler; the simulator answers with the same compiled expression and evaluator as the filter; webhook verification precedes dedup and tenant resolution; signature comparison is constant-time; no log carries a body, a secret, a signature or a caller-supplied value. Task 15 runs the `alvo-security-core-review` checklist, a labelled `/security-review` substitute, and labels the PR `needs-deep-review`.
- **Data-package allow-lists:** `ChangeTrackerReachTests._sqlComposingFiles` gains exactly `CommandLedgerTable.cs` and `InboundWebhookTable.cs`; the two stores (`EfCoreCommandLedger.cs`, `EfCoreInboundWebhookLedger.cs`) and the `EfAlvoData.Units.cs` additions call those tables' helpers and compose no SQL themselves.
- Public XML docs cite no `§` and no `#issue` (DocsGen's `RemarksFilterTests`). XML docs on every public member.
- C# style: `alvo-dotnet-conventions` — no inline comments, methods ≲ 25 lines, English only, `ConfigureAwait(false)` in `src/`, never in tests (xUnit1030); every test call passes `TestContext.Current.CancellationToken` (xUnit1051). `.cs` files are **UTF-8 with BOM + CRLF**: after creating or editing `.cs` files run `dotnet format whitespace MMLib.Alvo.slnx --include <file> <file> …` (file paths, never a directory).
- Single-test runs: `dotnet test --project <test project> --filter-class '*<ClassName>'` (MTP). Public-API baselines: `*PublicApiApprovalTests` in `test/MMLib.Alvo.Abstractions.Tests` (Abstractions) and `test/MMLib.Alvo.Tests` (core), `*AdminPublicApi*` in `test/MMLib.Alvo.Admin.Tests` (Admin's Razor components, HX-04 Ruling 19). To accept a baseline: run the approval test, read the `*.received.txt` diff — it must add exactly the symbols the task lists — then move it over the `*.verified.txt`. A grown `PublicApi.*.verified.txt` trips the turn-review gate: dispatch `alvo-snapshot-judge`, and justify each added symbol against `alvo-architecture-rules` (*public is the contract*) and the public-surface list above.
- Rings: `scripts/test-ring0` after every task, `scripts/test-ring1` after each task marked **[slice end]**, `scripts/test-ring2` before the PR. `scripts/test-admin-e2e` after Task 13 and in Task 15.
- **CI parity:** rings are Debug on the local SDK; CI builds Release with `TreatWarningsAsErrors`, `AnalysisLevel=latest-recommended` and `EnforceCodeStyleInBuild`. Task 15 runs the Release build in the CI SDK container, as the user, before anything is pushed.
- Commits: Conventional Commits, one per task, each message ending with `Claude-Session: https://claude.ai/code/session_01NJAtafM29iddLwfhb5gnmB`. No `Closes`/`Fixes` keyword anywhere in a commit message; the PR body carries `Closes #385` and `Closes #184` once each.

## Review Focus

0. **A negated rule and a caller without the value it negates** — `!(string(@tenant.id) == '…')` and a tenantless or anonymous caller — must be refused, not admitted by the absent operand's collapse to false: `CommandAccessJudgeTests` (Task 6), `CommandEngineFacts.A_negated_tenant_rule_refuses_a_tenantless_caller` (Task 8), `InternalCommandTests.A_negated_identity_rule_refuses_the_anonymous_caller` (Task 9), `CommandSimulatorTests.A_negated_rule_previews_refused_for_a_caller_without_the_value` (Task 12).
1. **Two requests with one `Idempotency-Key` at once** (a client that retries before the first answer arrives, while the handler is still calling the payment provider) must run the handler **once**: the second is refused `409` (`idempotency-conflict`, "still being processed"), never run beside the first and never answered with a half result — `CommandEngineFacts.Two_concurrent_requests_with_one_key_run_the_handler_once` (Task 8), holding the first handler open on a `TaskCompletionSource`; over SQLite in memory two quick requests never overlap, so a fact without the hold would pass without testing anything.
2. **A handler whose unit committed and which then throws or answers non-2xx** must not run again on a retry with its key — its writes and its completion event are already committed — and the retry gets the stored answer (`500` for a throw), not a second charge — `CommandEngineFacts.A_handler_that_throws_after_its_unit_committed_is_not_run_again` (Task 8), `CommandRunnerTests.A_committed_unit_followed_by_a_non_success_answer_keeps_its_event_and_records_the_answer` (Task 7).
3. **A key spent by one caller replayed by another** must run the second caller's own request, never hand them the first caller's answer — the scope is `command:<name>/<tenant>/<user>` — `CommandEngineFacts.A_key_spent_by_one_caller_is_not_replayed_to_another` (Task 8).
4. **A webhook whose body differs from the signed one by one byte, a header carrying several signatures of which only the rotated one is valid, and an entry of another version (`v1a,…`)** must be refused, accepted and ignored respectively — `StandardWebhookSignatureTests.A_body_changed_by_one_byte_is_refused`, `A_header_with_an_old_and_a_rotated_signature_verifies_on_the_rotated_one`, `Entries_of_another_version_are_ignored` (Task 11).
5. **A convention that silently stops gating, or names an entity that does not exist** — `ForEntity("bokings", …)`, a `MapGroup(...)` finally convention that clears `FilterFactories`, a command mapped after `app.Run()` — must fail loudly (route-table build failed / readiness `Failed` / `InvalidOperationException` at the map call), never leave a rate limiter or a command the host believes it has — `DataApiConventionTests.A_ForEntity_naming_no_declared_entity_fails_the_route_table_build` (Task 1), `A_group_finally_convention_removing_alvos_filter_fails_the_build` (Task 2), `CommandRegistryTests.A_command_mapped_after_the_boot_is_refused` (Task 6).

---

## File structure

Created — core `src/MMLib.Alvo`:

| File | Responsibility |
|---|---|
| `Api/AlvoEndpointKind.cs`, `Api/AlvoEndpointMetadata.cs` | public endpoint metadata on every generated route |
| `Api/AlvoEndpointConventionBuilderExtensions.cs` | `ForEntity`, `WithAlvoCaller` |
| `Api/AlvoCaller.cs` | the resolved caller as a minimal-API parameter |
| `Api/AlvoCommandEndpointRouteBuilderExtensions.cs` | the five `MapAlvoCommand` overloads |
| `Api/AlvoWebhookEndpointRouteBuilderExtensions.cs`, `Api/AlvoWebhookReceiverOptions.cs` | `MapAlvoWebhookReceiver` and its options |
| `Api/Internal/EntityConventions.cs` | the recorder `ForEntity` replays on one entity's endpoints |
| `Api/Internal/AlvoGateWitness.cs` | counts the gate's composition into each route's request delegate (#184) |
| `Api/Internal/CallerGate.cs`, `Api/Internal/AlvoCallerFilter.cs` | caller resolution shared by `WithAlvoCaller` and commands |
| `Api/Internal/CommandDocumentTransformer.cs` | OpenAPI enrichment of command operations |
| `Commands/AlvoCommandContext.cs`, `Commands/IAlvoCommand.cs`, `Commands/IAlvoCommands.cs` | the public command surface |
| `Commands/Setup.cs` | `AddAlvoCommands` |
| `Commands/Internal/CommandRegistration.cs`, `CommandAccess.cs`, `AccessTranslator.cs`, `CommandRegistry.cs` | what a command is, its rule, and where mapped ones are kept |
| `Commands/Internal/CommandBinding.cs`, `CommandProblems.cs`, `CommandApplyRefusals.cs` | boot and apply validation |
| `Commands/Internal/CommandAccessJudge.cs` | the one access judgement — filter, `IAlvoCommands` and simulator (Ruling 33) |
| `Commands/Internal/CommandRun.cs`, `CommandRunner.cs`, `CommandCompletion.cs`, `CommandAnswers.cs`, `CommandKeys.cs`, `CommandInputs.cs` | one run: claim, context, unit, completion, answer |
| `Commands/Internal/CommandEndpointFilter.cs`, `CommandEndpointMetadata.cs`, `CommandOperationIds.cs` | the HTTP filter and marker |
| `Commands/Internal/CommandGateCheck.cs` | the boot's gate-witness check over every command route (Ruling 38) |
| `Commands/Internal/AlvoCommands.cs` | `IAlvoCommands` |
| `Commands/Internal/CommandSimulator.cs`, `CommandDescription.cs`, `CommandLog.cs` | simulator verdicts, catalog items, log records |
| `Webhooks/Setup.cs`; `Webhooks/Internal/StandardWebhookSignature.cs`, `WebhookReceiver.cs`, `WebhookReceivers.cs`, `LedgerSweeper.cs`, `WebhookLog.cs` | inbound webhooks; the hourly sweep of both framework ledgers (Ruling 41) |

Created — Abstractions `src/MMLib.Alvo.Abstractions`: `Events/AlvoFrameworkEvent.cs`; `Data/ICommandLedger.cs`, `Data/IAlvoCommandUnit.cs`, `Data/IInboundWebhookLedger.cs` (internal); `Management/ManagementCommandVerdict.cs`.

Created — EF driver `src/MMLib.Alvo.Data.EntityFrameworkCore`: `Internal/CommandLedgerTable.cs`, `Internal/InboundWebhookTable.cs`, `Internal/OnceDdl.cs`, `EfCoreCommandLedger.cs`, `EfCoreInboundWebhookLedger.cs`.

Created — tests: `test/MMLib.Alvo.Api.Tests/{EndpointMetadataTests.cs,AlvoCallerTests.cs,GateWitnessTests.cs}`, `test/MMLib.Alvo.Api.Tests/Commands/{SqliteCommandTests.cs,CommandDocumentTests.cs}`, `test/MMLib.Alvo.Api.Tests/Webhooks/WebhookReceiverTests.cs`, `test/MMLib.Alvo.Tests/Webhooks/StandardWebhookSignatureTests.cs`, `test/MMLib.Alvo.Api.Tests.Integration/PostgresCommandTests.cs`, `test/_shared/api/{CommandEngineFacts.cs,CommandWorld.cs}`, `test/_shared/api/descriptors/command-desk.alvo.json`, `test/MMLib.Alvo.Tests/Commands/{AccessTranslatorTests.cs,CommandRegistryTests.cs,CommandBindingTests.cs,CommandAccessJudgeTests.cs,CommandKeysTests.cs,CommandRunnerTests.cs,AlvoCommandsTests.cs,CommandSimulatorTests.cs,FakeCommandLedger.cs}`, `test/MMLib.Alvo.Tests/Events/AlvoFrameworkEventTests.cs`, `test/_shared/ef/LedgerFacts.cs`, `test/MMLib.Alvo.Data.Sqlite.Tests/SqliteLedgerTests.cs`, `test/MMLib.Alvo.Data.PostgreSql.Tests.Integration/PostgreSqlLedgerTests.cs`, `test/MMLib.Alvo.TestHost.Tests/InternalCommandTests.cs`, `test/MMLib.Alvo.Admin.Tests/LifecycleLaneCommandTests.cs`, `test/MMLib.Alvo.Admin.Tests.EndToEnd/CommandScenarios.cs`.

Created — docs: `website/src/content/docs/extending/commands.mdx`, `website/src/content/docs/extending/http-conventions.mdx`, `website/src/snippets/extending/commands/{stay-desk.alvo.json,StayDeskCommands.cs,StayDeskCommandsTests.cs}`, `website/src/snippets/extending/http/{front-desk.alvo.json,FrontDeskEndpoints.cs,FrontDeskEndpointsTests.cs}`, `docs/architecture/commands.md`.

Modified: `src/MMLib.Alvo/Api/Internal/{DataApiEndpoints.cs,DataApiEndpointKind.cs,AlvoDataApiConventions.cs,AlvoEndpointDataSource.cs,AlvoDocumentTransformer.cs,DataApiDocumentation.cs,ProblemResultFactory.cs,AlvoExceptionHandler.cs}`, `src/MMLib.Alvo/Api/Setup.cs`, `src/MMLib.Alvo/AlvoServiceCollectionExtensions.cs`, `src/MMLib.Alvo/Extensions/AlvoExtensionBuilderExtensions.cs`, `src/MMLib.Alvo/Extensions/Setup.cs`, `src/MMLib.Alvo/Extensions/Internal/{ExtensionRegistry.cs,ExtensionBindings.cs,ExtensionBindingCompiler.cs,ExtensionBinder.cs,ExtensionIdProblems.cs,ExtensionApplyGuard.cs,ExtensionApplyRefusals.cs,ExtensionCatalog.cs}`, `src/MMLib.Alvo/Management/Internal/AlvoManagementService.cs`, `src/MMLib.Alvo.Abstractions/{AlvoFrameworkTables.cs,Management/AlvoExtensionKind.cs,Management/AlvoExtensionStage.cs,Management/ManagementExtension.cs,Management/ManagementModels.cs}`, `src/MMLib.Alvo.Data.EntityFrameworkCore/{AlvoEfCoreProvider.cs,Internal/SystemSchemaInitializer.cs,Internal/EfAlvoData.Units.cs,Internal/EfAlvoTransaction.cs}`, `src/MMLib.Alvo.Admin/Components/Schema/{LifecycleTab.razor,LifecycleLane.cs}`, `src/MMLib.Alvo.Admin/Components/Rules/PolicyVerdictView.razor`, `src/MMLib.Alvo.Ai/Internal/{ManagementTools.cs,AssistantInstructions.cs}`, `src/MMLib.Alvo.Ai/Instructions/schema-assistant.md`; tests `test/_shared/api/AlvoApiWorld.cs` (two setup members), `test/MMLib.Alvo.Api.Tests/DataApiConventionTests.cs`, `test/MMLib.Alvo.Data.EntityFrameworkCore.Tests/ChangeTrackerReachTests.cs`, `test/MMLib.Alvo.Api.Invariants.Tests.Integration/DocumentContractTests.cs`, `test/MMLib.Alvo.Ai.Tests/ManagementToolsTests.cs`, `test/MMLib.Alvo.Admin.Tests.EndToEnd/{AdminWorld.cs,ExtensionWorld.cs,ExtensionScenarios.cs}`, `test/MMLib.Alvo.DocsSnippets.Tests/MMLib.Alvo.DocsSnippets.Tests.csproj`; the three `PublicApi.*.verified.txt` (Abstractions, core, Admin); `website/astro.config.mjs`, `website/src/content/docs/extending/{index.mdx,testing.mdx,transactions.mdx}`, `website/src/content/docs/guides/call-from-endpoints.mdx`; `docs/architecture/{data-api.md,events.md,extensibility.md}`, `CHANGELOG.md`, `docs/PLAN.md`, the spec; mutation (Task 15, Ruling 42): `stryker-config.json`, `.github/workflows/mutation.yml`, and the new `stryker-config.commands-http.json`.

## Task 1: Public endpoint metadata on every generated route, and `ForEntity`

**Files:**
- Create: `src/MMLib.Alvo/Api/AlvoEndpointKind.cs`, `src/MMLib.Alvo/Api/AlvoEndpointMetadata.cs`, `src/MMLib.Alvo/Api/AlvoEndpointConventionBuilderExtensions.cs` (`ForEntity` only; `WithAlvoCaller` joins it in Task 3), `src/MMLib.Alvo/Api/Internal/EntityConventions.cs`
- Modify: `src/MMLib.Alvo/Api/Internal/DataApiEndpointKind.cs` (`ToPublic`), `src/MMLib.Alvo/Api/Internal/DataApiEndpoints.cs:711-730` (`Protect`), `src/MMLib.Alvo/Api/Internal/AlvoDataApiConventions.cs` (expected entities), `src/MMLib.Alvo/Api/Internal/AlvoEndpointDataSource.cs` (`Build` checks them)
- Test: `test/MMLib.Alvo.Api.Tests/EndpointMetadataTests.cs`, `test/MMLib.Alvo.Api.Tests/DataApiConventionTests.cs` (three facts)
- Baseline: `test/MMLib.Alvo.Tests/PublicApi.MMLib.Alvo.verified.txt`

**Interfaces:**
- Consumes: `DataApiEndpointKind`, `DataApiEndpointKinds.ToDataOperation`, `AlvoDataApiConventions`, `AlvoDataApiConventionException` (all existing, internal).
- Produces: `public enum AlvoEndpointKind`, `public sealed record AlvoEndpointMetadata(string Entity, DataOperation Operation, AlvoEndpointKind Kind)`, `internal static AlvoEndpointKind DataApiEndpointKinds.ToPublic(this DataApiEndpointKind kind)`, `public static TBuilder ForEntity<TBuilder>(this TBuilder builder, string entity, Action<IEndpointConventionBuilder> configure) where TBuilder : IEndpointConventionBuilder`, `internal void AlvoDataApiConventions.Expect(string entity)`, `internal IReadOnlyCollection<string> AlvoDataApiConventions.ExpectedEntities`.

- [ ] **Step 1: Write the failing tests**

`test/MMLib.Alvo.Api.Tests/EndpointMetadataTests.cs`:

```csharp
using Microsoft.AspNetCore.Routing;
using MMLib.Alvo.Api.Internal;

namespace MMLib.Alvo.Api.Tests;

/// <summary>The public endpoint metadata every generated route carries beside the internal marker (spec 2026-10-14 §4.9).</summary>
public sealed class EndpointMetadataTests
{
    private static readonly TestApiKey _admin = new("admin-key", ["admin", "authenticated"], ["*:read", "*:write"]);

    [Fact]
    public async Task Every_generated_endpoint_carries_public_metadata_agreeing_with_the_internal_marker()
    {
        await using var world = await AlvoApiWorld.VehicleRegistryAsync([_admin]);

        var generated = world.Endpoints.Where(endpoint => endpoint.Metadata.GetMetadata<DataApiOperationMetadata>() is not null).ToList();

        generated.Count.ShouldBe(30, "three entities, ten routes each");
        foreach (var endpoint in generated)
        {
            var marker = endpoint.Metadata.GetMetadata<DataApiOperationMetadata>()!;
            var metadata = endpoint.Metadata.GetMetadata<AlvoEndpointMetadata>().ShouldNotBeNull();
            metadata.ShouldBe(new AlvoEndpointMetadata(marker.Entity, marker.Operation, marker.Kind.ToPublic()));
        }
    }

    [Fact]
    public void The_public_kinds_mirror_the_internal_ones_member_for_member() =>
        Enum.GetValues<DataApiEndpointKind>()
            .Select(kind => (Name: kind.ToString(), Value: (int)kind, Public: kind.ToPublic()))
            .ShouldAllBe(entry => entry.Public.ToString() == entry.Name && (int)entry.Public == entry.Value);

    [Fact]
    public void Every_public_kind_has_an_internal_twin() =>
        Enum.GetNames<AlvoEndpointKind>().ShouldBe(Enum.GetNames<DataApiEndpointKind>());
}
```

Append to `test/MMLib.Alvo.Api.Tests/DataApiConventionTests.cs`, before `private static List<RouteEndpoint> Generated`:

```csharp
    /// <summary>
    /// Acceptance (5): <c>ForEntity</c> scopes a convention to one entity's routes — a rate limit on <c>owners</c> refuses
    /// the second owners request and leaves vehicles alone.
    /// </summary>
    [Fact]
    public async Task ForEntity_rate_limits_only_that_entity_s_routes()
    {
        await using var world = await AlvoApiWorld.VehicleRegistryAsync([_admin], new AlvoApiWorldSetup(
            ConfigureServices: AddOnePerWindowLimiter,
            ConfigureApp: app => app.UseRateLimiter(),
            ConfigureDataApiRoutes: routes => routes.ForEntity("owners", owners => owners.RequireRateLimiting(OnePerWindow))));

        using var firstOwners = await world.SendAsync(HttpMethod.Get, "/api/owners", _admin);
        using var secondOwners = await world.SendAsync(HttpMethod.Get, "/api/owners", _admin);
        using var firstVehicles = await world.SendAsync(HttpMethod.Get, "/api/vehicles", _admin);
        using var secondVehicles = await world.SendAsync(HttpMethod.Get, "/api/vehicles", _admin);

        firstOwners.StatusCode.ShouldBe(HttpStatusCode.OK);
        secondOwners.StatusCode.ShouldBe(HttpStatusCode.TooManyRequests, "the limit reaches the entity ForEntity named");
        firstVehicles.StatusCode.ShouldBe(HttpStatusCode.OK);
        secondVehicles.StatusCode.ShouldBe(HttpStatusCode.OK, "and no other entity's route");
    }

    /// <summary>A <c>ForEntity</c> convention reaches every route of its entity — the ten of them — and nothing else.</summary>
    [Fact]
    public async Task ForEntity_reaches_all_ten_routes_of_its_entity_and_no_other()
    {
        await using var world = await AlvoApiWorld.VehicleRegistryAsync([_admin], new AlvoApiWorldSetup(
            ConfigureDataApiRoutes: routes => routes.ForEntity("vehicles", vehicles => vehicles.WithMetadata(new HostMarker()))));

        var marked = Generated(world).Where(endpoint => endpoint.Metadata.GetMetadata<HostMarker>() is not null).ToList();

        marked.Count.ShouldBe(10);
        marked.ShouldAllBe(endpoint => endpoint.Metadata.GetMetadata<AlvoEndpointMetadata>()!.Entity == "vehicles");
    }

    /// <summary>Review focus (5): a typo in <c>ForEntity</c> is a rate limiter the host believes it has, so the build fails.</summary>
    [Fact]
    public async Task A_ForEntity_naming_no_declared_entity_fails_the_route_table_build()
    {
        await using var world = await AlvoApiWorld.VehicleRegistryAsync([_admin], new AlvoApiWorldSetup(
            ConfigureDataApiRoutes: routes => routes.ForEntity("ownres", owners => owners.WithMetadata(new HostMarker()))));

        using var response = await world.SendAsync(HttpMethod.Get, "/api/owners", _admin);

        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        var boot = world.Services.GetRequiredService<AlvoBootState>();
        boot.Phase.ShouldBe(AlvoBootPhase.Failed);
        boot.Failure.ShouldNotBeNull().ShouldContain("ForEntity('ownres')");
    }
```

- [ ] **Step 2: Run them to verify they fail**

Run: `dotnet test --project test/MMLib.Alvo.Api.Tests --filter-class '*EndpointMetadataTests' --filter-class '*DataApiConventionTests'`
Expected: FAIL to compile — `AlvoEndpointMetadata`, `ToPublic`, `ForEntity` do not exist.

- [ ] **Step 3: Write the public types**

`src/MMLib.Alvo/Api/AlvoEndpointKind.cs`:

```csharp
namespace MMLib.Alvo.Api;

/// <summary>Which generated Data API route an endpoint is — finer than the operation it is authorized as.</summary>
/// <remarks>
/// Two kinds can be one operation: <see cref="Query"/> is a second way to reach the <see cref="List"/> read, and
/// <see cref="Replace"/> and both batch updates are authorized as <c>update</c>. Members are appended, never reordered or
/// renumbered.
/// </remarks>
public enum AlvoEndpointKind
{
    /// <summary>The collection read, its parameters in the query string.</summary>
    List = 0,

    /// <summary>The collection read, its parameters in a JSON body.</summary>
    Query = 1,

    /// <summary>The single-row read.</summary>
    Get = 2,

    /// <summary>The create.</summary>
    Create = 3,

    /// <summary>The partial update.</summary>
    Update = 4,

    /// <summary>The create-or-replace of the row the path names.</summary>
    Replace = 5,

    /// <summary>The delete.</summary>
    Delete = 6,

    /// <summary>The batch create.</summary>
    BatchCreate = 7,

    /// <summary>The batch update.</summary>
    BatchUpdate = 8,

    /// <summary>The batch delete.</summary>
    BatchDelete = 9,
}
```

`src/MMLib.Alvo/Api/AlvoEndpointMetadata.cs`:

```csharp
using MMLib.Alvo.Rules;

namespace MMLib.Alvo.Api;

/// <summary>
/// Endpoint metadata on every route the Data API generates: the entity it serves, the operation its authorization gates
/// it as, and which route it is.
/// </summary>
/// <remarks>
/// Read it from <c>Endpoint.Metadata</c> in a convention, a middleware or a rate-limiting partition to tell Alvo's routes
/// apart by entity — <c>ForEntity</c> is built on it. It is set by Alvo in the same call that attaches the authorization
/// filter, so a route carrying it is a route Alvo gates.
/// </remarks>
/// <param name="Entity">The entity the route serves, as the applied descriptor names it.</param>
/// <param name="Operation">The operation the route is authorized as.</param>
/// <param name="Kind">Which generated route it is.</param>
public sealed record AlvoEndpointMetadata(string Entity, DataOperation Operation, AlvoEndpointKind Kind);
```

`src/MMLib.Alvo/Api/AlvoEndpointConventionBuilderExtensions.cs`:

```csharp
using MMLib.Alvo.Api;
using MMLib.Alvo.Api.Internal;

namespace Microsoft.AspNetCore.Builder;

/// <summary>Conventions Alvo adds to endpoint convention builders.</summary>
public static class AlvoEndpointConventionBuilderExtensions
{
    /// <summary>
    /// Applies the conventions <paramref name="configure"/> adds only to the generated routes of <paramref name="entity"/>.
    /// </summary>
    /// <remarks>
    /// Use it on the builder <c>MapAlvoDataApi()</c> returns:
    /// <c>app.MapAlvoDataApi().ForEntity("bookings", b =&gt; b.RequireRateLimiting("bookings"))</c>. A route belongs to an
    /// entity by its <see cref="AlvoEndpointMetadata"/>. On the Data API's builder an entity the applied descriptor does not
    /// declare fails the route-table build — readiness reports <c>Failed</c> — so a misspelt name is never a rate limit the
    /// host believes it has. On any other builder — a <c>MapGroup</c> the Data API is mapped under — the name is not checked.
    /// A named rate-limiting policy is one partition across every endpoint that names it; to limit each entity on its own,
    /// partition on <see cref="AlvoEndpointMetadata.Entity"/> instead.
    /// </remarks>
    /// <typeparam name="TBuilder">The convention builder.</typeparam>
    /// <param name="builder">The convention builder, usually the one <c>MapAlvoDataApi()</c> returned.</param>
    /// <param name="entity">The entity whose routes the conventions apply to.</param>
    /// <param name="configure">Adds the conventions, as on any convention builder.</param>
    /// <returns>The same builder, for chaining.</returns>
    public static TBuilder ForEntity<TBuilder>(this TBuilder builder, string entity, Action<IEndpointConventionBuilder> configure)
        where TBuilder : IEndpointConventionBuilder
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentException.ThrowIfNullOrWhiteSpace(entity);
        ArgumentNullException.ThrowIfNull(configure);

        var scoped = new EntityConventions(entity);
        configure(scoped);
        (builder as AlvoDataApiConventions)?.Expect(entity);
        builder.Add(scoped.Apply);
        if (scoped.HasFinally)
        {
            builder.Finally(scoped.ApplyFinally);
        }

        return builder;
    }
}
```

`src/MMLib.Alvo/Api/Internal/EntityConventions.cs`:

```csharp
using Microsoft.AspNetCore.Builder;

namespace MMLib.Alvo.Api.Internal;

/// <summary>The conventions a <c>ForEntity</c> call collected, replayed on the endpoints of its one entity.</summary>
/// <param name="entity">The entity whose generated routes the conventions apply to.</param>
internal sealed class EntityConventions(string entity) : IEndpointConventionBuilder
{
    private readonly List<Action<EndpointBuilder>> _conventions = [];
    private readonly List<Action<EndpointBuilder>> _finally = [];

    /// <summary>Gets a value indicating whether a finally convention was collected.</summary>
    internal bool HasFinally => _finally.Count > 0;

    /// <inheritdoc/>
    public void Add(Action<EndpointBuilder> convention)
    {
        ArgumentNullException.ThrowIfNull(convention);
        _conventions.Add(convention);
    }

    /// <inheritdoc/>
    public void Finally(Action<EndpointBuilder> finallyConvention)
    {
        ArgumentNullException.ThrowIfNull(finallyConvention);
        _finally.Add(finallyConvention);
    }

    /// <summary>Runs the ordinary conventions on <paramref name="endpoint"/> when it is one of the entity's routes.</summary>
    internal void Apply(EndpointBuilder endpoint) => Replay(_conventions, endpoint);

    /// <summary>Runs the finally conventions on <paramref name="endpoint"/> when it is one of the entity's routes.</summary>
    internal void ApplyFinally(EndpointBuilder endpoint) => Replay(_finally, endpoint);

    private void Replay(List<Action<EndpointBuilder>> conventions, EndpointBuilder endpoint)
    {
        if (!Serves(endpoint))
        {
            return;
        }

        foreach (var convention in conventions)
        {
            convention(endpoint);
        }
    }

    private bool Serves(EndpointBuilder endpoint) =>
        endpoint.Metadata.OfType<AlvoEndpointMetadata>().Any(metadata => string.Equals(metadata.Entity, entity, StringComparison.Ordinal));
}
```

- [ ] **Step 4: Attach the metadata and check the expected entities**

In `src/MMLib.Alvo/Api/Internal/DataApiEndpointKind.cs`, add to `DataApiEndpointKinds`:

```csharp
    /// <summary>The public twin of this kind, which <see cref="AlvoEndpointMetadata"/> carries.</summary>
    /// <remarks>Spelled out rather than cast, so a member added to one enum and not the other fails here, not on the wire.</remarks>
    /// <param name="kind">The endpoint kind.</param>
    internal static AlvoEndpointKind ToPublic(this DataApiEndpointKind kind) => kind switch
    {
        DataApiEndpointKind.List => AlvoEndpointKind.List,
        DataApiEndpointKind.Query => AlvoEndpointKind.Query,
        DataApiEndpointKind.Get => AlvoEndpointKind.Get,
        DataApiEndpointKind.Create => AlvoEndpointKind.Create,
        DataApiEndpointKind.Update => AlvoEndpointKind.Update,
        DataApiEndpointKind.Replace => AlvoEndpointKind.Replace,
        DataApiEndpointKind.Delete => AlvoEndpointKind.Delete,
        DataApiEndpointKind.BatchCreate => AlvoEndpointKind.BatchCreate,
        DataApiEndpointKind.BatchUpdate => AlvoEndpointKind.BatchUpdate,
        DataApiEndpointKind.BatchDelete => AlvoEndpointKind.BatchDelete,
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "Unmapped endpoint kind; give it a public twin."),
    };
```

and pin the internal enum's values to the public ones (`List = 0` … `BatchDelete = 9`, in the order already declared) so `The_public_kinds_mirror_the_internal_ones_member_for_member` holds.

In `DataApiEndpoints.Protect`, replace `.WithMetadata(new DataApiOperationMetadata(entity.Name, kind))` with:

```csharp
            .WithMetadata(
                new DataApiOperationMetadata(entity.Name, kind),
                new AlvoEndpointMetadata(entity.Name, kind.ToDataOperation(), kind.ToPublic()))
```

(add `using MMLib.Alvo.Api;`). In `AlvoDataApiConventions`, add:

```csharp
    private readonly HashSet<string> _expected = new(StringComparer.Ordinal);

    /// <summary>The entities a <c>ForEntity</c> on this builder named, which the applied schema must declare.</summary>
    internal IReadOnlyCollection<string> ExpectedEntities
    {
        get
        {
            lock (_gate)
            {
                return [.. _expected];
            }
        }
    }

    /// <summary>Records that a <c>ForEntity</c> named <paramref name="entity"/>, so the build can refuse a name that serves nothing.</summary>
    /// <param name="entity">The entity named.</param>
    internal void Expect(string entity)
    {
        lock (_gate)
        {
            EnsureOpen();
            _expected.Add(entity);
        }
    }
```

and split the existing sealed check out of `Collect` into `private void EnsureOpen()` (the same `if (_sealed) throw new InvalidOperationException(…)` body), called by both `Collect` and `Expect` under the lock. In `AlvoDataApiConventionException`, replace the primary constructor with two creation paths so a convention that *threw* and a convention that *misses* keep one exception type (one readiness diagnosis):

```csharp
internal sealed class AlvoDataApiConventionException : Exception
{
    internal AlvoDataApiConventionException(Exception failure)
        : base(BuildMessage(failure), failure)
    {
    }

    private AlvoDataApiConventionException(string message)
        : base(message)
    {
    }

    /// <summary>A <c>ForEntity</c> named an entity the applied schema does not declare.</summary>
    /// <param name="entity">The name it used.</param>
    /// <param name="declared">The entities the schema declares.</param>
    internal static AlvoDataApiConventionException NoSuchEntity(string entity, IEnumerable<string> declared) => new(
        $"ForEntity('{entity}') on MapAlvoDataApi() names no entity the applied descriptor declares, so its conventions would "
        + $"reach no route and the Data API has no routes. Declared: {string.Join(", ", declared)}. Name one of them.");

    private static string BuildMessage(Exception failure)
    {
        ArgumentNullException.ThrowIfNull(failure);

        return "A convention attached to MapAlvoDataApi() threw while Alvo was building its endpoints, so the "
            + $"Data API has no routes: {failure.Message}";
    }
}
```

In `AlvoEndpointDataSource.Build`, after the `foreach` that maps the entities and before `return RouteTable.Of(inner)`:

```csharp
        EnsureExpectedEntities(snapshot.Schema.Entities.Select(entity => entity.Name).ToHashSet(StringComparer.Ordinal));
```

with

```csharp
    private void EnsureExpectedEntities(HashSet<string> declared)
    {
        if (_conventions.ExpectedEntities.FirstOrDefault(entity => !declared.Contains(entity)) is { } missing)
        {
            throw AlvoDataApiConventionException.NoSuchEntity(missing, declared.Order(StringComparer.Ordinal));
        }
    }
```

`BuildOrRefuseToRoute` already turns an `AlvoDataApiConventionException` into an empty table, `Failed` readiness and the `AHostConventionFailed` critical log — the message is the one the fact reads.

- [ ] **Step 5: Run the tests to verify they pass, accept the baseline**

Run: `dotnet test --project test/MMLib.Alvo.Api.Tests --filter-class '*EndpointMetadataTests' --filter-class '*DataApiConventionTests'`
Expected: PASS. Then `dotnet test --project test/MMLib.Alvo.Tests --filter-class '*PublicApiApprovalTests'` — the received baseline adds exactly `AlvoEndpointKind` (ten members), `AlvoEndpointMetadata` and `AlvoEndpointConventionBuilderExtensions.ForEntity<TBuilder>`; move it over the verified file and dispatch `alvo-snapshot-judge`.

- [ ] **Step 6: Format, ring0, commit**

```bash
dotnet format whitespace MMLib.Alvo.slnx --include src/MMLib.Alvo/Api/AlvoEndpointKind.cs src/MMLib.Alvo/Api/AlvoEndpointMetadata.cs src/MMLib.Alvo/Api/AlvoEndpointConventionBuilderExtensions.cs src/MMLib.Alvo/Api/Internal/EntityConventions.cs src/MMLib.Alvo/Api/Internal/DataApiEndpointKind.cs src/MMLib.Alvo/Api/Internal/DataApiEndpoints.cs src/MMLib.Alvo/Api/Internal/AlvoDataApiConventions.cs src/MMLib.Alvo/Api/Internal/AlvoEndpointDataSource.cs test/MMLib.Alvo.Api.Tests/EndpointMetadataTests.cs test/MMLib.Alvo.Api.Tests/DataApiConventionTests.cs
scripts/test-ring0
git add src/MMLib.Alvo/Api/AlvoEndpointKind.cs src/MMLib.Alvo/Api/AlvoEndpointMetadata.cs src/MMLib.Alvo/Api/AlvoEndpointConventionBuilderExtensions.cs src/MMLib.Alvo/Api/Internal/EntityConventions.cs src/MMLib.Alvo/Api/Internal/DataApiEndpointKind.cs src/MMLib.Alvo/Api/Internal/DataApiEndpoints.cs src/MMLib.Alvo/Api/Internal/AlvoDataApiConventions.cs src/MMLib.Alvo/Api/Internal/AlvoEndpointDataSource.cs test/MMLib.Alvo.Api.Tests/EndpointMetadataTests.cs test/MMLib.Alvo.Api.Tests/DataApiConventionTests.cs test/MMLib.Alvo.Tests/PublicApi.MMLib.Alvo.verified.txt
git commit -m "feat(api): public endpoint metadata and ForEntity conventions

Claude-Session: https://claude.ai/code/session_01NJAtafM29iddLwfhb5gnmB"
```

---

## Task 2: A marked endpoint is a gated endpoint — the gate witness (#184) **[slice end]**

**Files:**
- Create: `src/MMLib.Alvo/Api/Internal/AlvoGateWitness.cs`
- Modify: `src/MMLib.Alvo/Api/Internal/DataApiEndpoints.cs` (`Protect`), `src/MMLib.Alvo/Api/Internal/AlvoDataApiConventions.cs` (`GateRemoved`), `src/MMLib.Alvo/Api/Internal/AlvoEndpointDataSource.cs` (`Build`, `GetGroupedEndpoints`), `src/MMLib.Alvo/Api/AlvoDataApiEndpointRouteBuilderExtensions.cs` (remarks), `test/_shared/api/AlvoApiWorld.cs` (`ConfigureRouteGroup`)
- Test: `test/MMLib.Alvo.Api.Tests/GateWitnessTests.cs`

**Interfaces:**
- Consumes: Task 1's `AlvoDataApiConventionException` creation paths.
- Produces: `internal sealed class AlvoGateWitness(string entity, DataApiEndpointKind kind)` with `Wrap(IEndpointFilter gate)`, `Compositions`, `Route`; `internal static AlvoDataApiConventionException AlvoDataApiConventionException.GateRemoved(AlvoGateWitness witness)`; `AlvoApiWorldSetup.ConfigureRouteGroup` (`Action<RouteGroupBuilder>?`).

- [ ] **Step 1: Write the failing tests**

In `test/_shared/api/AlvoApiWorld.cs`, append `Action<RouteGroupBuilder>? ConfigureRouteGroup = null` as the last member of `AlvoApiWorldSetup`, and in `StartAsync` replace the group branch with:

```csharp
        IEndpointConventionBuilder routes;
        if (setup.RouteGroupPrefix is { } groupPrefix)
        {
            var group = app.MapGroup(groupPrefix);
            setup.ConfigureRouteGroup?.Invoke(group);
            routes = group.MapAlvoDataApi();
        }
        else
        {
            routes = app.MapAlvoDataApi();
        }
```

`test/MMLib.Alvo.Api.Tests/GateWitnessTests.cs`:

```csharp
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using MMLib.Alvo.Api.Internal;
using MMLib.Alvo.Migrations;
using System.Net;
using System.Text.Json.Nodes;

namespace MMLib.Alvo.Api.Tests;

/// <summary>
/// Acceptance (6), issue #184: a convention that removes Alvo's authorization filter from a generated route fails the
/// route-table build — whichever builder it was attached to and in whichever order it ran — and readiness reports it.
/// </summary>
public sealed class GateWitnessTests
{
    private static readonly TestApiKey _admin = new("admin-key", ["admin", "authenticated"], ["*:read", "*:write"]);

    [Fact]
    public async Task Every_generated_endpoint_composes_alvos_gate_into_its_request_delegate()
    {
        await using var world = await AlvoApiWorld.VehicleRegistryAsync([_admin]);

        var witnesses = world.Endpoints.Select(endpoint => endpoint.Metadata.GetMetadata<AlvoGateWitness>()).ToList();

        witnesses.Count.ShouldBeGreaterThan(0);
        witnesses.ShouldAllBe(witness => witness != null && witness.Compositions > 0);
    }

    [Fact]
    public async Task A_convention_removing_alvos_filter_fails_the_route_table_build() =>
        await ShouldFailTheBuildAsync(new AlvoApiWorldSetup(ConfigureDataApiRoutes: routes => routes.Add(endpoint => endpoint.FilterFactories.Clear())));

    [Fact]
    public async Task A_finally_convention_removing_alvos_filter_fails_the_route_table_build() =>
        await ShouldFailTheBuildAsync(new AlvoApiWorldSetup(ConfigureDataApiRoutes: routes => routes.Finally(endpoint => endpoint.FilterFactories.Clear())));

    [Fact]
    public async Task A_group_finally_convention_removing_alvos_filter_fails_the_build() =>
        await ShouldFailTheBuildAsync(new AlvoApiWorldSetup(
            RouteGroupPrefix: "/v1",
            ConfigureRouteGroup: group => group.Finally(endpoint => endpoint.FilterFactories.Clear())),
            "/v1/api/owners");

    [Fact]
    public async Task A_convention_rebuilding_the_filter_list_with_the_same_filters_still_fails()
    {
        await ShouldFailTheBuildAsync(new AlvoApiWorldSetup(ConfigureDataApiRoutes: routes => routes.Add(endpoint =>
        {
            var kept = endpoint.FilterFactories.Select(factory => (Func<EndpointFilterFactoryContext, EndpointFilterDelegate, EndpointFilterDelegate>)((context, next) => next)).ToList();
            endpoint.FilterFactories.Clear();
            foreach (var factory in kept)
            {
                endpoint.FilterFactories.Add(factory);
            }
        })));
    }

    [Fact]
    public async Task A_convention_adding_a_filter_around_alvos_still_builds_and_still_gates()
    {
        await using var world = await AlvoApiWorld.VehicleRegistryAsync([_admin], new AlvoApiWorldSetup(
            ConfigureDataApiRoutes: routes => routes.AddEndpointFilter((context, next) => next(context))));

        using var anonymousWrite = await world.SendAsync(HttpMethod.Post, "/api/owners", body: new JsonObject { ["name"] = "Ann" });

        world.Services.GetRequiredService<AlvoBootState>().Phase.ShouldBe(AlvoBootPhase.Ready);
        anonymousWrite.StatusCode.ShouldBe(HttpStatusCode.Forbidden, "Alvo's gate still runs behind the host's filter");
    }

    private static async Task ShouldFailTheBuildAsync(AlvoApiWorldSetup setup, string path = "/api/owners")
    {
        await using var world = await AlvoApiWorld.VehicleRegistryAsync([_admin], setup);

        using var response = await world.SendAsync(HttpMethod.Get, path, _admin);

        response.StatusCode.ShouldBe(HttpStatusCode.NotFound, "a route table that could not be gated routes nothing");
        var boot = world.Services.GetRequiredService<AlvoBootState>();
        boot.Phase.ShouldBe(AlvoBootPhase.Failed, "an orchestrator has to be able to drain this pod");
        boot.Failure.ShouldNotBeNull().ShouldContain("removed Alvo's authorization filter");
    }
}
```

(`SendAsync` with no key sends no credential; `owners.create` admits only `admin`, so an anonymous create is refused by the policy behind the gate.)

- [ ] **Step 2: Run them to verify they fail**

Run: `dotnet test --project test/MMLib.Alvo.Api.Tests --filter-class '*GateWitnessTests'`
Expected: FAIL to compile (`AlvoGateWitness`); after Step 3's type alone, the four removal facts FAIL — the routes still answer 200.

- [ ] **Step 3: Write the witness**

`src/MMLib.Alvo/Api/Internal/AlvoGateWitness.cs`:

```csharp
using Microsoft.AspNetCore.Http;

namespace MMLib.Alvo.Api.Internal;

/// <summary>
/// One generated route's proof that Alvo's authorization filter was composed into its request delegate.
/// </summary>
/// <remarks>
/// <para>
/// The gate is added through a factory this type owns, and the factory counts each time ASP.NET Core composes it into the
/// filter pipeline — which it does while it builds the endpoint, after every convention (route, group, finally) has run.
/// So the count answers "did the factory compose Alvo's gate", not "is something in <c>FilterFactories</c> that looks like
/// it": a convention that clears the list, or rebuilds it with look-alike factories, leaves the count where it was. A
/// convention that assigns <c>EndpointBuilder.RequestDelegate</c> outright replaces the handler as well as the gate; that
/// endpoint no longer reaches Alvo's code at all, and the witness does not claim to see it (Ruling 49).
/// </para>
/// <para>
/// It rides on the endpoint as metadata, so the data source can read it back from each built endpoint. A route-level
/// <c>Finally</c> check was the issue's proposal; it is not used because whether a group's finally conventions run before
/// or after a route's is a framework detail, not a contract, so a <c>MapGroup(…).Finally(…)</c> could undo the check. The
/// witness does not depend on any order.
/// </para>
/// </remarks>
/// <param name="route">The route it belongs to, as a refusal names it: <c>create on 'owners'</c>, <c>command 'confirm_stay'</c>.</param>
internal sealed class AlvoGateWitness(string route)
{
    private int _compositions;

    /// <summary>The witness of one generated Data API route.</summary>
    internal AlvoGateWitness(string entity, DataApiEndpointKind kind)
        : this($"{kind.ToWireName()} on '{entity}'")
    {
    }

    /// <summary>Gets how many times the gate has been composed into a request delegate.</summary>
    internal int Compositions => Volatile.Read(ref _compositions);

    /// <summary>Gets the route this witness belongs to, for a refusal message.</summary>
    internal string Route => route;

    /// <summary>The filter factory that composes <paramref name="gate"/> and counts doing so.</summary>
    /// <param name="gate">Alvo's authorization filter for this route.</param>
    internal Func<EndpointFilterFactoryContext, EndpointFilterDelegate, EndpointFilterDelegate> Wrap(IEndpointFilter gate)
    {
        ArgumentNullException.ThrowIfNull(gate);

        return Wrap((_, next) => invocation => gate.InvokeAsync(invocation, next));
    }

    /// <summary>The filter factory that composes <paramref name="factory"/> — a command's gate — and counts doing so.</summary>
    /// <param name="factory">Alvo's filter factory for this route.</param>
    internal Func<EndpointFilterFactoryContext, EndpointFilterDelegate, EndpointFilterDelegate> Wrap(
        Func<EndpointFilterFactoryContext, EndpointFilterDelegate, EndpointFilterDelegate> factory)
    {
        ArgumentNullException.ThrowIfNull(factory);

        return (context, next) =>
        {
            Interlocked.Increment(ref _compositions);
            return factory(context, next);
        };
    }
}
```

In `AlvoDataApiConventionException`, add:

```csharp
    /// <summary>A convention removed Alvo's authorization filter from a generated route (issue 184).</summary>
    /// <param name="witness">The route whose gate was not composed.</param>
    internal static AlvoDataApiConventionException GateRemoved(AlvoGateWitness witness) => new(
        $"A convention removed Alvo's authorization filter from {witness.Route}, so the Data API has no routes. A convention "
        + "may add filters around Alvo's; it may not remove, replace or rebuild them. Look for a convention — on "
        + "MapAlvoDataApi(), in a ForEntity, or on a MapGroup the Data API is mapped under — that clears or rewrites "
        + "EndpointBuilder.FilterFactories.");
```

In `DataApiEndpoints.Protect`, replace the two `AddEndpointFilter` lines and the metadata line with:

```csharp
        var witness = new AlvoGateWitness(entity.Name, kind);
        var route = builder
            .AddEndpointFilter(NoStoreResponseFilter.Instance)
            .AddEndpointFilterFactory(witness.Wrap(filters.For(entity.Name, kind.ToDataOperation())))
            .WithMetadata(
                new DataApiOperationMetadata(entity.Name, kind),
                new AlvoEndpointMetadata(entity.Name, kind.ToDataOperation(), kind.ToPublic()),
                witness)
            .Documenting(entity, kind);
```

(the comment on `NoStoreResponseFilter` stays: it is still first, so it still wraps the gate).

- [ ] **Step 4: Check the witnesses where the endpoints are built**

In `AlvoEndpointDataSource`, change `RouteTable.Of` and add the check:

```csharp
    private sealed record RouteTable(IReadOnlyList<EndpointDataSource> Sources, IReadOnlyList<Endpoint> Endpoints)
    {
        internal static RouteTable NothingIsRoutable { get; } = new([], []);

        internal static RouteTable Of(NestedRouteBuilder inner)
        {
            IReadOnlyList<EndpointDataSource> sources = [.. inner.DataSources];
            IReadOnlyList<Endpoint> endpoints = [.. sources.SelectMany(source => source.Endpoints)];
            Gated(endpoints, before: null);

            return new RouteTable(sources, endpoints);
        }

        internal static void Gated(IEnumerable<Endpoint> endpoints, IReadOnlyDictionary<AlvoGateWitness, int>? before)
        {
            foreach (var witness in endpoints.Select(endpoint => endpoint.Metadata.GetMetadata<AlvoGateWitness>()).OfType<AlvoGateWitness>())
            {
                if (witness.Compositions <= (before?.GetValueOrDefault(witness) ?? 0))
                {
                    throw AlvoDataApiConventionException.GateRemoved(witness);
                }
            }
        }

        internal Dictionary<AlvoGateWitness, int> Counts() =>
            Endpoints.Select(endpoint => endpoint.Metadata.GetMetadata<AlvoGateWitness>()).OfType<AlvoGateWitness>()
                .Distinct().ToDictionary(witness => witness, witness => witness.Compositions);
    }
```

and replace `GetGroupedEndpoints` with a version that checks the group's build too, serialised so two group builds never read each other's counts:

```csharp
    public override IReadOnlyList<Endpoint> GetGroupedEndpoints(RouteGroupContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        var table = Materialise();

        lock (_gate)
        {
            var before = table.Counts();
            IReadOnlyList<Endpoint> grouped = [.. table.Sources.SelectMany(source => source.GetGroupedEndpoints(context))];
            return GatedOrNothing(grouped, before);
        }
    }

    private IReadOnlyList<Endpoint> GatedOrNothing(IReadOnlyList<Endpoint> grouped, Dictionary<AlvoGateWitness, int> before)
    {
        try
        {
            RouteTable.Gated(grouped, before);
            return grouped;
        }
        catch (AlvoDataApiConventionException failure)
        {
            _boot.Failed(failure.Message);
            AHostConventionFailed(_logger, failure.InnerException, failure.Message);
            return [];
        }
    }
```

The ungrouped path needs no catch of its own: `RouteTable.Of` runs inside `Build`, whose caller `BuildOrRefuseToRoute` already catches `AlvoDataApiConventionException`. Extend `GetGroupedEndpoints`' remarks with one sentence: "Each group build re-composes the gate, so the witnesses are read again here, against their counts before it." Rewrite the fifth `<para>` of `MapAlvoDataApi`'s remarks (the "a convention the host attaches … could take it away again" paragraph) as: "Every mapped endpoint carries the API-key context filter, and the route-table build **fails** — empty table, readiness `Failed`, a critical log naming the route — when a convention removes it: each route counts the composition of its own filter into its request delegate, after every route, group and finally convention has run. A convention may add filters around Alvo's; it cannot take Alvo's away without the build failing."

- [ ] **Step 5: Run the tests, then the whole API suite**

Run: `dotnet test --project test/MMLib.Alvo.Api.Tests --filter-class '*GateWitnessTests' --filter-class '*DataApiConventionTests' --filter-class '*DataApiRoutingTests' --filter-class '*LazyRouteMaterialisationTests' --filter-class '*PathBaseTests'`
Expected: PASS. `Every_generated_endpoint_composes_alvos_gate_into_its_request_delegate` is #184's third question measured — the framework composes filter factories while it builds the endpoint: if it reads zero compositions on this runtime, stop and re-plan this task rather than weaken the fact. If `A_group_finally_convention_removing_alvos_filter_fails_the_build` passes **without** Step 4's grouped check (comment it out once to see), record in the PR report that the group's finally conventions ran before the build's witness read on this runtime — the witness is still the order-independent guarantee; the fact stays either way.

- [ ] **Step 6: Format, ring1, commit**

```bash
dotnet format whitespace MMLib.Alvo.slnx --include src/MMLib.Alvo/Api/Internal/AlvoGateWitness.cs src/MMLib.Alvo/Api/Internal/DataApiEndpoints.cs src/MMLib.Alvo/Api/Internal/AlvoDataApiConventions.cs src/MMLib.Alvo/Api/Internal/AlvoEndpointDataSource.cs src/MMLib.Alvo/Api/AlvoDataApiEndpointRouteBuilderExtensions.cs test/_shared/api/AlvoApiWorld.cs test/MMLib.Alvo.Api.Tests/GateWitnessTests.cs
scripts/test-ring1
git add src/MMLib.Alvo/Api/Internal/AlvoGateWitness.cs src/MMLib.Alvo/Api/Internal/DataApiEndpoints.cs src/MMLib.Alvo/Api/Internal/AlvoDataApiConventions.cs src/MMLib.Alvo/Api/Internal/AlvoEndpointDataSource.cs src/MMLib.Alvo/Api/AlvoDataApiEndpointRouteBuilderExtensions.cs test/_shared/api/AlvoApiWorld.cs test/MMLib.Alvo.Api.Tests/GateWitnessTests.cs
git commit -m "feat(api): fail the route-table build when a convention removes Alvo's gate

Claude-Session: https://claude.ai/code/session_01NJAtafM29iddLwfhb5gnmB"
```

---

## Task 3: `AlvoCaller` and `.WithAlvoCaller()` on plain endpoints

**Files:**
- Create: `src/MMLib.Alvo/Api/AlvoCaller.cs`, `src/MMLib.Alvo/Api/Internal/CallerGate.cs`, `src/MMLib.Alvo/Api/Internal/AlvoCallerFilter.cs`
- Modify: `src/MMLib.Alvo/Api/AlvoEndpointConventionBuilderExtensions.cs` (`WithAlvoCaller`), `src/MMLib.Alvo/Api/Setup.cs` (register `CallerGate`, `AlvoCallerFilter`)
- Test: `test/MMLib.Alvo.Api.Tests/AlvoCallerTests.cs`
- Baseline: `test/MMLib.Alvo.Tests/PublicApi.MMLib.Alvo.verified.txt`

**Interfaces:**
- Consumes: `CallerResolution.Presented/ResolveAsync/PublishingAsync`, `ScopeGate`, `ProblemResultFactory.Unauthenticated/ScopeRefused`, `IAlvoContextResolver`, `IAlvoContextAccessor`, `AlvoAuthOptions` (existing).
- Produces: `public sealed class AlvoCaller : IBindableFromHttpContext<AlvoCaller>` (`Context`, `BindAsync`), `internal static AlvoCaller AlvoCaller.For(HttpContext)`, `internal void AlvoCaller.Resolve(AlvoContext)`; `internal sealed class CallerGate` with `ValueTask<CallerAdmission> AdmitAsync(HttpContext http, string? scopeEntity, DataOperation operation)` and `const string AnyEntity = "*"`; `internal readonly record struct CallerAdmission(AlvoPrincipal? Principal, IResult? Refusal)` with `AlvoContext Context`; `internal sealed class AlvoCallerFilter` (`InvokeAsync`); `public static TBuilder WithAlvoCaller<TBuilder>(this TBuilder builder)`.

- [ ] **Step 1: Write the failing tests**

`test/MMLib.Alvo.Api.Tests/AlvoCallerTests.cs`:

```csharp
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using MMLib.Alvo.Auth;
using System.Net;
using System.Text.Json.Nodes;

namespace MMLib.Alvo.Api.Tests;

/// <summary>Acceptance (9): <c>.WithAlvoCaller()</c> resolves the caller exactly as the generated routes do.</summary>
public sealed class AlvoCallerTests
{
    private static readonly TestApiKey _clerk = new("clerk-key", ["admin", "authenticated"], ["owners:read"]);

    [Fact]
    public async Task No_credential_on_an_alvo_caller_endpoint_binds_anonymous()
    {
        await using var world = await WorldAsync();

        using var response = await world.SendAsync(HttpMethod.Get, "/whoami", key: null);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var body = JsonNode.Parse(await response.ReadTextAsync())!;
        body["actor"]!.GetValue<string>().ShouldBe(nameof(AlvoActorKind.Anonymous));
        body["user"]!.GetValue<Guid>().ShouldBe(Guid.Empty);
    }

    [Fact]
    public async Task A_bad_key_on_an_alvo_caller_endpoint_is_401_before_the_handler()
    {
        var calls = 0;
        await using var world = await WorldAsync(() => calls++);

        using var response = await world.SendAsync(HttpMethod.Get, "/whoami", new TestApiKey("ghost-key", ["admin"], ["*:read"]));

        response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        (await response.ReadTextAsync()).ShouldContain("unauthenticated");
        calls.ShouldBe(0, "a refused credential never reaches the handler");
    }

    [Fact]
    public async Task A_good_key_binds_the_key_s_caller_and_publishes_it_to_the_accessor()
    {
        await using var world = await WorldAsync();

        using var response = await world.SendAsync(HttpMethod.Get, "/whoami", _clerk);

        var body = JsonNode.Parse(await response.ReadTextAsync())!;
        body["user"]!.GetValue<Guid>().ShouldBe(_clerk.User);
        body["accessor"]!.GetValue<Guid>().ShouldBe(_clerk.User, "the same caller IAlvoContextAccessor publishes");
    }

    [Fact]
    public async Task A_plain_endpoint_has_no_scope_gate_of_its_own()
    {
        await using var world = await WorldAsync();

        using var response = await world.SendAsync(HttpMethod.Get, "/whoami", _clerk);

        response.StatusCode.ShouldBe(HttpStatusCode.OK, "an owners:read key reaches a plain endpoint; the host's code decides");
    }

    [Fact]
    public async Task Reading_an_alvo_caller_on_an_endpoint_without_the_filter_throws_instead_of_answering_anonymous()
    {
        await using var world = await WorldAsync();

        using var response = await world.SendAsync(HttpMethod.Get, "/forgot-the-filter", _clerk);

        response.StatusCode.ShouldBe(HttpStatusCode.InternalServerError);
    }

    private static Task<AlvoApiWorld> WorldAsync(Action? onCall = null) =>
        AlvoApiWorld.VehicleRegistryAsync([_clerk], new AlvoApiWorldSetup(MapAlvoProblemDetails: true, ConfigureApp: app =>
        {
            app.MapGet("/whoami", (AlvoCaller caller, IAlvoContextAccessor accessor) =>
            {
                onCall?.Invoke();
                return Results.Json(new
                {
                    user = caller.Context.User.Value,
                    actor = caller.Context.ActorKind.ToString(),
                    accessor = accessor.Principal?.Context.User.Value ?? Guid.Empty,
                });
            }).WithAlvoCaller();
            app.MapGet("/forgot-the-filter", (AlvoCaller caller) => Results.Ok(caller.Context.User.Value));
        }));
}
```

(A key the world never registered is presented and unknown — `DataApiAuthTests.A_request_with_an_unknown_api_key_is_401_not_403`'s arrangement.)

- [ ] **Step 2: Run them to verify they fail**

Run: `dotnet test --project test/MMLib.Alvo.Api.Tests --filter-class '*AlvoCallerTests'`
Expected: FAIL to compile — `AlvoCaller`, `WithAlvoCaller` do not exist.

- [ ] **Step 3: Write `AlvoCaller`, `CallerGate` and the filter**

`src/MMLib.Alvo/Api/AlvoCaller.cs`:

```csharp
using Microsoft.AspNetCore.Http;
using System.Reflection;

namespace MMLib.Alvo.Api;

/// <summary>
/// The caller Alvo resolved for this request, as a minimal-API handler parameter.
/// </summary>
/// <remarks>
/// <para>
/// Take it as a parameter on an endpoint mapped with <c>.WithAlvoCaller()</c>, on a command or on a webhook receiver; pass
/// <see cref="Context"/> to <c>IAlvoData</c>, <c>IAlvoSessions.For</c> or <c>IAlvoTransactions</c>. A request with no
/// credential binds <see cref="AlvoContext.Anonymous"/>; a credential that cannot be used is answered 401 before the
/// handler runs — the rules the generated Data API applies.
/// </para>
/// <para>
/// Minimal APIs bind parameters before endpoint filters run, so this object is bound first and filled by Alvo's filter
/// afterwards. On an endpoint no Alvo filter resolves, reading <see cref="Context"/> throws rather than answering as
/// anonymous, so a forgotten <c>.WithAlvoCaller()</c> is a loud error, never a silently unauthenticated endpoint.
/// </para>
/// </remarks>
public sealed class AlvoCaller : IBindableFromHttpContext<AlvoCaller>
{
    private static readonly object _slot = new();
    private AlvoContext? _context;

    private AlvoCaller()
    {
    }

    /// <summary>Gets the resolved caller.</summary>
    /// <exception cref="InvalidOperationException">No Alvo filter resolved a caller for this endpoint.</exception>
    public AlvoContext Context => _context ?? throw new InvalidOperationException(
        "No caller was resolved for this request: the endpoint takes an AlvoCaller but was not mapped with .WithAlvoCaller(), "
        + "MapAlvoCommand or MapAlvoWebhookReceiver. Add .WithAlvoCaller() to the endpoint.");

    /// <summary>Binds the request's caller slot; Alvo's filter fills it before the handler runs.</summary>
    /// <param name="context">The request.</param>
    /// <param name="parameter">The handler parameter being bound.</param>
    /// <returns>The request's caller.</returns>
    public static ValueTask<AlvoCaller?> BindAsync(HttpContext context, ParameterInfo parameter)
    {
        ArgumentNullException.ThrowIfNull(context);

        return ValueTask.FromResult<AlvoCaller?>(For(context));
    }

    /// <summary>The request's one caller slot, created on first use.</summary>
    /// <param name="http">The request.</param>
    internal static AlvoCaller For(HttpContext http)
    {
        if (http.Items.TryGetValue(_slot, out var existing) && existing is AlvoCaller caller)
        {
            return caller;
        }

        var created = new AlvoCaller();
        http.Items[_slot] = created;
        return created;
    }

    /// <summary>Fills the slot with the resolved caller.</summary>
    /// <param name="context">The caller.</param>
    internal void Resolve(AlvoContext context) => _context = context ?? throw new ArgumentNullException(nameof(context));
}
```

`src/MMLib.Alvo/Api/Internal/CallerGate.cs`:

```csharp
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Options;
using MMLib.Alvo.Auth;
using MMLib.Alvo.Auth.Internal;
using MMLib.Alvo.Rules;

namespace MMLib.Alvo.Api.Internal;

/// <summary>What resolving a request's credential decided: the caller, or the refusal to answer with.</summary>
/// <param name="Principal">The resolved key's principal, or <see langword="null"/> for an anonymous caller.</param>
/// <param name="Refusal">The 401 or 403 to answer instead, when the credential cannot be used.</param>
internal readonly record struct CallerAdmission(AlvoPrincipal? Principal, IResult? Refusal)
{
    /// <summary>Gets the caller to act as.</summary>
    internal AlvoContext Context => Principal?.Context ?? AlvoContext.Anonymous;
}

/// <summary>
/// Resolves a request's credential with the same <see cref="CallerResolution"/> the generated routes use: no credential is
/// anonymous, a credential that cannot be used is 401, and — when a scope is asked for — a key whose scopes exclude it is 403.
/// </summary>
internal sealed class CallerGate(IAlvoContextResolver resolver, ScopeGate scopes, IOptions<AlvoAuthOptions> options)
{
    /// <summary>The scope entity a command bound to no entity needs: only a wildcard scope covers it.</summary>
    internal const string AnyEntity = "*";

    /// <summary>Resolves the caller of <paramref name="http"/>.</summary>
    /// <param name="http">The request.</param>
    /// <param name="scopeEntity">The entity a key's scopes must cover, or <see langword="null"/> for no scope check.</param>
    /// <param name="operation">The operation the scopes must cover.</param>
    /// <param name="alsoRead">Whether the scopes must also cover reading <paramref name="scopeEntity"/> (a command bound to its rows, Ruling 39).</param>
    internal async ValueTask<CallerAdmission> AdmitAsync(HttpContext http, string? scopeEntity, DataOperation operation, bool alsoRead = false)
    {
        ArgumentNullException.ThrowIfNull(http);
        var settings = options.Value;
        var presented = CallerResolution.Presented(http.Request, settings.HeaderName);
        if (presented is null)
        {
            return new CallerAdmission(null, null);
        }

        var principal = await CallerResolution.ResolveAsync(resolver, presented, http.Request, settings).ConfigureAwait(false);
        if (principal is null)
        {
            return new CallerAdmission(null, ProblemResultFactory.Unauthenticated(settings.HeaderName));
        }

        return scopeEntity is null || (scopes.Allows(principal, scopeEntity, operation) && (!alsoRead || scopes.Allows(principal, scopeEntity, DataOperation.Get)))
            ? new CallerAdmission(principal, null)
            : new CallerAdmission(principal, ProblemResultFactory.ScopeRefused());
    }
}
```

`src/MMLib.Alvo/Api/Internal/AlvoCallerFilter.cs`:

```csharp
using Microsoft.AspNetCore.Http;
using MMLib.Alvo.Auth;
using MMLib.Alvo.Auth.Internal;
using MMLib.Alvo.Rules;

namespace MMLib.Alvo.Api.Internal;

/// <summary>
/// The filter <c>.WithAlvoCaller()</c> adds: resolve the caller, answer 401 for a credential that cannot be used, fill the
/// request's <see cref="AlvoCaller"/> and publish the caller through <see cref="IAlvoContextAccessor"/> for the handler.
/// </summary>
/// <remarks>No scope check: a plain endpoint names no entity or operation, so the host's code decides what it allows.</remarks>
internal sealed class AlvoCallerFilter(CallerGate gate, IAlvoContextAccessor accessor) : IEndpointFilter
{
    /// <inheritdoc/>
    public async ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(next);

        var admission = await gate.AdmitAsync(context.HttpContext, scopeEntity: null, DataOperation.Get).ConfigureAwait(false);
        if (admission.Refusal is { } refused)
        {
            return refused;
        }

        AlvoCaller.For(context.HttpContext).Resolve(admission.Context);
        return await CallerResolution.PublishingAsync(accessor, admission.Principal, context, next).ConfigureAwait(false);
    }
}
```

Add to `AlvoEndpointConventionBuilderExtensions`:

```csharp
    /// <summary>
    /// Resolves the request's caller for a plain endpoint the way the generated Data API does, and binds it to an
    /// <see cref="AlvoCaller"/> parameter.
    /// </summary>
    /// <remarks>
    /// No credential binds the anonymous caller; a credential that cannot be used is answered 401 before the handler runs.
    /// The caller is also published through <c>IAlvoContextAccessor</c> while the handler runs. No API-key scope is checked:
    /// a plain endpoint names no entity, so what it allows is the host's code — and every <c>IAlvoData</c> call it makes as
    /// the caller is still judged by the descriptor's rules.
    /// </remarks>
    /// <typeparam name="TBuilder">The convention builder.</typeparam>
    /// <param name="builder">The endpoint's builder.</param>
    /// <returns>The same builder, for chaining.</returns>
    public static TBuilder WithAlvoCaller<TBuilder>(this TBuilder builder)
        where TBuilder : IEndpointConventionBuilder
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.Add(endpoint => endpoint.FilterFactories.Add((factory, next) =>
        {
            var filter = factory.ApplicationServices.GetRequiredService<AlvoCallerFilter>();
            return invocation => filter.InvokeAsync(invocation, next);
        }));

        return builder;
    }
```

(add `using Microsoft.Extensions.DependencyInjection;`). In `src/MMLib.Alvo/Api/Setup.cs`, `AddAlvoApi`, beside `AlvoContextFilterFactory`:

```csharp
        services.TryAddSingleton<CallerGate>();
        services.TryAddSingleton<AlvoCallerFilter>();
```

- [ ] **Step 4: Run the tests to verify they pass, accept the baseline**

Run: `dotnet test --project test/MMLib.Alvo.Api.Tests --filter-class '*AlvoCallerTests'`
Expected: PASS. Run the core `*PublicApiApprovalTests`: the diff adds exactly `AlvoCaller` (`Context`, `BindAsync`, the `IBindableFromHttpContext<AlvoCaller>` implementation) and `WithAlvoCaller<TBuilder>`; accept, dispatch `alvo-snapshot-judge`.

- [ ] **Step 5: Format, ring0, commit**

```bash
dotnet format whitespace MMLib.Alvo.slnx --include src/MMLib.Alvo/Api/AlvoCaller.cs src/MMLib.Alvo/Api/Internal/CallerGate.cs src/MMLib.Alvo/Api/Internal/AlvoCallerFilter.cs src/MMLib.Alvo/Api/AlvoEndpointConventionBuilderExtensions.cs src/MMLib.Alvo/Api/Setup.cs test/MMLib.Alvo.Api.Tests/AlvoCallerTests.cs
scripts/test-ring0
git add src/MMLib.Alvo/Api/AlvoCaller.cs src/MMLib.Alvo/Api/Internal/CallerGate.cs src/MMLib.Alvo/Api/Internal/AlvoCallerFilter.cs src/MMLib.Alvo/Api/AlvoEndpointConventionBuilderExtensions.cs src/MMLib.Alvo/Api/Setup.cs test/MMLib.Alvo.Api.Tests/AlvoCallerTests.cs test/MMLib.Alvo.Tests/PublicApi.MMLib.Alvo.verified.txt
git commit -m "feat(api): bind the resolved caller on plain endpoints with WithAlvoCaller

Claude-Session: https://claude.ai/code/session_01NJAtafM29iddLwfhb5gnmB"
```

---

## Task 4: Abstractions — catalog vocabulary, framework events, and the three internal seams

**Files:**
- Create: `src/MMLib.Alvo.Abstractions/Events/AlvoFrameworkEvent.cs`, `src/MMLib.Alvo.Abstractions/Data/ICommandLedger.cs`, `src/MMLib.Alvo.Abstractions/Data/IAlvoCommandUnit.cs`, `src/MMLib.Alvo.Abstractions/Data/IInboundWebhookLedger.cs`, `src/MMLib.Alvo.Abstractions/Management/ManagementCommandVerdict.cs`
- Modify: `src/MMLib.Alvo.Abstractions/Management/AlvoExtensionKind.cs`, `AlvoExtensionStage.cs`, `ManagementExtension.cs`, `ManagementModels.cs` (`ManagementPolicyVerdict.Commands`), `src/MMLib.Alvo.Abstractions/AlvoFrameworkTables.cs`
- Test: `test/MMLib.Alvo.Tests/Events/AlvoFrameworkEventTests.cs`, `test/MMLib.Alvo.Abstractions.Tests/Management/ManagementExtensionTests.cs` (three facts)
- Baseline: `test/MMLib.Alvo.Abstractions.Tests/PublicApi.MMLib.Alvo.Abstractions.verified.txt`

**Interfaces:**
- Consumes: `AlvoEventName.FrameworkNamespace`, `AlvoEventName.IsWellFormed`, `AlvoEventId.Create(DateTimeOffset)`, `AlvoEnvelopeStamp.Of`, `AlvoCustomEnvelope.EnsureWritable` (internal, existing).
- Produces (Abstractions, **internal**, reachable by core and the EF driver through `InternalsVisibleTo`):
  - `static class AlvoFrameworkEvent { AlvoEvent Create(string type, string subject, IReadOnlyDictionary<string, object?> data, AlvoContext context, DateTimeOffset now); }` (namespace `MMLib.Alvo.Events`)
  - `interface ICommandLedger { ClaimAsync; AnswerAsync; ReleaseAsync; CompleteAsync; Task<int> SweepAsync(DateTimeOffset claimedBefore, CancellationToken); }` (fenced, Ruling 36)
  - `sealed record CommandKey(string Scope, string Key, string Fingerprint) { string? Token { get; init; } }`; `sealed record CommandAnswer(int Status, string? Body) { bool IsSuccess; }`; `enum CommandClaimState { Claimed, Answered, InFlight, Unanswered, Mismatch }`; `sealed record CommandClaim(CommandClaimState State, CommandAnswer? Answer = null, string? Token = null)` with statics `Claimable`, `InFlight`, `Unanswered`, `Mismatch`, `Granted(string token)`, `Answered(CommandAnswer)`; `sealed class CommandClaimLostException : InvalidOperationException`; `static class CommandLedgerLimits { TimeSpan Lease = 15 min; TimeSpan Retention = 24 h; int MaxAnswerBytes = 65_536; }`
  - `interface IAlvoCommandUnit { Task PublishFrameworkAsync(AlvoEvent @event, CancellationToken cancellationToken); Task CommitCommandAsync(CommandKey key, DateTimeOffset now, CancellationToken cancellationToken); }` — the commit mark is conditional on `key.Token`; no row → `CommandClaimLostException`, which poisons the unit (Ruling 36)
  - `interface IInboundWebhookLedger { Task<WebhookClaimResult> ClaimAsync(string receiver, string webhookId, DateTimeOffset now, CancellationToken); Task<bool> CompleteAsync(string receiver, string webhookId, string token, DateTimeOffset now, CancellationToken); Task ReleaseAsync(string receiver, string webhookId, string token, CancellationToken); Task<int> SweepAsync(DateTimeOffset receivedBefore, CancellationToken); }`; `enum WebhookClaim { Claimed, Completed, InFlight }`; `readonly record struct WebhookClaimResult(WebhookClaim State, string? Token = null)`; `static class InboundWebhookLimits { TimeSpan Lease = 15 min; TimeSpan Retention = 7 days; TimeSpan SweepInterval = 1 h; int MaxIdBytes = 255; }`
  - `AlvoFrameworkTables.CommandRequestsSuffix = "_command_requests"`, `InboundWebhooksSuffix = "_inbound_webhooks"`, both in `NamesFor`.
- Produces (public, `MMLib.Alvo.Management`): `AlvoExtensionKind.Command = 1`; `AlvoExtensionStage.Http = 1`, `PreTransaction = 2`; `ManagementExtension.Route`, `.HttpMethod`, `.Access`; `ManagementCommandVerdict { required string Name; string? Entity; required string Access; bool? Allowed; }`; `ManagementPolicyVerdict.Commands`.

- [ ] **Step 1: Write the failing tests**

`test/MMLib.Alvo.Tests/Events/AlvoFrameworkEventTests.cs`:

```csharp
using MMLib.Alvo.Events;

namespace MMLib.Alvo.Tests.Events;

/// <summary>Framework events (spec 2026-10-14 E12, §4.10): only the core and the driver mint <c>alvo.*</c>, through one door.</summary>
public sealed class AlvoFrameworkEventTests
{
    private static readonly DateTimeOffset _now = new(2026, 10, 30, 9, 0, 0, TimeSpan.Zero);

    [Fact]
    public void A_command_completion_is_minted_with_its_payload_and_the_caller_s_stamp()
    {
        var caller = AlvoContext.System(new TenantId(Guid.NewGuid()));

        var @event = AlvoFrameworkEvent.Create(
            "alvo.command.confirm_booking.completed", "bookings/42", new Dictionary<string, object?> { ["command"] = "confirm_booking" }, caller, _now);

        @event.Type.ShouldBe("alvo.command.confirm_booking.completed");
        @event.Subject.ShouldBe("bookings/42");
        @event.Data.Record!["command"].ShouldBe("confirm_booking");
        @event.TenantId.ShouldBe(caller.Tenant!.Value.ToString());
        @event.Time.ShouldBe(_now);
        @event.PartitionKey.ShouldBe($"framework.event:{caller.Tenant}:bookings/42", "one tenant's completions never block another's");
    }

    [Theory]
    [InlineData("orders.approved")]
    [InlineData("entity.bookings.updated")]
    [InlineData("alvo")]
    [InlineData("alvo.Command.x.completed")]
    public void Anything_outside_the_framework_namespace_is_refused(string type) =>
        Should.Throw<ArgumentException>(() => AlvoFrameworkEvent.Create(type, "s", new Dictionary<string, object?>(), AlvoContext.Anonymous, _now))
            .ParamName.ShouldBe("type");

    [Fact]
    public void The_custom_door_still_refuses_the_framework_namespace() =>
        Should.Throw<ArgumentException>(() => AlvoEventName.EnsureCustom("alvo.command.x.completed", "type"));
}
```

Append to `test/MMLib.Alvo.Abstractions.Tests/Management/ManagementExtensionTests.cs`:

```csharp
    [Fact]
    public void A_command_serialises_its_kind_stage_route_and_access()
    {
        var command = Minimal() with
        {
            Id = "confirm_booking",
            Kind = AlvoExtensionKind.Command,
            Stage = AlvoExtensionStage.Http,
            Entities = ["bookings"],
            Route = "/commands/bookings/{id}/confirm",
            HttpMethod = "POST",
            Access = "'staff' in @user.roles",
        };

        var wire = JsonSerializer.SerializeToNode(command, JsonSerializerOptions.Web)!;

        wire["kind"]!.GetValue<string>().ShouldBe("command");
        wire["stage"]!.GetValue<string>().ShouldBe("http");
        wire["route"]!.GetValue<string>().ShouldBe("/commands/bookings/{id}/confirm");
        wire["httpMethod"]!.GetValue<string>().ShouldBe("POST");
        wire["access"]!.GetValue<string>().ShouldBe("'staff' in @user.roles");
    }

    [Fact]
    public void The_appended_members_keep_their_numbers()
    {
        ((int)AlvoExtensionKind.Command).ShouldBe(1);
        ((int)AlvoExtensionStage.Http).ShouldBe(1);
        ((int)AlvoExtensionStage.PreTransaction).ShouldBe(2);
        JsonSerializer.Serialize(AlvoExtensionStage.PreTransaction).ShouldBe("\"preTransaction\"");
    }

    [Fact]
    public void A_verdict_lists_no_commands_unless_given_and_carries_them_when_given()
    {
        var verdict = new ManagementPolicyVerdict(true, null, "true", null, null, [], []);
        var commands = verdict with { Commands = [new ManagementCommandVerdict { Name = "quote_stay", Access = "true", Allowed = true }] };

        verdict.Commands.ShouldBeEmpty();
        JsonSerializer.SerializeToNode(commands, JsonSerializerOptions.Web)!["commands"]![0]!["allowed"]!.GetValue<bool>().ShouldBeTrue();
    }
```

- [ ] **Step 2: Run them to verify they fail**

Run: `dotnet test --project test/MMLib.Alvo.Tests --filter-class '*AlvoFrameworkEventTests'` and `dotnet test --project test/MMLib.Alvo.Abstractions.Tests --filter-class '*ManagementExtensionTests'`
Expected: FAIL to compile.

- [ ] **Step 3: The public vocabulary**

Append to `AlvoExtensionKind`:

```csharp
    /// <summary>A command: host C# a caller invokes by name — over HTTP, or in process — after its access rule admits them.</summary>
    [JsonStringEnumMemberName("command")]
    Command = 1,
```

Append to `AlvoExtensionStage`:

```csharp
    /// <summary>On an HTTP request, outside any transaction: the extension opens its own, if it writes.</summary>
    [JsonStringEnumMemberName("http")]
    Http = 1,

    /// <summary>
    /// Before any transaction is open, in the flow of whatever invoked it: a network call is allowed, and its writes open
    /// their own unit.
    /// </summary>
    [JsonStringEnumMemberName("preTransaction")]
    PreTransaction = 2,
```

Append to `ManagementExtension`, before `Paused`:

```csharp
    /// <summary>
    /// Gets the route pattern a command was mapped at, as the host wrote it — a <c>MapGroup</c> prefix is not included;
    /// null for an extension with no route of its own.
    /// </summary>
    public string? Route { get; init; }

    /// <summary>Gets the HTTP method of <see cref="Route"/>; null when there is no route.</summary>
    public string? HttpMethod { get; init; }

    /// <summary>
    /// Gets a command's access rule as CEL text — a condition over <c>@user</c>, <c>@tenant</c> and, for a command bound to
    /// an entity, the row; null for an extension that has none.
    /// </summary>
    public string? Access { get; init; }
```

`src/MMLib.Alvo.Abstractions/Management/ManagementCommandVerdict.cs`:

```csharp
namespace MMLib.Alvo.Management;

/// <summary>What a command's access rule answers for a simulated caller.</summary>
/// <remarks>Init-only rather than positional, so a later version adds members without breaking a reader.</remarks>
public sealed record ManagementCommandVerdict
{
    /// <summary>Gets the command's name.</summary>
    public required string Name { get; init; }

    /// <summary>Gets the entity the command is bound to, or null for a command bound to none.</summary>
    public string? Entity { get; init; }

    /// <summary>Gets the access rule, as CEL text.</summary>
    public required string Access { get; init; }

    /// <summary>
    /// Gets whether the rule admits the caller: <see langword="true"/> or <see langword="false"/> when it reads only
    /// <c>@user</c> and <c>@tenant</c>; <see langword="null"/> when it also reads the row, which is decided per row — and a
    /// row the caller cannot read answers 404 before the rule runs.
    /// </summary>
    public bool? Allowed { get; init; }
}
```

In `ManagementPolicyVerdict`, after `Extensions`:

```csharp
    /// <summary>
    /// Gets the host's commands that act on this entity or on no entity, and what each one's access rule answers for this
    /// caller — the same compiled rule a command evaluates before its handler. Empty in a host that maps none.
    /// </summary>
    public IReadOnlyList<ManagementCommandVerdict> Commands { get; init; } = [];
```

- [ ] **Step 4: The internal seams and the framework event**

`src/MMLib.Alvo.Abstractions/Events/AlvoFrameworkEvent.cs`:

```csharp
using MMLib.Alvo.Data;

namespace MMLib.Alvo.Events;

/// <summary>
/// The one door through which Alvo mints the events of its reserved <c>alvo</c> namespace — a command's completion
/// (and, later, a schedule's run). A host cannot reach it: the custom-event door refuses the namespace. Its partition key
/// carries the tenant, so one tenant's completions never queue behind another's in an ordered partition (Ruling 49).
/// </summary>
internal static class AlvoFrameworkEvent
{
    private const string PartitionMarker = "framework.event";

    /// <summary>Builds a framework event stamped with <paramref name="context"/>'s identity and effective provenance.</summary>
    /// <param name="type">The event type, in the <c>alvo.</c> namespace.</param>
    /// <param name="subject">What it is about.</param>
    /// <param name="data">The flat payload.</param>
    /// <param name="context">The caller it happened as.</param>
    /// <param name="now">When it happened.</param>
    /// <exception cref="ArgumentException"><paramref name="type"/> is not a well-formed <c>alvo.</c> name, or the payload cannot be written.</exception>
    internal static AlvoEvent Create(
        string type, string subject, IReadOnlyDictionary<string, object?> data, AlvoContext context, DateTimeOffset now)
    {
        EnsureFramework(type);
        ArgumentException.ThrowIfNullOrWhiteSpace(subject);
        ArgumentNullException.ThrowIfNull(data);
        ArgumentNullException.ThrowIfNull(context);
        AlvoCustomEnvelope.EnsureWritable(data);

        var id = AlvoEventId.Create(now);
        var stamp = AlvoEnvelopeStamp.Of(context, id, context.Tenant);

        return stamp.Onto(new AlvoEvent
        {
            Id = id,
            Source = AlvoEvent.DefaultSource,
            Type = type,
            Time = now,
            Subject = subject,
            PartitionKey = $"{PartitionMarker}:{context.Tenant?.ToString() ?? AlvoIdempotency.TenantlessScope}:{subject}",
            AuthType = stamp.AuthType,
            CorrelationId = stamp.CorrelationId,
            Data = new AlvoEventData { Record = new AlvoRecord(data) },
        });
    }

    private static void EnsureFramework(string type)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(type);
        if (!type.StartsWith(AlvoEventName.FrameworkNamespace + AlvoEventName.Separator, StringComparison.Ordinal)
            || !AlvoEventName.IsWellFormed(type))
        {
            throw new ArgumentException(
                $"'{type}' is not a framework event name: it must be a well-formed name in the '{AlvoEventName.FrameworkNamespace}.' namespace.",
                nameof(type));
        }
    }
}
```

`src/MMLib.Alvo.Abstractions/Data/ICommandLedger.cs`:

```csharp
using MMLib.Alvo.Events;

namespace MMLib.Alvo.Data;

/// <summary>
/// Where a command's <c>Idempotency-Key</c> is claimed, committed and answered, and where a command that opened no unit
/// writes its completion. Implemented by the relational drivers; the core reaches it through <c>InternalsVisibleTo</c>.
/// </summary>
/// <remarks>
/// A key moves claimed → committed → answered. A claim is autocommit, leased and <b>fenced</b>: each grant mints a random
/// token, and the commit mark, the answer and a release apply only while the row still carries that token (Ruling 36) —
/// so a run that outlived its lease, whose key another run took over, can neither commit nor free the newer claim. The
/// rendered answer is kept, which <see cref="AlvoIdempotency"/>'s Data API contract never does; it is bound to one
/// credential (Ruling 34) and swept a day after its claim (Ruling 41).
/// </remarks>
internal interface ICommandLedger
{
    /// <summary>Claims <paramref name="key"/> for one run — granting a fencing token — or says why it cannot be run.</summary>
    Task<CommandClaim> ClaimAsync(CommandKey key, DateTimeOffset now, CancellationToken cancellationToken);

    /// <summary>Records the answer of a run whose unit already committed, while the run still holds the claim.</summary>
    Task AnswerAsync(CommandKey key, CommandAnswer answer, CancellationToken cancellationToken);

    /// <summary>Gives up an uncommitted claim the run still holds, so a retry runs the command again.</summary>
    Task ReleaseAsync(CommandKey key, CancellationToken cancellationToken);

    /// <summary>
    /// For a run that opened no unit: appends <paramref name="completion"/> and, with a key, marks it committed and answered —
    /// in one transaction, which is refused (<see cref="CommandClaimLostException"/>) when the run no longer holds the claim.
    /// </summary>
    Task CompleteAsync(AlvoEvent completion, CommandKey? key, CommandAnswer? answer, DateTimeOffset now, CancellationToken cancellationToken);

    /// <summary>Forgets every key claimed before <paramref name="claimedBefore"/>; answers how many.</summary>
    Task<int> SweepAsync(DateTimeOffset claimedBefore, CancellationToken cancellationToken);
}

/// <summary>
/// One command request's key: its scope (command, caller and credential), the caller's key, the request's fingerprint and,
/// once claimed, the fencing token the claim granted.
/// </summary>
internal sealed record CommandKey(string Scope, string Key, string Fingerprint)
{
    /// <summary>Gets the fencing token of the claim this run holds; null until claimed.</summary>
    internal string? Token { get; init; }
}

/// <summary>A run's answer as it is replayed: the status and the JSON body, if any.</summary>
internal sealed record CommandAnswer(int Status, string? Body)
{
    /// <summary>Gets a value indicating whether the answer is a 2xx.</summary>
    internal bool IsSuccess => Status is >= 200 and < 300;
}

/// <summary>What claiming a key found.</summary>
internal enum CommandClaimState
{
    /// <summary>This request may run: <see cref="CommandClaim.Token"/> carries its fencing token.</summary>
    Claimed,

    /// <summary>A run with this key answered; replay its answer.</summary>
    Answered,

    /// <summary>A run with this key is still being processed.</summary>
    InFlight,

    /// <summary>A run with this key committed, and its answer was never recorded.</summary>
    Unanswered,

    /// <summary>The key was spent on a different request.</summary>
    Mismatch,
}

/// <summary>The outcome of a claim.</summary>
internal sealed record CommandClaim(CommandClaimState State, CommandAnswer? Answer = null, string? Token = null)
{
    /// <summary>Gets the claimable state a lookup reports before the store takes the claim.</summary>
    internal static CommandClaim Claimable { get; } = new(CommandClaimState.Claimed);

    internal static CommandClaim InFlight { get; } = new(CommandClaimState.InFlight);

    internal static CommandClaim Unanswered { get; } = new(CommandClaimState.Unanswered);

    internal static CommandClaim Mismatch { get; } = new(CommandClaimState.Mismatch);

    internal static CommandClaim Granted(string token) => new(CommandClaimState.Claimed, Token: token);

    internal static CommandClaim Answered(CommandAnswer answer) => new(CommandClaimState.Answered, answer);
}

/// <summary>A run tried to commit or answer under a claim another run took over after its lease ran out.</summary>
internal sealed class CommandClaimLostException()
    : InvalidOperationException(
        "This command run's claim on its Idempotency-Key was taken over after its 15-minute lease ran out, so it may not commit: its unit is rolled back. A handler must finish within the lease.");

/// <summary>The ledger's fixed bounds.</summary>
internal static class CommandLedgerLimits
{
    /// <summary>How long a claim holds a key before a retry may take it over.</summary>
    internal static readonly TimeSpan Lease = TimeSpan.FromMinutes(15);

    /// <summary>How long a key and its answer are kept after their claim (the IETF draft's suggested expiry).</summary>
    internal static readonly TimeSpan Retention = TimeSpan.FromHours(24);

    /// <summary>The largest answer body recorded; a larger one is not replayable.</summary>
    internal const int MaxAnswerBytes = 65_536;
}
```

`src/MMLib.Alvo.Abstractions/Data/IAlvoCommandUnit.cs`:

```csharp
using MMLib.Alvo.Events;

namespace MMLib.Alvo.Data;

/// <summary>
/// What a command needs from its transaction unit besides the host's own calls: its completion event and its key's commit
/// mark, written in the unit so they commit or roll back with the command's writes. Implemented by the EF driver's
/// <c>IAlvoTransaction</c>; never exposed to the host.
/// </summary>
internal interface IAlvoCommandUnit
{
    /// <summary>Appends a framework event to the outbox in the unit's transaction.</summary>
    Task PublishFrameworkAsync(AlvoEvent @event, CancellationToken cancellationToken);

    /// <summary>
    /// Marks <paramref name="key"/> committed in the unit's transaction, only while the run holds its claim
    /// (<see cref="CommandKey.Token"/>); otherwise throws <see cref="CommandClaimLostException"/> and poisons the unit, so
    /// the run's writes roll back rather than commit beside the run that took the key over.
    /// </summary>
    Task CommitCommandAsync(CommandKey key, DateTimeOffset now, CancellationToken cancellationToken);
}
```

`src/MMLib.Alvo.Abstractions/Data/IInboundWebhookLedger.cs`:

```csharp
namespace MMLib.Alvo.Data;

/// <summary>
/// The per-receiver record of Standard Webhooks ids already handled, so a sender's retry does not run a handler twice.
/// Implemented by the relational drivers; the core reaches it through <c>InternalsVisibleTo</c>. A claim is leased and
/// fenced like a command's (Ruling 36): completing or releasing applies only while the row carries the claim's token.
/// </summary>
internal interface IInboundWebhookLedger
{
    /// <summary>Claims <paramref name="webhookId"/> on <paramref name="receiver"/> for one run.</summary>
    Task<WebhookClaimResult> ClaimAsync(string receiver, string webhookId, DateTimeOffset now, CancellationToken cancellationToken);

    /// <summary>Marks a claimed id handled; answers false when the claim was taken over.</summary>
    Task<bool> CompleteAsync(string receiver, string webhookId, string token, DateTimeOffset now, CancellationToken cancellationToken);

    /// <summary>Gives up a claim the run still holds, so the sender's retry runs it again.</summary>
    Task ReleaseAsync(string receiver, string webhookId, string token, CancellationToken cancellationToken);

    /// <summary>Deletes every id received before <paramref name="receivedBefore"/>; answers how many.</summary>
    Task<int> SweepAsync(DateTimeOffset receivedBefore, CancellationToken cancellationToken);
}

/// <summary>What claiming a webhook id found.</summary>
internal enum WebhookClaim
{
    /// <summary>This delivery may run.</summary>
    Claimed,

    /// <summary>A delivery with this id was handled already.</summary>
    Completed,

    /// <summary>A delivery with this id is still being handled.</summary>
    InFlight,
}

/// <summary>A webhook claim's outcome, and its fencing token when it was granted.</summary>
internal readonly record struct WebhookClaimResult(WebhookClaim State, string? Token = null);

/// <summary>The inbound webhook ledger's fixed bounds.</summary>
internal static class InboundWebhookLimits
{
    /// <summary>How long a claim holds an id before a retry may take it over.</summary>
    internal static readonly TimeSpan Lease = TimeSpan.FromMinutes(15);

    /// <summary>How long a handled id is remembered.</summary>
    internal static readonly TimeSpan Retention = TimeSpan.FromDays(7);

    /// <summary>How often the framework ledgers are swept.</summary>
    internal static readonly TimeSpan SweepInterval = TimeSpan.FromHours(1);

    /// <summary>The longest <c>webhook-id</c> accepted, in UTF-8 bytes.</summary>
    internal const int MaxIdBytes = 255;
}
```

In `AlvoFrameworkTables`, add the two suffixes and list them in `NamesFor` after `OperationsLogSuffix`:

```csharp
    internal const string CommandRequestsSuffix = "_command_requests";

    internal const string InboundWebhooksSuffix = "_inbound_webhooks";
```

```csharp
            schemaPrefix + OperationsLogSuffix,
            schemaPrefix + CommandRequestsSuffix,
            schemaPrefix + InboundWebhooksSuffix,
```

(`DescriptorValidator` reserves every name `NamesFor` lists, so an entity can no longer be called `alvo_command_requests`.)

- [ ] **Step 5: Run the tests to verify they pass, accept the baseline**

Run both suites from Step 2. Expected: PASS. Run `*PublicApiApprovalTests` in `test/MMLib.Alvo.Abstractions.Tests`: the diff adds exactly `AlvoExtensionKind.Command`, `AlvoExtensionStage.Http`, `AlvoExtensionStage.PreTransaction`, `ManagementExtension.Route/HttpMethod/Access`, `ManagementCommandVerdict` and `ManagementPolicyVerdict.Commands`; accept, dispatch `alvo-snapshot-judge`. Then `dotnet test --project test/MMLib.Alvo.Tests --filter-class '*DescriptorValidator*'` — a fact that enumerates reserved table names may need the two new names added to its expected list.

- [ ] **Step 6: Format, ring0, commit**

```bash
dotnet format whitespace MMLib.Alvo.slnx --include src/MMLib.Alvo.Abstractions/Events/AlvoFrameworkEvent.cs src/MMLib.Alvo.Abstractions/Data/ICommandLedger.cs src/MMLib.Alvo.Abstractions/Data/IAlvoCommandUnit.cs src/MMLib.Alvo.Abstractions/Data/IInboundWebhookLedger.cs src/MMLib.Alvo.Abstractions/Management/ManagementCommandVerdict.cs src/MMLib.Alvo.Abstractions/Management/AlvoExtensionKind.cs src/MMLib.Alvo.Abstractions/Management/AlvoExtensionStage.cs src/MMLib.Alvo.Abstractions/Management/ManagementExtension.cs src/MMLib.Alvo.Abstractions/Management/ManagementModels.cs src/MMLib.Alvo.Abstractions/AlvoFrameworkTables.cs test/MMLib.Alvo.Tests/Events/AlvoFrameworkEventTests.cs test/MMLib.Alvo.Abstractions.Tests/Management/ManagementExtensionTests.cs
scripts/test-ring0
git add src/MMLib.Alvo.Abstractions/Events/AlvoFrameworkEvent.cs src/MMLib.Alvo.Abstractions/Data/ICommandLedger.cs src/MMLib.Alvo.Abstractions/Data/IAlvoCommandUnit.cs src/MMLib.Alvo.Abstractions/Data/IInboundWebhookLedger.cs src/MMLib.Alvo.Abstractions/Management/ManagementCommandVerdict.cs src/MMLib.Alvo.Abstractions/Management/AlvoExtensionKind.cs src/MMLib.Alvo.Abstractions/Management/AlvoExtensionStage.cs src/MMLib.Alvo.Abstractions/Management/ManagementExtension.cs src/MMLib.Alvo.Abstractions/Management/ManagementModels.cs src/MMLib.Alvo.Abstractions/AlvoFrameworkTables.cs test/MMLib.Alvo.Tests/Events/AlvoFrameworkEventTests.cs test/MMLib.Alvo.Abstractions.Tests/Management/ManagementExtensionTests.cs test/MMLib.Alvo.Abstractions.Tests/PublicApi.MMLib.Alvo.Abstractions.verified.txt
git commit -m "feat(abstractions): command catalog vocabulary, framework events and ledger seams

Claude-Session: https://claude.ai/code/session_01NJAtafM29iddLwfhb5gnmB"
```

---

## Task 5: The EF driver's ledgers and the command's writes inside a unit, on both engines **[slice end]**

**Files:**
- Create: `src/MMLib.Alvo.Data.EntityFrameworkCore/Internal/CommandLedgerTable.cs`, `src/MMLib.Alvo.Data.EntityFrameworkCore/Internal/InboundWebhookTable.cs`, `src/MMLib.Alvo.Data.EntityFrameworkCore/Internal/OnceDdl.cs`, `src/MMLib.Alvo.Data.EntityFrameworkCore/EfCoreCommandLedger.cs`, `src/MMLib.Alvo.Data.EntityFrameworkCore/EfCoreInboundWebhookLedger.cs`
- Modify: `src/MMLib.Alvo.Data.EntityFrameworkCore/Internal/SystemSchemaInitializer.cs`, `Internal/EfAlvoData.Units.cs` (two `…InUnitAsync` entries), `Internal/EfAlvoTransaction.cs` (implements `IAlvoCommandUnit`), `AlvoEfCoreProvider.cs` (registrations)
- Test: `test/_shared/ef/LedgerFacts.cs`, `test/MMLib.Alvo.Data.Sqlite.Tests/SqliteLedgerTests.cs`, `test/MMLib.Alvo.Data.PostgreSql.Tests.Integration/PostgreSqlLedgerTests.cs`; both csproj files link the facts; `test/MMLib.Alvo.Data.EntityFrameworkCore.Tests/ChangeTrackerReachTests.cs` (allow-list)

**Interfaces:**
- Consumes: Task 4's seams; `IAlvoSqlDialect.DecodeConstraintViolation` (only a unique violation is a lost race, Ruling 49); `WriteUnit.Poison`; `RelationalConnectionFactory` (`Create()`, `WriteGate`), `RelationalSqlBatch.AddParameter`, `StoredInstant.Text`, `OutboxTable.NameFor/InsertAsync`, `EfAlvoData` unit machinery (`WriteUnit.RunAsync`, `ContextFor`, `BeginAsync`, `unit.Connection`).
- Produces: `internal static class CommandLedgerTable` (`NameFor`, `Ddl`, `IndexDdl`, `EnsureAsync`, `FindAsync`, `InsertClaimAsync`, `ReclaimAsync`, `CommitAsync`, `AnswerAsync`, `ReleaseAsync`, `SweepAsync` — every write after the claim conditional on `claim_token`); `internal sealed class OnceDdl`; `internal static class InboundWebhookTable` (`NameFor`, `Ddl`, `IndexDdl`, `EnsureAsync`, `InsertAsync`, `FindAsync`, `ReclaimAsync`, `CompleteAsync`, `ReleaseAsync`, `SweepAsync`); `internal sealed class EfCoreCommandLedger : ICommandLedger`; `internal sealed class EfCoreInboundWebhookLedger : IInboundWebhookLedger`; `EfAlvoData.PublishFrameworkInUnitAsync(WriteUnit, AlvoEvent, CancellationToken)`, `EfAlvoData.CommitCommandInUnitAsync(WriteUnit, CommandKey, DateTimeOffset, CancellationToken)`; DI: `ICommandLedger`, `IInboundWebhookLedger` registered by the provider with `TryAdd`.

- [ ] **Step 1: Write the failing facts**

`test/_shared/ef/LedgerFacts.cs`:

```csharp
using Microsoft.Extensions.DependencyInjection;
using MMLib.Alvo.Data;
using MMLib.Alvo.Descriptor;
using MMLib.Alvo.Events;
using MMLib.Alvo.Schema;
using Shouldly;
using Xunit;
using DescField = MMLib.Alvo.Descriptor.FieldType;
using SchemaField = MMLib.Alvo.Schema.FieldType;

namespace MMLib.Alvo.Tests.Ef;

/// <summary>
/// The command ledger, the inbound webhook ledger and a command's writes inside a unit — the same facts on SQLite and on
/// PostgreSQL (spec 2026-10-14 §6.1: identical SQL, contract facts on both). Every claim is fenced (Ruling 36).
/// </summary>
public abstract class LedgerFacts
{
    private const string Entity = "notes";

    private static readonly DateTimeOffset _now = new(2026, 10, 30, 9, 0, 0, TimeSpan.Zero);

    private static readonly TimeSpan _pastTheLease = CommandLedgerLimits.Lease + TimeSpan.FromSeconds(1);

    private static readonly AlvoContext _caller = new() { User = UserId.New(), Roles = new HashSet<Role> { Role.Authenticated } };

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    protected abstract Task<IServiceProvider> StartAsync(SchemaModel schema, AlvoDescriptor descriptor);

    [Fact]
    public async Task A_fresh_key_is_claimed_with_a_token_and_a_second_claim_finds_it_in_flight()
    {
        var ledger = await LedgerAsync();

        var first = await ledger.ClaimAsync(Key("k1"), _now, Ct);

        first.State.ShouldBe(CommandClaimState.Claimed);
        first.Token.ShouldNotBeNullOrWhiteSpace();
        (await ledger.ClaimAsync(Key("k1"), _now.AddSeconds(1), Ct)).State.ShouldBe(CommandClaimState.InFlight);
    }

    [Fact]
    public async Task A_key_spent_on_another_request_is_a_mismatch()
    {
        var ledger = await LedgerAsync();
        await ledger.ClaimAsync(Key("k2"), _now, Ct);

        (await ledger.ClaimAsync(Key("k2") with { Fingerprint = "other" }, _now, Ct)).State.ShouldBe(CommandClaimState.Mismatch);
    }

    [Fact]
    public async Task An_expired_uncommitted_claim_is_taken_over_with_a_new_token()
    {
        var ledger = await LedgerAsync();
        var first = await ledger.ClaimAsync(Key("k3"), _now, Ct);

        var second = await ledger.ClaimAsync(Key("k3"), _now + _pastTheLease, Ct);

        second.State.ShouldBe(CommandClaimState.Claimed);
        second.Token.ShouldNotBe(first.Token);
    }

    [Fact]
    public async Task A_released_claim_is_claimed_again_at_once()
    {
        var ledger = await LedgerAsync();
        var claim = await ledger.ClaimAsync(Key("k4"), _now, Ct);

        await ledger.ReleaseAsync(Held(Key("k4"), claim), Ct);

        (await ledger.ClaimAsync(Key("k4"), _now, Ct)).State.ShouldBe(CommandClaimState.Claimed);
    }

    [Fact]
    public async Task A_stale_release_does_not_free_a_live_claim()
    {
        var ledger = await LedgerAsync();
        var stale = await ledger.ClaimAsync(Key("k5"), _now, Ct);
        await ledger.ClaimAsync(Key("k5"), _now + _pastTheLease, Ct);

        await ledger.ReleaseAsync(Held(Key("k5"), stale), Ct);

        (await ledger.ClaimAsync(Key("k5"), _now + _pastTheLease, Ct)).State.ShouldBe(CommandClaimState.InFlight, "the newer run still holds it");
    }

    [Fact]
    public async Task A_completion_without_a_unit_commits_the_event_and_the_answer_together()
    {
        var services = await StartAsync(Schema(), Descriptor());
        var ledger = services.GetRequiredService<ICommandLedger>();
        var claim = await ledger.ClaimAsync(Key("k6"), _now, Ct);

        await ledger.CompleteAsync(Completion(), Held(Key("k6"), claim), new CommandAnswer(200, "{\"total\":3}"), _now, Ct);

        var replay = await ledger.ClaimAsync(Key("k6"), _now.AddHours(1), Ct);
        replay.State.ShouldBe(CommandClaimState.Answered);
        replay.Answer.ShouldBe(new CommandAnswer(200, "{\"total\":3}"));
        (await PendingTypesAsync(services)).ShouldContain("alvo.command.quote_stay.completed");
    }

    [Fact]
    public async Task A_completion_by_a_run_that_lost_its_claim_is_refused_with_its_event()
    {
        var services = await StartAsync(Schema(), Descriptor());
        var ledger = services.GetRequiredService<ICommandLedger>();
        var stale = await ledger.ClaimAsync(Key("k7"), _now, Ct);
        await ledger.ClaimAsync(Key("k7"), _now + _pastTheLease, Ct);

        await Should.ThrowAsync<CommandClaimLostException>(() =>
            ledger.CompleteAsync(Completion(), Held(Key("k7"), stale), new CommandAnswer(200, null), _now + _pastTheLease, Ct));

        (await PendingTypesAsync(services)).ShouldNotContain("alvo.command.quote_stay.completed", "the event rolled back with the refused commit");
    }

    [Fact]
    public async Task A_unit_that_commits_carries_the_event_and_the_commit_mark_and_an_answer_follows()
    {
        var services = await StartAsync(Schema(), Descriptor());
        var ledger = services.GetRequiredService<ICommandLedger>();
        var key = Held(Key("k8"), await ledger.ClaimAsync(Key("k8"), _now, Ct));

        await services.GetRequiredService<IAlvoTransactions>().TransactAsync(_caller, async (unit, ct) =>
        {
            await unit.CreateAsync(Entity, new Dictionary<string, object?> { ["text"] = "confirmed" }, ct);
            await ((IAlvoCommandUnit)unit).PublishFrameworkAsync(Completion(), ct);
            await ((IAlvoCommandUnit)unit).CommitCommandAsync(key, _now, ct);
        }, cancellationToken: Ct);

        (await ledger.ClaimAsync(Key("k8"), _now.AddHours(1), Ct)).State.ShouldBe(CommandClaimState.Unanswered);
        await ledger.AnswerAsync(key, new CommandAnswer(201, null), Ct);
        (await ledger.ClaimAsync(Key("k8"), _now.AddHours(1), Ct)).Answer.ShouldBe(new CommandAnswer(201, null));
        (await PendingTypesAsync(services)).ShouldContain("alvo.command.quote_stay.completed");
    }

    [Fact]
    public async Task A_unit_that_rolls_back_leaves_no_event_no_commit_mark_and_no_row()
    {
        var services = await StartAsync(Schema(), Descriptor());
        var ledger = services.GetRequiredService<ICommandLedger>();
        var key = Held(Key("k9"), await ledger.ClaimAsync(Key("k9"), _now, Ct));

        await Should.ThrowAsync<InvalidOperationException>(() => services.GetRequiredService<IAlvoTransactions>().TransactAsync(_caller, async (unit, ct) =>
        {
            await unit.CreateAsync(Entity, new Dictionary<string, object?> { ["text"] = "lost" }, ct);
            await ((IAlvoCommandUnit)unit).PublishFrameworkAsync(Completion(), ct);
            await ((IAlvoCommandUnit)unit).CommitCommandAsync(key, _now, ct);
            throw new InvalidOperationException("the handler failed after its writes");
        }, cancellationToken: Ct));

        (await ledger.ClaimAsync(Key("k9"), _now.AddSeconds(1), Ct)).State.ShouldBe(CommandClaimState.InFlight, "the claim is untouched: only the commit mark rolled back");
        (await PendingTypesAsync(services)).ShouldNotContain("alvo.command.quote_stay.completed");
        (await services.GetRequiredService<IAlvoData>().QueryAsync(new AlvoQuery { Entity = Entity }, _caller, Ct)).Items.ShouldBeEmpty();
    }

    [Fact]
    public async Task A_run_whose_lease_was_taken_over_cannot_commit_and_its_writes_roll_back()
    {
        var services = await StartAsync(Schema(), Descriptor());
        var ledger = services.GetRequiredService<ICommandLedger>();
        var stale = Held(Key("k10"), await ledger.ClaimAsync(Key("k10"), _now, Ct));
        var current = Held(Key("k10"), await ledger.ClaimAsync(Key("k10"), _now + _pastTheLease, Ct));

        await Should.ThrowAsync<CommandClaimLostException>(() => services.GetRequiredService<IAlvoTransactions>().TransactAsync(_caller, async (unit, ct) =>
        {
            await unit.CreateAsync(Entity, new Dictionary<string, object?> { ["text"] = "stale run" }, ct);
            await ((IAlvoCommandUnit)unit).CommitCommandAsync(stale, _now + _pastTheLease, ct);
        }, cancellationToken: Ct));

        (await services.GetRequiredService<IAlvoData>().QueryAsync(new AlvoQuery { Entity = Entity }, _caller, Ct)).Items.ShouldBeEmpty("the stale run's write rolled back");
        await services.GetRequiredService<IAlvoTransactions>().TransactAsync(_caller, (unit, ct) =>
            ((IAlvoCommandUnit)unit).CommitCommandAsync(current, _now + _pastTheLease, ct), cancellationToken: Ct);
        (await ledger.ClaimAsync(Key("k10"), _now.AddHours(1), Ct)).State.ShouldBe(CommandClaimState.Unanswered, "the run holding the claim commits");
    }

    [Fact]
    public async Task Command_keys_claimed_more_than_a_day_ago_are_swept_and_their_key_runs_again()
    {
        var ledger = await LedgerAsync();
        var old = await ledger.ClaimAsync(Key("old"), _now.AddHours(-25), Ct);
        await ledger.CompleteAsync(Completion(), Held(Key("old"), old), new CommandAnswer(200, null), _now.AddHours(-25), Ct);
        var young = await ledger.ClaimAsync(Key("young"), _now.AddHours(-1), Ct);
        await ledger.CompleteAsync(Completion(), Held(Key("young"), young), new CommandAnswer(200, null), _now.AddHours(-1), Ct);

        (await ledger.SweepAsync(_now - CommandLedgerLimits.Retention, Ct)).ShouldBe(1);

        (await ledger.ClaimAsync(Key("old"), _now, Ct)).State.ShouldBe(CommandClaimState.Claimed, "an expired key is a new request");
        (await ledger.ClaimAsync(Key("young"), _now, Ct)).State.ShouldBe(CommandClaimState.Answered);
    }

    [Fact]
    public async Task A_webhook_id_is_claimed_once_completed_and_then_found_completed()
    {
        var ledger = (await StartAsync(Schema(), Descriptor())).GetRequiredService<IInboundWebhookLedger>();

        var claim = await ledger.ClaimAsync("/hooks/pay", "msg_1", _now, Ct);
        claim.State.ShouldBe(WebhookClaim.Claimed);
        (await ledger.ClaimAsync("/hooks/pay", "msg_1", _now, Ct)).State.ShouldBe(WebhookClaim.InFlight);
        (await ledger.CompleteAsync("/hooks/pay", "msg_1", claim.Token!, _now, Ct)).ShouldBeTrue();
        (await ledger.ClaimAsync("/hooks/pay", "msg_1", _now.AddDays(1), Ct)).State.ShouldBe(WebhookClaim.Completed);
        (await ledger.ClaimAsync("/hooks/other", "msg_1", _now, Ct)).State.ShouldBe(WebhookClaim.Claimed, "dedup is per receiver");
    }

    [Fact]
    public async Task A_stale_webhook_run_can_neither_complete_nor_release_a_live_claim()
    {
        var ledger = (await StartAsync(Schema(), Descriptor())).GetRequiredService<IInboundWebhookLedger>();
        var stale = await ledger.ClaimAsync("/hooks/pay", "msg_2", _now, Ct);
        var live = await ledger.ClaimAsync("/hooks/pay", "msg_2", _now + InboundWebhookLimits.Lease + TimeSpan.FromSeconds(1), Ct);

        (await ledger.CompleteAsync("/hooks/pay", "msg_2", stale.Token!, _now, Ct)).ShouldBeFalse();
        await ledger.ReleaseAsync("/hooks/pay", "msg_2", stale.Token!, Ct);

        live.State.ShouldBe(WebhookClaim.Claimed);
        (await ledger.ClaimAsync("/hooks/pay", "msg_2", _now + InboundWebhookLimits.Lease + TimeSpan.FromSeconds(2), Ct)).State.ShouldBe(WebhookClaim.InFlight);
    }

    [Fact]
    public async Task The_sweep_removes_ids_received_before_its_cutoff_and_keeps_later_ones()
    {
        var ledger = (await StartAsync(Schema(), Descriptor())).GetRequiredService<IInboundWebhookLedger>();
        var old = await ledger.ClaimAsync("/hooks/pay", "old", _now.AddDays(-8), Ct);
        await ledger.CompleteAsync("/hooks/pay", "old", old.Token!, _now.AddDays(-8), Ct);
        var young = await ledger.ClaimAsync("/hooks/pay", "young", _now.AddDays(-6), Ct);
        await ledger.CompleteAsync("/hooks/pay", "young", young.Token!, _now.AddDays(-6), Ct);

        (await ledger.SweepAsync(_now - InboundWebhookLimits.Retention, Ct)).ShouldBe(1);

        (await ledger.ClaimAsync("/hooks/pay", "old", _now, Ct)).State.ShouldBe(WebhookClaim.Claimed, "a swept id is forgotten");
        (await ledger.ClaimAsync("/hooks/pay", "young", _now, Ct)).State.ShouldBe(WebhookClaim.Completed);
    }

    private async Task<ICommandLedger> LedgerAsync() =>
        (await StartAsync(Schema(), Descriptor())).GetRequiredService<ICommandLedger>();

    private static CommandKey Key(string key) => new("command:quote_stay/global/" + _caller.User.Value + "/-", key, "fp");

    private static CommandKey Held(CommandKey key, CommandClaim claim) => key with { Token = claim.Token };

    private static AlvoEvent Completion() => AlvoFrameworkEvent.Create(
        "alvo.command.quote_stay.completed", "commands/quote_stay", new Dictionary<string, object?> { ["command"] = "quote_stay" }, _caller, _now);

    private static async Task<IReadOnlyList<string>> PendingTypesAsync(IServiceProvider services)
    {
        var entries = await services.GetRequiredService<IOutboxStore>().ClaimAsync("ledger-facts", 100, 10, TimeSpan.FromMinutes(1), Ct);
        return [.. entries.Select(entry => entry.Type)];
    }

    private static SchemaModel Schema() => new([
        new EntitySchema
        {
            Name = Entity,
            Tenancy = TenancyMode.Global,
            Fields =
            [
                new FieldSchema { Name = "id", Type = SchemaField.Uuid, Required = true },
                new FieldSchema { Name = "text", Type = SchemaField.String, Required = true, MaxLength = 80 },
            ],
        },
    ]);

    private static AlvoDescriptor Descriptor() => new()
    {
        ApiVersion = "alvo.dev/v1",
        Name = "ledger-fixture",
        Entities = new Dictionary<string, EntityDescriptor>(StringComparer.Ordinal)
        {
            [Entity] = new EntityDescriptor
            {
                Tenancy = EntityTenancy.Global,
                Fields = new Dictionary<string, FieldDescriptor>(StringComparer.Ordinal)
                {
                    ["text"] = new() { Type = DescField.String, Required = true, MaxLength = 80 },
                },
                Rules = new AccessRules { List = "true", Get = "true", Create = "true" },
            },
        },
    };
}
```


(`PendingTypesAsync` reads the outbox through `IOutboxStore.ClaimAsync`; nothing dispatches in these fixtures, so claiming to read is harmless, and each fact claims once.)

`test/MMLib.Alvo.Data.Sqlite.Tests/SqliteLedgerTests.cs`:

```csharp
using MMLib.Alvo.Descriptor;
using MMLib.Alvo.Schema;
using MMLib.Alvo.Tests.Ef;

namespace MMLib.Alvo.Data.Sqlite.Tests;

/// <summary>SQLite's leg of <see cref="LedgerFacts"/>.</summary>
public sealed class SqliteLedgerTests : LedgerFacts, IAsyncDisposable
{
    private readonly SqliteAlvoDataFixture _fixture = new();

    /// <inheritdoc/>
    protected override async Task<IServiceProvider> StartAsync(SchemaModel schema, AlvoDescriptor descriptor) =>
        (await _fixture.StartAsync(schema, descriptor)).Services;

    /// <inheritdoc/>
    public ValueTask DisposeAsync() => _fixture.DisposeAsync();
}
```

`test/MMLib.Alvo.Data.PostgreSql.Tests.Integration/PostgreSqlLedgerTests.cs`:

```csharp
using MMLib.Alvo.Descriptor;
using MMLib.Alvo.Schema;
using MMLib.Alvo.Tests.Ef;
using Xunit;

namespace MMLib.Alvo.Data.PostgreSql.Tests.Integration;

/// <summary>PostgreSQL's leg of <see cref="LedgerFacts"/>.</summary>
public sealed class PostgreSqlLedgerTests : LedgerFacts, IAsyncLifetime
{
    private readonly PostgreSqlAlvoDataFixture _fixture = new();

    /// <inheritdoc/>
    public ValueTask InitializeAsync() => _fixture.InitializeAsync();

    /// <inheritdoc/>
    protected override async Task<IServiceProvider> StartAsync(SchemaModel schema, AlvoDescriptor descriptor) =>
        (await _fixture.StartAsync(schema, descriptor)).Services;

    /// <inheritdoc/>
    public ValueTask DisposeAsync() => _fixture.DisposeAsync();
}
```

Link the facts in both csproj files beside `UnitSavepointFacts.cs`:

```xml
    <Compile Include="$(MSBuildThisFileDirectory)../_shared/ef/LedgerFacts.cs" Link="_shared/LedgerFacts.cs" />
```

In `ChangeTrackerReachTests._sqlComposingFiles`, add `"CommandLedgerTable.cs"` and `"InboundWebhookTable.cs"` in alphabetical position.

- [ ] **Step 2: Run them to verify they fail**

Run: `dotnet test --project test/MMLib.Alvo.Data.Sqlite.Tests --filter-class '*SqliteLedgerTests'`
Expected: FAIL — `ICommandLedger` is not registered (`No service for type`), and `EfAlvoTransaction` is not an `IAlvoCommandUnit` (`InvalidCastException`).

- [ ] **Step 3: The command ledger's table**

`src/MMLib.Alvo.Data.EntityFrameworkCore/Internal/CommandLedgerTable.cs`:

```csharp
using System.Data.Common;
using System.Globalization;

namespace MMLib.Alvo.Data.EntityFrameworkCore.Internal;

/// <summary>
/// <c>&lt;prefix&gt;_command_requests</c>: one row per command <c>Idempotency-Key</c> — claimed (with a fencing token),
/// committed, answered. ANSI SQL only, identical on every engine; instants are <see cref="StoredInstant"/> text, which
/// orders lexically. Every write after the claim names the token (Ruling 36).
/// </summary>
internal static class CommandLedgerTable
{
    internal static string NameFor(string schemaPrefix) => schemaPrefix + AlvoFrameworkTables.CommandRequestsSuffix;

    internal static string Ddl(string table) =>
        $"""
        CREATE TABLE IF NOT EXISTS {table} (
            scope TEXT NOT NULL,
            idempotency_key TEXT NOT NULL,
            fingerprint TEXT NOT NULL,
            claim_token TEXT NOT NULL,
            claimed_at TEXT NOT NULL,
            lease_until TEXT NOT NULL,
            committed_at TEXT NULL,
            answer_status INTEGER NULL,
            answer_body TEXT NULL,
            PRIMARY KEY (scope, idempotency_key)
        )
        """;

    internal static string IndexDdl(string table) =>
        $"CREATE INDEX IF NOT EXISTS {table}_claimed ON {table} (claimed_at)";

    internal static async Task EnsureAsync(DbConnection connection, string table, CancellationToken ct)
    {
        await AffectedAsync(connection, null, Ddl(table), ct, null).ConfigureAwait(false);
        await AffectedAsync(connection, null, IndexDdl(table), ct, null).ConfigureAwait(false);
    }

    internal static async Task<CommandClaim?> FindAsync(
        DbConnection connection, DbTransaction? transaction, string table, CommandKey key, DateTimeOffset now, CancellationToken ct)
    {
        var command = Command(connection, transaction,
            $"SELECT fingerprint, lease_until, committed_at, answer_status, answer_body FROM {table} WHERE scope = @scope AND idempotency_key = @key", key);
        await using (command.ConfigureAwait(false))
        {
            var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
            await using (reader.ConfigureAwait(false))
            {
                return await reader.ReadAsync(ct).ConfigureAwait(false) ? Found(reader, key, now) : null;
            }
        }
    }

    internal static Task<int> InsertClaimAsync(DbConnection connection, DbTransaction transaction, string table, CommandKey key, string token, DateTimeOffset now, CancellationToken ct) =>
        AffectedAsync(connection, transaction,
            $"INSERT INTO {table} (scope, idempotency_key, fingerprint, claim_token, claimed_at, lease_until) VALUES (@scope, @key, @fingerprint, @token, @claimed_at, @lease_until)",
            ct, key, ("@fingerprint", key.Fingerprint), ("@token", token), ("@claimed_at", StoredInstant.Text(now)), ("@lease_until", StoredInstant.Text(now + CommandLedgerLimits.Lease)));

    internal static Task<int> ReclaimAsync(DbConnection connection, DbTransaction transaction, string table, CommandKey key, string token, DateTimeOffset now, CancellationToken ct) =>
        AffectedAsync(connection, transaction,
            $"UPDATE {table} SET claim_token = @token, claimed_at = @claimed_at, lease_until = @lease_until WHERE scope = @scope AND idempotency_key = @key AND committed_at IS NULL AND lease_until <= @claimed_at",
            ct, key, ("@token", token), ("@claimed_at", StoredInstant.Text(now)), ("@lease_until", StoredInstant.Text(now + CommandLedgerLimits.Lease)));

    internal static Task<int> CommitAsync(DbConnection connection, DbTransaction transaction, string table, CommandKey key, DateTimeOffset now, CancellationToken ct) =>
        AffectedAsync(connection, transaction,
            $"UPDATE {table} SET committed_at = @committed_at WHERE scope = @scope AND idempotency_key = @key AND claim_token = @token AND committed_at IS NULL",
            ct, key, ("@token", key.Token), ("@committed_at", StoredInstant.Text(now)));

    internal static Task<int> AnswerAsync(DbConnection connection, DbTransaction? transaction, string table, CommandKey key, CommandAnswer answer, CancellationToken ct) =>
        AffectedAsync(connection, transaction,
            $"UPDATE {table} SET answer_status = @status, answer_body = @body WHERE scope = @scope AND idempotency_key = @key AND claim_token = @token",
            ct, key, ("@token", key.Token), ("@status", answer.Status), ("@body", answer.Body));

    internal static Task<int> ReleaseAsync(DbConnection connection, string table, CommandKey key, CancellationToken ct) =>
        AffectedAsync(connection, null,
            $"DELETE FROM {table} WHERE scope = @scope AND idempotency_key = @key AND claim_token = @token AND committed_at IS NULL",
            ct, key, ("@token", key.Token));

    internal static Task<int> SweepAsync(DbConnection connection, string table, DateTimeOffset claimedBefore, CancellationToken ct) =>
        AffectedAsync(connection, null, $"DELETE FROM {table} WHERE claimed_at < @before", ct, null, ("@before", StoredInstant.Text(claimedBefore)));

    private static CommandClaim Found(DbDataReader reader, CommandKey key, DateTimeOffset now)
    {
        if (!string.Equals(reader.GetString(0), key.Fingerprint, StringComparison.Ordinal))
        {
            return CommandClaim.Mismatch;
        }

        if (!reader.IsDBNull(3))
        {
            return CommandClaim.Answered(new CommandAnswer(Convert.ToInt32(reader.GetValue(3), CultureInfo.InvariantCulture), reader.IsDBNull(4) ? null : reader.GetString(4)));
        }

        return !reader.IsDBNull(2) ? CommandClaim.Unanswered
            : string.CompareOrdinal(reader.GetString(1), StoredInstant.Text(now)) > 0 ? CommandClaim.InFlight
            : CommandClaim.Claimable;
    }

    private static DbCommand Command(DbConnection connection, DbTransaction? transaction, string sql, CommandKey? key, params (string Name, object? Value)[] values)
    {
        var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        if (key is not null)
        {
            RelationalSqlBatch.AddParameter(command, "@scope", key.Scope);
            RelationalSqlBatch.AddParameter(command, "@key", key.Key);
        }

        foreach (var (name, value) in values)
        {
            RelationalSqlBatch.AddParameter(command, name, value);
        }

        return command;
    }

    private static async Task<int> AffectedAsync(DbConnection connection, DbTransaction? transaction, string sql, CancellationToken ct, CommandKey? key, params (string Name, object? Value)[] values)
    {
        var command = Command(connection, transaction, sql, key, values);
        await using (command.ConfigureAwait(false))
        {
            return await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }
    }
}
```

(`Found` answers `Claimable` for an expired, uncommitted claim; the store then has to *take* it with `ReclaimAsync`, whose `WHERE … lease_until <= @claimed_at` is the race guard and whose new token fences the run it replaced. `RelationalSqlBatch.AddParameter` binds `null` as `DBNull`; if its signature takes a non-nullable object, pass `(object?)answer.Body ?? DBNull.Value`.)

- [ ] **Step 4: The inbound webhook table**

`src/MMLib.Alvo.Data.EntityFrameworkCore/Internal/InboundWebhookTable.cs`:

```csharp
using System.Data.Common;

namespace MMLib.Alvo.Data.EntityFrameworkCore.Internal;

/// <summary>
/// <c>&lt;prefix&gt;_inbound_webhooks</c>: one row per Standard Webhooks id a receiver has seen, claimed with a fencing token,
/// swept after seven days.
/// </summary>
internal static class InboundWebhookTable
{
    internal static string NameFor(string schemaPrefix) => schemaPrefix + AlvoFrameworkTables.InboundWebhooksSuffix;

    internal static string Ddl(string table) =>
        $"""
        CREATE TABLE IF NOT EXISTS {table} (
            receiver TEXT NOT NULL,
            webhook_id TEXT NOT NULL,
            claim_token TEXT NOT NULL,
            received_at TEXT NOT NULL,
            lease_until TEXT NOT NULL,
            completed_at TEXT NULL,
            PRIMARY KEY (receiver, webhook_id)
        )
        """;

    internal static string IndexDdl(string table) =>
        $"CREATE INDEX IF NOT EXISTS {table}_received ON {table} (received_at)";

    internal static async Task EnsureAsync(DbConnection connection, string table, CancellationToken ct)
    {
        await AffectedAsync(connection, null, Ddl(table), ct, null, null).ConfigureAwait(false);
        await AffectedAsync(connection, null, IndexDdl(table), ct, null, null).ConfigureAwait(false);
    }

    internal static async Task<WebhookClaim?> FindAsync(DbConnection connection, DbTransaction transaction, string table, string receiver, string id, DateTimeOffset now, CancellationToken ct)
    {
        var command = Command(connection, transaction, $"SELECT lease_until, completed_at FROM {table} WHERE receiver = @receiver AND webhook_id = @id", receiver, id);
        await using (command.ConfigureAwait(false))
        {
            var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
            await using (reader.ConfigureAwait(false))
            {
                if (!await reader.ReadAsync(ct).ConfigureAwait(false))
                {
                    return null;
                }

                return !reader.IsDBNull(1) ? WebhookClaim.Completed
                    : string.CompareOrdinal(reader.GetString(0), StoredInstant.Text(now)) > 0 ? WebhookClaim.InFlight
                    : WebhookClaim.Claimed;
            }
        }
    }

    internal static Task<int> InsertAsync(DbConnection connection, DbTransaction transaction, string table, string receiver, string id, string token, DateTimeOffset now, CancellationToken ct) =>
        AffectedAsync(connection, transaction, $"INSERT INTO {table} (receiver, webhook_id, claim_token, received_at, lease_until) VALUES (@receiver, @id, @token, @received_at, @lease_until)", ct, receiver, id, ("@token", token), ("@received_at", StoredInstant.Text(now)), ("@lease_until", StoredInstant.Text(now + InboundWebhookLimits.Lease)));

    internal static Task<int> ReclaimAsync(DbConnection connection, DbTransaction transaction, string table, string receiver, string id, string token, DateTimeOffset now, CancellationToken ct) =>
        AffectedAsync(connection, transaction, $"UPDATE {table} SET claim_token = @token, received_at = @received_at, lease_until = @lease_until WHERE receiver = @receiver AND webhook_id = @id AND completed_at IS NULL AND lease_until <= @received_at", ct, receiver, id, ("@token", token), ("@received_at", StoredInstant.Text(now)), ("@lease_until", StoredInstant.Text(now + InboundWebhookLimits.Lease)));

    internal static Task<int> CompleteAsync(DbConnection connection, string table, string receiver, string id, string token, DateTimeOffset now, CancellationToken ct) =>
        AffectedAsync(connection, null, $"UPDATE {table} SET completed_at = @completed_at WHERE receiver = @receiver AND webhook_id = @id AND claim_token = @token AND completed_at IS NULL", ct, receiver, id, ("@token", token), ("@completed_at", StoredInstant.Text(now)));

    internal static Task<int> ReleaseAsync(DbConnection connection, string table, string receiver, string id, string token, CancellationToken ct) =>
        AffectedAsync(connection, null, $"DELETE FROM {table} WHERE receiver = @receiver AND webhook_id = @id AND claim_token = @token AND completed_at IS NULL", ct, receiver, id, ("@token", token));

    internal static Task<int> SweepAsync(DbConnection connection, string table, DateTimeOffset before, CancellationToken ct) =>
        AffectedAsync(connection, null, $"DELETE FROM {table} WHERE received_at < @before", ct, null, null, ("@before", StoredInstant.Text(before)));

    private static DbCommand Command(DbConnection connection, DbTransaction? transaction, string sql, string? receiver, string? id, params (string Name, object? Value)[] values)
    {
        var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        if (receiver is not null)
        {
            RelationalSqlBatch.AddParameter(command, "@receiver", receiver);
            RelationalSqlBatch.AddParameter(command, "@id", id);
        }

        foreach (var (name, value) in values)
        {
            RelationalSqlBatch.AddParameter(command, name, value);
        }

        return command;
    }

    private static async Task<int> AffectedAsync(DbConnection connection, DbTransaction? transaction, string sql, CancellationToken ct, string? receiver, string? id, params (string Name, object? Value)[] values)
    {
        var command = Command(connection, transaction, sql, receiver, id, values);
        await using (command.ConfigureAwait(false))
        {
            return await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }
    }
}
```


- [ ] **Step 5: The two stores**

`src/MMLib.Alvo.Data.EntityFrameworkCore/Internal/OnceDdl.cs`:

```csharp
namespace MMLib.Alvo.Data.EntityFrameworkCore.Internal;

/// <summary>
/// Runs a store's <c>CREATE … IF NOT EXISTS</c> once per process, serialising concurrent first callers (Ruling 49): on
/// PostgreSQL two concurrent <c>IF NOT EXISTS</c> creates of one cold table can still collide in <c>pg_type</c>. A failed
/// attempt is forgotten, so the next call tries again. The boot's <c>SystemSchemaInitializer</c> normally created the
/// tables already; this covers a composition that never booted.
/// </summary>
/// <param name="ensure">The store's DDL, run on a connection of its own.</param>
internal sealed class OnceDdl(Func<CancellationToken, Task> ensure)
{
    private TaskCompletionSource? _done;

    internal async Task EnsureAsync(CancellationToken cancellationToken)
    {
        var mine = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        if (Interlocked.CompareExchange(ref _done, mine, null) is { } winner)
        {
            await winner.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
            return;
        }

        try
        {
            await ensure(CancellationToken.None).ConfigureAwait(false);
            mine.SetResult();
        }
        catch (Exception failure)
        {
            Interlocked.Exchange(ref _done, null);
            mine.SetException(failure);
            throw;
        }
    }
}
```

`src/MMLib.Alvo.Data.EntityFrameworkCore/EfCoreCommandLedger.cs`:

```csharp
using MMLib.Alvo.Data.EntityFrameworkCore.Internal;
using MMLib.Alvo.Events;
using System.Data.Common;

namespace MMLib.Alvo.Data.EntityFrameworkCore;

/// <summary>
/// <see cref="ICommandLedger"/> over the relational drivers: every operation is autocommit on its own connection, behind
/// the database's write gate (which refuses inside an open unit, HX-07 Ruling 33).
/// </summary>
/// <remarks>
/// A claim is a read, then an insert or a guarded update, in one transaction that commits only when it claimed; each grant
/// mints a fresh fencing token (Ruling 36). Two concurrent first claims both read nothing; the primary key lets one insert
/// and fails the other with a unique violation — and only that violation, decoded by the dialect, is read as
/// <see cref="CommandClaimState.InFlight"/> after rolling its transaction back (any other failure surfaces).
/// </remarks>
internal sealed class EfCoreCommandLedger : ICommandLedger
{
    private readonly RelationalConnectionFactory _connections;
    private readonly IAlvoSqlDialect _dialect;
    private readonly string _table;
    private readonly string _outbox;
    private readonly OnceDdl _ddl;

    public EfCoreCommandLedger(RelationalConnectionFactory connections, IAlvoSqlDialect dialect, AlvoOptions options)
    {
        ArgumentNullException.ThrowIfNull(connections);
        ArgumentNullException.ThrowIfNull(dialect);
        ArgumentNullException.ThrowIfNull(options);
        _connections = connections;
        _dialect = dialect;
        _table = CommandLedgerTable.NameFor(options.SchemaPrefix);
        _outbox = OutboxTable.NameFor(options.SchemaPrefix);
        _ddl = new OnceDdl(EnsureTableAsync);
    }

    /// <inheritdoc/>
    public Task<CommandClaim> ClaimAsync(CommandKey key, DateTimeOffset now, CancellationToken cancellationToken) =>
        InTransactionAsync(
            (connection, transaction, ct) => ClaimedAsync(connection, transaction, key, now, ct),
            claim => claim.Token is not null,
            cancellationToken);

    /// <inheritdoc/>
    public Task AnswerAsync(CommandKey key, CommandAnswer answer, CancellationToken cancellationToken) =>
        InTransactionAsync(
            (connection, transaction, ct) => CommandLedgerTable.AnswerAsync(connection, transaction, _table, key, answer, ct),
            _ => true,
            cancellationToken);

    /// <inheritdoc/>
    public async Task ReleaseAsync(CommandKey key, CancellationToken cancellationToken)
    {
        var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using (connection.ConfigureAwait(false))
        using (await _connections.WriteGate.EnterAsync(cancellationToken).ConfigureAwait(false))
        {
            await CommandLedgerTable.ReleaseAsync(connection, _table, key, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <inheritdoc/>
    public Task CompleteAsync(AlvoEvent completion, CommandKey? key, CommandAnswer? answer, DateTimeOffset now, CancellationToken cancellationToken) =>
        InTransactionAsync(
            async (connection, transaction, ct) =>
            {
                await OutboxTable.InsertAsync(connection, transaction, _outbox, completion, ct).ConfigureAwait(false);
                await CommittedAsync(connection, transaction, key, answer, now, ct).ConfigureAwait(false);
                return true;
            },
            _ => true,
            cancellationToken);

    /// <inheritdoc/>
    public async Task<int> SweepAsync(DateTimeOffset claimedBefore, CancellationToken cancellationToken)
    {
        var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using (connection.ConfigureAwait(false))
        using (await _connections.WriteGate.EnterAsync(cancellationToken).ConfigureAwait(false))
        {
            return await CommandLedgerTable.SweepAsync(connection, _table, claimedBefore, cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task CommittedAsync(DbConnection connection, DbTransaction transaction, CommandKey? key, CommandAnswer? answer, DateTimeOffset now, CancellationToken ct)
    {
        if (key is null)
        {
            return;
        }

        if (await CommandLedgerTable.CommitAsync(connection, transaction, _table, key, now, ct).ConfigureAwait(false) != 1)
        {
            throw new CommandClaimLostException();
        }

        if (answer is not null)
        {
            await CommandLedgerTable.AnswerAsync(connection, transaction, _table, key, answer, ct).ConfigureAwait(false);
        }
    }

    private async Task<CommandClaim> ClaimedAsync(DbConnection connection, DbTransaction transaction, CommandKey key, DateTimeOffset now, CancellationToken ct)
    {
        var found = await CommandLedgerTable.FindAsync(connection, transaction, _table, key, now, ct).ConfigureAwait(false);
        var token = Guid.NewGuid().ToString("N");

        return found is null ? await InsertedAsync(connection, transaction, key, token, now, ct).ConfigureAwait(false)
            : found.State == CommandClaimState.Claimed ? await ReclaimedAsync(connection, transaction, key, token, now, ct).ConfigureAwait(false)
            : found;
    }

    private async Task<CommandClaim> InsertedAsync(DbConnection connection, DbTransaction transaction, CommandKey key, string token, DateTimeOffset now, CancellationToken ct)
    {
        try
        {
            await CommandLedgerTable.InsertClaimAsync(connection, transaction, _table, key, token, now, ct).ConfigureAwait(false);
            return CommandClaim.Granted(token);
        }
        catch (DbException failure) when (_dialect.DecodeConstraintViolation(failure) is { Kind: AlvoConstraintKind.Unique })
        {
            await transaction.RollbackAsync(ct).ConfigureAwait(false);
            return CommandClaim.InFlight;
        }
    }

    private async Task<CommandClaim> ReclaimedAsync(DbConnection connection, DbTransaction transaction, CommandKey key, string token, DateTimeOffset now, CancellationToken ct) =>
        await CommandLedgerTable.ReclaimAsync(connection, transaction, _table, key, token, now, ct).ConfigureAwait(false) == 1
            ? CommandClaim.Granted(token)
            : CommandClaim.InFlight;

    private async Task<T> InTransactionAsync<T>(
        Func<DbConnection, DbTransaction, CancellationToken, Task<T>> work, Func<T, bool> commits, CancellationToken ct)
    {
        var connection = await OpenAsync(ct).ConfigureAwait(false);
        await using (connection.ConfigureAwait(false))
        using (await _connections.WriteGate.EnterAsync(ct).ConfigureAwait(false))
        {
            var transaction = await connection.BeginTransactionAsync(ct).ConfigureAwait(false);
            await using (transaction.ConfigureAwait(false))
            {
                var result = await work(connection, transaction, ct).ConfigureAwait(false);
                if (commits(result))
                {
                    await transaction.CommitAsync(ct).ConfigureAwait(false);
                }

                return result;
            }
        }
    }

    private async Task<DbConnection> OpenAsync(CancellationToken ct)
    {
        await _ddl.EnsureAsync(ct).ConfigureAwait(false);
        var connection = _connections.Create();
        await connection.OpenAsync(ct).ConfigureAwait(false);
        return connection;
    }

    private async Task EnsureTableAsync(CancellationToken ct)
    {
        var connection = _connections.Create();
        await using (connection.ConfigureAwait(false))
        {
            await connection.OpenAsync(ct).ConfigureAwait(false);
            await CommandLedgerTable.EnsureAsync(connection, _table, ct).ConfigureAwait(false);
        }
    }
}
```

A completion with an answer that is `null` (a committed run whose result cannot be replayed) leaves `answer_status` null, so a retry reads `Unanswered` (Ruling 9). A completion by a run that lost its claim throws `CommandClaimLostException` and its transaction — the outbox insert included — rolls back. The read-only claim outcomes (`Answered`, `Unanswered`, `InFlight`, `Mismatch`) never commit; disposing the transaction rolls back a read. An answer for a swept row affects nothing, silently.

`src/MMLib.Alvo.Data.EntityFrameworkCore/EfCoreInboundWebhookLedger.cs`:

```csharp
using MMLib.Alvo.Data.EntityFrameworkCore.Internal;
using System.Data.Common;

namespace MMLib.Alvo.Data.EntityFrameworkCore;

/// <summary><see cref="IInboundWebhookLedger"/> over the relational drivers, autocommit behind the write gate, fenced per claim.</summary>
internal sealed class EfCoreInboundWebhookLedger : IInboundWebhookLedger
{
    private readonly RelationalConnectionFactory _connections;
    private readonly IAlvoSqlDialect _dialect;
    private readonly string _table;
    private readonly OnceDdl _ddl;

    public EfCoreInboundWebhookLedger(RelationalConnectionFactory connections, IAlvoSqlDialect dialect, AlvoOptions options)
    {
        ArgumentNullException.ThrowIfNull(connections);
        ArgumentNullException.ThrowIfNull(dialect);
        ArgumentNullException.ThrowIfNull(options);
        _connections = connections;
        _dialect = dialect;
        _table = InboundWebhookTable.NameFor(options.SchemaPrefix);
        _ddl = new OnceDdl(EnsureTableAsync);
    }

    /// <inheritdoc/>
    public async Task<WebhookClaimResult> ClaimAsync(string receiver, string webhookId, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using (connection.ConfigureAwait(false))
        using (await _connections.WriteGate.EnterAsync(cancellationToken).ConfigureAwait(false))
        {
            var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
            await using (transaction.ConfigureAwait(false))
            {
                var claim = await ClaimedAsync(connection, transaction, receiver, webhookId, now, cancellationToken).ConfigureAwait(false);
                if (claim.Token is not null)
                {
                    await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
                }

                return claim;
            }
        }
    }

    /// <inheritdoc/>
    public async Task<bool> CompleteAsync(string receiver, string webhookId, string token, DateTimeOffset now, CancellationToken cancellationToken) =>
        await AffectedAsync(connection => InboundWebhookTable.CompleteAsync(connection, _table, receiver, webhookId, token, now, cancellationToken), cancellationToken).ConfigureAwait(false) == 1;

    /// <inheritdoc/>
    public Task ReleaseAsync(string receiver, string webhookId, string token, CancellationToken cancellationToken) =>
        AffectedAsync(connection => InboundWebhookTable.ReleaseAsync(connection, _table, receiver, webhookId, token, cancellationToken), cancellationToken);

    /// <inheritdoc/>
    public Task<int> SweepAsync(DateTimeOffset receivedBefore, CancellationToken cancellationToken) =>
        AffectedAsync(connection => InboundWebhookTable.SweepAsync(connection, _table, receivedBefore, cancellationToken), cancellationToken);

    private async Task<WebhookClaimResult> ClaimedAsync(DbConnection connection, DbTransaction transaction, string receiver, string id, DateTimeOffset now, CancellationToken ct)
    {
        var token = Guid.NewGuid().ToString("N");
        switch (await InboundWebhookTable.FindAsync(connection, transaction, _table, receiver, id, now, ct).ConfigureAwait(false))
        {
            case null:
                return await InsertedAsync(connection, transaction, receiver, id, token, now, ct).ConfigureAwait(false);
            case WebhookClaim.Claimed:
                return await InboundWebhookTable.ReclaimAsync(connection, transaction, _table, receiver, id, token, now, ct).ConfigureAwait(false) == 1
                    ? new WebhookClaimResult(WebhookClaim.Claimed, token)
                    : new WebhookClaimResult(WebhookClaim.InFlight);
            case var found:
                return new WebhookClaimResult(found.Value);
        }
    }

    private async Task<WebhookClaimResult> InsertedAsync(DbConnection connection, DbTransaction transaction, string receiver, string id, string token, DateTimeOffset now, CancellationToken ct)
    {
        try
        {
            await InboundWebhookTable.InsertAsync(connection, transaction, _table, receiver, id, token, now, ct).ConfigureAwait(false);
            return new WebhookClaimResult(WebhookClaim.Claimed, token);
        }
        catch (DbException failure) when (_dialect.DecodeConstraintViolation(failure) is { Kind: AlvoConstraintKind.Unique })
        {
            await transaction.RollbackAsync(ct).ConfigureAwait(false);
            return new WebhookClaimResult(WebhookClaim.InFlight);
        }
    }

    private async Task<int> AffectedAsync(Func<DbConnection, Task<int>> work, CancellationToken ct)
    {
        var connection = await OpenAsync(ct).ConfigureAwait(false);
        await using (connection.ConfigureAwait(false))
        using (await _connections.WriteGate.EnterAsync(ct).ConfigureAwait(false))
        {
            return await work(connection).ConfigureAwait(false);
        }
    }

    private async Task<DbConnection> OpenAsync(CancellationToken ct)
    {
        await _ddl.EnsureAsync(ct).ConfigureAwait(false);
        var connection = _connections.Create();
        await connection.OpenAsync(ct).ConfigureAwait(false);
        return connection;
    }

    private async Task EnsureTableAsync(CancellationToken ct)
    {
        var connection = _connections.Create();
        await using (connection.ConfigureAwait(false))
        {
            await connection.OpenAsync(ct).ConfigureAwait(false);
            await InboundWebhookTable.EnsureAsync(connection, _table, ct).ConfigureAwait(false);
        }
    }
}
```

(Only a granted claim writes anything, so only it commits; the other outcomes dispose the transaction, which rolls back a read.)

- [ ] **Step 6: The command's writes inside a unit, the tables at boot, the registrations**

In `EfAlvoData.Units.cs`, add:

```csharp
    internal Task PublishFrameworkInUnitAsync(WriteUnit unit, AlvoEvent @event, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(@event);
        return unit.RunAsync(token => InUnitAsync(unit, (connection, transaction, ct) => OutboxTable.InsertAsync(connection, transaction, _outboxTable, @event, ct), token), cancellationToken);
    }

    internal Task CommitCommandInUnitAsync(WriteUnit unit, CommandKey key, DateTimeOffset now, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(key);
        return unit.RunAsync(token => InUnitAsync(unit, (connection, transaction, ct) => CommittedOrLostAsync(unit, connection, transaction, key, now, ct), token), cancellationToken);
    }

    private async Task CommittedOrLostAsync(WriteUnit unit, DbConnection connection, DbTransaction transaction, CommandKey key, DateTimeOffset now, CancellationToken ct)
    {
        if (await CommandLedgerTable.CommitAsync(connection, transaction, _commandTable, key, now, ct).ConfigureAwait(false) == 1)
        {
            return;
        }

        var lost = new CommandClaimLostException();
        unit.Poison(lost);
        throw lost;
    }

    private async Task<bool> InUnitAsync(WriteUnit unit, Func<DbConnection, DbTransaction, CancellationToken, Task> write, CancellationToken cancellationToken)
    {
        using var db = ContextFor(unit);
        await using var transaction = await BeginAsync(db, unit, cancellationToken).ConfigureAwait(false);
        await write(unit.Connection, transaction.GetDbTransaction(), cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);

        return true;
    }
```

(`OutboxTable.InsertAsync` and the commit both fit `Func<…, Task>` — the commit's `Task<int>` result is read inside `CommittedOrLostAsync`. A lost claim **poisons** the unit (Ruling 36): even a caller that caught the exception cannot commit the run's writes beside the run that took the key over.)


and, in `EfAlvoData`'s constructor beside `_outboxTable`, `_commandTable = CommandLedgerTable.NameFor(options.SchemaPrefix);` with the field `private readonly string _commandTable;` (add `using System.Data.Common;`). Rewrite `PublishedAsync` to call `InUnitAsync` after its depth check and envelope build, so the unit's outbox insert has one shape. `EfAlvoTransaction` implements the seam:

```csharp
internal sealed class EfAlvoTransaction(EfAlvoData port, WriteUnit unit, AlvoContext context) : IAlvoTransaction, IAlvoSessionSchema, IAlvoCommandUnit
```

```csharp
    /// <inheritdoc/>
    public Task PublishFrameworkAsync(AlvoEvent @event, CancellationToken cancellationToken) =>
        port.PublishFrameworkInUnitAsync(unit, @event, cancellationToken);

    /// <inheritdoc/>
    public Task CommitCommandAsync(CommandKey key, DateTimeOffset now, CancellationToken cancellationToken) =>
        port.CommitCommandInUnitAsync(unit, key, now, cancellationToken);
```

In `SystemSchemaInitializer`: fields `_commandRequestsTableName = CommandLedgerTable.NameFor(schemaPrefix)` and `_inboundWebhooksTableName = InboundWebhookTable.NameFor(schemaPrefix)`, and at the end of `EnsureAsync`:

```csharp
        await CreateIfMissingAsync(
            _commandRequestsTableName, CommandLedgerTable.Ddl(_commandRequestsTableName), ct).ConfigureAwait(false);
        await CreateIndexIfMissingAsync(CommandLedgerTable.IndexDdl(_commandRequestsTableName), ct).ConfigureAwait(false);
        await CreateIfMissingAsync(
            _inboundWebhooksTableName, InboundWebhookTable.Ddl(_inboundWebhooksTableName), ct).ConfigureAwait(false);
        await CreateIndexIfMissingAsync(InboundWebhookTable.IndexDdl(_inboundWebhooksTableName), ct).ConfigureAwait(false);
```

In `AlvoEfCoreProvider`, beside `IManagementIdempotencyStore`:

```csharp
        builder.Services.TryAddSingleton<ICommandLedger>(services => new EfCoreCommandLedger(
            services.GetRequiredService<RelationalConnectionFactory>(),
            services.GetRequiredService<IAlvoSqlDialect>(),
            services.GetRequiredService<IOptions<AlvoOptions>>().Value));
        builder.Services.TryAddSingleton<IInboundWebhookLedger>(services => new EfCoreInboundWebhookLedger(
            services.GetRequiredService<RelationalConnectionFactory>(),
            services.GetRequiredService<IAlvoSqlDialect>(),
            services.GetRequiredService<IOptions<AlvoOptions>>().Value));
```

- [ ] **Step 7: Run the facts on both engines**

Run: `dotnet test --project test/MMLib.Alvo.Data.Sqlite.Tests --filter-class '*SqliteLedgerTests' --filter-class '*ChangeTrackerReachTests' --filter-class '*SystemSchemaInitializer*'` and `dotnet test --project test/MMLib.Alvo.Data.EntityFrameworkCore.Tests --filter-class '*ChangeTrackerReachTests'`
Expected: PASS. A `SystemSchemaInitializer` fact that pins the table list gains the two names. The PostgreSQL leg runs in ring1 below (Testcontainers).

- [ ] **Step 8: Format, ring1, commit**

```bash
dotnet format whitespace MMLib.Alvo.slnx --include src/MMLib.Alvo.Data.EntityFrameworkCore/Internal/CommandLedgerTable.cs src/MMLib.Alvo.Data.EntityFrameworkCore/Internal/InboundWebhookTable.cs src/MMLib.Alvo.Data.EntityFrameworkCore/Internal/OnceDdl.cs src/MMLib.Alvo.Data.EntityFrameworkCore/EfCoreCommandLedger.cs src/MMLib.Alvo.Data.EntityFrameworkCore/EfCoreInboundWebhookLedger.cs src/MMLib.Alvo.Data.EntityFrameworkCore/Internal/SystemSchemaInitializer.cs src/MMLib.Alvo.Data.EntityFrameworkCore/Internal/EfAlvoData.Units.cs src/MMLib.Alvo.Data.EntityFrameworkCore/Internal/EfAlvoData.cs src/MMLib.Alvo.Data.EntityFrameworkCore/Internal/EfAlvoTransaction.cs src/MMLib.Alvo.Data.EntityFrameworkCore/AlvoEfCoreProvider.cs test/_shared/ef/LedgerFacts.cs test/MMLib.Alvo.Data.Sqlite.Tests/SqliteLedgerTests.cs test/MMLib.Alvo.Data.PostgreSql.Tests.Integration/PostgreSqlLedgerTests.cs test/MMLib.Alvo.Data.EntityFrameworkCore.Tests/ChangeTrackerReachTests.cs
scripts/test-ring1
dotnet test --project test/MMLib.Alvo.Data.PostgreSql.Tests.Integration --filter-class '*PostgreSqlLedgerTests'
git add src/MMLib.Alvo.Data.EntityFrameworkCore/Internal/CommandLedgerTable.cs src/MMLib.Alvo.Data.EntityFrameworkCore/Internal/InboundWebhookTable.cs src/MMLib.Alvo.Data.EntityFrameworkCore/Internal/OnceDdl.cs src/MMLib.Alvo.Data.EntityFrameworkCore/EfCoreCommandLedger.cs src/MMLib.Alvo.Data.EntityFrameworkCore/EfCoreInboundWebhookLedger.cs src/MMLib.Alvo.Data.EntityFrameworkCore/Internal/SystemSchemaInitializer.cs src/MMLib.Alvo.Data.EntityFrameworkCore/Internal/EfAlvoData.Units.cs src/MMLib.Alvo.Data.EntityFrameworkCore/Internal/EfAlvoData.cs src/MMLib.Alvo.Data.EntityFrameworkCore/Internal/EfAlvoTransaction.cs src/MMLib.Alvo.Data.EntityFrameworkCore/AlvoEfCoreProvider.cs test/_shared/ef/LedgerFacts.cs test/MMLib.Alvo.Data.Sqlite.Tests/SqliteLedgerTests.cs test/MMLib.Alvo.Data.Sqlite.Tests/MMLib.Alvo.Data.Sqlite.Tests.csproj test/MMLib.Alvo.Data.PostgreSql.Tests.Integration/PostgreSqlLedgerTests.cs test/MMLib.Alvo.Data.PostgreSql.Tests.Integration/MMLib.Alvo.Data.PostgreSql.Tests.Integration.csproj test/MMLib.Alvo.Data.EntityFrameworkCore.Tests/ChangeTrackerReachTests.cs
git commit -m "feat(data): command and inbound webhook ledgers, and a command's writes in its unit

Claude-Session: https://claude.ai/code/session_01NJAtafM29iddLwfhb5gnmB"
```

---

## Task 6: Command registrations, access rules, and boot and apply validation

**Files:**
- Create: `src/MMLib.Alvo/Commands/Setup.cs`, `src/MMLib.Alvo/Commands/Internal/{CommandRegistration.cs,CommandAccess.cs,AccessTranslator.cs,CommandRegistry.cs,CommandBinding.cs,CommandAccessJudge.cs,CommandProblems.cs,CommandApplyRefusals.cs,CommandOperationIds.cs}`
- Modify: `src/MMLib.Alvo/AlvoServiceCollectionExtensions.cs` (`services.AddAlvoCommands()` after `AddAlvoExtensions()`), `src/MMLib.Alvo/Extensions/Setup.cs` (the registry factory), `src/MMLib.Alvo/Extensions/Internal/{ExtensionIds.cs,ExtensionRegistry.cs,ExtensionIdProblems.cs,ExtensionBindings.cs,ExtensionBindingCompiler.cs,ExtensionBinder.cs,ExtensionApplyGuard.cs,ExtensionApplyRefusals.cs}`
- Modify (also): `src/MMLib.Alvo/Extensions/Internal/ExtensionCatalog.cs` (seals on first read)
- Test: `test/MMLib.Alvo.Tests/Commands/{AccessTranslatorTests.cs,CommandRegistryTests.cs,CommandBindingTests.cs,CommandAccessJudgeTests.cs}`

**Interfaces:**
- Consumes: `ExtensionRegistration`, `ExtensionProblem`, `ExtensionBindingCompiler`, `ExtensionBinder`, `ConditionTranslator.Translate<T>(Expression<Func<EventCondition<T>, bool>>, string, HandlerVerb)`, `TypedEntity.NameOf<T>()`, `CelTree.Children`, `EventConditionReads.FieldsReadOrCompared`, `ICommandLedger` (Task 4).
- Produces:
  - `internal sealed record CommandRegistration(string Id, int Sequence, CommandAccess Access, string? Entity, string? Pattern, Type? CommandType, Type? InputType) : ExtensionRegistration` with `IsMapped`, `EventType` (`alvo.command.<Id>.completed`), `Subject(Guid? id)`, consts `EventPrefix`, `EventSuffix`.
  - `internal sealed record CommandAccess(string Source)` with `Of(string? access)`, `Typed(Expression<Func<AlvoConditionCaller, bool>>, string name)`, `Typed<TEntity>(Expression<Func<TEntity, AlvoConditionCaller, bool>>, string name)`.
  - `internal static class AccessTranslator` (`Translate`, `Translate<TEntity>`).
  - `internal sealed class CommandRegistry` (`Mapped`, `Map(string name, string pattern, string? entity, CommandAccess access)`, `Seal()`).
  - `internal sealed class CommandBinding(CommandRegistration, ICelCompiler)` (`Kind = "command"`, `CheckRegistration(bool relational)`, `Bind(PolicyCatalog)`, `Problems`); `internal sealed record BoundCommand(CommandRegistration Registration, CompiledExpression Access)` (`Name`, `ReadsRow`, `Required`).
  - `internal sealed class CommandAccessJudge(ExtensionBinder, IPolicyCatalogProvider, IPredicateEvaluator)` — `void EnsureAdmitted(string name, AlvoRecord? row, AlvoContext caller)`, `bool? Preview(string name, AlvoContext caller)`; consts `Refused`, `NotBound`, `MissingTenant`, `MissingUser` (Ruling 33). The filter (Task 8), `AlvoCommands` (Task 9) and `CommandSimulator` (Task 12) all judge through it.
  - `internal static class CommandProblems` (`Relational(IServiceProviderIsService)`, `Shared(ExtensionRegistry)`); `internal static class CommandApplyRefusals` (`For(...)`); `internal static class CommandOperationIds` (`Prefix = "commands."`, `Of(string name)`).
  - `ExtensionRegistry(IEnumerable<ExtensionRegistration>, CommandRegistry? commands = null)` with `All` (live: registered + mapped), `Commands`, `IsEmpty`, `Seal()`; `ExtensionBindings(handlers, unbound, IEnumerable<BoundCommand>? commands)` with `Command(string name)`; `ExtensionIds.IsCommandName`; `ExtensionApplyRefusals.Generic` made `internal`.

- [ ] **Step 1: Write the failing tests**

`test/MMLib.Alvo.Tests/Commands/AccessTranslatorTests.cs`:

```csharp
using MMLib.Alvo.Commands.Internal;
using MMLib.Alvo.Data;
using MMLib.Alvo.Events;

namespace MMLib.Alvo.Tests.Commands;

/// <summary>A typed access lambda becomes the CEL a string rule would be (spec 2026-10-14 §4.9: "translated like when").</summary>
public sealed class AccessTranslatorTests
{
    [Fact]
    public void A_caller_only_rule_translates_to_a_role_test() =>
        AccessTranslator.Translate(caller => caller.HasRole("staff"), "quote_stay").ShouldBe("'staff' in @user.roles");

    [Fact]
    public void A_row_rule_reads_the_row_as_its_current_image() =>
        AccessTranslator.Translate<Booking>((booking, caller) => booking.Status == BookingStatus.Requested && caller.HasRole("staff"), "confirm_booking")
            .ShouldBe("new.status == 'requested' && 'staff' in @user.roles");

    [Fact]
    public void A_rule_comparing_the_row_with_the_caller_translates_both_sides() =>
        AccessTranslator.Translate<Booking>((booking, caller) => booking.GuestId == caller.User, "cancel_booking")
            .ShouldBe("new.guest_id == @user.id");

    [Fact]
    public void An_untranslatable_rule_is_refused_naming_the_access_slot()
    {
        var refusal = Should.Throw<AlvoTranslationException>(() =>
            AccessTranslator.Translate<Booking>((booking, _) => booking.Room.GetHashCode() == 7, "confirm_booking"));

        refusal.Message.ShouldContain("'confirm_booking' (access)");
        refusal.FixSuggestion.ShouldContain("access: \"…\"");
    }

    [AlvoEntity("bookings")]
    public sealed class Booking
    {
        public Guid GuestId { get; init; }

        public required string Room { get; init; }

        public BookingStatus Status { get; init; }
    }

    public enum BookingStatus
    {
        Requested,
        Confirmed,
        Cancelled,
    }
}
```

(A field compared with the caller's id needs no `string()` — HX-05 Ruling 4 wraps only a uuid *literal* — and the same spelling is what `field-service.alvo.json`'s `assigned_to == @user.id` rules already compile.)

`test/MMLib.Alvo.Tests/Commands/CommandRegistryTests.cs`:

```csharp
using Microsoft.Extensions.DependencyInjection;
using MMLib.Alvo.Commands.Internal;
using MMLib.Alvo.Extensions.Internal;

namespace MMLib.Alvo.Tests.Commands;

/// <summary>Mapped commands live in one registry the boot seals (Ruling 13).</summary>
public sealed class CommandRegistryTests
{
    [Fact]
    public void A_mapped_command_joins_the_extension_registry_after_every_registered_extension()
    {
        using var provider = Provider(alvo => alvo.AddRetiredExtension("old-handler", AlvoRetirement.Discard));
        provider.GetRequiredService<CommandRegistry>().Map("quote_stay", "/commands/quote", null, CommandAccess.Of("true"));

        var all = provider.GetRequiredService<ExtensionRegistry>().All;

        all.Select(registration => registration.Id).ShouldBe(["old-handler", "quote_stay"]);
        all[1].ShouldBeOfType<CommandRegistration>().IsMapped.ShouldBeTrue();
    }

    [Fact]
    public void A_command_mapped_after_the_boot_is_refused()
    {
        using var provider = Provider(_ => { });
        provider.GetRequiredService<ExtensionRegistry>().Seal();

        var refusal = Should.Throw<InvalidOperationException>(() =>
            provider.GetRequiredService<CommandRegistry>().Map("late", "/commands/late", null, CommandAccess.Of("true")));

        refusal.Message.ShouldContain("before app.Run()");
    }

    [Fact]
    public void A_command_mapped_after_a_first_binding_is_refused()
    {
        using var provider = Provider(_ => { });
        provider.GetRequiredService<ExtensionBinder>().Result(MMLib.Alvo.Tests.Extensions.HandlerCatalog.Catalog());

        Should.Throw<InvalidOperationException>(() =>
            provider.GetRequiredService<CommandRegistry>().Map("late", "/commands/late", null, CommandAccess.Of("true")))
            .Message.ShouldContain("before applying a descriptor");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void An_empty_access_rule_refuses_registration(string? access) =>
        Should.Throw<ArgumentException>(() => CommandAccess.Of(access)).Message.ShouldContain("'true' to admit every caller");

    [Theory]
    [InlineData("confirm_booking", "commands.confirmBooking")]
    [InlineData("billing.issue_invoice", "commands.billingIssueInvoice")]
    [InlineData("a", "commands.a")]
    [InlineData("v2.re_quote", "commands.v2ReQuote")]
    public void An_operation_id_is_the_name_in_lower_camel_case(string name, string expected) =>
        CommandOperationIds.Of(name).ShouldBe(expected);

    private static ServiceProvider Provider(Action<IAlvoBuilder> configure)
    {
        var services = new ServiceCollection();
        services.AddAlvo(configure);
        return services.BuildServiceProvider();
    }
}
```

`test/MMLib.Alvo.Tests/Commands/CommandBindingTests.cs`:

```csharp
using Microsoft.Extensions.DependencyInjection;
using MMLib.Alvo.Commands.Internal;
using MMLib.Alvo.Data;
using MMLib.Alvo.Events;
using MMLib.Alvo.Extensions.Internal;
using MMLib.Alvo.Tests.Expressions;
using NSubstitute;

using static MMLib.Alvo.Tests.Extensions.HandlerCatalog;

namespace MMLib.Alvo.Tests.Commands;

/// <summary>
/// Boot validation of commands (spec 2026-10-14 §4.2, §4.9, E3): every problem at once, with id, kind, locator and fix;
/// a command with a problem never binds; the access rule compiles in CelProfile.Condition (Ruling 1).
/// </summary>
public sealed class CommandBindingTests
{
    [Fact]
    public void An_unbound_command_reading_the_caller_and_the_tenant_binds()
    {
        var result = Compile(commands => commands.Map("quote_stay", "/commands/quote", null, CommandAccess.Of("'staff' in @user.roles && has(@tenant.id)")));

        result.Problems.ShouldBeEmpty();
        var bound = result.Bindings.Command("quote_stay").ShouldNotBeNull();
        bound.ReadsRow.ShouldBeFalse();
    }

    [Fact]
    public void An_entity_command_reading_its_row_binds_and_reads_the_row()
    {
        var result = Compile(commands => commands.Map("resolve_ticket", "/commands/tickets/{id}/resolve", "tickets", CommandAccess.Of("status == 'open' && 'staff' in @user.roles")));

        result.Problems.ShouldBeEmpty();
        result.Bindings.Command("resolve_ticket").ShouldNotBeNull().ReadsRow.ShouldBeTrue();
    }

    [Fact]
    public void An_unbound_command_naming_a_field_is_refused_at_its_access_slot() =>
        Compile(commands => commands.Map("quote_stay", "/q", null, CommandAccess.Of("status == 'open'")))
            .Problems.ShouldContain(problem => problem.Id == "quote_stay" && problem.Kind == "command" && problem.Locator == "extensions/quote_stay/access");

    [Fact]
    public void A_command_bound_to_an_undeclared_entity_is_refused_at_its_entity_slot() =>
        Compile(commands => commands.Map("resolve", "/r/{id}", "tikets", CommandAccess.Of("true")))
            .Problems.ShouldContain(problem => problem.Locator == "extensions/resolve/entity");

    [Theory]
    [InlineData("old.status == 'open'")]
    [InlineData("changed(status)")]
    public void An_access_rule_reading_a_before_image_is_refused(string access) =>
        Compile(commands => commands.Map("resolve", "/r/{id}", "tickets", CommandAccess.Of(access)))
            .Problems.ShouldContain(problem => problem.Locator == "extensions/resolve/access" && problem.Message.Contains("no before-image", StringComparison.Ordinal));

    [Fact]
    public void A_command_name_off_the_grammar_is_refused_with_the_command_fix() =>
        Compile(commands => commands.Map("Confirm-Booking", "/c", null, CommandAccess.Of("true")))
            .Problems.ShouldContain(problem => problem.Locator == "extensions/Confirm-Booking/id" && problem.Fix.Contains("separated by dots", StringComparison.Ordinal));

    [Fact]
    public void Two_commands_publishing_one_operation_id_are_refused() =>
        Compile(commands =>
        {
            commands.Map("a_b", "/one", null, CommandAccess.Of("true"));
            commands.Map("a.b", "/two", null, CommandAccess.Of("true"));
        }).Problems.Where(problem => problem.Message.Contains("operationId", StringComparison.Ordinal)).Select(problem => problem.Id)
            .ShouldBe(["a_b", "a.b"], ignoreOrder: true);

    [Fact]
    public void A_command_and_a_handler_deriving_one_event_segment_are_refused() =>
        Compile(
            commands => commands.Map("send_note", "/n", null, CommandAccess.Of("true")),
            alvo => alvo.Entity("notes", n => n.OnCreated("send-note", OnCreated)))
            .Problems.ShouldContain(problem => problem.Message.Contains("one event-name segment", StringComparison.Ordinal));

    [Fact]
    public void A_handler_of_a_completion_no_command_produces_is_refused() =>
        Compile(
            _ => { },
            alvo => alvo.AddEventHandler("on-confirmed", "alvo.command.confirm_booking.completed", (AlvoRecord _) => Task.CompletedTask))
            .Problems.ShouldContain(problem => problem.Id == "on-confirmed" && problem.Locator == "extensions/on-confirmed/event");

    [Fact]
    public void A_handler_of_a_mapped_command_s_completion_binds()
    {
        var result = Compile(
            commands => commands.Map("confirm_booking", "/c/{id}", "tickets", CommandAccess.Of("true")),
            alvo => alvo.AddEventHandler("on-confirmed", "alvo.command.confirm_booking.completed", (AlvoRecord _) => Task.CompletedTask));

        result.Problems.ShouldBeEmpty();
        result.Bindings.Handler("on-confirmed").ShouldNotBeNull();
    }

    [Fact]
    public void A_composition_without_a_relational_provider_refuses_every_command()
    {
        var services = Substitute.For<IServiceProviderIsService>();
        services.IsService(Arg.Any<Type>()).Returns(call => call.Arg<Type>() != typeof(ICommandLedger));

        Compile(commands => commands.Map("quote_stay", "/q", null, CommandAccess.Of("true")), services: services)
            .Problems.ShouldContain(problem => problem.Locator == "extensions/quote_stay/provider");
    }

    private static ExtensionBindingResult Compile(
        Action<CommandRegistry> map, Action<IAlvoBuilder>? configure = null, IServiceProviderIsService? services = null)
    {
        var collection = new ServiceCollection();
        collection.AddAlvo(configure ?? (_ => { }));
        using var provider = collection.BuildServiceProvider();
        map(provider.GetRequiredService<CommandRegistry>());

        return ExtensionBindingCompiler.Compile(provider.GetRequiredService<ExtensionRegistry>(), Catalog(), CelFixtures.Compiler, services ?? EveryService());
    }
}
```

`test/MMLib.Alvo.Tests/Commands/CommandAccessJudgeTests.cs`:

```csharp
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using MMLib.Alvo.Commands.Internal;
using MMLib.Alvo.Data;
using MMLib.Alvo.Expressions;
using MMLib.Alvo.Extensions.Internal;
using MMLib.Alvo.Rules;
using MMLib.Alvo.Tests.Expressions;
using NSubstitute;

using static MMLib.Alvo.Tests.Extensions.HandlerCatalog;

namespace MMLib.Alvo.Tests.Commands;

/// <summary>
/// The one judge of command access (Ruling 33): a caller lacking a context value the rule reads is refused before the rule
/// runs — so a negation over an absent value never admits.
/// </summary>
public sealed class CommandAccessJudgeTests
{
    private const string Blocked = "0b0e0c2a-1b1b-4c4c-8d8d-9e9e9e9e9e9e";

    private static readonly AlvoContext _tenantless = new() { User = UserId.New(), Roles = new HashSet<Role> { Role.Authenticated } };

    [Fact]
    public void A_negated_tenant_rule_refuses_a_tenantless_caller_instead_of_admitting_it()
    {
        var judge = Judge(out _);

        Should.Throw<AlvoAuthorizationException>(() => judge.EnsureAdmitted("not_blocked", null, _tenantless))
            .Message.ShouldBe(CommandAccessJudge.MissingTenant);
        Should.Throw<AlvoAuthorizationException>(() => judge.EnsureAdmitted("not_blocked", null, AlvoContext.Anonymous));
    }

    [Fact]
    public void A_negated_tenant_rule_admits_another_tenant_and_refuses_the_blocked_one()
    {
        var judge = Judge(out _);

        Should.NotThrow(() => judge.EnsureAdmitted("not_blocked", null, _tenantless with { Tenant = new TenantId(Guid.NewGuid()) }));
        Should.Throw<AlvoAuthorizationException>(() => judge.EnsureAdmitted("not_blocked", null, _tenantless with { Tenant = new TenantId(Guid.Parse(Blocked)) }))
            .Message.ShouldBe(CommandAccessJudge.Refused);
    }

    [Fact]
    public void A_negated_identity_rule_refuses_the_anonymous_caller() =>
        Should.Throw<AlvoAuthorizationException>(() => Judge(out _).EnsureAdmitted("not_banned", null, AlvoContext.Anonymous))
            .Message.ShouldBe(CommandAccessJudge.MissingUser);

    [Fact]
    public void The_preview_refuses_whom_the_judge_refuses()
    {
        var judge = Judge(out _);

        judge.Preview("not_blocked", _tenantless).ShouldBe(false);
        judge.Preview("not_banned", AlvoContext.Anonymous).ShouldBe(false);
        judge.Preview("not_banned", _tenantless).ShouldBe(true);
    }

    [Fact]
    public void A_command_with_no_bound_catalog_is_refused_as_not_bound()
    {
        var judge = Judge(out var catalogs);
        catalogs.Current.Returns((PolicyCatalog?)null);

        Should.Throw<AlvoAuthorizationException>(() => judge.EnsureAdmitted("not_banned", null, _tenantless)).Message.ShouldBe(CommandAccessJudge.NotBound);
        judge.Preview("not_banned", _tenantless).ShouldBe(false);
    }

    private static CommandAccessJudge Judge(out IPolicyCatalogProvider catalogs)
    {
        var services = new ServiceCollection();
        services.AddAlvo();
        var provider = services.BuildServiceProvider();
        var commands = provider.GetRequiredService<CommandRegistry>();
        commands.Map("not_blocked", "/a", null, CommandAccess.Of($"!(string(@tenant.id) == '{Blocked}')"));
        commands.Map("not_banned", "/b", null, CommandAccess.Of("!(string(@user.id) == '6c1f9a3e-0000-4000-8000-000000000001')"));
        catalogs = Substitute.For<IPolicyCatalogProvider>();
        catalogs.Current.Returns(Catalog());
        var binder = new ExtensionBinder(provider.GetRequiredService<ExtensionRegistry>(), CelFixtures.Compiler, EveryService(), NullLogger<ExtensionBinder>.Instance);

        return new CommandAccessJudge(binder, catalogs, provider.GetRequiredService<IPredicateEvaluator>());
    }
}
```

Append to `CommandBindingTests`:

```csharp
    [Fact]
    public void An_access_rule_reading_a_hidden_field_is_refused()
    {
        var collection = new ServiceCollection();
        collection.AddAlvo();
        using var provider = collection.BuildServiceProvider();
        provider.GetRequiredService<CommandRegistry>().Map("resolve", "/r/{id}", "tickets", CommandAccess.Of("!(status == 'resolved')"));

        var problems = ExtensionBindingCompiler.Compile(provider.GetRequiredService<ExtensionRegistry>(), HidingStatus(), CelFixtures.Compiler, EveryService()).Problems;

        problems.ShouldContain(problem => problem.Locator == "extensions/resolve/access" && problem.Message.Contains("hides from some callers", StringComparison.Ordinal));
    }

    private static PolicyCatalog HidingStatus() =>
        PolicyCatalog.Build(
            new AlvoDescriptor
            {
                ApiVersion = "alvo.dev/v1",
                Name = "handlers",
                Entities = Schema.Entities.ToDictionary(
                    entity => entity.Name,
                    entity => new EntityDescriptor
                    {
                        Fields = entity.Name == "tickets"
                            ? new Dictionary<string, FieldDescriptor>(StringComparer.Ordinal) { ["status"] = new() { Hidden = BoolOrCel.FromExpression("!('staff' in @user.roles)") } }
                            : new Dictionary<string, FieldDescriptor>(StringComparer.Ordinal),
                    },
                    StringComparer.Ordinal),
            },
            Schema,
            CelFixtures.Compiler);
```

(add `using MMLib.Alvo.Descriptor; using MMLib.Alvo.Rules;` to `CommandBindingTests`; `FieldDescriptor`'s other members default as `HandlerCatalog.CatalogOf` relies on. A descriptor change that hides a field a bound command's rule reads unbinds the command, so `ExtensionApplyGuard` refuses it with this sentence at `""` — the binder's generic pointer, Ruling 35.)

- [ ] **Step 2: Run them to verify they fail**

Run: `dotnet test --project test/MMLib.Alvo.Tests --filter-class '*AccessTranslatorTests' --filter-class '*CommandRegistryTests' --filter-class '*CommandBindingTests' --filter-class '*CommandAccessJudgeTests'`
Expected: FAIL to compile (`MMLib.Alvo.Commands.Internal` does not exist).

- [ ] **Step 3: What a command is**

`src/MMLib.Alvo/Commands/Internal/CommandRegistration.cs`:

```csharp
using MMLib.Alvo.Extensions.Internal;

namespace MMLib.Alvo.Commands.Internal;

/// <summary>
/// One command: mapped over HTTP (<see cref="Pattern"/> set) or added for in-process invocation (<see cref="CommandType"/>
/// set). It shares the extension id namespace (spec 2026-10-14 E3).
/// </summary>
/// <param name="Id">The command's name.</param>
/// <param name="Sequence">Its place among the host's registrations.</param>
/// <param name="Access">Its access rule.</param>
/// <param name="Entity">The entity a mapped command binds its <c>{id}</c> to; null for none.</param>
/// <param name="Pattern">The route pattern of a mapped command; null for an internal one.</param>
/// <param name="CommandType">The <c>IAlvoCommand&lt;TInput&gt;</c> class of an internal command.</param>
/// <param name="InputType">The input type of an internal command.</param>
internal sealed record CommandRegistration(
    string Id, int Sequence, CommandAccess Access, string? Entity, string? Pattern, Type? CommandType, Type? InputType)
    : ExtensionRegistration(Id, Sequence)
{
    /// <summary>The start of every command completion's event type.</summary>
    internal const string EventPrefix = "alvo.command.";

    /// <summary>The end of every command completion's event type.</summary>
    internal const string EventSuffix = ".completed";

    /// <summary>Gets a value indicating whether the command is reached over HTTP.</summary>
    internal bool IsMapped => Pattern is not null;

    /// <summary>Gets the type of the event a completed run emits.</summary>
    internal string EventType => EventPrefix + Id + EventSuffix;

    /// <summary>The completion event's subject: the bound row, or the command.</summary>
    /// <param name="id">The bound row's id, when there is one.</param>
    internal string Subject(Guid? id) => Entity is { } entity && id is { } row ? $"{entity}/{row:D}" : $"commands/{Id}";
}
```

`src/MMLib.Alvo/Commands/Internal/CommandAccess.cs`:

```csharp
using MMLib.Alvo.Events;
using System.Linq.Expressions;

namespace MMLib.Alvo.Commands.Internal;

/// <summary>A command's access rule as CEL text — written by the host, or translated from a typed lambda.</summary>
/// <param name="Source">The CEL text, exactly as the catalog shows it.</param>
internal sealed record CommandAccess(string Source)
{
    /// <summary>A rule written as CEL; an empty one refuses registration (spec 2026-10-14 §4.9, §6.2).</summary>
    /// <param name="access">The CEL text.</param>
    /// <exception cref="ArgumentException"><paramref name="access"/> is null, empty or whitespace.</exception>
    internal static CommandAccess Of(string? access) =>
        string.IsNullOrWhiteSpace(access)
            ? throw new ArgumentException(
                "A command needs an access rule: nothing is reachable without one. Write the CEL condition that admits its "
                + "callers — 'staff' in @user.roles — or 'true' to admit every caller, anonymous ones included.",
                nameof(access))
            : new CommandAccess(access);

    /// <summary>A caller-only rule written as a lambda.</summary>
    /// <param name="access">The lambda.</param>
    /// <param name="name">The command's name, for a refusal.</param>
    internal static CommandAccess Typed(Expression<Func<AlvoConditionCaller, bool>> access, string name) =>
        new(AccessTranslator.Translate(access, name));

    /// <summary>A row-and-caller rule written as a lambda.</summary>
    /// <typeparam name="TEntity">The typed record of the bound entity.</typeparam>
    /// <param name="access">The lambda.</param>
    /// <param name="name">The command's name, for a refusal.</param>
    internal static CommandAccess Typed<TEntity>(Expression<Func<TEntity, AlvoConditionCaller, bool>> access, string name)
        where TEntity : class =>
        new(AccessTranslator.Translate(access, name));
}
```

`src/MMLib.Alvo/Commands/Internal/AccessTranslator.cs`:

```csharp
using MMLib.Alvo.Data;
using MMLib.Alvo.Events;
using MMLib.Alvo.Extensions.Internal;
using System.Linq.Expressions;

namespace MMLib.Alvo.Commands.Internal;

/// <summary>
/// Translates a typed access lambda by rewriting it into the shape of an event handler's <c>when</c> — the row becomes
/// <c>e.New</c>, the caller <c>e.Caller</c> — and handing it to the one condition translator (Ruling 2).
/// </summary>
/// <remarks>
/// Translating as a create's condition is what refuses <c>old.</c> for free: a command's row has no before-image. A refusal
/// quotes the rewritten node and names the <c>access</c> slot.
/// </remarks>
internal static class AccessTranslator
{
    private const string AccessFix = "write the access rule as a CEL string — access: \"…\" — or check the rest in the handler body after the rule admits the caller.";

    /// <summary>Translates a caller-only rule.</summary>
    /// <param name="access">The lambda.</param>
    /// <param name="name">The command's name.</param>
    internal static string Translate(Expression<Func<AlvoConditionCaller, bool>> access, string name)
    {
        ArgumentNullException.ThrowIfNull(access);
        var condition = Expression.Parameter(typeof(EventCondition<AlvoRecord>), "e");
        var body = Rebind(access.Body, access.Parameters[0], Expression.Property(condition, nameof(EventCondition<>.Caller)));

        return Translated(Expression.Lambda<Func<EventCondition<AlvoRecord>, bool>>(body, condition), name);
    }

    /// <summary>Translates a rule over the bound row and the caller.</summary>
    /// <typeparam name="TEntity">The typed record of the bound entity.</typeparam>
    /// <param name="access">The lambda.</param>
    /// <param name="name">The command's name.</param>
    internal static string Translate<TEntity>(Expression<Func<TEntity, AlvoConditionCaller, bool>> access, string name)
        where TEntity : class
    {
        ArgumentNullException.ThrowIfNull(access);
        var condition = Expression.Parameter(typeof(EventCondition<TEntity>), "e");
        var row = Rebind(access.Body, access.Parameters[0], Expression.Property(condition, nameof(EventCondition<>.New)));
        var body = Rebind(row, access.Parameters[1], Expression.Property(condition, nameof(EventCondition<>.Caller)));

        return Translated(Expression.Lambda<Func<EventCondition<TEntity>, bool>>(body, condition), name);
    }

    private static string Translated<T>(Expression<Func<EventCondition<T>, bool>> when, string name)
        where T : class
    {
        try
        {
            return ConditionTranslator.Translate(when, name, HandlerVerb.Created);
        }
        catch (AlvoTranslationException refusal)
        {
            throw new AlvoTranslationException(refusal.Message.Replace(" (when): ", " (access): ", StringComparison.Ordinal), AccessFix);
        }
    }

    private static Expression Rebind(Expression body, ParameterExpression from, Expression to) => new Rebinder(from, to).Visit(body);

    private sealed class Rebinder(ParameterExpression from, Expression to) : ExpressionVisitor
    {
        protected override Expression VisitParameter(ParameterExpression node) => node == from ? to : base.VisitParameter(node);
    }
}
```

`src/MMLib.Alvo/Commands/Internal/CommandOperationIds.cs`:

```csharp
using System.Text;

namespace MMLib.Alvo.Commands.Internal;

/// <summary>A command's OpenAPI operationId: <c>commands.</c> and its name in lower camel case, the shape the ruleset accepts.</summary>
internal static class CommandOperationIds
{
    /// <summary>The prefix every command operationId carries.</summary>
    internal const string Prefix = "commands.";

    /// <summary>The operationId of <paramref name="name"/>: <c>confirm_booking</c> → <c>commands.confirmBooking</c>.</summary>
    /// <param name="name">The command's name.</param>
    internal static string Of(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        var id = new StringBuilder(Prefix, Prefix.Length + name.Length);
        var upper = false;
        foreach (var character in name)
        {
            if (character is '.' or '_')
            {
                upper = id.Length > Prefix.Length;
                continue;
            }

            id.Append(upper ? char.ToUpperInvariant(character) : character);
            upper = false;
        }

        return id.ToString();
    }
}
```

`src/MMLib.Alvo/Commands/Internal/CommandRegistry.cs`:

```csharp
namespace MMLib.Alvo.Commands.Internal;

/// <summary>
/// The commands mapped with <c>MapAlvoCommand</c>. Mapping happens after the container is built, so mapped commands are
/// kept here and joined to the DI-registered extensions by <c>ExtensionRegistry</c>; the first binding seals it (Rulings 13, 40).
/// </summary>
internal sealed class CommandRegistry
{
    private const int MappedSequenceBase = 1 << 20;

    private readonly List<CommandRegistration> _mapped = [];
    private readonly Lock _gate = new();
    private bool _sealed;

    /// <summary>Gets the mapped commands, in mapping order.</summary>
    internal IReadOnlyList<CommandRegistration> Mapped
    {
        get
        {
            lock (_gate)
            {
                return [.. _mapped];
            }
        }
    }

    /// <summary>Records a mapped command.</summary>
    /// <exception cref="InvalidOperationException">The host has already booted.</exception>
    internal CommandRegistration Map(string name, string pattern, string? entity, CommandAccess access)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentException.ThrowIfNullOrWhiteSpace(pattern);
        ArgumentNullException.ThrowIfNull(access);

        lock (_gate)
        {
            if (_sealed)
            {
                throw new InvalidOperationException(
                    $"Command '{name}' was mapped after Alvo first bound its extensions — at boot, or when a descriptor was applied. "
                    + "A binding made then would never contain it: map every command before app.Run() and before applying a descriptor.");
            }

            var command = new CommandRegistration(name, MappedSequenceBase + _mapped.Count, access, entity, pattern, null, null);
            _mapped.Add(command);
            return command;
        }
    }

    /// <summary>Refuses every later <see cref="Map"/>; the boot calls it before it checks the registrations.</summary>
    internal void Seal()
    {
        lock (_gate)
        {
            _sealed = true;
        }
    }
}
```

`src/MMLib.Alvo/Commands/Setup.cs`:

```csharp
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using MMLib.Alvo.Commands.Internal;

namespace MMLib.Alvo.Commands;

/// <summary>Registers the command feature; called from <c>AddAlvo</c> and nowhere else.</summary>
internal static class CommandsSetup
{
    internal static IServiceCollection AddAlvoCommands(this IServiceCollection services)
    {
        services.TryAddSingleton<CommandRegistry>();
        return services;
    }
}
```

- [ ] **Step 4: Join the extension registry, its id checks and its binder**

`ExtensionIds`: add `internal static bool IsCommandName(string id) => Command().IsMatch(id);`.

`ExtensionRegistry` becomes:

```csharp
using MMLib.Alvo.Commands.Internal;

namespace MMLib.Alvo.Extensions.Internal;

/// <summary>
/// Every extension this host registers: the DI registrations, ordered by registration, then the mapped commands.
/// </summary>
/// <remarks><see cref="All"/> is read live, because commands are mapped after the container is built and before the boot.</remarks>
internal sealed class ExtensionRegistry
{
    private readonly IReadOnlyList<ExtensionRegistration> _registered;
    private readonly CommandRegistry? _commands;

    internal ExtensionRegistry(IEnumerable<ExtensionRegistration> registrations, CommandRegistry? commands = null)
    {
        _registered = [.. registrations.OrderBy(registration => registration.Sequence)];
        _commands = commands;
        Handlers = [.. _registered.OfType<HandlerRegistration>()];
        Retired = [.. _registered.OfType<RetiredExtension>()];
    }

    internal static ExtensionRegistry Empty { get; } = new([]);

    internal IReadOnlyList<ExtensionRegistration> All => _commands is null ? _registered : [.. _registered, .. _commands.Mapped];

    internal IReadOnlyList<HandlerRegistration> Handlers { get; }

    internal IReadOnlyList<RetiredExtension> Retired { get; }

    internal IReadOnlyList<CommandRegistration> Commands => [.. All.OfType<CommandRegistration>()];

    internal bool IsEmpty => Handlers.Count == 0 && Commands.Count == 0;

    internal void Seal() => _commands?.Seal();
}
```

(keep the existing XML docs on the members that already had them). In `Extensions/Setup.cs`: `services.TryAddSingleton(provider => new ExtensionRegistry(provider.GetServices<ExtensionRegistration>(), provider.GetRequiredService<CommandRegistry>()));`. In `AlvoServiceCollectionExtensions`, after `services.AddAlvoExtensions();` add `services.AddAlvoCommands();` (`using MMLib.Alvo.Commands;`).

`ExtensionIdProblems`:

```csharp
    private const string CommandNameFix =
        "Use lower-case segments of letters, digits and underscores, each starting with a letter, separated by dots — 'confirm_booking' or 'billing.issue_invoice'.";

    private static bool Grammatical(ExtensionRegistration registration) => registration switch
    {
        RetiredExtension => ExtensionIds.IsRetirableId(registration.Id),
        CommandRegistration => ExtensionIds.IsCommandName(registration.Id),
        _ => ExtensionIds.IsExtensionId(registration.Id),
    };

    private static ExtensionProblem BadId(ExtensionRegistration registration) => registration is CommandRegistration
        ? new(registration.Id, KindOf(registration), IdLocator(registration.Id), $"'{registration.Id}' is not a command name.", CommandNameFix)
        : new(registration.Id, KindOf(registration), IdLocator(registration.Id), $"'{registration.Id}' is not an extension id.", IdFix);

    private static string KindOf(ExtensionRegistration registration) => registration switch
    {
        RetiredExtension => ExtensionBindingCompiler.RetiredKind,
        CommandRegistration => CommandBinding.Kind,
        _ => ExtensionBindingCompiler.HandlerKind,
    };
```

`src/MMLib.Alvo/Commands/Internal/CommandBinding.cs`:

```csharp
using MMLib.Alvo.Events.Internal;
using MMLib.Alvo.Expressions;
using MMLib.Alvo.Expressions.Internal;
using MMLib.Alvo.Extensions.Internal;
using MMLib.Alvo.Rules;
using MMLib.Alvo.Schema;

namespace MMLib.Alvo.Commands.Internal;

/// <summary>A command bound to a catalog: its registration and its compiled access rule.</summary>
internal sealed record BoundCommand(CommandRegistration Registration, CompiledExpression Access)
{
    /// <summary>Gets the command's name.</summary>
    internal string Name => Registration.Id;

    /// <summary>Gets a value indicating whether the rule reads the row, so it can be answered only per row.</summary>
    internal bool ReadsRow => EventConditionReads.FieldsReadOrCompared(Access.Root).Any();

    /// <summary>
    /// Gets the context values the rule reads, measured once — as <c>PolicyEngine</c> measures a rule's, so a caller lacking
    /// one is refused before the rule is evaluated (Ruling 33).
    /// </summary>
    internal RequiredContext Required { get; } = EventConditionReads.Required(Access);
}

/// <summary>
/// Checks one command and compiles its access rule in <see cref="CelProfile.Condition"/> (Ruling 1): against a fieldless
/// scope when it binds no entity, so the rule reads only <c>@user</c> and <c>@tenant</c>; against its entity otherwise, the
/// row as the current image. <c>old.</c> and <c>changed(…)</c> are refused: a command's row has no before-image; so is a
/// field with any <c>hidden</c> flag, which the row the caller reads would not carry (Ruling 35 — the precedent is a
/// handler condition reading an <c>events.exclude</c> field).
/// </summary>
internal sealed class CommandBinding(CommandRegistration command, ICelCompiler compiler)
{
    /// <summary>The kind a problem names.</summary>
    internal const string Kind = "command";

    private const string AccessFix = "Correct the access rule: it is CEL over @user and @tenant, and over the row's fields for a command bound to an entity.";

    private static readonly EntitySchema _noRow = new() { Name = "<command>", Fields = [] };

    private readonly List<ExtensionProblem> _problems = [];
    private CompiledExpression? _unbound;

    /// <summary>Gets every problem found so far.</summary>
    internal IReadOnlyList<ExtensionProblem> Problems => _problems;

    /// <summary>The catalog-free pass: the provider, and an unbound command's rule.</summary>
    /// <param name="relational">Whether the composition has a command ledger and transaction units.</param>
    internal CommandBinding CheckRegistration(bool relational)
    {
        if (!relational)
        {
            Add("provider",
                "Commands need a relational data provider — it keeps their idempotency keys and opens their transaction unit — and this composition registers none.",
                "Register UseSqlite(…) or UsePostgreSql(…); the in-memory reference provider runs no commands.");
        }

        if (command.Entity is null)
        {
            _unbound = Compile(_noRow);
        }

        return this;
    }

    /// <summary>Binds the command to <paramref name="catalog"/>, or answers null when it has a problem.</summary>
    internal BoundCommand? Bind(PolicyCatalog catalog)
    {
        var access = command.Entity is { } entity ? Compile(entity, catalog) : _unbound;
        return _problems.Count == 0 && access is not null ? new BoundCommand(command, access) : null;
    }

    private CompiledExpression? Compile(string entity, PolicyCatalog catalog)
    {
        var schema = catalog.Schema.Entities.FirstOrDefault(candidate => string.Equals(candidate.Name, entity, StringComparison.Ordinal));
        if (schema is not null)
        {
            return Compile(schema) is { } access ? WithoutHiddenReads(access, entity, catalog) : null;
        }

        Add("entity", $"It is bound to entity '{entity}', which the descriptor does not declare.", "Bind it to a declared entity, or declare the entity.");
        return null;
    }

    private CompiledExpression? Compile(EntitySchema scope)
    {
        var result = compiler.Compile(command.Access.Source, CelProfile.Condition, scope);
        if (result.IsSuccess)
        {
            return WithoutImages(result.Expression!);
        }

        foreach (var error in result.Errors)
        {
            Add("access", error.Message, error.FixSuggestion ?? AccessFix);
        }

        return null;
    }

    private CompiledExpression? WithoutImages(CompiledExpression access)
    {
        if (!ReadsAnImage(access.Root))
        {
            return access;
        }

        Add("access",
            "An access rule reads the row as it is now: a command has no before-image, so old. and changed(…) mean nothing here.",
            "Read the field without old. — status == 'requested' — and drop changed(…).");
        return null;
    }

    private CompiledExpression? WithoutHiddenReads(CompiledExpression access, string entity, PolicyCatalog catalog)
    {
        if (!catalog.TryGetEntity(entity, out var policy))
        {
            return access;
        }

        var hidden = EventConditionReads.FieldsReadOrCompared(access.Root).Where(policy.Hidden.ContainsKey).Distinct(StringComparer.Ordinal).ToList();
        foreach (var field in hidden)
        {
            Add("access",
                $"The rule reads '{field}', which '{entity}' hides from some callers: for them the rule would be decided by the field's absence.",
                $"Remove '{field}' from the access rule, or its hidden flag; if it must decide, check it in the handler after the rule admits the caller.");
        }

        return hidden.Count == 0 ? access : null;
    }

    private static bool ReadsAnImage(CelNode node) =>
        node is CelChanged or CelFieldRef { State: CelRecordState.Old } || CelTree.Children(node).Any(ReadsAnImage);

    private void Add(string slot, string message, string fix) =>
        _problems.Add(new ExtensionProblem(command.Id, Kind, $"extensions/{command.Id}/{slot}", message, fix));
}
```

`src/MMLib.Alvo/Commands/Internal/CommandAccessJudge.cs`:

```csharp
using MMLib.Alvo.Data;
using MMLib.Alvo.Expressions;
using MMLib.Alvo.Extensions.Internal;
using MMLib.Alvo.Rules;

namespace MMLib.Alvo.Commands.Internal;

/// <summary>
/// The one judge of a command's access rule (Ruling 33): the HTTP filter, <c>IAlvoCommands</c> and the policy simulator all
/// ask it, so the simulator cannot answer otherwise than production does (spec 2026-10-14 §6.2 "simulator parity").
/// </summary>
/// <remarks>
/// Like <c>PolicyEngine</c>'s required-context gate, it refuses <b>before</b> evaluating when the rule reads
/// <c>@tenant.id</c> and the caller has no tenant, or <c>@user.id</c> and the caller has no identity: an absent operand
/// collapses to false, and a negation — <c>!(string(@tenant.id) == '…')</c> — would invert that into an admission.
/// </remarks>
internal sealed class CommandAccessJudge(ExtensionBinder binder, IPolicyCatalogProvider catalogs, IPredicateEvaluator evaluator)
{
    internal const string Refused = "This command's access rule does not admit the caller.";

    internal const string NotBound =
        "This command is not bound to an applied descriptor, so its access rule cannot be judged. It answers once a descriptor it fits is applied.";

    internal const string MissingTenant = "The caller has no tenant, and this command's access rule reads one.";

    internal const string MissingUser = "The caller has no identity, and this command's access rule reads one.";

    /// <summary>Admits the caller, or throws the refusal.</summary>
    /// <param name="name">The command.</param>
    /// <param name="row">The bound row as the caller reads it; null for a command bound to none.</param>
    /// <param name="caller">The caller.</param>
    /// <exception cref="AlvoAuthorizationException">The rule — or its required context — refuses the caller.</exception>
    internal void EnsureAdmitted(string name, AlvoRecord? row, AlvoContext caller)
    {
        if (Refusal(Bound(name), row, caller) is { } refusal)
        {
            throw new AlvoAuthorizationException(refusal);
        }
    }

    /// <summary>What the rule answers with no row: false when refused, null when it reads the row and so is decided per row.</summary>
    internal bool? Preview(string name, AlvoContext caller) =>
        Bound(name) is not { } bound || Missing(bound, caller) is not null ? false
        : bound.ReadsRow ? null
        : evaluator.Evaluate(bound.Access, AlvoRecord.Empty, null, caller);

    private string? Refusal(BoundCommand? bound, AlvoRecord? row, AlvoContext caller) =>
        bound is null ? NotBound
        : Missing(bound, caller) ?? (evaluator.Evaluate(bound.Access, row ?? AlvoRecord.Empty, null, caller) ? null : Refused);

    private static string? Missing(BoundCommand bound, AlvoContext caller) =>
        bound.Required.TenantIdMissingFrom(caller) ? MissingTenant
        : bound.Required.UserIdMissingFrom(caller) ? MissingUser
        : null;

    private BoundCommand? Bound(string name) =>
        catalogs.Current is { } catalog ? binder.For(catalog).Command(name) : null;
}
```

Register it in `Commands/Setup.cs`: `services.TryAddSingleton<CommandAccessJudge>();`.

`src/MMLib.Alvo/Commands/Internal/CommandProblems.cs`:

```csharp
using Microsoft.Extensions.DependencyInjection;
using MMLib.Alvo.Data;
using MMLib.Alvo.Extensions.Internal;

namespace MMLib.Alvo.Commands.Internal;

/// <summary>Problems that span commands: one operationId per command, and handlers of completions no command produces.</summary>
internal static class CommandProblems
{
    /// <summary>Whether the composition can run commands: a command ledger and transaction units are registered.</summary>
    internal static bool Relational(IServiceProviderIsService services) =>
        services.IsService(typeof(ICommandLedger)) && services.IsService(typeof(IAlvoTransactions));

    /// <summary>Every problem between registrations.</summary>
    internal static IReadOnlyList<ExtensionProblem> Shared(ExtensionRegistry registry) =>
        [.. OperationIdCollisions(registry), .. CompletionsOfNoCommand(registry)];

    private static IEnumerable<ExtensionProblem> OperationIdCollisions(ExtensionRegistry registry) =>
        registry.Commands.Where(command => command.IsMapped && ExtensionIds.IsCommandName(command.Id))
            .GroupBy(command => CommandOperationIds.Of(command.Id), StringComparer.Ordinal)
            .Where(group => group.Count() > 1)
            .SelectMany(group => group.Select(command => new ExtensionProblem(
                command.Id,
                CommandBinding.Kind,
                $"extensions/{command.Id}/id",
                $"'{string.Join("' and '", group.Select(other => other.Id))}' publish one OpenAPI operationId, '{group.Key}', so a client generated from the document could not tell them apart.",
                "Rename one of them.")));

    private static IEnumerable<ExtensionProblem> CompletionsOfNoCommand(ExtensionRegistry registry)
    {
        var completions = registry.Commands.Select(command => command.EventType).ToHashSet(StringComparer.Ordinal);

        return registry.Handlers
            .Where(handler => handler.Source is NamedEvent { Type: var type }
                && type.StartsWith(CommandRegistration.EventPrefix, StringComparison.Ordinal)
                && !completions.Contains(type))
            .Select(handler => new ExtensionProblem(
                handler.Id,
                ExtensionBindingCompiler.HandlerKind,
                $"extensions/{handler.Id}/event",
                $"It handles '{((NamedEvent)handler.Source).Type}', but this host registers no command that completes with it, so it would never run.",
                "Name a command this host maps or adds — alvo.command.<name>.completed — or remove the handler."));
    }
}
```

`ExtensionBindings`: constructor gains `IEnumerable<BoundCommand>? commands = null`; store `Commands = (commands ?? []).ToDictionary(command => command.Name, StringComparer.Ordinal)` as `internal IReadOnlyDictionary<string, BoundCommand> Commands { get; }` and add `internal BoundCommand? Command(string name) => Commands.GetValueOrDefault(name);`.

`ExtensionBindingCompiler`:

```csharp
    internal static ExtensionRegistrationResult Check(
        ExtensionRegistry registry, ICelCompiler compiler, IServiceProviderIsService services)
    {
        var problems = new List<ExtensionProblem>(ExtensionIdProblems.Of(registry));
        problems.AddRange(registry.Handlers.SelectMany(handler => new HandlerBinding(handler, compiler).CheckRegistration(services).Problems));
        var relational = CommandProblems.Relational(services);
        problems.AddRange(registry.Commands.SelectMany(command => new CommandBinding(command, compiler).CheckRegistration(relational).Problems));
        problems.AddRange(CommandProblems.Shared(registry));
        var refused = Refused(problems);

        return new(problems, Ties(registry.Handlers.Where(handler => !refused.Contains(handler.Id))));
    }

    internal static ExtensionBindingResult Compile(
        ExtensionRegistry registry, PolicyCatalog catalog, ICelCompiler compiler, IServiceProviderIsService services)
    {
        var problems = new List<ExtensionProblem>(ExtensionIdProblems.Of(registry));
        var bound = BindHandlers(registry, catalog, compiler, services, problems);
        var commands = BindCommands(registry, catalog, compiler, services, problems);
        var refused = Refused(problems);
        List<BoundHandler> binds = [.. bound.Where(handler => !refused.Contains(handler.Id))];
        var bindings = new ExtensionBindings(
            binds,
            registry.Handlers.Where(handler => refused.Contains(handler.Id)),
            commands.Where(command => !refused.Contains(command.Name)));

        return new(bindings, problems, Ties(binds.Select(handler => handler.Registration)));
    }

    private static List<BoundHandler> BindHandlers(
        ExtensionRegistry registry, PolicyCatalog catalog, ICelCompiler compiler, IServiceProviderIsService services, List<ExtensionProblem> problems)
    {
        var bound = new List<BoundHandler>();
        foreach (var handler in registry.Handlers)
        {
            var binding = new HandlerBinding(handler, compiler).CheckRegistration(services);
            if (binding.Bind(catalog) is { } boundHandler)
            {
                bound.Add(boundHandler);
            }

            problems.AddRange(binding.Problems);
        }

        return bound;
    }

    private static List<BoundCommand> BindCommands(
        ExtensionRegistry registry, PolicyCatalog catalog, ICelCompiler compiler, IServiceProviderIsService services, List<ExtensionProblem> problems)
    {
        var relational = CommandProblems.Relational(services);
        var bound = new List<BoundCommand>();
        foreach (var command in registry.Commands)
        {
            var binding = new CommandBinding(command, compiler).CheckRegistration(relational);
            if (binding.Bind(catalog) is { } boundCommand)
            {
                bound.Add(boundCommand);
            }

            problems.AddRange(binding.Problems);
        }

        problems.AddRange(CommandProblems.Shared(registry));
        return bound;
    }
```

`ExtensionBinder.EnsureRegistrable` and `ExtensionBinder.Binding` (the per-catalog cache every apply guard, boot and request goes through): first line `registry.Seal();` — the first binding, or the boot's first extension check, is where mapping ends (Ruling 40): a binding cached before a later map would never contain the command. `ExtensionCatalog`'s lazy item list seals too, for the same reason.

- [ ] **Step 5: The apply guard covers commands bound to an entity**

`ExtensionApplyGuard`: replace both `registry.Handlers.Count == 0` with `registry.IsEmpty`, and `BoundNow`:

```csharp
    private HashSet<string> BoundNow(PolicyCatalog? current)
    {
        if (current is null)
        {
            return new HashSet<string>(StringComparer.Ordinal);
        }

        var bindings = binder.Result(current).Bindings;
        return [.. bindings.Handlers.Select(handler => handler.Id), .. bindings.Commands.Keys];
    }
```

`ExtensionApplyRefusals.Refusals` dispatches per id, and `Generic` becomes `internal static`:

```csharp
    internal static IReadOnlyList<DescriptorValidationError> Refusals(
        ExtensionRegistry registry, IReadOnlyList<ExtensionProblem> regressions, SchemaModel current, SchemaModel proposed, ICelCompiler compiler) =>
        [.. regressions
            .GroupBy(problem => problem.Id, StringComparer.Ordinal)
            .SelectMany(refused => CommandOf(registry, refused.Key) is { } command
                ? CommandApplyRefusals.For(command, refused, current, proposed, compiler)
                : ForHandler(Registered(registry, refused.Key), refused, current, proposed, compiler))];

    private static CommandRegistration? CommandOf(ExtensionRegistry registry, string id) =>
        registry.Commands.FirstOrDefault(candidate => string.Equals(candidate.Id, id, StringComparison.Ordinal));
```

`src/MMLib.Alvo/Commands/Internal/CommandApplyRefusals.cs`:

```csharp
using MMLib.Alvo.Descriptor;
using MMLib.Alvo.Events.Internal;
using MMLib.Alvo.Expressions;
using MMLib.Alvo.Extensions.Internal;
using MMLib.Alvo.Schema;

namespace MMLib.Alvo.Commands.Internal;

/// <summary>
/// The apply guard's sentences for a command a change would unbind (spec 2026-10-14 §4.2): a pointer to the removed entity
/// or to each removed field its access rule reads, then the binder's own sentence for every problem.
/// </summary>
internal static class CommandApplyRefusals
{
    private const string FixPrefix = "Remove the command and redeploy first, or keep the ";

    internal static DescriptorValidationError[] For(
        CommandRegistration command, IEnumerable<ExtensionProblem> problems, SchemaModel current, SchemaModel proposed, ICelCompiler compiler) =>
        [.. Precise(command, current, proposed, compiler), .. problems.Select(problem => ExtensionApplyRefusals.Generic(problem, DescriptorValidationSeverity.Error))];

    private static DescriptorValidationError[] Precise(CommandRegistration command, SchemaModel current, SchemaModel proposed, ICelCompiler compiler)
    {
        if (command.Entity is not { } entity || Named(current, entity) is not { } before)
        {
            return [];
        }

        if (Named(proposed, entity) is not { } after)
        {
            return [EntityRemoved(command.Id, entity)];
        }

        return [.. FieldsRead(command.Access.Source, before, compiler)
            .Where(field => !after.Fields.Any(candidate => string.Equals(candidate.Name, field, StringComparison.Ordinal)))
            .Select(field => FieldRemoved(command.Id, entity, field))];
    }

    private static string[] FieldsRead(string access, EntitySchema before, ICelCompiler compiler)
    {
        var result = compiler.Compile(access, CelProfile.Condition, before);
        return result.IsSuccess ? [.. EventConditionReads.FieldsReadOrCompared(result.Expression!.Root).Distinct(StringComparer.Ordinal)] : [];
    }

    private static EntitySchema? Named(SchemaModel schema, string name) =>
        schema.Entities.FirstOrDefault(entity => string.Equals(entity.Name, name, StringComparison.Ordinal));

    private static DescriptorValidationError EntityRemoved(string id, string entity) => new(
        $"/entities/{entity}",
        $"Entity '{entity}' is bound by extension '{id}' ({CommandBinding.Kind}, in this host): its route reads a row of it, so without it the command could never be invoked.",
        FixPrefix + "entity.",
        DescriptorValidationSeverity.Error);

    private static DescriptorValidationError FieldRemoved(string id, string entity, string field) => new(
        $"/entities/{entity}/fields/{field}",
        $"Field '{field}' of '{entity}' is read by the access rule of extension '{id}' ({CommandBinding.Kind}, in this host): without it the rule would no longer compile, and the command would refuse every caller.",
        FixPrefix + "field.",
        DescriptorValidationSeverity.Error);
}
```

Add one guard fact to `CommandBindingTests`:

```csharp
    [Fact]
    public void Removing_an_entity_a_command_binds_is_refused_at_its_pointer()
    {
        var collection = new ServiceCollection();
        collection.AddAlvo();
        using var provider = collection.BuildServiceProvider();
        provider.GetRequiredService<CommandRegistry>().Map("resolve_ticket", "/r/{id}", "tickets", CommandAccess.Of("status == 'open'"));
        var registry = provider.GetRequiredService<ExtensionRegistry>();
        var regressions = ExtensionBindingCompiler.Compile(registry, CatalogOf(new([.. Schema.Entities.Where(entity => entity.Name != "tickets")])), CelFixtures.Compiler, EveryService()).Problems;

        var refusals = ExtensionApplyRefusals.Refusals(registry, regressions, Schema, new([.. Schema.Entities.Where(entity => entity.Name != "tickets")]), CelFixtures.Compiler);

        refusals.ShouldContain(refusal => refusal.Path == "/entities/tickets" && refusal.Message.Contains("extension 'resolve_ticket' (command, in this host)", StringComparison.Ordinal));
    }
```

(`DescriptorValidationError`'s path member is the one `ExtensionApplyRefusalsTests` reads; use the same member name.)

- [ ] **Step 6: Run the tests, then every extension suite**

Run: `dotnet test --project test/MMLib.Alvo.Tests --filter-class '*AccessTranslatorTests' --filter-class '*CommandRegistryTests' --filter-class '*CommandBindingTests' --filter-class '*CommandAccessJudgeTests' --filter-class '*ExtensionBindingCompilerTests' --filter-class '*ExtensionApplyGuard*' --filter-class '*ExtensionIdProblems*' --filter-class '*ExtensionCatalogTests'`
Expected: PASS, the existing extension suites unchanged.

- [ ] **Step 7: Format, ring0, commit**

```bash
dotnet format whitespace MMLib.Alvo.slnx --include src/MMLib.Alvo/Commands/Setup.cs src/MMLib.Alvo/Commands/Internal/CommandRegistration.cs src/MMLib.Alvo/Commands/Internal/CommandAccess.cs src/MMLib.Alvo/Commands/Internal/AccessTranslator.cs src/MMLib.Alvo/Commands/Internal/CommandRegistry.cs src/MMLib.Alvo/Commands/Internal/CommandBinding.cs src/MMLib.Alvo/Commands/Internal/CommandProblems.cs src/MMLib.Alvo/Commands/Internal/CommandApplyRefusals.cs src/MMLib.Alvo/Commands/Internal/CommandOperationIds.cs src/MMLib.Alvo/AlvoServiceCollectionExtensions.cs src/MMLib.Alvo/Extensions/Setup.cs src/MMLib.Alvo/Extensions/Internal/ExtensionIds.cs src/MMLib.Alvo/Extensions/Internal/ExtensionRegistry.cs src/MMLib.Alvo/Extensions/Internal/ExtensionIdProblems.cs src/MMLib.Alvo/Extensions/Internal/ExtensionBindings.cs src/MMLib.Alvo/Extensions/Internal/ExtensionBindingCompiler.cs src/MMLib.Alvo/Extensions/Internal/ExtensionBinder.cs src/MMLib.Alvo/Extensions/Internal/ExtensionApplyGuard.cs src/MMLib.Alvo/Extensions/Internal/ExtensionApplyRefusals.cs test/MMLib.Alvo.Tests/Commands/AccessTranslatorTests.cs test/MMLib.Alvo.Tests/Commands/CommandRegistryTests.cs test/MMLib.Alvo.Tests/Commands/CommandBindingTests.cs
scripts/test-ring0
git add src/MMLib.Alvo/Commands test/MMLib.Alvo.Tests/Commands src/MMLib.Alvo/AlvoServiceCollectionExtensions.cs src/MMLib.Alvo/Extensions/Setup.cs src/MMLib.Alvo/Extensions/Internal/ExtensionIds.cs src/MMLib.Alvo/Extensions/Internal/ExtensionRegistry.cs src/MMLib.Alvo/Extensions/Internal/ExtensionIdProblems.cs src/MMLib.Alvo/Extensions/Internal/ExtensionBindings.cs src/MMLib.Alvo/Extensions/Internal/ExtensionBindingCompiler.cs src/MMLib.Alvo/Extensions/Internal/ExtensionBinder.cs src/MMLib.Alvo/Extensions/Internal/ExtensionApplyGuard.cs src/MMLib.Alvo/Extensions/Internal/ExtensionApplyRefusals.cs
git commit -m "feat(commands): command registrations, access rules, boot and apply validation

Claude-Session: https://claude.ai/code/session_01NJAtafM29iddLwfhb5gnmB"
```

---

## Task 7: One command run — `AlvoCommandContext`, at most one unit, completion and answer

**Files:**
- Create: `src/MMLib.Alvo/Commands/AlvoCommandContext.cs`, `src/MMLib.Alvo/Commands/Internal/{CommandRun.cs,CommandRunner.cs,CommandCompletion.cs,CommandAnswers.cs}`
- Modify: `src/MMLib.Alvo/Commands/Setup.cs` (register `CommandCompletion`, `CommandRunner`), `src/MMLib.Alvo/Api/Internal/ProblemResultFactory.cs` (`IdempotencyInFlight`, `IdempotencyUnanswered`)
- Test: `test/MMLib.Alvo.Tests/Commands/{CommandRunnerTests.cs,FakeCommandLedger.cs}`
- Baseline: `test/MMLib.Alvo.Tests/PublicApi.MMLib.Alvo.verified.txt`

**Interfaces:**
- Consumes: Task 4's `ICommandLedger`, `CommandKey`, `CommandAnswer`, `CommandClaim`, `IAlvoCommandUnit`, `AlvoFrameworkEvent`; Task 6's `CommandRegistration`; HX-07's `IAlvoTransactions`, `IAlvoSessions`.
- Produces:
  - `public sealed class AlvoCommandContext : IBindableFromHttpContext<AlvoCommandContext>` — `Name`, `Caller`, `Data`, `Record`, `Task<T> TransactAsync<T>(Func<IAlvoTransaction, CancellationToken, Task<T>> work, AlvoTransactionOptions? options = null, CancellationToken cancellationToken = default)`, `Task TransactAsync(Func<IAlvoTransaction, CancellationToken, Task> work, AlvoTransactionOptions? options = null, CancellationToken cancellationToken = default)`, `static BindAsync`; internal `AlvoCommandContext(CommandRun run)`, `static AlvoCommandContext Pending(HttpContext http)`, `void Start(CommandRun run)`.
  - `internal sealed record CommandInvocation(CommandRegistration Command, AlvoContext Caller, AlvoRecord? Record, CommandKey? Key)` (`RowId`).
  - `internal sealed class CommandRun` — `Invocation`, `Name`, `Caller`, `Record`, `Data`, `Key`, `Committed`, `OpenedWithoutCommit`, `TransactAsync<T>`, `Finish()`.
  - `internal sealed class CommandRunner` — `CommandRun Start(CommandInvocation)`, `Task<CommandClaimed> ClaimAsync(CommandInvocation)` (`internal sealed record CommandClaimed(IResult? Early, CommandInvocation Invocation)`), `Task<object?> FinishAsync(CommandRun, Func<Task<object?>> handler)`.
  - `internal sealed class CommandCompletion(TimeProvider)` — `AlvoEvent EventOf(CommandInvocation)`, `Task WriteInUnitAsync(CommandInvocation, IAlvoTransaction, CancellationToken)`.
  - `internal static class CommandAnswers` — `int? StatusOf(object?)`, `CommandAnswer? Of(object?, JsonSerializerOptions)`, `CommandAnswer Failed`; `internal sealed class ReplayedAnswer(CommandAnswer) : IResult, IStatusCodeHttpResult`.
  - `ProblemResultFactory.IdempotencyInFlight()`, `ProblemResultFactory.IdempotencyUnanswered()` (409, `idempotency-conflict`).

- [ ] **Step 1: Write the failing tests**

`test/MMLib.Alvo.Tests/Commands/FakeCommandLedger.cs`:

```csharp
using MMLib.Alvo.Data;
using MMLib.Alvo.Events;
using System.Collections.Concurrent;

namespace MMLib.Alvo.Tests.Commands;

/// <summary>An in-memory <see cref="ICommandLedger"/> that records what the runner asked of it; it grants a token per claim.</summary>
internal sealed class FakeCommandLedger : ICommandLedger
{
    private readonly ConcurrentDictionary<(string, string), (string Fingerprint, bool Committed, CommandAnswer? Answer)> _rows = new();

    internal List<string> Calls { get; } = [];

    internal List<AlvoEvent> Completions { get; } = [];

    public Task<CommandClaim> ClaimAsync(CommandKey key, DateTimeOffset now, CancellationToken cancellationToken)
    {
        Calls.Add("claim");
        if (!_rows.TryGetValue((key.Scope, key.Key), out var row))
        {
            _rows[(key.Scope, key.Key)] = (key.Fingerprint, false, null);
            return Task.FromResult(CommandClaim.Granted(Guid.NewGuid().ToString("N")));
        }

        return Task.FromResult(row.Fingerprint != key.Fingerprint ? CommandClaim.Mismatch
            : row.Answer is { } answer ? CommandClaim.Answered(answer)
            : row.Committed ? CommandClaim.Unanswered
            : CommandClaim.InFlight);
    }

    public Task AnswerAsync(CommandKey key, CommandAnswer answer, CancellationToken cancellationToken)
    {
        Calls.Add("answer");
        _rows[(key.Scope, key.Key)] = _rows[(key.Scope, key.Key)] with { Answer = answer };
        return Task.CompletedTask;
    }

    public Task ReleaseAsync(CommandKey key, CancellationToken cancellationToken)
    {
        Calls.Add("release");
        _rows.TryRemove((key.Scope, key.Key), out _);
        return Task.CompletedTask;
    }

    public Task CompleteAsync(AlvoEvent completion, CommandKey? key, CommandAnswer? answer, DateTimeOffset now, CancellationToken cancellationToken)
    {
        Calls.Add("complete");
        Completions.Add(completion);
        if (key is not null)
        {
            _rows[(key.Scope, key.Key)] = (key.Fingerprint, true, answer);
        }

        return Task.CompletedTask;
    }

    public Task<int> SweepAsync(DateTimeOffset claimedBefore, CancellationToken cancellationToken) => Task.FromResult(0);

    internal void Commit(CommandKey key) => _rows[(key.Scope, key.Key)] = _rows[(key.Scope, key.Key)] with { Committed = true };
}

/// <summary>
/// A unit that only records the command's own writes into it. It implements every member of <see cref="IAlvoTransaction"/>
/// and <see cref="IAlvoSession"/> — <c>For(AlvoContext)</c> belongs to <c>IAlvoSessions</c>, not to a session (Ruling 49).
/// </summary>
internal sealed class FakeCommandUnit(FakeCommandLedger ledger, AlvoContext context) : IAlvoTransaction, IAlvoCommandUnit
{
    internal List<AlvoEvent> Published { get; } = [];

    public AlvoContext Context => context;

    public Task PublishFrameworkAsync(AlvoEvent @event, CancellationToken cancellationToken)
    {
        Published.Add(@event);
        return Task.CompletedTask;
    }

    public Task CommitCommandAsync(CommandKey key, DateTimeOffset now, CancellationToken cancellationToken)
    {
        key.Token.ShouldNotBeNull("a run commits only under the claim it was granted");
        ledger.Commit(key);
        return Task.CompletedTask;
    }

    public Task<AlvoPage> QueryAsync(AlvoQuery query, CancellationToken cancellationToken = default) => throw new NotSupportedException();

    public Task<AlvoRecord?> GetAsync(string entity, Guid id, CancellationToken cancellationToken = default) => throw new NotSupportedException();

    public Task<AlvoRecord> CreateAsync(string entity, IReadOnlyDictionary<string, object?> values, CancellationToken cancellationToken = default) =>
        Task.FromResult(new AlvoRecord(new Dictionary<string, object?>(values) { ["id"] = Guid.NewGuid() }));

    public Task<AlvoRecord> UpdateAsync(string entity, Guid id, IReadOnlyDictionary<string, object?> values, AlvoPrecondition? precondition = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();

    public Task<AlvoReplaceResult> ReplaceAsync(string entity, Guid id, IReadOnlyDictionary<string, object?> values, AlvoPrecondition? precondition = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();

    public Task DeleteAsync(string entity, Guid id, AlvoPrecondition? precondition = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();

    public Task<AlvoBatchResult> CreateManyAsync(string entity, IReadOnlyList<IReadOnlyDictionary<string, object?>> rows, CancellationToken cancellationToken = default) => throw new NotSupportedException();

    public Task<AlvoBatchResult> UpdateManyAsync(string entity, IReadOnlyList<AlvoRowPatch> rows, CancellationToken cancellationToken = default) => throw new NotSupportedException();

    public Task<AlvoBatchResult> DeleteManyAsync(string entity, IReadOnlyList<Guid> ids, CancellationToken cancellationToken = default) => throw new NotSupportedException();

    public Task PublishAsync(string type, string subject, IReadOnlyDictionary<string, object?>? data, CancellationToken cancellationToken = default) => throw new NotSupportedException();

    public IAlvoTransaction AsSystem() => throw new NotSupportedException();
}

/// <summary>Runs every delegate on one <see cref="FakeCommandUnit"/>; "commits" when it returns, or rolls back when told to.</summary>
internal sealed class FakeTransactions(FakeCommandUnit unit) : IAlvoTransactions
{
    internal bool RollBack { get; set; }

    public async Task<T> TransactAsync<T>(
        AlvoContext context, Func<IAlvoTransaction, CancellationToken, Task<T>> work, AlvoTransactionOptions? options = null, CancellationToken cancellationToken = default)
    {
        var result = await work(unit, cancellationToken);
        return RollBack ? throw new InvalidOperationException("the unit rolled back") : result;
    }
}
```

(If the compiler reports a member of `IAlvoSession` or `IAlvoTransaction` the fake does not implement — one added after this plan was written — implement it as `throw new NotSupportedException()`; the list above is the interfaces' members as `src/MMLib.Alvo.Abstractions/Data/IAlvoSession.cs` and `IAlvoTransaction.cs` declare them at `f18c51e6`.)

`test/MMLib.Alvo.Tests/Commands/CommandRunnerTests.cs`:

```csharp
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using MMLib.Alvo.Commands;
using MMLib.Alvo.Commands.Internal;
using MMLib.Alvo.Data;
using NSubstitute;
using System.Text.Json;

namespace MMLib.Alvo.Tests.Commands;

/// <summary>
/// One command run (spec 2026-10-14 §4.9, R-M7): at most one unit; the completion event and the key's record in that unit
/// when one commits, else together after a 2xx; nothing after a refusal or a rolled-back unit; a committed run is never re-run.
/// </summary>
public sealed class CommandRunnerTests
{
    private static readonly AlvoContext _staff = new() { User = UserId.New(), Roles = new HashSet<Role> { Role.Authenticated } };

    private static readonly CommandRegistration _quote =
        new("quote_stay", 1, CommandAccess.Of("true"), null, "/commands/quote", null, null);

    private readonly FakeCommandLedger _ledger = new();

    private FakeTransactions _transactions = null!;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task A_claim_hands_the_run_its_fencing_token()
    {
        var runner = Runner(out _);

        var claimed = await runner.ClaimAsync(Invocation("k0"));

        claimed.Early.ShouldBeNull();
        claimed.Invocation.Key!.Token.ShouldNotBeNullOrWhiteSpace();
    }

    [Fact]
    public async Task A_run_without_a_unit_answering_2xx_completes_with_its_event_and_its_answer()
    {
        var runner = Runner(out _);
        var run = runner.Start((await runner.ClaimAsync(Invocation("k1"))).Invocation);

        await runner.FinishAsync(run, () => Task.FromResult<object?>(TypedResults.Ok(new { total = 3 })));

        _ledger.Calls.ShouldBe(["claim", "complete"]);
        _ledger.Completions.ShouldHaveSingleItem().Type.ShouldBe("alvo.command.quote_stay.completed");
        (await runner.ClaimAsync(Invocation("k1"))).Early.ShouldBeOfType<ReplayedAnswer>().StatusCode.ShouldBe(200);
    }

    [Fact]
    public async Task A_run_without_a_unit_answering_4xx_releases_its_key_and_emits_nothing()
    {
        var runner = Runner(out _);
        var run = runner.Start((await runner.ClaimAsync(Invocation("k2"))).Invocation);

        await runner.FinishAsync(run, () => Task.FromResult<object?>(TypedResults.BadRequest()));

        _ledger.Calls.ShouldBe(["claim", "release"]);
        _ledger.Completions.ShouldBeEmpty();
    }

    [Fact]
    public async Task A_unit_carries_the_completion_and_the_commit_mark_and_the_answer_follows()
    {
        var runner = Runner(out var unit);
        var run = runner.Start((await runner.ClaimAsync(Invocation("k3"))).Invocation);
        var context = new AlvoCommandContext(run);

        await runner.FinishAsync(run, async () =>
        {
            await context.TransactAsync((tx, ct) => tx.CreateAsync("notes", new Dictionary<string, object?> { ["text"] = "x" }, ct), cancellationToken: Ct);
            return TypedResults.Created("/notes/1", new { id = 1 });
        });

        unit.Published.ShouldHaveSingleItem().Type.ShouldBe("alvo.command.quote_stay.completed");
        _ledger.Calls.ShouldBe(["claim", "answer"], "the event was in the unit; only the answer is written after it");
        (await runner.ClaimAsync(Invocation("k3"))).Early.ShouldBeOfType<ReplayedAnswer>().StatusCode.ShouldBe(201);
    }

    [Fact]
    public async Task A_committed_unit_followed_by_a_non_success_answer_keeps_its_event_and_records_the_answer()
    {
        var runner = Runner(out var unit);
        var run = runner.Start((await runner.ClaimAsync(Invocation("k4"))).Invocation);
        var context = new AlvoCommandContext(run);

        await runner.FinishAsync(run, async () =>
        {
            await context.TransactAsync((tx, ct) => tx.CreateAsync("notes", new Dictionary<string, object?> { ["text"] = "x" }, ct), cancellationToken: Ct);
            return TypedResults.Conflict();
        });

        unit.Published.ShouldHaveSingleItem();
        _ledger.Calls.ShouldNotContain("release", "a committed run is never given back to be run again");
        (await runner.ClaimAsync(Invocation("k4"))).Early.ShouldBeOfType<ReplayedAnswer>().StatusCode.ShouldBe(409);
    }

    [Fact]
    public async Task A_handler_that_catches_its_rolled_back_unit_and_answers_2xx_emits_no_completion()
    {
        var runner = Runner(out _);
        _transactions.RollBack = true;
        var run = runner.Start((await runner.ClaimAsync(Invocation("k8"))).Invocation);
        var context = new AlvoCommandContext(run);

        await runner.FinishAsync(run, async () =>
        {
            try
            {
                await context.TransactAsync((tx, ct) => tx.CreateAsync("notes", new Dictionary<string, object?> { ["text"] = "x" }, ct), cancellationToken: Ct);
            }
            catch (InvalidOperationException)
            {
            }

            return TypedResults.Ok();
        });

        _ledger.Completions.ShouldBeEmpty("its writes rolled back, so nothing completed");
        _ledger.Calls.ShouldBe(["claim", "release"]);
    }

    [Fact]
    public async Task A_handler_throwing_after_its_unit_committed_records_a_500_and_is_not_released()
    {
        var runner = Runner(out _);
        var run = runner.Start((await runner.ClaimAsync(Invocation("k5"))).Invocation);
        var context = new AlvoCommandContext(run);

        await Should.ThrowAsync<InvalidOperationException>(() => runner.FinishAsync(run, async () =>
        {
            await context.TransactAsync((tx, ct) => Task.CompletedTask, cancellationToken: Ct);
            throw new InvalidOperationException("after the commit");
        }));

        _ledger.Calls.ShouldBe(["claim", "answer"]);
        (await runner.ClaimAsync(Invocation("k5"))).Early.ShouldBeOfType<ReplayedAnswer>().StatusCode.ShouldBe(500);
    }

    [Fact]
    public async Task A_handler_throwing_before_any_unit_releases_its_key()
    {
        var runner = Runner(out _);
        var run = runner.Start((await runner.ClaimAsync(Invocation("k6"))).Invocation);

        await Should.ThrowAsync<TimeoutException>(() => runner.FinishAsync(run, () => throw new TimeoutException("the payment provider")));

        _ledger.Calls.ShouldBe(["claim", "release"]);
    }

    [Fact]
    public async Task A_second_unit_throws_and_the_first_one_stands()
    {
        var runner = Runner(out var unit);
        var run = runner.Start(new CommandInvocation(_quote, _staff, null, null));
        var context = new AlvoCommandContext(run);

        await context.TransactAsync((_, _) => Task.CompletedTask, cancellationToken: Ct);
        var refusal = await Should.ThrowAsync<InvalidOperationException>(() => context.TransactAsync((_, _) => Task.CompletedTask, cancellationToken: Ct));

        refusal.Message.ShouldContain("at most one");
        unit.Published.Count.ShouldBe(1);
    }

    [Fact]
    public async Task A_context_whose_run_answered_cannot_open_a_unit()
    {
        var runner = Runner(out _);
        var run = runner.Start(new CommandInvocation(_quote, _staff, null, null));
        await runner.FinishAsync(run, () => Task.FromResult<object?>(null));

        await Should.ThrowAsync<InvalidOperationException>(() => new AlvoCommandContext(run).TransactAsync((_, _) => Task.CompletedTask, cancellationToken: Ct));
    }

    [Fact]
    public async Task A_key_still_in_flight_is_a_409_and_a_key_spent_on_another_request_is_the_idempotency_conflict()
    {
        var runner = Runner(out _);
        await runner.ClaimAsync(Invocation("k7"));

        var inFlight = await runner.ClaimAsync(Invocation("k7"));
        var mismatch = Should.ThrowAsync<AlvoIdempotencyConflictException>(() => runner.ClaimAsync(new CommandInvocation(_quote, _staff, null, Key("k7") with { Fingerprint = "other" })));

        inFlight.Early.ShouldBeAssignableTo<IStatusCodeHttpResult>()!.StatusCode.ShouldBe(StatusCodes.Status409Conflict);
        await mismatch;
    }

    [Fact]
    public void A_plain_value_is_a_200_answer()
    {
        CommandAnswers.StatusOf(null).ShouldBe(200);
        CommandAnswers.StatusOf(new { total = 3 }).ShouldBe(200);
    }

    [Fact]
    public void A_plain_string_is_not_replayable_because_it_was_written_as_text() =>
        CommandAnswers.Of("done", JsonSerializerOptions.Web).ShouldBeNull();

    [Fact]
    public void A_result_with_no_status_is_not_replayable() =>
        CommandAnswers.Of(TypedResults.Redirect("/elsewhere"), JsonSerializerOptions.Web).ShouldBeNull();

    [Fact]
    public void An_answer_over_the_cap_is_not_replayable() =>
        CommandAnswers.Of(TypedResults.Ok(new string('x', 70_000)), JsonSerializerOptions.Web).ShouldBeNull();

    private CommandRunner Runner(out FakeCommandUnit unit)
    {
        unit = new FakeCommandUnit(_ledger, _staff);
        _transactions = new FakeTransactions(unit);
        var sessions = Substitute.For<IAlvoSessions>();
        var time = new FakeTimeProvider(new DateTimeOffset(2026, 10, 30, 9, 0, 0, TimeSpan.Zero));

        return new CommandRunner(_ledger, _transactions, sessions, new CommandCompletion(time), time, JsonSerializerOptions.Web, NullLogger<CommandRunner>.Instance);
    }

    private static CommandInvocation Invocation(string key) => new(_quote, _staff, null, Key(key));

    private static CommandKey Key(string key) => new($"command:quote_stay/global/{_staff.User.Value}/-", key, "fp");
}
```

- [ ] **Step 2: Run them to verify they fail**

Run: `dotnet test --project test/MMLib.Alvo.Tests --filter-class '*CommandRunnerTests'`
Expected: FAIL to compile.

- [ ] **Step 3: The public context**

`src/MMLib.Alvo/Commands/AlvoCommandContext.cs`:

```csharp
using Microsoft.AspNetCore.Http;
using MMLib.Alvo.Commands.Internal;
using MMLib.Alvo.Data;
using System.Reflection;

namespace MMLib.Alvo.Commands;

/// <summary>
/// What a command's handler is given: who invoked it, the row it is bound to, a data session as that caller, and the one
/// transaction unit it may open.
/// </summary>
/// <remarks>
/// <para>
/// <b>The handler is not in a transaction.</b> Make an external call — a payment provider, a VAT registry — first, through
/// your own services, then write everything that must commit together in one <see cref="TransactAsync{T}"/>. Inside it, use
/// the unit it hands you: <c>IAlvoData</c> and <see cref="Data"/> are refused while it is open.
/// </para>
/// <para>
/// <b>At most one unit per run.</b> The command's completion event (<c>alvo.command.&lt;name&gt;.completed</c>) and its
/// <c>Idempotency-Key</c> record are written inside that unit as it commits, so they exist exactly when its writes do. A run
/// that opens no unit gets both written after it answers 2xx.
/// </para>
/// <para>
/// Take it as a parameter of a handler mapped with <c>MapAlvoCommand</c>; an internal command receives it in
/// <see cref="IAlvoCommand{TInput}.HandleAsync"/>. On any other endpoint its members throw.
/// </para>
/// </remarks>
public sealed class AlvoCommandContext : IBindableFromHttpContext<AlvoCommandContext>
{
    private static readonly object _slot = new();
    private CommandRun? _run;

    private AlvoCommandContext()
    {
    }

    internal AlvoCommandContext(CommandRun run) => _run = run;

    /// <summary>Gets the command's name.</summary>
    public string Name => Run.Name;

    /// <summary>Gets the caller the command runs as — its access rule admitted them.</summary>
    public AlvoContext Caller => Run.Caller;

    /// <summary>Gets a data session as <see cref="Caller"/>; each call commits on its own, and it is refused inside the unit.</summary>
    public IAlvoSession Data => Run.Data;

    /// <summary>
    /// Gets the row a command bound to an entity was invoked on, as the caller may read it; null for a command bound to none.
    /// </summary>
    public AlvoRecord? Record => Run.Record;

    /// <summary>Runs <paramref name="work"/> in the command's one transaction unit, as <see cref="Caller"/>.</summary>
    /// <typeparam name="T">What the work answers.</typeparam>
    /// <param name="work">The writes that commit together; it receives the unit and a token.</param>
    /// <param name="options">The unit's options; its time limit is 30 seconds unless set.</param>
    /// <param name="cancellationToken">A token to cancel the unit.</param>
    /// <returns>What <paramref name="work"/> answered, once the unit has committed.</returns>
    /// <exception cref="InvalidOperationException">This run already opened its unit, or has already answered.</exception>
    public Task<T> TransactAsync<T>(
        Func<IAlvoTransaction, CancellationToken, Task<T>> work,
        AlvoTransactionOptions? options = null,
        CancellationToken cancellationToken = default) =>
        Run.TransactAsync(work, options, cancellationToken);

    /// <summary>Runs <paramref name="work"/> in the command's one transaction unit, as <see cref="Caller"/>.</summary>
    /// <param name="work">The writes that commit together.</param>
    /// <param name="options">The unit's options.</param>
    /// <param name="cancellationToken">A token to cancel the unit.</param>
    /// <returns>A task that completes once the unit has committed.</returns>
    /// <exception cref="InvalidOperationException">This run already opened its unit, or has already answered.</exception>
    public Task TransactAsync(
        Func<IAlvoTransaction, CancellationToken, Task> work,
        AlvoTransactionOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(work);

        return Run.TransactAsync(
            async (unit, ct) =>
            {
                await work(unit, ct).ConfigureAwait(false);
                return true;
            },
            options,
            cancellationToken);
    }

    /// <summary>Binds the request's command context; <c>MapAlvoCommand</c>'s filter starts it before the handler runs.</summary>
    /// <param name="context">The request.</param>
    /// <param name="parameter">The handler parameter being bound.</param>
    /// <returns>The request's command context.</returns>
    public static ValueTask<AlvoCommandContext?> BindAsync(HttpContext context, ParameterInfo parameter)
    {
        ArgumentNullException.ThrowIfNull(context);

        return ValueTask.FromResult<AlvoCommandContext?>(Pending(context));
    }

    /// <summary>The request's one command context, created unstarted on first use.</summary>
    internal static AlvoCommandContext Pending(HttpContext http)
    {
        if (http.Items.TryGetValue(_slot, out var existing) && existing is AlvoCommandContext context)
        {
            return context;
        }

        var created = new AlvoCommandContext();
        http.Items[_slot] = created;
        return created;
    }

    /// <summary>Starts the context with its run; a context starts once.</summary>
    internal void Start(CommandRun run) =>
        _run = _run is null ? run : throw new InvalidOperationException("This command context has already started.");

    private CommandRun Run => _run ?? throw new InvalidOperationException(
        "This AlvoCommandContext was bound on an endpoint MapAlvoCommand did not map, so no caller was resolved and no access "
        + "rule ran. Map the endpoint with MapAlvoCommand, or take an AlvoCaller on a plain endpoint with .WithAlvoCaller().");
}
```

- [ ] **Step 4: The run, its completion and its answer**

`src/MMLib.Alvo/Commands/Internal/CommandRun.cs`:

```csharp
using MMLib.Alvo.Data;

namespace MMLib.Alvo.Commands.Internal;

/// <summary>What a command is invoked with: the command, the caller, the bound row and the key.</summary>
internal sealed record CommandInvocation(CommandRegistration Command, AlvoContext Caller, AlvoRecord? Record, CommandKey? Key)
{
    /// <summary>Gets the bound row's id, when there is a row.</summary>
    internal Guid? RowId => Record?[AlvoManagedColumns.Id] as Guid?;
}

/// <summary>
/// One run of a command: its at-most-one unit, and whether that unit committed. The handler is not wrapped in a transaction
/// (R-M7); the unit is the handler's to open.
/// </summary>
internal sealed class CommandRun(
    CommandInvocation invocation, IAlvoSession data, IAlvoTransactions? transactions, CommandCompletion completion)
{
    private int _opened;
    private int _finished;
    private int _committed;

    internal CommandInvocation Invocation => invocation;

    internal string Name => invocation.Command.Id;

    internal AlvoContext Caller => invocation.Caller;

    internal AlvoRecord? Record => invocation.Record;

    internal IAlvoSession Data => data;

    internal CommandKey? Key => invocation.Key;

    /// <summary>Gets a value indicating whether the run's unit committed — its event and key mark with it.</summary>
    internal bool Committed => Volatile.Read(ref _committed) == 1;

    /// <summary>
    /// Gets a value indicating whether the run opened its unit and the unit did not commit — its writes rolled back, so
    /// whatever the handler answers afterwards, nothing completed (Ruling 43).
    /// </summary>
    internal bool OpenedWithoutCommit => Volatile.Read(ref _opened) == 1 && !Committed;

    /// <summary>Opens the run's one unit, writing the completion and the key's commit mark last inside it.</summary>
    internal async Task<T> TransactAsync<T>(
        Func<IAlvoTransaction, CancellationToken, Task<T>> work, AlvoTransactionOptions? options, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(work);
        EnsureFirstUnit();
        var units = transactions ?? throw new InvalidOperationException(
            "This host's data provider opens no transaction units (IAlvoTransactions), so a command cannot open one. Register UseSqlite(…) or UsePostgreSql(…).");

        var result = await units.TransactAsync(Caller, (unit, ct) => WorkedAsync(unit, work, ct), options, cancellationToken).ConfigureAwait(false);
        Interlocked.Exchange(ref _committed, 1);
        return result;
    }

    /// <summary>Ends the run: no unit may be opened after the handler answered.</summary>
    internal void Finish() => Interlocked.Exchange(ref _finished, 1);

    private async Task<T> WorkedAsync<T>(IAlvoTransaction unit, Func<IAlvoTransaction, CancellationToken, Task<T>> work, CancellationToken ct)
    {
        var result = await work(unit, ct).ConfigureAwait(false);
        await completion.WriteInUnitAsync(invocation, unit, ct).ConfigureAwait(false);
        return result;
    }

    private void EnsureFirstUnit()
    {
        if (Volatile.Read(ref _finished) == 1)
        {
            throw new InvalidOperationException($"The command '{Name}' has already answered; its context can no longer open a transaction unit.");
        }

        if (Interlocked.Exchange(ref _opened, 1) == 1)
        {
            throw new InvalidOperationException(
                $"The command '{Name}' already opened its transaction unit. A command opens at most one: put every write that must commit together into that one delegate, and make an external call before it.");
        }
    }
}
```

`src/MMLib.Alvo/Commands/Internal/CommandCompletion.cs`:

```csharp
using MMLib.Alvo.Data;
using MMLib.Alvo.Events;

namespace MMLib.Alvo.Commands.Internal;

/// <summary>
/// A command's completion event: <c>alvo.command.&lt;name&gt;.completed</c>, about the bound row or the command, carrying
/// only its name and the row it acted on — never its input or its answer (Ruling 10).
/// </summary>
internal sealed class CommandCompletion(TimeProvider time)
{
    /// <summary>The completion event of <paramref name="invocation"/>, minted now as its caller.</summary>
    internal AlvoEvent EventOf(CommandInvocation invocation) => AlvoFrameworkEvent.Create(
        invocation.Command.EventType, invocation.Command.Subject(invocation.RowId), Payload(invocation), invocation.Caller, time.GetUtcNow());

    /// <summary>Writes the completion and the key's commit mark into <paramref name="unit"/>, as its last calls.</summary>
    /// <exception cref="InvalidOperationException">The unit is not one of Alvo's own.</exception>
    internal async Task WriteInUnitAsync(CommandInvocation invocation, IAlvoTransaction unit, CancellationToken ct)
    {
        if (unit is not IAlvoCommandUnit framework)
        {
            throw new InvalidOperationException(
                "A command's unit must come from Alvo's own IAlvoTransactions: this one cannot carry the command's completion event and idempotency record.");
        }

        await framework.PublishFrameworkAsync(EventOf(invocation), ct).ConfigureAwait(false);
        if (invocation.Key is { } key)
        {
            await framework.CommitCommandAsync(key, time.GetUtcNow(), ct).ConfigureAwait(false);
        }
    }

    private static Dictionary<string, object?> Payload(CommandInvocation invocation)
    {
        var payload = new Dictionary<string, object?>(StringComparer.Ordinal) { ["command"] = invocation.Command.Id };
        if (invocation.Command.Entity is { } entity && invocation.RowId is { } id)
        {
            payload["entity"] = entity;
            payload["id"] = id;
        }

        return payload;
    }
}
```

`src/MMLib.Alvo/Commands/Internal/CommandAnswers.cs`:

```csharp
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using MMLib.Alvo.Data;
using System.Text;
using System.Text.Json;

namespace MMLib.Alvo.Commands.Internal;

/// <summary>
/// What a handler's result is as a replayable answer (Ruling 9): its status, and its value as JSON. A result that carries no
/// status — a redirect, a file, a stream — a plain <see cref="string"/> (minimal APIs write it as <c>text/plain</c>, a replay
/// would answer JSON), or a body past the cap is not replayable. Headers — a <c>Created</c>'s <c>Location</c> among them —
/// are not replayed (Ruling 49).
/// </summary>
internal static class CommandAnswers
{
    /// <summary>Gets the answer recorded for a handler that threw after its unit committed.</summary>
    internal static CommandAnswer Failed { get; } = new(StatusCodes.Status500InternalServerError, null);

    /// <summary>The status a result answers with, or null when it does not say.</summary>
    internal static int? StatusOf(object? result) => result switch
    {
        null or EmptyHttpResult => StatusCodes.Status200OK,
        IStatusCodeHttpResult coded => coded.StatusCode ?? StatusCodes.Status200OK,
        IResult or string => null,
        _ => StatusCodes.Status200OK,
    };

    /// <summary>Gets a value indicating whether the result is a success: a 2xx, or a result whose status is not known.</summary>
    internal static bool IsSuccess(object? result) => StatusOf(result) is not { } status || status is >= 200 and < 300;

    /// <summary>The replayable answer of <paramref name="result"/>, or null when it cannot be replayed.</summary>
    internal static CommandAnswer? Of(object? result, JsonSerializerOptions json)
    {
        if (StatusOf(result) is not { } status)
        {
            return null;
        }

        var value = result is IResult ? (result as IValueHttpResult)?.Value : result;
        if (value is null)
        {
            return new CommandAnswer(status, null);
        }

        var body = JsonSerializer.Serialize(value, value.GetType(), json);
        return Encoding.UTF8.GetByteCount(body) <= CommandLedgerLimits.MaxAnswerBytes ? new CommandAnswer(status, body) : null;
    }
}

/// <summary>A recorded answer, replayed: its status and its JSON body, nothing else.</summary>
internal sealed class ReplayedAnswer(CommandAnswer answer) : IResult, IStatusCodeHttpResult
{
    /// <inheritdoc/>
    public int? StatusCode => answer.Status;

    /// <inheritdoc/>
    public async Task ExecuteAsync(HttpContext httpContext)
    {
        ArgumentNullException.ThrowIfNull(httpContext);
        httpContext.Response.StatusCode = answer.Status;
        if (answer.Body is { } body)
        {
            httpContext.Response.ContentType = "application/json; charset=utf-8";
            await httpContext.Response.WriteAsync(body, httpContext.RequestAborted).ConfigureAwait(false);
        }
    }
}
```

`src/MMLib.Alvo/Commands/Internal/CommandRunner.cs`:

```csharp
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using MMLib.Alvo.Api.Internal;
using MMLib.Alvo.Data;
using System.Text.Json;

namespace MMLib.Alvo.Commands.Internal;

/// <summary>What a claim decided: the answer to give instead of running, or the invocation — with its fencing token — to run.</summary>
internal sealed record CommandClaimed(IResult? Early, CommandInvocation Invocation);

/// <summary>
/// Claims a command's key, starts its run, and finishes it: the bookkeeping after the handler is not cancellable — the
/// handler's effects already happened, so their record must not be lost to a client that hung up.
/// </summary>
internal sealed partial class CommandRunner(
    ICommandLedger? ledger,
    IAlvoTransactions? transactions,
    IAlvoSessions sessions,
    CommandCompletion completion,
    TimeProvider time,
    JsonSerializerOptions json,
    ILogger<CommandRunner> logger)
{
    private ICommandLedger Ledger => ledger ?? throw new InvalidOperationException(
        "This composition keeps no command ledger, so commands cannot run; the boot refuses this composition.");

    /// <summary>Starts a run as the invocation's caller.</summary>
    internal CommandRun Start(CommandInvocation invocation) =>
        new(invocation, sessions.For(invocation.Caller), transactions, completion);

    /// <summary>
    /// Claims the invocation's key — not cancellable: a client that hung up mid-claim must not strand the key (Ruling 49) —
    /// and answers the response to give instead of running, or the invocation carrying its fencing token.
    /// </summary>
    /// <exception cref="AlvoIdempotencyConflictException">The key was spent on a different request.</exception>
    internal async Task<CommandClaimed> ClaimAsync(CommandInvocation invocation)
    {
        if (invocation.Key is not { } key)
        {
            return new CommandClaimed(null, invocation);
        }

        var claim = await Ledger.ClaimAsync(key, time.GetUtcNow(), CancellationToken.None).ConfigureAwait(false);
        return claim.State switch
        {
            CommandClaimState.Claimed => new CommandClaimed(null, invocation with { Key = key with { Token = claim.Token } }),
            CommandClaimState.Answered => new CommandClaimed(new ReplayedAnswer(claim.Answer!), invocation),
            CommandClaimState.InFlight => new CommandClaimed(ProblemResultFactory.IdempotencyInFlight(), invocation),
            CommandClaimState.Unanswered => new CommandClaimed(ProblemResultFactory.IdempotencyUnanswered(), invocation),
            _ => throw new AlvoIdempotencyConflictException(),
        };
    }

    /// <summary>Runs the handler and records what followed from it.</summary>
    internal async Task<object?> FinishAsync(CommandRun run, Func<Task<object?>> handler)
    {
        ArgumentNullException.ThrowIfNull(run);
        ArgumentNullException.ThrowIfNull(handler);
        object? result;
        try
        {
            result = await handler().ConfigureAwait(false);
        }
        catch
        {
            await FailedAsync(run).ConfigureAwait(false);
            throw;
        }

        await AnsweredAsync(run, result).ConfigureAwait(false);
        return result;
    }

    private async Task FailedAsync(CommandRun run)
    {
        run.Finish();
        if (run.Key is not { } key)
        {
            return;
        }

        await (run.Committed
            ? Ledger.AnswerAsync(key, CommandAnswers.Failed, CancellationToken.None)
            : Ledger.ReleaseAsync(key, CancellationToken.None)).ConfigureAwait(false);
    }

    private async Task AnsweredAsync(CommandRun run, object? result)
    {
        run.Finish();
        var answer = CommandAnswers.Of(result, json);
        if (answer is null && run.Key is not null)
        {
            ResultNotReplayable(logger, run.Name);
        }

        if (run.Committed)
        {
            await AnswerCommittedAsync(run, answer).ConfigureAwait(false);
            return;
        }

        if (run.OpenedWithoutCommit)
        {
            UnitRolledBack(logger, run.Name);
            await ReleaseAsync(run).ConfigureAwait(false);
            return;
        }

        await (CommandAnswers.IsSuccess(result)
            ? Ledger.CompleteAsync(completion.EventOf(run.Invocation), run.Key, answer, time.GetUtcNow(), CancellationToken.None)
            : ReleaseAsync(run)).ConfigureAwait(false);
    }

    private Task AnswerCommittedAsync(CommandRun run, CommandAnswer? answer) =>
        run.Key is { } key && answer is not null ? Ledger.AnswerAsync(key, answer, CancellationToken.None) : Task.CompletedTask;

    private Task ReleaseAsync(CommandRun run) =>
        run.Key is { } key ? Ledger.ReleaseAsync(key, CancellationToken.None) : Task.CompletedTask;

    [LoggerMessage(Level = LogLevel.Warning, Message = "Command '{Command}' answered after its transaction unit rolled back; no completion is recorded and its Idempotency-Key is given back.")]
    private static partial void UnitRolledBack(ILogger logger, string command);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Command '{Command}' answered with a result that cannot be replayed (no status, or a body over 64 KiB); a retry with its Idempotency-Key will be refused, not answered.")]
    private static partial void ResultNotReplayable(ILogger logger, string command);
}
```

In `ProblemResultFactory`, beside `ManagementIdempotencyConflict`:

```csharp
    /// <summary>A command request whose key another request with the same key is still running under.</summary>
    internal static IResult IdempotencyInFlight() => Problem(
        StatusCodes.Status409Conflict,
        AlvoProblemTypes.IdempotencyConflict,
        "A request with this 'Idempotency-Key' is still being processed. Retry it once that one has answered: the retry is then "
        + "answered with its result.");

    /// <summary>A command request whose key's run committed and never recorded its answer.</summary>
    internal static IResult IdempotencyUnanswered() => Problem(
        StatusCodes.Status409Conflict,
        AlvoProblemTypes.IdempotencyConflict,
        "A request with this 'Idempotency-Key' already ran and committed its changes, but its answer could not be recorded. Do "
        + "not resend it: read what it changed instead.");
```

In `Commands/Setup.cs`, `AddAlvoCommands`:

```csharp
        services.TryAddSingleton<CommandRegistry>();
        services.TryAddSingleton(provider => new CommandCompletion(provider.GetService<TimeProvider>() ?? TimeProvider.System));
        services.TryAddSingleton(provider => new CommandRunner(
            provider.GetService<ICommandLedger>(),
            provider.GetService<IAlvoTransactions>(),
            provider.GetRequiredService<IAlvoSessions>(),
            provider.GetRequiredService<CommandCompletion>(),
            provider.GetService<TimeProvider>() ?? TimeProvider.System,
            provider.GetRequiredService<IOptions<JsonOptions>>().Value.SerializerOptions,
            provider.GetRequiredService<ILogger<CommandRunner>>()));
```

(`JsonOptions` is `Microsoft.AspNetCore.Http.Json.JsonOptions` — the host's minimal-API JSON options, so a replay serialises as the original response did.)

- [ ] **Step 5: Run the tests, accept the baseline**

Run: `dotnet test --project test/MMLib.Alvo.Tests --filter-class '*CommandRunnerTests'`
Expected: PASS. The core baseline adds exactly `AlvoCommandContext` (`Name`, `Caller`, `Data`, `Record`, two `TransactAsync`, `BindAsync`, the interface); accept, dispatch `alvo-snapshot-judge`.

- [ ] **Step 6: Format, ring0, commit**

```bash
dotnet format whitespace MMLib.Alvo.slnx --include src/MMLib.Alvo/Commands/AlvoCommandContext.cs src/MMLib.Alvo/Commands/Internal/CommandRun.cs src/MMLib.Alvo/Commands/Internal/CommandRunner.cs src/MMLib.Alvo/Commands/Internal/CommandCompletion.cs src/MMLib.Alvo/Commands/Internal/CommandAnswers.cs src/MMLib.Alvo/Commands/Setup.cs src/MMLib.Alvo/Api/Internal/ProblemResultFactory.cs test/MMLib.Alvo.Tests/Commands/CommandRunnerTests.cs test/MMLib.Alvo.Tests/Commands/FakeCommandLedger.cs
scripts/test-ring0
git add src/MMLib.Alvo/Commands/AlvoCommandContext.cs src/MMLib.Alvo/Commands/Internal/CommandRun.cs src/MMLib.Alvo/Commands/Internal/CommandRunner.cs src/MMLib.Alvo/Commands/Internal/CommandCompletion.cs src/MMLib.Alvo/Commands/Internal/CommandAnswers.cs src/MMLib.Alvo/Commands/Setup.cs src/MMLib.Alvo/Api/Internal/ProblemResultFactory.cs test/MMLib.Alvo.Tests/Commands/CommandRunnerTests.cs test/MMLib.Alvo.Tests/Commands/FakeCommandLedger.cs test/MMLib.Alvo.Tests/PublicApi.MMLib.Alvo.verified.txt
git commit -m "feat(commands): a command run with at most one unit, its completion and its answer

Claude-Session: https://claude.ai/code/session_01NJAtafM29iddLwfhb5gnmB"
```

---

## Task 8: `MapAlvoCommand` over HTTP — on both engines **[slice end]**

**Files:**
- Create: `src/MMLib.Alvo/Api/AlvoCommandEndpointRouteBuilderExtensions.cs`, `src/MMLib.Alvo/Commands/Internal/{CommandEndpointFilter.cs,CommandEndpointMetadata.cs,CommandInputs.cs,CommandKeys.cs,CommandGateCheck.cs}`; `test/MMLib.Alvo.Tests/Commands/CommandKeysTests.cs`
- Modify: `src/MMLib.Alvo/Commands/Setup.cs` (`CommandEndpointFilter`), `src/MMLib.Alvo/Commands/Internal/CommandRunner.cs` (`ReleaseAsync`), `src/MMLib.Alvo/Commands/Internal/CommandOperationIds.cs` (`Tag`), `src/MMLib.Alvo/Api/Internal/ProblemResultFactory.cs` (`PortRefusalFor`, `RefusalFor`), `src/MMLib.Alvo/Api/Internal/AlvoExceptionHandler.cs` (`FailedInsideAlvo`), `src/MMLib.Alvo/Api/Internal/CallerGate.cs` (`alsoRead`), `src/MMLib.Alvo/Migrations/Internal/AlvoBootService.cs` and `src/MMLib.Alvo/AlvoServiceCollectionExtensions.cs` (`CommandGateCheck`), `test/_shared/api/AlvoApiWorld.cs` (`ConfigureAlvo`), `test/MMLib.Alvo.Api.Tests/GateWitnessTests.cs` (two command facts)
- Test: `test/_shared/api/descriptors/command-desk.alvo.json`, `test/_shared/api/CommandWorld.cs`, `test/_shared/api/CommandEngineFacts.cs`, `test/MMLib.Alvo.Api.Tests/Commands/SqliteCommandTests.cs`, `test/MMLib.Alvo.Api.Tests.Integration/PostgresCommandTests.cs`
- Baseline: `test/MMLib.Alvo.Tests/PublicApi.MMLib.Alvo.verified.txt`

**Interfaces:**
- Consumes: Task 3's `CallerGate`, `AlvoCaller`; Task 6's `CommandRegistry`, `CommandAccess`, `CommandOperationIds`, `ExtensionBindings.Command`; Task 7's `CommandRunner`, `CommandRun`, `AlvoCommandContext`; `NoStoreResponseFilter`, `CallerResolution.PublishingAsync`, `IPolicyCatalogProvider.Current`, `IPredicateEvaluator`, `AlvoChainDepthLimit.EnsureWithin`, `TypedEntity.NameOf<T>()`.
- Produces:
  - Public: `AlvoCommandEndpointRouteBuilderExtensions.MapAlvoCommand(this IEndpointRouteBuilder, string name, string pattern, string access, Delegate handler)`, `(…, Expression<Func<AlvoConditionCaller, bool>> access, Delegate handler)`, `(…, string name, string pattern, string entity, string access, Delegate handler)`, `MapAlvoCommand<TEntity>(…, string access, Delegate handler)`, `MapAlvoCommand<TEntity>(…, Expression<Func<TEntity, AlvoConditionCaller, bool>> access, Delegate handler)` — all `RouteHandlerBuilder`.
  - `internal sealed record CommandEndpointMetadata(string Name, string? Entity)`; `internal sealed class CommandEndpointFilter` (`For(CommandRegistration, EndpointFilterFactoryContext, EndpointFilterDelegate)`); `internal sealed class CommandInputs` (`Of(MethodInfo, IServiceProviderIsService)`, `Fingerprint(...)`); `internal static class CommandKeys` (`Header`, `NoCredential`, `TryRead(request, command, caller, string? keyId, fingerprint, out key)`, `ScopeOf(command, caller, string? keyId)`); `internal sealed class CommandGateCheck` (`EnsureGated()`); `CallerGate.AdmitAsync(…, bool alsoRead = false)`; `CommandRunner.ReleaseAsync(CommandKey?)`; `CommandOperationIds.Tag = "commands"`; `ProblemResultFactory.PortRefusalFor(Exception)`, `RefusalFor(Exception)`.
  - Test kit: `AlvoApiWorldSetup.ConfigureAlvo` (`Action<IAlvoBuilder>?`), `CommandWorld.StartAsync(AlvoApiEngine, CommandProbe, Action<IAlvoBuilder>?)`, `CommandProbe`, `PaymentGateway`, the keys `CommandWorld.Staff/Guest/OtherStaff/ReadOnlyStaff`.

- [ ] **Step 1: The world, and the failing facts**

In `AlvoApiWorld.cs`, append `Action<IAlvoBuilder>? ConfigureAlvo = null` to `AlvoApiWorldSetup`, and in `BuildApp`'s `AddAlvo` callback, after `.AddDataApi(…)`: `setup.ConfigureAlvo?.Invoke(alvo);`.

`test/_shared/api/descriptors/command-desk.alvo.json`:

```json
{
  "$schema": "https://alvo.dev/schema/v1/project.json",
  "apiVersion": "alvo.dev/v1",
  "name": "command-desk",
  "description": "Bookings a guest makes and staff confirm, for the command facts: a guest reads only their own booking, and a command writes a note beside the booking it confirms.",
  "auth": { "providers": ["local"], "roles": ["guest", "staff"] },
  "entities": {
    "bookings": {
      "fields": {
        "guest_id": { "type": "uuid", "required": true },
        "room": { "type": "string", "required": true, "maxLength": 40 },
        "status": { "type": "enum", "required": true, "values": ["requested", "confirmed", "cancelled"] },
        "nights": { "type": "integer", "required": true }
      },
      "rules": {
        "list": "'staff' in @user.roles || guest_id == @user.id",
        "get": "'staff' in @user.roles || guest_id == @user.id",
        "create": "'staff' in @user.roles || guest_id == @user.id",
        "update": "'staff' in @user.roles"
      }
    },
    "booking_notes": {
      "fields": {
        "booking_id": { "type": "uuid", "required": true },
        "text": { "type": "string", "required": true, "maxLength": 200 }
      },
      "rules": {
        "list": "'staff' in @user.roles",
        "get": "'staff' in @user.roles",
        "create": "'staff' in @user.roles"
      }
    }
  }
}
```

`test/_shared/api/CommandWorld.cs`:

```csharp
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using MMLib.Alvo.Commands;
using MMLib.Alvo.Data;
using MMLib.Alvo.Events;
using System.Collections.Concurrent;
using System.Net;
using System.Text.Json.Nodes;

namespace MMLib.Alvo.Api.Tests;

/// <summary>What the command facts' handlers saw and did.</summary>
internal sealed class CommandProbe
{
    private int _runs;

    internal int Runs => Volatile.Read(ref _runs);

    internal ConcurrentQueue<string> Completions { get; } = new();

    internal bool? UnitOpenDuringExternalCall { get; set; }

    internal Exception? Refusal { get; set; }

    internal TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    internal TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    internal void Ran() => Interlocked.Increment(ref _runs);
}

/// <summary>A stand-in for a payment provider: an external call, made before the command's unit.</summary>
internal sealed class PaymentGateway(CommandProbe probe, IAlvoData data)
{
    internal async Task ChargeAsync(Guid booking, AlvoContext caller, CancellationToken ct)
    {
        probe.UnitOpenDuringExternalCall = AlvoFlow.Unit is not null;
        _ = await data.GetAsync("bookings", booking, caller, ct);
    }
}

/// <summary>A stay quote's input and answer.</summary>
public sealed record StayRequest(int Nights);

/// <summary>A stay quote's answer.</summary>
public sealed record Quote(decimal Total);

/// <summary>
/// The command-desk world: its descriptor, its keys and its commands. It maps Alvo's problem details, because a TestServer
/// rethrows an unhandled exception to the client instead of answering 500.
/// </summary>
internal static class CommandWorld
{
    internal static readonly TestApiKey Staff = new("staff-key", ["staff", "authenticated"], ["*:read", "*:write"]);
    internal static readonly TestApiKey OtherStaff = new("other-staff-key", ["staff", "authenticated"], ["*:read", "*:write"]);
    internal static readonly TestApiKey Guest = new("guest-key", ["guest", "authenticated"], ["*:read", "*:write"]);
    internal static readonly TestApiKey ReadOnlyStaff = new("read-only-staff-key", ["staff", "authenticated"], ["bookings:read"]);
    internal static readonly TestApiKey WriteOnlyStaff = new("write-only-staff-key", ["staff", "authenticated"], ["bookings:write"]);
    internal static readonly TestApiKey StaffAsGuest = new("staff-as-guest-key", ["guest", "authenticated"], ["*:read", "*:write"]) { User = Staff.User };
    internal const string BlockedTenant = "0b0e0c2a-1b1b-4c4c-8d8d-9e9e9e9e9e9e";

    internal static Task<AlvoApiWorld> StartAsync(AlvoApiEngine engine, CommandProbe probe, Action<IAlvoBuilder>? alvo = null) =>
        AlvoApiWorld.FromDescriptorAsync("command-desk.alvo.json", [Staff, OtherStaff, Guest, ReadOnlyStaff, WriteOnlyStaff, StaffAsGuest], new AlvoApiWorldSetup(
            MapAlvoProblemDetails: true,
            ConfigureServices: services =>
            {
                services.AddSingleton(probe);
                services.AddSingleton<PaymentGateway>();
                services.Configure<AlvoEventOptions>(options => options.DispatchMode = AlvoEventDispatchMode.Manual);
            },
            ConfigureAlvo: builder =>
            {
                Record(builder, probe, "record-confirm", "confirm_booking");
                Record(builder, probe, "record-quote", "quote_stay");
                Record(builder, probe, "record-fail-in", "misuse.fail_in_unit");
                Record(builder, probe, "record-fail-after", "misuse.fail_after_unit");
                alvo?.Invoke(builder);
            },
            ConfigureApp: Map),
            engine);

    internal static async Task<Guid> BookAsync(AlvoApiWorld world, Guid guest, string status = "requested")
    {
        using var created = await world.SendAsync(HttpMethod.Post, "/api/bookings", Staff, body: new JsonObject
        {
            ["guest_id"] = guest,
            ["room"] = "12",
            ["status"] = status,
            ["nights"] = 2,
        });
        created.StatusCode.ShouldBe(HttpStatusCode.Created);
        return (await created.ReadJsonObjectAsync())["id"]!.GetValue<Guid>();
    }

    internal static async Task<int> NotesAsync(AlvoApiWorld world)
    {
        using var notes = await world.SendAsync(HttpMethod.Get, "/api/booking_notes", Staff);
        return (await notes.ReadItemsAsync()).Count;
    }

    internal static KeyValuePair<string, string>[] Key(string key) => [new("Idempotency-Key", key)];

    private static void Record(IAlvoBuilder builder, CommandProbe probe, string id, string command) =>
        builder.AddEventHandler(id, $"alvo.command.{command}.completed", (AlvoRecord payload) =>
        {
            probe.Completions.Enqueue((string)payload["command"]!);
            return Task.CompletedTask;
        });

    private static void Map(WebApplication app)
    {
        app.MapAlvoCommand("confirm_booking", "/commands/bookings/{id:guid}/confirm", "bookings", "'staff' in @user.roles && status == 'requested'", ConfirmAsync);
        app.MapAlvoCommand("quote_stay", "/commands/quote", caller => caller.HasRole("guest") || caller.HasRole("staff"), QuoteStay);
        app.MapAlvoCommand("hold.quote", "/commands/hold", "'staff' in @user.roles", HoldAsync);
        app.MapAlvoCommand("guarded.not_blocked", "/commands/guarded", $"!(string(@tenant.id) == '{BlockedTenant}')", QuoteStay);
        app.MapAlvoCommand("misuse.data_port", "/commands/misuse/data-port", "'staff' in @user.roles", DataPortInsideUnitAsync);
        app.MapAlvoCommand("misuse.two_units", "/commands/misuse/two-units", "'staff' in @user.roles", TwoUnitsAsync);
        app.MapAlvoCommand("misuse.fail_in_unit", "/commands/misuse/fail-in-unit", "'staff' in @user.roles", FailInUnitAsync);
        app.MapAlvoCommand("misuse.fail_after_unit", "/commands/misuse/fail-after-unit", "'staff' in @user.roles", FailAfterUnitAsync);
    }

    private static async Task<IResult> ConfirmAsync(Guid id, AlvoCommandContext ctx, PaymentGateway payments, CommandProbe probe, CancellationToken ct)
    {
        probe.Ran();
        await payments.ChargeAsync(id, ctx.Caller, ct);
        var note = await ctx.TransactAsync(async (unit, token) =>
        {
            await unit.UpdateAsync("bookings", id, new Dictionary<string, object?> { ["status"] = "confirmed" }, cancellationToken: token);
            return await unit.CreateAsync("booking_notes", new Dictionary<string, object?> { ["booking_id"] = id, ["text"] = "confirmed" }, token);
        }, cancellationToken: ct);

        return TypedResults.Ok(new { booking = id, note = (Guid)note["id"]! });
    }

    private static IResult QuoteStay(StayRequest request, CommandProbe probe)
    {
        probe.Ran();
        return TypedResults.Ok(new Quote(request.Nights * 80m));
    }

    private static async Task<IResult> HoldAsync(StayRequest request, CommandProbe probe, CancellationToken ct)
    {
        probe.Ran();
        probe.Started.TrySetResult();
        await probe.Release.Task.WaitAsync(ct);
        return TypedResults.Ok(new Quote(request.Nights));
    }

    private static async Task<IResult> DataPortInsideUnitAsync(AlvoCommandContext ctx, IAlvoData data, CommandProbe probe, CancellationToken ct)
    {
        probe.Ran();
        try
        {
            await ctx.TransactAsync((_, token) => data.CreateAsync("booking_notes", Note(), ctx.Caller, cancellationToken: token), cancellationToken: ct);
        }
        catch (InvalidOperationException refusal)
        {
            probe.Refusal = refusal;
            throw;
        }

        return TypedResults.Ok();
    }

    private static async Task<IResult> TwoUnitsAsync(AlvoCommandContext ctx, CommandProbe probe, CancellationToken ct)
    {
        probe.Ran();
        await ctx.TransactAsync((unit, token) => unit.CreateAsync("booking_notes", Note(), token), cancellationToken: ct);
        try
        {
            await ctx.TransactAsync((unit, token) => unit.CreateAsync("booking_notes", Note(), token), cancellationToken: ct);
        }
        catch (InvalidOperationException refusal)
        {
            probe.Refusal = refusal;
        }

        return TypedResults.Ok();
    }

    private static async Task<IResult> FailInUnitAsync(AlvoCommandContext ctx, CommandProbe probe, CancellationToken ct)
    {
        probe.Ran();
        await ctx.TransactAsync(async (unit, token) =>
        {
            await unit.CreateAsync("booking_notes", Note(), token);
            throw new InvalidOperationException("the handler failed inside its unit");
        }, cancellationToken: ct);

        return TypedResults.Ok();
    }

    private static async Task<IResult> FailAfterUnitAsync(AlvoCommandContext ctx, CommandProbe probe, CancellationToken ct)
    {
        probe.Ran();
        await ctx.TransactAsync((unit, token) => unit.CreateAsync("booking_notes", Note(), token), cancellationToken: ct);
        throw new InvalidOperationException("the handler failed after its unit committed");
    }

    private static Dictionary<string, object?> Note() => new() { ["booking_id"] = Guid.NewGuid(), ["text"] = "note" };
}
```

`test/_shared/api/CommandEngineFacts.cs`:

```csharp
using Microsoft.Extensions.DependencyInjection;
using MMLib.Alvo.Data;
using MMLib.Alvo.Events;
using System.Net;
using System.Text.Json.Nodes;

using static MMLib.Alvo.Api.Tests.CommandWorld;

namespace MMLib.Alvo.Api.Tests;

/// <summary>
/// Commands over HTTP (spec 2026-10-14 §4.9, §7 HX-08 acceptance (1), (2), (3), (10)) — the same facts on SQLite and on
/// PostgreSQL. Each fact starts its own world.
/// </summary>
public abstract class CommandEngineFacts
{
    private readonly CommandProbe _probe = new();

    protected abstract AlvoApiEngine Engine { get; }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task An_access_rule_that_refuses_the_caller_answers_403_before_the_handler_runs()
    {
        await using var world = await StartAsync(Engine, _probe);
        var booking = await BookAsync(world, Guest.User);

        using var response = await world.SendAsync(HttpMethod.Post, $"/commands/bookings/{booking}/confirm", Guest);

        response.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await response.ReadProblemTypeAsync()).ShouldBe(AlvoProblemTypes.Forbidden);
        _probe.Runs.ShouldBe(0, "access is judged before the handler");
    }

    [Fact]
    public async Task An_access_rule_reading_the_row_refuses_a_row_in_the_wrong_state()
    {
        await using var world = await StartAsync(Engine, _probe);
        var booking = await BookAsync(world, Guest.User, status: "confirmed");

        using var response = await world.SendAsync(HttpMethod.Post, $"/commands/bookings/{booking}/confirm", Staff);

        response.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        _probe.Runs.ShouldBe(0);
    }

    [Fact]
    public async Task An_entity_command_on_a_row_the_caller_cannot_read_answers_404_before_the_handler_runs()
    {
        await using var world = await StartAsync(Engine, _probe);
        var someoneElses = await BookAsync(world, Guid.NewGuid());

        using var unreadable = await world.SendAsync(HttpMethod.Post, $"/commands/bookings/{someoneElses}/confirm", Guest);
        using var absent = await world.SendAsync(HttpMethod.Post, $"/commands/bookings/{Guid.NewGuid()}/confirm", Staff);

        unreadable.StatusCode.ShouldBe(HttpStatusCode.NotFound, "a row the caller cannot read is 404 — before, and instead of, the access rule's 403");
        absent.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await unreadable.ReadProblemDetailAsync()).ShouldBe(await absent.ReadProblemDetailAsync(), "the two must be indistinguishable");
        _probe.Runs.ShouldBe(0);
    }

    [Fact]
    public async Task A_key_whose_scopes_exclude_writing_the_entity_is_403_out_of_scope()
    {
        await using var world = await StartAsync(Engine, _probe);
        var booking = await BookAsync(world, Guest.User);

        using var response = await world.SendAsync(HttpMethod.Post, $"/commands/bookings/{booking}/confirm", ReadOnlyStaff);

        response.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await response.ReadProblemTypeAsync()).ShouldBe(AlvoProblemTypes.OutOfScope);
        _probe.Runs.ShouldBe(0);
    }

    [Fact]
    public async Task No_credential_is_judged_as_anonymous_and_an_unknown_key_is_401()
    {
        await using var world = await StartAsync(Engine, _probe);

        using var anonymous = await world.SendAsync(HttpMethod.Post, "/commands/quote", body: new JsonObject { ["nights"] = 2 });
        using var ghost = await world.SendAsync(HttpMethod.Post, "/commands/quote", new TestApiKey("ghost-key", ["staff"], ["*:write"]), body: new JsonObject { ["nights"] = 2 });

        anonymous.StatusCode.ShouldBe(HttpStatusCode.Forbidden, "the anonymous caller holds neither role the rule asks for");
        ghost.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        _probe.Runs.ShouldBe(0);
    }

    [Fact]
    public async Task A_completed_command_s_event_reaches_an_event_handler()
    {
        await using var world = await StartAsync(Engine, _probe);
        var booking = await BookAsync(world, Guest.User);

        using var response = await world.SendAsync(HttpMethod.Post, $"/commands/bookings/{booking}/confirm", Staff);
        await world.Services.GetRequiredService<IAlvoEventPump>().DrainAsync(Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        _probe.Completions.ShouldBe(["confirm_booking"]);
        (await NotesAsync(world)).ShouldBe(1);
    }

    [Fact]
    public async Task A_command_with_no_unit_completes_after_its_2xx()
    {
        await using var world = await StartAsync(Engine, _probe);

        using var response = await world.SendAsync(HttpMethod.Post, "/commands/quote", Guest, body: new JsonObject { ["nights"] = 3 });
        await world.Services.GetRequiredService<IAlvoEventPump>().DrainAsync(Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await response.ReadJsonObjectAsync())["total"]!.GetValue<decimal>().ShouldBe(240m);
        _probe.Completions.ShouldBe(["quote_stay"]);
    }

    [Fact]
    public async Task A_replayed_idempotency_key_answers_the_first_result_without_running_the_handler_again()
    {
        await using var world = await StartAsync(Engine, _probe);
        var booking = await BookAsync(world, Guest.User);

        using var first = await world.SendAsync(HttpMethod.Post, $"/commands/bookings/{booking}/confirm", Staff, headers: Key("confirm-1"));
        using var replay = await world.SendAsync(HttpMethod.Post, $"/commands/bookings/{booking}/confirm", Staff, headers: Key("confirm-1"));

        first.StatusCode.ShouldBe(HttpStatusCode.OK);
        replay.StatusCode.ShouldBe(HttpStatusCode.OK, "the replay is answered before the row's new state is judged (Ruling 7)");
        JsonNode.DeepEquals(await first.ReadJsonObjectAsync(), await replay.ReadJsonObjectAsync()).ShouldBeTrue();
        _probe.Runs.ShouldBe(1);
        (await NotesAsync(world)).ShouldBe(1);
    }

    [Fact]
    public async Task A_key_spent_by_one_caller_is_not_replayed_to_another()
    {
        await using var world = await StartAsync(Engine, _probe);

        using var mine = await world.SendAsync(HttpMethod.Post, "/commands/quote", Staff, body: new JsonObject { ["nights"] = 1 }, headers: Key("shared"));
        using var theirs = await world.SendAsync(HttpMethod.Post, "/commands/quote", OtherStaff, body: new JsonObject { ["nights"] = 1 }, headers: Key("shared"));

        mine.StatusCode.ShouldBe(HttpStatusCode.OK);
        theirs.StatusCode.ShouldBe(HttpStatusCode.OK);
        _probe.Runs.ShouldBe(2, "a key is scoped to the caller who sent it");
    }

    [Fact]
    public async Task A_key_spent_by_one_api_key_is_not_replayed_to_another_key_of_the_same_user()
    {
        await using var world = await StartAsync(Engine, _probe);
        var booking = await BookAsync(world, Guest.User);

        using var staff = await world.SendAsync(HttpMethod.Post, $"/commands/bookings/{booking}/confirm", Staff, headers: Key("shared-by-a-user"));
        using var lowerKey = await world.SendAsync(HttpMethod.Post, $"/commands/bookings/{booking}/confirm", StaffAsGuest, headers: Key("shared-by-a-user"));

        staff.StatusCode.ShouldBe(HttpStatusCode.OK);
        lowerKey.StatusCode.ShouldNotBe(HttpStatusCode.OK, "the replay is scoped to the credential: the guest-role key is judged on its own");
        _probe.Runs.ShouldBe(1);
    }

    [Fact]
    public async Task A_key_that_writes_but_cannot_read_the_bound_entity_is_403_out_of_scope()
    {
        await using var world = await StartAsync(Engine, _probe);
        var booking = await BookAsync(world, Guest.User);

        using var response = await world.SendAsync(HttpMethod.Post, $"/commands/bookings/{booking}/confirm", WriteOnlyStaff);

        response.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await response.ReadProblemTypeAsync()).ShouldBe(AlvoProblemTypes.OutOfScope);
        _probe.Runs.ShouldBe(0);
    }

    [Fact]
    public async Task A_negated_tenant_rule_refuses_a_tenantless_caller()
    {
        await using var world = await StartAsync(Engine, _probe);

        using var anonymous = await world.SendAsync(HttpMethod.Post, "/commands/guarded", body: new JsonObject { ["nights"] = 1 });
        using var tenantless = await world.SendAsync(HttpMethod.Post, "/commands/guarded", Staff, body: new JsonObject { ["nights"] = 1 });

        anonymous.StatusCode.ShouldBe(HttpStatusCode.Forbidden, "no tenant: the rule's negation must not admit by the absent operand");
        tenantless.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        _probe.Runs.ShouldBe(0);
    }

    [Fact]
    public async Task A_key_reused_with_a_different_body_is_an_idempotency_conflict()
    {
        await using var world = await StartAsync(Engine, _probe);

        using var first = await world.SendAsync(HttpMethod.Post, "/commands/quote", Staff, body: new JsonObject { ["nights"] = 2 }, headers: Key("q"));
        using var second = await world.SendAsync(HttpMethod.Post, "/commands/quote", Staff, body: new JsonObject { ["nights"] = 3 }, headers: Key("q"));

        second.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        (await second.ReadProblemTypeAsync()).ShouldBe(AlvoProblemTypes.IdempotencyConflict);
        _probe.Runs.ShouldBe(1);
    }

    [Fact]
    public async Task An_anonymous_caller_sending_a_key_is_refused_422()
    {
        await using var world = await StartAsync(Engine, _probe);

        using var response = await world.SendAsync(HttpMethod.Post, "/commands/quote", body: new JsonObject { ["nights"] = 2 }, headers: Key("anon"));

        response.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);
        _probe.Runs.ShouldBe(0);
    }

    [Fact]
    public async Task Two_concurrent_requests_with_one_key_run_the_handler_once()
    {
        await using var world = await StartAsync(Engine, _probe);

        var first = world.SendAsync(HttpMethod.Post, "/commands/hold", Staff, body: new JsonObject { ["nights"] = 1 }, headers: Key("hold-1"));
        await _probe.Started.Task.WaitAsync(TimeSpan.FromSeconds(10), Ct);
        using var second = await world.SendAsync(HttpMethod.Post, "/commands/hold", Staff, body: new JsonObject { ["nights"] = 1 }, headers: Key("hold-1"));
        _probe.Release.SetResult();
        using var firstAnswer = await first;

        second.StatusCode.ShouldBe(HttpStatusCode.Conflict, "the key is still being processed");
        (await second.ReadProblemTypeAsync()).ShouldBe(AlvoProblemTypes.IdempotencyConflict);
        firstAnswer.StatusCode.ShouldBe(HttpStatusCode.OK);
        _probe.Runs.ShouldBe(1);
    }

    [Fact]
    public async Task A_handler_calling_the_injected_data_port_inside_its_unit_is_refused()
    {
        await using var world = await StartAsync(Engine, _probe);

        using var response = await world.SendAsync(HttpMethod.Post, "/commands/misuse/data-port", Staff).WaitAsync(TimeSpan.FromSeconds(10), Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.InternalServerError);
        _probe.Refusal.ShouldBeOfType<AlvoUnitInProgressException>();
        (await NotesAsync(world)).ShouldBe(0, "no side write committed");
    }

    [Fact]
    public async Task An_external_call_before_the_unit_runs_with_no_transaction_open()
    {
        await using var world = await StartAsync(Engine, _probe);
        var booking = await BookAsync(world, Guest.User);

        using var response = await world.SendAsync(HttpMethod.Post, $"/commands/bookings/{booking}/confirm", Staff);

        response.StatusCode.ShouldBe(HttpStatusCode.OK, "the external call's own IAlvoData read would have been refused inside a unit");
        _probe.UnitOpenDuringExternalCall.ShouldBe(false);
    }

    [Fact]
    public async Task A_second_TransactAsync_throws_and_the_first_unit_stands()
    {
        await using var world = await StartAsync(Engine, _probe);

        using var response = await world.SendAsync(HttpMethod.Post, "/commands/misuse/two-units", Staff);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        _probe.Refusal.ShouldBeOfType<InvalidOperationException>().Message.ShouldContain("at most one");
        (await NotesAsync(world)).ShouldBe(1);
    }

    [Fact]
    public async Task The_completion_event_and_the_idempotency_record_commit_in_the_handler_s_unit()
    {
        await using var world = await StartAsync(Engine, _probe);

        using var failed = await world.SendAsync(HttpMethod.Post, "/commands/misuse/fail-in-unit", Staff, headers: Key("in-unit"));
        await world.Services.GetRequiredService<IAlvoEventPump>().DrainAsync(Ct);
        using var retried = await world.SendAsync(HttpMethod.Post, "/commands/misuse/fail-in-unit", Staff, headers: Key("in-unit"));

        failed.StatusCode.ShouldBe(HttpStatusCode.InternalServerError);
        retried.StatusCode.ShouldBe(HttpStatusCode.InternalServerError);
        _probe.Runs.ShouldBe(2, "the unit rolled back its key's record with its writes, so the retry runs again");
        _probe.Completions.ShouldBeEmpty("the completion rolled back with the unit");
        (await NotesAsync(world)).ShouldBe(0);
    }

    [Fact]
    public async Task A_handler_that_throws_after_its_unit_committed_is_not_run_again()
    {
        await using var world = await StartAsync(Engine, _probe);

        using var failed = await world.SendAsync(HttpMethod.Post, "/commands/misuse/fail-after-unit", Staff, headers: Key("after-unit"));
        using var retried = await world.SendAsync(HttpMethod.Post, "/commands/misuse/fail-after-unit", Staff, headers: Key("after-unit"));
        await world.Services.GetRequiredService<IAlvoEventPump>().DrainAsync(Ct);

        failed.StatusCode.ShouldBe(HttpStatusCode.InternalServerError);
        retried.StatusCode.ShouldBe(HttpStatusCode.InternalServerError, "the recorded answer is replayed");
        _probe.Runs.ShouldBe(1);
        _probe.Completions.ShouldBe(["misuse.fail_after_unit"], "its writes and its completion committed");
        (await NotesAsync(world)).ShouldBe(1);
    }

    [Fact]
    public async Task A_command_s_answer_is_never_stored_by_a_cache()
    {
        await using var world = await StartAsync(Engine, _probe);

        using var response = await world.SendAsync(HttpMethod.Post, "/commands/quote", Staff, body: new JsonObject { ["nights"] = 1 });

        response.Headers.CacheControl!.NoStore.ShouldBeTrue();
    }
}
```

`test/MMLib.Alvo.Api.Tests/Commands/SqliteCommandTests.cs`:

```csharp
namespace MMLib.Alvo.Api.Tests.Commands;

/// <summary>SQLite's leg of <see cref="CommandEngineFacts"/>.</summary>
public sealed class SqliteCommandTests : CommandEngineFacts
{
    /// <inheritdoc/>
    protected override AlvoApiEngine Engine => SqliteApiEngine.Instance;
}
```

`test/MMLib.Alvo.Api.Tests.Integration/PostgresCommandTests.cs`:

```csharp
using MMLib.Alvo.Api.Tests;
using Xunit;

namespace MMLib.Alvo.Api.Tests.Integration;

/// <summary>PostgreSQL's leg of <see cref="CommandEngineFacts"/>: not one assertion of its own.</summary>
public sealed class PostgresCommandTests : CommandEngineFacts, IAsyncLifetime
{
    private readonly PostgresApiEngine _engine = new();

    /// <inheritdoc/>
    protected override AlvoApiEngine Engine => _engine;

    /// <inheritdoc/>
    public ValueTask InitializeAsync() => _engine.InitializeAsync();

    /// <inheritdoc/>
    public ValueTask DisposeAsync() => _engine.DisposeAsync();
}
```

Append to `test/MMLib.Alvo.Api.Tests/GateWitnessTests.cs` (M1 — Ruling 38):

```csharp
    [Fact]
    public async Task Every_mapped_command_composes_its_gate()
    {
        await using var world = await AlvoApiWorld.FromDescriptorAsync("command-desk.alvo.json", [], new AlvoApiWorldSetup(
            ConfigureApp: app => app.MapAlvoCommand("quote_stay", "/commands/quote", "true", () => TypedResults.Ok())));

        var witnesses = world.Endpoints
            .Where(endpoint => endpoint.Metadata.GetMetadata<MMLib.Alvo.Commands.Internal.CommandEndpointMetadata>() is not null)
            .Select(endpoint => endpoint.Metadata.GetMetadata<AlvoGateWitness>())
            .ToList();

        witnesses.ShouldHaveSingleItem().ShouldNotBeNull().Compositions.ShouldBeGreaterThan(0);
    }

    [Fact]
    public async Task A_group_convention_removing_a_command_s_filter_refuses_the_start()
    {
        var refusal = await Should.ThrowAsync<Exception>(() => AlvoApiWorld.FromDescriptorAsync("command-desk.alvo.json", [], new AlvoApiWorldSetup(
            ConfigureApp: app =>
            {
                var group = app.MapGroup("/commands");
                group.MapAlvoCommand("quote_stay", "/quote", "true", () => TypedResults.Ok());
                group.Finally(endpoint => endpoint.FilterFactories.Clear());
            })));

        refusal.Message.ShouldContain("removed Alvo's filter from command 'quote_stay'");
    }
```

(`Should.ThrowAsync<Exception>` because the host may surface the boot's `AlvoStartupRefusedException` as itself or wrapped; assert on the message either way.)

- [ ] **Step 2: Run them to verify they fail**

Run: `dotnet test --project test/MMLib.Alvo.Api.Tests --filter-class '*SqliteCommandTests'`
Expected: FAIL to compile — `MapAlvoCommand` does not exist.

- [ ] **Step 3: The port-family refusals, once**

In `ProblemResultFactory`, extract `GuardAsync`'s catch ladder (keeping its comments on the arms) into:

```csharp
    /// <summary>
    /// The refusal for one of the data port's documented families thrown by a call the caller can fix — authorization,
    /// not-found, precondition, idempotency, constraint, chain depth — or null for anything else.
    /// </summary>
    internal static IResult? PortRefusalFor(Exception exception) => exception switch
    {
        AlvoAuthorizationException authorization => Problem(StatusCodes.Status403Forbidden, AlvoProblemTypes.Forbidden, authorization.Message),
        AlvoRecordNotFoundException => NotFound(),
        AlvoPreconditionFailedException precondition => Problem(StatusCodes.Status412PreconditionFailed, AlvoProblemTypes.PreconditionFailed, precondition.Message),
        AlvoIdempotencyConflictException conflict => Problem(StatusCodes.Status409Conflict, AlvoProblemTypes.IdempotencyConflict, conflict.Message),
        AlvoConstraintViolationException constraint => Conflict(constraint),
        AlvoChainDepthExceededException loop => ChainDepthExceeded(loop.Message),
        _ => null,
    };

    /// <summary><see cref="PortRefusalFor"/>, plus the malformed-request family (an <see cref="ArgumentException"/> that is not an <see cref="ArgumentNullException"/>).</summary>
    internal static IResult? RefusalFor(Exception exception) =>
        PortRefusalFor(exception)
        ?? (exception is ArgumentException argument and not ArgumentNullException ? Malformed(WithoutArgumentDetail(argument.Message)) : null);
```

and `GuardAsync` becomes:

```csharp
    internal static async Task<IResult> GuardAsync(Func<Task<IResult>> operation)
    {
        ArgumentNullException.ThrowIfNull(operation);
        try
        {
            return await operation().ConfigureAwait(false);
        }
        catch (Exception exception) when (RefusalFor(exception) is { } refusal)
        {
            return refusal;
        }
    }
```

The full Data API suite (`DataApiAuthTests`, `ProblemDetailsTests`, `IdempotencyTests`, `ConcurrencyTests`) must pass unchanged — the mapping is the same, only its place moved. This extraction is behaviour-neutral, so it lands as a commit of its own before any command code (Ruling 49):

```bash
dotnet format whitespace MMLib.Alvo.slnx --include src/MMLib.Alvo/Api/Internal/ProblemResultFactory.cs
dotnet test --project test/MMLib.Alvo.Api.Tests --filter-class '*DataApiAuthTests' --filter-class '*ProblemDetailsTests' --filter-class '*IdempotencyTests' --filter-class '*ConcurrencyTests' --filter-class '*AlvoExceptionHandlerTests'
git add src/MMLib.Alvo/Api/Internal/ProblemResultFactory.cs
git commit -m "refactor(api): one mapping of the data port's refusals

Claude-Session: https://claude.ai/code/session_01NJAtafM29iddLwfhb5gnmB"
```

- [ ] **Step 4: The marker, the key, the inputs**

`src/MMLib.Alvo/Commands/Internal/CommandEndpointMetadata.cs`:

```csharp
namespace MMLib.Alvo.Commands.Internal;

/// <summary>Marks an endpoint <c>MapAlvoCommand</c> mapped: the OpenAPI enrichment and the exception handler read it.</summary>
/// <param name="Name">The command's name.</param>
/// <param name="Entity">The entity it binds its row from, or null.</param>
internal sealed record CommandEndpointMetadata(string Name, string? Entity);
```

In `AlvoExceptionHandler.FailedInsideAlvo`, also answer for commands — which happens only when the host maps Alvo's problem details (`AddAlvoProblemDetails()` + `UseExceptionHandler()`); without them an unhandled exception is the host's own 500 (Ruling 45):

```csharp
    private static bool FailedInsideAlvo(HttpContext httpContext) =>
        httpContext.Features.Get<IExceptionHandlerFeature>()?.Endpoint?.Metadata is { } metadata
        && (metadata.GetMetadata<DataApiOperationMetadata>() is not null || metadata.GetMetadata<CommandEndpointMetadata>() is not null);
```

`src/MMLib.Alvo/Commands/Internal/CommandInputs.cs`:

```csharp
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Metadata;
using Microsoft.Extensions.DependencyInjection;
using MMLib.Alvo.Api;
using System.IO.Pipelines;
using System.Reflection;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace MMLib.Alvo.Commands.Internal;

/// <summary>
/// Which of a handler's bound arguments describe the request — route values, query and body, not services or the request
/// plumbing — so a key's fingerprint covers what was asked (Ruling 8): minimal-API filters run after binding, so the raw
/// body is already read.
/// </summary>
internal sealed class CommandInputs(IReadOnlyList<int> positions)
{
    private static readonly HashSet<Type> _supplied =
    [
        typeof(HttpContext), typeof(HttpRequest), typeof(HttpResponse), typeof(CancellationToken), typeof(ClaimsPrincipal),
        typeof(AlvoCommandContext), typeof(AlvoCaller), typeof(Stream), typeof(PipeReader), typeof(IFormFileCollection),
    ];

    /// <summary>The fingerprinted parameters of <paramref name="method"/>.</summary>
    internal static CommandInputs Of(MethodInfo method, IServiceProviderIsService services) =>
        new([.. method.GetParameters().Where(parameter => Describes(parameter, services)).Select(parameter => parameter.Position)]);

    /// <summary>The request's fingerprint: the command, the route's <c>id</c>, and each input as JSON, hashed.</summary>
    internal string Fingerprint(CommandRegistration command, HttpContext http, IList<object?> arguments, JsonSerializerOptions json)
    {
        var text = new StringBuilder(command.Id).Append('\n').Append(http.Request.RouteValues["id"]);
        foreach (var position in positions)
        {
            text.Append('\n').Append(arguments[position] is { } value ? JsonSerializer.Serialize(value, value.GetType(), json) : "null");
        }

        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text.ToString())));
    }

    private static bool Describes(ParameterInfo parameter, IServiceProviderIsService services) =>
        !_supplied.Contains(parameter.ParameterType)
        && !parameter.GetCustomAttributes().Any(attribute => attribute is IFromServiceMetadata or FromKeyedServicesAttribute)
        && !services.IsService(parameter.ParameterType);
}
```

`src/MMLib.Alvo/Commands/Internal/CommandKeys.cs`:

```csharp
using Microsoft.AspNetCore.Http;
using MMLib.Alvo.Api.Internal;
using MMLib.Alvo.Data;

namespace MMLib.Alvo.Commands.Internal;

/// <summary>
/// A command request's <c>Idempotency-Key</c>: one header value, the port's rules for a usable key, scoped to the command,
/// the caller and the <b>credential</b> (<c>command:&lt;name&gt;/&lt;tenant&gt;/&lt;user&gt;/&lt;key id&gt;</c>, Ruling 34),
/// fingerprinted over the request's inputs.
/// </summary>
/// <remarks>
/// The credential is in the scope because a command keeps its rendered answer and replays it before the access rule runs
/// again — a departure from <see cref="AlvoIdempotency"/>'s Data API contract, which keeps only a row id and re-reads the
/// row through the caller's current policy. Roles and scopes belong to an API key, not to its user, so two keys of one user
/// must never share a replay. Tenant and user stay in the scope as the Data API files them.
/// </remarks>
internal static class CommandKeys
{
    /// <summary>The header.</summary>
    internal const string Header = "Idempotency-Key";

    /// <summary>The credential segment of a caller who presented no key (an internal caller, or a non-key resolver).</summary>
    internal const string NoCredential = "-";

    /// <summary>Reads the key; answers a 422 to give instead, or null with <paramref name="key"/> set (null when no header).</summary>
    internal static IResult? TryRead(
        HttpRequest request, CommandRegistration command, AlvoContext caller, string? keyId, Func<string> fingerprint, out CommandKey? key)
    {
        key = null;
        var header = request.Headers[Header];
        if (header.Count == 0)
        {
            return null;
        }

        if (header.Count > 1)
        {
            return ProblemResultFactory.Malformed($"Send one '{Header}' header: two values are two keys, and this request can be recorded under one.");
        }

        try
        {
            AlvoIdempotency.EnsureUsableKey(header[0], caller);
        }
        catch (ArgumentException refusal)
        {
            return ProblemResultFactory.Malformed(ProblemResultFactory.WithoutArgumentDetail(refusal.Message));
        }

        key = new CommandKey(ScopeOf(command, caller, keyId), header[0]!, fingerprint());
        return null;
    }

    /// <summary>The scope a key of <paramref name="command"/> is filed under for <paramref name="caller"/> using <paramref name="keyId"/>.</summary>
    internal static string ScopeOf(CommandRegistration command, AlvoContext caller, string? keyId) =>
        $"command:{command.Id}/{AlvoIdempotency.IdentityOf(caller)}/{(string.IsNullOrEmpty(keyId) ? NoCredential : keyId)}";
}
```

`test/MMLib.Alvo.Tests/Commands/CommandKeysTests.cs` (in the core suite, so the core mutation shard kills `CommandKeys` — Ruling 42):

```csharp
using Microsoft.AspNetCore.Http;
using MMLib.Alvo.Commands.Internal;

namespace MMLib.Alvo.Tests.Commands;

/// <summary>A command key's scope: command, tenant, user and credential (Ruling 34).</summary>
public sealed class CommandKeysTests
{
    private static readonly CommandRegistration _quote = new("quote_stay", 1, CommandAccess.Of("true"), null, "/q", null, null);
    private static readonly AlvoContext _user = new() { User = UserId.New(), Roles = new HashSet<Role> { Role.Authenticated } };

    [Fact]
    public void Two_keys_of_one_user_file_one_idempotency_key_apart() =>
        CommandKeys.ScopeOf(_quote, _user, "key-a").ShouldNotBe(CommandKeys.ScopeOf(_quote, _user, "key-b"));

    [Fact]
    public void One_user_in_two_tenants_files_one_idempotency_key_apart_even_under_one_key_id() =>
        CommandKeys.ScopeOf(_quote, _user with { Tenant = new TenantId(Guid.NewGuid()) }, "key-a")
            .ShouldNotBe(CommandKeys.ScopeOf(_quote, _user with { Tenant = new TenantId(Guid.NewGuid()) }, "key-a"));

    [Fact]
    public void A_caller_with_no_credential_files_under_the_dash() =>
        CommandKeys.ScopeOf(_quote, _user, null).ShouldEndWith("/-");

    [Fact]
    public void A_repeated_header_and_an_anonymous_caller_are_refused_before_any_key_is_made()
    {
        var repeated = new DefaultHttpContext();
        repeated.Request.Headers.Append(CommandKeys.Header, "a");
        repeated.Request.Headers.Append(CommandKeys.Header, "b");
        var anonymous = new DefaultHttpContext();
        anonymous.Request.Headers[CommandKeys.Header] = "a";

        CommandKeys.TryRead(repeated.Request, _quote, _user, "k", () => "fp", out var first).ShouldNotBeNull();
        CommandKeys.TryRead(anonymous.Request, _quote, AlvoContext.Anonymous, null, () => "fp", out var second).ShouldNotBeNull();
        first.ShouldBeNull();
        second.ShouldBeNull();
    }
}
```


In `CommandOperationIds`, add `internal const string Tag = "commands";`. In `CommandRunner`, add:

```csharp
    /// <summary>Gives up a key claimed for a request that was refused before its handler ran.</summary>
    internal Task ReleaseAsync(CommandKey? key) =>
        key is null ? Task.CompletedTask : Ledger.ReleaseAsync(key, CancellationToken.None);
```

- [ ] **Step 5: The filter**

`src/MMLib.Alvo/Commands/Internal/CommandEndpointFilter.cs`:

```csharp
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using MMLib.Alvo.Api;
using MMLib.Alvo.Api.Internal;
using MMLib.Alvo.Auth;
using MMLib.Alvo.Auth.Internal;
using MMLib.Alvo.Data;
using MMLib.Alvo.Rules;

namespace MMLib.Alvo.Commands.Internal;

/// <summary>
/// The one filter on every command endpoint — thin HTTP glue around <see cref="CommandAccessJudge"/>, <see cref="CommandKeys"/>
/// and <see cref="CommandRunner"/> — in this order: the caller (401; 403 when the key's scopes exclude reading and writing the
/// bound entity, or, unbound, a wildcard write — Ruling 39); the key (422 when malformed; a replay, an in-flight or a spent
/// key answered at once — Ruling 7, scoped per credential — Ruling 34); the bound row read as the caller (404); the access
/// rule, its required context first (403 — Ruling 33); the chain depth (508); then the handler, outside any transaction (R-M7).
/// </summary>
internal sealed class CommandEndpointFilter(
    CallerGate gate,
    IAlvoContextAccessor accessor,
    CommandAccessJudge judge,
    IAlvoSessions sessions,
    AlvoChainDepthLimit depth,
    CommandRunner runner,
    IServiceProviderIsService services,
    IOptions<JsonOptions> json)
{
    /// <summary>The filter delegate for one command's endpoint.</summary>
    internal EndpointFilterDelegate For(CommandRegistration command, EndpointFilterFactoryContext factory, EndpointFilterDelegate next)
    {
        ArgumentNullException.ThrowIfNull(factory);
        var inputs = CommandInputs.Of(factory.MethodInfo, services);

        return invocation => InvokeAsync(command, inputs, invocation, next);
    }

    private async ValueTask<object?> InvokeAsync(
        CommandRegistration command, CommandInputs inputs, EndpointFilterInvocationContext invocation, EndpointFilterDelegate next)
    {
        var admission = await gate.AdmitAsync(
            invocation.HttpContext, command.Entity ?? CallerGate.AnyEntity, DataOperation.Update, alsoRead: command.Entity is not null).ConfigureAwait(false);
        if (admission.Refusal is { } refused)
        {
            return refused;
        }

        return await CallerResolution.PublishingAsync(
            accessor, admission.Principal, invocation, _ => GuardedAsync(command, inputs, invocation, next, admission)).ConfigureAwait(false);
    }

    private async ValueTask<object?> GuardedAsync(
        CommandRegistration command, CommandInputs inputs, EndpointFilterInvocationContext invocation, EndpointFilterDelegate next, CallerAdmission admission)
    {
        try
        {
            return await KeyedAsync(command, inputs, invocation, next, admission).ConfigureAwait(false);
        }
        catch (Exception exception) when (ProblemResultFactory.PortRefusalFor(exception) is { } refusal)
        {
            return refusal;
        }
    }

    private async Task<object?> KeyedAsync(
        CommandRegistration command, CommandInputs inputs, EndpointFilterInvocationContext invocation, EndpointFilterDelegate next, CallerAdmission admission)
    {
        var http = invocation.HttpContext;
        var malformed = CommandKeys.TryRead(
            http.Request, command, admission.Context, admission.Principal?.KeyId,
            () => inputs.Fingerprint(command, http, invocation.Arguments, json.Value.SerializerOptions), out var key);
        if (malformed is not null)
        {
            return malformed;
        }

        var claimed = await runner.ClaimAsync(new CommandInvocation(command, admission.Context, null, key)).ConfigureAwait(false);
        return claimed.Early ?? await AdmittedAsync(claimed.Invocation, invocation, next).ConfigureAwait(false);
    }

    private async Task<object?> AdmittedAsync(CommandInvocation requested, EndpointFilterInvocationContext invocation, EndpointFilterDelegate next)
    {
        CommandInvocation admitted;
        try
        {
            admitted = requested with { Record = await RowAsync(requested.Command, invocation.HttpContext, requested.Caller).ConfigureAwait(false) };
            judge.EnsureAdmitted(admitted.Command.Id, admitted.Record, admitted.Caller);
            depth.EnsureWithin(admitted.Caller);
        }
        catch
        {
            await runner.ReleaseAsync(requested.Key).ConfigureAwait(false);
            throw;
        }

        return await RunAsync(admitted, invocation, next).ConfigureAwait(false);
    }

    private async Task<object?> RunAsync(CommandInvocation admitted, EndpointFilterInvocationContext invocation, EndpointFilterDelegate next)
    {
        var run = runner.Start(admitted);
        AlvoCommandContext.Pending(invocation.HttpContext).Start(run);
        AlvoCaller.For(invocation.HttpContext).Resolve(admitted.Caller);

        return await runner.FinishAsync(run, async () => await next(invocation).ConfigureAwait(false)).ConfigureAwait(false);
    }

    private async Task<AlvoRecord?> RowAsync(CommandRegistration command, HttpContext http, AlvoContext caller)
    {
        if (command.Entity is not { } entity)
        {
            return null;
        }

        return Guid.TryParse(http.Request.RouteValues["id"]?.ToString(), out var id)
            ? await sessions.For(caller).GetAsync(entity, id, http.RequestAborted).ConfigureAwait(false) ?? throw new AlvoRecordNotFoundException()
            : throw new AlvoRecordNotFoundException();
    }
}
```

In `Commands/Setup.cs`, `AddAlvoCommands`: `services.TryAddSingleton<CommandEndpointFilter>();`.

- [ ] **Step 6: The five `MapAlvoCommand` overloads**

`src/MMLib.Alvo/Api/AlvoCommandEndpointRouteBuilderExtensions.cs`:

```csharp
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.Routing.Patterns;
using Microsoft.Extensions.DependencyInjection;
using MMLib.Alvo.Api.Internal;
using MMLib.Alvo.Commands.Internal;
using MMLib.Alvo.Data.Internal;
using MMLib.Alvo.Events;
using System.Linq.Expressions;

namespace Microsoft.AspNetCore.Builder;

/// <summary>Maps named commands: minimal-API endpoints whose caller, row and access rule Alvo judges before the handler runs.</summary>
/// <remarks>
/// <para>
/// A command is a <c>POST</c>. Before the handler runs, Alvo resolves the caller as the Data API does (no credential is the
/// anonymous caller; a key that cannot be used is 401; a key whose scopes do not cover reading and writing the command's
/// entity — or, for a command bound to none, a wildcard write scope — is 403), answers a replayed <c>Idempotency-Key</c>, reads the
/// bound row as the caller (404 when they cannot read it), and evaluates the access rule (403). The handler then runs
/// outside any transaction: make external calls first, and write in one <c>AlvoCommandContext.TransactAsync</c>.
/// </para>
/// <para>
/// The access rule is CEL over <c>@user</c> and <c>@tenant</c>, and over the row's fields for a command bound to an entity
/// — or a typed lambda translated to that CEL. It is checked when the host boots, with every other extension, and it may not
/// be empty: write <c>'true'</c> to admit every caller explicitly. Map every command before <c>app.Run()</c> and before a
/// descriptor is applied. A key reaches a command bound to an entity only with scopes that both read and write it; the
/// handler's own writes to other entities are judged by the data port's policy as the caller, which API-key scopes do not
/// narrow. A convention that removes Alvo's filter from a command refuses the host's start.
/// </para>
/// </remarks>
public static class AlvoCommandEndpointRouteBuilderExtensions
{
    /// <summary>Maps a command bound to no entity, its access rule written as CEL.</summary>
    /// <param name="endpoints">The route builder.</param>
    /// <param name="name">The command's name: lower-case segments separated by dots, e.g. <c>quote_stay</c>.</param>
    /// <param name="pattern">The route pattern.</param>
    /// <param name="access">The access rule: CEL over <c>@user</c> and <c>@tenant</c>.</param>
    /// <param name="handler">The minimal-API handler; it may take an <c>AlvoCommandContext</c>.</param>
    /// <returns>The endpoint's builder, for conventions such as <c>WithSummary</c> or <c>RequireRateLimiting</c>.</returns>
    public static RouteHandlerBuilder MapAlvoCommand(this IEndpointRouteBuilder endpoints, string name, string pattern, string access, Delegate handler) =>
        Map(endpoints, name, pattern, null, CommandAccess.Of(access), handler);

    /// <summary>Maps a command bound to no entity, its access rule written as a lambda over the caller.</summary>
    /// <param name="endpoints">The route builder.</param>
    /// <param name="name">The command's name.</param>
    /// <param name="pattern">The route pattern.</param>
    /// <param name="access">The access rule, e.g. <c>caller =&gt; caller.HasRole("staff")</c>; translated to CEL here.</param>
    /// <param name="handler">The minimal-API handler.</param>
    /// <returns>The endpoint's builder.</returns>
    /// <exception cref="MMLib.Alvo.Data.AlvoTranslationException">The lambda has no CEL equivalent.</exception>
    public static RouteHandlerBuilder MapAlvoCommand(
        this IEndpointRouteBuilder endpoints, string name, string pattern, Expression<Func<AlvoConditionCaller, bool>> access, Delegate handler) =>
        Map(endpoints, name, pattern, null, CommandAccess.Typed(access, name), handler);

    /// <summary>Maps a command bound to one row of <paramref name="entity"/>, which its route's <c>{id}</c> names.</summary>
    /// <param name="endpoints">The route builder.</param>
    /// <param name="name">The command's name.</param>
    /// <param name="pattern">The route pattern; it must have an <c>{id}</c> parameter.</param>
    /// <param name="entity">The entity the row belongs to.</param>
    /// <param name="access">The access rule: CEL over the row's fields, <c>@user</c> and <c>@tenant</c>.</param>
    /// <param name="handler">The minimal-API handler.</param>
    /// <returns>The endpoint's builder.</returns>
    public static RouteHandlerBuilder MapAlvoCommand(
        this IEndpointRouteBuilder endpoints, string name, string pattern, string entity, string access, Delegate handler) =>
        Map(endpoints, name, pattern, Bound(entity, pattern), CommandAccess.Of(access), handler);

    /// <summary>Maps a command bound to one row of <typeparamref name="TEntity"/>'s entity, its access rule written as CEL.</summary>
    /// <typeparam name="TEntity">A typed record carrying <c>[AlvoEntity("…")]</c>.</typeparam>
    /// <param name="endpoints">The route builder.</param>
    /// <param name="name">The command's name.</param>
    /// <param name="pattern">The route pattern; it must have an <c>{id}</c> parameter.</param>
    /// <param name="access">The access rule: CEL over the row's fields, <c>@user</c> and <c>@tenant</c>.</param>
    /// <param name="handler">The minimal-API handler.</param>
    /// <returns>The endpoint's builder.</returns>
    public static RouteHandlerBuilder MapAlvoCommand<TEntity>(
        this IEndpointRouteBuilder endpoints, string name, string pattern, string access, Delegate handler)
        where TEntity : class =>
        Map(endpoints, name, pattern, Bound(TypedEntity.NameOf<TEntity>(), pattern), CommandAccess.Of(access), handler);

    /// <summary>Maps a command bound to one row of <typeparamref name="TEntity"/>'s entity, its access rule a typed lambda.</summary>
    /// <typeparam name="TEntity">A typed record carrying <c>[AlvoEntity("…")]</c>.</typeparam>
    /// <param name="endpoints">The route builder.</param>
    /// <param name="name">The command's name.</param>
    /// <param name="pattern">The route pattern; it must have an <c>{id}</c> parameter.</param>
    /// <param name="access">The access rule, e.g. <c>(booking, caller) =&gt; booking.Status == BookingStatus.Requested &amp;&amp; caller.HasRole("staff")</c>.</param>
    /// <param name="handler">The minimal-API handler.</param>
    /// <returns>The endpoint's builder.</returns>
    /// <exception cref="MMLib.Alvo.Data.AlvoTranslationException">The lambda has no CEL equivalent.</exception>
    public static RouteHandlerBuilder MapAlvoCommand<TEntity>(
        this IEndpointRouteBuilder endpoints, string name, string pattern, Expression<Func<TEntity, AlvoConditionCaller, bool>> access, Delegate handler)
        where TEntity : class =>
        Map(endpoints, name, pattern, Bound(TypedEntity.NameOf<TEntity>(), pattern), CommandAccess.Typed(access, name), handler);

    private static string Bound(string entity, string pattern)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(entity);
        ArgumentException.ThrowIfNullOrWhiteSpace(pattern);

        return RoutePatternFactory.Parse(pattern).GetParameter("id") is not null
            ? entity
            : throw new ArgumentException(
                $"A command bound to '{entity}' reads the row its route names, so '{pattern}' needs an '{{id}}' route parameter — '/commands/{entity}/{{id:guid}}/confirm'.",
                nameof(pattern));
    }

    private static RouteHandlerBuilder Map(
        IEndpointRouteBuilder endpoints, string name, string pattern, string? entity, CommandAccess access, Delegate handler)
    {
        ArgumentNullException.ThrowIfNull(endpoints);
        ArgumentNullException.ThrowIfNull(handler);
        var registry = endpoints.ServiceProvider.GetService<CommandRegistry>()
            ?? throw new InvalidOperationException("Alvo is not registered. Call services.AddAlvo(...) before MapAlvoCommand(...).");
        var command = registry.Map(name, pattern, entity, access);
        var filter = endpoints.ServiceProvider.GetRequiredService<CommandEndpointFilter>();
        var witness = new AlvoGateWitness($"command '{command.Id}'");

        return endpoints.MapPost(pattern, handler)
            .AddEndpointFilter(NoStoreResponseFilter.Instance)
            .AddEndpointFilterFactory(witness.Wrap((factory, next) => filter.For(command, factory, next)))
            .WithMetadata(new CommandEndpointMetadata(command.Id, command.Entity), witness)
            .WithName(CommandOperationIds.Of(command.Id))
            .WithTags(CommandOperationIds.Tag);
    }
}
```

`src/MMLib.Alvo/Commands/Internal/CommandGateCheck.cs` (M1 — #184 for commands, Ruling 38):

```csharp
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using MMLib.Alvo.Api.Internal;
using MMLib.Alvo.Migrations;

namespace MMLib.Alvo.Commands.Internal;

/// <summary>
/// Refuses to start a host in which a convention removed a command's Alvo filter: every endpoint carrying a
/// <see cref="CommandEndpointMetadata"/> must carry its <see cref="AlvoGateWitness"/>, and the witness must have been
/// composed into the request delegate. Without the filter a command's handler would run with no caller, no key and no
/// access rule — whether or not it reads <c>AlvoCommandContext</c>.
/// </summary>
/// <remarks>
/// Run by the boot once it has published its phase, before the server listens. It reads the host's own endpoint data
/// sources (the routing composite's), never Alvo's Data API source, whose routes materialise on the first request; a
/// <c>MapGroup</c> holding both the Data API and a command is enumerated whole, so its Data API routes materialise at start
/// (Ruling 38 states the cost).
/// </remarks>
internal sealed class CommandGateCheck(CommandRegistry commands, IServiceProvider services)
{
    /// <exception cref="AlvoStartupRefusedException">A command endpoint's gate was removed.</exception>
    internal void EnsureGated()
    {
        if (commands.Mapped.Count == 0 || services.GetService<EndpointDataSource>() is not CompositeEndpointDataSource composite)
        {
            return;
        }

        foreach (var endpoint in composite.DataSources.Where(source => source is not AlvoEndpointDataSource).SelectMany(source => source.Endpoints))
        {
            if (endpoint.Metadata.GetMetadata<CommandEndpointMetadata>() is { } command
                && endpoint.Metadata.GetMetadata<AlvoGateWitness>() is not { Compositions: > 0 })
            {
                throw new AlvoStartupRefusedException(
                    $"Alvo cannot start: a convention removed Alvo's filter from command '{command.Name}', so its handler would run with no caller, no Idempotency-Key and no access rule.",
                    "Remove the convention that clears or rebuilds EndpointBuilder.FilterFactories on the command (or on a MapGroup it is mapped under); a convention may add filters around Alvo's, never take them away.");
            }
        }
    }
}
```

Register it in `Commands/Setup.cs` (`services.TryAddSingleton<CommandGateCheck>();`) and call it from `AlvoBootService` once the boot has published its phase — after the descriptor is primed (or the boot settled on `Awaiting`) and before `StartAsync` returns — with the same refusal handling as `RefuseUnregistrableExtensions` (`_state.Failed`, `BootRefused` log, rethrow): add an optional `CommandGateCheck? commandGate = null` constructor parameter and pass it from the registration in `AlvoServiceCollectionExtensions.AddBoot`.

- [ ] **Step 7: Run the facts, accept the baseline**

Run: `dotnet test --project test/MMLib.Alvo.Tests --filter-class '*CommandKeysTests'` and `dotnet test --project test/MMLib.Alvo.Api.Tests --filter-class '*SqliteCommandTests' --filter-class '*GateWitnessTests' --filter-class '*DataApiAuthTests' --filter-class '*ProblemDetailsTests' --filter-class '*IdempotencyTests' --filter-class '*AlvoExceptionHandlerTests'`
Expected: PASS. The core baseline adds exactly the five `MapAlvoCommand` overloads on `AlvoCommandEndpointRouteBuilderExtensions`; accept, dispatch `alvo-snapshot-judge`.

- [ ] **Step 8: Format, ring1 and the PostgreSQL leg, commit**

```bash
dotnet format whitespace MMLib.Alvo.slnx --include src/MMLib.Alvo/Commands/Internal/CommandGateCheck.cs src/MMLib.Alvo/Api/Internal/CallerGate.cs src/MMLib.Alvo/Migrations/Internal/AlvoBootService.cs test/MMLib.Alvo.Tests/Commands/CommandKeysTests.cs test/MMLib.Alvo.Api.Tests/GateWitnessTests.cs src/MMLib.Alvo/Api/AlvoCommandEndpointRouteBuilderExtensions.cs src/MMLib.Alvo/Commands/Internal/CommandEndpointFilter.cs src/MMLib.Alvo/Commands/Internal/CommandEndpointMetadata.cs src/MMLib.Alvo/Commands/Internal/CommandInputs.cs src/MMLib.Alvo/Commands/Internal/CommandKeys.cs src/MMLib.Alvo/Commands/Internal/CommandRunner.cs src/MMLib.Alvo/Commands/Internal/CommandOperationIds.cs src/MMLib.Alvo/Commands/Setup.cs src/MMLib.Alvo/Api/Internal/ProblemResultFactory.cs src/MMLib.Alvo/Api/Internal/AlvoExceptionHandler.cs test/_shared/api/AlvoApiWorld.cs test/_shared/api/CommandWorld.cs test/_shared/api/CommandEngineFacts.cs test/MMLib.Alvo.Api.Tests/Commands/SqliteCommandTests.cs test/MMLib.Alvo.Api.Tests.Integration/PostgresCommandTests.cs
scripts/test-ring1
dotnet test --project test/MMLib.Alvo.Api.Tests.Integration --filter-class '*PostgresCommandTests'
git add src/MMLib.Alvo/Api/AlvoCommandEndpointRouteBuilderExtensions.cs src/MMLib.Alvo/Commands src/MMLib.Alvo/Api/Internal/AlvoExceptionHandler.cs src/MMLib.Alvo/Api/Internal/CallerGate.cs src/MMLib.Alvo/Migrations/Internal/AlvoBootService.cs src/MMLib.Alvo/AlvoServiceCollectionExtensions.cs test/MMLib.Alvo.Tests/Commands/CommandKeysTests.cs test/MMLib.Alvo.Api.Tests/GateWitnessTests.cs test/_shared/api/AlvoApiWorld.cs test/_shared/api/CommandWorld.cs test/_shared/api/CommandEngineFacts.cs test/_shared/api/descriptors/command-desk.alvo.json test/MMLib.Alvo.Api.Tests/Commands/SqliteCommandTests.cs test/MMLib.Alvo.Api.Tests.Integration/PostgresCommandTests.cs test/MMLib.Alvo.Tests/PublicApi.MMLib.Alvo.verified.txt
git commit -m "feat(commands): MapAlvoCommand with access before the handler and idempotent replay

Claude-Session: https://claude.ai/code/session_01NJAtafM29iddLwfhb5gnmB"
```

---

## Task 9: Internal commands — `AddCommand<TCommand>` and `IAlvoCommands`

**Files:**
- Create: `src/MMLib.Alvo/Commands/IAlvoCommand.cs`, `src/MMLib.Alvo/Commands/IAlvoCommands.cs`, `src/MMLib.Alvo/Commands/Internal/AlvoCommands.cs`
- Modify: `src/MMLib.Alvo/Extensions/AlvoExtensionBuilderExtensions.cs` (`AddCommand<TCommand>` ×2), `src/MMLib.Alvo/Commands/Setup.cs` (`IAlvoCommands`)
- Test: `test/MMLib.Alvo.TestHost.Tests/InternalCommandTests.cs`
- Baseline: `test/MMLib.Alvo.Tests/PublicApi.MMLib.Alvo.verified.txt`

**Interfaces:**
- Consumes: Task 6's `CommandRegistration`, `CommandAccess`, `CommandAccessJudge`, `ExtensionRegistrations.Add`; Task 7's `CommandRunner`, `CommandRun`, `AlvoCommandContext(CommandRun)`; `AlvoChainDepthLimit`, `AlvoFlow.RefuseInsideUnit`.
- Produces: `public interface IAlvoCommand<in TInput> { Task HandleAsync(TInput input, AlvoCommandContext context, CancellationToken cancellationToken); }`; `public interface IAlvoCommands { Task InvokeAsync<TInput>(string name, TInput input, AlvoContext context, CancellationToken cancellationToken = default); }`; `AddCommand<TCommand>(this IAlvoBuilder, string name, string access)` and `AddCommand<TCommand>(this IAlvoBuilder, string name, Expression<Func<AlvoConditionCaller, bool>> access)` (`where TCommand : class`); `internal sealed class AlvoCommands : IAlvoCommands`.

- [ ] **Step 1: Write the failing tests**

`test/MMLib.Alvo.TestHost.Tests/InternalCommandTests.cs`:

```csharp
using Microsoft.Extensions.DependencyInjection;
using MMLib.Alvo.Commands;
using MMLib.Alvo.Data;
using MMLib.Alvo.Events;
using MMLib.Alvo.Extensions;
using System.Collections.Concurrent;

namespace MMLib.Alvo.TestHost.Tests;

/// <summary>
/// Internal commands (spec 2026-10-14 §4.9, acceptance (8)): invoked by handlers, timers and jobs through
/// <see cref="IAlvoCommands"/>, under the same access rule, unit and completion event as a mapped command.
/// </summary>
public sealed class InternalCommandTests
{
    private static string HelpDesk => Path.Combine(AppContext.BaseDirectory, "descriptors", "help-desk.alvo.json");

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task An_internal_command_invoked_from_a_handler_runs_its_access_rule_and_emits_its_event()
    {
        var seen = new Seen();
        await using var alvo = await StartAsync(seen, runAs: AlvoRunAs.System);

        await alvo.Data.CreateAsync("tickets", new Dictionary<string, object?> { ["title"] = "Printer jam" }, AlvoTestHost.Caller("agent"), cancellationToken: Ct);
        await alvo.DrainEventsAsync(Ct);

        seen.Escalated.Count.ShouldBe(1, "the system the handler runs as holds admin, which the rule admits");
        seen.Completions.ShouldBe(["escalate_ticket"]);
    }

    [Fact]
    public async Task A_handler_running_as_a_caller_the_rule_refuses_never_runs_the_command()
    {
        var seen = new Seen();
        await using var alvo = await StartAsync(seen, runAs: AlvoRunAs.EventActor);

        await alvo.Data.CreateAsync("tickets", new Dictionary<string, object?> { ["title"] = "Printer jam" }, AlvoTestHost.Caller("agent"), cancellationToken: Ct);
        await alvo.DrainEventsAsync(Ct);

        seen.Escalated.ShouldBeEmpty("an agent holds no admin, so the access rule refuses before the command runs");
        seen.Completions.ShouldBeEmpty();
    }

    [Fact]
    public async Task A_caller_the_rule_refuses_is_an_authorization_refusal()
    {
        await using var alvo = await StartAsync(new Seen(), runAs: AlvoRunAs.System);
        var commands = alvo.Services.GetRequiredService<IAlvoCommands>();

        await Should.ThrowAsync<AlvoAuthorizationException>(() => commands.InvokeAsync("escalate_ticket", new Escalation(Guid.NewGuid()), AlvoTestHost.Caller("agent"), Ct));
    }

    [Fact]
    public async Task A_negated_identity_rule_refuses_the_anonymous_caller()
    {
        await using var alvo = await StartAsync(new Seen(), runAs: AlvoRunAs.System);
        var commands = alvo.Services.GetRequiredService<IAlvoCommands>();

        (await Should.ThrowAsync<AlvoAuthorizationException>(() => commands.InvokeAsync("unbanned_escalation", new Escalation(Guid.NewGuid()), AlvoContext.Anonymous, Ct)))
            .Message.ShouldContain("has no identity");
    }

    [Fact]
    public async Task An_unknown_command_and_a_wrong_input_are_refused_by_name()
    {
        await using var alvo = await StartAsync(new Seen(), runAs: AlvoRunAs.System);
        var commands = alvo.Services.GetRequiredService<IAlvoCommands>();

        (await Should.ThrowAsync<ArgumentException>(() => commands.InvokeAsync("escalate", new Escalation(Guid.NewGuid()), AlvoContext.System(null), Ct))).ParamName.ShouldBe("name");
        (await Should.ThrowAsync<ArgumentException>(() => commands.InvokeAsync("escalate_ticket", "not an escalation", AlvoContext.System(null), Ct))).ParamName.ShouldBe("input");
    }

    [Fact]
    public async Task Invoking_a_command_inside_an_open_unit_is_refused()
    {
        await using var alvo = await StartAsync(new Seen(), runAs: AlvoRunAs.System);
        var commands = alvo.Services.GetRequiredService<IAlvoCommands>();

        await Should.ThrowAsync<AlvoUnitInProgressException>(() => alvo.Services.GetRequiredService<IAlvoTransactions>().TransactAsync(
            AlvoContext.System(null), (_, ct) => commands.InvokeAsync("escalate_ticket", new Escalation(Guid.NewGuid()), AlvoContext.System(null), ct), cancellationToken: Ct));
    }

    [Fact]
    public void A_class_that_is_no_command_is_refused_at_registration() =>
        Should.Throw<ArgumentException>(() => new ServiceCollection().AddAlvo(alvo => alvo.AddCommand<Seen>("not_a_command", "true")))
            .Message.ShouldContain("IAlvoCommand<TInput>");

    private static Task<AlvoTestHost> StartAsync(Seen seen, AlvoRunAs runAs) =>
        AlvoTestHost.StartAsync(builder =>
        {
            builder.Services.AddSingleton(seen);
            builder.FromDescriptor(HelpDesk)
                .AddCommand<EscalateTicket>("escalate_ticket", "'admin' in @user.roles")
                .AddCommand<EscalateTicket>("unbanned_escalation", "!(string(@user.id) == '6c1f9a3e-0000-4000-8000-000000000001')")
                .Entity("tickets", tickets => tickets.OnCreated(
                    "escalate-new",
                    (AlvoCreated<AlvoRecord> created, IAlvoCommands commands, AlvoEventContext context, CancellationToken ct) =>
                        commands.InvokeAsync("escalate_ticket", new Escalation((Guid)created.New["id"]!), context.Caller, ct),
                    configure: options => options.RunAs = runAs))
                .AddEventHandler("on-escalated", "alvo.command.escalate_ticket.completed", (AlvoRecord payload) =>
                {
                    seen.Completions.Enqueue((string)payload["command"]!);
                    return Task.CompletedTask;
                });
        }, cancellationToken: Ct);

    public sealed record Escalation(Guid Ticket);

    public sealed class EscalateTicket(Seen seen) : IAlvoCommand<Escalation>
    {
        public async Task HandleAsync(Escalation input, AlvoCommandContext context, CancellationToken cancellationToken)
        {
            await context.TransactAsync((_, _) => Task.CompletedTask, cancellationToken: cancellationToken);
            seen.Escalated.Enqueue(input.Ticket);
        }
    }

    public sealed class Seen
    {
        public ConcurrentQueue<Guid> Escalated { get; } = new();

        public ConcurrentQueue<string> Completions { get; } = new();
    }
}
```

- [ ] **Step 2: Run them to verify they fail**

Run: `dotnet test --project test/MMLib.Alvo.TestHost.Tests --filter-class '*InternalCommandTests'`
Expected: FAIL to compile — `IAlvoCommands`, `AddCommand` do not exist.

- [ ] **Step 3: The public interfaces**

`src/MMLib.Alvo/Commands/IAlvoCommand.cs`:

```csharp
namespace MMLib.Alvo.Commands;

/// <summary>
/// A command invoked in process — by an event handler, a timer or a job — through <see cref="IAlvoCommands"/>, under its
/// access rule, at most one transaction unit and its completion event, exactly as a command mapped over HTTP.
/// </summary>
/// <typeparam name="TInput">What the command is invoked with.</typeparam>
/// <remarks>Register it with <c>AddCommand&lt;TCommand&gt;(name, access)</c>; it is resolved from a fresh scope per invocation.</remarks>
public interface IAlvoCommand<in TInput>
{
    /// <summary>Runs the command.</summary>
    /// <param name="input">What it was invoked with.</param>
    /// <param name="context">Who invoked it, a session as them, and its one unit.</param>
    /// <param name="cancellationToken">A token to cancel the run.</param>
    /// <returns>A task that completes when the command has run.</returns>
    Task HandleAsync(TInput input, AlvoCommandContext context, CancellationToken cancellationToken);
}
```

`src/MMLib.Alvo/Commands/IAlvoCommands.cs`:

```csharp
namespace MMLib.Alvo.Commands;

/// <summary>Invokes the host's internal commands by name.</summary>
public interface IAlvoCommands
{
    /// <summary>
    /// Invokes the internal command <paramref name="name"/> as <paramref name="context"/>: its access rule is evaluated
    /// first, then it runs, and once it completes its <c>alvo.command.&lt;name&gt;.completed</c> event is recorded.
    /// </summary>
    /// <typeparam name="TInput">The input type the command takes, or one derived from it.</typeparam>
    /// <param name="name">The command's name.</param>
    /// <param name="input">Its input.</param>
    /// <param name="context">The caller it runs as — in an event handler, <c>AlvoEventContext.Caller</c>.</param>
    /// <param name="cancellationToken">A token to cancel the run.</param>
    /// <returns>A task that completes when the command has run and its completion is recorded.</returns>
    /// <exception cref="ArgumentException">No internal command has that name, or it takes another input type.</exception>
    /// <exception cref="MMLib.Alvo.Data.AlvoAuthorizationException">The command's access rule does not admit the caller.</exception>
    /// <exception cref="MMLib.Alvo.Data.AlvoUnitInProgressException">A transaction unit is open on this flow.</exception>
    /// <exception cref="MMLib.Alvo.Data.AlvoChainDepthExceededException">The caller's chain is already past the depth limit.</exception>
    Task InvokeAsync<TInput>(string name, TInput input, AlvoContext context, CancellationToken cancellationToken = default);
}
```

- [ ] **Step 4: Registration and invocation**

Add to `AlvoExtensionBuilderExtensions` (`using MMLib.Alvo.Commands;`, `using MMLib.Alvo.Commands.Internal;`, `using Microsoft.Extensions.DependencyInjection.Extensions;`, `using System.Linq.Expressions;`):

```csharp
    /// <summary>
    /// Registers an internal command: <typeparamref name="TCommand"/>, invoked through <c>IAlvoCommands.InvokeAsync</c>
    /// under <paramref name="access"/>.
    /// </summary>
    /// <typeparam name="TCommand">A class implementing <c>IAlvoCommand&lt;TInput&gt;</c> exactly once.</typeparam>
    /// <param name="builder">The Alvo builder.</param>
    /// <param name="name">The command's name: lower-case segments separated by dots, e.g. <c>escalate_ticket</c>.</param>
    /// <param name="access">The access rule: CEL over <c>@user</c> and <c>@tenant</c>; <c>'true'</c> admits every caller.</param>
    /// <returns><paramref name="builder"/>, for chaining.</returns>
    /// <exception cref="ArgumentException"><typeparamref name="TCommand"/> is no command, or <paramref name="access"/> is empty.</exception>
    public static IAlvoBuilder AddCommand<TCommand>(this IAlvoBuilder builder, string name, string access)
        where TCommand : class =>
        Commanded<TCommand>(builder, name, CommandAccess.Of(access));

    /// <summary>Registers an internal command whose access rule is a lambda over the caller, translated to CEL here.</summary>
    /// <typeparam name="TCommand">A class implementing <c>IAlvoCommand&lt;TInput&gt;</c> exactly once.</typeparam>
    /// <param name="builder">The Alvo builder.</param>
    /// <param name="name">The command's name.</param>
    /// <param name="access">The access rule, e.g. <c>caller =&gt; caller.HasRole("dispatcher")</c>.</param>
    /// <returns><paramref name="builder"/>, for chaining.</returns>
    /// <exception cref="AlvoTranslationException">The lambda has no CEL equivalent.</exception>
    public static IAlvoBuilder AddCommand<TCommand>(this IAlvoBuilder builder, string name, Expression<Func<AlvoConditionCaller, bool>> access)
        where TCommand : class =>
        Commanded<TCommand>(builder, name, CommandAccess.Typed(access, name));

    private static IAlvoBuilder Commanded<TCommand>(IAlvoBuilder builder, string name, CommandAccess access)
        where TCommand : class
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        var input = InputOf(typeof(TCommand));
        builder.Services.TryAddTransient<TCommand>();
        ExtensionRegistrations.Add(builder.Services, sequence => new CommandRegistration(name, sequence, access, null, null, typeof(TCommand), input));

        return builder;
    }

    private static Type InputOf(Type command)
    {
        var implemented = command.GetInterfaces()
            .Where(candidate => candidate.IsGenericType && candidate.GetGenericTypeDefinition() == typeof(IAlvoCommand<>))
            .ToList();

        return implemented is [var only]
            ? only.GetGenericArguments()[0]
            : throw new ArgumentException(
                $"{command.Name} must implement IAlvoCommand<TInput> exactly once to be a command; it implements it {implemented.Count} times.",
                nameof(command));
    }
```

`src/MMLib.Alvo/Commands/Internal/AlvoCommands.cs`:

```csharp
using Microsoft.Extensions.DependencyInjection;
using MMLib.Alvo.Data;
using MMLib.Alvo.Extensions.Internal;

namespace MMLib.Alvo.Commands.Internal;

/// <summary>
/// <see cref="IAlvoCommands"/>: refused inside an open unit (it would run beside it), depth-limited, access-ruled; the
/// command is resolved from a fresh scope and run like a mapped one, without an idempotency key (spec 2026-10-14 §4.9).
/// </summary>
internal sealed class AlvoCommands(
    ExtensionRegistry registry,
    CommandAccessJudge judge,
    AlvoChainDepthLimit depth,
    CommandRunner runner,
    IServiceScopeFactory scopes) : IAlvoCommands
{
    /// <inheritdoc/>
    public async Task InvokeAsync<TInput>(string name, TInput input, AlvoContext context, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(context);
        AlvoFlow.RefuseInsideUnit("IAlvoCommands.InvokeAsync");
        var command = Internal(name, typeof(TInput));
        depth.EnsureWithin(context);
        judge.EnsureAdmitted(command.Id, null, context);

        await RunAsync(command, input, context, cancellationToken).ConfigureAwait(false);
    }

    private CommandRegistration Internal(string name, Type input)
    {
        var command = registry.Commands.FirstOrDefault(candidate => !candidate.IsMapped && string.Equals(candidate.Id, name, StringComparison.Ordinal))
            ?? throw new ArgumentException(
                $"No internal command '{name}' is registered. AddCommand<TCommand>(\"{name}\", …) registers one; a command mapped over HTTP is invoked over HTTP.",
                nameof(name));

        return command.InputType!.IsAssignableFrom(input)
            ? command
            : throw new ArgumentException($"Command '{name}' takes {command.InputType!.Name}, not {input.Name}.", nameof(input));
    }

    private async Task RunAsync<TInput>(CommandRegistration command, TInput input, AlvoContext context, CancellationToken cancellationToken)
    {
        await using var scope = scopes.CreateAsyncScope();
        var handler = (IAlvoCommand<TInput>)scope.ServiceProvider.GetRequiredService(command.CommandType!);
        var run = runner.Start(new CommandInvocation(command, context, null, null));

        await runner.FinishAsync(run, async () =>
        {
            await handler.HandleAsync(input, new AlvoCommandContext(run), cancellationToken).ConfigureAwait(false);
            return null;
        }).ConfigureAwait(false);
    }
}
```

In `Commands/Setup.cs`: `services.TryAddSingleton<IAlvoCommands, AlvoCommands>();`.

- [ ] **Step 5: Run the tests, accept the baseline**

Run: `dotnet test --project test/MMLib.Alvo.TestHost.Tests --filter-class '*InternalCommandTests'`
Expected: PASS. The core baseline adds exactly `IAlvoCommand<TInput>`, `IAlvoCommands` and the two `AddCommand<TCommand>` overloads; accept, dispatch `alvo-snapshot-judge`.

- [ ] **Step 6: Format, ring0, commit**

```bash
dotnet format whitespace MMLib.Alvo.slnx --include src/MMLib.Alvo/Commands/IAlvoCommand.cs src/MMLib.Alvo/Commands/IAlvoCommands.cs src/MMLib.Alvo/Commands/Internal/AlvoCommands.cs src/MMLib.Alvo/Commands/Setup.cs src/MMLib.Alvo/Extensions/AlvoExtensionBuilderExtensions.cs test/MMLib.Alvo.TestHost.Tests/InternalCommandTests.cs
scripts/test-ring0
git add src/MMLib.Alvo/Commands/IAlvoCommand.cs src/MMLib.Alvo/Commands/IAlvoCommands.cs src/MMLib.Alvo/Commands/Internal/AlvoCommands.cs src/MMLib.Alvo/Commands/Setup.cs src/MMLib.Alvo/Extensions/AlvoExtensionBuilderExtensions.cs test/MMLib.Alvo.TestHost.Tests/InternalCommandTests.cs test/MMLib.Alvo.Tests/PublicApi.MMLib.Alvo.verified.txt
git commit -m "feat(commands): internal commands invoked in process through IAlvoCommands

Claude-Session: https://claude.ai/code/session_01NJAtafM29iddLwfhb5gnmB"
```

---

## Task 10: Commands in the OpenAPI document — and Vacuum green **[slice end]**

**Files:**
- Create: `src/MMLib.Alvo/Api/Internal/CommandDocumentTransformer.cs`
- Modify: `src/MMLib.Alvo/Api/Setup.cs` (`AlvoOpenApiSetup` adds the transformer), `src/MMLib.Alvo/Api/Internal/AlvoDocumentTransformer.cs` (three helpers made `internal static`), `src/MMLib.Alvo/Api/Internal/DataApiDocumentation.cs` (`CommandRefusals`)
- Test: `test/MMLib.Alvo.Api.Tests/Commands/CommandDocumentTests.cs`, `test/MMLib.Alvo.Api.Invariants.Tests.Integration/DocumentContractTests.cs` (one fact)

**Interfaces:**
- Consumes: Task 8's `CommandEndpointMetadata`, `CommandOperationIds`; `ProblemComponents.AddTo`, `DataApiDocumentation.Response` (`Status`, `SharedId`, `SharedNarrowing`, `Description`).
- Produces: `internal sealed class CommandDocumentTransformer(IOptions<AlvoAuthOptions>) : IOpenApiDocumentTransformer`; `internal static IReadOnlyList<DataApiDocumentation.Response> DataApiDocumentation.CommandRefusals(bool boundToRow)`; `internal static void AlvoDocumentTransformer.AddSharedRefusal(OpenApiDocument, DataApiDocumentation.Response)`, `internal static void AlvoDocumentTransformer.AddCredential(OpenApiDocument, string headerName)`, `internal static List<OpenApiSecurityRequirement> AlvoDocumentTransformer.SecurityOf(OpenApiDocument)`, `internal static OpenApiOperation AlvoDocumentTransformer.FindOperation(OpenApiDocument, ApiDescription)`.

- [ ] **Step 1: Write the failing tests**

`test/MMLib.Alvo.Api.Tests/Commands/CommandDocumentTests.cs`:

```csharp
using System.Text.Json.Nodes;

namespace MMLib.Alvo.Api.Tests.Commands;

/// <summary>Acceptance (7): the OpenAPI document lists a command with Alvo's operationId, its body, and its refusals.</summary>
public sealed class CommandDocumentTests
{
    [Fact]
    public async Task The_document_lists_an_entity_command_with_its_operation_id_body_and_refusals()
    {
        await using var world = await DocumentedAsync();
        var document = await world.OpenApiDocumentAsync();

        var operation = document["paths"]!["/commands/bookings/{id}/confirm"]!["post"]!.AsObject();

        operation["operationId"]!.GetValue<string>().ShouldBe("commands.confirmBooking");
        operation["tags"]!.AsArray().Select(tag => tag!.GetValue<string>()).ShouldBe(["commands"]);
        var responses = operation["responses"]!.AsObject();
        responses.Select(response => response.Key).ShouldBe(["200", "401", "403", "404", "409", "412", "422", "508"], ignoreOrder: true);
        responses["404"]!["$ref"]!.GetValue<string>().ShouldBe("#/components/responses/notFound");
        operation["security"]!.AsArray().Count.ShouldBe(2, "anonymous, or the API key — as every Alvo route");
    }

    [Fact]
    public async Task An_unbound_command_documents_its_request_body_and_no_404()
    {
        await using var world = await DocumentedAsync();
        var document = await world.OpenApiDocumentAsync();

        var operation = document["paths"]!["/commands/quote"]!["post"]!.AsObject();

        operation["operationId"]!.GetValue<string>().ShouldBe("commands.quoteStay");
        operation["requestBody"].ShouldNotBeNull();
        operation["responses"]!.AsObject().ContainsKey("404").ShouldBeFalse();
        operation["responses"]!.AsObject().ContainsKey("400").ShouldBeTrue("a body that cannot be bound is answered before Alvo runs");
    }

    [Fact]
    public async Task A_command_only_document_publishes_exactly_the_components_its_commands_reference()
    {
        await using var world = await DocumentedAsync();
        var document = await world.OpenApiDocumentAsync();

        var referenced = document["paths"]!.AsObject()
            .Where(path => path.Key.StartsWith("/commands", StringComparison.Ordinal))
            .SelectMany(path => path.Value!["post"]!["responses"]!.AsObject())
            .Select(response => response.Value!["$ref"]?.GetValue<string>())
            .OfType<string>()
            .Select(reference => reference["#/components/responses/".Length..])
            .ToHashSet(StringComparer.Ordinal);

        referenced.ShouldBeSubsetOf(document["components"]!["responses"]!.AsObject().Select(response => response.Key));
    }

    private static Task<AlvoApiWorld> DocumentedAsync() =>
        AlvoApiWorld.FromDescriptorAsync("command-desk.alvo.json", [CommandWorld.Staff], new AlvoApiWorldSetup(
            MapOpenApiDocument: true,
            ConfigureServices: services => services.AddSingleton(new CommandProbe()).AddSingleton<PaymentGateway>(),
            ConfigureApp: app =>
            {
                app.MapAlvoCommand("confirm_booking", "/commands/bookings/{id:guid}/confirm", "bookings", "'staff' in @user.roles", (Guid id) => TypedResults.Ok(id));
                app.MapAlvoCommand("quote_stay", "/commands/quote", "true", (StayRequest request) => TypedResults.Ok(new Quote(request.Nights)));
            }));
}
```

(add `using Microsoft.AspNetCore.Builder; using Microsoft.AspNetCore.Http; using Microsoft.Extensions.DependencyInjection;`.)

Append to `test/MMLib.Alvo.Api.Invariants.Tests.Integration/DocumentContractTests.cs`:

```csharp
    /// <summary>
    /// Acceptance (7): a document that lists commands beside the generated routes still holds the ruleset — every command
    /// operation's operationId, responses and refusals included.
    /// </summary>
    [Fact]
    public async Task A_document_with_commands_holds_the_ruleset()
    {
        var descriptor = Path.Combine(RepositoryRoot.Find(), "examples", "vehicle-registry", "vehicles.alvo.json");
        await using var world = await AlvoApiWorld.FromDescriptorPathAsync(descriptor, [Admin], _documented with
        {
            ConfigureApp = app =>
            {
                app.MapAlvoCommand("inspect_vehicle", "/commands/vehicles/{id:guid}/inspect", "vehicles", "'inspector' in @user.roles", (Guid id) => TypedResults.Ok(id));
                app.MapAlvoCommand("fleet.recount", "/commands/fleet/recount", "'admin' in @user.roles", () => TypedResults.NoContent());
            },
        });
        var document = await world.OpenApiDocumentAsync();

        var violations = VacuumRunner.Lint(document, "vehicles-with-commands");

        violations.ShouldBeEmpty(string.Join("; ", violations));
        document["paths"]!["/commands/fleet/recount"]!["post"]!["operationId"]!.GetValue<string>().ShouldBe("commands.fleetRecount");
    }
```

(add `using Microsoft.AspNetCore.Builder; using Microsoft.AspNetCore.Http;`.)

- [ ] **Step 2: Run them to verify they fail**

Run: `dotnet test --project test/MMLib.Alvo.Api.Tests --filter-class '*CommandDocumentTests'`
Expected: FAIL — the operation has ASP.NET's own operationId (the endpoint name from `WithName`) but no refusals and no security.

- [ ] **Step 3: The shared pieces, reachable**

In `DataApiDocumentation`, add:

```csharp
    /// <summary>
    /// The refusals every command can answer with (Ruling 45): 401, 403, 404 for a command bound to a row, 409, 412 (a
    /// precondition its handler's own write sent), 422 and 508 (the chain-depth limit).
    /// </summary>
    /// <param name="boundToRow">Whether the command reads a row its route names.</param>
    internal static IReadOnlyList<Response> CommandRefusals(bool boundToRow) =>
        boundToRow
            ? [Unauthenticated, Forbidden, Absent, Conflict, Precondition, Malformed, LoopDetected]
            : [Unauthenticated, Forbidden, Conflict, Precondition, Malformed, LoopDetected];

    /// <summary>The chain-depth refusal; published only where a command can answer it, so the Data API's document is unchanged.</summary>
    private static Response LoopDetected => new(
        StatusCodes.Status508LoopDetected,
        ResponseBody.Problem,
        "The request would extend a chain of writes, events and handlers past the configured depth. It is not retryable: "
        + "'retryable' is false. Break the loop rather than retrying.",
        SharedId: "loopDetected");
```

In `AlvoDocumentTransformer`, turn the pieces the command transformer reuses into `internal static`:

```csharp
    internal static void AddSharedRefusal(OpenApiDocument document, DataApiDocumentation.Response refusal)
    {
        if (document.Components?.Responses?.ContainsKey(refusal.SharedId!) != true)
        {
            document.AddComponent(refusal.SharedId!, Response(refusal, entity: null, document));
        }
    }

    internal static void AddCredential(OpenApiDocument document, string headerName)
    {
        if (document.Components?.SecuritySchemes?.ContainsKey(CredentialScheme) != true)
        {
            document.AddComponent(CredentialScheme, CredentialFor(headerName));
        }
    }
```

— `Reusable`'s loop calls `AddSharedRefusal`; `Credential()` becomes `private static OpenApiSecurityScheme CredentialFor(string headerName)` (the body unchanged, `Name = headerName`) and the `TransformAsync` call site becomes `AddCredential(document, auth.Value.HeaderName)`; rename `Security` to `internal static … SecurityOf` and `Find` to `internal static OpenApiOperation FindOperation(OpenApiDocument document, ApiDescription description)` (taking the description rather than the private `Endpoint` record). `OpenApiDocumentTests.The_document_is_stable` must stay byte-identical.

- [ ] **Step 4: The command transformer**

`src/MMLib.Alvo/Api/Internal/CommandDocumentTransformer.cs`:

```csharp
using Microsoft.AspNetCore.Mvc.ApiExplorer;
using Microsoft.AspNetCore.OpenApi;
using Microsoft.Extensions.Options;
using Microsoft.OpenApi;
using MMLib.Alvo.Auth;
using MMLib.Alvo.Commands.Internal;

namespace MMLib.Alvo.Api.Internal;

/// <summary>
/// Gives every command operation Alvo's shape: the operationId <c>commands.&lt;name&gt;</c>, the tag <c>commands</c>, the
/// refusals the command filter can answer with — referenced from the shared components, added here if the Data API added
/// none — and the anonymous-or-API-key security every Alvo route documents.
/// </summary>
/// <remarks>Runs after <see cref="AlvoDocumentTransformer"/>, so it adds only the components that one did not.</remarks>
internal sealed class CommandDocumentTransformer(IOptions<AlvoAuthOptions> auth) : IOpenApiDocumentTransformer
{
    /// <inheritdoc/>
    public Task TransformAsync(OpenApiDocument document, OpenApiDocumentTransformerContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(context);

        var commands = Commands(context);
        if (commands.Count == 0)
        {
            return Task.CompletedTask;
        }

        ProblemComponents.AddTo(document);
        AlvoDocumentTransformer.AddCredential(document, auth.Value.HeaderName);
        foreach (var (description, marker) in commands)
        {
            Enrich(document, description, marker);
        }

        return Task.CompletedTask;
    }

    private static List<(ApiDescription Description, CommandEndpointMetadata Marker)> Commands(OpenApiDocumentTransformerContext context) =>
        [.. context.DescriptionGroups.SelectMany(group => group.Items)
            .Select(description => (Description: description, Marker: description.ActionDescriptor.EndpointMetadata.OfType<CommandEndpointMetadata>().FirstOrDefault()))
            .Where(entry => entry.Marker is not null)
            .Select(entry => (entry.Description, entry.Marker!))];

    private static void Enrich(OpenApiDocument document, ApiDescription description, CommandEndpointMetadata marker)
    {
        var operation = AlvoDocumentTransformer.FindOperation(document, description);
        operation.OperationId = CommandOperationIds.Of(marker.Name);
        operation.Security = AlvoDocumentTransformer.SecurityOf(document);
        operation.Responses ??= new OpenApiResponses();
        if (operation.RequestBody is not null)
        {
            operation.Responses["400"] = new OpenApiResponse
            {
                Description = "The request body could not be bound to the command's input. Answered by ASP.NET Core's minimal APIs before Alvo runs, in the host's own shape — not a problem document Alvo writes.",
            };
        }

        foreach (var refusal in DataApiDocumentation.CommandRefusals(marker.Entity is not null))
        {
            AlvoDocumentTransformer.AddSharedRefusal(document, refusal);
            operation.Responses[refusal.Status.ToString(System.Globalization.CultureInfo.InvariantCulture)] =
                new OpenApiResponseReference(refusal.SharedId!, document) { Description = refusal.SharedNarrowing };
        }
    }
}
```

In `AlvoOpenApiSetup.Configure`, after `options.AddDocumentTransformer<AlvoDocumentTransformer>();`: `options.AddDocumentTransformer<CommandDocumentTransformer>();`.

- [ ] **Step 5: Run the tests**

Run: `dotnet test --project test/MMLib.Alvo.Api.Tests --filter-class '*CommandDocumentTests' --filter-class '*OpenApiDocumentTests'` then `dotnet test --project test/MMLib.Alvo.Api.Invariants.Tests.Integration --filter-class '*DocumentContractTests'` (it resolves the pinned Vacuum via `scripts/ensure-vacuum`).
Expected: PASS, the stable snapshot unchanged. If Vacuum reports a ruleset finding on the command operations, it is a document defect to fix here — never a ruleset mute.

- [ ] **Step 6: Format, ring1, commit**

```bash
dotnet format whitespace MMLib.Alvo.slnx --include src/MMLib.Alvo/Api/Internal/CommandDocumentTransformer.cs src/MMLib.Alvo/Api/Setup.cs src/MMLib.Alvo/Api/Internal/AlvoDocumentTransformer.cs src/MMLib.Alvo/Api/Internal/DataApiDocumentation.cs test/MMLib.Alvo.Api.Tests/Commands/CommandDocumentTests.cs test/MMLib.Alvo.Api.Invariants.Tests.Integration/DocumentContractTests.cs
scripts/test-ring1
git add src/MMLib.Alvo/Api/Internal/CommandDocumentTransformer.cs src/MMLib.Alvo/Api/Setup.cs src/MMLib.Alvo/Api/Internal/AlvoDocumentTransformer.cs src/MMLib.Alvo/Api/Internal/DataApiDocumentation.cs test/MMLib.Alvo.Api.Tests/Commands/CommandDocumentTests.cs test/MMLib.Alvo.Api.Invariants.Tests.Integration/DocumentContractTests.cs
git commit -m "feat(api): commands in the OpenAPI document with Alvo's operation shape

Claude-Session: https://claude.ai/code/session_01NJAtafM29iddLwfhb5gnmB"
```

---

## Task 11: Inbound webhooks — Standard Webhooks verification, dedup and the 7-day sweep **[slice end]**

**Files:**
- Create: `src/MMLib.Alvo/Api/AlvoWebhookReceiverOptions.cs`, `src/MMLib.Alvo/Api/AlvoWebhookEndpointRouteBuilderExtensions.cs`, `src/MMLib.Alvo/Webhooks/Setup.cs`, `src/MMLib.Alvo/Webhooks/Internal/{StandardWebhookSignature.cs,WebhookReceiver.cs,WebhookReceivers.cs,LedgerSweeper.cs,WebhookLog.cs}`
- Modify: `src/MMLib.Alvo/AlvoServiceCollectionExtensions.cs` (`services.AddAlvoWebhooks()`), `src/MMLib.Alvo/Api/Internal/ProblemResultFactory.cs` (`WebhookRefused`, `WebhookInFlight`)
- Test: `test/MMLib.Alvo.Tests/Webhooks/StandardWebhookSignatureTests.cs` (pure — in the core shard, Ruling 42), `test/MMLib.Alvo.Api.Tests/Webhooks/WebhookReceiverTests.cs`
- Baseline: `test/MMLib.Alvo.Tests/PublicApi.MMLib.Alvo.verified.txt`

**Interfaces:**
- Consumes: Task 3's `AlvoCaller.For/Resolve`; Task 4's `IInboundWebhookLedger`, `WebhookClaim`, `WebhookClaimResult`, `InboundWebhookLimits`, `ICommandLedger.SweepAsync`, `CommandLedgerLimits.Retention`; `ISecretStore`, `SecretName.TryParse`, `ProblemResultFactory.Unreadable/Internal`.
- Produces: `public sealed class AlvoWebhookReceiverOptions { TenantId? Tenant; Func<HttpRequest, ReadOnlyMemory<byte>, TenantId?>? ResolveTenant; TimeSpan Tolerance = 5 min; long MaxBodyBytes = 1 048 576; }`; `public static IEndpointConventionBuilder MapAlvoWebhookReceiver(this IEndpointRouteBuilder endpoints, string pattern, string secretName, Delegate handler, Action<AlvoWebhookReceiverOptions>? configure = null)`; `internal static class StandardWebhookSignature` (`IdHeader`, `TimestampHeader`, `SignatureHeader`, `Keys`, `Sign`, `Verifies`); `internal sealed class WebhookReceiver` (`ReceiveAsync`); `internal sealed class WebhookReceivers` (`Add`, `Count`); `internal sealed class LedgerSweeper : BackgroundService` (`SweepOnceAsync` — inbound webhook ids after 7 days, command keys after 24 h; Rulings 41, 49); `ProblemResultFactory.WebhookRefused(string)`, `WebhookInFlight()`.

- [ ] **Step 1: Write the failing tests**

`test/MMLib.Alvo.Tests/Webhooks/StandardWebhookSignatureTests.cs`:

```csharp
using MMLib.Alvo.Webhooks.Internal;
using System.Text;

namespace MMLib.Alvo.Tests.Webhooks;

/// <summary>The Standard Webhooks signature (acceptance (4), first half): <c>v1,</c> HMAC-SHA256 over <c>id.timestamp.body</c>.</summary>
public sealed class StandardWebhookSignatureTests
{
    private const string Secret = "whsec_MfKQ9r8GKYqrTwjUPD8ILPZIo2LaLaSw";
    private const string Id = "msg_p5jXN8AQM9LWM0D4loKWxJek";
    private const long Timestamp = 1614265330;
    private static readonly byte[] _body = Encoding.UTF8.GetBytes("{\"test\": 2432232314}");

    /// <summary>
    /// The reference libraries' own test vector. If this fails the implementation is wrong, not the vector: the signed
    /// content is <c>{id}.{timestamp}.{body}</c> and the key is the base64-decoded secret after <c>whsec_</c>.
    /// </summary>
    [Fact]
    public void A_reference_signature_verifies()
    {
        var keys = StandardWebhookSignature.Keys(Secret);

        StandardWebhookSignature.Sign(keys[0], Id, Timestamp, _body).ShouldBe("v1,g0hM9SsE+OTPJTGt/tmIKtSyZlE3uFJELVlNIOLJ1OE=");
        StandardWebhookSignature.Verifies(keys, Id, Timestamp, _body, "v1,g0hM9SsE+OTPJTGt/tmIKtSyZlE3uFJELVlNIOLJ1OE=").ShouldBeTrue();
    }

    [Fact]
    public void A_body_changed_by_one_byte_is_refused()
    {
        var tampered = (byte[])_body.Clone();
        tampered[^2]++;

        StandardWebhookSignature.Verifies(StandardWebhookSignature.Keys(Secret), Id, Timestamp, tampered, "v1,g0hM9SsE+OTPJTGt/tmIKtSyZlE3uFJELVlNIOLJ1OE=").ShouldBeFalse();
    }

    [Fact]
    public void Another_id_or_timestamp_is_refused()
    {
        var keys = StandardWebhookSignature.Keys(Secret);
        const string Header = "v1,g0hM9SsE+OTPJTGt/tmIKtSyZlE3uFJELVlNIOLJ1OE=";

        StandardWebhookSignature.Verifies(keys, "msg_other", Timestamp, _body, Header).ShouldBeFalse();
        StandardWebhookSignature.Verifies(keys, Id, Timestamp + 1, _body, Header).ShouldBeFalse();
    }

    [Fact]
    public void A_header_with_an_old_and_a_rotated_signature_verifies_on_the_rotated_one()
    {
        var rotated = StandardWebhookSignature.Keys("whsec_" + Convert.ToBase64String(Encoding.UTF8.GetBytes("the-rotated-secret-of-32-bytes!!")));
        var signedWithRotated = StandardWebhookSignature.Sign(rotated[0], Id, Timestamp, _body);

        StandardWebhookSignature.Verifies(rotated, Id, Timestamp, _body, $"v1,c3RhbGU= {signedWithRotated}").ShouldBeTrue();
    }

    [Fact]
    public void A_stored_value_holding_two_secrets_accepts_a_signature_by_either()
    {
        var other = "whsec_" + Convert.ToBase64String(Encoding.UTF8.GetBytes("the-next-secret-of-thirty-two-b!"));
        var keys = StandardWebhookSignature.Keys($"{other} {Secret}");

        StandardWebhookSignature.Verifies(keys, Id, Timestamp, _body, "v1,g0hM9SsE+OTPJTGt/tmIKtSyZlE3uFJELVlNIOLJ1OE=").ShouldBeTrue();
    }

    [Fact]
    public void Entries_of_another_version_are_ignored() =>
        StandardWebhookSignature.Verifies(StandardWebhookSignature.Keys(Secret), Id, Timestamp, _body, "v1a,g0hM9SsE+OTPJTGt/tmIKtSyZlE3uFJELVlNIOLJ1OE=").ShouldBeFalse();

    [Theory]
    [InlineData("")]
    [InlineData("v1,")]
    [InlineData("v1,not-base64!")]
    [InlineData("garbage")]
    public void A_malformed_header_verifies_nothing_and_throws_nothing(string header) =>
        StandardWebhookSignature.Verifies(StandardWebhookSignature.Keys(Secret), Id, Timestamp, _body, header).ShouldBeFalse();
}
```

`test/MMLib.Alvo.Api.Tests/Webhooks/WebhookReceiverTests.cs`:

```csharp
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Time.Testing;
using MMLib.Alvo.Api;
using MMLib.Alvo.Secrets;
using MMLib.Alvo.Webhooks.Internal;
using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;

namespace MMLib.Alvo.Api.Tests.Webhooks;

/// <summary>
/// Inbound webhooks over HTTP (spec 2026-10-14 §4.9, acceptance (4)): verified, inside the window, once per
/// <c>(pattern, webhook-id)</c>, run as the system in the configured tenant, and forgotten after seven days.
/// </summary>
public sealed class WebhookReceiverTests
{
    private const string Secret = "whsec_MfKQ9r8GKYqrTwjUPD8ILPZIo2LaLaSw";
    private const string TenantText = "7b8f0c4e-2b8e-4f6f-9b1e-0c7a2d1f3e55";
    private static readonly TenantId _tenant = new(Guid.Parse(TenantText));
    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 10, 30, 9, 0, 0, TimeSpan.Zero));
    private readonly Probe _probe = new();

    [Fact]
    public async Task A_signed_delivery_runs_the_handler_as_the_system_in_the_configured_tenant()
    {
        await using var world = await StartAsync();

        using var response = await SendAsync(world, "/hooks/payments", "msg_1", "{\"paid\":true}");

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var (body, caller) = _probe.Seen.ShouldHaveSingleItem();
        body.ShouldBe("{\"paid\":true}", "the handler binds the very bytes that were verified");
        caller.ActorKind.ShouldBe(AlvoActorKind.System);
        caller.Tenant.ShouldBe(_tenant);
    }

    [Theory]
    [InlineData(-6)]
    [InlineData(6)]
    public async Task A_timestamp_six_minutes_off_is_refused(int minutes)
    {
        await using var world = await StartAsync();

        using var response = await SendAsync(world, "/hooks/payments", "msg_2", "{}", _time.GetUtcNow().AddMinutes(minutes));

        response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        (await response.ReadProblemTypeAsync()).ShouldBe(AlvoProblemTypes.Unauthenticated);
        response.Headers.WwwAuthenticate.ShouldBeEmpty("Standard Webhooks has no challenge to name");
        _probe.Seen.ShouldBeEmpty();
    }

    [Fact]
    public async Task A_repeated_webhook_id_on_one_receiver_does_not_run_the_handler_again()
    {
        await using var world = await StartAsync();

        using var first = await SendAsync(world, "/hooks/payments", "msg_3", "{}");
        using var repeat = await SendAsync(world, "/hooks/payments", "msg_3", "{}");
        using var elsewhere = await SendAsync(world, "/hooks/refunds", "msg_3", "{}");

        first.StatusCode.ShouldBe(HttpStatusCode.OK);
        repeat.StatusCode.ShouldBe(HttpStatusCode.OK, "a handled id is acknowledged, so the sender stops retrying");
        elsewhere.StatusCode.ShouldBe(HttpStatusCode.OK);
        _probe.Seen.Count.ShouldBe(2, "dedup is per receiver: the second receiver runs the same id once");
    }

    [Fact]
    public async Task A_bad_signature_is_401_and_records_nothing()
    {
        await using var world = await StartAsync();

        using var forged = await SendAsync(world, "/hooks/payments", "msg_4", "{}", signature: "v1,Zm9yZ2Vk");
        using var genuine = await SendAsync(world, "/hooks/payments", "msg_4", "{}");

        forged.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        genuine.StatusCode.ShouldBe(HttpStatusCode.OK, "an unverified id was never recorded, so the genuine delivery runs");
        _probe.Seen.Count.ShouldBe(1);
        _probe.TenantsResolved.ShouldBe(1, "the tenant is resolved only for a verified delivery");
    }

    [Fact]
    public async Task A_failing_handler_gives_its_id_back_so_the_sender_s_retry_runs()
    {
        _probe.FailNext = true;
        await using var world = await StartAsync();

        using var failed = await SendAsync(world, "/hooks/payments", "msg_5", "{}");
        using var retried = await SendAsync(world, "/hooks/payments", "msg_5", "{}");

        failed.StatusCode.ShouldBe(HttpStatusCode.InternalServerError);
        retried.StatusCode.ShouldBe(HttpStatusCode.OK);
        _probe.Seen.Count.ShouldBe(1);
    }

    [Fact]
    public async Task A_missing_secret_is_a_500_that_names_nothing()
    {
        await using var world = await StartAsync(secret: null);

        using var response = await SendAsync(world, "/hooks/payments", "msg_6", "{}");

        response.StatusCode.ShouldBe(HttpStatusCode.InternalServerError);
        (await response.ReadTextAsync()).ShouldNotContain("pay-provider");
    }

    [Fact]
    public async Task A_body_over_the_limit_is_413()
    {
        await using var world = await StartAsync();

        using var response = await SendAsync(world, "/hooks/payments", "msg_7", "{\"pad\":\"" + new string('x', 2_000) + "\"}");

        response.StatusCode.ShouldBe(HttpStatusCode.RequestEntityTooLarge);
        _probe.Seen.ShouldBeEmpty();
    }

    [Fact]
    public async Task A_resolver_reading_the_verified_body_leaves_it_for_the_handler()
    {
        await using var world = await StartAsync();
        var body = $"{{\"tenant\":\"{TenantText}\",\"paid\":true}}";

        using var response = await SendAsync(world, "/hooks/orders", "msg_9", body);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var (seen, caller) = _probe.Seen.ShouldHaveSingleItem();
        seen.ShouldBe(body, "the resolver read the bytes; the handler still reads the whole body");
        caller.ActorKind.ShouldBe(AlvoActorKind.System);
        caller.Tenant.ShouldBe(_tenant, "the tenant came from the signed body");
    }

    [Fact]
    public async Task A_receiver_with_no_tenant_and_no_resolver_runs_the_handler_as_the_system_in_none()
    {
        await using var world = await StartAsync();

        using var response = await SendAsync(world, "/hooks/plain", "msg_10", "{}");

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var (_, caller) = _probe.Seen.ShouldHaveSingleItem();
        caller.ActorKind.ShouldBe(AlvoActorKind.System);
        caller.Tenant.ShouldBeNull("System(null) — which every tenant-scoped entity refuses");
    }

    [Fact]
    public async Task Rows_older_than_seven_days_are_swept()
    {
        await using var world = await StartAsync();
        using var first = await SendAsync(world, "/hooks/payments", "msg_8", "{}");
        _time.Advance(TimeSpan.FromDays(7) + TimeSpan.FromHours(1));

        _ = await world.Services.GetServices<IHostedService>().OfType<LedgerSweeper>().Single()
            .SweepOnceAsync(TestContext.Current.CancellationToken);
        using var redelivered = await SendAsync(world, "/hooks/payments", "msg_8", "{}");

        redelivered.StatusCode.ShouldBe(HttpStatusCode.OK);
        _probe.Seen.Count.ShouldBe(2, "a swept id is forgotten — which is why the window is far shorter than the retention");
    }

    private Task<AlvoApiWorld> StartAsync(string? secret = Secret) =>
        AlvoApiWorld.FromDescriptorAsync("command-desk.alvo.json", [], new AlvoApiWorldSetup(
            MapAlvoProblemDetails: true,
            ConfigureServices: services =>
            {
                services.AddSingleton<TimeProvider>(_time);
                services.AddSingleton(_probe);
                services.Configure<AlvoSecretOptions>(options =>
                {
                    if (secret is not null)
                    {
                        options.Values["pay-provider"] = secret;
                    }
                });
            },
            ConfigureApp: app =>
            {
                app.MapAlvoWebhookReceiver("/hooks/payments", "pay-provider", Handle, options =>
                {
                    options.ResolveTenant = (_, _) => _probe.Resolve(_tenant);
                    options.MaxBodyBytes = 1_024;
                });
                app.MapAlvoWebhookReceiver("/hooks/refunds", "pay-provider", Handle, options => options.Tenant = _tenant);
                app.MapAlvoWebhookReceiver("/hooks/orders", "pay-provider", Handle, options => options.ResolveTenant = TenantInBody);
                app.MapAlvoWebhookReceiver("/hooks/plain", "pay-provider", Handle);
            }));

    /// <summary>The documented way (Ruling 37): the tenant from the signed body, read from the bytes the receiver passes.</summary>
    private static TenantId? TenantInBody(HttpRequest request, ReadOnlyMemory<byte> body)
    {
        using var json = JsonDocument.Parse(body);
        return json.RootElement.TryGetProperty("tenant", out var tenant) ? new TenantId(tenant.GetGuid()) : null;
    }

    private static async Task<IResult> Handle(HttpRequest request, AlvoCaller caller, Probe probe)
    {
        using var reader = new StreamReader(request.Body, Encoding.UTF8);
        var body = await reader.ReadToEndAsync();
        if (probe.FailNext)
        {
            probe.FailNext = false;
            throw new InvalidOperationException("the handler failed");
        }

        probe.Seen.Enqueue((body, caller.Context));
        return Results.Ok();
    }

    private Task<HttpResponseMessage> SendAsync(AlvoApiWorld world, string path, string id, string body, DateTimeOffset? at = null, string? signature = null)
    {
        var timestamp = (at ?? _time.GetUtcNow()).ToUnixTimeSeconds();
        var bytes = Encoding.UTF8.GetBytes(body);
        var signed = signature ?? StandardWebhookSignature.Sign(StandardWebhookSignature.Keys(Secret)[0], id, timestamp, bytes);

        return world.SendRawAsync(HttpMethod.Post, path, content: AlvoApiWorld.RawJson(body), headers:
        [
            new(StandardWebhookSignature.IdHeader, id),
            new(StandardWebhookSignature.TimestampHeader, timestamp.ToString(CultureInfo.InvariantCulture)),
            new(StandardWebhookSignature.SignatureHeader, signed),
        ]);
    }

    private sealed class Probe
    {
        private int _resolved;

        internal ConcurrentQueue<(string Body, AlvoContext Caller)> Seen { get; } = new();

        internal bool FailNext { get; set; }

        internal int TenantsResolved => Volatile.Read(ref _resolved);

        internal TenantId Resolve(TenantId tenant)
        {
            Interlocked.Increment(ref _resolved);
            return tenant;
        }
    }
}
```

(`ShouldHaveSingleItem()` on a `ConcurrentQueue` of tuples deconstructs as shown; the handler's `Probe` parameter is resolved from DI — it is registered as a singleton. The sweep fact does not assert the count its own call swept: advancing the fake clock also ticks the hosted sweeper's timer, which may sweep first — the fact is that the id is forgotten.)

- [ ] **Step 2: Run them to verify they fail**

Run: `dotnet test --project test/MMLib.Alvo.Tests --filter-class '*StandardWebhookSignatureTests'` and `dotnet test --project test/MMLib.Alvo.Api.Tests --filter-class '*WebhookReceiverTests'`
Expected: FAIL to compile.

- [ ] **Step 3: The signature**

`src/MMLib.Alvo/Webhooks/Internal/StandardWebhookSignature.cs`:

```csharp
using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace MMLib.Alvo.Webhooks.Internal;

/// <summary>
/// The Standard Webhooks symmetric signature: <c>v1,</c> + base64(HMAC-SHA256(key, <c>{id}.{timestamp}.{body}</c>)), the key
/// being the base64-decoded secret after its <c>whsec_</c> prefix. A header may carry several space-separated signatures
/// (the sender's zero-downtime rotation); entries of another version (<c>v1a</c>, asymmetric) are ignored.
/// </summary>
internal static class StandardWebhookSignature
{
    internal const string IdHeader = "webhook-id";
    internal const string TimestampHeader = "webhook-timestamp";
    internal const string SignatureHeader = "webhook-signature";

    private const string SecretPrefix = "whsec_";
    private const string Version = "v1";

    /// <summary>The keys a stored secret value holds: one, or several separated by spaces (the receiver's rotation, Ruling 22).</summary>
    /// <exception cref="FormatException">A secret is not base64.</exception>
    internal static IReadOnlyList<byte[]> Keys(string stored)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(stored);

        return [.. stored.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Select(Key)];
    }

    /// <summary>The <c>v1,…</c> signature of one delivery.</summary>
    internal static string Sign(byte[] key, string id, long timestamp, ReadOnlySpan<byte> body) =>
        $"{Version},{Convert.ToBase64String(Mac(key, id, timestamp, body))}";

    /// <summary>Whether any <c>v1</c> signature in <paramref name="header"/> is one of <paramref name="keys"/>' — compared in fixed time.</summary>
    internal static bool Verifies(IReadOnlyList<byte[]> keys, string id, long timestamp, ReadOnlySpan<byte> body, string header)
    {
        var offered = Offered(header);
        foreach (var key in keys)
        {
            var expected = Mac(key, id, timestamp, body);
            if (offered.Any(signature => CryptographicOperations.FixedTimeEquals(signature, expected)))
            {
                return true;
            }
        }

        return false;
    }

    private static List<byte[]> Offered(string header) =>
        [.. header.Split(' ', StringSplitOptions.RemoveEmptyEntries).Select(Entry).OfType<byte[]>()];

    private static byte[]? Entry(string entry)
    {
        var comma = entry.IndexOf(',', StringComparison.Ordinal);
        if (comma < 0 || !string.Equals(entry[..comma], Version, StringComparison.Ordinal))
        {
            return null;
        }

        var buffer = new byte[entry.Length];
        return Convert.TryFromBase64String(entry[(comma + 1)..], buffer, out var written) && written > 0 ? buffer[..written] : null;
    }

    private static byte[] Key(string secret) =>
        Convert.FromBase64String(secret.StartsWith(SecretPrefix, StringComparison.Ordinal) ? secret[SecretPrefix.Length..] : secret);

    private static byte[] Mac(byte[] key, string id, long timestamp, ReadOnlySpan<byte> body)
    {
        var prefix = Encoding.UTF8.GetBytes(string.Create(CultureInfo.InvariantCulture, $"{id}.{timestamp}."));
        var content = new byte[prefix.Length + body.Length];
        prefix.CopyTo(content, 0);
        body.CopyTo(content.AsSpan(prefix.Length));

        return HMACSHA256.HashData(key, content);
    }
}
```

- [ ] **Step 4: The receiver, its registry, its sweep, its log**

`src/MMLib.Alvo/Api/AlvoWebhookReceiverOptions.cs`:

```csharp
using Microsoft.AspNetCore.Http;

namespace MMLib.Alvo.Api;

/// <summary>How one inbound webhook receiver verifies a delivery and whom its handler runs as.</summary>
public sealed class AlvoWebhookReceiverOptions
{
    /// <summary>Gets or sets the tenant the handler runs in, as the system; null for none.</summary>
    public TenantId? Tenant { get; set; }

    /// <summary>
    /// Gets or sets a function that names the tenant of a delivery from the request and the verified body bytes. It is
    /// called only for a delivery whose signature verified; a tenant it answers wins over <see cref="Tenant"/>, and null
    /// falls back to it. With neither, the handler runs as the system in no tenant.
    /// </summary>
    /// <remarks>
    /// Standard Webhooks signs the id, the timestamp and the body — not the path, the query or any other header. Derive
    /// the tenant from the signed body (the bytes passed in), or map one receiver per tenant with its own secret and
    /// <see cref="Tenant"/>. A tenant read from the path or a header can be changed by anyone who sees one signed
    /// delivery, without breaking its signature (Ruling 37). The request body is replayed after the call, so the handler
    /// still reads it whole.
    /// </remarks>
    public Func<HttpRequest, ReadOnlyMemory<byte>, TenantId?>? ResolveTenant { get; set; }

    /// <summary>Gets or sets how far <c>webhook-timestamp</c> may be from now, either way; 5 minutes, at most 1 hour.</summary>
    public TimeSpan Tolerance { get; set; } = TimeSpan.FromMinutes(5);

    /// <summary>Gets or sets the largest body accepted, in bytes; 1 MiB, at most 64 MiB. A larger one is answered 413.</summary>
    public long MaxBodyBytes { get; set; } = 1_048_576;

    internal AlvoWebhookReceiverOptions Validated()
    {
        if (Tolerance <= TimeSpan.Zero || Tolerance > TimeSpan.FromHours(1))
        {
            throw new ArgumentOutOfRangeException(nameof(Tolerance), Tolerance, "A webhook timestamp tolerance is greater than zero and at most one hour.");
        }

        return MaxBodyBytes is > 0 and <= 67_108_864
            ? this
            : throw new ArgumentOutOfRangeException(nameof(MaxBodyBytes), MaxBodyBytes, "A webhook body limit is greater than zero and at most 64 MiB.");
    }
}
```

`src/MMLib.Alvo/Webhooks/Internal/WebhookReceivers.cs`:

```csharp
namespace MMLib.Alvo.Webhooks.Internal;

/// <summary>The receivers this host mapped: one per pattern.</summary>
internal sealed class WebhookReceivers
{
    private readonly HashSet<string> _patterns = new(StringComparer.Ordinal);
    private readonly Lock _gate = new();

    internal int Count
    {
        get
        {
            lock (_gate)
            {
                return _patterns.Count;
            }
        }
    }

    /// <exception cref="InvalidOperationException">A receiver is already mapped at <paramref name="pattern"/>.</exception>
    internal void Add(string pattern)
    {
        lock (_gate)
        {
            if (!_patterns.Add(pattern))
            {
                throw new InvalidOperationException($"A webhook receiver is already mapped at '{pattern}'; its ids would be deduplicated together. Map each receiver once.");
            }
        }
    }
}
```

`src/MMLib.Alvo/Webhooks/Internal/WebhookLog.cs`:

```csharp
using Microsoft.Extensions.Logging;

namespace MMLib.Alvo.Webhooks.Internal;

/// <summary>The inbound webhook log records. They name the receiver's pattern and the secret's name — never an id, a body, a secret or a signature.</summary>
internal static partial class WebhookLog
{
    [LoggerMessage(Level = LogLevel.Warning, Message = "Webhook receiver '{Receiver}' refused a delivery: {Reason}.")]
    internal static partial void Refused(ILogger logger, string receiver, string reason);

    [LoggerMessage(Level = LogLevel.Information, Message = "Webhook receiver '{Receiver}' acknowledged a delivery it had already handled.")]
    internal static partial void Duplicate(ILogger logger, string receiver);

    [LoggerMessage(Level = LogLevel.Error, Message = "Webhook receiver '{Receiver}' cannot verify deliveries: the secret '{Secret}' is not stored, or is not base64.")]
    internal static partial void SecretUnusable(ILogger logger, string receiver, string secret);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Webhook receiver '{Receiver}' handled a delivery after its claim lapsed to another run; that run records the id.")]
    internal static partial void ClaimLost(ILogger logger, string receiver);

    [LoggerMessage(Level = LogLevel.Information, Message = "Swept {Webhooks} inbound webhook ids older than seven days and {Commands} command keys older than a day.")]
    internal static partial void Swept(ILogger logger, int webhooks, int commands);

    [LoggerMessage(Level = LogLevel.Warning, Message = "The ledger sweep failed; it runs again in an hour.")]
    internal static partial void SweepFailed(ILogger logger, Exception failure);
}
```

`src/MMLib.Alvo/Webhooks/Internal/WebhookReceiver.cs`:

```csharp
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using MMLib.Alvo.Api;
using MMLib.Alvo.Api.Internal;
using MMLib.Alvo.Data;
using MMLib.Alvo.Secrets;
using System.Globalization;
using System.Text;

namespace MMLib.Alvo.Webhooks.Internal;

/// <summary>
/// One receiver: verify (headers, window, body size, signature), then claim the id, then run the host's handler as the
/// system in the resolved tenant over the very bytes that were verified. Verification precedes the claim and the tenant
/// (Ruling 24): an unverified request records nothing and runs no host code.
/// </summary>
internal sealed class WebhookReceiver(
    string pattern,
    SecretName secret,
    AlvoWebhookReceiverOptions options,
    RequestDelegate handler,
    ISecretStore secrets,
    IInboundWebhookLedger ledger,
    TimeProvider time,
    ILogger<WebhookReceiver> logger)
{
    private sealed record Signed(string Id, long Timestamp, string Signature);

    /// <summary>Receives one delivery.</summary>
    internal async Task ReceiveAsync(HttpContext http)
    {
        ArgumentNullException.ThrowIfNull(http);
        var (signed, body, refusal) = await VerifiedAsync(http).ConfigureAwait(false);
        if (refusal is not null)
        {
            await refusal.ExecuteAsync(http).ConfigureAwait(false);
            return;
        }

        await ClaimedAsync(http, signed!, body!).ConfigureAwait(false);
    }

    private async Task<(Signed?, byte[]?, IResult?)> VerifiedAsync(HttpContext http)
    {
        if (Headers(http.Request) is not { } signed)
        {
            return (null, null, Refused("it lacks one of the webhook-id, webhook-timestamp and webhook-signature headers, or one is malformed"));
        }

        if (!Fresh(signed.Timestamp))
        {
            return (null, null, Refused($"its timestamp is more than {options.Tolerance.TotalMinutes:0} minutes from now"));
        }

        if (await BodyAsync(http.Request).ConfigureAwait(false) is not { } body)
        {
            return (null, null, ProblemResultFactory.Unreadable(StatusCodes.Status413PayloadTooLarge));
        }

        return await KeysAsync(http.RequestAborted).ConfigureAwait(false) switch
        {
            null => (null, null, ProblemResultFactory.Internal()),
            var keys when StandardWebhookSignature.Verifies(keys, signed.Id, signed.Timestamp, body, signed.Signature) => (signed, body, null),
            _ => (null, null, Refused("no signature in it verifies with the receiver's secret")),
        };
    }

    private async Task ClaimedAsync(HttpContext http, Signed signed, byte[] body)
    {
        var claim = await ledger.ClaimAsync(pattern, signed.Id, time.GetUtcNow(), CancellationToken.None).ConfigureAwait(false);
        switch (claim.State)
        {
            case WebhookClaim.Completed:
                WebhookLog.Duplicate(logger, pattern);
                http.Response.StatusCode = StatusCodes.Status200OK;
                return;
            case WebhookClaim.InFlight:
                await ProblemResultFactory.WebhookInFlight().ExecuteAsync(http).ConfigureAwait(false);
                return;
            default:
                await RunAsync(http, signed.Id, claim.Token!, body).ConfigureAwait(false);
                return;
        }
    }

    /// <summary>
    /// Runs the handler under the claim's fencing token (Ruling 36): the completion and the release name the token, so a
    /// run that outlived its lease neither records the id another run now holds nor frees that run's claim.
    /// </summary>
    private async Task RunAsync(HttpContext http, string id, string token, byte[] body)
    {
        try
        {
            AlvoCaller.For(http).Resolve(AlvoContext.System(TenantOf(http.Request, body)));
            Replay(http.Request, body);
            await handler(http).ConfigureAwait(false);
        }
        catch
        {
            await ledger.ReleaseAsync(pattern, id, token, CancellationToken.None).ConfigureAwait(false);
            throw;
        }

        if (http.Response.StatusCode is not (>= 200 and < 300))
        {
            await ledger.ReleaseAsync(pattern, id, token, CancellationToken.None).ConfigureAwait(false);
        }
        else if (!await ledger.CompleteAsync(pattern, id, token, time.GetUtcNow(), CancellationToken.None).ConfigureAwait(false))
        {
            WebhookLog.ClaimLost(logger, pattern);
        }
    }

    /// <summary>
    /// The tenant from the verified bytes (Ruling 37). The body is replayed before the resolver too, so one that reads
    /// <c>request.Body</c> instead of the bytes it is given reads the same verified content.
    /// </summary>
    private TenantId? TenantOf(HttpRequest request, byte[] body)
    {
        if (options.ResolveTenant is not { } resolve)
        {
            return options.Tenant;
        }

        Replay(request, body);
        return resolve(request, body) ?? options.Tenant;
    }

    private static void Replay(HttpRequest request, byte[] body)
    {
        request.Body = new MemoryStream(body, writable: false);
        request.ContentLength = body.Length;
    }

    private static Signed? Headers(HttpRequest request)
    {
        var id = Single(request, StandardWebhookSignature.IdHeader);
        var signature = Single(request, StandardWebhookSignature.SignatureHeader);
        var timestamp = Single(request, StandardWebhookSignature.TimestampHeader);

        return id is not null && signature is not null && Encoding.UTF8.GetByteCount(id) <= InboundWebhookLimits.MaxIdBytes
            && long.TryParse(timestamp, NumberStyles.None, CultureInfo.InvariantCulture, out var seconds)
            ? new Signed(id, seconds, signature)
            : null;
    }

    private static string? Single(HttpRequest request, string header) =>
        request.Headers[header] is { Count: 1 } values && !string.IsNullOrWhiteSpace(values[0]) ? values[0] : null;

    private bool Fresh(long seconds)
    {
        var now = time.GetUtcNow().ToUnixTimeSeconds();
        var tolerance = (long)options.Tolerance.TotalSeconds;
        return seconds >= now - tolerance && seconds <= now + tolerance;
    }

    private async Task<byte[]?> BodyAsync(HttpRequest request)
    {
        if (request.ContentLength > options.MaxBodyBytes)
        {
            return null;
        }

        using var buffer = new MemoryStream();
        var chunk = new byte[16_384];
        int read;
        while ((read = await request.Body.ReadAsync(chunk, request.HttpContext.RequestAborted).ConfigureAwait(false)) > 0)
        {
            if (buffer.Length + read > options.MaxBodyBytes)
            {
                return null;
            }

            buffer.Write(chunk, 0, read);
        }

        return buffer.ToArray();
    }

    private async Task<IReadOnlyList<byte[]>?> KeysAsync(CancellationToken ct)
    {
        try
        {
            return await secrets.GetAsync(secret, ct).ConfigureAwait(false) is { } stored ? StandardWebhookSignature.Keys(stored) : Unusable();
        }
        catch (FormatException)
        {
            return Unusable();
        }
    }

    private IReadOnlyList<byte[]>? Unusable()
    {
        WebhookLog.SecretUnusable(logger, pattern, secret.Value);
        return null;
    }

    private IResult Refused(string reason)
    {
        WebhookLog.Refused(logger, pattern, reason);
        return ProblemResultFactory.WebhookRefused($"The delivery was refused: {reason}.");
    }
}
```

`src/MMLib.Alvo/Webhooks/Internal/LedgerSweeper.cs`:

```csharp
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using MMLib.Alvo.Commands;
using MMLib.Alvo.Data;
using System.Diagnostics.CodeAnalysis;

namespace MMLib.Alvo.Webhooks.Internal;

/// <summary>
/// Once an hour, forgets inbound webhook ids received more than seven days ago (spec 2026-10-14 §4.9; #154 retention) and
/// command keys claimed more than a day ago (Ruling 41): a key replayed after that runs as a new request. One sweep for
/// both framework ledgers, so a host runs one timer, not two.
/// </summary>
internal sealed class LedgerSweeper(IServiceProvider services, TimeProvider time, ILogger<LedgerSweeper> logger) : BackgroundService
{
    /// <summary>Runs one sweep; answers how many ids and keys it forgot.</summary>
    [SuppressMessage("Design", "CA1031:Do not catch general exception types", Justification = "A failed sweep must not stop the host: it is logged and retried an hour later.")]
    internal async Task<int> SweepOnceAsync(CancellationToken cancellationToken)
    {
        try
        {
            var now = time.GetUtcNow();
            var webhooks = services.GetService<IInboundWebhookLedger>() is { } inbound
                ? await inbound.SweepAsync(now - InboundWebhookLimits.Retention, cancellationToken).ConfigureAwait(false)
                : 0;
            var commands = services.GetService<ICommandLedger>() is { } keys
                ? await keys.SweepAsync(now - CommandLedgerLimits.Retention, cancellationToken).ConfigureAwait(false)
                : 0;
            WebhookLog.Swept(logger, webhooks, commands);
            return webhooks + commands;
        }
        catch (Exception failure) when (failure is not OperationCanceledException)
        {
            WebhookLog.SweepFailed(logger, failure);
            return 0;
        }
    }

    /// <inheritdoc/>
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(InboundWebhookLimits.SweepInterval, time);
        do
        {
            await SweepOnceAsync(stoppingToken).ConfigureAwait(false);
        }
        while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false));
    }
}
```

(The sweeper no longer waits for a mapped receiver: a host with commands and no receivers still needs its keys swept, and
a ledger that is not registered — a non-relational provider — is skipped. Each sweep is one bound `DELETE` over an indexed
column; an empty one is cheap. `WebhookReceivers.Count` stays, for the duplicate-pattern check.)

`src/MMLib.Alvo/Webhooks/Setup.cs`:

```csharp
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using MMLib.Alvo.Webhooks.Internal;

namespace MMLib.Alvo.Webhooks;

/// <summary>Registers inbound webhooks; called from <c>AddAlvo</c> and nowhere else.</summary>
internal static class WebhooksSetup
{
    internal static IServiceCollection AddAlvoWebhooks(this IServiceCollection services)
    {
        services.TryAddSingleton<WebhookReceivers>();
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IHostedService, LedgerSweeper>(provider => new LedgerSweeper(
            provider,
            provider.GetService<TimeProvider>() ?? TimeProvider.System,
            provider.GetRequiredService<ILogger<LedgerSweeper>>())));
        return services;
    }
}
```

`src/MMLib.Alvo/Api/AlvoWebhookEndpointRouteBuilderExtensions.cs`:

```csharp
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.Routing.Patterns;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using MMLib.Alvo.Api;
using MMLib.Alvo.Data;
using MMLib.Alvo.Secrets;
using MMLib.Alvo.Webhooks.Internal;

namespace Microsoft.AspNetCore.Builder;

/// <summary>Maps receivers of Standard Webhooks deliveries.</summary>
public static class AlvoWebhookEndpointRouteBuilderExtensions
{
    /// <summary>
    /// Maps a <c>POST</c> receiver that runs <paramref name="handler"/> once per verified Standard Webhooks delivery, as the
    /// system in the tenant <paramref name="configure"/> names.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Before the handler: the <c>webhook-id</c>, <c>webhook-timestamp</c> and <c>webhook-signature</c> headers must be
    /// present, the timestamp within <see cref="AlvoWebhookReceiverOptions.Tolerance"/> of now (5 minutes), the body within
    /// <see cref="AlvoWebhookReceiverOptions.MaxBodyBytes"/>, and one <c>v1,</c> signature must verify with the secret
    /// stored under <paramref name="secretName"/> (several space-separated <c>whsec_</c> secrets are accepted, for rotation).
    /// Otherwise the answer is 401 (413 for the body) and nothing runs.
    /// </para>
    /// <para>
    /// A <c>webhook-id</c> is handled once per receiver: a delivery already handled is acknowledged 200 without running the
    /// handler, one still running is answered 409, and one whose handler failed or answered non-2xx is given back so the
    /// sender's retry runs it. Ids are forgotten after seven days. The handler takes its parameters as any minimal API —
    /// the body binds from the very bytes that were verified — and an <c>AlvoCaller</c> carries the system context.
    /// Delivery is at least once: a handler that wrote and then crashed before its id was recorded runs again, so it should
    /// be idempotent on the id. Receivers are not in the OpenAPI document. They need a relational data provider.
    /// </para>
    /// <para>
    /// The tenant comes from <see cref="AlvoWebhookReceiverOptions.Tenant"/> or from
    /// <see cref="AlvoWebhookReceiverOptions.ResolveTenant"/>, which is given the verified body. Only the body is signed:
    /// do not take the tenant from the path or a header (Ruling 37).
    /// </para>
    /// <para>
    /// The returned builder configures the outer receiver endpoint. Conventions on it — metadata, authorization, rate
    /// limiting — apply to the request as a whole, but an endpoint filter added with <c>AddEndpointFilter</c> never runs:
    /// the handler is built when the receiver is mapped, and the outer endpoint is a plain request delegate. Put such logic
    /// in the handler. The system caller is bound to the handler's <c>AlvoCaller</c> parameter only; it is not published
    /// through <c>IAlvoContextAccessor</c> (unlike <c>WithAlvoCaller()</c> and commands), so pass the
    /// <c>AlvoCaller</c> to the code that needs it (Ruling 49, L8).
    /// </para>
    /// </remarks>
    /// <param name="endpoints">The route builder.</param>
    /// <param name="pattern">The route pattern; it is also the receiver's dedup scope.</param>
    /// <param name="secretName">The secret store name of the signing secret.</param>
    /// <param name="handler">The minimal-API handler.</param>
    /// <param name="configure">The receiver's tenant, window and body limit.</param>
    /// <returns>The endpoint's convention builder.</returns>
    /// <exception cref="ArgumentException"><paramref name="secretName"/> is not a secret name.</exception>
    /// <exception cref="InvalidOperationException">The data provider keeps no webhook ledger, or the pattern is mapped twice.</exception>
    public static IEndpointConventionBuilder MapAlvoWebhookReceiver(
        this IEndpointRouteBuilder endpoints, string pattern, string secretName, Delegate handler, Action<AlvoWebhookReceiverOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(endpoints);
        ArgumentException.ThrowIfNullOrWhiteSpace(pattern);
        ArgumentNullException.ThrowIfNull(handler);
        var secret = SecretName.TryParse(secretName, out var name) ? name! : throw new ArgumentException(
            $"'{secretName}' is not a secret name: lower-case letters, digits, '.', '_' and '-', starting with a letter.", nameof(secretName));
        var options = new AlvoWebhookReceiverOptions();
        configure?.Invoke(options);

        var receiver = Receiver(endpoints, pattern, secret, options.Validated(), Inner(endpoints, pattern, handler));
        endpoints.ServiceProvider.GetRequiredService<WebhookReceivers>().Add(pattern);

        return endpoints.MapPost(pattern, (RequestDelegate)receiver.ReceiveAsync).ExcludeFromDescription();
    }

    private static WebhookReceiver Receiver(
        IEndpointRouteBuilder endpoints, string pattern, SecretName secret, AlvoWebhookReceiverOptions options, RequestDelegate handler)
    {
        var services = endpoints.ServiceProvider;
        var ledger = services.GetService<IInboundWebhookLedger>() ?? throw new InvalidOperationException(
            "Inbound webhooks need a relational data provider to remember which deliveries they handled. Register UseSqlite(…) or UsePostgreSql(…).");

        return new WebhookReceiver(
            pattern, secret, options, handler, services.GetRequiredService<ISecretStore>(), ledger,
            services.GetService<TimeProvider>() ?? TimeProvider.System, services.GetRequiredService<ILogger<WebhookReceiver>>());
    }

    private static RequestDelegate Inner(IEndpointRouteBuilder endpoints, string pattern, Delegate handler) =>
        RequestDelegateFactory.Create(handler, new RequestDelegateFactoryOptions
        {
            ServiceProvider = endpoints.ServiceProvider,
            RouteParameterNames = [.. RoutePatternFactory.Parse(pattern).Parameters.Select(parameter => parameter.Name)],
        }).RequestDelegate;
}
```

In `ProblemResultFactory`:

```csharp
    /// <summary>An inbound webhook delivery that failed verification: 401, with no challenge — Standard Webhooks names none.</summary>
    internal static IResult WebhookRefused(string detail) =>
        Problem(StatusCodes.Status401Unauthorized, AlvoProblemTypes.Unauthenticated, detail);

    /// <summary>An inbound webhook delivery whose id is still being handled.</summary>
    internal static IResult WebhookInFlight() => Problem(
        StatusCodes.Status409Conflict,
        AlvoProblemTypes.Conflict,
        "A delivery with this webhook-id is still being handled. Retry it later: once it is handled, a retry is acknowledged.");
```

In `AlvoServiceCollectionExtensions`, after `services.AddAlvoCommands();`: `services.AddAlvoWebhooks();` (`using MMLib.Alvo.Webhooks;`).

- [ ] **Step 5: Run the tests, accept the baseline**

Run: `dotnet test --project test/MMLib.Alvo.Tests --filter-class '*StandardWebhookSignatureTests'` and `dotnet test --project test/MMLib.Alvo.Api.Tests --filter-class '*WebhookReceiverTests'`
Expected: PASS. If `A_reference_signature_verifies` fails, the code is wrong — recheck the signed content and the key decoding — never the vector. The core baseline adds exactly `AlvoWebhookReceiverOptions` (four properties) and `MapAlvoWebhookReceiver`; accept, dispatch `alvo-snapshot-judge`.

- [ ] **Step 6: Format, ring1, commit**

```bash
dotnet format whitespace MMLib.Alvo.slnx --include src/MMLib.Alvo/Api/AlvoWebhookReceiverOptions.cs src/MMLib.Alvo/Api/AlvoWebhookEndpointRouteBuilderExtensions.cs src/MMLib.Alvo/Webhooks/Setup.cs src/MMLib.Alvo/Webhooks/Internal/StandardWebhookSignature.cs src/MMLib.Alvo/Webhooks/Internal/WebhookReceiver.cs src/MMLib.Alvo/Webhooks/Internal/WebhookReceivers.cs src/MMLib.Alvo/Webhooks/Internal/LedgerSweeper.cs src/MMLib.Alvo/Webhooks/Internal/WebhookLog.cs src/MMLib.Alvo/AlvoServiceCollectionExtensions.cs src/MMLib.Alvo/Api/Internal/ProblemResultFactory.cs test/MMLib.Alvo.Tests/Webhooks/StandardWebhookSignatureTests.cs test/MMLib.Alvo.Api.Tests/Webhooks/WebhookReceiverTests.cs
scripts/test-ring1
git add src/MMLib.Alvo/Api/AlvoWebhookReceiverOptions.cs src/MMLib.Alvo/Api/AlvoWebhookEndpointRouteBuilderExtensions.cs src/MMLib.Alvo/Webhooks src/MMLib.Alvo/AlvoServiceCollectionExtensions.cs src/MMLib.Alvo/Api/Internal/ProblemResultFactory.cs test/MMLib.Alvo.Api.Tests/Webhooks test/MMLib.Alvo.Tests/Webhooks test/MMLib.Alvo.Tests/PublicApi.MMLib.Alvo.verified.txt
git commit -m "feat(webhooks): receive Standard Webhooks deliveries verified, once, as the system

Claude-Session: https://claude.ai/code/session_01NJAtafM29iddLwfhb5gnmB"
```

---

## Task 12: Commands in the catalog, the policy simulator, and the assistant

**Files:**
- Create: `src/MMLib.Alvo/Commands/Internal/CommandDescription.cs`, `src/MMLib.Alvo/Commands/Internal/CommandSimulator.cs`
- Modify: `src/MMLib.Alvo/Extensions/Internal/ExtensionCatalog.cs` (`Describe`), `src/MMLib.Alvo/Commands/Setup.cs` (`CommandSimulator`), `src/MMLib.Alvo/Management/Internal/AlvoManagementService.cs` (`SimulatePolicyAsync`, a trailing `CommandSimulator? commands = null` constructor parameter), `src/MMLib.Alvo/Management/Setup.cs` (passes it), `src/MMLib.Alvo.Ai/Internal/ManagementTools.cs` (`get_extensions` description), `src/MMLib.Alvo.Ai/Instructions/schema-assistant.md`, `src/MMLib.Alvo.Ai/Internal/AssistantInstructions.cs` (`VersionLine` → v8)
- Test: `test/MMLib.Alvo.Tests/Commands/CommandSimulatorTests.cs`, `test/MMLib.Alvo.Tests/Extensions/ExtensionCatalogTests.cs` (two facts), `test/MMLib.Alvo.Ai.Tests/ManagementToolsTests.cs` (one fact)

**Interfaces:**
- Consumes: Task 4's `ManagementCommandVerdict`, `AlvoExtensionKind.Command`, `AlvoExtensionStage.Http/PreTransaction`, `ManagementExtension.Route/HttpMethod/Access`; Task 6's `ExtensionRegistry.Commands`, `CommandAccessJudge.Preview`.
- Produces: `internal static class CommandDescription { ManagementExtension Of(CommandRegistration, bool exposeTypeNames); }`; `internal sealed class CommandSimulator(ExtensionRegistry, CommandAccessJudge) { IReadOnlyList<ManagementCommandVerdict> Simulate(string entity, AlvoContext caller); }`; `ManagementPolicyVerdict.Commands` filled by `SimulatePolicyAsync`.

- [ ] **Step 1: Write the failing tests**

`test/MMLib.Alvo.Tests/Commands/CommandSimulatorTests.cs`:

```csharp
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using MMLib.Alvo.Commands.Internal;
using MMLib.Alvo.Data;
using MMLib.Alvo.Expressions;
using MMLib.Alvo.Extensions.Internal;
using MMLib.Alvo.Rules;
using MMLib.Alvo.Tests.Expressions;
using NSubstitute;

using static MMLib.Alvo.Tests.Extensions.HandlerCatalog;

namespace MMLib.Alvo.Tests.Commands;

/// <summary>
/// The simulator evaluates a command's access rule (spec 2026-10-14 §7 HX-08 dashboard row; §6.2 simulator parity): it asks
/// the judge the command filter asks (Ruling 33).
/// </summary>
public sealed class CommandSimulatorTests
{
    private const string Blocked = "0b0e0c2a-1b1b-4c4c-8d8d-9e9e9e9e9e9e";

    private static readonly AlvoContext _staff = new() { User = UserId.New(), Roles = new HashSet<Role> { Role.Authenticated, Role.Application("staff") } };

    [Fact]
    public void A_caller_only_rule_is_answered_for_the_simulated_caller()
    {
        var simulator = Simulator(out _);

        simulator.Simulate("tickets", _staff).Single(verdict => verdict.Name == "quote_stay").Allowed.ShouldBe(true);
        simulator.Simulate("tickets", AlvoContext.Anonymous).Single(verdict => verdict.Name == "quote_stay").Allowed.ShouldBe(false);
    }

    [Fact]
    public void A_rule_reading_the_row_is_decided_per_row()
    {
        var verdict = Simulator(out _).Simulate("tickets", _staff).Single(candidate => candidate.Name == "resolve_ticket");

        verdict.Allowed.ShouldBeNull();
        verdict.Entity.ShouldBe("tickets");
        verdict.Access.ShouldBe("status == 'open' && 'staff' in @user.roles");
    }

    [Fact]
    public void A_negated_rule_previews_refused_for_a_caller_without_the_value() =>
        Simulator(out _).Simulate("tickets", _staff).Single(verdict => verdict.Name == "not_blocked").Allowed
            .ShouldBe(false, "the staff caller carries no tenant, so the required-context gate refuses before the negation");

    [Fact]
    public void An_entity_lists_its_own_commands_and_the_unbound_ones_only() =>
        Simulator(out _).Simulate("notes", _staff).Select(verdict => verdict.Name).ShouldBe(["quote_stay", "not_blocked"]);

    [Fact]
    public void The_simulator_answers_exactly_as_the_judge_admits()
    {
        var simulator = Simulator(out var judge);

        foreach (var caller in new[] { _staff, AlvoContext.Anonymous, AlvoContext.System(null), _staff with { Tenant = new TenantId(Guid.NewGuid()) } })
        {
            foreach (var verdict in simulator.Simulate("notes", caller))
            {
                var admitted = Record.Exception(() => judge.EnsureAdmitted(verdict.Name, null, caller)) is null;
                verdict.Allowed.ShouldBe(admitted, $"{verdict.Name} for {caller.User}");
            }
        }
    }

    private static CommandSimulator Simulator(out CommandAccessJudge judge)
    {
        var services = new ServiceCollection();
        services.AddAlvo();
        var provider = services.BuildServiceProvider();
        var commands = provider.GetRequiredService<CommandRegistry>();
        commands.Map("quote_stay", "/q", null, CommandAccess.Of("'staff' in @user.roles"));
        commands.Map("resolve_ticket", "/t/{id}", "tickets", CommandAccess.Of("status == 'open' && 'staff' in @user.roles"));
        commands.Map("not_blocked", "/n", null, CommandAccess.Of($"!(string(@tenant.id) == '{Blocked}')"));
        var catalogs = Substitute.For<IPolicyCatalogProvider>();
        catalogs.Current.Returns(Catalog());
        var registry = provider.GetRequiredService<ExtensionRegistry>();
        var binder = new ExtensionBinder(registry, CelFixtures.Compiler, EveryService(), NullLogger<ExtensionBinder>.Instance);
        judge = new CommandAccessJudge(binder, catalogs, provider.GetRequiredService<IPredicateEvaluator>());

        return new CommandSimulator(registry, judge);
    }
}
```

(`Role.Application` is internal to Abstractions, which grants `MMLib.Alvo.Tests`. `Record.Exception` is xUnit's.)

Append to `ExtensionCatalogTests`:

```csharp
    [Fact]
    public void A_mapped_command_is_listed_after_the_handlers_with_its_route_method_and_access()
    {
        var services = new ServiceCollection();
        services.AddAlvo(alvo => alvo.Entity("tickets", tickets => tickets.OnCreated("on-ticket", OnCreated)));
        using var provider = services.BuildServiceProvider();
        provider.GetRequiredService<MMLib.Alvo.Commands.Internal.CommandRegistry>().Map(
            "resolve_ticket", "/commands/tickets/{id:guid}/resolve", "tickets", MMLib.Alvo.Commands.Internal.CommandAccess.Of("'staff' in @user.roles"));

        var items = Catalog(provider.GetRequiredService<ExtensionRegistry>()).Items;

        items.Select(item => item.Id).ShouldBe(["on-ticket", "resolve_ticket"]);
        var command = items[1];
        command.Kind.ShouldBe(AlvoExtensionKind.Command);
        command.Stage.ShouldBe(AlvoExtensionStage.Http);
        command.Entities.ShouldBe(["tickets"]);
        command.Operations.ShouldBeEmpty();
        command.Route.ShouldBe("/commands/tickets/{id:guid}/resolve");
        command.HttpMethod.ShouldBe("POST");
        command.Access.ShouldBe("'staff' in @user.roles");
    }

    [Fact]
    public void An_internal_command_runs_before_any_transaction_and_has_no_route()
    {
        var item = Catalog(Registry(alvo => alvo.AddCommand<Escalate>("escalate_ticket", "true"))).Items.ShouldHaveSingleItem();

        item.Stage.ShouldBe(AlvoExtensionStage.PreTransaction);
        item.Route.ShouldBeNull();
        item.HttpMethod.ShouldBeNull();
        Catalog(Registry(alvo => alvo.AddCommand<Escalate>("escalate_ticket", "true"))).HonouredKinds.ShouldBe(["extensions.command"]);
    }

    private sealed class Escalate : MMLib.Alvo.Commands.IAlvoCommand<Guid>
    {
        public Task HandleAsync(Guid input, MMLib.Alvo.Commands.AlvoCommandContext context, CancellationToken cancellationToken) => Task.CompletedTask;
    }
```

Append to `test/MMLib.Alvo.Ai.Tests/ManagementToolsTests.cs`:

```csharp
    /// <summary>The extensions tool shows a command as the catalog lists it: its route and its access rule.</summary>
    [Fact]
    public async Task The_extensions_tool_shows_a_command_s_route_and_access_rule()
    {
        var management = Substitute.For<IAlvoManagement>();
        management.GetExtensionsAsync(Project, Arg.Any<CancellationToken>()).Returns(new ManagementExtensionCatalog
        {
            Items =
            [
                new ManagementExtension
                {
                    Id = "confirm_booking",
                    Kind = AlvoExtensionKind.Command,
                    Stage = AlvoExtensionStage.Http,
                    Entities = ["bookings"],
                    Route = "/commands/bookings/{id}/confirm",
                    HttpMethod = "POST",
                    Access = "'staff' in @user.roles && status == 'requested'",
                },
            ],
        });

        var answer = await InvokeAsync(management, "get_extensions", []);

        answer.ShouldContain("\"command\"");
        answer.ShouldContain("/commands/bookings/{id}/confirm");
        answer.ShouldContain("status == 'requested'");
        ManagementTools.For(management, Project).Functions.Single(tool => tool.Name == "get_extensions").Description.ShouldContain("commands");
    }
```

- [ ] **Step 2: Run them to verify they fail**

Run: `dotnet test --project test/MMLib.Alvo.Tests --filter-class '*CommandSimulatorTests' --filter-class '*ExtensionCatalogTests'` and `dotnet test --project test/MMLib.Alvo.Ai.Tests --filter-class '*ManagementToolsTests'`
Expected: FAIL — `CommandSimulator` does not exist; the catalog lists no command; the tool's description names no command.

- [ ] **Step 3: The catalog and the simulator**

`src/MMLib.Alvo/Commands/Internal/CommandDescription.cs`:

```csharp
using MMLib.Alvo.Management;

namespace MMLib.Alvo.Commands.Internal;

/// <summary>A command as the extension catalog lists it (Ruling 14).</summary>
internal static class CommandDescription
{
    /// <summary>The method every command is mapped with.</summary>
    internal const string Method = "POST";

    internal static ManagementExtension Of(CommandRegistration command, bool exposeTypeNames) => new()
    {
        Id = command.Id,
        Kind = AlvoExtensionKind.Command,
        Stage = command.IsMapped ? AlvoExtensionStage.Http : AlvoExtensionStage.PreTransaction,
        Entities = command.Entity is { } entity ? [entity] : [],
        Route = command.Pattern,
        HttpMethod = command.IsMapped ? Method : null,
        Access = command.Access.Source,
        TypeName = exposeTypeNames ? command.CommandType?.FullName : null,
    };
}
```

In `ExtensionCatalog.Describe`:

```csharp
    private static ManagementExtension[] Describe(ExtensionRegistry registry, bool exposeTypeNames) =>
        [.. registry.Handlers
            .OrderBy(handler => handler.Settings.Order)
            .ThenBy(handler => handler.Sequence)
            .Select(handler => HandlerDescription.Of(handler, exposeTypeNames)),
         .. registry.Commands.OrderBy(command => command.Sequence).Select(command => CommandDescription.Of(command, exposeTypeNames))];
```

`src/MMLib.Alvo/Commands/Internal/CommandSimulator.cs`:

```csharp
using MMLib.Alvo.Extensions.Internal;
using MMLib.Alvo.Management;

namespace MMLib.Alvo.Commands.Internal;

/// <summary>
/// What each command's access rule answers for a simulated caller — asked of <see cref="CommandAccessJudge"/>, the judge the
/// command filter and <c>IAlvoCommands</c> ask, so the simulator cannot answer otherwise than production (Ruling 33). A rule
/// that reads the row has no answer without one, and the simulator reads no row (Management deviation D4).
/// </summary>
internal sealed class CommandSimulator(ExtensionRegistry registry, CommandAccessJudge judge)
{
    /// <summary>The verdicts of the commands bound to <paramref name="entity"/> and of those bound to none.</summary>
    internal IReadOnlyList<ManagementCommandVerdict> Simulate(string entity, AlvoContext caller) =>
        [.. registry.Commands
            .Where(command => command.Entity is null || string.Equals(command.Entity, entity, StringComparison.Ordinal))
            .OrderBy(command => command.Sequence)
            .Select(command => new ManagementCommandVerdict
            {
                Name = command.Id,
                Entity = command.Entity,
                Access = command.Access.Source,
                Allowed = judge.Preview(command.Id, caller),
            })];
}
```

In `Commands/Setup.cs`: `services.TryAddSingleton<CommandSimulator>();`. In `AlvoManagementService`, append the constructor parameter `CommandSimulator? commands = null` and rewrite `SimulatePolicyAsync`'s tail:

```csharp
        var caller = Caller(simulation.Caller);
        var decision = policies.Resolve(simulation.Entity, Operation(simulation.Operation), caller);

        return Task.FromResult(Verdict(decision) with
        {
            Extensions = extensions.RunningOn(simulation.Entity, simulation.Operation),
            Commands = commands?.Simulate(simulation.Entity, caller) ?? [],
        });
```

In `Management/Setup.cs`, append `provider.GetService<Commands.Internal.CommandSimulator>()` as the last argument of `new AlvoManagementService(…)`.

- [ ] **Step 4: The assistant reads commands in `get_extensions`**

In `ManagementTools`, the `get_extensions` description becomes:

```csharp
                "The host code (extensions) this host registers, as read-only metadata: event handlers with the entities, operations "
                + "and condition each binds, and commands with their route, the entity whose row they act on and their access rule "
                + "(CEL over @user, @tenant and that row). An entry names entities by name, or `*` for every entity. Descriptions "
                + "are host-authored text, trusted as the host is. When get_descriptor's hostExtensions names an entity, read it before "
                + "proposing an after-hook there: name an extension that already does what is asked instead of adding one. A "
                + "field or entity an extension reads cannot be removed or left out of event images; the dry run refuses it, "
                + "naming the extension."),
```

In `schema-assistant.md`, the `get_extensions` bullet becomes "- `get_extensions` — the host code (extensions) this host registers — event handlers and commands — and what each one binds; read-only." and the first line `<!-- alvo-schema-assistant v8 -->`; `AssistantInstructions.VersionLine` follows (`"<!-- alvo-schema-assistant v8 -->"`). No eval case changes: the eval's extension cases read handlers only, and a command adds no read to them (HX-04 Ruling 15's reasoning).

- [ ] **Step 5: Run the tests**

Run the two commands of Step 2, then `dotnet test --project test/MMLib.Alvo.Ai.Tests` and `dotnet test --project test/MMLib.Alvo.Api.Tests --filter-class '*ManagementExtensionsTests'`.
Expected: PASS. A fact pinning the instructions' version line or byte size moves with the edit.

- [ ] **Step 6: Format, ring0, commit**

```bash
dotnet format whitespace MMLib.Alvo.slnx --include src/MMLib.Alvo/Commands/Internal/CommandDescription.cs src/MMLib.Alvo/Commands/Internal/CommandSimulator.cs src/MMLib.Alvo/Extensions/Internal/ExtensionCatalog.cs src/MMLib.Alvo/Commands/Setup.cs src/MMLib.Alvo/Management/Internal/AlvoManagementService.cs src/MMLib.Alvo/Management/Setup.cs src/MMLib.Alvo.Ai/Internal/ManagementTools.cs src/MMLib.Alvo.Ai/Internal/AssistantInstructions.cs test/MMLib.Alvo.Tests/Commands/CommandSimulatorTests.cs test/MMLib.Alvo.Tests/Extensions/ExtensionCatalogTests.cs test/MMLib.Alvo.Ai.Tests/ManagementToolsTests.cs
scripts/test-ring0
git add src/MMLib.Alvo/Commands/Internal/CommandDescription.cs src/MMLib.Alvo/Commands/Internal/CommandSimulator.cs src/MMLib.Alvo/Extensions/Internal/ExtensionCatalog.cs src/MMLib.Alvo/Commands/Setup.cs src/MMLib.Alvo/Management/Internal/AlvoManagementService.cs src/MMLib.Alvo/Management/Setup.cs src/MMLib.Alvo.Ai/Internal/ManagementTools.cs src/MMLib.Alvo.Ai/Internal/AssistantInstructions.cs src/MMLib.Alvo.Ai/Instructions/schema-assistant.md test/MMLib.Alvo.Tests/Commands/CommandSimulatorTests.cs test/MMLib.Alvo.Tests/Extensions/ExtensionCatalogTests.cs test/MMLib.Alvo.Ai.Tests/ManagementToolsTests.cs
git commit -m "feat(management): commands in the catalog, the policy simulator and get_extensions

Claude-Session: https://claude.ai/code/session_01NJAtafM29iddLwfhb5gnmB"
```

---

## Task 13: The dashboard — the *Commands* lane and the simulator's command verdicts **[slice end]**

**Files:**
- Create: `src/MMLib.Alvo.Admin/Components/Rules/CommandVerdicts.cs`; `test/MMLib.Alvo.Admin.Tests/Schema/LifecycleLaneCommandTests.cs`, `test/MMLib.Alvo.Admin.Tests/Rules/CommandVerdictsTests.cs`, `test/MMLib.Alvo.Admin.Tests.EndToEnd/CommandScenarios.cs`
- Modify: `src/MMLib.Alvo.Admin/Components/Schema/{LifecycleTab.razor,LifecycleLane.cs}`, `src/MMLib.Alvo.Admin/Components/Rules/PolicyVerdictView.razor`, `test/MMLib.Alvo.Admin.Tests.EndToEnd/{AdminWorld.cs,ExtensionWorld.cs,ExtensionScenarios.cs}`

**Interfaces:**
- Consumes: `ManagementExtension.Kind/Route/HttpMethod/Access`, `ManagementPolicyVerdict.Commands`, `ManagementCommandVerdict`.
- Produces: `LifecycleLane.Commands(string entity, IReadOnlyList<ManagementExtension>)`, `LifecycleLane.Route(ManagementExtension)`; `internal static class CommandVerdicts { string Answer(ManagementCommandVerdict); string Tone(ManagementCommandVerdict); }`; the e2e seam `protected virtual void AdminWorld.Map(WebApplication app)`.

- [ ] **Step 1: Write the failing unit tests**

`test/MMLib.Alvo.Admin.Tests/Schema/LifecycleLaneCommandTests.cs`:

```csharp
using MMLib.Alvo.Admin.Components.Schema;
using MMLib.Alvo.Management;

namespace MMLib.Alvo.Admin.Tests.Schema;

/// <summary>The Commands lane (spec 2026-10-14 §4.13 dashboard row, HX-08): commands bound to the entity's rows, and the badge counts them.</summary>
public sealed class LifecycleLaneCommandTests
{
    private static readonly ManagementExtension[] _extensions =
    [
        new() { Id = "notify-reschedule", Kind = AlvoExtensionKind.EventHandler, Stage = AlvoExtensionStage.PostCommit, Entities = ["work_orders"], Operations = ["update"] },
        new() { Id = "complete_work_order", Kind = AlvoExtensionKind.Command, Stage = AlvoExtensionStage.Http, Entities = ["work_orders"], Route = "/commands/work-orders/{id:guid}/complete", HttpMethod = "POST", Access = "status == 'in_progress'" },
        new() { Id = "notify_customer", Kind = AlvoExtensionKind.Command, Stage = AlvoExtensionStage.PreTransaction, Access = "'dispatcher' in @user.roles" },
    ];

    [Fact]
    public void The_lane_lists_the_commands_bound_to_the_entity_only() =>
        LifecycleLane.Commands("work_orders", _extensions).Select(command => command.Id).ShouldBe(["complete_work_order"]);

    [Fact]
    public void The_after_commit_lane_never_lists_a_command() =>
        LifecycleLane.AfterCommit("work_orders", _extensions).Select(extension => extension.Id).ShouldBe(["notify-reschedule"]);

    [Fact]
    public void The_badge_counts_a_bound_command_as_code_that_runs_on_the_entity() =>
        LifecycleLane.Binding("work_orders", _extensions).Count.ShouldBe(2);

    [Fact]
    public void A_command_s_route_line_is_its_method_and_pattern_or_says_it_is_invoked_in_process()
    {
        LifecycleLane.Route(_extensions[1]).ShouldBe("POST /commands/work-orders/{id:guid}/complete");
        LifecycleLane.Route(_extensions[2]).ShouldBe("invoked in process");
    }
}
```

`test/MMLib.Alvo.Admin.Tests/Rules/CommandVerdictsTests.cs`:

```csharp
using MMLib.Alvo.Admin.Components.Rules;
using MMLib.Alvo.Management;

namespace MMLib.Alvo.Admin.Tests.Rules;

/// <summary>The simulator's words for a command verdict: it never claims an answer the engine did not give.</summary>
public sealed class CommandVerdictsTests
{
    [Theory]
    [InlineData(true, "admits", "a-badge--ok")]
    [InlineData(false, "refuses", "a-badge--warn")]
    [InlineData(null, "decided per row", "")]
    public void A_verdict_is_worded_as_what_the_rule_answered(bool? allowed, string answer, string tone)
    {
        var verdict = new ManagementCommandVerdict { Name = "c", Access = "true", Allowed = allowed };

        CommandVerdicts.Answer(verdict).ShouldBe(answer);
        CommandVerdicts.Tone(verdict).ShouldBe(tone);
    }
}
```

- [ ] **Step 2: Run them to verify they fail**

Run: `dotnet test --project test/MMLib.Alvo.Admin.Tests --filter-class '*LifecycleLaneCommandTests' --filter-class '*CommandVerdictsTests'`
Expected: FAIL to compile.

- [ ] **Step 3: The lane and the verdicts**

Add to `LifecycleLane`:

```csharp
    /// <summary>The commands the Commands lane draws for <paramref name="entity"/>: those bound to a row of it.</summary>
    public static IReadOnlyList<ManagementExtension> Commands(string entity, IReadOnlyList<ManagementExtension> extensions) =>
        [.. extensions.Where(extension => extension.Kind == AlvoExtensionKind.Command && extension.Entities.Contains(entity, StringComparer.Ordinal))];

    /// <summary>A command's route line: its method and pattern, or that it is invoked in process.</summary>
    public static string Route(ManagementExtension command) =>
        command.Route is { } route ? $"{command.HttpMethod ?? "POST"} {route}" : "invoked in process";
```

`src/MMLib.Alvo.Admin/Components/Rules/CommandVerdicts.cs`:

```csharp
using MMLib.Alvo.Management;

namespace MMLib.Alvo.Admin.Components.Rules;

/// <summary>How the simulator words a command's verdict — kept out of the markup so it can be tested.</summary>
internal static class CommandVerdicts
{
    /// <summary>What the rule answered, in words.</summary>
    public static string Answer(ManagementCommandVerdict verdict) => verdict.Allowed switch
    {
        true => "admits",
        false => "refuses",
        null => "decided per row",
    };

    /// <summary>The badge's tone class.</summary>
    public static string Tone(ManagementCommandVerdict verdict) => verdict.Allowed switch
    {
        true => "a-badge--ok",
        false => "a-badge--warn",
        null => string.Empty,
    };
}
```

In `LifecycleTab.razor`, update the header comment ("This build draws two lanes: After commit … and Commands — the commands the host maps on a row of this entity, read-only; the other lanes appear with the host code that can run in them."), and after the After-commit `<div>` add:

```razor
<div data-testid="lifecycle-commands">
    <SectionHead Title="Commands">
        <Subtitle>
            Named actions the host's code exposes on a row of this entity. Each runs only for a caller its access rule admits
            — judged before it runs — outside any transaction, and records its completion as an event.
        </Subtitle>
    </SectionHead>

    @if (_commands.Count == 0)
    {
        <div data-testid="lifecycle-commands-empty">
            <EmptyState Title="No command acts on this entity" Body="The host maps no command bound to a row of it." />
        </div>
    }

    @foreach (var command in _commands)
    {
        <ListRow class="a-listrow--roomy a-listrow--stacked" data-testid="@($"lane-command-{command.Id}")">
            <div class="a-row">
                <span class="a-badge a-badge--accent">⟨/⟩ from code</span>
                <span class="a-mono a-name">@command.Id</span>
            </div>
            <code class="a-mono" data-testid="lane-command-route">@LifecycleLane.Route(command)</code>
            <code class="a-mono" data-testid="lane-command-access">access @command.Access</code>
            <p class="a-note">Registered in the host's C#: it is changed there, never here.</p>
        </ListRow>
    }
</div>
```

with `private IReadOnlyList<ManagementExtension> _commands = [];` and, in `OnParametersSet`, `_commands = LifecycleLane.Commands(Entity, Extensions);`.

In `PolicyVerdictView.razor`, after the read-only fields block:

```razor
@if (Verdict.Commands.Count > 0)
{
    <div class="a-field" data-testid="verdict-commands">
        <span class="a-label">Commands — may this caller invoke them?</span>
        @foreach (var command in Verdict.Commands)
        {
            <div class="a-row" data-testid="@($"verdict-command-{command.Name}")">
                <span class="a-badge @CommandVerdicts.Tone(command)" data-testid="verdict-command-answer">@CommandVerdicts.Answer(command)</span>
                <span class="a-mono a-name">@command.Name</span>
                <code class="a-mono">@command.Access</code>
            </div>
        }
        <span class="a-section__sub">
            Judged with the same compiled rule a command checks before it runs. A rule that reads the row is decided per row,
            and a row the caller cannot read answers 404 first — this screen scores no row.
        </span>
    </div>
}
```

- [ ] **Step 4: The e2e world maps a command, and the scenarios**

In `AdminWorld`, between `_app = await AlvoHost.BuildAsync(builder)` and `await _app.StartAsync()`: `Map(_app);`, with

```csharp
    /// <summary>Maps this world's own endpoints — a host's commands — on the shipped host before it starts.</summary>
    /// <remarks>Empty by default, so the ordinary suite runs exactly what the container runs, which maps no command.</remarks>
    /// <param name="app">The shipped host, built and not yet started.</param>
    protected virtual void Map(WebApplication app)
    {
    }
```

`ExtensionWorld` adds:

```csharp
    internal const string Complete = "complete_work_order";
    internal const string Notify = "notify_customer";

    protected override void Map(WebApplication app) =>
        app.MapAlvoCommand(Complete, "/commands/work-orders/{id:guid}/complete", "work_orders", "'dispatcher' in @user.roles && status == 'in_progress'",
            (AlvoCommandContext context) => TypedResults.Ok());
```

and, in its `Configure(IAlvoBuilder alvo)` chain, `.AddCommand<NotifyCustomer>(Notify, "'dispatcher' in @user.roles")` with

```csharp
public sealed class NotifyCustomer : IAlvoCommand<Guid>
{
    public Task HandleAsync(Guid input, AlvoCommandContext context, CancellationToken cancellationToken) => Task.CompletedTask;
}
```

In `ExtensionScenarios.The_entity_list_counts_the_extensions_that_run_on_each_entity`, the `work_orders` badge becomes `"⟨/⟩ 4"` and its label `"4 extensions from code run on this entity"` (the bound command counts; `regions` stays `⟨/⟩ 1`).

`test/MMLib.Alvo.Admin.Tests.EndToEnd/CommandScenarios.cs`:

```csharp
namespace MMLib.Alvo.Admin.Tests.EndToEnd;

/// <summary>Commands on the dashboard (spec 2026-10-14 §7 HX-08): the Commands lane, and the simulator's verdict per command.</summary>
/// <param name="world">The field-service world with handlers and commands, and a browser.</param>
public sealed class CommandScenarios(ExtensionWorld world) : IClassFixture<ExtensionWorld>
{
    [Theory(Timeout = AdminWorld.ScenarioTimeout)]
    [InlineData(1400)]
    [InlineData(375)]
    public async Task The_lifecycle_tab_lists_a_command_bound_to_the_entity_with_its_route_and_access(int width)
    {
        await using var session = await world.SignInAsync(TestContext.Current.CancellationToken, width);
        await session.GoAsync("/schema/work_orders?tab=lifecycle");

        var item = session.Page.GetByTestId("lifecycle-commands").GetByTestId($"lane-command-{ExtensionWorld.Complete}");
        await item.WaitForAsync();

        (await item.GetByTestId("lane-command-route").InnerTextAsync()).Trim().ShouldBe("POST /commands/work-orders/{id:guid}/complete");
        (await item.GetByTestId("lane-command-access").InnerTextAsync()).Trim().ShouldBe("access 'dispatcher' in @user.roles && status == 'in_progress'");
        (await item.GetByRole(Microsoft.Playwright.AriaRole.Button).CountAsync()).ShouldBe(0, "host code is read-only on the dashboard");
        (await session.Page.GetByTestId("lifecycle-commands").GetByTestId($"lane-command-{ExtensionWorld.Notify}").CountAsync())
            .ShouldBe(0, "an internal command binds no row, so it is not on an entity's lane");
        await session.AssertNoHorizontalScrollAsync();
        session.AssertConsoleClean();
    }

    [Fact(Timeout = AdminWorld.ScenarioTimeout)]
    public async Task The_simulator_answers_each_command_s_access_rule_and_scores_no_row()
    {
        await using var session = await world.SignInAsync(TestContext.Current.CancellationToken);
        await session.GoAsync("/rules/work_orders");

        var commands = session.Page.GetByTestId("verdict-commands");
        await commands.WaitForAsync();

        (await commands.GetByTestId($"verdict-command-{ExtensionWorld.Complete}").GetByTestId("verdict-command-answer").InnerTextAsync())
            .Trim().ShouldBe("decided per row", "its rule reads the row's status");
        (await commands.GetByTestId($"verdict-command-{ExtensionWorld.Notify}").GetByTestId("verdict-command-answer").InnerTextAsync())
            .Trim().ShouldBeOneOf("admits", "refuses");
        (await session.Page.Locator("[data-record-verdict]").CountAsync()).ShouldBe(0);
        session.AssertConsoleClean();
    }
}
```

(`ExtensionWorld`'s usings gain `Microsoft.AspNetCore.Builder`, `Microsoft.AspNetCore.Http`, `MMLib.Alvo.Commands`.)

- [ ] **Step 5: Run the unit tests, the admin suite and the e2e suite**

Run: `dotnet test --project test/MMLib.Alvo.Admin.Tests` then `scripts/test-admin-e2e`.
Expected: PASS. The Admin public-API baseline does not move (no new component; `CommandVerdicts` is internal) — if it does, read why before accepting.

- [ ] **Step 6: Format, ring1, commit**

```bash
dotnet format whitespace MMLib.Alvo.slnx --include src/MMLib.Alvo.Admin/Components/Rules/CommandVerdicts.cs src/MMLib.Alvo.Admin/Components/Schema/LifecycleLane.cs test/MMLib.Alvo.Admin.Tests/Schema/LifecycleLaneCommandTests.cs test/MMLib.Alvo.Admin.Tests/Rules/CommandVerdictsTests.cs test/MMLib.Alvo.Admin.Tests.EndToEnd/CommandScenarios.cs test/MMLib.Alvo.Admin.Tests.EndToEnd/AdminWorld.cs test/MMLib.Alvo.Admin.Tests.EndToEnd/ExtensionWorld.cs test/MMLib.Alvo.Admin.Tests.EndToEnd/ExtensionScenarios.cs
scripts/test-ring1
git add src/MMLib.Alvo.Admin/Components/Rules/CommandVerdicts.cs src/MMLib.Alvo.Admin/Components/Rules/PolicyVerdictView.razor src/MMLib.Alvo.Admin/Components/Schema/LifecycleLane.cs src/MMLib.Alvo.Admin/Components/Schema/LifecycleTab.razor test/MMLib.Alvo.Admin.Tests/Schema/LifecycleLaneCommandTests.cs test/MMLib.Alvo.Admin.Tests/Rules/CommandVerdictsTests.cs test/MMLib.Alvo.Admin.Tests.EndToEnd/CommandScenarios.cs test/MMLib.Alvo.Admin.Tests.EndToEnd/AdminWorld.cs test/MMLib.Alvo.Admin.Tests.EndToEnd/ExtensionWorld.cs test/MMLib.Alvo.Admin.Tests.EndToEnd/ExtensionScenarios.cs
git commit -m "feat(admin): the Commands lane and command verdicts in the policy simulator

Claude-Session: https://claude.ai/code/session_01NJAtafM29iddLwfhb5gnmB"
```

---

## Task 14: Docs — *Commands and inbound webhooks*, *HTTP conventions*, architecture, CHANGELOG, PLAN, spec amendment **[slice end]**

**Files:**
- Create: `website/src/content/docs/extending/commands.mdx`, `website/src/content/docs/extending/http-conventions.mdx`; `website/src/snippets/extending/commands/{stay-desk.alvo.json,StayDeskCommands.cs,StayDeskCommandsTests.cs}`; `website/src/snippets/extending/http/{front-desk.alvo.json,FrontDeskEndpoints.cs,FrontDeskEndpointsTests.cs}`; `docs/architecture/commands.md`
- Modify: `test/MMLib.Alvo.DocsSnippets.Tests/MMLib.Alvo.DocsSnippets.Tests.csproj` (`Microsoft.AspNetCore.TestHost`), `website/astro.config.mjs`, `website/src/content/docs/extending/index.mdx`, `extending/testing.mdx` (order 10), `extending/transactions.mdx` (its *Not yet* section), `guides/call-from-endpoints.mdx`; `docs/architecture/data-api.md`, `docs/architecture/events.md`, `docs/architecture/extensibility.md`, `CHANGELOG.md`, `docs/PLAN.md`, `docs/superpowers/specs/2026-10-14-host-extensibility-design.md`

**Interfaces:**
- Consumes: everything above.
- Produces: the two pages, their snippets run by `StayDeskCommandsTests` and `FrontDeskEndpointsTests` in `MMLib.Alvo.DocsSnippets.Tests` (ring0).

- [ ] **Step 1: The snippet harness hosts HTTP**

In `MMLib.Alvo.DocsSnippets.Tests.csproj`, add `<PackageReference Include="Microsoft.AspNetCore.TestHost" />` (the version is pinned centrally). `AlvoTestHost` has no HTTP surface; a page about endpoints is tested over a real minimal-API host on a `TestServer` (Ruling 31).

- [ ] **Step 2: *Commands and inbound webhooks* — the snippets and their facts**

`website/src/snippets/extending/commands/stay-desk.alvo.json`:

```json
{
  "$schema": "https://alvo.dev/schema/v1/project.json",
  "apiVersion": "alvo.dev/v1",
  "name": "stay-desk",
  "auth": { "roles": ["guest", "staff"] },
  "entities": {
    "stays": {
      "fields": {
        "guest_id": { "type": "uuid", "required": true },
        "room": { "type": "string", "required": true, "maxLength": 20 },
        "status": { "type": "enum", "required": true, "values": ["requested", "confirmed", "cancelled"] },
        "nights": { "type": "integer", "required": true }
      },
      "rules": {
        "list": "'staff' in @user.roles || guest_id == @user.id",
        "get": "'staff' in @user.roles || guest_id == @user.id",
        "create": "'staff' in @user.roles || guest_id == @user.id",
        "update": "'staff' in @user.roles || 'admin' in @user.roles"
      }
    },
    "payments": {
      "fields": {
        "stay_id": { "type": "uuid", "required": true },
        "reference": { "type": "string", "required": true, "maxLength": 80 },
        "amount": { "type": "decimal", "required": true, "precision": 10, "scale": 2 }
      },
      "rules": {
        "list": "'staff' in @user.roles",
        "get": "'staff' in @user.roles",
        "create": "'staff' in @user.roles || 'admin' in @user.roles"
      }
    }
  }
}
```

`website/src/snippets/extending/commands/StayDeskCommands.cs`:

```csharp
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using MMLib.Alvo;
using MMLib.Alvo.Api;
using MMLib.Alvo.Commands;
using MMLib.Alvo.Data;

namespace StayDesk;

[AlvoEntity("stays")]
public sealed class Stay
{
    public Guid Id { get; init; }

    public Guid GuestId { get; init; }

    public required string Room { get; init; }

    public StayStatus Status { get; init; }

    public long Nights { get; init; }
}

public enum StayStatus
{
    Requested,
    Confirmed,
    Cancelled,
}

public interface IPaymentProvider
{
    Task<string> ChargeAsync(Guid stay, decimal amount, CancellationToken cancellationToken);
}

public interface IGuestMessenger
{
    Task RemindAsync(Guid stay, CancellationToken cancellationToken);
}

public sealed record QuoteRequest(int Nights);

public sealed record Quote(decimal Total);

public sealed record PaymentNotice(Guid StayId, string Reference, decimal Amount);

public static class StayDeskCommands
{
    public const decimal NightlyRate = 80m;

    public static IAlvoBuilder AddStayDesk(this IAlvoBuilder alvo) =>
        alvo.AddCommand<RemindGuest>("remind_guest", caller => caller.HasRole("admin"));

    public static WebApplication MapStayDesk(this WebApplication app)
    {
        app.MapAlvoCommand<Stay>(
            "confirm_stay",
            "/commands/stays/{id:guid}/confirm",
            (stay, caller) => stay.Status == StayStatus.Requested && caller.HasRole("staff"),
            ConfirmAsync);

        app.MapAlvoCommand(
            "cancel_stay",
            "/commands/stays/{id:guid}/cancel",
            "stays",
            "status == 'requested' && (guest_id == @user.id || 'staff' in @user.roles)",
            CancelAsync);

        app.MapAlvoCommand("quote_stay", "/commands/quote", caller => caller.HasRole("guest") || caller.HasRole("staff"),
            (QuoteRequest request) => TypedResults.Ok(new Quote(request.Nights * NightlyRate)));

        app.MapAlvoWebhookReceiver("/hooks/payments", "payment-provider", RecordPaymentAsync);

        return app;
    }

    private static async Task<IResult> ConfirmAsync(Guid id, AlvoCommandContext ctx, IPaymentProvider payments, CancellationToken ct)
    {
        var stay = ctx.Record!.As<Stay>();
        var amount = stay.Nights * NightlyRate;
        var reference = await payments.ChargeAsync(id, amount, ct);

        await ctx.TransactAsync(async (unit, token) =>
        {
            await unit.UpdateAsync(id, AlvoPatch.For<Stay>().Set(x => x.Status, StayStatus.Confirmed), cancellationToken: token);
            await unit.CreateAsync("payments", Payment(id, reference, amount), token);
        }, cancellationToken: ct);

        return TypedResults.Ok(new { stay = id, reference });
    }

    private static async Task<IResult> CancelAsync(Guid id, AlvoCommandContext ctx, CancellationToken ct)
    {
        await ctx.TransactAsync(
            (unit, token) => unit.AsSystem().UpdateAsync(id, AlvoPatch.For<Stay>().Set(x => x.Status, StayStatus.Cancelled), cancellationToken: token),
            cancellationToken: ct);

        return TypedResults.NoContent();
    }

    private static async Task<IResult> RecordPaymentAsync(PaymentNotice notice, AlvoCaller caller, IAlvoTransactions transactions, CancellationToken ct)
    {
        await transactions.TransactAsync(
            caller.Context,
            (unit, token) => unit.CreateAsync("payments", Payment(notice.StayId, notice.Reference, notice.Amount), token),
            cancellationToken: ct);

        return TypedResults.Ok();
    }

    private static Dictionary<string, object?> Payment(Guid stay, string reference, decimal amount) =>
        new() { ["stay_id"] = stay, ["reference"] = reference, ["amount"] = amount };
}

public sealed class RemindGuest(IGuestMessenger messenger) : IAlvoCommand<Guid>
{
    public Task HandleAsync(Guid input, AlvoCommandContext context, CancellationToken cancellationToken) =>
        messenger.RemindAsync(input, cancellationToken);
}
```

`website/src/snippets/extending/commands/StayDeskCommandsTests.cs`:

```csharp
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using MMLib.Alvo;
using MMLib.Alvo.Auth;
using MMLib.Alvo.Commands;
using MMLib.Alvo.Events;
using MMLib.Alvo.Secrets;
using Shouldly;
using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using Xunit;

namespace StayDesk.Tests;

public sealed class StayDeskCommandsTests : IAsyncLifetime
{
    private const string KeySecret = "s3cret-value-long-enough-for-the-floor";
    private const string SigningSecret = "whsec_MfKQ9r8GKYqrTwjUPD8ILPZIo2LaLaSw";
    private static readonly Guid _guest = Guid.NewGuid();
    private static readonly Guid _staff = Guid.NewGuid();

    private readonly SqliteConnection _database = new($"Data Source=stay-desk-{Guid.NewGuid():N};Mode=Memory;Cache=Shared");
    private readonly Payments _payments = new();
    private WebApplication _app = null!;
    private HttpClient _client = null!;

    private static string Descriptor => Path.Combine(AppContext.BaseDirectory, "stay-desk.alvo.json");

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync()
    {
        await _database.OpenAsync(Ct);
        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.UseTestServer();
        builder.Services.AddSingleton<IPaymentProvider>(_payments).AddSingleton<IGuestMessenger>(_payments);
        builder.Services.Configure<AlvoEventOptions>(options => options.DispatchMode = AlvoEventDispatchMode.Manual);
        builder.Services.Configure<AlvoSecretOptions>(options => options.Values["payment-provider"] = SigningSecret);
        builder.Services.Configure<AlvoAuthOptions>(options =>
        {
            options.DevKeys.Add(Key("guest", _guest, "guest"));
            options.DevKeys.Add(Key("staff", _staff, "staff"));
        });
        builder.Services.AddAlvo(alvo => alvo.UseSqlite(_database.ConnectionString).FromDescriptor(Descriptor).AddStayDesk());

        _app = builder.Build();
        _app.MapAlvoDataApi();
        _app.MapStayDesk();
        await _app.StartAsync(Ct);
        _client = _app.GetTestClient();
    }

    [Fact]
    public async Task Staff_confirm_a_requested_stay_and_its_payment_commits_with_it()
    {
        var stay = await BookAsync();

        using var confirmed = await SendAsync($"/commands/stays/{stay}/confirm", "staff");

        confirmed.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await ReadAsync($"/api/stays/{stay}", "staff"))["status"]!.GetValue<string>().ShouldBe("confirmed");
        (await ReadAsync("/api/payments", "staff"))["items"]!.AsArray().Count.ShouldBe(1);
        _payments.Charges.ShouldBe(1);
    }

    [Fact]
    public async Task A_guest_is_refused_before_anything_is_charged()
    {
        var stay = await BookAsync();

        using var refused = await SendAsync($"/commands/stays/{stay}/confirm", "guest");

        refused.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        _payments.Charges.ShouldBe(0);
    }

    [Fact]
    public async Task A_retried_confirmation_is_answered_without_charging_twice()
    {
        var stay = await BookAsync();

        using var first = await SendAsync($"/commands/stays/{stay}/confirm", "staff", idempotency: "confirm-42");
        using var retried = await SendAsync($"/commands/stays/{stay}/confirm", "staff", idempotency: "confirm-42");

        retried.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await retried.Content.ReadAsStringAsync(Ct)).ShouldBe(await first.Content.ReadAsStringAsync(Ct));
        _payments.Charges.ShouldBe(1);
    }

    [Fact]
    public async Task A_guest_cancels_their_own_stay_through_the_command_though_they_may_not_update_it()
    {
        var stay = await BookAsync();

        using var direct = await SendAsync($"/api/stays/{stay}", "guest", method: HttpMethod.Patch, body: new JsonObject { ["status"] = "cancelled" });
        using var cancelled = await SendAsync($"/commands/stays/{stay}/cancel", "guest");

        direct.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        cancelled.StatusCode.ShouldBe(HttpStatusCode.NoContent);
        (await ReadAsync($"/api/stays/{stay}", "guest"))["status"]!.GetValue<string>().ShouldBe("cancelled");
    }

    [Fact]
    public async Task A_signed_payment_notice_is_recorded_once_however_often_it_is_sent()
    {
        var stay = await BookAsync();
        var notice = $"{{\"stayId\":\"{stay}\",\"reference\":\"ch_1\",\"amount\":160}}";

        using var first = await NotifyAsync("msg_1", notice);
        using var again = await NotifyAsync("msg_1", notice);

        first.StatusCode.ShouldBe(HttpStatusCode.OK);
        again.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await ReadAsync("/api/payments", "staff"))["items"]!.AsArray().Count.ShouldBe(1);
    }

    [Fact]
    public async Task A_notice_signed_with_another_secret_is_refused()
    {
        using var forged = await NotifyAsync("msg_2", "{}", secret: "whsec_" + Convert.ToBase64String(Encoding.UTF8.GetBytes("not-the-provider's-secret-at-all")));

        forged.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task An_internal_command_is_invoked_in_process_under_its_access_rule()
    {
        var commands = _app.Services.GetRequiredService<IAlvoCommands>();
        var stay = await BookAsync();

        await commands.InvokeAsync("remind_guest", stay, AlvoContext.System(null), Ct);

        _payments.Reminders.ShouldBe([stay]);
    }

    public async ValueTask DisposeAsync()
    {
        await _app.DisposeAsync();
        await _database.DisposeAsync();
    }

    private async Task<Guid> BookAsync()
    {
        using var created = await SendAsync("/api/stays", "guest", method: HttpMethod.Post, body: new JsonObject
        {
            ["guest_id"] = _guest, ["room"] = "12", ["status"] = "requested", ["nights"] = 2,
        });
        created.StatusCode.ShouldBe(HttpStatusCode.Created);
        return (await created.Content.ReadFromJsonAsync<JsonObject>(Ct))!["id"]!.GetValue<Guid>();
    }

    private async Task<JsonObject> ReadAsync(string path, string key)
    {
        using var response = await SendAsync(path, key, method: HttpMethod.Get);
        return (await response.Content.ReadFromJsonAsync<JsonObject>(Ct))!;
    }

    private async Task<HttpResponseMessage> SendAsync(string path, string key, HttpMethod? method = null, JsonObject? body = null, string? idempotency = null)
    {
        using var request = new HttpRequestMessage(method ?? HttpMethod.Post, path) { Content = body is null ? null : JsonContent.Create(body) };
        request.Headers.Add("X-Alvo-Api-Key", $"{key}.{KeySecret}");
        if (idempotency is not null)
        {
            request.Headers.Add("Idempotency-Key", idempotency);
        }

        return await _client.SendAsync(request, Ct);
    }

    private async Task<HttpResponseMessage> NotifyAsync(string id, string body, string secret = SigningSecret)
    {
        var timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture);
        var key = Convert.FromBase64String(secret["whsec_".Length..]);
        var signature = Convert.ToBase64String(HMACSHA256.HashData(key, Encoding.UTF8.GetBytes($"{id}.{timestamp}.{body}")));

        using var request = new HttpRequestMessage(HttpMethod.Post, "/hooks/payments") { Content = new StringContent(body, Encoding.UTF8, "application/json") };
        request.Headers.Add("webhook-id", id);
        request.Headers.Add("webhook-timestamp", timestamp);
        request.Headers.Add("webhook-signature", $"v1,{signature}");
        return await _client.SendAsync(request, Ct);
    }

    private static AlvoDevApiKey Key(string id, Guid user, string role)
    {
        var key = new AlvoDevApiKey { KeyId = id, Secret = KeySecret, User = user };
        key.Roles.Add(role);
        key.Scopes.Add("*:read");
        key.Scopes.Add("*:write");
        return key;
    }

    private sealed class Payments : IPaymentProvider, IGuestMessenger
    {
        private int _charges;

        public int Charges => Volatile.Read(ref _charges);

        public List<Guid> Reminders { get; } = [];

        public Task<string> ChargeAsync(Guid stay, decimal amount, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _charges);
            return Task.FromResult($"ch_{stay:N}");
        }

        public Task RemindAsync(Guid stay, CancellationToken cancellationToken)
        {
            Reminders.Add(stay);
            return Task.CompletedTask;
        }
    }
}
```

The guest's PATCH on a stay is refused by `update`'s rule, which names `staff` and `admin` only — the command's `AsSystem()` is what lets the guest cancel, after the command's own access rule admitted them (the page says so).

Run: `dotnet test --project test/MMLib.Alvo.DocsSnippets.Tests --filter-class '*StayDeskCommandsTests' --filter-class '*SnippetCoverageTests'`
Expected: PASS (`SnippetCoverageTests` finds `StayDeskCommands` and `StayDeskCommandsTests`).

- [ ] **Step 3: *HTTP conventions* — the snippets and their facts**

`website/src/snippets/extending/http/front-desk.alvo.json`:

```json
{
  "$schema": "https://alvo.dev/schema/v1/project.json",
  "apiVersion": "alvo.dev/v1",
  "name": "front-desk",
  "auth": { "roles": ["clerk"] },
  "entities": {
    "rooms": {
      "fields": { "number": { "type": "string", "required": true, "maxLength": 10 } },
      "rules": { "list": "'clerk' in @user.roles", "get": "'clerk' in @user.roles", "create": "'clerk' in @user.roles" }
    },
    "guests": {
      "fields": { "name": { "type": "string", "required": true, "maxLength": 80 } },
      "rules": { "list": "'clerk' in @user.roles", "get": "'clerk' in @user.roles" }
    }
  }
}
```

`website/src/snippets/extending/http/FrontDeskEndpoints.cs`:

```csharp
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.RateLimiting;
using MMLib.Alvo;
using MMLib.Alvo.Api;
using MMLib.Alvo.Data;
using MMLib.Alvo.Rules;
using System.Threading.RateLimiting;

namespace FrontDesk;

public sealed record Me(Guid User, string Actor);

public sealed record AuditedWrite(string Entity);

public static class FrontDeskEndpoints
{
    public const string RoomsPolicy = "rooms";

    public static void AddFrontDeskLimits(this RateLimiterOptions limiter) =>
        limiter.AddFixedWindowLimiter(RoomsPolicy, window =>
        {
            window.PermitLimit = 2;
            window.Window = TimeSpan.FromMinutes(1);
            window.QueueLimit = 0;
        });

    public static WebApplication MapFrontDesk(this WebApplication app)
    {
        app.MapAlvoDataApi()
            .ForEntity("rooms", rooms => rooms.RequireRateLimiting(RoomsPolicy))
            .Add(endpoint =>
            {
                if (endpoint.Metadata.OfType<AlvoEndpointMetadata>().FirstOrDefault() is { Operation: not DataOperation.Get and not DataOperation.List } write)
                {
                    endpoint.Metadata.Add(new AuditedWrite(write.Entity));
                }
            });

        app.MapGet("/me", (AlvoCaller caller) => TypedResults.Ok(new Me(caller.Context.User.Value, caller.Context.ActorKind.ToString())))
            .WithAlvoCaller();

        app.MapGet("/me/rooms", async (AlvoCaller caller, IAlvoSessions sessions, CancellationToken ct) =>
                TypedResults.Ok((await sessions.For(caller.Context).QueryAsync(new AlvoQuery { Entity = "rooms" }, ct)).Items.Count))
            .WithAlvoCaller();

        return app;
    }
}
```

`website/src/snippets/extending/http/FrontDeskEndpointsTests.cs`:

```csharp
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using MMLib.Alvo;
using MMLib.Alvo.Auth;
using Shouldly;
using System.Net;
using System.Net.Http.Json;
using Xunit;

namespace FrontDesk.Tests;

public sealed class FrontDeskEndpointsTests : IAsyncLifetime
{
    private const string KeySecret = "s3cret-value-long-enough-for-the-floor";
    private static readonly Guid _clerk = Guid.NewGuid();
    private readonly SqliteConnection _database = new($"Data Source=front-desk-{Guid.NewGuid():N};Mode=Memory;Cache=Shared");
    private WebApplication _app = null!;
    private HttpClient _client = null!;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync()
    {
        await _database.OpenAsync(Ct);
        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.UseTestServer();
        builder.Services.AddRateLimiter(limiter => limiter.AddFrontDeskLimits());
        builder.Services.Configure<AlvoAuthOptions>(options =>
        {
            var key = new AlvoDevApiKey { KeyId = "clerk", Secret = KeySecret, User = _clerk };
            key.Roles.Add("clerk");
            key.Scopes.Add("*:read");
            key.Scopes.Add("*:write");
            options.DevKeys.Add(key);
        });
        builder.Services.AddAlvo(alvo => alvo.UseSqlite(_database.ConnectionString).FromDescriptor(Path.Combine(AppContext.BaseDirectory, "front-desk.alvo.json")));

        _app = builder.Build();
        _app.UseRateLimiter();
        _app.MapFrontDesk();
        await _app.StartAsync(Ct);
        _client = _app.GetTestClient();
    }

    [Fact]
    public async Task No_credential_is_the_anonymous_caller_and_a_key_is_its_own()
    {
        var anonymous = await _client.GetFromJsonAsync<Me>("/me", Ct);
        var clerk = await GetAsync<Me>("/me", "clerk");

        anonymous!.Actor.ShouldBe("Anonymous");
        clerk!.User.ShouldBe(_clerk);
    }

    [Fact]
    public async Task A_key_that_cannot_be_used_is_401_before_the_handler()
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, "/me");
        request.Headers.Add("X-Alvo-Api-Key", "clerk.wrong-secret-that-is-long-enough");

        (await _client.SendAsync(request, Ct)).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task A_plain_endpoint_reads_as_the_resolved_caller()
    {
        (await GetAsync<int>("/me/rooms", "clerk")).ShouldBe(0);
    }

    [Fact]
    public async Task ForEntity_limits_the_rooms_routes_and_no_other()
    {
        var statuses = new List<HttpStatusCode>();
        for (var i = 0; i < 3; i++)
        {
            statuses.Add(await StatusAsync("/api/rooms"));
        }

        statuses.ShouldBe([HttpStatusCode.OK, HttpStatusCode.OK, HttpStatusCode.TooManyRequests]);
        (await StatusAsync("/api/guests")).ShouldBe(HttpStatusCode.OK);
    }

    [Fact]
    public void A_convention_reads_alvo_s_endpoint_metadata()
    {
        var endpoints = _app.Services.GetRequiredService<EndpointDataSource>().Endpoints;

        endpoints.Count(endpoint => endpoint.Metadata.GetMetadata<AuditedWrite>() is { Entity: "rooms" }).ShouldBe(7, "create, update, replace, delete and the three batch routes");
    }

    public async ValueTask DisposeAsync()
    {
        await _app.DisposeAsync();
        await _database.DisposeAsync();
    }

    private async Task<T?> GetAsync<T>(string path, string key)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, path);
        request.Headers.Add("X-Alvo-Api-Key", $"{key}.{KeySecret}");
        using var response = await _client.SendAsync(request, Ct);
        return await response.Content.ReadFromJsonAsync<T>(Ct);
    }

    private async Task<HttpStatusCode> StatusAsync(string path)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, path);
        request.Headers.Add("X-Alvo-Api-Key", $"clerk.{KeySecret}");
        using var response = await _client.SendAsync(request, Ct);
        return response.StatusCode;
    }
}
```

Run: `dotnet test --project test/MMLib.Alvo.DocsSnippets.Tests --filter-class '*FrontDeskEndpointsTests'`
Expected: PASS.

- [ ] **Step 4: The pages**

`website/src/content/docs/extending/commands.mdx` — frontmatter `title: Commands and inbound webhooks`, `description: Expose a named, access-ruled action over HTTP or in process, make its external call first and its writes in one unit, and receive Standard Webhooks verified and once.`, `sidebar:` / `order: 8`. Imports as `transactions.mdx` does (`Code`, `SourceExcerpt`, the three snippets as `descriptor`, `desk`, `tests`). Sections, each a few sentences:

1. *When a command* — a write that is more than a row: confirm a stay, charge the card, record the payment. A command has a name, a route, an access rule judged before it runs, at most one transaction unit and a completion event. `<Code code={descriptor} lang="json" title="stay-desk.alvo.json" />`.
2. *Map one* — `MapAlvoCommand<Stay>(name, pattern, (stay, caller) => …, handler)`: the row the route's `{id}` names is read as the caller (a row they cannot read is 404 — the same answer as no row), then the rule — **typed** here, translated to CEL when the host starts — is judged (403). `<SourceExcerpt code={desk} from="app.MapAlvoCommand<Stay>(" to="ConfirmAsync);" title="StayDeskCommands.cs" />`. The same rule as a CEL string: `MapAlvoCommand("confirm_stay", pattern, "stays", "status == 'requested' && 'staff' in @user.roles", ConfirmAsync)` — `cancel_stay` is written that way: `<SourceExcerpt code={desk} from="app.MapAlvoCommand(\n            \"cancel_stay\"" to="CancelAsync);" title="StayDeskCommands.cs" />` (if the excerpt component cannot take the line break, start it at `"cancel_stay",`). The rule reads `@user`, `@tenant` and the row's fields; `old.` and `changed()` are refused — a command's row has no before-image. An empty rule refuses to start; write `'true'` to admit everyone. A rule that reads `@tenant` or `@user.id` refuses a caller who has none before it is evaluated — so `!(@tenant in [...])` never admits the anonymous caller (Ruling 33). A rule may not read a field the descriptor hides from some callers: the command refuses to start, and a descriptor apply that would hide a field a bound command reads is refused (Ruling 35). Map every command before the host serves: one mapped after the first descriptor is bound throws (Ruling 40). Names: lower-case segments separated by dots; the OpenAPI operationId is `commands.` and the name in lower camel case. The document lists each command's refusals — 400 for a body that does not bind (minimal API's own, before Alvo runs), 401, 403, 404, 409, 412, 422 and 508; an unexpected exception is answered 500 in Alvo's problem shape only when the host maps Alvo's problem details (`AddAlvoProblemDetails()` and `UseExceptionHandler()`), otherwise it is the host's (Ruling 45).
3. *External call first, then one unit* — the handler is not in a transaction. `<SourceExcerpt code={desk} from="private static async Task<IResult> ConfirmAsync" to="return TypedResults.Ok(new { stay = id, reference });" title="StayDeskCommands.cs" />`. Inside `ctx.TransactAsync` use the unit; `IAlvoData` and `ctx.Data` are refused there (*Transactions in your own code*). A second `TransactAsync` throws. The completion event `alvo.command.confirm_stay.completed` and the key's record are written in that unit as it commits; a command that opens no unit gets both after it answers 2xx. A handler that catches the failure of its unit and still answers 2xx gets neither: its writes rolled back, so no completion is announced and the key is released (Ruling 43). Handle the completion like any event: `AddEventHandler("…", "alvo.command.confirm_stay.completed", …)` — the payload carries `command`, `entity` and `id`, never the input. A handler of a completion no command produces refuses to start.
4. *Who may call it* — the caller is resolved as the Data API resolves it: no credential is the anonymous caller (judged by the rule), a key that cannot be used is 401, and an API key needs the read and the write scope on the command's entity — `stays:read` and `stays:write`, since the command reads the row before it writes (Ruling 39) — or, for a command bound to none, `*:write`. Writes the handler makes to other entities are judged by the data port's own rules for the caller it writes as, not by the command's scopes. `<SourceExcerpt code={tests} from="public async Task A_guest_is_refused_before_anything_is_charged" to="_payments.Charges.ShouldBe(0);" title="StayDeskCommandsTests.cs" />`. A command can do what the caller could not do directly: `cancel_stay` admits the guest by its rule and then writes `AsSystem()` — the rule is the gate.
5. *Retries: `Idempotency-Key`* — send one per logical request. A retry with the same key and the same input is answered with the first answer — status and JSON body — and the handler does not run again; a key reused for different input is 409 `idempotency-conflict`; a request still running under the key is 409 too (retry after it answers). Keys are per caller and per credential: two API keys of one user, or one user in two tenants, never share a key (Ruling 34). A replay is answered before the row and the rule are judged again, so a command that changed its row can still be retried. Answers are kept 24 hours; a retry after that runs as a new request — so make a long-retrying client's command safe to run twice, or send a new key (Ruling 41). Only the status and the JSON body are replayed — headers, `Location` included, are not. A result with no status (a redirect, a file), a plain `string` (written as text, it cannot be replayed as the same bytes), or an answer over 64 KiB is not replayable — a retry is then refused rather than run (Ruling 49). `<SourceExcerpt code={tests} from="public async Task A_retried_confirmation_is_answered_without_charging_twice" to="_payments.Charges.ShouldBe(1);" title="StayDeskCommandsTests.cs" />`.
6. *Internal commands* — `AddCommand<RemindGuest>("remind_guest", caller => …)` and `IAlvoCommands.InvokeAsync(name, input, caller)` from a handler, a timer or a job: the same rule, unit and completion event, no route, no key. In an event handler, pass `AlvoEventContext.Caller` — a handler running as the system holds `admin`. Not inside an open unit. `<SourceExcerpt code={desk} from="public sealed class RemindGuest" to="messenger.RemindAsync(input, cancellationToken);" title="StayDeskCommands.cs" />`.
7. *Inbound webhooks* — `MapAlvoWebhookReceiver(pattern, secretName, handler)` receives Standard Webhooks: `webhook-id`, `webhook-timestamp` within 5 minutes, `webhook-signature` `v1,` HMAC-SHA256 over `id.timestamp.body` with the `whsec_` secret stored under `secretName` (several space-separated secrets while you rotate). A delivery is handled once per receiver and id: a repeat is acknowledged 200 without running, one still running is 409, and a failed one is given back so the sender's retry runs it. Ids are forgotten after seven days. The handler runs as the system in `options.Tenant` or `options.ResolveTenant(request, body)` — called only for a verified delivery, with the verified body bytes. **Only the body is signed: take the tenant from it, or map one receiver per tenant with its own secret; never from the path or a header, which anyone who sees one signed delivery can change.** With neither, the handler runs as the system in no tenant, which every tenant-scoped entity refuses. Conventions on the returned builder apply to the outer endpoint; an endpoint filter never reaches the handler, and the system caller is bound to the handler's `AlvoCaller` only, not published through `IAlvoContextAccessor`. Delivery is at least once: make the handler idempotent on the id. `<SourceExcerpt code={desk} from="private static async Task<IResult> RecordPaymentAsync" to="return TypedResults.Ok();" title="StayDeskCommands.cs" />` and the test's signing `<SourceExcerpt code={tests} from="private async Task<HttpResponseMessage> NotifyAsync" to="return await _client.SendAsync(request, Ct);" title="StayDeskCommandsTests.cs" />`.
8. *On the dashboard and for the assistant* — the entity's *Lifecycle* tab lists the commands bound to it (route and rule, read-only); the policy simulator says per command whether the simulated caller passes its rule (or that it is decided per row); `get_extensions` lists them for the assistant.

`website/src/content/docs/extending/http-conventions.mdx` — frontmatter `title: HTTP conventions`, `description: Resolve Alvo's caller on your own endpoints, scope conventions to one entity's routes, and rely on Alvo's gate staying on every generated route.`, `sidebar:` / `order: 9`. Imports the three `http/` snippets as `descriptor`, `endpoints`, `tests`. Sections:

1. *Your endpoint, Alvo's caller* — `.WithAlvoCaller()` and an `AlvoCaller` parameter: no credential is `AlvoContext.Anonymous`, a key that cannot be used is 401 before your handler, the caller is also published through `IAlvoContextAccessor`. No scope is checked — your endpoint names no entity — but every `IAlvoData` or `IAlvoSessions.For(caller.Context)` call is still judged by the descriptor's rules. `<SourceExcerpt code={endpoints} from="app.MapGet(\"/me\"" to=".WithAlvoCaller();" title="FrontDeskEndpoints.cs" />`. Forgetting `.WithAlvoCaller()` makes `AlvoCaller.Context` throw, never answer as anonymous.
2. *One entity's routes: `ForEntity`* — `app.MapAlvoDataApi().ForEntity("rooms", r => r.RequireRateLimiting("rooms"))`. A name the descriptor applied when the routes are first built does not declare fails the route-table build (readiness `Failed`), so a typo is never a limiter you believe you have — on `MapAlvoDataApi()`'s builder. The table is built once, on the first routed request, so naming an entity the dashboard applies only later fails the Data API until the process restarts (Ruling 20). `ForEntity` on a `MapGroup` builder the Data API is mapped under cannot know the descriptor and checks no name, but it still reaches the entity's routes: its conventions are forwarded through the group's `Finally`, so they run after the route's own conventions (Ruling 50). A named rate-limit policy is one partition across every endpoint that references it: to limit each entity separately, partition the limiter on `AlvoEndpointMetadata.Entity` (Ruling 49). `<SourceExcerpt code={tests} from="public async Task ForEntity_limits_the_rooms_routes_and_no_other" to="(await StatusAsync(\"/api/guests\")).ShouldBe(HttpStatusCode.OK);" title="FrontDeskEndpointsTests.cs" />`.
3. *Reading Alvo's metadata* — every generated route carries `AlvoEndpointMetadata(Entity, Operation, Kind)`; a convention, a middleware or a rate-limit partition can read it. `<SourceExcerpt code={endpoints} from=".Add(endpoint =>" to="});" title="FrontDeskEndpoints.cs" />`.
4. *Alvo's gate stays on* — a convention may add filters around Alvo's authorization filter; one that removes it — clearing or rebuilding `EndpointBuilder.FilterFactories`, on `MapAlvoDataApi()`, in a `ForEntity`, or on a `MapGroup` the Data API is mapped under — fails the route-table build: no routes, readiness `Failed`, a critical log naming the route. A command endpoint stripped of its access filter fails the host's start, the same check on the command routes (Ruling 38); a `.WithAlvoCaller()` endpoint stripped of Alvo's filter fails closed — its `AlvoCaller` throws. The check is on what the endpoint factory composed: a convention that replaces an endpoint's request delegate outright replaces Alvo's handler with it, and is outside what the check sees.
5. *Commands are endpoints too* — `MapAlvoCommand` returns a `RouteHandlerBuilder`: `.WithSummary`, `.RequireRateLimiting`, `.RequireAuthorization` apply as on any endpoint. Link [Commands and inbound webhooks](/extending/commands/).

`website/astro.config.mjs`: `page('Commands and inbound webhooks', 'extending/commands'),` and `page('HTTP conventions', 'extending/http-conventions'),` after *Transactions in your own code*. `testing.mdx` frontmatter `order: 10`. `extending/index.mdx`: two bullets after *Transactions in your own code* — "- [Commands and inbound webhooks](/extending/commands/) — named, access-ruled actions over HTTP or in process; Standard Webhooks received verified and once." and "- [HTTP conventions](/extending/http-conventions/) — Alvo's caller on your own endpoints, conventions on one entity's routes, and the gate that stays on." `transactions.mdx`, section *Not yet*: drop "commands get `ctx.TransactAsync`" and add "Commands open their one unit with `ctx.TransactAsync` — [Commands and inbound webhooks](/extending/commands/)." as a sentence in section 1. `guides/call-from-endpoints.mdx`, section 1: "To resolve the caller the way the Data API does, map the endpoint with [`.WithAlvoCaller()`](/extending/http-conventions/) and take an `AlvoCaller`."

- [ ] **Step 5: Architecture docs, CHANGELOG, PLAN**

`docs/architecture/commands.md` (new), sections: *What a command is* (a minimal-API endpoint plus one filter, or an `IAlvoCommand<T>` invoked in process; one id namespace with every extension; registered at map time into `CommandRegistry`, sealed by the first binding — Ruling 40); *The filter's order and why* (caller → read+write scope → key/replay → row → rule → depth → handler, Rulings 6–7, 39; `CallerGate` shared with `WithAlvoCaller`; the boot's `CommandGateCheck` over every command route, Ruling 38); *The access rule* (CEL `Condition` profile against a fieldless scope or the entity, `old.`/`changed()` refused, typed lambdas through the `when` translator, compiled per catalog by `ExtensionBindingCompiler`, the apply guard; one `CommandAccessJudge` for the filter, `IAlvoCommands` and the simulator, which denies a caller missing the tenant or user the rule reads before evaluating; rules reading a hidden field refused; Rulings 1–2, 33, 35); *At most one unit* (R-M7; `CommandRun`; the completion and the key's commit mark written last inside the unit through `IAlvoCommandUnit`; the no-unit completion in one autocommit transaction; a unit opened and not committed releases the key and emits nothing; Rulings 10–11, 43); *The idempotency ledger* (`<prefix>_command_requests`: claimed → committed → answered, the 15-minute lease and its `claim_token` fencing — every later write conditional on it, a lost commit poisoning the unit; the race on PostgreSQL and SQLite, only the unique violation read as a race; answers replayed as status + JSON, the 64 KiB cap, scope per caller and per credential, the departure from `AlvoIdempotency`'s never-a-rendered-response contract; the 24-hour retention swept with webhook ids; Rulings 7–9, 34, 36, 41); *Inbound webhooks* (`<prefix>_inbound_webhooks`, verification before dedup and tenant, the tenant resolver given the verified bytes and why the path and headers are not trusted, the RDF-wrapped handler over the replayed bytes, the claim's fencing token, at-least-once, the hourly `LedgerSweeper`; Rulings 22–26, 36–37); *What is internal and why* (the three seams in Abstractions reached through `InternalsVisibleTo`, the `IAlvoSessionSchema` precedent; no public store a host could forge into).

`docs/architecture/data-api.md`, a new section **Conventions, `ForEntity`, endpoint metadata and the gate witness (HX-08)**: `AlvoEndpointMetadata` beside the internal marker, `ForEntity`'s recorder and its expected-entity check (against the descriptor applied when the table is first built — Ruling 20), its forwarding through `Finally` on a `MapGroup` builder (Ruling 50), the witness (a factory that counts its own invocation while the delegate is composed; checked after the ungrouped build and after every group build, the grouped check being the one on the endpoints that serve under a group; every `Finally` runs after composition on .NET 10, so a `Finally` cannot remove the gate and a `Finally` check would read the list rather than the delegate — Ruling 19; what it does not see — an assigned `RequestDelegate`, a wrapper that discards the factory's result — Ruling 49 L1), what a host convention may and may not do. Replace the paragraph that says a host convention "could take it away again" with a pointer to it.

`docs/architecture/events.md`, in the reserved-namespace section: "`alvo.command.<name>.completed` is minted by HX-08 through the internal `AlvoFrameworkEvent.Create` — the one door to the `alvo` namespace — in the command's unit or, with no unit, with its answer."

`docs/architecture/extensibility.md`, rule 4's `Add*` list already names `AddCommand`; add one sentence: "`MapAlvoCommand` and `MapAlvoWebhookReceiver` are endpoint verbs (`Map*`, the ASP.NET Core spelling), on the route builder — rule 10's separation of the DI seam from the endpoint seam."

`CHANGELOG.md`, `## [Unreleased]` → `### Added`, first entry: **Commands, inbound webhooks and HTTP conventions** (HX-08, #385). `MapAlvoCommand`/`MapAlvoCommand<TEntity>` — named commands whose caller (as the Data API resolves it), bound row (404 when unreadable) and access rule (CEL over `@user`, `@tenant` and the row, or a typed lambda; 403) are judged before the handler, which runs outside any transaction and opens at most one unit with `AlvoCommandContext.TransactAsync`; the completion event `alvo.command.<name>.completed` and the `Idempotency-Key` record commit in that unit; a replayed key answers the first status and JSON body without running the handler, for 24 hours, per caller and credential; commands appear in the OpenAPI document (`commands.<name>`), the extension catalog, the dashboard's *Commands* lane and policy simulator, and `get_extensions`. `AddCommand<TCommand>` + `IAlvoCommands.InvokeAsync` for internal commands. `MapAlvoWebhookReceiver` — Standard Webhooks verification (5-minute window, rotation), per-receiver dedup swept after 7 days, the handler run as the system in a configured tenant or one resolved from the verified body. `.WithAlvoCaller()` + `AlvoCaller` on plain endpoints; `ForEntity` and the public `AlvoEndpointMetadata`/`AlvoEndpointKind`. Docs: *Commands and inbound webhooks*, *HTTP conventions*. **Behaviour change:** a convention that removes Alvo's authorization filter from a generated route, and a `ForEntity` naming an undeclared entity, now fail the route-table build (readiness `Failed`) (#184). **Breaking for typed .NET clients:** `AlvoExtensionKind` and `AlvoExtensionStage` gained members (append-only; read the raw string to tolerate a newer server). New framework tables `<prefix>_command_requests`, `<prefix>_inbound_webhooks`; entity names `<prefix>_command_requests`/`<prefix>_inbound_webhooks` are now reserved.

`docs/PLAN.md`, *Host extensibility* line: "…; HX-07 (write unit, `TransactAsync`, savepoints) the seventh; HX-08 (commands, inbound webhooks, HTTP conventions) the eighth."

- [ ] **Step 6: Spec amendment**

In `docs/superpowers/specs/2026-10-14-host-extensibility-design.md`:

1. After §4.9's last bullet, add **HX-08 settled details.** — "Rulings and deviations taken while planning and building (1–52, verbatim from `docs/superpowers/plans/2026-10-30-hx-08-commands-http.md`), so a later reader can tell a decision from an oversight. Where a ruling and §4.9 disagree, the ruling is what shipped." — followed by this plan's *Rulings and deviations* (1)–(52), numbered, verbatim.
2. §4.9 *Endpoint metadata* bullet: after "the route-table build fails through `AlvoDataApiConventionException` (readiness `Failed`)", insert "— checked by a per-route gate witness rather than a `Finally` convention (§4.9 *HX-08 settled details* (19))".
3. §4.9 *Caller binding* / *MapAlvoCommand* bullets: after "`access` in `CelProfile.Access` over `@user`/`@tenant`", insert "— **as shipped:** compiled in `CelProfile.Condition` (§4.9 *HX-08 settled details* (1))".
4. §4.13 *HX-04 settled details* (1): append "— **HX-08 appended** `AlvoExtensionKind.Command`, `AlvoExtensionStage.Http` and `PreTransaction`, and `Route`, `HttpMethod`, `Access` (§4.9 *HX-08 settled details* (14))."
5. §4.10 *Framework events*: append "`AlvoFrameworkEvent.Create` arrived with HX-08's command completions; HX-13 reuses it."
6. §5, row `08` notes: "Also: an untyped entity-bound `MapAlvoCommand(name, pattern, entity, access, handler)` (D1), `AlvoCommandContext.Record`, `IBindableFromHttpContext` on `AlvoCaller` and `AlvoCommandContext`, `ManagementCommandVerdict` + `ManagementPolicyVerdict.Commands`, `AlvoExtensionKind.Command`, `AlvoExtensionStage.Http/PreTransaction`, `ManagementExtension.Route/HttpMethod/Access` (§4.9 *HX-08 settled details* (3), (5), (14), (18))."

- [ ] **Step 7: Build the site, ring1, commit**

```bash
dotnet format whitespace MMLib.Alvo.slnx --include website/src/snippets/extending/commands/StayDeskCommands.cs website/src/snippets/extending/commands/StayDeskCommandsTests.cs website/src/snippets/extending/http/FrontDeskEndpoints.cs website/src/snippets/extending/http/FrontDeskEndpointsTests.cs
scripts/test-ring1
(cd website && npm ci && npm run build)
git add website/src/content/docs/extending/commands.mdx website/src/content/docs/extending/http-conventions.mdx website/src/snippets/extending/commands website/src/snippets/extending/http website/astro.config.mjs website/src/content/docs/extending/index.mdx website/src/content/docs/extending/testing.mdx website/src/content/docs/extending/transactions.mdx website/src/content/docs/guides/call-from-endpoints.mdx test/MMLib.Alvo.DocsSnippets.Tests/MMLib.Alvo.DocsSnippets.Tests.csproj docs/architecture/commands.md docs/architecture/data-api.md docs/architecture/events.md docs/architecture/extensibility.md CHANGELOG.md docs/PLAN.md docs/superpowers/specs/2026-10-14-host-extensibility-design.md
git commit -m "docs: commands and inbound webhooks, HTTP conventions

Claude-Session: https://claude.ai/code/session_01NJAtafM29iddLwfhb5gnmB"
```

Expected: the site builds with link validation green (the new public types appear in the generated C# reference; `/extending/commands/` and `/extending/http-conventions/` resolve).

---

## Task 15: Base, rings, reviews, security core, plan-guard, PR report, PR

- [ ] **Step 1: Take `main`**

#424 (HX-07) merges into `main` before this PR. `git fetch origin && git merge origin/main` (merge-commit style), resolve nothing by hand that HX-07's merge did not touch, re-run `scripts/test-ring0`. From here every diff is against `origin/main` and the PR's base is `main`.

- [ ] **Step 2: CI-parity Release build, rings and the out-of-ring suites**

```bash
docker run --rm -u "$(id -u):$(id -g)" -e HOME=/tmp -e DOTNET_CLI_HOME=/tmp -v "$PWD":/src -w /src mcr.microsoft.com/dotnet/sdk:10.0 sh -c 'dotnet build MMLib.Alvo.slnx -c Release -p:HUSKY=0 2>&1 | grep -E "error [A-Z]+[0-9]+" | sort -u'
```

The container runs as you, so its `bin`/`obj` output is yours and nothing needs cleaning — **no `git clean`** (it would remove ignored local state: `.claude/settings.local.json`, `website/node_modules`, `.superpowers/`, the Husky install). Expected: no output. Fix any `error CAxxxx`/`IDExxxx` in a `fix(…): …` commit and re-run — watch CA1031 on `LedgerSweeper` (suppressed with its justification), CA1062 on the public extension methods' parameters, CA2007 in the snippets, CA1812 on the internal DI-only classes (`CommandEndpointFilter`, `AlvoCallerFilter`, `CommandSimulator` — instantiated by the container), CA1000 on `AlvoCaller.BindAsync`/`AlvoCommandContext.BindAsync` (static members on a generic interface's implementation are the interface's contract — suppress with that justification if raised), CA1716 on a parameter named `input`. Then `scripts/test-ring0`, `scripts/test-ring1`, `scripts/test-ring2` (both engines: `PostgresCommandTests`, `PostgreSqlLedgerTests`, and the API invariant suite including `DocumentContractTests.A_document_with_commands_holds_the_ruleset` and `scripts/lint-api`), `scripts/test-admin-e2e` (whole), `(cd website && npm run build)`. Expected: all green; the committed OpenAPI snapshot, the Vacuum ruleset and the TeaPie path set unchanged (`git diff --stat origin/main...HEAD -- test/MMLib.Alvo.Api.Tests/OpenApiDocumentTests.The_document_is_stable.verified.txt schema/openapi-ruleset.yaml test/teapie` lists nothing). The assistant eval is not run: no case reads a command (Task 12 Step 4).

**Mutation shards (Ruling 42, replacing Ruling 32).** The security-relevant command and webhook code stays under a CI mutation leg — none of it moves to the on-demand `stryker-config.api.json`.

1. *The core shard keeps what its suite kills.* `stryker-config.json` (killers in `MMLib.Alvo.Tests`) already mutates `Commands/**` and `Webhooks/**`; it keeps `CommandAccessJudge`, `CommandBinding`, `AccessTranslator`, `CommandRegistry`, `CommandRun`, `CommandRunner`, `CommandAnswers`, `CommandKeys`, `AlvoCommands`, `CommandSimulator` and `StandardWebhookSignature` — each has its killers in the core suite (`CommandAccessJudgeTests`, `CommandKeysTests`, `StandardWebhookSignatureTests`, moved there for this, …). Add to its `mutate` only the HTTP glue its suite cannot reach: `"!**/Commands/Internal/CommandEndpointFilter.cs"`, `"!**/Commands/Internal/CommandInputs.cs"`, `"!**/Commands/Internal/CommandGateCheck.cs"`, `"!**/Webhooks/Internal/WebhookReceiver.cs"`, `"!**/Webhooks/Internal/LedgerSweeper.cs"` (`Api/**` is already excluded).
2. *A new leg for the HTTP glue.* Create `stryker-config.commands-http.json` — `"project": "MMLib.Alvo.csproj"`, `"test-projects": ["MMLib.Alvo.Api.Tests/MMLib.Alvo.Api.Tests.csproj"]`, `"test-runner": "mtp"`, `"coverage-analysis": "off"`, `"additional-timeout": 300000`, reporters `progress`/`cleartext`, and `mutate` exactly: `**/Commands/Internal/CommandEndpointFilter.cs`, `**/Commands/Internal/CommandInputs.cs`, `**/Commands/Internal/CommandGateCheck.cs`, `**/Webhooks/Internal/WebhookReceiver.cs`, `**/Webhooks/Internal/LedgerSweeper.cs`, `**/Api/Internal/CallerGate.cs`, `**/Api/Internal/AlvoCallerFilter.cs`, `**/Api/Internal/AlvoGateWitness.cs`, `**/Api/AlvoCommandEndpointRouteBuilderExtensions.cs`, `**/Api/AlvoWebhookEndpointRouteBuilderExtensions.cs`. Thresholds: `break` set from the first probe's measured score rounded down to a multiple of 5, never above 80 (the header's *MOVEMENT RULE*). In `.github/workflows/mutation.yml` add a matrix leg after `security core (rules, auth, rest)`: `name: commands and webhooks over HTTP`, `config: stryker-config.commands-http.json`, `project: src/MMLib.Alvo/MMLib.Alvo.csproj`, `mutants:`/`suite:` from the probe, `timeout:` twice the probe's measured wall clock scaled to a 4-vCPU runner (the header's rule), with a comment naming the measurement; add `'stryker-config.commands-http.json'` to the `paths:` filter; and in the *DECLARED GAP* header block add a paragraph: "Carved out of the gap by HX-08: the command filter, the webhook receiver, the caller gate, the gate witness and their map calls — the security-relevant HTTP glue — run on their own leg against `MMLib.Alvo.Api.Tests` (`stryker-config.commands-http.json`); the rest of `Api/**` stays on #143." Keep the per-mutant cost honest: the gap block measures `MMLib.Alvo.Api.Tests` at 6.3 s/mutant, so a few hundred mutants is roughly the `rules, auth, rest` leg's budget. **If the probe projects past 180 minutes,** do not move a file to an on-demand config: narrow the leg to the six security files (`CommandEndpointFilter`, `CommandGateCheck`, `WebhookReceiver`, `CallerGate`, `AlvoCallerFilter`, `AlvoGateWitness`) and report the measurement in the PR — the leg's existence is the ruling, its exact membership is a measurement.
3. *Re-probe every shard.* Then re-probe **every** shard's declared `mutants:`/`suite:` in `.github/workflows/mutation.yml`, one Stryker at a time, never two in the repo at once: `cd test && dotnet tool run dotnet-stryker -f ../stryker-config.<shard>.json --concurrency 4` for every `config:` the matrix names (canary, expressions, the core shard, the new commands-http leg, data-ef-core, data-ef-rest, data-sqlite, data-postgresql), stopping each once its log prints the mutants created and the initial test run's count — except the new leg, which runs to completion once, because its `break` and `timeout` are set from that run. Record each with a `# probed <date> (was N)` comment and a dated block in the header's *RE-PROBED* table; run `scripts/test-assert-mutation-run` (it reads the configs it names; the new one is none of them, so it needs no change — confirm by grep). Commit as `ci(mutation): a leg for the command and webhook HTTP glue; re-probe every shard after HX-08`.

- [ ] **Step 3: Reviews (substitutes) and the security-core checklist**

Freeze the tree. `/code-review` and `/security-review` are user-only: dispatch two reviewer subagents over `git diff origin/main...HEAD`, labelled "substitute for /code-review high" and "substitute for /security-review". Point the code reviewer at: `CommandRun`'s at-most-once and after-answer guards under concurrency; `CommandRunner.FinishAsync` — every path claims ⇒ answers, commits or releases exactly once, and the post-handler writes are not cancellable; `EfCoreCommandLedger`'s claim race on PostgreSQL (the unique violation rolled back, never committed; any other failure propagates); the claim tokens — every write after a claim conditional on its token, a lost commit poisoning the unit (Ruling 36); the gate witness's grouped re-check and its lock; `ForEntity`'s finally forwarding only when one was recorded; the `RequestDelegateFactory`-wrapped receiver (the body replayed, `ContentLength` reset, route parameters passed); `AlvoCaller`/`AlvoCommandContext` slots never shared across requests; `ProblemResultFactory.RefusalFor` mapping exactly what `GuardAsync` mapped before. Point the security reviewer at: access judged before the handler on every path — mapped and internal — through the one `CommandAccessJudge` the simulator also reads, refusing a caller missing the tenant or user its rule reads before evaluating (negated rules, Ruling 33); rules over hidden fields refused (Ruling 35); the row read through the caller's policy before the rule (404 indistinguishable from absent); the scope gate (read+write on a bound entity, `*:write` for unbound commands; Ruling 39); the key's scope including the caller's tenant, user and API key id (no cross-caller or cross-credential replay; Ruling 34), and answers expiring after 24 h; the replay answered before the rule — and why that discloses nothing the caller was not already answered; the webhook signature compared in fixed time over the exact body bytes, the window both ways, verification before dedup and before `ResolveTenant`, which gets only the verified bytes (path and headers unsigned — Ruling 37), the id length bound, no body, secret, id or signature in a log or a response; the handler as `System(tenant)` never cross-tenant; `AsSystem()` inside a command (the page's `cancel_stay`) gated by the command's own rule; #184's witness unremovable by any convention order. Then run the `alvo-security-core-review` checklist — §6.2 lists HX-08 — recording each item's evidence for the PR report: *SQL predicate* (no new predicate rendering; the ledgers bind every value — `CommandLedgerTable`/`InboundWebhookTable`, table names from validated prefixes only); *authorization in the WHERE* (the bound row is read through `IAlvoSession.GetAsync`, the port's own policy path; no in-memory filter of rows — the access rule judges one already-authorized row); *fail-fast compile* (access rules compiled at boot with every extension, `old.`/`changed()` refused, empty rules refused at registration, typed lambdas at the map call; the apply guard refuses an unbinding change); *cross-tenant isolation* (the key's scope carries the tenant; webhook handlers run `System(tenant)`; `CommandEngineFacts.A_key_spent_by_one_caller_is_not_replayed_to_another`; the unreadable-row 404 fact; a two-tenant fact is not added — commands write only through the port, which the existing two-tenant suites hold, and the key scope is per tenant by `AlvoIdempotency.IdentityOf`); *dynamic entities* (not built, #41 — no new path); *default-deny* (no command without an access rule; an unbound command answers 403; anonymous judged by the rule; a webhook with no tenant runs `System(null)`, which every tenant-scoped entity refuses); *before-hooks in-transaction, budgeted, network-free* (untouched; the command handler is deliberately outside any transaction and its external call goes before its unit — `An_external_call_before_the_unit_runs_with_no_transaction_open`; R-B1 inside the unit — `A_handler_calling_the_injected_data_port_inside_its_unit_is_refused`). Close it with: **mark this change `needs-deep-review`.** Fix findings, re-run ring0, one `fix(…): …` commit per finding.

- [ ] **Step 4: `alvo-plan-guard`**

Dispatch `alvo-plan-guard` last (read-only); act on drift it reports. The PLAN marker stays where it is.

- [ ] **Step 5: PR report**

Use the `alvo-pr-report` skill (it dispatches `alvo-pr-reporter` and publishes the 8-section page). Evidence: acceptance (1)–(10) → tests (the table below), the three public-API diffs against the public-surface list, the both-engine runs of `CommandEngineFacts` and `LedgerFacts`, the Vacuum fact, the admin e2e scenarios, the Release build, the security-core checklist and both substitute reviews, the mutation re-probe, and the rulings — calling out Ruling 1 (Condition, not Access), 5 (bind-then-fill slots, fail closed), 6 (scopes for commands), 7 (claim lease, replay before the rule, the 409s), 9 (what is replayable), 12 (commands need a relational provider), 19 (the witness, not a `Finally`), 22–24 (webhook verification, rotation, order) and 32 (mutation shards).

- [ ] **Step 6: Push and open the PR**

```bash
git branch --show-current   # hx/08-commands-http
git push -u origin hx/08-commands-http
```

`gh pr create --base main`. The body is the skill's five-line pointer, carries `Closes #385` and `Closes #184` once each, and ends with `https://claude.ai/code/session_01NJAtafM29iddLwfhb5gnmB`. Then `gh pr edit --add-label needs-deep-review`. After the merge, read #385 and #184 back (`gh issue view 385`, `gh issue view 184`) to confirm both — and only they — were closed; comment on #33 that HX-08 shipped inbound webhooks (Standard Webhooks receivers) and on #154 that `alvo_inbound_webhooks` is swept at 7 days while `alvo_command_requests` has no retention yet (#115).

---

## Rulings and deviations this plan takes

Each is recorded in the spec amendment (Task 14 Step 6) so a later reader can tell a decision from an oversight.

1. **Command access compiles in `CelProfile.Condition`, not `CelProfile.Access`.** §4.9 names `CelProfile.Access` "over `@user`/`@tenant`", but that profile refuses `@tenant` by design (it is the descriptor's project-scoped `access` block: `CelTypeChecker`'s `AccessProjectScopedMessage`), and §5/X7 add no profile. A command bound to no entity compiles against a fieldless scope (`<command>`), so it reads only `@user` and `@tenant`; one bound to an entity compiles against it, the row as the current image (`status`, or `new.status` from a typed lambda). `old.` and `changed()` are refused — a command's row has no before-image. Host CEL functions are admitted, as in every condition. §4.9's "`record.`" spelling does not exist in Alvo's CEL; the row's fields are read bare.
2. **Typed access lambdas reuse the `when` translator.** `Expression<Func<AlvoConditionCaller, bool>>` (unbound) and `Expression<Func<TEntity, AlvoConditionCaller, bool>>` (bound) are rewritten into an `EventCondition<T>` lambda — the row becomes `e.New`, the caller `e.Caller` — and translated as a create's condition, so §5 gains no access-condition type and a typed and a CEL rule compile to one tree. A refusal quotes the rewritten node and names `(access)`.
3. **An untyped entity-bound overload, `MapAlvoCommand(name, pattern, entity, access, handler)`,** beside §5's `MapAlvoCommand<TEntity>`: D1 gives every entity an untyped path, and a host with no typed record of the entity must still be able to bind a command to it.
4. **Commands are `POST`.** A command is an unsafe, non-idempotent action (RFC 9110 §9.2.2); a GET would be cached, prefetched and replayed by intermediaries.
5. **`AlvoCaller` and `AlvoCommandContext` bind from a per-request slot that Alvo's filter fills** — minimal APIs bind parameters before endpoint filters run, so `IBindableFromHttpContext.BindAsync` returns the request's slot and the filter starts it. Reading either on an endpoint no Alvo filter ran on throws, rather than answering as anonymous: fail closed. `AlvoCommandContext.Record` (the bound row as the caller reads it) is a member §4.9 does not list — the filter has read the row anyway, and a handler re-reading it would race its own access decision.
6. **API-key scopes for commands:** a command bound to an entity needs a scope that writes it (`stays:write` or `*:write`); one bound to none needs `*:write`, because it can write anything its handler writes. `.WithAlvoCaller()` on a plain endpoint checks no scope — it names no entity — and every data call it makes is still judged by the descriptor's rules. Scopes stay an HTTP narrowing; the port judges by policy. **Amended by Ruling 39:** a bound command needs the read scope too.
7. **The idempotency ledger claims before it runs.** A key is claimed (autocommit, a **15-minute** lease) right after the caller resolves — so a replay is answered before the row and the rule are judged again: the key's scope is the caller's own identity, so a replay returns only what that caller was already answered, and a command that changed or deleted its row can still be retried. A refusal before the handler (404, 403, 508) gives the claim back. The spec's "written in that unit at its commit" is the commit mark (`committed_at`); the answer is recorded after the handler returns. Concurrent use of one key is `409` (`idempotency-conflict`, "still being processed"), as the IETF draft recommends; a key reused for a different request is `409` `idempotency-conflict` too — **a deviation from the draft's `422`**, kept for one Alvo meaning across the Data API and commands; a run that committed and never recorded an answer is `409` ("do not resend"). **Amended by Rulings 34, 36 and 41:** the scope also names the credential, every write after the claim is fenced by its token, and answers expire after 24 hours.
8. **The fingerprint covers the bound inputs, not the raw body:** filters run after binding, so the body is already read; the command name, the route's `id` and every bound argument that is not a service or request plumbing are serialised as JSON and hashed (SHA-256). Two requests that bind to equal inputs share a fingerprint whatever their whitespace.
9. **A replay answers the first status and JSON value only** — from `IStatusCodeHttpResult`/`IValueHttpResult`, or a plain value as 200; no other headers. A result with no status (a redirect, a file, a stream) or a body over **64 KiB** is not replayable: its run is recorded as committed without an answer, and a retry is refused `409` rather than run again; a warning names the command. **Amended by Ruling 49 (L4):** a plain `string` result is not replayable either; `Location` is not replayed.
10. **The completion event** is `alvo.command.<name>.completed`, subject `commands/<name>` or `<entity>/<id>`, payload `command` (and `entity`, `id` for a bound command) — never the input or the answer: the outbox is read by every consumer, and an input may carry what the caller sent a payment provider. It is minted by `AlvoFrameworkEvent.Create`, which §4.10 assigns to HX-13; HX-08 is its first user, so it arrives here.
11. **When there is no completion.** A run that opened no unit and answered non-2xx (or threw) emits nothing and gives its key back. A run whose unit committed keeps its event whatever it answers afterwards — the effects happened — and records its answer; one that threw after the commit records `500`, so a retry is answered `500` and does not charge twice. **Amended by Ruling 43:** a unit opened and not committed is a run with no completion, whatever it answers.
12. **Commands need a relational provider.** `ICommandLedger` and `IAlvoTransactions` are EF-driver services; a composition without them (the in-memory reference) refuses to boot with a command registered, at `extensions/<name>/provider`, rather than failing on the first keyed request.
13. **Mapped commands register at map time** into an internal `CommandRegistry`, which `ExtensionRegistry.All` reads live; the boot's first extension check seals it, and a command mapped after that throws at the map call — a later one could never be bound or catalogued. **Amended by Ruling 40:** sealed by the first binding, not only the boot.
14. **The catalog's vocabulary for commands** (appended per HX-04 Ruling 1/17): `AlvoExtensionKind.Command = 1`; `AlvoExtensionStage.Http = 1` for a mapped command and `PreTransaction = 2` for an internal one (it runs in its invoker's flow before its own unit — the promise validate hooks will make); `Route` is the pattern as mapped (a `MapGroup` prefix is not included), `HttpMethod` `POST`, `Access` the CEL text; no `Description` (`.WithSummary` is the mapped command's description surface) and no `Event`. Webhook receivers are not catalogued: §4.13 has no kind for them and they bind nothing in the descriptor.
15. **OpenAPI:** operationId `commands.<lower camel case of the name>` (the ruleset's `alvo-operation-id-shape`), tag `commands`, refusals `$ref` the shared components (401, 403, 404 for a bound command, 409, 422), the anonymous-or-key security. Two names mapping to one operationId (`a_b`, `a.b`) refuse boot. **Amended by Ruling 45:** 400, 412 and 508 are listed too.
16. **A handler of `alvo.command.<n>.completed` naming no registered command refuses boot** (`extensions/<id>/event`): it would never run.
17. **Internal commands:** `AddCommand<TCommand>(name, access)` takes an access rule (§4.9: "same access rule"); they bind no entity (an input carries ids); `IAlvoCommands.InvokeAsync` answers nothing, takes no key, is refused inside an open unit (R-B1: it would write beside it) and past the depth limit, and resolves the command from a fresh DI scope; its dependencies are resolved at invocation, not checked at boot. No caller is exempt — `AlvoContext.System` passes a rule only because it holds `admin`.
18. **The simulator's command verdicts are a new public shape:** `ManagementPolicyVerdict.Commands` (init, `[]`) of `ManagementCommandVerdict { Name, Entity, Access, Allowed }` — the commands bound to the simulated entity and those bound to none. `Allowed` is null when the rule reads the row (the simulator reads no row, D4); `false` for a command not bound to the current catalog, as the filter answers.
19. **#184 by a gate witness, not a `Finally` convention.** Each generated route's authorization filter is added through a factory that counts its own invocation while the request delegate is composed; the data source checks every witness after the ungrouped build and again after each group build (counts before vs after, under its lock). *Amended in Task 2 (measured on .NET 10, `RouteEndpointDataSource.CreateRouteEndpointBuilder`):* group conventions, then route conventions, run **before** the filter pipeline is composed, and every `Finally` — route or group — runs **after** it, so a `Finally` that clears `FilterFactories` removes nothing (pinned by `GateWitnessTests`' two `…_finally_…_cannot_remove_alvos_gate` facts). The witness therefore guards the conventions that run before composition; a route-level `Finally` check is rejected because it would read `FilterFactories` after composition — the list, not the delegate — refusing a harmless finally that clears it and unable to tell a look-alike from Alvo's factory without an identity check. The grouped check is not insurance: under a group the grouped endpoints are the ones that serve, and a convention can behave differently there (`A_convention_that_removes_alvos_filter_only_under_a_group_fails_the_group_build`). The count means "Alvo's factory was invoked" (#184's third question, measured by `Every_generated_endpoint_composes_alvos_gate_into_its_request_delegate`); a convention assigning `EndpointBuilder.RequestDelegate`, or wrapping the factory and discarding its result, is out of scope (Ruling 49 L1) and fails closed to an anonymous caller. A failure is the existing `AlvoDataApiConventionException` path: empty table, readiness `Failed`, a critical log naming the route.
20. **`ForEntity` replays a recorder** on the endpoints whose `AlvoEndpointMetadata.Entity` matches; on `MapAlvoDataApi()`'s builder a name the applied schema does not declare fails the route-table build; finally conventions are forwarded only when one was recorded (a plain builder's `Finally` throws by default).
21. **`AlvoEndpointKind` mirrors the internal `DataApiEndpointKind` member for member, values pinned**, and `DataApiOperationMetadata` stays the OpenAPI transformer's source (§4.9); a test holds the two enums equal.
22. **Webhook verification:** `v1` entries only (`v1a` and others ignored); the stored secret may hold several space-separated `whsec_` secrets (the receiver's side of rotation; the sender's side is several signatures in the header); a missing or undecodable secret is `500` with an Error log naming the receiver and the secret's name; a failed check is `401` with problem type `unauthenticated` and **no** `WWW-Authenticate` — Standard Webhooks names no challenge, and no new problem type is added.
23. **Duplicates are acknowledged, not refused with an error:** a handled id answers `200` without running the handler (a Standard Webhooks sender retries anything else, which would dead-letter a delivery that succeeded); an id still running is `409`; a handler that throws or answers non-2xx gives its id back so the sender's retry runs it. "Refused" in acceptance (4) is read as "not run again".
24. **Verification precedes dedup and the tenant:** an unverified request records no id (an attacker cannot burn a real sender's ids) and runs no host code — `ResolveTenant` is called only for a verified delivery, and is given the verified bytes (Ruling 37). A receiver with no tenant runs `System(null)`, which every tenant-scoped entity refuses. **Amended by Ruling 37:** the resolver receives the verified body bytes; path and headers are unsigned.
25. **The receiver reads the body itself** (1 MiB by default, `413` past it, at most 64 MiB) and replays the verified bytes into the host's handler, which is built with `RequestDelegateFactory` — so the handler binds what was signed. Receivers are excluded from the OpenAPI document (a sender follows its provider's contract), and a pattern may be mapped once.
26. **The sweep** is an hourly hosted service deleting ids received more than **7 days** ago (a constant: far longer than any sender's retry horizon and the 5-minute window); a failed sweep is logged and retried, never fatal; delivery stays at least once — a handler that wrote and crashed before its id was recorded runs again, so the docs ask for handlers idempotent on the id. **Amended by Ruling 41:** the same `LedgerSweeper` forgets command keys after 24 hours.
27. **Handler exceptions:** the command filter answers the six data-port families a handler's own data calls can raise (403, 404, 412, 409 idempotency/constraint, 508) as the Data API does; any other exception — an `ArgumentException` from host code included, whose message is the host's — propagates and is answered `500` by `AlvoExceptionHandler`, which now covers command endpoints, never echoing its message. A malformed `Idempotency-Key` is `422` before the handler. **Amended by Ruling 45:** `500` in Alvo's problem shape only when the host maps Alvo's problem details.
28. **A command whose catalog is not primed** (a dashboard-first host awaiting its first descriptor) answers `403` "not bound" — fail closed.
29. **The bookkeeping after a handler is not cancellable** (`CancellationToken.None`): a client that hung up must not leave committed effects without their key record, nor a claimed key held for its lease.
30. **`.WithAlvoCaller()` resolves exactly as the Data API does** (`CallerResolution`, the configured resolver and header) and publishes the caller through `IAlvoContextAccessor`; no cookie path is added.
31. **The docs' HTTP snippets run on `WebApplication` + `TestServer`,** not `AlvoTestHost` (which has no HTTP surface); `MMLib.Alvo.DocsSnippets.Tests` gains `Microsoft.AspNetCore.TestHost`.
32. **Mutation — superseded by Ruling 42.** (As first planned, the HTTP-only command and webhook files left the core shard for the on-demand `stryker-config.api.json`, which has no matrix leg; the review's M5 and the coordinator ruled that security-critical code does not leave CI.) Every shard's declared counts are re-probed (HX-07 Ruling 38).

33. **One `CommandAccessJudge` for every path (B1).** The HTTP filter, `IAlvoCommands` and `CommandSimulator` all judge through one internal `CommandAccessJudge` (`EnsureAdmitted(name, row, caller)`, `Preview(name, caller)`), so the simulator cannot drift from what runs. A bound command carries `Required = EventConditionReads.Required(access)` — computed the way event-handler conditions compute it — and the judge refuses a caller missing the tenant or the user the rule reads **before** evaluating, as `PolicyEngine` does with `RequiredContext.TenantIdMissingFrom`/`UserIdMissingFrom`. Without it, `!(@tenant in […])` or `@user.id != …` evaluates `null` comparisons and admits the anonymous or tenantless caller. Facts: a negated rule refuses the anonymous and the tenantless caller (`CommandAccessJudgeTests`, `CommandEngineFacts`, `AlvoCommandsTests.A_negated_identity_rule_refuses_the_anonymous_caller`), and `CommandSimulatorTests` holds `Preview` equal to `EnsureAdmitted` for every caller shape.
34. **The idempotency scope is per credential (B2).** The key's scope is `command:<name>/<AlvoIdempotency.IdentityOf(caller)>/<API key id or ->`: two API keys of one user never share a key, nor do two tenants of one user (`IdentityOf` carries the tenant). Facts for both, on both engines. **Departure, recorded:** `AlvoIdempotency`'s contract for the Data API is "never a rendered response" — it replays by re-reading the row. A command has no row to re-read, so its ledger stores the rendered answer (status + JSON, ≤ 64 KiB, Ruling 9); the per-credential scope is what keeps a stored answer from reaching anyone but the credential that was answered it.
35. **Access rules may not read hidden fields (H1).** A rule over a bound entity that reads a field the policy hides from some callers (`PolicyCatalog.TryGetEntity(…).Hidden`) is refused at binding (`extensions/<id>/access`, "… hides from some callers") — the 403/allow outcome would otherwise be an oracle on a value the caller cannot read. The apply guard refuses a descriptor change that would hide a field a bound command reads, through the same binder (the `HandlerBinding.ExcludedReads` precedent).
36. **Fencing tokens on command and webhook claims (H2).** A claim (or reclaim after the 15-minute lease) writes a fresh `claim_token`; commit, answer, release and complete are all `… WHERE claim_token = @token`. On a command, the commit mark is written inside the handler's unit: zero rows means the lease was taken over, so `CommitCommandInUnitAsync` poisons the unit (`WriteUnit.Poison`) and throws `CommandClaimLostException` — the handler's writes and its completion event roll back rather than commit beside another run's. A stale release or answer changes nothing; a stale webhook completion returns `false` and is logged. Facts for a run that outlives its lease, on both engines.
37. **The webhook tenant comes from signed bytes (H3, coordinator's ruling).** `ResolveTenant` is `Func<HttpRequest, ReadOnlyMemory<byte>, TenantId?>` and receives the verified body; the request body is replayed before and after it so the handler still reads it whole. Standard Webhooks signs only id, timestamp and body: the XML docs and the page say the path, query and headers are unsigned and tell hosts to derive the tenant from the body or from a per-tenant receiver with its own secret. A null answer falls back to `Tenant`; with neither, the caller is `System(null)`. Facts: a resolver reading the body leaves it for the handler; no tenant and no resolver runs `System(null)`.
38. **The gate witness covers command endpoints (M1).** `MapAlvoCommand` composes its access filter through `AlvoGateWitness`, and `CommandGateCheck` verifies every command route after the boot primes the descriptor (so the Data API is not materialised early); a convention that strips the filter fails the start with `AlvoStartupRefusedException`. The check walks the composite data sources except `AlvoEndpointDataSource`; the cost on a mixed `MapGroup` (its endpoints built once more at boot) is accepted and stated.
39. **A bound command needs read and write scope on its entity (M2).** It reads the row before it writes, so an API key needs `stays:read` and `stays:write` (`CallerGate.AdmitAsync(…, alsoRead: true)`); an unbound command still needs `*:write`. A write-only key is `403 out-of-scope`. Writes the handler makes to other entities are judged by the data port's own policy for the identity it writes as — the command's scope is an HTTP narrowing on its own entity, not a grant over others.
40. **The command registry seals on first binding (M3).** `ExtensionBinder.Binding` and the catalog's lazy initialisation seal it, not only the boot — a binding that happened before a late `MapAlvoCommand` would never include it. Mapping after that throws, naming the command and "before applying a descriptor".
41. **Stored command answers expire after 24 hours (M4, coordinator's ruling).** `CommandLedgerLimits.Retention = 24 h`; the hourly `LedgerSweeper` (renamed from `InboundWebhookSweeper`) deletes command keys claimed before the cutoff alongside 7-day-old webhook ids, on an index on `claimed_at`. A replay after expiry runs as a new request; the page says so. 24 h matches the IETF draft's guidance that keys be kept "at least 24 hours" and common practice (Stripe), and bounds a table that otherwise grows with every keyed call (#115).
42. **Command and webhook code stays in CI mutation (M5, coordinator's ruling).** Supersedes Ruling 32. Files whose killers are in `MMLib.Alvo.Tests` stay on the core shard (`StandardWebhookSignatureTests` and `CommandKeysTests` move to the core suite for this); the HTTP glue — the command filter, inputs and gate check, the webhook receiver and sweeper, `CallerGate`, `AlvoCallerFilter`, `AlvoGateWitness` and the two map extensions — gets its own matrix leg, `stryker-config.commands-http.json`, against `MMLib.Alvo.Api.Tests`. Every shard is re-probed. If the budget does not fit, the leg narrows to the security files; nothing moves to an on-demand config.
43. **A unit opened and not committed emits nothing (M6).** `CommandRun.OpenedWithoutCommit` — a handler that caught its rolled-back unit and answered 2xx — gets no completion event, and its key is released (logged `UnitRolledBack`), because the effects the event would announce did not happen. Fact: `CommandRunnerTests.A_handler_that_catches_its_rolled_back_unit_and_answers_2xx_emits_no_completion`.
44. **400 is listed but not Alvo's shape.** A body that does not bind is refused by minimal API's own binding before Alvo's filter runs; the document lists `400` (inline, on commands with a body) so clients see it, and the page says it is the framework's answer, not an Alvo problem type. Alvo does not re-implement body binding to own it.
45. **OpenAPI lists every refusal a command can answer, and 500 needs the host's mapping (M7).** Besides 401/403/404/409/422: `412` (a handler's precondition refusal from the port), `508` (depth; a new shared `LoopDetected` component, `loopDetected`), and `400` per Ruling 44. Ruling 27's "answered 500 by `AlvoExceptionHandler`" holds only when the host calls `AddAlvoProblemDetails()` and `UseExceptionHandler()`; otherwise the 500 is the host's own. Stated in the XML docs of `MapAlvoCommand` and on the page.
46. **The claim never rides the request's cancellation (L3).** The key and the webhook id are claimed with `CancellationToken.None`: a client that hangs up right after the claim commits must not strand the key for the lease. And only a unique violation (`IAlvoSqlDialect.DecodeConstraintViolation(…)?.Kind == AlvoConstraintKind.Unique`) is read as a concurrent claim; any other database failure propagates as a 500 instead of a misleading 409.
47. **Framework DDL is ensured once per table (L6).** `OnceDdl` runs `CREATE … IF NOT EXISTS` once per process and table (a `TaskCompletionSource`, retried after a failure), beside `SystemSchemaInitializer`'s boot creation — no per-call DDL, so PostgreSQL's concurrent `IF NOT EXISTS` race on a cold table cannot fire on a hot path.
48. **Framework events partition per tenant (L7).** `alvo.command.<name>.completed` is keyed `framework.event:{tenant or the tenantless scope}:{subject}`, so one tenant's completions of an unbound command never queue behind another's (the `AlvoCustomEnvelope.PartitionKeyFor` convention).
49. **The review's remaining LOWs, as proposed.** (L1) The witness's remarks claim only "the factory composed Alvo's gate"; a convention assigning `EndpointBuilder.RequestDelegate` outright is out of its scope, and says so. (L2) `ForEntity` checks names only on `MapAlvoDataApi()`'s builder; a named rate-limit policy is one partition, so the docs show a limiter partitioned on `AlvoEndpointMetadata.Entity`. (L3) Ruling 46. (L4) A `string` result is not replayable (written as text, replayed it would be a JSON string), and `Location`/headers are not replayed. (L5) The review's premise was wrong — `For(AlvoContext)` is on `IAlvoSessions`, not `IAlvoSession` — so `FakeCommandUnit` stays as is, documented, and now asserts the claim token. (L6) Ruling 47. (L7) Ruling 48. (L8) A webhook receiver's returned builder configures the outer endpoint — an endpoint filter never reaches the RDF-built handler — and its system caller is bound to the handler's `AlvoCaller` only, not published through `IAlvoContextAccessor`; both documented rather than aligned, because the receiver is not a caller-resolving endpoint. (L9) Task 8's `GuardAsync`/`PortRefusalFor` extraction is its own behaviour-neutral commit, `refactor(api): one mapping of the data port's refusals`, before the filter.
50. **`ForEntity` on a builder other than `MapAlvoDataApi()`'s forwards through `Finally`** (Task 1 review I1). A group's ordinary conventions run before a route's own conventions, so `AlvoEndpointMetadata` — added by `Protect`, an entry convention — is not there yet and an `Add`-forwarded convention would reach zero routes, silently. On any builder that is not the Data API's own, `ForEntity` therefore forwards its ordinary and finally conventions, in that order, through `builder.Finally`, where the metadata exists; a builder whose `Finally` is unsupported throws (the interface default) rather than reaching nothing. Pinned by `DataApiConventionTests.ForEntity_on_a_group_the_data_api_is_mapped_under_reaches_that_entity` (red before the fix). The name is still checked only on `MapAlvoDataApi()`'s builder.
51. **A handler's data-port refusal is the run's answer (Task 8 review I1).** A refusal of the data port's six families (403, 404, 412, 409 idempotency/constraint, 508) thrown by the handler — after its unit committed or without one — is mapped inside the handler delegate given to `CommandRunner.FinishAsync`, so the mapped problem is the recorded answer and a replay with the key returns it (acceptance (3)); a non-2xx run without a committed unit still releases its key (Ruling 11). Refines Ruling 11's "records `500` for a throw" and Ruling 27: only an exception outside those families is a throw. Fact: `CommandEngineFacts.A_port_refusal_after_the_unit_committed_is_the_answer_a_replay_returns` (both engines).
52. **Uploaded files are fingerprinted by content (Task 8 review M5).** The idempotency fingerprint covers `IFormFile` and `IFormFileCollection` arguments: each file's name, length and the SHA-256 of its content — two different uploads under one key are a `409`, never a replay. Fact: `CommandEngineFacts.Two_different_uploads_under_one_key_are_an_idempotency_conflict` (both engines).

---

## Acceptance criteria → tasks

| HX-08 criterion | Task(s) | Test |
|---|---|---|
| (1) access denied → 403 before the handler; `MapAlvoCommand<TEntity>` on an unreadable row → 404 | 6, 8 | `CommandEngineFacts.An_access_rule_that_refuses_the_caller_answers_403_before_the_handler_runs`, `An_access_rule_reading_the_row_refuses_a_row_in_the_wrong_state`, `An_entity_command_on_a_row_the_caller_cannot_read_answers_404_before_the_handler_runs`, `A_key_whose_scopes_exclude_writing_the_entity_is_403_out_of_scope`, `A_key_that_writes_but_cannot_read_the_bound_entity_is_403_out_of_scope`, `A_negated_tenant_rule_refuses_a_tenantless_caller` (SQLite + PostgreSQL); `CommandAccessJudgeTests`; `CommandBindingTests.An_access_rule_reading_a_hidden_field_is_refused`; snippet `A_guest_is_refused_before_anything_is_charged` |
| (2) the completion event reaches an `AddEventHandler` handler | 4, 5, 7, 8 | `CommandEngineFacts.A_completed_command_s_event_reaches_an_event_handler`, `A_command_with_no_unit_completes_after_its_2xx` (both engines); `LedgerFacts.A_unit_that_commits_carries_the_event_and_the_commit_mark_and_an_answer_follows`, `A_completion_without_a_unit_commits_the_event_and_the_answer_together` (both engines); `CommandBindingTests.A_handler_of_a_mapped_command_s_completion_binds` |
| (3) a replayed `Idempotency-Key` returns the first result without re-running the handler | 5, 7, 8 | `CommandEngineFacts.A_replayed_idempotency_key_answers_the_first_result_without_running_the_handler_again`, `A_key_spent_by_one_caller_is_not_replayed_to_another`, `A_key_spent_by_one_api_key_is_not_replayed_to_another_key_of_the_same_user`, `A_key_reused_with_a_different_body_is_an_idempotency_conflict`, `Two_concurrent_requests_with_one_key_run_the_handler_once` (both engines); `CommandKeysTests.One_user_in_two_tenants_files_one_idempotency_key_apart_even_under_one_key_id`; `LedgerFacts.A_run_whose_lease_was_taken_over_cannot_commit_and_its_writes_roll_back`, `Command_keys_claimed_more_than_a_day_ago_are_swept_and_their_key_runs_again` (both engines); `CommandRunnerTests`; snippet `A_retried_confirmation_is_answered_without_charging_twice` |
| (4) a reference Standard Webhooks signature verifies; a 6-minute-old timestamp and a repeated `(pattern, webhook-id)` are refused; rows older than 7 days are swept | 5, 11 | `StandardWebhookSignatureTests.A_reference_signature_verifies` (+ tampered body, rotation, other versions); `WebhookReceiverTests.A_timestamp_six_minutes_off_is_refused`, `A_repeated_webhook_id_on_one_receiver_does_not_run_the_handler_again`, `Rows_older_than_seven_days_are_swept`, `A_resolver_reading_the_verified_body_leaves_it_for_the_handler`, `A_receiver_with_no_tenant_and_no_resolver_runs_the_handler_as_the_system_in_none`; `LedgerFacts.The_sweep_removes_ids_received_before_its_cutoff_and_keeps_later_ones`, `A_stale_webhook_run_can_neither_complete_nor_release_a_live_claim` (both engines) |
| (5) `ForEntity("bookings")` rate-limits only bookings routes | 1 | `DataApiConventionTests.ForEntity_rate_limits_only_that_entity_s_routes`, `ForEntity_reaches_all_ten_routes_of_its_entity_and_no_other`, `A_ForEntity_naming_no_declared_entity_fails_the_route_table_build`, `ForEntity_forwards_a_finally_convention_to_that_entity_only`, `ForEntity_on_a_group_the_data_api_is_mapped_under_reaches_that_entity`; snippet `ForEntity_limits_the_rooms_routes_and_no_other` |
| (6) a convention removing Alvo's filter fails the route-table build (#184) | 2 | `GateWitnessTests.A_convention_removing_alvos_filter_fails_the_route_table_build`, `A_finally_convention_removing_alvos_filter_fails_the_route_table_build`, `A_group_finally_convention_removing_alvos_filter_fails_the_build`, `A_convention_rebuilding_the_filter_list_with_the_same_filters_still_fails`, `Every_generated_endpoint_composes_alvos_gate_into_its_request_delegate`, `A_convention_adding_a_filter_around_alvos_still_builds_and_still_gates`, `Every_mapped_command_composes_its_gate`, `A_group_convention_removing_a_command_s_filter_refuses_the_start` |
| (7) OpenAPI lists the command (Vacuum green) | 8, 10 | `CommandDocumentTests` (three facts); `DocumentContractTests.A_document_with_commands_holds_the_ruleset` (ring2, Vacuum) |
| (8) an internal command invoked from a handler runs its access rule and emits its event | 9 | `InternalCommandTests.An_internal_command_invoked_from_a_handler_runs_its_access_rule_and_emits_its_event`, `A_handler_running_as_a_caller_the_rule_refuses_never_runs_the_command`, `A_caller_the_rule_refuses_is_an_authorization_refusal`, `Invoking_a_command_inside_an_open_unit_is_refused`; snippet `An_internal_command_is_invoked_in_process_under_its_access_rule` |
| (9) no credential on a `WithAlvoCaller()` endpoint binds `Anonymous`; a bad key → 401 | 3 | `AlvoCallerTests.No_credential_on_an_alvo_caller_endpoint_binds_anonymous`, `A_bad_key_on_an_alvo_caller_endpoint_is_401_before_the_handler`, `Reading_an_alvo_caller_on_an_endpoint_without_the_filter_throws_instead_of_answering_anonymous`; `CommandEngineFacts.No_credential_is_judged_as_anonymous_and_an_unknown_key_is_401`; snippets `No_credential_is_the_anonymous_caller_and_a_key_is_its_own`, `A_key_that_cannot_be_used_is_401_before_the_handler` |
| (10) DI-injected `IAlvoData` inside `ctx.TransactAsync` refused (R-B1); an external call before it runs with no transaction open; a second `ctx.TransactAsync` throws; the completion event and idempotency record commit in the handler's unit | 5, 7, 8 | `CommandEngineFacts.A_handler_calling_the_injected_data_port_inside_its_unit_is_refused`, `An_external_call_before_the_unit_runs_with_no_transaction_open`, `A_second_TransactAsync_throws_and_the_first_unit_stands`, `The_completion_event_and_the_idempotency_record_commit_in_the_handler_s_unit`, `A_handler_that_throws_after_its_unit_committed_is_not_run_again` (both engines); `CommandRunnerTests.A_second_unit_throws_and_the_first_one_stands`, `A_unit_carries_the_completion_and_the_commit_mark_and_the_answer_follows`; `LedgerFacts.A_unit_that_rolls_back_leaves_no_event_no_commit_mark_and_no_row` (both engines) |
| Scope: boot validation, apply guard, catalog | 6, 12 | `CommandBindingTests`, `CommandRegistryTests` (incl. `A_command_mapped_after_a_first_binding_is_refused`), `ExtensionCatalogTests` (two facts), `CommandSimulatorTests` (incl. `The_simulator_answers_exactly_as_the_judge_admits`, `A_negated_rule_previews_refused_for_a_caller_without_the_value`) |
| Review hardening (Rulings 33–49) | 4–12, 15 | the facts above, plus `CommandRunnerTests.A_handler_that_catches_its_rolled_back_unit_and_answers_2xx_emits_no_completion`, `LedgerFacts.A_stale_release_does_not_free_a_live_claim`, `A_completion_by_a_run_that_lost_its_claim_is_refused_with_its_event`, `AlvoCommandsTests.A_negated_identity_rule_refuses_the_anonymous_caller`, `CommandDocumentTests` (400/412/508); the `commands and webhooks over HTTP` mutation leg (post-merge) |
| Dashboard: *Commands* lane; simulator evaluates a command's access rule | 12, 13 | `LifecycleLaneCommandTests`, `CommandVerdictsTests`; e2e `CommandScenarios` (two scenarios), `ExtensionCatalogScenarios` (badge 4) |
| Assistant: commands in `get_extensions` | 12 | `ManagementToolsTests.The_extensions_tool_shows_a_command_s_route_and_access_rule` |
| Docs: *Commands and inbound webhooks*, *HTTP conventions* | 14 | `StayDeskCommandsTests`, `FrontDeskEndpointsTests` in `MMLib.Alvo.DocsSnippets.Tests`; website build |
| Security core (§6.2) | 15 | `alvo-security-core-review` checklist, `/security-review` substitute, `needs-deep-review` |
