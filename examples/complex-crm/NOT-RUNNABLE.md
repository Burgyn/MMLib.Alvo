# `complex-crm` is a format showcase, not a runnable backend

Applying this descriptor **fails**, on purpose. It exists to exercise the *shape* of every key
`schema/project.schema.json` declares — including the ones this build does not yet honour — so the schema
corpus has one fixture that covers the whole surface.

Today it declares four such features, each refused at apply by
`DescriptorToSchemaMapper`/`DescriptorValidator` with the consequence and the fix named:

| Feature | Where | Why it is refused |
|---|---|---|
| `computed` | `invoices.gross_total`, `invoice_items.line_total` | the expression is never evaluated, so the column stays null |
| `rollup` | `companies.open_deals`, `invoices.net_total` | nothing maintains the aggregate, so it reads as permanently null while looking like data |
| `default` | `companies.owner_id`, `deals.stage`, `deals.owner_id` | no column default is emitted and the value is dropped, so the field is simply null |
| `hooks` | `contacts.beforeCreate`, `deals.beforeUpdate` | the hooks never run, so a write the author believes is vetted or patched is neither |

**Start from `simple-tasks/` or `vehicle-registry/` instead** — both apply as they stand.

## When this file goes away

Delete it as soon as applying `crm.alvo.json` succeeds. That is not a suggestion a reader has to remember:
`DescriptorToSchemaMapperTests.Every_example_marked_not_runnable_really_is_refused` asserts every marked
example *is* refused, so the day the last of the four features lands, that fact fails until this file is
removed. The marker cannot outlive its reason.
