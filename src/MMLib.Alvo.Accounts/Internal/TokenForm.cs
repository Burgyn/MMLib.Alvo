using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.Primitives;
using Microsoft.Net.Http.Headers;
using System.Text;

namespace MMLib.Alvo.Accounts.Internal;

/// <summary>
/// A token request's parameters (RFC 6749 §3.2): one <c>application/x-www-form-urlencoded</c> body of at most
/// <see cref="MaxBytes"/>, read before anything else, with every parameter present at most once.
/// </summary>
/// <remarks>
/// <para>
/// <b>Read by hand, not through <c>ReadFormAsync</c>.</b> The form feature buffers up to its own (much larger) limits and
/// merges nothing about duplicates into a refusal; this reads at most one byte past the limit and refuses a parameter sent
/// twice, which RFC 6749 §3.2 says "MUST NOT be included more than once" — an ambiguity an attacker could otherwise use to
/// make two layers disagree about which value counts.
/// </para>
/// <para>Never printed: its values are credentials.</para>
/// </remarks>
internal sealed class TokenForm
{
    /// <summary>The largest body the token endpoint reads: an ID token is a few KiB, so this leaves room and no more.</summary>
    internal const int MaxBytes = 16 * 1024;

    private const string FormMediaType = "application/x-www-form-urlencoded";

    private readonly Dictionary<string, StringValues> _values;

    private TokenForm(Dictionary<string, StringValues> values) => _values = values;

    /// <summary>
    /// The value of <paramref name="name"/>, or <see langword="null"/> when it is absent or empty — RFC 6749 §3.2: "parameters
    /// sent without a value MUST be treated as if they were omitted from the request".
    /// </summary>
    /// <param name="name">The parameter's name.</param>
    internal string? this[string name] => _values.TryGetValue(name, out var value) && value.Count == 1 && !string.IsNullOrEmpty(value[0])
        ? value[0]
        : null;

    /// <summary>The form <paramref name="request"/> carries, or <see langword="null"/> when it is not a well-formed one.</summary>
    /// <param name="request">The token request.</param>
    /// <param name="ct">A token to cancel the read.</param>
    internal static async Task<TokenForm?> ReadAsync(HttpRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (!IsForm(request.ContentType) || request.ContentLength > MaxBytes
            || await ReadBodyAsync(request.Body, ct).ConfigureAwait(false) is not { } body)
        {
            return null;
        }

        return Parse(body);
    }

    private static bool IsForm(string? contentType)
        => MediaTypeHeaderValue.TryParse(contentType, out var parsed)
            && parsed.MediaType.Equals(FormMediaType, StringComparison.OrdinalIgnoreCase);

    private static async Task<string?> ReadBodyAsync(Stream body, CancellationToken ct)
    {
        var buffer = new byte[MaxBytes + 1];
        var total = 0;
        int read;
        while (total < buffer.Length && (read = await body.ReadAsync(buffer.AsMemory(total), ct).ConfigureAwait(false)) > 0)
        {
            total += read;
        }

        return total > MaxBytes ? null : Encoding.UTF8.GetString(buffer, 0, total);
    }

    private static TokenForm? Parse(string body)
    {
        try
        {
            using var reader = new FormReader(body);
            var values = reader.ReadForm();
            return values.Values.Any(value => value.Count != 1) ? null : new TokenForm(values);
        }
        catch (InvalidDataException)
        {
            return null;
        }
    }
}
