using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Primitives;
using System.Text.Json;

namespace MMLib.Alvo.Api.Internal;

/// <summary>
/// Reads a <c>POST …/query</c> body — a JSON object whose members <b>are</b> the query-string parameters —
/// into the collection <see cref="QueryStringParser"/> takes, or into the violations that stopped it.
/// </summary>
/// <remarks>
/// <para>
/// <b>It transposes; it never interprets.</b> No field name is resolved, no operator is recognised and no
/// bound of the grammar's is applied here — all of that is the one parser, reached unmodified. A second
/// grammar for the body is how the two surfaces come to disagree, and this type exists precisely so there
/// is not one.
/// </para>
/// <para>
/// <b>Nothing is percent-decoded, so the two surfaces are equal on <em>values</em> rather than on bytes.</b>
/// ASP.NET Core hands the parser query values it has already decoded, and a JSON string is already decoded —
/// so the body carries the operand and the query string carries its encoding. Three consequences follow and
/// all are intended: <c>+</c> is a space in a query string and a plus here; <c>%25</c> is an escape there
/// and a literal here; and a caller assembling a four-hundred-element <c>in</c> list — the request this
/// endpoint exists for — escapes nothing.
/// </para>
/// <para>
/// <b>Keys are compared as <see cref="QueryCollection"/> compares them</b>, ordinal-ignoring-case, and
/// repeated names accumulate exactly as <c>QueryHelpers.ParseQuery</c> accumulates them. So
/// <c>{"limit":1,"LIMIT":2}</c> is one parameter carrying two values and earns the same
/// <c>repeated-parameter</c> the query string earns — where an ordinal comparer would have made one request
/// answer two different refusals depending on which side it arrived on, which is the single divergence this
/// transposition could have introduced.
/// </para>
/// <para>
/// <b>Everything about the body's size and shape is decided before this type sees it</b>, by
/// <see cref="BoundedJsonBody"/> and under the same three payload bounds a write is read under. The document
/// is therefore known to be a bounded JSON object with no duplicate names by the time it is parsed here,
/// which is why the parse cannot fail — a relationship <see cref="BoundedJsonBody"/>'s own remarks record as
/// measured rather than assumed.
/// </para>
/// </remarks>
internal static class QueryBodyReader
{
    /// <summary>What one query body produced.</summary>
    /// <param name="Parameters">The parameters to parse, or <see langword="null"/> when something refused the body.</param>
    /// <param name="Violations">Every reason the body was refused; empty on success.</param>
    internal sealed record Result(IQueryCollection? Parameters, IReadOnlyList<AlvoViolation> Violations);

    /// <summary>Reads the request body into the parameters a list query is parsed from.</summary>
    /// <param name="request">The request whose body to read.</param>
    /// <param name="options">The payload bounds to enforce.</param>
    /// <param name="cancellationToken">A token to cancel the read.</param>
    internal static async Task<Result> ReadAsync(
        HttpRequest request, AlvoApiOptions options, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(options);

        using var body = new MemoryStream();
        var refusal = await BoundedJsonBody
            .ReadAsync(request, body, options, cancellationToken).ConfigureAwait(false);
        if (refusal is { } refused)
        {
            return new Result(null, [QueryViolations.Body(refused, options)]);
        }

        body.Position = 0;
        using var document = JsonDocument.Parse(
            body, new JsonDocumentOptions { MaxDepth = options.MaxPayloadDepth });

        return new TranspositionPass(options).Run(document.RootElement);
    }

    /// <summary>
    /// One body's transposition, as an object so the running value count, the parameters and the
    /// de-duplicated violations are one piece of state rather than four <c>ref</c> parameters — the shape
    /// <c>QueryStringParser.ParsePass</c> already uses for the same reason.
    /// </summary>
    /// <param name="options">The bounds this pass enforces.</param>
    private sealed class TranspositionPass(AlvoApiOptions options)
    {
        private readonly Dictionary<string, List<string?>> _parameters = new(StringComparer.OrdinalIgnoreCase);
        private readonly List<AlvoViolation> _violations = [];
        private readonly HashSet<(string Code, string Pointer)> _reported = [];
        private int _values;

        /// <summary>Turns the body's members into parameters, or into every reason one of them is not a value.</summary>
        internal Result Run(JsonElement root)
        {
            foreach (var member in root.EnumerateObject())
            {
                if (!Read(member))
                {
                    break;
                }
            }

            return _violations.Count > 0
                ? new Result(null, _violations)
                : new Result(new QueryCollection(Collected()), []);
        }

        /// <summary>Each parameter's values, built once rather than grown one value at a time.</summary>
        /// <remarks>
        /// <b><c>StringValues.Concat</c> copies</b>, so appending N values one
        /// by one is quadratic in N — and N is the length of an array the caller chose. Accumulating in a
        /// list and converting once is linear, and the bound below is what keeps N itself finite.
        /// </remarks>
        private Dictionary<string, StringValues> Collected()
        {
            var collected = new Dictionary<string, StringValues>(_parameters.Count, StringComparer.OrdinalIgnoreCase);
            foreach (var (name, values) in _parameters)
            {
                collected[name] = new StringValues([.. values]);
            }

            return collected;
        }

        /// <summary>
        /// Reads one member — a single value, or an array standing for a repeated parameter — answering
        /// whether the pass should continue.
        /// </summary>
        /// <remarks>
        /// An <em>empty</em> array is refused rather than read as an absent parameter. A caller who wrote one
        /// sent a parameter that does nothing, and this grammar refuses a parameter it cannot act on rather
        /// than ignoring it — the same rule that makes a mistyped <c>oder=name</c> a refusal instead of an
        /// unsorted page.
        /// </remarks>
        private bool Read(JsonProperty member)
        {
            if (member.Value.ValueKind != JsonValueKind.Array)
            {
                return Append(member.Name, member.Value);
            }

            if (member.Value.GetArrayLength() == 0)
            {
                Add(QueryViolations.UnrepresentableQueryValue(RoleOf(member.Name)));
                return true;
            }

            foreach (var element in member.Value.EnumerateArray())
            {
                if (!Append(member.Name, element))
                {
                    return false;
                }
            }

            return true;
        }

        /// <summary>
        /// Adds one value to a parameter, answering <see langword="false"/> once the body has carried more
        /// values than this API reads.
        /// </summary>
        /// <remarks>
        /// <b>The bound counts <em>values</em>, and it has to, because nothing above it does.</b>
        /// <see cref="BoundedJsonBody"/>'s key bound counts property names at every depth — an array's
        /// <em>elements</em> are not property names, so <c>{"or":[…500 000 strings…]}</c> is one key, passes
        /// every shape bound, and fits inside <see cref="AlvoApiOptions.MaxRequestBodyBytes"/>. The parser
        /// below would refuse the 257th of them, but only after this method had built all 500 000. This is
        /// the same "a budget spent after the work does not bound the work" rule the filter's node budget and
        /// the projection's entry bound both follow.
        /// </remarks>
        private bool Append(string name, JsonElement value)
        {
            if (++_values > options.MaxPayloadKeys)
            {
                Add(QueryViolations.TooManyQueryValues(options.MaxPayloadKeys));
                return false;
            }

            if (Scalar(value) is not { } text)
            {
                Add(QueryViolations.UnrepresentableQueryValue(RoleOf(name)));
                return true;
            }

            if (!_parameters.TryGetValue(name, out var values))
            {
                _parameters[name] = values = [];
            }

            values.Add(text);
            return true;
        }

        /// <summary>
        /// Records one refusal — <b>once per distinct <c>(code, pointer)</c></b>, exactly as
        /// <c>QueryStringParser</c> does and for the same reason: an array of ten thousand nulls is one
        /// mistake, and reporting it ten thousand times fills the response with one kind while telling the
        /// caller nothing they did not know from the first.
        /// </summary>
        private void Add(AlvoViolation violation)
        {
            if (_reported.Add((violation.Code, violation.Pointer)))
            {
                _violations.Add(violation);
            }
        }
    }

    /// <summary>
    /// The text a query string would have carried for this value, or <see langword="null"/> when it carries
    /// none.
    /// </summary>
    /// <remarks>
    /// A number contributes <see cref="JsonElement.GetRawText"/> — the literal the caller wrote — rather than
    /// a re-rendered CLR value. Round-tripping through <see cref="decimal"/> or <see cref="double"/> would
    /// put a formatting decision (a culture, an exponent, a trailing zero) between the two surfaces, and the
    /// parser reads every value as text anyway.
    /// </remarks>
    private static string? Scalar(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.String => value.GetString(),
        JsonValueKind.Number or JsonValueKind.True or JsonValueKind.False => value.GetRawText(),
        _ => null,
    };

    /// <summary>
    /// The role a refusal about this parameter points at: the reserved parameter's own name, or
    /// <c>filter</c> for everything else.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The same roles <see cref="QueryViolations"/> uses, and for its reason: in PostgREST's grammar a
    /// filter's parameter name <em>is</em> a field name, so a pointer carrying it would answer "does this
    /// entity have a field called X" for exactly the caller most likely to be asking. <c>or</c>, <c>and</c>
    /// and <c>not</c> are reserved and still point at <c>filter</c>, because they are filters.
    /// </para>
    /// <para>
    /// <b>The comparison is ordinal, and deliberately not the <see cref="StringComparer.OrdinalIgnoreCase"/>
    /// the parameter dictionary uses.</b> The two answer different questions: the dictionary decides which
    /// values <em>merge</em> into one parameter, while this decides which role a refusal names — and the
    /// authority on that is <c>QueryStringParser.IsSetting</c>, whose constant patterns are ordinal. A key
    /// spelled <c>LIMIT</c> that reaches the parser alone is read as a filter on a field of that name, so
    /// <c>filter</c> is the role its refusal really carries; answering <c>limit</c> here would name a role
    /// the parser would not have used.
    /// </para>
    /// </remarks>
    private static string RoleOf(string name) => name switch
    {
        ReservedQueryKeys.Order or ReservedQueryKeys.Limit or ReservedQueryKeys.Offset
            or ReservedQueryKeys.After or ReservedQueryKeys.Select => name,
        _ => QueryViolations.FilterPointer,
    };
}
