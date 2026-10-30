using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.Routing.Patterns;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using MMLib.Alvo.Api;
using MMLib.Alvo.Data;
using MMLib.Alvo.InboundWebhooks.Internal;
using MMLib.Alvo.Secrets;

namespace Microsoft.AspNetCore.Builder;

/// <summary>Maps receivers of Standard Webhooks deliveries.</summary>
public static class AlvoWebhookEndpointRouteBuilderExtensions
{
    /// <summary>
    /// Maps a <c>POST</c> receiver that runs <paramref name="handler"/> once per verified Standard Webhooks delivery, as the
    /// system in the tenant <paramref name="configure"/> names.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Before the handler: the <c>webhook-id</c>, <c>webhook-timestamp</c> and <c>webhook-signature</c> headers must be
    /// present, the timestamp within <see cref="AlvoWebhookReceiverOptions.Tolerance"/> of now (5 minutes), the body within
    /// <see cref="AlvoWebhookReceiverOptions.MaxBodyBytes"/>, and one <c>v1,</c> signature must verify with the secret
    /// stored under <paramref name="secretName"/> (several space-separated <c>whsec_</c> secrets are accepted, for rotation).
    /// Otherwise the answer is 401 (413 for the body) and nothing runs. Every stored secret must decode to at least 24
    /// bytes, as Standard Webhooks asks: a missing, malformed or shorter one answers 500 and logs an Error naming the
    /// secret and why, never its value (Ruling 54). The handler's own failures are answered as Alvo's problem document.
    /// </para>
    /// <para>
    /// A <c>webhook-id</c> is handled once per receiver: a delivery already handled is acknowledged 200 without running the
    /// handler, one still running is answered 409, and one whose handler failed or answered non-2xx is given back so the
    /// sender's retry runs it. Ids are forgotten after seven days. The handler takes its parameters as any minimal API —
    /// the body binds from the very bytes that were verified — and an <c>AlvoCaller</c> carries the system context.
    /// Delivery is at least once: a handler that wrote and then crashed before its id was recorded runs again, so it should
    /// be idempotent on the id. Receivers are not in the OpenAPI document. They need a relational data provider.
    /// </para>
    /// <para>
    /// The tenant comes from <see cref="AlvoWebhookReceiverOptions.Tenant"/> or from
    /// <see cref="AlvoWebhookReceiverOptions.ResolveTenant"/>, which is given the verified body and may look the tenant up
    /// asynchronously (Ruling 56). Only the body is signed:
    /// do not take the tenant from the path or a header (Ruling 37).
    /// </para>
    /// <para>
    /// The returned builder configures the outer receiver endpoint. Conventions on it — metadata, authorization, rate
    /// limiting — apply to the request as a whole, but an endpoint filter added with <c>AddEndpointFilter</c> never runs:
    /// the handler is built when the receiver is mapped, and the outer endpoint is a plain request delegate. Put such logic
    /// in the handler. The system caller is bound to the handler's <c>AlvoCaller</c> parameter only; it is not published
    /// through <c>IAlvoContextAccessor</c> (unlike <c>WithAlvoCaller()</c> and commands), so pass the
    /// <c>AlvoCaller</c> to the code that needs it (Ruling 49, L8).
    /// </para>
    /// </remarks>
    /// <param name="endpoints">The route builder.</param>
    /// <param name="pattern">The route pattern; it is also the receiver's dedup scope.</param>
    /// <param name="secretName">The secret store name of the signing secret.</param>
    /// <param name="handler">The minimal-API handler.</param>
    /// <param name="configure">The receiver's tenant, window and body limit.</param>
    /// <returns>The endpoint's convention builder.</returns>
    /// <exception cref="ArgumentException"><paramref name="secretName"/> is not a secret name.</exception>
    /// <exception cref="InvalidOperationException">The data provider keeps no webhook ledger, or the pattern is mapped twice.</exception>
    public static IEndpointConventionBuilder MapAlvoWebhookReceiver(
        this IEndpointRouteBuilder endpoints, string pattern, string secretName, Delegate handler, Action<AlvoWebhookReceiverOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(endpoints);
        ArgumentException.ThrowIfNullOrWhiteSpace(pattern);
        ArgumentNullException.ThrowIfNull(handler);
        var secret = SecretName.TryParse(secretName, out var name) ? name! : throw new ArgumentException(
            $"'{secretName}' is not a secret name: lower-case letters, digits, '.', '_' and '-', starting with a letter.", nameof(secretName));
        var options = new AlvoWebhookReceiverOptions();
        configure?.Invoke(options);

        var receiver = Receiver(endpoints, pattern, secret, options.Validated(), Inner(endpoints, pattern, handler));
        endpoints.ServiceProvider.GetRequiredService<WebhookReceivers>().Add(pattern);

        return endpoints.MapPost(pattern, (RequestDelegate)receiver.ReceiveAsync)
            .WithMetadata(new WebhookReceiverEndpointMetadata(pattern))
            .ExcludeFromDescription();
    }

    private static WebhookReceiver Receiver(
        IEndpointRouteBuilder endpoints, string pattern, SecretName secret, AlvoWebhookReceiverOptions options, RequestDelegate handler)
    {
        var services = endpoints.ServiceProvider;
        var ledger = services.GetService<IInboundWebhookLedger>() ?? throw new InvalidOperationException(
            "Inbound webhooks need a relational data provider to remember which deliveries they handled. Register UseSqlite(…) or UsePostgreSql(…).");

        return new WebhookReceiver(
            pattern, secret, options, handler, services.GetRequiredService<ISecretStore>(), ledger,
            services.GetService<TimeProvider>() ?? TimeProvider.System, services.GetRequiredService<ILogger<WebhookReceiver>>());
    }

    private static RequestDelegate Inner(IEndpointRouteBuilder endpoints, string pattern, Delegate handler) =>
        RequestDelegateFactory.Create(handler, new RequestDelegateFactoryOptions
        {
            ServiceProvider = endpoints.ServiceProvider,
            RouteParameterNames = [.. RoutePatternFactory.Parse(pattern).Parameters.Select(parameter => parameter.Name)],
        }).RequestDelegate;
}
