# Review 2 — usability, interaction and comprehension

Produced by an adversarial reviewer (Fable 5) who read the source and clicked through every
route at 1400px, in a 390px iframe, in both themes. 2026-09-21, against prototype v9/v10.
Verbatim. Two of its hardest claims were independently re-verified before this file was written:

- `evaluate()` never honoured `anon` — the check was `[caller.role, 'authenticated']` *(fixed in v10)*
- there are **zero** `.focus()` calls in `app.js`, so the command palette is keyboard-dead

Line references are into `app.js` as it stood at v9/v10 and have shifted since; find by content.

---

## Must fix

**1. Three separate drafts funnel into one preview that shows only one of them.**
Schema edits (`state.pending`), rule edits (`ruleChanges`, per entity) and the role catalogue (`catalogChanged`) each have their own pending bar, and all three link to `#/schema/preview`, which renders only the two schema changes. I changed the Read-many rule; the Rules bar said "1 rule changed"; Preview said "Two changes" and listed neither the rule nor any rule diff; the Overview still said "Two schema changes are waiting". A user cannot tell whether Apply will include their rule change or drop it. This is the single biggest mental-model breach — the product's thesis is *one descriptor*, and the UI presents three unrelated queues.
Fix: one working copy, one count shown in the shell (e.g. next to the project switcher: "3 unapplied"), one preview grouped by kind (Schema / Rules / Roles), each with its diff, one identical pending bar everywhere.

**2. The descriptor pane says "what apply will receive" and shows the applied revision.**
`descriptorJson()` renders from `ENTITIES` (revision 7): `precision: 10`, no `completed_by`, while the bar beneath says "2 changes not applied" and the preview diff shows exactly those two edits. Nothing in the field list marks which fields are edited either. The design's central claim ("you watch the JSON build") is falsified within a minute.
Fix: the pane renders the working copy with +/– gutter marks on changed lines; field rows carry a changed dot; pane header reads "working copy · 2 changes vs r7".

**3. The field drawer opens exactly on top of the descriptor pane it claims to annotate.**
`.a-drawer--wide` is 560px; the aside is 520px. "Selecting a field marks the lines it owns in the descriptor" — you can never see that while editing, only after closing. The two-pane concept is defeated at the moment it matters.
Fix: edit in place in the right column (field form on top, that field's JSON fragment live beneath it — the drawer's `<details>` already has that fragment), whole-descriptor pane on demand. If you keep a drawer, make it narrower than the aside and left-aligned to it.

**4. Rules matrix: the `technician` row is blank while the simulator says technician is *allowed*.**
The grant reaches technicians through "The person assigned_to names", but nothing connects that row to the technician row. A newcomer reads "technicians can do nothing" from the matrix and "allowed" from the simulator for the same caller. This is the exact question the screen exists to answer.
Fix: in role rows, render a faint "own" glyph in cells where an owner branch exists, with the sub-text "via assigned_to". *(Done in v10 — verify it reads correctly and that the glyph is not mistaken for a grant.)*

**5. "Only when" reads as another row of the matrix but is a column-wide AND — and the matrix's "+ add" opens an editor off-screen.**
(a) Structure: the When row sits under the role rows as if it were an 8th "who". It is not; it is ANDed across the whole column, admins included. Clicking "+ add" inserts `status == 'scheduled'` by default, which silently locks admins and dispatchers out of every non-scheduled record.
(b) Expressiveness: `(who || who) && when` cannot say "technicians only while not completed, dispatchers always" — the most common real rule — and the builder never says so.
(c) Mechanics: `ruleopen` from the matrix only re-renders; the editor appears ~700px lower. I clicked "+ add"; nothing visible changed. It reads as broken.
*(a) and (b) addressed in v10 by moving conditions to the branch; (c) addressed by scrollIntoView. Verify all three.*
Still open from this finding: say what "the record" means per operation — for Create there is no stored record; hooks use `new.status`/`old.status` two tabs away while rules use bare `status`. Two vocabularies for one thing, unexplained.

**6. Simulator verdicts are wrong in three ways.**
- "Read many — refused" for a technician on someone else's job. For a list, the record is *filtered out*, not refused; the assistant transcript on the same screen says exactly that. The simulator contradicts the product's own most important teaching point. *(Partly addressed in v10: the badge now reads "filtered out" for list. Verify the wording and the explanation.)*
- `evaluate()` never honours `anon`. Tick anon on Read one → a technician on Martin's record still shows "refused". *(Fixed in v10.)*
- It evaluates the *unapplied draft* while saying "Runs against the same policy engine the API runs". Say "against your unapplied draft — callers still get revision 7". *(Added in v10 only when a draft exists. Verify.)*
Also the default (Peter looking at his own job) makes every read "allowed", the dull case; default to a record not his.

**7. New record form: a permanent red error, an impossible control, and two open pickers.**
- `recordForm()` always renders "reference is already taken" — at the *bottom*, 1000px from the field, on an untouched form. A first-run user sees an error before typing.
- `uuid` renders as "Choose a technician…". The descriptor cannot produce that — a uuid has no target. This is the Invite defect the notes are proud of catching, committed again. Honest control is a text input, or propose `ref → users` as a type.
- Both ref pickers open expanded (~200px each) simultaneously; three refs and the form is a wall. Collapse until focus.
- "Searches the display field" names a concept the schema does not have (no `displayField` in `schema/project.schema.json`). Either pick `name` heuristically and say so, or add it to the descriptor.

**8. The command palette is keyboard-dead.**
Verified: ⌘K opens it, `document.activeElement` is `BODY`, typed text goes nowhere (`autofocus` does not fire on innerHTML insertion), Down/Return do nothing, the first item is hard-coded active. No `role="dialog"`/`aria-modal` anywhere, no `.focus()` call in the file, no focus return on close; drawers are appended at the end of the DOM so Tab walks the whole page first. Escape does work, and alvo.css's global `:focus-visible` ring is present — those are fine.

**9. Copy that is false on its face.**
- Raw CEL mode: "available: … reference title description status priority — and nothing else" lists 5 of 18 fields (`slice(0, 5)`).
- Field drawer: the amber "Changing the type drops values … the preview will ask you to type the entity name" shows *permanently* on every field with facets, before any type change is attempted. Show it on hover/focus of a different chip or after a change.
- Data footer: the filter chip "status is in_progress" is active over rows of every status.

## Worth doing

**10. Five representations of one rule on one screen.** Matrix row, sentence, CEL line, editor chips, editor CEL readout — and in raw mode the readout and the textarea show the identical string. Make the matrix the overview and the sentence list the editor's expanded state; show the CEL once.

**11. The Access ladder silently reflects the unapplied rule draft.** Compute from applied rules, or badge draft-derived facts.

**12. Assign modal offers `authenticated`** as a grantable role, and grants `admin` in one click with no confirmation. The inert-role warning says "Declare it below" — offer a one-click "Declare billing-manager" that opens New role prefilled.

**13. "Who may use this dashboard" has no editor** although its band says "reviewed before it applies". *(Level names fixed in v11; the editor is still missing.)*

**14. Dead controls drawn as live.** Hooks Edit / Add a hook, Indexes Add / Remove, API Open reference / Download OpenAPI, the ⋮⋮ reorder handle, Filter fields input — none has a handler or a disabled state. Grey them with "not yet" or remove. The API tab's rule column truncates CEL at 42 chars — identical for all six rows, so it says nothing; show the sentence or role badges. Two places make an index (field toggle vs Indexes tab) with no statement that `index: true` and `indexes: [{fields:[x]}]` are equivalent — and the drawer's own JSON disclosure never emits `index`.

**15. Sample data blurs tenant and customer.** Tenant chips and the Customer column both read "Nordreg Facilities" / "Bytehouse s.r.o." — the one confusion the product most needs to avoid. Rename the tenants.

**16. Authorial copy leaking into the product surface.** `p-note__tag` values "design", "honest", "three consequences", "read only"; "That is an acceptance criterion, not a slogan"; "It is checked inside the same transaction as the query, against that closed context and nothing else" ("closed context" means nothing to a newcomer); "No network — that is the guarantee, not a limitation"; wizard: "A wizard step for it would be a second, worse copy of that screen." Cut or move to notes. Also "whoever assigned_to names" is clever, not clear — "the user in assigned_to".

**17. Missing states.** Schema with zero entities (first run lands on `#/schema`; no empty state drawn). Entity with no rules at all (blank matrix, no callout). Entity bar with 40 entities (no overflow strategy). 200 fields (no filter behaviour, no grouping). Apply failure / dry-run refusal (the REFUSED texts exist but no apply-error state is drawn). Stale apply (second admin applied r8 meanwhile). A `developer`-level user on Preview (Apply must be disabled with a reason; undrawn). Loading exists only for Data. Long CEL / long enum lists.

**18. Phone.** Matrix shows one column with four off-screen and no scroll cue; simulator is below everything. Not broken, not usable. Under 720px transpose: operations as rows, role chips inside each. Drawer footers wrap ("Cancel" above the primary, "Close" under the title).

**19. Record drawer** shows a curated subset (no title/description/priority/is_emergency/contact_email/metadata), "version 4" unexplained, Edit opens the *new* form. Spec the full-field detail; explain version as the ETag.

## Taste
- Welcome wordmark SVG overlaps its tagline at 168×40.
- History compares only against r7; r3↔r4 impossible.
- Integrations repeats the identical unsigned warning under each endpoint; say it once.
- Field type column (`string`, `text`) is low-contrast at 13px.
- Drawer opens with the previous field still highlighted in the JSON when switching tabs.

## Task timings
- Add a field: Schema → entity → Add field → form → Add to the model → Preview → Apply = 6 clicks. Fine, except #3.
- Make technicians able to create: Rules → tick cell → Preview → Apply = 3 clicks, but Preview shows nothing about it (#1). Broken.
- Why can't Peter see X: Rules → technician → record = 3 clicks, works, wrong word (#6). Access → "What they can do" answers per-entity, not per-record; the ladder should link into the simulator with the person preselected.
- Add a record: 2 clicks + 14 fields; hurt by #7.
- Understand a project: Overview → Schema map → entity → Rules tab. Good; the map is the best onboarding artifact here.

## Already good — do not touch
- Access bands "takes effect at once" / "reviewed before it applies" — the clearest thing in the product.
- The person ladder's four-layer structure and the "own" column.
- Preview: diff + ordered migration with per-step safety note. Pending bar as a sticky bar whose primary is Preview, never Apply.
- Type grid with one hint; the "Needed for an enum" required block; refused-vs-warned split; the capabilities list on Overview with server wording.
- Hooks tab before/after split. Relationships as the `ref` field. Entity bar. Inert-role treatment. Structured error component. JSON key/string colors distinct in both themes. Global focus ring.

## Should any screen not exist?
No. But "What each one says" on Rules should stop being a second panel and become the editor state of the matrix (#10), and the Automations/Functions "not yet" pages are fine as they are.
