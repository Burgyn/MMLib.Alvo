using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Primitives;
using MMLib.Alvo.Data;
using MMLib.Alvo.Schema;
using System.Globalization;

namespace MMLib.Alvo.Api.Internal;

/// <summary>
/// A parsed list request: the <see cref="AlvoQuery"/> the port serves, plus the projection the API applies
/// to the rows it returns.
/// </summary>
/// <remarks>
/// <b>The projection is deliberately not on <see cref="AlvoQuery"/>.</b> That record invites additive
/// members, and a future <c>Select</c> on it is the right long-term home — but only once a provider
/// <em>honours</em> it. Adding it now would publish a port member both shipped drivers and the in-memory
/// reference silently ignore, so a caller reaching the port directly would ask for two fields and receive
/// every one, with nothing raised. Until the drivers push a projection into their <c>SELECT</c> list, the
/// projection is an HTTP-response concern and lives here; the follow-up is named in the deferred-work list.
/// </remarks>
/// <param name="Query">The query to serve.</param>
/// <param name="Select">The fields to project, or <see langword="null"/> for every field the port returns.</param>
internal sealed record ParsedListQuery(AlvoQuery Query, IReadOnlyList<string>? Select);

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
/// <see cref="AlvoFilter.EnsureWithinLimits"/>, <see cref="AlvoQuery.EnsurePagingWindowIsSane"/> and
/// <see cref="AlvoQuery.EnsureSortKeysCanBePaged"/>. They are the rules of the port and they throw; here they
/// are run early so a caller gets a structured violation with a fix suggestion instead of the port's bare
/// <c>ArgumentException</c> text, and so the same refusal happens whether or not the API is in front.
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
    private const int MaxCursorLength = 512;

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
        private IReadOnlyList<string>? _select;

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

            var projected = new List<string>();
            foreach (var name in value.Split(','))
            {
                if (_scope.Fields.Resolve(name) is not { } declared)
                {
                    Add(QueryViolations.UnavailableField(ReservedQueryKeys.Select));
                    return;
                }

                AddOnce(projected, declared.Name);
            }

            _select = projected;
        }

        private static void AddOnce(List<string> projected, string field)
        {
            if (!projected.Contains(field, StringComparer.Ordinal))
            {
                projected.Add(field);
            }
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
        /// Runs the port's own rules over what this parse produced — all three, unconditionally.
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
            Record(() => AlvoQuery.EnsureSortKeysCanBePaged(query, entity), QueryViolations.UnpageableSortKey);
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
                Add(asViolation(WithoutArgumentDetail(exception.Message)));
            }
        }

        /// <summary>
        /// <see cref="ArgumentException.Message"/> with everything <see cref="ArgumentException"/> itself appends
        /// removed — the <c>(Parameter '…')</c> suffix and, for a range exception, the <c>Actual value was …</c>
        /// line after it.
        /// </summary>
        /// <remarks>
        /// The suffix is appended on the <b>same line</b>, separated by a space, so an earlier version of this
        /// that only cut at the first newline stripped nothing at all and shipped <c>(Parameter 'query')</c> in
        /// 422 bodies. An internal argument name is an implementation detail of the guard, not part of the
        /// contract an agent reads; <c>AlvoApiWorld</c> now screens every response in the suite for it, so the
        /// claim is asserted rather than described.
        /// </remarks>
        private static string WithoutArgumentDetail(string message)
        {
            var appended = message.IndexOf(ArgumentNameSuffix, StringComparison.Ordinal);
            var text = appended < 0 ? message : message[..appended];
            var newline = text.IndexOf('\n');
            return (newline < 0 ? text : text[..newline]).TrimEnd();
        }

        /// <summary>How <see cref="ArgumentException"/> introduces the argument name it appends to a message.</summary>
        private const string ArgumentNameSuffix = " (Parameter '";

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
