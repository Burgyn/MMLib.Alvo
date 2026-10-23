using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;

using MMLib.Alvo.Data;
using MMLib.Alvo.Events;
using MMLib.Alvo.Extensions;
using MMLib.Alvo.Migrations;

namespace MMLib.Alvo.TestHost.Tests;

/// <summary>
/// A handler removed from the code leaves its unfinished deliveries in a real SQLite database; the next start refuses
/// until the host says what becomes of them (spec 2026-10-14 E3).
/// </summary>
public sealed class ExtensionStateBootTests
{
    private const string Removed = "ticket-sync";

    private static string HelpDesk => Path.Combine(AppContext.BaseDirectory, "descriptors", "help-desk.alvo.json");

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task A_removed_handler_with_unfinished_work_refuses_the_restart_until_it_is_retired()
    {
        var file = Path.Combine(Path.GetTempPath(), $"alvo-retire-{Guid.NewGuid():N}.db");
        try
        {
            await LeaveARetryingDeliveryAsync(file);

            var refusal = await Should.ThrowAsync<AlvoStartupRefusedException>(() => StartOnFileAsync(file, alvo => { }));
            refusal.Message.ShouldContain($"'{Removed}'");
            refusal.FixSuggestion.ShouldContain($"AddRetiredExtension(\"{Removed}\", AlvoRetirement.DeadLetter)");

            await using (await StartOnFileAsync(file, alvo => alvo.AddRetiredExtension(Removed, AlvoRetirement.DeadLetter)))
            {
                (await DeliveryOfAsync(file)).ShouldBe(("dead", "retired"));
            }
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            File.Delete(file);
        }
    }

    private static async Task LeaveARetryingDeliveryAsync(string file)
    {
        await using var alvo = await StartOnFileAsync(file, builder => builder.Entity("tickets", tickets => tickets.OnCreated(
            Removed, (AlvoCreated<AlvoRecord> _) => Task.FromException(new InvalidOperationException("The remote system is down.")))));
        await alvo.Data.CreateAsync(
            "tickets", new Dictionary<string, object?> { ["title"] = "Printer jam" }, AlvoTestHost.Caller("agent"), cancellationToken: Ct);

        (await alvo.DrainEventsAsync(Ct)).Failed.ShouldBe(1, "the handler's first attempt fails and waits out its backoff");
        (await DeliveryOfAsync(file)).Status.ShouldBe("retrying");
    }

    private static Task<AlvoTestHost> StartOnFileAsync(string file, Action<IAlvoBuilder> extensions) => AlvoTestHost.StartAsync(
        alvo =>
        {
            alvo.UseSqlite($"Data Source={file}").FromDescriptor(HelpDesk);
            extensions(alvo);
        },
        new FakeTimeProvider(new DateTimeOffset(2026, 10, 23, 9, 0, 0, TimeSpan.Zero)),
        cancellationToken: Ct);

    private static async Task<(string Status, string? LastErrorType)> DeliveryOfAsync(string file)
    {
        await using var connection = new SqliteConnection($"Data Source={file}");
        await connection.OpenAsync(Ct);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT status, last_error_type FROM alvo_deliveries";
        await using var reader = await command.ExecuteReaderAsync(Ct);
        (await reader.ReadAsync(Ct)).ShouldBeTrue("the delivery row is still there");
        var delivery = (reader.GetString(0), await reader.IsDBNullAsync(1, Ct) ? null : reader.GetString(1));
        (await reader.ReadAsync(Ct)).ShouldBeFalse("the host fanned out one delivery");

        return delivery;
    }
}
