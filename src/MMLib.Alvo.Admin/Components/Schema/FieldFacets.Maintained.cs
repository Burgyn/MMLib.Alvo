using MMLib.Alvo.Schema;
using System.Text.Json.Nodes;

namespace MMLib.Alvo.Admin.Components.Schema;

/* The two kinds whose value is maintained for the field — a rollup (RollupResolver) and a computed column
   (ComputedColumnSql) — and exactly what the apply accepts of each. */
internal sealed partial class FieldFacets
{
    private const int CelMaxLength = 2000;

    /// <summary>The frozen schema's <c>rollup.op</c> values, in its own order.</summary>
    public static IReadOnlyList<string> RollupOps { get; } = ["sum", "count", "avg", "min", "max"];

    /// <summary>The types a computed column is offered as.</summary>
    /// <remarks>
    /// The scalar ones. The type is chosen rather than derived because the dashboard has no CEL compiler
    /// (<c>RulesTab.razor</c> gives the reason); a ref, an enum, a json or a uuid generated column is withheld
    /// as the conservative choice — unverified whether the migrator refuses one.
    /// </remarks>
    public static IReadOnlyList<FieldType> ComputedTypes { get; } =
        [FieldType.String, FieldType.Text, FieldType.Integer, FieldType.Decimal, FieldType.Boolean, FieldType.Date, FieldType.DateTime];

    private static readonly string[] _withheld = ["required", "unique"];

    private FieldKind _declaredKind;

    /// <summary>Where the value comes from.</summary>
    public FieldKind Kind { get; set; }

    /// <summary>The child entity a rollup aggregates.</summary>
    public string RollupFrom { get; set; } = string.Empty;

    /// <summary>The rollup's operation; one of <see cref="RollupOps"/>.</summary>
    public string RollupOp { get; set; } = "count";

    /// <summary>The child field aggregated — for every op but <c>count</c>.</summary>
    public string RollupField { get; set; } = string.Empty;

    /// <summary>The child's ref it follows, when it has more than one here.</summary>
    public string RollupVia { get; set; } = string.Empty;

    /// <summary>Whether a declared <c>rollup.where</c> — refused at apply — is dropped on save.</summary>
    public bool RemoveRollupFilter { get; set; }

    /// <summary>A computed field's CEL.</summary>
    public string Computed { get; set; } = string.Empty;

    /// <summary>The entities that could be rolled up, from <see cref="RollupSources.For"/>.</summary>
    public IReadOnlyList<RollupSource> Sources { get; set; } = [];

    /// <summary>Whether the value is maintained for the field, so a caller never writes it.</summary>
    public bool MaintainedElsewhere => Kind != FieldKind.Supplied;

    /// <summary>Whether <c>required</c> is drawn — a maintained value is never supplied by a caller.</summary>
    public bool RequiredOffered => Kind == FieldKind.Supplied;

    /// <summary>The chosen source, when it is one of <see cref="Sources"/>.</summary>
    public RollupSource? Source => Sources.FirstOrDefault(source => source.Entity == RollupFrom);

    /// <summary>The child fields the current op can aggregate: none for <c>count</c>, decimals only for <c>avg</c>.</summary>
    public IReadOnlyList<RollupChildField> Aggregatable => Source is not { } source || RollupOp == "count"
        ? []
        : [.. source.Numbers.Where(child => RollupOp != "avg" || child.Type == FieldType.Decimal)];

    /// <summary>The type a rollup is stored as: a count is whole, anything else takes the aggregated field's type.</summary>
    public FieldType DerivedType => Aggregatable.FirstOrDefault(child => child.Name == RollupField)?.Type ?? FieldType.Integer;

    /// <summary>Whether the declaration carries a <c>rollup.where</c>.</summary>
    public bool DeclaresRollupFilter => _declared["rollup"]?["where"] is not null;

    /// <summary>Reads the rollup or the computed expression a declaration carries, and so its kind.</summary>
    private void ReadMaintained(JsonObject facets)
    {
        if (facets["rollup"] is JsonObject rollup)
        {
            Kind = FieldKind.Rollup;
            RollupFrom = Text(rollup["from"]);
            RollupOp = Text(rollup["op"]) is { Length: > 0 } op ? op : "count";
            RollupField = Text(rollup["field"]);
            RollupVia = Text(rollup["via"]);
        }
        else if (facets["computed"] is JsonValue)
        {
            Kind = FieldKind.Computed;
            Computed = Text(facets["computed"]);
        }

        _declaredKind = Kind;
    }

    /// <summary>Writes a rollup, in place where the declaration already has one.</summary>
    private string? WriteRollup(JsonObject facets)
    {
        if (RefuseRollup() is { } refusal)
        {
            return refusal;
        }

        WriteDerivedType(facets);
        facets.Remove("computed");
        WriteRollupObject(RollupObject(facets), Source!);
        Toggle(facets, "index", Indexed);
        return WriteDefault(facets);
    }

    /// <summary>Why the apply would refuse this rollup, in <c>RollupResolver</c>'s order.</summary>
    private string? RefuseRollup()
    {
        if (Source is not { } source)
        {
            return Sources.Count == 0
                ? "Nothing points at this entity, so there is nothing to roll up — add a ref field here on the child first."
                : "A rollup needs the entity whose rows it aggregates — pick one of the entities that point here.";
        }

        if (source.Refusal is { } refused)
        {
            return refused;
        }

        if (!RollupOps.Contains(RollupOp, StringComparer.Ordinal))
        {
            return "A rollup's op is one of sum, count, avg, min or max.";
        }

        return RefuseAggregatedField() ?? RefuseVia(source);
    }

    private string? RefuseAggregatedField()
    {
        if (RollupOp == "count" || Aggregatable.Any(field => field.Name == RollupField))
        {
            return null;
        }

        return Aggregatable.Count == 0
            ? $"{RollupFrom} has no {(RollupOp == "avg" ? "decimal" : "number")} field a {RollupOp} can aggregate — use count, or add one."
            : $"A {RollupOp} needs the {RollupFrom} field it aggregates — pick one.";
    }

    private string? RefuseVia(RollupSource source)
        => source.Via.Count > 1 && !source.Via.Contains(RollupVia, StringComparer.Ordinal)
            ? $"{RollupFrom} points here through {string.Join(", ", source.Via)} — pick which one this rollup follows."
            : null;

    /// <summary>The derived type; a declared precision and scale are the author's, derived only when absent.</summary>
    private void WriteDerivedType(JsonObject facets)
    {
        var child = Aggregatable.FirstOrDefault(field => field.Name == RollupField);
        Type = child?.Type ?? FieldType.Integer;
        facets["type"] = Word(Type);
        ClearFacetsOfOtherTypes(facets);

        if (child is { Type: FieldType.Decimal })
        {
            facets["precision"] ??= child.Precision ?? DefaultPrecision;
            facets["scale"] ??= child.Scale ?? DefaultScale;
        }
    }

    private static JsonObject RollupObject(JsonObject facets)
    {
        if (facets["rollup"] is JsonObject existing)
        {
            return existing;
        }

        var created = new JsonObject();
        facets["rollup"] = created;
        return created;
    }

    private void WriteRollupObject(JsonObject rollup, RollupSource source)
    {
        rollup["from"] = RollupFrom;
        rollup["op"] = RollupOp;
        SetOrRemove(rollup, "field", RollupOp == "count" ? null : RollupField);
        SetOrRemove(rollup, "via", source.Via.Contains(RollupVia, StringComparer.Ordinal) ? RollupVia : null);

        if (RemoveRollupFilter)
        {
            rollup.Remove("where");
        }
    }

    /// <summary>Writes a computed column: the expression and a chosen scalar type.</summary>
    private string? WriteComputed(JsonObject facets)
    {
        var expression = Computed.Trim();
        if (expression.Length == 0)
        {
            return "A computed field needs its expression — CEL over this row's own fields, such as unit_price * amount.";
        }

        if (expression.Length > CelMaxLength)
        {
            return "A computed expression is at most 2000 characters — the schema's limit for CEL.";
        }

        if (!ComputedTypes.Contains(Type))
        {
            return $"A computed value is stored as a generated column, offered here as {string.Join(", ", ComputedTypes.Select(Word))}.";
        }

        facets.Remove("rollup");
        facets["type"] = Word(Type);
        facets["computed"] = expression;
        Toggle(facets, "index", Indexed);
        ClearFacetsOfOtherTypes(facets);
        return WriteDefault(facets) ?? WriteTypeFacets(facets);
    }

    /// <summary>A kept <c>rollup.where</c> is refused at apply (<c>RollupResolver.cs:107</c>).</summary>
    private string? RefuseAKeptFilter(JsonObject facets)
        => Kind == FieldKind.Rollup && facets["rollup"]?["where"] is not null
            ? "Its rollup declares a 'where' filter, which this build refuses at apply (rollup.where). Tick \"Remove the declared filter\" to save the field."
            : null;

    /// <summary>A required or unique on a maintained value: carried, and said.</summary>
    private IEnumerable<FacetNote> Withheld()
        => MaintainedElsewhere
            ? _withheld.Where(facet => Flag(_declared[facet])).Select(facet => new FacetNote(
                facet, "true", FacetFate.Kept, "not offered for a value that is maintained for it — kept as declared."))
            : [];

    private static void SetOrRemove(JsonObject owner, string key, string? value)
    {
        if (value is { Length: > 0 })
        {
            owner[key] = value;
            return;
        }

        owner.Remove(key);
    }
}
