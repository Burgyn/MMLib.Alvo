# F5 — the hooks editor: hooks authored end to end in the dashboard (slice B, #276)

Status: design, 2026-10-05. Written autonomously from the controller's rulings B1–B9 (decisions already taken by the
architect; recorded below with their cost, not re-litigated). Approvals are **delegated, not given** — the gate is the
PR. Stacked on `feat/expression-check` (PR #298, `cel/check`), whose `ExpressionCheck` state and `ExpressionSlots` this
slice extends.

Inputs read: the [slice A spec](2026-10-01-f5-expression-check-design.md) (slice table §2: B = this),
[analysis](2026-10-01-f5-automation-analysis.md) §1, §2.3, §4.3, §4.5, §6, §9.5 and its
[review](2026-10-01-f5-automation-analysis-review.md) §3 persona 2, §8, §9; `docs/todo-admin.md` §8a hook rows, §8b
"Edit a hook in place", §8d item 26; triage `evidence/2026-09-27-todo-admin-triage.md` §8d 26; the pattern language
[`2026-09-24-f5-admin-mudblazor-design.md`](2026-09-24-f5-admin-mudblazor-design.md) §3 (binding); issues #276, #120,
#152; `docs/architecture/events.md` (delivery, signing, URL, egress passages); `docs/architecture/cel.md` (profiles);
the prototype `docs/design/f5-admin/app.js` (`hooksTab`, `screenIntegrations`, `new-endpoint`, `new-template`).

## 1. Intent and personas

#276, verbatim: *"The On write tab can add and remove hooks but not edit one in place; mutate literals and multi-field
mutate are not offered; endpoints/templates are not pickable; webhook endpoints and templates cannot be declared in
Integrations."* The triage's done-means adds: edit **keeping the entry's position**, pickers over declared endpoints,
templates **and the entity's fields**, a `{{…}}` payload box refusing raw JSONata with the build's own refusal, and
endpoint/template declaration with `secretRef` shown with the build's "unsigned" warning.

| Persona | Needs | Gets in this slice |
|---|---|---|
| **Operator, not a CEL expert** (primary — review §3 p.2) | finish a webhook or email hook without leaving the dashboard; fix a hook without remove + re-add (which also moves it to the end of an ordered list); say "when status is ready" without writing CEL | edit in place; pickers; endpoint/template declaration; a guided `when field op value` condition that **emits ordinary CEL**, with a text switch |
| **Agent / the assistant** | the descriptor it writes stays the descriptor the dashboard reads | no change: the assistant patches JSON (`alvo-descriptor-hooks`); the editor reads every shape it writes and preserves what it cannot draw. Its limit "never a new `webhooks`/`templates` block" stays — that is the assistant's, not the operator's |

## 2. What exists, and the gap (counts on this branch)

| Area | Today | Gap |
|---|---|---|
| `HooksTab.razor(.cs)` (245 + 309 lines) | add (New hook) and remove (confirm `remove-hook`); each hook drawn as raw `CodeBlock` JSON (`HooksTab.razor:84-102`) | **no Edit** (§8b) |
| `HookBuilder.cs` (206 lines) | 4 kinds; mutate = one field, value always `$cel` (`:192-202`); endpoint/template/to free text | literal values, several fields, `payload`; `Action()` rebuilds from scratch, so an edit built from it would drop `payload`, `data`, extra mutate keys |
| `HookBuilder.Images/Example` (`:93-106`) | hard-coded per point | the drift analysis §4.5.2 names — and `afterDelete`'s example names `new.`, which an after-delete has no image of |
| `WorkingCopy.Hooks.cs` (129 lines) | `AddHook`, `RemoveHook(…, expectedList)`, `HooksOf`, `StagedHooksOf` | no replace primitive |
| `Integrations.razor` (92 lines) | read-only `CodeBlock` of the **applied** descriptor (`:82-86`); panel "Why there is no "new endpoint" button" (`:47`) | reads the applied revision, so a staged declaration is invisible; nothing writes `webhooks`/`templates` (0 writers) |
| Tests | `HookBuilderTests` 11, `WorkingCopyHookTests` 9, `ExpressionSlotsTests` 25, e2e `HookEditingScenarios` 9, `ExpressionCheckScenarios` 8 | none for edit, mutate literals, pickers, declarations, guided conditions |
| `todo-admin.md` §8a | hooks 6 points "edit — add and remove only"; mutate literal **gap**; multi-field **gap**; `webhook.payload` **gap**; `email.template`/`webhook.endpoint` free text; `templates.*`, `webhooks.endpoints.*` **read** | 9 rows move to edit (§12 lists them) |

## 3. Rulings, recorded as decisions

| # | Decision (controller ruling) | Cost if wrong | How this design honours it |
|---|---|---|---|
| B1 | Edit in place = `WorkingCopy.ReplaceHook(entity, point, position, expectedHookJson, newHook)` through `Edit`; patch the existing node, never `HookBuilder.Action()` for an existing hook; an undrawable shape is read-only with Remove only; reveal + light the edited hook | a writer that rebuilds drops what it cannot draw — the silent-narrowing class `WorkingCopy`'s own remarks exist against | §5.1–§5.3. **Interpretations, stated:** "opens read-only" = the row shows its JSON, Remove, and a one-line reason **instead of an Edit button** (no read-only editor is drawn); the hook is **re-found by its drawn JSON at save**, as Remove already does (`HooksTab.razor.cs:271-292`), then `ReplaceHook` re-checks under the gate |
| B2 | An edited hook badges as **new** (multiset compare, no identity); no per-hook revert | an operator reads an edited applied hook as an addition; Preview's diff is the truth | documented under the badge's tooltip-free behaviour; the snackbar says "saved", not "added"; whole-copy Discard only |
| B3 | Mutate: per-field literal/CEL toggle, several fields; literal type-fit client-side from the field type, "checked on apply" for the rest; CEL value through `cel/check` | a client rule that disagrees with apply | the fit calls the **same** `System.Text.Json` `TryGet*` methods `BeforeHookCompiler.Convert` calls (`src/MMLib.Alvo/Rules/Internal/BeforeHookCompiler.cs:435-445`), and a Host.Tests agreement fact pins it |
| B4 | Webhook `payload` box; needs `{{…}}`, must render JSON; raw JSONata gets the build's refusal; `email.data` stays refused | — | **Deviation (mechanism):** the payload (and email `to`) are judged by the **live `cel/check`**, not a client copy of `JsonataSlot.IsTemplate`: `ExpressionSlotCheck.Splice` already splices a bare string at any pointer (`src/MMLib.Alvo/Management/Internal/ExpressionSlotCheck.cs:235-252`) and the after-hook compiler reports `JSONata`, JSON-rendering and placeholder refusals at `…/action/payload`. One authority instead of a mirror; pinned by a new core test (plan Task 7) |
| B5 | Pickers read the **working copy** (pending declarations included) through a `DescriptorLens` helper; Integrations switches to the working copy | — | `DescriptorLens.Endpoints/Templates/IntegrationUses` (plan Task 2) |
| B6 | Declare endpoint/template from Integrations (`WorkingCopy.Webhooks.cs`/`.Templates.cs`, `AlvoEditor`); strict endpoint name; https (http loopback) mirroring `ResolveTarget`; `secretRef` is a NAME, written because the schema requires it, labelled not read; a **prominent** statement (unsigned, `hidden` fields, private destinations refused at delivery, no DLQ); template = `subject`/`body` only; placeholder help `new`, `old`, `event`, `@user.id`; line breaks refused in subject/to; unsigned sentences from `capabilities.warned` verbatim | an operator believes a delivery is signed, or reachable | §6. **Deviations:** (a) titles and triggers are **"New endpoint" / "New template"**, not "Declare …", because §3.1/§3.7 and `PatternLanguageTests.Every_editor_title_is_new_or_edit` admit only New/Edit; (b) only the unsigned/automation/#152 half exists in `capabilities.warned` (`UnhonouredSubsystems.cs:151-157`); the build publishes **no** sentence for the egress guard, the explicit `hidden` disclosure or the missing DLQ, so those three are dashboard-owned constants, each pinned to a core fact by Host.Tests, and a follow-up asks the core to publish them; (c) **Edit** of an endpoint/template (name fixed) is added — a typo in a URL must not need a whole-copy Discard; **removal is deferred** (§13) |
| B7 | Guided condition ships **last**, an optional layer over the text box with a text switch; strict recognizer + generator + one table + Host.Tests conformance | a recognizer that accepts what the generator does not write, or a table that offers what apply refuses | §7 |
| B8 | New e2e on a derived `AdminWorld` over `Descriptors.BikeWorkshop`; `PatternLanguageTests`/`FieldConventionTests` pass for every new editor | — | `BikeWorkshopWorld` **already exists** (`test/MMLib.Alvo.Admin.Tests.EndToEnd/ComputedTextScenarios.cs:8`); reused, one world per scenario class |
| B9 | Out of scope: signing (#120), projection (#152), `cel/parse`, automation evaluator, csx | — | §10 |

Decisions this design adds (each with its cost):

| # | Decision | Why | Cost if wrong |
|---|---|---|---|
| D1 | In Edit, the **point is fixed**; moving a hook is remove + add | "edit keeps the position" has no meaning across two lists; the kinds a point admits differ | one extra step for a rare move |
| D2 | In Edit, changing the **kind** replaces the action wholesale (a webhook's `payload` does not survive becoming an email); keeping the kind patches the action **in place, preserving key order** | the operator chose a different action; key order keeps Preview's diff to the lines they changed | a payload lost on a deliberate kind switch — the dirty guard and Preview show it |
| D3 | `HooksTab` performs the replace itself through the cascaded `WorkingCopy` (the Add path stays on `Entity`) | the save must know whether the guard held, to keep the editor open with an in-place refusal (§3.3 "every error in place"); an `EventCallback` returns nothing, and a public `Func<,>` parameter would grow `PublicApi` for no consumer | two paths for two verbs on one tab |
| D4 | An action slot (mutate value, payload, to) is checked on a candidate **without the condition**; a condition is checked with a stand-in action | `AfterHookCompiler.CompileHook` skips the action when the condition fails (`AfterHookCompiler.cs:104-117`), so a broken condition would read as a green payload | none — the condition has its own box and check |
| D5 | The guided form reads **canonical** CEL only; anything else is text mode | a second, lenient CEL parser in the dashboard is the drift analysis §4.5.2 forbids | an author's `new.flag` (bare boolean) opens in text mode |

## 4. Screens and flows

The pattern language rules applied on every screen below: editor = `AlvoEditor` (right sheet; full screen < 600 px),
title "New …"/"Edit …", submit "Add to the working copy"/"Save to the working copy", Cancel beside it; create trigger
in a section head's `<Actions>`, row trigger "Edit"; inputs named by `Field For`/`aria-labelledby`, never their own
`Label`/`HelperText`; a hinted input carries `aria-describedby="{For}-hint"`; Enter submits, a multi-line box submits on
Ctrl/⌘+Enter with `ChordHint.Of`; Escape on a dirty editor asks "Discard your changes?"; the edited/created item is
scrolled to and lit (`RevealOnRender`, `data-alvo-new`); errors are an `ErrorPanel`/field sentence **in place** with focus
on a failed submit, never a snackbar; success is one snackbar sentence (`StagedWords`); 375 px with no horizontal scroll,
16 px input text on a phone. No new confirm is added (so no new `FocusAfterConfirmAsync` pin is owed).

### 4.1 Hooks list (On write tab)

```
On write                                                  [New hook]
Before-hooks run inside the transaction …
▸ 3 things this build refuses on a hook — function, http.call, …
beforeUpdate  in the transaction · may refuse or change the row · no network
  ┌ {"condition": "old.status == 'collected' && …", "action": {"reject": "…"}} ┐  [Edit] [Remove]
  ┌ new  {"condition": "…", "action": {"mutate": {"completed_at": {"$cel": "now()"}}}} ┐  [Edit] [Remove]
afterUpdate   after the commit · may leave the process
  ┌ {"action": {"type": "entity.update", …}} ┐  [Remove]
    This hook's action type 'entity.update' is refused by this build; it is kept as it is. (hook-readonly)
```

* **Edit** (`hook-edit`, `aria-label="Edit hook {n} of {point}"`) only on a drawable hook (§5.2) and only while the
  copy declares the entity (`Editable`). Edit sits before Remove; both ghost, small; under the JSON on a phone.
* A staged hook — added **or edited** — carries `new` (B2).

### 4.2 The hook editor (one sheet, New and Edit)

| Element | New | Edit |
|---|---|---|
| Title | "New hook" | "Edit {point} hook {n}" |
| Subtitle | today's | "It keeps its place: the hooks at one point run in order, and this is number {n} of {m}." |
| When | point `ChipGroup` | the point as text (`hook-point-fixed`) + hint "To move it to another point, remove it and add a new one." (D1) |
| Only when | guided/text switch (§4.6) | opens guided when the condition is canonical (or empty), else text |
| Then | kind `ChipGroup` (point decides kinds; `beforeDelete` reject only — unchanged) | same; switching kind replaces the action (D2) |
| Action fields | per kind, below | prefilled from the hook |
| Submit | "Add to the working copy" (`hook-add`) | "Save to the working copy" (`hook-save`) |
| Dirty | anything typed (point/kind excluded, as today) | anything different from what was opened, kind included |
| After submit | snackbar "Hook {point} added to the working copy", new row lit | snackbar "Hook {point} saved to the working copy", **the same row** lit |
| Refused by the guard | — | `ErrorPanel` "That hook could not be saved": "This hook changed in the working copy after you opened it — in another tab, by the assistant or by an import. Nothing was saved. Close this editor and open the hook again." Editor stays open, focus on the panel |

Action fields per kind:

| Kind | Fields |
|---|---|
| reject | "Refuse with" (single line, today's) |
| mutate | rows (§4.3), "Add a field" |
| webhook | "Endpoint" picker (§4.4); "Payload (optional)" multi-line (§4.5) |
| email | "Template" picker (§4.4); "To" (single line, live-checked as a slot) |

### 4.3 Mutate rows

```
Field 1  [completed_at ▾]   Write  (•) a value  ( ) an expression
Set field 1 to  [2026-10-05T12:00:00Z      ]   □ Set to empty          [Remove]
                 It must be a date and time such as 2026-10-05T12:00:00Z.  ← fit sentence
[Add a field]
```

* **Field** = `MudSelect` over the entity's **writable** fields in the working copy (`PendingSchema.Read` minus
  `AlvoManagedColumns.For`, minus `computed` and `rollup` fields), plus — in Edit — the hook's current field when it is
  not among them, labelled "(not offered)". A field chosen twice is refused at submit ("'x' is patched twice").
* **Write a value | an expression** `ChipGroup`. Expression → CEL box, live `cel/check` under it (key
  `hook-mutate-value-{i}`). Value → the input the field type takes: text box (string/text, empty string allowed;
  number/decimal with `inputmode`; date/datetime; uuid/ref), a `MudSelect` of declared values (enum), `true`/`false`
  chips (boolean); **json takes no literal** ("A json field takes no literal value here — this build converts none. Write
  an expression, or set it to empty."). "Set to empty" (`MudCheckBox`) only on a field that is not `required`.
* The fit sentence is computed locally on each render (`MutateLiteral.TryValue`) and is focus-free; at submit the same
  refusal goes to the editor's `ErrorPanel` with focus. **Stricter than apply** in one place: an enum literal must be a
  declared value — apply converts it as a plain string (`BeforeHookCompiler.cs:437`; reported as a finding, §11).
* `beforeDelete` offers no mutate (unchanged). One row minimum; Remove on a row only when there are two or more.

### 4.4 Endpoint and template pickers

* `MudSelect` over `DescriptorLens.Endpoints(Copy.Json)` / `Templates(Copy.Json)` names — the working copy, so a
  declaration staged a minute ago is offered (B5); a pending one reads "rental-desk (not applied yet)".
* A template whose declaration uses `bodyFile` is not offered; the hint says how many were left out and why (the build
  refuses an email whose template reads `bodyFile`, `AfterHookCompiler.cs:347-357`).
* The hook's current value is always shown, even when nothing declares it — "{name} (not declared)" — never silently
  replaced. Submitting it is allowed (apply refuses it with "did you mean"); the hint says so.
* None declared: the picker is disabled and the hint says "No endpoint is declared yet. Declare one on the Integrations
  screen first — this sheet does not keep what you typed if you leave it." — **no link** inside the sheet: the editor
  has no navigation guard, and a link would lose the draft silently.

### 4.5 Payload box (webhook)

Multi-line (`Lines="4"`), monospace, optional. Hint (three sentences + the chord): *"Left empty, the endpoint receives
the CloudEvents envelope: the whole row, `hidden` fields included. A payload is JSON around `{{…}}` placeholders over
`new`, `old`, `event` and `@user.id` — a placeholder in quotes for text (`"{{new.order_number}}"`), bare for a value
(`[{{new.total}}]`). This build reads anything with a `{` or `}` outside a placeholder as JSONata and refuses it, so an
object payload is refused today — write an array."* + `ChordHint.Of(…)`. Findings come from the live check at
`…/action/payload` (B4 deviation) once a **declared** endpoint is chosen (an undeclared endpoint stops the compiler
before the payload, `AfterHookCompiler.cs:234-239`; the box then says "Checked once a declared endpoint is chosen.").
Over 8,000 characters (`$defs/jsonata`) a local sentence says so without asking the server (the check's own cap is
8,000).

### 4.6 Guided condition with a text switch

```
Only when (optional)     ( ) Guided  (•) Text
Match  (•) all  ( ) any  of these
 Condition 1: [status ▾] [now ▾] [is ▾] [ready ▾]                     [Remove]
 Condition 2: [The person writing ▾] [has the role ▾] [manager ▾]     [Remove]
 [Add a condition]
 Expression: new.status == 'ready' && 'manager' in @user.roles
 ← cel/check sentences for "hook-condition"
```

* Mode switch `ChipGroup` "Guided | Text" (`hook-condition-mode`). Guided → Text always succeeds: the box receives the
  generated text. Text → Guided runs the strict recognizer (§7.3); when it fails the switch stays on Text and a sentence
  says "This condition cannot be shown as rows; it stays as text." Empty text ⇄ zero rows ("runs on every write").
* A row: field (or "The person writing"), image chips **now / before** only where the point has both images, operator
  (only those the table allows for that field, point and nullability), value (text box, enum select, number box, role
  select). Every control is named by its row ("Condition 1 field", "Condition 1 operator", …) so the names never
  collide with the mutate rows' "Field 1"; a row is `role="group" aria-label="Condition {n}"`.
* The **Expression** readout is the CEL that will be written, and the live `cel/check` (key `hook-condition`) stays the
  authority (§7.5). Over 2,000 characters a local sentence says so (`$defs/cel` 1–2000).

### 4.7 Integrations, reading the working copy

```
Integrations
The webhook endpoints and message templates this descriptor declares …
┌ Webhook endpoints ─────────────────────────────────────── [New endpoint] ┐
│ (warned) the build's `webhooks` sentence, verbatim, with "not yet"         │
│ rental-desk   posted to by rentals afterCreate   not signed   [Edit]       │
│ http://127.0.0.1:5081/hooks/rentals                                         │
│ secretRef: rental-desk-signing-key — declared, and not read.               │
└────────────────────────────────────────────────────────────────────────────┘
┌ Message templates ─────────────────────────────────────── [New template] ┐
│ (warned) the build's `templates` sentence, verbatim                         │
│ order-ready   sent by service_orders afterUpdate   [Edit]                   │
│ Your bike is ready — order {{new.order_number}}                             │
└────────────────────────────────────────────────────────────────────────────┘
┌ What this build refuses in these blocks ┐  (integrations-refused-{slot}, unchanged ids)
```

* Reads `Session.WorkingCopyAsync` and follows it (`Session.Follow`), like the entity screen; a staged row carries
  "not applied yet". Usage badges ("posted to by …" / "no hook posts here") come from `IntegrationUses` — **hooks only**:
  an automation reference delivers nothing in this build, and the warned sentence says so.
* A declaration the editors cannot draw (an endpoint with unknown keys, a template with `bodyFile`) has no Edit; the
  `bodyFile` row shows the build's `bodyFile` refusal. The panel title changes from "Why there is no "new endpoint"
  button" (now false) to "What this build refuses in these blocks"; its test ids stay, so
  `RefusalPlacementScenarios.Integrations_says_why_a_body_file_and_a_jsonata_payload_are_refused` stays as it is.
* Create triggers live in each `Panel`'s `<Actions>` (§3.7), lit and scrolled to after a save.

### 4.8 New/Edit endpoint (`EndpointEditor`) and New/Edit template (`TemplateEditor`)

Endpoint sheet, top to bottom: the **statement** (an `AlvoAlert` in the Warning tone, *above* the fields — not fine
print, §6.3); Name (`^[a-z][a-z0-9-]{0,62}$`, unique; fixed in Edit); URL; Secret name (prefilled
`{name}-signing-key` until touched); Description (optional). Submit "Add to the working copy"/"Save to the working
copy"; field refusals under their input (`FieldRefusals`, focus on the first). Snackbar "Endpoint {name} added to the
working copy" — **the name, never the URL** (a URL can be its own bearer secret, `events.md` "the failure path").

Template sheet: the build's `templates` sentence (Info tone); Name (`$defs/identifier`, unique; fixed in Edit); Subject
(optional, single line); Body (multi-line, required, `ChordHint`). Hint: placeholders over `new`, `old`, `event` and
`@user.id`; "checked against an entity's fields when an email hook on that entity sends this template — on apply". No
`bodyFile` control (refused), no `email.data` control (refused; it stays on the refusal panels as today).

## 5. Data model

### 5.1 `ReplaceHook` semantics

`bool WorkingCopy.ReplaceHook(string entity, string point, int position, string expectedHook, JsonObject hook)` — under
the gate (`WorkingCopy.Edit`, `WorkingCopy.cs:370`): writes a clone of `hook` at `position` **only if** the entry there,
read as the screen draws it (indented, relaxed encoder — `Readable`, the same options `HooksTab._readable` uses), equals
`expectedHook`. Returns whether it wrote. One entry rather than `RemoveHook`'s whole list because an unrelated append to
the same point must not refuse an edit; two identical entries are indistinguishable, so replacing either yields the same
document. The tab finds the position first by the drawn JSON (`IndexOf`), so a hook moved by another tab's removal is
still the one edited ("acts on what it named").

### 5.2 What the form can draw (`HookShape.Undrawable`)

Drawable: an object with only `condition` (string) and `action`; before-points: `{reject: string}` or `{mutate:
{field: scalar | null | {"$cel": string}}}` (not under `beforeDelete`); after-points: `{type: "webhook", endpoint:
string, payload?: string}` or `{type: "email", template: string, to: string}`. Anything else is read-only with one
sentence: unknown keys; non-string condition; a refused type (`function`, `http.call`, `entity.update`); email `data`;
a mutate literal that is an object or array; mutate at `beforeDelete`; an unknown `type`. Every read-only shape is one
apply refuses or the schema rejects — it reaches the copy only through import or the assistant, and is kept byte-for-byte.

### 5.3 The patch (`HookPatch.Apply`)

New hook: `{condition?, action}`, condition first (as `AddHook` writes). Edit: a clone of the original; the condition is
replaced in place, removed when blank, or inserted first when absent; the action is merged **in place** when the kind is
unchanged (keys the form owns set or removed, mutate keys kept in their order with new ones appended) and replaced when
the kind changed (D2). Generated shapes are what `FieldReferences.cs`/`CelNames.cs` and rename already scan.

### 5.4 Staged badge and ordering

Edit keeps the index; nothing else moves. B2's badge follows from `StagedChanges.NewEntries`. Two operators each hold
their own copy (`WorkingCopyStore` keys by user); the later apply is refused by `If-Match` — existing behaviour
(`OtherCircuitApplyScenarios`), unchanged.

### 5.5 Endpoint and template writes

`WorkingCopy.DeclareEndpoint(name, url, secretRef, description, editing)` writes
`webhooks.endpoints.{name} = {url, secretRef, description?}` (schema order), creating `webhooks`/`endpoints` as needed;
an edit patches the existing object in place. `DeclareTemplate(name, subject, body, editing)` writes
`templates.{name} = {subject?, body}`. Both refuse (return `false`) a new name that exists or an edited name that does not
— re-checked under the gate, since another tab may have declared it meanwhile.

## 6. Endpoint and template declaration rules

### 6.1 Endpoint

| Field | Rule | Mirrors | Note |
|---|---|---|---|
| Name | `^[a-z][a-z0-9-]{0,62}$`, unique | schema `webhooks.endpoints.propertyNames` | stricter than a hook's `endpoint` reference (`$defs/identifier` admits `_`); pickers offer only declared keys, so no `_` name can be picked that no key matches |
| URL | `Uri.TryCreate(…, Absolute)`, and `https`, or `http` with `Uri.IsLoopback` | `AfterHookCompiler.ResolveTarget/IsDeliverable` (`:279-293`) — the same BCL calls | apply checks the URL **only once a hook references the endpoint**; the editor always checks (stricter, stated in the hint) |
| Secret name | `SecretName.TryParse` (`^[a-z][a-z0-9._-]{0,63}$`, `MMLib.Alvo.Secrets`, public) | the store's own name rule | the schema requires `secretRef`; the build never reads it. A pasted secret value almost never matches the rule — an accidental guard, not a promise |
| Description | free, optional | — | unread by the build (§8a "ignored (unverified)") |

### 6.2 Template

Name `^[a-z][a-z0-9_-]{0,62}$` unique; body required; subject single line (no control character but tab, no line or
paragraph separator — `MailHeaders.BreaksALine`); a placeholder root must be one of `new`, `old`, `event`, `@user`, and
`{{@tenant.…}}` / `{{@user.roles}}` are refused with the reason (the envelope carries neither, `TemplatePlaceholder`).
Field names inside placeholders are judged on apply, per referencing entity.

### 6.3 The statement (endpoint sheet; required wording)

Warning-tone `AlvoAlert`, title **"Deliveries to this endpoint are not signed, and carry the whole row"**, body in this
order:

1. The build's `webhooks` sentence from `capabilities.warned`, **verbatim** (it says: automation-only endpoints receive
   nothing; no delivery is signed, `secretRef` is not read, no Standard Webhooks HMAC header, the receiver cannot verify
   the sender; no per-endpoint projection, #152).
2. Dashboard-owned (not published by the build — B6 deviation (b)): "Each delivery carries the record's complete image —
   fields declared `hidden` included."
3. Dashboard-owned: "A destination on a private, loopback, link-local or otherwise non-public network is accepted here
   and on apply, and refused on every delivery: the check runs when the connection is made, on the address the name
   resolves to. A loopback address passes only when the URL names it (localhost, 127.0.0.1, [::1]). Only the host's
   configuration, `Alvo:Events:WebhookAllowedNetworks`, admits another network — this dashboard cannot."
4. Dashboard-owned: "A delivery that keeps failing is retried up to the attempt ceiling and then abandoned; there is no
   dead-letter queue and no redelivery screen yet."

The Secret name field's hint: "The name the signing secret will be stored under, never the secret itself. This build
does not read it." Never a field for a secret value; nothing claims a destination is reachable.

## 7. The guided condition

### 7.1 One table (`ConditionTable`) — the single source

Kinds: string/text → Text; enum → Choice; integer/decimal → Number; boolean → Flag; date/datetime → Moment; uuid/ref →
Identity; json → Json. Images: `new.` at beforeCreate, beforeUpdate, afterCreate, afterUpdate; `old.` at beforeUpdate,
beforeDelete, afterUpdate, afterDelete.

| Operator (words) | CEL (canonical) | Kinds | Legal at | When the field is empty (null rule) |
|---|---|---|---|---|
| is | `new.f == 'v'` / `== 12` | Text, Choice, Number | image points | false |
| is not (and has a value) | `new.f != 'v'` | Text, Choice, Number | image points | **false** — every comparison with an empty value is false, `!=` included |
| is not, or is empty | `!(new.f == 'v')` | same, **nullable only** | image points | **true** |
| is less than / at most / more than / at least | `<` `<=` `>` `>=` | Number | image points | false |
| is true / is false | `new.f == true` / `new.f == false` | Flag | image points | false (empty is neither) |
| has a value / is empty | `has(new.f)` / `!has(new.f)` | every kind, **nullable only** | image points | defines it |
| changed | `changed(f)` | all but Json | beforeUpdate, afterUpdate | unverified for empty→value (the interpreter's `changed` arm decides; not asserted) |
| is the person writing | `new.f == @user.id` | Identity | image points | false; an after-hook needing an actor the event lacks is not selected (`AfterHookCompiler.ActorRead`) |
| The person writing has the role / does not | `'r' in @user.roles` / `!('r' in @user.roles)` | — | **before-points only** (`HonoursTheEnvelope` refuses `@user.roles` after) | false / true |

Not offered (text mode only): string relational (refused outside Computed, `CelTypeChecker.cs:639-645`); negative
numbers (unary `-` is Computed-only, `CelTypeChecker.cs:389-398`); `== null` (refused, `:629-636`); timestamp or uuid
literals (no literal syntax; `Cannot compare`); field-to-field comparisons; `@tenant.id`; function calls (the functions
slice may add them — §10); a mix of `&&` and `||`. Text literals use only the escapes `\\ \' \n \r \t`
(`CelLexer.cs:198-218`); a value with another control character is refused in the form. Numbers are
`(0|[1-9][0-9]*)(\.[0-9]+)?`, written as typed.

**After-hook images are narrower than apply** (unverified that apply refuses `old.` at afterCreate — no phase check was
found in `AfterHookCompiler`): the form does not offer them; the conformance fact records that apply accepts them.

### 7.2 Generator

`ConditionText.Generate(GuidedCondition)` substitutes each row into its table format and joins rows with ` && ` (all)
or ` || ` (any). Literal values are spelled by kind (quoted and escaped for Text/Choice, as typed for Number); a role is
quoted.

### 7.3 Strict recognizer and round trip

`ConditionText.Recognize(text, point, scope)`: split at top level (outside quotes) on ` && ` or ` || ` (both → null);
match each part against the table's formats (anchored regexes derived from the same format strings); resolve the field
against the working-copy schema; refuse a kind/operator/image/nullability/point the table does not allow, an enum value
not declared, a role not declared, a non-canonical escape; **then require `Generate(result) == text`**, else null.
Property: for every condition the generator can produce, `Recognize(Generate(c)) == c` and `Generate(Recognize(t)) ==
t` (CsCheck, 2,000 cases, hostile values: quotes, backslashes, newlines, tabs, ` && ` inside a literal, unicode).

### 7.4 Conformance (Host.Tests, sees Admin and core internals)

For every (point × field kind × nullability × operator × image) the table allows, the generated condition, placed in a
hook of a probe descriptor, gets **no error** from the real `DescriptorValidator` at that hook. And the table's refusals
are real where apply has them: `old.` at beforeCreate, `new.` at beforeDelete, `changed` at a create or delete
before-point, and a role row at any after-point are each refused by apply.

### 7.5 `cel/check` stays the authority

The guided form only decides what to *offer*; the Expression readout is checked by `cel/check` exactly as the text box
is. A table cell that apply refuses would show its refusal under the readout — and fail the conformance fact first.

## 8. Security considerations (what the UI must never do)

* Store, show, or offer a field for a **secret value**; `secretRef` is a name, validated as one.
* Offer `bodyFile`, raw JSONata, `email.data`, or the `function` / `http.call` / `entity.update` action types — they
  stay on the refusal panels (`RefusalPlaces`), and `RefusalPlacementScenarios` stays exhaustive.
* Claim a destination is reachable, or that a delivery is signed or projected (§6.3).
* Put an endpoint URL in a snackbar, a log line or an error sentence (names only).
* Evaluate any expression in the dashboard: the guided form generates text; the core judges it (`cel/check`, apply).
* Write a hook shape it cannot draw, or drop one (§5.2, `A_hook_the_editor_cannot_draw_survives_beside_it` extended to
  edits).
* Render descriptor text as markup: names, URLs, subjects and conditions go through Razor's encoding or `CodeBlock`.
* Mutate values run inside the write's transaction (SC-adjacent, triage): nothing new is evaluated in Admin; apply's
  `BeforeHookCompiler` stays the gate, and the security-core checklist runs on the PR.

## 9. Accessibility and keyboard

Every new input is named by a visible label (`Field For` or `LabelId` + `aria-labelledby`); repeated rows carry their
number in the name ("Field 2", "Condition 2 operator"). The fit sentence, check sentences and refusals are listed in the input's
`aria-describedby` after its hint. Chip groups are radio groups (`ChipGroup`). Enter submits every sheet; the payload
and template body submit on Ctrl/⌘+Enter. Escape closes the sheet, asking first when dirty; Keep editing returns focus
to the first control. Focus on open: first field (Edit: the first editable field after the fixed point). After a save
focus returns to the trigger (the row's Edit, which still exists — the row keeps its id).

## 10. Forecloses / keeps open

| Item | This slice | Kept open by |
|---|---|---|
| Signing (#120 / 7.1) | the sheet writes `secretRef` as a store name | `SecretName` validation means a signing build can resolve it; the statement's sentence 1 comes from the build, so it changes when #120 lands without a dashboard edit |
| Projection (#152) | sentence 2 states the disclosure | a per-endpoint allow-list is a new key in the endpoint object; until the editor learns it, an endpoint carrying it is read-only (no Edit), so the key is never dropped |
| Functions (slice C, parallel branch) | the table has no call rows; a call makes a condition text-only | a `cel/functions` list can feed the mutate expression box's help and, once Condition admits calls, a table row — additive |
| `automation` | usage badges count hooks only | the warned sentence explains; an automation builder is D5 of the analysis (not before an evaluator) |
| Per-hook identity | none (B2) | a future `id` on a hook would let the badge say "changed"; nothing here assumes positions are identities beyond one save |
| `cel/check` scope | used for payload and `to` string slots (B4 deviation) | documented in `management-api.md`; pinned by a core test |

## 11. Risks and findings

* **Finding (core): an enum mutate is not checked for membership at apply** — `BeforeHookCompiler.Convert` maps enum to
  `String` (`:435-437`) and `Fits` compares types only, so `mutate: {status: "bogus"}` (literal or `{"$cel":
  "'bogus'"}`) applies; what the write does then is unverified. The form only offers declared values. Needs a core issue.
* **Finding (core): object payloads are refused** as raw JSONata by the classifier's no-bare-brace clause
  (`JsonataSlot.cs:62`, `JsonataSlotTests.cs:16,42`); the hint tells the truth and an array works. Relaxing it is a
  core classification change the classifier's own remarks anticipate.
* MudSelect inside `AlvoEditor` (a `MudDialog`) is new on this dashboard (the only select today is on a page,
  `Rules.razor:34`); the first task that adds one writes its e2e first.
* `HooksTab` grows; the guided rows are their own component to keep it reviewable (PublicApi grows, §14).
* Dashboard-owned statement sentences can drift from the build; pinned by claims facts, and a follow-up moves them to
  `capabilities`.

## 12. Acceptance criteria (numeric where a source gives one)

1. Every flow in §4 has an e2e scenario (§13 list); every new editor passes `PatternLanguageTests` and
   `FieldConventionTests` unchanged; `RefusalPlacementScenarios` passes unchanged (0 unplaced refusals).
2. 375 px: `AssertNoHorizontalScrollAsync` passes with the hook editor (all four kinds, guided mode) and both
   Integrations sheets open (analysis §9.5, baas-analyza §2.8).
3. A check sentence appears ≤ 3 s after typing (300 ms debounce, slice A §5.4) under a mutate expression, the payload,
   `to` and the guided readout, and never steals focus.
4. Round trip: 2,000 generated conditions, 0 failures. Conformance: every allowed combination (count printed by the
   fact) accepted by the real validator, and 4 refusal classes confirmed refused.
5. An edit keeps the hook's index (asserted on a point with ≥ 2 hooks); a guard refusal writes nothing.
6. `docs/todo-admin.md`: §5a `templates` and `webhooks.endpoints` read → edit; §8a the 6-point hooks row (edit in place),
   mutate literal, multi-field, `webhook.payload`, `webhook.endpoint`, `email.template` (pickers), `templates.subject`/
   `.body`, `webhooks.endpoints.<n>.url`/`.secretRef` → edit; §8b "Edit a hook in place" → edit; item 26 → Done (plan
   Task 20 lists each row).

## 13. Test strategy

| Layer | What |
|---|---|
| Admin.Tests (unit) | `ConditionTableTests`, `DescriptorLensIntegrationTests`, `HookShapeTests`, `WorkingCopyHookReplaceTests`, `MutateLiteralTests`, `HookPatchTests`, `HookBuilderEditTests` (+ updated `HookBuilderTests`), `ExpressionSlotsHookTests`, `EndpointDraftTests`, `TemplateDraftTests`, `WorkingCopyIntegrationTests`, `IntegrationRowsTests`, `ConditionTextTests`, `ConditionTextRecognitionTests` |
| Admin.Tests (property) | `ConditionTextRoundTripTests` (CsCheck) |
| Core tests | `ExpressionSlotCheckTests`: a payload and a `to` slot are spliced as strings and judged |
| Host.Tests | `HooksEditorAgreementTests` (URL rule ↔ `ResolveTarget`; literal fit ↔ apply; template roots ↔ `TemplatePlaceholder.Roots`; statement claims ↔ core), `GuidedConditionConformanceTests` |
| e2e (`BikeWorkshopWorld`) | `HookEditInPlaceScenarios`: edit keeps position + lit + snackbar + badge; Edit title/submit/fixed point; dirty Escape; another tab removed it (guard); another tab moved it (re-found); imported undrawable hook read-only. `MutateEditingScenarios`: literal + expression + two fields; fit sentence; enum select; ExpressionCheck stays focus-free. `HookPickerScenarios`: endpoint/template pickers list pending declarations; undeclared current value kept; payload refusal from the build; phone width. `IntegrationsScenarios`: working copy listed; New endpoint (statement present, verbatim sentence, refusals for `http://example.com`, bad name, secret value); loopback accepted; Edit endpoint; Integrations → picker round trip; double click; Escape; phone width. `TemplateDeclarationScenarios`: refusals (line break, `@tenant`); a new template offered to an email hook before apply; Edit template; phone width. `GuidedConditionScenarios`: build rows → readout → add; text → guided recognition; non-canonical stays text; role row absent at an after-point; phone width. Updated: `ExpressionCheckScenarios` mutate scenario (row ids), `HookEditingScenarios` unchanged |

## 14. Public API

`PublicApi.MMLib.Alvo.Admin.verified.txt` grows by three components — `ConditionBuilder` (`Point`, `Entity`, `Value`,
`ValueChanged`), `EndpointEditor` and `TemplateEditor` (`Editing`, `Warning`, `OnSaved`, `OnClose`) — and `Integrations`
gains `IDisposable` (it now follows the working copy) — each justified per
`alvo-architecture-rules`: a Razor component cannot be internal and remain a tag; every parameter is a string or an
`EventCallback` of one (D5 of the MudBlazor design: no Mud and no internal type in a public parameter); the working copy
reaches them by cascade. No Abstractions or Management change.

## 15. Open questions (for the maintainer)

1. Enum membership for mutate values at apply (§11) — file the core bug now, or in slice D?
2. Object payloads (§11): accept "arrays only" in this slice, or open the classification change?
3. The three dashboard-owned statement sentences (§6.3): keep them pinned in Admin, or publish them from the core
   (`capabilities.warned` qualified slots such as `webhooks.egress`) before this ships?
4. Removing an endpoint/template from the dashboard (refused while a hook references it) — next slice?
5. Should Edit be able to move a hook to another point (D1)?

## 16. Follow-ups

Remove endpoint/template; publish the egress/DLQ/hidden sentences from the core; enum membership at apply; object
payload classification; a `Position` caret for the check (slice A deferral); field-to-field rows in the guided form;
functions in the guided table once Condition admits calls (slice C).

## 17. As built

(To be written when the slice lands: commits, deviations from the plan, measured numbers.)

Deviations recorded as they were made:

- **Task 8, a condition edit asks the condition box only** (pre-flight C4). The merged slice-A code re-asked every box on
  a condition edit (`CheckBoth()`), so no answer was shown against an older form. Under D4 no action slot's candidate
  carries the condition, so that reason no longer holds; and the guided condition (§7) calls the same handler on every row
  change, where re-asking every box would cost 3 + N checks per click. An e2e assertion pins that the mutate value's flag
  stays as it was across a condition edit.
- **Task 8, the row text and the §5.1 guard come from one writer.** The tab draws each hook with `WorkingCopy.Readable`
  rather than options of its own, because two separately written option sets that drifted would refuse every in-place
  edit, silently.
