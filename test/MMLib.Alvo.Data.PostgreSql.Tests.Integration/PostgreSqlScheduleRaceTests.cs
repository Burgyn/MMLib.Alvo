using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;

using MMLib.Alvo.Scheduling;
using MMLib.Alvo.TestHost;
using MMLib.Alvo.Tests.Events;

using Npgsql;

using System.Collections.Concurrent;

using Xunit;

namespace MMLib.Alvo.Data.PostgreSql.Tests.Integration;

/// <summary>Records the occurrence each run was for.</summary>
public sealed class OccurrenceRecordingJob(ConcurrentQueue<DateTimeOffset> runs) : IAlvoScheduledJob
{
    public Task RunAsync(AlvoJobContext context, CancellationToken cancellationToken)
    {
        runs.Enqueue(context.ScheduledFor);
        return Task.CompletedTask;
    }
}

/// <summary>
/// Three replicas on one PostgreSQL database race one cron occurrence: the claim's EvalPlanQual-safe <c>UPDATE</c> and the
/// advance's fence let exactly one of them run it. PostgreSQL only — SQLite serialises its writers, so the race cannot occur there.
/// </summary>
[Collection(DispatchedEventCollection.Name)]
public sealed class PostgreSqlScheduleRaceTests(PostgresFixture postgres) : IClassFixture<PostgresFixture>, IAsyncLifetime
{
    private readonly List<string> _databases = [];

    private static string Desk => Path.Combine(AppContext.BaseDirectory, "descriptors", "booking-desk.alvo.json");

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public ValueTask InitializeAsync() => ValueTask.CompletedTask;

    public async ValueTask DisposeAsync()
    {
        foreach (var name in _databases)
        {
            await ExecuteAsync($"DROP DATABASE IF EXISTS \"{name}\" WITH (FORCE)");
        }
    }

    [Fact]
    public async Task Three_dispatchers_fire_one_cron_occurrence_exactly_once()
    {
        Assert.SkipUnless(!OperatingSystem.IsWindows(), "PostgreSQL Testcontainers requires a Linux Docker daemon; unavailable on Windows-container runners.");
        var connectionString = await DatabaseAsync();
        var time = new FakeTimeProvider(new DateTimeOffset(2026, 11, 6, 1, 0, 0, TimeSpan.Zero));
        var runs = new ConcurrentQueue<DateTimeOffset>();
        var hosts = new List<AlvoTestHost>();
        for (var index = 0; index < 3; index++)
        {
            hosts.Add(await AlvoTestHost.StartAsync(alvo => alvo.UsePostgreSql(connectionString).FromDescriptor(Desk)
                .AddSchedule<OccurrenceRecordingJob>("nightly-roll", "0 2 * * *").Services.AddSingleton(runs), time, cancellationToken: Ct));
        }

        await hosts[0].DrainEventsAsync(Ct);
        time.Advance(TimeSpan.FromHours(1));
        await Task.WhenAll(hosts.Select(host => Task.Run(() => host.DrainEventsAsync(Ct), Ct)));
        await hosts[0].DrainEventsAsync(Ct);

        runs.ShouldHaveSingleItem().ShouldBe(new DateTimeOffset(2026, 11, 6, 2, 0, 0, TimeSpan.Zero));
        foreach (var host in hosts)
        {
            await host.DisposeAsync();
        }
    }

    private async Task<string> DatabaseAsync()
    {
        Assert.SkipWhen(string.IsNullOrEmpty(postgres.ConnectionString), "Docker is unavailable on this platform, so the PostgreSQL engine cannot be started.");

        var name = $"alvo_race_{Guid.NewGuid():N}";
        await ExecuteAsync($"CREATE DATABASE \"{name}\"");
        _databases.Add(name);

        return new NpgsqlConnectionStringBuilder(postgres.ConnectionString) { Database = name }.ToString();
    }

    private async Task ExecuteAsync(string sql)
    {
        await using var admin = new NpgsqlConnection(postgres.ConnectionString);
        await admin.OpenAsync(Ct);
        await using var command = admin.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync(Ct);
    }
}
