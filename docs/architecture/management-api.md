# The Management API

The configuration surface: ten HTTP routes over one service, `IAlvoManagement`, that read and change what a
project **is** — its descriptor, its revision history, its resolved schema, what this build honours, and who
a policy would admit. It never reads or writes a row of application data. That is not an omission; it is
deviation **D4**, and the whole shape of this document follows from it.

Mounted by `MapAlvo()` under `Alvo:Management:RoutePrefix` (default `/management`), and closed: every route
carries the gate the descriptor's `access` block compiles, so a build honouring no `access` block admits
nobody but the deployment's **bootstrap administrator** — the one identity that sits above the descriptor,
because it is infrastructure configuration rather than a block a locked-out project could edit.

## The surface

| Route | `IAlvoManagement` member | Level |
|---|---|---|
| `GET {m}/info` | `GetInfoAsync` | `viewer` |
| `GET {m}/projects` | `ListProjectsAsync` | `viewer` |
| `GET {m}/projects/{project}/descriptor` | `GetDescriptorAsync` | `viewer` |
| `GET {m}/projects/{project}/revisions` | `ListRevisionsAsync` | `viewer` |
| `GET {m}/projects/{project}/revisions/{revision:int}` | `GetRevisionAsync` | `viewer` |
| `GET {m}/projects/{project}/schema` | `GetSchemaAsync` | `viewer` |
| `GET {m}/projects/{project}/capabilities` | `GetCapabilitiesAsync` | `viewer` |
| `POST {m}/projects/{project}/policy/simulate` | `SimulatePolicyAsync` | `viewer` |
| `PUT {m}/projects/{project}/descriptor` | `ApplyDescriptorAsync` | `developer` |
| `POST {m}/projects/{project}/revisions/{revision:int}/rollback` | `RollbackAsync` | `developer` |

**This table is generated from nothing.** It is prose, and prose drifts — so it is not what holds the
mapping. `ManagementContractTests` does, reflectively and in four directions: every member of
`IAlvoManagement` has a route, no route stands for two members and no member for two routes, every route
names an operation `ManagementOperations` requires a level for, and no management route reaches the
published OpenAPI document. Rename a member and the build breaks at the mapping site, because every route's
metadata is written with `nameof`; delete one and the test breaks. If this table and the code ever disagree,
the code is right.

**The level is not on the route.** A route carries a `ManagementOperation`; `ManagementOperations` is the
one table mapping an operation to the level it needs, and an operation it does not list requires `admin` —
the most restrictive answer, not the most convenient one. Three of the thirteen operations
(`ManageApiKeys`, `ManageUsers`, `DeleteProject`) have no route at all; see *What is deliberately absent*.

### The one place a route's level is not the whole answer

A `developer` may apply a descriptor. A `developer` may **not** apply one whose `access` block differs from
the applied one, and may not roll back to a revision whose block differs — both write **members** re-resolve
the requirement to `admin` in that case, through one shared expression inside `AlvoManagementService`, which
is what makes the rule hold on the in-process transport too. Spec §3.3 divides the levels as
*`developer` edits what the backend is, `admin` also decides who may reach it*, and `access` is the one
infrastructure-shaped block that lives inside the descriptor. Without the guard a `developer` promotes
itself by editing three lines of JSON, because every accepted apply re-primes the catalog the gate reads.

The rollback arm is the subtler half: a restore carries a **stored** descriptor the caller never had to
write, so any project whose history ever held a looser block would otherwise be a standing escalation at a
`developer`-gated route.

## One path, two transports

The dashboard calls `IAlvoManagement` in-process. An agent calls the HTTP routes. Spec §0.5 contract 4
forbids two paths for one decision — and this is not two paths, because **every route delegate is a thin
adapter**: it binds, calls one member, and renders. No business rule lives in `ManagementEndpoints`. The
rules that look like they live there — the `If-Match` precondition, the `?dryRun` reading, the repeated-key
refusal — are all *transport* concerns: they translate an HTTP request into the arguments the member already
takes. `ManagementApplyRequest` carries its expected revision and its idempotency key as **fields**, not as
headers, which is what makes an MCP adapter a mapping rather than a second implementation.

**Authorization used to sit in the endpoint layer, and none of it does any more.** Both halves — the level
table (`ManagementOperations`) and the §3.3 `access`-block comparison — are read by `AlvoManagementService`,
at the head of every contract member, so both transports meet the same gate. Leaving either in
`ManagementEndpoints` made it an HTTP-only rule: a dashboard resolves one registered `IAlvoManagement` and
serves many humans through it, so "whatever composed this reference already admitted the caller" admits the
*process*, not the person, and the in-process path escalated freely. `ManagementInProcessAccessTests`
measures the closed version over the in-process transport; every other access fact in the repo goes over
HTTP, which is why nothing saw it.

`RequireAlvoManagementAccess` stays on every route, and is now an **early rejection rather than the only
one**. It reads the same table through the same evaluator as the service, so it cannot answer differently;
what it adds is the moment — it refuses before model binding, so a caller with no level never costs a
descriptor parse. `ManagementEndpoints.Answer` renders `ManagementForbiddenException` as the same 403 the
filter returns, which is what makes the filter a cost optimisation rather than a load-bearing gate.

The consequence is worth stating plainly: **every in-process management call needs a published principal.**
An embedded host that holds `IAlvoManagement` and publishes nobody is the anonymous caller, and the
anonymous caller reaches no level — so it is refused, on reads as well as on writes. That is default-deny
applied to the surface that admits it does not know who is calling. A host that wants an unattended apply
publishes a caller the project's own `access` block admits, or applies through the boot.

## D3: `If-Match` carries `revision`, not an `ETag`

The Data API's optimistic concurrency is a strong `ETag` over a row version. The Management API's is an
integer: `If-Match: "7"`, where 7 is the descriptor's `revision`.

`revision` is already in the **frozen** `schema/project.schema.json:49` — *"Content revision counter,
incremented on every applied change; used for optimistic concurrency during apply."* Minting a second
concurrency token beside a frozen one would leave two answers in the repo for one decision, and an operator
reading the schema would have no way to tell which one an apply honours. Same mechanism (`If-Match`),
different source.

**No `ETag` is emitted.** There is no row version to mint one from — a descriptor is not a row, and the
revision is not a hash of its bytes. A response header carrying a value the server cannot recompute from
what it stored would be a second token in all but name. A caller reads the current revision from
`GET {m}/projects/{project}/descriptor`, which is the same response it needs anyway to know what it is
editing.

`W/` tags, `*`, and a list of tags are all **uncomparable** rather than ignored: a weak tag is by definition
unusable for a write precondition (RFC 9110 §8.8.3), `*` means "any current representation" — which no
revision comparison can honour — and a list names more than one.

## `If-Match` is required, and 428 is why

The Data API's rule is *every precondition this API cannot evaluate is refused, never ignored*. Applied to
the header's **absence**, that gives 428 `precondition-required` (RFC 6585 §3) rather than a default.

An absent `If-Match` read as "revision 0" is a lost update on the one document that defines the whole
backend, with nothing anywhere to detect it. And the two refusals must stay distinguishable: 428 means *you
sent no precondition, read one and send it*; 412 `precondition-failed` means *you sent one and it does not
hold*. Collapsing them answers "you sent the wrong revision" to a caller who sent none — the one wording
they cannot act on. `IfMatchRevision` is three states for exactly that reason, not an `int?`.

## The two added slugs

`data-api.md` §"The status and `type`-slug catalogue" states the slugs are exactly `AlvoProblemTypes.All`.
This surface added two to that list, and each is a different **fix** from every slug already there — which
is the rule the catalogue splits on, not the status code.

| Status | Slug | Fix |
|---|---|---|
| 409 | `destructive-change` | resend with an explicit destructive allowance, or send a descriptor that keeps what the plan would drop |
| 428 | `precondition-required` | read the current revision and send it as `If-Match` |

`destructive-change` is the **third** 409. An `idempotency-conflict` is repaired with a fresh key, a
`conflict` with a different value, and this one with the same request plus consent. A caller that cannot
tell the three apart retries the wrong one forever. It is not `validation`: nothing about the descriptor is
malformed — it is a well-formed request colliding with data already stored, which is what 409 means.

`precondition-required` is distinct from `precondition-failed` for the reason the section above gives.

## `Idempotency-Key`: safety versus attribution

Honoured on both write routes (`PUT descriptor`, `POST rollback`), on the Data API's header spelling and
through the same `{prefix}_idempotency` table — no new table, no `AlvoFrameworkTables` change.

**The key adds no safety. It adds attribution, and that is the whole argument for building it.** The
required `If-Match` revision already gives at-most-once: a replayed apply loses the race against the
revision it advanced and is refused. What it cannot give is *why*. When a 200 is lost in transit and the
caller retries, the 412 they get means both "my own write landed" and "somebody else changed it under me" —
the same answer with opposite recoveries, and nothing in the response tells them which. That is what the
key removes.

This inverts the Data API's own reasoning for ignoring the header on `PATCH`/`DELETE`, deliberately: there
the replayable result is a whole record nobody stored, here it is one integer, and the table already exists.

**What is stored is the revision, never a body.** A record is filed under (key, caller identity, request
fingerprint). The fingerprint covers the operation, the project, the expected revision, the destructive
allowance and the payload — so a retry that flipped `allowDestructive` is a different request and replays
neither the refusal nor the success, and one key spent on an apply cannot be answered with a rollback's
revision. The descriptor is hashed **as sent**, not canonicalised: a descriptor is stored verbatim and
exported verbatim, so two spellings are two different things to store.

**The scope is the caller's own identity**, so a 409 `idempotency-conflict` always means *you have already
used this key for something else* and never *somebody has*. An anonymous caller has no identity to scope by
and is refused the key rather than sharing a key space.

**A key on a `?dryRun=true` request is refused, and not spent.** A preview has no revision to record and
none to replay; accepting the key would turn the caller's later real apply into a replay of a preview,
reporting a revision nobody appended.

### A replay says so: `Replayed: true`

A replay answers `applied: true` — a revision *was* appended, by the request this one repeats — with
`replayed: true` beside it, which is how a caller tells the two apart without changing what `applied` means
for everyone else.

**`plan.isEmpty` carries its published meaning only when `replayed` is false.** A replay reports an empty
plan with no steps, because the original plan was not stored (the response owed a diff; the history did
not) and cannot be re-planned from a base that has moved. On a replay, `plan` describes what *this request*
did, which is nothing — not what the revision applied. A dashboard rendering "no changes" off a replay is
reading the wrong field; `GET {m}/projects/{project}/revisions/{n}` is where what a revision applied is
read from.

### The post-commit window, stated plainly

The record is written **after** `ApplyAndAppendAsync` commits. That writer owns its transaction and exposes
no seam to enlist in, so a crash between the commit and the record leaves the attempt unrecorded, and the
retry gets the unattributable 412 again. The key narrows the window; it does not close it. Closing it needs
a widened writer, and the cost is recorded here rather than left to be rediscovered.

A failure to file the record does **not** fail the write: the migration and the revision are already
committed, and telling the caller their apply failed when it landed is the exact confusion the key exists to
remove, inverted. It logs a warning naming the revision. The consequence is the documented one and nothing
more — the retry is unrecorded, so it is that same 412. Two costs follow, and both are open: a permanently
misconfigured store gets one warning per apply and silently no key semantics, and a key is spendable
forever because nothing retires a record.

## Dry run is `PreviewAsync`, not `MigrationOptions.DryRun`

`MigrationOptions.DryRun` exists and the runtime path **refuses** it. `RuntimeSchemaService` calls
`RejectDryRun` first thing in both `ApplyAsync` and `RollbackAsync`, because `IRuntimeSchemaWriter` applies
and appends in one atomic step and there is no seam to preview from without mutating. Its refusal message
already points callers at *"a plan-only operation"*.

`?dryRun=true` is that operation. It runs `RuntimeSchemaService.PreviewAsync`, which produces the migration
plan and the guardrail verdict without touching the database, and it is one mechanism with three consumers:
the schema editor's diff, the rollback preview, and the later AI proposal card.

**`?dryRun=` is tri-state, and a value this API cannot read is 422.** `?dryRun=yes` asks for a preview, and
answering that with a committed schema change is the one outcome a dry run exists to make impossible. A bare
`?dryRun` is unreadable too: the allowance is explicit or it is not given. An **unknown query key** is
refused for the same reason one character over — `?dry_run=true` asks for a preview and would otherwise
commit the change.

### The apply path plans twice, and what that buys

Both write routes call `PreviewAsync` and then, when it is not a dry run, apply — which plans again inside
`ApplyAsync`. That is deliberate.

`RuntimeSchemaService.ApplyAsync` does not return its plan, and the response owes the caller a diff.
Planning is a pure function of two schemas and takes no lock, so the second call costs a diff and buys the
editor's confirmation view — the same view the dry run renders, from the same code, which is what keeps a
preview and the apply that follows it from disagreeing. The alternative is widening `ApplyAsync`'s **public**
return type for a rendering convenience, which is a breaking change to a type an embedded host already
consumes.

If the second plan ever shows up in the load gate, widen it then. Until then the cost is one extra plan per
management write — a surface whose traffic is a human or an agent editing configuration, not a request path.
The reverse plan is the one an operator most wants to see anyway: it is the list of what a restore is about
to drop.

## An API key's `scopes` govern no management request (D7)

An `ApiKeyScope` is `<entity|*>:<read|write>`. Management admission is decided by the descriptor's `access`
block and the bootstrap administrator, on **roles alone** — the key's scopes are not consulted anywhere on
this surface.

There is no spelling for "may manage this project": the surface is not an entity and has no read/write pair.
Inventing one would put a second authorization answer for configuration beside `access`, which is the
divergence contract 4 exists to prevent, and it would mean a project's administrator could be locked out by
a credential setting they do not edit.

**The consequence is stated rather than left implicit: a key narrow enough to be refused by the Data API
still reaches management if its roles match a level.** A key scoped to `["orders:read"]` — an entity the
descriptor may not even declare — gets 200 on `GET {m}/info` when its roles satisfy `access.viewer`. That is
pinned over the wire by
`ManagementAccessTests.A_key_scoped_to_one_entity_still_reaches_management_because_scopes_do_not_govern_configuration`.
#146's review recorded this as #212's question to decide; this is the answer. Revisit if a
management-shaped scope is ever wanted, and note it would then need a default for every key already issued.

## `info` reports the data provider, not the engine

`GET {m}/info` answers `{ version, mode, dataProvider, startupMode }`. `dataProvider` is the **registered
port implementation's type name** — `EfAlvoData`, `InMemoryAlvoData` — or `"none"` when no driver is
registered, which is a supported composition.

It is not `"postgresql"` or `"sqlite"`, and it cannot be. The core may not reference the adapter that knows
an engine's name: that is the provider-model principle, and `IAlvoData` deliberately exposes no engine
identity. Reporting one would mean either a `switch` over type names in the core — the engine-specific `if`
that principle forbids — or a new port member whose only consumer is a diagnostic string.

`mode` is `standalone` or `embedded`, two values and no more; it is computed from the registered `AlvoMode`,
whose default is `Standalone`, which is why the standalone image needs no configuration to describe itself.
`AlvoManagementOptions.ModeLabel` is `internal` precisely so `mode` stays a two-valued contract an agent can
branch on.

## What is deliberately absent

**Data (D4).** There is no route here that reads or writes an application row, and no admin bypass at all.
The analysis asks for a bypass *"ale každá operácia ide do audit logu"* — and the audit log is #42, in F7. A
bypass that cannot be audited is the thing that sentence exists to prevent. The dashboard browses data
through the ordinary Data API under the caller's own context, so no privilege exists to audit.

**The record-id arm of the simulator.** The F5 design's §2.2 used to say the simulator takes *"optionally
a record id"*; it does not, this document was that correction's home, and **the design has since taken the
correction back** — §2.2.1 states it there, because a drawing was built against the old line and took a
whole screen's shape from it. Evaluating `USING` against a **stored row** needs a read, and a read through
the Management API is the data surface D4 refuses to create. A caller who wants to know whether one row
passes fetches it through the Data API under the simulated caller's own credential, which is the production
answer by construction rather than a reimplementation of it.

The design adds one consequence this document did not draw out, and it belongs here too: **a client that
scores a stored row is a second policy evaluator**, and it fails the acceptance criterion *"the simulator
answers identically to production"* by construction — whatever it answers. The moment it disagrees with
`IPolicyEngine` over a null comparison, over role-name ordinality or over the tenant guard's precedence, it
is wrong with total confidence. So a per-record allowed/refused verdict is not a feature this surface is
missing; it is one no client of it may build.

What the simulator does answer is the policy engine's own verdict, from the same `IPolicyEngine` singleton
the production read path resolves through — never a copy. Two consequences follow and are deliberate:
`allowed` means *the engine resolved a policy at all*, not *this caller will see rows* (a role predicate is
handed back rather than evaluated), and an unknown entity answers 200 with the engine's denial, because the
engine makes "no such entity" indistinguishable from "not authorised" and reproducing that verbatim is what
"the same engine" means.

**`branding`, from both capability lists.** `GET {m}/projects/{project}/capabilities` reports three arrays:
`honoured` (`entities`, `auth`, `tenancy`, `access`, `formats`), `warned` (the five unhonoured subsystems,
each with the framework's own sentence for what does not happen), and `refused` (the features an apply
rejects outright). The block question is decided by the first two, and they are deliberately not the whole
schema: `branding` is in neither. It is not warned, because an
author who writes it and sees no logo has looked and found out, and it is not honoured, because nothing
renders it — claiming it would be the lie the capability report exists to prevent. The descriptor's metadata
keys (`name`, `revision`, `apiVersion`, `description`) and `$schema` are not subsystems and are in neither
list for the same reason. **Nothing holds the two lists to partitioning the schema**, so a new top-level
block lands in neither silently; that is filed rather than fixed here.

**The three `admin` operations with no route.** `ManageApiKeys`, `ManageUsers` and `DeleteProject` are in
the level table and have no HTTP surface. Key and user administration belongs to `MMLib.Alvo.Identity`
(§3.4); project deletion is the danger zone, and neither is in #212.

**Multi-project.** `ListProjectsAsync` answers a list because the wire shape must not change when a second
project becomes possible (§2.6); this build serves exactly one, and every parameterised route answers a
named 404 for any other name.

**An OpenAPI document of its own.** Every management route carries `ExcludeFromDescription`. The document
Alvo publishes is the **generated** Data API contract, pinned three ways — `OpenApiDocumentTests`' snapshot,
`scripts/lint-api` against `schema/openapi-ruleset.yaml`, and the TeaPie e2e suite's **path-set equality**.
Mixing a hand-written admin surface into it would move all three for a reason that has nothing to do with
the Data API, and the e2e break would name no cause. A document of its own is the follow-on.

**No log line, and no throttle.** No management request is logged — not the read, not the apply, not the
refusal. Audit is #42 in F7, by design (D4), and a half-audit here would be a second, thinner answer to the
question #42 owns. The surface writes exactly one log record in total, and it is not an audit record: a
warning when a write landed and its idempotency key could not be filed, because that is the one state an
operator has to reconcile by hand.

Nothing rate-limits it either. `MapAlvoManagementApi()` returns an `IEndpointConventionBuilder` over the
management routes and nothing else, so `RequireRateLimiting` is one call a host makes over the surface it
chose to mount; `MapAlvo()` discards that builder, and a host that wants the convention maps the three
pieces itself. Neither absence is pinned by a test; they are recorded here because an operator planning a
deployment needs them.

**A `WWW-Authenticate` challenge *is* sent**, and this corrects a review note that said otherwise. A 401
here comes from the shared `ProblemResultFactory.Unauthenticated`, which appends
`WWW-Authenticate: AlvoApiKey header="<the configured header>"` exactly as the Data API's does — RFC 7235
§3.1 makes it a MUST, and it is what lets an agent discover how to authenticate instead of guessing. Now
measured rather than argued, by
`ManagementAccessTests.A_management_401_names_the_scheme_and_the_header_to_send`.

## Configuration

One key: `Alvo:Management:RoutePrefix`, default `/management`, environment spelling
`Alvo__Management__RoutePrefix`. **Never `ALVO_*`** — that is deviation D2, following `Alvo:Schema` and
`Alvo:Events`, which deviated the same way; a third spelling would be worse than either. #233 owns the
vocabulary question globally.

The prefix normalises exactly as `AlvoApiOptions.RoutePrefix` does — `management`, `/management` and
`/management/` mount in one place — with one difference: it may **not** reduce to the empty string, because
this surface owns literal path segments (`info`, `projects`) that would shadow an entity route at the root.

## Alternatives rejected

**A second authorization seam for management (`IManagementAccessPolicy`).** An earlier plan proposed one,
with a deny-all default. #146 had already shipped `ManagementLevel`, `ManagementOperations`,
`ManagementAccessEvaluator` and `RequireAlvoManagementAccess`. Two authorization seams in one codebase is
the divergence contract 4 exists to prevent, and the shipped one already delivers the default-deny the
proposal wanted.

**A strong `ETag` over the descriptor's bytes.** Rejected under D3: `revision` is frozen in the schema as
the concurrency token, and a hash would be a second answer to a decided question. It would also make a
formatting-only edit a conflict, which `revision` correctly does not.

**`MigrationOptions.DryRun` on the apply path.** Rejected by the runtime path itself, and rightly: applying
and appending are one atomic step, so there is no seam to preview from without mutating. A flag read as "do
nothing" on a path that cannot honour it is how a preview becomes a real apply.

**Mounting the surface only on an explicit opt-in.** Considered, and dropped once the gate landed: the
routes are default-deny and harder to reach than the Data API's, so a mounted-but-closed surface costs a
host nothing and saves an embedded host from discovering a second call it has to make. A host that wants it
truly absent maps `MapAlvoHealth()` and `MapAlvoDataApi()` instead of `MapAlvo()`, which is the same seam
it already uses to pick route groups.

The decision stands, and the case that argues against it is not the empty `access` block — it is the
**non-empty** one. `access` was previously parsed and not honoured, and a host could have written a block
that did nothing but earn a capability warning. Upgrading to this build makes that block live *and* mounts
`/management` under `MapAlvo()`, so a deployment gains a working configuration surface — read and write —
with no code change, no configuration change, and nothing in its own repository that moved. We take that
cost knowingly: the block that comes alive is the one the operator wrote, the capability report warned that
it was inert, and a surface admitting exactly whom the operator named is the outcome they asked for. It is
stated here so an upgrade is a decision rather than a discovery.
