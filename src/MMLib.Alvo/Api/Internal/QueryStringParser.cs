using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Primitives;
using MMLib.Alvo.Data;
using MMLib.Alvo.Schema;
using System.Globalization;

namespace MMLib.Alvo.Api.Internal;

/// <summary>
/// One entry of a parsed projection: the response key, and the declared field its value comes from.
/// </summary>
/// <remarks>
/// The two are equal unless the caller wrote an alias. <b>Only <see cref="Source"/> reaches the port</b> —
/// <see cref="AlvoQuery.Select"/> carries declared field names, so the port's contract that these are the
/// entity's own names stays literally true and an alias never leaves the HTTP layer. That is also what
/// makes the port's own availability check meaningful: it compares declared names against declared names.
/// </remarks>
/// <param name="Key">The key this field answers under in the response.</param>
/// <param name="Source">The declared field the value is read from.</param>
internal sealed record ProjectedField(string Key, string Source);

/// <summary>
/// A parsed list request: the <see cref="AlvoQuery"/> the port serves, plus the response keys the API
/// renders the returned rows into.
/// </summary>
/// <remarks>
/// <b>The projection is on both, and they are not the same list.</b> <see cref="AlvoQuery.Select"/> carries
/// the declared fields the port must read; this carries the keys the response answers under, in the order
/// the request named them. They coincide exactly when no alias was used. The port cannot hold the second
/// list — an alias is an HTTP concern it is deliberately not told about — and the response cannot hold the
/// first, because the port returns framework-managed columns and sort keys the response must not show
/// unless the caller asked.
/// </remarks>
/// <param name="Query">The query to serve.</param>
/// <param name="Select">The response keys and their sources, or <see langword="null"/> for the row as the port returned it.</param>
internal sealed record ParsedListQuery(AlvoQuery Query, IReadOnlyList<ProjectedField>? Select);

/// <summary>
/// Parses a request's query string into an <see cref="AlvoQuery"/>, or into the violations that stopped it.
/// Every field name and operator is checked against the entity here, so nothing unvalidated reaches the
/// port — which validates again, deliberately.
/// </summary>
/// <remarks>
/// <para>
/// <b>An unrecognized key is refused, not ignored.</b> An ignored <c>oder=name</c> answers with unsorted data
/// and the agent that sent it has no way to notice; in this grammar every non-reserved key <em>is</em> a
/// filter on a field, so a mistyped keyword is refused as an unavailable field — the same refusal a masked
/// one earns, which is what keeps the refusal from confirming which fields exist.
/// </para>
/// <para>
/// <b>Every refusal is collected, not thrown at the first one.</b> §0 principle 4's reader is an agent
/// deciding what to change, and one violation per request is one round trip per mistake.
/// </para>
/// <para>
/// <b>The port's own guards are called, not re-implemented</b> —
/// <see cref="AlvoFilter.EnsureWithinLimits"/> and <see cref="AlvoQuery.EnsurePagingWindowIsSane"/>. They are
/// the rules of the port and they throw; here they are run early so a caller gets a structured violation with
/// a fix suggestion instead of the port's bare <c>ArgumentException</c> text, and so the same refusal happens
/// whether or not the API is in front.
/// </para>
/// </remarks>
internal static class QueryStringParser
{
    /// <summary>
    /// The longest opaque cursor this API will pass through to a provider.
    /// </summary>
    /// <remarks>
    /// A keyset cursor encodes the sort key's values for one row, so a few hundred characters is far past anything
    /// a page mints — and without a bound this is a caller-supplied string of arbitrary length handed to a
    /// provider's decoder on a path reachable <b>without authentication</b>. Kestrel's request-line limit caps it
    /// at some kilobytes in practice, which is a property of the transport rather than a decision this layer made.
    /// A cursor past this is refused as malformed, exactly as an empty one is.
    /// </remarks>
    /// <remarks>
    /// <b>Internal rather than private</b> so the generated OpenAPI document publishes the bound this parser
    /// enforces instead of a second copy of the number — a documented <c>maxLength</c> that drifted from the
    /// enforced one would refuse a cursor a page had just minted, or promise room for one it would not accept.
    /// </remarks>
    internal const int MaxCursorLength = 512;

    /// <summary>Parses <paramref name="query"/> for a list request over <paramref name="entity"/>.</summary>
    /// <param name="query">The request's query string.</param>
    /// <param name="entity">The entity being listed, as the applied schema declares it.</param>
    /// <param name="hiddenFields">The field mask the caller's policy resolved, so a masked field is refused like an undeclared one.</param>
    /// <param name="options">The API options the paging defaults and bounds come from.</param>
    /// <param name="parsed">The parsed request, when nothing refused it.</param>
    /// <param name="violations">Every reason the request was refused; empty on success.</param>
    internal static bool TryParse(
        IQueryCollection query,
        EntitySchema entity,
        IReadOnlySet<string> hiddenFields,
        AlvoApiOptions options,
        out ParsedListQuery? parsed,
        out IReadOnlyList<AlvoViolation> violations)
    {
        ArgumentNullException.ThrowIfNull(query);
        ArgumentNullException.ThrowIfNull(entity);
        ArgumentNullException.ThrowIfNull(hiddenFields);
        ArgumentNullException.ThrowIfNull(options);

        var pass = new ParsePass(entity, hiddenFields, options);
        var accepted = pass.TryRun(query, out parsed);
        violations = pass.Violations;
        return accepted;
    }

    /// <summary>
    /// One request's parse, as an object so each parameter's reader is a short named method over shared state
    /// rather than a branch inside one long function.
    /// </summary>
    private sealed class ParsePass(EntitySchema entity, IReadOnlySet<string> hiddenFields, AlvoApiOptions options)
    {
        private readonly FilterParseScope _scope = new(new QueryFieldResolver(entity, hiddenFields));
        private readonly List<AlvoFilter> _terms = [];
        private readonly List<AlvoViolation> _violations = [];
        private readonly HashSet<(string Code, string Pointer)> _reported = [];
        private IReadOnlyList<AlvoSort> _sort = [];
        private int? _limit;
        private int? _offset;
        private string? _after;
        private IReadOnlyList<ProjectedField>? _select;

        /// <summary>Each response key the projection has claimed, and the field it answers from.</summary>
        private Dictionary<string, string>? _claimedKeys;

        /// <summary>
        /// How many fields this caller can read — the projection's width bound, and the number its refusal
        /// names.
        /// </summary>
        /// <remarks>
        /// <b>The caller's count, not the entity's, and that is a confidentiality fix rather than a
        /// tightening.</b> Charging against every declared field would have told a caller who hit the bound
        /// how many fields the entity has, while an unprojected list tells them how many they can read — the
        /// difference being exactly the number of fields hidden from them. That is the bit the byte-identical
        /// <c>unavailable-field</c> refusal and the response schema's exclusion of masked fields both exist
        /// to withhold, and an alias makes it cheap to ask for: one readable field mints unlimited distinct
        /// keys. Every value this class puts in a message is server-owned; a count over masked fields would
        /// have been the one exception.
        /// </remarks>
        private int ReadableFieldCount =>
            _readableFieldCount ??= entity.Fields.Count(declared => !hiddenFields.Contains(declared.Name));

        private int? _readableFieldCount;

        internal IReadOnlyList<AlvoViolation> Violations => _violations;

        internal bool TryRun(IQueryCollection query, out ParsedListQuery? parsed)
        {
            foreach (var parameter in query)
            {
                Read(parameter.Key, parameter.Value);
            }

            return TryBuild(out parsed);
        }

        private void Read(string key, StringValues values)
        {
            if (IsSetting(key))
            {
                ReadSetting(key, values);
                return;
            }

            foreach (var value in values)
            {
                ReadFilter(key, value ?? string.Empty);
            }
        }

        /// <summary>
        /// Whether <paramref name="key"/> configures the read rather than filtering it. <c>or</c> and
        /// <c>and</c> are reserved but are <em>filters</em>, so they deliberately fall through to the filter
        /// grammar — which is also what lets one of them appear twice and simply conjoin.
        /// </summary>
        private static bool IsSetting(string key) => key is ReservedQueryKeys.Order or ReservedQueryKeys.Limit
            or ReservedQueryKeys.Offset or ReservedQueryKeys.After or ReservedQueryKeys.Select;

        private void ReadSetting(string key, StringValues values)
        {
            if (values.Count > 1)
            {
                Add(QueryViolations.RepeatedParameter(key));
                return;
            }

            var value = values[0] ?? string.Empty;
            switch (key)
            {
                case ReservedQueryKeys.Order: ReadOrder(value); break;
                case ReservedQueryKeys.Limit: ReadLimit(value); break;
                case ReservedQueryKeys.Offset: ReadOffset(value); break;
                case ReservedQueryKeys.After: ReadAfter(value); break;
                case ReservedQueryKeys.Select: ReadSelect(value); break;
                default:
                    throw new InvalidOperationException(
                    $"'{nameof(IsSetting)}' admitted a parameter this switch does not read.");
            }
        }

        /// <summary>
        /// Reads one filter parameter. The negation prefix is split off here and passed as a flag rather than
        /// re-encoded into member text, which is what keeps the top-level form unambiguous.
        /// </summary>
        private void ReadFilter(string key, string value)
        {
            var (negated, name) = FilterGroupParser.SplitNegation(key);

            if (!FilterGroupParser.TryParseNamed(name, value, negated, _scope, 1, out var filter, out var violation))
            {
                Add(violation!);
                return;
            }

            _terms.Add(filter!);
            ChargeTheConjunction();
        }

        /// <summary>
        /// Charges the node the top-level conjunction will occupy, the moment a <b>second</b> parameter makes it
        /// certain to exist.
        /// </summary>
        /// <remarks>
        /// <b>Charged on arrival rather than at build time, because a budget spent after the tree is assembled does
        /// not bound the tree.</b> Measured, not theorised: <c>?year=gte.1</c> repeated 256 times charged 256
        /// leaves, added the 257th node anyway, and left the port's own guard to answer — so a caller saw
        /// <c>filter-beyond-port-limits</c>, the code documented as unreachable, beside the
        /// <c>filter-too-wide</c> they actually needed. Charging here keeps the running total equal to the node
        /// count of the tree that will be produced, which is the whole of why that code is now unreachable.
        /// </remarks>
        private void ChargeTheConjunction()
        {
            if (_terms.Count == 2 && !_scope.TryChargeNode())
            {
                Add(QueryViolations.FilterTooWide());
            }
        }

        private void ReadOrder(string value)
        {
            if (SortParser.TryParse(value, _scope.Fields, out var sort, out var violation))
            {
                _sort = sort;
                return;
            }

            Add(violation!);
        }

        /// <summary>
        /// A page size past <see cref="AlvoApiOptions.MaxPageSize"/> is <b>refused, not clamped</b>: a client that
        /// asked for 1000 rows and silently received 200 computes its own paging arithmetic from a number no
        /// response ever told it about.
        /// </summary>
        /// <remarks>
        /// <b>A deliberate tightening of the port's own bound, recorded as one.</b>
        /// <see cref="AlvoQuery.EnsurePagingWindowIsSane"/> refuses a <em>negative</em> limit; this refuses zero
        /// and anything past the configured maximum as well. Zero is not a smaller page, it is a request that can
        /// never return a row — a silently disabled read rather than a configured limit — and the maximum is
        /// §2.1's requirement, which the port has no way to know. The port's guard still runs, so nothing is
        /// admitted here that it would refuse.
        /// </remarks>
        private void ReadLimit(string value)
        {
            if (TryReadWholeNumber(value, out var limit) && limit >= 1 && limit <= options.MaxPageSize)
            {
                _limit = limit;
                return;
            }

            Add(QueryViolations.InvalidPageSize(options.MaxPageSize));
        }

        /// <summary>
        /// An offset must be a whole number of zero or more rows.
        /// </summary>
        /// <remarks>
        /// <b>A deliberate restatement of the port's own bound, recorded as one</b> — the sibling of the
        /// <see cref="ReadLimit"/> tightening. <see cref="AlvoQuery.EnsurePagingWindowIsSane"/> refuses a negative
        /// offset too, and it still runs; this refuses the same thing earlier so a caller gets a structured
        /// violation naming the parameter instead of the port's bare <c>ArgumentOutOfRangeException</c> text. Unlike
        /// the <c>limit</c> case it adds no <em>new</em> bound: non-numeric text is the only condition here the
        /// port cannot see, because by the time it looks the value is already an <see cref="int"/>.
        /// </remarks>
        private void ReadOffset(string value)
        {
            if (TryReadWholeNumber(value, out var offset) && offset >= 0)
            {
                _offset = offset;
                return;
            }

            Add(QueryViolations.InvalidOffset());
        }

        /// <summary>
        /// The cursor is opaque and provider-owned, so it is echoed verbatim and never decoded here. A forged one
        /// is the provider's problem and already yields an empty page rather than a leak; the two things this can
        /// tell are that an <em>empty</em> string is not a cursor any page issued, and that one longer than any
        /// page could have minted is not either.
        /// </summary>
        private void ReadAfter(string value)
        {
            if (value.Length is 0 or > MaxCursorLength)
            {
                Add(QueryViolations.InvalidCursor(MaxCursorLength));
                return;
            }

            _after = value;
        }

        private void ReadSelect(string value)
        {
            if (value.Length == 0)
            {
                Add(QueryViolations.EmptySelect());
                return;
            }

            // A dictionary rather than a scan of the list: the claimed-key lookup runs once per comma-
            // separated entry, and the entry count is bounded only by the transport's URL length. The
            // width bound below caps the number of *distinct* keys, not the number of entries, so a
            // repeated entry that dedupes is free to arrive thousands of times — with a list scan each one
            // would have paid O(keys claimed so far), and the total would have been quadratic in a length
            // the caller chooses. Safe as per-parse state because 'select' twice is already refused as a
            // repeated parameter.
            _claimedKeys = new Dictionary<string, string>(StringComparer.Ordinal);

            var projected = new List<ProjectedField>();
            foreach (var entry in value.Split(','))
            {
                if (!TryAddProjectedField(entry, projected))
                {
                    return;
                }
            }

            _select = projected;
        }

        /// <summary>
        /// Reads one <c>field</c> or <c>alias:field</c> entry. The <em>source</em> is resolved through the
        /// same resolver every other field name goes through, which is what makes an alias unable to reach a
        /// field the caller may not read: the refusal is the one an undeclared name earns, byte for byte.
        /// </summary>
        private bool TryAddProjectedField(string entry, List<ProjectedField> projected)
        {
            if (!TrySplitProjectedField(entry, out var key, out var source))
            {
                Add(QueryViolations.MalformedSelectAlias());
                return false;
            }

            if (_scope.Fields.Resolve(source) is not { } declared)
            {
                Add(QueryViolations.UnavailableField(ReservedQueryKeys.Select));
                return false;
            }

            return TryClaimKey(key ?? declared.Name, declared.Name, projected);
        }

        /// <summary>
        /// Splits <c>alias:field</c>, or reports the whole entry as a field with no alias. An entry with
        /// more than one colon, an empty half, or an alias outside the field-name grammar is malformed.
        /// </summary>
        /// <param name="entry">The comma-separated entry as the caller wrote it.</param>
        /// <param name="key">The alias, or <see langword="null"/> when the entry carried none.</param>
        /// <param name="source">The field name half.</param>
        private static bool TrySplitProjectedField(string entry, out string? key, out string source)
        {
            key = null;
            source = entry;

            var colon = entry.IndexOf(':', StringComparison.Ordinal);
            if (colon < 0)
            {
                return entry.Length > 0;
            }

            if (entry.IndexOf(':', colon + 1) >= 0)
            {
                return false;
            }

            key = entry[..colon];
            source = entry[(colon + 1)..];
            return source.Length > 0 && IsAliasShaped(key);
        }

        /// <summary>
        /// Whether <paramref name="alias"/> is shaped like a declared field name
        /// (<c>^[a-z][a-z0-9_]{0,62}$</c>) and is not one of the reserved names.
        /// </summary>
        /// <remarks>
        /// The grammar is the schema's own (<c>project.schema.json</c>), checked here rather than borrowed
        /// as a regex: this is the only place a caller can put an arbitrary string into a response key, and
        /// the answer is a handful of character tests.
        /// </remarks>
        private static bool IsAliasShaped(string alias) =>
            alias.Length is > 0 and <= 63
            && char.IsAsciiLetterLower(alias[0])
            && alias.All(character =>
                char.IsAsciiLetterLower(character) || char.IsAsciiDigit(character) || character == '_')
            && !ReservedQueryKeys.IsReserved(alias);

        /// <summary>
        /// Claims one response key. A repeated identical entry dedupes; a second <em>source</em> for a key
        /// already taken is refused; and a newly claimed key past the entity's field count is refused as the
        /// projection's width bound.
        /// </summary>
        /// <remarks>
        /// <para>
        /// <b>The bound is charged here, on each newly claimed key, and that is the whole of whether it
        /// works.</b> <see cref="ChargeTheConjunction"/>'s remark records the measured incident from the
        /// filter side — a budget spent after the tree is assembled does not bound the tree — so a cap
        /// tested after the parse loop would leave the entire amplification payable before the 422.
        /// </para>
        /// <para>
        /// <b>Charged on the <em>distinct</em> key rather than the raw entry count</b>, which is what keeps
        /// a repeat deduping: a repeat claims nothing and therefore costs nothing, so
        /// <c>?select=id,id,id,id,id,id</c> on a five-field entity is still one key and still answered.
        /// </para>
        /// <para>
        /// An alias onto a framework-owned name is refused, and against <em>every</em> such name rather
        /// than the ones this entity carries — see <see cref="QueryViolations.CollidingProjectionKey"/> for
        /// why, and for what this deliberately does not refuse.
        /// </para>
        /// </remarks>
        private bool TryClaimKey(string key, string source, List<ProjectedField> projected)
        {
            if (_claimedKeys!.TryGetValue(key, out var claimed))
            {
                if (string.Equals(claimed, source, StringComparison.Ordinal))
                {
                    return true;
                }

                Add(QueryViolations.CollidingProjectionKey());
                return false;
            }

            if (!string.Equals(key, source, StringComparison.Ordinal) && AlvoManagedColumns.All.Contains(key))
            {
                Add(QueryViolations.CollidingProjectionKey());
                return false;
            }

            if (projected.Count == ReadableFieldCount)
            {
                Add(QueryViolations.ProjectionTooWide(ReadableFieldCount));
                return false;
            }

            _claimedKeys[key] = source;
            projected.Add(new ProjectedField(key, source));
            return true;
        }

        /// <summary>
        /// Whole numbers only, invariant, with no thousands separator or exponent: a query string is a wire
        /// format rather than a locale, and <c>1,000</c> is a candidate list in this grammar.
        /// </summary>
        private static bool TryReadWholeNumber(string value, out int number) =>
            int.TryParse(value, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out number);

        private bool TryBuild(out ParsedListQuery? parsed)
        {
            parsed = null;
            var query = new AlvoQuery
            {
                Entity = entity.Name,
                Filter = Conjoin(),
                Sort = _sort,
                Limit = _limit ?? options.DefaultPageSize,
                Offset = _offset,
                After = _after,

                // Sources only, deduped: the port is asked for each declared field once, however many
                // response keys the caller aliased onto it.
                Select = _select?.Select(field => field.Source).Distinct(StringComparer.Ordinal).ToList(),
            };

            EnsureWithinPortRules(query);
            if (_violations.Count > 0)
            {
                return false;
            }

            parsed = new ParsedListQuery(query, _select);
            return true;
        }

        /// <summary>
        /// Several top-level parameters are one conjunction — PostgREST's own semantics, and the only reading
        /// that keeps a filter narrowing: an implicit <c>OR</c> between two parameters would widen the set as
        /// the caller added terms.
        /// </summary>
        /// <remarks>
        /// Its node was already charged by <see cref="ChargeTheConjunction"/> when the second parameter arrived,
        /// and the level it occupies is one of the two <see cref="FilterGroupParser.MaxNesting"/> reserves — so
        /// this only assembles.
        /// </remarks>
        private AlvoFilter? Conjoin() => _terms.Count switch
        {
            0 => null,
            1 => _terms[0],
            _ => new AlvoAnd(_terms),
        };

        /// <summary>
        /// Runs the port's own rules over what this parse produced — both of them, unconditionally.
        /// </summary>
        /// <remarks>
        /// <b><see cref="AlvoFilter.EnsureWithinLimits"/> runs even when this parse has already refused, and that
        /// is deliberate.</b> An earlier version skipped it in that case, reasoning that a parser which had already
        /// found the overflow should not also report the belt's "the API's accounting is broken" code. It changes
        /// no outcome — the request is refused either way — and its only real effect is to <em>suppress</em>
        /// <see cref="QueryViolations.FilterBeyondPortLimits"/> whenever any other violation exists. That is
        /// precisely the case in which the belt is carrying information: under the accounting defect it was written
        /// alongside, the tree stayed unbounded and the guard hid the symptom. A defensive control that can only
        /// ever conceal a real defect is worse than no control, and it blinds the suite-wide screen that now
        /// asserts the belt code reaches no response body.
        /// </remarks>
        private void EnsureWithinPortRules(AlvoQuery query)
        {
            Record(() => AlvoQuery.EnsurePagingWindowIsSane(query), QueryViolations.ConflictingPagingWindow);
            Record(() => AlvoFilter.EnsureWithinLimits(query.Filter), QueryViolations.FilterBeyondPortLimits);
        }

        /// <summary>
        /// Runs one of the port's guards and records its refusal as a violation, in the port's own wording with
        /// the .NET argument machinery stripped off it.
        /// </summary>
        private void Record(Action guard, Func<string, AlvoViolation> asViolation)
        {
            try
            {
                guard();
            }
            catch (ArgumentException exception)
            {
                // Sanitized by ProblemResultFactory, which is the one authority on what a caller is shown:
                // the same stripping is owed to every other ArgumentException the port can raise, and a second
                // copy here is how the two come to disagree about what an internal detail is.
                Add(asViolation(ProblemResultFactory.WithoutArgumentDetail(exception.Message)));
            }
        }

        /// <summary>
        /// Records one refusal — <b>once per distinct <c>(code, pointer)</c></b>.
        /// </summary>
        /// <remarks>
        /// <para>
        /// <b>De-duplicating is what bounds the response, and it replaces a global cap that flooding defeated.</b>
        /// Three hundred bad parameters used to fill a twenty-entry allowance with one repeated
        /// <c>filter-too-wide</c>, so a <c>limit</c> and an <c>order</c> mistake in the same request were never
        /// reported at all — #19's definition of done and §2.1 both require <em>every</em> violation, and a
        /// response that repeats one kind twenty times while silently dropping two others satisfies the letter and
        /// defeats the purpose. One per kind needs no numeric cap: the code catalogue and the pointer set are both
        /// small and fixed, so the list is bounded by construction and every distinct problem survives.
        /// </para>
        /// <para>
        /// The message and fix suggestion are deliberately <em>not</em> part of the identity: both are derived from
        /// the code plus server-owned values, so including them could only ever split one kind into several. And
        /// nothing is lost by collapsing — a pointer here names a parameter's <em>role</em>, never a field, so two
        /// bad filter values were already reported identically.
        /// </para>
        /// </remarks>
        private void Add(AlvoViolation violation)
        {
            if (_reported.Add((violation.Code, violation.Pointer)))
            {
                _violations.Add(violation);
            }
        }
    }
}
