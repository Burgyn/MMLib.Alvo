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
people      name, email(unique, format:email)
milestones  name, status(enum), due_on
tasks       title, status(enum), description, estimate_hours(decimal 6,2),
            assignee_id  → people      onDelete: restrict
            milestone_id → milestones  onDelete: cascade
```

## What each construct is there for

| In the descriptor | Why |
|---|---|
| `people.email` `unique` | a second person at a taken address is a **409 `conflict`**, not a 422 — `unique` is one of two facets nothing can check *before* the write, because only the engine knows whether a value is held. |
| `people.email` `format: email` | the facet the framework *can* check: a 422 naming the field. The pair is the whole distinction — same field, two kinds of refusal, two different fixes. |
| `assignee_id` `restrict` | deleting a person who still holds work is refused. The alternative would silently unassign their tasks, losing the one fact somebody wanted to look up. |
| `milestone_id` `cascade` | deleting a milestone takes its tasks. A task belonging to a milestone that no longer exists is orphaned data, not history. |
| both refs nullable | a task in nobody's queue and no milestone is a legitimate row — and `assignee_id=is.null` is how a triage view is built. |
| `estimate_hours` scale 2 | a third fractional digit is refused rather than rounded. A silently rounded number is a wrong number nobody can see. |
| `audit: true` on all three | every row is versioned, so every write can be made conditional. |
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

`040` runs last because it destroys what the others built. Both engines enforce it — the suite is green
on SQLite and on `--pg`.

## What is deliberately not here

No tenancy, no roles beyond `authenticated`, no hidden or read-only fields, no idempotency keys. Those
are measured in `test/teapie-field-service`, which is the gated demo with five keys across two
tenants. Adding them here would make this a second copy of that rather than a small PM tool.
