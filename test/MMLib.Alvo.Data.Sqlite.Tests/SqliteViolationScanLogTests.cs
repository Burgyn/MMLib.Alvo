using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using MMLib.Alvo.Data.EntityFrameworkCore;
using MMLib.Alvo.Descriptor;
using MMLib.Alvo.Migrations;
using MMLib.Alvo.Testing.Data;
using MMLib.Alvo.Tests.Data;

namespace MMLib.Alvo.Data.Sqlite.Tests;

/// <summary>
/// A violation count the database cannot run is refused at its validation without the provider's exception (Ruling 67) —
/// so the operator gets that exception in the log instead, at warning (HX-11 Task 10 review m4).
/// </summary>
public sealed class SqliteViolationScanLogTests : IAsyncDisposable
{
    private readonly SqliteAlvoDataFixture _fixture = new();
    private readonly WarningLog _log = new();

    [Fact]
    public async Task A_count_the_database_cannot_run_is_logged_with_the_provider_s_exception()
    {
        var ct = TestContext.Current.CancellationToken;
        var host = await _fixture.StartAsync(
            CheckSchemas.Unvalidated,
            CheckSchemas.Descriptor,
            configure: services => services.AddLogging(logging => logging.AddProvider(_log)));
        (await OutOfBandStatement.ExecuteAsync(
            host.Services.GetRequiredService<AlvoDataContextFactory>(), "ALTER TABLE \"products\" DROP COLUMN \"temperature\"", ct))
            .ShouldBeNull();

        await Should.ThrowAsync<DescriptorValidationException>(() => host.Services.GetRequiredService<ISchemaMigrator>()
            .PlanAsync(CheckSchemas.Unvalidated, CheckSchemas.Validated, new MigrationOptions(), ct));

        var warning = _log.Warnings.ShouldHaveSingleItem();
        warning.Message.ShouldContain("'products'");
        warning.Message.ShouldContain("'temperature'");
        warning.Exception.ShouldBeAssignableTo<System.Data.Common.DbException>();
    }

    public ValueTask DisposeAsync() => _fixture.DisposeAsync();

    /// <summary>Every warning the migrator writes, with its exception.</summary>
    private sealed class WarningLog : ILoggerProvider, ILogger
    {
        private readonly List<(string Message, Exception? Exception)> _warnings = [];
        private readonly Lock _gate = new();

        internal IReadOnlyList<(string Message, Exception? Exception)> Warnings
        {
            get
            {
                lock (_gate)
                {
                    return [.. _warnings];
                }
            }
        }

        public ILogger CreateLogger(string categoryName) =>
            categoryName == typeof(EfCoreSchemaMigrator).FullName ? this : Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance;

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (logLevel == LogLevel.Warning)
            {
                lock (_gate)
                {
                    _warnings.Add((formatter(state, exception), exception));
                }
            }
        }

        public void Dispose()
        {
            // Nothing to release: the provider holds only the captured entries.
        }
    }
}
