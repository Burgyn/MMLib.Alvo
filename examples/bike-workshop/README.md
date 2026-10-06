# bike-workshop — the admin dashboard's demo backend

*Velo Dielňa*, a small bicycle repair and rental workshop in Banská Bystrica: customers and their bikes,
service orders with their lines, the parts on the shelf, the technicians who do the work, and a small
rental fleet. It is the backend `scripts/demo-admin` stands up for UX reviews and local demos of the
dashboard, so it is shaped to put **as much of what this build honours as possible** on screen, with
data that looks like a real workshop's. **It applies as it stands.**

## Run it

```bash
scripts/demo-admin              # build, start on :5080 over a new SQLite file, seed, stay up
scripts/demo-admin --port 5090  # another port
scripts/demo-admin --keep       # come back to the previous run's data
```

It prints the dashboard URL (`/admin`), the bootstrap administrator (`admin@alvo.demo`) with the password
generated for the demo directory, and a dev API key for `/scalar`. Ctrl+C stops the host and everything
the script started. The database, `host.log` and `webhooks.log` live in `${TMPDIR:-/tmp}/alvo-demo-admin`
(override with `ALVO_DEMO_DIR`).

The seed data is `seed/bike-workshop.seed.json`, posted through the public API — six staff accounts
through the Management API, then one `POST /api/{entity}/batch` per entity: 5 technicians, 15 customers,
25 bikes, 30 parts, 40 service orders across every status, 75 order lines, 8 rental bikes and 13 rentals.
Dates are relative to today, so the demo never looks stale. Its `$comment` explains the placeholders.

## What it exercises

| Construct | Where |
|---|---|
| Every field type: `string`, `text`, `integer`, `decimal`, `boolean`, `date`, `datetime`, `uuid`, `json`, `enum`, `ref` | across the entities; `service_orders` alone has nine of them |
| `required`, `unique`, `maxLength`, `precision`/`scale`, enum `values` | everywhere it is natural |
| Built-in `format` (`email`, `phone`) and four declared `formats` | `sk-postal-code`, `part-sku`, `order-number`, `fleet-code` |
| Literal `default` (enum, boolean, integer, decimal) | `status: received`, `priority: normal`, `active: true`, `labour_hours: 0`, … |
| `ref` with all three `onDelete`s | `restrict` (bike → customer), `cascade` (line → order), `setNull` (order → technician, line → part) |
| `computed` | `parts.margin`, `order_lines.line_total`, `service_orders.labour_total`, `rentals.price`; `service_orders.total` reads a rollup |
| `rollup` — `count` and `sum` | `customers.bikes_count`, `bikes.service_orders_count`, `technicians.orders_assigned`, `service_orders.parts_total`/`lines_count`, `rental_fleet.rentals_count`/`revenue` |
| `hidden: true` | `service_orders.internal_notes`, `rentals.id_document` |
| `hidden` and `readOnly` as CEL (per-role masking) | `parts.purchase_price` and `parts.margin` — manager and admin only |
| `audit` | every entity but `order_lines` and `rental_fleet` |
| `renamedFrom` | `customers.last_name` (was `surname`) |
| Field-level `index`, composite `indexes`, a `unique` composite index | `technicians.user_id`; two or three per entity; `rentals (fleet_bike_id, starts_at)` |
| Role-differentiated rules for all five operations | roles `manager`, `reception`, `technician` plus the built-in `admin`/`authenticated`; a row predicate on `service_orders.update` (`assigned_user_id == @user.id`) |
| Before-hooks: `reject` and `mutate` with CEL | a collected order cannot be reopened; moving to `ready` stamps `completed_at` with `now()`; only a `received`/`cancelled` order can be deleted; a line needs a positive quantity |
| Hook functions and operators: `upperAscii`, `trim`, `replace`, `math.round(x, 2)`, string `+`, `endsWith` | a bike's frame number and rack tag, the week discount on a rental, the workshop's own address refused on a customer — see [Hook functions](#hook-functions) |
| After-hooks: `email` over `templates` | an express intake mails the workshop; `ready` mails the customer (the development sender writes the mail to `host.log`) |
| After-hook: `webhook` over `webhooks.endpoints` | every new rental posts to `http://127.0.0.1:5081/hooks/rentals` — the script's receiver appends it to `webhooks.log` |
| `access` (management levels) | `admin` → admin, `manager` → developer, `reception` → viewer |
| `branding`, `auth.providers`/`roles` | — |

## Hook functions

Four rules, in six before-hooks, call the built-in CEL functions. Each hook is written the way the dashboard draws it,
so it opens in place in the hook editor, never read-only:

| Hook | What it computes | In the seed |
|---|---|---|
| `bikes` `beforeCreate` #0 and `beforeUpdate` #0 | `frame_number` = `upperAscii(replace(trim(new.frame_number), ' ', ''))` — trimmed, without spaces, in capitals, so one bike cannot be registered twice under two spellings. The field's `maxLength: 32` is measured on the payload, before the hook cleans it, so a frame number padded past 32 characters is refused with 422 even if it would fit afterwards | three bikes are posted as `cny23gr7sl04512`, `GNT 21TL 2C1 180447` and ` hai24trk6m00176 `, and stored as `CNY23GR7SL04512`, `GNT21TL2C1180447` and `HAI24TRK6M00176` |
| `bikes` `beforeCreate` #1 | `rack_tag` = `upperAscii(new.brand) + ' ' + new.frame_number` — the tag hung on the bike in the rack. It runs after #0, so it sees the cleaned frame number. A brand is at most 40 characters and a frame number 32, so the tag always fits its 80. The field is `readOnly`: a caller who sends a `rack_tag` is refused with 422, and the hook still writes it | every bike, as in `TREK WTU312C4471T` |
| `rentals` `beforeCreate` #0, when `new.days >= 7` | `daily_rate` = `math.round(new.daily_rate * 0.875, 2)` — a week or longer gets an eighth off. Without the rounding, 45 × 0.875 = 39.375 has three decimals, and the field's `scale: 2` would refuse the write | the week-long reservation of `RENT-002`: posted at 45.00 a day, stored at 39.38, a price of 275.66 |
| `customers` `beforeCreate` #0 and `beforeUpdate` #0, when `endsWith(new.email, '@velo-dielna.example')` | `reject` — the workshop's own address on a customer would send the "your bike is ready" notices back to the workshop. A customer without an e-mail passes, because a test of an empty value does not fire. The test is case-sensitive, so `DIELNA@VELO-DIELNA.EXAMPLE` passes too; `endsWith(lowerAscii(new.email), '@velo-dielna.example')` in text mode would catch it, at the cost of the guided row | no seeded customer trips it; see below |

**See it in the dashboard.** Open **Schema**, pick `bikes`, `rentals` or `customers`, and open the **On write** tab.
**Edit** on a hook opens it in place. The `rentals` and `customers` conditions open as guided rows: *days is at least 7*,
and *email ends with @velo-dielna.example*. Under a mutate expression box, and under the condition in text mode, a
folded **Functions you can call here** lists the built-ins that slot admits, each with its signature, a **built-in**
badge and an **Insert** button that writes the call at the caret. The stored values are on
the **Data** pages of `bikes` and `rentals`.

**The refusal.** The seeder posts each entity as one batch, in one transaction, so it cannot carry a row that is meant
to be refused. Try it against the running demo, with the key it prints:

```bash
curl -si http://127.0.0.1:5080/api/customers \
  -H "X-Alvo-Api-Key: <the demo's key>" -H 'Content-Type: application/json' \
  -d '{"first_name":"Desk","last_name":"Copy","phone":"+421 905 000 222","email":"dielna@velo-dielna.example"}'
# HTTP/1.1 403 Forbidden — "That is the workshop's own address. ... (refused by the before-hook at
# '/entities/customers/hooks/beforeCreate/0')"
```

**A function of your own** — such as `normalizeVin` — is registered from C# in an embedded host. This standalone demo
cannot do that, by design: the image has no C# extension point. See
[`samples/MMLib.Alvo.Samples.EmbeddedHost`](../../samples/MMLib.Alvo.Samples.EmbeddedHost/README.md). The dashboard lists
such a function beside the built-ins, with a **this host** badge.

## Deliberately left out

Each of these is declared in the schema and **refused at apply** by this build (see
[`../README.md`](../README.md#declared-in-the-schema-refused-at-apply)), so it is not here:

- `field.validation` — the before-hook `reject` on `order_lines` stands in for `quantity > 0`.
- a `$cel` `default` — the `mutate` hook stamps `completed_at` instead of a `now()` default.
- `rollup.where` — so there is no "open orders" count on a bike, only a count of all of them.
- `entity.softDelete` — deletion is guarded by a before-hook instead.
- the `function` action type (an after-hook that runs a script — not the CEL functions above), the `http.call` and
  `entity.update` actions, JSONata payloads, `email.data`, `bodyFile`.

Also left out on purpose:

- `automation`, `functions` and `dynamicEntities` — accepted with a warning but nothing runs for them, and
  a demo that shows a rule which never fires misleads its reviewer.
- **Tenancy.** One workshop is one tenant; turning it on would make every write carry `tenant_id` and the
  bootstrap administrator act in no tenant, which is a different demo (`../field-service` is the tenancy
  one).

## Rough edges you will see

These are the current behaviour of the build, not of the example:

- **A rollup over no children is `null`, not `0`** — a new customer's `bikes_count`, a fresh order's
  `lines_count`. And because `service_orders.total` reads `parts_total`, an order with no lines has a
  `null` total even when labour is booked (three of the six `received` orders have no lines on purpose).
- **A `json` field reads back as a string** carrying the JSON text (`preferences`, `compatibility`,
  `diagnostics`), although it is written as a JSON value.
- **A field hidden by a CEL `hidden` reads back as `null`** to a caller it is hidden from (a technician
  sees `"purchase_price": null`), where a `hidden: true` field is omitted outright. Writing it is refused
  with "a field this caller may read but not change", though that caller cannot read it.
- **Staff accounts cannot sign in.** The Management API creates them and the dashboard lists them under
  Access, but a credential token has no redemption route in the host yet, so only `admin@alvo.demo` can
  sign in.
- Boot logs a `templates, webhooks … not honour` **warning** although both are honoured on the after-hook
  path they are used on here, and two `fail` lines (`no such table: alvo_identity_users`) on a new database
  before the identity tables are created.
