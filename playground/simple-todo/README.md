# simple-todo

A todo list: a title, a status, a description and a date. **No authorization** — everyone may do
everything, so nothing here needs a credential.

```bash
playground/run simple-todo --test
```

```bash
# no key, no header, no login
curl localhost:8090/api/todos
curl -X POST localhost:8090/api/todos -H 'content-type: application/json' \
     -d '{"title":"buy milk","status":"todo","due_on":"2026-09-09"}'
```

The point of this project is the size of `todo.alvo.json` — one entity, four fields, five one-word
rules, and that is the whole backend. What you get from it:

- `GET/POST /api/todos`, `GET/PATCH/DELETE /api/todos/{id}`
- the PostgREST-shaped query grammar over every field
- keyset and offset paging, ETag/`If-Match` optimistic concurrency
- an OpenAPI document and a `/scalar` UI describing all of it
- RFC 9457 problem documents with per-field violations and fix suggestions

## Open, but still declared

Alvo is default-deny: **a missing rule is a refusal**, so "no authorization" is not the absence of
rules. It is five rules that say yes.

```json
"rules": {
  "list": "true", "get": "true", "create": "true", "update": "true", "delete": "true"
}
```

That is the whole difference, and it stays honest — you can see at a glance that this entity is public,
which you could not if public were the default. Delete any one of those lines and that operation stops
existing for everybody.

There is also no `auth` block at all. It is optional, and it configures *login providers* — of which a
public API needs none.

Two consequences worth knowing:

- **`created_by` and `updated_by` are `null`.** `audit: true` still records *when*; it records *who* as
  absent rather than inventing a stand-in id. (An all-zero uuid would be indistinguishable from a real
  user and would make an ownership rule match every row.)
- **A credential you actually present still has to work.** Nothing requires a key, but a request
  offering a *broken* one gets `401` rather than being quietly treated as anonymous — otherwise a
  misconfigured client would look fine for exactly as long as the rules stayed open.

## What each construct is there for

| In the descriptor | Why |
|---|---|
| `rules` all `true` | default-deny means public has to be stated. |
| `audit: true` | mints `updated_at`, which is what an `ETag` is minted from. Drop it and the row has no version: no ETag, and an `If-Match` naming one is refused rather than ignored. |
| `title` required | a paged list may only sort by a non-nullable field, so this is a legal `order` key. |
| `status` as `enum` | a refusal that lists the values that *do* exist, rather than a free-text field that accepts anything. |
| `description` as `text` | no length bound, unlike `string`. |
| `due_on` nullable | the field a paged `order=due_on` is **refused** on (issue #116) — every list here is paged, so that is permanent for now. It is also what makes the `is.null` filter worth showing. |
| one composite index | `["status", "title"]`, the pair a task list actually reads by. |

## The suite

| Case | Claim |
|---|---|
| `010-Crud/001-lifecycle` | create → read → 304 → conditional update → stale-tag 412 → delete → 404, and a PATCH really is partial. |
| `020-Query/001-filter-and-order` | equality, `in`, `not.`, `or=(…)`, a range over a nullable field, `is.null`, `select`, and `order` both ways. |
| `020-Query/002-paging` | three pages of two, each row visited once, compared against the whole set — and `offset` reaching the same window. |
| `030-Refusals/001-body` | three problems in one payload, all reported at once; both required fields named; no caller text echoed back. |
| `030-Refusals/002-query-and-access` | the query refusals; all four verbs anonymous; `created_by` null; and a broken key still 401. |

## What happens the moment you stop being public

Worth knowing before you write your first real rule, because it surprises everybody: swap a rule for
one that reads `@user` — say `'authenticated' in @user.roles` — and an anonymous **list** does not
become `401` or `403`. It becomes **`200` with an empty page**.

A configured rule compiles into a row *predicate*; a caller with no roles matches no row; a read that
matched no rows is an honest empty page. A refusal is what you get when the rule is evaluated against a
*candidate row* instead — which is what a create does, so an anonymous `POST` would be `403`. And an
anonymous read of one row would be `404`, indistinguishable from a row that does not exist, because
telling those apart is an existence oracle.

Read, write, read-one: three different answers from one rule. `simple-pm` is the project with
role-based rules if you want to poke at it.
