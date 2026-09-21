# Session — 2026-09-21: the four design gaps, and the prototype rebuilt on them

Branch `f5/admin-prototype-iteration`. Nothing on `main`.

## What this session was asked to do

Two adversarial reviews (`reviews/REVIEW-product.md`, `reviews/REVIEW-ux.md`) had taken the first
prototype apart. The brief was: fix the **four design gaps the drawing exposed** in the merged
design document first, then rebuild the prototype worst-defect-first, then prove it by driving it
rather than by looking at it.

---

## 1. The four gaps, and what was decided

These are not prototype bugs. They are questions
`docs/superpowers/specs/2026-09-18-f5-admin-dashboard-design.md` never asked, and a screen cannot
avoid. Each is now a section of that document, in its own voice, with the alternative it rejected.

### §2.7 — a signed-in operator has no tenant

`AlvoIdentityContextResolver.ResolveAsync` returns `null` the moment a tenant is requested and
mints none on the success path. `PolicyEngine` refuses a `tenancy: scoped` entity for a tenantless
caller **before any rule is consulted**. Nothing anywhere enumerates tenants. So in the descriptor
the product ships to demonstrate multi-tenancy, Data serves one entity of three.

**Decided:** an operator carries **one** tenant on their own row (`AlvoUser.Tenant`), honoured as a
*confirmation* — byte-for-byte `TenantResolver`'s rule for an API key. A request naming any other
tenant still resolves to no caller, so the resolver's own refusal stays true for the caller it was
written about.

**Alternative rejected:** a set of tenants and a switcher. That is cross-tenant capability, which
`TenantResolver`'s summary already calls *"a deliberate, audited grant, deferred to #42"*. Shipping
the picker in F5 is shipping the grant without the audit — the trade D4 refuses one layer up. An
operator who administers two tenants holds two accounts until #42. **The drawn tenant switcher is
removed.**

It also dissolves the registry question rather than answering it: a scalar on the caller's own row
is not an enumeration, so there is no list to build. `SELECT DISTINCT tenant_id` was the obvious
alternative and is refused twice over — it is the data read D4 forbids the Management API, and it
is the cross-tenant existence oracle `data-api.md` closed once already (#137).

### §2.2.1 — the simulator returns a predicate, not a row decision

`ManagementPolicySimulation` is `(Entity, Operation, Caller)` — no record id, deliberately.
An earlier draft of §2.2 read *"optionally a record id"*; that line is **corrected rather than
edited away**, on §2.1.1's own precedent, because a drawing was built against it and took the whole
screen's shape from it.

**Decided:** the screen explains a predicate and never scores a row. It renders `Using`,
`WithCheck`, `TenantScope`, `HiddenFields`, `ReadOnlyFields`, `DenyReason`, the four ways a caller
gets 403 in `PolicyEngine.Resolve`'s own order, and what failing the read predicate looks like per
operation (200 and a shorter page on `list`; 404 on `get`/`update`/`delete`). A per-record
allowed/refused badge is forbidden **by construction** — §6.3 criterion 4 restated as a UI rule.

### §3.7 / §3.8 — a second human cannot exist under `providers: ["local"]`

`IAlvoUserStore` has no create; the bootstrap seed *"only ever creates the account, once"*;
`ManageUsers` is in the level table with no route. So a local-auth project has one account forever,
and "people arrive by signing in" describes a door nobody can reach.

**Decided:** `IAlvoUserStore` is **not touched** — widening it would break every implementer,
including a read-only directory mirror with nothing to create into. A **second** contract,
`IAlvoUserAdministration`, lands in Abstractions with six members and six management routes at
`admin`; `MMLib.Alvo.Identity` implements it. Every member may **refuse by name**, which is what
keeps it provider-agnostic: a directory mirror refuses `CreateAsync`, an OIDC-only deployment
refuses `IssueCredentialTokenAsync`. That is the distinction against `IAlvoUserStore`'s *"no
credential appears on this port"* — that sentence refuses a contract which *demands* a credential,
and a member asking *"mint a token if you have such a thing"* is a question an implementation may
decline. The new person sets their **own** password through the token; an administrator never types
a colleague's password, for the same reason `Alvo__Admin__BootstrapPassword` is refused as a
*value*.

**The self-grant guard is in the CORE, not in the implementation**, and it covers the tenant as
well as roles — a rule enforced only inside a swappable adapter is optional by construction. It is
pinned as a contract test in `MMLib.Alvo.Testing`, on `PolicyEngineContractTests`' precedent.
§3.7's U3.1 weighs what a tenant self-grant would cost rather than assuming: it creates no
authority an `admin` lacks (they hold `ApplyDescriptor` and can rewrite the rules), but it reaches
it by a path that records **nothing**, where an apply appends a revision with an author. Same
authority, quieter route — which is why the self-grant is refused and granting somebody else's is
not.

**Cost stated rather than discovered:** nothing in this build delivers that token. Identity
configures no mail transport, and `templates`/`webhooks` reach an entity write, not an identity
event. F5 shows it once for the administrator to hand over out of band.

### §4.5 — three edit queues, one descriptor

**They do not queue, because there is nothing to queue.** One `DescriptorJson`, one `If-Match`, one
appended revision; `entities.*.fields`, `entities.*.rules`, `auth.roles` and `access` are keys of
that one document, and the server re-primes both catalogues from the same accepted descriptor. One
working copy, one preview grouped by kind, one apply. Role *membership* is the one thing
legitimately outside it, and the drawing's two-speed Access split was already the right way to draw
that line.

**One consequence only visible once they merged:** `access` is in the same document, and both write
members re-resolve the **whole** apply to `admin` when that block differs. A developer who edits one
rule and one level is refused the entire apply — including the half within their level — and is
owed that sentence at Preview rather than at a 403.

### Also amended

§4.2's route map (every route regains `/projects/{project}`; Settings stops claiming API keys and a
danger zone no route serves; Integrations, the entity API tab and the assistant's absence are
recorded); §4.3 (Schema above Data, with the reason); §4.6 (the drawing's own upheld decisions, in
the layer they belong to); §6.1 (five new pinned claims); §6.3-3 and §6.3-4 (restated as UI rules);
§6.4 (the assistant, tenant switching, key issuance and project deletion, each with its reason);
§7 (two items that need issues filed); §8; and a new §9 recording what the second pass found.

---

## 2. The prototype

It moved into the repository at `docs/design/f5-admin/` and stopped carrying a copy of `alvo.css`:
`index.html` links `src/MMLib.Alvo.Admin/wwwroot/alvo.css` directly, so there is exactly one copy of
the design system and no freshness check to forget. Serve from the repository root.

Three structural changes carry most of the fixes:

**`working-copy.js` — one document.** The applied descriptor and the one being edited, diffed by
JSON pointer, each pointer classified as schema / rules / roles / access. The three pending bars are
gone. The descriptor pane renders the **working** copy with a gutter mark per changed leaf, and it
renders the stored JSON rather than a typed projection — which is what kept `nullable`, `index`,
`renamedFrom`, `storage`, `realtime` and every `x-` key from being silently dropped (§6.3-3).

**`policy.js` — a verdict.** `PolicyEngine.Resolve`'s order, reproduced, and it stops where the
engine stops. The record picker and the JavaScript evaluator are deleted.

**`generated/` — fixtures nobody typed.** `scripts/gen-prototype-fixtures` writes the capability
report, the management route table with its levels, the per-type field facets, the built-in formats,
the identifier patterns, the problem-type slugs and the example descriptor, from the repository.
`--check` fails on drift. All **eleven** refusals are present and verbatim; the first version had
five, three of them truncated.

Everything else the reviews named: the field editor sits beside the pane instead of on top of it;
Data refuses a scoped entity with the tenant guard's own reason and never renders it as an empty
page; a filter over a hidden field is indistinguishable from one over a field that does not exist;
paging claims stability rather than depth-independence and Previous is disabled with the reason;
the record form carries every writable field including both hidden ones and a `uuid` gets a text
input; errors land at their field after a real collision with the slug and violation code the API
uses; Settings names no engine, offers no key issuance and no danger zone; Integrations serves the
warned prose verbatim and once; every control is wired or visibly inert; the palette, focus traps,
`j`/`k`, `/` and `g`+letter work; and the missing states are drawn.

---

## 3. What the suite found

`scripts/test-prototype` — Node + `@playwright/test`, **93 tests**, eleven scenarios, four matrix
cells each. In no ring, for `scripts/test-load`'s reason.

Driving it found eleven defects that looking at it did not:

1. A re-render on `change` fires on **blur**, so clicking the next control destroyed the node the
   click was heading for. Typing a length and then clicking a format chip lost the chip.
2. The pane marked every ancestor line of a changed leaf — a one-digit edit gutter-marked the file.
3. The right column had a `max-height` and no scroll, so the field editor's own buttons were
   unreachable.
4. The command palette's input had no `data-act`, so nothing read what was typed: it opened, it
   focused, and it ignored you.
5. The anonymous caller fell back to the first user — the simulator answered as Jana while the chip
   said `anon`.
6. The ref picker offered rows from another tenant: a control whose output the tenant scope refuses.
7. An enum arrived pre-filled with two invented values.
8. The destructive confirmation word was regexed out of prose and came back `enum`.
9. Three refusals were quoted nowhere.
10. A restore's reverse plan was measured against the wrong base.
11. `prefers-reduced-motion` was asserted rather than checked.

The console is an assertion: every scenario fails on any `console.error`, any `pageerror` and any
failed request.

---

## 4. Review findings this session rejected

In `#/notes` in full, with the reasoning. In short: the reviews were right about far more than they
were wrong about, and four things did not survive scrutiny —

- **product §3** (wizard step 1) — accepted, and it understated the fix: drawing the wizard
  honestly exposed a **third** step neither review asked for.
- **product §12** (offering `admin` is fine because it is in `RoleCatalog`) — true about the
  catalogue, insufficient about the consequence. It is offered, and it asks first.
- **ux §6** (default the simulator to a record that is not the caller's own) — moot: there is no
  record picker any more. The instinct is honoured by opening on technician + `list`.
- **ux §10** (five representations, show the CEL once) — four of the five had a job. The genuine
  duplicate — a readout beside a raw textarea showing the identical string — is gone.

---

## 5. What could not be done, and why

- **`AlvoUser.Tenant` and `IAlvoUserAdministration` are designed, not built.** They are framework
  changes with no issue; §7 now says both must be filed. The prototype draws the behaviour they
  would produce.
- **The assistant is out.** It needs a secret store that does not exist, and `GET /management/info`
  reports no AI connection to gate it on. The drawer's shape is kept as a design in §4.6/§6.4.
- **Search and paging over people are drawn as absent**, because `IAlvoUserStore.ListAsync` has
  neither — a port gap rather than a screen one. §3.7 widens the port; the screen says so.
- **Role and tenant changes are not audited.** #42 is F7. The screen says plainly that nothing
  records them rather than refusing to make them.
- **The Razor dashboard is not here.** #227–#231 are five issues with their own plans and PRs, and
  the design they would be built against changed this session. They are blocked on the two items
  above besides.

---

## 6. What the next session should pick up

1. **File the two missing issues** §7 names, and get #227's implementation plan written against the
   amended design — that is the next real step, and it is a separate branch and PR.
2. **The hook editor** is here in the shape the automation builder will need. Decide whether it
   lands in F5 or waits so the two are designed together (`#/notes`, still open).
3. **A tenant has no name anywhere.** Every screen shows a uuid, honestly and awkwardly. Decide
   whether a name is worth a descriptor block, an `x-` hint, or nothing.
4. **The display-field heuristic.** The ref picker searches the first required string field and says
   so at the control. An `x-` hint would settle it properly.
5. **File the two issues §7 names, in the first commit after this merges**, and replace "no issue
   exists" with the numbers. They are deliberately not filed before the merge: an issue citing a
   section of a design that is not on `main` cites nothing.

## 7. What `alvo-plan-guard` found, and what changed because of it

It returned **ISSUES** with nine findings and `needs-deep-review: yes`. Eight were accepted and
fixed before the PR opened:

| Finding | What changed |
|---|---|
| §3.7 named two different ports for the same members | `IAlvoUserStore` is untouched; a second contract is added, with the table that says why |
| `IssueCredentialTokenAsync` on the port forecloses OIDC | it stays, and the refusal-by-name rule is written down with the distinction it rests on |
| a tenant self-grant is unaudited and §2.7 did not weigh it | §3.7 U3.1 weighs it; the self-grant is refused, and the guard now covers the tenant |
| U3 placed in a provider implementation | moved into the core gate, pinned by a `MMLib.Alvo.Testing` contract test; §6.1's row moved from ring2 to ring0 |
| Node in the pipeline is an undeclared deviation | recorded as **D8** in §0.2, with what the source sentence was actually protecting |
| `npm install` with a gitignored lockfile | lockfile committed, `scripts/test-prototype` uses `npm ci` |
| a required check now points at a docs artifact | the retirement obligation is a table in the prototype's README and a paragraph in the generator's own docstring |
| the committed plan read as unstarted | ticked, with the two places what shipped differs from what it said |

The ninth is a process note and is answered here: the tree moved under the review because two
docs commits landed while it read. Its verdict covers `f25a046..76da0c2`; everything after that is
the eight fixes above, and the PR body says so.

**`needs-deep-review: yes` stands.** No product code changed, but the design this PR amends is the
security core by area — `AlvoUser.Tenant` changes what a cookie session carries into the tenant
guard, and six new `admin` routes plus a self-grant guard are proposed. The PR carries the label
and the `alvo-security-core-review` checklist is owed before merge.
