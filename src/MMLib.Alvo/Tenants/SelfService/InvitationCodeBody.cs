using Microsoft.AspNetCore.Http;
using MMLib.Alvo.Api;
using MMLib.Alvo.Api.Internal;
using System.Text.Json;

namespace MMLib.Alvo.Tenants.SelfService;

/// <summary>
/// The body of <c>POST {prefix}/invitations/preview</c> and <c>…/accept</c>: exactly <c>{ "code": string }</c> — read,
/// hashed at once, and dropped, so only <see cref="CodeHash"/> outlives the read.
/// </summary>
/// <remarks>
/// <para>
/// <b>The code travels in a body, never a URL</b> (spec §6.3): a URL is written to access logs, browser history and
/// <c>Referer</c> headers.
/// </para>
/// <para>
/// <b>Any non-empty string is a code to look up</b> (S4 plan D2): a malformed one hashes like any other and is simply not
/// found — one path, one answer. Only a body that carries no string at all is a 422, with fixed sentences at
/// <c>/code</c> that never repeat the value.
/// </para>
/// </remarks>
/// <param name="CodeHash">The lowercase hex SHA-256 of the presented code (<see cref="InvitationCode.Hash"/>).</param>
internal sealed record InvitationCodeBody(string CodeHash)
{
    private const string Member = "code";

    private static readonly string _pointer = PayloadViolations.PointerTo(Member);

    /// <summary>The presented code's hash, or every reason the body presents none.</summary>
    /// <param name="request">The request, its body not yet read.</param>
    /// <param name="options">The Data API's body bounds.</param>
    /// <param name="ct">A token to cancel the read.</param>
    internal static Task<(InvitationCodeBody? Value, IReadOnlyList<AlvoViolation> Violations)> ReadAsync(
        HttpRequest request, AlvoApiOptions options, CancellationToken ct) =>
        SelfServiceBody.ReadAsync<InvitationCodeBody>(request, options, Validate, ct);

    /// <summary>The code <paramref name="root"/> presents, hashed, or every reason it presents none.</summary>
    /// <param name="root">The body's top-level object.</param>
    internal static (InvitationCodeBody? Value, IReadOnlyList<AlvoViolation> Violations) Validate(JsonElement root)
    {
        var violations = SelfServiceBody.Unknown(root, Member);
        var code = root.TryGetProperty(Member, out var value) && value.ValueKind == JsonValueKind.String
            ? SelfServiceBody.TextOf(value)
            : null;
        if (string.IsNullOrEmpty(code))
        {
            violations.Add(NoCode());
        }

        return violations.Count == 0 ? (new InvitationCodeBody(InvitationCode.Hash(code!)), []) : (null, violations);
    }

    private static AlvoViolation NoCode() => new(
        _pointer,
        "required",
        "An invitation code is required: a non-empty JSON string, and no other member beside it.",
        "Send { \"code\": \"alvo_inv_…\" } with the code exactly as the invitation gave it.");
}
