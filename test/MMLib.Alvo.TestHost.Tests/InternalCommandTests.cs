using Microsoft.Extensions.DependencyInjection;
using MMLib.Alvo.Commands;
using MMLib.Alvo.Data;
using MMLib.Alvo.Events;
using MMLib.Alvo.Extensions;
using System.Collections.Concurrent;

namespace MMLib.Alvo.TestHost.Tests;

/// <summary>
/// Internal commands (spec 2026-10-14 §4.9, acceptance (8)): invoked by handlers, timers and jobs through
/// <see cref="IAlvoCommands"/>, under the same access rule, unit and completion event as a mapped command.
/// </summary>
public sealed class InternalCommandTests
{
    private static string HelpDesk => Path.Combine(AppContext.BaseDirectory, "descriptors", "help-desk.alvo.json");

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task An_internal_command_invoked_from_a_handler_runs_its_access_rule_and_emits_its_event()
    {
        var seen = new Seen();
        await using var alvo = await StartAsync(seen, runAs: AlvoRunAs.System);

        await alvo.Data.CreateAsync("tickets", new Dictionary<string, object?> { ["title"] = "Printer jam" }, AlvoTestHost.Caller("agent"), cancellationToken: Ct);
        await alvo.DrainEventsAsync(Ct);

        seen.Escalated.Count.ShouldBe(1, "the system the handler runs as holds admin, which the rule admits");
        seen.Completions.ShouldBe(["escalate_ticket"]);
    }

    [Fact]
    public async Task A_handler_running_as_a_caller_the_rule_refuses_never_runs_the_command()
    {
        var seen = new Seen();
        await using var alvo = await StartAsync(seen, runAs: AlvoRunAs.EventActor);

        await alvo.Data.CreateAsync("tickets", new Dictionary<string, object?> { ["title"] = "Printer jam" }, AlvoTestHost.Caller("agent"), cancellationToken: Ct);
        await alvo.DrainEventsAsync(Ct);

        seen.Escalated.ShouldBeEmpty("an agent holds no admin, so the access rule refuses before the command runs");
        seen.Completions.ShouldBeEmpty();
    }

    [Fact]
    public async Task A_caller_the_rule_refuses_is_an_authorization_refusal()
    {
        await using var alvo = await StartAsync(new Seen(), runAs: AlvoRunAs.System);
        var commands = alvo.Services.GetRequiredService<IAlvoCommands>();

        await Should.ThrowAsync<AlvoAuthorizationException>(() => commands.InvokeAsync("escalate_ticket", new Escalation(Guid.NewGuid()), AlvoTestHost.Caller("agent"), Ct));
    }

    [Fact]
    public async Task A_negated_identity_rule_refuses_the_anonymous_caller()
    {
        await using var alvo = await StartAsync(new Seen(), runAs: AlvoRunAs.System);
        var commands = alvo.Services.GetRequiredService<IAlvoCommands>();

        (await Should.ThrowAsync<AlvoAuthorizationException>(() => commands.InvokeAsync("unbanned_escalation", new Escalation(Guid.NewGuid()), AlvoContext.Anonymous, Ct)))
            .Message.ShouldContain("has no identity");
    }

    [Fact]
    public async Task An_unknown_command_and_a_wrong_input_are_refused_by_name()
    {
        await using var alvo = await StartAsync(new Seen(), runAs: AlvoRunAs.System);
        var commands = alvo.Services.GetRequiredService<IAlvoCommands>();

        (await Should.ThrowAsync<ArgumentException>(() => commands.InvokeAsync("escalate", new Escalation(Guid.NewGuid()), AlvoContext.System(null), Ct))).ParamName.ShouldBe("name");
        (await Should.ThrowAsync<ArgumentException>(() => commands.InvokeAsync("escalate_ticket", "not an escalation", AlvoContext.System(null), Ct))).ParamName.ShouldBe("input");
    }

    [Fact]
    public async Task Invoking_a_command_inside_an_open_unit_is_refused()
    {
        await using var alvo = await StartAsync(new Seen(), runAs: AlvoRunAs.System);
        var commands = alvo.Services.GetRequiredService<IAlvoCommands>();

        await Should.ThrowAsync<AlvoUnitInProgressException>(() => alvo.Services.GetRequiredService<IAlvoTransactions>().TransactAsync(
            AlvoContext.System(null), (_, ct) => commands.InvokeAsync("escalate_ticket", new Escalation(Guid.NewGuid()), AlvoContext.System(null), ct), cancellationToken: Ct));
    }

    [Fact]
    public void A_class_that_is_no_command_is_refused_at_registration() =>
        Should.Throw<ArgumentException>(() => new ServiceCollection().AddAlvo(alvo => alvo.AddCommand<Seen>("not_a_command", "true")))
            .Message.ShouldContain("IAlvoCommand<TInput>");

    private static Task<AlvoTestHost> StartAsync(Seen seen, AlvoRunAs runAs) =>
        AlvoTestHost.StartAsync(builder =>
        {
            builder.Services.AddSingleton(seen);
            builder.FromDescriptor(HelpDesk)
                .AddCommand<EscalateTicket>("escalate_ticket", "'admin' in @user.roles")
                .AddCommand<EscalateTicket>("unbanned_escalation", "!(string(@user.id) == '6c1f9a3e-0000-4000-8000-000000000001')")
                .Entity("tickets", tickets => tickets.OnCreated(
                    "escalate-new",
                    (AlvoCreated<AlvoRecord> created, IAlvoCommands commands, AlvoEventContext context, CancellationToken ct) =>
                        commands.InvokeAsync("escalate_ticket", new Escalation(Guid.Parse((string)created.New["id"]!)), context.Caller, ct),
                    configure: options => options.RunAs = runAs))
                .AddEventHandler("on-escalated", "alvo.command.escalate_ticket.completed", (AlvoRecord payload) =>
                {
                    seen.Completions.Enqueue((string)payload["command"]!);
                    return Task.CompletedTask;
                });
        }, cancellationToken: Ct);

    public sealed record Escalation(Guid Ticket);

    public sealed class EscalateTicket(Seen seen) : IAlvoCommand<Escalation>
    {
        public async Task HandleAsync(Escalation input, AlvoCommandContext context, CancellationToken cancellationToken)
        {
            await context.TransactAsync((_, _) => Task.CompletedTask, cancellationToken: cancellationToken);
            seen.Escalated.Enqueue(input.Ticket);
        }
    }

    public sealed class Seen
    {
        public ConcurrentQueue<Guid> Escalated { get; } = new();

        public ConcurrentQueue<string> Completions { get; } = new();
    }
}
