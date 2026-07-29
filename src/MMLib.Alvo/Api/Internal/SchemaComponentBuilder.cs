using Microsoft.OpenApi;
using MMLib.Alvo.Schema;
using System.Text.Json.Nodes;

namespace MMLib.Alvo.Api.Internal;

/// <summary>
/// Turns one entity of the applied schema into the five JSON Schema components the generated OpenAPI
/// document references: the row a single read, a create or an update returns; the page item a list's rows
/// are (the same fields, without the row's <c>required</c> list); the body a create accepts; and the body a
/// patch accepts.
/// </summary>
/// <remarks>
/// <para>
/// <b>Built by hand rather than through <c>GetOrCreateSchemaAsync</c>, and that is not the lazy option.</b>
/// That helper reflects over a CLR type, and a generated endpoint has none: its payload is an
/// <c>IReadOnlyDictionary&lt;string, object?&gt;</c>, which reflects to <c>object</c> and documents nothing.
/// The declared shape only exists in <see cref="EntitySchema"/>, so the only source that can produce a
/// substantive schema is the applied schema itself.
/// </para>
/// <para>
/// <b>Two response schemas, not one, because <c>select</c> narrows only one of them.</b> <c>GetAsync</c>
/// takes no projection and a single-row read therefore always carries every field, so <see cref="RowId"/>
/// carries a real <c>required</c> list — every readable field is present, even when its value is
/// <see langword="null"/>. A list's rows are the one shape <c>select</c> can narrow, so
/// <see cref="PageItemId"/> repeats the same properties with no <c>required</c> list at all. Collapsing them
/// into one schema would either lie about a projected page (a false <c>required</c>) or defeat the point of a
/// generated client reading a single row (no <c>required</c> at all, not even <c>id</c>).
/// </para>
/// <para>
/// <b>Two write schemas as well, because a create and a patch really differ.</b> A create accepts only what
/// a caller may supply and states which of those are mandatory; a patch accepts the same set and mandates
/// none of it, because <c>IAlvoData.UpdateAsync</c> is partial by contract. Collapsing them into one schema
/// annotated with <c>readOnly</c> would leave a generated client unable to tell a required create field from
/// an optional patch one — which is the single most useful thing the document can say about a write.
/// </para>
/// <para>
/// <b>A <c>hidden</c> field is excluded from both response schemas, and appears in a write schema if and
/// only if the descriptor also marks it <c>required</c>.</b> That is a narrowed confidentiality rule, not the
/// absolute one it might look like: excluding a hidden field from <em>every</em> schema — including a create
/// a caller must actually be able to perform — would silently drop a mandatory field from the body a caller
/// has to send, since a required field a caller cannot see cannot be supplied at all. The name of an
/// <em>optional</em> hidden field never appears anywhere, which is the confidentiality guarantee the port's
/// refusal wording is built on; the name of a <em>required</em> hidden field is published only where a
/// caller must read it to use the create and the patch at all — never in a response. A field is excluded
/// when the descriptor carries <em>any</em> <c>hidden</c> flag for it — a static <c>true</c> or a per-role
/// expression — because a name published for the callers who may read it is published to the callers who may
/// not.
/// </para>
/// <para>
/// <b>The cost of that, stated:</b> a field that is hidden, writable and optional is absent from the request
/// schemas while a write to it is still accepted, so the document understates what a create will take. The
/// asymmetry is deliberate — <c>hidden</c> governs responses and <c>readOnly</c> governs writes — and the
/// direction of the loss is the safe one: a caller who follows the document sends less than it may, never
/// something that is refused.
/// </para>
/// <para>
/// <b>Neither write schema sets <c>additionalProperties: false</c>,</b> even though an undeclared key really
/// is refused with 422. It would be a lie for exactly the fields above: a hidden, writable and optional field
/// is accepted and is not in the schema, so a validator reading <c>additionalProperties: false</c> would
/// reject a body the API takes. The refusal is stated in the operation's own description instead, where it
/// costs no accuracy.
/// </para>
/// </remarks>
/// <param name="entity">The entity as the applied schema declares it.</param>
/// <param name="hidden">
/// Every field the descriptor carries a <c>hidden</c> flag for — statically or per caller. Not the mask one
/// caller resolved to: a document has no caller.
/// </param>
/// <param name="readOnly">Every field the descriptor carries a <c>readOnly</c> flag for, on the same basis.</param>
internal sealed class SchemaComponentBuilder(
    EntitySchema entity, IReadOnlySet<string> hidden, IReadOnlySet<string> readOnly)
{
    /// <summary>The component id of the row a single read, a create or an update returns.</summary>
    /// <remarks>
    /// <para>
    /// The entity's own name, and the three siblings below suffix it with a capitalised word. That is
    /// collision-proof by construction rather than by luck: the descriptor's field and entity grammar is
    /// <c>^[a-z][a-z0-9_]{0,62}$</c>, so no entity name can carry an upper-case letter and no suffixed id can
    /// therefore collide with another entity's plain one. An underscore separator could — an entity called
    /// <c>orders_page</c> would collide with <c>orders</c>'s envelope.
    /// </para>
    /// </remarks>
    /// <param name="entity">The entity name.</param>
    internal static string RowId(string entity) => entity;

    /// <summary>The component id of the body a create accepts.</summary>
    /// <param name="entity">The entity name.</param>
    internal static string CreateId(string entity) => entity + "Create";

    /// <summary>The component id of the body a patch accepts.</summary>
    /// <param name="entity">The entity name.</param>
    internal static string PatchId(string entity) => entity + "Patch";

    /// <summary>The component id of the page envelope a list returns.</summary>
    /// <param name="entity">The entity name.</param>
    internal static string PageId(string entity) => entity + "Page";

    /// <summary>The component id of one item inside a list's page — the same fields as <see cref="RowId"/>, with no <c>required</c> list.</summary>
    /// <param name="entity">The entity name.</param>
    internal static string PageItemId(string entity) => entity + "PageItem";

    /// <summary>Registers this entity's five components on <paramref name="document"/>.</summary>
    /// <param name="document">The document being built.</param>
    internal void AddTo(OpenApiDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);
        document.AddComponent(RowId(entity.Name), Row());
        document.AddComponent(PageItemId(entity.Name), PageItem());
        document.AddComponent(CreateId(entity.Name), Body(isUpdate: false));
        document.AddComponent(PatchId(entity.Name), Body(isUpdate: true));
        document.AddComponent(PageId(entity.Name), Page(document));
    }

    /// <summary>
    /// The row a single read, a create or an update returns: every field the caller may see, with the
    /// framework's own columns annotated <c>readOnly</c> — and, because none of those three operations can
    /// narrow the projection, a <c>required</c> list naming every one of them.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b><c>required</c> lists every readable field, deliberately.</b> <c>GetAsync</c> takes no projection,
    /// so this schema's row is never partial — a field with no value is present and <see langword="null"/>,
    /// never absent. A generated client that read no <c>required</c> list here would have to treat <c>id</c>
    /// itself as optional, which defeats the one thing a single-row read can promise that a page's row
    /// cannot.
    /// </para>
    /// <para>
    /// <b>Open to further properties, deliberately.</b> A per-role <c>hidden</c> expression means a response
    /// legitimately carries a field this document does not declare — for the callers the expression does not
    /// mask. Closing the schema would make the document contradict a real response.
    /// </para>
    /// </remarks>
    private OpenApiSchema Row() => new()
    {
        Type = JsonSchemaType.Object,
        Title = entity.Name,
        Description = RowDescription(),
        Properties = Fields(readable: true, isUpdate: false),
        Required = ReadableFieldNames(),
    };

    /// <summary>
    /// One item inside a list's page: the same properties as <see cref="Row"/>, with no <c>required</c> list.
    /// </summary>
    /// <remarks>
    /// <c>select</c> narrows exactly this shape — the only one of the three read-ish responses it can touch,
    /// since a single-row read and a write's echo take no projection — so a schema that required anything
    /// here would be violated by the very page it describes. What a caller needs to know instead is which
    /// fields can be <see langword="null"/>, and that is in each field's own type.
    /// </remarks>
    private OpenApiSchema PageItem() => new()
    {
        Type = JsonSchemaType.Object,
        Title = PageItemId(entity.Name),
        Description = PageItemDescription(),
        Properties = Fields(readable: true, isUpdate: false),
    };

    private string RowDescription() =>
        (entity.Description is { } declared ? declared + "\n\n" : string.Empty)
        + "One row as a single read, a create or an update returns it. Every field is present, even one with "
        + "no value — that field is present and null. Fields marked read-only are written by the framework "
        + "and refused in a request body.";

    private string PageItemDescription() =>
        (entity.Description is { } declared ? declared + "\n\n" : string.Empty)
        + "One row inside a list's page. Every field is present unless the request narrowed the projection "
        + "with `select`; a field with no value is present and null. Fields marked read-only are written by "
        + "the framework and refused in a request body.";

    /// <summary>Every field this schema's read side may show, in the schema's own order.</summary>
    private HashSet<string>? ReadableFieldNames()
    {
        var names = entity.Fields
            .Where(field => Belongs(field, readable: true, isUpdate: false))
            .Select(field => field.Name)
            .ToHashSet(StringComparer.Ordinal);

        return names.Count == 0 ? null : names;
    }

    /// <summary>
    /// The body a write accepts: the fields a caller may supply, and — on a create only — which of them are
    /// mandatory.
    /// </summary>
    /// <remarks>
    /// <see cref="AlvoManagedColumns.IsCallerWritable"/> decides which framework-managed columns survive,
    /// rather than a second list here: it is the same authority the port's write guard refuses by, so the
    /// document cannot advertise a column the write would reject — <c>tenant_id</c> on a create being the one
    /// entry where the answer is yes.
    /// </remarks>
    /// <param name="isUpdate">Whether this is the patch body rather than the create body.</param>
    private OpenApiSchema Body(bool isUpdate) => new()
    {
        Type = JsonSchemaType.Object,
        Title = isUpdate ? PatchId(entity.Name) : CreateId(entity.Name),
        Description = BodyDescription(isUpdate),
        Properties = Fields(readable: false, isUpdate),
        Required = isUpdate ? null : Mandatory(),
    };

    private static string BodyDescription(bool isUpdate) => isUpdate
        ? "The fields to change. A field this object does not mention keeps its stored value, so nothing here "
        + "is mandatory — that is what makes the verb PATCH rather than PUT."
        : "The row to create. A field declared `required` by the descriptor must be present; the row's `id` "
        + "and the framework's own columns are assigned by Alvo and are refused if supplied.";

    /// <summary>The page envelope: the rows, and the cursor for the page after this one.</summary>
    /// <remarks>
    /// Both members are always present — <c>next</c> is written as <see langword="null"/> on the last page
    /// rather than omitted (<c>DataApiJson</c> never ignores a null), so requiring them is a statement about
    /// the bytes and not an aspiration.
    /// </remarks>
    /// <param name="document">The document the row component is referenced from.</param>
    private OpenApiSchema Page(OpenApiDocument document) => new()
    {
        Type = JsonSchemaType.Object,
        Title = PageId(entity.Name),
        Description = "One page of rows, plus the cursor that reads the page after it.",
        Properties = new Dictionary<string, IOpenApiSchema>(StringComparer.Ordinal)
        {
            ["items"] = new OpenApiSchema
            {
                Type = JsonSchemaType.Array,
                Description = "The rows in this page, in the requested order.",
                Items = new OpenApiSchemaReference(PageItemId(entity.Name), document),
            },
            ["next"] = new OpenApiSchema
            {
                Type = JsonSchemaType.String | JsonSchemaType.Null,
                MaxLength = QueryStringParser.MaxCursorLength,
                Description =
                    "The opaque cursor for the next page, or null when this page is the last. Send it back "
                    + "verbatim as `after`; it is the provider's to interpret and must not be decoded.",
            },
        },
        Required = new HashSet<string>(StringComparer.Ordinal) { "items", "next" },
    };

    /// <summary>Every field of the entity that belongs in one of the four schemas, in the schema's own order.</summary>
    /// <param name="readable">Whether this is a read schema, which keeps the framework's columns.</param>
    /// <param name="isUpdate">Whether a write schema is the patch one rather than the create one.</param>
    private Dictionary<string, IOpenApiSchema> Fields(bool readable, bool isUpdate)
    {
        var properties = new Dictionary<string, IOpenApiSchema>(StringComparer.Ordinal);
        foreach (var field in entity.Fields.Where(field => Belongs(field, readable, isUpdate)))
        {
            properties[field.Name] = Field(field, forRequest: !readable);
        }

        return properties;
    }

    /// <summary>
    /// Whether one field belongs in the schema being built.
    /// </summary>
    /// <remarks>
    /// <b>A response never carries a hidden field, whatever its other flags.</b> A write schema is the one
    /// exception, and only for a field the descriptor also marks <c>required</c>: excluding it there too
    /// would document a create nobody could perform, since a caller cannot supply a mandatory field it was
    /// never told exists. An <em>optional</em> hidden field stays excluded from a write schema exactly as
    /// from a response — its name is never the price of documenting a create.
    /// </remarks>
    private bool Belongs(FieldSchema field, bool readable, bool isUpdate)
    {
        if (readable)
        {
            return !hidden.Contains(field.Name);
        }

        if (hidden.Contains(field.Name) && !field.Required)
        {
            return false;
        }

        return !readOnly.Contains(field.Name)
            && (!_managed.Contains(field.Name) || AlvoManagedColumns.IsCallerWritable(field.Name, isUpdate));
    }

    private readonly IReadOnlySet<string> _managed = AlvoManagedColumns.For(entity);

    /// <summary>
    /// One field's schema: the wire shape its declared type implies, plus every facet the API actually
    /// enforces.
    /// </summary>
    /// <param name="field">The declared field.</param>
    /// <param name="forRequest">Whether this schema describes a body being sent rather than one returned.</param>
    private OpenApiSchema Field(FieldSchema field, bool forRequest)
    {
        var (type, format) = WireShapeOf(field, forRequest);
        return new OpenApiSchema
        {
            Type = Nullable(type, field),
            Format = format,
            Description = FieldDescription(field),
            MaxLength = field.MaxLength,
            Enum = EnumOf(field),
            Pattern = FormatCatalog.PatternOf(field) is { } pattern
                ? FormatCatalog.AsJsonSchemaPattern(pattern)
                : null,
            Example = forRequest ? null : ExampleOf(field),
            ReadOnly = !forRequest && IsReadOnly(field),
        };
    }

    /// <summary>Whether the read schema should annotate this field as one a request may not carry.</summary>
    /// <remarks>
    /// JSON Schema's own <c>readOnly</c> annotation, which OpenAPI defines as "MAY be sent in a response,
    /// SHOULD NOT be sent in a request" — exactly what a framework-managed column and a <c>readOnly</c> field
    /// are here, and the reason both are absent from the two request schemas.
    /// </remarks>
    private bool IsReadOnly(FieldSchema field) =>
        readOnly.Contains(field.Name) || _managed.Contains(field.Name);

    /// <summary>
    /// The names a create must carry: the fields the descriptor declared <c>required</c> that a caller may
    /// actually supply.
    /// </summary>
    /// <remarks>
    /// Filtered through <see cref="Belongs"/> rather than read straight off the schema, because a required
    /// field the caller may not write — <c>id</c>, or a required field marked <c>readOnly</c> — is the
    /// framework's to fill, and demanding it would document a create nobody can perform.
    /// </remarks>
    private HashSet<string>? Mandatory()
    {
        var required = entity.Fields
            .Where(field => field.Required && Belongs(field, readable: false, isUpdate: false))
            .Select(field => field.Name)
            .ToHashSet(StringComparer.Ordinal);

        return required.Count == 0 ? null : required;
    }

    /// <summary>
    /// The JSON type and format a value of this field travels as.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Derived from <see cref="FieldClrType"/>, not from a second switch over <see cref="FieldType"/>.</b>
    /// That type is already the one authority on what a field's value is carried as through <c>IAlvoData</c>,
    /// and its own remarks record what two copies of the mapping cost when they disagreed. What is left here is
    /// genuinely this layer's question — how <c>System.Text.Json</c> writes that CLR type — so a field type
    /// added to the port arrives here as a compile-time or startup failure rather than as a silently wrong
    /// <c>string</c>.
    /// </para>
    /// <para>
    /// <b>A <c>json</c> field is the one asymmetry, and it is real.</b> A request accepts any JSON value there
    /// (the reader stores the value's text), while a read returns that text as a JSON <em>string</em> — so the
    /// request side declares no type at all and the read side declares <c>string</c>. Declaring
    /// <c>string</c> in both would document objects as refused when they are accepted; declaring no type in
    /// both would document a response shape that never occurs.
    /// </para>
    /// </remarks>
    /// <param name="field">The declared field.</param>
    /// <param name="forRequest">Whether this schema describes a body being sent.</param>
    private static (JsonSchemaType? Type, string? Format) WireShapeOf(FieldSchema field, bool forRequest)
    {
        if (field.Type == FieldType.Json)
        {
            return forRequest ? (null, null) : (JsonSchemaType.String, null);
        }

        var clr = FieldClrType.Of(field);
        return clr switch
        {
            _ when clr == typeof(Guid) => (JsonSchemaType.String, "uuid"),
            _ when clr == typeof(long) => (JsonSchemaType.Integer, "int64"),
            _ when clr == typeof(decimal) => (JsonSchemaType.Number, "decimal"),
            _ when clr == typeof(bool) => (JsonSchemaType.Boolean, null),
            _ when clr == typeof(DateOnly) => (JsonSchemaType.String, "date"),
            _ when clr == typeof(DateTimeOffset) => (JsonSchemaType.String, "date-time"),
            _ when clr == typeof(string) => (JsonSchemaType.String, BuiltInFormatOf(field)),
            _ => throw new NotSupportedException(
                $"Field '{field.Name}' is carried as {clr.Name}, which the OpenAPI document has no wire shape "
                + "for. Add one beside FieldClrType's mapping, in Api.Internal.SchemaComponentBuilder."),
        };
    }

    /// <summary>
    /// The field's declared <c>format</c>, published only when it is one of the framework's own built-ins.
    /// </summary>
    /// <remarks>
    /// A descriptor-declared format's name (<c>sku-code</c>) means nothing to a client, so publishing it as
    /// <c>format</c> would put a private token in a slot readers treat as a known vocabulary. Its
    /// <em>pattern</em> is published instead, which every JSON Schema validator can act on.
    /// </remarks>
    private static string? BuiltInFormatOf(FieldSchema field) =>
        field.Format is { } format && FormatCatalog.BuiltIns.ContainsKey(format) ? format : null;

    /// <summary>
    /// The declared type widened with <c>null</c> when the column admits one — OpenAPI 3.1's own way of
    /// saying so, since draft 2020-12 has no <c>nullable</c> keyword.
    /// </summary>
    /// <remarks>
    /// A <c>json</c> request field has no type to widen: "any JSON value" already includes null, and
    /// <c>["null"]</c> alone would say the opposite of what was meant.
    /// </remarks>
    private static JsonSchemaType? Nullable(JsonSchemaType? type, FieldSchema field) =>
        type is { } declared && field.Nullable ? declared | JsonSchemaType.Null : type;

    private static List<JsonNode>? EnumOf(FieldSchema field) =>
        field.EnumValues is { Count: > 0 } values
            ? [.. values.Select(value => (JsonNode)JsonValue.Create(value))]
            : null;

    /// <summary>
    /// The field's own description, plus the enforced facets JSON Schema draft 2020-12 cannot express.
    /// </summary>
    /// <remarks>
    /// Only two are appended, and both are refusals a caller would otherwise meet as an unexplained 422.
    /// A decimal's <c>precision</c>/<c>scale</c> has no faithful keyword — <c>multipleOf: 0.01</c> is the
    /// usual encoding and is a binary-floating-point trap in half the validators that read it — and a
    /// <c>json</c> field's read-side representation is the asymmetry <see cref="WireShapeOf"/> records. Every
    /// other declared facet reaches a real keyword, so nothing else is narrated.
    /// </remarks>
    private static string? FieldDescription(FieldSchema field)
    {
        var notes = new List<string>();
        if (field.Description is { Length: > 0 } declared)
        {
            notes.Add(declared);
        }

        if (field is { Precision: { } precision, Scale: { } scale })
        {
            notes.Add(FormattableString.Invariant(
                $"At most {precision} digits in total, {scale} of them after the decimal point."));
        }

        if (field.Type == FieldType.Json)
        {
            notes.Add(
                "Accepts any JSON value on a write, and reads back as a string carrying that value's JSON "
                + "text.");
        }

        return notes.Count == 0 ? null : string.Join(" ", notes);
    }

    /// <summary>
    /// An example, but <b>only where the wire encoding is not implied by the type</b> — a date, a timestamp, a
    /// GUID, an enum member, or a value shaped by one of the framework's formats.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The omissions are the point. An example integer or boolean tells a reader nothing they did not already
    /// know from <c>type</c>, and an example for a field constrained by a descriptor-declared
    /// <em>pattern</em> cannot be synthesized at all — a value that did not satisfy the pattern would be
    /// refused with a 422 the moment somebody copied it, which is worse than no example.
    /// </para>
    /// <para>
    /// A string example longer than the field's own <c>maxLength</c> is dropped for the same reason: the
    /// document must never carry a value the API would refuse.
    /// </para>
    /// </remarks>
    /// <param name="field">The declared field.</param>
    private static JsonValue? ExampleOf(FieldSchema field)
    {
        if (Sample(field) is not { } example)
        {
            return null;
        }

        return field.MaxLength is { } maxLength && example.Length > maxLength ? null : JsonValue.Create(example);
    }

    private static string? Sample(FieldSchema field) => field switch
    {
        { Type: FieldType.Enum, EnumValues: [var first, ..] } => first,
        { Type: FieldType.Uuid or FieldType.Ref } => SampleId,
        { Type: FieldType.Date } => "2026-01-31",
        { Type: FieldType.DateTime } => "2026-01-31T09:30:00+00:00",
        { Format: "email" } => "someone@example.com",
        { Format: "uri" } => "https://example.com/a",
        { Format: "phone" } => "+421 900 123 456",
        _ => null,
    };

    /// <summary>
    /// The one GUID every example id uses. A fixed literal rather than a fresh <see cref="Guid"/>: an example
    /// that changed per build would move the document's snapshot on every run and make drift unreviewable.
    /// </summary>
    private const string SampleId = "3f8d6c1e-9b47-4a5f-8c21-0d7e5a2b6f04";
}
