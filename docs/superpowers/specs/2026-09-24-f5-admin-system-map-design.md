# F5 admin — the system map

**Status:** design, executed on `f5/ai-agent` (PR #264) after the UX and architecture passes.
**Builds on:** `2026-09-18-f5-admin-dashboard-design.md` (the decision table's *"The model map is
read-only"* — this is that map, built), the F5 prototype's `entityMap()` (`docs/design/f5-admin/app.js`)
and its note *"The model map draws the focused entity and one hop, past six entities"*
(`docs/design/f5-admin/notes.js`), and `docs/architecture/admin-dashboard-review.md` (what not to build).

## 1. Why

The descriptor says what the backend *is* in two places a list cannot show at once:

- **the data model** — entities, the `ref` fields between them with their `onDelete`, and the rollups a
  parent keeps over its children (bike-workshop: 8 entities, 7 refs, 7 rollups);
- **the reactions** — before-hooks that refuse or rewrite a write (`reject` / `mutate`), after-hooks that
  send mail through a `templates` entry or post to a `webhooks` endpoint, and `automation` rules
  (declared; this build evaluates none of them).

Today the first is spread over one Relationships tab per entity and the second over the On-write tab
and a raw JSON dump on Integrations. The maintainer's ask (2026-09-24): *a visualisation of the whole
system* — what is possible, what makes sense, what is useful. The operator's questions it answers:
*what is in this project*, *what breaks if I delete a customer*, *what happens when an order goes
ready*, *who gets mail and from where*.

Access is deliberately **not** a layer: roles × operations is already a matrix on Rules/Access, and a
matrix reads better than a bipartite graph of the same facts.

## 2. What it is

One read-only drawing, two layers, two places.

| | |
|---|---|
| **Where** | Schema screen, a `List | Map` switch in the page header, addressed as `?view=map` (a link someone sends opens on the map). And the entity's Relationships tab, above the two lists: the same drawing focused on that entity and one hop. |
| **Data layer (always)** | A box per entity: name, `tenant` / `global`, field count; the `ref` fields listed with `→ target · onDelete`; the rollup fields listed as `Σ count from rentals` / `Σ sum price from rentals`; up to 6 other fields, then `+N more`. A wire per `ref` from the field's row to the target's header. A self-reference is a loop on the box. |
| **Reactions layer (toggle, `?layers=reactions`)** | Per box a guard line: `2 refuse · 1 sets` (the before-hooks, counted by action). An *Outside* column on the right with one node per `templates` entry (✉) and per `webhooks` endpoint (↗) that is referenced; a wire from the entity to it, labelled with the hook point (`afterCreate`, `afterUpdate` …), and a `condition` shown as the wire's tooltip (`<title>`). `automation` rules draw the same wires **dashed**, labelled `automation · not yet`, because the block is in `capabilities.warned` — the §4.1 rule: warned is drawn as declared-and-inert, never as working. A trigger in the schema's coalesced shape (`entity.companies.created.batch`) starts at its entity like any other. **Deviation, stated:** the word *drawn* on a dashed wire is `not yet`; `automation · not yet` stays in the wire's tooltip and in the screen-reader sentence. The full label did not fit the 96 px gap before a node and ran under the last box, and the dash and the legend (*dashed: automation*) already say automation. A hook or automation wire is drawn **over** the boxes, and one from an entity that is not in the last column rides a lane above every box to the Outside column (see §3). **Rules the map cannot draw are said, not dropped:** a rule on a `schedule`, on a pattern that names no single declared entity (`entity.*.created`), or whose actions are only `function` / `http.call` / `entity.update` has no box to start at or no node to reach, so it draws no wire, and the map panel says how many: *"2 automation rules draw no wire: they run on a schedule, match no single entity, or call something other than a template or webhook."* A template or endpoint referenced by nothing is drawn in the Outside column, faint, labelled `unused`. |
| **Pending** | Drawn from the **working copy**, not the applied descriptor (design §4.5: every schema screen renders the working copy). An entity that exists only in the working copy, or whose JSON differs from the applied one, carries a `not applied` badge in the accent tint, like the Schema list's pending rows. |
| **Interaction** | Each box is an SVG `<a>` to the entity screen — focusable, Enter follows it, hover and focus light the box and the wires that touch it. Nothing is draggable or editable (design decision table: an editable graph is a second schema editor). **Deviation, stated:** hover and focus light the **box only**, not its wires. Lighting the wires needs either script (the dashboard ships none for the map — the picture is server-rendered and exists before the circuit connects) or a CSS `:has()` rule per wire keyed on the box's name, which the stylesheet's token-and-layer rules do not admit per descriptor; each wire's tooltip and the screen-reader list already say what it joins. |
| **Scale** | Whole graph up to 12 entities. Past 12 the Schema map is focused like the prototype's: a picker (the entity names, `ChipGroup` single) chooses the centre and the map shows it and one hop, with *"Showing 5 of 23 — service_orders and one hop in each direction."* The entity tab is always focused. |
| **Phone** | The drawing keeps its size inside its own `overflow-x: auto` container (the page contract: only a diagram may be wider than the screen); the page itself never scrolls sideways. |

## 3. How

**Server-rendered SVG, laid out in C#.** Pure internal types under `Components/Schema/Map/`:

- `SystemGraph.From(string descriptorJson, string? appliedJson)` — reads the descriptor through JSON
  (the `DescriptorLens` rule: a lens, never a typed projection; an absent optional block is an empty
  graph part, never a throw) into nodes (entities, outside nodes) and edges (ref, reaction, automation).
- `MapLayout.Arrange(SystemGraph, MapFocus?)` — a small layered (Sugiyama-style) layout:
  1. **layer** = the longest `ref` path to a root (a referenced-only entity sits left, the entities that
     point at it to its right — the prototype's direction), cycles broken by a visited set so a mutual or
     self reference terminates;
  2. **order** within a layer by barycenter of neighbours, four alternating sweeps, ties broken by name
     (deterministic: the same descriptor draws the same picture, which is what makes it testable and what
     keeps the picture from shuffling on every apply);
  3. **coordinates**: fixed box width (the mono face makes a character count a width), height from the
     row count, fixed gaps; the Outside column after the last layer; wires as cubic Béziers from the
     field row's edge to the target header's edge.
- `SystemMap.razor` renders the arranged graph as `<svg>` with `a-map__*` classes; colours only through
  tokens, so both themes hold. A visually hidden `<ul>` lists each edge in words for a screen reader
  (the prototype's single `aria-label` sentence stops scaling at eight entities).

**Why not a library.** Mermaid, ELK and dagre would lay out better past ~30 nodes, but each is a 0.5–3 MB
static asset in a package whose review rejected a component library and a build step, and a client-side
layout makes the picture untestable from .NET and invisible before the circuit connects. Bike-workshop
is 8 entities; the focused mode caps what the layout ever has to handle. **Deviation, stated:** the layout
is a deliberately small Sugiyama variant (no dummy nodes for long edges; a long edge is a Bézier across
the intervening layer). The depth is **not** bounded by the focus: a focused map (past 12 entities, and the
entity tab) is at most three layers deep — the centre, what it points at, what points at it — but a whole
graph of up to 12 entities can be as deep as its longest ref chain (`examples/complex-crm` is five layers,
`countries` to `invoice_items`). It is accepted because at up to 12 boxes a long reference still reads as
one curve between two named ends. The one case where it did not is handled apart: a hook or automation
wire from an entity that is not in the last column would cross every column after it at header height —
under their plates if drawn first, through their names if drawn last — and read as the last box's wire. Such
a wire climbs, in the gap right of its box, to its own lane above every box (the picture moves down to make
room), runs along it, and comes down in the gap before its node.

**Not built:** editing on the map; dragging boxes (a position would have nowhere to be stored — the
descriptor has no layout block, and `x-*` extensions for it would be a second truth); an access layer;
a data-level (records) graph; export to an image.

## 4. Acceptance

1. Bike-workshop's map shows 8 boxes and 7 ref wires; the reactions layer adds `order-ready`,
   `express-order-received` and `rental-desk` with 3 wires, and `service_orders` / `order_lines` carry
   their guard lines — unit-tested on the graph, e2e-tested on the screen.
2. Complex-crm's automation rules draw dashed and say `not yet`.
3. The layout is deterministic, boxes never overlap, every ref edge goes from a later layer to an earlier
   one or is a loop, and a cyclic descriptor terminates — unit tests.
4. A staged entity is on the map with `not applied` before any apply.
5. `?view=map&layers=reactions` survives a reload; a box click opens the entity; Enter on a focused box
   does too.
6. At 375 px the page has no horizontal scroll (the existing `AssertNoHorizontalScrollAsync`) and the
   console is clean.
7. Both themes: every map colour is a token (`DesignTokenTests` / `ComponentLayerTests` stay green), and
   the gallery (`docs/design/gallery.html`) shows the map.
