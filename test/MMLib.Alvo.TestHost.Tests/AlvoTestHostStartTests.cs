using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

using MMLib.Alvo.Data;
using MMLib.Alvo.Events;
using MMLib.Alvo.Migrations;

namespace MMLib.Alvo.TestHost.Tests;

/// <summary>Starting, isolating and stopping a host — the package's own promises, independent of the engine.</summary>
public sealed class AlvoTestHostStartTests
{
    private static string HelpDesk => Path.Combine(AppContext.BaseDirectory, "descriptors", "help-desk.alvo.json");

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Each_host_has_its_own_database()
    {
        await using var first = await AlvoTestHost.StartAsync(alvo => alvo.FromDescriptor(HelpDesk), cancellationToken: Ct);
        await using var second = await AlvoTestHost.StartAsync(alvo => alvo.FromDescriptor(HelpDesk), cancellationToken: Ct);
        var agent = AlvoTestHost.Caller("agent");

        await first.Data.CreateAsync("tickets", Title("Only in the first"), agent, cancellationToken: Ct);

        (await second.Data.QueryAsync(new AlvoQuery { Entity = "tickets" }, agent, Ct)).Items.ShouldBeEmpty();
    }

    [Fact]
    public async Task A_provider_registered_in_configure_replaces_the_in_memory_database()
    {
        var file = Path.Combine(Path.GetTempPath(), $"alvo-testhost-{Guid.NewGuid():N}.db");
        var agent = AlvoTestHost.Caller("agent");
        try
        {
            await using (var writer = await StartOnFileAsync(file))
            {
                await writer.Data.CreateAsync("tickets", Title("Kept on disk"), agent, cancellationToken: Ct);
            }

            await using var reader = await StartOnFileAsync(file);
            (await reader.Data.QueryAsync(new AlvoQuery { Entity = "tickets" }, agent, Ct)).Items.ShouldHaveSingleItem();
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            File.Delete(file);
        }
    }

    [Fact]
    public async Task A_host_without_a_descriptor_refuses_to_start_with_the_boot_s_fix()
    {
        var refusal = await Should.ThrowAsync<AlvoStartupRefusedException>(
            () => AlvoTestHost.StartAsync(_ => { }, cancellationToken: Ct));

        refusal.Message.ShouldContain("FromDescriptor");
    }

    [Fact]
    public async Task Disposing_twice_is_harmless()
    {
        var alvo = await AlvoTestHost.StartAsync(builder => builder.FromDescriptor(HelpDesk), cancellationToken: Ct);

        await alvo.DisposeAsync();
        await Should.NotThrowAsync(async () => await alvo.DisposeAsync());
    }

    /// <summary>
    /// A service whose stop throws still has its container disposed — asynchronously, or an async-only singleton would
    /// make the container's synchronous dispose throw instead.
    /// </summary>
    [Fact]
    public async Task A_failing_stop_still_disposes_the_host_asynchronously()
    {
        var service = new AsyncOnlyFailingStop();
        var alvo = await AlvoTestHost.StartAsync(
            builder =>
            {
                builder.FromDescriptor(HelpDesk);
                builder.Services.AddHostedService(_ => service);
            },
            cancellationToken: Ct);

        await Should.ThrowAsync<Exception>(async () => await alvo.DisposeAsync());

        service.IsDisposed.ShouldBeTrue("the stop failed, and the host was disposed all the same");
    }

    [Fact]
    public async Task A_failing_dispose_does_not_hide_the_failing_stop()
    {
        var service = new AsyncOnlyFailingStop { DisposeFails = true };
        var alvo = await AlvoTestHost.StartAsync(
            builder =>
            {
                builder.FromDescriptor(HelpDesk);
                builder.Services.AddHostedService(_ => service);
            },
            cancellationToken: Ct);

        var failure = await Should.ThrowAsync<AggregateException>(async () => await alvo.DisposeAsync());

        failure.InnerExceptions.Count.ShouldBe(2, "the stop's failure first, then the dispose's");
        failure.InnerExceptions[0].ToString().ShouldContain("The stop failed.");
        failure.InnerExceptions[1].ToString().ShouldContain("The dispose failed.");
    }

    [Fact]
    public async Task A_disposed_host_refuses_to_drain_or_hand_out_its_ports()
    {
        var alvo = await AlvoTestHost.StartAsync(builder => builder.FromDescriptor(HelpDesk), cancellationToken: Ct);
        await alvo.DisposeAsync();

        Should.Throw<ObjectDisposedException>(() => alvo.Data);
        Should.Throw<ObjectDisposedException>(() => alvo.Services);
        await Should.ThrowAsync<ObjectDisposedException>(() => alvo.DrainEventsAsync(Ct));
    }

    [Fact]
    public void A_caller_carries_authenticated_and_the_named_roles()
    {
        var first = AlvoTestHost.Caller("agent", "lead");
        var second = AlvoTestHost.Caller("agent");

        first.Roles.Select(role => role.Name).ShouldBe(["authenticated", "agent", "lead"], ignoreOrder: true);
        first.User.ShouldNotBe(second.User, "each caller is a different user");
    }

    [Fact]
    public async Task The_services_carry_the_host_s_other_ports()
    {
        await using var alvo = await AlvoTestHost.StartAsync(builder => builder.FromDescriptor(HelpDesk), cancellationToken: Ct);

        alvo.Services.GetRequiredService<IAlvoEvents>().ShouldNotBeNull();
    }

    /// <summary>
    /// The host is handed back only once its boot is Ready, so the manual pump's first round is never refused as
    /// "boot not ready" or "no schema primed" — a drain can start the moment <c>StartAsync</c> returns.
    /// </summary>
    [Fact]
    public async Task A_started_host_can_drain_at_once()
    {
        await using var alvo = await AlvoTestHost.StartAsync(builder => builder.FromDescriptor(HelpDesk), cancellationToken: Ct);

        var round = await alvo.Services.GetRequiredService<IAlvoEventPump>().DrainAsync(Ct);

        round.IsQuiet.ShouldBeTrue();
    }

    private static Task<AlvoTestHost> StartOnFileAsync(string file) => AlvoTestHost.StartAsync(
        alvo => alvo.UseSqlite($"Data Source={file}").FromDescriptor(HelpDesk), cancellationToken: Ct);

    private static Dictionary<string, object?> Title(string title) => new(StringComparer.Ordinal) { ["title"] = title };

    private sealed class AsyncOnlyFailingStop : IHostedService, IAsyncDisposable
    {
        internal bool IsDisposed { get; private set; }

        internal bool DisposeFails { get; init; }

        public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        public Task StopAsync(CancellationToken cancellationToken) =>
            throw new InvalidOperationException("The stop failed.");

        public ValueTask DisposeAsync()
        {
            IsDisposed = true;

            return DisposeFails
                ? ValueTask.FromException(new InvalidOperationException("The dispose failed."))
                : ValueTask.CompletedTask;
        }
    }
}
