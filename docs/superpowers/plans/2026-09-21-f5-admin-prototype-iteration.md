# F5 admin prototype — second iteration Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development
> (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use
> checkbox (`- [ ]`) syntax for tracking.

**Goal:** Close the four design gaps the F5 admin drawing exposed, then rebuild the drawing on
one working copy of the descriptor and prove it with an end-to-end suite that lives in this
repository.

**Architecture:** Three movements. (1) `docs/superpowers/specs/2026-09-18-f5-admin-dashboard-design.md`
gains four sections — a tenant for a signed-in operator, the simulator's real shape, how a second
local human comes to exist, and how three kinds of edit queue into one apply — plus the deviations
the reviewers judged justified. (2) The prototype moves into the repository at
`docs/design/f5-admin/`, stops carrying a copy of `alvo.css`, and is rebuilt around a single
`state.working` JSON document diffed against `state.applied`; every fixture it shows is generated
from the repository rather than authored. (3) A Node + `@playwright/test` suite beside it drives
ten realistic operator scenarios at 1400 px and 375 px in both themes, asserting behaviour, layout
and a clean console.

**Tech Stack:** plain HTML + CSS + vanilla ES modules (no build step); Node 22 +
`@playwright/test` for the suite; `python3 -m http.server` as the static server; POSIX shell for
`scripts/test-prototype` and `scripts/gen-prototype-fixtures`.

**Spec:** `docs/superpowers/specs/2026-09-18-f5-admin-dashboard-design.md` (amended by Phase 1 of
this plan), `~/alvo-admin-prototype/REVIEW-product.md`, `~/alvo-admin-prototype/REVIEW-ux.md`
(both copied into `docs/design/f5-admin/reviews/` by Task 6 so the backlog travels with the work).

**Status: executed, 2026-09-21.** Every task ran and every box is ticked. Two places where what
shipped differs from what this plan said, recorded rather than quietly reconciled:

- **Task 5 said the upheld drawing decisions join §0.2's deviation table. They did not.** §0.2 is
  *deviations from the sources*; the drawing's own calls are departures from **this design**, which
  is a different layer, so they landed in a new **§4.6** with that reason stated. §0.2 did gain one
  genuine row — **D8**, the Node suite against spec §415's *"žiadny Node v pipeline"*.
- **Phase 5 grew a twelfth spec.** `12-contrast.spec.js` measures acceptance criterion §6.3-5
  (WCAG AA over the tokens) which this plan did not schedule, and it carries a vacuity guard
  because a contrast test with a broken ratio function passes everything.

Findings from `alvo-plan-guard` were folded back into Phase 1's output before the PR: the port
split in §3.7, `IssueCredentialTokenAsync`'s refusal-by-name, the tenant self-grant weighed rather
than assumed, U3 moved from an adapter into the core with a contract test, D8, the committed
lockfile with `npm ci`, and the retirement obligation.

## Global Constraints

- **Never commit to `main`.** All work lands on `f5/admin-prototype-iteration` and reaches `main`
  through a PR a human merges.
- **Every number, route, field facet and quoted refusal in the prototype must be traceable to a
  file actually opened.** Where a value comes from the repository it is *generated* by
  `scripts/gen-prototype-fixtures`, never retyped.
- **Refusal and warning prose is served verbatim and never rewritten**
  (`ManagementCapabilities`' own remark; design §2.3).
- **The frozen schema bounds the UI.** `schema/project.schema.json` is frozen; a control whose
  only possible output is a descriptor the apply rejects must not exist, or must be inert carrying
  the refusal text.
- **Management routes are `{m}/projects/{p}/…`.** Only `/info` and `/projects` are unprefixed
  (`ManagementEndpoints`, `management-api.md` §The surface).
- **The access levels are `admin` / `developer` / `viewer`** with the grants
  `ManagementOperations.cs` gives: `viewer` may simulate; `developer` may apply *and* roll back;
  `admin` adds the settings surface. There is no `editor`.
- **`info` reports `dataProvider`, never an engine name** (`management-api.md`).
- **The prototype is a design artifact, not product code.** It carries no build step and no
  framework, and nothing in `src/` may depend on it.
- **Console must stay clean.** Every scenario asserts zero `console.error` and zero uncaught
  exceptions; a caught exception that blanks a screen is the defect class that already took the
  Access page down once.

---

## File structure

| File | Responsibility |
|---|---|
| `docs/superpowers/specs/2026-09-18-f5-admin-dashboard-design.md` | the merged design; gains §2.7, §3.7, §3.8, §4.5 and amended §0.2 / §4.2 |
| `docs/design/f5-admin/index.html` | the page; links the repository's own `alvo.css` |
| `docs/design/f5-admin/proposed.css` | what this design asks to add to `alvo.css` — the component inventory |
| `docs/design/f5-admin/app.js` | shell, routes, screens, interaction |
| `docs/design/f5-admin/working-copy.js` | the one working copy: `applied`, `working`, diff, change groups, apply/rollback |
| `docs/design/f5-admin/policy.js` | what the simulator renders — the verdict shape, never an evaluator |
| `docs/design/f5-admin/generated/capabilities.js` | `honoured` / `warned` / `refused`, generated |
| `docs/design/f5-admin/generated/descriptor.js` | the field-service descriptor, generated verbatim |
| `docs/design/f5-admin/generated/schema-facets.js` | per-type facets, generated from `$defs/field` |
| `docs/design/f5-admin/sample-rows.js` | sample records — the only authored content left |
| `docs/design/f5-admin/notes.js` | the decision log rendered at `#/notes` |
| `docs/design/f5-admin/tests/*.spec.js` | one file per scenario |
| `docs/design/f5-admin/tests/helpers.js` | server fixture, theme/viewport matrix, console guard |
| `scripts/gen-prototype-fixtures` | regenerates `generated/*.js` from the repository |
| `scripts/test-prototype` | installs deps if needed and runs the suite |

---

# Phase 1 — the four design gaps

Each task amends `docs/superpowers/specs/2026-09-18-f5-admin-dashboard-design.md` in its own voice
(numbered deviations, "stated rather than left implicit", a Do/Why/Cost shape) and commits alone,
so a reviewer can reject one gap's answer without rejecting the others.

### Task 1: §2.7 — a signed-in operator has no tenant

**Files:** Modify: the design doc (new §2.7, after §2.6).

**Facts it must argue from, each verified:**
- `AlvoIdentityContextResolver.ResolveAsync` returns `null` when `requestedTenant` is non-empty,
  and the principal it mints carries no `Tenant`.
- `ManagementSimulatedCaller.Tenant`: "null denies on a tenant-scoped entity, as production does".
- `data-api.md` §The decision procedure, cause 2: the tenant guard refuses a scoped entity for a
  caller with no tenant *before any rule is consulted*.
- No tenant registry exists anywhere in `src/`.
- `field-service.alvo.json` sets `tenancy.enabled` and marks `customers`/`work_orders` scoped,
  `regions` global.

- [x] **Step 1:** Write §2.7 stating the gap as a fact with its consequence: in the example the
      product ships to demonstrate multi-tenancy, a signed-in operator can browse exactly one of
      three entities.
- [x] **Step 2:** Write the decision — an explicit per-session tenant the identity resolver
      accepts, gated on management level, and a tenant list that comes from the descriptor's own
      declaration rather than from the data. Record the two rejected alternatives with their
      reasons (`SELECT DISTINCT tenant_id`, and an admin bypass) and the deviation it creates.
- [x] **Step 3:** State what F5 ships versus what the gap defers, and what the dashboard must do
      until then.
- [x] **Step 4:** Commit `docs(f5): a signed-in operator acquires a tenant, or the Data screen says so`.

### Task 2: §2.2 correction — the simulator returns a predicate, not a row decision

**Files:** Modify: the design doc §2.2 (the `policy/simulate` paragraph) and §6.3 criterion 4.

**Facts:** `ManagementPolicySimulation(Entity, Operation, Caller)` — no record id, with the remark
that says why; `ManagementPolicyVerdict.Allowed` "is not 'this caller will see rows'";
`management-api.md` §"The record-id arm of the simulator" is the correction's home and the design
still carries the superseded sentence *"optionally a record id"*.

- [x] **Step 1:** Replace *"optionally a record id"* with the shipped shape and mark the change as
      a correction rather than editing it silently, on §2.1.1's own precedent.
- [x] **Step 2:** Write what a client may render: `Using` / `WithCheck` / `TenantScope`,
      `HiddenFields`, `ReadOnlyFields`, `DenyReason`, and the four ways a caller actually gets 403.
- [x] **Step 3:** State the rule the drawing broke — a client that evaluates a stored row itself is
      a second policy evaluator, which §6.3 criterion 4 forbids by construction — and give the
      honest affordance that replaces it.
- [x] **Step 4:** Commit.

### Task 3: §3.7 — a second human under `providers: ["local"]`

**Files:** Modify: the design doc (new §3.7 after §3.6); amend deviation **D8** wherever it is
referenced.

**Facts:** `IAlvoUserStore` has `FindAsync`, `FindByEmailAsync`, `ListAsync`, `SetRolesAsync` and
no create; `host.md` §The bootstrap administrator — seeding only ever *creates* the account once
and never resets a password; `host.md` — *"What they cannot do is sign in: nothing in `src/`
resolves `AlvoIdentity.ResolverKey`, no cookie authentication scheme is added, and there is no
sign-in endpoint"*; `ManagementOperation.ManageUsers` is in the level table with no route.

- [x] **Step 1:** State the gap: with local auth nobody can sign in until an account exists, and
      only the bootstrap admin is seeded — so a local-auth project can never have a second
      administrator, and `IAlvoUserStore.ListAsync` returns one row forever.
- [x] **Step 2:** Decide where creation lives — on the port, or on the Identity package's own admin
      surface — and say which, with the reason, and what `ManageUsers` gets as a route.
- [x] **Step 3:** Record U3 and U4 from the product review as known misses against the F5
      acceptance list, with their issues.
- [x] **Step 4:** Commit.

### Task 4: §4.5 — three kinds of edit, one descriptor, one apply

**Files:** Modify: the design doc (new §4.5 after §4.4).

**Facts:** `PUT {m}/projects/{p}/descriptor` takes one `DescriptorJson`;
`ManagementPlanSummary.IsEmpty` is true for a rules-only edit and is *not* "nothing was applied";
role *membership* is `IAlvoUserStore`, outside the descriptor; the role *catalogue* is
`auth.roles`, inside it.

- [x] **Step 1:** Name the three kinds and where each lives: schema and rules and `auth.roles` are
      the descriptor; role membership is the identity store and applies at once.
- [x] **Step 2:** Decide: one working copy of the descriptor, one preview, one apply — and state
      what the preview shows for each kind, including that a rules-only or roles-only apply has an
      empty migration plan and why that is not "nothing happened".
- [x] **Step 3:** State the consequence for the UI: one pending count in the shell, one bar, one
      preview grouped by kind, and membership visibly outside the queue.
- [x] **Step 4:** Commit.

### Task 5: §0.2 and §4.2 — the deviations the reviewers upheld, and the routes that were drawn but never recorded

**Files:** Modify: the design doc §0.2 (deviation table) and §4.2 (route map).

- [x] **Step 1:** Add the upheld deviations to §0.2 with their reasons: Schema above Data;
      Access split by speed; no provider picker; the roles×operations matrix (distinguished from
      the rejected teams matrix); branch conditions with a raw fallback; the read-only model map;
      the API tab; the On-write tab; the Integrations page; the wizard creating no entity.
- [x] **Step 2:** Correct §4.2's **Access** row: membership lives in the identity store, the role
      catalogue in the descriptor — not "users and roles LIVE".
- [x] **Step 3:** Add the three routes the drawing invented to the route map with their F5 status
      and data source: Integrations, the entity API tab, Assistant-in-Settings.
- [x] **Step 4:** Correct the §4.2 **Settings** row: `ManageApiKeys` and `DeleteProject` have no
      route and `IApiKeyStore` is `FindAsync` + `TouchAsync`, so "API keys LIVE" and "danger zone
      LIVE" are both false; say what is actually reachable.
- [x] **Step 5:** Record in §6.4 that the assistant is out of F5, and why the drawn shape is kept
      as a design.
- [x] **Step 6:** Commit, then run `scripts/test-ring0` to confirm nothing in the repository
      depended on the prose.

---

# Phase 2 — the prototype enters the repository, and its content is derived

### Task 6: relocate, de-duplicate the stylesheet, scaffold the suite

**Files:**
- Create: `docs/design/f5-admin/{index.html,app.js,proposed.css,alvo-mark.svg,alvo-wordmark.svg}`
- Create: `docs/design/f5-admin/README.md`, `docs/design/f5-admin/reviews/REVIEW-{product,ux}.md`
- Create: `docs/design/f5-admin/tests/{package.json,playwright.config.js,helpers.js,smoke.spec.js}`
- Create: `scripts/test-prototype`
- Delete: the prototype's own `alvo.css`

**Interfaces produced:** `helpers.js` exports `open(page, route, {theme, width})`,
`guardConsole(page)` and `MATRIX` (`[{theme:'light',width:1400}, …]`), used by every later spec.

- [x] **Step 1:** Copy `~/alvo-admin-prototype/` into `docs/design/f5-admin/`, dropping `alvo.css`,
      `HANDOFF.md` and `PROMPT.md`; put the two reviews under `reviews/`.
- [x] **Step 2:** Point `index.html` at `../../../src/MMLib.Alvo.Admin/wwwroot/alvo.css` so exactly
      one copy of the design system exists, and say so in `README.md` with the run instruction
      (`python3 -m http.server 8099` from the repository root).
- [x] **Step 3:** Write `tests/package.json` pinning `@playwright/test`, and
      `playwright.config.js` with a `webServer` running the static server at the repository root
      and `use.baseURL` naming `/docs/design/f5-admin/`.
- [x] **Step 4:** Write `helpers.js` with the console guard: collect `console.error`, `pageerror`
      and failed requests, and fail the test if any is non-empty.
- [x] **Step 5:** Write `smoke.spec.js` — every route in `NAV` plus `#/notes` opens, renders a
      non-empty `main`, and logs nothing.
- [x] **Step 6:** Write `scripts/test-prototype` (install if `node_modules` is absent, then
      `npx playwright test`), make it executable, and record in `CLAUDE.md`'s ring table that it is
      in no ring, with `scripts/test-load`'s reason.
- [x] **Step 7:** Run `scripts/test-prototype`. Expect the smoke spec to fail on whatever the
      relocation broke; fix until green.
- [x] **Step 8:** Commit.

### Task 7: generate the capability fixtures instead of authoring them

**Files:**
- Create: `scripts/gen-prototype-fixtures`
- Create: `docs/design/f5-admin/generated/capabilities.js`
- Modify: `docs/design/f5-admin/data.js` (drop `REFUSED`, `WARNED`)

**Facts the generator reads, and nothing else:**
`test/MMLib.Alvo.Tests/UnhonouredFeaturesTests.Both_unhonoured_tables_are_pinned.verified.txt`,
`test/MMLib.Alvo.Tests/UnhonouredFeaturesTests.Every_unhonoured_slot_is_pinned.verified.txt`,
`src/MMLib.Alvo/Descriptor/Internal/UnhonouredSubsystems.cs`,
`src/MMLib.Alvo/Management/Internal/CapabilityReport.cs`.

- [x] **Step 1:** Write the generator so `refused` carries all eleven slots in `EveryRefusal`'s
      order — `field.validation`, `field.default`, `entity.softDelete`, `rollup.where`,
      `trigger.event`, `JSONata`, `email.data`, `bodyFile`, `function`, `http.call`,
      `entity.update` — each with `consequence` and `fix` byte-for-byte.
- [x] **Step 2:** Have it emit `warned` from `UnhonouredSubsystems.All` (five blocks, in order) and
      `honoured` from `CapabilityReport.Honoured`.
- [x] **Step 3:** Have it write a header naming the source file and line of every value, and a
      `GENERATED — do not edit` line.
- [x] **Step 4:** Add a check mode (`--check`) that regenerates into a temp file and diffs, so a
      later drift is a red script rather than a stale drawing.
- [x] **Step 5:** Run it; assert in a new `tests/capabilities.spec.js` that the Overview's warned
      list is the intersection of `warned` with the descriptor's declared blocks (for
      field-service: empty) and that every refused slot the UI shows matches the fixture exactly.
- [x] **Step 6:** Commit.

### Task 8: generate the descriptor, and the facets, from the frozen sources

**Files:**
- Modify: `scripts/gen-prototype-fixtures`
- Create: `docs/design/f5-admin/generated/descriptor.js`, `…/generated/schema-facets.js`
- Modify: `docs/design/f5-admin/data.js` → `sample-rows.js` (records only)

- [x] **Step 1:** Have the generator copy `examples/field-service/field-service.alvo.json`
      verbatim into `descriptor.js` as `export const APPLIED_DESCRIPTOR = { … }`.
- [x] **Step 2:** Have it derive `schema-facets.js` from `$defs/field`: the eleven types from
      `$defs/fieldType.enum`, the `needs` list from each `if/then` `required`, the `optional` list
      from each `if not/then properties:false` block, and the built-in formats from
      `field.format.anyOf[0].enum` (`email`, `uri`, `phone`) — never a hand-written `url`.
- [x] **Step 3:** Reduce `data.js` to sample rows, users, revisions and palette items, and rename
      it `sample-rows.js`; delete the "every value is taken from the repository verbatim" header,
      which was false, and replace it with what the file actually is.
- [x] **Step 4:** Rename the tenants so neither shares a name with a customer (UX #15).
- [x] **Step 5:** Run the suite; commit.

---

# Phase 3 — one working copy

### Task 9: `working-copy.js` — one document, one diff, one count

**Files:** Create `docs/design/f5-admin/working-copy.js`; modify `app.js`.

**Interfaces produced:**
```js
export const wc = {
  applied,                 // the JSON document at the applied revision
  working,                 // the JSON document being edited
  revision,                // the applied revision number
  changes(),               // [{kind:'schema'|'rules'|'roles', pointer, before, after, label}]
  count(),                 // changes().length
  reset(), apply({author, reason}), rollbackTo(n),
  markedLines(),           // Set of line numbers changed, for the descriptor pane's gutter
};
```

- [x] **Step 1:** Write a failing test `tests/working-copy.spec.js`: edit a field's `precision` on
      Schema, then assert the shell's pending count reads 1, the Rules screen's bar reads the same
      1, and Preview lists that one change under **Schema**.
- [x] **Step 2:** Run it; expect three separate counters and a preview that lists nothing.
- [x] **Step 3:** Implement `working-copy.js` as a JSON-pointer diff over two plain objects,
      classifying each pointer: `/entities/*/fields/*` and `/entities/*/indexes` → schema,
      `/entities/*/rules/*` → rules, `/auth/roles` → roles.
- [x] **Step 4:** Replace `state.pending`, `ruleChanges()` and `catalogChanged` with `wc`
      throughout `app.js`; delete the three pending bars and render one.
- [x] **Step 5:** Run; commit.

### Task 10: the descriptor pane shows the working copy

**Files:** Modify `app.js` (`descriptorJson`, `fieldObject`, the entity screen aside).

- [x] **Step 1:** Write a failing test: change `quoted_price.precision` from 10 to 12; assert the
      pane's header reads `working copy · 1 change vs r7`, that the line containing `"precision"`
      carries the changed marker, and that the pane text contains `12`.
- [x] **Step 2:** Delete `fieldObject()` as the pane's source and render
      `JSON.stringify(wc.working, null, 2)` instead, so nothing the descriptor carries is dropped
      — `nullable`, `index`, `renamedFrom`, `default`, `validation`, entity `storage`, `realtime`,
      `renamedFrom`, index `unique`, and any `x-` key all survive by construction (U1).
- [x] **Step 3:** Compute the gutter from `wc.markedLines()`, and put a changed dot on each edited
      field row in the list.
- [x] **Step 4:** Make the export modal serve `wc.applied` for "as applied" and `wc.working` for
      "as it will be sent", each labelled, since `DescriptorJson` is the export and not a
      re-serialisation.
- [x] **Step 5:** Run; commit.

### Task 11: one preview, grouped by kind, with an honest plan

**Files:** Modify `app.js` (`screenPreview`).

- [x] **Step 1:** Write a failing test: make one schema edit, one rule edit and one role-catalogue
      edit; assert Preview shows three groups with one row each, and that the migration plan
      section says in so many words that the rules and roles changes produce no migration step.
- [x] **Step 2:** Render the three groups from `wc.changes()`, each with its own diff.
- [x] **Step 3:** Render the migration plan for the schema group only, and carry
      `ManagementPlanSummary`'s own distinction: `isEmpty` is *the descriptor changes nothing about
      the schema*, which is not *nothing was applied*.
- [x] **Step 4:** Add the destructive path: when a step discards data, the confirm requires the
      entity name typed and lists what is lost, and `allowDestructive` is never implied by having
      previewed.
- [x] **Step 5:** Run; commit.

### Task 12: apply, conflict, rollback

**Files:** Modify `app.js` (apply flow, `screenHistory`).

- [x] **Step 1:** Write a failing test: apply; assert the revision advances, the pending count
      falls to 0, the pane's header stops saying "working copy", and History's top row carries the
      typed reason and the signed-in author.
- [x] **Step 2:** Add the Reason field to the apply confirm (`ManagementApplyRequest.Author`/
      `Reason` are what History renders, U5).
- [x] **Step 3:** Add the 412 state: a "somebody else applied revision 8 while you were editing"
      panel offering re-preview against the new base, rendered as the structured error component
      and not a toast.
- [x] **Step 4:** Add the 428 note where `If-Match` is explained, and the `developer`-on-Preview
      state: Apply disabled with the reason, because a `developer` may not apply a descriptor whose
      `access` block differs from the applied one.
- [x] **Step 5:** Make History compare any two revisions, not only against the head, and keep the
      typed-name confirmation on restore. Fix `rolledBackFrom` to mean *the revision this one
      restored* and render it without arithmetic.
- [x] **Step 6:** Run; commit.

---

# Phase 4 — the screens the reviews named

### Task 13: the field editor stops covering the pane it annotates

- [x] **Step 1:** Failing test: open a field at 1400 px; assert the descriptor pane is visible and
      not overlapped (bounding boxes do not intersect).
- [x] **Step 2:** Move field editing into the right column above the pane, with that field's
      fragment live beneath it; keep a drawer only below 1100 px, full-width.
- [x] **Step 3:** Show the type-change warning on a *changed* type, never permanently.
- [x] **Step 4:** Render the refused facets as inert controls carrying the generated refusal text:
      `field.default`, `field.validation` on a field; `entity.softDelete` on an entity;
      `rollup.where` inside a rollup.
- [x] **Step 5:** Add the missing editors the honoured features need: `computed`, `rollup`,
      `index`, and state once that `index: true` and an `indexes` entry are the same thing.
- [x] **Step 6:** Run; commit.

### Task 14: the simulator renders a verdict, and evaluates nothing

**Files:** Create `policy.js`; modify `app.js` (`screenRules`, delete `evaluate()`,
`holdsCondition()`).

- [x] **Step 1:** Failing test: pick a technician and `list` on `work_orders`; assert the screen
      shows the `Using` CEL, says the result is *a shorter list, not an error*, and that no
      per-record allowed/refused badge exists anywhere on the screen.
- [x] **Step 2:** Write `policy.js` to produce `ManagementPolicyVerdict`'s shape from the working
      copy's rules: `Allowed`, `DenyReason`, `Using`, `WithCheck`, `TenantScope`, `HiddenFields`,
      `ReadOnlyFields` — and the four 403 causes, in `PolicyEngine.Resolve`'s order.
- [x] **Step 3:** Delete `evaluate()` and `holdsCondition()`; replace "Try it on someone" with
      "open a row as yourself and compare", which is the only honest affordance a client holds.
- [x] **Step 4:** Say, where the simulator runs against unapplied rules, that callers still get the
      applied revision.
- [x] **Step 5:** State per operation what "the record" means, and that hooks say `new.`/`old.`
      while rules say the bare field.
- [x] **Step 6:** Run; commit.

### Task 15: Data is honest about the tenant, and about paging

- [x] **Step 1:** Failing test: open `#/data/customers` as a signed-in operator with no tenant;
      assert the screen refuses with the tenant-guard reason rather than showing rows, and that the
      route to acquiring one (§2.7) is on the screen.
- [x] **Step 2:** Implement §2.7's decision as drawn; make `regions` (global) browsable throughout.
- [x] **Step 3:** Replace *"page 900 costs what page 1 costs"* with the measured claim —
      stable under concurrent writes — and remove **Previous** or mark it client-side history.
- [x] **Step 4:** Make the filter-over-a-hidden-field error indistinguishable from an unknown key,
      and use only slugs in `AlvoProblemTypes.All`; a unique collision is `409 conflict` with
      violation code `unique`.
- [x] **Step 5:** Sort the sample curl by a required column and say why.
- [x] **Step 6:** Add a column chooser mapped to `select=`, and the loading and empty states.
- [x] **Step 7:** Run; commit.

### Task 16: the record form

- [x] **Step 1:** Failing test: open New record on `work_orders`; assert no error is visible before
      typing, that `access_code` (required + hidden) is on the form with its explanation, that
      `internal_notes` (hidden, optional) is on the form too, that `assigned_to` (uuid) is a plain
      input and not a person picker, and that both ref pickers start collapsed.
- [x] **Step 2:** Rebuild `recordForm()` from the working copy's fields, excluding only
      `readOnly` / `computed` / `rollup`.
- [x] **Step 3:** Put each error inline at its field, from an RFC 9457 `violations` entry, and keep
      it until fixed.
- [x] **Step 4:** Make the ref picker say what it searches, and stop naming a `displayField` the
      schema does not have.
- [x] **Step 5:** Run; commit.

### Task 17: nothing is drawn live that is not live

- [x] **Step 1:** Failing test: walk every `button` and `[data-act]` on every route; assert each is
      either wired (clicking changes the DOM or the route) or carries `disabled` plus a reason.
- [x] **Step 2:** Wire or disable: Hooks Edit / Add a hook, Indexes Add / Remove, API Open
      reference / Download OpenAPI, the reorder handle, the Filter fields input.
- [x] **Step 3:** Integrations: serve `WARNED.webhooks` and `WARNED.templates` verbatim, once, and
      make New endpoint / New template inert carrying the refusals they would produce.
- [x] **Step 4:** Settings: remove the engine badge in all three places and show `dataProvider`;
      remove key issuance and revocation, which no store supports, and show what
      `ApiKeyRecord` actually carries — `User`, `RoleNames`, `Tenant`, `ExpiresAt`, `RevokedAt` —
      with the note that **roles**, not scopes, decide management reach.
- [x] **Step 5:** Fix the sample hooks: `!has(old.completed_on)` rather than `== null`, and a
      mutate the `Mutate` profile admits.
- [x] **Step 6:** Run; commit.

### Task 18: Access

- [x] **Step 1:** Failing test: declare a role, assert it sits unapplied in the same one pending
      count, apply it, assign it, and assert the person's level changes.
- [x] **Step 2:** Give "Who may use this dashboard" an editor over `access.admin`/`developer`/
      `viewer`, compiled against the `Access` profile's closed construct set.
- [x] **Step 3:** Stop offering `authenticated` as assignable; confirm before granting `admin`;
      offer "Declare <name>" from the inert-role warning.
- [x] **Step 4:** Show `IsDisabled`; drop "Last seen" and display names no port supplies; add
      search and paging over people, and say the port has neither yet.
- [x] **Step 5:** Compute the ladder from applied rules, or badge a draft-derived fact.
- [x] **Step 6:** Say on the sign-in screen, and on Access, what happens to a caller who matches no
      level — listing the three predicates.
- [x] **Step 7:** Run; commit.

### Task 19: keyboard and focus

- [x] **Step 1:** Failing test: ⌘K, type, ArrowDown, Enter — the route changes; Esc returns focus
      to the trigger; Tab from the drawer's first control never leaves it.
- [x] **Step 2:** Focus the palette input on open (after insertion, not via `autofocus`); wire
      Up/Down/Enter and filter.
- [x] **Step 3:** Give every drawer `role="dialog"` + `aria-modal`, a focus trap and focus return.
- [x] **Step 4:** Make `role="switch"`/`role="checkbox"` spans focusable and Space/Enter operable;
      make `tr[data-act]` rows reachable.
- [x] **Step 5:** Add `j`/`k`, `/`, `g`+letter, and Enter-to-open.
- [x] **Step 6:** Run; commit.

### Task 20: the missing states

- [x] **Step 1:** Failing test at `#/schema` with no entities: an empty state that says what to do
      next, and a New entity control.
- [x] **Step 2:** Entity with no rules: a callout saying default-deny refuses everyone.
- [x] **Step 3:** Entity bar past eight entities: typeahead; model map: selected entity ± 1 hop.
- [x] **Step 4:** Apply failure and dry-run refusal states, rendered as the structured error block.
- [x] **Step 5:** Long CEL, long enum lists, a 12-role sentence collapsing past four.
- [x] **Step 6:** Run; commit.

---

# Phase 5 — the scenarios

Each is one spec file, run across `{light,dark} × {1400,375}`, and each asserts three things
separately: **works** (no dead control, no thrown error, the state changed), **looks right**
(nothing clipped or overlapping, no visible placeholder, both themes), **usable** (the move count
is asserted against a stated budget, and the copy that explains what happened is asserted by text).

- [x] **Task 21:** `01-first-run.spec.js` — empty instance, sign in as the bootstrap administrator,
      name the project, land somewhere useful with no entities.
- [x] **Task 22:** `02-model-a-domain.spec.js` — create `invoices`; a string with a format, a
      decimal, an enum, a date, a `ref` to `customers`; preview; apply; then a `count` rollup on
      `customers`.
- [x] **Task 23:** `03-safe-then-unsafe.spec.js` — widen a decimal; then change a field's type on
      an entity with rows and confirm the destructive path asks for the entity name and says what
      is lost.
- [x] **Task 24:** `04-publish-one-endpoint.spec.js` — `anon` on `regions` read, the warning, the
      preview, the apply, and the revert.
- [x] **Task 25:** `05-layered-permission.spec.js` — dispatchers may change any work order; the
      assigned technician may change theirs while it is not completed. Build it, read it in the
      simulator, then open the technician in Access and assert the ladder says the same thing.
- [x] **Task 26:** `06-diagnose-a-refusal.spec.js` — "why can Peter not see WO-100419" — reach the
      answer and assert the answer is the true one (a row-level exclusion on `get` is a 404, and on
      `list` it is a shorter page).
- [x] **Task 27:** `07-declare-and-use-a-role.spec.js` — new role, unapplied, applied, assigned,
      level changes.
- [x] **Task 28:** `08-record-crud.spec.js` — create a work order through the ref picker and the
      required hidden field; hit a unique violation; fix it; open the detail; follow the reverse
      relation.
- [x] **Task 29:** `09-roll-back.spec.js` — compare two revisions, restore the older one with the
      typed confirmation.
- [x] **Task 30:** `10-honest-edges.spec.js` — Integrations' unsigned deliveries and unused
      template, Automations and Functions as *not yet*, a refused facet in the field editor, and
      the capability panel intersected with the descriptor.
- [x] **Task 31:** `11-responsive.spec.js` — every route at 375 px: no horizontal scroll, the
      matrix reachable, the bottom bar holding only live entries.

Each task: write the spec, run it, fix the prototype until it passes at all four matrix cells,
screenshot the states worth seeing into `docs/design/f5-admin/tests/screenshots/`, commit.

---

# Phase 6 — close

- [x] **Task 32:** Rewrite `#/notes` from `notes.js` so it records the decisions actually made,
      including every reviewer finding rejected and why.
- [x] **Task 33:** Write `~/alvo-admin-prototype/SESSION.md` (and a copy at
      `docs/design/f5-admin/SESSION.md`): what changed, what was decided and on what grounds, what
      could not be done and why, what the next session takes.
- [x] **Task 34:** Run `scripts/test-ring0`, `scripts/test-ring1`, `scripts/test-prototype`; freeze
      the tree; dispatch `alvo-plan-guard`; build the PR report via `alvo-pr-report`; open the PR.
