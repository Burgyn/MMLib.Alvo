using MMLib.Alvo.Data;
using System.Buffers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace MMLib.Alvo.Api.Internal;

/// <summary>
/// The digest of "the request this idempotency key was first used for" — <c>AlvoIdempotency.Fingerprint</c>
/// for an HTTP create.
/// </summary>
/// <remarks>
/// <para>
/// <b>The port cannot compute this, and it says so:</b> only the layer that owns the wire format knows what
/// "the same request" means on it. What the port <em>does</em> promise is what happens when the fingerprint is
/// wrong in each direction, and the two are not symmetric — which is why this type exists as a named authority
/// rather than as three lines inside a delegate:
/// </para>
/// <list type="bullet">
///   <item>
///   A fingerprint that omitted the <b>entity</b> would be caught by the port: a replay re-reads the recorded
///   row id under the entity of the request being served, finds nothing, and answers a not-found. Wrong, but
///   fail-closed.
///   </item>
///   <item>
///   A fingerprint too coarse <b>within</b> one entity — one that dropped a field — is <b>silently wrong</b>.
///   The second, different request matches the stored fingerprint, so it is answered with the first request's
///   row, with no error raised anywhere and nothing in either response to notice. The caller holds an id for a
///   row that does not contain what they sent.
///   </item>
/// </list>
/// <para>
/// So <b>every field of the body is in the digest</b>, and it is in it by construction rather than by a list
/// this type maintains: the whole parsed document is walked, so a field added to an entity tomorrow is covered
/// with no edit here.
/// <c>IdempotencyTests.Two_creates_differing_only_in_a_field_the_fingerprint_must_cover_are_a_conflict</c>
/// holds that claim over every field the entity declares.
/// </para>
/// <para>
/// <b>Canonical, not verbatim.</b> The body is re-serialized from the parsed document with property names
/// sorted ordinally, so a retry that reformats its own JSON — different whitespace, different key order,
/// which two runs of one serializer are entitled to produce — is a <em>replay</em> rather than a 409. A
/// digest over the raw bytes would make a retrying client's success depend on byte-identical whitespace,
/// which no HTTP client promises.
/// </para>
/// <para>
/// <b>Canonicalization stops at the token, deliberately.</b> A number keeps the spelling it arrived with
/// (<c>1</c> and <c>1.0</c> are different tokens) and a string keeps whatever escaping its writer chose. Both
/// could be normalized further, and neither is: getting it wrong in <em>this</em> direction costs a caller a
/// 409 and a fresh key, whereas the other direction — treating two spellings as one request — is the silent
/// wrong answer above. A conflict is the safe way to be imprecise.
/// </para>
/// </remarks>
internal static class IdempotencyFingerprint
{
    /// <summary>
    /// The fingerprint of one write: its method, its entity, the row it addresses, the precondition it
    /// carries and its body.
    /// </summary>
    /// <param name="method">The request method, e.g. <c>POST</c>.</param>
    /// <param name="entity">The entity being written, as the applied schema names it.</param>
    /// <param name="id">The row the write addresses, or <see langword="null"/> for a create.</param>
    /// <param name="precondition">The version the write is conditional on, or <see langword="null"/>.</param>
    /// <param name="body">
    /// The request body as the payload reader parsed it, or <see langword="null"/> for a delete, which
    /// carries none.
    /// </param>
    /// <returns>A lower-case hex SHA-256 digest.</returns>
    /// <remarks>
    /// <para>
    /// <b>The route is deliberately <em>not</em> in the digest, and it was once.</b> Including the mapped
    /// template embedded <see cref="AlvoApiOptions.RoutePrefix"/>, so moving the prefix — a deployment-time
    /// configuration change — invalidated every stored fingerprint, and a client retry that straddled that
    /// redeploy became a 409 rather than a replay. What identifies the operation is <em>what</em> was written
    /// and <em>how</em>, not where an operator chose to mount the API. Dropping it also removed the one part of
    /// the digest no fact could hold on its own: entity and template were mutually redundant (the template
    /// contains the entity name), so removing either alone killed nothing and only their conjunction was
    /// covered.
    /// </para>
    /// <para>
    /// The parts are joined by a newline, which cannot be mistaken for part of a neighbour, and the
    /// operative reason is the <b>schema's</b> rather than the API's: an entity name is constrained by
    /// <c>project.schema.json</c> to <c>^[a-z][a-z0-9_]{0,62}$</c>, so it can hold no separator; the method is
    /// one token from HTTP's own closed set; a <see cref="Guid"/>'s <c>D</c> form is hex and hyphens; the
    /// precondition is <c>v</c> followed by digits; and the caller-controlled part is last <em>and</em> carries
    /// no raw newline, because a JSON writer escapes every control character inside a string. So no two
    /// different requests share a digest input.
    /// </para>
    /// <para>
    /// <b>The row id is in the digest and it has to be.</b> Without it, <c>PATCH /vehicles/A</c> and
    /// <c>PATCH /vehicles/B</c> carrying one key and one body share a fingerprint — so the second is answered
    /// as a <em>replay</em> of the first, row B is never written, and the caller is told it was. That is the
    /// "silently wrong" direction the bullet list above describes, one level up: the caller holds an id for a
    /// row that does not contain what they sent.
    /// </para>
    /// <para>
    /// <b>Each optional part is appended only when it is present, which is what keeps a create's digest where
    /// it was.</b> Always joining an empty segment would digest a create as <c>POST\nowners\n\n{…}</c> — a
    /// different value, so every create in flight across the deploy that widened this would become a 409.
    /// <c>A_creates_fingerprint_is_unchanged_by_the_parameters_a_create_does_not_carry</c> holds it, and the
    /// <c>v</c> prefix is what keeps a precondition from ever being read as an id.
    /// </para>
    /// <para>
    /// <b>The precondition is digested as an instant</b> (<see cref="DateTimeOffset.UtcTicks"/>), because
    /// <see cref="AlvoPrecondition.EnsureMatches"/> compares instants: two offsets of one instant are one
    /// precondition to the port, and a formatted timestamp would make them two digests and two records.
    /// <see cref="RowVersionETag"/> already encodes the ticks, so this is the existing spelling rather than a
    /// new one.
    /// </para>
    /// <para>
    /// SHA-256 rather than a fast non-cryptographic hash: the consequence of a collision is one caller's create
    /// being answered with another request's row, so the property needed is that a collision cannot be
    /// <em>constructed</em>, not merely that it is unlikely. It is not a secret and needs no key — the digest
    /// only ever meets a value the same caller's earlier request produced.
    /// </para>
    /// </remarks>
    internal static string Of(
        string method, string entity, Guid? id, AlvoPrecondition? precondition, JsonObject? body)
    {
        var input = new StringBuilder(method).Append('\n').Append(entity);
        if (id is { } row)
        {
            input.Append('\n').Append(row);
        }

        if (precondition is { } version)
        {
            input.Append("\nv").Append(version.Version.UtcTicks);
        }

        input.Append('\n').Append(body is null ? string.Empty : Canonical(body));

        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(input.ToString())));
    }

    /// <summary>The body re-serialized with every object's property names in ordinal order.</summary>
    /// <param name="body">The parsed body.</param>
    private static string Canonical(JsonObject body)
    {
        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer, new JsonWriterOptions { Indented = false }))
        {
            Write(body, writer);
        }

        return Encoding.UTF8.GetString(buffer.WrittenSpan);
    }

    /// <summary>
    /// Writes one node, sorting an object's members and leaving an array's in place.
    /// </summary>
    /// <remarks>
    /// <para>
    /// An array's order is <b>not</b> sorted, because in JSON it is data: <c>[1,2]</c> and <c>[2,1]</c> are
    /// two different values, and a canonicalization that conflated them would drop information out of the
    /// digest exactly as dropping a field would.
    /// </para>
    /// <para>
    /// The recursion is bounded before it starts. <c>JsonPayloadReader</c> refuses a body deeper than
    /// <see cref="AlvoApiOptions.MaxPayloadDepth"/> from a forward-only scan that builds no tree, and this
    /// only ever runs on a body that survived it — so there is no depth here that a request could choose.
    /// </para>
    /// </remarks>
    /// <param name="node">The node to write.</param>
    /// <param name="writer">The writer to write it to.</param>
    private static void Write(JsonNode? node, Utf8JsonWriter writer)
    {
        switch (node)
        {
            case JsonObject members:
                WriteObject(members, writer);
                break;
            case JsonArray items:
                WriteArray(items, writer);
                break;
            case null:
                writer.WriteNullValue();
                break;
            default:
                node.WriteTo(writer);
                break;
        }
    }

    private static void WriteObject(JsonObject members, Utf8JsonWriter writer)
    {
        writer.WriteStartObject();
        foreach (var member in members.OrderBy(member => member.Key, StringComparer.Ordinal))
        {
            writer.WritePropertyName(member.Key);
            Write(member.Value, writer);
        }

        writer.WriteEndObject();
    }

    private static void WriteArray(JsonArray items, Utf8JsonWriter writer)
    {
        writer.WriteStartArray();
        foreach (var item in items)
        {
            Write(item, writer);
        }

        writer.WriteEndArray();
    }
}
