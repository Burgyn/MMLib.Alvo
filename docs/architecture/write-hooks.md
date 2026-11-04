# Write hooks — host C# inside a write, in three phases (HX-09)

> How a host's validate, before-write and after-write (and delete) hooks are bound, run, bounded and failed.
> Spec 2026-10-14 §4.1 and §4.4, with §4.5 for the unit; the plan `docs/superpowers/plans/2026-11-04-hx-09-write-hooks.md`
> carries every ruling cited here by number, and the spec's *HX-09 settled details* records them verbatim. The
> user-facing page is `website/src/content/docs/extending/write-hooks.mdx`. The unit itself is
> [`data-path.md`](./data-path.md)'s *A transaction unit spans several writes (HX-07)*.

## The stages and where they live

Spec §4.1's map, with the code that runs each stage:

| Stage | What | Where |
|---|---|---|
| 0 | idempotency pre-lookup for a keyed write with a validate hook (autocommit; a hit replays with no hook) | the driver's public entry points, `EfAlvoData` |
| 1 | the caller's payload guard and policy pre-check; E9's pre-read for update and replace | `EfAlvoData.Hooks.cs` (`ValidateChangeAsync`) |
| 2 | **validate** hooks, no transaction open | `EfAlvoData.Hooks.cs` → `IWriteExtensionRunner.ValidateAsync` → `WriteExtensionRunner` |
| — | `BEGIN`, in the write's own unit when an in-transaction hook is registered | `EfAlvoData.Units.cs` (`OwnUnitAsync`), `UnitRun` |
| 3 | the descriptor's before-hooks | `IBeforeHookRunner` (unchanged) |
| 4 | **before-write** / **before-delete** hooks | `IWriteExtensionRunner.BeforeWriteAsync` |
| 5 | `WITH CHECK`, tenant scope and the `readOnly` half of the payload guard over the final candidate | `EfAlvoData` (re-run after 4) |
| 6 | the write and its rollups | `EfAlvoData` |
| 7 | **after-write** / **after-delete** hooks | `IWriteExtensionRunner.AfterWriteAsync` |
| 7b | re-read of the post-image when a stage-7 hook wrote | `EfAlvoData.Hooks.cs` |
| 8 | emit into the outbox, then `COMMIT` | unchanged |

`WriteExtensionRunner` (in `MMLib.Alvo/Hooks/Internal`) selects the bound hooks, checks for unbound ones up front, and
then — hook by hook, in order (descriptor hooks first, then `Order`, entity-specific before `"*"`, then registration
order; Ruling 26) — judges each hook's `when` against the row as the earlier hooks left it (Ruling 39), runs it under
its token, and collects its `Set`s and rejections. Each stage call reads the policy catalog once and judges everything
of that stage against it (Ruling 63). The driver knows nothing of hook types: it calls the internal port and receives
patches, a rejection, or a `HookFailedException`.

## A unit of the write's own

A top-level write runs in a unit of its own only when a host in-transaction hook is registered for its entity and
operation (`IWriteExtensionRunner.RunsInTransaction`; Ruling 1). The unit is HX-07's: `OwnUnitAsync` opens it with
`AlvoTransactionOptions`' 30 s default, puts it on the flow, runs the write through `UnitRun.CommittedAsync` — the same
failure mapping and commit as `TransactAsync` — and releases it, flushing the rejection log afterwards. On the flow,
R-B1 refuses a hook's `IAlvoData` call; one DI scope serves every in-transaction hook of the unit (Ruling 22); its events
share the unit's correlation id.

Making every write a unit was rejected: a scope, a timer and a `SAVEPOINT`/`RELEASE` pair on every write of every host,
and a change to shipped behaviour for hosts with no hook. A write with no such hook is byte-for-byte HX-07. With a hook
registered the create's `AuthorizedCandidate` and payload guard run inside the unit, after `BEGIN` and the write turn
([`events.md`](./events.md), C13). `Validates` and `RunsInTransaction` answer from registrations, not bindings, so an
unbound hook still opens the unit and then fails closed (Ruling 31).

## What a hook's transaction is

`write.Transaction` is the unit seen as the caller one hop deeper (`EfAlvoData.HookTransaction`). The depth is checked
lazily, inside each write and publish, where a refusal poisons the unit (Ruling 9): a hook at depth 8 that only reads is
not refused. `HookTransaction` (core) wraps it to count every call — reads, writes, batches, `PublishAsync` and the
`AsSystem()` view's calls — against `MaxOperations` (Ruling 10), to run each call under the hook's token (Ruling 11),
and to refuse a self-write: a row whose hook is running, and every row of a batch whose hooks are running, is held in an
`AsyncLocal` set that flows into the hook's nested calls (Rulings 35, 43).

`AsSystem()` returns a view whose context is `AlvoContext.System(caller.Tenant)` with the caller's provenance — never
another tenant, and a tenantless caller's view is refused by every tenant-scoped entity. Its calls still count, still
pass the self-write check and the token, still run the nested write's hooks, and still raise depth. Each call logs
`ExtensionSystemAccess` (Warning: extension, tenant, entity, operation, actor, correlation id) and increments
`alvo.hook.system_access` (Rulings 2, 46). Host code is trusted operator code, and the boot ban removes every other
escalation route an in-transaction hook could take, so `AsSystem()` is the one audited door.

## What poisons

A failure that the host cannot catch its way out of: the unit is poisoned, the write rolls back, and the answer is 500
`hook-failed` with the hook's id in `extensions` and one fixed `reason`. `HookFailed` logs the host exception's type,
never its message.

| Failure | `reason` | How |
|---|---|---|
| the 51st call through `write.Transaction` | `budget` | `AlvoBudgetExceededException` is thrown into the hook *and* the unit is poisoned, so catching it changes nothing |
| a write of the row whose hook is running | `self-write` | thrown into the hook and recorded on the unit |
| the hook outlived its `Timeout` | `timeout` | measured when it returns; a call it made that was cancelled first recorded an `OperationCanceledException`, which `IAlvoFlowUnit.Blame` replaces — and only a cancellation — with the hook's timeout |
| an exception escaping the hook, or a host CEL function failing in its `when` | `exception` | wrapped as `HookFailedException` naming the hook it escaped (Rulings 12, 63) |
| a `Set` of a maintained, undeclared or ill-typed field, or of a value outside the field's facets | `invalid-set` | `HookSets.FirstProblem`; the log line names the field and facet unless the field is hidden, never the value |
| a registration that does not bind the current catalog | `unbound` | fails closed rather than letting the write through (Ruling 7) |
| stage 7b's re-read finds no row (the hook's write cascaded it away) | `row-vanished` | names the last hook that wrote (Ruling 45) |

Two failures pass through unchanged: `AlvoChainDepthExceededException` (508 `chain-depth-exceeded`) and a nested write's
own `HookFailedException`, which names the innermost hook. A built-in CEL function failing on the caller's data inside a
`when` escapes unchanged and unpoisoned, answered as any condition's built-in failure (Ruling 63's amendment).

**The stated limit (Ruling 55).** Alvo awaits a hook — its calls share the unit's one connection, so abandoning it would
put two commands on that connection — and the unit's deadline rolls back only when no call is running (HX-07 Ruling 10).
A hook that ignores its token and never returns therefore keeps its call running, and the unit open: on SQLite it holds
the database's one writer lock past the 30 s limit. Enforcing it needs out-of-process hooks (#35) or the analyzer
(HX-10); the page tells the host to pass the token on.

## Cascades

Rows a declared `onDelete` cascade or set-null removes or changes run no host hook (Ruling 45). This is a recorded
deviation from PostgreSQL row triggers, which fire on cascaded rows: the cascade is the database's, not the pipeline's,
and running hooks there would mean re-implementing it row by row. A hook whose own write cascades its row away fails the
write with `row-vanished` instead of emitting an event for a row that no longer exists.

## Rejections

`Invalid`, `Conflict` and `Forbidden` record onto the context (`HookRejections`). The winning kind is computed by
precedence — 403 over 409 over 422 — never read from the enum's value, and the answer lists every violation of that
kind (Ruling 19). A rejection is always an answer; PostgreSQL's `BEFORE` trigger returning `NULL` to skip a row has no
counterpart (Ruling 18). A rejecting hook's `Set`s are discarded.

`field:` becomes an RFC 6901 pointer only for a declared field the descriptor never hides; anything else — a hidden
field, an undeclared name, a case variant — gets the body pointer `""`, decided from the descriptor, not the caller's
mask (Rulings 17, 53). A host-thrown `AlvoWriteRejectedException` is an exception like any other: 500 `hook-failed`,
never a rejection.

In a batch a rejection is that row's `AlvoRowRefusal` through the internal `IRowRefusal` (Ruling 4); over HTTP a refused
batch answers 403 with one violation per row whatever the kind, and a rejection at after-write rolls back rows already
inserted. Every rejection is logged to `<prefix>_hook_rejections` (ids, kind, the hook's code, actor — no payload, no
message), written autocommit **after the unit ends** whatever its outcome, because a `TransactAsync` delegate may catch a
rejection and commit the rest; inside a unit the row is handed to the unit and flushed once it released the write turn
and left the flow. A validate rejection, with no unit, is written at once. A failed insert is logged
`HookRejectionNotRecorded` and never turns the caller's answer into a 500 (Ruling 8).

## Validate

Stage 0 decides only whether validate runs: a keyed write first looks its key up autocommit, and a hit skips validate and
replays with no hook (Ruling 14). Stage 2 runs once per top-level write, in the driver's public entry points, before the
replay loop and before a batch's transaction (Ruling 13). The caller's payload guard runs first, so no hook makes an
outbound call for a payload the write will refuse (Ruling 41). E9's pre-read for an update or replace is autocommit,
unlocked and unmasked, under the operation's own decision: an update whose pre-read finds no row answers 404 before any
validate hook, so validate never fails open (Ruling 42). Validate's `Old` may be stale by the time the transaction runs;
an invariant belongs in a before-write hook. There is no validate stage on deletes (Ruling 49).

A replace whose pre-read finds no row is validated as the create it will be: the create rule's `WITH CHECK` and tenant
scope judge it first (`EnsureCreatable`, Ruling 60), and an `If-Match` on it answers 412 before that, since a version
names a row that is not there (Ruling 61). An `If-Match` that no longer matches a stored row is the write's to check, so
validate still runs and the answer is 412 afterwards. `New` differs by operation: a create's is the stamped candidate
without `id` (the id is minted per attempt), a replace's the unstamped whole row with the path's id.

A batch validates every row first and reports every bad row together — payload and rule refusals, rows not found, and
validate refusals (Ruling 59) — without opening a transaction. Validate's `Data` is a session as the caller one hop
deeper, so a validate hook writing what it validates stops at the depth limit. Validate hooks resolve from one fresh DI
scope per write (Ruling 22).

**Switching off.** `PUT …/extensions/{id}/enabled` (Admin level) records the switch in
`alvo_subscriber_controls.disabled_at/disabled_by` through two abstract `IDeliveryStore` members, `SetDisabledAsync` and
`DisabledAsync` (Ruling 5); the runner reads the disabled set with one autocommit read per write that has a validate hook
to run, so a switch on one replica is honoured by every replica on its next write. Any other hook is refused 409
(Ruling 51): a guard cannot be disabled from outside the code.

There is no public factory for hook contexts in HX-09: a hook is tested through `AlvoTestHost` and a real write.

## SuppressHostHooks

A system unit with `SuppressHostHooks` and a `Reason` skips stages 4 and 7 for every write of the unit (stage 2 never
runs inside a unit); the descriptor's hooks and emission still run. It is audited once per unit: an
`alvo_operations_log` row (`suppress-host-hooks`, actor `system`, target `unit:<correlation id>`, the reason) written in
the unit's transaction — a rolled-back unit skipped nothing durably and leaves no row — and a `ManagementAudit` Warning
after the commit (Ruling 30).

## The boot ban

An in-transaction hook may not reach the network or write outside its unit, so a class hook's public constructors and a
delegate hook's non-supplied parameters are checked at boot (`HookDependencies`; Rulings 23, 54, 62): `System.Net.Http`,
`System.Net.Sockets`, `System.Net.WebSockets`, `System.Net.Mail`, `System.Net.Quic`, `WebClient`/`WebRequest` and their
kin (matched by name), `IHttpClientFactory`, `DbConnection` and `DbDataSource`, `IEmailSender` (by name), Alvo's data and
operations ports (`IAlvoData`, `IAlvoSessions`, `IAlvoTransactions`, `IAlvoEvents`, `IAlvoCommands`, `IAlvoManagement`,
`IAlvoOperations`, `IAlvoEventPump`, `IAlvoAssistant`), the outbox and delivery stores, and the identity stores
(`Microsoft.AspNetCore.Identity.*`, `MMLib.Alvo.Identity.*`, `IAlvoUserStore`, `IAlvoUserAdministration`,
`IAlvoBootstrapAdmin`). Validate hooks are exempt: they run before any transaction.

The check sees **direct dependencies only**. A typed HTTP client that wraps an `HttpClient`, a service the hook resolves
later from `IServiceProvider`, and whatever a delegate's closure captured are not caught; they are the HX-10 analyzer's.
The boot also refuses an unknown entity, a `when` that does not compile or reads `new.` on a delete, options out of
bounds (in-transaction `Timeout` in (0, 30 s], validate in (0, 60 s], `MaxOperations` 1–1000; Ruling 24), and a
composition without `IAlvoTransactions` or `IHookRejectionLog` — the in-memory reference (Ruling 34). An apply that
would unbind a hook is refused naming it (`WriteHookApplyRefusals`).

## Telemetry and the test host's recorder

One `alvo.hook` span per invocation carries the extension id in `alvo.subscriber` and the outcome (`succeeded`,
`rejected`, `failed`); the meter `MMLib.Alvo.Hooks` records `alvo.hook.duration`, `alvo.hook.rejections`,
`alvo.hook.operations` and `alvo.hook.system_access` (Ruling 27). `ExtensionStats` reads the duration histogram, so the
operations stats and the dashboard's write lanes list write hooks beside handlers — invocations, failures, rejections
and p50/p95, this instance, 24 hourly buckets (Ruling 28).

`AlvoHookRecorder` reads the same tag, so `Hooks.AssertRan` counts write hooks with no recorder change; a rejection
counts as a run. `AlvoTestHost.Data` is an internal tracking decorator: a call joins an ambient W3C trace when there is
one and otherwise opens its own recorded root, so a write's hooks are this host's runs (Ruling 64). A write through
`Services.GetRequiredService<IAlvoData>()` stays outside the recorder.

## Engine parity

HX-09 adds no SQL dialect member. The rejection log is an ordinary framework table created by the EF driver on both
engines; the switch-off columns are HX-06's. The end-to-end facts (`test/_shared/testhost/WriteHookFacts.cs`) run on
SQLite (`MMLib.Alvo.TestHost.Tests`) and PostgreSQL (`MMLib.Alvo.Data.PostgreSql.Tests.Integration`), including the proof
that no transaction is open while validate runs — SQLite's `sqlite3_get_autocommit` on the connection, PostgreSQL's
`pg_stat_activity` showing no backend `idle in transaction` (Ruling 44) — and the HTTP status facts run on both through
`DataApiEngineTests.WriteHooks`.

## Deviations

Every decision this design took against a source recommendation — cascades without hooks, no silent skip, a unit only
where a hook is registered, `readOnly` honoured by `Set`, the direct-dependency ban — is listed with its reason in the
spec's *HX-09 settled details* (after §4.4.6), numbered as the plan's rulings.
