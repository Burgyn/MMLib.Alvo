# F5 admin — the system map Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** A read-only map of the project — entities, refs, rollups, and (as a toggle) the reactions: before-hook guards, after-hook mail/webhooks, declared automation — on the Schema screen and, focused, on the entity's Relationships tab.

**Architecture:** Pure internal C# under `Components/Schema/Map/`: `SystemGraph` reads the working copy's descriptor JSON into nodes and edges; `MapLayout` arranges them (small layered layout, deterministic); `SystemMap.razor` renders the arranged picture as server-side SVG with `a-map__*` classes. No JS library, no client layout.

**Tech Stack:** .NET 10, Blazor server-interactive Razor class library, `System.Text.Json.Nodes`, xUnit v3 + Shouldly (unit), Microsoft.Playwright + xUnit (e2e).

**Spec:** `docs/superpowers/specs/2026-09-24-f5-admin-system-map-design.md`

## Global Constraints

- Scope: `src/MMLib.Alvo.Admin`, `test/MMLib.Alvo.Admin.Tests*`, `docs/design/gallery.html`, docs. No core (`src/MMLib.Alvo*` other than Admin) changes.
- The map is **read-only**: no drag, no edit, no stored positions.
- Drawn from the **working copy** (`WorkingCopy.Json`), pending marked against `WorkingCopy.AppliedJson`.
- Read the descriptor as JSON (the `DescriptorLens` rule): an absent or malformed optional block is an empty part of the graph, never a throw.
- `automation` is warned by this build: its wires are **dashed** and labelled `automation · not yet`.
- Whole graph up to **12** entities; past 12 the Schema map is focused on one entity and one hop. The entity tab is always focused.
- Every colour through tokens in `wwwroot/alvo.css` (`DesignTokenTests`, `ComponentLayerTests`, `StylesheetHygieneTests` stay green); new rules go in `@layer components`. Breakpoints stay {720, 1100}.
- Internal types stay internal. A component parameter must be public, so `SystemMap` takes strings/bools, never a `SystemGraph`.
- Razor components are public by the SDK; the `PublicApi.MMLib.Alvo.Admin.verified.txt` baseline grows by the new components only, and the commit says why (F-10 policy: `Components.*` are implementation).
- `.cs` files: UTF-8 BOM + CRLF; `.razor`: UTF-8 BOM + LF (match the neighbours); `alvo.css` has no BOM. The `alvo-dotnet-conventions` style: methods ≤ ~25 lines, XML docs on members, comments say *why*.
- E2E selectors: `GetByRole` / `GetByTestId`; do not add raw `.a-*` selectors or `:has-text(` (the `EndToEndSelectorTests` ratchet).
- Conventional Commits ending with the line `Claude-Session: https://claude.ai/code/session_018YXhgd1YrKkzdt1CbE29fV`. Never push, never switch branches, never dispatch subagents. Stage files by name (never `git add -A`: another agent may have uncommitted files in the tree). Do not touch processes on ports 5080 or 5090.
- Gates at the end of every task: `scripts/test-ring1` green; Tasks 3–4 also `scripts/test-admin-e2e` and `scripts/test-prototype` green.

---

### Task 1: `SystemGraph` — the descriptor read into nodes and edges

**Files:**
- Create: `src/MMLib.Alvo.Admin/Components/Schema/Map/SystemGraph.cs`
- Create: `src/MMLib.Alvo.Admin/Components/Schema/Map/SystemGraph.Reader.cs` (the JSON reading, partial of the same type, to keep each file focused)
- Test: `test/MMLib.Alvo.Admin.Tests/Schema/SystemGraphTests.cs`

**Interfaces:**
- Produces (namespace `MMLib.Alvo.Admin.Components.Schema.Map`, all `internal`):

```csharp
internal enum MapFieldKind { Plain, Reference, Rollup }

/// <param name="Detail">Plain: the type ("string"). Reference: "→ customers · restrict". Rollup: "Σ count from rentals" / "Σ sum price from rentals".</param>
internal sealed record MapField(string Name, string Detail, MapFieldKind Kind);

/// <param name="Fields">Every declared field in descriptor order.</param>
/// <param name="Refuses">How many before-hook entries have a `reject` action, over all three before points.</param>
/// <param name="Sets">How many before-hook entries have a `mutate` action.</param>
/// <param name="Pending">Absent from the applied descriptor, or its JSON differs from the applied one.</param>
internal sealed record MapEntity(string Name, bool Scoped, IReadOnlyList<MapField> Fields, int Refuses, int Sets, bool Pending);

internal enum OutsideKind { Template, Endpoint }

/// <param name="Used">Referenced by at least one hook or automation action.</param>
internal sealed record MapOutside(string Name, OutsideKind Kind, bool Used)
{
    /// <summary>"template:order-ready" / "endpoint:rental-desk" — the id an edge's To names.</summary>
    public string Id => $"{(Kind == OutsideKind.Template ? "template" : "endpoint")}:{Name}";
}

internal enum MapEdgeKind { Reference, Hook, Automation }

/// <param name="From">An entity name.</param>
/// <param name="To">Reference: the target entity name. Hook/Automation: a MapOutside.Id.</param>
/// <param name="Label">Reference: onDelete ("restrict" when absent — the schema's default). Hook: the hook point ("afterUpdate"). Automation: "automation · not yet".</param>
/// <param name="Field">Reference: the ref field's name; otherwise null.</param>
/// <param name="Condition">The hook's or the rule's CEL `condition`, or null; Automation also prefixes the rule name: "deal-won: changed(stage) && …".</param>
internal sealed record MapEdge(MapEdgeKind Kind, string From, string To, string Label, string? Field, string? Condition);

internal sealed partial record SystemGraph(IReadOnlyList<MapEntity> Entities, IReadOnlyList<MapOutside> Outside, IReadOnlyList<MapEdge> Edges)
{
    public static SystemGraph Empty { get; }
    public static SystemGraph From(string descriptorJson, string? appliedJson = null);
    /// <summary>The centre, every entity one ref away in either direction, the outside nodes their hook/automation edges reach, and the edges among what is kept.</summary>
    public SystemGraph Focus(string centre);
}
```

Reading rules (all from `schema/project.schema.json`):
- `entities.<name>.tenancy == "scoped"` → `Scoped`.
- field `type == "ref"` → `Reference`, detail `→ {entity} · {onDelete ?? "restrict"}`; one `Reference` edge per ref field whose target exists in `entities` (a ref to an undeclared entity yields the field but no edge). A self-reference is an edge with `From == To`.
- field with a `rollup` object → `Rollup`, detail `Σ {op} from {from}` for `count`, `Σ {op} {field} from {from}` otherwise.
- `hooks.before{Create,Update,Delete}[].action.reject` → `Refuses++`; `.action.mutate` → `Sets++`.
- `hooks.after{Create,Update,Delete}[].action` with `type == "email"` → edge to `template:{template}`; `type == "webhook"` → edge to `endpoint:{endpoint}`; `Label` = the point, `Condition` = the entry's `condition`. Other action types (refused at apply) are ignored.
- `automation.<rule>`: `trigger.event` of the form `entity.<entity>.<created|updated|deleted>` gives the source entity (any other trigger — `schedule`, a pattern that does not name a declared entity — draws no edge). Each of its `actions[]` of type `email`/`webhook` → an `Automation` edge as above.
- `templates.<name>` → `MapOutside(Template)`; `webhooks.endpoints.<name>` → `MapOutside(Endpoint)`; a name referenced by an action but not declared still gets a node (so the wire has an end), `Used = true`. Declared and unreferenced → `Used = false`.
- Order: entities and outside nodes in descriptor order; edges in the order read.
- Pending: `appliedJson` null → nothing pending. Otherwise an entity is pending when the applied `entities` lacks it or `!JsonNode.DeepEquals(working, applied)` for that entity's object.
- Any parse failure or a non-object where an object is expected → that part is skipped; `From("not json")` returns `Empty`.

- [ ] **Step 1: Write the failing tests** — `test/MMLib.Alvo.Admin.Tests/Schema/SystemGraphTests.cs`:

```csharp
using MMLib.Alvo.Admin.Components.Schema.Map;

namespace MMLib.Alvo.Admin.Tests.Schema;

/// <summary>The descriptor, read into what the system map draws.</summary>
public sealed class SystemGraphTests
{
    private static string Example(string folder, string file)
        => File.ReadAllText(Path.Combine(RepositoryRoot.Find(), "examples", folder, file));

    private static readonly SystemGraph _bikes = SystemGraph.From(Example("bike-workshop", "bike-workshop.alvo.json"));

    [Fact]
    public void Every_entity_is_a_box_and_every_ref_a_wire()
    {
        _bikes.Entities.Count.ShouldBe(8);
        _bikes.Edges.Count(edge => edge.Kind == MapEdgeKind.Reference).ShouldBe(7);
    }

    [Fact]
    public void A_ref_wire_starts_at_its_field_and_says_what_a_delete_does()
    {
        var wire = _bikes.Edges.Single(edge => edge.Kind == MapEdgeKind.Reference && edge.Field == "customer_id" && edge.From == "bikes");

        wire.To.ShouldBe("customers");
        wire.Label.ShouldBe(_bikes.Entities.Single(e => e.Name == "bikes").Fields.Single(f => f.Name == "customer_id")
            .Detail.Split(" · ")[1]);
    }

    [Fact]
    public void A_rollup_is_listed_as_what_it_sums_and_from_where()
    {
        var revenue = _bikes.Entities.Single(e => e.Name == "rental_fleet").Fields.Single(f => f.Name == "revenue");

        revenue.Kind.ShouldBe(MapFieldKind.Rollup);
        revenue.Detail.ShouldBe("Σ sum price from rentals");
        _bikes.Entities.Single(e => e.Name == "customers").Fields.Single(f => f.Name == "bikes_count")
            .Detail.ShouldBe("Σ count from bikes");
    }

    [Fact]
    public void Before_hooks_are_counted_by_what_they_do()
    {
        var orders = _bikes.Entities.Single(e => e.Name == "service_orders");

        orders.Refuses.ShouldBe(2);
        orders.Sets.ShouldBe(1);
        _bikes.Entities.Single(e => e.Name == "order_lines").Refuses.ShouldBe(1);
    }

    [Fact]
    public void After_hooks_wire_the_entity_to_the_template_or_endpoint_they_use()
    {
        var hooks = _bikes.Edges.Where(edge => edge.Kind == MapEdgeKind.Hook).ToList();

        hooks.Select(edge => (edge.From, edge.To, edge.Label)).ShouldBe(
        [
            ("service_orders", "template:express-order-received", "afterCreate"),
            ("service_orders", "template:order-ready", "afterUpdate"),
            ("rentals", "endpoint:rental-desk", "afterCreate"),
        ], ignoreOrder: true);
        hooks.Single(edge => edge.To == "template:order-ready").Condition.ShouldNotBeNull().ShouldContain("new.status == 'ready'");
        _bikes.Outside.Select(node => node.Id).ShouldBe(
            ["template:order-ready", "template:express-order-received", "endpoint:rental-desk"], ignoreOrder: true);
        _bikes.Outside.ShouldAllBe(node => node.Used);
    }

    [Fact]
    public void Automation_rules_are_drawn_as_declared_and_not_yet_running()
    {
        var crm = SystemGraph.From(Example("complex-crm", "crm.alvo.json"));
        var automation = crm.Edges.Where(edge => edge.Kind == MapEdgeKind.Automation).ToList();

        automation.ShouldNotBeEmpty();
        automation.ShouldAllBe(edge => edge.Label == "automation · not yet");
        automation.ShouldContain(edge => edge.From == "deals" && edge.To == "endpoint:invoicing");
    }

    [Fact]
    public void An_entity_the_applied_descriptor_lacks_or_differs_on_is_pending()
    {
        const string applied = """{"entities":{"a":{"fields":{"name":{"type":"string"}}},"b":{"fields":{"n":{"type":"string"}}}}}""";
        const string working = """{"entities":{"a":{"fields":{"name":{"type":"string"}}},"b":{"fields":{"n":{"type":"text"}}},"c":{"fields":{}}}}""";

        var graph = SystemGraph.From(working, applied);

        graph.Entities.Where(e => e.Pending).Select(e => e.Name).ShouldBe(["b", "c"]);
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("{}")]
    [InlineData("""{"entities":[]}""")]
    [InlineData("""{"entities":{"a":{"fields":{"x":{"type":"ref"}},"hooks":{"afterCreate":"nope"}}},"automation":{"r":{"trigger":{"schedule":"* * * * *"}}}}""")]
    public void A_descriptor_this_screen_did_not_write_draws_what_it_can_and_never_throws(string json)
        => Should.NotThrow(() => SystemGraph.From(json));

    [Fact]
    public void A_self_reference_is_a_wire_from_the_box_to_itself()
    {
        var graph = SystemGraph.From("""{"entities":{"parts":{"fields":{"parent_id":{"type":"ref","entity":"parts","onDelete":"setNull"}}}}}""");

        graph.Edges.ShouldHaveSingleItem().ShouldBe(new MapEdge(MapEdgeKind.Reference, "parts", "parts", "setNull", "parent_id", null));
    }

    [Fact]
    public void Focus_keeps_the_centre_one_hop_either_way_and_what_they_wire_to()
    {
        var focused = _bikes.Focus("bikes");

        focused.Entities.Select(e => e.Name).ShouldBe(["customers", "bikes", "service_orders"], ignoreOrder: true);
        focused.Edges.Where(edge => edge.Kind == MapEdgeKind.Reference)
            .ShouldAllBe(edge => focused.Entities.Any(e => e.Name == edge.From) && focused.Entities.Any(e => e.Name == edge.To));
        focused.Outside.Select(node => node.Id).ShouldBe(
            ["template:order-ready", "template:express-order-received"], ignoreOrder: true);
    }
}
```

Before writing the assertions, confirm the numbers against the example (`jq` over `examples/bike-workshop/bike-workshop.alvo.json`): 8 entities, 7 ref fields, `service_orders` 2 rejects + 1 mutate, `order_lines` 1 reject; `complex-crm` has an automation `deal-won` on `entity.deals.updated` with a webhook to `invoicing`. If an example value differs, the example is the truth — fix the test's expected value and say so in the report.

- [ ] **Step 2: Run to verify they fail** — `dotnet test --project test/MMLib.Alvo.Admin.Tests` → compile error, `SystemGraph` not found.

- [ ] **Step 3: Implement** `SystemGraph.cs` (records + `Empty` + `Focus`) and `SystemGraph.Reader.cs` (`From` and its helpers: `ReadEntity`, `ReadField`, `CountBefore`, `ReadAfter`, `ReadAutomation`, `ReadOutside`, `IsPending`). Parse with `JsonNode.Parse` inside a `try` that catches `JsonException` only and returns `Empty`; every access through `as JsonObject` / `as JsonArray` / `GetValue<string>()` guarded by `is JsonValue v && v.TryGetValue(out string? s)`. Each method ≤ ~25 lines, XML-documented, the remarks saying why (e.g. *why a ref to an undeclared entity keeps its row but loses its wire*).

- [ ] **Step 4: Run to verify they pass** — `dotnet test --project test/MMLib.Alvo.Admin.Tests` → all green.

- [ ] **Step 5: Commit** — `git add` the three files by name; `feat(f5): the system map reads the descriptor into a graph`.

---

### Task 2: `MapLayout` — a deterministic layered layout

**Files:**
- Create: `src/MMLib.Alvo.Admin/Components/Schema/Map/MapLayout.cs`
- Create: `src/MMLib.Alvo.Admin/Components/Schema/Map/MapPicture.cs` (the placed records)
- Test: `test/MMLib.Alvo.Admin.Tests/Schema/MapLayoutTests.cs`

**Interfaces:**
- Consumes: Task 1's `SystemGraph`, `MapEntity`, `MapField`, `MapOutside`, `MapEdge`, `MapEdgeKind`.
- Produces (internal, same namespace):

```csharp
/// <param name="Layer">0 = referenced by the others and pointing at nothing drawn; higher = further right.</param>
/// <param name="Shown">The rows drawn: every Reference and Rollup field, then up to 6 Plain ones, in descriptor order within each group.</param>
/// <param name="More">How many Plain fields were left out.</param>
/// <param name="Guard">"2 refuse · 1 sets" (only the non-zero parts, "refuse"/"sets" never pluralised) when reactions are on and the entity has before-hooks; else null.</param>
internal sealed record PlacedBox(MapEntity Entity, int Layer, double X, double Y, double Height, IReadOnlyList<MapField> Shown, int More, string? Guard)
{
    /// <summary>The y of a shown row's baseline centre — where a ref wire leaves the box.</summary>
    public double RowY(int index);
}

internal sealed record PlacedOutside(MapOutside Node, double X, double Y);

/// <param name="Path">An SVG path `d`.</param>
internal sealed record PlacedWire(MapEdge Edge, string Path, double StartX, double StartY);

internal sealed record MapPicture(IReadOnlyList<PlacedBox> Boxes, IReadOnlyList<PlacedOutside> Outside, IReadOnlyList<PlacedWire> Wires, double Width, double Height)
{
    public static MapPicture Empty { get; }
}

internal static class MapLayout
{
    public const double BoxWidth = 256, Head = 42, Row = 19, Pad = 12, LayerGap = 132, BoxGap = 38, OutsideWidth = 208, OutsideHeight = 44;
    public const int PlainRows = 6;

    /// <param name="reactions">Whether the reactions layer is drawn: guard rows, the outside column, hook and automation wires.</param>
    public static MapPicture Arrange(SystemGraph graph, bool reactions);
}
```

Algorithm (state it in the type's remarks, with the spec's stated deviation: no dummy nodes, a long edge is a Bézier across the layer between):
1. **Layer**: `layer(e) = 0` when e has no Reference edge to *another* drawn entity; else `1 + max(layer(target))`, computed with a visiting set so a cycle (a→b→a) terminates — an edge back into the visiting set counts as 0 for that step. Self-references are ignored for layering.
2. **Order**: start each layer sorted by name (ordinal). Then 4 sweeps alternating right-ward (sort layer i by the mean index of its Reference targets in lower layers) and left-ward (by the mean index of its Reference sources in higher layers); an entity with no neighbours in that direction keeps its index; ties by current index, then name. Result is deterministic.
3. **Coordinates**: `X = layer * (BoxWidth + LayerGap)`; within a layer boxes stack from `Y = 0` with `BoxGap` between. `Height = Head + (Guard is null ? 0 : Row) + Shown.Count * Row + Pad + (More > 0 ? 14 : 0)`. `RowY(i) = Y + Head + (Guard is null ? 0 : Row) + i * Row + 8`.
4. **Outside column** (reactions only, only nodes that have a wire or `Used == false` — every node): `X = (maxLayer + 1) * (BoxWidth + LayerGap)`, ordered by the mean `Y + 19` of their source boxes (unused nodes last, by name), stacked with `BoxGap`.
5. **Wires**: Reference — from the source's left edge at the field's row (`StartX = box.X`, `StartY = RowY(i)`) to the target's right edge at `target.Y + 19`: `M x1 y1 C mid y1, mid y2, x2 y2` with `mid = (x1 + x2) / 2`. Self-reference: `M x y C x-44 y, x-44 y2, x y2` to its own `Y + 19`. Hook/Automation — from the source's right edge `(box.X + BoxWidth, box.Y + 19)` to the node's left edge `(node.X, node.Y + OutsideHeight / 2)`, same Bézier form. Format numbers with `CultureInfo.InvariantCulture` and at most one decimal (`0.#`).
6. `Width` / `Height` = the extents of every box and node (0 for an empty graph → return `MapPicture.Empty`).

- [ ] **Step 1: Write the failing tests** — `test/MMLib.Alvo.Admin.Tests/Schema/MapLayoutTests.cs`:

```csharp
using MMLib.Alvo.Admin.Components.Schema.Map;
using System.Text.Json;

namespace MMLib.Alvo.Admin.Tests.Schema;

/// <summary>Where the system map puts things — the properties a picture has to keep, not its pixels.</summary>
public sealed class MapLayoutTests
{
    private static readonly SystemGraph _bikes = SystemGraph.From(File.ReadAllText(
        Path.Combine(RepositoryRoot.Find(), "examples", "bike-workshop", "bike-workshop.alvo.json")));

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void No_two_boxes_overlap(bool reactions)
    {
        var picture = MapLayout.Arrange(_bikes, reactions);
        var rects = picture.Boxes.Select(b => (b.X, b.Y, W: MapLayout.BoxWidth, H: b.Height))
            .Concat(picture.Outside.Select(o => (o.X, o.Y, W: MapLayout.OutsideWidth, H: MapLayout.OutsideHeight)))
            .ToList();

        for (var i = 0; i < rects.Count; i++)
        {
            for (var j = i + 1; j < rects.Count; j++)
            {
                var (a, b) = (rects[i], rects[j]);
                var apart = a.X + a.W <= b.X || b.X + b.W <= a.X || a.Y + a.H <= b.Y || b.Y + b.H <= a.Y;
                apart.ShouldBeTrue($"{i} and {j} overlap");
            }
        }
    }

    [Fact]
    public void A_ref_points_from_a_later_layer_to_an_earlier_one()
    {
        var picture = MapLayout.Arrange(_bikes, reactions: false);
        var layer = picture.Boxes.ToDictionary(b => b.Entity.Name, b => b.Layer);

        foreach (var wire in picture.Wires.Where(w => w.Edge.Kind == MapEdgeKind.Reference && w.Edge.From != w.Edge.To))
        {
            layer[wire.Edge.From].ShouldBeGreaterThan(layer[wire.Edge.To], $"{wire.Edge.From} → {wire.Edge.To}");
        }
    }

    [Fact]
    public void The_same_descriptor_draws_the_same_picture()
        => JsonSerializer.Serialize(MapLayout.Arrange(_bikes, true))
            .ShouldBe(JsonSerializer.Serialize(MapLayout.Arrange(_bikes, true)));

    [Fact]
    public void A_cycle_of_references_terminates_and_every_box_is_placed()
    {
        var graph = SystemGraph.From("""
            {"entities":{
              "a":{"fields":{"b_id":{"type":"ref","entity":"b"}}},
              "b":{"fields":{"a_id":{"type":"ref","entity":"a"},"self_id":{"type":"ref","entity":"b"}}}}}
            """);

        var picture = MapLayout.Arrange(graph, reactions: false);

        picture.Boxes.Count.ShouldBe(2);
        picture.Wires.Count.ShouldBe(3);
    }

    [Fact]
    public void Reactions_add_the_outside_column_to_the_right_of_every_box()
    {
        var plain = MapLayout.Arrange(_bikes, reactions: false);
        var reactive = MapLayout.Arrange(_bikes, reactions: true);

        plain.Outside.ShouldBeEmpty();
        plain.Wires.ShouldAllBe(w => w.Edge.Kind == MapEdgeKind.Reference);
        reactive.Outside.Count.ShouldBe(3);
        reactive.Outside.ShouldAllBe(o => o.X >= reactive.Boxes.Max(b => b.X) + MapLayout.BoxWidth);
        reactive.Boxes.Single(b => b.Entity.Name == "service_orders").Guard.ShouldBe("2 refuse · 1 sets");
        plain.Boxes.ShouldAllBe(b => b.Guard == null);
    }

    [Fact]
    public void A_box_shows_its_refs_and_rollups_and_at_most_six_other_fields()
    {
        var box = MapLayout.Arrange(_bikes, false).Boxes.Single(b => b.Entity.Name == "service_orders");
        var entity = box.Entity;

        box.Shown.Count(f => f.Kind == MapFieldKind.Plain).ShouldBeLessThanOrEqualTo(MapLayout.PlainRows);
        box.Shown.Where(f => f.Kind != MapFieldKind.Plain).Count().ShouldBe(entity.Fields.Count(f => f.Kind != MapFieldKind.Plain));
        box.More.ShouldBe(Math.Max(0, entity.Fields.Count(f => f.Kind == MapFieldKind.Plain) - MapLayout.PlainRows));
    }

    [Fact]
    public void An_empty_graph_is_an_empty_picture()
        => MapLayout.Arrange(SystemGraph.Empty, true).ShouldBe(MapPicture.Empty);
}
```

- [ ] **Step 2: Run to verify they fail** — compile error, `MapLayout` not found.
- [ ] **Step 3: Implement** `MapPicture.cs` and `MapLayout.cs` (private helpers `Layers`, `Order`, `Sweep`, `Place`, `PlaceOutside`, `Wire`, `Curve`, `Guard`; each ≤ ~25 lines).
- [ ] **Step 4: Run to verify they pass.**
- [ ] **Step 5: Commit** — `feat(f5): the system map's layered layout`.

---

### Task 3: `SystemMap` on the Schema screen

**Files:**
- Create: `src/MMLib.Alvo.Admin/Components/Schema/Map/SystemMap.razor`
- Modify: `src/MMLib.Alvo.Admin/Components/Schema/SchemaList.razor` (the `List | Map` switch, the reactions toggle, the focus picker past 12, the map panel)
- Modify: `src/MMLib.Alvo.Admin/Internal/AdminPaths.cs` (add `SchemaMap(bool reactions = false, string? centre = null)` → `/admin/schema?view=map[&layers=reactions][&centre=x]`) and its test `test/MMLib.Alvo.Admin.Tests/Internal/AdminPathsTests.cs`
- Modify: `src/MMLib.Alvo.Admin/wwwroot/alvo.css` (`a-map` block, `a-sr-only` utility)
- Modify: `docs/design/gallery.html` (a static map sample) and `test/MMLib.Alvo.Admin.Tests/GalleryTests.cs` only if its primitive list requires it
- Modify: `test/MMLib.Alvo.Admin.Tests/PublicApi.MMLib.Alvo.Admin.verified.txt` (grows by `SystemMap`)
- Create: `test/MMLib.Alvo.Admin.Tests.EndToEnd/SystemMapScenarios.cs`

**Interfaces:**
- Consumes: `SystemGraph.From`, `SystemGraph.Focus`, `MapLayout.Arrange`, `MapPicture`, `PlacedBox.RowY`, `AdminPaths.Entity(string)`; `AdminSession.CopyAsync/Follow/EnsureLoadedAsync` exactly as `SchemaList` already uses them; `ChipGroup<TValue>` (`Items`, `Selected`, `SelectedChanged`, `Label`, `aria-labelledby`); `PageHeader` `Secondary` slot; `Panel` (`Title`, `Subtitle`, `ChildContent`).
- Produces: component `SystemMap` with public parameters:

```csharp
/// <summary>The descriptor to draw — the working copy's JSON.</summary>
[Parameter, EditorRequired] public string DescriptorJson { get; set; } = "{}";
/// <summary>The applied descriptor, to mark what is not applied yet; null marks nothing.</summary>
[Parameter] public string? AppliedJson { get; set; }
/// <summary>Whether the reactions layer is drawn.</summary>
[Parameter] public bool Reactions { get; set; }
/// <summary>The entity to centre on with one hop; null draws the whole graph.</summary>
[Parameter] public string? Centre { get; set; }
```

It builds `SystemGraph.From(DescriptorJson, AppliedJson)`, focuses when `Centre` is set, arranges, and memoises the picture on the four inputs (recompute only when one changed — the working copy re-renders the screen on every edit).

Markup contract (the e2e and gallery rely on these):
- Wrapper `<div class="a-map" data-testid="system-map">` with `overflow-x: auto`; inside, `<svg class="a-map__svg" viewBox="-8 -8 {W+16} {H+16}" width="{W+16}" height="{H+16}" role="group" aria-label="System map">`.
- Wires first (under the boxes): `<path class="a-map__wire [a-map__wire--hook|a-map__wire--automation]" d="…" data-from="x" data-to="y"><title>{label}{ — condition}</title></path>`, and for Reference a `<circle class="a-map__dot">` at the start.
- A box: `<a class="a-map__box [a-map__box--pending]" href="{AdminPaths.Entity(name)}" data-entity="{name}" aria-label="{name}, {n} fields{, not applied}">` containing plate `rect`, head `path`, name `text`, meta `text` (`tenant · 12 fields` / `global · 12 fields`), the `not applied` badge as a `text` in the head when pending, the guard row, then one `text` pair per shown row (`a-map__field` + `--ref`/`--rollup`, and `a-map__type` right-aligned), and `+N more`.
- Outside node: `<g class="a-map__outside [a-map__outside--unused]" data-node="{Id}">` with a rect, a glyph `text` (✉ for a template, ↗ for an endpoint), the name, and `unused` when unused.
- Hover/focus: CSS only — `.a-map__box:hover .a-map__plate, .a-map__box:focus-visible .a-map__plate { stroke: var(--accent) }`. (Lighting the wires that touch a box is not required.)
- After the svg, a `<ul class="a-sr-only">` with one `<li>` per edge in words: *"bikes.customer_id points at customers; on delete restrict"*, *"service_orders sends the order-ready template after update"*, *"deals posts to invoicing on automation deal-won, which this build does not run yet"*.
- Empty graph → `<EmptyState Title="Nothing to draw yet" Body="The map draws entities and the references between them. Add an entity and it appears here." />`.

CSS (`@layer components`, tokens only — port the prototype's `a-map__*` rules from `docs/design/f5-admin/proposed.css:1317-1386`, then add): `--pending` plate `stroke: var(--accentBorder)` + the badge `fill: var(--accent)`; `--rollup` field `fill: var(--dim)` with the `Σ` detail `fill: var(--accent)`; `a-map__wire--hook { stroke: var(--accentBorder) }`; `a-map__wire--automation { stroke: var(--warnBorder, var(--border2)); stroke-dasharray: 5 4 }` (use whichever warn/amber token `alvo.css` actually defines — check `:root`); outside node plate `fill: var(--panel2)`, unused `opacity: .6`. A `.a-sr-only` utility in `@layer utilities` (the standard clip pattern). Font sizes: name 13px mono 600, meta 11px, rows 11px mono, type 10px — no smaller.

SchemaList changes:
- Read `view`, `layers`, `centre` from the query (`NavigationManager` + `QueryHelpers`, as `EntityTabs.FromUri` does), and re-read on `LocationChanged` (the handler exists already).
- In `PageHeader`'s `Secondary`: a `ChipGroup<string>` single with `["List", "Map"]`, `aria-label="View"`; choosing navigates (`replace: false`) to `AdminPaths.Schema` or `AdminPaths.SchemaMap(reactions, centre)`.
- When `view=map`: instead of the entities panel, a `Panel Title="Map"` whose `Subtitle` is *"Every relation is many-to-one and starts at the ref field that makes it; the word on the wire is what a delete on the other side does. Read only — a relation is added as a ref field in the entity's own editor."*; above the drawing a row with a checkbox `<label class="a-check"><input type="checkbox" …/> <span>Show reactions</span></label>` (navigates with `layers=reactions` toggled), a legend line in `a-hint` (*"✉ template · ↗ webhook · dashed: automation, declared and not run by this build"*), and past 12 entities a `ChipGroup<string>` single over the entity names labelled *"Centre on"* plus the *"Showing N of M — x and one hop in each direction."* sentence; default centre = the first entity.
- `SystemMap DescriptorJson="@Copy.Json" AppliedJson="@Copy.AppliedJson" Reactions=… Centre=…` — only once `Copy.Loaded`; a `Skeleton` before.
- The pending-entities list rows and the "New entity" form stay as they are in List view.

- [ ] **Step 1: Failing unit test** in `AdminPathsTests`: `AdminPaths.SchemaMap()` → `/admin/schema?view=map`; `SchemaMap(true)` → `/admin/schema?view=map&layers=reactions`; `SchemaMap(true, "bikes")` → `…&layers=reactions&centre=bikes` (the centre URL-escaped as `AdminPaths.Entity` escapes a name). Run → fails; implement; passes.
- [ ] **Step 2: Failing e2e** — `SystemMapScenarios.cs` (own `IClassFixture<AdminWorld>`; the world boots `examples/field-service`: `regions`, `customers`, `work_orders` with refs `work_orders.customer_id` and `work_orders.region_id`):
  1. `The_map_draws_every_entity_and_every_reference` — go `/schema`, choose the `Map` chip (`GetByRole(AriaRole.Radio, new() { Name = "Map" })` — check the role `ChipGroup` renders for a single group), wait for `GetByTestId("system-map")`; the three entity links (`GetByRole(AriaRole.Link, new() { Name = "work_orders", Exact = false })` etc.) are visible; the sr list contains `work_orders.customer_id points at customers`; the URL contains `view=map`; `AssertConsoleClean`.
  2. `A_box_opens_its_entity` — click the `regions` box link → URL ends `/schema/regions`.
  3. `A_staged_entity_is_on_the_map_before_any_apply` — add entity `invoices` through the New-entity form (as `RouteScenarios.AddEntityAsync` does), go `/schema?view=map`, the `invoices` link's accessible name contains `not applied`.
  4. `The_reactions_layer_is_addressable_and_survives_a_reload` — stage a before-hook `reject` on `work_orders` through the On write tab (follow how the existing hook scenarios in `SchemaEditingScenarios.cs` stage one), open `/schema?view=map`, tick *Show reactions*, URL has `layers=reactions`, reload, the checkbox is still checked and the `work_orders` box text contains `1 refuse`.
  5. `The_map_fits_a_phone` — 375 px, `/schema?view=map&layers=reactions`, `AssertNoHorizontalScrollAsync()` (the map's own container scrolls; check that helper already exempts a deliberate `overflow-x: auto` scroller — if it does not, the map container needs `data-scroller` or whatever marker that helper honours; read `AdminSession.AssertNoHorizontalScrollAsync` first), `AssertConsoleClean`.
  Run `scripts/test-admin-e2e` → the new scenarios fail.
- [ ] **Step 3: Implement** `SystemMap.razor`, the SchemaList changes and the CSS. Keep `SystemMap.razor` render fragments small (`Box`, `Row`, `Wire`, `Node`, `Words` as private `RenderFragment` methods); numbers into attributes via `CultureInfo.InvariantCulture`.
- [ ] **Step 4: Gallery** — add a section to `docs/design/gallery.html` with a small static sample of the map's markup (two boxes, one ref wire, one hook wire to a template node, one dashed automation wire, one pending box) using the shipped classes, so both themes can be eyeballed; `GalleryTests` must stay green (no literal colours).
- [ ] **Step 5: Run** unit (`dotnet test --project test/MMLib.Alvo.Admin.Tests`; accept the `PublicApi` received file only if the sole change is the `SystemMap` class), `scripts/test-admin-e2e`, `scripts/test-prototype`, `scripts/test-ring1` → green.
- [ ] **Step 6: Visual check** — the controller does this, not the implementer: note in the report that screenshots are pending.
- [ ] **Step 7: Commit** — `feat(f5): the system map on the Schema screen`; body names the PublicApi growth and why.

---

### Task 4: The focused map on the Relationships tab

**Files:**
- Modify: `src/MMLib.Alvo.Admin/Components/Schema/Relationships.razor` (the map above the two lists)
- Modify: `src/MMLib.Alvo.Admin/Components/Schema/Entity.razor` (pass what the map needs)
- Modify: `test/MMLib.Alvo.Admin.Tests.EndToEnd/SystemMapScenarios.cs` (one more scenario)

**Interfaces:**
- Consumes: `SystemMap` (Task 3) with `DescriptorJson`, `AppliedJson`, `Reactions`, `Centre`.
- `Relationships` gains two parameters: `[Parameter] public string? DescriptorJson { get; set; }` and `[Parameter] public string? AppliedJson { get; set; }`; `Entity.razor` passes `Copy.Json` / `Copy.AppliedJson` when `Copy.Loaded` (it already holds `Copy`). When `DescriptorJson` is null the tab renders exactly as today.

- [ ] **Step 1: Failing e2e** — `The_relationships_tab_draws_the_entity_and_its_neighbours`: open `/schema/customers?tab=relationships`, `GetByTestId("system-map")` is visible, the `customers` and `work_orders` box links are in it and `regions` is **not** (it is two hops away); a link *"Open the whole map"* goes to `/schema?view=map&centre=customers`. Run → fails.
- [ ] **Step 2: Implement** — in `Relationships.razor`, above "Points at": `<SectionHead Title="Around this entity">` with subtitle *"This entity, what it points at and what points at it, with what they send out. The whole project is on the Schema map."*, then `<SystemMap … Reactions="true" Centre="@Entity.Name" />` and the link (`AdminPaths.SchemaMap(reactions: true, centre: Entity.Name)`).
- [ ] **Step 3: Run** unit + `scripts/test-admin-e2e` + `scripts/test-ring1` → green.
- [ ] **Step 4: Commit** — `feat(f5): the relationships tab draws the entity's neighbourhood`.

---

## Self-review (done while writing)

- Spec §2 rows → Tasks: data layer (1, 2, 3), reactions layer (1, 2, 3), pending (1, 3), interaction (3), scale/focus past 12 (1 `Focus`, 3 picker), phone (3 scenario 5), entity tab (4). §3 how → 1–3. §4 acceptance 1 → Task 1 tests + Task 3 e2e; 2 → Task 1 automation test; 3 → Task 2; 4 → Task 3 scenario 3; 5 → Task 3 scenarios 2 and 4; 6 → Task 3 scenario 5; 7 → Task 3 CSS + gallery.
- Names used across tasks: `SystemGraph.From/Focus/Empty`, `MapEdgeKind.{Reference,Hook,Automation}`, `MapFieldKind.{Plain,Reference,Rollup}`, `MapOutside.Id`, `MapLayout.Arrange/BoxWidth/OutsideWidth/OutsideHeight/PlainRows`, `PlacedBox.{Layer,Guard,Shown,More,RowY}`, `MapPicture.Empty`, `AdminPaths.SchemaMap(bool, string?)`, `SystemMap.{DescriptorJson,AppliedJson,Reactions,Centre}` — consistent.
