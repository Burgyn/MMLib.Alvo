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
| **dry run** | `MigrationOptions.DryRun` — already a member |
| destructive gate | `MigrationOptions.AllowDestructive` |
| rollback | `RuntimeSchemaService.RollbackAsync(project, targetRevision, options, ct)` |
| append-only history + optimistic lock | `IDescriptorVersionStore` (`GetCurrentAsync`/`GetAsync`/`ListAsync`/`AppendAsync(…, expectedRevision, …)`) |
| atomic DDL + version insert | `IRuntimeSchemaWriter.ApplyAndAppendAsync` |
| export runtime → file | `DescriptorVersion.DescriptorJson` **is** the export |
| resolved schema | `ISchemaRegistry.GetSchema()` |
| policy evaluation | `IPolicyEngine`, `IPredicateEvaluator`, `IPredicateRenderer` |
| record counts | `AlvoQuery.IncludeTotalCount` → `AlvoPage.TotalCount` (opt-in) |

`MigrationOptions.DryRun` is the load-bearing find: the schema editor's diff, the rollback
preview, and the later AI proposal card are **one mechanism with three consumers**, not three
implementations.

### 2.2 Surface

Route prefix `{m}`, default `/management`, bound from `Alvo:Management:RoutePrefix` (D2).
Default-deny applies exactly as it does to the Data API: unreachable without an explicit policy.

**Descriptor — the one write path to configuration**

| Route | Notes |
|---|---|
| `GET {m}/projects` | project list |
| `GET {m}/projects/{p}/descriptor` | current descriptor JSON + `revision`. **This is the export** — `DescriptorVersion.DescriptorJson`, not a new serialiser |
| `PUT {m}/projects/{p}/descriptor` | apply. `If-Match` carries `revision` (D3); `?dryRun=true` returns the migration plan and guardrail verdict without touching the database; `Idempotency-Key` honoured |
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
roles, tenant), optionally a record id → verdict plus the compiled predicate.

The DoD says the simulator *"answers identically to production"*. The only way that is not a
promise is that the simulator calls **the same `IPolicyEngine`**, never a copy — and §6 pins it by
comparing the simulator's verdict against the Data API's actual response for the same context.

**Meta**

`GET {m}/info` — build, mode (standalone/embedded), engine, startup mode.

### 2.3 `capabilities`: one source of truth for "not yet"

```json
{
  "honoured": ["entities", "rules", "hooks", "tenancy", "auth"],
  "warned": [
    { "block": "automation",
      "consequence": "no rule is ever evaluated, so no declared action runs — which looks exactly like a condition that never matched",
      "issue": 28 }
  ],
  "refused": [
    { "slot": "field.default",
      "consequence": "Field 'default' is not honoured yet: no column default is emitted and the value is dropped before any writer sees it…",
      "fix": "…" }
  ]
}
```

`warned` is projected from `UnhonouredSubsystems.All`; `refused` from `UnhonouredFeatures`.

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

| Route | F5 status | Data source |
|---|---|---|
| **Welcome / first-run** | LIVE | bootstrap admin (§3.5), #230 |
| **Overview** | LIVE, narrowed | `GET schema`, `GET revisions`, `GET capabilities`; counts via `AlvoQuery.IncludeTotalCount` |
| **Data** | LIVE | the existing Data API `/api/*` (§2.4) |
| **Schema** | LIVE read (#228), LIVE editor (#229) | `GET schema` + `GET descriptor`; writes via `PUT ?dryRun=true` → diff → `PUT` |
| **Rules** | LIVE, simulator included | `GET descriptor` + `POST policy/simulate` |
| **Access** | PARTIAL | users and roles LIVE (`IAlvoUserStore` + `auth.roles`); **teams and the permission matrix are #37 (F7)** |
| **Configuration history** | LIVE, reframed | `GET revisions` — §4.4 |
| **Automations** | NOT YET (warned) | `capabilities` |
| **Functions** | NOT YET (warned) | `capabilities` |
| **Settings** | PARTIAL | `GET info` LIVE; API keys LIVE (`IApiKeyStore`); danger zone LIVE |
| **Projects** | LIVE, degraded to one | `GET projects` (§2.6) |

### 4.3 Navigation order

Live sections first, `Not yet` separated below:

```
Overview
Data
Schema
Rules
Access
Configuration history
──────────────────────
Automations      Not yet
Functions        Not yet
──────────────────────
Settings
Projects
```

The reason is the 375 px acceptance criterion rather than tidiness. A bottom navigation bar holds
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
| The simulator answers as production does | property: simulator verdict vs the Data API's actual response for the same `AlvoContext` | ring2 |
| `access` is actually enforced | integration: a caller matching no level gets `403` on every management route | ring2 |
| A role the descriptor does not declare is never minted | unit: `IAlvoUserStore` ∩ `IRoleCatalogProvider`, fail closed | ring0 |
| The `Access` profile is closed | unit per construct: field ref, `@tenant`, arithmetic, `changed()` **do not compile** | ring0 |
| `capabilities` does not lie | reads `UnhonouredSubsystems.All` and compares against the payload — the same shape that already guards that table against the schema | ring0 |
| Four doors, one result | mount / Management API / `FromDescriptor()` produce an identical `SchemaModel` (the CLI door is absent, #213) | ring2 |
| The dashboard is not a policy bypass | integration: the same caller sees exactly the same rows through the dashboard as through `/api` | ring2 |

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
   equals what the editor sent. No drift.
4. **The policy simulator answers identically to production** — §6.1.
5. **WCAG AA contrast** — an automated check over the tokens, not a manual pass.
6. **Keyboard operability** — every primary flow completes without a mouse, asserted in Playwright.

### 6.4 Explicitly out of F5

Deferred with their reason, so a later reader does not read absence as oversight:

| Item | Why not now |
|---|---|
| AI agent (#29) | needs `ISecretStore` (§7.1), which does not exist |
| csx editor / functions | `functions` are never invoked — warned, not runnable |
| webhook delivery log + redelivery | deliveries happen only from after-hooks and are unsigned; there is nothing to log yet |
| teams, permission matrix | #37 (F7) — `@user` exposes no teams |
| data-level audit | #42 (F7) — §2.4, D4 |
| multi-project management | §2.6 |
| realtime | unhonoured for every entity of every descriptor |

---

## 7. What this design requires of the milestone

Three items are missing from F5 today and the plan does not hold without them.

| Item | Action |
|---|---|
| **#212** (Management API) | exists as an issue and already blocks #229/#230; **needs the F5 milestone** |
| **Identity + `IAlvoUserStore` + bootstrap admin** | **no issue exists** — must be filed, and blocks #146 and #227 |
| **#146** (`access` enforcement + the fifth CEL profile) | currently F6; **move to F5**, ordered before #227 |

`#227`'s body must also be corrected: *"Blocked by: nothing. Can start today."* is no longer true.
It is blocked by #146, which is blocked by the identity issue.

## 8. Documents this design will change

- `docs/architecture/cel.md` — the fifth profile, `Access`, as a column in `_allowedProfiles`.
- `docs/architecture/package-boundary.md` — §Current projects gains `MMLib.Alvo.Admin` and
  `MMLib.Alvo.Identity`; the file's own instruction is *"Keep this list current."*
- `docs/architecture/host.md` — the admin and management route prefixes, and the bootstrap
  configuration.
- A new `docs/architecture/management-api.md` — the surface, the conventions it adopts, and D3.
- `src/MMLib.Alvo/Descriptor/Internal/UnhonouredSubsystems.cs` — the `access` entry leaves (§3.6).
- `docs/PLAN.md` — §3 once F5 begins to close.
