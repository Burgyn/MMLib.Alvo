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
| `renamedFrom` | Rename, in the header | **edit** | Item 8, done ([#266](https://github.com/Burgyn/MMLib.Alvo/issues/266)). The key names the **applied** name, never an intermediate; it is dropped on a rename back, and absent for an entity this working copy invented. |
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
| `renamedFrom` | written by the rename | **edit** | The audit said "edit, implicitly" and that was wrong: the field editor **locked** the name, so nothing wrote the key on either level. Both now do (#266). |
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

Ordered by what it costs to leave alone, not by effort. **All seven are filed** — this list is the
reasoning, the issues are the queue. 7 and 10–13 are on [F5](https://github.com/Burgyn/MMLib.Alvo/milestone/6);
8 and 9 are on [F6](https://github.com/Burgyn/MMLib.Alvo/milestone/7), as debt on shipped code.
None carries `ready`: they are gaps this audit found, not confirmed specifications.

7. ([#265](https://github.com/Burgyn/MMLib.Alvo/issues/265)) **A ref field's `onDelete` is chosen by nobody.** Every ref the dashboard creates gets
   `restrict`, written without a control and without a word on screen. The other three semantics
   are reachable only by editing the descriptor by hand. This is the one gap below that changes
   what the *database* does, which is why it is first.
8. ✅ ([#266](https://github.com/Burgyn/MMLib.Alvo/issues/266), **done**) **Nothing can be renamed without dropping it.** `renamedFrom` exists on an entity
   exactly as it does on a field, and the audit recorded that "only the field's is wired" — which was
   itself wrong. Neither was: the field editor **locked** the name and told the operator to edit the
   descriptor by hand, so no screen wrote the key at all. Both are wired now, and the key names the
   applied name rather than an intermediate.
9. ([#267](https://github.com/Burgyn/MMLib.Alvo/issues/267)) **`hidden` and `readOnly` are invisible to the dashboard.** Not merely uneditable: absent from
   `SchemaModel.FieldSchema`, so no screen can show that a field never leaves the server. Fixing it
   starts one layer down, in the schema model, which makes it the only item here that is not purely
   an Admin change.
10. ([#268](https://github.com/Burgyn/MMLib.Alvo/issues/268)) **The project's own identity is unreachable** — `description` and `branding` (5a), plus a
    field's `description` (5c). Three keys the schema says are for the admin UI, in a dashboard
    that renders none of them.
11. ([#269](https://github.com/Burgyn/MMLib.Alvo/issues/269)) **Refusals still reach two screens out of three.** `FieldEditor` filters
    `capabilities.refused` to `field.*` and the "On write" tab now reads the three action types —
    that half is done. What is left: nothing consumes the `entity.*` slots, so `softDelete` is
    still silent on the entity header; and the Fields *tab* — the list, as opposed to the editor
    over it — shows no refusal, so a field carrying `validation` looks unremarkable until you open
    it.
12. ([#270](https://github.com/Burgyn/MMLib.Alvo/issues/270)) **The read-only halves of Access are a trap.** The role catalogue and the three management
    levels are rendered beside membership controls that do write, under a paragraph promising that
    a change waits for an apply. Either wire them into the working copy or say plainly that they
    are descriptor-only.
13. ([#271](https://github.com/Burgyn/MMLib.Alvo/issues/271)) **`formats`, `realtime`, `auth.providers`, `tenancy.enabled`, `dynamicEntities`** — five
    declared blocks with no screen and no sentence. The cheap fix is one honest surface (a
    "declared, not editable here" panel, as Integrations already does for `webhooks`/`templates`)
    rather than five editors.

### 5f. Found after the audit, by using it

The audit walked the descriptor against the dashboard. These three were not reachable that way —
two came from driving the real thing on a phone, one from a review — and they are recorded because
the pattern is the point: **a walk over what a screen *declares* does not see what it *renders*, and
sees nothing at all about what it cannot undo.**

- ✅ **A hook's CEL condition was rendered escaped.** `HooksTab` re-serialised each entry with a bare
  `ToJsonString()`, so the default encoder turned `&&` into `\u0026\u0026` and every apostrophe into
  `\u0027`. `WorkingCopy` carries two paragraphs about exactly this defect for the document as a
  whole; it arrived one re-serialisation later. Every fact that asserted against the *document*
  passed while the *screen* was wrong.
- ✅ **The phone-fit check was measuring the wrong element.** `AssertNoHorizontalScrollAsync` read
  `document.documentElement`, but the shell gives `.a-content` `overflow: auto` — so content wider
  than a phone scrolls that pane and the document never moves. It reported clean on the very route
  whose screenshot showed it scrolling. It now measures the shell pane too, and still excludes
  deliberate scrollers, because a check that fails on a tab strip is a check somebody turns off.
- ✅ **A staged change could not be discarded.** `WorkingCopy.Discard` was fully written and **no
  screen called it**, so an edit could be applied or abandoned with the session and nothing else.
  This is the sharpest of the three for the audit's method: the method asks "can an operator reach
  this", and `Discard` is not a descriptor facet, so nothing in §5a–5d was ever going to ask.

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

## 7. A rollup (and a computed field) cannot be authored — and the editor hides that it is one

Reported by the maintainer on 24 Sep 2026, editing `customers.bikes_count` in the bike-workshop demo.
The Fields list badges it `rollup`, but **Edit** opens the ordinary field editor: `integer` selected
among the eleven type chips, `required` / `unique` / `indexed`, and nothing about `rollup` at all — no
`from`, no `op`, no `field`, no `via`, no `where`, and not even a sentence saying the value is
maintained by Alvo. So:

- **A rollup cannot be created from the dashboard.** `$defs/field.rollup` (`from` + `op`, `field` for
  every op but `count`, `via` when the child has two refs to this parent, optional `where`) is plain
  declarative configuration, not an expression — nothing about it needs a text editor. The same holds
  for `computed`, which is one CEL string exactly like a rule's and could reuse the rules editor's CEL
  input.
- **An existing one can be broken from the dashboard.** The editor preserves the `rollup` key it does
  not draw, but it still offers every type chip and every constraint. Switching `bikes_count` to
  `string`, or ticking `required`, composes a declaration that contradicts its own rollup (a string
  that holds a count; a value the caller must supply and may not write). Whether the apply refuses it
  or keeps it is for §8 to establish — either way the operator was never told they were editing a
  rollup.

**Why the audit in §5 missed it.** §5c recorded `computed` and `rollup` as *"locked — read — right, it
is an expression"*. Both halves are wrong: the editor does not lock them (only `default` is withheld,
`FieldFacets.TakesADefault`), and a rollup is not an expression. The row judged the facet by its name
rather than by opening the editor on a real one — the §5f lesson again: a walk over what a screen
*declares* does not see what it *renders*.

Probably: a **Maintained by Alvo** kind in the field editor, chosen instead of a type rather than
beside one. `Rollup` offers `from` (the entities whose `ref` points here — the incoming list the
Relationships tab already computes), `op`, `field` (that child's numeric fields, hidden for `count`),
`via` (only when the child has more than one ref here) and `where` (CEL, marked `not yet` —
`rollup.where` is in `UnhonouredFeatures`). `Computed` offers the CEL input. Either way the type is
derived (`count` → `integer`; `sum`/`avg`/`min`/`max` → the child field's type), and `required`,
`unique`, `default` are not offered. **Done** — §8d item 14.

The wider question the maintainer asked — *how many more of these are there* — is §8.

## 8. Full audit, 24 Sep 2026 — every schema key, what the build does, what the dashboard does

Method: every property of `schema/project.schema.json` enumerated by a script walking `properties`,
`additionalProperties`, `patternProperties`, `items`, `$ref`, every `oneOf` branch and every
`if/then` (222 raw paths). The six hook points reuse two `$defs` and automation reuses `$defs/action`,
so the raw paths collapse to **115 authorable keys**, one row each below. Build column from
`UnhonouredFeatures`/`UnhonouredSubsystems` plus a grep of `src/` (Admin and Testing excluded) for a
reader of the key; Dashboard column from the component code, not from §5. Audited at **`98849b6`**.
Count: **115 keys — 34 edit / 23 read / 38 said / 20 gap; 14 keys where a screen contradicts or can
break an existing declaration** (marked **contradicts**). Every key is also reachable *raw* by pasting
a whole descriptor on Import (`Transfer.razor:120` `Copy.Replace`) or by an assistant proposal
(`AssistantDrawer.razor:211`); that is not counted as edit.

Legend — Build: **honoured** · **warned** (applies, runs nothing, logged + `capabilities.warned`) ·
**refused** (apply error) · **ignored** (parsed, read by nothing, *not* warned). Dashboard: as §5
(edit / read / said / gap). Evidence abbreviations: `UF` UnhonouredFeatures.cs, `US`
UnhonouredSubsystems.cs, `Map` DescriptorToSchemaMapper.cs, `RR` RollupResolver.cs, `DV`
DescriptorValidator.cs (all `src/MMLib.Alvo/Descriptor/…`); `AHC`/`BHC` After/BeforeHookCompiler.cs;
`PCB` Rules/Internal/PolicyCatalogBuilder.cs; `FF` FieldFacets.cs, `FE` FieldEditor.razor, `EC`
Entity.razor.cs, `PS` PendingSchema.cs, `WC*` WorkingCopy.*.cs (all `Admin/Components/Schema/`);
`Lens` Admin/Internal/DescriptorLens.cs.

### 8a. Schema keys

**Project**

| Key | Build | Dashboard | When it meets one it cannot draw | Evidence |
|---|---|---|---|---|
| `$schema` | ignored (editor hint) | read (raw only) | preserved | AlvoDescriptor.cs:22 |
| `apiVersion` | honoured (const) | read (Preview raw) | preserved | schema const |
| `name` | honoured | read (title, switcher) | — | |
| `description` | ignored — no reader in `src/` | gap | preserved, never shown | grep `descriptor.Description`: none |
| `branding.title` | ignored | gap | preserved | DescriptorBlocks.cs:4; no reader |
| `branding.logoUrl` | ignored | gap | preserved | DescriptorBlocks.cs:10; no reader |
| `revision` | honoured (concurrency) | read (History) | — | |
| `tenancy.enabled` | honoured | gap | **contradicts**: a *pending* entity without `tenancy` is drawn `tenancy: global` even when enabled (it resolves scoped) | Map:36,385; PS:58 |

**dynamicEntities** — the whole block is F7.

| Key | Build | Dashboard | When it meets one | Evidence |
|---|---|---|---|---|
| `dynamicEntities.enabled` | warned when `true` | said (Overview "Declared, with limits") | shown on key presence, so also for `enabled: false` | US:122; DeclaredLimits.cs; Overview.razor:66-79 |
| `.namePrefix` | ignored | said (block-level) | preserved | no reader |
| `.defaultRules.list/get/create/update/delete` (5) | ignored | said (block-level) ×5 | preserved; Rules screen does not list them | no reader |
| `.allowedFieldTypes` | ignored | said (block-level) | preserved | no reader |
| `.maxFieldsPerEntity` | ignored | said (block-level) | preserved | no reader |
| `.maxRecordsPerEntity` | ignored | said (block-level) | preserved | no reader |
| `.maxEntitiesPerTenant` | ignored | said (block-level) | preserved | no reader |
| `.defaultTenancy` | ignored | said (block-level) | preserved | no reader |

**auth / access**

| Key | Build | Dashboard | When it meets one | Evidence |
|---|---|---|---|---|
| `auth.providers` | **ignored** — no reader, no warning; providers are host config | gap | preserved | DescriptorBlocks.cs:55 only |
| `auth.roles` | honoured | read (Access catalogue, Rules) | page says a change "waits for an apply", offers none (#270) | RoleCatalog.cs:49; PCB:249; Access.razor |
| `access.admin` | honoured | read (Access levels) | as `auth.roles` (#270) | ManagementAccessEvaluator.cs:90; Lens:103 |
| `access.developer` | honoured | read | as above | same |
| `access.viewer` | honoured | read | as above | same |

**Entity** (`entities.<name>.*`; `users` is reserved, not authorable)

| Key | Build | Dashboard | When it meets one | Evidence |
|---|---|---|---|---|
| `description` | honoured (OpenAPI) | read (header, list) | not editable | Map:212; SchemaComponentBuilder.cs:325 |
| `renamedFrom` | honoured | edit | carries refs to the entity | WC.Entities:65-90; EntityReferences.cs |
| `storage` | `physical` honoured; **`dynamic`: entity silently dropped** from the applied schema (no warning unless `dynamicEntities.enabled`) | read | **contradicts**: a dynamic entity is listed "not applied yet — does not exist until you apply" forever, and its page badges it `physical table` (PS reads no `storage`) | Map:44-46,173; SchemaList.razor:106-116,204; Entity.razor:88 |
| `tenancy` | honoured | edit once (Add entity), then read | pending-without-key drawn global (see `tenancy.enabled`) | WC.Entities:21; Entity.razor badge |
| `softDelete` | refused | gap (#269) | **contradicts**: a pending entity declaring it gets a plain `soft delete` badge, no refusal | UF:101; PS:62; Entity.razor:86 |
| `audit` | honoured | edit once, then read | — | Map:190; WC.Entities:22 |
| `realtime` | **ignored** — no reader, no realtime channel in `src/` | gap | preserved | EntityDescriptor.cs:40 only |
| `indexes[].fields` | honoured | edit (Indexes tab) | **contradicts**: candidates are the *applied* fields (a staged field cannot be picked, a staged-removed one can); a field rename/remove leaves the index naming a field that no longer exists — no validator check, outcome unverified (EF `HasIndex`) | Indexes.razor:125; Entity.razor:140; WC.Fields:19-68; DescriptorModelBuilder.cs:104 |
| `indexes[].unique` | honoured | edit | — | WC.Indexes:46 |
| `rules.list` | honoured | edit | rename of a referenced field not carried into CEL | RulesTab.razor:73; WC.Rules |
| `rules.get` | honoured | edit | same | same |
| `rules.create` | honoured | edit | same | same |
| `rules.update` | honoured | edit | same | same |
| `rules.delete` | honoured | edit | same | same |
| `x-*` | preserved, not read | gap | preserved | EntityDescriptor.cs:47 |

**Field** (`entities.<e>.fields.<f>.*`)

| Key | Build | Dashboard | When it meets one it cannot draw | Evidence |
|---|---|---|---|---|
| `type` | honoured | edit | **contradicts**: all 11 chips offered on a rollup/computed (§7) | FE Type ChipGroup; FF:124 |
| `description` | honoured (OpenAPI) | gap | preserved, never shown on Fields tab | Map:401; SchemaComponentBuilder.cs:666; Fields.razor Facets() |
| `renamedFrom` | honoured | edit | **contradicts**: rename carries nothing — composite indexes, a child rollup's `field`/`via` (RR refuses), `computed`/rule/hook CEL, `mutate` keys (BHC refuses) all keep the old name | WC.Fields:19-49; RR:252-326 |
| `required` | honoured | edit | **contradicts**: offered beside a `readOnly: true` it does not show → `required`+`readOnly:true` is refused at apply; offered on rollup/computed (accepted, RecordValidator skips it) | DV:438,504,635; RecordValidator.cs:156 |
| `unique` | honoured | edit, 5 types only | **contradicts**: checkbox hidden for ref/enum/decimal/boolean/text/json but the prefilled value is still written — a unique ref cannot be un-uniqued, a string→boolean switch keeps `unique: true` invisibly; offered on a rollup (accepted, no check) | FF:68,118,186 |
| `nullable` | honoured | gap | preserved | Map:405 |
| `default` (literal) | honoured | edit | **contradicts**: kept when switching to `ref` (hidden, then refused at apply) or `json` (accepted, meaning changes); boolean default coerced — anything but `"true"` saved as `false` | FF:85,229,252; FieldDefault.cs:190-198 |
| `default.$cel` | refused | said (editor refusal list) | **contradicts**: prefill puts the object's JSON text in the box (`not JsonArray` admits an object) and a string-typed save writes it back as the literal string `{"$cel":…}` — a refused declaration turned into an accepted wrong one | UF:63; FF:125,255 |
| `maxLength` | honoured | edit | **contradicts**: cannot be absent — prefill defaults 120 and Build always writes it, so opening *Edit* on an unbounded string (even to rename) adds `maxLength: 120` and narrows the column | FF:47,120,290 |
| `precision` | honoured | edit | — | FF:293 |
| `scale` | honoured | edit | — | FF:294 |
| `values` | honoured | edit | — | FF:305 |
| `entity` | honoured | edit | targets are applied entities only — a pending entity (and `users`, unverified) cannot be pointed at | EC:95 |
| `onDelete` | honoured (FK) | read (badges) — gap to author (#265) | preserved (`??=`); new refs get `restrict` | Map:510; FF:325; Relationships.razor:23 |
| `format` | honoured | read (badge) | preserved while `string`, dropped on type change (correct) | Map:411,471; FF:280 |
| `validation` | refused | said (editor only) | Fields list silent (#269) | UF:56; FE refused-facets |
| `index` | honoured | edit | — | FF:187 |
| `hidden` (bool) | honoured | read (Data screen masks) — gap on Schema | preserved | PCB:103; Lens:130; FieldMasks.cs |
| `hidden` (CEL) | honoured | read (Data: never searched/sorted) — gap on Schema | preserved | same; GridQuery.cs Searchable/Sortable |
| `readOnly` (bool) | honoured | read (Data: "Calculated") — gap on Schema | preserved; see `required` | PCB:104; Lens:143; FormFields.cs |
| `readOnly` (CEL) | honoured | read (Data: stays a control) — gap on Schema | preserved | FieldLocks.cs |
| `computed` | honoured | read (badge) — gap to author (§7) | **contradicts**: type chips/`unique` offered; badge vanishes once the field is staged-changed (PS drops it) | Map:418; FF:124; EC:279-286; PS:78-89 |
| `rollup.from` | honoured | read (badge) — gap (§7) | **contradicts**: as `computed`; type change accepted by the apply — RR checks no parent type (a `count` rollup on a `string`) | RR:46-69 |
| `rollup.op` | honoured | gap (§7) | same row | RR:334 |
| `rollup.field` | honoured | gap (§7) | child field rename not carried | RR:304-327 |
| `rollup.via` | honoured | gap (§7) | same | RR:252-279 |
| `rollup.where` | refused | gap — its refusal slot is `rollup.where`, not `field.*`, so no screen renders it | preserved | UF:128; RR:107; FieldEditor.razor.cs:81 |
| `x-*` | preserved, not read | gap | preserved | FieldDescriptor.cs:87 |

**Hooks** (`entities.<e>.hooks.*`) — the six points share `$defs/beforeHookList` / `afterHookList`

| Key | Build | Dashboard | When it meets one | Evidence |
|---|---|---|---|---|
| `beforeCreate` / `beforeUpdate` / `beforeDelete` / `afterCreate` / `afterUpdate` / `afterDelete` (6) | honoured | edit ×6 — add and remove only, no edit in place | existing entries rendered raw, removable | HookBuilder.cs:38; WC.Hooks:20,70 |
| before `condition` | honoured | edit | — | HookBuilder.cs:93 |
| before `reject` | honoured | edit | — | HookBuilder.cs:174 |
| before `mutate.<f>` literal | honoured | gap — every value is wrapped in `$cel` | existing literal shown raw | BHC:298-300,392; HookBuilder.cs:177 |
| before `mutate.<f>.$cel` | honoured (not under `beforeDelete`) | edit — one field per hook, field name free text | multi-field mutate shown raw | BHC:267; HookBuilder.cs:34,79 |
| after `condition` | honoured | edit | — | |
| `webhook.endpoint` | honoured | edit (free text; endpoints themselves unauthorable) | — | AHC:246 |
| `webhook.payload` | `{{…}}` template honoured; raw JSONata refused | gap — §3 withheld it as "refused"; only raw JSONata is | JSONata refusal rendered nowhere | AHC:243,379,384 |
| `email.template` | honoured | edit (free text; templates unauthorable) | — | AHC:297 |
| `email.to` | honoured | edit | — | |
| `email.data` | refused | said (Integrations "why no button" only) | not on the On-write tab | UF:210; AHC:306; Integrations.razor:71-77 |
| `function.name` / `.input` (2) | refused | said ×2 (On-write tab) | — | AHC:226; HooksTab.razor.cs:86 |
| `entity.update.entity` / `.recordId` / `.payload` (3) | refused | said ×3 | — | same |
| `http.call.url` / `.method` / `.headersSecretRef` / `.payload` (4) | refused | said ×4 | — | same |

**Automation / templates / formats / webhooks / functions**

| Key | Build | Dashboard | When it meets one | Evidence |
|---|---|---|---|---|
| `automation.<r>.description` | warned (block) | said (Overview) | **contradicts**: the Automations page looks up block `automations`, the build names it `automation`, so it says "not part of the descriptor at all yet" | US:127; NotYet.razor:33,57 |
| `.enabled` | warned | said (Overview) | same page | same |
| `.trigger.event` | warned; **wildcard refused** | said (Overview) | wildcard refusal (`trigger.event`) rendered nowhere | Map:79-115; DV:211; UF:162 |
| `.trigger.schedule` | warned | said (Overview) | — | |
| `.condition` | warned | said | — | |
| `.delivery` | warned | said | — | AutomationRule.cs:25 |
| `.actions[]` (all 5 shapes) | warned; per-type refusals apply to after-hooks only | said | — | AHC:226 is the only caller |
| `x-*` | preserved | gap | preserved | |
| `templates.<t>.subject` | honoured for after-hook email; warned for automation | read (Integrations raw) | — | AHC:297; EventActionExecutor.cs:106 |
| `templates.<t>.body` | same | read | — | same |
| `templates.<t>.bodyFile` | refused when an after-hook references it, else warned | read (raw) | refusal (`bodyFile`) dropped by the Integrations prefix filter | UF:229; AHC:344; Integrations.razor:71-77 |
| `formats.<f>.pattern` | honoured | gap | preserved | Map:128-153; RecordValidator.cs:195 |
| `formats.<f>.description` | ignored (unverified — no reader found) | gap | preserved | |
| `webhooks.endpoints.<n>.url` | honoured for after-hooks | read (Integrations raw) | — | AHC:246 |
| `.secretRef` | warned — not read, no HMAC | said (Integrations warning) | — | US:138-143 |
| `.description` | ignored (unverified) | read (raw) | — | |
| `functions.<f>.script` | warned | said (Functions page, Overview) | — | US:145; NotYet.razor:57 |
| `.trigger.http.route` | warned | said | — | |
| `.trigger.http.method` | warned | said | — | |
| `.trigger.schedule` | warned | said | — | |
| `.trigger.event` | warned; wildcard refused | said | wildcard refusal rendered nowhere | Map:86 |
| `.execution` | warned | said | — | |

### 8b. Capabilities that are not descriptor keys

| Capability | Where it lives | Dashboard | Evidence |
|---|---|---|---|
| 11 `IAlvoManagement` operations | port | all reached (§5d) | IAlvoManagement.cs:55-263 |
| Rollback **dry run** | `ManagementRollbackRequest.DryRun` | **gap** — rollback runs blind: no plan, and `allowDestructive: true` is always sent after the typed-name confirm, beside text saying "permission to lose data is never implied" | History.razor:137-139 |
| Apply idempotency key | `ManagementApplyRequest.IdempotencyKey` | gap (never sent; double-click safety rests on `ExpectedRevision`) | ManagementGateway.cs:177-183 |
| People list | `IAlvoUserAdministration.ListAsync(search, limit=50, after)` | **partial** — first 50 only, no search, no next page | Access.razor:153; IAlvoUserAdministration.cs:125 |
| Create / roles / tenant / disable / credential token | same port | reached | Access.razor, PersonRow.razor |
| API keys | `IApiKeyStore` is Find/Touch only | said (Settings) — nothing to reach | IApiKeyStore.cs:10,16 |
| Remove an entity | `WorkingCopy.RemoveEntity` | **gap** — written, **no caller** (the §5f `Discard` pattern again) | WC.Entities:93 |
| Edit a hook in place | — | gap — remove + re-add, which also moves it to the end of an ordered list | WC.Hooks:20-51 |
| Data API list: PostgREST filters, multi-sort, `select` | `GET /api/{e}` | partial — `ilike` over string fields only, one sort column, no projection | GridQuery.cs Search/Sort |
| `POST …/query` | DataApiEndpoints.cs:335 | gap | |
| `PUT` replace | DataApiEndpoints.cs:543 | gap (PATCH only) | DataGateway.cs:119-124 |
| Conditional write (`If-Match`, version) | `AlvoPrecondition` | **gap** — update and delete send none: two operators overwrite each other silently on an audited entity | DataGateway.cs:123,130 |
| Batch create/update/delete | DataApiEndpoints.cs:131-133 | gap (single-row selection) | RecordGrid.razor:122 |
| Record history / versions | not in the build | said (History subtitle) | History.razor |
| Realtime | not in the build | n/a | no SignalR/SSE/WebSocket in `src/` |
| OpenAPI docs | host | reached (item 4) | |
| Sign-in / sign-out | AlvoAdminSignIn.cs:54-55 | reached | SignIn.razor |
| Export of the *working copy* | — | gap — Export is the applied descriptor, so hand-editing staged work means losing it | Transfer.razor Export panel |

### 8c. Corrections to §5

| §5 row | Said | Today |
|---|---|---|
| lead, 5d | "all twelve `IAlvoManagement` operations" | Eleven; the table lists eleven. |
| 5a `automation` | said — Right | Wrong on its own page: `NotYet.razor:57` looks up `automations`; the build's block is `automation` (US:127). Overview does list it. |
| 5a `dynamicEntities` | gap | Stale: Overview "Declared, with limits" lists it with the build's sentence (Overview.razor:66-79). |
| 5a `templates` / `webhooks` | read — Right | The "Why there is no new endpoint button" panel's prefix filter admits only `email.data`; `bodyFile` and `JSONata` are dropped (Integrations.razor:71-77). |
| 5a `auth.providers`, 5b `realtime`, 5a `description`/`branding` | gap | Also **ignored by the build** — no reader, no warning. A "not editable here" sentence would be false; the sentence owed is "this build does nothing with it". |
| 5b `storage` | read — Right | Wrong for `dynamic`: dropped by the mapper, listed as not-applied forever, badged `physical table` (8a). |
| 5b `hooks` | edit | Add/remove only; mutate literal and multi-field, and a `{{…}}` webhook `payload`, are honoured and not offered. §3's "`payload` and `data` are both JSONata slots this build refuses" is half wrong: `payload` as a template is honoured (AHC:379). |
| 5b `indexes` | edit | Candidates are applied fields; field rename/remove is not carried into the index. |
| 5c lead | "nothing below is destroyed by an edit — it is only unauthorable" | Wrong: `maxLength` is injected, a hidden `unique` is rewritten, a `default` survives into a type that refuses it, a `$cel` default is rewritten as a string (8a). |
| 5c `maxLength`, `unique` | edit | Edit with a hidden write each (8a). |
| 5c `hidden` / `readOnly` | "SchemaModel carries no Hidden, so the dashboard cannot read it even to display it" | Stale: `DescriptorLens.Masks/Locks` read both from the descriptor and the Data screen honours them. Only the Schema screen is silent — #267's "starts one layer down" premise no longer holds. |
| 5c `computed` / `rollup` | locked — read — right | Already corrected by §7; also the badge disappears once the field is staged (PS drops both). |
| 5c `entity` | edit | Applied targets only. |
| 5d `RollbackAsync` | ✓ | Reached, but never dry-run and always destructive (8b). |
| 5f | the pattern "written and no screen calls it" | Recurs: `WorkingCopy.RemoveEntity`. |

### 8d. The queue (continues §5e)

Already filed and not repeated: **#265** (`onDelete`), **#266** done, **#267** (premise stale — 8c),
**#268** (identity — add that the build ignores these keys too), **#269** (extended by 19),
**#270**, **#271** (extended by 27).

14. ✅ **Rollup and computed cannot be authored, and the editor contradicts them** — §7. Add from this
    audit: `unique` is offered on a rollup and accepted by the apply; the apply checks no parent type
    (RR:46-69), so a `string` count rollup applies; the `rollup` / `computed` badge vanishes once the
    field is staged. Answer as §7, and withhold `unique` alongside `required`/`default`.
    **Done:** the field editor has a Value kind — written by callers / rollup / computed. A rollup offers the working copy's sources (refused ones said: dynamic, tenancy), op, the child's number fields (decimals for avg), via when ambiguous, and the build's where refusal; its type is derived; required/unique/default are withheld and a declared one is kept and said. An existing rollup/computed field opens in its kind, and its badge survives staging.
15. ✅ **Opening Edit on an unbounded string adds `maxLength: 120`.** FF:120 prefills 120 when absent and
    FF:290 always writes it, so a rename or a `required` tick narrows the column — a DB change nobody
    chose (destructive-plan guard catching it: unverified). Probably: prefill null, write only when set,
    allow clearing; same for new fields rather than a silent 120.
    **Done:** `maxLength` is optional in the editor — prefilled from the declaration, written only when
    set, clearable; a new string is unbounded unless a limit is typed.
16. ✅ **A field rename or removal carries no references.** Composite indexes, a child rollup's
    `field`/`via`, `computed`/rule/hook CEL and `mutate` keys keep the old name (WC.Fields:19-68).
    Some are structured refusals (RR, BHC); an index naming a missing field has no validator check
    (outcome unverified). Probably: an `EntityReferences`-style carry for fields, and a removal that
    names what still points at the field before staging.
    **Done:** a rename carries composite indexes, a child rollup's field/via/where, mutate keys, {{new./old.}} placeholders and — token by token — the entity's own rules/computed/hook CEL; a place it cannot rewrite safely is named on the Fields tab. A removal names every place first and is refused while one of them would make the apply refuse it.
17. ✅ **Hidden controls still write.** `unique` hidden for 6 types but rewritten (FF:68,186); `required`
    offered beside an unseen `readOnly: true` → refused (DV:504); a `default` kept into `ref` (refused)
    or `json` (accepted, new meaning) (FF:229); boolean default coerced to `false` (FF:252); a `$cel`
    default rewritten as a string literal (FF:125). Probably: one rule in `FieldFacets.Build` — a facet
    the current type/kind does not draw is either carried untouched and *shown*, or removed with a line
    saying so; never rewritten from a stale prefill.
    **Done:** `FieldFacets` keeps or removes every undrawn facet and says which ("Not drawn here");
    unique is offered wherever it is declared; a default never follows a retype into ref/json;
    boolean/uuid/date/enum/length defaults are refused as `FieldDefault` refuses them; a `$cel` default
    is kept and refused until removed; required beside `readOnly: true` is refused as `DescriptorValidator`
    refuses it.
18. **Rollback is blind and always destructive.** No dry-run plan, `allowDestructive: true` hard-coded
    (History.razor:137-139). Probably: the Preview flow — dry run, show steps with `destroys`, destructive
    permission as its own confirm.
19. **Four refusals are rendered nowhere** — `rollup.where`, wildcard `trigger.event`, `JSONata`,
    `bodyFile`. Each screen filters `capabilities.refused` by a prefix none of them match
    (FieldEditor.razor.cs:81, HooksTab.razor.cs:86, Integrations.razor:71-77). Extends #269. Probably:
    give refusals an explicit owner/area in `ManagementRefusedFeature` instead of prefix matching.
20. **The Automations page denies the block exists.** `"automations"` vs `"automation"`
    (NotYet.razor:57, US:127). One-word fix plus a fact asserting each NotYet page finds its warning.
21. **`storage: dynamic` is mis-rendered, and the build drops it silently.** Listed as not-applied forever,
    badged `physical table` (SchemaList.razor:204, PS). Core question first: an entity the mapper
    discards without a warning (Map:44-46) is the silent case `UnhonouredSubsystems` exists to prevent.
22. **The staged view of a field loses facets.** `PendingSchema` reads 11 facets; `rollup`, `computed`,
    `index`, `default`, `nullable` vanish from a row the moment it is changed (EC:279-286, PS:78-89), and
    a pending entity without `tenancy` is drawn global. Probably: map the staged field through the same
    shape as the applied one, or badge from the JSON directly.
23. **People beyond the first 50 are unreachable.** `new AlvoUserQuery()` — no search, no next page
    (Access.razor:153). The port already takes `Search` and `After`.
24. **An entity cannot be removed.** `WorkingCopy.RemoveEntity` has no caller (WC.Entities:93). Probably
    a Remove beside Rename, behind `ConfirmByName`, naming the refs and rollups that point at it.
25. **Data writes are last-writer-wins.** No precondition on update/delete although audited entities
    carry a version (DataGateway.cs:123,130). Then the lesser gaps: structured filters beyond `ilike`,
    multi-sort, batch, replace.
26. **Hooks are half-authorable.** No edit in place; mutate literals and multi-field mutate not offered;
    `{{…}}` webhook `payload` withheld as if refused; endpoint, template and mutate field are free text
    while endpoints and templates cannot be declared here at all — so a webhook/email hook cannot be
    completed from the dashboard alone. Probably pickers over declared names, and 13/#271's panel.
27. **Keys the build ignores without a word** — `auth.providers`, `realtime`, `branding`, project
    `description`, `formats.*.description`. Extends #271/#268: the core should warn (they belong in
    `UnhonouredSubsystems` or a sibling), and the dashboard then renders that warning like the others.
28. **Ref targets and index candidates are applied-only.** A pending entity cannot be a ref target
    (EC:95); a staged field cannot join an index (Indexes.razor:125). Read both from the working copy.
29. Cosmetic: `x-*` extensions never shown; `unique` not offered for `ref` (one-to-one), `enum`,
    `decimal` with no reason given (FF:68); Add entity writes `audit: false` / `tenancy: global`
    explicitly; Overview shows the `dynamicEntities` warning for `enabled: false`; apply sends no
    idempotency key.
