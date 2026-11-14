using Microsoft.AspNetCore.Http;
using MMLib.Alvo.Api;
using MMLib.Alvo.Tenants.SelfService;

namespace MMLib.Alvo.Accounts.Internal;

/// <summary>
/// Reads the body of <c>PATCH {prefix}/user</c>: <c>{ "name": string }</c> and nothing else, because the display name is
/// the only thing a person changes there (S2 D8) — under the rules every self-service name follows
/// (<see cref="SelfServiceName"/>: the Data API's body bounds and codes, trimmed, 1–<see cref="MaxLength"/> code points,
/// no control character, no half of a surrogate pair).
/// </summary>
internal static class DisplayName
{
    /// <summary>The longest display name, in Unicode code points.</summary>
    internal const int MaxLength = SelfServiceName.MaxLength;

    private static readonly SelfServiceName _name = new("display name");

    /// <summary>The trimmed name the request carries, or every reason it carries none.</summary>
    /// <param name="request">The request, its body not yet read.</param>
    /// <param name="options">The Data API's body bounds.</param>
    /// <param name="ct">A token to cancel the read.</param>
    internal static Task<(string? Name, IReadOnlyList<AlvoViolation> Violations)> ReadAsync(
        HttpRequest request, AlvoApiOptions options, CancellationToken ct) =>
        _name.ReadAsync(request, options, ct);
}
