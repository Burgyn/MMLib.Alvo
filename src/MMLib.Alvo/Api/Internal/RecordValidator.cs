using MMLib.Alvo.Data;
using MMLib.Alvo.Rules;
using MMLib.Alvo.Schema;
using System.Text;

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
            violations.Add(IsUnsatisfiableForThisCaller(field, request, supplied)
                ? PayloadViolations.ReadOnlyRequired(field)
                : PayloadViolations.Required(field));
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
        field.Required
        && (supplied ? value is null : request.IsCreate && !IsFilledInByTheStore(field));

    /// <summary>
    /// Whether the framework, not the caller, is the source of this field's value — a <c>computed</c>
    /// expression (a stored generated column) or a <c>rollup</c> the write path maintains.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>A create that omits one of these is not missing a value; it is behaving correctly.</b> The database
    /// fills the column in on the INSERT itself, so <c>NOT NULL</c> is satisfied without the caller writing
    /// anything — which is the same reason <c>DescriptorValidator</c> deliberately does <em>not</em> refuse
    /// <c>required</c> + <c>computed</c> at apply. Demanding the field would refuse a create that works.
    /// </para>
    /// <para>
    /// <b>The create branch only.</b> An explicit <see langword="null"/> is a write to a framework-maintained
    /// field, which is a different request from omitting it, and refusing it stays correct.
    /// </para>
    /// <para>
    /// <b>It also keeps <c>read-only-required-field</c> honest.</b> That violation is only reachable through
    /// this predicate, and it asserts an <em>impossibility</em> — so a field declaring <c>required</c>,
    /// <c>computed</c> and an expression-valued <c>readOnly</c> together would otherwise be refused with a
    /// confidently wrong message for a create that would have succeeded.
    /// </para>
    /// </remarks>
    /// <param name="field">The declared field.</param>
    private static bool IsFilledInByTheStore(FieldSchema field) =>
        field.ComputedExpression is not null || field.Rollup is not null;

    /// <summary>
    /// Whether the missing required value is one this caller could not have supplied — a create of a
    /// required field their own <c>readOnly</c> mask froze.
    /// </summary>
    /// <remarks>
    /// <para>
    /// It narrows an already-reported violation rather than adding a check, so it runs only on the path
    /// that was going to refuse anyway. The three conditions are exactly what makes the request
    /// unsatisfiable: a create (a partial update may leave the field alone), a value the caller did
    /// <em>not</em> send (one they did send is the ordinary <c>read-only-field</c> refusal above), and a
    /// mask that freezes it.
    /// </para>
    /// <para>
    /// A caller who sent an explicit <see langword="null"/> is deliberately not here: that is a write to a
    /// frozen field, already refused by <see cref="PayloadViolations.ReadOnly"/> before this runs.
    /// </para>
    /// </remarks>
    private static bool IsUnsatisfiableForThisCaller(
        FieldSchema field, RecordValidationRequest request, bool supplied) =>
        request.IsCreate && !supplied && request.ReadOnlyFields.Contains(field.Name);

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

    /// <summary>
    /// Whether the value overruns the field's declared <c>maxLength</c>, measured in <b>Unicode code
    /// points</b>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Code points, because that is the unit the column itself bounds.</b> PostgreSQL's
    /// <c>varchar(n)</c> — and SQL's <c>character_length</c> generally — counts characters in the SQL
    /// sense, which is code points. <see cref="string.Length"/> counts UTF-16 code units, so six
    /// astral-plane characters are twelve of those and a value well inside a <c>varchar(10)</c> was
    /// refused with a 422 telling the caller to shorten something already short enough.
    /// </para>
    /// <para>
    /// <b>Not grapheme clusters (<c>StringInfo</c>), and the difference is not cosmetic.</b> A family
    /// emoji is one grapheme cluster and seven code points, so counting clusters would admit a value
    /// seven times over its column's bound — an INSERT the engine refuses, which is the one direction the
    /// UTF-16 bug did <em>not</em> fail in. SQLite enforces no length at all and so cannot break the tie;
    /// it is broken by the engine that does enforce, because the bound has to be the tightest any
    /// registered driver applies.
    /// </para>
    /// <para>
    /// <b>The tie-break is "the tightest bound any registered driver applies", and that answer is
    /// engine-dependent — an obligation the third engine inherits.</b> Both drivers this build ships agree
    /// with code points: PostgreSQL's <c>varchar(n)</c> counts characters, and SQLite enforces no length at
    /// all. <b>T-SQL does not.</b> <c>nvarchar(n)</c> bounds UTF-16 units, so on Azure SQL — which §0
    /// principle 3 names as a production engine — ten astral characters would pass this check and fail the
    /// INSERT. The answer is the <b>dialect widening the column</b> so the store holds what the descriptor
    /// promises, never a per-dialect unit read here: <c>IAlvoSqlDialect</c> lives in the Entity Framework Core
    /// adapter, which the core must not reference, and a per-engine unit would make one descriptor mean
    /// different things per engine. It is not an <c>if</c> in this method either way. Tracked as <b>#175</b>,
    /// which is provable before a real driver exists — <c>TSqlSqlDialect</c> ships for exactly that, and
    /// <c>RowLockClause</c> is the precedent where the same exercise caught a silent defect.
    /// </para>
    /// <para>
    /// <b>The UTF-16 length is checked first, and that is a short circuit rather than a second rule.</b>
    /// A string never has more code points than code units, so one that already fits in code units
    /// cannot overrun in code points — the ordinary value keeps its O(1) answer and only a string that
    /// was going to be refused pays the walk.
    /// </para>
    /// </remarks>
    private static AlvoViolation? TooLong(FieldSchema field, object value) =>
        field.MaxLength is { } maxLength
        && value is string text
        && text.Length > maxLength
        && CodePointsIn(text) > maxLength
            ? PayloadViolations.MaxLength(field)
            : null;

    /// <summary>
    /// Counts <paramref name="text"/>'s Unicode code points.
    /// </summary>
    /// <remarks>
    /// An unpaired surrogate — which a JSON string may legally carry — enumerates as
    /// <see cref="Rune.ReplacementChar"/> and therefore counts as one code point, the same as the
    /// replacement character the engine would store for it.
    /// </remarks>
    private static int CodePointsIn(string text)
    {
        var count = 0;
        foreach (var _ in text.EnumerateRunes())
        {
            count++;
        }

        return count;
    }

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
    /// <para>
    /// Measured by rounding rather than by counting the digits of the literal, because
    /// <see cref="decimal"/> preserves trailing zeros: <c>1.230</c> has a scale of three and a value
    /// perfectly representable at a scale of two. Counting digits would refuse it, which is a refusal the
    /// caller cannot act on — their number <em>is</em> within the bound.
    /// </para>
    /// <para>
    /// <b>The <c>&lt;= 28</c> bound is a decision, not a typo</b> — stated because the sibling
    /// <see cref="ExceedsPrecision"/> carries the same clause with its reasoning and this one did not, so
    /// it read as one and the next reader would either "fix" it or copy it (#123). A
    /// <see cref="decimal"/> holds at most 28 fractional digits, so a value that bound at all cannot
    /// exceed a declared scale <em>above</em> 28: the comparison's answer is already known, and skipping
    /// it is what keeps <see cref="decimal.Round(decimal, int)"/> from throwing for a scale it cannot
    /// express. A declared <c>scale</c> over 28 is therefore <b>not</b> refused at apply — it is a legal
    /// <c>NUMERIC(38,30)</c> column, and every value this build can bind is checked against it correctly.
    /// </para>
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

    /// <summary>
    /// The format refusal for one value — <b>and the two of them are different refusals</b>.
    /// </summary>
    /// <remarks>
    /// A value that was matched and did not match is the caller's to fix; a match that did not finish inside
    /// <c>FormatCatalog.MatchTimeout</c> concluded nothing about the value, and telling that caller to correct
    /// it sends them to fix something that may well be valid. Both refuse the request, because an unevaluable
    /// check must fail closed, and they say different things.
    /// </remarks>
    private static AlvoViolation? FailedFormat(FieldSchema field, object value, FormatCatalog formats) =>
        value is not string text ? null : formats.Check(field, text) switch
        {
            FormatCatalog.FormatVerdict.Failed => PayloadViolations.Format(field),
            FormatCatalog.FormatVerdict.Undecided => PayloadViolations.FormatNotEvaluated(field),
            _ => null,
        };

    /// <summary>
    /// Probes every queued reference and reports the ones that could not be resolved.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Last, and only for the fields that passed every other check.</b> Each probe is a database round
    /// trip, so a field whose value was already refused never earns one.
    /// </para>
    /// <para>
    /// <b>A deviation from the plan's cost clause, recorded as one.</b> The plan says the probes "must not run
    /// for input that was going to fail anyway", which read strictly means skipping them entirely once any
    /// other field has a violation. They still run, and the reason is the promise this whole task exists to
    /// keep: suppressing a reference violation because a different field was also wrong is "every violation of
    /// the first kind", the exact defect a single unrecognised key once caused across the whole response. The
    /// cost of the deviation is bounded and not caller-controlled — one round trip per <em>declared</em> ref
    /// field, a number the descriptor author fixes, and only for values that were otherwise acceptable — and
    /// it is paid strictly after authorization, so an unauthorized caller never reaches it
    /// (<c>An_unauthorized_write_is_refused_before_its_body_is_validated</c>). If that trade ever needs
    /// revisiting, the answer is a bound on probes per request, not a silently dropped violation.
    /// </para>
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
