using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using MMLib.Alvo.Api;
using MMLib.Alvo.Api.Internal;
using MMLib.Alvo.Data;
using System.Security.Cryptography;
using System.Text;

namespace MMLib.Alvo.Tenants.SelfService;

/// <summary>
/// The <c>Idempotency-Key</c> of <c>POST {prefix}/tenants</c> (#542 item 5): read with the Data API's rules, and fingerprinted
/// over what makes two creations the same request.
/// </summary>
/// <remarks>
/// <para>
/// <b>The key's rules are the Data API's</b> (<see cref="AlvoIdempotency.EnsureUsableKey"/>): at most one header line,
/// not blank, within <see cref="AlvoApiOptions.MaxIdempotencyKeyBytes"/> — a key that cannot be recorded is 422, before
/// anything is written, never silently ignored. The port's third rule, an anonymous caller, cannot arise: only a person
/// reaches this route.
/// </para>
/// <para>
/// <b>The fingerprint is the operation and the validated name</b>, not the body's bytes: the body has exactly one member,
/// and an unknown one is refused, so <c>{"name":"Acme"}</c> however it is spaced is one request. The same key with
/// another name is <c>idempotency-conflict</c>.
/// </para>
/// </remarks>
internal static class TenantCreationKey
{
    private const string Header = DataApiEndpoints.IdempotencyKeyHeader;

    private const string Operation = SelfServiceEndpoints.CreateOperationId;

    private const string BlankKey =
        $"The '{Header}' header must not be blank: a record is filed under the key and the person, so every blank key would "
        + "share one record. Send an opaque, unique key, or send the creation without one.";

    private const string RepeatedKey =
        $"The '{Header}' header must be sent at most once. Two values are two keys and a tenant creation is recorded under "
        + "one, so picking either would answer a question the caller did not ask.";

    /// <summary>The request's key, or <see langword="null"/> when it sent none, or the 422 a key that cannot be recorded earns.</summary>
    /// <param name="call">The admitted call.</param>
    /// <returns>The key and no refusal, or no key and the refusal.</returns>
    internal static (string? Key, IResult? Refusal) Read(SelfServiceCall call)
    {
        ArgumentNullException.ThrowIfNull(call);
        var header = call.Http.Request.Headers[Header];
        return header.Count switch
        {
            0 => (null, null),
            1 => Usable(header[0], call),
            _ => (null, ProblemResultFactory.Malformed(RepeatedKey)),
        };
    }

    /// <summary>The token a creation of <paramref name="name"/> with <paramref name="key"/> is recorded under.</summary>
    /// <param name="key">The caller's key.</param>
    /// <param name="name">The validated tenant name.</param>
    internal static AlvoIdempotency Token(string key, string name) => new(key, Fingerprint(name));

    private static (string? Key, IResult? Refusal) Usable(string? key, SelfServiceCall call)
    {
        var bound = call.Http.RequestServices.GetRequiredService<IOptions<AlvoApiOptions>>().Value.MaxIdempotencyKeyBytes;
        if (key is not null && Encoding.UTF8.GetByteCount(key) > bound)
        {
            return (null, ProblemResultFactory.Malformed(
                $"The '{Header}' header must be at most {bound} bytes when encoded as UTF-8. It is refused rather than "
                + "shortened, because two keys that differ only past that length would become one key."));
        }

        return string.IsNullOrWhiteSpace(key) ? (null, ProblemResultFactory.Malformed(BlankKey)) : (key, null);
    }

    private static string Fingerprint(string name) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes($"{Operation}\n{name}")));
}
