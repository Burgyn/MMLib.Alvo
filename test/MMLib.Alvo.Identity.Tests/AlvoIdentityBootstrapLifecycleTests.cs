using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using System.Collections.Concurrent;

namespace MMLib.Alvo.Identity.Tests;

/// <summary>
/// The identity tables exist before any hosted service starts, however the host starts them: a web host's server is
/// one of those services, and a request it accepts first reads the users table (final-fix-A re-review, finding 16).
/// </summary>
/// <remarks>
/// <b>A service registered ahead of the identity package</b> stands in for the server. Under the default sequential
/// start it runs first, and under <c>ServicesStartConcurrently</c> it runs beside the bootstrap; either way only a
/// bootstrap that works in <c>StartingAsync</c>, which every service finishes before any <c>StartAsync</c> begins,
/// has the tables there when it reads.
/// </remarks>
public sealed class AlvoIdentityBootstrapLifecycleTests : IAsyncLifetime
{
    private readonly string _file = Path.Combine(Path.GetTempPath(), $"alvo-lifecycle-{Guid.NewGuid():N}.db");

    /// <inheritdoc/>
    public ValueTask InitializeAsync() => ValueTask.CompletedTask;

    /// <inheritdoc/>
    public ValueTask DisposeAsync()
    {
        /* This file's pool only: clearing every pool in the process races the other classes' open connections. */
        using (var connection = new SqliteConnection($"Data Source={_file}"))
        {
            SqliteConnection.ClearPool(connection);
        }

        File.Delete(_file);
        return ValueTask.CompletedTask;
    }

    /// <summary>A service that starts before the bootstrap in registration order still finds the tables.</summary>
    /// <param name="concurrently">Whether the host starts its services concurrently.</param>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task The_tables_exist_before_any_service_starts(bool concurrently)
    {
        var reader = new FirstReader();
        using var host = Build(concurrently, reader, new Captured());

        await host.StartAsync(TestContext.Current.CancellationToken);
        await host.StopAsync(TestContext.Current.CancellationToken);

        reader.Failure.ShouldBeNull("the users table was read before the bootstrap had created it");
    }

    /// <summary>A first boot's probe for the missing tables logs nothing at error level.</summary>
    /// <remarks>
    /// "no such table" at <c>fail:</c> is the line an operator greps for when something is broken, and on a first boot
    /// nothing is (docs/todo-admin.md §8d item 43).
    /// </remarks>
    [Fact]
    public async Task A_first_boot_logs_no_error()
    {
        var log = new Captured();
        using var host = Build(concurrently: false, new FirstReader(), log);

        await host.StartAsync(TestContext.Current.CancellationToken);
        await host.StopAsync(TestContext.Current.CancellationToken);

        log.Errors.ShouldBeEmpty();
    }

    /// <summary>
    /// A probe that fails for another reason than a missing table fails the start with what it said, not with the
    /// creation's "already exists" (batch-B re-review N5): here the users "table" is a view over a table dropped since.
    /// </summary>
    [Fact]
    public async Task A_probe_that_fails_for_another_reason_fails_the_start_with_its_own_error()
    {
        await using (var connection = new SqliteConnection($"Data Source={_file}"))
        {
            await connection.OpenAsync(TestContext.Current.CancellationToken);
            await using var command = connection.CreateCommand();
            command.CommandText = "CREATE TABLE elsewhere (id INTEGER); CREATE VIEW alvo_identity_users AS SELECT * FROM elsewhere; DROP TABLE elsewhere;";
            await command.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
        }

        using var host = Build(concurrently: false, new FirstReader(), new Captured());

        var refused = await Should.ThrowAsync<InvalidOperationException>(() => host.StartAsync(TestContext.Current.CancellationToken));
        refused.Message.ShouldStartWith("Alvo's identity tables could not be read, and could not be created.");
        refused.Message.ShouldContain("elsewhere", Case.Sensitive, "the read's own words say what is wrong");
    }

    private IHost Build(bool concurrently, FirstReader reader, Captured log)
    {
        var builder = Host.CreateApplicationBuilder();
        builder.Logging.ClearProviders();
        builder.Logging.AddProvider(log);
        builder.Services.Configure<HostOptions>(options => options.ServicesStartConcurrently = concurrently);

        /* Ahead of the identity package, the way a web host's server is ahead of nothing it depends on. */
        builder.Services.AddSingleton(reader);
        builder.Services.AddHostedService(provider => new ReaderService(provider, reader));
        builder.Services.AddAlvoIdentity(store => store.UseSqlite($"Data Source={_file}"), _ => { });
        return builder.Build();
    }

    /// <summary>What the first read of the users table threw, if it threw.</summary>
    private sealed class FirstReader
    {
        public Exception? Failure { get; set; }
    }

    /// <summary>Reads the users table in its own start, as the server's first cookie check does.</summary>
    private sealed class ReaderService(IServiceProvider provider, FirstReader reader) : IHostedService
    {
        public async Task StartAsync(CancellationToken cancellationToken)
        {
            using var scope = provider.CreateScope();
            try
            {
                await scope.ServiceProvider.GetRequiredService<IAlvoUserStore>().ListAsync(cancellationToken);
            }
            catch (Exception exception)
            {
                reader.Failure = exception;
            }
        }

        public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    }

    /// <summary>Every entry logged at error level or above.</summary>
    private sealed class Captured : ILoggerProvider
    {
        private readonly ConcurrentQueue<string> _errors = new();

        public IReadOnlyCollection<string> Errors => _errors;

        public ILogger CreateLogger(string categoryName) => new Logger(categoryName, _errors);

        public void Dispose()
        {
        }

        private sealed class Logger(string category, ConcurrentQueue<string> errors) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state)
                where TState : notnull => null;

            public bool IsEnabled(LogLevel logLevel) => true;

            public void Log<TState>(
                LogLevel logLevel, EventId eventId, TState state, Exception? exception,
                Func<TState, Exception?, string> formatter)
            {
                if (logLevel >= LogLevel.Error)
                {
                    errors.Enqueue($"{category}[{eventId.Id}]: {formatter(state, exception)}");
                }
            }
        }
    }
}
