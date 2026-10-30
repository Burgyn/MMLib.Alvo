using Microsoft.AspNetCore.Http;
using MMLib.Alvo.Api.Internal;
using MMLib.Alvo.Data;

namespace MMLib.Alvo.Commands.Internal;

/// <summary>
/// A command request's <c>Idempotency-Key</c>: one header value, the port's rules for a usable key, scoped to the command,
/// the caller and the <b>credential</b> (<c>command:&lt;name&gt;/&lt;tenant&gt;/&lt;user&gt;/&lt;key id&gt;</c>, Ruling 34),
/// fingerprinted over the request's inputs.
/// </summary>
/// <remarks>
/// The credential is in the scope because a command keeps its rendered answer and replays it before the access rule runs
/// again — a departure from <see cref="AlvoIdempotency"/>'s Data API contract, which keeps only a row id and re-reads the
/// row through the caller's current policy. Roles and scopes belong to an API key, not to its user, so two keys of one user
/// must never share a replay. Tenant and user stay in the scope as the Data API files them.
/// </remarks>
internal static class CommandKeys
{
    /// <summary>The header.</summary>
    internal const string Header = "Idempotency-Key";

    /// <summary>The credential segment of a caller who presented no key (an internal caller, or a non-key resolver).</summary>
    internal const string NoCredential = "-";

    /// <summary>Whether the request sent the header at all — only then is its fingerprint worth computing.</summary>
    internal static bool Sent(HttpRequest request) => request.Headers.ContainsKey(Header);

    /// <summary>Reads the key; answers a 422 to give instead, or null with <paramref name="key"/> set (null when no header).</summary>
    internal static IResult? TryRead(
        HttpRequest request, CommandRegistration command, AlvoContext caller, string? keyId, string fingerprint, out CommandKey? key)
    {
        key = null;
        var header = request.Headers[Header];
        if (header.Count == 0)
        {
            return null;
        }

        if (header.Count > 1)
        {
            return ProblemResultFactory.Malformed($"Send one '{Header}' header: two values are two keys, and this request can be recorded under one.");
        }

        try
        {
            AlvoIdempotency.EnsureUsableKey(header[0], caller);
        }
        catch (ArgumentException refusal)
        {
            return ProblemResultFactory.Malformed(ProblemResultFactory.WithoutArgumentDetail(refusal.Message));
        }

        key = new CommandKey(ScopeOf(command, caller, keyId), header[0]!, fingerprint);
        return null;
    }

    /// <summary>The scope a key of <paramref name="command"/> is filed under for <paramref name="caller"/> using <paramref name="keyId"/>.</summary>
    internal static string ScopeOf(CommandRegistration command, AlvoContext caller, string? keyId) =>
        $"command:{command.Id}/{AlvoIdempotency.IdentityOf(caller)}/{(string.IsNullOrEmpty(keyId) ? NoCredential : keyId)}";
}
