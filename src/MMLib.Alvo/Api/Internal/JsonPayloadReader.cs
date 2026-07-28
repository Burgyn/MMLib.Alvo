using MMLib.Alvo.Schema;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace MMLib.Alvo.Api.Internal;

/// <summary>
/// Binds a JSON request body to the CLR values <c>IAlvoData</c>'s write methods take, using the field
/// types the applied schema declares.
/// </summary>
/// <remarks>
/// <para>
/// This is <b>binding, not validation</b>. The port publishes a typed contract — a <c>uuid</c> field is a
/// <see cref="Guid"/>, a timestamp a <see cref="DateTimeOffset"/>, a decimal a <see cref="decimal"/>
/// (<c>FieldClrTypeMap</c>'s remarks) — and JSON has none of those types, so something has to convert
/// before the port is called at all. Task 5's <c>RecordValidator</c> validates <em>over</em> these values
/// (required, max length, scale, enum, format, FK existence) and reports every violation as RFC 7807;
/// it does not replace this.
/// </para>
/// <para>
/// A key the entity does not declare is passed through untouched rather than rejected here, because the
/// port already refuses it in one place (<c>WritePayloadGuard</c>) with a message that does not confirm
/// whether the field exists. Task 5 adds the earlier 422 with a fix suggestion; the port's 403 stays as
/// the backstop for a caller that bypasses this layer.
/// </para>
/// </remarks>
internal static class JsonPayloadReader
{
    /// <summary>
    /// Reads a JSON object body into field values typed as <paramref name="entity"/> declares them.
    /// </summary>
    /// <param name="body">The parsed request body.</param>
    /// <param name="entity">The entity being written.</param>
    /// <param name="values">The bound field values, when this returns <see langword="true"/>.</param>
    /// <param name="failure">Why binding failed, when this returns <see langword="false"/>.</param>
    /// <returns><see langword="true"/> when every present key bound to its declared type.</returns>
    internal static bool TryRead(
        JsonNode? body,
        EntitySchema entity,
        out Dictionary<string, object?> values,
        out string? failure)
    {
        ArgumentNullException.ThrowIfNull(entity);
        values = new Dictionary<string, object?>(StringComparer.Ordinal);

        if (body is not JsonObject payload)
        {
            failure = "The request body must be a JSON object of field names to values.";
            return false;
        }

        foreach (var (key, node) in payload)
        {
            if (!TryBind(key, node, entity, out var value, out failure))
            {
                return false;
            }

            values[key] = value;
        }

        failure = null;
        return true;
    }

    private static bool TryBind(
        string key, JsonNode? node, EntitySchema entity, out object? value, out string? failure)
    {
        failure = null;
        var field = entity.Fields.FirstOrDefault(candidate => string.Equals(candidate.Name, key, StringComparison.Ordinal));
        if (node is null)
        {
            value = null;
            return true;
        }

        if (field is null)
        {
            // Undeclared: hand the raw text on and let the port's own single refusal answer it.
            value = node.ToJsonString();
            return true;
        }

        return TryConvert(key, node, field.Type, out value, out failure);
    }

    private static bool TryConvert(
        string key, JsonNode node, FieldType type, out object? value, out string? failure)
    {
        try
        {
            value = Convert(node, type);
            failure = null;
            return true;
        }
        catch (Exception exception) when (exception is FormatException or InvalidOperationException or OverflowException)
        {
            // Task 5: this becomes one AlvoViolation per offending field, with a JSON Pointer and a fix
            // suggestion, instead of the first failure stopping the read.
            value = null;
            failure = string.Create(
                CultureInfo.InvariantCulture,
                $"The value supplied for '{key}' is not a valid {type.ToString().ToLowerInvariant()}.");
            return false;
        }
    }

    /// <summary>
    /// Converts one JSON value to the CLR type the field's <see cref="FieldType"/> maps to — the same
    /// mapping the read path returns values in, so a value written and read back round-trips.
    /// </summary>
    private static object? Convert(JsonNode node, FieldType type) => type switch
    {
        FieldType.Uuid or FieldType.Ref => Guid.Parse(node.GetValue<string>(), CultureInfo.InvariantCulture),
        FieldType.String or FieldType.Text or FieldType.Enum => node.GetValue<string>(),
        FieldType.Integer => node.GetValue<long>(),
        FieldType.Decimal => node.GetValue<decimal>(),
        FieldType.Boolean => node.GetValue<bool>(),
        FieldType.Date => DateOnly.Parse(node.GetValue<string>(), CultureInfo.InvariantCulture),
        FieldType.DateTime => DateTimeOffset.Parse(node.GetValue<string>(), CultureInfo.InvariantCulture),
        FieldType.Json => node.ToJsonString(),
        _ => throw new InvalidOperationException($"Field type '{type}' has no JSON binding."),
    };

    /// <summary>
    /// Reads the request body as a JSON node, or reports that it was not JSON at all.
    /// </summary>
    /// <param name="body">The raw body stream.</param>
    /// <param name="cancellationToken">A token to cancel the read.</param>
    internal static async Task<(JsonNode? Node, string? Failure)> ParseAsync(
        Stream body, CancellationToken cancellationToken)
    {
        try
        {
            return (await JsonNode.ParseAsync(body, cancellationToken: cancellationToken).ConfigureAwait(false), null);
        }
        catch (JsonException)
        {
            // Task 5: a malformed body is a 422 carrying a violation, not a bare detail string. Caught
            // here rather than left to minimal API's own body binding, which answers 400 for a parse
            // failure and would put one refusal on two status codes.
            return (null, "The request body is not well-formed JSON.");
        }
    }
}
