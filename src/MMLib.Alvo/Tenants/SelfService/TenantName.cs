using Microsoft.AspNetCore.Http;
using MMLib.Alvo.Api;

namespace MMLib.Alvo.Tenants.SelfService;

/// <summary>
/// Reads the body of <c>POST {prefix}/tenants</c> and <c>PATCH {prefix}/tenants/{id}</c>: <c>{ "name": string }</c> under
/// the rules every self-service name follows (<see cref="SelfServiceName"/>, the display name's own).
/// </summary>
internal static class TenantName
{
    /// <summary>The longest tenant name, in Unicode code points.</summary>
    internal const int MaxLength = SelfServiceName.MaxLength;

    private static readonly SelfServiceName _name = new("tenant name");

    /// <summary>The trimmed name the request carries, or every reason it carries none.</summary>
    /// <param name="request">The request, its body not yet read.</param>
    /// <param name="options">The Data API's body bounds.</param>
    /// <param name="ct">A token to cancel the read.</param>
    internal static Task<(string? Name, IReadOnlyList<AlvoViolation> Violations)> ReadAsync(
        HttpRequest request, AlvoApiOptions options, CancellationToken ct) =>
        _name.ReadAsync(request, options, ct);
}
