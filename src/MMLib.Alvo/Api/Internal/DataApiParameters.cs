using Microsoft.OpenApi;
using MMLib.Alvo.Rules;
using MMLib.Alvo.Schema;
using System.Globalization;
using System.Text.Json.Nodes;

namespace MMLib.Alvo.Api.Internal;

/// <summary>
/// Every parameter a generated endpoint reads: the row id in the path, the request headers it honours, and —
/// on a list — the whole PostgREST-shaped query surface.
/// </summary>
/// <remarks>
/// <para>
/// <b>None of this is discoverable by ApiExplorer, which is why it is here.</b> A list delegate takes an
/// <c>HttpContext</c> and parses the query string itself, so the framework sees no parameters at all; the
/// precondition and idempotency headers are read from <c>HttpRequest.Headers</c> for the same reason. A
/// document without them describes an endpoint that accepts nothing but a path — from which no useful client
/// can be generated, which is exactly the §6 promise the document exists to keep.
/// </para>
/// <para>
/// <b>One query parameter per filterable field, spelled out rather than described in prose.</b> In this
/// grammar a filter's parameter name <em>is</em> a field name, so the parameters really are a known, finite
/// set — and an explicit list is what lets a client offer them. A <c>hidden</c> field contributes none, for
/// the confidentiality reason <see cref="SchemaComponentBuilder"/> states: its name must not appear.
/// </para>
/// <para>
/// <b>The <c>not.</c> prefix is not emitted as a second parameter per field.</b> Doing so would double the
/// list to say one thing — that any parameter name may be prefixed — which the grammar paragraph on the
/// operation says once. Every bound published here (<c>limit</c>'s maximum, the cursor's length, the
/// idempotency key's length) is read from the option or parser constant that enforces it, never restated.
/// </para>
/// </remarks>
internal static class DataApiParameters
{
    /// <summary>The parameters <paramref name="kind"/> reads on <paramref name="entity"/>.</summary>
    /// <param name="kind">The endpoint kind.</param>
    /// <param name="entity">The entity it serves.</param>
    /// <param name="hidden">Every field carrying a <c>hidden</c> flag, which contributes no filter parameter.</param>
    /// <param name="document">The document the shared parameter components are referenced from.</param>
    internal static List<IOpenApiParameter> For(
        DataApiEndpointKind kind, EntitySchema entity, IReadOnlySet<string> hidden, OpenApiDocument document)
    {
        ArgumentNullException.ThrowIfNull(entity);
        ArgumentNullException.ThrowIfNull(hidden);
        ArgumentNullException.ThrowIfNull(document);

        return
        [
            .. Shared(Names(kind, entity), document),
            .. kind == DataApiEndpointKind.List
                ? entity.Fields.Where(field => !hidden.Contains(field.Name)).Select(Filter)
                : [],
        ];
    }

    /// <summary>
    /// Every shared parameter id at least one of <paramref name="operations"/> references — the set
    /// <see cref="AlvoDocumentTransformer.Reusable"/> publishes, so a parameter no mapped operation reads
    /// (the tenant header on a descriptor with no tenant-scoped entity, <c>ifNoneMatch</c> on one with no
    /// audited entity) is never an orphan component.
    /// </summary>
    /// <param name="operations">Every generated endpoint's kind and the entity it serves.</param>
    internal static IReadOnlySet<string> UsedSharedIds(
        IEnumerable<(DataApiEndpointKind Kind, EntitySchema Entity)> operations)
    {
        ArgumentNullException.ThrowIfNull(operations);

        var used = new HashSet<string>(StringComparer.Ordinal);
        foreach (var (kind, entity) in operations)
        {
            used.UnionWith(Names(kind, entity));
        }

        return used;
    }

    /// <summary>Which of the shared parameters this operation reads, in the order they are published.</summary>
    /// <remarks>
    /// The seven query parameters belong to <see cref="DataApiEndpointKind.List"/> alone. On
    /// <see cref="DataApiEndpointKind.Query"/> they are the request body's members instead, which is the
    /// whole of that endpoint — publishing them as query parameters there would advertise a second way to
    /// send them that the delegate does not read.
    /// </remarks>
    private static IEnumerable<string> Names(DataApiEndpointKind kind, EntitySchema entity) =>
    [
        .. AddressesOneRow(kind) ? new[] { RowIdId } : [],
        .. entity.Tenancy == TenancyMode.Scoped ? new[] { TenantId } : [],
        .. HeaderNames(kind, entity),
        .. kind == DataApiEndpointKind.List
            ? new[] { SelectId, OrderId, LimitId, OffsetId, AfterId, OrId, AndId }
            : [],
    ];

    private static IEnumerable<IOpenApiParameter> Shared(IEnumerable<string> ids, OpenApiDocument document) =>
        ids.Select(id => new OpenApiParameterReference(id, document));

    /// <summary>
    /// Every parameter whose meaning does not depend on the entity, keyed by the component id it is published
    /// under.
    /// </summary>
    /// <remarks>
    /// <b>The one place each is written.</b> Two of them carry a host-configured bound
    /// (<c>limit</c>'s maximum, the idempotency key's length) and one a host-configured header name, so they are
    /// per-host rather than per-entity — which is exactly the granularity a document-level component has.
    /// <b>Every candidate, whether or not any mapped operation actually references it.</b>
    /// <see cref="AlvoDocumentTransformer.Reusable"/> is the one place that decides which of these to
    /// register as a document component, using <see cref="UsedSharedIds"/> — so a descriptor with no
    /// tenant-scoped entity, or none that is audited, never ships an orphan <c>tenant</c> or
    /// <c>ifNoneMatch</c> component that no response could ever reference.
    /// </remarks>
    /// <param name="options">The API options the paging and key bounds are published from.</param>
    /// <param name="tenantHeader">The header a tenant is requested in.</param>
    internal static IReadOnlyList<(string Id, OpenApiParameter Parameter)> Shared(
        AlvoApiOptions options, string tenantHeader)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentException.ThrowIfNullOrWhiteSpace(tenantHeader);

        return
        [
            (RowIdId, RowId),
            (TenantId, Tenant(tenantHeader)),
            (IfMatchId, IfMatch),
            (IfNoneMatchId, IfNoneMatch),
            (IdempotencyKeyId, IdempotencyKey(options)),
            (PreferId, Prefer),
            (SelectId, Select),
            (OrderId, Order),
            (LimitId, Limit(options)),
            (OffsetId, Offset),
            (AfterId, After),
            (OrId, Group(ReservedQueryKeys.Or, "disjunction (OR)")),
            (AndId, Group(ReservedQueryKeys.And, "conjunction (AND)")),
        ];
    }

    private const string RowIdId = "rowId";

    private const string TenantId = "tenant";

    private const string IfMatchId = "ifMatch";

    private const string IfNoneMatchId = "ifNoneMatch";

    private const string IdempotencyKeyId = "idempotencyKey";

    private const string PreferId = "prefer";

    private const string SelectId = "select";

    private const string OrderId = "order";

    private const string LimitId = "limit";

    private const string OffsetId = "offset";

    private const string AfterId = "after";

    private const string OrId = "orGroup";

    private const string AndId = "andGroup";

    private static bool AddressesOneRow(DataApiEndpointKind kind) =>
        kind is DataApiEndpointKind.Get or DataApiEndpointKind.Update or DataApiEndpointKind.Delete;

    /// <summary>
    /// The row key in the path. Re-declared rather than left to ApiExplorer's inference from the delegate's
    /// <c>Guid id</c> parameter, so its description says what a 404 for it means.
    /// </summary>
    private static OpenApiParameter RowId => new()
    {
        Name = "id",
        In = ParameterLocation.Path,
        Required = true,
        Description =
            "The row's key. A value routing cannot read as a GUID is a 404 from routing itself, before the "
            + "endpoint runs; a well-formed key naming a row the caller may not see is also a 404, "
            + "indistinguishable from one that does not exist.",
        Schema = new OpenApiSchema { Type = JsonSchemaType.String, Format = "uuid" },
    };

    /// <summary>
    /// The tenant header, published only for a tenant-scoped entity.
    /// </summary>
    /// <remarks>
    /// Conditional because on a global entity the header decides nothing, and a document that listed it
    /// everywhere would invite a client to send a value that changes no answer. The name comes from
    /// <c>AlvoAuthOptions.TenantHeaderName</c> — the option that defines it — rather than from a literal here,
    /// because an embedded host may have moved the header out of the way of its own.
    /// </remarks>
    private static OpenApiParameter Tenant(string tenantHeader) => new()
    {
        Name = tenantHeader,
        In = ParameterLocation.Header,
        Required = false,
        Description =
            "The tenant to act in. An operation on a tenant-scoped entity refuses a caller with no tenant "
            + "(403) before any rule is consulted, and a key issued for one tenant may not request another. "
            + "Only the operations of a tenant-scoped entity reference this parameter.",
        Schema = new OpenApiSchema { Type = JsonSchemaType.String, Format = "uuid" },
    };

    /// <summary>The request headers this operation honours — and only the ones it does.</summary>
    /// <remarks>
    /// <para>
    /// A header the operation <em>ignores</em> is deliberately not listed as a parameter, because a parameter is
    /// an invitation to send it. The two gaps that matter — <c>If-Match</c> on a read, and
    /// <c>Idempotency-Key</c> on an update or a delete — are stated in the operation's own description instead,
    /// where the text can say that sending them has no effect.
    /// </para>
    /// <para>
    /// <b>A header the operation <em>refuses</em> is not listed either, which is why the write arm carries the
    /// same version guard as the read arm.</b> <see cref="AlvoManagedColumns.VersionColumn"/> answering
    /// <see langword="null"/> means <see cref="RowVersionETag.For"/> mints no <c>ETag</c> for any row of this
    /// entity, and <c>AlvoPrecondition.EnsureSupported</c> refuses <em>any</em> precondition on it — so
    /// publishing <c>ifMatch</c> here would invite a client to send a header whose value it has no way to
    /// obtain, and every value it invented would be 412 forever. That is worse than staying silent: §0
    /// principle 4 makes this document the contract an agent reads, and it was reading an instruction into a
    /// permanent refusal. Without the guard the arm structurally could not be conditional, which is how the
    /// asymmetry survived — the read arm was entity-conditional from the start.
    /// </para>
    /// </remarks>
    private static IEnumerable<string> HeaderNames(DataApiEndpointKind kind, EntitySchema entity) =>
        kind switch
        {
            DataApiEndpointKind.List or DataApiEndpointKind.Query => [PreferId],
            DataApiEndpointKind.Get when AlvoManagedColumns.VersionColumn(entity) is not null => [IfNoneMatchId],
            DataApiEndpointKind.Create => [IdempotencyKeyId],
            DataApiEndpointKind.Update or DataApiEndpointKind.Delete
                when AlvoManagedColumns.VersionColumn(entity) is not null => [IfMatchId],
            _ => [],
        };

    private static OpenApiParameter IfNoneMatch => new()
    {
        Name = "If-None-Match",
        In = ParameterLocation.Header,
        Required = false,
        Description =
            "Read the row only if it is no longer at one of these versions; otherwise 304 with no body. "
            + "Compared with RFC 9110 §13.1.2's *weak* comparison, so a `W/` prefix is ignored — deliberately "
            + "not the strong comparison `If-Match` gets on a write.",
        Schema = new OpenApiSchema { Type = JsonSchemaType.String },
    };

    private static OpenApiParameter IfMatch => new()
    {
        Name = "If-Match",
        In = ParameterLocation.Header,
        Required = false,
        Description =
            "Perform the write only if the row is still at this version. One entity tag exactly as a previous "
            + "response returned it, or `*` to require only that the row still exist. Anything else — several "
            + "tags, a weak `W/` tag, a value this API never minted — is 412 rather than ignored, because "
            + "ignoring a precondition is the lost update the header exists to prevent.",
        Schema = new OpenApiSchema { Type = JsonSchemaType.String },
    };

    private static OpenApiParameter IdempotencyKey(AlvoApiOptions options) => new()
    {
        Name = DataApiEndpoints.IdempotencyKeyHeader,
        In = ParameterLocation.Header,
        Required = false,
        Description =
            "Makes this create retry-safe. The result is recorded against the key and the caller's own scope: "
            + "the same key with the same body replays the first result and writes no second row, and the same "
            + "key with a different body is 409. An anonymous caller's key is refused, because every anonymous "
            + "caller shares one identity and their keys would share one space. The bound below is a **byte** "
            + "bound — at most "
            + $"{options.MaxIdempotencyKeyBytes.ToString(CultureInfo.InvariantCulture)} bytes once UTF-8 "
            + "encoded — so a key of non-ASCII characters reaches it sooner than `maxLength` suggests; an "
            + "over-long key is refused rather than shortened, because two keys differing only past the cut "
            + "would become one.",

        // maxLength counts characters and the rule counts UTF-8 bytes, so this is the tightest *sound*
        // schema bound available: a key of N characters is at least N bytes, so "within N bytes" implies
        // "within N characters". It therefore never advertises a key the API would refuse — it under-promises
        // for a multi-byte key, which the description states in the one unit the rule is actually in.
        Schema = new OpenApiSchema
        {
            Type = JsonSchemaType.String,
            MaxLength = options.MaxIdempotencyKeyBytes,
            MinLength = 1,
        },
    };

    /// <summary>
    /// The RFC 7240 preference header, for the one preference a list honours.
    /// </summary>
    /// <remarks>
    /// Published even though an unrecognised preference is <em>ignored</em> rather than refused: that is
    /// RFC 7240's own rule, and a document that did not name the one preference this endpoint acts on would
    /// leave an agent with no way to discover a count is available at all. What was applied comes back in
    /// <c>Preference-Applied</c>, which is described with the 200 response.
    /// </remarks>
    private static OpenApiParameter Prefer => new()
    {
        Name = PreferHeader.Name,
        In = ParameterLocation.Header,
        Required = false,
        Description =
            "`count=exact` fills the page envelope's `count` with the number of rows the query matches in "
            + "total. Opt-in, because it is a second scan of the matching set on every request; a request "
            + "sending no recognised `count` preference gets `null` there.\n\n"
            + "`count=planned` and `count=estimated` are accepted and **degrade to an exact count**, so they "
            + "fill `count` too — a planner estimate exists on one supported engine and not the other, and "
            + "this API answers identically on both. The response says which was applied in "
            + "`Preference-Applied`, and it is always `count=exact`. Per RFC 7240 a preference this server "
            + "does not recognise is ignored rather than refused, and its absence from `Preference-Applied` "
            + "is how that is reported.",
        Schema = new OpenApiSchema { Type = JsonSchemaType.String },
        Example = JsonValue.Create("count=exact"),
    };

    private static OpenApiParameter Select => new()
    {
        Name = ReservedQueryKeys.Select,
        In = ParameterLocation.Query,
        Description =
            "Comma-separated field names to return, in the order named, each optionally renamed as "
            + "`alias:field`. It narrows the **read** as well as the response: a field the projection does "
            + "not name is not read from the row. Two groups of columns are read regardless — the "
            + "framework-managed ones, and any field named in `order`, because no engine can sort by a "
            + "column it did not read — but neither appears in the response unless the projection named it. "
            + "A field the caller may not read is refused exactly as an undeclared one is. An alias is lower "
            + "snake_case, is not the name of a framework-managed column, and cannot be claimed twice; and a "
            + "projection cannot name more distinct keys than there are fields this caller can read.",
        Schema = new OpenApiSchema { Type = JsonSchemaType.String },
        Example = JsonValue.Create("label:make,model"),
    };

    private static OpenApiParameter Order => new()
    {
        Name = ReservedQueryKeys.Order,
        In = ParameterLocation.Query,
        Description =
            "`<field>[.asc|.desc][.nullsfirst|.nullslast]`, comma-separated for several keys, outermost "
            + "first. The modifiers must appear in that order and each at most once, so one sort key has one "
            + "spelling; an unrecognised modifier is refused rather than ignored. A **nullable** field is a "
            + "sort key like any other and defaults to `nullslast`; paging honours the same placement — see "
            + "the operation description for what it costs.",
        Schema = new OpenApiSchema { Type = JsonSchemaType.String },
        Example = JsonValue.Create("id.desc"),
    };

    private static OpenApiParameter Limit(AlvoApiOptions options) => new()
    {
        Name = ReservedQueryKeys.Limit,
        In = ParameterLocation.Query,
        Description =
            "How many rows this page carries. A value past the maximum is **refused, not clamped**: a client "
            + "that asked for more and silently received fewer computes its paging from a number no response "
            + "ever told it. Zero is refused too — it is a read that can never return a row.",
        Schema = new OpenApiSchema
        {
            Type = JsonSchemaType.Integer,
            Format = "int32",
            Minimum = "1",
            Maximum = Text(options.MaxPageSize),
            Default = JsonValue.Create(options.DefaultPageSize),
        },
    };

    private static OpenApiParameter Offset => new()
    {
        Name = ReservedQueryKeys.Offset,
        In = ParameterLocation.Query,
        Description =
            "How many rows to skip. Prefer `after`: an offset re-scans the skipped rows and shifts under "
            + "concurrent writes, where a keyset cursor does neither.",
        Schema = new OpenApiSchema { Type = JsonSchemaType.Integer, Format = "int32", Minimum = "0" },
    };

    private static OpenApiParameter After => new()
    {
        Name = ReservedQueryKeys.After,
        In = ParameterLocation.Query,
        Description =
            "The keyset cursor a previous page returned as `next`, sent back verbatim. It is opaque and only "
            + "the provider that issued it may interpret it, so it must not be decoded or constructed. A "
            + "forged one yields an empty page rather than an error.",
        Schema = new OpenApiSchema
        {
            Type = JsonSchemaType.String,
            MinLength = 1,
            MaxLength = QueryStringParser.MaxCursorLength,
        },
    };

    /// <summary>One of the two explicit grouping keywords.</summary>
    /// <param name="keyword">The reserved keyword.</param>
    /// <param name="meaning">What the group means, for the description.</param>
    private static OpenApiParameter Group(string keyword, string meaning) => new()
    {
        Name = keyword,
        In = ParameterLocation.Query,
        Description =
            $"A bracketed, comma-separated list of terms combined as a {meaning}: "
            + $"`{keyword}=(color.eq.red,make.in.(skoda,vw))`. Groups may nest, and either the keyword or any "
            + "member may carry the `not.` prefix. Repeating the parameter conjoins the groups.",
        Schema = new OpenApiSchema { Type = JsonSchemaType.String },
    };

    /// <summary>The filter parameter one declared field contributes.</summary>
    /// <remarks>
    /// The description is the field's own sentence plus the value syntax, and not the whole grammar: the
    /// operator list, the <c>not.</c> prefix and the conjunction rule are identical for every field, so
    /// repeating them here would be that paragraph once per field in one document. They are stated once on the
    /// operation.
    /// </remarks>
    /// <param name="field">The declared field.</param>
    private static OpenApiParameter Filter(FieldSchema field) => new()
    {
        Name = field.Name,
        In = ParameterLocation.Query,
        Description =
            (field.Description is { Length: > 0 } declared ? declared + " " : string.Empty)
            + $"Filter on `{field.Name}`, as `<operator>.<operand>`. See the operation description for the "
            + "operators, the `not.` prefix and how several parameters combine.",
        Schema = new OpenApiSchema { Type = JsonSchemaType.String },
    };

    private static string Text(int value) =>
        value.ToString(System.Globalization.CultureInfo.InvariantCulture);
}
