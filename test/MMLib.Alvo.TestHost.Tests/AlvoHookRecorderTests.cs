using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;

using MMLib.Alvo.Data;

namespace MMLib.Alvo.TestHost.Tests;

/// <summary>
/// The recorder reads the process-wide <c>MMLib.Alvo.Hooks</c> source but counts only its own host's runs, and only
/// the ones that succeeded (spec 2026-10-14 §4.14, §4.15).
/// </summary>
public sealed class AlvoHookRecorderTests
{
    private const string TicketsWebhook = "/entities/tickets/hooks/afterCreate/0";

    private static string HelpDesk => Path.Combine(AppContext.BaseDirectory, "descriptors", "help-desk.alvo.json");

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Hosts_running_side_by_side_never_see_each_others_runs()
    {
        await using var first = await StartAsync(new StubWebhookReceiver());
        await using var second = await StartAsync(new StubWebhookReceiver());
        await Task.WhenAll(CreateHighPriorityAsync(first), CreateHighPriorityAsync(second));

        await Task.WhenAll(first.DrainEventsAsync(Ct), second.DrainEventsAsync(Ct));

        first.Hooks.AssertRan(TicketsWebhook, times: 1);
        second.Hooks.AssertRan(TicketsWebhook, times: 1);
    }

    [Fact]
    public async Task A_failed_attempt_is_not_a_run()
    {
        await using var alvo = await StartAsync(webhooks: null, new FakeTimeProvider());
        await CreateHighPriorityAsync(alvo);

        await alvo.DrainEventsAsync(Ct);

        alvo.Hooks.AssertRan(TicketsWebhook, times: 0);
        var mismatch = Should.Throw<InvalidOperationException>(() => alvo.Hooks.AssertRan(TicketsWebhook));
        mismatch.Message.ShouldContain($"{TicketsWebhook} (failed)", Case.Sensitive, "the message lists every recorded run");
    }

    [Fact]
    public async Task A_hook_that_never_ran_names_the_runs_there_were()
    {
        await using var alvo = await StartAsync(new StubWebhookReceiver());

        var mismatch = Should.Throw<InvalidOperationException>(() => alvo.Hooks.AssertRan("/entities/tickets/hooks/afterCreate/9"));

        mismatch.Message.ShouldContain("ran 0");
        mismatch.Message.ShouldContain("Recorded runs: none");
    }

    [Fact]
    public async Task A_run_outside_a_drain_is_not_this_host_s()
    {
        await using var alvo = await StartAsync(new StubWebhookReceiver());
        await CreateHighPriorityAsync(alvo);

        await alvo.Services.GetRequiredService<MMLib.Alvo.Events.IAlvoEventPump>().DrainAsync(Ct);

        alvo.Hooks.AssertRan(TicketsWebhook, times: 0);
    }

    [Fact]
    public async Task An_id_is_required()
    {
        await using var alvo = await StartAsync(new StubWebhookReceiver());

        Should.Throw<ArgumentException>(() => alvo.Hooks.AssertRan(" "));
    }

    private static Task<AlvoTestHost> StartAsync(HttpMessageHandler? webhooks, TimeProvider? time = null) =>
        AlvoTestHost.StartAsync(alvo => alvo.FromDescriptor(HelpDesk), time, webhooks, Ct);

    private static Task<AlvoRecord> CreateHighPriorityAsync(AlvoTestHost alvo) => alvo.Data.CreateAsync(
        "tickets",
        new Dictionary<string, object?>(StringComparer.Ordinal) { ["title"] = "Server down", ["priority"] = "high" },
        AlvoTestHost.Caller("agent"),
        cancellationToken: Ct);
}
