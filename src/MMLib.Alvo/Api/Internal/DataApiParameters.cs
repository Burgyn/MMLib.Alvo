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
    /// <summary>The parameters <paramref name="operation"/> reads on <paramref name="entity"/>.</summary>
    /// <param name="operation">The operation the endpoint performs.</param>
    /// <param name="entity">The entity it serves.</param>
    /// <param name="hidden">Every field carrying a <c>hidden</c> flag, which contributes no filter parameter.</param>
    /// <param name="document">The document the shared parameter components are referenced from.</param>
    internal static List<IOpenApiParameter> For(
        DataOperation operation, EntitySchema entity, IReadOnlySet<string> hidden, OpenApiDocument document)
    {
        ArgumentNullException.ThrowIfNull(entity);
        ArgumentNullException.ThrowIfNull(hidden);
        ArgumentNullException.ThrowIfNull(document);

        return
        [
            .. Shared(Names(operation, entity), document),
            .. operation == DataOperation.List
                ? entity.Fields.Where(field => !hidden.Contains(field.Name)).Select(Filter)
                : [],
        ];
    }

    /// <summary>Which of the shared parameters this operation reads, in the order they are published.</summary>
    private static IEnumerable<string> Names(DataOperation operation, EntitySchema entity) =>
    [
        .. AddressesOneRow(operation) ? new[] { RowIdId } : [],
        .. entity.Tenancy == TenancyMode.Scoped ? new[] { TenantId } : [],
        .. HeaderNames(operation, entity),
        .. operation == DataOperation.List
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

    private const string SelectId = "select";

    private const string OrderId = "order";

    private const string LimitId = "limit";

    private const string OffsetId = "offset";

    private const string AfterId = "after";

    private const string OrId = "orGroup";

    private const string AndId = "andGroup";

    private static bool AddressesOneRow(DataOperation operation) =>
        operation is DataOperation.Get or DataOperation.Update or DataOperation.Delete;

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
    /// A header the operation <em>ignores</em> is deliberately not listed as a parameter, because a parameter is
    /// an invitation to send it. The two gaps that matter — <c>If-Match</c> on a read, and
    /// <c>Idempotency-Key</c> on an update or a delete — are stated in the operation's own description instead,
    /// where the text can say that sending them has no effect.
    /// </remarks>
    private static IEnumerable<string> HeaderNames(DataOperation operation, EntitySchema entity) =>
        operation switch
        {
            DataOperation.Get when AlvoManagedColumns.VersionColumn(entity) is not null => [IfNoneMatchId],
            DataOperation.Create => [IdempotencyKeyId],
            DataOperation.Update or DataOperation.Delete => [IfMatchId],
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

    private static OpenApiParameter Select => new()
    {
        Name = ReservedQueryKeys.Select,
        In = ParameterLocation.Query,
        Description =
            "Comma-separated field names to return, in the order named. It narrows the *response* only — the "
            + "read still fetches the whole row — so it saves bandwidth to the caller and nothing at the "
            + "database. A field the caller may not read is refused exactly as an undeclared one is.",
        Schema = new OpenApiSchema { Type = JsonSchemaType.String },
    };

    private static OpenApiParameter Order => new()
    {
        Name = ReservedQueryKeys.Order,
        In = ParameterLocation.Query,
        Description =
            "`<field>[.asc|.desc][.nullsfirst|.nullslast]`, comma-separated for several keys, outermost "
            + "first. The modifiers must appear in that order and each at most once, so one sort key has one "
            + "spelling; an unrecognised modifier is refused rather than ignored. **A nullable field is "
            + "refused as a sort key** — see the operation description for why, and for what that means for "
            + "the two null-placement modifiers.",
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
