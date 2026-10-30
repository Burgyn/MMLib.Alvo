using Microsoft.AspNetCore.Http;

namespace MMLib.Alvo.Api;

/// <summary>How one inbound webhook receiver verifies a delivery and whom its handler runs as.</summary>
public sealed class AlvoWebhookReceiverOptions
{
    /// <summary>Gets or sets the tenant the handler runs in, as the system; null for none.</summary>
    public TenantId? Tenant { get; set; }

    /// <summary>
    /// Gets or sets a function that names the tenant of a delivery from the request and the verified body bytes. It is
    /// called only for a delivery whose signature verified; a tenant it answers wins over <see cref="Tenant"/>, and null
    /// falls back to it. With neither, the handler runs as the system in no tenant. It is asynchronous, so it can look the
    /// tenant up (a provider's account id mapped to a tenant row); the token is the request's (Ruling 56).
    /// </summary>
    /// <remarks>
    /// Standard Webhooks signs the id, the timestamp and the body — not the path, the query or any other header. Derive
    /// the tenant from the signed body (the bytes passed in), or map one receiver per tenant with its own secret and
    /// <see cref="Tenant"/>. A tenant read from the path or a header can be changed by anyone who sees one signed
    /// delivery, without breaking its signature (Ruling 37). The request body is replayed after the call, so the handler
    /// still reads it whole.
    /// </remarks>
    public Func<HttpRequest, ReadOnlyMemory<byte>, CancellationToken, ValueTask<TenantId?>>? ResolveTenant { get; set; }

    /// <summary>Gets or sets how far <c>webhook-timestamp</c> may be from now, either way; 5 minutes, at most 1 hour.</summary>
    public TimeSpan Tolerance { get; set; } = TimeSpan.FromMinutes(5);

    /// <summary>Gets or sets the largest body accepted, in bytes; 1 MiB, at most 64 MiB. A larger one is answered 413.</summary>
    /// <remarks>
    /// The receiver raises the server's own request body limit (Kestrel's is 30 MB by default) to this value when the
    /// server lets it, and never lowers it (Ruling 55). IIS and HTTP.sys enforce their own limits — IIS's
    /// <c>maxAllowedContentLength</c> among them — which this does not lift: raise them too for a value above theirs.
    /// </remarks>
    public long MaxBodyBytes { get; set; } = 1_048_576;

    internal AlvoWebhookReceiverOptions Validated()
    {
        if (Tolerance <= TimeSpan.Zero || Tolerance > TimeSpan.FromHours(1))
        {
            throw new ArgumentOutOfRangeException(nameof(Tolerance), Tolerance, "A webhook timestamp tolerance is greater than zero and at most one hour.");
        }

        return MaxBodyBytes is > 0 and <= 67_108_864
            ? this
            : throw new ArgumentOutOfRangeException(nameof(MaxBodyBytes), MaxBodyBytes, "A webhook body limit is greater than zero and at most 64 MiB.");
    }
}
