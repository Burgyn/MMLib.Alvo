using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Extensions.Logging;
using MMLib.Alvo.Api;
using MMLib.Alvo.Api.Internal;
using MMLib.Alvo.Data;
using MMLib.Alvo.Secrets;
using System.Globalization;
using System.Text;

namespace MMLib.Alvo.InboundWebhooks.Internal;

/// <summary>
/// One receiver: verify (headers, window, body size, signature), then claim the id, then run the host's handler as the
/// system in the resolved tenant over the very bytes that were verified. Verification precedes the claim and the tenant
/// (Ruling 24): an unverified request records nothing and runs no host code.
/// </summary>
internal sealed class WebhookReceiver(
    string pattern,
    SecretName secret,
    AlvoWebhookReceiverOptions options,
    RequestDelegate handler,
    ISecretStore secrets,
    IInboundWebhookLedger ledger,
    TimeProvider time,
    ILogger<WebhookReceiver> logger)
{
    private sealed record Signed(string Id, string Timestamp, long Seconds, string Signature);

    /// <summary>Receives one delivery.</summary>
    internal async Task ReceiveAsync(HttpContext http)
    {
        ArgumentNullException.ThrowIfNull(http);
        var (signed, body, refusal) = await VerifiedAsync(http).ConfigureAwait(false);
        if (refusal is not null)
        {
            await refusal.ExecuteAsync(http).ConfigureAwait(false);
            return;
        }

        await ClaimedAsync(http, signed!, body!).ConfigureAwait(false);
    }

    private async Task<(Signed?, byte[]?, IResult?)> VerifiedAsync(HttpContext http)
    {
        if (Headers(http.Request) is not { } signed)
        {
            return (null, null, Refused("it lacks one of the webhook-id, webhook-timestamp and webhook-signature headers, or one is malformed"));
        }

        if (!Fresh(signed.Seconds))
        {
            return (null, null, Refused($"its timestamp is more than {options.Tolerance.TotalMinutes:0} minutes from now"));
        }

        if (await BodyAsync(http.Request).ConfigureAwait(false) is not { } body)
        {
            return (null, null, ProblemResultFactory.Unreadable(StatusCodes.Status413PayloadTooLarge));
        }

        return await KeysAsync(http.RequestAborted).ConfigureAwait(false) switch
        {
            null => (null, null, ProblemResultFactory.Internal()),
            var keys when StandardWebhookSignature.Verifies(keys, signed.Id, signed.Timestamp, body, signed.Signature) => (signed, body, null),
            _ => (null, null, Refused("no signature in it verifies with the receiver's secret")),
        };
    }

    private async Task ClaimedAsync(HttpContext http, Signed signed, byte[] body)
    {
        var claim = await ledger.ClaimAsync(pattern, signed.Id, time.GetUtcNow(), CancellationToken.None).ConfigureAwait(false);
        switch (claim.State)
        {
            case WebhookClaim.Completed:
                WebhookLog.Duplicate(logger, pattern);
                http.Response.StatusCode = StatusCodes.Status200OK;
                return;
            case WebhookClaim.InFlight:
                await ProblemResultFactory.WebhookInFlight().ExecuteAsync(http).ConfigureAwait(false);
                return;
            default:
                await RunAsync(http, signed.Id, claim.Token!, body).ConfigureAwait(false);
                return;
        }
    }

    /// <summary>
    /// Runs the handler under the claim's fencing token (Ruling 36): the completion and the release name the token, so a
    /// run that outlived its lease neither records the id another run now holds nor frees that run's claim.
    /// </summary>
    private async Task RunAsync(HttpContext http, string id, string token, byte[] body)
    {
        try
        {
            AlvoCaller.For(http).Resolve(AlvoContext.System(await TenantOfAsync(http.Request, body).ConfigureAwait(false)));
            Replay(http.Request, body);
            await handler(http).ConfigureAwait(false);
        }
        catch
        {
            await ReleaseAsync(id, token).ConfigureAwait(false);
            throw;
        }

        if (http.Response.StatusCode is not (>= 200 and < 300))
        {
            await ReleaseAsync(id, token).ConfigureAwait(false);
        }
        else if (!await ledger.CompleteAsync(pattern, id, token, time.GetUtcNow(), CancellationToken.None).ConfigureAwait(false))
        {
            WebhookLog.ClaimLost(logger, pattern);
        }
    }

    /// <summary>
    /// Gives the id back so the sender's retry runs. A failure to do so is logged, never thrown: it would replace the
    /// handler's own exception or answer, and the id frees itself when its lease runs out.
    /// </summary>
    private async Task ReleaseAsync(string id, string token)
    {
        try
        {
            await ledger.ReleaseAsync(pattern, id, token, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception bookkeeping)
        {
            WebhookLog.NotReleased(logger, pattern, bookkeeping);
        }
    }

    /// <summary>
    /// The tenant from the verified bytes (Ruling 37). The body is replayed before the resolver too, so one that reads
    /// <c>request.Body</c> instead of the bytes it is given reads the same verified content.
    /// </summary>
    private async ValueTask<TenantId?> TenantOfAsync(HttpRequest request, byte[] body)
    {
        if (options.ResolveTenant is not { } resolve)
        {
            return options.Tenant;
        }

        Replay(request, body);
        return await resolve(request, body, request.HttpContext.RequestAborted).ConfigureAwait(false) ?? options.Tenant;
    }

    private static void Replay(HttpRequest request, byte[] body)
    {
        request.Body = new MemoryStream(body, writable: false);
        request.ContentLength = body.Length;
    }

    private static Signed? Headers(HttpRequest request)
    {
        var id = Single(request, StandardWebhookSignature.IdHeader);
        var signature = Single(request, StandardWebhookSignature.SignatureHeader);
        var timestamp = Single(request, StandardWebhookSignature.TimestampHeader);

        return id is not null && signature is not null && Encoding.UTF8.GetByteCount(id) <= InboundWebhookLimits.MaxIdBytes
            && long.TryParse(timestamp, NumberStyles.None, CultureInfo.InvariantCulture, out var seconds)
            ? new Signed(id, timestamp!, seconds, signature)
            : null;
    }

    private static string? Single(HttpRequest request, string header) =>
        request.Headers[header] is { Count: 1 } values && !string.IsNullOrWhiteSpace(values[0]) ? values[0] : null;

    private bool Fresh(long seconds)
    {
        var now = time.GetUtcNow().ToUnixTimeSeconds();
        var tolerance = (long)options.Tolerance.TotalSeconds;
        return seconds >= now - tolerance && seconds <= now + tolerance;
    }

    /// <summary>
    /// The body, or null past <see cref="AlvoWebhookReceiverOptions.MaxBodyBytes"/> — counted while it is read, so a body
    /// without a length is never buffered past it. The server's own limit is raised to the receiver's, never lowered, and
    /// a server that refuses the read past its limit answers the receiver's 413 too (Ruling 55).
    /// </summary>
    private async Task<byte[]?> BodyAsync(HttpRequest request)
    {
        if (request.ContentLength > options.MaxBodyBytes)
        {
            return null;
        }

        RaiseServerLimit(request.HttpContext);
        try
        {
            return await ReadCappedAsync(request).ConfigureAwait(false);
        }
        catch (BadHttpRequestException refused) when (refused.StatusCode == StatusCodes.Status413PayloadTooLarge)
        {
            return null;
        }
    }

    private void RaiseServerLimit(HttpContext http)
    {
        if (http.Features.Get<IHttpMaxRequestBodySizeFeature>() is { IsReadOnly: false, MaxRequestBodySize: { } limit } feature
            && limit < options.MaxBodyBytes)
        {
            feature.MaxRequestBodySize = options.MaxBodyBytes;
        }
    }

    private async Task<byte[]?> ReadCappedAsync(HttpRequest request)
    {
        using var buffer = new MemoryStream();
        var chunk = new byte[16_384];
        int read;
        while ((read = await request.Body.ReadAsync(chunk, request.HttpContext.RequestAborted).ConfigureAwait(false)) > 0)
        {
            if (buffer.Length + read > options.MaxBodyBytes)
            {
                return null;
            }

            buffer.Write(chunk, 0, read);
        }

        return buffer.ToArray();
    }

    private async Task<IReadOnlyList<byte[]>?> KeysAsync(CancellationToken ct)
    {
        try
        {
            return await secrets.GetAsync(secret, ct).ConfigureAwait(false) is { } stored
                ? StandardWebhookSignature.Keys(stored)
                : Unusable("Nothing is stored under that name.");
        }
        catch (FormatException unusable)
        {
            return Unusable(unusable.Message);
        }
    }

    private IReadOnlyList<byte[]>? Unusable(string reason)
    {
        WebhookLog.SecretUnusable(logger, pattern, secret.Value, reason);
        return null;
    }

    private IResult Refused(string reason)
    {
        WebhookLog.Refused(logger, pattern, reason);
        return ProblemResultFactory.WebhookRefused($"The delivery was refused: {reason}.");
    }
}
