using System.Buffers.Text;
using System.Text;

namespace MMLib.Alvo.Accounts.Tests;

/// <summary>
/// Three-part ID tokens whose <c>iss</c> is JSON <c>null</c>, empty, or absent — each readable as a JWT, signed by
/// nobody, and refused as malformed before any provider is looked up (S2 final review I1).
/// </summary>
internal static class IssuerDefects
{
    /// <summary>Gets a token whose <c>iss</c> is JSON <c>null</c>.</summary>
    internal static string Null { get; } = Token("""{"iss":null,"sub":"sub-1","aud":"client-1"}""");

    /// <summary>Gets a token whose <c>iss</c> is the empty string.</summary>
    internal static string Empty { get; } = Token("""{"iss":"","sub":"sub-1","aud":"client-1"}""");

    /// <summary>Gets a token with no <c>iss</c> at all.</summary>
    internal static string Missing { get; } = Token("""{"sub":"sub-1","aud":"client-1"}""");

    private static string Token(string payload)
        => $"{Encode("""{"alg":"RS256","typ":"JWT","kid":"test-1"}""")}.{Encode(payload)}.{Encode("not a signature")}";

    private static string Encode(string json) => Base64Url.EncodeToString(Encoding.UTF8.GetBytes(json));
}
