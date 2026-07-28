using Microsoft.AspNetCore.Http;
using MMLib.Alvo.Schema;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace MMLib.Alvo.Api.Internal;

/// <summary>
/// Reads a write request's body and binds it to the CLR values <c>IAlvoData</c>'s write methods take.
/// </summary>
/// <remarks>
/// <para>
/// This is <b>binding, not validation</b>. The port publishes a typed contract —
/// <see cref="FieldClrType"/> is that contract, in the ports, and this <em>reads</em> it rather than
/// restating it — and JSON carries none of those types, so something has to convert before the port can
/// be called at all. Task 5's <c>RecordValidator</c> validates <em>over</em> these values (required, max
/// length, scale, enum, format, FK existence) and reports every violation as RFC 7807; it does not
/// replace this.
/// </para>
/// <para>
/// <b>Everything here runs after authorization and before validation.</b> <c>DataApiEndpoints</c> resolves
/// the operation's policy decision and the API-key scope gate <em>before</em> calling this, so a caller who
/// is going to be refused never pays for a parse — doing megabytes of work on behalf of a request that
/// cannot succeed is a denial-of-service amplifier, and it is also the correct precedence: an unauthorized
/// caller must not be told their body was malformed. It is still bounded three ways
/// (<see cref="AlvoApiOptions.MaxRequestBodyBytes"/>, <see cref="AlvoApiOptions.MaxPayloadDepth"/>,
/// <see cref="AlvoApiOptions.MaxPayloadKeys"/>), because an <em>authorized</em> caller can be hostile too,
/// and every bound refuses <em>before</em> the work it exists to prevent: the size bound stops at the first
/// chunk that would cross it rather than buffering the body first, and the depth and key bounds are decided
/// by a forward-only <see cref="Utf8JsonReader"/> scan that builds no node tree. A bound applied to a
/// finished document has already paid the cost.
/// </para>
/// <para>
/// <b>An undeclared key is refused before it is materialised</b> — not to withhold anything, but so no
/// attacker-controlled value is re-serialised into a string on its way to a refusal that was already
/// certain. See <see cref="UndeclaredFieldFailure"/> for what the wording does and does not protect.
/// </para>
/// </remarks>
internal static class JsonPayloadReader
{
    /// <summary>The refusal for a key the entity does not declare.</summary>
    /// <remarks>
    /// <para>
    /// <b>The declared, non-hidden schema shape is public, and this wording is not trying to hide it.</b>
    /// Alvo maps route literals from the applied schema, so an undeclared entity already answers 404 where a
    /// declared one answers 403 — entity existence is disclosed before authorization, by design, and that
    /// design is what lets the OpenAPI document list real paths. Task 8 then publishes the declared,
    /// non-hidden field list to anyone who can read the document. A framework cannot both publish its schema
    /// shape and treat that shape as confidential. What is confidential is <em>data</em>.
    /// </para>
    /// <para>
    /// <b>The one carve-out: a <c>hidden</c> field's name.</b> That is a field the descriptor author marked
    /// confidential and Task 8 excludes from the document, so its name must stay indistinguishable from an
    /// unknown one — which is why the query parser takes the resolved mask
    /// (<c>DataApiEndpoints.MaskedFields</c>) and refuses both with one identical violation. On this write
    /// path there is nothing to distinguish: <c>hidden</c> restricts reading, so a hidden field is
    /// legitimately writable and is simply accepted.
    /// </para>
    /// <para>
    /// So the message names neither the key nor the entity for a plainer reason than secrecy: it is
    /// caller-supplied text, echoing it back is a log-injection vector, and the port's own refusal
    /// (<c>QueryFieldGuard</c>) already says exactly this much.
    /// </para>
    /// </remarks>
    private const string UndeclaredFieldFailure =
        "The request body names a field that is not writable on this entity. Send only the fields the "
        + "entity declares.";

    private const string NotAnObjectFailure =
        "The request body must be a JSON object mapping field names to values.";

    private const string MalformedJsonFailure = "The request body is not well-formed JSON.";

    /// <summary>One buffer's worth of body; the size bound trips on chunk boundaries, so this only sets the granularity.</summary>
    private const int ReadChunkBytes = 8 * 1024;

    /// <summary>Reads and binds the request body, or reports why it could not be.</summary>
    /// <param name="request">The request whose body to read.</param>
    /// <param name="entity">The entity being written, as the applied schema declares it.</param>
    /// <param name="options">The payload bounds to enforce.</param>
    /// <param name="cancellationToken">A token to cancel the read.</param>
    /// <returns>The bound field values, or the failure to render as a 422.</returns>
    internal static async Task<(Dictionary<string, object?>? Values, string? Failure)> ReadAsync(
        HttpRequest request, EntitySchema entity, AlvoApiOptions options, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(entity);
        ArgumentNullException.ThrowIfNull(options);

        using var body = new MemoryStream();
        var readFailure = await ReadBoundedAsync(request, body, options.MaxRequestBodyBytes, cancellationToken)
            .ConfigureAwait(false);
        if (readFailure is not null)
        {
            return (null, readFailure);
        }

        var shapeFailure = EnsureWithinShapeBounds(body.GetBuffer().AsSpan(0, (int)body.Length), options);
        return shapeFailure is not null ? (null, shapeFailure) : Bind(body, entity, options);
    }

    /// <summary>
    /// Copies the body into <paramref name="destination"/>, refusing at the first chunk that would cross
    /// <paramref name="maxBytes"/>. A declared <c>Content-Length</c> past the bound is refused without
    /// reading a byte; a chunked body that declares no length is bounded all the same, because the check
    /// is on what has actually arrived.
    /// </summary>
    private static async Task<string?> ReadBoundedAsync(
        HttpRequest request, MemoryStream destination, int maxBytes, CancellationToken cancellationToken)
    {
        if (request.ContentLength > maxBytes)
        {
            return TooLargeFailure(maxBytes);
        }

        var chunk = new byte[ReadChunkBytes];
        int read;
        while ((read = await request.Body.ReadAsync(chunk, cancellationToken).ConfigureAwait(false)) > 0)
        {
            if (destination.Length + read > maxBytes)
            {
                return TooLargeFailure(maxBytes);
            }

            destination.Write(chunk, 0, read);
        }

        return null;
    }

    /// <summary>
    /// Decides the shape bounds — is it an object at all, how deep does it nest, how many property names does
    /// it carry <em>anywhere</em> — from a forward-only scan that builds nothing.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The reader's own <see cref="JsonReaderOptions.MaxDepth"/> is deliberately given headroom over
    /// <see cref="AlvoApiOptions.MaxPayloadDepth"/>. The reader raises the same <see cref="JsonException"/>
    /// for a too-deep body as for a malformed one, so anything the reader refuses could only ever be reported
    /// as "not well-formed JSON" — the one bound whose message could not name itself, which sends an agent
    /// hunting a syntax error that is not there. Checking <see cref="Utf8JsonReader.CurrentDepth"/> first
    /// means the depth refusal names the depth.
    /// </para>
    /// <para>
    /// The headroom is <b>two</b> levels, not one, because the two numbers are counted differently:
    /// <see cref="JsonReaderOptions.MaxDepth"/> counts the outermost container as level 1 while
    /// <see cref="Utf8JsonReader.CurrentDepth"/> reports it as 0. With only one level of slack the reader
    /// threw on the very token whose <see cref="Utf8JsonReader.CurrentDepth"/> the check needed to see, and
    /// the named message was unreachable — measured, not reasoned. The reader remains a hard backstop; it is
    /// simply never the first to speak.
    /// </para>
    /// </remarks>
    private static string? EnsureWithinShapeBounds(ReadOnlySpan<byte> utf8Body, AlvoApiOptions options)
    {
        var reader = new Utf8JsonReader(
            utf8Body,
            new JsonReaderOptions { MaxDepth = options.MaxPayloadDepth + 2, AllowTrailingCommas = false });

        try
        {
            if (!reader.Read() || reader.TokenType != JsonTokenType.StartObject)
            {
                return NotAnObjectFailure;
            }

            return ScanShape(ref reader, options);
        }
        catch (JsonException)
        {
            return MalformedJsonFailure;
        }
    }

    /// <summary>
    /// Walks every token of the body, refusing as soon as the property-name count or the nesting depth
    /// crosses its bound.
    /// </summary>
    /// <remarks>
    /// <b>Property names are counted at every depth, not just the top level.</b> Counting only depth 1 was a
    /// bound that did not bound: <c>{"name":{…150 000 keys…}}</c> satisfied it, satisfied the depth cap at
    /// depth 2, fitted inside <see cref="AlvoApiOptions.MaxRequestBodyBytes"/>, and was then materialised in
    /// full — a ~20–40× memory amplification per request, refused only afterwards. The scan already visits
    /// every token (it no longer <c>Skip</c>s a property's value), so this is a counter placement rather
    /// than a second pass.
    /// </remarks>
    private static string? ScanShape(ref Utf8JsonReader reader, AlvoApiOptions options)
    {
        var names = 0;
        while (reader.Read())
        {
            if (reader.CurrentDepth > options.MaxPayloadDepth)
            {
                return string.Create(
                    CultureInfo.InvariantCulture,
                    $"The request body nests deeper than {options.MaxPayloadDepth} levels, the configured maximum.");
            }

            if (reader.TokenType == JsonTokenType.PropertyName && ++names > options.MaxPayloadKeys)
            {
                return string.Create(
                    CultureInfo.InvariantCulture,
                    $"The request body carries more than {options.MaxPayloadKeys} fields, the configured maximum.");
            }
        }

        return null;
    }

    /// <summary>Parses the already-bounded body into a node tree and binds every key to its declared type.</summary>
    private static (Dictionary<string, object?>? Values, string? Failure) Bind(
        MemoryStream body, EntitySchema entity, AlvoApiOptions options)
    {
        body.Position = 0;
        var node = JsonNode.Parse(
            body,
            nodeOptions: null,
            documentOptions: new JsonDocumentOptions { MaxDepth = options.MaxPayloadDepth });
        if (node is not JsonObject payload)
        {
            return (null, NotAnObjectFailure);
        }

        var declared = DeclaredFields(entity);
        var values = new Dictionary<string, object?>(StringComparer.Ordinal);
        foreach (var (key, value) in payload)
        {
            if (!declared.TryGetValue(key, out var field))
            {
                // Task 5: this becomes an AlvoViolation carrying a JSON Pointer and a fix suggestion.
                return (null, UndeclaredFieldFailure);
            }

            if (!TryBind(key, value, field, out var bound, out var failure))
            {
                return (null, failure);
            }

            values[key] = bound;
        }

        return (values, null);
    }

    /// <summary>
    /// The entity's fields by name, ordinally — the comparer the schema, the CEL type checker and the
    /// rendered SQL all use, so <c>Owner_Id</c> is a different (and undeclared) name.
    /// </summary>
    private static Dictionary<string, FieldSchema> DeclaredFields(EntitySchema entity)
    {
        var declared = new Dictionary<string, FieldSchema>(entity.Fields.Count, StringComparer.Ordinal);
        foreach (var field in entity.Fields)
        {
            declared[field.Name] = field;
        }

        return declared;
    }

    /// <summary>Converts one JSON value to the CLR type <see cref="FieldClrType"/> maps the field to.</summary>
    /// <remarks>
    /// Only <see cref="JsonException"/> is caught, and the narrowness is the point: a
    /// <see cref="NotSupportedException"/> from <see cref="FieldClrType.Of(FieldType)"/> means the applied
    /// schema carries a field type this build cannot serve, which is a broken invariant of whoever
    /// composed it — family 3 in <c>IAlvoData</c>'s table, rendered 500. An earlier version caught it
    /// too and rendered 422, telling the caller to fix a request that was fine.
    /// </remarks>
    private static bool TryBind(
        string key, JsonNode? node, FieldSchema field, out object? value, out string? failure)
    {
        if (node is null)
        {
            value = null;
            failure = null;
            return true;
        }

        try
        {
            value = Convert(node, field);
            failure = null;
            return true;
        }
        catch (JsonException)
        {
            // Task 5: one AlvoViolation per offending field, rather than the first failure stopping the read.
            value = null;
            failure = string.Create(
                CultureInfo.InvariantCulture,
                $"The value supplied for '{key}' is not a valid {field.Type.ToString().ToLowerInvariant()}.");
            return false;
        }
    }

    /// <summary>
    /// Deserializes through <see cref="FieldClrType"/> and Alvo's own serializer options, so no second
    /// CLR-type table exists to drift from the port's contract.
    /// </summary>
    /// <remarks>
    /// One field type is not a plain deserialize: a <c>json</c> field's CLR type is
    /// <see cref="string"/>, but its <em>value</em> is the JSON text rather than a JSON string, so it is
    /// taken verbatim. That is a serialization detail of one type, not a second opinion about which CLR
    /// type it maps to.
    /// </remarks>
    private static object? Convert(JsonNode node, FieldSchema field) => field.Type == FieldType.Json
        ? node.ToJsonString(DataApiJson.Options)
        : node.Deserialize(FieldClrType.Of(field), DataApiJson.Options);

    private static string TooLargeFailure(int maxBytes) => string.Create(
        CultureInfo.InvariantCulture,
        $"The request body is larger than {maxBytes} bytes, the configured maximum.");
}
