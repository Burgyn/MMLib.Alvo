# F5 admin — the dashboard on MudBlazor, with one interaction pattern language

**Status:** design, executed on `f5/ai-agent` (PR #264). **Decided by the maintainer, 24 Sep 2026.**
**Evidence** (committed): `evidence/2026-09-24-admin-ui-inventory.md` (every screen's interactions, file:line),
`evidence/2026-09-24-mudblazor-study.md` (MudBlazor 9.10.0: a spike app built under the repo's analyzers, Playwright
probes, the per-screen component map in §4.2, the M3 baseline in §7, spikes and migration order in §8),
`evidence/2026-09-24-fluentui-blazor-study.md` (why not Fluent UI Blazor v5).

## 1. Why

The maintainer, driving the real dashboard: *"the whole UI feels cobbled together, not a professionally built system
… editing is in a modal here, inline there, a strip slides in at the top … the AI input has no Ctrl+Enter, does not
scroll, the last message stays in the box … school-level misses … as if you didn't use a library and assembled the
whole UI yourself and couldn't pull it off."* The inventory confirms it: **4** patterns for editing an item, **3** for
creating one, **4** for confirming a destructive action (one of them *none* — deleting a record is a single click),
**5** for success feedback, **5** overlay mechanisms of which only one is shared (and the command palette re-implements
it). The UX pass of 23 Sep measured type sizes, contrast and inline styles; it never wrote down how the UI **behaves**,
and the e2e suite asserts that things render, not that they behave.

## 2. Decisions

| # | Decision | Why | Deviation it records |
|---|---|---|---|
| D1 | **MudBlazor 9.10.0**, pinned exactly, is the component foundation of `MMLib.Alvo.Admin`. | Stable for net10.0; plain HTML (no shadow DOM) so Playwright `GetByRole`/`GetByLabel`/`FillAsync` work; compiled with 0 warnings under the repo's `TreatWarningsAsErrors` + analyzers in the spike; mature dialog / snackbar / data grid / tabs / menus with keyboard handling. | `docs/architecture/admin-dashboard-review.md` "Considered and rejected" listed *a component library*; that rejection is **reversed** by the maintainer. The review gets a dated note saying so and why (the hand-rolled primitives are where the dashboard failed). |
| D2 | **Not Fluent UI Blazor v5.** | Only a release candidate exists for net10 (pre-release would block a stable Alvo, NU5104, and its docs advise against `TreatWarningsAsErrors`); Playwright role locators do not see `ElementInternals` roles; static-SSR inputs unlabeled. | — |
| D3 | **One interaction pattern language (§3) is binding** for every screen, now and later. A screen that needs a pattern §3 does not have adds it to §3 first. | The inconsistency is the defect; a library does not by itself prevent it. | — |
| D4 | **Thin Alvo wrappers** over Mud for the pieces with policy: `AlvoEditor` (the side-sheet editor), `AlvoConfirm` (+ typed-name variant), `AlvoAlert` (role + fix action), `AlvoButton` defaults via a shared style, focus ring. Pages use Mud directly for everything else. | MudBlazor 9 has no global defaults; the a11y gaps (no focus ring, `MudAlert` without role, drawer not a dialog) must be patched once, not per page. | — |
| D5 | **No Mud type in a public parameter.** Wrapper components take Alvo's own enums/strings. | Razor components are public; a Mud enum on a parameter would put MudBlazor's API into `PublicApi.MMLib.Alvo.Admin.verified.txt` and make every Mud major a breaking change of Alvo. | — |
| D6 | **Keep Alvo's logic types verbatim** (`Internal/*`, `WorkingCopy*`, `FieldFacets*`, `Map/*`, Data grid/form helpers, `RevisionHistory`, `RollbackGate`, `DescriptorDiff`/`LineDiff`, gateways). The rebuild is presentation. | They are unit-tested and correct; the defects are in markup and behaviour. | *Amended 26 Sep (Task 11, controller-authorised):* `WorkingCopy.Load` clears the suggested reason (a fresh take has none of the edits it described); `WorkingCopy.Snapshot()` reads the document and its revision as one pair under the gate, for the apply; `AdminInterop.FocusAndSelectAsync` (and admin.js `focusAndSelect`) removed, having no caller. Earlier: `RollbackGate.CanRollBack` deleted (Task 8). |
| D7 | **Keep what is Alvo's own and has no library equivalent**: the system map (SVG), `CodeBlock`, `DescriptorDiff`, the `j`/`k` row cursor and `g`-letter / ⌘K map in `alvo.js`, the design tokens (mapped into `MudTheme`), self-hosted Public Sans + IBM Plex Mono. | — | — |
| D8 | **CSS**: MudBlazor's stylesheet is imported into a cascade layer **below** Alvo's (`@layer mud, tokens, base, layout, components, utilities`), so its unlayered global reset cannot beat Alvo rules during the migration; Alvo ships a `:focus-visible` ring for every interactive element. | Mud's reset (`*{margin:0;…}`, `button:focus{outline:none}`) is unlayered. | — |
| D9 | **Dark mode without a flash**: `alvo.js` always writes a resolved `data-theme` before paint; a second `MudThemeProvider`-equivalent palette is scoped under `:root[data-theme=dark]` (the spike's working pattern). `alvo.theme` stays the one store. | Mud writes one palette server-side. | — |
| D10 | **Sign-in stays native markup** styled with Mud's CSS variables (static SSR, no circuit). | Mud inputs are interactive components; labels overlap on a static page. | — |
| D11 | **The F5 design prototype is not rebuilt** (maintainer, 23 Sep: "leave it"). `alvo.css` keeps its `prototype-only` rules until the prototype retires; `scripts/test-prototype` stays green. The gallery documents tokens, Alvo's own pieces (map, code block, diff) and the wrappers. | — | — |

## 3. The interaction pattern language (binding)

1. **Where editing happens.**
   - *Create or edit one item with more than one value* (entity, field, record, person, index, hook, AI connection):
     **`AlvoEditor`** — a right-hand modal side sheet (a right-positioned `MudDialog`: focus trap, Escape, focus return;
     full-screen below 600 px). Title = "New …" / "Edit …"; primary action bottom-right ("Save to the working copy",
     "Save record", "Create person"); Cancel beside it.
   - *Content read beside the main content* (History detail, Rules simulator, the assistant): a **standard,
     non-modal side pane** (`MudSplitPanel` / persistent end drawer with `role=complementary` and a label). No scrim.
   - *One atomic value with an explicit commit* (rename in place, a toggle): inline, Enter commits, Escape cancels.
     **No save-on-blur**, anywhere.
   - Never a dialog over a dialog. An editor's destructive step replaces the editor's content or is a confirm after it
     closes.
2. **Destructive actions** (delete record, remove field/index/hook/entity, disable person, discard the working copy,
   apply a destructive plan, rollback): **`AlvoConfirm`** — centred dialog, no light dismiss, Escape = Cancel, title =
   the action, body leads with the consequence, the button names the verb ("Delete record"). Irreversible and
   wide-blast-radius ones (destructive apply, rollback, remove entity) add the **type-the-name** step. A staged
   (undoable before apply) removal may instead be immediate with a snackbar **Undo**.
3. **Feedback.**
   - **Snackbar** (`ISnackbar`, **bottom-left, never over an open editor's footer** — the editor is a right-hand
     sheet with its actions bottom-right; 4–6 s, at most two; its text and close on the tone's contrast colour, AA in
     both themes) for the brief confirmation of an action just taken:
     "Saved to the working copy", "Applied as revision 12", "Record deleted". Never the only copy of something the
     operator must keep (a credential token goes in the dialog with a Copy button).
   - **`AlvoAlert`** (persistent, in place, `role=alert` / `role=status`) for state and **every error**, with Alvo's
     structured fix as its action. On a failed submit, focus moves to it. **No error ever goes to a snackbar.**
   - **Field error** under the input for a field-specific refusal.
4. **Forms and keys.** Enter submits a single-line form. In a multi-line input Enter is a newline and
   **Ctrl/Cmd+Enter submits** (the assistant, CEL rules, import). Escape closes the topmost dialog; an editor with
   unsaved changes asks "Discard changes?" first. The submit button is **disabled and shows progress while busy**
   (no double submit anywhere); Cancel stays enabled. On open, focus lands on the first field; on close it returns to
   the trigger. After a submit the input the operator typed into is **cleared** when it was a message/command
   (assistant, search-and-go), kept when it was a form value.
5. **Lists and new items.** A created item appears **in place and is scrolled to and highlighted**; a list that grows
   while the operator watches (the assistant thread, a log) **follows the newest item** unless the operator has
   scrolled up.
6. **Loading, empty, error.** `MudSkeleton` for known-shape content; `MudProgressLinear` at the top of a pane for a
   refresh; content stays in place. Empty = one sentence on what the operator can do + one primary action. Page-level
   failures → the `ErrorBoundary` panel with Reload.
7. **Navigation.** One app bar (search/⌘K, theme, the signed-in menu with Sign out), one nav drawer (responsive: a
   temporary drawer on a phone, plus the existing bottom bar), the pending-changes bar under the app bar. Page title
   row = title, subtitle, primary action, a `…` menu for the rest (the existing `PageHeader` rule).
8. **Fields** (added 26 Sep, after the screenshots of Task 9 showed three input looks in one editor and an outline
   striking through the typed-name confirm's label). One presentation for every text input, whichever element draws
   it — the library's `MudTextField`/`MudSelect`, or a native `<input>` where the library cannot run (sign-in, D10)
   or is not used (the record form's reference combobox, D7):
   - **Name above the box**, never floating and never in a notch: the `Field` component's `<label for>` (or, for a
     group, a label a group names with `aria-labelledby`), left-aligned with the box, `--text-sm`, `--dim`, medium
     weight. A library input carries **no `Label`, `HelperText`, `Margin`, `Dense` or `@attributes`** of its own,
     and it **must be named**: a `Field For=` its id, `aria-label` or `aria-labelledby` (`FieldConventionTests`).
     One name only — the library's label is never drawn beside the Field's. The one exception is a **search over a list** (the entity filter, the Data search):
     no visible name, its placeholder says what it searches and `aria-label` names it.
   - **Box**: the outlined variant, drawn as `.a-input` is — 1 px `--border`, `--border2` on hover, the focus ring on
     the frame, `--panel` background, a `--radius-xs` corner (the buttons' — the theme's `DefaultBorderRadius` — so a
     field and its submit have one edge). The library's own outline and its legend are never drawn.
   - **Text**: `--text-sm` at line-height 1.5 (**`--text-lg`, 16 px, on a phone**, ≤ 720 px: iOS Safari zooms the
     page into any input under 16 px as it takes focus; the name and hint stay `--text-sm`), in the font around it (`a-mono` for names and CEL). The value, the name
     and the hint are one size, so a field reads as one thing. *Deviation from the brief's "body text size"*:
     `--text-sm`, not `--text-base`, because it is the size every Alvo form control (`.a-input`, `.a-label`,
     `.a-hint`, `.a-check`) and the static sign-in page already use; `--text-base` is running text.
   - **Rhythm**: one single-line height everywhere — `--space-2 × --density` above and below one line, `--space-3`
     at the sides, 40 px minimum on a phone; a field has no margin of its own (the stack spaces it, `--space-1`
     between name, box and hint). Multi-line = `Lines="n"` rows of the same line-height.
   - **Width**: a field fills its form's column. Side by side: `.a-field-row` (wraps below 160 px a field). A single
     picker is `.a-field--narrow` (360 px at most); a search over a list is `.a-filter` / `.a-grid-search` (360 px).
   - **Numbers**: `MudTextField InputType.Number` (a decimal is text with `inputmode=decimal`, D-8). `MudNumericField`
     is not used: its spin buttons would be a second adornment look. If one is ever needed, every rule here applies.
   - **Adornments**: a button inside the box (the filter's clear, a select's arrow) sits at its end with 2 px padding
     and keeps the box one line tall. No other adornment (no password reveal, no start icon) is used today.
   - **Required**: an accent `*` after the name, drawn by the stylesheet (`.a-label--required`) with empty
     alternative text, so the accessible name stays the field's name; `aria-required` on the control says the rest.
     Never the browser's `required` (it would validate ahead of the engine). A required **switch** carries the class
     `a-required`, which draws the same mark after its text. *Exemption, D10*: the static sign-in page's two inputs
     are native and carry `required` — a form posted without a circuit has no other way to say "empty"; both fields
     are required, so no mark is drawn there.
   - **Hint** under the box, `.a-hint` at `--text-sm`. The `Field` gives it the id `{For}-hint`, and **the call site
     writes `aria-describedby="{For}-hint"` on the input** (with `FieldRefusals.DescribedBy(id, hint)` when the field
     can be refused): a `Field` cannot set an attribute on the component inside it. `FieldConventionTests` fails a
     hinted Field whose input does not. *Why not shared defaults*: MudBlazor 9.10 has no global input defaults
     (`MudGlobal` carries menu and tooltip defaults only), and a process-wide default would reach an embedding host's
     own MudBlazor UI; a wrapper component cannot be internal and still be a tag (Razor discovers public components
     only), and a public one would add a Mud-shaped parameter surface to PublicApi (D5). So the look is alvo.css's,
     and the two attributes each call site writes (`Variant.Outlined`, `aria-describedby`) are enforced by the test.
   - **Field error** (`FieldRefusals`, the record form's own read errors): the sentence **after the hint** (directly
     under the box when there is none), so every field reads box, hint, refusal — the order its `aria-describedby`
     lists them in, and the hint that says what the box takes stays in view while the refusal says what was wrong
     (decided 26 Sep, after the Task 10 screenshots showed the refusal wedged between the box and its hint).
     `.a-field__problem` at `--text-sm` in `--danger-fg`, with an id the input's `aria-describedby` lists after the
     hint. A `Field`'s refusal is written inside its content, before the hint the `Field` draws, so alvo.css moves it
     last (`order`) rather than the `Field` growing a public parameter for it; the input is `aria-invalid` (`Error`) and its border takes `--danger-fg`. Focus moves to it on a failed
     submit (§3.3). A refusal no field owns is the `ErrorPanel`.
   - **Disabled**: `--panel2` background, `--faint` text. **Read-only**: not a box — a value that can never be changed
     is text (the record form's *Calculated* readout), because a greyed box reads as a broken control.
   - **Select**: `MudSelect` outlined, named by a `Field` label through `aria-labelledby`, the same box.
   - **Checkbox / switch**: the library's (`MudCheckBox`, `MudSwitch`; never a native checkbox), its label beside it
     at `--text-sm`; a group of them sits under a label it names with `role="group"` + `aria-labelledby`
     ("Constraints"). **Chip groups** (`ChipGroup`) stay Alvo's, under a `Field` label, as V7 has them.
   - Not a field: the command palette's input, which is the palette's own search-and-go line (§3.7).

   *This item supersedes the plan's control-migration rule on two points*: a library input is not given the
   `Field`'s label as `Label` nor its hint as `HelperText`; the `Field` stays around it. Reason: the library draws a
   label on an outlined box in a notch sized by a classless `legend` (which alvo.css's base-layer restore reached),
   floats it into the box when empty, pads its helper text with an `!important` utility no layer above the library
   can outrank, and turns the label red with another `!important`. A name the library never draws cannot be drawn any
   of those ways. `FieldConsistencyScenarios` measures the rule in a browser, both themes, sign-in included.

## 4. Screen map

The per-screen composition is the MudBlazor study's §4.2 table (restated in the plan per task). Changes of *behaviour*
beyond the migration, all from the inventory:

| Screen | Behaviour that changes |
|---|---|
| Assistant | full-height non-modal pane opened from the app bar; Ctrl/Cmd+Enter; thread follows the newest turn; input cleared on send; Send disabled while a turn streams; `aria-live=polite` thread |
| History | split pane; the selected revision shows **"Changes from r(n−1)"** by default (`DescriptorDiff` of the two revisions' descriptors), with a JSON toggle; rollback plan inline + `AlvoConfirm` typed-name |
| Data record | Delete → `AlvoConfirm`; save/delete → snackbar; the editor guards unsaved changes |
| Access | person editor in `AlvoEditor` (roles, tenant, token with Copy, Disable → `AlvoConfirm`); Add person → `AlvoEditor`; Create disabled while busy |
| Schema | New entity → `AlvoEditor` (was the top strip); a filter over the entity list; field / index / hook editors → `AlvoEditor`; rules get an explicit Save + dirty marker (no save-on-blur); Remove field → `AlvoConfirm` |
| Command palette | `MudDialog` (top) — Escape, focus, scroll lock from the dialog |
| Settings | AI connection form → snackbar "Saved"; refusal → `AlvoAlert` |

## 5. Tests that must exist (behaviour, not rendering)

For every task, e2e scenarios with `GetByRole`/`GetByLabel`/`GetByTestId` that assert behaviour: the §3 rule the
screen uses (focus lands in the editor, Escape closes it and focus returns, the dirty guard asks, the submit is
disabled while busy and a double click creates one item, a destructive action cannot run without its confirm, a
snackbar appears after success, an error is an alert with focus on it), plus the assistant set (Ctrl+Enter sends,
Enter makes a newline, the input is empty after send, the thread's last turn is in view) and the History diff (the
diff against the previous revision is what opens). The existing behavioural scenarios keep passing, ported to the new
DOM. `EndToEndSelectorTests` ratchet: raw class selectors and `:has-text(` must **fall**, not merely not rise.

## 6. What this does not do

No change to the Management/Data APIs, the working-copy model or any core package. No second theme or rebrand — the
identity is kept and mapped into `MudTheme`. No bUnit. The prototype is not ported.
