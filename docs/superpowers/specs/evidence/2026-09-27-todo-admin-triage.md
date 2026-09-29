# Triage of the open items in docs/todo-admin.md, at ba4f3b0 (branch f5/ai-agent, PR #264)

Read-only. Every status below was checked against the code at HEAD, not against the todo's own prose.
Paths are relative to the repo root. **SC** marks a security-core item (rule engine, CEL, tenancy,
auth/RBAC, identity), which needs `alvo-security-core-review` and a `/security-review` run by the
maintainer. "Abs+" means the public surface of Abstractions grows, so a `PublicApi.*.verified.txt`
grows and each new symbol has to be justified under `alvo-architecture-rules`.

## 0. Summary

| Status | Items |
|---|---|
| done but unticked | **none**. The closest is #269, where the entity half is done and the Fields-list half is not. |
| partially done | #265 (7), #267 (9), #269 (11), #271 (13), 29, 42 |
| open | #268 (10), #270 (12), 21, 25, 26, 27, 31, 32, 36 (latent), 37, 38, 39, 40, 41, 44, 45, 46, 43 (the part still open) |
| not in this PR's reach | 33 (#272, milestone F7) and 34 (#273, milestone F5, size L: it needs new Management endpoints). Both are filed already; the todo should link them and nothing more. |

Two corrections to what the tracker says:
- **#265 says the schema declares "four semantics". It declares three**: `restrict`, `cascade`, `setNull` (`schema/project.schema.json:654-661`, `OnDelete.cs`).
- **#267's premise is stale, as §8c already says.** `FieldSchema` still has no `Hidden` or `ReadOnly` (`src/MMLib.Alvo.Abstractions/Schema/FieldSchema.cs`), but `DescriptorLens.Masks/Locks` (`src/MMLib.Alvo.Admin/Internal/DescriptorLens.cs:127-146`) already read both from the descriptor. So the read half needs **no** Abstractions change.

## 1. Per item

### §5e 7 — #265: nobody chooses a ref's `onDelete` (milestone F5, labelled bug)
- **(a) Partially done.** The value is still written with no control: `FieldFacets.cs:451` `facets["onDelete"] ??= "restrict"`. The silent part is gone, though: the editor now shows a note saying `restrict` is written and that choosing another value "is #265" (`FieldFacets.Notes.cs:26-29`). The Fields tab and Relationships show a badge (`FieldBadges.cs:86-89`, `Relationships.razor:33,60`). The core honours all three values: `DescriptorToSchemaMapper.cs:510-514` maps to EF, and `DescriptorModelBuilder.cs:272-273`.
- **(b) Done means:** a chip group with the three values next to "Points at", visibly defaulting to `restrict`, with one sentence per value on what the database does. Also:
  - **`setNull` beside `required` (or `nullable: false`) is refused** in the editor. Nothing in the core refuses it either: `DescriptorValidator` has no `onDelete` check. On PostgreSQL the foreign key then fails at delete time (the exact outcome is unverified). That needs a core refusal at validation too, with a structured violation that names both facets.
  - **The `cascade` sentence says what it bypasses.** A foreign-key cascade deletes the children inside the database, below Alvo's own pipeline, so the children's delete rules, `before/afterDelete` hooks and outbox events are not run for them. This is unverified and needs a test. It is precisely the thing an operator who is one click from `cascade` needs to read. Check `baas-analyza.md:155`, which covers app-level cascade for dynamic targets.
  - An e2e test chooses `cascade`, applies, and reads the badge.
- **(c) Size:** S for the Admin part, plus S in the core for the `setNull`/`required` refusal. No Abstractions change. Schema: optional `if/then`; `required` + `setNull` can be expressed in JSON Schema.
- **(d) F5.**
- **(e)** None. Put it in the same batch as the other field-editor facets (B3).

### §5e 9 — #267: `hidden` and `readOnly` (milestone F6)
- **(a) Partially done.**
  - The Data screen honours both, through `DescriptorLens.cs:127-146`, `FieldMasks` and `FieldLocks`.
  - The field editor lists them as undrawn and kept, because `_drawn` in `FieldFacets.Notes.cs:10-14` does not contain them (§8d 17).
  - The **Fields tab badges neither** (`FieldBadges.cs:31-108`), and nothing can edit them.
- **(b) Done means:**
  - **Read half:** `hidden`, `hidden (conditional)`, `readOnly` and `readOnly (conditional)` badges on applied rows and on staged rows. Read them through `DescriptorLens`, or from the working-copy JSON for `PendingSchema`, and route both through `FieldBadges`.
  - **Edit half:** a three-way control for each (off / always / CEL), with the CEL input reusing the rules editor's.
  - The `required`+`readOnly` refusal already exists (`FieldFacets.cs:477-499`) and has to keep working.
  - Spec: `alvo-specifikacia.md:328` and `baas-analyza.md:257` put field-level hidden/read-only **in the rule engine**, so the edit half is **SC**.
- **(c) Size:** read half S (Admin only). Edit half M, and SC because it authors policy.
- **(d)** Filed as F6 debt. The read half is cheap enough to fold into this PR. The CEL edit half can stay F5 as part of the rules builder (#28), or be refused on screen with a pointer to #267.
- **(e)** The edit half pairs naturally with #270 (both author policy). It is independent of the read half.

### §5e 10 — #268: the project's identity (milestone F5)
- **(a) Open.**
  - Nothing reads `branding` anywhere in `src/`. `UnhonouredSubsystems.cs:64-73` and `CapabilityReport.cs:46-54` record, deliberately, that it is neither honoured nor warned, "and the day the dashboard renders it, nothing changes here either".
  - The project `description` has no reader.
  - A field's `description` is carried by `FieldSchema.Description` (`FieldSchema.cs:30`) and read by `PendingSchema.cs:94`, but `FieldBadges` and `Fields.razor` never render it.
- **(b) Done means:**
  - The shell title and logo come from `branding.title`/`logoUrl`, with the Alvo mark as the fallback.
  - The Overview shows the project description.
  - Each Fields row shows its description.
  - The entity header, project and field descriptions can be edited through the working copy. The header already shows the entity description read-only.
  - **The logo URL is guarded:** only `https:` or a same-origin path. A descriptor must not be able to make the admin shell load an arbitrary `http:` or `javascript:` URL. Check the CSP `img-src` in `docs/architecture/host.md`.
- **(c) Size:** S for reading, S for editing. Admin only, no Abstractions change. This is also the answer to item 27's metadata keys, see below.
- **(d) F5.**
- **(e)** It closes the `description`/`branding`/`formats.*.description` part of item 27.

### §5e 11 — #269: refusals reach only some screens (milestone F5)
- **(a) Partially done. 66817f6 and c558a5a closed most of it.**
  - `RefusalPlaces` (`src/MMLib.Alvo.Admin/Internal/RefusalPlaces.cs:45-72`) maps every published slot to the screens that show it.
  - `entity.softDelete` is rendered on the header (`Entity.razor:152-160`) and pinned by `RefusalPlacementScenarios`.
  - A slot the map does not place shows on the Overview, and an e2e test fails when one appears.
  - **Still open:**
    1. The Fields **list** shows no refusal (`Fields.razor:23-26` badges only `FieldBadges.Of`). A staged field that carries `validation` or a `$cel` default looks ordinary until it is opened. An applied field cannot carry either, because the apply refuses them.
    2. `RefusalPlaces.cs:36-40` defers "an owner or area on `ManagementRefusedFeature`" to #269. That decision is still unmade.
- **(b) Done means:**
  - A `refused` badge on a Fields row, read from the working-copy JSON (FieldSchema cannot carry `validation`), for the keys the `field.*` slots name.
  - A recorded decision on the owner field. **Recommendation: keep the map in Admin and do not add the field**, because it would grow Abstractions for one consumer. The map is backed by a fail-loud e2e test. Record it as a deliberate deviation in `RefusalPlaces`' remarks and close #269.
- **(c) Size:** S, Admin only.
- **(d) F5.**
- **(e)** Shares the row renderer with #267's read half and #268's field description. Do all three in one pass over `FieldBadges`.

### §5e 12 — #270: the read-only halves of Access (milestone F5)
- **(a) Open.**
  - The subtitle still promises that "a change to either waits for an apply" (`Access.razor:14`).
  - The Management levels and Role catalogue panels render and offer no control (`Access.razor:115-158`).
- **(b) Done means one of two things. Choose one; they are not both needed.**
  - **Honest (S):**
    - The subtitle says both blocks are descriptor-only.
    - Each panel says where to change it: Import, or the assistant's proposal, then Preview.
    - A link goes to Preview.
  - **Wired (M, SC):**
    - A role catalogue editor: add, and remove, where removing a role that a rule, a level or a member names is refused.
    - A CEL editor for each of the three levels, compiled against `CelProfile.Access` at apply (#146).
    - Preview says loudly that an apply touching `access` re-qualifies to admin. The panel says so already.
    - An e2e test: an operator narrows the `developer` level, applies, and a developer is refused.
  - Spec F5 scope names "RBAC dashboardu" (`alvo-specifikacia.md` §5).
  - **Recommendation: the wired version.** It is F5 scope, and the lock-out risk is covered by the admin re-qualification.
- **(c) Size:** S for honest, M for wired. SC, because it touches management authorization and CEL. Admin only, through `WorkingCopy`.
- **(d) F5.**
- **(e)** It pairs with #267's edit half into one "policy authoring" batch.

### §5e 13 — #271: five blocks with no screen (milestone F5)
- **(a) Partially done.**
  - `tenancy.enabled` no longer misdraws a pending entity: `PendingSchema.cs:54-73` resolves the tenancy, which was §8d 22. It is still neither **shown** nor editable.
  - `dynamicEntities` is shown on the Overview through `DeclaredLimits` (§8c).
  - `formats`, `auth.providers` and `realtime` appear nowhere. A grep of `src/MMLib.Alvo.Admin` for them finds only the unrelated `AdminInterop`/`SignInLayout`.
  - `field.format` is not in `_drawn`, so it is unauthorable.
- **(b) Done means:**
  - A "Declared, not editable here" panel. Settings or Overview, reusing Integrations' pattern.
  - `tenancy.enabled` is shown, with the sentence "an entity that declares no tenancy is scoped/global". It is also editable as a switch: this is honoured, a WorkingCopy write, S.
  - `formats` are listed, with an editor (name + pattern). `formats` is honoured (`CapabilityReport.Honoured`).
  - A `format` picker in the field editor, over the built-ins and the declared names. That closes the "unreachable from both ends" half of the complaint.
  - `auth.providers`, `realtime` and `dynamicEntities` are read, with the build's own sentence (see 27) and a link to #36, #38 and #41 (all milestone F7).
- **(c) Size:** M as a whole. The panel is S, the formats editor and picker are S-M, the tenancy switch is S. The switch is SC-adjacent: flipping `tenancy.enabled` changes how every undeclared entity is scoped. Preview must show that as a plan step, so check what the apply does to existing entities without `tenancy`.
- **(d)** F5 for the surface. The subsystems themselves are F7: #41 dynamic, #36 auth, #38 realtime. **Build none of them.**
- **(e)** It depends on 27 for the sentences on providers and realtime. It absorbs 29's "`dynamicEntities` shown for `enabled: false`".

### §8d 21 — `storage: dynamic`
- **(a) Open.**
  - The mapper drops the entity silently: `DescriptorToSchemaMapper.cs:44-46` filters through `IsPhysical` (:172).
  - `UnhonouredSubsystems` warns only when `dynamicEntities.enabled == true`, so an entity with `storage: dynamic` in a descriptor that does not enable the block gets no warning at all.
  - `PendingSchema.Entity` (`PendingSchema.cs:60-67`) reads no `storage`, so the header badges "physical table" (`Entity.razor:167`).
  - `SchemaList.razor:112-116` says "not applied yet" forever.
- **(b) Done means:**
  - **Core:** any entity with `storage: dynamic` is a warned subsystem. Either widen the `dynamicEntities` predicate, or add a sibling entry with the consequence "this entity is not created, because the dynamic driver is F7". Pinned by a test.
    - Refusing would also be defensible under `UnhonouredFeatures`' rule: a write path the author believes exists does not exist. But the table's own rule is to warn when nothing is wrongly **permitted**, and nothing is permitted here, so warn. Record that reasoning.
  - **Admin:** `PendingSchema` reads `storage`. The list row and the header say "dynamic — not built in this build (F7, #41)" instead of "not applied yet", and the header badge reads "dynamic".
- **(c) Size:** S in the core (internal table) plus S in Admin. No Abstractions change: `EntityStorage` exists already.
- **(d)** The capability is F7 (#41). **This item is the notice, not the driver.**
- **(e)** Same batch as 27 and #271 (the capability surface).

### §8d 25 — data writes are last-writer-wins
- **(a) Open.**
  - `DataGateway.UpdateAsync/DeleteAsync` (`src/MMLib.Alvo.Admin/Internal/DataGateway.cs:119-130`) pass no precondition.
  - The port takes one already: `IAlvoData.cs:433` `AlvoPrecondition? precondition`, and `AlvoPrecondition(DateTimeOffset Version)` (`AlvoPrecondition.cs:31`). `EnsureSupported` refuses the precondition on an entity with no version column.
- **(b) Done means:**
  - On an audited entity, the record editor sends the version it opened (`updated_at`) on update and on delete. An unaudited entity sends none and the screen says "last write wins here".
  - `AlvoPreconditionFailedException` becomes a refusal: "changed since you opened it", with Reload.
  - An e2e test: two tabs, the second save is refused.
  - The lesser gaps each get one sentence on the grid, or an issue, not an implementation:
    - structured filters beyond `ilike`, multi-sort and `select`;
    - `POST …/query` and `PUT` replace;
    - batch, which is F7 (#200).
- **(c) Size:** S-M, Admin only.
- **(d)** Conditional write is **F5**: spec §5 asks for a "data browser s auditom zásahov". The lesser gaps are F5 polish at most. Batch is F7.
- **(e)** None. Same batch as 44 (Data grid).

### §8d 26 — hooks can only be half authored
- **(a) Open.**
  - Add and remove only (`WorkingCopy.Hooks.cs:20,70`).
  - Endpoint, template and mutate field are free text (`HooksTab.razor:177,194-215`).
  - Every mutate value is wrapped in `$cel`, with one field per hook (`HookBuilder.cs`).
  - A `{{…}}` webhook `payload` is withheld, although AHC:379 honours it.
- **(b) Done means:**
  - **Edit in place**, keeping the entry's position in its ordered list.
  - Pickers over the declared `webhooks.endpoints`, `templates` and the entity's fields.
  - A mutate over several fields, with literal or CEL per value.
  - A `payload` box for a `{{…}}` template, which refuses raw JSONata with the build's `JSONata` refusal. `RefusalPlaces` already routes that refusal to OnWrite.
  - Declaring an endpoint (name + URL) and a template (subject + body) in Integrations, so that a webhook or email hook can be completed from the dashboard alone. `secretRef` is shown with the build's warning ("unsigned").
- **(c) Size:** L as a whole. Edit in place + multi-mutate is M; the pickers and payload are S-M; the endpoint and template editors are M. Admin only. `before*` hooks run in-transaction (CEL `mutate`), so the `mutate` half is **SC-adjacent**. The core is the authority and nothing new is evaluated in Admin.
- **(d)** F5, as part of the rules/automation builder (#28). Delivering webhooks *from automation*, HMAC signing and DLQ are F7 (#33, #120). The screen already says so through `webhooks`' warning. Do not build them.
- **(e)** Depends on nothing. It is the largest single item: give it its own batch.

### §8d 27 — keys the build ignores silently
- **(a) Open. The item conflicts with two decisions that are already recorded:**
  - `branding` is deliberately in neither capability list (`UnhonouredSubsystems.cs:64-73`, `CapabilityReport.cs:46-54`).
  - `realtime` is deliberately not warned, because its default is `true` and a warning would fire on every apply (`UnhonouredSubsystems.cs:85-94`, `docs/architecture/data-api.md:1331-1334`, #38).
  - Only `auth.providers` has no recorded decision. `DescriptorBlocks.cs:55` carries it and nothing reads it; `auth` is on the honoured list because of `roles`.
- **(b) Done means, one answer per key:**
  - **Metadata** (`description`, `branding`, `formats.*.description`): the dashboard **honours** them by rendering them (#268, #271), and **no core warning** is added. That is consistent with `CapabilityReport`'s rule that metadata keys are not subsystems.
  - **`auth.providers`:** the core warns when a value other than `local` is declared. The consequence reads "only local credentials exist in this build; OIDC/social sign-in is #36".
    - The Warned list is keyed by top-level block, and `auth` is Honoured, so the fact that keeps the two lists disjoint would break. That needs a sibling table or a dotted slot. This is a small **capability-contract** decision: architecture rules apply, but the Abstractions types stay as they are.
  - **`realtime`:** keep the no-warning decision. The dashboard needs its sentence from the core, not invented in Admin. The cheapest honest source is a capabilities-only entry, so that `GET capabilities` lists `realtime` as not built while the apply stays quiet.
    - That splits the "apply log" from the "capability report" list, which is a deliberate deviation to record in `CapabilityReport`.
    - The alternative is a dashboard note citing #38 by link, which reinterprets nothing.
- **(c) Size:** S-M, core internal. No Abstractions change unless the second list becomes a new field on `ManagementCapabilities`, which would be Abs+. Avoid that.
- **(d)** F5 for the notices. The subsystems are F7.
- **(e)** #271 renders what this item emits, so this goes first.

### §8d 29 — cosmetic
- **(a) Partially done.**
  - A `unique` that is already declared on a ref, enum or decimal is now drawn (`FieldFacets.cs:92`, §8d 17).
  - A new field of one of those types still gets no box and no reason (`TakesUnique`, `FieldFacets.cs:77-78`).
  - `x-*` keys are never shown.
  - `AddEntity` still writes `tenancy` and `audit` explicitly (`WorkingCopy.Entities.cs:17-31`).
  - The Overview lists `dynamicEntities` for `enabled: false` (`DeclaredLimits.cs:30-35` intersects on the key only).
  - An apply sends no `IdempotencyKey` (`ManagementGateway.cs:179-183` builds the request without one).
- **(b) Done means:**
  - A reason line where `unique` is not offered. For a ref, `unique` means one-to-one, so consider offering it.
  - `x-*` keys listed read-only in the editor's "Not drawn here" notes and in the Preview.
  - `DeclaredLimits` honours `enabled: false`.
  - One idempotency key per Apply click, reused on retry.
  - `AddEntity` explicitness stays as it is, and that is recorded as a decision: an explicit key is what the operator chose, so it is arguably right.
- **(c) Size:** S, Admin only.
- **(d) F5.**
- **(e)** None. The `dynamicEntities` part goes into the #271 batch.

### §8d 31 — Settings says "connected" when the key is missing
- **(a) Open.**
  - `ManagementAi(bool Configured, string? Kind, string? Model, string? Source)` (`src/MMLib.Alvo.Abstractions/Management/ManagementModels.cs:43`) has no key state.
  - The resolver only logs (`AiConnectionResolver.cs:70-113,237-244`).
- **(b) Done means:**
  - `ManagementAi` gains `KeyState` (present / missing / not needed).
  - `GET {m}/info` serialises it, the snapshot and the OpenAPI move, and Settings badges it.
  - A test for each state, including the "dials a host that always needs a key" case.
- **(c) Size:** S-M. **Abs+**: a positional record, and nothing is released yet. It moves the public API baseline, the OpenAPI document and the TeaPie/e2e path pins (see memory "E2E pins outside the rings").
- **(d) F5.**
- **(e)** Batch it with 40, which is the same record family and the same Settings screen.

### §8d 32 — `AlvoContextAccessor` nesting (SC)
- **(a) Open.**
  - The setter nulls the current holder before it installs a new one (`src/MMLib.Alvo/Auth/Internal/AlvoContextAccessor.cs:51-67`).
  - A nested publication therefore loses the outer one silently. It fails closed.
- **(b) Done means:** a decided behaviour, pinned by tests. The options:
  - Throw `InvalidOperationException` on a non-null set while a non-null principal is already published. That is fail-loud, and HttpContextAccessor-like in not supporting nesting.
  - Support real nesting, through a scope API that restores the outer principal on dispose.
  - **First audit every setter:** the Data API middleware, `ManagementGateway.AsOperatorAsync` (`ManagementGateway.cs:419`) and the static-render path. The throw is only safe if none of them publish twice.
- **(c) Size:** S-M. Core internal. `IAlvoContextAccessor` stays as it is unless a scope API is chosen, which would be Abs+.
- **(d)** F6 debt on shipped code. Nothing misbehaves today. Keep it in this PR only because the maintainer asked for everything. Otherwise file it on F6.
- **(e)** It touches the same `AsOperatorAsync` as 36. Do them together.

### §8d 36 — the assistant resolves its caller once per turn (SC, latent)
- **(a) Open, but the condition that makes it matter is not met.** Every assistant tool is a read or a dry run: `get_descriptor`, `get_schema`, `get_capabilities`, `get_revisions` and `validate_descriptor` (`src/MMLib.Alvo.Ai/Internal/ManagementTools.cs:41-64`). #29 forbids an apply tool.
- **(b) Done means, one of:**
  - (i) Re-resolve the operator before each stream step in `AsOperatorAsync<T>(Func<IAsyncEnumerable<T>>)` (`ManagementGateway.cs:419`), refusing the step and not the turn.
  - (ii) A test pinning "every assistant tool is read-only", so that adding a writing tool fails until (i) is done.
  - (i) is cheap and closes the item for good. Do it, and keep (ii) as a guard.
- **(c) Size:** S. Admin only. SC (revocation).
- **(d) F5**, because the assistant is F5.
- **(e)** Shares code with 32.

### §8d 37 — identity writes are unversioned (SC)
- **(a) Open. Decided and documented as it is** (`IAlvoUserAdministration` remarks). The role chips were mitigated by the grant/revoke change (`ManagementGateway.ChangeRolesAsync`).
- **(b) Done means:**
  - A version on `AlvoUser`.
  - An expected version on every write member of `IAlvoUserAdministration`.
  - `If-Match` on the `…/users/{id}/…` routes, answering 412.
  - The Access editor sends the version it opened.
  - The contract tests in `MMLib.Alvo.Testing` are extended for every implementation.
- **(c) Size:** M-L. **Abs+** on the port, a breaking change to a contract, which is acceptable because nothing is released. Touches the Identity package, the Host routes and OpenAPI. SC.
- **(d)** F6 debt. **Recommendation: file it on F6 and link it from the todo.** The residual window is one round trip long. This is the one item where "finish everything in this PR" costs the most for the least.
- **(e)** If it is done, do it with 39 and 46, which touch the same port and the same editor.

### §8d 38 — SQLite "database is locked" (availability)
- **(a) Open and unexplained.** The todo records the measurements. The suspect is the runtime migrator's DDL racing pooled open/close. WAL and `busy_timeout` are ruled out.
- **(b) Done means:** a reproduction from the e2e run, then either a fix in the migrator's connection handling, or a bounded retry on `SQLITE_BUSY` at open for the identity read. The second would be a mitigation, and has to say so.
- **(c) Size:** L, and uncertain. It is an investigation. Core and Data.Sqlite.
- **(d)** F6 debt. **File it with the evidence and do not schedule it here.** A flake investigation in a PR that is already large, with an unknown end, is the wrong trade.
- **(e)** None.

### §8d 39 — a temporary lockout does not show on Access (identity)
- **(a) Open.** `AlvoUser` (`src/MMLib.Alvo.Abstractions/Identity/AlvoUser.cs:15`) carries only `IsDisabled`.
- **(b) Done means:**
  - An additive `DateTimeOffset? LockedOutUntil`, projected by the Identity store when `LockoutEnd` lies in the future and below the disabled sentinel.
  - The person's row and editor show "Locked until HH:mm after failed sign-ins".
  - An **Unlock** action on the port (`ClearLockoutAsync`) and on the screen. This is 46's "operator-visible unlock".
  - A contract test for each.
- **(c) Size:** S-M. **Abs+** on `AlvoUser`, plus a port member if Unlock is included. SC-adjacent, since it touches identity administration.
- **(d) F5**, because it is the Access screen.
- **(e)** A prerequisite for 46's unlock.

### §8d 40 — an AI connection that cannot be dialled is stored as if it could
- **(a) Open.**
  - `SetAiConnectionAsync` stores what it is given.
  - The resolver reads an invalid connection as "none".
  - Settings has its own stricter copy of the rule (Task 10 of the rebuild).
- **(b) Done means:**
  - One public validator on `StoredAiConnection` in Abstractions, returning a sentence per field.
  - The resolver, `AlvoManagementService.SetAiConnectionAsync` and Settings all use it. `AlvoManagementService` answers with a structured 400 that names the field.
  - The http/https scheme rule lives in the validator.
  - A 400 invariant test runs through the API invariants suite.
- **(c) Size:** S-M. **Abs+** (the validator is public). Core, Host (the problem document) and Admin.
- **(d) F5.**
- **(e)** Batch it with 31.

### §8d 41 — a scale above the precision is accepted
- **(a) Open.**
  - No cross-check exists: a grep of `src/MMLib.Alvo/Descriptor` for scale finds only the mapper and `FieldDefault.cs:167-176`.
  - The editor's `FacetNumber` does not check it either.
- **(b) Done means:**
  - `DescriptorValidator` refuses `scale > precision` with a violation that names both facets and gives a fix.
  - The field editor refuses the same thing under the Scale box.
  - A schema test in the `alvo-schema-testing` style shows that the schema admits it and the validator refuses it. JSON Schema cannot compare two values, and that is why the rule sits in the validator.
- **(c) Size:** S. Core internal and Admin. No Abstractions change.
- **(d)** F6 debt by the triage rule. At size S, do it here.
- **(e)** Fits into B3, which already touches the field editor.

### §8d 42 — importing `null`
- **(a) Partially done.**
  - The Import *screen* is guarded now: `Transfer.razor:139-140` checks `CopyReplacement.IsObject` (`CopyReplacement.cs:36-44`) before `Load`.
  - `WorkingCopy.Replace` itself (`WorkingCopy.cs:325-343`) still assigns any parsed node. `null` empties the copy, and **an array or a number is kept and reported as parsed**.
  - The assistant path calls `copy.Replace` without the guard (`AssistantDrawer.razor:332`). It is fed a validated draft, so it is safe today only by the caller's discipline.
- **(b) Done means:**
  - Parse into a local variable and assign only a `JsonObject`.
  - Unit tests for `null`, an array and a number, each leaving the copy untouched.
- **(c) Size:** S, Admin only.
- **(d) F5.**
- **(e)** None.

### §8d 44 — a record created off the grid's page is not revealed
- **(a) Open.** `RecordGrid.SelectCreated` (`RecordGrid.razor:222-233`) only looks at `Scope.Page.Items`.
- **(b) Done means:**
  - A record created while the grid is not showing its position narrows the grid with an id filter (the Data API's `eq` on `id`).
  - The filter appears as a removable chip, "Showing the record you created — Clear", matching Access's `PeoplePaging` reveal (spec §3.5, amended 27 Sep).
  - An e2e test covers it: a record that sorts onto page 2 is selected and lit.
- **(c) Size:** M, Admin only. `GridQuery` gains a filter concept.
- **(d) F5.**
- **(e)** None. Batch it with 25.

### §8d 45 — the dashboard under a PathBase
- **(a) Open.**
  - `AdminApp.razor:38` hard-codes `<base href="/" />`.
  - The sign-in and sign-out redirects go through `Results.Redirect` (`src/MMLib.Alvo.Host/Internal/AlvoAdminSignIn.cs`).
  - The set-password and throttle redirects already use `AlvoAdminRedirect.SeeOther`.
  - The host already exposes `Alvo:PathBase` (`AlvoAdminOptions.cs:20-21`), so this is shipped configuration that is broken.
- **(b) Done means:**
  - `<base href>` comes from `Request.PathBase`.
  - The form actions and `AlvoAdminAssets.*` are base-relative.
  - The sign-in and sign-out redirects go through `SeeOther`.
  - Settle `returnUrl` against the prefix. This is an open-redirect surface, so SC-adjacent: `returnUrl` has to stay local.
  - One Host test mounts at `/tenant-a`, signs in, issues a set-password link, asserts the prefix, and sets a password.
- **(c) Size:** M. Admin and Host. No Abstractions change.
- **(d)** F6 debt on shipped host configuration. But item 30's new set-password link is F5 code that is wrong under a prefix, so **do it here**.
- **(e)** None.

### §8d 46 — anyone can keep a known account locked out (SC)
- **(a) Open. Kept deliberately** by ruling 10.8 of item 30's design. `AlvoSignIn.cs:110` signs in with `lockoutOnFailure: true` under Identity's defaults.
- **(b) Done means:**
  - (i) An operator-visible unlock (see 39).
  - (ii) A scheme that does not give strangers that lever: a lockout per (account, client), or an exponential per-account delay instead of a hard lock. This needs a design note that states the guessing-defence trade-off against NIST 800-63B §5.2.2, which recommends a limit on failed attempts per account, not per client.
- **(c) Size:** (i) S, together with 39. (ii) M and SC: identity, custom lockout storage or a timing scheme, adversarial tests.
- **(d)** (i) is F5. (ii) is "minor before 1.0", which makes it **F6**. **Recommendation: do (i) and file (ii) on F6.**
- **(e)** (i) depends on 39.

### §8d 43 — what is still open
- **(a) Open:** two replicas that cold-start on one empty database both try to create the tables, and the loser's `CREATE TABLE` fails its start once. It predates the rebuild.
- **(b)** `IF NOT EXISTS` DDL through the dialect, or a retry on the first boot. S-M, Identity plus the dialect port.
- **(d)** F6 debt. **File it.**

## 2. Dependency graph (the edges that matter)

- 27 → #271: the panel renders the core's sentences.
- 21 core → 21 Admin.
- 39 → 46(i).
- #267 read, #269 Fields list and #268 field description all edit one renderer (`FieldBadges`/`Fields.razor`).
- 31 ↔ 40: same record family, same Settings screen, same OpenAPI move.
- 32 ↔ 36: same `AsOperatorAsync`.
- #267 edit ↔ #270: both author policy through CEL, and share one CEL input.
- #265 and 41 are both field-editor and validator work.
- Independent: 25, 44, 42, 45, 26.

## 3. Proposed batches (ordered by risk, then by dependency)

Each batch is one SDD plan with 2–5 tasks and its own review pass. Safe, Admin-only batches come first.
Batches that grow Abstractions or touch the security core come later, when the tree is stable, so that
the public-API baseline and the security review are each done once.

**B1 — The schema screen shows what the descriptor declares** (Admin only, low risk, S×4)
1. The `FieldBadges` pass: `hidden`/`readOnly` badges, plain and conditional (#267 read half), plus the field `description` on each row (#268, part).
2. A Fields-row refusal badge for staged `validation`/`$cel` default. Record the decision in `RefusalPlaces` that no owner field is added, and close #269.
3. Project identity: shell title and logo from `branding` (logo URL scheme-guarded), the project description on Overview, and description editors for project, entity and field (the rest of #268).
4. `WorkingCopy.Replace` accepts objects only (42), plus the small 29 fixes: reason line for `unique`, `x-*` notes, idempotency key on apply.

**B2 — The capability surface is honest** (core internal + Admin, medium risk; architecture rules apply)
1. Core: warn on every `storage: dynamic` entity (21), and warn on a non-`local` `auth.providers` value. Decide how `realtime` appears in `capabilities` and record it as a deviation (27). No Abstractions change.
2. Admin: `PendingSchema` reads `storage`; the list, header and badge say "dynamic — F7, #41" (21).
3. The "Declared, not editable here" panel: `auth.providers`, `realtime` and `dynamicEntities` shown with the build's own sentences, and `DeclaredLimits` honours `enabled: false` (#271, 29).
4. The honoured blocks become editable: the `tenancy.enabled` switch (with a plan step in Preview), the `formats` editor and the `field.format` picker (#271).

**B3 — Field-editor facets that change the database** (Admin + core validator, medium risk)
1. The `onDelete` control over the three values, each with its sentence. The `cascade` sentence states which pipeline steps are bypassed; a test establishes that first (#265).
2. Core validator: refuse `setNull` beside `required`/non-nullable (#265), and refuse `scale > precision` (41). The field editor mirrors both. Schema tests for both.
3. An e2e test: choose `cascade`, apply, delete the parent, and assert the outcome that the new test established.

**B4 — Data grid correctness** (Admin only, medium risk)
1. Conditional update and delete with `AlvoPrecondition` on audited entities, a 412 turned into a refusal with Reload, and the "last write wins" sentence on unaudited entities (25).
2. Reveal a record created off the page, through an id filter chip (44).
3. One sentence, or an issue link, for each of the Data API features the grid does not offer: filters/multi-sort/select, query, replace, batch → #200 (25's lesser gaps).

**B5 — Hooks can be authored end to end** (Admin only, SC-adjacent through `mutate`; the largest)
1. Edit a hook in place, keeping its position.
2. Pickers over the declared endpoints, templates and fields. A mutate over several fields, literal or CEL per value.
3. A `{{…}}` `payload` box, which shows the JSONata refusal for raw JSONata.
4. Declare webhook endpoints and templates in Integrations, with `secretRef` shown under the build's "unsigned" warning (26).

**B6 — Authoring policy [SC]** (Admin through the working copy; security-core checklist plus `/security-review`)
1. Role catalogue add/remove. Removing a role that something still names is refused (#270).
2. A CEL editor for the three management levels, compiled at apply against `CelProfile.Access`, with Preview's re-qualify notice (#270).
3. A `hidden`/`readOnly` editor (off / always / CEL), sharing the CEL input from task 2 (#267 edit half).
4. An e2e test: narrow the developer level, and a developer is refused. A field hidden by CEL is masked in Data for a role and not for another.

**B7 — Management and AI contracts [Abs+]** (Abstractions growth: public API, OpenAPI and e2e pins move once)
1. `ManagementAi.KeyState` from the resolver to `/info` to a Settings badge (31).
2. A public `StoredAiConnection` validator, used by the resolver, the service (structured 400) and Settings (40).

**B8 — Identity and caller hardening [SC] [Abs+]**
1. `AlvoUser.LockedOutUntil`, a port `ClearLockoutAsync`, and the Access row, editor and Unlock (39, 46(i)).
2. The assistant re-resolves the operator per stream step, and a test pins that every tool is read-only (36).
3. Decide the `AlvoContextAccessor` nesting behaviour after an audit of every setter (throw, or a scoped restore), with tests (32).

**B9 — Deployability** (Admin + Host, medium risk; the `returnUrl` part is SC-adjacent)
1. `<base href>` from PathBase, base-relative forms and assets, redirects through `SeeOther`, and `returnUrl` settled and kept local.
2. A Host test at `/tenant-a` covering sign-in and set-password (45).

**Filed on F6, and linked from the todo instead of built:**
- 37: identity expected-version; L, SC, a breaking port change.
- 38: the SQLite lock; an investigation.
- 43: the two-replica first boot.
- 46(ii): the lockout scheme redesign.

For each, "done in this PR" means an issue with the todo's evidence, and the todo item marked
"→ #NNN (F6)". #273 and #272 stay where they are.

**Order:** B1 → B2 → B3 → B4 → B9 → B5 → B7 → B6 → B8.
- B1 and B2 first: they carry the least risk, and B2 unblocks the sentences B1 and B5 display.
- B7 and B8 last, so the Abstractions baseline and the OpenAPI pins move in two known batches.
- B6 and B8 go to a maintainer `/security-review` before the PR is marked ready.
- Estimated total: about 8–10 working days of SDD work. **B5 and B6 are the two that could each justify their own PR**, if the maintainer is willing to relax "all in #264".
