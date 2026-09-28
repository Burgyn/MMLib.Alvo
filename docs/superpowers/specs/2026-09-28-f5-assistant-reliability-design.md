# F5 — the schema assistant edits by patch, not by retyping the descriptor

> Builds on `docs/superpowers/specs/2026-09-22-f5-ai-agent-design.md` (#29). Nothing here changes its
> security argument: **no tool writes**, the operator applies from Preview, the dry run runs with the
> literals `DryRun: true, AllowDestructive: false`. What changes is *how a draft is expressed*.

## 0. The live failure, and what it actually proves

The operator asked, in Slovak: *"add to customers a column full_name = first name + space + last name"*.
The assistant read four tools, then called `validate_descriptor` twice with a **hand-retyped whole
descriptor**: first a JSON syntax error at line 257 (the CEL string literal's quotes), then
`'0x01' is invalid within a JSON string` (Slovak diacritics elsewhere in the document retyped as
`\u0001`-style garbage), asked whether to "fix the unicode escapes", and stopped. The card showed
*"Alvo refused this draft: Descriptor is not valid JSON…"*.

**Finding the brief did not state: even a perfect retype would have been refused.** In this build the
`Computed` profile types `+` as arithmetic only (`CelTypeChecker.CheckArithmetic` →
`RequireNumeric`: *"Arithmetic left operand must be numeric; found String"*), and `ComputedFieldCheck`
refuses any constant — `' '` renders as a bind parameter, which DDL has no form for. `Mutate` admits no
arithmetic at all (`Arithmetic` is `_computedOnly`; the call allow-list is `lowerAscii`, `now`). So
**`full_name` is not expressible server-side today**, by any path. The reliable outcome for this exact
request is therefore *one* dry-run attempt, the framework's refusal quoted, and a Slovak explanation
with the honest alternative (a plain `string` field the client writes) — in one turn, with no question
back. §6 owes the core issue that makes the request honourable; the eval case flips when it lands.

> **Superseded (28 Sep 2026, ruling 2).** The core change landed before the assistant tasks: the `Computed`
> profile now joins two strings with `+`, and `first_name + ' ' + last_name` validates, dry-runs and applies —
> including to a populated SQLite table. §3.4 states exactly what `Computed` allows now; the paragraph above is
> kept as the record of why the design was written.

## 1. Root causes

| # | Cause | Where | Why a patch removes it |
|---|---|---|---|
| R1 | **Triple encoding.** `get_descriptor` returns `ManagementDescriptor.DescriptorJson` as a *string* property, so the model reads JSON-in-a-JSON-string; `validate_descriptor(string descriptorJson)` makes it write JSON-in-a-string-in-tool-call-arguments; a CEL string literal adds a third level (`\\\"`). Line 257 was that third level. | `ManagementTools.GetDescriptorAsync`, `ValidateDescriptorAsync` | Patch values are JSON *values* in the arguments — one encoding level, the one providers train on. CEL literals use single quotes (`CelLexer` accepts `'`), so no escaping remains at all |
| R2 | **Default encoder escapes everything non-ASCII.** `ToolJson.Options` is `JsonSerializerDefaults.Web` with the default `JavaScriptEncoder`: `č` → `č`, `"` → `"`, `'` → `'`, `+` → `+`. The model saw a descriptor of `\uXXXX` sequences and had to reproduce them by hand — small models hallucinate hex digits, which is exactly `0x01` | `ToolJson.cs` | The model never re-emits text it did not change: untouched bytes stay server-side. Tool results additionally switch to `UnsafeRelaxedJsonEscaping` (§2.4) so what it reads is legible |
| R3 | **Long verbatim output.** `bike-workshop` is 31.6 KB (~9–10k output tokens) to change ~120 bytes. Every token is an independent chance of corruption, and local/OpenAI-compatible models commonly truncate or drift on long outputs; latency and cost scale with the whole document | prompt step 2: *"Draft the whole descriptor, not a fragment"* | A patch is O(change), not O(document) — the full_name edit is one `add` op of ~150 bytes |
| R4 | **Wrong document model for errors.** Refusals are flattened to `"{path}: {message} — {fix}"` strings; a JSON parse failure points at `/` with a line number in text the model never saw | `ManagementTools.Refusals`, `DescriptorValidator` L148 | A patch fails *per op, at a pointer the model wrote* — `/entities/customers/fields/full_name/computed` is both the validator's `DescriptorValidationError.Path` and the patch `path`, so a violation names the exact op to fix |
| R5 | **The last attempt wins, even a broken one.** `ProposalFrom` takes `LastValidated` regardless of refusals, so a refused retry overwrites an earlier valid draft | `AlvoAssistant.ProposalFrom` | Rule change (§2.3) — independent of patching, but the same PR |
| R6 | **No stop rule the code enforces.** "Fix and retry" is prompt prose; nothing bounds attempts, nothing tells the model what cannot be done before it tries, so it improvised a confirmation question | `SystemPrompt.Text` | Attempt budget in the tool (§2.3) + a descriptor-model section that states the Computed limits (§3) |
| R7 | **Undocumented exceptions end the turn.** `ManagementEscalationException` (an `access` change by a developer) and `AlvoIdempotencyConflictException` are documented on `ApplyDescriptorAsync` but not caught by `AnsweredAsync` — a proposal touching `access` is a failed turn, not a refusal | `ManagementTools.AnsweredAsync` | Mapped to violations (§2.2) |

## 2. The tool surface

### 2.1 Prior art and the choice

- **RFC 6902 JSON Patch** (ops `add`/`remove`/`replace`/`move`/`copy`/`test`, paths are RFC 6901 JSON
  Pointers) — chosen. Models know it from training data (Alvo's "adopt the known spec" rule); it covers
  every descriptor edit without a tool per schema feature; its pointers are the validator's pointers;
  `test` gives optimistic checks on the subtree the model reasoned about.
- **RFC 7396 merge patch** — rejected: it cannot address array elements (`hooks.beforeCreate` is an
  array), and `null` means *delete*, colliding with a legitimate `"default": null`.
- **Typed ops** (`add_field{entity,name,type,…}`, `set_rule`, …) — rejected as the *only* surface: every
  schema feature (rollup, hooks, indexes, formats, realtime) would need its own tool and schema, so the
  tool set would drift from `project.schema.json` — a second spelling of the frozen artifact. A typed
  op also does not remove any error class that JSON Patch + the validator does not already catch.
  Deviation recorded: Anthropic/OpenAI tool-design guidance favours small typed tools; the descriptor is
  already a typed, schema-validated document, so the *document* is the type and the pointer is the
  address. Revisit if the eval shows pointer errors dominate (§4.3).
- **Microsoft.Extensions.AI**: tools stay `AIFunctionFactory.Create` over small methods; parameters are
  typed (`int baseRevision`, `JsonElement operations`, `string summary`) so the generated JSON schema
  constrains the call. `ChatClientAgent` wraps `FunctionInvokingChatClient`; its
  `MaximumIterationsPerRequest` is set explicitly (12) so a looping model ends as a turn, not a bill.
- No RFC 6902 package: `Microsoft.AspNetCore.JsonPatch.SystemTextJson` (.NET 10) targets typed POCOs
  and would pull ASP.NET Core into a package whose boundary is `Abstractions` only. An internal
  `JsonPatch` over `System.Text.Json.Nodes` (~150 lines) is the idiomatic, dependency-free answer; its
  conformance is proven by the community `json-patch-tests` suite, vendored as test data after a licence
  check per `alvo-dotnet-conventions`.

### 2.2 The six tools

| Tool | Parameters | Returns | Change |
|---|---|---|---|
| `get_descriptor` | — | `{ project, revision, descriptor: {…object…} }` | descriptor as a **JSON object**, not a string (R1) |
| `get_schema`, `get_capabilities`, `get_revisions` | — | as today | relaxed encoder only |
| `check_change` | `baseRevision`, `operations` (RFC 6902 array) | `Outcome` (below) | the dry run — files nothing; for "would this work?" questions |
| `propose_change` | `baseRevision`, `operations`, `summary` (one sentence, operator's language) | `Outcome`; files a proposal **only when valid** | replaces `validate_descriptor` |

`validate_descriptor(descriptorJson)` is **removed, with no fallback.** A fallback is the path a model
reaches for the moment a patch is refused — reintroducing R1–R3 exactly when it is struggling. Any
wholesale edit is still expressible as `replace` at `/entities/<name>` (or any subtree). A `replace` or
`remove` at the **root pointer `""`** is refused by the tool (`whole-document-replace`), because it is
the removed path in disguise.

Pipeline, one method shared by both tools (`DescriptorDraft.BuildAsync`):

1. `GetDescriptorAsync`; if `revision != baseRevision` → violation `stale-revision` carrying the current
   revision (the model re-reads and re-bases; it does not guess).
2. Parse to `JsonObject`; apply the ops **atomically** (RFC 6902 §5: any failing op aborts all). Bounds:
   ≤ 50 ops, ≤ 16 KB of op values — refused as `patch-too-large`, not truncated.
3. Serialize with `UnsafeRelaxedJsonEscaping` + indentation matching `WorkingCopy`'s writer, so Slovak text
   stays literal UTF-8 and Preview's diff shows only the change (`WorkingCopy.Replace` already parses to
   `JsonObject`, so no formatting contract is lost).
4. `ApplyDescriptorAsync(… DryRun: true, AllowDestructive: false)` — unchanged literals.
5. Map the result to one `Outcome`.

```jsonc
// Outcome (internal record, camelCase, relaxed encoder)
{ "valid": false, "revision": 12,
  "plan": { "isEmpty": false, "hasDestructiveChanges": false, "steps": ["Add column customers.full_name …"] },
  "changedPaths": ["/entities/customers/fields/full_name"],       // what the patch touched, server-computed
  "violations": [ { "source": "validation",                       // patch | validation | plan | concurrency | access | budget
                    "pointer": "/entities/customers/fields/full_name/computed",
                    "message": "…framework's words, verbatim…",
                    "fix": "…FixSuggestion, verbatim, or null…",
                    "op": 0 } ],                                  // index of the op whose path is the pointer's prefix, when one is
  "attemptsLeft": 2 }
```

Source mapping: patch errors (bad pointer, missing parent, `test` mismatch) → `patch`, pointer = the op's
path; `DescriptorValidationException.Result.Errors` → `validation`, one entry per error (Warnings are
returned as `severity: warning` and do not block); `DestructiveChangeNotAllowedException` → `plan`, with
the plan's step reasons; `DescriptorConcurrencyException` → `concurrency`;
`ManagementEscalationException` → `access`; `ManagementForbiddenException` stays the existing
`ToolError("forbidden")`. `Proposal.Refusals` (public, `IReadOnlyList<string>`) keeps its current
string form, built from the same violations, so the drawer's card is untouched.

### 2.3 How a turn ends

- **The proposal is the last *valid* `propose_change`.** If none was valid, the last refused attempt is the
  proposal and carries its refusals (today's card, now reached only after a real attempt). Fixes R5.
- **Budget: 3 refused `propose_change`/`check_change` attempts per turn.** The 4th call returns only a
  `budget` violation: *"Stop proposing. Explain to the operator what the framework refused, quoting it."*
  Enforced in `ManagementTools` state, which is already per turn.
- **One proposal per turn.** A second valid `propose_change` replaces the first (the drawer shows one card),
  and the instructions say not to split one request into several proposals.

### 2.4 The one engine-level refusal the dry run cannot see today — make it a violation

`SchemaMigrationRunner.RunAsync` returns at `options.DryRun` **after planning, before DDL**. SQLite rejects
`ALTER TABLE … ADD COLUMN … GENERATED ALWAYS AS (…) STORED` on an existing table (it admits only
`VIRTUAL` there), so a computed field added to `customers` validates, dry-runs clean, and fails at the
operator's Apply with a raw provider error. Whatever the fix being written now chooses (table rebuild, or
refusal), the requirement from this design is: **the decision is made at plan time, as a dialect port
member** (per-engine DDL capability is a port member, never an `if` in the core), and when the engine
cannot do it the plan raises a `DescriptorValidationError` at `/entities/<e>/fields/<f>/computed` with a
fix ("…declare it on a new entity, or …"). Then it reaches the assistant as `source: validation` with a
pointer, and the model explains it like any other refusal. A test in `MMLib.Alvo.Host.Tests` pins it:
dry run of "add a computed field to a populated SQLite table" yields a violation, not a clean plan.

## 3. The instructions ("skill")

Shipped as an **embedded resource** `src/MMLib.Alvo.Ai/Instructions/schema-assistant.md`, first line
`<!-- alvo-schema-assistant v2 -->`, loaded once by `internal static class AssistantInstructions`
(replaces `SystemPrompt`). Still fixed, still not operator-editable (#29 §5). Markdown, because models
follow headed sections and examples better than prose; a resource rather than a `const` so it is
diffable in review and the drift tests (§4.1) can parse it. Sections, with their load-bearing content:

1. **Role and guard** — you propose; the operator applies from Preview; you have no writing tool.
2. **Can / cannot** — can: entities, fields, facets, `renamedFrom`, rules, hooks (`reject`/`mutate`),
   rollups, indexes, formats. Cannot: `automation`, `functions` (quote `get_capabilities` verbatim), data
   queries, apply, `access` unless the operator is admin (the tool will say).
3. **The descriptor model in brief** — `entities.<name>.fields.<name>` with `type` ∈ the schema's
   `fieldType` enum (listed; drift-tested); facets per type (`maxLength` string, `precision`+`scale`
   decimal — total digits, `values` enum, `entity`+`onDelete` ref); `required`/`unique`/`default`
   (`{"$cel": …}`); `rules.{list,get,create,update,delete}` — a missing op is deny; before-hooks
   `reject`/`mutate`, in-transaction; rollups over related rows are read-only.
4. **What Computed allows** — same-row CEL rendered into a STORED generated column, maintained by the
   database. The next task copies this list into the instructions verbatim; each rule names the refusal the
   framework gives, so the model can explain it.
   - **Arithmetic**: `+ - * /` and unary `-` over **numeric** fields (`unit_price * amount`,
     `net_total + vat_total`); a computed field may read a rollup field of its own row.
   - **Text**: `+` over **two strings** joins them (CEL's `string + string`), left to right:
     `first_name + ' ' + last_name`. A `string`, `text` or `enum` field, or a text constant in single quotes,
     may be joined. There is **no implicit conversion**: `first_name + visits` is refused, and a computed field
     has no `string()` to convert with.
   - **Null rule**: every joined operand must be **never null** — a `required` field, a constant, or a
     field read inside the branch its own `has()` guards: `(has(middle_name) ? middle_name : '') + last_name`,
     or with the separator only when present, `first_name + (has(middle_name) ? ' ' + middle_name : '') + ' ' +
     last_name` and `has(middle_name) ? first_name + ' ' + middle_name : first_name`. An
     optional field joined directly is refused (*"'+' would join 'street', which may be null…"*, fix: *"Make
     'street' required, or write the fallback explicitly: (has(street) ? street : '')"*). Reason: CEL's `+`
     has no null overload, and SQL's `||` makes the whole value NULL when any part is.
   - **Type and length**: a text result needs a field declared `"type": "string"` (or `"text"`), and a number a
     numeric type; with `maxLength`, it must hold the longest join (the sum of the parts' `maxLength`s —
     `first_name` 60 + `' '` 1 + `last_name` 60 = 121). Omitting `maxLength` is always fine.
   - **Ternary** `c ? a : b` whose condition compares two fields of the row or tests `has(field)`; `has()`.
   - **Constants**: a **text** constant may appear in a join or a ternary branch. A **numeric** constant
     (`unit_price * 1.2`) is refused — hold a rate in a field of its own that a before-hook maintains. An
     expression that reads **no field** (`'always the same'`) is refused — that is a `default`, not computed.
     A text constant cannot hold a line break, a tab or another control character.
   - **Not another computed field**: a computed field reads stored fields only (a rollup is stored); reading
     another computed field is refused with that field's expression as the fix, and a computed field never reads
     itself.
   - **A text constant is joined, never compared**: `first_name == 'Jana' ? …` is refused (*"a text constant
     can be joined, not compared, in a computed field"*); compare two fields instead.
   - **Never**: `@user`/`@tenant`, `now()` or any function, `old.`/`new.`, `changed()`, role membership.
5. **Editing mechanics** — read `get_descriptor` once; express the change as RFC 6902 ops against its
   `revision`; pointers are `/entities/<entity>/fields/<field>`; `add` to create, `replace` to change,
   `remove` to delete; rename = `move` + `add …/renamedFrom` (without `renamedFrom` a rename is drop+add
   = data loss); write CEL string literals in **single quotes**; never touch what the request did not ask
   for; `check_change` only when the operator asks *whether* something is possible.
6. **Worked examples** (the section models copy most): (a) add field `notes` to `bikes` — one `add` op;
   (b) rename `phone` → `phone_number` — `move` + `renamedFrom`; (c) a rule "only technicians delete
   parts" — `add /entities/parts/rules/delete` `"'technician' in @user.roles"`; (d) **full_name**:
   `add /entities/customers/fields/full_name {type:string, computed:"first_name + ' ' + last_name"}` →
   a valid proposal (both parts are `required`); the reply says the database now maintains it for every
   existing customer and that a caller cannot write it. (e) the same with `middle_name` (optional) → the tool
   returns the null-rule refusal → the model retries once with `(has(middle_name) ? middle_name : '')`.
   Each example shows the call, the outcome JSON and the final reply.
7. **Behaviour rules** — act, don't ask: a request that names what it wants is a request to propose it;
   ask only when two readings lead to different schemas. Answer in the operator's language; quote
   framework refusals verbatim (they are English) in a quote block, then explain in the operator's
   language. One proposal per request. After a valid proposal: 2–3 sentences — what a caller can now
   send, what is now rejected, what data moves, cost first ("a dropped column is lost data"). On a
   refusal: fix the op the violation's `op`/`pointer` names and retry; after 3 refused attempts — or at
   once if the refusal says the construct is unsupported — stop and explain. Never repeat a secret.

## 4. Evaluation

### 4.1 Deterministic, in the rings (`MMLib.Alvo.Ai.Tests`, scripted `IChatClient`, no network)

- Tool set is exactly `check_change, get_capabilities, get_descriptor, get_revisions, get_schema,
  propose_change`; both write-shaped tools call `ApplyDescriptorAsync` with `DryRun: true,
  AllowDestructive: false` (replaces the five-name assertion).
- `JsonPatch`: vendored `json-patch-tests` cases pass; atomicity (a failing 3rd op leaves the doc
  untouched); root `replace`/`remove` refused; bounds refused.
- **Byte-stability**: a descriptor containing `č ť ž ô "quoted" +` patched with one `add` → the proposal
  parses, contains those characters literally (no `\u`), and is `JsonNode.DeepEquals` to the original
  everywhere outside `changedPaths`.
- Mapping: validation error → `validation` violation with pointer/message/fix verbatim and the right `op`
  index; stale base → `concurrency` with current revision; escalation → `access`, not a failed turn.
- Turn rules: last valid proposal survives a later refused attempt; 4th attempt gets `budget`; a turn with
  no `propose_change` emits no `Proposal`.
- Instructions drift: resource present with its version line; every tool name it mentions is registered
  and vice versa; its type list equals `project.schema.json` `$defs.fieldType.enum`; every JSON example
  op in it applies cleanly to `examples/bike-workshop` and yields the outcome the example claims (the
  examples are executable, so they cannot rot).

### 4.2 Real-model evals — on demand, in no ring

`scripts/eval-assistant --endpoint … --model … [--runs 3]`, driving a small console project
`eval/MMLib.Alvo.Ai.Eval` (not a test project, not in the rings' `dotnet test`, like `test-load`). It boots
the real management surface over a temp SQLite seeded from `examples/bike-workshop`, runs the real
`AlvoAssistant`, and grades **outcomes, not prose**. Deviation recorded: #29 §6.4 AC5 reads *"No test in
the repository performs a network call to a model provider"* — it is narrowed to *"no test in the rings or
on the PR"*; the eval is a measurement, like load calibration.

| Case (asked in Slovak and English) | Pass when |
|---|---|
| full_name | valid proposal; `changedPaths == ["/entities/customers/fields/full_name"]`; ≤ 1 refused attempt (the §6 core issue has landed; the pre-core "cannot" case is retired) |
| full_name with an optional part | ≤ 2 `propose_change` calls; the second uses the `has()` fallback and is valid |
| add optional `notes` text to bikes | valid; one `add`; `type: text`, not required |
| rename customers.phone → phone_number | valid; plan non-destructive; `renamedFrom: "phone"` present |
| only technicians may delete parts | valid; `rules.delete` compiles; no other rule changed |
| send a webhook when an order completes | no proposal; capability sentence quoted verbatim |
| drop customers.street | refused as destructive; reply states the data loss first |

Every case also asserts the invariants: ≤ 12 model iterations, ≤ 6 tool calls, no `whole-document`
violation, wall-clock recorded. Suite pass: each case ≥ 2/3 runs, overall ≥ 90 %. Results (model, pass
rate, median calls, p50 latency, tokens) print as a table; published per model in
`docs/assistant-evals.md` when the maintainer runs a calibration — Convex's `convex-evals` is the prior art
the analysis (§9.1) already names.

### 4.3 What would change the design

If pointer errors (`source: patch`) dominate refused attempts on the eval, add **one** typed convenience,
`add_field(entity, name, field)`, compiled to the same `add` op — additive, no second pipeline.

## 5. Public API, files, risks, tasks

**Public API: no new public symbol.** Every new type (`JsonPatch`, `DescriptorDraft`, `ChangeOutcome`,
`ToolViolation`, `AssistantInstructions`) is `internal` to `MMLib.Alvo.Ai`, reached by the existing
`InternalsVisibleTo` grants; `PublicApi.MMLib.Alvo.Ai.verified.txt` moves by one line only — the eval's
`InternalsVisibleTo` grant (Ruling 5, D7). `AssistantUpdate`
keeps its shape (`Proposal.Refusals` stays strings); only the `ToolInvoked` remark ("a fixed five") is
reworded — a doc comment, not the contract. The core change in §2.4 lives behind the existing dialect port.

**Files.** `src/MMLib.Alvo.Ai/Internal/ManagementTools.cs` (tools, budget, mapping);
`Internal/JsonPatch.cs`, `Internal/DescriptorDraft.cs`, `Internal/ChangeOutcome.cs` (new);
`Internal/ToolJson.cs` (relaxed encoder); `Internal/SystemPrompt.cs` → `Internal/AssistantInstructions.cs`
+ `Instructions/schema-assistant.md` + `<EmbeddedResource>` in the csproj; `AlvoAssistant.cs`
(`ProposalFrom`, iteration limit); `src/MMLib.Alvo.Abstractions/Ai/AssistantModels.cs` (remark);
`test/MMLib.Alvo.Ai.Tests/*` (+ `TestData/json-patch-tests`); `test/MMLib.Alvo.Admin.Tests.EndToEnd/
{AssistantScenarios,ScriptedAssistant}.cs` (tool name); `scripts/eval-assistant`, `eval/MMLib.Alvo.Ai.Eval`;
the #29 design §4.2 table and §6.2/§6.4 (tool set, AC5 wording); `CLAUDE.md` repo map (the eval, in no
ring); `package-boundary.md` only if the eval project needs listing.

**Risks.**
- *Pointer hallucination* (model invents `/entities/customer`): the `patch` violation names the missing
  parent and lists sibling keys in `fix` ("entities has: customers, bikes, …") — the "similar: Y" hint
  §9.2 of the analysis asks for.
- *Array indices shift* inside hooks: instructions prefer `/-` for append and `test` before a `remove` by
  index.
- *Relaxed encoder* emits `<`/`>` unescaped: acceptable because tool results go only to the model, never
  into HTML; the drawer renders refusals as Razor-encoded text. Stated in `ToolJson`'s remarks.
- *Instructions grow into a manual*: the section 4 table is the budget; the drift tests keep it true, and
  the Computed section is the only one that must change with the core.
- *Prompt injection via descriptor text* is unchanged: no write tool, dry run literal, human apply.
- *Dry-run blind spots beyond §2.4*: any other apply-time DDL failure still surfaces only at Preview's
  Apply. Out of scope; §2.4 fixes the one this request hits.

**SDD split (each a green PR-able slice, TDD):**
1. **Patch engine** — `JsonPatch` + conformance suite + atomicity/bounds/root refusal. Pure, no model.
2. **Tools** — `get_descriptor` as object, relaxed encoder, `check_change`/`propose_change` over
   `DescriptorDraft`, violation mapping (incl. escalation), budget, last-valid rule; remove
   `validate_descriptor`; update the two e2e scripted files and the #29 design's tables.
3. **Instructions** — the embedded resource, loader, executable examples and drift tests.
4. **Eval harness** — `eval/MMLib.Alvo.Ai.Eval` + `scripts/eval-assistant` + the case table; first run
   against the maintainer's model recorded.

## 6. Owed, outside this design

- **Core issue: string concatenation in `Computed` — landed (ruling 2).** As built:
  - `CelTypeChecker`: `+` over two strings is its own construct (`Concatenation`), admitted in `Computed` only;
    a mixed pair is a type error (no implicit conversion, no `string()` in the profile).
  - **The null rule is a refusal**, decided against the CEL spec: CEL's `+` has no null overload (a null operand
    is an evaluation error), SQL's `||` yields `NULL`. Neither is adopted over the other; an operand that can be
    null is refused at compile time unless its fallback is written with the profile's own coalescing construct,
    `has(f) ? f : ''`. **Deviation recorded:** `Computed` arithmetic already lets a null propagate (the
    interpreter answers null, SQL answers NULL), and concatenation does *not* follow that precedent — a name
    that silently turns empty for every row missing one part is the wrong default, and the refusal is additive
    to relax. The interpreter joins two strings and answers null for a null operand, as `||` does, so the two
    backends agree on every tree the compiler admits.
  - Rendering through two `IFieldSqlRenderer` members (the expression half of the engine port, which is where
    the core renderer reaches a dialect): `RenderStringConcatenation` (default `(l || r)`; the T-SQL fake spells
    `CONCAT`) and `RenderStringLiteral` (default `null` = the dialect declines, the constant is bound and refused).
    Only the scalar entry point inlines, only a text constant in a value position; every predicate still binds.
    Fix round 1: the concatenation default also denies (throws `NotSupportedException`, since `||` is a logical OR
    on some engines); SQLite and PostgreSQL declare `||`.
  - Quoting: SQLite the standard literal (`AlvoSqlStringLiteral`, quotes doubled; `internal`, shared with the two
    driver assemblies only, because a public quoter invites interpolating a value that should be bound). **Deviation recorded:**
    PostgreSQL writes an escape string `E'…'` (backslashes doubled, then quotes) instead of the standard literal
    under a documented `standard_conforming_strings = on` assumption, because with the setting off a standard
    literal is broken out of by a backslash before a quote, and an escape string reads the same under both —
    proved against a real server under both settings. Control characters and unpaired surrogates are refused
    (compiler and dialect).
  - `ComputedFieldCheck`/`ComputedValueShape`: text result ↔ declared type, join length ≤ declared `maxLength`,
    and "reads no field" refused.
- **§2.4 plan-time refusal/rebuild for SQLite STORED columns** — being fixed now; this design only fixes
  its required *shape*.

## Rulings (controller, 28 Sep 2026)

1. The design is adopted. The maintainer's expectation is that the assistant does a request like `full_name = first_name + ' ' + last_name` right the first time; the tool surface (RFC 6902 patch applied server-side, structured violations, retry cap), the embedded instructions and the eval harness are the answer.
2. **String concatenation in a computed field is built in this PR** (a task before the assistant tasks), because the maintainer's own first request needs it: CEL `+` over two strings in the Computed profile, rendered by the dialect (`||` on SQLite and PostgreSQL), string literals rendered inline into DDL only through the dialect's literal escaping (security core: property test that no literal breaks out of its quotes). NULL semantics stated explicitly (CEL has no implicit null → a nullable operand is refused at validation unless wrapped, or rendered with `COALESCE` — decide in that task against the CEL spec, record the deviation).
3. The SQLite "add a STORED generated column to an existing table" limitation is fixed first (in progress), so the full_name case can actually apply end to end.
4. Order: SQLite stored column → computed string concatenation → patch engine → tools → instructions → eval harness.
5. **The eval gets one `InternalsVisibleTo("MMLib.Alvo.Ai.Eval")` grant** (pre-flight ruling I4): an assembly-level grant, not a public symbol, and the same pattern the Host suite's grant already follows. `PublicApi.MMLib.Alvo.Ai.verified.txt` gains exactly that one line and nothing else; §5's "does not move" is amended by it (D7).

## Deviations recorded by the implementation plan (28 Sep 2026)

From `docs/superpowers/plans/2026-09-28-f5-assistant-reliability.md`, amended where the tasks' rulings changed them,
so a later reader of this design can tell a decision from an oversight.

| # | Deviation | Reason |
|---|---|---|
| D1 | Root refusal and the op-count bound live in `PatchAdmission`, not in `JsonPatch.Apply`; **amended:** the 16 KB added-bytes budget is carried by the engine itself, counting every operation's `value` *and every subtree a `copy` duplicates* | The vendored RFC conformance suite requires root `add`/`replace` to *work* (e.g. "replacing the root of the document is possible with add"), so the root refusal is the tool's policy, checked before the engine runs. The byte budget is a bound RFC 6902 lacks, and only the engine sees the document a `copy` reads: a budget over the operations' values alone would let one small `copy` repeated fifty times duplicate the whole descriptor. Both are tested as §4.1 asks |
| D2 | `ToolViolation` carries a `code` slug (`whole-document-replace`, `stale-revision`, `attempts-exhausted`, `path-not-found`, …) beyond the Outcome sketch in §2.2 | The model and the eval branch on a slug, not on prose — the same reason `ToolError` has one. The patch codes are `JsonPatchError`'s constants, passed through `ViolationMapping` unchanged. Additive |
| D3 | `AlvoIdempotencyConflictException` is **not** caught (R7 names it) | The dry run never sends an `IdempotencyKey`, so the exception is unreachable from these tools; catching it would turn a future bug into a quiet refusal — the rule `AnsweredAsync`'s remarks state. `ManagementEscalationException` is mapped to `access` |
| D4 | A refused attempt that never produced a draft (stale base, inadmissible or failing patch) files the **current** descriptor as the refused proposal's `DescriptorJson` | No draft exists; the card still shows the refusals, and Preview shows no diff rather than a half-applied patch |
| D5 | Worked example (c) uses `test` + `replace` at `/entities/parts/rules/delete`, not §3's `add` | The member already exists in `bike-workshop`; the instructions teach "`replace` to change". RFC `add` would also succeed |
| D6 | Eval case 6 is a nightly **automation** request, not "send a webhook when an order completes" | This build *delivers* after-hook webhooks (`UnhonouredSubsystems`' `webhooks` consequence: "an endpoint an after-hook posts to is delivered to"; `bike-workshop`'s `rentals.hooks.afterCreate` uses one), so the webhook request is honourable and "no proposal" would grade a correct answer as a failure. Also, `get_capabilities` lists a warned block only when the descriptor declares it and `bike-workshop` declares no `automation`, so "sentence quoted verbatim" is not gradable there — the case grades "no proposal, the reply names `automation`" (in Slovak, `automatiz…`) |
| D7 | `PublicApi.MMLib.Alvo.Ai.verified.txt` grows by one `InternalsVisibleTo("MMLib.Alvo.Ai.Eval")` line (§5 says it does not move) — Ruling 5 | The grant is the chosen route, not a strictly necessary one: the eval uses the internal constructor's chat-client seam to count tool rounds and tokens and to read tool outcomes. The alternative — a local recording proxy between the public assistant and the provider — was rejected as more moving parts (an HTTP listener, a second parse of the provider's wire format) for the same measurement. Publishing the seam instead would hand every host a way to swap the client this package owns — the reason the Host.Tests grant already exists |
| D8 | The "first run against the maintainer's model" (§5 task 4) is the maintainer's step | It needs their endpoint and key; the harness prints the table they publish into `docs/assistant-evals.md` |
| D9 | `operations` sent as a JSON **string** is unwrapped before patching | Several OpenAI-compatible servers stringify nested tool arguments; refusing them would be a refusal about transport, not about the change. Additive |
| D10 | `Proposal.Summary` is the turn's answer text, falling back to `propose_change`'s `summary` when the answer is empty | The public record is unchanged; the fallback only fills what was empty |
| D11 | The executable-examples test is split: the patch half in `MMLib.Alvo.Ai.Tests`, the validity half in `MMLib.Alvo.Host.Tests` | The validator lives in the core, and `MMLib.Alvo.Ai.Tests` does not reference the core: the package it tests references `MMLib.Alvo.Abstractions` only, which `BoundaryArchitectureTests` constrains, and the Host suite is where the real validator already runs |
| D12 | An example's `baseRevision` is illustrative; the tests substitute the live revision | The booted revision is the host's business, and pinning it would make the example rot on the next boot change |
| D13 | Warnings reach the model only beside errors: a *valid* dry run's outcome carries none | `ApplyDescriptorAsync`'s success result has no warnings member — they surface only inside `DescriptorValidationException.Result`, which is thrown when there are errors. Carrying them on success needs an Abstractions change (a new member on the result), out of scope for this design |
| D14 | A violation's `op` is the operation whose *landed* target (`JsonPatchResult.Targets`) is the reported pointer, contains it, **or lies under it** — deepest wins, last on a tie — where §2.2 says "the op whose path is the pointer's prefix" | The validator also reports at a container (`/entities/bikes`) about a member an operation added beneath it; prefix-only matching would leave that violation with no `op`. Matching landed targets rather than written paths is what gives an `/-` append its real index |

**Two known limits of `op` attribution (D14).** Each target is recorded where the operation landed *in the document
the earlier operations left*: (1) a later operation that shifts an array (an insert or remove before an earlier
target's index) is not reflected back into that earlier target, so the earlier operation's recorded index can be
stale in the final document the validator reads; (2) a `remove` by index records the index it emptied, which a later
item — or a replacement added at the same index — now occupies, so a violation about that item can name the `remove`.
`op` is therefore **best-effort**; `pointer` is authoritative, and the instructions teach the model to fix what the
pointer names.
