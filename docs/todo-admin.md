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
   **Read half done** in `0ccaf1d` — and it needed no schema-model change after all (§8c): the Fields tab reads
   each row's declaration from the working copy and badges `hidden`, `hidden (conditional)`, `readOnly` and
   `readOnly (conditional)`, through the same `DescriptorLens.PolicyOf` the Data screen's masks and locks use.
   **Still open:** the edit half (off / always / CEL), which authors policy and is B6's.
10. ([#268](https://github.com/Burgyn/MMLib.Alvo/issues/268)) **The project's own identity is unreachable** — `description` and `branding` (5a), plus a
    field's `description` (5c). Three keys the schema says are for the admin UI, in a dashboard
    that renders none of them.
    **Deferred → stays open on [#268](https://github.com/Burgyn/MMLib.Alvo/issues/268)**, by the maintainer's scope
    ruling of 27 Sep. Only the field's description is shown, on each Fields row, since `0ccaf1d`; the project's
    `description` and `branding` and every description editor remain #268's. When it is built, the schema's own wording decides
    where `branding` goes: "its section in the admin dashboard", "NOT the instance-wide dashboard chrome" — the
    project's card and Overview, not the app bar — and the logo is drawn only as an `https:` URL or a
    `data:image/` URI.
11. ✅ ([#269](https://github.com/Burgyn/MMLib.Alvo/issues/269), **done**) **Refusals still reach two screens out of three.** `FieldEditor` filters
    `capabilities.refused` to `field.*` and the "On write" tab now reads the three action types —
    that half is done. What is left: nothing consumes the `entity.*` slots, so `softDelete` is
    still silent on the entity header; and the Fields *tab* — the list, as opposed to the editor
    over it — shows no refusal, so a field carrying `validation` looks unremarkable until you open
    it.
    **Done:** the entity half in `66817f6` and `c558a5a`; the Fields-row half in `4003d62`, with the row's detection
    pinned to the real build's dry run form by form and a renamed entity's removed rows read under their applied
    name (B1 fix round 1) — a staged field that
    carries `validation`, a `$cel` default or a `rollup.where` leads its row with a danger badge, for the slots the
    build publishes (`RefusalPlaces` gained the `FieldsList` screen). Decided: no owner or area field on
    `ManagementRefusedFeature` — which screen shows a refusal is this dashboard's layout, not a fact about the
    feature, and the field would grow Abstractions for one consumer; the map stays in Admin, kept honest by
    `Unplaced` and its fail-loud end-to-end fact (recorded in `RefusalPlaces`' remarks).
12. ([#270](https://github.com/Burgyn/MMLib.Alvo/issues/270)) **The read-only halves of Access are a trap.** The role catalogue and the three management
    levels are rendered beside membership controls that do write, under a paragraph promising that
    a change waits for an apply. Either wire them into the working copy or say plainly that they
    are descriptor-only.
13. ([#271](https://github.com/Burgyn/MMLib.Alvo/issues/271)) **`formats`, `realtime`, `auth.providers`, `tenancy.enabled`, `dynamicEntities`** — five
    declared blocks with no screen and no sentence. The cheap fix is one honest surface (a
    "declared, not editable here" panel, as Integrations already does for `webhooks`/`templates`)
    rather than five editors.
    **Partly done** in `aa402bf` + `45edf80` (B2, narrowed by the maintainer's ruling of 27 Sep): the Overview's
    "Declared, not honoured by this build" panel says `auth.providers` (a provider other than `local`), an explicit
    `realtime: true` and `dynamicEntities` in the build's own sentences (the default realtime is said once, on
    Settings), and `DeclaredLimits` honours `enabled: false` and every other
    block declined by value. **Still open on #271:** the editors — the `tenancy.enabled` switch (with its plan step
    in Preview), the `formats` editor and the `field.format` picker — and showing `tenancy.enabled` and the declared
    `formats` themselves.

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
18. ✅ **Rollback is blind and always destructive.** No dry-run plan, `allowDestructive: true` hard-coded
    (History.razor:137-139). Probably: the Preview flow — dry run, show steps with `destroys`, destructive
    permission as its own confirm.
    **Done:** History asks the rollback's plan as a dry run and shows it with Preview's own step rendering
    (PlanSteps); a plan that destroys data asks its own typed confirm, and allowDestructive is sent only
    then (RollbackGate). No e2e: two revisions need a second in-process apply, which does not finish
    (ChangeTheBackendScenarios).
19. ✅ **Four refusals are rendered nowhere** — `rollup.where`, wildcard `trigger.event`, `JSONata`,
    `bodyFile`. Each screen filters `capabilities.refused` by a prefix none of them match
    (FieldEditor.razor.cs:81, HooksTab.razor.cs:86, Integrations.razor:71-77). Extends #269. Probably:
    give refusals an explicit owner/area in `ManagementRefusedFeature` instead of prefix matching.
    **Done:** RefusalPlaces maps every published slot to its screens (field editor: rollup.where; On write: JSONata, email.data; Integrations: bodyFile; Automations/Functions: trigger.event; entity header: softDelete), and a slot it does not place is shown on Overview — an e2e fact fails on one.
20. ✅ **The Automations page denies the block exists.** `"automations"` vs `"automation"`
    (NotYet.razor:57, US:127). One-word fix plus a fact asserting each NotYet page finds its warning.
    **Done:** the page reads "automation", and an e2e fact asserts both not-yet pages find their warned block.
21. ✅ **`storage: dynamic` is mis-rendered, and the build drops it silently.** Listed as not-applied forever,
    badged `physical table` (SchemaList.razor:204, PS). Core question first: an entity the mapper
    discards without a warning (Map:44-46) is the silent case `UnhonouredSubsystems` exists to prevent.
    **Done** in `aa402bf` + `45edf80`: the core warns at apply on every `storage: dynamic` entity, whether or not
    `dynamicEntities` is enabled (`UnhonouredSubsystems.WithinBlocks`, slot `entity.storage`, served in
    `capabilities.warned`) — warned, not refused, because nothing is wrongly permitted; `PendingSchema` reads
    `storage`, so the list row and the entity header say "dynamic — not honoured by this build (F7, #41)" instead
    of "not applied yet", the header's storage badge reads `dynamic`, and the screen prints the build's sentence.
    Preview (`b903573`) no longer calls a copy that adds or changes a dynamic entity "the schema is unchanged": it
    names each one, against the applied descriptor, with the build's `entity.storage` sentence, and says the apply
    records them in the descriptor and appends a revision. The driver itself stays F7 (#41).
22. ✅ **The staged view of a field loses facets.** `PendingSchema` reads 11 facets; `rollup`, `computed`,
    `index`, `default`, `nullable` vanish from a row the moment it is changed (EC:279-286, PS:78-89), and
    a pending entity without `tenancy` is drawn global. Probably: map the staged field through the same
    shape as the applied one, or badge from the JSON directly.
    **Done:** PendingSchema reads index, default (literal only), nullable, rollup and computed, and resolves a missing tenancy through tenancy.enabled; applied and staged rows are badged by one FieldBadges, which also badges an explicit nullability.
23. ✅ **People beyond the first 50 are unreachable.** `new AlvoUserQuery()` — no search, no next page
    (Access.razor:153). The port already takes `Search` and `After`.
    **Done:** Access searches by address and pages forward and back through the port's own Search/After (PeoplePaging). The identity store's keyset cursor did not translate to SQL (`string.Compare(…, Ordinal)`), so every second page threw; it now compares by the database's collation, pinned by AlvoIdentityUserAdministrationPagingTests. A person created while the list is not the whole directory narrows it to their address in the same one read, and the screen says so with Clear the search (spec §3.5, amended 27 Sep). The search ignores case (the normalised address, as sign-in matches), pages are ordered and keyed by the unique normalised user name, TotalCount counts before the cursor, a no-match search offers Clear the search, and a pager button that goes with its page hands focus to the other one — on Access and the Data grid alike.
24. ✅ **An entity cannot be removed.** `WorkingCopy.RemoveEntity` has no caller (WC.Entities:93). Probably
    a Remove beside Rename, behind `ConfirmByName`, naming the refs and rollups that point at it.
    **Done:** Remove sits beside Rename; the confirm (`AlvoConfirm`, spec §3.2's typed name) names every ref, rollup, entity.update action and trigger that points at the entity (EntityReferences.Inbound), each with what becomes of it (EntityRemovalWords); it refuses only while a ref or a rollup would make the apply refuse, and otherwise asks for the entity's name before WorkingCopy.RemoveEntityUnlessReferenced checks and removes under one lock. An applied entity the copy removed is badged "removed — not applied yet" on the schema list and its own screen, which then offers no edit.
25. ✅ **Data writes are last-writer-wins.** No precondition on update/delete although audited entities
    carry a version (DataGateway.cs:123,130). Then the lesser gaps: structured filters beyond `ilike`,
    multi-sort, batch, replace.
    **Done** in `0b8ed3c`: on an audited entity the record editor's Save and the grid's Delete send the `updated_at` the record was opened with as the port's `AlvoPrecondition` (`RecordVersion`, the value `/api` takes as `If-Match`); a write that lost to another writer — a stale version, or a record deleted meanwhile — is refused in place, "This record changed since you opened it", with Reload: in the editor it reads the record again and reopens it as it is now (focus back on the form's first field), on the grid it reads the page again and gives focus to the row. Never a snackbar, never a silent overwrite. An unaudited entity's editor says once that the last write wins there (no version is sent, since the port would refuse one). Pinned by `RecordVersionTests` and `RecordConflictScenarios` (another writer changes the row through the data port while the editor or the delete confirm is open). The lesser gaps (structured filters, multi-sort, select, `POST …/query`, `PUT` replace, batch → #200) are not offered and not sentenced on the grid: it has no help surface to carry one line, so B4.3 was skipped under ruling 5.
26. **Hooks are half-authorable.** No edit in place; mutate literals and multi-field mutate not offered;
    `{{…}}` webhook `payload` withheld as if refused; endpoint, template and mutate field are free text
    while endpoints and templates cannot be declared here at all — so a webhook/email hook cannot be
    completed from the dashboard alone. Probably pickers over declared names, and 13/#271's panel.
    **Deferred → [#276](https://github.com/Burgyn/MMLib.Alvo/issues/276)**, by the maintainer's scope ruling of 27 Sep.
27. ✅ **Keys the build ignores without a word** — `auth.providers`, `realtime`, `branding`, project
    `description`, `formats.*.description`. Extends #271/#268: the core should warn (they belong in
    `UnhonouredSubsystems` or a sibling), and the dashboard then renders that warning like the others.
    **Done** in `aa402bf` + `45edf80` + `b903573` (B2 fix round 1), one answer per key: `auth.providers` other than
    `local` is warned at apply (slot `auth.providers`, #36); `realtime` is **reported, not warned** —
    `capabilities.warned` lists `entity.realtime` while the apply stays quiet, since its default is `true` (the
    recorded deviation in `CapabilityReport`, #38) — and the default is said **once**, on Settings under *This build*,
    while the Overview lists `entity.realtime` only where an entity declares `realtime: true`; `branding`, the project
    `description` and a format's `description` are metadata the build honours, so no core row and no row under
    "Declared, not honoured by this build" — one quiet line on Overview names what this dashboard does not show yet
    (#268, #271). `DeclaredSlotsAgreementTests` holds the dashboard's slot names to the real report **and** each
    reported row's declared-or-not answer to the core's own predicate, over every `examples/*.alvo.json` and the
    cases declined by value.
28. ✅ **Ref targets and index candidates are applied-only.** A pending entity cannot be a ref target
    (EC:95); a staged field cannot join an index (Indexes.razor:125). Read both from the working copy.
    **Done:** ref targets and index candidates come from the working copy, and so do the standalone Rules page's entity tabs (a pending entity shows the copy's rules, with no simulation). `users`, the reserved ref target the validator accepts beside the declared entities (`DescriptorValidator.ReservedUsersEntity`), is verified never offered — a separate, pre-existing gap this item did not touch.
29. Cosmetic: `x-*` extensions never shown; `unique` not offered for `ref` (one-to-one), `enum`,
    `decimal` with no reason given (FF:68); Add entity writes `audit: false` / `tenancy: global`
    explicitly; Overview shows the `dynamicEntities` warning for `enabled: false`; apply sends no
    idempotency key.
    **Deferred → [#277](https://github.com/Burgyn/MMLib.Alvo/issues/277)**, by the maintainer's scope ruling of
    27 Sep: none of it is needed for day-to-day use of the dashboard.
    Except one part, which landed with #271's in `45edf80`: the Overview no longer lists `dynamicEntities` for
    `enabled: false`.
30. ✅ **A person the dashboard creates can never sign in.** Access → *Add a person* creates the account
    **without a password** (deliberately — `IAlvoUserAdministration.CreateAsync` remarks), and *Issue a
    credential token* mints a single-use token (`IssueCredentialTokenAsync`, ASP.NET Identity's reset
    token) "for them to set their own password" — but **nothing in the build redeems it**: no endpoint, no
    screen, no CLI (grep for a `ResetPasswordAsync` caller: none; the only `MapPost`s are sign-in and
    sign-out, `AlvoAdminSignIn.cs:54-55`). So only the bootstrap administrator can ever sign in. Found by
    the maintainer asking "how does a user get in?", 24 Sep 2026 — the §5f pattern a third time (written,
    tested by contract, reached by no one). Probably: an unauthenticated *Set your password* screen at
    `{admin}/set-password` (email + token + new password twice, antiforgery and the same rate limit as
    sign-in, the token consumed through `UserManager.ResetPasswordAsync`, the refusal wording not saying
    which half was wrong), and the Access token panel showing the link to it. Security-sensitive — the
    `alvo-security-core-review` checklist and `/security-review` apply.
    **Done:** design `docs/superpowers/specs/2026-09-27-f5-admin-set-password-design.md`. Identity (edbb91b, ddd85f7, 3b6a154): `AlvoSignIn.SetPasswordAsync` redeems the token once (policy first, then the account, then Identity's reset; one `Refused` for unknown, disabled, bootstrap, expired, used and foreign), the NIST policy (15–128, no composition, no address; a new bootstrap seed under it is refused at start), and the session check compares the security stamp, so a password set and a disable end every cookie and open tab. The sign-in timing oracle §0.3 found was **fixed**, not filed (spec §10.3): every refusal Identity makes without hashing now pays one dummy verification by the registered hasher. Dashboard and host (79d561d, 12f9f49, 11a0c8f): the token panel shows the set-password link (address and token in the fragment) with Copy link; the static page `/admin/set-password` and its endpoint; a two-layer credential limit (20 a minute per client and subject, 200 per client) charged after antiforgery; a 16 KB body bound on the anonymous posts. End to end (`test(f5): a created person sets a password and signs in`): create → issue → open the link in a fresh browser → no fragment after load → set → sign in as them; a used link gets the generic refusal; with JavaScript off the token is pasted by hand; an open tab drops to sign-in after a password is set elsewhere. `docs/architecture/host.md` documents the limit, its residuals, forwarded headers, the key ring and the policy. The per-account lockout's denial-of-service trade-off is item 46; the dashboard under a PathBase is item 45.
31. ✅ **Settings can say "connected" for a connection whose key is missing.** The live case that drove F5's
    failure-text work (24 Sep 2026): `Alvo:Ai:ApiKeySecretRef` named a secret nobody had saved, the OpenAI
    client sent its own placeholder credential in the key's place, and every turn failed with a 401 the
    operator had no way to see coming — `GET {m}/info` reports `ai.configured` from the endpoint, the model
    and the kind alone, never whether a key actually resolved. `AiConnectionResolver` now logs the missing
    reference by name (`src/MMLib.Alvo/Ai/Internal/AiConnectionResolver.cs`), which fixes the *log*, but
    Settings still has nothing to render — the fix needs `ManagementInfo.Ai` to carry a key state
    (present / missing / not applicable for a keyless endpoint), which is an Abstractions change and was out
    of scope for that task. Probably: a third field beside `Configured` and `Source`, and a Settings badge
    that reads it the way the connection badges already read `Source`.
    **Done:** (e8907f1, 3ec62d7, 5a2b022) `AiKeyState` (`none` / `present` / `missing` / `not-needed`, serialised by name, kebab case like `ai.kind`) is decided by the resolver — an absent, undecryptable or malformed `ApiKeySecretRef` is `missing` whatever the endpoint; with no key, the known key-only hosts (`api.openai.com`, `*.openai.azure.com`, `*.cognitiveservices.azure.com`, `*.services.ai.azure.com`) are `missing` and any other host `not-needed`; a malformed reference is logged by length only, never its value — and carried on `AiConnectionResolution.KeyState` and `ManagementAi.KeyState` to `GET {m}/info` as `ai.keyState` (management routes are outside the OpenAPI document, so no OpenAPI or TeaPie pin moved; the Abstractions baseline grew by exactly those symbols). Settings reads "key missing" instead of "connected", with a warning alert "Key missing — every request will be refused" naming the fix (the deployment's `Alvo:Ai:ApiKeySecretRef` as prose, or for a saved connection the action *Change the connection*), "no key needed" for a keyless endpoint, and nothing extra when a key is present; never the key, never the secret's name. Tests: the resolver per state (and a pasted key never reaching a log line), `ai.keyState` over the wire, `MissingKeyScenarios` over a host whose reference names an absent secret, and Settings scenarios for no key needed, a present key adding nothing, and the saved-connection action. Carried from B2 in the same batch: `ManagementWarnedBlock.Block` and `ManagementCapabilities.Warned` now document the qualified slots.
32. **`AlvoContextAccessor`'s box makes nesting through it unsafe, and it fails silently rather than
    refusing.** Found writing the exit-path facts for `ManagementGateway.AsCallerAsync` (24 Sep 2026): the
    core accessor's setter always nulls the box the *previous* published principal lives in, whether or not
    it is about to install a new one — so a scope that starts publishing while an outer scope already has
    (a Blazor static-rendered pass inside an HTTP request that had already published one, say) loses the
    outer publication the instant the nested scope's first assignment runs, and gets back a box the outer
    scope was never pointed at once the nested scope "restores". It fails safe — to no principal, which the
    core then refuses — rather than composing or silently keeping the wrong identity, so nothing in this
    build has ever misbehaved from it that testing has found; but the failure is silent (no exception, no
    log) and would be confusing to debug from a report of "it refused me and I don't know why". Probably: a
    core-side guard that either supports real nesting (a stack rather than one box) or throws when a second
    publication is attempted while one is already active, so a future nested caller gets a clear answer
    instead of a lost principal.
    **Deferred → [#278](https://github.com/Burgyn/MMLib.Alvo/issues/278)**, by the maintainer's scope ruling of 27 Sep.
33. ([#272](https://github.com/Burgyn/MMLib.Alvo/issues/272)) **A host cannot register its own functions for CEL.** A developer
    wants to write a function in their own code — a name, typed parameters, a typed result — register it at
    startup and use it in a `computed` field, a rule, a hook condition or a `mutate` value. Today the CEL subset
    is closed (a fixed call allow-list in `CelParser`, no registry, nothing on `IAlvoBuilder`); the descriptor's
    `functions` block is csx and warned; after-hooks refuse the `function` action. The issue records the design
    questions — `computed` is a DB generated column and rules are SQL predicates, so a .NET delegate needs a
    per-engine SQL translation or a new "computed at write" rung; hooks and `mutate` run in memory and can call
    one directly. Raised by the maintainer, 24 Sep 2026.
34. ([#273](https://github.com/Burgyn/MMLib.Alvo/issues/273)) **An expression editor and evaluator for computed fields** — and
    the same component for rules, hook conditions and `mutate` values: completion for the entity's fields,
    `new`/`old`/`@user`, the profile's built-ins and #272's registered functions; diagnostics from the core as you
    type; "what would this give?" over sample values. The core stays the only CEL authority (check / scope /
    evaluate endpoints on the Management API; a stored row is read through the Data API, never by Management).
    Raised by the maintainer, 24 Sep 2026.
35. ✅ **A disabled operator, or one moved to another tenant, kept acting from an already-open tab.** High, security core (revocation + tenancy). `AlvoIdentityUserStore.FindAsync`/`FindByEmailAsync` read through `UserManager.Find*` — EF's tracked lookups — on a `DbContext` that lives as long as the Blazor circuit, so every resolution after the first was served from the change tracker and a disable or tenant move written elsewhere never reached the open tab (evidence: `docs/superpowers/specs/evidence/2026-09-25-disabled-operator-stale-identity.md`). **Done** in `7e29461`: both lookups are untracked queries over the store's set (email normalised through `UserManager.NormalizeEmail`, still `SingleOrDefault`), write paths unchanged, and `IAlvoUserStore` now states the obligation for any implementation (reads reflect the store as of the call, never a per-scope cache). Pinned by `AlvoIdentityLongLivedScopeTests` (two scopes, one SQLite file) and `OpenCircuitRevocationScenarios` (disable out of band, no navigation, Apply refused, no revision appended) — both red before the fix.
36. **The assistant resolves its caller once per turn, so a disable lands on the next turn, not the next tool call.** Minor, security core (revocation). `ManagementGateway.AsOperatorAsync<T>(Func<IAsyncEnumerable<T>>)` (~345) resolves the operator once and republishes that principal before every step of the stream, so a turn that started before an administrator disabled the operator keeps running its remaining tool calls as them. Contained while every assistant tool is a read — the rest of that one turn can still read what the operator could; once a tool can write, re-resolve the caller before each tool step (refusing the step, not the turn) so a disable takes effect on the next call, as it does everywhere else in the dashboard. Found by the security review of `7e29461`; deliberately not fixed there.
    **Deferred → [#279](https://github.com/Burgyn/MMLib.Alvo/issues/279)**, by the maintainer's scope ruling of 27 Sep.
37. **Identity administration writes are unversioned: the later write wins, per column.** Minor, security core (revocation). Since `c10f75c` every administration call is its own unit of work — one transaction on a fresh read — so a write from a stale screen is applied to the row as stored and changes only its own column; nothing refuses it for having been made from an old view, and the concurrency stamp only catches a race *inside* the call. Decided, and recorded on `IAlvoUserAdministration`'s remarks. The one place a stale view could move authority — the role chips sending the whole snapshot back, which restored a role revoked elsewhere — now sends a grant/revoke applied to a fresh read (`ManagementGateway.ChangeRolesAsync`), leaving a round-trip-sized window. Follow-up: an expected-version check — the person's version on the port's write members and `If-Match` on the `…/users/{id}/…` routes, 412 on a mismatch — so a stale screen is refused rather than applied. Found by the security review of `c10f75c` (Q1/Q3).
    **Deferred → [#283](https://github.com/Burgyn/MMLib.Alvo/issues/283)**, by the maintainer's scope ruling of 27 Sep.
38. **An unexplained SQLite "database is locked" under the rollback journal fails a cookie-checked request closed, with a 500.** Minor, availability (security core adjacent). One full `scripts/test-admin-e2e` run in three during the `c10f75c` review rounds had 8 `SchemaScenarios` failures: parallel requests re-checking the session cookie got `SQLite Error 5` from `SqliteConnection.Open()`/`Close()` inside `AlvoIdentityUserStore.FindAsync` — outside Microsoft.Data.Sqlite's busy-retry loop, which covers statement execution only. Measured: the host's database runs `journal_mode=delete` (EF Core sets WAL only on a file it creates, and the boot creates this one first); the read runs in no transaction and on no writing connection; 600 parallel reads against ~110k identity and ~118k data writes in the real host reproduced nothing. Suspect: the runtime migrator's DDL applies (EXCLUSIVE at commit under a rollback journal, on its long-lived connection) against pooled connections opening and closing — every failure was in the scenarios that apply. Since `3c61843`, static assets and the sign-in page skip the check, so what remains is a protected page load or a circuit's hub connection during an apply on SQLite, answered with a 500 (fail-closed). Reproduce from the e2e run, not in-process. WAL is not the fix (Spike Q5: `SQLITE_BUSY_SNAPSHOT` on the read-then-write transaction an identity unit of work is), nor `busy_timeout` (measured no-op, and it does not cover open/close).
    **Deferred → [#284](https://github.com/Burgyn/MMLib.Alvo/issues/284)**, by the maintainer's scope ruling of 27 Sep.
39. ✅ **Access shows a temporary sign-in lockout as nothing at all.** Minor, UX (identity). Since the security fix of `9003eab`, *disabled* means `LockoutEnd` ≥ `MaxValue` − 1 day, and a lockout from failed sign-ins is not disabled; but `AlvoUser` carries only `IsDisabled`, so the Access row and editor cannot say "Locked until HH:mm after failed sign-ins" and an operator asked why somebody cannot sign in sees an ordinary, enabled person. Needs Abstractions growth: an additive `DateTimeOffset? LockedOutUntil` on `AlvoUser`, projected by the Identity store when `LockoutEnd` is in the future and below the disabled sentinel; the row's meta and the editor then show it. Deferred from Task 7 of the MudBlazor rebuild, whose scope forbids Abstractions changes. **✅ Done** in `e071782` and `ddf0295`: `AlvoUser.LockedOutUntil` (additive) is projected by both identity reads when `LockoutEnd` lies in the future and below the disabled sentinel (`AlvoIdentityLockout.LockedOutUntil`, against the clock Identity's own check reads), and never for a disable; the port gains `ClearLockoutAsync` (one unit of work: failed-attempt count reset and `LockoutEnd` cleared, security stamp untouched, a disabled person refused by name on the row the unit of work writes; the guard admits it at the manage level, the bootstrap administrator included), routed as `DELETE {m}/projects/{p}/users/{id}/lockout`. Access's row meta and the editor say "Locked until HH:mm after failed sign-ins" in the operator's own time (the browser's offset, read once per circuit; UTC, said as such, until then), and the editor's info alert offers **Unlock** behind an `AlvoConfirm` ("They can try to sign in again now."), with the snackbar after the list is read again and focus on the person's Edit. *Deviation:* Unlock is confirmed though not destructive, because ending a lockout early removes the account's guard against guessing. Pinned by `AlvoIdentityUserAdministrationLockoutTests` (projection, clear, refuse disabled, a disable written elsewhere, stamp untouched, nothing tracked), the contract suite (a disabled person's lockout cannot be cleared; ending the bootstrap administrator's is not refused; the gate), `UserLockoutRouteTests` (200 with the person; 422 for a disabled one, who stays disabled), `UserAdministrationRouteTests`, `LockoutWordsTests` and `LockoutScenarios` (five wrong passwords, the lock shown, Cancel and Unlock pinned for focus, the person then signs in).
40. **An AI connection that cannot be dialled is stored as if it could.** Minor, agent-first (management surface). `IAlvoManagement.SetAiConnectionAsync` writes whatever `StoredAiConnection` it is given, and `AiConnectionResolver.Build` then reads a blank model, an unknown kind or a non-absolute endpoint as no connection at all — so a save over the Management API (or from any caller but the dashboard) succeeds and the status stays "not configured", with nothing saying why. The dashboard's Settings screen checks the model and endpoint before it writes (Task 10 of the MudBlazor rebuild), as a twin of the resolver's rule that it cannot call, and is stricter by one condition: the endpoint's scheme must be http or https, since `Uri.TryCreate(…, Absolute)` admits `localhost:11434/v1` (scheme `localhost`) and, on Unix, `/v1` as a file path. Follow-up: one public validator on `StoredAiConnection` in Abstractions (per field, with a sentence each), used by the resolver, by `AlvoManagementService.SetAiConnectionAsync` (a structured 400 naming the field) and by Settings in place of its own copy — so the three cannot drift.
    **Deferred → [#280](https://github.com/Burgyn/MMLib.Alvo/issues/280)**, by the maintainer's scope ruling of 27 Sep.
41. **A decimal field whose scale exceeds its precision is accepted.** Minor, schema (fail-fast compile). `schema/project.schema.json` bounds precision to 1–38 and scale to ≥ 0 but has no cross-keyword rule, and nothing downstream (apply, `RecordValidator`, the field editor's `FacetNumber`) refuses `scale > precision`, so a descriptor with `{ "precision": 4, "scale": 6 }` reaches DDL, where the engine refuses it (PostgreSQL) or silently accepts it (SQLite). Follow-up: refuse it at descriptor validation with a structured violation naming both facets, and at the field editor under the Scale box. Found by the Task 11 re-review of the MudBlazor rebuild.
    **Deferred → [#281](https://github.com/Burgyn/MMLib.Alvo/issues/281)**, by the maintainer's scope ruling of 27 Sep.
42. ✅ **Importing the text `null` leaves the working copy null.** Minor, UX (working copy). `WorkingCopy.Replace("null")` parses the literal to a null node, assigns it to the working document and returns false (`WorkingCopy.cs`, `Replace`), so the import screen says "not a JSON object" while the copy it just emptied now reads as unloaded — the pending bar, Preview and every schema screen then see no descriptor until the next take. It is kept verbatim by D6 of the MudBlazor rebuild, so it was not fixed there. Follow-up: parse into a local and assign only a JSON object, leaving the copy untouched on anything else (a unit test for `null`, an array and a number). Found by the whole-plan review of the MudBlazor rebuild (M20). **✅ Done** in `3b207d9`: `Replace` parses into a local and assigns only a `JsonObject`; `null`, an array, a number, a string or unparseable text returns false and leaves the copy untouched and unannounced (`WorkingCopyReplaceTests`), so the assistant's call is guarded by the copy itself, not by its caller — and the assistant's drawer now says so in place when the copy refuses its proposal, instead of opening Preview over the old copy (`ProposalTake`, B1 fix round 1).
43. **Every first boot logs "no such table: alvo_identity_users" at error level.** Minor, operability (identity). `AlvoIdentityBootstrap.TablesExistAsync` probes for the identity tables with `store.Users.AnyAsync()` and treats the `DbException` as "absent", which is correct, but EF Core logs the failed command (`Microsoft.EntityFrameworkCore.Database.Command[20102]`) and the failed query (`Query[10100]`) at `Error` before the catch sees it, so a fresh container's first start prints two `fail:` entries naming a missing table — the line an operator greps for when something is broken. Measured in the MudBlazor rebuild's e2e runs: exactly one probe per world boot (64 in a run of 64 worlds), always the bootstrap's `SELECT EXISTS (SELECT 1 FROM "alvo_identity_users")`, printed only because MTP shows a failed run's captured standard output; it was mistaken for a startup race behind the `The_goto_shortcut_and_escape_both_work` flake, which it is not (the hosted service creates the tables before Kestrel listens: `WebApplicationBuilder.Build` adds the web host service last, and hosted services start in order). Follow-up: probe without a failing command — ask the provider's `IRelationalDatabaseCreator`/`HasTables`-style check or the engine's catalogue (`sqlite_master`, `information_schema.tables`) through the dialect — or suppress those two event ids for the probe's own context scope. Found by the final fix batch of the MudBlazor rebuild. **✅ Done (final fix batch B).** The probe is `SELECT 1 FROM <users table> WHERE 1 = 0` sent on the store's own connection, with the table name the model's and delimited by the provider's `ISqlGenerationHelper`, so EF logs nothing when it fails; and the bootstrap now runs in `IHostedLifecycleService.StartingAsync`, so the tables exist before any hosted service's `StartAsync` — the web server's included — also under `HostOptions.ServicesStartConcurrently = true`, which the default sequential order did not cover (final-fix-A re-review, finding 16). Pinned by `AlvoIdentityBootstrapLifecycleTests` (a service registered ahead of the package reads the users table in its own start, sequential and concurrent; a first boot logs nothing at error level). A probe that fails for another reason than a missing table now fails the start with the probe's own words, not the creation's "already exists" (batch-B re-review N5). **Still open:** two replicas cold-starting on one empty database both find no tables, and the loser's `CREATE TABLE` fails its start once (a retry, or `IF NOT EXISTS` DDL through the dialect, would close it); it predates the rebuild. **Deferred → [#285](https://github.com/Burgyn/MMLib.Alvo/issues/285)**, by the maintainer's scope ruling of 27 Sep.
44. ✅ **A record created off the grid's page is not revealed.** Minor, UX (Data). Spec §3.5 (amended 27 Sep) has a created item always on screen, and Access narrows its list to a person created off the page it shows (Task 8 of the audit fixes); the Data grid's `RecordGrid.SelectCreated` looks for the new record on the page it read again, and a record that sorts onto another page, or falls outside the search, is simply not selected or lit. Not cheap there: the grid's search is a pattern over the searchable fields (`GridQuery.Search`), which cannot express "this record", so the fix needs a filter by id (the Data API's `eq` on `id`) shown as a removable chip saying why, or reading the page the new record starts. Found by the Task 8 review. **✅ Done** in `8ae168e`: a record created while the grid is not the whole entity with room on its page (a search, a page either side, or a full page — `GridQuery.ShowsEverything`, Access's rule) narrows the grid to it by the Data API's `eq` on `id` in the same one read, replacing the search; the grid selects and lights it, and says so beside the search, "Showing the record you created." with Clear, which puts back the search and page the reveal replaced and gives focus to the search. Typing a search ends the reveal; deleting the revealed record ends it too, and when another writer deleted it (or the read rule no longer admits it) the grid goes back to what it showed and says so once, never an empty narrowed page naming a read rule. *Deviation from the triage's "id filter chip":* a note line with Clear beside the search, not a chip — the pattern Access's reveal set and spec §3.5's amendment binds to, so the two reveals read alike. Pinned by `GridQueryTests` and `RecordRevealScenarios` (a full sorted page, a record that sorts onto page two; Clear restoring a search; the operator's own delete; another writer's delete). B4 fix round 1 also made the editor say "this record carries no readable version…" rather than "not audited" for an audited record read without a usable version, and pinned an unaudited delete (no version sent) and a save for a record deleted meanwhile (Reload closes the editor) in `RecordConflictScenarios`.
45. **Dashboard under a PathBase.** Minor, deployability (dashboard). Render `<base href>` from `Request.PathBase` in AdminApp.razor (today it is a hard-coded `<base href="/">`, and a circuit's `NavigationManager.BaseUri` comes from it, so `SetPasswordLink.For` omits the prefix); make the form actions (`AlvoAdmin.SignInEndpoint`, `SetPasswordEndpoint`) and `AlvoAdminAssets.*` base-relative; route the sign-in/sign-out redirects through `AlvoAdminRedirect.SeeOther` (they answer 302 through `Results.Redirect` with no PathBase, while the set-password and throttle redirects are 303 with it; the sign-in success redirect's `returnUrl` has to be settled against the prefix first, so it was not folded in). Add one Host test that mounts at `/tenant-a`, signs in, issues a set-password link, checks the link has the prefix, and sets a password. Found by the review of item 30's Task 2.
    **Deferred → [#282](https://github.com/Burgyn/MMLib.Alvo/issues/282)**, by the maintainer's scope ruling of 27 Sep.
46. **Anyone can keep a known account locked out.** Minor before 1.0, security core (availability). `AlvoSignIn.PasswordSignInAsync` signs in with `lockoutOnFailure: true` under Identity's defaults (5 failures, 5 minutes), so about one wrong post a minute from anywhere keeps any known address locked, the bootstrap administrator's included; the credential rate limit (per client and subject, under a per-client ceiling) cannot help, because the attacker needs no more than that. Kept deliberately (ruling 10.8 of item 30's design): it is the only defence against a guesser spread over many addresses. Follow-up: a scheme that does not hand that lever to strangers — a lockout per (account, client) instead of per account, or an exponential per-account delay in place of a hard lock — plus an operator-visible unlock on the person (Access), which item 39 needs anyway. Found by the re-review of item 30's Task 2 (O-1). **Part (i) done** with item 39 (`e071782`, `ddf0295`): an administrator sees the lockout on Access and ends it with Unlock (`ClearLockoutAsync`), the bootstrap administrator's included. **Part (ii)**, the scheme that does not hand strangers the lever, is [#286](https://github.com/Burgyn/MMLib.Alvo/issues/286) (F6).
