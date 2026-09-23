# Admin dashboard — open items

What the dashboard still owes, kept here rather than in a chat scrollback. Each item says what
is missing, why it matters, and what the answer probably is — so whoever picks it up starts from
the decision rather than from the symptom.

Raised by the maintainer while driving the real dashboard from a phone, 23 Sep 2026.

**Status, 23 Sep 2026 — all six are done.** What each item says below is what was wrong; the
`✅ Done` note under it says what landed and, where the item's own premise turned out to be stale,
what the answer really was. The audit in item 5 is the live part of this file: its tables are the
record of what the dashboard still cannot reach, and items **7–13** under §5e are the queue.

## 1. Indexes cannot be created or managed

`field.indexed` and `unique` are honoured by the apply, and the Fields tab *shows* them as badges,
but there is no control that adds or removes one, and nothing at all for a composite index — which
the descriptor carries as an entity-level `indexes` block. So the one thing an operator reaches for
after watching a list get slow is the one thing they have to leave the dashboard for.

Probably: a section on the entity's Fields tab beside "Declared fields", listing the declared
indexes with their fields and uniqueness, and an editor that writes the `indexes` block into the
working copy like every other edit — no new write path, the same preview and apply.

**✅ Done.** Both shapes, and on the **Indexes tab** rather than beside "Declared fields" — the tab
already existed and already rendered the block, so a second list on the Fields tab would have been
two places to read one thing. A composite index is picked field by field, in order, with each chip
showing its position, because order is what a composite index is *for*. A single-field index is the
field's own `index` facet and is now a toggle in the field editor, offered only while the field is
not unique. `AddIndex` appends, so an index this editor cannot draw survives an edit beside it.

## 2. The AI panel never appears

Reported from the phone: Settings shows the connection panel, but no assistant launcher renders
anywhere. Expected when no connection resolves (§3.3 asks for exactly that), so the first question
is whether the connection actually saved — `GET {m}/info` reports `ai.configured` and `ai.source`,
and the panel now writes through `PUT {m}/ai/connection`.

Needs a reproduction on a host with a mounted `Alvo:Secrets:EncryptionKeyFile` and a saved
connection, checking `info` before blaming the drawer. If `configured` is true and the launcher is
still absent, the fault is in `AdminLayout`'s one-shot `IsConfiguredAsync` — it runs once per
circuit, so a connection saved *during* a session does not light the launcher until a reload.
That last part is almost certainly the real answer, and it is a bug: saving a connection should
make the assistant appear without a reload.

**✅ Done — and the guess at the end was right.** The write worked the whole time; `AdminLayout`
resolved "is one configured" once in `OnInitializedAsync` and nothing told it the answer had moved.
`AssistantGateway` now raises `ConnectionChanged` on a successful save and the layout re-asks over
it. A refused save raises nothing, because the launcher is mounted off the announcement and
announcing a write that did not happen would light an assistant that cannot be dialled. Measured by
a browser scenario that saves on Settings and waits for the launcher in the same circuit; removing
the subscription turns it red, which was checked rather than assumed.

## 3. `on write` hooks are not editable from the dashboard

The entity page has an "On write" tab that renders the hook points and says which this build
refuses. Reading them is not editing them. Whether they *should* be editable here depends on what
the build honours — several hook points are in `UnhonouredFeatures`, and an editor for a facet the
apply refuses is the control this dashboard already argues against elsewhere.

So: first the audit in item 5, then an editor for exactly the hook points that are honoured, with
the refused ones staying as the read-only explanation they are now.

**Answered by the audit (23 Sep 2026): none of them are refused.** The premise above was stale on
both halves. `UnhonouredFeatures.OnAnEntity` holds no hook entry at all — PR5a removed the three
`after*` points and PR5b the three `before*`, and the table's own remark records that it kept the
shape rather than deleting it. And the tab does not in fact "say which this build refuses": it
renders each declared point with what that *kind* of point may contain, and reads no capability. So
there is no subset to carve out — the editor is owed for all six points. What the tab does still
owe is the refusal of the three action *types* (`function`, `http.call`, `entity.update`) that
`UnhonouredFeatures` does hold, which it can read the way `FieldEditor` already reads its own.

**✅ Done.** All six points are editable. The point decides what the action may be — mostly because
the schema does: a before-point offers `reject` and `mutate`, an after-point offers `webhook` and
`email`. One restriction is **not** the schema's: `$defs/beforeHookList` is one definition for all
three before-points, but `BeforeHookCompiler` refuses a `mutate` under `beforeDelete` outright (the
row is going, so the patch is discarded), and it refuses `new.` there and `old.` under a
`beforeCreate` — so the editor offers `reject` only at `beforeDelete`, and the condition guidance
names only the row images the selected point actually has. Both were caught by plan-guard rather
than by a test, which is why there are now two. Switching from an after-point to a before-point
takes the network actions away with it
— leaving `webhook` selected would let the editor compose a descriptor the apply refuses, and the
refusal would name a choice the operator never made. `payload` and `data` are deliberately not
offered: both are JSONata slots this build refuses, and a control for a facet the apply refuses is
the control this file argues against elsewhere. The three refused action types are now read off
`capabilities` and shown on the tab, which also closes the `entity.*` half of §5e item 11.

## 4. No link to the OpenAPI documentation

The host serves a generated OpenAPI document and a docs UI, and the dashboard never points at it.
An operator who has just changed a schema is one click from the contract that changed, and that
click does not exist.

Probably: a link on the entity page's API tab and one in Settings, pointing at the host's docs
route — with the caveat that an embedded host may mount it elsewhere or not at all, so the link
appears only when the route is actually mapped.

**✅ Done.** Two links on each of the two screens — the docs UI for a person, the raw document for
an agent. The routes arrive from the host through `AlvoAdminOptions`, since the dashboard references
Abstractions alone and cannot see `AlvoHost.ScalarPath`; null is the default and means no link
rather than a broken one. The standalone host writes them after the configuration bind and nulls
them when `Alvo:Docs:Enabled` is off. It also fixed something worse than the absence: the API tab
named `/openapi/v1.json` in prose on **every** deployment, including those that serve nothing there.

## 5. Audit: what Alvo can do that the dashboard cannot

The dashboard was built screen by screen against the F5 design, not against the product's full
surface, so the gaps are wherever nobody looked. Items 1, 3 and 4 were all found by one person
using it for an hour, which suggests there are more.

The audit is mechanical: walk `IAlvoManagement`, the descriptor schema's top-level blocks and
`UnhonouredFeatures`, and for each ask — can an operator reach this from the dashboard, is it
read-only when it should be editable, and is its absence explained or silent? Output is a table in
this file, not prose.

**Done, 23 Sep 2026, against the code at `d038b29`.** The tables are below. Three things the walk
settled before any of them:

- **The Management API is not the gap.** All twelve `IAlvoManagement` operations are reached by a
  screen (5d). Every hole in this audit is a *descriptor* hole — a block or a facet the schema
  declares and no control writes — which means none of them needs a new port, a new endpoint or a
  new gateway method. They all land in `WorkingCopy` and go out through the one apply.
- **Item 3's premise is stale.** It says "several hook points are in `UnhonouredFeatures`". None
  are: PR5a removed the three `after*` points and PR5b the three `before*`, and the table's own
  remark says so. All six hook points are honoured, so the audit item 3 was waiting for is
  answered — an editor for `hooks` is owed for every point, not for a subset.
- **The distinction that matters is not "missing" but "silent".** A facet this build refuses
  (`validation`, `softDelete`) should have no editor; it should still have a sentence. The tables
  separate *refused-and-explained* from *absent-and-silent*, and almost everything below is the
  second.

Legend: **edit** = an operator can change it · **read** = rendered, not editable · **said** = not
reachable and the dashboard says why · **gap** = not reachable and nothing says so.

### 5a. Descriptor top-level blocks

| Block | Where in the dashboard | State | What that costs |
|---|---|---|---|
| `apiVersion` | Preview (raw JSON) | read | Right. The apply owns the format version. |
| `name` | Overview title, project switcher | read | Right. A project rename is not a dashboard operation. |
| `description` | nowhere | **gap** | The schema says it is "surfaced to agents and in the admin UI". It is surfaced in neither. |
| `branding` | nowhere | **gap** | `title` and `logoUrl`, declared to be shown "wherever the project is presented". Every deployment draws the Alvo mark instead, including one that declared its own. |
| `revision` | History, Preview | read | Right. |
| `tenancy.enabled` | nowhere | **gap** | Nothing breaks — `AddEntity` writes `tenancy: scoped` per entity and `ResolveTenancy` honours it with the block absent. But the project-level switch that makes an *undeclared* entity scoped cannot be set or even seen, so an operator cannot tell which default their next entity will get. |
| `dynamicEntities` | nowhere | **gap** | Eight governance keys. A warned subsystem (F7), so read-only is the right answer — silence is not. |
| `auth.providers` | nowhere | **gap** | |
| `auth.roles` | Access → role catalogue | read | The page's own text says a change here "waits for an apply", and then offers no way to make one. Membership sits beside it and *is* editable, which makes the read-only half read as broken rather than deliberate. |
| `access.admin/developer/viewer` | Access → management levels | read | Same sentence, same page, same problem. |
| `entities` | Schema, Entity | edit | See 5b and 5c. |
| `automation` | Automations (nav) | said | "Not yet", from `capabilities`. Right. |
| `templates` | Integrations | read | Served as stored, with the build's warning. Right for now. |
| `formats` | nowhere | **gap** | Named validation formats. `field.format` has no control either (5c), so a project's own formats are unreachable from both ends at once. |
| `webhooks.endpoints` | Integrations | read | As `templates`. |
| `functions` | Functions (nav) | said | "Not yet". Right. |

### 5b. Entity-level facets

| Facet | Where | State | What that costs |
|---|---|---|---|
| `description` | Entity header, Schema list | read | The absence is explained ("no description in the descriptor") and cannot be fixed from the screen that explains it. |
| `renamedFrom` | nowhere | **gap** | A field carries a rename through `FieldEditor`; an entity does not. Renaming an entity from the dashboard is therefore remove + add — a drop and recreate, which is the exact data loss the key exists to prevent. |
| `storage` | Entity header badge | read | Right: `dynamic` is F7. |
| `tenancy` | written once, by "Add entity" | partial | Nothing changes it afterwards. |
| `softDelete` | nowhere | **gap** | Refused by `UnhonouredFeatures.OnAnEntity`, so an editor would be wrong — but the refusal is never rendered: `FieldEditor` filters `capabilities.refused` to `field.*` and nothing consumes the `entity.*` half. Its absence reads as an oversight rather than a refusal. |
| `audit` | written once, by "Add entity" | partial | As `tenancy`. The Fields tab does show the managed columns that result. |
| `fields` | Fields tab | edit | See 5c. |
| `rules` | Rules tab | edit | |
| `hooks` | On write tab | **edit** | Item 3, done. All six points; the tab now also reads `capabilities` and names the three refused action types. |
| `realtime` | nowhere | **gap** | |
| `indexes` | Indexes tab | **edit** | Item 1, done. |

### 5c. Field-level facets

`FieldEditor` deliberately preserves facets it cannot draw (its own comment says so), so nothing
below is *destroyed* by an edit — it is only unauthorable.

| Facet | Editor control | Fields-tab badge | State |
|---|---|---|---|
| `type` | yes | column | edit |
| `description` | no | not shown | **gap** — the schema says "surfaced to agents and in the admin UI"; an entity's is shown, a field's is not. |
| `renamedFrom` | written by the rename | — | edit, implicitly |
| `required` | yes | `required` | edit |
| `unique` | yes | `unique` | edit |
| `nullable` | no | not shown | **gap** — preserved, never authored. |
| `default` | yes, literal only | `default …` | edit. A `$cel` default is refused by the build, and the editor's refusal says so. |
| `maxLength` | yes | `max N` | edit |
| `precision` / `scale` | yes | `p,s` | edit |
| `values` | yes | `N values` | edit |
| `entity` | yes | — | edit |
| `onDelete` | no — `facets["onDelete"] ??= "restrict"` | `on delete x` | **gap** — the schema declares four semantics and the dashboard writes one, silently, on every ref field it creates. |
| `format` | no | shown | **gap** — readable, unauthorable, and `formats` (5a) is unreachable too. |
| `validation` | no — refusal shown | not shown | Refused by `UnhonouredFeatures.OnAField`; `FieldEditor` renders that refusal, the Fields list does not. |
| `index` | yes, while not unique | `indexed` | **edit** — item 1, done. |
| `hidden` | no | not shown | **gap**, and deeper than the others: `SchemaModel.FieldSchema` carries no `Hidden`, so the dashboard cannot read it even to display it. A field hidden from every API response looks identical to one that is not. |
| `readOnly` | no | not shown | Same, same reason. |
| `computed` | locked (`_maintainedElsewhere`) | `computed` | read — right, it is an expression. |
| `rollup` | locked | `rollup` | read — right. |

### 5d. `IAlvoManagement`

| Operation | Where | |
|---|---|---|
| `GetInfoAsync` | Settings | ✓ |
| `ListProjectsAsync` | Project switcher | ✓ |
| `GetDescriptorAsync` | Preview and every editor | ✓ |
| `ListRevisionsAsync` | History | ✓ |
| `GetRevisionAsync` | History detail | ✓ |
| `GetSchemaAsync` | Schema, Entity, Data | ✓ |
| `GetCapabilitiesAsync` | Integrations, "Not yet" badges | ✓ |
| `SimulatePolicyAsync` | Rules | ✓ |
| `ApplyDescriptorAsync` | Preview | ✓ |
| `RollbackAsync` | History | ✓ |
| `SetAiConnectionAsync` | Settings | ✓ — but see item 2 |

### 5e. What the audit adds to this list

Ordered by what it costs to leave alone, not by effort.

7. **A ref field's `onDelete` is chosen by nobody.** Every ref the dashboard creates gets
   `restrict`, written without a control and without a word on screen. The other three semantics
   are reachable only by editing the descriptor by hand. This is the one gap below that changes
   what the *database* does, which is why it is first.
8. **An entity cannot be renamed without dropping it.** `renamedFrom` exists on an entity exactly
   as it does on a field, and only the field's is wired.
9. **`hidden` and `readOnly` are invisible to the dashboard.** Not merely uneditable: absent from
   `SchemaModel.FieldSchema`, so no screen can show that a field never leaves the server. Fixing it
   starts one layer down, in the schema model, which makes it the only item here that is not purely
   an Admin change.
10. **The project's own identity is unreachable** — `description` and `branding` (5a), plus a
    field's `description` (5c). Three keys the schema says are for the admin UI, in a dashboard
    that renders none of them.
11. **Refusals still reach two screens out of three.** `FieldEditor` filters
    `capabilities.refused` to `field.*` and the "On write" tab now reads the three action types —
    that half is done. What is left: nothing consumes the `entity.*` slots, so `softDelete` is
    still silent on the entity header; and the Fields *tab* — the list, as opposed to the editor
    over it — shows no refusal, so a field carrying `validation` looks unremarkable until you open
    it.
12. **The read-only halves of Access are a trap.** The role catalogue and the three management
    levels are rendered beside membership controls that do write, under a paragraph promising that
    a change waits for an apply. Either wire them into the working copy or say plainly that they
    are descriptor-only.
13. **`formats`, `realtime`, `auth.providers`, `tenancy.enabled`, `dynamicEntities`** — five
    declared blocks with no screen and no sentence. The cheap fix is one honest surface (a
    "declared, not editable here" panel, as Integrations already does for `webhooks`/`templates`)
    rather than five editors.

## 6. Crowded toolbars on a phone

With more than two controls a header row becomes a wall of equal-weight buttons. Wanted: the
ordinary toolbar behaviour — keep the primary action and at most one other, and move the rest
behind an overflow `…` menu.

This is a design-system item rather than a screen one: `PageHeader`'s `Actions` slot should take
a primary action and a collection of secondary ones, and decide at the breakpoint which are shown
and which fold into the menu. The sections sheet already has the sheet pattern the overflow menu
would reuse.

**✅ Done, exactly as described.** `Actions` became `Primary` and `Secondary`; above 720 px they all
sit on the row as before, below it the secondaries fold into an overflow menu built from `Sheet`, so
it inherits the scrim, Escape, focus and scroll lock. One markup tree and one media query, for the
reason the shell gives about a second navigation.
