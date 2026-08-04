# playground

A place to try things. One folder per project: a descriptor, a few TeaPie tests, and a launcher that
starts the Alvo image over whichever one you name.

```bash
playground/run                        # pick from a menu, then start
playground/run simple-todo            # start that one
playground/run simple-pm --test       # start it, then run its suite
playground/run simple-pm --test-only  # run the suite against a stack already up
playground/run simple-todo --pg       # over PostgreSQL instead of SQLite
playground/run simple-todo --down     # stop it, discard its data
playground/run --list                 # what is here
```

Starting a project prints its base URL, its API key and a link to `/scalar` — the whole surface the
descriptor generated, browsable.

## The projects

| Project | What it is for |
|---|---|
| **`simple-todo`** | One entity, four fields, **no authorization** — every request is a plain `curl` with no credential. What "a simple thing stays simple" looks like end to end, plus the query grammar, paging, and the refusals you will meet on day one. |
| **`simple-pm`** | People, milestones, tasks. Two references, a unique value, a `restrict` and a `cascade` — the relational behaviour a real backend has. Role-based rules, so it is also where you see what authorization actually does. |

Each has its own README stating which behaviour every construct in its descriptor exists to show.

## Adding one

`mkdir playground/my-thing`, drop in one `*.alvo.json`, done — `run` globs for projects and nothing
registers anywhere. Add a `tests/` directory when you want `--test` to have something to run; its
scripts reach the shared helpers as `_shared/Rows.csx`, three levels up from a case folder.

Two constraints worth knowing before you write a descriptor:

- **Exactly one `*.alvo.json`** per folder. That is what makes the glob unambiguous.
- **Several schema keys are refused at apply**, deliberately, because ignoring them would produce
  silently wrong data: `field.default`, `field.validation`, `field.computed`, `field.rollup`,
  `entity.softDelete` and the three `before*` hooks. The refusal names the consequence and the fix.
  `examples/README.md` keeps the current table.

## How it is wired

- **`docker-compose.yml`** — one templated stack, parameterised by descriptor path, port and key
  secret. `run` supplies all three, which is why adding a project needs no compose file of its own.
- **`docker-compose.pg.yml`** — the `--pg` overlay: the same stack over `postgres:16-alpine`.
- **`.run/`** — per-project state (the key secret, the port, the generated TeaPie environment).
  Gitignored; the secret is a credential.

**SQLite is the default** because it is one container with a database file inside it: a recreated
container is a fresh database, with no volume to forget. Reach for `--pg` when the question is whether
something behaves the same on a real server engine — §0 principle 3 says it must, and this is where
you would notice the day it does not. Both suites currently pass on both.

**Every start tears the stack down first.** A descriptor is applied at *boot*, so a stack left up from
an earlier run is serving the descriptor as it was then — you would edit a field, restart nothing, and
measure the old shape. It also means a suite always starts from an empty database.

**A project keeps its port** across restarts (default 8090, walking forward if something else holds
it), so an open `/scalar` tab survives. The repo's own demos take 8080 and 8081.

## Not a gate

This is in no ring and in no CI workflow. `scripts/test-ring0..2` and the PR's e2e gate the product;
the playground gates nothing and is free to be broken while you are experimenting. When a suite here
goes red, "the descriptor was wrong" is a perfectly good answer — that is what it is for.

The suites are written to be **rerun-safe** even so: `run` mints a fresh `runToken` for every test
run, every row a suite creates carries it, and every list assertion filters on it. So `--test-only`
twice in a row passes twice, rather than failing the second time on a row count.

For the gated equivalents, see `test/teapie` (the smoke stack) and `test/teapie-field-service` (the
multi-tenant demo, which is where roles, tenancy, hidden fields and idempotency are measured).
