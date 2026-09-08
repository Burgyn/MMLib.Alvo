# API contract linting (Vacuum) + API invariant tests — design

**Issue:** [#26] `[21b] API contract linting (Vacuum) + API invariant tests`
**Milestone:** F4 — Demo from the start (the last item in the phase that is still a literal placeholder)
**Date:** 2026-09-08
**Sources:** spec §§286, 291–292, 300, 308–309, 415, 419 · analysis `baas-analyza.md` tables at 1097/1102 ·
`docs/PLAN.md` §3a · the frozen `schema/project.schema.json` · the served OpenAPI document

## 1. What this closes, and what it deliberately does not

`scripts/test-ring2:51` prints `placeholder: API invariant + Vacuum` and nothing lints the generated
document. The issue asks for two things that are the same claim measured two ways:

- **static shape** — the generated OpenAPI document obeys Alvo's own conventions (Vacuum, a Spectral-compatible
  ruleset in the repo);
- **behaviour across descriptors** — default-deny, idempotency and a consistent CRUD shape hold for *N generated
  projects*, not only for the demo. This is the hole a metadata-driven framework actually has: "works for the
  demo, breaks on another combination of fields".

Out of scope, stated so a later reader can tell a decision from an oversight: **#191** (the Data API accepts a
write with no `Content-Type`) stays with the embedded sample in #24, where `docs/PLAN.md` §3a puts it.

## 2. Everything below was measured, not assumed

Vacuum v0.30.3 was run against the repository's own document
(`test/MMLib.Alvo.Api.Tests/OpenApiDocumentTests.The_document_is_stable.verified.txt`, which is a valid
OpenAPI 3.1.1 JSON document on disk) before any of this was written. Five findings changed the design:

1. **Rules see the *resolved* document.** A rule asserting `$.paths[*][*].responses.403.$ref` is truthy fires on
   *every* operation — by evaluation time the node is the resolved response object, not the reference. So
   "every 4xx is a `$ref` into `components.responses`" is **not expressible as a rule** and is not attempted;
   the resolved shape (`application/problem+json` + the `problemDetails` schema) is asserted instead, and the
   `$ref` spelling is already pinned verbatim by the document snapshot.
2. **`field: "@key"` works with `casing` and is silently vacuous with `pattern`.** A deliberately impossible
   `match: "^ZZZ"` over `$.paths` reported *zero* violations. A rule that tests nothing looks identical to a
   rule that passes, which is why §6 makes a per-rule mutation proof part of the suite rather than a nicety.
3. **JSONPath filter expressions are unusable here.** `$.paths[?(!(@path =~ /batch/))]` and
   `$.components.schemas[?(@.x-alvo-page)]` both fail to parse ("Spectral compatibility mode"), and one
   unparseable `given` aborts the whole run. Conditional and cross-referential claims therefore live in C#
   (§5), not in the ruleset (§4) — which is also where they produce a usable failure message.
4. **`vacuum spectral-report -r <ruleset> <doc> <out>` emits a JSON array carrying `code` (the rule id),
   `severity` (0 = error), `message` and `path`.** That is the machine-readable attribution the harness needs;
   `vacuum lint`'s console output prints a rule's *description*, not its id, so exit codes alone cannot say
   *which* rule fired.
5. **Three genuine findings in Alvo's own document**, none of them known before this run — §3.

## 3. The three findings, and what each one does to the design

*Found before implementation. Three more arrived during it — §8a.*

### 3.1 `servers[0].url` has a trailing slash — fix it

With no path base the document advertises `http://localhost/`. OpenAPI 3.1.1 §Server Object is explicit that
a path is **appended** to the server URL, "no relative URL resolution", so a conforming client builds
`http://localhost//api/products`. `oas3-api-servers` is right and the document is wrong.

**Decision: fix the document** in `AlvoDocumentTransformer` (normalise a `servers[*].url` whose path is bare
`/`), keep the rule at error, and correct the value `OpenApiServersTests` pins today. The path-base case
(`http://localhost/alvo`) is already correct, so this makes the two consistent. Muting a *validation*-category
rule in order to keep a real defect is the "gate that lies" pattern this repo has already paid for once (#142).

### 3.2 `paths-kebab-case` fires on the real examples — mute it, with a reason

An entity identifier is snake_case by schema (`^[a-z][a-z0-9_]{0,62}$`), so `work_orders` and `invoice_items`
in `examples/field-service` and `examples/complex-crm` produce `/api/work_orders` and `/api/invoice_items`.
Measured: 4 violations per snake_case entity.

**Decision: `paths-kebab-case: off`.** Alvo deliberately adopts PostgREST's spelling — the path segment *is*
the entity identifier, verbatim — and rewriting it to kebab-case would be a wire-breaking change to the one
convention the framework copied on purpose. Recorded as a **deviation from spec §308's "camelCase" shorthand**.

### 3.3 Batch `DELETE` carries a request body — mute the rule, pin the deviation in C#

`no-request-body` fires twice: `DELETE /api/{entity}/batch` takes `{"ids":[…]}`. RFC 9110 §9.3.5 says a client
"SHOULD NOT generate content in a DELETE request" and that such content has no defined semantics; the reason
Alvo has `POST /{entity}/query` at all is the mirror-image argument about GET-with-body.

**Decision: `no-request-body: off`, plus a C# fact that pins exactly which routes carry a DELETE body**, so the
exemption cannot silently spread to a single-row delete — the same construction `UnhonouredFeaturesTests` uses
for known gaps. **#206 is filed** to reconsider the verb (`POST /{entity}/batch/delete`) as a
wire change, which is F6 work and not this issue's.

### 3.4 The rest of the muted set, each with its reason

| Rule | Hits on the fixture | Why it is `off` |
|---|---|---|
| `camel-case-properties` | 45 (warn) | Schema *property* names are the descriptor's field names — snake_case by contract, and renaming them would change the wire. Casing is enforced on framework-minted keys only (§4). |
| `oas3-missing-example` | 64 (warn) | Alvo emits no examples. Adding per-field-type examples is a real agent-first DX improvement and a **product change**; **#207** is filed rather than the gate being bent around it. |
| `description-duplication` | 118 (info) | One generator serves N entities, so identical prose across operations is a *property* of a metadata-driven document, not a defect. |
| `oas-missing-type` | 6 (info) | A `json`-typed field is deliberately type-free: in draft 2020-12 an absent `type` means "any", which is exactly the intent. |

Everything else in `vacuum:oas, recommended` stays on, and the gate runs at **`--fail-severity warn`** — after
the mutes above and the §3.1 fix the document is measured clean at *every* severity, so any new warning fails
the build instead of accumulating.

## 4. The ruleset — `schema/openapi-ruleset.yaml`

`extends: [[vacuum:oas, recommended]]`, the six mutes of §3, and seven Alvo rules, all `severity: error`.
Every one of them was proven to fire (§6):

| Rule | Claim |
|---|---|
| `alvo-operation-id-shape` | `operationId` matches `^[a-z][a-zA-Z0-9]*\.[a-z][a-zA-Z0-9]*$` — the `categories.list` spelling |
| `alvo-schema-key-casing` | `components.schemas` keys are camelCase |
| `alvo-parameter-key-casing` | `components.parameters` keys are camelCase |
| `alvo-response-key-casing` | `components.responses` keys are camelCase |
| `alvo-response-described` | every response carries a description |
| `alvo-response-no-store` | every response documents `Cache-Control` |
| `alvo-problem-media-type` | every declared error response is an `application/problem+json` document |

**Two recorded deviations from the spec's wording.** §308 says "camelCase" — enforced on framework-minted keys
only, per §3.2/§3.4. §308 says "RFC 7807" — the implementation answers RFC **9457**, its successor, which is
what `AlvoProblemTypes` and the document already do; the rule follows the code, not the older RFC number.

The ruleset lives in `schema/` beside `project.schema.json`: both are contracts the repo publishes about a
generated artifact, and neither is test-only.

## 5. What stays in C#, and why

Anything conditional ("*if* this operation pages, *then* its 200 is an envelope") or cross-referential
("routes = entities × 10") is a C# fact. Measured reason: the `schema` function with `if`/`then` fired on every
operation with the message ``, is missing and is required`` — unusable — and JSONPath cannot express the
predicate (§2.3).

A new `test/_shared/api/OpenApiDocumentFacts.cs` asserts, generically over `(document, entities)`:

- the path key set is exactly `entities × {"", "/query", "/{id}", "/batch"}`, and **no path segment is numeric**
  (the "no integers in URLs" rule, which no built-in covers — measured);
- every list operation documents `prefer`, `select`, `order`, `limit`, `offset`, `after`;
- every list 200 is a page envelope with `items`, `next` and `count` all present;
- every operation's error responses resolve to `problemDetails`, and `type` is under `https://alvo.dev/errors/`;
- the DELETE-with-body exemption of §3.3 holds for the batch route and nothing else;
- the component schema key set is exactly `entities × 12 shapes` plus the framework's two — the claim that
  replaced the dropped casing rule, and one a linter cannot make because it needs the entity list.

These are written fresh rather than extracted from `OpenApiDocumentTests` — that file's 1346 lines are
*fixture-specific* pins with hand-counted expectations, and rewriting it generically inside this PR would put a
working gate at risk for no gain. `OpenApiDocumentTests` gains **one** fact that runs the shared assertions over
its own document (it already links `_shared/api/*.cs`), so the fixture, the four examples and the 16 generated
documents are all judged by one implementation of each generic claim, and the fixture's detailed pins stay
exactly as they are.

## 6. Anti-vacuity — the half of the DoD that keeps this trustworthy

The DoD asks that a deliberate violation goes red. Two batteries, both mutating at runtime so no fixture rots,
and both structural rather than textual: a *textual* rename of a component key silently breaks every `$ref` to
it, and vacuum answers that with `unable to build unresolved model` — or, in one shape, a panic — so the
mutation would have been measuring the resolver rather than the rule. Both were hit while validating this design.

- **Lint battery**, in the invariant project (the one place that already resolves the binary). One mutation
  per rule id — 8 today, including one that violates a `recommended` rule to prove the extended set is still
  live. Each asserts vacuum reports **that** `code`. A test reads the Alvo rule ids out of the YAML and fails if
  any of them has no mutation, so a rule added later cannot arrive unproven — the "pin the set from outside"
  discipline, applied to the ruleset itself.
- **Behaviour battery.** The invariant bodies run a second time against a world sabotaged through
  `ConfigureServicesAfterAlvo` + `ServiceDecoration` — an `IPolicyEngine` that admits everything, an `IAlvoData`
  that forgets the idempotency record — and the assertions **must** fail. A green sabotage run fails the suite.

Vacuum exits 2 on a crash and 1 on violations; the harness distinguishes them, so a crash can never read as a
legitimate red or, worse, be mistaken for a pass.

## 7. The invariant suite

**Project:** `test/MMLib.Alvo.Api.Invariants.Tests.Integration` — the `.Tests.Integration` suffix is the repo's
only tier discriminator (`scripts/test-ring0` runs `*.Tests.dll` and excludes it by naming), and this suite
boots 16+ hosts, so it belongs in the slow tier the ring table already places before a PR. It links
`test/_shared/api/*.cs` — the same `AlvoApiWorld`, never a second copy — and needs one addition to it: an entry
point taking an **absolute** descriptor path, since generated descriptors are written to a temp directory. It
also needs an `InternalsVisibleTo` entry in `src/MMLib.Alvo/Properties/AssemblyInfo.cs`, like every sibling.

**Engine: SQLite only.** The axis under test here is descriptor variation; engine variation for the demo
descriptor is already proven on both engines by `MMLib.Alvo.Api.Tests.Integration` running the same world.
Stated as a deviation rather than left implicit.

**The generator.** CsCheck `Gen` composes a descriptor — 1–3 entities, 1–6 fields drawn from all 11 field
types, `required`/`unique`/`nullable`/`default`, a `ref` between entities, `softDelete`/`audit`, `tenancy` on
and off, and entities deliberately **with and without** `rules`. Reserved field names come from the core's own
`ReservedQueryKeys`, not from a copied list. Every descriptor is validated against
`schema/project.schema.json` before it boots, so a generator bug fails as a generator bug.

**16 committed seeds become 16 xUnit theory cases**, so a failure names its seed and prints the descriptor,
and the cases parallelise. `ALVO_INVARIANT_SEED` / `ALVO_INVARIANT_N` widen it. **Deliberate deviation from
"property-based" (spec §286):** CsCheck is used as a deterministic case generator, not as a shrinking property
runner — a shrink that re-boots a host per step costs more than the descriptor dump is worth, and a
random-seeded suite on a required PR check is a flake generator.

**Per case:**

1. the document is clean under the ruleset (§4) and satisfies the generic facts (§5);
2. an entity with no `rules` answers 403 on all ten operations to a fully-scoped authenticated key, and **no SQL
   statement is composed** — default-deny refuses before the port is reached;
3. an entity with rules completes the CRUD shape: 201 + `Location`, 200, the `{items,next,count}` envelope,
   204, then 404;
4. `PUT` twice is the same whole row, **from two different starting states** — the assertion a merge cannot
   satisfy, which is the property #105's port suite already holds for one descriptor;
5. a replayed `Idempotency-Key` writes one row, counted from the table with `CountRowsAsync`, never from a list;
6. every refusal is `application/problem+json` with a `type` under `https://alvo.dev/errors/` and JSON-Pointer
   violations.

The four `examples/` descriptors run through 1 and the generic facts as four further cases, which is how the
demo-side lint of spec §415 is satisfied for descriptors the compose stack actually serves.

## 8. Wiring

- **`scripts/ensure-vacuum`** — pinned to **v0.30.3**, resolves `uname` to the release asset
  (`darwin_arm64`, `linux_x86_64`, `windows_x86_64` — all verified to exist; the Windows tarball carries
  `vacuum.exe`), verifies the download against the release's own `checksums.txt`, unpacks into
  `artifacts/tools/vacuum/`. A `vacuum` already on `PATH` at the pinned version is used as-is, and a different
  local version warns — the k6 precedent in `scripts/test-load`.
  **Not Docker:** CI runs ring2 on `windows-latest`, whose Docker is in Windows-container mode and cannot run a
  Linux image. A suite that self-skipped there would be invisible, because CI sets
  `TESTINGPLATFORM_EXITCODE_IGNORE=8` on Windows.
- **`scripts/lint-api`** — lints the committed document snapshot with the ruleset. Needs no build and no .NET,
  so it is the cheap gate that fires when someone edits the snapshot; vacuum sniffs content rather than the
  extension, so the `.verified.txt` is linted in place (measured). Same ruleset file as the test: one
  authority, two callers.
- **`scripts/test-ring2`** — the placeholder echo becomes `ensure-vacuum` before the integration loop (which
  picks the new project up through its existing `*.Tests.Integration.csproj` find) and `scripts/lint-api`
  after it.
- **CI** needs no new job: `build-test` already runs `scripts/test-ring2` on both OSes. The comments in
  `test-ring0`, `test-ring2` and `ci.yml` that promise a placeholder are corrected, and `docs/PLAN.md` §3a
  stops listing #26 as remaining.
- **Licensing:** Vacuum is MIT and is named in `alvo-dotnet-conventions`'s carve-out for a dev/CI tool invoked
  as a separate process. Nothing ships, nothing is linked.

## 8a. What building it found

Three defects and two gaps, none of them known when this design was written. That the corpus produced them
on its first run is the argument the issue makes, so they are recorded here and not only in commits.

**Fixed in this PR, because each is a defect in the artifact #26 is about:**

1. **`servers[0].url` had a trailing slash** — §3.1, found before implementation started.
2. **A nullable enum published a self-contradicting schema.** `type` was widened to `["null","string"]`
   while `enum` kept only the declared values; the two keywords are conjunctive, so the document admitted
   null and forbade it at once — a generated client rejected a row the API legitimately returns, and no
   request could send the value the type promised. `simple-tasks` and `complex-crm` both published it.
   Vacuum's `nullable-enum-contains-null` reported it; the fix appends a real JSON `null` to the value set,
   and `A_nullable_enums_value_set_contains_null_and_a_required_ones_does_not` pins both directions over its
   own descriptor.
3. **Two lint rules were written against a single-word entity** — §4.

**Filed, not fixed, because each is a product decision or a change outside this issue:**

4. **`examples/complex-crm` cannot be applied at all** (`field.default` is refused). The real gap is that
   `ExamplesTests` validates each example against the *schema* and never against appliability, so a shipped
   descriptor can be both valid and un-bootable. **#208.** This suite pins the refusal explicitly rather
   than excluding the example silently, so the exclusion fails the moment it becomes appliable.
5. **The batch `DELETE` body (#206)** and **the absent examples in generated schemas (#207)** — §3.3, §3.4.

**And three things the generated corpus taught the suite about its own fixtures**, each now written where it
was wrong: `softDelete` is refused at apply in this build, so it is never generated; a create on a
tenant-scoped entity must *echo* `tenant_id` while a replace refuses the same member; and a decimal has to
fit its declared precision and scale.

## 9. Definition of Done, mapped

| DoD clause | Where it is satisfied |
|---|---|
| the Vacuum lint is green against the demo | §3.1's fix + §3's mutes; measured clean at every severity |
| and fails on a deliberate rule violation | §6's lint battery, one mutation per rule id, enforced complete |
| invariant tests pass for ≥N generated projects | §7, N=16 committed seeds + the four examples |
| breaking idempotency/default-deny causes a red test | §6's behaviour battery via `ServiceDecoration` |

## 10. Risks

- **Ring2 cost — measured, not projected.** The invariant project is **82 tests in 9.6 s** (20 hosts, 29
  vacuum invocations), against a stated target of 60 s. It stays well inside the tier it was placed in.
- **The snapshot moves.** §3.1 changes `servers[0].url`, so
  `OpenApiDocumentTests.The_document_is_stable.verified.txt` and one `OpenApiServersTests` expectation change
  with it. That is a reviewed event by design — the Stop-hook gate will ask the snapshot judge to justify it.
- **An entity named `delete`.** The descriptor permits it and `no-http-verbs-in-path` would flag the resulting
  route. The rule stays on: it is a genuine smell, and it makes the lint useful against a *user's* descriptor
  too. The generator avoids verb names so the suite measures Alvo, not a hypothetical user's naming.
