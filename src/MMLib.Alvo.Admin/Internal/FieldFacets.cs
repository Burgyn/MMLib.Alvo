using MMLib.Alvo.Schema;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace MMLib.Alvo.Admin.Internal;

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
internal sealed class FieldFacets
{
    private const int DefaultMaxLength = 120;
    private const int DefaultPrecision = 10;
    private const int DefaultScale = 2;

    private static readonly string[] _typedFacets = ["maxLength", "precision", "scale", "values", "entity"];

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

    /// <summary>A string's maximum length.</summary>
    public int MaxLength { get; set; } = DefaultMaxLength;

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

    /// <summary>Whether the declaration is <c>computed</c> or a <c>rollup</c>, whose value is maintained for it.</summary>
    public bool MaintainedElsewhere { get; set; }

    /// <summary>Whether the type may be declared unique.</summary>
    public bool TakesUnique => Type is FieldType.String or FieldType.Integer or FieldType.Uuid
        or FieldType.Date or FieldType.DateTime;

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

        return new FieldFacets
        {
            Name = name,
            Type = Enum.TryParse<FieldType>(facets["type"]?.GetValue<string>(), ignoreCase: true, out var type)
                ? type : FieldType.String,
            Required = facets["required"]?.GetValue<bool>() ?? false,
            Unique = facets["unique"]?.GetValue<bool>() ?? false,
            Indexed = facets["index"]?.GetValue<bool>() ?? false,
            MaxLength = facets["maxLength"]?.GetValue<int>() ?? DefaultMaxLength,
            Precision = facets["precision"]?.GetValue<int>() ?? DefaultPrecision,
            Scale = facets["scale"]?.GetValue<int>() ?? DefaultScale,
            Target = facets["entity"]?.GetValue<string>() ?? string.Empty,
            MaintainedElsewhere = facets["computed"] is not null || facets["rollup"] is not null,
            Default = facets["default"] is { } declared and not JsonArray ? declared.ToString() : string.Empty,
            Values = facets["values"] is JsonArray values
                ? string.Join(", ", values.Select(value => value?.GetValue<string>()))
                : string.Empty,
        };
    }

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
        facets["type"] = Type.ToString().ToLowerInvariant();
        Toggle(facets, "required", Required);

        refusal = WriteDefault(facets);
        if (refusal is not null)
        {
            return null;
        }

        Toggle(facets, "unique", Unique);
        Toggle(facets, "index", Indexed);
        ClearFacetsOfOtherTypes(facets);

        refusal = WriteTypeFacets(facets);
        return refusal is null ? facets : null;
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
    /// Writes the declared default into the facets as a literal of the field's own type, or says why it
    /// cannot be one.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Typed here rather than sent as a string.</b> The descriptor's <c>default</c> is a JSON literal, and
    /// the apply refuses one whose kind the field cannot hold — so a box that always wrote a string would
    /// produce a descriptor refused for a reason the operator never typed. An empty box removes the key: a
    /// field with no default is the normal case, and a <c>null</c> would be a declaration of nothing.
    /// </para>
    /// <para>
    /// A control that was never drawn must not remove what the declaration carries: a json or computed
    /// field's own default is one the editor cannot draw, and dropping it would be a silent narrowing.
    /// </para>
    /// </remarks>
    private string? WriteDefault(JsonObject facets)
    {
        if (!TakesADefault)
        {
            return null;
        }

        if (Default.Length == 0)
        {
            facets.Remove("default");
            return null;
        }

        if (TypedDefault() is { } literal)
        {
            facets["default"] = literal;
            return null;
        }

        return $"'{Default}' is not a number, and this field's default has to be one.";
    }

    /// <summary>The default as a literal of the field's type, or nothing when a number does not parse.</summary>
    private JsonNode? TypedDefault() => Type switch
    {
        FieldType.Boolean => Default == "true",
        FieldType.Integer => long.TryParse(Default, CultureInfo.InvariantCulture, out var whole) ? whole : null,
        FieldType.Decimal => decimal.TryParse(Default, CultureInfo.InvariantCulture, out var number) ? number : null,
        _ => Default,
    };

    /// <summary>
    /// Drops the facets that belong to a type this field no longer has.
    /// </summary>
    /// <remarks>
    /// A <c>maxLength</c> left behind on a field retyped to <c>integer</c>, or a <c>format</c> on one retyped
    /// away from <c>string</c>, is a descriptor the apply refuses — and refused for a facet nobody typed,
    /// which is the worst kind of refusal to read. The facets the current type needs are written back after.
    /// </remarks>
    private void ClearFacetsOfOtherTypes(JsonObject facets)
    {
        foreach (var facet in _typedFacets)
        {
            facets.Remove(facet);
        }

        if (Type is not FieldType.Ref)
        {
            facets.Remove("onDelete");
        }

        if (Type is not FieldType.String)
        {
            facets.Remove("format");
        }
    }

    /// <summary>Writes the facets the type requires, or says which one is missing.</summary>
    private string? WriteTypeFacets(JsonObject facets)
    {
        switch (Type)
        {
            case FieldType.String:
                facets["maxLength"] = MaxLength;
                return null;
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
}
