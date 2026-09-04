using Microsoft.AspNetCore.Http;
using MMLib.Alvo.Data;
using MMLib.Alvo.Rules;
using MMLib.Alvo.Schema;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace MMLib.Alvo.Api.Internal;

/// <summary>
/// Reads a batch request body — <c>{"rows": [ … ]}</c> — and binds every row, or reports every reason it
/// could not.
/// </summary>
/// <remarks>
/// <para>
/// <b>The row bound is spent while reading</b>, exactly as <see cref="QueryBodyReader"/>'s value bound is:
/// a batch is refused at the first row past <see cref="AlvoApiOptions.MaxBatchRows"/> rather than after the
/// whole array has been materialised, because materialising first is the cost the bound exists to refuse.
/// </para>
/// <para>
/// <b><see cref="AlvoApiOptions.MaxPayloadKeys"/> applies per row, not across the body.</b> That number has
/// always meant "how many fields may one row carry"; spending it across a batch would cap a five-field
/// entity near a hundred rows and tell the caller they sent too many <em>fields</em>, which is advice about
/// the wrong thing. <see cref="AlvoApiOptions.MaxBatchRows"/> is the bound on rows.
/// </para>
/// <para>
/// <b>Every row is read before any refusal is returned.</b> A batch that reported its first bad row and
/// stopped would be sent again for each of the remaining ones — the same reason the port reports every
/// refused row rather than the first.
/// </para>
/// </remarks>
internal static class BatchBodyReader
{
    /// <summary>What a batch body bound to, or every reason it did not.</summary>
    /// <param name="Rows">The payloads, in request order; empty when anything was refused.</param>
    /// <param name="Ids">The row ids a batch update or delete named, in request order.</param>
    /// <param name="Violations">Every reason the batch cannot be performed.</param>
    internal sealed record Batch(
        IReadOnlyList<Dictionary<string, object?>> Rows,
        IReadOnlyList<Guid> Ids,
        IReadOnlyList<AlvoViolation> Violations);

    /// <summary>Reads and binds the batch body.</summary>
    /// <param name="request">The request whose body to read.</param>
    /// <param name="entity">The entity being written, as the applied schema declares it.</param>
    /// <param name="options">The payload bounds to enforce.</param>
    /// <param name="kind">Which batch verb this is; decides whether a row carries an id and a payload.</param>
    /// <param name="decision">The verdict the policy engine returned for this caller.</param>
    /// <param name="formats">The applied descriptor's compiled field formats.</param>
    /// <param name="data">The store, for the validator's own lookups.</param>
    /// <param name="context">The caller performing the batch.</param>
    /// <param name="cancellationToken">A token to cancel the read.</param>
    internal static async Task<Batch> ReadAsync(
        HttpRequest request,
        EntitySchema entity,
        AlvoApiOptions options,
        DataApiEndpointKind kind,
        PolicyDecision decision,
        FormatCatalog formats,
        IAlvoData data,
        AlvoContext context,
        CancellationToken cancellationToken)
    {
        using var body = new MemoryStream();
        var refusal = await BoundedJsonBody
            .ReadAsync(request, body, options, cancellationToken).ConfigureAwait(false);
        if (refusal is { } refused)
        {
            return Refused(PayloadViolations.Body(refused, options));
        }

        body.Position = 0;
        var node = JsonNode.Parse(
            body,
            nodeOptions: null,
            documentOptions: new JsonDocumentOptions { MaxDepth = options.MaxPayloadDepth });

        return node is JsonObject document && document[BatchViolations.RowsMember] is JsonArray rows
            ? await BoundAsync(rows, entity, options, kind, decision, formats, data, context, cancellationToken)
                .ConfigureAwait(false)
            : Refused(BatchViolations.NotABatch());
    }

    /// <summary>A batch that bound nothing at all, carrying the one violation that stopped it.</summary>
    /// <param name="violation">The refusal.</param>
    private static Batch Refused(AlvoViolation violation) => new([], [], [violation]);

    /// <inheritdoc cref="ReadAsync"/>
    /// <param name="rows">The <c>rows</c> array.</param>
    /// <param name="entity">The entity being written.</param>
    /// <param name="options">The payload bounds to enforce.</param>
    /// <param name="kind">Which batch verb this is.</param>
    /// <param name="decision">The verdict the policy engine returned for this caller.</param>
    /// <param name="formats">The applied descriptor's compiled field formats.</param>
    /// <param name="data">The store, for the validator's own lookups.</param>
    /// <param name="context">The caller performing the batch.</param>
    /// <param name="cancellationToken">A token to cancel the read.</param>
    private static async Task<Batch> BoundAsync(
        JsonArray rows,
        EntitySchema entity,
        AlvoApiOptions options,
        DataApiEndpointKind kind,
        PolicyDecision decision,
        FormatCatalog formats,
        IAlvoData data,
        AlvoContext context,
        CancellationToken cancellationToken)
    {
        if (rows.Count == 0)
        {
            return Refused(BatchViolations.EmptyBatch());
        }

        if (rows.Count > options.MaxBatchRows)
        {
            return Refused(BatchViolations.TooManyRows(options.MaxBatchRows));
        }

        var bound = new List<Dictionary<string, object?>>(rows.Count);
        var ids = new List<Guid>(rows.Count);
        var violations = new List<AlvoViolation>();
        for (var index = 0; index < rows.Count; index++)
        {
            await BindRowAsync(
                rows[index], index, entity, kind, decision, formats, data, context, bound, ids, violations,
                cancellationToken).ConfigureAwait(false);
        }

        return violations.Count > 0 ? new Batch([], [], violations) : new Batch(bound, ids, []);
    }

    /// <summary>Binds one row of the batch, or records every reason it could not be bound.</summary>
    /// <remarks>
    /// A delete's row is the bare id and carries no payload at all, so it is bound and validated as nothing —
    /// which is why the id is read before the shape is judged.
    /// </remarks>
    /// <param name="node">The array element.</param>
    /// <param name="index">The row's position, counting from zero.</param>
    /// <param name="entity">The entity being written.</param>
    /// <param name="kind">Which batch verb this is.</param>
    /// <param name="decision">The verdict the policy engine returned for this caller.</param>
    /// <param name="formats">The applied descriptor's compiled field formats.</param>
    /// <param name="data">The store, for the validator's own lookups.</param>
    /// <param name="context">The caller performing the batch.</param>
    /// <param name="bound">The payloads bound so far.</param>
    /// <param name="ids">The row ids read so far.</param>
    /// <param name="violations">The refusals collected so far.</param>
    /// <param name="cancellationToken">A token to cancel the read.</param>
    private static async Task BindRowAsync(
        JsonNode? node,
        int index,
        EntitySchema entity,
        DataApiEndpointKind kind,
        PolicyDecision decision,
        FormatCatalog formats,
        IAlvoData data,
        AlvoContext context,
        List<Dictionary<string, object?>> bound,
        List<Guid> ids,
        List<AlvoViolation> violations,
        CancellationToken cancellationToken)
    {
        if (kind == DataApiEndpointKind.BatchDelete)
        {
            AddId(node, index, ids, violations);
            return;
        }

        if (node is not JsonObject row)
        {
            violations.Add(BatchViolations.RowIsNotAnObject(index));
            return;
        }

        if (kind == DataApiEndpointKind.BatchUpdate)
        {
            AddId(row[AlvoManagedColumns.Id], index, ids, violations);
            row = Without(row, AlvoManagedColumns.Id);
        }

        var payload = JsonPayloadReader.BindObject(row, entity);
        var validated = await RecordValidator.ValidateAsync(
            new RecordValidationRequest(
                entity,
                payload.Values,
                IsCreate: kind == DataApiEndpointKind.BatchCreate,
                decision.ReadOnlyFields,
                RefusedFields(payload.Violations),
                formats,
                data,
                context),
            cancellationToken).ConfigureAwait(false);

        var refusals = payload.Violations.Concat(validated).ToList();
        if (refusals.Count > 0)
        {
            violations.AddRange(refusals.Select(violation => AtRow(index, violation)));
            return;
        }

        bound.Add(payload.Values);
    }

    /// <summary>Reads one row's <c>id</c>, or records that it is absent or not a uuid.</summary>
    /// <param name="node">The value that should be the row id.</param>
    /// <param name="index">The row's position, counting from zero.</param>
    /// <param name="ids">The row ids read so far.</param>
    /// <param name="violations">The refusals collected so far.</param>
    private static void AddId(JsonNode? node, int index, List<Guid> ids, List<AlvoViolation> violations)
    {
        if (node is JsonValue value && value.TryGetValue(out Guid id))
        {
            ids.Add(id);
            return;
        }

        violations.Add(BatchViolations.RowIdIsNotAUuid(index));
    }

    /// <summary>
    /// <paramref name="row"/> without <paramref name="member"/>, so the update's payload is what the caller
    /// is changing rather than the row key they are addressing.
    /// </summary>
    /// <remarks>
    /// A copy rather than a removal in place, because the parsed document is also what the fingerprint
    /// digests — mutating it would make the digest cover a body the caller never sent.
    /// </remarks>
    /// <param name="row">The row object as it was parsed.</param>
    /// <param name="member">The member to leave out.</param>
    private static JsonObject Without(JsonObject row, string member) =>
        new(row.Where(entry => !string.Equals(entry.Key, member, StringComparison.Ordinal))
            .Select(entry => KeyValuePair.Create(entry.Key, entry.Value?.DeepClone())));

    /// <summary>The same violation, pointed at its row.</summary>
    /// <param name="index">The row's position, counting from zero.</param>
    /// <param name="violation">The violation a single write would have produced.</param>
    private static AlvoViolation AtRow(int index, AlvoViolation violation) =>
        violation with { Pointer = BatchViolations.FieldPointer(index, violation.Pointer) };

    /// <inheritdoc cref="DataApiEndpoints"/>
    /// <param name="violations">The reader's own violations.</param>
    private static HashSet<string> RefusedFields(IReadOnlyList<AlvoViolation> violations) =>
        violations
            .Select(violation => PayloadViolations.FieldOf(violation.Pointer))
            .OfType<string>()
            .ToHashSet(StringComparer.Ordinal);
}
