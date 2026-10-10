using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using MMLib.Alvo.Auth;
using NSubstitute;
using System.Data.Common;

namespace MMLib.Alvo.Identity.Tests;

/// <summary>
/// <b>Two components of one circuit resolve the operator at the same time, and both get an answer.</b>
/// </summary>
/// <remarks>
/// <para>
/// A Blazor circuit is one DI scope, and its components initialise concurrently: the overview, the
/// pending bar and the project switcher each resolve the caller while the others are still awaiting
/// theirs. A resolver that read through the scope's own <c>DbContext</c> started a second query on it
/// before the first had finished, and EF refused it — every dashboard screen over PostgreSQL showed
/// a generic error (#339).
/// </para>
/// <para>
/// <b>SQLite, made to behave like a network engine.</b> Microsoft.Data.Sqlite completes its "async"
/// reads synchronously, so on its own it never interleaves two queries and the defect cannot show —
/// which is exactly why the dashboard worked over SQLite. <see cref="HeldRead"/> holds the first
/// resolution's read open mid-query, the way a round trip to PostgreSQL does, and the second
/// resolution is made while it is held: deterministic, with no timing in it.
/// </para>
/// </remarks>
public sealed class AlvoIdentityConcurrentResolutionTests : IAsyncLifetime
{
    private readonly string _file = Path.Combine(Path.GetTempPath(), $"alvo-identity-{Guid.NewGuid():N}.db");
    private readonly HeldRead _held = new();
    private ServiceProvider _provider = null!;
    private IServiceScope _circuit = null!;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <inheritdoc/>
    public async ValueTask InitializeAsync()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddAlvoIdentity(store => store.UseSqlite($"Data Source={_file}").AddInterceptors(_held));

        var catalog = Substitute.For<IRoleCatalogProvider>();
        catalog.DeclaredRoles.Returns(RoleCatalog.Create(["editor"]));
        services.AddSingleton(catalog);

        _provider = services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true });
        _circuit = _provider.CreateScope();

        await _circuit.ServiceProvider.GetRequiredService<Internal.AlvoIdentityDbContext>()
            .Database.EnsureCreatedAsync(Ct);
    }

    /// <inheritdoc/>
    public async ValueTask DisposeAsync()
    {
        _held.Release();
        _circuit.Dispose();
        await _provider.DisposeAsync();
        using (var connection = new SqliteConnection($"Data Source={_file}"))
        {
            SqliteConnection.ClearPool(connection);
        }

        File.Delete(_file);
    }

    /// <summary>A second resolution started while the first is still reading resolves too.</summary>
    [Fact]
    public async Task A_resolution_started_while_another_is_reading_on_the_same_circuit_resolves()
    {
        var id = await CreateAsync();
        var overview = CookieResolver();
        var pendingBar = CookieResolver();

        _held.ArmNext();
        var first = overview.ResolveAsync(id.ToString(), null, Ct).AsTask();
        await _held.Holding.WaitAsync(Ct);

        var second = await pendingBar.ResolveAsync(id.ToString(), null, Ct);
        _held.Release();

        second.ShouldNotBeNull("a concurrent component must not be refused for sharing the circuit")
            .Context.User.ShouldBe(id);
        (await first).ShouldNotBeNull().Context.User.ShouldBe(id);
    }

    /// <summary>
    /// The circuit's own administration still works afterwards — the resolution did not leave the
    /// scope's store in a state its next user trips over.
    /// </summary>
    [Fact]
    public async Task Concurrent_resolutions_leave_the_circuits_own_store_usable()
    {
        var id = await CreateAsync();
        _held.ArmNext();
        var first = CookieResolver().ResolveAsync(id.ToString(), null, Ct).AsTask();
        await _held.Holding.WaitAsync(Ct);
        var second = await CookieResolver().ResolveAsync(id.ToString(), null, Ct);
        _held.Release();
        await first;

        var store = _circuit.ServiceProvider.GetRequiredService<IAlvoUserStore>();

        second.ShouldNotBeNull();
        (await store.FindAsync(id, Ct)).ShouldNotBeNull().RoleNames.ShouldBe(["editor"]);
    }

    /// <summary>The cookie resolver, resolved from the circuit's scope — the same instance every time.</summary>
    /// <returns>The resolver a dashboard component's caller resolution reaches.</returns>
    private IAlvoContextResolver CookieResolver()
        => _circuit.ServiceProvider.GetRequiredKeyedService<IAlvoContextResolver>(AlvoIdentity.ResolverKey);

    /// <summary>Creates an operator holding <c>editor</c>, from a scope of its own.</summary>
    /// <returns>The new operator's identifier.</returns>
    private async Task<UserId> CreateAsync()
    {
        using var other = _provider.CreateScope();
        var people = other.ServiceProvider.GetRequiredKeyedService<IAlvoUserAdministration>(
            AlvoUserAdministration.UnguardedKey);
        return (await people.CreateAsync(new AlvoUserCreation("eva@example.test", ["editor"], null), Ct)).Id;
    }

    /// <summary>
    /// Holds the next query open after EF has started it and before it reaches the database — the
    /// window a PostgreSQL round trip leaves open.
    /// </summary>
    /// <remarks>One-shot: only the armed read is held; every other read passes straight through.</remarks>
    private sealed class HeldRead : DbCommandInterceptor
    {
        private readonly TaskCompletionSource _released = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _armed;

        /// <summary>Signalled once the armed read is being held.</summary>
        public SemaphoreSlim Holding { get; } = new(0);

        /// <summary>Holds the next read that starts.</summary>
        public void ArmNext() => Volatile.Write(ref _armed, 1);

        /// <summary>Lets the held read continue.</summary>
        public void Release() => _released.TrySetResult();

        /// <inheritdoc/>
        public override async ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
            DbCommand command,
            CommandEventData eventData,
            InterceptionResult<DbDataReader> result,
            CancellationToken cancellationToken = default)
        {
            if (Interlocked.Exchange(ref _armed, 0) == 1)
            {
                Holding.Release();
                await _released.Task.WaitAsync(cancellationToken);
            }

            return result;
        }
    }
}
