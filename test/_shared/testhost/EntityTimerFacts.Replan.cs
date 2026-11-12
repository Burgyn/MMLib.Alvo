using Microsoft.Extensions.DependencyInjection;

using MMLib.Alvo.Auth;
using MMLib.Alvo.Data;
using MMLib.Alvo.Management;

using System.Data.Common;

namespace MMLib.Alvo.TestHost.Tests;

/// <summary>
/// The timer re-plan (issue 483): an apply that adds a timer, or changes its <c>at</c>, starts a framework data migration that
/// arms every existing row exactly once — keyset-paged past one page — and running it again changes nothing.
/// </summary>
public abstract partial class EntityTimerFacts
{
    private const string Project = "lifecycle-desk";
    private const string UntimedDesk = "lifecycle-desk-untimed.alvo.json";
    private const string EarlierDesk = "lifecycle-desk-earlier.alvo.json";
    private const int PastOnePage = 101;

    [Fact]
    public async Task Adding_a_timer_arms_every_existing_row_once_and_running_it_again_changes_nothing()
    {
        EnsureEngineAvailable();
        var database = await DatabaseAsync();
        var alvo = await StartUntimedAsync(database);
        var due = await SeedAsync(alvo);

        await ApplyAsync(alvo, StateMachineFacts.LifecycleDesk);
        await alvo.DrainEventsAsync(Ct);
        await ReplanSucceededAsync(alvo);
        await alvo.DisposeAsync();

        var armed = await ArmedAsync(database);
        armed.Select(timer => timer.Key).ShouldBe(due.Keys.Order(StringComparer.Ordinal));
        armed.ShouldAllBe(timer => timer.Scope == Tenant.ToString() && timer.Due == due[timer.Key]);

        await ForgetStartedMigrationsAsync(database);
        await using var restarted = await StartAsync(database, new ReminderLog());
        await restarted.DrainEventsAsync(Ct);
        (await ReplanDeliveriesAsync(restarted)).Count.ShouldBe(2);
        (await ArmedAsync(database)).ShouldBe(armed);
    }

    [Fact]
    public async Task Changing_a_timer_s_at_moves_every_armed_row()
    {
        EnsureEngineAvailable();
        var database = await DatabaseAsync();
        await using var alvo = await StartUntimedAsync(database);
        var due = await SeedAsync(alvo);
        await ApplyAsync(alvo, StateMachineFacts.LifecycleDesk);
        await alvo.DrainEventsAsync(Ct);

        await ApplyAsync(alvo, EarlierDesk);
        await alvo.DrainEventsAsync(Ct);

        var armed = await ArmedAsync(database);
        armed.Count.ShouldBe(due.Count);
        armed.ShouldAllBe(timer => timer.Due == due[timer.Key].AddHours(-12));
    }

    private async Task<AlvoTestHost> StartUntimedAsync(LifecycleDatabase database) =>
        await AlvoTestHost.StartAsync(
            alvo =>
            {
                database.Use(alvo);
                alvo.FromDescriptor(Path.Combine(AppContext.BaseDirectory, "descriptors", UntimedDesk));
            },
            Time,
            cancellationToken: Ct);

    /// <summary>
    /// More confirmed rows than one re-plan page holds, one draft (its <c>when</c> is false) and one whose reminder has already
    /// passed; returns the key and due of each row the re-plan must arm.
    /// </summary>
    private async Task<Dictionary<string, DateTimeOffset>> SeedAsync(AlvoTestHost alvo)
    {
        var confirmed = Enumerable.Range(0, PastOnePage).Select(index => Seeded("confirmed", _now.AddHours(48).AddMinutes(index))).ToList();
        IReadOnlyList<IReadOnlyDictionary<string, object?>> rows = [.. confirmed, Seeded("draft", _now.AddHours(48)), Seeded("confirmed", _now.AddHours(12))];
        var created = await alvo.Data.CreateManyAsync(StateMachineFacts.Bookings, rows, Staff(), cancellationToken: Ct);
        created.Refusals.ShouldBeEmpty();

        return created.Rows.Take(PastOnePage).Select((record, index) => (record, index)).ToDictionary(
            row => $"alvo:bookings:reminder:{(Guid)row.record["id"]!:D}",
            row => ((DateTimeOffset)confirmed[row.index]["starts_at"]!).AddHours(-24));
    }

    private Dictionary<string, object?> Seeded(string status, DateTimeOffset startsAt) => new(Booking(startsAt)) { ["status"] = status };

    private static async Task ReplanSucceededAsync(AlvoTestHost alvo) =>
        (await ReplanDeliveriesAsync(alvo)).ShouldHaveSingleItem().Status.ShouldBe("succeeded");

    private static async Task<List<ManagementDelivery>> ReplanDeliveriesAsync(AlvoTestHost alvo)
    {
        var page = await AsDeveloper<IAlvoOperations>(alvo).ListDeliveriesAsync(Project, new ManagementDeliveryQuery { Limit = 200 }, Ct);

        return [.. page.Items.Where(delivery => delivery.Subscriber.StartsWith("/entities/bookings/timers#", StringComparison.Ordinal))];
    }

    /// <summary>Removes every data migration's start, so the next process starts the re-plan again: the run again a fact needs.</summary>
    private static async Task ForgetStartedMigrationsAsync(LifecycleDatabase database)
    {
        await using var connection = database.Connect();
        await connection.OpenAsync(Ct);
        await using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM alvo_data_migrations";
        (await command.ExecuteNonQueryAsync(Ct)).ShouldBe(1);
    }

    private static async Task ApplyAsync(AlvoTestHost alvo, string descriptor)
    {
        var management = AsDeveloper<IAlvoManagement>(alvo);
        var current = await management.GetDescriptorAsync(Project, Ct);
        var json = await File.ReadAllTextAsync(Path.Combine(AppContext.BaseDirectory, "descriptors", descriptor), Ct);

        (await management.ApplyDescriptorAsync(Project, new ManagementApplyRequest(json, current.Revision), Ct)).Applied.ShouldBeTrue();
    }

    private static T AsDeveloper<T>(AlvoTestHost alvo)
        where T : notnull
    {
        alvo.Services.GetRequiredService<IAlvoContextAccessor>().Principal = new AlvoPrincipal
        {
            Context = AlvoTestHost.Caller("ops"),
            Scopes = new HashSet<ApiKeyScope>(),
            KeyId = "ops-key",
        };

        return alvo.Services.GetRequiredService<T>();
    }

    private static async Task<List<ArmedTimer>> ArmedAsync(LifecycleDatabase database)
    {
        await using var connection = database.Connect();
        await connection.OpenAsync(Ct);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT scope, timer_key, generation, due_at FROM alvo_timers ORDER BY timer_key";

        return await ReadArmedAsync(command);
    }

    private static async Task<List<ArmedTimer>> ReadArmedAsync(DbCommand command)
    {
        var armed = new List<ArmedTimer>();
        await using var reader = await command.ExecuteReaderAsync(Ct);
        while (await reader.ReadAsync(Ct))
        {
            armed.Add(new ArmedTimer(reader.GetString(0), reader.GetString(1), reader.GetString(2), DateTimeOffset.Parse(reader.GetString(3), System.Globalization.CultureInfo.InvariantCulture)));
        }

        return [.. armed.OrderBy(timer => timer.Key, StringComparer.Ordinal)];
    }

    private sealed record ArmedTimer(string Scope, string Key, string Generation, DateTimeOffset Due);
}
