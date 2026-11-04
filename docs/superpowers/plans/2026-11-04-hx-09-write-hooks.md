# HX-09 — Write hooks in three phases — Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** A host developer can run C# in the write pipeline at three phases — **validate** (before any transaction, network allowed, top-level writes only), **before-write / before-delete** (inside the write's transaction: reject, `Set` fields, write other rows through `write.Transaction`, publish) and **after-write / after-delete** (inside the transaction, after the row and its rollups exist: reject and roll back, write children, publish) — typed (`Entity<T>`) or untyped (`Entity("name")`, `Entities`, `"*"`), with CEL or lambda `when`, under a per-invocation timeout and operation budget, a boot-time network/data-port ban, a re-run of `WITH CHECK` and the field facets over every `Set`, a self-write refusal, `AsSystem()` that is tenant-bound and audited, `SuppressHostHooks` for system maintenance, a durable rejection log, and validate hooks an administrator can switch off — visible in the catalog, the Lifecycle tab, the rules/hooks editors, the policy simulator and the apply preview.

**Architecture:** One new **driver port**, `IWriteExtensionRunner` (Abstractions / Rules, the `IBeforeHookRunner` precedent), called by `EfAlvoData` at the stages §4.1 places: stage 0 (an autocommit idempotency pre-lookup) and stage 2 (validate, with E9's autocommit pre-read) run in the driver's public entry points before `ReplayableWriteAsync` and before any `BEGIN`; stages 4/5 (before-write, then `WITH CHECK`/tenant scope re-run over the union patch) and 7/7b (after-write, then a post-image re-read when a hook wrote) run inside the write's transaction. A top-level write on an entity+operation that has a host in-transaction hook runs in a **unit of its own** — HX-07's `WriteUnit`, put on the flow, with the write as its one outer call — so hooks get the unit's caller-bound `IAlvoTransaction` one hop deeper (`write.Transaction`), the unit's DI scope, R-B1 refusals and poisoning; a write already inside a `TransactAsync` unit uses that unit. The core implements the port (`WriteExtensionRunner`, `MMLib.Alvo.Hooks.Internal`): it selects the bound hooks of the entity and stage, evaluates their compiled `when`, builds the typed or untyped context, wraps `write.Transaction` in a counting, self-write-checking, token-linking `HookTransaction`, awaits each hook to completion, measures it, and turns what it recorded into the union patch, an `AlvoWriteRejectedException` (422/409/403, E2), or an internal `HookFailedException` (500 `hook-failed` with a `reason`) that has already poisoned the unit. Registrations join HX-03's `ExtensionRegistry`, boot validation (`ExtensionBindingCompiler`), the apply guard, the catalog and the stats.

**Tech Stack:** .NET 10 (C# 14), EF Core relational + ADO.NET, Microsoft.Data.Sqlite, Npgsql, `System.Diagnostics` (`ActivitySource`, `Meter`), `Microsoft.Extensions.TimeProvider.Testing`, xUnit v3 on Microsoft.Testing.Platform, Shouldly, NSubstitute, Verify + PublicApiGenerator, Testcontainers (PostgreSQL leg), Blazor (admin), Microsoft.Playwright (admin e2e), Astro/Starlight (docs), Stryker.NET (mutation).

**Spec:** `docs/superpowers/specs/2026-10-14-host-extensibility-design.md` — §7 *HX-09* (scope, docs, dashboard, acceptance (1)–(13)); §4.1 stages 0/2/4/5/7/7b and the *Stage 2 rules (B1)*, *Stages 3–5*, *Stages 6–8* and *Batches and replays* paragraphs; §4.4 (contexts D2, rejection model incl. Ruling X, ordering, budgets and poisoning, the §4.4.5 boot ban — the analyzer `ALVO1001` is HX-10 —, security re-run and system access); §4.5 and *HX-07 settled details* (1), (2), (9), (13), (14), (30), (42), (54); §4.6 (one depth counter); §4.8 *HX-06 settled details* (1), (10), (11), (16), (17), (21); §4.9 *HX-08 settled details* (2), (12), (14), (57); §4.13 and *HX-04 settled details* (1), (3), (12), (13), (20); §4.14; §4.15 (`Hooks.AssertRan`, *HX-02 settled details* "the recorder"); §2.2 C12, C13, C14, C15, C16; §2.3 X1, X8, X17, X19; §3.1 Q1, Q4, Q5, Q9; §3.3 E1, E2, E9, E10, E11; §5 rows `09`; §6.2. Issue #386.

**Prior art read and cited in the Rulings** (CLAUDE.md, *Study the problem before designing it*):

- **PostgreSQL triggers** (`CREATE TRIGGER`, *Overview of Trigger Behavior*): row-level `BEFORE` triggers may change `NEW` and see it as the row to be stored; `AFTER` row triggers see the final row (generated columns included) and can still abort the statement by raising; a `BEFORE` trigger returning `NULL` **silently skips** the row; trigger cascades are bounded only by `max_stack_depth` (`pg_trigger_depth()` exists to let a trigger stop itself); **RLS `WITH CHECK` is evaluated after `BEFORE ROW` triggers**, over the row the triggers produced (*CREATE POLICY*). Adopted: before/after row-level phases inside the statement's transaction, the after phase able to abort, `WITH CHECK` re-run over the patched row (stage 5). Deviated: no silent skip (Ruling 18), a bounded cascade (MaxDepth, 508), a time and operation budget (Rulings 10–11).
- **EF Core `SaveChanges` interceptors** (`ISaveChangesInterceptor.SavingChangesAsync`/`SavedChangesAsync`): code before and after the save on the same `DbContext` and transaction, async, no budget and no restriction on what it calls. Adopted: async in-transaction phases on the write's own connection; deviated: a budget, a timeout and a boot-time dependency ban (X1).
- **Hasura** — *actions* (synchronous custom logic called over HTTP before the mutation, outside the database transaction) and *input validation* (an HTTP webhook called before a mutation, with a timeout) versus *event triggers* (post-commit, at-least-once, from the database's own log). Adopted: validate is Hasura's input validation (outside the transaction, network allowed, timeout); event handlers (HX-03) are its event triggers. Hasura has no in-transaction custom code but Postgres triggers; Alvo's before/after-write fills that slot in C# with the network banned.
- **Supabase** — database work is Postgres triggers/RLS; custom code is post-commit (database webhooks over `pg_net`, edge functions) or Auth *hooks* that run synchronously with a hard timeout (a few seconds) and fail the request on timeout. Adopted: a hard per-invocation timeout that fails the write (Q5's 250 ms / 5 s).
- **Firebase** — Firestore triggers (`onDocumentWritten` …) run only after the write commits; validation is Security Rules; synchronous *blocking functions* exist only for Auth events, with a 7-second deadline. Adopted: a pre-write synchronous phase exists only where it can refuse the request (validate), bounded by a deadline.
- **Salesforce governor limits** (already X3's source): a per-transaction DML/query-row budget that throws an uncatchable `LimitException`. Adopted as `MaxOperations` with poisoning (a caught `AlvoBudgetExceededException` still rolls back).
- **RFC 9457** (one problem `type` per kind of refusal — `hook-failed` is new, the three rejection kinds reuse `validation`/`conflict`/`forbidden`, E2), **RFC 6901** (violation pointers), **RFC 5842** (508 for depth, X17).

## Global Constraints

- Worktree `/Users/martiniak/Developer/GitHub/Burgyn/MMLib.Alvo-wt-hx09`, branch `hx/09-write-hooks`, based on `main` at `04d33dda` (HX-01…HX-08 merged). Issue **#386**, epic #370; advances #371 and the C# half of #22. One writer per worktree; never `git add -A` (stage each task's files by path); `git branch --show-current` prints `hx/09-write-hooks` before every commit and before the push. **Never run `git clean`**, and never delete anything under `.superpowers/`. A bare `ls` hides git-ignored files in this shell — use `/bin/ls` or `find`.
- **Scope is §7 HX-09 only.** No analyzer (`ALVO1001`), no host-function budget, no `expression-failed`, no calibration run (HX-10); no `IAlvoScheduler` (HX-13 — §4.4.5's "`IAlvoScheduler` is allowed" has nothing to allow yet); no batch hooks (N-3: batches run hooks per row); no MCP; nothing in `schema/project.schema.json` changes. The standalone host (`MMLib.Alvo.Host`) registers no hook: its OpenAPI document, `OpenApiDocumentTests.The_document_is_stable.verified.txt`, the Vacuum ruleset and the TeaPie path set do not move. **The assistant is untouched**: its always-in-context instructions sit ~5 bytes under their spec cap, §7 HX-09 names no assistant item, and `get_descriptor`'s `hostExtensions` counts read the catalog, so write hooks appear there without an edit (Ruling 33).
- **Public surface added is exactly** (every addition §5's rows `09` does not list is a Ruling — 19, 21, 27, 29):
  - Abstractions `MMLib.Alvo.Data`: `AlvoWriteOperation` (`Create = 0`, `Update = 1`, `Replace = 2`, `Delete = 3`); `AlvoBudgetExceededException` (`InvalidOperationException`, the three standard constructors).
  - Abstractions `MMLib.Alvo.Rules`: `IWriteExtensionRunner` (`Validates`, `RunsInTransaction`, `ValidateAsync`, `BeforeWriteAsync`, `AfterWriteAsync`), `WriteInvocation` (required-init record: `Entity`, `Operation`, `Caller`, `Now`; init `Old`, `New`, `Saved`, `Transaction`, `Services`).
  - Abstractions `MMLib.Alvo.Management`: `AlvoExtensionKind.Validate = 2`, `.BeforeWrite = 3`, `.AfterWrite = 4`, `.BeforeDelete = 5`, `.AfterDelete = 6`; `AlvoExtensionStage.InTransaction = 3`; `ManagementExtension.TopLevelOnly`, `.Enabled` (init, default `true`).
  - Abstractions `MMLib.Alvo.Events`: `IDeliveryStore.SetDisabledAsync`, `IDeliveryStore.DisabledAsync` (abstract — breaks implementers: CHANGELOG, HX-06 Ruling 1's precedent); Testing: three `DeliveryStoreContractTests` rows.
  - Core `MMLib.Alvo.Hooks`: `IValidateWriteHook`, `IValidateWriteHook<T>`, `IBeforeWriteHook`, `IBeforeWriteHook<T>`, `IAfterWriteHook`, `IAfterWriteHook<T>`, `IBeforeDeleteHook`, `IBeforeDeleteHook<T>`, `IAfterDeleteHook`, `IAfterDeleteHook<T>` (each one `ValueTask …Async(context, CancellationToken)`); contexts `ValidateWrite<T>`, `BeforeWrite<T>`, `AfterWrite<T>`, `BeforeDelete<T>`, `AfterDelete<T>`; `WriteCondition<T>`; `AlvoWriteRejectedException`, `AlvoWriteRejectionKind` (`Invalid = 0`, `Conflict = 1`, `Forbidden = 2`).
  - Core `MMLib.Alvo.Extensions`: `AlvoHookOptions` (`Description`, `Order`, `Timeout`, `MaxOperations`); `AlvoEntityBuilder` gains `Validate`, `BeforeWrite`, `AfterWrite`, `BeforeDelete`, `AfterDelete` (class and delegate overloads); `AlvoEntityBuilder<T>` the same with lambda and CEL-string `when`.
  - Core `MMLib.Alvo.Api`: `AlvoProblemTypes.HookFailed` (`"hook-failed"`, in `All`).
  - Admin: no new component type; Razor parameters are public by construction (HX-04 Ruling 19), so the Admin baseline grows by exactly `LifecycleTab.Stats`, `HooksTab.Extensions`, `RulesTab.Extensions`.
  Everything else is `internal`.
- **Wire and naming constants (verbatim):** problem type `hook-failed` (500), members `extensions` (array of one extension id) and `reason` ∈ {`exception`, `timeout`, `budget`, `invalid-set`, `self-write`, `unbound`, `row-vanished`}; rejections answer `422 validation` / `409 conflict` / `403 forbidden` with `violations` (`pointer`, `code`, `message`, `fixSuggestion: null`) and `extensions`; precedence 403 > 409 > 422; default timeouts **250 ms** (before/after/delete) and **5 s** (validate); bounds: in-transaction `Timeout` in (0, 30 s], validate `Timeout` in (0, 60 s], `MaxOperations` 1–1 000, default **50**; the 51st call throws `AlvoBudgetExceededException`; catalog wire names `validate`, `beforeWrite`, `afterWrite`, `beforeDelete`, `afterDelete`, `inTransaction`; framework table `<prefix>_hook_rejections (id, at, extension, entity, operation, kind, code, actor)` (`AlvoFrameworkTables.HookRejectionsSuffix = "_hook_rejections"`), index `ix_<prefix>_hook_rejections_extension_at (extension, at)`; operations-log operations `disable-extension`, `enable-extension`, `suppress-host-hooks`; telemetry: `ActivitySource("MMLib.Alvo.Hooks")` span `alvo.hook` (display `alvo.hook {id}`, kind `Internal`) with tags `alvo.subscriber` (the id), `alvo.entity`, `alvo.operation`, `alvo.stage`, `alvo.outcome` ∈ {`succeeded`, `rejected`, `failed`}; `Meter("MMLib.Alvo.Hooks")` instruments `alvo.hook.duration` (ms, histogram), `alvo.hook.rejections`, `alvo.hook.operations`, `alvo.hook.system_access` (counters); log events `HookFailed` (Error), `ExtensionSystemAccess` (Warning), `HookRejectionNotRecorded` (Warning), `ManagementAudit` (Warning, E11), `HookSetRefused` (Error).
- **Engine parity:** every end-to-end hook fact runs on SQLite (`MMLib.Alvo.TestHost.Tests`) and PostgreSQL (`MMLib.Alvo.Data.PostgreSql.Tests.Integration`) through one shared abstract class `test/_shared/testhost/WriteHookFacts.cs`; every HTTP status fact runs through `test/_shared/api/DataApiEngineTests.WriteHooks.cs` on both engines; the delivery-store rows run through `DeliveryStoreContractTests` on both engines. New SQL is ANSI (`CREATE TABLE IF NOT EXISTS`, `CREATE INDEX IF NOT EXISTS`, `INSERT`, `UPDATE … WHERE`, `SELECT`); no dialect member is added.
- **Security core (§6.2 lists HX-09):** a hook's `write.Transaction` acts as the caller; every `Set` is re-judged by `WITH CHECK` + tenant scope (stage 5) and by the field's facets (`MutationTarget.Violation`); `AsSystem()` is `AlvoContext.System(caller.Tenant)` with the caller's provenance — never another tenant — logged and metered; in-transaction hook types may not take a network or data-port dependency (boot refusal); timeout, budget, depth, self-write and any escaping exception poison the unit; validate never runs with a transaction open; a registered hook that does not bind the current catalog **fails closed** (Ruling 7); a rejection on a hidden field carries no pointer (Ruling X). Task 14 runs the `alvo-security-core-review` checklist, a labelled `/security-review` substitute, and labels the PR `needs-deep-review`.
- **Data-package allow-lists:** `ChangeTrackerReachTests._sqlComposingFiles` gains exactly `HookRejectionTable.cs` and `EfAlvoData.Hooks.cs` (the latter from Task 4: the stage-0 idempotency pre-lookup opens the context's connection with `GetDbConnection` — its `WritePropertyBag` patch and `SingleAsync` re-reads from Task 3 match none of the scan's patterns, so Task 3 left it off the list, a permission covering nothing); `EfCoreHookRejectionLog.cs` calls the table's helpers and composes no SQL.
- Public XML docs cite no `§` and no `#issue` (DocsGen's `RemarksFilterTests`). XML docs on every public member.
- C# style: `alvo-dotnet-conventions` — no inline comments, methods ≲ 25 lines, English only, `ConfigureAwait(false)` in `src/`, never in tests (xUnit1030); every test call passes `TestContext.Current.CancellationToken` (xUnit1051). `.cs` files are **UTF-8 with BOM + CRLF**: after creating or editing `.cs` files run `dotnet format whitespace MMLib.Alvo.slnx --include <file> <file> …` (file paths, never a directory).
- Single-test runs: `dotnet test --project <test project> --filter-class '*<ClassName>'` (MTP). Public-API baselines: `*PublicApiApprovalTests` in `test/MMLib.Alvo.Abstractions.Tests`, `test/MMLib.Alvo.Tests`, `test/MMLib.Alvo.Testing.Tests` (if present; else the Testing baseline's own project — find it with `find test -name 'PublicApi.MMLib.Alvo.Testing.verified.txt'`). To accept a baseline: run the approval test, read the `*.received.txt` diff — it must add exactly the symbols the task lists — then move it over the `*.verified.txt`. A grown `PublicApi.*.verified.txt` trips the turn-review gate: dispatch `alvo-snapshot-judge`, and justify each added symbol against `alvo-architecture-rules` (*public is the contract*) and the public-surface list above.
- Rings: `scripts/test-ring0` after every task, `scripts/test-ring1` after each task marked **[slice end]**, `scripts/test-ring2` before the PR. `scripts/test-admin-e2e` runs **whole once**, in Task 14; Task 12 runs only the scenarios it touches (filtered).
- **CI parity:** rings are Debug on the local SDK; CI builds Release with `TreatWarningsAsErrors`, `AnalysisLevel=latest-recommended` and `EnforceCodeStyleInBuild`. Task 14 runs the Release build in the CI SDK container, as the user, before anything is pushed.
- Commits: Conventional Commits, one per task, each message ending with `Claude-Session: https://claude.ai/code/session_01NJAtafM29iddLwfhb5gnmB`. No `Closes`/`Fixes` keyword anywhere in a commit message; the PR body carries `Closes #386` once.

## Review Focus

0. **A hook that catches the failure meant to stop it** — a `try { … } catch { }` around a budget refusal, a self-write refusal, a cancelled call after its 250 ms, or a depth refusal from `AsSystem()` recursion — must still roll the write back and answer 500 (or 508 for depth), never commit what the hook did before it caught: `WriteHookFacts.A_hook_that_catches_its_budget_refusal_still_rolls_back_with_reason_budget`, `…_catches_its_timeout_…_reason_timeout`, `…_catches_a_self_write_refusal_…`, `A_before_write_hook_writing_its_own_entity_as_the_system_recursively_is_refused_508` (Task 11, both engines); `WriteExtensionRunnerTests.A_caught_budget_refusal_still_fails_the_hook_and_poisons_the_unit` (Task 8). A fact that only asserts the exception would pass for a hook that let it escape; each asserts **no row and no outbox row** too.
1. **A `Set` the caller could not have written themselves** — `owner_id` set to another user, `tenant_id` set to another tenant, a value of an undeclared enum member, a 5 000-character string into `maxLength: 40` — must be refused on both engines with the same answer (403 for the policy, 500 `invalid-set` for the facet), and SQLite (which enforces no length) must not store it: `DataApiEngineTests.A_set_of_another_owner_is_forbidden_on_every_engine`, `A_set_of_an_undeclared_enum_value_is_invalid_set_on_every_engine`, `A_set_moving_a_row_to_another_tenant_is_forbidden_on_every_engine` (Task 9); `HookSetsTests` (Task 8).
2. **A validate hook and an idempotent retry or a batch** — a client retrying a keyed create after a timeout, a 200-row import with a fraud check — must call the external service once per **new** write and never with a transaction holding SQLite's write lock or PostgreSQL row locks: `WriteHookFacts.A_validate_hook_never_runs_on_an_idempotent_replay`, `A_batch_validate_hook_runs_once_per_row_with_no_transaction_open` (the hook writes through its own `Data` mid-batch; on SQLite a held write lock would block it until the busy timeout) (Task 11).
3. **A write-hook registration the descriptor stops fitting** — a dashboard-first host booting `Awaiting`, then applying a descriptor that lacks the hooked entity's field the `when` reads — must not let writes through unvalidated: the write is refused 500 `unbound`, not silently unguarded: `WriteExtensionRunnerTests.A_registered_hook_that_does_not_bind_the_current_catalog_fails_the_write_closed` (Task 8), `WriteHookBindingTests.Removing_a_field_a_bound_write_hook_reads_is_refused_at_apply` (Task 7).
4. **A hook that names a hidden field** — `Invalid("too-low", "…", field: "internal_score")` on a field the descriptor hides from some callers, or a `Set` into it that breaks its facet — must answer without the pointer and without Alvo naming the field anywhere (the host's own message is the host's): `WriteHookFacts.A_rejection_on_a_hidden_field_carries_no_pointer` (Task 11), `DataApiEngineTests.A_rejection_on_a_hidden_field_answers_with_no_pointer_on_every_engine` (Task 9), `HookRejectionsTests.A_hidden_field_gets_the_body_pointer` (Task 5).

---

## Rulings and deviations this plan takes

Each is recorded in the spec amendment (Task 13) as *HX-09 settled details* (1–59), so a later reader can tell a decision from an oversight.

1. **A top-level write runs in a unit of its own only when a host in-transaction hook is registered for its entity and operation** (`IWriteExtensionRunner.RunsInTransaction`). §4.5 says "a top-level write opens and commits its own unit", and HX-07 Ruling 2 left "the one place to change" to HX-09. Making *every* write a flow-visible unit would add a DI scope, a deadline timer and a `SAVEPOINT`/`RELEASE` pair to every write of every host — measurable on the hot path — and change shipped behaviour for hosts with no hook (HX-07 acceptance (7)). With a hook registered the write gets the whole unit: on the flow (R-B1 refuses a hook's `IAlvoData` call), the default 30 s `Timeout` (§4.5's deferred clause, applied here), one DI scope, poisoning, and its events share the unit's correlation id (HX-07 Ruling 12). Without one it is byte-for-byte HX-07. Cost if wrong: a host CEL function's data call inside a hook-free write still waits on the write gate instead of being refused (pre-existing, `IBeforeHookRunner`'s remarks).
2. **Hooks may call `AsSystem()` on `write.Transaction`** (its log line carries the caller — Ruling 46) (the security decision HX-07's final review left open, *HX-07 settled details* (14)). §4.4.6 specifies it and the booking recipe needs it: a guard must read reference data (`opening_hours`) the caller may not read. It is bounded so it grants nothing beyond "the system, in the caller's tenant": the view's context is `AlvoContext.System(caller.Tenant) with { Provenance = caller.Provenance }` — never another tenant, and a tenantless caller's view is `System(null)`, which every tenant-scoped entity refuses; its calls still count against the budget, still pass the self-write check and the hook's token, still run the nested write's declarative and host hooks (as the system), and still raise depth. Each `AsSystem()` call logs `ExtensionSystemAccess` (Warning: extension, entity, operation) and increments `alvo.hook.system_access`. Host code is trusted operator code (Q6), and the boot ban (§4.4.5) removes every other escalation route an in-transaction hook could take (`IAlvoSessions`, `IAlvoData`, `IAlvoTransactions`), so `AsSystem()` is the one, audited door.
3. **A hook failure is an internal `HookFailedException : InvalidOperationException`** — `IAlvoData`'s family for "an invariant the implementation relies on", so an `IAlvoData` caller meets no new public type — carrying the extension id and one fixed `reason`. `AlvoExceptionHandler` answers it 500 `hook-failed` with `extensions` and `reason`, never the host's message; `HookFailed` logs the host exception's **type** only. §5 lists no public type for it.
4. **A rejection in a batch is that row's `AlvoRowRefusal`** (`Code` = the hook's code, `Message` = the hook's, no fix), through an internal Abstractions interface `IRowRefusal` that `AlvoWriteRejectedException` implements — the driver cannot name a core type. The batch answers what every refused batch answers, 403 `forbidden` with one violation per row (`BatchViolations.FromPort`); a batch does not carry the kind. A rejection at after-write refuses the batch after its rows were inserted, which rolls them back (no commit), all-or-nothing as today.
5. **Disabled validate hooks live in `alvo_subscriber_controls.disabled_at/disabled_by`** (the columns HX-06 Ruling 21 created for this), through two new abstract `IDeliveryStore` members, `SetDisabledAsync` and `DisabledAsync` — HX-06 Ruling 1's precedent: one port per provider, abstract (a default that did nothing would report a hook off while it still ran). The runner reads the disabled set with one autocommit read per write that has a validate hook to run, so a switch made on one replica is honoured by every replica on its next write.
6. **Catalog vocabulary** (appended per HX-04 Rulings 1/17): `AlvoExtensionKind.Validate = 2 … AfterDelete = 6`, `AlvoExtensionStage.InTransaction = 3`; a validate hook's stage is `PreTransaction` (HX-08 Ruling 14 named it for this) with `TopLevelOnly = true`; the other four are `InTransaction`. `ManagementExtension.Enabled` and `TopLevelOnly` ship now — HX-04 (1) and HX-06 (11) deferred both to this PR, the first to fill them. Write-hook `Operations` are policy wire names (HX-04 (3)): write stages `["create", "update"]` (a replace is an update), delete stages `["delete"]`.
7. **A registered write hook that does not bind the current catalog fails the write closed** — 500 `hook-failed`, reason `unbound`. A handler that cannot bind records dead `unbound` deliveries (HX-03 (6)) because its work can wait; a guard that cannot bind would otherwise let every write through unguarded. Reachable only on lenient catalogs (an `Awaiting` dashboard-first boot, a `Failed` stored boot), because the boot refuses and the apply guard refuses an unbinding change (Task 7).
8. **The rejection log** — `<prefix>_hook_rejections (id, at, extension, entity, operation, kind, code, actor)`, plus `id` (UUIDv7, the primary key) and an index on `(extension, at)` beyond §4.4.2's column list — is written autocommit **after the unit ends**, for every rejection whatever the unit's outcome (a `TransactAsync` delegate may catch a rejection and commit the rest); a validate rejection, with no unit, is written at once. The internal Abstractions port `IHookRejectionLog` is implemented by the EF driver: inside a unit it hands the row to the unit, which the driver flushes once the unit has released the write turn and left the flow (an autocommit insert there would wait for the unit's own turn). `actor` is `AlvoIdempotency.IdentityOf(caller)` (`tenant/user` ids). No payload, no message. A failed insert is logged `HookRejectionNotRecorded` and never turns the caller's answer into a 500; a crash between the rollback and the insert loses the row (stated). No retention (#154).
9. **`write.Transaction` is one hop deeper lazily.** The view's context carries `depth + 1`, and each write and publish through it checks the limit inside the unit's call, where the refusal poisons (HX-07 Ruling 9) — not at construction. HX-07's eager `EfAlvoTransaction.Deeper()` would refuse a hook at depth 8 that never writes; it becomes lazy and its HX-07 fact is rewritten to "refused when it writes" (Task 3).
10. **Every call through `write.Transaction` counts toward `MaxOperations`** — reads, writes, batches, `PublishAsync`, and calls through the `AsSystem()` view (X8: "counts port calls"). The 51st throws `AlvoBudgetExceededException` into the hook *and* poisons the unit with `HookFailedException(budget)`, so catching it changes nothing; the runner raises the failure when the hook returns.
11. **The timeout is enforced through the token and measured on return; the hook is always awaited.** Every call through `write.Transaction` runs under the hook's token (the unit's token, linked with `Timeout` on the host's `TimeProvider`); the runner never abandons a hook (a hook's calls share the unit's one connection — abandoning one would put two commands on it, the defect HX-07 Ruling 54 closed), so a hook that ignores its token is failed when it returns. A cancelled call poisons the unit with its `OperationCanceledException` first (HX-07 Ruling 9); the new internal `IAlvoFlowUnit.Blame` lets the runner replace a recorded cancellation — and only a cancellation — with the hook's timeout, so the unit fails as `timeout`, not as a client cancel. Validate hooks have no unit; their timeout fails the write before it starts.
12. **What escapes a hook.** `AlvoChainDepthExceededException` passes unchanged (508, X17); a `HookFailedException` from a nested write's own hook passes unchanged (it names the innermost hook); **everything else** — a host exception, a nested write's rejection or policy refusal the hook did not catch — is `HookFailedException(exception)` naming the hook it escaped, and poisons the unit. A nested rejection is not passed through: its pointer names a field of a write the caller never made.
13. **Stage 2 runs in the driver's public entry points** (amended by Rulings 41, 42, 49), the only code that knows where the replay loop and `BEGIN` are: once per top-level write, before `ReplayableWriteAsync` and before `OneBatchAsync`'s transaction (C14, C16). E9's pre-read for update, replace and delete is an autocommit, non-locking, **unmasked** read under the operation's own decision (replace reads under update's): a row the decision does not admit is not validated, and the write then answers as it always would (404; a replace creates). A create's `New` is the authorized candidate (stage 1) **without `id`**: the row id is minted per attempt. Validate's `Data` is `IAlvoSessions.For(caller one hop deeper)`, so a validate hook writing what it validates stops at the depth limit instead of recursing.
14. **Stage 0 decides only whether validate runs.** A keyed write with a validate hook first looks its key up autocommit (`IdempotencyTable.FindAsync` with no transaction): a hit skips stage 2 and proceeds to the in-transaction lookup, which replays with no hook at all, exactly as today; a miss validates. One replay path, not two; a race between the two lookups is resolved by the existing loop (C15).
15. **A replace's operation is `Replace` on both branches**; a replace that creates the row has `Old == null`.
16. **What a `Set` may write** (amended by Rulings 47, 48, 52). Undeclared fields, `id`, the audit columns, `deleted_at`, the version column, `computed` and `rollup` fields are refused — 500 `invalid-set` — as are values whose CLR type the field cannot hold (`FieldClrType.Of`; `long` into `decimal` is the one widening, Ruling V's) and values outside the field's facets (`MutationTarget.Violation`: `maxLength`, enum values, `format`, precision/scale, `required`). `tenant_id` and fields a policy freezes for callers are **allowed** and judged by stage 5 (`WITH CHECK`, tenant scope → 403) — deviation 75's ruling (a hook is not the caller; `WritePayloadGuard` is not re-run). A rejecting hook's `Set`s are discarded; a later hook sees an earlier hook's `Set`s in `New`. The `invalid-set` log line names the field and the facet unless the field is hidden (Ruling X), and never the value.
17. **Pointers** (amended by Ruling 53). `field:` becomes `PayloadViolations.PointerTo(field)` (RFC 6901, `/starts_at`); a field the descriptor carries any `hidden` flag for gets `PayloadViolations.BodyPointer` (`""`), decided from the descriptor, not the caller's mask (Ruling X). Alvo never echoes the field name of a hidden field; the host's own `message` is the host's text, and the docs say not to name hidden fields in it.
18. **A rejection is always an error.** PostgreSQL's `BEFORE` trigger can return `NULL` to skip a row silently; a host hook cannot — a write either happens or the caller is told why. A host that wants "skip" rejects with `Conflict`.
19. **`AlvoWriteRejectionKind`** is a public enum §5 does not list: `AlvoWriteRejectedException.Kind` needs a type, and the three wire statuses are its only values. Append only; precedence is computed, never read from the numeric value.
20. **`write.Publish` is `PublishAsync`**, the .NET async naming, on `BeforeWrite<T>`, `AfterWrite<T>`, `BeforeDelete<T>` and `AfterDelete<T>`; it publishes through the hook's transaction (one operation of the budget), so the event commits with the write or not at all (X13).
21. **Delegate hooks ship beside class hooks** on both builders (§4.4.5 names "delegate parameters (inline hooks)"): parameters bind to the context, a `CancellationToken` or a service; a delegate returns `ValueTask`, `Task` or nothing. Class hooks are built per invocation from the hook's scope (`ActivatorUtilities`, disposed after) unless registered — the handler precedent (HX-03).
22. **Hook DI scopes.** In-transaction hooks resolve from the unit's scope — the write's own unit, or the `TransactAsync` unit it runs in — so every hook of one unit shares one scope, which is never the HTTP request's (acceptance (13)). Validate hooks resolve from one fresh scope per write, shared by that write's validate hooks.
23. **The boot ban list** (widened by Ruling 54) is §4.4.5's — `HttpClient`, `IHttpClientFactory`, every type of `System.Net.Sockets` and `System.Net.Http`, `DbConnection` and its subclasses, `IEmailSender` (matched by name: Alvo references no mail package), `IAlvoEvents`, `IAlvoData`, `IAlvoSessions`, `IAlvoTransactions`, `IOutboxStore` — plus `IDeliveryStore` and `IAlvoCommands`, recorded additions: both write outside the unit (R-B1 would refuse them at runtime; boot refuses them earlier). Every public constructor's parameters of a class hook and every non-supplied parameter of a delegate hook are checked; validate hooks are exempt (they run before any transaction). Direct dependencies only — an `IServiceProvider` the hook resolves from later is the analyzer's (HX-10).
24. **Option bounds**, refused at boot (catalog-free, so an `Awaiting` boot refuses too — HX-03 (39)): in-transaction `Timeout` in (0, 30 s] (a hook must end inside its unit's 30 s default), validate `Timeout` in (0, 60 s], `MaxOperations` 1–1 000.
25. **`when` for write hooks** (each judged against the row as earlier hooks left it — Ruling 39) is CEL in `CelProfile.Condition`, compiled per entity like a declarative before-hook condition and evaluated with `CelInterpreter.EvaluatePredicate` — `new.` is the candidate (validate, before) or the stored row (after), `old.` the pre-image, `changed(f)` false on a create; delete stages refuse `new.` (#325's rule, `AfterImagePhase`); a `"*"` registration's `when` tests only `@user`/`@tenant`. A typed `when` is `Expression<Func<WriteCondition<T>, bool>>`, rewritten into an `EventCondition<T>` lambda and translated by HX-05's `ConditionTranslator` with the `Updated` verb (HX-08 Ruling 2's precedent), so a typed and a CEL condition compile to one tree. A CEL function failing in a `when` is the existing 500 `function-failed` (fail closed).
26. **Order.** Declarative before-hooks run first (stage 3, unchanged); host hooks then by `Order`, entity-specific before `"*"` at one `Order`, then registration order (§4.4.3). Equal stated `Order`s on one entity and stage log `ExtensionOrderTie` (HX-03 (10)).
27. **Telemetry.** One `alvo.hook` span per invocation carries the extension id in `alvo.subscriber` — the tag `AlvoHookRecorder` reads — so `Hooks.AssertRan` counts write hooks with no recorder change; `rejected` counts as ran (*HX-02 settled details*). `AlvoTestHost.Data` becomes an internal tracking decorator that opens a recorded root activity per call, so a write's hooks are this host's runs; a write through `Services.GetRequiredService<IAlvoData>()` stays outside the recorder, as HX-02 stated. No public member changes.
28. **Stats.** `ExtensionStats` lists write hooks beside handlers and reads `alvo.hook.duration`: `Invocations`, `Failures` (`failed`), `Rejections` (`rejected`) and p50/p95, this instance, 24 hourly buckets (HX-06 (16)).
29. **The dashboard slice.** The Lifecycle tab draws *Before the transaction* (validate) and *In the transaction* (before/after/delete) lanes only in a host that registers a write hook (HX-08 Ruling 57's rule: a lane appears with the host code that can run in it), each code item read-only with its `when`, timeout, `TopLevelOnly`/disabled badges, and its p95 and 24 h rejections from `GetExtensionStatsAsync` (absent when the stats call is refused); the On write tab and the Rules tab say "n extension(s) from code also run on create/update/delete"; the policy simulator lists `ManagementPolicyVerdict.Extensions` (HX-04 (12)); the apply preview lists the code extensions bound to every entity its plan touches. No new Razor component type.
30. **`SuppressHostHooks`** (validated since HX-07) now skips stages 4 and 7 for every write of the unit (stage 2 never runs inside a unit); declarative hooks and emission still run. It is audited once per unit: an `alvo_operations_log` row (`suppress-host-hooks`, actor `system`, target `unit:<correlation id>`, the reason) written **in the unit's transaction** — a rolled-back unit skipped nothing durably and leaves no row — and a `ManagementAudit` Warning after the commit.
31. **`Validates` and `RunsInTransaction` answer from registrations, not bindings**, so an unbound hook still opens the write's unit and then fails closed (Ruling 7) instead of being skipped by an early "nothing to run".
32. **Stage 7b** (a miss fails the write — Ruling 45) re-reads the post-image only when an after-write hook called a writing member of `write.Transaction` (`AfterWriteAsync` answers `true`), so the event and the echo carry rollups the hook's child writes moved; otherwise no extra read.
33. **The assistant is untouched** (see Global Constraints).
34. **Write hooks need a relational provider**, as commands do (HX-08 Ruling 12): with no `IAlvoTransactions`, or no `IHookRejectionLog`, registered (the in-memory reference), a write-hook registration refuses boot at `extensions/<id>/provider` — `InMemoryAlvoData` would otherwise skip every guard silently (X11).
35. **Self-write** (a batch's rows too — Ruling 43). A row is "in a hook stage" while one of its host hooks runs; the core keeps those rows in an `AsyncLocal` set that flows into the hook's nested calls. A nested update, replace or delete (single or batch) of such a row through `write.Transaction` throws `HookFailedException(self-write)` into the hook and poisons the unit. A create cannot address an existing row (the payload guard refuses `id`). An after-write hook therefore never writes its own row (§4.5).
36. **The hook context of a delete has no `Operation`** (it is always a delete) and a non-null `Old`; the write contexts' `Old` is `T?` (X18).
37. **Mutation is configured, not probed.** The runner, contexts, rejections, sets and bindings live in `MMLib.Alvo/Hooks/**`, already inside the core shard's `mutate` glob (killers in `MMLib.Alvo.Tests`); `EfAlvoData.Hooks.cs` and `UnitRun.cs` are added to `data-ef-core`'s `mutate` list (killers in `MMLib.Alvo.Data.EntityFrameworkCore.Tests`) and excluded from `data-ef-rest`; `EfCoreHookRejectionLog.cs` and `HookRejectionTable.cs` fall into `data-ef-rest`. No local Stryker run and no re-probe: mutation runs post-merge on `main` in CI, and the shard budgets are being re-sharded separately (coordinator's instruction, superseding HX-07 Ruling 38's re-probe for this PR).
38. **C13 is corrected in `events.md`**: the declarative before-hooks moved inside the transaction; `AuthorizedCandidate` did not — it runs before `BEGIN` on creates, updates authorize inside, deletes have no pre-transaction check.

Rulings taken after the plan review (`.superpowers/sdd/hx-09-plan-review.md`; H = high, M = medium, L = low finding; M8 by the coordinator's ruling):

39. **(H1) Each hook's `when` is judged against the row as the earlier hooks left it.** The runner selects the bound hooks and checks for unbound ones up front, then evaluates each hook's condition inside the loop, just before it would run, over `current` — PostgreSQL's rule for a `BEFORE` trigger's `WHEN`, which sees `NEW` as earlier triggers modified it. A guard an earlier `Set` makes applicable runs; one it makes inapplicable does not. Amends Ruling 25. Fact: `WriteExtensionRunnerTests.A_later_hook_s_condition_sees_an_earlier_hook_s_set_both_ways`.
40. **(H2) No end-to-end fact races a hook deadline.** `WriteHookFacts` start every host on a `FakeTimeProvider` nothing advances; only the timeout fact advances its own clock. `DataApiEngineTests.WriteHooks` registers every hook with a 30 s `Timeout`; the docs snippets' host runs on a never-advanced `FakeTimeProvider`. HX-07 Ruling 35's rule, applied to hooks.
41. **(M1) The caller's payload guard runs before validate.** `ValidateChangeAsync` runs `WritePayloadGuard.EnsureWritable` (and a replace's whole-row guard) before E9's pre-read; a batch's update row with a payload refusal is reported, not validated. No hook ever makes an outbound call for a payload the write will refuse — fail closed (§4.1: the policy pre-check is stage 1). Facts: `HostHookStageTests.A_validate_hook_never_sees_an_update_the_payload_guard_refuses`, `WriteHookFacts.Validate_never_runs_on_an_update_the_payload_guard_refuses_nor_on_a_row_the_caller_cannot_reach`.
42. **(M2, coordinator's form) Validate never fails open.** An update whose E9 pre-read finds no row answers 404 before any validate hook runs, so a row that becomes visible between the pre-read and the transaction cannot commit unvalidated; a batch update row not found is that row's `Unavailable` refusal. A replace whose pre-read finds no row is validated as the create it will be (`Old` null), so it is validated either way. Validate's `Old` is read without a lock and may be stale by the time the transaction runs; the docs and the architecture doc say an invariant belongs in a before-write hook. Amends Ruling 13.
43. **(M3) Every row of a batch is in a hook stage while the batch's hooks run.** `HeldRows` (now internal in Abstractions, so the driver can use it) is held by the core runner for the running row and by the driver for every id of an update or delete batch, and for every stored id of a create batch's after pass. A hook writing a sibling row of its batch is a self-write. Amends Ruling 35. Fact: `WriteHookFacts.A_batch_row_s_hook_writing_a_sibling_row_is_a_self_write` (both engines).
44. **(M4) Acceptance (8)'s "no transaction open" is proven on the connection, on both engines.** Driver facts record SQLite's own `sqlite3_get_autocommit` on the world's one connection at each validate call (`RecordingWriteExtensions.Autocommit`), not only whether a unit is on the flow; the end-to-end PostgreSQL leg asserts from inside the validate hook that no other backend of the database is `idle in transaction` (`pg_stat_activity`); the SQLite leg's hook write would block on a held writer lock.
45. **(M5, L12) Cascades run no hooks; a vanished row fails the write.** Rows a declared `onDelete` cascade or set-null changes run no host hook — a recorded deviation from PostgreSQL row triggers, which fire on cascaded rows. `IWriteExtensionRunner.AfterWriteAsync` answers the id of the last hook that wrote (not a `bool`), and when stage 7b's re-read finds no row the write fails with `HookFailedException(that id, row-vanished)` and poisons the unit — never an event or an echo of a row that no longer exists (no `?? stored` fallback). `HookFailedException` moves to Abstractions (internal) so the driver can raise it. Fact: `WriteHookFacts.An_after_write_whose_cascade_removes_its_own_row_fails_with_row_vanished_and_emits_nothing` (both engines).
46. **(M6) `AsSystem()` is audited with the caller.** `ExtensionSystemAccess` carries the extension, the tenant, the entity, the operation, the actor (`AlvoIdempotency.IdentityOf(caller)`) and the correlation id; the level stays Warning (§4.4.6). Amends Ruling 2.
47. **(M7) `Set` widens losslessly.** An integral CLR value is a `long` (`RecordWriter.Field`, as `AlvoPatch`), a `float` a `double`, and a whole number into a `decimal` field a `decimal`; anything lossy or of another type stays `invalid-set`. The widened values are the patch the driver applies. Amends Ruling 16. Facts: `HookSetsTests.An_untyped_int_or_short_set_is_a_whole_number_its_field_holds`, `A_fractional_double_into_a_decimal_field_stays_refused`.
48. **(M8, coordinator's ruling) `Set` honours `readOnly` exactly as the caller's payload would.** The driver runs the read-only half of `WritePayloadGuard` (`ReadOnlyRefusal`, made internal) over every hook patch: a field the caller's policy makes read-only is refused with the same 403 and message the caller would get (a batch row: that row's refusal). Fields constrained only by a rule's `WITH CHECK` stay judged by stage 5. Amends Ruling 16 (deviation 75 no longer covers `readOnly` for host hooks). Fact: `DataApiEngineTests.A_set_of_a_read_only_field_is_refused_as_the_caller_s_own_payload_would_be_on_every_engine`; docs caution on *Validate and guard writes*.
49. **(L1) No validate stage on deletes.** `RunsOn` gives validate hooks create and update only and §4.4.1 has no validate-delete context, so the delete entry points run no stage 2 and the delete pre-read code is not written.
50. **(L2) Tasks 10 and 11 are swapped**, so the operations task (validate switch-off) lands before the end-to-end facts that use it and every task commits green.
51. **(L3) Switching off any hook but a validate hook is refused 409**, pinned for a before-write hook: a guard cannot be disabled from the dashboard or the Management API. Fact: `AlvoOperationsWritesTests.Switching_a_before_write_hook_off_is_refused_409_and_changes_nothing`.
52. **(L4) `Set("tenant_id", …)` on an entity that is not tenant-scoped is `invalid-set` (`undeclared`)**, never an unmapped-column 500. Amends Ruling 16.
53. **(L5) A rejection points only at a declared field the descriptor never hides**; an undeclared name or a case variant gets the body pointer. Amends Ruling 17.
54. **(L6) The boot ban is wider**: also `System.Net.WebSockets`, `System.Net.Mail`, `System.Net.Quic`, `WebClient`/`WebRequest`/`HttpWebRequest`/`FtpWebRequest` (matched by name: naming the obsolete types fails the Release build), `IAlvoManagement`, `IAlvoOperations`, and the identity stores (`Microsoft.AspNetCore.Identity.*`, `MMLib.Alvo.Identity.*`). Amends Ruling 23.
55. **(L7) A hook that ignores its token and never returns holds the write open** — Alvo awaits it because it shares the write's connection (Ruling 11), and the unit's deadline rolls back only when no call is running (HX-07 Ruling 10), so on SQLite the writer lock stays held past the 30 s limit. Stated in the architecture doc and on the page; enforcing it needs out-of-process hooks (#35) or the analyzer (HX-10).
56. **(L8) Mutation entries are appended in order** at the end of the `mutate` arrays (core entries, then their `!` exclusions in the same order), because `scripts/test-assert-mutation-run` compares arrays in order; the script runs in Task 14. Extends Ruling 37.
57. **(L9) Acceptance (13)'s "request-scoped service" is read as any scope other than the unit's** — the fact opens one as an HTTP request would; the unit's scope is never the caller's.
58. **(L10) The `IDeliveryStore` break is folded into HX-06's [Unreleased] entry**, not a second "breaks implementers" line: nothing is released, so both ship in one version. Amends Ruling 5's CHANGELOG note.
59. **(L11) A batch's validate stage reports every bad row** — payload and rule refusals (`JudgedCandidate`, `PayloadRefusal`), a row not found, and validate refusals together — instead of only the validate ones.

---

## File structure

Created — Abstractions `src/MMLib.Alvo.Abstractions`:

| File | Responsibility |
|---|---|
| `Data/AlvoWriteOperation.cs` | the hook verb (X19) |
| `Data/AlvoBudgetExceededException.cs` | the 51st call through a hook's transaction |
| `Rules/IWriteExtensionRunner.cs`, `Rules/WriteInvocation.cs` | the driver port and its call shape |
| `Rules/NoWriteExtensions.cs` (internal) | the driver's default when no core runner is registered |
| `Rules/IRowRefusal.cs`, `Rules/IHookRejectionLog.cs`, `Rules/HookRejection.cs`, `Rules/HookFailedException.cs`, `Rules/HeldRows.cs` (internal) | batch refusal seam; rejection-log seam; the hook failure the driver also raises (Ruling 45); rows in a hook stage, held by the runner and by a batch (Ruling 43) |

Created — EF driver `src/MMLib.Alvo.Data.EntityFrameworkCore`: `Internal/EfAlvoData.Hooks.cs` (stages 0, 2, 4/5, 7/7b), `Internal/UnitRun.cs` (one run-and-commit mapping for `TransactAsync` and a write's own unit), `Internal/HookRejectionTable.cs`, `EfCoreHookRejectionLog.cs`.

Created — core `src/MMLib.Alvo/Hooks`:

| File | Responsibility |
|---|---|
| `IValidateWriteHook.cs`, `IValidateWriteHook{T}.cs`, `IBeforeWriteHook.cs`, `IBeforeWriteHook{T}.cs`, `IAfterWriteHook.cs`, `IAfterWriteHook{T}.cs`, `IBeforeDeleteHook.cs`, `IBeforeDeleteHook{T}.cs`, `IAfterDeleteHook.cs`, `IAfterDeleteHook{T}.cs` | the hook interfaces |
| `ValidateWrite{T}.cs`, `BeforeWrite{T}.cs`, `AfterWrite{T}.cs`, `BeforeDelete{T}.cs`, `AfterDelete{T}.cs`, `WriteCondition{T}.cs` | the contexts and the typed condition |
| `AlvoWriteRejectedException.cs`, `AlvoWriteRejectionKind.cs` | the rejection |
| `Internal/HookRun.cs`, `Internal/HookRejections.cs`, `Internal/HookImage.cs` | one invocation's state: recorded rejections, `Set`s, images |
| `Internal/WriteHookStage.cs`, `Internal/WriteHookRegistration.cs`, `Internal/HookInvoker.cs`, `Internal/HookContexts.cs`, `Internal/WriteConditionTranslator.cs` | registration |
| `Internal/WriteHookBinding.cs`, `Internal/BoundWriteHook.cs`, `Internal/HookDependencies.cs`, `Internal/WriteHookApplyRefusals.cs`, `Internal/HookDescription.cs` | boot, apply guard, catalog |
| `Internal/WriteExtensionRunner.cs`, `Internal/HookTransaction.cs`, `Internal/HookSets.cs`, `Internal/HookTelemetry.cs`, `Internal/HookLog.cs`, `Internal/Setup.cs` | running hooks |

Created — core `src/MMLib.Alvo/Extensions`: `AlvoHookOptions.cs`, `AlvoEntityBuilder.WriteHooks.cs`, `AlvoEntityBuilder{T}.WriteHooks.cs`. TestHost: `src/MMLib.Alvo.TestHost/Internal/TrackedData.cs`.

Created — tests: `test/MMLib.Alvo.Abstractions.Tests/Rules/WriteInvocationTests.cs`; `test/MMLib.Alvo.Tests/Hooks/{HookRejectionsTests.cs,HookSetsTests.cs,HookContextTests.cs,WriteHookRegistrationTests.cs,WriteConditionTranslatorTests.cs,WriteHookBindingTests.cs,WriteExtensionRunnerTests.cs,HookTransactionTests.cs,HookCatalog.cs,FakeHookTransaction.cs}`; `test/MMLib.Alvo.Data.EntityFrameworkCore.Tests/{HostHookStageTests.cs,RecordingWriteExtensions.cs}`; `test/_shared/testhost/{WriteHookFacts.cs,WriteHookRows.cs}`, `test/_shared/testhost/descriptors/hook-desk.alvo.json`; `test/MMLib.Alvo.TestHost.Tests/SqliteWriteHookTests.cs`; `test/MMLib.Alvo.Data.PostgreSql.Tests.Integration/PostgreSqlWriteHookTests.cs`; `test/_shared/api/DataApiEngineTests.WriteHooks.cs`, `test/MMLib.Alvo.Api.Tests/descriptors/hook-guards.alvo.json`; `test/MMLib.Alvo.Admin.Tests/LifecycleLaneWriteHookTests.cs`; `test/MMLib.Alvo.Admin.Tests.EndToEnd/{WriteHookWorld.cs,WriteHookScenarios.cs}`.

Created — docs: `website/src/content/docs/extending/write-hooks.mdx`; `website/src/snippets/extending/write-hooks/{opening-desk.alvo.json,OpeningDeskHooks.cs,OpeningDeskHooksTests.cs}`; `docs/architecture/write-hooks.md`.

Modified: Abstractions `Identity/IAlvoFlowUnit.cs`, `AlvoFrameworkTables.cs`, `Management/{AlvoExtensionKind.cs,AlvoExtensionStage.cs,ManagementExtension.cs}`, `Events/IDeliveryStore.cs`; Testing `DeliveryStoreContractTests.cs`; EF driver `AlvoEfCoreProvider.cs`, `Internal/{EfAlvoData.cs,EfAlvoData.Units.cs,EfAlvoTransaction.cs,EfAlvoTransactions.cs,WriteUnit.cs,IdempotencyTable.cs,SystemSchemaInitializer.cs,DeliveryOperationsTable.cs,EfCoreDeliveryStore.Operations.cs}`; core `Extensions/{AlvoEntityBuilder.cs,AlvoEntityBuilder{T}.cs,Setup.cs}` (`partial`), `Extensions/Internal/{ExtensionRegistry.cs,ExtensionBindings.cs,ExtensionBindingCompiler.cs,ExtensionApplyGuard.cs,ExtensionApplyRefusals.cs,ExtensionIdProblems.cs,ExtensionCatalog.cs,ExtensionStats.cs,HandlerInvoker.cs}`, `Events/Internal/AlvoHookTelemetry.cs`, `Api/AlvoProblemTypes.cs`, `Api/Internal/{ProblemResultFactory.cs,AlvoExceptionHandler.cs}`, `Management/Internal/AlvoOperationsService.Writes.cs`, `AlvoServiceCollectionExtensions.cs`; TestHost `AlvoTestHost.cs`; Admin `Components/Schema/{LifecycleLane.cs,LifecycleTab.razor,Entity.razor,Entity.razor.cs,HooksTab.razor,Preview.razor}`, `Components/Rules/{PolicyVerdictView.razor,RulesTab.razor}`; tests `test/MMLib.Alvo.Data.EntityFrameworkCore.Tests/{WritePathWorld.cs,EfAlvoTransactionsTests.cs,ChangeTrackerReachTests.cs}`, `test/MMLib.Alvo.Tests/Events/FakeDeliveryStore.cs`, `test/MMLib.Alvo.Tests/Management/{RacedReplayStore.cs,AlvoOperationsWritesTests.cs}`, `test/_shared/testhost/FailingFirstFanOut.cs`, `test/_shared/ef/DeliveryStoreWorld.cs`, `test/MMLib.Alvo.Data.PostgreSql.Tests.Integration/*.csproj`, the four `PublicApi.*.verified.txt` (Abstractions, core, Testing, Admin); `website/astro.config.mjs`, `website/src/content/docs/extending/{where-logic-goes.mdx,index.mdx,testing.mdx,transactions.mdx,extension-catalog.mdx}`; `docs/architecture/{events.md,data-path.md,extensibility.md}`, `CHANGELOG.md`, `docs/PLAN.md`, the spec; mutation configuration only (Task 14): `stryker-config.data-ef-core.json`, `stryker-config.data-ef-rest.json`.

---
## Task 1: Abstractions — the driver port, the verb, the budget refusal, the catalog vocabulary and the switch-off store members

**Files:**
- Create: `src/MMLib.Alvo.Abstractions/Data/AlvoWriteOperation.cs`, `src/MMLib.Alvo.Abstractions/Data/AlvoBudgetExceededException.cs`, `src/MMLib.Alvo.Abstractions/Rules/IWriteExtensionRunner.cs`, `src/MMLib.Alvo.Abstractions/Rules/WriteInvocation.cs`, `src/MMLib.Alvo.Abstractions/Rules/NoWriteExtensions.cs`, `src/MMLib.Alvo.Abstractions/Rules/IRowRefusal.cs`, `src/MMLib.Alvo.Abstractions/Rules/IHookRejectionLog.cs`, `src/MMLib.Alvo.Abstractions/Rules/HookRejection.cs`, `src/MMLib.Alvo.Abstractions/Rules/HeldRows.cs`, `src/MMLib.Alvo.Abstractions/Rules/HookFailedException.cs` (internal: the core's runner and the driver both fail a write with it — Ruling 45)
- Modify: `src/MMLib.Alvo.Abstractions/Identity/IAlvoFlowUnit.cs`, `src/MMLib.Alvo.Abstractions/AlvoFrameworkTables.cs`, `src/MMLib.Alvo.Abstractions/Management/AlvoExtensionKind.cs`, `src/MMLib.Alvo.Abstractions/Management/AlvoExtensionStage.cs`, `src/MMLib.Alvo.Abstractions/Management/ManagementExtension.cs`, `src/MMLib.Alvo.Abstractions/Events/IDeliveryStore.cs`, `src/MMLib.Alvo.Data.EntityFrameworkCore/Internal/WriteUnit.cs` (the `Blame` member only), every `IAlvoFlowUnit`/`IDeliveryStore` implementer the compiler names (`test/MMLib.Alvo.Tests/Data/FakeFlowUnit.cs`, `test/MMLib.Alvo.Tests/Events/FakeDeliveryStore.cs`, `test/MMLib.Alvo.Tests/Management/RacedReplayStore.cs`, `test/_shared/testhost/FailingFirstFanOut.cs`, `test/_shared/ef/DeliveryStoreWorld.cs` and the EF store — the EF store's real implementation is Task 2; here it throws `NotImplementedException` only until Task 2, which this task's commit must not ship: Task 1 and Task 2 commit together if ring0 needs it)
- Test: `test/MMLib.Alvo.Abstractions.Tests/Rules/WriteInvocationTests.cs`, `test/MMLib.Alvo.Abstractions.Tests/PublicApi.MMLib.Alvo.Abstractions.verified.txt`

**Interfaces:**
- Consumes: `AlvoContext`, `AlvoRecord`, `IAlvoTransaction`, `AlvoRowRefusal`, `DeliveryOperationEntry` (existing).
- Produces: `AlvoWriteOperation`; `IWriteExtensionRunner { bool Validates(string, AlvoWriteOperation); bool RunsInTransaction(string, AlvoWriteOperation); Task ValidateAsync(WriteInvocation, CancellationToken); Task<IReadOnlyDictionary<string, object?>> BeforeWriteAsync(WriteInvocation, CancellationToken); Task<string?> AfterWriteAsync(WriteInvocation, CancellationToken); }`; `WriteInvocation`; internal `NoWriteExtensions.Instance`; `AlvoBudgetExceededException`; internal `IRowRefusal { AlvoRowRefusal At(int index); }`; internal `HookRejection(DateTimeOffset At, string Extension, string Entity, string Operation, string Kind, string Code, string Actor)`; internal `IHookRejectionLog { Task RecordAsync(HookRejection, CancellationToken); }`; internal `HookFailedException(string extension, string reason)` with `Extension`, `Reason` and the reason constants `Exception`, `Timeout`, `Budget`, `InvalidSet`, `SelfWrite`, `Unbound`, `RowVanished`; internal `HeldRows.Hold(string, Guid?)`, `.HoldAll(string, IEnumerable<Guid>)`, `.Holds(string, Guid)`, `.RowOf(WriteInvocation)`; `IAlvoFlowUnit.Blame(Exception)`; `AlvoFrameworkTables.HookRejectionsSuffix`; the catalog members; `IDeliveryStore.SetDisabledAsync(string, bool, DeliveryOperationEntry, CancellationToken)` → `Task<bool>`, `IDeliveryStore.DisabledAsync(CancellationToken)` → `Task<IReadOnlyList<string>>`.

- [ ] **Step 1: Write the failing tests**

`test/MMLib.Alvo.Abstractions.Tests/Rules/WriteInvocationTests.cs`:

```csharp
using MMLib.Alvo.Data;
using MMLib.Alvo.Management;
using MMLib.Alvo.Rules;

namespace MMLib.Alvo.Abstractions.Tests.Rules;

/// <summary>The write-hook port's vocabulary (spec 2026-10-14 §5 row 09): stable numbers, and a default runner that runs nothing.</summary>
public sealed class WriteInvocationTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public void The_write_verbs_keep_their_numbers()
    {
        ((int)AlvoWriteOperation.Create, (int)AlvoWriteOperation.Update, (int)AlvoWriteOperation.Replace, (int)AlvoWriteOperation.Delete)
            .ShouldBe((0, 1, 2, 3));
    }

    [Fact]
    public void The_catalog_kinds_and_stages_are_appended_after_the_existing_ones()
    {
        ((int)AlvoExtensionKind.Validate, (int)AlvoExtensionKind.BeforeWrite, (int)AlvoExtensionKind.AfterWrite,
            (int)AlvoExtensionKind.BeforeDelete, (int)AlvoExtensionKind.AfterDelete).ShouldBe((2, 3, 4, 5, 6));
        ((int)AlvoExtensionStage.InTransaction).ShouldBe(3);
        System.Text.Json.JsonSerializer.Serialize(AlvoExtensionKind.BeforeDelete).ShouldBe("\"beforeDelete\"");
        System.Text.Json.JsonSerializer.Serialize(AlvoExtensionStage.InTransaction).ShouldBe("\"inTransaction\"");
    }

    [Fact]
    public void An_extension_is_enabled_and_not_top_level_only_unless_its_kind_says_so()
    {
        var extension = new ManagementExtension { Id = "x", Kind = AlvoExtensionKind.EventHandler, Stage = AlvoExtensionStage.PostCommit };

        (extension.Enabled, extension.TopLevelOnly).ShouldBe((true, false));
    }

    [Fact]
    public async Task The_default_runner_runs_nothing_and_patches_nothing()
    {
        var invocation = new WriteInvocation
        {
            Entity = "bookings",
            Operation = AlvoWriteOperation.Create,
            Caller = AlvoContext.Anonymous,
            Now = DateTimeOffset.UnixEpoch,
        };

        NoWriteExtensions.Instance.Validates("bookings", AlvoWriteOperation.Create).ShouldBeFalse();
        NoWriteExtensions.Instance.RunsInTransaction("bookings", AlvoWriteOperation.Delete).ShouldBeFalse();
        await NoWriteExtensions.Instance.ValidateAsync(invocation, Ct);
        (await NoWriteExtensions.Instance.BeforeWriteAsync(invocation, Ct)).ShouldBeEmpty();
        (await NoWriteExtensions.Instance.AfterWriteAsync(invocation, Ct)).ShouldBeNull();
    }

    [Fact]
    public void The_budget_refusal_is_an_invalid_operation_naming_the_fix()
    {
        var refusal = AlvoBudgetExceededException.For("audit-trail", 50);

        refusal.ShouldBeAssignableTo<InvalidOperationException>();
        refusal.Message.ShouldContain("'audit-trail'");
        refusal.Message.ShouldContain("50");
        refusal.Message.ShouldContain("MaxOperations");
    }

    [Fact]
    public void The_rejection_table_is_reserved()
    {
        AlvoFrameworkTables.NamesFor("alvo").ShouldContain("alvo_hook_rejections");
    }
}
```

(`MMLib.Alvo.Abstractions.Tests` holds `InternalsVisibleTo`; check `src/MMLib.Alvo.Abstractions/Properties/AssemblyInfo.cs` — if `MMLib.Alvo.Abstractions.Tests` is not granted, add `[assembly: InternalsVisibleTo("MMLib.Alvo.Abstractions.Tests")]` beside the existing test grants.)

- [ ] **Step 2: Run them to verify they fail**

Run: `dotnet test --project test/MMLib.Alvo.Abstractions.Tests --filter-class '*WriteInvocationTests'`
Expected: FAIL — `AlvoWriteOperation`, `WriteInvocation`, `NoWriteExtensions`, `AlvoBudgetExceededException`, the new enum members do not exist.

- [ ] **Step 3: Write the Abstractions types**

`src/MMLib.Alvo.Abstractions/Data/AlvoWriteOperation.cs`:

```csharp
namespace MMLib.Alvo.Data;

/// <summary>The write a host write hook runs on.</summary>
/// <remarks>
/// Not <see cref="Rules.DataOperation"/>, the policy's verb: the policy has no replace, and a hook tells a replace from an
/// update. Append only: a later version adds members after these and never renumbers them.
/// </remarks>
public enum AlvoWriteOperation
{
    /// <summary>A record is created.</summary>
    Create = 0,

    /// <summary>Some fields of an existing record are changed.</summary>
    Update = 1,

    /// <summary>A whole record is written at a known id: replacing it, or creating it when there was none (then there is no old record).</summary>
    Replace = 2,

    /// <summary>A record is deleted.</summary>
    Delete = 3,
}
```

`src/MMLib.Alvo.Abstractions/Data/AlvoBudgetExceededException.cs`:

```csharp
namespace MMLib.Alvo.Data;

/// <summary>
/// A write hook made more calls through its transaction in one run than its operation budget allows. The write it runs in
/// is failed: catching this changes nothing, the transaction rolls back.
/// </summary>
public sealed class AlvoBudgetExceededException : InvalidOperationException
{
    /// <summary>Initializes a new instance of the <see cref="AlvoBudgetExceededException"/> class.</summary>
    public AlvoBudgetExceededException()
        : base("A write hook made more calls through its transaction than its operation budget allows.")
    {
    }

    /// <summary>Initializes a new instance of the <see cref="AlvoBudgetExceededException"/> class.</summary>
    /// <param name="message">The message.</param>
    public AlvoBudgetExceededException(string message)
        : base(message)
    {
    }

    /// <summary>Initializes a new instance of the <see cref="AlvoBudgetExceededException"/> class.</summary>
    /// <param name="message">The message.</param>
    /// <param name="innerException">The cause.</param>
    public AlvoBudgetExceededException(string message, Exception innerException)
        : base(message, innerException)
    {
    }

    /// <summary>The refusal for <paramref name="extension"/>'s call past <paramref name="limit"/>.</summary>
    /// <param name="extension">The hook's id.</param>
    /// <param name="limit">Its budget.</param>
    internal static AlvoBudgetExceededException For(string extension, int limit) => new(
        $"The write hook '{extension}' made more than {limit} calls through its transaction in one run, so the write is failed "
        + "and will roll back. Fix: write many rows with one batch call, or raise AlvoHookOptions.MaxOperations.");
}
```

`src/MMLib.Alvo.Abstractions/Rules/WriteInvocation.cs`:

```csharp
using MMLib.Alvo.Data;

namespace MMLib.Alvo.Rules;

/// <summary>One stage of one write, as a driver hands it to <see cref="IWriteExtensionRunner"/>.</summary>
/// <remarks>Init-only rather than positional, so a later version adds members without breaking a driver.</remarks>
public sealed record WriteInvocation
{
    /// <summary>Gets the entity being written.</summary>
    public required string Entity { get; init; }

    /// <summary>Gets the write.</summary>
    public required AlvoWriteOperation Operation { get; init; }

    /// <summary>Gets the caller the write is performed as.</summary>
    public required AlvoContext Caller { get; init; }

    /// <summary>Gets the instant the write is stamped with.</summary>
    public required DateTimeOffset Now { get; init; }

    /// <summary>Gets the row before the write, unmasked; <see langword="null"/> on a create and on a replace that creates.</summary>
    public AlvoRecord? Old { get; init; }

    /// <summary>Gets the row the write would store, unmasked — validate and before-write stages; <see langword="null"/> on a delete.</summary>
    public AlvoRecord? New { get; init; }

    /// <summary>Gets the row as stored, with its id, defaults and rollups — the after-write stage only.</summary>
    public AlvoRecord? Saved { get; init; }

    /// <summary>
    /// Gets the write's transaction unit, acting as the caller one hop deeper in the causation chain — in-transaction stages
    /// only; <see langword="null"/> for validate, which runs with no transaction open.
    /// </summary>
    public IAlvoTransaction? Transaction { get; init; }

    /// <summary>
    /// Gets the DI scope hooks resolve from — the unit's, in-transaction; <see langword="null"/> for validate, whose hooks the
    /// runner resolves from a scope of its own.
    /// </summary>
    public IServiceProvider? Services { get; init; }
}
```

`src/MMLib.Alvo.Abstractions/Rules/IWriteExtensionRunner.cs`:

```csharp
using MMLib.Alvo.Data;

namespace MMLib.Alvo.Rules;

/// <summary>
/// Runs a host's write hooks for one stage of one write: validate before any transaction, before-write and after-write
/// inside it. Called by a storage driver at the stages it alone can place; implemented by Alvo's core.
/// </summary>
/// <remarks>
/// <para>
/// <b>A port, as <see cref="IBeforeHookRunner"/> is,</b> because every caller is a driver and the hooks live in the core: one
/// port makes every driver run the same pipeline. Unlike that runner it is asynchronous and takes a token — host code may
/// await calls through the unit's own transaction — and it bounds what it runs with a timeout and an operation budget
/// instead.
/// </para>
/// <para>
/// <b>A refusal is an exception.</b> A rejection rolls the write back with the hook's kind, code and pointer; a hook failure
/// (an exception, a timeout, a budget overrun, a value its field cannot hold, a write to its own row, a registration the
/// current schema does not bind) fails the write and — inside a transaction unit — poisons the unit, so a caller that catches
/// it still cannot commit.
/// </para>
/// </remarks>
public interface IWriteExtensionRunner
{
    /// <summary>Whether any validate hook is registered for <paramref name="operation"/> on <paramref name="entity"/>.</summary>
    /// <param name="entity">The entity.</param>
    /// <param name="operation">The write.</param>
    /// <returns><see langword="true"/> when a driver must run the validate stage, with its pre-reads.</returns>
    bool Validates(string entity, AlvoWriteOperation operation);

    /// <summary>Whether any before- or after-stage hook is registered for <paramref name="operation"/> on <paramref name="entity"/>.</summary>
    /// <param name="entity">The entity.</param>
    /// <param name="operation">The write.</param>
    /// <returns><see langword="true"/> when the write must run in a transaction unit its hooks can reach.</returns>
    bool RunsInTransaction(string entity, AlvoWriteOperation operation);

    /// <summary>Runs the validate hooks of the write, with no transaction open.</summary>
    /// <param name="invocation">The write; <see cref="WriteInvocation.Transaction"/> is <see langword="null"/>.</param>
    /// <param name="cancellationToken">The write's token.</param>
    /// <returns>A task that completes when every hook has passed the write.</returns>
    Task ValidateAsync(WriteInvocation invocation, CancellationToken cancellationToken);

    /// <summary>Runs the before-write (or before-delete) hooks of the write, inside its transaction.</summary>
    /// <param name="invocation">The write, with the candidate in <see cref="WriteInvocation.New"/> (a delete: its row in <see cref="WriteInvocation.Old"/>).</param>
    /// <param name="cancellationToken">The write's token.</param>
    /// <returns>
    /// The fields the hooks set, keyed by field name — every value already measured against its field's facets; empty when
    /// nothing was set, and always empty for a delete.
    /// </returns>
    Task<IReadOnlyDictionary<string, object?>> BeforeWriteAsync(WriteInvocation invocation, CancellationToken cancellationToken);

    /// <summary>Runs the after-write (or after-delete) hooks of the write, inside its transaction, after the row and its rollups.</summary>
    /// <param name="invocation">The write, with the stored row in <see cref="WriteInvocation.Saved"/> (a delete: <see cref="WriteInvocation.Old"/>).</param>
    /// <param name="cancellationToken">The write's token.</param>
    /// <returns>
    /// The id of the last hook that wrote through the transaction — the stored row must then be read again before it is
    /// emitted — or <see langword="null"/> when none wrote.
    /// </returns>
    Task<string?> AfterWriteAsync(WriteInvocation invocation, CancellationToken cancellationToken);
}
```

`src/MMLib.Alvo.Abstractions/Rules/NoWriteExtensions.cs`:

```csharp
using MMLib.Alvo.Data;

namespace MMLib.Alvo.Rules;

/// <summary>The runner a driver uses when no core runner is registered: no hook, no patch, no unit.</summary>
internal sealed class NoWriteExtensions : IWriteExtensionRunner
{
    private static readonly IReadOnlyDictionary<string, object?> _none = new Dictionary<string, object?>(StringComparer.Ordinal);

    private NoWriteExtensions()
    {
    }

    internal static NoWriteExtensions Instance { get; } = new();

    public bool Validates(string entity, AlvoWriteOperation operation) => false;

    public bool RunsInTransaction(string entity, AlvoWriteOperation operation) => false;

    public Task ValidateAsync(WriteInvocation invocation, CancellationToken cancellationToken) => Task.CompletedTask;

    public Task<IReadOnlyDictionary<string, object?>> BeforeWriteAsync(WriteInvocation invocation, CancellationToken cancellationToken) =>
        Task.FromResult(_none);

    public Task<string?> AfterWriteAsync(WriteInvocation invocation, CancellationToken cancellationToken) => Task.FromResult<string?>(null);
}
```

`src/MMLib.Alvo.Abstractions/Rules/IRowRefusal.cs`:

```csharp
using MMLib.Alvo.Data;

namespace MMLib.Alvo.Rules;

/// <summary>A refusal a batch reports as one row's, rather than failing the whole call (Ruling 4).</summary>
/// <remarks>Internal, so a driver can recognise the core's rejection without naming a core type.</remarks>
internal interface IRowRefusal
{
    /// <summary>This refusal as the row at <paramref name="index"/>'s.</summary>
    /// <param name="index">The row's place in the request.</param>
    AlvoRowRefusal At(int index);
}
```

`src/MMLib.Alvo.Abstractions/Rules/HookRejection.cs`:

```csharp
namespace MMLib.Alvo.Rules;

/// <summary>One recorded rejection by a host write hook: ids and the hook's code only — no payload, no message.</summary>
/// <param name="At">When the hook rejected.</param>
/// <param name="Extension">The hook's id.</param>
/// <param name="Entity">The entity written.</param>
/// <param name="Operation">The write, as <c>create</c>, <c>update</c>, <c>replace</c> or <c>delete</c>.</param>
/// <param name="Kind">The kind: <c>invalid</c>, <c>conflict</c> or <c>forbidden</c>.</param>
/// <param name="Code">The hook's own code.</param>
/// <param name="Actor">The caller's identity, as <c>AlvoIdempotency.IdentityOf</c> spells it.</param>
internal sealed record HookRejection(
    DateTimeOffset At, string Extension, string Entity, string Operation, string Kind, string Code, string Actor);
```

`src/MMLib.Alvo.Abstractions/Rules/HeldRows.cs`:

```csharp
using System.Collections.Immutable;

namespace MMLib.Alvo.Rules;

/// <summary>
/// The rows in a hook stage on this flow (Rulings 35, 43) — an <see cref="AsyncLocal{T}"/> set that flows into a hook's nested
/// calls: the row whose hooks are running (held by the core's runner) and every row of a batch while its hooks run (held by
/// the driver). A nested write to a held row is a self-write, however deep it is made.
/// </summary>
internal static class HeldRows
{
    private static readonly AsyncLocal<ImmutableHashSet<(string Entity, Guid Id)>?> _held = new();

    internal static bool Holds(string entity, Guid id) => _held.Value?.Contains((entity, id)) == true;

    internal static IDisposable Hold(string entity, Guid? id) => HoldAll(entity, id is { } row ? [row] : []);

    internal static IDisposable HoldAll(string entity, IEnumerable<Guid> ids)
    {
        var previous = _held.Value;
        var held = previous ?? [];
        foreach (var id in ids)
        {
            held = held.Add((entity, id));
        }

        _held.Value = held;
        return new Restore(previous);
    }

    internal static Guid? RowOf(WriteInvocation invocation) => (invocation.Saved ?? invocation.New ?? invocation.Old)?[AlvoManagedColumns.Id] as Guid?;

    private sealed class Restore(ImmutableHashSet<(string Entity, Guid Id)>? previous) : IDisposable
    {
        public void Dispose() => _held.Value = previous;
    }
}
```

`src/MMLib.Alvo.Abstractions/Rules/HookFailedException.cs` (every core file that names it adds `using MMLib.Alvo.Rules;`):

```csharp
namespace MMLib.Alvo.Rules;

/// <summary>
/// A host write hook failed, so the write failed — answered 500 <c>hook-failed</c> with the extension id and one fixed reason
/// (Ruling 3). An <see cref="InvalidOperationException"/>, the data port's family for an invariant the implementation relies on.
/// </summary>
/// <param name="extension">The hook's id.</param>
/// <param name="reason">One of the reason constants.</param>
internal sealed class HookFailedException(string extension, string reason)
    : InvalidOperationException($"The write hook '{extension}' failed ({reason}), so the write was rolled back.")
{
    internal const string Exception = "exception";
    internal const string Timeout = "timeout";
    internal const string Budget = "budget";
    internal const string InvalidSet = "invalid-set";
    internal const string SelfWrite = "self-write";
    internal const string Unbound = "unbound";
    internal const string RowVanished = "row-vanished";

    internal string Extension { get; } = extension;

    internal string Reason { get; } = reason;
}
```

`src/MMLib.Alvo.Abstractions/Rules/IHookRejectionLog.cs`:

```csharp
namespace MMLib.Alvo.Rules;

/// <summary>Where a host write hook's rejections are kept (Ruling 8), implemented by a relational driver.</summary>
internal interface IHookRejectionLog
{
    /// <summary>
    /// Records <paramref name="rejection"/>: at once with no transaction unit on the flow, or when the unit on the flow has
    /// ended — whatever it ended with.
    /// </summary>
    /// <param name="rejection">The rejection.</param>
    /// <param name="cancellationToken">A token to cancel an immediate insert.</param>
    /// <returns>A task that completes when the row is recorded or handed to the unit.</returns>
    Task RecordAsync(HookRejection rejection, CancellationToken cancellationToken);
}
```

`src/MMLib.Alvo.Abstractions/Identity/IAlvoFlowUnit.cs` — add after `Poison`:

```csharp
    /// <summary>
    /// Records <paramref name="cause"/> as the unit's failure, replacing a cancellation recorded before it and nothing else:
    /// a hook's deadline is why its own calls were cancelled (Ruling 11).
    /// </summary>
    /// <param name="cause">The failure the unit should end with.</param>
    void Blame(Exception cause);
```

`src/MMLib.Alvo.Data.EntityFrameworkCore/Internal/WriteUnit.cs` — add beside `Poison`:

```csharp
    /// <inheritdoc/>
    public void Blame(Exception cause)
    {
        ArgumentNullException.ThrowIfNull(cause);
        lock (_gate)
        {
            if (!_ended && _failure is null or OperationCanceledException)
            {
                _failure = cause;
            }
        }
    }
```

(A unit timeout is recorded as a `TimeoutException`, which is not an `OperationCanceledException`, so the unit's own deadline is never replaced.) `test/MMLib.Alvo.Tests/Data/FakeFlowUnit.cs` gains the same member over its own failure field (`public void Blame(Exception cause) => Failure = Failure is null or OperationCanceledException ? cause : Failure;` — adapt to its field names).

`src/MMLib.Alvo.Abstractions/AlvoFrameworkTables.cs` — add the suffix after `InboundWebhooksSuffix`, and the name to `NamesFor`'s list after the inbound webhooks entry:

```csharp
    internal const string HookRejectionsSuffix = "_hook_rejections";
```

```csharp
            schemaPrefix + HookRejectionsSuffix,
```

`src/MMLib.Alvo.Abstractions/Management/AlvoExtensionKind.cs` — append after `Command = 1`:

```csharp
    /// <summary>A validate hook: host C# run before a top-level write's transaction opens, network allowed; it can only refuse.</summary>
    [JsonStringEnumMemberName("validate")]
    Validate = 2,

    /// <summary>A before-write hook: host C# run inside a create, update or replace's transaction before the row is written.</summary>
    [JsonStringEnumMemberName("beforeWrite")]
    BeforeWrite = 3,

    /// <summary>An after-write hook: host C# run inside a create, update or replace's transaction after the row and its rollups.</summary>
    [JsonStringEnumMemberName("afterWrite")]
    AfterWrite = 4,

    /// <summary>A before-delete hook: host C# run inside a delete's transaction before the row is removed.</summary>
    [JsonStringEnumMemberName("beforeDelete")]
    BeforeDelete = 5,

    /// <summary>An after-delete hook: host C# run inside a delete's transaction after the row is removed.</summary>
    [JsonStringEnumMemberName("afterDelete")]
    AfterDelete = 6,
```

`src/MMLib.Alvo.Abstractions/Management/AlvoExtensionStage.cs` — append after `PreTransaction = 2`:

```csharp
    /// <summary>Inside the write's own transaction: it can refuse or undo the write, and may not leave the process.</summary>
    [JsonStringEnumMemberName("inTransaction")]
    InTransaction = 3,
```

`src/MMLib.Alvo.Abstractions/Management/ManagementExtension.cs` — append after `Paused`:

```csharp
    /// <summary>
    /// Gets whether it runs only for a write no transaction unit is open around — a validate hook, whose network call must
    /// never hold a transaction open; a write made inside a unit skips it.
    /// </summary>
    public bool TopLevelOnly { get; init; }

    /// <summary>Gets whether it runs: only a validate hook can be switched off, by an administrator.</summary>
    public bool Enabled { get; init; } = true;
```

`src/MMLib.Alvo.Abstractions/Events/IDeliveryStore.cs` — add after `ControlsAsync`:

```csharp
    /// <summary>Switches an extension off or on, recording <paramref name="entry"/> in the same transaction when the state changed.</summary>
    /// <remarks>
    /// Used for validate hooks: a switched-off hook is skipped by every write that reads <see cref="DisabledAsync"/> after the
    /// change commits, on every replica. Writes the control's <c>disabled_at</c> and <c>disabled_by</c>; a pause is untouched.
    /// </remarks>
    /// <param name="subscriber">The extension id.</param>
    /// <param name="disabled">Whether it should be off.</param>
    /// <param name="entry">The log row; switching off also stores its instant and actor on the control.</param>
    /// <param name="cancellationToken">A token to cancel the operation.</param>
    /// <returns>Whether the state changed; switching off an extension that is off, or on one that is on, records nothing.</returns>
    /// <exception cref="InvalidOperationException">The log refused <paramref name="entry"/>; the change is rolled back with it.</exception>
    Task<bool> SetDisabledAsync(string subscriber, bool disabled, DeliveryOperationEntry entry, CancellationToken cancellationToken = default);

    /// <summary>The ids of the extensions switched off, in ordinal order.</summary>
    /// <param name="cancellationToken">A token to cancel the operation.</param>
    /// <returns>The ids.</returns>
    Task<IReadOnlyList<string>> DisabledAsync(CancellationToken cancellationToken = default);
```

Every `IDeliveryStore` test double gains the two members over an in-memory set (`private readonly HashSet<string> _disabled = new(StringComparer.Ordinal);` — `SetDisabledAsync` answers `disabled ? _disabled.Add(id) : _disabled.Remove(id)`, `DisabledAsync` answers `[.. _disabled.Order(StringComparer.Ordinal)]`); decorators (`FailingFirstFanOut`, `RacedReplayStore`) forward to their inner store.

- [ ] **Step 4: Run the tests and accept the Abstractions baseline**

Run: `dotnet test --project test/MMLib.Alvo.Abstractions.Tests --filter-class '*WriteInvocationTests'` → PASS. Then `dotnet test --project test/MMLib.Alvo.Abstractions.Tests --filter-class '*PublicApiApprovalTests'`: the received diff adds exactly `AlvoWriteOperation`, `AlvoBudgetExceededException` (three constructors), `IWriteExtensionRunner` (five members), `WriteInvocation` (nine properties and the record members), `AlvoExtensionKind.Validate/BeforeWrite/AfterWrite/BeforeDelete/AfterDelete`, `AlvoExtensionStage.InTransaction`, `ManagementExtension.TopLevelOnly/Enabled`, `IDeliveryStore.SetDisabledAsync/DisabledAsync`. Move it over the verified file; dispatch `alvo-snapshot-judge` when the gate asks.

- [ ] **Step 5: Commit** (with Task 2 if the EF store's members are still stubs — never ship a `NotImplementedException`)

```bash
git branch --show-current   # hx/09-write-hooks
dotnet format whitespace MMLib.Alvo.slnx --include src/MMLib.Alvo.Abstractions/Data/AlvoWriteOperation.cs src/MMLib.Alvo.Abstractions/Data/AlvoBudgetExceededException.cs src/MMLib.Alvo.Abstractions/Rules/IWriteExtensionRunner.cs src/MMLib.Alvo.Abstractions/Rules/WriteInvocation.cs src/MMLib.Alvo.Abstractions/Rules/NoWriteExtensions.cs src/MMLib.Alvo.Abstractions/Rules/IRowRefusal.cs src/MMLib.Alvo.Abstractions/Rules/IHookRejectionLog.cs src/MMLib.Alvo.Abstractions/Rules/HookRejection.cs test/MMLib.Alvo.Abstractions.Tests/Rules/WriteInvocationTests.cs
scripts/test-ring0
git add src/MMLib.Alvo.Abstractions test/MMLib.Alvo.Abstractions.Tests src/MMLib.Alvo.Data.EntityFrameworkCore/Internal/WriteUnit.cs test/MMLib.Alvo.Tests/Data/FakeFlowUnit.cs test/MMLib.Alvo.Tests/Events/FakeDeliveryStore.cs test/MMLib.Alvo.Tests/Management/RacedReplayStore.cs test/_shared/testhost/FailingFirstFanOut.cs test/_shared/ef/DeliveryStoreWorld.cs
git commit -m "feat(abstractions): the write-hook port, its verb and catalog vocabulary

Claude-Session: https://claude.ai/code/session_01NJAtafM29iddLwfhb5gnmB"
```

---

## Task 2: EF driver storage — the rejection log, the switch-off control and their contract rows, on both engines

**Files:**
- Create: `src/MMLib.Alvo.Data.EntityFrameworkCore/Internal/HookRejectionTable.cs`, `src/MMLib.Alvo.Data.EntityFrameworkCore/EfCoreHookRejectionLog.cs`
- Modify: `src/MMLib.Alvo.Data.EntityFrameworkCore/Internal/SystemSchemaInitializer.cs`, `src/MMLib.Alvo.Data.EntityFrameworkCore/Internal/DeliveryOperationsTable.cs`, `src/MMLib.Alvo.Data.EntityFrameworkCore/Internal/EfCoreDeliveryStore.Operations.cs`, `src/MMLib.Alvo.Data.EntityFrameworkCore/Internal/WriteUnit.cs`, `src/MMLib.Alvo.Data.EntityFrameworkCore/AlvoEfCoreProvider.cs`, `src/MMLib.Alvo.Testing/DeliveryStoreContractTests.cs` (path: `find src/MMLib.Alvo.Testing -name DeliveryStoreContractTests.cs`), `test/MMLib.Alvo.Data.EntityFrameworkCore.Tests/ChangeTrackerReachTests.cs`
- Test: `test/MMLib.Alvo.Data.EntityFrameworkCore.Tests/HookRejectionLogTests.cs`; the contract rows run in every existing `DeliveryStoreContractTests` subclass (SQLite in `MMLib.Alvo.Data.Sqlite.Tests`, PostgreSQL in `MMLib.Alvo.Data.PostgreSql.Tests.Integration`, the core's `FakeDeliveryStoreTests`)

**Interfaces:**
- Consumes: Task 1's `IHookRejectionLog`, `HookRejection`, `IDeliveryStore` members, `AlvoFrameworkTables.HookRejectionsSuffix`.
- Produces: `HookRejectionTable.NameFor(prefix)`, `HookRejectionTable.Ddl(table)`, `HookRejectionTable.IndexDdl(table)`, `HookRejectionTable.InsertAsync(DbConnection, string table, HookRejection, CancellationToken)`; `EfCoreHookRejectionLog` (`IHookRejectionLog`; `internal Task FlushAsync(IReadOnlyList<HookRejection>)`); `WriteUnit.Remember(HookRejection)`, `WriteUnit.Rejections`; `DeliveryOperationsTable.DisableSql/EnableSql/DisabledSql`, `DeliveryOperationsTable.BindLogEntry(DbCommand, DeliveryOperationEntry)` (moved from the store, internal static).

- [ ] **Step 1: Write the failing contract rows and the log test**

In `DeliveryStoreContractTests` (Testing), beside the pause rows:

```csharp
    [Fact]
    public async Task Switching_an_extension_off_lists_it_and_logs_the_change_once()
    {
        await using var world = await CreateWorldAsync();
        var store = world.Store;

        (await store.SetDisabledAsync("vat-in-vies", true, Entry("disable-extension", "vat-in-vies"), Ct)).ShouldBeTrue();
        (await store.SetDisabledAsync("vat-in-vies", true, Entry("disable-extension", "vat-in-vies"), Ct)).ShouldBeFalse();

        (await store.DisabledAsync(Ct)).ShouldBe(["vat-in-vies"]);
        (await world.OperationsAsync(Ct)).Count(operation => operation == "disable-extension").ShouldBe(1);
    }

    [Fact]
    public async Task Switching_an_extension_on_again_removes_it_and_leaves_its_pause_alone()
    {
        await using var world = await CreateWorldAsync();
        var store = world.Store;
        await store.SetPausedAsync("vat-in-vies", true, Entry("pause", "vat-in-vies"), Ct);
        await store.SetDisabledAsync("vat-in-vies", true, Entry("disable-extension", "vat-in-vies"), Ct);

        (await store.SetDisabledAsync("vat-in-vies", false, Entry("enable-extension", "vat-in-vies"), Ct)).ShouldBeTrue();

        (await store.DisabledAsync(Ct)).ShouldBeEmpty();
        (await store.ControlsAsync(Ct)).Select(control => control.Subscriber).ShouldBe(["vat-in-vies"]);
    }

    [Fact]
    public async Task A_paused_extension_is_not_listed_as_switched_off()
    {
        await using var world = await CreateWorldAsync();
        await world.Store.SetPausedAsync("send-confirmation", true, Entry("pause", "send-confirmation"), Ct);

        (await world.Store.DisabledAsync(Ct)).ShouldBeEmpty();
    }
```

(`Entry(operation, target)` and `world.OperationsAsync` are the suite's existing helpers for the pause rows — `IDeliveryStoreWorld.OperationsAsync` exists since HX-06 Ruling 12; if the helper that builds an entry carries another name, use it.)

`test/MMLib.Alvo.Data.EntityFrameworkCore.Tests/HookRejectionLogTests.cs`:

```csharp
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using MMLib.Alvo.Data.EntityFrameworkCore.Internal;
using MMLib.Alvo.Rules;

namespace MMLib.Alvo.Data.EntityFrameworkCore.Tests;

/// <summary>The rejection log's table (Ruling 8): ids and codes only, written autocommit, and a failed insert never thrown.</summary>
public sealed class HookRejectionLogTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static readonly HookRejection _rejection =
        new(DateTimeOffset.UnixEpoch, "within-opening-hours", "bookings", "create", "conflict", "outside-hours", "tenant/user");

    [Fact]
    public async Task A_rejection_outside_a_unit_is_inserted_at_once()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync(Ct);
        var log = EfCoreHookRejectionLog.ForTests(() => connection, "alvo", NullLogger<EfCoreHookRejectionLog>.Instance);

        await log.RecordAsync(_rejection, Ct);

        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT extension, entity, operation, kind, code, actor FROM alvo_hook_rejections";
        await using var reader = await command.ExecuteReaderAsync(Ct);
        (await reader.ReadAsync(Ct)).ShouldBeTrue();
        Enumerable.Range(0, 6).Select(reader.GetString).ShouldBe(["within-opening-hours", "bookings", "create", "conflict", "outside-hours", "tenant/user"]);
    }

    [Fact]
    public async Task A_failed_insert_is_logged_not_thrown()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync(Ct);
        var logger = new RecordingLogger<EfCoreHookRejectionLog>();
        var log = EfCoreHookRejectionLog.ForTests(() => connection, "alvo", logger);
        await connection.CloseAsync();
        await using var broken = new SqliteConnection("Data Source=/nonexistent/dir/alvo.db;Mode=ReadOnly");

        await EfCoreHookRejectionLog.ForTests(() => broken, "alvo", logger).RecordAsync(_rejection, Ct);

        logger.EventNames.ShouldContain("HookRejectionNotRecorded");
    }
}
```

(`RecordingLogger<T>` — use the EF test project's existing recording logger if there is one (`grep -rn 'class Recording.*Logger' test/MMLib.Alvo.Data.EntityFrameworkCore.Tests`); otherwise add a 20-line `ILogger<T>` that queues `eventId.Name` into `EventNames`.)

- [ ] **Step 2: Run them to verify they fail**

Run: `dotnet test --project test/MMLib.Alvo.Data.Sqlite.Tests --filter-class '*DeliveryStore*'` and `dotnet test --project test/MMLib.Alvo.Data.EntityFrameworkCore.Tests --filter-class '*HookRejectionLogTests'`
Expected: FAIL — the members throw / do not exist; no table.

- [ ] **Step 3: Write the table, the log and the store members**

`src/MMLib.Alvo.Data.EntityFrameworkCore/Internal/HookRejectionTable.cs`:

```csharp
using System.Data.Common;
using MMLib.Alvo.Rules;

namespace MMLib.Alvo.Data.EntityFrameworkCore.Internal;

/// <summary>
/// <c>&lt;prefix&gt;_hook_rejections</c> (Ruling 8): one row per rejection by a host write hook — ids and the hook's code, never a
/// payload or a message. Created by <see cref="SystemSchemaInitializer"/> and ensured once per process before an insert.
/// </summary>
internal static class HookRejectionTable
{
    internal static string NameFor(string schemaPrefix) => schemaPrefix + AlvoFrameworkTables.HookRejectionsSuffix;

    internal static string Ddl(string table) =>
        $"""
        CREATE TABLE IF NOT EXISTS {table} (
            id TEXT NOT NULL PRIMARY KEY,
            at TEXT NOT NULL,
            extension TEXT NOT NULL,
            entity TEXT NOT NULL,
            operation TEXT NOT NULL,
            kind TEXT NOT NULL,
            code TEXT NOT NULL,
            actor TEXT NOT NULL
        )
        """;

    internal static string IndexDdl(string table) =>
        $"CREATE INDEX IF NOT EXISTS ix_{table}_extension_at ON {table} (extension, at)";

    internal static IReadOnlyList<string> Statements(string table) => [Ddl(table), IndexDdl(table)];

    private static string InsertSql(string table) =>
        $"INSERT INTO {table} (id, at, extension, entity, operation, kind, code, actor) VALUES (@id, @at, @extension, @entity, @operation, @kind, @code, @actor)";

    /// <summary>Inserts <paramref name="rejection"/> on <paramref name="connection"/>, autocommit.</summary>
    internal static async Task InsertAsync(DbConnection connection, string table, HookRejection rejection, CancellationToken ct)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = InsertSql(table);
        RelationalSqlBatch.AddParameter(command, "@id", Guid.CreateVersion7(rejection.At).ToString());
        RelationalSqlBatch.AddParameter(command, "@at", StoredInstant.Text(rejection.At));
        RelationalSqlBatch.AddParameter(command, "@extension", rejection.Extension);
        RelationalSqlBatch.AddParameter(command, "@entity", rejection.Entity);
        RelationalSqlBatch.AddParameter(command, "@operation", rejection.Operation);
        RelationalSqlBatch.AddParameter(command, "@kind", rejection.Kind);
        RelationalSqlBatch.AddParameter(command, "@code", rejection.Code);
        RelationalSqlBatch.AddParameter(command, "@actor", rejection.Actor);
        await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }
}
```

(`RelationalSqlBatch.AddParameter` and `StoredInstant.Text` are the helpers `DeliveryOperationsTable` binds with; check `RelationalSqlBatch.OpenAsync(connection, ct)` is how a closed connection is opened, as `EnsureOutboxTableAsync` does.)

`src/MMLib.Alvo.Data.EntityFrameworkCore/EfCoreHookRejectionLog.cs`:

```csharp
using Microsoft.Extensions.Logging;
using MMLib.Alvo.Data.EntityFrameworkCore.Internal;
using MMLib.Alvo.Rules;
using System.Data.Common;
using System.Diagnostics.CodeAnalysis;

namespace MMLib.Alvo.Data.EntityFrameworkCore;

/// <summary>
/// The relational <see cref="IHookRejectionLog"/> (Ruling 8): outside a unit a rejection is inserted at once; inside one it is
/// handed to the unit, and the driver flushes it after the unit has released its write turn and left the flow.
/// </summary>
/// <remarks>
/// A failed insert is logged and swallowed: the log is a record of answers already given, and a storage hiccup must not turn
/// a 422 into a 500. A crash between a unit's rollback and its flush loses the row.
/// </remarks>
internal sealed partial class EfCoreHookRejectionLog : IHookRejectionLog
{
    private readonly Func<DbConnection> _connect;
    private readonly string _table;
    private readonly ILogger<EfCoreHookRejectionLog> _logger;
    private readonly OnceDdl _ddl;

    internal EfCoreHookRejectionLog(Func<DbConnection> connect, string schemaPrefix, ILogger<EfCoreHookRejectionLog> logger)
    {
        _connect = connect;
        _table = HookRejectionTable.NameFor(schemaPrefix);
        _logger = logger;
        _ddl = new OnceDdl();
    }

    internal static EfCoreHookRejectionLog ForTests(Func<DbConnection> connect, string schemaPrefix, ILogger<EfCoreHookRejectionLog> logger) =>
        new(connect, schemaPrefix, logger);

    /// <inheritdoc/>
    public Task RecordAsync(HookRejection rejection, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(rejection);
        if (AlvoFlow.Unit is WriteUnit unit)
        {
            unit.Remember(rejection);
            return Task.CompletedTask;
        }

        return InsertAsync(rejection, cancellationToken);
    }

    /// <summary>Inserts every rejection a unit remembered, once the unit has ended and left the flow.</summary>
    internal async Task FlushAsync(IReadOnlyList<HookRejection> rejections)
    {
        foreach (var rejection in rejections)
        {
            await InsertAsync(rejection, CancellationToken.None).ConfigureAwait(false);
        }
    }

    [SuppressMessage(
        "Design",
        "CA1031:Do not catch general exception types",
        Justification = "The log records answers already given; no storage failure may change the answer (Ruling 8).")]
    private async Task InsertAsync(HookRejection rejection, CancellationToken cancellationToken)
    {
        try
        {
            await using var connection = _connect();
            await RelationalSqlBatch.OpenAsync(connection, cancellationToken).ConfigureAwait(false);
            await _ddl.EnsureAsync(connection, _table, HookRejectionTable.Statements(_table), cancellationToken).ConfigureAwait(false);
            await HookRejectionTable.InsertAsync(connection, _table, rejection, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception failure) when (failure is not OperationCanceledException)
        {
            HookRejectionNotRecorded(_logger, rejection.Extension, failure.GetType().FullName ?? failure.GetType().Name);
        }
    }

    [LoggerMessage(
        Level = LogLevel.Warning,
        Message = "A rejection by write hook {Extension} could not be recorded ({ExceptionType}); the caller's answer is unchanged.")]
    private static partial void HookRejectionNotRecorded(ILogger logger, string extension, string exceptionType);
}
```

(`OnceDdl` is HX-08's once-per-process DDL helper (Ruling 47); read its signature — `EnsureAsync(connection, key, statements, ct)` or similar — and call it in that shape. If its constructor or members differ, follow them; the behaviour required is "the table's DDL runs at most once per process, retried after a failure".)

`WriteUnit.cs` — add a list and two members:

```csharp
    private readonly List<HookRejection> _rejections = [];

    /// <summary>Keeps a host hook's rejection until the unit has ended (Ruling 8).</summary>
    internal void Remember(HookRejection rejection)
    {
        lock (_gate)
        {
            _rejections.Add(rejection);
        }
    }

    /// <summary>Gets the rejections remembered while the unit ran.</summary>
    internal IReadOnlyList<HookRejection> Rejections
    {
        get
        {
            lock (_gate)
            {
                return [.. _rejections];
            }
        }
    }
```

`SystemSchemaInitializer.cs` — add `HookRejectionTable.Statements(HookRejectionTable.NameFor(prefix))` to the statements it runs at boot, beside the command-ledger and inbound-webhook tables (HX-08 Task 5's shape).

`DeliveryOperationsTable.cs` — add:

```csharp
    /// <summary>Switches an extension off — from on only, so a second switch-off answers no row and logs nothing.</summary>
    internal static string DisableSql(string controls) =>
        $"""
        INSERT INTO {controls} (subscriber, disabled_at, disabled_by) VALUES (@subscriber, @at, @actor)
        ON CONFLICT (subscriber) DO UPDATE SET disabled_at = excluded.disabled_at, disabled_by = excluded.disabled_by
        WHERE {controls}.disabled_at IS NULL
        """;

    /// <summary>Switches an extension on again; one that is on answers no row.</summary>
    internal static string EnableSql(string controls) =>
        $"UPDATE {controls} SET disabled_at = NULL, disabled_by = NULL WHERE subscriber = @subscriber AND disabled_at IS NOT NULL";

    internal static string DisabledSql(string controls) =>
        $"SELECT subscriber FROM {controls} WHERE disabled_at IS NOT NULL ORDER BY subscriber";
```

and move `BindEntry`, `BindOutcome` and `Nullable` from `EfCoreDeliveryStore.Operations.cs` into it as `internal static void BindLogEntry(DbCommand command, DeliveryOperationEntry entry)` (same body; `BindOutcome`/`Nullable` private beside it), so the unit's suppression audit (Task 3) binds a log row the same way. The store's `LogAsync` calls `DeliveryOperationsTable.BindLogEntry`.

`EfCoreDeliveryStore.Operations.cs` — add:

```csharp
    /// <summary>Writes the switch and logs the change in one transaction when there was one.</summary>
    public Task<bool> SetDisabledAsync(string subscriber, bool disabled, DeliveryOperationEntry entry, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(subscriber);
        EnsureEntry(entry);

        return InTransactionAsync(async (connection, transaction) =>
        {
            var sql = disabled ? DeliveryOperationsTable.DisableSql(_controls) : DeliveryOperationsTable.EnableSql(_controls);
            var changed = await ExecuteInAsync(connection, transaction, sql, command => BindSwitch(command, subscriber, disabled, entry), cancellationToken)
                .ConfigureAwait(false) == 1;
            if (changed)
            {
                await LogChangeAsync(connection, transaction, entry with { Affected = 1 }, cancellationToken).ConfigureAwait(false);
            }

            return changed;
        }, cancellationToken);
    }

    public async Task<IReadOnlyList<string>> DisabledAsync(CancellationToken cancellationToken = default) =>
        await ReadAsync(DeliveryOperationsTable.DisabledSql(_controls), _ => { }, reader => reader.GetString(0), cancellationToken).ConfigureAwait(false);

    private static void BindSwitch(DbCommand command, string subscriber, bool disabled, DeliveryOperationEntry entry)
    {
        RelationalSqlBatch.AddParameter(command, "@subscriber", subscriber);
        if (disabled)
        {
            RelationalSqlBatch.AddParameter(command, "@at", StoredInstant.Text(entry.At));
            RelationalSqlBatch.AddParameter(command, "@actor", entry.Actor);
        }
    }
```

The pause's `PauseSql` inserts a control row with `disabled_*` null and `DisableSql` one with `paused_*` null, so the two never overwrite each other; `ControlsSql` already filters `paused_at IS NOT NULL`, so a switched-off, unpaused extension is not a paused one.

`AlvoEfCoreProvider.cs` — register the log beside the delivery store:

```csharp
        builder.Services.TryAddSingleton(CreateHookRejectionLog);
        builder.Services.TryAddSingleton<IHookRejectionLog>(services => services.GetRequiredService<EfCoreHookRejectionLog>());
```

```csharp
    private static EfCoreHookRejectionLog CreateHookRejectionLog(IServiceProvider services) => new(
        () => services.GetRequiredService<RelationalConnectionFactory>().Create(),
        services.GetRequiredService<IOptions<AlvoOptions>>().Value.SchemaPrefix,
        services.GetService<ILogger<EfCoreHookRejectionLog>>() ?? NullLogger<EfCoreHookRejectionLog>.Instance);
```

(`RelationalConnectionFactory` is what `RelationalReachability` opens connections with; use its real member name.)

`ChangeTrackerReachTests._sqlComposingFiles` gains `"HookRejectionTable.cs"` (alphabetical place). `EfCoreHookRejectionLog.cs` composes nothing.

- [ ] **Step 4: Run the tests to verify they pass, on both engines**

Run: `dotnet test --project test/MMLib.Alvo.Data.EntityFrameworkCore.Tests --filter-class '*HookRejectionLogTests'`, `dotnet test --project test/MMLib.Alvo.Data.Sqlite.Tests --filter-class '*DeliveryStore*'`, `dotnet test --project test/MMLib.Alvo.Tests --filter-class '*FakeDeliveryStoreTests'`, then `dotnet test --project test/MMLib.Alvo.Data.PostgreSql.Tests.Integration --filter-class '*DeliveryStore*'` (Docker).
Expected: PASS everywhere. Accept the Testing public-API baseline (three contract rows).

- [ ] **Step 5: Commit**

```bash
dotnet format whitespace MMLib.Alvo.slnx --include src/MMLib.Alvo.Data.EntityFrameworkCore/Internal/HookRejectionTable.cs src/MMLib.Alvo.Data.EntityFrameworkCore/EfCoreHookRejectionLog.cs src/MMLib.Alvo.Data.EntityFrameworkCore/Internal/DeliveryOperationsTable.cs src/MMLib.Alvo.Data.EntityFrameworkCore/Internal/EfCoreDeliveryStore.Operations.cs src/MMLib.Alvo.Data.EntityFrameworkCore/Internal/WriteUnit.cs src/MMLib.Alvo.Data.EntityFrameworkCore/Internal/SystemSchemaInitializer.cs src/MMLib.Alvo.Data.EntityFrameworkCore/AlvoEfCoreProvider.cs test/MMLib.Alvo.Data.EntityFrameworkCore.Tests/HookRejectionLogTests.cs
scripts/test-ring0
git add src/MMLib.Alvo.Data.EntityFrameworkCore src/MMLib.Alvo.Testing test/MMLib.Alvo.Data.EntityFrameworkCore.Tests
git commit -m "feat(data-ef): the hook rejection log and the extension switch-off control

Claude-Session: https://claude.ai/code/session_01NJAtafM29iddLwfhb5gnmB"
```

---

## Task 3: The driver's in-transaction stages — a unit of the write's own, stages 4/5 and 7/7b on every write path, `SuppressHostHooks`, the lazy hop **[slice end]**

**Files:**
- Create: `src/MMLib.Alvo.Data.EntityFrameworkCore/Internal/EfAlvoData.Hooks.cs`, `src/MMLib.Alvo.Data.EntityFrameworkCore/Internal/UnitRun.cs`, `test/MMLib.Alvo.Data.EntityFrameworkCore.Tests/RecordingWriteExtensions.cs`, `test/MMLib.Alvo.Data.EntityFrameworkCore.Tests/HostHookStageTests.cs`
- Modify: `src/MMLib.Alvo.Data.EntityFrameworkCore/Internal/EfAlvoData.cs`, `src/MMLib.Alvo.Data.EntityFrameworkCore/Internal/EfAlvoData.Units.cs`, `src/MMLib.Alvo.Data.EntityFrameworkCore/Internal/EfAlvoTransaction.cs`, `src/MMLib.Alvo.Data.EntityFrameworkCore/Internal/EfAlvoTransactions.cs`, `src/MMLib.Alvo.Data.EntityFrameworkCore/Internal/WriteUnit.cs`, `src/MMLib.Alvo.Data.EntityFrameworkCore/Internal/WritePayloadGuard.cs` (`ReadOnlyRefusal` internal), `src/MMLib.Alvo.Data.EntityFrameworkCore/AlvoEfCoreProvider.cs`, `test/MMLib.Alvo.Data.EntityFrameworkCore.Tests/WritePathWorld.cs`, `test/MMLib.Alvo.Data.EntityFrameworkCore.Tests/EfAlvoTransactionsTests.cs`, `test/MMLib.Alvo.Data.EntityFrameworkCore.Tests/ChangeTrackerReachTests.cs`

**Interfaces:**
- Consumes: Task 1's `IWriteExtensionRunner`, `WriteInvocation`, `NoWriteExtensions`, `IRowRefusal`; Task 2's `EfCoreHookRejectionLog.FlushAsync`, `WriteUnit.Rejections`, `DeliveryOperationsTable.BindLogEntry`.
- Produces: `EfAlvoData` constructor gains `IWriteExtensionRunner? extensions = null, IServiceScopeFactory? scopes = null, EfCoreHookRejectionLog? rejections = null` (after `writeGate`); `EfAlvoData.HookTransaction(WriteUnit, AlvoContext)`, `EfAlvoData.OneHopDeeper(AlvoContext)` (static), `EfAlvoData.AuditSuppressionAsync(WriteUnit, string, CancellationToken)` → `Task<DeliveryOperationEntry>`, `EfAlvoData.FlushRejectionsAsync(WriteUnit)`; `WriteUnit.Owned`, `WriteUnit.SuppressesHostHooks`; `UnitRun.CommittedAsync<T>(WriteUnit, Func<Task<T>>)`; `EfAlvoTransactions(EfAlvoData, IServiceScopeFactory, ILogger<EfAlvoTransactions>? = null)`.

- [ ] **Step 1: Write the failing driver facts**

`test/MMLib.Alvo.Data.EntityFrameworkCore.Tests/RecordingWriteExtensions.cs`:

```csharp
using MMLib.Alvo.Rules;
using System.Collections.Concurrent;

namespace MMLib.Alvo.Data.EntityFrameworkCore.Tests;

/// <summary>A write-hook runner that records what the driver hands it, and answers what a fact sets.</summary>
internal sealed class RecordingWriteExtensions : IWriteExtensionRunner
{
    internal HashSet<(string Entity, AlvoWriteOperation Operation)> InTransaction { get; } = [];

    internal HashSet<(string Entity, AlvoWriteOperation Operation)> Validating { get; } = [];

    internal ConcurrentQueue<(string Stage, WriteInvocation Invocation, bool UnitOnFlow, bool? Autocommit)> Calls { get; } = new();

    /// <summary>Whether the world's connection holds no open transaction at the moment of a call (Ruling 44); set by the fact.</summary>
    internal Func<bool>? Autocommit { get; set; }

    internal Dictionary<string, object?> Patch { get; } = new(StringComparer.Ordinal);

    internal Func<WriteInvocation, CancellationToken, Task<string?>>? After { get; set; }

    internal Exception? RejectBefore { get; set; }

    public bool Validates(string entity, AlvoWriteOperation operation) => Validating.Contains((entity, operation));

    public bool RunsInTransaction(string entity, AlvoWriteOperation operation) => InTransaction.Contains((entity, operation));

    public Task ValidateAsync(WriteInvocation invocation, CancellationToken cancellationToken)
    {
        Calls.Enqueue(("validate", invocation, AlvoFlow.Unit is not null, Autocommit?.Invoke()));
        return Task.CompletedTask;
    }

    public Task<IReadOnlyDictionary<string, object?>> BeforeWriteAsync(WriteInvocation invocation, CancellationToken cancellationToken)
    {
        Calls.Enqueue(("before", invocation, AlvoFlow.Unit is not null, Autocommit?.Invoke()));
        if (RejectBefore is { } rejection)
        {
            throw rejection;
        }

        return Task.FromResult<IReadOnlyDictionary<string, object?>>(invocation.Operation == AlvoWriteOperation.Delete ? new Dictionary<string, object?>() : Patch);
    }

    public Task<string?> AfterWriteAsync(WriteInvocation invocation, CancellationToken cancellationToken)
    {
        Calls.Enqueue(("after", invocation, AlvoFlow.Unit is not null, Autocommit?.Invoke()));
        return After?.Invoke(invocation, cancellationToken) ?? Task.FromResult<string?>(null);
    }

    internal IReadOnlyList<string> Stages => [.. Calls.Select(call => call.Stage)];
}

/// <summary>A row-scoped refusal, as the core's rejection is one, without naming the core.</summary>
internal sealed class RowScopedRefusal() : Exception("refused by a hook"), IRowRefusal
{
    public AlvoRowRefusal At(int index) => new(index, "refused-by-hook", Message, null);
}
```

`WritePathWorld.StartAsync` gains `IWriteExtensionRunner? extensions = null` (last parameter); `BuildAsync` and `Port` pass it on, and `Port` builds the port with `writeGate: null, extensions: extensions, scopes: world.Scopes`. The world also gains the discriminating probe of Ruling 44 — SQLite's own answer to "is a transaction open on this connection", which every write of this world shares:

```csharp
    /// <summary>Whether the world's one connection is in autocommit mode — no transaction open on it (sqlite3_get_autocommit).</summary>
    internal bool InAutocommit => SQLitePCL.raw.sqlite3_get_autocommit(_connection.Handle) != 0;
```

`test/MMLib.Alvo.Data.EntityFrameworkCore.Tests/HostHookStageTests.cs`:

```csharp
using MMLib.Alvo.Data.EntityFrameworkCore.Internal;
using MMLib.Alvo.Rules;

namespace MMLib.Alvo.Data.EntityFrameworkCore.Tests;

/// <summary>
/// Where the driver runs host write hooks (spec 2026-10-14 §4.1 stages 4, 5, 7, 7b): in a unit of the write's own only when a
/// hook is registered (Ruling 1), with the candidate, the pre-image and the stored row, one hop deeper, and the patch judged
/// by the rules again. The core's runner is a recording fake here; the end-to-end facts are <c>WriteHookFacts</c>.
/// </summary>
public sealed class HostHookStageTests
{
    private const string Note = WritePathFixture.Entity;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task A_write_no_hook_runs_on_opens_no_unit_and_takes_no_savepoint()
    {
        var hooks = new RecordingWriteExtensions();
        await using var world = await StartAsync(hooks);

        await world.Data.CreateAsync(Note, WritePathFixture.CreatePayload("plain"), world.Caller, cancellationToken: Ct);

        hooks.Calls.ShouldBeEmpty();
        world.Statements.Seen.ShouldNotContain(statement => statement.Contains("SAVEPOINT", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task A_create_runs_its_hooks_in_a_unit_of_its_own_with_the_candidate_and_the_stored_row_one_hop_deeper()
    {
        var hooks = new RecordingWriteExtensions();
        hooks.InTransaction.Add((Note, AlvoWriteOperation.Create));
        await using var world = await StartAsync(hooks);

        var created = await world.Data.CreateAsync(Note, WritePathFixture.CreatePayload("hooked"), world.Caller, cancellationToken: Ct);

        hooks.Stages.ShouldBe(["before", "after"]);
        hooks.Calls.ShouldAllBe(call => call.UnitOnFlow);
        var (_, before, _, _) = hooks.Calls.First();
        before.New!["title"].ShouldBe("hooked");
        before.Old.ShouldBeNull();
        before.Transaction!.Context.Provenance.Depth.ShouldBe(1);
        hooks.Calls.Last().Invocation.Saved!["id"].ShouldBe(created["id"]);
    }

    [Fact]
    public async Task A_patch_is_stored_and_judged_by_the_create_rule_again()
    {
        var hooks = new RecordingWriteExtensions();
        hooks.InTransaction.Add((Note, AlvoWriteOperation.Create));
        await using var world = await StartAsync(hooks, WritePathFixture.Descriptor(create: "owner_id == @user.id"));

        hooks.Patch["title"] = "set by the hook";
        var created = await world.Data.CreateAsync(Note, WritePathFixture.CreatePayload("asked"), world.Caller, cancellationToken: Ct);
        created["title"].ShouldBe("set by the hook");

        hooks.Patch["owner_id"] = Guid.NewGuid();
        await Should.ThrowAsync<AlvoAuthorizationException>(
            () => world.Data.CreateAsync(Note, WritePathFixture.CreatePayload("stolen"), world.Caller, cancellationToken: Ct));
        (await world.ScalarAsync("""SELECT COUNT(*) FROM "note" """)).ShouldBe(1L);
    }

    [Fact]
    public async Task An_update_hands_the_hook_the_pre_image_and_the_merged_candidate()
    {
        var hooks = new RecordingWriteExtensions();
        hooks.InTransaction.Add((Note, AlvoWriteOperation.Update));
        await using var world = await StartAsync(hooks);
        var id = await world.SeedAsync("before");

        await world.Data.UpdateAsync(Note, id, new Dictionary<string, object?> { ["title"] = "after" }, world.Caller, cancellationToken: Ct);

        var (_, before, _, _) = hooks.Calls.First();
        (before.Operation, before.Old!["title"], before.New!["title"]).ShouldBe((AlvoWriteOperation.Update, "before", "after"));
    }

    [Fact]
    public async Task A_replace_reports_replace_and_a_delete_hands_the_row_being_deleted()
    {
        var hooks = new RecordingWriteExtensions();
        hooks.InTransaction.Add((Note, AlvoWriteOperation.Replace));
        hooks.InTransaction.Add((Note, AlvoWriteOperation.Delete));
        await using var world = await StartAsync(hooks);
        var id = await world.SeedAsync("kept");

        await world.Data.ReplaceAsync(Note, id, WritePathFixture.CreatePayload("replaced"), world.Caller, cancellationToken: Ct);
        await world.Data.DeleteAsync(Note, id, world.Caller, cancellationToken: Ct);

        hooks.Calls.Select(call => (call.Stage, call.Invocation.Operation)).ShouldBe(
            [("before", AlvoWriteOperation.Replace), ("after", AlvoWriteOperation.Replace), ("before", AlvoWriteOperation.Delete), ("after", AlvoWriteOperation.Delete)]);
        hooks.Calls.Last().Invocation.Old!["title"].ShouldBe("replaced");
    }

    [Fact]
    public async Task A_rejected_batch_row_is_that_row_s_refusal_and_nothing_is_written()
    {
        var hooks = new RecordingWriteExtensions { RejectBefore = new RowScopedRefusal() };
        hooks.InTransaction.Add((Note, AlvoWriteOperation.Create));
        await using var world = await StartAsync(hooks);

        var result = await world.Data.CreateManyAsync(Note, [WritePathFixture.CreatePayload("a"), WritePathFixture.CreatePayload("b")], world.Caller, cancellationToken: Ct);

        result.Succeeded.ShouldBeFalse();
        result.Refusals.Select(refusal => (refusal.Index, refusal.Code)).ShouldBe([(0, "refused-by-hook"), (1, "refused-by-hook")]);
        (await world.ScalarAsync("""SELECT COUNT(*) FROM "note" """)).ShouldBe(0L);
    }

    [Fact]
    public async Task An_after_write_that_wrote_makes_the_driver_read_the_row_again()
    {
        var hooks = new RecordingWriteExtensions();
        hooks.InTransaction.Add((Note, AlvoWriteOperation.Create));
        hooks.After = (_, _) => Task.FromResult<string?>("writer");
        await using var world = await StartAsync(hooks);
        world.Statements.Clear();

        await world.Data.CreateAsync(Note, WritePathFixture.CreatePayload("re-read"), world.Caller, cancellationToken: Ct);

        world.Statements.Seen.Count(statement => statement.StartsWith("SELECT", StringComparison.OrdinalIgnoreCase) && statement.Contains("\"note\"", StringComparison.Ordinal))
            .ShouldBeGreaterThanOrEqualTo(3, "the insert's read-back, the post-image re-read, and the echo");
    }

    [Fact]
    public async Task A_write_inside_a_unit_runs_its_hooks_in_that_unit_and_none_when_it_suppresses_them()
    {
        var hooks = new RecordingWriteExtensions();
        hooks.InTransaction.Add((Note, AlvoWriteOperation.Create));
        await using var world = await StartAsync(hooks);

        await world.Transactions.TransactAsync(world.Caller, (unit, ct) => unit.CreateAsync(Note, WritePathFixture.CreatePayload("in a unit"), ct), cancellationToken: Ct);

        hooks.Stages.ShouldBe(["before", "after"]);
        hooks.Calls.First().Invocation.Transaction!.Context.Provenance.Depth.ShouldBe(1);
    }

    private static async Task<WritePathWorld> StartAsync(RecordingWriteExtensions hooks, Descriptor.AlvoDescriptor? descriptor = null)
    {
        var world = await WritePathWorld.StartAsync(descriptor ?? WritePathFixture.Descriptor(), WritePathFixture.Schema(), extensions: hooks);
        hooks.Autocommit = () => world.InAutocommit;

        return world;
    }
}
```

(`world.Statements.Seen`/`Clear()` are `StatementFailureInterceptor`'s recorded statements — read the interceptor's members and use their names. `SuppressHostHooks` needs the operations log table, which only a real boot creates: its fact is `WriteHookFacts.Suppressing_host_hooks_as_the_system_skips_them_and_writes_one_audit_row` (Task 11).)

In `EfAlvoTransactionsTests`, rewrite the HX-07 depth fact for the lazy hop (Ruling 9):

```csharp
    [Fact]
    public async Task A_write_one_hop_deeper_past_the_limit_is_refused_when_it_writes_and_a_caught_refusal_still_rolls_back()
    {
        await using var world = await StartAsync();
        var caller = world.Caller with { Provenance = new AlvoProvenance { Depth = AlvoChainDepthLimit.DefaultMaxDepth } };

        await Should.ThrowAsync<AlvoChainDepthExceededException>(() => world.Transactions.TransactAsync(caller, async (unit, ct) =>
        {
            await unit.CreateAsync(WritePathFixture.Entity, WritePathFixture.CreatePayload("at the limit"), ct);
            var deeper = ((EfAlvoTransaction)unit).Deeper();
            (await deeper.GetAsync(WritePathFixture.Entity, Guid.NewGuid(), ct)).ShouldBeNull("a read is not refused for depth");
            await Should.ThrowAsync<AlvoChainDepthExceededException>(() => deeper.CreateAsync(WritePathFixture.Entity, WritePathFixture.CreatePayload("too deep"), ct));
        }, cancellationToken: Ct));

        (await NotesAsync(world)).ShouldBe(0L);
    }
```

- [ ] **Step 2: Run them to verify they fail**

Run: `dotnet test --project test/MMLib.Alvo.Data.EntityFrameworkCore.Tests --filter-class '*HostHookStageTests'`
Expected: FAIL — `WritePathWorld.StartAsync` has no `extensions`; the port never calls a runner.

- [ ] **Step 3: Give the unit what a write's own unit needs**

`WriteUnit.cs`: the constructor gains `AlvoTransactionOptions options, bool owned` after `timeout` (the timeout is read from `options.Timeout`, so `timeout` is dropped from the parameter list):

```csharp
    internal WriteUnit(
        WriteUnitParts parts, AlvoContext caller, AlvoProvenance provenance, AlvoTransactionOptions options, bool owned, TimeProvider time,
        CancellationToken cancellationToken)
    {
        _parts = parts;
        Caller = caller;
        Provenance = provenance;
        _timeout = options.Timeout;
        SuppressesHostHooks = options.SuppressHostHooks;
        Owned = owned;
        _deadline = new CancellationTokenSource(_timeout, time);
        _lifetime = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _deadline.Token);
        _onDeadline = _deadline.Token.Register(static state => ((WriteUnit)state!).OnDeadline(), this);
    }

    /// <summary>Gets a value indicating whether the unit was opened for one top-level write rather than by <c>TransactAsync</c> (Ruling 1).</summary>
    internal bool Owned { get; }

    /// <summary>Gets a value indicating whether host write hooks are skipped for every write in the unit (Ruling 30).</summary>
    internal bool SuppressesHostHooks { get; }
```

`EfAlvoData.Units.cs`: `OpenUnitAsync` gains `bool owned = false` (last) and passes `options, owned` through `BegunAsync` to the constructor (`new WriteUnit(parts, context, UnitProvenance(context), options, owned, _time, cancellationToken)`). Replace the eager `Deeper(WriteUnit, AlvoContext)` with:

```csharp
    /// <summary>
    /// <paramref name="context"/> one hop deeper in its causation chain, unchecked: each write and publish through a unit checks
    /// the limit inside its own call, where the refusal poisons the unit (Ruling 9).
    /// </summary>
    internal static AlvoContext OneHopDeeper(AlvoContext context) =>
        context with { Provenance = context.Provenance with { Depth = AlvoEventProvenance.Effective(context).Depth + 1 } };

    /// <summary>What a host hook of a write in <paramref name="unit"/> receives as <c>write.Transaction</c>: the unit, as the writer, one hop deeper.</summary>
    internal IAlvoTransaction HookTransaction(WriteUnit unit, AlvoContext context) => new EfAlvoTransaction(this, unit, OneHopDeeper(context));

    /// <summary>Runs one top-level write in a unit of its own, on the flow, and commits it (Ruling 1).</summary>
    private async Task<T> OwnUnitAsync<T>(
        AlvoContext context, Func<WriteUnit?, CancellationToken, Task<T>> write, CancellationToken cancellationToken)
    {
        var unit = await OpenUnitAsync(context, AlvoTransactionOptions.Validated(null, context), Scopes, cancellationToken, owned: true)
            .ConfigureAwait(false);
        try
        {
            return await UnitRun.CommittedAsync(unit, () => unit.RunAsync(token => write(unit, token), cancellationToken)).ConfigureAwait(false);
        }
        finally
        {
            await unit.DisposeAsync().ConfigureAwait(false);
            await FlushRejectionsAsync(unit).ConfigureAwait(false);
        }
    }

    private IServiceScopeFactory Scopes => _scopes ?? throw new InvalidOperationException(
        "A write a host hook runs on needs the host's IServiceScopeFactory, and this data port was built without one.");

    /// <summary>Records the rejections a unit remembered, after it released its write turn and left the flow (Ruling 8).</summary>
    internal Task FlushRejectionsAsync(WriteUnit unit) =>
        _rejections is null || unit.Rejections.Count == 0 ? Task.CompletedTask : _rejections.FlushAsync(unit.Rejections);

    /// <summary>
    /// The one audit row of a unit that suppresses host hooks, written in the unit's own transaction — a rolled-back unit
    /// suppressed nothing durably (Ruling 30).
    /// </summary>
    internal async Task<DeliveryOperationEntry> AuditSuppressionAsync(WriteUnit unit, string reason, CancellationToken cancellationToken)
    {
        var entry = new DeliveryOperationEntry
        {
            Id = Guid.CreateVersion7(),
            At = _time.GetUtcNow(),
            Actor = "system",
            Operation = "suppress-host-hooks",
            Target = $"unit:{unit.Provenance.CorrelationId}",
            Reason = reason,
            Affected = 1,
        };
        await unit.RunAsync(
            token => InUnitAsync(unit, (connection, transaction, ct) => LoggedAsync(connection, transaction, entry, ct), token),
            cancellationToken).ConfigureAwait(false);

        return entry;
    }

    private async Task LoggedAsync(DbConnection connection, DbTransaction transaction, DeliveryOperationEntry entry, CancellationToken ct)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = DeliveryOperationsTable.InsertLogSql(_operationsLog);
        DeliveryOperationsTable.BindLogEntry(command, entry);
        await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }
```

(`_operationsLog = DeliveryOperationsTable.LogNameFor(options.SchemaPrefix)` is a new field set in the constructor beside `_commandTable`; `using MMLib.Alvo.Events;` for `DeliveryOperationEntry`.)

`EfAlvoTransaction.cs` — `Deeper()` becomes lazy:

```csharp
    /// <summary>The same unit as the same caller, one hop deeper; the limit is checked by each write and publish (Ruling 9).</summary>
    internal EfAlvoTransaction Deeper() => new(port, unit, EfAlvoData.OneHopDeeper(context));
```

`src/MMLib.Alvo.Data.EntityFrameworkCore/Internal/UnitRun.cs`:

```csharp
using System.Runtime.ExceptionServices;

namespace MMLib.Alvo.Data.EntityFrameworkCore.Internal;

/// <summary>
/// One unit's run, for <c>TransactAsync</c> and for a top-level write's own unit alike: on the unit's flow, a failure mapped
/// as the unit recorded it — a <see cref="TimeoutException"/> once the deadline passed, the recorded failure behind a refusal
/// that only wraps it — and a commit when the work returned.
/// </summary>
internal static class UnitRun
{
    internal static async Task<T> CommittedAsync<T>(WriteUnit unit, Func<Task<T>> work)
    {
        using var flow = unit.EnterFlow();
        var result = await MappedAsync(unit, work).ConfigureAwait(false);
        await unit.CommitAsync().ConfigureAwait(false);

        return result;
    }

    private static async Task<T> MappedAsync<T>(WriteUnit unit, Func<Task<T>> work)
    {
        try
        {
            return await work().ConfigureAwait(false);
        }
        catch (Exception failure) when (unit.TimedOut && failure is not TimeoutException)
        {
            throw unit.TimedOutBecause(failure);
        }
        catch (Exception failure) when (unit.RecordedFailureBehind(failure) is { } recorded)
        {
            ExceptionDispatchInfo.Throw(recorded);
            throw;
        }
    }
}
```

`EfAlvoTransactions.cs` — the run goes through `UnitRun`, suppression is audited, rejections are flushed after the unit ends:

```csharp
internal sealed partial class EfAlvoTransactions(EfAlvoData data, IServiceScopeFactory scopes, ILogger<EfAlvoTransactions>? logger = null) : IAlvoTransactions
{
    private readonly ILogger _logger = logger ?? NullLogger<EfAlvoTransactions>.Instance;

    /// <inheritdoc/>
    public async Task<T> TransactAsync<T>(
        AlvoContext context,
        Func<IAlvoTransaction, CancellationToken, Task<T>> work,
        AlvoTransactionOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(work);
        AlvoFlow.RefuseInsideUnit("IAlvoTransactions.TransactAsync");
        var settings = AlvoTransactionOptions.Validated(options, context);

        var unit = await data.OpenUnitAsync(context, settings, scopes, cancellationToken).ConfigureAwait(false);
        try
        {
            DeliveryOperationEntry? audit = null;
            var result = await UnitRun.CommittedAsync(unit, async () =>
            {
                audit = settings.SuppressHostHooks ? await data.AuditSuppressionAsync(unit, settings.Reason!, unit.Token).ConfigureAwait(false) : null;
                return await work(new EfAlvoTransaction(data, unit, context), unit.Token).ConfigureAwait(false);
            }).ConfigureAwait(false);
            Audited(audit);

            return result;
        }
        finally
        {
            await unit.DisposeAsync().ConfigureAwait(false);
            await data.FlushRejectionsAsync(unit).ConfigureAwait(false);
        }
    }

    private void Audited(DeliveryOperationEntry? entry)
    {
        if (entry is not null)
        {
            ManagementAudit(_logger, entry.Actor, entry.Operation, entry.Target, entry.Affected ?? 0, entry.Reason ?? string.Empty);
        }
    }

    [LoggerMessage(
        Level = LogLevel.Warning,
        Message = "Management audit: {Actor} made '{Operation}' on '{Target}' ({Affected} changed). Reason: {Reason}")]
    private static partial void ManagementAudit(ILogger logger, string actor, string operation, string target, int affected, string reason);
}
```

(The audit line is the core's `OperationsAudit.ManagementAudit` template and event name, so `AuditCapture` reads it the same way; the driver cannot call the core's.) `AlvoEfCoreProvider` passes `services.GetService<ILogger<EfAlvoTransactions>>()`.

- [ ] **Step 4: Run hooks at stages 4/5 and 7/7b**

`src/MMLib.Alvo.Data.EntityFrameworkCore/Internal/EfAlvoData.Hooks.cs`:

```csharp
using Microsoft.EntityFrameworkCore;
using MMLib.Alvo.Data.EntityFrameworkCore.Internal;
using MMLib.Alvo.Rules;
using MMLib.Alvo.Schema;

namespace MMLib.Alvo.Data.EntityFrameworkCore;

/// <summary>
/// Where a host's write hooks run in the data port (spec 2026-10-14 §4.1): before-write after the declarative before-hooks,
/// its patch judged by <c>WITH CHECK</c> and the tenant scope again (stages 4–5); after-write after the row and its rollups,
/// the stored row read again when a hook wrote (stages 7–7b). The validate stage and its pre-reads are the file's second half.
/// </summary>
/// <remarks>
/// Host hooks run only inside a unit — a <c>TransactAsync</c> unit, or the write's own (Ruling 1) — read off the flow, as
/// <see cref="Hooks"/> is: a write inside a unit only ever runs on that unit's flow.
/// </remarks>
internal sealed partial class EfAlvoData
{
    /// <summary>Gets the unit on the flow; <see langword="null"/> for a top-level write no host hook runs on.</summary>
    private static WriteUnit? FlowUnit => AlvoFlow.Unit as WriteUnit;

    /// <summary>Runs <paramref name="write"/> in a unit of its own when a host in-transaction hook runs on it; as always otherwise.</summary>
    private Task<T> HookedAsync<T>(
        string entity, AlvoWriteOperation operation, AlvoContext context, Func<WriteUnit?, CancellationToken, Task<T>> write,
        CancellationToken cancellationToken) =>
        _extensions.RunsInTransaction(entity, operation) ? OwnUnitAsync(context, write, cancellationToken) : write(null, cancellationToken);

    /// <summary>Whether host hooks run now: registered, inside a unit, and not suppressed by it.</summary>
    private bool HostHooksRun(string entity, AlvoWriteOperation operation) =>
        FlowUnit is { SuppressesHostHooks: false } && _extensions.RunsInTransaction(entity, operation);

    private WriteInvocation Invocation(EntitySchema schema, AlvoWriteOperation operation, AlvoContext context, DateTimeOffset now)
    {
        var unit = FlowUnit!;

        return new WriteInvocation
        {
            Entity = schema.Name,
            Operation = operation,
            Caller = context,
            Now = now,
            Transaction = HookTransaction(unit, context),
            Services = unit.Services,
        };
    }

    /// <summary>Stages 3–5 of a create (or a replace that creates): declarative hooks, host before-write, the rules again over the patch.</summary>
    private async Task<Dictionary<string, object>> BeforeCreateAsync(
        AlvoDataContext db, EntitySchema schema, PolicyDecision decision, AlvoContext context, Dictionary<string, object> candidate,
        AlvoWriteOperation operation, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var declared = RunBeforeCreate(db, schema, decision, context, candidate, now);
        if (!HostHooksRun(schema.Name, operation))
        {
            return declared;
        }

        var patch = await _extensions.BeforeWriteAsync(Invocation(schema, operation, context, now) with { New = Unmasked(declared) }, cancellationToken);

        return patch.Count == 0 ? declared : JudgedPatch(db, schema, decision, context, declared, patch);
    }

    private Dictionary<string, object> JudgedPatch(
        AlvoDataContext db, EntitySchema schema, PolicyDecision decision, AlvoContext context, Dictionary<string, object> candidate,
        IReadOnlyDictionary<string, object?> patch)
    {
        EnsureHookMayWrite(patch, decision);
        var patched = Patched(db.Rows(schema.Name).EntityType, candidate, patch);
        EnsureWriteAllowed(decision, Unmasked(patched), previous: null, context);

        return patched;
    }

    /// <summary>A batch row's host before-write: its refusal is the row's (Ruling 4), and so is a rule that refuses its patch.</summary>
    private async Task<(Dictionary<string, object>? Candidate, AlvoRowRefusal? Refusal)> HostBeforeCreateOrRefuseAsync(
        AlvoDataContext db, EntitySchema schema, PolicyDecision decision, AlvoContext context, Dictionary<string, object> candidate,
        DateTimeOffset now, int index, CancellationToken cancellationToken)
    {
        if (!HostHooksRun(schema.Name, AlvoWriteOperation.Create))
        {
            return (candidate, null);
        }

        IReadOnlyDictionary<string, object?> patch;
        try
        {
            patch = await _extensions.BeforeWriteAsync(
                Invocation(schema, AlvoWriteOperation.Create, context, now) with { New = Unmasked(candidate) }, cancellationToken);
        }
        catch (Exception refused) when (refused is IRowRefusal row)
        {
            return (null, row.At(index));
        }

        if (patch.Count == 0)
        {
            return (candidate, null);
        }

        if (WritePayloadGuard.ReadOnlyRefusal(patch, decision.ReadOnlyFields) is { } frozen)
        {
            return (null, Forbidden(index, frozen));
        }

        var patched = Patched(db.Rows(schema.Name).EntityType, candidate, patch);

        return WriteRefusal(decision, Unmasked(patched), previous: null, context) is { } refusal ? (null, Forbidden(index, refusal)) : (patched, null);
    }

    /// <summary>
    /// The read-only half of the caller's payload guard over a hook's patch (Ruling 48): a field the policy makes read-only for
    /// this caller is refused exactly as the caller's own payload would be — the same 403 and message. Fields frozen only by a
    /// rule's <c>WITH CHECK</c> are judged by stage 5.
    /// </summary>
    private static void EnsureHookMayWrite(IReadOnlyDictionary<string, object?> patch, PolicyDecision decision)
    {
        if (WritePayloadGuard.ReadOnlyRefusal(patch, decision.ReadOnlyFields) is { } refusal)
        {
            throw new AlvoAuthorizationException(refusal);
        }
    }

    /// <summary>Stage 4 of an update or a replace of an existing row: the host's patch over the declarative one.</summary>
    private async Task<IReadOnlyDictionary<string, object?>> HostBeforeUpdateAsync(
        EntitySchema schema, PolicyDecision decision, AlvoContext context, AlvoWriteOperation operation, AlvoRecord preImage,
        IReadOnlyDictionary<string, object?> values, DateTimeOffset now, CancellationToken cancellationToken)
    {
        if (!HostHooksRun(schema.Name, operation))
        {
            return values;
        }

        var patch = await _extensions.BeforeWriteAsync(
            Invocation(schema, operation, context, now) with { Old = preImage, New = Merge(preImage, values) }, cancellationToken);

        if (patch.Count == 0)
        {
            return values;
        }

        EnsureHookMayWrite(patch, decision);
        return Overlaid(values, patch);
    }

    private static Dictionary<string, object?> Overlaid(IReadOnlyDictionary<string, object?> values, IReadOnlyDictionary<string, object?> patch)
    {
        var overlaid = new Dictionary<string, object?>(values, StringComparer.Ordinal);
        foreach (var (field, value) in patch)
        {
            overlaid[field] = value;
        }

        return overlaid;
    }

    /// <summary>A batch row's update stage 4, its refusal the row's.</summary>
    private async Task<(IReadOnlyDictionary<string, object?>? Values, AlvoRowRefusal? Refusal)> HostBeforeUpdateOrRefuseAsync(
        EntitySchema schema, PolicyDecision decision, AlvoContext context, AlvoRecord preImage, IReadOnlyDictionary<string, object?> values,
        DateTimeOffset now, int index, CancellationToken cancellationToken)
    {
        try
        {
            return (await HostBeforeUpdateAsync(schema, decision, context, AlvoWriteOperation.Update, preImage, values, now, cancellationToken), null);
        }
        catch (Exception refused) when (refused is IRowRefusal row)
        {
            return (null, row.At(index));
        }
        catch (AlvoAuthorizationException frozen)
        {
            return (null, Forbidden(index, frozen.Message));
        }
    }

    /// <summary>Stages 7–7b: the host's after-write over the stored row, which is read again when a hook wrote.</summary>
    private async Task<Dictionary<string, object>> AfterSavedAsync(
        AlvoDataContext db, EntitySchema schema, PolicyDecision decision, AlvoContext context, AlvoWriteOperation operation, AlvoRecord? preImage,
        Dictionary<string, object> stored, DateTimeOffset now, CancellationToken cancellationToken)
    {
        if (!HostHooksRun(schema.Name, operation))
        {
            return stored;
        }

        var writer = await _extensions.AfterWriteAsync(
            Invocation(schema, operation, context, now) with { Old = preImage, Saved = Unmasked(stored) }, cancellationToken);

        return writer is null
            ? stored
            : await SingleAsync(db, schema, decision, context, RowIdOf(stored), lockFor: null, cancellationToken, unmasked: true)
                ?? throw Vanished(writer);
    }

    /// <summary>
    /// The row a hook's write made disappear from the write's own read (Ruling 45): the write fails and its unit is poisoned —
    /// never an event or an echo of a row that no longer exists.
    /// </summary>
    private static HookFailedException Vanished(string writer)
    {
        var failure = new HookFailedException(writer, HookFailedException.RowVanished);
        FlowUnit?.Poison(failure);

        return failure;
    }

    /// <summary>A batch's stage 7 for each written row: the first refusal is the batch's, and rolls every row back (Ruling 4).</summary>
    private async Task<(List<Dictionary<string, object>> Stored, AlvoRowRefusal? Refusal)> AfterSavedRowsAsync(
        AlvoDataContext db, EntitySchema schema, PolicyDecision decision, AlvoContext context, AlvoWriteOperation operation,
        IReadOnlyList<(int Index, AlvoRecord? PreImage, Dictionary<string, object> Stored)> rows, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var stored = new List<Dictionary<string, object>>(rows.Count);
        foreach (var (index, preImage, row) in rows)
        {
            try
            {
                stored.Add(await AfterSavedAsync(db, schema, decision, context, operation, preImage, row, now, cancellationToken));
            }
            catch (Exception refused) when (refused is IRowRefusal refusal)
            {
                return (stored, refusal.At(index));
            }
        }

        return (stored, null);
    }

    /// <summary>A delete's host before-delete; a delete carries no patch.</summary>
    private async Task HostBeforeDeleteAsync(EntitySchema schema, AlvoContext context, AlvoRecord preImage, DateTimeOffset now, CancellationToken cancellationToken)
    {
        if (HostHooksRun(schema.Name, AlvoWriteOperation.Delete))
        {
            _ = await _extensions.BeforeWriteAsync(Invocation(schema, AlvoWriteOperation.Delete, context, now) with { Old = preImage }, cancellationToken);
        }
    }

    /// <summary>A delete's host after-delete, once the row is gone and its rollups recomputed.</summary>
    private async Task HostAfterDeleteAsync(EntitySchema schema, AlvoContext context, AlvoRecord preImage, DateTimeOffset now, CancellationToken cancellationToken)
    {
        if (HostHooksRun(schema.Name, AlvoWriteOperation.Delete))
        {
            _ = await _extensions.AfterWriteAsync(Invocation(schema, AlvoWriteOperation.Delete, context, now) with { Old = preImage }, cancellationToken);
        }
    }

    /// <summary>A batch row's before-delete, its refusal the row's.</summary>
    private async Task<AlvoRowRefusal?> HostBeforeDeleteOrRefuseAsync(
        EntitySchema schema, AlvoContext context, AlvoRecord preImage, DateTimeOffset now, int index, CancellationToken cancellationToken)
    {
        try
        {
            await HostBeforeDeleteAsync(schema, context, preImage, now, cancellationToken);
            return null;
        }
        catch (Exception refused) when (refused is IRowRefusal row)
        {
            return row.At(index);
        }
    }

    /// <summary>The idempotency table, ensured before a keyed write opens its own unit — no DDL ever runs inside one.</summary>
    private async Task EnsureIdempotencyTableOutsideAsync(CancellationToken cancellationToken)
    {
        using var db = _contexts.Create();
        await EnsureIdempotencyTableAsync(db, cancellationToken);
    }
}
```

`EfAlvoData.cs` — the fields and the constructor:

```csharp
    /// <summary>The host's write hooks (spec 2026-10-14 §4.4); <see cref="NoWriteExtensions"/> when the core registers none.</summary>
    private readonly IWriteExtensionRunner _extensions;
    private readonly IServiceScopeFactory? _scopes;
    private readonly EfCoreHookRejectionLog? _rejections;
    private readonly string _operationsLog;
```

The constructor gains the three optional parameters after `writeGate` and sets `_extensions = extensions ?? NoWriteExtensions.Instance; _scopes = scopes; _rejections = rejections; _operationsLog = DeliveryOperationsTable.LogNameFor(options.SchemaPrefix);`. `AlvoEfCoreProvider.CreateData` passes `services.GetService<IWriteExtensionRunner>(), services.GetRequiredService<IServiceScopeFactory>(), services.GetService<EfCoreHookRejectionLog>()`.

Then the write paths. Each edit below is the whole new text of the named lines; nothing else in those methods changes.

`CreateAsync` (public) — the tail after `var decision = …`:

```csharp
        return idempotency is { } token
            ? await ReplayableCreateAsync(entity, values, decision, context, token, cancellationToken)
            : await HookedAsync(
                entity, AlvoWriteOperation.Create, context,
                (unit, ct) => CreatedAsync(entity, values, decision, context, unit, ct), cancellationToken);
```

`CreatedAsync` — the line `candidate = RunBeforeCreate(db, schema, decision, context, candidate, now);` becomes `candidate = await BeforeCreateAsync(db, schema, decision, context, candidate, AlvoWriteOperation.Create, now, cancellationToken);`, and after `await RecomputeRollupsAsync(db, schema, [stored!], cancellationToken);` add `stored = await AfterSavedAsync(db, schema, decision, context, AlvoWriteOperation.Create, preImage: null, stored, now, cancellationToken);`. `RecordedCreateAsync` takes the same two edits. `CreatedByReplaceAsync` takes them with `AlvoWriteOperation.Replace`.

`ReplayableCreateAsync`:

```csharp
    private async Task<AlvoRecord> ReplayableCreateAsync(
        string entity, IReadOnlyDictionary<string, object?> values, PolicyDecision decision, AlvoContext context,
        AlvoIdempotency token, CancellationToken cancellationToken)
    {
        await EnsureIdempotencyTableOutsideAsync(cancellationToken);

        return await ReplayableWriteAsync(
            () => HookedAsync(
                entity, AlvoWriteOperation.Create, context,
                (_, ct) => CreatedOrReplayedAsync(entity, values, decision, context, token, ct), cancellationToken),
            cancellationToken);
    }
```

`CreatedOrReplayedAsync`, `UpdatedOrReplayedAsync`, `ReplacedOrReplayedAsync`, `ErasedOrReplayedAsync`: `using var db = _contexts.Create();` becomes `using var db = ContextFor(FlowUnit);`, and `BeginAsync(db, unit: null, cancellationToken)` becomes `BeginAsync(db, FlowUnit, cancellationToken)` — the write's own unit when a hook runs, nothing otherwise (keyed writes are refused inside a `TransactAsync` unit at the entry). `ReplayedAfterKeyRaceAsync` is unchanged: it runs after the own unit has ended.

`UpdateAsync` (public) — the tail:

```csharp
        if (idempotency is { } token)
        {
            await EnsureIdempotencyTableOutsideAsync(cancellationToken);
            return await ReplayableWriteAsync(
                () => HookedAsync(
                    entity, AlvoWriteOperation.Update, context,
                    (_, ct) => UpdatedOrReplayedAsync(entity, id, values, decision, context, precondition, token, ct), cancellationToken),
                cancellationToken);
        }

        return await HookedAsync(
            entity, AlvoWriteOperation.Update, context,
            (unit, ct) => UpdatedAsync(entity, id, values, decision, context, precondition, unit, ct), cancellationToken);
```

`WriteAsync` gains `AlvoWriteOperation operation` after `values`; `WrittenAsync` passes `AlvoWriteOperation.Update`, `ReplacedRowAsync` passes `AlvoWriteOperation.Replace`. Inside `WriteAsync`:

```csharp
        var preImage = Unmasked(stored);
        var vetted = RunBeforeUpdate(schema, context, preImage, values, now);
        vetted = await HostBeforeUpdateAsync(schema, decision, context, operation, preImage, vetted, now, cancellationToken);
        EnsureWriteAllowed(decision, Merge(preImage, vetted), preImage, context);
```

and after `await RecomputeRollupsAsync(db, schema, [preImage.Values, postImage!], cancellationToken);`:

```csharp
        postImage = await AfterSavedAsync(db, schema, decision, context, operation, preImage, postImage, now, cancellationToken);
```

`ReplaceAsync` (public) — the non-keyed return and the keyed attempt:

```csharp
        if (idempotency is not { } token)
        {
            return await HookedAsync(
                entity, AlvoWriteOperation.Replace, context,
                (unit, ct) => ReplacedAsync(entity, id, values, branches, context, precondition, unit, ct), cancellationToken);
        }

        await EnsureIdempotencyTableOutsideAsync(cancellationToken);
        try
        {
            return await ReplayableWriteAsync(
                () => HookedAsync(
                    entity, AlvoWriteOperation.Replace, context,
                    (_, ct) => ReplacedOrReplayedAsync(entity, id, values, branches, context, precondition, token, ct), cancellationToken),
                cancellationToken);
        }
```

`DeleteAsync` (public):

```csharp
        if (idempotency is { } token)
        {
            await EnsureIdempotencyTableOutsideAsync(cancellationToken);
            _ = await ReplayableWriteAsync(
                () => HookedAsync(
                    entity, AlvoWriteOperation.Delete, context,
                    (_, ct) => ErasedOrReplayedAsync(entity, id, decision, context, precondition, token, ct), cancellationToken),
                cancellationToken);
            return;
        }

        await HookedAsync(
            entity, AlvoWriteOperation.Delete, context,
            async (unit, ct) =>
            {
                await ErasedAsync(entity, id, decision, context, precondition, unit, ct);
                return true;
            },
            cancellationToken);
```

`EraseAsync` — after `RunBeforeDelete(schema, context, Unmasked(stored), now);` add `await HostBeforeDeleteAsync(schema, context, Unmasked(stored), now, cancellationToken);`; after `await RecomputeRollupsAsync(db, schema, [stored!], cancellationToken);` add `await HostAfterDeleteAsync(schema, context, Unmasked(stored), now, cancellationToken);`.

Batches. The attempt delegate keeps its shape; the operation travels with the batch:

```csharp
    private Task<AlvoBatchResult> BatchAsync(
        string entity,
        AlvoWriteOperation operation,
        AlvoContext context,
        AlvoIdempotency? idempotency,
        Func<AlvoDataContext, IDbContextTransaction, EntitySchema, DateTimeOffset, Task<BatchOutcome>> attempt,
        bool producesRows,
        WriteUnit? unit,
        CancellationToken cancellationToken)
    {
        Task<AlvoBatchResult> Once() => unit is not null
            ? OneBatchAsync(entity, context, idempotency, attempt, producesRows, unit, cancellationToken)
            : HookedAsync(entity, operation, context, (own, ct) => OneBatchAsync(entity, context, idempotency, attempt, producesRows, own, ct), cancellationToken);

        return idempotency is null ? Once() : KeyedBatchAsync(Once, cancellationToken);
    }

    private async Task<AlvoBatchResult> KeyedBatchAsync(Func<Task<AlvoBatchResult>> once, CancellationToken cancellationToken)
    {
        await EnsureIdempotencyTableOutsideAsync(cancellationToken);
        return await ReplayableWriteAsync(once, ContendedBatchAttempts, cancellationToken);
    }
```

The three public batch methods and the three `*ManyInUnitAsync` methods pass `AlvoWriteOperation.Create`/`.Update`/`.Delete` as the second argument. `EnsureNotKeyedInsideUnit` refuses only a unit the write did not open: `if (unit is { Owned: false } && idempotency is not null)`. The row methods read the unit off the flow (no signature change):

- `CreatedRowsAsync` — after `RunBeforeCreateOrRefuse` succeeds:

```csharp
            var (hostPatched, refusedByHost) = await HostBeforeCreateOrRefuseAsync(db, schema, decision, context, hooked!, now, index, cancellationToken);
            if (refusedByHost is not null)
            {
                refusals.Add(refusedByHost);
                continue;
            }

            judged.Add(hostPatched!);
```

  and after `await RecomputeRollupsAsync(db, schema, stored!, cancellationToken);`:

```csharp
        using var held = HeldRows.HoldAll(schema.Name, stored.Select(RowIdOf));
        var (saved, refusedAfter) = await AfterSavedRowsAsync(
            db, schema, decision, context, AlvoWriteOperation.Create, [.. stored.Select((row, index) => (index, (AlvoRecord?)null, row))], now, cancellationToken);
        if (refusedAfter is not null)
        {
            return BatchOutcome.Refused([refusedAfter]);
        }

        stored = saved;
```

  (`judged` holds every row in request order once no row was refused, so a stored row's position is its request index.)

- **Every row of a batch is in a hook stage while the batch's hooks run (Ruling 43).** `UpdatedRowsAsync` and `RemovedRowsAsync` hold every id of the batch, right after their repeated-row check and for the rest of the method (which covers `WrittenRowsAsync`'s after pass): `using var held = HeldRows.HoldAll(schema.Name, rows.Select(row => row.Id));` (`ids` for a delete). `CreatedRowsAsync` holds the stored ids around its after pass (below). A hook of one row that writes a sibling row is then a self-write, and the sibling's echo and event can never be stale.
- `UpdatedRowsAsync` — after the `RunBeforeUpdate` try/catch:

```csharp
            var (hostVetted, refusedByHost) = await HostBeforeUpdateOrRefuseAsync(schema, decision, context, preImage, vetted, now, index, cancellationToken);
            if (refusedByHost is not null)
            {
                refusals.Add(refusedByHost);
                continue;
            }

            vetted = hostVetted!;
```

- `WrittenRowsAsync` — after its `RecomputeRollupsAsync`:

```csharp
        var (saved, refusedAfter) = await AfterSavedRowsAsync(
            db, schema, decision, context, AlvoWriteOperation.Update,
            [.. written.Select(row => (row.Index, (AlvoRecord?)row.PreImage, row.PostImage))], now, cancellationToken);
        if (refusedAfter is not null)
        {
            return BatchOutcome.Refused([refusedAfter]);
        }

        written = [.. written.Zip(saved, (row, post) => (row.Index, row.Id, row.PreImage, post))];
```

- `RemovedRowsAsync` — after the `RunBeforeDelete` try/catch, `if (await HostBeforeDeleteOrRefuseAsync(schema, context, Unmasked(locked), now, index, cancellationToken) is { } refusedByHost) { refusals.Add(refusedByHost); continue; }`; after its `RecomputeRollupsAsync`:

```csharp
        foreach (var (index, _, preImage) in judged)
        {
            try
            {
                await HostAfterDeleteAsync(schema, context, preImage, now, cancellationToken);
            }
            catch (Exception refused) when (refused is IRowRefusal row)
            {
                return BatchOutcome.Refused([row.At(index)]);
            }
        }
```

A batch's refused outcome returns before `transaction.CommitAsync`, so its own transaction (or savepoint) rolls back: the rows the after-write refused never commit.

`WritePayloadGuard.ReadOnlyRefusal(IReadOnlyDictionary<string, object?>, IReadOnlySet<string>)` becomes `internal static` (it is private today; body unchanged), so a hook's patch is refused with the caller's own message (Ruling 48). `ChangeTrackerReachTests._sqlComposingFiles` gains `"EfAlvoData.Hooks.cs"`; any other scan in that class that pins the files naming `db.Rows(`, `.Where(` or `SingleAsync` gains it too (read the failures, add the file to exactly the lists that name `EfAlvoData.cs` for the same reason).

- [ ] **Step 5: Run the driver facts, the HX-07 suites and ring1**

Run: `dotnet test --project test/MMLib.Alvo.Data.EntityFrameworkCore.Tests --filter-class '*HostHookStageTests'`, then `--filter-class '*EfAlvoTransactionsTests'`, `--filter-class '*ChangeTrackerReachTests'`.
Expected: PASS. Then `scripts/test-ring1` — every existing `AlvoData*Tests` must pass unchanged (a write with no hook is byte-for-byte HX-07, Ruling 1).

- [ ] **Step 6: Commit**

```bash
dotnet format whitespace MMLib.Alvo.slnx --include src/MMLib.Alvo.Data.EntityFrameworkCore/Internal/EfAlvoData.Hooks.cs src/MMLib.Alvo.Data.EntityFrameworkCore/Internal/UnitRun.cs src/MMLib.Alvo.Data.EntityFrameworkCore/Internal/EfAlvoData.cs src/MMLib.Alvo.Data.EntityFrameworkCore/Internal/EfAlvoData.Units.cs src/MMLib.Alvo.Data.EntityFrameworkCore/Internal/EfAlvoTransaction.cs src/MMLib.Alvo.Data.EntityFrameworkCore/Internal/EfAlvoTransactions.cs src/MMLib.Alvo.Data.EntityFrameworkCore/Internal/WriteUnit.cs src/MMLib.Alvo.Data.EntityFrameworkCore/AlvoEfCoreProvider.cs test/MMLib.Alvo.Data.EntityFrameworkCore.Tests/RecordingWriteExtensions.cs test/MMLib.Alvo.Data.EntityFrameworkCore.Tests/HostHookStageTests.cs test/MMLib.Alvo.Data.EntityFrameworkCore.Tests/WritePathWorld.cs test/MMLib.Alvo.Data.EntityFrameworkCore.Tests/EfAlvoTransactionsTests.cs test/MMLib.Alvo.Data.EntityFrameworkCore.Tests/ChangeTrackerReachTests.cs
scripts/test-ring1
git add src/MMLib.Alvo.Data.EntityFrameworkCore test/MMLib.Alvo.Data.EntityFrameworkCore.Tests
git commit -m "feat(data-ef): run host write hooks inside the write's transaction

Claude-Session: https://claude.ai/code/session_01NJAtafM29iddLwfhb5gnmB"
```

---

## Task 4: The driver's validate stage — the idempotency pre-lookup, E9's pre-read, hoisted out of the replay loop and the batch transaction

**Files:**
- Modify: `src/MMLib.Alvo.Data.EntityFrameworkCore/Internal/EfAlvoData.Hooks.cs`, `src/MMLib.Alvo.Data.EntityFrameworkCore/Internal/EfAlvoData.cs`, `src/MMLib.Alvo.Data.EntityFrameworkCore/Internal/IdempotencyTable.cs`
- Test: `test/MMLib.Alvo.Data.EntityFrameworkCore.Tests/HostHookStageTests.cs` (six more facts)

**Interfaces:**
- Consumes: Task 3's `HookedAsync`, `RecordingWriteExtensions`.
- Produces: the stage-2 entry points `ValidateCreateAsync`, `ValidateChangeAsync`, `ValidateRowsAsync` (private), and `IdempotencyTable.FindAsync(DbConnection, DbTransaction?, …)`.

- [ ] **Step 1: Write the failing facts** (append to `HostHookStageTests`)

```csharp
    [Fact]
    public async Task Validate_runs_before_any_transaction_with_the_authorized_candidate_and_no_id()
    {
        var hooks = new RecordingWriteExtensions();
        hooks.Validating.Add((Note, AlvoWriteOperation.Create));
        await using var world = await StartAsync(hooks);
        world.Statements.Clear();

        await world.Data.CreateAsync(Note, WritePathFixture.CreatePayload("checked"), world.Caller, cancellationToken: Ct);

        var (stage, invocation, unitOnFlow, autocommit) = hooks.Calls.ShouldHaveSingleItem();
        (stage, unitOnFlow, autocommit, invocation.Transaction).ShouldBe(("validate", false, (bool?)true, (IAlvoTransaction?)null));
        invocation.New!["title"].ShouldBe("checked");
        invocation.New.Values.ContainsKey("id").ShouldBeFalse("the row id is minted per attempt");
    }

    [Fact]
    public async Task Validate_of_an_update_sees_the_row_read_before_the_transaction_and_a_row_the_rule_does_not_admit_is_404_before_any_hook()
    {
        var hooks = new RecordingWriteExtensions();
        hooks.Validating.Add((Note, AlvoWriteOperation.Update));
        await using var world = await StartAsync(hooks, WritePathFixture.Descriptor(update: "title != 'locked'"));
        var open = await world.SeedAsync("open");
        var locked = await world.SeedAsync("locked");

        await world.Data.UpdateAsync(Note, open, new Dictionary<string, object?> { ["title"] = "moved" }, world.Caller, cancellationToken: Ct);
        await Should.ThrowAsync<AlvoRecordNotFoundException>(() => world.Data.UpdateAsync(Note, locked, new Dictionary<string, object?> { ["title"] = "x" }, world.Caller, cancellationToken: Ct));

        var validated = hooks.Calls.ShouldHaveSingleItem().Invocation;
        (validated.Old!["title"], validated.New!["title"]).ShouldBe(("open", "moved"));
    }

    [Fact]
    public async Task A_validate_hook_never_sees_an_update_the_payload_guard_refuses()
    {
        var hooks = new RecordingWriteExtensions();
        hooks.Validating.Add((Note, AlvoWriteOperation.Update));
        await using var world = await StartAsync(hooks);
        var id = await world.SeedAsync("kept");

        await Should.ThrowAsync<AlvoAuthorizationException>(() => world.Data.UpdateAsync(
            Note, id, new Dictionary<string, object?> { ["created_by"] = "someone else" }, world.Caller, cancellationToken: Ct));

        hooks.Calls.ShouldBeEmpty();
    }

    [Fact]
    public async Task A_batch_reports_every_bad_row_whichever_check_refused_it()
    {
        var hooks = new RecordingWriteExtensions();
        hooks.Validating.Add((Note, AlvoWriteOperation.Update));
        await using var world = await StartAsync(hooks);
        var kept = await world.SeedAsync("kept");

        var result = await world.Data.UpdateManyAsync(
            Note,
            [new AlvoRowPatch(kept, new Dictionary<string, object?> { ["title"] = "fine" }), new AlvoRowPatch(kept, new Dictionary<string, object?> { ["created_by"] = "x" }), new AlvoRowPatch(Guid.NewGuid(), new Dictionary<string, object?> { ["title"] = "gone" })],
            world.Caller,
            cancellationToken: Ct);

        result.Refusals.Select(refusal => refusal.Index).ShouldBe([1, 2]);
        hooks.Calls.Count.ShouldBe(1, "only the admitted row reached the validate hook");
    }

    [Fact]
    public async Task A_replayed_key_skips_validate()
    {
        var hooks = new RecordingWriteExtensions();
        hooks.Validating.Add((Note, AlvoWriteOperation.Create));
        await using var world = await StartAsync(hooks);
        var key = new AlvoIdempotency("create-1", "fingerprint-1");

        await world.Data.CreateAsync(Note, WritePathFixture.CreatePayload("once"), world.Caller, key, Ct);
        await world.Data.CreateAsync(Note, WritePathFixture.CreatePayload("once"), world.Caller, key, Ct);

        hooks.Calls.Count.ShouldBe(1);
    }

    [Fact]
    public async Task A_batch_validates_every_row_before_its_transaction_and_a_refused_row_refuses_the_batch()
    {
        var hooks = new RecordingWriteExtensions();
        hooks.Validating.Add((Note, AlvoWriteOperation.Create));
        await using var world = await StartAsync(hooks);
        world.Statements.Clear();

        var result = await world.Data.CreateManyAsync(Note, [WritePathFixture.CreatePayload("a"), WritePathFixture.CreatePayload("b")], world.Caller, cancellationToken: Ct);

        result.Succeeded.ShouldBeTrue();
        hooks.Calls.Select(call => (call.Stage, call.UnitOnFlow, call.Autocommit)).ShouldBe([("validate", false, (bool?)true), ("validate", false, (bool?)true)]);
    }
```

- [ ] **Step 2: Run them to verify they fail**

Run: `dotnet test --project test/MMLib.Alvo.Data.EntityFrameworkCore.Tests --filter-class '*HostHookStageTests'`
Expected: the four new facts FAIL (no validate call).

- [ ] **Step 3: Write the validate stage** (append to `EfAlvoData.Hooks.cs`)

```csharp
    /// <summary>
    /// Stages 0–2 of a create: the key looked up autocommit (Ruling 14), the authorized candidate (stage 1), and the validate hooks —
    /// before the replay loop and before any transaction (C14, C15). The candidate's id is left out: it is minted per attempt.
    /// </summary>
    private async Task ValidateCreateAsync(
        string entity, IReadOnlyDictionary<string, object?> values, PolicyDecision decision, AlvoContext context, AlvoIdempotency? idempotency,
        CancellationToken cancellationToken)
    {
        if (!_extensions.Validates(entity, AlvoWriteOperation.Create) || await ReplaysAsync(idempotency, context, cancellationToken))
        {
            return;
        }

        using var db = _contexts.Create();
        var now = WriteInstantNow();
        var (_, candidate) = AuthorizedCandidate(db, entity, values, decision, context, now);
        await _extensions.ValidateAsync(Validation(entity, AlvoWriteOperation.Create, context, now) with { New = WithoutId(Unmasked(candidate)) }, cancellationToken);
    }

    /// <summary>
    /// Stages 0–2 of an update or a replace: the caller's payload guard first (Ruling 41 — no hook sees a payload the write will
    /// refuse), E9's pre-read — autocommit, unmasked, under the operation's own decision (Ruling 13) — and the validate hooks.
    /// An update whose row the pre-read does not find answers 404 before any hook runs: validate never fails open (Ruling 42).
    /// A replace whose row it does not find is validated as the create it will be.
    /// </summary>
    private async Task ValidateChangeAsync(
        string entity, Guid id, IReadOnlyDictionary<string, object?> values, AlvoWriteOperation operation, PolicyDecision decision,
        AlvoContext context, AlvoIdempotency? idempotency, CancellationToken cancellationToken)
    {
        if (!_extensions.Validates(entity, operation) || await ReplaysAsync(idempotency, context, cancellationToken))
        {
            return;
        }

        using var db = _contexts.Create();
        var schema = GuardedPayload(db, entity, values, operation, decision);
        var old = await PreReadAsync(db, schema, decision, context, id, cancellationToken);
        if (old is null && operation == AlvoWriteOperation.Update)
        {
            throw new AlvoRecordNotFoundException();
        }

        await _extensions.ValidateAsync(
            Validation(entity, operation, context, WriteInstantNow()) with { Old = old, New = ValidatedNew(schema, id, old, values, operation) },
            cancellationToken);
    }

    /// <summary>The checks a caller's payload meets before anything else (stage 1): the payload guard, and a replace's whole-row guard.</summary>
    private static EntitySchema GuardedPayload(
        AlvoDataContext db, string entity, IReadOnlyDictionary<string, object?> values, AlvoWriteOperation operation, PolicyDecision decision)
    {
        var schema = Entity(db, entity) ?? throw new AlvoAuthorizationException(UnknownEntityMessage);
        WritePayloadGuard.EnsureWritable(values, schema, decision, isUpdate: true);
        if (operation == AlvoWriteOperation.Replace)
        {
            WholeRowGuard.EnsureWholeRow(WholeRowValues(schema, values), schema);
        }

        return schema;
    }

    private async Task<AlvoRecord?> PreReadAsync(
        AlvoDataContext db, EntitySchema schema, PolicyDecision decision, AlvoContext context, Guid id, CancellationToken cancellationToken) =>
        await SingleAsync(db, schema, decision, context, id, lockFor: null, cancellationToken, unmasked: true) is { } row ? Unmasked(row) : null;

    private static AlvoRecord ValidatedNew(
        EntitySchema schema, Guid id, AlvoRecord? old, IReadOnlyDictionary<string, object?> values, AlvoWriteOperation operation) =>
        operation == AlvoWriteOperation.Replace ? new AlvoRecord(WholeRowValues(schema, values)).With(AlvoManagedColumns.Id, id) : Merge(old!, values);

    /// <summary>
    /// A batch's stage 2: every row judged — its payload guard, its rule, its row — and every admitted row validated, once,
    /// before the replay loop and before <c>OneBatchAsync</c> opens its transaction (C16). Every bad row is reported, whichever
    /// check refused it (Ruling 59); any refusal refuses the batch with no transaction opened.
    /// </summary>
    private async Task<IReadOnlyList<AlvoRowRefusal>> ValidateRowsAsync(
        string entity, AlvoWriteOperation operation, int count,
        Func<AlvoDataContext, EntitySchema, int, Task<(WriteInvocation? Invocation, AlvoRowRefusal? Refusal)>> judge,
        AlvoContext context, AlvoIdempotency? idempotency, CancellationToken cancellationToken)
    {
        if (!_extensions.Validates(entity, operation) || await ReplaysAsync(idempotency, context, cancellationToken))
        {
            return [];
        }

        using var db = _contexts.Create();
        var schema = Entity(db, entity) ?? throw new AlvoAuthorizationException(UnknownEntityMessage);
        var refusals = new List<AlvoRowRefusal>();
        for (var index = 0; index < count; index++)
        {
            var (invocation, refusal) = await judge(db, schema, index);
            if ((refusal ?? (invocation is null ? null : await ValidatedRowAsync(invocation, index, cancellationToken))) is { } refused)
            {
                refusals.Add(refused);
            }
        }

        return refusals;
    }

    private async Task<AlvoRowRefusal?> ValidatedRowAsync(WriteInvocation invocation, int index, CancellationToken cancellationToken)
    {
        try
        {
            await _extensions.ValidateAsync(invocation, cancellationToken);
            return null;
        }
        catch (Exception refused) when (refused is IRowRefusal row)
        {
            return row.At(index);
        }
    }

    /// <summary>One create row: its payload and rule refusal, or its validation.</summary>
    private Func<AlvoDataContext, EntitySchema, int, Task<(WriteInvocation?, AlvoRowRefusal?)>> CreateRowValidation(
        IReadOnlyList<IReadOnlyDictionary<string, object?>> rows, PolicyDecision decision, AlvoContext context, DateTimeOffset now) =>
        (db, schema, index) => Task.FromResult<(WriteInvocation?, AlvoRowRefusal?)>(JudgedCandidate(db, schema, decision, context, rows[index], now, index) switch
        {
            ({ } candidate, null) => (Validation(schema.Name, AlvoWriteOperation.Create, context, now) with { New = WithoutId(Unmasked(candidate)) }, null),
            (_, var refusal) => (null, refusal),
        });

    /// <summary>One update row: its payload refusal, the row unavailable to this caller (fail closed, Ruling 42), or its validation.</summary>
    private Func<AlvoDataContext, EntitySchema, int, Task<(WriteInvocation?, AlvoRowRefusal?)>> UpdateRowValidation(
        IReadOnlyList<AlvoRowPatch> rows, PolicyDecision decision, AlvoContext context, DateTimeOffset now) =>
        async (db, schema, index) =>
        {
            if (WritePayloadGuard.PayloadRefusal(rows[index].Values, schema, decision, isUpdate: true) is { } unwritable)
            {
                return (null, Forbidden(index, unwritable));
            }

            return await PreReadAsync(db, schema, decision, context, rows[index].Id, CancellationToken.None) is { } old
                ? (Validation(schema.Name, AlvoWriteOperation.Update, context, now) with { Old = old, New = Merge(old, rows[index].Values) }, null)
                : (null, Unavailable(index));
        };

    private static WriteInvocation Validation(string entity, AlvoWriteOperation operation, AlvoContext context, DateTimeOffset now) =>
        new() { Entity = entity, Operation = operation, Caller = context, Now = now };

    private static AlvoRecord WithoutId(AlvoRecord record) =>
        new(record.Values.Where(entry => entry.Key != AlvoManagedColumns.Id).ToDictionary(StringComparer.Ordinal));

    /// <summary>Stage 0: whether the key is already recorded for this caller — read autocommit, before any transaction (Ruling 14).</summary>
    private async Task<bool> ReplaysAsync(AlvoIdempotency? idempotency, AlvoContext context, CancellationToken cancellationToken)
    {
        if (idempotency is not { } token)
        {
            return false;
        }

        using var db = _contexts.Create();
        await EnsureIdempotencyTableAsync(db, cancellationToken);
        var connection = db.Database.GetDbConnection();
        await RelationalSqlBatch.OpenAsync(connection, cancellationToken);

        return await IdempotencyTable.FindAsync(connection, transaction: null, _idempotencyTable, token.Key, AlvoIdempotency.IdentityOf(context), cancellationToken)
            is not null;
    }
```

(`WholeRowValues(schema, values)` and `Merge` are the existing private statics of `EfAlvoData.cs`; `JudgedCandidate` answers `(candidate, null)` for a row its payload guard and rule admit.)

`IdempotencyTable.FindAsync`: `DbTransaction transaction` becomes `DbTransaction? transaction` (the command's `Transaction` is assigned as is; `null` is an autocommit read).

`EfAlvoData.cs` — the public entries run stage 2 right after their decisions:

- `CreateAsync`: after `var decision = …`: `await ValidateCreateAsync(entity, values, decision, context, idempotency, cancellationToken);`
- `UpdateAsync` (the payload guard runs inside `ValidateChangeAsync` first, Ruling 41): `await ValidateChangeAsync(entity, id, values, AlvoWriteOperation.Update, decision, context, idempotency, cancellationToken);`
- `ReplaceAsync`: after `var branches = …`: `await ValidateChangeAsync(entity, id, values, AlvoWriteOperation.Replace, branches.Update, context, idempotency, cancellationToken);`
- `DeleteAsync` and `DeleteManyAsync` run no validate stage: no validate hook runs on a delete (§4.4.1 has no validate-delete context; `RunsOn` gives validate create and update only — Ruling 49).
- `CreateManyAsync` and `UpdateManyAsync` become `async` and, after their `BatchDecision`:

```csharp
        var refused = await ValidateRowsAsync(
            entity, AlvoWriteOperation.Create, rows.Count, CreateRowValidation(rows, decision, context, WriteInstantNow()), context, idempotency, cancellationToken);
        if (refused.Count > 0)
        {
            return AlvoBatchResult.Refused(BatchWrite.InRequestOrder([.. refused]));
        }
```

  (`UpdateRowValidation(rows, decision, …)` for `UpdateManyAsync`). The `*InUnitAsync` methods never validate (§4.1: no unit on the flow is the definition of top-level).

- [ ] **Step 4: Run them and ring0**

Run: `dotnet test --project test/MMLib.Alvo.Data.EntityFrameworkCore.Tests --filter-class '*HostHookStageTests'` → PASS; `scripts/test-ring0`.

- [ ] **Step 5: Commit**

```bash
dotnet format whitespace MMLib.Alvo.slnx --include src/MMLib.Alvo.Data.EntityFrameworkCore/Internal/EfAlvoData.Hooks.cs src/MMLib.Alvo.Data.EntityFrameworkCore/Internal/EfAlvoData.cs src/MMLib.Alvo.Data.EntityFrameworkCore/Internal/IdempotencyTable.cs test/MMLib.Alvo.Data.EntityFrameworkCore.Tests/HostHookStageTests.cs
git add src/MMLib.Alvo.Data.EntityFrameworkCore test/MMLib.Alvo.Data.EntityFrameworkCore.Tests
git commit -m "feat(data-ef): validate before any transaction, once per write and per batch row

Claude-Session: https://claude.ai/code/session_01NJAtafM29iddLwfhb5gnmB"
```

---

## Task 5: The core's public hook surface — interfaces, contexts, the typed condition, the rejection, `AlvoHookOptions`

**Files:**
- Create: `src/MMLib.Alvo/Hooks/IValidateWriteHook.cs`, `IValidateWriteHook{T}.cs`, `IBeforeWriteHook.cs`, `IBeforeWriteHook{T}.cs`, `IAfterWriteHook.cs`, `IAfterWriteHook{T}.cs`, `IBeforeDeleteHook.cs`, `IBeforeDeleteHook{T}.cs`, `IAfterDeleteHook.cs`, `IAfterDeleteHook{T}.cs`, `ValidateWrite{T}.cs`, `BeforeWrite{T}.cs`, `AfterWrite{T}.cs`, `BeforeDelete{T}.cs`, `AfterDelete{T}.cs`, `WriteCondition{T}.cs`, `AlvoWriteRejectedException.cs`, `AlvoWriteRejectionKind.cs`, `Internal/HookRun.cs`, `Internal/HookRejections.cs`, `Internal/HookImage.cs`; `src/MMLib.Alvo/Extensions/AlvoHookOptions.cs`
- Test: `test/MMLib.Alvo.Tests/Hooks/HookRejectionsTests.cs`, `test/MMLib.Alvo.Tests/Hooks/HookContextTests.cs`, `test/MMLib.Alvo.Tests/Hooks/FakeHookTransaction.cs`

**Interfaces:**
- Consumes: Task 1's `WriteInvocation`, `AlvoWriteOperation`, `IRowRefusal`; `AlvoViolation`, `PayloadViolations.PointerTo/BodyPointer`, `RecordShape.Of(...).Materialize<T>`, `TypedPatch<T>`, `RecordWriter.Field` (existing).
- Produces: `HookRun(string id, WriteInvocation invocation, IReadOnlySet<string> pointable, IAlvoTransaction? transaction, IAlvoSession? data, CancellationToken token)` with `Id`, `Invocation`, `Transaction`, `Data`, `Token`, `Rejections`, `Sets`, `Set(string, object?)`, `Failure`, `Fail(HookFailedException)`, `Operations`, `Wrote`; `HookRejections.Record(kind, code, message, field)`, `.Winner()`; `HookImage<T>.Of(AlvoRecord?)`; the public types listed in Global Constraints.

- [ ] **Step 1: Write the failing tests**

`test/MMLib.Alvo.Tests/Hooks/FakeHookTransaction.cs`:

```csharp
using MMLib.Alvo.Data;
using System.Collections.Concurrent;

namespace MMLib.Alvo.Tests.Hooks;

/// <summary>A transaction double that records every call and answers a fixed record; a hook's tests never touch a database.</summary>
internal sealed class FakeHookTransaction(AlvoContext context) : IAlvoTransaction
{
    internal ConcurrentQueue<string> Calls { get; } = new();

    internal CancellationToken LastToken { get; private set; }

    public AlvoContext Context => context;

    public Task<AlvoPage> QueryAsync(AlvoQuery query, CancellationToken cancellationToken = default) => Answer("query", cancellationToken, new AlvoPage { Items = [] });

    public Task<AlvoRecord?> GetAsync(string entity, Guid id, CancellationToken cancellationToken = default) => Answer<AlvoRecord?>($"get {entity}/{id}", cancellationToken, null);

    public Task<AlvoRecord> CreateAsync(string entity, IReadOnlyDictionary<string, object?> values, CancellationToken cancellationToken = default) =>
        Answer($"create {entity}", cancellationToken, new AlvoRecord(values));

    public Task<AlvoRecord> UpdateAsync(string entity, Guid id, IReadOnlyDictionary<string, object?> values, AlvoPrecondition? precondition = null, CancellationToken cancellationToken = default) =>
        Answer($"update {entity}/{id}", cancellationToken, new AlvoRecord(values));

    public Task<AlvoReplaceResult> ReplaceAsync(string entity, Guid id, IReadOnlyDictionary<string, object?> values, AlvoPrecondition? precondition = null, CancellationToken cancellationToken = default) =>
        Answer($"replace {entity}/{id}", cancellationToken, AlvoReplaceResult.ReplacedRow(new AlvoRecord(values)));

    public Task DeleteAsync(string entity, Guid id, AlvoPrecondition? precondition = null, CancellationToken cancellationToken = default) =>
        Answer($"delete {entity}/{id}", cancellationToken, true);

    public Task<AlvoBatchResult> CreateManyAsync(string entity, IReadOnlyList<IReadOnlyDictionary<string, object?>> rows, CancellationToken cancellationToken = default) =>
        Answer($"create-many {entity}", cancellationToken, AlvoBatchResult.Wrote([], rows.Count));

    public Task<AlvoBatchResult> UpdateManyAsync(string entity, IReadOnlyList<AlvoRowPatch> rows, CancellationToken cancellationToken = default) =>
        Answer($"update-many {entity}", cancellationToken, AlvoBatchResult.Wrote([], rows.Count));

    public Task<AlvoBatchResult> DeleteManyAsync(string entity, IReadOnlyList<Guid> ids, CancellationToken cancellationToken = default) =>
        Answer($"delete-many {entity}", cancellationToken, AlvoBatchResult.Wrote([], ids.Count));

    public Task PublishAsync(string type, string subject, IReadOnlyDictionary<string, object?>? data, CancellationToken cancellationToken = default) =>
        Answer($"publish {type}", cancellationToken, true);

    public IAlvoTransaction AsSystem() => new FakeHookTransaction(AlvoContext.System(context.Tenant) with { Provenance = context.Provenance }) { };

    private Task<T> Answer<T>(string call, CancellationToken token, T answer)
    {
        token.ThrowIfCancellationRequested();
        LastToken = token;
        Calls.Enqueue(call);
        return Task.FromResult(answer);
    }
}
```

(Check `AlvoPage`'s required members and `AlvoBatchResult.Wrote`'s signature in Abstractions; construct them as their factories allow.)

`test/MMLib.Alvo.Tests/Hooks/HookRejectionsTests.cs`:

```csharp
using MMLib.Alvo.Hooks;
using MMLib.Alvo.Hooks.Internal;

namespace MMLib.Alvo.Tests.Hooks;

/// <summary>The rejection model (spec 2026-10-14 §4.4.2): precedence, every violation of the winning kind, RFC 6901 pointers, Ruling X.</summary>
public sealed class HookRejectionsTests
{
    private static readonly IReadOnlySet<string> _hidden = new HashSet<string>(StringComparer.Ordinal) { "title", "slot", "starts_at", "a/b~c" };

    [Fact]
    public void Nothing_recorded_is_no_rejection() => new HookRejections("guard", _hidden).Winner().ShouldBeNull();

    [Fact]
    public void Forbidden_beats_conflict_beats_invalid_and_carries_every_violation_of_its_kind()
    {
        var rejections = new HookRejections("guard", _hidden);
        rejections.Record(AlvoWriteRejectionKind.Invalid, "too-short", "Too short.", "title");
        rejections.Record(AlvoWriteRejectionKind.Conflict, "taken", "Taken.", "slot");
        rejections.Record(AlvoWriteRejectionKind.Conflict, "overlap", "Overlaps.", "starts_at");

        var winner = rejections.Winner().ShouldNotBeNull();
        winner.Kind.ShouldBe(AlvoWriteRejectionKind.Conflict);
        winner.Violations.Select(violation => (violation.Pointer, violation.Code)).ShouldBe([("/slot", "taken"), ("/starts_at", "overlap")]);
        (winner.Code, winner.Pointer, winner.ExtensionId).ShouldBe(("taken", "/slot", "guard"));

        rejections.Record(AlvoWriteRejectionKind.Forbidden, "not-yours", "Not yours.", null);
        rejections.Winner()!.Kind.ShouldBe(AlvoWriteRejectionKind.Forbidden);
    }

    [Fact]
    public void A_hidden_field_gets_the_body_pointer() =>
        Recorded(AlvoWriteRejectionKind.Invalid, "too-low", "Below the bar.", "internal_score").Pointer.ShouldBe(string.Empty);

    [Theory]
    [InlineData("undeclared_field")]
    [InlineData("Title")]
    public void A_name_that_is_not_a_declared_visible_field_gets_the_body_pointer(string field) =>
        Recorded(AlvoWriteRejectionKind.Invalid, "bad", "Bad.", field).Pointer.ShouldBe(string.Empty);

    [Fact]
    public void A_field_name_is_escaped_as_a_json_pointer() =>
        Recorded(AlvoWriteRejectionKind.Invalid, "bad", "Bad.", "a/b~c").Pointer.ShouldBe("/a~1b~0c");

    [Theory]
    [InlineData("", "message")]
    [InlineData("code", " ")]
    public void A_rejection_needs_a_code_and_a_message(string code, string message) =>
        Should.Throw<ArgumentException>(() => new HookRejections("guard", _hidden).Record(AlvoWriteRejectionKind.Invalid, code, message, null));

    [Fact]
    public void A_rejection_is_one_batch_row_s_refusal()
    {
        var rejection = Recorded(AlvoWriteRejectionKind.Conflict, "taken", "Taken.", "slot");

        var row = ((MMLib.Alvo.Rules.IRowRefusal)rejection).At(4);

        (row.Index, row.Code, row.Message, row.FixSuggestion).ShouldBe((4, "taken", "Taken.", (string?)null));
    }

    private static AlvoWriteRejectedException Recorded(AlvoWriteRejectionKind kind, string code, string message, string? field)
    {
        var rejections = new HookRejections("guard", _hidden);
        rejections.Record(kind, code, message, field);

        return rejections.Winner()!;
    }
}
```

`test/MMLib.Alvo.Tests/Hooks/HookContextTests.cs`:

```csharp
using MMLib.Alvo.Data;
using MMLib.Alvo.Hooks;
using MMLib.Alvo.Hooks.Internal;
using MMLib.Alvo.Rules;

namespace MMLib.Alvo.Tests.Hooks;

[AlvoEntity("bookings")]
public sealed class HookBooking
{
    public Guid Id { get; init; }

    public required string Guest { get; init; }

    public long Nights { get; init; }
}

/// <summary>What a hook sees and records through its context (spec 2026-10-14 §4.4.1, D2).</summary>
public sealed class HookContextTests
{
    private static readonly AlvoContext _caller = AlvoContext.System(null);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public void A_typed_before_write_reads_its_images_as_the_host_type_and_has_no_old_record_on_a_create()
    {
        var write = new BeforeWrite<HookBooking>(Run(AlvoWriteOperation.Create, old: null, @new: Booking("Ada", 2)));

        (write.Operation, write.Old, write.New.Guest, write.New.Nights).ShouldBe((AlvoWriteOperation.Create, (HookBooking?)null, "Ada", 2L));
    }

    [Fact]
    public void A_typed_set_names_the_mapped_field_and_an_untyped_one_normalises_its_number()
    {
        var run = Run(AlvoWriteOperation.Update, Booking("Ada", 2), Booking("Ada", 3));
        var write = new BeforeWrite<HookBooking>(run);

        write.Set(x => x.Nights, 4L);
        write.Set("guest", "Grace");
        new BeforeWrite<AlvoRecord>(run).Set("nights", 5);

        run.Sets.ShouldBe(new Dictionary<string, object?> { ["nights"] = 5L, ["guest"] = "Grace" });
    }

    [Fact]
    public void A_typed_set_of_a_field_alvo_manages_is_refused_at_the_call() =>
        Should.Throw<ArgumentException>(() => new BeforeWrite<HookBooking>(Run(AlvoWriteOperation.Update, Booking("Ada", 2), Booking("Ada", 2))).Set(x => x.Id, Guid.NewGuid()));

    [Fact]
    public async Task Publish_goes_through_the_hook_s_transaction()
    {
        var transaction = new FakeHookTransaction(_caller);
        var write = new AfterWrite<AlvoRecord>(Run(AlvoWriteOperation.Create, null, null, saved: Booking("Ada", 2), transaction: transaction));

        await write.PublishAsync("bookings.audited", "bookings/1", null, Ct);

        transaction.Calls.ShouldBe(["publish bookings.audited"]);
    }

    [Fact]
    public void A_delete_context_always_has_the_row()
    {
        var write = new BeforeDelete<HookBooking>(Run(AlvoWriteOperation.Delete, Booking("Ada", 2), null));

        write.Old.Guest.ShouldBe("Ada");
    }

    [Fact]
    public void Each_kind_is_recorded_by_its_own_method()
    {
        var run = Run(AlvoWriteOperation.Create, null, Booking("Ada", 2));
        var write = new ValidateWrite<AlvoRecord>(run);

        write.Invalid("bad", "Bad.", "guest");
        write.Conflict("taken", "Taken.");
        write.Forbidden("no", "No.");

        run.Rejections.Winner()!.Kind.ShouldBe(AlvoWriteRejectionKind.Forbidden);
    }

    internal static AlvoRecord Booking(string guest, long nights) => new(new Dictionary<string, object?>
    {
        ["id"] = Guid.Parse("00000000-0000-0000-0000-0000000000b1"),
        ["guest"] = guest,
        ["nights"] = nights,
    });

    internal static HookRun Run(
        AlvoWriteOperation operation, AlvoRecord? old, AlvoRecord? @new, AlvoRecord? saved = null, IAlvoTransaction? transaction = null) =>
        new(
            "guard",
            new WriteInvocation { Entity = "bookings", Operation = operation, Caller = _caller, Now = DateTimeOffset.UnixEpoch, Old = old, New = @new, Saved = saved },
            new HashSet<string>(StringComparer.Ordinal) { "guest", "nights" },
            transaction ?? new FakeHookTransaction(_caller),
            data: null,
            CancellationToken.None);
}
```

- [ ] **Step 2: Run them to verify they fail**

Run: `dotnet test --project test/MMLib.Alvo.Tests --filter-class '*HookRejectionsTests'` and `--filter-class '*HookContextTests'`
Expected: FAIL — the namespace `MMLib.Alvo.Hooks` does not exist.

- [ ] **Step 3: Write the internals**

(`HookFailedException` is Task 1's, in Abstractions.)

`src/MMLib.Alvo/Hooks/Internal/HookRejections.cs`:

```csharp
using MMLib.Alvo.Api;
using MMLib.Alvo.Api.Internal;

namespace MMLib.Alvo.Hooks.Internal;

/// <summary>
/// What one hook recorded with <c>Invalid</c>, <c>Conflict</c> and <c>Forbidden</c> (spec 2026-10-14 §4.4.2): the winning kind by
/// precedence — 403 over 409 over 422 — with every violation of that kind. Only a declared field the descriptor never hides is
/// pointed at; anything else — a hidden field, an undeclared name, a case variant — gets the body pointer (Rulings 17, 53).
/// </summary>
/// <param name="extension">The hook's id.</param>
/// <param name="pointable">The entity's declared fields that carry no <c>hidden</c> flag, compared ordinally.</param>
internal sealed class HookRejections(string extension, IReadOnlySet<string> pointable)
{
    private readonly List<(AlvoWriteRejectionKind Kind, AlvoViolation Violation)> _recorded = [];

    internal void Record(AlvoWriteRejectionKind kind, string code, string message, string? field)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(code);
        ArgumentException.ThrowIfNullOrWhiteSpace(message);
        _recorded.Add((kind, new AlvoViolation(PointerOf(field), code, message, null)));
    }

    internal AlvoWriteRejectedException? Winner()
    {
        if (_recorded.Count == 0)
        {
            return null;
        }

        var kind = _recorded.Select(recorded => recorded.Kind).MaxBy(Precedence);

        return new AlvoWriteRejectedException(kind, extension, [.. _recorded.Where(recorded => recorded.Kind == kind).Select(recorded => recorded.Violation)]);
    }

    private string PointerOf(string? field) =>
        field is not null && pointable.Contains(field) ? PayloadViolations.PointerTo(field) : PayloadViolations.BodyPointer;

    private static int Precedence(AlvoWriteRejectionKind kind) => kind switch
    {
        AlvoWriteRejectionKind.Forbidden => 3,
        AlvoWriteRejectionKind.Conflict => 2,
        _ => 1,
    };
}
```

`src/MMLib.Alvo/Hooks/Internal/HookImage.cs`:

```csharp
using MMLib.Alvo.Data;
using MMLib.Alvo.Data.Internal;

namespace MMLib.Alvo.Hooks.Internal;

/// <summary>A record as a hook's <typeparamref name="T"/>: itself for <see cref="AlvoRecord"/>, the host type's mapping otherwise.</summary>
/// <typeparam name="T">The hook's record type.</typeparam>
internal static class HookImage<T>
    where T : class
{
    internal static T? Of(AlvoRecord? record) => record switch
    {
        null => null,
        _ when typeof(T) == typeof(AlvoRecord) => (T)(object)record,
        _ => RecordShape.Of(typeof(T)).Materialize<T>(record),
    };
}
```

`src/MMLib.Alvo/Hooks/Internal/HookRun.cs`:

```csharp
using MMLib.Alvo.Data;
using MMLib.Alvo.Data.Internal;
using MMLib.Alvo.Rules;

namespace MMLib.Alvo.Hooks.Internal;

/// <summary>
/// One invocation of one hook: what it was handed, and what it recorded — rejections, <c>Set</c>s, its calls through the
/// transaction, and a failure its transaction raised into it (budget, self-write) that catching cannot erase.
/// </summary>
internal sealed class HookRun(
    string id, WriteInvocation invocation, IReadOnlySet<string> pointable, IAlvoTransaction? transaction, IAlvoSession? data, CancellationToken token)
{
    private readonly Dictionary<string, object?> _sets = new(StringComparer.Ordinal);
    private int _operations;

    internal string Id { get; } = id;

    internal WriteInvocation Invocation { get; } = invocation;

    internal IAlvoTransaction? Transaction { get; } = transaction;

    internal IAlvoSession? Data { get; } = data;

    internal CancellationToken Token { get; } = token;

    internal HookRejections Rejections { get; } = new(id, pointable);

    internal IReadOnlyDictionary<string, object?> Sets => _sets;

    internal HookFailedException? Failure { get; private set; }

    internal int Operations => Volatile.Read(ref _operations);

    internal bool Wrote { get; private set; }

    internal void Set(string field, object? value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(field);
        _sets[field] = RecordWriter.Field(field, value);
    }

    internal void Fail(HookFailedException failure) => Failure ??= failure;

    /// <summary>Replaces the recorded <c>Set</c>s with their checked, widened form (Ruling 47).</summary>
    internal void Replace(IReadOnlyDictionary<string, object?> sets)
    {
        _sets.Clear();
        foreach (var (field, value) in sets)
        {
            _sets[field] = value;
        }
    }

    internal int Count() => Interlocked.Increment(ref _operations);

    internal void MarkWrote() => Wrote = true;
}
```

- [ ] **Step 4: Write the public types**

`src/MMLib.Alvo/Hooks/AlvoWriteRejectionKind.cs`:

```csharp
namespace MMLib.Alvo.Hooks;

/// <summary>How a write hook refused a write: what the caller is told, and with which status.</summary>
/// <remarks>Append only. When one hook records several kinds, forbidden wins over conflict, which wins over invalid.</remarks>
public enum AlvoWriteRejectionKind
{
    /// <summary>The data is not acceptable: 422 <c>validation</c>.</summary>
    Invalid = 0,

    /// <summary>The data clashes with the state of other data: 409 <c>conflict</c>.</summary>
    Conflict = 1,

    /// <summary>The caller may not make this write: 403 <c>forbidden</c>.</summary>
    Forbidden = 2,
}
```

`src/MMLib.Alvo/Hooks/AlvoWriteRejectedException.cs`:

```csharp
using MMLib.Alvo.Api;
using MMLib.Alvo.Data;
using MMLib.Alvo.Rules;

namespace MMLib.Alvo.Hooks;

/// <summary>
/// A host write hook refused the write: nothing was written and no event was emitted. Over HTTP it is answered 422, 409 or
/// 403 by its <see cref="Kind"/>, with every violation of that kind; in a batch it is the refused row's refusal.
/// </summary>
public sealed class AlvoWriteRejectedException : Exception, IRowRefusal
{
    /// <summary>Initializes a new instance of the <see cref="AlvoWriteRejectedException"/> class.</summary>
    public AlvoWriteRejectedException()
        : this("A write hook refused the write.")
    {
    }

    /// <summary>Initializes a new instance of the <see cref="AlvoWriteRejectedException"/> class.</summary>
    /// <param name="message">The message.</param>
    public AlvoWriteRejectedException(string message)
        : base(message)
    {
        ExtensionId = string.Empty;
        Violations = [];
    }

    /// <summary>Initializes a new instance of the <see cref="AlvoWriteRejectedException"/> class.</summary>
    /// <param name="message">The message.</param>
    /// <param name="innerException">The cause.</param>
    public AlvoWriteRejectedException(string message, Exception innerException)
        : base(message, innerException)
    {
        ExtensionId = string.Empty;
        Violations = [];
    }

    internal AlvoWriteRejectedException(AlvoWriteRejectionKind kind, string extensionId, IReadOnlyList<AlvoViolation> violations)
        : base($"The write hook '{extensionId}' refused the write: {string.Join(" ", violations.Select(violation => violation.Message).Distinct(StringComparer.Ordinal))}")
    {
        Kind = kind;
        ExtensionId = extensionId;
        Violations = violations;
    }

    /// <summary>Gets how the hook refused the write.</summary>
    public AlvoWriteRejectionKind Kind { get; }

    /// <summary>Gets the first violation's code.</summary>
    public string Code => Violations.Count > 0 ? Violations[0].Code : string.Empty;

    /// <summary>Gets the first violation's JSON pointer: the field it names, or empty for the whole body or a hidden field.</summary>
    public string Pointer => Violations.Count > 0 ? Violations[0].Pointer : string.Empty;

    /// <summary>Gets the id of the hook that refused.</summary>
    public string ExtensionId { get; }

    /// <summary>Gets every violation of <see cref="Kind"/> the hook recorded, in the order it recorded them.</summary>
    public IReadOnlyList<AlvoViolation> Violations { get; }

    /// <inheritdoc/>
    AlvoRowRefusal IRowRefusal.At(int index) =>
        new(index, Code, string.Join(" ", Violations.Select(violation => violation.Message).Distinct(StringComparer.Ordinal)), null);
}
```

`src/MMLib.Alvo/Hooks/WriteCondition{T}.cs`:

```csharp
using MMLib.Alvo.Events;
using System.Linq.Expressions;

namespace MMLib.Alvo.Hooks;

/// <summary>
/// What a typed write hook's <c>when</c> lambda reads. Alvo translates the lambda into a CEL condition when the hook is
/// registered and never runs it, so every member throws if called.
/// </summary>
/// <typeparam name="T">The record type the hook is registered for.</typeparam>
/// <remarks>
/// <see cref="New"/> is the row the write would store (after-write: the stored row), <see cref="Old"/> the row before it —
/// absent on a create, so a comparison with it is false there — and <see cref="Changed"/> is false on a create. A delete hook
/// reads <see cref="Old"/> only. The constructs a condition may use are an <c>EventCondition</c>'s.
/// </remarks>
public sealed class WriteCondition<T>
    where T : class
{
    private const string TranslationOnly = "A WriteCondition is read by Alvo's translator and never run: pass the lambda to when:.";

    private WriteCondition()
    {
    }

    /// <summary>Gets the row the write would store (<c>new.</c>).</summary>
    public T New => throw new NotSupportedException(TranslationOnly);

    /// <summary>Gets the row before the write (<c>old.</c>).</summary>
    public T Old => throw new NotSupportedException(TranslationOnly);

    /// <summary>Gets who makes the write.</summary>
    public AlvoConditionCaller Caller => throw new NotSupportedException(TranslationOnly);

    /// <summary>Whether any of <paramref name="fields"/> changes in the write (<c>changed(field)</c>); false on a create.</summary>
    /// <param name="fields">Mapped properties of the record, such as <c>x =&gt; x.Status</c>.</param>
    /// <returns>Never returns: the call is translated.</returns>
    public bool Changed(params Expression<Func<T, object?>>[] fields) => throw new NotSupportedException(TranslationOnly);
}
```

The ten interfaces — one per file, each with full XML docs; the generic one constrains `where T : class`:

```csharp
namespace MMLib.Alvo.Hooks;

/// <summary>
/// A validate hook over typed records: runs before a top-level write's transaction opens — never for a write inside a
/// transaction unit — and may call the network; it can only refuse.
/// </summary>
/// <typeparam name="T">The record type it is registered for.</typeparam>
public interface IValidateWriteHook<T>
    where T : class
{
    /// <summary>Judges the write; record a refusal on <paramref name="write"/> to stop it.</summary>
    /// <param name="write">The write.</param>
    /// <param name="cancellationToken">Cancelled at the hook's timeout.</param>
    /// <returns>A task that completes when the hook has judged.</returns>
    ValueTask ValidateAsync(ValidateWrite<T> write, CancellationToken cancellationToken);
}
```

`IValidateWriteHook` is the same over `ValidateWrite<AlvoRecord>` with no type parameter ("A validate hook over untyped records …"); `IBeforeWriteHook(<T>)` declares `ValueTask BeforeWriteAsync(BeforeWrite<T> write, CancellationToken cancellationToken)` ("runs inside a create, update or replace's transaction before the row is written; may refuse, set fields, write other rows through the write's transaction and publish; no network"); `IAfterWriteHook(<T>)` — `AfterWriteAsync(AfterWrite<T> write, …)` ("runs inside the transaction after the row and its rollups; may refuse — rolling the write back — write other rows and publish; never its own row"); `IBeforeDeleteHook(<T>)` — `BeforeDeleteAsync(BeforeDelete<T> delete, …)`; `IAfterDeleteHook(<T>)` — `AfterDeleteAsync(AfterDelete<T> delete, …)`.

`src/MMLib.Alvo/Hooks/ValidateWrite{T}.cs`:

```csharp
using MMLib.Alvo.Data;
using MMLib.Alvo.Hooks.Internal;

namespace MMLib.Alvo.Hooks;

/// <summary>A write as a validate hook sees it: before any transaction, with a data session as the caller.</summary>
/// <typeparam name="T">The record type.</typeparam>
public sealed class ValidateWrite<T>
    where T : class
{
    private readonly HookRun _run;

    internal ValidateWrite(HookRun run)
    {
        _run = run;
        Old = HookImage<T>.Of(run.Invocation.Old);
        New = HookImage<T>.Of(run.Invocation.New)!;
    }

    /// <summary>Gets the write: a create, an update or a replace.</summary>
    public AlvoWriteOperation Operation => _run.Invocation.Operation;

    /// <summary>Gets the row before the write, read with no lock — another write may move it before this one runs; null on a create.</summary>
    public T? Old { get; }

    /// <summary>Gets the row the write would store; on a create it has no id yet.</summary>
    public T New { get; }

    /// <summary>Gets data access as the caller, one hop deeper in the causation chain; each call commits on its own.</summary>
    public IAlvoSession Data => _run.Data!;

    /// <summary>Gets the caller.</summary>
    public AlvoContext Caller => _run.Invocation.Caller;

    /// <summary>Gets the instant the write is stamped with.</summary>
    public DateTimeOffset Now => _run.Invocation.Now;

    /// <summary>Refuses the write as invalid data (422).</summary>
    /// <param name="code">A stable code a client can branch on.</param>
    /// <param name="message">What is wrong, for a person; never name a hidden field in it.</param>
    /// <param name="field">The field at fault, or <see langword="null"/>; a hidden field is never pointed at.</param>
    public void Invalid(string code, string message, string? field = null) => _run.Rejections.Record(AlvoWriteRejectionKind.Invalid, code, message, field);

    /// <summary>Refuses the write as a clash with other data (409).</summary>
    /// <param name="code">A stable code.</param>
    /// <param name="message">What clashes.</param>
    /// <param name="field">The field at fault, or <see langword="null"/>.</param>
    public void Conflict(string code, string message, string? field = null) => _run.Rejections.Record(AlvoWriteRejectionKind.Conflict, code, message, field);

    /// <summary>Refuses the write as not permitted for this caller (403).</summary>
    /// <param name="code">A stable code.</param>
    /// <param name="message">Why.</param>
    public void Forbidden(string code, string message) => _run.Rejections.Record(AlvoWriteRejectionKind.Forbidden, code, message, null);
}
```

`BeforeWrite{T}.cs` — the same `Operation`, `Old`, `New`, `Caller`, `Now`, `Invalid`, `Conflict`, `Forbidden` (refusing also discards this hook's `Set`s), plus:

```csharp
    /// <summary>Gets the write's transaction, as the caller, one hop deeper in the causation chain: every call counts toward the hook's budget.</summary>
    public IAlvoTransaction Transaction => _run.Transaction!;

    /// <summary>Sets a field of the row to be stored; the row is then judged by the entity's rules and the field's facets again.</summary>
    /// <param name="field">The field's name.</param>
    /// <param name="value">The value, as the field holds it.</param>
    public void Set(string field, object? value) => _run.Set(field, value);

    /// <summary>Sets a mapped property's field of the row to be stored; refused at the call for a field Alvo manages.</summary>
    /// <typeparam name="TValue">The property's type.</typeparam>
    /// <param name="field">The property, such as <c>x =&gt; x.Status</c>.</param>
    /// <param name="value">The value.</param>
    public void Set<TValue>(Expression<Func<T, TValue>> field, TValue value)
    {
        foreach (var (name, stored) in new TypedPatch<T>().Set(field, value).Values)
        {
            _run.Set(name, stored);
        }
    }

    /// <summary>Publishes a custom event that commits with the write, or not at all; one call of the budget.</summary>
    /// <param name="type">The event's name.</param>
    /// <param name="subject">What it is about.</param>
    /// <param name="data">Its payload, or <see langword="null"/>.</param>
    /// <param name="cancellationToken">A token to cancel the publish.</param>
    /// <returns>A task that completes when the event is written into the transaction.</returns>
    public Task PublishAsync(string type, string subject, IReadOnlyDictionary<string, object?>? data, CancellationToken cancellationToken = default) =>
        Transaction.PublishAsync(type, subject, data, cancellationToken);
```

`AfterWrite{T}.cs` — `Operation`, `Old` (`T?`), `Saved` (`T`, from `run.Invocation.Saved` — "the row as stored: its id, defaults and rollups"), `Transaction`, `Caller`, `Now`, the three refusals ("refusing rolls the write back"), `PublishAsync`; no `Set` (the row is written; an after-write never writes its own row). `BeforeDelete{T}.cs` and `AfterDelete{T}.cs` — `Old` (`T`, non-null: `HookImage<T>.Of(run.Invocation.Old)!`), `Transaction`, `Caller`, `Now`, the three refusals, `PublishAsync`; no `Operation` (Ruling 36).

`src/MMLib.Alvo/Extensions/AlvoHookOptions.cs`:

```csharp
namespace MMLib.Alvo.Extensions;

/// <summary>How one write hook runs.</summary>
public sealed class AlvoHookOptions
{
    private int _order;

    /// <summary>Gets or sets what the hook does, in a sentence, for the dashboard and the docs.</summary>
    public string? Description { get; set; }

    /// <summary>
    /// Gets or sets where the hook runs among the host's hooks of the same entity and stage: lower first; at one order an
    /// entity's own hooks before every-entity ones, then registration order. Declarative hooks always run first.
    /// </summary>
    public int Order
    {
        get => _order;
        set
        {
            _order = value;
            OrderStated = true;
        }
    }

    /// <summary>
    /// Gets or sets how long one run may take before its calls are cancelled and the write fails; <see langword="null"/> for
    /// the stage's default — 250 milliseconds inside the transaction, 5 seconds for a validate hook. At most 30 seconds inside
    /// the transaction and 60 seconds for validate; a longer or non-positive timeout refuses the start.
    /// </summary>
    public TimeSpan? Timeout { get; set; }

    /// <summary>
    /// Gets or sets how many calls one run may make through the write's transaction, reads included; 50 unless set, 1 to 1000.
    /// The next call throws <c>AlvoBudgetExceededException</c> and fails the write.
    /// </summary>
    public int MaxOperations { get; set; } = DefaultMaxOperations;

    internal bool OrderStated { get; private set; }

    internal const int DefaultMaxOperations = 50;

    internal const int MaxMaxOperations = 1000;

    internal static TimeSpan DefaultTimeout { get; } = TimeSpan.FromMilliseconds(250);

    internal static TimeSpan DefaultValidateTimeout { get; } = TimeSpan.FromSeconds(5);

    internal static TimeSpan MaxTimeout { get; } = TimeSpan.FromSeconds(30);

    internal static TimeSpan MaxValidateTimeout { get; } = TimeSpan.FromSeconds(60);
}
```

- [ ] **Step 5: Run the tests and accept the core baseline for these types**

Run: `dotnet test --project test/MMLib.Alvo.Tests --filter-class '*HookRejectionsTests'`, `--filter-class '*HookContextTests'` → PASS. `--filter-class '*PublicApiApprovalTests'`: the diff adds exactly the ten interfaces, five contexts, `WriteCondition<T>`, `AlvoWriteRejectedException`, `AlvoWriteRejectionKind`, `AlvoHookOptions`. Accept; dispatch `alvo-snapshot-judge`.

- [ ] **Step 6: Commit**

```bash
dotnet format whitespace MMLib.Alvo.slnx --include $(git ls-files --others --exclude-standard src/MMLib.Alvo/Hooks src/MMLib.Alvo/Extensions/AlvoHookOptions.cs test/MMLib.Alvo.Tests/Hooks | tr '\n' ' ')
scripts/test-ring0
git add src/MMLib.Alvo/Hooks src/MMLib.Alvo/Extensions/AlvoHookOptions.cs test/MMLib.Alvo.Tests/Hooks test/MMLib.Alvo.Tests/PublicApi.MMLib.Alvo.verified.txt
git commit -m "feat(hooks): the public write-hook surface — interfaces, contexts, rejection

Claude-Session: https://claude.ai/code/session_01NJAtafM29iddLwfhb5gnmB"
```

---

## Task 6: Registration — the builders' hook verbs, class and delegate invokers, the typed condition's translation

**Files:**
- Create: `src/MMLib.Alvo/Hooks/Internal/WriteHookStage.cs`, `src/MMLib.Alvo/Hooks/Internal/WriteHookRegistration.cs`, `src/MMLib.Alvo/Hooks/Internal/HookInvoker.cs`, `src/MMLib.Alvo/Hooks/Internal/HookContexts.cs`, `src/MMLib.Alvo/Hooks/Internal/WriteConditionTranslator.cs`, `src/MMLib.Alvo/Extensions/AlvoEntityBuilder.WriteHooks.cs`, `src/MMLib.Alvo/Extensions/AlvoEntityBuilder{T}.WriteHooks.cs`
- Modify: `src/MMLib.Alvo/Extensions/AlvoEntityBuilder.cs` and `AlvoEntityBuilder{T}.cs` (`public sealed partial class`), `src/MMLib.Alvo/Extensions/Internal/ExtensionRegistry.cs`, `src/MMLib.Alvo/Extensions/Internal/HandlerInvoker.cs` (`ClassUnresolvable` becomes `internal static`)
- Test: `test/MMLib.Alvo.Tests/Hooks/WriteHookRegistrationTests.cs`, `test/MMLib.Alvo.Tests/Hooks/WriteConditionTranslatorTests.cs`

**Interfaces:**
- Consumes: Task 5's contexts, interfaces, `AlvoHookOptions`, `HookRun`; HX-05's `ConditionTranslator.Translate<T>(Expression<Func<EventCondition<T>, bool>>, string, HandlerVerb)`; `RecordShape`.
- Produces: `WriteHookStage { Validate, BeforeWrite, AfterWrite, BeforeDelete, AfterDelete }`; `WriteHookRegistration(string Id, int Sequence, IReadOnlyList<string> Entities, WriteHookStage Stage, string? When, HookSettings Settings, HookInvoker Invoker, RecordShape? Typed) : ExtensionRegistration` with `IsEveryEntity`, `Covers(string)`, `RunsOn(AlvoWriteOperation)`, `InTransaction`; `HookSettings(string? Description, int Order, bool OrderStated, TimeSpan Timeout, int MaxOperations)`; `HookInvoker.FromClass<THook, TContext>(Func<HookRun, TContext>, Func<THook, TContext, CancellationToken, ValueTask>)`, `HookInvoker.FromDelegate<TContext>(Delegate, Func<HookRun, TContext>)`, `.InvokeAsync(IServiceProvider, HookRun, CancellationToken)`, `.Unresolvable(IServiceProviderIsService)`, `.Dependencies` (`IReadOnlyList<(string Name, Type Type)>`), `.TypeName`; `ExtensionRegistry.WriteHooks`; `WriteConditionTranslator.Translate<T>(Expression<Func<WriteCondition<T>, bool>>, string id)`.

- [ ] **Step 1: Write the failing tests**

`test/MMLib.Alvo.Tests/Hooks/WriteHookRegistrationTests.cs`:

```csharp
using MMLib.Alvo.Data;
using MMLib.Alvo.Hooks;
using MMLib.Alvo.Hooks.Internal;
using MMLib.Alvo.Tests.Extensions;

namespace MMLib.Alvo.Tests.Hooks;

public sealed class VatIdIsRegistered : IValidateWriteHook
{
    public ValueTask ValidateAsync(ValidateWrite<AlvoRecord> write, CancellationToken cancellationToken) => ValueTask.CompletedTask;
}

public sealed class TypedGuard : IBeforeWriteHook<HookBooking>
{
    public ValueTask BeforeWriteAsync(BeforeWrite<HookBooking> write, CancellationToken cancellationToken) => ValueTask.CompletedTask;
}

public abstract class AbstractGuard : IBeforeWriteHook
{
    public abstract ValueTask BeforeWriteAsync(BeforeWrite<AlvoRecord> write, CancellationToken cancellationToken);
}

/// <summary>What registering a write hook records (spec 2026-10-14 §4.2, §4.4): stage, entities, condition, the stage's defaults.</summary>
public sealed class WriteHookRegistrationTests
{
    [Fact]
    public void An_untyped_validate_hook_records_its_stage_entity_condition_and_the_validate_defaults()
    {
        var hook = HandlerCatalog.Registry(alvo => alvo.Entity("bookings", b => b.Validate<VatIdIsRegistered>("vat-in-vies", when: "changed(vat_id)")))
            .WriteHooks.ShouldHaveSingleItem();

        (hook.Id, hook.Stage, hook.When, hook.Settings.Timeout, hook.Settings.MaxOperations, hook.InTransaction)
            .ShouldBe(("vat-in-vies", WriteHookStage.Validate, "changed(vat_id)", TimeSpan.FromSeconds(5), 50, false));
        hook.Entities.ShouldBe(["bookings"]);
        hook.Invoker.TypeName.ShouldBe(typeof(VatIdIsRegistered).FullName);
    }

    [Fact]
    public void An_in_transaction_hook_defaults_to_a_quarter_second_and_its_options_are_kept()
    {
        var hook = HandlerCatalog.Registry(alvo => alvo.Entities(["bookings", "notes"], e => e.AfterWrite(
            "audit", (AfterWrite<AlvoRecord> _) => ValueTask.CompletedTask,
            configure: o => { o.Order = 2; o.MaxOperations = 7; o.Description = "Keeps an audit row."; }))).WriteHooks.ShouldHaveSingleItem();

        (hook.Stage, hook.Settings.Timeout, hook.Settings.Order, hook.Settings.OrderStated, hook.Settings.MaxOperations, hook.Settings.Description)
            .ShouldBe((WriteHookStage.AfterWrite, TimeSpan.FromMilliseconds(250), 2, true, 7, "Keeps an audit row."));
        hook.Entities.ShouldBe(["bookings", "notes"]);
        hook.Invoker.TypeName.ShouldBeNull();
    }

    [Fact]
    public void A_typed_condition_is_translated_to_cel_when_the_hook_is_registered()
    {
        var hook = HandlerCatalog.Registry(alvo => alvo.Entity<HookBooking>(b => b.BeforeWrite<TypedGuard>(
            "nights-guard", when: w => w.Changed(x => x.Nights) && w.New.Nights > 14))).WriteHooks.ShouldHaveSingleItem();

        hook.When.ShouldBe("changed(nights) && new.nights > 14");
        hook.Typed!.Type.ShouldBe(typeof(HookBooking));
    }

    [Theory]
    [InlineData(AlvoWriteOperation.Create, true, false)]
    [InlineData(AlvoWriteOperation.Replace, true, false)]
    [InlineData(AlvoWriteOperation.Delete, false, true)]
    public void A_write_stage_runs_on_writes_and_a_delete_stage_on_deletes(AlvoWriteOperation operation, bool write, bool delete)
    {
        var registry = HandlerCatalog.Registry(alvo => alvo.Entity("*", every => every
            .BeforeWrite("w", (BeforeWrite<AlvoRecord> _) => ValueTask.CompletedTask)
            .BeforeDelete("d", (BeforeDelete<AlvoRecord> _) => ValueTask.CompletedTask)));

        registry.WriteHooks.Single(hook => hook.Id == "w").RunsOn(operation).ShouldBe(write);
        registry.WriteHooks.Single(hook => hook.Id == "d").RunsOn(operation).ShouldBe(delete);
        registry.WriteHooks.ShouldAllBe(hook => hook.IsEveryEntity && hook.Covers("anything"));
    }

    [Fact]
    public void A_delegate_whose_result_alvo_would_discard_is_refused_at_the_call() =>
        Should.Throw<ArgumentException>(() => HandlerCatalog.Registry(alvo => alvo.Entity("bookings", b => b.BeforeWrite(
            "bad", (BeforeWrite<AlvoRecord> _) => 42)))).ParamName.ShouldBe("hook");

    [Fact]
    public void A_delegate_asking_for_another_stage_s_context_is_refused_at_the_call() =>
        Should.Throw<ArgumentException>(() => HandlerCatalog.Registry(alvo => alvo.Entity("bookings", b => b.AfterWrite(
            "confused", (BeforeWrite<AlvoRecord> _) => ValueTask.CompletedTask)))).Message.ShouldContain("AfterWrite<AlvoRecord>");

    [Fact]
    public void An_abstract_hook_class_is_refused_at_the_call() =>
        Should.Throw<ArgumentException>(() => HandlerCatalog.Registry(alvo => alvo.Entity("bookings", b => b.BeforeWrite<AbstractGuard>("abstract"))));
}
```

`test/MMLib.Alvo.Tests/Hooks/WriteConditionTranslatorTests.cs`:

```csharp
using MMLib.Alvo.Data;
using MMLib.Alvo.Events;
using MMLib.Alvo.Extensions.Internal;
using MMLib.Alvo.Hooks;
using MMLib.Alvo.Hooks.Internal;

namespace MMLib.Alvo.Tests.Hooks;

/// <summary>A typed write condition translates to the CEL a typed event condition does (Ruling 25, HX-08 Ruling 2's precedent).</summary>
public sealed class WriteConditionTranslatorTests
{
    [Fact]
    public void New_old_changed_and_the_caller_translate_as_an_event_condition_does()
    {
        WriteConditionTranslator.Translate<HookBooking>(w => w.Changed(x => x.Nights) && w.New.Nights > w.Old.Nights && w.Caller.HasRole("staff"), "guard")
            .ShouldBe(ConditionTranslator.Translate<HookBooking>(e => e.Changed(x => x.Nights) && e.New.Nights > e.Old.Nights && e.Caller.HasRole("staff"), "guard", HandlerVerb.Updated));
    }

    [Fact]
    public void An_untranslatable_condition_is_refused_naming_the_hook()
    {
        var refusal = Should.Throw<AlvoTranslationException>(() => WriteConditionTranslator.Translate<HookBooking>(w => w.New.Guest.GetHashCode() == 1, "guard"));

        refusal.Message.ShouldContain("'guard'");
    }
}
```

- [ ] **Step 2: Run them to verify they fail**

Run: `dotnet test --project test/MMLib.Alvo.Tests --filter-class '*WriteHookRegistrationTests'` and `--filter-class '*WriteConditionTranslatorTests'`
Expected: FAIL — no `Validate`/`BeforeWrite`/… verbs, no `WriteHooks`.

- [ ] **Step 3: Write the registration internals**

`src/MMLib.Alvo/Hooks/Internal/WriteHookStage.cs`:

```csharp
namespace MMLib.Alvo.Hooks.Internal;

/// <summary>When, in a write, a host hook runs.</summary>
internal enum WriteHookStage
{
    Validate,
    BeforeWrite,
    AfterWrite,
    BeforeDelete,
    AfterDelete,
}
```

`src/MMLib.Alvo/Hooks/Internal/WriteHookRegistration.cs`:

```csharp
using Microsoft.Extensions.DependencyInjection;
using MMLib.Alvo.Data;
using MMLib.Alvo.Data.Internal;
using MMLib.Alvo.Extensions;
using MMLib.Alvo.Extensions.Internal;

namespace MMLib.Alvo.Hooks.Internal;

/// <summary>A hook's options, frozen at registration, with the stage's default timeout filled in.</summary>
internal sealed record HookSettings(string? Description, int Order, bool OrderStated, TimeSpan Timeout, int MaxOperations)
{
    internal static HookSettings From(AlvoHookOptions options, WriteHookStage stage) => new(
        options.Description,
        options.Order,
        options.OrderStated,
        options.Timeout ?? (stage == WriteHookStage.Validate ? AlvoHookOptions.DefaultValidateTimeout : AlvoHookOptions.DefaultTimeout),
        options.MaxOperations);
}

/// <summary>A host write hook: one id, one stage, bound to the named entities or to every entity (<c>"*"</c>).</summary>
internal sealed record WriteHookRegistration(
    string Id, int Sequence, IReadOnlyList<string> Entities, WriteHookStage Stage, string? When, HookSettings Settings, HookInvoker Invoker, RecordShape? Typed)
    : ExtensionRegistration(Id, Sequence)
{
    internal bool IsEveryEntity => Entities is [EntityEvents.AllEntities];

    internal bool InTransaction => Stage != WriteHookStage.Validate;

    internal bool Covers(string entity) => IsEveryEntity || Entities.Contains(entity, StringComparer.Ordinal);

    internal bool RunsOn(AlvoWriteOperation operation) => Stage is WriteHookStage.BeforeDelete or WriteHookStage.AfterDelete
        ? operation == AlvoWriteOperation.Delete
        : operation != AlvoWriteOperation.Delete;

    internal static void Add(
        IServiceCollection services, string id, IReadOnlyList<string> entities, WriteHookStage stage, string? when,
        Action<AlvoHookOptions>? configure, HookInvoker invoker, RecordShape? typed = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        if (when is not null)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(when);
        }

        var options = new AlvoHookOptions();
        configure?.Invoke(options);
        ExtensionRegistrations.Add(services, sequence => new WriteHookRegistration(id, sequence, entities, stage, when, HookSettings.From(options, stage), invoker, typed));
    }
}
```

`src/MMLib.Alvo/Hooks/Internal/HookContexts.cs`:

```csharp
namespace MMLib.Alvo.Hooks.Internal;

/// <summary>Builds one run's context, typed or untyped; each factory is captured at registration, where <typeparamref name="T"/> is known.</summary>
internal static class HookContexts<T>
    where T : class
{
    internal static ValidateWrite<T> Validate(HookRun run) => new(run);

    internal static BeforeWrite<T> Before(HookRun run) => new(run);

    internal static AfterWrite<T> After(HookRun run) => new(run);

    internal static BeforeDelete<T> BeforeDelete(HookRun run) => new(run);

    internal static AfterDelete<T> AfterDelete(HookRun run) => new(run);
}
```

`src/MMLib.Alvo/Hooks/Internal/HookInvoker.cs`:

```csharp
using Microsoft.Extensions.DependencyInjection;
using MMLib.Alvo.Extensions.Internal;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.ExceptionServices;

namespace MMLib.Alvo.Hooks.Internal;

/// <summary>
/// How one hook is called (Ruling 21): a class built per run from the hook's scope — or resolved, when registered — or a
/// delegate whose parameters bind to the context, the token or a service. Its direct dependencies are kept for the boot ban.
/// </summary>
internal sealed class HookInvoker
{
    private readonly Func<IServiceProvider, HookRun, CancellationToken, ValueTask> _invoke;
    private readonly Func<IServiceProviderIsService, IReadOnlyList<Type>> _unresolvable;

    private HookInvoker(
        string? typeName,
        IReadOnlyList<(string Name, Type Type)> dependencies,
        Func<IServiceProvider, HookRun, CancellationToken, ValueTask> invoke,
        Func<IServiceProviderIsService, IReadOnlyList<Type>> unresolvable)
    {
        TypeName = typeName;
        Dependencies = dependencies;
        _invoke = invoke;
        _unresolvable = unresolvable;
    }

    /// <summary>Gets the hook class's full name; <see langword="null"/> for a delegate.</summary>
    internal string? TypeName { get; }

    /// <summary>Gets every constructor parameter of a class hook, or every non-supplied parameter of a delegate.</summary>
    internal IReadOnlyList<(string Name, Type Type)> Dependencies { get; }

    internal ValueTask InvokeAsync(IServiceProvider services, HookRun run, CancellationToken cancellationToken) => _invoke(services, run, cancellationToken);

    internal IReadOnlyList<Type> Unresolvable(IServiceProviderIsService services) => _unresolvable(services);

    internal static HookInvoker FromClass<THook, TContext>(Func<HookRun, TContext> context, Func<THook, TContext, CancellationToken, ValueTask> call)
        where THook : class
    {
        if (typeof(THook).IsAbstract)
        {
            throw new ArgumentException($"{typeof(THook).Name} is abstract, so Alvo cannot build it for a write; register a concrete hook class.", nameof(THook));
        }

        return new(
            typeof(THook).FullName,
            [.. typeof(THook).GetConstructors().SelectMany(constructor => constructor.GetParameters()).Select(parameter => (parameter.Name ?? "?", parameter.ParameterType)).Distinct()],
            (services, run, ct) => InvokeClassAsync(services, context(run), call, ct),
            isService => HandlerInvoker.ClassUnresolvable(typeof(THook), isService));
    }

    internal static HookInvoker FromDelegate<TContext>(Delegate hook, Func<HookRun, TContext> context)
    {
        ArgumentNullException.ThrowIfNull(hook);
        EnsureReturnsAwaitable(hook);
        EnsureOnlyItsOwnContext(hook, typeof(TContext));
        var parameters = hook.Method.GetParameters();
        var dependencies = parameters.Where(parameter => !IsSupplied(parameter.ParameterType, typeof(TContext))).ToList();

        return new(
            typeName: null,
            [.. dependencies.Select(parameter => (parameter.Name ?? "?", parameter.ParameterType))],
            (services, run, ct) => InvokeDelegateAsync(hook, parameters, services, context(run)!, ct),
            isService => [.. dependencies.Where(parameter => !parameter.HasDefaultValue).Select(parameter => parameter.ParameterType).Where(type => !isService.IsService(type))]);
    }

    private static async ValueTask InvokeClassAsync<THook, TContext>(
        IServiceProvider services, TContext context, Func<THook, TContext, CancellationToken, ValueTask> call, CancellationToken ct)
        where THook : class
    {
        var registered = services.GetService<THook>();
        var hook = registered ?? ActivatorUtilities.CreateInstance<THook>(services);
        try
        {
            await call(hook, context, ct).ConfigureAwait(false);
        }
        finally
        {
            if (registered is null)
            {
                await DisposeAsync(hook).ConfigureAwait(false);
            }
        }
    }

    private static async ValueTask InvokeDelegateAsync(Delegate hook, ParameterInfo[] parameters, IServiceProvider services, object context, CancellationToken ct)
    {
        var arguments = parameters.Select(parameter => Argument(parameter, services, context, ct)).ToArray();
        switch (Invoke(hook, arguments))
        {
            case Task task:
                await task.ConfigureAwait(false);
                break;
            case ValueTask valueTask:
                await valueTask.ConfigureAwait(false);
                break;
        }
    }

    private static object? Argument(ParameterInfo parameter, IServiceProvider services, object context, CancellationToken ct) =>
        parameter.ParameterType == typeof(CancellationToken) ? ct
        : parameter.ParameterType.IsInstanceOfType(context) ? context
        : parameter.HasDefaultValue ? services.GetService(parameter.ParameterType) ?? parameter.DefaultValue
        : services.GetRequiredService(parameter.ParameterType);

    private static object? Invoke(Delegate hook, object?[] arguments)
    {
        try
        {
            return hook.DynamicInvoke(arguments);
        }
        catch (TargetInvocationException failure) when (failure.InnerException is not null)
        {
            ExceptionDispatchInfo.Capture(failure.InnerException).Throw();
            throw;
        }
    }

    private static void EnsureReturnsAwaitable(Delegate hook)
    {
        var method = hook.Method;
        if (method.ReturnType == typeof(void) && method.IsDefined(typeof(AsyncStateMachineAttribute), inherit: false))
        {
            throw new ArgumentException($"Hook '{method.Name}' is async void: Alvo could not wait for it, so the write would go on before it ends. Make it return ValueTask.", nameof(hook));
        }

        if (method.ReturnType != typeof(void) && method.ReturnType != typeof(Task) && method.ReturnType != typeof(ValueTask))
        {
            throw new ArgumentException($"A hook delegate returns ValueTask, Task or nothing; this one returns {method.ReturnType.Name}, whose result Alvo would discard.", nameof(hook));
        }
    }

    private static void EnsureOnlyItsOwnContext(Delegate hook, Type context)
    {
        var other = hook.Method.GetParameters().FirstOrDefault(parameter => IsHookContext(parameter.ParameterType) && parameter.ParameterType != context);
        if (other is not null)
        {
            throw new ArgumentException(
                $"This registration hands its delegate {Display(context)}; parameter '{other.Name}' asks for {Display(other.ParameterType)}, which it would never be given.",
                nameof(hook));
        }
    }

    private static bool IsSupplied(Type type, Type context) => type == context || type == typeof(CancellationToken);

    private static bool IsHookContext(Type type) => type.IsGenericType && type.GetGenericTypeDefinition() is var definition
        && (definition == typeof(ValidateWrite<>) || definition == typeof(BeforeWrite<>) || definition == typeof(AfterWrite<>)
            || definition == typeof(BeforeDelete<>) || definition == typeof(AfterDelete<>));

    private static string Display(Type type) =>
        type.IsGenericType ? $"{type.Name[..type.Name.IndexOf('`', StringComparison.Ordinal)]}<{string.Join(", ", type.GetGenericArguments().Select(Display))}>" : type.Name;

    private static ValueTask DisposeAsync(object hook)
    {
        switch (hook)
        {
            case IAsyncDisposable asyncDisposable:
                return asyncDisposable.DisposeAsync();
            case IDisposable disposable:
                disposable.Dispose();
                return ValueTask.CompletedTask;
            default:
                return ValueTask.CompletedTask;
        }
    }
}
```

`src/MMLib.Alvo/Hooks/Internal/WriteConditionTranslator.cs`:

```csharp
using MMLib.Alvo.Events;
using MMLib.Alvo.Extensions.Internal;
using System.Linq.Expressions;
using System.Reflection;

namespace MMLib.Alvo.Hooks.Internal;

/// <summary>
/// Translates a typed write hook's <c>when</c> by rewriting it into an <see cref="EventCondition{T}"/> lambda — <c>w.New</c> to
/// <c>e.New</c>, <c>w.Old</c>, <c>w.Caller</c>, <c>w.Changed(…)</c> likewise — and handing it to the one translator (Ruling 25).
/// The <c>Updated</c> verb admits both images; a create's absent <c>old.</c> compares false at run time.
/// </summary>
internal static class WriteConditionTranslator
{
    internal static string Translate<T>(Expression<Func<WriteCondition<T>, bool>> when, string id)
        where T : class
    {
        ArgumentNullException.ThrowIfNull(when);
        var condition = Expression.Parameter(typeof(EventCondition<T>), "e");
        var changed = typeof(EventCondition<T>).GetMethod(nameof(EventCondition<T>.Changed))!;
        var body = new Rebinder(when.Parameters[0], condition, changed).Visit(when.Body);

        return ConditionTranslator.Translate(Expression.Lambda<Func<EventCondition<T>, bool>>(body, condition), id, HandlerVerb.Updated);
    }

    private sealed class Rebinder(ParameterExpression from, ParameterExpression to, MethodInfo changed) : ExpressionVisitor
    {
        protected override Expression VisitMember(MemberExpression node) =>
            node.Expression == from ? Expression.Property(to, node.Member.Name) : base.VisitMember(node);

        protected override Expression VisitMethodCall(MethodCallExpression node) =>
            node.Object == from && node.Method.Name == changed.Name
                ? Expression.Call(to, changed, node.Arguments.Select(Visit)!)
                : base.VisitMethodCall(node);
    }
}
```

`ExtensionRegistry.cs` — the constructor adds `WriteHooks = [.. _registered.OfType<WriteHookRegistration>()];`, and:

```csharp
    internal IReadOnlyList<WriteHookRegistration> WriteHooks { get; }

    internal bool IsEmpty => Handlers.Count == 0 && Commands.Count == 0 && WriteHooks.Count == 0;
```

- [ ] **Step 4: Write the builder verbs**

`AlvoEntityBuilder.cs` and `AlvoEntityBuilder{T}.cs` become `public sealed partial class`. `src/MMLib.Alvo/Extensions/AlvoEntityBuilder.WriteHooks.cs`:

```csharp
using MMLib.Alvo.Data;
using MMLib.Alvo.Hooks;
using MMLib.Alvo.Hooks.Internal;

namespace MMLib.Alvo.Extensions;

/// <summary>The write hooks of the named entities (spec 2026-10-14 §4.4).</summary>
/// <remarks>
/// A write hook's <c>when</c> is a CEL condition over <c>new.</c> (the row the write would store; after-write: the stored row),
/// <c>old.</c> (absent on a create), <c>changed(field)</c>, <c>@user</c> and <c>@tenant</c>; a delete hook reads <c>old.</c> only,
/// and an every-entity (<c>"*"</c>) hook tests only <c>@user</c> and <c>@tenant</c>. Validate hooks run before a top-level
/// write's transaction and may call the network; the other four run inside it and may not — a hook type that takes a network
/// or data-port dependency refuses the start.
/// </remarks>
public sealed partial class AlvoEntityBuilder
{
    /// <summary>Runs a <typeparamref name="THook"/>, built per write, before a top-level create, update or replace's transaction.</summary>
    /// <typeparam name="THook">The hook class.</typeparam>
    /// <param name="id">A stable id, lower-case letters, digits and hyphens.</param>
    /// <param name="when">An optional CEL condition the write must meet.</param>
    /// <param name="configure">Optional hook options.</param>
    /// <returns>This builder.</returns>
    public AlvoEntityBuilder Validate<THook>(string id, string? when = null, Action<AlvoHookOptions>? configure = null)
        where THook : class, IValidateWriteHook =>
        AddHook(id, WriteHookStage.Validate, HookInvoker.FromClass<THook, ValidateWrite<AlvoRecord>>(HookContexts<AlvoRecord>.Validate, (hook, write, ct) => hook.ValidateAsync(write, ct)), when, configure);

    /// <summary>Runs <paramref name="hook"/> before a top-level create, update or replace's transaction.</summary>
    /// <param name="id">A stable id.</param>
    /// <param name="hook">A delegate taking a <c>ValidateWrite&lt;AlvoRecord&gt;</c>, and optionally a token and services.</param>
    /// <param name="when">An optional CEL condition.</param>
    /// <param name="configure">Optional hook options.</param>
    /// <returns>This builder.</returns>
    public AlvoEntityBuilder Validate(string id, Delegate hook, string? when = null, Action<AlvoHookOptions>? configure = null) =>
        AddHook(id, WriteHookStage.Validate, HookInvoker.FromDelegate(hook, HookContexts<AlvoRecord>.Validate), when, configure);

    /// <summary>Runs a <typeparamref name="THook"/> inside a create, update or replace's transaction, before the row is written.</summary>
    /// <typeparam name="THook">The hook class.</typeparam>
    /// <param name="id">A stable id.</param>
    /// <param name="when">An optional CEL condition.</param>
    /// <param name="configure">Optional hook options.</param>
    /// <returns>This builder.</returns>
    public AlvoEntityBuilder BeforeWrite<THook>(string id, string? when = null, Action<AlvoHookOptions>? configure = null)
        where THook : class, IBeforeWriteHook =>
        AddHook(id, WriteHookStage.BeforeWrite, HookInvoker.FromClass<THook, BeforeWrite<AlvoRecord>>(HookContexts<AlvoRecord>.Before, (hook, write, ct) => hook.BeforeWriteAsync(write, ct)), when, configure);

    /// <summary>Runs <paramref name="hook"/> inside a create, update or replace's transaction, before the row is written.</summary>
    /// <param name="id">A stable id.</param>
    /// <param name="hook">A delegate taking a <c>BeforeWrite&lt;AlvoRecord&gt;</c>, and optionally a token and services.</param>
    /// <param name="when">An optional CEL condition.</param>
    /// <param name="configure">Optional hook options.</param>
    /// <returns>This builder.</returns>
    public AlvoEntityBuilder BeforeWrite(string id, Delegate hook, string? when = null, Action<AlvoHookOptions>? configure = null) =>
        AddHook(id, WriteHookStage.BeforeWrite, HookInvoker.FromDelegate(hook, HookContexts<AlvoRecord>.Before), when, configure);

    /// <summary>Runs a <typeparamref name="THook"/> inside a create, update or replace's transaction, after the row and its rollups.</summary>
    /// <typeparam name="THook">The hook class.</typeparam>
    /// <param name="id">A stable id.</param>
    /// <param name="when">An optional CEL condition over the stored row.</param>
    /// <param name="configure">Optional hook options.</param>
    /// <returns>This builder.</returns>
    public AlvoEntityBuilder AfterWrite<THook>(string id, string? when = null, Action<AlvoHookOptions>? configure = null)
        where THook : class, IAfterWriteHook =>
        AddHook(id, WriteHookStage.AfterWrite, HookInvoker.FromClass<THook, AfterWrite<AlvoRecord>>(HookContexts<AlvoRecord>.After, (hook, write, ct) => hook.AfterWriteAsync(write, ct)), when, configure);

    /// <summary>Runs <paramref name="hook"/> inside a create, update or replace's transaction, after the row and its rollups.</summary>
    /// <param name="id">A stable id.</param>
    /// <param name="hook">A delegate taking an <c>AfterWrite&lt;AlvoRecord&gt;</c>, and optionally a token and services.</param>
    /// <param name="when">An optional CEL condition over the stored row.</param>
    /// <param name="configure">Optional hook options.</param>
    /// <returns>This builder.</returns>
    public AlvoEntityBuilder AfterWrite(string id, Delegate hook, string? when = null, Action<AlvoHookOptions>? configure = null) =>
        AddHook(id, WriteHookStage.AfterWrite, HookInvoker.FromDelegate(hook, HookContexts<AlvoRecord>.After), when, configure);

    /// <summary>Runs a <typeparamref name="THook"/> inside a delete's transaction, before the row is removed.</summary>
    /// <typeparam name="THook">The hook class.</typeparam>
    /// <param name="id">A stable id.</param>
    /// <param name="when">An optional CEL condition over the row (<c>old.</c>).</param>
    /// <param name="configure">Optional hook options.</param>
    /// <returns>This builder.</returns>
    public AlvoEntityBuilder BeforeDelete<THook>(string id, string? when = null, Action<AlvoHookOptions>? configure = null)
        where THook : class, IBeforeDeleteHook =>
        AddHook(id, WriteHookStage.BeforeDelete, HookInvoker.FromClass<THook, BeforeDelete<AlvoRecord>>(HookContexts<AlvoRecord>.BeforeDelete, (hook, delete, ct) => hook.BeforeDeleteAsync(delete, ct)), when, configure);

    /// <summary>Runs <paramref name="hook"/> inside a delete's transaction, before the row is removed.</summary>
    /// <param name="id">A stable id.</param>
    /// <param name="hook">A delegate taking a <c>BeforeDelete&lt;AlvoRecord&gt;</c>, and optionally a token and services.</param>
    /// <param name="when">An optional CEL condition over the row (<c>old.</c>).</param>
    /// <param name="configure">Optional hook options.</param>
    /// <returns>This builder.</returns>
    public AlvoEntityBuilder BeforeDelete(string id, Delegate hook, string? when = null, Action<AlvoHookOptions>? configure = null) =>
        AddHook(id, WriteHookStage.BeforeDelete, HookInvoker.FromDelegate(hook, HookContexts<AlvoRecord>.BeforeDelete), when, configure);

    /// <summary>Runs a <typeparamref name="THook"/> inside a delete's transaction, after the row is removed.</summary>
    /// <typeparam name="THook">The hook class.</typeparam>
    /// <param name="id">A stable id.</param>
    /// <param name="when">An optional CEL condition over the removed row (<c>old.</c>).</param>
    /// <param name="configure">Optional hook options.</param>
    /// <returns>This builder.</returns>
    public AlvoEntityBuilder AfterDelete<THook>(string id, string? when = null, Action<AlvoHookOptions>? configure = null)
        where THook : class, IAfterDeleteHook =>
        AddHook(id, WriteHookStage.AfterDelete, HookInvoker.FromClass<THook, AfterDelete<AlvoRecord>>(HookContexts<AlvoRecord>.AfterDelete, (hook, delete, ct) => hook.AfterDeleteAsync(delete, ct)), when, configure);

    /// <summary>Runs <paramref name="hook"/> inside a delete's transaction, after the row is removed.</summary>
    /// <param name="id">A stable id.</param>
    /// <param name="hook">A delegate taking an <c>AfterDelete&lt;AlvoRecord&gt;</c>, and optionally a token and services.</param>
    /// <param name="when">An optional CEL condition over the removed row (<c>old.</c>).</param>
    /// <param name="configure">Optional hook options.</param>
    /// <returns>This builder.</returns>
    public AlvoEntityBuilder AfterDelete(string id, Delegate hook, string? when = null, Action<AlvoHookOptions>? configure = null) =>
        AddHook(id, WriteHookStage.AfterDelete, HookInvoker.FromDelegate(hook, HookContexts<AlvoRecord>.AfterDelete), when, configure);

    private AlvoEntityBuilder AddHook(string id, WriteHookStage stage, HookInvoker invoker, string? when, Action<AlvoHookOptions>? configure)
    {
        WriteHookRegistration.Add(_services, id, _entities, stage, when, configure, invoker);

        return this;
    }
}
```

`src/MMLib.Alvo/Extensions/AlvoEntityBuilder{T}.WriteHooks.cs` — for each of the five stages, four overloads, in this pattern (shown for `BeforeWrite`; `Validate`, `AfterWrite`, `BeforeDelete`, `AfterDelete` repeat it with their interface, context factory and stage, and the XML docs of the untyped builder plus "`when`: a condition over `w.New`, `w.Old`, `w.Changed(…)` and `w.Caller`"):

```csharp
    public AlvoEntityBuilder<T> BeforeWrite<THook>(string id, Expression<Func<WriteCondition<T>, bool>> when, Action<AlvoHookOptions>? configure = null)
        where THook : class, IBeforeWriteHook<T> =>
        AddHook(id, WriteHookStage.BeforeWrite, ClassHook<THook, BeforeWrite<T>>(HookContexts<T>.Before, (hook, write, ct) => hook.BeforeWriteAsync(write, ct)), Translated(id, when), configure);

    public AlvoEntityBuilder<T> BeforeWrite<THook>(string id, string? when = null, Action<AlvoHookOptions>? configure = null)
        where THook : class, IBeforeWriteHook<T> =>
        AddHook(id, WriteHookStage.BeforeWrite, ClassHook<THook, BeforeWrite<T>>(HookContexts<T>.Before, (hook, write, ct) => hook.BeforeWriteAsync(write, ct)), when, configure);

    public AlvoEntityBuilder<T> BeforeWrite(string id, Delegate hook, Expression<Func<WriteCondition<T>, bool>> when, Action<AlvoHookOptions>? configure = null) =>
        AddHook(id, WriteHookStage.BeforeWrite, HookInvoker.FromDelegate(hook, HookContexts<T>.Before), Translated(id, when), configure);

    public AlvoEntityBuilder<T> BeforeWrite(string id, Delegate hook, string? when = null, Action<AlvoHookOptions>? configure = null) =>
        AddHook(id, WriteHookStage.BeforeWrite, HookInvoker.FromDelegate(hook, HookContexts<T>.Before), when, configure);
```

and the shared private members:

```csharp
    private static HookInvoker ClassHook<THook, TContext>(Func<HookRun, TContext> context, Func<THook, TContext, CancellationToken, ValueTask> call)
        where THook : class => HookInvoker.FromClass(context, call);

    private static string Translated(string id, Expression<Func<WriteCondition<T>, bool>> when)
    {
        ArgumentNullException.ThrowIfNull(when);
        return WriteConditionTranslator.Translate(when, id);
    }

    private AlvoEntityBuilder<T> AddHook(string id, WriteHookStage stage, HookInvoker invoker, string? when, Action<AlvoHookOptions>? configure)
    {
        WriteHookRegistration.Add(_services, id, [_entity], stage, when, configure, invoker, RecordShape.Of(typeof(T)));

        return this;
    }
```

(`HookRun`, `HookContexts<T>` and `WriteHookStage` are internal; private members of a public class may name them.) Each public overload carries full XML docs — `id`, `when`, `configure`, `hook`, `THook`, `returns` — as the untyped file shows; DocsGen fails a public member without them.

- [ ] **Step 5: Run the tests to verify they pass, then ring0**

Run: `dotnet test --project test/MMLib.Alvo.Tests --filter-class '*WriteHookRegistrationTests'`, `--filter-class '*WriteConditionTranslatorTests'` → PASS. Accept the core public-API baseline: the diff adds exactly ten methods on `AlvoEntityBuilder` and twenty on `AlvoEntityBuilder<T>`. `scripts/test-ring0`.

- [ ] **Step 6: Commit**

```bash
dotnet format whitespace MMLib.Alvo.slnx --include src/MMLib.Alvo/Hooks/Internal/WriteHookStage.cs src/MMLib.Alvo/Hooks/Internal/WriteHookRegistration.cs src/MMLib.Alvo/Hooks/Internal/HookInvoker.cs src/MMLib.Alvo/Hooks/Internal/HookContexts.cs src/MMLib.Alvo/Hooks/Internal/WriteConditionTranslator.cs src/MMLib.Alvo/Extensions/AlvoEntityBuilder.WriteHooks.cs "src/MMLib.Alvo/Extensions/AlvoEntityBuilder{T}.WriteHooks.cs" src/MMLib.Alvo/Extensions/AlvoEntityBuilder.cs "src/MMLib.Alvo/Extensions/AlvoEntityBuilder{T}.cs" src/MMLib.Alvo/Extensions/Internal/ExtensionRegistry.cs src/MMLib.Alvo/Extensions/Internal/HandlerInvoker.cs test/MMLib.Alvo.Tests/Hooks/WriteHookRegistrationTests.cs test/MMLib.Alvo.Tests/Hooks/WriteConditionTranslatorTests.cs
git add src/MMLib.Alvo/Hooks src/MMLib.Alvo/Extensions test/MMLib.Alvo.Tests/Hooks test/MMLib.Alvo.Tests/PublicApi.MMLib.Alvo.verified.txt
git commit -m "feat(hooks): register write hooks — typed and untyped, class and delegate

Claude-Session: https://claude.ai/code/session_01NJAtafM29iddLwfhb5gnmB"
```

---

## Task 7: Boot, apply and catalog — binding write hooks, the dependency ban, option bounds, the apply guard, the catalog entries

**Files:**
- Create: `src/MMLib.Alvo/Hooks/Internal/WriteHookBinding.cs`, `src/MMLib.Alvo/Hooks/Internal/BoundWriteHook.cs`, `src/MMLib.Alvo/Hooks/Internal/HookDependencies.cs`, `src/MMLib.Alvo/Hooks/Internal/WriteHookApplyRefusals.cs`, `src/MMLib.Alvo/Hooks/Internal/HookDescription.cs`
- Modify: `src/MMLib.Alvo/Extensions/Internal/ExtensionBindings.cs`, `ExtensionBindingCompiler.cs`, `ExtensionApplyGuard.cs`, `ExtensionApplyRefusals.cs`, `ExtensionIdProblems.cs`, `ExtensionCatalog.cs`
- Test: `test/MMLib.Alvo.Tests/Hooks/WriteHookBindingTests.cs`, `test/MMLib.Alvo.TestHost.Tests/ExtensionBootRefusalTests.cs` (one fact)

**Interfaces:**
- Consumes: Task 6's `WriteHookRegistration`, `HookInvoker.Dependencies/Unresolvable`, `ExtensionRegistry.WriteHooks`; Task 1's internal `IHookRejectionLog`; existing `AfterImagePhase.Refusals`, `ShapeMapping.Problems`, `ExtensionApplyRefusals.Generic`, `EventConditionReads.FieldsReadOrCompared`.
- Produces: `WriteHookBinding(WriteHookRegistration, ICelCompiler)` with `CheckRegistration(IServiceProviderIsService, bool relational)`, `Bind(PolicyCatalog)` → `BoundWriteHook?`, `Problems`, static `KindOf(WriteHookStage)`, static `Relational(IServiceProviderIsService)`; `BoundWriteHook(WriteHookRegistration Registration, IReadOnlyDictionary<string, CompiledExpression?> Conditions, CompiledExpression? Fallback)` with `Id`, `ConditionFor(string)`; `ExtensionBindings.WriteHooks`, `.UnboundWriteHooks`, `.WriteHook(string)`; `HookDependencies.IsBanned(Type)`; `HookDescription.Of(WriteHookRegistration, bool)`.

- [ ] **Step 1: Write the failing tests**

`test/MMLib.Alvo.Tests/Hooks/WriteHookBindingTests.cs`:

```csharp
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using MMLib.Alvo.Data;
using MMLib.Alvo.Descriptor;
using MMLib.Alvo.Extensions;
using MMLib.Alvo.Extensions.Internal;
using MMLib.Alvo.Hooks;
using MMLib.Alvo.Management;
using MMLib.Alvo.Rules;
using MMLib.Alvo.Schema;
using MMLib.Alvo.Tests.Expressions;
using MMLib.Alvo.Tests.Extensions;
using NSubstitute;
using System.Net.Http;

namespace MMLib.Alvo.Tests.Hooks;

public sealed class NetworkGuard(HttpClient http) : IBeforeWriteHook
{
    public ValueTask BeforeWriteAsync(BeforeWrite<AlvoRecord> write, CancellationToken cancellationToken)
    {
        _ = http.Timeout;
        return ValueTask.CompletedTask;
    }
}

public sealed class NetworkValidator(HttpClient http) : IValidateWriteHook
{
    public ValueTask ValidateAsync(ValidateWrite<AlvoRecord> write, CancellationToken cancellationToken)
    {
        _ = http.Timeout;
        return ValueTask.CompletedTask;
    }
}

/// <summary>Boot and apply checks of write hooks (spec 2026-10-14 §4.2, §4.4.5): all problems at once, each with id, kind, locator and fix.</summary>
public sealed class WriteHookBindingTests
{
    private static readonly Func<BeforeWrite<AlvoRecord>, ValueTask> _before = _ => ValueTask.CompletedTask;
    private static readonly Func<BeforeDelete<AlvoRecord>, ValueTask> _beforeDelete = _ => ValueTask.CompletedTask;

    [Fact]
    public void A_hook_on_a_known_entity_binds_with_its_compiled_condition()
    {
        var result = Compile(alvo => alvo.Entity("tickets", t => t.BeforeWrite("guard", _before, when: "new.status == 'open'")));

        result.Problems.ShouldBeEmpty();
        result.Bindings.WriteHook("guard").ShouldNotBeNull().ConditionFor("tickets").ShouldNotBeNull();
    }

    [Fact]
    public void An_unknown_entity_and_a_when_typo_are_refused_at_once_with_their_locators()
    {
        var problems = Compile(alvo => alvo
            .Entity("tikets", t => t.BeforeWrite("misspelled", _before))
            .Entity("tickets", t => t.BeforeWrite("typo", _before, when: "new.stauts == 'open'"))).Problems;

        problems.Select(problem => (problem.Id, problem.Kind, problem.Locator)).Distinct().ShouldBe(
            [("misspelled", "beforeWrite", "extensions/misspelled/entity"), ("typo", "beforeWrite", "extensions/typo/when")], ignoreOrder: true);
        problems.Single(problem => problem.Id == "misspelled").Fix.ShouldContain("Did you mean 'tickets'?");
    }

    [Fact]
    public void A_delete_hook_reading_the_new_image_is_refused() =>
        Compile(alvo => alvo.Entity("tickets", t => t.BeforeDelete("guard", _beforeDelete, when: "new.status == 'open'")))
            .Problems.ShouldHaveSingleItem().Locator.ShouldBe("extensions/guard/when");

    [Fact]
    public void An_every_entity_hook_whose_condition_names_a_field_is_refused() =>
        Compile(alvo => alvo.Entity("*", every => every.BeforeWrite("guard", _before, when: "new.status == 'open'")))
            .Problems.ShouldHaveSingleItem().Locator.ShouldBe("extensions/guard/when");

    [Fact]
    public void An_in_transaction_hook_taking_a_network_dependency_refuses_the_start_and_a_validate_hook_may()
    {
        var problems = Compile(alvo => alvo
            .Entity("tickets", t => t.BeforeWrite<NetworkGuard>("network-guard"))
            .Entity("tickets", t => t.Validate<NetworkValidator>("network-check"))
            .Entity("notes", n => n.AfterWrite("data-port", (AfterWrite<AlvoRecord> _, IAlvoData data) => ValueTask.CompletedTask))
            .Entity("notes", n => n.AfterWrite("lazy-port", (AfterWrite<AlvoRecord> _, Lazy<IAlvoSessions> sessions) => ValueTask.CompletedTask))).Problems;

        problems.Where(problem => problem.Locator.EndsWith("/dependencies", StringComparison.Ordinal)).Select(problem => problem.Id)
            .ShouldBe(["network-guard", "data-port", "lazy-port"], ignoreOrder: true);
        problems.Single(problem => problem.Id == "network-guard").Message.ShouldContain("System.Net.Http.HttpClient");
    }

    [Theory]
    [InlineData(0, 50)]
    [InlineData(31_000, 50)]
    [InlineData(250, 0)]
    [InlineData(250, 1001)]
    public void An_in_transaction_hook_s_bounds_are_checked_at_boot(int milliseconds, int operations) =>
        Compile(alvo => alvo.Entity("tickets", t => t.BeforeWrite("guard", _before, configure: o =>
        {
            o.Timeout = TimeSpan.FromMilliseconds(milliseconds);
            o.MaxOperations = operations;
        }))).Problems.ShouldHaveSingleItem().Id.ShouldBe("guard");

    [Fact]
    public void A_validate_hook_may_take_a_minute_and_no_more() =>
        Compile(alvo => alvo.Entity("tickets", t => t.Validate("slow", (ValidateWrite<AlvoRecord> _) => ValueTask.CompletedTask, configure: o => o.Timeout = TimeSpan.FromSeconds(61))))
            .Problems.ShouldHaveSingleItem().Locator.ShouldBe("extensions/slow/timeout");

    [Fact]
    public void A_composition_with_no_relational_provider_refuses_a_write_hook()
    {
        var services = Substitute.For<IServiceProviderIsService>();
        services.IsService(Arg.Any<Type>()).Returns(call => (Type)call[0] != typeof(IAlvoTransactions));

        HandlerCatalog.Compile(alvo => alvo.Entity("tickets", t => t.BeforeWrite("guard", _before)), services)
            .Problems.ShouldHaveSingleItem().Locator.ShouldBe("extensions/guard/provider");
    }

    [Fact]
    public void Removing_a_field_a_bound_write_hook_reads_is_refused_at_apply()
    {
        var registry = HandlerCatalog.Registry(alvo => alvo.Entity("tickets", t => t.BeforeWrite("guard", _before, when: "new.status == 'open'")));
        var binder = new ExtensionBinder(registry, CelFixtures.Compiler, HandlerCatalog.EveryService(), NullLogger<ExtensionBinder>.Instance);
        var catalogs = Substitute.For<IPolicyCatalogProvider>();
        catalogs.Current.Returns(HandlerCatalog.Catalog());
        var tickets = HandlerCatalog.Schema.Entities.Single(entity => entity.Name == "tickets");
        var proposed = HandlerCatalog.CatalogOf(new SchemaModel([tickets with { Fields = [.. tickets.Fields.Where(field => field.Name != "status")] }]));

        var refusal = Should.Throw<DescriptorValidationException>(() => new ExtensionApplyGuard(registry, binder, CelFixtures.Compiler, catalogs).Check(proposed));

        refusal.Message.ShouldContain("'guard'");
    }

    [Fact]
    public void The_catalog_describes_each_stage_with_its_kind_stage_operations_and_top_level_flag()
    {
        var registry = HandlerCatalog.Registry(alvo => alvo.Entity("tickets", t => t
            .Validate("check", (ValidateWrite<AlvoRecord> _) => ValueTask.CompletedTask)
            .BeforeWrite("guard", _before, when: "new.status == 'open'", configure: o => o.Description = "Guards.")
            .AfterDelete("tidy", (AfterDelete<AlvoRecord> _) => ValueTask.CompletedTask)));

        var items = new ExtensionCatalog(registry, Options.Create(new AlvoExtensionOptions())).Items;

        items.Select(item => (item.Id, item.Kind, item.Stage, string.Join(",", item.Operations), item.TopLevelOnly)).ShouldBe(
        [
            ("check", AlvoExtensionKind.Validate, AlvoExtensionStage.PreTransaction, "create,update", true),
            ("guard", AlvoExtensionKind.BeforeWrite, AlvoExtensionStage.InTransaction, "create,update", false),
            ("tidy", AlvoExtensionKind.AfterDelete, AlvoExtensionStage.InTransaction, "delete", false),
        ]);
        items.Single(item => item.Id == "guard").ShouldSatisfyAllConditions(
            item => item.When.ShouldBe("new.status == 'open'"),
            item => item.Timeout.ShouldBe(TimeSpan.FromMilliseconds(250)),
            item => item.Description.ShouldBe("Guards."),
            item => item.Enabled.ShouldBeTrue());
    }

    private static ExtensionBindingResult Compile(Action<IAlvoBuilder> configure) => HandlerCatalog.Compile(configure);
}
```

`ExtensionBootRefusalTests` gains (acceptance (7), through the real boot):

```csharp
    [Fact]
    public async Task A_write_hook_injecting_an_http_client_refuses_boot_naming_the_parameter()
    {
        var refusal = await Should.ThrowAsync<AlvoStartupRefusedException>(() => AlvoTestHost.StartAsync(alvo =>
        {
            alvo.FromDescriptor(Path.Combine(AppContext.BaseDirectory, "descriptors", "hook-desk.alvo.json"));
            alvo.Entity("bookings", b => b.BeforeWrite("network-guard", (BeforeWrite<AlvoRecord> _, HttpClient http) => ValueTask.CompletedTask));
        }, cancellationToken: TestContext.Current.CancellationToken));

        refusal.Message.ShouldContain("'network-guard'");
        refusal.Message.ShouldContain("extensions/network-guard/dependencies");
        refusal.Message.ShouldContain("'http'");
    }
```

(`hook-desk.alvo.json` is created in Task 11 Step 1; create it now if Task 11 has not run — its content is in Task 11.)

- [ ] **Step 2: Run them to verify they fail**

Run: `dotnet test --project test/MMLib.Alvo.Tests --filter-class '*WriteHookBindingTests'`
Expected: FAIL — `ExtensionBindings.WriteHook` does not exist; nothing checks write hooks.

- [ ] **Step 3: Write the binding**

`src/MMLib.Alvo/Hooks/Internal/HookDependencies.cs`:

```csharp
using MMLib.Alvo.Commands;
using MMLib.Alvo.Data;
using MMLib.Alvo.Events;
using System.Data.Common;

namespace MMLib.Alvo.Hooks.Internal;

/// <summary>
/// What code inside a write's transaction may not depend on (spec 2026-10-14 §4.4.5, Ruling 23): the network, and every data
/// port that would write beside the unit. Direct dependencies only, generic arguments included (<c>Lazy&lt;IAlvoData&gt;</c>).
/// </summary>
internal static class HookDependencies
{
    private static readonly Type[] _banned =
    [
        typeof(DbConnection), typeof(IAlvoEvents), typeof(IAlvoData), typeof(IAlvoSessions), typeof(IAlvoTransactions),
        typeof(IOutboxStore), typeof(IDeliveryStore), typeof(IAlvoCommands), typeof(Management.IAlvoManagement),
        typeof(Management.IAlvoOperations),
    ];

    private static readonly string[] _networkNamespaces =
        ["System.Net.Sockets", "System.Net.Http", "System.Net.WebSockets", "System.Net.Mail", "System.Net.Quic"];

    /// <summary>Matched by name: naming the obsolete types would raise SYSLIB0014 in the Release build.</summary>
    private static readonly string[] _webRequestTypes = ["WebClient", "WebRequest", "HttpWebRequest", "FtpWebRequest"];

    internal static bool IsBanned(Type type) =>
        _networkNamespaces.Contains(type.Namespace)
        || (type.Namespace == "System.Net" && _webRequestTypes.Contains(type.Name))
        || type.Namespace?.StartsWith("Microsoft.AspNetCore.Identity", StringComparison.Ordinal) == true
        || type.Namespace?.StartsWith("MMLib.Alvo.Identity", StringComparison.Ordinal) == true
        || _banned.Any(banned => banned.IsAssignableFrom(type))
        || IsMailSender(type)
        || (type.IsGenericType && type.GetGenericArguments().Any(IsBanned))
        || (type.IsArray && IsBanned(type.GetElementType()!));

    private static bool IsMailSender(Type type) =>
        type.Name == "IEmailSender" || (type.IsGenericType && type.GetGenericTypeDefinition().Name == "IEmailSender`1");
}
```

`src/MMLib.Alvo/Hooks/Internal/BoundWriteHook.cs`:

```csharp
using MMLib.Alvo.Expressions;

namespace MMLib.Alvo.Hooks.Internal;

/// <summary>A write hook bound to one catalog: its compiled condition per named entity, or one for every entity.</summary>
internal sealed record BoundWriteHook(WriteHookRegistration Registration, IReadOnlyDictionary<string, CompiledExpression?> Conditions, CompiledExpression? Fallback)
{
    internal string Id => Registration.Id;

    /// <summary>The condition the hook judges a write to <paramref name="entity"/> by; <see langword="null"/> runs it always.</summary>
    internal CompiledExpression? ConditionFor(string entity) => Registration.IsEveryEntity ? Fallback : Conditions.GetValueOrDefault(entity);
}
```

`src/MMLib.Alvo/Hooks/Internal/WriteHookBinding.cs`:

```csharp
using Microsoft.Extensions.DependencyInjection;
using MMLib.Alvo.Data;
using MMLib.Alvo.Data.Internal;
using MMLib.Alvo.Events.Internal;
using MMLib.Alvo.Expressions;
using MMLib.Alvo.Extensions;
using MMLib.Alvo.Extensions.Internal;
using MMLib.Alvo.Internal;
using MMLib.Alvo.Rules;
using MMLib.Alvo.Schema;

namespace MMLib.Alvo.Hooks.Internal;

/// <summary>
/// One write hook's checks, collecting its own problems: <see cref="CheckRegistration"/> needs no catalog — shape, services,
/// the dependency ban, bounds, provider, a fieldless <c>when</c> — and <see cref="Bind"/> binds each named entity.
/// </summary>
internal sealed class WriteHookBinding(WriteHookRegistration hook, ICelCompiler compiler)
{
    private const string WhenFix = "Correct the condition: it is CEL over new., old., changed(field), @user and @tenant.";

    private readonly List<ExtensionProblem> _problems = [];
    private CompiledExpression? _fallback;

    internal IReadOnlyList<ExtensionProblem> Problems => _problems;

    internal static string KindOf(WriteHookStage stage) => stage switch
    {
        WriteHookStage.Validate => "validate",
        WriteHookStage.BeforeWrite => "beforeWrite",
        WriteHookStage.AfterWrite => "afterWrite",
        WriteHookStage.BeforeDelete => "beforeDelete",
        _ => "afterDelete",
    };

    /// <summary>Whether the composition runs write hooks: a unit-capable relational driver with a rejection log (Ruling 34).</summary>
    internal static bool Relational(IServiceProviderIsService services) =>
        services.IsService(typeof(IAlvoTransactions)) && services.IsService(typeof(IHookRejectionLog));

    internal WriteHookBinding CheckRegistration(IServiceProviderIsService services, bool relational)
    {
        CheckShape();
        ResolveServices(services);
        CheckDependencies();
        CheckTimeout();
        CheckBudget();
        CheckProvider(relational);
        CheckSource();

        return this;
    }

    internal BoundWriteHook? Bind(PolicyCatalog catalog)
    {
        var conditions = hook.IsEveryEntity ? new Dictionary<string, CompiledExpression?>(StringComparer.Ordinal) : EntityConditions(catalog);

        return _problems.Count == 0 ? new BoundWriteHook(hook, conditions, _fallback) : null;
    }

    private void CheckShape()
    {
        foreach (var problem in hook.Typed?.Problems ?? [])
        {
            Add("record", problem.Message, problem.Fix);
        }
    }

    private void ResolveServices(IServiceProviderIsService services)
    {
        foreach (var type in hook.Invoker.Unresolvable(services))
        {
            Add("hook", $"The hook needs a {type.FullName} that the host's services cannot provide.", $"Register {type.Name} in the host's services.");
        }
    }

    private void CheckDependencies()
    {
        if (!hook.InTransaction)
        {
            return;
        }

        foreach (var (name, type) in hook.Invoker.Dependencies.Where(dependency => HookDependencies.IsBanned(dependency.Type)))
        {
            Add("dependencies",
                $"The hook takes a {type.FullName} ('{name}'): code that runs inside the write's transaction may not reach the network or another data port.",
                "Make the call in a validate hook, before the transaction, or in an event handler, after the commit; write other rows through write.Transaction.");
        }
    }

    private void CheckTimeout()
    {
        var limit = hook.InTransaction ? AlvoHookOptions.MaxTimeout : AlvoHookOptions.MaxValidateTimeout;
        if (hook.Settings.Timeout <= TimeSpan.Zero || hook.Settings.Timeout > limit)
        {
            Add("timeout", $"A timeout of {hook.Settings.Timeout} is not greater than zero and at most {limit}.",
                hook.InTransaction
                    ? "Set Timeout to at most 30 seconds: the hook holds the write's transaction while it runs."
                    : "Set Timeout to at most 60 seconds, or leave the default 5 seconds.");
        }
    }

    private void CheckBudget()
    {
        if (hook.Settings.MaxOperations is < 1 or > AlvoHookOptions.MaxMaxOperations)
        {
            Add("maxOperations", $"MaxOperations {hook.Settings.MaxOperations} is outside 1 to {AlvoHookOptions.MaxMaxOperations}.",
                "Set MaxOperations between 1 and 1000, or leave the default 50; write many rows with one batch call.");
        }
    }

    private void CheckProvider(bool relational)
    {
        if (!relational)
        {
            Add("provider", "Write hooks run in a relational provider's transaction, and this composition has none, so the hook would never run.",
                "Register UseSqlite or UsePostgreSql; the in-memory reference runs no host write hook.");
        }
    }

    private void CheckSource()
    {
        if (!hook.IsEveryEntity && hook.Entities.Contains(EntityEvents.AllEntities))
        {
            Add("entity", "'*' binds every entity, so naming entities beside it says nothing.", "Register Entity(\"*\", …) on its own, or name the entities.");
        }
        else if (hook.IsEveryEntity && hook.When is { } when)
        {
            _fallback = Compiled(when, new EntitySchema { Name = hook.Id, Fields = [] },
                "A '*' hook binds every entity, including ones added later: test only @user and @tenant here, or register the entities by name.");
        }
    }

    private Dictionary<string, CompiledExpression?> EntityConditions(PolicyCatalog catalog)
    {
        var conditions = new Dictionary<string, CompiledExpression?>(StringComparer.Ordinal);
        foreach (var name in hook.Entities)
        {
            if (catalog.Schema.Entities.FirstOrDefault(entity => string.Equals(entity.Name, name, StringComparison.Ordinal)) is not { } entity)
            {
                UnknownEntity(name, catalog);
                continue;
            }

            conditions[name] = hook.When is { } when ? Compiled(when, entity, WhenFix) : null;
            Mapped(entity);
        }

        return conditions;
    }

    private CompiledExpression? Compiled(string when, EntitySchema entity, string fix)
    {
        var result = compiler.Compile(when, CelProfile.Condition, entity);
        if (!result.IsSuccess)
        {
            result.Errors.ToList().ForEach(error => Add("when", $"{error.Message} (entity '{entity.Name}')", error.FixSuggestion ?? fix));
            return null;
        }

        foreach (var refusal in AfterImagePhase.Refusals(result.Expression!.Root, Images(), hook.Stage.ToString(), Locator("when")))
        {
            Add("when", refusal.Message, refusal.FixSuggestion ?? fix);
        }

        return result.Expression;
    }

    private AfterImages Images() => hook.Stage is WriteHookStage.BeforeDelete or WriteHookStage.AfterDelete
        ? new(Pre: true, Post: false)
        : new(Pre: true, Post: true);

    private void Mapped(EntitySchema entity)
    {
        if (hook.Typed is { Problems.Count: 0 } shape)
        {
            foreach (var problem in ShapeMapping.Problems(shape, entity, fromEventImages: false))
            {
                Add("record", problem.Message, problem.Fix);
            }
        }
    }

    private void UnknownEntity(string name, PolicyCatalog catalog)
    {
        var known = catalog.Schema.Entities.Select(entity => entity.Name).Order(StringComparer.Ordinal).ToList();
        var closest = NameSuggestion.Closest(name, known);
        var list = $"Entities: {string.Join(", ", known)}.";
        Add("entity", $"'{name}' is not an entity of this project, so the hook would never run.", closest is null ? list : $"Did you mean '{closest}'? {list}");
    }

    private void Add(string slot, string message, string fix) =>
        _problems.Add(new ExtensionProblem(hook.Id, KindOf(hook.Stage), Locator(slot), message, fix));

    private string Locator(string slot) => $"extensions/{hook.Id}/{slot}";
}
```

(`AfterImages` and `AfterImagePhase` are in `MMLib.Alvo.Events.Internal`; `NameSuggestion` in `MMLib.Alvo.Internal` — the namespaces `HandlerBinding` imports.)

`src/MMLib.Alvo/Hooks/Internal/HookDescription.cs`:

```csharp
using MMLib.Alvo.Management;

namespace MMLib.Alvo.Hooks.Internal;

/// <summary>One write hook as the catalog describes it (Ruling 6).</summary>
internal static class HookDescription
{
    private static readonly string[] _writes = ["create", "update"];
    private static readonly string[] _deletes = ["delete"];

    internal static ManagementExtension Of(WriteHookRegistration hook, bool exposeTypeNames) => new()
    {
        Id = hook.Id,
        Kind = KindOf(hook.Stage),
        Stage = hook.InTransaction ? AlvoExtensionStage.InTransaction : AlvoExtensionStage.PreTransaction,
        Entities = hook.Entities,
        Operations = hook.Stage is WriteHookStage.BeforeDelete or WriteHookStage.AfterDelete ? _deletes : _writes,
        When = hook.When,
        Order = hook.Settings.Order,
        Timeout = hook.Settings.Timeout,
        Description = hook.Settings.Description,
        TypeName = exposeTypeNames ? hook.Invoker.TypeName : null,
        TopLevelOnly = !hook.InTransaction,
    };

    private static AlvoExtensionKind KindOf(WriteHookStage stage) => stage switch
    {
        WriteHookStage.Validate => AlvoExtensionKind.Validate,
        WriteHookStage.BeforeWrite => AlvoExtensionKind.BeforeWrite,
        WriteHookStage.AfterWrite => AlvoExtensionKind.AfterWrite,
        WriteHookStage.BeforeDelete => AlvoExtensionKind.BeforeDelete,
        _ => AlvoExtensionKind.AfterDelete,
    };
}
```

`src/MMLib.Alvo/Hooks/Internal/WriteHookApplyRefusals.cs`:

```csharp
using MMLib.Alvo.Descriptor;
using MMLib.Alvo.Events.Internal;
using MMLib.Alvo.Expressions;
using MMLib.Alvo.Extensions.Internal;
using MMLib.Alvo.Schema;

namespace MMLib.Alvo.Hooks.Internal;

/// <summary>
/// An apply refusal for a write hook the change would unbind (HX-04 (6)'s guard): a pointer at the removed entity or at each
/// field its condition read that is gone, with §4.2's fix, then the compiler's own sentence for every problem.
/// </summary>
internal static class WriteHookApplyRefusals
{
    private const string FixPrefix = "Remove the registration and redeploy first, or keep the ";

    internal static IReadOnlyList<DescriptorValidationError> For(
        WriteHookRegistration hook, IEnumerable<ExtensionProblem> problems, SchemaModel current, SchemaModel proposed, ICelCompiler compiler) =>
        [.. hook.IsEveryEntity ? [] : hook.Entities.SelectMany(entity => Precise(hook, entity, current, proposed, compiler)),
         .. problems.Select(problem => ExtensionApplyRefusals.Generic(problem, DescriptorValidationSeverity.Error))];

    private static IEnumerable<DescriptorValidationError> Precise(
        WriteHookRegistration hook, string name, SchemaModel current, SchemaModel proposed, ICelCompiler compiler)
    {
        var kind = WriteHookBinding.KindOf(hook.Stage);
        if (Named(proposed, name) is not { } after)
        {
            return [Error($"/entities/{name}", $"Entity '{name}' is bound by extension '{hook.Id}' ({kind}, in this host): without it every write to it would be refused as unbound.", FixPrefix + "entity.")];
        }

        return Named(current, name) is { } before && hook.When is { } when
            ? FieldsRead(when, before, compiler).Where(field => !after.Fields.Any(kept => kept.Name == field)).Select(field => Error(
                $"/entities/{name}/fields/{field}",
                $"Field '{field}' of '{name}' is read by extension '{hook.Id}' ({kind}, in this host): without it the hook would no longer bind, and every write to '{name}' would be refused as unbound.",
                FixPrefix + "field."))
            : [];
    }

    private static string[] FieldsRead(string when, EntitySchema entity, ICelCompiler compiler)
    {
        var result = compiler.Compile(when, CelProfile.Condition, entity);
        return result.IsSuccess ? [.. EventConditionReads.FieldsReadOrCompared(result.Expression!.Root).Distinct(StringComparer.Ordinal)] : [];
    }

    private static EntitySchema? Named(SchemaModel schema, string name) =>
        schema.Entities.FirstOrDefault(entity => string.Equals(entity.Name, name, StringComparison.Ordinal));

    private static DescriptorValidationError Error(string path, string message, string fix) => new(path, message, fix, DescriptorValidationSeverity.Error);
}
```

Wiring into the existing extension plumbing:

- `ExtensionBindings`: the constructor gains `IEnumerable<BoundWriteHook>? writeHooks = null, IEnumerable<WriteHookRegistration>? unboundWriteHooks = null`; `WriteHooks` is `[.. (writeHooks ?? []).OrderBy(h => h.Registration.Settings.Order).ThenBy(h => h.Registration.IsEveryEntity ? 1 : 0).ThenBy(h => h.Registration.Sequence)]` (Ruling 26); `UnboundWriteHooks = [.. unboundWriteHooks ?? []]`; `internal BoundWriteHook? WriteHook(string id)` looks it up in a dictionary built like `_byId`.
- `ExtensionBindingCompiler.Check` adds `problems.AddRange(registry.WriteHooks.SelectMany(hook => new WriteHookBinding(hook, compiler).CheckRegistration(services, WriteHookBinding.Relational(services)).Problems));` before `Refused`, and its ties become `[.. Ties(handlers), .. HookTies(registry.WriteHooks.Where(hook => !refused.Contains(hook.Id)))]`.
- `ExtensionBindingCompiler.Compile` adds a `BindWriteHooks` step shaped like `BindHandlers` (one `WriteHookBinding` per registration, `Bind(catalog)`, problems collected), and builds `new ExtensionBindings(binds, unboundHandlers, commands, boundHooks.Where(h => !refused.Contains(h.Id)), registry.WriteHooks.Where(h => refused.Contains(h.Id)))`.
- `HookTies`:

```csharp
    private static IReadOnlyList<OrderTie> HookTies(IEnumerable<WriteHookRegistration> hooks) =>
        [.. hooks
            .SelectMany(hook => hook.Entities.Select(entity => (Target: $"{entity} {WriteHookBinding.KindOf(hook.Stage)}", Hook: hook)))
            .GroupBy(entry => (entry.Target, entry.Hook.Settings.Order))
            .Where(group => group.Count() > 1 && group.Any(entry => entry.Hook.Settings.OrderStated))
            .Select(group => new OrderTie(group.Key.Target, group.Key.Order, [.. group.Select(entry => entry.Hook.Id)]))];
```

- `ExtensionApplyGuard.BoundNow` adds `.. bindings.WriteHooks.Select(hook => hook.Id)`.
- `ExtensionApplyRefusals.Refusals`: before the handler branch, `registry.WriteHooks.FirstOrDefault(hook => hook.Id == refused.Key) is { } writeHook ? WriteHookApplyRefusals.For(writeHook, refused, current, proposed, compiler) : …`.
- `ExtensionIdProblems.KindOf` gains `WriteHookRegistration hook => WriteHookBinding.KindOf(hook.Stage)`.
- `ExtensionCatalog.Describe` becomes `[.. Handlers(registry, exposeTypeNames), .. Commands(registry, exposeTypeNames), .. WriteHooks(registry, exposeTypeNames)]` with `WriteHooks` ordered by `Settings.Order` then `Sequence` and mapped through `HookDescription.Of`; `CatalogAsync` reads the switched-off ids too:

```csharp
        var controls = await store.ControlsAsync(ct).ConfigureAwait(false);
        var disabled = (await store.DisabledAsync(ct).ConfigureAwait(false)).ToHashSet(StringComparer.Ordinal);
        var paused = controls.Select(control => control.Subscriber).ToHashSet(StringComparer.Ordinal);

        return new ManagementExtensionCatalog
        {
            Items = [.. Items.Select(item => item with { Paused = paused.Contains(item.Id), Enabled = !disabled.Contains(item.Id) })],
        };
```

- [ ] **Step 4: Run them, the existing extension suites, and the boot fact**

Run: `dotnet test --project test/MMLib.Alvo.Tests --filter-class '*WriteHookBindingTests'`, then `--filter-class '*Extension*'` (the existing binder, compiler, guard and catalog suites stay green), then `dotnet test --project test/MMLib.Alvo.TestHost.Tests --filter-class '*ExtensionBootRefusalTests'`.
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
dotnet format whitespace MMLib.Alvo.slnx --include src/MMLib.Alvo/Hooks/Internal/WriteHookBinding.cs src/MMLib.Alvo/Hooks/Internal/BoundWriteHook.cs src/MMLib.Alvo/Hooks/Internal/HookDependencies.cs src/MMLib.Alvo/Hooks/Internal/WriteHookApplyRefusals.cs src/MMLib.Alvo/Hooks/Internal/HookDescription.cs src/MMLib.Alvo/Extensions/Internal/ExtensionBindings.cs src/MMLib.Alvo/Extensions/Internal/ExtensionBindingCompiler.cs src/MMLib.Alvo/Extensions/Internal/ExtensionApplyGuard.cs src/MMLib.Alvo/Extensions/Internal/ExtensionApplyRefusals.cs src/MMLib.Alvo/Extensions/Internal/ExtensionIdProblems.cs src/MMLib.Alvo/Extensions/Internal/ExtensionCatalog.cs test/MMLib.Alvo.Tests/Hooks/WriteHookBindingTests.cs test/MMLib.Alvo.TestHost.Tests/ExtensionBootRefusalTests.cs
scripts/test-ring0
git add src/MMLib.Alvo/Hooks src/MMLib.Alvo/Extensions/Internal test/MMLib.Alvo.Tests/Hooks test/MMLib.Alvo.TestHost.Tests/ExtensionBootRefusalTests.cs
git commit -m "feat(hooks): bind write hooks at boot and apply, and catalogue them

Claude-Session: https://claude.ai/code/session_01NJAtafM29iddLwfhb5gnmB"
```

---

## Task 8: The runner — selection, order, budget, timeout, poisoning, self-write, `AsSystem`, `Set` checks, rejections, telemetry **[slice end]**

**Files:**
- Create: `src/MMLib.Alvo/Hooks/Internal/WriteExtensionRunner.cs`, `src/MMLib.Alvo/Hooks/Internal/HookTransaction.cs`, `src/MMLib.Alvo/Hooks/Internal/HookSets.cs`, `src/MMLib.Alvo/Hooks/Internal/HookTelemetry.cs`, `src/MMLib.Alvo/Hooks/Internal/HookLog.cs`, `src/MMLib.Alvo/Hooks/Internal/Setup.cs`
- Modify: `src/MMLib.Alvo/Hooks/Internal/HookRun.cs` (`Transaction` gets a private setter and `Attach`), `src/MMLib.Alvo/AlvoServiceCollectionExtensions.cs` (calls `AddAlvoWriteHooks()` beside `AddAlvoExtensions()`), `test/MMLib.Alvo.Tests/Data/FakeFlowUnit.cs` (`Blamed`)
- Test: `test/MMLib.Alvo.Tests/Hooks/WriteExtensionRunnerTests.cs`, `test/MMLib.Alvo.Tests/Hooks/HookTransactionTests.cs`, `test/MMLib.Alvo.Tests/Hooks/HookSetsTests.cs`, `test/MMLib.Alvo.Tests/Hooks/RunnerWorld.cs`

**Interfaces:**
- Consumes: Tasks 5–7 (`HookRun`, `HookRejections`, `HookFailedException`, `BoundWriteHook`, `ExtensionBindings.WriteHooks/WriteHook`, `HookInvoker.InvokeAsync`); `IPolicyCatalogProvider.Current`, `ExtensionBinder.For`, `CelInterpreter.EvaluatePredicate`, `EntityPolicy.Flagged`, `FormatCatalog.Build`, `MutationTarget`, `AlvoFlow.Unit`, `IAlvoSessions`, `IDeliveryStore.DisabledAsync`, `IHookRejectionLog`.
- Produces: `WriteExtensionRunner : IWriteExtensionRunner` (singleton, registered by `AddAlvoWriteHooks`); `HookTransaction(IAlvoTransaction inner, HookRun run, BoundWriteHook hook, ILogger logger)`; `HookSets.FirstProblem(IReadOnlyDictionary<string, object?>, EntitySchema)` → `(string Field, string Problem)?`; `HookTelemetry.MeterName`, `.DurationName`, `.Succeeded/Rejected/Failed`, `.SubscriberTag`, `.OutcomeTag`.

- [ ] **Step 1: Write the failing tests**

`test/MMLib.Alvo.Tests/Hooks/RunnerWorld.cs`:

```csharp
using Microsoft.Extensions.DependencyInjection;
using MMLib.Alvo.Data;
using MMLib.Alvo.Rules;
using MMLib.Alvo.Tests.Extensions;
using NSubstitute;
using System.Collections.Concurrent;

namespace MMLib.Alvo.Tests.Hooks;

/// <summary>A core composition with write hooks over <see cref="HandlerCatalog"/>'s schema, a fake unit on the flow and a recording rejection log.</summary>
internal sealed class RunnerWorld : IAsyncDisposable
{
    private readonly ServiceProvider _provider;

    private RunnerWorld(ServiceProvider provider, RecordingRejectionLog rejections)
    {
        _provider = provider;
        Rejections = rejections;
    }

    internal static AlvoContext Caller { get; } = AlvoContext.System(new TenantId(Guid.Parse("11111111-0000-0000-0000-000000000001")));

    internal static readonly Guid TicketId = Guid.Parse("00000000-0000-0000-0000-0000000000a1");

    internal RecordingRejectionLog Rejections { get; }

    internal IWriteExtensionRunner Runner => _provider.GetRequiredService<IWriteExtensionRunner>();

    internal IServiceProvider Services => _provider;

    internal static RunnerWorld Start(Action<IAlvoBuilder> configure, TimeProvider? time = null, Action<IServiceCollection>? services = null)
    {
        var log = new RecordingRejectionLog();
        var collection = new ServiceCollection();
        collection.AddSingleton(time ?? TimeProvider.System);
        collection.AddSingleton(Substitute.For<IAlvoTransactions>());
        collection.AddSingleton<IHookRejectionLog>(log);
        services?.Invoke(collection);
        collection.AddAlvo(configure);
        var world = new RunnerWorld(collection.BuildServiceProvider(), log);
        world._provider.GetRequiredService<IPolicyCatalogProvider>().SetCurrent("handlers", HandlerCatalog.Catalog());

        return world;
    }

    internal WriteInvocation Before(AlvoRecord @new, AlvoRecord? old = null, IAlvoTransaction? transaction = null) => new()
    {
        Entity = "tickets",
        Operation = old is null ? AlvoWriteOperation.Create : AlvoWriteOperation.Update,
        Caller = Caller,
        Now = DateTimeOffset.UnixEpoch,
        Old = old,
        New = @new,
        Transaction = transaction ?? new FakeHookTransaction(Caller),
        Services = _provider,
    };

    internal static AlvoRecord Ticket(string status = "open", string title = "t") => new(new Dictionary<string, object?>
    {
        ["id"] = TicketId,
        ["title"] = title,
        ["status"] = status,
    });

    public ValueTask DisposeAsync() => _provider.DisposeAsync();
}

/// <summary>A rejection log that keeps what the runner hands it.</summary>
internal sealed class RecordingRejectionLog : IHookRejectionLog
{
    internal ConcurrentQueue<HookRejection> Recorded { get; } = new();

    public Task RecordAsync(HookRejection rejection, CancellationToken cancellationToken)
    {
        Recorded.Enqueue(rejection);
        return Task.CompletedTask;
    }
}
```

(`AddAlvo` registers with `TryAdd`, so the clock, the transaction port, the log and a fact's own registrations made before it win.)

`test/MMLib.Alvo.Tests/Hooks/WriteExtensionRunnerTests.cs`:

```csharp
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;
using MMLib.Alvo.Data;
using MMLib.Alvo.Events;
using MMLib.Alvo.Hooks;
using MMLib.Alvo.Hooks.Internal;
using MMLib.Alvo.Tests.Data;
using NSubstitute;
using System.Collections.Concurrent;

namespace MMLib.Alvo.Tests.Hooks;

/// <summary>
/// The core's write-hook runner (spec 2026-10-14 §4.4): what it selects and in what order, and what it turns a run into — a
/// patch, a rejection, or a failure that has already poisoned the unit on the flow.
/// </summary>
public sealed class WriteExtensionRunnerTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Hooks_run_by_order_then_entity_before_every_entity_then_registration()
    {
        var ran = new ConcurrentQueue<string>();
        await using var world = RunnerWorld.Start(alvo => alvo
            .Entity("*", every => every.BeforeWrite("every", Record(ran, "every")))
            .Entity("tickets", t => t
                .BeforeWrite("late", Record(ran, "late"), configure: o => o.Order = 5)
                .BeforeWrite("first", Record(ran, "first"))
                .BeforeWrite("second", Record(ran, "second"))));

        await world.Runner.BeforeWriteAsync(world.Before(RunnerWorld.Ticket()), Ct);

        ran.ShouldBe(["first", "second", "every", "late"]);
    }

    [Fact]
    public async Task A_hook_whose_condition_does_not_hold_does_not_run()
    {
        var ran = new ConcurrentQueue<string>();
        await using var world = RunnerWorld.Start(alvo => alvo.Entity("tickets", t => t.BeforeWrite("resolved-only", Record(ran, "ran"), when: "new.status == 'resolved'")));

        await world.Runner.BeforeWriteAsync(world.Before(RunnerWorld.Ticket("open")), Ct);

        ran.ShouldBeEmpty();
    }

    [Fact]
    public async Task A_later_hook_sees_an_earlier_hook_s_set_and_the_patch_is_their_union()
    {
        string? seen = null;
        await using var world = RunnerWorld.Start(alvo => alvo.Entity("tickets", t => t
            .BeforeWrite("titles", (BeforeWrite<AlvoRecord> write) => { write.Set("title", "set"); return ValueTask.CompletedTask; })
            .BeforeWrite("codes", (BeforeWrite<AlvoRecord> write) => { seen = (string?)write.New["title"]; write.Set("code", "C-1"); return ValueTask.CompletedTask; })));

        var patch = await world.Runner.BeforeWriteAsync(world.Before(RunnerWorld.Ticket()), Ct);

        seen.ShouldBe("set");
        patch.ShouldBe(new Dictionary<string, object?> { ["title"] = "set", ["code"] = "C-1" });
    }

    [Fact]
    public async Task A_later_hook_s_condition_sees_an_earlier_hook_s_set_both_ways()
    {
        var ran = new ConcurrentQueue<string>();
        await using var world = RunnerWorld.Start(alvo => alvo.Entity("tickets", t => t
            .BeforeWrite("resolve", (BeforeWrite<AlvoRecord> write) => { write.Set("status", "resolved"); return ValueTask.CompletedTask; })
            .BeforeWrite("guard-resolved", Record(ran, "guard-resolved"), when: "new.status == 'resolved'")
            .BeforeWrite("guard-open", Record(ran, "guard-open"), when: "new.status == 'open'")));

        await world.Runner.BeforeWriteAsync(world.Before(RunnerWorld.Ticket("open")), Ct);

        ran.ShouldBe(["guard-resolved"], "a guard an earlier Set made applicable runs, and one it made inapplicable does not");
    }

    [Fact]
    public async Task A_rejection_stops_the_stage_discards_its_sets_and_is_logged_with_ids_and_the_code_only()
    {
        var ran = new ConcurrentQueue<string>();
        await using var world = RunnerWorld.Start(alvo => alvo.Entity("tickets", t => t
            .BeforeWrite("guard", (BeforeWrite<AlvoRecord> write) =>
            {
                write.Set("title", "discarded");
                write.Conflict("taken", "This title is taken by someone's secret project.", "title");
                return ValueTask.CompletedTask;
            })
            .BeforeWrite("after-guard", Record(ran, "after-guard"))));

        var rejection = await Should.ThrowAsync<AlvoWriteRejectedException>(() => world.Runner.BeforeWriteAsync(world.Before(RunnerWorld.Ticket()), Ct));

        (rejection.Kind, rejection.Code, rejection.Pointer, rejection.ExtensionId).ShouldBe((AlvoWriteRejectionKind.Conflict, "taken", "/title", "guard"));
        ran.ShouldBeEmpty();
        var logged = world.Rejections.Recorded.ShouldHaveSingleItem();
        (logged.Extension, logged.Entity, logged.Operation, logged.Kind, logged.Code, logged.Actor)
            .ShouldBe(("guard", "tickets", "create", "conflict", "taken", AlvoIdempotency.IdentityOf(RunnerWorld.Caller)));
    }

    [Fact]
    public async Task A_host_exception_is_a_hook_failure_that_poisons_the_unit_and_never_carries_the_host_s_message()
    {
        var unit = new FakeFlowUnit();
        await using var world = RunnerWorld.Start(alvo => alvo.Entity("tickets", t => t.BeforeWrite("boom", (BeforeWrite<AlvoRecord> _) =>
            throw new InvalidOperationException("SECRET connection string"))));
        using var flow = AlvoFlow.EnterUnit(unit, new AlvoProvenance());

        var failure = await Should.ThrowAsync<HookFailedException>(() => world.Runner.BeforeWriteAsync(world.Before(RunnerWorld.Ticket()), Ct));

        (failure.Extension, failure.Reason).ShouldBe(("boom", HookFailedException.Exception));
        failure.Message.ShouldNotContain("SECRET");
        unit.Poisoned.ShouldHaveSingleItem().ShouldBeSameAs(failure);
    }

    [Fact]
    public async Task A_caught_budget_refusal_still_fails_the_hook_and_poisons_the_unit()
    {
        var unit = new FakeFlowUnit();
        await using var world = RunnerWorld.Start(alvo => alvo.Entity("tickets", t => t.AfterWrite("greedy", async (AfterWrite<AlvoRecord> write, CancellationToken ct) =>
        {
            try
            {
                for (var call = 0; call < 51; call++)
                {
                    await write.Transaction.GetAsync("tickets", Guid.NewGuid(), ct);
                }
            }
            catch (AlvoBudgetExceededException)
            {
            }
        })));
        using var flow = AlvoFlow.EnterUnit(unit, new AlvoProvenance());

        var failure = await Should.ThrowAsync<HookFailedException>(() => world.Runner.AfterWriteAsync(world.Before(RunnerWorld.Ticket()) with { Saved = RunnerWorld.Ticket() }, Ct));

        failure.Reason.ShouldBe(HookFailedException.Budget);
        unit.Poisoned.ShouldContain(failure);
    }

    [Fact]
    public async Task A_hook_that_overruns_its_timeout_fails_with_timeout_even_when_it_catches_the_cancellation()
    {
        var clock = new FakeTimeProvider();
        var unit = new FakeFlowUnit();
        await using var world = RunnerWorld.Start(alvo => alvo.Entity("tickets", t => t.BeforeWrite("slow", async (BeforeWrite<AlvoRecord> write, CancellationToken ct) =>
        {
            clock.Advance(TimeSpan.FromMilliseconds(300));
            try
            {
                await write.Transaction.GetAsync("tickets", Guid.NewGuid(), ct);
            }
            catch (OperationCanceledException)
            {
            }
        })), clock);
        using var flow = AlvoFlow.EnterUnit(unit, new AlvoProvenance());

        var failure = await Should.ThrowAsync<HookFailedException>(() => world.Runner.BeforeWriteAsync(world.Before(RunnerWorld.Ticket()), Ct));

        failure.Reason.ShouldBe(HookFailedException.Timeout);
        unit.Blamed.ShouldHaveSingleItem().ShouldBeSameAs(failure);
    }

    [Fact]
    public async Task A_nested_write_to_the_row_in_its_own_hook_stage_is_a_self_write_even_when_caught()
    {
        var unit = new FakeFlowUnit();
        await using var world = RunnerWorld.Start(alvo => alvo.Entity("tickets", t => t.AfterWrite("echo", async (AfterWrite<AlvoRecord> write, CancellationToken ct) =>
        {
            try
            {
                await write.Transaction.UpdateAsync("tickets", (Guid)write.Saved["id"]!, new Dictionary<string, object?> { ["title"] = "again" }, cancellationToken: ct);
            }
            catch (HookFailedException)
            {
            }
        })));
        using var flow = AlvoFlow.EnterUnit(unit, new AlvoProvenance());

        var failure = await Should.ThrowAsync<HookFailedException>(() => world.Runner.AfterWriteAsync(world.Before(RunnerWorld.Ticket()) with { Saved = RunnerWorld.Ticket() }, Ct));

        failure.Reason.ShouldBe(HookFailedException.SelfWrite);
        unit.Poisoned.ShouldContain(failure);
    }

    [Fact]
    public async Task AsSystem_is_the_system_in_the_caller_s_tenant_with_the_caller_s_provenance_and_it_is_logged()
    {
        AlvoContext? seen = null;
        var logs = new Extensions.RecordingLoggerProvider();
        await using var world = RunnerWorld.Start(
            alvo => alvo.Entity("tickets", t => t.BeforeWrite("reader", (BeforeWrite<AlvoRecord> write) => { seen = write.Transaction.AsSystem().Context; return ValueTask.CompletedTask; })),
            services: services => services.AddLogging(logging => logging.AddProvider(logs)));
        var caller = RunnerWorld.Caller with { ActorKind = AlvoActorKind.User, Provenance = new AlvoProvenance { Depth = 3, CorrelationId = "c-1" } };

        await world.Runner.BeforeWriteAsync(world.Before(RunnerWorld.Ticket(), transaction: new FakeHookTransaction(caller)) with { Caller = caller }, Ct);

        (seen!.ActorKind, seen.Tenant, seen.Provenance).ShouldBe((AlvoActorKind.System, caller.Tenant, caller.Provenance));
        var line = logs.Messages.Single(message => message.Contains("acted as the system", StringComparison.Ordinal));
        line.ShouldContain(AlvoIdempotency.IdentityOf(caller));
        line.ShouldContain("correlation c-1");
    }

    [Fact]
    public async Task A_registered_hook_that_does_not_bind_the_current_catalog_fails_the_write_closed()
    {
        await using var world = RunnerWorld.Start(alvo => alvo.Entity("tickets", t => t.BeforeWrite("typo", (BeforeWrite<AlvoRecord> _) => ValueTask.CompletedTask, when: "new.stauts == 'x'")));

        world.Runner.RunsInTransaction("tickets", AlvoWriteOperation.Create).ShouldBeTrue("registrations, not bindings, decide that a unit is needed");
        (await Should.ThrowAsync<HookFailedException>(() => world.Runner.BeforeWriteAsync(world.Before(RunnerWorld.Ticket()), Ct))).Reason
            .ShouldBe(HookFailedException.Unbound);
    }

    [Fact]
    public async Task A_switched_off_validate_hook_is_skipped()
    {
        var ran = new ConcurrentQueue<string>();
        var store = Substitute.For<IDeliveryStore>();
        store.DisabledAsync(Arg.Any<CancellationToken>()).Returns(["check"]);
        await using var world = RunnerWorld.Start(
            alvo => alvo.Entity("tickets", t => t.Validate("check", (ValidateWrite<AlvoRecord> _) => { ran.Enqueue("check"); return ValueTask.CompletedTask; })),
            services: services => services.AddSingleton(store));

        await world.Runner.ValidateAsync(world.Before(RunnerWorld.Ticket()) with { Transaction = null, Services = null }, Ct);

        ran.ShouldBeEmpty();
    }

    [Fact]
    public async Task A_depth_refusal_escaping_a_hook_passes_unchanged_and_a_nested_rejection_is_the_hook_s_failure()
    {
        await using var world = RunnerWorld.Start(alvo => alvo.Entity("tickets", t => t
            .BeforeWrite("deep", (BeforeWrite<AlvoRecord> _) => throw new AlvoChainDepthExceededException("too deep"))
            .AfterWrite("nested", (AfterWrite<AlvoRecord> _) => throw new AlvoWriteRejectedException("from a nested write"))));

        await Should.ThrowAsync<AlvoChainDepthExceededException>(() => world.Runner.BeforeWriteAsync(world.Before(RunnerWorld.Ticket()), Ct));
        (await Should.ThrowAsync<HookFailedException>(() => world.Runner.AfterWriteAsync(world.Before(RunnerWorld.Ticket()) with { Saved = RunnerWorld.Ticket() }, Ct)))
            .ShouldSatisfyAllConditions(failure => failure.Extension.ShouldBe("nested"), failure => failure.Reason.ShouldBe(HookFailedException.Exception));
    }

    [Fact]
    public async Task Validate_hands_the_hook_a_session_one_hop_deeper()
    {
        AlvoContext? asked = null;
        var sessions = Substitute.For<IAlvoSessions>();
        sessions.For(Arg.Do<AlvoContext>(context => asked = context)).Returns(Substitute.For<IAlvoSession>());
        await using var world = RunnerWorld.Start(
            alvo => alvo.Entity("tickets", t => t.Validate("check", (ValidateWrite<AlvoRecord> write) => { _ = write.Data; return ValueTask.CompletedTask; })),
            services: services => services.AddSingleton(sessions));

        await world.Runner.ValidateAsync(world.Before(RunnerWorld.Ticket()) with { Transaction = null, Services = null }, Ct);

        asked!.Provenance.Depth.ShouldBe(1);
    }

    private static Func<BeforeWrite<AlvoRecord>, ValueTask> Record(ConcurrentQueue<string> ran, string name) => _ =>
    {
        ran.Enqueue(name);
        return ValueTask.CompletedTask;
    };
}
```

(`RecordingLoggerProvider` with `EventNames` and `Messages` (the formatted lines) — the core suite has a recording logger used by the extension tests (`grep -rn 'class Recording.*Logger' test/MMLib.Alvo.Tests`); use it, or add a 25-line provider that queues `eventId.Name`. `AddAlvo` registers `IAlvoSessions` with `TryAdd`, so the fact's registration before it wins.)

`test/MMLib.Alvo.Tests/Hooks/HookSetsTests.cs`:

```csharp
using MMLib.Alvo.Hooks;
using MMLib.Alvo.Hooks.Internal;
using MMLib.Alvo.Schema;

namespace MMLib.Alvo.Tests.Hooks;

/// <summary>What a <c>Set</c> may write (Ruling 16): declared, not maintained by Alvo, a value its field holds, inside its facets.</summary>
public sealed class HookSetsTests
{
    private static readonly EntitySchema _entity = new()
    {
        Name = "bookings",
        Fields =
        [
            new FieldSchema { Name = "id", Type = FieldType.Uuid },
            new FieldSchema { Name = "title", Type = FieldType.String, MaxLength = 10 },
            new FieldSchema { Name = "status", Type = FieldType.Enum, EnumValues = ["open", "closed"] },
            new FieldSchema { Name = "total", Type = FieldType.Decimal, Precision = 6, Scale = 2 },
            new FieldSchema { Name = "net", Type = FieldType.Integer, Rollup = new RollupSchema { From = "lines", Op = RollupOperation.Sum, Field = "amount" } },
            new FieldSchema { Name = "updated_at", Type = FieldType.DateTime, Nullable = true },
        ],
    };

    [Theory]
    [InlineData("nope", "x", "undeclared")]
    [InlineData("id", null, "maintained")]
    [InlineData("updated_at", null, "maintained")]
    [InlineData("net", 5L, "maintained")]
    [InlineData("title", 42L, "type")]
    [InlineData("status", "superadmin", "enum-value")]
    [InlineData("title", "far too long a title", "max-length")]
    public void A_set_it_may_not_write_names_its_field_and_problem(string field, object? value, string problem) =>
        HookSets.FirstProblem(new Dictionary<string, object?> { [field] = value }, _entity).ShouldBe((field, problem));

    [Fact]
    public void A_whole_number_into_a_decimal_field_and_a_declared_value_are_accepted_and_widened()
    {
        var sets = new Dictionary<string, object?> { ["total"] = 12L, ["status"] = "closed", ["title"] = "ok" };

        HookSets.FirstProblem(sets, _entity).ShouldBeNull();
        HookSets.Coerced(sets, _entity)["total"].ShouldBe(12m);
    }

    [Theory]
    [InlineData(3)]
    [InlineData((short)3)]
    public void An_untyped_int_or_short_set_is_a_whole_number_its_field_holds(object value)
    {
        var run = HookContextTests.Run(MMLib.Alvo.Data.AlvoWriteOperation.Create, null, HookContextTests.Booking("Ada", 1));
        new BeforeWrite<MMLib.Alvo.Data.AlvoRecord>(run).Set("total", value);

        HookSets.FirstProblem(run.Sets, _entity).ShouldBeNull();
        HookSets.Coerced(run.Sets, _entity)["total"].ShouldBe(3m);
    }

    [Fact]
    public void A_fractional_double_into_a_decimal_field_stays_refused() =>
        HookSets.FirstProblem(new Dictionary<string, object?> { ["total"] = 1.5d }, _entity).ShouldBe(("total", "type"));

    [Fact]
    public void A_tenant_is_accepted_on_a_scoped_entity_and_refused_on_a_global_one()
    {
        var tenant = new Dictionary<string, object?> { ["tenant_id"] = Guid.NewGuid() };

        HookSets.FirstProblem(tenant, _entity with { Tenancy = TenancyMode.Scoped }).ShouldBeNull();
        HookSets.FirstProblem(tenant, _entity with { Tenancy = TenancyMode.Global }).ShouldBe(("tenant_id", "undeclared"));
    }
}
```

(Construct `RollupSchema` with its real required members — read `RollupSchema` in Abstractions/Schema; the fact needs only `field.Rollup is not null`. Use the facet codes `RecordValidator.FacetViolation` really answers — read them (`max-length`, `enum-value` are what `DataApiEngineTests.HookFacets` expects) and keep the theory's strings equal to them.)

`test/MMLib.Alvo.Tests/Hooks/HookTransactionTests.cs`:

```csharp
using MMLib.Alvo.Data;
using MMLib.Alvo.Hooks.Internal;

namespace MMLib.Alvo.Tests.Hooks;

/// <summary>The hook's view of the unit (Rulings 10, 35): every call counted and run under the hook's token; writes marked.</summary>
public sealed class HookTransactionTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Every_call_runs_under_the_hook_s_token_and_counts_reads_and_the_system_view_too()
    {
        await using var world = RunnerWorld.Start(alvo => alvo.Entity("tickets", t => t.AfterWrite("audit", async (AfterWrite<AlvoRecord> write, CancellationToken ct) =>
        {
            await write.Transaction.GetAsync("tickets", Guid.NewGuid(), CancellationToken.None);
            await write.Transaction.AsSystem().QueryAsync(new AlvoQuery { Entity = "notes" }, ct);
            await write.Transaction.CreateAsync("notes", new Dictionary<string, object?> { ["text"] = "audited" }, ct);
        }, configure: o => o.MaxOperations = 3)));
        var inner = new FakeHookTransaction(RunnerWorld.Caller);

        var writer = await world.Runner.AfterWriteAsync(world.Before(RunnerWorld.Ticket(), transaction: inner) with { Saved = RunnerWorld.Ticket() }, Ct);

        writer.ShouldBe("audit");
        inner.Calls.Count.ShouldBe(2, "the system view's call went to its own fake");
        inner.LastToken.CanBeCanceled.ShouldBeTrue("the hook's token, not the CancellationToken.None the hook passed");
    }

    [Fact]
    public async Task A_hook_that_only_reads_and_publishes_wrote_nothing()
    {
        await using var world = RunnerWorld.Start(alvo => alvo.Entity("tickets", t => t.AfterWrite("reader", async (AfterWrite<AlvoRecord> write, CancellationToken ct) =>
        {
            await write.Transaction.GetAsync("tickets", Guid.NewGuid(), ct);
            await write.PublishAsync("tickets.seen", "tickets/1", null, ct);
        })));

        (await world.Runner.AfterWriteAsync(world.Before(RunnerWorld.Ticket()) with { Saved = RunnerWorld.Ticket() }, Ct)).ShouldBeNull();
    }
}
```

- [ ] **Step 2: Run them to verify they fail**

Run: `dotnet test --project test/MMLib.Alvo.Tests --filter-class '*WriteExtensionRunnerTests'` (and the other three)
Expected: FAIL — no `IWriteExtensionRunner` is registered.

- [ ] **Step 3: Write the runner's helpers**

`HookRun.cs` — `Transaction` becomes `internal IAlvoTransaction? Transaction { get; private set; } = transaction;` and gains `internal void Attach(IAlvoTransaction view) => Transaction = view;`.

(`HeldRows` is Task 1's, in Abstractions — the driver holds a batch's rows with it too, Ruling 43.)

`src/MMLib.Alvo/Hooks/Internal/HookSets.cs`:

```csharp
using MMLib.Alvo.Api.Internal;
using MMLib.Alvo.Rules.Internal;
using MMLib.Alvo.Schema;

namespace MMLib.Alvo.Hooks.Internal;

/// <summary>
/// What a hook's <c>Set</c> may write (Ruling 16): a declared field — or <c>tenant_id</c>, judged by the rules afterwards — that
/// Alvo does not maintain, a value of the field's type, inside its facets (Ruling V, through <see cref="MutationTarget"/>).
/// </summary>
internal static class HookSets
{
    /// <summary>
    /// <paramref name="sets"/> with every lossless widening applied (Ruling 47): an integral CLR value is already a <c>long</c>
    /// (<c>RecordWriter.Field</c>), a <c>float</c> becomes a <c>double</c>, and a whole number into a <c>decimal</c> field
    /// becomes a <c>decimal</c>. Nothing lossy is converted; it stays for <see cref="FirstProblem"/> to refuse.
    /// </summary>
    internal static Dictionary<string, object?> Coerced(IReadOnlyDictionary<string, object?> sets, EntitySchema entity) =>
        sets.ToDictionary(entry => entry.Key, entry => Coerce(entity.Fields.FirstOrDefault(field => field.Name == entry.Key), entry.Value), StringComparer.Ordinal);

    internal static (string Field, string Problem)? FirstProblem(IReadOnlyDictionary<string, object?> sets, EntitySchema entity)
    {
        FormatCatalog? formats = null;
        foreach (var (name, value) in Coerced(sets, entity))
        {
            if (Problem(name, value, entity, () => formats ??= FormatCatalog.Build([entity])) is { } problem)
            {
                return (name, problem);
            }
        }

        return null;
    }

    private static object? Coerce(FieldSchema? field, object? value) => (field?.Type, value) switch
    {
        (_, float single) => (double)single,
        (FieldType.Decimal, long whole) => (decimal)whole,
        _ => value,
    };

    private static string? Problem(string name, object? value, EntitySchema entity, Func<FormatCatalog> formats)
    {
        if (name == AlvoManagedColumns.TenantId)
        {
            return entity.Tenancy != TenancyMode.Scoped ? "undeclared" : value is null or Guid ? null : "type";
        }

        if (entity.Fields.FirstOrDefault(field => field.Name == name) is not { } field)
        {
            return "undeclared";
        }

        return Maintained(field, entity) ? "maintained"
            : !Holds(field, value) ? "type"
            : new MutationTarget(field, formats(), Disclosable: true).Violation(value)?.Code;
    }

    private static bool Maintained(FieldSchema field, EntitySchema entity) =>
        field.Name == AlvoManagedColumns.Id
        || field.Name == AlvoManagedColumns.DeletedAt
        || AlvoManagedColumns.Audit.Contains(field.Name)
        || field.Name == AlvoManagedColumns.VersionColumn(entity)
        || field.ComputedExpression is not null
        || field.Rollup is not null;

    private static bool Holds(FieldSchema field, object? value) => value is null || FieldClrType.Of(field).IsInstanceOfType(value);
}
```

`src/MMLib.Alvo/Hooks/Internal/HookTelemetry.cs`:

```csharp
using MMLib.Alvo.Events.Internal;
using MMLib.Alvo.Rules;
using System.Diagnostics;
using System.Diagnostics.Metrics;

namespace MMLib.Alvo.Hooks.Internal;

/// <summary>
/// One <c>alvo.hook</c> span per run on <c>MMLib.Alvo.Hooks</c> — its id in <c>alvo.subscriber</c>, the tag the test host's
/// recorder reads (Ruling 27) — and the hook meters of spec 2026-10-14 §4.14.
/// </summary>
internal static class HookTelemetry
{
    internal const string MeterName = "MMLib.Alvo.Hooks";
    internal const string SpanName = "alvo.hook";
    internal const string DurationName = "alvo.hook.duration";
    internal const string RejectionsName = "alvo.hook.rejections";
    internal const string OperationsName = "alvo.hook.operations";
    internal const string SystemAccessName = "alvo.hook.system_access";
    internal const string SubscriberTag = AlvoHookTelemetry.SubscriberTag;
    internal const string OutcomeTag = AlvoHookTelemetry.OutcomeTag;
    internal const string Succeeded = "succeeded";
    internal const string Rejected = "rejected";
    internal const string Failed = "failed";

    private static readonly ActivitySource _source = new(AlvoHookTelemetry.SourceName);
    private static readonly Meter _meter = new(MeterName);
    private static readonly Histogram<double> _duration = _meter.CreateHistogram<double>(DurationName, "ms", "How long one write-hook run took.");
    private static readonly Counter<long> _rejections = _meter.CreateCounter<long>(RejectionsName, description: "Write-hook runs that rejected the write.");
    private static readonly Counter<long> _operations = _meter.CreateCounter<long>(OperationsName, description: "Calls write hooks made through the write's transaction.");
    private static readonly Counter<long> _systemAccess = _meter.CreateCounter<long>(SystemAccessName, description: "Times a write hook acted as the system.");

    internal static Activity? Start(string id, WriteInvocation invocation, WriteHookStage stage)
    {
        var activity = _source.StartActivity(SpanName, ActivityKind.Internal);
        if (activity is { IsAllDataRequested: true })
        {
            activity.DisplayName = $"{SpanName} {id}";
            activity.SetTag(SubscriberTag, id);
            activity.SetTag("alvo.entity", invocation.Entity);
            activity.SetTag("alvo.operation", invocation.Operation.ToString().ToLowerInvariant());
            activity.SetTag("alvo.stage", WriteHookBinding.KindOf(stage));
        }

        return activity;
    }

    internal static void Finish(Activity? activity, string id, string outcome, TimeSpan elapsed, int operations)
    {
        var subscriber = new KeyValuePair<string, object?>(SubscriberTag, id);
        _duration.Record(elapsed.TotalMilliseconds, subscriber, new KeyValuePair<string, object?>(OutcomeTag, outcome));
        if (operations > 0)
        {
            _operations.Add(operations, subscriber);
        }

        if (outcome == Rejected)
        {
            _rejections.Add(1, subscriber);
        }

        activity?.SetTag(OutcomeTag, outcome);
        if (outcome == Failed)
        {
            activity?.SetStatus(ActivityStatusCode.Error);
        }
    }

    internal static void SystemAccess(string id) => _systemAccess.Add(1, new KeyValuePair<string, object?>(SubscriberTag, id));
}
```

`src/MMLib.Alvo/Hooks/Internal/HookLog.cs`:

```csharp
using Microsoft.Extensions.Logging;

namespace MMLib.Alvo.Hooks.Internal;

/// <summary>The write hooks' log lines: ids, reasons and exception types — never a value, a payload or a host's message.</summary>
internal static partial class HookLog
{
    [LoggerMessage(Level = LogLevel.Error, Message = "Write hook {Extension} failed ({Reason}) with {ExceptionType}; the write was rolled back.")]
    internal static partial void HookFailed(ILogger logger, string extension, string reason, string exceptionType);

    [LoggerMessage(Level = LogLevel.Error, Message = "Write hook {Extension} set {Field}, which it may not set ({Problem}); the write was rolled back.")]
    internal static partial void HookSetRefused(ILogger logger, string extension, string field, string problem);

    [LoggerMessage(
        Level = LogLevel.Warning,
        Message = "Write hook {Extension} acted as the system in tenant {Tenant} on {Entity} ({Operation}) for {Actor}, correlation {CorrelationId}.")]
    internal static partial void ExtensionSystemAccess(
        ILogger logger, string extension, string tenant, string entity, string operation, string actor, string correlationId);
}
```

`src/MMLib.Alvo/Hooks/Internal/HookTransaction.cs`:

```csharp
using Microsoft.Extensions.Logging;
using MMLib.Alvo.Data;
using MMLib.Alvo.Events;
using MMLib.Alvo.Schema;

namespace MMLib.Alvo.Hooks.Internal;

/// <summary>
/// The write's transaction as one hook run sees it (Rulings 2, 10, 11, 35): every call counted against the run's budget and run
/// under its token; a write to a row in a hook stage refused as a self-write; <see cref="AsSystem"/> logged and metered. A budget
/// or self-write refusal is recorded on the run and poisons the unit, so catching it changes nothing.
/// </summary>
internal sealed class HookTransaction(IAlvoTransaction inner, HookRun run, BoundWriteHook hook, ILogger logger) : IAlvoTransaction, IAlvoSessionSchema
{
    public AlvoContext Context => inner.Context;

    public EntitySchema? EntityOf(string entity) => (inner as IAlvoSessionSchema)?.EntityOf(entity);

    public Task<AlvoPage> QueryAsync(AlvoQuery query, CancellationToken cancellationToken = default) =>
        ReadAsync(token => inner.QueryAsync(query, token), cancellationToken);

    public Task<AlvoRecord?> GetAsync(string entity, Guid id, CancellationToken cancellationToken = default) =>
        ReadAsync(token => inner.GetAsync(entity, id, token), cancellationToken);

    public Task<AlvoRecord> CreateAsync(string entity, IReadOnlyDictionary<string, object?> values, CancellationToken cancellationToken = default) =>
        WriteAsync(entity, [], token => inner.CreateAsync(entity, values, token), cancellationToken);

    public Task<AlvoRecord> UpdateAsync(
        string entity, Guid id, IReadOnlyDictionary<string, object?> values, AlvoPrecondition? precondition = null, CancellationToken cancellationToken = default) =>
        WriteAsync(entity, [id], token => inner.UpdateAsync(entity, id, values, precondition, token), cancellationToken);

    public Task<AlvoReplaceResult> ReplaceAsync(
        string entity, Guid id, IReadOnlyDictionary<string, object?> values, AlvoPrecondition? precondition = null, CancellationToken cancellationToken = default) =>
        WriteAsync(entity, [id], token => inner.ReplaceAsync(entity, id, values, precondition, token), cancellationToken);

    public Task DeleteAsync(string entity, Guid id, AlvoPrecondition? precondition = null, CancellationToken cancellationToken = default) =>
        WriteAsync(entity, [id], async token =>
        {
            await inner.DeleteAsync(entity, id, precondition, token).ConfigureAwait(false);
            return true;
        }, cancellationToken);

    public Task<AlvoBatchResult> CreateManyAsync(
        string entity, IReadOnlyList<IReadOnlyDictionary<string, object?>> rows, CancellationToken cancellationToken = default) =>
        WriteAsync(entity, [], token => inner.CreateManyAsync(entity, rows, token), cancellationToken);

    public Task<AlvoBatchResult> UpdateManyAsync(string entity, IReadOnlyList<AlvoRowPatch> rows, CancellationToken cancellationToken = default) =>
        WriteAsync(entity, [.. rows.Select(row => row.Id)], token => inner.UpdateManyAsync(entity, rows, token), cancellationToken);

    public Task<AlvoBatchResult> DeleteManyAsync(string entity, IReadOnlyList<Guid> ids, CancellationToken cancellationToken = default) =>
        WriteAsync(entity, ids, token => inner.DeleteManyAsync(entity, ids, token), cancellationToken);

    public Task PublishAsync(string type, string subject, IReadOnlyDictionary<string, object?>? data, CancellationToken cancellationToken = default) =>
        ReadAsync(async token =>
        {
            await inner.PublishAsync(type, subject, data, token).ConfigureAwait(false);
            return true;
        }, cancellationToken);

    public IAlvoTransaction AsSystem()
    {
        HookTelemetry.SystemAccess(hook.Id);
        var caller = run.Invocation.Caller;
        HookLog.ExtensionSystemAccess(
            logger, hook.Id, caller.Tenant?.Value.ToString() ?? "-", run.Invocation.Entity, run.Invocation.Operation.ToString().ToLowerInvariant(),
            AlvoIdempotency.IdentityOf(caller), AlvoEventProvenance.Effective(caller).CorrelationId ?? "-");

        return new HookTransaction(inner.AsSystem(), run, hook, logger);
    }

    private Task<T> ReadAsync<T>(Func<CancellationToken, Task<T>> call, CancellationToken cancellationToken)
    {
        Count();
        return CallAsync(call, cancellationToken);
    }

    private Task<T> WriteAsync<T>(string entity, IReadOnlyList<Guid> rows, Func<CancellationToken, Task<T>> call, CancellationToken cancellationToken)
    {
        if (rows.Any(row => HeldRows.Holds(entity, row)))
        {
            throw Poisoned(HookFailedException.SelfWrite);
        }

        Count();
        run.MarkWrote();
        return CallAsync(call, cancellationToken);
    }

    private void Count()
    {
        if (run.Count() > hook.Registration.Settings.MaxOperations)
        {
            Poisoned(HookFailedException.Budget);
            throw AlvoBudgetExceededException.For(hook.Id, hook.Registration.Settings.MaxOperations);
        }
    }

    private HookFailedException Poisoned(string reason)
    {
        var failure = new HookFailedException(hook.Id, reason);
        run.Fail(failure);
        AlvoFlow.Unit?.Poison(failure);

        return failure;
    }

    private async Task<T> CallAsync<T>(Func<CancellationToken, Task<T>> call, CancellationToken cancellationToken)
    {
        if (!cancellationToken.CanBeCanceled || cancellationToken == run.Token)
        {
            return await call(run.Token).ConfigureAwait(false);
        }

        using var linked = CancellationTokenSource.CreateLinkedTokenSource(run.Token, cancellationToken);
        return await call(linked.Token).ConfigureAwait(false);
    }
}
```

- [ ] **Step 4: Write the runner**

`src/MMLib.Alvo/Hooks/Internal/WriteExtensionRunner.cs`:

```csharp
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using MMLib.Alvo.Data;
using MMLib.Alvo.Events;
using MMLib.Alvo.Expressions.Internal;
using MMLib.Alvo.Extensions.Internal;
using MMLib.Alvo.Rules;
using MMLib.Alvo.Schema;
using System.Diagnostics;

namespace MMLib.Alvo.Hooks.Internal;

/// <summary>
/// The core's <see cref="IWriteExtensionRunner"/> (spec 2026-10-14 §4.4): the bound hooks of one entity and stage, in order,
/// each whose <c>when</c> holds run once and awaited to its end (Ruling 11), and turned into a patch, a rejection, or a failure
/// that has already poisoned the unit on the flow.
/// </summary>
/// <remarks>
/// <b>Nothing it holds reaches the network:</b> its own dependencies are the catalog, the binder, the clock, a logger and the
/// root provider it resolves the session factory, the delivery store and the rejection log from — the architecture fact over
/// <c>WriteExtensionRunner</c>'s constructor (§4.4.5) pins that list.
/// </remarks>
internal sealed class WriteExtensionRunner(
    ExtensionRegistry registry, ExtensionBinder binder, IPolicyCatalogProvider catalogs, IServiceProvider services, TimeProvider time,
    ILogger<WriteExtensionRunner> logger) : IWriteExtensionRunner
{
    private static readonly IReadOnlyDictionary<string, object?> _none = new Dictionary<string, object?>(StringComparer.Ordinal);

    public bool Validates(string entity, AlvoWriteOperation operation) => Registered(entity, operation, inTransaction: false);

    public bool RunsInTransaction(string entity, AlvoWriteOperation operation) => Registered(entity, operation, inTransaction: true);

    public async Task ValidateAsync(WriteInvocation invocation, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(invocation);
        var hooks = Selected(invocation, WriteHookStage.Validate);
        if (hooks.Count == 0)
        {
            return;
        }

        var disabled = await DisabledAsync(cancellationToken).ConfigureAwait(false);
        await using var scope = services.GetRequiredService<IServiceScopeFactory>().CreateAsyncScope();
        var scoped = invocation with { Services = scope.ServiceProvider };
        foreach (var hook in hooks.Where(hook => !disabled.Contains(hook.Id) && Fires(hook, scoped, WriteHookStage.Validate)))
        {
            await RunAsync(hook, scoped, WriteHookStage.Validate, cancellationToken).ConfigureAwait(false);
        }
    }

    public async Task<IReadOnlyDictionary<string, object?>> BeforeWriteAsync(WriteInvocation invocation, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(invocation);
        var stage = invocation.Operation == AlvoWriteOperation.Delete ? WriteHookStage.BeforeDelete : WriteHookStage.BeforeWrite;
        var patch = new Dictionary<string, object?>(StringComparer.Ordinal);
        var current = invocation;
        foreach (var hook in Selected(invocation, stage))
        {
            if (!Fires(hook, current, stage))
            {
                continue;
            }

            var sets = (await RunAsync(hook, current, stage, cancellationToken).ConfigureAwait(false)).Sets;
            foreach (var (field, value) in sets)
            {
                patch[field] = value;
            }

            current = current with { New = Patched(current.New, sets) };
        }

        return patch.Count == 0 ? _none : patch;
    }

    public async Task<string?> AfterWriteAsync(WriteInvocation invocation, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(invocation);
        var stage = invocation.Operation == AlvoWriteOperation.Delete ? WriteHookStage.AfterDelete : WriteHookStage.AfterWrite;
        string? writer = null;
        foreach (var hook in Selected(invocation, stage).Where(hook => Fires(hook, invocation, stage)))
        {
            writer = (await RunAsync(hook, invocation, stage, cancellationToken).ConfigureAwait(false)).Wrote ? hook.Id : writer;
        }

        return writer;
    }

    private bool Registered(string entity, AlvoWriteOperation operation, bool inTransaction) =>
        registry.WriteHooks.Any(hook => hook.InTransaction == inTransaction && hook.Covers(entity) && hook.RunsOn(operation));

    /// <summary>
    /// The bound hooks of the stage, in order; a registered hook not bound fails the write closed (Ruling 7). Conditions are
    /// judged by the caller, per hook, against the row as the earlier hooks left it (Ruling 39).
    /// </summary>
    private List<BoundWriteHook> Selected(WriteInvocation invocation, WriteHookStage stage)
    {
        var registered = registry.WriteHooks.Where(hook => hook.Stage == stage && hook.Covers(invocation.Entity)).Select(hook => hook.Id).ToHashSet(StringComparer.Ordinal);
        if (registered.Count == 0)
        {
            return [];
        }

        var bindings = catalogs.Current is { } catalog ? binder.For(catalog) : ExtensionBindings.None;
        if (registered.FirstOrDefault(id => bindings.WriteHook(id) is null) is { } unbound)
        {
            throw Fail(unbound, HookFailedException.Unbound, cause: null);
        }

        return [.. bindings.WriteHooks.Where(hook => registered.Contains(hook.Id))];
    }

    private static bool Fires(BoundWriteHook hook, WriteInvocation invocation, WriteHookStage stage) =>
        hook.ConditionFor(invocation.Entity) is not { } condition
        || CelInterpreter.EvaluatePredicate(condition, Current(invocation, stage), invocation.Old, invocation.Caller);

    private static AlvoRecord Current(WriteInvocation invocation, WriteHookStage stage) => stage switch
    {
        WriteHookStage.AfterWrite => invocation.Saved!,
        WriteHookStage.BeforeDelete or WriteHookStage.AfterDelete => invocation.Old!,
        _ => invocation.New!,
    };

    private async Task<HookRun> RunAsync(BoundWriteHook hook, WriteInvocation invocation, WriteHookStage stage, CancellationToken cancellationToken)
    {
        using var deadline = new CancellationTokenSource(hook.Registration.Settings.Timeout, time);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, deadline.Token);
        var run = NewRun(hook, invocation, stage, linked.Token);
        using var activity = HookTelemetry.Start(hook.Id, invocation, stage);
        var started = time.GetTimestamp();
        try
        {
            using (HeldRows.Hold(invocation.Entity, HeldRows.RowOf(invocation)))
            {
                await hook.Registration.Invoker.InvokeAsync(invocation.Services!, run, linked.Token).ConfigureAwait(false);
            }
        }
        catch (Exception escaped)
        {
            HookTelemetry.Finish(activity, hook.Id, HookTelemetry.Failed, time.GetElapsedTime(started), run.Operations);
            throw Escaped(run, escaped, deadline.IsCancellationRequested, cancellationToken);
        }

        return await ConcludedAsync(run, hook, activity, time.GetElapsedTime(started), deadline.IsCancellationRequested).ConfigureAwait(false);
    }

    /// <summary>What an exception escaping a hook becomes (Ruling 12): its run's own failure, depth or a nested hook's failure unchanged, a timeout, or <c>exception</c>.</summary>
    private Exception Escaped(HookRun run, Exception escaped, bool deadlinePassed, CancellationToken cancellationToken) =>
        run.Failure is { } failure ? failure
        : escaped is AlvoChainDepthExceededException or HookFailedException ? escaped
        : escaped is OperationCanceledException && cancellationToken.IsCancellationRequested ? escaped
        : deadlinePassed ? TimedOut(run)
        : Fail(run.Id, HookFailedException.Exception, escaped);

    private async Task<HookRun> ConcludedAsync(HookRun run, BoundWriteHook hook, Activity? activity, TimeSpan elapsed, bool deadlinePassed)
    {
        var failure = run.Failure
            ?? (deadlinePassed || elapsed > hook.Registration.Settings.Timeout ? TimedOut(run) : null)
            ?? InvalidSet(run);
        if (failure is not null)
        {
            HookTelemetry.Finish(activity, hook.Id, HookTelemetry.Failed, elapsed, run.Operations);
            throw failure;
        }

        if (run.Rejections.Winner() is { } rejection)
        {
            HookTelemetry.Finish(activity, hook.Id, HookTelemetry.Rejected, elapsed, run.Operations);
            await RecordAsync(run, rejection).ConfigureAwait(false);
            throw rejection;
        }

        HookTelemetry.Finish(activity, hook.Id, HookTelemetry.Succeeded, elapsed, run.Operations);
        return run;
    }

    private HookRun NewRun(BoundWriteHook hook, WriteInvocation invocation, WriteHookStage stage, CancellationToken token)
    {
        var data = stage == WriteHookStage.Validate ? services.GetRequiredService<IAlvoSessions>().For(OneHopDeeper(invocation.Caller)) : null;
        var run = new HookRun(hook.Id, invocation, Pointable(invocation.Entity), transaction: null, data, token);
        if (invocation.Transaction is { } inner)
        {
            run.Attach(new HookTransaction(inner, run, hook, logger));
        }

        return run;
    }

    private HookFailedException? InvalidSet(HookRun run)
    {
        if (run.Sets.Count == 0 || Entity(run.Invocation.Entity) is not { } entity)
        {
            return null;
        }

        if (HookSets.FirstProblem(run.Sets, entity) is not { } problem)
        {
            run.Replace(HookSets.Coerced(run.Sets, entity));
            return null;
        }

        HookLog.HookSetRefused(logger, run.Id, Hidden(entity.Name).Contains(problem.Field) ? "a hidden field" : problem.Field, problem.Problem);
        return Fail(run.Id, HookFailedException.InvalidSet, cause: null);
    }

    private HookFailedException TimedOut(HookRun run)
    {
        var failure = new HookFailedException(run.Id, HookFailedException.Timeout);
        AlvoFlow.Unit?.Blame(failure);
        HookLog.HookFailed(logger, run.Id, HookFailedException.Timeout, "-");

        return failure;
    }

    private HookFailedException Fail(string id, string reason, Exception? cause)
    {
        var failure = new HookFailedException(id, reason);
        AlvoFlow.Unit?.Poison(failure);
        HookLog.HookFailed(logger, id, reason, cause?.GetType().FullName ?? "-");

        return failure;
    }

    private async Task RecordAsync(HookRun run, AlvoWriteRejectedException rejection)
    {
        if (services.GetService<IHookRejectionLog>() is { } log)
        {
            await log.RecordAsync(
                new HookRejection(
                    time.GetUtcNow(), run.Id, run.Invocation.Entity, run.Invocation.Operation.ToString().ToLowerInvariant(),
                    rejection.Kind.ToString().ToLowerInvariant(), rejection.Code, AlvoIdempotency.IdentityOf(run.Invocation.Caller)),
                CancellationToken.None).ConfigureAwait(false);
        }
    }

    private async Task<HashSet<string>> DisabledAsync(CancellationToken cancellationToken) =>
        services.GetService<IDeliveryStore>() is { } store
            ? (await store.DisabledAsync(cancellationToken).ConfigureAwait(false)).ToHashSet(StringComparer.Ordinal)
            : [];

    private IReadOnlySet<string> Hidden(string entity) =>
        catalogs.Current is { } catalog && catalog.TryGetEntity(entity, out var policy)
            ? EntityPolicy.Flagged(policy.Hidden)
            : new HashSet<string>(StringComparer.Ordinal);

    /// <summary>The fields a rejection may point at: declared and never hidden (Ruling 53).</summary>
    private IReadOnlySet<string> Pointable(string entity)
    {
        var hidden = Hidden(entity);
        return Entity(entity)?.Fields.Select(field => field.Name).Where(name => !hidden.Contains(name)).ToHashSet(StringComparer.Ordinal)
            ?? new HashSet<string>(StringComparer.Ordinal);
    }

    private EntitySchema? Entity(string entity) =>
        catalogs.Current?.Schema.Entities.FirstOrDefault(candidate => string.Equals(candidate.Name, entity, StringComparison.Ordinal));

    private static AlvoRecord? Patched(AlvoRecord? record, IReadOnlyDictionary<string, object?> sets)
    {
        var patched = record;
        foreach (var (field, value) in sets)
        {
            patched = patched?.With(field, value);
        }

        return patched;
    }

    private static AlvoContext OneHopDeeper(AlvoContext context) =>
        context with { Provenance = context.Provenance with { Depth = AlvoEventProvenance.Effective(context).Depth + 1 } };
}
```

(`CelInterpreter` lives in `MMLib.Alvo.Expressions.Internal`; `EntityPolicy` in `MMLib.Alvo.Rules`; the `Fail` called from `Selected` for an unbound hook poisons the unit too — fail closed in a `TransactAsync` unit that catches it.)

`src/MMLib.Alvo/Hooks/Internal/Setup.cs`:

```csharp
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using MMLib.Alvo.Rules;

namespace MMLib.Alvo.Hooks.Internal;

/// <summary>Registers the core's write-hook runner — the one <see cref="IWriteExtensionRunner"/> a relational driver resolves.</summary>
internal static class HooksSetup
{
    internal static IServiceCollection AddAlvoWriteHooks(this IServiceCollection services)
    {
        services.TryAddSingleton<IWriteExtensionRunner, WriteExtensionRunner>();
        return services;
    }
}
```

`AlvoServiceCollectionExtensions.cs` calls `services.AddAlvoWriteHooks();` right after `AddAlvoExtensions()`. `FakeFlowUnit` gains `private readonly List<Exception> _blamed = []; internal IReadOnlyList<Exception> Blamed => _blamed; public void Blame(Exception cause) => _blamed.Add(cause);` (replacing Task 1's minimal member).

The architecture fact §4.4.5 asks for: add to the core's architecture suite (beside the fact that pins `BeforeHookRunner`'s dependencies — `grep -rn 'BeforeHookRunner' test/MMLib.Alvo.Tests/Architecture*`), one fact asserting `WriteExtensionRunner`'s constructor parameters are exactly `ExtensionRegistry`, `ExtensionBinder`, `IPolicyCatalogProvider`, `IServiceProvider`, `TimeProvider`, `ILogger<WriteExtensionRunner>` — and that `HookDependencies.IsBanned` holds for none of them except `IServiceProvider` (the root provider resolves only `IServiceScopeFactory`, `IAlvoSessions`, `IDeliveryStore` and `IHookRejectionLog`, read where each is used).

- [ ] **Step 5: Run the core facts and ring1**

Run: `dotnet test --project test/MMLib.Alvo.Tests --filter-class '*WriteExtensionRunnerTests'`, `*HookTransactionTests`, `*HookSetsTests`, then the architecture suite.
Expected: PASS. `scripts/test-ring1`.

- [ ] **Step 6: Commit**

```bash
dotnet format whitespace MMLib.Alvo.slnx --include src/MMLib.Alvo/Hooks/Internal/WriteExtensionRunner.cs src/MMLib.Alvo/Hooks/Internal/HookTransaction.cs src/MMLib.Alvo/Hooks/Internal/HookSets.cs src/MMLib.Alvo/Hooks/Internal/HookTelemetry.cs src/MMLib.Alvo/Hooks/Internal/HookLog.cs src/MMLib.Alvo/Hooks/Internal/Setup.cs src/MMLib.Alvo/Hooks/Internal/HookRun.cs src/MMLib.Alvo/AlvoServiceCollectionExtensions.cs test/MMLib.Alvo.Tests/Hooks/WriteExtensionRunnerTests.cs test/MMLib.Alvo.Tests/Hooks/HookTransactionTests.cs test/MMLib.Alvo.Tests/Hooks/HookSetsTests.cs test/MMLib.Alvo.Tests/Hooks/RunnerWorld.cs test/MMLib.Alvo.Tests/Data/FakeFlowUnit.cs
scripts/test-ring1
git add src/MMLib.Alvo/Hooks src/MMLib.Alvo/AlvoServiceCollectionExtensions.cs test/MMLib.Alvo.Tests
git commit -m "feat(hooks): run write hooks under a budget, a timeout and the self-write rule

Claude-Session: https://claude.ai/code/session_01NJAtafM29iddLwfhb5gnmB"
```

---

## Task 9: Over HTTP — `hook-failed`, the three rejection documents, and the status facts on both engines

**Files:**
- Create: `test/_shared/api/DataApiEngineTests.WriteHooks.cs`, `test/MMLib.Alvo.Api.Tests/descriptors/hook-guards.alvo.json`
- Modify: `src/MMLib.Alvo/Api/AlvoProblemTypes.cs`, `src/MMLib.Alvo/Api/Internal/ProblemResultFactory.cs`, `src/MMLib.Alvo/Api/Internal/AlvoExceptionHandler.cs`, `test/MMLib.Alvo.Tests/PublicApi.MMLib.Alvo.verified.txt`
- Test: the new partial runs in `MMLib.Alvo.Api.Tests` (SQLite) and `MMLib.Alvo.Api.Tests.Integration` (PostgreSQL), as every `DataApiEngineTests` partial does

**Interfaces:**
- Consumes: Task 5's `AlvoWriteRejectedException`; Task 8's `HookFailedException`.
- Produces: `AlvoProblemTypes.HookFailed`; `ProblemResultFactory.HookRejected(AlvoWriteRejectedException)`, `ProblemResultFactory.HookFailed(string extension, string reason)`; `PortRefusalFor` answers a rejection (so the Data API and the command filter both map it); `AlvoExceptionHandler` answers a `HookFailedException` anywhere in the chain.

- [ ] **Step 1: Write the failing facts**

`test/MMLib.Alvo.Api.Tests/descriptors/hook-guards.alvo.json`:

```json
{
  "$schema": "https://alvo.dev/schema/v1/project.json",
  "apiVersion": "alvo.dev/v1",
  "name": "hook-guards",
  "description": "One entity guarded by host write hooks the facts register: a status enum, an owner the rules pin to the caller, and a score hidden from writers. Every answer must be the same on every engine.",
  "auth": { "providers": ["local"], "roles": ["writer", "auditor"] },
  "access": { "viewer": "'writer' in @user.roles" },
  "entities": {
    "orders": {
      "fields": {
        "title": { "type": "string", "required": true, "maxLength": 40 },
        "status": { "type": "enum", "values": ["open", "closed"] },
        "owner_id": { "type": "uuid" },
        "score": { "type": "integer", "hidden": "!('auditor' in @user.roles)" },
        "reference": { "type": "string", "maxLength": 20, "readOnly": true }
      },
      "rules": {
        "list": "'writer' in @user.roles",
        "get": "'writer' in @user.roles",
        "create": "'writer' in @user.roles && owner_id == @user.id",
        "update": "'writer' in @user.roles && owner_id == @user.id",
        "delete": "'writer' in @user.roles"
      }
    }
  }
}
```

`test/_shared/api/DataApiEngineTests.WriteHooks.cs`:

```csharp
using MMLib.Alvo.Data;
using MMLib.Alvo.Hooks;
using System.Net;
using System.Text.Json.Nodes;

namespace MMLib.Alvo.Api.Tests;

/// <summary>
/// Host write hooks over HTTP (spec 2026-10-14 §7 HX-09 acceptance (1)–(3)): each stage's rejection answers its status and
/// pointer with nothing written, a <c>Set</c> the rules or the facets refuse answers the same on every engine, and a hidden
/// field is never pointed at.
/// </summary>
public abstract partial class DataApiEngineTests
{
    private static readonly TestApiKey _orderWriter = new("order-writer", ["writer"], ["orders:read", "orders:write"]);

    public static TheoryData<string, HttpStatusCode, string, string> StageRejections => new()
    {
        { "validate", HttpStatusCode.UnprocessableEntity, "validation", "/title" },
        { "before", HttpStatusCode.Conflict, "conflict", "/title" },
        { "after", HttpStatusCode.Forbidden, "forbidden", "" },
    };

    [Theory]
    [MemberData(nameof(StageRejections))]
    public async Task A_rejection_at_each_write_stage_answers_its_status_and_pointer_and_writes_nothing_on_every_engine(
        string title, HttpStatusCode status, string type, string pointer)
    {
        await using var world = await StartHookGuardsAsync();

        using var refused = await world.SendAsync(HttpMethod.Post, "/api/orders", _orderWriter, body: Order(title));

        refused.StatusCode.ShouldBe(status, await refused.ReadTextAsync());
        (await refused.ReadProblemTypeAsync()).ShouldBe(type);
        (await refused.ReadViolationsAsync()).ShouldHaveSingleItem().Pointer.ShouldBe(pointer);
        (await refused.ReadJsonObjectAsync())["extensions"]!.AsArray().Select(node => node!.GetValue<string>()).ShouldBe([$"{title}-guard"]);
        (await world.CountRowsAsync("orders")).ShouldBe(0);
        (await world.CountRowsAsync("alvo_outbox")).ShouldBe(0);
    }

    [Fact]
    public async Task A_rejection_before_a_delete_answers_409_and_keeps_the_row_on_every_engine()
    {
        await using var world = await StartHookGuardsAsync();
        using var created = await world.SendAsync(HttpMethod.Post, "/api/orders", _orderWriter, body: Order("keep"));
        var id = (await created.ReadJsonObjectAsync())["id"]!.GetValue<Guid>();

        using var refused = await world.SendAsync(HttpMethod.Delete, $"/api/orders/{id}", _orderWriter);

        refused.StatusCode.ShouldBe(HttpStatusCode.Conflict, await refused.ReadTextAsync());
        (await world.CountRowsAsync("orders")).ShouldBe(1);
    }

    [Fact]
    public async Task A_set_of_another_owner_is_forbidden_on_every_engine()
    {
        await using var world = await StartHookGuardsAsync();

        using var refused = await world.SendAsync(HttpMethod.Post, "/api/orders", _orderWriter, body: Order("steal"));

        refused.StatusCode.ShouldBe(HttpStatusCode.Forbidden, await refused.ReadTextAsync());
        (await refused.ReadProblemTypeAsync()).ShouldBe("forbidden");
        (await world.CountRowsAsync("orders")).ShouldBe(0);
    }

    [Fact]
    public async Task A_set_of_a_read_only_field_is_refused_as_the_caller_s_own_payload_would_be_on_every_engine()
    {
        await using var world = await StartHookGuardsAsync();
        var asCaller = new JsonObject { ["title"] = "plain", ["owner_id"] = _orderWriter.User, ["reference"] = "R-1" };

        using var byCaller = await world.SendAsync(HttpMethod.Post, "/api/orders", _orderWriter, body: asCaller);
        using var byHook = await world.SendAsync(HttpMethod.Post, "/api/orders", _orderWriter, body: Order("freeze"));

        byHook.StatusCode.ShouldBe(byCaller.StatusCode);
        byHook.StatusCode.ShouldBe(HttpStatusCode.Forbidden, await byHook.ReadTextAsync());
        (await byHook.ReadProblemDetailAsync()).ShouldBe(await byCaller.ReadProblemDetailAsync());
        (await world.CountRowsAsync("orders")).ShouldBe(0);
    }

    [Fact]
    public async Task A_set_of_an_undeclared_enum_value_is_invalid_set_on_every_engine()
    {
        await using var world = await StartHookGuardsAsync();

        using var refused = await world.SendAsync(HttpMethod.Post, "/api/orders", _orderWriter, body: Order("promote"));

        refused.StatusCode.ShouldBe(HttpStatusCode.InternalServerError);
        (await refused.ReadProblemTypeAsync()).ShouldBe("hook-failed");
        var document = await refused.ReadJsonObjectAsync();
        (document["reason"]!.GetValue<string>(), document["extensions"]![0]!.GetValue<string>()).ShouldBe(("invalid-set", "promote-guard"));
        (await refused.ReadTextAsync()).ShouldNotContain("superadmin");
        (await world.CountRowsAsync("orders")).ShouldBe(0);
    }

    [Fact]
    public async Task A_rejection_on_a_hidden_field_answers_with_no_pointer_on_every_engine()
    {
        await using var world = await StartHookGuardsAsync();

        using var refused = await world.SendAsync(HttpMethod.Post, "/api/orders", _orderWriter, body: Order("score"));

        refused.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);
        (await refused.ReadViolationsAsync()).ShouldHaveSingleItem().Pointer.ShouldBe(string.Empty);
        (await refused.ReadTextAsync()).ShouldNotContain("\"/score\"");
    }

    [Fact]
    public async Task A_hook_exception_answers_500_hook_failed_and_never_its_message_on_every_engine()
    {
        await using var world = await StartHookGuardsAsync();

        using var failed = await world.SendAsync(HttpMethod.Post, "/api/orders", _orderWriter, body: Order("crash"));

        failed.StatusCode.ShouldBe(HttpStatusCode.InternalServerError);
        (await failed.ReadJsonObjectAsync())["reason"]!.GetValue<string>().ShouldBe("exception");
        (await failed.ReadTextAsync()).ShouldNotContain("SECRET");
    }

    private static JsonObject Order(string title) => new() { ["title"] = title, ["owner_id"] = _orderWriter.User };

    private Task<AlvoApiWorld> StartHookGuardsAsync() =>
        AlvoApiWorld.FromDescriptorPathAsync(
            Path.Combine(RepositoryRoot.Find(), "test", "MMLib.Alvo.Api.Tests", "descriptors", "hook-guards.alvo.json"),
            [_orderWriter],
            new AlvoApiWorldSetup(MapAlvoProblemDetails: true, ConfigureAlvo: RegisterGuards),
            Engine);

    /// <summary>A hook limit no CI runner reaches: these facts are about answers, not time (Ruling 40).</summary>
    private static void Roomy(MMLib.Alvo.Extensions.AlvoHookOptions options) => options.Timeout = TimeSpan.FromSeconds(30);

    private static void RegisterGuards(IAlvoBuilder alvo) => alvo.Entity("orders", orders => orders
        .Validate("validate-guard", (ValidateWrite<AlvoRecord> write) => Reject(() => write.Invalid("too-plain", "Pick a better title.", "title")), when: "new.title == 'validate'", configure: Roomy)
        .BeforeWrite("before-guard", (BeforeWrite<AlvoRecord> write) => Reject(() => write.Conflict("taken", "Taken.", "title")), when: "new.title == 'before'", configure: Roomy)
        .BeforeWrite("steal-guard", (BeforeWrite<AlvoRecord> write) => Reject(() => write.Set("owner_id", Guid.NewGuid())), when: "new.title == 'steal'", configure: Roomy)
        .BeforeWrite("promote-guard", (BeforeWrite<AlvoRecord> write) => Reject(() => write.Set("status", "superadmin")), when: "new.title == 'promote'", configure: Roomy)
        .BeforeWrite("freeze-guard", (BeforeWrite<AlvoRecord> write) => Reject(() => write.Set("reference", "R-1")), when: "new.title == 'freeze'", configure: Roomy)
        .BeforeWrite("score-guard", (BeforeWrite<AlvoRecord> write) => Reject(() => write.Invalid("too-low", "Below the bar.", "score")), when: "new.title == 'score'", configure: Roomy)
        .BeforeWrite("crash-guard", (BeforeWrite<AlvoRecord> _) => throw new InvalidOperationException("SECRET connection string"), when: "new.title == 'crash'", configure: Roomy)
        .AfterWrite("after-guard", (AfterWrite<AlvoRecord> write) => Reject(() => write.Forbidden("not-now", "Not now.")), when: "new.title == 'after'", configure: Roomy)
        .BeforeDelete("delete-guard", (BeforeDelete<AlvoRecord> write) => Reject(() => write.Conflict("kept", "Kept.")), when: "old.title == 'keep'", configure: Roomy));

    private static ValueTask Reject(Action record)
    {
        record();
        return ValueTask.CompletedTask;
    }
}
```

(`TestApiKey.User` is the key's user id, `Guid.NewGuid()` by default; the create rule pins `owner_id == @user.id`, so the body carries it.)

- [ ] **Step 2: Run them to verify they fail**

Run: `dotnet test --project test/MMLib.Alvo.Api.Tests --filter-class '*SqliteDataApiEngineTests'` (the SQLite subclass — `grep -rn ': DataApiEngineTests' test/MMLib.Alvo.Api.Tests` for its name)
Expected: the new facts FAIL — a rejection is rendered 500 `internal`, a hook failure 500 `internal`.

- [ ] **Step 3: Map them**

`AlvoProblemTypes.cs` — after `ChainDepthExceeded`:

```csharp
    /// <summary>A host write hook failed, so nothing was written (500).</summary>
    /// <remarks>
    /// The document carries <c>extensions</c> — the hook's id — and a <c>reason</c>: <c>exception</c> (the hook threw),
    /// <c>timeout</c>, <c>budget</c> (too many calls through the write's transaction), <c>invalid-set</c> (a value its field
    /// cannot hold, or a field it may not set), <c>self-write</c> (it wrote the row it runs on) or <c>unbound</c> (the hook
    /// does not fit the current schema). Never the hook's own message, which is the log's. Emitted only by
    /// <c>AlvoExceptionHandler</c>. A host hook's refusal is not this: it is <see cref="Validation"/>, <see cref="Conflict"/>
    /// or <see cref="Forbidden"/>.
    /// </remarks>
    public const string HookFailed = "hook-failed";
```

and `HookFailed` appended to `All` after `ChainDepthExceeded`.

`ProblemResultFactory.cs`:

```csharp
    /// <summary>A host write hook's refusal: 422, 409 or 403 by its kind, with every violation of that kind and the hook's id.</summary>
    /// <param name="rejected">The refusal.</param>
    /// <returns>The problem result.</returns>
    internal static IResult HookRejected(AlvoWriteRejectedException rejected)
    {
        ArgumentNullException.ThrowIfNull(rejected);
        var (status, type) = rejected.Kind switch
        {
            AlvoWriteRejectionKind.Forbidden => (StatusCodes.Status403Forbidden, AlvoProblemTypes.Forbidden),
            AlvoWriteRejectionKind.Conflict => (StatusCodes.Status409Conflict, AlvoProblemTypes.Conflict),
            _ => (StatusCodes.Status422UnprocessableEntity, AlvoProblemTypes.Validation),
        };

        return Problem(
            status,
            type,
            string.Join(" ", rejected.Violations.Select(violation => violation.Message).Distinct(StringComparer.Ordinal)),
            new Dictionary<string, object?>(StringComparer.Ordinal) { ["violations"] = rejected.Violations, [ExtensionsMember] = new[] { rejected.ExtensionId } });
    }

    /// <summary>The 500 for a host write hook that failed; the write was rolled back.</summary>
    /// <param name="extension">The hook's id.</param>
    /// <param name="reason">The fixed reason.</param>
    /// <returns>The problem result.</returns>
    internal static IResult HookFailed(string extension, string reason) =>
        Problem(
            StatusCodes.Status500InternalServerError,
            AlvoProblemTypes.HookFailed,
            $"The write hook '{extension}' failed ({reason}), so nothing was written. Its own error is in the server log.",
            new Dictionary<string, object?>(StringComparer.Ordinal) { [ExtensionsMember] = new[] { extension }, ["reason"] = reason });

    private const string ExtensionsMember = "extensions";
```

and in `PortRefusalFor`'s switch, before the `ArgumentException`-free arms end: `AlvoWriteRejectedException rejected => HookRejected(rejected),`.

`AlvoExceptionHandler.AnswerAsync` — after the function-failure branch:

```csharp
        if (HookFailure(exception, MaxWrapperDepth) is { } hook)
        {
            await ProblemResultFactory.HookFailed(hook.Extension, hook.Reason).ExecuteAsync(httpContext).ConfigureAwait(false);
            return;
        }
```

```csharp
    /// <summary>A hook failure in <paramref name="exception"/> or in what wraps it, found the way a function's failure is.</summary>
    private static HookFailedException? HookFailure(Exception? exception, int depth) =>
        exception switch
        {
            null => null,
            HookFailedException hook => hook,
            _ when depth <= 0 => null,
            _ => Children(exception).Select(child => HookFailure(child, depth - 1)).FirstOrDefault(found => found is not null),
        };
```

(The hook's failure was already logged by `HookLog.HookFailed`; the handler does not log it again at Error.)

- [ ] **Step 4: Run them on both engines**

Run: the SQLite subclass as above, then `dotnet test --project test/MMLib.Alvo.Api.Tests.Integration --filter-class '*DataApiEngineTests*'` (Docker).
Expected: PASS on both. Accept the core public-API baseline (`AlvoProblemTypes.HookFailed`). Any fact pinning `AlvoProblemTypes.All`'s exact list (`grep -rn 'AlvoProblemTypes.All' test`) gains `hook-failed`; the API document's shared problem components do not change (no route advertises it — a 500 is the host's mapping, HX-08 Ruling 45).

- [ ] **Step 5: Commit**

```bash
dotnet format whitespace MMLib.Alvo.slnx --include src/MMLib.Alvo/Api/AlvoProblemTypes.cs src/MMLib.Alvo/Api/Internal/ProblemResultFactory.cs src/MMLib.Alvo/Api/Internal/AlvoExceptionHandler.cs test/_shared/api/DataApiEngineTests.WriteHooks.cs
scripts/test-ring0
git add src/MMLib.Alvo/Api test/_shared/api/DataApiEngineTests.WriteHooks.cs test/MMLib.Alvo.Api.Tests/descriptors/hook-guards.alvo.json test/MMLib.Alvo.Tests/PublicApi.MMLib.Alvo.verified.txt
git commit -m "feat(api): answer write-hook rejections and failures

Claude-Session: https://claude.ai/code/session_01NJAtafM29iddLwfhb5gnmB"
```

---

## Task 10: Operations — switching a validate hook off and on, `Enabled` in the catalog, write hooks in the stats

**Files:**
- Modify: `src/MMLib.Alvo/Management/Internal/AlvoOperationsService.Writes.cs`, `src/MMLib.Alvo/Extensions/Internal/ExtensionStats.cs`, `src/MMLib.Alvo/Extensions/Setup.cs` (nothing if `ExtensionStats` keeps its constructor), `test/MMLib.Alvo.Tests/Management/OperationsTestWorld.cs`, `test/MMLib.Alvo.Tests/Management/AlvoOperationsWritesTests.cs`, `test/MMLib.Alvo.Tests/Extensions/ExtensionStatsTests.cs`
- Test: the facts below; the both-engine fact `WriteHookFacts.Switching_a_validate_hook_off_is_audited_and_the_next_write_skips_it` arrives with Task 11 and passes there, because this task has landed first (Ruling 50)

**Interfaces:**
- Consumes: Task 1's `IDeliveryStore.SetDisabledAsync/DisabledAsync`, `ManagementExtension.Enabled`; Task 7's catalog entries; Task 8's `HookTelemetry` names.
- Produces: `SetEnabledAsync` that switches validate hooks (audited, idempotent) and refuses every other kind 409; `ManagementExtensionStat.Rejections` filled for write hooks.

- [ ] **Step 1: Write the failing facts**

`OperationsTestWorld` registers a validate hook and a before-write hook beside its handler: `internal const string Validator = "vat-check";`, `internal const string Guard = "stamp-guard";` and, in `AddAlvo`, `alvo.Entity("tickets", tickets => tickets.Validate(Validator, (ValidateWrite<AlvoRecord> _) => ValueTask.CompletedTask).BeforeWrite(Guard, (BeforeWrite<AlvoRecord> _) => ValueTask.CompletedTask));` (a fact that counts the catalog's items gains one; adjust its expectation by name, not by number).

`AlvoOperationsWritesTests` gains:

```csharp
    [Fact]
    public async Task Switching_a_validate_hook_off_writes_one_log_row_and_one_audit_line_and_answers_it_disabled()
    {
        using var world = new OperationsTestWorld();

        var off = await world.Service.SetEnabledAsync(Project, OperationsTestWorld.Validator, enabled: false, "VIES is down", Ct);
        var again = await world.Service.SetEnabledAsync(Project, OperationsTestWorld.Validator, enabled: false, "VIES is down", Ct);

        (off.Enabled, again.Enabled, off.Kind).ShouldBe((false, false, AlvoExtensionKind.Validate));
        world.Store.Operations.ShouldHaveSingleItem().Operation.ShouldBe("disable-extension");
        world.Audit.Lines.ShouldHaveSingleItem().ShouldContain("'disable-extension' on 'vat-check'");
        (await world.Service.GetExtensionsAsync(Project, Ct)).Items.Single(item => item.Id == OperationsTestWorld.Validator).Enabled.ShouldBeFalse();
    }

    [Fact]
    public async Task Switching_a_before_write_hook_off_is_refused_409_and_changes_nothing()
    {
        using var world = new OperationsTestWorld();

        (await Should.ThrowAsync<ManagementConflictException>(() => world.Service.SetEnabledAsync(Project, OperationsTestWorld.Guard, enabled: false, "too slow", Ct)))
            .Message.ShouldContain("only a validate hook");
        world.Store.Operations.ShouldBeEmpty();
        world.Audit.Lines.ShouldBeEmpty();
    }

    [Fact]
    public async Task Switching_it_on_again_is_audited_as_enable()
    {
        using var world = new OperationsTestWorld();
        await world.Service.SetEnabledAsync(Project, OperationsTestWorld.Validator, enabled: false, null, Ct);

        (await world.Service.SetEnabledAsync(Project, OperationsTestWorld.Validator, enabled: true, null, Ct)).Enabled.ShouldBeTrue();

        world.Store.Operations.Select(operation => operation.Operation).ShouldBe(["disable-extension", "enable-extension"]);
    }
```

(The existing `…handler…409` fact stays: a handler is still refused with the pause hint. If `GetExtensionsAsync` is not on `AlvoOperationsService`, read the catalog through `IAlvoManagement.GetExtensionsAsync` from `world.Services`.)

`ExtensionStatsTests` gains:

```csharp
    [Fact]
    public void A_write_hook_s_runs_rejections_and_failures_are_counted_from_its_meter()
    {
        var id = $"hook-{Guid.NewGuid():N}"[..20];
        var clock = new FakeTimeProvider(DateTimeOffset.UtcNow);
        using var stats = new ExtensionStats(clock, HandlerCatalog.Registry(alvo => alvo.Entity("tickets", t => t.BeforeWrite(id, (BeforeWrite<AlvoRecord> _) => ValueTask.CompletedTask))));

        HookTelemetry.Finish(null, id, HookTelemetry.Succeeded, TimeSpan.FromMilliseconds(4), 0);
        HookTelemetry.Finish(null, id, HookTelemetry.Rejected, TimeSpan.FromMilliseconds(6), 0);
        HookTelemetry.Finish(null, id, HookTelemetry.Failed, TimeSpan.FromMilliseconds(8), 0);

        var stat = stats.Report([id]).Items.ShouldHaveSingleItem();
        (stat.Invocations, stat.Rejections, stat.Failures).ShouldBe((3L, 1L, 1L));
        stat.P95.ShouldNotBeNull();
    }
```

- [ ] **Step 2: Run them to verify they fail**

Run: `dotnet test --project test/MMLib.Alvo.Tests --filter-class '*AlvoOperationsWritesTests'` and `--filter-class '*ExtensionStatsTests'`
Expected: FAIL — `SetEnabledAsync` refuses every kind; the stats know no hook.

- [ ] **Step 3: Write the switch and the stats**

`AlvoOperationsService.Writes.cs` — replace `SetEnabledAsync`:

```csharp
    private const string DisableOperation = "disable-extension";
    private const string EnableOperation = "enable-extension";

    public async Task<ManagementExtension> SetEnabledAsync(string project, string extension, bool enabled, string? reason, CancellationToken ct = default)
    {
        gate.Admit(ManagementOperation.SetExtensionEnabled, project);
        OperationReasons.Ensure(reason);
        var found = Registered(extension);
        EnsureSwitchable(found, project, enabled);
        var entry = Entry(enabled ? EnableOperation : DisableOperation, found.Id, reason);
        if (await Store.SetDisabledAsync(found.Id, !enabled, entry, ct).ConfigureAwait(false))
        {
            OperationsAudit.Write(logger, entry with { Affected = 1 });
        }

        return found with { Enabled = enabled };
    }

    private static void EnsureSwitchable(ManagementExtension found, string project, bool enabled)
    {
        if (found.Kind == AlvoExtensionKind.Validate)
        {
            return;
        }

        throw new ManagementConflictException(found.Kind == AlvoExtensionKind.EventHandler
            ? $"'{found.Id}' is an event handler, and only a validate hook can be switched {(enabled ? "on" : "off")}. To stop a handler for a while, "
              + $"pause it instead: PUT {{m}}/projects/{project}/subscribers/{found.Id}/paused with {{\"paused\": true}}."
            : $"'{found.Id}' is a {ExtensionCatalog.WireName(found.Kind)} extension, and only a validate hook can be switched {(enabled ? "on" : "off")}: "
              + "a hook inside the write's transaction guards the data's integrity, so it is changed in the host's code and redeployed.");
    }
```

(`Store` is the operations service's `IDeliveryStore`; the method is now `async`.)

`ExtensionStats.cs`:

- the constructor's id set becomes `registry.Handlers.Select(handler => handler.Id).Concat(registry.WriteHooks.Select(hook => hook.Id)).Distinct(StringComparer.Ordinal)`;
- `Publish` also enables `instrument.Meter.Name == HookTelemetry.MeterName && instrument.Name == HookTelemetry.DurationName`;
- `OnDuration` passes `rejected: outcome == HookTelemetry.Rejected` and `failed: outcome is AlvoDeliveryMetrics.FailedOutcome or AlvoDeliveryMetrics.DeadOutcome` (a hook's `failed` is the same word) to `Record`;
- `Record(string subscriber, double milliseconds, bool failed, bool rejected = false)`; `Buckets` gains `private readonly long[] _rejections = new long[Hours];`, incremented in `Record`, cleared in `Slot`, summed in `Stat` into `Rejections = rejections`.

- [ ] **Step 4: Run them**

Run: the two classes above → PASS.

- [ ] **Step 5: Commit**

```bash
dotnet format whitespace MMLib.Alvo.slnx --include src/MMLib.Alvo/Management/Internal/AlvoOperationsService.Writes.cs src/MMLib.Alvo/Extensions/Internal/ExtensionStats.cs test/MMLib.Alvo.Tests/Management/OperationsTestWorld.cs test/MMLib.Alvo.Tests/Management/AlvoOperationsWritesTests.cs test/MMLib.Alvo.Tests/Extensions/ExtensionStatsTests.cs
scripts/test-ring0
git add src/MMLib.Alvo/Management src/MMLib.Alvo/Extensions/Internal/ExtensionStats.cs test/MMLib.Alvo.Tests/Management test/MMLib.Alvo.Tests/Extensions/ExtensionStatsTests.cs
git commit -m "feat(operations): switch validate hooks off and on, and count hook runs

Claude-Session: https://claude.ai/code/session_01NJAtafM29iddLwfhb5gnmB"
```

---

## Task 11: End to end on both engines — the acceptance facts through `AlvoTestHost`, and `Hooks.AssertRan` for write hooks **[slice end]**

**Files:**
- Create: `test/_shared/testhost/WriteHookFacts.cs`, `test/_shared/testhost/WriteHookRows.cs`, `test/_shared/testhost/descriptors/hook-desk.alvo.json`, `test/MMLib.Alvo.TestHost.Tests/SqliteWriteHookTests.cs`, `test/MMLib.Alvo.Data.PostgreSql.Tests.Integration/PostgreSqlWriteHookTests.cs`, `src/MMLib.Alvo.TestHost/Internal/TrackedData.cs`
- Modify: `src/MMLib.Alvo.TestHost/AlvoTestHost.cs`
- Test: the facts themselves (both legs link `_shared/testhost/*.cs` already)

**Interfaces:**
- Consumes: everything above; `AlvoTestHost.StartAsync(configure, time, webhooks, ct)`, `AlvoTestHost.Caller`, `alvo.Hooks.AssertRan`, `AuditCapture` (`_shared/testhost`).
- Produces: `WriteHookFacts` (abstract, `protected abstract Task<HookDatabase> DatabaseAsync()`, `protected sealed record HookDatabase(Action<IAlvoBuilder> Use, Func<DbConnection> Connect)`); `TrackedData : IAlvoData` (internal).

- [ ] **Step 1: Write the descriptor and the failing facts**

`test/_shared/testhost/descriptors/hook-desk.alvo.json`:

```json
{
  "$schema": "https://alvo.dev/schema/v1/project.json",
  "apiVersion": "alvo.dev/v1",
  "name": "hook-desk",
  "description": "The write-hook facts' desk: bookings with a default status, a score hidden from clerks and a rollup of their lines; audits a validate hook writes outside the write; limits only managers read, so a guard must act as the system to read them.",
  "tenancy": { "enabled": true },
  "auth": { "roles": ["clerk", "manager"] },
  "access": { "admin": "'manager' in @user.roles" },
  "entities": {
    "bookings": {
      "tenancy": "scoped",
      "fields": {
        "title": { "type": "string", "required": true, "maxLength": 40 },
        "status": { "type": "enum", "values": ["open", "closed"], "default": "open" },
        "score": { "type": "integer", "hidden": "!('manager' in @user.roles)" },
        "lines_total": { "type": "integer", "rollup": { "from": "booking_lines", "op": "sum", "field": "amount" } }
      },
      "rules": {
        "list": "'clerk' in @user.roles || 'admin' in @user.roles",
        "get": "'clerk' in @user.roles || 'admin' in @user.roles",
        "create": "'clerk' in @user.roles || 'admin' in @user.roles",
        "update": "'clerk' in @user.roles || 'admin' in @user.roles",
        "delete": "'clerk' in @user.roles || 'admin' in @user.roles"
      }
    },
    "booking_lines": {
      "tenancy": "scoped",
      "fields": {
        "booking_id": { "type": "ref", "entity": "bookings", "required": true, "onDelete": "cascade" },
        "amount": { "type": "integer", "required": true }
      },
      "rules": {
        "list": "'clerk' in @user.roles || 'admin' in @user.roles",
        "get": "'clerk' in @user.roles || 'admin' in @user.roles",
        "create": "'clerk' in @user.roles || 'admin' in @user.roles"
      }
    },
    "audits": {
      "tenancy": "scoped",
      "fields": { "note": { "type": "string", "required": true, "maxLength": 80 } },
      "rules": {
        "list": "'clerk' in @user.roles || 'admin' in @user.roles",
        "get": "'clerk' in @user.roles || 'admin' in @user.roles",
        "create": "'clerk' in @user.roles || 'admin' in @user.roles"
      }
    },
    "limits": {
      "tenancy": "scoped",
      "fields": { "max_title": { "type": "integer", "required": true } },
      "rules": {
        "list": "'admin' in @user.roles",
        "get": "'admin' in @user.roles",
        "create": "'admin' in @user.roles"
      }
    }
  }
}
```

`test/_shared/testhost/WriteHookRows.cs`:

```csharp
using System.Data.Common;
using System.Globalization;

namespace MMLib.Alvo.TestHost.Tests;

/// <summary>Reads the framework tables a write-hook fact checks, outside the port.</summary>
internal static class WriteHookRows
{
    internal static async Task<long> CountAsync(Func<DbConnection> connect, string table, string where = "1 = 1")
    {
        await using var connection = connect();
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = $"SELECT COUNT(*) FROM {table} WHERE {where}";

        return Convert.ToInt64(await command.ExecuteScalarAsync(TestContext.Current.CancellationToken), CultureInfo.InvariantCulture);
    }
}
```

`test/_shared/testhost/WriteHookFacts.cs`:

```csharp
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Time.Testing;
using MMLib.Alvo.Auth;
using MMLib.Alvo.Data;
using MMLib.Alvo.Events;
using MMLib.Alvo.Hooks;
using MMLib.Alvo.Management;
using System.Collections.Concurrent;
using System.Data.Common;

namespace MMLib.Alvo.TestHost.Tests;

/// <summary>A scoped service whose instance tells two resolutions of one scope from two scopes.</summary>
public sealed class ScopeProbe
{
    public Guid Id { get; } = Guid.NewGuid();
}

/// <summary>
/// Host write hooks over the real pipeline (spec 2026-10-14 §7 HX-09 acceptance (1)–(13)), on whichever engine the deriving class
/// supplies — the SQLite leg in <c>MMLib.Alvo.TestHost.Tests</c> (a temp file, so a held write lock would block), the PostgreSQL
/// leg in <c>MMLib.Alvo.Data.PostgreSql.Tests.Integration</c>.
/// </summary>
/// <remarks>Every fact that fails a write asserts no row and no outbox row, not only the exception (Review Focus 0).</remarks>
public abstract class WriteHookFacts
{
    private const string Bookings = "bookings";
    private static readonly TimeSpan _immediately = TimeSpan.FromSeconds(10);

    protected static CancellationToken Ct => TestContext.Current.CancellationToken;

    public static TheoryData<string, AlvoWriteRejectionKind, string> Stages => new()
    {
        { "validate", AlvoWriteRejectionKind.Invalid, "/title" },
        { "beforeWrite", AlvoWriteRejectionKind.Conflict, "/title" },
        { "afterWrite", AlvoWriteRejectionKind.Forbidden, "" },
    };

    [Theory]
    [MemberData(nameof(Stages))]
    public async Task A_rejection_at_each_write_stage_leaves_no_row_no_outbox_row_and_its_kind_and_pointer(string stage, AlvoWriteRejectionKind kind, string pointer)
    {
        var database = await DatabaseAsync();
        await using var alvo = await StartAsync(database, alvo => alvo.Entity(Bookings, b => Rejecting(b, stage)));
        var clerk = Clerk();

        var rejection = await Should.ThrowAsync<AlvoWriteRejectedException>(() => alvo.Data.CreateAsync(Bookings, Booking(clerk, "refused"), clerk, cancellationToken: Ct));

        (rejection.Kind, rejection.Pointer, rejection.ExtensionId).ShouldBe((kind, pointer, stage));
        (await WriteHookRows.CountAsync(database.Connect, Bookings)).ShouldBe(0);
        (await WriteHookRows.CountAsync(database.Connect, "alvo_outbox")).ShouldBe(0);
    }

    [Theory]
    [InlineData("beforeDelete")]
    [InlineData("afterDelete")]
    public async Task A_rejection_at_each_delete_stage_keeps_the_row_and_emits_nothing(string stage)
    {
        var database = await DatabaseAsync();
        await using var alvo = await StartAsync(database, alvo => alvo.Entity(Bookings, b => Rejecting(b, stage)));
        var clerk = Clerk();
        var created = await alvo.Data.CreateAsync(Bookings, Booking(clerk, "kept"), clerk, cancellationToken: Ct);
        var outbox = await WriteHookRows.CountAsync(database.Connect, "alvo_outbox");

        (await Should.ThrowAsync<AlvoWriteRejectedException>(() => alvo.Data.DeleteAsync(Bookings, (Guid)created["id"]!, clerk, cancellationToken: Ct))).Kind
            .ShouldBe(AlvoWriteRejectionKind.Conflict);

        (await WriteHookRows.CountAsync(database.Connect, Bookings)).ShouldBe(1);
        (await WriteHookRows.CountAsync(database.Connect, "alvo_outbox")).ShouldBe(outbox);
    }

    [Fact]
    public async Task A_set_of_an_undeclared_enum_value_is_invalid_set_and_writes_nothing()
    {
        var database = await DatabaseAsync();
        await using var alvo = await StartAsync(database, alvo => alvo.Entity(Bookings, b => b.BeforeWrite("promote", (BeforeWrite<AlvoRecord> write) =>
        {
            write.Set("status", "superadmin");
            return ValueTask.CompletedTask;
        })));
        var clerk = Clerk();

        (await Should.ThrowAsync<InvalidOperationException>(() => alvo.Data.CreateAsync(Bookings, Booking(clerk, "x"), clerk, cancellationToken: Ct))).Message
            .ShouldContain("invalid-set");
        (await WriteHookRows.CountAsync(database.Connect, Bookings)).ShouldBe(0);
    }

    [Fact]
    public async Task A_rejection_on_a_hidden_field_carries_no_pointer()
    {
        var database = await DatabaseAsync();
        await using var alvo = await StartAsync(database, alvo => alvo.Entity(Bookings, b => b.BeforeWrite("score-floor", (BeforeWrite<AlvoRecord> write) =>
        {
            write.Invalid("too-low", "Below the bar.", "score");
            return ValueTask.CompletedTask;
        })));
        var clerk = Clerk();

        (await Should.ThrowAsync<AlvoWriteRejectedException>(() => alvo.Data.CreateAsync(Bookings, Booking(clerk, "x"), clerk, cancellationToken: Ct))).Pointer
            .ShouldBe(string.Empty);
    }

    [Fact]
    public async Task An_after_write_sees_the_id_the_defaults_and_its_child_s_rollup_reaches_the_event_and_the_row()
    {
        var database = await DatabaseAsync();
        var saved = new ConcurrentQueue<AlvoRecord>();
        var events = new ConcurrentQueue<AlvoEvent>();
        await using var alvo = await StartAsync(database, alvo => alvo.Entity(Bookings, b => b
            .AfterWrite("first-line", async (AfterWrite<AlvoRecord> write, CancellationToken ct) =>
            {
                saved.Enqueue(write.Saved);
                await write.Transaction.CreateAsync("booking_lines", new Dictionary<string, object?>
                {
                    ["booking_id"] = write.Saved["id"],
                    ["amount"] = 5L,
                    ["tenant_id"] = write.Caller.Tenant!.Value.Value,
                }, ct);
            })
            .OnCreated("seen", (AlvoCreated<AlvoRecord> _, AlvoEventContext context) => events.Enqueue(context.Event))));
        var clerk = Clerk();

        var created = await alvo.Data.CreateAsync(Bookings, Booking(clerk, "with a line"), clerk, cancellationToken: Ct);
        await alvo.DrainEventsAsync(Ct);

        var row = saved.ShouldHaveSingleItem();
        (row["id"], row["status"]).ShouldBe((created["id"], "open"));
        created["lines_total"].ShouldBe(5L, "the echo carries the rollup the hook's child moved (stage 7b)");
        events.ShouldHaveSingleItem().Data.Record!["lines_total"].ShouldBe(5L);
        alvo.Hooks.AssertRan("first-line", times: 1);
    }

    [Fact]
    public async Task An_after_write_that_rejects_rolls_its_child_back_too()
    {
        var database = await DatabaseAsync();
        await using var alvo = await StartAsync(database, alvo => alvo.Entity(Bookings, b => b.AfterWrite("undo", async (AfterWrite<AlvoRecord> write, CancellationToken ct) =>
        {
            await write.Transaction.CreateAsync("booking_lines", new Dictionary<string, object?>
            {
                ["booking_id"] = write.Saved["id"],
                ["amount"] = 5L,
                ["tenant_id"] = write.Caller.Tenant!.Value.Value,
            }, ct);
            write.Conflict("no-lines-today", "No lines today.");
        })));
        var clerk = Clerk();

        await Should.ThrowAsync<AlvoWriteRejectedException>(() => alvo.Data.CreateAsync(Bookings, Booking(clerk, "undone"), clerk, cancellationToken: Ct));

        (await WriteHookRows.CountAsync(database.Connect, Bookings)).ShouldBe(0);
        (await WriteHookRows.CountAsync(database.Connect, "booking_lines")).ShouldBe(0);
        alvo.Hooks.AssertRan("undo", times: 1);
    }

    [Fact]
    public async Task A_hook_that_catches_a_self_write_refusal_still_rolls_back_with_reason_self_write()
    {
        var database = await DatabaseAsync();
        await using var alvo = await StartAsync(database, alvo => alvo.Entity(Bookings, b => b.AfterWrite("echo", async (AfterWrite<AlvoRecord> write, CancellationToken ct) =>
        {
            try
            {
                await write.Transaction.UpdateAsync(Bookings, (Guid)write.Saved["id"]!, new Dictionary<string, object?> { ["title"] = "again" }, cancellationToken: ct);
            }
            catch (InvalidOperationException)
            {
            }
        })));
        var clerk = Clerk();

        (await Should.ThrowAsync<InvalidOperationException>(() => alvo.Data.CreateAsync(Bookings, Booking(clerk, "self"), clerk, cancellationToken: Ct))).Message
            .ShouldContain("self-write");
        (await WriteHookRows.CountAsync(database.Connect, Bookings)).ShouldBe(0);
        (await WriteHookRows.CountAsync(database.Connect, "alvo_outbox")).ShouldBe(0);
    }

    [Fact]
    public async Task A_hook_that_catches_its_timeout_still_rolls_back_with_reason_timeout()
    {
        var database = await DatabaseAsync();
        var clock = new FakeTimeProvider(DateTimeOffset.UtcNow);
        await using var alvo = await StartAsync(database, alvo => alvo.Entity(Bookings, b => b.BeforeWrite("slow", async (BeforeWrite<AlvoRecord> write, CancellationToken ct) =>
        {
            await write.Transaction.CreateAsync("audits", Audit(write.Caller, "before the overrun"), ct);
            clock.Advance(TimeSpan.FromMilliseconds(300));
            try
            {
                await write.Transaction.GetAsync(Bookings, Guid.NewGuid(), ct);
            }
            catch (OperationCanceledException)
            {
            }
        })), clock);
        var clerk = Clerk();

        (await Should.ThrowAsync<InvalidOperationException>(() => alvo.Data.CreateAsync(Bookings, Booking(clerk, "slow"), clerk, cancellationToken: Ct))).Message
            .ShouldContain("timeout");
        (await WriteHookRows.CountAsync(database.Connect, Bookings)).ShouldBe(0);
        (await WriteHookRows.CountAsync(database.Connect, "audits")).ShouldBe(0);
    }

    [Fact]
    public async Task A_hook_that_catches_its_budget_refusal_still_rolls_back_with_reason_budget()
    {
        var database = await DatabaseAsync();
        await using var alvo = await StartAsync(database, alvo => alvo.Entity(Bookings, b => b.AfterWrite("greedy", async (AfterWrite<AlvoRecord> write, CancellationToken ct) =>
        {
            try
            {
                for (var call = 0; call < 51; call++)
                {
                    await write.Transaction.CreateAsync("audits", Audit(write.Caller, $"audit {call}"), ct);
                }
            }
            catch (AlvoBudgetExceededException)
            {
            }
        })));
        var clerk = Clerk();

        (await Should.ThrowAsync<InvalidOperationException>(() => alvo.Data.CreateAsync(Bookings, Booking(clerk, "greedy"), clerk, cancellationToken: Ct))).Message
            .ShouldContain("budget");
        (await WriteHookRows.CountAsync(database.Connect, "audits")).ShouldBe(0);
        (await WriteHookRows.CountAsync(database.Connect, Bookings)).ShouldBe(0);
    }

    [Fact]
    public async Task A_batch_validate_hook_runs_once_per_row_with_no_transaction_open()
    {
        var database = await DatabaseAsync();
        await using var alvo = await StartAsync(database, alvo => alvo.Entity(Bookings, b => b.Validate("probe", async (ValidateWrite<AlvoRecord> write, CancellationToken ct) =>
        {
            await write.Data.CreateAsync("audits", Audit(write.Caller, $"checked {write.New["title"]}"), ct).WaitAsync(_immediately, ct);
            await AssertNoOpenTransactionAsync(database);
            if ((string?)write.New["title"] == "bad")
            {
                write.Invalid("bad-title", "Bad title.", "title");
            }
        })));
        var clerk = Clerk();

        var result = await alvo.Data.CreateManyAsync(Bookings, [Booking(clerk, "good"), Booking(clerk, "bad")], clerk, cancellationToken: Ct);

        result.Succeeded.ShouldBeFalse();
        result.Refusals.ShouldHaveSingleItem().ShouldSatisfyAllConditions(refusal => refusal.Index.ShouldBe(1), refusal => refusal.Code.ShouldBe("bad-title"));
        (await WriteHookRows.CountAsync(database.Connect, "audits")).ShouldBe(2, "each row's check committed on its own, outside any transaction");
        (await WriteHookRows.CountAsync(database.Connect, Bookings)).ShouldBe(0);
    }

    [Fact]
    public async Task A_batch_row_s_hook_writing_a_sibling_row_is_a_self_write()
    {
        var database = await DatabaseAsync();
        var ids = new Guid[2];
        await using var alvo = await StartAsync(database, alvo => alvo.Entity(Bookings, b => b.BeforeWrite("sibling", async (BeforeWrite<AlvoRecord> write, CancellationToken ct) =>
        {
            var sibling = ids.First(id => id != (Guid)write.New["id"]!);
            await write.Transaction.UpdateAsync(Bookings, sibling, new Dictionary<string, object?> { ["title"] = "touched" }, cancellationToken: ct);
        }, when: "changed(title)")));
        var clerk = Clerk();
        ids[0] = (Guid)(await alvo.Data.CreateAsync(Bookings, Booking(clerk, "first"), clerk, cancellationToken: Ct))["id"]!;
        ids[1] = (Guid)(await alvo.Data.CreateAsync(Bookings, Booking(clerk, "second"), clerk, cancellationToken: Ct))["id"]!;

        (await Should.ThrowAsync<InvalidOperationException>(() => alvo.Data.UpdateManyAsync(
            Bookings,
            [new AlvoRowPatch(ids[0], new Dictionary<string, object?> { ["title"] = "first, renamed" }), new AlvoRowPatch(ids[1], new Dictionary<string, object?> { ["title"] = "second, renamed" })],
            clerk,
            cancellationToken: Ct))).Message.ShouldContain("self-write");
        (await WriteHookRows.CountAsync(database.Connect, Bookings, "title IN ('first', 'second')")).ShouldBe(2);
    }

    [Fact]
    public async Task An_after_write_whose_cascade_removes_its_own_row_fails_with_row_vanished_and_emits_nothing()
    {
        var database = await DatabaseAsync();
        await using var alvo = await StartAsync(database, alvo => alvo.Entity("booking_lines", lines => lines.AfterWrite("drop-parent", (AfterWrite<AlvoRecord> write, CancellationToken ct) =>
            write.Transaction.DeleteAsync(Bookings, (Guid)write.Saved["booking_id"]!, cancellationToken: ct))));
        var clerk = Clerk();
        var booking = await alvo.Data.CreateAsync(Bookings, Booking(clerk, "parent"), clerk, cancellationToken: Ct);
        var outbox = await WriteHookRows.CountAsync(database.Connect, "alvo_outbox");

        (await Should.ThrowAsync<InvalidOperationException>(() => alvo.Data.CreateAsync("booking_lines", new Dictionary<string, object?>
        {
            ["booking_id"] = booking["id"],
            ["amount"] = 1L,
            ["tenant_id"] = clerk.Tenant!.Value.Value,
        }, clerk, cancellationToken: Ct))).Message.ShouldContain("row-vanished");
        (await WriteHookRows.CountAsync(database.Connect, Bookings)).ShouldBe(1);
        (await WriteHookRows.CountAsync(database.Connect, "booking_lines")).ShouldBe(0);
        (await WriteHookRows.CountAsync(database.Connect, "alvo_outbox")).ShouldBe(outbox);
    }

    [Fact]
    public async Task Validate_never_runs_on_an_update_the_payload_guard_refuses_nor_on_a_row_the_caller_cannot_reach()
    {
        var database = await DatabaseAsync();
        var runs = 0;
        await using var alvo = await StartAsync(database, alvo => alvo.Entity(Bookings, b => b.Validate("count", (ValidateWrite<AlvoRecord> _) =>
        {
            Interlocked.Increment(ref runs);
            return ValueTask.CompletedTask;
        })));
        var clerk = Clerk();
        var created = await alvo.Data.CreateAsync(Bookings, Booking(clerk, "kept"), clerk, cancellationToken: Ct);

        await Should.ThrowAsync<AlvoAuthorizationException>(() => alvo.Data.UpdateAsync(
            Bookings, (Guid)created["id"]!, new Dictionary<string, object?> { ["lines_total"] = 9L }, clerk, cancellationToken: Ct));
        await Should.ThrowAsync<AlvoRecordNotFoundException>(() => alvo.Data.UpdateAsync(
            Bookings, Guid.NewGuid(), new Dictionary<string, object?> { ["title"] = "nowhere" }, clerk, cancellationToken: Ct));

        runs.ShouldBe(1, "only the create was validated");
    }

    [Fact]
    public async Task A_validate_hook_never_runs_on_an_idempotent_replay()
    {
        var database = await DatabaseAsync();
        var runs = 0;
        await using var alvo = await StartAsync(database, alvo => alvo.Entity(Bookings, b => b.Validate("count", (ValidateWrite<AlvoRecord> _) =>
        {
            Interlocked.Increment(ref runs);
            return ValueTask.CompletedTask;
        })));
        var clerk = Clerk();
        var key = new AlvoIdempotency("book-1", "fingerprint-1");

        await alvo.Data.CreateAsync(Bookings, Booking(clerk, "once"), clerk, key, Ct);
        await alvo.Data.CreateAsync(Bookings, Booking(clerk, "once"), clerk, key, Ct);

        runs.ShouldBe(1);
    }

    [Fact]
    public async Task A_write_inside_a_unit_never_runs_validate()
    {
        var database = await DatabaseAsync();
        var runs = 0;
        await using var alvo = await StartAsync(database, alvo => alvo.Entity(Bookings, b => b.Validate("count", (ValidateWrite<AlvoRecord> _) =>
        {
            Interlocked.Increment(ref runs);
            return ValueTask.CompletedTask;
        })));
        var clerk = Clerk();

        await alvo.Services.GetRequiredService<IAlvoTransactions>().TransactAsync(clerk, (unit, ct) => unit.CreateAsync(Bookings, Booking(clerk, "inside"), ct), cancellationToken: Ct);

        runs.ShouldBe(0);
    }

    [Fact]
    public async Task A_before_write_hook_writing_its_own_entity_as_the_system_recursively_is_refused_508()
    {
        var database = await DatabaseAsync();
        await using var alvo = await StartAsync(database, alvo => alvo.Entity(Bookings, b => b.BeforeWrite("echo", async (BeforeWrite<AlvoRecord> write, CancellationToken ct) =>
            await write.Transaction.AsSystem().CreateAsync(Bookings, Booking(write.Caller, "echo"), ct))));
        var clerk = Clerk();

        await Should.ThrowAsync<AlvoChainDepthExceededException>(() => alvo.Data.CreateAsync(Bookings, Booking(clerk, "start"), clerk, cancellationToken: Ct));

        (await WriteHookRows.CountAsync(database.Connect, Bookings)).ShouldBe(0);
        (await WriteHookRows.CountAsync(database.Connect, "alvo_outbox")).ShouldBe(0);
    }

    [Fact]
    public async Task A_guard_reads_reference_data_the_caller_cannot_as_the_system_and_no_other_tenant_s()
    {
        var database = await DatabaseAsync();
        await using var alvo = await StartAsync(database, alvo => alvo.Entity(Bookings, b => b.BeforeWrite("title-limit", async (BeforeWrite<AlvoRecord> write, CancellationToken ct) =>
        {
            var limits = await write.Transaction.AsSystem().QueryAsync(new AlvoQuery { Entity = "limits" }, ct);
            if (limits.Items.Count == 0 || ((string)write.New["title"]!).Length > (long)limits.Items[0]["max_title"]!)
            {
                write.Invalid("title-too-long", "The title is longer than this desk allows.", "title");
            }
        })));
        var clerk = Clerk();
        var otherTenant = AlvoContext.System(new TenantId(Guid.NewGuid()));
        await alvo.Data.CreateAsync("limits", new Dictionary<string, object?> { ["max_title"] = 100L, ["tenant_id"] = otherTenant.Tenant!.Value.Value }, otherTenant, cancellationToken: Ct);

        (await Should.ThrowAsync<AlvoWriteRejectedException>(() => alvo.Data.CreateAsync(Bookings, Booking(clerk, "short"), clerk, cancellationToken: Ct))).Code
            .ShouldBe("title-too-long", "another tenant's limit is not this tenant's");

        var system = AlvoContext.System(clerk.Tenant);
        await alvo.Data.CreateAsync("limits", new Dictionary<string, object?> { ["max_title"] = 10L, ["tenant_id"] = clerk.Tenant!.Value.Value }, system, cancellationToken: Ct);
        await alvo.Data.CreateAsync(Bookings, Booking(clerk, "short"), clerk, cancellationToken: Ct);
        await Should.ThrowAsync<AlvoWriteRejectedException>(() => alvo.Data.CreateAsync(Bookings, Booking(clerk, "far longer than ten"), clerk, cancellationToken: Ct));
    }

    [Fact]
    public async Task Suppressing_host_hooks_needs_the_system_and_skips_them_with_one_audit_row()
    {
        var database = await DatabaseAsync();
        var audit = new AuditCapture();
        var runs = 0;
        await using var alvo = await StartAsync(database, alvo =>
        {
            alvo.Services.AddSingleton<ILoggerProvider>(audit);
            alvo.Entity(Bookings, b => b.BeforeWrite("count", (BeforeWrite<AlvoRecord> _) =>
            {
                Interlocked.Increment(ref runs);
                return ValueTask.CompletedTask;
            }));
        });
        var clerk = Clerk();
        var transactions = alvo.Services.GetRequiredService<IAlvoTransactions>();
        var suppress = new AlvoTransactionOptions { SuppressHostHooks = true, Reason = "nightly repair" };

        await Should.ThrowAsync<ArgumentException>(() => transactions.TransactAsync(clerk, (_, _) => Task.CompletedTask, suppress, Ct));
        var system = AlvoContext.System(clerk.Tenant);
        await transactions.TransactAsync(system, async (unit, ct) =>
        {
            await unit.CreateAsync(Bookings, Booking(clerk, "repaired one"), ct);
            await unit.CreateAsync(Bookings, Booking(clerk, "repaired two"), ct);
        }, suppress, Ct);

        runs.ShouldBe(0);
        (await WriteHookRows.CountAsync(database.Connect, "alvo_operations_log", "operation = 'suppress-host-hooks'")).ShouldBe(1);
        audit.Lines.ShouldHaveSingleItem().ShouldContain("nightly repair");
    }

    [Fact]
    public async Task A_rejection_is_logged_after_the_rollback_with_ids_and_its_code()
    {
        var database = await DatabaseAsync();
        await using var alvo = await StartAsync(database, alvo => alvo.Entity(Bookings, b => Rejecting(b, "beforeWrite")));
        var clerk = Clerk();

        await Should.ThrowAsync<AlvoWriteRejectedException>(() => alvo.Data.CreateAsync(Bookings, Booking(clerk, "logged"), clerk, cancellationToken: Ct));

        (await WriteHookRows.CountAsync(database.Connect, Bookings)).ShouldBe(0);
        (await WriteHookRows.CountAsync(database.Connect, "alvo_hook_rejections", "extension = 'beforeWrite' AND code = 'rejected' AND kind = 'conflict' AND entity = 'bookings'"))
            .ShouldBe(1);
    }

    /// <summary>
    /// Acceptance (13), read as Ruling 57 states: "a request-scoped service" is any scope other than the unit's — here a scope
    /// the test opens as an HTTP request would; the hooks' scope is the unit's, never the caller's.
    /// </summary>
    [Fact]
    public async Task Two_hooks_of_one_unit_share_its_scope_and_a_request_scope_is_another()
    {
        var database = await DatabaseAsync();
        var seen = new ConcurrentQueue<Guid>();
        await using var alvo = await StartAsync(database, alvo =>
        {
            alvo.Services.AddScoped<ScopeProbe>();
            alvo.Entity(Bookings, b => b
                .BeforeWrite("before-probe", (BeforeWrite<AlvoRecord> _, ScopeProbe probe) => { seen.Enqueue(probe.Id); return ValueTask.CompletedTask; })
                .AfterWrite("after-probe", (AfterWrite<AlvoRecord> _, ScopeProbe probe) => { seen.Enqueue(probe.Id); return ValueTask.CompletedTask; }));
        });
        var clerk = Clerk();

        await alvo.Data.CreateAsync(Bookings, Booking(clerk, "scoped"), clerk, cancellationToken: Ct);
        await using var request = alvo.Services.CreateAsyncScope();

        seen.Distinct().ShouldHaveSingleItem().ShouldNotBe(request.ServiceProvider.GetRequiredService<ScopeProbe>().Id);
    }

    [Fact]
    public async Task Switching_a_validate_hook_off_is_audited_and_the_next_write_skips_it()
    {
        var database = await DatabaseAsync();
        var audit = new AuditCapture();
        var runs = 0;
        await using var alvo = await StartAsync(database, alvo =>
        {
            alvo.Services.AddSingleton<ILoggerProvider>(audit);
            alvo.Entity(Bookings, b => b.Validate("vat-check", (ValidateWrite<AlvoRecord> _) =>
            {
                Interlocked.Increment(ref runs);
                return ValueTask.CompletedTask;
            }));
        });
        var clerk = Clerk();
        await alvo.Data.CreateAsync(Bookings, Booking(clerk, "checked"), clerk, cancellationToken: Ct);

        var entry = await AsAdministrator(alvo).SetEnabledAsync("hook-desk", "vat-check", enabled: false, "VIES is down", Ct);
        await alvo.Data.CreateAsync(Bookings, Booking(clerk, "unchecked"), clerk, cancellationToken: Ct);

        (runs, entry.Enabled).ShouldBe((1, false));
        (await WriteHookRows.CountAsync(database.Connect, "alvo_operations_log", "operation = 'disable-extension' AND target = 'vat-check'")).ShouldBe(1);
        audit.Lines.ShouldHaveSingleItem().ShouldContain("VIES is down");
        (await AsAdministrator(alvo).SetEnabledAsync("hook-desk", "vat-check", enabled: true, null, Ct)).Enabled.ShouldBeTrue();
        await alvo.Data.CreateAsync(Bookings, Booking(clerk, "checked again"), clerk, cancellationToken: Ct);
        runs.ShouldBe(2);
    }

    [Fact]
    public async Task A_rejected_run_counts_as_ran_for_the_recorder()
    {
        var database = await DatabaseAsync();
        await using var alvo = await StartAsync(database, alvo => alvo.Entity(Bookings, b => Rejecting(b, "beforeWrite")));
        var clerk = Clerk();

        await Should.ThrowAsync<AlvoWriteRejectedException>(() => alvo.Data.CreateAsync(Bookings, Booking(clerk, "recorded"), clerk, cancellationToken: Ct));

        alvo.Hooks.AssertRan("beforeWrite", times: 1);
    }

    protected abstract Task<HookDatabase> DatabaseAsync();

    /// <summary>
    /// Proves, from inside a validate hook, that no transaction of the write is open (acceptance (8), Ruling 44). SQLite: the
    /// hook's own write above would wait on the held writer lock and fail its <c>WaitAsync</c>, so nothing more is needed;
    /// PostgreSQL overrides this to find no other backend of the database <c>idle in transaction</c>.
    /// </summary>
    protected virtual Task AssertNoOpenTransactionAsync(HookDatabase database) => Task.CompletedTask;

    /// <summary>A host whose hooks run on a clock that never moves unless a fact moves it: no fact here races the 250 ms default (Ruling 40).</summary>
    private static Task<AlvoTestHost> StartAsync(HookDatabase database, Action<IAlvoBuilder> hooks, TimeProvider? time = null) =>
        AlvoTestHost.StartAsync(
            alvo =>
            {
                database.Use(alvo);
                alvo.FromDescriptor(Path.Combine(AppContext.BaseDirectory, "descriptors", "hook-desk.alvo.json"));
                hooks(alvo);
            },
            time ?? new FakeTimeProvider(DateTimeOffset.UtcNow),
            cancellationToken: Ct);

    private static void Rejecting(MMLib.Alvo.Extensions.AlvoEntityBuilder bookings, string stage)
    {
        _ = stage switch
        {
            "validate" => bookings.Validate(stage, (ValidateWrite<AlvoRecord> write) => Done(() => write.Invalid("rejected", "Rejected.", "title"))),
            "beforeWrite" => bookings.BeforeWrite(stage, (BeforeWrite<AlvoRecord> write) => Done(() => write.Conflict("rejected", "Rejected.", "title"))),
            "afterWrite" => bookings.AfterWrite(stage, (AfterWrite<AlvoRecord> write) => Done(() => write.Forbidden("rejected", "Rejected."))),
            "beforeDelete" => bookings.BeforeDelete(stage, (BeforeDelete<AlvoRecord> write) => Done(() => write.Conflict("rejected", "Rejected."))),
            _ => bookings.AfterDelete(stage, (AfterDelete<AlvoRecord> write) => Done(() => write.Conflict("rejected", "Rejected."))),
        };
    }

    private static ValueTask Done(Action record)
    {
        record();
        return ValueTask.CompletedTask;
    }

    private static IAlvoOperations AsAdministrator(AlvoTestHost alvo)
    {
        alvo.Services.GetRequiredService<IAlvoContextAccessor>().Principal = new AlvoPrincipal
        {
            Context = AlvoTestHost.Caller("manager"),
            Scopes = new HashSet<ApiKeyScope>(),
            KeyId = "manager-key",
        };

        return alvo.Services.GetRequiredService<IAlvoOperations>();
    }

    private static AlvoContext Clerk() => AlvoTestHost.Caller("clerk") with { Tenant = new TenantId(Guid.Parse("22222222-0000-0000-0000-000000000002")) };

    private static Dictionary<string, object?> Booking(AlvoContext caller, string title) => new()
    {
        ["title"] = title,
        ["tenant_id"] = caller.Tenant!.Value.Value,
    };

    private static Dictionary<string, object?> Audit(AlvoContext caller, string note) => new()
    {
        ["note"] = note,
        ["tenant_id"] = caller.Tenant!.Value.Value,
    };

    protected sealed record HookDatabase(Action<IAlvoBuilder> Use, Func<DbConnection> Connect);
}
```

`test/MMLib.Alvo.TestHost.Tests/SqliteWriteHookTests.cs` — the `SqliteTransactionTests` shape (a temp file per fact, pools cleared and files deleted on dispose), answering `new HookDatabase(alvo => alvo.UseSqlite(connectionString), () => new SqliteConnection(connectionString))`. `test/MMLib.Alvo.Data.PostgreSql.Tests.Integration/PostgreSqlWriteHookTests.cs` — the `PostgreSqlTransactionTests` shape (`[Collection(DispatchedEventCollection.Name)]`, a database per fact, `Assert.SkipWhen` without Docker), answering `new HookDatabase(alvo => alvo.UsePostgreSql(connectionString), () => new NpgsqlConnection(connectionString))`, and overriding the probe:

```csharp
    protected override async Task AssertNoOpenTransactionAsync(HookDatabase database)
    {
        await using var connection = database.Connect();
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM pg_stat_activity WHERE datname = current_database() AND pid <> pg_backend_pid() AND state LIKE 'idle in transaction%'";

        Convert.ToInt64(await command.ExecuteScalarAsync(TestContext.Current.CancellationToken), CultureInfo.InvariantCulture)
            .ShouldBe(0, "a validate hook runs with no transaction of the write open");
    }
```

- [ ] **Step 2: Run them to verify which fail**

Run: `dotnet test --project test/MMLib.Alvo.TestHost.Tests --filter-class '*SqliteWriteHookTests'`
Expected: the recorder facts (`…first-line`, `…undo`, `A_rejected_run_counts_as_ran…`) FAIL — writes through `alvo.Data` are not tracked; the rest PASS after Tasks 1–9 (if any other fails, the defect is in the task that owns it: fix it there).

- [ ] **Step 3: Track writes for the recorder**

`src/MMLib.Alvo.TestHost/Internal/TrackedData.cs`:

```csharp
using MMLib.Alvo.Data;
using System.Diagnostics;

namespace MMLib.Alvo.TestHost.Internal;

/// <summary>
/// The test host's <see cref="AlvoTestHost.Data"/>: the host's own port, each call inside a recorded root activity, so the write
/// hooks a call runs are this host's runs for <see cref="AlvoHookRecorder"/> (Ruling 27).
/// </summary>
internal sealed class TrackedData(IAlvoData inner, Func<Activity?> track) : IAlvoData
{
    public async Task<AlvoPage> QueryAsync(AlvoQuery query, AlvoContext context, CancellationToken cancellationToken = default)
    {
        using var tracked = track();
        return await inner.QueryAsync(query, context, cancellationToken).ConfigureAwait(false);
    }

    public async Task<AlvoRecord?> GetAsync(string entity, Guid id, AlvoContext context, CancellationToken cancellationToken = default)
    {
        using var tracked = track();
        return await inner.GetAsync(entity, id, context, cancellationToken).ConfigureAwait(false);
    }

    public async Task<AlvoRecord> CreateAsync(
        string entity, IReadOnlyDictionary<string, object?> values, AlvoContext context, AlvoIdempotency? idempotency = null, CancellationToken cancellationToken = default)
    {
        using var tracked = track();
        return await inner.CreateAsync(entity, values, context, idempotency, cancellationToken).ConfigureAwait(false);
    }

    public async Task<AlvoRecord> UpdateAsync(
        string entity, Guid id, IReadOnlyDictionary<string, object?> values, AlvoContext context, AlvoPrecondition? precondition = null,
        AlvoIdempotency? idempotency = null, CancellationToken cancellationToken = default)
    {
        using var tracked = track();
        return await inner.UpdateAsync(entity, id, values, context, precondition, idempotency, cancellationToken).ConfigureAwait(false);
    }

    public async Task<AlvoReplaceResult> ReplaceAsync(
        string entity, Guid id, IReadOnlyDictionary<string, object?> values, AlvoContext context, AlvoPrecondition? precondition = null,
        AlvoIdempotency? idempotency = null, CancellationToken cancellationToken = default)
    {
        using var tracked = track();
        return await inner.ReplaceAsync(entity, id, values, context, precondition, idempotency, cancellationToken).ConfigureAwait(false);
    }

    public async Task DeleteAsync(
        string entity, Guid id, AlvoContext context, AlvoPrecondition? precondition = null, AlvoIdempotency? idempotency = null, CancellationToken cancellationToken = default)
    {
        using var tracked = track();
        await inner.DeleteAsync(entity, id, context, precondition, idempotency, cancellationToken).ConfigureAwait(false);
    }

    public async Task<AlvoBatchResult> CreateManyAsync(
        string entity, IReadOnlyList<IReadOnlyDictionary<string, object?>> rows, AlvoContext context, AlvoIdempotency? idempotency = null, CancellationToken cancellationToken = default)
    {
        using var tracked = track();
        return await inner.CreateManyAsync(entity, rows, context, idempotency, cancellationToken).ConfigureAwait(false);
    }

    public async Task<AlvoBatchResult> UpdateManyAsync(
        string entity, IReadOnlyList<AlvoRowPatch> rows, AlvoContext context, AlvoIdempotency? idempotency = null, CancellationToken cancellationToken = default)
    {
        using var tracked = track();
        return await inner.UpdateManyAsync(entity, rows, context, idempotency, cancellationToken).ConfigureAwait(false);
    }

    public async Task<AlvoBatchResult> DeleteManyAsync(
        string entity, IReadOnlyList<Guid> ids, AlvoContext context, AlvoIdempotency? idempotency = null, CancellationToken cancellationToken = default)
    {
        using var tracked = track();
        return await inner.DeleteManyAsync(entity, ids, context, idempotency, cancellationToken).ConfigureAwait(false);
    }
}
```

`AlvoTestHost.cs` — the constructor wraps the port: `_data = new TrackedData(composition.Host.Services.GetRequiredService<IAlvoData>(), StartDrain);` (`StartDrain` is the existing root-activity method; it is now also each tracked call's root). The class remarks' sentence "from write hooks on, it is what scopes a write's hooks to Hooks" is now true; no public member changes, so the TestHost baseline does not move. `TestHostBoundaryTests` (`grep -rn 'Data' test/MMLib.Alvo.TestHost.Tests/TestHostBoundaryTests.cs`) — if a fact pins `Data`'s identity to the host's `IAlvoData`, HX-02's notes say none does; if one does, it now pins that `Data` *behaves* as the host's port (a reject refuses), which `AlvoTestHostFacts` already holds.

- [ ] **Step 4: Run both legs and ring1**

Run: `dotnet test --project test/MMLib.Alvo.TestHost.Tests --filter-class '*SqliteWriteHookTests'` → PASS; `dotnet test --project test/MMLib.Alvo.TestHost.Tests --filter-class '*AlvoHookRecorderTests'` → PASS (unchanged); `dotnet test --project test/MMLib.Alvo.Data.PostgreSql.Tests.Integration --filter-class '*PostgreSqlWriteHookTests'` (Docker) → PASS. `scripts/test-ring1`.

- [ ] **Step 5: Commit**

```bash
dotnet format whitespace MMLib.Alvo.slnx --include src/MMLib.Alvo.TestHost/Internal/TrackedData.cs src/MMLib.Alvo.TestHost/AlvoTestHost.cs test/_shared/testhost/WriteHookFacts.cs test/_shared/testhost/WriteHookRows.cs test/MMLib.Alvo.TestHost.Tests/SqliteWriteHookTests.cs test/MMLib.Alvo.Data.PostgreSql.Tests.Integration/PostgreSqlWriteHookTests.cs
scripts/test-ring1
git add src/MMLib.Alvo.TestHost test/_shared/testhost test/MMLib.Alvo.TestHost.Tests/SqliteWriteHookTests.cs test/MMLib.Alvo.Data.PostgreSql.Tests.Integration/PostgreSqlWriteHookTests.cs
git commit -m "test(hooks): write hooks end to end on SQLite and PostgreSQL

Claude-Session: https://claude.ai/code/session_01NJAtafM29iddLwfhb5gnmB"
```

(Every fact passes at this commit: Task 10, which switches validate hooks off, has landed — Ruling 50.)

---

## Task 12: The dashboard — the write lanes, their numbers, the editors' note, the simulator's and the preview's code list **[slice end]**

**Files:**
- Create: `test/MMLib.Alvo.Admin.Tests/Schema/LifecycleLaneWriteHookTests.cs`, `test/MMLib.Alvo.Admin.Tests.EndToEnd/WriteHookWorld.cs`, `test/MMLib.Alvo.Admin.Tests.EndToEnd/WriteHookScenarios.cs`
- Modify: `src/MMLib.Alvo.Admin/Components/Schema/LifecycleLane.cs`, `LifecycleTab.razor`, `Entity.razor`, `Entity.razor.cs`, `HooksTab.razor` (+ its `.razor.cs` parameter), `Preview.razor`; `src/MMLib.Alvo.Admin/Components/Rules/RulesTab.razor`, `PolicyVerdictView.razor`; `test/MMLib.Alvo.Admin.Tests/PublicApi.MMLib.Alvo.Admin.verified.txt`
- Test: the Admin unit facts; e2e `WriteHookScenarios` and `WriteLaneAbsenceScenarios` (filtered run; the whole suite runs once, in Task 14)

**Interfaces:**
- Consumes: the catalog (Tasks 6/7/11: `Kind`, `Stage`, `TopLevelOnly`, `Enabled`, `When`, `Timeout`, `Operations`); `ManagementExtensionStats` via `OperationsGateway.StatsAsync`; `ManagementPolicyVerdict.Extensions`.
- Produces: `LifecycleLane.BeforeTransaction`, `.InTransaction`, `.HasWriteHooks`, `.IsWriteHook`, `.StageLabel`, `.Numbers`, `.AlsoRuns`, `.Touching`; component parameters `LifecycleTab.Stats`, `HooksTab.Extensions`, `RulesTab.Extensions` (public by construction — HX-04 Ruling 19; the Admin baseline grows by exactly these three properties).

- [ ] **Step 1: Write the failing unit facts**

`test/MMLib.Alvo.Admin.Tests/Schema/LifecycleLaneWriteHookTests.cs`:

```csharp
using MMLib.Alvo.Admin.Components.Schema;
using MMLib.Alvo.Management;

namespace MMLib.Alvo.Admin.Tests.Schema;

/// <summary>What the Lifecycle tab's write lanes draw (spec 2026-10-14 §4.13, Ruling 29), and the editors' note.</summary>
public sealed class LifecycleLaneWriteHookTests
{
    private static readonly IReadOnlyList<ManagementExtension> _extensions =
    [
        Hook("check-region", AlvoExtensionKind.Validate, AlvoExtensionStage.PreTransaction, ["work_orders"], ["create", "update"]) with { TopLevelOnly = true },
        Hook("stamp", AlvoExtensionKind.BeforeWrite, AlvoExtensionStage.InTransaction, ["work_orders"], ["create", "update"]),
        Hook("audit", AlvoExtensionKind.AfterWrite, AlvoExtensionStage.InTransaction, ["*"], ["create", "update"]),
        Hook("tidy", AlvoExtensionKind.AfterDelete, AlvoExtensionStage.InTransaction, ["work_orders"], ["delete"]),
        Hook("notify", AlvoExtensionKind.EventHandler, AlvoExtensionStage.PostCommit, ["work_orders"], ["update"]),
    ];

    [Fact]
    public void The_before_lane_holds_validate_hooks_and_the_in_lane_every_in_transaction_hook_every_entity_ones_included()
    {
        LifecycleLane.BeforeTransaction("work_orders", _extensions).Select(item => item.Id).ShouldBe(["check-region"]);
        LifecycleLane.InTransaction("work_orders", _extensions).Select(item => item.Id).ShouldBe(["stamp", "audit", "tidy"]);
        LifecycleLane.AfterCommit("work_orders", _extensions).Select(item => item.Id).ShouldBe(["notify"]);
    }

    [Fact]
    public void The_lanes_are_drawn_only_in_a_host_that_registers_a_write_hook()
    {
        LifecycleLane.HasWriteHooks(_extensions).ShouldBeTrue();
        LifecycleLane.HasWriteHooks([_extensions[^1]]).ShouldBeFalse();
    }

    [Fact]
    public void A_code_item_s_numbers_are_its_p95_and_its_rejections_over_a_day_and_nothing_without_stats()
    {
        var stats = new ManagementExtensionStats
        {
            Scope = "this-instance",
            Window = TimeSpan.FromHours(24),
            Since = DateTimeOffset.UnixEpoch,
            Items = [new ManagementExtensionStat { Id = "stamp", Invocations = 40, Rejections = 3, P95 = TimeSpan.FromMilliseconds(12) }],
        };

        LifecycleLane.Numbers(_extensions[1], stats).ShouldBe("p95 12 ms · 3 rejections in 24 h");
        LifecycleLane.Numbers(_extensions[1], null).ShouldBeNull();
        LifecycleLane.Numbers(_extensions[0], stats).ShouldBeNull();
    }

    [Theory]
    [InlineData("create", "3 extensions from code also run on this operation")]
    [InlineData("delete", "1 extension from code also runs on this operation")]
    [InlineData("list", null)]
    public void The_editors_note_counts_the_write_hooks_of_one_operation(string operation, string? note) =>
        LifecycleLane.AlsoRuns("work_orders", operation, _extensions).ShouldBe(note);

    [Fact]
    public void The_preview_lists_the_code_bound_to_any_entity_the_change_touches_once()
    {
        LifecycleLane.Touching(["work_orders", "work_orders"], _extensions).Select(item => item.Id).ShouldBe(["check-region", "stamp", "audit", "tidy", "notify"]);
        LifecycleLane.Touching(["regions"], _extensions).Select(item => item.Id).ShouldBe(["audit"]);
    }

    [Fact]
    public void Each_kind_has_a_stage_label() =>
        _extensions.Select(LifecycleLane.StageLabel).ShouldBe(["validate", "before write", "after write", "after delete", "after commit"]);

    private static ManagementExtension Hook(string id, AlvoExtensionKind kind, AlvoExtensionStage stage, string[] entities, string[] operations) =>
        new() { Id = id, Kind = kind, Stage = stage, Entities = entities, Operations = operations };
}
```

- [ ] **Step 2: Run them to verify they fail**

Run: `dotnet test --project test/MMLib.Alvo.Admin.Tests --filter-class '*LifecycleLaneWriteHookTests'`
Expected: FAIL — the members do not exist.

- [ ] **Step 3: Write the lane logic and the markup**

`LifecycleLane.cs` — add:

```csharp
    /// <summary>The validate hooks the Before the transaction lane draws for <paramref name="entity"/>.</summary>
    public static IReadOnlyList<ManagementExtension> BeforeTransaction(string entity, IReadOnlyList<ManagementExtension> extensions) =>
        [.. Binding(entity, extensions).Where(extension => extension.Kind == AlvoExtensionKind.Validate)];

    /// <summary>The before/after write and delete hooks the In the transaction lane draws for <paramref name="entity"/>.</summary>
    public static IReadOnlyList<ManagementExtension> InTransaction(string entity, IReadOnlyList<ManagementExtension> extensions) =>
        [.. Binding(entity, extensions).Where(extension => extension.Stage == AlvoExtensionStage.InTransaction)];

    /// <summary>Whether the write lanes are drawn: only in a host that registers a write hook (Ruling 29, HX-08 Ruling 57's rule).</summary>
    public static bool HasWriteHooks(IReadOnlyList<ManagementExtension> extensions) => extensions.Any(IsWriteHook);

    /// <summary>Whether <paramref name="extension"/> is a write hook of any stage.</summary>
    public static bool IsWriteHook(ManagementExtension extension) => extension.Kind is AlvoExtensionKind.Validate or AlvoExtensionKind.BeforeWrite
        or AlvoExtensionKind.AfterWrite or AlvoExtensionKind.BeforeDelete or AlvoExtensionKind.AfterDelete;

    /// <summary>When, in a write, <paramref name="extension"/> runs, in two words.</summary>
    public static string StageLabel(ManagementExtension extension) => extension.Kind switch
    {
        AlvoExtensionKind.Validate => "validate",
        AlvoExtensionKind.BeforeWrite => "before write",
        AlvoExtensionKind.AfterWrite => "after write",
        AlvoExtensionKind.BeforeDelete => "before delete",
        AlvoExtensionKind.AfterDelete => "after delete",
        AlvoExtensionKind.Command => "command",
        _ => "after commit",
    };

    /// <summary>A code item's numbers on this instance — its p95 and its rejections in the last day — or nothing without them.</summary>
    public static string? Numbers(ManagementExtension extension, ManagementExtensionStats? stats) =>
        stats?.Items.FirstOrDefault(item => item.Id == extension.Id) is { P95: { } p95 } stat
            ? string.Create(CultureInfo.InvariantCulture, $"p95 {p95.TotalMilliseconds:0.#} ms · {stat.Rejections} rejection{(stat.Rejections == 1 ? "" : "s")} in 24 h")
            : null;

    /// <summary>The editors' note: how many write hooks also run on <paramref name="operation"/>, or nothing.</summary>
    public static string? AlsoRuns(string entity, string operation, IReadOnlyList<ManagementExtension> extensions)
    {
        var count = Binding(entity, extensions).Count(extension => IsWriteHook(extension) && extension.Operations.Contains(operation, StringComparer.Ordinal));

        return count switch
        {
            0 => null,
            1 => "1 extension from code also runs on this operation",
            _ => string.Create(CultureInfo.InvariantCulture, $"{count} extensions from code also run on this operation"),
        };
    }

    /// <summary>The extensions bound to any of <paramref name="entities"/>, each once, in catalog order.</summary>
    public static IReadOnlyList<ManagementExtension> Touching(IEnumerable<string> entities, IReadOnlyList<ManagementExtension> extensions)
    {
        var touched = entities.ToHashSet(StringComparer.Ordinal);
        return [.. extensions.Where(extension => touched.Any(entity => Binding(entity, [extension]).Count > 0))];
    }
```

`LifecycleTab.razor` — a `Stats` parameter, two lanes before *After commit*, one templated code item shared by every code lane (the *After commit* lane's own item markup moves into it, unchanged except the stage badge and the numbers):

```razor
@if (_showsWriteLanes)
{
    <div data-testid="lifecycle-before-transaction">
        <SectionHead Title="Before the transaction">
            <Subtitle>
                Validate hooks run before a write opens its transaction — they may call another service, so they hold no lock —
                and only for a write no transaction unit is open around. They can only refuse the write.
            </Subtitle>
        </SectionHead>
        @if (_before.Count == 0)
        {
            <div data-testid="lifecycle-before-empty">
                <EmptyState Title="Nothing checks a write before its transaction" Body="The host registers no validate hook for this entity." />
            </div>
        }
        @foreach (var extension in _before)
        {
            @CodeItem(extension)
        }
    </div>

    <div data-testid="lifecycle-in-transaction">
        <SectionHead Title="In the transaction">
            <Subtitle>
                Before- and after-write hooks run inside the write's own transaction, after the descriptor's before-hooks: they
                may refuse, change the row or write other rows, and anything they do rolls back with the write. They never
                leave the process.
            </Subtitle>
        </SectionHead>
        @if (_inside.Count == 0)
        {
            <div data-testid="lifecycle-in-empty">
                <EmptyState Title="No host code runs inside a write to this entity" Body="The host registers no before- or after-write hook for it." />
            </div>
        }
        @foreach (var extension in _inside)
        {
            @CodeItem(extension)
        }
    </div>
}
```

The *After commit* lane's `@foreach (var extension in _extensions)` body becomes `@CodeItem(extension)`, and its subtitle's last sentence reads `@(_showsWriteLanes ? "The descriptor's before-hooks are on the On write tab." : "This tab shows what runs after the commit only — before-hooks, which run inside the transaction, are on the On write tab.")`. In `@code`:

```csharp
    private IReadOnlyList<ManagementExtension> _before = [];
    private IReadOnlyList<ManagementExtension> _inside = [];
    private bool _showsWriteLanes;

    /// <summary>This instance's numbers per extension over the last day; <see langword="null"/> when they could not be read.</summary>
    [Parameter]
    public ManagementExtensionStats? Stats { get; set; }

    private RenderFragment CodeItem(ManagementExtension extension) => @<ListRow class="a-listrow--roomy a-listrow--stacked" data-testid="@($"lane-extension-{extension.Id}")">
        <div class="a-row a-row--wrap">
            <span class="a-badge a-badge--accent">⟨/⟩ from code</span>
            <span class="a-mono a-name">@extension.Id</span>
            <span class="a-badge" data-testid="lane-extension-stage">@LifecycleLane.StageLabel(extension)</span>
            @if (extension.TopLevelOnly)
            {
                <span class="a-badge" data-testid="lane-extension-top-level">top-level writes only</span>
            }
            @if (!extension.Enabled)
            {
                <span class="a-badge a-badge--warn" data-testid="lane-extension-off">switched off</span>
            }
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
        @if (LifecycleLane.Numbers(extension, Stats) is { } numbers)
        {
            <span class="a-section__sub" data-testid="lane-extension-numbers">@numbers</span>
        }
        <p class="a-note">Registered in the host's C#: it is changed there, never here.</p>
    </ListRow>;
```

and `OnParametersSet` adds `_before = LifecycleLane.BeforeTransaction(Entity, Extensions); _inside = LifecycleLane.InTransaction(Entity, Extensions); _showsWriteLanes = LifecycleLane.HasWriteHooks(Extensions);`. The header comment names the four lanes now drawn.

`Entity.razor.cs` — inject `OperationsGateway` (the file's injection style: `[Inject] private OperationsGateway Operations { get; set; } = default!;`), keep `private ManagementExtensionStats? _stats;`, and after `_extensions = await Gateway.ExtensionsAsync(ct);`:

```csharp
        _stats = LifecycleLane.HasWriteHooks(_extensions) ? await StatsOrNoneAsync(ct) : null;
```

```csharp
    /// <summary>This instance's numbers, or none when the operator may not read them — the lanes then draw no numbers.</summary>
    private async Task<ManagementExtensionStats?> StatsOrNoneAsync(CancellationToken ct)
    {
        try
        {
            return await Operations.StatsAsync(ct);
        }
        catch (ManagementForbiddenException)
        {
            return null;
        }
    }
```

`Entity.razor` passes `Stats="@_stats"` to `LifecycleTab`, `Extensions="@_extensions"` to `HooksTab` and `RulesTab`.

`HooksTab` and `RulesTab` gain `[Parameter] public IReadOnlyList<ManagementExtension> Extensions { get; set; } = [];` and, right under their `SectionHead`:

```razor
@foreach (var operation in new[] { "create", "update", "delete" })
{
    @if (LifecycleLane.AlsoRuns(Entity, operation, Extensions) is { } note)
    {
        <p class="a-note" data-testid="@($"code-also-runs-{operation}")">On @operation: @note — <a class="a-link" href="@AdminPaths.Entity(Entity, EntityTabs.FromSlug(EntityTabs.Lifecycle))">see the Lifecycle tab</a>.</p>
    }
}
```

`PolicyVerdictView.razor` — after the commands block (`@using MMLib.Alvo.Admin.Components.Schema` at the top):

```razor
@if (Verdict.Extensions.Count > 0)
{
    <div class="a-field" data-testid="verdict-extensions">
        <span class="a-label">Code that also runs on this operation</span>
        @foreach (var extension in Verdict.Extensions)
        {
            <div class="a-row a-row--wrap" data-testid="@($"verdict-extension-{extension.Id}")">
                <span class="a-badge a-badge--accent">⟨/⟩ from code</span>
                <span class="a-mono a-name">@extension.Id</span>
                <span class="a-section__sub" data-testid="verdict-extension-stage">@LifecycleLane.StageLabel(extension)</span>
            </div>
        }
        <span class="a-section__sub">
            Registered in the host's C#. This screen judges the rules only: a hook may still refuse, or change, a write the rules admit.
        </span>
    </div>
}
```

`Preview.razor` — read the catalog where the plan is read (`_extensions = await Gateway.ExtensionsAsync(ct);`) and render after the plan's steps:

```razor
@if (_plan is { } preview && LifecycleLane.Touching(preview.Plan.Steps.Select(step => step.Change.Entity), _extensions) is { Count: > 0 } touched)
{
    <div class="a-field" data-testid="preview-extensions">
        <span class="a-label">Code bound to what this change touches</span>
        @foreach (var extension in touched)
        {
            <div class="a-row a-row--wrap" data-testid="@($"preview-extension-{extension.Id}")">
                <span class="a-badge a-badge--accent">⟨/⟩ from code</span>
                <span class="a-mono a-name">@extension.Id</span>
                <span class="a-section__sub">@LifecycleLane.StageLabel(extension) · @string.Join(", ", extension.Entities)</span>
            </div>
        }
        <span class="a-section__sub">A change that would unbind one of them is refused when you apply it, naming it.</span>
    </div>
}
```

(Every new `data-testid` must be one `EndToEndSelectorTests` accepts — read its rule and follow it.)

- [ ] **Step 4: Write the e2e world and scenarios**

`test/MMLib.Alvo.Admin.Tests.EndToEnd/WriteHookWorld.cs`:

```csharp
using MMLib.Alvo.Data;
using MMLib.Alvo.Hooks;

namespace MMLib.Alvo.Admin.Tests.EndToEnd;

/// <summary>The field-service world with one write hook of three stages on work orders — its own world, so the extension world's counts stay.</summary>
public sealed class WriteHookWorld : AdminWorld
{
    internal const string Check = "check-region";
    internal const string Stamp = "stamp-dispatcher";
    internal const string Tidy = "tidy-notes";

    protected override void Configure(IAlvoBuilder alvo) => alvo.Entity("work_orders", orders => orders
        .Validate(Check, (ValidateWrite<AlvoRecord> _) => ValueTask.CompletedTask, configure: o => o.Description = "Checks the region with the dispatch service.")
        .BeforeWrite(Stamp, (BeforeWrite<AlvoRecord> _) => ValueTask.CompletedTask, when: "changed(status)")
        .AfterDelete(Tidy, (AfterDelete<AlvoRecord> _) => ValueTask.CompletedTask));
}
```

`test/MMLib.Alvo.Admin.Tests.EndToEnd/WriteHookScenarios.cs`:

```csharp
using Microsoft.Playwright;

namespace MMLib.Alvo.Admin.Tests.EndToEnd;

/// <summary>Write hooks on the dashboard (spec 2026-10-14 §7 HX-09): the two write lanes, the editors' note, the simulator's code list.</summary>
/// <param name="world">The field-service world with write hooks, and a browser.</param>
public sealed class WriteHookScenarios(WriteHookWorld world) : IClassFixture<WriteHookWorld>
{
    [Theory(Timeout = AdminWorld.ScenarioTimeout)]
    [InlineData(1400)]
    [InlineData(375)]
    public async Task The_lifecycle_tab_draws_validate_before_the_transaction_and_the_rest_inside_read_only(int width)
    {
        await using var session = await world.SignInAsync(TestContext.Current.CancellationToken, width);
        await session.GoAsync("/schema/work_orders?tab=lifecycle");

        var check = session.Page.GetByTestId("lifecycle-before-transaction").GetByTestId($"lane-extension-{WriteHookWorld.Check}");
        await check.WaitForAsync();
        (await check.GetByTestId("lane-extension-top-level").CountAsync()).ShouldBe(1);
        var inside = session.Page.GetByTestId("lifecycle-in-transaction");
        (await inside.GetByTestId($"lane-extension-{WriteHookWorld.Stamp}").GetByTestId("lane-extension-when").InnerTextAsync()).Trim().ShouldBe("when changed(status)");
        (await inside.GetByTestId($"lane-extension-{WriteHookWorld.Tidy}").GetByTestId("lane-extension-stage").InnerTextAsync()).Trim().ShouldBe("after delete");
        (await session.Page.GetByTestId("lifecycle-before-transaction").GetByRole(AriaRole.Button).CountAsync()).ShouldBe(0, "host code is read-only on the dashboard");
        await session.AssertNoHorizontalScrollAsync();
        session.AssertConsoleClean();
    }

    [Fact(Timeout = AdminWorld.ScenarioTimeout)]
    public async Task The_on_write_tab_says_code_also_runs_and_links_to_the_lifecycle_tab()
    {
        await using var session = await world.SignInAsync(TestContext.Current.CancellationToken);
        await session.GoAsync("/schema/work_orders?tab=on-write");

        var note = session.Page.GetByTestId("code-also-runs-update");
        await note.WaitForAsync();
        (await note.InnerTextAsync()).ShouldContain("2 extensions from code also run on this operation");
        (await session.Page.GetByTestId("code-also-runs-delete").InnerTextAsync()).ShouldContain("1 extension from code also runs");
        session.AssertConsoleClean();
    }

    [Fact(Timeout = AdminWorld.ScenarioTimeout)]
    public async Task The_simulator_lists_the_code_that_also_runs_on_the_simulated_operation()
    {
        await using var session = await world.SignInAsync(TestContext.Current.CancellationToken);
        await session.GoAsync("/rules/work_orders");

        await PolicySimulation.ChooseOperationAsync(session, "update");

        var listed = session.Page.GetByTestId("verdict-extensions");
        await listed.WaitForAsync();
        (await listed.GetByTestId($"verdict-extension-{WriteHookWorld.Stamp}").CountAsync()).ShouldBe(1);
        (await listed.GetByTestId($"verdict-extension-{WriteHookWorld.Tidy}").CountAsync()).ShouldBe(0, "an after-delete hook does not run on an update");
        session.AssertConsoleClean();
    }
}

/// <summary>A host with handlers and commands but no write hook draws no write lane (Ruling 29).</summary>
/// <param name="world">The extension world.</param>
public sealed class WriteLaneAbsenceScenarios(ExtensionWorld world) : IClassFixture<ExtensionWorld>
{
    [Fact(Timeout = AdminWorld.ScenarioTimeout)]
    public async Task A_host_with_no_write_hook_draws_no_write_lanes()
    {
        await using var session = await world.SignInAsync(TestContext.Current.CancellationToken);
        await session.GoAsync("/schema/work_orders?tab=lifecycle");
        await session.Page.GetByTestId("lifecycle-after-commit").WaitForAsync();

        (await session.Page.GetByTestId("lifecycle-before-transaction").CountAsync()).ShouldBe(0);
        (await session.Page.GetByTestId("lifecycle-in-transaction").CountAsync()).ShouldBe(0);
        session.AssertConsoleClean();
    }
}
```

(`PolicySimulation.ChooseOperationAsync` — if the e2e project has no helper that picks the simulator's operation, write it beside the scenarios as a 10-line static class selecting the operation the way the policy scenarios do: read `PolicyScenarios.cs`/`SimulationRefusalScenarios.cs` for the operation control's test id or label. The scenario's assertion is what matters.)

- [ ] **Step 5: Run the Admin facts, the baseline and the e2e suite**

Run: `dotnet test --project test/MMLib.Alvo.Admin.Tests --filter-class '*LifecycleLane*'`, `--filter-class '*EndToEndSelectorTests'`, then `--filter-class '*AdminPublicApi*'` — accept the received diff only if it adds exactly `LifecycleTab.Stats`, `HooksTab.Extensions`, `RulesTab.Extensions`; dispatch `alvo-snapshot-judge`. Then `scripts/test-ring1` and only the scenarios this task touches: `dotnet test --project test/MMLib.Alvo.Admin.Tests.EndToEnd --filter-class '*WriteHookScenarios' --filter-class '*WriteLaneAbsenceScenarios' --filter-class '*ExtensionScenarios' --filter-class '*CommandScenarios'` (the full suite runs once, in Task 14; `ExtensionWorld`'s badge count does not move — it registers no write hook).

- [ ] **Step 6: Commit**

```bash
dotnet format whitespace MMLib.Alvo.slnx --include src/MMLib.Alvo.Admin/Components/Schema/LifecycleLane.cs src/MMLib.Alvo.Admin/Components/Schema/Entity.razor.cs test/MMLib.Alvo.Admin.Tests/Schema/LifecycleLaneWriteHookTests.cs test/MMLib.Alvo.Admin.Tests.EndToEnd/WriteHookWorld.cs test/MMLib.Alvo.Admin.Tests.EndToEnd/WriteHookScenarios.cs
git add src/MMLib.Alvo.Admin test/MMLib.Alvo.Admin.Tests test/MMLib.Alvo.Admin.Tests.EndToEnd
git commit -m "feat(admin): the write lanes, the editors' note, and code in the simulator and the preview

Claude-Session: https://claude.ai/code/session_01NJAtafM29iddLwfhb5gnmB"
```

---

## Task 13: Docs — *Validate and guard writes* with the booking recipe end to end, *Where logic goes*, C13, the architecture doc, CHANGELOG, PLAN, the spec amendment **[slice end]**

**Files:**
- Create: `website/src/content/docs/extending/write-hooks.mdx`, `website/src/snippets/extending/write-hooks/opening-desk.alvo.json`, `website/src/snippets/extending/write-hooks/OpeningDeskHooks.cs`, `website/src/snippets/extending/write-hooks/OpeningDeskHooksTests.cs`, `docs/architecture/write-hooks.md`
- Modify: `website/astro.config.mjs`, `website/src/content/docs/extending/{where-logic-goes.mdx,index.mdx,transactions.mdx,testing.mdx,extension-catalog.mdx,commands.mdx}`, `docs/architecture/events.md`, `docs/architecture/data-path.md`, `docs/architecture/extensibility.md`, `CHANGELOG.md`, `docs/PLAN.md`, `docs/superpowers/specs/2026-10-14-host-extensibility-design.md`
- Test: `OpeningDeskHooksTests` in `MMLib.Alvo.DocsSnippets.Tests` (ring0 — the snippets are its source files); the website build (`cd website && npm run build`)

**Interfaces:**
- Consumes: the whole public surface of Tasks 5–6; `AlvoTestHost`.
- Produces: the docs page and its executed snippets; the spec's *HX-09 settled details*.

- [ ] **Step 1: Write the recipe's snippets (typed form first) and their tests**

`website/src/snippets/extending/write-hooks/opening-desk.alvo.json`:

```json
{
  "$schema": "https://alvo.dev/schema/v1/project.json",
  "apiVersion": "alvo.dev/v1",
  "name": "opening-desk",
  "access": { "admin": "'manager' in @user.roles" },
  "auth": { "roles": ["staff", "manager"] },
  "entities": {
    "opening_hours": {
      "fields": {
        "resource_id": { "type": "uuid", "required": true },
        "weekday": { "type": "integer", "required": true },
        "opens_hour": { "type": "integer", "required": true },
        "closes_hour": { "type": "integer", "required": true }
      },
      "rules": {
        "list": "'manager' in @user.roles || 'admin' in @user.roles",
        "get": "'manager' in @user.roles || 'admin' in @user.roles",
        "create": "'manager' in @user.roles || 'admin' in @user.roles"
      }
    },
    "bookings": {
      "fields": {
        "resource_id": { "type": "uuid", "required": true },
        "guest": { "type": "string", "required": true, "maxLength": 80 },
        "starts_at": { "type": "datetime", "required": true },
        "ends_at": { "type": "datetime", "required": true },
        "vat_id": { "type": "string", "maxLength": 20 },
        "status": { "type": "enum", "values": ["requested", "confirmed"], "default": "requested" }
      },
      "rules": {
        "list": "'staff' in @user.roles",
        "get": "'staff' in @user.roles",
        "create": "'staff' in @user.roles",
        "update": "'staff' in @user.roles"
      }
    },
    "booking_audits": {
      "fields": {
        "booking_id": { "type": "uuid", "required": true },
        "note": { "type": "string", "required": true, "maxLength": 40 }
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

`website/src/snippets/extending/write-hooks/OpeningDeskHooks.cs`:

```csharp
using Microsoft.Extensions.DependencyInjection;
using MMLib.Alvo;
using MMLib.Alvo.Data;
using MMLib.Alvo.Events;
using MMLib.Alvo.Hooks;

namespace OpeningDesk;

[AlvoEntity("bookings")]
public sealed class Booking
{
    public Guid Id { get; init; }

    public Guid ResourceId { get; init; }

    public required string Guest { get; init; }

    public DateTimeOffset StartsAt { get; init; }

    public DateTimeOffset EndsAt { get; init; }

    public string? VatId { get; init; }

    public string? Status { get; init; }
}

[AlvoEntity("opening_hours")]
public sealed class OpeningHours
{
    public Guid ResourceId { get; init; }

    public long Weekday { get; init; }

    public long OpensHour { get; init; }

    public long ClosesHour { get; init; }
}

public static class OpeningDeskHooks
{
    public static IAlvoBuilder AddOpeningDesk(this IAlvoBuilder alvo) => alvo
        .Entity<Booking>(bookings => bookings
            .Validate<VatIdIsRegistered>(
                "vat-in-vies",
                when: w => w.New.VatId != null,
                configure: o => o.Timeout = TimeSpan.FromSeconds(3))
            .BeforeWrite<WithinOpeningHours>("within-opening-hours")
            .AfterWrite(
                "audit-booking",
                (AfterWrite<Booking> write, CancellationToken ct) => write.Transaction.CreateAsync(
                    "booking_audits",
                    new Dictionary<string, object?> { ["booking_id"] = write.Saved.Id, ["note"] = write.Old is null ? "booked" : "changed" },
                    ct))
            .OnUpdated<SendBookingConfirmation>(
                "send-confirmation",
                when: e => e.Changed(x => x.Status) && e.New.Status == "confirmed"));
}

public interface IVatRegistry
{
    Task<bool> IsRegisteredAsync(string vatId, CancellationToken cancellationToken);
}

public interface IGuestMail
{
    Task SendAsync(string guest, string text, CancellationToken cancellationToken);
}

public sealed class VatIdIsRegistered(IVatRegistry registry) : IValidateWriteHook<Booking>
{
    public async ValueTask ValidateAsync(ValidateWrite<Booking> write, CancellationToken cancellationToken)
    {
        if (write.New.VatId is { } vatId && !await registry.IsRegisteredAsync(vatId, cancellationToken))
        {
            write.Invalid("vat-not-registered", "This VAT id is not registered in VIES.", "vat_id");
        }
    }
}

public sealed class WithinOpeningHours : IBeforeWriteHook<Booking>
{
    public async ValueTask BeforeWriteAsync(BeforeWrite<Booking> write, CancellationToken cancellationToken)
    {
        var booking = write.New;
        var resource = booking.ResourceId;
        var weekday = (long)booking.StartsAt.UtcDateTime.DayOfWeek;
        var hours = await write.Transaction.AsSystem()
            .QueryAsync<OpeningHours>(x => x.ResourceId == resource && x.Weekday == weekday, limit: 1, cancellationToken);

        if (hours.Count == 0 || booking.StartsAt.UtcDateTime.Hour < hours[0].OpensHour || booking.EndsAt.UtcDateTime.Hour > hours[0].ClosesHour)
        {
            write.Conflict("outside-opening-hours", "The resource is closed at that time.", "starts_at");
        }
    }
}

public sealed class SendBookingConfirmation(IGuestMail mail) : IAlvoEventHandler<AlvoUpdated<Booking>>
{
    public Task HandleAsync(AlvoUpdated<Booking> confirmed, AlvoEventContext context, CancellationToken cancellationToken) =>
        mail.SendAsync(confirmed.New.Guest, "Your booking is confirmed.", cancellationToken);
}
```

`website/src/snippets/extending/write-hooks/OpeningDeskHooksTests.cs`:

```csharp
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;
using MMLib.Alvo;
using MMLib.Alvo.Data;
using MMLib.Alvo.Hooks;
using MMLib.Alvo.TestHost;
using Shouldly;
using Xunit;

namespace OpeningDesk.Tests;

public sealed class OpeningDeskHooksTests
{
    private static readonly Guid _court = Guid.Parse("00000000-0000-0000-0000-0000000000c1");
    private static readonly DateTimeOffset _monday = new(2026, 11, 9, 0, 0, 0, TimeSpan.Zero);

    private static string Descriptor => Path.Combine(AppContext.BaseDirectory, "opening-desk.alvo.json");

    [Fact]
    public async Task A_booking_in_opening_hours_is_stored_audited_and_confirmed_after_the_commit()
    {
        var ct = TestContext.Current.CancellationToken;
        var mail = new RecordingMail();
        await using var alvo = await StartAsync(mail, registered: true, ct);
        var staff = AlvoTestHost.Caller("staff");

        var booking = await alvo.Data.CreateAsync("bookings", Booking(10, 12, vatId: "SK2020000000"), staff, cancellationToken: ct);
        await alvo.Data.UpdateAsync("bookings", (Guid)booking["id"]!, new Dictionary<string, object?> { ["status"] = "confirmed" }, staff, cancellationToken: ct);
        await alvo.DrainEventsAsync(ct);

        (await alvo.Data.QueryAsync(new AlvoQuery { Entity = "booking_audits" }, staff, ct)).Items.Select(row => row["note"]).ShouldBe(["booked", "changed"], ignoreOrder: true);
        mail.Sent.ShouldBe(["Ada: Your booking is confirmed."]);
        alvo.Hooks.AssertRan("within-opening-hours", times: 2);
    }

    [Fact]
    public async Task A_booking_outside_opening_hours_is_refused_at_its_field_and_nothing_is_written()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var alvo = await StartAsync(new RecordingMail(), registered: true, ct);
        var staff = AlvoTestHost.Caller("staff");

        var refusal = await Should.ThrowAsync<AlvoWriteRejectedException>(
            () => alvo.Data.CreateAsync("bookings", Booking(19, 21), staff, cancellationToken: ct));

        (refusal.Kind, refusal.Code, refusal.Pointer).ShouldBe((AlvoWriteRejectionKind.Conflict, "outside-opening-hours", "/starts_at"));
        (await alvo.Data.QueryAsync(new AlvoQuery { Entity = "bookings" }, staff, ct)).Items.ShouldBeEmpty();
        (await alvo.Data.QueryAsync(new AlvoQuery { Entity = "booking_audits" }, staff, ct)).Items.ShouldBeEmpty();
    }

    [Fact]
    public async Task An_unregistered_vat_id_is_refused_before_the_write_opens_a_transaction()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var alvo = await StartAsync(new RecordingMail(), registered: false, ct);

        var refusal = await Should.ThrowAsync<AlvoWriteRejectedException>(
            () => alvo.Data.CreateAsync("bookings", Booking(10, 12, vatId: "XX0"), AlvoTestHost.Caller("staff"), cancellationToken: ct));

        (refusal.Kind, refusal.Pointer).ShouldBe((AlvoWriteRejectionKind.Invalid, "/vat_id"));
    }

    private static async Task<AlvoTestHost> StartAsync(RecordingMail mail, bool registered, CancellationToken ct)
    {
        var alvo = await AlvoTestHost.StartAsync(builder =>
        {
            builder.FromDescriptor(Descriptor).AddOpeningDesk();
            builder.Services.AddSingleton<IVatRegistry>(new FixedRegistry(registered));
            builder.Services.AddSingleton<IGuestMail>(mail);
        }, time: new FakeTimeProvider(_monday), cancellationToken: ct);
        await alvo.Data.CreateAsync(
            "opening_hours",
            new Dictionary<string, object?> { ["resource_id"] = _court, ["weekday"] = 1L, ["opens_hour"] = 8L, ["closes_hour"] = 18L },
            AlvoTestHost.Caller("manager"),
            cancellationToken: ct);

        return alvo;
    }

    private static Dictionary<string, object?> Booking(int from, int to, string? vatId = null) => new()
    {
        ["resource_id"] = _court,
        ["guest"] = "Ada",
        ["starts_at"] = _monday.AddHours(from),
        ["ends_at"] = _monday.AddHours(to),
        ["vat_id"] = vatId,
    };

    private sealed class FixedRegistry(bool registered) : IVatRegistry
    {
        public Task<bool> IsRegisteredAsync(string vatId, CancellationToken cancellationToken) => Task.FromResult(registered);
    }

    private sealed class RecordingMail : IGuestMail
    {
        private readonly Lock _gate = new();
        private readonly List<string> _sent = [];

        public IReadOnlyList<string> Sent
        {
            get
            {
                lock (_gate)
                {
                    return [.. _sent];
                }
            }
        }

        public Task SendAsync(string guest, string text, CancellationToken cancellationToken)
        {
            lock (_gate)
            {
                _sent.Add($"{guest}: {text}");
            }

            return Task.CompletedTask;
        }
    }
}
```

The host runs on a `FakeTimeProvider` that nothing advances, so no hook's 250 ms races a cold runner (Ruling 40) — the page shows the same `StartAsync` a reader copies. Run: `dotnet test --project test/MMLib.Alvo.DocsSnippets.Tests --filter-class '*OpeningDeskHooksTests'` → PASS (three facts), and `--filter-class '*SnippetCoverageTests'` → PASS (each `.cs` declares a type named after its file). A `"vat_id": null` in a create payload is accepted as "no value" — if the payload guard refuses an explicit null on an optional field, drop the key when `vatId` is null.

- [ ] **Step 2: Write the page**

`website/src/content/docs/extending/write-hooks.mdx`:

````mdx
---
title: Validate and guard writes
description: Run your C# in a write in three phases — validate before the transaction, guard and complete inside it — with the caller's rules applied again to everything a hook does.
sidebar:
  order: 7
---

import { Code } from '@astrojs/starlight/components';
import SourceExcerpt from '../../../components/SourceExcerpt.astro';
import hooks from '../../../snippets/extending/write-hooks/OpeningDeskHooks.cs?raw';
import tests from '../../../snippets/extending/write-hooks/OpeningDeskHooksTests.cs?raw';
import descriptor from '../../../snippets/extending/write-hooks/opening-desk.alvo.json?raw';

The descriptor's rules and before-hooks decide most writes. When a write needs your C# — a register only a web service
knows, a check across rows, an audit row with the new id — register a **write hook**. It runs in the same pipeline as
the descriptor's own hooks, in one of three phases:

| Phase | Runs | May | May not |
|---|---|---|---|
| **validate** | before the write opens its transaction; only for a write no transaction unit is open around | call another service; refuse | change anything |
| **before-write** / **before-delete** | inside the write's transaction, after the descriptor's before-hooks | refuse; set fields; write other rows; publish an event | call the network |
| **after-write** / **after-delete** | inside the transaction, after the row and its rollups exist | refuse — rolling the write back; write other rows; publish | write its own row; call the network |

An [event handler](/extending/event-handlers/) is the fourth place: after the commit, retried, never able to undo the write.

## The booking desk

A booking must fall inside the resource's opening hours, a VAT id must be registered, every booking leaves an audit row,
and a confirmation is mailed after the commit. Staff may book; only managers read the opening hours.

<Code code={descriptor} lang="json" title="opening-desk.alvo.json" />

<SourceExcerpt code={hooks} from="public static IAlvoBuilder AddOpeningDesk" to="e.New.Status == \"confirmed\"));" title="OpeningDeskHooks.cs" />

`Entity<Booking>` hands every hook your class; `when` is a lambda over `w.New`, `w.Old`, `w.Changed(…)` and `w.Caller`,
translated to CEL once, when the hook is registered — or a CEL string. `changed(field)` is false on a create, and `old.`
is absent there, so a condition that must hold for creates tests `new.` — `w.New.VatId != null` above.

<SourceExcerpt code={hooks} from="public sealed class WithinOpeningHours" to="write.Conflict(\"outside-opening-hours\", \"The resource is closed at that time.\", \"starts_at\");" title="WithinOpeningHours" />

The guard reads `opening_hours`, which staff may not read, through `write.Transaction.AsSystem()`: the system **in the
caller's tenant** — never another — with the caller's place in the causation chain. Every such call is logged
(`ExtensionSystemAccess`) and counted (`alvo.hook.system_access`).

## What a hook sees

| Context | `Old` | `New` / `Saved` | Also |
|---|---|---|---|
| `ValidateWrite<T>` | before the write, read without a lock; null on a create | `New`: the row to store; a create has no id yet | `Data` (a session as the caller), `Caller`, `Now`, `Operation` |
| `BeforeWrite<T>` | as above, read under the write's lock | `New`, with earlier hooks' `Set`s | `Transaction`, `Set`, `PublishAsync` |
| `AfterWrite<T>` | as above | `Saved`: id, defaults and rollups | `Transaction`, `PublishAsync` |
| `BeforeDelete<T>` / `AfterDelete<T>` | the row, always | — | `Transaction`, `PublishAsync` |

Untyped hooks take the same contexts over `AlvoRecord` — `Entity("bookings", b => b.BeforeWrite<…>(…))`,
`Entities([...], …)`, and `Entity("*", …)` for every entity (its condition tests only `@user` and `@tenant`). A hook is a
class (`IBeforeWriteHook<T>`, built per write from the write's DI scope) or a delegate whose parameters are the context,
a `CancellationToken` or services.

## Refusing a write

`Invalid(code, message, field)` answers **422** `validation`, `Conflict(…)` **409** `conflict`, `Forbidden(code, message)`
**403** `forbidden`; when one hook records several kinds, 403 wins over 409 over 422, and the answer lists every
violation of that kind. `field` becomes the violation's JSON pointer (`/starts_at`) — **except on a field the descriptor
hides from some callers**, which is never pointed at; do not name it in your message either. In `IAlvoData` the refusal
is an `AlvoWriteRejectedException`; in a batch it is that row's refusal and the batch writes nothing. Every refusal is
recorded in `alvo_hook_rejections` — ids and your code, never the payload.

## Setting fields

`write.Set("status", "confirmed")`, or `write.Set(x => x.Status, "confirmed")` on a typed hook. The row is then judged
again by the entity's `create`/`update` rule and its tenant scope — a hook cannot place a row the caller could not — and
every value by its field's facets (`maxLength`, enum values, format, precision). A field the caller's policy makes
`readOnly` is refused exactly as the caller's own payload would be (403). A field Alvo maintains (`id`, the audit columns,
`computed`, `rollup`) cannot be set. A whole number goes into a `decimal` field; a value the field cannot hold without
loss fails the write with 500 `hook-failed`, reason `invalid-set`.

:::caution[What binds a hook]
`readOnly` binds a hook as it binds the caller; the rule's `WITH CHECK` is judged again over every row a hook produces. A
field you only constrain elsewhere — in your own code, or in a field nobody marks `readOnly` — is not protected from a hook.
:::

## Writing other rows

`write.Transaction` is the write's own transaction, acting as the caller one hop deeper in the causation chain: what it
writes commits with the write or not at all, runs the other entity's rules and hooks, and emits its events. Three limits:

- **No self-write.** Writing the row whose hook is running — even from a hook of another row's write — fails the write
  (`self-write`).
- **A budget.** 50 calls per run, reads included (`AlvoHookOptions.MaxOperations`); the 51st throws
  `AlvoBudgetExceededException` — write many rows with one batch call.
- **Depth.** A hook writing what triggers itself stops at the chain limit (`Alvo:Extensions:MaxDepth`, 8) with
  **508** `chain-depth-exceeded`.
- **Cascades run no hooks.** Rows a declared `onDelete: cascade` or `set-null` removes or changes run no host hook —
  unlike a PostgreSQL row trigger. A hook whose write makes its own row disappear (deleting its parent through a cascade)
  fails the write (`row-vanished`).

Catching any of these changes nothing: the write is already failed and rolls back.

## Time limits

A before/after hook has **250 ms**, a validate hook **5 s** (`AlvoHookOptions.Timeout`, at most 30 s inside the
transaction and 60 s for validate). The token a hook receives ends at its limit, and every call through
`write.Transaction` honours it; a hook that ignores it is failed when it returns (`timeout`). Alvo waits for a hook to
return — it shares the write's connection — so a hook that ignores its token and never returns holds the write open, and
on SQLite the database's one writer lock with it, past even the unit's 30-second limit. Pass the token on. An exception escaping a
hook fails the write with 500 `hook-failed`, reason `exception`; its message stays in your log.

## Validate: before the transaction

Validate runs once per write, before the write's transaction and before an idempotent write's retry loop, so a network
call never holds a lock. It does **not** run for a write inside a [transaction unit](/extending/transactions/) — a
[command](/extending/commands/) makes its external check before `ctx.TransactAsync` — nor for a replayed
`Idempotency-Key`. A batch validates every row first; a refused row refuses the batch with nothing opened. What a
validate hook saw — its `Old` was read without a lock — may change before the write runs: never rest integrity on it;
an invariant belongs in a before-write hook. A validate hook never sees a payload the caller may not send, and an update
of a row the caller cannot reach answers 404 before any validate hook runs.

An administrator can switch a validate hook off while its service is down:
`PUT {management}/projects/{project}/extensions/{id}/enabled` with `{"enabled": false, "reason": "…"}` — audited, and
honoured by the next write on every instance. Other hooks cannot be switched off: they guard the data.

## What refuses the start

A before/after/delete hook that takes `HttpClient`, `IHttpClientFactory`, a socket, a `DbConnection`, a mail sender,
`IAlvoData`, `IAlvoSessions`, `IAlvoTransactions`, `IAlvoEvents`, `IAlvoCommands`, `IAlvoManagement`, `IAlvoOperations`, an
identity store, web sockets, `SmtpClient`, `WebClient`/`WebRequest`, or a delivery/outbox store — in its
constructor or as a delegate parameter — refuses the start, naming the parameter. So do an unknown entity, a condition
that does not compile or reads `new.` on a delete, a timeout or budget out of range, and an in-memory provider (write
hooks need SQLite or PostgreSQL). A registration the descriptor stops fitting is refused at apply; one that never fit
fails every write it guards (`unbound`) rather than letting it through.

## Maintenance without host hooks

A system job may write with host hooks skipped:
`TransactAsync(AlvoContext.System(tenant), work, new AlvoTransactionOptions { SuppressHostHooks = true, Reason = "…" })`.
Any other caller is refused; the descriptor's hooks and events still run; the unit is audited once.

## Testing

<SourceExcerpt code={tests} from="public async Task A_booking_outside_opening_hours" to="Items.ShouldBeEmpty();" title="OpeningDeskHooksTests.cs" />

`alvo.Hooks.AssertRan(id, times)` counts write-hook runs made through `alvo.Data` — a rejection counts as a run.

:::note[Not yet]
An analyzer that flags network use inside a hook arrives later; until then the start-up check covers direct
dependencies only.
:::
````

- [ ] **Step 3: Update the neighbouring pages and the sidebar**

- `astro.config.mjs`: `page('Validate and guard writes', 'extending/write-hooks'),` after `'Operate deliveries'`; `transactions` becomes order 8, `commands` 9, `http-conventions` 10, `testing` 11 (front matter `sidebar.order` in each page moves by one).
- `where-logic-goes.mdx`: the three rows `IValidateWriteHook`, `IBeforeWriteHook`, `IAfterWriteHook` change *Today* from `planned` to `available — [Validate and guard writes](/extending/write-hooks/)`.
- `index.mdx`: the page list gains *Validate and guard writes*.
- `transactions.mdx`: the `SuppressHostHooks` sentence (if it says "accepted, takes effect with write hooks") now says what it does (Ruling 30); a new paragraph: "A write inside a unit runs its entity's before/after-write hooks in that unit, one hop deeper; it never runs validate."
- `testing.mdx`: `Hooks.AssertRan` counts write hooks run through `alvo.Data`.
- `extension-catalog.mdx`: the kinds `validate`, `beforeWrite`, `afterWrite`, `beforeDelete`, `afterDelete`, the stage `inTransaction`, `topLevelOnly` and `enabled`; the Lifecycle tab's two new lanes and their numbers; the simulator's and the preview's code list.
- `commands.mdx`: one sentence — a command's writes inside `ctx.TransactAsync` run before/after-write hooks, never validate.

Run: `cd website && npm run build` → passes (link validation included).

- [ ] **Step 4: Architecture, C13, CHANGELOG, PLAN**

`docs/architecture/write-hooks.md` — sections, each a few paragraphs: *The stages and where they live* (the §4.1 map with the file that runs each stage: `EfAlvoData.Hooks.cs`, `WriteExtensionRunner`); *A unit of the write's own* (Ruling 1, the `OwnUnitAsync`/`UnitRun` shape, the 30 s default); *What a hook's transaction is* (lazy hop, budget, token, self-write, `AsSystem`); *What poisons* (a table: budget, self-write, timeout (via `Blame`), escaping exception, invalid-set, unbound, row-vanished, depth — each with its reason string; and the stated limit of Ruling 55: a hook that ignores its token and never returns keeps its call running, and the unit's deadline rolls back only when no call is running, so it holds SQLite's writer lock); *Cascades* (Ruling 45: declared `onDelete` cascades and set-null run no host hooks — a deviation from PostgreSQL row triggers); *Rejections* (precedence, pointers, Ruling X, batches, the log and its flush after the unit); *Validate* (stage 0/2, pre-read, batch hoisting, switch-off); *SuppressHostHooks*; *The boot ban*; *Telemetry and the test host's recorder*; *Engine parity* (no new SQL dialect; the facts that run on both); *Deviations* (a pointer to the spec's *HX-09 settled details*). `extensibility.md`: the rule-4 verb list names the hook verbs as `Entity`-builder members. `data-path.md`'s *A transaction unit spans several writes (HX-07)* gains two sentences: a top-level write with a host in-transaction hook runs in a unit of its own; the hook's view is one hop deeper.

`docs/architecture/events.md` — the C13 correction (§2.2, HX-09's scope). In the *Gone — PR5b-1 lifted all three* bullet (search `AuthorizedCandidate\` moved`), replace "with `AuthorizedCandidate` moved inside the transaction" by "with the before-hooks run inside the transaction — `AuthorizedCandidate` itself still runs before `BEGIN` on creates (updates authorize inside the transaction, deletes have no pre-transaction check; with a host in-transaction hook registered for the write, the create's `AuthorizedCandidate` and payload guard run inside the write's own unit, after `BEGIN` and the write turn; corrected in HX-09, spec C13)". Grep the file for any other sentence claiming `AuthorizedCandidate` runs inside a transaction (`grep -n 'AuthorizedCandidate' docs/architecture/*.md`) and correct each the same way (sweep for the claim, not the file).

`CHANGELOG.md`, under *Unreleased → Added*, first:

```markdown
- **Write hooks in three phases** (HX-09, #386). `Entity<T>(…)`/`Entity("name", …)`/`Entities`/`Entity("*", …)` gain
  `Validate`, `BeforeWrite`, `AfterWrite`, `BeforeDelete` and `AfterDelete` — class (`IValidateWriteHook<T>`, …) or
  delegate hooks, with a lambda or CEL `when`. Validate runs before a top-level write's transaction (network allowed,
  5 s, skipped on idempotent replays and inside transaction units; an administrator can switch it off, audited);
  before/after hooks run inside the write's transaction (250 ms, 50 calls) and may refuse (422/409/403 with pointers —
  never at a hidden field), `Set` fields (judged again by the rules and the field's facets), write other rows through
  `write.Transaction` (as the caller, one hop deeper; `AsSystem()` in the caller's tenant only, logged) and publish
  events that commit with the write. Timeouts, budget overruns, self-writes and hook exceptions fail the write with
  500 `hook-failed` and a `reason`, even when the hook catches them; a hook taking a network or data-port dependency
  refuses the start. `SuppressHostHooks` now skips host hooks for a system unit, audited. Rejections are kept in the new
  framework table `<prefix>_hook_rejections` (the entity name is now reserved). The dashboard's Lifecycle tab draws
  *Before the transaction* and *In the transaction* lanes with p95 and 24 h rejections; the editors, the policy
  simulator and the apply preview name the code that also runs. Docs: *Validate and guard writes*.
  **Breaking for typed .NET clients:** `AlvoExtensionKind` and `AlvoExtensionStage` gained members (append-only).
  **Behaviour change:** a top-level write on an entity with a host in-transaction hook runs in a transaction unit of
  its own — a 30 s limit, one DI scope, and an `IAlvoData` call from the hook is refused.
```

The two new abstract `IDeliveryStore` members are folded into HX-06's existing *[Unreleased]* entry that already names the port's breaking growth (search `IDeliveryStore` in `CHANGELOG.md`): append ", and `SetDisabledAsync`/`DisabledAsync` (HX-09)" to its list rather than adding a second "breaks implementers" line (Ruling 58) — nothing is released, so both breaks ship in one version.

`docs/PLAN.md` — the HX sentence gains "HX-09 (write hooks in three phases) the ninth".

- [ ] **Step 5: Amend the spec**

In `docs/superpowers/specs/2026-10-14-host-extensibility-design.md`:

- after §4.4.6, a new block **HX-09 settled details.** "Rulings and deviations taken while planning and after the plan review (1–59, verbatim from `docs/superpowers/plans/2026-11-04-hx-09-write-hooks.md`), so a later reader can tell a decision from an oversight. Where a ruling and §4.1 or §4.4 disagree, the ruling is what shipped." followed by the 59 rulings of this plan, verbatim, numbered; and "Rulings taken during execution continue the numbering." as its last line;
- §4.5's "top-level units use the same default — **not in HX-07:** …" gains "— **HX-09:** applied to a top-level write a host in-transaction hook runs on (*HX-09 settled details* (1)); a write with none still opens no unit";
- §4.5 *HX-07 settled details* (14)'s last sentence gains "— **decided in HX-09:** hooks may call `AsSystem()`, bounded and audited (*HX-09 settled details* (2))";
- §4.13 *HX-04 settled details* (1)'s last sentence gains "**HX-09 appended** `AlvoExtensionKind.Validate`…`AfterDelete`, `AlvoExtensionStage.InTransaction`, and `TopLevelOnly`, `Enabled` (*HX-09 settled details* (6))";
- §5 rows `09`: the notes column gains "`AlvoWriteRejectionKind`, delegate-hook overloads, two `IDeliveryStore` members and three contract rows added (*HX-09 settled details* (4), (5), (19), (21))";
- §2.2 C13's *This design* column gains "— corrected in `events.md` by HX-09".

- [ ] **Step 6: Run ring1 and commit**

```bash
dotnet format whitespace MMLib.Alvo.slnx --include website/src/snippets/extending/write-hooks/OpeningDeskHooks.cs website/src/snippets/extending/write-hooks/OpeningDeskHooksTests.cs
scripts/test-ring1
git add website docs/architecture CHANGELOG.md docs/PLAN.md docs/superpowers/specs/2026-10-14-host-extensibility-design.md
git commit -m "docs: validate and guard writes, the booking recipe, and HX-09's settled details

Claude-Session: https://claude.ai/code/session_01NJAtafM29iddLwfhb5gnmB"
```

(The pre-commit hook's brief-freshness check does not fire: the spec under `docs/superpowers/specs/` is not the analysis or the product spec.)

---

## Task 14: Base, CI parity, rings, admin e2e, mutation configuration, reviews, security core, plan-guard, PR report, PR

- [ ] **Step 1: Take `main`**

`git fetch origin`; if `origin/main` moved past `04d33dda`, `git merge origin/main` (merge-commit style), resolve only what the merge touched, and re-run `scripts/test-ring0`. Every diff below is against `origin/main`; the PR targets `main`.

- [ ] **Step 2: CI-parity Release build, rings, admin e2e**

```bash
docker run --rm -u "$(id -u):$(id -g)" -e HOME=/tmp -e DOTNET_CLI_HOME=/tmp -v "$PWD":/src -w /src mcr.microsoft.com/dotnet/sdk:10.0 sh -c 'dotnet build MMLib.Alvo.slnx -c Release -p:HUSKY=0 2>&1 | grep -E "error [A-Z]+[0-9]+" | sort -u'
```

The container runs as you, so its `bin`/`obj` output is yours — **no `git clean`** (it would delete ignored local state: `.claude/settings.local.json`, `website/node_modules`, `.superpowers/`, the Husky install). Expected: no output. Fix any `error CAxxxx`/`IDExxxx` in a `fix(…): …` commit and re-run. Watch: CA1031 on `EfCoreHookRejectionLog.InsertAsync` (suppressed with its justification), CA1062 on the builders' public parameters, CA1716 (`Validate`? no; a parameter named `delete` in the delete contexts' consumers is fine — rename to `write` if raised), CA1000 (static members on generic types: `HookContexts<T>` is internal — suppression not needed), CA1812 on `WriteExtensionRunner` (instantiated by DI — the repository's suppression convention for DI-only types), CA2007 in the snippets, CA1822 on `WriteCondition<T>`'s members (the `EventCondition<T>` precedent shows how it is handled), CA1032/CA1064 on `HookFailedException` (internal, the `CommandClaimLostException` precedent). Then `scripts/test-ring0`, `scripts/test-ring1`, `scripts/test-ring2` (PostgreSQL legs: `PostgreSqlWriteHookTests`, the `DataApiEngineTests` integration subclass, the delivery-store contract), `scripts/test-admin-e2e` — the **one** full run of the dashboard suite in this PR — and `cd website && npm run build`. If a ring2 paging fact fails on an Npgsql connect timeout before any Alvo code runs, read the trace before calling it a regression (known flake).

- [ ] **Step 3: Mutation — configuration only, no local run (Ruling 37)**

Mutation runs post-merge on `main` in CI; **do not run Stryker locally and do not re-probe any shard** (a local probe costs ~4 h; budgets are being re-sharded separately). Only make sure the new security-relevant code sits in a CI leg, by configuration:

1. *Core shard:* `stryker-config.json` mutates `**/*.cs` minus its exclusions, so `Hooks/**` is already in (killers: `WriteExtensionRunnerTests`, `HookTransactionTests`, `HookSetsTests`, `HookRejectionsTests`, `HookContextTests`, `WriteHookBindingTests`, `WriteHookRegistrationTests`, `WriteConditionTranslatorTests` in `MMLib.Alvo.Tests`). Confirm by reading the config that no exclusion catches `Hooks/`; change nothing.
2. *data-ef-core:* **append**, at the end of `stryker-config.data-ef-core.json`'s `mutate` array and in this order, `"Internal/EfAlvoData.Hooks.cs"` then `"Internal/UnitRun.cs"` (killers `HostHookStageTests`, `EfAlvoTransactionsTests` in `MMLib.Alvo.Data.EntityFrameworkCore.Tests`, already that leg's test project); **append** at the end of `stryker-config.data-ef-rest.json`'s `mutate`, in the same order, `"!Internal/EfAlvoData.Hooks.cs"` then `"!Internal/UnitRun.cs"`. `scripts/test-assert-mutation-run` compares the arrays in order (`$whole + ($core | map("!"+.))`), so the order matters (Ruling 56). `HookRejectionTable.cs`/`EfCoreHookRejectionLog.cs` stay in data-ef-rest by its glob; note in the PR that `DeliveryOperationsTable`'s new switch statements land in data-ef-rest, whose killers (the contract rows) live in `Sqlite.Tests`, so survivors there are expected, and that data-ef-core is the leg nearest its time ceiling.
3. Run `scripts/test-assert-mutation-run` (it reads the configs; it runs no mutation) and fix any mismatch it names. Leave the `mutants:`/`suite:` values in `.github/workflows/mutation.yml` untouched. Commit as `ci(mutation): mutate the write-hook stages in data-ef-core`.

- [ ] **Step 4: Reviews (labelled substitutes) and the security-core checklist**

Freeze the tree (no edits while a reviewer reads it). `/code-review` and `/security-review` are user-only: dispatch two reviewer subagents over `git diff origin/main...HEAD`, labelled **"substitute for /code-review high"** and **"substitute for /security-review"**, and check `git branch --show-current` after each returns (a reviewer can switch branches).

Point the code reviewer at: every write path of `EfAlvoData` runs stage 4 after the declarative hooks and stage 5 over the union patch, and stage 7 after rollups — including `CreatedByReplaceAsync`, `RecordedCreateAsync`/`RecordedUpdateAsync`, and the three batch row methods; a keyed write's own unit per replay attempt and the idempotency table ensured before it (no DDL inside a unit); `UnitRun` preserving `TransactAsync`'s exact failure mapping; `FlushRejectionsAsync` after the unit left the flow and released its turn; `WriteExtensionRunner.RunAsync` always awaiting the hook (never abandoning it), and `Escaped`'s order; `HeldRows`' restore on every path; `HookTransaction.CallAsync` disposing its linked source; the batch after-write refusal returning before the commit; `ExtensionStats`' new bucket cleared with its slot.

Run the `alvo-security-core-review` checklist and give the security reviewer: Rulings 2 (`AsSystem`), 3, 7 (fail closed), 9 (lazy hop — every write and publish checks depth inside its call), 10–12, 16 (what `Set` may write; stage 5 re-judges `tenant_id`/`owner_id`), 17 (hidden pointers), 23 (boot ban, generic arguments), 30 (`SuppressHostHooks` system-only, audited in the unit), 34 (no in-memory silent skip), 35 (self-write across nesting). Questions to answer explicitly: can a caller-controlled value reach a hook's `when` evaluation in a way that skips a guard (a `when` throwing is `function-failed`, fail closed)? Can a hook's nested write run with a context other than the caller's or the caller's tenant's system? Can a rejection message or a log line carry a hidden field's name, a value or a host exception's message? Can validate run with a transaction open (stage 2 placement in all nine public entry points)? Can a `TransactAsync` delegate that catches a hook failure commit (poison on every failure path, `Blame` only replacing a cancellation)? Fix every finding before the PR; record any accepted risk as a ruling in the spec's *HX-09 settled details*.

- [ ] **Step 5: `alvo-plan-guard`**

Dispatch `alvo-plan-guard` last (read-only); act on drift it reports. The PLAN marker stays where it is.

- [ ] **Step 6: PR report**

Use the `alvo-pr-report` skill (it dispatches `alvo-pr-reporter` and publishes the 8-section page). Evidence: acceptance (1)–(13) → tests (the table below), the four public-API diffs against the public-surface list (Abstractions, core, Testing, Admin), the both-engine runs of `WriteHookFacts`, `DataApiEngineTests.WriteHooks` and the delivery-store contract rows, the admin e2e scenarios, the Release build, the security-core checklist and both substitute reviews, the mutation configuration (no local run, Ruling 37), and the rulings — calling out Ruling 1 (a unit of the write's own only with hooks), 2 (`AsSystem` allowed, bounded, audited), 3 (an internal failure type), 5 (two `IDeliveryStore` members), 7 (unbound fails closed), 9 (the lazy hop), 11 (`Blame`), 13–14 (validate placement), 16 (what `Set` may write), 30 (suppression audited in the unit).

- [ ] **Step 7: Push and open the PR**

```bash
git branch --show-current   # hx/09-write-hooks
git push -u origin hx/09-write-hooks
```

`gh pr create --base main`. The body is the skill's five-line pointer, carries `Closes #386` once, and ends with `https://claude.ai/code/session_01NJAtafM29iddLwfhb5gnmB`. Then `gh pr edit --add-label needs-deep-review`. After the merge, read #386 back (`gh issue view 386`) to confirm it — and only it — closed; comment on #371 and #22 that HX-09 shipped the C# write hooks (validate, before/after write and delete), and on #154 that `alvo_hook_rejections` has no retention yet.

---

## Acceptance criteria → tasks

| HX-09 criterion | Task(s) | Test |
|---|---|---|
| (1) per stage, both engines: reject → no row, no outbox row, correct status and pointer | 3, 4, 8, 9, 11 | `WriteHookFacts.A_rejection_at_each_write_stage_leaves_no_row_no_outbox_row_and_its_kind_and_pointer` (validate, beforeWrite, afterWrite), `A_rejection_at_each_delete_stage_keeps_the_row_and_emits_nothing` (beforeDelete, afterDelete) — SQLite + PostgreSQL; `DataApiEngineTests.A_rejection_at_each_write_stage_answers_its_status_and_pointer_and_writes_nothing_on_every_engine`, `A_rejection_before_a_delete_answers_409_and_keeps_the_row_on_every_engine` (both engines); `HostHookStageTests.A_rejected_batch_row_is_that_row_s_refusal_and_nothing_is_written`; snippet `A_booking_outside_opening_hours_is_refused_at_its_field_and_nothing_is_written` |
| (2) a `Set` of a forbidden `owner_id` → 403 (adversarial); a `Set` of an undeclared enum value → 500 `invalid-set` identically on both engines | 3, 8, 9, 11 | `DataApiEngineTests.A_set_of_another_owner_is_forbidden_on_every_engine`, `A_set_of_an_undeclared_enum_value_is_invalid_set_on_every_engine` (both engines); `WriteHookFacts.A_set_of_an_undeclared_enum_value_is_invalid_set_and_writes_nothing` (both engines); `HostHookStageTests.A_patch_is_stored_and_judged_by_the_create_rule_again`; `HookSetsTests` |
| (3) a rejection on a hidden field carries no pointer | 5, 9, 11 | `HookRejectionsTests.A_hidden_field_gets_the_body_pointer`; `WriteHookFacts.A_rejection_on_a_hidden_field_carries_no_pointer` (both engines); `DataApiEngineTests.A_rejection_on_a_hidden_field_answers_with_no_pointer_on_every_engine` (both engines) |
| (4) after-write sees id/defaults/rollups and can roll back; an after-write child's event carries the committed parent rollup | 3, 8, 11 | `WriteHookFacts.An_after_write_sees_the_id_the_defaults_and_its_child_s_rollup_reaches_the_event_and_the_row`, `An_after_write_that_rejects_rolls_its_child_back_too` (both engines); `HostHookStageTests.An_after_write_that_wrote_makes_the_driver_read_the_row_again` |
| (5) self-write refused | 8, 11 | `WriteExtensionRunnerTests.A_nested_write_to_the_row_in_its_own_hook_stage_is_a_self_write_even_when_caught`; `WriteHookFacts.A_hook_that_catches_a_self_write_refusal_still_rolls_back_with_reason_self_write` (both engines) |
| (6) a 250 ms overrun and a 51st operation → 500 with reason and rollback, even when caught | 8, 9, 11 | `WriteExtensionRunnerTests.A_hook_that_overruns_its_timeout_fails_with_timeout_even_when_it_catches_the_cancellation`, `A_caught_budget_refusal_still_fails_the_hook_and_poisons_the_unit`; `WriteHookFacts.A_hook_that_catches_its_timeout_still_rolls_back_with_reason_timeout`, `A_hook_that_catches_its_budget_refusal_still_rolls_back_with_reason_budget` (both engines); `DataApiEngineTests.A_hook_exception_answers_500_hook_failed_and_never_its_message_on_every_engine` |
| (7) a hook injecting `HttpClient` refuses boot | 7 | `WriteHookBindingTests.An_in_transaction_hook_taking_a_network_dependency_refuses_the_start_and_a_validate_hook_may`; `ExtensionBootRefusalTests.A_write_hook_injecting_an_http_client_refuses_boot_naming_the_parameter` |
| (8) batch validate with no transaction open; never on an idempotent replay; not for a nested write inside `TransactAsync` | 4, 11 | `WriteHookFacts.A_batch_validate_hook_runs_once_per_row_with_no_transaction_open`, `A_validate_hook_never_runs_on_an_idempotent_replay`, `A_write_inside_a_unit_never_runs_validate` (both engines); `HostHookStageTests.Validate_runs_before_any_transaction_with_the_authorized_candidate_and_no_id`, `A_replayed_key_skips_validate`, `A_batch_validates_every_row_before_its_transaction_and_a_refused_row_refuses_the_batch` |
| (9) a before-write `AsSystem()` recursion → 508 | 3, 8, 11 | `WriteHookFacts.A_before_write_hook_writing_its_own_entity_as_the_system_recursively_is_refused_508` (both engines); `EfAlvoTransactionsTests.A_write_one_hop_deeper_past_the_limit_is_refused_when_it_writes_and_a_caught_refusal_still_rolls_back`; `WriteExtensionRunnerTests.A_depth_refusal_escaping_a_hook_passes_unchanged_and_a_nested_rejection_is_the_hook_s_failure` |
| (10) `SuppressHostHooks` non-system → `ArgumentException`; system → hooks skipped, one audit row | 3, 11 | `WriteHookFacts.Suppressing_host_hooks_needs_the_system_and_skips_them_with_one_audit_row` (both engines) |
| (11) disabling a validate hook writes an audit row and the next write skips it | 2, 8, 10, 11 | `WriteHookFacts.Switching_a_validate_hook_off_is_audited_and_the_next_write_skips_it` (both engines); `AlvoOperationsWritesTests.Switching_a_validate_hook_off_writes_one_log_row_and_one_audit_line_and_answers_it_disabled`, `Switching_it_on_again_is_audited_as_enable`; `WriteExtensionRunnerTests.A_switched_off_validate_hook_is_skipped`; `DeliveryStoreContractTests.Switching_an_extension_off_lists_it_and_logs_the_change_once` (+ two rows, both engines) |
| (12) a rejection log row exists after rollback | 2, 3, 8, 11 | `WriteHookFacts.A_rejection_is_logged_after_the_rollback_with_ids_and_its_code` (both engines); `HookRejectionLogTests`; `WriteExtensionRunnerTests.A_rejection_stops_the_stage_discards_its_sets_and_is_logged_with_ids_and_the_code_only` |
| (13) two hooks of one unit share the unit's scope; a request-scoped service differs | 3, 11 | `WriteHookFacts.Two_hooks_of_one_unit_share_its_scope_and_a_request_scope_is_another` (both engines) |
| Scope: `AsSystem` tenant-bound and audited (Ruling 2) | 8, 11 | `WriteExtensionRunnerTests.AsSystem_is_the_system_in_the_caller_s_tenant_with_the_caller_s_provenance_and_it_is_logged`; `WriteHookFacts.A_guard_reads_reference_data_the_caller_cannot_as_the_system_and_no_other_tenant_s` (both engines) |
| Scope: `write.Publish`, `Hooks.AssertRan` for write hooks | 5, 11 | `HookContextTests.Publish_goes_through_the_hook_s_transaction`; `WriteHookFacts.A_rejected_run_counts_as_ran_for_the_recorder`; snippet `A_booking_in_opening_hours_is_stored_audited_and_confirmed_after_the_commit` |
| Scope: boot validation, apply guard, catalog, unbound fails closed | 6, 7, 8 | `WriteHookBindingTests` (incl. `Removing_a_field_a_bound_write_hook_reads_is_refused_at_apply`, `The_catalog_describes_each_stage_…`), `WriteHookRegistrationTests`, `WriteConditionTranslatorTests`, `WriteExtensionRunnerTests.A_registered_hook_that_does_not_bind_the_current_catalog_fails_the_write_closed` |
| Scope: `events.md:1164` corrected (C13) | 13 | the edit in Task 13 Step 4 (reviewed by plan-guard) |
| Dashboard: write lanes, editor note, simulator/dry-run list, p95 and 24 h rejections | 10, 12 | `LifecycleLaneWriteHookTests`, `ExtensionStatsTests.A_write_hook_s_runs_rejections_and_failures_are_counted_from_its_meter`; e2e `WriteHookScenarios` (three), `WriteLaneAbsenceScenarios` |
| Docs: *Validate and guard writes*, *Where logic goes*, the booking recipe | 13 | `OpeningDeskHooksTests` (three) in `MMLib.Alvo.DocsSnippets.Tests`; website build |
| Review hardening (Rulings 39–59) | 3, 4, 8, 9, 10, 11 | `WriteExtensionRunnerTests.A_later_hook_s_condition_sees_an_earlier_hook_s_set_both_ways`; `HostHookStageTests.A_validate_hook_never_sees_an_update_the_payload_guard_refuses`, `A_batch_reports_every_bad_row_whichever_check_refused_it`; `WriteHookFacts.A_batch_row_s_hook_writing_a_sibling_row_is_a_self_write`, `An_after_write_whose_cascade_removes_its_own_row_fails_with_row_vanished_and_emits_nothing`, `Validate_never_runs_on_an_update_the_payload_guard_refuses_nor_on_a_row_the_caller_cannot_reach` (both engines); `DataApiEngineTests.A_set_of_a_read_only_field_is_refused_as_the_caller_s_own_payload_would_be_on_every_engine` (both engines); `HookSetsTests` (widening, tenant on a global entity); `HookRejectionsTests.A_name_that_is_not_a_declared_visible_field_gets_the_body_pointer`; `AlvoOperationsWritesTests.Switching_a_before_write_hook_off_is_refused_409_and_changes_nothing` |
| Security core (§6.2) | 14 | `alvo-security-core-review` checklist, `/security-review` substitute, `needs-deep-review` |
