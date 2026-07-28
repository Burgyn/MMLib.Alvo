using MMLib.Alvo.Data;
using MMLib.Alvo.Rules;
using MMLib.Alvo.Schema;

namespace MMLib.Alvo.Api.Internal;

/// <summary>
/// Validates a bound write payload against the entity's declared shape and the caller's resolved field
/// masks, reporting <b>every</b> violation rather than the first.
/// </summary>
/// <remarks>
/// <para>
/// <b>Every violation, because the reader is an agent.</b> §2.1 and #19's definition of done both ask for
/// the full list, and the reason is arithmetic: a payload with three problems answered one at a time is
/// three round trips, each one a fresh chance to introduce a fourth. Ordering the checks so one field yields
/// at most one violation is what keeps the list actionable — a null required field reported as both "missing"
/// and "too short" tells a caller to fix two things that are one thing.
/// </para>
/// <para>
/// <b>This validates; it does not authorize.</b> Whether the caller may write at all is already resolved
/// (the endpoint checks the operation's decision before the body is even read) and is resolved again inside
/// the port, which remains the authority. The one thing taken from the decision here is
/// <see cref="PolicyDecision.ReadOnlyFields"/> — the caller's own resolved mask — and it produces a 422 with
/// a fix rather than a 403 with none. The port's <c>AlvoAuthorizationException</c> for the same write is not
/// replaced by that; it is the backstop for a caller who never came through HTTP.
/// </para>
/// <para>
/// <b>Two things it deliberately does not check.</b> A write to a <c>hidden</c> field is <em>accepted</em>:
/// <c>hidden</c> restricts reading and <c>readOnly</c> restricts writing, they are different flags, and
/// refusing the write would both change the port's contract and disclose the hidden field's existence — the
/// one thing the query parser's mask parameter exists to withhold. And a
/// <see cref="AlvoManagedColumns">framework-managed column</see> is skipped entirely, in both directions:
/// its <c>required</c> flag is not the caller's to satisfy (the framework assigns <c>id</c>,
/// <c>created_at</c> and <c>updated_at</c>, so demanding them would refuse every well-formed create), and a
/// payload that <em>does</em> name one is the port's to refuse — its
/// <c>AlvoAuthorizationException</c> for that is <c>IAlvoData</c>'s documented contract, decided before any
/// row is looked up, and pre-empting it with a 422 here would quietly reclassify a documented 403.
/// </para>
/// </remarks>
internal static class RecordValidator
{
    /// <summary>Validates one write payload, returning every violation it carries.</summary>
    /// <param name="request">Everything the validation is measured against.</param>
    /// <param name="cancellationToken">A token to cancel the reference probes.</param>
    /// <returns>Every violation, in field order; empty when the payload is valid.</returns>
    internal static async Task<IReadOnlyList<AlvoViolation>> ValidateAsync(
        RecordValidationRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var violations = new List<AlvoViolation>();
        var references = new List<FieldSchema>();
        var managed = AlvoManagedColumns.For(request.Entity);
        foreach (var field in request.Entity.Fields.Where(field => !managed.Contains(field.Name)))
        {
            Check(field, request, violations, references);
        }

        await AddUnresolvedReferencesAsync(references, request, violations, cancellationToken)
            .ConfigureAwait(false);
        return violations;
    }

    /// <summary>
    /// Runs one field's checks in the order that yields <b>at most one</b> violation for it, and remembers
    /// a reference whose value survived every check so the round trip it costs is only spent on input that
    /// was otherwise going to be accepted.
    /// </summary>
    /// <remarks>
    /// A field the body reader already refused is skipped entirely: its value never bound, so every check
    /// below would be measuring the absence the reader has already reported. Without that, a body sending
    /// <c>"year": "abc"</c> for a required field would come back with both "not a valid integer" and
    /// "required", and only one of them is true.
    /// </remarks>
    private static void Check(
        FieldSchema field,
        RecordValidationRequest request,
        List<AlvoViolation> violations,
        List<FieldSchema> references)
    {
        if (request.AlreadyReported.Contains(field.Name))
        {
            return;
        }

        var supplied = request.Values.TryGetValue(field.Name, out var value);
        if (supplied && request.ReadOnlyFields.Contains(field.Name))
        {
            violations.Add(PayloadViolations.ReadOnly(field));
            return;
        }

        if (IsMissingRequiredValue(field, request, supplied, value))
        {
            violations.Add(PayloadViolations.Required(field));
            return;
        }

        if (!supplied || value is null)
        {
            return;
        }

        AddValueViolation(field, value, request, violations, references);
    }

    /// <summary>
    /// Whether the payload leaves a required field without a value.
    /// </summary>
    /// <remarks>
    /// <b>A create and a partial update ask different questions.</b> <c>UpdateAsync</c> is partial by
    /// contract — "a field this dictionary does not mention keeps its stored value" — so an <em>absent</em>
    /// required field on a PATCH is not a missing value, it is an unchanged one, and demanding it would make
    /// every partial update send the whole row. An explicit <see langword="null"/> is a different request in
    /// both: the caller is asking to clear a field the entity declares required, which no create and no
    /// update may do.
    /// </remarks>
    private static bool IsMissingRequiredValue(
        FieldSchema field, RecordValidationRequest request, bool supplied, object? value) =>
        field.Required && (supplied ? value is null : request.IsCreate);

    /// <summary>Runs the value-shaped checks, at most one of which can report.</summary>
    /// <remarks>
    /// Ordered widest-first: a value that overruns the column cannot be stored at all, so reporting its
    /// format instead would name the smaller of two problems. A reference is queued rather than checked,
    /// because it costs a round trip.
    /// </remarks>
    private static void AddValueViolation(
        FieldSchema field,
        object value,
        RecordValidationRequest request,
        List<AlvoViolation> violations,
        List<FieldSchema> references)
    {
        var violation = TooLong(field, value)
            ?? OutsideDecimalBounds(field, value)
            ?? OutsideEnumValues(field, value)
            ?? FailedFormat(field, value, request.Formats);

        if (violation is not null)
        {
            violations.Add(violation);
            return;
        }

        if (field.Reference is not null)
        {
            references.Add(field);
        }
    }

    private static AlvoViolation? TooLong(FieldSchema field, object value) =>
        field.MaxLength is { } maxLength && value is string text && text.Length > maxLength
            ? PayloadViolations.MaxLength(field)
            : null;

    /// <summary>
    /// The two bounds a <c>decimal</c> field declares: how many fractional digits it keeps, and how many
    /// digits it keeps in total.
    /// </summary>
    /// <remarks>
    /// Both are checked here rather than left to the engine, which answers with a truncation on one backend
    /// and an overflow on another — and a truncation is the worse of the two, because it stores a number the
    /// caller never sent.
    /// </remarks>
    private static AlvoViolation? OutsideDecimalBounds(FieldSchema field, object value)
    {
        if (value is not decimal number)
        {
            return null;
        }

        return ExceedsScale(field, number) ? PayloadViolations.Scale(field)
            : ExceedsPrecision(field, number) ? PayloadViolations.Precision(field)
            : null;
    }

    /// <summary>
    /// Whether <paramref name="number"/> needs more fractional digits than the field keeps.
    /// </summary>
    /// <remarks>
    /// Measured by rounding rather than by counting the digits of the literal, because
    /// <see cref="decimal"/> preserves trailing zeros: <c>1.230</c> has a scale of three and a value
    /// perfectly representable at a scale of two. Counting digits would refuse it, which is a refusal the
    /// caller cannot act on — their number <em>is</em> within the bound.
    /// </remarks>
    private static bool ExceedsScale(FieldSchema field, decimal number) =>
        field.Scale is { } scale and >= 0 and <= 28 && number != decimal.Round(number, scale);

    /// <summary>
    /// Whether <paramref name="number"/> needs more integral digits than the field's precision leaves after
    /// its scale — the same rule SQL's <c>NUMERIC(p,s)</c> applies.
    /// </summary>
    /// <remarks>
    /// The integral bound is computed as a <see cref="decimal"/> power of ten rather than through
    /// <see cref="Math.Pow"/>, whose double result is already inexact at the magnitudes a
    /// <c>NUMERIC(28,0)</c> column reaches — an inexact bound refuses or admits the boundary value
    /// arbitrarily. A precision the <see cref="decimal"/> type cannot express at all is not checked here;
    /// the value could not have been bound in the first place.
    /// </remarks>
    private static bool ExceedsPrecision(FieldSchema field, decimal number)
    {
        if (field.Precision is not { } precision || field.Scale is not { } scale)
        {
            return false;
        }

        var integralDigits = precision - scale;
        return integralDigits is >= 0 and <= 28 && Math.Abs(number) >= PowerOfTen(integralDigits);
    }

    private static decimal PowerOfTen(int exponent)
    {
        var result = 1m;
        for (var digit = 0; digit < exponent; digit++)
        {
            result *= 10m;
        }

        return result;
    }

    private static AlvoViolation? OutsideEnumValues(FieldSchema field, object value) =>
        field.Type == FieldType.Enum
        && field.EnumValues is { } declared
        && value is string candidate
        && !declared.Contains(candidate, StringComparer.Ordinal)
            ? PayloadViolations.EnumValue(field)
            : null;

    private static AlvoViolation? FailedFormat(FieldSchema field, object value, FormatCatalog formats) =>
        value is string text && !formats.Satisfies(field, text)
            ? PayloadViolations.Format(field)
            : null;

    /// <summary>
    /// Probes every queued reference and reports the ones that could not be resolved.
    /// </summary>
    /// <remarks>
    /// <b>Last, and only for values that already passed every other check</b> — each probe is a database
    /// round trip, so a payload that was going to be refused anyway must not pay for them.
    /// </remarks>
    private static async Task AddUnresolvedReferencesAsync(
        List<FieldSchema> references,
        RecordValidationRequest request,
        List<AlvoViolation> violations,
        CancellationToken cancellationToken)
    {
        foreach (var field in references)
        {
            if (!await IsResolvableAsync(field, request, cancellationToken).ConfigureAwait(false))
            {
                violations.Add(PayloadViolations.UnresolvedReference(field));
            }
        }
    }

    /// <summary>
    /// Whether the referenced row is one <b>this caller</b> can resolve.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Probed through the policy, as the caller, never as <see cref="AlvoContext.System"/>.</b>
    /// <see cref="IAlvoData.GetAsync"/> returns <see langword="null"/> both for a row that does not exist
    /// and for one the caller's <c>USING</c> predicate excludes, and that indistinguishability is the whole
    /// point: a probe run as the system would answer "this id exists" for a row in another tenant, turning a
    /// create endpoint into a cross-tenant existence oracle one request per candidate id.
    /// </para>
    /// <para>
    /// A refusal of the read <em>itself</em> is folded into the same answer for the same reason. A caller
    /// with no <c>get</c> policy on the target entity cannot resolve the row either, and letting that
    /// escape as a 403 would tell them apart from a caller whose predicate merely excluded it — a coarser
    /// version of the oracle above, and one that reports a refusal about a second entity the caller did not
    /// ask about.
    /// </para>
    /// </remarks>
    private static async Task<bool> IsResolvableAsync(
        FieldSchema field, RecordValidationRequest request, CancellationToken cancellationToken)
    {
        if (request.Values[field.Name] is not Guid id)
        {
            return true;
        }

        try
        {
            var row = await request.Data
                .GetAsync(field.Reference!.TargetEntity, id, request.Context, cancellationToken)
                .ConfigureAwait(false);
            return row is not null;
        }
        catch (AlvoAuthorizationException)
        {
            return false;
        }
    }
}

/// <summary>
/// Everything one payload's validation is measured against, bundled rather than threaded through eight
/// parameters that never vary within a request.
/// </summary>
/// <param name="Entity">The entity being written, as the applied schema declares it.</param>
/// <param name="Values">The bound field values the port would be called with.</param>
/// <param name="IsCreate">
/// Whether this is a create. A create must carry every required field; a partial update legitimately omits
/// the fields it is not changing — see <c>IAlvoData.UpdateAsync</c>'s contract.
/// </param>
/// <param name="ReadOnlyFields">
/// The fields this caller may read but not write, as their own policy decision resolved them — per-caller,
/// because <c>readOnly</c> may be a CEL expression over the caller's roles.
/// </param>
/// <param name="AlreadyReported">
/// The fields the body reader has already refused, so a value that never bound is not also measured against
/// the rules it could not reach.
/// </param>
/// <param name="Formats">The compiled formats of the applied schema.</param>
/// <param name="Data">The port every reference is probed through, so existence is answered under policy.</param>
/// <param name="Context">The caller every probe acts as.</param>
internal sealed record RecordValidationRequest(
    EntitySchema Entity,
    IReadOnlyDictionary<string, object?> Values,
    bool IsCreate,
    IReadOnlySet<string> ReadOnlyFields,
    IReadOnlySet<string> AlreadyReported,
    FormatCatalog Formats,
    IAlvoData Data,
    AlvoContext Context);
