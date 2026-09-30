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
   (`ProposalWording`) that every example reply must itself pass. §7 also states two rules the table above marks
   covered or partly covered, so the model reads them as rules rather than infers them from the tools:
   - **retry yourself** — folded into the existing refusal bullet: every refusal is in the tool's answer, so fix and
     retry in the same turn, never ask the operator to paste a refusal back or to say "try again";
   - **capabilities from `get_capabilities` only** — what this build cannot do is quoted from `get_capabilities` and
     §2, and a hook or hook action is never described from memory.
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
   `propose_change` calls, and the proposal carries both the other operator's edit and the nickname field. The
   grader is stricter than that sentence, deliberately: the changed paths are *exactly* those two, and `nickname` is
   left optional, as asked — a proposal that also touches something else, or makes the field required, fails.
6. The bar is §4.2's, unchanged: each case ≥ 2/3 runs per language, suite ≥ 90 %. The real-model numbers are the
   maintainer's run (D8 still holds).
7. No public symbol; `MMLib.Alvo.Admin` untouched; ring2 and `dotnet build MMLib.Alvo.slnx -c Release -warnaserror`
   green.

## 6. Deviations

| # | Deviation | Reason |
|---|---|---|
| D15 | The forced-refusal case is forced by **the world** (a concurrent apply → `stale-revision`), not by the prompt | Every refusal a prompt can reliably elicit is one scope item 1 exists to prevent; a prompt-forced case would grade the instructions' failure as its precondition and turn flaky exactly as they improve. A concurrent edit is refused whatever the model knows, is a real production path, and exercises the same in-turn loop (read the violation, re-read, retry). The edit lands after the turn's first `get_descriptor`; a model that proposes without reading the descriptor is never interfered with, so its turn grades `forced=False` and fails — the case reports the force did not land rather than passing a turn that recovered from nothing |
| D16 | `audit_entity` grades the **schema only** (one new entity, nothing else changed), not an audit that fills itself | The mechanism is #288, undecided. Grading "exactly one changed path" also fails a proposal that adds an unhonoured `entity.update` after-hook — the capability confusion #289 names — without grading prose. "Declares no managed column" is graded although validity implies it, so a validator regression still fails the case |
| D17 | Language is judged by a deterministic stop-word count plus Czech-only markers (`ě ř ů`, `se pro jsem jsou …`), not a language-identification library | No dependency for an eval grader; the one hard distinction (Slovak vs Czech) is exactly the letters Slovak lacks; `unknown` (a reply with no own prose) fails, since the rule requires an explanation in the operator's language |
| D18 | The admin drawer is **not** changed, though #289 asks to consider it | It never offered "paste the refusal back": the refused card (`AssistantDrawer.razor:71-79`) lists the refusals under *"Nothing has been applied"* with no control; the relay was the old model stopping after one refusal. The card now appears only after the turn's own attempts, and the next turn carries the refusal through the reply the model is told to quote verbatim (`_turns` holds text only). Saying "after N attempts" would need an attempt count on the public `AssistantUpdate.Proposal` — an Abstractions change not earned by a card label |
| D19 | The generic `pattern` fix (`DescriptorValidator.FixSuggestionFor`) is not improved | A core wording change with its own tests; with the rule in the instructions the first attempt should not reach it. Owed if the first real run shows name refusals |
| D20 | The worked examples' replies are rewritten | They modelled the done-tense the transcript shows; left alone, they would contradict the new rule with the authority of an example |
| D21 | The two behaviour graders tighten every existing case | A turn that proposed the right change and said "Done." in Czech is a failure the operator saw; the pass bar is unchanged, what counts as a pass is stricter |

## 7. Skills (scope extension, 2026-09-29)

The maintainer extended #289 in this PR: *"tooling, skills — done here"*, no fine-tuning. §§1–6 fix what the
transcript got wrong about names, wording and language. This section gives the assistant the rest of the descriptor —
rules, hooks, rollups, indexes, access, capabilities — **on demand**, as [Agent Skills](https://agentskills.io/specification),
without growing the always-in-context prompt.

### 7.1 Prior art, verified against the pinned package

- **The standard** (agentskills.io; adopted by Claude Code, Codex, Copilot, Cursor, Gemini CLI and others): a skill is
  a directory `name/` holding `SKILL.md` — YAML frontmatter `name` (≤ 64 chars of `a-z0-9-`, no leading, trailing or
  doubled hyphen, equal to the directory name) and `description` (1–1024 chars: what it does and when to use it) —
  plus optional `references/`. Three levels of progressive disclosure: metadata (~100 tokens per skill, always
  present), the body (< 5000 tokens and < 500 lines recommended, loaded on activation), resources (loaded on need).
- **The framework**: `Microsoft.Agents.AI` **1.22.0** (pinned in `Directory.Packages.props`) ships the standard as
  `AgentSkillsProvider : AIContextProvider`, attached through `ChatClientAgentOptions.AIContextProviders`. It appends
  the skill list to the instructions (`AgentSkillsProviderOptions.SkillsInstructionPrompt`, which must contain
  `{skills}`) and adds the function tools `load_skill(skillName)` and `read_skill_resource(skillName, resourceName)`
  (`AgentSkillsProvider.LoadSkillToolName`, `.ReadSkillResourceToolName`). Code-defined skills are `AgentInlineSkill`
  (`new AgentInlineSkill(AgentSkillFrontmatter, instructions)`, `.AddResource(name, value, description)`); the
  frontmatter constructor validates name and description and throws. None of these types is `[Experimental]`. It works
  on every connection kind Alvo dials, because it needs nothing beyond OpenAI-style function calling.
- **Measured, and different from the documentation**:
  - **`run_skill_script` is always advertised.** The XML docs say *"when scripts exist"*, but `BuildTools` in 1.22.0
    adds it unconditionally. Its default is approval-required (`ApprovalRequiredAIFunction`). → D28.
  - **All three tools require approval by default.** A default provider would end the turn on a
    `ToolApprovalRequestContent` (M.E.AI 10.10). → D28.
  - **The iteration cap holds.** The provider's tools run through the agent's own `FunctionInvokingChatClient`, so
    `Constrain`'s cap bounds a model that loops on `load_skill`. A probe with a cap of 3 made 4 requests.
  - **Unknown names never throw.** An unknown skill or resource answers `Error: Skill 'x' not found.` or
    `Error: Resource 'x' not found in skill 'y'.` Inline resources are looked up by exact name, so there is no path
    traversal.

### 7.2 Decisions and deviations

| # | Decision | Reason / deviation |
|---|---|---|
| D22 | Use the framework's `AgentSkillsProvider` and its tool names `load_skill` / `read_skill_resource`. Do not hand-roll a `read_skill` | It is shipped and GA in the pinned version: no new dependency, and names models recognise. Inventing a variant of a shipped standard is the defect CLAUDE.md names. The brief's `read_skill(name)` is dropped on purpose |
| D23 | Skills are **embedded resources** of `MMLib.Alvo.Ai`, loaded by an internal `EmbeddedSkills` into `AgentInlineSkill`s and served by the `AgentSkillsProvider(IEnumerable<AgentSkill>, AgentSkillsProviderOptions)` constructor. `AgentFileSkillsSource` is not used | #29 §5's reason for the instructions: a deployment must not be able to edit what the assistant believes, and a file source needs a directory the deployment can write |
| D24 | Each skill follows the standard with no variant. Frontmatter holds only `name` and `description` (one line each, no quotes, so a 20-line parser suffices). The body is Markdown | The same directories serve Claude Code in this repository (D33). A frontmatter key only one consumer understands is where a variant starts |
| D25 | Facts that have a source of truth sit in **marked regions**, `<!-- gen:<id> -->…<!-- /gen:<id> -->`. They are **drift-tested, not generated**. A failing test prints the expected region | Spec §4 of this design: drift-tested, not generated. The test never rewrites the file, because a self-accepting test would bypass the snapshot-judge gate |
| D26 | **Schema slices are not copies.** A body cites `schema/project.schema.json#<JSON Pointer>`. Claude Code reads that repo path. The loader registers each cited pointer as a resource **of that exact name**, whose value is the slice of the embedded `schema/project.schema.json` (linked into the Ai csproj, not copied) | Zero copies means zero drift. A linked file is not an assembly reference, so `BoundaryArchitectureTests` holds. The reference is repo-relative, not skill-relative as agentskills.io prefers: that is the one path both consumers resolve, and it avoids a second copy inside the skill directory |
| D27 | Capability texts are **not copied**. `alvo-descriptor-capabilities-and-limits` names only the honoured blocks (`CapabilityReport.Honoured`), the warned blocks (`UnhonouredSubsystems.All`, `WithinBlocks` and `ReportedOnly`) and the refused action types (`UnhonouredFeatures.EveryActionType`), all drift-tested. For everything else it says to call `get_capabilities` and quote it | `get_capabilities` returns every refusal on every call. A second copy would be a second truth. This departs from the design draft: the draft listed every refused slot, which is dozens of names, and those names would spend the size cap on a list the tool already returns |
| D28 | Approval is off for the two read tools (`DisableLoadSkillApproval`, `DisableReadSkillResourceApproval`). **`run_skill_script` is removed** by an internal `ReadOnlySkillsProvider : AIContextProvider`, which overrides `InvokingCoreAsync`, delegates to the inner provider's `InvokingAsync`, and filters `AgentSkillsProvider.RunSkillScriptToolName` out of `AIContext.Tools`. No skill has scripts | Both read tools are read-only over fixed, secret-free text, and an approval request is something the drawer cannot render. The draft assumed the framework omits the script tool when no script exists; 1.22.0 does not (§7.1), and leaving it advertised invites an approval halt. The wrapper is the framework's own extension point, not a fork |
| D29 | §4 *What Computed allows* moves into `alvo-descriptor-computed-and-rollups`, with its drift tests. §3 (names, managed columns, types) **stays** in the base prompt | Every proposal touches §3, and §3's always-in-context facts are the first-try fix of §3 scope item 1. Computed is one area |
| D30 | The model sees two more tools. This departs from the reliability plan's Global Constraint "tool set unchanged" | Both are read-only. The iteration cap (12) and the refusal budget (3) are unchanged |
| D31 | A **skill-read grader** (`SkillsRead`) runs on every turn, as D21 does. A proposal that touches an area fails when that area's skill was not loaded before the first dry run touching it | Otherwise "knows Alvo" is ungraded. This makes existing cases stricter; the pass bar itself is unchanged |
| D32 | Size limits are test-enforced (§7.4 AC 2) | They keep the always-in-context budget flat and a loaded skill well under the standard's 5k tokens |
| D33 | **One source, two consumers** (maintainer ruling, 2026-09-29): the descriptor skills live in `.claude/skills/alvo-descriptor-<area>/SKILL.md`, where Claude Code discovers them for work in this repository. `MMLib.Alvo.Ai` embeds exactly those directories through an `EmbeddedResource` glob, never a copy. A build that finds none fails. The Docker context carries them. Bodies are tool-neutral where the two consumers differ: the rule first, then one line *"In the dashboard: `check_change` / `propose_change` / `get_capabilities`. In this repo: edit the descriptor file, then the validator via `dotnet test` or the Management API."* Each `description` says when to use the skill, so it triggers in Claude Code and reads well in the agent's list | A skill that teaches the assistant a rule is the same text a developer's agent needs, and two copies would drift. The `alvo-descriptor-` prefix keeps them apart from the dev-discipline `alvo-*` skills. It supersedes the draft's `src/MMLib.Alvo.Ai/Skills/` location |
| D34 | The eval's per-turn ≤ 6 tool-call invariant counts **management tools only**. Skill reads (`load_skill` and `read_skill_resource`) are bounded separately at ≤ 4 per turn | The ≤ 6 bar predates skills. Without the split, a turn that loads two skills, reads one resource and proposes after one refusal fails the invariant for doing what the instructions ask. A separate bar still catches a model that browses the catalogue |
| D35 | The CEL-profile regions are drift-tested by **probing `CelCompiler.Compile(source, profile, entity)`** (Host.Tests), not by reading `CelTypeChecker._allowedProfiles` | The table and its key enum `CelConstructKind` are `private`. A probe of each stated example against its profile is the drift test the Computed quotes already use (`InstructionExampleOutcomeTests`), and it needs no core change |
| D36 | The eval cases (§7.5) are retargeted from the draft to what `bike-workshop` does not already contain, and to what this build truly refuses | `service_orders` already sets `completed_at` in a `mutate` hook. `customers.bikes_count` already exists. `bikes.frame_number` is already unique. **Webhook and email after-hook actions are delivered** (`rentals` posts to `rental-desk`), so the draft's `webhook_on_finish` and `can_alvo_email` would have graded a correct answer as a failure, the D6 trap. The refused action types are `function`, `http.call` and `entity.update` |
| D37 | **Amends D23: the skills are an internal `DescriptorSkill : AgentSkill`, not `AgentInlineSkill`.** It emits the same envelope (`<name>`, `<description>`, `<instructions>`, `<available_resources>`) with the body **free of XML escapes** in the tool result and its HTML comments (the `gen:` and example markers) removed (citations are collected from that stripped body; only the resource list's attributes are escaped), and serves each schema slice through its own `DescriptorSkillResource : AgentSkillResource`. It emits no `<available_scripts>` block | Measured in 1.22.0: `AgentInlineSkillContentBuilder` XML-escapes the body, so `'admin' in @user.roles` reaches the model as `&apos;admin&apos; in @user.roles` and every JSON example as `&quot;`-ridden text. A model that must reproduce CEL and JSON from entity-escaped text writes the entities back — the `ToolJson` relaxed-escaping precedent, in another encoding. The transport still JSON-encodes the string result (M.E.AI's relaxed encoder: `\"` and `\n`, never `\u0027`), as it does for every string tool result; what D37 removes is the second, XML layer on top. The builder is internal, so the envelope is re-stated rather than reused; the provider, its tool names and its lookup are still the framework's (D22) |

### 7.3 The skill set

These are nine skills, each `.claude/skills/alvo-descriptor-<area>/SKILL.md`. **G** marks a drift-tested region.

| Area | What it teaches | G region → source (test home) |
|---|---|---|
| `entities-and-fields` | pointer paths; rename by `renamedFrom`; removal is destructive, so the operator applies it; `default` is a literal of the field's type (a `$cel` default is refused, D38); `validation` is refused; `required` + `readOnly` needs a literal `default` | entity keys, field keys ← schema (Ai.Tests); reserved fields ← `ReservedQueryKeys.All` (Host.Tests) |
| `field-types-and-formats` | facets per type; `precision` counts all digits; `text` vs `string`; `onDelete` and the automatic index hold on a `ref` to a declared entity, and a `ref` to `users` has neither (D38) | type enum, `onDelete` enum, built-in formats ← schema (Ai.Tests) |
| `traits-and-tenancy` | project × entity tenancy; a scoped `POST` create echoes `tenant_id`; `softDelete` is refused at apply as a whole (D38) | managed columns per trait ← `AlvoManagedColumns` (Ai.Tests, via `InstructionClaims.ManagedColumns`) |
| `rules-and-cel` | a missing operation denies; `USING` / `WITH CHECK` per operation; role literals are checked at apply | Rule profile: allowed and refused examples ← `CelCompiler` probes (Host.Tests, D35) |
| `hooks` | `reject` / `mutate`; `old.` / `new.`; `changed()`; runs in the transaction with no network | Condition and Mutate profiles ← probes; Mutate functions ← `CelCall.LowerAscii` / `CelCall.Now` (Host.Tests) |
| `computed-and-rollups` | the ladder computed → rollup → hook → action → csx; today's §4; rollup `from` / `op` | Computed profile ← probes (Host.Tests); rollup `op` enum ← schema (Ai.Tests) |
| `indexes` | automatic indexes (PK, unique, ref) versus explicit `indexes`; composite `unique` | — (the schema slice) |
| `project-access` | Access levels are CEL over `@user` only; only an administrator changes `access` | Access profile ← probes (Host.Tests) |
| `capabilities-and-limits` | `get_capabilities` is the authority; never propose an unhonoured block; offer the lowest honoured rung; no dates; no data rows | honoured, warned and refused-action names ← `CapabilityReport` / `UnhonouredSubsystems` / `UnhonouredFeatures` (Host.Tests, D27) |

Worked examples in a skill use the base prompt's `<!-- example: name -->` + two-`json`-fence format, so
`InstructionExamples.Parse` and the Host.Tests outcome check run them unchanged.

**D38 (2026-09-29, Task 6): the table above is corrected against this build, not the draft.**

- A `$cel` field `default` is refused (`UnhonouredFeatures.OnAField`, #113). Only a literal of the field's own type,
  within its facets, is honoured.
- `softDelete: true` is refused at apply by itself (`UnhonouredFeatures.OnAnEntity`), not only beside a `default`.
- `onDelete` and the automatic foreign-key index exist only on a `ref` to a declared entity (`ConfigureReferences`).
  A `ref` to `users` is a bare id.
- The worked examples are retargeted to what `bike-workshop` lacks. `audit-rental-fleet` replaces `audit-rentals`,
  since `rentals` is already audited. `index-technicians-by-specialization` replaces `index-technician-status`,
  since `service_orders` already has `[technician_id, status]` (Task 5 B5).
- A prose claim that a skill makes about a refusal or acceptance is probed in `SkillClaimTests` (Host.Tests), beside
  the region drift tests. The probes cover:
  - `required` + `readOnly` with `computed`, without it, and with a literal default;
  - a default of another type, and a `$cel` default;
  - `hidden` over a row field, and over `@user`;
  - a facet on another type;
  - a declared `id`, and a declared `created_at` on an audited entity;
  - an entity named `users`;
  - a `ref` to `users` with `index`;
  - the hook phases: `new.` in a `beforeDelete` (refused), `old.` in a `beforeDelete` (accepted), and `old.` and
    `changed()` in a `beforeCreate` (refused);
  - a `mutate` that reads `@user` (refused), and one that reads `old.` (accepted);
  - the three refused after-hook action types, each in a shape the schema accepts. Each is asserted refused **by its
    own unhonoured-action consequence** (`UnhonouredFeatures.UnhonouredAction(type)`), not merely refused.

  `SkillCoreClaimsTests` also runs every allowed `cel-computed` example through the real dry run as a computed
  field, because the compiler alone is not the whole Computed authority.

  The worked examples that claim `softDelete` and `validation` are refused are run by `SkillExampleOutcomeTests`.

**D39 (2026-09-29, Task 7): the core-sourced skills and v4.**

- The rules example is `technician-updates-own-profile`: `technicians.rules.update` gains `user_id == @user.id`
  (ruling B6). It is not the eval's `own_orders_only`.
- **When to load a skill is stated once, in the base prompt's `### Skills`** (ruling H3). The catalogue frame
  (`EmbeddedSkills.Catalogue`) is only `## Skill list` and the list.
- Descriptions are capped at 200 characters, not AC 2's 300, and carry no quote or apostrophe (H3).
- `computed-and-rollups`, `project-access` and `capabilities-and-limits` carry no worked example:
  - the computed examples stay in the base prompt (§5), so the fence count stays exact in both;
  - an access change needs an administrator;
  - the limits are answered, not proposed.
- A refused CEL example must compile under another profile (ruling M3), so each refusal is the profile's rather than
  a typo's (`SkillCoreClaimsTests`).
- §3 of the base prompt no longer offers the `softDelete` trait or a `$cel` default, both of which are refused
  (D38).
- The computed section moves verbatim, except that "as in example (g)" becomes "as `alvo-descriptor-hooks` shows",
  because the skill is read without the base prompt.
- The always-in-context text (the base prompt plus the list) measures about 21.4 KB of AC 3's 22,758 bytes, and a
  test holds it there.

**D40 (2026-09-29, Task 8): `SkillsRead` is stricter than D31's wording.**

- **What it measures:** a proposal's needed skill must be loaded in a round before the **first dry run of any kind**,
  not before the first dry run touching that area (ruling H4). A turn's dry runs are attempts at one proposal. A
  model that is refused and only then loads the skill has spent an attempt on what the skill says.
- **Which skills are needed:**
  - The needed skills are read from the proposal's changed paths, and for a new field from its declaration too
    (`computed` or `rollup` means `computed-and-rollups`).
  - `field-types-and-formats` and `capabilities-and-limits` are never required: the base prompt states the types,
    and the limits skill serves answers rather than proposals.
  - The area is read by segment **position** (`/entities/{entity}/{facet}` and
    `/entities/{entity}/fields/{field}/{facet}`), so an entity or a field named `audit` or `rules` is not misrouted.
  - A path that adds the entity map, a whole entity or a whole fields map needs the union of the areas its subtree
    declares, beside `entities-and-fields`: computed or rollup fields, rules, hooks, indexes, and the traits
    `audit`, `softDelete`, `tenancy`, `storage` and `realtime`.
- **Where it shows:** the verdict prints `skillsNeeded=[…] skillsLoaded=[…]` on every turn, pass or fail.

### 7.4 Acceptance criteria

1. `.claude/skills/` holds exactly **9** `alvo-descriptor-*` directories. Each `SKILL.md` has frontmatter with only
   `name` and `description`, `name` equals the directory name, and `new AgentSkillFrontmatter(name, description)`
   accepts both.
2. **Sizes**, measured in UTF-8 after LF normalisation:
   - each `SKILL.md` ≤ **6,144 bytes** and ≤ **200 lines** (about 1.5k tokens, against the standard's < 5000 tokens
     and < 500 lines);
   - each `description` ≤ **300 characters** (the standard allows 1024);
   - each served resource ≤ **16,384 bytes**;
   - the catalogue ≤ **10** skills.
3. **The always-in-context budget does not grow.** The instructions the model receives on its first request (base
   prompt plus skill list) are ≤ **22,758 bytes**, the size of the v3 base prompt alone. Expected: about 3.8k tokens
   of base plus about 0.5k of list, against about 4.5k today. A typical rule, hook or rollup turn loads 1–2 skills
   (+1.5–3k tokens) and carries the knowledge it lacked.
4. The model's tools are the six management tools plus `load_skill` and `read_skill_resource`. `run_skill_script` is
   absent and none of the tools is an `ApprovalRequiredAIFunction`. A turn that loads a skill never halts for
   approval, and the 12-iteration cap still ends a turn that loops on `load_skill`.
5. Every **G** region equals its source. Every skill worked example gets its claimed outcome from the real validator
   on `bike-workshop`. Every `schema/project.schema.json#` pointer resolves. Every snake_case name in a skill's code
   spans is a tool, schema or `bike-workshop` name.
6. The embedded catalogue equals `.claude/skills/alvo-descriptor-*` after LF normalisation. `docker build` over
   `src/MMLib.Alvo.Host/Dockerfile` succeeds, and the packed `MMLib.Alvo.Ai.dll` carries 9 `SKILL.md` resources and the
   schema.
7. `schema-assistant.md` is **v4**. Its tools section lists all eight tools, it states the load-skill rule, and §4 is
   gone from it and stated in the skill, drift-tested there.
8. `EvalCases.All` has **16** cases. `SkillsRead` and the split invariant (D34) apply to every turn and are
   ring0-tested with a passing and a failing canned turn. The bar is §4.2's, unchanged: each case ≥ 2/3 runs per
   language, suite ≥ 90 %. The real-model numbers are the maintainer's run (D8).
9. No public symbol: no `PublicApi.*.verified.txt` moves, Abstractions is untouched, and `MMLib.Alvo.Admin` is
   untouched (if that changes, `scripts/test-admin-e2e` runs whole). ring2 and
   `dotnet build MMLib.Alvo.slnx -c Release -warnaserror` are green.

### 7.5 Eval cases

These are the seven new cases. Each names the skill it needs and the grade it gets (D36).

| Case | Request (EN) | Pass |
|---|---|---|
| `hook_returned_at` | "When a rental becomes returned, set returned_at to the current time." | one valid proposal, only under `rentals.hooks`: a `beforeUpdate` `mutate` of `returned_at` with `now()`, whose condition says `new.status == 'returned'` and (`old.status != 'returned'` or `changed(status)`). [hooks] |
| `reject_negative_price` | "A part's selling price may never be negative." | only `parts.hooks` changes, with a `reject` of a negative `new.unit_price` in **both** `beforeCreate` and `beforeUpdate`; so no `validation` facet (refused). [hooks] |
| `rollup_rentals_count` | "Customers: how many rentals they have." | exactly one change: a new `customers` field with a rollup `count` from `rentals`; so no hook (the ladder). [computed-and-rollups] |
| `unique_part_per_order` | "A part may appear only once on each service order." | only `order_lines.indexes` changes, with a `unique` index over exactly `order_id` and `part_id`. [indexes] |
| `own_orders_only` | "Technicians may list and read only the service orders assigned to them; admins and managers still see all." | exactly `rules.list` and `rules.get` change; each compares `assigned_user_id` with `@user.id`, keeps the admin and manager grants, and no longer admits every `authenticated` caller. [rules-and-cel] |
| `function_action_refused` | "When a service order is ready, run our invoicing function." | no proposal touches `/hooks` at all: not the refused `function`, and not a `webhook` or `http.call` put in its place unasked; `get_capabilities` called; a non-empty answer. [capabilities-and-limits] |
| `can_alvo_call_http` | "Can Alvo call our ERP's HTTP API when a part's stock changes?" | answer only: no proposal, `get_capabilities` called, and the reply does not open by claiming the capability (a leading yes, áno, can, vie or dokáže). [capabilities-and-limits] |

## 8. RCA of the task-management turn (2026-09-30)

The maintainer asked the demo build (`gpt-4.1-mini`, `bike-workshop`) for task management: tasks and task comments,
comments editable only by their author. The turn ended on a card that said the rule *"must evaluate to a boolean"*,
and on a reply claiming the framework had refused an attempt it never checked. The RCA re-ran every shape the model
sent against the real `AlvoHostWorld`, `DescriptorDraft`, `ManagementTools`, validator and `CelCompiler`, in an xUnit
probe over a `git archive` copy of this branch. What follows is what the probe **proved**. A guess stays out.

### 8.1 The proven sequence

1. **The first proposal carried two independent defects**, and the validator reported both in one answer:
   `tasks.product` referenced an entity `products` that does not exist, and `task_comments.rules.update` read
   `assigned_to`, a field of `tasks`. The two violations were *"Field references unknown entity 'products'"* and
   *"'assigned_to' is not a field of entity 'task_comments'"*, whose fix lists the known fields. That is the
   operator's first refusal, verbatim.
2. **A schema-level defect hides the rule pass completely** (probes A6, A8). A `maxLength` on a `text`, or an `enum`
   with `options`, in one entity suppresses the rule error in the other. Each schema defect then fans out into 3–4
   violations (`type`, `then`, `allOf`, the keyword), and their pointers start with `#/`, so `op` is null. This is #292
   (S4).
3. **The model then wrapped the whole rule in quotes.** `"'author == @user.id'"`, `"\"author == @user.id\""` and
   `"'true'"` each draw *"A Rule expression must evaluate to a boolean; this expression evaluates to String."* with
   the fix *"Add a comparison, e.g. field == value, so the expression yields true/false."* That fix is wrong for this
   defect: the expression already is a comparison. `{"$cel": …}` draws a schema type error. The bare
   `"author == @user.id"` is **valid**. So is `"true"`, which opens the operation to every caller (#292, S5).
4. **The budget spent itself on one unchanged defect.** Four `propose_change` calls with the same patch: #1–#3 were
   dry-run (`attemptsLeft` 2, 1, 0). **#4 was never dry-run** and got only
   `{"source":"budget","message":"Stop proposing. Explain to the operator what the framework refused, quoting it."}`.
   The filed proposal stayed #3. A later *valid* `check_change` also got only the budget answer
   (`ManagementTools.cs:48`, `:107`, `:176-179`, `:189-191`, `:199-205`; `ViolationMapping.cs:47`). The model told
   the operator the last attempt was refused. The budget message invited that claim: it says to explain "what the
   framework refused" about an attempt the framework never saw.
5. **Nobody could reconstruct the turn afterwards.** The drawer receives tool names only (`AlvoAssistant.cs:262`). The
   host logs only EventId 6201, on an endpoint failure (`:320-326`). The sequence above had to be rebuilt by probing.
   The eval already records every call (`RecordingChatClient.cs:70-91`, `EvalTrace.cs:73`). The product does not.

### 8.2 Root causes, ranked

| # | Cause | Evidence | Fixed by |
|---|---|---|---|
| 1 | **Nothing states a rule's JSON value shape, and every example starts with `'`.** *"Write CEL string literals in single quotes, so nothing needs escaping inside the JSON"* reads as "wrap the expression" | `schema-assistant.md:82`; `alvo-descriptor-rules-and-cel/SKILL.md:17-18`; examples: base prompt (c) `:139-150`, the skill's only example; 40 of 40 `bike-workshop` rules begin with a role literal. No whole rule of the shape `field == @user.id` is shown anywhere | D43 |
| 2 | **The budget counts attempts, not progress, and never validates the 4th.** Its message invites a false "refused" | `ManagementTools.cs:189-191`; `ViolationMapping.cs:45-48` | D41 |
| 3 | **The compiler's fix is wrong for a quoted expression.** It says to add a comparison, never echoes the source, never says to remove the outer quotes | `CelCompiler.cs:108-113` | D42 |
| 4 | **No skill covers a multi-area new entity.** The load rule is per area. `SkillsRead.AreasOf` needs `entities-and-fields` + `rules-and-cel` + `traits-and-tenancy`, and the turn loaded two. No example composes fields, `audit`, rules, a `ref` to `users` and a `ref` to an existing entity; every rules example is a `test` + `replace` of an existing rule | `schema-assistant.md:30`; `eval/…/SkillsRead.cs` (`AreasOf`) | D43 |
| 5 | Staged validator passes and cascade noise | `DescriptorValidator.cs:115-117`, `:131-138`, `:165-176` | #292 (S4) |
| 6 | Nothing forbids loosening access as a workaround | `alvo-descriptor-rules-and-cel/SKILL.md:30`; the stop rule `schema-assistant.md:317-319` | #292 (S5) |
| 7 | The design turn was never checked against the descriptor (`products`). The `<x>_id` ref naming (7 of 7 refs in `bike-workshop`) is not taught, and the skill example `checked_in_by` teaches the opposite | `DescriptorValidator.cs:422-428`; `alvo-descriptor-field-types-and-formats/SKILL.md:60-70` | naming: D43; the design-time check: #292 (S8) |
| 8 | No turn record | `AlvoAssistant.cs:262`, `:320-326` | D44–D46 |

### 8.3 Decisions

| # | Decision | What it forecloses, and why it is still right |
|---|---|---|
| D41 | **The refusal budget measures progress** (S1). A refusal is *stalled* when its set of blocking violations (keyed by `source`, `pointer`, `code` and `message`) equals, or is a superset of, the previous refusal's set. The first refusal is stalled against the empty set. Only a stalled refusal spends one of **3** attempts. A turn stops after **6** refusals in all, stalled or not. A patch is dry-run unless the 6 are spent, or the 3 are spent **and** that exact patch (`baseRevision` plus operations, `JsonElement.DeepEquals`) was already **refused** in this turn (identity over the unwrapped operations, so an array and its string form are one patch); a patch checked valid is not remembered, so it is dry-run again and filed. A new patch shape is therefore always checked before any budget answer. That answer says the attempt was **not** checked, quotes the re-sent patch's own earlier refusal verbatim (*"This exact patch was refused earlier: …"*; the last refusal, *"The last refused attempt said: …"*, when the ceiling stopped a patch never refused), says so when a valid proposal is already filed, and never ends in an empty quote, and carries `"unchecked": true`. `attemptsLeft` is the smaller of the two remainders | A model that keeps changing its defects without converging can now make 6 dry runs instead of 3: at most 3 more validator runs per turn, still inside the 12-iteration cap. `attemptsLeft` no longer falls on every refusal, so the instructions' sentence "every refused call spends one of the same three attempts" is rewritten. The reliability design's Global Constraint "budget text unchanged" is superseded here, deliberately. Oscillating between two defect sets never stalls, and the 6-refusal ceiling is exactly what bounds it. The eval's `RefusedAttempts` stops counting an `unchecked` answer, because it was no dry run |
| D42 | **A quoted whole expression gets its own refusal** (S3; security core, CEL compile: *needs-deep-review*). In `CelCompiler.ValidateResultType`, under a predicate profile (`Rule`, `Condition`, `Access`), when the parsed root is a `String` literal whose content compiles to `Bool` under the same profile and entity, the fix is *"Remove the outer quotes; the value is the expression itself: `<content>`"*. **Every** result-type refusal also echoes its source, truncated to 120 characters plus `…`. The existing first sentence of each message is kept as its prefix, so every fragment the skills and tests quote still matches | **The accepted set does not move.** The check runs only on a source already refused, and only ever changes the text of the refusal. The content is compiled **once** more with the quote check off, so nesting cannot recurse: at most two compilations per source. `Computed` and `Mutate` get the echo only, because a string literal is a legitimate value there. A message now carries author text, so a test that quotes a whole result-type message must match a fragment. None does today (`git grep` finds the sentence only in `CelCompiler.cs`) |
| D43 | **The rule's JSON shape is taught, and a new entity has a worked example** (S2). (1) The single-quote line becomes, in both the base prompt and `rules-and-cel`: *a rule's value is the bare CEL expression as one JSON string (`"author_id == @user.id"`); single quotes go only around a text value inside it, such as a role name; quoting the expression itself makes it a string, not a rule (`"'author_id == @user.id'"` is refused)* — worded without "wrap … in quotes", so the sentence cannot be skimmed as the instruction it forbids. (2) `gen:cel-rule` gains the refused `'user_id == @user.id'` and `'true'`. (3) The worked example `new-entity-with-rules` in the rules skill: `order_comments` with `audit`, a `ref` to `service_orders` with `onDelete`, a `ref` to `users` without one, and the whole `rules` object with an owner clause. (4) **Amended by pre-flight ruling B1:** no whole-entity example goes in the base prompt, which points at the rules skill instead; a smaller whole entity without owner rules (`suppliers`) goes in `entities-and-fields`. Every example is proven by the Host outcome tests. (5) The load rule for a new entity: `entities-and-fields`, plus the skill of every area it declares (`rules-and-cel` for rules, `traits-and-tenancy` for `audit` or `tenancy`), pinned against `SkillsRead.AreasOf`. (6) A `ref` is named for what it points at, ending `_id`, following the project. The `checked_in_by` example becomes `check_in_technician_id` | The base prompt grew by 755 B in T13 and 46 B more in its review round, against a budget of 22,758 B. The budget is **not** raised: the task measures it first and trims prose if it must, never a drift-tested fact. The worked-reply count stays 9 (B1). Measured: 21,890 bytes before, 22,645 after T13, 22,691 after its review round (F1–F4: the rules skill loads for every new entity, a ref's `entity` is one the descriptor declares, refs are named for their role, and the counter-example reads *"is one string literal, not a rule"*); the unknown-ref fix now offers the declared entities first. The instructions become **v5**. The naming rule is a convention, not a validator rule, so a project that names refs otherwise is followed, not corrected. The design-time name check stays #292 (S8) |
| D44 | **Every turn is traced** (S7). `TurnTrace`, internal to `MMLib.Alvo.Ai`, holds a header, one entry per call and an end reason. The header has `format` (`alvo.assistant.turn/1`), the instructions version, the provider kind, the model, the project and the base revision (the first `get_descriptor`'s). Each entry has `round`, `tool`, the arguments and a result summary. The arguments are `baseRevision`, the patch `operations`, `skillName` and `resourceName`; the `summary` appears only as its length. The result summary has `valid`, `revision`, `changedPaths`, `attemptsLeft`, `unchecked` and every violation for a dry run; the `revision` for `get_descriptor`; a length for any other read. The end reason is `answered`, `iteration-cap` or `endpoint-failed`. It is recorded by an internal `TurnRecorder : DelegatingChatClient` under the function-invoking loop, with the eval's call-matching rule (round plus call id). The entries mirror the eval's `RecordedCall` members, so a trace line and an eval line read alike. **Never operator text**: not the message, the history, the model's reply, or the `summary` argument. Every string is scrubbed of secret-like values. The trace is capped at **65,536** bytes. Each call is logged as **EventId 6202** and the turn's end as **EventId 6203**, with operation paths but never operation values. **Amended by the pre-flight rulings:** the header's `calls` is the entry array and its count is `callCount` (M4); each entry is scrubbed before it is measured, and the trace is measured and emitted by one serialiser with relaxed escaping, keeping room for `droppedCalls` (H2); a 6202 line reduces each violation to `source`, `pointer`, `code` and `severity` and drops a tool error's message, because after D42 a refusal quotes the author's source (H3); the 6202 lines are built from the recorder's calls, not the capped trace (M2); and 6203 is written from a `finally`, so a turn the caller abandons is logged with the end `abandoned`, which never appears in a trace (L12) | The eval keeps its own recorder: it also counts tokens and requests, and sharing the type would need a grant from `MMLib.Alvo.Ai` to the eval's suite. The shape is what is reused, not the class; merging the two is a later tidy-up. The log deliberately carries less than the trace. A host ships Information logs to aggregators, and a descriptor's text is the operator's, so the log names *where* a patch wrote, and only the screen shows *what* |
| D45 | **The trace crosses `IAlvoAssistant` as an internal, opt-in update.** `AssistantRequest` gains `internal bool IncludeTrace { get; init; }` and `AssistantUpdate` gains the nested `internal sealed record TurnTraced(string Json)`, emitted last and only when the request asked. `MMLib.Alvo.Abstractions` grants `InternalsVisibleTo` to `MMLib.Alvo.Ai` and `MMLib.Alvo.Admin` (the producer and the one consumer), and to `MMLib.Alvo.Ai.Tests` and `MMLib.Alvo.Admin.Tests.EndToEnd` (the scripted assistant there emits it). **No public type or member is added.** `PublicApi.MMLib.Alvo.Abstractions.verified.txt` moves by exactly those four attribute lines, and every other baseline stays put | This is Abstractions' own precedent (`AlvoFrameworkTables`, `IAlvoDataReachability`): a seam only this family uses is internal, because un-publishing is breaking and publishing is one word away. The rejected alternative was a public `AssistantUpdate.Trace(string Json)`: it would freeze a diagnostic shape as contract, and every third-party consumer would receive it. Opt-in means such a consumer never meets a case it cannot name. **This departs from `ToolInvoked`'s remark** (*"the name only; an argument list would carry the descriptor…"*): the trace carries operations, but only to the screen that asked for it, never into a log line. A third-party `IAlvoAssistant` cannot emit a trace, and the drawer then shows none, which is the correct empty state. The grants carry the family's forgeability caveat (unsigned, a name match). **They reach every Abstractions internal**, `PolicyDecision`'s constructor and `Allow` and `CompiledExpression`'s constructor among them, so `InternalGrantArchitectureTests` in the Ai and the Admin suite reads each compiled assembly's member references and holds that neither reaches one (pre-flight H4); both types' remarks name the new grants |
| D46 | **"Admin-only" is read as the dashboard only, and as the operator who asked the turn, not as the `admin` access level.** The drawer shows a collapsed *Turn details* (the existing `a-disclosure` pattern) holding the pretty-printed trace and a *Copy JSON* button (`AdminInterop.CopyAsync`); the text stays selectable when the clipboard is refused | The dashboard cannot tell an administrator today. `IAlvoManagement` has no caller-level query, and Access already works by attempt-and-refuse (ux-pass D-10). A level gate would need a new public member on `IAlvoManagement`, which is not earned by a diagnostic panel. It would also protect nothing: every value in the trace was read by tools running under **that operator's own publication**, it holds no operator text, and it is scrubbed. **Open for the maintainer:** if the level gate is wanted, it is a follow-up that adds the caller-level query |

### 8.4 Acceptance criteria

1. **Budget (D41):**
   - three refusals whose blocking sets each change, then a valid fourth: `attemptsLeft` reads 2, 2, 2 and the fourth
     is filed as the proposal;
   - three identical refusals, then the same patch: the fourth is not dry-run (3 Management dry runs), carries
     `"unchecked": true`, and its message starts *"This attempt was not checked"* and contains the last refusal
     verbatim;
   - after three stalled refusals, a **new** patch is dry-run, and a valid one is filed;
   - after three stalled refusals, a patch `check_change` found valid is dry-run again by `propose_change`, and filed;
   - the seventh refusal-bound attempt is never dry-run: exactly **6** refused dry runs per turn, the most;
   - the instructions state the progress rule and `unchecked` (drift-tested `_toolFacts`).
2. **CEL (D42):**
   - under `Rule`, `'owner_id == @user.id'`, `"owner_id == @user.id"` and `'true'` are refused, each with a fix that
     starts *"Remove the outer quotes"* and names the content;
   - `'admin'` (content that does not compile) keeps the generic fix;
   - every result-type refusal contains its source, and a 300-character source is echoed as 120 characters plus `…`;
   - no fixture that compiled before now fails, and none that failed now compiles.
3. **Teaching (D43):**
   - the always-in-context instructions stay ≤ **22,758** bytes, and every `SKILL.md` stays ≤ **6,144** bytes and
     ≤ 200 lines;
   - `new-entity-with-rules` and example (i) are valid on `bike-workshop` through the real tool;
   - the quoted rule is refused with the D42 fix and the bare rule accepted, both by the dry run (`SkillClaimTests`);
   - the new-entity load rule names exactly `SkillsRead.AreasOf` of example (i);
   - every `ref` in `bike-workshop` and in every worked example ends in `_id`;
   - `InstructionRepliesTests.WorkedReplies` is 10.
4. **Trace (D44–D46):**
   - a traced turn's last update is `TurnTraced`, with one entry per call in order, `skillName` on `load_skill`,
     `operations` on dry runs, and `attemptsLeft`/violations in their results;
   - the trace is ≤ **65,536** bytes UTF-8 for a 40-call turn of 10 KB patches, and still has its header and end
     reason;
   - the operator's message, a history turn, the model's reply and the `summary` argument appear in **0** trace
     bytes and **0** log lines;
   - the six secret shapes (OpenAI-style `sk-`, GitHub `gh?_`, Slack `xox?-`, AWS `AKIA`, a JWT, and a
     connection-string password) are redacted;
   - EventId 6202 is logged once per call and 6203 once per turn;
   - an untraced request gets no `TurnTraced`;
   - `scripts/test-admin-e2e` runs **whole** and green, including the drawer's *Turn details* and *Copy JSON*
     scenario.
5. **Public API:** no public symbol is added. The only baseline that moves is the Abstractions baseline, by the four
   D45 `InternalsVisibleTo` lines. ring2, `dotnet build MMLib.Alvo.slnx -c Release -warnaserror` and
   `docker build -f src/MMLib.Alvo.Host/Dockerfile .` are green.

### 8.5 Out of scope: #292

The RCA's remaining solutions are filed as **#292**:
- **S4**: every validator pass at once, `#`-free pointers, one leaf error per schema defect (cause 5);
- **S5**: never loosen access as a workaround, and an `access-widened` warning (cause 6);
- **S6**: the two-turn `task_management` eval case, which needs history-carrying cases;
- **S8**: check names at design time (cause 7, first half). D43 only *teaches* the `<x>_id` convention.
