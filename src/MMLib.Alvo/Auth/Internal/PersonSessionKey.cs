using System.Globalization;

namespace MMLib.Alvo.Auth.Internal;

/// <summary>
/// The key id of a person principal: <c>session:</c> and the session id as 32 hex digits (accounts S2 D15). The person
/// resolver writes it and the self-service routes read it back, so a write that hands out power can name the session that
/// asked for it, and the store can refuse it once that session has ended (stack review A-I1).
/// </summary>
/// <remarks>
/// The key id is no secret: a session id authorises nothing on its own, it only scopes idempotency keys and, here, ties a
/// credential write to the session it came from. A host's own person resolver that writes some other key id simply ties
/// nothing — it has no Alvo session for a reuse to end.
/// </remarks>
internal static class PersonSessionKey
{
    /// <summary>The prefix of a person principal's key id.</summary>
    internal const string Prefix = "session:";

    /// <summary>The key id of a principal resolved from <paramref name="session"/>.</summary>
    /// <param name="session">The session id.</param>
    internal static string Of(Guid session) => Prefix + session.ToString("N", CultureInfo.InvariantCulture);

    /// <summary>The session a person principal's key id names, or <see langword="null"/> when it names none.</summary>
    /// <param name="keyId">The principal's key id.</param>
    internal static Guid? SessionOf(string? keyId) =>
        keyId is not null
        && keyId.StartsWith(Prefix, StringComparison.Ordinal)
        && Guid.TryParseExact(keyId.AsSpan(Prefix.Length), "N", out var session)
            ? session
            : null;
}
