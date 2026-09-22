# F5 — the admin dashboard: what it stands on, and where the line between real and drawn runs

*Design, 2026-09-18. F5 (milestone #6). Covers #27 and its five sub-issues (#227–#231), and
settles three prerequisites they depend on: #212 (Management API), #146 (`access` enforcement),
and an identity subsystem that has no issue at all.*

## 0. Why this document exists, and what it is not

`#227`'s own body says the shell *"blocks every other part"* and that *"nothing blocks it"*.
That was true when it was written. It is not true any more, and this design is where that gets
recorded rather than discovered mid-PR.

Two facts move it:

1. **`src/MMLib.Alvo/Descriptor/Internal/UnhonouredSubsystems.cs` carries an `access` entry whose
   own doc comment sets a trigger**: *"`access` governs an administration surface that does not
   exist in this build at all… the day the surface lands, `access` is either honoured or refused
   — never warned about."* The first PR that ships an admin surface invalidates that warning.
2. **The descriptor has no write path that is not the Management API.** Spec §0.5 contract 4
   (`alvo-specifikacia.md:94`): *"Jedno Management API: dashboard aj CLI sú klienti toho istého
   API."* The Management API does not exist.

This document is **not** a UI specification down to the pixel. The visual work already exists as
a drawing — a Claude design-system project (`Alvo Admin.dc.html`, 10 routes, both themes, a full
mobile branch) that this design treats as the reference and converts into a token system in §5.
What this document owes is the **spine**: which packages, which contract, which authorization
model, and — the part the drawing cannot answer — **which of the ten drawn screens this build can
honestly serve.**

### 0.1 What the sources ask for

| Source | The requirement, verbatim | Bearing |
|---|---|---|
| `baas-analyza.md:524` | *"Pre agentov a CLI: **Management API** — všetko, čo vie dashboard, vie aj API"* | the operation inventory **is** the dashboard's own list |
| `alvo-specifikacia.md:94` | *"**Jedno Management API:** dashboard aj CLI sú klienti toho istého API"* | forbids a divergent **write** path, not a read |
| `baas-analyza.md:554` | *"Ten istý project descriptor prejde všetkými štyrmi cestami… s identickým výsledkom"* | four doors, one result — an acceptance criterion |
| `baas-analyza.md` §2.8 | *"dashboard je **prvý dojem produktu**… musí sám vyzerať moderne a hotovo, nie ako vygenerované CRUD lešenie"* | the visual audit is a gate, not taste |
| `baas-analyza.md` §2.8 | *"Vlastný moderný dizajn, NIE default Blazor/Bootstrap look… Prístupnosť (WCAG AA)"* | §5 |
| `alvo-specifikacia.md:353` | *"všetko naklikateľné v UI je exportovateľné ako kód (žiadny config drift)"* | §6 criterion 3 |
| `schema/project.schema.json` → `access` | *"a CEL predicate over the closed context `@user` exposes — `@user.id` and `@user.roles`, nothing else"* | §3 — the frozen schema already settled #146's open question |

### 0.2 Deliberate deviations from the sources

Stated here so a later reader can tell a decision from an oversight.

| # | Deviation | Reason |
|---|---|---|
| D1 | Analysis §2.8 recommends a **mature Blazor component library** (MudBlazor / Radzen / FluentUI) re-themed. This design uses **no component library.** | The drawing is fully custom — own tokens, own radii, own density scale. Re-theming a library to match it is, measured against the drawn components, close to rewriting it, and it buys a heavy dependency inside the `mmlib/alvo` image. Blazor's in-box `Virtualize` covers the one capability (grid virtualisation) that was the library's strongest argument. |
| D2 | Spec §X.1 sketches `ALVO_ADMIN__PATH`, `ALVO_ADMIN_EMAIL`, `ALVO_ADMIN__ALLOWED_IPS`. This design uses the **`Alvo:*` spelling the host already binds.** | `host.md` §Configuration already deviated once, deliberately: *"The container form is the standard .NET double-underscore spelling… not the `ALVO_*` names spec §X.1 sketches."* Inventing a third spelling here is worse than either. **#233** owns the vocabulary question globally; this design follows what exists and defers to it. |
| D3 | The Management API's optimistic-concurrency token is `revision` (an `int`), not a strong `ETag` over a row version as the Data API uses. | `revision` is already in the **frozen** `schema/project.schema.json:49` — *"used for optimistic concurrency during apply"*. Minting a second concurrency token beside a frozen one would leave two answers in the repo for one decision. Same mechanism (`If-Match`), different source. |
| D4 | Analysis §2.8 requires *"admin bypass politík… ale každá operácia ide do audit logu"*. This design ships **no admin bypass at all** in F5. | The audit log is **#42 (F7)**. A bypass that cannot be audited is the thing the sentence exists to prevent. The dashboard browses data through the ordinary Data API under the caller's own context, so no privilege exists to audit. §2.4. |
| D5 | The drawn `Activity` screen is a system-wide event feed. This design ships it as **`Configuration history`** — the descriptor's append-only revisions. | Data-level audit is #42. `DescriptorVersion` (`Revision`/`CreatedAt`/`Author`/`Reason`/`RolledBackFrom`) is a real, complete audit trail of configuration, and it currently has no consumer anywhere in the product. §4.4. |
| D6 | The drawing's light accent is `#128a52` on white text. This design uses `#0f7a48`. | Measured: `#128a52`/`#ffffff` is **4.39 : 1**, below WCAG AA's 4.5 : 1 for normal text, and the drawn primary buttons set 12.5–13 px — normal text, not large. `#0f7a48` is **5.39 : 1** and is already in the drawing's own palette as `--won-fg`, so the palette does not widen. §5.3. |
| D7 | An API key's `scopes` gate every Data API request. They gate **no** Management API request: management admission is decided by the descriptor's `access` block and the bootstrap admin, on **roles alone**. | An `ApiKeyScope` is `<entity\|*>:<read\|write>`, so there is no spelling for "may manage this project" — the surface is not an entity and has no read/write pair. Inventing one would put a second authorization answer for configuration beside `access`, which is the divergence contract 4 exists to prevent, and it would mean a project's administrator could be locked out by a credential setting they do not edit. The consequence is stated rather than left implicit: **a key narrow enough to be refused by the Data API still reaches management if its roles match a level** — `ManagementAccessTests.A_key_scoped_to_one_entity_still_reaches_management_because_scopes_do_not_govern_configuration` pins it over the wire. #146's review recorded this as #212's question; this is the answer. Revisit if a management-shaped scope is ever wanted, and note it would then need a default for every key already issued. |
| D8 | Spec §308 and §415 say the contract lint is *"Go binárka v CI (**žiadny Node**)"* / *"žiadny Node v pipeline"*, and `docs/PLAN.md` §3a repeats it. The F5 design prototype's scenario suite **is** Node. | Those sentences are about the **contract lint** — they exist so `vacuum` is a pinned Go binary rather than Spectral-on-npm, and that stays true: `scripts/ensure-vacuum` still resolves a checksum-verified Go binary and `scripts/lint-api` still runs it. What is added is a second thing in a second job: a browser-driven suite over a **drawing** (`docs/design/f5-admin`), which contains no .NET, produces no artifact anything ships, and cannot be written in the Go binary's language or in xUnit without standing up a browser from .NET anyway. It is paths-filtered, it is **outside** the required `Build & test` gate, and it retires with the prototype it drives. The sentence the sources were protecting — *the API contract lint does not depend on the npm ecosystem* — is untouched. Recorded here rather than left as a silent widening, because "no Node in the pipeline" is the kind of line a later reader will hold the repository to. |

---

## 1. Package boundary

### 1.1 What is earned and what is not

`docs/architecture/package-boundary.md` §The rule (hard): a package is justified only by **(a)** a
foreign/heavy dependency, **(b)** a real swap point, or **(c)** a different distribution/licence
policy.

| Component | Decision | Rule |
|---|---|---|
| **`MMLib.Alvo.Admin`** | **new package** | **(a)** — Blazor. An embedded host that wants only the Data API must not acquire it. `package-boundary.md` §Illustrative example already names `MMLib.Alvo.Admin` *(Blazor — heavy dep)*. |
| **Management API** | **not a package** — a vertical slice at `src/MMLib.Alvo/Management/` | none of (a)/(b)/(c). Minimal-API delegates over services already in the core. `package-boundary.md` §Consequence **lists "Management API" by name** among what lives in the one large core package. |
| **`MMLib.Alvo.Identity`** | **new package** | **(a)** — ASP.NET Core Identity plus its EF stores; and **(b)** — identity is a genuine swap point, already exercised: `samples/MMLib.Alvo.Samples.EmbeddedHost` resolves its callers through its own cookie auth and `IRoleCatalogProvider`. |
| **`MMLib.Alvo.Cli`** | **not now** | #213 is its own issue. F5 does not need it. A package is earned when its turn comes. |

`MMLib.Alvo.Host` gains references to `MMLib.Alvo.Admin` and `MMLib.Alvo.Identity`. That is the
same category it already uses to justify Scalar and both database drivers: a hosting decision, in
the one project that is `IsPackable=false` and therefore hands nothing to a NuGet consumer.

### 1.2 The boundary the whole design rests on

**`MMLib.Alvo.Admin` must not hold a project reference to `MMLib.Alvo`.**

That is what turns *"dashboard and CLI are clients of the same API"* from a promise into a
structural fact. An architecture test holds it, in the same family as the one that already keeps
the core EF-free.

But a Blazor Server app issuing HTTP to its own process is waste. The resolution is **one
application service, two transports**:

```
src/MMLib.Alvo.Abstractions/Management/
  IAlvoManagement              — the one operation contract

src/MMLib.Alvo/Management/
  AlvoManagementService        — the implementation: RuntimeSchemaService,
                                 IDescriptorVersionStore, ISchemaRegistry, IPolicyEngine
  ManagementEndpoints          — minimal-API delegates over that same service

src/MMLib.Alvo.Admin/          — depends on IAlvoManagement (Abstractions) only
```

Standalone: the Admin package receives `IAlvoManagement` from DI, in-process. An external agent,
the future CLI, and the future MCP adapter: the same contract over HTTP. **One path, two
transports.** Contract 4 forbids a divergent *path*; it does not require serialisation.

The risk in this shape is drift — an operation reachable in-process and not over HTTP. §6 pins it
with a contract test: **every member of `IAlvoManagement` has an HTTP route.**

### 1.3 Ordering consequence, and a correction to #227

`#228`'s body is right and this design adopts it: *"a read-only dashboard over `ISchemaRegistry`
+ `IAlvoData` violates nothing the sources state"* — contract 4 binds the **write** path. `#212`'s
own body agrees in as many words.

So the read half is not blocked on the Management API. What **is** hard-ordered before any admin
surface is reachable is §3: identity, and `access` enforcement. The sequence is therefore:

```
identity + IAlvoUserStore ──► #146 (access) ──► #227 (shell) ──► #228 (read-only browser)
                                                                        │
#212 (Management API) ──────────────────────────────────────────────────┴──► #229 ──► #230 ──► #231
```

`#212` is independent of the identity chain and may land in parallel with it; it is required
before #229 and #230, which are the first screens that write. `#228` reaches its data through the
Data API and the read half of §2.2, neither of which needs the write path.

**This design is a phase, not a pull request.** It settles the spine every sub-issue shares; each
of #212, the identity issue, #146 and #227–#231 then gets its own implementation plan under
`docs/superpowers/plans/`, and its own PR.

---

## 2. The Management API

### 2.1 What already exists

`#212` is closer to *"expose existing service operations over minimal-API delegates"* than to a
new subsystem. Verified against the code:

| Need | What ships today |
|---|---|
| runtime apply | `RuntimeSchemaService.ApplyAsync(project, descriptorJson, expectedRevision, options, ct)` |
| **dry run** | **does not exist on the runtime path — see §2.1.1** |
| destructive gate | `MigrationOptions.AllowDestructive` |
| rollback | `RuntimeSchemaService.RollbackAsync(project, targetRevision, options, ct)` |
| append-only history + optimistic lock | `IDescriptorVersionStore` (`GetCurrentAsync`/`GetAsync`/`ListAsync`/`AppendAsync(…, expectedRevision, …)`) |
| atomic DDL + version insert | `IRuntimeSchemaWriter.ApplyAndAppendAsync` |
| export runtime → file | `DescriptorVersion.DescriptorJson` **is** the export |
| resolved schema | `ISchemaRegistry.GetSchema()` |
| policy evaluation | `IPolicyEngine`, `IPredicateEvaluator`, `IPredicateRenderer` |
| record counts | `AlvoQuery.IncludeTotalCount` → `AlvoPage.TotalCount` (opt-in) |

### 2.1.1 Correction: the runtime path has no dry run

**An earlier draft of this section was wrong on a load-bearing point**, and the correction is
recorded rather than quietly edited, because a plan was written against it.

That draft read `MigrationOptions.DryRun` as "already a member" and called it *"the load-bearing
find — one mechanism with three consumers"*. The member exists; the runtime path **refuses** it.
`RuntimeSchemaService` calls `RejectDryRun` first thing in both `ApplyAsync` and `RollbackAsync`,
and its own doc comment gives the reason:

> *"The runtime path has no dry-run: `IRuntimeSchemaWriter` applies and appends in one atomic step,
> so there is no seam to preview from without mutating. It is refused rather than ignored, so a
> caller expecting a no-op preview does not get a real apply."*

That is a good design, not a gap to route around — and the refusal message already points callers
at *"a plan-only operation"* that does not exist.

**So it has to be built.** `RuntimeSchemaService` gains a `PreviewAsync` that produces the
migration plan and the guardrail verdict without touching the database, and `?dryRun=true` is built
on that rather than on `MigrationOptions.DryRun`, which stays refused on the applying path. The
claim the draft made survives the correction — the schema editor's diff, the rollback preview and
the later AI proposal card are **one mechanism with three consumers** — but the mechanism is new
work in #212, not a member already sitting there.

The cost is stated where it lands: the apply path plans twice, once through `PreviewAsync` for the
response's diff and once inside `ApplyAsync`, because `ApplyAsync` does not return its plan and
widening its public return type for a rendering convenience is a breaking change.

### 2.2 Surface

Route prefix `{m}`, default `/management`, bound from `Alvo:Management:RoutePrefix` (D2).
Default-deny applies exactly as it does to the Data API: unreachable without an explicit policy.

**Descriptor — the one write path to configuration**

| Route | Notes |
|---|---|
| `GET {m}/projects` | project list |
| `GET {m}/projects/{p}/descriptor` | current descriptor JSON + `revision`. **This is the export** — `DescriptorVersion.DescriptorJson`, not a new serialiser |
| `PUT {m}/projects/{p}/descriptor` | apply. `If-Match` carries `revision` (D3) and is **required** — an absent precondition is `428`, on the Data API's own rule that a precondition this API cannot evaluate is refused rather than ignored. `?dryRun=true` returns the migration plan and guardrail verdict without touching the database, through the new `PreviewAsync` of §2.1.1. `Idempotency-Key` honoured |
| `GET {m}/projects/{p}/revisions` | append-only history |
| `GET {m}/projects/{p}/revisions/{n}` | one revision — the export of a past state |
| `POST {m}/projects/{p}/revisions/{n}/rollback` | reverse migration; `allowDestructive` explicit, never implied |

**Schema and honesty**

| Route | Notes |
|---|---|
| `GET {m}/projects/{p}/schema` | the resolved `SchemaModel` — what the Data API actually serves |
| `GET {m}/projects/{p}/capabilities` | what this build honours — §2.3 |

`descriptor` versus `schema` is the same idea one layer down: the descriptor is what the author
wrote; `SchemaModel` is what survived. Where they differ is exactly where *declared but not
honoured* lives.

**Policy**

`POST {m}/projects/{p}/policy/simulate` — entity, operation, a simulated `AlvoContext` (user id,
roles, tenant) → the engine's verdict and the predicates it resolved.

The DoD says the simulator *"answers identically to production"*. The only way that is not a
promise is that the simulator calls **the same `IPolicyEngine`**, never a copy — and §6 pins it by
comparing the simulator's verdict against the Data API's actual response for the same context.

#### 2.2.1 Correction: there is no record id, and a client that supplies one is a second evaluator

**An earlier draft of the line above read *"optionally a record id"*, and it was wrong**, the same
way §2.1.1's draft was wrong about `MigrationOptions.DryRun` — so it is corrected here rather than
edited away, because a drawing was built against it and got the whole screen's shape from it.

`ManagementPolicySimulation` is `(Entity, Operation, Caller)`, and its own remark says why:

> *"There is deliberately no record id. Evaluating a predicate against a stored row needs a read,
> and a read through the Management API is the data surface deviation D4 refuses to create — a
> caller who wants to know whether one row passes fetches it through the Data API under the
> simulated caller's own credential, which is the production answer by construction."*

`docs/architecture/management-api.md` §"The record-id arm of the simulator" already names this
document as the place the correction belongs. This is that place.

**What a client may render**, and it is more than it looks: `Using`, `WithCheck` and `TenantScope`
as CEL source, `HiddenFields`, `ReadOnlyFields`, and `DenyReason` when the engine refused outright.
Plus one sentence the shape forces and the product badly needs — `ManagementPolicyVerdict.Allowed`
means *the engine resolved a policy at all*, **not** *this caller will see rows*:

> *"A rule over `@user.roles` is a predicate the engine hands back rather than evaluates, so a
> caller no rule admits still earns `true` here together with a `Using` none of their rows
> satisfies. A client that rendered this alone as 'permitted' would be wrong exactly where it
> matters."*

**So the screen's job is to explain a predicate, not to score a row**, and it has four things to
say that are true for every caller at once: which of the four 403 causes applies if any
(`data-api.md` §The decision procedure — unknown entity, the tenant guard, an unconfigured
operation, a missing required context value), what the `USING` predicate is, that failing it on
`list` is *200 with a shorter page* and on `get`/`update`/`delete` a *404*, and which fields drop
out for this caller.

**A per-record allowed/refused badge is forbidden by construction**, and this is §6.3 criterion 4
restated as a UI rule: a client that decides a stored row's fate is a second policy evaluator, and
the moment it disagrees with `IPolicyEngine` — over a null comparison, over role-name ordinality,
over the tenant guard's precedence — the dashboard is teaching the wrong thing with total
confidence. The honest affordance is *"open this row through the Data API as yourself and compare"*,
because the operator holds their own credential and holds nobody else's.

**Meta**

`GET {m}/info` — build, mode (standalone/embedded), **the registered data provider**, startup mode.
Not the database engine: `ManagementInfo.DataProvider` is the `IAlvoData` implementation's type
name, and `management-api.md` §"`info` reports the data provider, not the engine" argues why the
core may not know one. A dashboard badge reading *"PostgreSQL 16"* has no source.

### 2.3 `capabilities`: one source of truth for "not yet"

```json
{
  "honoured": ["entities", "rules", "hooks", "tenancy", "auth"],
  "warned": [
    { "block": "automation",
      "consequence": "no rule is ever evaluated, so no declared action runs — which looks exactly like a condition that never matched" }
  ],
  "refused": [
    { "slot": "field.default",
      "consequence": "Field 'default' is not honoured yet: no column default is emitted and the value is dropped before any writer sees it…",
      "fix": "…" }
  ]
}
```

`warned` is projected from `UnhonouredSubsystems.All`; `refused` from `UnhonouredFeatures`, which
gains an `EveryRefusal` enumeration — today the refused slots are reachable only one by one, as
named static members.

**Two corrections to the sketch above, both found while planning #212.** There is **no `issue`
field**: `UnhonouredSubsystem` carries no issue number, and some consequences name one in prose
while others do not, so minting one would be inventing data. And **`honoured` is a written list,
not a derivation** — nothing in the build enumerates what it *does* honour. It is held honest by a
test asserting it is disjoint from `UnhonouredSubsystems.All`, which is weaker than derivation and
is the strongest thing available; the badge the dashboard actually draws comes from `warned` and
`refused`, which *are* derived.

**The prose is served verbatim and never rewritten in the UI.** Those sentences are deliberate,
already covered by tests, and already asserted against the frozen schema. A second wording inside
a Razor component would be a third spelling of one truth — the same failure mode D2 avoids.

The consequence that makes this worth an endpoint: when the PR that implements automation deletes
the entry from `UnhonouredSubsystems`, the badge disappears from the dashboard **without anyone
touching the dashboard.**

### 2.4 Data is deliberately not in this API

The data browser uses the **existing Data API** (`/api/*`) with the operator's own credentials.
An admin therefore sees exactly what the descriptor's rules permit them to see, and **no admin
bypass path is created in F5** (D4).

**This is also #228's own open question answered.** That issue requires an explicit decision on
the word *"audited"* in #27's scope line, and forbids shipping it silently. The decision:
**reads are unaudited in v0.1, because they travel the ordinary path and use no privilege beyond
the caller's own.** Audited reads arrive with #42.

### 2.5 Conventions adopted, not invented

- RFC 7807, and **the same `type` slug catalogue** the Data API publishes.
- `Idempotency-Key` on apply and rollback, with the write path's existing semantics.
- `If-Match` carrying `revision` (D3).
- Every operation **descriptor-shaped and idempotent**, so `MMLib.Alvo.Mcp` is later a mapping
  rather than a translation. No HTTP-only affordance an adapter would have to fake — the
  constraint spec §0.5 contract 4 imposes on anything built here.

### 2.6 What this shape does not solve

`GET {m}/projects` presumes several projects; **multi-project standalone (a database per project)
is not built.** In F5 it returns one. The drawing's project switcher degrades to a single row, and
that is the right outcome: the alternative is building project management because it was drawn.


### 2.7 A signed-in operator has no tenant, so the Data screen is dead for most of the example

**Found by drawing it, and it is a gap in this design rather than in the drawing.** §2.4 says the
data browser uses the ordinary Data API under the operator's own credentials, and §3.4 says a
cookie session mints an `AlvoContext` through `IAlvoContextResolver`. Both are right. Put together
they produce an operator who can browse almost nothing:

- `AlvoIdentityContextResolver.ResolveAsync` returns `null` the moment a tenant is requested —
  *"a cookie session carries no tenant grant, so honouring the request would let the caller choose
  the tenant it acts in, which is the one thing `TenantResolver` exists to refuse for an API key"* —
  and the principal it mints on the success path carries no `Tenant` at all.
- `PolicyEngine.Resolve` refuses a `tenancy: scoped` entity for a caller with no tenant **before
  any rule is consulted** (`data-api.md` §The decision procedure, cause 2). Not an empty page: a
  403.
- Nothing anywhere in `src/` enumerates tenants. There is no registry, no list route, no column
  the API will name.

So in `examples/field-service` — the descriptor that exists to demonstrate multi-tenancy — a
signed-in operator can browse `regions` and neither `customers` nor `work_orders`. The screen the
analysis calls *"prvý dojem produktu"* answers 403 on two of three entities on day one, and the
drawing's tenant switcher, its cross-tenant record count and its implicit `tenant_id` on the create
form all rest on a value the operator does not hold and has no way to ask for.

#### The decision: an operator carries one tenant, exactly as a key does

`AlvoUser` gains a `Tenant` (`TenantId?`), and `AlvoIdentityContextResolver` honours a requested
tenant **only as a confirmation of it** — byte-for-byte `TenantResolver`'s own rule, reused rather
than restated:

> *"A requested tenant is only ever honoured as a confirmation of the key's own tenant — it can
> never grant a tenant the key itself was not issued for."*

A resolver that consults the user's own grant is therefore not the escalation its remarks refuse.
The sentence those remarks make stays exactly true for the caller they were written about: an
operator asking for a tenant their row does not name is still `null`, still refused, and a session
still cannot *choose* a tenant — it can only confirm the one it was granted.

**The load-bearing half is the case where nothing is requested at all**, and it is
`TenantResolver.TryResolve`'s own first branch: *no requested tenant → the credential's own tenant,
and a successful result.* A dashboard browsing `/api/*` under a cookie sends no `X-Alvo-Tenant`
header and does not have to — the operator's row supplies it, exactly as a key's record does. The
three refusals stay refusals: a requested tenant the row does not name, a malformed one, and a
request naming any tenant at all from a row that names none. What changes is only that a row can
now name one.

**`TenantId` must reserve its all-zero value, and today it does not.** `UserId` refuses
`Guid.Empty` in two places, and `ManagementAccessEvaluator`'s remark calls that *"the gate making
that structural rather than conventional"*. `TenantId` has no equivalent: `Guid.TryParse` accepts
`00000000-0000-0000-0000-000000000000`, the result is not `null`, and `PolicyEngine`'s tenant guard
therefore treats it as a real tenant. That is an internal detail while a tenant only ever arrives
from an API key record an operator does not type. §2.7 ends that: the grant becomes a **form
field**, and a store that materialises `default(TenantId)` for a NULL column, or a form that posts
the all-zero string, would produce an operator who reads every row written under the all-zero
tenant instead of one who reads nothing — failing **open**, where every other refusal on this path
fails closed. The guard belongs in the type, on `UserId`'s own precedent, and §7 carries it.

**One implementation trap, written down because it is a one-character mistake.**
`TenantResolver.TryResolve` returns `true` with `tenant = null` on the no-request/no-grant path and
`false` on denial. Code that ignores the bool turns *"you asked for a tenant you were not granted"*
into *"you act with no tenant"* — which still denies every `scoped` entity, so it is a weakened
refusal rather than an escalation, but it **admits a session on `global` entities** where the
resolver today mints no caller at all. §6.1 pins it.

**Why the grant is on `AlvoUser` and not a second port.** It is membership, and
`IAlvoUserStore`'s own remarks already say what that port is for: *"who exists, and which roles
each of them is a member of"*. A tenant is the same kind of fact as a role name — assigned
elsewhere, meaningful only where the context is minted — and the alternative, a
`ITenantMembershipStore`, would be a second store to seed, to keep in step and to fail closed on,
for one nullable column.

**What this buys, stated plainly:** one line in the identity store makes every scoped entity
browsable under the ordinary Data API, under the ordinary rules, with no bypass and nothing to
audit that is not already audited. The operator sees what their rules permit, which is what §2.4
promised and could not deliver.

**What it deliberately does not buy: there is no tenant switcher, and the drawn one is removed.**
A set of tenants with a picker is cross-tenant capability, and this repository has already ruled on
that: `TenantResolver`'s own summary calls it *"a deliberate, audited grant, deferred to #42"*. #42
is F7. Shipping a switcher in F5 would be shipping the grant without the audit — the same trade D4
refuses for an admin bypass, one layer down. So the shell shows the tenant the operator acts in,
never a list, and an operator who must administer two tenants holds two accounts until #42 lands.
That cost is real and it is the smaller one.

**And it dissolves the registry question rather than answering it.** There is no tenant list to
build, because nothing is ever listed: a scalar on the caller's own row is not an enumeration.
`SELECT DISTINCT tenant_id` was the obvious alternative and it is refused on two counts — it is the
data-surface read D4 forbids the Management API to have, and it is a cross-tenant existence oracle
of exactly the kind `data-api.md` §"A `unique` field on a tenant-scoped entity was a cross-tenant
existence oracle (#137, fixed)" closed once already.

#### Where the grant is set, and who may set it

On the same surface §3.7 builds for the same reason: an operator's tenant is *membership*, which
`IAlvoUserStore` owns, beside their role names and for the same argument. It is an `admin`
operation (`ManagementOperation.ManageUsers`), never a `developer` one, because it decides who
reaches which data rather than what the backend is.

**The bootstrap administrator is not exempt, and that is the point.** They are an `admin` whatever
`access` says, because management admission is infrastructure — but a tenant grant is *data-path*
authority, and D4 says the dashboard holds none. A bootstrap admin whose row names no tenant sees
`regions` and nothing else until somebody grants them one, including themselves. A first run
therefore has one honest extra step, and the wizard owns it (§4.2, Welcome).

#### What the UI owes until an operator has one

A scoped entity for a tenant-less caller is refused, and the screen says so with the tenant guard's
own reason and the route to fixing it. It is not an empty state and it is not a spinner: the
distinction between *"your rules exclude every row"* (200, empty page — `data-api.md` §The RLS
surprise) and *"you carry no tenant"* (403, before any rule runs) is the single most useful thing
this screen can teach, and it is free to teach it correctly.
---

## 3. Identity, `access`, and the RBAC model

### 3.1 The frozen schema already settled #146's open question

`#146` asks whoever implements `access` to *"decide what `access` is for — role-based only, or
attribute-based via #37 — and then make the example say that, once."*

`schema/project.schema.json` already answers it, in the `access` block's own description:

> *"Each level is a CEL predicate over the closed context `@user` exposes — `@user.id` and
> `@user.roles`, nothing else… Attribute-based rules (an email domain, a team) are **NOT**
> expressible in this or any other block; typed claims are tracked by #37, and widening `@user` is
> additive, so a role-based level keeps compiling once they land."*

So this design **records** rather than decides: `access` is role-based, and that is a
forward-compatible property the schema already guaranteed, not a temporary shortcut.

### 3.2 A fifth CEL profile

None of the four profiles in `docs/architecture/cel.md` fits. `Rule` sees the current row **and**
`@user` — but `access` has no row, so `"owner_id == @user.id"` would **compile under `Rule` and
then have nothing to evaluate against.** That is precisely the silent failure the
`_allowedProfiles` table exists to prevent: *"a construct kind missing from it compiles in no
profile rather than every profile."*

A fifth column, `Access`:

| Construct | Access |
|---|---|
| Literal | ✓ |
| `@user` context ref | ✓ |
| `&&` / `\|\|` / `!` | ✓ |
| Comparison | ✓ |
| `in` (role membership) | ✓ |
| Field ref (current row) | ✗ |
| `old.` / `new.` | ✗ |
| `has(field)` | ✗ |
| `changed(field)` | ✗ |
| Arithmetic, ternary | ✗ |
| Allow-listed function call | ✗ |
| `@tenant` | ✗ |

`@tenant` is excluded deliberately: `access` is **project-scoped** by its own schema description,
not tenant-scoped. Admitting `@tenant` would make a project-level predicate answer differently per
request, which is neither what the block says nor something an operator could reason about.

**Role literals are validated at apply**, against `auth.roles`, exactly as rules already are
(`cel.md` §*Role literals are validated at apply, not at request time*). That closes the second
half of #146: a role the descriptor does not declare is refused at apply rather than silently
never matching.

### 3.3 What a level governs

| Level | Management API |
|---|---|
| `viewer` | every `GET` — `projects`, `descriptor`, `revisions`, `schema`, `capabilities`, `info`, and `policy/simulate` (which writes nothing) |
| `developer` | + `PUT descriptor` (including `?dryRun=true`) and `rollback` |
| `admin` | everything, plus the **settings** surface |

**"Settings" is named, not left to reading.** It is the set `developer` is excluded from: API-key
issue and revocation (`IApiKeyStore`), user and role-membership administration (§3.4), and the
danger zone (project deletion). The distinction is the schema's own — `developer` edits *what the
backend is*, `admin` also decides *who may reach it*.

**Management levels govern the Management API only. Data access is governed by the descriptor.**

That dissolves an ambiguity in the schema's own wording — `admin` names *"schema, rules, data,
settings"* while `developer` is silent about data. The question *"may a developer write data"* is
not a management question at all: `entities.*.rules` answers it, through the ordinary Data API,
identically for the dashboard and for everyone else. No second authorization system for data is
created.

The three levels are **independent predicates, not a hierarchy.** All three are evaluated and the
highest match wins. No match, and the caller is not the bootstrap admin → `403`.

### 3.4 What `MMLib.Alvo.Identity` owns

It owns **membership**. It does **not** own the role catalogue — that is the descriptor's, through
`IRoleCatalogProvider`, whose own doc comment protects the boundary: *"A host that registers its
own `IRoleCatalogProvider` takes identity roles over entirely… and the descriptor still governs
which role literals a rule may name."*

New port in Abstractions: **`IAlvoUserStore`** — users and their assigned roles. The `Alvo` prefix
follows `IAlvoData`'s precedent, and here it also avoids a real collision with ASP.NET Identity's
own `IUserStore<T>`.

No new resolution mechanism is introduced:

```
cookie auth (Identity)  →  IAlvoContextResolver  →  AlvoContext { User, Roles, Tenant? }
                                                        ↑
                               role membership from IAlvoUserStore
                               ∩ IRoleCatalogProvider.DeclaredRoles
```

**The intersection is load-bearing.** A user assigned a role the descriptor no longer declares must
not mint it — fail closed, the same rule the port already imposes for a `null` catalogue.

### 3.5 The bootstrap admin is infrastructure

`Alvo:Admin:BootstrapEmail` + `Alvo:Admin:BootstrapPasswordFile` (D2). The bootstrap admin holds
full management access **independently of `access`**, and that is a boundary rather than a hole:
the brief lists *"bootstrap admin credentials"* explicitly among infra config, never the
descriptor. `Descriptor ≠ infra config` is invariant §4 of `docs/PLAN.md`.

The consequence is worth stating: a descriptor with **no** `access` block means only the bootstrap
admin can manage the project. That is default-deny, and it is usable — the first-run wizard works,
and nobody else gets in until the descriptor says so.

The image still ships **no credential** (`host.md` §*No default credential*); the deployment
configures the bootstrap. `AlvoHostOptionsValidation` already reports every refusal at once, so a
misconfigured bootstrap is fixable in one restart.

### 3.6 The warning-to-enforcement transition

The PR that delivers §3 **removes the `access` entry from `UnhonouredSubsystems.All`**, and its
test stops expecting that name in the line. The file asked for this itself: *"the day the surface
lands, `access` is either honoured or refused — never warned about."*

### 3.7 A second human cannot exist, and with `providers: ["local"]` nobody can ever sign in

**The port is right and the product is not.** The prototype's decision log (`#/notes`, entry 8)
argues *no Invite; people arrive by signing in, and Alvo records what they already are*, and cites
the port correctly for it. That reasoning is exactly right for an OIDC project and exactly false
for a local one. Three facts, each verified:

- `IAlvoUserStore` is `FindAsync`, `FindByEmailAsync`, `ListAsync`, `SetRolesAsync`. **No create.**
- The bootstrap seed *"only ever creates the account, once"* and *"does not reset an existing
  account's password"* (`host.md` §The bootstrap administrator). It seeds exactly one row.
- `ManagementOperation.ManageUsers` sits in the level table at `admin` and **has no route**
  (`management-api.md` §"The three `admin` operations with no route").

So a `providers: ["local"]` project — which is what `examples/field-service` declares — has one
account forever. `ListAsync` returns one row, the Access screen is a list of one, and the second
administrator the §3.5 default-deny story assumes ("nobody else gets in until the descriptor says
so") can never come to exist to be let in. The descriptor can name `dispatcher` and `technician`
all it likes; there is nobody to assign them to.

#### The decision: membership creation on the port, credential issuance in the implementation

The split follows the port's own line, which is worth keeping rather than crossing:

> *"No credential appears on this port. Verifying a password, rotating it, or federating to an
> external provider is the implementation's business — an OIDC-backed implementation has no
> password to verify at all, and a port that demanded one would foreclose it."*

**`IAlvoUserStore` is not touched. A second contract is added.**

The two are different surfaces with different consumers, and conflating them was this section's
first draft:

| | `IAlvoUserStore` | `IAlvoUserAdministration` |
|---|---|---|
| consumer | `AlvoIdentityContextResolver`, on the **request path** | the dashboard and the CLI, on the **management path** |
| gate | none — it is below the gate | `ManagementOperation.ManageUsers`, at `admin` |
| shape | four read-shaped members | create, and the three writes below |
| cost of widening | **every** implementer, including a read-only directory mirror that has nothing to create into | none — a deployment without the package simply has no routes |

So the port every host already implements keeps its four members and its public surface, and the
new contract lands beside `IAlvoManagement` in Abstractions:

```
ValueTask<IReadOnlyList<AlvoUser>> ListAsync(UserQuery query, CancellationToken ct);
ValueTask<AlvoUser> CreateAsync(string email, IReadOnlyList<string> roleNames, TenantId? tenant, CancellationToken ct);
ValueTask SetRolesAsync(UserId user, IReadOnlyList<string> roleNames, CancellationToken ct);
ValueTask SetTenantAsync(UserId user, TenantId? tenant, CancellationToken ct);
ValueTask SetDisabledAsync(UserId user, bool disabled, CancellationToken ct);
ValueTask<CredentialSetToken> IssueCredentialTokenAsync(UserId user, CancellationToken ct);
```

`CreateAsync` carries §2.7's tenant grant, because a tenant is membership for the same reason a
role name is. It is **not** an invitation and it is not a sign-up: it is the row an OIDC host wants
to pre-provision so a colleague's first sign-in already carries their roles — which is the thing
the current port cannot express either, and the half of *no Invite* that was always missing rather
than deliberate.

**Every member may refuse by name, and that is what keeps the contract provider-agnostic.** A
read-only directory mirror refuses `CreateAsync`; an OIDC-only deployment refuses
`IssueCredentialTokenAsync`. Refusing by name rather than returning `null` is the port family's own
fail-closed precedent, and it is the distinction that matters against `IAlvoUserStore`'s remark that
*"no credential appears on this port"*: that sentence refuses a contract which **demands** a
credential — verify this password, rotate that one — because an implementation with no passwords
could not answer at all. A member that asks *"mint a set-password token if you have such a thing"*
is a question an implementation may decline, and a deployment already knows which it is from
`auth.providers`. The distinction is written here because it is the one an implementer will get
wrong.

**§6.1's contract test therefore reads "every member of `IAlvoUserAdministration` has a route",**
and it holds unconditionally — a refused member still has a route, and the refusal is its answer.

**The credential half belongs to `MMLib.Alvo.Identity`, and only `local` has one.** The package
already holds `UserManager` — `AlvoIdentityBootstrap.CreateAsync` uses it — so the capability
exists in the package and not on the port, which is precisely the boundary to keep.

**How the second person gets a password, and the cost that answer carries.** An administrator
creates the row and mints a **single-use credential-set token**; the new operator sets their own
password with it. The administrator never types a colleague's password, and the reason is the
repository's own: `Alvo__Admin__BootstrapPassword` — the password as a *value* — is **refused
outright**, because a credential that travels as a value is readable by whoever handles it. An
administrator typing a colleague's initial password into a form is the same class one layer up.

The cost, stated rather than discovered: **nothing in this build delivers that token.** The
identity package configures no mail transport, and `templates`/`webhooks` are warned subsystems
whose reach is an after-hook on an entity write, not an identity event. So F5 renders the token for
the administrator to hand over out of band, and says on the screen that it does. Inventing a mailer
here would be a subsystem arriving because a screen wanted it.

#### The routes, and why they are management routes

`IAlvoUserAdministration` is a **second contract in Abstractions**, implemented by
`MMLib.Alvo.Identity`, mapped by the core's `ManagementEndpoints` when DI holds one — the same
shape `IAlvoManagement` already has, so §1.2's boundary is untouched and a deployment without the
package simply has no routes there.

| Route | Member | Level |
|---|---|---|
| `GET {m}/projects/{p}/users` | `ListAsync` | `admin` |
| `POST {m}/projects/{p}/users` | `CreateAsync` | `admin` |
| `PUT {m}/projects/{p}/users/{id}/roles` | `SetRolesAsync` | `admin` |
| `PUT {m}/projects/{p}/users/{id}/tenant` | `SetTenantAsync` | `admin` |
| `PUT {m}/projects/{p}/users/{id}/disabled` | `SetDisabledAsync` | `admin` |
| `POST {m}/projects/{p}/users/{id}/credential-reset` | `IssueCredentialTokenAsync` | `admin` |

They are management routes because `ManageUsers` is already a `ManagementOperation` at `admin`, and
because *"všetko, čo vie dashboard, vie aj API"* binds them as much as it binds the descriptor.
§6.1's contract test gains a sibling: **every member of `IAlvoUserAdministration` has a route too.**

#### The bootstrap administrator is not a target of this surface, and that is load-bearing

Two of these six members would otherwise remove the one identity the whole default-deny story
rests on. `ManagementAccessEvaluator`'s own remark states the invariant: *"a project whose `access`
block locks everyone out still has exactly one person who can fix it."* So both are refused on that
account **by name**, and the refusal is part of the contract rather than a policy an implementation
might hold:

- **`IssueCredentialTokenAsync` refuses the bootstrap administrator.** Without the refusal, any
  `admin` mints a set-password token for the account the descriptor's `access` block does not
  govern, sets the password, and signs in as it — which is precisely the capability `host.md`
  refuses when it says *"Seeding is idempotent and does not reset an existing account's password."*
  The bootstrap credential comes from a **mounted file**, and rotating it is a deployment
  operation, not a dashboard one.
  There is a second consequence and it is the sharper one. §3.7's U3.1 argues the self-tenant-grant
  is worth refusing because an apply *"appends a `DescriptorVersion` carrying `Author` and
  `Reason`, which Configuration history renders forever."* A credential reset aimed at anybody
  makes that `Author` **forgeable**: reset a colleague's password, sign in as them, apply, and the
  permanent record names the colleague. The refusal for the bootstrap admin does not close that —
  see U3.2 — it closes the case where the forged identity is the one above the descriptor.
- **`SetDisabledAsync` refuses the bootstrap administrator.** Trace what it would do:
  `AlvoIdentityContextResolver` returns `null` for a disabled user **before** anything consults
  `IAlvoBootstrapAdmin`, so no context is minted and the evaluator's bootstrap branch is never
  reached; `IsDisabled` is a lockout the identity store holds; and a restart does not help, because
  the seed finds the existing row and returns without touching either password or lockout. A
  deployment whose `access` block admits nobody else — **which §3.5 says is the default** — would
  be permanently locked out of its own Management API, recoverable only by editing the identity
  database by hand.

**No "last administrator" guard is needed, and that is why.** The obvious alternative — refuse
disabling the last caller any `access` level admits — is both harder (it means resolving every
user against every predicate on every write) and unnecessary: the bootstrap administrator is
always there and cannot be disabled, so the invariant holds without counting anybody.

#### U3.2 — what the self-grant guard is, and what it is not

**It is a mistake-guard, not a malice-guard, and pretending otherwise would be the more dangerous
claim.** An `admin` who wants the reach can have it in one hop and the guard cannot stop them:
`CreateAsync(email, ["admin"], tenant)` mints a puppet, `IssueCredentialTokenAsync(puppet)` signs
them in as it, and `SetTenantAsync(somebody-else)` is explicitly left open. None of it is recorded
(U4).

That is not a hole to be plugged at this level, because it is the trust boundary itself: `admin`
is defined by §3.3 as the level that *decides who may reach the project*, and an `admin` already
holds `ApplyDescriptor` and can therefore rewrite `entities.*.rules` to admit themselves to every
row of every entity. There is no arrangement of guards that makes an untrusted `admin` safe; what
makes one accountable is #42, and F5 does not have it.

So the guard earns its place on a narrower claim, stated rather than implied: **it catches the
honest mistake at zero cost, and it keeps the one clearly-recorded route the clearly-recorded
one.** A caller who cannot grant themselves a level has to either use the audited route (an apply,
with an `Author`) or take a deliberate, visible detour through a second account. It is a
speed bump with a name, and calling it a control would be the thing that misleads.

#### The level is re-resolved, never name-matched

The guard's rule is *"a role that raises the level `access` resolves for them"*, and the obvious
implementation — *did they add `admin` to themselves?* — is wrong. A level is any CEL predicate
over declared roles: `access.admin: "'dispatcher' in @user.roles"` is legal, so a self-grant of
`dispatcher` resolves to `admin` and a name-match waves it through.

The implementation builds the **prospective** `AlvoContext` — the caller's roles after the write,
intersected with `IRoleCatalogProvider.DeclaredRoles` exactly as `AlvoIdentityContextResolver.Minted`
does — and compares `ManagementAccessEvaluator.Resolve` before against after. Higher is refused.
Written down because the shortcut is the obvious thing to write.

#### What DI registers under the public interface

`IAlvoUserAdministration` is public in Abstractions and implemented in `MMLib.Alvo.Identity`, while
the guards above live in the **core**. `AlvoManagementService`'s own remark names the failure that
shape invites: a guard living in one adapter *"would be the divergent authorization path spec §0.5
contract 4 forbids"*.

So the registration is explicit: **the core registers a guarded decorator under
`IAlvoUserAdministration`**, and the Identity implementation is registered under its own internal
type that only the decorator resolves. An in-process consumer that resolves the public interface
gets the gate, the escalation guard and the bootstrap refusals; there is no registration that
hands out the raw implementation. This is the same shape `AlvoManagementService` already has over
`IDescriptorVersionStore` and `ISchemaRegistry`, and §6.1 pins it: **resolving
`IAlvoUserAdministration` from a composed container and calling a member as a caller with no level
is refused.**

**One operation for six members, including the read — decided, not inherited.** The obvious
alternative is a second operation, `ReadUsers`, at `viewer`, so a viewer can see who is on the
project without being able to change anything. It is refused: the people list is the one place a
project's administrators are **enumerated**, and "who is an admin here" is reconnaissance a
default-deny posture has no reason to hand to every viewer. The cost is real and small — a viewer
cannot answer *"who else can see this?"* from the dashboard — and the answer they actually need,
*"what can **this** person do"*, is `policy/simulate`, which they already have at `viewer`. Revisit
if a viewer is ever expected to administer anything, and note that splitting the operation later is
additive.

**`ManagementOperations`' fallback makes the failure mode safe either way.** An operation missing
from that table resolves to `admin`, so a seventh member added without a decision is refused for
everyone but an administrator rather than opened to every viewer.

**`ListAsync` grows paging and a filter here, not later.** The port's current `ListAsync` returns
every user in no order, which is the whole table in one render — fine for a build with one row,
wrong at the 2 000 the analysis sizes for, and a port-level gap rather than a screen-level one. It
is cheaper to widen a port nobody implements twice yet.

#### Two rules that must be server-side, because the UI refusing them is decoration

**U3 — nobody grants themselves anything, and the guard is in the CORE, not in an
implementation.** The dashboard drawing refuses it in JavaScript, which is decoration; putting it
inside `IAlvoUserAdministration`'s implementation would be barely better, because a rule enforced
only inside a swappable adapter is **optional by construction** — the second implementation simply
does not have it, and principles 2 and 5 both fail quietly. It goes exactly where the guard it
resembles already is: at the head of the contract member in the **core's** management service,
beside `EnsureMayChangeAccess`, raising the same `ManagementEscalationException`. And it is pinned
the way the engine is pinned — a contract test in `MMLib.Alvo.Testing`, on
`PolicyEngineContractTests`' precedent, that **every** implementation runs. A ring2 integration test
against the one implementation that exists would measure the implementation, not the rule.

**It covers two grants, not one.** A caller may not add themselves a role that raises the level
`access` resolves for them — and may not change their **own tenant**, which is the half with
data-path consequence and the half an earlier draft of this section left out.

**U3.1 — what a tenant grant actually costs, weighed rather than assumed.** `SetTenantAsync` is an
`admin` operation, and an `admin` may grant one to themselves. Two facts settle whether that is a
bypass:

- **It creates no authority they did not already have.** An `admin` holds `ApplyDescriptor`, so
  they can rewrite `entities.*.rules` to admit themselves to every row of every entity. The reach
  is identical; only the route differs.
- **The two routes are not equally visible, and the tenant grant is the quieter one.** An apply
  appends a `DescriptorVersion` carrying `Author` and `Reason`, which Configuration history renders
  forever. A membership change records nothing at all (U4). So the honest statement is not *"no new
  authority, therefore fine"* — it is **the same authority by an unrecorded path**, and that is a
  real cost that #42 closes and nothing before it does.

Which is why the self-grant is refused rather than merely noted: it is the one case where refusing
costs an administrator a second account and buys the difference between "they used the recorded
route" and "nobody can tell". Granting a tenant to **somebody else** stays available and stays
unrecorded, like every other membership change, and the screen says so.

**U4 — a membership change is not recorded anywhere, and F5 ships it that way.** #42 is F7; there
is no audit table and a half-audit here would be a second, thinner answer to the question #42 owns
(`management-api.md` §"No log line, and no throttle"). Refusing membership administration until #42
would make the product unusable, so the change ships and the **screen says plainly that nothing
records it**. Filed against the F5 acceptance list as a known miss rather than left to be found.

### 3.8 What the drawing's "no Invite" decision becomes

It said: *no Invite; people arrive by signing in.* Amended: **people arrive by signing in where an
external identity provider can mint them, and are created here where none can.** A second contract
carries membership creation for both cases; only `local` additionally needs a credential, and that
member is one an OIDC implementation refuses by name. The drawn Access screen therefore does gain a "New person"
control — and it is not the Invite defect the notes were right to catch, because it produces a real
row through a real port member rather than a button with no output.

---

## 4. Information architecture and the capability map

### 4.1 Two classes of "not yet", and they must not look alike

This is the central UI rule, and it falls out of two tables that already exist in the code.

| Class | Source | Descriptor behaviour | What the UI may do |
|---|---|---|---|
| **Warned** | `UnhonouredSubsystems` | descriptor **applies**; nothing runs | the section exists, carries a `Not yet` badge and an empty state quoting the table's own sentence |
| **Refused** | `UnhonouredFeatures` | descriptor is **rejected at apply** | the control **must not exist** — or is disabled carrying the refusal text verbatim |

A control for a *refused* feature is worse than a missing control: it is a control whose only
possible output is a descriptor the apply will reject.

This is not academic against the reference drawing, which currently declares `default: 'lead'` on
an enum field and a `pattern` on an e-mail field — both **refused** today (`field.default`,
`field.validation`), alongside `softDelete`, `rollup.where`, wildcard event subscriptions, JSONata
and `email.data`.

### 4.2 Route map

Every route is `{m}/projects/{p}/…` except `info` and `projects`, which are unprefixed. The
drawing wrote `/management/descriptor` and `/management/capabilities` throughout; those routes do
not exist.

| Route | F5 status | Data source |
|---|---|---|
| **Welcome / first-run** | LIVE | bootstrap admin (§3.5), #230. **Does not create an account** — the bootstrap already exists before the dashboard can be reached (§3.5), so step one is signing in, plus the tenant grant §2.7 needs |
| **Overview** | LIVE, narrowed | `GET …/schema`, `GET …/revisions`, `GET …/capabilities`; counts via `AlvoQuery.IncludeTotalCount`. The *declared and not running yet* panel is `warned` **intersected with the descriptor's own top-level keys** — `CapabilityReport.Project()` projects all five, and `UnhonouredSubsystems.DeclaredBy` is the predicate that narrows them |
| **Data** | LIVE for global entities; **scoped entities need §2.7's tenant grant** | the existing Data API `/api/*` (§2.4) |
| **Schema** | LIVE read (#228), LIVE editor (#229) | `GET …/schema` + `GET …/descriptor`; writes via `PUT …/descriptor?dryRun=true` → diff → `PUT` |
| **Rules** | LIVE; the simulator renders a predicate, never a row verdict (§2.2.1) | `GET …/descriptor` + `POST …/policy/simulate` |
| **Access** | PARTIAL | **membership** — people, their roles, their tenant — is the identity store through `IAlvoUserAdministration` (§3.7) and takes effect at once; the **role catalogue** (`auth.roles`) and the three **levels** (`access.*`) are the descriptor and wait for an apply (§4.5). **Teams and the permission matrix are #37 (F7)** |
| **Configuration history** | LIVE, reframed | `GET …/revisions`, `GET …/revisions/{n}` — §4.4 |
| **Automations** | NOT YET (warned) | `capabilities` |
| **Functions** | NOT YET (warned) | `capabilities` |
| **Integrations** | PARTIAL, **added to this map** — the drawing invented it and it was never recorded here | the descriptor's `webhooks` and `templates`, rendered beside `capabilities.warned` for both, served verbatim. Creating either is **refused**: `bodyFile`, `email.data` and the three action types are in `UnhonouredFeatures.EveryRefusal`, so the controls are inert or absent (§4.1) |
| **Entity → API tab** | LIVE, **added to this map** | `GET …/schema` plus the generated route shape; it is a rendering of what the Data API already publishes, not a second document |
| **Settings** | PARTIAL, **narrower than the previous row claimed** | `GET {m}/info` LIVE — and it reports `dataProvider`, never an engine. **API keys are not LIVE**: `IApiKeyStore` is `FindAsync` + `TouchAsync`, and `ManageApiKeys` has no route, so issuance and revocation do not exist; what can honestly be shown is a key's `User`, `RoleNames`, `Tenant`, `ExpiresAt` and `RevokedAt`, with D7's consequence stated where an operator reads it. **The danger zone is not LIVE either**: `DeleteProject` has no route |
| **Assistant** | **NOT IN F5** — the drawing carries it and §6.4 defers it | needs `ISecretStore`; kept as a design (the drawer proposes a diff, exits through the same dry run, never applies), shipped later |
| **Projects** | LIVE, degraded to one | `GET {m}/projects` (§2.6) |

### 4.3 Navigation order

Live sections first, `Not yet` separated below:

```
Overview
Schema
Data
Rules
Access
Configuration history
Integrations
──────────────────────
Automations      Not yet
Functions        Not yet
──────────────────────
Settings
Projects
```

**Schema sits above Data, and that reverses an earlier draft of this list.** The drawing put it
there and the reason holds: the dashboard's reason to exist is defining what a backend *is* —
entities, fields, types, relationships and the rules that guard them. Browsing records proves the
model works; it is not why the tool is opened. The mobile bar therefore takes Overview, Schema,
Data, Rules, Access, which is the order of a first session rather than of a CRUD scaffold.

The reason for the separator is the 375 px acceptance criterion rather than tidiness. A bottom navigation bar holds
about five items and must contain only what works. If `Not yet` sections are interleaved, the
mobile navigation either lies or has to be decided separately — which is a second navigation. This
way there is one: the bar takes the first five live entries, the rest goes to the hamburger sheet
the drawing already has.

### 4.4 `Activity` is not "not yet" — it is something else (D5)

The drawn `Activity` is a system-wide feed. That is an audit log, and it is **#42 (F7)**.

But the descriptor's append-only history exists and is complete: `DescriptorVersion` carries
`Revision`, `CreatedAt`, `Author`, `Reason` and `RolledBackFrom`. That is an audit trail — **of
configuration changes.** So the route ships LIVE under an honest name, **Configuration history**,
with a revision-to-revision diff and a rollback action.

It is less than was drawn and more than an empty state. It is also the only place in the product
where `Author` and `Reason` become visible, which finally gives those fields a consumer.

Data-level audit joins as a second tab when #42 lands.

### 4.5 Three kinds of edit, one descriptor, one apply

**The drawing grew three unapplied-change queues, and the product has one document.** Schema edits,
rule edits and role-catalogue edits each earned their own pending bar, all three linked to one
preview, and the preview rendered only the schema ones. A person who ticked a cell in the rules
matrix, read *"1 rule changed"*, opened Preview and saw two unrelated schema rows has no way to
tell whether Apply will carry their rule or drop it.

That is not a drawing bug to be tidied. It is this document never saying how the kinds of edit
compose, so the drawing composed them three ways.

#### They do not queue, because there is nothing to queue

`ManagementApplyRequest` takes **one `DescriptorJson`**, one `ExpectedRevision`, and produces one
appended revision. `entities.*.fields`, `entities.*.rules`, `auth.roles` and `access` are all keys
of that one document. There is no ordering question between them, no partial apply, and no
interleaving to design: the server applies the migration and then re-primes the policy catalog and
the role catalog **from the same accepted descriptor**, which is the property §6.1's *four doors,
one result* criterion already measures.

So the decision is the one the product's thesis already implies, written down so a UI cannot
invent a second: **one working copy of the descriptor, one preview, one apply.**

| Kind | Where it lives | When it takes effect |
|---|---|---|
| entities, fields, indexes | the descriptor | the apply |
| `entities.*.rules` | the descriptor | the apply |
| `auth.roles` (the catalogue) | the descriptor | the apply |
| `access.*` (the three levels) | the descriptor | the apply, at `admin` — see below |
| **role membership, a person's tenant, disabled** | **the identity store** (§3.7) | **at once** |

The last row is the only thing legitimately outside the queue, and the drawing's two-speed Access
split — *takes effect at once* against *reviewed before it applies* — is already the right way to
draw the line. It is kept; what changes is that the upper half stops having a pending bar of its
own and joins the one count.

#### What the preview owes, per kind

One preview, grouped by kind, each group with its own diff — and one sentence the shape makes
mandatory rather than nice:

**A rules-only or roles-only apply has an empty migration plan, and that is not "nothing
happened".** `ManagementPlanSummary.IsEmpty` is documented as *"the descriptor changes nothing
about the schema — which a rules-only edit does, and which is therefore not the same claim as
'nothing was applied'"*. A preview that renders `isEmpty` as *No changes* tells an operator their
rule edit will be dropped, at the exact moment they are deciding whether to trust the tool. The
diff is the authority on what is changing; the plan is the authority on what the database will do,
and for a rules edit the honest plan is *no migration step — this apply changes policy, not
storage*.

#### One consequence that only appears once the queues are merged

`access` is a key of the same document. `AlvoManagementService` re-resolves **the whole write** to
`admin` when the descriptor's `access` block differs from the applied one, on both write members
(`management-api.md` §"The one place a route's level is not the whole answer") — and the rollback
arm too, because a stored descriptor the caller never wrote is the subtler escalation.

So a `developer` who edits one rule *and* one `access` predicate in one working copy is refused the
**entire** apply, including the rule change that was within their level. With three queues that
could never be seen; with one it is the ordinary case, and the UI owes the operator the sentence
**before** they reach Apply: *this working copy changes who may manage the project, so applying it
needs `admin`.* Shown at Preview, not discovered at 403.

#### What the shell shows

One count, beside the project name: *3 unapplied*. One pending bar, identical on every screen that
can change the descriptor, whose primary action is **Preview** and never Apply. Membership changes
report separately and in the past tense, because they already happened.

### 4.6 What the drawing decided, and which of those decisions this design now carries

The drawing (`docs/design/f5-admin/`) made a number of calls this document never made, recorded in
its own `#/notes`. They are not deviations *from the sources* — §0.2's table is for those — so they
are recorded here, in the layer they actually belong to. Each was read against the repository by
two adversarial reviews; these are the ones that survived.

| Decision | Why it stands |
|---|---|
| **Every schema editor is a split: model left, the descriptor it produces right.** | §6.3 criterion 3 is *everything clickable is exportable as code*. Showing the document being built turns an acceptance criterion into something an operator watches happen, and makes the dry-run diff unsurprising rather than a second opinion. Binding: the pane must render the **working copy** (§4.5) with the changed lines marked, and the export must be `DescriptorJson` — a re-serialisation through a typed projection silently narrows any descriptor it touches (`nullable`, `index`, `renamedFrom`, `default`, `storage`, `realtime`, `x-*`, and a CEL-valued `hidden`/`readOnly` flattened to `true`). |
| **The eleven field types are one uniform grid, and the editor is eleven forms.** | `$defs/field`'s nine `if/then` rules decide what a field of each type may carry; three types (`decimal`, `enum`, `ref`) carry **required** facets and seven carry none. An editor covering four branches and rendering nothing for the other seven is indistinguishable from a finished one, so the schema's own table is the spec. |
| **Access is split by how fast a change takes effect, not by which store holds it.** | §4.5's table is that line drawn once. On one undifferentiated screen half the controls would lie about when they work. |
| **An assigned role the descriptor does not declare is shown as inert, not as an error.** | `AlvoIdentityContextResolver.Minted` drops it *silently* — nothing refuses it anywhere, so every rule naming it never matches and the failure has no symptom. The screen is the only place that quiet can be made loud. |
| **A roles × operations matrix, with built-ins separated and `anon` marked amber.** | The matrix the analysis §2.3 warns against is teams × permissions, which needs #37's typed claims and is not this. Roles × the five operations is exactly what `entities.*.rules` holds, so the grid is a rendering of the descriptor rather than a model beside it. `anon` subsumes every other branch, and an open rule is the failure the sources single out — it must not look like an ordinary tick. |
| **Conditions live on the branch, not under the column.** | A condition ANDed across a whole column cannot say *technicians only while not completed, dispatchers always* — the commonest real rule — and silently locks out the roles it was not aimed at. Per-branch conditions say it; anything the builder cannot express falls back to a raw CEL editor rather than to a narrower rule. |
| **The model map is read-only.** | An editable graph is a second schema editor with a second set of affordances and no descriptor pane. Read-only, it is the best onboarding artifact in the build. |
| **Each entity carries an API tab and an on-write (hooks) tab.** | Both are renderings of what the descriptor already declares — the generated route shape, and `entities.*.hooks` split before/after the commit. Neither invents surface. The sample CEL in them must compile: `==`/`!=` against `null` is **rejected** (`cel.md` deviation 10, use `has()`), arithmetic is ✗ in the `Rule` profile, and `now()` returns a `Timestamp`. |
| **The first-run wizard creates no entity.** | A wizard step for modelling would be a second, worse copy of the schema editor. It signs in, names the project, and lands on Schema's empty state. |
| **No provider picker in Settings.** | The driver is composed at boot; a dashboard control that appears to change it would be a control whose only output is a restart it cannot perform. `info` reports what was registered. |

Two of the drawing's decisions do **not** stand, and both are answered above: the simulator over a
real record (§2.2.1) and *no Invite* (§3.7, §3.8). A third — the assistant inside Settings — is
deferred by §6.4 rather than refused.

---

## 5. The design system

### 5.1 What the reference drawing is

A drawing, not a system — which is normal for its stage. Measured: **24 distinct font sizes**
(9, 9.5, 12, 12.5, 13, 13.5, 14, 14.5, 15, 16, 17, 18, 19, 20, 21, 22, 24, 26, 28, 40…) and
**12 distinct radii**.

The colours, by contrast, already **are** a system: a complete token map for both themes
(`--bg`, `--panel`, `--panel2`, `--codeBg`, `--border`, `--border2`, `--text`, `--dim`, `--faint`,
`--accent`, `--accentText`, `--accentSoft`, `--accentBorder`, plus semantic state pairs). That is
the harder half, and it is adopted as-is apart from D6.

So §5 is mostly **narrowing.**

### 5.2 Scales

Typeface: **Public Sans** + **IBM Plex Mono**, as drawn.

| Token | px | Use |
|---|---|---|
| `--text-2xs` | 11 | badges, meta |
| `--text-xs` | 12 | table cells, secondary text |
| `--text-sm` | 13 | buttons, navigation, most chrome |
| `--text-base` | 14 | body, card headings |
| `--text-lg` | 16 | section headings |
| `--text-xl` | 20 | page title |
| `--text-2xl` | 26 | welcome and empty states |

Half-pixel sizes (`12.5`, `13.5`, `14.5`) are dropped. A half pixel is not a decision; it is
residue from drawing, and no Razor component will reproduce it consistently.

**Radius** — five steps: `6` (checkbox, small chips) · `10` (controls, inputs, buttons) ·
`12` (panels) · `16` (cards) · `999` (pill). The drawing's most-used values cluster at
11 / 10 / 16 / 9 / 12, so this is rounding, not redesign.

**Weight** — 500 / 600 / 700, as drawn. No 400 in UI chrome.

**Spacing** — multiples of 4, from 4 to 32.

**Density** — `comfortable | compact`, kept from the drawing's own props, implemented as a
multiplier over the spacing tokens rather than a second set of values.

### 5.3 Accent contrast (D6)

`--accent: #128a52` with `--accentText: #ffffff` measures **4.39 : 1**. WCAG AA requires 4.5 : 1
for normal text, and the drawn primary buttons set 12.5–13 px — normal text, not large.

Light accent moves to **`#0f7a48`** → **5.39 : 1**. Already present in the drawing as `--won-fg`,
so the palette does not widen. The dark accent `#39E991` on `#1e2029` passes with margin.

Analysis §2.8 lists WCAG AA among *"must contain"*, so this is an acceptance criterion rather than
a detail — one that would otherwise surface at audit time.

### 5.4 Token implementation

`:root` custom properties; dark under `[data-theme="dark"]` **and**
`@media (prefers-color-scheme: dark)`. The drawing computes the map in JavaScript and inlines it;
here it is one static stylesheet and the toggle flips an attribute. That is not cosmetic under
server-interactive Blazor: the theme is correct before hydration and does not flash.

### 5.5 Readability and operability come first

The maintainer's stated priority. What it adds beyond the drawing:

- **Keyboard operation is first-class, not an add-on.** The drawn ⌘K palette, plus `j`/`k` through
  rows, `/` to focus the filter, `Enter` to open a detail, `Esc` to dismiss anything, `g`+letter to
  jump to a section. The focus ring is always visible and never removed.
- **`font-variant-numeric: tabular-nums`** on numeric columns. A column of amounts is otherwise
  unreadable vertically.
- **Errors are not toasts.** The Data API returns RFC 7807 with `title`, `detail` and a `type` slug
  catalogue, and `UnhonouredFeatures` additionally carries `fix`. So an error lands **inline, at
  the field it concerns**, with the fix suggestion, and stays. A toast that disappears in three
  seconds loses information in a configuration tool.
- **Every empty state says what to do next.** Never "No data".
- **Loading is a skeleton, never a full-page spinner.** The drawing already has skeletons; they
  extend to every section.
- **Destructive actions require typing the name.** Dropping a column and rolling back destroy
  data; `MigrationOptions.AllowDestructive` is explicit in the API, so it is explicit in the UI.
- **`prefers-reduced-motion` is honoured** — the drawn animations (`alvoDrawer`, `alvoPop`,
  `alvoShimmer`, …) are disabled, not shortened.

### 5.6 Component inventory

With D1 taken, this is the build scope #227 must cover.

**Chrome** — app shell (sidebar + header + content), project switcher, nav item, bottom nav,
hamburger sheet, command palette (⌘K)
**Data** — grid over in-box `Virtualize`, entity tabs, bulk action bar, detail drawer, card as the
mobile row substitute
**Inputs** — input, select, textarea, checkbox, toggle, CEL/code editor (monospace, highlighting),
field editor
**Feedback** — toast, skeleton, empty state, **`NotYet` badge + empty state** (new, driven by
`capabilities`), diff viewer, confirmation dialog
**Layout** — panel, card, modal, drawer, section heading

The diff viewer is worth naming: the schema editor needs it (`dryRun`), Configuration history needs
it (revision against revision), and the later AI proposal card needs it. Three consumers, one
component — which is why it is in the inventory rather than improvised inside the editor.

### 5.7 Where the design system lives

The source of truth is **in the repository**: `src/MMLib.Alvo.Admin/wwwroot/alvo.css` for the
tokens, Razor components beside it. The Claude design-system project remains the reference
drawing. Pushing the built components back as preview cards through `/design-sync` is an option,
not part of F5.

---

## 6. Testing strategy and acceptance criteria

Organised by **what each test can fail**, because a test that cannot fail is a failure mode this
repository has already met once (`#142`, the mutation gate that reported `Killed` for surviving
mutants).

### 6.1 What must be pinned by a test rather than by agreement

| Claim | Test | Ring |
|---|---|---|
| Admin has no reach into the core | arch: `MMLib.Alvo.Admin` holds no reference to `MMLib.Alvo` | ring1 |
| One path, two transports | contract: **every `IAlvoManagement` member has an HTTP route** | ring1 |
| …and the same for user administration | contract: **every `IAlvoUserAdministration` member has an HTTP route** (§3.7) | ring1 |
| An operator cannot choose a tenant they were not granted | integration: a session requesting a tenant the user's row does not name resolves to `null`, exactly as an API key does (§2.7) | ring2 |
| …and a denied request is not read as "no tenant" | unit: `AlvoIdentityContextResolverTests` — requested == held is honoured, requested ≠ held refuses the **whole principal**, no request mints the held grant, and an unreadable request refuses rather than being dropped (§2.7) | ring0 |
| Nobody grants themselves a role or a tenant | **contract test in `MMLib.Alvo.Testing`**, run by every `IAlvoUserAdministration` implementation — a guard living in one adapter is optional by construction (§3.7, U3) | ring0 |
| …and the level is re-resolved, not name-matched | unit: `access.admin: "'dispatcher' in @user.roles"`, caller self-grants `dispatcher`, refused (§3.7) | ring0 |
| The bootstrap administrator cannot be disabled or credential-reset | integration: both members refuse that id by name, so the identity `ManagementAccessEvaluator` calls *"exactly one person who can fix it"* survives every write this surface has (§3.7) | ring2 |
| Resolving `IAlvoUserAdministration` from a container gets the guard | integration: a composed container's public registration is the guarded decorator, and an unpublished caller is refused in-process (§3.7) | ring2 |
| A tenant is never the all-zero value | unit: `TenantId.TryParse`, `Parse` and the JSON converter refuse the reserved value; and, because the management route binds a bare `uuid` and builds the `TenantId` itself, a fourth guard in `GuardedUserAdministration` refuses it on both doors — `SetTenantAsync` and `CreateAsync` — as a `MMLib.Alvo.Testing` contract fact (§2.7) | ring0 |
| No client evaluates a stored row | Playwright: the rules screen renders no per-record allowed/refused verdict at any width (§2.2.1) | prototype suite |
| The simulator answers as production does | property: `PolicySimulatorAgreementTests` — two entities × four operations × three callers, verdict against the data port's own answer for the same `AlvoContext`. 23 of 24 agree exactly; the 24th is the documented boundary of `Allowed` (a write's `WITH CHECK` runs over a post-image the simulator may not invent), and the suite holds that the verdict then hands the predicate back for the screen to render | ring2 |
| `access` is actually enforced | integration: a caller matching no level gets `403` on every management route | ring2 |
| A role the descriptor does not declare is never minted | unit: `IAlvoUserStore` ∩ `IRoleCatalogProvider`, fail closed | ring0 |
| The `Access` profile is closed | unit per construct: field ref, `@tenant`, arithmetic, `changed()` **do not compile** | ring0 |
| `capabilities` does not lie | reads `UnhonouredSubsystems.All` and compares against the payload — the same shape that already guards that table against the schema | ring0 |
| Four doors, one result | mount / Management API / `FromDescriptor()` produce an identical `SchemaModel` (the CLI door is absent, #213) | ring2 |
| The dashboard is not a policy bypass | **the row as written is not measurable, and that is a finding rather than a gap.** A cookie does not authenticate `/api` — `AlvoContextFilter` resolves an API key — so there is no pair of paths to compare for one caller. What carries the claim instead is that the dashboard reaches rows through `IAlvoData` and nothing else (`BoundaryArchitectureTests`, `ComponentLayerTests`), and that a **session-resolved** `AlvoContext` over that port obeys the tenant predicate adversarially (`SessionTenancyIsolationTests`) — which is the same port, the same predicate and the same caller the dashboard uses | ring1 + ring2 |
| A cookie session is isolated across tenants exactly as a key is | adversarial: `SessionTenancyIsolationTests` — two operators, two tenants, one otherwise identical descriptor. Neither sees the other's rows, neither can fetch one by an id learned out of band, neither's total count includes the other's, and a write aimed across is *not found* rather than forbidden. A third operator holding no tenant is refused the scoped entity outright and still reads the global one, which is what tells "holds no tenant" apart from "was not admitted". A revoked grant takes effect on the next resolve. §2.7 gives `AlvoContext.Tenant` a second provenance (a user row an `admin` edits), and every existing cross-tenant fact was written about the first one | ring2 |

### 6.2 Playwright

`#231` asks for the full E2E. Analysis and spec agree: **it runs on the PR, whole, not
affected-scoped**, `Microsoft.Playwright` + xUnit. Flows from the DoD: first-run wizard → project →
entity → descriptor export; the policy simulator; (later) an AI proposal → diff confirmation.

One test beyond the DoD: **a `Not yet` section opens and breaks nothing.** It is a trivial test,
and it is exactly the one that fires when somebody deletes an entry from `UnhonouredSubsystems`
and forgets the UI.

Visual snapshots use `Verify.HeadlessBrowsers`, as the spec asks — but note that a snapshot
baseline is precisely the file class `.claude/hooks/turn-review-gate` watches. Every accepted
visual snapshot therefore passes through `alvo-snapshot-judge`. That is correct and is not to be
worked around.

### 6.3 F5 acceptance criteria, made measurable

1. **Fully operable at 375 px with no horizontal scroll** — a Playwright test at that width, not an
   eye.
2. **The visual audit fails if it looks like a default template** — operationalised as: no
   Bootstrap/MudBlazor class in the DOM, tokens present, both themes render.
3. **Everything clickable is exportable as code** — after any UI schema change, `GET descriptor`
   equals what the editor sent. No drift. The editor therefore **mutates the stored JSON document**;
   it does not project the descriptor into typed objects and serialise them back, which narrows
   every key the projection does not know about (§4.6, first row).
4. **The policy simulator answers identically to production** — §6.1, and §2.2.1 restates it as a
   UI rule: **no client evaluates a stored row.** A per-record allowed/refused badge fails this
   criterion by construction, whatever it answers.
5. **WCAG AA contrast** — an automated check over the tokens, not a manual pass.
6. **Keyboard operability** — every primary flow completes without a mouse, asserted in Playwright.

### 6.4 Explicitly out of F5

Deferred with their reason, so a later reader does not read absence as oversight:

| Item | Why not now |
|---|---|
| AI agent (#29) | needs `ISecretStore` (§7.1), which does not exist. **The drawing ships a full assistant surface and it is out of F5 anyway** — the drawer's shape is good design and is kept as one (it proposes a diff, exits through the same `?dryRun=true` every other change uses, and never applies), but nothing in the build answers it, `baas-analyza` §2.8's own criterion — *"prepnutie providera je len zmena connection v UI… kľúč je v secret store"* — is unmet by construction without a secret store, and every transcript a drawing writes for it is a claim no code makes. It returns gated on `GET {m}/info` reporting an AI connection |
| tenant switching (an operator acting in more than one tenant) | cross-tenant capability is *"a deliberate, audited grant, deferred to #42"* (`TenantResolver`). §2.7 ships one tenant per operator instead |
| API-key issuance and revocation | `IApiKeyStore` is `FindAsync` + `TouchAsync`, and `ManageApiKeys` has no route. §4.2 |
| project deletion (the danger zone) | `DeleteProject` has no route. §4.2 |
| csx editor / functions | `functions` are never invoked — warned, not runnable |
| webhook delivery log + redelivery | deliveries happen only from after-hooks and are unsigned; there is nothing to log yet |
| teams, permission matrix | #37 (F7) — `@user` exposes no teams |
| data-level audit | #42 (F7) — §2.4, D4 |
| multi-project management | §2.6 |
| realtime | unhonoured for every entity of every descriptor |

---

## 7. What this design requires of the milestone

Five items are missing from F5 today and the plan does not hold without them. **Four of the five
have no issue**, and a list nobody is accountable for is how debt accumulates — so the accountable
sentence goes here rather than in a PR description that scrolls away: **filing them is the first
task after the PR that adds this section merges**, and the row's "no issue exists" is replaced with
the number in the same commit. They are deliberately not filed before the merge, because an issue
citing a section of a design that is not on `main` cites nothing.

| Item | Action |
|---|---|
| **#212** (Management API) | exists as an issue and already blocks #229/#230; **needs the F5 milestone** |
| **Identity + `IAlvoUserStore` + bootstrap admin** | **no issue exists** — must be filed, and blocks #146 and #227 |
| **#146** (`access` enforcement + the fifth CEL profile) | currently F6; **move to F5**, ordered before #227 |
| **An operator's tenant** (§2.7) | `AlvoUser.Tenant`, honoured by `AlvoIdentityContextResolver` on `TenantResolver`'s confirmation rule — **and `TenantId` reserving its all-zero value**, which `UserId` already does and `TenantId` does not (§2.7). **No issue exists** — must be filed. Blocks the Data screen for every scoped entity, which is two of the three in the example the product ships |
| **`IAlvoUserAdministration`** (§3.7) | a **second** contract in Abstractions — `IAlvoUserStore` is untouched — implemented by `MMLib.Alvo.Identity`, six management routes at `admin`, the credential-set token, and the self-grant guard **in the core** with a `MMLib.Alvo.Testing` contract test. **No issue exists** — must be filed. Blocks a `providers: ["local"]` project ever having a second person |

`#227`'s body must also be corrected: *"Blocked by: nothing. Can start today."* is no longer true.
It is blocked by #146, which is blocked by the identity issue.

## 8. Documents this design will change

- ✅ `docs/architecture/cel.md` — the fifth profile, `Access`, as a column in `_allowedProfiles`.
  **Done** by `.superpowers/sdd/2026-09-18-f5-access-enforcement` (task 7): the column, the split
  `@user`/`@tenant` rows, the `Access` bullet, the `@user.id` gap, the role-literal walk on
  `/access/<level>`, and deviation 1's enforcement claim.
- `docs/architecture/package-boundary.md` — §Current projects gains `MMLib.Alvo.Admin` and
  `MMLib.Alvo.Identity`; the file's own instruction is *"Keep this list current."*
- `docs/architecture/host.md` — the admin and management route prefixes, and the bootstrap
  configuration.
- A new `docs/architecture/management-api.md` — the surface, the conventions it adopts, and D3.
- ✅ `src/MMLib.Alvo/Descriptor/Internal/UnhonouredSubsystems.cs` — the `access` entry leaves (§3.6).
  **Done** by `.superpowers/sdd/2026-09-18-f5-access-enforcement` (task 5): the entry, the three
  paragraphs that argued from it, and the runtime-apply remark that named it.
- ✅ **`schema/project.schema.json` — the `access` block's own description.** Added after a
  security-core review found it unowned. The frozen schema said *"nothing reads or compiles
  this block yet — applying a descriptor that declares it earns a warning naming it, and a level
  referring to a role that `auth.roles` does not declare is therefore not reported today either."*
  Both halves stopped being true the moment the levels compiled at apply. The under-promise is
  harmless at runtime and that is exactly why it was dangerous: **the frozen schema is the artifact
  an agent reads first**, nothing pins its prose, and no test would ever catch the contradiction.
  **Done** by the same plan (task 5), which is the task that flipped warn into honour; the
  description now records apply-time compilation, highest-match-wins, the bootstrap-admin bypass and
  the `@user.id` gap. Description string only — no structural change.
- `docs/architecture/management-api.md` — §The surface gains `IAlvoUserAdministration`'s six
  routes; §"The three `admin` operations with no route" loses `ManageUsers` and keeps the other two.
- `src/MMLib.Alvo.Abstractions/Identity/AlvoUser.cs` — the `Tenant` grant (§2.7), and its remark on
  why a person carries one tenant and not a set.
- `docs/PLAN.md` — §3 once F5 begins to close.

## 9. What a second pass found, and where it went

This document was drawn before it was read back. The drawing
(`docs/design/f5-admin/`) was then reviewed twice, adversarially, against this repository, and the
reviews are kept beside it at `docs/design/f5-admin/reviews/`. Four of their findings were **not**
defects in the drawing: they were holes here that only a drawing could expose, because each is a
question a prose design can leave unasked and a screen cannot.

| What the drawing could not answer | Where it is answered |
|---|---|
| A signed-in operator has no tenant, so Data is dead for every scoped entity | §2.7 |
| The simulator returns a predicate; the drawing evaluated a row | §2.2.1 |
| A `providers: ["local"]` project can never have a second person | §3.7, §3.8 |
| Three kinds of edit, three pending queues, one preview showing one of them | §4.5 |

That is the argument for drawing before building, stated once: the four gaps cost a weekend to find
here and would each have cost an implementation plan and a PR to find later.

---

## 10. What building it found

Section 9 is what a drawing found that prose could not. This is the next layer down: what a
**running application** found that neither could — and, in the last five rows, what an adversarial
read of the finished branch found that even driving it did not. Each was discovered by building or
attacking the thing rather than by reading it, and each is recorded here because the next person to
touch this surface will otherwise rediscover it the same way.

| What was wrong | Why it was invisible until it ran |
|---|---|
| **The layout was not interactive.** Each page declared `@rendermode InteractiveServer`; the *layout* around it did not, because the router renders it outside the page's subtree. The sidebar, the theme toggle and the ⌘K palette were static markup with handlers that were never wired. | Nothing fails. The screen is correct, the pointer works, and only the keyboard is dead — which no screenshot review looks at. |
| **…and the two obvious fixes are both wrong.** Declaring the mode on the *layout* does not run: a layout receives `Body` as a `RenderFragment`, and a delegate cannot cross a static-to-interactive boundary. Declaring it unconditionally on the *router* makes the sign-in screen interactive, and an interactive page cannot reliably post an antiforgery-protected form. | The answer is the framework's own: the router's mode is chosen **per request**, and the sign-in screen opts out with `[ExcludeFromInteractiveRouting]`. The chrome is interactive everywhere else, which is what a dashboard whose navigation is half its interface needs. |
| **The authentication forms had to opt out of enhanced posting.** Blazor's enhanced form handling posts through `fetch` and patches the DOM; the answer to a sign-in is a `Set-Cookie` and a redirect. The cookie arrived and the redirect was applied as a patch, leaving the operator on the screen they had just completed. | It looks like a wrong password. `data-enhance="false"` on both auth forms, with the reason beside them. |
| **`window.Blazor` is not "the keyboard works".** The circuit connects before a component has imported the interop module and subscribed to the key map, and a keystroke in that window is swallowed — the map runs, nothing listens. | A readiness signal on the document element, set by the palette once it has actually subscribed. It is not a test hook: it is the answer to *"the shortcut did nothing"*, which is the first thing anybody asks in a console. |
| **The working copy cannot live in a circuit.** A Blazor Server scope ends on a browser reload, so unapplied edits died on F5. | Nobody reloads while composing a change — until they do. It is now a singleton store keyed by operator: one copy per person, never shared, in memory. A restart still loses a draft, and that is written down rather than discovered. |
| **An entity added at runtime answers `404` on `/api` until the host restarts.** The Data API's route literals are entity names and the endpoint table materialises on the first request (#103). | The dashboard keeps working, because it reaches rows through the data port — so the gap is invisible from inside the product and obvious to anyone who then calls the API. The entity's API tab now says it. |
| **A static-asset manifest can answer `200` with an empty body.** In the end-to-end host the environment was set *after* the builder had decided whether to load static web assets, so every asset resolved to nothing. | The page renders, unstyled and inert, and a suite that checked status codes went green. The suite now asserts the length too. |
| **`import()` needs a rooted specifier.** The interop module's path was written the way the stylesheet's is — `_content/…`, which a `<link>` resolves against the document's `<base>`. Handed to `import()` it is a *bare specifier*: a package name, which a browser with no import map cannot resolve. | The import rejects inside `OnAfterRenderAsync`, Blazor **terminates the circuit**, and the page is left rendered and completely dead — no theme toggle, no palette, no keyboard. It reads as a broken application rather than as a missing file, and the server log is the only place the word "specifier" appears. |
| **The self-grant guard's role arm is unreachable.** `ManageUsers` is an `admin` operation and `admin` is the highest level, so no caller who passes the gate can raise their own level. | §3.7's U3 argues the guard on the assumption that a caller below `admin` could reach the member. The guard is **kept** — the condition that makes it unreachable is one line of the level table — and both it and the contract suite now say so, rather than shipping a test that asserts an outcome no caller can produce. The **tenant** arm is reachable, and is the one the tests exercise. |

| **`AddIdentityCore` registers no token providers.** `IssueCredentialTokenAsync` — the member §3.7 argues for at length, because it is how a second person gets a password without an administrator typing one — threw *"no `IUserTwoFactorTokenProvider` named 'Default' is registered"* the first time it ran. | The design reasoned about the *policy* and never about the composition. One named provider is registered, not the default set: the default set also advertises email, phone and authenticator providers, and this package has no mail transport, no SMS and no second factor. |
| **Every change made through the dashboard was anonymous.** The apply was sent with no `Author`, so Configuration history rendered every one of them as *code-first or system* — which is true of a mounted descriptor and false of a person who clicked Apply. | §3.7's whole argument for preferring the recorded route over a quiet one rests on that field being filled. Nothing failed; the record was simply empty, and would have stayed empty until somebody went looking for who had changed something. |
| **The editor rewrote every apostrophe in the descriptor.** `System.Text.Json`'s default encoder escapes `'` as `\u0027` for HTML safety, and a CEL rule is mostly apostrophes — so adding one field turned every rule in the file into `\u0027dispatcher\u0027 in @user.roles`. | The descriptor still round-tripped: JSON unescapes to the same string and the apply never noticed. §6.3-3's criterion — *everything clickable is exportable as code* — was satisfied in the letter and broken in the spirit, because the file a person commits and diffs had been mangled by a control that was asked to add a field. |
| **`networkidle` never arrives.** A server-interactive page holds a SignalR connection open for its whole life and sends a keep-alive down it, so *"no network activity for 500 ms"* is a condition that may never hold. A settle helper called before every assertion turned a fifteen-second scenario into one that did not finish. | The habit is imported from static pages, where it is the right wait. Here the signals that mean something are the circuit being up and the keyboard being wired; everything else is waited for by the locator that needs it. |
| **Every scenario passed alone and the class never finished.** Playwright polls a `WaitForFunction` on an animation frame by default, and Chromium stops firing animation frames for a page that is not visible — so the first context's poller stopped the moment a second one opened. | The symptom is the worst kind: green in isolation, hung in the suite, and no error anywhere. Polling on a timer instead is one option object; finding it is an afternoon. |
| **A stock `.gitignore` line swallowed the whole suite.** `*.e2e` is Visual Studio's trace-file pattern; git's ignore matching is case-insensitive on a case-insensitive filesystem, so it matched the project directory `…Tests.E2E`. | `git add -A` said nothing, the local run was green, and CI would have run a test project that is not in the repository. The suite is `…Tests.EndToEnd`, and the ignore file now says why. |

| **A caller resolved once per circuit is a caller who cannot be revoked.** The management and data gateways cached the operator's principal in a scoped instance, and a Blazor Server scope is the *circuit* — so disabling an operator, stripping their roles or revoking their tenant took effect on the next HTTP request everywhere except the one surface built entirely out of one long-lived scope. | §3.7 argues `SetDisabledAsync` as *"a lockout the identity store holds"*, and the cache's own remark claimed the opposite of what the code did. Both gateways now re-resolve per call — one indexed read against a screen that is already making a round trip — and the router is an `AuthorizeRouteView`, because in-circuit navigation never touches the endpoint whose `[Authorize]` closed the door on the way in. |
| **The all-zero uuid was a grantable tenant.** `TenantId.TryParse` was a bare `Guid.TryParse`, and the tenant grant became a *form field* in this release. | It is the one refusal on that path that fails **open**: a caller granted all-zero has a tenant that is *present*, so the predicate is attached and matches every row whose `tenant_id` was defaulted rather than assigned — which a partially migrated dataset really does contain. Refusing it in the type is not enough, because the route binds a bare `uuid` and builds the `TenantId` itself; the refusal is now a fourth guard in `GuardedUserAdministration`, which every transport passes through. |
| **The working copy was keyed by a value two different people share.** `WorkingCopyStore` keyed on `AlvoContext.User`, which is the reserved all-zero id for every caller the resolver refuses — so two unresolvable operators would have composed one descriptor between them. Nothing but a successful apply removed an entry, either. | The store's own remarks said *"per operator, never shared"*; the key was what made that false. A refused caller now gets a private copy that is never stored, and an entry nobody touches inside the idle window is evicted. |
| **The first column added to the identity model had no way of reaching an existing database.** `EnsureTablesAsync` creates the identity tables when absent and does nothing when present, which is right exactly once: `AspNetUsers.TenantId` would have been missing from every database an earlier build created, and the first read would have failed at runtime. | Nothing is released, so the cost was zero and the *mechanism* was the gap. `AlvoIdentitySchema` reconciles the model against the database — probe, then add what is missing, in the active provider's own DDL through EF's migration generator, so no engine-specific SQL enters this package. It refuses what it cannot do safely (a required column with no default, on a table with rows) rather than guessing. |
| **The simulator and the engine disagree about one thing, legitimately, and the screen has to say so.** A write is checked twice — the engine resolves a policy, then `WITH CHECK` runs over the *post-image*. The simulator has no post-image and §6.3-4 forbids it inventing one, so it hands the predicate back and reports `Allowed`. Across the whole cross product of two entities, four operations and three callers, that is the **only** disagreement. | Found by writing the property test rather than by reasoning: 23 of 24 cases agree exactly, and the 24th is the documented boundary of what `Allowed` means. `PolicyVerdictView` already renders it as *"policy resolved"* rather than *"permitted"*, with the predicates beside it — the suite now holds that pairing, so a future simplification of that component fails a test instead of quietly lying to an operator. |

**One thing this suite could not do, recorded rather than quietly dropped.** The rules screen's
*behavioural* scenarios — clicking a caller and reading the verdict, and the Data screen explaining
an empty page — pass individually and do not finish when run together under the **in-process**
host, while the same flows drive perfectly against a host in another process. The cause is in the
interaction between that host and the test platform and was not found. The class therefore keeps
the claim that is an acceptance criterion (§6.3-4, *no client evaluates a stored row*, asserted at
both widths as an absence) and `PolicyScenarios`' own remarks say what was dropped and where it is
covered instead. A suite that hangs a CI job is worse than one that is honest about its edges.

**The pattern in all of them is the same**, and it is the argument for §6.2's insistence that the
end-to-end suite runs whole on every PR: each of them leaves a screen that renders, or a suite that
passes. None would have been caught by a review of the diff, a screenshot, or a unit test — only by
driving the thing, and in the last case only by checking that what ran was what was committed.
