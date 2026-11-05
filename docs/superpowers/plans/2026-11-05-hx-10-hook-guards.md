# HX-10 — Analyzer, host-function budget, `expression-failed`, calibration — Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** In-transaction host code gets the three guards the spec promised it: a Roslyn analyzer (`ALVO1001`, shipped inside the `MMLib.Alvo` nupkg) that flags the network, a database connection, the file system, a second data port, and any `await` that is not the write's transaction in a before/after-write or before/after-delete hook and in an `AddCelFunction` delegate; a per-evaluation **time budget** for host CEL functions, with a trailing `CancellationToken` a function may take; and a caller-fixable answer — **422 `expression-failed`** — when a built-in CEL function or hook arithmetic refuses the caller's data (E1, #315), while host failures stay 500. The 250 ms in-transaction default (Q5) is calibrated by a `scripts/test-load` hook scenario published in `docs/performance.md`.

**Architecture:** The budget is an internal, `AsyncLocal` **evaluation pass** (`CelFunctionBudget`) opened by the three places that evaluate host-callable CEL — `BeforeHookRunner.Run` (declarative before-hooks, in the write's transaction), `WriteExtensionRunner`'s write-hook `when`, and `PredicateEvaluator` for `Condition`-profile expressions (post-commit after-hook and handler conditions) — from one DI singleton, `CelFunctionBudgetPolicy` (`Alvo:Extensions:CelFunctionBudget` on the host's `TimeProvider`). `HostCelFunction`'s body runs every call through the ambient pass: it hands a token-aware delegate the pass's token, and fails the call as the host function's `CelFunctionException` when the deadline cancels it or when it returns late; `IBeforeHookRunner.Run` stays synchronous and token-free (#309). E1 is one classification on the internal `CelFunctionException` (`OnCallerData`: a built-in's or the interpreter's refusal of a value the caller sent — not of a host function's output, the stored row, or a value a hook wrote, judged from the compiled tree on the failure path) read by `ProblemResultFactory.RefusalFor` on the generated Data API routes — so every host answers it, no opt-in — and by `AlvoExceptionHandler` for those routes only; commands keep 500. The analyzer is a new **project, not a package** (E6): `src/MMLib.Alvo.Analyzers` (netstandard2.0, `IsPackable=false`), packed by `MMLib.Alvo`'s pack into `analyzers/dotnet/cs`, proven by a script that builds a packed consumer on the minimum SDK in a container. Calibration runs a tiny host in `tools/MMLib.Alvo.Load.Host` — the standalone composition plus three no-op/one-read hooks gated by the work order's reference prefix — as a second arm of `scripts/test-load --tier calibration`.

**Tech Stack:** .NET 10 (C# 14), Roslyn (`Microsoft.CodeAnalysis.CSharp` 4.14.0 — the floor any .NET 10 SDK compiler loads; `Microsoft.CodeAnalysis.Analyzers` 3.11.0), `System.Threading.CancellationTokenSource(TimeSpan, TimeProvider)`, `Microsoft.Extensions.TimeProvider.Testing`, xUnit v3 on Microsoft.Testing.Platform, Shouldly, Verify + PublicApiGenerator, Testcontainers (PostgreSQL leg), k6 2.2.0 + Docker (load), Astro/Starlight (docs).

**Spec:** `docs/superpowers/specs/2026-10-14-host-extensibility-design.md` — §7 *HX-10* (scope, docs, acceptance (1)–(5)); §3.3 E1, E6; §3.1 Q5; §4.4.4 (budgets); §4.4.5 (the boot ban and `ALVO1001`); §2.3 X1; §5 row `10`; §6.2. HX-09's amendments it builds on: `docs/superpowers/plans/2026-11-04-hx-09-write-hooks.md` Rulings 23, 54 and 62 (the boot ban checks direct dependencies only; indirect use is `ALVO1001`'s), 55 (L7) and 63 as amended (a built-in failing in a hook's `when` answers `function-failed` until E1). Issues #387 (this PR), #309 (budget + analyzer for host functions), #315 (E1). Earlier designs it amends: `docs/superpowers/specs/2026-10-05-f5-cel-functions-design.md` §5.8 / deviation X14 (no budget, no analyzer) and `2026-10-06-f5-hook-functions-end-to-end-design.md` Q7 (built-in failures were 500).

**Prior art read and cited in the Rulings** (CLAUDE.md, *Study the problem before designing it*):

- **Roslyn analyzers** — `DiagnosticAnalyzer` with `RegisterCompilationStartAction` (resolve the framework's symbols once, bail out when the compilation does not reference them), `RegisterOperationBlockStartAction` (scope by the owning symbol's containing type), `RegisterOperationAction(OperationKind.Invocation)` (inspect a registration call's delegate argument: `IConversionOperation` → `IDelegateCreationOperation` → `IAnonymousFunctionOperation` / `IMethodReferenceOperation`), `IOperation.SemanticModel` (no `Compilation.GetSemanticModel`, RS1030), `EnforceExtendedAnalyzerRules` (RS1036), release tracking (`AnalyzerReleases.Shipped.md`/`Unshipped.md`, RS2008), `netstandard2.0` (RS1041). Packaging: `analyzers/dotnet/cs/` inside the library's own nupkg — `System.Text.Json`, `Microsoft.Extensions.Logging.Abstractions` and `xunit.v3` ship their generators/analyzers that way; a compiler loads an analyzer referencing a Roslyn version no newer than its own, so the reference is pinned to the oldest compiler a supported SDK carries.
- **Microsoft's `BannedApiAnalyzers` (RS0030)** and **`Microsoft.VisualStudio.Threading.Analyzers` (VSTHRD002/VSTHRD103)** — a type/member ban list matched by symbol, and "do not block on a task in a synchronous context". Adopted: symbol-based matching incl. base types, interfaces and generic arguments; the CEL-function rule that a `Task`-returning call is reported (the function is synchronous, so it can only block on it). Deviated: the list is fixed (Alvo's boot ban), not consumer-configurable, so the analyzer and the boot refusal cannot disagree.
- **Salesforce governor limits** (CPU time per transaction, already X3's source), **Supabase Auth hooks / Firebase blocking functions** (a hard deadline that fails the request), **PostgreSQL `statement_timeout`** (a statement past its limit is cancelled and its transaction aborted) — adopted as: one budget per evaluation pass, enforced through a token where the code cooperates and **measured on return** where it does not (.NET has no thread abort — X15's reasoning), and an overrun fails the write closed.
- **CEL specification** — evaluation errors (`int('x')`, overflow, division by zero, `no_such_overload`) are *errors* of the expression over its input, distinct from the host environment failing; **RFC 9110 §15.5.21** (422: the server understands the content but cannot process the contained instructions — the client can change the content) vs §15.6.1 (500: the server's own failure); **RFC 9457** (one `type` per kind of refusal, `detail` never parsed). Adopted: E1's split along exactly that line.
- **k6** (open-model `constant-arrival-rate`, `min` as service time) and this repository's load design (`docs/superpowers/specs/2026-09-02-f4-pr-e-load-test-foundations-design.md`): a shape is compared only with another shape measured on the same rig and dataset minutes apart.

## Global Constraints

- Worktree `/Users/martiniak/Developer/GitHub/Burgyn/MMLib.Alvo-wt-hx10`, branch `hx/10-hook-guards`, based on `hx/09-write-hooks` (PR #431), which merges into `main` first — Task 10 Step 1 rebases onto `main` and the PR targets `main`. Issue **#387**, epic #370; closes #309 and #315. One writer per worktree; never `git add -A` (stage each task's files by path); `git branch --show-current` prints `hx/10-hook-guards` before every commit and before the push. **Never run `git clean`**, and never delete anything under `.superpowers/`. A bare `ls` hides git-ignored files in this shell — use `/bin/ls` or `find`.
- **Scope is §7 HX-10 only.** No new problem type but `expression-failed`; no dashboard slice (§7: "Dashboard: none"); no schema change (`schema/project.schema.json` does not move); no MCP; no assistant change (the descriptor skills under `plugins/alvo/skills` say nothing about `function-failed` — checked); no generator (HX-17); no `IAlvoScheduler` (HX-13). The standalone host registers no hook and no host function.
- **Public surface added is exactly** (each beyond §5 row `10` is a Ruling — 1, 4, 15):
  - Core `MMLib.Alvo.Api`: `AlvoProblemTypes.ExpressionFailed` (`"expression-failed"`, appended to `All`).
  - Core `MMLib.Alvo.Extensions`: `AlvoExtensionOptions.CelFunctionBudget` (`TimeSpan`, get/set, default 250 ms).
  - **No new `AddCelFunction` overload** (Ruling 1): the existing `AddCelFunction(IAlvoBuilder, string, Delegate, string?)` accepts a trailing `CancellationToken`; only its XML docs change.
  - New baseline `PublicApi.MMLib.Alvo.Analyzers.verified.txt`: `MMLib.Alvo.Analyzers.InTransactionIoAnalyzer` (`public sealed`, `DiagnosticAnalyzer`: `SupportedDiagnostics`, `Initialize`) — nothing else.
  Everything else is `internal`.
- **Wire and naming constants (verbatim):** problem type `expression-failed`, status **422**, `detail` = `The CEL function '<name>' refused this request's data: <reason>. Nothing was written.` (a whole condition with no Bool: `The hook's condition evaluated to a present value that is not a Bool, so nothing was written. Correct the values it reads.`), no `violations` member; `function-failed` stays 500 for a host function (thrown, argument mismatch, budget) and for an interpreter defect (`<hook>`); budget reason `it ran past the <n> ms that host functions have in one evaluation`; configuration key `Alvo:Extensions:CelFunctionBudget` (env `Alvo__Extensions__CelFunctionBudget`), default `00:00:00.250`, bounds (0, 30 s]; diagnostic id **`ALVO1001`**, category `Reliability`, severity **Warning**, help link `https://alvo.burgyn.online/extending/analyzer-rules/#alvo1001`; analyzer assembly `MMLib.Alvo.Analyzers.dll` at nupkg path `analyzers/dotnet/cs/`; calibration hook ids `calibration-noop`, `calibration-read`, `calibration-after`; k6 shapes `hook_unit`, `hook_noop`, `hook_read`, `hook_after` with reference prefixes `WO-80`, `WO-81`, `WO-82`, `WO-83`.
- **Engine parity:** acceptance (3) and (4) run on SQLite (`MMLib.Alvo.Api.Tests`, `SqliteDataApiTests`) and PostgreSQL (`MMLib.Alvo.Api.Tests.Integration`, `DataApiOnPostgresTests`) through `test/_shared/api/DataApiEngineTests.HookGuards.cs`. No SQL changes; no dialect member.
- **Security core.** §6.2 does not list HX-10, but #309 carries `needs-deep-review` and the budget and E1 change how a before-hook's failure is classified and answered inside the write's transaction. Task 10 runs the `alvo-security-core-review` checklist, a labelled `/security-review` substitute, and labels the PR `needs-deep-review` (Ruling 23).
- Public XML docs cite no `§` and no `#issue` (DocsGen's `RemarksFilterTests`). XML docs on every public member.
- C# style: `alvo-dotnet-conventions` — no inline comments, methods ≲ 25 lines, English only, `ConfigureAwait(false)` in `src/`, never in tests (xUnit1030); every test call passes `TestContext.Current.CancellationToken` (xUnit1051). `.cs` files are **UTF-8 with BOM + CRLF**: after creating or editing `.cs` files run `dotnet format whitespace MMLib.Alvo.slnx --include <file> <file> …` (file paths, never a directory). The analyzer project targets `netstandard2.0`: no `ArgumentNullException.ThrowIfNull`, no `string.Contains(char)`, no records (no `IsExternalInit`), every string comparison ordinal.
- Single-test runs: `dotnet test --project <test project> --filter-class '*<ClassName>'` (MTP). Public-API baselines: `*PublicApiApprovalTests` in `test/MMLib.Alvo.Tests` and the new `test/MMLib.Alvo.Analyzers.Tests`. To accept a baseline: run the approval test, read the `*.received.txt` diff — it must add exactly the symbols listed above — then move it over the `*.verified.txt`. A grown `PublicApi.*.verified.txt` (or any `*.verified.*`) trips the turn-review gate: dispatch `alvo-snapshot-judge`, and justify each added symbol against `alvo-architecture-rules` (*public is the contract*) and the list above.
- Rings: `scripts/test-ring0` after every task, `scripts/test-ring1` after each task marked **[slice end]**, `scripts/test-ring2` before the PR. `scripts/test-admin-e2e` runs **whole once**, in Task 10 (no dashboard slice here). **Never run Stryker locally**; mutation is configuration only (Ruling 22). **`scripts/test-load` runs once, in Task 10** (Ruling 21); earlier tasks only build its image and lint the script.
- **Test-gap Importants fold into the next task:** an Important review finding that is only a missing test is fixed as the first step of the following task, not by reopening the reviewed one.
- **CI parity:** rings are Debug on the local SDK; CI builds Release with `TreatWarningsAsErrors`, `AnalysisLevel=latest-recommended` and `EnforceCodeStyleInBuild`. Task 10 runs the Release build in the CI SDK container, as the user, before anything is pushed.
- Commits: Conventional Commits, one per task, each message ending with `Claude-Session: https://claude.ai/code/session_01NJAtafM29iddLwfhb5gnmB`. No `Closes`/`Fixes` keyword anywhere in a commit message; the PR body carries `Closes #387`, `Closes #309` and `Closes #315`, each with its own keyword.

## Review Focus

0. **A host function that catches its own cancellation and returns a value after the budget** — `try { client.Wait(ct); } catch (OperationCanceledException) { return "fallback"; }` — must still fail the write: the overrun is measured on return, not only observed through the token. `CelFunctionBudgetTests.A_function_that_catches_its_cancellation_and_returns_is_still_an_overrun` (Task 1); `DataApiEngineTests.A_host_function_that_catches_its_cancellation_and_returns_a_fallback_is_still_function_failed_on_every_engine` (Task 4, both engines).
1. **A failure that is not the caller's, answered 422** — a host function throwing, a host function's argument not fitting its parameter, an interpreter defect (`<hook>`), a write hook's own failure (`hook-failed`), **a built-in refusing a value a host function produced (`int(rateOf(new.c))`, `new.qty / hostZero()`), the stored row (`int(old.qty_text)`), or a value an earlier hook wrote**, a command handler's own write, or an aggregate that also holds a host failure — must stay 500: a 422 tells an agent to change its input and retry, forever. `CelFailureOriginTests` (every origin, incl. `A_built_in_refusing_a_host_function_s_output_is_not_the_caller_s`, `…the_stored_row…`, `…a_value_an_earlier_hook_wrote…`, Task 3); `ExpressionFailedAnswerTests.A_hook_failure_and_a_host_function_failure_are_never_a_port_refusal`, `…An_aggregate_that_also_holds_a_host_failure_is_not_422`, `…A_command_s_own_write_is_never_422` (Task 3).
2. **Documented, compliant hook code reported by `ALVO1001`** — awaiting `write.Transaction` (incl. `AsSystem()`), the typed `QueryAsync<T>`/`CreateAsync<T>` extensions on `IAlvoSession`, a context's `PublishAsync`, and the hook type's own private helper (or a nested helper type's) must not be reported; a false positive under `TreatWarningsAsErrors` breaks a consumer's build. `Alvo1001AwaitTests.Awaiting_the_write_s_transaction_its_typed_extensions_publish_and_own_helpers_is_not_reported` (Task 6); every `website/src/snippets/extending/**` snippet compiled with the analyzer as an error (Task 7).
3. **The service-locator route around the boot ban** — `services.GetRequiredService<HttpClient>()`, `provider.GetService(typeof(IAlvoData))`, `Lazy<HttpClient>`, `IEnumerable<IAlvoData>` inside a before-write hook — is exactly what HX-09 Ruling 62 deferred to the analyzer and must be reported — and so is **awaiting a typed client the host wrote itself** (`await rates.EuroAsync(ct)`), the case Ruling 62 named; only a *synchronous* call into one is a documented blind spot. `Alvo1001ReachTests.A_service_locator_lookup_or_a_wrapper_of_a_banned_type_is_reported`, `Alvo1001AwaitTests.Awaiting_a_typed_client_the_host_wrote_itself_is_reported` (Task 6).
4. **A misconfigured budget** — `Alvo__Extensions__CelFunctionBudget=0`, `-00:00:01`, `01:00:00` — must refuse the start with a sentence naming the key and its environment variable, never run with no limit or a one-hour transaction. `AlvoExtensionOptionsTests.A_cel_function_budget_outside_its_bounds_refuses_the_start_naming_the_key` (Task 1).
5. **A host function that blocks on a task with `.Result` past the budget** — the most common sync-over-async shape — throws an `AggregateException`, not an `OperationCanceledException`, and must still be reported as the budget overrun, not as an anonymous host failure. `CelFunctionBudgetTests.A_function_that_blocks_with_result_past_the_budget_is_an_overrun` (Task 1).

---

## Rulings and deviations this plan takes

Each is recorded in the spec amendment (Task 9) as *HX-10 settled details* (1–44), so a later reader can tell a decision from an oversight.

1. **No new `AddCelFunction` overload; the `Delegate` method accepts a trailing `CancellationToken`.** §5 row `10` lists "`AddCelFunction` overload with trailing `CancellationToken`", but the method already takes `Delegate`, which admits every shape: a second overload over `Delegate` cannot exist, and typed `Func<…, CancellationToken, TResult>` overloads (four arities) would add eight public members for no shape the current one refuses. A lambda `(string currency, CancellationToken ct) => …` binds by its natural type. The token is never a CEL parameter (four CEL parameters plus the token are accepted); a token that is not last, or two tokens, refuse at the call. §5 row `10` is amended.
2. **The budget bounds every host function call, token-aware or not.** A token-aware function receives a token that cancels at the pass's deadline (linked with the caller's token where one exists); every function — one that ignores the token, one that takes none, one that catches its cancellation and returns — is **measured on return** and fails if it returned after the deadline (HX-09 Ruling 11's rule, X15's reasoning). Measuring the token-free ones too is a behaviour change for existing hosts (CHANGELOG *Changed*): §1.2's "before = in-transaction, time budget" was otherwise unenforced for exactly the code that can block. *(Amended by Rulings 29, 30, 34, 36.)*
3. **"Per evaluation" is one pass, opened by three sites.** A pass is `BeforeHookRunner.Run` (all declarative before-hooks of one row), one write hook's `when`, and one `Condition`-profile evaluation by `PredicateEvaluator` (after-hook and handler conditions at fan-out). Every host call in a pass shares one deadline, whose clock starts at the pass's **first host call** (a pass that calls none reads no clock and creates no token source; the pass object and the `AsyncLocal` set/restore still allocate — Ruling 35). `Rule` and `Access` expressions cannot call host functions (the profiles refuse them), so `PredicateEvaluator` opens no pass for them and the rule hot path is unchanged. Outside a pass (unit tests, tooling) a host function gets `CancellationToken.None` and no limit; `CelEvaluationSitesTests` pins the three sites so a fourth caller of the interpreter's hook entry points is a deliberate change. *(Amended by Ruling 35.)*
4. **`AlvoExtensionOptions.CelFunctionBudget`** (`TimeSpan`, default `AlvoHookOptions.DefaultTimeout` = 250 ms, bounds (0, `AlvoHookOptions.MaxTimeout` = 30 s], refused at start naming `Alvo:Extensions:CelFunctionBudget` and `Alvo__Extensions__CelFunctionBudget`). A public property §5 does not list: #309 asks for "an option", and `Alvo:Extensions` is the section that already bounds host code (`MaxDepth`). One number for all in-transaction host code: a before-write hook and a host function get the same 250 ms, and Q5's calibration (Ruling 20) judges both.
5. **An overrun is the host function's failure.** It is a `CelFunctionException` with `IsHost: true` and Alvo's reason: in a declarative before-hook the write rolls back with 500 `function-failed`; in a write hook's `when` it is that hook's failure, 500 `hook-failed`, reason `exception` (HX-09 Ruling 63, unchanged); in a post-commit condition the after-hook is dropped with a Warning, as any function failure there. A cancellation that is **not** the deadline (the unit's 30 s, a client abort) is not an overrun: it is the function's exception, as today.
6. **E1's line: `CelFunctionException.OnCallerData`.** True for a built-in's refusal (`int('x')`, `substring` past the end, `timestamp()` of a malformed value, `math.round` digits), a built-in argument that does not fit its parameter, hook arithmetic (overflow, division by zero, a non-number operand), an operator's refusal on the fail-closed path (`!_`, a comparison of incomparable values, `changed` that cannot compare), and a whole condition that is a present non-Bool (`<condition>` — HX-09 Ruling 63's amendment counts it as the caller's). False for anything a host function did (threw, argument mismatch, budget), for a built-in body that threw an unexpected exception, and for the interpreter's defect wrapper (`<hook>`, Ruling Y-D) — those are 500 `function-failed`. *(Amended by Rulings 26, 28, 44.)*
7. **`expression-failed` is answered on every host, not opt-in.** `ProblemResultFactory.PortRefusalFor` maps it (so `GuardAsync` on every generated route and `CommandEndpointFilter` on every command answer it), walking wrappers the way `AlvoExceptionHandler.FunctionFailure` does; `AlvoExceptionHandler` answers it too for a failure that escapes elsewhere on an Alvo route. Not logged — like every caller refusal (a 422 pages nobody); the previous Error line for a built-in failure is gone (CHANGELOG). The `detail` names the function and Alvo's reason, never the value (built-in reasons never carry one); no `violations` member, because no field is known — the function's argument is an expression, not a payload field. *(Amended by Rulings 27, 28, 37.)*
8. **A host function's argument that does not fit its parameter stays 500.** The value is the caller's, but the type is the host's signature: the fix is the host's (widen the parameter) or the descriptor author's, never something the caller can learn from a 422. One rule for agents: anything about a host function is 500.
9. **A single-row `DELETE` that answers 422 `expression-failed` does not document 422 in OpenAPI.** Reachable only through a before-delete condition (descriptor or host `when`) whose built-in refuses the stored row; the OpenAPI generator reads the schema model, which carries no hooks, so documenting it would document a status unreachable for every hook-free entity, which `OpenApiDocumentTests.Every_documented_status_code_is_one_the_endpoint_can_actually_return` refuses. The `unprocessable` response's description names the new kind; the `type` enum gains the slug. Recorded deviation; follow-up issue (Task 10 Step 8). *(Moot under Ruling 44: a DELETE carries no body, so no field of it was sent and it never answers 422 — the OpenAPI document is right as it stands, and the follow-up issue is dropped.)*
10. **`ALVO1001`'s scope.** (a) Every operation block whose containing type implements `IBeforeWriteHook`, `IAfterWriteHook`, `IBeforeDeleteHook` or `IAfterDeleteHook` (generic or not) — the whole type, constructors and field initializers included, matching the boot ban's "in-transaction hook type"; (b) the delegate passed as `hook` to `AlvoEntityBuilder`/`AlvoEntityBuilder<T>`'s `BeforeWrite`, `AfterWrite`, `BeforeDelete`, `AfterDelete`; (c) the delegate passed as `function` to `AddCelFunction`. For (b) and (c) a lambda's body is analysed, and a method group whose method is declared **in the same file** (through `IOperation.SemanticModel`); a method group declared elsewhere is not (documented limit, like "direct dependencies only"). Validate hooks, event handlers and commands are out of scope — they run outside any transaction. *(Amended by Rulings 33, 39.)*
11. **What `ALVO1001` reports — the boot ban's list, plus files.** A type is reached when it (or an array element, a generic argument, a base type or an implemented interface of it) is in `System.Net.Http`, `System.Net.Sockets`, `System.Net.WebSockets`, `System.Net.Mail`, `System.Net.Quic`, `System.Net.{WebClient,WebRequest,HttpWebRequest,FtpWebRequest}`, `Microsoft.AspNetCore.Identity*`, `MMLib.Alvo.Identity*`, is named `IEmailSender`, or is one of `HookDependencies`' types (`DbConnection`, `DbDataSource`, `IAlvoEvents`, `IAlvoData`, `IAlvoSessions`, `IAlvoTransactions`, `IOutboxStore`, `IDeliveryStore`, `IAlvoCommands`, `IAlvoEventPump`, `IAlvoManagement`, `IAlvoOperations`, `IAlvoUserStore`, `IAlvoUserAdministration`, `IAlvoBootstrapAdmin`, `IAlvoAssistant`) — plus `System.Data.IDbConnection` and `System.IO.{File,FileInfo,FileStream,Directory,DirectoryInfo}` (#309 names `System.IO.File*`). `BootBanParityTests` holds the analyzer's list a superset of `HookDependencies`' (core IVT to the analyzer's tests). Reported operations: an object creation, a field/property/parameter/local reference of a reached type, a static call on one, a generic argument (`GetRequiredService<HttpClient>()`), a `typeof` (`GetService(typeof(IAlvoData))`). *(Amended by Ruling 32.)*
12. **The await rule (hooks only).** An `await` (through `ConfigureAwait`) of a call to a method declared in metadata is reported unless the method belongs to `IAlvoSession` (so `IAlvoTransaction`), is an extension method whose `this` parameter is an `IAlvoSession` (the typed reads/writes), or is declared in `MMLib.Alvo.Hooks` (a context's `PublishAsync`). An await of the host's own source method is not reported — its body is analysed where it belongs to a hook type. An await of a local, field or property is not reported (no call to judge). §4.4.5's "any `await` on a task not produced by `write.Transaction`", narrowed to what an operation tree can decide without data flow. *(Amended by Ruling 25.)*
13. **The CEL-function task rule.** In an `AddCelFunction` delegate, any call returning `Task`, `Task<T>`, `ValueTask` or `ValueTask<T>` is reported: the delegate is synchronous (async delegates are refused at registration), so it can only block on it (VSTHRD002's reasoning). #309's "`Task`-returning calls".
14. **One diagnostic per use.** A call on a reached receiver (`http.GetStringAsync(…)`) is reported once, at the receiver; the invocation and an `await` of it are not reported again. *(Amended by Ruling 31.)*
15. **Analyzer packaging (E6).** A project, not a package: `src/MMLib.Alvo.Analyzers` is `IsPackable=false`, targets `netstandard2.0` (the one target a compiler loads; `SolutionConventionTests` gains an exemption keyed on `<IsRoslynComponent>true</IsRoslynComponent>` allowing exactly `TargetFramework=netstandard2.0`), references `Microsoft.CodeAnalysis.CSharp` **4.14.0** (`PrivateAssets=all`; older than the compiler of the minimum SDK 10.0.100, so every supported SDK loads it), and is placed by `MMLib.Alvo`'s pack at `analyzers/dotnet/cs/` through a `TargetsForTfmSpecificContentInPackage` target, behind a `ProjectReference` with `ReferenceOutputAssembly="false"` and `PrivateAssets="all"` (build order only; no runtime reference, no nuspec dependency — `Core_depends_only_on_Abstractions` keeps proving the first). The analyzer class is `public`: Roslyn instantiates it by reflection and every Microsoft analyzer is public; the assembly is never a compile reference of a consumer, so the public type is not API a consumer can call. Rule (a) names Roslyn because a *runtime* Roslyn dependency is heavy; an analyzer asset adds nothing to the runtime closure.
16. **Acceptance (2) is a script, not a ring.** `scripts/test-packed-analyzer` packs `MMLib.Alvo.Abstractions` and `MMLib.Alvo` (or takes CI's `artifacts/`), checks the nupkg layout and nuspec, and builds a consumer generated in a temp directory inside `mcr.microsoft.com/dotnet/sdk:<global.json version>` (10.0.100), asserting `warning ALVO1001` on exactly the marked lines of `scripts/fixtures/packed-consumer/Hooks.cs` and a successful build. In no ring (it needs Docker and nuget.org); CI runs it on the Linux leg right after `Pack`, inside the required **Build & test** check.
17. **The analyzer is applied in-repo only to the docs snippets.** `MMLib.Alvo.DocsSnippets.Tests` references it as an analyzer, so every `website/src/snippets/extending/**` snippet compiles with `ALVO1001` as an error — the code users copy is clean, and a false positive fails the build. No other in-repo project gets it: test suites deliberately inject banned types into hooks to prove the boot ban.
18. **The analyzer is not mutated.** Every Stryker shard pins one `project`; the analyzer is a new project, so mutating it needs a new shard with probed `mutants:`/`suite:` budgets, which this PR may not probe (process constraint, 2026-11-04). Declared gap: the analyzer is advisory (a warning); the enforcement is the boot ban and the budget, both in mutated shards. Follow-up issue (Task 10 Step 8).
19. **Calibration runs a load host with three gated hooks.** The standalone image registers no hook, so `tools/MMLib.Alvo.Load.Host` (an `IsPackable=false` tool, like DocsGen, its own Dockerfile and `Dockerfile.dockerignore`) boots `AlvoHost`'s composition and adds, on `work_orders`, `calibration-noop` (before-write, no work), `calibration-read` (before-write, one `GetAsync` of the work order's customer through `write.Transaction`) and `calibration-after` (after-write, no work), each gated by a `when` on the reference prefix. Shapes: `create` (standalone image, no unit) vs `hook_unit` (`WO-80…`: no `when` matches — the write's own unit plus three `when` evaluations), `hook_noop`, `hook_read`, `hook_after`, on the **same dataset**, the alvo container swapped in place as the A/B arms are. Calibration tier only (and an explicit `--scenarios hook_*`); no gate row — a gate ceiling must be measured on the gate tier, and Q5 asks for a calibrated default, not a regression gate.
20. **Q5's decision rule.** Keep the 250 ms in-transaction default (hooks and host functions alike) when `hook_read`'s p99 on the calibration rig is ≤ 25 ms — a tenfold headroom over a hook that does one read; otherwise raise `AlvoHookOptions.DefaultTimeout` (and with it `CelFunctionBudget`'s default) to the smallest of 500 ms or 1 s that restores tenfold headroom, amend Q5, and record it in CHANGELOG. Validate's 5 s is not calibrated here: it bounds a network call this rig cannot represent.
21. **How the calibration numbers are produced.** Once, in Task 10, by `scripts/test-load --tier calibration --scenarios create,hook_unit,hook_noop,hook_read,hook_after` on the maintainer's machine (the first `docs/performance.md` baseline's rig: Apple M-series, Docker Desktop, co-resident generator), ~10 minutes. If Docker is unavailable there, `gh workflow run load.yml --ref hx/10-hook-guards -f tier=calibration` runs the whole calibration tier on `ubuntu-latest` (the branch's own `scripts/test-load`), its `artifacts/load/*.json` are downloaded with `gh run download`, and the section names that rig instead. Either way the numbers are copied by hand from the printed table into `docs/performance.md`, never edited.
22. **Mutation is configuration only.** New core code lands in existing shards by their globs, so no config changes: `Expressions/Internal/CelFunctionBudget.cs`, `CelFunctionBudgetPolicy.cs`, `HostCelFunction.cs`, `CelFunctionException.cs`, `PredicateEvaluator.cs` → `expressions` (killers in `MMLib.Alvo.Tests`; its `break: 90` is why every branch of the budget has a fact in Task 1); `Api/**` → `api` (killers in `MMLib.Alvo.Api.Tests`); `Rules/Internal/BeforeHookRunner.cs`, `Hooks/Internal/WriteExtensionRunner.cs`, `Extensions/Internal/AlvoExtensionOptionsConfiguration.cs` → the core shard. No local Stryker run and no re-probe (coordinator's instruction).
23. **Security-core treatment** although §6.2 omits HX-10 (see Global Constraints).
24. **`IBeforeHookRunner.Run` stays synchronous and token-free** (#309: "`BeforeHookRunner` mints the budgeted token itself"); its remarks, which said "Alvo offers no timeout around [a host function]", are rewritten, and `BeforeHookIsolationArchitectureTests` gains `CelFunctionBudgetPolicy` as the runner's second constructor dependency (its closure still reaches no network).

Rulings taken after the plan review (`.superpowers/sdd/hx-10-plan-review.md`; H = high, M = medium, L = low finding):

25. **(H1) The await rule exempts only the hook's own code.** A source-declared method is exempt from the await rule only when its containing type is the scope's hook type or a type nested in it (Ruling 33), or it is a local function declared inside the analysed body; for a delegate scope only such local functions. Everything else the host wrote — a typed HTTP client `IRatesClient.EuroAsync`, a repository, a helper class — is reported when awaited ("'IRatesClient.EuroAsync' is awaited, and only the write's transaction may be"), which is §4.4.5's rule and keeps HX-09 Ruling 62's promise ("a typed HTTP client … is the HX-10 analyzer's"). The documented blind spot is a *synchronous* call into the host's own code. `HookDependencies`' remarks and HX-09 Ruling 62 are amended to match (Task 9). Amends Ruling 12.
26. **(H2) E1 is decided by whose data was refused, not by which function threw.** A refusal that would be the caller's (`OnCallerData`) is re-attributed as not the caller's — 500 `function-failed` — when (a) the refusing node's subtree calls a host function or reads the stored row (`old.`, `changed`), judged once, at the innermost node that sees the failure, from the compiled tree (`CelDataOrigin.IsCallersOwn`; computed on the failure path, not stored at compile time — the same answer, no change to a tree node, X7's "no new CEL AST node", and no cost unless a refusal happens); or (b) the failing expression reads a field a hook wrote earlier in the same write — `BeforeHookRunner` attributes against every earlier hook's mutate targets, whether that hook fired or not (final review L1: the fields only fired hooks mutated made 422 against 500 a one-bit answer about the stored row an earlier condition reads), `WriteExtensionRunner` against the declarative before-hooks' mutate targets plus earlier host `Set`s for the before stages, and treats every refusal in an after-write `when` as not the caller's (the saved row carries defaults, rollups and hook writes). Conservative toward 500: an expression that reads a hook-written field anywhere is attributed as a whole. `BeforeHookRunner.Refusal`'s own remark ("the field … may be one they never sent") is the precedent. Amends Ruling 6. *(Amended by Rulings 44 and 50.)*
27. **(H2) Only the Data API's generated routes answer 422.** `expression-failed` is mapped in `ProblemResultFactory.RefusalFor` (the `GuardAsync` path), **not** in `PortRefusalFor`, so `CommandEndpointFilter` and the webhook receiver do not answer it; `AlvoExceptionHandler` answers it only when the failed endpoint carries `DataApiOperationMetadata`, and `function-failed` otherwise. A command handler builds its own writes, so a built-in refusing them is the handler's data, not the command caller's. Amends Ruling 7.
28. **(L2) An aggregate answers 422 only when every function failure in it is the caller's.** `AlvoExceptionHandler.CallerDataFailure` walks the wrappers, collects every `CelFunctionException`, and answers 422 only when there is at least one and all have `OnCallerData`; a mix answers 500 and logs the host's failure. Amends Rulings 6 and 7. *(Amended by Ruling 44.)*
29. **(H3) One boundary: at the limit is over.** An evaluation overran when `elapsed >= Limit` — the same instant the deadline token cancels (`CancellationTokenSource(Limit, time)` fires when the clock reaches it). The inside-budget fact advances `Limit − 1 ms`; a new fact pins that exactly `Limit` is an overrun. Amends Ruling 2.
30. **(M4) Once the deadline has passed, whatever the body threw is the overrun.** `CelFunctionBudget.Call` catches `Exception` (not only `OperationCanceledException`) when `Overran`, so `.Result`/`.Wait()` on a cancelled task (`AggregateException`) and a function that converts its cancellation into its own exception both read the budget sentence. Amends Ruling 2.
31. **(M1) One diagnostic per reached symbol per body.** References to constant fields and to static fields/properties of a value or enum type (`SocketType.Stream`, `ProtocolType.Tcp`) are not reported; a reached field, property, parameter or local is reported once per operation block (per lambda body for a delegate scope), at its first use. Creations, static calls, generic arguments, `typeof`, awaits and task calls are reported where they occur. Amends Ruling 14.
32. **(M2, L3, L7) Every reach rule applies to base types and interfaces, and the list grows.** `IsReachedItself`'s namespace, web-request, file, identity and `IEmailSender` checks run over the type and all its ancestors (`class RatesHttp : HttpClient`, a `DelegatingHandler` subclass, `SendGridSender : IEmailSender` are reached); `Microsoft.EntityFrameworkCore.DbContext` and `IDbContextFactory`1` join the type list by metadata name (the analyzer may be wider than the boot ban); the identity prefixes match by `StartsWith` exactly as the boot ban does. `BootBanParityTests` also compares the web-request names and the identity prefixes. Synchronous use of other clients (Redis `IDatabase`, gRPC `ClientBase`, `new StreamReader(path)`) is listed under *What it cannot see*. Amends Ruling 11.
33. **(L5) A hook's nested types are in its scope.** The scope of an operation block is the nearest type in its containing-type chain that implements an in-transaction hook contract, so a private nested helper inside a hook is analysed — and its methods count as the hook's own for Ruling 25. Amends Ruling 10.
34. **(M5) The budget is labelled breaking, with its post-commit effects.** CHANGELOG, `cel.md` and the CEL functions guide say: a host function past the budget fails the write (500); in an after-hook condition the after-hook is dropped with a Warning; in a handler's `when` the delivery is recorded dead (`HandlerVerdict.Failed`). Amends Ruling 2's CHANGELOG note.
35. **(L1) A pass is cheap, not free.** Opening one allocates the pass and sets an `AsyncLocal` on every before-hook run and every `Condition` evaluation at fan-out, host function or not; no clock is read and no token source is created until the first host call. Measured by the calibration's `hook_unit` shape only indirectly; recorded rather than optimised (a compile-time "calls a host function" flag would remove it, and is the follow-up if it ever shows). Corrects Ruling 3's wording.
36. **(L9) What the budget cannot do.** A function that never returns holds the write's transaction until it does (.NET cannot stop it; measured-on-return needs a return), and `BeforeHookRunner`'s pass links no outer token (`IBeforeHookRunner.Run` is token-free, Ruling 24), so a client abort never reaches a token-aware function in a declarative before-hook — only the deadline does. Both are stated in `cel.md`'s *Time budget* bullet and on *Analyzer rules*.
37. **(L10) The 422 stays unlogged.** `RefusalFor` is a static mapping with no logger, and no other 422 (`validation`, `malformed-query`) is logged; an operator sees a descriptor whose built-in refuses most writes in the HTTP request log by status and `type`. A counter is a follow-up if one is asked for. Amends Ruling 7 (recorded, unchanged).
38. **(L8) The no-Bool sentence stays reachable under 500.** A whole condition that is a present non-Bool and reads `old.` or a host function is not the caller's (Ruling 26), so `FunctionFailedDetail`'s `<condition>` arm still answers it; the 422 path has its own sentence.
39. **(L4, L6) The documented blind spots grow, and one is owed.** *What it cannot see* also lists a delegate held in a local or returned by a factory before it is passed to `AddCelFunction` or a hook verb, and a synchronous call into the host's own code; `docs/architecture/analyzers.md` records that HX-13 must add `IAlvoScheduler` to the await allow-list (§4.4.5 allows it in-transaction), or every scheduler call becomes a false positive. Amends Ruling 10.
40. **(L13) The Roslyn pin is repository-wide.** `CentralPackageTransitivePinningEnabled` makes the `Microsoft.CodeAnalysis.CSharp` 4.14.0 `PackageVersion` a transitive pin for every project; no current restore graph uses Roslyn, and the `Directory.Packages.props` comment says a future dependency on a newer Roslyn must move the analyzer to a separate pin (`VersionOverride` is banned) or raise the floor deliberately.
41. **(M3) The prototype fixtures follow the problem types.** `scripts/gen-prototype-fixtures` copies `AlvoProblemTypes`' constants into `docs/design/f5-admin/generated/schema-facets.js`, which CI checks with `--check`; Task 3 regenerates it after adding `ExpressionFailed`, and Task 10 re-checks after the rebase onto `main` (which carries HX-09's own regeneration).
42. **(M6, L11) The engine facts cannot race.** The overrun facts use a 200 ms budget against a 1 s sleep or a 10 s token wait; the inside-budget fact uses 2 s; the token-free fact is named for what it is, and a both-engine fact covers Review Focus 0's shape (a function that catches its cancellation and returns a fallback).
43. **(L12) The snippet type is `CurrencyDeskHooks`**, in `CurrencyDeskHooks.cs` (tests `CurrencyDeskHooksTests.cs`), so no type shares its namespace's name — `OpeningDeskHooks`' precedent.

Ruling taken after the Task 3 review (`.superpowers/sdd/2026-11-05-hx-10-hook-guards/task-3-review.md`):

44. **(E1) Only values the caller sent in this request are the caller's data.** `new.` (and an unqualified reference) reaches more than the request: on an update the stored value of every field the PATCH did not send, on a create or a replace a default, on a delete the row being removed. So the innermost node that judges a refusal (Ruling 26) also records, on the `CelFunctionException`, the written row's fields its subtree reads (`ReadFields`, computed on the failure path), and the Data API answers 422 `expression-failed` only when that set is non-empty and every field in it was in this request's body; otherwise 500 `function-failed`. The body's fields are recorded on the request (`CallerSentFields`, an `HttpContext` feature) where a generated write route binds its body — a batch records the fields every row carried, because a refusal does not say which row it came from — and both `GuardAsync` and `AlvoExceptionHandler` read it; a request with no body records nothing, so a DELETE (single or batch) never answers 422. No public port changes: `IBeforeHookRunner.Run` and `WriteInvocation` stay as they are, because whose data a value is matters only to the HTTP answer. A refusal that reads only literals or `@user`/`@tenant` reads no field and is 500 (the descriptor author's or the identity provider's data, never the caller's — Task 3 review m2). An aggregate answers 422 only when every leaf is such a refusal; a leaf that is no function failure (a storage exception beside it) disqualifies it, so the 500 path logs it (amends Ruling 28). Amends Rulings 6, 9 (moot), 26 and 28.

Rulings taken after the final review (`.superpowers/sdd/2026-11-05-hx-10-hook-guards/substitute-code-review.md`; Rulings 45–47 are recorded in the spec's *HX-10 settled details*):

48. **The text cap reached by caller-sent data stays 500** (final review M5). `CelBuiltInFunctions.TextCapExceeded` keeps the defect-style constructor, so a `_+_` or `replace` over a field the caller sent that grows past the text cap answers 500 `function-failed`, fail-closed — the cap is a server limit, not a built-in refusing the caller's input, unlike `substring` past the end (Ruling 6). Cost if wrong: a 500 where a 422 would help the caller.
49. **The per-pass allocation and the attribution on declarative-only writes are accepted** (final review M3, M4). Every pass that calls a host function allocates a timer-backed and a linked token source, and every hook run pays the `AsyncLocal` pass and `CelDataOrigin.Attributed`'s closure, host function or not; the calibration shows `hook_unit` at 1.05x `create` on `min`, so neither is optimised here — revisit if a profile shows them (Ruling 35's follow-up). Cost: a small per-write overhead.
50. **A non-Bool operand of `&&`, `||` or `?:` is judged over the whole logical node** (final review M6). `Truth` throws inside the logical node's `EvaluateNode`, so `IsCallersOwn` and `ReadFields` also cover an operand that never ran; it errs toward 500, which is safe, and is accepted rather than judging the operand itself. Cost: a less precise 422 (amends Ruling 26's "innermost node").
51. **A host hook's `when` that reads `old.` can still tell 422 from 500 by whether an earlier host hook ran** (re-review N2). `WriteExtensionRunner.HookWritten` counts host `Set`s only from hooks that ran, because a host hook's targets are decided by its code at run time, not declared — there is no static set to attribute against, as Ruling 26 (b) does for declarative hooks. The bit exists only when the host registers a hook gated on the stored row and a later expression refuses a field that hook would have set; the host is trusted code and owns that design. Accepted as a stated limit, not fixed. Cost if wrong: a one-bit answer about a stored field, through a host-authored hook, to a caller who may not read it. Follow-up: declared `Set` targets on host hooks would close it.

---

## File structure

Created — core `src/MMLib.Alvo`:

| File | Responsibility |
|---|---|
| `Expressions/Internal/CelFunctionBudget.cs` | one evaluation pass: `AsyncLocal` current, lazy deadline, the budgeted host call |
| `Expressions/Internal/CelFunctionBudgetPolicy.cs` | DI singleton: opens a pass from `Alvo:Extensions:CelFunctionBudget` on the host's clock |
| `Expressions/Internal/CelDataOrigin.cs` | whose data a refusal read: the caller's, or a host function's output, the stored row, a hook-written value (E1, Ruling 26) |

Created — analyzer `src/MMLib.Alvo.Analyzers`: `MMLib.Alvo.Analyzers.csproj`, `AnalyzerReleases.Shipped.md`, `AnalyzerReleases.Unshipped.md`, `Properties/AssemblyInfo.cs`, `AlvoDiagnostics.cs` (the descriptor), `AlvoSymbols.cs` (the framework symbols of one compilation, scopes, allowed awaits), `AnalysisScope.cs`, `IoReach.cs` (what an operation reaches), `ReportedSymbols.cs` (one diagnostic per reached symbol per body), `InTransactionIoAnalyzer.cs` (the entry).

Created — load host `tools/MMLib.Alvo.Load.Host`: `MMLib.Alvo.Load.Host.csproj`, `Program.cs`, `CalibrationHooks.cs`, `Dockerfile`, `Dockerfile.dockerignore`.

Created — tests: `test/MMLib.Alvo.Tests/Expressions/{CelFunctionBudgetTests.cs,TestBudgets.cs,CelFailureOriginTests.cs,PredicateEvaluatorBudgetTests.cs,CelEvaluationSitesTests.cs}`; `test/MMLib.Alvo.Api.Tests/ExpressionFailedAnswerTests.cs`; `test/_shared/api/DataApiEngineTests.HookGuards.cs`, `test/_shared/api/descriptors/{cel-budget.alvo.json,expression-failed.alvo.json}`; `test/MMLib.Alvo.Analyzers.Tests/{MMLib.Alvo.Analyzers.Tests.csproj,AnalyzerHarness.cs,Alvo1001ScopeTests.cs,Alvo1001ReachTests.cs,Alvo1001AwaitTests.cs,Alvo1001CelFunctionTests.cs,BootBanParityTests.cs,Alvo1001DocsTests.cs}`; `test/MMLib.Alvo.TestHost.Tests/CalibrationHooksTests.cs`, `test/_shared/testhost/descriptors/calibration-desk.alvo.json`.

Created — scripts: `scripts/test-packed-analyzer`, `scripts/fixtures/packed-consumer/Hooks.cs`.

Created — docs: `website/src/content/docs/extending/analyzer-rules.mdx`; `website/src/snippets/extending/analyzer-rules/{CurrencyDeskHooks.cs,CurrencyDeskHooksTests.cs,currency-desk.alvo.json}`; `docs/architecture/analyzers.md`.

Modified: core `Expressions/Internal/CelInterpreter.cs` (the judging catch), `Extensions/AlvoExtensionOptions.cs`, `Extensions/Internal/AlvoExtensionOptionsConfiguration.cs`, `Expressions/Setup.cs`, `Expressions/Internal/{HostCelFunction.cs,CelFunctionException.cs,PredicateEvaluator.cs}`, `AlvoBuilderExtensions.cs` (docs), `Rules/Internal/BeforeHookRunner.cs`, `Hooks/Internal/{WriteExtensionRunner.cs,HookDependencies.cs}` (also its remarks), `Api/AlvoProblemTypes.cs`, `Api/Internal/{ProblemResultFactory.cs,AlvoExceptionHandler.cs,DataApiDocumentation.cs}`, `Properties/AssemblyInfo.cs`, `MMLib.Alvo.csproj`; Abstractions `Rules/IBeforeHookRunner.cs` (remarks only); `Directory.Packages.props`; `MMLib.Alvo.slnx`; tests `test/MMLib.Alvo.Tests/{Extensions/AlvoExtensionOptionsTests.cs,Rules/BeforeHookFacetTests.cs,Rules/BeforeHookCompilerTests.cs,Rules/BeforeHookIsolationArchitectureTests.cs,Hooks/WriteExtensionRunnerTests.cs,Expressions/CelFunctionRegistrationTests.cs,PublicApi.MMLib.Alvo.verified.txt}`, `test/MMLib.Alvo.Api.Tests/{CelBuiltInWriteTests.cs,ProblemDetailsTests.cs,AlvoExceptionHandlerTests.cs,OpenApiDocumentTests.The_document_is_stable.verified.txt}`, `test/MMLib.Alvo.Conventions.Tests/SolutionConventionTests.cs`, `test/MMLib.Alvo.TestHost.Tests/MMLib.Alvo.TestHost.Tests.csproj`, `test/MMLib.Alvo.DocsSnippets.Tests/MMLib.Alvo.DocsSnippets.Tests.csproj`, any other `*.verified.txt` the `type` enum moves (found by the approval runs); load `test/load/{scenarios.js,README.md}`, `scripts/test-load`; CI `.github/workflows/ci.yml`; docs `website/src/data/problem-types.json`, `website/astro.config.mjs`, `website/src/content/docs/extending/{index.mdx,write-hooks.mdx}`, the `function-failed` sweep (`website/src/content/docs/{guides/before-hooks.mdx,guides/custom-cel-functions.mdx,guides/handle-errors.mdx,guides/call-from-endpoints.mdx,data-api/conventions.mdx,concepts/security-model.md}`, `samples/MMLib.Alvo.Samples.EmbeddedHost/README.md` — whatever `git grep` finds), `docs/architecture/{cel.md,data-api.md,extensibility.md,write-hooks.md,package-boundary.md}`, `docs/design/f5-admin/generated/schema-facets.js` (regenerated), `docs/superpowers/plans/2026-11-04-hx-09-write-hooks.md` (Ruling 62's amendment note), `docs/performance.md`, `CHANGELOG.md`, `CLAUDE.md` (repo map: `tools/` and `src/MMLib.Alvo.Analyzers`), the spec.

---

## Task 1: The host-function budget — the option, the pass, the token-aware delegate

**Files:**
- Create: `src/MMLib.Alvo/Expressions/Internal/CelFunctionBudget.cs`, `src/MMLib.Alvo/Expressions/Internal/CelFunctionBudgetPolicy.cs`
- Modify: `src/MMLib.Alvo/Extensions/AlvoExtensionOptions.cs`, `src/MMLib.Alvo/Extensions/Internal/AlvoExtensionOptionsConfiguration.cs`, `src/MMLib.Alvo/Expressions/Internal/HostCelFunction.cs`, `src/MMLib.Alvo/Expressions/Setup.cs`, `src/MMLib.Alvo/AlvoBuilderExtensions.cs` (XML docs)
- Test: `test/MMLib.Alvo.Tests/Expressions/CelFunctionBudgetTests.cs`, `test/MMLib.Alvo.Tests/Expressions/TestBudgets.cs`, `test/MMLib.Alvo.Tests/Expressions/CelFunctionRegistrationTests.cs`, `test/MMLib.Alvo.Tests/Extensions/AlvoExtensionOptionsTests.cs`, `test/MMLib.Alvo.Tests/PublicApi.MMLib.Alvo.verified.txt`

**Interfaces:**
- Consumes: `AlvoHookOptions.DefaultTimeout` (250 ms), `AlvoHookOptions.MaxTimeout` (30 s); `CelFunctionException(string functionName, bool isHost, string reason)`; `CelFunction.Run` (wraps any non-`CelFunctionException` as the host's failure).
- Produces:
  - `public TimeSpan AlvoExtensionOptions.CelFunctionBudget { get; set; }` (default 250 ms).
  - `internal sealed class CelFunctionBudget : IDisposable` — `internal static CelFunctionBudget Enter(TimeSpan limit, TimeProvider time, CancellationToken outer)`, `internal static object? Call(string function, bool tokenAware, object?[] arguments, Func<object?[], object?> body)`, `internal TimeSpan Limit { get; }`.
  - `internal sealed class CelFunctionBudgetPolicy(IOptions<AlvoExtensionOptions> options, TimeProvider time)` — `internal CelFunctionBudget Enter(CancellationToken outer = default)`; registered `TryAddSingleton` in `AddAlvoExpressions`.
  - Test helper `TestBudgets.Default(TimeProvider? time = null, TimeSpan? limit = null) : CelFunctionBudgetPolicy`.

- [ ] **Step 1: Write the failing option facts**

Append to `test/MMLib.Alvo.Tests/Extensions/AlvoExtensionOptionsTests.cs` (it already has a `Container(Dictionary<string, string?>)` helper building a provider over in-memory configuration):

```csharp
    [Fact]
    public void The_cel_function_budget_defaults_to_the_in_transaction_hook_timeout()
    {
        using var services = Container(new());

        services.GetRequiredService<IOptions<AlvoExtensionOptions>>().Value.CelFunctionBudget.ShouldBe(TimeSpan.FromMilliseconds(250));
    }

    [Fact]
    public void The_cel_function_budget_is_bound_from_its_section()
    {
        using var services = Container(new() { ["Alvo:Extensions:CelFunctionBudget"] = "00:00:00.040" });

        services.GetRequiredService<IOptions<AlvoExtensionOptions>>().Value.CelFunctionBudget.ShouldBe(TimeSpan.FromMilliseconds(40));
    }

    [Theory]
    [InlineData("00:00:00")]
    [InlineData("-00:00:01")]
    [InlineData("00:00:30.001")]
    [InlineData("01:00:00")]
    public void A_cel_function_budget_outside_its_bounds_refuses_the_start_naming_the_key(string configured)
    {
        using var services = Container(new() { ["Alvo:Extensions:CelFunctionBudget"] = configured });

        var refused = Should.Throw<OptionsValidationException>(
            () => services.GetRequiredService<IOptions<AlvoExtensionOptions>>().Value);

        refused.Message.ShouldContain("Alvo:Extensions:CelFunctionBudget");
        refused.Message.ShouldContain("Alvo__Extensions__CelFunctionBudget");
        refused.Message.ShouldContain("30 s");
    }

    [Fact]
    public void A_budget_of_exactly_thirty_seconds_is_accepted()
    {
        using var services = Container(new() { ["Alvo:Extensions:CelFunctionBudget"] = "00:00:30" });

        services.GetRequiredService<IOptions<AlvoExtensionOptions>>().Value.CelFunctionBudget.ShouldBe(TimeSpan.FromSeconds(30));
    }
```

- [ ] **Step 2: Write the failing budget facts**

Create `test/MMLib.Alvo.Tests/Expressions/TestBudgets.cs`:

```csharp
using Microsoft.Extensions.Options;
using MMLib.Alvo.Expressions.Internal;
using MMLib.Alvo.Extensions;

namespace MMLib.Alvo.Tests.Expressions;

/// <summary>A budget policy for facts that build a runner or an evaluator by hand.</summary>
internal static class TestBudgets
{
    internal static CelFunctionBudgetPolicy Default(TimeProvider? time = null, TimeSpan? limit = null) =>
        new(Options.Create(new AlvoExtensionOptions { CelFunctionBudget = limit ?? TimeSpan.FromMilliseconds(250) }), time ?? TimeProvider.System);
}
```

Create `test/MMLib.Alvo.Tests/Expressions/CelFunctionBudgetTests.cs`:

```csharp
using Microsoft.Extensions.Time.Testing;
using MMLib.Alvo.Expressions.Internal;

namespace MMLib.Alvo.Tests.Expressions;

/// <summary>
/// The host functions' time budget (#309): one deadline per evaluation pass, a token for a function that takes one, and a
/// measurement on return for every function, so neither ignoring nor catching the token escapes it.
/// </summary>
public sealed class CelFunctionBudgetTests
{
    private readonly FakeTimeProvider _clock = new();

    private CelFunction Function(Func<string, CancellationToken, string> body) =>
        HostCelFunction.Create("slow", body, summary: null);

    private CelFunction TokenFree(Func<string, string> body) => HostCelFunction.Create("plain", body, summary: null);

    private CelFunctionBudget Pass(CancellationToken outer = default) =>
        TestBudgets.Default(_clock).Enter(outer);

    [Fact]
    public void A_token_aware_function_receives_the_pass_s_token_and_its_cancellation_is_an_overrun()
    {
        var function = Function((s, ct) =>
        {
            _clock.Advance(TimeSpan.FromMilliseconds(300));
            ct.ThrowIfCancellationRequested();
            return s;
        });
        using var pass = Pass();

        var failure = Should.Throw<CelFunctionException>(() => function.Invoke(["x"]));

        failure.IsHost.ShouldBeTrue();
        failure.FunctionName.ShouldBe("slow");
        failure.Reason.ShouldBe("it ran past the 250 ms that host functions have in one evaluation");
        failure.OnCallerData.ShouldBeFalse();
    }

    [Fact]
    public void A_function_that_ignores_the_token_is_measured_when_it_returns()
    {
        var function = TokenFree(s =>
        {
            _clock.Advance(TimeSpan.FromMilliseconds(251));
            return s;
        });
        using var pass = Pass();

        Should.Throw<CelFunctionException>(() => function.Invoke(["x"])).Reason.ShouldStartWith("it ran past");
    }

    [Fact]
    public void A_function_that_catches_its_cancellation_and_returns_is_still_an_overrun()
    {
        var function = Function((s, ct) =>
        {
            _clock.Advance(TimeSpan.FromMilliseconds(300));
            try
            {
                ct.ThrowIfCancellationRequested();
            }
            catch (OperationCanceledException)
            {
                return "fallback";
            }

            return s;
        });
        using var pass = Pass();

        Should.Throw<CelFunctionException>(() => function.Invoke(["x"])).Reason.ShouldStartWith("it ran past");
    }

    [Fact]
    public void A_function_inside_its_budget_answers_and_its_token_is_live()
    {
        var seen = CancellationToken.None;
        var function = Function((s, ct) =>
        {
            seen = ct;
            _clock.Advance(TimeSpan.FromMilliseconds(249));
            return s + "!";
        });
        using var pass = Pass();

        function.Invoke(["x"]).ShouldBe("x!");
        seen.CanBeCanceled.ShouldBeTrue("the pass hands a real, cancellable token");
        seen.IsCancellationRequested.ShouldBeFalse("one millisecond before the limit is still inside it");
    }

    [Fact]
    public void Exactly_the_limit_is_an_overrun_and_the_token_has_fired()
    {
        var seen = CancellationToken.None;
        var function = Function((s, ct) =>
        {
            seen = ct;
            _clock.Advance(TimeSpan.FromMilliseconds(250));
            return s;
        });
        using var pass = Pass();

        Should.Throw<CelFunctionException>(() => function.Invoke(["x"])).Reason.ShouldStartWith("it ran past");
        seen.IsCancellationRequested.ShouldBeTrue("the token and the measurement share one boundary");
    }

    [Fact]
    public void A_function_that_blocks_with_result_past_the_budget_is_an_overrun()
    {
        var function = Function((s, ct) =>
        {
            _clock.Advance(TimeSpan.FromMilliseconds(300));
            return Task.Run(() => s, ct).Result;
        });
        using var pass = Pass();

        Should.Throw<CelFunctionException>(() => function.Invoke(["x"])).Reason.ShouldStartWith("it ran past");
    }

    [Fact]
    public void A_function_that_turns_its_cancellation_into_its_own_exception_is_still_an_overrun()
    {
        var function = Function((s, ct) =>
        {
            _clock.Advance(TimeSpan.FromMilliseconds(300));
            return ct.IsCancellationRequested ? throw new TimeoutException("rates service") : s;
        });
        using var pass = Pass();

        Should.Throw<CelFunctionException>(() => function.Invoke(["x"])).Reason.ShouldStartWith("it ran past");
    }

    [Fact]
    public void Two_calls_in_one_pass_share_one_deadline()
    {
        var function = TokenFree(s =>
        {
            _clock.Advance(TimeSpan.FromMilliseconds(150));
            return s;
        });
        using var pass = Pass();

        function.Invoke(["first"]).ShouldBe("first");
        Should.Throw<CelFunctionException>(() => function.Invoke(["second"])).Reason.ShouldStartWith("it ran past");
    }

    [Fact]
    public void A_new_pass_starts_a_new_budget()
    {
        var function = TokenFree(s =>
        {
            _clock.Advance(TimeSpan.FromMilliseconds(150));
            return s;
        });

        using (Pass())
        {
            function.Invoke(["first"]).ShouldBe("first");
        }

        using (Pass())
        {
            function.Invoke(["second"]).ShouldBe("second");
        }
    }

    [Fact]
    public void The_clock_starts_at_the_first_host_call_not_when_the_pass_opens()
    {
        var function = TokenFree(s =>
        {
            _clock.Advance(TimeSpan.FromMilliseconds(100));
            return s;
        });
        using var pass = Pass();
        _clock.Advance(TimeSpan.FromSeconds(10));

        function.Invoke(["x"]).ShouldBe("x");
    }

    [Fact]
    public void Outside_a_pass_a_function_gets_no_token_and_no_limit()
    {
        var seen = new CancellationTokenSource().Token;
        var function = Function((s, ct) =>
        {
            seen = ct;
            _clock.Advance(TimeSpan.FromSeconds(10));
            return s;
        });

        function.Invoke(["x"]).ShouldBe("x");
        seen.CanBeCanceled.ShouldBeFalse();
    }

    [Fact]
    public void An_outer_cancellation_reaches_the_token_and_is_the_function_s_failure_not_an_overrun()
    {
        using var outer = new CancellationTokenSource();
        var function = Function((s, ct) =>
        {
            outer.Cancel();
            ct.ThrowIfCancellationRequested();
            return s;
        });
        using var pass = Pass(outer.Token);

        var failure = Should.Throw<CelFunctionException>(() => function.Invoke(["x"]));

        failure.Reason.ShouldBeNull("a body that threw keeps no reason; the caller reads the generic sentence");
        failure.InnerException.ShouldBeAssignableTo<OperationCanceledException>();
    }

    [Fact]
    public void Leaving_a_pass_restores_the_one_around_it()
    {
        using var outer = Pass();
        using (Pass())
        {
            CelFunctionBudget.Current.ShouldNotBeSameAs(outer);
        }

        CelFunctionBudget.Current.ShouldBeSameAs(outer);
    }

    [Fact]
    public void The_overrun_reason_names_the_configured_limit()
    {
        var function = TokenFree(s =>
        {
            _clock.Advance(TimeSpan.FromMilliseconds(41));
            return s;
        });
        using var pass = TestBudgets.Default(_clock, TimeSpan.FromMilliseconds(40)).Enter();

        Should.Throw<CelFunctionException>(() => function.Invoke(["x"])).Reason
            .ShouldBe("it ran past the 40 ms that host functions have in one evaluation");
    }
}
```

`OnCallerData` does not exist until Task 3: in this task write that one assertion as `failure.IsHost.ShouldBeTrue();` only, and Task 3 Step 1 adds the `OnCallerData` line (noted there).

Append to `test/MMLib.Alvo.Tests/Expressions/CelFunctionRegistrationTests.cs`:

```csharp
    [Fact]
    public void A_trailing_cancellation_token_is_not_a_cel_parameter()
    {
        var function = HostCelFunction.Create("rate", (string currency, CancellationToken ct) => currency, summary: null);

        function.Parameters.Select(p => p.Name).ShouldBe(["currency"]);
        function.Signature().ShouldBe("rate(currency: String) -> String");
    }

    [Fact]
    public void Four_cel_parameters_and_a_token_are_accepted()
    {
        var function = HostCelFunction.Create(
            "four", (string a, long b, decimal c, bool d, CancellationToken ct) => a, summary: null);

        function.Parameters.Count.ShouldBe(4);
    }

    [Fact]
    public void A_token_that_is_not_the_last_parameter_is_refused()
    {
        var refused = Should.Throw<ArgumentException>(
            () => HostCelFunction.Create("early", (CancellationToken ct, string s) => s, summary: null));

        refused.ParamName.ShouldBe("function");
        refused.Message.ShouldContain("not its last parameter");
    }

    [Fact]
    public void Two_tokens_are_refused()
    {
        Should.Throw<ArgumentException>(
            () => HostCelFunction.Create("twice", (string s, CancellationToken a, CancellationToken b) => s, summary: null))
            .Message.ShouldContain("2 CancellationTokens");
    }
```

- [ ] **Step 3: Run the facts to verify they fail**

Run: `dotnet test --project test/MMLib.Alvo.Tests --filter-class '*CelFunctionBudgetTests' --filter-class '*CelFunctionRegistrationTests' --filter-class '*AlvoExtensionOptionsTests'`
Expected: compile errors — `CelFunctionBudget`, `CelFunctionBudgetPolicy`, `AlvoExtensionOptions.CelFunctionBudget` do not exist.

- [ ] **Step 4: Add the option and its validation**

In `src/MMLib.Alvo/Extensions/AlvoExtensionOptions.cs` add after `ExposeTypeNames`:

```csharp
    /// <summary>
    /// Gets or sets how long the host's CEL functions (<c>AddCelFunction</c>) may run in one evaluation — every call in a
    /// hook's condition and its <c>mutate</c> values shares it; default 250 ms, the same as a before-write hook, at most
    /// 30 seconds. A function that takes a trailing <see cref="CancellationToken"/> receives one that is cancelled when the
    /// time is up; every function, with a token or not, that returns after it fails the write as the function's failure.
    /// </summary>
    public TimeSpan CelFunctionBudget { get; set; } = AlvoHookOptions.DefaultTimeout;
```

Replace the `Validate` method of `AlvoExtensionOptionsConfiguration` and add the key and refusal:

```csharp
    internal const string CelFunctionBudgetKey = $"{AlvoExtensionOptions.SectionName}:{nameof(AlvoExtensionOptions.CelFunctionBudget)}";

    public ValidateOptionsResult Validate(string? name, AlvoExtensionOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        List<string> refusals = [];
        if (options.MaxDepth < MinimumMaxDepth)
        {
            refusals.Add(MaxDepthRefusal(options.MaxDepth));
        }

        if (options.CelFunctionBudget <= TimeSpan.Zero || options.CelFunctionBudget > AlvoHookOptions.MaxTimeout)
        {
            refusals.Add(BudgetRefusal(options.CelFunctionBudget));
        }

        return refusals.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(refusals);
    }

    private static string BudgetRefusal(TimeSpan configured) =>
        $"'{configured}' is not a usable {CelFunctionBudgetKey}. Set it (as an environment variable, "
        + $"{CelFunctionBudgetKey.Replace(":", "__", StringComparison.Ordinal)}) to more than zero and at most 30 s, for "
        + "example 00:00:00.250: host functions run inside the write's transaction, which must not wait on them longer.";
```

- [ ] **Step 5: Write the pass and the policy**

Create `src/MMLib.Alvo/Expressions/Internal/CelFunctionBudget.cs`:

```csharp
using System.Globalization;

namespace MMLib.Alvo.Expressions.Internal;

/// <summary>
/// One evaluation pass's time for host functions: every call inside it shares one deadline, whose clock starts at the
/// first call. Ambient (<see cref="AsyncLocal{T}"/>), because the interpreter is static and a host function's body is a
/// delegate in the compiled tree; the three places that evaluate host-callable CEL open it.
/// </summary>
/// <remarks>
/// A token-aware function receives a token cancelled at the deadline (linked with the caller's); every function is
/// measured when it returns, because .NET cannot stop a thread that ignores its token and the write must still fail. One
/// boundary for both: reaching the limit is over it. Once the deadline has passed, whatever the body threw — an
/// <see cref="OperationCanceledException"/>, an <see cref="AggregateException"/> from <c>.Result</c>, its own exception —
/// is the overrun. A function that never returns holds the evaluation until it does.
/// </remarks>
internal sealed class CelFunctionBudget : IDisposable
{
    private static readonly AsyncLocal<CelFunctionBudget?> _current = new();

    private readonly CelFunctionBudget? _previous;
    private readonly TimeProvider _time;
    private readonly CancellationToken _outer;
    private CancellationTokenSource? _deadline;
    private CancellationTokenSource? _linked;
    private long _startedAt;

    private CelFunctionBudget(TimeSpan limit, TimeProvider time, CancellationToken outer)
    {
        Limit = limit;
        _time = time;
        _outer = outer;
        _previous = _current.Value;
    }

    /// <summary>Gets the pass the current evaluation runs in, or <see langword="null"/>.</summary>
    internal static CelFunctionBudget? Current => _current.Value;

    /// <summary>Gets the time every host call of this pass shares.</summary>
    internal TimeSpan Limit { get; }

    private bool Overran =>
        _deadline is not null && (_deadline.IsCancellationRequested || _time.GetElapsedTime(_startedAt) >= Limit);

    /// <summary>Opens a pass; dispose it to restore the one around it.</summary>
    /// <param name="limit">The time host calls share.</param>
    /// <param name="time">The host's clock.</param>
    /// <param name="outer">The caller's token, linked into the one a function receives.</param>
    /// <returns>The pass.</returns>
    internal static CelFunctionBudget Enter(TimeSpan limit, TimeProvider time, CancellationToken outer)
    {
        ArgumentNullException.ThrowIfNull(time);
        var pass = new CelFunctionBudget(limit, time, outer);
        _current.Value = pass;
        return pass;
    }

    /// <summary>Calls a host function's body under the current pass, if any.</summary>
    /// <param name="function">The function's CEL name, for the overrun's message.</param>
    /// <param name="tokenAware">Whether the body takes a trailing token.</param>
    /// <param name="arguments">The CEL arguments, already converted.</param>
    /// <param name="body">The body.</param>
    /// <returns>What the body returned.</returns>
    /// <exception cref="CelFunctionException">The body ran past the pass's deadline.</exception>
    internal static object? Call(string function, bool tokenAware, object?[] arguments, Func<object?[], object?> body)
    {
        ArgumentNullException.ThrowIfNull(body);
        var pass = _current.Value;
        var token = pass?.Start() ?? CancellationToken.None;
        object? result;
        try
        {
            result = body(tokenAware ? [.. arguments, token] : arguments);
        }
        catch (Exception) when (pass is { Overran: true })
        {
            throw pass.Overrun(function);
        }

        return pass is { Overran: true } ? throw pass.Overrun(function) : result;
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        _current.Value = _previous;
        _linked?.Dispose();
        _deadline?.Dispose();
    }

    private CancellationToken Start()
    {
        if (_linked is null)
        {
            _startedAt = _time.GetTimestamp();
            _deadline = new CancellationTokenSource(Limit, _time);
            _linked = CancellationTokenSource.CreateLinkedTokenSource(_outer, _deadline.Token);
        }

        return _linked.Token;
    }

    private CelFunctionException Overrun(string function) =>
        new(function, isHost: true,
            $"it ran past the {Limit.TotalMilliseconds.ToString("0", CultureInfo.InvariantCulture)} ms that host functions have in one evaluation");
}
```

Create `src/MMLib.Alvo/Expressions/Internal/CelFunctionBudgetPolicy.cs`:

```csharp
using Microsoft.Extensions.Options;
using MMLib.Alvo.Extensions;

namespace MMLib.Alvo.Expressions.Internal;

/// <summary>Opens host-function passes with the configured budget on the host's clock.</summary>
/// <param name="options">The extension options (<c>Alvo:Extensions:CelFunctionBudget</c>).</param>
/// <param name="time">The host's clock.</param>
internal sealed class CelFunctionBudgetPolicy(IOptions<AlvoExtensionOptions> options, TimeProvider time)
{
    /// <summary>Opens a pass; dispose it when the evaluation ends.</summary>
    /// <param name="outer">The caller's token, if the evaluation has one.</param>
    /// <returns>The pass.</returns>
    internal CelFunctionBudget Enter(CancellationToken outer = default) =>
        CelFunctionBudget.Enter(options.Value.CelFunctionBudget, time, outer);
}
```

Register it in `src/MMLib.Alvo/Expressions/Setup.cs`, before the catalog line:

```csharp
        services.TryAddSingleton(TimeProvider.System);
        services.TryAddSingleton<CelFunctionBudgetPolicy>();
```

- [ ] **Step 6: Make `HostCelFunction` token-aware and budgeted**

In `src/MMLib.Alvo/Expressions/Internal/HostCelFunction.cs`, replace `Create`'s body and `Parameters`, and add `TakesToken`:

```csharp
    internal static CelFunction Create(string name, Delegate function, string? summary)
    {
        EnsureValidName(name);
        ArgumentNullException.ThrowIfNull(function);
        var invoke = InvokeMethodOf(function, name);
        var (result, resultNullable) = Result(function, invoke, name);
        var tokenAware = TakesToken(invoke, name);

        return new CelFunction(
            name, Parameters(function, invoke, name, tokenAware), result, resultNullable, summary ?? string.Empty,
            IsHost: true, CelBuiltInFunctions.ConditionAndMutate,
            arguments => CelFunctionBudget.Call(
                name, tokenAware, arguments,
                values => invoke.Invoke(function, BindingFlags.DoNotWrapExceptions, binder: null, values, culture: null)));
    }

    private static bool TakesToken(MethodInfo invoke, string name)
    {
        var parameters = invoke.GetParameters();
        var tokens = parameters.Count(parameter => parameter.ParameterType == typeof(CancellationToken));
        return tokens switch
        {
            0 => false,
            1 when parameters[^1].ParameterType == typeof(CancellationToken) => true,
            1 => throw Refused(name, "takes a CancellationToken that is not its last parameter; put the token last"),
            _ => throw Refused(name, $"takes {tokens} CancellationTokens; a CEL function takes at most one, as its last parameter"),
        };
    }

    private static List<CelFunctionArgument> Parameters(Delegate function, MethodInfo invoke, string name, bool tokenAware)
    {
        var signature = invoke.GetParameters();
        var cel = tokenAware ? signature[..^1] : signature;
        if (cel.Length > MaxParameters)
        {
            throw Refused(name, $"takes {cel.Length} parameters; a CEL function takes at most {MaxParameters}");
        }

        var declared = DeclaredParameters(function, signature);
        return [.. cel.Select((parameter, index) => Parameter(parameter, declared?[index], name))];
    }
```

- [ ] **Step 7: Rewrite `AddCelFunction`'s docs**

In `src/MMLib.Alvo/AlvoBuilderExtensions.cs`, replace the paragraph starting "**The function is host code inside the write's transaction.**" with:

```xml
    /// <para>
    /// <b>The function is host code inside the write's transaction.</b> It must be synchronous, fast, thread-safe and
    /// free of side effects. Every call in one evaluation — a hook's condition, its <c>mutate</c> values — shares a time
    /// budget, <c>Alvo:Extensions:CelFunctionBudget</c> (250 ms by default). Give the function a trailing
    /// <see cref="CancellationToken"/> parameter — <c>(string vin, CancellationToken ct) =&gt; …</c>; it is not a CEL
    /// argument — and it receives a token cancelled when the budget is spent. A function that returns after the budget,
    /// with a token or without one, fails the write as a function failure, so a catch around the token buys nothing. The
    /// <c>ALVO1001</c> analyzer reports the network, a database connection, a file or a data port used inside it. It
    /// captures what it closes over for the host's lifetime (no DI scope).
    /// </para>
```

and in the first `<para>` change "Up to four parameters" to "Up to four parameters (plus an optional trailing `CancellationToken`)". In the failure paragraph replace "a Data API write is rolled back and answers HTTP 500 `…/errors/function-failed` naming the function" with "a Data API write is rolled back and answers HTTP 500 `…/errors/function-failed` naming the function (a built-in function that refuses the caller's data answers 422 `…/errors/expression-failed` instead)".

- [ ] **Step 8: Run the facts and accept the baseline**

Run: `dotnet test --project test/MMLib.Alvo.Tests --filter-class '*CelFunctionBudgetTests' --filter-class '*CelFunctionRegistrationTests' --filter-class '*AlvoExtensionOptionsTests' --filter-class '*CelDelegateShapeTests' --filter-class '*PublicApiApprovalTests'`
Expected: every budget, registration and option fact PASSES; `PublicApiApprovalTests` FAILS with a `.received.txt` adding exactly `public System.TimeSpan CelFunctionBudget { get; set; }` to `AlvoExtensionOptions`. Read the diff, move it over `PublicApi.MMLib.Alvo.verified.txt`, re-run (PASS); dispatch `alvo-snapshot-judge` for the grown baseline (Ruling 4 is the justification).

- [ ] **Step 9: Format, ring0, commit**

```bash
dotnet format whitespace MMLib.Alvo.slnx --include src/MMLib.Alvo/Expressions/Internal/CelFunctionBudget.cs src/MMLib.Alvo/Expressions/Internal/CelFunctionBudgetPolicy.cs src/MMLib.Alvo/Expressions/Internal/HostCelFunction.cs src/MMLib.Alvo/Expressions/Setup.cs src/MMLib.Alvo/Extensions/AlvoExtensionOptions.cs src/MMLib.Alvo/Extensions/Internal/AlvoExtensionOptionsConfiguration.cs src/MMLib.Alvo/AlvoBuilderExtensions.cs test/MMLib.Alvo.Tests/Expressions/CelFunctionBudgetTests.cs test/MMLib.Alvo.Tests/Expressions/TestBudgets.cs test/MMLib.Alvo.Tests/Expressions/CelFunctionRegistrationTests.cs test/MMLib.Alvo.Tests/Extensions/AlvoExtensionOptionsTests.cs
scripts/test-ring0
git branch --show-current   # hx/10-hook-guards
git add src/MMLib.Alvo/Expressions/Internal/CelFunctionBudget.cs src/MMLib.Alvo/Expressions/Internal/CelFunctionBudgetPolicy.cs src/MMLib.Alvo/Expressions/Internal/HostCelFunction.cs src/MMLib.Alvo/Expressions/Setup.cs src/MMLib.Alvo/Extensions/AlvoExtensionOptions.cs src/MMLib.Alvo/Extensions/Internal/AlvoExtensionOptionsConfiguration.cs src/MMLib.Alvo/AlvoBuilderExtensions.cs test/MMLib.Alvo.Tests/Expressions/CelFunctionBudgetTests.cs test/MMLib.Alvo.Tests/Expressions/TestBudgets.cs test/MMLib.Alvo.Tests/Expressions/CelFunctionRegistrationTests.cs test/MMLib.Alvo.Tests/Extensions/AlvoExtensionOptionsTests.cs test/MMLib.Alvo.Tests/PublicApi.MMLib.Alvo.verified.txt
git commit -m "feat(cel): bound host functions by a per-evaluation budget and accept a trailing CancellationToken

Claude-Session: https://claude.ai/code/session_01NJAtafM29iddLwfhb5gnmB"
```

---

## Task 2: Budget passes at every evaluation site **[slice end]**

**Files:**
- Modify: `src/MMLib.Alvo/Rules/Internal/BeforeHookRunner.cs`, `src/MMLib.Alvo/Hooks/Internal/WriteExtensionRunner.cs`, `src/MMLib.Alvo/Expressions/Internal/PredicateEvaluator.cs`, `src/MMLib.Alvo.Abstractions/Rules/IBeforeHookRunner.cs` (remarks only)
- Test: `test/MMLib.Alvo.Tests/Rules/BeforeHookFacetTests.cs`, `test/MMLib.Alvo.Tests/Rules/BeforeHookCompilerTests.cs`, `test/MMLib.Alvo.Tests/Rules/BeforeHookIsolationArchitectureTests.cs`, `test/MMLib.Alvo.Tests/Hooks/WriteExtensionRunnerTests.cs`, `test/MMLib.Alvo.Tests/Expressions/PredicateEvaluatorBudgetTests.cs`, `test/MMLib.Alvo.Tests/Expressions/CelEvaluationSitesTests.cs`

**Interfaces:**
- Consumes: `CelFunctionBudgetPolicy.Enter(CancellationToken)`, `TestBudgets.Default(...)` (Task 1).
- Produces: `public BeforeHookRunner(IPolicyCatalogProvider catalog, CelFunctionBudgetPolicy budget)`; `WriteExtensionRunner(..., TimeProvider time, CelFunctionBudgetPolicy budget, ILogger<WriteExtensionRunner> logger)` (primary constructor, `budget` inserted before `logger`); `PredicateEvaluator(CelFunctionBudgetPolicy? budget = null)`.

- [ ] **Step 1: Write the failing site facts**

In `test/MMLib.Alvo.Tests/Rules/BeforeHookFacetTests.cs`, give the private `Run` helper two optional parameters and use them (the existing call sites keep compiling):

```csharp
    private static IReadOnlyDictionary<string, object?> Run(
        EntityHooks hooks, AlvoRecord candidate, DataOperation operation = DataOperation.Create,
        CelCompiler? compiler = null, CelFunctionBudgetPolicy? budget = null)
    {
        var provider = new PolicyCatalogProvider();
        provider.SetCurrent(Contacts, PolicyCatalog.Build(Descriptor(hooks), Schema, compiler ?? Compiler));

        return new BeforeHookRunner(provider, budget ?? TestBudgets.Default()).Run(
            Contacts, operation, candidate, operation == DataOperation.Create ? null : candidate, _caller, DateTimeOffset.UnixEpoch);
    }
```

and append (the class already declares `Mutate`, `Cel`, `BeforeCreate`, `Row`; `code` holds at most 5 characters):

```csharp
    [Fact]
    public void A_before_hook_s_host_functions_share_one_budget_and_an_overrun_refuses_the_write()
    {
        var clock = new FakeTimeProvider();
        var slow = HostCelFunction.Create("slow", (string s, CancellationToken ct) =>
        {
            clock.Advance(TimeSpan.FromMilliseconds(150));
            ct.ThrowIfCancellationRequested();
            return "ok";
        }, summary: null);
        var hooks = BeforeCreate(Mutate("code", Cel("slow(new.note)")), Mutate("title", Cel("slow(new.note)")));

        var failure = Should.Throw<CelFunctionException>(
            () => Run(hooks, Row(("note", "x")), compiler: TestCelFunctions.Compiler(slow), budget: TestBudgets.Default(clock)));

        failure.FunctionName.ShouldBe("slow");
        failure.Reason.ShouldStartWith("it ran past the 250 ms");
    }

    [Fact]
    public void Each_write_gets_a_fresh_budget()
    {
        var clock = new FakeTimeProvider();
        var slow = HostCelFunction.Create("slow", (string s) =>
        {
            clock.Advance(TimeSpan.FromMilliseconds(200));
            return "ok";
        }, summary: null);
        var hooks = BeforeCreate(Mutate("code", Cel("slow(new.note)")));

        Run(hooks, Row(("note", "x")), compiler: TestCelFunctions.Compiler(slow), budget: TestBudgets.Default(clock))["code"].ShouldBe("ok");
        Run(hooks, Row(("note", "y")), compiler: TestCelFunctions.Compiler(slow), budget: TestBudgets.Default(clock))["code"].ShouldBe("ok");
    }
```

(`BeforeCreate` takes `params BeforeHookAction[]` — if it takes one action, build the two-hook block the way the class's other two-hook facts do; add `using Microsoft.Extensions.Time.Testing;`, `using MMLib.Alvo.Expressions.Internal;`.) In `BeforeHookCompilerTests.cs` change `new BeforeHookRunner(provider)` to `new BeforeHookRunner(provider, TestBudgets.Default())`.

Append to `test/MMLib.Alvo.Tests/Hooks/WriteExtensionRunnerTests.cs` (`RunnerWorld.Start(configure, time)` builds the runner through DI; its descriptor's `tickets` entity has the `status` field `RunnerWorld.Ticket(status)` sets):

```csharp
    [Fact]
    public async Task A_condition_whose_host_function_overruns_its_budget_fails_the_hook_with_reason_exception()
    {
        var clock = new FakeTimeProvider();
        await using var world = RunnerWorld.Start(alvo => alvo
            .AddCelFunction("slowStatus", (string status, CancellationToken ct) =>
            {
                clock.Advance(TimeSpan.FromMilliseconds(300));
                ct.ThrowIfCancellationRequested();
                return status;
            })
            .Entity("tickets", e => e.BeforeWrite("guard", (BeforeWrite<AlvoRecord> _) => { }, when: "slowStatus(new.status) == 'open'")), clock);

        var failure = await Should.ThrowAsync<HookFailedException>(() => world.Runner.BeforeWriteAsync(world.Before(RunnerWorld.Ticket("open")), Ct));

        failure.Extension.ShouldBe("guard");
        failure.Reason.ShouldBe(HookFailedException.Exception);
    }
```

(If `RunnerWorld.Start` applies its descriptor in its own `configure`, chain the two registrations onto the builder it passes, as the class's other `Start` calls do.)

Create `test/MMLib.Alvo.Tests/Expressions/PredicateEvaluatorBudgetTests.cs`:

```csharp
using Microsoft.Extensions.Time.Testing;
using MMLib.Alvo.Data;
using MMLib.Alvo.Expressions;
using MMLib.Alvo.Expressions.Internal;

namespace MMLib.Alvo.Tests.Expressions;

/// <summary>After-hook and handler conditions are evaluated post-commit by the evaluator, which opens a pass for them too.</summary>
public sealed class PredicateEvaluatorBudgetTests
{
    private static readonly AlvoContext _caller = new() { Roles = new HashSet<Role> { Role.Authenticated } };

    [Fact]
    public void A_condition_s_host_function_runs_under_the_budget()
    {
        var clock = new FakeTimeProvider();
        var slow = HostCelFunction.Create("slow", (string s) =>
        {
            clock.Advance(TimeSpan.FromMilliseconds(251));
            return s;
        }, summary: null);
        var condition = TestCelFunctions.Compile("slow('x') == 'x'", CelProfile.Condition, slow);

        Should.Throw<CelFunctionException>(
            () => new PredicateEvaluator(TestBudgets.Default(clock)).Evaluate(condition, AlvoRecord.Empty, null, _caller))
            .Reason.ShouldStartWith("it ran past");
    }

    [Fact]
    public void A_condition_inside_its_budget_answers()
    {
        var slow = HostCelFunction.Create("slow", (string s) => s, summary: null);
        var condition = TestCelFunctions.Compile("slow('x') == 'x'", CelProfile.Condition, slow);

        new PredicateEvaluator(TestBudgets.Default(new FakeTimeProvider())).Evaluate(condition, AlvoRecord.Empty, null, _caller).ShouldBeTrue();
    }
}
```

Create `test/MMLib.Alvo.Tests/Expressions/CelEvaluationSitesTests.cs`:

```csharp
namespace MMLib.Alvo.Tests.Expressions;

/// <summary>
/// The interpreter's hook entry points are called from exactly the three places that open a host-function budget pass, so
/// a fourth caller — which would run host functions with no limit — is a deliberate, reviewed change.
/// </summary>
public sealed class CelEvaluationSitesTests
{
    private static readonly string[] _entryPoints =
        ["CelInterpreter.EvaluatePredicate(", "CelInterpreter.EvaluateCondition(", "CelInterpreter.EvaluateMutation("];

    [Fact]
    public void Only_the_budgeted_sites_call_the_interpreter_s_hook_entry_points()
    {
        var core = Path.Combine(RepositoryRoot.Find(), "src", "MMLib.Alvo");

        var callers = Directory.EnumerateFiles(core, "*.cs", SearchOption.AllDirectories)
            .Where(file => File.ReadLines(file).Any(line => !line.TrimStart().StartsWith("///", StringComparison.Ordinal)
                && _entryPoints.Any(entry => line.Contains(entry, StringComparison.Ordinal))))
            .Select(file => Path.GetRelativePath(core, file).Replace('\\', '/'))
            .Where(file => file != "Expressions/Internal/CelInterpreter.cs")
            .Order(StringComparer.Ordinal);

        callers.ShouldBe(["Expressions/Internal/PredicateEvaluator.cs", "Hooks/Internal/WriteExtensionRunner.cs", "Rules/Internal/BeforeHookRunner.cs"]);
    }
}
```

In `BeforeHookIsolationArchitectureTests.cs`: the first fact's expected list becomes `[typeof(IPolicyCatalogProvider), typeof(CelFunctionBudgetPolicy)]` (rename it `A_host_function_reaches_a_hook_through_the_compiled_tree_and_the_budget_is_the_runner_s_only_other_dependency`), and the write-hook runner fact's list gains `typeof(CelFunctionBudgetPolicy)` before `typeof(ILogger<WriteExtensionRunner>)`.

- [ ] **Step 2: Run them to verify they fail**

Run: `dotnet test --project test/MMLib.Alvo.Tests --filter-class '*BeforeHookFacetTests' --filter-class '*PredicateEvaluatorBudgetTests' --filter-class '*CelEvaluationSitesTests' --filter-class '*BeforeHookIsolationArchitectureTests' --filter-class '*WriteExtensionRunnerTests'`
Expected: compile errors (constructors), then — once compiling — the budget facts FAIL (no pass is opened; the overrun is not raised).

- [ ] **Step 3: Open the passes**

`BeforeHookRunner`: add the field and the constructor parameter, and open a pass around the hooks (keep the remarks; add one sentence: "Every host function the hooks call shares one budget pass, opened here, so the port stays token-free."):

```csharp
    private readonly CelFunctionBudgetPolicy _budget;

    public BeforeHookRunner(IPolicyCatalogProvider catalog, CelFunctionBudgetPolicy budget)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        ArgumentNullException.ThrowIfNull(budget);
        _catalog = catalog;
        _budget = budget;
    }

    public IReadOnlyDictionary<string, object?> Run(
        string entity, DataOperation operation, AlvoRecord candidate, AlvoRecord? previous, AlvoContext context, DateTimeOffset now)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(entity);
        ArgumentNullException.ThrowIfNull(candidate);
        ArgumentNullException.ThrowIfNull(context);

        var hooks = Declared(entity, operation);
        if (hooks.Count == 0)
        {
            return _noPatch;
        }

        using var pass = _budget.Enter();
        return Applied(hooks, candidate, previous, context, now);
    }
```

`WriteExtensionRunner`: add `CelFunctionBudgetPolicy budget` to the primary constructor (after `TimeProvider time`), give `Fires` a `CancellationToken cancellationToken` parameter, pass it at its three call sites (`ValidateAsync`, `BeforeWriteAsync`, `AfterWriteAsync` — each has `cancellationToken` in scope), and open the pass around the evaluation:

```csharp
        try
        {
            using var pass = budget.Enter(cancellationToken);
            return CelInterpreter.EvaluatePredicate(condition, Current(invocation, stage), invocation.Old, invocation.Caller);
        }
        catch (Exception thrown) when (thrown is not CelFunctionException { IsHost: false })
        {
            throw FailBeforeRun(hook.Id, HookFailedException.Exception, thrown);
        }
```

`PredicateEvaluator`: a primary constructor `(CelFunctionBudgetPolicy? budget = null)` and a pass only for `Condition`-profile expressions:

```csharp
internal sealed class PredicateEvaluator(CelFunctionBudgetPolicy? budget = null) : IPredicateEvaluator, IChangeListPredicateEvaluator
{
    public bool Evaluate(CompiledExpression expression, AlvoRecord current, AlvoRecord? previous, AlvoContext context)
    {
        using var pass = PassFor(expression);
        return CelInterpreter.EvaluatePredicate(expression, current, previous, context);
    }

    public bool Evaluate(
        CompiledExpression expression, AlvoRecord current, AlvoRecord? previous, IReadOnlyCollection<string> changed, AlvoContext context)
    {
        using var pass = PassFor(expression);
        return CelInterpreter.EvaluateCondition(expression, current, previous, context, changed);
    }

    private CelFunctionBudget? PassFor(CompiledExpression expression) =>
        budget is not null && expression?.Profile == CelProfile.Condition ? budget.Enter() : null;
}
```

Keep the existing XML docs; add to the class summary: "A `Condition` expression — the only profile besides `Mutate` that may call a host function — is evaluated in a host-function budget pass; a `Rule` or `Access` expression opens none, so the rule path pays nothing."

`IBeforeHookRunner` remarks: replace the last two sentences of the "What bounds the time a hook may spend" paragraph ("**A host function is the one exception and the bound does not extend to it:** … Alvo offers no timeout around it.") with: "**A host function is the one exception, and it has a budget of its own:** its cost may depend on the caller's input and it may block, so the implementation runs every host call of one `Run` under one time budget (`Alvo:Extensions:CelFunctionBudget`) — handing a token-aware function a token it cancels, and failing the write when any function returns late. The budget lives in the implementation, so this signature stays synchronous and token-free."

- [ ] **Step 4: Run them to verify they pass**

Run the Step 2 command. Expected: PASS, including the unchanged facts of each class.

- [ ] **Step 5: Format, ring0, ring1, commit**

```bash
dotnet format whitespace MMLib.Alvo.slnx --include src/MMLib.Alvo/Rules/Internal/BeforeHookRunner.cs src/MMLib.Alvo/Hooks/Internal/WriteExtensionRunner.cs src/MMLib.Alvo/Expressions/Internal/PredicateEvaluator.cs src/MMLib.Alvo.Abstractions/Rules/IBeforeHookRunner.cs test/MMLib.Alvo.Tests/Rules/BeforeHookFacetTests.cs test/MMLib.Alvo.Tests/Rules/BeforeHookCompilerTests.cs test/MMLib.Alvo.Tests/Rules/BeforeHookIsolationArchitectureTests.cs test/MMLib.Alvo.Tests/Hooks/WriteExtensionRunnerTests.cs test/MMLib.Alvo.Tests/Expressions/PredicateEvaluatorBudgetTests.cs test/MMLib.Alvo.Tests/Expressions/CelEvaluationSitesTests.cs
scripts/test-ring0 && scripts/test-ring1
git branch --show-current
git add src/MMLib.Alvo/Rules/Internal/BeforeHookRunner.cs src/MMLib.Alvo/Hooks/Internal/WriteExtensionRunner.cs src/MMLib.Alvo/Expressions/Internal/PredicateEvaluator.cs src/MMLib.Alvo.Abstractions/Rules/IBeforeHookRunner.cs test/MMLib.Alvo.Tests/Rules/BeforeHookFacetTests.cs test/MMLib.Alvo.Tests/Rules/BeforeHookCompilerTests.cs test/MMLib.Alvo.Tests/Rules/BeforeHookIsolationArchitectureTests.cs test/MMLib.Alvo.Tests/Hooks/WriteExtensionRunnerTests.cs test/MMLib.Alvo.Tests/Expressions/PredicateEvaluatorBudgetTests.cs test/MMLib.Alvo.Tests/Expressions/CelEvaluationSitesTests.cs
git commit -m "feat(hooks): open a host-function budget pass in before-hooks, write-hook conditions and fan-out conditions

Claude-Session: https://claude.ai/code/session_01NJAtafM29iddLwfhb5gnmB"
```

If any other test constructs `BeforeHookRunner`, `WriteExtensionRunner` or `PredicateEvaluator` with positional arguments that no longer fit (`git grep -n "new BeforeHookRunner(\|new WriteExtensionRunner(" -- test`), pass `TestBudgets.Default()` and stage that file too.

---

## Task 3: E1 — `expression-failed` (422) for a refusal of the caller's data

**Files:**
- Create: `src/MMLib.Alvo/Expressions/Internal/CelDataOrigin.cs`
- Modify: `src/MMLib.Alvo/Expressions/Internal/CelFunctionException.cs`, `src/MMLib.Alvo/Expressions/Internal/CelInterpreter.cs`, `src/MMLib.Alvo/Rules/Internal/BeforeHookRunner.cs`, `src/MMLib.Alvo/Hooks/Internal/WriteExtensionRunner.cs`, `src/MMLib.Alvo/Api/AlvoProblemTypes.cs`, `src/MMLib.Alvo/Api/Internal/ProblemResultFactory.cs`, `src/MMLib.Alvo/Api/Internal/AlvoExceptionHandler.cs`, `src/MMLib.Alvo/Api/Internal/DataApiDocumentation.cs`, `website/src/data/problem-types.json`, `docs/design/f5-admin/generated/schema-facets.js` (regenerated)
- Test: `test/MMLib.Alvo.Tests/Expressions/CelFailureOriginTests.cs`, `test/MMLib.Alvo.Tests/Rules/BeforeHookFacetTests.cs`, `test/MMLib.Alvo.Tests/Hooks/WriteExtensionRunnerTests.cs`, `test/MMLib.Alvo.Tests/Expressions/CelFunctionBudgetTests.cs`, `test/MMLib.Alvo.Api.Tests/ExpressionFailedAnswerTests.cs`, `test/MMLib.Alvo.Api.Tests/CelBuiltInWriteTests.cs`, `test/MMLib.Alvo.Api.Tests/ProblemDetailsTests.cs`, `test/MMLib.Alvo.Api.Tests/AlvoExceptionHandlerTests.cs`, `test/MMLib.Alvo.Api.Tests/OpenApiDocumentTests.The_document_is_stable.verified.txt`, `test/MMLib.Alvo.Tests/PublicApi.MMLib.Alvo.verified.txt`

**Interfaces:**
- Consumes: `AlvoExceptionHandler.FunctionFailure(Exception)` (wrapper walk), `ProblemResultFactory.Problem(int, string, string)`.
- Produces: `public bool CelFunctionException.OnCallerData { get; }` (on the internal type), `internal CelFunctionException CelFunctionException.JudgedBy(bool callersOwn)`, `internal CelFunctionException CelFunctionException.NotTheCallers()`; `internal static class CelDataOrigin { IsCallersOwn(CelNode), Reads(CelNode, IReadOnlySet<string>), Attributed<T>(CompiledExpression, IReadOnlySet<string>, Func<T>) }`; `public const string AlvoProblemTypes.ExpressionFailed = "expression-failed"`; `internal static IResult ProblemResultFactory.ExpressionFailed(string detail)`; `internal static CelFunctionException? AlvoExceptionHandler.CallerDataFailure(Exception)`; `internal static string AlvoExceptionHandler.ExpressionFailedDetail(CelFunctionException failure)`.

- [ ] **Step 1: Write the failing origin facts**

Add `failure.OnCallerData.ShouldBeFalse();` back to `CelFunctionBudgetTests.A_token_aware_function_receives_the_pass_s_token_and_its_cancellation_is_an_overrun` (Task 1 left it out). Create `test/MMLib.Alvo.Tests/Expressions/CelFailureOriginTests.cs`:

```csharp
using MMLib.Alvo.Data;
using MMLib.Alvo.Expressions;
using MMLib.Alvo.Expressions.Internal;

namespace MMLib.Alvo.Tests.Expressions;

/// <summary>
/// E1's line, decided by whose data was refused: a built-in or the interpreter refusing a value the caller sent is theirs
/// to fix (422); anything a host function did or produced, the stored row, a value a hook wrote, and Alvo's own defect are
/// not (500).
/// </summary>
public sealed class CelFailureOriginTests
{
    [Fact]
    public void A_built_in_s_refusal_is_on_the_caller_s_data() =>
        Should.Throw<CelFunctionException>(() => Builtin("int").Invoke(["x"])).OnCallerData.ShouldBeTrue();

    [Fact]
    public void Hook_arithmetic_is_on_the_caller_s_data() =>
        Should.Throw<CelFunctionException>(() => CelHookArithmetic.Apply(CelBinaryOperator.Divide, 1L, 0L)).OnCallerData.ShouldBeTrue();

    [Fact]
    public void A_whole_condition_with_no_bool_is_on_the_caller_s_data() =>
        new CelFunctionException(CelInterpreter.WholeCondition, CelInterpreter.WholeConditionReason).OnCallerData.ShouldBeTrue();

    [Fact]
    public void A_built_in_argument_that_does_not_fit_is_on_the_caller_s_data() =>
        new CelFunctionException("substring", isHost: false, "an argument does not fit parameter 'start' (Int32)").OnCallerData.ShouldBeTrue();

    [Fact]
    public void A_host_function_s_throw_is_not() =>
        Should.Throw<CelFunctionException>(() => HostCelFunction.Create("boom", (string s) => throw new FormatException(), summary: null).Invoke(["x"]))
            .OnCallerData.ShouldBeFalse();

    [Fact]
    public void A_host_function_s_argument_that_does_not_fit_is_not() =>
        new CelFunctionException("rate", isHost: true, "an argument does not fit parameter 'n' (Int32)").OnCallerData.ShouldBeFalse();

    [Fact]
    public void The_interpreter_s_defect_is_not() =>
        new CelFunctionException(CelInterpreter.HookEvaluation, CelInterpreter.HookEvaluationReason, new InvalidCastException()).OnCallerData.ShouldBeFalse();

    [Fact]
    public void A_built_in_body_that_threw_unexpectedly_is_not() =>
        new CelFunctionException("math.round", isHost: false, new ArgumentOutOfRangeException()).OnCallerData.ShouldBeFalse();

    [Fact]
    public void A_built_in_refusing_the_caller_s_own_value_is_the_caller_s()
    {
        var condition = TestCelFunctions.Compile("int(new.name) > 1", CelProfile.Condition);

        Should.Throw<CelFunctionException>(() => CelInterpreter.EvaluatePredicate(condition, Row("x"), null, _caller)).OnCallerData.ShouldBeTrue();
    }

    [Fact]
    public void A_built_in_refusing_a_host_function_s_output_is_not_the_caller_s()
    {
        var condition = TestCelFunctions.Compile("int(echo(new.name)) > 1", CelProfile.Condition, TestCelFunctions.Echo);

        Should.Throw<CelFunctionException>(() => CelInterpreter.EvaluatePredicate(condition, Row("x"), null, _caller)).OnCallerData.ShouldBeFalse();
    }

    [Fact]
    public void Arithmetic_over_a_host_function_s_output_is_not_the_caller_s()
    {
        var condition = TestCelFunctions.Compile("new.qty / half(new.price) > 1", CelProfile.Condition, TestCelFunctions.Half);
        var row = new AlvoRecord(new Dictionary<string, object?> { ["qty"] = 4L, ["price"] = 0m });

        Should.Throw<CelFunctionException>(() => CelInterpreter.EvaluatePredicate(condition, row, null, _caller)).OnCallerData.ShouldBeFalse();
    }

    [Fact]
    public void A_built_in_refusing_the_stored_row_is_not_the_caller_s()
    {
        var condition = TestCelFunctions.Compile("int(old.name) > 1", CelProfile.Condition);

        Should.Throw<CelFunctionException>(() => CelInterpreter.EvaluatePredicate(condition, Row("12"), Row("x"), _caller)).OnCallerData.ShouldBeFalse();
    }

    [Fact]
    public void An_inner_refusal_is_judged_by_its_own_subtree_not_by_a_host_call_beside_it()
    {
        var condition = TestCelFunctions.Compile("echo(new.name) == 'a' || int(new.name) > 1", CelProfile.Condition, TestCelFunctions.Echo);

        Should.Throw<CelFunctionException>(() => CelInterpreter.EvaluatePredicate(condition, Row("x"), null, _caller)).OnCallerData.ShouldBeTrue();
    }

    [Fact]
    public void A_value_an_earlier_hook_wrote_is_not_the_caller_s()
    {
        var condition = TestCelFunctions.Compile("int(new.name) > 1", CelProfile.Condition);

        Should.Throw<CelFunctionException>(() => CelDataOrigin.Attributed(
                condition, new HashSet<string>(StringComparer.Ordinal) { "name" },
                () => CelInterpreter.EvaluatePredicate(condition, Row("x"), null, _caller)))
            .OnCallerData.ShouldBeFalse();
    }

    [Fact]
    public void A_value_no_hook_wrote_stays_the_caller_s()
    {
        var condition = TestCelFunctions.Compile("int(new.name) > 1", CelProfile.Condition);

        Should.Throw<CelFunctionException>(() => CelDataOrigin.Attributed(
                condition, new HashSet<string>(StringComparer.Ordinal) { "qty" },
                () => CelInterpreter.EvaluatePredicate(condition, Row("x"), null, _caller)))
            .OnCallerData.ShouldBeTrue();
    }

    private static readonly AlvoContext _caller = new() { Roles = new HashSet<Role> { Role.Authenticated } };

    private static AlvoRecord Row(string name) => new(new Dictionary<string, object?> { ["name"] = name });

    private static CelFunction Builtin(string name) =>
        CelFunctionCatalog.BuiltIns.Overloads(name).First(function => function.Parameters.Count == 1 && function.Parameters[0].Type == CelValueType.String);
}
```


- [ ] **Step 2: Write the failing answer facts**

Create `test/MMLib.Alvo.Api.Tests/ExpressionFailedAnswerTests.cs`:

```csharp
using Microsoft.AspNetCore.Http;
using MMLib.Alvo.Api.Internal;
using MMLib.Alvo.Expressions.Internal;
using MMLib.Alvo.Rules;
using System.Text.Json.Nodes;

namespace MMLib.Alvo.Api.Tests;

/// <summary>
/// 422 <c>expression-failed</c> is a Data API refusal every host answers; a host's failure, a command's own write and an
/// aggregate holding any failure that is not the caller's never are.
/// </summary>
public sealed class ExpressionFailedAnswerTests
{
    private static readonly CelFunctionException _intOfText = new("int", "the text is not a whole number such as 42 or -7");

    [Fact]
    public async Task A_built_in_refusal_is_a_422_naming_the_function_and_its_reason()
    {
        var (status, document) = await WrittenAsync(ProblemResultFactory.RefusalFor(_intOfText).ShouldNotBeNull());

        status.ShouldBe(StatusCodes.Status422UnprocessableEntity);
        document["type"]!.GetValue<string>().ShouldBe("https://alvo.dev/errors/expression-failed");
        document["detail"]!.GetValue<string>().ShouldBe(
            "The CEL function 'int' refused this request's data: the text is not a whole number such as 42 or -7. Nothing was written.");
        document.ContainsKey("violations").ShouldBeFalse();
    }

    [Fact]
    public async Task A_wrapped_refusal_is_found_through_its_wrapper() =>
        (await WrittenAsync(ProblemResultFactory.RefusalFor(new AggregateException(new InvalidOperationException("w", _intOfText))).ShouldNotBeNull()))
            .Status.ShouldBe(StatusCodes.Status422UnprocessableEntity);

    [Fact]
    public void An_aggregate_that_also_holds_a_host_failure_is_not_422() =>
        ProblemResultFactory.RefusalFor(new AggregateException(_intOfText, new CelFunctionException("rate", isHost: true, new FormatException())))
            .ShouldBeNull();

    [Fact]
    public void A_command_s_own_write_is_never_422() =>
        ProblemResultFactory.PortRefusalFor(_intOfText).ShouldBeNull("PortRefusalFor is the command filter's mapping (Ruling 27)");

    [Fact]
    public async Task A_condition_with_no_bool_answers_its_own_sentence()
    {
        var failure = new CelFunctionException(CelInterpreter.WholeCondition, CelInterpreter.WholeConditionReason);

        (await WrittenAsync(ProblemResultFactory.RefusalFor(failure).ShouldNotBeNull())).Document["detail"]!.GetValue<string>().ShouldBe(
            "The hook's condition evaluated to a present value that is not a Bool, so nothing was written. Correct the values it reads.");
    }

    [Fact]
    public void A_hook_failure_and_a_host_function_failure_are_never_a_port_refusal()
    {
        ProblemResultFactory.RefusalFor(new HookFailedException("guard", HookFailedException.Exception)).ShouldBeNull();
        ProblemResultFactory.RefusalFor(new CelFunctionException("rate", isHost: true, new FormatException())).ShouldBeNull();
        ProblemResultFactory.RefusalFor(new CelFunctionException(CelInterpreter.HookEvaluation, CelInterpreter.HookEvaluationReason, new InvalidCastException()))
            .ShouldBeNull();
        ProblemResultFactory.RefusalFor(_intOfText.NotTheCallers()).ShouldBeNull("a refusal of a value a hook wrote is not the caller's");
    }

    private static Task<(int Status, JsonObject Document)> WrittenAsync(IResult result) =>
        ProblemDetailsTests.DocumentWrittenByAsync(result);
}
```

(`ProblemDetailsTests.DocumentWrittenByAsync(IResult)` already executes a factory result against a real `HttpContext` and parses what it wrote; change it from `private static` to `internal static`.)

In `test/MMLib.Alvo.Api.Tests/CelBuiltInWriteTests.cs` change the two built-in facts: `A_division_by_zero_in_a_condition_fails_the_write_closed` asserts `HttpStatusCode.UnprocessableEntity`, type ending `/errors/expression-failed`, detail `"The CEL function '_/_' refused this request's data: the divisor is zero. Nothing was written."`; `A_substring_past_the_end_of_the_data_fails_the_write_closed` asserts `UnprocessableEntity` and `/errors/expression-failed` (keep the "never the caller's data" assertion). Rename them `…_refuses_the_write_as_expression_failed`.

In `ProblemDetailsTests`: add `ProblemResultFactory.ExpressionFailed("The CEL function 'int' refused this request's data: x. Nothing was written."),` to `EveryFactoryResult()`, and to `Only_the_slugs_awaiting_a_later_task_are_unreachable_over_http` add `reached.Add(await ExpressionFailedSlugAnsweredByAnIntOfTextAsync());` with:

```csharp
    /// <summary>The <c>expression-failed</c> slug's probe: a built-in refusing the caller's own data, on a host with no opt-in.</summary>
    private static async Task<string> ExpressionFailedSlugAnsweredByAnIntOfTextAsync()
    {
        var writer = new TestApiKey("lots-writer", ["writer"], ["lots:read", "lots:write"]);
        await using var world = await AlvoApiWorld.FromDescriptorAsync("expression-failed.alvo.json", [writer]);

        return await SlugAnsweredByAsync(world, new Probe(HttpMethod.Post, "/api/lots", writer, new JsonObject { ["quantity_text"] = "x" }));
    }
```

and create `test/_shared/api/descriptors/expression-failed.alvo.json`:

```json
{
  "$schema": "https://alvo.dev/schema/v1/project.json",
  "apiVersion": "alvo.dev/v1",
  "name": "expression-failed",
  "description": "One entity whose before-hooks read a quantity a client sends as text with int(): a text that is no whole number is the caller's own data refusing a built-in, which answers 422 expression-failed on every engine and writes nothing.",
  "auth": { "providers": ["local"], "roles": ["writer"] },
  "access": { "viewer": "'writer' in @user.roles" },
  "entities": {
    "lots": {
      "description": "A stock lot whose quantity arrives as text.",
      "fields": {
        "code": { "type": "string", "maxLength": 20 },
        "quantity_text": { "type": "string", "maxLength": 20 }
      },
      "rules": { "list": "true", "get": "true", "create": "true", "update": "true", "delete": "true" },
      "hooks": {
        "beforeCreate": [
          { "condition": "int(new.quantity_text) > 9999", "action": { "reject": "A lot holds at most 9999 pieces." } }
        ],
        "beforeUpdate": [
          { "condition": "int(new.quantity_text) > 9999", "action": { "reject": "A lot holds at most 9999 pieces." } }
        ]
      }
    }
  }
}
```

In `AlvoExceptionHandlerTests`, every fact that feeds a built-in refusal (`new CelFunctionException("<built-in>", "<reason>")` or an interpreter `<condition>` failure) on a **Data API** endpoint (`DataApiOperationMetadata`) and asserts `function-failed` now asserts 422 `expression-failed` and that **no** Error record was logged; add `A_caller_data_refusal_on_a_command_endpoint_answers_function_failed` (the same failure on a `CommandEndpointMetadata` endpoint → 500 `function-failed`, logged — Ruling 27); the `<hook>` defect fact (line ~162) stays `function-failed`. Find them with `grep -n "CelFunctionException(" test/MMLib.Alvo.Api.Tests/AlvoExceptionHandlerTests.cs`.

Append to `BeforeHookFacetTests` (Ruling 26 (b)) — the first hook mutates `note`, the second's condition refuses it:

```csharp
    [Fact]
    public void A_later_hook_refusing_a_value_an_earlier_hook_wrote_is_not_the_caller_s()
    {
        var hooks = BeforeCreate(Mutate("note", Cel("'x'")), Reject("int(new.note) > 1", "Too many."));

        Should.Throw<CelFunctionException>(() => Run(hooks, Row(("note", "5")))).OnCallerData.ShouldBeFalse();
    }

    [Fact]
    public void A_hook_refusing_the_caller_s_own_value_stays_the_caller_s()
    {
        var hooks = BeforeCreate(Reject("int(new.note) > 1", "Too many."));

        Should.Throw<CelFunctionException>(() => Run(hooks, Row(("note", "x")))).OnCallerData.ShouldBeTrue();
    }
```

(`Reject(condition, message)` builds a conditional reject action the way the class's existing reject facts do; if the class has no such helper, add it beside `Mutate`.) Append to `WriteExtensionRunnerTests`: `A_built_in_failing_in_an_after_write_condition_is_not_the_caller_s` (an after-write hook `when: "int(new.status) > 1"` over a saved ticket with status `"open"` → the escaping `CelFunctionException` has `OnCallerData` false) and `A_built_in_failing_on_a_field_an_earlier_hook_set_is_not_the_caller_s` (hook A `Set("status", "x")`, hook B `when: "int(new.status) > 1"` → `OnCallerData` false); and keep HX-09's `A_condition_whose_built_in_function_fails_escapes_unchanged_and_poisons_nothing`, now also asserting `OnCallerData` true for a value the caller sent.

- [ ] **Step 3: Run them to verify they fail**

Run: `dotnet test --project test/MMLib.Alvo.Tests --filter-class '*CelFailureOriginTests' --filter-class '*CelFunctionBudgetTests' --filter-class '*BeforeHookFacetTests' --filter-class '*WriteExtensionRunnerTests'` and `dotnet test --project test/MMLib.Alvo.Api.Tests --filter-class '*ExpressionFailedAnswerTests' --filter-class '*CelBuiltInWriteTests' --filter-class '*ProblemDetailsTests' --filter-class '*AlvoExceptionHandlerTests'`
Expected: compile errors (`OnCallerData`, `ExpressionFailed`), then FAIL on status/type.

- [ ] **Step 4: Classify, catalogue, answer**

`CelFunctionException`: add the property and set it in the constructors — the `(functionName, isHost, Exception failure)` and `(functionName, reason, Exception failure)` constructors leave it `false`; the `(functionName, isHost, string reason)` constructor sets `OnCallerData = !isHost;` (the two-argument built-in constructor chains to it). Add the re-attribution (a copy, because an exception is immutable once thrown) — Ruling 26. Update the class summary's last sentences: "A Data API caller receives `…/errors/expression-failed` (422) when `OnCallerData` holds and `…/errors/function-failed` (500) otherwise."

```csharp
    private CelFunctionException(CelFunctionException original, bool onCallerData)
        : base(original.Message, original.InnerException)
    {
        FunctionName = original.FunctionName;
        IsHost = original.IsHost;
        Reason = original.Reason;
        OnCallerData = onCallerData;
        Judged = true;
    }

    /// <summary>
    /// Gets a value indicating whether the refused value is the caller's own — a built-in, hook arithmetic or the interpreter
    /// refused what the request carries — so the caller fixes it by sending other data (422). Never for anything a host
    /// function did or produced, the stored row, a value a hook wrote, nor a defect.
    /// </summary>
    public bool OnCallerData { get; }

    /// <summary>Gets a value indicating whether a node has already decided whose data this is.</summary>
    internal bool Judged { get; }

    /// <summary>The failure as the innermost node that saw it attributes it; an already judged failure is returned as is.</summary>
    /// <param name="callersOwn">Whether that node's subtree reads only the caller's data.</param>
    /// <returns>The attributed failure.</returns>
    internal CelFunctionException JudgedBy(bool callersOwn) => Judged || !OnCallerData ? this : new(this, callersOwn);

    /// <summary>The failure, attributed as not the caller's (a value a hook wrote).</summary>
    /// <returns>A copy with <see cref="OnCallerData"/> false.</returns>
    internal CelFunctionException NotTheCallers() => OnCallerData ? new(this, onCallerData: false) : this;
```

Create `src/MMLib.Alvo/Expressions/Internal/CelDataOrigin.cs`:

```csharp
namespace MMLib.Alvo.Expressions.Internal;

/// <summary>
/// Whose data a refusing node read (E1, Ruling 26): only the caller's when its subtree calls no host function and reads no
/// stored row. Decided on the failure path from the compiled tree, so a write that refuses nothing pays nothing.
/// </summary>
internal static class CelDataOrigin
{
    /// <summary>Whether <paramref name="node"/> reads only what the request carries.</summary>
    internal static bool IsCallersOwn(CelNode node) => node switch
    {
        CelCall { Function.IsHost: true } => false,
        CelFieldRef { State: CelRecordState.Old } => false,
        CelChanged => false,
        _ => CelTree.Children(node).All(IsCallersOwn),
    };

    /// <summary>Whether <paramref name="node"/> reads one of <paramref name="fields"/> from the row being written.</summary>
    internal static bool Reads(CelNode node, IReadOnlySet<string> fields) => node switch
    {
        CelFieldRef { State: not CelRecordState.Old } field => fields.Contains(field.FieldName),
        _ => CelTree.Children(node).Any(child => Reads(child, fields)),
    };

    /// <summary>
    /// Runs <paramref name="evaluate"/>; a refusal of the caller's data whose expression reads a field a hook wrote earlier
    /// in this write is re-attributed as not the caller's. Conservative: the whole expression is judged, not the node.
    /// </summary>
    internal static T Attributed<T>(CompiledExpression expression, IReadOnlySet<string> writtenByHooks, Func<T> evaluate)
    {
        ArgumentNullException.ThrowIfNull(expression);
        ArgumentNullException.ThrowIfNull(evaluate);
        try
        {
            return evaluate();
        }
        catch (CelFunctionException failure) when (failure.OnCallerData && writtenByHooks.Count > 0 && Reads(expression.Root, writtenByHooks))
        {
            throw failure.NotTheCallers();
        }
    }
}
```

`CelInterpreter`: rename the node `switch` method `Evaluate` to `EvaluateNode` and make `Evaluate` judge at the innermost node that sees an unjudged refusal; and judge the whole-condition refusal in `EvaluateVerdict` by the root:

```csharp
    private static object? Evaluate(CelNode node, in EvalState state)
    {
        try
        {
            return EvaluateNode(node, state);
        }
        catch (CelFunctionException failure) when (failure is { OnCallerData: true, Judged: false })
        {
            throw failure.JudgedBy(CelDataOrigin.IsCallersOwn(node));
        }
    }
```

and in `EvaluateVerdict`, around `Truth(Evaluate(expression.Root, state), WholeCondition, …)`, add `catch (CelFunctionException failure) when (failure is { OnCallerData: true, Judged: false }) { throw failure.JudgedBy(CelDataOrigin.IsCallersOwn(expression.Root)); }` before the existing defence-in-depth catch (which already lets `CelFunctionException` through). `EvaluateMutation` needs nothing more: its failures come from nodes.

`BeforeHookRunner.Applied`: evaluate each hook's condition and each mutation value through `CelDataOrigin.Attributed(expression, patch.Keys…, () => …)` with the fields earlier hooks of this run have mutated (`patch.Keys` as an ordinal `HashSet`, built once per hook iteration only when `patch.Count > 0`). `WriteExtensionRunner.Fires` takes the hook-written set: empty for validate; for before-write/before-delete, the mutate targets of the entity's declarative before-hooks for the operation (`catalog.TryGetEntity(entity, out var policy)` → `policy.BeforeHooks.For(op).SelectMany(h => h.Mutations).Select(m => m.Field)`, with `Create → Create`, `Update`/`Replace → Update`, `Delete → Delete`) plus the keys of the stage's host `patch` so far; for after-write and after-delete, every refusal is re-attributed (`failure.NotTheCallers()`), because the saved row carries defaults, rollups and hook writes. The existing `catch … when (thrown is not CelFunctionException { IsHost: false })` stays as it is.

`AlvoProblemTypes`: add after `HookFailed`, and append `ExpressionFailed` as the **last** element of `All`; reword `FunctionFailed`'s summary/remarks to "a host function (`AddCelFunction`) failed — it threw, ran past its time budget, or was handed an argument its parameter cannot hold — or Alvo's own evaluation of a hook failed. A built-in function refusing the caller's data is `ExpressionFailed` instead.":

```csharp
    /// <summary>A built-in CEL function or hook arithmetic refused the request's own data, so nothing was written (422).</summary>
    /// <remarks>
    /// <c>int('x')</c>, a <c>substring</c> past the end of the text, a <c>timestamp()</c> of a malformed value, an overflow, a
    /// division by zero, a condition that is no Bool: the caller fixes it by sending other data. The detail names the
    /// function and Alvo's reason, never the value. Answered on every host, without opting in. A host function's failure
    /// is <see cref="FunctionFailed"/> (500), because only the host can fix it.
    /// </remarks>
    public const string ExpressionFailed = "expression-failed";
```

`ProblemResultFactory`: add the factory and the arm (last but one, before `_ => null`):

```csharp
    /// <summary>The 422 for a built-in CEL function or hook arithmetic that refused the request's data.</summary>
    /// <param name="detail">Names the function and Alvo's reason, never the value.</param>
    /// <returns>The problem result.</returns>
    internal static IResult ExpressionFailed(string detail) =>
        Problem(StatusCodes.Status422UnprocessableEntity, AlvoProblemTypes.ExpressionFailed, detail);
```

`PortRefusalFor` stays as it is — it is also the command filter's mapping (Ruling 27). `RefusalFor` (the `GuardAsync` path of the generated routes) gains the arm before the malformed-request arm:

```csharp
    internal static IResult? RefusalFor(Exception exception) =>
        PortRefusalFor(exception)
        ?? (AlvoExceptionHandler.CallerDataFailure(exception) is { } refused
            ? ExpressionFailed(AlvoExceptionHandler.ExpressionFailedDetail(refused))
            : null)
        ?? <the existing malformed-request arm, unchanged>;
```

(keep the existing expression's shape; only the middle arm is new) and extend `RefusalFor`'s summary with "a built-in CEL function refusing the caller's own data (422 `expression-failed`, generated routes only),".

`AlvoExceptionHandler`: in `AnswerAsync`, inside the `FunctionFailure` branch, answer the caller's refusal before logging:

```csharp
        if (FunctionFailure(exception) is { } function)
        {
            await (IsDataApi(httpContext) && CallerDataFailure(exception) is { } refused
                ? ProblemResultFactory.ExpressionFailed(ExpressionFailedDetail(refused)).ExecuteAsync(httpContext)
                : AnswerFunctionFailureAsync(httpContext, function)).ConfigureAwait(false);
            return;
        }
```

with `IsDataApi` reading `IExceptionHandlerFeature.Endpoint` metadata for `DataApiOperationMetadata` (as `FailedInsideAlvo` does), and the aggregate rule (Ruling 28):

```csharp
    /// <summary>
    /// The caller's refusal in <paramref name="exception"/>, when every function failure in it (and in what wraps it) is
    /// the caller's; <see langword="null"/> when there is none, or when any is a host's or a defect.
    /// </summary>
    internal static CelFunctionException? CallerDataFailure(Exception exception)
    {
        var failures = FunctionFailures(exception, MaxWrapperDepth).ToList();
        return failures.Count > 0 && failures.All(failure => failure.OnCallerData) ? failures[0] : null;
    }

    private static IEnumerable<CelFunctionException> FunctionFailures(Exception? exception, int depth) => exception switch
    {
        null => [],
        CelFunctionException function => [function],
        _ when depth <= 0 => [],
        _ => Children(exception).SelectMany(child => FunctionFailures(child, depth - 1)),
    };
```

and add:

```csharp
    private const string NoBoolDetail =
        "The hook's condition evaluated to a present value that is not a Bool, so nothing was written. Correct the values it reads.";

    /// <summary>The caller-facing sentence for a refusal of the caller's data.</summary>
    /// <param name="failure">The refusal (<see cref="CelFunctionException.OnCallerData"/>).</param>
    /// <returns>The detail text; it names the function and Alvo's reason, never the value.</returns>
    internal static string ExpressionFailedDetail(CelFunctionException failure)
    {
        ArgumentNullException.ThrowIfNull(failure);

        return failure.FunctionName == CelInterpreter.WholeCondition
            ? NoBoolDetail
            : $"The CEL function '{failure.FunctionName}' refused this request's data: {failure.Reason}. Nothing was written.";
    }
```

`DataApiDocumentation.Malformed`: the description becomes "The request could not be acted on: a query string or body that is malformed, a body the entity's declared shape refuses, a header this API cannot honour, or data a hook's built-in CEL function refuses (`expression-failed`). The 'violations' array carries every reason at once — a pointer, a stable code and a fix suggestion each — never only the first, so one round trip is enough to repair the request." (`violations` stays described as present "on a refusal that has itemised reasons".)

`website/src/data/problem-types.json`: add, after `hook-failed` (key order as the other entries):

```json
  "expression-failed": {
    "causes": [
      "A built-in CEL function refused a value the write carries, while a hook's condition or a `mutate` evaluated it: `int('x')`, a `substring` past the end of the text, `timestamp()` of a malformed value.",
      "Hook arithmetic failed on the data: an overflow, a division by zero, or an operand that is not a number.",
      "A hook's whole condition evaluated to a present value that is not a Bool."
    ],
    "fix": "Correct the value the function in the detail reads. The detail names the function and why it refused, never the value; the same request fails the same way every time.",
    "violationCodes": [],
    "guides": ["guides/before-hooks", "guides/custom-cel-functions", "guides/handle-errors"],
    "needsProblemDetails": false
  },
```

and reword `function-failed`'s cause to "A host function (`AddCelFunction`) failed while a write was evaluated: it threw, ran past its time budget, or was handed an argument its parameter cannot hold — or Alvo's own evaluation of a hook failed. Nothing was written." (`ProblemTypeNotesTests.Only_the_opt_in_slugs_need_problem_details` keeps its list.)

- [ ] **Step 5: Run the facts and accept the baselines**

Run the Step 3 commands, then `dotnet test --project test/MMLib.Alvo.Api.Tests --filter-class '*OpenApiDocumentTests'`, `dotnet test --project test/MMLib.Alvo.Tests --filter-class '*PublicApiApprovalTests'` and `dotnet test --project test/MMLib.Alvo.DocsGen.Tests`.
Expected: the facts PASS; `OpenApiDocumentTests.The_document_is_stable` FAILS with a received file whose only differences are `"https://alvo.dev/errors/expression-failed"` appended to the `type` enum and the `unprocessable` description; `PublicApiApprovalTests` FAILS adding exactly `public const string ExpressionFailed = "expression-failed";`. Read each diff, move each over its verified file, re-run (PASS), dispatch `alvo-snapshot-judge`. Then `find test -name '*.received.*'` must print nothing; if another approval suite snapshots the document (`git grep -ln "errors/hook-failed" -- 'test/**/*.verified.*'`), run it and accept the same one-line enum change.

- [ ] **Step 6: Format, ring0, commit**

Regenerate the prototype's copy of the problem types (Ruling 41) and prove it current: `python3 scripts/gen-prototype-fixtures && python3 scripts/gen-prototype-fixtures --check` (expected: no "stale"); the only change in `docs/design/f5-admin/generated/schema-facets.js` is the new constant (and `HookFailed`, if this branch predates HX-09's own regeneration). Nothing in the prototype renders it, so `scripts/test-prototype` is not needed; if `git grep -n "hook-failed\|function-failed" docs/design/f5-admin/*.js` shows a screen that lists problem types, run it.

```bash
dotnet format whitespace MMLib.Alvo.slnx --include src/MMLib.Alvo/Expressions/Internal/CelDataOrigin.cs src/MMLib.Alvo/Expressions/Internal/CelInterpreter.cs src/MMLib.Alvo/Rules/Internal/BeforeHookRunner.cs src/MMLib.Alvo/Hooks/Internal/WriteExtensionRunner.cs test/MMLib.Alvo.Tests/Rules/BeforeHookFacetTests.cs test/MMLib.Alvo.Tests/Hooks/WriteExtensionRunnerTests.cs src/MMLib.Alvo/Expressions/Internal/CelFunctionException.cs src/MMLib.Alvo/Api/AlvoProblemTypes.cs src/MMLib.Alvo/Api/Internal/ProblemResultFactory.cs src/MMLib.Alvo/Api/Internal/AlvoExceptionHandler.cs src/MMLib.Alvo/Api/Internal/DataApiDocumentation.cs test/MMLib.Alvo.Tests/Expressions/CelFailureOriginTests.cs test/MMLib.Alvo.Tests/Expressions/CelFunctionBudgetTests.cs test/MMLib.Alvo.Api.Tests/ExpressionFailedAnswerTests.cs test/MMLib.Alvo.Api.Tests/CelBuiltInWriteTests.cs test/MMLib.Alvo.Api.Tests/ProblemDetailsTests.cs test/MMLib.Alvo.Api.Tests/AlvoExceptionHandlerTests.cs
scripts/test-ring0
git branch --show-current
git add src/MMLib.Alvo/Expressions/Internal/CelDataOrigin.cs src/MMLib.Alvo/Expressions/Internal/CelInterpreter.cs src/MMLib.Alvo/Rules/Internal/BeforeHookRunner.cs src/MMLib.Alvo/Hooks/Internal/WriteExtensionRunner.cs test/MMLib.Alvo.Tests/Rules/BeforeHookFacetTests.cs test/MMLib.Alvo.Tests/Hooks/WriteExtensionRunnerTests.cs docs/design/f5-admin/generated/schema-facets.js src/MMLib.Alvo/Expressions/Internal/CelFunctionException.cs src/MMLib.Alvo/Api/AlvoProblemTypes.cs src/MMLib.Alvo/Api/Internal/ProblemResultFactory.cs src/MMLib.Alvo/Api/Internal/AlvoExceptionHandler.cs src/MMLib.Alvo/Api/Internal/DataApiDocumentation.cs website/src/data/problem-types.json test/_shared/api/descriptors/expression-failed.alvo.json test/MMLib.Alvo.Tests/Expressions/CelFailureOriginTests.cs test/MMLib.Alvo.Tests/Expressions/CelFunctionBudgetTests.cs test/MMLib.Alvo.Api.Tests/ExpressionFailedAnswerTests.cs test/MMLib.Alvo.Api.Tests/CelBuiltInWriteTests.cs test/MMLib.Alvo.Api.Tests/ProblemDetailsTests.cs test/MMLib.Alvo.Api.Tests/AlvoExceptionHandlerTests.cs test/MMLib.Alvo.Api.Tests/OpenApiDocumentTests.The_document_is_stable.verified.txt test/MMLib.Alvo.Tests/PublicApi.MMLib.Alvo.verified.txt
git commit -m "feat(api): answer a built-in CEL refusal of the caller's data with 422 expression-failed

Claude-Session: https://claude.ai/code/session_01NJAtafM29iddLwfhb5gnmB"
```

(Stage any other accepted `*.verified.txt` by path.)

---

## Task 4: Both engines over HTTP — the budget and `expression-failed` **[slice end]**

**Files:**
- Create: `test/_shared/api/DataApiEngineTests.HookGuards.cs`, `test/_shared/api/descriptors/cel-budget.alvo.json`
- Test-only task (the behaviour landed in Tasks 1–3).

**Interfaces:**
- Consumes: `AlvoApiWorld.FromDescriptorAsync(string, IReadOnlyList<TestApiKey>, AlvoApiWorldSetup, AlvoApiEngine)`, `AlvoApiWorldSetup(ConfigureAlvo, ConfigureServicesAfterAlvo, MapAlvoProblemDetails)`, `world.CountRowsAsync(string)`, `ReadTextAsync`, `ReadJsonObjectAsync`, `DataApiEngineTests.Engine`; `expression-failed.alvo.json` (Task 3).
- Produces: acceptance facts (3) and (4) on SQLite and PostgreSQL.

- [ ] **Step 1: Write the descriptor and the facts**

Create `test/_shared/api/descriptors/cel-budget.alvo.json`:

```json
{
  "$schema": "https://alvo.dev/schema/v1/project.json",
  "apiVersion": "alvo.dev/v1",
  "name": "cel-budget",
  "description": "One entity whose before-hook mutate calls a host function the test registers: a function past the host-function time budget must roll the write back with function-failed on every engine.",
  "auth": { "providers": ["local"], "roles": ["writer"] },
  "access": { "viewer": "'writer' in @user.roles" },
  "entities": {
    "quotes": {
      "description": "A price quote stamped with an exchange rate a host function looks up.",
      "fields": {
        "currency": { "type": "string", "maxLength": 3 },
        "rate": { "type": "string", "maxLength": 20 }
      },
      "rules": { "list": "true", "get": "true", "create": "true", "update": "true", "delete": "true" },
      "hooks": {
        "beforeCreate": [
          { "action": { "mutate": { "rate": { "$cel": "rateOf(new.currency)" } } } }
        ]
      }
    }
  }
}
```

Create `test/_shared/api/DataApiEngineTests.HookGuards.cs`:

```csharp
using Microsoft.Extensions.DependencyInjection;
using MMLib.Alvo.Extensions;
using System.Net;
using System.Text.Json.Nodes;

namespace MMLib.Alvo.Api.Tests;

/// <summary>
/// HX-10 acceptance (3) and (4) on every engine: a host function past its time budget rolls the write back with 500
/// <c>function-failed</c>; a built-in refusing the caller's own data answers 422 <c>expression-failed</c> and writes nothing.
/// </summary>
/// <remarks>
/// The budget facts run on the real clock: the overrun facts give 200 ms against a 1 s sleep or a 10 s token wait, and the
/// inside-budget fact gives 2 s to a function that returns at once (Ruling 42) — a margin on both sides that a loaded CI
/// runner cannot cross. The rollback is read back through storage, which is what differs per engine.
/// </remarks>
public abstract partial class DataApiEngineTests
{
    private static readonly TestApiKey _quotesWriter = new("quotes-writer", ["writer"], ["quotes:read", "quotes:write"]);
    private static readonly TestApiKey _lotsWriter = new("lots-writer", ["writer"], ["lots:read", "lots:write"]);

    [Fact]
    public async Task A_token_aware_host_function_past_its_budget_is_function_failed_and_rolls_back_on_every_engine()
    {
        await using var world = await StartCelBudgetAsync(TightBudget, (string currency, CancellationToken ct) =>
        {
            ct.WaitHandle.WaitOne(TimeSpan.FromSeconds(10));
            ct.ThrowIfCancellationRequested();
            return "1.0";
        });

        using var refused = await world.SendAsync(HttpMethod.Post, "/api/quotes", _quotesWriter, body: new JsonObject { ["currency"] = "EUR" });

        await ShouldBeBudgetOverrunAsync(world, refused);
    }

    [Fact]
    public async Task A_token_free_host_function_past_the_budget_is_function_failed_and_rolls_back_on_every_engine()
    {
        await using var world = await StartCelBudgetAsync(TightBudget, (string currency) =>
        {
            Thread.Sleep(TimeSpan.FromSeconds(1));
            return "1.0";
        });

        using var refused = await world.SendAsync(HttpMethod.Post, "/api/quotes", _quotesWriter, body: new JsonObject { ["currency"] = "EUR" });

        await ShouldBeBudgetOverrunAsync(world, refused);
    }

    [Fact]
    public async Task A_host_function_that_catches_its_cancellation_and_returns_a_fallback_is_still_function_failed_on_every_engine()
    {
        await using var world = await StartCelBudgetAsync(TightBudget, (string currency, CancellationToken ct) =>
        {
            try
            {
                Task.Delay(TimeSpan.FromSeconds(10), ct).Wait(ct);
            }
            catch (OperationCanceledException)
            {
                return "fallback";
            }

            return "1.0";
        });

        using var refused = await world.SendAsync(HttpMethod.Post, "/api/quotes", _quotesWriter, body: new JsonObject { ["currency"] = "EUR" });

        await ShouldBeBudgetOverrunAsync(world, refused);
    }

    [Fact]
    public async Task A_host_function_inside_its_budget_stamps_the_row_on_every_engine()
    {
        await using var world = await StartCelBudgetAsync(TimeSpan.FromSeconds(2), (string currency, CancellationToken ct) => currency == "EUR" ? "1.0" : "0.5");

        using var created = await world.SendAsync(HttpMethod.Post, "/api/quotes", _quotesWriter, body: new JsonObject { ["currency"] = "EUR" });

        created.StatusCode.ShouldBe(HttpStatusCode.Created, await created.ReadTextAsync());
        (await created.ReadJsonObjectAsync())["rate"]!.GetValue<string>().ShouldBe("1.0");
    }

    [Fact]
    public async Task An_int_of_text_on_the_caller_s_data_is_422_expression_failed_and_writes_nothing_on_every_engine()
    {
        await using var world = await AlvoApiWorld.FromDescriptorAsync("expression-failed.alvo.json", [_lotsWriter], new AlvoApiWorldSetup(), Engine);

        using var refused = await world.SendAsync(HttpMethod.Post, "/api/lots", _lotsWriter, body: new JsonObject { ["code"] = "L1", ["quantity_text"] = "x" });

        refused.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity, await refused.ReadTextAsync());
        var problem = await refused.ReadJsonObjectAsync();
        problem["type"]!.GetValue<string>().ShouldBe("https://alvo.dev/errors/expression-failed");
        problem["detail"]!.GetValue<string>().ShouldBe(
            "The CEL function 'int' refused this request's data: the text is not a whole number such as 42 or -7. Nothing was written.");
        (await world.CountRowsAsync("lots")).ShouldBe(0);
    }

    [Fact]
    public async Task An_update_whose_data_a_built_in_refuses_is_422_and_leaves_the_row_on_every_engine()
    {
        await using var world = await AlvoApiWorld.FromDescriptorAsync("expression-failed.alvo.json", [_lotsWriter], new AlvoApiWorldSetup(), Engine);
        using var created = await world.SendAsync(HttpMethod.Post, "/api/lots", _lotsWriter, body: new JsonObject { ["code"] = "L1", ["quantity_text"] = "12" });
        created.StatusCode.ShouldBe(HttpStatusCode.Created, await created.ReadTextAsync());
        var id = (await created.ReadJsonObjectAsync())["id"]!.GetValue<Guid>();

        using var refused = await world.SendAsync(HttpMethod.Patch, $"/api/lots/{id}", _lotsWriter, body: new JsonObject { ["quantity_text"] = "twelve" });

        refused.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity, await refused.ReadTextAsync());
        using var read = await world.SendAsync(HttpMethod.Get, $"/api/lots/{id}", _lotsWriter);
        (await read.ReadJsonObjectAsync())["quantity_text"]!.GetValue<string>().ShouldBe("12");
    }

    private static readonly TimeSpan TightBudget = TimeSpan.FromMilliseconds(200);

    private Task<AlvoApiWorld> StartCelBudgetAsync(TimeSpan budget, Delegate rateOf) =>
        AlvoApiWorld.FromDescriptorAsync(
            "cel-budget.alvo.json",
            [_quotesWriter],
            new AlvoApiWorldSetup(
                MapAlvoProblemDetails: true,
                ConfigureAlvo: alvo => alvo.AddCelFunction("rateOf", rateOf),
                ConfigureServicesAfterAlvo: services => services.Configure<AlvoExtensionOptions>(o => o.CelFunctionBudget = budget)),
            Engine);

    private static async Task ShouldBeBudgetOverrunAsync(AlvoApiWorld world, HttpResponseMessage refused)
    {
        refused.StatusCode.ShouldBe(HttpStatusCode.InternalServerError, await refused.ReadTextAsync());
        var problem = await refused.ReadJsonObjectAsync();
        problem["type"]!.GetValue<string>().ShouldBe("https://alvo.dev/errors/function-failed");
        problem["detail"]!.GetValue<string>().ShouldBe(
            "The CEL function 'rateOf' failed: it ran past the 200 ms that host functions have in one evaluation. Nothing was written.");
        (await world.CountRowsAsync("quotes")).ShouldBe(0, "the write rolled back");
    }
}
```

(If `ConfigureAlvo` runs after the descriptor is read, the apply would refuse `rateOf` as unknown: it runs inside `AddAlvo`, as `AlvoApiWorld`'s setup shows — check `AlvoApiWorld.StartAsync` before running; if it does not, register the function in `ConfigureServices` through `services.AddAlvo(a => a.AddCelFunction(...))`, which composes with the world's own `AddAlvo`.)

- [ ] **Step 2: Run them on SQLite and PostgreSQL**

Run: `dotnet test --project test/MMLib.Alvo.Api.Tests --filter-class '*SqliteDataApiTests' --filter-method '*on_every_engine'` then `dotnet test --project test/MMLib.Alvo.Api.Tests.Integration --filter-class '*DataApiOnPostgresTests' --filter-method '*budget*' --filter-method '*expression_failed*' --filter-method '*built_in_refuses*' --filter-method '*cancellation*'` (Docker running).
Expected: PASS on both. A FAIL here is a defect in Tasks 1–3 (fix it in place and say which); these facts assert nothing new.

- [ ] **Step 3: ring0, ring1, commit**

```bash
dotnet format whitespace MMLib.Alvo.slnx --include test/_shared/api/DataApiEngineTests.HookGuards.cs
scripts/test-ring0 && scripts/test-ring1
git branch --show-current
git add test/_shared/api/DataApiEngineTests.HookGuards.cs test/_shared/api/descriptors/cel-budget.alvo.json
git commit -m "test(api): pin the host-function budget and expression-failed on SQLite and PostgreSQL

Claude-Session: https://claude.ai/code/session_01NJAtafM29iddLwfhb5gnmB"
```

---

## Task 5: The analyzer project — packed into `MMLib.Alvo`, in-transaction hook types

**Files:**
- Create: `src/MMLib.Alvo.Analyzers/{MMLib.Alvo.Analyzers.csproj,AnalyzerReleases.Shipped.md,AnalyzerReleases.Unshipped.md,Properties/AssemblyInfo.cs,AlvoDiagnostics.cs,AnalysisScope.cs,AlvoSymbols.cs,IoReach.cs,InTransactionIoAnalyzer.cs}`, `test/MMLib.Alvo.Analyzers.Tests/{MMLib.Alvo.Analyzers.Tests.csproj,AnalyzerHarness.cs,Alvo1001ScopeTests.cs}`
- Modify: `src/MMLib.Alvo/MMLib.Alvo.csproj`, `Directory.Packages.props`, `MMLib.Alvo.slnx`, `test/MMLib.Alvo.Conventions.Tests/SolutionConventionTests.cs`, `docs/architecture/package-boundary.md`, `CLAUDE.md`
- Baseline: `test/MMLib.Alvo.Analyzers.Tests/PublicApi.MMLib.Alvo.Analyzers.verified.txt` (generated)

**Interfaces:**
- Consumes: nothing from earlier tasks; matches the core's types by metadata name (the analyzer references no Alvo assembly).
- Produces: `public sealed class InTransactionIoAnalyzer : DiagnosticAnalyzer`; `internal static class AlvoDiagnostics { Alvo1001, HelpLink }`; `internal sealed class AnalysisScope { CelFunction, OfHookType(string, INamedTypeSymbol), HookDelegate(string), HookType, IsOwnCode(IMethodSymbol) }`; `internal sealed class AlvoSymbols { static From(Compilation), HookScopeOf(INamedTypeSymbol), RegistrationOf(IInvocationOperation, out IOperation?), IsTransactionCall(IMethodSymbol, AnalysisScope) }`; `internal static class IoReach { BannedTypes, NetworkNamespaces, IsReached(ITypeSymbol?), Describe(IOperation, AnalysisScope, AlvoSymbols) }`; test helper `AnalyzerHarness.Alvo1001Async(params string[] sources)`, `AnalyzerHarness.MarkedLines(string source)`, `AnalyzerHarness.Lines(IEnumerable<Diagnostic>)`.

This is the project the package rule earns, as E6 decided: a compiler loads an analyzer only from a `netstandard2.0` assembly of its own, so it cannot be a namespace of the `net10.0` core; it is **not** a package (Ruling 15).

- [ ] **Step 1: Write the failing convention fact**

In `SolutionConventionTests`, the `ProjectDescriptor` gains `bool IsRoslynComponent` (read like `IsPackable`: `ElementsNamed(document, "IsRoslynComponent")` equals `true`) and the inherited-property rule exempts exactly `TargetFramework=netstandard2.0` for such a project:

```csharp
    [Fact]
    public void A_roslyn_component_may_target_only_netstandard2_0()
    {
        var offenders = _projects
            .Where(project => project.IsRoslynComponent && project.TargetFramework != "netstandard2.0")
            .Select(project => project.RelativePath)
            .ToList();

        offenders.ShouldBeEmpty("A compiler loads an analyzer or generator only from netstandard2.0.");
    }
```

and in `No_project_redeclares_an_inherited_msbuild_property` compute the redeclared set as `project.RedeclaredInheritedProperties.Where(p => !(project.IsRoslynComponent && p == "TargetFramework"))`. Record `TargetFramework` (the element's trimmed value or `null`) in `ProjectDescriptor`. Run `dotnet test --project test/MMLib.Alvo.Conventions.Tests` — PASS now (no Roslyn component yet), and it is what keeps the new project honest.

- [ ] **Step 2: Create the projects**

```bash
dotnet new classlib -o src/MMLib.Alvo.Analyzers -n MMLib.Alvo.Analyzers --no-restore
rm src/MMLib.Alvo.Analyzers/Class1.cs
dotnet new console -o test/MMLib.Alvo.Analyzers.Tests -n MMLib.Alvo.Analyzers.Tests --no-restore
rm test/MMLib.Alvo.Analyzers.Tests/Program.cs
dotnet sln MMLib.Alvo.slnx add --solution-folder src src/MMLib.Alvo.Analyzers/MMLib.Alvo.Analyzers.csproj
dotnet sln MMLib.Alvo.slnx add --solution-folder test test/MMLib.Alvo.Analyzers.Tests/MMLib.Alvo.Analyzers.Tests.csproj
```

(Check `MMLib.Alvo.slnx` afterwards: each `<Project Path=…>` sits in its `/src/` or `/test/` folder, alphabetically, like its neighbours.) Replace the generated project files:

`src/MMLib.Alvo.Analyzers/MMLib.Alvo.Analyzers.csproj`:

```xml
<Project Sdk="Microsoft.NET.Sdk">

  <!--
    ALVO1001 (and, from HX-17, the typed-entity generator). Not a package: MMLib.Alvo packs this assembly at
    analyzers/dotnet/cs, so every project referencing MMLib.Alvo directly gets it (NuGet does not flow analyzers
    transitively) and its runtime closure gains nothing. netstandard2.0 is
    the only target a compiler loads an analyzer from (SolutionConventionTests exempts a Roslyn component from the
    inherited TargetFramework for exactly that value). Microsoft.CodeAnalysis.CSharp is pinned to the oldest compiler a
    supported .NET 10 SDK carries, so every SDK at or above global.json's loads it.
  -->
  <PropertyGroup>
    <TargetFramework>netstandard2.0</TargetFramework>
    <IsRoslynComponent>true</IsRoslynComponent>
    <EnforceExtendedAnalyzerRules>true</EnforceExtendedAnalyzerRules>
    <IsPackable>false</IsPackable>
  </PropertyGroup>

  <ItemGroup>
    <PackageReference Include="Microsoft.CodeAnalysis.CSharp" PrivateAssets="all" />
    <PackageReference Include="Microsoft.CodeAnalysis.Analyzers" PrivateAssets="all" />
  </ItemGroup>

  <ItemGroup>
    <AdditionalFiles Include="AnalyzerReleases.Shipped.md" />
    <AdditionalFiles Include="AnalyzerReleases.Unshipped.md" />
  </ItemGroup>

</Project>
```

`test/MMLib.Alvo.Analyzers.Tests/MMLib.Alvo.Analyzers.Tests.csproj`:

```xml
<Project Sdk="Microsoft.NET.Sdk">

  <ItemGroup>
    <ProjectReference Include="../../src/MMLib.Alvo.Analyzers/MMLib.Alvo.Analyzers.csproj" />
    <!-- The analyzer's subject: its tests compile consumer sources against the real core and abstractions. -->
    <ProjectReference Include="../../src/MMLib.Alvo/MMLib.Alvo.csproj" />
    <PackageReference Include="Microsoft.CodeAnalysis.CSharp" />
  </ItemGroup>

</Project>
```

`Directory.Packages.props` (in the alphabetical block of build-time packages):

```xml
    <!-- The analyzer's Roslyn floor: the oldest compiler a .NET 10 SDK carries loads an analyzer built against this.
         CentralPackageTransitivePinningEnabled makes it a transitive pin for EVERY project: a future dependency on a newer
         Roslyn (Workspaces, a generator test kit) must raise this floor deliberately — and re-run
         scripts/test-packed-analyzer — rather than fail restore with NU1109. -->
    <PackageVersion Include="Microsoft.CodeAnalysis.CSharp" Version="4.14.0" />
    <PackageVersion Include="Microsoft.CodeAnalysis.Analyzers" Version="3.11.0" />
```

`AnalyzerReleases.Shipped.md`:

```markdown
; Shipped analyzer releases
; https://github.com/dotnet/roslyn-analyzers/blob/main/src/Microsoft.CodeAnalysis.Analyzers/ReleaseTrackingAnalyzers.Help.md

```

`AnalyzerReleases.Unshipped.md`:

```markdown
; Unshipped analyzer release
; https://github.com/dotnet/roslyn-analyzers/blob/main/src/Microsoft.CodeAnalysis.Analyzers/ReleaseTrackingAnalyzers.Help.md

### New Rules

Rule ID | Category | Severity | Notes
--------|----------|----------|-------
ALVO1001 | Reliability | Warning | InTransactionIoAnalyzer
```

`Properties/AssemblyInfo.cs`: `[assembly: System.Runtime.CompilerServices.InternalsVisibleTo("MMLib.Alvo.Analyzers.Tests")]`. Add `[assembly: InternalsVisibleTo("MMLib.Alvo.Analyzers.Tests")]` to `src/MMLib.Alvo/Properties/AssemblyInfo.cs` too, with the one-line reason the file's other grants carry ("the analyzer's parity fact reads the boot ban's own list").

In `src/MMLib.Alvo/MMLib.Alvo.csproj`, add (after the `Corvus` item groups):

```xml
  <!--
    ALVO1001 ships inside this package (E6): built before the core, never referenced by it (no runtime reference, no
    nuspec dependency), and packed at analyzers/dotnet/cs so the compiler of every project referencing this package directly runs it (NuGet
    does not flow analyzers transitively). The inner build's
    TargetFramework=net10.0 is removed from the call, or it would ask the netstandard2.0 project for a net10.0 path.
  -->
  <ItemGroup>
    <ProjectReference Include="../MMLib.Alvo.Analyzers/MMLib.Alvo.Analyzers.csproj" ReferenceOutputAssembly="false" PrivateAssets="all" />
  </ItemGroup>

  <PropertyGroup>
    <TargetsForTfmSpecificContentInPackage>$(TargetsForTfmSpecificContentInPackage);PackAlvoAnalyzers</TargetsForTfmSpecificContentInPackage>
  </PropertyGroup>

  <Target Name="PackAlvoAnalyzers">
    <MSBuild Projects="../MMLib.Alvo.Analyzers/MMLib.Alvo.Analyzers.csproj" Targets="GetTargetPath" Properties="Configuration=$(Configuration)" RemoveProperties="TargetFramework">
      <Output TaskParameter="TargetOutputs" ItemName="AlvoAnalyzerAssembly" />
    </MSBuild>
    <ItemGroup>
      <TfmSpecificPackageFile Include="@(AlvoAnalyzerAssembly)" PackagePath="analyzers/dotnet/cs" />
    </ItemGroup>
  </Target>
```

- [ ] **Step 3: Write the harness and the failing hook-type facts**

`test/MMLib.Alvo.Analyzers.Tests/AnalyzerHarness.cs`:

```csharp
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Diagnostics;
using System.Collections.Immutable;

namespace MMLib.Alvo.Analyzers.Tests;

/// <summary>
/// Compiles consumer sources against the running process's own assemblies — the real core and abstractions among them —
/// and runs ALVO1001 over them. Offline and deterministic: no reference-assembly download.
/// </summary>
internal static class AnalyzerHarness
{
    internal const string Marker = "// ALVO1001";

    private static readonly Lazy<ImmutableArray<MetadataReference>> _all = new(() => References(withAlvo: true));
    private static readonly Lazy<ImmutableArray<MetadataReference>> _withoutAlvo = new(() => References(withAlvo: false));

    internal static Task<IReadOnlyList<Diagnostic>> Alvo1001Async(params string[] sources) => RunAsync(_all.Value, sources);

    internal static Task<IReadOnlyList<Diagnostic>> Alvo1001WithoutAlvoAsync(string source) => RunAsync(_withoutAlvo.Value, [source]);

    /// <summary>The 1-based lines of <paramref name="source"/> that carry the marker.</summary>
    internal static IReadOnlyList<int> MarkedLines(string source) =>
        [.. source.Split('\n').Select((line, index) => (line, index)).Where(l => l.line.Contains(Marker, StringComparison.Ordinal)).Select(l => l.index + 1)];

    internal static IReadOnlyList<int> Lines(IEnumerable<Diagnostic> diagnostics) =>
        [.. diagnostics.Select(d => d.Location.GetLineSpan().StartLinePosition.Line + 1).Order()];

    private static async Task<IReadOnlyList<Diagnostic>> RunAsync(ImmutableArray<MetadataReference> references, string[] sources)
    {
        var trees = sources.Select((source, index) => CSharpSyntaxTree.ParseText(
            source, new CSharpParseOptions(LanguageVersion.Latest), path: $"Consumer{index}.cs", cancellationToken: TestContext.Current.CancellationToken));
        var compilation = CSharpCompilation.Create(
            "Consumer", trees, references, new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, nullableContextOptions: NullableContextOptions.Enable));
        compilation.GetDiagnostics(TestContext.Current.CancellationToken).Where(d => d.Severity == DiagnosticSeverity.Error)
            .ShouldBeEmpty("the consumer source must compile, or the analyzer is judged on a broken tree");

        var diagnostics = await compilation
            .WithAnalyzers(ImmutableArray.Create<DiagnosticAnalyzer>(new InTransactionIoAnalyzer()))
            .GetAnalyzerDiagnosticsAsync(TestContext.Current.CancellationToken);
        return [.. diagnostics.Where(d => d.Id == "ALVO1001").OrderBy(d => d.Location.SourceTree?.FilePath, StringComparer.Ordinal).ThenBy(d => d.Location.SourceSpan.Start)];
    }

    private static ImmutableArray<MetadataReference> References(bool withAlvo) =>
    [
        .. ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!).Split(Path.PathSeparator)
            .Where(path => withAlvo || !Path.GetFileName(path).StartsWith("MMLib.Alvo", StringComparison.Ordinal))
            .Select(path => (MetadataReference)MetadataReference.CreateFromFile(path)),
    ];
}
```

`test/MMLib.Alvo.Analyzers.Tests/Alvo1001ScopeTests.cs`:

```csharp
namespace MMLib.Alvo.Analyzers.Tests;

/// <summary>Where ALVO1001 looks: in-transaction hook types (here), delegates and CEL functions (Task 6) — and not elsewhere.</summary>
public sealed class Alvo1001ScopeTests
{
    private const string Usings = """
        using System;
        using System.Net.Http;
        using System.Threading;
        using System.Threading.Tasks;
        using Microsoft.Extensions.DependencyInjection;
        using MMLib.Alvo;
        using MMLib.Alvo.Data;
        using MMLib.Alvo.Events;
        using MMLib.Alvo.Hooks;

        """;

    [Fact]
    public async Task An_http_client_in_a_before_write_hook_is_reported_once_at_its_use()
    {
        const string source = Usings + """
            public sealed class RateGuard(HttpClient http) : IBeforeWriteHook
            {
                public async ValueTask BeforeWriteAsync(BeforeWrite<AlvoRecord> write, CancellationToken cancellationToken)
                {
                    var rate = await http.GetStringAsync("https://rates.example/eur", cancellationToken); // ALVO1001
                    write.Set("rate", rate);
                }
            }
            """;

        var reported = await AnalyzerHarness.Alvo1001Async(source);

        AnalyzerHarness.Lines(reported).ShouldBe(AnalyzerHarness.MarkedLines(source));
        var message = reported[0].GetMessage(System.Globalization.CultureInfo.InvariantCulture);
        message.ShouldStartWith("'HttpClient' is used inside the before-write hook 'RateGuard'");
        message.ShouldContain("validate hook");
        reported[0].Severity.ShouldBe(Microsoft.CodeAnalysis.DiagnosticSeverity.Warning);
    }

    [Theory]
    [InlineData("IAfterWriteHook", "ValueTask AfterWriteAsync(AfterWrite<AlvoRecord> write, CancellationToken cancellationToken)", "after-write")]
    [InlineData("IBeforeDeleteHook", "ValueTask BeforeDeleteAsync(BeforeDelete<AlvoRecord> deletion, CancellationToken cancellationToken)", "before-delete")]
    [InlineData("IAfterDeleteHook", "ValueTask AfterDeleteAsync(AfterDelete<AlvoRecord> deletion, CancellationToken cancellationToken)", "after-delete")]
    public async Task Every_in_transaction_stage_is_in_scope(string contract, string signature, string stage)
    {
        var source = Usings + $$"""
            public sealed class Audit(HttpClient http) : {{contract}}
            {
                public async {{signature}}
                {
                    await http.GetAsync("https://audit.example", cancellationToken); // ALVO1001
                }
            }
            """;

        var reported = await AnalyzerHarness.Alvo1001Async(source);

        AnalyzerHarness.Lines(reported).ShouldBe(AnalyzerHarness.MarkedLines(source));
        reported[0].GetMessage(System.Globalization.CultureInfo.InvariantCulture).ShouldContain($"the {stage} hook 'Audit'");
    }

    [Fact]
    public async Task A_typed_hook_its_constructor_and_its_field_initializer_are_all_in_scope()
    {
        const string source = Usings + """
            public sealed class Ticket { public Guid Id { get; init; } }
            public sealed class Guard : IBeforeWriteHook<Ticket>
            {
                private readonly HttpClient _client = new(); // ALVO1001
                public ValueTask BeforeWriteAsync(BeforeWrite<Ticket> write, CancellationToken cancellationToken) =>
                    _client.Timeout > TimeSpan.Zero ? ValueTask.CompletedTask : ValueTask.CompletedTask; // ALVO1001
            }
            """;

        AnalyzerHarness.Lines(await AnalyzerHarness.Alvo1001Async(source)).ShouldBe(AnalyzerHarness.MarkedLines(source));
    }

    [Fact]
    public async Task A_validate_hook_may_call_the_network()
    {
        const string source = Usings + """
            public sealed class VatCheck(HttpClient http) : IValidateWriteHook
            {
                public async ValueTask ValidateAsync(ValidateWrite<AlvoRecord> write, CancellationToken cancellationToken) =>
                    await http.GetAsync("https://vies.example", cancellationToken);
            }
            """;

        (await AnalyzerHarness.Alvo1001Async(source)).ShouldBeEmpty();
    }

    [Fact]
    public async Task An_event_handler_may_call_the_network()
    {
        const string source = Usings + """
            public sealed class Notify(HttpClient http) : IAlvoEventHandler<AlvoCreated<AlvoRecord>>
            {
                public Task HandleAsync(AlvoCreated<AlvoRecord> created, AlvoEventContext context, CancellationToken cancellationToken) =>
                    http.GetAsync("https://hooks.example", cancellationToken);
            }
            """;

        (await AnalyzerHarness.Alvo1001Async(source)).ShouldBeEmpty();
    }

    [Fact]
    public async Task A_type_that_is_no_hook_is_not_analysed()
    {
        const string source = Usings + """
            public sealed class Rates(HttpClient http)
            {
                public Task<string> EuroAsync(CancellationToken ct) => http.GetStringAsync("https://rates.example/eur", ct);
            }
            """;

        (await AnalyzerHarness.Alvo1001Async(source)).ShouldBeEmpty();
    }

    [Fact]
    public async Task A_compilation_that_does_not_reference_alvo_gets_no_analysis() =>
        (await AnalyzerHarness.Alvo1001WithoutAlvoAsync("""
            using System.Net.Http;
            public interface IBeforeWriteHook { }
            public sealed class Hook(HttpClient http) : IBeforeWriteHook { public object Client => http; }
            """)).ShouldBeEmpty();
}
```

Run: `dotnet test --project test/MMLib.Alvo.Analyzers.Tests --filter-class '*Alvo1001ScopeTests'`
Expected: compile error — `InTransactionIoAnalyzer` does not exist.

- [ ] **Step 4: Write the analyzer**

`AlvoDiagnostics.cs`:

```csharp
using Microsoft.CodeAnalysis;

namespace MMLib.Alvo.Analyzers;

/// <summary>The diagnostics Alvo's analyzers report.</summary>
internal static class AlvoDiagnostics
{
    /// <summary>The page every rule's help link points into.</summary>
    internal const string HelpRoot = "https://alvo.burgyn.online/extending/analyzer-rules/";

    /// <summary>In-transaction host code reaches the network, a database connection, a file or another data port.</summary>
    internal static readonly DiagnosticDescriptor Alvo1001 = new(
        id: "ALVO1001",
        title: "In-transaction host code reaches the network, a database connection, a file or another data port",
        messageFormat: "{0} inside {1}, which runs while the write's transaction holds its locks; {2}",
        category: "Reliability",
        defaultSeverity: DiagnosticSeverity.Warning,
        isEnabledByDefault: true,
        description: "Before-write, after-write, before-delete and after-delete hooks and CEL functions registered with AddCelFunction run inside the write's transaction. A network call, a database connection, a file or a second Alvo data port there holds the write's row locks for as long as it takes, and does work a rollback cannot undo. Validate hooks and event handlers are where such calls belong.",
        helpLinkUri: HelpRoot + "#alvo1001");
}
```

`AnalysisScope.cs`:

```csharp
using Microsoft.CodeAnalysis;

namespace MMLib.Alvo.Analyzers;

/// <summary>Where an operation runs, as a diagnostic names it, and what to do instead.</summary>
internal sealed class AnalysisScope
{
    private const string HookFix = "call the service from a validate hook, which runs before the transaction, or from an event handler after the commit";
    private const string FunctionFix = "a CEL function must be synchronous and pure: read the value before the write, or check it in a validate hook";

    private AnalysisScope(string name, string fix, bool isCelFunction, INamedTypeSymbol? hookType)
    {
        Name = name;
        Fix = fix;
        IsCelFunction = isCelFunction;
        HookType = hookType;
    }

    internal static AnalysisScope CelFunction { get; } = new("a CEL function registered with AddCelFunction", FunctionFix, isCelFunction: true, hookType: null);

    internal string Name { get; }

    internal string Fix { get; }

    internal bool IsCelFunction { get; }

    /// <summary>Gets the hook type whose own members (and nested types' members) count as the hook's code; none for a delegate.</summary>
    internal INamedTypeSymbol? HookType { get; }

    internal static AnalysisScope OfHookType(string stage, INamedTypeSymbol type) => new($"the {stage} hook '{type.Name}'", HookFix, isCelFunction: false, type);

    internal static AnalysisScope HookDelegate(string stage) => new($"a {stage} hook delegate", HookFix, isCelFunction: false, hookType: null);

    /// <summary>Whether <paramref name="method"/> is the hook's own code — a member of the hook type or a type nested in it, or a local function.</summary>
    internal bool IsOwnCode(IMethodSymbol method)
    {
        if (method.MethodKind == MethodKind.LocalFunction)
        {
            return true;
        }

        for (var type = method.ContainingType; type is not null && HookType is not null; type = type.ContainingType)
        {
            if (SymbolEqualityComparer.Default.Equals(type.OriginalDefinition, HookType.OriginalDefinition))
            {
                return true;
            }
        }

        return false;
    }
}
```

`AlvoSymbols.cs`:

```csharp
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Operations;
using System.Collections.Immutable;

namespace MMLib.Alvo.Analyzers;

/// <summary>The framework symbols one compilation resolves, read once at its start.</summary>
internal sealed class AlvoSymbols
{
    private static readonly ImmutableDictionary<string, string> _hookStages = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["MMLib.Alvo.Hooks.IBeforeWriteHook"] = "before-write",
        ["MMLib.Alvo.Hooks.IBeforeWriteHook`1"] = "before-write",
        ["MMLib.Alvo.Hooks.IAfterWriteHook"] = "after-write",
        ["MMLib.Alvo.Hooks.IAfterWriteHook`1"] = "after-write",
        ["MMLib.Alvo.Hooks.IBeforeDeleteHook"] = "before-delete",
        ["MMLib.Alvo.Hooks.IBeforeDeleteHook`1"] = "before-delete",
        ["MMLib.Alvo.Hooks.IAfterDeleteHook"] = "after-delete",
        ["MMLib.Alvo.Hooks.IAfterDeleteHook`1"] = "after-delete",
    }.ToImmutableDictionary(StringComparer.Ordinal);

    private static readonly ImmutableDictionary<string, string> _delegateVerbs = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["BeforeWrite"] = "before-write",
        ["AfterWrite"] = "after-write",
        ["BeforeDelete"] = "before-delete",
        ["AfterDelete"] = "after-delete",
    }.ToImmutableDictionary(StringComparer.Ordinal);

    private readonly ImmutableDictionary<INamedTypeSymbol, string> _hooks;
    private readonly INamedTypeSymbol? _celHost;
    private readonly INamedTypeSymbol? _entityBuilder;
    private readonly INamedTypeSymbol? _typedEntityBuilder;
    private readonly INamedTypeSymbol? _session;

    private AlvoSymbols(Compilation compilation, ImmutableDictionary<INamedTypeSymbol, string> hooks)
    {
        _hooks = hooks;
        _celHost = compilation.GetTypeByMetadataName("Microsoft.Extensions.DependencyInjection.AlvoBuilderExtensions");
        _entityBuilder = compilation.GetTypeByMetadataName("MMLib.Alvo.Extensions.AlvoEntityBuilder");
        _typedEntityBuilder = compilation.GetTypeByMetadataName("MMLib.Alvo.Extensions.AlvoEntityBuilder`1");
        _session = compilation.GetTypeByMetadataName("MMLib.Alvo.Data.IAlvoSession");
    }

    /// <summary>The symbols of <paramref name="compilation"/>, or <see langword="null"/> when it does not reference Alvo.</summary>
    internal static AlvoSymbols? From(Compilation compilation)
    {
        var hooks = _hookStages
            .Select(pair => (Type: compilation.GetTypeByMetadataName(pair.Key), Stage: pair.Value))
            .Where(pair => pair.Type is not null)
            .ToImmutableDictionary(pair => pair.Type!, pair => pair.Stage, SymbolEqualityComparer.Default);
        return hooks.IsEmpty ? null : new AlvoSymbols(compilation, hooks);
    }

    /// <summary>
    /// The scope of code declared in <paramref name="type"/>: the nearest type in its containing-type chain that implements
    /// an in-transaction hook contract, so a helper type nested in a hook is the hook's code.
    /// </summary>
    internal AnalysisScope? HookScopeOf(INamedTypeSymbol type)
    {
        for (var current = type; current is not null; current = current.ContainingType)
        {
            foreach (var contract in current.AllInterfaces)
            {
                if (_hooks.TryGetValue(contract.OriginalDefinition, out var stage))
                {
                    return AnalysisScope.OfHookType(stage, current);
                }
            }
        }

        return null;
    }

    /// <summary>The scope of a registration call's delegate, and that delegate, when <paramref name="invocation"/> registers one.</summary>
    internal AnalysisScope? RegistrationOf(IInvocationOperation invocation, out IOperation? registered)
    {
        registered = null;
        var method = invocation.TargetMethod;
        var container = method.ContainingType?.OriginalDefinition;
        if (Same(container, _celHost) && method.Name == "AddCelFunction")
        {
            registered = Argument(invocation, "function");
            return AnalysisScope.CelFunction;
        }

        if ((Same(container, _entityBuilder) || Same(container, _typedEntityBuilder)) && _delegateVerbs.TryGetValue(method.Name, out var stage))
        {
            registered = Argument(invocation, "hook");
            return registered is null ? null : AnalysisScope.HookDelegate(stage);
        }

        return null;
    }

    /// <summary>Whether awaiting a call to <paramref name="method"/> is the write's own transaction, or the hook's own code.</summary>
    internal bool IsTransactionCall(IMethodSymbol method, AnalysisScope scope) =>
        scope.IsOwnCode(method)
        || IsSession(method.ContainingType)
        || (method.IsExtensionMethod && method.Parameters.Length > 0 && IsSession(method.Parameters[0].Type))
        || method.ContainingType?.ContainingNamespace?.ToDisplayString() == "MMLib.Alvo.Hooks";

    private bool IsSession(ITypeSymbol? type) =>
        _session is not null && type is not null
        && (Same(type.OriginalDefinition, _session) || type.AllInterfaces.Any(contract => Same(contract, _session)));

    private static IOperation? Argument(IInvocationOperation invocation, string parameter) =>
        invocation.Arguments.FirstOrDefault(argument => argument.Parameter?.Name == parameter)?.Value;

    private static bool Same(ISymbol? left, ISymbol? right) => SymbolEqualityComparer.Default.Equals(left, right);
}
```

`IoReach.cs` (Task 5 needs the type rules and the member-use rules; Task 6 adds the await and task rules to `Describe`):

```csharp
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Operations;
using System.Collections.Immutable;

namespace MMLib.Alvo.Analyzers;

/// <summary>What an operation reaches that in-transaction host code must not: the boot ban's list, plus files.</summary>
internal static class IoReach
{
    /// <summary>The types reached by name, with their subclasses and implementations — <c>HookDependencies</c>' list and more.</summary>
    internal static readonly ImmutableArray<string> BannedTypes =
    [
        "System.Data.Common.DbConnection", "System.Data.Common.DbDataSource", "System.Data.IDbConnection",
        "MMLib.Alvo.Data.IAlvoData", "MMLib.Alvo.Data.IAlvoSessions", "MMLib.Alvo.Data.IAlvoTransactions",
        "MMLib.Alvo.Events.IAlvoEvents", "MMLib.Alvo.Events.IOutboxStore", "MMLib.Alvo.Events.IDeliveryStore",
        "MMLib.Alvo.Events.IAlvoEventPump", "MMLib.Alvo.Commands.IAlvoCommands", "MMLib.Alvo.Management.IAlvoManagement",
        "MMLib.Alvo.Management.IAlvoOperations", "MMLib.Alvo.IAlvoUserStore", "MMLib.Alvo.IAlvoUserAdministration",
        "MMLib.Alvo.IAlvoBootstrapAdmin", "MMLib.Alvo.Ai.IAlvoAssistant",
        "Microsoft.EntityFrameworkCore.DbContext", "Microsoft.EntityFrameworkCore.IDbContextFactory`1",
    ];

    /// <summary>Every type of these namespaces is reached.</summary>
    internal static readonly ImmutableArray<string> NetworkNamespaces =
        ["System.Net.Http", "System.Net.Sockets", "System.Net.WebSockets", "System.Net.Mail", "System.Net.Quic"];

    /// <summary>The <c>System.Net</c> types reached by name, as the boot ban names them.</summary>
    internal static readonly ImmutableHashSet<string> WebRequestTypes =
        ImmutableHashSet.Create(StringComparer.Ordinal, "WebClient", "WebRequest", "HttpWebRequest", "FtpWebRequest");

    private static readonly ImmutableHashSet<string> _fileTypes =
        ImmutableHashSet.Create(StringComparer.Ordinal, "File", "FileInfo", "FileStream", "Directory", "DirectoryInfo");

    /// <summary>Every namespace starting with one of these is reached — the boot ban's own <c>StartsWith</c> rule.</summary>
    internal static readonly ImmutableArray<string> IdentityPrefixes = ["Microsoft.AspNetCore.Identity", "MMLib.Alvo.Identity"];

    /// <summary>Whether <paramref name="type"/> — or an element, generic argument, base type or interface of it — is reached.</summary>
    internal static bool IsReached(ITypeSymbol? type) => type switch
    {
        null => false,
        IArrayTypeSymbol array => IsReached(array.ElementType),
        INamedTypeSymbol { IsGenericType: true } named when named.TypeArguments.Any(IsReached) => true,
        _ => IsReachedItself(type),
    };

    /// <summary>The clause a diagnostic opens with, or <see langword="null"/> when <paramref name="operation"/> reaches nothing.</summary>
    internal static string? Describe(IOperation operation, AnalysisScope scope, AlvoSymbols symbols) => operation switch
    {
        IObjectCreationOperation creation when IsReached(creation.Type) => $"'{Name(creation.Type)}' is created",
        IFieldReferenceOperation { Field.HasConstantValue: true } => null,
        IFieldReferenceOperation { Field.IsStatic: true, Type.IsValueType: true } => null,
        IPropertyReferenceOperation { Property.IsStatic: true, Type.IsValueType: true } => null,
        IFieldReferenceOperation field when IsReached(field.Type) => $"'{Name(field.Type)}' is used",
        IPropertyReferenceOperation property when IsReached(property.Type) => $"'{Name(property.Type)}' is used",
        IParameterReferenceOperation parameter when IsReached(parameter.Type) => $"'{Name(parameter.Type)}' is used",
        ILocalReferenceOperation local when IsReached(local.Type) => $"'{Name(local.Type)}' is used",
        ITypeOfOperation typeOf when IsReached(typeOf.TypeOperand) => $"'{Name(typeOf.TypeOperand)}' is looked up",
        IInvocationOperation call => DescribeCall(call),
        _ => null,
    };

    private static string? DescribeCall(IInvocationOperation call)
    {
        var method = call.TargetMethod;
        if (call.Instance is null && !method.IsExtensionMethod && IsReached(method.ContainingType))
        {
            return $"'{Name(method.ContainingType)}.{method.Name}' is called";
        }

        return method.TypeArguments.FirstOrDefault(IsReached) is { } resolved ? $"'{Name(resolved)}' is resolved" : null;
    }

    private static bool IsReachedItself(ITypeSymbol type) => SelfAndAncestors(type).Any(IsBannedByItself);

    private static bool IsBannedByItself(ITypeSymbol type)
    {
        var space = type.ContainingNamespace?.ToDisplayString() ?? string.Empty;
        return NetworkNamespaces.Contains(space)
            || (space == "System.Net" && WebRequestTypes.Contains(type.Name))
            || (space == "System.IO" && _fileTypes.Contains(type.Name))
            || IdentityPrefixes.Any(prefix => space.StartsWith(prefix, StringComparison.Ordinal))
            || type.Name == "IEmailSender"
            || BannedTypes.Contains(MetadataName(type.OriginalDefinition));
    }

    private static IEnumerable<ITypeSymbol> SelfAndAncestors(ITypeSymbol type)
    {
        for (var current = type; current is not null; current = current.BaseType)
        {
            yield return current;
        }

        foreach (var contract in type.AllInterfaces)
        {
            yield return contract;
        }
    }

    private static string MetadataName(ITypeSymbol type) =>
        type.ContainingNamespace is { IsGlobalNamespace: false } space ? $"{space.ToDisplayString()}.{type.MetadataName}" : type.MetadataName;

    private static string Name(ITypeSymbol? type) => type?.Name ?? "?";
}
```

`symbols` is unused by `Describe` until Task 6 adds the await rule; if the build reports it (IDE0060/CA1801 under `TreatWarningsAsErrors`), add Task 6 Step 3's two arms now and keep Task 6's facts as the proof they work.

`InTransactionIoAnalyzer.cs`:

```csharp
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Operations;
using System.Collections.Immutable;

namespace MMLib.Alvo.Analyzers;

/// <summary>
/// ALVO1001: in-transaction host code — a before/after-write or before/after-delete hook, and a CEL function registered
/// with <c>AddCelFunction</c> — must not reach the network, a database connection, a file or another data port.
/// </summary>
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class InTransactionIoAnalyzer : DiagnosticAnalyzer
{
    private static readonly ImmutableArray<OperationKind> _reaching = ImmutableArray.Create(
        OperationKind.ObjectCreation, OperationKind.Invocation, OperationKind.TypeOf, OperationKind.FieldReference,
        OperationKind.PropertyReference, OperationKind.ParameterReference, OperationKind.LocalReference, OperationKind.Await);

    /// <inheritdoc/>
    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics { get; } = ImmutableArray.Create(AlvoDiagnostics.Alvo1001);

    /// <inheritdoc/>
    public override void Initialize(AnalysisContext context)
    {
        if (context is null)
        {
            throw new ArgumentNullException(nameof(context));
        }

        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
        context.EnableConcurrentExecution();
        context.RegisterCompilationStartAction(OnCompilationStart);
    }

    private static void OnCompilationStart(CompilationStartAnalysisContext context)
    {
        if (AlvoSymbols.From(context.Compilation) is not { } symbols)
        {
            return;
        }

        context.RegisterOperationBlockStartAction(block => OnBlock(block, symbols));
        context.RegisterOperationAction(operation => OnRegistration(operation, symbols), OperationKind.Invocation);
    }

    private static void OnBlock(OperationBlockStartAnalysisContext block, AlvoSymbols symbols)
    {
        if (block.OwningSymbol.ContainingType is { } type && symbols.HookScopeOf(type) is { } scope)
        {
            var seen = new ReportedSymbols();
            block.RegisterOperationAction(operation => Report(operation.ReportDiagnostic, operation.Operation, scope, symbols, seen), _reaching);
        }
    }

    private static void OnRegistration(OperationAnalysisContext context, AlvoSymbols symbols)
    {
        var invocation = (IInvocationOperation)context.Operation;
        if (symbols.RegistrationOf(invocation, out var registered) is not { } scope || Body(registered, invocation) is not { } body)
        {
            return;
        }

        var seen = new ReportedSymbols();
        foreach (var operation in body.DescendantsAndSelf())
        {
            Report(context.ReportDiagnostic, operation, scope, symbols, seen);
        }
    }

    /// <summary>A lambda's body, or the body of a method group declared in the same file.</summary>
    private static IOperation? Body(IOperation? registered, IOperation invocation)
    {
        while (registered is IConversionOperation conversion)
        {
            registered = conversion.Operand;
        }

        if (registered is IDelegateCreationOperation creation)
        {
            registered = creation.Target;
        }

        return registered switch
        {
            IAnonymousFunctionOperation lambda => lambda.Body,
            IMethodReferenceOperation reference => SameFileBody(reference.Method, invocation),
            _ => null,
        };
    }

    private static IOperation? SameFileBody(IMethodSymbol method, IOperation invocation)
    {
        var declaration = method.DeclaringSyntaxReferences.FirstOrDefault(reference => reference.SyntaxTree == invocation.Syntax.SyntaxTree);
        return declaration is null ? null : invocation.SemanticModel?.GetOperation(declaration.GetSyntax());
    }

    private static void Report(Action<Diagnostic> report, IOperation operation, AnalysisScope scope, AlvoSymbols symbols, ReportedSymbols seen)
    {
        if (IoReach.Describe(operation, scope, symbols) is { } what && seen.FirstUse(operation))
        {
            report(Diagnostic.Create(AlvoDiagnostics.Alvo1001, operation.Syntax.GetLocation(), what, scope.Name, scope.Fix));
        }
    }
}
```

`ReportedSymbols.cs` (Ruling 31 — one diagnostic per reached symbol per body; block operation actions may run concurrently, so the set is concurrent):

```csharp
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Operations;
using System.Collections.Concurrent;

namespace MMLib.Alvo.Analyzers;

/// <summary>The reached symbols one body has already reported, so a field, property, parameter or local is reported at its first use only.</summary>
internal sealed class ReportedSymbols
{
    private readonly ConcurrentDictionary<ISymbol, byte> _reported = new(SymbolEqualityComparer.Default);

    /// <summary>Whether <paramref name="operation"/> should be reported: anything but a symbol reference always; a reference the first time.</summary>
    internal bool FirstUse(IOperation operation) => SymbolOf(operation) is not { } symbol || _reported.TryAdd(symbol, 0);

    private static ISymbol? SymbolOf(IOperation operation) => operation switch
    {
        IFieldReferenceOperation field => field.Field,
        IPropertyReferenceOperation property => property.Property,
        IParameterReferenceOperation parameter => parameter.Parameter,
        ILocalReferenceOperation local => local.Local,
        _ => null,
    };
}
```

(Block operation actions are invoked in no guaranteed order, so "first use" is the first one *analysed*; the diagnostic still lands on a real use, and facts assert lines with one marker per reached symbol per body.)

- [ ] **Step 5: Run the facts, accept the analyzer baseline**

Run: `dotnet build src/MMLib.Alvo.Analyzers` (expected: 0 warnings — RS1036/RS2008/RS1041 satisfied; fix any CA finding in place), then `dotnet test --project test/MMLib.Alvo.Analyzers.Tests` and `dotnet test --project test/MMLib.Alvo.Conventions.Tests`.
Expected: `Alvo1001ScopeTests` PASS; `PublicApiApprovalTests` writes a new `PublicApi.MMLib.Alvo.Analyzers.received.txt` listing only `InTransactionIoAnalyzer` (`SupportedDiagnostics`, `Initialize`, the constructor) — move it to `.verified.txt`, re-run (PASS), dispatch `alvo-snapshot-judge` (Ruling 15). Shared architecture facts PASS (the public type lives in `MMLib.Alvo.Analyzers`, no `Internal` namespace). Then `dotnet pack src/MMLib.Alvo -c Release -o /private/tmp/claude-775810410/-Users-martiniak-Developer-GitHub-Burgyn-MMLib-Alvo/01f2ec53-1ec5-49b6-9215-2f48c56b138f/scratchpad/hx10-pack -p:MinVerVersionOverride=0.0.0-hx10 -p:HUSKY=0` and `unzip -l <that dir>/MMLib.Alvo.0.0.0-hx10.nupkg | grep analyzers/dotnet/cs/MMLib.Alvo.Analyzers.dll` prints one line; `unzip -p … '*.nuspec' | grep -c 'Analyzers\|CodeAnalysis'` prints `0`.

- [ ] **Step 6: Record the project**

`docs/architecture/package-boundary.md`, *Current projects*, after the `MMLib.Alvo` bullet:

```markdown
- `src/MMLib.Alvo.Analyzers` — the Roslyn analyzers (`ALVO1001` from HX-10; the typed-entity generator joins it in
  HX-17). **A project, not a package** (spec 2026-10-14 E6): `IsPackable=false`, and `MMLib.Alvo`'s pack places its
  assembly at `analyzers/dotnet/cs/`, so every project that references `MMLib.Alvo` directly gets the rule — NuGet does
  not flow an analyzer asset transitively (a package depending on the core gets `exclude="Build,Analyzers"` by default),
  so a consumer reaching the core only through another package does not — while the core neither references it nor
  declares a dependency on it. It is a separate project only because a compiler
  loads an analyzer from a `netstandard2.0` assembly of its own; rule (a) names Roslyn because a *runtime* Roslyn
  dependency is heavy, and an analyzer asset adds nothing to a consumer's runtime closure. `Microsoft.CodeAnalysis.CSharp`
  is pinned to 4.14.0, older than the compiler of the minimum SDK in `global.json`, and `scripts/test-packed-analyzer`
  builds a packed consumer on that SDK. `SolutionConventionTests` lets a `<IsRoslynComponent>` project declare
  `TargetFramework=netstandard2.0`, and nothing else.
```

`CLAUDE.md` *Repo map*: after the `src/` line add "`src/MMLib.Alvo.Analyzers` is the analyzers project, packed inside the `MMLib.Alvo` nupkg (not a package of its own)." (one line; the `tools/` line is extended in Task 8).

- [ ] **Step 7: Format, ring0, commit**

```bash
dotnet format whitespace MMLib.Alvo.slnx --include src/MMLib.Alvo.Analyzers/AlvoDiagnostics.cs src/MMLib.Alvo.Analyzers/AnalysisScope.cs src/MMLib.Alvo.Analyzers/AlvoSymbols.cs src/MMLib.Alvo.Analyzers/IoReach.cs src/MMLib.Alvo.Analyzers/ReportedSymbols.cs src/MMLib.Alvo.Analyzers/InTransactionIoAnalyzer.cs src/MMLib.Alvo.Analyzers/Properties/AssemblyInfo.cs src/MMLib.Alvo/Properties/AssemblyInfo.cs test/MMLib.Alvo.Analyzers.Tests/AnalyzerHarness.cs test/MMLib.Alvo.Analyzers.Tests/Alvo1001ScopeTests.cs test/MMLib.Alvo.Conventions.Tests/SolutionConventionTests.cs
scripts/test-ring0
git branch --show-current
git add src/MMLib.Alvo.Analyzers test/MMLib.Alvo.Analyzers.Tests src/MMLib.Alvo/MMLib.Alvo.csproj src/MMLib.Alvo/Properties/AssemblyInfo.cs Directory.Packages.props MMLib.Alvo.slnx test/MMLib.Alvo.Conventions.Tests/SolutionConventionTests.cs docs/architecture/package-boundary.md CLAUDE.md
git commit -m "feat(analyzers): ALVO1001 for in-transaction hook types, packed inside the MMLib.Alvo nupkg

Claude-Session: https://claude.ai/code/session_01NJAtafM29iddLwfhb5gnmB"
```

(`git add` of the two new directories adds only their tracked sources — `bin/`/`obj/` are git-ignored; confirm with `git status --short` that no `bin/` path is staged.)

---

## Task 6: `ALVO1001` on delegates, awaits and CEL functions **[slice end]**

**Files:**
- Modify: `src/MMLib.Alvo.Analyzers/IoReach.cs`, `src/MMLib.Alvo/Hooks/Internal/HookDependencies.cs`
- Test: `test/MMLib.Alvo.Analyzers.Tests/{Alvo1001ReachTests.cs,Alvo1001AwaitTests.cs,Alvo1001CelFunctionTests.cs,BootBanParityTests.cs}`, `test/MMLib.Alvo.Analyzers.Tests/Alvo1001ScopeTests.cs`

**Interfaces:**
- Consumes: `AnalyzerHarness`, `AlvoSymbols.IsTransactionCall`, `AnalysisScope.IsCelFunction` (Task 5).
- Produces: `IoReach.Describe` gains the await and task rules; `internal static IReadOnlyList<Type> HookDependencies.Banned`, `NetworkNamespaces`, `WebRequestTypes`, `IdentityPrefixes` (for the parity facts).

- [ ] **Step 1: Write the failing facts**

Append to `Alvo1001ScopeTests` (acceptance (1)'s delegate halves):

```csharp
    [Fact]
    public async Task An_http_client_captured_by_a_cel_function_is_reported()
    {
        const string source = Usings + """
            public static class Rates
            {
                public static IAlvoBuilder AddRates(this IAlvoBuilder alvo, HttpClient http) =>
                    alvo.AddCelFunction("rateOf", (string currency) =>
                        http.GetStringAsync("https://rates.example/" + currency).Result); // ALVO1001
            }
            """;

        var reported = await AnalyzerHarness.Alvo1001Async(source);

        AnalyzerHarness.Lines(reported).ShouldBe(AnalyzerHarness.MarkedLines(source));
        reported[0].GetMessage(System.Globalization.CultureInfo.InvariantCulture).ShouldContain("a CEL function registered with AddCelFunction");
    }

    [Theory]
    [InlineData("BeforeWrite", "BeforeWrite<AlvoRecord>", "before-write")]
    [InlineData("AfterWrite", "AfterWrite<AlvoRecord>", "after-write")]
    [InlineData("BeforeDelete", "BeforeDelete<AlvoRecord>", "before-delete")]
    [InlineData("AfterDelete", "AfterDelete<AlvoRecord>", "after-delete")]
    public async Task Every_in_transaction_delegate_verb_is_in_scope(string verb, string context, string stage)
    {
        var source = Usings + $$"""
            public static class Orders
            {
                public static IAlvoBuilder AddOrders(this IAlvoBuilder alvo) =>
                    alvo.Entity("orders", e => e.{{verb}}("ping", async ({{context}} write, HttpClient http, CancellationToken ct) =>
                        await http.GetAsync("https://ping.example", ct))); // ALVO1001
            }
            """;

        var reported = await AnalyzerHarness.Alvo1001Async(source);

        AnalyzerHarness.Lines(reported).ShouldBe(AnalyzerHarness.MarkedLines(source));
        reported[0].GetMessage(System.Globalization.CultureInfo.InvariantCulture).ShouldContain($"a {stage} hook delegate");
    }

    [Fact]
    public async Task A_validate_delegate_and_a_handler_delegate_are_not_in_scope()
    {
        const string source = Usings + """
            public static class Desk
            {
                public static IAlvoBuilder AddDesk(this IAlvoBuilder alvo) => alvo
                    .Entity("orders", e => e.Validate("vies", async (ValidateWrite<AlvoRecord> write, HttpClient http, CancellationToken ct) =>
                        await http.GetAsync("https://vies.example", ct)))
                    .AddEventHandler("notify", "orders.created", async (HttpClient http, CancellationToken ct) =>
                        await http.GetAsync("https://hooks.example", ct));
            }
            """;

        (await AnalyzerHarness.Alvo1001Async(source)).ShouldBeEmpty();
    }

    [Fact]
    public async Task A_method_group_declared_in_the_same_file_is_analysed_and_one_elsewhere_is_not()
    {
        const string registration = Usings + """
            public static class Desk
            {
                public static IAlvoBuilder AddDesk(this IAlvoBuilder alvo) => alvo
                    .AddCelFunction("near", Near)
                    .AddCelFunction("far", Far.Lookup);

                private static string Near(string key) => System.IO.File.ReadAllText(key); // ALVO1001
            }
            """;
        const string elsewhere = """
            public static class Far
            {
                public static string Lookup(string key) => System.IO.File.ReadAllText(key);
            }
            """;

        var reported = await AnalyzerHarness.Alvo1001Async(registration, elsewhere);

        AnalyzerHarness.Lines(reported).ShouldBe(AnalyzerHarness.MarkedLines(registration));
        reported.ShouldAllBe(d => d.Location.SourceTree!.FilePath == "Consumer0.cs");
    }
```

Create `Alvo1001ReachTests.cs` (Usings as in the scope tests, plus `using System.Data.Common;`):

```csharp
    [Fact]
    public async Task A_service_locator_lookup_or_a_wrapper_of_a_banned_type_is_reported()
    {
        const string source = Usings + """
            public sealed class Guard(IServiceProvider services, Lazy<HttpClient> lazy, System.Collections.Generic.IEnumerable<IAlvoData> all) : IBeforeWriteHook
            {
                public ValueTask BeforeWriteAsync(BeforeWrite<AlvoRecord> write, CancellationToken cancellationToken)
                {
                    var client = services.GetRequiredService<HttpClient>(); // ALVO1001
                    var data = services.GetService(typeof(IAlvoData)); // ALVO1001
                    _ = lazy.IsValueCreated; // ALVO1001
                    _ = all; // ALVO1001
                    return ValueTask.CompletedTask;
                }
            }
            """;

        AnalyzerHarness.Lines(await AnalyzerHarness.Alvo1001Async(source)).ShouldBe(AnalyzerHarness.MarkedLines(source));
    }

    [Fact]
    public async Task A_data_port_a_connection_a_file_and_a_socket_are_reported()
    {
        const string source = Usings + """
            public sealed class Guard(IAlvoData data, DbConnection connection) : IBeforeWriteHook
            {
                public ValueTask BeforeWriteAsync(BeforeWrite<AlvoRecord> write, CancellationToken cancellationToken)
                {
                    _ = data; // ALVO1001
                    _ = connection.State; // ALVO1001
                    _ = System.IO.File.Exists("/tmp/x"); // ALVO1001
                    using var socket = new System.Net.Sockets.Socket(System.Net.Sockets.SocketType.Stream, System.Net.Sockets.ProtocolType.Tcp); // ALVO1001
                    return ValueTask.CompletedTask;
                }
            }
            """;

        AnalyzerHarness.Lines(await AnalyzerHarness.Alvo1001Async(source)).ShouldBe(AnalyzerHarness.MarkedLines(source));
    }

    [Fact]
    public async Task A_reference_to_a_typed_client_the_host_wrote_is_not_reported_by_itself()
    {
        const string source = Usings + """
            public interface IRatesClient { decimal Euro(); }
            public sealed class Guard(IRatesClient rates) : IBeforeWriteHook
            {
                public ValueTask BeforeWriteAsync(BeforeWrite<AlvoRecord> write, CancellationToken cancellationToken)
                {
                    write.Set("rate", rates.Euro());
                    return ValueTask.CompletedTask;
                }
            }
            """;

        (await AnalyzerHarness.Alvo1001Async(source)).ShouldBeEmpty("a synchronous call into the host's own code is the documented blind spot");
    }

    [Fact]
    public async Task A_subclass_of_a_reached_type_and_an_implementation_of_a_reached_interface_are_reached()
    {
        const string source = Usings + """
            public interface IEmailSender { void Send(string to); }
            public sealed class RatesHttp : HttpClient { }
            public sealed class Mailer : IEmailSender { public void Send(string to) { } }
            public sealed class Guard(RatesHttp http, Mailer mail) : IBeforeWriteHook
            {
                public ValueTask BeforeWriteAsync(BeforeWrite<AlvoRecord> write, CancellationToken cancellationToken)
                {
                    _ = http.Timeout; // ALVO1001
                    mail.Send("a@b.example"); // ALVO1001
                    return ValueTask.CompletedTask;
                }
            }
            """;

        AnalyzerHarness.Lines(await AnalyzerHarness.Alvo1001Async(source)).ShouldBe(AnalyzerHarness.MarkedLines(source));
    }

    [Fact]
    public async Task An_ef_core_context_is_reached_by_its_metadata_name()
    {
        const string source = Usings + """
            namespace Microsoft.EntityFrameworkCore { public class DbContext { } }
            public sealed class ShopContext : Microsoft.EntityFrameworkCore.DbContext { }
            public sealed class Guard(ShopContext shop) : IBeforeWriteHook
            {
                public ValueTask BeforeWriteAsync(BeforeWrite<AlvoRecord> write, CancellationToken cancellationToken)
                {
                    _ = shop; // ALVO1001
                    return ValueTask.CompletedTask;
                }
            }
            """;

        AnalyzerHarness.Lines(await AnalyzerHarness.Alvo1001Async(source)).ShouldBe(AnalyzerHarness.MarkedLines(source));
    }

    [Fact]
    public async Task A_reached_local_is_reported_once_however_often_it_is_used()
    {
        const string source = Usings + """
            public sealed class Guard(IHttpClientFactory clients) : IBeforeWriteHook
            {
                public async ValueTask BeforeWriteAsync(BeforeWrite<AlvoRecord> write, CancellationToken cancellationToken)
                {
                    var answer = await clients.CreateClient().GetAsync("https://rates.example", cancellationToken); // ALVO1001
                    write.Set("ok", answer.IsSuccessStatusCode);
                    write.Set("status", (int)answer.StatusCode);
                }
            }
            """;

        AnalyzerHarness.Lines(await AnalyzerHarness.Alvo1001Async(source)).Distinct().Count().ShouldBe(2,
            "the factory parameter on the marked line, and the reached local `answer` once — not at each of its uses");
    }
```

Create `Alvo1001AwaitTests.cs`:

```csharp
    [Fact]
    public async Task Awaiting_the_write_s_transaction_its_typed_extensions_publish_and_own_helpers_is_not_reported()
    {
        const string source = Usings + """
            public sealed class Ticket { public Guid Id { get; init; } public string? Status { get; init; } }
            public sealed class Guard : IBeforeWriteHook<Ticket>
            {
                public async ValueTask BeforeWriteAsync(BeforeWrite<Ticket> write, CancellationToken cancellationToken)
                {
                    _ = await write.Transaction.GetAsync("tickets", write.New.Id, cancellationToken);
                    _ = await write.Transaction.AsSystem().QueryAsync<Ticket>(t => t.Status == "open", 1, cancellationToken).ConfigureAwait(false);
                    await write.PublishAsync("tickets.checked", "ticket", null, cancellationToken);
                    await CheckAsync(write, cancellationToken);
                }

                private static async Task CheckAsync(BeforeWrite<Ticket> write, CancellationToken cancellationToken) =>
                    _ = await write.Transaction.GetAsync("tickets", write.New.Id, cancellationToken);
            }
            """;

        (await AnalyzerHarness.Alvo1001Async(source)).ShouldBeEmpty();
    }

    [Fact]
    public async Task Awaiting_a_typed_client_the_host_wrote_itself_is_reported()
    {
        const string source = Usings + """
            public interface IRatesClient { Task<decimal> EuroAsync(CancellationToken ct); }
            public sealed class Guard(IRatesClient rates) : IBeforeWriteHook
            {
                public async ValueTask BeforeWriteAsync(BeforeWrite<AlvoRecord> write, CancellationToken cancellationToken) =>
                    write.Set("rate", await rates.EuroAsync(cancellationToken)); // ALVO1001
            }
            """;

        var reported = await AnalyzerHarness.Alvo1001Async(source);

        AnalyzerHarness.Lines(reported).ShouldBe(AnalyzerHarness.MarkedLines(source));
        reported[0].GetMessage(System.Globalization.CultureInfo.InvariantCulture).ShouldStartWith("'IRatesClient.EuroAsync' is awaited");
    }

    [Fact]
    public async Task A_helper_type_nested_in_the_hook_is_the_hook_s_own_code_and_is_analysed()
    {
        const string source = Usings + """
            public sealed class Guard : IBeforeWriteHook
            {
                public async ValueTask BeforeWriteAsync(BeforeWrite<AlvoRecord> write, CancellationToken cancellationToken) =>
                    await Checks.RunAsync(write, cancellationToken);

                private static class Checks
                {
                    public static async Task RunAsync(BeforeWrite<AlvoRecord> write, CancellationToken cancellationToken)
                    {
                        _ = await write.Transaction.GetAsync("tickets", Guid.Empty, cancellationToken);
                        await Task.Delay(1, cancellationToken); // ALVO1001
                    }
                }
            }
            """;

        AnalyzerHarness.Lines(await AnalyzerHarness.Alvo1001Async(source)).ShouldBe(AnalyzerHarness.MarkedLines(source));
    }

    [Fact]
    public async Task Awaiting_anything_else_in_a_hook_is_reported_once()
    {
        const string source = Usings + """
            public sealed class Slow(System.IO.Stream stream) : IAfterWriteHook
            {
                public async ValueTask AfterWriteAsync(AfterWrite<AlvoRecord> write, CancellationToken cancellationToken)
                {
                    await Task.Delay(10, cancellationToken); // ALVO1001
                    _ = await stream.ReadAsync(new byte[1], cancellationToken).ConfigureAwait(false); // ALVO1001
                }
            }
            """;

        var reported = await AnalyzerHarness.Alvo1001Async(source);

        AnalyzerHarness.Lines(reported).ShouldBe(AnalyzerHarness.MarkedLines(source));
        reported[0].GetMessage(System.Globalization.CultureInfo.InvariantCulture).ShouldStartWith("'Task.Delay' is awaited, and only the write's transaction may be");
    }
```

Create `Alvo1001CelFunctionTests.cs`:

```csharp
    [Fact]
    public async Task A_task_returning_call_in_a_cel_function_is_reported()
    {
        const string source = Usings + """
            public static class Waits
            {
                public static IAlvoBuilder AddWaits(this IAlvoBuilder alvo) =>
                    alvo.AddCelFunction("later", (string s) => Task.FromResult(s).Result); // ALVO1001
            }
            """;

        var reported = await AnalyzerHarness.Alvo1001Async(source);

        AnalyzerHarness.Lines(reported).ShouldBe(AnalyzerHarness.MarkedLines(source));
        reported[0].GetMessage(System.Globalization.CultureInfo.InvariantCulture).ShouldStartWith("'Task.FromResult' returns a task");
    }

    [Fact]
    public async Task A_pure_cel_function_with_a_token_is_not_reported() =>
        (await AnalyzerHarness.Alvo1001Async(Usings + """
            public static class Pure
            {
                public static IAlvoBuilder AddPure(this IAlvoBuilder alvo) =>
                    alvo.AddCelFunction("upper", (string s, CancellationToken ct) => ct.IsCancellationRequested ? s : s.ToUpperInvariant());
            }
            """)).ShouldBeEmpty();
```

Create `BootBanParityTests.cs`:

```csharp
using MMLib.Alvo.Hooks.Internal;

namespace MMLib.Alvo.Analyzers.Tests;

/// <summary>Every type and namespace the boot ban refuses at start is one ALVO1001 reports, so the two never disagree.</summary>
public sealed class BootBanParityTests
{
    [Fact]
    public void The_analyzer_names_every_type_the_boot_ban_refuses() =>
        HookDependencies.Banned.Select(type => type.FullName!).Except(IoReach.BannedTypes, StringComparer.Ordinal)
            .ShouldBeEmpty("a type the start refuses but the analyzer does not report is a refusal nobody saw coming");

    [Fact]
    public void The_analyzer_names_every_network_namespace_the_boot_ban_refuses() =>
        HookDependencies.NetworkNamespaces.Except(IoReach.NetworkNamespaces, StringComparer.Ordinal).ShouldBeEmpty();

    [Fact]
    public void The_analyzer_names_every_web_request_type_the_boot_ban_refuses() =>
        HookDependencies.WebRequestTypes.Except(IoReach.WebRequestTypes, StringComparer.Ordinal).ShouldBeEmpty();

    [Fact]
    public void The_analyzer_matches_the_identity_namespaces_by_the_boot_ban_s_own_prefixes() =>
        HookDependencies.IdentityPrefixes.Order(StringComparer.Ordinal).ShouldBe(IoReach.IdentityPrefixes.Order(StringComparer.Ordinal));
}
```

In `src/MMLib.Alvo/Hooks/Internal/HookDependencies.cs` expose the lists (no behaviour change; `IsBanned`'s two `StartsWith` literals become `IdentityPrefixes.Any(prefix => type.Namespace?.StartsWith(prefix, StringComparison.Ordinal) == true)`):

```csharp
    /// <summary>Gets the types refused by name, for the analyzer's parity fact.</summary>
    internal static IReadOnlyList<Type> Banned => _banned;

    /// <summary>Gets the namespaces every type of which is refused.</summary>
    internal static IReadOnlyList<string> NetworkNamespaces => _networkNamespaces;

    /// <summary>Gets the <c>System.Net</c> type names refused by name.</summary>
    internal static IReadOnlyList<string> WebRequestTypes => _webRequestTypes;

    /// <summary>Gets the namespace prefixes of the identity stores.</summary>
    internal static IReadOnlyList<string> IdentityPrefixes { get; } = ["Microsoft.AspNetCore.Identity", "MMLib.Alvo.Identity"];
```

- [ ] **Step 2: Run them to verify they fail**

Run: `dotnet test --project test/MMLib.Alvo.Analyzers.Tests`
Expected: the delegate facts PASS already for lambdas (Task 5's registration path) — confirm; the await, task, receiver-once and reach facts FAIL (no await or task rule; `http.GetAsync` inside an await reports on the await too once the await rule exists — the receiver rule below prevents it).

- [ ] **Step 3: Add the await and task rules**

In `IoReach.Describe` add two arms before `IInvocationOperation call => DescribeCall(call)`:

```csharp
        IAwaitOperation wait when !scope.IsCelFunction && AwaitedCall(wait.Operation) is { } awaited
            && !HasReachedReceiver(awaited) && !symbols.IsTransactionCall(awaited.TargetMethod, scope) =>
            $"'{Name(awaited.TargetMethod.ContainingType)}.{awaited.TargetMethod.Name}' is awaited, and only the write's transaction may be",
        IInvocationOperation call when scope.IsCelFunction && ReturnsTask(call.TargetMethod) && !HasReachedReceiver(call) =>
            $"'{Name(call.TargetMethod.ContainingType)}.{call.TargetMethod.Name}' returns a task, which a synchronous CEL function can only block on",
```

and the helpers:

```csharp
    private static IInvocationOperation? AwaitedCall(IOperation operation)
    {
        while (operation is IConversionOperation conversion)
        {
            operation = conversion.Operand;
        }

        return operation is IInvocationOperation { TargetMethod.Name: "ConfigureAwait", Instance: { } inner } ? AwaitedCall(inner) : operation as IInvocationOperation;
    }

    private static bool HasReachedReceiver(IInvocationOperation call) =>
        IsReached(call.Instance?.Type)
        || (call.TargetMethod.IsExtensionMethod && call.Arguments.Length > 0 && IsReached(call.Arguments[0].Value.Type));

    private static bool ReturnsTask(IMethodSymbol method) =>
        method.ReturnType is INamedTypeSymbol { ContainingNamespace: { } space } returned
        && space.ToDisplayString() == "System.Threading.Tasks"
        && returned.Name is "Task" or "ValueTask";
```

(`Task.Delay`'s containing type is `Task` → message `'Task.Delay' is awaited…`. `Task.FromResult(s).Result` — the property reference `.Result` has type `string`, not reported; the invocation `FromResult` returns `Task<string>` → reported once.)

- [ ] **Step 4: Run them to verify they pass**

Run: `dotnet test --project test/MMLib.Alvo.Analyzers.Tests` and `dotnet test --project test/MMLib.Alvo.Tests --filter-class '*HookDependenciesTests'`
Expected: PASS. If a marked-line assertion fails because one use yields two diagnostics, the fix is in `Describe` (Ruling 14: report the receiver only), never in the test.

- [ ] **Step 5: Format, ring0, ring1, commit**

```bash
dotnet format whitespace MMLib.Alvo.slnx --include src/MMLib.Alvo.Analyzers/IoReach.cs src/MMLib.Alvo/Hooks/Internal/HookDependencies.cs test/MMLib.Alvo.Analyzers.Tests/Alvo1001ScopeTests.cs test/MMLib.Alvo.Analyzers.Tests/Alvo1001ReachTests.cs test/MMLib.Alvo.Analyzers.Tests/Alvo1001AwaitTests.cs test/MMLib.Alvo.Analyzers.Tests/Alvo1001CelFunctionTests.cs test/MMLib.Alvo.Analyzers.Tests/BootBanParityTests.cs
scripts/test-ring0 && scripts/test-ring1
git branch --show-current
git add src/MMLib.Alvo.Analyzers/IoReach.cs src/MMLib.Alvo/Hooks/Internal/HookDependencies.cs test/MMLib.Alvo.Analyzers.Tests/Alvo1001ScopeTests.cs test/MMLib.Alvo.Analyzers.Tests/Alvo1001ReachTests.cs test/MMLib.Alvo.Analyzers.Tests/Alvo1001AwaitTests.cs test/MMLib.Alvo.Analyzers.Tests/Alvo1001CelFunctionTests.cs test/MMLib.Alvo.Analyzers.Tests/BootBanParityTests.cs
git commit -m "feat(analyzers): ALVO1001 on hook delegates, CEL functions, awaits and service lookups

Claude-Session: https://claude.ai/code/session_01NJAtafM29iddLwfhb5gnmB"
```

---

## Task 7: A packed consumer on the minimum SDK, and the snippets under the analyzer

**Files:**
- Create: `scripts/test-packed-analyzer`, `scripts/fixtures/packed-consumer/Hooks.cs`
- Modify: `.github/workflows/ci.yml`, `test/MMLib.Alvo.DocsSnippets.Tests/MMLib.Alvo.DocsSnippets.Tests.csproj`

**Interfaces:**
- Consumes: the `PackAlvoAnalyzers` target (Task 5); CI's `artifacts/*.nupkg` (the `Pack` step).
- Produces: `scripts/test-packed-analyzer` (env `ALVO_FEED` = a directory of `.nupkg`, optional); acceptance (2).

- [ ] **Step 1: Write the consumer fixture**

`scripts/fixtures/packed-consumer/Hooks.cs` (not part of any project — the script copies it into a temporary consumer; it is outside every project directory, so no build globs it):

```csharp
using System.Net.Http;
using Microsoft.Extensions.DependencyInjection;
using MMLib.Alvo;
using MMLib.Alvo.Data;
using MMLib.Alvo.Events;
using MMLib.Alvo.Hooks;

namespace PackedConsumer;

public sealed class RateGuard(HttpClient http) : IBeforeWriteHook
{
    public async ValueTask BeforeWriteAsync(BeforeWrite<AlvoRecord> write, CancellationToken cancellationToken) =>
        write.Set("rate", await http.GetStringAsync("https://rates.example/eur", cancellationToken)); // ALVO1001
}

public static class Rates
{
    public static IAlvoBuilder AddRates(this IAlvoBuilder alvo, HttpClient http) =>
        alvo.AddCelFunction("rateOf", (string currency) => http.GetStringAsync("https://rates.example/" + currency).Result); // ALVO1001
}

public sealed class Notify(HttpClient http) : IAlvoEventHandler<AlvoCreated<AlvoRecord>>
{
    public Task HandleAsync(AlvoCreated<AlvoRecord> created, AlvoEventContext context, CancellationToken cancellationToken) =>
        http.GetAsync("https://hooks.example", cancellationToken);
}
```

- [ ] **Step 2: Write the script**

`scripts/test-packed-analyzer` (`chmod +x`):

```bash
#!/usr/bin/env bash
# test-packed-analyzer — ALVO1001 reaches a NuGet consumer of MMLib.Alvo, built on the MINIMUM SDK (HX-10 acceptance (2)).
#
# In NO ring: it needs Docker and nuget.org. CI runs it on the Linux leg right after `Pack`, over that step's
# artifacts/ (ALVO_FEED=artifacts); a human runs it bare and it packs the two packages itself.
#
# What it proves, each against the nupkg a consumer downloads and nothing from this repository's build:
#   - the analyzer sits at analyzers/dotnet/cs/ inside MMLib.Alvo, and the nuspec names neither it nor Roslyn;
#   - a consumer compiled by the SDK global.json pins (the oldest a contributor may use) loads it: the build succeeds,
#     and warns ALVO1001 on exactly the lines scripts/fixtures/packed-consumer/Hooks.cs marks — the before-write hook and
#     the AddCelFunction delegate, and not the event handler.
#
# The consumer references MMLib.Alvo directly: NuGet does not flow an analyzer to a project that only reaches the core
# through another package (docs/architecture/package-boundary.md).
#
# Portable bash + GNU/BSD coreutils alike: CI runs it on Linux, contributors on macOS.
set -euo pipefail
DIR="$(cd "$(dirname "$0")" && pwd)"
ROOT="$(cd "$DIR/.." && pwd)"
FIXTURE="$ROOT/scripts/fixtures/packed-consumer/Hooks.cs"

say() { printf '[packed-analyzer] %s\n' "$*"; }
fail() { printf '[packed-analyzer] FAIL: %s\n' "$*" >&2; exit 1; }

for tool in docker jq unzip; do command -v "$tool" >/dev/null || fail "$tool is required"; done
SDK_VERSION="$(jq -r '.sdk.version' "$ROOT/global.json")"
WORK="$(mktemp -d "${TMPDIR:-/tmp}/alvo-packed.XXXXXX")"
trap 'rm -rf "$WORK"' EXIT

FEED="${ALVO_FEED:-}"
if [ -z "$FEED" ]; then
    FEED="$WORK/feed"
    say "packing MMLib.Alvo.Abstractions and MMLib.Alvo"
    for project in MMLib.Alvo.Abstractions MMLib.Alvo; do
        if ! dotnet pack "$ROOT/src/$project/$project.csproj" -c Release -o "$FEED" -p:MinVerVersionOverride=0.0.0-packed -p:HUSKY=0 \
            >"$WORK/pack.log" 2>&1; then
            cat "$WORK/pack.log" >&2
            fail "packing $project failed"
        fi
    done
fi
FEED="$(cd "$FEED" && pwd)"
# Globs and here-strings, never `producer | grep -q` / `| head`: under pipefail the producer's SIGPIPE fails the check.
shopt -s nullglob
packages=("$FEED"/MMLib.Alvo.[0-9]*.nupkg)
shopt -u nullglob
[ "${#packages[@]}" -eq 1 ] || fail "expected one MMLib.Alvo package in $FEED, found ${#packages[@]}"
nupkg="${packages[0]}"
version="${nupkg##*/MMLib.Alvo.}"; version="${version%.nupkg}"

listing="$(unzip -l "$nupkg")" || fail "cannot list $nupkg"
grep -q 'analyzers/dotnet/cs/MMLib.Alvo.Analyzers.dll' <<<"$listing" || fail "the analyzer is not at analyzers/dotnet/cs/ in $nupkg"
nuspec="$(unzip -p "$nupkg" '*.nuspec')" || fail "cannot read the nuspec of $nupkg"
grep -q '<id>MMLib.Alvo</id>' <<<"$nuspec" || fail "the nuspec of $nupkg is not MMLib.Alvo's"
# Ruling 46: name the packages, not the word — NuGet's own exclude="Build,Analyzers" attributes say "Analyzers".
dependencies="$(grep -c 'MMLib\.Alvo\.Analyzers\|CodeAnalysis' <<<"$nuspec" || true)"
[ "$dependencies" = "0" ] || fail "the nuspec names the analyzer or Roslyn ($dependencies lines)"
say "layout ok: MMLib.Alvo $version carries the analyzer and no dependency on it"

mkdir -p "$WORK/consumer"
cp "$FIXTURE" "$WORK/consumer/Hooks.cs"
cat > "$WORK/consumer/Consumer.csproj" <<EOF
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
    <ImplicitUsings>enable</ImplicitUsings>
    <Nullable>enable</Nullable>
  </PropertyGroup>
  <ItemGroup>
    <PackageReference Include="MMLib.Alvo" Version="[$version]" />
  </ItemGroup>
</Project>
EOF
cat > "$WORK/consumer/nuget.config" <<'EOF'
<?xml version="1.0" encoding="utf-8"?>
<configuration>
  <packageSources>
    <clear />
    <add key="local" value="/feed" />
    <add key="nuget.org" value="https://api.nuget.org/v3/index.json" />
  </packageSources>
  <!-- The packages under test come from the feed only: a published MMLib.Alvo of the same version never stands in. -->
  <packageSourceMapping>
    <packageSource key="local">
      <package pattern="MMLib.Alvo*" />
    </packageSource>
    <packageSource key="nuget.org">
      <package pattern="*" />
    </packageSource>
  </packageSourceMapping>
</configuration>
EOF

say "building the consumer on mcr.microsoft.com/dotnet/sdk:$SDK_VERSION"
if ! docker run --rm -u "$(id -u):$(id -g)" -e HOME=/tmp -e DOTNET_CLI_HOME=/tmp -e NUGET_PACKAGES=/tmp/nuget \
    -e DOTNET_CLI_TELEMETRY_OPTOUT=1 -e DOTNET_NOLOGO=1 -v "$WORK/consumer:/work" -v "$FEED:/feed:ro" -w /work \
    "mcr.microsoft.com/dotnet/sdk:$SDK_VERSION" dotnet build -nologo >"$WORK/build.log" 2>&1; then
    cat "$WORK/build.log" >&2
    fail "the consumer did not build"
fi
if grep -q 'warning CS8032' "$WORK/build.log"; then
    cat "$WORK/build.log" >&2
    fail "the SDK could not load the analyzer (CS8032): the Roslyn pin is above the $SDK_VERSION compiler"
fi

expected="$(grep -n '// ALVO1001' "$FIXTURE" | cut -d: -f1 | sort -n | paste -sd ' ' - || true)"
actual="$(grep -oE 'Hooks\.cs\(([0-9]+),[0-9]+\): warning ALVO1001' "$WORK/build.log" | sed -E 's/Hooks\.cs\(([0-9]+),.*/\1/' | sort -nu | paste -sd ' ' - || true)"
[ -n "$expected" ] || fail "the fixture marks no line"
[ "$expected" = "$actual" ] || { cat "$WORK/build.log" >&2; fail "ALVO1001 on lines [$actual], expected [$expected]"; }
say "ALVO1001 reported on lines $expected and nowhere else"
```

- [ ] **Step 3: Run it**

Run: `bash -n scripts/test-packed-analyzer && scripts/test-packed-analyzer` (Docker running).
Expected: `layout ok …`, then `ALVO1001 reported on lines 13 19 and nowhere else` (the two marked lines). If the consumer build fails with an analyzer-load warning (`CS8032`), the Roslyn pin is above the 10.0.100 compiler — lower `Microsoft.CodeAnalysis.CSharp` in `Directory.Packages.props` and re-run Task 5–6's suites.

- [ ] **Step 4: Wire it into CI and into the snippets**

In `.github/workflows/ci.yml`, after the `Pack` step of `build-test`:

```yaml
      # HX-10 acceptance (2): the packed analyzer reaches a consumer built on the minimum SDK (global.json's), from the
      # very packages the step above produced. Linux only: it builds the consumer in that SDK's container.
      - name: Packed analyzer on the minimum SDK
        if: runner.os == 'Linux'
        run: ALVO_FEED=artifacts scripts/test-packed-analyzer
```

In `test/MMLib.Alvo.DocsSnippets.Tests/MMLib.Alvo.DocsSnippets.Tests.csproj`, add to the `ProjectReference` group (Ruling 17):

```xml
    <!-- Every snippet a reader copies compiles under ALVO1001, which TreatWarningsAsErrors makes an error here. -->
    <ProjectReference Include="../../src/MMLib.Alvo.Analyzers/MMLib.Alvo.Analyzers.csproj" OutputItemType="Analyzer" ReferenceOutputAssembly="false" />
```

Run: `dotnet build test/MMLib.Alvo.DocsSnippets.Tests` — expected: success. An `ALVO1001` error on an existing snippet is a finding: either the snippet does what the docs forbid (fix the snippet) or the analyzer reports compliant code (fix `IoReach`/`AlvoSymbols` and add the snippet's shape as a negative fact to `Alvo1001AwaitTests`) — never a suppression.

- [ ] **Step 5: ring0, commit**

```bash
scripts/test-ring0
git branch --show-current
git add scripts/test-packed-analyzer scripts/fixtures/packed-consumer/Hooks.cs .github/workflows/ci.yml test/MMLib.Alvo.DocsSnippets.Tests/MMLib.Alvo.DocsSnippets.Tests.csproj
git commit -m "ci(analyzers): build a packed consumer on the minimum SDK and compile the docs snippets under ALVO1001

Claude-Session: https://claude.ai/code/session_01NJAtafM29iddLwfhb5gnmB"
```

---

## Task 8: The calibration harness — a load host with gated hooks, four k6 shapes, a hooks arm

**Files:**
- Create: `tools/MMLib.Alvo.Load.Host/{MMLib.Alvo.Load.Host.csproj,Program.cs,CalibrationHooks.cs,Dockerfile,Dockerfile.dockerignore}`, `test/MMLib.Alvo.TestHost.Tests/CalibrationHooksTests.cs`, `test/_shared/testhost/descriptors/calibration-desk.alvo.json`
- Modify: `MMLib.Alvo.slnx`, `test/MMLib.Alvo.TestHost.Tests/MMLib.Alvo.TestHost.Tests.csproj`, `test/load/scenarios.js`, `scripts/test-load`, `test/load/README.md`, `CLAUDE.md`, `docs/architecture/package-boundary.md`

**Interfaces:**
- Consumes: `AlvoHost.CreateBuilder(string[], Action<IConfigurationBuilder>?)`, `AlvoHost.BuildAsync(WebApplicationBuilder)`; `AlvoEntityBuilder.BeforeWrite/AfterWrite(string id, Delegate hook, string? when)`.
- Produces: `internal static IAlvoBuilder CalibrationHooks.AddCalibrationHooks(this IAlvoBuilder alvo)`; image tag `alvo-load:hooks`; k6 shapes `hook_unit`, `hook_noop`, `hook_read`, `hook_after`; `artifacts/load/hooks-1.json`.

- [ ] **Step 1: Write the failing gating fact**

`test/_shared/testhost/descriptors/calibration-desk.alvo.json`:

```json
{
  "$schema": "https://alvo.dev/schema/v1/project.json",
  "apiVersion": "alvo.dev/v1",
  "name": "calibration-desk",
  "description": "The two entities the load host's calibration hooks touch, with the field-service names: a work order's reference selects which hook runs, and the read hook loads its customer.",
  "auth": { "roles": ["dispatcher"] },
  "entities": {
    "customers": {
      "fields": { "name": { "type": "string", "required": true, "maxLength": 80 } },
      "rules": { "list": "'dispatcher' in @user.roles", "get": "'dispatcher' in @user.roles", "create": "'dispatcher' in @user.roles" }
    },
    "work_orders": {
      "fields": {
        "reference": { "type": "string", "required": true, "maxLength": 12 },
        "customer_id": { "type": "uuid", "required": true }
      },
      "rules": { "list": "'dispatcher' in @user.roles", "get": "'dispatcher' in @user.roles", "create": "'dispatcher' in @user.roles" }
    }
  }
}
```

`test/MMLib.Alvo.TestHost.Tests/MMLib.Alvo.TestHost.Tests.csproj` — add (the descriptor glob already copies `_shared/testhost/descriptors/*.alvo.json`):

```xml
    <Compile Include="$(MSBuildThisFileDirectory)../../tools/MMLib.Alvo.Load.Host/CalibrationHooks.cs" Link="_load/CalibrationHooks.cs" />
```

`test/MMLib.Alvo.TestHost.Tests/CalibrationHooksTests.cs`:

```csharp
using MMLib.Alvo.Data;
using MMLib.Alvo.Load.Host;
using MMLib.Alvo.TestHost;

namespace MMLib.Alvo.TestHost.Tests;

/// <summary>
/// The load host's calibration hooks run exactly for their own reference prefix, so each k6 shape measures the hook it is
/// named after — a `when` that matched every write would make all four shapes the same number.
/// </summary>
public sealed class CalibrationHooksTests
{
    [Fact]
    public async Task Each_prefix_runs_exactly_its_own_hook()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var alvo = await AlvoTestHost.StartAsync(
            builder => builder.FromDescriptor(Path.Combine(AppContext.BaseDirectory, "descriptors", "calibration-desk.alvo.json")).AddCalibrationHooks(),
            cancellationToken: ct);
        var dispatcher = AlvoTestHost.Caller("dispatcher");
        var customer = (Guid)(await alvo.Data.CreateAsync("customers", new Dictionary<string, object?> { ["name"] = "Ada" }, dispatcher, cancellationToken: ct))["id"]!;

        foreach (var reference in new[] { "WO-80000001", "WO-81000001", "WO-82000001", "WO-83000001" })
        {
            await alvo.Data.CreateAsync("work_orders", new Dictionary<string, object?> { ["reference"] = reference, ["customer_id"] = customer }, dispatcher, cancellationToken: ct);
        }

        alvo.Hooks.AssertRan("calibration-noop", times: 1);
        alvo.Hooks.AssertRan("calibration-read", times: 1);
        alvo.Hooks.AssertRan("calibration-after", times: 1);
    }

    [Fact]
    public async Task The_read_hook_fails_the_write_when_it_finds_no_customer()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var alvo = await AlvoTestHost.StartAsync(
            builder => builder.FromDescriptor(Path.Combine(AppContext.BaseDirectory, "descriptors", "calibration-desk.alvo.json")).AddCalibrationHooks(),
            cancellationToken: ct);

        await Should.ThrowAsync<Exception>(() => alvo.Data.CreateAsync(
            "work_orders", new Dictionary<string, object?> { ["reference"] = "WO-82000002", ["customer_id"] = Guid.NewGuid() },
            AlvoTestHost.Caller("dispatcher"), cancellationToken: ct));
    }
}
```

(The descriptor path follows how `test/_shared/testhost` facts already locate their descriptors — if they copy flat to the output root, drop the `"descriptors"` segment.) Run: `dotnet test --project test/MMLib.Alvo.TestHost.Tests --filter-class '*CalibrationHooksTests'` — expected: compile error (`CalibrationHooks` missing).

- [ ] **Step 2: Write the load host**

`tools/MMLib.Alvo.Load.Host/MMLib.Alvo.Load.Host.csproj`:

```xml
<Project Sdk="Microsoft.NET.Sdk.Web">

  <!--
    The load harness's second arm (HX-10, Q5): the standalone host's own composition plus three calibration hooks on
    work_orders, so scripts/test-load can measure what an in-transaction hook costs against the image that has none.
    A tool, run and never referenced (tools/Directory.Build.props: IsPackable=false), like MMLib.Alvo.DocsGen.
  -->
  <PropertyGroup>
    <InvariantGlobalization>true</InvariantGlobalization>
    <RequiresAspNetWebAssets>true</RequiresAspNetWebAssets>
  </PropertyGroup>

  <ItemGroup>
    <ProjectReference Include="../../src/MMLib.Alvo.Host/MMLib.Alvo.Host.csproj" />
  </ItemGroup>

</Project>
```

`tools/MMLib.Alvo.Load.Host/Program.cs`:

```csharp
using MMLib.Alvo.Host;
using MMLib.Alvo.Load.Host;

var builder = AlvoHost.CreateBuilder(args);
builder.Services.AddAlvo(alvo => alvo.AddCalibrationHooks());

await using var app = await AlvoHost.BuildAsync(builder);
await app.RunAsync();
```

`tools/MMLib.Alvo.Load.Host/CalibrationHooks.cs`:

```csharp
using System.Globalization;
using Microsoft.Extensions.DependencyInjection;
using MMLib.Alvo.Data;
using MMLib.Alvo.Hooks;

namespace MMLib.Alvo.Load.Host;

/// <summary>
/// The hooks the calibration measures, each selected by the work order's reference prefix so one host serves every
/// shape: <c>WO-81…</c> a before-write hook that does nothing, <c>WO-82…</c> one that reads the customer through the
/// write's transaction, <c>WO-83…</c> an after-write hook that does nothing. <c>WO-80…</c> matches none and measures the
/// write's own unit; the standalone image's <c>WO-9…</c> creates measure a write with no hook at all.
/// </summary>
internal static class CalibrationHooks
{
    internal static IAlvoBuilder AddCalibrationHooks(this IAlvoBuilder alvo) =>
        alvo.Entity("work_orders", orders => orders
            .BeforeWrite("calibration-noop", (BeforeWrite<AlvoRecord> _) => { }, when: "startsWith(new.reference, 'WO-81')")
            .BeforeWrite("calibration-read", ReadCustomerAsync, when: "startsWith(new.reference, 'WO-82')")
            .AfterWrite("calibration-after", (AfterWrite<AlvoRecord> _) => { }, when: "startsWith(new.reference, 'WO-83')"));

    private static async Task ReadCustomerAsync(BeforeWrite<AlvoRecord> write, CancellationToken cancellationToken)
    {
        var customer = write.New["customer_id"] switch
        {
            Guid id => id,
            string text => Guid.Parse(text, CultureInfo.InvariantCulture),
            _ => throw new InvalidOperationException("calibration-read needs the work order's customer_id."),
        };

        _ = await write.Transaction.GetAsync("customers", customer, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException("calibration-read found no customer, so it measured nothing.");
    }
}
```

`tools/MMLib.Alvo.Load.Host/Dockerfile` — the standalone `src/MMLib.Alvo.Host/Dockerfile` with three differences, and nothing else: an extra `COPY tools/MMLib.Alvo.Load.Host/ tools/MMLib.Alvo.Load.Host/` after `COPY src/ src/`; `dotnet restore`/`dotnet publish` name `tools/MMLib.Alvo.Load.Host/MMLib.Alvo.Load.Host.csproj`; `ENTRYPOINT ["dotnet", "MMLib.Alvo.Load.Host.dll"]`. Drop the OCI labels and the examples `COPY` lines (this image is never published; the field-service descriptor is mounted by compose). Header comment: "The load harness's hooks arm (HX-10). Built only by scripts/test-load; never published."

`tools/MMLib.Alvo.Load.Host/Dockerfile.dockerignore` — BuildKit reads `<Dockerfile>.dockerignore` next to the Dockerfile in place of the root one: copy the root `.dockerignore` and replace its `tools/` line with

```
tools/*
!tools/MMLib.Alvo.Load.Host
```

Register the project: `dotnet sln MMLib.Alvo.slnx add --solution-folder tools tools/MMLib.Alvo.Load.Host/MMLib.Alvo.Load.Host.csproj` (beside DocsGen in the `/tools/` folder).

- [ ] **Step 3: Run the gating facts**

Run: `dotnet test --project test/MMLib.Alvo.TestHost.Tests --filter-class '*CalibrationHooksTests'` and `dotnet build tools/MMLib.Alvo.Load.Host`
Expected: PASS; build succeeds. Then the image builds: `docker build -q -f tools/MMLib.Alvo.Load.Host/Dockerfile -t alvo-load:hooks .` prints an image id (this is the one Docker action this task runs; it measures nothing).

- [ ] **Step 4: Add the k6 shapes**

In `test/load/scenarios.js`, replace `createWorkOrder` with a shared poster and five entry points (the `create` shape keeps its reference range and modulus byte-for-byte):

```js
// References must not collide with the seed's (1..ROWS north, 50 000 001.. south) nor with each other, and must stay
// inside the descriptor's four-to-eight digit `work-order-ref` format. The standalone image's `create` owns 9xxxxxxx;
// the hooks arm owns 80…83 xxxxxx, one million each, whose prefix selects the calibration hook (tools/MMLib.Alvo.Load.Host).
function postWorkOrder(name, base, span) {
    const ordinal = base + (exec.scenario.iterationInTest % span);
    const body = JSON.stringify({
        tenant_id: TENANT,
        reference: `WO-${ordinal}`,
        title: 'Created under load',
        status: 'scheduled',
        priority: 3,
        access_code: 'AC-LOAD',
        customer_id: `22222222-0001-4000-8000-${(1).toString(16).padStart(12, '0')}`,
        region_id: `11111111-0000-4000-8000-${(1).toString(16).padStart(12, '0')}`,
    });
    const options = {
        timeout: TIMEOUT,
        headers: { 'X-Alvo-Api-Key': KEY_DISPATCHER, 'Content-Type': 'application/json' },
    };
    record(name, http.post(`${BASE}/api/work_orders`, body, options));
}

export function createWorkOrder() { postWorkOrder('create', 90000000, 9000000); }
export function hookUnit() { postWorkOrder('hook_unit', 80000000, 1000000); }
export function hookNoop() { postWorkOrder('hook_noop', 81000000, 1000000); }
export function hookRead() { postWorkOrder('hook_read', 82000000, 1000000); }
export function hookAfter() { postWorkOrder('hook_after', 83000000, 1000000); }
```

Add to `CATALOGUE` after `create`, each with `arm: 'hooks'`:

```js
    { name: 'hook_unit', exec: 'hookUnit', rate: WRITE_RATE, arm: 'hooks' },
    { name: 'hook_noop', exec: 'hookNoop', rate: WRITE_RATE, arm: 'hooks' },
    { name: 'hook_read', exec: 'hookRead', rate: WRITE_RATE, arm: 'hooks' },
    { name: 'hook_after', exec: 'hookAfter', rate: WRITE_RATE, arm: 'hooks' },
```

and make `selected()` leave hook shapes out of an unfiltered run (they only mean something against the hooks image):

```js
function selected() {
    const wanted = CATALOGUE.filter((s) => (REQUESTED.length === 0 ? s.arm !== 'hooks' : REQUESTED.includes(s.name)));
    return wanted.filter((s) => !s.needsCursor || DEEP_CURSOR !== '');
}
```

(The customer id `22222222-0001-4000-8000-000000000001` is the seed's first north customer, which `calibration-read` loads.)

- [ ] **Step 5: Add the hooks arm to `scripts/test-load`**

Do **not** run `scripts/test-load` in this task (Global Constraints). Edits: *(The two code blocks below are refreshed to the shipped script, as amended by the Task 8 and Task 9 review carries — the script is authoritative.)*

1. Constants near `IMAGE_BASE`: `IMAGE_HOOKS=alvo-load:hooks` and `HOOK_SCENARIOS=hook_unit,hook_noop,hook_read,hook_after`.
2. After the `HEAD_SCENARIOS` block, split hook shapes out of an explicit `--scenarios`:

```bash
# The hook shapes run against the hooks image (tools/MMLib.Alvo.Load.Host), never the standalone one: they mean "what an
# in-transaction hook costs" only beside `create`, measured on the same dataset minutes earlier (HX-10, Q5). They run in
# the calibration tier, or when --scenarios names them; never judged by the gate (a gate ceiling must be measured on the
# gate tier, and the verdict reads only head-1.json), and never beside --baseline-ref.
HOOKS_RUN=""
if [ -n "$SCENARIOS_OVERRIDE" ]; then
    HOOKS_RUN="$(tr ',' '\n' <<<"$SCENARIOS_OVERRIDE" | grep '^hook_' | paste -sd, - || true)"
    HEAD_SCENARIOS="$(tr ',' '\n' <<<"$SCENARIOS_OVERRIDE" | grep -v '^hook_' | paste -sd, - || true)"
elif [ "$TIER" = calibration ]; then
    HOOKS_RUN="$HOOK_SCENARIOS"
fi
if [ -n "$BASELINE_REF" ] && [ -n "$HOOKS_RUN" ]; then
    say "no hooks arm with --baseline-ref: the A/B gate compares two standalone images, and a hook shape is measured only against create in a single-arm run"
    HOOKS_RUN=""
fi
# Refused before any image is built: an empty head list would mean "every scenario" to scenarios.js, and the hook table
# is published against `create` from the same run, so a hooks arm without it is ten minutes spent on nothing publishable.
[ -n "$HEAD_SCENARIOS" ] || die "--scenarios names only hook shapes: add create, the shape they are published against"
if [ -n "$HOOKS_RUN" ]; then
    grep -qx create <<<"${HEAD_SCENARIOS//,/$'\n'}" \
        || die "the hook shapes are published against \`create\`: add it to --scenarios"
    # A misspelt shape (`hook_reed`) passes `^hook_`, and k6 silently skips a name it does not know — the arm would then
    # publish a table without the shape that was asked for.
    for shape in ${HOOKS_RUN//,/ }; do
        grep -qx "$shape" <<<"${HOOK_SCENARIOS//,/$'\n'}" \
            || die "unknown hook shape '$shape' (expected one of $HOOK_SCENARIOS)"
    done
fi
```

(`paste -sd, -` is POSIX on both BSD and GNU; a `--scenarios` of only hook shapes leaves `HEAD_SCENARIOS` empty — guard the head `run_k6` with `if [ -n "$HEAD_SCENARIOS" ]`, and give `assert-load-baseline` the head file only when it exists.)
3. `reset_created_rows` deletes both ranges: `-c "DELETE FROM work_orders WHERE reference LIKE 'WO-9%' OR reference LIKE 'WO-8%';"` (the seed's references never start with 8 or 9 — its comment says so; extend it).
4. In `cleanup`, remove the hooks image when it was built: `[ -n "$HOOKS_RUN" ] && docker image rm -f "$IMAGE_HOOKS" >/dev/null 2>&1 || true`.
5. After `build_image "$IMAGE_HEAD" "$ROOT"`: build the hooks image when needed — `build_image` takes its Dockerfile from the context's `src/MMLib.Alvo.Host`, so add `build_hooks_image()` that runs `docker build --quiet -f "$ROOT/tools/MMLib.Alvo.Load.Host/Dockerfile" -t "$IMAGE_HOOKS" "$ROOT" >/dev/null`, called when `HOOKS_RUN` is non-empty.
6. After the head arm (single-arm branch), the hooks arm and its report:

```bash
# Validity first, as the guard enforces it for the head arm: a dropped iteration or a failed request voids the arm, and a
# void arm publishes garbage. Beyond the guard, every shape the arm was asked for must have produced samples — k6 skips
# a scenario it does not run, and a table missing a shape is not the table that was asked for. A missing customer also
# voids the arm (the `ref` refuses it, or `calibration-read` throws); a clean arm proves nothing about the hooks firing —
# assert_hooks_fire does.
assert_valid_summary() {
    local file="$1" shape
    jq -e '(.http_req_failed.rate // 0) == 0 and (.dropped_iterations.count // 0) == 0' "$file" >/dev/null \
        || die "$(basename "$file") is void: failed requests or dropped iterations — publish nothing from it"
    for shape in ${HOOKS_RUN//,/ }; do
        jq -e --arg s "$shape" '.scenarios[$s].min != null' "$file" >/dev/null \
            || die "$(basename "$file") is void: \`$shape\` produced no samples — publish nothing from it"
    done
}

# THE HOOKS TRIPWIRE, the hooks arm's `row_policy`: a clean arm cannot tell an image that runs the gated hooks from one
# that silently serves the standalone composition or whose `when` never matches — and that arm would publish a hook-free
# write as a hook's cost (HX-10, Ruling 20's p99 rule). So, before k6, one `WO-89…` work order is posted: the hooks
# image's `calibration-probe` refuses exactly that prefix with 409, through the same `when` gating as the measured
# hooks, naming itself in `extensions`. Any other answer refuses the arm. The probe writes nothing, and
# reset_created_rows covers WO-8… anyway.
assert_hooks_fire() {
    local body status
    body=$(jq -cn --arg tenant "$ALVO_FS_TENANT_NORTH" '{
        tenant_id: $tenant, reference: "WO-89000001", title: "Hooks probe", status: "scheduled", priority: 3,
        access_code: "AC-LOAD", customer_id: "22222222-0001-4000-8000-000000000001",
        region_id: "11111111-0000-4000-8000-000000000001" }')
    status=$(curl -sS --max-time 30 -o "$OUT_DIR/probe.json" -w '%{http_code}' -X POST \
        -H "X-Alvo-Api-Key: $KEY_DISPATCHER" -H 'Content-Type: application/json' --data "$body" \
        "$BASE_URL/api/work_orders") \
        || die "the hooks probe (WO-89000001) could not reach $BASE_URL: the hooks image is not serving, so the hooks arm would measure nothing"
    [ "$status" = "409" ] \
        || die "the hooks probe (WO-89000001) answered $status, not calibration-probe's 409: the hooks image is not running its gated calibration hooks, so the hooks arm would measure nothing"
    # A unique `reference` violation is also a 409 `conflict`; only the hook's refusal names it in `extensions`.
    jq -e '.extensions == ["calibration-probe"]' "$OUT_DIR/probe.json" >/dev/null \
        || die "the hooks probe (WO-89000001) answered 409 without calibration-probe in its extensions: the conflict is not the gated hook's, so the hooks arm would measure nothing"
    say "hooks probe refused with 409: the gated calibration hooks are live"
}

hook_table() {
    local head="$1" hooks="$2"
    printf '\n| Shape | min | med | p95 | p99 | max | min / create min |\n|---|---|---|---|---|---|---|\n'
    jq -r --slurpfile head "$head" '
        ($head[0].scenarios.create.min) as $base
        | ([{key: "create", value: $head[0].scenarios.create}] + (.scenarios | to_entries))[]
        | "| `\(.key)` | \(.value.min*100|round/100) | \(.value.med*100|round/100) | \(.value["p(95)"]*100|round/100) | \(.value["p(99)"]*100|round/100) | \(.value.max*100|round/100) | \((.value.min / $base)*100|round/100)x |"' "$hooks"
}

# The hooks arm: the alvo container swapped for the hooks image in place, PostgreSQL and its rows untouched, so the hook
# shapes and the head arm's `create` measure the same dataset. The head image is put back afterwards for --keep.
if [ -n "$HOOKS_RUN" ]; then
    use_image "$IMAGE_HOOKS"
    reset_created_rows
    assert_hooks_fire
    run_k6 hooks-1.json "$HOOKS_RUN" "$CURSOR"
    use_image "$IMAGE_HEAD"
    assert_valid_summary "$OUT_DIR/hooks-1.json"
    [ -f "$OUT_DIR/head-1.json" ] && jq -e '.scenarios.create' "$OUT_DIR/head-1.json" >/dev/null \
        || die "the hook shapes are published against \`create\`: run it in the same invocation"
    # Teed into the artifacts as well as stdout: CI appends hooks.md to calibration.md and uploads it, so the table is
    # copied from the published artifact rather than scraped out of a step log.
    hook_table "$OUT_DIR/head-1.json" "$OUT_DIR/hooks-1.json" | tee "$OUT_DIR/hooks.md"
fi
```

(Place this after the head arm and before the verdict call, so the verdict's exit code stays the script's; the `--baseline-ref` branch runs no hooks arm — add `[ -z "$BASELINE_REF" ] || HOOKS_RUN=""` with a `say` explaining that the A/B gate does not measure hooks.) Extend the usage block at the top: `scripts/test-load --tier calibration --scenarios create,hook_unit,hook_noop,hook_read,hook_after   # the hook calibration (HX-10)`.

Run: `bash -n scripts/test-load && shellcheck scripts/test-load` (if shellcheck is installed; otherwise `bash -n` only). Expected: no output.

- [ ] **Step 6: Document the arm**

`test/load/README.md` — a section *The hooks arm (HX-10)*: what `tools/MMLib.Alvo.Load.Host` adds and why it is a separate image; the five shapes and what each subtracts (`hook_unit − create` = a write's own transaction unit + three `when` evaluations; `hook_noop − hook_unit` = invoking one hook; `hook_read − hook_noop` = one read through `write.Transaction`; `hook_after − hook_unit` = an after-write invocation); calibration tier only; the table is copied into `docs/performance.md` by hand; the Q5 decision rule (Ruling 20). `CLAUDE.md` repo map, `tools/` line: append "and `MMLib.Alvo.Load.Host`, the load harness's hooks arm (built only by `scripts/test-load`)". `docs/architecture/package-boundary.md`, the `tools/` bullet: one sentence naming `MMLib.Alvo.Load.Host` with the same not-a-package reasoning as DocsGen.

- [ ] **Step 7: Format, ring0, commit**

```bash
dotnet format whitespace MMLib.Alvo.slnx --include tools/MMLib.Alvo.Load.Host/Program.cs tools/MMLib.Alvo.Load.Host/CalibrationHooks.cs test/MMLib.Alvo.TestHost.Tests/CalibrationHooksTests.cs
scripts/test-ring0
git branch --show-current
git add tools/MMLib.Alvo.Load.Host test/MMLib.Alvo.TestHost.Tests/CalibrationHooksTests.cs test/MMLib.Alvo.TestHost.Tests/MMLib.Alvo.TestHost.Tests.csproj test/_shared/testhost/descriptors/calibration-desk.alvo.json MMLib.Alvo.slnx test/load/scenarios.js test/load/README.md scripts/test-load CLAUDE.md docs/architecture/package-boundary.md
git commit -m "test(load): a hooks arm for the calibration tier — a load host with gated calibration hooks and four k6 shapes

Claude-Session: https://claude.ai/code/session_01NJAtafM29iddLwfhb5gnmB"
```

---

## Task 9: Docs — *Analyzer rules*, the `function-failed` sweep, the architecture docs, CHANGELOG, the spec amendment **[slice end]**

**Files:**
- Create: `website/src/content/docs/extending/analyzer-rules.mdx`, `website/src/snippets/extending/analyzer-rules/{CurrencyDeskHooks.cs,CurrencyDeskHooksTests.cs,currency-desk.alvo.json}`, `docs/architecture/analyzers.md`, `test/MMLib.Alvo.Analyzers.Tests/Alvo1001DocsTests.cs`
- Modify: `website/astro.config.mjs`, `website/src/content/docs/extending/{index.mdx,write-hooks.mdx}`, every page the sweep finds, `docs/architecture/{cel.md,data-api.md,extensibility.md,write-hooks.md}`, `CHANGELOG.md`, `docs/superpowers/specs/2026-10-14-host-extensibility-design.md`

**Interfaces:**
- Consumes: everything above. Produces: no code.

- [ ] **Step 1: Write the failing docs facts**

`test/MMLib.Alvo.Analyzers.Tests/Alvo1001DocsTests.cs`:

```csharp
namespace MMLib.Alvo.Analyzers.Tests;

/// <summary>The rule's help link opens a page that documents it — the link a developer clicks from the warning.</summary>
public sealed class Alvo1001DocsTests
{
    [Fact]
    public void The_help_link_points_at_the_analyzer_rules_page_s_alvo1001_section()
    {
        AlvoDiagnostics.Alvo1001.HelpLinkUri.ShouldBe("https://alvo.burgyn.online/extending/analyzer-rules/#alvo1001");

        var page = File.ReadAllText(Path.Combine(RepositoryRoot.Find(), "website", "src", "content", "docs", "extending", "analyzer-rules.mdx"));
        page.ShouldContain("\n## ALVO1001", Case.Sensitive, "Starlight slugs '## ALVO1001 …' to #alvo1001");
    }
}
```

Run: `dotnet test --project test/MMLib.Alvo.Analyzers.Tests --filter-class '*Alvo1001DocsTests'` — expected FAIL (no page).

- [ ] **Step 2: Write the snippet and its executed tests**

`website/src/snippets/extending/analyzer-rules/currency-desk.alvo.json`:

```json
{
  "$schema": "https://alvo.dev/schema/v1/project.json",
  "apiVersion": "alvo.dev/v1",
  "name": "currency-desk",
  "auth": { "roles": ["clerk"] },
  "entities": {
    "rates": {
      "fields": {
        "currency": { "type": "string", "required": true, "maxLength": 3 },
        "per_euro": { "type": "decimal", "required": true, "precision": 12, "scale": 6 }
      },
      "rules": { "list": "'clerk' in @user.roles", "get": "'clerk' in @user.roles", "create": "'clerk' in @user.roles" }
    },
    "invoices": {
      "fields": {
        "currency": { "type": "string", "required": true, "maxLength": 3 },
        "amount": { "type": "decimal", "required": true, "precision": 12, "scale": 2 },
        "per_euro": { "type": "decimal", "precision": 12, "scale": 6 }
      },
      "rules": { "list": "'clerk' in @user.roles", "get": "'clerk' in @user.roles", "create": "'clerk' in @user.roles" }
    }
  }
}
```

`website/src/snippets/extending/analyzer-rules/CurrencyDeskHooks.cs`:

```csharp
using System.Net;
using MMLib.Alvo;
using MMLib.Alvo.Data;
using MMLib.Alvo.Hooks;

namespace CurrencyDesk;

[AlvoEntity("rates")]
public sealed class Rate
{
    public required string Currency { get; init; }

    public decimal PerEuro { get; init; }
}

[AlvoEntity("invoices")]
public sealed class Invoice
{
    public Guid Id { get; init; }

    public required string Currency { get; init; }

    public decimal Amount { get; init; }

    public decimal? PerEuro { get; init; }
}

public static class CurrencyDeskHooks
{
    public static IAlvoBuilder AddCurrencyDesk(this IAlvoBuilder alvo) => alvo
        .Entity<Invoice>(invoices => invoices
            .Validate<CurrencyIsTraded>("currency-is-traded")
            .BeforeWrite<StampTheRate>("stamp-the-rate"));
}

public sealed class CurrencyIsTraded(HttpClient exchange) : IValidateWriteHook<Invoice>
{
    public async ValueTask ValidateAsync(ValidateWrite<Invoice> write, CancellationToken cancellationToken)
    {
        using var answer = await exchange.GetAsync($"/traded/{write.New.Currency}", cancellationToken);
        if (answer.StatusCode == HttpStatusCode.NotFound)
        {
            write.Invalid("currency-not-traded", "The exchange does not trade this currency.", "currency");
        }
    }
}

public sealed class StampTheRate : IBeforeWriteHook<Invoice>
{
    public async ValueTask BeforeWriteAsync(BeforeWrite<Invoice> write, CancellationToken cancellationToken)
    {
        var currency = write.New.Currency;
        var rates = await write.Transaction.QueryAsync<Rate>(x => x.Currency == currency, limit: 1, cancellationToken);
        if (rates.Count == 0)
        {
            write.Conflict("no-rate", "No rate is stored for this currency yet.", "currency");
            return;
        }

        write.Set(x => x.PerEuro, rates[0].PerEuro);
    }
}
```

`website/src/snippets/extending/analyzer-rules/CurrencyDeskHooksTests.cs`:

```csharp
using System.Net;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;
using MMLib.Alvo;
using MMLib.Alvo.Data;
using MMLib.Alvo.Hooks;
using MMLib.Alvo.TestHost;
using Shouldly;
using Xunit;

namespace CurrencyDesk.Tests;

public sealed class CurrencyDeskHooksTests
{
    private static string Descriptor => Path.Combine(AppContext.BaseDirectory, "currency-desk.alvo.json");

    [Fact]
    public async Task An_invoice_is_stamped_with_the_stored_rate_inside_its_transaction()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var alvo = await StartAsync(ct);
        var clerk = AlvoTestHost.Caller("clerk");
        await alvo.Data.CreateAsync("rates", new Dictionary<string, object?> { ["currency"] = "CZK", ["per_euro"] = 25.1m }, clerk, cancellationToken: ct);

        var invoice = await alvo.Data.CreateAsync("invoices", new Dictionary<string, object?> { ["currency"] = "CZK", ["amount"] = 100m }, clerk, cancellationToken: ct);

        invoice["per_euro"].ShouldBe(25.1m);
        alvo.Hooks.AssertRan("currency-is-traded");
        alvo.Hooks.AssertRan("stamp-the-rate");
    }

    [Fact]
    public async Task A_currency_the_exchange_does_not_trade_is_refused_before_the_transaction()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var alvo = await StartAsync(ct);

        var refused = await Should.ThrowAsync<AlvoWriteRejectedException>(() => alvo.Data.CreateAsync(
            "invoices", new Dictionary<string, object?> { ["currency"] = "XTS", ["amount"] = 1m }, AlvoTestHost.Caller("clerk"), cancellationToken: ct));

        refused.Code.ShouldBe("currency-not-traded");
        (await alvo.Data.QueryAsync(new AlvoQuery { Entity = "invoices" }, AlvoTestHost.Caller("clerk"), ct)).Items.ShouldBeEmpty();
    }

    private static Task<AlvoTestHost> StartAsync(CancellationToken ct) =>
        AlvoTestHost.StartAsync(builder =>
        {
            builder.FromDescriptor(Descriptor).AddCurrencyDesk();
            builder.Services.AddSingleton(new HttpClient(new Exchange("CZK", "EUR")) { BaseAddress = new Uri("https://exchange.example") });
        }, time: new FakeTimeProvider(new DateTimeOffset(2026, 11, 5, 9, 0, 0, TimeSpan.Zero)), cancellationToken: ct);

    private sealed class Exchange(params string[] traded) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(
                traded.Contains(request.RequestUri!.Segments[^1], StringComparer.Ordinal) ? HttpStatusCode.OK : HttpStatusCode.NotFound));
    }
}
```

Run: `dotnet test --project test/MMLib.Alvo.DocsSnippets.Tests` — expected: PASS, and it compiled under `ALVO1001` (Task 7's wiring): the validate hook's `HttpClient` is allowed, the before-write hook awaits only `write.Transaction`'s typed query. (If `QueryAsync<Rate>`'s `per_euro` decimal comes back as another numeric CLR type, compare with `Convert.ToDecimal(invoice["per_euro"], CultureInfo.InvariantCulture)` — the assertion is "the stored rate", not a CLR type.)

- [ ] **Step 3: Write the page**

`website/src/content/docs/extending/analyzer-rules.mdx` (sidebar order after *Validate and guard writes*; `astro.config.mjs` gains `page('Analyzer rules', 'extending/analyzer-rules'),` after the write-hooks entry; `extending/index.mdx` lists it). Frontmatter `title: Analyzer rules`, `description: ALVO1001 — what the analyzer that ships with MMLib.Alvo reports in hooks and CEL functions, what it cannot see, and what to write instead.` Content, in this order:

1. Intro: the `MMLib.Alvo` package carries a Roslyn analyzer; nothing to install; it runs in the IDE and in `dotnet build`; rules are warnings (a project with `TreatWarningsAsErrors` gets errors).
2. `## ALVO1001 — no network, connection, file or data port inside the write's transaction`: where it looks (the table: before-write / after-write / before-delete / after-delete hook classes — the whole type; the same four as delegates on `Entity(…)`/`Entity<T>(…)`; `AddCelFunction` delegates; **not** validate hooks, event handlers, commands); what it reports (the Ruling 11 list in prose: `HttpClient` and the rest of `System.Net.Http`, sockets, web sockets, mail, QUIC, `WebClient`/`WebRequest`; `DbConnection`, `DbDataSource`, `IDbConnection`; `File`/`FileInfo`/`FileStream`/`Directory`/`DirectoryInfo`; `IAlvoData`, `IAlvoSessions`, `IAlvoTransactions`, `IAlvoEvents`, `IOutboxStore`, `IDeliveryStore`, `IAlvoCommands`, `IAlvoEventPump`, `IAlvoManagement`, `IAlvoOperations`, the identity stores, `IAlvoAssistant`, any `IEmailSender`; also through `GetRequiredService<T>()`, `GetService(typeof(T))`, `Lazy<T>`, arrays and collections); awaits (only `write.Transaction` — incl. `AsSystem()` and the typed `QueryAsync<T>`/`CreateAsync<T>` — the context's `PublishAsync`, and the hook's own methods and the methods of types nested in it; awaiting anything else, **your own typed clients and services included** (`await rates.EuroAsync(ct)`), is reported); in a CEL function, any call that returns a task. A base type or interface counts: a subclass of `HttpClient`, an implementation of `IEmailSender`, an EF Core `DbContext`. One warning per thing reached per method.
3. A flagged example as a plain ```` ```csharp ```` block — a `RateGuard : IBeforeWriteHook<Invoice>` fetching the rate with an injected `HttpClient`, with the warning text it produces in a `:::caution` aside — then "Do this instead": `<SourceExcerpt code={desk} from="public static class CurrencyDeskHooks" title="CurrencyDeskHooks.cs" />` and the paragraph explaining the split (the exchange call moves to a validate hook, which may use the network and runs before the transaction; the rate is read from a table inside it), plus `<Code code={tests} lang="csharp" title="CurrencyDeskHooksTests.cs" />` collapsed or excerpted.
4. `### What it cannot see`: a *synchronous* call into your own code (`rates.Euro()` on a typed client you wrote) — it sees the await of one, not what a synchronous method does inside; synchronous use of other clients it does not list (Redis `IDatabase`, gRPC `ClientBase`, `new StreamReader(path)`); a method group declared in another file; a delegate held in a local or returned by a factory before it is passed to `AddCelFunction` or a hook verb; code in another class your hook calls synchronously. The boot refusal (*Validate and guard writes*) covers a hook's constructor; the time budget covers a CEL function whatever it does — except one that never returns, which holds the write until it does.
5. `### Turning it off`: `#pragma warning disable ALVO1001` around one line, or `dotnet_diagnostic.ALVO1001.severity = none` in `.editorconfig` — and why not to (one sentence).
6. `## The time budget for host functions` (short, links to the CEL functions guide): 250 ms per evaluation shared by every call, the trailing `CancellationToken`, `Alvo:Extensions:CelFunctionBudget`; an overrun fails the write with 500 `function-failed`, drops an after-hook (Warning), or records a handler's delivery dead; reaching the limit is over it.

- [ ] **Step 4: Sweep every claim that a built-in failure is 500**

```bash
git grep -n "function-failed" -- website/src/content docs/architecture samples CHANGELOG.md src/MMLib.Alvo/AlvoBuilderExtensions.cs
git grep -n -i "built-in.*500\|500.*built-in\|a built-in refused\|built-in refuses" -- website/src/content docs/architecture
```

For each hit (memory: sweep for the claim, not a file list): a sentence attributing 500 `function-failed` to a built-in, to hook arithmetic or to a condition with no Bool now says 422 `expression-failed` (link `/reference/problem-types/#expression-failed`); tables of statuses gain a 422 `expression-failed` row (`guides/before-hooks.mdx`, `guides/handle-errors.mdx` both tables, `guides/custom-cel-functions.mdx` — which also gains the budget and the trailing-token paragraph and the `ALVO1001` link —, `guides/call-from-endpoints.mdx` — an in-process `IAlvoData` caller still receives the internal exception, unchanged —, `data-api/conventions.mdx` (a 422 row; `expression-failed` is **not** one of the slugs that need `AddAlvoProblemDetails()`), `concepts/security-model.md`). `modes.mdx` and `start-here/embed.mdx` list only opt-in slugs — unchanged, check the list still reads true. `samples/MMLib.Alvo.Samples.EmbeddedHost/README.md`: same rule. `extending/write-hooks.mdx`: the *Time limits* section names the host-function budget, and the network-ban paragraph links *Analyzer rules* ("the boot refusal checks a hook's constructor; `ALVO1001` checks its code"); a built-in failing in a `when` answers 422.

`docs/architecture/data-api.md`: the slug table gains `| 422 | expression-failed | a built-in CEL function or hook arithmetic refused the request's data (int('x'), substring past the end, overflow, division by zero, a condition that is no Bool) — nothing was written; the detail names the function and Alvo's reason, never the value; answered on every host on the generated routes (GuardAsync), not opt-in, and never on a command or webhook receiver (500 there, Ruling 27); a refusal of a host function's output, of `old.` or of a hook-written value stays 500 (Ruling 26); only a field the request's body carried counts — a refusal of the stored row an update left alone, of a default, of a literal or of `@user`, and any refusal on a DELETE, is 500 (HX-10 Ruling 44) |`; the `function-failed` row becomes "a host function failed (it threw, ran past `Alvo:Extensions:CelFunctionBudget`, or got an argument its parameter cannot hold) or Alvo's own evaluation of a hook failed — …"; the "Three of them" sentence is unchanged. `docs/architecture/cel.md`: the *Failure fails closed* bullet splits by origin (E1) and gains a *Time budget* bullet (Rulings 2–5, 29, 30, 35, 36: the pass sites, measured on return, at the limit is over, any exception after the deadline is the overrun, the post-commit effects — an after-hook dropped, a handler delivery dead —, a function that never returns, no outer token in a declarative before-hook) and an *Origin* sentence (Ruling 26: a built-in over a host function's output, over `old.`, or over a hook-written value is 500); the *Throw when you cannot answer* bullet's "The price of a throw is a 500" stays (host). `docs/architecture/extensibility.md:274`: name 422 for built-ins beside 500 for host functions. `src/MMLib.Alvo/Hooks/Internal/HookDependencies.cs` remarks: "What a direct check cannot see is the HX-10 analyzer's: a typed HTTP client …" becomes "…is `ALVO1001`'s: an awaited call into a typed client, a service-locator lookup, a captured closure — and not a synchronous call into the host's own code (docs: *Analyzer rules*)." `docs/superpowers/plans/2026-11-04-hx-09-write-hooks.md` Ruling 62 gains "*(HX-10 amendment: `ALVO1001` reports an awaited typed client, a service-locator lookup and a captured closure; a synchronous call into host code is its documented blind spot — HX-10 Ruling 25.)*". `docs/architecture/write-hooks.md`: HX-09 Ruling 63's "until HX-10's E1" becomes "422 `expression-failed` when the refused value is the caller's own, 500 otherwise (HX-10 Ruling 26; every refusal in an after-write `when` is 500)"; the network-ban section links `analyzers.md`; L7's sentence becomes "…needs out-of-process hooks (#35); `ALVO1001` reports the awaits that cause it."

Create `docs/architecture/analyzers.md`: what the project is and why it is not a package (Ruling 15); `ALVO1001`'s scope, reach list, await rule, CEL rule and limits (Rulings 10–14); parity with the boot ban (`BootBanParityTests`); how acceptance (2) is proven (Ruling 16); the in-repo wiring (Ruling 17); the mutation gap (Ruling 18); the blind spots (Rulings 25, 39); **HX-13 owes `IAlvoScheduler` on the await allow-list** (§4.4.5 allows it in-transaction; Ruling 39); how to add a rule (id `ALVO1xxx`, a section on *Analyzer rules*, `AnalyzerReleases.Unshipped.md`, the help-link fact).

- [ ] **Step 5: CHANGELOG and the spec amendment**

`CHANGELOG.md`, `## [Unreleased]`:

```markdown
### Added

- **`ALVO1001`, an analyzer shipped inside `MMLib.Alvo`** (HX-10, #387). Before-write, after-write, before-delete and
  after-delete hooks — classes and delegates — and `AddCelFunction` delegates are checked at build time for the network
  (`HttpClient`, sockets, mail…), a database connection, a file, a second Alvo data port (`IAlvoData`, `IAlvoEvents`…),
  a service-locator lookup of one, an `await` of anything but the write's transaction and, in a CEL function, a call that
  returns a task. A warning; validate hooks and event handlers are not checked. Docs: *Analyzer rules*.
- **A time budget for host CEL functions** (#309). Every host function call in one evaluation shares
  `Alvo:Extensions:CelFunctionBudget` (default 250 ms, at most 30 s); a function may take a trailing `CancellationToken`,
  cancelled when the budget is spent.
- **`expression-failed` (422)** (#315): a built-in CEL function or hook arithmetic refusing the request's own data —
  `int('x')`, a `substring` past the end, an overflow, a division by zero — is now a caller-fixable 422 on every host,
  without `AddAlvoProblemDetails()`.

### Changed

- **Breaking for clients that branch on 500:** a built-in CEL function's refusal of the caller's data answered 500
  `function-failed`; it answers 422 `expression-failed`, and is no longer logged at Error. Host functions keep 500.
- **Breaking:** host CEL functions that reach 250 ms in one evaluation — with or without a cancellation token, and
  whatever they do when it fires — now fail: in a before-hook or a write hook's `when` the write is refused (500
  `function-failed` / `hook-failed`); in an after-hook condition, after the commit, the after-hook is dropped with a
  Warning; in an event handler's `when` the delivery is recorded dead. Raise `Alvo:Extensions:CelFunctionBudget` if a
  function legitimately needs longer.
- A built-in refusing a value a host function produced, the stored row (`old.`), or a value a hook wrote stays 500
  `function-failed`; only the caller's own data answers 422, and only on the generated Data API routes (commands keep 500).
```

Spec (`docs/superpowers/specs/2026-10-14-host-extensibility-design.md`): §5 row `10` becomes "`AlvoProblemTypes.ExpressionFailed`; `AddCelFunction` accepts a trailing `CancellationToken` (no new overload); `AlvoExtensionOptions.CelFunctionBudget` | core | 10 | #315, #309; *HX-10 settled details* (1), (4)"; §3.3 E6 gains "(a project packed into the core's nupkg — *HX-10 settled details* (15))"; §4.4.5's analyzer sentence gains "(scope, reach list and limits: *HX-10 settled details* (10)–(14))"; §3.1 Q5's reason gains the calibration result once Task 10 has it (a placeholder line here is **not** written — Task 10 Step 5 adds the sentence); and a new subsection after §7 *HX-10*: `#### HX-10 settled details` listing Rulings 1–44 of this plan, one paragraph each (copy them; amend wording only where Task 1–8 reviews changed a decision).

- [ ] **Step 6: Build the site, run the docs facts, ring0, ring1, commit**

Run: `dotnet test --project test/MMLib.Alvo.Analyzers.Tests --filter-class '*Alvo1001DocsTests'` (PASS), `dotnet test --project test/MMLib.Alvo.DocsGen.Tests` (PASS — the problem-type page now has `expression-failed`), `cd website && npm run build` (PASS; link validation covers the new anchors), then:

```bash
dotnet format whitespace MMLib.Alvo.slnx --include website/src/snippets/extending/analyzer-rules/CurrencyDeskHooks.cs website/src/snippets/extending/analyzer-rules/CurrencyDeskHooksTests.cs test/MMLib.Alvo.Analyzers.Tests/Alvo1001DocsTests.cs
scripts/test-ring0 && scripts/test-ring1
git branch --show-current
git add website/src/content/docs website/src/snippets/extending/analyzer-rules website/astro.config.mjs docs/architecture CHANGELOG.md docs/superpowers/specs/2026-10-14-host-extensibility-design.md test/MMLib.Alvo.Analyzers.Tests/Alvo1001DocsTests.cs samples/MMLib.Alvo.Samples.EmbeddedHost/README.md
git commit -m "docs: analyzer rules, the host-function budget, expression-failed, and HX-10's settled details

Claude-Session: https://claude.ai/code/session_01NJAtafM29iddLwfhb5gnmB"
```

(Stage only files the sweep changed; `git status --short` before committing.)

---

## Task 10: Base, CI parity, rings, packed analyzer, admin e2e, calibration, mutation configuration, reviews, security core, plan-guard, PR report, PR

- [ ] **Step 1: Take `main`**

PR #431 (HX-09) merges into `main` first. `git fetch origin`; `git rebase --onto origin/main origin/hx/09-write-hooks hx/10-hook-guards` if `main` now contains HX-09 as a merge commit of that branch (otherwise `git merge origin/main`, merge-commit style); resolve only what the base change touched, re-run `scripts/test-ring0` and `python3 scripts/gen-prototype-fixtures --check` (Ruling 41 — `main` carries HX-09's own regeneration and its late fix for concurrent idempotency-table DDL on PostgreSQL; regenerate and commit if it reports stale). Every diff below is against `origin/main`; the PR targets `main`.

- [ ] **Step 2: CI-parity Release build, rings, packed analyzer, admin e2e**

```bash
docker run --rm -u "$(id -u):$(id -g)" -e HOME=/tmp -e DOTNET_CLI_HOME=/tmp -v "$PWD":/src -w /src mcr.microsoft.com/dotnet/sdk:10.0 sh -c 'dotnet build MMLib.Alvo.slnx -c Release -p:HUSKY=0 2>&1 | grep -E "error [A-Z]+[0-9]+" | sort -u'
```

The container runs as you, so its `bin`/`obj` output is yours — **no `git clean`**. Expected: no output. Fix any `error CAxxxx`/`RSxxxx`/`IDExxxx` in a `fix(…): …` commit and re-run. Watch: RS1036/RS2008/RS1041/RS1024 in the analyzer project (symbol comparisons must use `SymbolEqualityComparer`), CA1062 on `Initialize`, CA1305 on the overrun's number formatting (invariant culture), CA2000 on `CelFunctionBudget`'s token sources (disposed in `Dispose`), CA1031 nowhere new, CA2007 in the snippets, CA1812 on `CelFunctionBudgetPolicy` (DI-only type — the repository's suppression convention). Then `scripts/test-ring0`, `scripts/test-ring1`, `scripts/test-ring2` (PostgreSQL legs: `DataApiOnPostgresTests`'s `HookGuards` facts), `scripts/test-packed-analyzer` (acceptance (2)), `scripts/test-admin-e2e` — the **one** full run of the dashboard suite in this PR — and `cd website && npm run build`. If a ring2 paging fact fails on an Npgsql connect timeout before any Alvo code runs, read the trace before calling it a regression (known flake).

- [ ] **Step 3: The calibration run (once)**

Run, on the maintainer's machine with Docker running (~10 minutes):

```bash
scripts/test-load --tier calibration --scenarios create,hook_unit,hook_noop,hook_read,hook_after
```

Expected: the seed and its assertions pass, the head arm measures `create`, the hooks arm swaps the alvo container to `alvo-load:hooks`, both summaries are valid (0 failed, 0 dropped — `assert_valid_summary`), and the script prints the hook table. **Never edit `scripts/test-load` while it runs.** If Docker cannot run it here, produce the numbers on GitHub instead (Ruling 21): `git push -u origin hx/10-hook-guards` first (Step 9's push, moved earlier, PR not yet opened), `gh workflow run load.yml --ref hx/10-hook-guards -f tier=calibration`, wait for it (`gh run watch`), `gh run download <id> -n load-calibration -D /private/tmp/claude-775810410/-Users-martiniak-Developer-GitHub-Burgyn-MMLib-Alvo/01f2ec53-1ec5-49b6-9215-2f48c56b138f/scratchpad/hx10-calibration`, and read the table from the job log (the same `hook_table` prints there).

- [ ] **Step 4: Publish the numbers**

Add to `docs/performance.md` a section after the baseline:

```markdown
## In-transaction hooks — 2026-11-xx, pre-v0.1 (`hx/10-hook-guards`)

**What it answers.** Q5 of the host-extensibility design: is 250 ms the right default time limit for a before-write or
after-write hook (and for the host CEL functions of one evaluation)? Measured as what a hook adds to a create.

**Rig.** <the rig, as the baseline section names its own — machine, Docker, generator placement, k6 version>.

**Data and load.** As the baseline (100 000 work orders per tenant, field-service, PostgreSQL 16), `constant-arrival-rate`
10 req/s, 60 s per shape, 10 s warm-up. `create` runs on the standalone image; the four hook shapes on
`tools/MMLib.Alvo.Load.Host` over the same rows, minutes later. <iterations>, **0 dropped**, **0 failed**.

<the printed table, copied verbatim>

**What each step costs, on `min`:** `hook_unit − create` = the write's own transaction unit and three `when`
evaluations; `hook_noop − hook_unit` = invoking one hook; `hook_read − hook_noop` = one read through `write.Transaction`;
`hook_after − hook_unit` = one after-write hook.

**Verdict.** <"250 ms stays: `hook_read`'s p99 is N ms, a tenfold headroom is 10N ms ≤ 250 ms." — or the raised value
and its reason, per the rule below.> Rule (HX-10 Ruling 20): keep 250 ms while `hook_read`'s p99 ≤ 25 ms on the
calibration rig; otherwise the smallest of 500 ms or 1 s that restores tenfold headroom.
```

If the verdict raises the default: change `AlvoHookOptions.DefaultTimeout` (and through it `CelFunctionBudget`'s default), the option facts and docs that state 250 ms (`git grep -n "250 ms\|250ms\|FromMilliseconds(250)"`), Q5 in the spec, and CHANGELOG — in a `fix(hooks): …` commit with ring0/ring1. Either way add Q5's calibration sentence to the spec ("Calibrated in HX-10: `hook_read` p99 N ms on <rig> — `docs/performance.md`"). Commit `docs(performance): publish the in-transaction hook calibration`.

- [ ] **Step 5: Mutation — configuration only, no local run (Ruling 22)**

Do **not** run Stryker and do not re-probe a shard. Read `stryker-config.expressions.json` (`**/Expressions/**`), `stryker-config.api.json` (`**/Api/**`) and `stryker-config.json` (`**/*.cs` minus `!**/Expressions/**`, `!**/Api/**`, …) and confirm: `CelFunctionBudget.cs`, `CelFunctionBudgetPolicy.cs`, `HostCelFunction.cs`, `CelFunctionException.cs`, `PredicateEvaluator.cs` fall in `expressions`; `ProblemResultFactory.cs`, `AlvoExceptionHandler.cs`, `DataApiDocumentation.cs`, `AlvoProblemTypes.cs` in `api`; `BeforeHookRunner.cs`, `WriteExtensionRunner.cs`, `HookDependencies.cs`, `AlvoExtensionOptionsConfiguration.cs` in the core shard — so **no config changes**. Run `scripts/test-assert-mutation-run` (it reads configs and fixtures; it runs no mutation) — expected green. Note in the PR that the analyzer project is in no shard (Ruling 18) and that `expressions`' `break: 90` is the shard this PR adds the most code to.

- [ ] **Step 6: Reviews (labelled substitutes) and the security-core checklist**

Freeze the tree (no edits while a reviewer reads it). `/code-review` and `/security-review` are user-only: dispatch two reviewer subagents over `git diff origin/main...HEAD`, labelled **"substitute for /code-review high"** and **"substitute for /security-review"**, and check `git branch --show-current` after each returns (a reviewer can switch branches).

Point the code reviewer at: `CelFunctionBudget` — the `AsyncLocal` restored on every path, both token sources disposed, the clock read only after the first host call, `Overran` true for a late return and for a deadline-cancelled token but not for an outer cancellation; `HostCelFunction.TakesToken`/`Parameters` (a token never a CEL parameter, four plus the token accepted); the three pass sites and nothing else (`CelEvaluationSitesTests`); `RefusalFor`'s new arm (and its absence from `PortRefusalFor`, so commands keep 500); `CelInterpreter.Evaluate`'s judging catch (innermost node decides, `Judged` stops outer nodes) and `CelDataOrigin.Attributed` at every hook evaluation in both runners; `CallerDataFailure`'s all-or-nothing rule; `ExpressionFailedDetail` never echoing a value; the analyzer's `RegistrationOf` (extension call shapes), the receiver-once rule, `IsTransactionCall`'s source-method clause (only the hook type, its nested types and local functions — Ruling 25), the per-block dedupe, `SameFileBody`; `scripts/test-packed-analyzer`'s line comparison; `scripts/test-load`'s hooks arm (restores the head image, deletes both reference ranges, no hooks arm with `--baseline-ref`).

Run the `alvo-security-core-review` checklist and give the security reviewer Rulings 2, 3, 5, 6, 7, 8, 11, 12, 24, 25–30. Questions to answer explicitly: can a host function run in a before-hook with no budget (a fourth evaluation site; a pass disposed before the function runs)? Can catching the budget's cancellation, or ignoring the token, let a write commit after the deadline? Can a 422 `expression-failed` be produced for a host's failure or a hook failure (wrapper walking into a `HookFailedException`'s cause, an `AggregateException` holding both kinds must answer 500 (Ruling 28); a built-in over a host function's output, over `old.`, or over a field a declarative or host hook wrote must answer 500 (Ruling 26) — is there an evaluation path that bypasses `Evaluate`'s judging catch or `Attributed`)? Can the 422's detail carry a stored or submitted value, or a hidden field's name? Does the budget change what a before-hook can observe of another tenant (it must not: no new data access)? Does `ALVO1001` claim a guarantee the docs then repeat — the page must say it is a warning with stated blind spots, and that the boot refusal and the budget are the enforcement. Fix every finding before the PR; record any accepted risk as a ruling in the spec's *HX-10 settled details*. End the checklist with **mark this change `needs-deep-review`**.

- [ ] **Step 7: `alvo-plan-guard`**

Dispatch `alvo-plan-guard` last (read-only); act on drift it reports. The PLAN marker stays where it is.

- [ ] **Step 8: Follow-up issues**

Open one issue (and link it from the spec's settled detail (18)): "Mutation: a shard for `MMLib.Alvo.Analyzers` (HX-10 Ruling 18)". (Ruling 9's OpenAPI issue is dropped: Ruling 44 made it moot.) Comment on #309 and #315 with the PR link once it exists.

- [ ] **Step 9: PR report**

Use the `alvo-pr-report` skill (it dispatches `alvo-pr-reporter` and publishes the 8-section page). Evidence: acceptance (1)–(5) → tests (the table below); the two public-API diffs (core: `ExpressionFailed`, `CelFunctionBudget`; the new analyzer baseline) against Global Constraints; the both-engine runs of `DataApiEngineTests.HookGuards`; the packed-analyzer script's output; the calibration table and verdict; the Release build; the admin e2e run; the security-core checklist and both substitute reviews; the mutation configuration (no local run, Ruling 22) — calling out Rulings 1 (no overload), 2 (every host function measured — a behaviour change), 6–7 (E1's line and that it is not opt-in), 44 (only fields the request sent are the caller's; a DELETE is never 422), 15 (a project, not a package), 18 (no mutation of the analyzer), 20 (Q5's verdict).

- [ ] **Step 10: Push and open the PR**

```bash
git branch --show-current   # hx/10-hook-guards
git push -u origin hx/10-hook-guards
```

`gh pr create --base main`. The body is the skill's five-line pointer plus three lines `Closes #387`, `Closes #309`, `Closes #315` (one keyword each), ending with `https://claude.ai/code/session_01NJAtafM29iddLwfhb5gnmB`. Then `gh pr edit --add-label needs-deep-review`. After the merge, read #387, #309 and #315 back (`gh issue view`) to confirm each — and only they — closed.

---

## Acceptance criteria → tasks

| HX-10 criterion | Task(s) | Test or artifact |
|---|---|---|
| (1) `ALVO1001` on `HttpClient` use in a before-write hook and in an `AddCelFunction` delegate; none in a handler | 5, 6, 7 | `Alvo1001ScopeTests.An_http_client_in_a_before_write_hook_is_reported_once_at_its_use`, `An_http_client_captured_by_a_cel_function_is_reported`, `An_event_handler_may_call_the_network`, `A_validate_delegate_and_a_handler_delegate_are_not_in_scope`; `scripts/fixtures/packed-consumer/Hooks.cs` (handler line unmarked) |
| (2) A packed consumer on the minimum SDK builds and reports it | 5, 7, 10 | `scripts/test-packed-analyzer` (nupkg layout, nuspec, consumer on `mcr.microsoft.com/dotnet/sdk:10.0.100`, warnings on exactly the marked lines); CI step *Packed analyzer on the minimum SDK* inside **Build & test** |
| (3) A token-aware host CEL function over budget → `function-failed` and rollback, both engines | 1, 2, 4 | `DataApiEngineTests.A_token_aware_host_function_past_its_budget_is_function_failed_and_rolls_back_on_every_engine` (+ `…token_free…`, `…catches_its_cancellation…`, `…inside_its_budget…`) on `SqliteDataApiTests` and `DataApiOnPostgresTests`; `CelFunctionBudgetTests`; `BeforeHookFacetTests.A_before_hook_s_host_functions_share_one_budget_and_an_overrun_refuses_the_write` |
| (4) `int('x')` on caller data → 422 `expression-failed`, both engines | 3, 4 | `DataApiEngineTests.An_int_of_text_on_the_caller_s_data_is_422_expression_failed_and_writes_nothing_on_every_engine`, `An_update_whose_data_a_built_in_refuses_is_422_and_leaves_the_row_on_every_engine` (both engines); `ExpressionFailedAnswerTests`; `CelFailureOriginTests`; `ProblemDetailsTests` (slug emitted and reachable) |
| (5) `docs/performance.md` has the calibrated overhead | 8, 10 | `scripts/test-load --tier calibration --scenarios create,hook_unit,hook_noop,hook_read,hook_after` run once (Ruling 21); the *In-transaction hooks* section with the table and Q5's verdict (Ruling 20); `CalibrationHooksTests` (each shape runs its own hook) |
| Scope: `ALVO1001` beyond (1) — every stage, awaits, service lookups, files, CEL task calls, parity with the boot ban | 5, 6 | `Alvo1001ScopeTests` (stages, typed hook, field initializer, method groups), `Alvo1001ReachTests`, `Alvo1001AwaitTests`, `Alvo1001CelFunctionTests`, `BootBanParityTests` |
| Scope: the budget at every evaluation site; `IBeforeHookRunner` token-free | 2 | `WriteExtensionRunnerTests.A_condition_whose_host_function_overruns_its_budget_fails_the_hook_with_reason_exception`, `PredicateEvaluatorBudgetTests`, `CelEvaluationSitesTests`, `BeforeHookIsolationArchitectureTests` |
| Scope: E1 documented in `data-api.md` and `cel.md` (#315's acceptance) | 9 | the two architecture docs; `website/src/data/problem-types.json` + DocsGen's problem-type page |
| Docs: *Analyzer rules* with executed snippets | 7, 9 | `CurrencyDeskHooksTests` (two, in `MMLib.Alvo.DocsSnippets.Tests`, compiled under `ALVO1001`); `Alvo1001DocsTests`; website build |
| Every PR: plan, CHANGELOG, architecture doc, plan-guard, PR report | 9, 10 | this file; `CHANGELOG.md`; `docs/architecture/analyzers.md`; Task 10 Steps 7 and 9 |
| Security core (Ruling 23) | 10 | `alvo-security-core-review` checklist, `/security-review` substitute, `needs-deep-review` |
