# F5 admin: an entity's audit, seen in the dashboard (#290)

**Status:** approved 2026-09-29 (the clickable mock and this document). §9's questions are ruled in §10; the
maintainer can override any ruling later.
**Issue:** #290. **Related:** #288 (record history in an audit entity), #42 (the audit stream, F7), #154 (outbox
retention, F6).
**Binding context:** `2026-09-24-f5-admin-mudblazor-design.md` §3 (the pattern language, D3: a pattern §3 lacks is
added to §3 first), `2026-09-18-f5-admin-dashboard-design.md` D4, §2.4 and §4.4.
**Mock:** `mock.html`, a single clickable file (example bike-workshop data).

## 1. Problem

> "Some of my schemas have audit switched on. That should be done already. I want to be able to look at it in Admin
> mode."

`audit: true` works: every row carries who created it and who last wrote it, and when. The dashboard hides all four
columns. The Data grid leaves them out, the record editor leaves them out, and the only trace is a grey `audited` badge
on the Schema screen. The operator switched a feature on and gets nothing to look at.

## 2. What exists: findings, with evidence

### 2.1 What `audit: true` does

| Fact | Evidence |
|---|---|
| The descriptor trait adds four framework-managed columns: `created_at`, `created_by`, `updated_at`, `updated_by`. | `schema/project.schema.json:509-513` |
| The mapper injects them: `created_at`/`updated_at` are required instants, `created_by`/`updated_by` are nullable uuids ("actor"). If an entity declares one of these names itself, the entity is refused. | `src/MMLib.Alvo/Descriptor/DescriptorToSchemaMapper.cs:264-270`, `Descriptor/Internal/ManagedColumnNames.cs:153-175` |
| One stamp authority, `AlvoAuditStamp.Applied`, which every `IAlvoData` implementation calls. A **create** stamps all four columns (updated = created). An **update** stamps only `updated_*`. The time comes from `TimeProvider`. | `src/MMLib.Alvo.Abstractions/Schema/AlvoAuditStamp.cs:54-85` |
| The actor is `AlvoContext.User`. It is **`null` for the anonymous caller** (the all-zero id is reserved to mean "no identity"). | `AlvoAuditStamp.cs:87-88`, `Identity/UserId.cs:13-22` |
| The system caller has one reserved id, `…0000a1`. It is private, and the only way to read it is `AlvoContext.System(null).User`. | `Identity/AlvoContext.cs:12, 54-59`, `Events/AlvoEventProvenance.cs:80` |
| **An API key authenticates as a `UserId`** (`ApiKeyRecord.User`). A key-made write therefore records that user id, never the key id. From the row alone you cannot tell a key from a person. | `Auth/ApiKeyRecord.cs:14-15`, `src/MMLib.Alvo/Auth/Internal/ApiKeyContextResolver.cs:127-137` |
| A caller can never write these columns. Forgery is pinned by an adversarial test. | `AlvoManagedColumns.IsCallerWritable`, `src/MMLib.Alvo.Testing/Data/AlvoDataAdversarialTests.cs:1120-1191` |
| `updated_at` is also the **optimistic-concurrency version** (If-Match / `AlvoPrecondition`). | `AlvoManagedColumns.cs:150-154`, `src/MMLib.Alvo.Admin/Components/Data/RecordVersion.cs:40-49` |
| Instants are normalised to UTC on both engines, so sorting and range-filtering `updated_at` behave the same on SQLite and PostgreSQL. | `src/MMLib.Alvo.Data.EntityFrameworkCore/Internal/StoredInstant.cs:5-22` |
| On a physical entity the managed columns cannot be masked, so any caller the read rule admits sees the four values, the uuids included. | `RecordVersion.cs:20-23` |
| **Keeps no history.** An update overwrites `updated_*`, and nothing keeps the earlier values. | `AlvoAuditStamp.cs:44-47` |
| There is no automatic index on `created_at`/`updated_at`. | `DescriptorToSchemaMapper.cs` has no index for them (checked) |
| The issue says bike-workshop has "1 entity" with audit. **It has 6 of 8.** `order_lines` and `rental_fleet` are the unaudited ones. complex-crm has 4 of 6. | `examples/bike-workshop/bike-workshop.alvo.json`, `examples/complex-crm/crm.alvo.json` |

### 2.2 What the dashboard does with it today

| Fact | Evidence |
|---|---|
| The grid **leaves every managed column out** of its columns, its search and its sorts. | `Components/Data/GridColumns.cs:143-151`, `GridQuery.cs:50-58` |
| The grid already reads **newest first on an audited entity** (`created_at desc`), but never shows the column it sorts by. | `GridQuery.cs:115-125` |
| The grid does not sort a `ref`, because a uuid order means nothing to a reader. The same holds for `updated_by`. | `GridQuery.cs:137-149` |
| The record editor has a read-only **"Calculated" readout** (`a-readout`). It is the one existing place for values that cannot be edited. | `Components/Data/RecordForm.razor:66-85`, `wwwroot/alvo.css:1489-1522` |
| The Schema entity header **already has the badge** `audited` / `not audited`, and the Schema list shows `audited`. The Fields tab lists the four columns under "Maintained by Alvo". | `Components/Schema/Entity.razor:155`, `SchemaList.razor:252-255`, `Fields.razor:69-86` |
| Audit can be chosen only when an entity is created ("edit once, then read"). | `WorkingCopy.Entities.cs:17-22`, `docs/todo-admin.md:421` |
| Times: the grid draws a datetime as invariant `yyyy-MM-dd HH:mm` in the value's own offset (UTC) and does not say it is UTC. `OperatorTime.Clock` is "one format for every time the dashboard draws" (operator zone, date only when not today, `UTC` suffix when unknown). The grid does not use it yet. | `Components/Data/GridCell.cs:100-106`, `Internal/OperatorTime.cs:33-49` |
| The dashboard reads rows **only through `IAlvoData`** with the operator's own `AlvoContext`, so tenancy, default-deny and masks apply unchanged. | `Internal/DataGateway.cs:8-26, 48-61` |

### 2.3 Resolving a user id to a person

| Fact | Evidence |
|---|---|
| `IAlvoUserAdministration` has `ListAsync(Search, Limit, After)`. The search matches an **address**. There is **no lookup by id** and no batch lookup. | `Identity/IAlvoUserAdministration.cs:44-56, 181` |
| The whole port is guarded at **Admin**: `ListAsync` → `EnsureMayManage` → `ManagementOperation.ManageUsers`, which requires `ManagementLevel.Admin`. | `src/MMLib.Alvo/Management/Internal/GuardedUserAdministration.cs:55-58, 152-158`, `Management/Access/ManagementOperations.cs:30` |
| `IAlvoUserStore.FindAsync(UserId)` exists, but it is the **unguarded** read that the context resolver makes. The port's own rule is that authorization never sits in the consumer. So the dashboard must not use it to look up names. | `Identity/IAlvoUserStore.cs:33-38`, `IAlvoUserAdministration.cs:22-27` |
| The operator's **own** id is known without a people read (`ManagementGateway.SelfAsync`), and so is the operator's address (`AuthorAsync`). | `Internal/ManagementGateway.cs:244-270` |
| A host can have **no membership store**. In that case `CanAdministerPeople` is false. | `ManagementGateway.cs:254-260` |
| No reverse map from user to API key exists. `IApiKeyStore` has only `FindAsync(keyId)` and `TouchAsync`, and `ManageApiKeys` has no surface. The demo seeds every row through a dev key whose user is `5eed0000-0000-4000-8000-000000000001`, which is **not a dashboard account**. | `Auth/IApiKeyStore.cs:4-16`, `ManagementOperations.cs:29`, `scripts/demo-admin:106, 183-189` |

### 2.4 The outbox: are the pre- and post-images kept, and can anything read them?

| Question | Answer | Evidence |
|---|---|---|
| Does every write carry both images? | Yes. Create, update, replace, delete and the batch forms all append an event in the caller's transaction, with `Record` (post) and `OldRecord` (pre). **Both are unmasked.** | `EfAlvoData.cs:258, 1025, 1315, 1606, 2416, 2636, 2731`, `OutboxEventFactory.cs:33-43, 70-71` |
| Are they kept after dispatch? | **Yes, forever, by omission.** Dispatch sets `dispatched_at`. Nothing deletes a row. The code's own remark says the payload "holds the complete unmasked post- and pre-image of every write for every entity and tenant". **#154 (F6) exists to add retention**, which will make the store incomplete by design. | `OutboxTable.cs:94-107`, `EfCoreOutboxStore.cs:128`, `src/MMLib.Alvo/Events/Internal/EventLog.cs:25-30`, issue #154 |
| Can anything read them through a port? | **No.** `IOutboxStore` has `EnsureAsync`, `AppendAsync`, `ClaimAsync`, `MarkDispatchedAsync`, `ReleaseAsync`: a queue with no query. `IAlvoData` and `IAlvoManagement` have no event read. | `Events/IOutboxStore.cs:76-200`, `Management/IAlvoManagement.cs:55-231`, `Data/IAlvoData.cs:255-732` |
| Would reading them bypass the security core? | **Yes, on every axis.** The payload is one JSON blob with no tenant column (`OutboxTable.cs:50-61`: provenance is not duplicated into columns). The images are **unmasked**, so `hidden` fields would appear. No `select` rule is compiled into a read of `alvo_outbox`, and a row that the read rule now excludes would still show its history. A "what changed" view built on the outbox would be a second path to rows. That is exactly what `DataGateway`'s remarks and the dashboard design's D4 forbid. | as above; `DataGateway.cs:12-18`; `2026-09-18-f5-admin-dashboard-design.md` §2.4, D4 |
| Can the outbox tell a key from a person? | No. `AuthTypeOf` answers `api_key` for every identified caller that is not the system caller, and that includes a signed-in person. | `Events/AlvoEventProvenance.cs:29-35` |

### 2.5 What the sources say the full thing is

- `baas-analyza.md` §5: audit is an **append-only stream kept apart from application data**, with "different retention,
  different read permissions". A record has actor, action, target, **before/after or diff**, time, tenant, request id and
  result. It is tamper-evident. "Kto smie čítať audit" (who may read the audit) must be separated, and reading it is
  itself audited. §2.8: the data browser has an "audit of admin interventions".
- `alvo-specifikacia.md` §F7 table, row 7.10: *Audit — append-only stream, hash chaining, GDPR export/erasure*. That
  is **#42**.
- The F5 dashboard design, §4.4: *"Data-level audit joins as a second tab [of Configuration history] when #42 lands."*
  §2.4: reads are not audited in v0.1.

The `audit` trait is therefore **who and when on the row**, not the §5 audit. This screen must not claim more than that.

## 3. Recommendation on history ("what changed")

**Out of #290. Defer it to #288, and the cross-entity feed to #42.** #290 shows who and when, the last change only.

Why not the outbox:

1. It has no read port. Adding one (`IOutboxStore.QueryAsync`, or an event read on `IAlvoData`) would put a
   second, unfiltered path to row data behind a public port.
2. Every image is unmasked and carries no tenant column. Filtering tenancy, `hidden`, and the `select` rule in the
   dashboard would re-implement the security core outside it (§0 principle 5).
3. #154 will add retention, so a history built on it would silently lose versions. An audit trail that can quietly
   miss a change is the weakness #288 is told to avoid.
4. The sources want the audit **apart** from the transport queue, under its own read permission (§5).

#288's audit entity is an ordinary entity, read through `IAlvoData` under its own rules. When it lands, the record
detail gains a **History** section that reads it. The block this design adds has a slot for that section and says
until then that no earlier versions are kept (§4.2).

## 4. UX by screen

The pattern language rules apply as written: §3.3 feedback, §3.5 lists, §3.6 loading/empty/error, §3.7 navigation and
§3.8 fields. This design adds **one pattern to §3**: **§3.9, narrowing a list by a facet** (below). D3 requires that.

### 4.1 Data grid (audited entity)

**Column.** One column, **Changed**, comes **after** the chosen content columns. It does not count towards
`GridColumns.Cap`, so no content column is pushed out. It has two lines:

| Line | Value | Drawn as |
|---|---|---|
| 1 | `updated_at` | `OperatorTime.Clock`: `14:05` today, `2026-09-22 09:40` otherwise, `UTC` suffix while the zone is unknown. `title` = full ISO-8601 UTC. `<time datetime>` |
| 2 | `updated_by` | the actor label (§4.4) at `--text-xs`. It is a link that narrows the grid to that actor (like `ref-cell`, `stopPropagation`). A person reads as the part of the address before `@`, with the whole address as `title`. The record detail shows the whole address |

The header sorts by `updated_at` only: asc, desc, none (`GridQuery.Next`). `updated_by` is never a sort key, for the
same reason as a `ref` (`GridQuery.cs:137-149`).

*Why one column and not two.* The mock was drawn first with separate *Changed* and *Changed by* columns. With the
drawer open at 1280 px, `service_orders` (8 content columns + 2) scrolled inside its panel, and the person column
was the one cut off. Two lines in one column fit. The grid's cell width is also bounded, because only the address's
local part is drawn.

`Created` and `Created by` are **not** in the grid. They are in the record detail. The default order stays
`created_at desc`, which the pager already relies on.

**Facets (new §3.9).** A single row under the search, drawn only for an audited entity:

- **Changed** — `ChipGroup`, single choice: *Any time* (default) · *Last 24 hours* · *Last 7 days* · *Last 30 days*.
  This becomes `updated_at gte <now − window>`. The window is taken from `TimeProvider` when the page is read, not when
  the chip is pressed.
- **By me** — a toggle chip, which becomes `updated_by eq <SelfAsync()>`. It is hidden when the operator has no id.
- **Changed by <actor> ×** — a dismissible chip, shown after the person in a *Changed* cell was pressed. It becomes
  `updated_by eq <id>`, or `updated_by is null` for "no identity". It replaces *By me*: two actor filters would never
  both match.

The facets join the quick search with `AlvoAnd`. A reveal still replaces both, as spec §3.5 amended 27 Sep says.
Every facet change reads page 1 again under `RefreshBar`. The chip state is per circuit and is not written to the URL
(see Q4).

§3.9, as it should be written into the pattern language: *"A list narrowed by a facet shows its facets as one row of
chips under the list's search: a single-choice `ChipGroup` per dimension, whose first option is the unnarrowed state,
plus dismissible chips for a value picked from a cell. Every change reads the list again from its first page under
`RefreshBar`. An empty result is §3.6's empty state, whose one action removes the facet that emptied it. A facet is
only ever a filter the Data API would answer for the same caller."*

**Phone (≤ 720 px).** The card (`a-row-card`) gets one pair more, **Changed**, drawn as `14:05 · you` across the card's full width. The facet row
wraps. There is no horizontal scroll at 375 px.

**API equivalence** (the grid never narrows a list in a way `/api` could not):
`GET /api/service_orders?updated_at=gte.2026-09-28T12:05:00Z&updated_by=eq.<uuid>&order=updated_at.desc`.

### 4.2 Record detail (the `AlvoEditor` sheet)

A new read-only section, **Record**, is added after *Calculated* and before the *API* disclosure. It follows the
`a-readout` pattern and has two pairs:

- **Created**: `2026-08-15 17:25 · Katarína Novotná`, with the address under it when resolved.
- **Last changed**: `14:05 · you`.

It closes with one `a-note`: *"Alvo keeps who made the last change and when. Earlier versions of this record are not
kept."* When #288 lands, that sentence is replaced by the History section.

It is shown only when the record is edited (not on *New record*) and only on an audited entity. After *Save changes*
the sheet closes (unchanged). If the record is reopened, it shows the new *Last changed*. There is no second read:
the values come from the row the grid already has, or from the record the write returned.

### 4.3 Schema entity header

The badge is already there (`Entity.razor:155`). Two changes:

- `audited` gets the accent tone (`a-badge--accent`), because it is a feature the operator switched on, and a
  `title`: *"Records who created each record and who changed it last, and when. Keeps no earlier versions."*
  `not audited` stays neutral.
- In the list (`SchemaList.razor:254`) the same title. There is no new control.

Optional, see Q5: a secondary **Recently changed** button beside *Browse records*. It opens Data sorted by
*Changed*, descending.

### 4.4 Who: resolving an actor to a label

These rules run in order. Each is drawn as text and never as a control, except where the cell is the filter link.

| Stored `*_by` | Label | Who sees it |
|---|---|---|
| `null` | **no identity** (`title`: "Written by an anonymous caller, or before the column was filled") | everyone |
| `AlvoContext.System(null).User` | **Alvo (system)** (`title`: "Written by the framework: an automation, hook or rollup") | everyone |
| the operator's own id (`SelfAsync`) | **you** | everyone |
| an id that `IAlvoUserAdministration` resolves | the **address**, e.g. `katarina.novotna@velo-dielna.example`. `AlvoUser` carries no display name, so the address is the name | **Admin level only** |
| any other id (Admin: not a dashboard account; below Admin: not looked up) | short id `5eed0000…0001` in `a-ident`, with the full id as `title` and a copy action in the detail. Admin sees the hint *"Not a dashboard account: an API key's identity or an external caller."* | everyone |

- **The identity rule.** Only an operator who may already list every address (the Access screen, Admin) gets
  addresses. Viewer and Developer see ids, which the Data API already shows them (the column is in the row). So
  *no new disclosure* is made at any level below Admin. The read goes through the guarded port, so the core
  enforces the rule and the screen does not.
- **API keys.** A key authenticates *as* a user id (§2.3), so a key cannot be shown "as a key" from the row. The
  honest label is the table's last row. A user-to-key lookup waits until API keys have a management surface (Q2).
- **No membership store** (`CanAdministerPeople == false`) or a `NotSupportedException`: every row falls back to
  the rules without the port. One `a-note` under the grid says *"People cannot be looked up on this deployment,
  so changes show the caller's id."* This is a state, not an error, so it is not an `AlvoAlert`.
- **The lookup fails** (store down): the same fallback, plus the page-level `ErrorPanel` rule only if the *row* read
  failed. A failed name lookup never blocks the rows.

### 4.5 Empty, refusal and not-audited states

| State | What is drawn |
|---|---|
| **Entity not audited** (`order_lines`, `rental_fleet`) | No *Changed* column, no facet row, no *Record* section. One `a-note` under the "Reading as" line: *"order_lines is not audited, so it keeps no record of who created or changed a row. Audit is chosen when an entity is created."* The editor's existing last-write-wins caveat stays (`RecordVersion.cs:32-35`). |
| **Facet empties the page** | `EmptyState`: *"Nothing changed in the last 24 hours"* (or *"… by you"*), with the body *"The search and the other filters still apply."* and one primary action, **Show any time** (or **Anyone**), which removes the facet that emptied the page. |
| **Scoped entity, operator holds no tenant** | Unchanged: `OutOfScope`, with no read and **no people lookup**. |
| **Read refused** (rules) | Unchanged: `RefusalPanel`. |
| **`*_at` missing from a row** (a host-assembled schema that masks it) | *"hidden from you"*, never *"no identity"*. The two are different states. |

### 4.6 Dark mode, contrast and accessibility

Only existing tokens are used (`--accentSoft`/`--accentInk` for the badge, `--dim` for readout names, `a-ident`).
`ControlContrastScenarios` covers the new badge tone and the facet chips in both themes. The *Changed* header keeps
`aria-sort`. The facet row is `role="group"` with `aria-label="Narrow by change"`. Every chip is a button with
`aria-pressed`. The person links in *Changed* read *"Show only changes by katarina.novotna@…"*.

## 5. Data and port changes

| Change | Where | Public? | Why |
|---|---|---|---|
| Grid *Changed* column, facets, record *Record* section, actor labels | `MMLib.Alvo.Admin` `Components/Data/*` (new `AuditColumns`, `AuditFacets`, `Actor` helpers). Unit-tested like `GridQuery` | **internal** | presentation only |
| `GridCell` datetime → `OperatorTime.Clock` for the *Changed* column | `Components/Data` | internal | one time format. Whether *all* datetime columns switch is Q3 |
| **New port member: batch lookup of people by id** | `IAlvoUserAdministration`: `Task<IReadOnlyList<AlvoUser>> FindAsync(IReadOnlyCollection<UserId> users, CancellationToken ct = default)`. At most `AlvoUserQuery`'s page limit (50) ids per call. An unknown id is simply absent, and the bootstrap admin is returned like anyone | **public** (it is a port, implemented in `MMLib.Alvo.Identity` and guarded in `MMLib.Alvo`) | `ListAsync` searches by address only. Paging through everyone to find 25 ids is the shape that port's remarks call wrong "at the thousands a real deployment has". `IAlvoUserStore.FindAsync` is unguarded (§2.3) |
| Guard for it | `GuardedUserAdministration.FindAsync` → `EnsureMayManage()` (Admin, `ManageUsers`). **No new `ManagementOperation`** | internal | the same disclosure as the Access list, so the same level |
| `ManagementGateway.PeopleByIdAsync` | Admin | internal | the one call per page |
| Contract tests | `src/MMLib.Alvo.Testing/Management/UserAdministrationContractTests.cs`: unknown ids absent, duplicates collapsed, more than 50 refused, order not promised, a disabled person still resolved (an author who was disabled is still the author) | test | interface-first, §0.1 |
| `PublicApi.MMLib.Alvo.Abstractions.verified.txt` grows by **1 member** | — | — | justified above under *public is the contract*. `internal` cannot be used because the implementer is in another package |

**No change** to `IAlvoData`, `IAlvoManagement`, `IOutboxStore`, `IApiKeyStore`, the descriptor schema or either
driver. **No index** is added (see acceptance criterion A9, and Q6).

Adding a member to a public interface breaks any external implementer at the source level. None exist before v0.1
(F6). This is recorded as a deliberate break. The alternative, a default interface implementation that pages
`ListAsync`, is rejected: it would hide an O(people) scan behind a call that looks cheap.

## 6. Acceptance criteria

- **A1.** On an audited entity the grid shows one *Changed* column after at most 8 content columns. The set of
  content columns is **identical** to today's (a unit fact over every example entity). At **1280 px** with the drawer
  open, `service_orders` shows *Changed* without scrolling inside the grid.
- **A2.** The audit data costs **0 extra `IAlvoData` reads** per page: the columns are in the row already read.
- **A3.** Name resolution costs **at most 1 `FindAsync` call per page** (≤ 25 distinct ids with `PageSize` 25). It
  costs **0** below Admin, on an unaudited entity and in the `OutOfScope` state. There is no per-row call (pinned by
  a counting fake).
- **A4.** Every facet is one `AlvoQuery` that `/api` would answer identically for the same caller. A fact builds the
  query string from the facet and compares both result sets over the same fixture.
- **A5.** Sorting by *Changed* keeps keyset paging: pages 1 → 2 → 1 return the same rows in the same order (tie-break
  on id), on SQLite **and** PostgreSQL.
- **A6.** An operator below Admin never receives an address that is not their own. No `FindAsync` call is made, and
  the DOM contains no address in a *Changed* cell except the operator's own, in "you"'s title.
- **A7.** At **375 px**: no horizontal scroll on the grid, the cards, the facet row or the sheet. The sheet is
  full-screen below 600 px (unchanged).
- **A8.** Both themes: the new badge tone and the chips are ≥ **4.5:1** (text at 11 to 13 px), measured composited as
  `ControlContrastScenarios` does.
- **A9.** A page sorted or narrowed by `updated_at` on **10 000** rows of `service_orders` answers within the same
  bound as today's default `created_at desc` page (both unindexed). Measured once. If it is more than 2× slower, Q6
  becomes a blocker.
- **A10.** No string in the new UI says *audit log*, *history* or *version*, except the sentence saying versions are
  not kept.

## 7. E2E scenarios (`AuditViewScenarios`, Microsoft.Playwright, run whole)

1. **Create, then see yourself.** New record on `service_orders` → the revealed row's *Changed* cell reads today's
   `HH:mm` over **you**. The record's *Record* section shows the same for *Created*.
2. **Save moves only Last changed.** Edit and save a seeded record → reopen it: *Created* is unchanged and *Last
   changed* is **you** · now.
3. **Sort by Changed.** Press the header → `aria-sort=ascending`, then `descending`, then none. The row order matches
   `/api?order=updated_at.desc`. The pager preserves it.
4. **Last 24 hours.** The chip narrows, the count drops, and `aria-pressed` is correct. Combined with a search term,
   both apply.
5. **Empty facet.** *By me* on an entity nobody changed as the operator → the empty state *"Nothing changed … by
   you"*. **Anyone** restores the rows, and focus goes to the facet row.
6. **Changed-by link.** Press the person in a *Changed* cell → a chip *"Changed by …"* appears, the grid narrows, and the row
   does **not** open. Pressing × restores.
7. **Identity rule.** As an Admin, a row written by a second person shows their address. As a Viewer-level operator,
   the same row shows the short id and no address anywhere on the page.
8. **Not a dashboard account.** A row written through a dev API key shows the short id and the hint (Admin).
9. **No membership store.** The host without Identity administration → ids plus the one note. The rows still load.
10. **Not audited.** `order_lines`: no *Changed* column, no facet row, the not-audited note. The editor has no *Record*
    section and has the last-write-wins caveat. Schema shows `not audited`.
11. **Schema badge.** `service_orders` header: `audited` with the accent tone and the title text.
12. **Phone.** 375 × 812: a card shows *Changed · you*, the facet row wraps, and `scrollWidth == clientWidth`.
13. **Tenant-scoped, no tenant.** complex-crm scoped entity as an operator without a tenant → the unchanged
    `OutOfScope` state. The counting fake sees 0 lookups.

## 8. Deviations (decisions, not oversights)

| # | Deviation from | What and why |
|---|---|---|
| X1 | Issue #290, "API keys shown as keys" | Not possible from the row: a key authenticates as a `UserId` (`ApiKeyContextResolver.cs:127-137`), and no user-to-key map exists. It is shown as *"not a dashboard account"* plus the id. Deferred to Q2. |
| X2 | Issue #290, "display name and email" | `AlvoUser` has an address and no display name (`AlvoUser.cs:17-24`). The address is the name. A display name is a port change nobody needs yet. |
| X3 | baas-analyza §5, "record content: before/after, request id, …" | This is *who and when of the last change* on the row, not the §5 audit. The UI names it that way (A10). The §5 audit is #42, and history is #288. |
| X4 | `2026-09-18-f5-admin-dashboard-design.md` §4.4 (data-level audit as a second History tab) | Kept, not built. There is no project-wide "recent changes" view in #290: a project-wide feed across entities is N queries on N rules, and it is the #42 stream. Per entity, *Changed ↓* is the recent-changes view. |
| X5 | Pattern language §3 | Adds §3.9 (facets), as D3 requires, instead of an ad-hoc filter bar. |
| X6 | `GridColumns.Cap` (8) | The *Changed* column does not count towards it, so on audited entities the grid has up to 9 columns. Two separate columns were tried in the mock and did not fit at 1280 px (§4.1). On a phone it is one card pair. |
| X7 | "the grid never sorts a uuid" | Kept for `updated_by`. You narrow by person (the link or the chip) instead of sorting. |
| X8 | Management level table | The new member reuses `ManageUsers` (Admin) instead of adding `ResolvePeople` at a lower level. A lower level would leak the directory one id at a time. See Q1. |
| X9 | `ManagementGateway`'s own preference for no new port members | The one new member is justified by scale (§5), which is the same reason that port gives for being paged. |

## 9. Open questions for the maintainer

1. **Who may see addresses?** The proposal is Admin only, the same level as the Access screen. Should Developer see
   them too? That would need a new `ManagementOperation` at Developer level.
2. **API keys.** Is showing a key's identity as *"not a dashboard account"* enough for now? Or should a
   user-to-key lookup come with the API-key management surface (`ManageApiKeys` has none today)?
3. **Time format.** Should every datetime column in the grid move to `OperatorTime.Clock` (operator zone) in the
   same PR, or only *Changed*? Moving only *Changed* leaves two time formats in one row.
4. **Facets in the URL.** Should the facet and sort state go into the Data URL (`?changed=24h&by=me`), so the Schema
   *Recently changed* link and browser Back work? The grid keeps no URL state today.
5. **"Recently changed" on the Schema header.** Add the secondary button, or is *Browse records* plus a header press
   enough?
6. **Index.** Should `audit: true` also create an index on `updated_at` (a descriptor/migrator change, not the
   dashboard's)? Decide after A9 is measured.
7. **Default order.** Keep `created_at desc`, or switch an audited entity's default to *Changed ↓*, so that
   "look at the audit" is the first thing the operator sees?
8. **Issue text.** The issue says bike-workshop has 1 audited entity. It has 6 of 8. Should the issue be corrected?
9. **Turning audit on later.** The Schema screen cannot do it, and the not-audited note says so. Is adding audit to
   an entity that already has rows (with required `created_at`/`updated_at`) in scope anywhere? Not verified; it
   belongs to the migrator, not to #290.

## 10. Rulings (approved 2026-09-29)

The maintainer approved the mock and this design and did not answer §9 one by one. The controller ruled each question
by the design's own default. Each ruling can be overridden later; a follow-up named here is an issue to open, not
work in this PR.

| Q | Ruling | Consequence for this PR |
|---|---|---|
| Q1 | **Admin only** for people's addresses. `FindAsync` reuses `ManageUsers`, as X8 says. | No new `ManagementOperation`. Viewer and Developer see ids, which the Data API already shows them. |
| Q2 | **"not a dashboard account" + the id** is enough for an API key's identity. A user-to-key lookup is deferred to the API-key management surface. | No change to `IApiKeyStore`. X1 stands. |
| Q3 | **Only the new *Changed* column and the *Record* block** use `OperatorTime.Clock`. Switching every datetime column in the grid is a follow-up issue. | Cost, stated: a row can show two time formats, `2026-09-22 09:40` (invariant, the value's own offset) in a content column beside `14:05` (operator zone) in *Changed*. |
| Q4 | **No URL state** for the facets in this PR (follow-up). | Facets are per circuit. A reload or Back drops them. |
| Q5 | **Include "Recently changed"** in the Schema entity header, as the approved mock has it. | See correction C7 for how it opens Data sorted without URL state. |
| Q6 | **No index.** Measure A9 once and record the number here. | Task 10 of the plan measures and writes the result into this row. If a narrowed or sorted page is more than 2× slower than today's default page, Q6 becomes a blocker. |
| Q7 | **The default order stays `created_at desc`.** | *Changed ↓* is one header press, or the Schema button. |
| Q8 | **Correct #290's text later**: bike-workshop has 6 of 8 audited entities. | Note only. No change to the issue from this PR. |
| Q9 | **Out of scope:** enabling audit on an entity that already has rows. | The not-audited note says audit is chosen when an entity is created. |

**Also out of scope** (the maintainer asked about both; each is a follow-up):

- **Showing soft-deleted records, and restoring them, in Data.** The grid reads what the Data API returns for the
  caller, and a restore is a write the Data API does not offer today. This belongs to the soft-delete work, not to
  #290.
- **A project-wide change log** (every change across entities). That is #288 (record history in an audit entity)
  for "what changed" and #42 (the append-only audit stream, F7) for a feed across entities. X4 says why #290 builds
  neither.

### Corrections found while planning

These come from reading the code the design has to live with. None of them changes the approved UX. Each changes how
a stated fact is met, or corrects a fact in the design.

| # | Design text | Correction | Why |
|---|---|---|---|
| C1 | §5 lists no HTTP change for the new member. | `FindAsync` gets a management route, `GET {m}/projects/{project}/users/by-id?id=…&id=…`, at `ManageUsers` (Admin). Like every management route, it is excluded from the OpenAPI document. | `UserAdministrationRouteTests.Every_member_of_IAlvoUserAdministration_has_an_http_route` requires one: *everything the dashboard can do, the API can do*. |
| C2 | §5: the public API grows by 1 member. | `PublicApi.MMLib.Alvo.Abstractions` grows by 1 member, as stated. `PublicApi.MMLib.Alvo.Testing` also grows, by the new contract facts in `UserAdministrationContractTests` and one fact in `AlvoDataPagingTests`. | xUnit discovers only public test methods. A contract suite's facts are public by construction. |
| C3 | A3 and A6: "0 `FindAsync` calls below Admin". | The zero is **measured at the membership store** (the unguarded implementation), by a counting world. The dashboard cannot know the operator's level in advance: no port exposes it, and Access learns it from a refusal too. So it calls the guarded port, the guard refuses before the store, and `ManagementGateway` remembers the refusal until the next navigation. That is at most one refused guarded call per screen visit, and none reaches the store. | The guard is the authority (§4.4, *the identity rule*). A level read added to the dashboard would be a second authority. |
| C4 | §4.6: "Every chip is a button with `aria-pressed`". | The *Changed* window is the single-choice `ChipGroup`, which is a WAI-ARIA **radio group** (`role=radio`, `aria-checked`). *By me* is a toggle button with `aria-pressed`. The *Changed by …* chip is text plus a remove button named *"Remove the filter Changed by …"*. | `ChipGroup` already draws a single choice as a radio group (V7). A second chip semantics for one row would break §3's consistency. |
| C5 | §7 scenario 13: "complex-crm scoped entity". | complex-crm has **no** tenant-scoped entity. The scenario uses field-service `work_orders`, which is audited and scoped. The bootstrap operator holds no tenant there. | `examples/complex-crm/crm.alvo.json`: every entity is global. |
| C6 | §4.6 and A8: "`ControlContrastScenarios` covers the new badge tone and the facet chips". | They are measured **by the same probe** (`ContrastProbe`, composited, both themes) in the audit scenarios, over bike-workshop. | field-service's one audited entity is tenant-scoped, and `ControlContrastScenarios`' operator holds no tenant, so its Data screen draws no facet row. |
| C7 | Q5's button "opens Data sorted by *Changed*, descending"; Q4 rules out URL state. | The button links to `…/data/{entity}?order=updated_at.desc`. The query is read **once, on arrival** (the `?record=` precedent), in the Data API's own `order=` syntax, and only honoured on an audited entity. The screen never writes its state back to the URL. | The only way to open a sorted grid from another screen without URL state. |
| C8 | §4.2: "`2026-08-15 17:25 · Katarína Novotná`". | The *Record* block shows the **address** as the person (X2). The mock's name is illustrative. | `AlvoUser` has no display name. |
| C9 | §4.1: the facets join the quick search with `AlvoAnd`. | `GridQuery.Searchable` keeps room for the facet terms under `AlvoFilter.MaxTerms` (the `or` node, the `and` node and two comparisons). | Otherwise an entity with 253 or more string fields would send a filter the port refuses whole. |
