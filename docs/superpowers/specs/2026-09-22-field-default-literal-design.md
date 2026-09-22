# `field.default`, the literal half — what #113 leaves to decide

> Issue **#113** (*field.default, split into its literal and CEL halves*), whose literal half this
> implements, and **#208** (`examples/complex-crm` cannot be applied because it declares one).
> #113 settles the *what* and the *why*; this settles the four questions it leaves open, because
> each of them can be got wrong in a way no test would notice until a wrong value was stored.

## 0. What is already decided, and stays decided

From #113, in the maintainer's own words:

- **A literal default** — `"default": false`, `"normal"`, `1` — emits a column `DEFAULT` and is
  bound at insert when the payload omits the field. This is the half that ships here.
- **A CEL default** — `{"$cel": "now()"}`, `{"$cel": "@user.id"}` — needs the caller's context at
  write time, which is the `computed` machinery rather than a column default. **It stays refused**,
  and #113 stays open for it.
- The refusal it replaces was right: before PR3 a default was *silently dropped*, so a `required`
  field carrying one was an `INSERT` of NULL into a NOT NULL column.

## 1. How the schema model carries it — `JsonElement?`, not `object?`

`FieldSchema` gains `Default`, typed `JsonElement?`:

- the applied schema is **persisted** (`schema_json`) through a source-generated
  `JsonSerializerContext`, and `object?` is exactly the shape that serializes as nothing useful and
  deserializes as `JsonElement` anyway — so the honest type is the one it round-trips as;
- it keeps the model **engine-agnostic and driver-agnostic**, which is what lets the F7 dynamic
  driver read the same field without a second representation;
- the conversion to a CLR value happens where the value is used, through `ColumnValue` — the funnel
  an insert's payload and a filter operand already share, so a default is stored by the same rules
  as a value somebody sent.

## 2. The DDL carries it; the runtime model does **not**

| Model | What it does | Why |
|---|---|---|
| `DescriptorModelBuilder` (migration) | `property.HasDefaultValue(value)` | EF's own per-provider generator spells `DEFAULT` on both engines, which is the same seam `computed` uses — the core never writes DDL |
| `AlvoDataContext` (runtime) | **nothing** | see below |

**Not annotating the runtime model is the decision, and it is a safety decision.** EF treats a
property's *CLR default* as "not set" for a value-generated-on-add property, so with
`HasDefaultValue` on the runtime model a caller who explicitly sends `false` for a `bool` whose
default is `true` would have their `false` **silently replaced by `true`**. That is the
wrong-stored-value failure the refusal existed to prevent, reintroduced from the other side. A
sentinel could be configured per property to dodge it; carrying that reasoning on every column to
gain nothing is worse than not annotating at all.

**So the write path fills it, and this is the correction the first run forced.** The design above
argued that nothing was lost by leaving the runtime model alone, because "the insert already omits
what the payload does not carry" — `WritePropertyBag`'s own remark says that, and it is about the
*bag*, not about the statement. The insert this provider builds **names every mapped column**, so a
field the payload omits is written as an explicit `NULL` and the clause never applies. Measured:
the generated DDL was right and the row still came back `null`.

`FieldDefaults.Applied` therefore fills every absent field's default into the payload of a write
that composes a **whole row** — a create, and *both* branches of a replace. Not "a create": tying
it to the audit stamp's `isUpdate` is what broke `PUT`'s idempotence the first time it was written,
because the create branch filled the defaults and the replace branch did not, so one `PUT` stored
the default and the identical `PUT` after it stored `null`. An **update** is the one write that
does not take them, because its absent field means *leave it alone*.

The column's `DEFAULT` clause still ships, and is what a writer reaching the table without going
through Alvo gets. It is pinned by a per-engine SQL snapshot, because nothing behavioural touches
it.

## 3. A literal that does not fit the field is refused at apply

`"default": "yes"` on a `boolean` and `"default": 3.5` on an `integer` are refused by JSON kind; a
string longer than `maxLength`, a value outside an `enum`'s `values`, a malformed `uuid` or date are
refused by the field's own facets. Each names the field and says which facet excluded it, the same
way every other descriptor refusal does. The alternative is a `DEFAULT` clause
the engine rejects at migration time, which surfaces as provider SQL in a stack trace rather than
as a sentence about a descriptor.

**A `computed` or `rollup` field with a `default` is refused too**: the value is maintained for the
field, so a default for it is a contradiction rather than a narrowing. Both, not one — the frozen
schema forbids each pair identically, and a defence covering one of them is a defence with a hole.

**`"default": null` is not a declaration**, and applies as it always did: the JSON pass reads an
absent, empty or `false` value as declaring nothing, so refusing a null in the typed pass would be
the two passes disagreeing about a descriptor that used to work.

## 4. What this build then honours, and what it must not claim

`default` leaves `UnhonouredFeatures.OnAField`. `validation` **stays**, so
`examples/complex-crm/NOT-RUNNABLE.md` stays too — `DescriptorToSchemaMapperTests` asserts that the
marker means what it says, and it still does for a different reason than before. #208 therefore
narrows rather than closes; its own text says the example declares both.

The examples that lost a default get it back — `simple-tasks` (`done: false`,
`priority: "normal"`) and `vehicle-registry` (`passed: true`) — because an example that had to be
edited around a refusal is the clearest measure that the refusal cost something.

## 5. What a test pins

| Claim | Where |
|---|---|
| A literal default reaches `FieldSchema.Default` | mapper tests |
| A CEL default is still refused, by name | mapper tests, unchanged in spirit |
| A literal of the wrong type is refused, naming the field | mapper tests |
| A `computed` field with a default is refused | mapper tests |
| The generated DDL carries `DEFAULT` on both engines | `Add_column_with_a_default_sql_is_stable`, per engine |
| A create that omits the field stores the default | integration, both engines |
| A replacement takes the same defaults a create does | integration — `PUT` twice is one row, not two |
| A create that sends the CLR default value stores **that**, not the declared default | integration — the sentinel trap in §2, as a test rather than as a paragraph |
| `capabilities` no longer reports `field.default` as refused | management tests |
