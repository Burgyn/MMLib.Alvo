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

        return Transpose(document.RootElement);
    }

    /// <summary>Turns the body's members into parameters, or into every reason one of them is not a value.</summary>
    private static Result Transpose(JsonElement root)
    {
        var parameters = new Dictionary<string, StringValues>(StringComparer.OrdinalIgnoreCase);
        var violations = new List<AlvoViolation>();
        foreach (var member in root.EnumerateObject())
        {
            Read(member, parameters, violations);
        }

        return violations.Count > 0
            ? new Result(null, violations)
            : new Result(new QueryCollection(parameters), []);
    }

    /// <summary>Reads one member — a single value, or an array standing for a repeated parameter.</summary>
    /// <remarks>
    /// An <em>empty</em> array is refused rather than read as an absent parameter. A caller who wrote one
    /// sent a parameter that does nothing, and this grammar refuses a parameter it cannot act on rather than
    /// ignoring it — the same rule that makes a mistyped <c>oder=name</c> a refusal instead of an unsorted
    /// page.
    /// </remarks>
    private static void Read(
        JsonProperty member, Dictionary<string, StringValues> parameters, List<AlvoViolation> violations)
    {
        if (member.Value.ValueKind != JsonValueKind.Array)
        {
            Append(member.Name, member.Value, parameters, violations);
            return;
        }

        if (member.Value.GetArrayLength() == 0)
        {
            violations.Add(QueryViolations.UnrepresentableQueryValue(RoleOf(member.Name)));
            return;
        }

        foreach (var element in member.Value.EnumerateArray())
        {
            Append(member.Name, element, parameters, violations);
        }
    }

    /// <summary>Adds one value to a parameter, accumulating a repeat the way a query string accumulates one.</summary>
    private static void Append(
        string name,
        JsonElement value,
        Dictionary<string, StringValues> parameters,
        List<AlvoViolation> violations)
    {
        if (Scalar(value) is not { } text)
        {
            violations.Add(QueryViolations.UnrepresentableQueryValue(RoleOf(name)));
            return;
        }

        parameters[name] = parameters.TryGetValue(name, out var existing)
            ? StringValues.Concat(existing, text)
            : new StringValues(text);
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
    /// The same roles <see cref="QueryViolations"/> uses, and for its reason: in PostgREST's grammar a
    /// filter's parameter name <em>is</em> a field name, so a pointer carrying it would answer "does this
    /// entity have a field called X" for exactly the caller most likely to be asking. <c>or</c>, <c>and</c>
    /// and <c>not</c> are reserved and still point at <c>filter</c>, because they are filters.
    /// </remarks>
    private static string RoleOf(string name) => name switch
    {
        ReservedQueryKeys.Order or ReservedQueryKeys.Limit or ReservedQueryKeys.Offset
            or ReservedQueryKeys.After or ReservedQueryKeys.Select => name,
        _ => QueryViolations.FilterPointer,
    };
}
