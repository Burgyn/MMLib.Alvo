# simple-pm

People, milestones and tasks. Still small, but relational: two references, a unique value, and the two
`onDelete` policies pulling against each other.

```bash
playground/run simple-pm --test
```

Start from `simple-todo` if you have not — this project assumes the CRUD surface and adds only what
having more than one entity brings.

## The shape

```
people      name, email(unique, format:email), user_id
milestones  name, status(enum), due_on
tasks       title, status(enum), description, estimate_hours(decimal 6,2),
            assignee_id  → people      onDelete: restrict
            milestone_id → milestones  onDelete: cascade
```

## The rules, and who may do what

The stack publishes **two keys** that differ in exactly one thing — whether the caller holds `admin`.
Same scopes, same tenant, everything else identical, so anything the member cannot do is the descriptor
and can be nothing else. `playground/run simple-pm` prints both.

| Operation | Rule | An ordinary member gets |
|---|---|---|
| `people` list/get | `'authenticated' in @user.roles` | the whole directory |
| **`people.create`** | **`'admin' in @user.roles`** | **403** — only an admin adds a person |
| `people.delete` | `'admin' in @user.roles` | 404 |
| `people.update` | `'admin' in @user.roles || user_id == @user.id` | 200 on their own row, **404** on anyone else's |
| `milestones` read | `'authenticated' in @user.roles` | the plan |
| `milestones` write | `'admin' in @user.roles` | 403 on create |
| `tasks` read/create/update | `'authenticated' in @user.roles` | anyone may file and work a task |
| `tasks.delete` | `'admin' in @user.roles` | **404** |

`user_id` is a plain `uuid`, not a `ref` to the reserved `users` entity, and that is forced: a rule
compiles to a SQL predicate over *this entity's own columns* and cannot join. So "edit your own
profile" has to compare a column on `people` with `@user.id`, which means `people` has to carry the
identity itself.

### The one thing to take away: a rule cannot be relied on to produce 403

`people.create` and `tasks.delete` are **the same rule text** — `'admin' in @user.roles` — and the same
member gets **403** from one and **404** from the other. Not a bug:

- **create** — there is no existing row, so the rule is checked against the *candidate* row and rejects
  it. A refusal is the only answer available. `403`.
- **delete / update / get** — the row is looked up *under the rule as a predicate* first. For a
  non-admin the predicate matches nothing, so there is no row to act on. `404`, deliberately
  indistinguishable from a row that is not there, because telling them apart is an existence oracle.
- **list** — same predicate, so: `200` with an empty page.

Only four things actually produce `403`: an unknown entity, the tenant guard, an operation with **no
rule at all**, and a rule reading `@user.id`/`@tenant.id` that the caller cannot supply. Everything
your own predicate excludes is `404` or an empty page. `docs/architecture/data-api.md` calls this "the
RLS surprise" and it is the assumption most likely to be wrong in code written against this API.

A useful consequence: the two `403`s are distinguishable by their `detail`. A configured rule that
rejected the row says *"The write was rejected by policy."*; an operation you never wrote a rule for
says *"No policy allows '&lt;op&gt;' on this entity."* — different problem, different fix.

## What each construct is there for

| In the descriptor | Why |
|---|---|
| `people.email` `unique` | a second person at a taken address is a **409 `conflict`**, not a 422 — `unique` is one of two facets nothing can check *before* the write, because only the engine knows whether a value is held. |
| `people.email` `format: email` | the facet the framework *can* check: a 422 naming the field. The pair is the whole distinction — same field, two kinds of refusal, two different fixes. |
| `assignee_id` `restrict` | deleting a person who still holds work is refused. The alternative would silently unassign their tasks, losing the one fact somebody wanted to look up. |
| `milestone_id` `cascade` | deleting a milestone takes its tasks. A task belonging to a milestone that no longer exists is orphaned data, not history. |
| both refs nullable | a task in nobody's queue and no milestone is a legitimate row — and `assignee_id=is.null` is how a triage view is built. |
| `estimate_hours` scale 2 | a third fractional digit is refused rather than rounded. A silently rounded number is a wrong number nobody can see. |
| `audit: true` on all three | every row is versioned, so every write can be made conditional. `created_by` also records *which* of the two keys wrote a row. |
| `people.user_id` | the column the `update` rule compares with `@user.id` — what makes "edit your own profile" expressible at all. Not `unique`, only because the playground reuses one member identity across runs; a real deployment would want the constraint. |
| `["status","title"]`, `["milestone_id"]` | the two reads a task board issues. |

## The suite

It is one story told in order, and the later cases depend on the earlier ones' rows.

| Case | Claim |
|---|---|
| `010-People/001-people` | create; `email` optional; the unique **409** beside the format **422**. |
| `020-Milestones/001-milestones` | create; a conditional PATCH; an undeclared enum value refused with the real values listed. |
| `030-Tasks/001-tasks` | both references stored and filterable; `is.null` on a ref; a decimal at its declared scale; a ref to a missing row refused **before** the write; a value past the scale refused. |
| `030-Tasks/002-board` | a card across the board, each step conditional — and a second editor's stale write refused rather than reverting the first one's change. Also: an explicit `null` on a nullable ref is a *value*, not a silence. |
| `040-Integrity/001-restrict-and-cascade` | deleting a referenced person is 409; deleting an unreferenced one is 204 (the control); the cascade takes one milestone's tasks and only those; and then the *same* delete that was refused succeeds. |
| `050-Authorization/001-roles` | the table above, measured — including the 403-vs-404 finding, and a member who can *see* a row in the directory and still cannot write it. |

`040` runs before `050` and destroys what `010`–`030` built; `050` creates its own rows under a
`-auth` name prefix so it depends on nothing left over. Both engines agree — the suite is green on
SQLite and on `--pg`.

Every refusal is paired with a **control**: the admin doing the same thing successfully. Without it a
`404` is equally well explained by "that row was never there", and the test would keep passing on a
build that had lost the row entirely.

## What is deliberately not here

No tenancy, no hidden or read-only fields, no idempotency keys, and no application-defined roles beyond
the built-in `admin`/`authenticated`. Those are measured in `test/teapie-field-service`, the gated demo
with five keys across two tenants. Adding them here would make this a second copy of that rather than a
small PM tool.
