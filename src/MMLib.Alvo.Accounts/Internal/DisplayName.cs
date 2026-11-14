using Microsoft.AspNetCore.Http;
using MMLib.Alvo.Api;
using MMLib.Alvo.Api.Internal;
using System.Globalization;
using System.Text.Json;

namespace MMLib.Alvo.Accounts.Internal;

/// <summary>
/// Reads the body of <c>PATCH {prefix}/user</c>: <c>{ "name": string }</c>, trimmed, 1–<see cref="MaxLength"/> characters,
/// no control character — and nothing else, because the display name is the only thing a person changes there (S2 D8).
/// </summary>
/// <remarks>
/// <para>
/// <b>The body is read under the Data API's own bounds</b> (<see cref="BoundedJsonBody"/>: size, depth, member count,
/// duplicate names), and its refusals carry the Data API's codes, so a client handles one vocabulary.
/// </para>
/// <para>
/// <b>Length is counted in Unicode code points</b>, the unit the Data API's <c>max-length</c> counts in. A control
/// character (C0, DEL, C1) is refused rather than stripped: a name that renders differently from what was sent is not the
/// name the person chose. No member but <c>name</c> is accepted — an ignored <c>email</c> would look like a change that
/// happened.
/// </para>
/// </remarks>
internal static class DisplayName
{
    /// <summary>The longest display name, in Unicode code points.</summary>
    internal const int MaxLength = 100;

    private const string Member = "name";

    private static readonly string _pointer = PayloadViolations.PointerTo(Member);

    /// <summary>The trimmed name the request carries, or every reason it carries none.</summary>
    /// <param name="request">The request, its body not yet read.</param>
    /// <param name="options">The Data API's body bounds.</param>
    /// <param name="ct">A token to cancel the read.</param>
    internal static async Task<(string? Name, IReadOnlyList<AlvoViolation> Violations)> ReadAsync(
        HttpRequest request, AlvoApiOptions options, CancellationToken ct)
    {
        using var body = new MemoryStream();
        if (await BoundedJsonBody.ReadAsync(request, body, options, ct).ConfigureAwait(false) is { } refused)
        {
            return (null, [PayloadViolations.Body(refused, options)]);
        }

        using var document = JsonDocument.Parse(body.GetBuffer().AsMemory(0, (int)body.Length));
        return Validate(document.RootElement);
    }

    private static (string? Name, IReadOnlyList<AlvoViolation> Violations) Validate(JsonElement root)
    {
        var unknown = root.EnumerateObject()
            .Where(member => !member.NameEquals(Member))
            .Select(member => PayloadViolations.UnknownField(member.Name))
            .ToList();
        var (name, violation) = root.TryGetProperty(Member, out var value) ? Judge(value) : (null, Required());
        if (violation is not null)
        {
            unknown.Add(violation);
        }

        return unknown.Count == 0 ? (name, []) : (null, unknown);
    }

    private static (string? Name, AlvoViolation? Violation) Judge(JsonElement value)
    {
        if (value.ValueKind != JsonValueKind.String)
        {
            return (null, value.ValueKind == JsonValueKind.Null ? Required() : NotText());
        }

        var name = value.GetString()!.Trim();
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
        "A display name is required, and one made only of whitespace is none.",
        "Send { \"name\": \"…\" } with at least one visible character.");

    private static AlvoViolation NotText() => new(
        _pointer,
        "invalid-value",
        "A display name is a JSON string with no control character in it.",
        "Send the name as a quoted string, without tabs, line breaks or other control characters.");

    private static AlvoViolation TooLong() => new(
        _pointer,
        "max-length",
        string.Create(CultureInfo.InvariantCulture, $"A display name is longer than {MaxLength} characters."),
        string.Create(
            CultureInfo.InvariantCulture,
            $"Shorten it to at most {MaxLength} characters, counted in Unicode code points after trimming."));
}
