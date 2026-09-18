using Microsoft.Extensions.Hosting;

namespace MMLib.Alvo.Identity.Internal;

/// <summary>
/// Seeds the configured bootstrap administrator at startup.
/// </summary>
/// <remarks>
/// <b>A shell until the seeding lands.</b> It is registered now so the composition — a hosted service
/// that runs once, before the first request — is the shape the facts are written against, rather than
/// a registration retro-fitted around an implementation.
/// </remarks>
internal sealed class AlvoIdentityBootstrap : IHostedService
{
    /// <inheritdoc/>
    public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    /// <inheritdoc/>
    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
