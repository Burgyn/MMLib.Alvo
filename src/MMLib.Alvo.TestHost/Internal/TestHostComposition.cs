using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

using MMLib.Alvo.Events;
using MMLib.Alvo.Migrations;

using GenericHost = Microsoft.Extensions.Hosting.Host;

namespace MMLib.Alvo.TestHost.Internal;

/// <summary>
/// The host behind one <see cref="AlvoTestHost"/>: an empty generic host, Alvo exactly as the caller configured it, a
/// private database unless the caller chose one, manual dispatch, the caller's clock, and webhooks that never leave
/// the process.
/// </summary>
/// <remarks>
/// <para>
/// <b>An empty builder</b>, so no environment variable or <c>appsettings.json</c> of the machine running the tests can
/// change what a test measures; the one configuration value is <c>Alvo:Events:DispatchMode = Manual</c>.
/// </para>
/// <para>
/// <b>A provider is "chosen" when <c>configure</c> registered an <see cref="ISchemaMigrator"/></b> — what
/// <c>UseSqlite</c> and <c>UsePostgreSql</c> both register at the call, and what Alvo's own startup validation reads.
/// </para>
/// <para>
/// The webhook client is configured by the name a host uses to own Alvo's outbound deliveries
/// (<c>docs/architecture/extensibility.md</c>); a fact that delivers to a stub pins the name.
/// </para>
/// </remarks>
internal sealed class TestHostComposition : IAsyncDisposable
{
    private const string WebhookClientName = "MMLib.Alvo.Events.Webhook";

    private static readonly string _dispatchModeKey =
        $"{AlvoEventOptions.SectionName}:{nameof(AlvoEventOptions.DispatchMode)}";

    private readonly InMemorySqlite? _database;
    private bool _started;

    private TestHostComposition(IHost host, InMemorySqlite? database)
    {
        Host = host;
        _database = database;
    }

    /// <summary>Gets the composed host.</summary>
    internal IHost Host { get; }

    /// <summary>Composes the host; nothing runs until <see cref="StartAsync"/>.</summary>
    internal static TestHostComposition Build(
        Action<IAlvoBuilder> configure, TimeProvider? time, HttpMessageHandler? webhooks)
    {
        var builder = GenericHost.CreateEmptyApplicationBuilder(new HostApplicationBuilderSettings());
        builder.Configuration.AddInMemoryCollection([new(_dispatchModeKey, nameof(AlvoEventDispatchMode.Manual))]);
        if (time is not null)
        {
            builder.Services.AddSingleton(time);
        }

        var alvo = builder.Services.AddAlvo(configure);
        var database = HasProvider(builder.Services) ? null : InMemorySqlite.Open(alvo);
        RouteWebhooks(builder.Services, webhooks);

        return new TestHostComposition(Built(builder, database), database);
    }

    /// <summary>Builds the host, closing the database already opened for it when the build itself throws.</summary>
    private static IHost Built(HostApplicationBuilder builder, InMemorySqlite? database)
    {
        try
        {
            return builder.Build();
        }
        catch
        {
            database?.Dispose();
            throw;
        }
    }

    /// <summary>Starts the host and insists its boot reached Ready.</summary>
    /// <exception cref="InvalidOperationException">The host started but the boot did not reach Ready.</exception>
    internal async Task StartAsync(CancellationToken cancellationToken)
    {
        await Host.StartAsync(cancellationToken).ConfigureAwait(false);
        _started = true;

        var boot = Host.Services.GetRequiredService<AlvoBootState>();
        if (boot.Phase is not AlvoBootPhase.Ready)
        {
            throw NotReady(boot);
        }
    }

    /// <summary>Stops the host, then disposes it and its database whether or not the stop succeeded.</summary>
    /// <exception cref="AggregateException">The stop and the disposal both failed; the stop's failure comes first.</exception>
    /// <returns>A task that completes when everything is disposed.</returns>
    public async ValueTask DisposeAsync()
    {
        try
        {
            await StopAsync().ConfigureAwait(false);
        }
        catch (Exception stopFailure)
        {
            await After(stopFailure, DisposeHostAndDatabaseAsync).ConfigureAwait(false);
            throw;
        }

        await DisposeHostAndDatabaseAsync().ConfigureAwait(false);
    }

    /// <summary>
    /// Disposes after <paramref name="failure"/>; if disposing fails too, both surface together, the original first,
    /// so a cleanup failure never hides why the host was being torn down.
    /// </summary>
    internal Task DisposeAfterAsync(Exception failure) => After(failure, () => DisposeAsync().AsTask());

    private static async Task After(Exception failure, Func<Task> cleanup)
    {
        try
        {
            await cleanup().ConfigureAwait(false);
        }
        catch (Exception cleanupFailure)
        {
            throw new AggregateException(failure, cleanupFailure);
        }
    }

    private async Task StopAsync()
    {
        if (_started)
        {
            await Host.StopAsync(CancellationToken.None).ConfigureAwait(false);
        }
    }

    private async Task DisposeHostAndDatabaseAsync()
    {
        try
        {
            await DisposeHostAsync().ConfigureAwait(false);
        }
        finally
        {
            if (_database is not null)
            {
                await _database.DisposeAsync().ConfigureAwait(false);
            }
        }
    }

    private ValueTask DisposeHostAsync()
    {
        if (Host is IAsyncDisposable host)
        {
            return host.DisposeAsync();
        }

        Host.Dispose();

        return ValueTask.CompletedTask;
    }

    private static bool HasProvider(IServiceCollection services) =>
        services.Any(descriptor => descriptor.ServiceType == typeof(ISchemaMigrator));

    private static void RouteWebhooks(IServiceCollection services, HttpMessageHandler? webhooks) =>
        services.AddHttpClient(WebhookClientName).ConfigurePrimaryHttpMessageHandler(
            () => webhooks is null ? new NoWebhookReceiver() : new SharedReceiver(webhooks));

    private static InvalidOperationException NotReady(AlvoBootState boot) => new(
        $"The test host started, but its boot ended {boot.Phase} rather than Ready, so there is no schema to test "
        + $"against: {boot.Failure ?? "the boot recorded no reason"}. An AlvoTestHost needs FromDescriptor.");
}
