# F5 — the schema assistant gets a change right on the first try, and says it only proposed it

> Issue #289. Builds on `docs/superpowers/specs/2026-09-28-f5-assistant-reliability-design.md` (adopted, built):
> its tool surface, budget, instructions and eval are the base, and nothing here changes its security argument —
> **no tool writes**, the dry run runs with the literals `DryRun: true, AllowDestructive: false`, the operator applies
> from Preview. Deviations continue that design's numbering (D15 onward), so the two tables never share a number.

## 1. The transcript, and what it actually proves

Asked to "at least make the schema" for a customer audit entity, the demo build's assistant proposed `CustomersAudit`
(refused: `propertyNames` pattern `^[a-z][a-z0-9_]{0,62}$`), then, after the operator pasted the refusal back,
`customers_audit` **with a declared `id`** (refused: *"Field 'id' is a framework-managed column and cannot be
declared"*), and only the third attempt passed. Its reply said the entity **"is created"** and was partly Czech
("Pojďme") in a Slovak conversation. The operator relayed each refusal by hand.

**Both refusals were predictable from what the repository already knows.** The name rule is the schema's own
pattern, and the managed columns are `AlvoManagedColumns.For(tenancy, audit, softDelete)`. Neither appears in the
instructions today, so the model filled the gap from its training prior: PascalCase table names and an explicit `id`.

## 2. What the built reliability work already covers (evidence), and what remains

| Ask in #289 | Status | Evidence |
|---|---|---|
| Retry on its own before handing a refusal to the operator | **Covered** | `ManagementTools.MaximumRefusedAttempts = 3` (`src/MMLib.Alvo.Ai/Internal/ManagementTools.cs:48`), the 4th attempt answers `BudgetSpent` (`:191`); loop cap `AlvoAssistant.MaximumIterations = 12` (`AlvoAssistant.cs:285`); instructions §5 *Reading a refusal* and §7 *"On a refusal: … apply the fix at the `pointer`, and retry"* |
| A refusal the model can act on | **Covered** for `id`, **weak** for the name | `id`: `ManagedColumnNames.Refusing` gives the fix *"Remove 'id' from the entity's fields; the store assigns it."* Name: `DescriptorValidator.FixSuggestionFor` gives only the generic *"Schema keyword 'pattern' failed … see schema/project.schema.json there."* (D19) |
| Operator relay | **Covered** | One turn holds all three attempts; the drawer's refused card is filed only when no attempt in the turn was valid (`ManagementTools.FileProposal`) |
| Right on the **first** try | **Open** | `schema-assistant.md` states neither the name pattern nor a managed column (`grep -n "a-z0-9\|tenant_id\|created_at"` finds nothing) |
| "Proposed", never "created/applied" | **Open, and taught the wrong way** | No rule; worse, the worked examples' replies are written as done facts (*"Bikes get an optional `notes` text field"*, *"Customers get `full_name`, which the database now maintains"*) — the section models copy most |
| Reply in the operator's language | **Rule present, ungraded** | §7 *"Answer in the operator's language"*; the eval asks every case in `en` and `sk` (`EvalOptions._allLanguages`) but no grader reads the reply's language |
| Capability statements only from `get_capabilities` | **Partly** | §2 *"You cannot"* says to quote `get_capabilities` for `automation`/`functions`; nothing forbids paraphrasing what a hook action does |
| Eval cases for the transcript | **Open** | `EvalCases.All` has the seven §4.2 cases; none creates an entity, none forces a refusal the instructions cannot prevent |
| Measured against a real model | **Open (the maintainer's step, D8)** | `docs/assistant-evals.md` does not exist: `scripts/eval-assistant` has never run |

**Verdict:** the in-turn retry the maintainer asked for is built; what the transcript shows failing is knowledge (the
name rule, the managed columns), wording (proposed vs done), and language — none of which the eval grades yet.

## 3. Scope

1. **Instructions §3 states the name rule and the managed columns**, drift-tested so they cannot go stale:
   - the pattern, quoted verbatim, equals both `properties.entities.propertyNames.pattern` and
     `$defs.entity.properties.fields.propertyNames.pattern` in `schema/project.schema.json`;
   - the reserved names: entity `users`; fields exactly `ReservedQueryKeys.All` (Host.Tests, which has the core's grant);
   - the managed columns, one line per trait, equal to `AlvoManagedColumns.For` per trait in both directions, and
     their union equal to `For(Scoped, audit: true, softDelete: true)`;
   - every stated refusal is one the real validator gives on `bike-workshop` (probes, as the Computed quotes are),
     and the trait scoping is proven the other way: `created_at` on the non-audited `order_lines` is **accepted**.
2. **"Proposed, never done"** — a §7 rule, the worked examples' replies rewritten to it, and a grader
   (`ProposalWording`) that every example reply must itself pass.
3. **Language** — §7 says Slovak is not Czech; a grader (`ReplyLanguage`) judges the model's own prose.
4. **Two new eval cases** — `audit_entity` (the transcript, graded on the first attempt) and
   `stale_revision_recovered` (a refusal forced by the world and recovered inside the turn).
5. **The drawer is not changed** (D18).

Out of scope: the audit mechanism itself (#288); the generic `pattern` fix text (D19).

## 4. Decisions

- **Drift-tested, not generated.** Generating the text would put `project.schema.json` into the `MMLib.Alvo.Ai`
  runtime, whose only reference is Abstractions (`BoundaryArchitectureTests`), and would make the instructions a
  template rather than a reviewable document. The field-type list already follows the drift-test pattern; this
  follows it again. The instructions' version line moves to `v3`.
- **Two graders apply to every turn, not only the new cases.** `EvalRunner.Graded` checks the case, then
  `ProposalWording` (never claims done; says *proposed* when a valid proposal was filed) and `ReplyLanguage` (own
  prose in the asked language) — the transcript's wording and language failures can happen on any request (D21).
- **Graders read the model's own prose** (`ReplyText.OwnProse`: quote lines and repeated framework text removed;
  code spans removed for language), for the reason the drop-street grader already records: the framework's English
  refusal is quoted verbatim by design.
- **The forced refusal is a concurrent apply** (D15): after the turn's first `get_descriptor`, the eval applies an
  unrelated real edit (`/entities/bikes/description`), so the model's first `propose_change` is refused with
  `stale-revision` and must re-read and re-base in the same turn.
- **The schema's `fields` description is corrected.** It says *"The `id` field (uuid, PK) is added automatically when
  not defined"* — an invitation to define it, which the validator refuses. Prose only; no validation keyword moves.

## 5. Acceptance criteria

1. `schema-assistant.md` (first line `<!-- alvo-schema-assistant v3 -->`) states the name pattern, the reserved names
   and the managed columns per trait; changing any of the three sources fails a ring-1 test.
2. Every name the instructions call refused is refused by the real validator (Host.Tests), and `created_at` on
   `order_lines` is accepted.
3. No worked-example reply matches `ProposalWording.ClaimsDone`; every reply of a valid example passes
   `SaysProposed`.
4. `ProposalWording` and `ReplyLanguage` are unit-tested with the transcript's own failures: *"je vytvorená"* and
   *"is created"* claim done; *"Pojďme"* is Czech; an English reply to a Slovak question fails; a Slovak reply after an
   English quote block passes.
5. `EvalCases.All` has nine cases. `audit_entity` passes only with 0 refused attempts, exactly one `propose_change`,
   exactly one changed path that is a new `/entities/<snake_case>` declaring no managed column.
   `stale_revision_recovered` passes only when a `stale-revision` violation was answered, the turn ended valid in ≤ 2
   `propose_change` calls, and the proposal carries both the other operator's edit and the nickname field.
6. The bar is §4.2's, unchanged: each case ≥ 2/3 runs per language, suite ≥ 90 %. The real-model numbers are the
   maintainer's run (D8 still holds).
7. No public symbol; `MMLib.Alvo.Admin` untouched; ring2 and `dotnet build MMLib.Alvo.slnx -c Release -warnaserror`
   green.

## 6. Deviations

| # | Deviation | Reason |
|---|---|---|
| D15 | The forced-refusal case is forced by **the world** (a concurrent apply → `stale-revision`), not by the prompt | Every refusal a prompt can reliably elicit is one scope item 1 exists to prevent; a prompt-forced case would grade the instructions' failure as its precondition and turn flaky exactly as they improve. A concurrent edit is refused whatever the model knows, is a real production path, and exercises the same in-turn loop (read the violation, re-read, retry) |
| D16 | `audit_entity` grades the **schema only** (one new entity, nothing else changed), not an audit that fills itself | The mechanism is #288, undecided. Grading "exactly one changed path" also fails a proposal that adds an unhonoured `entity.update` after-hook — the capability confusion #289 names — without grading prose. "Declares no managed column" is graded although validity implies it, so a validator regression still fails the case |
| D17 | Language is judged by a deterministic stop-word count plus Czech-only markers (`ě ř ů`, `se pro jsem jsou …`), not a language-identification library | No dependency for an eval grader; the one hard distinction (Slovak vs Czech) is exactly the letters Slovak lacks; `unknown` (a reply with no own prose) fails, since the rule requires an explanation in the operator's language |
| D18 | The admin drawer is **not** changed, though #289 asks to consider it | It never offered "paste the refusal back": the refused card (`AssistantDrawer.razor:71-79`) lists the refusals under *"Nothing has been applied"* with no control; the relay was the old model stopping after one refusal. The card now appears only after the turn's own attempts, and the next turn carries the refusal through the reply the model is told to quote verbatim (`_turns` holds text only). Saying "after N attempts" would need an attempt count on the public `AssistantUpdate.Proposal` — an Abstractions change not earned by a card label |
| D19 | The generic `pattern` fix (`DescriptorValidator.FixSuggestionFor`) is not improved | A core wording change with its own tests; with the rule in the instructions the first attempt should not reach it. Owed if the first real run shows name refusals |
| D20 | The worked examples' replies are rewritten | They modelled the done-tense the transcript shows; left alone, they would contradict the new rule with the authority of an example |
| D21 | The two behaviour graders tighten every existing case | A turn that proposed the right change and said "Done." in Czech is a failure the operator saw; the pass bar is unchanged, what counts as a pass is stricter |
