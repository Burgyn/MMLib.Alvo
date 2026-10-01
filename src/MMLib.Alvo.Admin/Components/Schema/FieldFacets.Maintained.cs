using MMLib.Alvo.Schema;
using System.Globalization;
using System.Text.Json.Nodes;

namespace MMLib.Alvo.Admin.Components.Schema;

/* The two kinds whose value is maintained for the field — a rollup (RollupResolver) and a computed column
   (ComputedColumnSql) — and exactly what the apply accepts of each.

   Two rules pull in different directions here and both hold: a NEW choice is offered conservatively (avg over
   decimals, number fields only, the scalar computed types), and an EXISTING declaration the core accepts is saved
   as declared — nothing the apply accepts is blocked, and nothing is rewritten from what the editor would have
   offered. */
internal sealed partial class FieldFacets
{
    private const int CelMaxLength = 2000;

    /// <summary>The frozen schema's <c>rollup.op</c> values, in its own order.</summary>
    public static IReadOnlyList<string> RollupOps { get; } = ["sum", "count", "avg", "min", "max"];

    /// <summary>The types a new computed column is offered as.</summary>
    /// <remarks>
    /// The scalar ones. The type is chosen rather than derived because the dashboard has no CEL compiler
    /// (<c>RulesTab.razor</c> gives the reason); a ref, an enum, a json or a uuid generated column is withheld
    /// from a new field as the conservative choice — unverified whether the migrator refuses one. A field already
    /// declared as one keeps it (<see cref="ComputedTypesOffered"/>).
    /// </remarks>
    public static IReadOnlyList<FieldType> ComputedTypes { get; } =
        [FieldType.String, FieldType.Text, FieldType.Integer, FieldType.Decimal, FieldType.Boolean, FieldType.Date, FieldType.DateTime];

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

    /// <summary>
    /// The child fields the current op can aggregate: none for <c>count</c>; for a new choice the number fields
    /// (decimals only for <c>avg</c>); and the declared field, whatever its type, while the declaration is unchanged.
    /// </summary>
    public IReadOnlyList<RollupChildField> Aggregatable
    {
        get
        {
            if (Source is not { } source || RollupOp == "count")
            {
                return [];
            }

            List<RollupChildField> offered = [.. source.Numbers.Where(child => RollupOp != "avg" || child.Type == FieldType.Decimal)];
            if (KeptAggregate is { } kept && !offered.Any(child => child.Name == kept.Name))
            {
                offered.Add(kept);
            }

            return offered;
        }
    }

    /// <summary>The type a new rollup is stored as: a count is whole, anything else takes the aggregated field's type.</summary>
    public FieldType DerivedType => Aggregated?.Type ?? FieldType.Integer;

    /// <summary>
    /// The type the save writes: the declared one while it can hold the aggregate, and <see cref="DerivedType"/>
    /// for a new rollup or a declared type that cannot.
    /// </summary>
    /// <remarks>
    /// Kept rather than re-derived because the apply checks no parent type (<c>RollupResolver</c>): a
    /// <c>decimal(12,2)</c> sum over an integer child, or a count declared decimal, is legal, and re-deriving it
    /// silently retyped the column and dropped its precision and scale.
    /// </remarks>
    public FieldType RollupType => KeepsDeclaredType ? _declaredType : DerivedType;

    /// <summary>Whether the declared type is kept — see <see cref="RollupType"/>.</summary>
    public bool KeepsDeclaredType => StillDeclaredRollup && _declared.ContainsKey("type") && Holds(_declaredType, DerivedType);

    /// <summary>The types the computed chips offer: <see cref="ComputedTypes"/>, and the declared type of a computed field.</summary>
    public IReadOnlyList<FieldType> ComputedTypesOffered
        => _declaredKind == FieldKind.Computed && !ComputedTypes.Contains(_declaredType)
            ? [.. ComputedTypes, _declaredType]
            : ComputedTypes;

    /// <summary>Whether the declaration carries a <c>rollup.where</c>.</summary>
    public bool DeclaresRollupFilter => DeclaredRollup?["where"] is not null;

    private JsonObject? DeclaredRollup => _declared["rollup"] as JsonObject;

    /// <summary>Whether the field was declared a rollup and still is one.</summary>
    private bool StillDeclaredRollup => Kind == FieldKind.Rollup && _declaredKind == FieldKind.Rollup;

    /// <summary>The aggregated child field, when it is one <see cref="Aggregatable"/> allows.</summary>
    private RollupChildField? Aggregated => Aggregatable.FirstOrDefault(child => child.Name == RollupField);

    /// <summary>
    /// The declared aggregated field, while the rollup still declares it from the same entity with the same op and it
    /// exists on the child — which is all <c>RollupResolver.EnsureAggregatedFieldIsResolvable</c> asks.
    /// </summary>
    private RollupChildField? KeptAggregate
        => StillDeclaredRollup && RollupField.Length > 0
            && RollupFrom == Text(DeclaredRollup?["from"]) && RollupOp == Text(DeclaredRollup?["op"])
            && RollupField == Text(DeclaredRollup?["field"])
            ? Source?.Fields.FirstOrDefault(child => child.Name == RollupField)
            : null;

    /// <summary>Whether a column of <paramref name="declared"/> holds an aggregate of <paramref name="aggregate"/> — the same type, or an integer widened to a decimal.</summary>
    private static bool Holds(FieldType declared, FieldType aggregate)
        => declared == aggregate || (declared == FieldType.Decimal && aggregate == FieldType.Integer);

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

        WriteRollupType(facets);
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
        if (RollupOp == "count" || Aggregated is not null)
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

    /// <summary>
    /// Writes <see cref="RollupType"/> and drops only the facets of other types; the editor's own <see cref="Type"/>
    /// is not touched, so a successful build leaves the sheet as the operator left it.
    /// </summary>
    private void WriteRollupType(JsonObject facets)
    {
        var type = RollupType;
        facets["type"] = Word(type);
        ClearFacetsOfOtherTypes(facets, type);

        if (type == FieldType.Decimal)
        {
            facets["precision"] ??= Aggregated?.Precision ?? DefaultPrecision;
            facets["scale"] ??= Aggregated?.Scale ?? DefaultScale;
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

    /// <summary>Writes the rollup object's keys in place.</summary>
    /// <remarks>
    /// A <c>field</c> on a count and a <c>via</c> that is not a ref here are dropped, each with a note
    /// (<see cref="DroppedFromRollup"/>): the apply ignores the first (<c>RollupResolver.cs:66</c>) and refuses the
    /// second (<c>:256-263</c>).
    /// </remarks>
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

    /// <summary>Writes a computed column: the expression and a chosen type.</summary>
    private string? WriteComputed(JsonObject facets)
    {
        if (RefuseComputed(Computed.Trim()) is { } refusal)
        {
            return refusal;
        }

        facets.Remove("rollup");
        facets["type"] = Word(Type);
        facets["computed"] = Computed.Trim();
        Toggle(facets, "index", Indexed);
        ClearFacetsOfOtherTypes(facets);
        return WriteDefault(facets) ?? WriteTypeFacets(facets);
    }

    private string? RefuseComputed(string expression)
    {
        if (expression.Length == 0)
        {
            return "A computed field needs its expression — CEL over this row's own fields, such as unit_price * amount.";
        }

        if (expression.Length > CelMaxLength)
        {
            return string.Create(
                CultureInfo.InvariantCulture, $"A computed expression is at most {CelMaxLength} characters — the schema's limit for CEL.");
        }

        return ComputedTypesOffered.Contains(Type)
            ? null
            : $"A computed value is stored as a generated column, offered here as {string.Join(", ", ComputedTypes.Select(Word))}.";
    }

    /// <summary>A kept <c>rollup.where</c> is refused at apply (<c>RollupResolver.cs:107</c>).</summary>
    private string? RefuseAKeptFilter(JsonObject facets)
        => Kind == FieldKind.Rollup && facets["rollup"]?["where"] is not null
            ? "Its rollup declares a 'where' filter, which this build refuses at apply (rollup.where). Tick \"Remove the declared filter\" to save the field."
            : null;

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
