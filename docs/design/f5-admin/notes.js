/* The decision log, rendered at #/notes.
   ======================================

   Every deliberate choice with its reason — including the ones that reverse an earlier version of
   this prototype, and the review findings this iteration rejected. A decision a later reader
   cannot tell from an oversight is not recorded. */

/** `reversal: true` marks a decision that undoes one the first iteration made. */
export const DECISIONS = [
  {
    title: 'There is one working copy of the descriptor, one preview, and one apply.',
    why: 'Schema edits, rule edits and the role catalogue each had their own pending bar, all three linked to one preview, and the preview rendered only the schema ones. A person who ticked a cell in the matrix, read "1 rule changed", opened Preview and saw two unrelated schema rows had no way to tell whether Apply would carry their rule or drop it. The product has one document: one <code class="a-mono">DescriptorJson</code>, one <code class="a-mono">If-Match</code>, one appended revision. So the shell carries one count, every screen shows the same bar, and Preview groups the diff by kind.',
    alternative: 'Keep three queues and make each apply separately. Rejected: there is no second write path to apply them through, so it would be three buttons calling the same endpoint with the same document — three spellings of one truth.',
    source: 'ManagementApplyRequest · design §4.5',
    reversal: true,
  },
  {
    title: 'The descriptor pane renders the working copy, with the changed lines marked.',
    why: 'It said "what apply will receive" and rendered the applied revision — <code class="a-mono">precision: 10</code> under a bar reading "2 changes not applied". Worse, it rendered a typed projection of a hand-written model, which silently dropped <code class="a-mono">nullable</code>, <code class="a-mono">index</code>, <code class="a-mono">renamedFrom</code>, <code class="a-mono">default</code>, <code class="a-mono">storage</code>, <code class="a-mono">realtime</code>, every <code class="a-mono">x-</code> key, and flattened a CEL-valued <code class="a-mono">hidden</code> to <code class="a-mono">true</code>. A Razor editor built that way would narrow every descriptor it touched. So the editor mutates the stored JSON document and the pane renders it whole.',
    source: 'F5 acceptance criterion §6.3-3 — "everything clickable is exportable as code"',
    reversal: true,
  },
  {
    title: 'The simulator renders a predicate. It never scores a record.',
    why: '<code class="a-mono">ManagementPolicySimulation</code> takes no record id, deliberately: evaluating a predicate against a stored row needs a read, and a read through the Management API is the data surface D4 refuses to create. The first iteration picked a real record and answered allowed/refused per operation, computed in JavaScript. That is a second policy evaluator, and it fails acceptance criterion §6.3-4 <em>by construction</em> — the first time it disagreed with the engine over a null comparison or the tenant guard’s precedence, this screen would teach the wrong thing with total confidence. What it shows instead is the verdict: the four ways a caller gets 403 in the engine’s own order, the USING and WITH CHECK source, the synthesised tenant scope, the field masks, and what failing the read predicate actually looks like per operation.',
    alternative: 'Call the Data API under the simulated caller’s credential and compare. Rejected for F5: the operator holds their own credential and holds nobody else’s, so the honest version of that affordance is "open this row as yourself", which is the link the panel offers.',
    source: 'ManagementModels.cs · management-api.md §The record-id arm of the simulator',
    reversal: true,
  },
  {
    title: 'An operator carries one tenant, and there is no tenant switcher.',
    why: 'A signed-in operator had no tenant at all, so every <code class="a-mono">tenancy: scoped</code> entity answered 403 before any rule ran — two of the three entities in the example the product ships to demonstrate multi-tenancy. The fix is a tenant on the user’s own row, honoured exactly the way <code class="a-mono">TenantResolver</code> honours an API key’s: as a <em>confirmation</em>, never a choice. A request naming any other tenant still resolves to no caller.',
    alternative: 'A set of tenants and a picker. Rejected: that is cross-tenant capability, which <code class="a-mono">TenantResolver</code>’s own summary calls "a deliberate, audited grant, deferred to #42". Shipping the picker in F5 would be shipping the grant without the audit — the trade D4 refuses one layer up. An operator who must administer two tenants holds two accounts until #42.',
    source: 'AlvoIdentityContextResolver.cs · TenantResolver.cs · design §2.7',
    reversal: true,
  },
  {
    title: 'A tenant is a uuid, and no screen shows a tenant "name".',
    why: 'There is no tenant registry anywhere in <code class="a-mono">src/</code> — tenancy is resolved per request from a discriminator column. The first iteration drew named tenant chips, and named them after two of the sample customers, which is the one confusion a multi-tenant product most needs to avoid. Showing the uuid is less pretty and it is what exists.',
    reversal: true,
  },
  {
    title: 'There IS a New person control, and it produces a real row.',
    why: 'The first iteration was right that <code class="a-mono">IAlvoUserStore</code> cannot create anybody, and wrong about what follows. With <code class="a-mono">providers: ["local"]</code> nobody can sign in until an account exists and only the bootstrap admin is seeded — so a local-auth project can never have a second administrator, and "people arrive by signing in" describes a door nobody can reach. The design now widens the port with membership creation and keeps the credential half inside the implementation that has one.',
    alternative: 'Leave it out and say "only OIDC users can appear here". Honest, and it makes the example descriptor’s own project unusable.',
    source: 'IAlvoUserStore.cs · host.md §The bootstrap administrator · design §3.7',
    reversal: true,
  },
  {
    title: 'The first-run wizard signs you in; it does not create an account.',
    why: 'The bootstrap administrator already exists before this page can be reached: the deployment sets <code class="a-mono">Alvo__Admin__BootstrapEmail</code> and mounts a password file, a direct password value is refused outright, and seeding never resets an existing account. The old step one offered to change a "default password" the image does not ship. Step three is new and is the honest cost of §2.7: with multi-tenancy on, an operator with no tenant grant sees nothing scoped.',
    source: 'host.md §No default credential, §The bootstrap administrator',
    reversal: true,
  },
  {
    title: 'The field editor sits in the right column, above the descriptor it annotates.',
    why: 'It was a 560 px drawer over a 520 px aside, so "selecting a field marks the lines it owns in the descriptor" was true only after you closed the thing that made the claim — the two-pane idea defeated at the moment it mattered. Below 1100 px there is one column and it is a panel in it.',
    reversal: true,
  },
  {
    title: 'Every refusal, warning, route and facet is generated from the repository.',
    why: 'Nine values the first iteration called "verbatim" were not: refusal texts were truncated, six refusals were absent entirely, every management route lost its <code class="a-mono">/projects/{project}</code> segment, an access level existed that the frozen schema refuses, and the database engine was named in three places by an API that structurally cannot report one. <code class="a-mono">scripts/gen-prototype-fixtures</code> now writes all of it, <code class="a-mono">--check</code> fails on drift, and nothing in <code class="a-mono">generated/</code> is edited by hand.',
    source: 'scripts/gen-prototype-fixtures',
    reversal: true,
  },
  {
    title: 'The dashboard is a modelling tool first, so Schema sits above Data.',
    why: 'The reason to open it is to define what you keep track of — entities, fields, types, relationships and the rules that guard them. Browsing records proves the model works; it is not why the tool exists. The written design put Data first; §4.3 now records the reversal.',
  },
  {
    title: 'Every schema editor is a split: model on the left, the descriptor it produces on the right.',
    why: '"Everything clickable is exportable as code" is an acceptance criterion rather than a slogan. Showing the document being built turns it into something you watch happen, and makes the dry-run diff unsurprising instead of a second opinion.',
  },
  {
    title: 'The field type is eleven visible buttons in one grid, and the editor is eleven forms.',
    why: 'A select hides ten of eleven choices behind a click. And picking a type does not only set a value — the frozen schema’s nine <code class="a-mono">if/then</code> rules decide what the rest of the form may contain. Three types carry <em>required</em> facets, so those render as a block the form will not let you leave empty; seven carry none and render none, which is correct rather than unfinished.',
    source: 'schema/project.schema.json $defs/field',
  },
  {
    title: 'Access is split by how fast a change takes effect, not by which store holds it.',
    why: 'Assigning a role, or a tenant, happens in the identity store and counts on the next request. Declaring a role, or a level, changes the descriptor and waits for an apply. On one undifferentiated screen half the controls would lie about when they work, so the line is drawn once and labelled in words.',
  },
  {
    title: 'An assigned role the descriptor does not declare is shown as inert, not as an error.',
    why: '<code class="a-mono">AlvoIdentityContextResolver.Minted</code> drops it <em>silently</em> — nothing refuses it anywhere, so every rule naming it never matches and the failure has no symptom at all. That is the worst kind of quiet, so the row and the ladder both call it out and offer the two ways out: declare it, or remove it.',
  },
  {
    title: 'A roles × operations matrix is allowed; a teams × entities one is not.',
    why: 'The matrix the analysis warns about is teams against permissions, which needs typed claims <code class="a-mono">@user</code> does not have (#37) — a grid would let you draw a permission nothing enforces. Roles against the five operations is exactly what <code class="a-mono">entities.*.rules</code> holds, so the grid is a rendering of the engine rather than a promise beyond it.',
  },
  {
    title: 'A condition belongs to the way in, not to the column.',
    why: '<code class="a-mono">(who || who) && when</code> cannot say "dispatchers always, the assignee only while the job is open" — the commonest rule anyone writes — and silently locks out the roles it was not aimed at. Each way in carries its own tests. When every branch happens to carry the same test the CEL is emitted factored, because that is the same expression and reads better.',
  },
  {
    title: 'The builder reads an expression back, or admits it cannot.',
    why: 'It recognises exactly the shapes it can write. Anything else sets raw: the controls step aside, the text editor takes over, and the matrix cells for that operation go inert rather than rewriting the author’s expression. A builder that silently rewrites a hand-written rule is worse than no builder.',
  },
  {
    title: 'The raw CEL editor lists what is in scope, and checks what a client can check.',
    why: 'It used to list five of eighteen fields and call the list complete. Now it lists them all, and runs the subset of the compiler’s rules a client can run without being the compiler: <code class="a-mono">== null</code> is rejected, function calls other than <code class="a-mono">has()</code> do not exist, arithmetic is ✗ in the Rule profile, <code class="a-mono">@user</code> is closed, and a role literal <code class="a-mono">auth.roles</code> does not declare is refused at apply. It says the compiler is the authority.',
    source: 'cel.md — the profile table and deviation 10',
    reversal: true,
  },
  {
    title: 'The record form carries every writable field, including the hidden ones.',
    why: '<code class="a-mono">hidden</code> restricts reading and <code class="a-mono">readOnly</code> restricts writing, so a hidden field is writable by design — and a <em>required</em> hidden one must be on the form or its create is impossible. The first iteration excluded <code class="a-mono">internal_notes</code> by name and rendered a permanent "reference is already taken" error a thousand pixels from the field it concerned, on an untouched form. Errors are inline, at the field, after a real collision, and they carry the slug and the violation code the API actually uses.',
    source: 'data-api.md §A hidden field is writable, by design',
    reversal: true,
  },
  {
    title: 'A uuid gets a plain input, not a person picker.',
    why: 'A <code class="a-mono">uuid</code> has no target: the descriptor does not say what it points at, only a <code class="a-mono">ref</code> does. A "Choose a technician…" select over one is a control the descriptor cannot produce — the same defect as an Invite button, committed on the next screen along.',
    reversal: true,
  },
  {
    title: 'The ref picker starts collapsed, and says what it searches.',
    why: 'Three refs meant three 200 px lists open at once on an untouched form. And "searches the display field" named a concept the schema does not have — there is no <code class="a-mono">displayField</code>. It now picks the first required string field, falls back to the id, and says so at the control. An <code class="a-mono">x-</code> hint would settle it properly.',
    reversal: true,
  },
  {
    title: 'Every control is either wired or visibly inert.',
    why: 'Hooks Edit, Add a hook, Add index, Remove, Open reference, Download OpenAPI and the field filter were drawn live and had no handler. A control whose click has no output is the same defect class as a control for a refused feature. The ones that are real are wired; the two that need a running instance are <code class="a-mono">disabled</code> with the reason in their title.',
    reversal: true,
  },
  {
    title: 'The Data screen refuses a scoped entity honestly, and never calls it an empty page.',
    why: '403 "you carry no tenant" and 200 "your rules admit no row" look identical in a spinner and need opposite fixes. The tenant guard runs <em>before any rule is consulted</em>, so the screen says which of the two it is, every time.',
    source: 'data-api.md §The decision procedure, cause 2',
  },
  {
    title: 'A filter over a hidden field is refused exactly as one over a field that does not exist.',
    why: 'The first iteration’s most "honest" error state disclosed that <code class="a-mono">internal_notes</code> is hidden — which is the disclosure Position A exists to prevent: a hidden field’s name is not public on the read surface, and <code class="a-mono">QueryFieldResolver</code> returns one <code class="a-mono">null</code> for both cases. It is one answer, and the slug is <code class="a-mono">malformed-query</code>; there is no <code class="a-mono">unknown-field</code> slug in the catalogue.',
    source: 'data-api.md §Position A · AlvoProblemTypes.cs',
    reversal: true,
  },
  {
    title: 'Paging is stable, not depth-independent, and Previous is disabled with the reason.',
    why: '"Page 900 costs what page 1 costs" is the claim <code class="a-mono">data-api.md</code> says in so many words not to write anywhere. What keyset paging buys is correctness under concurrent writes. And the cursor is opaque and forward-only — the API takes <code class="a-mono">after</code>, never <code class="a-mono">before</code> — so Previous is client-side history, and this drawing keeps none.',
    source: 'data-api.md §The cost, stated honestly (#100)',
    reversal: true,
  },
  {
    title: 'Settings shows the data provider, and no engine.',
    why: '<code class="a-mono">ManagementInfo.DataProvider</code> is the registered <code class="a-mono">IAlvoData</code> implementation’s type name. The core may not reference the adapter that knows an engine’s name — that is the provider-model principle — so "PostgreSQL 16" had no source. It appeared in three places.',
    source: 'management-api.md §info reports the data provider, not the engine',
    reversal: true,
  },
  {
    title: 'There is no New key, no Revoke, and no Delete project.',
    why: '<code class="a-mono">IApiKeyStore</code> is <code class="a-mono">FindAsync</code> and <code class="a-mono">TouchAsync</code>; <code class="a-mono">ManageApiKeys</code> and <code class="a-mono">DeleteProject</code> are in the level table with no HTTP route at all. Each was a button whose only possible output was nothing. What the page says instead is what a key record carries, and that its <strong>roles</strong> — never its scopes — decide whether it reaches management.',
    source: 'IApiKeyStore.cs · management-api.md §The three admin operations with no route',
    reversal: true,
  },
  {
    title: 'The warned panel is intersected with what the descriptor declares.',
    why: '<code class="a-mono">CapabilityReport.Project()</code> projects all five unhonoured subsystems whether or not this descriptor declares any, so the panel listed five "declared, and not running yet" blocks for a descriptor that declares none. Intersected, it is usually empty — and the empty state says the thing that makes it matter: a runtime apply writes <strong>no warning line at all</strong>, so this panel is the only place a dashboard-first operator will ever be told.',
    source: 'UnhonouredSubsystems.DeclaredBy · UnhonouredSubsystems.cs remarks (#83)',
    reversal: true,
  },
  {
    title: 'The assistant is out of F5, and its shape is kept as a design.',
    why: 'An instance-level key does arrive the way a connection string does — but <code class="a-mono">baas-analyza</code> §2.8’s own criterion is "the key is in a secret store", and there is no secret store. It was also the largest drawn surface with nothing behind it, and every transcript it carried asserted something the repository refutes: a management route that does not exist, a rule the compiler rejects, and a role-per-region recommendation that is the role explosion the analysis warns about by name. The drawer’s shape — proposes a diff, exits through the same dry run, never applies — is good and returns gated on <code class="a-mono">GET /management/info</code> reporting an AI connection.',
    source: 'design §6.4',
    reversal: true,
  },
  {
    title: 'The model map draws the focused entity and one hop, past six entities.',
    why: 'Fixed-width boxes stacked per reference depth is a 3,000 px picture with wires crossing boxes at forty entities. One hop in each direction is the neighbourhood anybody is actually reading.',
  },
  {
    title: 'The entity bar becomes a typeahead past eight entities.',
    why: 'A flex row that does not wrap overflows horizontally, which is the 375 px acceptance criterion failing on a desktop.',
  },
  {
    title: 'Keyboard operation is wired, not asserted.',
    why: 'The palette opened with ⌘K, and then <code class="a-mono">document.activeElement</code> was <code class="a-mono">BODY</code> — <code class="a-mono">autofocus</code> does not fire on an element inserted through <code class="a-mono">innerHTML</code>. There were zero <code class="a-mono">.focus()</code> calls in the whole file. Now: the palette focuses and filters and answers Up/Down/Enter, every overlay is a <code class="a-mono">role="dialog"</code> with a focus trap and focus return, toggles are reachable and Space-operable, and <code class="a-mono">j</code>/<code class="a-mono">k</code>, <code class="a-mono">/</code> and <code class="a-mono">g</code>+letter work.',
    reversal: true,
  },
  {
    title: 'A screen that throws renders an error and leaves the shell standing.',
    why: 'The Access page went blank once because one function threw during a render that replaced the whole shell. The router now catches, renders the failure in place, and re-throws on a microtask so the console still shows it — a caught exception that hides itself is how the same bug happens twice.',
    reversal: true,
  },
  {
    title: 'The history compares any two revisions, and a restore appends rather than rewinds.',
    why: 'It compared everything against the head, so r3 ↔ r4 was impossible. And <code class="a-mono">RolledBackFrom</code> is <em>the revision this one restored</em>; the old screen printed <code class="a-mono">rolledBackFrom - 1</code> to make wrong data look right.',
    source: 'ManagementRevision',
    reversal: true,
  },
  {
    title: 'Preview carries a Reason, and has a 412 state.',
    why: '<code class="a-mono">ManagementApplyRequest</code> carries <code class="a-mono">Author</code> and <code class="a-mono">Reason</code>, and Configuration history is the only place either ever becomes visible — yet there was nowhere to type one. Two administrators colliding is day one in any team, so the 412 is drawn: distinct from 428, which means you sent no precondition at all.',
    source: 'management-api.md §If-Match is required, and 428 is why',
    reversal: true,
  },
  {
    title: 'Preview says, before Apply, when a working copy needs admin.',
    why: 'Only visible once the three queues became one: <code class="a-mono">access</code> is a key of the same document, and both write members re-resolve the <em>whole</em> apply to <code class="a-mono">admin</code> when that block differs. So a developer who edits one rule and one level is refused the entire apply, including the half that was within their level — and is owed that sentence at Preview rather than at a 403.',
    source: 'management-api.md §The one place a route’s level is not the whole answer',
  },
];

/** Findings from the two adversarial reviews that this iteration did not accept. */
export const REJECTED = [
  {
    finding: 'product §3',
    claim: 'Only the bootstrap admin exists, so the wizard should be "sign in" plus an optional password rotation.',
    why: 'Accepted for step 1 — but the review’s "keep step 2" understates it. Drawing the wizard honestly exposed a third step nobody had: with multi-tenancy on, an operator with no tenant grant lands on a Data screen that refuses two of three entities. The wizard now has three steps, and the third is the one the reviews did not ask for.',
  },
  {
    finding: 'product §12',
    claim: 'Offering <code class="a-mono">admin</code> in the assign modal is fine, because it is in <code class="a-mono">RoleCatalog</code>.',
    why: 'True about the catalogue and insufficient about the consequence. Granting the built-in administrator role in one click, with no confirmation, is the one assignment that can hand somebody a management level on their next request. It is offered, and it asks first. The review’s companion point — that <code class="a-mono">authenticated</code> must not be offered — is accepted in full.',
  },
  {
    finding: 'ux §6, last clause',
    claim: 'The simulator should default to a record that is not the caller’s own, because their own job makes every read "allowed" — the dull case.',
    why: 'Moot rather than wrong: there is no record picker any more (D3), so there is no default record to choose. The underlying instinct — do not open on the case that teaches nothing — is honoured differently: the panel opens on a technician and <code class="a-mono">list</code>, which is the pair that shows the RLS surprise.',
  },
  {
    finding: 'ux §10',
    claim: 'Five representations of one rule on one screen; show the CEL once.',
    why: 'Partly rejected. Four of the five had a job: the matrix is the overview, the sentence is the plain-language reading, the chips are the editor, and the CEL is the artefact that actually ships. What was genuinely duplicated was the readout beside a raw textarea showing the identical string, and that is gone. Collapsing further would mean choosing between "who may do this" and "what will be applied", and the whole argument for the descriptor pane is that a person is owed both.',
  },
  {
    finding: 'ux §16, partly',
    claim: 'Cut authorial copy from the product surface, including "It is checked inside the same transaction as the query, against that closed context and nothing else".',
    why: 'The tags (<code class="a-mono">design</code>, <code class="a-mono">honest</code>, <code class="a-mono">three consequences</code>) are gone, and so is the wizard’s aside about itself. The transaction sentence stays, reworded: <em>where</em> a rule is evaluated is the fact that explains why a technician gets a shorter list rather than an error, and it is the single most misunderstood thing about the API. "Closed context" is what was unclear, and that phrase is what changed.',
  },
  {
    finding: 'ux taste',
    claim: 'The field type column is low-contrast at 13 px.',
    why: 'Measured rather than judged: <code class="a-mono">--dim</code> on <code class="a-mono">--panel</code> is <strong>5.81 : 1</strong> in light and <strong>6.26 : 1</strong> in dark, and the type column sets <code class="a-mono">--text-xs</code> (12 px). WCAG AA asks 4.5 : 1 for normal text, so it passes with margin in both themes — and it is deliberately quieter than the field name, which is the thing being scanned. The suite now measures every foreground token against every surface rather than leaving this to an eye, which is what acceptance criterion §6.3-5 asks for.',
  },
  {
    finding: 'product §10, last item',
    claim: 'The sample curl sorts by a nullable column and so teaches the slow path.',
    why: 'Accepted, and generalised past the fix the review proposed. The sample now derives its sort column from the descriptor — the first <em>required</em> sortable field — so it stays correct for an entity this drawing has never seen, rather than being right once for <code class="a-mono">work_orders</code>.',
  },
];

export const OPEN_QUESTIONS = [
  'Every sentence here is a hardcoded English string builder, pluralisation included. Fine for a drawing; the Razor version must not inherit the pattern, and nothing yet says how it will not (baas-analyza §2.8 asks for i18n-ready).',
  'A tenant has no name anywhere in the product. Every screen shows a uuid. Is a name worth a descriptor block, an <code class="a-mono">x-</code> hint, or nothing at all?',
  'Reordering fields changes the descriptor and changes nothing in the database. Worth a control, or noise? The drag handle is drawn and inert.',
  'A <code class="a-mono">json</code> field on the record form is a textarea that accepts invalid JSON until submit. Worth a real editor, or is the 422 enough?',
  'Bulk actions cover the rows you selected, because the batch endpoint takes ids. Is a delete-by-filter endpoint worth having, given what it would be able to destroy in one request?',
  'Role and tenant changes are not audited — #42 is F7. The screen says so plainly and changes them anyway. Is that the right trade, or should the controls wait?',
  'The set-password token is shown once for an administrator to hand over. Is out-of-band delivery acceptable for v0.1, or does identity need a mail transport before this ships?',
  'Should a <code class="a-mono">viewer</code>-level person see this dashboard at all, or only the API? The level exists and nothing has decided what sign-in does with it.',
  'A hook editor exists here in the shape the automation builder will need. Does it land in F5, or wait so the two are designed together?',
];

export const COMPONENTS = [
  ['a-split / a-json', 'Model on the left, the working copy on the right, with a gutter mark per changed line.', 'new'],
  ['a-fieldrow', 'One field: name, type, flag strip, changed dot.', 'new'],
  ['a-form', 'Form scale — one step up from the grid scale. 13 px label, 15 px control.', 'new'],
  ['a-typegrid / a-typechip', 'The eleven field types as one uniform grid, with the selected one explained beneath.', 'new'],
  ['a-entitybar', 'Switch entity without leaving the editor; a typeahead past eight.', 'new'],
  ['a-rel', 'A relationship, shown as the ref field it actually is.', 'new'],
  ['a-map', 'The model drawn: entity boxes, an arrow from each ref field, one hop from the focus.', 'new'],
  ['a-matrix / a-cell', 'Roles down, operations across, a cell you tick. Built-ins apart, anon marked, a via-the-record marker, a condition count.', 'new'],
  ['a-branch / a-nulltrap', 'One way in and the tests that narrow it, plus the null-comparison warning.', 'new'],
  ['a-band', 'The line between what takes effect at once and what waits for an apply.', 'new'],
  ['a-ladder / a-rung / a-can', 'One person read downward through identity, roles, tenant, dashboard level and data.', 'new'],
  ['a-perm / a-perm__editor', 'The five rules as sentences, with the full editor inside the row.', 'new'],
  ['a-sim / a-verdict', 'The policy verdict: the cause, the predicates, and what failing one looks like.', 'new'],
  ['a-presets / a-readout', 'Multi-select chips, and the CEL the controls produced.', 'new'],
  ['a-picker / a-subgrid', 'Choosing a referenced record, and listing the records that point back.', 'new'],
  ['a-hook', 'A lifecycle hook, before or after the commit.', 'new'],
  ['a-disclose', 'A refused facet, one click away and inert.', 'new'],
  ['a-pending', 'The one unapplied-changes bar. Sticky, never a toast, primary action always Preview.', 'new'],
  ['a-wizard / a-steps / a-reveal', 'First run, and a token shown once.', 'new'],
  ['a-shell / a-sidebar / a-header / a-nav', 'Chrome.', 'exists'],
  ['a-grid / a-row-card / a-bulkbar / a-toolbar', 'The data grid and its 375 px substitute.', 'exists'],
  ['a-diff', 'The preview diff, the revision compare, and the later AI proposal. Three consumers, one component.', 'exists'],
  ['a-notyet / a-notyet-panel / a-refused', 'The two classes of "not yet".', 'exists'],
  ['a-error / a-empty / a-skeleton', 'Feedback. Errors are inline and stay; they are never toasts.', 'exists'],
  ['a-palette / a-modal / a-drawer / a-confirm', 'Overlays, each a role="dialog" with a focus trap.', 'exists'],
];
