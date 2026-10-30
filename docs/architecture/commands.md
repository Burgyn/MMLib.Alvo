# Commands and inbound webhooks — the HTTP seam for host actions (HX-08)

> How a named host action is gated, run and made retry-safe, and how a Standard Webhooks delivery is received once.
> Spec 2026-10-14 §4.9 and its *HX-08 settled details*; the plan `docs/superpowers/plans/2026-10-30-hx-08-commands-http.md`
> carries every ruling cited here by number. The user-facing pages are `website/src/content/docs/extending/commands.mdx`
> and `…/http-conventions.mdx`. The conventions, `ForEntity` and the Data API's gate witness are in `data-api.md`.

## What a command is

Two shapes, one model:

- **A mapped command** is a minimal-API endpoint plus one Alvo filter. `MapAlvoCommand(name, pattern, access, handler)`
  (and its entity-bound and typed overloads) calls `MapPost` with the host's own delegate, so the host's parameter
  binding, results and conventions are ASP.NET Core's; Alvo adds `CommandEndpointFilter` through a gate-witnessed
  factory, `CommandEndpointMetadata`, the operationId `commands.<lowerCamel>` and the tag `commands`. A command is
  always a `POST` (Ruling 4).
- **An internal command** is an `IAlvoCommand<TInput>` registered with `AddCommand<T>(name, access)` and invoked in
  process through `IAlvoCommands.InvokeAsync` — the same rule, unit and completion, no route, no key (Ruling 17).

Command names share one id namespace with every other extension (`extensions/<id>/…` pointers, one catalog), and match
`[a-z][a-z0-9_]*(\.[a-z][a-z0-9_]*)*`. Mapping happens after the container is built, so mapped commands register at
map time into the internal `CommandRegistry`, which `ExtensionRegistry` reads live. The registry is **sealed by the
first binding** — the boot's extension check, or the catalog's lazy initialisation, whichever comes first — and a
command mapped after that throws at the map call: a binding made without it would never include it (Rulings 13, 40).
Commands need a relational provider: `ICommandLedger` and `IAlvoTransactions` are EF-driver services, and a
composition without them refuses to boot with a command registered (Ruling 12).

## The filter's order and why

`CommandEndpointFilter` judges, in this order:

1. **The caller** — `CallerGate`, the code `.WithAlvoCaller()`'s `AlvoCallerFilter` shares: no credential is
   anonymous, an unusable key is `401`. A bound command needs the read **and** the write scope on its entity, because
   it reads the row before it writes; an unbound one needs `*:write` (Rulings 6, 39). Scopes are an HTTP narrowing of
   the command's own entity; the handler's writes elsewhere are judged by the port's policy.
2. **The key** — a malformed `Idempotency-Key` is `422` before anything is hashed; then the key is claimed. A replay,
   an in-flight key or a spent one is answered here (Ruling 7).
3. **The row** — read as the caller through `IAlvoSessions`; unreadable or absent is the same `404`.
4. **The rule** — `CommandAccessJudge.EnsureAdmitted`, its required context first (`403`).
5. **The depth** — `AlvoChainDepthLimit` (`508`).
6. **The handler**, outside any transaction.

The key is claimed **before** the row and the rule so a replay is answered before they are judged again: the key's
scope is the caller's own identity and credential, so a replay returns only what that credential was answered, and a
command that changed or deleted its row can still be retried. A refusal at steps 3–5 gives the claim back.

The access filter is composed through an `AlvoGateWitness`, and `CommandGateCheck` — an `IStartupFilter` that runs
after the request pipeline is built — walks every composite endpoint data source except `AlvoEndpointDataSource` and
refuses the start (`AlvoStartupRefusedException`) when a command endpoint's witness was never invoked: a convention
stripped its gate (Ruling 38). It is a startup filter rather than a boot stage because the host's endpoint data
sources reach the routing composite only when the pipeline is built — after the boot's `StartingAsync` has primed the
descriptor and before the server listens; read during the boot, the composite is still empty and every command would
pass unchecked (measured, `GateWitnessTests`). A refusal fails the start before the server binds and is recorded on
`AlvoBootState` for readiness. It never reads Alvo's own Data API source, whose routes materialise on the first
request; a `MapGroup` holding both the Data API and a command is enumerated whole, so its Data API routes materialise
at start — the accepted cost. With no routing composite it logs at Debug and checks nothing.

## The access rule

CEL compiled in `CelProfile.Condition`, not §4.9's `CelProfile.Access`, which refuses `@tenant` by design (Ruling 1).
An unbound command compiles against a fieldless scope, so it reads only `@user` and `@tenant`; a bound one against its
entity, the row as the current image. `old.` and `changed()` are refused — there is no before-image. Host CEL
functions are admitted. A typed lambda (`caller => …`, `(row, caller) => …`) is rewritten into an `EventCondition<T>`
lambda and translated by the `when` translator, so a typed and a CEL rule compile to one tree and §5 gains no type
(Ruling 2). An empty rule is refused when the command is registered.

Rules compile per catalog in `ExtensionBindingCompiler`, beside every other extension's binding, so the apply guard
refuses a descriptor that would unbind a command. One `CommandAccessJudge` serves the HTTP filter, `IAlvoCommands` and
the policy simulator (`Preview`), so the simulator cannot drift from what runs. A bound command carries the context
its rule requires (`EventConditionReads.Required`), and the judge refuses a caller missing the tenant or the user the
rule reads **before** evaluating — as `PolicyEngine` does — so `!(@tenant in […])` cannot admit the tenantless caller
through a `null` comparison (Ruling 33). A rule reading a field the policy hides from some callers is refused at
binding, and an apply that would hide a field a bound command reads is refused: the allow/deny would be an oracle on
a value the caller cannot read (Ruling 35).

## At most one unit

The handler runs outside any transaction (R-M7): external calls first, then `AlvoCommandContext.TransactAsync` opens
at most one unit; a second throws. `CommandRun` tracks the run: whether a unit was opened, whether it committed. The
completion event and the key's commit mark are written **last, inside the unit**, through the internal
`IAlvoCommandUnit` that only Alvo's own `EfAlvoTransaction` implements — so they commit with the handler's writes or
not at all (Ruling 10).

- A run that opened **no** unit and answered `2xx` gets its completion and its answered key in one autocommit
  transaction after the handler returns; non-`2xx` (or a throw) emits nothing and gives its key back (Ruling 11).
- A run whose unit committed keeps its event whatever it answers afterwards — the effects happened — and records its
  answer; one that threw after the commit records `500`, so a retry is not run twice.
- A unit **opened and not committed** — a handler that caught its rolled-back unit and answered `2xx` — emits nothing,
  and its key is released, logged `UnitRolledBack` (Ruling 43).
- A data-port refusal the handler raises is mapped inside the handler delegate, so it is the run's recorded answer and
  a replay returns it (Ruling 51).
- The bookkeeping after the handler is not cancellable: a client that hung up must not leave effects without their
  key record (Ruling 29).

The completion is `alvo.command.<name>.completed`, subject `<entity>/<id>` or `commands/<name>`, payload `command`
(plus `entity`, `id`) — never the input or the answer, because the outbox is read by every consumer. It is minted by
the internal `AlvoFrameworkEvent.Create`, keyed per tenant (Ruling 48). A handler of a completion no command produces
refuses boot (Ruling 16).

## The idempotency ledger

`<prefix>_command_requests`, one row per `(scope, idempotency_key)`: claimed → committed → answered.

- **The claim** is an autocommit insert with a fresh `claim_token` and a **15-minute** lease, made with
  `CancellationToken.None` (Ruling 46). A concurrent claim is detected by the unique violation only
  (`IAlvoSqlDialect.DecodeConstraintViolation(…)?.Kind == Unique`), on PostgreSQL and SQLite alike; any other database
  failure propagates as a `500`.
- **Fencing** (Ruling 36). Commit, answer, release and complete are all `… WHERE claim_token = @token`. The commit mark
  is written inside the handler's unit; zero rows means the lease was taken over, so `CommitCommandInUnitAsync` poisons
  the unit and throws `CommandClaimLostException` — the handler's writes and its completion roll back rather than
  commit beside another run's, and the request is answered `409`. What the run did outside the unit stands: a no-unit
  command's own autocommit writes and every external call are not undone by fencing. A stale release or answer changes
  nothing.
- **The fingerprint** is SHA-256 over the command name, the route's `id` and every bound argument that is not a
  service or request plumbing, serialised as JSON — uploaded files by name, length and content hash (Rulings 8, 52).
  The same key with a different fingerprint is `409 idempotency-conflict`: a deliberate deviation from the IETF
  draft's `422`, for one Alvo meaning across the Data API and commands (Ruling 7).
- **Answers** are replayed as status + JSON body (a problem document keeps `application/problem+json`), at most
  **64 KiB**; no headers, `Location` included. A result with no status, a plain `string`, an unserialisable value or a
  larger body is recorded committed without an answer, and a retry is refused `409` "do not resend" rather than run
  (Rulings 9, 49 L4).
- **Scope** is `command:<name>/<AlvoIdempotency.IdentityOf(caller)>/<API key id or ->` — per caller, tenant and
  credential (Ruling 34). A resolver of the host's own that sets no key id files every token of one user under `-`, so
  those tokens share replays. **Departure, recorded:** `AlvoIdempotency`'s Data API contract is "never a rendered
  response" — it replays by re-reading the row through the caller's current policy. A command has no row to re-read, so
  its ledger stores the rendered answer; the per-credential scope is what keeps a stored answer from reaching anyone
  but the credential it was given to.
- **Retention**: keys claimed more than **24 hours** ago are deleted by the hourly `LedgerSweeper`, on an index on
  `claimed_at`; a retry after that runs as a new request (Ruling 41). 24 h matches the IETF draft's "at least 24 hours"
  and bounds a table that otherwise grows with every keyed call (#115).
- Framework DDL is ensured once per process and table by `OnceDdl`, beside `SystemSchemaInitializer`'s boot creation
  (Ruling 47).

## Inbound webhooks

`MapAlvoWebhookReceiver(pattern, secretName, handler)` maps a `POST` whose outer delegate is `WebhookReceiver`; the
host's handler is built with `RequestDelegateFactory` and run over the replayed verified bytes, so it binds exactly
what was signed (Ruling 25).

- **Verification precedes dedup and the tenant** (Ruling 24): the three headers, the timestamp within `Tolerance`
  (5 minutes), the body within `MaxBodyBytes`, then a `v1,` HMAC-SHA256 over `id.timestamp.body` with one of the
  space-separated `whsec_` secrets (Ruling 22). An unverified request records no id — an attacker cannot burn a real
  sender's ids — and runs no host code. A failed check is `401 unauthenticated` with no `WWW-Authenticate`.
- **Keys of at least 24 bytes** (Ruling 54): every key of the stored value must decode to ≥ 24 bytes, Standard
  Webhooks' lower bound; an empty key — which anyone could sign with — or a short one refuses the whole value: `500`
  and an Error log naming the receiver, the secret's name and the reason, never a key.
- **The body cap applies** (Ruling 55): counted while read, so a body without a `Content-Length` is never buffered
  past the cap; the server's `IHttpMaxRequestBodySizeFeature` is only raised to `MaxBodyBytes`, never lowered, and a
  server's own `413` is answered as the receiver's. IIS and HTTP.sys keep their own limits.
- **Dedup** in `<prefix>_inbound_webhooks` (`receiver`, `webhook_id`, `claim_token`, `received_at`, `lease_until`,
  `completed_at`): a completed id is acknowledged `200` without running; one in flight is `409`; a handler that throws
  or answers non-`2xx` releases its id so the sender's retry runs it (Ruling 23). Completion and release are fenced by
  the claim token; a stale completion returns `false` and is logged (Ruling 36). Delivery stays at least once.
- **The tenant comes from signed bytes** (Rulings 37, 56). Standard Webhooks signs only id, timestamp and body, so the
  path, query and headers are attacker-changeable on a replayed delivery. `ResolveTenant` is asynchronous
  (`Func<HttpRequest, ReadOnlyMemory<byte>, CancellationToken, ValueTask<TenantId?>>`), is called only after
  verification and is given the verified bytes; a `null` answer falls back to `Tenant`; with neither the handler runs
  as `System(null)`, which every tenant-scoped entity refuses. The body is replayed before and after the resolver.
- **The sweep**: the hourly `LedgerSweeper` deletes ids received more than **7 days** ago, and command keys older than
  24 hours; a failed sweep is logged and retried, never fatal (Rulings 26, 41).
- **What the returned builder does** (Ruling 49 L8): it configures the outer endpoint; an endpoint filter never reaches
  the RDF-built handler, and the system caller is bound to the handler's `AlvoCaller` only, not published through
  `IAlvoContextAccessor`. Receivers are excluded from the OpenAPI document, are not catalogued (§4.13 has no kind for
  them and they bind nothing in the descriptor), and a pattern may be mapped once.

## What is internal and why

Three seams live in `MMLib.Alvo.Abstractions` as **internal** types reached through `InternalsVisibleTo`:
`ICommandLedger`, `IInboundWebhookLedger` and `IAlvoCommandUnit` (with `AlvoFrameworkEvent`). The core needs them and
the EF driver implements them, but a public ledger would be a store a host could forge into — write a committed
answer for a key, or a completed webhook id — and a public `IAlvoCommandUnit` would let any `IAlvoTransaction` claim to
carry a command's completion. `IAlvoSessionSchema` is the precedent: an internal Abstractions seam between the core and
its drivers, never a host extension point. The public surface is the verbs (`MapAlvoCommand`, `AddCommand`,
`MapAlvoWebhookReceiver`, `WithAlvoCaller`, `ForEntity`), the handler-facing types (`AlvoCommandContext`,
`AlvoCaller`, `IAlvoCommand<T>`, `IAlvoCommands`, `AlvoWebhookReceiverOptions`) and the read-only descriptions
(`AlvoEndpointMetadata`, `ManagementCommandVerdict`, the catalog's command members).
