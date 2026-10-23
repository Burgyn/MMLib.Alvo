using Microsoft.Extensions.DependencyInjection;

using MMLib.Alvo.Data;
using MMLib.Alvo.TestHost.Internal;

namespace MMLib.Alvo.TestHost;

/// <summary>
/// A real Alvo backend inside a test: the production write pipeline, rules, hooks and outbox over a private database,
/// started from the same <c>AddAlvo</c> builder a host uses.
/// </summary>
/// <remarks>
/// <para>
/// <b>Nothing above the database is faked.</b> <see cref="Data"/> behaves exactly as the host's own <c>IAlvoData</c>:
/// a descriptor <c>reject</c> refuses, a <c>mutate</c> applies and a rule hides rows as in production. Unless the builder
/// registers a provider of its own — <c>UsePostgreSql</c>, for parity runs — each host gets a fresh SQLite database in
/// memory, private to it and dropped when the host is disposed.
/// </para>
/// <para>
/// <b>Events wait for the test.</b> The dispatcher runs in the manual dispatch mode, so nothing is delivered until the
/// test drains. Webhooks go to the handler passed as <c>webhooks</c>; without one, every webhook attempt fails without
/// touching the network.
/// </para>
/// </remarks>
public sealed class AlvoTestHost : IAsyncDisposable
{
    private readonly TestHostComposition _composition;
    private bool _disposed;

    private AlvoTestHost(TestHostComposition composition)
    {
        _composition = composition;
        Data = composition.Host.Services.GetRequiredService<IAlvoData>();
    }

    /// <summary>Gets the data port, behaving exactly as the host's own endpoints and services reach it.</summary>
    public IAlvoData Data { get; }

    /// <summary>Gets the host's services, for any other Alvo port a test needs, such as <c>IAlvoEvents</c>.</summary>
    public IServiceProvider Services => _composition.Host.Services;

    /// <summary>Starts a backend and waits until its descriptor is applied.</summary>
    /// <param name="configure">
    /// Configures Alvo as <c>AddAlvo</c> would — at least <c>FromDescriptor</c>. Registering a provider here (for
    /// example <c>UsePostgreSql</c>) replaces the in-memory SQLite database.
    /// </param>
    /// <param name="time">
    /// The clock every timestamp and retry backoff reads; the system clock when omitted. A <c>FakeTimeProvider</c> pins
    /// it.
    /// </param>
    /// <param name="webhooks">
    /// Receives every webhook delivery in place of the network. The host never disposes it, so one handler can serve
    /// several hosts. Without one, every webhook attempt fails.
    /// </param>
    /// <param name="cancellationToken">Cancels the start.</param>
    /// <returns>The started host; dispose it to stop the host and drop its database.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="configure"/> is <see langword="null"/>.</exception>
    /// <exception cref="Migrations.AlvoStartupRefusedException">The boot refused the descriptor or its database.</exception>
    public static async Task<AlvoTestHost> StartAsync(
        Action<IAlvoBuilder> configure,
        TimeProvider? time = null,
        HttpMessageHandler? webhooks = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(configure);

        var composition = TestHostComposition.Build(configure, time, webhooks);
        try
        {
            await composition.StartAsync(cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            await composition.DisposeAsync().ConfigureAwait(false);
            throw;
        }

        return new AlvoTestHost(composition);
    }

    /// <summary>
    /// A caller holding <c>authenticated</c> plus <paramref name="roles"/>, as a new user each time.
    /// </summary>
    /// <param name="roles">The application roles the caller holds, as the descriptor's rules name them.</param>
    /// <returns>The caller context to pass to <see cref="Data"/>.</returns>
    public static AlvoContext Caller(params string[] roles)
    {
        ArgumentNullException.ThrowIfNull(roles);

        return new AlvoContext
        {
            User = new UserId(Guid.NewGuid()),
            Roles = RoleCatalog.Create(roles).Resolve([Role.Authenticated.Name, .. roles]),
        };
    }

    /// <summary>Stops the host and drops its database. Safe to call more than once.</summary>
    /// <returns>A task that completes when the host has stopped.</returns>
    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        await _composition.DisposeAsync().ConfigureAwait(false);
    }
}
