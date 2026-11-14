using Microsoft.AspNetCore.Http;
using MMLib.Alvo.Api;
using MMLib.Alvo.Api.Internal;
using System.Globalization;
using System.Text.Json;

namespace MMLib.Alvo.Tenants.SelfService;

/// <summary>
/// A name a person types on a self-service route — their own display name (<c>PATCH {prefix}/user</c>, S2 D8) or a
/// tenant's (<c>POST</c> and <c>PATCH {prefix}/tenants</c>): a body of exactly <c>{ "name": string }</c>, trimmed,
/// 1–<see cref="MaxLength"/> Unicode code points, no control character, no half of a surrogate pair.
/// </summary>
/// <remarks>
/// <para>
/// <b>One set of rules for every name a person types</b>, so a fix to one (a character class refused, a length changed)
/// cannot miss the other; only the noun in the wording differs.
/// </para>
/// <para>
/// <b>Length is counted in Unicode code points</b>, the unit the Data API's <c>max-length</c> counts in. A control
/// character (C0, DEL, C1) is refused rather than stripped: a name that renders differently from what was sent is not the
/// name chosen. No member but <c>name</c> is accepted — an ignored one would look like a change that happened.
/// </para>
/// </remarks>
internal sealed class SelfServiceName
{
    /// <summary>The longest name, in Unicode code points.</summary>
    internal const int MaxLength = 100;

    private const string Member = "name";

    private static readonly string _pointer = PayloadViolations.PointerTo(Member);

    private readonly string _noun;

    /// <summary>Initializes a new instance of the <see cref="SelfServiceName"/> class.</summary>
    /// <param name="noun">What the name names, as the refusals word it: <c>display name</c>, <c>tenant name</c>.</param>
    internal SelfServiceName(string noun)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(noun);
        _noun = noun;
    }

    /// <summary>The trimmed name the request carries, or every reason it carries none.</summary>
    /// <param name="request">The request, its body not yet read.</param>
    /// <param name="options">The Data API's body bounds.</param>
    /// <param name="ct">A token to cancel the read.</param>
    internal Task<(string? Name, IReadOnlyList<AlvoViolation> Violations)> ReadAsync(
        HttpRequest request, AlvoApiOptions options, CancellationToken ct) =>
        SelfServiceBody.ReadAsync<string>(request, options, Validate, ct);

    /// <summary>The trimmed name <paramref name="root"/> carries, or every reason it carries none.</summary>
    /// <param name="root">The body's top-level object.</param>
    internal (string? Name, IReadOnlyList<AlvoViolation> Violations) Validate(JsonElement root)
    {
        var violations = SelfServiceBody.Unknown(root, Member);
        var (name, violation) = root.TryGetProperty(Member, out var value) ? Judge(value) : (null, Required());
        if (violation is not null)
        {
            violations.Add(violation);
        }

        return violations.Count == 0 ? (name, []) : (null, violations);
    }

    private (string? Name, AlvoViolation? Violation) Judge(JsonElement value)
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

    private AlvoViolation Required() => new(
        _pointer,
        "required",
        $"A {_noun} is required, and one made only of whitespace is none.",
        "Send { \"name\": \"…\" } with at least one visible character.");

    private AlvoViolation NotText() => new(
        _pointer,
        "invalid-value",
        $"A {_noun} is a JSON string with no control character in it.",
        "Send the name as a quoted string, without tabs, line breaks or other control characters.");

    private AlvoViolation TooLong() => new(
        _pointer,
        "max-length",
        string.Create(CultureInfo.InvariantCulture, $"A {_noun} is longer than {MaxLength} characters."),
        string.Create(
            CultureInfo.InvariantCulture,
            $"Shorten it to at most {MaxLength} characters, counted in Unicode code points after trimming."));
}
