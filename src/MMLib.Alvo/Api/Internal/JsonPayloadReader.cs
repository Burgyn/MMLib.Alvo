using Microsoft.AspNetCore.Http;
using MMLib.Alvo.Schema;
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
/// be called at all. <see cref="RecordValidator"/> validates <em>over</em> these values (required, max
/// length, scale, enum, format, reference existence); it does not replace this, and the two report into one
/// list of <see cref="AlvoViolation"/> so a body with a bad type <em>and</em> a missing required field is
/// one response rather than two round trips.
/// </para>
/// <para>
/// <b>Everything here runs after authorization and before validation.</b> <c>DataApiEndpoints</c> resolves
/// the operation's policy decision and the API-key scope gate <em>before</em> calling this, so a caller who
/// is going to be refused never pays for a parse — doing megabytes of work on behalf of a request that
/// cannot succeed is a denial-of-service amplifier, and it is also the correct precedence: an unauthorized
/// caller must not be told their body was malformed. It is still bounded three ways
/// (<see cref="AlvoApiOptions.MaxRequestBodyBytes"/>, <see cref="AlvoApiOptions.MaxPayloadDepth"/>,
/// <see cref="AlvoApiOptions.MaxPayloadKeys"/>), because an <em>authorized</em> caller can be hostile too —
/// and all three are enforced by <see cref="BoundedJsonBody"/>, which the query surface reads its own body
/// through as well, so a bound cannot be tightened for one surface and left alone for the other. What stays
/// here is the parse and the binding: the node tree exists because <see cref="IdempotencyFingerprint"/>
/// cannot be built from a bound value bag, which is a write's requirement rather than a shared one.
/// </para>
/// <para>
/// <b>An undeclared key is refused before its value is materialised</b> — not to withhold anything, but so
/// no attacker-controlled <em>value</em> is re-serialised into a string on its way to a refusal that was
/// already certain. The key itself is named, as the violation's JSON Pointer; see
/// <see cref="PayloadViolations.UnknownField"/> for why that is the location and not a disclosure.
/// </para>
/// <para>
/// <b>A body-level refusal stops the read; a per-field one does not.</b> A body that is too large, too
/// deep, not an object or not JSON has nothing to bind, so it produces exactly one violation. Once the
/// document is a bindable object, every offending <em>key</em> is reported — one violation per field rather
/// than the first failure ending the read, which is the same reason the query parser collects.
/// </para>
/// </remarks>
internal static class JsonPayloadReader
{
    /// <summary>What one body read produced: the bound values, and every reason a field or the body was refused.</summary>
    /// <remarks>
    /// <para>
    /// <b><paramref name="BoundAsAnObject"/> is stated by the reader, never inferred from a violation's
    /// pointer.</b> It was inferred once — "every pointer is non-empty, so the body must have bound" — and
    /// that made a structural fact ride on an in-band value: an unrecognised <em>key</em> reported against
    /// the body pointer, so one such key made the reader conclude the whole body had failed to bind, and
    /// every other violation was discarded. A payload simultaneously missing a required field, over a length
    /// bound and writing a read-only field came back with <b>one</b> violation. Two different questions
    /// cannot share one representation; this record answers the structural one explicitly and nothing else
    /// encodes it.
    /// </para>
    /// <para>
    /// It exists at all because <b>a body that is not an object must not be validated as if it were
    /// empty</b>: an array, a scalar, a truncated document or an over-bound body binds no field, so running
    /// the record validator over it would report every required field as missing beside the real reason —
    /// telling a caller who sent <c>[1,2,3]</c> to supply <c>name</c>, which is advice about a body they
    /// never sent.
    /// </para>
    /// </remarks>
    /// <param name="Values">
    /// The bound field values — every key that bound, even when another key did not, so
    /// <see cref="RecordValidator"/> can measure the rest of the payload in the same pass.
    /// </param>
    /// <param name="Violations">Every reason the body was refused; empty when it bound completely.</param>
    /// <param name="BoundAsAnObject">Whether the body was a JSON object this entity's fields could be read out of at all.</param>
    /// <param name="Document">
    /// The body exactly as it was parsed, or <see langword="null"/> when nothing parsed as an object.
    /// <para>
    /// It is carried alongside <paramref name="Values"/> rather than reconstructed from them because
    /// <see cref="IdempotencyFingerprint"/> cannot be built from a bound value bag. <b>It is <em>not</em>
    /// because a key might be missing from <paramref name="Values"/></b> — an unbound key is a violation, and a
    /// violation is answered before a fingerprint is ever computed, so that reason (which is what this remark
    /// used to give) describes a request the digest never sees. The two operative reasons are:
    /// </para>
    /// <para>
    /// <b>A JSON token is finer than the CLR value it binds to</b>, and the digest is the one place that
    /// difference matters. Two bodies that bind to the same <see cref="decimal"/> or the same
    /// <see cref="DateTimeOffset"/> can be different requests on the wire, and a digest over the bound values
    /// would call them one — the failure direction that answers the second caller with the first caller's row.
    /// Digesting what arrived keeps the imprecision on the safe side (a 409).
    /// </para>
    /// <para>
    /// <b>A <see cref="Dictionary{TKey, TValue}"/> has no defined order</b>, so a digest over it would need a
    /// canonicalization of its own — a second implementation of the thing
    /// <see cref="IdempotencyFingerprint"/> exists to own, over values that no longer carry their JSON shape.
    /// </para>
    /// </param>
    internal sealed record Payload(
        Dictionary<string, object?> Values,
        IReadOnlyList<AlvoViolation> Violations,
        bool BoundAsAnObject,
        JsonObject? Document);

    /// <summary>Reads and binds the request body, or reports why it could not be.</summary>
    /// <param name="request">The request whose body to read.</param>
    /// <param name="entity">The entity being written, as the applied schema declares it.</param>
    /// <param name="options">The payload bounds to enforce.</param>
    /// <param name="cancellationToken">A token to cancel the read.</param>
    /// <returns>The bound field values plus every violation that stopped part of the body binding.</returns>
    internal static async Task<Payload> ReadAsync(
        HttpRequest request, EntitySchema entity, AlvoApiOptions options, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(entity);
        ArgumentNullException.ThrowIfNull(options);

        using var body = new MemoryStream();
        var refusal = await BoundedJsonBody
            .ReadAsync(request, body, options, cancellationToken).ConfigureAwait(false);

        return refusal is { } refused
            ? Refused(PayloadViolations.Body(refused, options))
            : Bind(body, entity, options);
    }

    /// <summary>A body that bound nothing at all, carrying the one violation that stopped it.</summary>
    private static Payload Refused(AlvoViolation violation) =>
        new([], [violation], BoundAsAnObject: false, Document: null);

    /// <summary>Parses the already-bounded body into a node tree and binds every key to its declared type.</summary>
    /// <remarks>
    /// A key that cannot be bound does not stop the ones after it: each contributes its own violation and
    /// the rest of the payload still binds, so <see cref="RecordValidator"/> measures what is there in the
    /// same pass and the caller sees every problem at once.
    /// </remarks>
    private static Payload Bind(MemoryStream body, EntitySchema entity, AlvoApiOptions options)
    {
        body.Position = 0;
        var node = JsonNode.Parse(
            body,
            nodeOptions: null,
            documentOptions: new JsonDocumentOptions { MaxDepth = options.MaxPayloadDepth });
        if (node is not JsonObject payload)
        {
            return Refused(PayloadViolations.NotAnObject());
        }

        var declared = DeclaredFields(entity);
        var values = new Dictionary<string, object?>(StringComparer.Ordinal);
        var violations = new List<AlvoViolation>();
        foreach (var (key, value) in payload)
        {
            BindOne(key, value, declared, values, violations);
        }

        return new Payload(values, violations, BoundAsAnObject: true, payload);
    }

    /// <summary>Binds one key, or records why it could not be bound.</summary>
    private static void BindOne(
        string key,
        JsonNode? value,
        Dictionary<string, FieldSchema> declared,
        Dictionary<string, object?> values,
        List<AlvoViolation> violations)
    {
        if (!declared.TryGetValue(key, out var field))
        {
            violations.Add(PayloadViolations.UnknownField(key));
            return;
        }

        if (TryBind(value, field, out var bound))
        {
            values[key] = bound;
            return;
        }

        violations.Add(PayloadViolations.UnrepresentableValue(field));
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
    private static bool TryBind(JsonNode? node, FieldSchema field, out object? value)
    {
        if (node is null)
        {
            value = null;
            return true;
        }

        try
        {
            value = Convert(node, field);
            return true;
        }
        catch (JsonException)
        {
            value = null;
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
}
