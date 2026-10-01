using MMLib.Alvo.Schema;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace MMLib.Alvo.Admin.Components.Schema;

/// <summary>
/// What the field editor holds, and the declaration it builds from it in the schema's own shape.
/// </summary>
/// <remarks>
/// <para>
/// <b>The controls change with the type</b>, because the frozen schema's if/then rules decide which facets a
/// type may carry — <c>decimal</c> needs precision and scale, <c>enum</c> needs values, <c>ref</c> needs an
/// entity. Building a facet a type may not carry would be building a descriptor the apply refuses, so the
/// builder refuses it first, in the words the operator can act on.
/// </para>
/// <para>
/// <b>Out of the component so it can be tested</b> (docs/architecture/admin-dashboard-review.md, F-13): the
/// component draws these values and raises what <see cref="Build"/> returns; every refusal is decided here.
/// </para>
/// </remarks>
internal sealed partial class FieldFacets
{
    private const int DefaultPrecision = 10;
    private const int DefaultScale = 2;

    /// <summary>Every facet that belongs to exactly one type (the frozen schema's if/then rules).</summary>
    private static readonly string[] _typedFacets = ["maxLength", "format", "precision", "scale", "values", "entity", "onDelete"];

    /// <summary>The declaration the editor was opened on — empty for a new field. Never written to.</summary>
    private JsonObject _declared = [];

    /// <summary>The type it was declared with, so a facet of the old type can be told from one of the current.</summary>
    private FieldType _declaredType = FieldType.String;

    /// <summary>The field's name as typed.</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>The field's type.</summary>
    public FieldType Type { get; set; } = FieldType.String;

    /// <summary>Whether a write must carry the field.</summary>
    public bool Required { get; set; }

    /// <summary>Whether the field is unique across the entity.</summary>
    public bool Unique { get; set; }

    /// <summary>Whether the field carries an index of its own.</summary>
    public bool Indexed { get; set; }

    /// <summary>A string's maximum length, or <see langword="null"/> for none.</summary>
    /// <remarks>
    /// <b>Absent is a declaration the editor must be able to keep.</b> The frozen schema makes <c>maxLength</c>
    /// optional (minimum 1) and the mapper leaves such a column unbounded (<c>DescriptorToSchemaMapper.cs:406</c>).
    /// This used to default to 120 and was always written, so opening Edit on an unbounded string — even to rename
    /// it — narrowed a column nobody asked to narrow (docs/todo-admin.md §8d item 15).
    /// </remarks>
    public int? MaxLength { get; set; }

    /// <summary>A decimal's total digits.</summary>
    public int Precision { get; set; } = DefaultPrecision;

    /// <summary>A decimal's digits after the point.</summary>
    public int Scale { get; set; } = DefaultScale;

    /// <summary>An enum's values, comma separated as typed.</summary>
    public string Values { get; set; } = string.Empty;

    /// <summary>The entity a ref points at.</summary>
    public string Target { get; set; } = string.Empty;

    /// <summary>The default as typed, or empty for none.</summary>
    public string Default { get; set; } = string.Empty;

    /// <summary>Whether the type may be declared unique.</summary>
    public bool TakesUnique => Type is FieldType.String or FieldType.Integer or FieldType.Uuid
        or FieldType.Date or FieldType.DateTime;

    /// <summary>Whether a declared default no box shows — a <c>$cel</c> object, or one no control draws — is dropped on save.</summary>
    public bool RemoveUndrawnDefault { get; set; }

    /// <summary>
    /// Whether the unique checkbox is drawn: for a type that takes one, and wherever the declaration already
    /// carries one.
    /// </summary>
    /// <remarks>
    /// The apply maps <c>unique</c> for every type (<c>DescriptorToSchemaMapper.cs:404</c>), so a declared
    /// <c>unique: true</c> on a ref is legal — and was unclearable, because the box was hidden and the prefilled
    /// value still written (§8d item 17). A control is owed for every facet the save writes.
    /// </remarks>
    public bool UniqueOffered => Kind == FieldKind.Supplied && (TakesUnique || Flag(_declared["unique"]));

    /// <summary>Whether the field's type or kind differs from the one it was declared with.</summary>
    private bool Retyped => Type != _declaredType || Kind != _declaredKind;

    /// <summary>
    /// Whether the declaration carries a default no box can show, on the type it was declared for — a
    /// <c>$cel</c> object, or a literal on a type the editor draws no default for (json, a maintained value).
    /// </summary>
    public bool KeepsUndrawnDefault
        => !Retyped && _declared["default"] is { } declared && (declared is not JsonValue || !TakesADefault);

    /// <summary>Whether the default box is drawn: the type takes a literal, and nothing undrawn is being kept.</summary>
    public bool DrawsDefault => TakesADefault && !(KeepsUndrawnDefault && !RemoveUndrawnDefault);

    /// <summary>
    /// Whether this build fills a default in for the field as it currently stands.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A <c>ref</c> is left out because a default foreign key is a row that must already exist, which the
    /// apply cannot promise, and <c>json</c> because a one-line box would write the string <c>"{}"</c> where
    /// the author meant the object. Every other type takes a literal.
    /// </para>
    /// <para>
    /// A <c>computed</c> or <c>rollup</c> field is left out for the reason the apply gives: its value is
    /// maintained for it, so it can never fall back to a default, and the frozen schema forbids the pair.
    /// </para>
    /// </remarks>
    public bool TakesADefault => Type is not (FieldType.Ref or FieldType.Json) && !MaintainedElsewhere;

    /// <summary>An example of the literal this field's type takes, so the box is not a guess.</summary>
    public string DefaultPlaceholder => Type switch
    {
        FieldType.Enum => SplitValues().FirstOrDefault() is { } first ? $"e.g. {first}" : "one of the values above",
        _ => $"e.g. {DefaultExample}",
    };

    private string DefaultExample => Type switch
    {
        FieldType.Integer => "0",
        FieldType.Decimal => "0.00",
        FieldType.Uuid => "00000000-0000-0000-0000-000000000000",
        FieldType.Date => "2026-01-01",
        FieldType.DateTime => "2026-01-01T09:00:00Z",
        FieldType.Text => "a paragraph of prose",
        _ => "normal",
    };

    /// <summary>The editor's values for a declared field, read from its facets.</summary>
    /// <param name="name">The field's name.</param>
    /// <param name="json">Its facets as the working copy carries them.</param>
    public static FieldFacets Prefill(string name, string? json)
    {
        var facets = Parse(json);
        var type = Enum.TryParse<FieldType>(Text(facets["type"]), ignoreCase: true, out var declared)
            ? declared : FieldType.String;

        var editor = new FieldFacets
        {
            Name = name,
            Type = type,
            Required = Flag(facets["required"]),
            Unique = Flag(facets["unique"]),
            Indexed = Flag(facets["index"]),
            MaxLength = Whole(facets["maxLength"]),
            Precision = Whole(facets["precision"]) ?? DefaultPrecision,
            Scale = Whole(facets["scale"]) ?? DefaultScale,
            Target = Text(facets["entity"]),
            Default = LiteralText(facets["default"]),
            Values = facets["values"] is JsonArray values ? string.Join(", ", values.Select(Text)) : string.Empty,
            _declared = facets,
            _declaredType = type,
        };

        editor.ReadMaintained(facets);
        return editor;
    }

    /// <summary>
    /// A declared literal as the box shows it; a <c>$cel</c> object or an array is not one, and the box stays empty
    /// rather than holding the object's JSON — which a string field then saved as the literal <c>"{"$cel":…}"</c>.
    /// </summary>
    private static string LiteralText(JsonNode? declared) => declared switch
    {
        JsonValue value when value.TryGetValue<string>(out var text) => text,
        JsonValue value => value.ToJsonString(),
        _ => string.Empty,
    };

    private static string Text(JsonNode? node)
        => node is JsonValue value && value.TryGetValue<string>(out var text) ? text : string.Empty;

    private static bool Flag(JsonNode? node)
        => node is JsonValue value && value.TryGetValue<bool>(out var on) && on;

    private static int? Whole(JsonNode? node)
        => node is JsonValue value && value.TryGetValue<int>(out var number) ? number : (int?)null;

    /// <summary>A type as the descriptor spells it.</summary>
    internal static string Word(FieldType type) => type.ToString().ToLowerInvariant();

    /// <summary>
    /// The facets as an object, or an empty one — a field whose declaration the editor cannot read is one
    /// it must not silently rewrite, so the refusal surfaces when <see cref="Build"/> runs.
    /// </summary>
    public static JsonObject Parse(string? json)
    {
        try
        {
            return json is { Length: > 0 } ? JsonNode.Parse(json) as JsonObject ?? [] : [];
        }
        catch (JsonException)
        {
            return [];
        }
    }

    /// <summary>
    /// Builds the field in the schema's own shape, or says why it cannot.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The refusals are the frozen schema's own required facets — a <c>decimal</c> with no precision, an
    /// <c>enum</c> with no values, a <c>ref</c> with no entity. Each is a descriptor the apply rejects.
    /// </para>
    /// <para>
    /// <b>An edit starts from the declaration that is there, not from an empty object.</b> The editor draws
    /// the facets it can; a field may also carry <c>description</c>, <c>format</c>, <c>nullable</c>,
    /// <c>renamedFrom</c> or an <c>x-</c> extension, and rebuilding from scratch would drop every one of
    /// them — the silent narrowing design §4.6 forbids of the export, arriving through the editor instead.
    /// </para>
    /// </remarks>
    /// <param name="editing">The declared field being edited, or <see langword="null"/> for a new one.</param>
    /// <param name="editingJson">The edited field's current facets.</param>
    /// <param name="siblings">The field names the entity already declares.</param>
    /// <param name="refusal">Why the field cannot be built, when it cannot.</param>
    /// <returns>The facets, or <see langword="null"/> with <paramref name="refusal"/> set.</returns>
    public JsonObject? Build(string? editing, string? editingJson, IReadOnlyList<string> siblings, out string? refusal)
    {
        refusal = RefuseName(editing, siblings);
        if (refusal is not null)
        {
            return null;
        }

        var facets = editing is { Length: > 0 } ? Parse(editingJson) : [];
        refusal = Kind switch
        {
            FieldKind.Rollup => WriteRollup(facets),
            FieldKind.Computed => WriteComputed(facets),
            _ => WriteSupplied(facets),
        } ?? RefuseWhatTheApplyRefuses(facets);
        return refusal is null ? facets : null;
    }

    /// <summary>
    /// Writes a field a caller supplies. Every key that already exists is set in place, so an edit that changes
    /// nothing writes the declaration back unchanged — a staged row that says "changed" for an unchanged field is a
    /// second kind of lie about what the operator did.
    /// </summary>
    private string? WriteSupplied(JsonObject facets)
    {
        facets.Remove("rollup");
        facets.Remove("computed");
        facets["type"] = Word(Type);
        Toggle(facets, "required", Required);
        if (WriteDefault(facets) is { } refused)
        {
            return refused;
        }

        if (UniqueOffered)
        {
            Toggle(facets, "unique", Unique);
        }

        Toggle(facets, "index", Indexed);
        ClearFacetsOfOtherTypes(facets);
        return WriteTypeFacets(facets);
    }

    /// <summary>Why the typed name cannot be used, or nothing.</summary>
    /// <remarks>
    /// A rename onto a name that is taken would merge two declarations into one, silently, and an addition
    /// under one would replace the declaration that is there.
    /// </remarks>
    private string? RefuseName(string? editing, IReadOnlyList<string> siblings)
    {
        if (!DescriptorNames.IsMember(Name))
        {
            return $"A field name must match {DescriptorNames.Member}.";
        }

        return !string.Equals(Name, editing, StringComparison.Ordinal) && siblings.Contains(Name, StringComparer.Ordinal)
            ? $"This entity already declares a field called {Name}."
            : null;
    }

    /// <summary>
    /// Writes the default the box holds, or does to a declared one what <see cref="DefaultFate"/> decides.
    /// </summary>
    /// <remarks>
    /// Typed here rather than sent as a string: the descriptor's <c>default</c> is a JSON literal and the apply
    /// refuses one its field cannot hold (<c>FieldDefault.cs:190-199</c>). A declared default no box draws is
    /// kept or removed — never rewritten from the box's prefill.
    /// </remarks>
    private string? WriteDefault(JsonObject facets)
    {
        if (DrawsDefault)
        {
            return WriteTypedDefault(facets);
        }

        if (DefaultFate() == FacetFate.Removed)
        {
            facets.Remove("default");
        }

        return null;
    }

    private string? WriteTypedDefault(JsonObject facets)
    {
        if (Default.Length == 0)
        {
            facets.Remove("default");
            return null;
        }

        if (TypedDefault() is not { } literal)
        {
            return DefaultRefusal();
        }

        facets["default"] = literal;
        return null;
    }

    /// <summary>
    /// What the save does to a declared default no box draws, or <see langword="null"/> when the box decides.
    /// </summary>
    private FacetFate? DefaultFate()
        => _declared["default"] is null || DrawsDefault ? null
            : KeepsUndrawnDefault && !RemoveUndrawnDefault ? FacetFate.Kept
            : FacetFate.Removed;

    /// <summary>The default as a literal of the field's type, or nothing when the apply would refuse it.</summary>
    /// <remarks>
    /// Each arm is <c>FieldDefault</c>'s own check (<c>Fits</c>, <c>Excluded</c>): a boolean is <c>true</c> or
    /// <c>false</c> and nothing else — anything else used to be saved as <c>false</c> — a uuid parses, a date
    /// parses, an enum default is one of its values, a string fits its max length.
    /// </remarks>
    private JsonNode? TypedDefault() => Type switch
    {
        FieldType.Boolean => Default switch { "true" => JsonValue.Create(true), "false" => JsonValue.Create(false), _ => null },
        FieldType.Integer => long.TryParse(Default, CultureInfo.InvariantCulture, out var whole) ? whole : null,
        FieldType.Decimal => decimal.TryParse(Default, CultureInfo.InvariantCulture, out var number) ? number : null,
        FieldType.Uuid => Guid.TryParse(Default, out _) ? Default : null,
        FieldType.Date or FieldType.DateTime
            => DateTimeOffset.TryParse(Default, CultureInfo.InvariantCulture, out _) ? Default : null,
        FieldType.Enum => SplitValues().Contains(Default, StringComparer.Ordinal) ? Default : null,
        _ => MaxLength is { } max && Default.Length > max ? null : Default,
    };

    /// <summary>Why <see cref="TypedDefault"/> found nothing, in the field's own terms.</summary>
    private string DefaultRefusal() => Type switch
    {
        FieldType.Integer or FieldType.Decimal => $"'{Default}' is not a number, and this field's default has to be one.",
        FieldType.Boolean => $"'{Default}' is not true or false, and this field's default has to be one.",
        FieldType.Uuid => $"'{Default}' is not a uuid, and this field's default has to be one.",
        FieldType.Date or FieldType.DateTime => $"'{Default}' is not a date, and this field's default has to be one.",
        FieldType.Enum => $"'{Default}' is not one of the values above, and this field's default has to be one.",
        _ => string.Create(
            CultureInfo.InvariantCulture, $"'{Default}' is {Default.Length} characters and the max length is {MaxLength}."),
    };

    /// <summary>Drops the facets that belong to a type this field no longer has, and only those.</summary>
    /// <remarks>
    /// The current type's own facets are left where they stand and set in place by <see cref="WriteTypeFacets"/>;
    /// removing and re-adding them moved them to the end of the object and badged an untouched field "changed".
    /// </remarks>
    private void ClearFacetsOfOtherTypes(JsonObject facets) => ClearFacetsOfOtherTypes(facets, Type);

    /// <summary>Drops the facets that belong to any type but <paramref name="type"/> — the one being written.</summary>
    private static void ClearFacetsOfOtherTypes(JsonObject facets, FieldType type)
    {
        foreach (var facet in _typedFacets.Where(facet => OwnerOf(facet) != type))
        {
            facets.Remove(facet);
        }
    }

    /// <summary>The one type a type-bound facet belongs to.</summary>
    private static FieldType OwnerOf(string facet) => facet switch
    {
        "maxLength" or "format" => FieldType.String,
        "precision" or "scale" => FieldType.Decimal,
        "values" => FieldType.Enum,
        _ => FieldType.Ref,
    };

    /// <summary>Writes the facets the type requires, or says which one is missing.</summary>
    private string? WriteTypeFacets(JsonObject facets)
    {
        switch (Type)
        {
            case FieldType.String:
                return WriteMaxLength(facets);
            case FieldType.Decimal:
                facets["precision"] = Precision;
                facets["scale"] = Scale;
                return null;
            case FieldType.Enum:
                return WriteValues(facets);
            case FieldType.Ref:
                return WriteTarget(facets);
            default:
                return null;
        }
    }

    private string? WriteMaxLength(JsonObject facets)
    {
        if (MaxLength is not { } max)
        {
            facets.Remove("maxLength");
            return null;
        }

        if (max < 1)
        {
            return "A max length is at least 1 — the schema's minimum. Leave the box empty for no limit.";
        }

        facets["maxLength"] = max;
        return null;
    }

    private string? WriteValues(JsonObject facets)
    {
        var values = SplitValues();
        if (values.Length == 0)
        {
            return "An enum needs at least one value — the schema requires it.";
        }

        facets["values"] = new JsonArray([.. values.Select(value => JsonValue.Create(value))]);
        return null;
    }

    private string? WriteTarget(JsonObject facets)
    {
        if (Target.Length == 0)
        {
            return "A ref needs the entity it points at — the schema requires it.";
        }

        facets["entity"] = Target;
        facets["onDelete"] ??= "restrict";
        return null;
    }

    private string[] SplitValues()
        => Values.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    private static void Toggle(JsonObject facets, string facet, bool on)
    {
        if (on)
        {
            facets[facet] = true;
            return;
        }

        facets.Remove(facet);
    }

    private const string CelDefaultRefusal =
        "Its declared default is a '$cel' expression, which this build refuses at apply (field.default). Tick "
        + "\"Remove the declared default\" to save the field, or declare a literal.";

    private const string MaintainedDefaultRefusal =
        "It declares a default beside a value that is maintained for it, which the apply refuses — that value can "
        + "never fall back to a default. Tick \"Remove the declared default\" to save the field.";

    private const string RequiredReadOnlyRefusal =
        "This field is readOnly: true, so no create could ever supply it, and the apply refuses 'required' beside "
        + "it. Give it a default, or leave required off.";

    /// <summary>What the apply would still refuse about the built declaration, checked on the result itself.</summary>
    private string? RefuseWhatTheApplyRefuses(JsonObject facets)
        => RefuseAKeptDefault(facets) ?? RefuseRequiredBesideReadOnly(facets) ?? RefuseAKeptFilter(facets);

    /// <summary>A kept default the apply refuses: <c>$cel</c> (<c>UnhonouredFeatures.cs:63</c>) or beside a maintained value (<c>FieldDefault.cs:88</c>).</summary>
    private string? RefuseAKeptDefault(JsonObject facets)
    {
        if (DefaultFate() != FacetFate.Kept || facets["default"] is not { } kept)
        {
            return null;
        }

        return IsCel(kept) ? CelDefaultRefusal : MaintainedElsewhere ? MaintainedDefaultRefusal : null;
    }

    /// <summary><c>DescriptorValidator.IsRequiredAndStaticallyReadOnly</c> (<c>DescriptorValidator.cs:504-521</c>), asked of the result.</summary>
    private static string? RefuseRequiredBesideReadOnly(JsonObject facets)
        => Flag(facets["required"]) && Flag(facets["readOnly"]) && (facets["default"] is null || IsCel(facets["default"]))
            ? RequiredReadOnlyRefusal
            : null;

    private static bool IsCel(JsonNode? node) => node is JsonObject tagged && tagged.ContainsKey("$cel");
}
