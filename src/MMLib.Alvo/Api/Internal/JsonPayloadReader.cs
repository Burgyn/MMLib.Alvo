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
/// <b>Everything here runs before policy and without authentication.</b> An anonymous caller's POST is
/// parsed before the port has any say, which makes this the one part of the Data API an unauthenticated
/// request can put to work. So it is bounded three ways
/// (<see cref="AlvoApiOptions.MaxRequestBodyBytes"/>, <see cref="AlvoApiOptions.MaxPayloadDepth"/>,
/// <see cref="AlvoApiOptions.MaxPayloadKeys"/>) and every bound refuses <em>before</em> the work it
/// exists to prevent: the size bound stops at the first chunk that would cross it rather than buffering
/// the body first, and the depth and key bounds are decided by a forward-only
/// <see cref="Utf8JsonReader"/> scan that builds no node tree. A bound applied to a finished document has
/// already paid the cost.
/// </para>
/// <para>
/// <b>An undeclared key is refused before it is materialised.</b> The port refuses it too — its own
/// single check, with a message that does not confirm whether the field exists — but refusing here means
/// no attacker-controlled value is ever re-serialised into a string on its way to a refusal that was
/// already certain, and the wording below says no more than the port's does.
/// </para>
/// </remarks>
internal static class JsonPayloadReader
{
    /// <summary>
    /// The refusal for a key the entity does not declare. Deliberately as uninformative as the port's own
    /// (<c>QueryFieldGuard</c>): it names neither the key nor the entity, so it cannot answer "does this
    /// entity have a field called X?" one request at a time.
    /// </summary>
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
    /// Decides the shape bounds — is it an object at all, how deep does it nest, how many keys does its
    /// top level carry — from a forward-only scan that builds nothing. The reader itself enforces the
    /// depth cap, so a pathological body is refused mid-scan rather than after a tree exists for it.
    /// </summary>
    private static string? EnsureWithinShapeBounds(ReadOnlySpan<byte> utf8Body, AlvoApiOptions options)
    {
        var reader = new Utf8JsonReader(
            utf8Body,
            new JsonReaderOptions { MaxDepth = options.MaxPayloadDepth, AllowTrailingCommas = false });

        try
        {
            if (!reader.Read() || reader.TokenType != JsonTokenType.StartObject)
            {
                return NotAnObjectFailure;
            }

            return CountTopLevelKeys(ref reader, options.MaxPayloadKeys);
        }
        catch (JsonException)
        {
            // Covers a malformed body and one past MaxPayloadDepth alike: the reader raises the same
            // exception for both, and telling them apart is Task 5's structured-violation job.
            return MalformedJsonFailure;
        }
    }

    /// <summary>
    /// Walks the top-level object, refusing as soon as the key count crosses <paramref name="maxKeys"/> —
    /// a wide-but-shallow object is exactly what a depth cap alone misses.
    /// </summary>
    private static string? CountTopLevelKeys(ref Utf8JsonReader reader, int maxKeys)
    {
        var keys = 0;
        while (reader.Read() && reader.TokenType != JsonTokenType.EndObject)
        {
            if (reader.TokenType != JsonTokenType.PropertyName)
            {
                continue;
            }

            if (++keys > maxKeys)
            {
                return string.Create(
                    CultureInfo.InvariantCulture,
                    $"The request body carries more than {maxKeys} fields, the configured maximum.");
            }

            reader.Read();
            reader.Skip();
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
            value = Convert(node, field.Type);
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
    private static object? Convert(JsonNode node, FieldType type) => type == FieldType.Json
        ? node.ToJsonString(DataApiJson.Options)
        : node.Deserialize(FieldClrType.Of(type), DataApiJson.Options);

    private static string TooLargeFailure(int maxBytes) => string.Create(
        CultureInfo.InvariantCulture,
        $"The request body is larger than {maxBytes} bytes, the configured maximum.");
}
