using Microsoft.AspNetCore.Http;
using MMLib.Alvo.Api;
using MMLib.Alvo.Api.Internal;
using System.Globalization;
using System.Text.Json;

namespace MMLib.Alvo.Tenants.SelfService;

/// <summary>
/// Reads the body of <c>POST {prefix}/tenants</c> and <c>PATCH {prefix}/tenants/{id}</c>: <c>{ "name": string }</c>,
/// trimmed, 1–<see cref="MaxLength"/> Unicode code points, no control character, no half of a surrogate pair — and nothing
/// else.
/// </summary>
/// <remarks>
/// The rules are the display name's (S2 D8), for the same reasons: length in code points, the unit the Data API's
/// <c>max-length</c> counts in; a control character refused rather than stripped, because a name that renders differently
/// from what was sent is not the name chosen; an unknown member refused, because an ignored one looks like a change that
/// happened.
/// </remarks>
internal static class TenantName
{
    /// <summary>The longest tenant name, in Unicode code points.</summary>
    internal const int MaxLength = 100;

    private const string Member = "name";

    private static readonly string _pointer = PayloadViolations.PointerTo(Member);

    /// <summary>The trimmed name the request carries, or every reason it carries none.</summary>
    /// <param name="request">The request, its body not yet read.</param>
    /// <param name="options">The Data API's body bounds.</param>
    /// <param name="ct">A token to cancel the read.</param>
    internal static Task<(string? Name, IReadOnlyList<AlvoViolation> Violations)> ReadAsync(
        HttpRequest request, AlvoApiOptions options, CancellationToken ct) =>
        SelfServiceBody.ReadAsync<string>(request, options, Validate, ct);

    private static (string? Name, IReadOnlyList<AlvoViolation> Violations) Validate(JsonElement root)
    {
        var violations = SelfServiceBody.Unknown(root, Member);
        var (name, violation) = root.TryGetProperty(Member, out var value) ? Judge(value) : (null, Required());
        if (violation is not null)
        {
            violations.Add(violation);
        }

        return violations.Count == 0 ? (name, []) : (null, violations);
    }

    private static (string? Name, AlvoViolation? Violation) Judge(JsonElement value)
    {
        if (value.ValueKind != JsonValueKind.String)
        {
            return (null, value.ValueKind == JsonValueKind.Null ? Required() : NotText());
        }

        if (SelfServiceBody.TextOf(value) is not { } text)
        {
            return (null, NotText());
        }

        var name = text.Trim();
        return name switch
        {
            { Length: 0 } => (null, Required()),
            _ when name.Any(char.IsControl) => (null, NotText()),
            _ when name.EnumerateRunes().Count() > MaxLength => (null, TooLong()),
            _ => (name, null),
        };
    }

    private static AlvoViolation Required() => new(
        _pointer,
        "required",
        "A tenant name is required, and one made only of whitespace is none.",
        "Send { \"name\": \"…\" } with at least one visible character.");

    private static AlvoViolation NotText() => new(
        _pointer,
        "invalid-value",
        "A tenant name is a JSON string with no control character in it.",
        "Send the name as a quoted string, without tabs, line breaks or other control characters.");

    private static AlvoViolation TooLong() => new(
        _pointer,
        "max-length",
        string.Create(CultureInfo.InvariantCulture, $"A tenant name is longer than {MaxLength} characters."),
        string.Create(
            CultureInfo.InvariantCulture,
            $"Shorten it to at most {MaxLength} characters, counted in Unicode code points after trimming."));
}
