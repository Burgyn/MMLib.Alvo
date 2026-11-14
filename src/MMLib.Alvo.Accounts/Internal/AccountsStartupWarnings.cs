using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace MMLib.Alvo.Accounts.Internal;

/// <summary>
/// Says, on every start, which providers this deployment fetches signing keys from over plain HTTP (spec §11.2).
/// </summary>
/// <remarks>
/// <b>Every start, not once.</b> <see cref="AlvoOidcProviderOptions.AllowHttpMetadata"/> exists for an end-to-end
/// stack's test issuer; a deployment that carries it into production must see it in every start's log, because the
/// one start someone happened to read is not the one that went live.
/// </remarks>
/// <param name="options">The accounts options.</param>
/// <param name="logger">Where the warning goes.</param>
internal sealed partial class AccountsStartupWarnings(
    IOptions<AlvoAccountsOptions> options,
    ILogger<AccountsStartupWarnings> logger) : IHostedService
{
    /// <inheritdoc/>
    public Task StartAsync(CancellationToken cancellationToken)
    {
        foreach (var (name, provider) in options.Value.Providers.Where(provider => provider.Value.AllowHttpMetadata))
        {
            HttpMetadataAllowed(logger, name, provider.Authority);
        }

        return Task.CompletedTask;
    }

    /// <inheritdoc/>
    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    /* Accounts' own range, 64xx, beside the core's 61xx, the assistant's 62xx and the host's 63xx. */
    [LoggerMessage(
        EventId = 6401,
        Level = LogLevel.Warning,
        Message = "Identity provider {Provider} allows http:// metadata (AllowHttpMetadata), so its signing keys "
            + "from {Authority} can be replaced by anyone on the network path. Use it for a test issuer only.")]
    private static partial void HttpMetadataAllowed(ILogger logger, string provider, string? authority);
}
