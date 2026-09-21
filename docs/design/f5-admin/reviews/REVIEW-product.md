# Review 1 — product and architectural honesty

Produced by an adversarial reviewer (Fable 5) with access to both the repository and the
prototype, 2026-09-21, against prototype v10. Verbatim. Four of its hardest claims were
independently re-verified against the repository before this file was written:

- `AlvoIdentityContextResolver.cs:53` — `if (!string.IsNullOrEmpty(requestedTenant) || …) return null;`
- `ManagementLevel.cs` — `None · Viewer · Developer · Admin`, no `editor`
- `ManagementEndpoints.cs` — `"/projects/{project}/descriptor"`, not `"/management/descriptor"`
- `ManagementModels.cs:81` — `ManagementPolicySimulation(Entity, Operation, Caller)`, no record id
- `IApiKeyStore.cs` — `FindAsync` + `TouchAsync` only

Already fixed in v11: the access levels are now `admin / developer / viewer` with the grants
`ManagementOperations.cs` gives them. Everything else below stands.

---

Sources read: `docs/design-brief.en.md`, `docs/superpowers/specs/2026-09-18-f5-admin-dashboard-design.md`, `schema/project.schema.json`, `UnhonouredFeatures.cs`, `UnhonouredSubsystems.cs`, `baas-analyza.md` §2.3/§2.8, `IAlvoManagement.cs`, `ManagementModels.cs`, `ManagementEndpoints.cs`, `ManagementOperations.cs`, `CapabilityReport.cs`, `RoleCatalog.cs`, `IAlvoUserStore.cs`, `AlvoUser.cs`, `IApiKeyStore.cs`, `ApiKeyRecord.cs`, `AlvoIdentityContextResolver.cs`, `TenantResolver.cs`, `docs/architecture/{management-api,data-api,cel,host,events}.md`, `examples/field-service/field-service.alvo.json`; and every line of `app.js`, `data.js`, `proposed.css`, `index.html`.

The prototype's `data.js` header claims every value is "taken from the repository… verbatim". That claim is false in at least nine places listed below; either make it true or remove it, because a reviewer who trusts it will not check.

## 1. Ranked, worst first

### 1. FACTUALLY WRONG — the Data screen cannot serve a tenant-scoped entity for a signed-in human
`src/MMLib.Alvo.Identity/Internal/AlvoIdentityContextResolver.cs:17` returns `null` when any tenant is requested (`!string.IsNullOrEmpty(requestedTenant) → return null`) and the principal it mints carries no `Tenant`. `ManagementSimulatedCaller` doc: a null tenant "denies on a tenant-scoped entity, as production does". So the operator browsing `customers` or `work_orders` through `/api/*` "as jana, admin" (design §2.4, D4) gets nothing; only `regions` (global) is browsable. The tenant switcher, "Records 26,532 across both tenants", "Multi-tenancy: enabled — 2 tenants" and the create form's implicit tenant (scoped create must echo `tenant_id`) all rest on a tenant the dashboard user does not have and cannot ask for. There is also no tenant list anywhere in the build (tenancy is resolved per request; no registry).
**Consequence:** in every multi-tenant project — the one the example exists to demonstrate — the Data section is dead for every scoped entity on day one.
**Do:** this is a design gap, not a prototype fix. Decide how a cookie principal acquires a tenant for browsing (an explicit `X-Alvo-Tenant` the identity resolver accepts for admins, or a per-session tenant choice) and where the tenant list comes from (none exists — probably `SELECT DISTINCT tenant_id` is exactly the bypass D4 forbids). Until decided, the prototype must show the Data screen refusing scoped entities honestly.

### 2. FACTUALLY WRONG — the simulator as drawn is a second policy evaluator, the thing §2.2/§6.1 forbid
`ManagementPolicySimulation` has **no record id** ("There is deliberately no record id" — `ManagementModels.cs`; `management-api.md` §"The record-id arm of the simulator"). `ManagementPolicyVerdict.Allowed` "is not 'this caller will see rows'" — a role predicate is handed back, not evaluated. The prototype's "Try it on someone" picks a real record, returns allowed/refused per operation and explains "this record's status is completed" — computed by `evaluate()` in `app.js`, a client-side reimplementation. Built this way it fails design §6.3 criterion 4 by construction, and the D18 rationale ("the simulator answers about a real record") is precisely the arm the design rejected under D4.
**Do:** correct the prototype. Render what the API returns: `Using`/`WithCheck`/`TenantScope` CEL, `HiddenFields`, `ReadOnlyFields`, `DenyReason`, and the truth that a role-only rule is "200 with an empty page, not 403" (`data-api.md` §"The RLS surprise"). A "check this row" affordance is honest only if it calls the Data API under the *simulated* caller's credential, which the operator does not hold — so drop it or make it "open this row as yourself and compare".

### 3. FACTUALLY WRONG — the wizard's first step describes a product that does not exist
Copy: *"Alvo refuses to start with the default password still set. This is the only screen that can change it."* and step 1 "Create the first account". `host.md` §"No default credential" and §"The bootstrap administrator": there is no default password; the bootstrap admin is `Alvo__Admin__BootstrapEmail` + `Alvo__Admin__BootstrapPasswordFile`, seeded before the dashboard can be reached, a direct value is refused outright, and seeding never resets an existing password. Design §3.5 says the same. The account already exists when the wizard opens.
**Do:** step 1 is at most "sign in as the bootstrap administrator" plus an optional password rotation (host.md: "rotating a live administrator's credential is a dashboard operation"). Keep step 2.

### 4. FACTUALLY WRONG — the access levels are misnamed and mis-granted
`data.js ACCESS_LEVELS` = `admin / editor / viewer`. The frozen schema (`project.schema.json` `access`) and `ManagementLevel.cs` have `admin / developer / viewer`; there is no `editor`. Grants are also wrong: `ManagementOperations.cs` gives `developer` **apply and rollback** (prototype: "May not apply"), and `viewer` **simulate** (prototype omits it). The predicates map `technician → viewer`, which is the author's own open question — fine — but the level table itself would be refused at apply.

*(Fixed in v11.)*

### 5. FACTUALLY WRONG — every Management route in the prototype is missing `/projects/{project}`
`AI_TOOLS`, Overview ("GET /management/capabilities"), Preview ("PUT /management/descriptor?dryRun=true"), Transfer, and every AI "read:" source line. Actual (`ManagementEndpoints.cs`, `management-api.md`): `GET {m}/projects/{p}/schema|descriptor|capabilities|revisions`, `PUT {m}/projects/{p}/descriptor`, `POST {m}/projects/{p}/policy/simulate`. Only `/info` and `/projects` are unprefixed.

### 6. FACTUALLY WRONG — the engine is displayed in three places and the API cannot supply it
Sidebar "revision 7 · PostgreSQL", Overview badge "PostgreSQL 16", Settings "Database: PostgreSQL 16". `ManagementInfo` has `DataProvider` (a type name: `EfAlvoData`), and `management-api.md` §"`info` reports the data provider, not the engine" explains why it *cannot* name the engine (provider-model principle). Show `dataProvider` or nothing.

### 7. FACTUALLY WRONG — "verbatim" refusal/warning texts are edited, incomplete, and rewritten
- `field.validation` fix drops "once #22 lands".
- `rollup.where` fix drops the final sentence ("A partial implementation is deliberately not offered…").
- `JSONata` consequence drops ", which is indistinguishable from a bug in the consumer."; fix drops "A partial JSONata implementation is deliberately not offered… Tracked in #149."
- `webhooks` warning drops "(7.1), nor is the payload projected per endpoint (#152)".
- **Six refusals are absent from `REFUSED`:** `email.data`, `bodyFile`, `trigger.event` (wildcard), and the three action types `function`, `http.call`, `entity.update` (`UnhonouredFeatures.EveryRefusal`). Integrations' "New endpoint"/"New template" and the hooks tab's "Add a hook" would need every one of them as inert controls; none is drawn.
- Integrations and the hooks tab **rewrite** the warned sentence ("No Standard Webhooks HMAC header is sent, so the receiver cannot verify… Treat the endpoint as unauthenticated until signing lands") instead of serving `WARNED.webhooks`. `IAlvoManagement.GetCapabilitiesAsync` remarks and design §2.3: "served verbatim and never rewritten in the UI."
**Do:** the prototype must render `capabilities.refused[*]`/`warned[*]` by slot key, never hold copies. Treat `data.js REFUSED/WARNED` as fixtures generated from the endpoint, not authored.

### 8. FACTUALLY WRONG — Overview "Declared, and not running yet" lists all five blocks whether or not the descriptor declares them
`CapabilityReport.Project()` projects `UnhonouredSubsystems.All`, not `DeclaredBy(descriptor)`. `field-service.alvo.json` declares none of them (no `automation`, `templates`, `webhooks`, `functions`, `dynamicEntities` — the prototype's webhooks/templates/hooks were added in `data.js` and are not in the example, contrary to the header). Either the UI intersects `warned` with the descriptor's top-level keys, or the heading changes. Note also that runtime apply emits **no** warning line at all (`UnhonouredSubsystems.cs` remarks, #83) — so this panel, intersected, is the only place a dashboard-first operator will ever learn it. That makes it more important than drawn, not less.

### 9. FACTUALLY WRONG — API-key issuance/revocation over a store that has neither
`IApiKeyStore` is `FindAsync` + `TouchAsync`. `ManageApiKeys` has no HTTP route (`management-api.md` §"The three admin operations with no route"). "New key", the key-reveal modal ("Alvo stores a hash… alvo_sk_…"), and "Revoke" are the Invite defect again. Also: the real credential is `<keyId>.<secret>` (`ApiKeyContextResolver`), not `alvo_sk_…`; `ApiKeyRecord` carries `User`, `RoleNames`, `Tenant`, `ExpiresAt`, `RevokedAt` — the prototype shows none. **Roles are the security-relevant attribute**: per design D7 a key's roles decide both its Data-API rules *and* whether it reaches management. A key UI that shows scopes and hides roles hides the escalation path.

### 10. FACTUALLY WRONG — Data-API facts the prototype states
- *"keyset paging, so page 900 costs what page 1 costs"* — `data-api.md` §"The cost, stated honestly": "It is **not** a claim of depth-independent paging cost. Do not write that here or anywhere else." Say "stable under concurrent writes" instead.
- **"Previous"** button — the cursor is opaque, forward-only (`after`); `offset` cannot be combined with it. Previous exists only as client-side history.
- The data-browser error state discloses *"internal_notes is hidden, so it… can be named in no filter"* with slug `/problems/unknown-field`. `data-api.md` §Position A: a hidden field's **name is not public**; the refusal must be indistinguishable from an unknown key. The prototype's most "honest" error is a disclosure. Slugs are `https://alvo.dev/errors/<slug>`; there is no `unique-violation` (unique is `409 conflict` with violation code `unique`) and no `unknown-field` slug in the catalogue.
- Record form excludes `internal_notes` (hardcoded) — `data-api.md` §"A `hidden` field is writable, by design". Hidden fields belong on the create form; only `readOnly`/`computed`/`rollup` do not. The field-drawer toggle "Never appears in a response or in the schema" is also wrong for required+hidden (published in write schemas — the prototype says so itself two screens later).
- *"Alvo also maintains id, created_at and updated_at"* (fields tab and New-entity modal) — only `id` is unconditional; `created_at/created_by/updated_at/updated_by` are trait-scoped to `audit: true` (schema `$defs/entity/audit`; `data-api.md` §managed columns). The New-entity toggle "Keep a version on every row" writes `audit: true`, which also adds four columns it does not mention.
- Field-drawer format chips include **`url`** — built-ins are `email`, `uri`, `phone`; an unknown name is refused fail-fast at apply (schema `format` description). The list must be `[built-ins] ∪ descriptor.formats`.
- Sample curl sorts by `scheduled_for` (nullable) — legal, but `data-api.md` §"Sorting over nulls" calls it "a reason to sort by a required column". The sample teaches the slow path.

### 11. FACTUALLY WRONG — the sample hooks and the AI's proposed rule would be refused by the compiler
- `old.completed_on == null` — `cel.md` deviation 10: `==`/`!=` against `null` is **rejected**; use `!has(old.completed_on)`.
- `mutate: completed_on ← today` — no `today`; `now()` returns a `Timestamp` into a `date` field (type check).
- AI transcript proposes `('tech-' + region_id) in @user.roles` for a `get` rule — arithmetic is ✗ in the `Rule` profile (`cel.md` table), `region_id` is a uuid ref, and it recommends a role per region, which is the **role explosion** `baas-analyza` §2.3 "Pozor na" warns against by name ("ABAC… je často lepšia cesta než ďalšia rola"). The assistant demo shows Alvo's AI hallucinating against Alvo's own compiler — a preview of the real risk (see §3).

### 12. FACTUALLY WRONG — smaller items
- `RolledBackFrom` = "the revision this one restored" (`ManagementRevision`); `data.js` sets 4 and the UI prints `rolledBackFrom - 1`. Wrong data, hack display.
- Assign modal offers **`authenticated`** as an assignable role; `AlvoIdentityContextResolver.Minted` appends it automatically — assigning it is a no-op the UI says is a grant. (Offering `admin` is fine: it is in `RoleCatalog`.)
- `AlvoUser` = `Id, Email, RoleNames, IsDisabled`. The Access table shows display names and a "Last seen" column that no port supplies, and omits `IsDisabled` — a locked-out user resolves to `null` and should be visible as such.
- Role "Remove" is guarded by entity rules only; `access.*` predicates also name roles and are compiled at apply (schema `access` description). Removing `technician` breaks `viewer` and the apply is refused.
- `parseCel` accepts roles matching `[a-z_-]+`; `$defs/identifier` allows digits (`tier2`). Such a rule falls to "raw" and the matrix column goes inert. *(Partly fixed in v10: the pattern now allows digits. Re-verify.)*
- Export modal titled "Revision 7, exactly as applied" shows a single-entity fragment produced by re-serialisation, not `DescriptorJson`.

## 2. Unmet requirements (cite the source)

**U1 — No drift, §6.3 criterion 3 / spec :353.** The editor's "descriptor pane" and export are produced by `fieldObject()` — a typed JS projection that drops `nullable`, `index`, `renamedFrom`, `default`, `validation`, entity `storage`/`realtime`/`renamedFrom`, index `unique`, every `x-*` key (schema guarantees passthrough), and **rewrites a CEL-valued `hidden`/`readOnly` to `true`** (`if (f.hidden) o.hidden = true`). If the Razor editor is built this way it silently narrows any descriptor it touches. The editor must mutate the stored JSON document (the design's own rule: "export is `DescriptorJson`, not a re-serialisation") and the pane must render that document with the touched lines marked. Role *membership* is the one clickable thing legitimately outside the descriptor — the prototype says so; keep that.

**U2 — Policy editor with live validation (§2.8 must-have).** Rules fail fast at *save*; the CEL editor shows a token list but no compile diagnostics. Wire `PUT ?dryRun=true` to the raw editor on blur and render the structured error inline (the design's own "errors are not toasts").

**U3 — Self-promotion refused only in the UI (§2.3 acceptance: adversarial test).** `IAlvoUserStore.SetRolesAsync` has no such rule; `ManageUsers` has no route. Server side, or it is decoration.

**U4 — Role change audited with before/after and actor (§2.3 acceptance).** #42 is F7. Author's open question — my answer: change them and state plainly on the screen that nothing records it; refusing makes the product unusable. But file it against the F5 acceptance list as a known miss.

**U5 — Apply conflict and provenance.** `If-Match` is required (428), a stale revision is 412 (`management-api.md`), `ManagementApplyRequest` carries `Author`/`Reason`. The prototype has no 412 state ("somebody else applied revision 8 while you edited") and no Reason field — yet History shows a reason on every row. Two admins colliding is the invariant the brief names; day one in any team.

**U6 — Second human with `providers: ["local"]`.** D8 ("no Invite, people arrive by signing in") is correct about the port and false about the product: with local ASP.NET Identity nobody *can* sign in until an account exists, and only the bootstrap admin is seeded (`AlvoIdentityBootstrap.CreateAsync` uses `UserManager.CreateAsync` — the capability exists in the package, not the port). Either `IAlvoUserStore`/the Identity admin surface gains create/invite, or the Access screen must say "only OIDC users can appear here". As drawn, a local-auth project can never have a second administrator.

**U7 — Honoured features with no control.** `computed` and `rollup` are honoured (they left `UnhonouredFeatures`) but the drawer renders them read-only; there is no way to create either. "Add a hook", "Add index", "New endpoint", "New template" are buttons with no editor. A button whose click has no output is the same defect class as a refused control.

**U8 — Keyboard operability (§6.3 criterion 6; design §5.5).** Only ⌘K and Esc are wired. `span role="switch"`/`role="checkbox"` without `tabindex`, `<tr data-act>`/`<td data-act>` rows, and no j/k, `/`, `g`+letter. Acceptable for a drawing only if the plan lists them; today nothing does.

**U9 — Wide entities.** The grid hand-picks 7 columns per entity; a 30-field entity needs a column chooser mapped to `select=`. Not drawn.

**U10 — i18n-ready (§2.8).** `roleWord` pluralisation and every sentence are hardcoded English string-builders (`sentenceOf`). Fine for a prototype; the Razor version must not inherit the pattern.

§6.3 scorecard as drawn: 1 (375 px) plausible but unverified — matrix scrolls inside `.a-matrix-wrap`, grids fall back to cards; 2 ✓; 3 ✗ (U1); 4 ✗ (#2); 5 ✓ (D6 taken; `--faint` #6b7180 on white ≈ 4.9:1); 6 ✗ (U8).

## 3. Scope and shape

**Exclusions — all six are right.** Agree on each reason. Add to the list: the "Previous" page button; the tenant switcher until #1 is designed.

**Wrong inclusions:**
- **The assistant.** The config-key argument is technically sound (`Alvo:Ai:ApiKey` is like a connection string), but it makes `baas-analyza` §2.8's acceptance criterion — "prepnutie providera je len zmena connection v UI… kľúč je v secret store" — unmet by construction, and it is the largest drawn surface with zero code behind it. Every AI transcript in the prototype asserts something the repo refutes (#5, #11). The *drawer shape* (proposes a diff, exits through the same dry run, never applies) is good design and worth keeping as a design; ship it out of F5 as the written design says. If it stays, it must be gated on `GET /info` reporting an AI connection and its transcripts must be regenerated from real calls.
- **API keys** (#9), **Delete project** (no route; design §4.2 says LIVE, `management-api.md` says not in #212 — the design and the code disagree; fix the design), **engine badge** (#6), **display names / last seen** (#12).

**Missing that a developer needs on day one:** U5 (412 + Reason), U6 (second user), #1 (tenant for browsing), disabled users, key roles/tenant/expiry, the sign-in screen and the "you match no level" state (author's open question — answer: say so at sign-in, listing the three predicates; silent default-deny is the first support ticket), a column chooser (U9), and the intersected warned panel (#8) as the only runtime-apply warning an operator will ever see.

## 4. Where it breaks at 40 entities × 30 fields, 2,000 users, 12 roles

- **Entity bar** (`.a-entitybar`, flex no-wrap): 40 chips overflow horizontally; needs typeahead past ~8.
- **Model map** (`entityMap`): fixed 256 px boxes stacked per ref-depth column → a 3,000+ px SVG with wires crossing stacked boxes. Draw the selected entity ± 1 hop, not the world.
- **Access → People**: `IAlvoUserStore.ListAsync` has no paging or search — 2,000 rows in one render. **Port-level** gap before the screen is real. Same for `ListRevisionsAsync` after a year of CI applies.
- **Simulator**: "Signed in as" is three hardcoded callers; needs a user search plus role-set composer. "Looking at" is a `<select>` of every record. Moot until #2 is fixed.
- **Matrix + sentences**: 3 built-in + 12 declared + one owner row per uuid field ≈ 20 rows — fine; `sentenceOf` with 12 roles produces a 12-clause sentence — collapse past 4.
- **Ref picker**: `ilike` on "the display field" — the descriptor has no display-field concept; needs a heuristic or an `x-` hint. The `uuid` "technician" `<select>` lists users the Data API cannot list and a dispatcher may not.
- **Command palette**: items are hardcoded per entity; 40 × 3 generated items need fuzzy search.
- **Data grid**: 30 columns with no chooser (U9). Bulk actions: only the loaded page; a filter-wide delete has no batch-by-filter endpoint (batch is by id).
- **Overview "Your entities"**: 40 rows × 2 buttons; cap and search.

## 5. Deviations from the written design (the `#/notes` list)

Justified — amend the design: **D1** Schema above Data; **D7** Access split by speed (design §4.2 should say membership lives in the identity store, not "users and roles LIVE"); **D13** no provider picker; **D14/D15** roles×operations matrix — the best decision in the prototype, correctly distinguished from the rejected teams matrix; **D17** branch conditions with raw fallback; **D21** read-only map; **D22** API tab (fix its facts, add `PUT {id}` and the batch routes); **D23** On-write tab (with valid sample hooks); **D24** Integrations page (with verbatim wording and inert refused slots); **D28** wizard creates no entity; **D29** = design D5.

Correct the prototype: **D8** no Invite (true of the port, false of the product — U6); **D18** simulator over a real record (#2); the assistant in F5 (above); the wizard step 1 (#3); the "editor" level (#4, fixed in v11).

Neither — decide: Integrations, an API tab, and Assistant-in-Settings are new routes not in design §4.2; add them to the route map with their data source column, since that table is where "which screens this build can honestly serve" is recorded.

## 6. Genuinely right — leave alone

The two-class "not yet" treatment (nav separator, Not-yet pages quoting the table, inert refused controls in the drawer — fix wording only). The facets table from `$defs/field` — I checked all nine `if/then` rules; it is exact, including "seven types render nothing, correctly". Built-in roles `anon/authenticated/admin` (matches `RoleCatalog`). Roles×operations matrix with the amber `anon` warning. Two-speed Access split and the inert-role treatment (assigned ∩ declared, fail closed — matches `Minted()`). Self-change refusal as a rule (make it server-side). Read-only model map. Entity bar as an idea. Descriptor pane beside the editor (as a rendering of the real document). "No rule — refused for everyone" default-deny language everywhere. The technician "sees a shorter list, not an error" sentence (the RLS surprise, stated correctly). Configuration history with compare, restore, and typed-name confirmation on destructive plans. No admin bypass. The hidden+required explanation on the create form. CloudEvents claim (v1.0.2 pinned). Health probe paths. Sticky pending bar, inline RFC 7807 error block shape. D6 accent contrast. Scopes-don't-gate-management note in Settings — but then show key roles.

---

Bottom line: the visual and interaction decisions are mostly right and the honesty *framework* is right; the honesty *content* is wrong in about a dozen places because `data.js` was authored rather than derived. Three of the findings (#1 tenant, #2 simulator, U6 second user) are not prototype fixes — they are gaps in the F5 design that the drawing exposed, and they should go back into `docs/superpowers/specs/2026-09-18-f5-admin-dashboard-design.md` before any sub-issue plan is written against it.
