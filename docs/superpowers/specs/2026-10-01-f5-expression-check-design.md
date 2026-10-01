# F5 — expression check: the first slice of automation, hooks and host functions

Status: design, 2026-10-01. Written autonomously: the maintainer asked for the work to proceed without
check-ins, so the brainstorming approvals (design, spec) are **delegated, not given** — the gate that remains is
the PR. Nothing here is merged by the author.

Inputs: [analysis](2026-10-01-f5-automation-analysis.md) and its
[adversarial review](2026-10-01-f5-automation-analysis-review.md) (read both; this file records where it follows
and where it deviates), `docs/architecture/management-api.md`, `docs/architecture/cel.md`, the F5 dashboard design.

## 1. Intent (what the maintainer asked, and what was assumed)

Asked: look at the whole cluster — hooks, automation, custom functions, scripting — and make it one coherent
thing; the dashboard needs an editor for it; a host developer must be able to define functions **from code**, and
the dashboard must offer them. Success: an operator who is not a CEL expert can author a working hook; a host
developer can add a function; an agent can discover both before it applies anything; the design absorbs later
requirements without rework.

Assumed (correct me in the PR): operators and agents are the primary users, the host developer second; the
headline "automation" promise stays empty until an evaluator exists, and this work does not pretend otherwise.

## 2. The whole, decomposed

One spec cannot hold it. Five slices, each its own spec → plan → PR, in this order (the review's order, not the
analysis's: what an operator sees first, the power tool last):

| Slice | What | Why this position |
|---|---|---|
| **A** (this spec) | `cel/check` — one Management operation, diagnostics under every expression input | Needs no function work; improves five inputs at once; is the diagnostics channel B–D reuse |
| B | #276 hooks end to end: edit in place, mutate literal and multi-field, endpoint/template pickers, a guided `when field op value` condition that emits CEL, with a switch to text | Gives the operator a usable result with no new API |
| C | Minimal `ICelFunctionCatalog` (name, one signature, summary, provenance), `AddCelFunction` for `Condition` + `Mutate`, `cel/functions`, a generated section for the assistant's skills | Host functions from code, discoverable the moment they exist |
| D | A few built-in SQL-translatable functions (`replace`, `trim`, `round`, `abs`, `size`) | Covers most "normalise/compute" needs with no host registration |
| E | Later, each earned: webhook signing (#120/#33), `automation` event-only evaluator, C# hook face, SQL templates for host functions, csx | Not before B–D prove the shape |

Deviations from the analysis, each on the review's evidence: the **guided form is the primary editor, CodeMirror
the optional advanced mode** (D4 flipped); the **catalog ships minimal** — no `Purity`, `Cost`, `Examples`,
overloads or `Evaluation` enum until a second value exists (D1 narrowed; Abstractions is frozen and growth is
hard to take back); **`cel/evaluate` is out of the first cut** (DoS and oracle risk; `policy/simulate` already
covers rules); "computed at write" is **not rejected** but answered by the before-hook `mutate` recipe (D2). Kept:
the in-process ban for `Rule` and `Computed` (authorisation is SQL `WHERE`, never a post-filter — spec §2.4), and
the dashboard has no second CEL parser: the core is the only authority.

## 3. Slice A — the problem

Every CEL input in the dashboard — a rule, a hook condition, a mutate value, a computed field — is a plain
`MudTextField` (`RulesTab.razor`, `HooksTab.razor`, `FieldEditor.razor`). A mistake surfaces only at **apply**, as
one "the descriptor is not valid" for the whole document, and one bad expression hides the verdict on all the
others. An operator learns what is wrong with a rule after typing the whole change.

## 4. Slice A — design

### 4.1 One operation, no second code path

`IAlvoManagement.CheckExpressionAsync(project, ManagementExpressionCheck request, ct)`.

* **Request** `ManagementExpressionCheck(string Descriptor, string Path, string Source)`: the working-copy
  descriptor JSON the dashboard already holds (`WorkingCopy.Json`), the RFC 6901 pointer of the slot being edited
  (`/entities/orders/rules/list`, `/entities/orders/hooks/beforeCreate/0/condition`,
  `/entities/orders/hooks/beforeCreate/0/action/mutate/status`, `/entities/orders/fields/total/computed`), and
  the candidate expression as typed.
* **Result** `ManagementExpressionVerdict(IReadOnlyList<DescriptorValidationError> Findings)`. The existing public
  `DescriptorValidationError(Path, Message, FixSuggestion, Severity)` is the diagnostic; **no new diagnostic type**.
  `IsValid` is "no `Error` finding"; warnings never block, as at apply.
* **Mechanism — splice and validate.** The service parses the descriptor, replaces the node at `Path` with the
  candidate (a string; under `/action/mutate/<field>` a `{"$cel": source}` object, the form the descriptor
  documents), runs the **same** `IDescriptorValidator` that apply runs, and keeps the findings whose `Path` equals
  the slot's pointer or sits under it. Role-literal declaration, hook phase and envelope refusal, mutate type fit,
  computed shape and the dry SQL render are therefore checked exactly as apply checks them — calling
  `ICelCompiler.Compile` alone would pass `'amdin' in @user.roles`, `old.x` in `beforeCreate`, and a computed
  constant that would become a bind parameter.
* **Not the dry-run apply.** `ApplyDescriptorAsync(DryRun)` is all-or-nothing, revision-coupled and plans a
  migration; it cannot answer per keystroke and one bad expression elsewhere would mask this one. The check reads no
  store, no revision, no runtime.
* **Errors as data.** A candidate that does not compile is a **200 with findings**, never an exception. A request
  that cannot be answered (null body, `Path` not a pointer into the descriptor, descriptor that is not JSON, over
  the size cap) is `ManagementRequestException` → 422 through the existing `Answer` ladder.

### 4.2 Contract details that decide the shape

* **Level `Viewer`**, like `policy/simulate` and `GetDescriptor`: it reads nothing the caller did not send, and the
  messages are derived from the descriptor the caller supplied. `A_finding_never_carries_stored_state_the_caller_did_not_send` pins that the role catalog is the sent one.
* **Body binds nullable** (`T? body`) and the service refuses null with 422, or the gate sweep
  (`Every_mapped_management_route_refuses_…`) gets a framework 400 ahead of the 403.
* **Size.** A viewer can POST a whole descriptor on a hot path. `Descriptor` is capped at 1,000,000 characters
  (`MaxCheckedDescriptorChars`, refused 422 before it is parsed). The package ships no rate limiter (throttling is a
  host decision, see `AlvoManagementEndpointRouteBuilderExtensions`), so the cap and the measured cost are the whole
  defence. `Path` and `Source` are not capped yet (follow-up).
* **Position is deferred.** The validator collapses `CelCompilationError.Position` into a message; a visual
  underline needs it. v1 shows the message under the input; `Position` is an additive property added when the
  editor (slice B/E) needs a caret, not now.
* **Field defaults are not wired.** A `field.default` is a literal; a `{"$cel": …}` default is refused as
  unhonoured (`UnhonouredFeatures`, `FieldBadges`). The input shows the capabilities refusal instead of a check.
* `cel/scope` is **not** in this slice: its only consumer is completion or a field dropdown, which is slice B/C.

### 4.3 Dashboard

* `ManagementGateway.CheckExpressionAsync(path, source)` — uncached (the answer depends on the working copy), through
  `AsOperatorAsync` like the rest.
* A small `ExpressionCheck` behaviour on the four inputs, **debounced** (the inputs are `Immediate`), cancelling the
  previous in-flight check. Findings are shown by a focus-free `ExpressionCheck` state rendered as
  `.a-field__problem` markup (keyed by input id, with `aria-describedby`), **not** through `FieldRefusals`, because
  `FieldRefusals` takes focus on every refusal and would steal focus from the input the operator is typing in
  (recorded deviation). A check that is slower than the debounce never shows a stale
  verdict: the latest source wins.
* Behaviour when the check itself fails (network, 403): the input shows nothing extra and apply still decides — a
  broken helper must never block editing.

### 4.4 What this deliberately forecloses, and what it keeps open

Closes nothing about functions: when slice C adds a function, `IDescriptorValidator` already knows it (the
catalog feeds the compiler), so `cel/check` reports "unknown function" and later "function X is not allowed in a
`Rule`" with no change here. Keeps the diagnostic shape stable for the guided form (B) and for the assistant, which
can call the same operation instead of guessing.

## 5. Acceptance criteria (numeric where the sources give none, so they are measured, not invented)

1. `CheckExpressionAsync` and its route exist, one route per member (`ManagementContractTests`), `Viewer` level
   (`ManagementOperationsTests`), refuse an unauthorised caller before a missing project, and are absent from the
   OpenAPI document.
2. **Parity:** for a generated corpus of valid and invalid expressions over every slot kind, the verdict's error
   set equals the apply refusal restricted to that slot (a property test, the `PolicySimulatorAgreementTests`
   pattern). No slot where check is green and apply refuses, or the reverse.
3. A candidate with one bad expression elsewhere in the descriptor still returns a green verdict for a good slot.
4. The dashboard shows the finding under rule, hook condition, mutate value and computed inputs within one debounce
   interval of the last keystroke, and never a stale one. As built: an e2e scenario per input in `ExpressionCheckScenarios`
   (Playwright; rule, hook condition, mutate value, computed, plus Escape, save-while-flagged and the unchecked
   field default), and `ExpressionCheckTests` / `ExpressionSlotsTests` (bUnit) for the latest-wins state.
5. **Cost is measured and recorded**, not assumed. `ExpressionCheckCostTests` (opt-in, `ALVO_MEASURE=1`, in
   `MMLib.Alvo.Api.Tests`) calls `CheckExpressionAsync` in-process, as the dashboard does, on a rule slot of the
   `bike-workshop` descriptor (24.0 KB): 20 warm-up calls, then 200 alternating a valid and an invalid source.
   Measured on the maintainer's Apple-silicon (arm64) dev machine, .NET 10.0.0 runtime, three runs each:

   | Build | Descriptor | p50 (ms) | p95 (ms) | max (ms) |
   |---|---|---|---|---|
   | Release | bike-workshop, 24.0 KB | 1.9 - 2.5 | 2.4 - 3.0 | 2.8 - 3.5 |
   | Debug | bike-workshop, 24.0 KB | 5.0 - 5.6 | 6.3 - 6.8 | 11.1 - 12.0 |
   | Release | 4x synthetic, 89.6 KB | 12.1 - 19.0 | 22.0 - 22.6 | 24.9 - 60.2 |
   | Debug | 4x synthetic, 89.6 KB | 9.1 - 12.3 | 18.8 - 21.6 | 21.4 - 25.0 |

   **Decision (by the rule: p95 at most 100 ms means check-as-you-type):** p95 is about 3 ms (Release) to 7 ms
   (Debug) on the real descriptor and at most 23 ms on one four times larger, so the dashboard checks as the
   operator types, with `ExpressionCheck.Debounce` = 300 ms. Check-on-blur stays the fallback if a future
   measurement on a slower host exceeds 100 ms; it changes the dashboard only. The measurement is in-process; an
   HTTP caller adds the transport and the 1 MB body parse on top.
6. `PublicApi.MMLib.Alvo.Abstractions.verified.txt` grows by exactly the one interface member and two records; each is
   justified in the PR per `alvo-architecture-rules` ("public is the contract"). The Admin baseline also moves:
   `RulesTab`, `FieldEditor` and `HooksTab` gain `IDisposable`, and `HooksTab` a public `Entity` parameter.
7. `docs/architecture/management-api.md` states the operation, why it is not the dry run, and why it is `Viewer`.

## 6. Risks

* **Cost per keystroke** — the validator also runs schema validation and the policy catalog over the whole document.
  Mitigation: criterion 5; a slot-scoped fast path is a later optimisation behind the same contract.
* **Splice fidelity** — the mutate form (`$cel` object) is the one slot that is not a bare string. A table-driven
  test covers every slot kind so a new slot cannot silently skip the splice.
* **Viewer-level amplification** — a viewer can burn validator time. Mitigation: the size cap and the measured cost
  (criterion 5); throttling stays the host's.

## 7. Open questions (none block slice A)

* Whether the finding's `Path` should be translated back to the dashboard's input id on the server or in the
  gateway — the dashboard already has `RefusalPlaces`; the plan reuses it.
* `Position` shape (offset only, as `CelCompilationError` has no length) — decided with the editor, slice B/E.

## 8. As built

Commits since the design (`git log --oneline c24c763..HEAD`):

* Core, `fbcbe57`..`d2370f5`: the slot judge (`ExpressionSlotCheck`), its hardening (duplicate keys, non-object
  descriptors, anchored mutate splice, strict array indexes), `CheckExpressionAsync` and its route, the agreement
  and raw-findings tests, the cost measurement.
* Dashboard, `6422711`..`603e785`: the focus-free `ExpressionCheck` state and gateway call, then the rule, hook
  condition, mutate value and computed inputs, and a test that the field default is not checked.
* Docs (this commit): `management-api.md` and this section.

Deviations from the plan, ruled:

* The dashboard check state is focus-free and is **not** `FieldRefusals` (which takes focus on every refusal).
* Check-as-you-type with a 300 ms debounce, from the measurement in criterion 5.
* `HooksTab`, `FieldEditor` and `RulesTab` gained `IDisposable` (to cancel an in-flight check) and `HooksTab` a public
  `Entity` parameter, consistent with their siblings; the Admin public-API baseline records it.

Deferred (not in this slice): `Position`, `cel/scope`, `cel/evaluate`.

Follow-ups: cap `Path` and `Source`; a guided form for conditions; a shared `ExpressionFindings` fragment for the
four inputs' markup.
