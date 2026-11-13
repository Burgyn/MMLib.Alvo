using MMLib.Alvo.Api;

namespace MMLib.Alvo.Samples.FieldService;

/// <summary>
/// The refusals this host's own routes answer, in Alvo's problem shape — the same <c>type</c> URIs and the same
/// <c>violations</c> array (<c>pointer</c>, <c>code</c>, <c>message</c>) a client already reads from the Data API.
/// </summary>
internal static class FieldServiceProblems
{
    public const string Required = "required";

    public const string OutOfRange = "out-of-range";

    /// <summary>422 <c>validation</c>, naming each field to fix.</summary>
    public static IResult Invalid(IReadOnlyList<AlvoViolation> violations) => TypedResults.Problem(
        title: "The request is not valid.",
        statusCode: StatusCodes.Status422UnprocessableEntity,
        type: AlvoProblemTypes.UriOf(AlvoProblemTypes.Validation),
        extensions: new Dictionary<string, object?> { ["violations"] = violations });

    /// <summary>409 <c>conflict</c>: the request is valid, the row's state does not admit it.</summary>
    public static IResult Conflict(string detail) => TypedResults.Problem(
        detail: detail,
        statusCode: StatusCodes.Status409Conflict,
        type: AlvoProblemTypes.UriOf(AlvoProblemTypes.Conflict));

    public static AlvoViolation Missing(string pointer) =>
        new(pointer, Required, $"'{pointer.TrimStart('/')}' is required.", FixSuggestion: null);

    public static AlvoViolation Range(string pointer, string message) =>
        new(pointer, OutOfRange, message, FixSuggestion: null);
}
